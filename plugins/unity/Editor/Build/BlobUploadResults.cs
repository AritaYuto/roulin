using System;
using System.Collections.Generic;
using UnityEditor.Build.Pipeline.Interfaces;

namespace Roulin.Editor.Build
{
    // SBP-injectable record of "what RoulinPublishBlobs uploaded this run".
    // Filled by RoulinPublishBlobs, read by RoulinPublishParcel when building
    // the catalog (Bundle.blob_hash + Bundle.size_bytes come from here).
    public interface IBlobUploadResults : IContextObject
    {
        // Returns false when the bundle was not uploaded this run.
        bool TryGet(string bundleName, out string blobHashHex, out long sizeBytes);
        IEnumerable<string> Bundles { get; }
        int Count { get; }
    }

    public sealed class BlobUploadResults : IBlobUploadResults
    {
        private readonly Dictionary<string, (string Hash, long Size)> mResults =
            new(StringComparer.Ordinal);

        public IEnumerable<string> Bundles => mResults.Keys;
        public int Count => mResults.Count;

        public void Add(string bundleName, string blobHashHex, long sizeBytes)
        {
            if (string.IsNullOrEmpty(bundleName)) throw new ArgumentException("bundleName");
            if (string.IsNullOrEmpty(blobHashHex)) throw new ArgumentException("blobHashHex");
            mResults[bundleName] = (blobHashHex, sizeBytes);
        }

        public bool TryGet(string bundleName, out string blobHashHex, out long sizeBytes)
        {
            if (mResults.TryGetValue(bundleName, out var v))
            {
                blobHashHex = v.Hash;
                sizeBytes = v.Size;
                return true;
            }
            blobHashHex = null;
            sizeBytes = 0;
            return false;
        }
    }
}
