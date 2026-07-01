using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Roulin.Editor.Build;
using UnityEngine;

namespace Roulin.Editor
{
    public sealed class RoulinServerClient : IDisposable
    {
        private const int MaxAttempts = 3;

        private readonly string _baseUrl;
        private readonly HttpClient _http;

        public RoulinServerClient(string baseUrl, TimeSpan? timeout = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        }

        public void Dispose()
        {
            _http?.Dispose();
        }

        // Returns true if the blob already exists on the server.
        public async Task<bool> BlobExists(string hexHash, CancellationToken ct = default)
        {
            var prefix = hexHash[..2];
            var url = $"{_baseUrl}/blobs/{prefix}/{hexHash}";
            var (status, _) = await SendWithRetry(
                () => new HttpRequestMessage(HttpMethod.Head, url), ct);
            if (status == HttpStatusCode.OK) return true;
            if (status == HttpStatusCode.NotFound) return false;
            throw new Exception($"HEAD {url}: unexpected {(int)status}");
        }

        // Uploads a blob and returns its hex BLAKE3 hash. Idempotent.
        // Persistent path — bytes land in canonical storage (= S3 / CDN).
        // For developer-local hot-reload iterations use PostHotBlob.
        public Task<string> PostBlob(byte[] body, CancellationToken ct = default)
            => PostBlobCore("/blobs", body, ct);

        // Hot-reload counterpart to PostBlob. Bytes go to the server's
        // transient store only (never CDN); the SSE /patches relay points
        // devices at the same content-addressed URL. Use for Sync iterations
        // — anything posted here is local-to-this-server.
        public Task<string> PostHotBlob(byte[] body, CancellationToken ct = default)
            => PostBlobCore("/hot/blobs", body, ct);

        private async Task<string> PostBlobCore(string path, byte[] body, CancellationToken ct)
        {
            var url = $"{_baseUrl}{path}";
            var (status, respBody) = await SendWithRetry(() =>
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            }, ct);
            if ((int)status >= 400)
            {
                throw new Exception($"POST {path} failed: {(int)status} {status}: {respBody}");
            }
            var parsed = JsonUtility.FromJson<HashResponse>(respBody);
            if (parsed == null || string.IsNullOrEmpty(parsed.hash))
            {
                throw new Exception($"POST {path}: unexpected response body: {respBody}");
            }
            return parsed.hash;
        }

        // Submits a JSON Parcel to materialise the Parcel at /index/{revision}.
        public async Task PostParcel(string revision, Parcel parcel, CancellationToken ct = default)
        {
            var url = $"{_baseUrl}/parcels/{revision}";
            var json = JsonUtility.ToJson(parcel);
            var (status, respBody) = await SendWithRetry(
                () => JsonRequest(HttpMethod.Post, url, json), ct);
            if ((int)status >= 400)
            {
                throw new Exception($"POST /parcels/{revision} failed: {(int)status} {status}: {respBody}");
            }
        }

        // Relays a transient hot-reload patch through the SSE channel.
        public async Task PostPatches(string platform, PatchChange[] changes, CancellationToken ct = default)
        {
            var url = $"{_baseUrl}/patches";
            var json = JsonUtility.ToJson(new PatchEvent { platform = platform, changes = changes });
            var (status, respBody) = await SendWithRetry(
                () => JsonRequest(HttpMethod.Post, url, json), ct);
            if ((int)status >= 400)
            {
                throw new Exception($"POST /patches failed: {(int)status} {status}: {respBody}");
            }
        }

        // Fetches the incremental build inputs. The server picks the base
        // revision from its own stored Index list (newest by LastModified) —
        // guaranteeing whatever we get back has an Index the merge step can
        // load. Empty base_revision = "no prior publish", caller falls back
        // to a full rebuild.
        public async Task<DiffResponse> GetDiffAsync(CancellationToken ct = default)
        {
            var url = $"{_baseUrl}/diff";
            var (status, body) = await SendWithRetry(
                () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if ((int)status >= 400)
            {
                throw new Exception($"GET /diff failed: {(int)status} {status}: {body}");
            }
            return JsonUtility.FromJson<DiffResponse>(body);
        }

        // ---- retry plumbing -------------------------------------------------

        // Sends an HTTP request with up to MaxAttempts attempts. Retries on 5xx
        // and HttpRequestException (network flake / VPN blip); 4xx returns
        // immediately for caller to interpret. The factory rebuilds the
        // HttpRequestMessage + HttpContent each attempt because HttpClient
        // disposes the body after each send.
        private async Task<(HttpStatusCode status, string body)> SendWithRetry(
            Func<HttpRequestMessage> requestFactory, CancellationToken ct)
        {
            HttpStatusCode lastStatus = 0;
            string lastBody = null;
            Uri lastUri = null;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                HttpResponseMessage resp = null;
                bool threw = false;
                try
                {
                    using var req = requestFactory();
                    lastUri = req.RequestUri;
                    resp = await _http.SendAsync(req, ct);
                    lastStatus = resp.StatusCode;
                    lastBody = await resp.Content.ReadAsStringAsync();
                }
                catch (HttpRequestException ex) when (attempt < MaxAttempts - 1)
                {
                    threw = true;
                    Debug.LogWarning(
                        $"[RoulinServerClient] {lastUri} attempt {attempt + 1}/{MaxAttempts} " +
                        $"threw {ex.Message}, retrying in {BackoffSeconds(attempt)}s");
                }
                finally
                {
                    resp?.Dispose();
                }

                if (!threw && ((int)lastStatus < 500 || attempt == MaxAttempts - 1))
                {
                    return (lastStatus, lastBody);
                }

                if (!threw)
                {
                    Debug.LogWarning(
                        $"[RoulinServerClient] {lastUri} attempt {attempt + 1}/{MaxAttempts} " +
                        $"got {(int)lastStatus} {lastStatus}, retrying in {BackoffSeconds(attempt)}s");
                }
                await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds(attempt)), ct);
            }
            return (lastStatus, lastBody);
        }

        private static int BackoffSeconds(int attempt) => 1 << attempt; // 1, 2, 4, ...

        private static HttpRequestMessage JsonRequest(HttpMethod method, string url, string json)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return new HttpRequestMessage(method, url) { Content = content };
        }

        [Serializable]
        private class HashResponse
        {
            public string hash;
        }

        [Serializable]
        public sealed class PatchEvent
        {
            public string platform;
            public PatchChange[] changes;
        }

        [Serializable]
        public sealed class PatchChange
        {
            public string address;
            public string new_blob_hex;
        }

        [Serializable]
        public sealed class DiffResponse
        {
            public string base_revision;
            public string head_revision;
            public List<string> changed;
            public List<string> uncommitted;
            // Bundle names present in the base revision's Index. Used by the
            // build to detect NEW bundles (present in HEAD's Addressables walk
            // but missing at base) that would otherwise slip past the
            // path-based dirty detection.
            public List<string> base_bundle_names;
        }
    }
}
