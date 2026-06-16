// Package cache provides in-process Storage implementations for use as cache layers.
package cache

import (
	"container/list"
	"context"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

// MemoryStorage is a thread-safe, size-bounded in-memory Storage with LRU eviction.
// Items larger than MaxBytes are never cached. Typical role: L1 cache in front of cloud Storage.
type MemoryStorage struct {
	mu       sync.Mutex
	items    map[string]*list.Element
	order    *list.List // front = most recently used
	size     int64
	maxBytes int64
}

type memEntry struct {
	key      string
	data     []byte
	modified time.Time
}

// NewMemory creates a MemoryStorage capped at maxBytes. Zero or negative disables the cap.
func NewMemory(maxBytes int64) *MemoryStorage {
	return &MemoryStorage{
		items:    make(map[string]*list.Element),
		order:    list.New(),
		maxBytes: maxBytes,
	}
}

func (m *MemoryStorage) PutBlob(_ context.Context, hash string, data []byte) error {
	m.put(storage.BlobKey(hash), data)
	return nil
}

func (m *MemoryStorage) GetBlob(_ context.Context, hash string) ([]byte, error) {
	return m.get(storage.BlobKey(hash))
}

func (m *MemoryStorage) HasBlob(_ context.Context, hash string) (bool, error) {
	return m.has(storage.BlobKey(hash)), nil
}

func (m *MemoryStorage) PutIndex(_ context.Context, rev string, data []byte) error {
	m.put(storage.IndexKey(rev), data)
	return nil
}

func (m *MemoryStorage) GetIndex(_ context.Context, rev string) ([]byte, error) {
	return m.get(storage.IndexKey(rev))
}

func (m *MemoryStorage) ListIndexRevisions(_ context.Context) ([]storage.ObjectInfo, error) {
	const indexPrefix = "index/"
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []storage.ObjectInfo
	for k, el := range m.items {
		if !strings.HasPrefix(k, indexPrefix) {
			continue
		}
		rev := strings.TrimPrefix(k, indexPrefix)
		if strings.Contains(rev, "/") {
			continue
		}
		e := el.Value.(*memEntry)
		out = append(out, storage.ObjectInfo{
			Revision:     rev,
			Size:         int64(len(e.data)),
			LastModified: e.modified,
		})
	}
	return out, nil
}

func (m *MemoryStorage) PutBlobMeta(_ context.Context, hash string, data []byte) error {
	m.put(storage.BlobMetaKey(hash), data)
	return nil
}

func (m *MemoryStorage) GetBlobMeta(_ context.Context, hash string) ([]byte, error) {
	return m.get(storage.BlobMetaKey(hash))
}

func (m *MemoryStorage) ListBlobMetaHashes(_ context.Context) ([]string, error) {
	const prefix = "blobs_meta/"
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []string
	for k := range m.items {
		if !strings.HasPrefix(k, prefix) {
			continue
		}
		rel := strings.TrimPrefix(k, prefix)
		slash := strings.IndexByte(rel, '/')
		if slash < 0 {
			continue
		}
		hash := rel[slash+1:]
		if hash == "" || strings.Contains(hash, "/") {
			continue
		}
		out = append(out, hash)
	}
	return out, nil
}

func (m *MemoryStorage) put(key string, data []byte) {
	dataSize := int64(len(data))
	if m.maxBytes > 0 && dataSize > m.maxBytes {
		return
	}
	cp := make([]byte, len(data))
	copy(cp, data)

	m.mu.Lock()
	defer m.mu.Unlock()

	if el, ok := m.items[key]; ok {
		old := el.Value.(*memEntry)
		m.size -= int64(len(old.data))
		old.data = cp
		old.modified = time.Now()
		m.size += dataSize
		m.order.MoveToFront(el)
		m.evict()
		return
	}

	if m.maxBytes > 0 {
		for m.size+dataSize > m.maxBytes && m.order.Len() > 0 {
			m.removeLRU()
		}
	}
	el := m.order.PushFront(&memEntry{key: key, data: cp, modified: time.Now()})
	m.items[key] = el
	m.size += dataSize
}

func (m *MemoryStorage) get(key string) ([]byte, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	el, ok := m.items[key]
	if !ok {
		return nil, fmt.Errorf("cache.MemoryStorage: %s: %w", key, os.ErrNotExist)
	}
	m.order.MoveToFront(el)
	e := el.Value.(*memEntry)
	cp := make([]byte, len(e.data))
	copy(cp, e.data)
	return cp, nil
}

func (m *MemoryStorage) has(key string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	_, ok := m.items[key]
	return ok
}

func (m *MemoryStorage) evict() {
	if m.maxBytes <= 0 {
		return
	}
	for m.size > m.maxBytes && m.order.Len() > 0 {
		m.removeLRU()
	}
}

func (m *MemoryStorage) removeLRU() {
	el := m.order.Back()
	if el == nil {
		return
	}
	e := m.order.Remove(el).(*memEntry)
	delete(m.items, e.key)
	m.size -= int64(len(e.data))
}
