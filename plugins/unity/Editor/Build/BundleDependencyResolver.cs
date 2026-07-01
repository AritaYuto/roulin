using System;
using System.Collections.Generic;
using Roulin.Editor.PackRule;
using UnityEditor;

namespace Roulin.Editor.Build
{
    // Resolves the bundle-level dependencies of a set of input bundles:
    // walks AssetDatabase.GetDependencies(recursive: true) for each asset in
    // the input bundles, maps each dep asset path back to its owning bundle
    // via the IRoulinPackRule, and propagates outward until no new bundle is
    // found.
    //
    // Used by the incremental build: when the VCS-diff signal identifies a
    // set of changed bundles, the resolved dependency set defines the minimal
    // subset that must be fed to Scriptable Build Pipeline so that cross-bundle
    // references resolve correctly.
    //
    // Empirical justification: AssetDatabase recursive matches what
    // Scriptable Build Pipeline's ContentBuildInterface discovers; any
    // divergence is in the over-include direction (= produces extra
    // unchanged bundles, never missing ones — safe).
    public static class BundleDependencyResolver
    {
        // Returns `bundleNames` itself plus every bundle they transitively
        // depend on. Dep paths that belong to no bundle (built-in shaders,
        // scripts, unowned assets) are silently skipped.
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

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!byName.TryGetValue(current, out var bundle)) continue;
                if (bundle.assetNames == null) continue;

                depPathsBuffer.Clear();
                foreach (var assetPath in bundle.assetNames)
                {
                    var depPaths = AssetDatabase.GetDependencies(assetPath, recursive: true);
                    foreach (var d in depPaths) depPathsBuffer.Add(d);
                }
                if (depPathsBuffer.Count == 0) continue;

                var resolved = packRule.ResolveGroupsForPaths(depPathsBuffer);
                foreach (var kv in resolved)
                {
                    var depBundle = ToBundleName(kv.Value, kv.Key);
                    if (depBundle == null) continue;
                    if (visited.Add(depBundle))
                    {
                        queue.Enqueue(depBundle);
                    }
                }
            }
            return visited;
        }

        // Combines the group name from a pack rule with the same sanitize +
        // scene-suffix convention AddressablesGroupsView.Walk uses when
        // emitting bundles, so the returned name matches what SBP sees.
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
