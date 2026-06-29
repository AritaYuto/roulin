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
)

func init() {
	inspectParcelCmd.Flags().StringVar(&inspectFlagBaseURL, "base-url", "",
		"roulin-server base URL (required), e.g. http://localhost:8765")
	inspectParcelCmd.Flags().StringVar(&inspectFlagRevision, "revision", "",
		"revision id to inspect (required)")
	inspectParcelCmd.Flags().BoolVar(&inspectFlagJSON, "json", false,
		"emit JSON instead of human-readable text")
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
	BlobHash  string           `json:"blob_hash"`
	Name      string           `json:"name,omitempty"`
	SizeBytes uint64           `json:"size_bytes,omitempty"`
	Deps      []string         `json:"deps"`
	Addresses []inspectAddress `json:"addresses"`
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
		result.Blobs = append(result.Blobs, inspectBlob{
			BlobHash:  hex.EncodeToString(e.BlobHash[:]),
			Name:      e.Name,
			SizeBytes: e.SizeBytes,
			Deps:      deps,
			Addresses: addrs,
		})
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
		if b.Name != "" {
			fmt.Fprintf(w, "  %s  [%s]\n", b.BlobHash, b.Name)
		} else {
			fmt.Fprintf(w, "  %s\n", b.BlobHash)
		}
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
			fmt.Fprintf(w, "      %-40s%s\n", a.AddressStr, suffix)
		}
		fmt.Fprintln(w)
	}
	return nil
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
var errNotFound = fmt.Errorf("404 not found")
