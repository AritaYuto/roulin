#pragma once
#include "roulin/hash.h"
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace roulin {

namespace Flags {
    constexpr uint8_t Encrypted  = 1 << 0;
    constexpr uint8_t Compressed = 1 << 1;
}

// Per-asset address record. Multiple Address records share an IndexEntry
// when they live in the same blob.
struct Address {
    std::string              address_str;
    std::string              asset_id;
    std::vector<std::string> labels;
    uint8_t                  flags        = 0;
    uint16_t                 key_id       = 0;
    std::vector<uint32_t>    type_idxs;       // indices into Index.types()
};

// 1 entry = 1 blob. addresses[] is 0..N records of the addressable assets
// living inside this blob; empty when the blob is purely a dep target.
struct IndexEntry {
    Hash32                   blob_hash{};
    uint64_t                 size_bytes = 0;
    std::vector<std::string> deps;
    std::vector<Address>     addresses;
};

// Read-only view of a serialized Index FlatBuffer.
class Index {
public:
    static Index LoadFromFile(const std::string& path);
    static Index FromBytes(std::vector<uint8_t> buffer);

    // Look up the IndexEntry that owns the given address. O(log n) via
    // an in-memory address64 → entry-index map built at FromBytes time.
    std::optional<IndexEntry> Get(std::string_view address) const;

    // Look up the IndexEntry by blob hash. O(log n) binary search on the
    // FlatBuffer (entries[] is sorted by blob_hash memcmp).
    std::optional<IndexEntry> GetByHash(const Hash32& blob_hash) const;

    // Walk every IndexEntry in blob_hash sort order.
    void ForEach(const std::function<void(const IndexEntry&)>& fn) const;

    size_t EntryCount() const;

    // Intern table of engine-specific type identifiers (Unity:
    // AssemblyQualifiedName). Address.type_idxs reference this list.
    const std::vector<std::string>& Types() const { return mTypes; }

private:
    explicit Index(std::vector<uint8_t> buffer);

    // address64 → index-into-entries[], sorted by address64 for binary search.
    struct AddressLookup {
        uint64_t address64;
        uint32_t entry_index;
    };

    std::vector<uint8_t>       mBuffer;
    std::vector<AddressLookup> mAddressMap;
    std::vector<std::string>   mTypes;
};

// Accumulates IndexEntry values and serializes them into a sorted FlatBuffer.
class IndexBuilder {
public:
    void Add(IndexEntry entry);

    // Intern table of engine-specific type identifiers. Address.type_idxs
    // reference this list. Optional; empty if no caller sets it.
    void SetTypes(std::vector<std::string> types) { mTypes = std::move(types); }

    // Sort entries by blob_hash (memcmp) and produce the FlatBuffer bytes.
    std::vector<uint8_t> Build() const;
    void                 SaveToFile(const std::string& path) const;

private:
    std::vector<IndexEntry>  mEntries;
    std::vector<std::string> mTypes;
};

} // namespace roulin
