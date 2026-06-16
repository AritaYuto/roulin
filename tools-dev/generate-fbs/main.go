// generate-fbs regenerates C++ and Go FlatBuffers stubs from core/schema/*.fbs.
// Uses flatc built from third_party/flatbuffers (submodule HEAD is the version pin).
// Usage: go run .
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

const (
	schemaDir      = "core/schema"
	cppOutDir      = "core/generated"
	goOutDir       = "tools/roulin/internal/storage/parcel/generated"
	goPackage      = "roulin_fbs"
	flatbuffersSrc = "third_party/flatbuffers"
	flatbuffersBin = "third_party/flatbuffers/build"
)

func main() {
	if err := chdirRepoRoot(); err != nil {
		fatal("%v", err)
	}
	if !exists(filepath.Join(flatbuffersSrc, "CMakeLists.txt")) {
		fatal("third_party/flatbuffers is empty — run `git submodule update --init --recursive`")
	}

	flatc, err := ensureFlatc()
	if err != nil {
		fatal("provision flatc: %v", err)
	}

	schemas, err := filepath.Glob(filepath.Join(schemaDir, "*.fbs"))
	if err != nil || len(schemas) == 0 {
		fatal("no schemas in %s", schemaDir)
	}
	if err := os.MkdirAll(cppOutDir, 0o755); err != nil {
		fatal("mkdir %s: %v", cppOutDir, err)
	}
	if err := os.MkdirAll(filepath.Join(goOutDir, goPackage), 0o755); err != nil {
		fatal("mkdir %s: %v", goOutDir, err)
	}

	fmt.Printf("→ %s/\n", cppOutDir)
	if err := run(flatc, append([]string{"--cpp", "-o", cppOutDir}, schemas...)...); err != nil {
		fatal("flatc --cpp: %v", err)
	}
	fmt.Printf("→ %s/%s/\n", goOutDir, goPackage)
	if err := run(flatc, append([]string{"--go", "--go-namespace", goPackage, "-o", goOutDir}, schemas...)...); err != nil {
		fatal("flatc --go: %v", err)
	}
	fmt.Println("done.")
}

// ensureFlatc returns a path to flatc, building it from third_party/flatbuffers
// into a gitignored build dir if not already present.
func ensureFlatc() (string, error) {
	candidates := []string{
		filepath.Join(flatbuffersBin, flatcExe()),
		filepath.Join(flatbuffersBin, "Release", flatcExe()),
	}
	for _, p := range candidates {
		if exists(p) {
			return p, nil
		}
	}
	fmt.Println("building flatc from third_party/flatbuffers (one-time)...")
	if err := run("cmake", "-S", flatbuffersSrc, "-B", flatbuffersBin,
		"-DFLATBUFFERS_BUILD_TESTS=OFF",
		"-DFLATBUFFERS_INSTALL=OFF",
		"-DCMAKE_BUILD_TYPE=Release"); err != nil {
		return "", err
	}
	// --config Release for multi-config generators (VS, Xcode); single-config ignores it.
	if err := run("cmake", "--build", flatbuffersBin,
		"--parallel", "--config", "Release", "--target", "flatc"); err != nil {
		return "", err
	}
	for _, p := range candidates {
		if exists(p) {
			return p, nil
		}
	}
	return "", fmt.Errorf("flatc not produced in %v", candidates)
}

func flatcExe() string {
	if runtime.GOOS == "windows" {
		return "flatc.exe"
	}
	return "flatc"
}

func exists(p string) bool {
	_, err := os.Stat(p)
	return err == nil
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
