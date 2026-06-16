#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Roulin.HotReload
{
    // Captures old texture refs → CopyPropertiesFromMaterial → restores refs.
    // Restore is required because newMat's textures live in the freshly-loaded
    // bundle that Replace's Unload(true) destroys; without restore oldMat
    // would dangle and render black. Texture-ref edits don't propagate here —
    // hot reload the texture asset directly instead.
    public sealed class MaterialReplacer : AssetReplacerBase<Material>
    {
        protected override bool TryReplaceTyped(Material oldMat, Material newMat)
        {
            var savedTextures = CaptureTextures(oldMat);

            oldMat.CopyPropertiesFromMaterial(newMat);

            // Shader may have changed; SetTexture silently drops unknown names.
            RestoreTextures(oldMat, savedTextures);

            return true;
        }

        static Dictionary<int, Texture> CaptureTextures(Material mat)
        {
            var dict = new Dictionary<int, Texture>();
            var shader = mat.shader;
            if (shader == null) return dict;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                int id = shader.GetPropertyNameId(i);
                dict[id] = mat.GetTexture(id);
            }
            return dict;
        }

        static void RestoreTextures(Material mat, Dictionary<int, Texture> saved)
        {
            foreach (var kv in saved)
                mat.SetTexture(kv.Key, kv.Value);
        }
    }
}
#endif
