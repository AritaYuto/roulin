#pragma once
#include "roulin/fetch/desktop_fetcher.h"  // HttpMode
#include "roulin/fetch/session.h"

#include "winhttp_entry.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>

#include <string>

namespace roulin::fetch::desktop {

struct WinHttpResult {
    bool          success      = false;
    bool          cancelled    = false;
    int           http_version = 0;
    ErrorCategory category     = ErrorCategory::Unknown;
    std::string   message;
};

// Runs one synchronous WinHTTP attempt on the calling thread.
WinHttpResult runWinHttp(HINTERNET    session,
                          HttpMode     http_mode,
                          WinHttpEntry& entry);

}  // namespace roulin::fetch::desktop
