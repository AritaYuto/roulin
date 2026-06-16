using System;
using System.Collections.Generic;

namespace Roulin.Editor.Build
{
    // Parcel mirrors tools/roulin/internal/build/parcel.Parcel — the JSON
    // body POSTed to /parcels/{revision} when finalising a build.
    [Serializable]
    public class Parcel
    {
        public List<Bundle> bundles = new();
    }

    [Serializable]
    public class Bundle
    {
        public string address;
        public string blob_hash;
        public long size_bytes;
        public List<Entry> entries = new();
        public List<string> dependencies = new();
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
