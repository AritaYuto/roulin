// Standalone binary that verifies a parcel written by the Go roulin-cli
// can be opened and read correctly by the C FFI (rln_parcel_open / rln_blob_read).
//
// Usage:
//   pipeline_test <parcel_dir> <revision_id> <address> <expected_content_file>
//
// Exit code: 0 = pass, 1 = fail.

#include "roulin.h"
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

static std::string readFile(const std::string& path) {
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) return {};
    auto size = f.tellg();
    f.seekg(0);
    std::string buf(static_cast<size_t>(size), '\0');
    f.read(buf.data(), size);
    return buf;
}

static bool verify(ACParcel* parcel, const std::string& address,
                   const std::string& expected) {
    ACBlob* blob = rln_parcel_get(parcel, address.c_str());
    if (!blob) {
        std::cerr << "FAIL: rln_parcel_get(\"" << address << "\") returned null\n";
        return false;
    }

    size_t sz = rln_blob_size(blob);
    std::vector<char> buf(sz);
    int64_t n = rln_blob_read(blob, buf.data(), 0, sz);
    rln_blob_release(blob);

    if (n < 0 || static_cast<size_t>(n) != sz) {
        std::cerr << "FAIL: rln_blob_read returned " << n
                  << " (expected " << sz << ")\n";
        return false;
    }

    std::string got(buf.begin(), buf.end());
    if (got != expected) {
        std::cerr << "FAIL: content mismatch for \"" << address << "\"\n";
        std::cerr << "  expected (" << expected.size() << " bytes)\n";
        std::cerr << "  got      (" << got.size() << " bytes)\n";
        return false;
    }

    std::cout << "  PASS  " << address << "  (" << sz << " B)\n";
    return true;
}

int main(int argc, char* argv[]) {
    if (argc < 5) {
        std::cerr << "usage: pipeline_test <parcel_dir> <revision_id>"
                     " <address> <expected_content_file> [...]\n"
                  << "  (address / expected_content_file pairs may repeat)\n";
        return 1;
    }

    const char* parcel_dir = argv[1];
    const char* revision   = argv[2];

    ACParcel* parcel = rln_parcel_open(parcel_dir, revision);
    if (!parcel) {
        std::cerr << "FAIL: rln_parcel_open(\"" << parcel_dir
                  << "\", \"" << revision << "\") returned null\n";
        return 1;
    }

    bool ok = true;
    for (int i = 3; i + 1 < argc; i += 2) {
        std::string address  = argv[i];
        std::string expected = readFile(argv[i + 1]);
        if (expected.empty()) {
            std::cerr << "FAIL: cannot read expected file: " << argv[i + 1] << "\n";
            ok = false;
            continue;
        }
        if (!verify(parcel, address, expected)) ok = false;
    }

    rln_parcel_close(parcel);
    return ok ? 0 : 1;
}
