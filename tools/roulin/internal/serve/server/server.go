// Package server provides the HTTP/2 asset server.
package server

import (
	"fmt"
	"net/http"
	"time"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build/vcs"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
	"golang.org/x/net/http2"
	"golang.org/x/net/http2/h2c"
)

// Writer enables the build-time POST endpoints. Pass nil to New for read-only mode.
type Writer struct {
	Storage          storage.Storage  // canonical object store (durable, CDN-visible)
	HotReloadStorage storage.Storage  // transient cache-tier; hot-reload bytes never reach cloud
	Broadcaster      *sse.Broadcaster // notified on POST /parcels; nil disables SSE
}

// New creates an h2c HTTP server. Pass nil writer for read-only mode.
func New(rdStorage storage.Storage, writer *Writer, vcsAdapter vcs.VCSAdapter, port int) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /index/{revision}", handleIndex(rdStorage))
	mux.HandleFunc("GET /blobs/{prefix}/{hash}", handleBlob(rdStorage))
	mux.HandleFunc("GET /health", handleHealth())

	if vcsAdapter != nil {
		mux.HandleFunc("GET /diff", handleDiff(rdStorage, vcsAdapter))
		mux.HandleFunc("GET /uncommitted", handleUncommitted(vcsAdapter))
	}

	if writer != nil {
		mux.HandleFunc("POST /blobs", handlePostBlob(writer.Storage))
		mux.HandleFunc("HEAD /blobs/{prefix}/{hash}", handleHeadBlob(writer.Storage))
		mux.HandleFunc("POST /parcels/{revision}", handlePostParcel(writer.Storage))

		if writer.HotReloadStorage != nil {
			mux.HandleFunc("POST /hot/blobs", handlePostBlob(writer.HotReloadStorage))
		}

		if writer.Broadcaster != nil {
			mux.HandleFunc("GET /watch/changes", handleWatchChanges(writer.Broadcaster))
			mux.HandleFunc("POST /patches", handlePostPatches(writer.Broadcaster))
		}
	}

	return &http.Server{
		Addr:              fmt.Sprintf(":%d", port),
		Handler:           h2c.NewHandler(logMiddleware(mux), &http2.Server{}),
		ReadHeaderTimeout: 10 * time.Second, // Slowloris defense; covers headers only, not large bodies
		// WriteTimeout intentionally unset: it would terminate SSE hot-reload streams.
		IdleTimeout: 120 * time.Second,
	}
}
