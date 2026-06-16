package server

import (
	"errors"
	"net/http"
	"os"
	"regexp"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

var revisionRe = regexp.MustCompile(`^[a-zA-Z0-9._-]+$`)

func handleIndex(s storage.Storage) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		revision := r.PathValue("revision")
		if !revisionRe.MatchString(revision) {
			writeErr(w, http.StatusBadRequest, "invalid_revision",
				"revision must match [a-zA-Z0-9._-]+")
			return
		}

		data, err := s.GetIndex(r.Context(), revision)
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				writeErr(w, http.StatusNotFound, "index_not_found",
					"index for revision "+revision+" not found")
				return
			}
			writeErr(w, http.StatusInternalServerError, "index_read", err.Error())
			return
		}

		w.Header().Set("Content-Type", "application/octet-stream")
		w.Write(data)
	}
}
