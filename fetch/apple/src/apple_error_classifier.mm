#include "apple_error_classifier.h"

#include <utility>

namespace roulin::fetch::apple {

ClassifiedError AppleErrorClassifier::Classify() const {
    if (!mError) return {ErrorCategory::Unknown, "unknown error"};

    std::string msg;
    if (mError.localizedDescription) {
        msg = mError.localizedDescription.UTF8String;
    } else {
        msg = "ns error";
    }

    if (![mError.domain isEqualToString:NSURLErrorDomain]) {
        return {ErrorCategory::Network, std::move(msg)};
    }

    switch (static_cast<AppleErrorKind>(mError.code)) {
        case AppleErrorKind::Cancelled:
            return {ErrorCategory::Cancelled, "cancelled"};
        case AppleErrorKind::Timeout:
            return {ErrorCategory::Timeout, "timed out"};
        case AppleErrorKind::CannotFindHost:
        case AppleErrorKind::CannotConnect:
        case AppleErrorKind::NetworkLost:
        case AppleErrorKind::NotConnected:
        case AppleErrorKind::SecureConnectionFail:
        case AppleErrorKind::CertUntrusted:
            return {ErrorCategory::Network, std::move(msg)};
    }
    return {ErrorCategory::Network, std::move(msg)};
}

}  // namespace roulin::fetch::apple
