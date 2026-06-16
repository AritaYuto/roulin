using Roulin.Editor.Build.Meta;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Drop-in for SBP CalculateAssetDependencyData. Two modes:
    //   passthrough: runs CBI without the dirty-check walk.
    //   restore:     when RestorePayload is set, applies stored dependency data up-front
    //                and skips ContentBuildInterface for restored assets.
    internal sealed class RoulinCalculateAssetDependencyData : IBuildTask
    {
        // Pre-decoded dependency data from previous-build blob_metas. Optional.
        public RestorePayload RestorePayload { get; set; }

        // Per-ObjectIdentifier → Type[] (multi-Type) collected during Run.
        // Mirrors BuildCacheUtility.m_ObjectToType shape for blob_meta stamping.
        internal IReadOnlyDictionary<ObjectIdentifier, Type[]> CollectedObjectToType { get; private set; }
            = new Dictionary<ObjectIdentifier, Type[]>();

        public int Version => 1;

        public ReturnCode Run()
        {
            // Match SBP lazy-create pattern; InOut injection writes back.
            if (_extendedAssetData == null)
            {
                _extendedAssetData = new BuildExtendedAssetData();
            }

            var processed = 0;
            var restored = 0;
            var withNoObjs = 0;
            var withRepresentations = 0;
            long totalIncluded = 0;
            long totalReferenced = 0;

            var target = _parameters.Target;
            var typeDb = _parameters.ScriptInfo;
            var globalUsage = _dependencyData.GlobalUsage;
            var usageCache = new BuildUsageCache();

            var typeEntries = new List<KeyValuePair<ObjectIdentifier, Type[]>>();
            var seenObjects = new HashSet<ObjectIdentifier>();

            // Restrict restore to assets in _content.Assets. Entries outside
            // the build content trip UpdateBundleObjectLayout's KeyNotFoundException.
            HashSet<GUID> restoredGuids = null;
            if (RestorePayload != null)
            {
                var contentSet = new HashSet<GUID>(_content.Assets);
                var filtered = new RestorePayload
                {
                    AssetByGuid = new Dictionary<GUID, RestoredAsset>(),
                    ObjectTypes = RestorePayload.ObjectTypes
                };
                foreach (var kv in RestorePayload.AssetByGuid)
                {
                    if (contentSet.Contains(kv.Key))
                    {
                        filtered.AssetByGuid[kv.Key] = kv.Value;
                    }
                }

                // Apply blob_meta restore. Surviving GUIDs skip the CBI walk
                // below; rejected ones fall through and get fresh data.
                restoredGuids = new RestoreBlobMetas().ApplyToContext(
                    filtered, _dependencyData, _extendedAssetData,
                    currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                    currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);
                restored = restoredGuids.Count;

                // Pre-seed type cache from restored entries.
                if (RestorePayload.ObjectTypes != null)
                {
                    foreach (var kv in RestorePayload.ObjectTypes)
                    {
                        if (seenObjects.Add(kv.Key))
                        {
                            typeEntries.Add(kv);
                        }
                    }
                }

                // Fallback for old blob_metas with empty ObjectTypes. One bulk
                // GetTypeForObjects beats per-object CBI by ~100x downstream.
                if (restoredGuids.Count > 0
                    && (RestorePayload.ObjectTypes == null || RestorePayload.ObjectTypes.Count == 0))
                {
                    var missingObjs = new List<ObjectIdentifier>();
                    foreach (var guid in restoredGuids)
                    {
                        if (!filtered.AssetByGuid.TryGetValue(guid, out var ra))
                        {
                            continue;
                        }

                        if (ra.LoadInfo.includedObjects != null)
                        {
                            foreach (var o in ra.LoadInfo.includedObjects)
                            {
                                if (seenObjects.Add(o))
                                {
                                    missingObjs.Add(o);
                                }
                            }
                        }

                        if (ra.LoadInfo.referencedObjects != null)
                        {
                            foreach (var o in ra.LoadInfo.referencedObjects)
                            {
                                if (seenObjects.Add(o))
                                {
                                    missingObjs.Add(o);
                                }
                            }
                        }

                        if (ra.Extended?.Representations != null)
                        {
                            foreach (var o in ra.Extended.Representations)
                            {
                                if (seenObjects.Add(o))
                                {
                                    missingObjs.Add(o);
                                }
                            }
                        }
                    }

                    if (missingObjs.Count > 0)
                    {
                        var arr = missingObjs.ToArray();
                        var types = ContentBuildInterface.GetTypeForObjects(arr);
                        for (var i = 0; i < arr.Length; i++)
                        {
                            typeEntries.Add(new KeyValuePair<ObjectIdentifier, Type[]>(arr[i], new[] { types[i] }));
                        }

                        Debug.LogWarning(
                            "[RoulinCAD] fallback: blob_meta ObjectTypes empty, " +
                            $"bulk-resolved {arr.Length} restored ObjectId(s) via CBI " +
                            "(remove block once blob_meta is regenerated from a cold build)");
                    }
                }
            }

            // CBI walk for any asset not covered by the restore.
            foreach (var assetGuid in _content.Assets)
            {
                if (restoredGuids != null && restoredGuids.Contains(assetGuid))
                {
                    processed++;
                    continue;
                }

                var includedObjects = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(assetGuid, target);
                if (includedObjects == null || includedObjects.Length == 0)
                {
                    withNoObjs++;
                    _dependencyData.AssetInfo[assetGuid] = new AssetLoadInfo
                    {
                        asset = assetGuid,
                        address = AssetDatabase.GUIDToAssetPath(assetGuid.ToString()),
                        includedObjects = new List<ObjectIdentifier>(),
                        referencedObjects = new List<ObjectIdentifier>()
                    };
                    _dependencyData.AssetUsage[assetGuid] = new BuildUsageTagSet();
                    processed++;
                    continue;
                }

                var referencedObjects = ContentBuildInterface.GetPlayerDependenciesForObjects(
                    includedObjects, target, typeDb);

                var usageTagSet = new BuildUsageTagSet();
                ContentBuildInterface.CalculateBuildUsageTags(
                    referencedObjects, includedObjects, globalUsage, usageTagSet, usageCache);

                _dependencyData.AssetInfo[assetGuid] = new AssetLoadInfo
                {
                    asset = assetGuid,
                    address = AssetDatabase.GUIDToAssetPath(assetGuid.ToString()),
                    includedObjects = new List<ObjectIdentifier>(includedObjects),
                    referencedObjects = new List<ObjectIdentifier>(referencedObjects)
                };
                _dependencyData.AssetUsage[assetGuid] = usageTagSet;

                // Mirror SBP GatherAssetRepresentations: drop editor-only objects
                // and the index-0 main asset; otherwise GenerateSubAssetPathMaps
                // does IndexOf=-1 → Swap OOR.
                var reps = ContentBuildInterface.GetPlayerAssetRepresentations(assetGuid, target);
                if (reps != null && reps.Length > 0)
                {
                    var includedSet = new HashSet<ObjectIdentifier>(includedObjects);
                    var filtered = new List<ObjectIdentifier>(reps.Length);
                    foreach (var r in reps)
                    {
                        if (includedSet.Contains(r))
                        {
                            filtered.Add(r);
                        }
                    }

                    if (filtered.Count >= 2)
                    {
                        _extendedAssetData.ExtendedData[assetGuid] = new ExtendedAssetData
                        {
                            Representations = filtered.GetRange(1, filtered.Count - 1)
                        };
                        withRepresentations++;
                    }
                }

                processed++;
                totalIncluded += includedObjects.Length;
                totalReferenced += referencedObjects.Length;

                CollectTypes(includedObjects, seenObjects, typeEntries);
                CollectTypes(referencedObjects, seenObjects, typeEntries);
            }

            // Warm BuildCacheUtility.m_ObjectToType with restored + freshly discovered entries.
            int typeCount;
            try
            {
                typeCount = SbpReflection.Instance.WarmTypeCache(typeEntries);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[RoulinCAD] WarmTypeCache failed: " +
                    (ex.InnerException?.Message ?? ex.Message) +
                    " (downstream cache will miss; rebuild times will be high)");
                typeCount = 0;
            }

            // Surface full Type[] (not just first) so cold/warm WSF cache keys match.
            var flat = new Dictionary<ObjectIdentifier, Type[]>(typeEntries.Count);
            foreach (var kv in typeEntries)
            {
                if (kv.Value != null && kv.Value.Length > 0)
                {
                    flat[kv.Key] = kv.Value;
                }
            }

            CollectedObjectToType = flat;

            Debug.Log(
                $"[RoulinCAD] processed {processed} asset(s) " +
                $"(restored={restored}, no-objs={withNoObjs}, sub-asset reps={withRepresentations}) " +
                $"avg included={(processed - restored > 0 ? totalIncluded / (double)(processed - restored) : 0):F1}, " +
                $"avg referenced={(processed - restored > 0 ? totalReferenced / (double)(processed - restored) : 0):F1}, " +
                $"type cache populated for {typeCount} object(s), " +
                $"objectToTypes surfaced for {flat.Count} object(s)");

            return ReturnCode.Success;
        }

        // Explicitly release heavy state; SBP / Addressables can retain the
        // task list in a static cache and leak this into the next build.
        public void ReleaseRetainedState()
        {
            RestorePayload = null;
            CollectedObjectToType = new Dictionary<ObjectIdentifier, Type[]>();
        }

        private static void CollectTypes(
            ObjectIdentifier[] ids,
            HashSet<ObjectIdentifier> seen,
            List<KeyValuePair<ObjectIdentifier, Type[]>> entries)
        {
            foreach (var id in ids)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                var types = ContentBuildInterface.GetTypeForObjects(new[] { id });
                entries.Add(new KeyValuePair<ObjectIdentifier, Type[]>(id, types));
            }
        }

#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        private IBuildParameters _parameters;

        [InjectContext(ContextUsage.In)]
        private IBuildContent _content;

        [InjectContext]
        private IDependencyData _dependencyData;

        [InjectContext(ContextUsage.InOut, true)]
        private IBuildExtendedAssetData _extendedAssetData;
#pragma warning restore 649
    }
}