# .mm sources need OBJCXX; root project only enables C/CXX.
enable_language(OBJC)
enable_language(OBJCXX)

add_library(roulin-fetch-apple STATIC
    src/apple_fetcher.mm
    src/apple_handle_consumer.mm
    src/apple_error_classifier.mm
)

target_include_directories(roulin-fetch-apple
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:include>
)

target_compile_features(roulin-fetch-apple PUBLIC cxx_std_17)

target_link_libraries(roulin-fetch-apple
    PUBLIC  roulin-fetch-share
            roulin-core
    PRIVATE "-framework Foundation"
)

target_compile_options(roulin-fetch-apple PRIVATE -fobjc-arc)

install(TARGETS roulin-fetch-apple
    ARCHIVE DESTINATION lib
)
install(FILES include/roulin/fetch/apple_fetcher.h
    DESTINATION include/roulin/fetch
)
