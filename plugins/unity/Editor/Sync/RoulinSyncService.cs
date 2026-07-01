using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace Roulin.Editor.Sync
{
    // Per-group BuildPipeline.BuildAssetBundles (seconds, not minutes), then
    // POST /blobs + POST /patches for SSE-relayed hot reload. Bypasses SBP /
    // full Addressables since Sync only touches existing addresses.
    public static class RoulinSyncService
    {
        // Returns the number of patches accepted by /patches.
        public static async Task<int> SyncAsync(
            IEnumerable<string> assetPaths,
            string serverUrl,
            CancellationToken ct = default)
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null)
            {
                throw new InvalidOperationException("AddressableAssetSettings not configured.");
            }

            // One group → one AssetBundleBuild → one .bundle file.
            var byGroup = new Dictionary<AddressableAssetGroup, List<AddressableAssetEntry>>();
            foreach (var path in assetPaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }

                var entry = aas.FindAssetEntry(guid);
                if (entry == null)
                {
                    continue;
                }

                if (!byGroup.TryGetValue(entry.parentGroup, out var list))
                {
                    list = new List<AddressableAssetEntry>();
                    byGroup[entry.parentGroup] = list;
                }

                list.Add(entry);
            }

            if (byGroup.Count == 0)
            {
                return 0;
            }

            var outDir = Path.Combine("Library", "roulin", "sync");
            Directory.CreateDirectory(outDir);

            // Build the whole group as one bundle (legacy API requires it).
            // Device only Replace()s addresses in the patch, so unchanged
            // entries in the regenerated bundle are dead bytes.
            var builds = new List<AssetBundleBuild>(byGroup.Count);
            var groupBundleNames = new Dictionary<AddressableAssetGroup, string>();
            foreach (var (group, dirtyEntries) in byGroup)
            {
                var allEntries = group.entries.ToArray();
                var assetNames = new string[allEntries.Length];
                var addressableNames = new string[allEntries.Length];
                for (var i = 0; i < allEntries.Length; i++)
                {
                    assetNames[i] = AssetDatabase.GUIDToAssetPath(allEntries[i].guid);
                    addressableNames[i] = allEntries[i].address;
                }

                var bundleName = SanitizeBundleName(group.Name) + ".sync.bundle";
                groupBundleNames[group] = bundleName;
                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = bundleName,
                    assetNames = assetNames,
                    addressableNames = addressableNames
                });
            }

            var target = EditorUserBuildSettings.activeBuildTarget;
            var manifest = BuildPipeline.BuildAssetBundles(
                outDir,
                builds.ToArray(),
                BuildAssetBundleOptions.None,
                target);
            if (manifest == null)
            {
                throw new Exception("BuildPipeline.BuildAssetBundles returned null");
            }

            // Upload each freshly built bundle and collect (address, hash) pairs.
            using var client = new RoulinServerClient(serverUrl);
            var changes = new List<RoulinServerClient.PatchChange>();
            foreach (var (group, dirtyEntries) in byGroup)
            {
                var bundlePath = Path.Combine(outDir, groupBundleNames[group]);
                if (!File.Exists(bundlePath))
                {
                    throw new Exception($"expected bundle missing: {bundlePath}");
                }

                var bytes = await Task.Run(() => File.ReadAllBytes(bundlePath), ct);
                // Hot-reload upload: stays server-local, never written to CDN.
                var hashHex = await client.PostHotBlob(bytes, ct);

                foreach (var entry in dirtyEntries)
                {
                    changes.Add(new RoulinServerClient.PatchChange
                    {
                        address = entry.address,
                        new_blob_hex = hashHex
                    });
                }
            }

            var platform = ResolvePlayerPlatform(target);
            await client.PostPatches(platform, changes.ToArray(), ct);
            return changes.Count;
        }

        // Editor BuildTarget → runtime Application.platform string (device compares strings).
        private static string ResolvePlayerPlatform(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "WindowsPlayer";
                case BuildTarget.StandaloneOSX:
                    return "OSXPlayer";
                case BuildTarget.StandaloneLinux64:
                    return "LinuxPlayer";
                case BuildTarget.iOS:
                    return "IPhonePlayer";
                case BuildTarget.Android:
                    return "Android";
                case BuildTarget.WebGL:
                    return "WebGLPlayer";
                default:
                    return target.ToString();
            }
        }

        private static string SanitizeBundleName(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (ch == '_' || ch == '-' || ch == '.')
                {
                    sb.Append(ch);
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