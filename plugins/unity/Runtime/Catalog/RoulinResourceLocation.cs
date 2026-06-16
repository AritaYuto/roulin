using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;

namespace Roulin
{
    internal class RoulinAssetData
    {
        internal string InternalName; // passed to AssetBundle.LoadAsset()
    }

    // Extends AssetBundleRequestOptions so Addressables internals that cast
    // dep bundle Data to AssetBundleRequestOptions succeed.
    internal class RoulinBundleData : AssetBundleRequestOptions
    {
        string _blobHashHex;

        // Mirrored to BundleName so Addressables can read the roulin identity.
        internal string BlobHashHex
        {
            get => _blobHashHex;
            set
            {
                _blobHashHex = value;
                BundleName   = value;
            }
        }

        // Roulin owns its on-disk blob store; bypass the inherited
        // UnityEngine.Caching check and look there instead.
        public override long ComputeSize(IResourceLocation loc, ResourceManager rm)
        {
            if (BundleSize <= 0 || string.IsNullOrEmpty(_blobHashHex)) return 0;
            string localPath = Path.Combine(
                Roulin.LocalDir, "blobs",
                _blobHashHex.Substring(0, 2), _blobHashHex);
            return File.Exists(localPath) ? 0 : BundleSize;
        }
    }

    internal class RoulinResourceLocation : IResourceLocation
    {
        readonly string                    _internalId;
        readonly string                    _providerId;
        readonly IList<IResourceLocation>  _deps;
        readonly object                    _data;
        readonly Type                      _resourceType;

        internal RoulinResourceLocation(
            string internalId,
            string providerId,
            Type resourceType,
            object data,
            IList<IResourceLocation> deps = null)
        {
            _internalId   = internalId;
            _providerId   = providerId;
            _resourceType = resourceType;
            _data         = data;
            _deps         = deps ?? new List<IResourceLocation>();
        }

        public string                   InternalId    => _internalId;
        public string                   ProviderId    => _providerId;
        public IList<IResourceLocation> Dependencies  => _deps;
        public object                   Data          => _data;
        public Type                     ResourceType  => _resourceType;
        public bool                     HasDependencies => _deps != null && _deps.Count > 0;
        public bool                     IsDependency  { get; set; }
        public string                   PrimaryKey    => _internalId;

        public int Hash(Type type) =>
            _internalId.GetHashCode() * 31 ^ (type?.GetHashCode() ?? 0);

        public int DependencyHashCode
        {
            get
            {
                int hash = 17;
                for (int i = 0; i < _deps.Count; i++)
                    hash = hash * 31 + _deps[i].Hash(typeof(object));
                return hash;
            }
        }
    }
}
