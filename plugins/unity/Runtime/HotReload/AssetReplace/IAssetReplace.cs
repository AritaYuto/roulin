#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG

namespace Roulin.HotReload
{
    // Strategy interface for type-specific in-place replacement. Return true
    // when handled; false lets the next replacer try. `address` is consumed
    // only by replacers that need to find live instances (e.g. PrefabReplacer).
    public interface IAssetReplacer
    {
        bool TryReplace(string address, UnityEngine.Object oldObj, UnityEngine.Object newObj);
    }

    // Convenience base for "I handle exactly type T"; ignores address.
    public abstract class AssetReplacerBase<T> : IAssetReplacer where T : UnityEngine.Object
    {
        public bool TryReplace(string address, UnityEngine.Object oldObj, UnityEngine.Object newObj)
        {
            if (oldObj is not T typedOld) return false;
            if (newObj is not T typedNew) return false;
            return TryReplaceTyped(typedOld, typedNew);
        }

        protected abstract bool TryReplaceTyped(T oldObj, T newObj);
    }
}

#endif
