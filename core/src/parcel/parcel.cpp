#include "roulin/parcel.h"
#include <stdexcept>

namespace roulin {

Parcel Parcel::Open(std::string base_dir, std::string revision_id) {
    std::string path = base_dir + "/index/" + revision_id;
    auto index = Index::LoadFromFile(path);
    return Parcel(std::move(base_dir), std::move(revision_id), std::move(index));
}

Parcel::Parcel(std::string base_dir, std::string revision_id, Index index)
    : mBaseDir(std::move(base_dir))
    , mRevisionId(std::move(revision_id))
    , mIndex(std::move(index)) {}

std::optional<IndexEntry> Parcel::Get(std::string_view address) const {
    return mIndex.Get(address);
}

} // namespace roulin
