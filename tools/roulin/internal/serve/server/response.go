package server

import (
	"encoding/json"
	"log/slog"
	"net/http"
)

// ErrorResponse is the JSON shape returned by writeErr.
type ErrorResponse struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// PostBlobResponse is returned by POST /blobs on success.
type PostBlobResponse struct {
	Hash string `json:"hash"`
}

// PostParcelResponse is returned by POST /parcels/{revision} on success.
type PostParcelResponse struct {
	Revision string `json:"revision"`
	Bundles  int    `json:"bundles"`
}

// PostPatchesResponse is returned by POST /patches on success.
type PostPatchesResponse struct {
	BroadcastSubscribers int `json:"broadcast_subscribers"`
	Changes              int `json:"changes"`
}

// writeJSON writes status with body marshalled as application/json.
func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(body); err != nil {
		// Headers are already flushed; log and continue.
		slog.Warn("response encode failed", "err", err)
	}
}

// writeErr writes status with an ErrorResponse{Code, Message} payload.
func writeErr(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, ErrorResponse{Code: code, Message: message})
}
