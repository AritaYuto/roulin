#include "roulin/fetch/hashed_writer.h"
#include <filesystem>
#include <stdexcept>
#include <system_error>

namespace roulin::fetch {

namespace {

constexpr roulin::Hash32 kZeroHash{};

bool isZeroHash(const roulin::Hash32& h) {
    return h == kZeroHash;
}

}  // namespace

HashedWriter::HashedWriter(const std::string& dest_path,
                           const roulin::Hash32& expected_hash)
    : mDestPath(dest_path),
      mTempPath(dest_path + ".tmp"),
      mExpectedHash(expected_hash),
      mHaveExpectedHash(!isZeroHash(expected_hash)),
      mHasher(roulin::EHashAlgorithm::blake3) {
    std::error_code ec;
    auto parent = std::filesystem::path(mDestPath).parent_path();
    if (!parent.empty()) {
        std::filesystem::create_directories(parent, ec);
    }

    mStream.open(mTempPath, std::ios::out | std::ios::binary | std::ios::trunc);
    if (!mStream.is_open()) {
        throw std::runtime_error("HashedWriter: failed to open " + mTempPath);
    }
}

HashedWriter::~HashedWriter() {
    if (!mFinalized) {
        closeStream();
        removeTemp();
    }
}

bool HashedWriter::Write(const uint8_t* data, size_t len) {
    if (mIoFailed) return false;
    if (len == 0)  return true;
    mStream.write(reinterpret_cast<const char*>(data),
                  static_cast<std::streamsize>(len));
    if (!mStream) {
        mIoFailed = true;
        return false;
    }
    mHasher.Update(data, len);
    mBytesWritten += len;
    return true;
}

HashedWriter::FinalizeResult HashedWriter::Finalize() {
    if (mFinalized) return FinalizeResult::Ok;
    mFinalized = true;

    if (mIoFailed) {
        closeStream();
        removeTemp();
        return FinalizeResult::IoError;
    }

    closeStream();
    if (!mStream) {
        removeTemp();
        return FinalizeResult::IoError;
    }

    if (mHaveExpectedHash) {
        const roulin::Hash32 actual = mHasher.Finalize();
        if (actual != mExpectedHash) {
            removeTemp();
            return FinalizeResult::HashMismatch;
        }
    } else {
        (void)mHasher.Finalize();
    }

    std::error_code ec;
    std::filesystem::rename(mTempPath, mDestPath, ec);
    if (ec) {
        // Cross-filesystem fallback so the dest_path contract still holds.
        std::filesystem::copy_file(mTempPath, mDestPath,
                                   std::filesystem::copy_options::overwrite_existing,
                                   ec);
        if (ec) {
            removeTemp();
            return FinalizeResult::IoError;
        }
        std::filesystem::remove(mTempPath, ec);
    }
    return FinalizeResult::Ok;
}

void HashedWriter::Cancel() {
    if (mFinalized) return;
    mFinalized = true;
    closeStream();
    removeTemp();
}

bool HashedWriter::Reset() {
    if (mFinalized) return false;
    closeStream();
    removeTemp();
    mHasher = roulin::Hasher(roulin::EHashAlgorithm::blake3);
    mIoFailed = false;
    mBytesWritten = 0;
    mStream.clear();
    mStream.open(mTempPath, std::ios::out | std::ios::binary | std::ios::trunc);
    if (!mStream.is_open()) {
        mIoFailed = true;
        return false;
    }
    return true;
}

void HashedWriter::closeStream() {
    if (mStream.is_open()) {
        mStream.flush();
        mStream.close();
    }
}

void HashedWriter::removeTemp() {
    std::error_code ec;
    std::filesystem::remove(mTempPath, ec);
}

}  // namespace roulin::fetch
