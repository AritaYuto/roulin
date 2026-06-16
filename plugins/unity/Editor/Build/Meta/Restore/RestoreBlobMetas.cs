using System;
using System.Collections.Generic;
using System.Text;
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
        // SBP tolerates missing type info via cache miss (only warm speedup lost).
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

        // Returns default(Hash128) for missing / deleted assets so vanished
        // assets are naturally flagged as drifted.
        public static Hash128 DefaultAssetHashLookup(GUID guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid.ToString());
            return string.IsNullOrEmpty(path)
                ? default
                : AssetDatabase.GetAssetDependencyHash(path);
        }

        // null = built-in / Library / Resources (runtime-resolved, skip).
        // empty = guid no longer resolves in the project (deleted), so the
        // drift check flags any restored ObjectId at this guid as stale.
        public static ObjectIdentifier[] DefaultObjectIdsLookup(GUID guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid.ToString());
            if (string.IsNullOrEmpty(path))
            {
                return Array.Empty<ObjectIdentifier>();
            }

            if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                && !path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return null;
            }

            return ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(
                guid, EditorUserBuildSettings.activeBuildTarget);
        }

        // Returns the GUIDs that survived the staleness screen; the caller
        // commits them and falls back to CBI for the rest. Either lookup
        // may be null to disable its corresponding check.
        public HashSet<GUID> ApplyToContext(
            RestorePayload payload,
            IDependencyData dependencyData,
            IBuildExtendedAssetData extendedAssetData,
            Func<GUID, Hash128> currentHashLookup = null,
            Func<GUID, ObjectIdentifier[]> currentObjectIdsLookup = null)
        {
            if (payload?.AssetByGuid == null)
            {
                return new HashSet<GUID>();
            }

            if (dependencyData == null)
            {
                throw new ArgumentNullException(nameof(dependencyData));
            }

            bool IsPayloadResident(GUID g) => payload.AssetByGuid.ContainsKey(g);

            var candidates = new List<RestoreCandidate>(payload.AssetByGuid.Count);
            foreach (var kv in payload.AssetByGuid)
            {
                candidates.Add(new RestoreCandidate(
                    selfGuid: kv.Key,
                    storedHash: kv.Value.AssetDependencyHash,
                    referencedObjects: kv.Value.LoadInfo.referencedObjects,
                    referencedAssetHashes: kv.Value.ReferencedAssetHashes));
            }

            var screener = BuildScreener(IsPayloadResident, currentHashLookup, currentObjectIdsLookup);
            var report = screener.Screen(candidates);

            foreach (var guid in report.Passed)
            {
                var restored = payload.AssetByGuid[guid];
                dependencyData.AssetInfo[guid] = restored.LoadInfo;
                dependencyData.AssetUsage[guid] = restored.Usage;
                if (extendedAssetData != null && restored.Extended != null)
                {
                    extendedAssetData.ExtendedData[guid] = restored.Extended;
                }
            }

            if (currentHashLookup != null || currentObjectIdsLookup != null)
            {
                LogScreenReport("asset", report, candidates.Count);
            }

            return report.Passed;
        }

        // Scene-side counterpart of ApplyToContext.
        public HashSet<GUID> ApplyScenesToContext(
            RestorePayload payload,
            IDependencyData dependencyData,
            Func<GUID, Hash128> currentHashLookup = null,
            Func<GUID, ObjectIdentifier[]> currentObjectIdsLookup = null)
        {
            if (payload?.SceneByGuid == null)
            {
                return new HashSet<GUID>();
            }

            if (dependencyData == null)
            {
                throw new ArgumentNullException(nameof(dependencyData));
            }

            bool IsPayloadResident(GUID g) => payload.SceneByGuid.ContainsKey(g);

            var candidates = new List<RestoreCandidate>(payload.SceneByGuid.Count);
            foreach (var kv in payload.SceneByGuid)
            {
                candidates.Add(new RestoreCandidate(
                    selfGuid: kv.Key,
                    storedHash: kv.Value.PrefabDependencyHash,
                    referencedObjects: kv.Value.Info.referencedObjects,
                    referencedAssetHashes: kv.Value.ReferencedAssetHashes));
            }

            var screener = BuildScreener(IsPayloadResident, currentHashLookup, currentObjectIdsLookup);
            var report = screener.Screen(candidates);

            foreach (var guid in report.Passed)
            {
                var restored = payload.SceneByGuid[guid];
                dependencyData.SceneInfo[guid] = restored.Info;
                dependencyData.SceneUsage[guid] = restored.Usage;
                dependencyData.DependencyHash[guid] = restored.PrefabDependencyHash;
            }

            if (currentHashLookup != null || currentObjectIdsLookup != null)
            {
                LogScreenReport("scene", report, candidates.Count);
            }

            return report.Passed;
        }

        private static RestoreScreener BuildScreener(
            Func<GUID, bool> isPayloadResident,
            Func<GUID, Hash128> currentHashLookup,
            Func<GUID, ObjectIdentifier[]> currentObjectIdsLookup)
        {
            var referenceChecks = new List<IStalenessCheck>
            {
                new PayloadDepDriftCheck(isPayloadResident),
            };
            if (currentObjectIdsLookup != null)
            {
                referenceChecks.Add(new OutOfPayloadObjectIdDriftCheck(
                    currentObjectIdsLookup, isPayloadResident, currentHashLookup));
            }

            IStalenessCheck selfCheck = currentHashLookup == null
                ? null
                : new OwnHashDriftCheck(currentHashLookup);

            return new RestoreScreener(selfCheck, referenceChecks);
        }

        private static void LogScreenReport(string kind, ScreenReport report, int total)
        {
            var sb = new StringBuilder();
            sb.Append("[RestoreBlobMetas] ").Append(kind).Append(" screen: total=").Append(total);
            foreach (var entry in report.RejectionByReason)
            {
                sb.Append(' ').Append(entry.Key).Append('=').Append(entry.Value);
            }
            sb.Append(" applied=").Append(report.Passed.Count);
            sb.Append(" objectIdsCacheSize=").Append(report.ObjectIdsCacheSize);
            Debug.Log(sb.ToString());
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

            var depHash = string.IsNullOrEmpty(dto.asset_dependency_hash)
                ? default
                : Hash128.Parse(dto.asset_dependency_hash);

            return new RestoredAsset
            {
                LoadInfo = info,
                Usage = usage,
                Extended = ext,
                AssetDependencyHash = depHash,
                ReferencedAssetHashes = DecodeReferencedAssetHashes(dto.referenced_asset_hashes),
            };
        }

        // null when the blob_meta predates the field (legacy capture); empty
        // Dictionary when the field is present but no project-resident refs
        // were captured.
        private static Dictionary<GUID, Hash128> DecodeReferencedAssetHashes(
            List<RoulinUnityAssetHashEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            var map = new Dictionary<GUID, Hash128>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.guid)
                    || string.IsNullOrEmpty(entry.asset_dependency_hash))
                {
                    continue;
                }

                var hash = Hash128.Parse(entry.asset_dependency_hash);
                if (!hash.isValid)
                {
                    continue;
                }

                map[new GUID(entry.guid)] = hash;
            }
            return map;
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
                ReferencedAssetHashes = DecodeReferencedAssetHashes(dto.referenced_asset_hashes),
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

    // Decoded RoulinBlobMeta batch ready to inject into SBP.
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
        public Hash128 AssetDependencyHash;
        public Dictionary<GUID, Hash128> ReferencedAssetHashes;
    }

    public struct RestoredScene
    {
        public GUID SceneGuid;
        public SceneDependencyInfo Info;
        public BuildUsageTagSet Usage;
        public Hash128 PrefabDependencyHash;
        public Dictionary<GUID, Hash128> ReferencedAssetHashes;
    }
}
