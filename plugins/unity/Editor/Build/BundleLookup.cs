using System;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Build
{
    // Reverse index from asset path → owning bundle name.
    //
    // Built from AssetBundleBuild[] (the structure WalkAddressableGroups
    // produces and SBP consumes). The forward direction (group → bundle →
    // asset list) is what Addressables natively encodes; this class flips
    // it so an incremental builder can ask "which bundle does Assets/X.png
    // belong to?" in O(1).
    //
    // Assumption: one asset path appears in at most one bundle. Addressables
    // enforces single-group membership per entry, so this holds for normal
    // builds. Duplicate paths across bundles throw at index-build time.
    public sealed class BundleLookup
    {
        private readonly Dictionary<string, string> mAssetPathToBundle;

        private BundleLookup(Dictionary<string, string> map)
        {
            mAssetPathToBundle = map;
        }

        public static BundleLookup From(IEnumerable<AssetBundleBuild> builds)
        {
            if (builds == null)
            {
                throw new ArgumentNullException(nameof(builds));
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in builds)
            {
                if (b.assetNames == null)
                {
                    continue;
                }

                foreach (var path in b.assetNames)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    if (map.TryGetValue(path, out var existing))
                    {
                        if (existing != b.assetBundleName)
                        {
                            throw new InvalidOperationException(
                                $"asset '{path}' appears in two bundles: " +
                                $"'{existing}' and '{b.assetBundleName}'");
                        }

                        continue;
                    }

                    map[path] = b.assetBundleName;
                }
            }

            return new BundleLookup(map);
        }

        // Returns the bundle name owning assetPath, or null if assetPath
        // is not part of any bundle. Null is the expected signal for
        // "this file does not require a rebuild because no bundle owns it".
        public string GetBundleFor(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            return mAssetPathToBundle.TryGetValue(assetPath, out var bundle)
                ? bundle
                : null;
        }

        public int Count => mAssetPathToBundle.Count;

        // Resolves a list of changed file paths to the set of bundles
        // that need to be rebuilt. Paths with no owning bundle are
        // silently skipped.
        public ISet<string> ResolveAffectedBundles(IEnumerable<string> changedPaths)
        {
            var bundles = new HashSet<string>(StringComparer.Ordinal);
            if (changedPaths == null)
            {
                return bundles;
            }

            foreach (var path in changedPaths)
            {
                var bundle = GetBundleFor(path);
                if (bundle != null)
                {
                    bundles.Add(bundle);
                }
            }

            return bundles;
        }
    }
}
