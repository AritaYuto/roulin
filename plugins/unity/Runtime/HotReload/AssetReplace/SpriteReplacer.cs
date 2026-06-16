#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using UnityEngine;

namespace Roulin.HotReload
{
    // Pushes new pixel data into the underlying Texture2D; sprite rect / pivot
    // / uv stay frozen. Atlas-shared textures affect every sliced sprite.
    public sealed class SpriteReplacer : AssetReplacerBase<Sprite>
    {
        static readonly Texture2DReplacer s_TextureReplacer = new Texture2DReplacer();

        protected override bool TryReplaceTyped(Sprite oldObj, Sprite newObj)
        {
            if (oldObj.texture == null || newObj.texture == null)
            {
                Debug.LogError(
                    "[HotReloadController] SpriteReplacer: sprite has null texture " +
                    $"(old={oldObj.texture}, new={newObj.texture})");
                return false;
            }
            return s_TextureReplacer.TryReplace(null, oldObj.texture, newObj.texture);
        }
    }
}
#endif
