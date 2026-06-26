package roulin_test

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/server"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
)

// 64-char lower-hex fixtures.
const (
	bmHash    = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
	bmHashPfx = "ab"
)

// sampleBlobMeta is a RoulinBlobMeta with one Unity asset including a couple
// of ObjectIds across the three SBP roles. Covers the structural shape the
// server must round-trip through FlatBuffer storage.
func sampleBlobMeta(hash string) build.BlobMeta {
	return build.BlobMeta{
		BlobHash: hash,
		BodyType: "unity",
		UnityBody: &build.UnityBlob{
			UnityVersion:  "2022.3.40f1",
			SbpVersion:    "1.21.25",
			BuiltRevision: "rev-001",
			Types: []string{
				"UnityEngine.GameObject, UnityEngine.CoreModule",
				"UnityEngine.Material, UnityEngine.CoreModule",
			},
			Assets: []build.UnityAsset{
				{
					Guid:             "11111111111111111111111111111111",
					AssetAddress:     "Assets/Hero.prefab",
					BuildUsageTagSet: "AQIDBA==", // base64 of {1,2,3,4}
					IncludedObjects: []build.UnityObjectId{
						{
							Guid:                  "11111111111111111111111111111111",
							LocalIdentifierInFile: 21300000,
							FileType:              1,
							FilePath:              "Assets/Hero.prefab",
							TypeIdxs:              []uint32{0},
						},
					},
					ReferencedObjects: []build.UnityObjectId{
						{
							Guid:                  "22222222222222222222222222222222",
							LocalIdentifierInFile: 2100000,
							FileType:              3,
							FilePath:              "library/unity default resources",
							TypeIdxs:              []uint32{1},
						},
					},
					Representations: []build.UnityObjectId{},
				},
			},
		},
	}
}

func setupBlobMetaServer(t *testing.T) (*httptest.Server, string) {
	t.Helper()
	dir := t.TempDir()
	st := local.NewFile(dir)
	w := &server.Writer{Storage: st}
	srv := server.New(st, w, nil, 0)
	ts := httptest.NewServer(srv.Handler)
	t.Cleanup(ts.Close)
	return ts, dir
}

// TestBlobMeta_Roundtrip verifies POST stores the JSON-decoded sidecar as
// FlatBuffer binary on disk and GET reverse-decodes it back to JSON with
// all structural fields preserved.
func TestBlobMeta_Roundtrip(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	in := sampleBlobMeta(bmHash)
	postBlobMeta(t, ts, bmHashPfx, bmHash, in, http.StatusCreated)

	got := getBlobMeta(t, ts, bmHashPfx, bmHash)
	if got.BlobHash != in.BlobHash {
		t.Errorf("BlobHash: %s, want %s", got.BlobHash, in.BlobHash)
	}
	if got.BodyType != in.BodyType {
		t.Errorf("BodyType: %s, want %s", got.BodyType, in.BodyType)
	}
	if got.UnityBody == nil {
		t.Fatal("UnityBody nil")
	}
	if !reflect.DeepEqual(got.UnityBody.Types, in.UnityBody.Types) {
		t.Errorf("Types: %v, want %v", got.UnityBody.Types, in.UnityBody.Types)
	}
	if len(got.UnityBody.Assets) != 1 {
		t.Fatalf("Assets count: %d, want 1", len(got.UnityBody.Assets))
	}
	asset := got.UnityBody.Assets[0]
	wantAsset := in.UnityBody.Assets[0]
	if asset.Guid != wantAsset.Guid {
		t.Errorf("Asset.Guid: %s, want %s", asset.Guid, wantAsset.Guid)
	}
	if asset.AssetAddress != wantAsset.AssetAddress {
		t.Errorf("Asset.AssetAddress: %s, want %s", asset.AssetAddress, wantAsset.AssetAddress)
	}
	if asset.BuildUsageTagSet != wantAsset.BuildUsageTagSet {
		t.Errorf("BuildUsageTagSet: %s, want %s", asset.BuildUsageTagSet, wantAsset.BuildUsageTagSet)
	}
	if got, want := asset.IncludedObjects[0].FileType, uint8(1); got != want {
		t.Errorf("Included[0].FileType: %d, want %d", got, want)
	}
	if got, want := asset.ReferencedObjects[0].FileType, uint8(3); got != want {
		t.Errorf("Referenced[0].FileType: %d, want %d (built-in SerializedAssetType must survive)", got, want)
	}
	if got, want := asset.IncludedObjects[0].LocalIdentifierInFile, int64(21300000); got != want {
		t.Errorf("Included[0].LocalIdentifierInFile: %d, want %d", got, want)
	}
}

// TestBlobMeta_PostResponse reports body_type + asset count.
func TestBlobMeta_PostResponse(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	in := sampleBlobMeta(bmHash)
	body, _ := json.Marshal(in)

	res, err := http.Post(ts.URL+"/blobs_meta/"+bmHashPfx+"/"+bmHash,
		"application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusCreated {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("got %d (%s), want 201", res.StatusCode, raw)
	}
	var resp server.PostBlobMetaResponse
	if err := json.NewDecoder(res.Body).Decode(&resp); err != nil {
		t.Fatal(err)
	}
	if resp.Hash != bmHash {
		t.Errorf("Hash: %s, want %s", resp.Hash, bmHash)
	}
	if resp.BodyType != "unity" {
		t.Errorf("BodyType: %s, want unity", resp.BodyType)
	}
	if resp.Assets != 1 {
		t.Errorf("Assets: %d, want 1", resp.Assets)
	}
}

// TestBlobMeta_GetNotFound returns 404 with a stable error code.
func TestBlobMeta_GetNotFound(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	res, err := http.Get(ts.URL + "/blobs_meta/" + bmHashPfx + "/" + bmHash)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusNotFound {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("got %d (%s), want 404", res.StatusCode, raw)
	}
	var er server.ErrorResponse
	json.NewDecoder(res.Body).Decode(&er)
	if er.Code != "blob_meta_not_found" {
		t.Errorf("error code: %s, want blob_meta_not_found", er.Code)
	}
}

// TestBlobMeta_InvalidHash covers prefix/hash format rejection on both verbs.
func TestBlobMeta_InvalidHash(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	in := sampleBlobMeta(bmHash)
	body, _ := json.Marshal(in)

	cases := []struct {
		name, prefix, hash string
	}{
		{"upper_hash", bmHashPfx, strings.ToUpper(bmHash)},
		{"short_hash", bmHashPfx, "abcd"},
		{"bad_prefix", "abc", bmHash},
		{"non_hex", "zz", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"},
	}
	for _, c := range cases {
		t.Run("GET_"+c.name, func(t *testing.T) {
			res, err := http.Get(ts.URL + "/blobs_meta/" + c.prefix + "/" + c.hash)
			if err != nil {
				t.Fatal(err)
			}
			res.Body.Close()
			if res.StatusCode != http.StatusBadRequest {
				t.Errorf("got %d, want 400", res.StatusCode)
			}
		})
		t.Run("POST_"+c.name, func(t *testing.T) {
			res, err := http.Post(ts.URL+"/blobs_meta/"+c.prefix+"/"+c.hash,
				"application/json", bytes.NewReader(body))
			if err != nil {
				t.Fatal(err)
			}
			res.Body.Close()
			if res.StatusCode != http.StatusBadRequest {
				t.Errorf("got %d, want 400", res.StatusCode)
			}
		})
	}
}

// TestBlobMeta_PrefixMismatch returns 400 when path prefix doesn't match
// the first 2 chars of the hash.
func TestBlobMeta_PrefixMismatch(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	res, err := http.Get(ts.URL + "/blobs_meta/ff/" + bmHash) // hash starts with "ab"
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusBadRequest {
		t.Errorf("got %d, want 400", res.StatusCode)
	}
	var er server.ErrorResponse
	json.NewDecoder(res.Body).Decode(&er)
	if er.Code != "prefix_mismatch" {
		t.Errorf("error code: %s, want prefix_mismatch", er.Code)
	}
}

// TestBlobMeta_Idempotent verifies re-POSTing the same content succeeds
// and the read sees the same structural payload.
func TestBlobMeta_Idempotent(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	in := sampleBlobMeta(bmHash)
	postBlobMeta(t, ts, bmHashPfx, bmHash, in, http.StatusCreated)
	postBlobMeta(t, ts, bmHashPfx, bmHash, in, http.StatusCreated)
	got := getBlobMeta(t, ts, bmHashPfx, bmHash)
	if got.BodyType != "unity" || got.UnityBody == nil || len(got.UnityBody.Assets) != 1 {
		t.Errorf("after re-POST: %+v", got)
	}
}

// TestBlobMeta_PostRejectsInvalidJson surfaces a parse error rather than
// silently storing garbage.
func TestBlobMeta_PostRejectsInvalidJson(t *testing.T) {
	ts, _ := setupBlobMetaServer(t)
	res, err := http.Post(ts.URL+"/blobs_meta/"+bmHashPfx+"/"+bmHash,
		"application/json", bytes.NewReader([]byte("not a json object")))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusBadRequest {
		raw, _ := io.ReadAll(res.Body)
		t.Errorf("got %d (%s), want 400", res.StatusCode, raw)
	}
}

// ---- helpers --------------------------------------------------------------

func postBlobMeta(t *testing.T, ts *httptest.Server, prefix, hash string, m build.BlobMeta, want int) {
	t.Helper()
	body, _ := json.Marshal(m)
	res, err := http.Post(ts.URL+"/blobs_meta/"+prefix+"/"+hash,
		"application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != want {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("POST /blobs_meta/%s/%s: got %d (%s), want %d",
			prefix, hash, res.StatusCode, raw, want)
	}
}

func getBlobMeta(t *testing.T, ts *httptest.Server, prefix, hash string) build.BlobMeta {
	t.Helper()
	res, err := http.Get(ts.URL + "/blobs_meta/" + prefix + "/" + hash)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(res.Body)
		t.Fatalf("GET /blobs_meta/%s/%s: got %d (%s)", prefix, hash, res.StatusCode, raw)
	}
	var m build.BlobMeta
	if err := json.NewDecoder(res.Body).Decode(&m); err != nil {
		t.Fatal(err)
	}
	return m
}
