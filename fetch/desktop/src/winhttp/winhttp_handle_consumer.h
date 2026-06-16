#pragma once
#include "roulin/fetch/handle_consumer_base.h"

#include "../common/worker_inbox.h"

#include "winhttp_attempt.h"
#include "winhttp_entry.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>

namespace roulin::fetch::desktop {

class WinHttpHandleConsumer : public HandleConsumerBase<WinHttpEntry> {
public:
    WinHttpHandleConsumer(HINTERNET                    session,
                           HttpMode                     http_mode,
                           EntryRegistry<WinHttpEntry>* registry,
                           WorkerInbox*                 inbox);

    void Run(Handle h);

protected:
    // Re-posts to the inbox; the next Tick runs the next attempt.
    bool reopen(WinHttpEntry& entry) override;

    // Per-attempt HINTERNETs live on the stack inside runWinHttp; no release.
    void releaseResources(WinHttpEntry& /*entry*/) override {}

private:
    HINTERNET     mNativeSession;
    HttpMode      mHttpMode;
    WorkerInbox*  mInbox;
};

}  // namespace roulin::fetch::desktop
