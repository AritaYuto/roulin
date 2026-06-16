#pragma once
#include "roulin/fetch/handle_consumer_base.h"

#include "libcurl_config_builder.h"
#include "libcurl_entry.h"

#include <curl/curl.h>

namespace roulin::fetch::desktop {

class LibcurlHandleConsumer : public HandleConsumerBase<LibcurlEntry> {
public:
    LibcurlHandleConsumer(CURLM*                       multi,
                           EntryRegistry<LibcurlEntry>* registry,
                           LibcurlConfigBuilder*        builder);

    void OnDone(const CURLMsg& msg);

protected:
    bool reopen(LibcurlEntry& entry) override;
    void releaseResources(LibcurlEntry& entry) override;

private:
    CURLM*                mMulti;     // borrowed
    LibcurlConfigBuilder* mBuilder;
};

}  // namespace roulin::fetch::desktop
