# Android arm64-v8a. API 24 = Unity 2022 LTS floor (64-bit time_t).

if(DEFINED ENV{ANDROID_NDK_HOME})
    set(ANDROID_NDK $ENV{ANDROID_NDK_HOME})
elseif(DEFINED ENV{ANDROID_NDK})
    set(ANDROID_NDK $ENV{ANDROID_NDK})
elseif(EXISTS /opt/android-ndk)
    set(ANDROID_NDK /opt/android-ndk)   # docker/cpp-build/Dockerfile.android
else()
    message(FATAL_ERROR "Android NDK not found — set ANDROID_NDK_HOME or use docker/cpp-build/Dockerfile.android.")
endif()

set(ANDROID_ABI arm64-v8a)
set(ANDROID_PLATFORM android-24)
set(ANDROID_STL c++_static)

include(${ANDROID_NDK}/build/cmake/android.toolchain.cmake)
