#pragma once
#include "roulin/fetch/entry_registry.h"
#include "roulin/fetch/fetcher.h"
#include "roulin/fetch/session.h"

// Requires Obj-C++ compilation context (.mm or LANGUAGE OBJCXX).
#import <Foundation/Foundation.h>

#include "../../../src/apple_entry.h"
#include "../../../src/apple_handle_consumer.h"

@class RoulinAppleDelegate;

#include <atomic>

namespace roulin::fetch::apple {

enum class HttpMode {
    Auto,
    Http1Only,
};

struct Config {
    int      max_parallel = 8;
    HttpMode http_mode    = HttpMode::Auto;
};

// NSURLSession dispatches delegate callbacks on mDelegateQueue (serial), so
// this backend does not need a Worker / WorkerInbox.
class AppleFetcher : public Fetcher {
public:
    AppleFetcher(Session& session, Config cfg = {});
    ~AppleFetcher() override;

    Handle       Enqueue(const Request& req) override;
    void         Cancel(Handle h)            override;
    PollSnapshot Poll(Handle h)              override;

    AppleFetcher(const AppleFetcher&)            = delete;
    AppleFetcher& operator=(const AppleFetcher&) = delete;

    // Public for RoulinAppleDelegate (non-friend Obj-C class) to invoke.
    void OnReceiveResponse(Handle h, long long contentLength);
    void OnReceiveData(Handle h, const uint8_t* data, size_t len);
    void OnHttpVersion(Handle h, int version);
    void OnDidComplete(Handle h, NSError* error);

private:
    Session*                       mSession;
    Config                         mConfig;
    std::atomic<bool>              mStopping{false};
    NSURLSession*    __strong      mNativeSession  = nil;
    NSOperationQueue* __strong     mDelegateQueue  = nil;
    RoulinAppleDelegate* __strong mDelegate       = nil;
    EntryRegistry<AppleEntry>      mRegistry;
    AppleHandleConsumer            mConsumer;
};

}  // namespace roulin::fetch::apple
