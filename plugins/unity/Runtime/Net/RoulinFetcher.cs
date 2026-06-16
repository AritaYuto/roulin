using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace Roulin
{
    public interface IHttpRequest : IDisposable
    {
        // expectedHash != null (32 bytes) ⇒ impl verifies BLAKE3 and throws on mismatch.
        UniTask<byte[]> GetAsync(
            string            url,
            byte[]            expectedHash = null,
            CancellationToken ct           = default,
            IProgress<float>  progress     = null);
    }

    internal class UnityWebRequestHttp : IHttpRequest
    {
        readonly SemaphoreSlim _sem;

        public UnityWebRequestHttp(int maxParallel = 3)
        {
            int cap = Math.Max(1, maxParallel);
            _sem = new SemaphoreSlim(cap, cap);
        }

        public async UniTask<byte[]> GetAsync(
            string            url,
            byte[]            expectedHash = null,
            CancellationToken ct           = default,
            IProgress<float>  progress     = null)
        {
            await _sem.WaitAsync(ct);
            try
            {
                using var req = UnityWebRequest.Get(url);
                try { await req.SendWebRequest().ToUniTask(progress: progress, cancellationToken: ct); }
                catch (UnityWebRequestException e)
                {
                    throw new Exception($"HTTP GET {url}: {e.Message}", e);
                }
                progress?.Report(1f);
                byte[] bytes = req.downloadHandler.data;

                if (expectedHash != null)
                {
                    if (expectedHash.Length != 32)
                        throw new ArgumentException(
                            $"expectedHash must be 32 bytes (got {expectedHash.Length})",
                            nameof(expectedHash));
                    var actual = new byte[32];
                    RoulinNative.ComputeBlake3(bytes, actual);
                    if (!BytesEqual(actual, expectedHash))
                        throw new Exception(
                            $"hash mismatch for {url}: expected {RoulinNative.HashToHex(expectedHash)} got {RoulinNative.HashToHex(actual)}");
                }
                return bytes;
            }
            finally
            {
                _sem.Release();
            }
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void Dispose() => _sem.Dispose();
    }

    public class RoulinFetcher
    {
        readonly string       _baseUrl;
        readonly IHttpRequest _http;

        public RoulinFetcher(string baseUrl, IHttpRequest http = null)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _http    = http ?? new UnityWebRequestHttp();
        }

        public string BaseUrl => _baseUrl;

        public async UniTask DownloadFileAsync(string url, string destPath, CancellationToken ct = default, IProgress<float> progress = null)
        {
            byte[] bytes = await _http.GetAsync(url, expectedHash: null, ct, progress);
            await UniTask.SwitchToThreadPool();
            ExceptionDispatchInfo exInfo = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                string tmp = destPath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
            }
            catch (Exception e) { exInfo = ExceptionDispatchInfo.Capture(e); }
            await UniTask.SwitchToMainThread();
            exInfo?.Throw();
        }

        // Path layout: {blobsDir}/{hash[:2]}/{hash}. No-op if already on disk.
        public async UniTask DownloadBlobAsync(string hashHex, string blobsDir, CancellationToken ct = default, IProgress<float> progress = null)
        {
            string localPath = Path.Combine(blobsDir, hashHex.Substring(0, 2), hashHex);
            if (File.Exists(localPath))
            {
                UnityEngine.Debug.Log($"[RoulinFetcher] skip (already cached): hash={hashHex}");
                progress?.Report(1f);
                return;
            }

            string url      = $"{_baseUrl}/blobs/{hashHex.Substring(0, 2)}/{hashHex}";
            byte[] expected = RoulinNative.HashFromHex(hashHex);
            UnityEngine.Debug.Log($"[RoulinFetcher] GET {url}");
            byte[] bytes    = await _http.GetAsync(url, expected, ct, progress);

            await UniTask.SwitchToThreadPool();
            ExceptionDispatchInfo exInfo = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath));
                string tmp = localPath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(localPath)) File.Delete(localPath);
                File.Move(tmp, localPath);
            }
            catch (Exception e) { exInfo = ExceptionDispatchInfo.Capture(e); }
            await UniTask.SwitchToMainThread();
            exInfo?.Throw();
            UnityEngine.Debug.Log($"[RoulinFetcher] wrote: hash={hashHex} bytes={bytes.Length}");
        }
    }
}
