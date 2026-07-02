package vcs

import (
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"testing"
)

func initGitRepo(t *testing.T) (*GitAdapter, string) {
	t.Helper()
	if _, err := exec.LookPath("git"); err != nil {
		t.Skipf("git not available: %v", err)
	}
	dir := t.TempDir()
	adapter := &GitAdapter{WorkDir: dir}
	mustGit(t, dir, "init", "-q", "-b", "main")
	mustGit(t, dir, "config", "user.email", "test@example.com")
	mustGit(t, dir, "config", "user.name", "test")
	mustGit(t, dir, "config", "commit.gpgsign", "false")
	writeGitFile(t, dir, "seed.txt", "seed\n")
	mustGit(t, dir, "add", "seed.txt")
	mustGit(t, dir, "commit", "-q", "-m", "seed")
	return adapter, dir
}

func mustGit(t *testing.T, dir string, args ...string) {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = dir
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
}

func writeGitFile(t *testing.T, dir, name, body string) {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatalf("write %s: %v", name, err)
	}
}

func TestGitAdapter_CurrentRevision(t *testing.T) {
	adapter, _ := initGitRepo(t)
	rev, err := adapter.CurrentRevision()
	if err != nil {
		t.Fatal(err)
	}
	if len(rev) != 40 {
		t.Fatalf("want 40-char SHA, got %q", rev)
	}
}

func TestGitAdapter_ChangedFilesBetweenCommits(t *testing.T) {
	adapter, dir := initGitRepo(t)
	base, err := adapter.CurrentRevision()
	if err != nil {
		t.Fatal(err)
	}
	writeGitFile(t, dir, "Assets/Tex/foo.png", "image-bytes")
	writeGitFile(t, dir, "Assets/Mat/bar.mat", "material")
	mustGit(t, dir, "add", ".")
	mustGit(t, dir, "commit", "-q", "-m", "add two files")

	got, err := adapter.ChangedFiles(base)
	if err != nil {
		t.Fatal(err)
	}
	sort.Strings(got)
	want := []string{"Assets/Mat/bar.mat", "Assets/Tex/foo.png"}
	if !sliceEqual(got, want) {
		t.Fatalf("ChangedFiles = %v, want %v", got, want)
	}
}

func TestGitAdapter_ChangedFilesEmptySince(t *testing.T) {
	adapter, _ := initGitRepo(t)
	got, err := adapter.ChangedFiles("")
	if err != nil {
		t.Fatal(err)
	}
	if got != nil {
		t.Fatalf("empty since should return nil, got %v", got)
	}
}

func TestGitAdapter_UncommittedFilesMixed(t *testing.T) {
	adapter, dir := initGitRepo(t)
	writeGitFile(t, dir, "seed.txt", "seed-modified\n")
	writeGitFile(t, dir, "Assets/new.png", "new")
	writeGitFile(t, dir, "Assets/staged.mat", "staged")
	mustGit(t, dir, "add", "Assets/staged.mat")

	got, err := adapter.UncommittedFiles()
	if err != nil {
		t.Fatal(err)
	}
	sort.Strings(got)
	want := []string{"Assets/new.png", "Assets/staged.mat", "seed.txt"}
	if !sliceEqual(got, want) {
		t.Fatalf("UncommittedFiles = %v, want %v", got, want)
	}
}

func TestGitAdapter_UncommittedFilesClean(t *testing.T) {
	adapter, _ := initGitRepo(t)
	got, err := adapter.UncommittedFiles()
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 0 {
		t.Fatalf("clean tree should return no paths, got %v", got)
	}
}

func TestGitAdapter_UncommittedRenameReportsBothEndpoints(t *testing.T) {
	adapter, dir := initGitRepo(t)
	writeGitFile(t, dir, "Assets/old.png", "image")
	mustGit(t, dir, "add", "Assets/old.png")
	mustGit(t, dir, "commit", "-q", "-m", "add old")
	mustGit(t, dir, "mv", "Assets/old.png", "Assets/new.png")

	got, err := adapter.UncommittedFiles()
	if err != nil {
		t.Fatal(err)
	}
	has := func(target string) bool {
		for _, path := range got {
			if path == target {
				return true
			}
		}
		return false
	}
	if !has("Assets/old.png") || !has("Assets/new.png") {
		t.Fatalf("rename should report both endpoints, got %v", got)
	}
}

func TestGitAdapter_PathspecsScopeUncommitted(t *testing.T) {
	adapter, dir := initGitRepo(t)
	writeGitFile(t, dir, "client/proj/Assets/foo.png", "in-scope")
	writeGitFile(t, dir, "tools/script.sh", "out-of-scope")
	mustGit(t, dir, "add", ".")
	mustGit(t, dir, "commit", "-q", "-m", "seed two trees")
	writeGitFile(t, dir, "client/proj/Assets/foo.png", "in-scope-modified")
	writeGitFile(t, dir, "tools/script.sh", "out-of-scope-modified")
	writeGitFile(t, dir, "client/proj/Assets/new.png", "in-scope-untracked")
	writeGitFile(t, dir, "tools/other.sh", "out-of-scope-untracked")

	adapter.Pathspecs = []string{"client/proj"}
	got, err := adapter.UncommittedFiles()
	if err != nil {
		t.Fatal(err)
	}
	sort.Strings(got)
	want := []string{"client/proj/Assets/foo.png", "client/proj/Assets/new.png"}
	if !sliceEqual(got, want) {
		t.Fatalf("UncommittedFiles with pathspec = %v, want %v", got, want)
	}
}

func TestGitAdapter_PathspecsScopeChangedFiles(t *testing.T) {
	adapter, dir := initGitRepo(t)
	base, err := adapter.CurrentRevision()
	if err != nil {
		t.Fatal(err)
	}
	writeGitFile(t, dir, "client/proj/Assets/foo.png", "in")
	writeGitFile(t, dir, "tools/script.sh", "out")
	mustGit(t, dir, "add", ".")
	mustGit(t, dir, "commit", "-q", "-m", "two commits")

	adapter.Pathspecs = []string{"client/proj"}
	got, err := adapter.ChangedFiles(base)
	if err != nil {
		t.Fatal(err)
	}
	sort.Strings(got)
	want := []string{"client/proj/Assets/foo.png"}
	if !sliceEqual(got, want) {
		t.Fatalf("ChangedFiles with pathspec = %v, want %v", got, want)
	}
}

func TestGitAdapter_PathspecsEmptyAppliesNothing(t *testing.T) {
	adapter, dir := initGitRepo(t)
	writeGitFile(t, dir, "seed.txt", "seed-modified\n")
	writeGitFile(t, dir, "Assets/new.png", "new")

	adapter.Pathspecs = nil
	got, err := adapter.UncommittedFiles()
	if err != nil {
		t.Fatal(err)
	}
	sort.Strings(got)
	want := []string{"Assets/new.png", "seed.txt"}
	if !sliceEqual(got, want) {
		t.Fatalf("UncommittedFiles with no pathspec = %v, want %v", got, want)
	}
}

func sliceEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
