#pragma once
#include "roulin/fetch/handle_consumer_base.h"

#include "apple_entry.h"

#import <Foundation/Foundation.h>

@class NSURLSession;
@class NSURLSessionTask;
@class NSURLSessionDataTask;
@class NSError;

namespace roulin::fetch::apple {

class AppleHandleConsumer : public HandleConsumerBase<AppleEntry> {
public:
    AppleHandleConsumer(NSURLSession* __strong*    sessionRef,
                         EntryRegistry<AppleEntry>* registry);

    void OnDone(Handle h, NSError* error);

protected:
    bool reopen(AppleEntry& entry) override;
    void releaseResources(AppleEntry& entry) override;

private:
    NSURLSession* __strong* mNativeSessionRef;  // borrowed
};

}  // namespace roulin::fetch::apple
