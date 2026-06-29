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
    //
    // Built from the RoulinCatalog that RoulinPublishParcel assembled — the
    // catalog already merges Addressables-side entry data, blob upload
    // results, and SBP dep closure, so the report is just a projection.
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

        // Total addressable entry count across all reported bundles.
        public int LocationCount => entry_count;

        public static BuildReport Compose(
            string server,
            string revision,
            RoulinCatalog catalog)
        {
            var r = new BuildReport
            {
                server = server,
                revision = revision,
                generated_utc = DateTime.UtcNow.ToString("o"),
            };
            foreach (var entry in catalog.Bundles)
            {
                var b = new BuildReportBundle
                {
                    name = entry.Name,
                    binary_hash = entry.BlobHash,
                    size_bytes = entry.SizeBytes
                };
                foreach (var addr in entry.Addresses)
                {
                    var be = new BuildReportEntry { address = addr.Address };
                    if (addr.Labels != null)
                    {
                        be.labels = new List<string>(addr.Labels);
                    }
                    b.entries.Add(be);
                }
                // Resolve dep bundle names to hex blob hashes via the catalog
                // so the on-disk report stays stable across renames.
                foreach (var depName in entry.DepBundleNames)
                {
                    var depEntry = catalog.Get(depName);
                    if (depEntry != null && !string.IsNullOrEmpty(depEntry.BlobHash))
                    {
                        b.dependency_hashes.Add(depEntry.BlobHash);
                    }
                }
                r.entry_count += b.entries.Count;
                r.bundles.Add(b);
            }
            r.bundle_count = r.bundles.Count;
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
