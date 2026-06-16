#pragma once

// Internal-only header shared across roulin C ABI implementations.
// Not installed; not part of the public surface.
//
// The C ABI exposes a single rln_last_error() entry point that returns the most
// recent failure message on the calling thread. To keep that contract intact
// when failures originate from libraries beyond core (e.g. roulin-fetch), all
// implementations must write into the same thread_local string. This header
// declares that storage and the helpers used to populate it.
//
// Definitions live in core/bindings/c/roulin.cpp. Other translation units that
// include this header must link against roulin-core (or its SHARED variant) to
// satisfy the references.

#include <exception>
#include <string>

namespace roulin::error {

// Shared thread-local error buffer. rln_last_error() reads this directly.
extern thread_local std::string tl_error;

// Clears tl_error. Call at the start of every C entry point.
void clearError() noexcept;

// Sets tl_error to "{prefix}: {e.what()}", with fall-back to {prefix} on OOM.
void setError(const char* prefix, const std::exception& e) noexcept;

// Sets tl_error to "{prefix}: {detail}", with fall-back to {prefix} on OOM.
void setError(const char* prefix, const char* detail) noexcept;

} // namespace roulin::error
