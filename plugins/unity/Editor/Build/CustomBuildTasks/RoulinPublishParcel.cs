using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Builds the RoulinCatalog from the read-only inputs that earlier tasks
    // (and the View) produced, serialises to the wire Parcel, and POSTs it.
    // This is the only task that knows about the catalog as a coherent
    // structure — the rest just contribute slices of data.
    internal sealed class RoulinPublishParcel : IBuildTask
    {
#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        private IBundleBuildResults _sbpResults;

        [InjectContext(ContextUsage.In)]
        private IBundleWriteData _writeData;

        [InjectContext(ContextUsage.In)]
        private AddressablesGroupsView _view;

        [InjectContext(ContextUsage.In)]
        private IBlobUploadResults _uploadResults;

        // Output: catalog the task assembles. The instance is constructed by
        // RoulinBuildScript and passed in as a SBP context object; this task
        // fills it. Lets BuildReport (running outside the pipeline) read the
        // same data without recomputing dep closure.
        [InjectContext(ContextUsage.In)]
        private RoulinCatalog _catalog;
#pragma warning restore 649

        public int Version => 1;

        public RoulinServerClient Server { get; set; }
        public string Revision { get; set; }

        // Set to the server's idea of the base revision (from GetDiff response)
        // to publish incrementally. Leave null/empty for a full publish.
        public string BaseRevision { get; set; }

        public ReturnCode Run()
        {
            if (Server == null)
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.Server is null — set before adding to task list");
            }
            if (string.IsNullOrEmpty(Revision))
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.Revision is null/empty — set before adding to task list");
            }

            var incremental = !string.IsNullOrEmpty(BaseRevision);

            PopulateCatalog(_catalog);
            var parcel = _catalog.ToParcel();
            if (incremental)
            {
                parcel.base_revision = BaseRevision;
                // The set of bundle names that should exist in the new revision:
                // every bundle the Addressables walk produced + every bundle SBP
                // synthesised (UnityBuiltInShaders.bundle / UnityMonoScripts.bundle).
                // Server drops base entries not in this set, so omitting the
                // synthesised ones would lose them across revisions.
                var allNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in _view.BundleBuilds)
                {
                    allNames.Add(b.assetBundleName);
                }
                foreach (var kv in _sbpResults.BundleInfos)
                {
                    allNames.Add(kv.Key);
                }
                parcel.all_bundle_names = new List<string>(allNames);
            }

            EditorUtility.DisplayProgressBar(
                "Roulin Build", $"POST /parcels/{Revision}…", 0.95f);
            Debug.Log(
                $"[RoulinPublishParcel] POST /parcels/{Revision} " +
                $"mode={(incremental ? "incremental" : "full")} " +
                $"delta={parcel.bundles.Count} " +
                $"all_names={(parcel.all_bundle_names?.Count ?? 0)}");
            Task.Run(async () => await Server.PostParcel(Revision, parcel)).GetAwaiter().GetResult();

            return ReturnCode.Success;
        }

        // Assemble the in-memory RoulinCatalog from the SBP-built bundle set
        // + per-bundle blob upload result + Addressables-side entries +
        // SBP-derived dep closure.
        private void PopulateCatalog(RoulinCatalog catalog)
        {
            var depClosure = RoulinBundleDepClosure.Compute(
                _writeData.FileToBundle,
                _writeData.AssetToFiles);

            foreach (var kv in _sbpResults.BundleInfos)
            {
                var name = kv.Key;
                if (!_uploadResults.TryGet(name, out var hash, out var size))
                {
                    throw new InvalidOperationException(
                        $"bundle '{name}' has no upload result — RoulinPublishBlobs " +
                        "did not run for it");
                }

                var entry = new RoulinCatalog.Entry
                {
                    Name = name,
                    BlobHash = hash,
                    SizeBytes = size,
                };
                entry.Addresses.AddRange(_view.GetEntries(name));

                if (depClosure.Immediate.TryGetValue(name, out var imm))
                {
                    entry.DepBundleNames.AddRange(imm);
                }
                if (depClosure.Expanded.TryGetValue(name, out var exp))
                {
                    entry.DepBundleNames.AddRange(exp);
                }

                catalog.Add(entry);
            }
            Debug.Log(
                $"[RoulinPublishParcel] catalog assembled: {catalog.Count} entries, " +
                $"dep edges across closure");
        }
    }

    // Dep closure derived from SBP's IBundleWriteData. Computes per-bundle
    // (immediate + expanded) dep bundle names. Pure transform — no SBP state
    // mutation, no global side effects.
    internal static class RoulinBundleDepClosure
    {
        public static Result Compute(
            Dictionary<string, string> fileToBundle,
            Dictionary<GUID, List<string>> assetToFiles)
        {
            var bundleToEntry = new Dictionary<string, BundleEntry>();

            foreach (var files in assetToFiles.Values)
            foreach (var f in files)
            {
                GetOrCreate(bundleToEntry, fileToBundle[f]);
            }

            foreach (var kv in assetToFiles)
            {
                var files = kv.Value;
                var primary = bundleToEntry[fileToBundle[files[0]]];
                foreach (var f in files)
                {
                    primary.Dependencies.Add(bundleToEntry[fileToBundle[f]]);
                }
            }

            foreach (var e in bundleToEntry.Values)
            {
                e.ExpandedDependencies = Bfs(e);
            }

            var immediate = bundleToEntry.Values.ToDictionary(
                x => x.BundleName,
                x => x.Dependencies.Select(d => d.BundleName).ToList());
            var expanded = bundleToEntry.Values.ToDictionary(
                x => x.BundleName,
                x => x.ExpandedDependencies
                    .Where(d => !x.Dependencies.Contains(d))
                    .Select(d => d.BundleName).ToList());

            return new Result(immediate, expanded);
        }

        private static BundleEntry GetOrCreate(Dictionary<string, BundleEntry> map, string name)
        {
            if (!map.TryGetValue(name, out var e))
            {
                map.Add(name, e = new BundleEntry { BundleName = name });
            }
            return e;
        }

        private static HashSet<BundleEntry> Bfs(BundleEntry start)
        {
            var visited = new HashSet<BundleEntry>();
            var queue = new Queue<BundleEntry>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                visited.Add(cur);
                foreach (var dep in cur.Dependencies)
                {
                    if (!visited.Contains(dep)) queue.Enqueue(dep);
                }
            }
            return visited;
        }

        public readonly struct Result
        {
            public readonly Dictionary<string, List<string>> Immediate;
            public readonly Dictionary<string, List<string>> Expanded;

            public Result(
                Dictionary<string, List<string>> immediate,
                Dictionary<string, List<string>> expanded)
            {
                Immediate = immediate;
                Expanded = expanded;
            }
        }

        private sealed class BundleEntry
        {
            public readonly HashSet<BundleEntry> Dependencies = new();
            public string BundleName;
            public HashSet<BundleEntry> ExpandedDependencies;
        }
    }
}
