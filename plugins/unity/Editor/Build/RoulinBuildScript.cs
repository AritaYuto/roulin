using Roulin.Editor;
using Roulin.Editor.Build.CustomBuildTasks;
using Roulin.Editor.Build.Meta;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build
{
    [CreateAssetMenu(
        fileName = "RoulinBuildScript",
        menuName = "Roulin/Build Script (Roulin Parcel)")]
    public class RoulinBuildScript : BuildScriptBase
    {
        public override string Name => "Roulin Parcel Build";

        // Settings live in RoulinEditorSettings; this SO is a stateless invoker.
        public override bool CanBuildData<T>()
        {
            return true;
        }

        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput input)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = RunBuild(input);
                result.Duration = sw.Elapsed.TotalSeconds;
                return (TResult)(object)result;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return (TResult)(object)new AddressablesPlayerBuildResult
                {
                    Duration = sw.Elapsed.TotalSeconds,
                    Error = e.Message
                };
            }
            finally
            {
                // Unhandled exceptions would otherwise leave the progress bar stuck.
                EditorUtility.ClearProgressBar();
            }
        }

        private AddressablesPlayerBuildResult RunBuild(AddressablesDataBuilderInput input)
        {
            // Outer wall-clock. Subtracting SBP logger's top-level sum yields
            // the "outside SBP" time (groups walk, warm fetch, uploads, parcel POST).
            var runBuildSw = Stopwatch.StartNew();
            var phaseMs = new List<(string Name, long Ms)>();

            long markPhase(Stopwatch ps, string name)
            {
                ps.Stop();
                var ms = ps.ElapsedMilliseconds;
                phaseMs.Add((name, ms));
                return ms;
            }

            var aas = input.AddressableSettings;
            var settings = RoulinEditorSettings.instance;
            var serverUrl = settings.ServerUrl;
            var bundleOutputDir = settings.BundleOutputDir;

            // Revision priority: CLI arg > settings.ManualRevision > git rev-parse > UTC timestamp.
            // CLI form (-roulinRevision <value> or =<value>) is the CI entry point; it bypasses
            // the per-developer settings file so batch builds don't mutate UserSettings/.
            var revision = RoulinUtil.TryCommandLineArg("-roulinRevision");
            if (string.IsNullOrWhiteSpace(revision))
            {
                revision = settings.ManualRevision;
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                revision = RoulinUtil.TryGitSha();
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                revision = RoulinUtil.TimestampRevision();
            }

            revision = revision.Trim();

            var outputDir = Path.GetFullPath(bundleOutputDir);
            Directory.CreateDirectory(outputDir);

            Debug.Log(
                $"[RoulinBuild] start: server={serverUrl}, " +
                $"revision={revision}, output={outputDir}");

            using var client = new RoulinServerClient(serverUrl);
            var meta = new MetaClient(client);

            var phaseSw = Stopwatch.StartNew();
            var walk = WalkAddressableGroups.Run(aas);
            markPhase(phaseSw, "1. groups walk");

            // Warm path. Returns null on any failure; falls through to cold path.
            // walk.BundleBuilds is NOT filtered: SBP downstream tasks need the full
            // cross-bundle graph. We rely on BuildCache (driven by restored dependency data)
            // + BlobExists upload skip for unchanged-blob short-circuit.
            phaseSw = Stopwatch.StartNew();
            RestorePayload warmRestorePayload = null;
            if (settings.EnableBlobMetaCapture)
            {
                var blobMetas = Task.Run(() => meta.FetchAllBlobMetas()).GetAwaiter().GetResult();
                if (blobMetas != null)
                {
                    warmRestorePayload = new RestoreBlobMetas().Decode(blobMetas);
                }
            }

            markPhase(phaseSw, "1.5. warm restore fetch");

            // Run SBP.
            phaseSw = Stopwatch.StartNew();
            var target = EditorUserBuildSettings.activeBuildTarget;
            var targetGroup = BuildPipeline.GetBuildTargetGroup(target);

            var buildParams = new BundleBuildParameters(target, targetGroup, outputDir);
            Debug.Log(
                $"[RoulinBuild] BundleBuildParameters: UseCache={buildParams.UseCache}, " +
                $"WriteLinkXML={buildParams.WriteLinkXML}, " +
                $"CacheServerHost={buildParams.CacheServerHost ?? "(local)"}");
            var firstSchema = aas.groups
                .Where(g => g != null && g.HasSchema<BundledAssetGroupSchema>())
                .Select(g => g.GetSchema<BundledAssetGroupSchema>())
                .FirstOrDefault();
            if (firstSchema != null)
            {
                buildParams.BundleCompression = firstSchema.Compression switch
                {
                    BundledAssetGroupSchema.BundleCompressionMode.Uncompressed => BuildCompression.Uncompressed,
                    BundledAssetGroupSchema.BundleCompressionMode.LZ4 => BuildCompression.LZ4,
                    BundledAssetGroupSchema.BundleCompressionMode.LZMA => BuildCompression.LZMA,
                    _ => BuildCompression.LZ4
                };
                Debug.Log($"[RoulinBuild] BundleCompression = {firstSchema.Compression}");
            }

            var content = new BundleBuildContent(walk.BundleBuilds);

            Debug.Log($"[RoulinBuild] running SBP for target={target}, group={targetGroup}…");

            // AssetBundleCompatible omits shader/script extraction → materials
            // using built-in shaders render pink at runtime. ShaderAndScriptExtraction
            // adds CreateBuiltInShadersBundle + CreateMonoScriptBundle as auto-wired deps.
            var buildTasks = DefaultBuildTasks.Create(
                DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

            // Drop-in replacement for SBP CalculateAssetDependencyData: skips the
            // dirty-check walk (~62% faster). With warmRestorePayload set, it also
            // pre-populates context for unchanged assets and skips their CBI walk.
            var assetDepReplacedAt = -1;
            RoulinCalculateAssetDependencyData assetDepTask = null;
            for (var i = 0; i < buildTasks.Count; i++)
            {
                if (buildTasks[i].GetType().Name == "CalculateAssetDependencyData")
                {
                    assetDepTask = new RoulinCalculateAssetDependencyData
                    {
                        RestorePayload = warmRestorePayload
                    };
                    buildTasks[i] = assetDepTask;
                    assetDepReplacedAt = i;
                    break;
                }
            }

            if (assetDepReplacedAt < 0)
            {
                Debug.LogWarning(
                    "[RoulinBuild] default CalculateAssetDependencyData task " +
                    "not found in SBP preset — passthrough/restore disabled");
            }
            else
            {
                Debug.Log(
                    $"[RoulinBuild] CalculateAssetDependencyData replaced at index {assetDepReplacedAt} " +
                    $"(restore_payload={(warmRestorePayload != null ? "set" : "null")})");
            }

            var sceneDepReplacedAt = -1;
            RoulinCalculateSceneDependencyData sceneDepTask = null;
            for (var i = 0; i < buildTasks.Count; i++)
            {
                if (buildTasks[i].GetType().Name == "CalculateSceneDependencyData")
                {
                    sceneDepTask = new RoulinCalculateSceneDependencyData
                    {
                        RestorePayload = warmRestorePayload
                    };
                    buildTasks[i] = sceneDepTask;
                    sceneDepReplacedAt = i;
                    break;
                }
            }

            if (sceneDepReplacedAt < 0)
            {
                Debug.LogWarning(
                    "[RoulinBuild] default CalculateSceneDependencyData not found in SBP preset — " +
                    "scene dependency restore disabled");
            }
            else
            {
                Debug.Log(
                    $"[RoulinBuild] CalculateSceneDependencyData replaced at index {sceneDepReplacedAt} " +
                    $"(restore_payload={(warmRestorePayload != null ? "set" : "null")})");
            }

            // Shared state injected into Roulin IBuildTasks via [InjectContext].
            var roulinContext = new RoulinBuildSharedContext(
                walk.BundleBuilds, walk.Inputs, walk.BundleToAssetGroup, walk.AssetEntries, aas, target);

            buildTasks.Add(new RoulinGenerateLocationLists());

            // Per-bundle blob_meta capture (opt-in via EnableBlobMetaCapture).
            buildTasks.Add(new RoulinCaptureBlobMeta
            {
                EnableCapture = settings.EnableBlobMetaCapture,
                UnityVersion = Application.unityVersion,
                SbpVersion = typeof(IBuildTask).Assembly.GetName().Version.ToString(),
                AssetDependencyTask = assetDepTask,
                SceneDependencyTask = sceneDepTask
            });

            // Publish each bundle binary + its blob_meta sidecar.
            buildTasks.Add(new RoulinPublishBlobs
            {
                Server = client,
                Meta = meta,
                OutputDir = outputDir,
                Verbose = settings.Verbose
            });
            
            // Build the wire Parcel and POST it to /parcels/{rev}.
            buildTasks.Add(new RoulinPublishParcel
            {
                Server = client,
                Revision = revision
            });

            // Surface per-task SBP timing via logger.ScopedStep — needed to
            // decide which task to optimise next.
            var sbpTimingLogger = new RoulinTimingBuildLogger { EmitPerStepLog = settings.Verbose };

            var rc = ContentPipeline.BuildAssetBundles(
                buildParams, content, out var sbpResults, buildTasks,
                sbpTimingLogger, roulinContext);
            if (rc < ReturnCode.Success)
            {
                throw new Exception($"ContentPipeline.BuildAssetBundles failed: {rc}");
            }

            Debug.Log($"[RoulinBuild] SBP done ({sbpResults.BundleInfos.Count} bundle(s))");
            markPhase(phaseSw, "2. SBP ContentPipeline.BuildAssetBundles");

            var locationCount = 0;
            long totalBytes = 0;
            foreach (var bi in walk.Inputs.Values)
            {
                locationCount += bi.Entries.Count;
                totalBytes += bi.SizeBytes;
            }

            var report = BuildReport.Compose(serverUrl, revision, walk.Inputs, locationCount);
            report.LogSummary(settings.Verbose);
            var reportPath = report.WriteJson(outputDir);
            Debug.Log($"[RoulinBuild] build report → {reportPath}");

            // Final managed-GC sweep so the next build starts clean (native pool
            // stays reserved; Unity does not return it to the OS).
            warmRestorePayload = null;
            EditorUtility.UnloadUnusedAssetsImmediate();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Unified timing summary: macro phases + SBP per-step aggregates.
            runBuildSw.Stop();
            var runBuildMs = runBuildSw.ElapsedMilliseconds;
            sbpTimingLogger.FlushSummary(runBuildMs, phaseMs);

            return new AddressablesPlayerBuildResult
            {
                OutputPath = outputDir,
                LocationCount = locationCount
            };
        }
    }
}