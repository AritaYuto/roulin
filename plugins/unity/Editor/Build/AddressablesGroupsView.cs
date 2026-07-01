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
    // Immutable snapshot of AddressableAssetSettings as SBP-shaped bundle
    // input plus per-bundle addressable entries for the catalog builder.
    // Injected into the SBP task chain as an IContextObject; RoulinPublishParcel
    // pulls from this when constructing the catalog.
    //
    // Scope kept intentionally narrow: this class only mirrors AAS into the
    // shape SBP + the catalog want. "Which group would rule X land in for a
    // transitive path" is handled by IRoulinPackRule. This view reads AAS,
    // nothing else.
    public sealed class AddressablesGroupsView : IContextObject
    {
        private readonly List<AssetBundleBuild> mBundleBuilds = new();
        private readonly Dictionary<string, List<AddressableEntry>> mEntriesByBundle =
            new(StringComparer.Ordinal);

        public IReadOnlyList<AssetBundleBuild> BundleBuilds => mBundleBuilds;

        private AddressablesGroupsView() { }

        public static AddressablesGroupsView From(AddressableAssetSettings aas)
        {
            var v = new AddressablesGroupsView();
            v.Walk(aas);
            return v;
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

        private void Walk(AddressableAssetSettings aas)
        {
            var skippedGroups = 0;
            foreach (var g in aas.groups)
            {
                if (g == null || g.ReadOnly)
                {
                    skippedGroups++;
                    continue;
                }

                if (!g.HasSchema<BundledAssetGroupSchema>())
                {
                    skippedGroups++;
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

                // Flatten group entries so folder entries (e.g. an asset
                // folder dragged whole into a group) expand to their leaf
                // assets. Without this, VCS changes to files inside a folder
                // entry never resolve back to a bundle and the incremental
                // filter misses them.
                var leaves = new List<AddressableAssetEntry>();
                foreach (var entry in g.entries)
                {
                    if (entry == null) continue;
                    if (entry.IsFolder)
                    {
                        entry.GatherAllAssets(leaves, false, true, false);
                    }
                    else
                    {
                        leaves.Add(entry);
                    }
                }

                foreach (var entry in leaves)
                {
                    if (entry == null) continue;
                    if (entry.IsFolder) continue;
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
                    skippedGroups++;
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
                $"{skippedGroups} group(s) skipped");
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
            mEntriesByBundle[bundleName] = entries;

            Debug.Log(
                $"[RoulinBuild]   group '{sourceGroupName}' → bundle '{bundleName}' " +
                $"({entries.Count} entries)");
        }

        // SBP bundle names round-trip through file paths; anything outside
        // [a-z0-9_-] (including '/') becomes '_'.
        // Shared with IRoulinPackRule consumers that need to convert a group
        // name back to the same bundle name Walk produced.
        internal static string SanitizeBundleName(string name)
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
