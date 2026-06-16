#pragma once
#include "roulin/fetch/session.h"

#include <curl/curl.h>

namespace roulin::fetch::desktop {

struct LibcurlEntry {
    Handle    handle   = 0;
    Request   request;
    CURL*     easy     = nullptr;
    int       attempts = 0;
    bool      attached = false;   // true while the multi owns the CURL handle
    Session*  session  = nullptr;
};

}  // namespace roulin::fetch::desktop
