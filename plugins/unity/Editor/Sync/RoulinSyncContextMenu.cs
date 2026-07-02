using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Roulin.Editor.PackRule;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Roulin.Editor.Sync
{
    // Project-window context menu forcing a sync regardless of dirty-set state.
    public static class RoulinSyncContextMenu
    {
        private const string MenuPath = "Assets/Roulin/Sync this Asset";
        private const int MenuPriority = 1100;

        [MenuItem(MenuPath, priority = MenuPriority)]
        private static void Sync()
        {
            var paths = ResolveSelectedAddressablePaths();
            if (paths.Count == 0)
            {
                Debug.LogWarning("[RoulinSync] right-click: nothing addressable selected");
                return;
            }

            _ = RunAsync(paths, RoulinEditorSettings.instance.ServerUrl);
        }

        // Greys out the menu unless the selection includes at least one
        // pack-rule-claimed asset.
        [MenuItem(MenuPath, validate = true, priority = MenuPriority)]
        private static bool SyncValidate()
        {
            return ResolveSelectedAddressablePaths().Count > 0;
        }

        private static List<string> ResolveSelectedAddressablePaths()
        {
            var result = new List<string>();
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null) return result;
            var packRule = RoulinPackRuleRegistry.Resolve(aas);
            if (packRule == null) return result;

            var candidates = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) candidates.Add(path);
            }
            if (candidates.Count == 0) return result;

            try
            {
                var claimed = packRule.ResolveGroupsForPaths(candidates);
                foreach (var path in claimed.Keys) result.Add(path);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogWarning($"[RoulinSync] pack rule overlap on selection: {ex.Message}");
            }
            return result;
        }

        private static async Task RunAsync(List<string> paths, string url)
        {
            try
            {
                var relayed = await RoulinSyncService.SyncAsync(paths, url);
                Debug.Log($"[RoulinSync] right-click sync: {relayed} change(s) relayed → {url}");
                await RoulinAssetWatcher.RefreshAsync(url);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}