using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;

namespace Roulin.Editor.Sync
{
    // In-memory dirty set of Addressables-managed assets re-imported this
    // session. Editor restart drops it by design.
    public sealed class RoulinAssetWatcher : AssetPostprocessor
    {
        private static readonly HashSet<string> s_Dirty = new(StringComparer.Ordinal);

        public static IReadOnlyCollection<string> Dirty => s_Dirty;

        // Imports only; deletes / moves require a parcel change (full build, not Sync).
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var changed = false;
            foreach (var path in importedAssets)
            {
                if (!IsAddressable(path))
                {
                    continue;
                }

                if (s_Dirty.Add(path))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                OnDirtyChanged?.Invoke();
            }
        }

        public static event Action OnDirtyChanged;

        private static bool IsAddressable(string assetPath)
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            if (aas == null)
            {
                return false;
            }

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }

            return aas.FindAssetEntry(guid) != null;
        }

        public static void Clear()
        {
            if (s_Dirty.Count == 0)
            {
                return;
            }

            s_Dirty.Clear();
            OnDirtyChanged?.Invoke();
        }

        public static void Remove(string path)
        {
            if (s_Dirty.Remove(path))
            {
                OnDirtyChanged?.Invoke();
            }
        }
    }
}