using System;
using UnityEditor;

namespace Roulin.Editor.Build.Meta
{
    // Stale when the candidate references a payload-resident guid that
    // itself failed its own-hash check.
    public sealed class PayloadDepDriftCheck : IStalenessCheck
    {
        private readonly Func<GUID, bool> isPayloadResident;

        public PayloadDepDriftCheck(Func<GUID, bool> isPayloadResident)
        {
            this.isPayloadResident = isPayloadResident
                ?? throw new ArgumentNullException(nameof(isPayloadResident));
        }

        public string Reason => "payload-dep-drift";

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

                if (!isPayloadResident(o.guid))
                {
                    continue;
                }

                if (!state.SurvivedSelfCheck.Contains(o.guid))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
