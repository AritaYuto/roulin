package server

import (
	"context"
	"net/http"
	"sort"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build/vcs"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

type DiffResponse struct {
	BaseRevision    string   `json:"base_revision"`
	HeadRevision    string   `json:"head_revision"`
	Changed         []string `json:"changed"`
	Uncommitted     []string `json:"uncommitted"`
	BaseBundleNames []string `json:"base_bundle_names"`
}

func handleDiff(s storage.Storage, adapter vcs.VCSAdapter) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		head, err := adapter.CurrentRevision()
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "vcs_revision", err.Error())
			return
		}

		base, err := latestStoredRevision(r.Context(), s)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, "list_indexes", err.Error())
			return
		}

		var changed []string
		if base != "" && base != head {
			changed, err = adapter.ChangedFiles(base)
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

		var baseBundleNames []string
		if base != "" {
			baseBundleNames, err = loadIndexBundleNames(r.Context(), s, base)
			if err != nil {
				writeErr(w, http.StatusInternalServerError, "read_base_index", err.Error())
				return
			}
		}

		writeJSON(w, http.StatusOK, DiffResponse{
			BaseRevision:    base,
			HeadRevision:    head,
			Changed:         changed,
			Uncommitted:     uncommitted,
			BaseBundleNames: baseBundleNames,
		})
	}
}

func loadIndexBundleNames(ctx context.Context, s storage.Storage, rev string) ([]string, error) {
	buf, err := s.GetIndex(ctx, rev)
	if err != nil {
		return nil, err
	}
	entries, _ := build.ParseIndexBytes(buf)
	names := make([]string, 0, len(entries))
	for _, entry := range entries {
		if entry.Name != "" {
			names = append(names, entry.Name)
		}
	}
	return names, nil
}

// Backends don't guarantee list order (file = name, S3 = lex key), so sort by
// LastModified here.
func latestStoredRevision(ctx context.Context, s storage.Storage) (string, error) {
	infos, err := s.ListIndexRevisions(ctx)
	if err != nil {
		return "", err
	}
	if len(infos) == 0 {
		return "", nil
	}
	sort.Slice(infos, func(i, j int) bool {
		return infos[i].LastModified.After(infos[j].LastModified)
	})
	return infos[0].Revision, nil
}
