#pragma once
#include "roulin/hash.h"
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace roulin {

// Reads blobs from the local filesystem.
// Expected on-disk layout (same as BlobStore produces):
//   {base_dir}/blobs/{hash[0:2]}/{full_hex_hash}
class BlobReader {
public:
    explicit BlobReader(std::string base_dir);

    bool   Exists(const Hash32& hash) const;
    size_t BlobSize(const Hash32& hash) const;

    // Reads up to 'size' bytes from the blob at byte 'offset' into 'buf'.
    // Returns the number of bytes actually written to buf.
    // Throws std::runtime_error if the blob does not exist.
    size_t Read(const Hash32& hash, size_t offset,
                uint8_t* buf, size_t size) const;

    // Convenience: allocates a vector and reads the entire blob into it.
    std::vector<uint8_t> ReadAll(const Hash32& hash) const {
        size_t n = BlobSize(hash);
        std::vector<uint8_t> buf(n);
        Read(hash, 0, buf.data(), n);
        return buf;
    }

    std::string BlobPath(const Hash32& hash) const;

private:
    std::string mBaseDir;
};

} // namespace roulin
