#include "roulin/blob_reader.h"
#include "roulin/hash.h"
#include <filesystem>
#include <fstream>
#include <stdexcept>

namespace roulin {

namespace fs = std::filesystem;

BlobReader::BlobReader(std::string base_dir)
    : mBaseDir(std::move(base_dir)) {}

std::string BlobReader::BlobPath(const Hash32& hash) const {
    std::string hex = ToHex(hash);
    return mBaseDir + "/blobs/" + hex.substr(0, 2) + "/" + hex;
}

bool BlobReader::Exists(const Hash32& hash) const {
    return fs::exists(BlobPath(hash));
}

size_t BlobReader::BlobSize(const Hash32& hash) const {
    std::error_code ec;
    auto size = fs::file_size(BlobPath(hash), ec);
    if (ec) throw std::runtime_error(
        "BlobReader::BlobSize: " + ToHex(hash) + ": " + ec.message());
    return static_cast<size_t>(size);
}

size_t BlobReader::Read(const Hash32& hash, size_t offset,
                        uint8_t* buf, size_t size) const {
    std::string path = BlobPath(hash);
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error(
        "BlobReader::Read: blob not found: " + ToHex(hash));

    // Seeking past EOF on an empty file is implementation-defined; guard it.
    f.seekg(static_cast<std::streamoff>(offset));
    if (!f) return 0;

    f.read(reinterpret_cast<char*>(buf), static_cast<std::streamsize>(size));
    return static_cast<size_t>(f.gcount());
}

} // namespace roulin
