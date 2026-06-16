#include "apple_handle_consumer.h"

#include "apple_error_classifier.h"

namespace roulin::fetch::apple {

namespace {

void tagTaskWithHandle(NSURLSessionDataTask* task, Handle h) {
    task.taskDescription = [NSString stringWithFormat:@"%llu",
                            static_cast<unsigned long long>(h)];
}

}  // namespace

AppleHandleConsumer::AppleHandleConsumer(NSURLSession* __strong*    sessionRef,
                                          EntryRegistry<AppleEntry>* registry)
    : HandleConsumerBase<AppleEntry>(registry),
      mNativeSessionRef(sessionRef) {}

void AppleHandleConsumer::releaseResources(AppleEntry& entry) {
    if (entry.task) {
        [entry.task cancel];
        entry.task = nil;
    }
}

bool AppleHandleConsumer::reopen(AppleEntry& entry) {
    NSURLSession* session = *mNativeSessionRef;
    if (!session) return false;
    NSString* urlStr = [NSString stringWithUTF8String:entry.request.url.c_str()];
    NSURL* url = urlStr ? [NSURL URLWithString:urlStr] : nil;
    if (!url) return false;
    NSURLSessionDataTask* task = [session dataTaskWithURL:url];
    if (!task) return false;
    entry.task         = task;
    entry.http_version = 0;
    tagTaskWithHandle(task, entry.handle);
    [task resume];
    return true;
}

void AppleHandleConsumer::OnDone(Handle h, NSError* error) {
    AppleEntry* entry_ptr = nullptr;
    registry()->WithEntry(h, [&](AppleEntry& e) { entry_ptr = &e; });
    if (!entry_ptr) return;

    const bool cancelled =
        entry_ptr->session->IsCancelRequested(h)
        || (error && [error.domain isEqualToString:NSURLErrorDomain]
            && error.code == NSURLErrorCancelled);

    if (cancelled) {
        handleCancellation(*entry_ptr);
        return;
    }

    if (!error) {
        handleTransportSuccess(*entry_ptr, entry_ptr->http_version);
    } else {
        const auto e = AppleErrorClassifier(error).Classify();
        handleTransportFailure(*entry_ptr, e.category, e.message);
    }
}

}  // namespace roulin::fetch::apple
