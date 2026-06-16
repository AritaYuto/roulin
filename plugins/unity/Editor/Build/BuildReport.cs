using Roulin.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    [Serializable]
    // Post-build report: in-memory DTO that is both the on-disk JSON
    // (roulin-build-report.json) and the human summary printed to console.
    public class BuildReport
    {
        public string server;
        public string revision;
        public string generated_utc;
        public int bundle_count;
        public int entry_count;
        public List<BuildReportBundle> bundles = new();

        public long TotalBytes
        {
            get
            {
                long t = 0;
                foreach (var b in bundles)
                {
                    t += b.size_bytes;
                }
                return t;
            }
        }

        public static BuildReport Compose(
            string server,
            string revision,
            Dictionary<string, BundleInput> inputs,
            int entryCount)
        {
            var r = new BuildReport
            {
                server = server,
                revision = revision,
                generated_utc = DateTime.UtcNow.ToString("o"),
                bundle_count = inputs.Count,
                entry_count = entryCount
            };
            foreach (var bi in inputs.Values)
            {
                var b = new BuildReportBundle
                {
                    name = bi.Name,
                    binary_hash = bi.BinaryHash,
                    size_bytes = bi.SizeBytes
                };
                foreach (var e in bi.Entries)
                {
                    var be = new BuildReportEntry { address = e.Address };
                    if (e.Labels != null)
                    {
                        be.labels = new List<string>(e.Labels);
                    }
                    b.entries.Add(be);
                }
                foreach (var d in bi.DepBundleNames)
                {
                    if (inputs.TryGetValue(d, out var depBi) && !string.IsNullOrEmpty(depBi.BinaryHash))
                    {
                        b.dependency_hashes.Add(depBi.BinaryHash);
                    }
                }
                r.bundles.Add(b);
            }
            return r;
        }

        public string WriteJson(string outputDir)
        {
            var json = JsonUtility.ToJson(this, prettyPrint: true);
            var path = Path.Combine(outputDir, "roulin-build-report.json");
            File.WriteAllText(path, json);
            return path;
        }

        // verbose=true adds a per-bundle table (size / hash / entry counts).
        public void LogSummary(bool verbose)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("─── Roulin Parcel Build summary ───────────────────────────────────");
            sb.AppendLine($"  server   : {server}");
            sb.AppendLine($"  revision : {revision}");
            sb.AppendLine($"  bundles  : {bundle_count}  (total {RoulinUtil.FormatBytes(TotalBytes)})");
            sb.AppendLine($"  entries  : {entry_count}");
            if (verbose)
            {
                sb.AppendLine();
                sb.AppendLine("  Bundle                            Size       Hash         Entries  Deps");
                sb.AppendLine("  --------------------------------  ---------  -----------  -------  ----");
                foreach (var b in bundles)
                {
                    var h12 = string.IsNullOrEmpty(b.binary_hash) ? "(missing)" : b.binary_hash[..12];
                    sb.AppendLine(
                        $"  {b.name,-32}  {RoulinUtil.FormatBytes(b.size_bytes),9}  {h12,-11}  " +
                        $"{b.entries.Count,7}  {b.dependency_hashes.Count,4}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("  Inspect deployed parcel:");
            sb.AppendLine(
                $"    roulin-cli inspect-parcel --base-url {server} --revision {revision}");
            sb.AppendLine("────────────────────────────────────────────────────────────────────");
            Debug.Log(sb.ToString());
        }
    }

    [Serializable]
    public class BuildReportBundle
    {
        public string name;
        public string binary_hash;
        public long size_bytes;
        public List<BuildReportEntry> entries = new();
        public List<string> dependency_hashes = new();
    }

    [Serializable]
    public class BuildReportEntry
    {
        public string address;
        public List<string> labels = new();
    }
}
