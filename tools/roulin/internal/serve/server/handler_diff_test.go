package server

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sort"
	"testing"
	"time"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
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
	changedCall int
}

func (s *stubVCS) CurrentRevision() (string, error) { return s.rev, s.revErr }
func (s *stubVCS) ChangedFiles(since string) ([]string, error) {
	s.gotSince = since
	s.changedCall++
	return s.changed, s.changedErr
}
func (s *stubVCS) UncommittedFiles() ([]string, error) { return s.uncommitted, s.uncomErr }

func newDiffTestServer(t *testing.T, v *stubVCS, storeDir string) *httptest.Server {
	t.Helper()
	srv := New(local.NewFile(storeDir), nil, v, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)
	return ts
}

// putIndex writes a real FlatBuffers Index for the revision so the /diff
// handler can parse it back and extract bundle names. Names come from the
// caller; hashes are synthesised so each entry is unique.
func putIndex(t *testing.T, dir, rev string, bundleNames ...string) {
	t.Helper()
	entries := make([]build.IndexEntry, len(bundleNames))
	for i, name := range bundleNames {
		var h [32]byte
		h[0] = byte(i + 1) // unique per entry so the memcmp sort is stable
		entries[i] = build.IndexEntry{BlobHash: h, Name: name}
	}
	buf := build.BuildIndexBytes(entries, nil)
	if err := local.NewFile(dir).PutIndex(context.Background(), rev, buf); err != nil {
		t.Fatal(err)
	}
}

func TestDiff_UsesLatestStoredIndexAsBase(t *testing.T) {
	dir := t.TempDir()
	putIndex(t, dir, "aaaa1111", "old-bundle")
	putIndex(t, dir, "bbbb2222", "ui-icons", "chr-common")
	// Back-date the older index so the LastModified-based sort is deterministic;
	// filesystems can round mtimes coarsely if we relied on write order.
	older := time.Now().Add(-2 * time.Second)
	if err := os.Chtimes(filepath.Join(dir, "index", "aaaa1111"), older, older); err != nil {
		t.Fatal(err)
	}

	v := &stubVCS{
		rev:         "ccc33333",
		changed:     []string{"Assets/Tex/foo.png"},
		uncommitted: []string{"Assets/Mat/bar.mat"},
	}
	ts := newDiffTestServer(t, v, dir)

	resp, err := http.Get(ts.URL + "/diff")
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
	if body.BaseRevision != "bbbb2222" {
		t.Errorf("BaseRevision = %q, want bbbb2222 (newest)", body.BaseRevision)
	}
	if body.HeadRevision != "ccc33333" {
		t.Errorf("HeadRevision = %q, want ccc33333", body.HeadRevision)
	}
	if v.gotSince != "bbbb2222" {
		t.Errorf("ChangedFiles since = %q, want bbbb2222", v.gotSince)
	}
	if len(body.Changed) != 1 || body.Changed[0] != "Assets/Tex/foo.png" {
		t.Errorf("Changed = %v", body.Changed)
	}
	if len(body.Uncommitted) != 1 || body.Uncommitted[0] != "Assets/Mat/bar.mat" {
		t.Errorf("Uncommitted = %v", body.Uncommitted)
	}
	got := append([]string(nil), body.BaseBundleNames...)
	sort.Strings(got)
	want := []string{"chr-common", "ui-icons"}
	if len(got) != len(want) || got[0] != want[0] || got[1] != want[1] {
		t.Errorf("BaseBundleNames = %v, want %v (from newest index bbbb2222)", got, want)
	}
}

func TestDiff_NoStoredIndexReturnsEmptyBase(t *testing.T) {
	dir := t.TempDir()
	v := &stubVCS{
		rev:         "ccc33333",
		uncommitted: []string{"Assets/u.png"},
	}
	ts := newDiffTestServer(t, v, dir)

	resp, err := http.Get(ts.URL + "/diff")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	var body DiffResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if body.BaseRevision != "" {
		t.Errorf("BaseRevision = %q, want empty", body.BaseRevision)
	}
	if body.Changed != nil {
		t.Errorf("Changed = %v, want nil when no base", body.Changed)
	}
	if body.BaseBundleNames != nil {
		t.Errorf("BaseBundleNames = %v, want nil when no base", body.BaseBundleNames)
	}
	if v.changedCall != 0 {
		t.Errorf("ChangedFiles should not be called when no base; got %d calls", v.changedCall)
	}
	if len(body.Uncommitted) != 1 {
		t.Errorf("Uncommitted = %v", body.Uncommitted)
	}
}

func TestDiff_BaseEqualsHeadSkipsChangedCall(t *testing.T) {
	dir := t.TempDir()
	putIndex(t, dir, "samesamesame", "ui-icons")
	v := &stubVCS{
		rev:         "samesamesame",
		uncommitted: []string{"Assets/u.png"},
	}
	ts := newDiffTestServer(t, v, dir)

	resp, err := http.Get(ts.URL + "/diff")
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d", resp.StatusCode)
	}
	var body DiffResponse
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	if v.changedCall != 0 {
		t.Errorf("ChangedFiles should not be called when base == head; got %d calls", v.changedCall)
	}
	if body.Changed != nil {
		t.Errorf("Changed = %v, want nil", body.Changed)
	}
	if body.BaseRevision != "samesamesame" || body.HeadRevision != "samesamesame" {
		t.Errorf("Base=%q Head=%q", body.BaseRevision, body.HeadRevision)
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
