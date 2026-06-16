using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using System.Linq;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Roulin
{
    // FS-backed default cache. Locator is set after LoadCatalogAsync; until
    // then key-scoped ClearAsync is a no-op (can't resolve bundle deps).
    public class RoulinCache : IRoulinCache
    {
        private readonly string _blobsDir;
        public string CacheBlobsRoot => _blobsDir;

        // Wired by Roulin after LoadCatalogAsync builds the locator.
        public RoulinLocator Locator { get; set; }

        public RoulinCache(string blobsDir)
        {
            _blobsDir = blobsDir;
        }

        public bool HasAny()
        {
            if (string.IsNullOrEmpty(_blobsDir) || !Directory.Exists(_blobsDir))
            {
                return false;
            }

            return Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories).Any();
        }

        public int GetBlobCount()
        {
            if (string.IsNullOrEmpty(_blobsDir) || !Directory.Exists(_blobsDir))
            {
                return 0;
            }

            return Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories).Count();
        }

        public long GetTotalSize()
        {
            if (string.IsNullOrEmpty(_blobsDir) || !Directory.Exists(_blobsDir))
            {
                return 0;
            }

            long total = 0;
            foreach (var p in Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(p).Length;
            }

            return total;
        }

        public UniTask<int> ClearAsync(string key)
        {
            if (string.IsNullOrEmpty(key)) return UniTask.FromResult(0);

            int removed = 0;
            foreach (var hashHex in GetBundleHashesFor(key))
            {
                if (string.IsNullOrEmpty(hashHex) || hashHex.Length < 2) continue;
                string path = Path.Combine(_blobsDir, hashHex.Substring(0, 2), hashHex);
                if (TryDeleteBlob(path)) removed++;
            }
            return UniTask.FromResult(removed);
        }

        public UniTask<int> ClearAllAsync()
        {
            if (string.IsNullOrEmpty(_blobsDir) || !Directory.Exists(_blobsDir))
                return UniTask.FromResult(0);

            int removed = 0;
            foreach (var path in Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories))
                if (TryDeleteBlob(path)) removed++;
            return UniTask.FromResult(removed);
        }

        public UniTask<int> PurgeOrphansAsync(IEnumerable<string> pinnedHashes)
        {
            if (string.IsNullOrEmpty(_blobsDir) || !Directory.Exists(_blobsDir))
                return UniTask.FromResult(0);

            var pinned = new HashSet<string>(
                pinnedHashes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            int removed = 0;
            foreach (var path in Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories))
            {
                string hash = Path.GetFileName(path);
                if (pinned.Contains(hash)) continue;
                if (TryDeleteBlob(path)) removed++;
            }
            return UniTask.FromResult(removed);
        }



        IEnumerable<string> GetBundleHashesFor(string key)
        {
            if (Locator == null) return Array.Empty<string>();
            if (!Locator.Locate(key, typeof(UnityEngine.Object), out var locations))
                return Array.Empty<string>();

            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in locations)
                CollectBundleHashes(loc, hashes);
            return hashes;
        }

        static void CollectBundleHashes(IResourceLocation loc, HashSet<string> sink)
        {
            if (loc.Data is RoulinBundleData data && !string.IsNullOrEmpty(data.BlobHashHex))
                sink.Add(data.BlobHashHex);
            if (loc.HasDependencies)
                foreach (var dep in loc.Dependencies)
                    CollectBundleHashes(dep, sink);
        }

        static bool TryDeleteBlob(string path)
        {
            try { File.Delete(path); return true; }
            catch (IOException)                 { return false; } // file in use
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
