#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using UnityEngine;

namespace Roulin.HotReload
{
    // Clears destination then writes new vertex/index/channel data. Instance
    // ID is preserved so MeshFilter / SkinnedMeshRenderer refs stay valid.
    // Both meshes must have Read/Write Enabled. Bone weights / bind poses
    // not copied (skinned mesh hot reload unsafe with naive copy).
    public sealed class MeshReplacer : AssetReplacerBase<Mesh>
    {
        protected override bool TryReplaceTyped(Mesh oldMesh, Mesh newMesh)
        {
            if (!newMesh.isReadable)
            {
                Debug.LogError(
                    "[MeshReplacer] new mesh isReadable=false — enable Read/Write Enabled " +
                    "on the model's Import Settings to allow hot reload.");
                return false;
            }
            if (!oldMesh.isReadable)
            {
                Debug.LogError(
                    "[MeshReplacer] old mesh isReadable=false — enable Read/Write Enabled " +
                    "on the model's Import Settings to allow hot reload.");
                return false;
            }

            oldMesh.Clear(keepVertexLayout: false);
            oldMesh.indexFormat = newMesh.indexFormat;

            oldMesh.SetVertices(newMesh.vertices);

            var normals  = newMesh.normals;   if (normals.Length  > 0) oldMesh.SetNormals(normals);
            var tangents = newMesh.tangents;  if (tangents.Length > 0) oldMesh.SetTangents(tangents);
            var colors   = newMesh.colors;    if (colors.Length   > 0) oldMesh.SetColors(colors);

            // UV 0–3 only; 4–7 uncommon enough to skip.
            var uv0 = newMesh.uv;  if (uv0.Length > 0) oldMesh.SetUVs(0, uv0);
            var uv1 = newMesh.uv2; if (uv1.Length > 0) oldMesh.SetUVs(1, uv1);
            var uv2 = newMesh.uv3; if (uv2.Length > 0) oldMesh.SetUVs(2, uv2);
            var uv3 = newMesh.uv4; if (uv3.Length > 0) oldMesh.SetUVs(3, uv3);

            // SetTriangles auto-grows subMeshCount.
            int subMeshCount = newMesh.subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
                oldMesh.SetTriangles(newMesh.GetTriangles(i), i);

            oldMesh.RecalculateBounds();
            return true;
        }
    }
}
#endif
