#include "worker_inbox.h"

namespace roulin::fetch::desktop {

void WorkerInbox::PostStart(Handle h) {
    {
        std::lock_guard<std::mutex> lk(mMu);
        mStarts.push_back(h);
    }
    mCv.notify_one();
}

void WorkerInbox::PostCancel(Handle h) {
    {
        std::lock_guard<std::mutex> lk(mMu);
        mCancels.push_back(h);
    }
    mCv.notify_all();
}

void WorkerInbox::FlushAll(std::deque<Handle>& starts,
                            std::deque<Handle>& cancels) {
    std::lock_guard<std::mutex> lk(mMu);
    starts.swap(mStarts);
    cancels.swap(mCancels);
}

void WorkerInbox::NotifyAll() {
    mCv.notify_all();
}

}  // namespace roulin::fetch::desktop
