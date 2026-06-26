package roulin_test

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"lukechampine.com/blake3"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/server"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// setupWritableServer spins up a server backed by a fresh temp dir,
// configured with both read source and write capability.
func setupWritableServer(t *testing.T) (*httptest.Server, string) {
	t.Helper()
	dir := t.TempDir()
	st := local.NewFile(dir)
	srv := server.New(st, &server.Writer{Storage: st}, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)
	return ts, dir
}

// ---- POST /blobs ----------------------------------------------------------

func TestPostBlob_RoundtripAndIdempotent(t *testing.T) {
	ts, _ := setupWritableServer(t)
	payload := []byte("hello, blob")

	// First POST: stores and returns the BLAKE3 hash.
	hash := postBlob(t, ts, payload)
	expected := blake3.Sum256(payload)
	if hash != hex.EncodeToString(expected[:]) {
		t.Fatalf("hash mismatch: got %s want %s",
			hash, hex.EncodeToString(expected[:]))
	}

	// GET round-trips identical bytes via the existing read path.
	got := getBlob(t, ts, hash)
	if !bytes.Equal(got, payload) {
		t.Fatalf("body mismatch: got %q want %q", got, payload)
	}

	// Second POST is a no-op (same hash, no error).
	hash2 := postBlob(t, ts, payload)
	if hash2 != hash {
		t.Fatalf("idempotent POST returned different hash: %s vs %s", hash2, hash)
	}
}

func TestPostBlob_ReadOnlyServer(t *testing.T) {
	dir := t.TempDir()
	srv := server.New(local.NewFile(dir), nil /* no writer */, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	res, err := http.Post(ts.URL+"/blobs", "application/octet-stream",
		bytes.NewReader([]byte("x")))
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	// With no writer the POST route is never registered, so ServeMux
	// returns 404 — read-only deployments do not advertise the build API.
	if res.StatusCode != http.StatusNotFound {
		t.Fatalf("read-only POST: got %d, want 404", res.StatusCode)
	}
}

// ---- POST /parcels/{revision} ---------------------------------------------

func TestPostParcel_HappyPath(t *testing.T) {
	ts, baseDir := setupWritableServer(t)

	// Upload two bundle payloads first.
	bundleA := []byte("bundle-A bytes")
	bundleB := []byte("bundle-B bytes")
	hashA := postBlob(t, ts, bundleA)
	hashB := postBlob(t, ts, bundleB)

	p := build.Parcel{
		Bundles: []build.Bundle{
			{
				Address:  "ui/icons",
				BlobHash: hashA,
				Entries: []build.Entry{
					{Address: "ui/icons/player"},
					{Address: "ui/icons/enemy"},
				},
				// Dependencies are hex BLAKE3 of dep bundle binaries.
				Dependencies: []string{hashB},
			},
			{
				Address:  "shared",
				BlobHash: hashB,
				Entries:  []build.Entry{{Address: "shared/atlas"}},
			},
		},
	}

	postParcel(t, ts, "rev-001", &p, http.StatusCreated)

	// Index has 1 IndexEntry per blob, addresses nested inside.
	idxBytes, err := os.ReadFile(filepath.Join(baseDir, "index", "rev-001"))
	if err != nil {
		t.Fatalf("read index: %v", err)
	}
	entries, _ := build.ParseIndexBytes(idxBytes)

	if len(entries) != 2 {
		t.Fatalf("IndexEntries: got %d want 2", len(entries))
	}

	// Collect every address across both blobs and the deps map.
	addrs := map[string]bool{}
	depsByHash := map[string][]string{}
	for _, e := range entries {
		for _, a := range e.Addresses {
			addrs[a.AddressStr] = true
		}
		depsByHash[hex.EncodeToString(e.BlobHash[:])] = e.Deps
	}
	for _, want := range []string{"ui/icons/player", "ui/icons/enemy", "shared/atlas"} {
		if !addrs[want] {
			t.Errorf("addresses missing %q", want)
		}
	}

	// Bundle A (= ui/icons) has one dep on bundle B's hash; bundle B has none.
	if got := depsByHash[hashA]; len(got) != 1 || got[0] != hashB {
		t.Errorf("bundle %s deps: got %v want [%s]", hashA, got, hashB)
	}
	if got := depsByHash[hashB]; len(got) != 0 {
		t.Errorf("bundle %s deps: got %v want []", hashB, got)
	}
}

// TestPostParcel_PropagatesSize verifies that Parcel.Bundle.SizeBytes is
// preserved through the wire: server writes it onto every IndexEntry within
// the bundle, and the root entry's BlobSize equals the BundleIndex blob's
// own length. Powers Addressables.GetDownloadSizeAsync via ILocationSizeData.
func TestPostParcel_PropagatesSize(t *testing.T) {
	ts, baseDir := setupWritableServer(t)

	bundleBytes := []byte("bundle-A bytes that have a known length")
	bundleSize := uint64(len(bundleBytes))
	hashA := postBlob(t, ts, bundleBytes)

	p := build.Parcel{
		Bundles: []build.Bundle{{
			Address:   "ui/icons",
			BlobHash:  hashA,
			SizeBytes: bundleSize,
			Entries: []build.Entry{
				{Address: "ui/icons/player"},
				{Address: "ui/icons/enemy"},
			},
		}},
	}
	postParcel(t, ts, "rev-size", &p, http.StatusCreated)

	// Bundle A is the only blob, with 2 addresses living inside.
	idxBytes, err := os.ReadFile(filepath.Join(baseDir, "index", "rev-size"))
	if err != nil {
		t.Fatalf("read index: %v", err)
	}
	entries, _ := build.ParseIndexBytes(idxBytes)

	if len(entries) != 1 {
		t.Fatalf("IndexEntries: got %d want 1", len(entries))
	}
	e := entries[0]
	if e.SizeBytes != bundleSize {
		t.Errorf("SizeBytes: got %d want %d", e.SizeBytes, bundleSize)
	}
	if hex.EncodeToString(e.BlobHash[:]) != hashA {
		t.Errorf("BlobHash: got %x want %s", e.BlobHash, hashA)
	}
	if len(e.Addresses) != 2 {
		t.Errorf("Addresses: got %d want 2", len(e.Addresses))
	}
}

func TestPostParcel_RejectsUnuploadedBlob(t *testing.T) {
	ts, _ := setupWritableServer(t)

	bogus := strings.Repeat("ab", 32) // valid hex but no blob backing it
	p := build.Parcel{
		Bundles: []build.Bundle{{
			Address: "ui/icons", BlobHash: bogus,
			Entries: []build.Entry{{Address: "ui/icons/player"}},
		}},
	}
	postParcel(t, ts, "rev-002", &p, http.StatusBadRequest)
}

func TestPostParcel_RejectsBadRevision(t *testing.T) {
	ts, _ := setupWritableServer(t)
	p := build.Parcel{Bundles: nil}
	// Single-segment so it routes; "!" is outside [A-Za-z0-9._-].
	postParcel(t, ts, "rev!bad", &p, http.StatusBadRequest)
}

func TestPostParcel_RejectsInvalidJSON(t *testing.T) {
	ts, _ := setupWritableServer(t)
	res, err := http.Post(ts.URL+"/parcels/rev-003", "application/json",
		strings.NewReader("{not valid json"))
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	if res.StatusCode != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", res.StatusCode)
	}
}

// ---- Helpers --------------------------------------------------------------

func postBlob(t *testing.T, ts *httptest.Server, body []byte) string {
	t.Helper()
	res, err := http.Post(ts.URL+"/blobs", "application/octet-stream",
		bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("POST /blobs: got %d (%s)", res.StatusCode, raw)
	}
	var resp struct{ Hash string }
	if err := json.NewDecoder(res.Body).Decode(&resp); err != nil {
		t.Fatal(err)
	}
	return resp.Hash
}

func getBlob(t *testing.T, ts *httptest.Server, hash string) []byte {
	t.Helper()
	url := ts.URL + "/blobs/" + hash[:2] + "/" + hash
	res, err := http.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		t.Fatalf("GET %s: got %d", url, res.StatusCode)
	}
	got, err := io.ReadAll(res.Body)
	if err != nil {
		t.Fatal(err)
	}
	return got
}

func postParcel(t *testing.T, ts *httptest.Server, rev string, p *build.Parcel, wantStatus int) {
	t.Helper()
	body, err := json.Marshal(p)
	if err != nil {
		t.Fatal(err)
	}
	res, err := http.Post(ts.URL+"/parcels/"+rev, "application/json",
		bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != wantStatus {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("POST /parcels/%s: got %d (%s) want %d",
			rev, res.StatusCode, raw, wantStatus)
	}
}
