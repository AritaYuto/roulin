#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Roulin.HotReload
{
    // SSE → Replace pipeline. Subscribes /watch/changes, validates platform,
    // fetches new blobs, applies via Roulin.HotReload.Replace. Parcel
    // is never mutated — effects are transient and lost on device restart.
    public sealed class RoulinHotReloadDriver : IDisposable
    {
        readonly RoulinSseClient _sse;

        CancellationTokenSource   _cts;

        public string LastEventStatus { get; private set; } = "(idle)";

        public RoulinHotReloadDriver(string baseUrl)
        {
            _sse = new RoulinSseClient(baseUrl);
            _sse.OnConnected = url => {
                LastEventStatus = $"connected: {url}";
                Debug.Log($"[HotReloadDriver] SSE connected: {url}");
            };
            _sse.OnError = (e, msg) => {
                LastEventStatus = $"sse error: {e.Message} ({msg})";
                Debug.LogWarning($"[HotReloadDriver] SSE error: {e.Message} → {msg}");
            };
            _sse.OnMessage = HandleMessage;
        }

        public void Start(CancellationToken outer = default)
        {
            if (_cts != null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _sse.Start(_cts.Token);
            LastEventStatus = "starting…";
            Debug.Log("[HotReloadDriver] starting SSE listener");
        }

        public void Stop()
        {
            _sse.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            _sse.Dispose();
        }

        // Worker thread; main-thread dispatch happens inside.
        private void HandleMessage(string payload)
        {
            Debug.Log($"[HotReloadDriver] SSE message: {payload}");
            PatchEvent ev;
            try { ev = JsonUtility.FromJson<PatchEvent>(payload); }
            catch (Exception e)
            {
                LastEventStatus = $"sse: bad payload: {e.Message}";
                Debug.LogWarning($"[HotReloadDriver] {LastEventStatus}");
                return;
            }
            if (ev == null || string.IsNullOrEmpty(ev.platform) || ev.changes == null || ev.changes.Length == 0)
            {
                LastEventStatus = $"sse: malformed patch: {payload}";
                Debug.LogWarning($"[HotReloadDriver] {LastEventStatus}");
                return;
            }
            ApplyAsync(ev, _cts?.Token ?? CancellationToken.None).Forget();
        }

        private async UniTaskVoid ApplyAsync(PatchEvent ev, CancellationToken ct)
        {
            await UniTask.SwitchToMainThread();
            try
            {
                // Bail before /blobs fetch if Editor build platform mismatches device.
                string device = Application.platform.ToString();
                if (!string.Equals(ev.platform, device, StringComparison.Ordinal))
                {
                    LastEventStatus = $"platform mismatch: build={ev.platform}, device={device}";
                    Debug.LogError($"[HotReloadDriver] {LastEventStatus} — patch rejected");
                    return;
                }

                LastEventStatus = $"applying {ev.changes.Length} change(s)";
                int replaced = 0;
                string blobsDir = Path.Combine(Roulin.LocalDir, "blobs");

                var hr = Roulin.HotReload;
                if (hr == null)
                {
                    Debug.LogWarning("[HotReloadDriver] Roulin.HotReload is null; Initialize() not called?");
                    return;
                }

                foreach (var c in ev.changes)
                {
                    if (string.IsNullOrEmpty(c.address) || string.IsNullOrEmpty(c.new_blob_hex)) continue;
                    if (hr.Get(c.address) == null) continue;   // not live, skip

                    await Roulin.Fetcher.DownloadBlobAsync(c.new_blob_hex, blobsDir, ct);
                    string blobPath = Path.Combine(blobsDir, c.new_blob_hex.Substring(0, 2), c.new_blob_hex);
                    if (!File.Exists(blobPath))
                    {
                        Debug.LogWarning($"[HotReloadDriver] new blob missing post-DL: {blobPath}");
                        continue;
                    }
                    var bytes = File.ReadAllBytes(blobPath);
                    if (hr.Replace(c.address, bytes)) replaced++;
                }

                LastEventStatus = $"applied: changes={ev.changes.Length} replaced={replaced}";
                Debug.Log($"[HotReload] {LastEventStatus}");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception e)
            {
                LastEventStatus = $"apply failed: {e.Message}";
                Debug.LogException(e);
            }
        }

        // Wire format for POST /patches; matches Go's PatchEvent / PatchChange.
        [Serializable]
        sealed class PatchEvent
        {
            public string         platform;
            public PatchChange[]  changes;
        }

        [Serializable]
        sealed class PatchChange
        {
            public string address;
            public string new_blob_hex;
        }

        // Populated by JsonUtility via reflection.
#pragma warning disable 0649
#pragma warning restore 0649
    }
}
#endif
