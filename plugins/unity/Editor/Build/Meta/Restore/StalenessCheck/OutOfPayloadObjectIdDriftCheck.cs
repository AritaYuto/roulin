// Hash fast-accept gate. Comment out to fall back to unconditional
// GetPlayerObjectIdentifiersInAsset. Soundness rests on the
// "hash unchanged ⇒ ObjectId unchanged" invariant, enforced by
// CrossAssetObjectIdDriftProbeTests. C# #define is file-scoped, so the
// toggle must sit next to the #if it gates.
#define ROULIN_USE_HASH_REFCHECK

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Roulin.Editor.Build.Meta
{
    // Rejects a candidate when a referenced out-of-payload guid's stored
    // ObjectId is missing from current GetPlayerObjectIdentifiersInAsset.
    // A null lookup result (built-in / Library / Resources) is treated as
    // "skip": those refs are resolved at runtime, not from the build pool.
    public sealed class OutOfPayloadObjectIdDriftCheck : IStalenessCheck
    {
        private readonly Func<GUID, ObjectIdentifier[]> currentObjectIdsLookup;
        private readonly Func<GUID, bool> isPayloadResident;
#if ROULIN_USE_HASH_REFCHECK
        private readonly Func<GUID, Hash128> currentHashLookup;
#endif

        public OutOfPayloadObjectIdDriftCheck(
            Func<GUID, ObjectIdentifier[]> currentObjectIdsLookup,
            Func<GUID, bool> isPayloadResident,
            Func<GUID, Hash128> currentHashLookup = null)
        {
            this.currentObjectIdsLookup = currentObjectIdsLookup
                ?? throw new ArgumentNullException(nameof(currentObjectIdsLookup));
            this.isPayloadResident = isPayloadResident
                ?? throw new ArgumentNullException(nameof(isPayloadResident));
#if ROULIN_USE_HASH_REFCHECK
            this.currentHashLookup = currentHashLookup;
#endif
        }

        public string Reason => "out-of-payload-objectid-drift";

        public bool IsStale(RestoreCandidate candidate, ScreenState state)
        {
            var refs = candidate.ReferencedObjects;
            if (refs == null)
            {
                return false;
            }

            foreach (var o in refs)
            {
                if (o.guid == candidate.SelfGuid)
                {
                    continue;
                }

                if (isPayloadResident(o.guid))
                {
                    continue;
                }

#if ROULIN_USE_HASH_REFCHECK
                if (TryFastAcceptByHash(candidate, o.guid, state))
                {
                    continue;
                }
#endif

                if (!state.ObjectIdsCache.TryGetValue(o.guid, out var currentSet))
                {
                    var objectIds = currentObjectIdsLookup(o.guid);
                    currentSet = objectIds == null ? null : new HashSet<ObjectIdentifier>(objectIds);
                    state.ObjectIdsCache[o.guid] = currentSet;
                }

                if (currentSet == null)
                {
                    continue;
                }

                if (!currentSet.Contains(o))
                {
                    return true;
                }
            }

            return false;
        }

#if ROULIN_USE_HASH_REFCHECK
        // Result memoized in state.HashMatchCache so shared deps cost one
        // AssetDependencyHash lookup. Returns false when captured data is
        // absent (legacy meta), forcing fall-through to the ObjectId path.
        private bool TryFastAcceptByHash(RestoreCandidate candidate, GUID guid, ScreenState state)
        {
            if (currentHashLookup == null)
            {
                return false;
            }

            var captured = candidate.ReferencedAssetHashes;
            if (captured == null || !captured.TryGetValue(guid, out var capturedHash))
            {
                return false;
            }

            if (!state.HashMatchCache.TryGetValue(guid, out var current))
            {
                current = currentHashLookup(guid);
                state.HashMatchCache[guid] = current;
            }

            return current == capturedHash;
        }
#endif
    }
}
