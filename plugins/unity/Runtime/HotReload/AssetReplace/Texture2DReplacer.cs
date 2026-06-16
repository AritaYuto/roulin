#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using UnityEngine;

namespace Roulin.HotReload
{
    public sealed class Texture2DReplacer : AssetReplacerBase<Texture2D>
    {
        protected override bool TryReplaceTyped(Texture2D oldObj, Texture2D newObj)
        {
            bool needsReshape =
                            oldObj.width != newObj.width ||
                            oldObj.height != newObj.height ||
                            oldObj.format != newObj.format ||
                            oldObj.mipmapCount != newObj.mipmapCount;

            if (needsReshape)
            {
                bool reinitSuccess;
                try
                {
                    reinitSuccess = oldObj.Reinitialize(newObj.width, newObj.height, newObj.format, newObj.mipmapCount > 1);
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[HotReloadController] Texture2D.Reinitialize threw: {e.Message} " +
                        $"(old {oldObj.width}x{oldObj.height} {oldObj.format}, " +
                        $"new {newObj.width}x{newObj.height} {newObj.format})");
                    return false;
                }

                bool reCreated = (oldObj.width == newObj.width) && (oldObj.height == newObj.height);
                if (!reinitSuccess || !reCreated)
                {
                    Debug.LogError(
                        "[HotReloadController] texture reshape unsuccessful — " +
                        "Texture2D.Reinitialize requires the destination texture to be readable. " +
                        "Enable 'Read/Write Enabled' in the texture's Import Settings, " +
                        "or keep the source asset's dimensions consistent across builds.\n" +
                        $"  post-Reinitialize: {oldObj.width}x{oldObj.height} {oldObj.format} mip={oldObj.mipmapCount} (isReadable={oldObj.isReadable})\n" +
                        $"  target:            {newObj.width}x{newObj.height} {newObj.format} mip={newObj.mipmapCount}");
                    return false;
                }

                // Apply() commits the GPU allocation post-Reinitialize; without
                // it CopyTexture errors with "destination not initialized on GPU".
                try
                {
                    oldObj.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[HotReloadController] Texture2D.Apply after Reinitialize failed: {e.Message}");
                    return false;
                }

                Debug.Log(
                    $"[HotReloadController] reshaped texture: now " +
                    $"{oldObj.width}x{oldObj.height} {oldObj.format} mip={oldObj.mipmapCount}");
            }

            Graphics.CopyTexture(newObj, oldObj);
            return true;
        }
    }
}
#endif
