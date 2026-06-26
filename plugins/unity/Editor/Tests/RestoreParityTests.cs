using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Roulin.Editor.Build;
using Roulin.Editor.Build.CustomBuildTasks;
using Roulin.Editor.Build.Meta;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Tests
{
    // Verifies the core promise behind VCS-diff incremental build:
    //
    //   "Changed assets go through fresh ContentBuildInterface walks; unchanged assets get their
    //    dependency data restored from blob_meta; the resulting bundles are
    //    byte-identical to a cold full build of the same slice."
    //
    // Test structure:
    //   A. Cold build (RestorePayload = null) with RoulinCaptureBlobMeta enabled
    //      → ground-truth bundle bytes + captured per-bundle RoulinUnityBlob
    //   B. Decode captured blob_meta into a RestorePayload (mirrors what
    //      MetaClient.FetchAllBlobMetas + RestoreBlobMetas.Decode do in prod)
    //   C. Pick half the slice asset GUIDs as "simulated VCS-changed"; remove
    //      them from the RestorePayload so they fall through to ContentBuildInterface walk
    //   D. Mixed build with the filtered RestorePayload
    //      → unchanged half restored from cache, changed half walked fresh
    //   E. Assert: every produced bundle has the same Hash + size as in cold
    //
    // PASS → the restore path is byte-correct; VCS-diff can replace the
    //        hash-drift staleness check without breaking correctness.
    // FAIL → log shows which specific bundle's hash differs; that points at
    //        whatever dep info the mixed flow corrupts (next fix target).
    public class RestoreParityTests
    {
        [Test, Explicit("Two Scriptable Build Pipeline builds on a 10-bundle non-scene slice; minutes on production catalogs"),
            Timeout(15 * 60 * 1000)]
        public void Mixed_Restore_Matches_Cold_Full_Build()
        {
            var aas = AddressableAssetSettingsDefaultObject.Settings;
            Assert.IsNotNull(aas, "AddressableAssetSettings not configured");

            var walkSw = Stopwatch.StartNew();
            var walk = WalkAddressableGroups.Run(aas);
            walkSw.Stop();
            Debug.Log($"[RestoreParity] WalkAddressableGroups.Run: {walkSw.ElapsedMilliseconds} ms, " +
                $"produced {walk.BundleBuilds.Count} bundles");

            // Scene ContentBuildInterface walk is ~7-10s per bundle in production-scale catalogs.
            // Exclude scenes so the test stays under the 15-min wall.
            var assetOnly = walk.BundleBuilds
                .Where(b => b.assetNames != null
                    && !b.assetNames.Any(a => a != null
                        && a.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.That(assetOnly.Count, Is.GreaterThanOrEqualTo(30),
                "need at least 30 non-scene bundles");

            const int sliceStart = 100;
            const int sliceCount = 10;
            var slice = assetOnly.GetRange(sliceStart, sliceCount);
            var sliceNames = new HashSet<string>(slice.Select(b => b.assetBundleName));
            Debug.Log($"[RestoreParity] slice ({slice.Count} bundles): " +
                $"{string.Join(", ", sliceNames)}");

            var target = EditorUserBuildSettings.activeBuildTarget;

            // ----- Step A: cold build, capture blob_meta -----
            var coldCtx = MakeContext(slice, aas, target);
            var coldDir = MakeTempDir("cold");
            Debug.Log($"[RestoreParity] cold build → {coldDir}");
            var coldResults = RunBuild(slice, coldDir,
                restorePayload: null, ctx: coldCtx, captureBlobMeta: true);
            Debug.Log($"[RestoreParity] cold produced {coldResults.BundleInfos.Count} bundles; " +
                $"captured {coldCtx.BlobMetasByBundle.Count} blob_meta entries");

            // ----- Step B: decode captured blob_meta into RestorePayload -----
            var capturedEnvelopes = coldCtx.BlobMetasByBundle
                .Select(kv => new RoulinBlobMeta(kv.Value, blobHash: "test-" + kv.Key))
                .ToList();
            var fullPayload = new RestoreBlobMetas().Decode(capturedEnvelopes);
            Debug.Log($"[RestoreParity] decoded RestorePayload: " +
                $"AssetByGuid={fullPayload.AssetByGuid.Count}, " +
                $"SceneByGuid={fullPayload.SceneByGuid.Count}, " +
                $"ObjectTypes={fullPayload.ObjectTypes.Count}");

            // ----- Step C: half the slice GUIDs treated as "VCS-changed" -----
            var sliceGuids = new List<GUID>();
            foreach (var b in slice)
            {
                if (b.assetNames == null) continue;
                foreach (var assetPath in b.assetNames)
                {
                    var guidStr = AssetDatabase.AssetPathToGUID(assetPath);
                    if (!string.IsNullOrEmpty(guidStr))
                    {
                        sliceGuids.Add(new GUID(guidStr));
                    }
                }
            }
            var changedCount = Math.Max(1, sliceGuids.Count / 2);
            var changedGuids = new HashSet<GUID>(sliceGuids.Take(changedCount));
            Debug.Log($"[RestoreParity] slice GUIDs: total={sliceGuids.Count}, " +
                $"simulated-changed={changedGuids.Count}, " +
                $"simulated-unchanged={sliceGuids.Count - changedGuids.Count}");

            var filteredAssetByGuid = new Dictionary<GUID, RestoredAsset>(fullPayload.AssetByGuid.Count);
            foreach (var kv in fullPayload.AssetByGuid)
            {
                if (!changedGuids.Contains(kv.Key))
                {
                    filteredAssetByGuid[kv.Key] = kv.Value;
                }
            }
            var filteredPayload = new RestorePayload
            {
                AssetByGuid = filteredAssetByGuid,
                SceneByGuid = fullPayload.SceneByGuid, // slice is non-scene → empty in practice
                ObjectTypes = fullPayload.ObjectTypes,
            };
            Debug.Log($"[RestoreParity] filtered RestorePayload: " +
                $"AssetByGuid={filteredPayload.AssetByGuid.Count} " +
                $"(removed {fullPayload.AssetByGuid.Count - filteredPayload.AssetByGuid.Count} changed)");

            // ----- Step D: mixed build, changed half walks ContentBuildInterface, rest restored -----
            var mixedCtx = MakeContext(slice, aas, target);
            var mixedDir = MakeTempDir("mixed");
            Debug.Log($"[RestoreParity] mixed build → {mixedDir}");
            var mixedResults = RunBuild(slice, mixedDir,
                restorePayload: filteredPayload, ctx: mixedCtx, captureBlobMeta: false);
            Debug.Log($"[RestoreParity] mixed produced {mixedResults.BundleInfos.Count} bundles");

            // ----- Step E: byte parity per bundle -----
            foreach (var name in sliceNames)
            {
                Assert.That(coldResults.BundleInfos.ContainsKey(name), Is.True,
                    $"{name} missing from cold results (fixture bug)");
                Assert.That(mixedResults.BundleInfos.ContainsKey(name), Is.True,
                    $"{name} missing from mixed results");

                var coldBI = coldResults.BundleInfos[name];
                var mixedBI = mixedResults.BundleInfos[name];

                var coldPath = Path.Combine(coldDir, coldBI.FileName);
                var mixedPath = Path.Combine(mixedDir, mixedBI.FileName);
                long coldSize = File.Exists(coldPath) ? new FileInfo(coldPath).Length : -1;
                long mixedSize = File.Exists(mixedPath) ? new FileInfo(mixedPath).Length : -1;

                Debug.Log($"[RestoreParity] {name}: " +
                    $"cold hash={coldBI.Hash} size={coldSize}, " +
                    $"mixed hash={mixedBI.Hash} size={mixedSize}");

                Assert.That(mixedBI.Hash, Is.EqualTo(coldBI.Hash),
                    $"{name}: mixed bundle hash differs from cold → restore path corrupted bytes");
                Assert.That(mixedSize, Is.EqualTo(coldSize),
                    $"{name}: mixed bundle size differs (cold={coldSize}, mixed={mixedSize})");
            }
        }

        private static IBundleBuildResults RunBuild(
            List<AssetBundleBuild> bundles,
            string outputDir,
            RestorePayload restorePayload,
            RoulinBuildSharedContext ctx,
            bool captureBlobMeta)
        {
            var sw = Stopwatch.StartNew();
            var target = EditorUserBuildSettings.activeBuildTarget;
            var targetGroup = BuildPipeline.GetBuildTargetGroup(target);
            var buildParams = new BundleBuildParameters(target, targetGroup, outputDir)
            {
                BundleCompression = BuildCompression.LZ4
            };

            var content = new BundleBuildContent(bundles);
            var tasks = DefaultBuildTasks.Create(
                DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

            RoulinCalculateAssetDependencyData assetDepTask = null;
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].GetType().Name == "CalculateAssetDependencyData")
                {
                    assetDepTask = new RoulinCalculateAssetDependencyData
                    {
                        RestorePayload = restorePayload
                    };
                    tasks[i] = assetDepTask;
                    break;
                }
            }
            Assert.IsNotNull(assetDepTask,
                "CalculateAssetDependencyData not found in Scriptable Build Pipeline preset — preset mismatch");

            RoulinCalculateSceneDependencyData sceneDepTask = null;
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].GetType().Name == "CalculateSceneDependencyData")
                {
                    sceneDepTask = new RoulinCalculateSceneDependencyData
                    {
                        RestorePayload = restorePayload
                    };
                    tasks[i] = sceneDepTask;
                    break;
                }
            }

            if (captureBlobMeta)
            {
                tasks.Add(new RoulinCaptureBlobMeta
                {
                    EnableCapture = true,
                    UnityVersion = Application.unityVersion,
                    SbpVersion = typeof(IBuildTask).Assembly.GetName().Version.ToString(),
                    AssetDependencyTask = assetDepTask,
                    SceneDependencyTask = sceneDepTask,
                });
            }

            var rc = ContentPipeline.BuildAssetBundles(
                buildParams, content, out var results, tasks, null, ctx);
            sw.Stop();
            Debug.Log($"[RestoreParity] Scriptable Build Pipeline: {sw.ElapsedMilliseconds} ms, rc={rc}, " +
                $"input={bundles.Count}, output={results?.BundleInfos.Count ?? 0}, " +
                $"restorePayload={(restorePayload != null ? $"AssetByGuid={restorePayload.AssetByGuid.Count}" : "null")}");
            Assert.That((int)rc, Is.GreaterThanOrEqualTo((int)ReturnCode.Success),
                $"Scriptable Build Pipeline failed with ReturnCode={rc}");
            Assert.IsNotNull(results, "Scriptable Build Pipeline returned null IBundleBuildResults");
            return results;
        }

        // Minimal context: tasks we run only touch BlobMetasByBundle (newly
        // allocated empty dict inside the constructor) and the type-injected
        // _dependencyData / _writeData that Scriptable Build Pipeline provides directly.
        private static RoulinBuildSharedContext MakeContext(
            List<AssetBundleBuild> slice, AddressableAssetSettings aas, BuildTarget target)
        {
            return new RoulinBuildSharedContext(
                bundleBuilds: slice,
                bundleInputs: new Dictionary<string, BundleInput>(),
                bundleToAssetGroup: new Dictionary<string, string>(),
                assetEntries: new List<AddressableAssetEntry>(),
                aas: aas,
                target: target);
        }

        private static string MakeTempDir(string suffix)
        {
            var dir = Path.Combine(
                Application.temporaryCachePath,
                "RestoreParityTests",
                suffix);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
