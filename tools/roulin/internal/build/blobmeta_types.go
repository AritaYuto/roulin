package build

// BlobMeta is the top-level union envelope; body_type discriminates which body sub-table is populated.
type BlobMeta struct {
	BlobHash  string     `json:"blob_hash"`               // 64-char lower-hex BLAKE3
	BodyType  string     `json:"body_type"`               // "unity" | "ue" | "godot"
	UnityBody *UnityBlob `json:"unity_body,omitempty"`
	UEBody    *UEBlob    `json:"ue_body,omitempty"`
	GodotBody *GodotBlob `json:"godot_body,omitempty"`
}

// UnityBlob is the per-blob dependency sidecar for Unity; assets and scenes are siblings within the same entry.
type UnityBlob struct {
	UnityVersion  string       `json:"unity_version"`
	SbpVersion    string       `json:"sbp_version"`
	BuiltRevision string       `json:"built_revision"`
	Types         []string     `json:"types"`
	Assets        []UnityAsset `json:"assets"`
	Scenes        []UnityScene `json:"scenes"`
}

// UnityScene holds one scene's dependency data (IDependencyData.SceneInfo / SceneUsage / DependencyHash).
type UnityScene struct {
	Guid                 string                   `json:"guid"`                   // 32-char hex AssetDB GUID
	ScenePath            string                   `json:"scene_path"`             // SceneDependencyInfo.m_Scene
	ReferencedObjects    []UnityObjectId          `json:"referenced_objects"`     // .m_ReferencedObjects
	IncludedTypeIdxs     []uint32                 `json:"included_type_idxs"`     // .m_IncludedTypes → UnityBlob.Types indices
	GlobalUsage          UnityBuildUsageTagGlobal `json:"global_usage"`           // .m_GlobalUsage
	BuildUsageTagSet     string                   `json:"build_usage_tag_set"`    // base64 of SceneUsage[guid]
	PrefabDependencyHash string                   `json:"prefab_dependency_hash"` // 32-char hex Hash128 (DependencyHash[guid])
}

// UnityBuildUsageTagGlobal mirrors BuildUsageTagGlobal; fields are name-addressed (not positional) to survive future Unity additions without schema churn.
type UnityBuildUsageTagGlobal struct {
	UintFields []UsageGlobalUintField `json:"uint_fields"`
	BoolFields []UsageGlobalBoolField `json:"bool_fields"`
}

type UsageGlobalUintField struct {
	Name  string `json:"name"`  // verbatim Unity field name (e.g. "m_LightmapModesUsed")
	Value uint32 `json:"value"`
}

type UsageGlobalBoolField struct {
	Name  string `json:"name"`
	Value bool   `json:"value"`
}

// UnityAsset holds one asset's dependency data (IDependencyData.AssetInfo / AssetUsage / ExtendedData).
type UnityAsset struct {
	Guid              string          `json:"guid"`                // 32-char lower-hex AssetDB GUID
	AssetAddress      string          `json:"asset_address"`       // SBP AssetLoadInfo.address (Addressables address, or asset path fallback)
	IncludedObjects   []UnityObjectId `json:"included_objects"`
	ReferencedObjects []UnityObjectId `json:"referenced_objects"`
	Representations   []UnityObjectId `json:"representations"`
	BuildUsageTagSet  string          `json:"build_usage_tag_set"` // base64 of BuildUsageTagSet binary
}

// UnityObjectId mirrors SBP's ObjectIdentifier struct.
type UnityObjectId struct {
	Guid                  string   `json:"guid"`                     // 32-char lower-hex
	LocalIdentifierInFile int64    `json:"local_identifier_in_file"`
	FileType              uint8    `json:"file_type"`                // SBP FileType enum
	FilePath              string   `json:"file_path"`                // typically empty; non-empty for built-ins
	TypeIdxs              []uint32 `json:"type_idxs"`                // indices into UnityBlob.Types; slice not scalar — BuildCacheUtility stores Type[] per id, truncating to [0] breaks warm hash equality
}

type UEBlob struct {
	EngineVersion string `json:"engine_version"`
}

type GodotBlob struct {
	GodotVersion string `json:"godot_version"`
}
