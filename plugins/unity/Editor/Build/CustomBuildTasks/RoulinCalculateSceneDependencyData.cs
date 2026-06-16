using Roulin.Editor.Build.Meta;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Scene-side counterpart of RoulinCalculateAssetDependencyData. Skips
    // SBP's per-scene dirty check + Sprite stale re-walk; restores SceneInfo
    // / SceneUsage / DependencyHash from blob_meta when RestorePayload is set.
    internal sealed class RoulinCalculateSceneDependencyData : IBuildTask
    {
        public RestorePayload RestorePayload { get; set; }

        // Per-ObjectIdentifier → Type[] (multi-Type) collected during Run.
        internal IReadOnlyDictionary<ObjectIdentifier, Type[]> CollectedObjectToType { get; private set; }
            = new Dictionary<ObjectIdentifier, Type[]>();

        public int Version => 1;

        public ReturnCode Run()
        {
            if (_content.Scenes == null || _content.Scenes.Count == 0)
            {
                return ReturnCode.SuccessNotRun;
            }

            var processed = 0;
            var restored = 0;
            long totalReferenced = 0;

            var target = _parameters.Target;
            var settings = _parameters.GetContentBuildSettings();
            var usageCache = _dependencyData.DependencyUsageCache;

            var typeEntries = new List<KeyValuePair<ObjectIdentifier, Type[]>>();
            var seenObjects = new HashSet<ObjectIdentifier>();

            // Apply scene restore. Surviving GUIDs skip the CBI walk below.
            HashSet<GUID> restoredGuids = null;
            if (RestorePayload != null)
            {
                restoredGuids = new RestoreBlobMetas().ApplyScenesToContext(
                    RestorePayload, _dependencyData,
                    currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                    currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);
                restored = restoredGuids.Count;

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

                // Fallback for old blob_metas with empty ObjectTypes.
                if (restoredGuids.Count > 0
                    && (RestorePayload.ObjectTypes == null || RestorePayload.ObjectTypes.Count == 0)
                    && RestorePayload.SceneByGuid != null)
                {
                    var missingObjs = new List<ObjectIdentifier>();
                    foreach (var guid in restoredGuids)
                    {
                        if (!RestorePayload.SceneByGuid.TryGetValue(guid, out var rs))
                        {
                            continue;
                        }

                        if (rs.Info.referencedObjects != null)
                        {
                            foreach (var o in rs.Info.referencedObjects)
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
                            "[RoulinSceneCAD] fallback: blob_meta ObjectTypes empty, " +
                            $"bulk-resolved {arr.Length} restored ObjectId(s) via CBI " +
                            "(remove block once blob_meta is regenerated from a cold build)");
                    }
                }
            }

            // CBI walk for any scene not covered by the restore.
            foreach (var sceneGuid in _content.Scenes)
            {
                if (restoredGuids != null && restoredGuids.Contains(sceneGuid))
                {
                    processed++;
                    continue;
                }

                var scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid.ToString());
                if (string.IsNullOrEmpty(scenePath))
                {
                    Debug.LogWarning($"[RoulinSceneCAD] scene GUID {sceneGuid} has no asset path; skipping");
                    continue;
                }

                var usageTags = new BuildUsageTagSet();
                var sceneInfo = ContentBuildInterface.CalculatePlayerDependenciesForScene(
                    scenePath, settings, usageTags, usageCache);

                _dependencyData.SceneInfo[sceneGuid] = sceneInfo;
                _dependencyData.SceneUsage[sceneGuid] = usageTags;
                _dependencyData.DependencyHash[sceneGuid] = AssetDatabase.GetAssetDependencyHash(scenePath);

                processed++;
                totalReferenced += sceneInfo.referencedObjects.Count;

                CollectTypes(sceneInfo.referencedObjects, seenObjects, typeEntries);
            }

            // Warm BuildCacheUtility for collected (objectId, Type[]) pairs.
            int typeCount;
            try
            {
                typeCount = SbpReflection.Instance.WarmTypeCache(typeEntries);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[RoulinSceneCAD] WarmTypeCache failed: " +
                    (ex.InnerException?.Message ?? ex.Message));
                typeCount = 0;
            }

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
                $"[RoulinSceneCAD] processed {processed} scene(s) " +
                $"(restored={restored}) " +
                $"avg referenced={(processed - restored > 0 ? totalReferenced / (double)(processed - restored) : 0):F1}, " +
                $"type cache populated for {typeCount} object(s), " +
                $"objectToTypes surfaced for {flat.Count} object(s)");

            return ReturnCode.Success;
        }

        // Same leak-into-next-build hazard as RoulinCalculateAssetDependencyData.ReleaseRetainedState.
        public void ReleaseRetainedState()
        {
            RestorePayload = null;
            CollectedObjectToType = new Dictionary<ObjectIdentifier, Type[]>();
        }

        private static void CollectTypes(
            ReadOnlyCollection<ObjectIdentifier> ids,
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

        [InjectContext()]
        private IDependencyData _dependencyData;
#pragma warning restore 649
    }
}