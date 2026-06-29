package storage

import (
	"context"
	"errors"
	"log/slog"
	"os"
)

// CachedStorage wraps a canonical cloud Storage with ordered cache layers (L1..Ln).
// Reads check caches first and backfill on miss. Writes go to cloud first, then caches best-effort.
type CachedStorage struct {
	cloud  Storage
	caches []Storage
}

func NewCachedStorage(cloud Storage, caches ...Storage) *CachedStorage {
	return &CachedStorage{cloud: cloud, caches: caches}
}

// Caches returns the configured cache layers in L1..Ln order.
// Hot-reload endpoints write here so transient bytes never reach the canonical cloud Storage.
func (c *CachedStorage) Caches() []Storage { return c.caches }

func (c *CachedStorage) Cloud() Storage { return c.cloud }

func (c *CachedStorage) PutBlob(ctx context.Context, hash string, data []byte) error {
	if err := c.cloud.PutBlob(ctx, hash, data); err != nil {
		return err
	}
	for _, cache := range c.caches {
		if err := cache.PutBlob(ctx, hash, data); err != nil {
			slog.Warn("cache PutBlob failed", "hash", hash, "err", err)
		}
	}
	return nil
}

func (c *CachedStorage) GetBlob(ctx context.Context, hash string) ([]byte, error) {
	for i, cache := range c.caches {
		if data, err := cache.GetBlob(ctx, hash); err == nil {
			c.backfillBlob(ctx, hash, data, i)
			return data, nil
		}
	}
	data, err := c.cloud.GetBlob(ctx, hash)
	if err != nil {
		return nil, err
	}
	c.backfillBlob(ctx, hash, data, len(c.caches))
	return data, nil
}

func (c *CachedStorage) HasBlob(ctx context.Context, hash string) (bool, error) {
	for _, cache := range c.caches {
		if ok, err := cache.HasBlob(ctx, hash); err == nil && ok {
			return true, nil
		}
	}
	return c.cloud.HasBlob(ctx, hash)
}

func (c *CachedStorage) PutIndex(ctx context.Context, rev string, data []byte) error {
	if err := c.cloud.PutIndex(ctx, rev, data); err != nil {
		return err
	}
	for _, cache := range c.caches {
		if err := cache.PutIndex(ctx, rev, data); err != nil {
			slog.Warn("cache PutIndex failed", "rev", rev, "err", err)
		}
	}
	return nil
}

func (c *CachedStorage) GetIndex(ctx context.Context, rev string) ([]byte, error) {
	for i, cache := range c.caches {
		if data, err := cache.GetIndex(ctx, rev); err == nil {
			c.backfillIndex(ctx, rev, data, i)
			return data, nil
		}
	}
	data, err := c.cloud.GetIndex(ctx, rev)
	if err != nil {
		return nil, err
	}
	c.backfillIndex(ctx, rev, data, len(c.caches))
	return data, nil
}

func (c *CachedStorage) ListIndexRevisions(ctx context.Context) ([]ObjectInfo, error) {
	return c.cloud.ListIndexRevisions(ctx)
}

func (c *CachedStorage) backfillBlob(ctx context.Context, hash string, data []byte, hitAt int) {
	for i := 0; i < hitAt; i++ {
		if err := c.caches[i].PutBlob(ctx, hash, data); err != nil && !errors.Is(err, os.ErrNotExist) {
			slog.Warn("cache backfill PutBlob failed", "layer", i, "hash", hash, "err", err)
		}
	}
}

func (c *CachedStorage) backfillIndex(ctx context.Context, rev string, data []byte, hitAt int) {
	for i := 0; i < hitAt; i++ {
		if err := c.caches[i].PutIndex(ctx, rev, data); err != nil && !errors.Is(err, os.ErrNotExist) {
			slog.Warn("cache backfill PutIndex failed", "layer", i, "rev", rev, "err", err)
		}
	}
}
