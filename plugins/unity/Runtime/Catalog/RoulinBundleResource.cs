using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Roulin
{
    // Lazy AssetBundle wrapper. DD path keeps the bundle null; LoadAsset path
    // synchronously LoadFromFiles on first GetAssetBundle().
    public class RoulinBundleResource : IAssetBundleResource
    {
        // LoadFromFile call counter; E2E tests assert it's 0 during DD and
        // +1 per first-time LoadAssetAsync of an unloaded bundle.
        public static int LoadFromFileInvocations { get; private set; }
        public static void ResetLoadCounter() => LoadFromFileInvocations = 0;

        // Diagnostic-only registry for Roulin.GetLoadedBundles.
        static readonly Dictionary<string, RoulinBundleResource> s_Loaded =
            new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<string> GetLoadedBlobHashes() =>
            new HashSet<string>(s_Loaded.Keys, StringComparer.OrdinalIgnoreCase);

        readonly string                 _blobHash;
        readonly string                 _localPath;
        readonly IAssetBundleResource[] _deps;
        AssetBundle                     _bundle;

        // Already-loaded ctor; no lazy path.
        internal RoulinBundleResource(string blobHash, AssetBundle bundle)
        {
            _blobHash = blobHash;
            _bundle   = bundle;
            if (bundle != null)
            {
                LoadFromFileInvocations++;
                s_Loaded[blobHash] = this;
            }
        }

        // Deferred-load ctor; deps may be null.
        internal RoulinBundleResource(string blobHash, string localPath, IAssetBundleResource[] deps)
        {
            _blobHash  = blobHash;
            _localPath = localPath;
            _deps      = deps;
        }

        public AssetBundle GetAssetBundle()
        {
            if (_bundle != null) return _bundle;
            if (_deps != null)
                foreach (var dep in _deps)
                    dep?.GetAssetBundle();
            _bundle = AssetBundle.LoadFromFile(_localPath);
            if (_bundle != null)
            {
                LoadFromFileInvocations++;
                s_Loaded[_blobHash] = this;
            }
            return _bundle;
        }

        internal void Unload()
        {
            if (_bundle == null) return;
            _bundle.Unload(true);
            _bundle = null;
            s_Loaded.Remove(_blobHash);
        }
    }
}
