using System;
using System.Collections.Generic;

namespace Roulin.Editor.Vcs
{
    // Kept public only for pure-function unit tests; production callers go
    // through VcsDiffClient.
    public static class VcsDiffPathNormalizer
    {
        public static List<string> Normalize(
            string gitRoot,
            string projectRoot,
            IEnumerable<string> repoRelativePaths)
        {
            var result = new List<string>();
            if (repoRelativePaths == null)
            {
                return result;
            }

            var prefix = ComputeProjectPrefix(gitRoot, projectRoot);
            foreach (var raw in repoRelativePaths)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                var normalized = raw.Replace('\\', '/');
                if (prefix.Length > 0
                    && !normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = prefix.Length == 0
                    ? normalized
                    : normalized.Substring(prefix.Length);

                if (relative.StartsWith("Assets/", StringComparison.Ordinal)
                    || relative.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    result.Add(relative);
                }
            }
            return result;
        }

        public static string ComputeProjectPrefix(string gitRoot, string projectRoot)
        {
            if (string.IsNullOrEmpty(gitRoot) || string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            var normalizedGit = gitRoot.Replace('\\', '/').TrimEnd('/');
            var normalizedProject = projectRoot.Replace('\\', '/').TrimEnd('/');

            if (string.Equals(normalizedGit, normalizedProject, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (!normalizedProject.StartsWith(normalizedGit + "/", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return normalizedProject.Substring(normalizedGit.Length + 1) + "/";
        }
    }
}
