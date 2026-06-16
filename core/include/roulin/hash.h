#pragma once
#include <array>
#include <cstdint>
#include <string>

namespace roulin {

using Hash32 = std::array<uint8_t, 32>;

std::string ToHex(const Hash32& hash);
Hash32      FromHex(const std::string& hex);

} // namespace roulin
