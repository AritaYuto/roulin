using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEditor.Build.Pipeline.Interfaces;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    internal sealed class RoulinTimingBuildLogger : IBuildLogger
    {
        private readonly Dictionary<string, AggregateEntry> _agg = new(StringComparer.Ordinal);

        // SBP ArchiveItemsThreaded calls Begin/EndBuildStep from worker threads;
        // without the lock Stack<T> resize races crash the build with an NRE
        // inside ArchiveSingleItem.
        private readonly object _lock = new();
        private readonly Stack<StepFrame> _stack = new();
        private readonly Stopwatch _wallClock = Stopwatch.StartNew();

        // When false, suppresses per-step Debug.Log lines (FlushSummary still emits).
        public bool EmitPerStepLog { get; set; }

        public long WallClockMs => _wallClock.ElapsedMilliseconds;
        public long TopLevelSumMs { get; private set; }

        public void BeginBuildStep(LogLevel level, string stepName, bool subStepsCanBeThreaded)
        {
            lock (_lock)
            {
                _stack.Push(new StepFrame
                {
                    Name = stepName ?? "<unnamed>",
                    Sw = Stopwatch.StartNew(),
                    Depth = _stack.Count
                });
            }
        }

        public void EndBuildStep()
        {
            StepFrame f;
            long ms;
            string indent;
            lock (_lock)
            {
                if (_stack.Count == 0)
                {
                    return;
                }

                f = _stack.Pop();
                if (f.Sw == null)
                {
                    // Defensive: a logger bug must never tank the build.
                    return;
                }

                f.Sw.Stop();
                ms = f.Sw.ElapsedMilliseconds;

                if (!_agg.TryGetValue(f.Name, out var e))
                {
                    e = new AggregateEntry { Name = f.Name, MinDepth = f.Depth };
                    _agg[f.Name] = e;
                }

                e.TotalMs += ms;
                e.Count += 1;
                if (f.Depth < e.MinDepth)
                {
                    e.MinDepth = f.Depth;
                }

                if (f.Depth == 0)
                {
                    TopLevelSumMs += ms;
                }

                indent = f.Depth > 0 ? new string(' ', f.Depth * 2) : string.Empty;
            }

            if (!EmitPerStepLog)
            {
                return;
            }

            // Log outside the lock — Unity logging may call back into our hooks.
            Debug.Log($"[RoulinBuild][SBP]{indent} {ms,7} ms  {f.Name}");
        }

        public void AddEntry(LogLevel level, string msg)
        {
            // Drop Info/Verbose (already covered by step tracking); surface
            // warnings/errors so they aren't swallowed.
            if (level == LogLevel.Error)
            {
                Debug.LogError($"[RoulinBuild][SBP] {msg}");
            }
            else if (level == LogLevel.Warning)
            {
                Debug.LogWarning($"[RoulinBuild][SBP] {msg}");
            }
        }

        // Emits one Debug.Log: optional macro-phase block + SBP per-step block.
        // Both share "Unaccounted = wallClock − Σ" semantics.
        public void FlushSummary(long runBuildWallClockMs,
            IReadOnlyList<(string Name, long Ms)> macroPhases = null)
        {
            _wallClock.Stop();

            var sb = new StringBuilder();
            sb.AppendLine();

            if (macroPhases != null && macroPhases.Count > 0)
            {
                long phaseSum = 0;
                foreach (var p in macroPhases)
                {
                    phaseSum += p.Ms;
                }
                var macroGap = runBuildWallClockMs - phaseSum;

                sb.AppendLine("─── RunBuild phase timing ────────────────────────────────────────────");
                sb.AppendLine($"  {"phase",-50}  {"ms",10}");
                sb.AppendLine("  " + new string('-', 50) + "  ----------");
                foreach (var p in macroPhases)
                {
                    sb.AppendLine($"    {p.Name,-50}  {p.Ms,10}");
                }
                sb.AppendLine();
                sb.AppendLine($"  Σ phases                                          : {phaseSum,10} ms");
                sb.AppendLine($"  RunBuild wall-clock                               : {runBuildWallClockMs,10} ms");
                sb.AppendLine($"  Unaccounted (RunBuild − Σ phases)                 : {macroGap,10} ms");
                sb.AppendLine();
            }

            var rows = _agg.Values.OrderByDescending(r => r.TotalMs).ToList();
            var aggTotal = rows.Sum(r => r.TotalMs);
            var topLevelTotal = rows.Where(r => r.MinDepth == 0).Sum(r => r.TotalMs);
            var sbpGap = runBuildWallClockMs - topLevelTotal;

            sb.AppendLine("─── SBP timing summary (aggregated by step name) ─────────────────────");
            sb.AppendLine($"  {"step",-54}  {"calls",5}  {"total ms",10}");
            sb.AppendLine("  " + new string('-', 54) + "  -----  ----------");
            foreach (var r in rows)
            {
                var mark = r.MinDepth == 0 ? "    " : "  ↳ ";
                var name = r.Name.Length > 54 ? r.Name.Substring(0, 54) : r.Name;
                sb.AppendLine($"{mark}{name,-54}  {r.Count,5}  {r.TotalMs,10}");
            }

            sb.AppendLine();
            sb.AppendLine($"  Σ all rows (incl. nested)      : {aggTotal,10} ms");
            sb.AppendLine($"  Σ top-level steps              : {topLevelTotal,10} ms");
            sb.AppendLine($"  logger wall-clock (first Begin→FlushSummary) : {_wallClock.ElapsedMilliseconds,10} ms");
            sb.AppendLine($"  RunBuild wall-clock (build script reported)  : {runBuildWallClockMs,10} ms");
            sb.AppendLine($"  Unaccounted (RunBuild − Σ top-level)         : {sbpGap,10} ms");
            sb.AppendLine("───────────────────────────────────────────────────────────────────────");
            Debug.Log(sb.ToString());
        }

        private struct StepFrame
        {
            public string Name;
            public Stopwatch Sw;
            public int Depth;
        }

        private sealed class AggregateEntry
        {
            public int Count;
            public int MinDepth;
            public string Name;
            public long TotalMs;
        }
    }
}