#pragma once
#include "roulin/fetch/session.h"

#import <Foundation/Foundation.h>

@class NSURLSessionDataTask;

namespace roulin::fetch::apple {

struct AppleEntry {
    Handle                            handle       = 0;
    Request                           request;
    int                               attempts     = 0;
    int                               http_version = 0;
    Session*                          session      = nullptr;
    NSURLSessionDataTask* __strong    task         = nil;
};

}  // namespace roulin::fetch::apple
