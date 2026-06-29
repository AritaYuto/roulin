using System.Collections.Generic;

namespace Roulin.Editor.Build
{
    // One addressable asset entry inside a bundle. Pure metadata — populated
    // from AddressableAssetSettings at walk time, read by the catalog builder
    // when assembling Parcel.entries for a bundle. Immutable once constructed.
    public readonly struct AddressableEntry
    {
        // Logical address passed to LoadAsync<T> at runtime.
        public readonly string Address;

        // Addressables labels. null/empty = no labels.
        public readonly IReadOnlyList<string> Labels;

        // Engine-native asset id (Unity AssetGUID 32 hex, UE FGuid, ...).
        // Lets LoadAssetAsync keyed by id resolve (Unity AssetReference).
        public readonly string AssetID;

        // Engine-specific type identifier (Unity: AssemblyQualifiedName).
        // Lets the runtime Locator disambiguate same-address-different-type
        // entries. Empty when not captured at build time.
        public readonly string AssetType;

        public AddressableEntry(string address, IReadOnlyList<string> labels, string assetID, string assetType)
        {
            Address = address;
            Labels = labels;
            AssetID = assetID;
            AssetType = assetType;
        }
    }
}
