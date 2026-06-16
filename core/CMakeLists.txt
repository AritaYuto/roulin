include(FetchContent)

# --- BLAKE3 -----------------------------------------------------------------
# Compile BLAKE3 C sources directly instead of using its own CMakeLists.
# blake3_dispatch.c embeds GNU assembler SIMD stubs; on Ubuntu 22.04 / GCC 15
# the assembler emits DWARF5 .base64 sections that binutils 2.38 cannot parse.
# Disabling all SIMD below forces the portable C fallback and avoids the issue.
FetchContent_Declare(
    blake3
    GIT_REPOSITORY https://github.com/BLAKE3-team/BLAKE3.git
    GIT_TAG        1.5.4
    GIT_SHALLOW    TRUE
)
FetchContent_GetProperties(blake3)
if(NOT blake3_POPULATED)
    FetchContent_Populate(blake3)
endif()

add_library(blake3 STATIC
    ${blake3_SOURCE_DIR}/c/blake3.c
    ${blake3_SOURCE_DIR}/c/blake3_portable.c
    ${blake3_SOURCE_DIR}/c/blake3_dispatch.c
)
target_include_directories(blake3 PUBLIC ${blake3_SOURCE_DIR}/c)
target_compile_definitions(blake3 PRIVATE
    BLAKE3_NO_SSE2
    BLAKE3_NO_SSE41
    BLAKE3_NO_AVX2
    BLAKE3_NO_AVX512
)

# blake3_dispatch.c hard-references blake3_hash_many_neon on aarch64.
# CMAKE_SYSTEM_NAME is needed for the iOS toolchain, where the other two
# variables do not reliably propagate into this scope.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$"
   OR CMAKE_OSX_ARCHITECTURES MATCHES "arm64"
   OR CMAKE_SYSTEM_NAME STREQUAL "iOS")
    target_sources(blake3 PRIVATE ${blake3_SOURCE_DIR}/c/blake3_neon.c)
    target_compile_definitions(blake3 PRIVATE BLAKE3_USE_NEON=1)
endif()

# --- FlatBuffers ------------------------------------------------------------
# Header-only runtime vendored at third_party/flatbuffers/ (submodule).
# Generated code is committed under core/generated/; regenerate with
# `go run ./tools-dev/generate-fbs` after editing core/schema/*.fbs.
set(FLATBUFFERS_INCLUDE_DIR ${CMAKE_SOURCE_DIR}/third_party/flatbuffers/include)
if(NOT EXISTS ${FLATBUFFERS_INCLUDE_DIR}/flatbuffers/flatbuffers.h)
    message(FATAL_ERROR "third_party/flatbuffers is empty — run `git submodule update --init --recursive`.")
endif()

set(ROULIN_GENERATED_DIR ${CMAKE_CURRENT_SOURCE_DIR}/generated)

# --- roulin-core library ---------------------------------------------------
add_library(roulin-core STATIC
    src/version.cpp
    src/hash.cpp
    src/algo/hash/blake3_hasher.cpp
    src/algo/hash/hasher.cpp
    src/algo/hash/custom_hash.cpp
    src/algo/cipher/chacha20_cipher.cpp
    src/algo/cipher/cipher.cpp
    src/algo/cipher/custom_cipher.cpp
    src/algo/compressor/compressor.cpp
    src/storage/blob_store.cpp
    src/storage/blob_reader.cpp
    src/parcel/index.cpp
    src/parcel/parcel.cpp
    src/parcel/diff.cpp
    bindings/c/roulin.cpp
)

target_include_directories(roulin-core
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/bindings/c>  # C FFI header (roulin.h)
            $<INSTALL_INTERFACE:include>
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src   # exposes internal headers (blake3_hash.h, etc.) to the library only
            ${ROULIN_GENERATED_DIR}          # exposes FlatBuffers generated headers to the library only
            ${FLATBUFFERS_INCLUDE_DIR}        # FlatBuffers vendored headers (header-only runtime)
)

target_compile_features(roulin-core PUBLIC cxx_std_17)

# Static archive: no DLL decoration on Windows, for both the impl and consumers.
target_compile_definitions(roulin-core PUBLIC ROULIN_CORE_STATIC)

target_link_libraries(roulin-core PRIVATE blake3)

if(IOS)
    add_custom_command(TARGET roulin-core POST_BUILD
        COMMAND libtool -static -o
                $<TARGET_FILE:roulin-core>
                $<TARGET_FILE:roulin-core>
                $<TARGET_FILE:blake3>
        VERBATIM)
endif()

# ---- SHARED roulin_core for Unity / UE / Godot P/Invoke. Skipped on iOS
# (App Store rejects loose dylibs — iOS uses the STATIC roulin-core via
# __Internal).
if(NOT IOS)
add_library(roulin_core SHARED
    src/version.cpp
    src/hash.cpp
    src/algo/hash/blake3_hasher.cpp
    src/algo/hash/hasher.cpp
    src/algo/hash/custom_hash.cpp
    src/algo/cipher/chacha20_cipher.cpp
    src/algo/cipher/cipher.cpp
    src/algo/cipher/custom_cipher.cpp
    src/algo/compressor/compressor.cpp
    src/storage/blob_store.cpp
    src/storage/blob_reader.cpp
    src/parcel/index.cpp
    src/parcel/parcel.cpp
    src/parcel/diff.cpp
    bindings/c/roulin.cpp
)
target_include_directories(roulin_core
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
            ${CMAKE_CURRENT_SOURCE_DIR}/bindings/c
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src
            ${ROULIN_GENERATED_DIR}
            ${FLATBUFFERS_INCLUDE_DIR}
)
target_compile_features(roulin_core PUBLIC cxx_std_17)
target_compile_definitions(roulin_core PRIVATE ROULIN_CORE_EXPORTS)
target_link_libraries(roulin_core PRIVATE blake3)
set_target_properties(roulin_core PROPERTIES OUTPUT_NAME "roulin_core")
# Android 15+ on ARM64 requires 16KB page alignment.
if(ANDROID)
    target_link_options(roulin_core PRIVATE "LINKER:-z,max-page-size=16384")
endif()
# MinGW prefixes shared libs with "lib"; strip it so [DllImport("roulin_core")]
# resolves on Windows.
if(WIN32)
    set_target_properties(roulin_core PROPERTIES PREFIX "")
endif()
endif()  # NOT IOS

# ---- Install layout for engine bundling --------------------------------------
# build-cpp runs `cmake --install --prefix dist/<plat>-<arch>/` →
# dist/<plat>-<arch>/{lib/{libroulin_core.so|roulin_core.dll|libroulin-core.a},include/roulin/}.
# Static is required for iOS (__Internal); shared everywhere else.
install(TARGETS roulin-core
    ARCHIVE DESTINATION lib
)
if(NOT IOS)
    install(TARGETS roulin_core
        LIBRARY DESTINATION lib    # .so / .dylib
        ARCHIVE DESTINATION lib    # MinGW import .lib
        RUNTIME DESTINATION lib    # .dll (kept in lib/ for engine bundling)
    )
endif()
install(FILES bindings/c/roulin.h
    DESTINATION include/roulin
)

if(ROULIN_BUILD_TESTS)
    add_subdirectory(tests)
endif()
