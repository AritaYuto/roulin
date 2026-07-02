using System;
using UnityEditor.AddressableAssets.Settings;

namespace Roulin.Editor.PackRule
{
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
