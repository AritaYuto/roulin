using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Pipeline.Interfaces;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Cross-task shared state. Collection references are fixed at construction;
    // their contents are mutated by tasks as the pipeline runs.
    public interface IRoulinBuildSharedContext : IContextObject
    {
        List<AssetBundleBuild> BundleBuilds { get; }
        Dictionary<string, BundleInput> BundleInputs { get; }
        Dictionary<string, string> BundleToAssetGroup { get; }
        List<AddressableAssetEntry> AssetEntries { get; }
        AddressableAssetSettings Aas { get; }
        BuildTarget Target { get; }
    }

    public readonly struct RoulinBuildSharedContext : IRoulinBuildSharedContext
    {
        public List<AssetBundleBuild> BundleBuilds { get; }
        public Dictionary<string, BundleInput> BundleInputs { get; }
        public Dictionary<string, string> BundleToAssetGroup { get; }
        public List<AddressableAssetEntry> AssetEntries { get; }
        public AddressableAssetSettings Aas { get; }
        public BuildTarget Target { get; }

        public RoulinBuildSharedContext(
            List<AssetBundleBuild> bundleBuilds,
            Dictionary<string, BundleInput> bundleInputs,
            Dictionary<string, string> bundleToAssetGroup,
            List<AddressableAssetEntry> assetEntries,
            AddressableAssetSettings aas,
            BuildTarget target)
        {
            BundleBuilds = bundleBuilds ?? throw new ArgumentNullException(nameof(bundleBuilds));
            BundleInputs = bundleInputs ?? throw new ArgumentNullException(nameof(bundleInputs));
            BundleToAssetGroup = bundleToAssetGroup ?? throw new ArgumentNullException(nameof(bundleToAssetGroup));
            AssetEntries = assetEntries ?? throw new ArgumentNullException(nameof(assetEntries));
            Aas = aas ?? throw new ArgumentNullException(nameof(aas));
            Target = target;
        }
    }
}
