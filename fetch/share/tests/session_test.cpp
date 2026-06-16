#include <gtest/gtest.h>

#include "roulin/fetch/session.h"
#include "roulin/hash.h"
#include "roulin/hasher.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
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

bool ContainsCaseInsensitive(const std::string& haystack,
                             const std::string& needle) {
    if (needle.empty()) return true;
    auto it = std::search(haystack.begin(), haystack.end(),
                          needle.begin(), needle.end(),
                          [](char a, char b) {
                              return std::tolower(static_cast<unsigned char>(a))
                                  == std::tolower(static_cast<unsigned char>(b));
                          });
    return it != haystack.end();
}

class SessionTest : public ::testing::Test {
protected:
    void SetUp() override {
        static std::atomic<uint64_t> counter{0};
        const auto unique = counter.fetch_add(1, std::memory_order_relaxed);
        mTmpDir = std::filesystem::temp_directory_path()
                / ("roulin_session_"
                   + std::to_string(reinterpret_cast<uintptr_t>(this))
                   + "_" + std::to_string(unique));
        std::filesystem::remove_all(mTmpDir);
        std::filesystem::create_directories(mTmpDir);
    }
    void TearDown() override {
        std::error_code ec;
        std::filesystem::remove_all(mTmpDir, ec);
    }

    std::string DestPath(const std::string& name) const {
        return (mTmpDir / name).string();
    }

    roulin::fetch::Request MakeRequest(const std::string& name,
                                        const roulin::Hash32& expected_hash) {
        roulin::fetch::Request req;
        req.url           = "https://example.test/" + name;
        req.expected_hash = expected_hash;
        req.dest_path     = DestPath(name);
        req.max_attempts  = 1;
        return req;
    }

    std::filesystem::path mTmpDir;
};

TEST_F(SessionTest, Register_ReturnsNonZeroHandle) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("a.bin", roulin::Hash32{}));
    EXPECT_NE(h, 0u);

    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::InProgress);
    EXPECT_EQ(snap.bytes_done, 0u);
    EXPECT_EQ(snap.bytes_total, 0u);
}

TEST_F(SessionTest, WriteChunk_UpdatesBytesDone) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("b.bin", roulin::Hash32{}));

    const auto chunk1 = SampleBytes("hello ");
    const auto chunk2 = SampleBytes("world");

    s.WriteChunk(h, chunk1.data(), chunk1.size());
    auto snap1 = s.Poll(h);
    EXPECT_EQ(snap1.state, roulin::fetch::State::InProgress);
    EXPECT_EQ(snap1.bytes_done, chunk1.size());

    s.WriteChunk(h, chunk2.data(), chunk2.size());
    auto snap2 = s.Poll(h);
    EXPECT_EQ(snap2.state, roulin::fetch::State::InProgress);
    EXPECT_EQ(snap2.bytes_done, chunk1.size() + chunk2.size());
    EXPECT_GE(snap2.bytes_done, snap1.bytes_done);
}

TEST_F(SessionTest, SetBytesTotal_ReflectsInPoll) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("c.bin", roulin::Hash32{}));

    s.SetBytesTotal(h, 1000);
    auto snap = s.Poll(h);
    EXPECT_EQ(snap.bytes_total, 1000u);
    EXPECT_EQ(snap.state, roulin::fetch::State::InProgress);
}

TEST_F(SessionTest, MarkComplete_TransitionsToCompleted_WithHashMatch) {
    roulin::fetch::Session s;
    const auto bytes = SampleBytes("verify me");
    const auto expected = Blake3Of(bytes);

    auto req = MakeRequest("d.bin", expected);
    auto h = s.Register(req);
    s.WriteChunk(h, bytes.data(), bytes.size());

    const auto outcome = s.FinalizeAttempt(h);
    ASSERT_EQ(outcome, roulin::fetch::Session::FinalizeOutcome::Verified);

    s.MarkComplete(h, 3);
    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::Completed);
    EXPECT_EQ(snap.http_version, 3);
    EXPECT_TRUE(snap.error_message.empty());

    EXPECT_TRUE(std::filesystem::exists(req.dest_path));
    EXPECT_EQ(ReadFile(req.dest_path), bytes);
}

TEST_F(SessionTest, MarkComplete_TransitionsToFailed_OnHashMismatch) {
    roulin::fetch::Session s;
    roulin::Hash32 bogus;
    bogus.fill(0xFF);

    auto req = MakeRequest("e.bin", bogus);
    auto h = s.Register(req);

    const auto bytes = SampleBytes("real body");
    s.WriteChunk(h, bytes.data(), bytes.size());

    const auto outcome = s.FinalizeAttempt(h);
    ASSERT_EQ(outcome, roulin::fetch::Session::FinalizeOutcome::HashMismatch);

    s.MarkFailed(h, roulin::fetch::ErrorCategory::HashMismatch,
                 "hash mismatch on body");

    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::Failed);
    EXPECT_TRUE(ContainsCaseInsensitive(snap.error_message, "hash"));

    EXPECT_FALSE(std::filesystem::exists(req.dest_path));
}

TEST_F(SessionTest, MarkComplete_SkipsHashVerify_WhenExpectedHashZero) {
    roulin::fetch::Session s;
    auto req = MakeRequest("f.bin", roulin::Hash32{});
    auto h = s.Register(req);

    const auto bytes = SampleBytes("anything goes");
    s.WriteChunk(h, bytes.data(), bytes.size());

    const auto outcome = s.FinalizeAttempt(h);
    ASSERT_EQ(outcome, roulin::fetch::Session::FinalizeOutcome::Verified);

    s.MarkComplete(h, 2);
    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::Completed);
    EXPECT_TRUE(std::filesystem::exists(req.dest_path));
}

TEST_F(SessionTest, MarkFailed_TransitionsToFailed) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("g.bin", roulin::Hash32{}));

    s.MarkFailed(h, roulin::fetch::ErrorCategory::Network,
                 "connection refused");
    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::Failed);
    EXPECT_TRUE(ContainsCaseInsensitive(snap.error_message,
                                        "connection refused"));
}

TEST_F(SessionTest, Poll_ConsumesTerminalEntry) {
    roulin::fetch::Session s;
    const auto bytes = SampleBytes("done");
    const auto expected = Blake3Of(bytes);

    auto req = MakeRequest("h.bin", expected);
    auto h = s.Register(req);
    s.WriteChunk(h, bytes.data(), bytes.size());
    ASSERT_EQ(s.FinalizeAttempt(h),
              roulin::fetch::Session::FinalizeOutcome::Verified);
    s.MarkComplete(h, 3);

    auto snap1 = s.Poll(h);
    EXPECT_EQ(snap1.state, roulin::fetch::State::Completed);

    auto snap2 = s.Poll(h);
    EXPECT_EQ(snap2.state, roulin::fetch::State::Failed);
    EXPECT_TRUE(ContainsCaseInsensitive(snap2.error_message, "invalid_handle"));
}

TEST_F(SessionTest, Poll_OnUnknownHandle_ReturnsInvalidHandle) {
    roulin::fetch::Session s;
    auto snap = s.Poll(99999);
    EXPECT_EQ(snap.state, roulin::fetch::State::Failed);
    EXPECT_TRUE(ContainsCaseInsensitive(snap.error_message, "invalid_handle"));
}

TEST_F(SessionTest, RequestCancel_SetsFlag) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("i.bin", roulin::Hash32{}));

    EXPECT_FALSE(s.IsCancelRequested(h));
    s.RequestCancel(h);
    EXPECT_TRUE(s.IsCancelRequested(h));

    auto snap_before = s.Poll(h);
    EXPECT_EQ(snap_before.state, roulin::fetch::State::InProgress);

    s.MarkFailed(h, roulin::fetch::ErrorCategory::Cancelled, "cancelled");
    auto snap_after = s.Poll(h);
    EXPECT_EQ(snap_after.state, roulin::fetch::State::Failed);
    EXPECT_TRUE(ContainsCaseInsensitive(snap_after.error_message, "cancel"));
}

TEST_F(SessionTest, RequestCancel_NonExistentHandle_IsNoOp) {
    roulin::fetch::Session s;
    s.RequestCancel(99999);
    EXPECT_FALSE(s.IsCancelRequested(99999));
}

TEST_F(SessionTest, Concurrent_WriteChunkAndPoll_DoNotRace) {
    roulin::fetch::Session s;
    auto h = s.Register(MakeRequest("j.bin", roulin::Hash32{}));

    std::atomic<bool> stop{false};
    std::atomic<uint64_t> last_seen{0};

    const std::vector<uint8_t> chunk(64, 0xAB);

    std::thread writer([&]() {
        while (!stop.load(std::memory_order_acquire)) {
            s.WriteChunk(h, chunk.data(), chunk.size());
        }
    });
    std::thread poller([&]() {
        while (!stop.load(std::memory_order_acquire)) {
            auto snap = s.Poll(h);
            const auto prev = last_seen.load(std::memory_order_relaxed);
            if (snap.bytes_done >= prev) {
                last_seen.store(snap.bytes_done, std::memory_order_relaxed);
            } else {
                FAIL() << "bytes_done went backwards: "
                       << prev << " -> " << snap.bytes_done;
            }
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    stop.store(true, std::memory_order_release);
    writer.join();
    poller.join();

    auto final_snap = s.Poll(h);
    EXPECT_EQ(final_snap.state, roulin::fetch::State::InProgress);
    EXPECT_GT(final_snap.bytes_done, 0u);
}

TEST_F(SessionTest, ResetForRetry_ClearsBytesAndHash) {
    roulin::fetch::Session s;
    const auto bytes_attempt_b = SampleBytes("the second attempt body");
    const auto expected = Blake3Of(bytes_attempt_b);

    auto req = MakeRequest("k.bin", expected);
    auto h = s.Register(req);

    const auto bytes_attempt_a = SampleBytes("partial / wrong body");
    s.WriteChunk(h, bytes_attempt_a.data(), bytes_attempt_a.size());
    auto snap_pre = s.Poll(h);
    EXPECT_EQ(snap_pre.bytes_done, bytes_attempt_a.size());

    s.ResetForRetry(h);
    auto snap_post = s.Poll(h);
    EXPECT_EQ(snap_post.state, roulin::fetch::State::InProgress);
    EXPECT_EQ(snap_post.bytes_done, 0u);

    s.WriteChunk(h, bytes_attempt_b.data(), bytes_attempt_b.size());
    ASSERT_EQ(s.FinalizeAttempt(h),
              roulin::fetch::Session::FinalizeOutcome::Verified);
    s.MarkComplete(h, 3);

    auto snap_done = s.Poll(h);
    EXPECT_EQ(snap_done.state, roulin::fetch::State::Completed);
    EXPECT_TRUE(std::filesystem::exists(req.dest_path));
    EXPECT_EQ(ReadFile(req.dest_path), bytes_attempt_b);
}

TEST_F(SessionTest, FinalizeAttempt_ReturnsHashMismatchResult_WithoutTerminalTransition) {
    roulin::fetch::Session s;
    roulin::Hash32 bogus;
    bogus.fill(0xFF);

    auto req = MakeRequest("l.bin", bogus);
    auto h = s.Register(req);

    const auto bytes = SampleBytes("body that does not match expected hash");
    s.WriteChunk(h, bytes.data(), bytes.size());

    const auto outcome = s.FinalizeAttempt(h);
    EXPECT_EQ(outcome, roulin::fetch::Session::FinalizeOutcome::HashMismatch);

    auto snap = s.Poll(h);
    EXPECT_EQ(snap.state, roulin::fetch::State::InProgress);
    EXPECT_FALSE(std::filesystem::exists(req.dest_path));
}

}  // namespace
