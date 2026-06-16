#pragma once
#include "roulin/fetch/error_classifier.h"

#import <Foundation/Foundation.h>

namespace roulin::fetch::apple {

// Codes not listed here fall back to ErrorCategory::Network with
// NSError.localizedDescription as the message.
enum class AppleErrorKind : NSInteger {
    Cancelled            = NSURLErrorCancelled,
    Timeout              = NSURLErrorTimedOut,
    CannotFindHost       = NSURLErrorCannotFindHost,
    CannotConnect        = NSURLErrorCannotConnectToHost,
    NetworkLost          = NSURLErrorNetworkConnectionLost,
    NotConnected         = NSURLErrorNotConnectedToInternet,
    SecureConnectionFail = NSURLErrorSecureConnectionFailed,
    CertUntrusted        = NSURLErrorServerCertificateUntrusted,
};

class AppleErrorClassifier : public ErrorClassifier {
public:
    explicit AppleErrorClassifier(NSError* error) : mError(error) {}

    ClassifiedError Classify() const override;

private:
    NSError* __strong mError;
};

}  // namespace roulin::fetch::apple
