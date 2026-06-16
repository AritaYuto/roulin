package server

import (
	"encoding/hex"
	"io"
	"log/slog"
	"net/http"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
	"lukechampine.com/blake3"
)

const maxBlobUploadSize = 512 << 20 // 512 MiB; covers the largest expected AssetBundle

// handlePostBlob stores an opaque byte stream and returns its BLAKE3 hex hash.
func handlePostBlob(s storage.Storage) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		body := http.MaxBytesReader(w, r.Body, maxBlobUploadSize)
		defer body.Close()

		data, err := io.ReadAll(body)
		if err != nil {
			writeErr(w, http.StatusBadRequest, "read_body", err.Error())
			return
		}

		sum := blake3.Sum256(data)
		hashHex := hex.EncodeToString(sum[:])

		if err := s.PutBlob(r.Context(), hashHex, data); err != nil {
			slog.Error("storage.PutBlob failed", "hash", hashHex, "err", err)
			writeErr(w, http.StatusInternalServerError, "put_blob", err.Error())
			return
		}

		writeJSON(w, http.StatusOK, PostBlobResponse{Hash: hashHex})
	}
}
