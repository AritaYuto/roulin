#pragma once
#include "roulin/fetch/entry_registry.h"
#include "roulin/fetch/fetcher.h"
#include "roulin/fetch/session.h"

// Direct members reference JNI types — requires Android NDK jni.h.
#include <jni.h>

#include "../../../src/android_entry.h"
#include "../../../src/android_handle_consumer.h"

#include <atomic>

namespace roulin::fetch::android {

enum class HttpMode {
    Auto,
    Http1Only,
};

struct Config {
    int      max_parallel = 8;
    HttpMode http_mode    = HttpMode::Auto;
};

// Bridges to a Kotlin AndroidFetcher via JNI; Kotlin drives OkHttp Call.enqueue
// and calls back through JNI exports in android_fetcher.cpp.
class AndroidFetcher : public Fetcher {
public:
    AndroidFetcher(Session& session, Config cfg = {});
    ~AndroidFetcher() override;

    Handle       Enqueue(const Request& req) override;
    void         Cancel(Handle h)            override;
    PollSnapshot Poll(Handle h)              override;

    AndroidFetcher(const AndroidFetcher&)            = delete;
    AndroidFetcher& operator=(const AndroidFetcher&) = delete;

    // Routed by JNI exports via the Kotlin instance's nativeFetcherPtr field.
    void OnChunk(Handle h, const uint8_t* data, size_t len);
    void OnComplete(Handle h, int http_version);
    void OnFailure(Handle h, jint category_int, std::string message);
    void OnBytesTotal(Handle h, uint64_t total);
    bool IsCancelRequested(Handle h) const;

private:
    Session*                       mSession;
    Config                         mConfig;
    std::atomic<bool>              mStopping{false};
    jobject                        mKotlinInstance = nullptr;  // global ref
    EntryRegistry<AndroidEntry>    mRegistry;
    AndroidHandleConsumer          mConsumer;
};

}  // namespace roulin::fetch::android
