// OutOfPayloadObjectIdDriftCheck: rejects restore entries whose referenced ObjectIds are absent from the current GetPlayerObjectIdentifiersInAsset of out-of-payload project assets.

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
    public class RestoreBlobMetasOutOfPayloadObjectIdDriftTests
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

        // A's stored referenced_objects contain an ObjectId at guid=P with
        // a localId NOT in P's current GetPlayerObjectIdentifiersInAsset.
        // P is real and project-resident but NOT in payload. The check
        // must reject A.
        [Test]
        public void ApplyToContext_AReferencesOutOfPayloadGuidWithStaleObjectId_ARejectedByOutOfPayloadObjectIdDriftCheck()
        {
            var fx = CreateTwoMaterials();
            var aHashStored = AssetDatabase.GetAssetDependencyHash(fx.aPath);
            var pCurrentObjectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.pGuid, _target);
            Assume.That(pCurrentObjectIds, Is.Not.Empty,
                "P must produce non-empty GetPlayerObjectIdentifiersInAsset for the probe to be meaningful");

            // Synthesize a stale ObjectId: copy a real one's fileType +
            // filePath (so equality only fails on localId), but use a
            // localId not present in P's current result.
            var template = pCurrentObjectIds[0];
            const long staleLocalId = 999_999_999_999_999_999L;
            var pLocalIds = new HashSet<long>();
            foreach (var o in pCurrentObjectIds)
            {
                pLocalIds.Add(o.localIdentifierInFile);
            }
            Assume.That(pLocalIds.Contains(staleLocalId), Is.False,
                "synthetic stale localId must not collide with any real ObjectId in P's current set");

            var staleObjectId = SbpReflection.Instance.MakeObjectIdentifier(
                fx.pGuid, staleLocalId, template.fileType, template.filePath);

            var aRestored = MakeRestored(fx.aGuid, fx.aPath, aHashStored,
                included: Array.Empty<ObjectIdentifier>(),
                referenced: new[] { staleObjectId });
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [fx.aGuid] = aRestored },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);

            Assert.IsFalse(applied.Contains(fx.aGuid),
                "A must be rejected because its restored referenced_objects contain " +
                "an ObjectId at guid=P that does not exist in P's current " +
                "GetPlayerObjectIdentifiersInAsset. Without this, the stale ObjectId " +
                "reaches WriteSerializedFile and produces 'Required build Object N at " +
                "guid P is missing'.");
            Assert.IsFalse(dep.AssetInfo.ContainsKey(fx.aGuid),
                "A's stale AssetLoadInfo must NOT reach IDependencyData when drift is detected");
        }

        // Regression guard: when A's referenced ObjectId matches one
        // currently produced for P, the check must not over-reject.
        [Test]
        public void ApplyToContext_AReferencesOutOfPayloadGuidWithFreshObjectId_AApplied()
        {
            var fx = CreateTwoMaterials();
            var aHashStored = AssetDatabase.GetAssetDependencyHash(fx.aPath);
            var pCurrentObjectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.pGuid, _target);
            Assume.That(pCurrentObjectIds, Is.Not.Empty);

            var freshObjectId = pCurrentObjectIds[0];

            var aRestored = MakeRestored(fx.aGuid, fx.aPath, aHashStored,
                included: Array.Empty<ObjectIdentifier>(),
                referenced: new[] { freshObjectId });
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [fx.aGuid] = aRestored },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);

            Assert.IsTrue(applied.Contains(fx.aGuid),
                "Must not reject A when its referenced ObjectIds are all present in " +
                "the corresponding target guids' current GetPlayerObjectIdentifiersInAsset");
        }

        [Test]
        public void ApplyToContext_AReferencesDeletedOutOfPayloadGuid_ARejectedByOutOfPayloadObjectIdDriftCheck()
        {
            var fx = CreateTwoMaterials();
            var aHashStored = AssetDatabase.GetAssetDependencyHash(fx.aPath);
            var pCurrentObjectIds = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.pGuid, _target);
            Assume.That(pCurrentObjectIds, Is.Not.Empty);
            var capturedRefAtP = pCurrentObjectIds[0];

            Assert.IsTrue(AssetDatabase.DeleteAsset(fx.pPath));
            _createdPaths.Remove(fx.pPath);
            AssetDatabase.Refresh();
            Assume.That(AssetDatabase.GUIDToAssetPath(fx.pGuid.ToString()), Is.Empty);

            var aRestored = MakeRestored(fx.aGuid, fx.aPath, aHashStored,
                included: Array.Empty<ObjectIdentifier>(),
                referenced: new[] { capturedRefAtP });
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [fx.aGuid] = aRestored },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);

            Assert.IsFalse(applied.Contains(fx.aGuid),
                "A must be rejected because its restored referenced_objects point at " +
                "guid=P that no longer exists in the project. Null lookup result for a " +
                "non-existent project guid must NOT be conflated with built-in/Library " +
                "skip semantics — the latter still resolves via GUIDToAssetPath.");
            Assert.IsFalse(dep.AssetInfo.ContainsKey(fx.aGuid),
                "A's stale AssetLoadInfo must NOT reach IDependencyData when its " +
                "referenced guid has been removed from the project");
        }

        // Regression guard: A references a Unity built-in resource (e.g.
        // Library/unity default resources). DefaultObjectIdsLookup returns
        // null for these guids (path doesn't start with Assets/ or
        // Packages/), and the check treats null as "skip — resolved by
        // runtime". A must still be applied.
        [Test]
        public void ApplyToContext_AReferencesBuiltInGuid_AApplied()
        {
            var aPath = CreateMaterialAsset();
            var aGuid = new GUID(AssetDatabase.AssetPathToGUID(aPath));
            var aHashStored = AssetDatabase.GetAssetDependencyHash(aPath);

            // Well-known Unity built-in guid for "Library/unity default
            // resources". Any localId works since the check will skip
            // this guid entirely.
            var builtinGuid = new GUID("0000000000000000e000000000000000");
            Assume.That(
                AssetDatabase.GUIDToAssetPath(builtinGuid.ToString()).StartsWith("Library/"),
                Is.True,
                "this guid must resolve to a Library/ path for the test premise to hold");

            var builtinObjectId = SbpReflection.Instance.MakeObjectIdentifier(
                builtinGuid, 10202L, FileType.NonAssetType, "Library/unity default resources");

            var aRestored = MakeRestored(aGuid, aPath, aHashStored,
                included: Array.Empty<ObjectIdentifier>(),
                referenced: new[] { builtinObjectId });
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [aGuid] = aRestored },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup,
                currentObjectIdsLookup: RestoreBlobMetas.DefaultObjectIdsLookup);

            Assert.IsTrue(applied.Contains(aGuid),
                "References to built-in / Library / Resources guids must not trigger " +
                "rejection — those are resolved by the runtime at load time, not from " +
                "the project's build pool");
        }

        private struct TwoMatsFixture
        {
            public string aPath;
            public GUID aGuid;
            public string pPath;
            public GUID pGuid;
        }

        private TwoMatsFixture CreateTwoMaterials()
        {
            var aPath = CreateMaterialAsset();
            var pPath = CreateMaterialAsset();
            return new TwoMatsFixture
            {
                aPath = aPath,
                aGuid = new GUID(AssetDatabase.AssetPathToGUID(aPath)),
                pPath = pPath,
                pGuid = new GUID(AssetDatabase.AssetPathToGUID(pPath)),
            };
        }

        private string CreateMaterialAsset()
        {
            var standard = Shader.Find("Standard");
            Assume.That(standard, Is.Not.Null);
            var mat = new Material(standard) { color = Color.white };
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/__objectiddrift_material.mat");
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            _createdPaths.Add(path);
            return path;
        }

        private static RestoredAsset MakeRestored(
            GUID guid, string path, Hash128 hash,
            IReadOnlyList<ObjectIdentifier> included,
            IReadOnlyList<ObjectIdentifier> referenced)
        {
            return new RestoredAsset
            {
                LoadInfo = new AssetLoadInfo
                {
                    asset = guid,
                    address = path,
                    includedObjects = new List<ObjectIdentifier>(included),
                    referencedObjects = new List<ObjectIdentifier>(referenced),
                },
                Usage = new BuildUsageTagSet(),
                Extended = null,
                AssetDependencyHash = hash,
            };
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
