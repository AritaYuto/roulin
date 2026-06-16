#include "algo/cipher/chacha20_cipher.h"
#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace roulin {
namespace {
    
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__
    #  define ROULIN_BSWAP32(v) \
        ( (((v) & 0xff000000u) >> 24) | (((v) & 0x00ff0000u) >>  8)   \
        | (((v) & 0x0000ff00u) <<  8) | (((v) & 0x000000ffu) << 24) )
#elif defined(__BYTE_ORDER__) || defined(_MSC_VER)
    #  define ROULIN_BSWAP32(v) (v)
#else
    #  error "Unknown byte order; add detection for this compiler"
#endif

static inline uint32_t rotl32(uint32_t v, int n) {
    return (v << n) | (v >> (32 - n));
}

static inline uint32_t load32le(const uint8_t* p) {
    uint32_t v;
    memcpy(&v, p, 4);
    v = ROULIN_BSWAP32(v);
    return v;
}

static inline void store32le(uint8_t* p, uint32_t v) {
    v = ROULIN_BSWAP32(v);
    memcpy(p, &v, 4);
}

static void quarter_round(uint32_t* s, int a, int b, int c, int d) {
    s[a] += s[b]; s[d] ^= s[a]; s[d] = rotl32(s[d], 16);
    s[c] += s[d]; s[b] ^= s[c]; s[b] = rotl32(s[b], 12);
    s[a] += s[b]; s[d] ^= s[a]; s[d] = rotl32(s[d],  8);
    s[c] += s[d]; s[b] ^= s[c]; s[b] = rotl32(s[b],  7);
}

static void chacha20_block(const uint8_t key[32], const uint8_t nonce[12],
                           uint32_t counter, uint8_t out[64]) {
    static constexpr uint32_t kConst[4] = {
        0x61707865u, 0x3320646eu, 0x79622d32u, 0x6b206574u
    };
    uint32_t s[16] = {
        kConst[0],        kConst[1],        kConst[2],        kConst[3],
        load32le(key+0),  load32le(key+4),  load32le(key+8),  load32le(key+12),
        load32le(key+16), load32le(key+20), load32le(key+24), load32le(key+28),
        counter,
        load32le(nonce+0), load32le(nonce+4), load32le(nonce+8),
    };
    uint32_t ws[16];
    memcpy(ws, s, sizeof(s));

    for (int i = 0; i < 10; ++i) {
        quarter_round(ws, 0, 4,  8, 12);
        quarter_round(ws, 1, 5,  9, 13);
        quarter_round(ws, 2, 6, 10, 14);
        quarter_round(ws, 3, 7, 11, 15);
        quarter_round(ws, 0, 5, 10, 15);
        quarter_round(ws, 1, 6, 11, 12);
        quarter_round(ws, 2, 7,  8, 13);
        quarter_round(ws, 3, 4,  9, 14);
    }
    for (int i = 0; i < 16; ++i) store32le(out + 4 * i, ws[i] + s[i]);
}

static void chacha20_xor(const uint8_t key[32], const uint8_t nonce[12],
                         size_t byte_offset, uint8_t* buf, size_t len) {
    auto block_idx = static_cast<uint32_t>(byte_offset / 64);
    size_t block_off = byte_offset % 64;

    uint8_t keystream[64];
    size_t pos = 0;

    while (pos < len) {
        chacha20_block(key, nonce, block_idx, keystream);
        size_t ks_start = (pos == 0) ? block_off : 0;
        size_t ks_end   = std::min<size_t>(64, ks_start + (len - pos));
        for (size_t i = ks_start; i < ks_end; ++i)
            buf[pos++] ^= keystream[i];
        ++block_idx;
    }
}

} // namespace

void ChaCha20Cipher::Encrypt(uint8_t* buf, size_t offset, size_t len,
                              const uint8_t* key, size_t key_len) {
    if (key_len != 32 && key_len != 44)
        throw std::invalid_argument(
            "ChaCha20: key_len must be 32 (zero-nonce) or 44 (key||nonce)");
    uint8_t nonce[12] = {};
    if (key_len == 44) memcpy(nonce, key + 32, 12);
    chacha20_xor(key, nonce, offset, buf, len);
}

void ChaCha20Cipher::Decrypt(uint8_t* buf, size_t offset, size_t len,
                              const uint8_t* key, size_t key_len) {
    Encrypt(buf, offset, len, key, key_len);
}

} // namespace roulin
