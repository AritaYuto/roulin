package build

import (
	"encoding/hex"
	"fmt"
)

// BuildIndexFromParcel converts a full Parcel to a FlatBuffers Index buffer.
// Dep references in DepBundleNames are resolved against the parcel itself
// (every dep must be a bundle name present in p.Bundles).
func BuildIndexFromParcel(p *Parcel) ([]byte, error) {
	nameToHash := make(map[string]string, len(p.Bundles))
	for _, b := range p.Bundles {
		nameToHash[b.Address] = b.BlobHash
	}

	typeIdxByName := make(map[string]uint32)
	var types []string
	entries := make([]IndexEntry, 0, len(p.Bundles))
	for _, b := range p.Bundles {
		h, err := parseHashBytes(b.BlobHash)
		if err != nil {
			return nil, fmt.Errorf("bundle %q: invalid blob hash: %w", b.Address, err)
		}
		addresses := make([]Address, 0, len(b.Entries))
		for _, e := range b.Entries {
			var typeIdxs []uint32
			if e.AssetType != "" {
				idx, ok := typeIdxByName[e.AssetType]
				if !ok {
					idx = uint32(len(types))
					typeIdxByName[e.AssetType] = idx
					types = append(types, e.AssetType)
				}
				typeIdxs = []uint32{idx}
			}
			addresses = append(addresses, Address{
				AddressStr: e.Address,
				AssetID:    e.AssetID,
				Labels:     e.Labels,
				TypeIdxs:   typeIdxs,
			})
		}
		deps, err := resolveDeps(b, nameToHash)
		if err != nil {
			return nil, fmt.Errorf("bundle %q: %w", b.Address, err)
		}
		entries = append(entries, IndexEntry{
			BlobHash:  h,
			SizeBytes: b.SizeBytes,
			Deps:      deps,
			Addresses: addresses,
			Name:      b.Address,
		})
	}
	return BuildIndexBytes(entries, types), nil
}

// resolveDeps returns the dep blob-hash list for b, combining any pre-resolved
// Dependencies with the resolution of DepBundleNames via nameToHash.
func resolveDeps(b Bundle, nameToHash map[string]string) ([]string, error) {
	if len(b.DepBundleNames) == 0 {
		return b.Dependencies, nil
	}
	out := make([]string, 0, len(b.Dependencies)+len(b.DepBundleNames))
	out = append(out, b.Dependencies...)
	for _, depName := range b.DepBundleNames {
		if depName == "" {
			continue
		}
		hash, ok := nameToHash[depName]
		if !ok {
			return nil, fmt.Errorf("dep bundle name %q not found in parcel + base", depName)
		}
		out = append(out, hash)
	}
	return out, nil
}

func parseHashBytes(s string) ([32]byte, error) {
	var h [32]byte
	raw, err := hex.DecodeString(s)
	if err != nil {
		return h, err
	}
	if len(raw) != 32 {
		return h, fmt.Errorf("must be 32 bytes (got %d)", len(raw))
	}
	copy(h[:], raw)
	return h, nil
}
