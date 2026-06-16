#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using UnityEngine;

namespace Roulin.HotReload
{
    // SetData onto the old clip; instance ID preserved.
    // Limits: same frequency / channels / samples (SetData can't resize),
    // both clips need non-Streaming Load Type (otherwise GetData fails).
    public sealed class AudioClipReplacer : AssetReplacerBase<AudioClip>
    {
        protected override bool TryReplaceTyped(AudioClip oldClip, AudioClip newClip)
        {
            if (oldClip.frequency != newClip.frequency ||
                oldClip.channels  != newClip.channels  ||
                oldClip.samples   != newClip.samples)
            {
                Debug.LogError(
                    "[AudioClipReplacer] spec mismatch — AudioClip.SetData requires same " +
                    "frequency / channels / sample-count.\n" +
                    $"  old: {oldClip.frequency}Hz {oldClip.channels}ch samples={oldClip.samples}\n" +
                    $"  new: {newClip.frequency}Hz {newClip.channels}ch samples={newClip.samples}\n" +
                    "  Match the import settings (frequency / channels) and keep audio " +
                    "duration consistent across builds, or fall back to a baseline rebuild.");
                return false;
            }

            int total = newClip.samples * newClip.channels;
            var buf = new float[total];
            if (!newClip.GetData(buf, 0))
            {
                Debug.LogError(
                    $"[AudioClipReplacer] new clip GetData failed. Set Load Type to " +
                    $"'Decompress On Load' or 'Compressed In Memory' (= not Streaming).");
                return false;
            }
            if (!oldClip.SetData(buf, 0))
            {
                Debug.LogError(
                    $"[AudioClipReplacer] old clip SetData failed. The old clip may have " +
                    $"been created with streamingMode=true; rebuild baseline with non-streaming Load Type.");
                return false;
            }
            return true;
        }
    }
}
#endif
