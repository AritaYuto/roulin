#pragma once
#include <atomic>
#include <functional>
#include <thread>
#include <vector>

namespace roulin::fetch::desktop {

// The body callable must check ShouldStop() at every wake point.
// wake() is invoked from Stop() to break threads out of platform waits.
class Worker {
public:
    using LoopBody = std::function<void()>;
    using Wake     = std::function<void()>;

    Worker() = default;
    ~Worker() { Stop(); }

    Worker(const Worker&)            = delete;
    Worker& operator=(const Worker&) = delete;

    void Start(LoopBody body, Wake wake);

    // N threads each running body(). For backends that scale by worker count.
    void StartPool(int n, LoopBody body, Wake wake);

    // Idempotent.
    void Stop();

    bool ShouldStop() const noexcept {
        return mStop.load(std::memory_order_acquire);
    }

private:
    std::vector<std::thread> mThreads;
    std::atomic<bool>        mStop{false};
    Wake                     mWake;
};

}  // namespace roulin::fetch::desktop
