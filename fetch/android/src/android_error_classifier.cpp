#include "android_error_classifier.h"

namespace roulin::fetch::android {

ClassifiedError AndroidErrorClassifier::Classify() const {
    switch (mKind) {
        case AndroidErrorKind::Network:
            return {ErrorCategory::Network, mMessage};
        case AndroidErrorKind::HashMismatch:
            return {ErrorCategory::HashMismatch,
                    mMessage.empty() ? "hash mismatch" : mMessage};
        case AndroidErrorKind::Cancelled:
            return {ErrorCategory::Cancelled,
                    mMessage.empty() ? "cancelled" : mMessage};
        case AndroidErrorKind::Timeout:
            return {ErrorCategory::Timeout,
                    mMessage.empty() ? "timed out" : mMessage};
        case AndroidErrorKind::Io:
            return {ErrorCategory::Io, mMessage};
        case AndroidErrorKind::Unknown:
            return {ErrorCategory::Unknown,
                    mMessage.empty() ? "unknown" : mMessage};
    }
    return {ErrorCategory::Unknown,
            mMessage.empty() ? "unknown" : mMessage};
}

}  // namespace roulin::fetch::android
