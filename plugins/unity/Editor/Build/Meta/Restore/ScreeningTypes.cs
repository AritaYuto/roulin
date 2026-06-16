using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Roulin.Editor.Build.Meta
{
    public readonly struct RestoreCandidate
    {
        public readonly GUID SelfGuid;
        public readonly Hash128 StoredHash;
        public readonly IReadOnlyList<ObjectIdentifier> ReferencedObjects;
        // null when the source blob_meta predates referenced_asset_hashes;
        // hash fast-accept then falls through to the ObjectId path.
        public readonly IReadOnlyDictionary<GUID, Hash128> ReferencedAssetHashes;

        public RestoreCandidate(
            GUID selfGuid,
            Hash128 storedHash,
            IReadOnlyList<ObjectIdentifier> referencedObjects,
            IReadOnlyDictionary<GUID, Hash128> referencedAssetHashes)
        {
            SelfGuid = selfGuid;
            StoredHash = storedHash;
            ReferencedObjects = referencedObjects;
            ReferencedAssetHashes = referencedAssetHashes;
        }
    }

    // Shared scratch for one Screen call. The caches dedupe per-guid
    // lookups across candidates.
    public sealed class ScreenState
    {
        public HashSet<GUID> SurvivedSelfCheck { get; } = new HashSet<GUID>();
        public Dictionary<GUID, HashSet<ObjectIdentifier>> ObjectIdsCache { get; }
            = new Dictionary<GUID, HashSet<ObjectIdentifier>>();
        public Dictionary<GUID, Hash128> HashMatchCache { get; }
            = new Dictionary<GUID, Hash128>();
    }

    // RejectionByReason preserves registration order so logs are
    // deterministic.
    public sealed class ScreenReport
    {
        public HashSet<GUID> Passed { get; }
        public IReadOnlyDictionary<string, int> RejectionByReason { get; }
        public int ObjectIdsCacheSize { get; }

        public ScreenReport(
            HashSet<GUID> passed,
            IReadOnlyDictionary<string, int> rejectionByReason,
            int objectIdsCacheSize)
        {
            Passed = passed;
            RejectionByReason = rejectionByReason;
            ObjectIdsCacheSize = objectIdsCacheSize;
        }
    }
}
