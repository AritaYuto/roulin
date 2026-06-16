using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Roulin.Editor.Build.Meta
{
    public sealed class MetaClient
    {
        private readonly RoulinServerClient _server;

        public MetaClient(RoulinServerClient server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        // Pulls every blob_meta currently on the server (LIST → bounded parallel GET). 
        public async Task<List<RoulinBlobMeta>> FetchAllBlobMetas()
        {
            var sw = Stopwatch.StartNew();
            List<string> hashes;
            try
            {
                hashes = await _server.ListBlobMetaHashes();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[MetaClient] ListBlobMetaHashes failed: {ex.Message} — cold path");
                return null;
            }
            if (hashes == null || hashes.Count == 0)
            {
                Debug.Log("[MetaClient] FetchAllBlobMetas: server has no blob_meta → cold path");
                return null;
            }

            // Parallel GET; ceiling kept consistent with publish-side throttling
            // so the HttpClient connection pool sees stable pressure.
            const int maxParallel = 8;
            using var throttle = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = new List<Task<RoulinBlobMeta>>(hashes.Count);
            foreach (var hash in hashes)
            {
                tasks.Add(FetchOneAsync(hash, throttle));
            }

            var blobs = new List<RoulinBlobMeta>(hashes.Count);
            int fetched = 0, missing = 0;
            for (int i = 0; i < tasks.Count; i++)
            {
                try
                {
                    var bm = await tasks[i];
                    if (bm == null)
                    {
                        missing++;
                        continue;
                    }
                    blobs.Add(bm);
                    fetched++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[MetaClient] GetBlobMeta {hashes[i].Substring(0, 12)}…: {ex.Message}");
                    missing++;
                }
            }

            sw.Stop();
            Debug.Log(
                $"[MetaClient] FetchAllBlobMetas: listed={hashes.Count} " +
                $"fetched={fetched} missing={missing} ({sw.ElapsedMilliseconds} ms)");
            return blobs;
        }

        private async Task<RoulinBlobMeta> FetchOneAsync(string hash, SemaphoreSlim throttle)
        {
            await throttle.WaitAsync();
            try
            {
                return await _server.GetBlobMeta(hash);
            }
            finally
            {
                throttle.Release();
            }
        }

        public Task PublishBlobMeta(string blobHash, RoulinBlobMeta meta) =>
            _server.PostBlobMeta(blobHash, meta);
    }
}
