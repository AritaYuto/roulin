#include "winhttp_handle_consumer.h"

namespace roulin::fetch::desktop {

WinHttpHandleConsumer::WinHttpHandleConsumer(HINTERNET                    session,
                                              HttpMode                     http_mode,
                                              EntryRegistry<WinHttpEntry>* registry,
                                              WorkerInbox*                 inbox)
    : HandleConsumerBase<WinHttpEntry>(registry),
      mNativeSession(session),
      mHttpMode(http_mode),
      mInbox(inbox) {}

bool WinHttpHandleConsumer::reopen(WinHttpEntry& entry) {
    mInbox->PostStart(entry.handle);
    return true;
}

void WinHttpHandleConsumer::Run(Handle h) {
    WinHttpEntry* entry_ptr = nullptr;
    registry()->WithEntry(h, [&](WinHttpEntry& e) { entry_ptr = &e; });
    if (!entry_ptr) return;

    if (entry_ptr->session->IsCancelRequested(h)) {
        handleCancellation(*entry_ptr);
        return;
    }

    const auto result = runWinHttp(mNativeSession, mHttpMode, *entry_ptr);

    if (result.cancelled) {
        handleCancellation(*entry_ptr);
    } else if (result.success) {
        handleTransportSuccess(*entry_ptr, result.http_version);
    } else {
        handleTransportFailure(*entry_ptr, result.category, result.message);
    }
}

}  // namespace roulin::fetch::desktop
