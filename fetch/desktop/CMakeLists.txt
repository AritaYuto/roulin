if(WIN32)
    set(DESKTOP_BACKEND_SOURCES
        src/winhttp/winhttp_backend.cpp
        src/winhttp/winhttp_attempt.cpp
        src/winhttp/winhttp_handle_consumer.cpp
        src/winhttp/winhttp_error_classifier.cpp
    )
    set(DESKTOP_BACKEND_LIBS winhttp)
else()
    find_package(CURL REQUIRED)
    set(DESKTOP_BACKEND_SOURCES
        src/libcurl/libcurl_backend.cpp
        src/libcurl/libcurl_config_builder.cpp
        src/libcurl/libcurl_handle_consumer.cpp
        src/libcurl/libcurl_error_classifier.cpp
    )
    set(DESKTOP_BACKEND_LIBS CURL::libcurl)
endif()

add_library(roulin-fetch-desktop STATIC
    src/common/desktop_backend.cpp
    src/common/desktop_fetcher.cpp
    src/common/worker.cpp
    src/common/worker_inbox.cpp
    ${DESKTOP_BACKEND_SOURCES}
)

target_include_directories(roulin-fetch-desktop
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:include>
)

target_compile_features(roulin-fetch-desktop PUBLIC cxx_std_17)

target_link_libraries(roulin-fetch-desktop
    PUBLIC  roulin-fetch-share
    PRIVATE ${DESKTOP_BACKEND_LIBS}
)

install(TARGETS roulin-fetch-desktop
    ARCHIVE DESTINATION lib
)
install(FILES include/roulin/fetch/desktop_fetcher.h
    DESTINATION include/roulin/fetch
)
