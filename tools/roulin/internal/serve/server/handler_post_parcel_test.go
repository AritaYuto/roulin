package server

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

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
)

func TestPostParcel_HappyPath(t *testing.T) {
	ts, baseDir := setupWritableServer(t)

	bundleA := []byte("bundle-A bytes")
	bundleB := []byte("bundle-B bytes")
	hashA := postBlob(t, ts, bundleA)
	hashB := postBlob(t, ts, bundleB)

	parcel := build.Parcel{
		Bundles: []build.Bundle{
			{
				Address:  "ui/icons",
				BlobHash: hashA,
				Entries: []build.Entry{
					{Address: "ui/icons/player"},
					{Address: "ui/icons/enemy"},
				},
				Dependencies: []string{hashB},
			},
			{
				Address:  "shared",
				BlobHash: hashB,
				Entries:  []build.Entry{{Address: "shared/atlas"}},
			},
		},
	}

	postParcel(t, ts, "rev-001", &parcel, http.StatusCreated)

	idxBytes, err := os.ReadFile(filepath.Join(baseDir, "index", "rev-001"))
	if err != nil {
		t.Fatalf("read index: %v", err)
	}
	entries, _ := build.ParseIndexBytes(idxBytes)

	if len(entries) != 2 {
		t.Fatalf("IndexEntries: got %d want 2", len(entries))
	}

	addrs := map[string]bool{}
	depsByHash := map[string][]string{}
	for _, entry := range entries {
		for _, addr := range entry.Addresses {
			addrs[addr.AddressStr] = true
		}
		depsByHash[hex.EncodeToString(entry.BlobHash[:])] = entry.Deps
	}
	for _, want := range []string{"ui/icons/player", "ui/icons/enemy", "shared/atlas"} {
		if !addrs[want] {
			t.Errorf("addresses missing %q", want)
		}
	}

	if got := depsByHash[hashA]; len(got) != 1 || got[0] != hashB {
		t.Errorf("bundle %s deps: got %v want [%s]", hashA, got, hashB)
	}
	if got := depsByHash[hashB]; len(got) != 0 {
		t.Errorf("bundle %s deps: got %v want []", hashB, got)
	}
}

// Verifies that Parcel.Bundle.SizeBytes is preserved through the wire:
// server writes it onto every IndexEntry within the bundle. Powers
// Addressables.GetDownloadSizeAsync via ILocationSizeData.
func TestPostParcel_PropagatesSize(t *testing.T) {
	ts, baseDir := setupWritableServer(t)

	bundleBytes := []byte("bundle-A bytes that have a known length")
	bundleSize := uint64(len(bundleBytes))
	hashA := postBlob(t, ts, bundleBytes)

	parcel := build.Parcel{
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
	postParcel(t, ts, "rev-size", &parcel, http.StatusCreated)

	idxBytes, err := os.ReadFile(filepath.Join(baseDir, "index", "rev-size"))
	if err != nil {
		t.Fatalf("read index: %v", err)
	}
	entries, _ := build.ParseIndexBytes(idxBytes)

	if len(entries) != 1 {
		t.Fatalf("IndexEntries: got %d want 1", len(entries))
	}
	entry := entries[0]
	if entry.SizeBytes != bundleSize {
		t.Errorf("SizeBytes: got %d want %d", entry.SizeBytes, bundleSize)
	}
	if hex.EncodeToString(entry.BlobHash[:]) != hashA {
		t.Errorf("BlobHash: got %x want %s", entry.BlobHash, hashA)
	}
	if len(entry.Addresses) != 2 {
		t.Errorf("Addresses: got %d want 2", len(entry.Addresses))
	}
}

func TestPostParcel_RejectsUnuploadedBlob(t *testing.T) {
	ts, _ := setupWritableServer(t)

	bogus := strings.Repeat("ab", 32)
	parcel := build.Parcel{
		Bundles: []build.Bundle{{
			Address: "ui/icons", BlobHash: bogus,
			Entries: []build.Entry{{Address: "ui/icons/player"}},
		}},
	}
	postParcel(t, ts, "rev-002", &parcel, http.StatusBadRequest)
}

func TestPostParcel_RejectsBadRevision(t *testing.T) {
	ts, _ := setupWritableServer(t)
	parcel := build.Parcel{Bundles: nil}
	// Single-segment so it routes; "!" is outside [A-Za-z0-9._-].
	postParcel(t, ts, "rev!bad", &parcel, http.StatusBadRequest)
}

func TestPostParcel_RejectsInvalidJSON(t *testing.T) {
	ts, _ := setupWritableServer(t)
	resp, err := http.Post(ts.URL+"/parcels/rev-003", "application/json",
		strings.NewReader("{not valid json"))
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", resp.StatusCode)
	}
}

func postParcel(t *testing.T, ts *httptest.Server, rev string, parcel *build.Parcel, wantStatus int) {
	t.Helper()
	body, err := json.Marshal(parcel)
	if err != nil {
		t.Fatal(err)
	}
	resp, err := http.Post(ts.URL+"/parcels/"+rev, "application/json",
		bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != wantStatus {
		raw, _ := io.ReadAll(resp.Body)
		t.Fatalf("POST /parcels/%s: got %d (%s) want %d",
			rev, resp.StatusCode, raw, wantStatus)
	}
}
