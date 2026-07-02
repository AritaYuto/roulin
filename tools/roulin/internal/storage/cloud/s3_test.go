// Integration test for the S3Storage backend. Hits a live MinIO at
// localhost:9000 by default — override via ROULIN_TEST_S3_ENDPOINT.
//
// Gated by ROULIN_INTEGRATION=1 so a plain `go test ./...` skips it.

package cloud

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"sort"
	"testing"

	"lukechampine.com/blake3"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

func requireIntegration(t *testing.T) {
	t.Helper()
	if os.Getenv("ROULIN_INTEGRATION") != "1" {
		t.Skip("set ROULIN_INTEGRATION=1 to run integration tests")
	}
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if value != "" {
			return value
		}
	}
	return ""
}

func s3Endpoint() string {
	if endpoint := os.Getenv("ROULIN_TEST_S3_ENDPOINT"); endpoint != "" {
		return endpoint
	}
	return "http://localhost:9000"
}

// setupS3Storage opens a Storage rooted at a unique prefix in the test
// bucket so concurrent test runs don't collide.
func setupS3Storage(ctx context.Context, t *testing.T) storage.Storage {
	t.Helper()
	t.Setenv("AWS_ACCESS_KEY_ID", firstNonEmpty(os.Getenv("AWS_ACCESS_KEY_ID"), "minioadmin"))
	t.Setenv("AWS_SECRET_ACCESS_KEY", firstNonEmpty(os.Getenv("AWS_SECRET_ACCESS_KEY"), "minioadmin"))
	t.Setenv("AWS_REGION", firstNonEmpty(os.Getenv("AWS_REGION"), "us-east-1"))

	// Per-test prefix isolates state so parallel runs / re-runs don't
	// see each other's writes.
	prefixBuf := make([]byte, 8)
	_, _ = rand.Read(prefixBuf)
	url := "s3://roulin-dev/integration_" + hex.EncodeToString(prefixBuf)

	store, err := storage.Open(ctx, url, storage.Options{
		Endpoint:  s3Endpoint(),
		PathStyle: true,
	})
	if err != nil {
		t.Fatalf("storage.Open: %v", err)
	}
	return store
}

func hashHex(label string) string {
	sum := blake3.Sum256([]byte(label))
	return hex.EncodeToString(sum[:])
}

// TestS3Storage_RoundTrip exercises Put / Get / Has for each kind.
// Mirrors what the HTTP handlers do for a build cycle.
func TestS3Storage_RoundTrip(t *testing.T) {
	requireIntegration(t)
	ctx := context.Background()
	store := setupS3Storage(ctx, t)

	cases := []struct {
		name string
		put  func(key string, data []byte) error
		get  func(key string) ([]byte, error)
	}{
		{
			"blob",
			func(key string, data []byte) error { return store.PutBlob(ctx, key, data) },
			func(key string) ([]byte, error) { return store.GetBlob(ctx, key) },
		},
		{
			"index",
			func(key string, data []byte) error { return store.PutIndex(ctx, key, data) },
			func(key string) ([]byte, error) { return store.GetIndex(ctx, key) },
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			key := hashHex(tc.name + "-roundtrip")
			if tc.name == "index" {
				key = "rev-" + tc.name + "-001"
			}
			payload := []byte("payload for " + tc.name)
			if err := tc.put(key, payload); err != nil {
				t.Fatalf("put: %v", err)
			}
			got, err := tc.get(key)
			if err != nil {
				t.Fatalf("get: %v", err)
			}
			if !bytes.Equal(got, payload) {
				t.Fatalf("round-trip mismatch: got %q want %q", got, payload)
			}
		})
	}
}

func TestS3Storage_HasBlob(t *testing.T) {
	requireIntegration(t)
	ctx := context.Background()
	store := setupS3Storage(ctx, t)

	hash := hashHex("has-blob-fixture")
	ok, err := store.HasBlob(ctx, hash)
	if err != nil {
		t.Fatalf("HasBlob (missing): %v", err)
	}
	if ok {
		t.Fatalf("HasBlob returned true for missing blob")
	}

	if err := store.PutBlob(ctx, hash, []byte("x")); err != nil {
		t.Fatalf("PutBlob: %v", err)
	}
	ok, err = store.HasBlob(ctx, hash)
	if err != nil {
		t.Fatalf("HasBlob: %v", err)
	}
	if !ok {
		t.Fatalf("HasBlob returned false after PutBlob")
	}
}

func TestS3Storage_NotFound(t *testing.T) {
	requireIntegration(t)
	ctx := context.Background()
	store := setupS3Storage(ctx, t)

	_, err := store.GetBlob(ctx, hashHex("never-uploaded"))
	if !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("GetBlob missing: want os.ErrNotExist wrap, got %v", err)
	}
	_, err = store.GetIndex(ctx, "rev-does-not-exist")
	if !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("GetIndex missing: want os.ErrNotExist wrap, got %v", err)
	}
}

func TestS3Storage_ListIndexRevisions(t *testing.T) {
	requireIntegration(t)
	ctx := context.Background()
	store := setupS3Storage(ctx, t)

	revs := []string{"rev-list-001", "rev-list-002", "rev-list-003"}
	for _, rev := range revs {
		if err := store.PutIndex(ctx, rev, []byte("idx-"+rev)); err != nil {
			t.Fatalf("PutIndex %s: %v", rev, err)
		}
	}

	got, err := store.ListIndexRevisions(ctx)
	if err != nil {
		t.Fatalf("ListIndexRevisions: %v", err)
	}
	gotRevs := make([]string, len(got))
	for i, obj := range got {
		gotRevs[i] = obj.Revision
	}
	sort.Strings(gotRevs)
	for i, want := range revs {
		if i >= len(gotRevs) || gotRevs[i] != want {
			t.Fatalf("ListIndexRevisions = %v, want at least %v", gotRevs, revs)
		}
	}
}
