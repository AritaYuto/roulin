#include "roulin/compressor.h"

namespace roulin {
namespace {

struct NullCompressor final : public ICompressor {
    std::vector<uint8_t> Compress(const uint8_t* src, size_t len) override {
        return {src, src + len};
    }
    std::vector<uint8_t> Decompress(const uint8_t* src, size_t len, size_t) override {
        return {src, src + len};
    }
};

} // namespace

Compressor::Compressor() : mImpl(std::make_unique<NullCompressor>()) {}

std::vector<uint8_t> Compressor::Compress(const uint8_t* src, size_t len) {
    return mImpl->Compress(src, len);
}

std::vector<uint8_t> Compressor::Decompress(const uint8_t* src, size_t len,
                                              size_t original_size_hint) {
    return mImpl->Decompress(src, len, original_size_hint);
}

} // namespace roulin
