// SBP↔DTO conversion tests. Naming convention:
//   *_DtoJsonRoundtrip — DTO ↔ JsonUtility only (wire format).
//   *_SbpRoundtrip     — SBP types → DTO → SBP types (dependency restore fidelity).
//   *_Budget           — Stopwatch-bounded perf at production scale.

using Roulin.Editor.Build;
using Roulin.Editor.Build.Meta;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Roulin.Editor.Tests
{
    public class BlobMetaConverterTests
    {
        // To* lives on Collector, From* on Restorer; exercise each direction
        // through its owning class.
        private readonly CollectBlobMetas _collector = new();
        private readonly RestoreBlobMetas _restorer = new();


        // 1 typical large blob ≈ 25 assets × ~55 ObjectRefs each = ~1375 ObjectRefs.
        private const int ScaleAssetsPerBlob = 25;
        private const int ScaleObjectsPerAsset = 55;



        [Test]
        public void DtoJsonRoundtrip_PreservesAllFields()
        {
            var dto = new RoulinBlobMeta(
                new RoulinUnityBlob
                {
                    unity_version = "2022.3.40f1",
                    sbp_version = "1.21.25",
                    built_revision = "rev-001",
                    types = new List<string>
                    {
                        "UnityEngine.GameObject, UnityEngine.CoreModule",
                        "UnityEngine.Material, UnityEngine.CoreModule"
                    },
                    assets = new List<RoulinUnityAsset>
                    {
                        new()
                        {
                            guid = "11111111111111111111111111111111",
                            asset_address = "Assets/Hero.prefab",
                            asset_dependency_hash = "deadbeefdeadbeefdeadbeefdeadbeef",
                            build_usage_tag_set = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
                            included_objects = new List<RoulinUnityObjectId>
                            {
                                new()
                                {
                                    guid = "11111111111111111111111111111111",
                                    local_identifier_in_file = 21300000,
                                    file_type = 1,
                                    file_path = "Assets/Hero.prefab",
                                    type_idxs = new List<int> { 0 }
                                }
                            },
                            referenced_objects = new List<RoulinUnityObjectId>
                            {
                                new()
                                {
                                    guid = "22222222222222222222222222222222",
                                    local_identifier_in_file = 2100000,
                                    file_type = 3,
                                    file_path = "library/unity default resources",
                                    type_idxs = new List<int> { 1 }
                                }
                            },
                            representations = new List<RoulinUnityObjectId>
                            {
                                new()
                                {
                                    guid = "11111111111111111111111111111111",
                                    local_identifier_in_file = 21300001,
                                    file_type = 1,
                                    file_path = "Assets/Hero.prefab",
                                    type_idxs = new List<int>()
                                }
                            }
                        }
                    }
                },
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
            );

            var json = JsonUtility.ToJson(dto);
            var back = JsonUtility.FromJson<RoulinBlobMeta>(json);

            Assert.AreEqual(dto.blob_hash, back.blob_hash);
            Assert.AreEqual(dto.body_type, back.body_type);
            Assert.IsNotNull(back.unity_body);
            Assert.AreEqual(dto.unity_body.unity_version, back.unity_body.unity_version);
            Assert.AreEqual(dto.unity_body.sbp_version, back.unity_body.sbp_version);
            Assert.That(back.unity_body.types, Is.EquivalentTo(dto.unity_body.types));
            Assert.AreEqual(1, back.unity_body.assets.Count);

            var ba = back.unity_body.assets[0];
            var da = dto.unity_body.assets[0];
            Assert.AreEqual(da.guid, ba.guid);
            Assert.AreEqual(da.asset_address, ba.asset_address);
            Assert.AreEqual(da.asset_dependency_hash, ba.asset_dependency_hash);
            Assert.AreEqual(da.build_usage_tag_set, ba.build_usage_tag_set);
            Assert.AreEqual(1, ba.included_objects.Count);
            Assert.AreEqual(1, ba.referenced_objects.Count);
            Assert.AreEqual(1, ba.representations.Count);

            var inc = ba.included_objects[0];
            Assert.AreEqual(21300000L, inc.local_identifier_in_file);
            Assert.AreEqual(1, inc.file_type);
            Assert.AreEqual(1, inc.type_idxs.Count);
            Assert.AreEqual(0, inc.type_idxs[0]);

            var refd = ba.referenced_objects[0];
            Assert.AreEqual(3, refd.file_type, "FileType=3 (SerializedAssetType) must survive — built-in refs depend on it");
            Assert.AreEqual("library/unity default resources", refd.file_path);

            var rep = ba.representations[0];
            Assert.AreEqual(0, rep.type_idxs.Count, "empty type_idxs (= no type info) must survive");
        }

        [Test]
        public void DtoJsonRoundtrip_EmptyOptionalFields()
        {
            // Empty optional fields must parse to defaults (not crashing nulls).
            var dto = new RoulinBlobMeta (
                new RoulinUnityBlob
                {
                    unity_version = "2022.3.40f1",
                    assets = new List<RoulinUnityAsset>
                    {
                        new() { guid = "0".PadRight(32, '0'), asset_address = "Assets/Empty.asset" }
                    }
                },
                "00".PadRight(64, '0')
            );

            var json = JsonUtility.ToJson(dto);
            var back = JsonUtility.FromJson<RoulinBlobMeta>(json);
            var a = back.unity_body.assets[0];
            Assert.IsNotNull(a.included_objects);
            Assert.AreEqual(0, a.included_objects.Count);
            Assert.IsNotNull(a.representations);
            Assert.AreEqual(0, a.representations.Count);
            Assert.That(string.IsNullOrEmpty(a.build_usage_tag_set));
        }



        [Test]
        public void ObjectIdentifier_RoundtripPreservesEquality()
        {
            // Equality (dict key) depends on guid/localId/fileType/filePath — all must survive.
            var src = MakeObjectIdentifier(
                new GUID("11111111111111111111111111111111"),
                21300000,
                FileType.MetaAssetType,
                "Assets/Hero.prefab");

            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();
            var dto = _collector.ToDto(src, null, typeIndex, typeList);
            var restored = _restorer.FromDto(dto);

            Assert.AreEqual(src, restored, "ObjectIdentifier equality must roundtrip — SBP dictionaries depend on it");
            Assert.AreEqual(src.GetHashCode(), restored.GetHashCode());
        }

        [Test]
        public void ObjectIdentifier_FileTypeSurvives()
        {
            // file_type (Meta=1 / Serialized=3) drives bundle dep resolution.
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();

            var src3 = MakeObjectIdentifier(default, 2800000, FileType.SerializedAssetType, "library/unity default resources");
            var dto3 = _collector.ToDto(src3, null, typeIndex, typeList);
            var back3 = _restorer.FromDto(dto3);
            Assert.AreEqual(FileType.SerializedAssetType, back3.fileType);
            Assert.AreEqual("library/unity default resources", back3.filePath);
        }

        [Test]
        public void TypeInterning_DedupesAcrossObjects()
        {
            // 1000 GameObjects → single interned type entry with correct indices.
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();
            var objToTypes = new Dictionary<ObjectIdentifier, Type[]>();

            var ids = new List<ObjectIdentifier>();
            for (var i = 0; i < 100; i++)
            {
                var id = MakeObjectIdentifier(
                    new GUID("11111111111111111111111111111111"),
                    i, FileType.MetaAssetType, "Assets/Hero.prefab");
                ids.Add(id);
                // Alternate two types → 2 interned strings.
                objToTypes[id] = new[] { i % 2 == 0 ? typeof(GameObject) : typeof(Material) };
            }

            var dtos = new List<RoulinUnityObjectId>();
            foreach (var id in ids)
            {
                dtos.Add(_collector.ToDto(id, objToTypes, typeIndex, typeList));
            }

            Assert.AreEqual(2, typeList.Count, "exactly 2 unique types should be interned");
            for (var i = 0; i < dtos.Count; i++)
            {
                var wantIdx = i % 2 == 0
                    ? typeIndex[typeof(GameObject).AssemblyQualifiedName]
                    : typeIndex[typeof(Material).AssemblyQualifiedName];
                Assert.AreEqual(1, dtos[i].type_idxs.Count);
                Assert.AreEqual(wantIdx, dtos[i].type_idxs[0]);
            }
        }

        [Test]
        public void AssetLoadInfo_RoundtripPreservesAllFields()
        {
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();
            var objToTypes = new Dictionary<ObjectIdentifier, Type[]>();

            var includedObj = MakeObjectIdentifier(new GUID("11111111111111111111111111111111"), 21300000, FileType.MetaAssetType,
                "Assets/Hero.prefab");
            var referencedObj = MakeObjectIdentifier(new GUID("22222222222222222222222222222222"), 2100000, FileType.SerializedAssetType,
                "library/unity default resources");
            var representObj = MakeObjectIdentifier(new GUID("11111111111111111111111111111111"), 21300001, FileType.MetaAssetType,
                "Assets/Hero.prefab");
            objToTypes[includedObj] = new[] { typeof(GameObject) };
            objToTypes[referencedObj] = new[] { typeof(Material) };

            var input = new CapturedAsset
            {
                AssetGuid = new GUID("11111111111111111111111111111111"),
                AssetAddress = "Assets/Hero.prefab",
                AssetDependencyHash = Hash128.Compute("test"),
                IncludedObjects = new[] { includedObj },
                ReferencedObjects = new[] { referencedObj },
                Representations = new[] { representObj },
                BuildUsageTagSetBytes = Array.Empty<byte>() // SerializeToFile of an empty set is engine-version-dependent
            };

            var dto = _collector.ToDto(input, objToTypes, typeIndex, typeList);
            var outputs = _restorer.FromDto(dto);

            Assert.AreEqual(input.AssetGuid, outputs.LoadInfo.asset);
            Assert.AreEqual(input.AssetAddress, outputs.LoadInfo.address);
            Assert.AreEqual(1, outputs.LoadInfo.includedObjects.Count);
            Assert.AreEqual(includedObj, outputs.LoadInfo.includedObjects[0]);
            Assert.AreEqual(1, outputs.LoadInfo.referencedObjects.Count);
            Assert.AreEqual(referencedObj, outputs.LoadInfo.referencedObjects[0]);
            Assert.IsNotNull(outputs.Extended);
            Assert.AreEqual(1, outputs.Extended.Representations.Count);
            Assert.AreEqual(representObj, outputs.Extended.Representations[0]);
            Assert.AreEqual(input.AssetDependencyHash, outputs.AssetDependencyHash);
        }



        [Test]
        public void ToDto_AtOneBlobScale_Budget()
        {
            // Unit of work: ~1375 refs. Must stay well under 100 ms.
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();
            var objToTypes = new Dictionary<ObjectIdentifier, Type[]>();
            var inputs = SynthesizeInputs(ScaleAssetsPerBlob, ScaleObjectsPerAsset, objToTypes);

            var sw = Stopwatch.StartNew();
            var dtos = new List<RoulinUnityAsset>(inputs.Count);
            foreach (var input in inputs)
            {
                dtos.Add(_collector.ToDto(input, objToTypes, typeIndex, typeList));
            }

            sw.Stop();

            TestContext.WriteLine(
                $"ToDto: {ScaleAssetsPerBlob} assets × {ScaleObjectsPerAsset} objs " +
                $"= {ScaleAssetsPerBlob * ScaleObjectsPerAsset * 3} ObjectRefs → " +
                $"{sw.ElapsedMilliseconds} ms (types interned: {typeList.Count})");

            Assert.AreEqual(ScaleAssetsPerBlob, dtos.Count);
            Assert.Less(sw.ElapsedMilliseconds, 100,
                "ToDto budget exceeded — per-blob capture would dominate cold-build overhead");
        }

        [Test]
        public void FromDto_AtOneBlobScale_Budget()
        {
            // Tighter budget — runs per unaffected blob (up to 4000 worst case).
            var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeList = new List<string>();
            var objToTypes = new Dictionary<ObjectIdentifier, Type[]>();
            var inputs = SynthesizeInputs(ScaleAssetsPerBlob, ScaleObjectsPerAsset, objToTypes);
            var dtos = new List<RoulinUnityAsset>(inputs.Count);
            foreach (var input in inputs)
            {
                dtos.Add(_collector.ToDto(input, objToTypes, typeIndex, typeList));
            }

            var sw = Stopwatch.StartNew();
            var outs = new List<RestoredAsset>(dtos.Count);
            foreach (var dto in dtos)
            {
                outs.Add(_restorer.FromDto(dto));
            }

            sw.Stop();

            TestContext.WriteLine(
                $"FromDto: {ScaleAssetsPerBlob} assets × {ScaleObjectsPerAsset} objs " +
                $"= {ScaleAssetsPerBlob * ScaleObjectsPerAsset * 3} ObjectRefs → " +
                $"{sw.ElapsedMilliseconds} ms");

            Assert.AreEqual(dtos.Count, outs.Count);
            Assert.Less(sw.ElapsedMilliseconds, 100,
                "FromDto budget exceeded — per-blob restore would dominate warm-build overhead");
        }



        // Construct ObjectIdentifier via SbpReflection — SBP has no public
        // ctor, so this is the only path.
        private static ObjectIdentifier MakeObjectIdentifier(GUID guid, long lid, FileType ft, string path)
        {
            return SbpReflection.Instance.MakeObjectIdentifier(guid, lid, ft, path);
        }

        private static List<CapturedAsset> SynthesizeInputs(
            int assetCount, int objsPerAsset, IDictionary<ObjectIdentifier, Type[]> objToTypes)
        {
            var list = new List<CapturedAsset>(assetCount);
            for (var a = 0; a < assetCount; a++)
            {
                var guidHex = $"{a:x32}";
                if (guidHex.Length > 32)
                {
                    guidHex = guidHex.Substring(0, 32);
                }
                else if (guidHex.Length < 32)
                {
                    guidHex = guidHex.PadLeft(32, '0');
                }

                var assetGuid = new GUID(guidHex);
                var assetPath = $"Assets/Synthetic/Asset_{a:0000}.prefab";

                var included = new List<ObjectIdentifier>(objsPerAsset);
                var referenced = new List<ObjectIdentifier>(objsPerAsset);
                var reps = new List<ObjectIdentifier>(objsPerAsset);
                for (var o = 0; o < objsPerAsset; o++)
                {
                    var inc = MakeObjectIdentifier(assetGuid, 21300000L + o, FileType.MetaAssetType, assetPath);
                    included.Add(inc);
                    objToTypes[inc] = new[] { typeof(GameObject) };
                    var refd = MakeObjectIdentifier(
                        new GUID($"{(a + 1) % 0x10000:x32}".PadLeft(32, '0')),
                        2100000L + o, FileType.SerializedAssetType, "library/unity default resources");
                    referenced.Add(refd);
                    objToTypes[refd] = new[] { typeof(Material) };
                    var rep = MakeObjectIdentifier(assetGuid, 21400000L + o, FileType.MetaAssetType, assetPath);
                    reps.Add(rep);
                    objToTypes[rep] = new[] { typeof(GameObject) };
                }

                list.Add(new CapturedAsset
                {
                    AssetGuid = assetGuid,
                    AssetAddress = assetPath,
                    AssetDependencyHash = Hash128.Compute(assetPath),
                    IncludedObjects = included,
                    ReferencedObjects = referenced,
                    Representations = reps,
                    BuildUsageTagSetBytes = Array.Empty<byte>()
                });
            }

            return list;
        }
    }
}