#pragma once
#include <jni.h>

namespace roulin::fetch::android {

// Only detaches threads that this scope attached — never the JVM's own.
class ScopedEnv {
public:
    explicit ScopedEnv(JavaVM* vm) {
        if (!vm) return;
        const jint res = vm->GetEnv(reinterpret_cast<void**>(&mEnv),
                                      JNI_VERSION_1_6);
        if (res == JNI_EDETACHED) {
            if (vm->AttachCurrentThread(&mEnv, nullptr) == JNI_OK) {
                mVm       = vm;
                mAttached = true;
            } else {
                mEnv = nullptr;
            }
        } else if (res != JNI_OK) {
            mEnv = nullptr;
        }
    }
    ~ScopedEnv() {
        if (mAttached && mVm) mVm->DetachCurrentThread();
    }

    JNIEnv* get() const noexcept { return mEnv; }
    explicit operator bool() const noexcept { return mEnv != nullptr; }

    ScopedEnv(const ScopedEnv&)            = delete;
    ScopedEnv& operator=(const ScopedEnv&) = delete;

private:
    JNIEnv* mEnv      = nullptr;
    JavaVM* mVm       = nullptr;
    bool    mAttached = false;
};

}  // namespace roulin::fetch::android
