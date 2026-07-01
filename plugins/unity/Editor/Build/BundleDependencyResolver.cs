using System;
using System.Collections.Generic;
using System.Diagnostics;
using Roulin.Editor.PackRule;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    // Closes the "changed bundles" set over cross-bundle refs so SBP doesn't
    // inline them. AssetDatabase.GetDependencies(recursive) over-includes
    // relative to SBP's ContentBuildInterface — safe direction.
    public static class BundleDependencyResolver
    {
        // Returns `bundleNames` plus every bundle they transitively depend
        // on. Dep paths owned by no bundle are silently skipped.
        public static HashSet<string> Resolve(
            ISet<string> bundleNames,
            IReadOnlyList<AssetBundleBuild> allBuilds,
            IRoulinPackRule packRule)
        {
            if (bundleNames == null) throw new ArgumentNullException(nameof(bundleNames));
            if (allBuilds == null) throw new ArgumentNullException(nameof(allBuilds));
            if (packRule == null) throw new ArgumentNullException(nameof(packRule));

            var byName = new Dictionary<string, AssetBundleBuild>(allBuilds.Count);
            foreach (var b in allBuilds)
            {
                byName[b.assetBundleName] = b;
            }

            var visited = new HashSet<string>(bundleNames);
            var queue = new Queue<string>(bundleNames);
            var depPathsBuffer = new List<string>();

            var swTotal = Stopwatch.StartNew();
            var swGetDeps = new Stopwatch();
            var swResolve = new Stopwatch();
            long iter = 0;
            long assetsSeen = 0;
            long depPathsSeen = 0;
            long resolveCalls = 0;
            long resolveInputTotal = 0;
            long enqueued = 0;
            long peakQueue = queue.Count;
            long tick = 5000;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                iter++;
                if (iter % tick == 0)
                {
                    Debug.Log(
                        $"[BundleDepResolver] progress: iter={iter} visited={visited.Count} " +
                        $"queueDepth={queue.Count} " +
                        $"getDeps={swGetDeps.ElapsedMilliseconds}ms " +
                        $"resolve={swResolve.ElapsedMilliseconds}ms");
                }

                if (!byName.TryGetValue(current, out var bundle)) continue;
                if (bundle.assetNames == null) continue;

                depPathsBuffer.Clear();
                foreach (var assetPath in bundle.assetNames)
                {
                    assetsSeen++;
                    swGetDeps.Start();
                    var depPaths = AssetDatabase.GetDependencies(assetPath, recursive: true);
                    swGetDeps.Stop();
                    foreach (var d in depPaths) depPathsBuffer.Add(d);
                }
                depPathsSeen += depPathsBuffer.Count;
                if (depPathsBuffer.Count == 0) continue;

                resolveCalls++;
                resolveInputTotal += depPathsBuffer.Count;
                swResolve.Start();
                var resolved = packRule.ResolveGroupsForPaths(depPathsBuffer);
                swResolve.Stop();
                foreach (var kv in resolved)
                {
                    var depBundle = ToBundleName(kv.Value, kv.Key);
                    if (depBundle == null) continue;
                    if (visited.Add(depBundle))
                    {
                        queue.Enqueue(depBundle);
                        enqueued++;
                        if (queue.Count > peakQueue) peakQueue = queue.Count;
                    }
                }
            }
            swTotal.Stop();

            Debug.Log(
                $"[BundleDepResolver] done: visited={visited.Count} iter={iter} " +
                $"peakQueue={peakQueue} enqueued={enqueued} " +
                $"assetsSeen={assetsSeen} depPathsSeen={depPathsSeen} " +
                $"resolveCalls={resolveCalls} resolveInputAvg={(resolveCalls == 0 ? 0 : resolveInputTotal / resolveCalls)} " +
                $"getDeps={swGetDeps.ElapsedMilliseconds}ms " +
                $"resolve={swResolve.ElapsedMilliseconds}ms " +
                $"other={swTotal.ElapsedMilliseconds - swGetDeps.ElapsedMilliseconds - swResolve.ElapsedMilliseconds}ms " +
                $"total={swTotal.ElapsedMilliseconds}ms");
            return visited;
        }

        // Must match AddressablesGroupsView.Walk's naming (sanitize + optional
        // "_scenes" suffix) so pack-rule results align with SBP's bundle set.
        private static string ToBundleName(string groupName, string assetPath)
        {
            if (string.IsNullOrEmpty(groupName)) return null;
            var baseName = AddressablesGroupsView.SanitizeBundleName(groupName);
            return assetPath != null && assetPath.EndsWith(".unity", StringComparison.Ordinal)
                ? baseName + "_scenes"
                : baseName;
        }
    }
}
