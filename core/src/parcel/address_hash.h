#pragma once
#include <cstdint>
#include <string_view>

namespace roulin {

// FNV-1a 64-bit hash of an address string (e.g. "ui/icons/player").
// Used as the binary-search key in Index.
// A parcel of 1M unique addresses has a collision probability of ~3e-8,
// which is negligible. Collisions are caught by verifying address_str on hit.
inline uint64_t HashAddress(std::string_view s) {
    uint64_t h = 14695981039346656037ULL;  // FNV-1a 64-bit offset basis
    for (unsigned char c : s) {
        h ^= c;
        h *= 1099511628211ULL;             // FNV-1a 64-bit prime
    }
    return h;
}

} // namespace roulin
