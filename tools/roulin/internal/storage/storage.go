// Package storage defines the unified read/write interface over the object store.
// Backends register via Register; callers compose cache layers via NewCachedStorage.
package storage

import (
	"context"
	"fmt"
	"net/url"
	"time"
)

// Storage is the unified read/write interface over the object store (blob, index).
// Not-found errors satisfy errors.Is(err, os.ErrNotExist).
type Storage interface {
	PutBlob(ctx context.Context, hash string, data []byte) error
	GetBlob(ctx context.Context, hash string) ([]byte, error)
	HasBlob(ctx context.Context, hash string) (bool, error)

	PutIndex(ctx context.Context, revision string, data []byte) error
	GetIndex(ctx context.Context, revision string) ([]byte, error)
	ListIndexRevisions(ctx context.Context) ([]ObjectInfo, error)
}

// ObjectInfo carries the metadata `revisions list` consumes.
type ObjectInfo struct {
	Revision     string
	Size         int64
	LastModified time.Time
}

func BlobKey(hash string) string { return "blobs/" + hash[:2] + "/" + hash }
func IndexKey(rev string) string { return "index/" + rev }

// Options carries cloud-backend connection parameters.
type Options struct {
	// Endpoint overrides the default AWS endpoint. Set to e.g.
	// "http://localhost:9000" for MinIO.
	Endpoint string
	// PathStyle forces path-style URLs ({endpoint}/{bucket}/…).
	// Required for MinIO; not needed for AWS S3.
	PathStyle bool
	// Region overrides the region from the environment. Defaults to
	// "us-east-1" when empty and Endpoint is set.
	Region string
}

// Factory builds a Storage for one URL.
type Factory func(ctx context.Context, u *url.URL, opts Options) (Storage, error)

var factories = map[string]Factory{}

// Register adds a Factory for a URL scheme; called from backend package init().
func Register(scheme string, f Factory) {
	factories[scheme] = f
}

// Open returns a Storage for the URL. Wrap with NewCachedStorage to add cache layers.
func Open(ctx context.Context, rawURL string, opts Options) (Storage, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, fmt.Errorf("storage.Open: parse %q: %w", rawURL, err)
	}
	f, ok := factories[u.Scheme]
	if !ok {
		return nil, fmt.Errorf("storage.Open: unsupported scheme %q (no factory registered — has the backend package been imported?)", u.Scheme)
	}
	s, err := f(ctx, u, opts)
	if err != nil {
		return nil, fmt.Errorf("storage.Open: %w", err)
	}
	return s, nil
}
