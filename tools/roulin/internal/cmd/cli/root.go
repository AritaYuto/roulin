package cli

import (
	"os"

	_ "github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/cloud"
	"github.com/spf13/cobra"
)

var rootCmd = &cobra.Command{
	Use:   "roulin",
	Short: "Asset management CLI for Roulin",
}

func Execute() {
	if err := rootCmd.Execute(); err != nil {
		os.Exit(1)
	}
}

func init() {
	rootCmd.AddCommand(initCmd)
	rootCmd.AddCommand(revisionsCmd)
	rootCmd.AddCommand(watchCmd)
}
