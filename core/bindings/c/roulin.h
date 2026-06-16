#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#include <stddef.h>
#include <stdint.h>

/*
 * Linkage decoration. Three states on Windows:
 *   ROULIN_CORE_STATIC   — consumer (or impl) of the static archive; no decoration
 *   ROULIN_CORE_EXPORTS  — building the SHARED roulin_core; dllexport
 *   (none)                — consumer of the SHARED roulin_core; dllimport
 */
#if defined(_WIN32) || defined(_WIN64)
  #if defined(ROULIN_CORE_STATIC)
    #define ROULIN_API
  #elif defined(ROULIN_CORE_EXPORTS)
    #define ROULIN_API __declspec(dllexport)
  #else
    #define ROULIN_API __declspec(dllimport)
  #endif
#elif __GNUC__ >= 4
  #define ROULIN_API __attribute__((visibility("default")))
#else
  #define ROULIN_API
#endif

/*
 * Roulin C FFI — complete interface for all engine integrations.
 *
 * Design principles:
 *   - All handles (ACParcel / ACBlob / ACStream) are opaque.
 *     Callers never touch internals.
 *   - FlatBuffers is a roulin-core implementation detail; callers are free
 *     from any FlatBuffers dependency.
 *   - Engine plugins wrap this FFI and expose language-native APIs
 *     (C# properties, UE Blueprint nodes, Godot GDExtension, etc.).
 *   - Thread safety: each handle must be used from one thread at a time.
 *     rln_last_error() is thread-local.
 */

/* Opaque handle types. */
typedef struct ACParcel     ACParcel;
typedef struct ACBlob       ACBlob;
typedef struct ACStream     ACStream;

/* ---- Error reporting ------------------------------------------------------- */

/*
 * Returns the error message from the most recent failing rln_* call on this
 * thread, or NULL if the last call succeeded.
 * The returned pointer is valid until the next rln_* call on the same thread.
 * Copy immediately if you need it beyond that point.
 */
ROULIN_API const char* rln_last_error(void);

/* ---- Hashing --------------------------------------------------------------- */

/*
 * Computes BLAKE3 over the given bytes, writing 32 bytes into out_hash.
 * Use this to verify a blob downloaded over HTTP matches its expected
 * content-addressed hash before storing it locally.
 */
ROULIN_API void rln_compute_blake3(const void* data, size_t len, uint8_t out_hash[32]);

/* ---- Parcel ---------------------------------------------------------------- */

/*
 * Opens the Parcel at local_dir/index/revision_id.
 * local_dir is the device-side blob cache root
 * (Application.persistentDataPath/roulin on Unity).
 * Returns NULL on failure — call rln_last_error() for details.
 */
ROULIN_API ACParcel* rln_parcel_open(const char* local_dir, const char* revision_id);
ROULIN_API void      rln_parcel_close(ACParcel* parcel);

/* ---- Blob ------------------------------------------------------------------ */

/*
 * Resolves the logical address to a blob handle.
 * Returns NULL if the address is not in the index.
 * The returned handle must be released with rln_blob_release().
 */
ROULIN_API ACBlob* rln_parcel_get(ACParcel* parcel, const char* address);
ROULIN_API void    rln_blob_release(ACBlob* blob);

/* Total byte size of the blob. Returns 0 on error. */
ROULIN_API size_t  rln_blob_size(ACBlob* blob);

/*
 * Reads up to len bytes from the blob starting at offset into buf.
 * Returns bytes read, or -1 on error.
 */
ROULIN_API int64_t rln_blob_read(ACBlob* blob, void* buf, size_t offset, size_t len);

/* ---- Stream (transparent encryption) --------------------------------------- */

/*
 * Opens a blob as a seekable stream with transparent ChaCha20 decryption.
 * Use with AssetBundle.LoadFromStream / equivalent.
 */
ROULIN_API ACStream* rln_blob_open_stream(ACBlob* blob);
ROULIN_API int       rln_stream_read_at(ACStream* s, void* buf, size_t offset, size_t len);
ROULIN_API void      rln_stream_close(ACStream* s);

/* ---- Bundle dependencies --------------------------------------------------- */

/*
 * Frees a const char** array previously returned by roulin-core
 * (rln_index_bundle_deps_for, ...).
 */
ROULIN_API void rln_strings_free(const char** strs, size_t count);

/*
 * Looks up the dependency list for the bundle whose binary BLAKE3 hash matches
 * bundle_hash. O(log n) binary search on the sorted bundle_deps table.
 * Returns NULL and sets *out_count=0 when the bundle has no recorded deps.
 * The returned array must be freed with rln_strings_free.
 *
 * Use this when the engine integration resolves deps lazily per bundle.
 * For bulk graph construction (e.g. Unity Addressables locator), prefer
 * rln_index_for_each_bundle_deps to avoid N binary searches.
 */
ROULIN_API const char** rln_index_bundle_deps_for(ACParcel* parcel,
                                                    const uint8_t bundle_hash[32],
                                                    size_t* out_count);

/*
 * Callback invoked for every bundle that has recorded dependencies.
 * bundle_hash: 32-byte BLAKE3 of the bundle binary (valid only during the callback)
 * deps:        array of hex BLAKE3 strings, one per dep bundle binary
 *              (valid only during the callback)
 * deps_count:  number of strings in deps[]
 * userdata:    value passed to rln_index_for_each_bundle_deps
 *
 * Walked in bundle_hash sort order (memcmp). Use this to bulk-build a
 * dep graph once at boot.
 */
typedef void (*ACBundleDepsFn)(const uint8_t      bundle_hash[32],
                                 const char* const* deps,
                                 size_t             deps_count,
                                 void*              userdata);

ROULIN_API void rln_index_for_each_bundle_deps(ACParcel*       parcel,
                                                  ACBundleDepsFn  fn,
                                                  void*           userdata);

/* ---- Diff / download planning ---------------------------------------------- */

typedef struct {
    const char* address;      /* logical address — valid until rln_diff_free */
    uint8_t     blob_hash[32];/* BLAKE3 hash → construct CDN URL */
} ACMissingBlob;

/*
 * Compares remote_parcel's Index against the local blob store at local_dir.
 * Stores the list of blobs that need downloading in *out_blobs.
 * The array must be freed with rln_diff_free.
 * Returns the count of missing blobs, or -1 on error.
 */
ROULIN_API int  rln_parcel_diff(ACParcel*        remote_parcel,
                                const char*      local_dir,
                                ACMissingBlob**  out_blobs,
                                size_t*          out_count);
ROULIN_API void rln_diff_free(ACMissingBlob* blobs, size_t count);

/* ---- Enumeration ----------------------------------------------------------- */

/*
 * Callback invoked for each addressable entry in the Parcel's Index.
 * address:           logical address string (valid only during the callback)
 * blob_hash:         32-byte BLAKE3 hash of the blob binary (valid only
 *                    during the callback). Multiple addresses can share the
 *                    same blob_hash when they live in the same blob.
 * blob_size:         size in bytes of the blob at blob_hash; 0 if not recorded
 *                    (powers Addressables.GetDownloadSizeAsync via ILocationSizeData)
 * labels:            array of label strings (valid only during the callback,
 *                    NULL if labels_count == 0)
 * labels_count:      number of strings in labels[]
 * asset_id:          Engine-native asset identifier string (Unity AssetGUID
 *                    32 hex, UE FGuid string, Godot UUID, ...). Valid only
 *                    during the callback. NULL when the entry has no id.
 * type_idxs:         array of indices into the Parcel's type table (see
 *                    rln_index_type_at). Engine-specific type identifiers
 *                    (e.g. Unity AssemblyQualifiedName). Valid only during
 *                    the callback. NULL if type_idxs_count == 0.
 * type_idxs_count:   number of indices in type_idxs[]
 * userdata:          value passed to rln_parcel_foreach
 */
typedef void (*ACForEachFn)(const char*        address,
                             const uint8_t*     blob_hash,
                             uint64_t           blob_size,
                             const char* const* labels,
                             size_t             labels_count,
                             const char*        asset_id,
                             const uint32_t*    type_idxs,
                             size_t             type_idxs_count,
                             void*              userdata);

ROULIN_API void rln_parcel_foreach(ACParcel* parcel, ACForEachFn fn, void* userdata);

/*
 * Returns the number of engine-specific type identifiers in the Parcel's
 * type table. type_idxs[] values inside ACForEachFn point into this table.
 */
ROULIN_API size_t      rln_index_types_count(ACParcel* parcel);

/*
 * Returns the type identifier string at the given index (e.g. Unity
 * AssemblyQualifiedName). Returns NULL if idx is out of range.
 * The returned pointer is valid for the lifetime of the Parcel.
 */
ROULIN_API const char* rln_index_type_at(ACParcel* parcel, size_t idx);

#ifdef __cplusplus
} /* extern "C" */
#endif
