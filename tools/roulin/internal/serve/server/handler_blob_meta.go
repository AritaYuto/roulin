package server

import (
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"os"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// maxBlobMetaUploadSize caps POST /blobs_meta. JSON wire format inflates ~2-3x over FlatBuffers.
const maxBlobMetaUploadSize = 256 << 20 // 256 MiB

// handleGetBlobMeta reads the FlatBuffers sidecar and returns it as JSON.
func handleGetBlobMeta(s storage.Storage) http.HandlerFunc {
	return func(rw http.ResponseWriter, r *http.Request) {
		prefix := r.PathValue("prefix")
		hash := r.PathValue("hash")
		if !validateBlobHash(rw, prefix, hash) {
			return
		}

		buf, err := s.GetBlobMeta(r.Context(), hash)
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				writeErr(rw, http.StatusNotFound, "blob_meta_not_found",
					"blob_meta for "+hash+" not found")
				return
			}
			writeErr(rw, http.StatusInternalServerError, "blob_meta_read", err.Error())
			return
		}

		writeJSON(rw, http.StatusOK, build.ParseBlobMetaBytes(buf))
	}
}

// handlePostBlobMeta accepts the sidecar as JSON, encodes to FlatBuffers, and writes via Storage.
// URL hash is canonical — overrides whatever blob_hash the body contains.
func handlePostBlobMeta(s storage.Storage) http.HandlerFunc {
	return func(rw http.ResponseWriter, r *http.Request) {
		prefix := r.PathValue("prefix")
		hash := r.PathValue("hash")
		if !validateBlobHash(rw, prefix, hash) {
			return
		}

		body := http.MaxBytesReader(rw, r.Body, maxBlobMetaUploadSize)
		defer body.Close()

		var m build.BlobMeta
		if err := json.NewDecoder(body).Decode(&m); err != nil {
			// Log content_length so "exceeded cap" is distinguishable from "malformed JSON".
			slog.Warn("blob_meta decode failed",
				"hash", hash,
				"content_length", r.ContentLength,
				"max_bytes", maxBlobMetaUploadSize,
				"err", err.Error(),
			)
			writeErr(rw, http.StatusBadRequest, "decode_blob_meta", err.Error())
			return
		}
		// URL hash is canonical — body's blob_hash is informational only.
		m.BlobHash = hash

		buf := build.BuildBlobMetaBytes(&m)
		if err := s.PutBlobMeta(r.Context(), hash, buf); err != nil {
			slog.Error("storage.PutBlobMeta failed", "hash", hash, "err", err)
			writeErr(rw, http.StatusInternalServerError, "save_blob_meta", err.Error())
			return
		}

		assetCount := 0
		if m.UnityBody != nil {
			assetCount = len(m.UnityBody.Assets)
		}
		slog.Info("blob_meta saved",
			"hash", hash,
			"body_type", m.BodyType,
			"assets", assetCount,
		)
		writeJSON(rw, http.StatusCreated, PostBlobMetaResponse{
			Hash:     hash,
			BodyType: m.BodyType,
			Assets:   assetCount,
		})
	}
}

// PostBlobMetaResponse is returned by POST /blobs_meta/{prefix}/{hash}.
type PostBlobMetaResponse struct {
	Hash     string `json:"hash"`
	BodyType string `json:"body_type"`
	Assets   int    `json:"assets"`
}

// handleListBlobMetaHashes returns all blob_meta hashes in the canonical store.
func handleListBlobMetaHashes(s storage.Storage) http.HandlerFunc {
	return func(rw http.ResponseWriter, r *http.Request) {
		hashes, err := s.ListBlobMetaHashes(r.Context())
		if err != nil {
			slog.Error("storage.ListBlobMetaHashes failed", "err", err)
			writeErr(rw, http.StatusInternalServerError, "list_blob_metas", err.Error())
			return
		}
		if hashes == nil {
			hashes = []string{}
		}
		writeJSON(rw, http.StatusOK, ListBlobMetasResponse{Hashes: hashes})
	}
}

// ListBlobMetasResponse is returned by GET /blobs_meta/.
type ListBlobMetasResponse struct {
	Hashes []string `json:"hashes"`
}
