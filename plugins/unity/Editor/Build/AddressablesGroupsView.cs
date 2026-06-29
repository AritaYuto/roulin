using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline.Interfaces;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    // SBP-injectable contract for the immutable Addressables-side view.
    // RoulinPublishParcel pulls from this when constructing the catalog.
    public interface IAddressablesGroupsView : IContextObject
    {
        IReadOnlyList<AssetBundleBuild> BundleBuilds { get; }
        string GetBundle(string assetPath);
        ISet<string> ResolveAffectedBundles(IEnumerable<string> changedPaths);
        IReadOnlyList<AddressableEntry> GetEntries(string bundleName);
    }

    // Immutable snapshot of AddressableAssetSettings as seen at build time.
    //
    //   - BundleBuilds[]:        AssetBundleBuild list for SBP input
    //   - GetEntries(name):      per-bundle addressable entries
    //                            (consumed by catalog builder to fill
    //                            Parcel.Bundle.entries)
    //   - GetBundle(path) /
    //     ResolveAffectedBundles:reverse index from asset path → owning
    //                            bundle, used by VCS-diff and the bundle
    //                            dependency resolver
    //                            for incremental build identification
    //
    // No mutable build state lives here — RoulinPublishBlobs / RoulinPublish-
    // Parcel publish their own results via separate context objects.
    //
    // Invariant: every asset path appears in at most one bundle (Addressables
    // enforces single-group membership). Violation throws at snapshot-build
    // time.
    public sealed class AddressablesGroupsView : IAddressablesGroupsView
    {
        private readonly List<AssetBundleBuild> mBundleBuilds = new();
        private readonly Dictionary<string, string> mAssetPathToBundle =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AddressableEntry>> mEntriesByBundle =
            new(StringComparer.Ordinal);

        public IReadOnlyList<AssetBundleBuild> BundleBuilds => mBundleBuilds;
        public int SkippedGroups { get; private set; }

        private AddressablesGroupsView() { }

        // Full-fidelity factory: walks AddressableAssetSettings → BundleBuilds,
        // per-bundle entries, and the reverse lookup.
        public static AddressablesGroupsView From(AddressableAssetSettings aas)
        {
            var v = new AddressablesGroupsView();
            v.Walk(aas);
            return v;
        }

        // Test factory: builds the reverse lookup + BundleBuilds list from a
        // hand-rolled AssetBundleBuild[] without any AddressableAssetSettings.
        // Per-bundle entries are left empty. Use only from tests that exercise
        // the lookup methods in isolation.
        public static AddressablesGroupsView FromBundleBuilds(IEnumerable<AssetBundleBuild> builds)
        {
            if (builds == null) throw new ArgumentNullException(nameof(builds));
            var v = new AddressablesGroupsView();
            foreach (var b in builds)
            {
                v.mBundleBuilds.Add(b);
                v.RegisterAssetPaths(b);
            }
            return v;
        }

        // Returns the bundle name owning assetPath, or null when no bundle owns
        // it. Null is the expected signal for "this file does not trigger a
        // rebuild because no bundle owns it".
        public string GetBundle(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            return mAssetPathToBundle.TryGetValue(assetPath, out var bundle)
                ? bundle
                : null;
        }

        // Resolves a list of changed file paths to the set of bundles that
        // need to be rebuilt. Paths with no owning bundle are silently
        // skipped.
        public ISet<string> ResolveAffectedBundles(IEnumerable<string> changedPaths)
        {
            var bundles = new HashSet<string>(StringComparer.Ordinal);
            if (changedPaths == null) return bundles;
            foreach (var path in changedPaths)
            {
                var bundle = GetBundle(path);
                if (bundle != null) bundles.Add(bundle);
            }
            return bundles;
        }

        // Returns the addressable entries for this bundle, or an empty list
        // when the bundle is SBP-synthesised (UnityBuiltInShaders /
        // UnityMonoScripts) and has no Addressables-side records.
        public IReadOnlyList<AddressableEntry> GetEntries(string bundleName)
        {
            return mEntriesByBundle.TryGetValue(bundleName, out var list)
                ? (IReadOnlyList<AddressableEntry>)list
                : Array.Empty<AddressableEntry>();
        }

        // Number of (asset path → bundle) entries in the reverse lookup.
        public int LookupCount => mAssetPathToBundle.Count;

        private void Walk(AddressableAssetSettings aas)
        {
            foreach (var g in aas.groups)
            {
                if (g == null || g.ReadOnly)
                {
                    SkippedGroups++;
                    continue;
                }

                if (!g.HasSchema<BundledAssetGroupSchema>())
                {
                    SkippedGroups++;
                    continue;
                }

                var baseName = SanitizeBundleName(g.Name);
                var assetBundleName = baseName;
                var sceneBundleName = baseName + "_scenes";

                // Lazy per-bundle accumulators: only allocate when the first
                // matching entry arrives. Groups containing only asset entries
                // (or only scene entries) pay for one set of lists, not two.
                List<string> assetPaths = null, assetAddrs = null;
                List<AddressableEntry> assetEntries = null;
                List<string> scenePaths = null, sceneAddrs = null;
                List<AddressableEntry> sceneEntries = null;

                foreach (var entry in g.entries)
                {
                    if (entry == null) continue;
                    if (entry.IsFolder) continue; // expand-by-folder is a v2 concern
                    if (string.IsNullOrEmpty(entry.AssetPath)) continue;

                    var resType = AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath);
                    var assetType = resType != null ? resType.AssemblyQualifiedName : string.Empty;
                    var record = new AddressableEntry(
                        entry.address,
                        new List<string>(entry.labels),
                        entry.guid ?? string.Empty,
                        assetType);

                    if (entry.IsScene)
                    {
                        if (sceneEntries == null)
                        {
                            scenePaths = new List<string>();
                            sceneAddrs = new List<string>();
                            sceneEntries = new List<AddressableEntry>();
                        }
                        scenePaths.Add(entry.AssetPath);
                        sceneAddrs.Add(entry.address);
                        sceneEntries.Add(record);
                    }
                    else
                    {
                        if (assetEntries == null)
                        {
                            assetPaths = new List<string>();
                            assetAddrs = new List<string>();
                            assetEntries = new List<AddressableEntry>();
                        }
                        assetPaths.Add(entry.AssetPath);
                        assetAddrs.Add(entry.address);
                        assetEntries.Add(record);
                    }
                }

                if (assetEntries == null && sceneEntries == null)
                {
                    SkippedGroups++;
                    continue;
                }

                if (assetEntries != null)
                {
                    EmitBundle(assetBundleName, assetPaths, assetAddrs, assetEntries, g.Name);
                }
                if (sceneEntries != null)
                {
                    EmitBundle(sceneBundleName, scenePaths, sceneAddrs, sceneEntries, g.Name);
                }
            }

            if (mBundleBuilds.Count == 0)
            {
                throw new InvalidOperationException(
                    "no Addressables groups produced any bundles — nothing to ship");
            }

            Debug.Log(
                $"[RoulinBuild] groups resolved: {mBundleBuilds.Count} bundle(s), " +
                $"{SkippedGroups} group(s) skipped");
        }

        private void EmitBundle(
            string bundleName,
            List<string> assetPaths,
            List<string> addressableNames,
            List<AddressableEntry> entries,
            string sourceGroupName)
        {
            if (mEntriesByBundle.ContainsKey(bundleName))
            {
                throw new InvalidOperationException(
                    $"two groups sanitize to the same bundle name '{bundleName}' — rename one of them");
            }

            var build = new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = assetPaths.ToArray(),
                addressableNames = addressableNames.ToArray()
            };
            mBundleBuilds.Add(build);
            RegisterAssetPaths(build);
            mEntriesByBundle[bundleName] = entries;

            Debug.Log(
                $"[RoulinBuild]   group '{sourceGroupName}' → bundle '{bundleName}' " +
                $"({entries.Count} entries)");
        }

        // Folds an AssetBundleBuild's assetNames[] into the reverse map.
        // Same-bundle re-registration of the same path is a no-op; the same
        // path appearing under two bundles throws.
        private void RegisterAssetPaths(AssetBundleBuild b)
        {
            if (b.assetNames == null) return;
            foreach (var path in b.assetNames)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (mAssetPathToBundle.TryGetValue(path, out var existing))
                {
                    if (existing != b.assetBundleName)
                    {
                        throw new InvalidOperationException(
                            $"asset '{path}' appears in two bundles: " +
                            $"'{existing}' and '{b.assetBundleName}'");
                    }
                    continue;
                }
                mAssetPathToBundle[path] = b.assetBundleName;
            }
        }

        // SBP bundle names round-trip through file paths; anything outside
        // [a-z0-9_-] (including '/') becomes '_'.
        private static string SanitizeBundleName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            return sb.ToString();
        }
    }
}
