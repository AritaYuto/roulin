package server

import (
	"net/http"
	"testing"
)

func TestGetBlob(t *testing.T) {
	ts, hash := setupReadOnlyServer(t)
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
	ts, _ := setupReadOnlyServer(t)
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
	ts, _ := setupReadOnlyServer(t)
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

// Health lives in handler_blob.go alongside handleHealth, so its test sits
// next to the blob tests rather than in a standalone file.
func TestHealth(t *testing.T) {
	ts, _ := setupReadOnlyServer(t)
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
