// Package log initializes structured logging using slog.
// It supports dual output: console (text) + daily rotated log files (JSON).
package log

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"time"
)

// Config controls logger behavior.
type Config struct {
	Level   slog.Level // minimum log level (default: Info)
	LogDir  string     // directory for log files (empty = no file output)
	Console bool       // also write to stderr in text format
}

// Init sets up the global slog logger. Call once at startup.
// Returns a cleanup function that closes the log file (if any).
func Init(cfg Config) (cleanup func(), err error) {
	var logFile *os.File

	opts := &slog.HandlerOptions{
		Level:     cfg.Level,
		AddSource: true,
	}

	if cfg.LogDir != "" {
		if err := os.MkdirAll(cfg.LogDir, 0o755); err != nil {
			return nil, fmt.Errorf("log.Init: create log dir: %w", err)
		}
		name := fmt.Sprintf("roulin-server-%s.log", time.Now().Format("2006-01-02"))
		path := filepath.Join(cfg.LogDir, name)
		logFile, err = os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
		if err != nil {
			return nil, fmt.Errorf("log.Init: open log file: %w", err)
		}
	}

	cleanup = func() {
		if logFile != nil {
			logFile.Close()
		}
	}

	var handler slog.Handler
	switch {
	case logFile != nil && cfg.Console:
		handler = &multiHandler{
			handlers: []slog.Handler{
				slog.NewJSONHandler(logFile, opts),
				slog.NewTextHandler(os.Stderr, opts),
			},
		}
	case logFile != nil:
		handler = slog.NewJSONHandler(logFile, opts)
	default:
		handler = slog.NewTextHandler(os.Stderr, opts)
	}

	slog.SetDefault(slog.New(handler))
	return cleanup, nil
}
