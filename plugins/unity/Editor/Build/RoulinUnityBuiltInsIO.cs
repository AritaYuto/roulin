using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Roulin.Editor.Build
{
    // Editor asmdef only; Runtime counterpart needed so SBP's Player-target walk recognises the asset type.
    public static class RoulinUnityBuiltInsIO
    {
        // Fixed GUID for unity_builtin_extra — the file where built-in Shaders live.
        private const string BuiltInExtraGuidHex = "0000000000000000f000000000000000";

        // Defense against a previous crashed build leaving the asset behind.
        public static void DeleteAssetIfExists()
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(RoulinUnityBuiltIns.AssetPath) != null)
            {
                AssetDatabase.DeleteAsset(RoulinUnityBuiltIns.AssetPath);
            }
        }

        public static Populated CreateFresh()
        {
            DeleteAssetIfExists();

            var monoScripts    = CollectAllMonoScripts();
            var builtInShaders = CollectAllBuiltInShaders();

            var asset = ScriptableObject.CreateInstance<RoulinUnityBuiltIns>();
            asset.MonoScripts    = monoScripts;
            asset.BuiltInShaders = builtInShaders;

            AssetDatabase.CreateAsset(asset, RoulinUnityBuiltIns.AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new Populated(monoScripts.Length, builtInShaders.Length);
        }

        public readonly struct Populated
        {
            public readonly int MonoScriptCount;
            public readonly int BuiltInShaderCount;

            public Populated(int monoScriptCount, int builtInShaderCount)
            {
                MonoScriptCount    = monoScriptCount;
                BuiltInShaderCount = builtInShaderCount;
            }
        }

        private static UnityEngine.Object[] CollectAllMonoScripts()
        {
            // Mirrors SBP's CreateMonoScriptBundle filter: only MonoBehaviour/ScriptableObject in Player-target assemblies.
            var playerAssemblyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                playerAssemblyNames.Add(assembly.name);
            }

            var guids   = AssetDatabase.FindAssets("t:MonoScript");
            var scripts = new UnityEngine.Object[guids.Length];
            var count   = 0;
            foreach (var guidStr in guids)
            {
                var path   = AssetDatabase.GUIDToAssetPath(guidStr);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null)
                {
                    continue;
                }

                var scriptClass = script.GetClass();
                if (scriptClass == null)
                {
                    continue;
                }

                if (!typeof(MonoBehaviour).IsAssignableFrom(scriptClass)
                    && !typeof(ScriptableObject).IsAssignableFrom(scriptClass))
                {
                    continue;
                }

                if (!playerAssemblyNames.Contains(scriptClass.Assembly.GetName().Name))
                {
                    continue;
                }

                scripts[count++] = script;
            }
            Array.Resize(ref scripts, count);
            return scripts;
        }

        private static Shader[] CollectAllBuiltInShaders()
        {
            var loaded = Resources.FindObjectsOfTypeAll<Shader>();
            var kept   = new Shader[loaded.Length];
            var count  = 0;
            foreach (var shader in loaded)
            {
                if (shader == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader, out string guid, out long _))
                    continue;
                if (guid != BuiltInExtraGuidHex) continue;
                kept[count++] = shader;
            }
            Array.Resize(ref kept, count);
            return kept;
        }
    }
}
