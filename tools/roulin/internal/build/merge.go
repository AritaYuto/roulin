package build

import (
	"encoding/hex"
	"fmt"
)

// MergeParcel folds an incremental Parcel (p) onto the previous revision's
// Index (basisBytes) and returns the new revision's Index bytes.
//
// Rules:
//   - p.Bundles[] is the delta. Each bundle replaces the prev entry of the
//     same name (or is added if no prev entry by that name exists).
//   - Any prev entry whose name is NOT in p.AllBundleNames is dropped (handles
//     bundle removal across revisions).
//   - Prev entries that survive get their deps[] hash-remapped: any dep blob
//     hash that referred to a bundle present in the delta is rewritten to that
//     delta bundle's new blob hash. This keeps dep edges pointing at the
//     correct revision-current blob.
//
// Returns an error if the delta lists a bundle whose name is empty (no key for
// matching) or if AllBundleNames is empty (would erase everything).
func MergeParcel(p *Parcel, basisBytes []byte) ([]byte, error) {
	if len(p.AllBundleNames) == 0 {
		return nil, fmt.Errorf("incremental publish requires all_bundle_names; got empty")
	}

	deltaByName := make(map[string]Bundle, len(p.Bundles))
	for _, b := range p.Bundles {
		if b.Address == "" {
			return nil, fmt.Errorf("delta bundle has empty address (= bundle name)")
		}
		deltaByName[b.Address] = b
	}

	// old hex blob hash → new hex blob hash, for bundles in the delta whose
	// hash actually changed (= referenced as deps from surviving prev entries).
	oldToNewHash := make(map[string]string, len(p.Bundles))

	prevEntries, prevTypes := ParseIndexBytes(basisBytes)

	prevByName := make(map[string]*IndexEntry, len(prevEntries))
	for i := range prevEntries {
		e := &prevEntries[i]
		if e.Name == "" {
			continue
		}
		prevByName[e.Name] = e
	}

	// name → current-revision blob hash, for resolving DepBundleNames on
	// delta bundles. Delta wins over base for any name in both.
	nameToHash := make(map[string]string, len(p.AllBundleNames))
	for name, pe := range prevByName {
		nameToHash[name] = hex.EncodeToString(pe.BlobHash[:])
	}
	for name, d := range deltaByName {
		nameToHash[name] = d.BlobHash
	}

	// Build oldToNewHash from delta vs prev hashes.
	for name, d := range deltaByName {
		newHash, err := parseHashBytes(d.BlobHash)
		if err != nil {
			return nil, fmt.Errorf("delta bundle %q: %w", name, err)
		}
		if pe, ok := prevByName[name]; ok {
			oldHex := hex.EncodeToString(pe.BlobHash[:])
			newHex := hex.EncodeToString(newHash[:])
			if oldHex != newHex {
				oldToNewHash[oldHex] = newHex
			}
		}
	}

	keepName := make(map[string]struct{}, len(p.AllBundleNames))
	for _, n := range p.AllBundleNames {
		keepName[n] = struct{}{}
	}

	typeIdxByName := make(map[string]uint32, len(prevTypes))
	mergedTypes := append([]string{}, prevTypes...)
	for i, t := range mergedTypes {
		typeIdxByName[t] = uint32(i)
	}
	internType := func(t string) uint32 {
		if t == "" {
			return 0
		}
		if idx, ok := typeIdxByName[t]; ok {
			return idx
		}
		idx := uint32(len(mergedTypes))
		typeIdxByName[t] = idx
		mergedTypes = append(mergedTypes, t)
		return idx
	}

	merged := make([]IndexEntry, 0, len(p.AllBundleNames))
	for _, name := range p.AllBundleNames {
		if d, ok := deltaByName[name]; ok {
			entry, err := bundleToIndexEntry(d, internType, nameToHash)
			if err != nil {
				return nil, fmt.Errorf("delta bundle %q: %w", name, err)
			}
			merged = append(merged, entry)
			continue
		}
		if pe, ok := prevByName[name]; ok {
			carried := *pe
			if len(carried.Deps) > 0 {
				remappedDeps := make([]string, len(carried.Deps))
				for i, d := range carried.Deps {
					if newHex, exists := oldToNewHash[d]; exists {
						remappedDeps[i] = newHex
					} else {
						remappedDeps[i] = d
					}
				}
				carried.Deps = remappedDeps
			}
			merged = append(merged, carried)
			continue
		}
		return nil, fmt.Errorf(
			"bundle %q listed in all_bundle_names is neither in delta nor in base revision",
			name)
	}

	_ = keepName
	return BuildIndexBytes(merged, mergedTypes), nil
}

func bundleToIndexEntry(b Bundle, internType func(string) uint32, nameToHash map[string]string) (IndexEntry, error) {
	h, err := parseHashBytes(b.BlobHash)
	if err != nil {
		return IndexEntry{}, fmt.Errorf("invalid blob hash: %w", err)
	}
	addresses := make([]Address, 0, len(b.Entries))
	for _, e := range b.Entries {
		var typeIdxs []uint32
		if e.AssetType != "" {
			typeIdxs = []uint32{internType(e.AssetType)}
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
		return IndexEntry{}, err
	}
	return IndexEntry{
		BlobHash:  h,
		SizeBytes: b.SizeBytes,
		Deps:      deps,
		Addresses: addresses,
		Name:      b.Address,
	}, nil
}
