#include "roulin/cipher.h"
#include "algo/cipher/chacha20_cipher.h"
#include "algo/cipher/custom_cipher.h"

namespace roulin {

Cipher::Cipher() : mImpl(std::make_unique<ChaCha20Cipher>()) {}

Cipher::Cipher(ECipherAlgorithm algo) {
    switch (algo) {
        case ECipherAlgorithm::chacha20:
            mImpl = std::make_unique<ChaCha20Cipher>();
            break;
        case ECipherAlgorithm::custom:
            mImpl = std::make_unique<CustomCipher>();
            break;
    }
}

void Cipher::Encrypt(uint8_t* buf, size_t offset, size_t len,
                     const uint8_t* key, size_t key_len) {
    mImpl->Encrypt(buf, offset, len, key, key_len);
}

void Cipher::Decrypt(uint8_t* buf, size_t offset, size_t len,
                     const uint8_t* key, size_t key_len) {
    mImpl->Decrypt(buf, offset, len, key, key_len);
}

} // namespace roulin
