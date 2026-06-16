add_executable(roulin-fetch-share-tests
    session_test.cpp
    hashed_writer_test.cpp
)

target_link_libraries(roulin-fetch-share-tests
    PRIVATE roulin-fetch-share
            GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(roulin-fetch-share-tests)
