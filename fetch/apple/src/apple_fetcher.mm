#include "roulin/fetch/apple_fetcher.h"
#include "roulin/fetch/entry_registry.h"

#include "apple_entry.h"
#include "apple_handle_consumer.h"

#import <Foundation/Foundation.h>

#include <atomic>
#include <memory>
#include <utility>

@class RoulinAppleDelegate;

@interface RoulinAppleDelegate : NSObject <NSURLSessionDataDelegate>
- (instancetype)initWithImpl:(roulin::fetch::apple::AppleFetcher*)instance;
@end

@implementation RoulinAppleDelegate {
    roulin::fetch::apple::AppleFetcher* mInstance;
}

- (instancetype)initWithImpl:(roulin::fetch::apple::AppleFetcher*)instance {
    if (self = [super init]) {
        mInstance = instance;
    }
    return self;
}

static roulin::fetch::Handle handleFromTask(NSURLSessionTask* task) {
    if (!task || !task.taskDescription) return 0;
    return static_cast<roulin::fetch::Handle>(
        strtoull(task.taskDescription.UTF8String, nullptr, 10));
}

- (void)URLSession:(NSURLSession*)session
          dataTask:(NSURLSessionDataTask*)dataTask
didReceiveResponse:(NSURLResponse*)response
 completionHandler:(void (^)(NSURLSessionResponseDisposition))completionHandler {
    if (response.expectedContentLength > 0) {
        mInstance->OnReceiveResponse(handleFromTask(dataTask),
                                      response.expectedContentLength);
    }
    completionHandler(NSURLSessionResponseAllow);
}

- (void)URLSession:(NSURLSession*)session
          dataTask:(NSURLSessionDataTask*)dataTask
    didReceiveData:(NSData*)data {
    auto h = handleFromTask(dataTask);
    if (h == 0) return;
    [data enumerateByteRangesUsingBlock:^(const void* bytes,
                                           NSRange byteRange,
                                           BOOL* /*stop*/) {
        mInstance->OnReceiveData(h,
                              static_cast<const uint8_t*>(bytes),
                              static_cast<size_t>(byteRange.length));
    }];
}

- (void)URLSession:(NSURLSession*)session
              task:(NSURLSessionTask*)task
didFinishCollectingMetrics:(NSURLSessionTaskMetrics*)metrics {
    NSURLSessionTaskTransactionMetrics* last = metrics.transactionMetrics.lastObject;
    if (!last) return;
    NSString* name = last.networkProtocolName;
    int v = 0;
    if ([name isEqualToString:@"h2"])       v = 3;
    else if ([name isEqualToString:@"http/1.1"]) v = 2;
    else if ([name isEqualToString:@"http/1.0"]) v = 1;
    else if ([name isEqualToString:@"h3"])       v = 30;
    if (v != 0) mInstance->OnHttpVersion(handleFromTask(task), v);
}

- (void)URLSession:(NSURLSession*)session
              task:(NSURLSessionTask*)task
didCompleteWithError:(NSError*)error {
    mInstance->OnDidComplete(handleFromTask(task), error);
}

@end

namespace roulin::fetch::apple {

namespace {

NSURLSession* makeNativeSession(int max_parallel,
                                  RoulinAppleDelegate* delegate,
                                  NSOperationQueue* queue) {
    NSURLSessionConfiguration* sc =
        [NSURLSessionConfiguration ephemeralSessionConfiguration];
    sc.HTTPMaximumConnectionsPerHost = max_parallel > 0 ? max_parallel : 8;
    sc.requestCachePolicy = NSURLRequestReloadIgnoringLocalCacheData;
    sc.URLCache           = nil;
    return [NSURLSession sessionWithConfiguration:sc
                                          delegate:delegate
                                     delegateQueue:queue];
}

void tagTaskWithHandle(NSURLSessionDataTask* task, Handle h) {
    task.taskDescription = [NSString stringWithFormat:@"%llu",
                            static_cast<unsigned long long>(h)];
}

}

AppleFetcher::AppleFetcher(Session& session, Config cfg)
    : mSession(&session),
      mConfig(std::move(cfg)),
      mConsumer(&mNativeSession, &mRegistry) {
    mDelegateQueue                            = [[NSOperationQueue alloc] init];
    mDelegateQueue.maxConcurrentOperationCount = 1;
    mDelegateQueue.name                       = @"roulin.fetch.apple.delegate";
    mDelegate       = [[RoulinAppleDelegate alloc] initWithImpl:this];
    mNativeSession  = makeNativeSession(mConfig.max_parallel, mDelegate,
                                          mDelegateQueue);
}

AppleFetcher::~AppleFetcher() {
    mStopping.store(true, std::memory_order_release);

    [mNativeSession invalidateAndCancel];
    [mDelegateQueue waitUntilAllOperationsAreFinished];

    mSession->MarkAllPendingFailed(ErrorCategory::Cancelled,
                                    "session shutting down");
    mRegistry.Clear();
}

Handle AppleFetcher::Enqueue(const Request& req) {
    if (mStopping.load(std::memory_order_acquire)) return 0;

    Handle h = 0;
    try {
        h = mSession->Register(req);
    } catch (...) {
        return 0;
    }
    if (h == 0) return 0;

    auto entry      = std::make_unique<AppleEntry>();
    entry->handle   = h;
    entry->request  = req;
    entry->session  = mSession;

    NSString* urlStr = [NSString stringWithUTF8String:req.url.c_str()];
    NSURL*    url    = urlStr ? [NSURL URLWithString:urlStr] : nil;
    if (!url) {
        mSession->MarkFailed(h, ErrorCategory::Network, "invalid url");
        return h;
    }
    NSURLSessionDataTask* task = [mNativeSession dataTaskWithURL:url];
    if (!task) {
        mSession->MarkFailed(h, ErrorCategory::Network,
                              "NSURLSession dataTaskWithURL failed");
        return h;
    }
    entry->task = task;
    tagTaskWithHandle(task, h);

    mRegistry.Insert(h, std::move(entry));
    [task resume];
    return h;
}

void AppleFetcher::Cancel(Handle h) {
    if (h == 0 || !mRegistry.Contains(h)) return;
    mSession->RequestCancel(h);
    mRegistry.WithEntry(h, [&](AppleEntry& entry) {
        if (entry.task) [entry.task cancel];
    });
}

PollSnapshot AppleFetcher::Poll(Handle h) {
    return mSession->Poll(h);
}

void AppleFetcher::OnReceiveResponse(Handle h, long long contentLength) {
    if (h == 0 || contentLength <= 0) return;
    mSession->SetBytesTotal(h, static_cast<uint64_t>(contentLength));
}

void AppleFetcher::OnReceiveData(Handle h, const uint8_t* data, size_t len) {
    if (h == 0) return;
    if (mSession->IsCancelRequested(h)) {
        mRegistry.WithEntry(h, [&](AppleEntry& entry) {
            if (entry.task) [entry.task cancel];
        });
        return;
    }
    mSession->WriteChunk(h, data, len);
}

void AppleFetcher::OnHttpVersion(Handle h, int version) {
    if (h == 0) return;
    mRegistry.WithEntry(h, [&](AppleEntry& entry) {
        entry.http_version = version;
    });
}

void AppleFetcher::OnDidComplete(Handle h, NSError* error) {
    if (h == 0) return;
    mConsumer.OnDone(h, error);
}

}  // namespace roulin::fetch::apple
