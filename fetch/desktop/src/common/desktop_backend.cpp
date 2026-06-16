#include "desktop_backend.h"

#include <utility>

namespace roulin::fetch::desktop {

DesktopBackend::DesktopBackend(Session& session, Config cfg)
    : mSession(&session), mConfig(std::move(cfg)) {}

void DesktopBackend::Cancel(Handle h) {
    if (h == 0 || !hasHandle(h)) return;
    mSession->RequestCancel(h);
    onCancelRequested(h);
}

PollSnapshot DesktopBackend::Poll(Handle h) {
    return mSession->Poll(h);
}

}  // namespace roulin::fetch::desktop
