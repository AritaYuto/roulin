#include <gtest/gtest.h>
#include "roulin/blob_store.h"
#include "roulin/cipher.h"
#include "roulin/compressor.h"
#include "roulin/hasher.h"
#include "roulin/hash.h"
#include <filesystem>
#include <fstream>
#include <string>

// ---- BlobStore tests -------------------------------------------------------

class BlobStoreTest : public ::testing::Test {
protected:
    void SetUp() override {
        tmp_dir_ = std::filesystem::temp_directory_path()
                 / ("roulin_blob_" + std::to_string(reinterpret_cast<uintptr_t>(this)));
        std::filesystem::remove_all(tmp_dir_);
        store_ = std::make_unique<roulin::BlobStore>(tmp_dir_.string());
    }
    void TearDown() override { std::filesystem::remove_all(tmp_dir_); }

    std::filesystem::path tmp_dir_;
    std::unique_ptr<roulin::BlobStore> store_;
};

TEST_F(BlobStoreTest, WriteAndRead) {
    std::string data = "hello, roulin!";
    auto hash = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    ASSERT_TRUE(store_->Exists(hash));
    auto back = store_->Read(hash);
    ASSERT_EQ(back.size(), data.size());
    EXPECT_EQ(memcmp(back.data(), data.data(), data.size()), 0);
}

TEST_F(BlobStoreTest, SameContentSameHash) {
    std::string data = "same content";
    auto h1 = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    auto h2 = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    EXPECT_EQ(h1, h2);
}

TEST_F(BlobStoreTest, DifferentContentDifferentHash) {
    std::string a = "apple", b = "orange";
    auto ha = store_->Write(reinterpret_cast<const uint8_t*>(a.data()), a.size());
    auto hb = store_->Write(reinterpret_cast<const uint8_t*>(b.data()), b.size());
    EXPECT_NE(ha, hb);
}

TEST_F(BlobStoreTest, VerifyIntact) {
    std::string data = "verify me";
    auto hash = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    EXPECT_TRUE(store_->Verify(hash));
}

TEST_F(BlobStoreTest, VerifyCorrupted) {
    std::string data = "to be corrupted";
    auto hash = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    std::ofstream f(store_->BlobPath(hash), std::ios::binary | std::ios::trunc);
    f << "CORRUPTED";
    EXPECT_FALSE(store_->Verify(hash));
}

TEST_F(BlobStoreTest, ReadMissingThrows) {
    roulin::Hash32 fake{};
    EXPECT_FALSE(store_->Exists(fake));
    EXPECT_THROW(store_->Read(fake), std::runtime_error);
}

TEST_F(BlobStoreTest, BlobPathLayout) {
    std::string data = "path layout check";
    auto hash = store_->Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    std::string hex  = roulin::ToHex(hash);
    std::string path = store_->BlobPath(hash);
    EXPECT_NE(path.find("blobs/" + hex.substr(0, 2) + "/" + hex), std::string::npos);
}

TEST_F(BlobStoreTest, ExplicitHasherFactory) {
    // Verify that a BlobStore with an explicitly specified algorithm factory works correctly.
    auto custom = roulin::BlobStore(
        (tmp_dir_ / "custom").string(),
        [] { return roulin::Hasher{roulin::EHashAlgorithm::blake3}; }
    );
    std::string data = "explicit hasher factory";
    auto hash = custom.Write(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    EXPECT_TRUE(custom.Verify(hash));
    auto back = custom.Read(hash);
    EXPECT_EQ(std::string(back.begin(), back.end()), data);
}

// ---- Cipher tests ----------------------------------------------------------

TEST(CipherTest, EncryptDecryptRoundtrip) {
    roulin::Cipher cipher;
    std::string plaintext = "The quick brown fox jumps over the lazy dog";
    std::vector<uint8_t> buf(plaintext.begin(), plaintext.end());

    uint8_t key[32] = {};
    for (int i = 0; i < 32; ++i) key[i] = static_cast<uint8_t>(i);

    cipher.Encrypt(buf.data(), 0, buf.size(), key, 32);
    EXPECT_NE(std::string(buf.begin(), buf.end()), plaintext);

    cipher.Decrypt(buf.data(), 0, buf.size(), key, 32);
    EXPECT_EQ(std::string(buf.begin(), buf.end()), plaintext);
}

TEST(CipherTest, RandomAccessConsistency) {
    roulin::Cipher cipher;
    uint8_t key[32] = {};

    std::vector<uint8_t> full(128, 0xAB);
    cipher.Encrypt(full.data(), 0, 128, key, 32);

    std::vector<uint8_t> half1(64, 0xAB);
    std::vector<uint8_t> half2(64, 0xAB);
    roulin::Cipher c2, c3;
    c2.Encrypt(half1.data(), 0,  64, key, 32);
    c3.Encrypt(half2.data(), 64, 64, key, 32);

    std::vector<uint8_t> combined(half1.begin(), half1.end());
    combined.insert(combined.end(), half2.begin(), half2.end());
    EXPECT_EQ(full, combined);
}

TEST(CipherTest, RFC8439TestVector) {
    roulin::Cipher cipher;
    uint8_t key_and_nonce[44] = {};

    std::vector<uint8_t> buf(64, 0x00);
    cipher.Encrypt(buf.data(), 0, 64, key_and_nonce, 44);

    static constexpr uint8_t kExpected[16] = {
        0x76, 0xb8, 0xe0, 0xad, 0xa0, 0xf1, 0x3d, 0x90,
        0x40, 0x5d, 0x6a, 0xe5, 0x53, 0x86, 0xbd, 0x28,
    };
    EXPECT_EQ(memcmp(buf.data(), kExpected, 16), 0);
}

// ---- Compressor tests ------------------------------------------------------

TEST(CompressorTest, NullCompressorRoundtrip) {
    roulin::Compressor c;
    std::string data = "compressor roundtrip test";
    auto compressed = c.Compress(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    auto restored   = c.Decompress(compressed.data(), compressed.size(), data.size());
    EXPECT_EQ(std::string(restored.begin(), restored.end()), data);
}

// ---- Hash utility tests ----------------------------------------------------

TEST(HashTest, ToHexFromHex) {
    roulin::Hash32 h{};
    for (uint8_t i = 0; i < 32; ++i) h[i] = i;
    std::string hex = roulin::ToHex(h);
    EXPECT_EQ(hex.size(), 64u);
    EXPECT_EQ(roulin::FromHex(hex), h);
}

TEST(HashTest, FromHexBadLength) {
    EXPECT_THROW(roulin::FromHex("deadbeef"), std::invalid_argument);
}

// ---- Custom algorithm extension point tests --------------------------------

TEST(CustomHashTest, ConstructsAndProducesHash) {
    // EHashAlgorithm::custom must not throw and must produce a non-empty hash.
    roulin::Hasher h(roulin::EHashAlgorithm::custom);
    std::string data = "custom hash test";
    h.Update(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    roulin::Hash32 result = h.Finalize();
    // The stub XOR hash is deterministic: same input must give same output.
    roulin::Hasher h2(roulin::EHashAlgorithm::custom);
    h2.Update(reinterpret_cast<const uint8_t*>(data.data()), data.size());
    EXPECT_EQ(result, h2.Finalize());
}

TEST(CustomCipherTest, PassthroughLeavesDataUnchanged) {
    // ECipherAlgorithm::custom (null cipher) must not modify the buffer.
    roulin::Cipher c(roulin::ECipherAlgorithm::custom);
    const std::string plaintext = "passthrough cipher test";
    std::vector<uint8_t> buf(plaintext.begin(), plaintext.end());
    uint8_t key[32] = {};
    c.Encrypt(buf.data(), 0, buf.size(), key, 32);
    EXPECT_EQ(std::string(buf.begin(), buf.end()), plaintext);
    c.Decrypt(buf.data(), 0, buf.size(), key, 32);
    EXPECT_EQ(std::string(buf.begin(), buf.end()), plaintext);
}
