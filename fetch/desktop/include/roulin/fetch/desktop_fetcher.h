#pragma once
#include "roulin/fetch/fetcher.h"
#include "roulin/fetch/session.h"

#include <memory>

namespace roulin::fetch::desktop {

// Auto: h2c on http://, ALPN-negotiated HTTP/2 on https://.
enum class HttpMode {
    Auto,
    Http1Only,
};

struct Config {
    int      max_parallel = 8;
    HttpMode http_mode    = HttpMode::Auto;
};

class DesktopBackend;

class DesktopFetcher : public Fetcher {
public:
    DesktopFetcher(Session& session, Config cfg = {});
    ~DesktopFetcher() override;

    Handle       Enqueue(const Request& req) override;
    void         Cancel(Handle h)            override;
    PollSnapshot Poll(Handle h)              override;

    DesktopFetcher(const DesktopFetcher&)            = delete;
    DesktopFetcher& operator=(const DesktopFetcher&) = delete;

private:
    std::unique_ptr<DesktopBackend> mBackend;
};

}  // namespace roulin::fetch::desktop
