#pragma once
#include "roulin/fetch/session.h"

namespace roulin::fetch::desktop {

struct WinHttpEntry {
    Handle   handle   = 0;
    Request  request;
    int      attempts = 0;
    Session* session  = nullptr;
};

}  // namespace roulin::fetch::desktop
