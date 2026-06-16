#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Roulin.HotReload
{
    // Address → live UnityEngine.Object map. Provides the Replace primitive
    // and owns the SSE Driver. Lifetime bound to Roulin.Initialize / Shutdown.
    public sealed class HotReloadController : IDisposable
    {
        readonly Dictionary<string, UnityEngine.Object> _live        = new(StringComparer.Ordinal);
        readonly Dictionary<UnityEngine.Object, string> _liveReverse = new();
        readonly List<IAssetReplacer>                   _replacers   = new() {
            new SpriteReplacer(),
            new Texture2DReplacer(),
            new MaterialReplacer(),
            new MeshReplacer(),
            new AudioClipReplacer(),
            new ScriptableObjectReplacer(),
            new PrefabReplacer(),
        };
        readonly RoulinHotReloadDriver _driver;

        // baseUrl null/empty = Replace primitive only, no SSE Driver.
        // autoStartDriver = false = caller will Start() the Driver manually.
        public HotReloadController(string baseUrl, bool autoStartDriver = true)
        {
            RoulinInstanceProvider.Install();
            RoulinAssetProvider.OnAssetProvided += OnProvided;
            RoulinAssetProvider.OnAssetReleased += OnReleased;

            if (!string.IsNullOrEmpty(baseUrl))
            {
                _driver = new RoulinHotReloadDriver(baseUrl);
                if (autoStartDriver) _driver.Start();
            }
        }

        public RoulinHotReloadDriver Driver => _driver;

        public void Dispose()
        {
            // Driver dispatches Replace() to the main thread; stop it before
            // the live-object dictionaries are cleared to avoid the race.
            _driver?.Dispose();
            RoulinAssetProvider.OnAssetProvided -= OnProvided;
            RoulinAssetProvider.OnAssetReleased -= OnReleased;
            _live.Clear();
            _liveReverse.Clear();
        }

        public int LiveCount => _live.Count;

        public UnityEngine.Object Get(string address)
            => _live.TryGetValue(address, out var obj) ? obj : null;

        // Reverse lookup; null when the object isn't roulin-tracked.
        public string FindAddressFor(UnityEngine.Object obj)
            => obj != null && _liveReverse.TryGetValue(obj, out var addr) ? addr : null;

        public void AddAssetReplacer(IAssetReplacer replacer) => _replacers.Add(replacer);

        public bool Replace(string address, byte[] newBundleBytes)
        {
            if (!_live.TryGetValue(address, out var oldObj) || oldObj == null)
            {
                Debug.LogWarning($"[HotReloadController] Replace: address not live: {address}");
                return false;
            }

            var oldBundle = FindLoadedBundleContaining(address);
            if (oldBundle != null) oldBundle.Unload(false);

            var bundle = AssetBundle.LoadFromMemory(newBundleBytes);
            if (bundle == null)
            {
                Debug.LogError($"[HotReloadController] Replace: LoadFromMemory failed: {address}");
                return false;
            }

            try
            {
                var newObj = bundle.LoadAsset(address, oldObj.GetType());
                if (newObj == null)
                {
                    Debug.LogError(
                        $"[HotReloadController] Replace: asset {address} (type={oldObj.GetType().Name}) " +
                        "not found in new bundle");
                    return false;
                }

                bool replaced = false;
                foreach (var replacer in _replacers)
                {
                    if (replacer.TryReplace(address, oldObj, newObj))
                    {
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    Debug.LogError(
                        "[HotReloadController] Replace: no replacer succeeded for type " +
                        $"{oldObj.GetType().Name} (address={address})");
                    return false;
                }
                Debug.Log($"[HotReloadController] Replace: {address} ({oldObj.GetType().Name}) updated in place");
                return true;
            }
            finally
            {
                // Texture2D: pixels already copied; new bundle is disposable.
                // Material support uses Unload(false) to keep sub-asset refs alive.
                bundle.Unload(true);
            }
        }

        static AssetBundle FindLoadedBundleContaining(string address)
        {
            foreach (var ab in AssetBundle.GetAllLoadedAssetBundles())
                if (ab != null && ab.Contains(address))
                    return ab;
            return null;
        }

        void OnProvided(string address, UnityEngine.Object asset)
        {
            _live[address] = asset;
            if (asset != null) _liveReverse[asset] = address;
        }

        void OnReleased(string address)
        {
            if (_live.TryGetValue(address, out var obj) && obj != null)
                _liveReverse.Remove(obj);
            _live.Remove(address);
        }
    }
}
#endif
