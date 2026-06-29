using System;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Build
{
    // Resolves the bundle-level dependencies of a set of input bundles:
    // walks AssetDatabase.GetDependencies(recursive: true) for each asset in
    // the input bundles, maps each dep asset path back to its owning bundle
    // via the view, and propagates outward until no new bundle is found.
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
        // scripts, ...) are silently skipped.
        public static HashSet<string> Resolve(
            ISet<string> bundleNames,
            IAddressablesGroupsView view)
        {
            if (bundleNames == null) throw new ArgumentNullException(nameof(bundleNames));
            if (view == null) throw new ArgumentNullException(nameof(view));

            var byName = new Dictionary<string, AssetBundleBuild>(view.BundleBuilds.Count);
            foreach (var b in view.BundleBuilds)
            {
                byName[b.assetBundleName] = b;
            }

            var visited = new HashSet<string>(bundleNames);
            var queue = new Queue<string>(bundleNames);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!byName.TryGetValue(current, out var bundle)) continue;
                if (bundle.assetNames == null) continue;
                foreach (var assetPath in bundle.assetNames)
                {
                    var depPaths = AssetDatabase.GetDependencies(assetPath, recursive: true);
                    foreach (var depPath in depPaths)
                    {
                        var depBundle = view.GetBundle(depPath);
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
