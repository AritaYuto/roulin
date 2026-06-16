#pragma once
#include "roulin/fetch/entry_registry.h"
#include "roulin/fetch/session.h"

#include <string>
#include <utility>

namespace roulin::fetch {

template <class Entry>
class HandleConsumerBase {
public:
    virtual ~HandleConsumerBase() = default;

    void ConsumeAsCancelled(Handle h) {
        mRegistry->WithEntry(h, [&](Entry& entry) {
            publishFailure(entry, ErrorCategory::Cancelled, "cancelled");
        });
        mRegistry->Erase(h);
    }

protected:
    explicit HandleConsumerBase(EntryRegistry<Entry>* registry)
        : mRegistry(registry) {}

    // Returns false when the transport refuses; the base then treats the
    // attempt as terminally failed.
    virtual bool reopen(Entry& entry) = 0;

    virtual void releaseResources(Entry& entry) = 0;

    void handleCancellation(Entry& entry) {
        publishFailure(entry, ErrorCategory::Cancelled, "cancelled");
        mRegistry->Erase(entry.handle);
    }

    void handleTransportSuccess(Entry& entry, int http_version) {
        const auto result = entry.session->FinalizeAttempt(entry.handle);
        if (result == Session::FinalizeOutcome::Verified) {
            publishComplete(entry, http_version);
            mRegistry->Erase(entry.handle);
            return;
        }
        const ErrorCategory cat =
            result == Session::FinalizeOutcome::HashMismatch
                ? ErrorCategory::HashMismatch : ErrorCategory::Io;
        std::string msg =
            result == Session::FinalizeOutcome::HashMismatch
                ? "hash mismatch" : "io error finalising response";
        handleTransportFailure(entry, cat, std::move(msg));
    }

    void handleTransportFailure(Entry& entry, ErrorCategory cat, std::string message) {
        if (++entry.attempts < entry.request.max_attempts) {
            entry.session->ResetForRetry(entry.handle);
            if (reopen(entry)) return;
            cat = ErrorCategory::Network;
            message = "reopen failed";
        }
        publishFailure(entry, cat, std::move(message));
        mRegistry->Erase(entry.handle);
    }

    EntryRegistry<Entry>* registry() noexcept { return mRegistry; }

private:
    void publishComplete(Entry& entry, int http_version) {
        releaseResources(entry);
        entry.session->MarkComplete(entry.handle, http_version);
    }

    void publishFailure(Entry& entry, ErrorCategory cat, std::string msg) {
        releaseResources(entry);
        entry.session->MarkFailed(entry.handle, cat, std::move(msg));
    }

    EntryRegistry<Entry>* mRegistry;
};

}  // namespace roulin::fetch
