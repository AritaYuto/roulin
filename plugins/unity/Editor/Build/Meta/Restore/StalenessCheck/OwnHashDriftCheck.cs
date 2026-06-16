using System;
using UnityEditor;
using UnityEngine;

namespace Roulin.Editor.Build.Meta
{
    // Stale when stored AssetDependencyHash (or scene PrefabDependencyHash)
    // differs from the current AssetDatabase value.
    public sealed class OwnHashDriftCheck : IStalenessCheck
    {
        private readonly Func<GUID, Hash128> currentHashLookup;

        public OwnHashDriftCheck(Func<GUID, Hash128> currentHashLookup)
        {
            this.currentHashLookup = currentHashLookup
                ?? throw new ArgumentNullException(nameof(currentHashLookup));
        }

        public string Reason => "own-hash-drift";

        public bool IsStale(RestoreCandidate candidate, ScreenState state)
        {
            // No stored signal: defer the decision to downstream checks.
            if (!candidate.StoredHash.isValid)
            {
                return false;
            }
            return currentHashLookup(candidate.SelfGuid) != candidate.StoredHash;
        }
    }
}
