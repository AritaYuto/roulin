# iOS arm64 device. Static-only — Unity uses [DllImport("__Internal")] and
# App Store rejects loose dylibs. Deployment target 13.0 = Unity 2022 LTS floor.

set(CMAKE_SYSTEM_NAME iOS)
set(CMAKE_OSX_SYSROOT iphoneos)
set(CMAKE_OSX_ARCHITECTURES arm64)
set(CMAKE_OSX_DEPLOYMENT_TARGET 13.0)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "iOS plugins ship as static archives" FORCE)
