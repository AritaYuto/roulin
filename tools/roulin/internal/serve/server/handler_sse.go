package server

import (
	"fmt"
	"log/slog"
	"net/http"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
)

// handleWatchChanges streams SSE to the client; each event carries a newly-posted revision.
func handleWatchChanges(b *sse.Broadcaster) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		flusher, ok := w.(http.Flusher)
		if !ok {
			writeErr(w, http.StatusInternalServerError, "streaming_unsupported",
				"http.Flusher not supported by this ResponseWriter")
			return
		}
		w.Header().Set("Content-Type", "text/event-stream")
		w.Header().Set("Cache-Control", "no-cache")
		w.Header().Set("Connection", "keep-alive")
		// Reconnect hint for EventSource-style clients on transport drop.
		fmt.Fprint(w, "retry: 2000\n\n")
		flusher.Flush()

		ch := b.Subscribe()
		defer b.Unsubscribe(ch)

		ctx := r.Context()
		slog.Info("sse client connected", "remote", r.RemoteAddr, "total", b.ClientCount())
		defer slog.Info("sse client disconnected", "remote", r.RemoteAddr)

		for {
			select {
			case <-ctx.Done():
				return
			case payload, ok := <-ch:
				if !ok {
					return
				}
				if _, err := fmt.Fprintf(w, "data: %s\n\n", payload); err != nil {
					return
				}
				flusher.Flush()
			}
		}
	}
}
