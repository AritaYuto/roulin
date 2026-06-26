using System;
using System.IO;
using System.Linq;
using Roulin.Editor;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Roulin.Editor.Build
{
    internal static class BundleLookupDebugMenu
    {
        [MenuItem("Assets/Roulin/Resolve Bundle for Selection", false, 2000)]
        private static void ResolveSelection()
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null)
            {
                Debug.LogError("[BundleLookup] AddressableAssetSettings is not initialized");
                return;
            }

            var paths = Selection.objects
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToArray();
            if (paths.Length == 0)
            {
                Debug.Log("[BundleLookup] no asset selected");
                return;
            }

            var walk = WalkAddressableGroups.Run(aas);
            var lookup = BundleLookup.From(walk.BundleBuilds);
            Debug.Log($"[BundleLookup] index: {lookup.Count} assets across {walk.BundleBuilds.Count} bundles");

            foreach (var path in paths)
            {
                var bundle = lookup.GetBundleFor(path);
                Debug.Log($"[BundleLookup] {path} → {bundle ?? "(not in any bundle)"}");
            }

            var affected = lookup.ResolveAffectedBundles(paths);
            Debug.Log($"[BundleLookup] affected bundle set: {affected.Count}");
            foreach (var b in affected)
            {
                Debug.Log($"  - {b}");
            }
        }

        [MenuItem("Assets/Roulin/Resolve Bundle for Selection", true)]
        private static bool ResolveSelectionValidate()
        {
            return Selection.objects != null && Selection.objects.Length > 0;
        }

        [MenuItem("Roulin/Debug/Show Affected Bundles (uncommitted)")]
        private static async void ShowAffectedBundlesFromUncommittedChanges()
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null)
            {
                Debug.LogError("[VCS-Diff] AddressableAssetSettings is not initialized");
                return;
            }

            var settings = RoulinEditorSettings.instance;
            if (string.IsNullOrEmpty(settings.ServerUrl))
            {
                Debug.LogError("[VCS-Diff] RoulinEditorSettings.ServerUrl is not set");
                return;
            }

            RoulinServerClient.DiffResponse diff;
            try
            {
                using var client = new RoulinServerClient(settings.ServerUrl);
                diff = await client.GetDiffAsync(sinceSha: null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VCS-Diff] /diff request failed: {ex.Message}");
                return;
            }

            if (diff == null)
            {
                Debug.LogError("[VCS-Diff] /diff returned a null response");
                return;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var gitRoot = FindGitRoot(projectRoot);

            var unityPaths = VcsDiffPathNormalizer.Normalize(
                gitRoot, projectRoot, diff.uncommitted);

            var walk = WalkAddressableGroups.Run(aas);
            var lookup = BundleLookup.From(walk.BundleBuilds);
            var affected = lookup.ResolveAffectedBundles(unityPaths);

            Debug.Log(
                $"[VCS-Diff] revision={diff.revision}, " +
                $"uncommitted paths in Unity scope: {unityPaths.Count}, " +
                $"affected bundles: {affected.Count}");

            foreach (var p in unityPaths)
            {
                Debug.Log($"  {p} → {lookup.GetBundleFor(p) ?? "(not in any bundle)"}");
            }

            foreach (var b in affected.OrderBy(x => x, StringComparer.Ordinal))
            {
                Debug.Log($"  affected: {b}");
            }
        }

        private static string FindGitRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var marker = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return startDir;
        }

        [MenuItem("Roulin/Debug/Show BundleLookup Stats")]
        private static void ShowStats()
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null)
            {
                Debug.LogError("[BundleLookup] AddressableAssetSettings is not initialized");
                return;
            }

            var walk = WalkAddressableGroups.Run(aas);
            var lookup = BundleLookup.From(walk.BundleBuilds);
            Debug.Log(
                $"[BundleLookup] {walk.BundleBuilds.Count} bundles, " +
                $"{lookup.Count} indexed assets, {walk.SkippedGroups} skipped groups");

            var sample = walk.BundleBuilds.Take(5).Select(b =>
                $"  - {b.assetBundleName} ({(b.assetNames?.Length ?? 0)} assets)");
            foreach (var line in sample)
            {
                Debug.Log(line);
            }
            if (walk.BundleBuilds.Count > 5)
            {
                Debug.Log($"  ... and {walk.BundleBuilds.Count - 5} more");
            }
        }
    }
}
