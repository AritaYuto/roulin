#pragma once
#include <cstddef>
#include <cstdint>
#include <memory>
#include <vector>

namespace roulin {

// Interface for custom compression algorithm implementations.
// Pipeline order is fixed: Compress → Encrypt on write, Decrypt → Decompress
// on read. Reversing this would produce random-looking ciphertext that cannot
// be compressed, wasting space.
class ICompressor {
public:
    virtual ~ICompressor() = default;
    virtual std::vector<uint8_t> Compress(const uint8_t* src, size_t len) = 0;
    virtual std::vector<uint8_t> Decompress(const uint8_t* src, size_t len,
                                             size_t original_size_hint = 0) = 0;
};

// Move-only wrapper that owns one compressor instance.
// Default ctor is a passthrough (NullCompressor); LZ4/Zstd are not yet wired in.
class Compressor {
public:
    Compressor();

    Compressor(const Compressor&)            = delete;
    Compressor& operator=(const Compressor&) = delete;
    Compressor(Compressor&&)                 = default;
    Compressor& operator=(Compressor&&)      = default;

    std::vector<uint8_t> Compress(const uint8_t* src, size_t len);
    std::vector<uint8_t> Decompress(const uint8_t* src, size_t len,
                                    size_t original_size_hint = 0);

private:
    std::unique_ptr<ICompressor> mImpl;
};

} // namespace roulin
