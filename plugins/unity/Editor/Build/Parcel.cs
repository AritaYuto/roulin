using System;
using System.Collections.Generic;

namespace Roulin.Editor.Build
{
    // Parcel mirrors tools/roulin/internal/build/parcel.Parcel — the JSON
    // body POSTed to /parcels/{revision} when finalising a build.
    //
    // Full publish: leave base_revision empty; bundles[] covers every bundle.
    // Incremental publish: set base_revision + all_bundle_names; bundles[] holds
    // only what this build regenerated. The server merges the delta with the
    // base revision's stored Index.
    [Serializable]
    public class Parcel
    {
        public List<Bundle> bundles = new();
        public string base_revision;
        public List<string> all_bundle_names = new();
    }

    [Serializable]
    public class Bundle
    {
        public string address;
        public string blob_hash;
        public long size_bytes;
        public List<Entry> entries = new();
        // Pre-resolved hex blob hashes. Empty when emitting dep_bundle_names.
        public List<string> dependencies = new();
        // Bundle names; server resolves to current-revision blob hashes.
        public List<string> dep_bundle_names = new();
    }

    [Serializable]
    public class Entry
    {
        public string address;
        public List<string> labels = new();
        public string asset_id;
        public string asset_type; // engine-specific type identifier (Unity: AssemblyQualifiedName)
    }
}
