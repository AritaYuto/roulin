package local

import (
	"context"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// FileStorage is an FS-persistent Storage. Works as a primary backend (file:// scheme)
// or as a cache layer via storage.NewCachedStorage.
type FileStorage struct {
	baseDir string
}

func init() {
	storage.Register("file", openFile)
}

func openFile(_ context.Context, u *url.URL, _ storage.Options) (storage.Storage, error) {
	// file:///abs/path → path = "/abs/path"; file://./rel → host=".", path="/rel".
	base := u.Path
	if u.Host != "" && u.Host != "." {
		base = filepath.Join(u.Host, base)
	} else if u.Host == "." {
		base = filepath.Join(".", base)
	}
	if base == "" {
		return nil, fmt.Errorf("local.openFile: empty path in %q", u.String())
	}
	return NewFile(base), nil
}

// NewFile roots a FileStorage at baseDir. Subdirectories (blobs/, index/) are
// created lazily on first write.
func NewFile(baseDir string) *FileStorage {
	return &FileStorage{baseDir: baseDir}
}

func (f *FileStorage) PutBlob(_ context.Context, hash string, data []byte) error {
	return f.write(storage.BlobKey(hash), data)
}

func (f *FileStorage) GetBlob(_ context.Context, hash string) ([]byte, error) {
	return f.read(storage.BlobKey(hash))
}

func (f *FileStorage) HasBlob(_ context.Context, hash string) (bool, error) {
	return f.exists(storage.BlobKey(hash))
}

func (f *FileStorage) PutIndex(_ context.Context, rev string, data []byte) error {
	return f.write(storage.IndexKey(rev), data)
}

func (f *FileStorage) GetIndex(_ context.Context, rev string) ([]byte, error) {
	return f.read(storage.IndexKey(rev))
}

func (f *FileStorage) ListIndexRevisions(_ context.Context) ([]storage.ObjectInfo, error) {
	dir := filepath.Join(f.baseDir, "index")
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	var out []storage.ObjectInfo
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		info, err := e.Info()
		if err != nil {
			return nil, err
		}
		out = append(out, storage.ObjectInfo{
			Revision:     e.Name(),
			Size:         info.Size(),
			LastModified: info.ModTime(),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Revision < out[j].Revision })
	return out, nil
}

func (f *FileStorage) write(key string, data []byte) error {
	path := filepath.Join(f.baseDir, key)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return fmt.Errorf("local.FileStorage: mkdir: %w", err)
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		return fmt.Errorf("local.FileStorage: write %s: %w", key, err)
	}
	return nil
}

func (f *FileStorage) read(key string) ([]byte, error) {
	return os.ReadFile(filepath.Join(f.baseDir, key))
}

func (f *FileStorage) exists(key string) (bool, error) {
	_, err := os.Stat(filepath.Join(f.baseDir, key))
	if err != nil {
		if os.IsNotExist(err) {
			return false, nil
		}
		return false, err
	}
	return true, nil
}
