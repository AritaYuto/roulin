#include "roulin/index.h"
#include "parcel/address_hash.h"
#include "index_generated.h"
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>

namespace roulin {

namespace {

// Binary search on entries[] sorted by blob_hash (memcmp).
// Returns nullptr if hash not present.
const roulin_fbs::IndexEntry* FindByBlobHash(
    const flatbuffers::Vector<flatbuffers::Offset<roulin_fbs::IndexEntry>>* entries,
    const Hash32& hash)
{
    if (!entries || entries->size() == 0) return nullptr;
    size_t lo = 0, hi = entries->size();
    while (lo < hi) {
        const size_t mid = lo + (hi - lo) / 2;
        const auto*  e   = entries->Get(mid);
        const auto*  bh  = e ? e->blob_hash() : nullptr;
        if (!bh || bh->size() != 32) { lo = mid + 1; continue; }
        const int cmp = std::memcmp(bh->data(), hash.data(), 32);
        if      (cmp < 0) lo = mid + 1;
        else if (cmp > 0) hi = mid;
        else              return e;
    }
    return nullptr;
}

Address ToAddress(const roulin_fbs::Address* a) {
    Address out;
    if (a->address_str()) out.address_str = a->address_str()->str();
    if (a->asset_id())    out.asset_id    = a->asset_id()->str();
    if (auto labels = a->labels()) {
        out.labels.reserve(labels->size());
        for (flatbuffers::uoffset_t i = 0; i < labels->size(); ++i)
            if (auto s = labels->Get(i)) out.labels.push_back(s->str());
    }
    out.flags  = a->flags();
    out.key_id = a->key_id();
    if (auto idxs = a->type_idxs()) {
        out.type_idxs.reserve(idxs->size());
        for (flatbuffers::uoffset_t i = 0; i < idxs->size(); ++i)
            out.type_idxs.push_back(idxs->Get(i));
    }
    return out;
}

IndexEntry ToIndexEntry(const roulin_fbs::IndexEntry* e) {
    IndexEntry out;
    if (auto bh = e->blob_hash(); bh && bh->size() == 32)
        std::copy(bh->begin(), bh->end(), out.blob_hash.begin());
    out.size_bytes = e->size_bytes();
    if (auto deps = e->deps()) {
        out.deps.reserve(deps->size());
        for (flatbuffers::uoffset_t i = 0; i < deps->size(); ++i)
            if (auto s = deps->Get(i)) out.deps.push_back(s->str());
    }
    if (auto addresses = e->addresses()) {
        out.addresses.reserve(addresses->size());
        for (flatbuffers::uoffset_t i = 0; i < addresses->size(); ++i)
            if (auto a = addresses->Get(i)) out.addresses.push_back(ToAddress(a));
    }
    return out;
}

} // namespace

// ---- Index -----------------------------------------------------------------

Index Index::LoadFromFile(const std::string& path) {
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) throw std::runtime_error("Index::LoadFromFile: cannot open " + path);
    auto size = f.tellg();
    f.seekg(0);
    std::vector<uint8_t> buf(static_cast<size_t>(size));
    f.read(reinterpret_cast<char*>(buf.data()), size);
    return FromBytes(std::move(buf));
}

Index Index::FromBytes(std::vector<uint8_t> buffer) {
#ifndef NDEBUG
    flatbuffers::Verifier verifier(buffer.data(), buffer.size());
    if (!roulin_fbs::VerifyIndexBuffer(verifier))
        throw std::runtime_error("Index::FromBytes: invalid buffer");
#endif
    return Index(std::move(buffer));
}

Index::Index(std::vector<uint8_t> buffer) : mBuffer(std::move(buffer)) {
    auto* root = roulin_fbs::GetIndex(mBuffer.data());

    if (auto types = root->types()) {
        mTypes.reserve(types->size());
        for (flatbuffers::uoffset_t i = 0; i < types->size(); ++i)
            if (auto s = types->Get(i)) mTypes.push_back(s->str());
    }

    // Build address64 → entry_index map for O(log n) Get(address) lookup.
    auto entries = root->entries();
    if (!entries) return;

    for (flatbuffers::uoffset_t i = 0; i < entries->size(); ++i) {
        const auto* e = entries->Get(i);
        if (!e) continue;
        auto* addrs = e->addresses();
        if (!addrs) continue;
        for (flatbuffers::uoffset_t j = 0; j < addrs->size(); ++j) {
            const auto* a = addrs->Get(j);
            if (!a) continue;
            mAddressMap.push_back({a->address64(), i});
        }
    }
    std::sort(mAddressMap.begin(), mAddressMap.end(),
              [](const AddressLookup& x, const AddressLookup& y) {
                  return x.address64 < y.address64;
              });
}

std::optional<IndexEntry> Index::Get(std::string_view address) const {
    if (mAddressMap.empty()) return std::nullopt;
    const uint64_t target = HashAddress(address);

    size_t lo = 0, hi = mAddressMap.size();
    while (lo < hi) {
        const size_t mid = lo + (hi - lo) / 2;
        const auto&  rec = mAddressMap[mid];
        if      (rec.address64 < target) lo = mid + 1;
        else if (rec.address64 > target) hi = mid;
        else {
            // Verify the full string against an Address inside the entry to
            // rule out a 64-bit hash collision.
            auto entries = roulin_fbs::GetIndex(mBuffer.data())->entries();
            const auto* e = entries->Get(rec.entry_index);
            if (!e || !e->addresses()) return std::nullopt;
            for (flatbuffers::uoffset_t i = 0; i < e->addresses()->size(); ++i) {
                const auto* a = e->addresses()->Get(i);
                if (a && a->address64() == target && a->address_str()
                    && a->address_str()->string_view() == address)
                    return ToIndexEntry(e);
            }
            return std::nullopt;
        }
    }
    return std::nullopt;
}

std::optional<IndexEntry> Index::GetByHash(const Hash32& blob_hash) const {
    auto entries = roulin_fbs::GetIndex(mBuffer.data())->entries();
    auto e = FindByBlobHash(entries, blob_hash);
    if (!e) return std::nullopt;
    return ToIndexEntry(e);
}

void Index::ForEach(const std::function<void(const IndexEntry&)>& fn) const {
    auto entries = roulin_fbs::GetIndex(mBuffer.data())->entries();
    if (!entries) return;
    for (flatbuffers::uoffset_t i = 0; i < entries->size(); ++i)
        fn(ToIndexEntry(entries->Get(i)));
}

size_t Index::EntryCount() const {
    auto entries = roulin_fbs::GetIndex(mBuffer.data())->entries();
    return entries ? entries->size() : 0;
}

// ---- IndexBuilder ----------------------------------------------------------

void IndexBuilder::Add(IndexEntry entry) {
    mEntries.push_back(std::move(entry));
}

std::vector<uint8_t> IndexBuilder::Build() const {
    // Sort by blob_hash (memcmp) for deterministic order and binary search.
    std::vector<IndexEntry> sorted = mEntries;
    std::sort(sorted.begin(), sorted.end(),
              [](const IndexEntry& a, const IndexEntry& b) {
                  return std::memcmp(a.blob_hash.data(), b.blob_hash.data(), 32) < 0;
              });

    flatbuffers::FlatBufferBuilder fbb;
    std::vector<flatbuffers::Offset<roulin_fbs::IndexEntry>> entry_offsets;
    entry_offsets.reserve(sorted.size());

    for (const auto& e : sorted) {
        // Build all nested objects before opening any table builder
        // (FlatBuffers forbids interleaved table construction).
        auto blob_hash_off = fbb.CreateVector(e.blob_hash.data(), e.blob_hash.size());

        flatbuffers::Offset<flatbuffers::Vector<flatbuffers::Offset<flatbuffers::String>>> deps_off;
        if (!e.deps.empty()) {
            std::vector<flatbuffers::Offset<flatbuffers::String>> dep_strs;
            dep_strs.reserve(e.deps.size());
            for (const auto& d : e.deps) dep_strs.push_back(fbb.CreateString(d));
            deps_off = fbb.CreateVector(dep_strs);
        }

        std::vector<flatbuffers::Offset<roulin_fbs::Address>> addr_offs;
        addr_offs.reserve(e.addresses.size());
        for (const auto& a : e.addresses) {
            auto addr_str_off = fbb.CreateString(a.address_str);
            flatbuffers::Offset<flatbuffers::String> asset_id_off;
            if (!a.asset_id.empty()) asset_id_off = fbb.CreateString(a.asset_id);

            flatbuffers::Offset<flatbuffers::Vector<flatbuffers::Offset<flatbuffers::String>>> labels_off;
            if (!a.labels.empty()) {
                std::vector<flatbuffers::Offset<flatbuffers::String>> label_strs;
                label_strs.reserve(a.labels.size());
                for (const auto& l : a.labels) label_strs.push_back(fbb.CreateString(l));
                labels_off = fbb.CreateVector(label_strs);
            }

            flatbuffers::Offset<flatbuffers::Vector<uint32_t>> type_idxs_off;
            if (!a.type_idxs.empty()) {
                type_idxs_off = fbb.CreateVector(a.type_idxs);
            }

            roulin_fbs::AddressBuilder ab(fbb);
            ab.add_address64(HashAddress(a.address_str));
            ab.add_address_str(addr_str_off);
            if (!a.asset_id.empty())   ab.add_asset_id(asset_id_off);
            if (!a.labels.empty())     ab.add_labels(labels_off);
            ab.add_flags(a.flags);
            ab.add_key_id(a.key_id);
            if (!a.type_idxs.empty())  ab.add_type_idxs(type_idxs_off);
            addr_offs.push_back(ab.Finish());
        }
        auto addresses_vec = addr_offs.empty()
            ? flatbuffers::Offset<flatbuffers::Vector<flatbuffers::Offset<roulin_fbs::Address>>>(0)
            : fbb.CreateVector(addr_offs);

        roulin_fbs::IndexEntryBuilder eb(fbb);
        eb.add_blob_hash(blob_hash_off);
        if (e.size_bytes != 0) eb.add_size_bytes(e.size_bytes);
        if (!e.deps.empty())   eb.add_deps(deps_off);
        if (!addr_offs.empty()) eb.add_addresses(addresses_vec);
        entry_offsets.push_back(eb.Finish());
    }

    auto entries_vec = fbb.CreateVector(entry_offsets);

    flatbuffers::Offset<flatbuffers::Vector<flatbuffers::Offset<flatbuffers::String>>> types_vec;
    if (!mTypes.empty()) {
        std::vector<flatbuffers::Offset<flatbuffers::String>> type_strs;
        type_strs.reserve(mTypes.size());
        for (const auto& t : mTypes) type_strs.push_back(fbb.CreateString(t));
        types_vec = fbb.CreateVector(type_strs);
    }

    roulin_fbs::IndexBuilder ib(fbb);
    ib.add_entries(entries_vec);
    if (!mTypes.empty()) ib.add_types(types_vec);
    fbb.Finish(ib.Finish());

    const uint8_t* ptr = fbb.GetBufferPointer();
    return {ptr, ptr + fbb.GetSize()};
}

void IndexBuilder::SaveToFile(const std::string& path) const {
    auto buf = Build();
    std::filesystem::create_directories(std::filesystem::path(path).parent_path());
    std::ofstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("IndexBuilder::SaveToFile: cannot open " + path);
    f.write(reinterpret_cast<const char*>(buf.data()), static_cast<std::streamsize>(buf.size()));
}

} // namespace roulin
