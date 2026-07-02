using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Roulin.Editor.PackRule;
using Roulin.Editor.Vcs;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Roulin.Editor.Sync
{
    // Sync-target list, sourced from VCS worktree uncommitted paths filtered
    // through the registered RoulinPackRule. RefreshAsync is the only way to
    // update the set — the previous AssetPostprocessor-based reactive model
    // was dropped because it drifted from git state.
    public static class RoulinAssetWatcher
    {
        private static readonly HashSet<string> s_Dirty = new HashSet<string>(StringComparer.Ordinal);

        public static IReadOnlyCollection<string> Dirty => s_Dirty;
        public static event Action OnDirtyChanged;

        public static async Task RefreshAsync(string serverUrl, CancellationToken ct = default)
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            var packRule = aas != null ? RoulinPackRuleRegistry.Resolve(aas) : null;
            if (packRule == null)
            {
                Debug.LogWarning(
                    "[RoulinAssetWatcher] no IRoulinPackRule registered; Sync target list stays empty. " +
                    "Register a project-specific IRoulinPackRule via RoulinPackRuleRegistry.");
                ReplaceDirty(Array.Empty<string>());
                return;
            }

            IReadOnlyList<string> uncommitted;
            using (var client = new RoulinServerClient(serverUrl))
            {
                var vcs = new VcsDiffClient(client);
                uncommitted = await vcs.FetchUncommittedAsync(ct);
            }

            IReadOnlyDictionary<string, string> claimed;
            try
            {
                claimed = packRule.ResolveGroupsForPaths(uncommitted);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogWarning(
                    $"[RoulinAssetWatcher] pack rule overlap during refresh: {ex.Message}");
                ReplaceDirty(Array.Empty<string>());
                return;
            }

            ReplaceDirty(claimed.Keys);
        }

        public static void Clear()
        {
            if (s_Dirty.Count == 0) return;
            s_Dirty.Clear();
            OnDirtyChanged?.Invoke();
        }

        public static void Remove(string path)
        {
            if (s_Dirty.Remove(path)) OnDirtyChanged?.Invoke();
        }

        private static void ReplaceDirty(IEnumerable<string> paths)
        {
            s_Dirty.Clear();
            foreach (var path in paths) s_Dirty.Add(path);
            OnDirtyChanged?.Invoke();
        }
    }
}
