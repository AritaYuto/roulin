#pragma once
#include <cstddef>
#include <cstdint>
#include <memory>
#include "roulin/hash.h"

namespace roulin {

enum class EHashAlgorithm {
    blake3,
    custom,
};

// Interface for custom hash algorithm implementations.
class IHash {
public:
    virtual ~IHash() = default;
    virtual void   Update(const uint8_t* data, size_t len) = 0;
    virtual Hash32 Finalize() = 0;
};

// Move-only wrapper that owns one hash computation.
// The default ctor uses BLAKE3; the concrete type lives in src/ so this header
// does not expose the blake3 dependency to consumers.
// For parallel hashing, construct independent Hasher instances on each thread.
class Hasher {
public:
    Hasher();
    explicit Hasher(EHashAlgorithm algo);

    Hasher(const Hasher&)            = delete;
    Hasher& operator=(const Hasher&) = delete;
    Hasher(Hasher&&)                 = default;
    Hasher& operator=(Hasher&&)      = default;

    void   Update(const uint8_t* data, size_t len);
    Hash32 Finalize();

private:
    std::unique_ptr<IHash> mImpl;
};

} // namespace roulin
