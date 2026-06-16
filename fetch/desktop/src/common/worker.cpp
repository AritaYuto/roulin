#include "worker.h"

#include <utility>

namespace roulin::fetch::desktop {

void Worker::Start(LoopBody body, Wake wake) {
    mWake = std::move(wake);
    mThreads.emplace_back([this, body = std::move(body)] {
        while (!ShouldStop()) body();
    });
}

void Worker::StartPool(int n, LoopBody body, Wake wake) {
    mWake = std::move(wake);
    mThreads.reserve(static_cast<size_t>(n > 0 ? n : 1));
    for (int i = 0; i < (n > 0 ? n : 1); ++i) {
        mThreads.emplace_back([this, body] {
            while (!ShouldStop()) body();
        });
    }
}

void Worker::Stop() {
    mStop.store(true, std::memory_order_release);
    if (mWake) mWake();
    for (auto& t : mThreads) {
        if (t.joinable()) t.join();
    }
    mThreads.clear();
}

}  // namespace roulin::fetch::desktop
