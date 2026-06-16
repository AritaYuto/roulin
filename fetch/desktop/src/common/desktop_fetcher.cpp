#include "roulin/fetch/desktop_fetcher.h"

#include "desktop_backend.h"

#ifdef _WIN32
#  include "../winhttp/winhttp_backend.h"
#else
#  include "../libcurl/libcurl_backend.h"
#endif

#include <utility>

namespace roulin::fetch::desktop {

DesktopFetcher::DesktopFetcher(Session& session, Config cfg)
    :
#ifdef _WIN32
      mBackend(std::make_unique<WinHttpBackend>(session, std::move(cfg)))
#else
      mBackend(std::make_unique<LibcurlBackend>(session, std::move(cfg)))
#endif
{}

DesktopFetcher::~DesktopFetcher() = default;

Handle DesktopFetcher::Enqueue(const Request& req) {
    return mBackend->Enqueue(req);
}

void DesktopFetcher::Cancel(Handle h) {
    mBackend->Cancel(h);
}

PollSnapshot DesktopFetcher::Poll(Handle h) {
    return mBackend->Poll(h);
}

}  // namespace roulin::fetch::desktop
