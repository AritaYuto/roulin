#pragma once
#include "roulin/cipher.h"

namespace roulin {

// RFC 8439 ChaCha20 stream cipher with random-access support.
// key_len == 32: 32-byte key, nonce is implicitly all zeros.
// key_len == 44: bytes [0, 32) = key, bytes [32, 44) = 12-byte nonce.
class ChaCha20Cipher final : public ICipher {
public:
    void Encrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len) override;
    void Decrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len) override;
};

} // namespace roulin
