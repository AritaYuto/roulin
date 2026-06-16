#pragma once

// Errors share the same thread-local rln_last_error() channel as roulin.h.

#include "roulin.h"

#ifdef __cplusplus
extern "C" {
#endif

#include <stddef.h>
#include <stdint.h>

typedef struct rln_fetch_session rln_fetch_session;

// RLN_HTTP_AUTO: h2 via ALPN on https://, h2c prior-knowledge on http://.
typedef enum {
    RLN_HTTP_AUTO     = 0,
    RLN_HTTP_1_ONLY   = 1,
} rln_http_mode;

// max_parallel  : <= 0 → default 8.
// max_attempts  : initial + retries; <= 0 clamped to 1.
typedef struct {
    int          max_parallel;
    rln_http_mode http_mode;
    int          max_attempts;
} rln_fetch_config;

// cfg may be NULL. NULL return = failure (see rln_last_error).
ROULIN_API rln_fetch_session* rln_fetch_session_new(const rln_fetch_config* cfg);

// Stops the I/O thread and drops in-flight handles. Safe with NULL.
ROULIN_API void rln_fetch_session_free(rln_fetch_session* session);

// expected_hash : 32-byte BLAKE3, or NULL to skip verification.
// Returns 0 on failure (see rln_last_error).
ROULIN_API uint64_t rln_fetch_enqueue(rln_fetch_session* session,
                                       const char*       url,
                                       const uint8_t*    expected_hash);

// Return:
//    0 = in-progress (out_bytes_done / out_bytes_total valid)
//    1 = completed   (out_buf / out_len / out_http_version populated; caller
//                     owns out_buf, free via rln_fetch_free_buf)
//   -1 = failed or cancelled (see rln_last_error)
// On 1 / -1 the handle is consumed; a subsequent poll returns -1 with
// "invalid_handle".
// out_http_version: 1 = HTTP/1.0, 2 = HTTP/1.1, 3 = HTTP/2, 30 = HTTP/3.
// Any out_ may be NULL to skip.
ROULIN_API int rln_fetch_poll(rln_fetch_session* session,
                               uint64_t          handle,
                               uint64_t*         out_bytes_done,
                               uint64_t*         out_bytes_total,
                               uint8_t**         out_buf,
                               size_t*           out_len,
                               int*              out_http_version);

// Next rln_fetch_poll returns -1 with rln_last_error() == "cancelled".
ROULIN_API void rln_fetch_cancel(rln_fetch_session* session, uint64_t handle);

// Safe with NULL.
ROULIN_API void rln_fetch_free_buf(uint8_t* buf);

#ifdef __cplusplus
} /* extern "C" */
#endif
