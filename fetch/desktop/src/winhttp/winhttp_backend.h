#pragma once
#include "roulin/fetch/entry_registry.h"

#include "../common/desktop_backend.h"

#include "winhttp_entry.h"
#include "winhttp_handle_consumer.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>

namespace roulin::fetch::desktop {

// Concurrency comes from N=max_parallel worker threads, each running one
// synchronous WinHTTP attempt per Tick.
class WinHttpBackend : public DesktopBackend {
public:
    WinHttpBackend(Session& session, Config cfg);
    ~WinHttpBackend() override;

    Handle Enqueue(const Request& req) override;

    WinHttpBackend(const WinHttpBackend&)            = delete;
    WinHttpBackend& operator=(const WinHttpBackend&) = delete;

protected:
    void Tick() override;
    bool hasHandle(Handle h) const override { return mRegistry.Contains(h); }
    void onCancelRequested(Handle /*h*/) override { mInbox.NotifyAll(); }

private:
    HINTERNET                    mNativeSession = nullptr;
    EntryRegistry<WinHttpEntry>  mRegistry;
    WinHttpHandleConsumer        mConsumer;
};

}  // namespace roulin::fetch::desktop
