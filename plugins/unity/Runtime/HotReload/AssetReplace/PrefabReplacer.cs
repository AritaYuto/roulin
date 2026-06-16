#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using UnityEngine;

namespace Roulin.HotReload
{
    // Hot-reloads prefab addresses by JsonUtility-copying components from
    // the new prefab onto live instances (tracked by RoulinInstanceProvider).
    // Limits: Transform skipped; structural changes (added/removed Components
    // or GameObjects) not supported; only Addressables.InstantiateAsync is tracked.
    public sealed class PrefabReplacer : IAssetReplacer
    {
        public bool TryReplace(string address, UnityEngine.Object oldObj, UnityEngine.Object newObj)
        {
            if (oldObj is not GameObject oldPrefab) return false;
            if (newObj is not GameObject newPrefab) return false;
            if (string.IsNullOrEmpty(address))
            {
                Debug.LogWarning("[PrefabReplacer] no address provided; cannot fan out to live instances");
                return false;
            }

            int updatedInstances = 0;
            int failedInstances  = 0;
            foreach (var live in RoulinInstanceProvider.LiveInstances(address))
            {
                try
                {
                    CopyTree(newPrefab.transform, live.transform);
                    updatedInstances++;
                }
                catch (Exception e)
                {
                    failedInstances++;
                    Debug.LogWarning($"[PrefabReplacer] {address}: failed updating instance '{live.name}': {e.Message}");
                }
            }

            Debug.Log($"[PrefabReplacer] {address}: updated {updatedInstances} instance(s)" +
                      (failedInstances > 0 ? $", {failedInstances} failed" : ""));

            // Address is a prefab; even with 0 live instances, we own this layer.
            return true;
        }

        // Parallel-walk src/dst transforms; recurses by matching child index.
        static void CopyTree(Transform src, Transform dst)
        {
            CopyComponents(src, dst);

            int childCount = Math.Min(src.childCount, dst.childCount);
            for (int i = 0; i < childCount; i++)
                CopyTree(src.GetChild(i), dst.GetChild(i));

            // Children beyond dst.childCount are structural changes; out of scope.
        }

        // Components match by type, in order — same-type duplicates pair by index.
        static void CopyComponents(Transform src, Transform dst)
        {
            var srcComps = src.GetComponents<Component>();
            var dstComps = dst.GetComponents<Component>();

            // Per-type consumption count for same-type pairing.
            var dstUsed = new bool[dstComps.Length];

            foreach (var s in srcComps)
            {
                if (s == null) continue;
                if (s is Transform) continue;

                int dstIdx = FindMatchingComponent(dstComps, dstUsed, s.GetType());
                if (dstIdx < 0) continue;
                dstUsed[dstIdx] = true;

                CopyComponentValues(s, dstComps[dstIdx]);
            }
        }

        static int FindMatchingComponent(Component[] dstComps, bool[] used, Type type)
        {
            for (int i = 0; i < dstComps.Length; i++)
            {
                if (used[i]) continue;
                var c = dstComps[i];
                if (c == null) continue;
                if (c.GetType() != type) continue;
                return i;
            }
            return -1;
        }

        static void CopyComponentValues(Component src, Component dst)
        {
            try
            {
                string json = JsonUtility.ToJson(src);
                if (string.IsNullOrEmpty(json) || json == "{}") return;
                JsonUtility.FromJsonOverwrite(json, dst);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[PrefabReplacer] component copy failed ({src.GetType().Name} → {dst.name}): {e.Message}");
            }
        }
    }
}
#endif
