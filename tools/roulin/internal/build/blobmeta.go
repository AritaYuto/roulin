package build

// FlatBuffers builder/parser for blob_meta sidecars (core/schema/blob_meta.fbs).
// Wire format from the Editor is JSON; SaveBlobMeta encodes to FlatBuffers binary.

import (
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"

	flatbuffers "github.com/google/flatbuffers/go"
	fbs "github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/parcel/generated/roulin_fbs"
)

// BlobMetaPath returns the on-disk path for the FB sidecar of the given
// blob hash. Mirrors the URL layout /blobs_meta/{prefix}/{hash}.
func BlobMetaPath(baseDir, hexHash string) string {
	if len(hexHash) < 2 {
		return filepath.Join(baseDir, "blobs_meta", hexHash+".meta")
	}
	return filepath.Join(baseDir, "blobs_meta", hexHash[:2], hexHash+".meta")
}

// BuildBlobMetaBytes serialises a BlobMeta into a FlatBuffers BlobMeta buffer.
func BuildBlobMetaBytes(m *BlobMeta) []byte {
	if m == nil {
		m = &BlobMeta{}
	}

	b := flatbuffers.NewBuilder(1024)

	// Build the body table first so its offset is known when we open the
	// root BlobMeta. FlatBuffers forbids interleaved table construction.
	var (
		bodyOffset flatbuffers.UOffsetT
		bodyType   = fbs.BlobBodyNONE
	)
	switch m.BodyType {
	case "unity":
		if m.UnityBody != nil {
			bodyOffset = writeUnityBlob(b, m.UnityBody)
			bodyType = fbs.BlobBodyUnityBlob
		}
	case "ue":
		if m.UEBody != nil {
			bodyOffset = writeUEBlob(b, m.UEBody)
			bodyType = fbs.BlobBodyUEBlob
		}
	case "godot":
		if m.GodotBody != nil {
			bodyOffset = writeGodotBlob(b, m.GodotBody)
			bodyType = fbs.BlobBodyGodotBlob
		}
	}

	hashOff := b.CreateByteVector(decodeHexLenient(m.BlobHash))

	fbs.BlobMetaStart(b)
	fbs.BlobMetaAddBlobHash(b, hashOff)
	if bodyOffset != 0 {
		fbs.BlobMetaAddBodyType(b, bodyType)
		fbs.BlobMetaAddBody(b, bodyOffset)
	}
	root := fbs.BlobMetaEnd(b)
	fbs.FinishBlobMetaBuffer(b, root)
	return b.FinishedBytes()
}

func writeUnityBlob(b *flatbuffers.Builder, u *UnityBlob) flatbuffers.UOffsetT {
	unityVerOff := b.CreateString(u.UnityVersion)
	sbpVerOff := b.CreateString(u.SbpVersion)
	builtRevOff := b.CreateString(u.BuiltRevision)

	var typesOff flatbuffers.UOffsetT
	if len(u.Types) > 0 {
		offs := make([]flatbuffers.UOffsetT, len(u.Types))
		for i := len(u.Types) - 1; i >= 0; i-- {
			offs[i] = b.CreateString(u.Types[i])
		}
		fbs.UnityBlobStartTypesVector(b, len(offs))
		for i := len(offs) - 1; i >= 0; i-- {
			b.PrependUOffsetT(offs[i])
		}
		typesOff = b.EndVector(len(offs))
	}

	var assetsOff flatbuffers.UOffsetT
	if len(u.Assets) > 0 {
		assetOffsets := make([]flatbuffers.UOffsetT, len(u.Assets))
		for i := len(u.Assets) - 1; i >= 0; i-- {
			assetOffsets[i] = writeUnityAsset(b, u.Assets[i])
		}
		fbs.UnityBlobStartAssetsVector(b, len(assetOffsets))
		for i := len(assetOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(assetOffsets[i])
		}
		assetsOff = b.EndVector(len(assetOffsets))
	}

	var scenesOff flatbuffers.UOffsetT
	if len(u.Scenes) > 0 {
		sceneOffsets := make([]flatbuffers.UOffsetT, len(u.Scenes))
		for i := len(u.Scenes) - 1; i >= 0; i-- {
			sceneOffsets[i] = writeUnityScene(b, u.Scenes[i])
		}
		fbs.UnityBlobStartScenesVector(b, len(sceneOffsets))
		for i := len(sceneOffsets) - 1; i >= 0; i-- {
			b.PrependUOffsetT(sceneOffsets[i])
		}
		scenesOff = b.EndVector(len(sceneOffsets))
	}

	fbs.UnityBlobStart(b)
	fbs.UnityBlobAddUnityVersion(b, unityVerOff)
	fbs.UnityBlobAddSbpVersion(b, sbpVerOff)
	fbs.UnityBlobAddBuiltRevision(b, builtRevOff)
	if typesOff != 0 {
		fbs.UnityBlobAddTypes(b, typesOff)
	}
	if assetsOff != 0 {
		fbs.UnityBlobAddAssets(b, assetsOff)
	}
	if scenesOff != 0 {
		fbs.UnityBlobAddScenes(b, scenesOff)
	}
	return fbs.UnityBlobEnd(b)
}

func writeUnityScene(b *flatbuffers.Builder, s UnityScene) flatbuffers.UOffsetT {
	guidOff := b.CreateByteVector(decodeHexLenient(s.Guid))
	pathOff := b.CreateString(s.ScenePath)
	depHashOff := b.CreateByteVector(decodeHexLenient(s.PrefabDependencyHash))
	usageBytes, _ := base64.StdEncoding.DecodeString(s.BuildUsageTagSet)
	usageOff := b.CreateByteVector(usageBytes)

	refsOff := writeUnityObjectIdVector(b, s.ReferencedObjects, fbs.UnitySceneStartReferencedObjectsVector)

	var typesOff flatbuffers.UOffsetT
	if len(s.IncludedTypeIdxs) > 0 {
		fbs.UnitySceneStartIncludedTypeIdxsVector(b, len(s.IncludedTypeIdxs))
		for i := len(s.IncludedTypeIdxs) - 1; i >= 0; i-- {
			b.PrependUint32(s.IncludedTypeIdxs[i])
		}
		typesOff = b.EndVector(len(s.IncludedTypeIdxs))
	}

	globalUsageOff := writeUnityBuildUsageTagGlobal(b, s.GlobalUsage)

	fbs.UnitySceneStart(b)
	fbs.UnitySceneAddGuid(b, guidOff)
	fbs.UnitySceneAddScenePath(b, pathOff)
	if refsOff != 0 {
		fbs.UnitySceneAddReferencedObjects(b, refsOff)
	}
	if typesOff != 0 {
		fbs.UnitySceneAddIncludedTypeIdxs(b, typesOff)
	}
	fbs.UnitySceneAddGlobalUsage(b, globalUsageOff)
	fbs.UnitySceneAddBuildUsageTagSet(b, usageOff)
	fbs.UnitySceneAddPrefabDependencyHash(b, depHashOff)
	return fbs.UnitySceneEnd(b)
}

func writeUnityBuildUsageTagGlobal(b *flatbuffers.Builder, g UnityBuildUsageTagGlobal) flatbuffers.UOffsetT {
	var uintOff flatbuffers.UOffsetT
	if len(g.UintFields) > 0 {
		offs := make([]flatbuffers.UOffsetT, len(g.UintFields))
		for i := len(g.UintFields) - 1; i >= 0; i-- {
			nameOff := b.CreateString(g.UintFields[i].Name)
			fbs.UsageGlobalUintFieldStart(b)
			fbs.UsageGlobalUintFieldAddName(b, nameOff)
			fbs.UsageGlobalUintFieldAddValue(b, g.UintFields[i].Value)
			offs[i] = fbs.UsageGlobalUintFieldEnd(b)
		}
		fbs.UnityBuildUsageTagGlobalStartUintFieldsVector(b, len(offs))
		for i := len(offs) - 1; i >= 0; i-- {
			b.PrependUOffsetT(offs[i])
		}
		uintOff = b.EndVector(len(offs))
	}

	var boolOff flatbuffers.UOffsetT
	if len(g.BoolFields) > 0 {
		offs := make([]flatbuffers.UOffsetT, len(g.BoolFields))
		for i := len(g.BoolFields) - 1; i >= 0; i-- {
			nameOff := b.CreateString(g.BoolFields[i].Name)
			fbs.UsageGlobalBoolFieldStart(b)
			fbs.UsageGlobalBoolFieldAddName(b, nameOff)
			fbs.UsageGlobalBoolFieldAddValue(b, g.BoolFields[i].Value)
			offs[i] = fbs.UsageGlobalBoolFieldEnd(b)
		}
		fbs.UnityBuildUsageTagGlobalStartBoolFieldsVector(b, len(offs))
		for i := len(offs) - 1; i >= 0; i-- {
			b.PrependUOffsetT(offs[i])
		}
		boolOff = b.EndVector(len(offs))
	}

	fbs.UnityBuildUsageTagGlobalStart(b)
	if uintOff != 0 {
		fbs.UnityBuildUsageTagGlobalAddUintFields(b, uintOff)
	}
	if boolOff != 0 {
		fbs.UnityBuildUsageTagGlobalAddBoolFields(b, boolOff)
	}
	return fbs.UnityBuildUsageTagGlobalEnd(b)
}

func writeUnityAsset(b *flatbuffers.Builder, a UnityAsset) flatbuffers.UOffsetT {
	guidOff := b.CreateByteVector(decodeHexLenient(a.Guid))
	addressOff := b.CreateString(a.AssetAddress)
	usageBytes, _ := base64.StdEncoding.DecodeString(a.BuildUsageTagSet)
	usageOff := b.CreateByteVector(usageBytes)

	includedOff := writeUnityObjectIdVector(b, a.IncludedObjects, fbs.UnityAssetStartIncludedObjectsVector)
	referencedOff := writeUnityObjectIdVector(b, a.ReferencedObjects, fbs.UnityAssetStartReferencedObjectsVector)
	repsOff := writeUnityObjectIdVector(b, a.Representations, fbs.UnityAssetStartRepresentationsVector)

	fbs.UnityAssetStart(b)
	fbs.UnityAssetAddGuid(b, guidOff)
	fbs.UnityAssetAddAssetAddress(b, addressOff)
	if includedOff != 0 {
		fbs.UnityAssetAddIncludedObjects(b, includedOff)
	}
	if referencedOff != 0 {
		fbs.UnityAssetAddReferencedObjects(b, referencedOff)
	}
	if repsOff != 0 {
		fbs.UnityAssetAddRepresentations(b, repsOff)
	}
	fbs.UnityAssetAddBuildUsageTagSet(b, usageOff)
	return fbs.UnityAssetEnd(b)
}

// writeUnityObjectIdVector writes a [UnityObjectId] vector. startFn is the
// schema-specific StartXxxVector helper (= different name per field).
func writeUnityObjectIdVector(
	b *flatbuffers.Builder,
	objs []UnityObjectId,
	startFn func(builder *flatbuffers.Builder, numElems int) flatbuffers.UOffsetT,
) flatbuffers.UOffsetT {
	if len(objs) == 0 {
		return 0
	}
	objOffsets := make([]flatbuffers.UOffsetT, len(objs))
	for i := len(objs) - 1; i >= 0; i-- {
		objOffsets[i] = writeUnityObjectId(b, objs[i])
	}
	startFn(b, len(objOffsets))
	for i := len(objOffsets) - 1; i >= 0; i-- {
		b.PrependUOffsetT(objOffsets[i])
	}
	return b.EndVector(len(objOffsets))
}

func writeUnityObjectId(b *flatbuffers.Builder, o UnityObjectId) flatbuffers.UOffsetT {
	guidOff := b.CreateByteVector(decodeHexLenient(o.Guid))
	pathOff := b.CreateString(o.FilePath)

	// type_idxs must be written before UnityObjectIdStart (FlatBuffers table-building rule).
	var typeIdxsOff flatbuffers.UOffsetT
	if len(o.TypeIdxs) > 0 {
		fbs.UnityObjectIdStartTypeIdxsVector(b, len(o.TypeIdxs))
		for i := len(o.TypeIdxs) - 1; i >= 0; i-- {
			b.PrependUint32(o.TypeIdxs[i])
		}
		typeIdxsOff = b.EndVector(len(o.TypeIdxs))
	}

	fbs.UnityObjectIdStart(b)
	fbs.UnityObjectIdAddGuid(b, guidOff)
	fbs.UnityObjectIdAddLocalIdentifierInFile(b, o.LocalIdentifierInFile)
	fbs.UnityObjectIdAddFileType(b, o.FileType)
	fbs.UnityObjectIdAddFilePath(b, pathOff)
	if typeIdxsOff != 0 {
		fbs.UnityObjectIdAddTypeIdxs(b, typeIdxsOff)
	}
	return fbs.UnityObjectIdEnd(b)
}

func writeUEBlob(b *flatbuffers.Builder, u *UEBlob) flatbuffers.UOffsetT {
	verOff := b.CreateString(u.EngineVersion)
	fbs.UEBlobStart(b)
	fbs.UEBlobAddEngineVersion(b, verOff)
	return fbs.UEBlobEnd(b)
}

func writeGodotBlob(b *flatbuffers.Builder, g *GodotBlob) flatbuffers.UOffsetT {
	verOff := b.CreateString(g.GodotVersion)
	fbs.GodotBlobStart(b)
	fbs.GodotBlobAddGodotVersion(b, verOff)
	return fbs.GodotBlobEnd(b)
}

// ParseBlobMetaBytes is the inverse of BuildBlobMetaBytes.
func ParseBlobMetaBytes(buf []byte) *BlobMeta {
	raw := fbs.GetRootAsBlobMeta(buf, 0)
	out := &BlobMeta{
		BlobHash: hex.EncodeToString(raw.BlobHashBytes()),
	}

	bodyTable := new(flatbuffers.Table)
	if !raw.Body(bodyTable) {
		return out
	}
	switch raw.BodyType() {
	case fbs.BlobBodyUnityBlob:
		var u fbs.UnityBlob
		u.Init(bodyTable.Bytes, bodyTable.Pos)
		out.BodyType = "unity"
		out.UnityBody = readUnityBlob(&u)
	case fbs.BlobBodyUEBlob:
		var u fbs.UEBlob
		u.Init(bodyTable.Bytes, bodyTable.Pos)
		out.BodyType = "ue"
		out.UEBody = &UEBlob{EngineVersion: string(u.EngineVersion())}
	case fbs.BlobBodyGodotBlob:
		var g fbs.GodotBlob
		g.Init(bodyTable.Bytes, bodyTable.Pos)
		out.BodyType = "godot"
		out.GodotBody = &GodotBlob{GodotVersion: string(g.GodotVersion())}
	}
	return out
}

func readUnityBlob(u *fbs.UnityBlob) *UnityBlob {
	types := make([]string, u.TypesLength())
	for i := 0; i < u.TypesLength(); i++ {
		types[i] = string(u.Types(i))
	}
	assets := make([]UnityAsset, u.AssetsLength())
	var ua fbs.UnityAsset
	for i := 0; i < u.AssetsLength(); i++ {
		if !u.Assets(&ua, i) {
			continue
		}
		assets[i] = UnityAsset{
			Guid:              hex.EncodeToString(ua.GuidBytes()),
			AssetAddress:      string(ua.AssetAddress()),
			IncludedObjects:   readUnityObjectIds(ua.IncludedObjectsLength(), ua.IncludedObjects),
			ReferencedObjects: readUnityObjectIds(ua.ReferencedObjectsLength(), ua.ReferencedObjects),
			Representations:   readUnityObjectIds(ua.RepresentationsLength(), ua.Representations),
			BuildUsageTagSet:  base64.StdEncoding.EncodeToString(ua.BuildUsageTagSetBytes()),
		}
	}
	scenes := make([]UnityScene, u.ScenesLength())
	var us fbs.UnityScene
	for i := 0; i < u.ScenesLength(); i++ {
		if !u.Scenes(&us, i) {
			continue
		}
		scenes[i] = readUnityScene(&us)
	}

	return &UnityBlob{
		UnityVersion:  string(u.UnityVersion()),
		SbpVersion:    string(u.SbpVersion()),
		BuiltRevision: string(u.BuiltRevision()),
		Types:         types,
		Assets:        assets,
		Scenes:        scenes,
	}
}

func readUnityScene(s *fbs.UnityScene) UnityScene {
	var typeIdxs []uint32
	if n := s.IncludedTypeIdxsLength(); n > 0 {
		typeIdxs = make([]uint32, n)
		for i := 0; i < n; i++ {
			typeIdxs[i] = s.IncludedTypeIdxs(i)
		}
	}

	var globalUsage UnityBuildUsageTagGlobal
	var gu fbs.UnityBuildUsageTagGlobal
	if s.GlobalUsage(&gu) != nil {
		if n := gu.UintFieldsLength(); n > 0 {
			globalUsage.UintFields = make([]UsageGlobalUintField, n)
			var f fbs.UsageGlobalUintField
			for i := 0; i < n; i++ {
				if gu.UintFields(&f, i) {
					globalUsage.UintFields[i] = UsageGlobalUintField{
						Name:  string(f.Name()),
						Value: f.Value(),
					}
				}
			}
		}
		if n := gu.BoolFieldsLength(); n > 0 {
			globalUsage.BoolFields = make([]UsageGlobalBoolField, n)
			var f fbs.UsageGlobalBoolField
			for i := 0; i < n; i++ {
				if gu.BoolFields(&f, i) {
					globalUsage.BoolFields[i] = UsageGlobalBoolField{
						Name:  string(f.Name()),
						Value: f.Value(),
					}
				}
			}
		}
	}

	return UnityScene{
		Guid:                 hex.EncodeToString(s.GuidBytes()),
		ScenePath:            string(s.ScenePath()),
		ReferencedObjects:    readUnityObjectIds(s.ReferencedObjectsLength(), s.ReferencedObjects),
		IncludedTypeIdxs:     typeIdxs,
		GlobalUsage:          globalUsage,
		BuildUsageTagSet:     base64.StdEncoding.EncodeToString(s.BuildUsageTagSetBytes()),
		PrefabDependencyHash: hex.EncodeToString(s.PrefabDependencyHashBytes()),
	}
}

func readUnityObjectIds(
	length int,
	accessor func(obj *fbs.UnityObjectId, j int) bool,
) []UnityObjectId {
	if length == 0 {
		return nil
	}
	out := make([]UnityObjectId, length)
	var oi fbs.UnityObjectId
	for i := 0; i < length; i++ {
		if !accessor(&oi, i) {
			continue
		}
		var typeIdxs []uint32
		if n := oi.TypeIdxsLength(); n > 0 {
			typeIdxs = make([]uint32, n)
			for k := 0; k < n; k++ {
				typeIdxs[k] = oi.TypeIdxs(k)
			}
		}
		out[i] = UnityObjectId{
			Guid:                  hex.EncodeToString(oi.GuidBytes()),
			LocalIdentifierInFile: oi.LocalIdentifierInFile(),
			FileType:              oi.FileType(),
			FilePath:              string(oi.FilePath()),
			TypeIdxs:              typeIdxs,
		}
	}
	return out
}

// SaveBlobMeta serialises and writes the FB-encoded sidecar to
// BlobMetaPath(baseDir, hexHash). Creates the prefix dir if missing.
func SaveBlobMeta(m *BlobMeta, baseDir, hexHash string) error {
	path := BlobMetaPath(baseDir, hexHash)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return fmt.Errorf("blobmeta.Save: mkdir: %w", err)
	}
	buf := BuildBlobMetaBytes(m)
	if err := os.WriteFile(path, buf, 0o644); err != nil {
		return fmt.Errorf("blobmeta.Save: write: %w", err)
	}
	return nil
}

// LoadBlobMeta reads + parses the FB sidecar. Returns os.ErrNotExist when
// the file is absent.
func LoadBlobMeta(baseDir, hexHash string) (*BlobMeta, error) {
	path := BlobMetaPath(baseDir, hexHash)
	buf, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return ParseBlobMetaBytes(buf), nil
}

// decodeHexLenient falls back to raw bytes when s is not valid hex.
func decodeHexLenient(s string) []byte {
	if s == "" {
		return nil
	}
	if raw, err := hex.DecodeString(s); err == nil {
		return raw
	}
	return []byte(s)
}
