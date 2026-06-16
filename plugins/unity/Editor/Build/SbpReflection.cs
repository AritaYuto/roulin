using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace Roulin.Editor.Build
{
    // Reflection access to the Unity / SBP private surface used by blob_meta
    // capture and restore: ObjectIdentifier ctor, BuildUsageTagSet (de)serialise,
    // BuildCacheUtility.SetTypeForObjects, SceneDependencyInfo fields.
    // Ctor probes once at startup; missing critical surface throws there.
    public sealed class SbpReflection
    {
        public static readonly SbpReflection Instance = new();

        // BuildUsageTagGlobal field names vary across Unity versions; resolved by name.
        private readonly Dictionary<string, FieldInfo> _buildUsageTagGlobalFields;

        // (De)serialisation variants. Optional — null falls back to empty bytes.
        private readonly Func<BuildUsageTagSet, byte[]> _buildUsageTagSetSerializer;
        private readonly Action<BuildUsageTagSet, byte[]> _buildUsageTagSetDeserializer;

        // ObjectIdentifier private fields, identified by type (one of each).
        private readonly FieldInfo _objectIdGuidField;
        private readonly FieldInfo _objectIdLocalIdField;
        private readonly FieldInfo _objectIdFileTypeField;
        private readonly FieldInfo _objectIdFilePathField;

        // BuildCacheUtility.SetTypeForObjects + internal ObjectTypes struct.
        // Warms m_ObjectToType (per-build static type cache).
        private readonly ConstructorInfo _objectTypesCtor;
        private readonly Type _objectTypesType;
        private readonly MethodInfo _setTypeForObjectsMethod;

        // SceneDependencyInfo private fields: m_Scene, m_ReferencedObjects,
        // m_IncludedTypes, m_GlobalUsage. Type-unique within the struct.
        private readonly FieldInfo _sceneInfoSceneField;
        private readonly FieldInfo _sceneInfoReferencedObjectsField;
        private readonly FieldInfo _sceneInfoIncludedTypesField;
        private readonly FieldInfo _sceneInfoGlobalUsageField;

        private SbpReflection()
        {
            var sbpEditorAssembly = typeof(IBuildExtendedAssetData).Assembly;
            var log = new StringBuilder("[SbpReflection] probe: ");


            const BindingFlags fieldFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in typeof(ObjectIdentifier).GetFields(fieldFlags))
            {
                if (_objectIdGuidField == null && f.FieldType == typeof(GUID))
                {
                    _objectIdGuidField = f;
                }
                else if (_objectIdLocalIdField == null && f.FieldType == typeof(long))
                {
                    _objectIdLocalIdField = f;
                }
                else if (_objectIdFileTypeField == null && f.FieldType == typeof(FileType))
                {
                    _objectIdFileTypeField = f;
                }
                else if (_objectIdFilePathField == null && f.FieldType == typeof(string))
                {
                    _objectIdFilePathField = f;
                }
            }

            log.Append($"ObjectIdentifier=({_objectIdGuidField?.Name ?? "?"},")
                .Append($"{_objectIdLocalIdField?.Name ?? "?"},")
                .Append($"{_objectIdFileTypeField?.Name ?? "?"},")
                .Append($"{_objectIdFilePathField?.Name ?? "?"}) ");


            _buildUsageTagSetSerializer = ResolveBuildUsageTagSetSerializer(out var extractTag);
            _buildUsageTagSetDeserializer = ResolveBuildUsageTagSetDeserializer(out var loadTag);
            log.Append($"BuildUsageTagSet=({extractTag}/{loadTag}) ");


            foreach (var f in typeof(SceneDependencyInfo).GetFields(fieldFlags))
            {
                if (_sceneInfoSceneField == null && f.FieldType == typeof(string))
                {
                    _sceneInfoSceneField = f;
                }
                else if (_sceneInfoReferencedObjectsField == null && f.FieldType == typeof(ObjectIdentifier[]))
                {
                    _sceneInfoReferencedObjectsField = f;
                }
                else if (_sceneInfoIncludedTypesField == null && f.FieldType == typeof(Type[]))
                {
                    _sceneInfoIncludedTypesField = f;
                }
                else if (_sceneInfoGlobalUsageField == null && f.FieldType == typeof(BuildUsageTagGlobal))
                {
                    _sceneInfoGlobalUsageField = f;
                }
            }

            log.Append($"SceneDependencyInfo=({_sceneInfoSceneField?.Name ?? "?"},")
                .Append($"{_sceneInfoReferencedObjectsField?.Name ?? "?"},")
                .Append($"{_sceneInfoIncludedTypesField?.Name ?? "?"},")
                .Append($"{_sceneInfoGlobalUsageField?.Name ?? "?"}) ");


            _buildUsageTagGlobalFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            var unknownTypeFields = new List<string>();
            foreach (var f in typeof(BuildUsageTagGlobal).GetFields(fieldFlags))
            {
                _buildUsageTagGlobalFields[f.Name] = f;
                if (f.FieldType != typeof(uint) && f.FieldType != typeof(bool))
                {
                    unknownTypeFields.Add($"{f.Name}:{f.FieldType.Name}");
                }
            }

            log.Append($"BuildUsageTagGlobal=(n={_buildUsageTagGlobalFields.Count}");
            if (unknownTypeFields.Count > 0)
            {
                log.Append($", unsupported_types=[{string.Join(",", unknownTypeFields)}]");
            }

            log.Append(") ");


            // BuildCacheUtility sits in the global namespace inside the SBP assembly.
            var buildCacheUtilityType = sbpEditorAssembly.GetType("BuildCacheUtility");
            _objectTypesType = sbpEditorAssembly.GetType(
                "UnityEditor.Build.Pipeline.Utilities.ObjectTypes");
            _objectTypesCtor = _objectTypesType?.GetConstructor(
                new[] { typeof(ObjectIdentifier), typeof(Type[]) });
            _setTypeForObjectsMethod = buildCacheUtilityType?.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "SetTypeForObjects" && m.GetParameters().Length == 1);
            log.Append($"BuildCacheUtility={buildCacheUtilityType != null} ")
                .Append($"ObjectTypes={_objectTypesType != null} ")
                .Append($"SetTypeForObjects={_setTypeForObjectsMethod != null}");

            Debug.Log(log.ToString());

            // Throw at init for version drift. BuildUsageTagSet path is optional.
            var missing = new List<string>();
            if (_objectIdGuidField == null)
            {
                missing.Add("ObjectIdentifier.guid field");
            }

            if (_objectIdLocalIdField == null)
            {
                missing.Add("ObjectIdentifier.localIdentifierInFile field");
            }

            if (_objectIdFileTypeField == null)
            {
                missing.Add("ObjectIdentifier.fileType field");
            }

            if (_objectIdFilePathField == null)
            {
                missing.Add("ObjectIdentifier.filePath field");
            }

            if (_objectTypesType == null)
            {
                missing.Add("UnityEditor.Build.Pipeline.Utilities.ObjectTypes type");
            }

            if (_objectTypesCtor == null)
            {
                missing.Add("ObjectTypes ctor (ObjectIdentifier, Type[])");
            }

            if (_setTypeForObjectsMethod == null)
            {
                missing.Add("BuildCacheUtility.SetTypeForObjects method");
            }

            if (_sceneInfoSceneField == null)
            {
                missing.Add("SceneDependencyInfo.m_Scene field");
            }

            if (_sceneInfoReferencedObjectsField == null)
            {
                missing.Add("SceneDependencyInfo.m_ReferencedObjects field");
            }

            if (_sceneInfoIncludedTypesField == null)
            {
                missing.Add("SceneDependencyInfo.m_IncludedTypes field");
            }

            if (_sceneInfoGlobalUsageField == null)
            {
                missing.Add("SceneDependencyInfo.m_GlobalUsage field");
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "[SbpReflection] critical SBP internal surface missing " +
                    "— Unity / SBP version drift suspected. Missing: " +
                    string.Join(", ", missing));
            }
        }

        // SBP has no public ObjectIdentifier ctor; this and fresh CBI calls
        // are the only ways to materialise one.
        public ObjectIdentifier MakeObjectIdentifier(
            GUID guid, long localIdentifierInFile, FileType fileType, string filePath)
        {
            object boxed = default(ObjectIdentifier);
            _objectIdGuidField.SetValue(boxed, guid);
            _objectIdLocalIdField.SetValue(boxed, localIdentifierInFile);
            _objectIdFileTypeField.SetValue(boxed, fileType);
            _objectIdFilePathField.SetValue(boxed, filePath ?? string.Empty);
            return (ObjectIdentifier)boxed;
        }

        // Returns Array.Empty<byte>() when tag is null or probe found no variant.
        public byte[] SerializeBuildUsageTagSet(BuildUsageTagSet usage)
        {
            if (usage == null)
            {
                return Array.Empty<byte>();
            }

            return _buildUsageTagSetSerializer(usage) ?? Array.Empty<byte>();
        }

        // No-op when bytes are empty or probe found no variant.
        public void DeserializeBuildUsageTagSet(BuildUsageTagSet target, byte[] bytes)
        {
            if (target == null || bytes == null || bytes.Length == 0)
            {
                return;
            }

            _buildUsageTagSetDeserializer(target, bytes);
        }

        // Mirrors SBP ReflectionExtensions.SetScene / SetReferencedObjects
        // (boxed-struct field write) and extends to IncludedTypes / GlobalUsage.
        public SceneDependencyInfo MakeSceneDependencyInfo(
            string scenePath,
            ObjectIdentifier[] referencedObjects,
            Type[] includedTypes,
            BuildUsageTagGlobal globalUsage)
        {
            object boxed = default(SceneDependencyInfo);
            _sceneInfoSceneField.SetValue(boxed, scenePath ?? string.Empty);
            _sceneInfoReferencedObjectsField.SetValue(boxed, referencedObjects ?? Array.Empty<ObjectIdentifier>());
            _sceneInfoIncludedTypesField.SetValue(boxed, includedTypes ?? Array.Empty<Type>());
            _sceneInfoGlobalUsageField.SetValue(boxed, globalUsage);
            return (SceneDependencyInfo)boxed;
        }

        // Unmatched fields stay at default; unknown names are ignored so
        // old meta against a new Unity version still loads.
        public BuildUsageTagGlobal MakeBuildUsageTagGlobal(
            IReadOnlyDictionary<string, uint> uintFields,
            IReadOnlyDictionary<string, bool> boolFields)
        {
            object boxed = default(BuildUsageTagGlobal);
            if (uintFields != null)
            {
                foreach (var kv in uintFields)
                {
                    if (_buildUsageTagGlobalFields.TryGetValue(kv.Key, out var f) && f.FieldType == typeof(uint))
                    {
                        f.SetValue(boxed, kv.Value);
                    }
                }
            }

            if (boolFields != null)
            {
                foreach (var kv in boolFields)
                {
                    if (_buildUsageTagGlobalFields.TryGetValue(kv.Key, out var f) && f.FieldType == typeof(bool))
                    {
                        f.SetValue(boxed, kv.Value);
                    }
                }
            }

            return (BuildUsageTagGlobal)boxed;
        }

        // Unsupported field types (not uint/bool) are dropped silently.
        public void CaptureBuildUsageTagGlobal(
            BuildUsageTagGlobal global,
            out Dictionary<string, uint> uintFields,
            out Dictionary<string, bool> boolFields)
        {
            uintFields = new Dictionary<string, uint>(StringComparer.Ordinal);
            boolFields = new Dictionary<string, bool>(StringComparer.Ordinal);
            object boxed = global;
            foreach (var kv in _buildUsageTagGlobalFields)
            {
                var f = kv.Value;
                if (f.FieldType == typeof(uint))
                {
                    uintFields[f.Name] = (uint)f.GetValue(boxed);
                }
                else if (f.FieldType == typeof(bool))
                {
                    boolFields[f.Name] = (bool)f.GetValue(boxed);
                }
            }
        }

        // Warms BuildCacheUtility.m_ObjectToType; without this every bundle
        // rebuilds because downstream tasks miss the type cache.
        public int WarmTypeCache(
            IReadOnlyCollection<KeyValuePair<ObjectIdentifier, Type[]>> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return 0;
            }

            var listType = typeof(List<>).MakeGenericType(_objectTypesType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var kv in entries)
            {
                list.Add(_objectTypesCtor.Invoke(new object[] { kv.Key, kv.Value }));
            }

            _setTypeForObjectsMethod.Invoke(null, new object[] { list });
            return list.Count;
        }

        // Probes the three signatures BuildUsageTagSet may expose; returns a
        // uniform delegate + short tag for the variant found.
        private static Func<BuildUsageTagSet, byte[]> ResolveBuildUsageTagSetSerializer(out string tag)
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var usageType = typeof(BuildUsageTagSet);

            var serializeToBinaryReturning = usageType.GetMethod(
                "SerializeToBinary", flags, null, Type.EmptyTypes, null);
            if (serializeToBinaryReturning != null
                && serializeToBinaryReturning.ReturnType == typeof(byte[]))
            {
                tag = "SerializeToBinary";
                return usage => (byte[])serializeToBinaryReturning.Invoke(usage, null);
            }

            var serializeToBinaryOutParam = usageType.GetMethod(
                "SerializeToBinary", flags, null,
                new[] { typeof(byte[]).MakeByRefType() }, null);
            if (serializeToBinaryOutParam != null)
            {
                tag = "SerializeToBinary(out)";
                return usage =>
                {
                    var args = new object[] { null };
                    serializeToBinaryOutParam.Invoke(usage, args);
                    return (byte[])args[0];
                };
            }

            // Older surface: round-trip via temp file.
            var serializeToFile = usageType.GetMethod(
                "SerializeToFile", flags, null, new[] { typeof(string) }, null);
            if (serializeToFile != null)
            {
                tag = "SerializeToFile";
                return usage =>
                {
                    var tmp = Path.GetTempFileName();
                    try
                    {
                        serializeToFile.Invoke(usage, new object[] { tmp });
                        return File.ReadAllBytes(tmp);
                    }
                    finally
                    {
                        try { File.Delete(tmp); }
                        catch
                        {
                            /* best-effort */
                        }
                    }
                };
            }

            tag = "missing";
            return _ => Array.Empty<byte>();
        }

        private static Action<BuildUsageTagSet, byte[]> ResolveBuildUsageTagSetDeserializer(out string tag)
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var usageType = typeof(BuildUsageTagSet);

            var deserializeFromBinary = usageType.GetMethod(
                "DeserializeFromBinary", flags, null, new[] { typeof(byte[]) }, null);
            if (deserializeFromBinary != null)
            {
                tag = "DeserializeFromBinary";
                return (target, bytes) =>
                    deserializeFromBinary.Invoke(target, new object[] { bytes });
            }

            var deserializeFromFile = usageType.GetMethod(
                "DeserializeFromFile", flags, null, new[] { typeof(string) }, null);
            if (deserializeFromFile != null)
            {
                tag = "DeserializeFromFile";
                return (target, bytes) =>
                {
                    var tmp = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllBytes(tmp, bytes);
                        deserializeFromFile.Invoke(target, new object[] { tmp });
                    }
                    finally
                    {
                        try { File.Delete(tmp); }
                        catch
                        {
                            /* best-effort */
                        }
                    }
                };
            }

            tag = "missing";
            return (_, __) => { };
        }
    }
}