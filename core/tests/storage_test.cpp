#include <gtest/gtest.h>
#include "roulin/blob_reader.h"
#include "roulin/blob_store.h"
#include "roulin/index.h"
#include "roulin/parcel.h"
#include "roulin/hash.h"
#include <filesystem>
#include <string>

// ---- Test fixture ----------------------------------------------------------

class StorageTest : public ::testing::Test {
protected:
    void SetUp() override {
        mTmpDir = std::filesystem::temp_directory_path()
                / ("roulin_storage_" + std::to_string(reinterpret_cast<uintptr_t>(this)));
        std::filesystem::remove_all(mTmpDir);
        std::filesystem::create_directories(mTmpDir);
    }
    void TearDown() override { std::filesystem::remove_all(mTmpDir); }

    std::string TmpDir() const { return mTmpDir.string(); }
    std::string TmpDir(const std::string& sub) const { return mTmpDir.string() + "/" + sub; }

    std::filesystem::path mTmpDir;
};

// ---- BlobReader tests ------------------------------------------------------

TEST_F(StorageTest, ExistsAndReadAll) {
    roulin::BlobStore store(TmpDir());
    const std::string data = "hello, BlobReader";
    auto hash = store.Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());

    roulin::BlobReader r(TmpDir());
    EXPECT_TRUE(r.Exists(hash));
    EXPECT_EQ(r.BlobSize(hash), data.size());

    auto got = r.ReadAll(hash);
    EXPECT_EQ(std::string(got.begin(), got.end()), data);
}

TEST_F(StorageTest, PartialRead) {
    roulin::BlobStore store(TmpDir());
    const std::string data = "0123456789";
    auto hash = store.Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());

    roulin::BlobReader r(TmpDir());
    uint8_t buf[4] = {};
    // Read bytes [3, 7)
    size_t n = r.Read(hash, 3, buf, 4);
    EXPECT_EQ(n, 4u);
    EXPECT_EQ(std::string(reinterpret_cast<char*>(buf), 4), "3456");
}

TEST_F(StorageTest, ExistsReturnsFalseForMissingBlob) {
    roulin::BlobReader r(TmpDir());
    roulin::Hash32 fake{};
    EXPECT_FALSE(r.Exists(fake));
}

TEST_F(StorageTest, ReadThrowsForMissingBlob) {
    roulin::BlobReader r(TmpDir());
    roulin::Hash32 fake{};
    uint8_t buf[1];
    EXPECT_THROW(r.Read(fake, 0, buf, 1), std::runtime_error);
}

// ---- End-to-end test -------------------------------------------------------
//
// Full pipeline: BlobStore::Write → IndexBuilder → Parcel::Open → BlobReader::Read
// This is the canonical read path for all asset loading.

TEST_F(StorageTest, EndToEnd) {
    const std::string kRevision = "abc123";
    const std::string kAddress  = "ui/icons/player";
    const std::string kData     = "texture binary data (placeholder)";

    // 1. Write the blob via BlobStore.
    roulin::BlobStore store(TmpDir());
    auto hash = store.Write(
        reinterpret_cast<const uint8_t*>(kData.data()), kData.size());

    // 2. Build the parcel index and save to disk.
    roulin::IndexBuilder ib;
    roulin::IndexEntry ie;
    ie.blob_hash = hash;
    {
        roulin::Address a;
        a.address_str = kAddress;
        ie.addresses.push_back(std::move(a));
    }
    ib.Add(std::move(ie));
    ib.SaveToFile(TmpDir() + "/index/" + kRevision);

    // 3. Open the Parcel.
    auto parcel = roulin::Parcel::Open(TmpDir(), kRevision);
    ASSERT_EQ(parcel.RevisionId(), kRevision);

    // 4. Resolve the address to a blob hash.
    auto entry = parcel.Get(kAddress);
    ASSERT_TRUE(entry.has_value());
    EXPECT_EQ(entry->blob_hash, hash);
    ASSERT_FALSE(entry->addresses.empty());
    EXPECT_EQ(entry->addresses.front().address_str, kAddress);

    // 5. Read the blob content through BlobReader.
    roulin::BlobReader r(TmpDir());
    auto got = r.ReadAll(entry->blob_hash);
    EXPECT_EQ(std::string(got.begin(), got.end()), kData);
}
