package cli

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"

	"github.com/spf13/cobra"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/build"
)

// ---- inspect-parcel --------------------------------------------------------

var inspectParcelCmd = &cobra.Command{
	Use:   "inspect-parcel",
	Short: "Walk a Parcel on a roulin-server and display its contents",
	Long: `Fetches /index/{revision} and each referenced BundleIndex blob via
/blobs/<h[:2]>/<h>, then prints a human-readable summary (or JSON with --json).

Useful for verifying what RoulinBuildScript actually deployed without
needing roulin-core / FlatBuffers tooling.

Example:
  roulin-cli inspect-parcel --base-url http://localhost:8765 --revision 5463184abc...`,
	RunE: runInspectParcel,
}

var (
	inspectFlagBaseURL  string
	inspectFlagRevision string
	inspectFlagJSON     bool
	inspectFlagWithMeta bool
)

func init() {
	inspectParcelCmd.Flags().StringVar(&inspectFlagBaseURL, "base-url", "",
		"roulin-server base URL (required), e.g. http://localhost:8765")
	inspectParcelCmd.Flags().StringVar(&inspectFlagRevision, "revision", "",
		"revision id to inspect (required)")
	inspectParcelCmd.Flags().BoolVar(&inspectFlagJSON, "json", false,
		"emit JSON instead of human-readable text")
	inspectParcelCmd.Flags().BoolVar(&inspectFlagWithMeta, "with-meta", false,
		"also fetch and display each blob's blob_meta sidecar (per-blob dependency data)")
	_ = inspectParcelCmd.MarkFlagRequired("base-url")
	_ = inspectParcelCmd.MarkFlagRequired("revision")
	rootCmd.AddCommand(inspectParcelCmd)
}

// ---- output shapes ---------------------------------------------------------

type inspectAddress struct {
	AddressStr string   `json:"address"`
	AssetID    string   `json:"asset_id,omitempty"`
	Labels     []string `json:"labels,omitempty"`
}

type inspectBlob struct {
	BlobHash  string             `json:"blob_hash"`
	SizeBytes uint64             `json:"size_bytes,omitempty"`
	Deps      []string           `json:"deps"`
	Addresses []inspectAddress   `json:"addresses"`
	Meta      *build.BlobMeta `json:"meta,omitempty"`
}

type inspectResult struct {
	BaseURL  string        `json:"base_url"`
	Revision string        `json:"revision"`
	Blobs    []inspectBlob `json:"blobs"`
}

// ---- run -------------------------------------------------------------------

func runInspectParcel(cmd *cobra.Command, _ []string) error {
	base := strings.TrimRight(inspectFlagBaseURL, "/")
	rev := inspectFlagRevision

	idxBytes, err := httpGetBytes(base + "/index/" + rev)
	if err != nil {
		return fmt.Errorf("fetch index: %w", err)
	}

	entries, _ := build.ParseIndexBytes(idxBytes)

	result := inspectResult{BaseURL: base, Revision: rev}
	result.Blobs = make([]inspectBlob, 0, len(entries))
	for _, e := range entries {
		addrs := make([]inspectAddress, 0, len(e.Addresses))
		for _, a := range e.Addresses {
			addrs = append(addrs, inspectAddress{
				AddressStr: a.AddressStr,
				AssetID:    a.AssetID,
				Labels:     a.Labels,
			})
		}
		deps := e.Deps
		if deps == nil {
			deps = []string{}
		}
		hashHex := hex.EncodeToString(e.BlobHash[:])
		blob := inspectBlob{
			BlobHash:  hashHex,
			SizeBytes: e.SizeBytes,
			Deps:      deps,
			Addresses: addrs,
		}
		if inspectFlagWithMeta {
			// 404 is the expected case for bundles whose blob_meta was
			// never uploaded (SBP-synthesized UnityBuiltIn.bundle /
			// UnityMonoScripts.bundle); leave blob.Meta nil and let
			// the text renderer print "Meta: (none)". Surface other
			// transport errors so they aren't silently swallowed.
			m, err := fetchBlobMeta(base, hashHex)
			switch {
			case err == nil:
				blob.Meta = m
			case isNotFound(err):
				// expected, no-op
			default:
				fmt.Fprintf(cmd.ErrOrStderr(),
					"blob_meta fetch for %s failed: %v\n", hashHex[:12], err)
			}
		}
		result.Blobs = append(result.Blobs, blob)
	}

	if inspectFlagJSON {
		enc := json.NewEncoder(cmd.OutOrStdout())
		enc.SetIndent("", "  ")
		return enc.Encode(result)
	}
	return printInspectText(cmd.OutOrStdout(), result)
}

func printInspectText(w io.Writer, r inspectResult) error {
	fmt.Fprintf(w, "Parcel at %s/index/%s\n", r.BaseURL, r.Revision)
	fmt.Fprintf(w, "Blobs: %d\n\n", len(r.Blobs))
	for _, b := range r.Blobs {
		fmt.Fprintf(w, "  %s\n", b.BlobHash)
		if b.SizeBytes > 0 {
			fmt.Fprintf(w, "    Size: %d bytes\n", b.SizeBytes)
		}
		if len(b.Deps) > 0 {
			fmt.Fprintf(w, "    Dependencies (%d):\n", len(b.Deps))
			for _, d := range b.Deps {
				fmt.Fprintf(w, "      - %s\n", d)
			}
		}
		fmt.Fprintf(w, "    Addresses (%d):\n", len(b.Addresses))
		for _, a := range b.Addresses {
			suffix := ""
			if len(a.Labels) > 0 {
				suffix += "  labels=[" + strings.Join(a.Labels, ",") + "]"
			}
			if a.AssetID != "" {
				suffix += "  id=" + a.AssetID
			}
			fmt.Fprintf(w, "      %-40s%s\n",
				a.AddressStr, suffix)
		}
		if inspectFlagWithMeta {
			if b.Meta != nil {
				printBlobMetaSection(w, b.Meta)
			} else {
				fmt.Fprintln(w, "    Meta: (none)")
			}
		}
		fmt.Fprintln(w)
	}
	return nil
}

func printBlobMetaSection(w io.Writer, m *build.BlobMeta) {
	fmt.Fprintf(w, "    Meta:\n")
	fmt.Fprintf(w, "      body_type: %s\n", m.BodyType)
	if m.UnityBody == nil {
		fmt.Fprintln(w, "      (no unity_body)")
		return
	}
	ub := m.UnityBody
	fmt.Fprintf(w, "      unity_version=%s sbp_version=%s\n", ub.UnityVersion, ub.SbpVersion)
	fmt.Fprintf(w, "      types=%d assets=%d scenes=%d\n",
		len(ub.Types), len(ub.Assets), len(ub.Scenes))
	for i, a := range ub.Assets {
		fmt.Fprintf(w, "      [asset %d] %s\n", i, a.AssetAddress)
		fmt.Fprintf(w, "        included=%d referenced=%d representations=%d referenced_asset_hashes=%d\n",
			len(a.IncludedObjects), len(a.ReferencedObjects), len(a.Representations), len(a.ReferencedAssetHashes))
		fmt.Fprintf(w, "        asset_dependency_hash=%s\n", a.AssetDependencyHash)
	}
	for i, s := range ub.Scenes {
		fmt.Fprintf(w, "      [scene %d] %s\n", i, s.ScenePath)
		fmt.Fprintf(w, "        referenced=%d included_type_idxs=%d referenced_asset_hashes=%d global_usage(uint=%d bool=%d)\n",
			len(s.ReferencedObjects), len(s.IncludedTypeIdxs), len(s.ReferencedAssetHashes),
			len(s.GlobalUsage.UintFields), len(s.GlobalUsage.BoolFields))
	}
}

func fetchBlobMeta(base, hash string) (*build.BlobMeta, error) {
	if len(hash) < 2 {
		return nil, fmt.Errorf("invalid hash: %q", hash)
	}
	url := fmt.Sprintf("%s/blobs_meta/%s/%s", base, hash[:2], hash)
	body, err := httpGetBytes(url)
	if err != nil {
		return nil, err
	}
	var m build.BlobMeta
	if err := json.Unmarshal(body, &m); err != nil {
		return nil, fmt.Errorf("decode: %w", err)
	}
	return &m, nil
}

// ---- HTTP helper -----------------------------------------------------------

func httpGetBytes(url string) ([]byte, error) {
	resp, err := http.Get(url)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusNotFound {
		return nil, errNotFound
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GET %s: %d %s", url, resp.StatusCode, resp.Status)
	}
	return io.ReadAll(resp.Body)
}

// errNotFound is returned by httpGetBytes when the server responds with 404.
// Callers use isNotFound() to distinguish "expected absence" from transport
// errors.
var errNotFound = fmt.Errorf("404 not found")

func isNotFound(err error) bool { return err == errNotFound }
