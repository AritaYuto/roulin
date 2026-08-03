using Roulin.Editor;
using Roulin.Editor.Build.CustomBuildTasks;
using Roulin.Editor.PackRule;
using Roulin.Editor.Vcs;
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
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return (TResult)(object)new AddressablesPlayerBuildResult
                {
                    Duration = sw.Elapsed.TotalSeconds,
                    Error = ex.Message
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

            T Phase<T>(string name, Func<T> body)
            {
                var sw = Stopwatch.StartNew();
                var result = body();
                sw.Stop();
                phaseMs.Add((name, sw.ElapsedMilliseconds));
                return result;
            }

            void PhaseAction(string name, Action body)
            {
                var sw = Stopwatch.StartNew();
                body();
                sw.Stop();
                phaseMs.Add((name, sw.ElapsedMilliseconds));
            }

            var aas = input.AddressableSettings;
            var settings = RoulinEditorSettings.instance;
            var serverUrl = settings.ServerUrl;
            var (revision, outputDir) = PrepareBuildContext(settings, serverUrl);

            using var client = new RoulinServerClient(serverUrl);
            var packRule = RoulinPackRuleRegistry.Resolve(aas);

            // Defense against a previous crashed build leaving the asset behind.
            RoulinUnityBuiltInsIO.DeleteAssetIfExists();

            try
            {
                PhaseAction("pack rule apply", () => RunPackRuleApply(packRule, aas));
                var view = Phase("groups walk", () => AddressablesGroupsView.From(aas));
                var diff = Phase("vcs diff", () => FetchDiff(client));

                var forceFull = RoulinForceFullPublish.ConsumeForNextBuild();
                if (forceFull)
                {
                    Debug.Log("[RoulinBuild] force-full-publish armed → ignoring base revision");
                }
                var incremental = !forceFull && !string.IsNullOrEmpty(diff.BaseRevision) && packRule != null;

                var changedBundles = Phase("changed bundles resolve",
                    () => ComputeChangedBundles(view, packRule, diff, forceFull, incremental));

                var sbpInput = Phase("dep closure walk",
                    () => ComputeSbpInput(changedBundles, view, packRule, incremental));

                if (incremental)
                {
                    PhaseAction("RoulinUnityBuiltIns asset create", () =>
                    {
                        var populated = RoulinUnityBuiltInsIO.CreateFresh();
                        sbpInput.Builds.Add(new AssetBundleBuild
                        {
                            assetBundleName  = RoulinUnityBuiltIns.BundleName,
                            assetNames       = new[] { RoulinUnityBuiltIns.AssetPath },
                            addressableNames = new[] { RoulinUnityBuiltIns.AssetPath },
                        });
                        Debug.Log(
                            $"[RoulinBuild] RoulinUnityBuiltIns asset created: " +
                            $"MonoScripts={populated.MonoScriptCount}, " +
                            $"BuiltInShaders={populated.BuiltInShaderCount}, " +
                            $"bundle '{RoulinUnityBuiltIns.BundleName}' " +
                            $"added to SBP input");
                    });
                }

                var sbp = Phase(
                    "Scriptable Build Pipeline",
                    () => RunScriptableBuildPipeline(
                        aas, outputDir, revision, diff, incremental,
                        sbpInput.Builds, view, client, settings));


                var report = BuildReport.Compose(serverUrl, revision, sbp.Catalog);
                report.LogSummary(settings.Verbose);
                var reportPath = report.WriteJson(outputDir);
                Debug.Log($"[RoulinBuild] build report → {reportPath}");

                EditorUtility.UnloadUnusedAssetsImmediate();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                runBuildSw.Stop();
                sbp.TimingLogger.FlushSummary(runBuildSw.ElapsedMilliseconds, phaseMs);

                return new AddressablesPlayerBuildResult
                {
                    OutputPath = outputDir,
                    LocationCount = report.LocationCount
                };
            }
            finally
            {
                // Cleans up even if incremental=false or CreateFresh threw.
                RoulinUnityBuiltInsIO.DeleteAssetIfExists();
            }
        }

        private static (string revision, string outputDir) PrepareBuildContext(
            RoulinEditorSettings settings, string serverUrl)
        {
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

            var outputDir = Path.GetFullPath(settings.BundleOutputDir);
            Directory.CreateDirectory(outputDir);

            Debug.Log(
                $"[RoulinBuild] start: server={serverUrl}, " +
                $"revision={revision}, output={outputDir}");
            return (revision, outputDir);
        }

        private static void RunPackRuleApply(IRoulinPackRule packRule, AddressableAssetSettings aas)
        {
            if (!(packRule is IRoulinPackRuleApplier applier)) return;
            var report = applier.Apply(aas);
            Debug.Log(
                $"[RoulinBuild] pack rule apply: " +
                $"groups {report.GroupsCreated}+/{report.GroupsRemoved}- " +
                $"entries {report.EntriesAdded}+/{report.EntriesRemoved}-/{report.EntriesMoved}~ " +
                $"addresses {report.AddressesReassigned}~ " +
                $"labels {report.LabelsChanged}~ " +
                $"(touched {report.ModifiedGroupNames.Count} groups)");
        }

        // Empty BaseRevision = no prior publish → full rebuild.
        private struct DiffSnapshot
        {
            public string BaseRevision;
            public IReadOnlyList<string> DirtyUnityPaths;
            public IReadOnlyList<string> BaseBundleNames;
        }

        private static DiffSnapshot FetchDiff(RoulinServerClient client)
        {
            try
            {
                var vcsClient = new VcsDiffClient(client);
                var diff = Task.Run(() => vcsClient.FetchProjectDiffAsync())
                    .GetAwaiter().GetResult();
                Debug.Log(
                    $"[RoulinBuild] /diff base={diff.BaseRevision ?? "(none)"}, " +
                    $"dirty unity paths={diff.UnityPaths.Count}, " +
                    $"base bundle names={diff.BaseBundleNames.Count}");
                return new DiffSnapshot
                {
                    BaseRevision = diff.BaseRevision,
                    DirtyUnityPaths = diff.UnityPaths,
                    BaseBundleNames = diff.BaseBundleNames,
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoulinBuild] /diff unavailable ({ex.Message}) → full rebuild");
                return new DiffSnapshot
                {
                    BaseRevision = null,
                    DirtyUnityPaths = Array.Empty<string>(),
                    BaseBundleNames = Array.Empty<string>(),
                };
            }
        }

        private static HashSet<string> ComputeChangedBundles(
            AddressablesGroupsView view,
            IRoulinPackRule packRule,
            DiffSnapshot diff,
            bool forceFull,
            bool incremental)
        {
            if (packRule == null && !forceFull && !string.IsNullOrEmpty(diff.BaseRevision))
            {
                Debug.LogWarning(
                    "[RoulinBuild] no IRoulinPackRule registered → falling back to full rebuild. " +
                    "Register a project-specific IRoulinPackRule via RoulinPackRuleRegistry to enable incremental builds.");
            }

            if (!incremental)
            {
                var full = new HashSet<string>(
                    view.BundleBuilds.Select(bundle => bundle.assetBundleName),
                    StringComparer.Ordinal);
                Debug.Log($"[RoulinBuild] full rebuild: {full.Count} bundles");
                return full;
            }

            var changedBundles = new HashSet<string>(StringComparer.Ordinal);
            if (diff.DirtyUnityPaths != null)
            {
                var resolveSw = Stopwatch.StartNew();
                var resolved = packRule.ResolveGroupsForPaths(diff.DirtyUnityPaths);
                resolveSw.Stop();
                foreach (var (path, groupName) in resolved)
                {
                    var baseName = AddressablesGroupsView.SanitizeBundleName(groupName);
                    var isScene = path.EndsWith(".unity", StringComparison.Ordinal);
                    changedBundles.Add(isScene ? baseName + "_scenes" : baseName);
                }
                Debug.Log(
                    $"[RoulinBuild] initial resolve: dirtyPaths={diff.DirtyUnityPaths.Count} " +
                    $"→ resolved={resolved.Count} paths → changedBundles={changedBundles.Count} " +
                    $"({resolveSw.ElapsedMilliseconds}ms)");
            }

            // Bundles absent from base must be built; server-side merge rejects parcels that list them otherwise.
            var baseNamesSet = new HashSet<string>(diff.BaseBundleNames, StringComparer.Ordinal);
            var newBundleCount = 0;
            foreach (var bundle in view.BundleBuilds)
            {
                if (!baseNamesSet.Contains(bundle.assetBundleName) && changedBundles.Add(bundle.assetBundleName))
                {
                    newBundleCount++;
                }
            }
            if (newBundleCount > 0)
            {
                Debug.Log($"[RoulinBuild] new bundles (in view, not in base): {newBundleCount}");
            }
            return changedBundles;
        }

        private struct SbpInput
        {
            public HashSet<string> Names;
            public List<AssetBundleBuild> Builds;
        }

        private static SbpInput ComputeSbpInput(
            HashSet<string> changedBundles,
            AddressablesGroupsView view,
            IRoulinPackRule packRule,
            bool incremental)
        {
            var sbpInputNames = incremental
                ? BundleDependencyResolver.Resolve(changedBundles, view.BundleBuilds, packRule)
                : new HashSet<string>(changedBundles, StringComparer.Ordinal);
            var sbpInputBuilds = view.BundleBuilds
                .Where(bundle => sbpInputNames.Contains(bundle.assetBundleName))
                .ToList();
            Debug.Log(
                $"[RoulinBuild] SBP input = {sbpInputBuilds.Count}/{view.BundleBuilds.Count} " +
                $"(changed={changedBundles.Count}, +closure={sbpInputNames.Count - changedBundles.Count})");

            const int detailCap = 20;
            if (changedBundles.Count > 0 && changedBundles.Count <= detailCap)
            {
                Debug.Log(
                    $"[RoulinBuild]   changed bundles: " +
                    $"{string.Join(", ", changedBundles)}");
            }
            return new SbpInput { Names = sbpInputNames, Builds = sbpInputBuilds };
        }

        // TimingLogger deferred so FlushSummary captures total runBuild elapsed time.
        private struct SbpBuildResult
        {
            public RoulinCatalog Catalog;
            public RoulinTimingBuildLogger TimingLogger;
        }

        private static SbpBuildResult RunScriptableBuildPipeline(
            AddressableAssetSettings aas,
            string outputDir,
            string revision,
            DiffSnapshot diff,
            bool incremental,
            List<AssetBundleBuild> sbpInputBuilds,
            AddressablesGroupsView view,
            RoulinServerClient client,
            RoulinEditorSettings settings)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var targetGroup = BuildPipeline.GetBuildTargetGroup(target);

            var buildParams = new BundleBuildParameters(target, targetGroup, outputDir);
            Debug.Log(
                $"[RoulinBuild] BundleBuildParameters: UseCache={buildParams.UseCache}, " +
                $"WriteLinkXML={buildParams.WriteLinkXML}, " +
                $"CacheServerHost={buildParams.CacheServerHost ?? "(local)"}");
            var firstSchema = aas.groups
                .Where(group => group != null && group.HasSchema<BundledAssetGroupSchema>())
                .Select(group => group.GetSchema<BundledAssetGroupSchema>())
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

            // Without shader extraction, built-in shader materials render pink at runtime.
            var buildTasks = DefaultBuildTasks.Create(
                DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

            var uploadResults = new BlobUploadResults();
            var catalog = new RoulinCatalog();

            buildTasks.Add(new RoulinPublishBlobs
            {
                Server = client,
                OutputDir = outputDir,
                Verbose = settings.Verbose
            });

            // BaseBundleNames fallback prevents losing SBP-generated bundle names when SBP produces 0 bundles.
            var publishParcel = new RoulinPublishParcel
            {
                Server = client,
                Revision = revision,
            };
            if (incremental)
            {
                publishParcel.BaseRevision = diff.BaseRevision;
                publishParcel.BaseBundleNames = diff.BaseBundleNames;
            }
            buildTasks.Add(publishParcel);

            var timingLogger = new RoulinTimingBuildLogger { EmitPerStepLog = settings.Verbose };

            var rc = ContentPipeline.BuildAssetBundles(
                buildParams, content, out var sbpResults, buildTasks,
                timingLogger, view, uploadResults, catalog);
            if (rc < ReturnCode.Success)
            {
                throw new Exception($"ContentPipeline.BuildAssetBundles failed: {rc}");
            }

            Debug.Log($"[RoulinBuild] Scriptable Build Pipeline done ({sbpResults.BundleInfos.Count} bundle(s))");
            return new SbpBuildResult { Catalog = catalog, TimingLogger = timingLogger };
        }
    }
}
