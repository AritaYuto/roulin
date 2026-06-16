#pragma once
#include "roulin/cipher.h"

namespace roulin {

// Stub implementation of ICipher for ECipherAlgorithm::custom.
//
// HOW TO CUSTOMIZE:
//   Replace the Encrypt() and Decrypt() logic in custom_cipher.cpp
//   with your own stream cipher. The cipher MUST support random-access
//   decryption (seeking to an arbitrary byte offset) because
//   AssetBundle.LoadFromStream reads at arbitrary positions.
class CustomCipher final : public ICipher {
public:
    // Default: passthrough (no encryption). Replace with your cipher.
    void Encrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len) override;
    void Decrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len) override;
};

} // namespace roulin
