// pipeline-verify checks that a parcel written by Go is readable by the C++ FFI.
// Spawns roulin-server, uploads blobs, posts a parcel, then runs the C++ pipeline-test binary.
// Usage: go run . [build_dir]
package main

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

// Parcel wire shape mirrors tools/roulin/internal/build.
// Duplicated here to keep pipeline-verify module-independent; drift surfaces at compile time.
type Parcel struct {
	Bundles []Bundle `json:"bundles"`
}

type Bundle struct {
	Address   string  `json:"address"`
	BlobHash  string  `json:"blob_hash"`
	SizeBytes uint64  `json:"size_bytes,omitempty"`
	Entries   []Entry `json:"entries"`
}

type Entry struct {
	Address string `json:"address"`
}

type postBlobResponse struct {
	Hash string `json:"hash"`
}

func main() {
	if err := chdirRepoRoot(); err != nil {
		fatal("%v", err)
	}

	buildDir := "build"
	if len(os.Args) > 1 {
		buildDir = os.Args[1]
	}

	pipelineTest, err := findBinary("PIPELINE_TEST",
		filepath.Join(buildDir, "core", "tests", exe("pipeline-test")))
	if err != nil {
		fatal("%v", err)
	}
	fetchPipelineTest, err := findBinary("FETCH_PIPELINE_TEST",
		filepath.Join(buildDir, "fetch", "tests", exe("fetch-pipeline-test")))
	if err != nil {
		fatal("%v", err)
	}
	roulinServer, err := findBinary("ROULIN_SERVER",
		filepath.Join("tools", "roulin", exe("roulin-server")))
	if err != nil {
		fatal("%v", err)
	}

	work, err := os.MkdirTemp("", "roulin-pipeline-")
	if err != nil {
		fatal("mktemp: %v", err)
	}
	defer os.RemoveAll(work)

	parcelDir := filepath.Join(work, "parcel")
	expected := filepath.Join(work, "expected")
	if err := os.MkdirAll(expected, 0o755); err != nil {
		fatal("mkdir: %v", err)
	}

	files := []struct{ address, content string }{
		{"hello.txt", "Hello from Roulin pipeline test!"},
		{"data/config.json", `{"version":1,"name":"test"}`},
		{"ui/icons/player.png", "\x89PNG\r\n\x1a\nfake png bytes"},
	}
	// Materialise expected-content files for pipeline-test's byte compare.
	for _, f := range files {
		p := filepath.Join(expected, filepath.FromSlash(f.address))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			fatal("mkdir %s: %v", filepath.Dir(p), err)
		}
		if err := os.WriteFile(p, []byte(f.content), 0o644); err != nil {
			fatal("write %s: %v", p, err)
		}
	}

	revision, err := randomRevision()
	if err != nil {
		fatal("revision: %v", err)
	}

	port, err := freePort()
	if err != nil {
		fatal("free port: %v", err)
	}
	baseURL := fmt.Sprintf("http://127.0.0.1:%d", port)

	fmt.Println("=== 1. spawn roulin-server (file:// backend) ===")
	srv := exec.Command(roulinServer, "serve",
		"--storage", "file://"+parcelDir,
		"--port", fmt.Sprint(port),
		"--log-dir", filepath.Join(work, "logs"))
	srv.Stdout = os.Stdout
	srv.Stderr = os.Stderr
	if err := srv.Start(); err != nil {
		fatal("spawn roulin-server: %v", err)
	}
	defer func() {
		_ = srv.Process.Kill()
		_, _ = srv.Process.Wait()
	}()
	if err := waitReady(baseURL, 5*time.Second); err != nil {
		fatal("roulin-server not ready: %v", err)
	}
	fmt.Printf("    storage = file://%s   server = %s\n", parcelDir, baseURL)

	fmt.Println("\n=== 2. POST /blobs for each file ===")
	bundles := make([]Bundle, 0, len(files))
	for _, f := range files {
		hash, err := postBlob(baseURL, []byte(f.content))
		if err != nil {
			fatal("POST /blobs %s: %v", f.address, err)
		}
		fmt.Printf("    %s → %s…\n", f.address, hash[:12])
		bundles = append(bundles, Bundle{
			Address:   f.address,
			BlobHash:  hash,
			SizeBytes: uint64(len(f.content)),
			Entries:   []Entry{{Address: f.address}},
		})
	}

	fmt.Println("\n=== 3. POST /parcels/" + revision + " ===")
	if err := postParcel(baseURL, revision, Parcel{Bundles: bundles}); err != nil {
		fatal("POST /parcels: %v", err)
	}

	fmt.Println("\n=== 4. C++ ac_fetch_* round-trip (HTTP fetch + BLAKE3 verify) ===")
	fetchArgs := []string{baseURL}
	for _, b := range bundles {
		fetchArgs = append(fetchArgs, b.BlobHash)
	}
	if err := run(fetchPipelineTest, fetchArgs...); err != nil {
		fatal("fetch-pipeline-test: %v", err)
	}

	// Shut roulin-server down before pipeline-test so file handles flush.
	_ = srv.Process.Kill()
	_, _ = srv.Process.Wait()

	fmt.Println("\n=== 5. C++ pipeline-test (ac_parcel_open → ac_blob_read) ===")
	args := []string{parcelDir, revision}
	for _, f := range files {
		args = append(args, f.address, filepath.Join(expected, filepath.FromSlash(f.address)))
	}
	if err := run(pipelineTest, args...); err != nil {
		fatal("pipeline-test: %v", err)
	}

	fmt.Println("\n=== Pipeline verification PASSED ===")
	fmt.Println("    Go-written parcel (file://) is readable by C++ roulin-core.")
}

func waitReady(baseURL string, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		res, err := http.Get(baseURL + "/health")
		if err == nil {
			res.Body.Close()
			if res.StatusCode == http.StatusOK {
				return nil
			}
		}
		time.Sleep(50 * time.Millisecond)
	}
	return fmt.Errorf("server did not become ready within %s", timeout)
}

func freePort() (int, error) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port, nil
}

func postBlob(baseURL string, body []byte) (string, error) {
	res, err := http.Post(baseURL+"/blobs", "application/octet-stream", bytes.NewReader(body))
	if err != nil {
		return "", err
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(res.Body)
		return "", fmt.Errorf("status %d: %s", res.StatusCode, raw)
	}
	var resp postBlobResponse
	if err := json.NewDecoder(res.Body).Decode(&resp); err != nil {
		return "", err
	}
	return resp.Hash, nil
}

func postParcel(baseURL, revision string, p Parcel) error {
	body, err := json.Marshal(p)
	if err != nil {
		return err
	}
	res, err := http.Post(baseURL+"/parcels/"+revision, "application/json", bytes.NewReader(body))
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusCreated {
		raw, _ := io.ReadAll(res.Body)
		return fmt.Errorf("status %d: %s", res.StatusCode, raw)
	}
	return nil
}

func findBinary(envVar, defaultPath string) (string, error) {
	if override := os.Getenv(envVar); override != "" {
		if _, err := os.Stat(override); err != nil {
			return "", fmt.Errorf("%s=%s: %w", envVar, override, err)
		}
		return override, nil
	}
	if _, err := os.Stat(defaultPath); err == nil {
		return defaultPath, nil
	}
	return "", fmt.Errorf("binary not found at %s (override with %s)", defaultPath, envVar)
}

func exe(name string) string {
	if runtime.GOOS == "windows" {
		return name + ".exe"
	}
	return name
}

func randomRevision() (string, error) {
	var b [6]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return "pipeline-" + hex.EncodeToString(b[:]), nil
}

func chdirRepoRoot() error {
	out, err := exec.Command("git", "rev-parse", "--show-toplevel").Output()
	if err != nil {
		return fmt.Errorf("must run inside the roulin git repo: %w", err)
	}
	return os.Chdir(strings.TrimSpace(string(out)))
}

func run(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "error: "+format+"\n", args...)
	os.Exit(1)
}
