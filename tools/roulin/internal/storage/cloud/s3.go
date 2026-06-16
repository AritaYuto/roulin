// Package cloud provides Storage backends for external object stores.
package cloud

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"strings"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/config"
	"github.com/aws/aws-sdk-go-v2/service/s3"
	"github.com/aws/aws-sdk-go-v2/service/s3/types"

	"github.com/KirisameMarisa/roulin/tools/roulin/internal/storage"
)

func init() {
	storage.Register("s3", openS3)
}

// S3Storage implements Storage against an S3 bucket. Supports AWS S3 and S3-compatible backends.
type S3Storage struct {
	client *s3.Client
	bucket string
	prefix string
}

// NewS3 constructs an S3Storage. Callers usually go through
// storage.Open("s3://...") which dispatches to the registered factory.
func NewS3(ctx context.Context, bucket, prefix string, opts storage.Options) (*S3Storage, error) {
	cfgOpts := []func(*config.LoadOptions) error{}
	if opts.Region != "" {
		cfgOpts = append(cfgOpts, config.WithRegion(opts.Region))
	} else if opts.Endpoint != "" {
		// AWS SDK requires a region even for non-AWS endpoints.
		cfgOpts = append(cfgOpts, config.WithRegion("us-east-1"))
	}
	cfg, err := config.LoadDefaultConfig(ctx, cfgOpts...)
	if err != nil {
		return nil, err
	}
	s3Opts := []func(*s3.Options){}
	if opts.Endpoint != "" {
		ep := opts.Endpoint
		s3Opts = append(s3Opts, func(o *s3.Options) { o.BaseEndpoint = &ep })
	}
	if opts.PathStyle {
		s3Opts = append(s3Opts, func(o *s3.Options) { o.UsePathStyle = true })
	}
	return &S3Storage{
		client: s3.NewFromConfig(cfg, s3Opts...),
		bucket: bucket,
		prefix: prefix,
	}, nil
}

// openS3 is the factory registered for the s3:// scheme.
func openS3(ctx context.Context, u *url.URL, opts storage.Options) (storage.Storage, error) {
	prefix := strings.TrimPrefix(strings.TrimSuffix(u.Path, "/"), "/")
	return NewS3(ctx, u.Host, prefix, opts)
}

func (s *S3Storage) fullKey(key string) string {
	if s.prefix == "" {
		return key
	}
	return s.prefix + "/" + key
}

func (s *S3Storage) PutBlob(ctx context.Context, hash string, data []byte) error {
	return s.put(ctx, storage.BlobKey(hash), data)
}

func (s *S3Storage) GetBlob(ctx context.Context, hash string) ([]byte, error) {
	return s.get(ctx, storage.BlobKey(hash))
}

func (s *S3Storage) HasBlob(ctx context.Context, hash string) (bool, error) {
	return s.has(ctx, storage.BlobKey(hash))
}

func (s *S3Storage) PutIndex(ctx context.Context, rev string, data []byte) error {
	return s.put(ctx, storage.IndexKey(rev), data)
}

func (s *S3Storage) GetIndex(ctx context.Context, rev string) ([]byte, error) {
	return s.get(ctx, storage.IndexKey(rev))
}

func (s *S3Storage) ListIndexRevisions(ctx context.Context) ([]storage.ObjectInfo, error) {
	const indexPrefix = "index/"
	full := s.fullKey(indexPrefix)
	var out []storage.ObjectInfo
	paginator := s3.NewListObjectsV2Paginator(s.client, &s3.ListObjectsV2Input{
		Bucket: aws.String(s.bucket),
		Prefix: aws.String(full),
	})
	for paginator.HasMorePages() {
		page, err := paginator.NextPage(ctx)
		if err != nil {
			return nil, err
		}
		for _, obj := range page.Contents {
			rel := strings.TrimPrefix(*obj.Key, full)
			if rel == "" || strings.Contains(rel, "/") {
				continue
			}
			out = append(out, storage.ObjectInfo{
				Revision:     rel,
				Size:         *obj.Size,
				LastModified: *obj.LastModified,
			})
		}
	}
	return out, nil
}

func (s *S3Storage) PutBlobMeta(ctx context.Context, hash string, data []byte) error {
	return s.put(ctx, storage.BlobMetaKey(hash), data)
}

func (s *S3Storage) GetBlobMeta(ctx context.Context, hash string) ([]byte, error) {
	return s.get(ctx, storage.BlobMetaKey(hash))
}

func (s *S3Storage) ListBlobMetaHashes(ctx context.Context) ([]string, error) {
	const blobMetaPrefix = "blobs_meta/"
	full := s.fullKey(blobMetaPrefix)
	var out []string
	paginator := s3.NewListObjectsV2Paginator(s.client, &s3.ListObjectsV2Input{
		Bucket: aws.String(s.bucket),
		Prefix: aws.String(full),
	})
	for paginator.HasMorePages() {
		page, err := paginator.NextPage(ctx)
		if err != nil {
			return nil, err
		}
		for _, obj := range page.Contents {
			// Key shape: {prefix}blobs_meta/{xx}/{hash}. Extract trailing hash.
			rel := strings.TrimPrefix(*obj.Key, full)
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
	}
	return out, nil
}

func (s *S3Storage) put(ctx context.Context, key string, data []byte) error {
	_, err := s.client.PutObject(ctx, &s3.PutObjectInput{
		Bucket: aws.String(s.bucket),
		Key:    aws.String(s.fullKey(key)),
		Body:   bytes.NewReader(data),
	})
	return err
}

func (s *S3Storage) get(ctx context.Context, key string) ([]byte, error) {
	out, err := s.client.GetObject(ctx, &s3.GetObjectInput{
		Bucket: aws.String(s.bucket),
		Key:    aws.String(s.fullKey(key)),
	})
	if err != nil {
		var nsk *types.NoSuchKey
		var notFound *types.NotFound
		if errors.As(err, &nsk) || errors.As(err, &notFound) {
			return nil, fmt.Errorf("cloud.S3Storage: %s: %w", key, os.ErrNotExist)
		}
		return nil, err
	}
	defer out.Body.Close()
	return io.ReadAll(out.Body)
}

func (s *S3Storage) has(ctx context.Context, key string) (bool, error) {
	_, err := s.client.HeadObject(ctx, &s3.HeadObjectInput{
		Bucket: aws.String(s.bucket),
		Key:    aws.String(s.fullKey(key)),
	})
	if err != nil {
		var nsk *types.NoSuchKey
		var notFound *types.NotFound
		if errors.As(err, &nsk) || errors.As(err, &notFound) {
			return false, nil
		}
		return false, err
	}
	return true, nil
}
