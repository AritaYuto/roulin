#pragma once
#include "roulin/hash.h"
#include <cstdint>
#include <memory>
#include <string>

namespace roulin::fetch {

// 0 is reserved for "invalid".
using Handle = uint64_t;

// Integer mapping matches rln_fetch_poll return codes (C ABI).
enum class State : int {
    InProgress = 0,
    Completed  = 1,
    Failed     = -1,
};

enum class ErrorCategory {
    Network,
    HashMismatch,
    Cancelled,
    Timeout,
    Io,
    Unknown,
};

struct Request {
    std::string     url;
    // All-zero = skip verification.
    roulin::Hash32 expected_hash{};
    // Written to "<dest_path>.tmp" first; atomic rename on verified success.
    std::string     dest_path;
    int             max_attempts = 1;
};

struct PollSnapshot {
    State    state       = State::InProgress;
    uint64_t bytes_done  = 0;
    uint64_t bytes_total = 0;       // 0 = unknown
    int      attempts    = 0;
    // 1 = HTTP/1.0, 2 = HTTP/1.1, 3 = HTTP/2, 30 = HTTP/3. 0 if unknown.
    int      http_version = 0;
    std::string error_message;
};

// Thread safety: one "owner" thread (Register / Poll / RequestCancel) plus
// one "backend" thread (WriteChunk / SetBytesTotal / MarkComplete /
// MarkFailed / IsCancelRequested) may run concurrently. Per-handle atomics
// cover the fields that cross the boundary; the handle map is mutex-guarded.
class Session {
public:
    Session();
    ~Session();

    Handle Register(Request req);

    // Once Poll returns Completed or Failed, the entry is consumed: a
    // subsequent Poll yields Failed with error_message = "invalid_handle".
    PollSnapshot Poll(Handle h);

    void RequestCancel(Handle h);
    bool IsCancelRequested(Handle h) const;

    void WriteChunk(Handle h, const uint8_t* data, size_t len);
    void SetBytesTotal(Handle h, uint64_t total);

    enum class FinalizeOutcome {
        Verified,
        HashMismatch,
        IoError,
    };

    // Increments attempts and renames temp→dest on Verified. Does NOT publish
    // a terminal state; the backend follows up with MarkComplete or MarkFailed.
    FinalizeOutcome FinalizeAttempt(Handle h);

    void MarkComplete(Handle h, int http_version);
    void ResetForRetry(Handle h);
    void MarkFailed(Handle h, ErrorCategory category, std::string message);

    // Transitions every still-InProgress handle to Failed. Called from a
    // backend's destructor so Poll loops don't hang after the backend dies.
    void MarkAllPendingFailed(ErrorCategory category, std::string reason);

    Session(const Session&)            = delete;
    Session& operator=(const Session&) = delete;

private:
    struct Impl;
    std::unique_ptr<Impl> mImpl;
};

}  // namespace roulin::fetch
