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
        private IAddressablesGroupsView _view;

        [InjectContext(ContextUsage.In)]
        private IBlobUploadResults _uploadResults;
#pragma warning restore 649

        public int Version => 1;

        public RoulinServerClient Server { get; set; }
        public string Revision { get; set; }

        // Set to the server's idea of the base revision (from GetDiff response)
        // to publish incrementally. Leave null/empty for a full publish.
        public string BaseRevision { get; set; }

        // Full list of bundle names that should exist in the new revision.
        // Required when BaseRevision is set. The server keeps base entries
        // whose names are in this list and drops the rest.
        public List<string> AllBundleNames { get; set; }

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
            if (incremental && (AllBundleNames == null || AllBundleNames.Count == 0))
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.AllBundleNames is required when BaseRevision is set");
            }

            var catalog = BuildCatalog();
            var parcel = catalog.ToParcel();
            if (incremental)
            {
                parcel.base_revision = BaseRevision;
                parcel.all_bundle_names = AllBundleNames;
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
        private RoulinCatalog BuildCatalog()
        {
            var depClosure = RoulinBundleDepClosure.Compute(
                _writeData.FileToBundle,
                _writeData.AssetToFiles);

            var catalog = new RoulinCatalog();
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
            return catalog;
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
