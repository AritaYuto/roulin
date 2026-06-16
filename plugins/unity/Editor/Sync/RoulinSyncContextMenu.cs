using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        // Addressables-managed asset.
        [MenuItem(MenuPath, validate = true, priority = MenuPriority)]
        private static bool SyncValidate()
        {
            return ResolveSelectedAddressablePaths().Count > 0;
        }

        private static List<string> ResolveSelectedAddressablePaths()
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            var result = new List<string>();
            if (aas == null)
            {
                return result;
            }

            foreach (var guid in Selection.assetGUIDs)
            {
                if (aas.FindAssetEntry(guid) == null)
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    result.Add(path);
                }
            }

            return result;
        }

        private static async Task RunAsync(List<string> paths, string url)
        {
            try
            {
                var relayed = await RoulinSyncService.SyncAsync(paths, url);
                Debug.Log($"[RoulinSync] right-click sync: {relayed} change(s) relayed → {url}");
                // Keep the dirty-set in sync — these paths were just synced,
                // so the Sync window shouldn't keep them as "pending".
                foreach (var p in paths)
                {
                    RoulinAssetWatcher.Remove(p);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}