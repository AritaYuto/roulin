namespace Roulin.Editor.Build.Meta
{
    // One staleness criterion. Implementations decide whether a candidate
    // is stale; RestoreScreener orchestrates self vs reference phases and
    // aggregates rejection counts.
    public interface IStalenessCheck
    {
        // Stable identifier used in logs and rejection counters
        // (e.g. "own-hash-drift", "payload-dep-drift").
        string Reason { get; }

        bool IsStale(RestoreCandidate candidate, ScreenState state);
    }
}
