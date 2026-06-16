using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Roulin
{
    public readonly struct PairingResult
    {
        public readonly string BaseUrl;
        public readonly string Revision;

        public PairingResult(string baseUrl, string revision)
        {
            BaseUrl  = baseUrl;
            Revision = revision;
        }
    }

    public static class RoulinPairing
    {
        // Distinct ports for Editor vs device-over-USB so both can pair on
        // the same host. Must match Go side (device.DefaultPairPort + hostPairPort).
#if UNITY_EDITOR
        public const int PairPort = 12766;
#else
        public const int PairPort = 12765;
#endif
        public const int DefaultPairWindowMs = 1500;

        static string BasePath        => Application.persistentDataPath;
        static string DevAddrPath     => Path.Combine(BasePath, "roulin_dev_addr");
        static string DevRevisionPath => Path.Combine(BasePath, "roulin_dev_revision");

        public static string LoadCachedDevAddr()         => ReadFile(DevAddrPath);
        public static string LoadCachedDevRevisionHint() => ReadFile(DevRevisionPath);

        public static void ClearCachedDevAddr()         => DeleteFile(DevAddrPath);
        public static void ClearCachedDevRevisionHint() => DeleteFile(DevRevisionPath);

        // Cached launches honour pairWindowMs before falling back to cache;
        // first launch (no cache) blocks regardless.
        public static async UniTask<PairingResult> GetOrListenAsync(
            CancellationToken ct = default,
            int pairWindowMs = DefaultPairWindowMs)
        {
            var cachedAddr = LoadCachedDevAddr();
            var cachedRev  = LoadCachedDevRevisionHint();

            if (string.IsNullOrEmpty(cachedAddr))
            {
                Debug.Log($"[RoulinPairing] No cached pairing — listening on :{PairPort}.");
                return await ListenAsync(ct);
            }

            var fresh = await TryListenAsync(pairWindowMs, ct);
            if (fresh.HasValue)
            {
                var f = fresh.Value;
                if (f.BaseUrl != cachedAddr || f.Revision != cachedRev)
                    Debug.LogWarning(
                        $"[RoulinPairing] Pair refreshed — " +
                        $"addr: {cachedAddr} → {f.BaseUrl}, " +
                        $"revision: {cachedRev ?? "(none)"} → {f.Revision ?? "(none)"}");
                else
                    Debug.Log($"[RoulinPairing] Pair re-confirmed: addr={f.BaseUrl}, revision={f.Revision ?? "(none)"}");
                return f;
            }

            Debug.LogWarning(
                $"[RoulinPairing] No pair within {pairWindowMs}ms — using CACHED " +
                $"(addr={cachedAddr}, revision={cachedRev ?? "(none)"}). " +
                $"Run pair BEFORE launch to refresh.");
            return new PairingResult(cachedAddr, cachedRev);
        }

        // Blocks indefinitely until pair connection arrives. Throws on cancel.
        public static async UniTask<PairingResult> ListenAsync(CancellationToken ct = default)
        {
            var result = await ListenInternal(Timeout.Infinite, ct);
            // Infinite timeout never returns null without throwing.
            return result.Value;
        }
        
        public static UniTask<PairingResult?> TryListenAsync(int timeoutMs, CancellationToken ct)
            => ListenInternal(timeoutMs, ct);

        static async UniTask<PairingResult?> ListenInternal(int timeoutMs, CancellationToken ct)
        {
            string addrPath = DevAddrPath;
            string revPath  = DevRevisionPath;

            await UniTask.SwitchToThreadPool();

            var listener = new TcpListener(IPAddress.Loopback, PairPort);
            string addr     = null;
            string revision = null;
            bool timedOut   = false;
            try
            {
                listener.Start();

                // Closing the listener is the only way to unblock AcceptTcpClientAsync.
                using var _ = ct.Register(() => listener.Stop());

                var acceptTask = listener.AcceptTcpClientAsync();
                TcpClient client = null;

                if (timeoutMs == Timeout.Infinite)
                {
                    try { client = await acceptTask; }
                    catch (SocketException) when (ct.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw; // unreachable; satisfies compiler
                    }
                }
                else
                {
                    var winner = await Task.WhenAny(acceptTask, Task.Delay(timeoutMs, ct));
                    if (winner != acceptTask)
                    {
                        // Timed out (or cancelled): unblock accept, swallow its fault.
                        listener.Stop();
                        try { await acceptTask; } catch { /* expected: aborted */ }
                        ct.ThrowIfCancellationRequested();
                        timedOut = true;
                    }
                    else
                    {
                        client = await acceptTask;
                    }
                }

                if (!timedOut)
                {
                    using (client)
                    using (var reader = new StreamReader(client.GetStream()))
                    {
                        addr     = (await reader.ReadLineAsync())?.Trim();
                        revision = (await reader.ReadLineAsync())?.Trim();
                    }

#if UNITY_EDITOR
                    addr = "http://localhost:8765";
#endif

                    if (string.IsNullOrEmpty(addr))
                        throw new Exception("[RoulinPairing] Received empty address from roulin pair");

                    File.WriteAllText(addrPath, addr);
                    if (!string.IsNullOrEmpty(revision))
                        File.WriteAllText(revPath, revision);
                    else if (File.Exists(revPath))
                        File.Delete(revPath);
                }
            }
            finally
            {
                listener.Stop();
            }

            await UniTask.SwitchToMainThread();

            if (timedOut) return null;

            var rev = string.IsNullOrEmpty(revision) ? null : revision;
            Debug.Log($"[RoulinPairing] Paired — addr={addr}, revision={rev ?? "(none)"}");
            return new PairingResult(addr, rev);
        }

        static string ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string s = File.ReadAllText(path).Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }

        static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort */ }
        }
    }
}
