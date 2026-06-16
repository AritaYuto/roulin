#pragma once
#include <cstddef>
#include <cstdint>
#include <memory>

namespace roulin {

enum class ECipherAlgorithm {
    chacha20,
    custom,
};

// Interface for custom stream cipher implementations.
// Implementations MUST support random-access decryption: given (offset, len),
// the caller must be able to decrypt an arbitrary byte range without reading
// the preceding ciphertext. ChaCha20 achieves this by seeking the block
// counter to offset/64, which is required for AssetBundle.LoadFromStream
// arbitrary-position reads on encrypted assets.
class ICipher {
public:
    virtual ~ICipher() = default;
    virtual void Encrypt(uint8_t* buf, size_t offset, size_t len,
                         const uint8_t* key, size_t key_len) = 0;
    virtual void Decrypt(uint8_t* buf, size_t offset, size_t len,
                         const uint8_t* key, size_t key_len) = 0;
};

// Move-only wrapper that owns one stream cipher instance.
// The default ctor uses ChaCha20; the concrete type lives in src/.
class Cipher {
public:
    Cipher();
    explicit Cipher(ECipherAlgorithm algo);

    Cipher(const Cipher&)            = delete;
    Cipher& operator=(const Cipher&) = delete;
    Cipher(Cipher&&)                 = default;
    Cipher& operator=(Cipher&&)      = default;

    void Encrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len);
    void Decrypt(uint8_t* buf, size_t offset, size_t len,
                 const uint8_t* key, size_t key_len);

private:
    std::unique_ptr<ICipher> mImpl;
};

} // namespace roulin
