// build-cpp drives cmake to build the C++ libraries (roulin-core +
// roulin-fetch) for a target platform.
// Usage: go run . <platform> <arch> [--release] [--test]
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

type target struct {
	platform string
	arch     string
	release  bool
	test     bool
}

func main() {
	t, err := parseArgs(os.Args[1:])
	if err != nil {
		fmt.Fprintf(os.Stderr, "error: %v\n\n", err)
		printUsage()
		os.Exit(1)
	}
	if err := chdirRepoRoot(); err != nil {
		fatal("%v", err)
	}
	if err := build(t); err != nil {
		fatal("%v", err)
	}
}

func build(t target) error {
	// Windows is MSVC-native only; cross-compilation from POSIX hosts is no
	// longer supported (the previous MinGW container path was retired when
	// the WinHTTP backend landed). Catch the wrong-host case here so the
	// build doesn't silently produce a non-Windows artifact under
	// dist/windows-x86_64/ on a macOS / Linux box.
	if t.platform == "windows" && runtime.GOOS != "windows" {
		return fmt.Errorf("windows builds require a Windows host (got runtime.GOOS=%s); "+
			"use CI's windows-2022 runner or build on a Windows machine", runtime.GOOS)
	}

	buildType := "Debug"
	if t.release {
		buildType = "Release"
	}
	buildDir := filepath.Join("build", fmt.Sprintf("%s-%s-%s", t.platform, t.arch, strings.ToLower(buildType)))
	distDir := filepath.Join("dist", fmt.Sprintf("%s-%s", t.platform, t.arch))

	fmt.Printf("=== %s/%s (%s) ===\n", t.platform, t.arch, buildType)

	args := []string{
		"-S", ".",
		"-B", buildDir,
		"-DCMAKE_BUILD_TYPE=" + buildType,
	}
	if t.test {
		args = append(args, "-DROULIN_BUILD_TESTS=ON")
	} else {
		args = append(args, "-DROULIN_BUILD_TESTS=OFF")
	}
	if tc := toolchainFile(t); tc != "" {
		args = append(args, "-DCMAKE_TOOLCHAIN_FILE="+tc)
	}

	if err := run("cmake", args...); err != nil {
		return fmt.Errorf("cmake configure: %w", err)
	}
	// --config is required by multi-config generators (Visual Studio, Xcode)
	// and ignored by single-config ones (Make, Ninja, MinGW Makefiles), so
	// passing it unconditionally is safe.
	if err := run("cmake", "--build", buildDir, "--parallel", "--config", buildType); err != nil {
		return fmt.Errorf("cmake build: %w", err)
	}
	if t.test {
		if err := run("ctest", "--test-dir", buildDir, "--output-on-failure", "-C", buildType); err != nil {
			return fmt.Errorf("ctest: %w", err)
		}
	}
	if err := run("cmake", "--install", buildDir, "--prefix", distDir, "--config", buildType); err != nil {
		return fmt.Errorf("cmake install: %w", err)
	}
	fmt.Printf("=== done → %s ===\n", distDir)
	return nil
}

// toolchainFile returns the CMake toolchain path, or "" for native builds.
// Toolchain files assume Linux containers and are skipped when host == target.
func toolchainFile(t target) string {
	if isNativeBuild(t) {
		return ""
	}
	p := filepath.Join("tools-dev", "build-cpp", "toolchains", fmt.Sprintf("%s-%s.cmake", t.platform, t.arch))
	if _, err := os.Stat(p); err == nil {
		return p
	}
	return ""
}

func isNativeBuild(t target) bool {
	switch t.platform {
	case "windows":
		return runtime.GOOS == "windows"
	case "linux":
		return runtime.GOOS == "linux"
	case "macos":
		return runtime.GOOS == "darwin"
	}
	// android, ios are always cross.
	return false
}

func parseArgs(args []string) (target, error) {
	if len(args) < 2 {
		return target{}, fmt.Errorf("requires <platform> <arch>")
	}
	t := target{platform: args[0], arch: args[1]}
	for _, arg := range args[2:] {
		switch arg {
		case "--release":
			t.release = true
		case "--test":
			t.test = true
		default:
			return target{}, fmt.Errorf("unknown flag: %s", arg)
		}
	}
	switch t.platform {
	case "linux", "android", "windows", "macos", "ios":
	default:
		return target{}, fmt.Errorf("unsupported platform: %s", t.platform)
	}
	switch t.arch {
	case "x86_64", "arm64":
	default:
		return target{}, fmt.Errorf("unsupported arch: %s", t.arch)
	}
	return t, nil
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

func printUsage() {
	fmt.Println(`Usage: (from tools-dev/build-cpp/) go run . <platform> <arch> [flags]

Platforms: linux, android, windows, macos, ios
Arches:    x86_64, arm64

Flags:
  --release    Release build (default Debug)
  --test       Build + run tests

Outputs to dist/<platform>-<arch>/{lib,include}/.

Requires the target toolchain in PATH/env: NDK for Android,
MSVC + Windows SDK for Windows (run on a Windows host),
Xcode for Apple targets, etc.
docker/cpp-build/Dockerfile.<platform> provides ready containers
for Linux and Android cross-builds.

Examples:
  go run . linux x86_64 --test
  go run . android arm64 --release
  go run . ios arm64`)
}
