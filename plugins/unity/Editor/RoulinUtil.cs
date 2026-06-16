using System;
using System.Diagnostics;
using System.IO;

namespace Roulin.Editor
{
    public static class RoulinUtil
    {
        // Binary IEC units (1 KB = 1024 B).
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            if (bytes < 1024L * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return $"{bytes / (1024.0 * 1024):F1} MB";
            }

            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // Fallback revision id when no git SHA / CLI override is available.
        public static string TimestampRevision()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        // Full HEAD SHA via `git rev-parse HEAD`, or null on any failure.
        public static string TryGitSha()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory()
                };
                using var proc = Process.Start(psi);
                var s = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                return proc.ExitCode == 0 && !string.IsNullOrEmpty(s) ? s : null;
            }
            catch
            {
                return null;
            }
        }

        // Reads "-flag value" or "-flag=value" from Unity's process args.
        public static string TryCommandLineArg(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == flag && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                if (args[i].StartsWith(flag + "="))
                {
                    return args[i].Substring(flag.Length + 1);
                }
            }
            return null;
        }
    }
}
