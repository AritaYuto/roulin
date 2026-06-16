package cli

import (
	"context"
	"fmt"
	"sort"
	"time"

	"github.com/spf13/cobra"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// ---- revisions (parent) ----------------------------------------------------

var revisionsCmd = &cobra.Command{
	Use:   "revisions",
	Short: "Manage revisions on a remote",
}

// ---- revisions list --------------------------------------------------------

var revisionsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List revisions stored on a remote",
	RunE:  runRevisionsList,
}

var (
	revisionsListFlagStorage  string
	revisionsListFlagEndpoint string
	revisionsListFlagPathStyle bool
)

func init() {
	revisionsListCmd.Flags().StringVar(&revisionsListFlagStorage, "storage", "",
		"Storage URL, e.g. s3://bucket/prefix (required)")
	revisionsListCmd.Flags().StringVar(&revisionsListFlagEndpoint, "endpoint", "",
		"Custom S3 endpoint URL (e.g. http://localhost:9000 for MinIO)")
	revisionsListCmd.Flags().BoolVar(&revisionsListFlagPathStyle, "path-style", false,
		"Use path-style S3 URLs (required for MinIO)")
	_ = revisionsListCmd.MarkFlagRequired("storage")

	revisionsCmd.AddCommand(revisionsListCmd)
}

func runRevisionsList(cmd *cobra.Command, _ []string) error {
	ctx := context.Background()
	st, err := storage.Open(ctx, revisionsListFlagStorage, storage.Options{
		Endpoint:  revisionsListFlagEndpoint,
		PathStyle: revisionsListFlagPathStyle,
	})
	if err != nil {
		return err
	}

	revs, err := st.ListIndexRevisions(ctx)
	if err != nil {
		return err
	}
	// Sort newest-first by last-modified.
	sort.Slice(revs, func(i, j int) bool {
		return revs[i].LastModified.After(revs[j].LastModified)
	})

	fmt.Fprintf(cmd.OutOrStdout(), "Revisions at %s:\n", revisionsListFlagStorage)
	for _, r := range revs {
		age := time.Since(r.LastModified).Round(time.Second)
		fmt.Fprintf(cmd.OutOrStdout(), "  %-48s  %s (%s ago)\n",
			r.Revision, r.LastModified.Format("2006-01-02 15:04"), age)
	}
	return nil
}
