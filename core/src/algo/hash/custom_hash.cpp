#include "custom_hash.h"

// ================================================================
// Custom hash stub — replace this file with your own algorithm.
//
// EHashAlgorithm::custom routes here.  The default implementation
// is a trivial XOR accumulator: it is NOT cryptographically secure
// and should NOT be used in production builds as-is.
// ================================================================

namespace roulin {

void CustomHash::Update(const uint8_t* data, size_t len) {
    for (size_t i = 0; i < len; ++i) {
        mAcc[i % 32] ^= data[i];
    }
}

Hash32 CustomHash::Finalize() {
    return mAcc;
}

} // namespace roulin
