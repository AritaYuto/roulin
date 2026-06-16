#pragma once
#include "roulin/hash.h"
#include "roulin/hasher.h"
#include <cstdint>
#include <fstream>
#include <string>

namespace roulin::fetch {

// Atomicity invariant: dest_path either does not exist or holds bytes whose
// BLAKE3 equals expected_hash. Partial / mismatched bodies live only in
// <dest_path>.tmp and are removed on every failure path.
class HashedWriter {
public:
    enum class FinalizeResult {
        Ok,
        HashMismatch,
        IoError,
    };

    HashedWriter(const std::string& dest_path,
                 const roulin::Hash32& expected_hash);

    ~HashedWriter();

    HashedWriter(const HashedWriter&)            = delete;
    HashedWriter& operator=(const HashedWriter&) = delete;
    HashedWriter(HashedWriter&&)                 = delete;
    HashedWriter& operator=(HashedWriter&&)      = delete;

    // Returns false on I/O error; Finalize() then returns IoError.
    bool Write(const uint8_t* data, size_t len);

    uint64_t BytesWritten() const { return mBytesWritten; }

    FinalizeResult Finalize();

    void Cancel();

    // Discards the temp file and re-opens for a fresh attempt. Returns false
    // if the temp file cannot be re-opened.
    bool Reset();

private:
    void closeStream();
    void removeTemp();

    std::string      mDestPath;
    std::string      mTempPath;
    roulin::Hash32  mExpectedHash;
    bool             mHaveExpectedHash = false;
    std::ofstream    mStream;
    roulin::Hasher  mHasher;
    bool             mIoFailed   = false;
    bool             mFinalized  = false;
    uint64_t         mBytesWritten = 0;
};

}  // namespace roulin::fetch
