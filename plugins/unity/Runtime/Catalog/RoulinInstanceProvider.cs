#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using System.Collections.Generic;
using System.Reflection;
using Roulin.HotReload;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Roulin
{
    // Side-effect-only decorator over Addressables.InstanceProvider. Tracks
    // (address → live GameObject) so PrefabReplacer can find instances on
    // hot reload. Gated under UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG.
    public sealed class RoulinInstanceProvider : IInstanceProvider
    {
        readonly IInstanceProvider _inner;

        // WeakReference so game-code Destroy without ReleaseInstance still GCs.
        static readonly Dictionary<string, List<WeakReference<GameObject>>> s_ByAddress
            = new Dictionary<string, List<WeakReference<GameObject>>>();

        static readonly Dictionary<int, string> s_InstanceIdToAddress
            = new Dictionary<int, string>();

        static bool s_Installed;

        // The InstanceProvider setter sits on internal AddressablesImpl, only
        // reachable via `internal Addressables.Instance`. Reflection is the
        // pragmatic path; failure degrades to "prefab hot reload disabled".
        public static void Install()
        {
            if (s_Installed) return;
            try
            {
                var instanceProp = typeof(Addressables).GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);
                if (instanceProp == null)
                {
                    Debug.LogWarning("[RoulinInstanceProvider] Addressables.Instance not found via reflection — Unity Addressables internals changed; prefab hot reload disabled");
                    return;
                }

                var impl = instanceProp.GetValue(null);
                if (impl == null)
                {
                    Debug.LogWarning("[RoulinInstanceProvider] Addressables.Instance is null at Install time; prefab hot reload disabled");
                    return;
                }

                var providerProp = impl.GetType().GetProperty("InstanceProvider", BindingFlags.Public | BindingFlags.Instance);
                if (providerProp == null || !providerProp.CanWrite)
                {
                    Debug.LogWarning("[RoulinInstanceProvider] AddressablesImpl.InstanceProvider setter unavailable; prefab hot reload disabled");
                    return;
                }

                var current = providerProp.GetValue(impl) as IInstanceProvider;
                if (current is RoulinInstanceProvider) return;

                if (current == null)
                    current = new InstanceProvider();

                providerProp.SetValue(impl, new RoulinInstanceProvider(current));
                s_Installed = true;
                Debug.Log("[RoulinInstanceProvider] installed (Addressables InstanceProvider hijacked via reflection)");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoulinInstanceProvider] install failed: {e.Message}; prefab hot reload disabled");
            }
        }

        RoulinInstanceProvider(IInstanceProvider inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public GameObject ProvideInstance(
            ResourceManager rm,
            AsyncOperationHandle<GameObject> prefabHandle,
            InstantiationParameters instantiateParameters)
        {
            // Forward verbatim; don't mutate the inner return value.
            var go = _inner.ProvideInstance(rm, prefabHandle, instantiateParameters);
            if (go != null)
            {
                try { Track(prefabHandle, go); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RoulinInstanceProvider] track failed: {e.Message}");
                }
            }
            return go;
        }

        public void ReleaseInstance(ResourceManager rm, GameObject instance)
        {
            try { Untrack(instance); }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoulinInstanceProvider] untrack failed: {e.Message}");
            }
            _inner.ReleaseInstance(rm, instance);
        }

        // Snapshot-safe: prunes dead WeakReferences; safe to Destroy mid-iteration.
        public static IEnumerable<GameObject> LiveInstances(string address)
        {
            if (string.IsNullOrEmpty(address)) yield break;
            if (!s_ByAddress.TryGetValue(address, out var list)) yield break;

            // Snapshot live targets and prune dead refs in one pass.
            var snapshot = new List<GameObject>(list.Count);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].TryGetTarget(out var go) && go != null)
                    snapshot.Add(go);
                else
                    list.RemoveAt(i);
            }
            if (list.Count == 0) s_ByAddress.Remove(address);

            foreach (var go in snapshot) yield return go;
        }

        static void Track(AsyncOperationHandle<GameObject> handle, GameObject go)
        {
            // FindAddressFor returns null when the prefab wasn't loaded via
            // roulin — silently skip tracking in that case.
            var prefabAsset = handle.IsValid() ? handle.Result : null;
            if (prefabAsset == null) return;
            var hr = Roulin.HotReload;
            if (hr == null) return;
            string addr = hr.FindAddressFor(prefabAsset);
            if (string.IsNullOrEmpty(addr)) return;

            if (!s_ByAddress.TryGetValue(addr, out var list))
                s_ByAddress[addr] = list = new List<WeakReference<GameObject>>();
            list.Add(new WeakReference<GameObject>(go));
            s_InstanceIdToAddress[go.GetInstanceID()] = addr;
        }

        static void Untrack(GameObject go)
        {
            int id = go.GetInstanceID();
            if (!s_InstanceIdToAddress.TryGetValue(id, out var addr)) return;
            s_InstanceIdToAddress.Remove(id);
            if (!s_ByAddress.TryGetValue(addr, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out var alive) || alive == null || alive == go)
                    list.RemoveAt(i);
            }
            if (list.Count == 0) s_ByAddress.Remove(addr);
        }
    }
}
#endif
