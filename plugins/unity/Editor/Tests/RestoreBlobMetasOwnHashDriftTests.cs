// OwnHashDriftCheck: rejects restore entries whose stored AssetDependencyHash differs from current.

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
    public class RestoreBlobMetasOwnHashDriftTests
    {
        private readonly RestoreBlobMetas _restorer = new();
        private readonly List<string> _createdPaths = new();
        private BuildTarget _target;

        [SetUp]
        public void SetUp()
        {
            _target = EditorUserBuildSettings.activeBuildTarget;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var path in _createdPaths)
            {
                AssetDatabase.DeleteAsset(path);
            }

            _createdPaths.Clear();
            AssetDatabase.Refresh();
        }

        [Test]
        public void ApplyToContext_StaleEntry_CurrentlyCommittedWithoutHashCheck()
        {
            var path = CreateSimplePrefab();
            var guid = new GUID(AssetDatabase.AssetPathToGUID(path));
            var capturedHash = AssetDatabase.GetAssetDependencyHash(path);
            var capturedObjectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, _target);
            Assume.That(capturedObjectIds, Is.Not.Empty, "fixture prefab must expose at least one ObjectId");

            var loaded = PrefabUtility.LoadPrefabContents(path);
            loaded.AddComponent<SphereCollider>();
            PrefabUtility.SaveAsPrefabAsset(loaded, path);
            PrefabUtility.UnloadPrefabContents(loaded);
            AssetDatabase.Refresh();

            var afterHash = AssetDatabase.GetAssetDependencyHash(path);
            Assume.That(afterHash, Is.Not.EqualTo(capturedHash),
                "fixture mutation must change AssetDependencyHash for this test to be meaningful");

            // Mimics blob_meta captured before the mutation: carries the OLD hash and ObjectIds.
            var restoredAsset = new RestoredAsset
            {
                LoadInfo = new AssetLoadInfo
                {
                    asset = guid,
                    address = path,
                    includedObjects = new List<ObjectIdentifier>(capturedObjectIds),
                    referencedObjects = new List<ObjectIdentifier>()
                },
                Usage = new BuildUsageTagSet(),
                Extended = null,
                AssetDependencyHash = capturedHash, // ← stale relative to current AssetDatabase
            };

            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [guid] = restoredAsset },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext);

            Assert.IsTrue(applied.Contains(guid),
                "Without a currentHashLookup, ApplyToContext applies every payload entry " +
                "unconditionally — no AssetDependencyHash comparison happens.");
            Assert.IsTrue(dep.AssetInfo.ContainsKey(guid),
                "stale AssetLoadInfo was committed into IDependencyData.AssetInfo");
        }

        // OwnHashDriftCheck in action: same setup as the previous test,
        // but ApplyToContext is called with a currentHashLookup. The
        // stale-hash entry must be rejected so its ObjectIds never reach
        // IDependencyData.
        [Test]
        public void ApplyToContext_StaleEntry_RejectedByOwnHashDriftCheck_WhenLookupProvided()
        {
            var path = CreateSimplePrefab();
            var guid = new GUID(AssetDatabase.AssetPathToGUID(path));
            var capturedHash = AssetDatabase.GetAssetDependencyHash(path);
            var capturedObjectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, _target);
            Assume.That(capturedObjectIds, Is.Not.Empty);

            var loaded = PrefabUtility.LoadPrefabContents(path);
            loaded.AddComponent<SphereCollider>();
            PrefabUtility.SaveAsPrefabAsset(loaded, path);
            PrefabUtility.UnloadPrefabContents(loaded);
            AssetDatabase.Refresh();

            var afterHash = AssetDatabase.GetAssetDependencyHash(path);
            Assume.That(afterHash, Is.Not.EqualTo(capturedHash),
                "fixture mutation must change AssetDependencyHash for this test to be meaningful");

            var restoredAsset = new RestoredAsset
            {
                LoadInfo = new AssetLoadInfo
                {
                    asset = guid,
                    address = path,
                    includedObjects = new List<ObjectIdentifier>(capturedObjectIds),
                    referencedObjects = new List<ObjectIdentifier>()
                },
                Usage = new BuildUsageTagSet(),
                Extended = null,
                AssetDependencyHash = capturedHash,
            };
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [guid] = restoredAsset },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup);

            Assert.IsFalse(applied.Contains(guid),
                "OwnHashDriftCheck should reject restore entries whose stored " +
                "AssetDependencyHash differs from the current AssetDatabase hash");
            Assert.IsFalse(dep.AssetInfo.ContainsKey(guid),
                "stale AssetLoadInfo must NOT be committed when OwnHashDriftCheck is wired");
        }

        // Mirror of the rejection test for the positive case: when hash
        // matches, the entry should still be applied. Guards against an
        // overzealous OwnHashDriftCheck implementation that drops
        // everything.
        [Test]
        public void ApplyToContext_FreshEntry_PassesOwnHashDriftCheck()
        {
            var path = CreateSimplePrefab();
            var guid = new GUID(AssetDatabase.AssetPathToGUID(path));
            var currentHash = AssetDatabase.GetAssetDependencyHash(path);
            var objectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, _target);
            Assume.That(objectIds, Is.Not.Empty);

            var restoredAsset = new RestoredAsset
            {
                LoadInfo = new AssetLoadInfo
                {
                    asset = guid,
                    address = path,
                    includedObjects = new List<ObjectIdentifier>(objectIds),
                    referencedObjects = new List<ObjectIdentifier>()
                },
                Usage = new BuildUsageTagSet(),
                Extended = null,
                AssetDependencyHash = currentHash, // matches current → should pass
            };
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [guid] = restoredAsset },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup);

            Assert.IsTrue(applied.Contains(guid),
                "matching-hash entry must pass OwnHashDriftCheck and be applied");
            Assert.IsTrue(dep.AssetInfo.ContainsKey(guid));
        }

        private string CreateSimplePrefab()
        {
            var go = new GameObject("OwnHashDriftRoot");
            go.AddComponent<BoxCollider>();
            try
            {
                var p = AssetDatabase.GenerateUniqueAssetPath("Assets/__ownhashdrift.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, p);
                _createdPaths.Add(p);
                return p;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

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
    }
}
