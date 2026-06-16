package servercmd

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	roulinlog "github.com/KirisameMarisa/roulin/tools/roulin/internal/log"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/server"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/serve/sse"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/cache"
	_ "github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/cloud" // register s3:// factory
	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage/local"
	"github.com/spf13/cobra"
)

// shutdownGrace covers the largest expected in-flight request (512 MiB blob upload) plus margin.
const shutdownGrace = 30 * time.Second

// hotReloadFallbackBytes caps the in-process MemoryStorage used when no cache layer is configured.
const hotReloadFallbackBytes = 256 << 20

var rootCmd = &cobra.Command{
	Use:   "roulin-server",
	Short: "Asset pipeline hub for Roulin",
	PersistentPreRunE: func(cmd *cobra.Command, _ []string) error {
		cleanup, err := roulinlog.Init(roulinlog.Config{
			Level:   slog.LevelDebug,
			LogDir:  flagLogDir,
			Console: true,
		})
		if cleanup != nil {
			// Store cleanup for later; cobra doesn't have a built-in post-run for root.
			cmd.Root().PersistentPostRun = func(_ *cobra.Command, _ []string) { cleanup() }
		}
		return err
	},
}

var serveCmd = &cobra.Command{
	Use:   "serve",
	Short: "Start the local asset server",
	Long: `Start an HTTP/2 server backed by an object store (S3 or S3-compatible).

The server is a stateless bypass to the configured storage URL — POST writes
go directly to the store, GET reads pass through with an optional in-memory cache.`,
	RunE: runServe,
}

var (
	flagStorage          string
	flagStorageEndpoint  string
	flagStoragePathStyle bool
	flagStorageRegion    string

	flagCacheMemoryBytes int64
	flagCacheDir         string

	flagPort   int
	flagLogDir string

	flagReadOnly bool
)

func init() {
	rootCmd.PersistentFlags().StringVar(&flagLogDir, "log-dir", "./logs",
		"Log file output directory")

	serveCmd.Flags().StringVar(&flagStorage, "storage", "",
		"Cloud storage URL (s3://bucket/prefix). Required.")
	serveCmd.Flags().StringVar(&flagStorageEndpoint, "storage-endpoint", "",
		"Custom S3 endpoint URL (e.g. http://minio:9000). Only applies to s3:// storage.")
	serveCmd.Flags().BoolVar(&flagStoragePathStyle, "storage-path-style", false,
		"Use path-style S3 URLs. Required for MinIO; not needed for AWS S3.")
	serveCmd.Flags().StringVar(&flagStorageRegion, "storage-region", "",
		"S3 region override. Defaults to env / us-east-1 when --storage-endpoint is set.")

	serveCmd.Flags().Int64Var(&flagCacheMemoryBytes, "cache-memory", 0,
		"In-memory LRU cache (L1) size in bytes. 0 disables.")
	serveCmd.Flags().StringVar(&flagCacheDir, "cache-dir", "",
		"FS cache (L2) directory, behind the in-memory L1. Empty disables.")

	serveCmd.Flags().IntVar(&flagPort, "port", 8765, "Listen port")
	serveCmd.Flags().BoolVar(&flagReadOnly, "read-only", false,
		"Disable POST endpoints; serve GET / HEAD only.")

	rootCmd.AddCommand(serveCmd)
}

// Execute runs the root command.
func Execute() {
	if err := rootCmd.Execute(); err != nil {
		os.Exit(1)
	}
}

func runServe(cmd *cobra.Command, _ []string) error {
	if flagStorage == "" {
		return fmt.Errorf("--storage is required (e.g. s3://roulin-test)")
	}

	ctx := context.Background()
	cloud, err := storage.Open(ctx, flagStorage, storage.Options{
		Endpoint:  flagStorageEndpoint,
		PathStyle: flagStoragePathStyle,
		Region:    flagStorageRegion,
	})
	if err != nil {
		return fmt.Errorf("open storage: %w", err)
	}

	var caches []storage.Storage
	if flagCacheMemoryBytes > 0 {
		caches = append(caches, cache.NewMemory(flagCacheMemoryBytes))
	}
	if flagCacheDir != "" {
		caches = append(caches, local.NewFile(flagCacheDir))
	}
	var store storage.Storage = cloud
	if len(caches) > 0 {
		store = storage.NewCachedStorage(cloud, caches...)
	}

	var writer *server.Writer
	if !flagReadOnly {
		// HotReloadStorage targets L1 cache when available; otherwise a fresh MemoryStorage
		// so hot-reload writes never reach the canonical cloud store.
		var hotReload storage.Storage
		if cs, ok := store.(*storage.CachedStorage); ok && len(cs.Caches()) > 0 {
			hotReload = cs.Caches()[0]
		} else {
			hotReload = cache.NewMemory(hotReloadFallbackBytes)
		}
		writer = &server.Writer{
			Storage:          store,
			HotReloadStorage: hotReload,
			Broadcaster:      sse.New(),
		}
	}

	srv := server.New(store, writer, flagPort)
	slog.Info("server started",
		"port", flagPort,
		"storage", flagStorage,
		"endpoint", flagStorageEndpoint,
		"cache_memory_bytes", flagCacheMemoryBytes,
		"cache_dir", flagCacheDir,
		"writable", writer != nil,
	)

	sigctx, stop := signal.NotifyContext(ctx, os.Interrupt, syscall.SIGTERM)
	defer stop()

	serveErr := make(chan error, 1)
	go func() {
		err := srv.ListenAndServe()
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			serveErr <- err
		}
		close(serveErr)
	}()

	select {
	case err := <-serveErr:
		// Server exited on its own (e.g. bind failure); skip Shutdown.
		return err
	case <-sigctx.Done():
		slog.Info("shutdown signal received, draining...", "grace", shutdownGrace)
	}

	shutdownCtx, cancel := context.WithTimeout(ctx, shutdownGrace)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		slog.Error("server shutdown error", "err", err)
	}

	slog.Info("server stopped cleanly")
	return nil
}
