#include "winhttp_backend.h"

#include <stdexcept>
#include <utility>

namespace roulin::fetch::desktop {

namespace {

HINTERNET openWinHttpSession() {
    HINTERNET s = WinHttpOpen(
        L"roulin-fetch/1.0",
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) throw std::runtime_error("WinHttpOpen failed");
    return s;
}

}  // namespace

WinHttpBackend::WinHttpBackend(Session& session, Config cfg)
    : DesktopBackend(session, std::move(cfg)),
      mNativeSession(openWinHttpSession()),
      mConsumer(mNativeSession, mConfig.http_mode, &mRegistry, &mInbox) {
    const int pool_size = mConfig.max_parallel > 0 ? mConfig.max_parallel : 1;
    mWorker.StartPool(pool_size,
                       [this] { Tick(); },
                       [this] { mInbox.NotifyAll(); });
}

WinHttpBackend::~WinHttpBackend() {
    mRegistry.ForEach([&](WinHttpEntry& entry) {
        mSession->RequestCancel(entry.handle);
    });
    mWorker.Stop();
    mRegistry.Clear();
    mSession->MarkAllPendingFailed(ErrorCategory::Cancelled,
                                    "session shutting down");

    if (mNativeSession) {
        WinHttpCloseHandle(mNativeSession);
        mNativeSession = nullptr;
    }
}

Handle WinHttpBackend::Enqueue(const Request& req) {
    if (mWorker.ShouldStop()) return 0;

    Handle h = 0;
    try {
        h = mSession->Register(req);
    } catch (...) {
        return 0;
    }
    if (h == 0) return 0;

    auto entry      = std::make_unique<WinHttpEntry>();
    entry->handle   = h;
    entry->request  = req;
    entry->session  = mSession;

    mRegistry.Insert(h, std::move(entry));
    mInbox.PostStart(h);
    return h;
}

void WinHttpBackend::Tick() {
    const Handle h = mInbox.WaitForStart([this] { return mWorker.ShouldStop(); });
    if (h == 0) return;
    mConsumer.Run(h);
}

}  // namespace roulin::fetch::desktop
