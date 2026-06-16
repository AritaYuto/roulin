#pragma once
#include "roulin/fetch/desktop_fetcher.h"

#include "worker.h"
#include "worker_inbox.h"

namespace roulin::fetch::desktop {

class DesktopBackend : public Fetcher {
public:
    DesktopBackend(Session& session, Config cfg);
    ~DesktopBackend() override = default;

    void         Cancel(Handle h) final;
    PollSnapshot Poll(Handle h)   final;

    DesktopBackend(const DesktopBackend&)            = delete;
    DesktopBackend& operator=(const DesktopBackend&) = delete;

protected:
    virtual void Tick()                           = 0;
    virtual bool hasHandle(Handle h) const        = 0;
    virtual void onCancelRequested(Handle h)      = 0;

    Session*    mSession;
    Config      mConfig;
    WorkerInbox mInbox;
    Worker      mWorker;
};

}  // namespace roulin::fetch::desktop
