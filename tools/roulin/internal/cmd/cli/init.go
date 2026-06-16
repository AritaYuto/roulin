package cli

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"
)

var initCmd = &cobra.Command{
	Use:   "init <dir>",
	Short: "Initialize a Parcel directory",
	Long:  "Creates the blobs/ and index/ subdirectories required by a roulin Parcel.",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		dir := args[0]
		for _, sub := range []string{"blobs", "index"} {
			if err := os.MkdirAll(filepath.Join(dir, sub), 0o755); err != nil {
				return fmt.Errorf("init: %w", err)
			}
		}
		fmt.Fprintf(cmd.OutOrStdout(), "Initialized Parcel at %s\n", dir)
		return nil
	},
}
