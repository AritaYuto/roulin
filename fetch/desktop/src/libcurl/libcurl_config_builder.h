#pragma once
#include "roulin/fetch/desktop_fetcher.h"  // HttpMode

#include "libcurl_entry.h"

namespace roulin::fetch::desktop {

class LibcurlConfigBuilder {
public:
    LibcurlConfigBuilder(HttpMode http_mode);

    // entry.easy stays nullptr on failure.
    bool Build(LibcurlEntry& entry) const;

    // curl_easy_reset + re-apply, for reusing the handle on retry.
    bool Reconfigure(LibcurlEntry& entry) const;

    HttpMode http_mode() const noexcept { return mHttpMode; }

private:
    void apply(LibcurlEntry& entry) const;

    HttpMode mHttpMode;
};

}  // namespace roulin::fetch::desktop
