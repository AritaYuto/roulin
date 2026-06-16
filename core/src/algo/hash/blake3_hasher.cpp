#include "algo/hash/blake3_hash.h"
#include <blake3.h>

namespace roulin {

struct Blake3Hash::Impl {
    blake3_hasher state;
};

Blake3Hash::Blake3Hash() : mImpl(std::make_unique<Impl>()) {
    blake3_hasher_init(&mImpl->state);
}

Blake3Hash::~Blake3Hash() = default;

void Blake3Hash::Update(const uint8_t* data, size_t len) {
    blake3_hasher_update(&mImpl->state, data, len);
}

Hash32 Blake3Hash::Finalize() {
    Hash32 out{};
    blake3_hasher_finalize(&mImpl->state, out.data(), BLAKE3_OUT_LEN);
    return out;
}

} // namespace roulin
