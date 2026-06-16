#include <gtest/gtest.h>

#include "roulin/fetch/hashed_writer.h"
#include "roulin/hash.h"
#include "roulin/hasher.h"

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

roulin::Hash32 Blake3Of(const std::vector<uint8_t>& bytes) {
    roulin::Hasher h(roulin::EHashAlgorithm::blake3);
    if (!bytes.empty()) {
        h.Update(bytes.data(), bytes.size());
    }
    return h.Finalize();
}

std::vector<uint8_t> SampleBytes(const std::string& s) {
    return std::vector<uint8_t>(s.begin(), s.end());
}

std::vector<uint8_t> ReadFile(const std::filesystem::path& p) {
    std::ifstream in(p, std::ios::binary);
    return std::vector<uint8_t>(std::istreambuf_iterator<char>(in),
                                std::istreambuf_iterator<char>());
}

class HashedWriterTest : public ::testing::Test {
protected:
    void SetUp() override {
        static std::atomic<uint64_t> counter{0};
        const auto unique = counter.fetch_add(1, std::memory_order_relaxed);
        mTmpDir = std::filesystem::temp_directory_path()
                / ("roulin_hashed_writer_"
                   + std::to_string(reinterpret_cast<uintptr_t>(this))
                   + "_" + std::to_string(unique));
        std::filesystem::remove_all(mTmpDir);
        std::filesystem::create_directories(mTmpDir);
    }
    void TearDown() override {
        std::error_code ec;
        std::filesystem::remove_all(mTmpDir, ec);
    }

    std::string DestPath(const std::string& name = "blob.bin") const {
        return (mTmpDir / name).string();
    }

    std::filesystem::path mTmpDir;
};

TEST_F(HashedWriterTest, Write_AccumulatesToTmpFile) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("hello world");

    roulin::fetch::HashedWriter w(dest, roulin::Hash32{});
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    EXPECT_EQ(w.BytesWritten(), bytes.size());

    const std::filesystem::path tmp = dest + ".tmp";
    EXPECT_TRUE(std::filesystem::exists(tmp));
    EXPECT_FALSE(std::filesystem::exists(dest));
}

TEST_F(HashedWriterTest, Finalize_Ok_OnHashMatch) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("the quick brown fox");
    const auto expected = Blake3Of(bytes);

    roulin::fetch::HashedWriter w(dest, expected);
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    EXPECT_EQ(w.Finalize(), roulin::fetch::HashedWriter::FinalizeResult::Ok);

    EXPECT_TRUE(std::filesystem::exists(dest));
    EXPECT_FALSE(std::filesystem::exists(dest + ".tmp"));
    EXPECT_EQ(ReadFile(dest), bytes);
}

TEST_F(HashedWriterTest, Finalize_HashMismatch_OnWrongExpected) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("payload contents");
    roulin::Hash32 bogus;
    bogus.fill(0xFF);

    roulin::fetch::HashedWriter w(dest, bogus);
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    EXPECT_EQ(w.Finalize(),
              roulin::fetch::HashedWriter::FinalizeResult::HashMismatch);

    EXPECT_FALSE(std::filesystem::exists(dest));
    EXPECT_FALSE(std::filesystem::exists(dest + ".tmp"));
}

TEST_F(HashedWriterTest, Finalize_HashMatch_SkippedWhenExpectedAllZero) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("arbitrary bytes");

    roulin::fetch::HashedWriter w(dest, roulin::Hash32{});
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    EXPECT_EQ(w.Finalize(), roulin::fetch::HashedWriter::FinalizeResult::Ok);

    EXPECT_TRUE(std::filesystem::exists(dest));
    EXPECT_FALSE(std::filesystem::exists(dest + ".tmp"));
    EXPECT_EQ(ReadFile(dest), bytes);
}

TEST_F(HashedWriterTest, Cancel_DeletesTmp) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("about to be cancelled");

    roulin::fetch::HashedWriter w(dest, roulin::Hash32{});
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    w.Cancel();

    EXPECT_FALSE(std::filesystem::exists(dest));
    EXPECT_FALSE(std::filesystem::exists(dest + ".tmp"));

    w.Cancel();
    EXPECT_EQ(w.Finalize(), roulin::fetch::HashedWriter::FinalizeResult::Ok);
    EXPECT_FALSE(std::filesystem::exists(dest));
}

TEST_F(HashedWriterTest, Destructor_CleansUpUnfinalizedTmp) {
    const auto dest = DestPath();
    const auto bytes = SampleBytes("leaks if not cleaned");
    {
        roulin::fetch::HashedWriter w(dest, roulin::Hash32{});
        ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    }
    EXPECT_FALSE(std::filesystem::exists(dest + ".tmp"));
    EXPECT_FALSE(std::filesystem::exists(dest));
}

TEST_F(HashedWriterTest, Reset_StartsFresh) {
    const auto dest = DestPath();
    const auto bytes_a = SampleBytes("first attempt body");
    const auto bytes_b = SampleBytes("second attempt body, different length");
    const auto expected_b = Blake3Of(bytes_b);

    roulin::fetch::HashedWriter w(dest, expected_b);
    ASSERT_TRUE(w.Write(bytes_a.data(), bytes_a.size()));
    EXPECT_EQ(w.BytesWritten(), bytes_a.size());

    ASSERT_TRUE(w.Reset());
    EXPECT_EQ(w.BytesWritten(), 0u);

    ASSERT_TRUE(w.Write(bytes_b.data(), bytes_b.size()));
    EXPECT_EQ(w.Finalize(), roulin::fetch::HashedWriter::FinalizeResult::Ok);

    EXPECT_TRUE(std::filesystem::exists(dest));
    EXPECT_EQ(ReadFile(dest), bytes_b);
}

TEST_F(HashedWriterTest, Write_ToNestedDirectory_CreatesParents) {
    const auto nested_dir = mTmpDir / "nested" / "sub";
    const std::string dest = (nested_dir / "blob.bin").string();
    EXPECT_FALSE(std::filesystem::exists(nested_dir));

    const auto bytes = SampleBytes("nested payload");
    const auto expected = Blake3Of(bytes);

    roulin::fetch::HashedWriter w(dest, expected);
    EXPECT_TRUE(std::filesystem::exists(nested_dir));
    ASSERT_TRUE(w.Write(bytes.data(), bytes.size()));
    EXPECT_EQ(w.Finalize(), roulin::fetch::HashedWriter::FinalizeResult::Ok);

    EXPECT_TRUE(std::filesystem::exists(dest));
    EXPECT_EQ(ReadFile(dest), bytes);
}

}  // namespace
