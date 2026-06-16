#include "android_handle_consumer.h"

#include "android_error_classifier.h"
#include "android_jni.h"

#include "roulin/fetch/android_fetcher.h"  // HttpMode

namespace roulin::fetch::android {

AndroidHandleConsumer::AndroidHandleConsumer(JavaVM*                       jvm,
                                              jobject*                      kotlinInstanceRef,
                                              jmethodID                     startMethodId,
                                              HttpMode                      http_mode,
                                              EntryRegistry<AndroidEntry>*  registry)
    : HandleConsumerBase<AndroidEntry>(registry),
      mJavaVM(jvm),
      mKotlinInstanceRef(kotlinInstanceRef),
      mStartMethodId(startMethodId),
      mHttpMode(http_mode) {}

bool AndroidHandleConsumer::reopen(AndroidEntry& entry) {
    if (!mKotlinInstanceRef || !*mKotlinInstanceRef) return false;
    ScopedEnv env(mJavaVM);
    if (!env) return false;
    JNIEnv* je = env.get();

    jstring     j_url  = je->NewStringUTF(entry.request.url.c_str());
    jstring     j_dest = je->NewStringUTF(entry.request.dest_path.c_str());
    jbyteArray  j_hash = je->NewByteArray(32);
    if (j_hash) {
        je->SetByteArrayRegion(
            j_hash, 0, 32,
            reinterpret_cast<const jbyte*>(entry.request.expected_hash.data()));
    }

    je->CallVoidMethod(*mKotlinInstanceRef, mStartMethodId,
                       static_cast<jlong>(entry.handle),
                       j_url, j_hash, j_dest,
                       static_cast<jint>(mHttpMode == HttpMode::Http1Only ? 1 : 0));

    bool ok = true;
    if (je->ExceptionCheck()) {
        je->ExceptionDescribe();
        je->ExceptionClear();
        ok = false;
    }

    if (j_url)  je->DeleteLocalRef(j_url);
    if (j_dest) je->DeleteLocalRef(j_dest);
    if (j_hash) je->DeleteLocalRef(j_hash);

    if (ok) entry.http_version = 0;
    return ok;
}

void AndroidHandleConsumer::OnSuccess(Handle h, int http_version) {
    AndroidEntry* entry_ptr = nullptr;
    registry()->WithEntry(h, [&](AndroidEntry& e) {
        e.http_version = http_version;
        entry_ptr      = &e;
    });
    if (!entry_ptr) return;

    if (entry_ptr->session->IsCancelRequested(h)) {
        handleCancellation(*entry_ptr);
        return;
    }
    handleTransportSuccess(*entry_ptr, http_version);
}

void AndroidHandleConsumer::OnFailure(Handle h, jint category_int,
                                       std::string message) {
    AndroidEntry* entry_ptr = nullptr;
    registry()->WithEntry(h, [&](AndroidEntry& e) { entry_ptr = &e; });
    if (!entry_ptr) return;

    const auto e = AndroidErrorClassifier(category_int,
                                            std::move(message)).Classify();
    if (e.category == ErrorCategory::Cancelled) {
        handleCancellation(*entry_ptr);
    } else {
        handleTransportFailure(*entry_ptr, e.category, e.message);
    }
}

}  // namespace roulin::fetch::android
