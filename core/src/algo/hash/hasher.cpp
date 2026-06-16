#include "roulin/hasher.h"
#include "algo/hash/blake3_hash.h"
#include "algo/hash/custom_hash.h"

namespace roulin {

Hasher::Hasher() : mImpl(std::make_unique<Blake3Hash>()) {}

Hasher::Hasher(EHashAlgorithm algo) {
    switch (algo) {
        case EHashAlgorithm::blake3:
            mImpl = std::make_unique<Blake3Hash>();
            break;
        case EHashAlgorithm::custom:
            mImpl = std::make_unique<CustomHash>();
            break;
    }
}

void Hasher::Update(const uint8_t* data, size_t len) {
    mImpl->Update(data, len);
}

Hash32 Hasher::Finalize() {
    return mImpl->Finalize();
}

} // namespace roulin
