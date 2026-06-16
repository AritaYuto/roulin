package server

import (
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// handlePostParcel decodes a JSON Parcel and writes the FlatBuffers Index for the revision.
// Referenced blobs must already be uploaded via POST /blobs.
func handlePostParcel(s storage.Storage) http.HandlerFunc {
	return func(rw http.ResponseWriter, r *http.Request) {
		revision := r.PathValue("revision")
		if !revisionRe.MatchString(revision) {
			writeErr(rw, http.StatusBadRequest, "invalid_revision",
				"revision must match [a-zA-Z0-9._-]+")
			return
		}

		var p build.Parcel
		if err := json.NewDecoder(r.Body).Decode(&p); err != nil {
			writeErr(rw, http.StatusBadRequest, "decode_parcel", err.Error())
			return
		}

		for _, b := range p.Bundles {
			ok, err := s.HasBlob(r.Context(), b.BlobHash)
			if err != nil {
				writeErr(rw, http.StatusInternalServerError, "has_blob", err.Error())
				return
			}
			if !ok {
				writeErr(rw, http.StatusBadRequest, "blob_not_uploaded",
					fmt.Sprintf("bundle %q: blob %s not uploaded", b.Address, b.BlobHash))
				return
			}
		}

		idxBytes, err := build.BuildIndexFromParcel(&p)
		if err != nil {
			writeErr(rw, http.StatusBadRequest, "invalid_blob_hash", err.Error())
			return
		}
		if err := s.PutIndex(r.Context(), revision, idxBytes); err != nil {
			slog.Error("storage.PutIndex failed", "rev", revision, "err", err)
			writeErr(rw, http.StatusInternalServerError, "put_index", err.Error())
			return
		}

		slog.Info("parcel saved",
			"rev", revision,
			"bundles", len(p.Bundles),
			"index_bytes", len(idxBytes),
		)
		writeJSON(rw, http.StatusCreated, PostParcelResponse{
			Revision: revision,
			Bundles:  len(p.Bundles),
		})
	}
}
