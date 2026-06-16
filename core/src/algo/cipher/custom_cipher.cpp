#include "custom_cipher.h"

// ================================================================
// Custom cipher stub — replace this file with your own algorithm.
//
// ECipherAlgorithm::custom routes here.  The default implementation
// is a passthrough (plaintext == ciphertext), i.e. no encryption.
// ================================================================

namespace roulin {

void CustomCipher::Encrypt(uint8_t* /*buf*/, size_t /*offset*/,
                            size_t /*len*/, const uint8_t* /*key*/,
                            size_t /*key_len*/) {
    // Passthrough — replace with your encryption logic.
}

void CustomCipher::Decrypt(uint8_t* /*buf*/, size_t /*offset*/,
                            size_t /*len*/, const uint8_t* /*key*/,
                            size_t /*key_len*/) {
    // Passthrough — replace with your decryption logic.
}

} // namespace roulin
