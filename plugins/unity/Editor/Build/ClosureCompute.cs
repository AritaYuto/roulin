using System;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Build
{
    // Bundle-level downward closure via AssetDatabase.GetDependencies(recursive: true).
    //
    // Used by the incremental build: when the VCS-diff signal identifies a
    // set of changed bundles, the closure (= every bundle they transitively
    // reference) defines the minimal subset that must be fed to Scriptable
    // Build Pipeline so that cross-bundle references resolve correctly.
    //
    // Empirical justification lives in DependencyDiscoveryParityTests:
    // recursive:true matches ContentBuildInterface for in-bundle deps in
    // every sample, and any divergence is in the over-include direction
    // (safe — produces extra unchanged bundles, not missing ones).
    public static class ClosureCompute
    {
        // Returns the union of `seedBundleNames` and every bundle reachable
        // through AssetDatabase deps. Excludes built-in bundles (caller adds
        // them back when running Scriptable Build Pipeline if needed).
        public static HashSet<string> Downward(
            ISet<string> seedBundleNames,
            List<UnityEditor.AssetBundleBuild> allBundles,
            BundleLookup lookup)
        {
            if (seedBundleNames == null) throw new ArgumentNullException(nameof(seedBundleNames));
            if (allBundles == null) throw new ArgumentNullException(nameof(allBundles));
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));

            var byName = new Dictionary<string, UnityEditor.AssetBundleBuild>(allBundles.Count);
            foreach (var b in allBundles)
            {
                byName[b.assetBundleName] = b;
            }

            var visited = new HashSet<string>();
            foreach (var name in seedBundleNames)
            {
                visited.Add(name);
            }

            var queue = new Queue<string>(seedBundleNames);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!byName.TryGetValue(current, out var bundle))
                {
                    continue;
                }
                if (bundle.assetNames == null) continue;
                foreach (var assetPath in bundle.assetNames)
                {
                    var depPaths = AssetDatabase.GetDependencies(assetPath, recursive: true);
                    foreach (var depPath in depPaths)
                    {
                        var depBundle = lookup.GetBundleFor(depPath);
                        if (depBundle == null) continue;
                        if (visited.Add(depBundle))
                        {
                            queue.Enqueue(depBundle);
                        }
                    }
                }
            }
            return visited;
        }
    }
}
