package build

import (
	"encoding/hex"
	"fmt"
	"strings"
)

// DanglingDep is a dep hash with no owning entry in the same Index.
type DanglingDep struct {
	BundleName string
	DepHash    string
}

// One missing SBP-generated bundle can yield 80+ danglers; cap so the signal isn't buried.
const maxDanglingDepsInError = 10

// ValidateIndexBytes rejects an Index whose deps point at blob hashes no entry owns —
// happens when the client's AllBundleNames omitted a bundle still referenced by carry.
func ValidateIndexBytes(buf []byte) error {
	entries, _ := ParseIndexBytes(buf)

	ownedHashes := make(map[string]struct{}, len(entries))
	for _, entry := range entries {
		ownedHashes[hex.EncodeToString(entry.BlobHash[:])] = struct{}{}
	}

	var violations []DanglingDep
	for _, entry := range entries {
		for _, depHex := range entry.Deps {
			if _, ok := ownedHashes[depHex]; ok {
				continue
			}
			violations = append(violations, DanglingDep{
				BundleName: entry.Name,
				DepHash:    depHex,
			})
		}
	}
	if len(violations) == 0 {
		return nil
	}

	var lines []string
	for i, v := range violations {
		if i >= maxDanglingDepsInError {
			lines = append(lines, fmt.Sprintf("  ... and %d more", len(violations)-maxDanglingDepsInError))
			break
		}
		lines = append(lines, fmt.Sprintf("  %q → %s", v.BundleName, v.DepHash))
	}
	return fmt.Errorf(
		"catalog has %d dangling dep(s):\n%s",
		len(violations),
		strings.Join(lines, "\n"))
}
