package build

// Address is one addressable asset living inside a blob. Multiple Address
// records share an IndexEntry when they are packaged into the same blob.
type Address struct {
	AddressStr string
	AssetID    string   // engine-native id (Unity AssetGUID, UE FGuid, ...). optional
	Labels     []string // Addressables-compat labels
	Flags      uint8
	KeyID      uint16
	TypeIdxs   []uint32 // indices into Index-level types[]; typically 1 entry
}

// IndexEntry is one blob the parcel tracks. 1 IndexEntry == 1 blob ==
// 1 binary file. Empty Addresses means the blob has no addressable assets
// (= a pure dep target, e.g. an extracted shader / monoscript bundle).
type IndexEntry struct {
	BlobHash  [32]byte // BLAKE3 of the blob bytes
	SizeBytes uint64
	Deps      []string // hex BLAKE3 of dep blobs
	Addresses []Address
	Name      string // bundle name (AssetBundle name). Build-tool identity for
	// incremental merge; runtime does not consume it.
}
