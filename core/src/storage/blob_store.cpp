#include "roulin/blob_store.h"
#include <filesystem>
#include <fstream>
#include <stdexcept>

namespace roulin {

namespace fs = std::filesystem;

BlobStore::BlobStore(std::string base_dir, std::function<Hasher()> make_hasher)
    : mBaseDir(std::move(base_dir))
    , mMakeHasher(std::move(make_hasher)) {
    fs::create_directories(mBaseDir + "/blobs");
}

std::string BlobStore::BlobPath(const Hash32& hash) const {
    std::string hex = ToHex(hash);
    return mBaseDir + "/blobs/" + hex.substr(0, 2) + "/" + hex;
}

Hash32 BlobStore::Write(const uint8_t* data, size_t len) {
    Hasher h = mMakeHasher();
    h.Update(data, len);
    Hash32 hash = h.Finalize();

    if (Exists(hash)) return hash;

    std::string final_path = BlobPath(hash);
    fs::create_directories(fs::path(final_path).parent_path());

    std::string temp_path = final_path + ".tmp" + ToHex(hash).substr(0, 8);
    {
        std::ofstream temp(temp_path, std::ios::binary);
        if (!temp) throw std::runtime_error("BlobStore::Write: cannot open " + temp_path);
        temp.write(reinterpret_cast<const char*>(data), static_cast<std::streamsize>(len));
        temp.flush();
        if(!temp) {
            fs::remove(temp_path);
            throw std::runtime_error("BlobStore::Write: failed to write data to " + temp_path);
        }
    }

    std::error_code ec;
    fs::rename(temp_path, final_path, ec);
    if (ec) fs::remove(temp_path);

    if (!Exists(hash)) {
        throw std::runtime_error("BlobStore::Write: failed to write blob: " + ToHex(hash));
    }
    return hash;
}

std::vector<uint8_t> BlobStore::Read(const Hash32& hash) const {
    std::string path = BlobPath(hash);
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) throw std::runtime_error("BlobStore::Read: blob not found: " + ToHex(hash));

    auto size = f.tellg();
    f.seekg(0);
    std::vector<uint8_t> buf(static_cast<size_t>(size));
    f.read(reinterpret_cast<char*>(buf.data()), size);
    return buf;
}

bool BlobStore::Exists(const Hash32& hash) const {
    return fs::exists(BlobPath(hash));
}

bool BlobStore::Verify(const Hash32& hash) const {
    if (!Exists(hash)) return false;
    try {
        auto data = Read(hash);
        Hasher h = mMakeHasher();
        h.Update(data.data(), data.size());
        return h.Finalize() == hash;
    } catch (...) {
        return false;
    }
}

} // namespace roulin
