using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace Roulin.Editor.Build.Meta
{
    public sealed class RestoreBlobMetas
    {
        // Batch decode RoulinBlobMeta envelopes into a dependency-data payload.
        // Non-Unity bodies and unresolved Type strings are dropped silently;
        // Scriptable Build Pipeline tolerates missing type info via cache miss (only warm speedup lost).
        public RestorePayload Decode(IEnumerable<RoulinBlobMeta> blobMetas)
        {
            var assetByGuid = new Dictionary<GUID, RestoredAsset>();
            var sceneByGuid = new Dictionary<GUID, RestoredScene>();
            var objectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>();
            var seenObjects = new HashSet<ObjectIdentifier>();

            if (blobMetas == null)
            {
                return new RestorePayload
                {
                    AssetByGuid = assetByGuid,
                    SceneByGuid = sceneByGuid,
                    ObjectTypes = objectTypes
                };
            }

            foreach (var blobMeta in blobMetas)
            {
                if (blobMeta == null)
                {
                    continue;
                }

                if (!string.Equals(blobMeta.body_type, "unity", StringComparison.Ordinal))
                {
                    Debug.LogWarning(
                        $"[RestoreBlobMetas] skipping non-unity body (type={blobMeta.body_type})");
                    continue;
                }

                var unity = blobMeta.unity_body;
                if (unity == null)
                {
                    continue;
                }

                Debug.Log(
                    $"[RestoreBlobMetas] decoded blob_hash={blobMeta.blob_hash?.Substring(0, Math.Min(12, blobMeta.blob_hash?.Length ?? 0))}…: " +
                    $"types={unity.types.Count} assets={unity.assets.Count} scenes={unity.scenes.Count}");

                // Resolve interned types[] once per blob; reused via type_idxs.
                var resolvedTypes = new Type[unity.types.Count];
                for (var i = 0; i < unity.types.Count; i++)
                {
                    var aqn = unity.types[i];
                    resolvedTypes[i] = string.IsNullOrEmpty(aqn)
                        ? null
                        : Type.GetType(aqn, false);
                }

                foreach (var assetDto in unity.assets)
                {
                    var restored = FromDto(assetDto);
                    var guid = restored.LoadInfo.asset;
                    if (!assetByGuid.ContainsKey(guid))
                    {
                        assetByGuid[guid] = restored;
                    }

                    AccumulateObjectTypes(assetDto.included_objects, resolvedTypes, objectTypes, seenObjects);
                    AccumulateObjectTypes(assetDto.referenced_objects, resolvedTypes, objectTypes, seenObjects);
                    AccumulateObjectTypes(assetDto.representations, resolvedTypes, objectTypes, seenObjects);
                }

                foreach (var sceneDto in unity.scenes)
                {
                    var restored = SceneFromDto(sceneDto, resolvedTypes);
                    if (!sceneByGuid.ContainsKey(restored.SceneGuid))
                    {
                        sceneByGuid[restored.SceneGuid] = restored;
                    }

                    AccumulateObjectTypes(sceneDto.referenced_objects, resolvedTypes, objectTypes, seenObjects);
                }
            }

            return new RestorePayload
            {
                AssetByGuid = assetByGuid,
                SceneByGuid = sceneByGuid,
                ObjectTypes = objectTypes
            };
        }

        // Writes every payload entry directly into the Scriptable Build Pipeline context. The
        // caller is responsible for pre-filtering payload to only the entries
        // it wants restored (e.g. remove VCS-changed GUIDs to force ContentBuildInterface walk
        // for those). Returns the set of GUIDs actually written.
        public HashSet<GUID> ApplyToContext(
            RestorePayload payload,
            IDependencyData dependencyData,
            IBuildExtendedAssetData extendedAssetData)
        {
            var applied = new HashSet<GUID>();
            if (payload?.AssetByGuid == null)
            {
                return applied;
            }
            if (dependencyData == null)
            {
                throw new ArgumentNullException(nameof(dependencyData));
            }

            foreach (var kv in payload.AssetByGuid)
            {
                var restored = kv.Value;
                dependencyData.AssetInfo[kv.Key] = restored.LoadInfo;
                dependencyData.AssetUsage[kv.Key] = restored.Usage;
                if (extendedAssetData != null && restored.Extended != null)
                {
                    extendedAssetData.ExtendedData[kv.Key] = restored.Extended;
                }
                applied.Add(kv.Key);
            }
            return applied;
        }

        // Scene-side counterpart. Same pre-filter responsibility on the caller.
        public HashSet<GUID> ApplyScenesToContext(
            RestorePayload payload,
            IDependencyData dependencyData)
        {
            var applied = new HashSet<GUID>();
            if (payload?.SceneByGuid == null)
            {
                return applied;
            }
            if (dependencyData == null)
            {
                throw new ArgumentNullException(nameof(dependencyData));
            }

            foreach (var kv in payload.SceneByGuid)
            {
                var restored = kv.Value;
                dependencyData.SceneInfo[kv.Key] = restored.Info;
                dependencyData.SceneUsage[kv.Key] = restored.Usage;
                dependencyData.DependencyHash[kv.Key] = restored.PrefabDependencyHash;
                applied.Add(kv.Key);
            }
            return applied;
        }

        // One asset DTO → dependency data. Caller resolves RoulinUnityBlob.types
        // separately for the type-cache warm pass; this only fills the asset shape.
        public RestoredAsset FromDto(RoulinUnityAsset dto)
        {
            var included = new List<ObjectIdentifier>(dto.included_objects.Count);
            var referenced = new List<ObjectIdentifier>(dto.referenced_objects.Count);
            var reps = new List<ObjectIdentifier>(dto.representations.Count);
            foreach (var o in dto.included_objects)
            {
                included.Add(FromDto(o));
            }

            foreach (var o in dto.referenced_objects)
            {
                referenced.Add(FromDto(o));
            }

            foreach (var o in dto.representations)
            {
                reps.Add(FromDto(o));
            }

            var info = new AssetLoadInfo
            {
                asset = new GUID(dto.guid),
                address = dto.asset_address,
                includedObjects = included,
                referencedObjects = referenced
            };

            var usage = new BuildUsageTagSet();
            if (!string.IsNullOrEmpty(dto.build_usage_tag_set))
            {
                var raw = Convert.FromBase64String(dto.build_usage_tag_set);
                if (raw.Length > 0)
                {
                    SbpReflection.Instance.DeserializeBuildUsageTagSet(usage, raw);
                }
            }

            var ext = reps.Count == 0
                ? null
                : new ExtendedAssetData { Representations = reps };

            return new RestoredAsset
            {
                LoadInfo = info,
                Usage = usage,
                Extended = ext,
            };
        }

        // Scene-side counterpart of FromDto. resolvedTypes is the caller's
        // pre-resolved RoulinUnityBlob.types table.
        public RestoredScene SceneFromDto(RoulinUnityScene dto, Type[] resolvedTypes)
        {
            var refs = new ObjectIdentifier[dto.referenced_objects.Count];
            for (var i = 0; i < dto.referenced_objects.Count; i++)
            {
                refs[i] = FromDto(dto.referenced_objects[i]);
            }

            var includedTypes = new Type[dto.included_type_idxs.Count];
            for (var i = 0; i < dto.included_type_idxs.Count; i++)
            {
                var idx = dto.included_type_idxs[i];
                includedTypes[i] = idx >= 0 && idx < resolvedTypes.Length ? resolvedTypes[idx] : null;
            }

            var uintMap = new Dictionary<string, uint>(StringComparer.Ordinal);
            var boolMap = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var f in dto.global_usage.uint_fields)
            {
                if (!string.IsNullOrEmpty(f.name))
                {
                    uintMap[f.name] = f.value;
                }
            }

            foreach (var f in dto.global_usage.bool_fields)
            {
                if (!string.IsNullOrEmpty(f.name))
                {
                    boolMap[f.name] = f.value;
                }
            }

            var globalUsage = SbpReflection.Instance.MakeBuildUsageTagGlobal(uintMap, boolMap);

            var sceneInfo = SbpReflection.Instance.MakeSceneDependencyInfo(
                dto.scene_path,
                refs,
                includedTypes,
                globalUsage);

            var usage = new BuildUsageTagSet();
            if (!string.IsNullOrEmpty(dto.build_usage_tag_set))
            {
                var raw = Convert.FromBase64String(dto.build_usage_tag_set);
                if (raw.Length > 0)
                {
                    SbpReflection.Instance.DeserializeBuildUsageTagSet(usage, raw);
                }
            }

            var prefabDepHash = string.IsNullOrEmpty(dto.prefab_dependency_hash)
                ? default
                : Hash128.Parse(dto.prefab_dependency_hash);

            return new RestoredScene
            {
                SceneGuid = new GUID(dto.guid),
                Info = sceneInfo,
                Usage = usage,
                PrefabDependencyHash = prefabDepHash,
            };
        }

        public ObjectIdentifier FromDto(RoulinUnityObjectId dto)
        {
            return SbpReflection.Instance.MakeObjectIdentifier(
                new GUID(dto.guid),
                dto.local_identifier_in_file,
                (FileType)dto.file_type,
                dto.file_path);
        }

        private void AccumulateObjectTypes(
            List<RoulinUnityObjectId> objectDtos,
            Type[] resolvedTypes,
            List<KeyValuePair<ObjectIdentifier, Type[]>> output,
            HashSet<ObjectIdentifier> seen)
        {
            foreach (var dto in objectDtos)
            {
                if (dto.type_idxs == null || dto.type_idxs.Count == 0)
                {
                    continue;
                }

                var collected = new List<Type>(dto.type_idxs.Count);
                foreach (var idx in dto.type_idxs)
                {
                    if (idx < 0 || idx >= resolvedTypes.Length)
                    {
                        continue;
                    }

                    var t = resolvedTypes[idx];
                    if (t == null)
                    {
                        continue;
                    }

                    collected.Add(t);
                }

                if (collected.Count == 0)
                {
                    continue;
                }

                var oi = FromDto(dto);
                if (!seen.Add(oi))
                {
                    continue;
                }

                output.Add(new KeyValuePair<ObjectIdentifier, Type[]>(oi, collected.ToArray()));
            }
        }
    }

    // Decoded RoulinBlobMeta batch ready to inject into Scriptable Build Pipeline.
    public sealed class RestorePayload
    {
        public Dictionary<GUID, RestoredAsset> AssetByGuid;
        public List<KeyValuePair<ObjectIdentifier, Type[]>> ObjectTypes;
        public Dictionary<GUID, RestoredScene> SceneByGuid;
    }

    public struct RestoredAsset
    {
        public AssetLoadInfo LoadInfo;
        public BuildUsageTagSet Usage;
        public ExtendedAssetData Extended;
    }

    public struct RestoredScene
    {
        public GUID SceneGuid;
        public SceneDependencyInfo Info;
        public BuildUsageTagSet Usage;
        public Hash128 PrefabDependencyHash;
    }
}
