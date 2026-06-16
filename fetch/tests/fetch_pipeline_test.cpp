// usage: fetch-pipeline-test <base_url> <hex_hash1> [<hex_hash2> ...]

#include "fetch.h"
#include "roulin.h"

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace {

bool hexToBytes(const std::string& hex, uint8_t out[32]) {
    if (hex.size() != 64) return false;
    for (size_t i = 0; i < 32; ++i) {
        unsigned v = 0;
        for (int n = 0; n < 2; ++n) {
            char c = hex[i * 2 + n];
            unsigned d;
            if (c >= '0' && c <= '9')      d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else return false;
            v = (v << 4) | d;
        }
        out[i] = static_cast<uint8_t>(v);
    }
    return true;
}

std::string bytesToHex(const uint8_t bytes[32]) {
    static const char* hex = "0123456789abcdef";
    std::string s(64, '0');
    for (size_t i = 0; i < 32; ++i) {
        s[i * 2]     = hex[(bytes[i] >> 4) & 0xF];
        s[i * 2 + 1] = hex[bytes[i] & 0xF];
    }
    return s;
}

struct Item {
    std::string hash_hex;
    uint8_t     expected[32];
    std::string url;
    uint64_t    handle       = 0;
    bool        done         = false;
    bool        ok           = false;
    std::string error;
    size_t      bytes        = 0;
    int         http_version = 0;
};

const char* httpVersionName(int v) {
    switch (v) {
        case 1:  return "HTTP/1.0";
        case 2:  return "HTTP/1.1";
        case 3:  return "HTTP/2";
        case 30: return "HTTP/3";
        default: return "unknown";
    }
}

}  // namespace

int main(int argc, char* argv[]) {
    if (argc < 3) {
        std::fprintf(stderr,
                     "usage: fetch-pipeline-test <base_url> <hex_hash1> [...]\n");
        return 1;
    }

    std::string base_url = argv[1];
    while (!base_url.empty() && base_url.back() == '/') base_url.pop_back();

    std::vector<Item> items;
    items.reserve(static_cast<size_t>(argc - 2));
    for (int i = 2; i < argc; ++i) {
        Item it;
        it.hash_hex = argv[i];
        if (!hexToBytes(it.hash_hex, it.expected)) {
            std::fprintf(stderr, "FAIL: invalid hex hash: %s\n", argv[i]);
            return 1;
        }
        it.url = base_url + "/blobs/" + it.hash_hex.substr(0, 2) + "/" + it.hash_hex;
        items.push_back(std::move(it));
    }

    // Accept HTTP/1.1 or HTTP/2 so this test passes across backends; per-
    // backend protocol expectations are covered by their unit tests.
    rln_fetch_config cfg{};
    cfg.max_parallel = 8;
    cfg.http_mode    = RLN_HTTP_AUTO;
    cfg.max_attempts = 3;

    rln_fetch_session* session = rln_fetch_session_new(&cfg);
    if (!session) {
        const char* msg = rln_last_error();
        std::fprintf(stderr, "FAIL: rln_fetch_session_new: %s\n",
                     msg ? msg : "(no error)");
        return 1;
    }

    for (auto& it : items) {
        it.handle = rln_fetch_enqueue(session, it.url.c_str(), it.expected);
        if (it.handle == 0) {
            const char* msg = rln_last_error();
            std::fprintf(stderr, "FAIL: rln_fetch_enqueue %s: %s\n",
                         it.url.c_str(), msg ? msg : "(no error)");
            rln_fetch_session_free(session);
            return 1;
        }
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    size_t pending = items.size();

    while (pending > 0) {
        if (std::chrono::steady_clock::now() > deadline) {
            std::fprintf(stderr,
                         "FAIL: timeout after 10s with %zu handle(s) still in flight\n",
                         pending);
            for (auto& it : items) {
                if (!it.done) rln_fetch_cancel(session, it.handle);
            }
            rln_fetch_session_free(session);
            return 1;
        }

        for (auto& it : items) {
            if (it.done) continue;

            uint8_t* buf = nullptr;
            size_t   len = 0;
            int      ver = 0;
            int rc = rln_fetch_poll(session, it.handle,
                                   nullptr, nullptr, &buf, &len, &ver);
            if (rc == 0) continue;

            it.done         = true;
            it.http_version = ver;
            --pending;

            if (rc == 1) {
                uint8_t actual[32];
                rln_compute_blake3(buf, len, actual);
                if (len == 0) {
                    it.error = "empty body";
                } else if (std::memcmp(actual, it.expected, 32) != 0) {
                    it.error = "BLAKE3 mismatch after fetch: got " + bytesToHex(actual);
                } else if (ver != 2 && ver != 3) {
                    it.error = std::string("expected HTTP/1.1 or HTTP/2 but server negotiated ")
                             + httpVersionName(ver);
                } else {
                    it.ok    = true;
                    it.bytes = len;
                }
                rln_fetch_free_buf(buf);
            } else {
                const char* msg = rln_last_error();
                it.error = msg ? msg : "(no error)";
            }
        }

        if (pending > 0) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    rln_fetch_session_free(session);

    size_t failures = 0;
    for (const auto& it : items) {
        std::string short_hash = it.hash_hex.substr(0, 12);
        if (it.ok) {
            std::printf("  OK    %s...  %zu bytes  %s\n",
                        short_hash.c_str(), it.bytes,
                        httpVersionName(it.http_version));
        } else {
            std::printf("  FAIL  %s...  %s\n", short_hash.c_str(), it.error.c_str());
            ++failures;
        }
    }

    if (failures > 0) {
        std::fprintf(stderr, "FAIL: %zu/%zu fetch(es) failed\n",
                     failures, items.size());
        return 1;
    }
    return 0;
}
