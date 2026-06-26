using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace Roulin.Editor.Build.Meta
{
    public sealed class CollectBlobMetas
    {
        // 3 phases: (1) group asset GUIDs by owning bundle, (2) for each bundle
        // fork each GUID into asset or scene capture, (3) sort + log per blob.
        // blob_hash is left empty (publish task fills it after BLAKE3).
        // objectToTypes null/empty → type_idxs empty; warm restore falls back
        // to per-object ContentBuildInterface lookup.
        public Dictionary<string, RoulinUnityBlob> ByBundle(
            IDependencyData dependencyData,
            IBuildExtendedAssetData extendedAssetData,
            IBundleWriteData writeData,
            IDictionary<ObjectIdentifier, Type[]> objectToTypes,
            string unityVersion,
            string sbpVersion)
        {
            if (dependencyData == null)
            {
                throw new ArgumentNullException(nameof(dependencyData));
            }

            if (writeData == null)
            {
                throw new ArgumentNullException(nameof(writeData));
            }

            // (1) Group asset GUIDs by owning bundle. files[0] is the canonical
            // file (main object); secondary files hold sub-assets in the same bundle.
            var assetsByBundle = new Dictionary<string, List<GUID>>(StringComparer.Ordinal);
            foreach (var kv in writeData.AssetToFiles)
            {
                var assetGuid = kv.Key;
                var files = kv.Value;
                if (files == null || files.Count == 0)
                {
                    continue;
                }

                if (!writeData.FileToBundle.TryGetValue(files[0], out var bundleName))
                {
                    continue;
                }

                if (!assetsByBundle.TryGetValue(bundleName, out var list))
                {
                    list = new List<GUID>();
                    assetsByBundle[bundleName] = list;
                }

                list.Add(assetGuid);
            }

            // (2) Build one RoulinUnityBlob per bundle. typeIndex is per-blob
            // so type interning is shared across assets within the blob.
            var result = new Dictionary<string, RoulinUnityBlob>(
                assetsByBundle.Count, StringComparer.Ordinal);

            foreach (var kv in assetsByBundle)
            {
                var bundleName = kv.Key;
                var assetGuids = kv.Value;

                var blob = new RoulinUnityBlob
                {
                    unity_version = unityVersion ?? string.Empty,
                    sbp_version = sbpVersion ?? string.Empty,
                    built_revision = string.Empty
                };
                var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);

                // Fork each GUID: asset (AssetInfo) vs scene (SceneInfo).
                foreach (var guid in assetGuids)
                {
                    if (dependencyData.AssetInfo.TryGetValue(guid, out var assetInfo))
                    {
                        BuildUsageTagSet usage = null;
                        if (dependencyData.AssetUsage != null)
                        {
                            dependencyData.AssetUsage.TryGetValue(guid, out usage);
                        }

                        ExtendedAssetData ext = null;
                        if (extendedAssetData != null)
                        {
                            extendedAssetData.ExtendedData.TryGetValue(guid, out ext);
                        }

                        // Scriptable Build Pipeline's GenerateBundleCommands has already populated address with
                        // AssetBundleBuild.addressableNames[i] (or asset path fallback).
                        var assetAddress = assetInfo.address;
                        var assetPath = AssetDatabase.GUIDToAssetPath(guid.ToString());

                        var captured = new CapturedAsset
                        {
                            AssetGuid = guid,
                            AssetAddress = assetAddress,
                            IncludedObjects = assetInfo.includedObjects,
                            ReferencedObjects = assetInfo.referencedObjects,
                            Representations = ext?.Representations,
                            BuildUsageTagSetBytes = usage != null
                                ? SbpReflection.Instance.SerializeBuildUsageTagSet(usage)
                                : Array.Empty<byte>()
                        };
                        blob.assets.Add(ToDto(captured, objectToTypes, typeIndex, blob.types));
                        continue;
                    }

                    // Scene .unity files surface here via WriteData.AssetToFiles too.
                    if (dependencyData.SceneInfo.TryGetValue(guid, out var sceneInfo))
                    {
                        BuildUsageTagSet sceneUsage = null;
                        if (dependencyData.SceneUsage != null)
                        {
                            dependencyData.SceneUsage.TryGetValue(guid, out sceneUsage);
                        }

                        Hash128 prefabDepHash = default;
                        if (dependencyData.DependencyHash != null)
                        {
                            dependencyData.DependencyHash.TryGetValue(guid, out prefabDepHash);
                        }

                        blob.scenes.Add(SceneToDto(
                            guid, sceneInfo, sceneUsage, prefabDepHash,
                            objectToTypes, typeIndex, blob.types));
                    }
                }

                // (3) Sort deterministically → byte-stable JSON for inspect / debug parity.
                blob.assets.Sort((a, b) => string.CompareOrdinal(a.asset_address, b.asset_address));
                blob.scenes.Sort((a, b) => string.CompareOrdinal(a.scene_path, b.scene_path));

                Debug.Log(
                    $"[CollectBlobMetas] {bundleName}: " +
                    $"assets={blob.assets.Count} scenes={blob.scenes.Count}");

                result[bundleName] = blob;
            }
            return result;
        }

        // One asset → DTO. typeIndex/typeList are shared across the enclosing blob.
        public RoulinUnityAsset ToDto(
            CapturedAsset input,
            IDictionary<ObjectIdentifier, Type[]> objectToTypes,
            IDictionary<string, int> typeIndex,
            List<string> typeList)
        {
            var dto = new RoulinUnityAsset
            {
                guid = input.AssetGuid.ToString(),
                asset_address = input.AssetAddress ?? string.Empty,
                build_usage_tag_set = input.BuildUsageTagSetBytes == null
                                      || input.BuildUsageTagSetBytes.Length == 0
                    ? string.Empty
                    : Convert.ToBase64String(input.BuildUsageTagSetBytes)
            };
            if (input.IncludedObjects != null)
            {
                foreach (var o in input.IncludedObjects)
                {
                    dto.included_objects.Add(ToDto(o, objectToTypes, typeIndex, typeList));
                }
            }

            if (input.ReferencedObjects != null)
            {
                foreach (var o in input.ReferencedObjects)
                {
                    dto.referenced_objects.Add(ToDto(o, objectToTypes, typeIndex, typeList));
                }
            }

            if (input.Representations != null)
            {
                foreach (var o in input.Representations)
                {
                    dto.representations.Add(ToDto(o, objectToTypes, typeIndex, typeList));
                }
            }

            return dto;
        }

        // One scene → DTO. SceneDependencyInfo has no includedObjects, only
        // referenced; included types are interned via included_type_idxs.
        public RoulinUnityScene SceneToDto(
            GUID sceneGuid,
            SceneDependencyInfo info,
            BuildUsageTagSet usage,
            Hash128 prefabDependencyHash,
            IDictionary<ObjectIdentifier, Type[]> objectToTypes,
            IDictionary<string, int> typeIndex,
            List<string> typeList)
        {
            var dto = new RoulinUnityScene
            {
                guid = sceneGuid.ToString(),
                scene_path = info.scene ?? string.Empty,
                prefab_dependency_hash = prefabDependencyHash.isValid
                    ? prefabDependencyHash.ToString()
                    : string.Empty,
                build_usage_tag_set = usage == null
                    ? string.Empty
                    : Convert.ToBase64String(
                        SbpReflection.Instance.SerializeBuildUsageTagSet(usage))
            };

            foreach (var oi in info.referencedObjects)
            {
                dto.referenced_objects.Add(ToDto(oi, objectToTypes, typeIndex, typeList));
            }

            foreach (var t in info.includedTypes)
            {
                dto.included_type_idxs.Add(ResolveTypeIdxForType(t, typeIndex, typeList));
            }

            // Name-addressed serialisation: forward-compatible with new Unity fields.
            SbpReflection.Instance.CaptureBuildUsageTagGlobal(
                info.globalUsage,
                out var uintFields,
                out var boolFields);
            foreach (var kv in uintFields)
            {
                dto.global_usage.uint_fields.Add(new RoulinUsageGlobalUintField
                {
                    name = kv.Key,
                    value = kv.Value
                });
            }

            foreach (var kv in boolFields)
            {
                dto.global_usage.bool_fields.Add(new RoulinUsageGlobalBoolField
                {
                    name = kv.Key,
                    value = kv.Value
                });
            }

            return dto;
        }

        public RoulinUnityObjectId ToDto(
            ObjectIdentifier id,
            IDictionary<ObjectIdentifier, Type[]> objectToTypes,
            IDictionary<string, int> typeIndex,
            List<string> typeList)
        {
            return new RoulinUnityObjectId
            {
                guid = id.guid.ToString(),
                local_identifier_in_file = id.localIdentifierInFile,
                file_type = (int)id.fileType,
                file_path = id.filePath ?? string.Empty,
                type_idxs = ResolveTypeIdxs(id, objectToTypes, typeIndex, typeList)
            };
        }

        // Returns indices for all Types tied to an ObjectIdentifier — multi-Type
        // matters because Scriptable Build Pipeline hashes the union into its WSF cache key.
        private List<int> ResolveTypeIdxs(
            ObjectIdentifier id,
            IDictionary<ObjectIdentifier, Type[]> objectToTypes,
            IDictionary<string, int> typeIndex,
            List<string> typeList)
        {
            var result = new List<int>();
            if (objectToTypes == null || !objectToTypes.TryGetValue(id, out var types) || types == null)
            {
                return result;
            }

            foreach (var t in types)
            {
                if (t == null)
                {
                    continue;
                }

                var aqn = t.AssemblyQualifiedName ?? t.FullName;
                if (string.IsNullOrEmpty(aqn))
                {
                    continue;
                }

                if (typeIndex.TryGetValue(aqn, out var idx))
                {
                    result.Add(idx);
                    continue;
                }

                idx = typeList.Count;
                typeList.Add(aqn);
                typeIndex[aqn] = idx;
                result.Add(idx);
            }

            return result;
        }

        // Direct-Type variant of ResolveTypeIdxs; used when caller already has
        // SceneDependencyInfo.includedTypes (Type[]) instead of an ObjectIdentifier.
        private int ResolveTypeIdxForType(
            Type t,
            IDictionary<string, int> typeIndex,
            List<string> typeList)
        {
            if (t == null)
            {
                return -1;
            }

            var aqn = t.AssemblyQualifiedName ?? t.FullName;
            if (string.IsNullOrEmpty(aqn))
            {
                return -1;
            }

            if (typeIndex.TryGetValue(aqn, out var idx))
            {
                return idx;
            }

            idx = typeList.Count;
            typeList.Add(aqn);
            typeIndex[aqn] = idx;
            return idx;
        }
    }

    // One asset's dependency data captured from build context, in CollectBlobMetas.ToDto-friendly shape.
    public struct CapturedAsset
    {
        public GUID AssetGuid;
        public string AssetAddress;
        public IReadOnlyList<ObjectIdentifier> IncludedObjects;
        public IReadOnlyList<ObjectIdentifier> ReferencedObjects;
        public IReadOnlyList<ObjectIdentifier> Representations;
        public byte[] BuildUsageTagSetBytes;
    }
}
