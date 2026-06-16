cmake_minimum_required(VERSION 3.20)
project(roulin VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Static archives are linked into the SHARED libroulin_fetch.* on Linux/Android
# so all targets must compile -fPIC. iOS has no SHARED so this is harmless there.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

option(ROULIN_BUILD_TESTS "Build tests" ON)
option(ROULIN_BUILD_FETCH "Build roulin-fetch (requires libcurl)" ON)

if(ROULIN_BUILD_TESTS)
    enable_testing()  # required for gtest_discover_tests in subdirectories to register with the top-level CTest
    include(FetchContent)
    FetchContent_Declare(
        googletest
        GIT_REPOSITORY https://github.com/google/googletest.git
        GIT_TAG        v1.14.0
        GIT_SHALLOW    TRUE
    )
    set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
    set(INSTALL_GTEST       OFF CACHE BOOL "" FORCE)  # keep dist/ clean — gtest is a test-only dependency
    FetchContent_MakeAvailable(googletest)
endif()

add_subdirectory(core)
if(ROULIN_BUILD_FETCH)
    add_subdirectory(fetch)
endif()
