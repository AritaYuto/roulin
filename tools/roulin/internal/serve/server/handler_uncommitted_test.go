package server

import (
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

func TestUncommitted_HappyPath(t *testing.T) {
	dir := t.TempDir()
	adapter := &stubVCS{
		uncommitted: []string{"Assets/Tex/foo.png", "Assets/Mat/bar.mat"},
	}
	srv := New(local.NewFile(dir), nil, adapter, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/uncommitted")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body UncommittedResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if len(body.Uncommitted) != 2 ||
		body.Uncommitted[0] != "Assets/Tex/foo.png" ||
		body.Uncommitted[1] != "Assets/Mat/bar.mat" {
		t.Errorf("Uncommitted = %v", body.Uncommitted)
	}
}

func TestUncommitted_AdapterError(t *testing.T) {
	dir := t.TempDir()
	adapter := &stubVCS{uncomErr: errors.New("git status boom")}
	srv := New(local.NewFile(dir), nil, adapter, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/uncommitted")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusInternalServerError {
		t.Fatalf("status = %d, want 500", resp.StatusCode)
	}
}

func TestUncommitted_DisabledWhenAdapterNil(t *testing.T) {
	dir := t.TempDir()
	srv := New(local.NewFile(dir), nil, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/uncommitted")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}
