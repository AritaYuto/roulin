#include "roulin/hash.h"
#include <stdexcept>

namespace roulin {

namespace {
constexpr char kHex[] = "0123456789abcdef";
}

std::string ToHex(const Hash32& hash) {
    std::string out(64, '\0');
    for (size_t i = 0; i < 32; ++i) {
        out[2 * i]     = kHex[(hash[i] >> 4) & 0xf];
        out[2 * i + 1] = kHex[hash[i] & 0xf];
    }
    return out;
}

Hash32 FromHex(const std::string& hex) {
    if (hex.size() != 64)
        throw std::invalid_argument("hash hex string must be exactly 64 characters");

    auto nibble = [](char c) -> uint8_t {
        if (c >= '0' && c <= '9') return static_cast<uint8_t>(c - '0');
        if (c >= 'a' && c <= 'f') return static_cast<uint8_t>(c - 'a' + 10);
        if (c >= 'A' && c <= 'F') return static_cast<uint8_t>(c - 'A' + 10);
        throw std::invalid_argument("invalid hex character");
    };

    Hash32 out{};
    for (size_t i = 0; i < 32; ++i)
        out[i] = static_cast<uint8_t>((nibble(hex[2 * i]) << 4) | nibble(hex[2 * i + 1]));
    return out;
}

} // namespace roulin
