// Probes whether GetPlayerDependenciesForObjects results at a guid are always a subset of GetPlayerObjectIdentifiersInAsset for that guid.

using NUnit.Framework;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEngine;

namespace Roulin.Editor.Tests
{
    public class ObjectIdReferenceInvariantProbeTests
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

        [Test]
        public void GetPlayerDependenciesForObjects_Deterministic_OnRepeatedCalls()
        {
            var fx = CreatePrefabReferencingMaterial();
            var included = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);

            var ref1 = ContentBuildInterface.GetPlayerDependenciesForObjects(included, _target, null);
            var ref2 = ContentBuildInterface.GetPlayerDependenciesForObjects(included, _target, null);

            LogObjectIds("ref call #1", ref1);
            LogObjectIds("ref call #2", ref2);

            CollectionAssert.AreEqual(ref1, ref2,
                "GetPlayerDependenciesForObjects is expected to be deterministic for identical inputs");
        }

        // Unity runs AssetDatabase.Refresh routinely during builds; if
        // Refresh can shift ObjectIds, blob_meta is in trouble even
        // without explicit edits.
        [Test]
        public void GetPlayerDependenciesForObjects_StableAcrossAssetDatabaseRefresh()
        {
            var fx = CreatePrefabReferencingMaterial();
            var included = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);

            var refBefore = ContentBuildInterface.GetPlayerDependenciesForObjects(included, _target, null);
            AssetDatabase.Refresh();
            var refAfter = ContentBuildInterface.GetPlayerDependenciesForObjects(included, _target, null);

            LogObjectIds("ref before Refresh", refBefore);
            LogObjectIds("ref after Refresh", refAfter);

            CollectionAssert.AreEqual(refBefore, refAfter,
                "AssetDatabase.Refresh without source mutation must not shift " +
                "GetPlayerDependenciesForObjects output");
        }

        // The core invariant. For every ObjectId o returned by
        // GetPlayerDependenciesForObjects(A.includedObjects) where
        // o.guid is a PROJECT-resident asset, o must appear in
        // GetPlayerObjectIdentifiersInAsset(o.guid).
        //
        // Built-in / Library guids (e.g. 0000000000000000e000000000000000
        // for "Library/unity default resources") are excluded:
        // GetPlayerObjectIdentifiersInAsset on them returns empty —
        // those references are resolved by the runtime at load time, not
        // from any project asset's build pool.
        //
        // If this invariant holds, referenced_objects in blob_meta is a
        // function of (asset contents, target only) and can be restored
        // safely as long as upstream content is unchanged.
        [Test]
        public void GetPlayerDependenciesForObjects_ReturnedObjectIdsAt_TargetGuid_AreSubsetOf_GetPlayerObjectIdentifiersInAssetOfTargetGuid()
        {
            var fx = CreatePrefabReferencingMaterialAndCustomMesh();
            var prefabIncluded = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var referenced = ContentBuildInterface.GetPlayerDependenciesForObjects(prefabIncluded, _target, null);

            LogObjectIds("prefab included", prefabIncluded);
            LogObjectIds("prefab referenced", referenced);

            var refsByGuid = GroupByGuid(referenced);
            TestContext.WriteLine($"distinct referenced guids: {refsByGuid.Count}");

            var checkedProjectGuids = 0;
            foreach (var entry in refsByGuid)
            {
                var depGuid = entry.Key;
                var objectIdsAtDepGuid = entry.Value;

                if (depGuid == fx.prefabGuid)
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(depGuid.ToString());
                if (!IsProjectAssetPath(path))
                {
                    TestContext.WriteLine(
                        $"---- SKIPPED (non-project) depGuid={depGuid} path='{path}' " +
                        $"objectIds_from_GetPlayerDependenciesForObjects={objectIdsAtDepGuid.Count}");
                    continue;
                }

                TestContext.WriteLine(
                    $"---- depGuid={depGuid} path='{path}' " +
                    $"objectIds_from_GetPlayerDependenciesForObjects={objectIdsAtDepGuid.Count}");
                LogObjectIds("  from GetPlayerDependenciesForObjects", objectIdsAtDepGuid.ToArray());

                var currentSet = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(depGuid, _target);
                LogObjectIds("  GetPlayerObjectIdentifiersInAsset(dep)", currentSet);
                var asSet = new HashSet<ObjectIdentifier>(currentSet);

                foreach (var objectId in objectIdsAtDepGuid)
                {
                    Assert.IsTrue(asSet.Contains(objectId),
                        $"INVARIANT VIOLATION: GetPlayerDependenciesForObjects returned ObjectId " +
                        $"localId={objectId.localIdentifierInFile} fileType={objectId.fileType} " +
                        $"filePath='{objectId.filePath}' at guid={depGuid} path='{path}' " +
                        $"(project asset), but GetPlayerObjectIdentifiersInAsset({depGuid}) does NOT " +
                        $"include it. Implication: referenced_objects cannot be reliably " +
                        $"reconstructed from GetPlayerObjectIdentifiersInAsset alone, so a stored " +
                        $"referenced ObjectId may never appear in any future build pool even if the " +
                        $"dependency's content hash is unchanged.");
                }

                checkedProjectGuids++;
            }

            Assume.That(checkedProjectGuids, Is.GreaterThan(0),
                "fixture must produce at least one project-resident referenced guid for the " +
                "invariant check to be meaningful — otherwise the test passes trivially");
        }

        // Relaxed form: maybe the strict set returns ObjectIds that are
        // representations rather than main-included objects. Union with
        // GetPlayerAssetRepresentations and re-check.
        [Test]
        public void GetPlayerDependenciesForObjects_ReturnedObjectIdsAt_TargetGuid_AreSubsetOf_GetPlayerObjectIdentifiersInAssetPlusRepresentations()
        {
            var fx = CreatePrefabReferencingMaterialAndCustomMesh();
            var prefabIncluded = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var referenced = ContentBuildInterface.GetPlayerDependenciesForObjects(prefabIncluded, _target, null);

            var refsByGuid = GroupByGuid(referenced);

            var checkedProjectGuids = 0;
            foreach (var entry in refsByGuid)
            {
                var depGuid = entry.Key;
                if (depGuid == fx.prefabGuid)
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(depGuid.ToString());
                if (!IsProjectAssetPath(path))
                {
                    TestContext.WriteLine($"---- SKIPPED (non-project) depGuid={depGuid} path='{path}'");
                    continue;
                }

                var current = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(depGuid, _target);
                var reps = ContentBuildInterface.GetPlayerAssetRepresentations(depGuid, _target);
                var union = new HashSet<ObjectIdentifier>(current);
                if (reps != null)
                {
                    foreach (var r in reps) union.Add(r);
                }

                TestContext.WriteLine(
                    $"---- depGuid={depGuid} path='{path}' " +
                    $"current={current.Length} reps={(reps?.Length ?? 0)}");

                foreach (var objectId in entry.Value)
                {
                    Assert.IsTrue(union.Contains(objectId),
                        $"RELAXED INVARIANT VIOLATION: ObjectId localId={objectId.localIdentifierInFile} " +
                        $"at guid={depGuid} path='{path}' (project asset) is in NEITHER " +
                        $"GetPlayerObjectIdentifiersInAsset NOR representations. Implication: there " +
                        $"exists a third source of valid build-pool ObjectIds that we haven't " +
                        $"accounted for — storing or recomputing alone won't be enough.");
                }

                checkedProjectGuids++;
            }

            Assume.That(checkedProjectGuids, Is.GreaterThan(0),
                "fixture must produce at least one project-resident referenced guid for the " +
                "relaxed invariant check to be meaningful");
        }

        // Excludes built-in resource roots (default cube mesh, default
        // material, etc.) and unknown/empty paths.
        private static bool IsProjectAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (path.StartsWith("Assets/", System.StringComparison.Ordinal))
            {
                return true;
            }

            if (path.StartsWith("Packages/", System.StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        // Sizes the cost of "recompute referenced_objects on every
        // restored asset" honestly.
        [Test]
        public void GetPlayerDependenciesForObjects_FanOut_BaselineMagnitude()
        {
            var fx = CreatePrefabReferencingMaterial();
            var included = ContentBuildInterface.GetPlayerObjectIdentifiersInAsset(fx.prefabGuid, _target);
            var referenced = ContentBuildInterface.GetPlayerDependenciesForObjects(included, _target, null);

            var refsByGuid = GroupByGuid(referenced);
            TestContext.WriteLine(
                $"fan-out: included={included.Length} referenced_total={referenced.Length} " +
                $"distinct_referenced_guids={refsByGuid.Count}");
            Assert.IsTrue(referenced.Length >= 0, "sanity");
        }

        private static Dictionary<GUID, List<ObjectIdentifier>> GroupByGuid(ObjectIdentifier[] objectIds)
        {
            var grouped = new Dictionary<GUID, List<ObjectIdentifier>>();
            foreach (var o in objectIds)
            {
                if (!grouped.TryGetValue(o.guid, out var list))
                {
                    list = new List<ObjectIdentifier>();
                    grouped[o.guid] = list;
                }
                list.Add(o);
            }
            return grouped;
        }

        private static void LogObjectIds(string label, ObjectIdentifier[] objectIds)
        {
            TestContext.WriteLine($"[{label}] count={objectIds.Length}");
            for (int i = 0; i < objectIds.Length; i++)
            {
                var o = objectIds[i];
                TestContext.WriteLine(
                    $"  [{i}] guid={o.guid} localId={o.localIdentifierInFile} " +
                    $"fileType={o.fileType} filePath='{o.filePath}'");
            }
        }

        private static void LogObjectIds(string label, IReadOnlyList<ObjectIdentifier> objectIds)
        {
            TestContext.WriteLine($"[{label}] count={objectIds.Count}");
            for (int i = 0; i < objectIds.Count; i++)
            {
                var o = objectIds[i];
                TestContext.WriteLine(
                    $"  [{i}] guid={o.guid} localId={o.localIdentifierInFile} " +
                    $"fileType={o.fileType} filePath='{o.filePath}'");
            }
        }

        private struct PrefabMatFixture
        {
            public string prefabPath;
            public GUID prefabGuid;
            public string matPath;
            public GUID matGuid;
            public string meshPath;
            public GUID meshGuid;
        }

        // Builds a prefab that references TWO project-resident assets
        // (Material + custom Mesh) so the invariant tests have meaningful
        // coverage. The custom mesh replaces the previous primitive-cube
        // approach, which pulled in Library/unity default resources and
        // gave the dependency call nothing project-resident to validate.
        private PrefabMatFixture CreatePrefabReferencingMaterialAndCustomMesh()
        {
            var standard = Shader.Find("Standard");
            Assume.That(standard, Is.Not.Null);
            var mat = new Material(standard) { color = Color.white };
            var matPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__objectid_invariant_material.mat");
            AssetDatabase.CreateAsset(mat, matPath);
            _createdPaths.Add(matPath);

            var mesh = BuildCustomTriangleMesh();
            var meshPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__objectid_invariant_mesh.asset");
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();
            _createdPaths.Add(meshPath);

            var go = new GameObject("ObjectIdInvariantRoot");
            try
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__objectid_invariant_prefab.prefab");
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                _createdPaths.Add(prefabPath);
                AssetDatabase.Refresh();

                return new PrefabMatFixture
                {
                    prefabPath = prefabPath,
                    prefabGuid = new GUID(AssetDatabase.AssetPathToGUID(prefabPath)),
                    matPath = matPath,
                    matGuid = new GUID(AssetDatabase.AssetPathToGUID(matPath)),
                    meshPath = meshPath,
                    meshGuid = new GUID(AssetDatabase.AssetPathToGUID(meshPath)),
                };
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // Single-triangle mesh AssetDatabase can persist as .asset. We
        // only care that GetPlayerObjectIdentifiersInAsset returns a
        // non-empty set for the saved guid.
        private static Mesh BuildCustomTriangleMesh()
        {
            var mesh = new Mesh
            {
                name = "ObjectIdInvariantTriangle",
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
                triangles = new[] { 0, 1, 2 }
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Retained for the sanity / determinism / Refresh / fan-out tests
        // which don't care whether the mesh is built-in.
        private PrefabMatFixture CreatePrefabReferencingMaterial()
            => CreatePrefabReferencingMaterialAndCustomMesh();
    }
}
