using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Roulin.Editor.Vcs
{
    // Domain-facing wrapper around Roulin server's /diff endpoint. Callers pass
    // a since-SHA (or null for "server-recorded base") and get back a base
    // revision + the Unity-project-relative paths of dirty files.
    //
    // Encapsulates three concerns the build script used to do itself:
    //   1. HTTP call via RoulinServerClient.GetDiffAsync (raw response)
    //   2. Discovering the git-root ancestor of the Unity project so raw
    //      repo-relative paths can be trimmed to project-relative
    //   3. Filtering to Unity paths only (Assets/ or Packages/), silently
    //      dropping everything else
    public sealed class VcsDiffClient
    {
        private readonly RoulinServerClient _server;

        public VcsDiffClient(RoulinServerClient server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public async Task<VcsDiffResult> FetchProjectDiffAsync(
            string sinceSha = null,
            CancellationToken ct = default)
        {
            var raw = await _server.GetDiffAsync(sinceSha, ct);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var gitRoot = FindGitRoot(projectRoot);
            var unityPaths = VcsDiffPathNormalizer.Normalize(
                gitRoot, projectRoot, raw?.uncommitted);
            return new VcsDiffResult(raw?.revision, unityPaths);
        }

        // Walk parent directories from the Unity project root until a .git
        // marker turns up. Falls back to the start directory when nothing is
        // found — the normaliser handles that case as "no prefix stripping".
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

        public VcsDiffResult(string baseRevision, IReadOnlyList<string> unityPaths)
        {
            BaseRevision = baseRevision;
            UnityPaths = unityPaths ?? Array.Empty<string>();
        }
    }
}
