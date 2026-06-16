#include "roulin/fetch/android_fetcher.h"

#include "android_entry.h"
#include "android_jni.h"

#include <jni.h>

#include <cstring>
#include <stdexcept>
#include <string>
#include <utility>

namespace roulin::fetch::android {

namespace {

// All resolved at JNI_OnLoad.
JavaVM*   gJavaVM                 = nullptr;
jclass    gKotlinFetcherClass     = nullptr;  // global ref
jmethodID gMethodStart            = nullptr;
jmethodID gMethodCancel           = nullptr;
jmethodID gMethodSetNativeFetcher = nullptr;
jfieldID  gFieldNativeFetcherPtr  = nullptr;

int httpModeToInt(HttpMode m) {
    return m == HttpMode::Http1Only ? 1 : 0;
}

jobject createKotlinFetcher(JNIEnv* env, AndroidFetcher* self) {
    if (!gKotlinFetcherClass) return nullptr;
    jmethodID ctor = env->GetMethodID(gKotlinFetcherClass, "<init>", "()V");
    if (!ctor) return nullptr;
    jobject local = env->NewObject(gKotlinFetcherClass, ctor);
    if (!local) return nullptr;
    jobject global = env->NewGlobalRef(local);
    env->DeleteLocalRef(local);
    if (!global) return nullptr;
    env->CallVoidMethod(global, gMethodSetNativeFetcher,
                        reinterpret_cast<jlong>(self));
    return global;
}

AndroidFetcher* fetcherFromInstance(JNIEnv* env, jobject self) {
    if (!self || !gFieldNativeFetcherPtr) return nullptr;
    const jlong ptr = env->GetLongField(self, gFieldNativeFetcherPtr);
    return reinterpret_cast<AndroidFetcher*>(ptr);
}

}  // namespace

AndroidFetcher::AndroidFetcher(Session& session, Config cfg)
    : mSession(&session),
      mConfig(std::move(cfg)),
      mConsumer(gJavaVM, &mKotlinInstance, gMethodStart, mConfig.http_mode,
                &mRegistry) {
    if (!gJavaVM || !gKotlinFetcherClass) {
        throw std::runtime_error(
            "AndroidFetcher: JNI_OnLoad has not registered the Kotlin class");
    }
    ScopedEnv env(gJavaVM);
    if (!env) {
        throw std::runtime_error(
            "AndroidFetcher: failed to attach thread to JVM");
    }
    mKotlinInstance = createKotlinFetcher(env.get(), this);
    if (!mKotlinInstance) {
        throw std::runtime_error(
            "AndroidFetcher: failed to instantiate Kotlin AndroidFetcher");
    }
}

AndroidFetcher::~AndroidFetcher() {
    mStopping.store(true, std::memory_order_release);

    // Detach the Kotlin instance before its callbacks could deref a freed ptr.
    if (mKotlinInstance) {
        ScopedEnv env(gJavaVM);
        if (env) {
            env.get()->CallVoidMethod(mKotlinInstance,
                                       gMethodSetNativeFetcher,
                                       static_cast<jlong>(0));
            env.get()->DeleteGlobalRef(mKotlinInstance);
        }
        mKotlinInstance = nullptr;
    }

    mSession->MarkAllPendingFailed(ErrorCategory::Cancelled,
                                    "session shutting down");
    mRegistry.Clear();
}

Handle AndroidFetcher::Enqueue(const Request& req) {
    if (mStopping.load(std::memory_order_acquire)) return 0;
    if (!mKotlinInstance) return 0;

    Handle h = 0;
    try {
        h = mSession->Register(req);
    } catch (...) {
        return 0;
    }
    if (h == 0) return 0;

    auto entry      = std::make_unique<AndroidEntry>();
    entry->handle   = h;
    entry->request  = req;
    entry->session  = mSession;
    mRegistry.Insert(h, std::move(entry));

    ScopedEnv env(gJavaVM);
    if (!env) {
        mSession->MarkFailed(h, ErrorCategory::Unknown,
                              "failed to attach thread to JVM");
        mRegistry.Erase(h);
        return h;
    }
    JNIEnv* je = env.get();

    jstring     j_url  = je->NewStringUTF(req.url.c_str());
    jstring     j_dest = je->NewStringUTF(req.dest_path.c_str());
    jbyteArray  j_hash = je->NewByteArray(32);
    if (j_hash) {
        je->SetByteArrayRegion(
            j_hash, 0, 32,
            reinterpret_cast<const jbyte*>(req.expected_hash.data()));
    }

    je->CallVoidMethod(mKotlinInstance, gMethodStart,
                       static_cast<jlong>(h),
                       j_url, j_hash, j_dest,
                       static_cast<jint>(httpModeToInt(mConfig.http_mode)));

    if (je->ExceptionCheck()) {
        je->ExceptionDescribe();
        je->ExceptionClear();
        mSession->MarkFailed(h, ErrorCategory::Unknown,
                              "java exception during start");
        mRegistry.Erase(h);
    }

    if (j_url)  je->DeleteLocalRef(j_url);
    if (j_dest) je->DeleteLocalRef(j_dest);
    if (j_hash) je->DeleteLocalRef(j_hash);

    return h;
}

void AndroidFetcher::Cancel(Handle h) {
    if (h == 0 || !mKotlinInstance) return;
    if (!mRegistry.Contains(h)) return;
    mSession->RequestCancel(h);

    ScopedEnv env(gJavaVM);
    if (!env) return;
    JNIEnv* je = env.get();
    je->CallVoidMethod(mKotlinInstance, gMethodCancel, static_cast<jlong>(h));
    if (je->ExceptionCheck()) {
        je->ExceptionDescribe();
        je->ExceptionClear();
    }
}

PollSnapshot AndroidFetcher::Poll(Handle h) {
    return mSession->Poll(h);
}

void AndroidFetcher::OnChunk(Handle h, const uint8_t* data, size_t len) {
    if (!data || len == 0) return;
    mSession->WriteChunk(h, data, len);
}

void AndroidFetcher::OnComplete(Handle h, int http_version) {
    mConsumer.OnSuccess(h, http_version);
}

void AndroidFetcher::OnFailure(Handle h, jint category_int, std::string message) {
    mConsumer.OnFailure(h, category_int, std::move(message));
}

void AndroidFetcher::OnBytesTotal(Handle h, uint64_t total) {
    mSession->SetBytesTotal(h, total);
}

bool AndroidFetcher::IsCancelRequested(Handle h) const {
    return mSession->IsCancelRequested(h);
}

}  // namespace roulin::fetch::android


// Kotlin AndroidFetcher.nativeFetcherPtr → AndroidFetcher* (no global table).
extern "C" {

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* /*reserved*/) {
    using namespace roulin::fetch::android;
    gJavaVM = vm;

    JNIEnv* env = nullptr;
    if (vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) != JNI_OK) {
        return JNI_ERR;
    }

    jclass local_cls = env->FindClass("io/roulin/fetch/AndroidFetcher");
    if (!local_cls) return JNI_ERR;
    gKotlinFetcherClass =
        static_cast<jclass>(env->NewGlobalRef(local_cls));
    env->DeleteLocalRef(local_cls);
    if (!gKotlinFetcherClass) return JNI_ERR;

    gMethodStart = env->GetMethodID(
        gKotlinFetcherClass, "start",
        "(JLjava/lang/String;[BLjava/lang/String;I)V");
    gMethodCancel = env->GetMethodID(
        gKotlinFetcherClass, "cancel", "(J)V");
    gMethodSetNativeFetcher = env->GetMethodID(
        gKotlinFetcherClass, "setNativeFetcher", "(J)V");
    gFieldNativeFetcherPtr = env->GetFieldID(
        gKotlinFetcherClass, "nativeFetcherPtr", "J");

    if (!gMethodStart || !gMethodCancel || !gMethodSetNativeFetcher
        || !gFieldNativeFetcherPtr) {
        return JNI_ERR;
    }

    return JNI_VERSION_1_6;
}

JNIEXPORT void JNICALL
Java_io_assetroulin_fetch_AndroidFetcher_nativeChunk(
        JNIEnv* env, jobject self, jlong handle, jbyteArray data, jint len) {
    auto* fetcher = roulin::fetch::android::fetcherFromInstance(env, self);
    if (!fetcher || !data || len <= 0) return;
    jbyte* bytes = env->GetByteArrayElements(data, nullptr);
    if (!bytes) return;
    fetcher->OnChunk(static_cast<roulin::fetch::Handle>(handle),
                      reinterpret_cast<const uint8_t*>(bytes),
                      static_cast<size_t>(len));
    env->ReleaseByteArrayElements(data, bytes, JNI_ABORT);
}

JNIEXPORT void JNICALL
Java_io_assetroulin_fetch_AndroidFetcher_nativeComplete(
        JNIEnv* env, jobject self, jlong handle, jint httpVersion) {
    auto* fetcher = roulin::fetch::android::fetcherFromInstance(env, self);
    if (!fetcher) return;
    fetcher->OnComplete(static_cast<roulin::fetch::Handle>(handle),
                         static_cast<int>(httpVersion));
}

JNIEXPORT void JNICALL
Java_io_assetroulin_fetch_AndroidFetcher_nativeFailed(
        JNIEnv* env, jobject self, jlong handle, jint category, jstring msgJStr) {
    auto* fetcher = roulin::fetch::android::fetcherFromInstance(env, self);
    if (!fetcher) return;
    std::string msg;
    if (msgJStr) {
        const char* utf = env->GetStringUTFChars(msgJStr, nullptr);
        if (utf) {
            msg.assign(utf);
            env->ReleaseStringUTFChars(msgJStr, utf);
        }
    }
    fetcher->OnFailure(static_cast<roulin::fetch::Handle>(handle),
                        category, std::move(msg));
}

JNIEXPORT jboolean JNICALL
Java_io_assetroulin_fetch_AndroidFetcher_nativeShouldContinue(
        JNIEnv* env, jobject self, jlong handle) {
    auto* fetcher = roulin::fetch::android::fetcherFromInstance(env, self);
    if (!fetcher) return JNI_FALSE;
    return fetcher->IsCancelRequested(
        static_cast<roulin::fetch::Handle>(handle)) ? JNI_FALSE : JNI_TRUE;
}

JNIEXPORT void JNICALL
Java_io_assetroulin_fetch_AndroidFetcher_nativeSetBytesTotal(
        JNIEnv* env, jobject self, jlong handle, jlong total) {
    auto* fetcher = roulin::fetch::android::fetcherFromInstance(env, self);
    if (!fetcher || total < 0) return;
    fetcher->OnBytesTotal(static_cast<roulin::fetch::Handle>(handle),
                           static_cast<uint64_t>(total));
}

}  // extern "C"
