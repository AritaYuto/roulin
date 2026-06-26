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
            // Outer wall-clock. Subtracting Scriptable Build Pipeline logger's top-level sum yields
            // the "outside Scriptable Build Pipeline" time (groups walk, warm fetch, uploads, parcel POST).
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
            // walk.BundleBuilds is NOT filtered: Scriptable Build Pipeline downstream tasks need the full
            // cross-bundle graph. We rely on the restored dependency data +
            // BlobExists upload skip for unchanged-blob short-circuit.
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

            // VCS-diff staleness signal. Skip when there's no payload to filter.
            // Failure → conservative fallback (all asset/scene GUIDs marked changed
            // = no restore = cold-equivalent build).
            phaseSw = Stopwatch.StartNew();
            HashSet<GUID> changedGuids = null;
            if (warmRestorePayload != null)
            {
                try
                {
                    var diff = Task.Run(() => client.GetDiffAsync(sinceSha: null))
                        .GetAwaiter().GetResult();
                    var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    var gitRoot = FindGitRoot(projectRoot);
                    var unityPaths = VcsDiffPathNormalizer.Normalize(
                        gitRoot, projectRoot, diff.uncommitted);
                    var lookup = BundleLookup.From(walk.BundleBuilds);
                    var changedBundles = lookup.ResolveAffectedBundles(unityPaths);
                    changedGuids = ExpandBundleSetToAssetGuids(changedBundles, walk.BundleBuilds);
                    Debug.Log(
                        $"[RoulinBuild] /diff revision={diff.revision}, " +
                        $"unity paths in scope={unityPaths.Count}, " +
                        $"changed bundles={changedBundles.Count}, " +
                        $"changed asset GUIDs={changedGuids.Count}");

                    // Cap-to-N detail logs let the user verify single-file
                    // change scenarios (e.g. edit one .png → expect 1 path →
                    // 1 bundle) without spamming logs on bulk-change builds.
                    const int detailCap = 20;
                    if (unityPaths.Count > 0 && unityPaths.Count <= detailCap)
                    {
                        foreach (var path in unityPaths)
                        {
                            var bundle = lookup.GetBundleFor(path);
                            Debug.Log(
                                $"[RoulinBuild]   path: {path} → " +
                                $"bundle: {bundle ?? "(not in any bundle)"}");
                        }
                    }
                    else if (unityPaths.Count > detailCap)
                    {
                        Debug.Log(
                            $"[RoulinBuild]   (per-path detail omitted: {unityPaths.Count} > {detailCap})");
                    }
                    if (changedBundles.Count > 0 && changedBundles.Count <= detailCap)
                    {
                        Debug.Log(
                            $"[RoulinBuild]   changed bundles: " +
                            $"{string.Join(", ", changedBundles)}");
                    }
                }
                catch (Exception ex)
                {
                    changedGuids = AllAssetAndSceneGuids(walk.BundleBuilds);
                    Debug.LogWarning(
                        $"[RoulinBuild] /diff query failed → fallback: full rebuild " +
                        $"({changedGuids.Count} GUIDs marked changed): {ex.Message}");
                }
            }
            markPhase(phaseSw, "1.6. vcs diff");

            // Run Scriptable Build Pipeline.
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

            Debug.Log($"[RoulinBuild] running Scriptable Build Pipeline for target={target}, group={targetGroup}…");

            // AssetBundleCompatible omits shader/script extraction → materials
            // using built-in shaders render pink at runtime. ShaderAndScriptExtraction
            // adds CreateBuiltInShadersBundle + CreateMonoScriptBundle as auto-wired deps.
            var buildTasks = DefaultBuildTasks.Create(
                DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

            // Drop-in replacement for Scriptable Build Pipeline CalculateAssetDependencyData: skips the
            // dirty-check walk (~62% faster). With warmRestorePayload set, it also
            // pre-populates context for unchanged assets and skips their ContentBuildInterface walk.
            var assetDepReplacedAt = -1;
            RoulinCalculateAssetDependencyData assetDepTask = null;
            for (var i = 0; i < buildTasks.Count; i++)
            {
                if (buildTasks[i].GetType().Name == "CalculateAssetDependencyData")
                {
                    assetDepTask = new RoulinCalculateAssetDependencyData
                    {
                        RestorePayload = warmRestorePayload,
                        ChangedGuids = changedGuids,
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
                    "not found in Scriptable Build Pipeline preset — passthrough/restore disabled");
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
                        RestorePayload = warmRestorePayload,
                        ChangedGuids = changedGuids,
                    };
                    buildTasks[i] = sceneDepTask;
                    sceneDepReplacedAt = i;
                    break;
                }
            }

            if (sceneDepReplacedAt < 0)
            {
                Debug.LogWarning(
                    "[RoulinBuild] default CalculateSceneDependencyData not found in Scriptable Build Pipeline preset — " +
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

            // Surface per-task Scriptable Build Pipeline timing via logger.ScopedStep — needed to
            // decide which task to optimise next.
            var sbpTimingLogger = new RoulinTimingBuildLogger { EmitPerStepLog = settings.Verbose };

            var rc = ContentPipeline.BuildAssetBundles(
                buildParams, content, out var sbpResults, buildTasks,
                sbpTimingLogger, roulinContext);
            if (rc < ReturnCode.Success)
            {
                throw new Exception($"ContentPipeline.BuildAssetBundles failed: {rc}");
            }

            Debug.Log($"[RoulinBuild] Scriptable Build Pipeline done ({sbpResults.BundleInfos.Count} bundle(s))");
            markPhase(phaseSw, "2. Scriptable Build Pipeline ContentPipeline.BuildAssetBundles");

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

            // Unified timing summary: macro phases + Scriptable Build Pipeline per-step aggregates.
            runBuildSw.Stop();
            var runBuildMs = runBuildSw.ElapsedMilliseconds;
            sbpTimingLogger.FlushSummary(runBuildMs, phaseMs);

            return new AddressablesPlayerBuildResult
            {
                OutputPath = outputDir,
                LocationCount = locationCount
            };
        }

        private static string FindGitRoot(string startDir)
        {
            var dir = new System.IO.DirectoryInfo(startDir);
            while (dir != null)
            {
                var marker = Path.Combine(dir.FullName, ".git");
                if (System.IO.Directory.Exists(marker) || System.IO.File.Exists(marker))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return startDir;
        }

        private static HashSet<GUID> ExpandBundleSetToAssetGuids(
            ISet<string> bundleNames, List<UnityEditor.AssetBundleBuild> bundleBuilds)
        {
            var result = new HashSet<GUID>();
            foreach (var b in bundleBuilds)
            {
                if (!bundleNames.Contains(b.assetBundleName) || b.assetNames == null)
                {
                    continue;
                }
                foreach (var assetPath in b.assetNames)
                {
                    var guidStr = AssetDatabase.AssetPathToGUID(assetPath);
                    if (!string.IsNullOrEmpty(guidStr))
                    {
                        result.Add(new GUID(guidStr));
                    }
                }
            }
            return result;
        }

        private static HashSet<GUID> AllAssetAndSceneGuids(
            List<UnityEditor.AssetBundleBuild> bundleBuilds)
        {
            var result = new HashSet<GUID>();
            foreach (var b in bundleBuilds)
            {
                if (b.assetNames == null)
                {
                    continue;
                }
                foreach (var assetPath in b.assetNames)
                {
                    var guidStr = AssetDatabase.AssetPathToGUID(assetPath);
                    if (!string.IsNullOrEmpty(guidStr))
                    {
                        result.Add(new GUID(guidStr));
                    }
                }
            }
            return result;
        }
    }
}