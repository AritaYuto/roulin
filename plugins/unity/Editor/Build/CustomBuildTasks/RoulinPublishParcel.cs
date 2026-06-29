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

        // Set to the server's idea of the base revision (from GetDiff response)
        // to publish incrementally. Leave null/empty for a full publish (every
        // bundle in BundleInputs must have BinaryHash).
        public string BaseRevision { get; set; }

        // Full list of bundle names that should exist in the new revision.
        // Required when BaseRevision is set. The server keeps base entries whose
        // names are in this list and drops the rest.
        public System.Collections.Generic.List<string> AllBundleNames { get; set; }

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

            var incremental = !string.IsNullOrEmpty(BaseRevision);
            if (incremental && (AllBundleNames == null || AllBundleNames.Count == 0))
            {
                throw new InvalidOperationException(
                    "RoulinPublishParcel.AllBundleNames is required when BaseRevision is set");
            }

            var parcel = ParcelBuilder.Build(roulinContext.BundleInputs, deltaOnly: incremental);
            if (incremental)
            {
                parcel.base_revision = BaseRevision;
                parcel.all_bundle_names = AllBundleNames;
            }

            EditorUtility.DisplayProgressBar(
                "Roulin Build", $"POST /parcels/{Revision}…", 0.95f);
            Debug.Log(
                $"[RoulinPublishParcel] POST /parcels/{Revision} " +
                $"mode={(incremental ? "incremental" : "full")} " +
                $"delta={parcel.bundles.Count} " +
                $"all_names={(parcel.all_bundle_names?.Count ?? 0)}");
            Task.Run(async () => await Server.PostParcel(Revision, parcel)).GetAwaiter().GetResult();

            return ReturnCode.Success;
        }
    }


    public static class ParcelBuilder
    {
        // Builds the wire Parcel from BundleInputs.
        //
        // deltaOnly = false (full publish): every BundleInput must have a
        // BinaryHash; an entry is emitted for each. Dep resolution is left to
        // the server using dep_bundle_names against the parcel itself.
        //
        // deltaOnly = true (incremental publish): only BundleInputs with a
        // BinaryHash get an entry (= bundles this build actually produced);
        // the rest are server-merged from the base revision.
        public static Parcel Build(
            System.Collections.Generic.IDictionary<string, BundleInput> bundles,
            bool deltaOnly = false)
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
                    if (deltaOnly)
                    {
                        continue;
                    }
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
                    bundle.dep_bundle_names.Add(depName);
                }

                parcel.bundles.Add(bundle);
            }

            return parcel;
        }
    }
}
