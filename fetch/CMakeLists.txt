# apple_fetcher.h references Obj-C types directly → bindings TU is Obj-C++.
if(APPLE)
    enable_language(OBJC)
    enable_language(OBJCXX)
endif()

add_subdirectory(share)

if(APPLE)
    add_subdirectory(apple)
elseif(ANDROID)
    add_subdirectory(android)
else()
    add_subdirectory(desktop)
endif()

add_library(roulin-fetch STATIC
    bindings/c/fetch.cpp
)

target_include_directories(roulin-fetch
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/bindings/c>
            $<INSTALL_INTERFACE:include>
)

target_compile_features(roulin-fetch PUBLIC cxx_std_17)

if(APPLE)
    set_source_files_properties(bindings/c/fetch.cpp
        TARGET_DIRECTORY roulin-fetch
        PROPERTIES LANGUAGE OBJCXX COMPILE_FLAGS "-fobjc-arc")
endif()

if(APPLE)
    target_link_libraries(roulin-fetch
        PUBLIC  roulin-fetch-share
                roulin-fetch-apple
                roulin-core
    )
elseif(ANDROID)
    target_link_libraries(roulin-fetch
        PUBLIC  roulin-fetch-share
                roulin-fetch-android
                roulin-core
    )
else()
    target_link_libraries(roulin-fetch
        PUBLIC  roulin-fetch-share
                roulin-fetch-desktop
                roulin-core
    )
endif()

# SHARED roulin_fetch for non-iOS P/Invoke (Unity / UE / Godot).
if(NOT IOS)
    add_library(roulin_fetch SHARED
        bindings/c/fetch.cpp
    )
    target_include_directories(roulin_fetch
        PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/bindings/c>
    )
    target_compile_features(roulin_fetch PUBLIC cxx_std_17)
    target_compile_definitions(roulin_fetch PRIVATE ROULIN_CORE_EXPORTS)
    if(APPLE)
        set_source_files_properties(bindings/c/fetch.cpp
            TARGET_DIRECTORY roulin_fetch
            PROPERTIES LANGUAGE OBJCXX COMPILE_FLAGS "-fobjc-arc")
    endif()
    target_link_libraries(roulin_fetch PRIVATE roulin-fetch-share roulin_core)
    if(APPLE)
        target_link_libraries(roulin_fetch PRIVATE roulin-fetch-apple)
    elseif(ANDROID)
        target_link_libraries(roulin_fetch PRIVATE roulin-fetch-android)
    else()
        target_link_libraries(roulin_fetch PRIVATE roulin-fetch-desktop)
    endif()
    set_target_properties(roulin_fetch PROPERTIES OUTPUT_NAME "roulin_fetch")
    if(WIN32)
        set_target_properties(roulin_fetch PROPERTIES PREFIX "")
    endif()
    if(ANDROID)
        target_link_options(roulin_fetch PRIVATE "LINKER:-z,max-page-size=16384")
    endif()
endif()

install(TARGETS roulin-fetch
    ARCHIVE DESTINATION lib
)
if(NOT IOS)
    install(TARGETS roulin_fetch
        LIBRARY DESTINATION lib
        ARCHIVE DESTINATION lib
        RUNTIME DESTINATION lib
    )
endif()
install(FILES bindings/c/fetch.h
    DESTINATION include/roulin
)

if(ROULIN_BUILD_TESTS)
    add_subdirectory(tests)
endif()
