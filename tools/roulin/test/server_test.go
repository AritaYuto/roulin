package roulin_test

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/server"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/cache"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/cloud"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

func setupParcelServer(t *testing.T) (*httptest.Server, string) {
	t.Helper()
	dir := t.TempDir()

	// Create index file.
	os.MkdirAll(filepath.Join(dir, "index"), 0o755)
	os.WriteFile(filepath.Join(dir, "index", "abc123"), []byte("indexdata"), 0o644)

	// Create blob file.
	hash := "ab00000000000000000000000000000000000000000000000000000000000000"
	os.MkdirAll(filepath.Join(dir, "blobs", "ab"), 0o755)
	os.WriteFile(filepath.Join(dir, "blobs", "ab", hash), []byte("blobdata"), 0o644)

	srv := server.New(local.NewFile(dir), nil, 0)
	ts := httptest.NewServer(srv.Handler)
	return ts, hash
}

func TestGetIndex(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/index/abc123")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
}

func TestGetIndexNotFound(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/index/missing")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 404 {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}

func TestGetIndexInvalidRevision(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/index/../etc/passwd")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	// Go's ServeMux will clean the path, so this should be 400 or 404.
	if resp.StatusCode == 200 {
		t.Fatal("expected non-200 for path traversal attempt")
	}
}

func TestGetBlob(t *testing.T) {
	ts, hash := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/blobs/ab/" + hash)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
}

func TestGetBlobNotFound(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/blobs/00/0000000000000000000000000000000000000000000000000000000000000000")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 404 {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}

func TestGetBlobInvalidHash(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	tests := []struct {
		name string
		path string
	}{
		{"short hash", "/blobs/ab/ab0000"},
		{"non-hex", "/blobs/zz/zz00000000000000000000000000000000000000000000000000000000000000"},
		{"prefix mismatch", "/blobs/00/ab00000000000000000000000000000000000000000000000000000000000000"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			resp, err := http.Get(ts.URL + tc.path)
			if err != nil {
				t.Fatal(err)
			}
			defer resp.Body.Close()
			if resp.StatusCode != 400 {
				t.Fatalf("status = %d, want 400", resp.StatusCode)
			}
		})
	}
}

func TestHealth(t *testing.T) {
	ts, _ := setupParcelServer(t)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/health")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
}

// Verify Storage interface is satisfied at compile time by each backend.
var (
	_ storage.Storage = (*cloud.S3Storage)(nil)
	_ storage.Storage = (*cache.MemoryStorage)(nil)
	_ storage.Storage = (*local.FileStorage)(nil)
	_ storage.Storage = (*storage.FakeStorage)(nil)
	_ storage.Storage = (*storage.CachedStorage)(nil)
)
