package server

import (
	"net/http"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build/vcs"
)

type DiffResponse struct {
	Revision    string   `json:"revision"`
	Changed     []string `json:"changed"`
	Uncommitted []string `json:"uncommitted"`
}

func handleDiff(adapter vcs.VCSAdapter) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		since := r.URL.Query().Get("since")
		if since != "" && !revisionRe.MatchString(since) {
			writeErr(w, http.StatusBadRequest, "invalid_revision",
				"since must match [a-zA-Z0-9._-]+")
			return
		}

		rev, err := adapter.CurrentRevision()
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "vcs_revision", err.Error())
			return
		}

		var changed []string
		if since != "" {
			changed, err = adapter.ChangedFiles(since)
			if err != nil {
				writeErr(w, http.StatusUnprocessableEntity, "vcs_diff", err.Error())
				return
			}
		}

		uncommitted, err := adapter.UncommittedFiles()
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "vcs_status", err.Error())
			return
		}

		writeJSON(w, http.StatusOK, DiffResponse{
			Revision:    rev,
			Changed:     changed,
			Uncommitted: uncommitted,
		})
	}
}
