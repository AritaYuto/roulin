using System;
using System.Collections.Generic;

namespace Roulin.Editor.Build
{
    // Build-time in-memory model of the catalog that becomes /parcels/{rev}
    // on the server.
    //
    // Constructed fresh in RoulinPublishParcel from
    //   - AddressablesGroupsView (addressable assets per bundle)
    //   - IBundleBuildResults  (SBP-built bundle set)
    //   - IBundleWriteData     (SBP dep closure)
    //   - IBlobUploadResults   (blob hash + size from upload)
    // then serialised to the wire Parcel for POST.
    public sealed class RoulinCatalog
    {
        // One bundle's record in the catalog.
        public sealed class Entry
        {
            public string Name;
            public string BlobHash;
            public long SizeBytes;
            public List<AddressableEntry> Addresses = new();
            public List<string> DepBundleNames = new();
        }

        private readonly Dictionary<string, Entry> mByName = new(StringComparer.Ordinal);

        public IReadOnlyCollection<Entry> Bundles => mByName.Values;

        public int Count => mByName.Count;

        public Entry Get(string name)
        {
            return mByName.TryGetValue(name, out var e) ? e : null;
        }

        public void Add(Entry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new ArgumentException(
                    "RoulinCatalog.Entry.Name must be set before Add", nameof(entry));
            }
            if (mByName.ContainsKey(entry.Name))
            {
                throw new InvalidOperationException(
                    $"duplicate bundle name in catalog: '{entry.Name}'");
            }
            mByName[entry.Name] = entry;
        }

        // Serialises the catalog into the wire Parcel form ready for POST.
        // Field-by-field translation; no semantic conversion.
        public Parcel ToParcel()
        {
            var p = new Parcel();
            foreach (var e in mByName.Values)
            {
                var b = new Bundle
                {
                    address = e.Name,
                    blob_hash = e.BlobHash,
                    size_bytes = e.SizeBytes,
                };
                foreach (var a in e.Addresses)
                {
                    var wireEntry = new global::Roulin.Editor.Build.Entry { address = a.Address };
                    if (a.Labels != null && a.Labels.Count > 0)
                    {
                        wireEntry.labels = new List<string>(a.Labels);
                    }
                    if (!string.IsNullOrEmpty(a.AssetID))
                    {
                        wireEntry.asset_id = a.AssetID;
                    }
                    if (!string.IsNullOrEmpty(a.AssetType))
                    {
                        wireEntry.asset_type = a.AssetType;
                    }
                    b.entries.Add(wireEntry);
                }
                foreach (var dn in e.DepBundleNames)
                {
                    if (string.IsNullOrEmpty(dn)) continue;
                    b.dep_bundle_names.Add(dn);
                }
                p.bundles.Add(b);
            }
            return p;
        }
    }
}
