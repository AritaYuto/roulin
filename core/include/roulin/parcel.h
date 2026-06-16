#pragma once
#include "roulin/index.h"
#include <optional>
#include <string>
#include <string_view>

namespace roulin {

// A snapshot of all assets at a given revision — the "Parcel" delivered by
// the build pipeline and opened at runtime.
//
// On-disk layout:
//   {base_dir}/index/{revision_id}      - Index (required)
//   {base_dir}/blobs/{hash[0:2]}/{hash} - Blob content files
class Parcel {
public:
    // Opens an existing Parcel. Throws std::runtime_error if the Index is missing.
    static Parcel Open(std::string base_dir, std::string revision_id);

    std::optional<IndexEntry> Get(std::string_view address) const;

    const Index&     GetIndex()     const { return mIndex; }

    std::string_view BaseDir()     const { return mBaseDir; }
    std::string_view RevisionId()  const { return mRevisionId; }

private:
    Parcel(std::string base_dir, std::string revision_id, Index index);

    std::string              mBaseDir;
    std::string              mRevisionId;
    Index                    mIndex;
};

} // namespace roulin
