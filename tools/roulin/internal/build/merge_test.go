package build

import (
	"encoding/hex"
	"sort"
	"strings"
	"testing"
)

// mkHash returns a 32-byte hash filled with a single byte value. Callers
// use distinct bytes to guarantee each bundle in a test gets a unique hash.
func mkHash(fill byte) [32]byte {
	var hash [32]byte
	for i := range hash {
		hash[i] = fill
	}
	return hash
}

func mkHashHex(fill byte) string {
	hash := mkHash(fill)
	return hex.EncodeToString(hash[:])
}

func findEntry(entries []IndexEntry, name string) *IndexEntry {
	for i := range entries {
		if entries[i].Name == name {
			return &entries[i]
		}
	}
	return nil
}

// ---- 3-way merge cardinals -----------------------------------------------

func TestMerge_KeepsUnchanged(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01), SizeBytes: 100},
		{Name: "bundle-B", BlobHash: mkHash(0x02), SizeBytes: 200},
	}, nil)
	parcel := &Parcel{
		Bundles:        nil,
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	if len(entries) != 2 {
		t.Fatalf("entries = %d, want 2", len(entries))
	}
	if entry := findEntry(entries, "bundle-A"); entry == nil || entry.SizeBytes != 100 {
		t.Errorf("bundle-A missing or size wrong: %+v", entry)
	}
	if entry := findEntry(entries, "bundle-B"); entry == nil || entry.SizeBytes != 200 {
		t.Errorf("bundle-B missing or size wrong: %+v", entry)
	}
}

func TestMerge_AddsNewFromDelta(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01)},
	}, nil)
	parcel := &Parcel{
		Bundles: []Bundle{
			{
				Address:   "bundle-B",
				BlobHash:  mkHashHex(0x02),
				SizeBytes: 42,
				Entries:   []Entry{{Address: "addr/x"}},
			},
		},
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	added := findEntry(entries, "bundle-B")
	if added == nil {
		t.Fatal("bundle-B missing")
	}
	if added.SizeBytes != 42 {
		t.Errorf("SizeBytes = %d, want 42", added.SizeBytes)
	}
	if len(added.Addresses) != 1 || added.Addresses[0].AddressStr != "addr/x" {
		t.Errorf("Addresses = %+v", added.Addresses)
	}
	if findEntry(entries, "bundle-A") == nil {
		t.Error("bundle-A should be carried")
	}
}

func TestMerge_UpdatesExistingWithDelta(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01), SizeBytes: 100},
	}, nil)
	parcel := &Parcel{
		Bundles: []Bundle{
			{
				Address:   "bundle-A",
				BlobHash:  mkHashHex(0x99),
				SizeBytes: 999,
			},
		},
		AllBundleNames: []string{"bundle-A"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	entry := findEntry(entries, "bundle-A")
	if entry == nil {
		t.Fatal("bundle-A missing")
	}
	if entry.BlobHash != mkHash(0x99) {
		t.Errorf("BlobHash = %x, want %x", entry.BlobHash, mkHash(0x99))
	}
	if entry.SizeBytes != 999 {
		t.Errorf("SizeBytes = %d, want 999", entry.SizeBytes)
	}
}

func TestMerge_RemovesBundleAbsentFromAllBundleNames(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-A", BlobHash: mkHash(0x01)},
		{Name: "bundle-old", BlobHash: mkHash(0xFE)},
	}, nil)
	parcel := &Parcel{
		Bundles:        nil,
		AllBundleNames: []string{"bundle-A"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	if len(entries) != 1 {
		t.Fatalf("entries = %d, want 1 (bundle-old should be dropped)", len(entries))
	}
	if findEntry(entries, "bundle-old") != nil {
		t.Error("bundle-old should be removed")
	}
}

// ---- dep hash remap -------------------------------------------------------

func TestMerge_RemapsDepHashWhenDepUpdatedInDelta(t *testing.T) {
	// bundle-A carries over unchanged but depends on bundle-B, whose hash
	// changes in the delta. The carried A's Deps must be rewritten to the
	// new hash so runtime resolution finds the current blob.
	basis := BuildIndexBytes([]IndexEntry{
		{
			Name: "bundle-A", BlobHash: mkHash(0x01),
			Deps: []string{mkHashHex(0x02)},
		},
		{Name: "bundle-B", BlobHash: mkHash(0x02)},
	}, nil)
	parcel := &Parcel{
		Bundles: []Bundle{
			{Address: "bundle-B", BlobHash: mkHashHex(0x22)},
		},
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	carried := findEntry(entries, "bundle-A")
	if carried == nil {
		t.Fatal("bundle-A missing")
	}
	if len(carried.Deps) != 1 || carried.Deps[0] != mkHashHex(0x22) {
		t.Errorf("bundle-A.Deps = %v, want [%s] (new hash of bundle-B)",
			carried.Deps, mkHashHex(0x22))
	}
}

func TestMerge_KeepsDepHashWhenDepUnchanged(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{
			Name: "bundle-A", BlobHash: mkHash(0x01),
			Deps: []string{mkHashHex(0x02)},
		},
		{Name: "bundle-B", BlobHash: mkHash(0x02)},
	}, nil)
	parcel := &Parcel{
		Bundles:        nil,
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	carried := findEntry(entries, "bundle-A")
	if carried == nil || len(carried.Deps) != 1 || carried.Deps[0] != mkHashHex(0x02) {
		t.Errorf("bundle-A.Deps = %v, want [%s]", carried.Deps, mkHashHex(0x02))
	}
}

// ---- error paths ---------------------------------------------------------

func TestMerge_ErrorsWhenAllBundleNamesEmpty(t *testing.T) {
	basis := BuildIndexBytes(nil, nil)
	_, err := MergeParcel(&Parcel{
		Bundles:        nil,
		AllBundleNames: nil,
	}, basis)
	if err == nil {
		t.Fatal("want error, got nil")
	}
	if !strings.Contains(err.Error(), "all_bundle_names") {
		t.Errorf("error should mention all_bundle_names, got: %v", err)
	}
}

func TestMerge_ErrorsWhenNameNeitherInDeltaNorBase(t *testing.T) {
	// Regression: the exact scenario incremental publish hit when a bundle
	// existed in HEAD's Addressables walk but neither the current build's
	// SBP delta nor the base Index carried it.
	basis := BuildIndexBytes(nil, nil)
	_, err := MergeParcel(&Parcel{
		Bundles:        nil,
		AllBundleNames: []string{"phantom-bundle"},
	}, basis)
	if err == nil {
		t.Fatal("want error, got nil")
	}
	if !strings.Contains(err.Error(), "neither in delta nor in base") {
		t.Errorf("error should call out neither/nor, got: %v", err)
	}
}

func TestMerge_ErrorsWhenDeltaBundleAddressEmpty(t *testing.T) {
	basis := BuildIndexBytes(nil, nil)
	_, err := MergeParcel(&Parcel{
		Bundles:        []Bundle{{Address: "", BlobHash: mkHashHex(0x01)}},
		AllBundleNames: []string{"anything"},
	}, basis)
	if err == nil {
		t.Fatal("want error, got nil")
	}
	if !strings.Contains(err.Error(), "empty address") {
		t.Errorf("error should mention empty address, got: %v", err)
	}
}

func TestMerge_ErrorsWhenDeltaBundleHashInvalid(t *testing.T) {
	basis := BuildIndexBytes(nil, nil)
	_, err := MergeParcel(&Parcel{
		Bundles:        []Bundle{{Address: "bundle-A", BlobHash: "not-hex"}},
		AllBundleNames: []string{"bundle-A"},
	}, basis)
	if err == nil {
		t.Fatal("want error, got nil")
	}
}

// ---- delta with DepBundleNames -------------------------------------------

func TestMerge_ResolvesDepBundleNamesFromDelta(t *testing.T) {
	// Delta bundle depends on another delta bundle by name; server must
	// resolve the name to the current-revision hash.
	basis := BuildIndexBytes(nil, nil)
	parcel := &Parcel{
		Bundles: []Bundle{
			{
				Address:        "bundle-A",
				BlobHash:       mkHashHex(0x01),
				DepBundleNames: []string{"bundle-B"},
			},
			{Address: "bundle-B", BlobHash: mkHashHex(0x02)},
		},
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	entryA := findEntry(entries, "bundle-A")
	if entryA == nil {
		t.Fatal("bundle-A missing")
	}
	if len(entryA.Deps) != 1 || entryA.Deps[0] != mkHashHex(0x02) {
		t.Errorf("bundle-A.Deps = %v, want [%s]", entryA.Deps, mkHashHex(0x02))
	}
}

func TestMerge_ResolvesDepBundleNamesFromBase(t *testing.T) {
	// Delta bundle depends on a base-only bundle by name; server must
	// resolve to the base bundle's hash (still valid in the new revision
	// because base bundle is carried over).
	basis := BuildIndexBytes([]IndexEntry{
		{Name: "bundle-B", BlobHash: mkHash(0x02)},
	}, nil)
	parcel := &Parcel{
		Bundles: []Bundle{
			{
				Address:        "bundle-A",
				BlobHash:       mkHashHex(0x01),
				DepBundleNames: []string{"bundle-B"},
			},
		},
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	entries, _ := ParseIndexBytes(merged)
	entryA := findEntry(entries, "bundle-A")
	if entryA == nil {
		t.Fatal("bundle-A missing")
	}
	if len(entryA.Deps) != 1 || entryA.Deps[0] != mkHashHex(0x02) {
		t.Errorf("bundle-A.Deps = %v, want [%s] (base hash of bundle-B)",
			entryA.Deps, mkHashHex(0x02))
	}
}

// ---- type intern table ---------------------------------------------------

func TestMerge_CarriesBaseTypesAndAddsDeltaTypes(t *testing.T) {
	basis := BuildIndexBytes([]IndexEntry{
		{
			Name: "bundle-A", BlobHash: mkHash(0x01),
			Addresses: []Address{{AddressStr: "addr/a", TypeIdxs: []uint32{0}}},
		},
	}, []string{"OldType"})
	parcel := &Parcel{
		Bundles: []Bundle{
			{
				Address:  "bundle-B",
				BlobHash: mkHashHex(0x02),
				Entries:  []Entry{{Address: "addr/b", AssetType: "NewType"}},
			},
		},
		AllBundleNames: []string{"bundle-A", "bundle-B"},
	}

	merged, err := MergeParcel(parcel, basis)
	if err != nil {
		t.Fatal(err)
	}
	_, types := ParseIndexBytes(merged)
	got := append([]string(nil), types...)
	sort.Strings(got)
	want := []string{"NewType", "OldType"}
	if len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Errorf("types = %v, want %v", got, want)
	}
}
