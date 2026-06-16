using System;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Build.Meta
{
    // Two-phase orchestrator: the self-check must run across every
    // candidate first so reference-checks (e.g. PayloadDepDriftCheck) can
    // read the survivor set when classifying transitive drift.
    public sealed class RestoreScreener
    {
        private readonly IStalenessCheck selfCheck;
        private readonly IReadOnlyList<IStalenessCheck> referenceChecks;

        public RestoreScreener(
            IStalenessCheck selfCheck,
            IReadOnlyList<IStalenessCheck> referenceChecks)
        {
            this.selfCheck = selfCheck;
            this.referenceChecks = referenceChecks ?? Array.Empty<IStalenessCheck>();
        }

        public ScreenReport Screen(IReadOnlyList<RestoreCandidate> candidates)
        {
            var state = new ScreenState();
            var rejectionByReason = new Dictionary<string, int>();
            if (selfCheck != null)
            {
                rejectionByReason[selfCheck.Reason] = 0;
            }
            foreach (var check in referenceChecks)
            {
                rejectionByReason[check.Reason] = 0;
            }

            // Phase 1: self-check. A null selfCheck lets every candidate
            // through to phase 2.
            if (selfCheck == null)
            {
                foreach (var c in candidates)
                {
                    state.SurvivedSelfCheck.Add(c.SelfGuid);
                }
            }
            else
            {
                foreach (var c in candidates)
                {
                    if (!selfCheck.IsStale(c, state))
                    {
                        state.SurvivedSelfCheck.Add(c.SelfGuid);
                    }
                }
            }

            // Phase 2: reference-checks per surviving candidate.
            var passed = new HashSet<GUID>();
            foreach (var c in candidates)
            {
                if (!state.SurvivedSelfCheck.Contains(c.SelfGuid))
                {
                    // Unreachable when selfCheck is null (phase 1 adds
                    // everyone) but guard the deref anyway.
                    if (selfCheck != null)
                    {
                        rejectionByReason[selfCheck.Reason]++;
                    }
                    continue;
                }

                string staleReason = null;
                foreach (var check in referenceChecks)
                {
                    if (check.IsStale(c, state))
                    {
                        staleReason = check.Reason;
                        break;
                    }
                }

                if (staleReason == null)
                {
                    passed.Add(c.SelfGuid);
                }
                else
                {
                    rejectionByReason[staleReason]++;
                }
            }

            return new ScreenReport(passed, rejectionByReason, state.ObjectIdsCache.Count);
        }
    }
}
