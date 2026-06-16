#pragma once
#include "roulin/hasher.h"

namespace roulin {

// Stub implementation of IHash for EHashAlgorithm::custom.
//
// HOW TO CUSTOMIZE:
//   Replace the Update() and Finalize() logic in custom_hash.cpp
//   with your own hash algorithm. This file (the class declaration)
//   can be left as-is unless you need additional state.
class CustomHash final : public IHash {
public:
    void   Update(const uint8_t* data, size_t len) override;
    Hash32 Finalize() override;

private:
    Hash32 mAcc{};  // accumulator — replace with your own state
};

} // namespace roulin
