using UnityEngine;

namespace Roulin
{
    // Proxy asset added to SBP input so CreateMonoScriptBundle / CreateBuiltInShadersBundle pack the full project-wide superset
    // instead of only the delta bundles' subset.
    public sealed class RoulinUnityBuiltIns : ScriptableObject
    {
        public const string AssetPath = "Assets/RoulinUnityBuiltIns.asset";
        public const string BundleName = "roulin_unity_built_ins";

        // MonoScript is UnityEditor-only; store as Object[] to keep this asmdef Runtime-safe.
        [SerializeField] private Object[] _monoScripts;
        [SerializeField] private Shader[] _builtInShaders;

        public Object[] MonoScripts
        {
            get => _monoScripts;
            set => _monoScripts = value;
        }

        public Shader[] BuiltInShaders
        {
            get => _builtInShaders;
            set => _builtInShaders = value;
        }
    }
}
