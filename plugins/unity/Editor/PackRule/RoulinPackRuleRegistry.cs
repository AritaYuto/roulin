using System;
using UnityEditor.AddressableAssets.Settings;

namespace Roulin.Editor.PackRule
{
    // Global registration point for the IRoulinPackRule instance the Roulin
    // build script should consult. Projects register their pack rule once
    // from an [InitializeOnLoad] hook.
    //
    // No default is provided: Resolve returns null when nothing is registered,
    // and the build script degrades to full rebuild in that case. A future
    // "Roulin-supplied group config" implementation would land here as the
    // default; until then, projects that want incremental builds must supply
    // their own IRoulinPackRule.
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

        // Returns the registered pack rule (constructed via factory) or null
        // when nothing is registered.
        public static IRoulinPackRule Resolve(AddressableAssetSettings aas)
        {
            if (aas == null) throw new ArgumentNullException(nameof(aas));
            return s_factory?.Invoke(aas);
        }
    }
}
