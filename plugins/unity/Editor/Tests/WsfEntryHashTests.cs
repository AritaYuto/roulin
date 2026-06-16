// Byte-for-byte parity check between our WSF.GetCacheEntry reconstruction
// (used by RoulinWriteOpHashProbe) and SBP's private implementation.
// Synthetic inputs + reflection-invoked SBP version → seconds per iteration
// instead of hours of real builds when binary-searching for divergences.

using Roulin.Editor.Build;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEditor.Build.Pipeline.WriteTypes;
using UnityEngine;

namespace Roulin.Editor.Tests
{
    public class WsfEntryHashTests
    {


        private static readonly Assembly s_SbpEditor = typeof(IBuildTask).Assembly;
        private static readonly Type s_WsfType = s_SbpEditor.GetType("UnityEditor.Build.Pipeline.Tasks.WriteSerializedFiles");

        private static readonly MethodInfo s_GetCacheEntry = s_WsfType?.GetMethod(
            "GetCacheEntry", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo s_WsfLogField = s_WsfType?.GetField(
            "m_Log", BindingFlags.NonPublic | BindingFlags.Instance);

        // Read at runtime so SBP version bumps don't silently desync the probe.
        private static readonly int s_WsfVersion = ResolveWsfVersion();

        private static CacheEntry InvokeSbpGetCacheEntry(
            IWriteOperation op, BuildSettings settings,
            BuildUsageTagGlobal globalUsage, bool slim)
        {
            Assert.IsNotNull(s_WsfType, "WriteSerializedFiles type not found via reflection");
            Assert.IsNotNull(s_GetCacheEntry, "WriteSerializedFiles.GetCacheEntry method not found");
            Assert.IsNotNull(s_WsfLogField, "WriteSerializedFiles.m_Log field not found");

            var wsf = Activator.CreateInstance(s_WsfType);
            s_WsfLogField.SetValue(wsf, new NullBuildLogger());

            return (CacheEntry)s_GetCacheEntry.Invoke(wsf,
                new object[] { op, settings, globalUsage, slim });
        }

        private static int ResolveWsfVersion()
        {
            var prop = s_WsfType?.GetProperty("Version");
            if (prop == null)
            {
                return 4;
            }

            return (int)prop.GetValue(Activator.CreateInstance(s_WsfType));
        }

        // Mirrors RoulinWriteOpHashProbe's entry.Hash reconstruction.
        private static Hash128 OurReconstructEntryHash(
            IWriteOperation op, BuildSettings settings,
            BuildUsageTagGlobal globalUsage, bool slim)
        {
            var overall = op.GetHash128(new NullBuildLogger());
            var settingsHash = HashingMethods.Calculate(
                settings.target, settings.group, settings.buildFlags).ToHash128();
            var playerSettingsHash = ComputePlayerSettingsHash(settings.target);
            return HashingMethods.Calculate(
                s_WsfVersion,
                overall,
                settingsHash,
                globalUsage,
                slim,
                playerSettingsHash
            ).ToHash128();
        }

        // PlayerSettings hash; mips count comes from a SerializedObject read.
        private static Hash128 ComputePlayerSettingsHash(BuildTarget target)
        {
            var mipsStripped = 0;
            if (PlayerSettings.mipStripping)
            {
                try
                {
                    var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                    var so = new SerializedObject(assets);
                    var prop = so.FindProperty("numberOfMipsStripped");
                    so.Update();
                    if (prop != null)
                    {
                        mipsStripped = prop.intValue;
                    }
                }
                catch { }
            }

            return HashingMethods.Calculate(
                PlayerSettings.stripUnusedMeshComponents,
                PlayerSettings.bakeCollisionMeshes,
                mipsStripped,
                PlayerSettings.GetGraphicsAPIs(target)
            ).ToHash128();
        }



        private static BuildSettings MakeSettings()
        {
            return new BuildSettings()
            {
                target = BuildTarget.iOS,
                group = BuildTargetGroup.iOS,
                buildFlags = ContentBuildFlags.None
            };
        }



        // Smallest possible op; divergence here points at the Calculate wrapper itself.
        [Test]
        public void EntryHash_EmptyAssetBundleOp()
        {
            var op = new AssetBundleWriteOperation
            {
                Command = new WriteCommand
                {
                    fileName = "test_empty",
                    internalName = "archive:/test_empty/test_empty",
                    serializeObjects = new List<SerializationInfo>()
                },
                UsageSet = new BuildUsageTagSet(),
                ReferenceMap = new BuildReferenceMap(),
                Info = new AssetBundleInfo
                {
                    bundleName = "test_bundle",
                    bundleAssets = new List<AssetLoadInfo>()
                },
                DependencyHash = default
            };
            var settings = MakeSettings();
            var globalUsage = default(BuildUsageTagGlobal);
            var slim = true;

            var sbp = InvokeSbpGetCacheEntry(op, settings, globalUsage, slim).Hash;
            var ours = OurReconstructEntryHash(op, settings, globalUsage, slim);

            Assert.AreEqual(sbp, ours,
                $"\nentry.Hash mismatch on empty op.\nSBP : {sbp}\nours: {ours}");
        }

        // Toggle slim flag: catches bool-encoding skew between us and SBP.
        [Test]
        public void EntryHash_EmptyAssetBundleOp_SlimFalse()
        {
            var op = MakeMinimalAssetBundleOp("test_slim_false");
            var settings = MakeSettings();
            var sbp = InvokeSbpGetCacheEntry(op, settings, default, /*slim=*/false).Hash;
            var ours = OurReconstructEntryHash(op, settings, default, /*slim=*/false);
            Assert.AreEqual(sbp, ours);
        }

        // Non-default settings: catches target/group/flags marshalling skew.
        [Test]
        public void EntryHash_DifferentBuildSettings()
        {
            var op = MakeMinimalAssetBundleOp("test_settings");
            var settings = new BuildSettings
            {
                target = BuildTarget.Android,
                group = BuildTargetGroup.Android,
                buildFlags = ContentBuildFlags.DevelopmentBuild
            };
            var sbp = InvokeSbpGetCacheEntry(op, settings, default, true).Hash;
            var ours = OurReconstructEntryHash(op, settings, default, true);
            Assert.AreEqual(sbp, ours);
        }

        // Non-default globalUsage: catches struct marshalling skew.
        [Test]
        public void EntryHash_NonDefaultGlobalUsage()
        {
            var op = MakeMinimalAssetBundleOp("test_globalusage");
            var settings = MakeSettings();

            // Reflection over private fields (mirrors SbpReflection.probe).
            var g = new BuildUsageTagGlobal();
            var boxed = (object)g;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var set = 0;
            foreach (var f in typeof(BuildUsageTagGlobal).GetFields(F))
            {
                if (f.FieldType == typeof(uint))
                {
                    f.SetValue(boxed, (uint)(0x42 + set));
                    set++;
                }
                else if (f.FieldType == typeof(bool))
                {
                    f.SetValue(boxed, (set & 1) == 0);
                    set++;
                }
            }

            g = (BuildUsageTagGlobal)boxed;

            var sbp = InvokeSbpGetCacheEntry(op, settings, g, true).Hash;
            var ours = OurReconstructEntryHash(op, settings, g, true);
            Assert.AreEqual(sbp, ours);
        }

        // SerializationInfo whose ObjectIdentifier misses m_ObjectToType.
        // SBP CBI fallback should return stable empty Type[] for non-existent ids.
        [Test]
        public void EntryHash_SingleSerializationInfo_NoTypeCache()
        {
            var dummyId = SbpReflection.Instance.MakeObjectIdentifier(
                new GUID("11111111111111111111111111111111"),
                21300000,
                FileType.MetaAssetType,
                "Assets/Dummy.prefab");
            var op = new AssetBundleWriteOperation
            {
                Command = new WriteCommand
                {
                    fileName = "test_one_so",
                    internalName = "archive:/test_one_so/test_one_so",
                    serializeObjects = new List<SerializationInfo>
                    {
                        new()
                        {
                            serializationObject = dummyId,
                            serializationIndex = 1L
                        }
                    }
                },
                UsageSet = new BuildUsageTagSet(),
                ReferenceMap = new BuildReferenceMap(),
                Info = new AssetBundleInfo
                {
                    bundleName = "test_bundle",
                    bundleAssets = new List<AssetLoadInfo>()
                },
                DependencyHash = default
            };
            var settings = MakeSettings();
            var sbp = InvokeSbpGetCacheEntry(op, settings, default, true).Hash;
            var ours = OurReconstructEntryHash(op, settings, default, true);
            Assert.AreEqual(sbp, ours,
                $"\nWith one SO entry, hashes should still match.\nSBP : {sbp}\nours: {ours}");
        }

        // entry.Guid path: Calculate("WriteSerializedFiles", fileName).ToGUID().
        // Wrong here → every disk lookup misses regardless of entry.Hash.
        [Test]
        public void EntryGuid_DerivedFromFileName()
        {
            var op = MakeMinimalAssetBundleOp("test_guid_path");
            var sbp = InvokeSbpGetCacheEntry(op, MakeSettings(), default, true).Guid;
            var ours = HashingMethods.Calculate(
                "WriteSerializedFiles", op.Command.fileName).ToGUID();
            Assert.AreEqual(sbp, ours);
        }


        // Logs every ingredient hash to localise which input drives the divergence.
        [Test]
        public void Diag_NarrowDownMismatchIngredient()
        {
            var op = MakeMinimalAssetBundleOp("test_diag");
            var settings = MakeSettings();
            var gu = default(BuildUsageTagGlobal);
            var slim = true;

            // Reference: SBP's actual final entry.Hash.
            var sbpFinal = InvokeSbpGetCacheEntry(op, settings, gu, slim).Hash;
            Debug.Log($"[Diag] sbp final = {sbpFinal}");

            // Our ingredients.
            var ourOpHash = op.GetHash128(new NullBuildLogger());
            var ourSettingsHash = HashingMethods.Calculate(settings.target, settings.group, settings.buildFlags).ToHash128();
            var ourPsHash = ComputePlayerSettingsHash(settings.target);
            var ourFinal = HashingMethods.Calculate(1, ourOpHash, ourSettingsHash, gu, slim, ourPsHash).ToHash128();
            Debug.Log($"[Diag] our op.GetHash128       = {ourOpHash}");
            Debug.Log($"[Diag] our settings hash       = {ourSettingsHash}");
            Debug.Log($"[Diag] our PlayerSettings hash = {ourPsHash}");
            Debug.Log($"[Diag] our final               = {ourFinal}");
            Debug.Log($"[Diag] our final == sbp final? = {ourFinal == sbpFinal}");

            // Try the HashingHelpers extension for BuildSettings.
            var hh = typeof(IBuildTask).Assembly.GetType("HashingHelpers");
            if (hh != null)
            {
                var ext = hh.GetMethod("GetHash128",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(BuildSettings) }, null);
                if (ext != null)
                {
                    var extSettingsHash = (Hash128)ext.Invoke(null, new object[] { settings });
                    Debug.Log($"[Diag] HashingHelpers ext settings hash = {extSettingsHash} (matches ours? {extSettingsHash == ourSettingsHash})");
                    var finalWithExt = HashingMethods.Calculate(1, ourOpHash, extSettingsHash, gu, slim, ourPsHash).ToHash128();
                    Debug.Log($"[Diag] final swapping in ext settings hash = {finalWithExt} (matches sbp? {finalWithExt == sbpFinal})");
                }
            }

            // Try BuildSettings.GetHash128 if it exists.
            var memberGetHash = typeof(BuildSettings).GetMethod("GetHash128",
                BindingFlags.Public | BindingFlags.Instance);
            if (memberGetHash != null)
            {
                var memberHash = (Hash128)memberGetHash.Invoke(settings, null);
                Debug.Log($"[Diag] BuildSettings.GetHash128 (struct member) = {memberHash} (declaring={memberGetHash.DeclaringType})");
                var finalWithMember = HashingMethods.Calculate(1, ourOpHash, memberHash, gu, slim, ourPsHash).ToHash128();
                Debug.Log($"[Diag] final swapping in member settings hash = {finalWithMember} (matches sbp? {finalWithMember == sbpFinal})");
            }
            else
            {
                Debug.Log("[Diag] BuildSettings has no public-instance GetHash128 member method");
            }

            // Binary-search: zero each ingredient to find the divergent one.
            var f_no_op = HashingMethods.Calculate(1, default(Hash128), ourSettingsHash, gu, slim, ourPsHash).ToHash128();
            var f_no_settings = HashingMethods.Calculate(1, ourOpHash, default(Hash128), gu, slim, ourPsHash).ToHash128();
            var f_no_ps = HashingMethods.Calculate(1, ourOpHash, ourSettingsHash, gu, slim, default(Hash128)).ToHash128();
            Debug.Log($"[Diag] final w/ default op       = {f_no_op}");
            Debug.Log($"[Diag] final w/ default settings = {f_no_settings}");
            Debug.Log($"[Diag] final w/ default PS       = {f_no_ps}");

            // Probe WSF.Version at runtime in case it isn't 1.
            var wsfInst = Activator.CreateInstance(s_WsfType);
            var versionPi = s_WsfType.GetProperty("Version");
            if (versionPi != null)
            {
                var v = (int)versionPi.GetValue(wsfInst);
                Debug.Log($"[Diag] WSF.Version property = {v}");
                if (v != 1)
                {
                    var finalWithRealVersion = HashingMethods.Calculate(v, ourOpHash, ourSettingsHash, gu, slim, ourPsHash).ToHash128();
                    Debug.Log($"[Diag] final w/ WSF.Version={v} = {finalWithRealVersion} (matches sbp? {finalWithRealVersion == sbpFinal})");
                }
            }

            // Diagnostic-only.
        }



        private static AssetBundleWriteOperation MakeMinimalAssetBundleOp(string fileName)
        {
            return new AssetBundleWriteOperation
            {
                Command = new WriteCommand
                {
                    fileName = fileName,
                    internalName = $"archive:/{fileName}/{fileName}",
                    serializeObjects = new List<SerializationInfo>()
                },
                UsageSet = new BuildUsageTagSet(),
                ReferenceMap = new BuildReferenceMap(),
                Info = new AssetBundleInfo
                {
                    bundleName = fileName + "_bundle",
                    bundleAssets = new List<AssetLoadInfo>()
                },
                DependencyHash = default
            };
        }


        // WSF.GetCacheEntry wraps in ScopedStep for tracing; no-op logger is enough.
        private sealed class NullBuildLogger : IBuildLogger
        {
            public void BeginBuildStep(LogLevel level, string stepName, bool subStepsCanBeThreaded) { }
            public void EndBuildStep() { }
            public void AddEntry(LogLevel level, string msg) { }
        }
    }
}