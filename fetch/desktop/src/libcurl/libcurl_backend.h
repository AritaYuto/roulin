#pragma once
#include "roulin/fetch/entry_registry.h"

#include "../common/desktop_backend.h"

#include "libcurl_config_builder.h"
#include "libcurl_entry.h"
#include "libcurl_handle_consumer.h"

#include <curl/curl.h>

namespace roulin::fetch::desktop {

class LibcurlBackend : public DesktopBackend {
public:
    LibcurlBackend(Session& session, Config cfg);
    ~LibcurlBackend() override;

    Handle Enqueue(const Request& req) override;

    LibcurlBackend(const LibcurlBackend&)            = delete;
    LibcurlBackend& operator=(const LibcurlBackend&) = delete;

protected:
    void Tick() override;
    bool hasHandle(Handle h) const override { return mRegistry.Contains(h); }
    void onCancelRequested(Handle h) override;

private:
    CURLM*                       mMulti = nullptr;
    EntryRegistry<LibcurlEntry>  mRegistry;
    LibcurlConfigBuilder         mBuilder;
    LibcurlHandleConsumer        mConsumer;
};

}  // namespace roulin::fetch::desktop
