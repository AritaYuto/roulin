#include "roulin/fetch/session.h"
#include "roulin/fetch/hashed_writer.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <utility>

namespace roulin::fetch {

namespace {

const char* categoryLabel(ErrorCategory cat) {
    switch (cat) {
        case ErrorCategory::Network:      return "network";
        case ErrorCategory::HashMismatch: return "hash mismatch";
        case ErrorCategory::Cancelled:    return "cancelled";
        case ErrorCategory::Timeout:      return "timeout";
        case ErrorCategory::Io:           return "io";
        case ErrorCategory::Unknown:
        default:                          return "unknown";
    }
}

}  // namespace

struct HandleEntry {
    Request                request;
    std::unique_ptr<HashedWriter> writer;

    std::atomic<State>     state{State::InProgress};
    std::atomic<uint64_t>  bytes_done{0};
    std::atomic<uint64_t>  bytes_total{0};
    std::atomic<bool>      cancel_requested{false};
    std::atomic<int>       attempts{0};
    std::atomic<int>       http_version{0};

    // Read only after state is observed terminal.
    std::string            error_message;
};

struct Session::Impl {
    mutable std::mutex                                          map_mu;
    std::unordered_map<Handle, std::unique_ptr<HandleEntry>>    handles;
    std::atomic<Handle>                                         next_handle{1};
};

Session::Session() : mImpl(std::make_unique<Impl>()) {}

Session::~Session() = default;

Handle Session::Register(Request req) {
    auto entry = std::make_unique<HandleEntry>();
    entry->request = std::move(req);

    entry->writer = std::make_unique<HashedWriter>(entry->request.dest_path,
                                                   entry->request.expected_hash);

    const Handle h = mImpl->next_handle.fetch_add(1, std::memory_order_relaxed);
    std::lock_guard<std::mutex> lk(mImpl->map_mu);
    mImpl->handles.emplace(h, std::move(entry));
    return h;
}

void Session::WriteChunk(Handle h, const uint8_t* data, size_t len) {
    HandleEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) return;
        entry = it->second.get();
    }
    if (entry->state.load(std::memory_order_acquire) != State::InProgress) return;
    if (!entry->writer->Write(data, len)) {
        entry->error_message = "io error while writing temp file";
        entry->writer->Cancel();
        entry->state.store(State::Failed, std::memory_order_release);
        return;
    }
    entry->bytes_done.store(entry->writer->BytesWritten(),
                            std::memory_order_relaxed);
}

void Session::SetBytesTotal(Handle h, uint64_t total) {
    std::lock_guard<std::mutex> lk(mImpl->map_mu);
    auto it = mImpl->handles.find(h);
    if (it == mImpl->handles.end()) return;
    it->second->bytes_total.store(total, std::memory_order_relaxed);
}

Session::FinalizeOutcome Session::FinalizeAttempt(Handle h) {
    HandleEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) return FinalizeOutcome::IoError;
        entry = it->second.get();
    }
    entry->attempts.fetch_add(1, std::memory_order_relaxed);

    const auto result = entry->writer->Finalize();
    switch (result) {
        case HashedWriter::FinalizeResult::Ok:
            return FinalizeOutcome::Verified;
        case HashedWriter::FinalizeResult::HashMismatch:
            return FinalizeOutcome::HashMismatch;
        case HashedWriter::FinalizeResult::IoError:
        default:
            return FinalizeOutcome::IoError;
    }
}

void Session::MarkComplete(Handle h, int http_version) {
    HandleEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) return;
        entry = it->second.get();
    }
    if (entry->state.load(std::memory_order_acquire) != State::InProgress) return;
    entry->http_version.store(http_version, std::memory_order_relaxed);
    entry->state.store(State::Completed, std::memory_order_release);
}

void Session::ResetForRetry(Handle h) {
    HandleEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) return;
        entry = it->second.get();
    }
    // Old writer must be destroyed before constructing the new one: both
    // share the same temp path and ~HashedWriter() unlinks it.
    entry->writer.reset();
    entry->writer = std::make_unique<HashedWriter>(entry->request.dest_path,
                                                   entry->request.expected_hash);
    entry->bytes_done.store(0, std::memory_order_relaxed);
    entry->bytes_total.store(0, std::memory_order_relaxed);
}

void Session::MarkFailed(Handle h, ErrorCategory category, std::string message) {
    HandleEntry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) return;
        entry = it->second.get();
    }
    if (entry->state.load(std::memory_order_acquire) != State::InProgress) return;

    entry->attempts.fetch_add(1, std::memory_order_relaxed);
    entry->writer->Cancel();
    entry->error_message = message.empty() ? std::string(categoryLabel(category))
                                           : std::move(message);
    entry->state.store(State::Failed, std::memory_order_release);
}

PollSnapshot Session::Poll(Handle h) {
    PollSnapshot snap;
    if (h == 0) {
        snap.state         = State::Failed;
        snap.error_message = "invalid_handle";
        return snap;
    }

    std::unique_ptr<HandleEntry> taken;
    {
        std::lock_guard<std::mutex> lk(mImpl->map_mu);
        auto it = mImpl->handles.find(h);
        if (it == mImpl->handles.end()) {
            snap.state         = State::Failed;
            snap.error_message = "invalid_handle";
            return snap;
        }
        auto& entry = *it->second;
        const State s = entry.state.load(std::memory_order_acquire);
        snap.state        = s;
        snap.bytes_done   = entry.bytes_done.load(std::memory_order_relaxed);
        snap.bytes_total  = entry.bytes_total.load(std::memory_order_relaxed);
        snap.attempts     = entry.attempts.load(std::memory_order_relaxed);
        snap.http_version = entry.http_version.load(std::memory_order_relaxed);
        if (s == State::InProgress) {
            return snap;
        }
        snap.error_message = entry.error_message;
        taken = std::move(it->second);
        mImpl->handles.erase(it);
    }
    return snap;
}

void Session::MarkAllPendingFailed(ErrorCategory category, std::string reason) {
    std::lock_guard<std::mutex> lk(mImpl->map_mu);
    for (auto& kv : mImpl->handles) {
        HandleEntry* entry = kv.second.get();
        if (entry->state.load(std::memory_order_acquire) != State::InProgress) continue;
        entry->attempts.fetch_add(1, std::memory_order_relaxed);
        entry->writer->Cancel();
        entry->error_message = reason.empty() ? std::string(categoryLabel(category))
                                              : reason;
        entry->state.store(State::Failed, std::memory_order_release);
    }
}

void Session::RequestCancel(Handle h) {
    std::lock_guard<std::mutex> lk(mImpl->map_mu);
    auto it = mImpl->handles.find(h);
    if (it == mImpl->handles.end()) return;
    it->second->cancel_requested.store(true, std::memory_order_release);
}

bool Session::IsCancelRequested(Handle h) const {
    std::lock_guard<std::mutex> lk(mImpl->map_mu);
    auto it = mImpl->handles.find(h);
    if (it == mImpl->handles.end()) return false;
    return it->second->cancel_requested.load(std::memory_order_acquire);
}

}  // namespace roulin::fetch
