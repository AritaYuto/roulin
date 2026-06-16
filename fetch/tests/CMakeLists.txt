enable_testing()

add_executable(fetch-pipeline-test
    fetch_pipeline_test.cpp
)

target_link_libraries(fetch-pipeline-test PRIVATE roulin-fetch)
