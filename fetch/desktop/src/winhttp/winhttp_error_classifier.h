#pragma once
#include "roulin/fetch/error_classifier.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>

#include <string>

namespace roulin::fetch::desktop {

// Codes not listed here fall back to ErrorCategory::Network.
enum class WinHttpErrorKind : DWORD {
    Timeout         = static_cast<DWORD>(ERROR_WINHTTP_TIMEOUT),
    Cancelled       = static_cast<DWORD>(ERROR_WINHTTP_OPERATION_CANCELLED),
    NameNotResolved = static_cast<DWORD>(ERROR_WINHTTP_NAME_NOT_RESOLVED),
    CannotConnect   = static_cast<DWORD>(ERROR_WINHTTP_CANNOT_CONNECT),
    SecureFailure   = static_cast<DWORD>(ERROR_WINHTTP_SECURE_FAILURE),
    InvalidUrl      = static_cast<DWORD>(ERROR_WINHTTP_INVALID_URL),
};

// context: name of the WinHTTP API that failed (e.g. "WinHttpConnect").
class WinHttpErrorClassifier : public ErrorClassifier {
public:
    WinHttpErrorClassifier(DWORD code, const char* context)
        : mCode(code), mContext(context ? context : "") {}

    ClassifiedError Classify() const override;

private:
    DWORD       mCode;
    std::string mContext;
};

}  // namespace roulin::fetch::desktop
