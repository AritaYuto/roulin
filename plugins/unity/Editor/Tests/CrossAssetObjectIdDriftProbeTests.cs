// Probes whether upstream asset mutations drift a byte-unchanged prefab's GetPlayerObjectIdentifiersInAsset output.

using NUnit.Framework;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Roulin.Editor.Tests
{
    public class CrossAssetObjectIdDriftProbeTests
    {
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

        // Two consecutive calls with no mutation must return identical
        // ObjectIdentifier[]. If this fails, the rest of the probe is
        // meaningless.
        [Test]
        public void Baseline_RepeatedCalls_ReturnIdenticalObjectIds()
        {
            var path = CreatePrefabWithMeshRendererAndNewMaterial(out _, out _);
            var guid = new GUID(AssetDatabase.AssetPathToGUID(path));

            var objectIds1 = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, _target);
            var objectIds2 = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(guid, _target);

            LogObjectIds("baseline call #1", objectIds1);
            LogObjectIds("baseline call #2", objectIds2);

            CollectionAssert.AreEqual(objectIds1, objectIds2,
                "GetPlayerObjectIdentifiersInAsset is expected to be deterministic for " +
                "repeated calls on the same asset state");
        }

        // Edit only the referenced Material's color. The prefab is
        // byte-unchanged. Does AssetDependencyHash change? Do ObjectIds
        // drift?
        [Test]
        public void UpstreamMaterialColorChange_DoesNotDriftPrefabObjectIds()
        {
            var prefabPath = CreatePrefabWithMeshRendererAndNewMaterial(out _, out var matPath);
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));

            var hashBefore = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var before = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            mat.color = Color.red;
            AssetDatabase.SaveAssetIfDirty(mat);
            AssetDatabase.Refresh();

            var hashAfter = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var after = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogScenario("upstream material color edit", prefabPath,
                hashBefore, hashAfter, before, after);

            Assert.AreEqual(hashBefore, hashAfter,
                "upstream material color change is expected to leave prefab " +
                "AssetDependencyHash untouched — if this fails, OwnHashDriftCheck would " +
                "actually catch this scenario after all");
            CollectionAssert.AreEqual(before, after,
                "upstream material color change is expected to NOT drift prefab " +
                "ObjectIds — if this fails, OutOfPayloadObjectIdDriftCheck (per-ObjectId " +
                "re-query) would catch this real-world scenario");
        }

        // Replace the material's shader (Standard -> Unlit/Color). Prefab
        // itself is byte-unchanged.
        [Test]
        public void UpstreamMaterialShaderSwap_DoesNotDriftPrefabObjectIds()
        {
            var prefabPath = CreatePrefabWithMeshRendererAndNewMaterial(out _, out var matPath);
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));

            var hashBefore = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var before = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            var unlit = Shader.Find("Unlit/Color");
            Assume.That(unlit, Is.Not.Null, "Unlit/Color shader must be available for this probe");
            mat.shader = unlit;
            AssetDatabase.SaveAssetIfDirty(mat);
            AssetDatabase.Refresh();

            var hashAfter = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var after = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogScenario("upstream material shader swap", prefabPath,
                hashBefore, hashAfter, before, after);

            Assert.AreEqual(hashBefore, hashAfter,
                "upstream material shader swap is expected to leave prefab " +
                "AssetDependencyHash untouched — if this fails, OwnHashDriftCheck would " +
                "catch the scenario");
            CollectionAssert.AreEqual(before, after,
                "upstream material shader swap is expected to NOT drift prefab " +
                "ObjectIds — if this fails, OutOfPayloadObjectIdDriftCheck is the viable " +
                "detector");
        }

        // Prefab-level edit (sharedMaterial points to a different
        // Material). The prefab itself changes byte-wise, so we expect
        // BOTH hash and ObjectIds to potentially change. Sanity-check
        // that our mutation pipeline is wired correctly.
        [Test]
        public void PrefabLevelMaterialReferenceReplaced_ChangesHashAndPossiblyObjectIds()
        {
            var prefabPath = CreatePrefabWithMeshRendererAndNewMaterial(out _, out _);
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));
            var secondMatPath = CreateUniqueMaterialAsset();

            var hashBefore = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var before = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            var renderer = loaded.GetComponentInChildren<MeshRenderer>();
            Assume.That(renderer, Is.Not.Null, "prefab fixture must contain a MeshRenderer");
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(secondMatPath);
            PrefabUtility.SaveAsPrefabAsset(loaded, prefabPath);
            PrefabUtility.UnloadPrefabContents(loaded);
            AssetDatabase.Refresh();

            var hashAfter = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var after = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogScenario("prefab-level material reference replaced", prefabPath,
                hashBefore, hashAfter, before, after);

            Assert.AreNotEqual(hashBefore, hashAfter,
                "swapping the prefab's sharedMaterial is a prefab-level edit and MUST " +
                "change AssetDependencyHash — if this fails, our mutation pipeline is not " +
                "actually saving the change");
        }

        // Add a Component to the prefab. Prefab itself changes byte-wise.
        // Establishes the upper bound: both hash and ObjectIds should
        // shift. Confirms test infrastructure produces visible drift.
        [Test]
        public void DirectPrefabEdit_AddComponent_ChangesHashAndObjectIds()
        {
            var prefabPath = CreatePrefabWithMeshRendererAndNewMaterial(out _, out _);
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));

            var hashBefore = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var before = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            loaded.AddComponent<SphereCollider>();
            PrefabUtility.SaveAsPrefabAsset(loaded, prefabPath);
            PrefabUtility.UnloadPrefabContents(loaded);
            AssetDatabase.Refresh();

            var hashAfter = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var after = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogScenario("direct prefab edit (AddComponent SphereCollider)", prefabPath,
                hashBefore, hashAfter, before, after);

            Assert.AreNotEqual(hashBefore, hashAfter,
                "direct prefab edit MUST change AssetDependencyHash — this is the " +
                "OwnHashDriftCheck-catchable case");
            Assert.AreNotEqual(before.Length, 0, "baseline ObjectIds must be non-empty");
            Assert.IsTrue(after.Length >= before.Length,
                "adding a Component should produce at least as many ObjectIds as before");
        }

        // Delete the referenced material. The prefab's serialized bytes
        // remain intact (the reference becomes a dangling FileID).
        [Test]
        public void UpstreamMaterialDeleted_DoesNotDriftPrefabObjectIds()
        {
            var prefabPath = CreatePrefabWithMeshRendererAndNewMaterial(out _, out var matPath);
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));

            var hashBefore = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var before = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            // Remove from tracked list so TearDown does not try to
            // re-delete it.
            _createdPaths.Remove(matPath);
            Assert.IsTrue(AssetDatabase.DeleteAsset(matPath),
                "upstream material delete must succeed for this probe");
            AssetDatabase.Refresh();

            var hashAfter = AssetDatabase.GetAssetDependencyHash(prefabPath);
            var after = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogScenario("upstream material deleted", prefabPath,
                hashBefore, hashAfter, before, after);

            CollectionAssert.AreEqual(before, after,
                "deleting an upstream material is expected to NOT drift prefab " +
                "ObjectIds — if this fails, OutOfPayloadObjectIdDriftCheck would catch " +
                "the scenario");
        }

        // Sanity probe that the ObjectId set is meaningful for a prefab
        // carrying a custom MonoBehaviour with a serialized field. The
        // script source cannot be modified in-process, so this is
        // baseline only.
        [Test]
        public void PrefabWithCustomMonoBehaviour_BaselineObjectIdsAreStable()
        {
            var prefabPath = CreatePrefabWithMonoBehaviour();
            var prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));

            var objectIds1 = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);
            var objectIds2 = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(prefabGuid, _target);

            LogObjectIds("MB prefab call #1", objectIds1);
            LogObjectIds("MB prefab call #2", objectIds2);

            CollectionAssert.AreEqual(objectIds1, objectIds2,
                "prefab carrying a custom MonoBehaviour should return deterministic " +
                "ObjectIds across calls");
            Assert.IsTrue(objectIds1.Length >= 2,
                "expected at least GameObject + Transform + ProbeBehaviour ObjectIds on this prefab");
        }

        // Creates a prefab with a MeshRenderer pointing at a freshly-
        // created Material asset. Out-params return the GUID and the
        // material asset path so individual tests can mutate the
        // upstream material directly.
        private string CreatePrefabWithMeshRendererAndNewMaterial(out GUID prefabGuid, out string materialPath)
        {
            materialPath = CreateUniqueMaterialAsset();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);

            var go = new GameObject("CrossObjectIdProbeRoot");
            try
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = GetDefaultCubeMesh();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;

                var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__cross_objectid_probe_prefab.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                _createdPaths.Add(prefabPath);
                prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath));
                return prefabPath;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private string CreatePrefabWithMonoBehaviour()
        {
            var go = new GameObject("CrossObjectIdProbeMBRoot");
            try
            {
                go.AddComponent<ProbeBehaviour>();
                var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__cross_objectid_probe_mb_prefab.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                _createdPaths.Add(prefabPath);
                return prefabPath;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private string CreateUniqueMaterialAsset()
        {
            var standard = Shader.Find("Standard");
            Assume.That(standard, Is.Not.Null, "Standard shader must be available to seed material fixtures");
            var mat = new Material(standard) { color = Color.white };
            var matPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__cross_objectid_probe_material.mat");
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            _createdPaths.Add(matPath);
            return matPath;
        }

        // PrimitiveType returns a known mesh without committing a fixture
        // FBX file.
        private static Mesh GetDefaultCubeMesh()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                return temp.GetComponent<MeshFilter>().sharedMesh;
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }
        }

        private static void LogObjectIds(string label, ObjectIdentifier[] objectIds)
        {
            TestContext.WriteLine($"[{label}] count={objectIds.Length}");
            for (int i = 0; i < objectIds.Length; i++)
            {
                var o = objectIds[i];
                TestContext.WriteLine(
                    $"  [{i}] guid={o.guid} localId={o.localIdentifierInFile} fileType={o.fileType} filePath={o.filePath}");
            }
        }

        // Two-table-style summary so the Test Runner output answers, at
        // a glance: { mutation, hashChanged?, objectIdsChanged? }
        private static void LogScenario(
            string scenario,
            string prefabPath,
            Hash128 hashBefore,
            Hash128 hashAfter,
            ObjectIdentifier[] before,
            ObjectIdentifier[] after)
        {
            var hashChanged = hashBefore != hashAfter;
            var objectIdsChanged = !ObjectIdsEqual(before, after);

            TestContext.WriteLine("==== scenario: " + scenario + " ====");
            TestContext.WriteLine("prefab path: " + prefabPath);
            TestContext.WriteLine($"hashBefore = {hashBefore}");
            TestContext.WriteLine($"hashAfter  = {hashAfter}");
            TestContext.WriteLine($"hashChanged = {hashChanged}    objectIdsChanged = {objectIdsChanged}");
            TestContext.WriteLine("--- summary ---");
            TestContext.WriteLine($"| {scenario,-50} | hashChanged={hashChanged,-5} | objectIdsChanged={objectIdsChanged,-5} |");
            TestContext.WriteLine("--- ObjectIds ---");
            LogObjectIds("before", before);
            LogObjectIds("after ", after);
        }

        private static bool ObjectIdsEqual(ObjectIdentifier[] a, ObjectIdentifier[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }
            return true;
        }
    }

    // Minimal MonoBehaviour with a serialized field, used by the
    // MonoBehaviour-prefab probe. Declared at namespace scope so Unity's
    // serializer treats it as a real script type rather than a nested-
    // in-test workaround.
    internal sealed class ProbeBehaviour : MonoBehaviour
    {
        [SerializeField] private int _probeField;
    }
}
