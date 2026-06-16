using System.Collections.Generic;

namespace Roulin.Editor.Build
{
    // Engine-neutral per-bundle build output. Feeds ParcelBuilder — no
    // Unity / SBP types so it stays unit-testable.
    public class BundleInput
    {
        // Hex BLAKE3 of the .bundle binary, returned by POST /blobs.
        public string BinaryHash;

        // Bundle names this bundle depends on at AssetBundle load time;
        // ParcelBuilder resolves them to BinaryHash via the bundle dict.
        public List<string> DepBundleNames = new();

        public List<EntryInput> Entries = new();

        // Unique within the build, stable enough to reference as a dep.
        public string Name;

        // Byte length of the .bundle binary (drives ILocationSizeData on runtime).
        public long SizeBytes;
    }

    public readonly struct EntryInput
    {
        // Logical address passed to LoadAsync<T> at runtime. Registered as
        // the bundle-internal name via AssetBundleBuild.addressableNames.
        public readonly string Address;

        // Addressables labels; null/empty means no labels.
        public readonly List<string> Labels;

        // Engine-native asset id (Unity AssetGUID 32 hex, UE FGuid string, ...).
        // Lets LoadAssetAsync calls keyed by id resolve (e.g. Unity
        // AssetReference.RuntimeKey). Optional.
        public readonly string AssetID;

        // Engine-specific type identifier (Unity: AssemblyQualifiedName).
        // Lets the runtime Locator disambiguate same-address-different-type
        // entries (e.g. a Scene and a Prefab sharing the address "skit_system").
        // Empty when the type was not captured at build time.
        public readonly string AssetType;

        public EntryInput(string address, List<string> labels, string assetID, string assetType)
        {
            Address = address;
            Labels = labels;
            AssetID = assetID;
            AssetType = assetType;
        }
    }
}