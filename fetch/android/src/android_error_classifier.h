#pragma once
#include "roulin/fetch/error_classifier.h"

#include <jni.h>

#include <string>

namespace roulin::fetch::android {

// Must stay in sync with AndroidFetcher.kt's ERROR_* const ints.
enum class AndroidErrorKind : jint {
    Network      = 0,
    HashMismatch = 1,
    Cancelled    = 2,
    Timeout      = 3,
    Io           = 4,
    Unknown      = 5,
};

class AndroidErrorClassifier : public ErrorClassifier {
public:
    AndroidErrorClassifier(jint category_int, std::string message)
        : mKind(static_cast<AndroidErrorKind>(category_int)),
          mMessage(std::move(message)) {}

    ClassifiedError Classify() const override;

private:
    AndroidErrorKind mKind;
    std::string      mMessage;
};

}  // namespace roulin::fetch::android
