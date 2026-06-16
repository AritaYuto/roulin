#include <gtest/gtest.h>
#include <gmock/gmock.h>
#include "roulin/diff.h"
#include "roulin/index.h"
#include "roulin/blob_store.h"
#include "roulin/blob_reader.h"
#include "roulin/parcel.h"
#include "roulin/hash.h"
#include "roulin.h"   // C FFI
#include <cstring>
#include <filesystem>
#include <string>

// ---- Test fixture ----------------------------------------------------------

class DiffTest : public ::testing::Test {
protected:
    void SetUp() override {
        mTmpDir = std::filesystem::temp_directory_path()
                / ("roulin_diff_" + std::to_string(reinterpret_cast<uintptr_t>(this)));
        std::filesystem::remove_all(mTmpDir);
        std::filesystem::create_directories(mTmpDir);
    }
    void TearDown() override { std::filesystem::remove_all(mTmpDir); }

    std::string TmpDir(const std::string& sub = "") const {
        return sub.empty() ? mTmpDir.string() : mTmpDir.string() + "/" + sub;
    }

    // Build an IndexEntry holding a single address.
    static roulin::IndexEntry MakeEntry(const std::string& addr,
                                          const roulin::Hash32& hash) {
        roulin::IndexEntry e;
        e.blob_hash = hash;
        roulin::Address a;
        a.address_str = addr;
        e.addresses.push_back(std::move(a));
        return e;
    }

    std::filesystem::path mTmpDir;
};

// ---- DiffIndex (C++ API) ---------------------------------------------------

TEST_F(DiffTest, AllMissingWhenLocalEmpty) {
    roulin::IndexBuilder ib;
    for (const char* addr : {"a/b", "c/d", "e/f"}) {
        roulin::Hash32 h{};
        h.fill(static_cast<uint8_t>(addr[0]));
        ib.Add(MakeEntry(addr, h));
    }
    auto index = roulin::Index::FromBytes(ib.Build());

    // Local storage has nothing.
    roulin::BlobReader local(TmpDir("empty"));
    auto missing = roulin::DiffIndex(index, local);
    EXPECT_EQ(missing.size(), 3u);
}

TEST_F(DiffTest, NoneReturnedWhenAllPresent) {
    roulin::BlobStore store(TmpDir("local"));
    std::string data = "asset content";
    auto hash = store.Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());

    roulin::IndexBuilder ib;
    ib.Add(MakeEntry("ui/player", hash));
    auto index = roulin::Index::FromBytes(ib.Build());

    roulin::BlobReader local(TmpDir("local"));
    auto missing = roulin::DiffIndex(index, local);
    EXPECT_TRUE(missing.empty());
}

TEST_F(DiffTest, OnlyMissingBlobsReturned) {
    // Build a remote index with 3 blobs: A, B, C.
    // Write blobs A and C locally; B is absent.
    roulin::BlobStore store(TmpDir("local"));

    std::string da = "data-A", db = "data-B", dc = "data-C";
    auto ha = store.Write(reinterpret_cast<const uint8_t*>(da.data()), da.size());
    // Intentionally skip writing blob B to local store.
    roulin::Hash32 hb{};
    hb.fill(0xBB);  // fabricated hash not written to local
    auto hc = store.Write(reinterpret_cast<const uint8_t*>(dc.data()), dc.size());

    roulin::IndexBuilder ib;
    ib.Add(MakeEntry("addr/A", ha));
    ib.Add(MakeEntry("addr/B", hb));
    ib.Add(MakeEntry("addr/C", hc));

    auto index = roulin::Index::FromBytes(ib.Build());
    roulin::BlobReader local(TmpDir("local"));
    auto missing = roulin::DiffIndex(index, local);

    ASSERT_EQ(missing.size(), 1u);
    EXPECT_EQ(missing[0].address, "addr/B");
    EXPECT_EQ(missing[0].blob_hash, hb);
}

// ---- C FFI -----------------------------------------------------------------

TEST_F(DiffTest, CFfi_OpenClose) {
    // Create a minimal parcel
    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x01);
    ib.Add(MakeEntry("x", h));
    ib.SaveToFile(TmpDir() + "/index/rev1");

    ACParcel* parcel = rln_parcel_open(TmpDir().c_str(), "rev1");
    ASSERT_NE(parcel, nullptr);
    rln_parcel_close(parcel);
}

TEST_F(DiffTest, CFfi_GetAndRead) {
    const std::string kAddr = "ui/icon";
    const std::string kData = "icon raw bytes";

    roulin::BlobStore store(TmpDir());
    auto hash = store.Write(
        reinterpret_cast<const uint8_t*>(kData.data()), kData.size());

    roulin::IndexBuilder ib;
    ib.Add(MakeEntry(kAddr, hash));
    ib.SaveToFile(TmpDir() + "/index/rev1");

    ACParcel* parcel = rln_parcel_open(TmpDir().c_str(), "rev1");
    ASSERT_NE(parcel, nullptr);

    ACBlob* blob = rln_parcel_get(parcel, kAddr.c_str());
    ASSERT_NE(blob, nullptr);

    EXPECT_EQ(rln_blob_size(blob), kData.size());

    std::string buf(kData.size(), '\0');
    int64_t n = rln_blob_read(blob, buf.data(), 0, buf.size());
    EXPECT_EQ(n, static_cast<int64_t>(kData.size()));
    EXPECT_EQ(buf, kData);

    rln_blob_release(blob);
    rln_parcel_close(parcel);
}

TEST_F(DiffTest, CFfi_GetMissingAddressReturnsNull) {
    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x01);
    ib.Add(MakeEntry("exists", h));
    ib.SaveToFile(TmpDir() + "/index/rev1");

    ACParcel* parcel = rln_parcel_open(TmpDir().c_str(), "rev1");
    ASSERT_NE(parcel, nullptr);

    EXPECT_EQ(rln_parcel_get(parcel, "does_not_exist"), nullptr);
    rln_parcel_close(parcel);
}

TEST_F(DiffTest, CFfi_Diff) {
    // Remote has 2 blobs; local has 1 of them.
    roulin::BlobStore local_store(TmpDir("local"));
    std::string d1 = "present";
    auto h1 = local_store.Write(
        reinterpret_cast<const uint8_t*>(d1.data()), d1.size());

    roulin::Hash32 h2{}; h2.fill(0xFF);  // absent locally

    // Build remote parcel
    roulin::IndexBuilder ib;
    ib.Add(MakeEntry("present/blob", h1));
    ib.Add(MakeEntry("missing/blob", h2));
    ib.SaveToFile(TmpDir("remote") + "/index/rev1");

    ACParcel* remote = rln_parcel_open(TmpDir("remote").c_str(), "rev1");
    ASSERT_NE(remote, nullptr);

    ACMissingBlob* blobs  = nullptr;
    size_t         count  = 0;
    int ret = rln_parcel_diff(remote, TmpDir("local").c_str(), &blobs, &count);

    EXPECT_EQ(ret, 1);
    ASSERT_EQ(count, 1u);
    ASSERT_NE(blobs, nullptr);
    EXPECT_STREQ(blobs[0].address, "missing/blob");

    rln_diff_free(blobs, count);
    rln_parcel_close(remote);
}

// ---- rln_last_error tests ----------------------------------------------------

TEST_F(DiffTest, CFfi_LastError_ClearsOnSuccess) {
    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x01);
    ib.Add(MakeEntry("x", h));
    ib.SaveToFile(TmpDir() + "/index/rev1");

    // First call fails → error is set.
    ACParcel* bad = rln_parcel_open(TmpDir().c_str(), "nonexistent");
    EXPECT_EQ(bad, nullptr);
    EXPECT_NE(rln_last_error(), nullptr);

    // Next successful call → error is cleared.
    ACParcel* good = rln_parcel_open(TmpDir().c_str(), "rev1");
    ASSERT_NE(good, nullptr);
    EXPECT_EQ(rln_last_error(), nullptr);
    rln_parcel_close(good);
}

TEST_F(DiffTest, CFfi_LastError_NullArgument) {
    ACParcel* t = rln_parcel_open(nullptr, "rev");
    EXPECT_EQ(t, nullptr);
    ASSERT_NE(rln_last_error(), nullptr);
    EXPECT_THAT(std::string(rln_last_error()), ::testing::HasSubstr("rln_parcel_open"));
}

TEST_F(DiffTest, CFfi_LastError_AddressNotFound) {
    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x01);
    ib.Add(MakeEntry("exists", h));
    ib.SaveToFile(TmpDir() + "/index/rev1");

    ACParcel* parcel = rln_parcel_open(TmpDir().c_str(), "rev1");
    ASSERT_NE(parcel, nullptr);

    ACBlob* blob = rln_parcel_get(parcel, "does_not_exist");
    EXPECT_EQ(blob, nullptr);
    ASSERT_NE(rln_last_error(), nullptr);
    EXPECT_THAT(std::string(rln_last_error()), ::testing::HasSubstr("does_not_exist"));

    rln_parcel_close(parcel);
}

TEST_F(DiffTest, CFfi_LastError_InvalidParcelDir) {
    ACParcel* t = rln_parcel_open("/nonexistent/path", "rev999");
    EXPECT_EQ(t, nullptr);
    ASSERT_NE(rln_last_error(), nullptr);
    // Error message should contain the function name for easy identification.
    EXPECT_THAT(std::string(rln_last_error()), ::testing::HasSubstr("rln_parcel_open"));
}

// ---- rln_compute_blake3 -----------------------------------------------------

TEST(FfiHash, ComputeBlake3_EmptyInput) {
    // BLAKE3("") = af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262
    const uint8_t expected[32] = {
        0xaf,0x13,0x49,0xb9, 0xf5,0xf9,0xa1,0xa6, 0xa0,0x40,0x4d,0xea, 0x36,0xdc,0xc9,0x49,
        0x9b,0xcb,0x25,0xc9, 0xad,0xc1,0x12,0xb7, 0xcc,0x9a,0x93,0xca, 0xe4,0x1f,0x32,0x62,
    };
    uint8_t out[32];
    rln_compute_blake3(nullptr, 0, out);
    EXPECT_EQ(0, std::memcmp(out, expected, 32));
}

TEST(FfiHash, ComputeBlake3_AbcVector) {
    // BLAKE3("abc") = 6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85
    const uint8_t expected[32] = {
        0x64,0x37,0xb3,0xac, 0x38,0x46,0x51,0x33, 0xff,0xb6,0x3b,0x75, 0x27,0x3a,0x8d,0xb5,
        0x48,0xc5,0x58,0x46, 0x5d,0x79,0xdb,0x03, 0xfd,0x35,0x9c,0x6c, 0xd5,0xbd,0x9d,0x85,
    };
    const char* msg = "abc";
    uint8_t out[32];
    rln_compute_blake3(msg, 3, out);
    EXPECT_EQ(0, std::memcmp(out, expected, 32));
}
