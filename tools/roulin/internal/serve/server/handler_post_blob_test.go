package server

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	"lukechampine.com/blake3"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// setupWritableServer spins up a server backed by a fresh temp dir,
// configured with both read source and write capability. Shared by the
// POST /blob and POST /parcel tests.
func setupWritableServer(t *testing.T) (*httptest.Server, string) {
	t.Helper()
	dir := t.TempDir()
	store := local.NewFile(dir)
	srv := New(store, &Writer{Storage: store}, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)
	return ts, dir
}

func TestPostBlob_RoundtripAndIdempotent(t *testing.T) {
	ts, _ := setupWritableServer(t)
	payload := []byte("hello, blob")

	hash := postBlob(t, ts, payload)
	expected := blake3.Sum256(payload)
	if hash != hex.EncodeToString(expected[:]) {
		t.Fatalf("hash mismatch: got %s want %s",
			hash, hex.EncodeToString(expected[:]))
	}

	got := getBlob(t, ts, hash)
	if !bytes.Equal(got, payload) {
		t.Fatalf("body mismatch: got %q want %q", got, payload)
	}

	hash2 := postBlob(t, ts, payload)
	if hash2 != hash {
		t.Fatalf("idempotent POST returned different hash: %s vs %s", hash2, hash)
	}
}

func TestPostBlob_ReadOnlyServer(t *testing.T) {
	dir := t.TempDir()
	srv := New(local.NewFile(dir), nil, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	resp, err := http.Post(ts.URL+"/blobs", "application/octet-stream",
		bytes.NewReader([]byte("x")))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	// With no writer the POST route is never registered, so ServeMux
	// returns 404 — read-only deployments do not advertise the build API.
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("read-only POST: got %d, want 404", resp.StatusCode)
	}
}

// postBlob / getBlob are shared with handler_post_parcel_test.go which
// uploads bundle payloads as a prerequisite before POSTing a parcel.
func postBlob(t *testing.T, ts *httptest.Server, body []byte) string {
	t.Helper()
	resp, err := http.Post(ts.URL+"/blobs", "application/octet-stream",
		bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("POST /blobs: got %d (%s)", resp.StatusCode, raw)
	}
	var parsed struct{ Hash string }
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		t.Fatal(err)
	}
	return parsed.Hash
}

func getBlob(t *testing.T, ts *httptest.Server, hash string) []byte {
	t.Helper()
	url := ts.URL + "/blobs/" + hash[:2] + "/" + hash
	resp, err := http.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("GET %s: got %d", url, resp.StatusCode)
	}
	got, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	return got
}
