package server

import (
	"context"
	"net/http"
	"sort"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build/vcs"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// DiffResponse is the payload Unity consumes to decide the incremental build
// base and the set of dirty paths.
//
// BaseRevision is the newest revision the server holds an Index for — Unity
// sends it back as parcel.base_revision so the server-side merge is applied
// against a known-existing catalog. Empty means "no prior publish exists";
// Unity falls back to full publish.
//
// HeadRevision is the engine repo's current HEAD, reported so the caller can
// cross-check against the parcel revision it will POST. Not used by the merge.
//
// Changed is the committed diff BaseRevision..HeadRevision. Nil when there is
// no base yet, or when base already equals HEAD (only worktree edits).
//
// BaseBundleNames is the flat list of bundle names present in the base
// revision's Index. Unity uses it to detect bundles that exist in HEAD's
// Addressables walk but did NOT exist at base — those must be forced into
// the "changed" set, otherwise SBP skips them and the server-side merge sees
// a name listed in all_bundle_names that is neither in the delta nor in base.
// Nil when there is no base yet.
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

// loadIndexBundleNames returns the flat list of bundle names present in the
// given revision's stored Index. Unity uses this to detect NEW bundles that
// appear in HEAD's Addressables walk but not in base — SBP would otherwise
// silently skip them and the merge server would reject the parcel.
func loadIndexBundleNames(ctx context.Context, s storage.Storage, rev string) ([]string, error) {
	buf, err := s.GetIndex(ctx, rev)
	if err != nil {
		return nil, err
	}
	entries, _ := build.ParseIndexBytes(buf)
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		if e.Name != "" {
			names = append(names, e.Name)
		}
	}
	return names, nil
}

// latestStoredRevision returns the most recently written Index revision by
// LastModified, or "" when none exist. ListIndexRevisions makes no ordering
// guarantee (file backend sorts by name, S3 by lex key), so we sort here.
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
