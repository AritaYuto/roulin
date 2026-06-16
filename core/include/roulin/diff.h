#pragma once
#include "roulin/hash.h"
#include "roulin/index.h"
#include "roulin/blob_reader.h"
#include <string>
#include <vector>

namespace roulin {

// A blob that exists in the remote Index but is absent from local storage.
struct MissingBlob {
    std::string address;    // logical address, e.g. "ui/icons/player"
    Hash32      blob_hash;
};

// Compares the remote Index against what is already present in local_storage.
// Returns one entry per address whose blob is not found locally.
// The caller uses the result to build a download queue for roulin-fetch.
std::vector<MissingBlob> DiffIndex(const Index&      remote_index,
                                   const BlobReader& local_storage);

} // namespace roulin
