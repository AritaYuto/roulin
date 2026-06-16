#include "winhttp_error_classifier.h"

#include <string>

namespace roulin::fetch::desktop {

ClassifiedError WinHttpErrorClassifier::Classify() const {
    switch (static_cast<WinHttpErrorKind>(mCode)) {
        case WinHttpErrorKind::Timeout:
            return {ErrorCategory::Timeout, mContext + ": timed out"};
        case WinHttpErrorKind::Cancelled:
            return {ErrorCategory::Cancelled, mContext + ": cancelled"};
        case WinHttpErrorKind::NameNotResolved:
        case WinHttpErrorKind::CannotConnect:
        case WinHttpErrorKind::SecureFailure:
        case WinHttpErrorKind::InvalidUrl:
            return {ErrorCategory::Network,
                    mContext + ": winhttp error " + std::to_string(mCode)};
    }
    return {ErrorCategory::Network,
            mContext + ": winhttp error " + std::to_string(mCode)};
}

}  // namespace roulin::fetch::desktop
