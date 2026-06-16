using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    internal sealed class RoulinPublishParcel : RoulinBuildTaskBase
    {
        public override int Version => 1;

        public RoulinServerClient Server { get; set; }
        public string Revision { get; set; }

        public override ReturnCode Run()
        {
            if (Server == null)
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.Server is null — set before adding to task list");
            }

            if (string.IsNullOrEmpty(Revision))
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.Revision is null/empty — set before adding to task list");
            }

            var parcel = ParcelBuilder.Build(roulinContext.BundleInputs);

            EditorUtility.DisplayProgressBar(
                "Roulin Build", $"POST /parcels/{Revision}…", 0.95f);
            Debug.Log(
                $"[RoulinPublishParcel] POST /parcels/{Revision} ({parcel.bundles.Count} bundle(s))");
            Task.Run(async () => await Server.PostParcel(Revision, parcel)).GetAwaiter().GetResult();

            return ReturnCode.Success;
        }
    }


    public static class ParcelBuilder
    {
        // Bundles keyed by BundleInput.Name so dep references resolve to BinaryHash.
        public static Parcel Build(System.Collections.Generic.IDictionary<string, BundleInput> bundles)
        {
            if (bundles == null)
            {
                throw new ArgumentNullException(nameof(bundles));
            }

            var parcel = new Parcel();

            foreach (var kv in bundles)
            {
                var b = kv.Value;
                if (string.IsNullOrEmpty(b.BinaryHash))
                {
                    throw new InvalidOperationException(
                        $"bundle '{b.Name}' has no BinaryHash — was POST /blobs not run yet?");
                }

                var bundle = new Bundle
                {
                    address = b.Name,
                    blob_hash = b.BinaryHash,
                    size_bytes = b.SizeBytes
                };

                foreach (var e in b.Entries)
                {
                    if (string.IsNullOrEmpty(e.Address))
                    {
                        continue;
                    }

                    var entry = new Entry { address = e.Address };
                    if (e.Labels != null && e.Labels.Count > 0)
                    {
                        entry.labels = new System.Collections.Generic.List<string>(e.Labels);
                    }

                    if (!string.IsNullOrEmpty(e.AssetID))
                    {
                        entry.asset_id = e.AssetID;
                    }

                    if (!string.IsNullOrEmpty(e.AssetType))
                    {
                        entry.asset_type = e.AssetType;
                    }

                    bundle.entries.Add(entry);
                }

                foreach (var depName in b.DepBundleNames)
                {
                    if (string.IsNullOrEmpty(depName))
                    {
                        continue;
                    }

                    if (!bundles.TryGetValue(depName, out var depBundle))
                    {
                        throw new InvalidOperationException(
                            $"bundle '{b.Name}' depends on unknown bundle '{depName}'");
                    }

                    if (string.IsNullOrEmpty(depBundle.BinaryHash))
                    {
                        throw new InvalidOperationException(
                            $"bundle '{b.Name}' depends on '{depName}' which has no BinaryHash");
                    }

                    bundle.dependencies.Add(depBundle.BinaryHash);
                }

                parcel.bundles.Add(bundle);
            }

            return parcel;
        }
    }
}
