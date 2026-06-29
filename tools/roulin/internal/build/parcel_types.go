package build

// Parcel is the body of POST /parcels/{revision}.
//
// Two publishing modes:
//
//   - Full publish (BaseRevision empty): Bundles[] must cover every bundle that
//     the new revision should contain. AllBundleNames is ignored.
//   - Incremental publish (BaseRevision set): Bundles[] is the delta — only
//     bundles SBP regenerated this build. AllBundleNames is the complete set
//     of bundle names that should exist in the new revision (= the current
//     Addressables walk). The server keeps any base entry whose name is in
//     AllBundleNames and not overridden by the delta, drops the rest, and
//     remaps deps[] entries whose blob hash refers to a bundle whose hash
//     changed in the delta.
type Parcel struct {
	Bundles         []Bundle `json:"bundles"`
	BaseRevision    string   `json:"base_revision,omitempty"`
	AllBundleNames  []string `json:"all_bundle_names,omitempty"`
}

// Bundle describes one blob to register in the revision Index.
//
// Dependency representation: clients normally provide DepBundleNames (bundle
// names); the server resolves each name to its current revision's blob hash
// using the delta + base Index. Dependencies (hex hashes) is retained for
// direct/legacy callers that pre-resolve.
type Bundle struct {
	Address        string   `json:"address"`                     // key in the root Index (e.g. "ui/icons")
	BlobHash       string   `json:"blob_hash"`                   // lower-hex BLAKE3; blob must be uploaded before posting the parcel
	SizeBytes      uint64   `json:"size_bytes,omitempty"`        // byte length of the bundle binary; shared by all Entries
	Entries        []Entry  `json:"entries"`                     // addressable assets inside this bundle
	Dependencies   []string `json:"dependencies,omitempty"`      // hex BLAKE3 hashes of bundles this bundle depends on (pre-resolved)
	DepBundleNames []string `json:"dep_bundle_names,omitempty"`  // bundle names; server resolves to blob hashes
}

// Entry is one addressable asset inside a Bundle.
type Entry struct {
	Address   string   `json:"address"`
	Labels    []string `json:"labels,omitempty"`
	AssetID   string   `json:"asset_id,omitempty"`
	AssetType string   `json:"asset_type,omitempty"` // engine-specific type identifier
}
