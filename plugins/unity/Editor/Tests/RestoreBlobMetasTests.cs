// Pairs with CollectBlobMetasTests to cover capture ↔ restore round-trip
// at the data-shape level; no real SBP build runs.

using Roulin.Editor.Build;
using Roulin.Editor.Build.Meta;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace Roulin.Editor.Tests
{
    public class RestoreBlobMetasTests
    {
        private readonly RestoreBlobMetas _restorer = new();
        private readonly CollectBlobMetas _collector = new();



        [Test]
        public void Decode_BuildsAssetByGuidMap()
        {
            var meta = MakeUnityMeta(
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                new[] { "11111111111111111111111111111111", "22222222222222222222222222222222" });

            var payload = _restorer.Decode(new[] { meta });

            Assert.AreEqual(2, payload.AssetByGuid.Count);
            Assert.IsTrue(payload.AssetByGuid.ContainsKey(new GUID("11111111111111111111111111111111")));
            Assert.IsTrue(payload.AssetByGuid.ContainsKey(new GUID("22222222222222222222222222222222")));
        }

        [Test]
        public void Decode_RestoresAssetLoadInfo()
        {
            var meta = MakeUnityMeta(
                "00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111" });

            // Add an included + referenced object to the asset.
            var asset = meta.unity_body.assets[0];
            asset.included_objects.Add(new RoulinUnityObjectId
            {
                guid = "11111111111111111111111111111111",
                local_identifier_in_file = 21300000,
                file_type = (int)FileType.MetaAssetType,
                file_path = "Assets/Hero.prefab",
                type_idxs = new List<int>()
            });
            asset.referenced_objects.Add(new RoulinUnityObjectId
            {
                guid = "22222222222222222222222222222222",
                local_identifier_in_file = 2100000,
                file_type = (int)FileType.SerializedAssetType,
                file_path = "library/unity default resources",
                type_idxs = new List<int>()
            });

            var payload = _restorer.Decode(new[] { meta });
            var restored = payload.AssetByGuid[new GUID("11111111111111111111111111111111")];

            Assert.AreEqual(1, restored.LoadInfo.includedObjects.Count);
            Assert.AreEqual(21300000, restored.LoadInfo.includedObjects[0].localIdentifierInFile);
            Assert.AreEqual(1, restored.LoadInfo.referencedObjects.Count);
            Assert.AreEqual(FileType.SerializedAssetType,
                restored.LoadInfo.referencedObjects[0].fileType);
        }

        [Test]
        public void Decode_ResolvesTypeIndicesToSystemTypes()
        {
            // Resolvable + unresolvable types; only the resolvable one should
            // surface in ObjectTypes.
            var meta = MakeUnityMeta("00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111" });
            meta.unity_body.types.Add(typeof(GameObject).AssemblyQualifiedName);
            meta.unity_body.types.Add("Bogus.Type.That.Does.Not.Exist, NoSuchAssembly");
            meta.unity_body.assets[0].included_objects.Add(new RoulinUnityObjectId
            {
                guid = "11111111111111111111111111111111",
                local_identifier_in_file = 21300000,
                file_type = (int)FileType.MetaAssetType,
                file_path = "Assets/Hero.prefab",
                type_idxs = new List<int> { 0 } // → GameObject
            });
            meta.unity_body.assets[0].included_objects.Add(new RoulinUnityObjectId
            {
                guid = "11111111111111111111111111111111",
                local_identifier_in_file = 21300001,
                file_type = (int)FileType.MetaAssetType,
                file_path = "Assets/Hero.prefab",
                type_idxs = new List<int> { 1 } // → unresolvable
            });

            var payload = _restorer.Decode(new[] { meta });

            Assert.AreEqual(1, payload.ObjectTypes.Count,
                "ObjectIds with unresolvable types are silently dropped");
            Assert.AreEqual(typeof(GameObject), payload.ObjectTypes[0].Value[0]);
            Assert.AreEqual(21300000, payload.ObjectTypes[0].Key.localIdentifierInFile);
        }

        [Test]
        public void Decode_DedupesObjectTypesAcrossAssets()
        {
            // Shared ObjectIdentifier across assets must dedupe in ObjectTypes.
            var meta = MakeUnityMeta("00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111", "22222222222222222222222222222222" });
            meta.unity_body.types.Add(typeof(GameObject).AssemblyQualifiedName);

            var sharedObj = new RoulinUnityObjectId
            {
                guid = "ffffffffffffffffffffffffffffffff",
                local_identifier_in_file = 99999,
                file_type = (int)FileType.SerializedAssetType,
                file_path = "library/unity default resources",
                type_idxs = new List<int> { 0 }
            };
            // Both assets reference it.
            meta.unity_body.assets[0].referenced_objects.Add(sharedObj);
            meta.unity_body.assets[1].referenced_objects.Add(sharedObj);

            var payload = _restorer.Decode(new[] { meta });

            Assert.AreEqual(1, payload.ObjectTypes.Count,
                "shared ObjectIdentifier must appear exactly once across the batch");
        }

        [Test]
        public void Decode_SkipsNonUnityBodies()
        {
            // body_type != "unity" → entry is skipped, no exception.
            var meta = new RoulinBlobMeta
            (
                new RoulinUEBlob { engine_version = "5.3" },
                "00".PadRight(64, '0')
            );
            
            var payload = _restorer.Decode(new[] { meta });
            Assert.AreEqual(0, payload.AssetByGuid.Count);
        }

        [Test]
        public void Decode_NullInput_ReturnsEmptyPayload()
        {
            var payload = _restorer.Decode(null);
            Assert.IsNotNull(payload);
            Assert.AreEqual(0, payload.AssetByGuid.Count);
            Assert.AreEqual(0, payload.ObjectTypes.Count);
        }



        [Test]
        public void ApplyToContext_PopulatesDependencyData()
        {
            var meta = MakeUnityMeta("00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111" });
            var payload = _restorer.Decode(new[] { meta });

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();
            var applied = _restorer.ApplyToContext(payload, dep, ext);

            var g = new GUID("11111111111111111111111111111111");
            Assert.IsTrue(applied.Contains(g));
            Assert.IsTrue(dep.AssetInfo.ContainsKey(g));
            Assert.IsTrue(dep.AssetUsage.ContainsKey(g));
        }

        [Test]
        public void ApplyToContext_PopulatesExtendedDataWhenPresent()
        {
            var meta = MakeUnityMeta("00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111" });
            // Add a representation so RestoredAsset.Extended is non-null.
            meta.unity_body.assets[0].representations.Add(new RoulinUnityObjectId
            {
                guid = "11111111111111111111111111111111",
                local_identifier_in_file = 21300001,
                file_type = (int)FileType.MetaAssetType,
                file_path = "Assets/Hero.prefab",
                type_idxs = new List<int>()
            });

            var payload = _restorer.Decode(new[] { meta });
            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();
            _restorer.ApplyToContext(payload, dep, ext);

            var g = new GUID("11111111111111111111111111111111");
            Assert.IsTrue(ext.ExtendedData.ContainsKey(g));
            Assert.AreEqual(1, ext.ExtendedData[g].Representations.Count);
        }

        [Test]
        public void ApplyToContext_NullExtendedData_DoesNotThrow()
        {
            // Some pipelines run without IBuildExtendedAssetData. Apply must
            // still write to dependencyData and ignore extended.
            var meta = MakeUnityMeta("00".PadRight(64, '0'),
                new[] { "11111111111111111111111111111111" });
            var payload = _restorer.Decode(new[] { meta });

            var dep = new FakeDependencyData();
            Assert.DoesNotThrow(() =>
                _restorer.ApplyToContext(payload, dep, null));
            Assert.IsTrue(dep.AssetInfo.ContainsKey(new GUID("11111111111111111111111111111111")));
        }



        [Test]
        public void RoundTrip_CollectorOutputThroughRestorer()
        {
            // Collector → wrap → Restorer.Decode → ApplyToContext: source fields preserved.
            var g = new GUID("11111111111111111111111111111111");
            var included = SbpReflection.Instance.MakeObjectIdentifier(
                g, 21300000, FileType.MetaAssetType, "Assets/Hero.prefab");
            var referenced = SbpReflection.Instance.MakeObjectIdentifier(
                new GUID("22222222222222222222222222222222"),
                2100000, FileType.SerializedAssetType, "library/unity default resources");

            var srcDep = new FakeDependencyData();
            srcDep.AssetInfo[g] = new AssetLoadInfo
            {
                asset = g,
                address = "Assets/Hero.prefab",
                includedObjects = new List<ObjectIdentifier> { included },
                referencedObjects = new List<ObjectIdentifier> { referenced }
            };
            srcDep.AssetUsage[g] = new BuildUsageTagSet();
            var srcWrite = new FakeBundleWriteData();
            srcWrite.AssetToFiles[g] = new List<string> { "file_ui" };
            srcWrite.FileToBundle["file_ui"] = "ui_main";

            var byBundle = _collector.ByBundle(
                srcDep, null, srcWrite,
                null, "2022.3.40f1", "1.21.25");
            var wrapped = new RoulinBlobMeta(
                byBundle["ui_main"], "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");


            var payload = _restorer.Decode(new[] { wrapped });
            var dstDep = new FakeDependencyData();
            var applied = _restorer.ApplyToContext(payload, dstDep, null);

            Assert.IsTrue(applied.Contains(g));
            Assert.IsTrue(dstDep.AssetInfo.TryGetValue(g, out var restored));
            Assert.AreEqual("Assets/Hero.prefab", restored.address);
            Assert.AreEqual(1, restored.includedObjects.Count);
            Assert.AreEqual(included, restored.includedObjects[0]);
            Assert.AreEqual(1, restored.referencedObjects.Count);
            Assert.AreEqual(referenced, restored.referencedObjects[0]);
        }



        // Builds a minimal RoulinBlobMeta with a RoulinUnityBlob body and
        // one RoulinUnityAsset per provided guid string. Caller adds objects
        // / types as needed.
        private static RoulinBlobMeta MakeUnityMeta(string blobHashHex, string[] assetGuids)
        {
            var unity = new RoulinUnityBlob
            {
                unity_version = "2022.3.40f1",
                sbp_version = "1.21.25",
                built_revision = "rev-test"
            };
            foreach (var g in assetGuids)
            {
                unity.assets.Add(new RoulinUnityAsset
                {
                    guid = g,
                    asset_address = $"Assets/Asset_{g.Substring(0, 4)}.prefab",
                    asset_dependency_hash = string.Empty,
                    build_usage_tag_set = string.Empty
                });
            }
            return new RoulinBlobMeta(unity, blobHashHex);
        }

        // SBP DI fakes — mirror BlobMetaCollectorTests structure. Members the
        // restorer doesn't exercise throw to surface accidental access.

        private sealed class FakeDependencyData : IDependencyData
        {
            public Dictionary<GUID, AssetLoadInfo> AssetInfo { get; } = new();
            public Dictionary<GUID, BuildUsageTagSet> AssetUsage { get; } = new();
            public Dictionary<GUID, SceneDependencyInfo> SceneInfo { get; } = new();
            public Dictionary<GUID, BuildUsageTagSet> SceneUsage { get; } = new();
            public BuildUsageTagGlobal GlobalUsage { get; set; } = default;
            public BuildUsageCache DependencyUsageCache { get; } = new();
            public Dictionary<GUID, Hash128> DependencyHash => throw new NotImplementedException();
        }

        private sealed class FakeBuildExtendedAssetData : IBuildExtendedAssetData
        {
            public Dictionary<GUID, ExtendedAssetData> ExtendedData { get; } = new();
        }

        private sealed class FakeBundleWriteData : IBundleWriteData
        {
            public Dictionary<string, List<WriteCommand>> WriteOperations { get; } = new();
            public Dictionary<GUID, List<string>> AssetToFiles { get; } = new();
            public Dictionary<string, string> FileToBundle { get; } = new();
            public Dictionary<string, BuildUsageTagSet> FileToUsageSet => throw new NotImplementedException();
            public Dictionary<string, BuildReferenceMap> FileToReferenceMap => throw new NotImplementedException();
            public Dictionary<string, List<ObjectIdentifier>> FileToObjects => throw new NotImplementedException();
            List<IWriteOperation> IWriteData.WriteOperations => throw new NotImplementedException();
        }
    }
}