package server

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// setupReadOnlyServer builds a temp-backed read-only server preloaded with
// one index revision and one blob. Blob hash is returned so blob tests can
// reference it without duplicating the fixture setup.
func setupReadOnlyServer(t *testing.T) (*httptest.Server, string) {
	t.Helper()
	dir := t.TempDir()

	if err := os.MkdirAll(filepath.Join(dir, "index"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "index", "abc123"), []byte("indexdata"), 0o644); err != nil {
		t.Fatal(err)
	}

	hash := "ab00000000000000000000000000000000000000000000000000000000000000"
	if err := os.MkdirAll(filepath.Join(dir, "blobs", "ab"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "blobs", "ab", hash), []byte("blobdata"), 0o644); err != nil {
		t.Fatal(err)
	}

	srv := New(local.NewFile(dir), nil, nil, 0)
	return httptest.NewServer(srv.Handler), hash
}

func TestGetIndex(t *testing.T) {
	ts, _ := setupReadOnlyServer(t)
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
	ts, _ := setupReadOnlyServer(t)
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
	ts, _ := setupReadOnlyServer(t)
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
