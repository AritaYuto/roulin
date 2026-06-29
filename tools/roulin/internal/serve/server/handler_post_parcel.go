package server

import (
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// handlePostParcel decodes a JSON Parcel and writes the FlatBuffers Index for
// the revision. Referenced blobs must already be uploaded via POST /blobs.
//
// Two modes (see Parcel doc):
//   - Full publish: Parcel.Bundles[] covers every bundle in the new revision.
//   - Incremental: Parcel.BaseRevision + Parcel.AllBundleNames are set, and
//     Bundles[] holds only the bundles this build regenerated. The server
//     merges the delta with the base revision's stored Index.
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

		var idxBytes []byte
		mode := "full"
		if p.BaseRevision != "" {
			if !revisionRe.MatchString(p.BaseRevision) {
				writeErr(rw, http.StatusBadRequest, "invalid_base_revision",
					"base_revision must match [a-zA-Z0-9._-]+")
				return
			}
			basis, err := s.GetIndex(r.Context(), p.BaseRevision)
			if err != nil {
				if errors.Is(err, os.ErrNotExist) {
					writeErr(rw, http.StatusBadRequest, "base_not_found",
						fmt.Sprintf("base_revision %q has no stored Index", p.BaseRevision))
					return
				}
				writeErr(rw, http.StatusInternalServerError, "get_base_index", err.Error())
				return
			}
			merged, err := build.MergeParcel(&p, basis)
			if err != nil {
				writeErr(rw, http.StatusBadRequest, "merge_parcel", err.Error())
				return
			}
			idxBytes = merged
			mode = "incremental"
		} else {
			full, err := build.BuildIndexFromParcel(&p)
			if err != nil {
				writeErr(rw, http.StatusBadRequest, "invalid_blob_hash", err.Error())
				return
			}
			idxBytes = full
		}

		if err := s.PutIndex(r.Context(), revision, idxBytes); err != nil {
			slog.Error("storage.PutIndex failed", "rev", revision, "err", err)
			writeErr(rw, http.StatusInternalServerError, "put_index", err.Error())
			return
		}

		slog.Info("parcel saved",
			"rev", revision,
			"mode", mode,
			"base_revision", p.BaseRevision,
			"delta_bundles", len(p.Bundles),
			"total_bundle_names", len(p.AllBundleNames),
			"index_bytes", len(idxBytes),
		)
		writeJSON(rw, http.StatusCreated, PostParcelResponse{
			Revision: revision,
			Bundles:  len(p.Bundles),
		})
	}
}
