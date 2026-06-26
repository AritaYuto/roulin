using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    // Walks AddressableAssetSettings.groups → AssetBundleBuild list + BundleInput
    // skeleton + ownership lookups. Mirrors BuildScriptPackedMode.GenerateBuildInputDefinitions:
    // each group splits into asset + scene partitions ("_scenes" suffix) since
    // SBP rejects mixed Asset/Scene bundles.
    public sealed class WalkAddressableGroups
    {
        public List<AssetBundleBuild> BundleBuilds { get; } = new();
        public Dictionary<string, BundleInput> Inputs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> BundleToAssetGroup { get; } = new(StringComparer.Ordinal);
        public List<AddressableAssetEntry> AssetEntries { get; } = new();
        public int SkippedGroups { get; private set; }

        public static WalkAddressableGroups Run(AddressableAssetSettings aas)
        {
            var w = new WalkAddressableGroups();
            w.Walk(aas);
            return w;
        }

        private WalkAddressableGroups() { }

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

                var assetPartition = new EntryPartition();
                var scenePartition = new EntryPartition();
                foreach (var entry in g.entries)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    if (entry.IsFolder)
                    {
                        continue; // expand-by-folder is a v2 concern
                    }

                    if (string.IsNullOrEmpty(entry.AssetPath))
                    {
                        continue;
                    }

                    var part = entry.IsScene ? scenePartition : assetPartition;
                    var resType = AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath);

                    part.AssetPaths.Add(entry.AssetPath);
                    part.Addresses.Add(entry.address);
                    part.Labels.Add(new List<string>(entry.labels));
                    part.AssetIDs.Add(entry.guid ?? string.Empty);
                    part.AssetTypes.Add(resType != null ? resType.AssemblyQualifiedName : string.Empty);
                    AssetEntries.Add(entry);
                }

                if (assetPartition.Count == 0 && scenePartition.Count == 0)
                {
                    SkippedGroups++;
                    continue;
                }

                var baseName = SanitizeBundleName(g.Name);
                EmitBundle(baseName, assetPartition, g.Name, g.Guid);
                EmitBundle(baseName + "_scenes", scenePartition, g.Name, g.Guid);
            }

            if (BundleBuilds.Count == 0)
            {
                throw new InvalidOperationException(
                    "no Addressables groups produced any bundles — nothing to ship");
            }

            Debug.Log(
                $"[RoulinBuild] groups resolved: {Inputs.Count} bundle(s), " +
                $"{SkippedGroups} group(s) skipped");
        }

        private void EmitBundle(
            string bundleName,
            EntryPartition part,
            string sourceGroupName,
            string sourceGroupGuid)
        {
            if (part.Count == 0)
            {
                return;
            }

            if (Inputs.ContainsKey(bundleName))
            {
                throw new InvalidOperationException(
                    $"two groups sanitize to the same bundle name '{bundleName}' — rename one of them");
            }

            BundleBuilds.Add(new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = part.AssetPaths.ToArray(),
                addressableNames = part.Addresses.ToArray()
            });

            var bi = new BundleInput { Name = bundleName };
            for (var i = 0; i < part.Addresses.Count; i++)
            {
                bi.Entries.Add(new EntryInput(
                    part.Addresses[i],
                    part.Labels[i],
                    part.AssetIDs[i],
                    part.AssetTypes[i]));
            }

            Inputs[bundleName] = bi;
            BundleToAssetGroup[bundleName] = sourceGroupGuid;

            Debug.Log(
                $"[RoulinBuild]   group '{sourceGroupName}' → bundle '{bundleName}' " +
                $"({part.Count} entries)");
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

        private sealed class EntryPartition
        {
            public readonly List<string> Addresses = new();
            public readonly List<string> AssetIDs = new();
            public readonly List<string> AssetPaths = new();
            public readonly List<string> AssetTypes = new();
            public readonly List<List<string>> Labels = new();
            public int Count => AssetPaths.Count;
        }
    }
}
