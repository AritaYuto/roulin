using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Roulin
{
    // Loads a typed asset from a RoulinBundleProvider-produced dependency.
    public class RoulinAssetProvider : ResourceProviderBase
    {
        public const string Id = nameof(RoulinAssetProvider);
        public override string ProviderId => Id;

        // Hot-reload registry hooks. No-op when no subscriber.
        internal static event Action<string, UnityEngine.Object> OnAssetProvided;
        internal static event Action<string>                     OnAssetReleased;

        public override void Provide(ProvideHandle handle)
        {
            UnityEngine.Debug.Log($"[RoulinAssetProvider] Provide: key={handle.Location.PrimaryKey}");
            var data = handle.Location.Data as RoulinAssetData;
            if (data == null)
            {
                handle.Complete<UnityEngine.Object>(null, false,
                    new Exception("RoulinAssetProvider: missing RoulinAssetData"));
                return;
            }

            // Force LoadFromFile on every dep so script bundles register in
            // AppDomain before LoadAssetAsync deserialises. Index 0 = primary.
            int depCount = handle.DependencyCount;
            AssetBundle primaryBundle = null;
            for (int i = 0; i < depCount; i++)
            {
                var depRes = handle.GetDependency<IAssetBundleResource>(i);
                var ab     = depRes?.GetAssetBundle();
                if (i == 0) primaryBundle = ab;
            }

            if (primaryBundle == null)
            {
                handle.Complete<UnityEngine.Object>(null, false,
                    new Exception($"RoulinAssetProvider: bundle dependency not resolved for [{handle.Location.InternalId}]"));
                return;
            }

            LoadAsync(handle, primaryBundle, data.InternalName).Forget();
        }

        public override void Release(IResourceLocation location, object asset)
        {
            if (location?.Data is RoulinAssetData d)
                OnAssetReleased?.Invoke(d.InternalName);
        }

        static async UniTaskVoid LoadAsync(ProvideHandle handle, AssetBundle bundle, string internalName)
        {
            var asset = await bundle.LoadAssetAsync(internalName, handle.Type);

            if (asset == null)
            {
                handle.Complete<UnityEngine.Object>(null, false,
                    new Exception($"RoulinAssetProvider: asset [{internalName}] not found in bundle"));
                return;
            }

            OnAssetProvided?.Invoke(internalName, (UnityEngine.Object)asset);
            handle.Complete(asset, true, null);
        }
    }
}
