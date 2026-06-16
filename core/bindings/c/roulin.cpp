#include "roulin.h"
#include "error_internal.h"
#include "roulin/index.h"
#include "roulin/diff.h"
#include "roulin/blob_reader.h"
#include "roulin/hash.h"
#include "roulin/hasher.h"
#include <cstdlib>
#include <cstring>
#include <optional>
#include <string>
#include <vector>

// ---- Opaque handle types ---------------------------------------------------

struct ACParcel {
    std::optional<roulin::Index>      index;        // always set after construction
    std::optional<roulin::BlobReader> storage;      // always set after construction
    std::string                        base_dir;     // root of the local cache
    std::string                        revision_id;
};

// Non-owning back-pointer to the ACParcel's BlobReader; an ACBlob must not
// outlive its parent ACParcel.
struct ACBlob {
    roulin::Hash32           hash;
    const roulin::BlobReader* storage;
};

// ---- Thread-local error storage --------------------------------------------
//
// Definitions for the shared tl_error / clearError / setError declared in
// error_internal.h. Lives here (rather than in its own .cpp) so callers only
// need to link roulin-core to use the helpers.

namespace roulin::error {

thread_local std::string tl_error;

void clearError() noexcept { tl_error.clear(); }

void setError(const char* prefix, const std::exception& e) noexcept {
    try { tl_error = std::string(prefix) + ": " + e.what(); }
    catch (...) { tl_error = prefix; }
}

void setError(const char* prefix, const char* detail) noexcept {
    try { tl_error = std::string(prefix) + ": " + detail; }
    catch (...) { tl_error = prefix; }
}

} // namespace roulin::error

namespace {

using roulin::error::clearError;
using roulin::error::setError;
using roulin::error::tl_error;

// Copies a string into malloc memory for C callers. Returns NULL on OOM.
char* mallocStr(const std::string& s) {
    auto* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (p) std::memcpy(p, s.data(), s.size() + 1);
    return p;
}

void freeStr(const char* p) noexcept {
    std::free(const_cast<char*>(p));
}

} // namespace

extern "C" {

// ---- Error reporting -------------------------------------------------------

const char* rln_last_error(void) {
    return tl_error.empty() ? nullptr : tl_error.c_str();
}

// ---- Hashing ---------------------------------------------------------------

void rln_compute_blake3(const void* data, size_t len, uint8_t out_hash[32]) {
    roulin::Hasher h(roulin::EHashAlgorithm::blake3);
    if (data && len > 0) {
        h.Update(static_cast<const uint8_t*>(data), len);
    }
    roulin::Hash32 digest = h.Finalize();
    std::memcpy(out_hash, digest.data(), 32);
}

// ---- Parcel ----------------------------------------------------------------

ACParcel* rln_parcel_open(const char* local_dir, const char* revision_id) {
    clearError();
    if (!local_dir || !revision_id) {
        setError("rln_parcel_open", "null argument");
        return nullptr;
    }
    try {
        auto index = roulin::Index::LoadFromFile(
            std::string(local_dir) + "/index/" + revision_id);
        auto* p = new ACParcel{};
        p->index.emplace(std::move(index));
        p->storage.emplace(local_dir);
        p->base_dir    = local_dir;
        p->revision_id = revision_id;
        return p;
    } catch (const std::exception& e) {
        setError("rln_parcel_open", e);
        return nullptr;
    } catch (...) {
        setError("rln_parcel_open", "unknown error");
        return nullptr;
    }
}

void rln_parcel_close(ACParcel* parcel) {
    delete parcel;
}

// ---- Blob ------------------------------------------------------------------

ACBlob* rln_parcel_get(ACParcel* parcel, const char* address) {
    clearError();
    if (!parcel || !address) {
        setError("rln_parcel_get", "null argument");
        return nullptr;
    }
    try {
        auto entry = parcel->index->Get(address);
        if (!entry) {
            setError("rln_parcel_get",
                     (std::string("address not found: ") + address).c_str());
            return nullptr;
        }
        auto* b = new ACBlob{};
        b->hash    = entry->blob_hash;
        b->storage = &*parcel->storage;
        return b;
    } catch (const std::exception& e) {
        setError("rln_parcel_get", e);
        return nullptr;
    } catch (...) {
        setError("rln_parcel_get", "unknown error");
        return nullptr;
    }
}

void rln_blob_release(ACBlob* blob) {
    delete blob;
}

size_t rln_blob_size(ACBlob* blob) {
    clearError();
    if (!blob) { setError("rln_blob_size", "null blob handle"); return 0; }
    try {
        return blob->storage->BlobSize(blob->hash);
    } catch (const std::exception& e) {
        setError("rln_blob_size", e);
        return 0;
    } catch (...) {
        setError("rln_blob_size", "unknown error");
        return 0;
    }
}

int64_t rln_blob_read(ACBlob* blob, void* buf, size_t offset, size_t len) {
    clearError();
    if (!blob || !buf) { setError("rln_blob_read", "null argument"); return -1; }
    try {
        return static_cast<int64_t>(
            blob->storage->Read(blob->hash, offset,
                                static_cast<uint8_t*>(buf), len));
    } catch (const std::exception& e) {
        setError("rln_blob_read", e);
        return -1;
    } catch (...) {
        setError("rln_blob_read", "unknown error");
        return -1;
    }
}

// ---- Stream (not yet implemented) ------------------------------------------

ACStream* rln_blob_open_stream(ACBlob*) {
    setError("rln_blob_open_stream", "not yet implemented");
    return nullptr;
}
int  rln_stream_read_at(ACStream*, void*, size_t, size_t) { return -1; }
void rln_stream_close(ACStream*) {}

// ---- Bundle dependencies ---------------------------------------------------

void rln_strings_free(const char** strs, size_t count) {
    if (!strs) return;
    for (size_t i = 0; i < count; ++i) freeStr(strs[i]);
    std::free(const_cast<const char**>(strs));
}

const char** rln_index_bundle_deps_for(ACParcel*     parcel,
                                       const uint8_t bundle_hash[32],
                                       size_t*       out_count) {
    clearError();
    if (!parcel || !bundle_hash || !out_count) {
        setError("rln_index_bundle_deps_for", "null argument");
        if (out_count) *out_count = 0;
        return nullptr;
    }
    try {
        roulin::Hash32 h{};
        std::copy(bundle_hash, bundle_hash + 32, h.begin());
        auto entry = parcel->index->GetByHash(h);
        if (!entry || entry->deps.empty()) { *out_count = 0; return nullptr; }
        const auto& deps = entry->deps;
        *out_count = deps.size();

        auto** arr = static_cast<const char**>(
            std::malloc(deps.size() * sizeof(char*)));
        if (!arr) { setError("rln_index_bundle_deps_for", "malloc failed"); return nullptr; }

        for (size_t i = 0; i < deps.size(); ++i) {
            arr[i] = mallocStr(deps[i]);
            if (!arr[i]) {
                for (size_t j = 0; j < i; ++j) freeStr(arr[j]);
                std::free(arr);
                setError("rln_index_bundle_deps_for", "malloc failed");
                return nullptr;
            }
        }
        return arr;
    } catch (const std::exception& e) {
        setError("rln_index_bundle_deps_for", e);
        return nullptr;
    } catch (...) {
        setError("rln_index_bundle_deps_for", "unknown error");
        return nullptr;
    }
}

void rln_index_for_each_bundle_deps(ACParcel*      parcel,
                                     ACBundleDepsFn fn,
                                     void*          userdata) {
    clearError();
    if (!parcel || !fn || !parcel->index) {
        setError("rln_index_for_each_bundle_deps", "null argument");
        return;
    }
    // Wrap the entire walk: any throw (internal C++ or callback ABI violation)
    // aborts iteration and surfaces via rln_last_error() rather than unwinding
    // through the C ABI boundary as UB.
    try {
        parcel->index->ForEach([&](const roulin::IndexEntry& e) {
            // Emits the callback for every IndexEntry, including those with empty
            // deps. The runtime uses this walk both to wire dep edges and to
            // enumerate every blob the parcel knows about (so bundles with
            // addresses[]=[] and deps=[] still surface here).
            std::vector<const char*> dep_ptrs;
            dep_ptrs.reserve(e.deps.size());
            for (const auto& d : e.deps) dep_ptrs.push_back(d.c_str());

            fn(e.blob_hash.data(),
               dep_ptrs.empty() ? nullptr : dep_ptrs.data(),
               dep_ptrs.size(),
               userdata);
        });
    } catch (const std::exception& e) {
        setError("rln_index_for_each_bundle_deps", e);
    } catch (...) {
        setError("rln_index_for_each_bundle_deps", "unknown error");
    }
}

// ---- Diff ------------------------------------------------------------------

int rln_parcel_diff(ACParcel*       remote_parcel,
                   const char*     local_dir,
                   ACMissingBlob** out_blobs,
                   size_t*         out_count) {
    clearError();
    if (!remote_parcel || !local_dir || !out_blobs || !out_count) {
        setError("rln_parcel_diff", "null argument");
        return -1;
    }
    try {
        roulin::BlobReader local(local_dir);
        auto missing = roulin::DiffIndex(*remote_parcel->index, local);

        *out_count = missing.size();
        if (missing.empty()) { *out_blobs = nullptr; return 0; }

        auto* arr = static_cast<ACMissingBlob*>(
            std::malloc(missing.size() * sizeof(ACMissingBlob)));
        if (!arr) { setError("rln_parcel_diff", "malloc failed"); return -1; }

        for (size_t i = 0; i < missing.size(); ++i) {
            arr[i].address = mallocStr(missing[i].address);
            if (!arr[i].address) {
                for (size_t j = 0; j < i; ++j) freeStr(arr[j].address);
                std::free(arr);
                setError("rln_parcel_diff", "malloc failed");
                *out_count = 0;
                return -1;
            }
            std::memcpy(arr[i].blob_hash, missing[i].blob_hash.data(), 32);
        }
        *out_blobs = arr;
        return static_cast<int>(missing.size());
    } catch (const std::exception& e) {
        setError("rln_parcel_diff", e);
        return -1;
    } catch (...) {
        setError("rln_parcel_diff", "unknown error");
        return -1;
    }
}

void rln_diff_free(ACMissingBlob* blobs, size_t count) {
    if (!blobs) return;
    for (size_t i = 0; i < count; ++i) freeStr(blobs[i].address);
    std::free(blobs);
}

// ---- Enumeration -----------------------------------------------------------

void rln_parcel_foreach(ACParcel* parcel, ACForEachFn fn, void* userdata) {
    clearError();
    if (!parcel || !fn || !parcel->index) {
        setError("rln_parcel_foreach", "null argument");
        return;
    }
    // Wrap the entire walk: any throw (internal C++ or callback ABI violation)
    // aborts iteration and surfaces via rln_last_error() rather than unwinding
    // through the C ABI boundary as UB.
    try {
        parcel->index->ForEach([&](const roulin::IndexEntry& e) {
            // One callback per address. Blobs with addresses[]=[] (= pure dep
            // targets) emit nothing here; enumerate them via
            // rln_index_for_each_bundle_deps, which fires once per IndexEntry.
            for (const auto& a : e.addresses) {
                std::vector<const char*> label_ptrs;
                label_ptrs.reserve(a.labels.size());
                for (const auto& l : a.labels) label_ptrs.push_back(l.c_str());

                fn(a.address_str.c_str(),
                   e.blob_hash.data(),
                   e.size_bytes,
                   label_ptrs.empty() ? nullptr : label_ptrs.data(),
                   label_ptrs.size(),
                   a.asset_id.empty() ? nullptr : a.asset_id.c_str(),
                   a.type_idxs.empty() ? nullptr : a.type_idxs.data(),
                   a.type_idxs.size(),
                   userdata);
            }
        });
    } catch (const std::exception& e) {
        setError("rln_parcel_foreach", e);
    } catch (...) {
        setError("rln_parcel_foreach", "unknown error");
    }
}

size_t rln_index_types_count(ACParcel* parcel) {
    clearError();
    if (!parcel || !parcel->index) {
        setError("rln_index_types_count", "null argument");
        return 0;
    }
    return parcel->index->Types().size();
}

const char* rln_index_type_at(ACParcel* parcel, size_t idx) {
    clearError();
    if (!parcel || !parcel->index) {
        setError("rln_index_type_at", "null argument");
        return nullptr;
    }
    const auto& types = parcel->index->Types();
    if (idx >= types.size()) {
        setError("rln_index_type_at", "index out of range");
        return nullptr;
    }
    return types[idx].c_str();
}

} // extern "C"
