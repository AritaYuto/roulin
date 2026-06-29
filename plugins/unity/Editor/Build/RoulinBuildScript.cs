using Roulin.Editor;
using Roulin.Editor.Build.CustomBuildTasks;
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
                EditorUtility.ClearProgressBar();
            }
        }

        private AddressablesPlayerBuildResult RunBuild(AddressablesDataBuilderInput input)
        {
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

            var phaseSw = Stopwatch.StartNew();
            var view = AddressablesGroupsView.From(aas);
            markPhase(phaseSw, "1. groups walk");

            // Ask the server which revision it considers "base", and which paths
            // are dirty since that revision. We do NOT fetch the Index here:
            // catalog merge is server-side, so Unity never reads the previous
            // Index. If /diff fails or returns an empty revision, fall back
            // to full publish (every bundle goes to SBP).
            phaseSw = Stopwatch.StartNew();
            string baseRevision = null;
            List<string> uncommittedUnityPaths = new();
            try
            {
                var diff = Task.Run(() => client.GetDiffAsync(sinceSha: null))
                    .GetAwaiter().GetResult();
                baseRevision = diff?.revision;
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var gitRoot = FindGitRoot(projectRoot);
                uncommittedUnityPaths = VcsDiffPathNormalizer.Normalize(
                    gitRoot, projectRoot, diff?.uncommitted);
                Debug.Log(
                    $"[RoulinBuild] /diff base={baseRevision ?? "(none)"}, " +
                    $"uncommitted unity paths={uncommittedUnityPaths.Count}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RoulinBuild] /diff unavailable ({ex.Message}) → full rebuild");
            }
            markPhase(phaseSw, "1.5. vcs diff");

            // Identify bundles to rebuild this revision: those owning a path
            // in the uncommitted set. Plus the downward closure (every bundle
            // they transitively depend on) so SBP can resolve cross-bundle
            // refs without inlining the dep.
            //
            // If we have no base revision (or the user armed the force-full
            // flag for schema migration), fall through to full rebuild =
            // every walk bundle is considered changed; SBP input = full set;
            // POST is a full publish.
            phaseSw = Stopwatch.StartNew();
            HashSet<string> changedBundles;
            var forceFull = RoulinForceFullPublish.ConsumeForNextBuild();
            if (forceFull)
            {
                Debug.Log("[RoulinBuild] force-full-publish armed → ignoring base revision");
            }
            var incremental = !forceFull && !string.IsNullOrEmpty(baseRevision);
            if (!incremental)
            {
                changedBundles = new HashSet<string>(
                    view.BundleBuilds.Select(b => b.assetBundleName), StringComparer.Ordinal);
                Debug.Log($"[RoulinBuild] full rebuild: {changedBundles.Count} bundles");
            }
            else
            {
                changedBundles = new HashSet<string>(StringComparer.Ordinal);
                if (uncommittedUnityPaths != null)
                {
                    foreach (var name in view.ResolveAffectedBundles(uncommittedUnityPaths))
                    {
                        changedBundles.Add(name);
                    }
                }
            }

            var sbpInputNames = BundleDependencyResolver.Resolve(changedBundles, view);
            var sbpInputBuilds = view.BundleBuilds
                .Where(b => sbpInputNames.Contains(b.assetBundleName))
                .ToList();
            Debug.Log(
                $"[RoulinBuild] SBP input = {sbpInputBuilds.Count}/{view.BundleBuilds.Count} " +
                $"(changed={changedBundles.Count}, +closure={sbpInputNames.Count - changedBundles.Count})");

            // Cap-to-N detail log so single-file iterations are auditable.
            const int detailCap = 20;
            if (changedBundles.Count > 0 && changedBundles.Count <= detailCap)
            {
                Debug.Log(
                    $"[RoulinBuild]   changed bundles: " +
                    $"{string.Join(", ", changedBundles)}");
            }
            markPhase(phaseSw, "1.6. closure compute");

            // Scriptable Build Pipeline runs over the closure subset only.
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

            var content = new BundleBuildContent(sbpInputBuilds);

            Debug.Log($"[RoulinBuild] running Scriptable Build Pipeline for target={target}, group={targetGroup}…");

            // Shader/script extraction adds CreateBuiltInShadersBundle +
            // CreateMonoScriptBundle. Without it materials using built-in
            // shaders render pink at runtime.
            var buildTasks = DefaultBuildTasks.Create(
                DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

            // Per-concern SBP context objects (no shared mutable god-bag).
            var uploadResults = new BlobUploadResults();
            var catalog = new RoulinCatalog();

            buildTasks.Add(new RoulinPublishBlobs
            {
                Server = client,
                OutputDir = outputDir,
                Verbose = settings.Verbose
            });

            // Hand the parcel publisher the base revision + the full walk's
            // bundle name list. Server merges the delta with that base; bundles
            // in all_bundle_names that the delta did not regenerate are carried
            // over from base.
            var publishParcel = new RoulinPublishParcel
            {
                Server = client,
                Revision = revision,
            };
            if (incremental)
            {
                publishParcel.BaseRevision = baseRevision;
                publishParcel.AllBundleNames = view.BundleBuilds
                    .Select(b => b.assetBundleName)
                    .ToList();
            }
            buildTasks.Add(publishParcel);

            var sbpTimingLogger = new RoulinTimingBuildLogger { EmitPerStepLog = settings.Verbose };

            var rc = ContentPipeline.BuildAssetBundles(
                buildParams, content, out var sbpResults, buildTasks,
                sbpTimingLogger, view, uploadResults, catalog);
            if (rc < ReturnCode.Success)
            {
                throw new Exception($"ContentPipeline.BuildAssetBundles failed: {rc}");
            }

            Debug.Log($"[RoulinBuild] Scriptable Build Pipeline done ({sbpResults.BundleInfos.Count} bundle(s))");
            markPhase(phaseSw, "2. Scriptable Build Pipeline ContentPipeline.BuildAssetBundles");

            var report = BuildReport.Compose(serverUrl, revision, catalog);
            report.LogSummary(settings.Verbose);
            var reportPath = report.WriteJson(outputDir);
            Debug.Log($"[RoulinBuild] build report → {reportPath}");

            EditorUtility.UnloadUnusedAssetsImmediate();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            runBuildSw.Stop();
            var runBuildMs = runBuildSw.ElapsedMilliseconds;
            sbpTimingLogger.FlushSummary(runBuildMs, phaseMs);

            return new AddressablesPlayerBuildResult
            {
                OutputPath = outputDir,
                LocationCount = report.LocationCount
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
    }
}
