package storage

import (
	"context"
	"fmt"
	"os"
	"sort"
	"strings"
	"sync"
	"time"
)

// FakeStorage is a thread-safe in-memory Storage for tests.
type FakeStorage struct {
	mu      sync.RWMutex
	objects map[string]fakeObj
}

type fakeObj struct {
	data     []byte
	modified time.Time
}

// NewFakeStorage returns an empty in-memory Storage.
func NewFakeStorage() *FakeStorage {
	return &FakeStorage{objects: make(map[string]fakeObj)}
}

func (f *FakeStorage) PutBlob(_ context.Context, hash string, data []byte) error {
	return f.put(BlobKey(hash), data)
}

func (f *FakeStorage) GetBlob(_ context.Context, hash string) ([]byte, error) {
	return f.get(BlobKey(hash))
}

func (f *FakeStorage) HasBlob(_ context.Context, hash string) (bool, error) {
	return f.has(BlobKey(hash)), nil
}

func (f *FakeStorage) PutIndex(_ context.Context, rev string, data []byte) error {
	return f.put(IndexKey(rev), data)
}

func (f *FakeStorage) GetIndex(_ context.Context, rev string) ([]byte, error) {
	return f.get(IndexKey(rev))
}

func (f *FakeStorage) ListIndexRevisions(_ context.Context) ([]ObjectInfo, error) {
	const indexPrefix = "index/"
	f.mu.RLock()
	defer f.mu.RUnlock()
	var out []ObjectInfo
	for k, obj := range f.objects {
		if !strings.HasPrefix(k, indexPrefix) {
			continue
		}
		rev := strings.TrimPrefix(k, indexPrefix)
		if strings.Contains(rev, "/") {
			continue
		}
		out = append(out, ObjectInfo{
			Revision:     rev,
			Size:         int64(len(obj.data)),
			LastModified: obj.modified,
		})
	}
	return out, nil
}

// Keys returns a sorted snapshot of all stored keys.
func (f *FakeStorage) Keys() []string {
	f.mu.RLock()
	defer f.mu.RUnlock()
	out := make([]string, 0, len(f.objects))
	for k := range f.objects {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// Len returns the number of stored objects.
func (f *FakeStorage) Len() int {
	f.mu.RLock()
	defer f.mu.RUnlock()
	return len(f.objects)
}

func (f *FakeStorage) put(key string, data []byte) error {
	cp := make([]byte, len(data))
	copy(cp, data)
	f.mu.Lock()
	defer f.mu.Unlock()
	f.objects[key] = fakeObj{data: cp, modified: time.Now()}
	return nil
}

func (f *FakeStorage) get(key string) ([]byte, error) {
	f.mu.RLock()
	defer f.mu.RUnlock()
	obj, ok := f.objects[key]
	if !ok {
		return nil, fmt.Errorf("storage: %s: %w", key, os.ErrNotExist)
	}
	cp := make([]byte, len(obj.data))
	copy(cp, obj.data)
	return cp, nil
}

func (f *FakeStorage) has(key string) bool {
	f.mu.RLock()
	defer f.mu.RUnlock()
	_, ok := f.objects[key]
	return ok
}
