add_library(roulin-fetch-share STATIC
    src/session.cpp
    src/hashed_writer.cpp
)

target_include_directories(roulin-fetch-share
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:include>
)

target_compile_features(roulin-fetch-share PUBLIC cxx_std_17)

target_link_libraries(roulin-fetch-share
    PUBLIC roulin-core
)

install(TARGETS roulin-fetch-share
    ARCHIVE DESTINATION lib
)
install(FILES include/roulin/fetch/session.h
              include/roulin/fetch/fetcher.h
              include/roulin/fetch/entry_registry.h
              include/roulin/fetch/error_classifier.h
              include/roulin/fetch/handle_consumer_base.h
    DESTINATION include/roulin/fetch
)

if(ROULIN_BUILD_TESTS)
    add_subdirectory(tests)
endif()
