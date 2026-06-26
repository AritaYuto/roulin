package server

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

type stubVCS struct {
	rev         string
	revErr      error
	changed     []string
	changedErr  error
	uncommitted []string
	uncomErr    error
	gotSince    string
}

func (s *stubVCS) CurrentRevision() (string, error) { return s.rev, s.revErr }
func (s *stubVCS) ChangedFiles(since string) ([]string, error) {
	s.gotSince = since
	return s.changed, s.changedErr
}
func (s *stubVCS) UncommittedFiles() ([]string, error) { return s.uncommitted, s.uncomErr }

func newDiffTestServer(t *testing.T, v *stubVCS) *httptest.Server {
	t.Helper()
	dir := t.TempDir()
	srv := New(local.NewFile(dir), nil, v, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)
	return ts
}

func TestDiff_HappyPath(t *testing.T) {
	v := &stubVCS{
		rev:         "deadbeef",
		changed:     []string{"Assets/Tex/foo.png"},
		uncommitted: []string{"Assets/Mat/bar.mat"},
	}
	ts := newDiffTestServer(t, v)

	resp, err := http.Get(ts.URL + "/diff?since=cafebabe")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}
	var body DiffResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if body.Revision != "deadbeef" {
		t.Errorf("Revision = %q, want deadbeef", body.Revision)
	}
	if v.gotSince != "cafebabe" {
		t.Errorf("ChangedFiles since = %q, want cafebabe", v.gotSince)
	}
	if len(body.Changed) != 1 || body.Changed[0] != "Assets/Tex/foo.png" {
		t.Errorf("Changed = %v", body.Changed)
	}
	if len(body.Uncommitted) != 1 || body.Uncommitted[0] != "Assets/Mat/bar.mat" {
		t.Errorf("Uncommitted = %v", body.Uncommitted)
	}
}

func TestDiff_EmptySinceSkipsChangedFiles(t *testing.T) {
	v := &stubVCS{
		rev:         "deadbeef",
		uncommitted: []string{"Assets/u.png"},
	}
	ts := newDiffTestServer(t, v)

	resp, err := http.Get(ts.URL + "/diff")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	var body DiffResponse
	_ = json.NewDecoder(resp.Body).Decode(&body)
	if body.Changed != nil {
		t.Errorf("Changed should be nil when since is empty, got %v", body.Changed)
	}
	if v.gotSince != "" {
		t.Errorf("ChangedFiles should not have been called; gotSince = %q", v.gotSince)
	}
}

func TestDiff_InvalidSince(t *testing.T) {
	ts := newDiffTestServer(t, &stubVCS{rev: "x"})
	resp, err := http.Get(ts.URL + "/diff?since=../etc/passwd")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", resp.StatusCode)
	}
}

func TestDiff_DisabledWhenAdapterNil(t *testing.T) {
	dir := t.TempDir()
	srv := New(local.NewFile(dir), nil, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	defer ts.Close()

	resp, err := http.Get(ts.URL + "/diff")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", resp.StatusCode)
	}
}
