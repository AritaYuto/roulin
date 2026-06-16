#include "roulin/diff.h"

namespace roulin {

std::vector<MissingBlob> DiffIndex(const Index&      remote_index,
                                   const BlobReader& local_storage) {
    std::vector<MissingBlob> missing;
    remote_index.ForEach([&](const IndexEntry& entry) {
        if (local_storage.Exists(entry.blob_hash)) return;
        std::string label = entry.addresses.empty()
            ? std::string{}
            : entry.addresses.front().address_str;
        missing.push_back({std::move(label), entry.blob_hash});
    });
    return missing;
}

} // namespace roulin
