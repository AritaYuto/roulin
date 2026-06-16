#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using UnityEngine;

namespace Roulin.HotReload
{
    // JsonUtility round-trip from newSO onto oldSO. Covers any user SO type
    // for the standard serialisation surface ([SerializeField] + public fields).
    // ObjectRefs survive across bundles but may null when referencing same-bundle
    // assets (those instances are destroyed by Replace's Unload(true)).
    public sealed class ScriptableObjectReplacer : AssetReplacerBase<ScriptableObject>
    {
        protected override bool TryReplaceTyped(ScriptableObject oldSO, ScriptableObject newSO)
        {
            if (oldSO.GetType() != newSO.GetType())
            {
                Debug.LogError(
                    $"[ScriptableObjectReplacer] type mismatch — old={oldSO.GetType().Name}, " +
                    $"new={newSO.GetType().Name}; skipping");
                return false;
            }

            try
            {
                string json = JsonUtility.ToJson(newSO);
                JsonUtility.FromJsonOverwrite(json, oldSO);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[ScriptableObjectReplacer] copy failed for {oldSO.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
#endif
