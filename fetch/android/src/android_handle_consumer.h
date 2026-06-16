#pragma once
#include "roulin/fetch/handle_consumer_base.h"

#include "android_entry.h"

#include <jni.h>

#include <string>

namespace roulin::fetch::android {

enum class HttpMode : int;  // defined in android_fetcher.h

class AndroidHandleConsumer : public HandleConsumerBase<AndroidEntry> {
public:
    AndroidHandleConsumer(JavaVM*                       jvm,
                           jobject*                      kotlinInstanceRef,
                           jmethodID                     startMethodId,
                           HttpMode                      http_mode,
                           EntryRegistry<AndroidEntry>*  registry);

    void OnSuccess(Handle h, int http_version);
    void OnFailure(Handle h, jint category_int, std::string message);

protected:
    bool reopen(AndroidEntry& entry) override;
    void releaseResources(AndroidEntry& /*entry*/) override {}

private:
    JavaVM*    mJavaVM;
    jobject*   mKotlinInstanceRef;  // borrowed
    jmethodID  mStartMethodId;
    HttpMode   mHttpMode;
};

}  // namespace roulin::fetch::android
