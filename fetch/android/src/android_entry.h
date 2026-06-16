#pragma once
#include "roulin/fetch/session.h"

namespace roulin::fetch::android {

// OkHttp Call lives on the Kotlin side; this entry only holds bookkeeping.
struct AndroidEntry {
    Handle    handle       = 0;
    Request   request;
    int       attempts     = 0;
    int       http_version = 0;
    Session*  session      = nullptr;
};

}  // namespace roulin::fetch::android
