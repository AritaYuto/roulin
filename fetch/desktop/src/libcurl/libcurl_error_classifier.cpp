#include "libcurl_error_classifier.h"

namespace roulin::fetch::desktop {

ClassifiedError LibcurlErrorClassifier::Classify() const {
    switch (static_cast<CurlErrorKind>(mCode)) {
        case CurlErrorKind::Timeout:
            return {ErrorCategory::Timeout, "timed out"};
        case CurlErrorKind::Cancelled:
            return {ErrorCategory::Cancelled, "cancelled"};
        case CurlErrorKind::WriteError:
        case CurlErrorKind::ReadError:
            return {ErrorCategory::Io, curl_easy_strerror(mCode)};
        case CurlErrorKind::DnsFailure:
        case CurlErrorKind::ConnectFailed:
        case CurlErrorKind::SslHandshake:
        case CurlErrorKind::PeerCertFail:
        case CurlErrorKind::HttpStatus:
            return {ErrorCategory::Network, curl_easy_strerror(mCode)};
    }
    return {ErrorCategory::Network, curl_easy_strerror(mCode)};
}

}  // namespace roulin::fetch::desktop
