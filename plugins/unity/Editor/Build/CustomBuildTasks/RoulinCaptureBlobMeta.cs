using Roulin.Editor.Build.Meta;
using System;
using System.Collections.Generic;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Writes per-bundle RoulinUnityBlob into roulinContext.BlobMetasByBundle
    // for the publish task. Reads type-cache output from the asset/scene dependency
    // tasks (injected via task properties rather than IContextObject).
    internal sealed class RoulinCaptureBlobMeta : RoulinBuildTaskBase
    {
        public override int Version => 1;

        public bool EnableCapture { get; set; }
        public string UnityVersion { get; set; }
        public string SbpVersion { get; set; }
        public RoulinCalculateAssetDependencyData AssetDependencyTask { get; set; }
        public RoulinCalculateSceneDependencyData SceneDependencyTask { get; set; }

        public override ReturnCode Run()
        {
            try
            {
                if (!EnableCapture || _dependencyData == null)
                {
                    return ReturnCode.SuccessNotRun;
                }

                var objectToTypes = new Dictionary<ObjectIdentifier, Type[]>();
                if (AssetDependencyTask?.CollectedObjectToType != null)
                {
                    foreach (var kv in AssetDependencyTask.CollectedObjectToType)
                    {
                        objectToTypes[kv.Key] = kv.Value;
                    }
                }

                if (SceneDependencyTask?.CollectedObjectToType != null)
                {
                    foreach (var kv in SceneDependencyTask.CollectedObjectToType)
                    {
                        objectToTypes[kv.Key] = kv.Value;
                    }
                }

                var blobMetas = new CollectBlobMetas().ByBundle(
                    _dependencyData,
                    _extendedAssetData,
                    _writeData,
                    objectToTypes,
                    UnityVersion,
                    SbpVersion);

                foreach (var kv in blobMetas)
                {
                    roulinContext.BlobMetasByBundle[kv.Key] = kv.Value;
                }

                Debug.Log(
                    $"[RoulinCaptureBlobMeta] {blobMetas.Count} bundle(s), " +
                    $"objectToTypes entries={objectToTypes.Count}");

                return ReturnCode.Success;
            }
            finally
            {
                // Release type-cache references for GC before downstream tasks run; cuts peak memory by ~1.5 GB.
                AssetDependencyTask?.ReleaseRetainedState();
                SceneDependencyTask?.ReleaseRetainedState();
                AssetDependencyTask = null;
                SceneDependencyTask = null;
            }
        }

#pragma warning disable 649
        [InjectContext(ContextUsage.In, true)]
        private IDependencyData _dependencyData;

        [InjectContext(ContextUsage.In, true)]
        private IBuildExtendedAssetData _extendedAssetData;

        [InjectContext(ContextUsage.In)]
        private IBundleWriteData _writeData;
#pragma warning restore 649
    }
}