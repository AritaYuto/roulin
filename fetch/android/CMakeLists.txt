add_library(roulin-fetch-android STATIC
    src/android_fetcher.cpp
    src/android_handle_consumer.cpp
    src/android_error_classifier.cpp
)

target_include_directories(roulin-fetch-android
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:include>
)

target_compile_features(roulin-fetch-android PUBLIC cxx_std_17)

target_link_libraries(roulin-fetch-android
    PUBLIC  roulin-fetch-share
)

if(ANDROID)
    target_link_libraries(roulin-fetch-android PRIVATE log)
endif()

install(TARGETS roulin-fetch-android
    ARCHIVE DESTINATION lib
)
install(FILES include/roulin/fetch/android_fetcher.h
    DESTINATION include/roulin/fetch
)
