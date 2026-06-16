#pragma once
#include "roulin/fetch/session.h"  // ErrorCategory

#include <string>

namespace roulin::fetch {

struct ClassifiedError {
    ErrorCategory category;
    std::string   message;
};

// Backend-provided wrapper around a native error type (CURLcode, DWORD,
// NSError, jthrowable). Constructed at the detection site, queried once.
class ErrorClassifier {
public:
    virtual ~ErrorClassifier() = default;
    virtual ClassifiedError Classify() const = 0;
};

}  // namespace roulin::fetch
