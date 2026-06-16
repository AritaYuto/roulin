#include "libcurl_backend.h"

#include <mutex>
#include <stdexcept>
#include <utility>

namespace roulin::fetch::desktop {

namespace {

constexpr int kPollTimeoutMs = 200;  // bounds shutdown latency if multi misses a wakeup

void ensureCurlGlobalInit() {
    static std::once_flag flag;
    std::call_once(flag, [] {
        if (curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK) {
            throw std::runtime_error("curl_global_init failed");
        }
    });
}

CURLM* openMulti(int max_parallel) {
    ensureCurlGlobalInit();
    CURLM* m = curl_multi_init();
    if (!m) throw std::runtime_error("curl_multi_init failed");
    curl_multi_setopt(m, CURLMOPT_MAX_TOTAL_CONNECTIONS,
                      static_cast<long>(max_parallel));
    curl_multi_setopt(m, CURLMOPT_PIPELINING, CURLPIPE_MULTIPLEX);
    return m;
}

}  // namespace

LibcurlBackend::LibcurlBackend(Session& session, Config cfg)
    : DesktopBackend(session, std::move(cfg)),
      mMulti(openMulti(mConfig.max_parallel)),
      mBuilder(mConfig.http_mode),
      mConsumer(mMulti, &mRegistry, &mBuilder) {
    mWorker.Start([this] { Tick(); },
                  [this] { curl_multi_wakeup(mMulti); });
}

LibcurlBackend::~LibcurlBackend() {
    mWorker.Stop();
    mRegistry.ForEach([&](LibcurlEntry& entry) {
        if (entry.attached && entry.easy) {
            curl_multi_remove_handle(mMulti, entry.easy);
            entry.attached = false;
        }
        if (entry.easy) {
            curl_easy_cleanup(entry.easy);
            entry.easy = nullptr;
        }
    });
    mRegistry.Clear();
    mSession->MarkAllPendingFailed(ErrorCategory::Cancelled,
                                    "session shutting down");

    if (mMulti) {
        curl_multi_cleanup(mMulti);
        mMulti = nullptr;
    }
}

Handle LibcurlBackend::Enqueue(const Request& req) {
    if (mWorker.ShouldStop()) return 0;

    Handle h = 0;
    try {
        h = mSession->Register(req);
    } catch (...) {
        return 0;
    }
    if (h == 0) return 0;

    auto entry      = std::make_unique<LibcurlEntry>();
    entry->handle   = h;
    entry->request  = req;
    entry->session  = mSession;

    mRegistry.Insert(h, std::move(entry));
    mInbox.PostStart(h);
    curl_multi_wakeup(mMulti);
    return h;
}

void LibcurlBackend::onCancelRequested(Handle h) {
    mInbox.PostCancel(h);
    curl_multi_wakeup(mMulti);
}

void LibcurlBackend::Tick() {
    // Cancels before starts: an entry cancelled before its start is popped
    // never touches the multi.
    std::deque<Handle> starts;
    std::deque<Handle> cancels;
    mInbox.FlushAll(starts, cancels);

    for (Handle h : cancels) {
        mConsumer.ConsumeAsCancelled(h);
    }

    for (Handle h : starts) {
        if (mSession->IsCancelRequested(h)) {
            mConsumer.ConsumeAsCancelled(h);
            continue;
        }
        bool ok = false;
        mRegistry.WithEntry(h, [&](LibcurlEntry& entry) {
            if (!mBuilder.Build(entry)) {
                mSession->MarkFailed(h, ErrorCategory::Network,
                                      "curl_easy_init failed");
                return;
            }
            if (curl_multi_add_handle(mMulti, entry.easy) != CURLM_OK) {
                curl_easy_cleanup(entry.easy);
                entry.easy = nullptr;
                mSession->MarkFailed(h, ErrorCategory::Network,
                                      "curl_multi_add_handle failed");
                return;
            }
            entry.attached = true;
            ok = true;
        });
        if (!ok) mRegistry.Erase(h);
    }

    int running = 0;
    curl_multi_perform(mMulti, &running);

    CURLMsg* msg      = nullptr;
    int      msgs_left = 0;
    while ((msg = curl_multi_info_read(mMulti, &msgs_left))) {
        if (msg->msg == CURLMSG_DONE) {
            mConsumer.OnDone(*msg);
        }
    }

    curl_multi_poll(mMulti, nullptr, 0, kPollTimeoutMs, nullptr);
}

}  // namespace roulin::fetch::desktop
