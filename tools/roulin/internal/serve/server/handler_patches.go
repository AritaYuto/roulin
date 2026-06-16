package server

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"strings"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
)

// PatchEvent is the wire shape posted by the Editor and broadcast verbatim to SSE subscribers.
type PatchEvent struct {
	Platform string         `json:"platform"`
	Changes  []PatchChange  `json:"changes"`
}

// PatchChange names one address to hot-reload. new_blob_hex must already be uploaded via POST /blobs.
type PatchChange struct {
	Address    string `json:"address"`
	NewBlobHex string `json:"new_blob_hex"`
}

// handlePostPatches broadcasts a hot-reload patch to all SSE subscribers. Nothing is persisted.
func handlePostPatches(b *sse.Broadcaster) http.HandlerFunc {
	return func(rw http.ResponseWriter, r *http.Request) {
		var ev PatchEvent
		if err := json.NewDecoder(r.Body).Decode(&ev); err != nil {
			writeErr(rw, http.StatusBadRequest, "decode_patch", err.Error())
			return
		}
		if strings.TrimSpace(ev.Platform) == "" {
			writeErr(rw, http.StatusBadRequest, "platform_required",
				"platform field is required")
			return
		}
		if len(ev.Changes) == 0 {
			writeErr(rw, http.StatusBadRequest, "changes_empty",
				"changes must contain at least one entry")
			return
		}
		for i, c := range ev.Changes {
			if strings.TrimSpace(c.Address) == "" {
				writeErr(rw, http.StatusBadRequest, "address_empty",
					fmt.Sprintf("changes[%d]: address empty", i))
				return
			}
			if _, err := hex.DecodeString(c.NewBlobHex); err != nil || len(c.NewBlobHex) != 64 {
				writeErr(rw, http.StatusBadRequest, "invalid_blob_hex",
					fmt.Sprintf("changes[%d]: new_blob_hex must be 64 hex chars", i))
				return
			}
		}

		payload, err := json.Marshal(ev)
		if err != nil {
			writeErr(rw, http.StatusInternalServerError, "re_marshal", err.Error())
			return
		}
		b.Broadcast(payload)

		slog.Info("patch broadcast",
			"platform", ev.Platform,
			"changes", len(ev.Changes),
			"subscribers", b.ClientCount(),
		)

		writeJSON(rw, http.StatusOK, PostPatchesResponse{
			BroadcastSubscribers: b.ClientCount(),
			Changes:              len(ev.Changes),
		})
	}
}
