using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Roulin.Editor.Vcs
{
    // Fetches the server's /diff, then trims and filters the returned paths so
    // callers see only Unity-project-relative Assets/ or Packages/ entries.
    public sealed class VcsDiffClient
    {
        private readonly RoulinServerClient _server;

        public VcsDiffClient(RoulinServerClient server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public async Task<VcsDiffResult> FetchProjectDiffAsync(CancellationToken ct = default)
        {
            var raw = await _server.GetDiffAsync(ct);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var gitRoot = FindGitRoot(projectRoot);
            // committed (base_revision..HEAD) and uncommitted worktree edits
            // are both "dirty" from the incremental build's viewpoint —
            // merge before normalisation so downstream sees one list.
            var repoPaths = new List<string>();
            if (raw?.changed != null) repoPaths.AddRange(raw.changed);
            if (raw?.uncommitted != null) repoPaths.AddRange(raw.uncommitted);
            var unityPaths = VcsDiffPathNormalizer.Normalize(
                gitRoot, projectRoot, repoPaths);
            return new VcsDiffResult(
                raw?.base_revision,
                unityPaths,
                raw?.base_bundle_names);
        }

        // Uncommitted-only Unity paths. Used by Sync (hot-reload): committed
        // changes are picked up by the next build, so Sync only cares about
        // worktree edits still in flight. Hits the dedicated /uncommitted
        // endpoint — /diff would waste time on committed-diff + Index parse
        // that Sync never reads.
        public async Task<IReadOnlyList<string>> FetchUncommittedAsync(CancellationToken ct = default)
        {
            var raw = await _server.GetUncommittedAsync(ct);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var gitRoot = FindGitRoot(projectRoot);
            return VcsDiffPathNormalizer.Normalize(gitRoot, projectRoot, raw?.uncommitted);
        }

        // Fallback (no .git found) returns startDir; the normaliser treats
        // that as "no prefix stripping".
        internal static string FindGitRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var marker = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return startDir;
        }
    }

    public sealed class VcsDiffResult
    {
        public string BaseRevision { get; }
        public IReadOnlyList<string> UnityPaths { get; }
        // Bundle names present in the base revision's Index. Empty when there
        // is no base yet; caller falls back to full rebuild anyway.
        public IReadOnlyList<string> BaseBundleNames { get; }

        public VcsDiffResult(
            string baseRevision,
            IReadOnlyList<string> unityPaths,
            IReadOnlyList<string> baseBundleNames)
        {
            BaseRevision = baseRevision;
            UnityPaths = unityPaths ?? Array.Empty<string>();
            BaseBundleNames = baseBundleNames ?? Array.Empty<string>();
        }
    }
}
