// PayloadDepDriftCheck: rejects an asset when any in-payload dependency it references failed OwnHashDriftCheck.

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
    public class RestoreBlobMetasPayloadDepDriftTests
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

        // P unmodified, M modified. P's referencedObjects contain ObjectIds from M.
        // Without the check: OwnHashDriftCheck sees only P's own hash → P
        // would be applied even though M is stale. With the check:
        // P is rejected because the transitive dependency M failed
        // OwnHashDriftCheck.
        [Test]
        public void ApplyToContext_PReferencesModifiedM_PRejectedByPayloadDepDriftCheck()
        {
            // (1) Real prefab P + real material M, where P's MeshRenderer
            //     references M. P's CBI referencedObjects will then contain
            //     ObjectIds whose guid == M.guid.
            var fx = CreatePrefabAndMaterial();
            var pStoredHash = AssetDatabase.GetAssetDependencyHash(fx.prefabPath);
            var mStoredHash = AssetDatabase.GetAssetDependencyHash(fx.matPath);
            var pIncluded = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var pReferenced = ContentBuildInterface.GetPlayerDependenciesForObjects(pIncluded, _target, null);

            var pReferencesM = false;
            foreach (var o in pReferenced)
            {
                if (o.guid == fx.matGuid) { pReferencesM = true; break; }
            }
            Assume.That(pReferencesM, Is.True,
                "fixture must wire P's referencedObjects to actually include ObjectIds from M");

            // (2) Mutate M only. P's bytes (and dependency hash) stay put.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(fx.matPath);
            mat.color = Color.red;
            AssetDatabase.SaveAssetIfDirty(mat);
            AssetDatabase.Refresh();
            Assume.That(AssetDatabase.GetAssetDependencyHash(fx.prefabPath), Is.EqualTo(pStoredHash),
                "P's own AssetDependencyHash must remain stable for this scenario");
            Assume.That(AssetDatabase.GetAssetDependencyHash(fx.matPath), Is.Not.EqualTo(mStoredHash),
                "M's AssetDependencyHash must change so OwnHashDriftCheck rejects M");

            // (3) Synth payload that mimics blob_meta captured BEFORE M's
            //     mutation. P carries the (now-stale) referencedObjects.
            var pRestored = MakeRestored(fx.prefabGuid, fx.prefabPath, pStoredHash, pIncluded, pReferenced);
            var mRestored = MakeRestored(fx.matGuid, fx.matPath, mStoredHash,
                Array.Empty<ObjectIdentifier>(), Array.Empty<ObjectIdentifier>());
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset>
                {
                    [fx.prefabGuid] = pRestored,
                    [fx.matGuid] = mRestored,
                },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup);

            Assert.IsFalse(applied.Contains(fx.matGuid),
                "precondition: M's stored hash mismatches current → OwnHashDriftCheck rejects M");
            Assert.IsFalse(applied.Contains(fx.prefabGuid),
                "PayloadDepDriftCheck: P must be rejected because its referencedObjects " +
                "depend on M, and M failed OwnHashDriftCheck. Without this, stale " +
                "referencedObjects reach WriteSerializedFile and produce " +
                "'Required build Object ... is missing'.");
            Assert.IsFalse(dep.AssetInfo.ContainsKey(fx.prefabGuid),
                "P's stale AssetLoadInfo must NOT reach IDependencyData when M is stale.");
        }

        // Both P and M unmodified → both must be applied. Catches an
        // overzealous PayloadDepDriftCheck that drops healthy entries.
        [Test]
        public void ApplyToContext_AllFresh_BothApplied()
        {
            var fx = CreatePrefabAndMaterial();
            var pStoredHash = AssetDatabase.GetAssetDependencyHash(fx.prefabPath);
            var mStoredHash = AssetDatabase.GetAssetDependencyHash(fx.matPath);
            var pIncluded = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var pReferenced = ContentBuildInterface.GetPlayerDependenciesForObjects(pIncluded, _target, null);

            var pRestored = MakeRestored(fx.prefabGuid, fx.prefabPath, pStoredHash, pIncluded, pReferenced);
            var mRestored = MakeRestored(fx.matGuid, fx.matPath, mStoredHash,
                Array.Empty<ObjectIdentifier>(), Array.Empty<ObjectIdentifier>());
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset>
                {
                    [fx.prefabGuid] = pRestored,
                    [fx.matGuid] = mRestored,
                },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup);

            Assert.IsTrue(applied.Contains(fx.matGuid));
            Assert.IsTrue(applied.Contains(fx.prefabGuid));
        }

        // P references GUIDs that are NOT in the payload (built-in shader,
        // Library default resources, anything out-of-build).
        // PayloadDepDriftCheck must let P through — only payload entries
        // whose OwnHashDriftCheck failed should propagate rejection.
        [Test]
        public void ApplyToContext_PReferencesOnlyOutOfBuildGuids_PApplied()
        {
            var fx = CreatePrefabAndMaterial();
            var pStoredHash = AssetDatabase.GetAssetDependencyHash(fx.prefabPath);
            var pIncluded = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var pReferenced = ContentBuildInterface.GetPlayerDependenciesForObjects(pIncluded, _target, null);

            // Payload contains ONLY P. M (and any built-in references in
            // pReferenced) are out-of-build from the payload's perspective.
            var pRestored = MakeRestored(fx.prefabGuid, fx.prefabPath, pStoredHash, pIncluded, pReferenced);
            var payload = new RestorePayload
            {
                AssetByGuid = new Dictionary<GUID, RestoredAsset> { [fx.prefabGuid] = pRestored },
                SceneByGuid = new Dictionary<GUID, RestoredScene>(),
                ObjectTypes = new List<KeyValuePair<ObjectIdentifier, Type[]>>()
            };

            var dep = new FakeDependencyData();
            var ext = new FakeBuildExtendedAssetData();

            var applied = _restorer.ApplyToContext(payload, dep, ext,
                currentHashLookup: RestoreBlobMetas.DefaultAssetHashLookup);

            Assert.IsTrue(applied.Contains(fx.prefabGuid),
                "GUIDs absent from the payload (built-in, Library, out-of-build) " +
                "must not cause transitive rejection of an otherwise-fresh asset.");
        }

        private struct PrefabMatFixture
        {
            public string prefabPath;
            public GUID prefabGuid;
            public string matPath;
            public GUID matGuid;
        }

        private PrefabMatFixture CreatePrefabAndMaterial()
        {
            var standard = Shader.Find("Standard");
            Assume.That(standard, Is.Not.Null, "Standard shader must be available");
            var mat = new Material(standard) { color = Color.white };
            var matPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__payloaddep_material.mat");
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            _createdPaths.Add(matPath);

            var go = new GameObject("PayloadDepRoot");
            try
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = GetCubeMesh();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__payloaddep_prefab.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                _createdPaths.Add(prefabPath);
                AssetDatabase.Refresh();

                return new PrefabMatFixture
                {
                    prefabPath = prefabPath,
                    prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath)),
                    matPath = matPath,
                    matGuid = new GUID(AssetDatabase.AssetPathToGUID(matPath)),
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static Mesh GetCubeMesh()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try { return temp.GetComponent<MeshFilter>().sharedMesh; }
            finally { UnityEngine.Object.DestroyImmediate(temp); }
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
