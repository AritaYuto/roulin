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
    // Assembles the RoulinCatalog and POSTs the parcel.
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

        // Constructed by RoulinBuildScript, filled here, read by BuildReport.
        [InjectContext(ContextUsage.In)]
        private RoulinCatalog _catalog;
#pragma warning restore 649

        public int Version => 1;

        // Hardcoded by SBP's DefaultBuildTasks; stable across the versions we target.
        private const string SbpMonoScriptsBundleName    = "UnityMonoScripts.bundle";
        private const string SbpBuiltInShadersBundleName = "UnityBuiltInShaders.bundle";

        public RoulinServerClient Server { get; set; }
        public string Revision { get; set; }

        // Base revision from GetDiff. Null/empty → full publish.
        public string BaseRevision { get; set; }

        // Bundle names in the base revision's Index.
        // Carries SBP-generated names across incrementals so the server merge doesn't drop them.
        public IReadOnlyList<string> BaseBundleNames { get; set; }

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
                // Names that should exist in the new revision. Server drops any base
                // entry not listed here, so include SBP-generated bundles too.
                var allNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bundleBuild in _view.BundleBuilds)
                {
                    allNames.Add(bundleBuild.assetBundleName);
                }
                foreach (var (name, _) in _sbpResults.BundleInfos)
                {
                    // Build-time proxy; never ship.
                    if (name == RoulinUnityBuiltIns.BundleName)
                    {
                        continue;
                    }
                    allNames.Add(name);
                }
                // Keep the SBP-generated names alive whenever base has them so
                // other bundles' Deps to them don't dangle after merge.
                if (BaseBundleNames != null)
                {
                    var baseNameSet = new HashSet<string>(BaseBundleNames, StringComparer.Ordinal);
                    if (baseNameSet.Contains(SbpMonoScriptsBundleName))
                    {
                        allNames.Add(SbpMonoScriptsBundleName);
                    }
                    if (baseNameSet.Contains(SbpBuiltInShadersBundleName))
                    {
                        allNames.Add(SbpBuiltInShadersBundleName);
                    }
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

        // SBP bundle set + upload results + view entries + dep closure → catalog.
        private void PopulateCatalog(RoulinCatalog catalog)
        {
            var depClosure = RoulinBundleDepClosure.Compute(
                _writeData.FileToBundle,
                _writeData.AssetToFiles);

            foreach (var kv in _sbpResults.BundleInfos)
            {
                var name = kv.Key;
                // Build-time proxy; RoulinPublishBlobs skipped it, no upload result exists.
                if (name == RoulinUnityBuiltIns.BundleName)
                {
                    continue;
                }
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

    // Per-bundle dep closure (immediate + expanded) from SBP's IBundleWriteData.
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
