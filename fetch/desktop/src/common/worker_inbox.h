#pragma once
#include "roulin/fetch/session.h"  // Handle

#include <condition_variable>
#include <deque>
#include <mutex>

namespace roulin::fetch::desktop {

class WorkerInbox {
public:
    void PostStart(Handle h);
    void PostCancel(Handle h);

    // Atomically moves both queues out under a single lock acquisition.
    void FlushAll(std::deque<Handle>& starts, std::deque<Handle>& cancels);

    // Returns 0 when stop_predicate() becomes true before a start arrives.
    template <class StopPredicate>
    Handle WaitForStart(StopPredicate&& stop_predicate) {
        std::unique_lock<std::mutex> lk(mMu);
        mCv.wait(lk, [&] { return !mStarts.empty() || stop_predicate(); });
        if (mStarts.empty()) return 0;
        Handle h = mStarts.front();
        mStarts.pop_front();
        return h;
    }

    void NotifyAll();

private:
    std::mutex              mMu;
    std::condition_variable mCv;
    std::deque<Handle>      mStarts;
    std::deque<Handle>      mCancels;
};

}  // namespace roulin::fetch::desktop
