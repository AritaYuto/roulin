using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    internal sealed class RoulinGenerateLocationLists : RoulinBuildTaskBase
    {
        public override int Version => 1;

        public override ReturnCode Run()
        {
            var bundleToAssetGroup = roulinContext.BundleToAssetGroup;
            var aas = roulinContext.Aas;
            var inputs = roulinContext.BundleInputs;

            // SBP-synthesised bundles have no source group; map them and add a
            // BundleInput stub so upload / parcel iterate every SBP bundle.
            var fallbackGroupGuid = aas.groups
                .FirstOrDefault(g => g != null && g.HasSchema<BundledAssetGroupSchema>())
                ?.Guid;
            var sbpOnlyBundles = 0;
            foreach (var kv in _sbpResults.BundleInfos)
            {
                if (!bundleToAssetGroup.ContainsKey(kv.Key) && fallbackGroupGuid != null)
                {
                    bundleToAssetGroup[kv.Key] = fallbackGroupGuid;
                }

                if (!inputs.ContainsKey(kv.Key))
                {
                    inputs[kv.Key] = new BundleInput { Name = kv.Key };
                    sbpOnlyBundles++;
                }
            }

            if (_sbpResults.BundleInfos.Count != inputs.Count)
            {
                throw new Exception(
                    "[RoulinGenerateLocationLists] sbp/inputs mismatch: SBP produced " +
                    $"{_sbpResults.BundleInfos.Count} bundles but inputs has {inputs.Count} " +
                    "after synthesis — bundle drop detected, fix the build pipeline");
            }

            var closure = RoulinBundleDepClosure.Compute(
                _writeData.FileToBundle,
                _writeData.AssetToFiles);

            var closureEdges = 0;
            foreach (var kv in inputs)
            {
                var bi = kv.Value;
                if (closure.Immediate.TryGetValue(bi.Name, out var imm))
                {
                    bi.DepBundleNames.AddRange(imm);
                }

                if (closure.Expanded.TryGetValue(bi.Name, out var exp))
                {
                    bi.DepBundleNames.AddRange(exp);
                }

                closureEdges += bi.DepBundleNames.Count;
            }

            Debug.Log(
                $"[RoulinGenerateLocationLists] sbp-only synthesized={sbpOnlyBundles}, " +
                $"dep closure resolved: {closureEdges} edge(s) across {inputs.Count} bundle(s)");

            return ReturnCode.Success;
        }

#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        private IBundleWriteData _writeData;

        [InjectContext(ContextUsage.In, true)]
        private IDependencyData _dependencyData;

        [InjectContext(ContextUsage.In)]
        private IBundleBuildResults _sbpResults;
#pragma warning restore 649
    }

    public static class RoulinBundleDepClosure
    {
        public static Result Compute(
            Dictionary<string, string> fileToBundle,
            Dictionary<GUID, List<string>> assetToFiles)
        {
            var bundleToEntry = new Dictionary<string, BundleEntry>();

            // Cover every bundle reachable from any asset's file list.
            foreach (var files in assetToFiles.Values)
            foreach (var f in files)
            {
                GetOrCreate(bundleToEntry, fileToBundle[f]);
            }

            // Immediate deps: from each asset's primary bundle to every bundle
            // in that asset's file list (self included).
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

        // Visited includes start node (matches ProcessInput.ExpandDependencies).
        // Expanded output subtracts Immediate so self appears only via a cycle.
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
                    if (!visited.Contains(dep))
                    {
                        queue.Enqueue(dep);
                    }
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