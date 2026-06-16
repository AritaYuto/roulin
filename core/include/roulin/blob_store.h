#pragma once
#include "roulin/hash.h"
#include "roulin/hasher.h"
#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace roulin {

// Content-addressed blob storage.
// On-disk layout: {base_dir}/blobs/{hash[0:2]}/{full_hex_hash}
// The first byte of the hash is used as a subdirectory prefix, limiting
// directory entries to at most 256 — the same trick Git uses for its objects.
class BlobStore {
public:
    // make_hasher is called once per Write/Verify to produce a fresh Hasher.
    // A factory is used rather than a stored instance because Hasher is
    // move-only; each operation needs its own independent hash state.

    explicit BlobStore(std::string base_dir,
                       std::function<Hasher()> make_hasher = [] { return Hasher{}; });

    Hash32               Write(const uint8_t* data, size_t len);
    std::vector<uint8_t> Read(const Hash32& hash) const;
    bool                 Verify(const Hash32& hash) const;
    bool                 Exists(const Hash32& hash) const;
    std::string          BlobPath(const Hash32& hash) const;

private:
    std::string             mBaseDir;
    std::function<Hasher()> mMakeHasher;
};

} // namespace roulin
