#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Roulin.HotReload
{
    // Minimal SSE consumer; 2s backoff on transport errors. Uses HttpClient +
    // ResponseHeadersRead — UnityWebRequest can't stream incremental chunks.
    public sealed class RoulinSseClient : IDisposable
    {
        readonly HttpClient _http;
        readonly string     _url;
        CancellationTokenSource _cts;

        public Action<string> OnMessage;          // raw payload string (post-"data: ")
        public Action<string> OnConnected;        // arg = url
        public Action<Exception, string> OnError; // (exception, will-retry-message)

        public RoulinSseClient(string baseUrl)
        {
            _url  = $"{baseUrl.TrimEnd('/')}/watch/changes";
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public void Start(CancellationToken outer = default)
        {
            if (_cts != null) return;  // already running
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _ = RunLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            _http.Dispose();
        }

        async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var resp = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    OnConnected?.Invoke(_url);

                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    while (!ct.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;             // server closed
                        if (line.StartsWith("data: "))
                            OnMessage?.Invoke(line.Substring(6));
                        // ignore "retry: ..." / comment / empty separators.
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    OnError?.Invoke(e, "reconnect in 2s");
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }
}
#endif
