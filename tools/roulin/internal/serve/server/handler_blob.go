package server

import (
	"errors"
	"net/http"
	"os"
	"regexp"
	"strconv"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

var (
	prefixRe = regexp.MustCompile(`^[0-9a-f]{2}$`)
	hashRe   = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

func validateBlobHash(rw http.ResponseWriter, prefix, hash string) bool {
	if !prefixRe.MatchString(prefix) || !hashRe.MatchString(hash) {
		writeErr(rw, http.StatusBadRequest, "invalid_hash",
			"blob hash must be 64 lower-hex chars and prefix must be 2 lower-hex chars")
		return false
	}
	if hash[:2] != prefix {
		writeErr(rw, http.StatusBadRequest, "prefix_mismatch",
			"path prefix does not match the first 2 chars of the hash")
		return false
	}
	return true
}

func handleHeadBlob(s storage.Storage) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		prefix := r.PathValue("prefix")
		hash := r.PathValue("hash")
		if !validateBlobHash(w, prefix, hash) {
			return
		}
		ok, err := s.HasBlob(r.Context(), hash)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "has_blob", err.Error())
			return
		}
		if ok {
			w.WriteHeader(http.StatusOK)
		} else {
			w.WriteHeader(http.StatusNotFound)
		}
	}
}

func handleBlob(s storage.Storage) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		prefix := r.PathValue("prefix")
		hash := r.PathValue("hash")

		if !validateBlobHash(w, prefix, hash) {
			return
		}

		data, err := s.GetBlob(r.Context(), hash)
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				writeErr(w, http.StatusNotFound, "blob_not_found", "blob "+hash+" not found")
				return
			}
			writeErr(w, http.StatusInternalServerError, "blob_read", err.Error())
			return
		}

		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Length", strconv.Itoa(len(data)))
		w.Write(data)
	}
}

func handleHealth() http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.Write([]byte("ok"))
	}
}
