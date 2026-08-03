package build

import (
	"fmt"
	"strings"
	"testing"
)

func TestValidate_HappyPath(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01), Deps: []string{mkHashHex(0x02)}},
		{Name: "bundle-B", BlobHash: mkHash(0x02)},
	}, nil)
	if err := ValidateIndexBytes(basis); err != nil {
		t.Fatalf("self-contained index rejected: %v", err)
	}
}

func TestValidate_DetectsDangling(t *testing.T) {
	// bundle-A depends on hash 0x02, but no entry owns 0x02.
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01), Deps: []string{mkHashHex(0x02)}},
	}, nil)

	err := ValidateIndexBytes(basis)
	if err == nil {
		t.Fatal("dangling dep should be detected")
	}
	if !strings.Contains(err.Error(), "bundle-A") {
		t.Errorf("error should name the offending bundle, got: %v", err)
	}
	if !strings.Contains(err.Error(), mkHashHex(0x02)) {
		t.Errorf("error should include the missing hash, got: %v", err)
	}
	if !strings.Contains(err.Error(), "1 dangling") {
		t.Errorf("error should report total count, got: %v", err)
	}
}

func TestValidate_ReportsMultipleViolationsUpToCap(t *testing.T) {
	// 15 bundles, each depending on a hash no one owns → 15 violations,
	// but the error message truncates at maxDanglingDepsInError (10) and
	// summarises the remainder as "... and 5 more".
	const total = 15
	entries := make([]IndexEntry, total)
	for i := 0; i < total; i++ {
		entries[i] = IndexEntry{
			Name:     fmt.Sprintf("bundle-%d", i),
			BlobHash: mkHash(byte(0x10 + i)),
			Deps:     []string{mkHashHex(byte(0xF0 + i))},
		}
	}
	basis := BuildIndexBytes(entries, nil)

	err := ValidateIndexBytes(basis)
	if err == nil {
		t.Fatal("multiple dangling deps should be detected")
	}
	if !strings.Contains(err.Error(), fmt.Sprintf("%d dangling dep", total)) {
		t.Errorf("error should report total count %d, got: %v", total, err)
	}
	if !strings.Contains(err.Error(), fmt.Sprintf("and %d more", total-maxDanglingDepsInError)) {
		t.Errorf("error should indicate truncation, got: %v", err)
	}
}
