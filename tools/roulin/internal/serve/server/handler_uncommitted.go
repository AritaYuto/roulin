package server

import (
	"net/http"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build/vcs"
)

type UncommittedResponse struct {
	Uncommitted []string `json:"uncommitted"`
}

func handleUncommitted(adapter vcs.VCSAdapter) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		uncommitted, err := adapter.UncommittedFiles()
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "vcs_status", err.Error())
			return
		}
		writeJSON(w, http.StatusOK, UncommittedResponse{Uncommitted: uncommitted})
	}
}
