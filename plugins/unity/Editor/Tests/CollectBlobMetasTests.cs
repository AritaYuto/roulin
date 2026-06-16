// SBP context interfaces are mocked with minimal in-memory fakes (defined
// at the bottom of this file); no real SBP build runs.

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
    public class CollectBlobMetasTests
    {
        private readonly CollectBlobMetas _collector = new();



        [Test]
        public void CollectByBundle_GroupsAssetsByOwningBundle()
        {
            // 3 assets across 2 bundles: ui_main has 2, shared has 1.
            var g1 = new GUID("11111111111111111111111111111111");
            var g2 = new GUID("22222222222222222222222222222222");
            var g3 = new GUID("33333333333333333333333333333333");

            var dep = new FakeDependencyData();
            dep.AssetInfo[g1] = MakeAssetLoadInfo(g1, "Assets/UI/A.prefab");
            dep.AssetInfo[g2] = MakeAssetLoadInfo(g2, "Assets/UI/B.prefab");
            dep.AssetInfo[g3] = MakeAssetLoadInfo(g3, "Assets/Shared/Atlas.png");

            var write = new FakeBundleWriteData();
            write.AssetToFiles[g1] = new List<string> { "file_ui_a" };
            write.AssetToFiles[g2] = new List<string> { "file_ui_b" };
            write.AssetToFiles[g3] = new List<string> { "file_shared" };
            write.FileToBundle["file_ui_a"] = "ui_main";
            write.FileToBundle["file_ui_b"] = "ui_main";
            write.FileToBundle["file_shared"] = "shared";

            var result = _collector.ByBundle(
                dep, null, write,
                null,
                "2022.3.40f1",
                "1.21.25");

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.ContainsKey("ui_main"));
            Assert.IsTrue(result.ContainsKey("shared"));
            Assert.AreEqual(2, result["ui_main"].assets.Count);
            Assert.AreEqual(1, result["shared"].assets.Count);
        }

        [Test]
        public void CollectByBundle_FillsAssetFields()
        {
            // End-to-end: converter output reaches the per-bundle DTO unchanged.
            var g = new GUID("11111111111111111111111111111111");
            var included = SbpReflection.Instance.MakeObjectIdentifier(g, 21300000, FileType.MetaAssetType, "Assets/Hero.prefab");
            var referenced = SbpReflection.Instance.MakeObjectIdentifier(
                new GUID("22222222222222222222222222222222"),
                2100000, FileType.SerializedAssetType, "library/unity default resources");
            var rep = SbpReflection.Instance.MakeObjectIdentifier(g, 21300001, FileType.MetaAssetType, "Assets/Hero.prefab");

            var dep = new FakeDependencyData();
            dep.AssetInfo[g] = new AssetLoadInfo
            {
                asset = g,
                address = "Assets/Hero.prefab",
                includedObjects = new List<ObjectIdentifier> { included },
                referencedObjects = new List<ObjectIdentifier> { referenced }
            };
            dep.AssetUsage[g] = new BuildUsageTagSet();
            var ext = new FakeBuildExtendedAssetData();
            ext.ExtendedData[g] = new ExtendedAssetData
            {
                Representations = new List<ObjectIdentifier> { rep }
            };

            var write = new FakeBundleWriteData();
            write.AssetToFiles[g] = new List<string> { "file_ui" };
            write.FileToBundle["file_ui"] = "ui_main";

            var result = _collector.ByBundle(
                dep, ext, write,
                null,
                "2022.3.40f1",
                "1.21.25");

            Assert.IsTrue(result.ContainsKey("ui_main"));
            var blob = result["ui_main"];
            Assert.AreEqual("2022.3.40f1", blob.unity_version);
            Assert.AreEqual("1.21.25", blob.sbp_version);
            Assert.AreEqual(1, blob.assets.Count);

            var asset = blob.assets[0];
            Assert.AreEqual("11111111111111111111111111111111", asset.guid);
            Assert.AreEqual("Assets/Hero.prefab", asset.asset_address);
            Assert.AreEqual(1, asset.included_objects.Count);
            Assert.AreEqual(1, asset.referenced_objects.Count);
            Assert.AreEqual(1, asset.representations.Count);
            Assert.AreEqual(21300000, asset.included_objects[0].local_identifier_in_file);
            Assert.AreEqual((int)FileType.SerializedAssetType, asset.referenced_objects[0].file_type);
        }

        [Test]
        public void CollectByBundle_SkipsAssetsMissingFromDependencyData()
        {
            // Assets in writeData but not in dependencyData are silently skipped.
            var g1 = new GUID("11111111111111111111111111111111");
            var g2 = new GUID("22222222222222222222222222222222");

            var dep = new FakeDependencyData();
            dep.AssetInfo[g1] = MakeAssetLoadInfo(g1, "Assets/A.prefab");
            // g2 deliberately missing from AssetInfo.

            var write = new FakeBundleWriteData();
            write.AssetToFiles[g1] = new List<string> { "file_a" };
            write.AssetToFiles[g2] = new List<string> { "file_b" };
            write.FileToBundle["file_a"] = "bundle";
            write.FileToBundle["file_b"] = "bundle";

            var result = _collector.ByBundle(
                dep, null, write, null, "u", "s");

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result["bundle"].assets.Count);
            Assert.AreEqual("11111111111111111111111111111111", result["bundle"].assets[0].guid);
        }

        [Test]
        public void CollectByBundle_AssetsSortedByPath_Deterministic()
        {
            // Output sorted by asset_address; idempotent across runs.
            var ga = new GUID("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var gb = new GUID("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var gc = new GUID("cccccccccccccccccccccccccccccccc");

            var dep = new FakeDependencyData();
            dep.AssetInfo[gc] = MakeAssetLoadInfo(gc, "Assets/C.prefab");
            dep.AssetInfo[ga] = MakeAssetLoadInfo(ga, "Assets/A.prefab");
            dep.AssetInfo[gb] = MakeAssetLoadInfo(gb, "Assets/B.prefab");

            var write = new FakeBundleWriteData();
            write.AssetToFiles[gc] = new List<string> { "file_c" };
            write.AssetToFiles[ga] = new List<string> { "file_a" };
            write.AssetToFiles[gb] = new List<string> { "file_b" };
            write.FileToBundle["file_a"] = "b";
            write.FileToBundle["file_b"] = "b";
            write.FileToBundle["file_c"] = "b";

            var r1 = _collector.ByBundle(dep, null, write, null, "u", "s");
            var r2 = _collector.ByBundle(dep, null, write, null, "u", "s");

            var paths1 = r1["b"].assets.ConvertAll(a => a.asset_address);
            var paths2 = r2["b"].assets.ConvertAll(a => a.asset_address);
            CollectionAssert.AreEqual(
                new[] { "Assets/A.prefab", "Assets/B.prefab", "Assets/C.prefab" }, paths1);
            CollectionAssert.AreEqual(paths1, paths2);
        }

        [Test]
        public void CollectByBundle_EmptyContext_ReturnsEmptyDict()
        {
            var result = _collector.ByBundle(
                new FakeDependencyData(), null, new FakeBundleWriteData(),
                null, "u", "s");
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void WrapUnity_StampsHashAndBodyType()
        {
            var unity = new RoulinUnityBlob { unity_version = "2022.3.40f1" };
            var meta = new RoulinBlobMeta(
                unity, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
            Assert.AreEqual("unity", meta.body_type);
            Assert.AreEqual("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", meta.blob_hash);
            Assert.AreSame(unity, meta.unity_body);
        }



        private static AssetLoadInfo MakeAssetLoadInfo(GUID guid, string path)
        {
            return new AssetLoadInfo
            {
                asset = guid,
                address = path,
                includedObjects = new List<ObjectIdentifier>(),
                referencedObjects = new List<ObjectIdentifier>()
            };
        }


        // Minimal SBP interface fakes; unsupported members throw to surface
        // accidental new accesses instead of running on zeroed data.

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

            // Explicit impl: IBundleWriteData.WriteOperations narrows the base return.
            List<IWriteOperation> IWriteData.WriteOperations => throw new NotImplementedException();
        }
    }
}