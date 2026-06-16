enable_testing()

# ---- Unit / integration tests (GoogleTest) ---------------------------------
add_executable(roulin-core-tests
    blob_store_test.cpp
    index_test.cpp
    storage_test.cpp
    diff_test.cpp
)

target_link_libraries(roulin-core-tests
    PRIVATE roulin-core
            GTest::gtest_main
            GTest::gmock
)

include(GoogleTest)
gtest_discover_tests(roulin-core-tests)

# ---- Pipeline test (standalone, used by tools-dev/pipeline-verify) ---------
# Verifies that a parcel written by the Go roulin-cli (FlatBuffers + blobs)
# can be opened and read by the C FFI (ac_parcel_open / ac_blob_read).
add_executable(pipeline-test
    pipeline_test.cpp
)

target_link_libraries(pipeline-test PRIVATE roulin-core)
