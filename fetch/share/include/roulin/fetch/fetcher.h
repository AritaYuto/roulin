#pragma once
#include "roulin/fetch/session.h"

namespace roulin::fetch {

// The Session passed to a concrete Fetcher must outlive it. ~Fetcher must
// finish or fail every in-flight handle so subsequent Poll calls observe a
// terminal state.
class Fetcher {
public:
    virtual ~Fetcher() = default;

    virtual Handle       Enqueue(const Request& req) = 0;
    virtual void         Cancel(Handle h)            = 0;
    virtual PollSnapshot Poll(Handle h)              = 0;
};

}  // namespace roulin::fetch
