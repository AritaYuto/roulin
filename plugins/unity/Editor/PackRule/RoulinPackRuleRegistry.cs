using System;
using UnityEditor.AddressableAssets.Settings;

namespace Roulin.Editor.PackRule
{
    // Registration point for the project's IRoulinPackRule (register from an
    // [InitializeOnLoad] hook). No default: Resolve returns null when nothing
    // is registered, and the build script degrades to full rebuild.
    public static class RoulinPackRuleRegistry
    {
        private static Func<AddressableAssetSettings, IRoulinPackRule> s_factory;

        public static void Register(Func<AddressableAssetSettings, IRoulinPackRule> factory)
        {
            s_factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public static void Clear()
        {
            s_factory = null;
        }

        public static IRoulinPackRule Resolve(AddressableAssetSettings aas)
        {
            if (aas == null) throw new ArgumentNullException(nameof(aas));
            return s_factory?.Invoke(aas);
        }
    }
}
