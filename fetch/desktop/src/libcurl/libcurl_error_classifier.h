#pragma once
#include "roulin/fetch/error_classifier.h"

#include <curl/curl.h>

namespace roulin::fetch::desktop {

// Codes not listed here fall back to ErrorCategory::Network with
// curl_easy_strerror as the message.
enum class CurlErrorKind : long {
    Timeout       = CURLE_OPERATION_TIMEDOUT,
    Cancelled     = CURLE_ABORTED_BY_CALLBACK,
    DnsFailure    = CURLE_COULDNT_RESOLVE_HOST,
    ConnectFailed = CURLE_COULDNT_CONNECT,
    SslHandshake  = CURLE_SSL_CONNECT_ERROR,
    PeerCertFail  = CURLE_PEER_FAILED_VERIFICATION,
    WriteError    = CURLE_WRITE_ERROR,
    ReadError     = CURLE_READ_ERROR,
    HttpStatus    = CURLE_HTTP_RETURNED_ERROR,
};

class LibcurlErrorClassifier : public ErrorClassifier {
public:
    explicit LibcurlErrorClassifier(CURLcode code) : mCode(code) {}

    ClassifiedError Classify() const override;

private:
    CURLcode mCode;
};

}  // namespace roulin::fetch::desktop
