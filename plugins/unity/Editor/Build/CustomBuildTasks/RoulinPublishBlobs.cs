using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Roulin.Editor;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using System.Runtime.InteropServices;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Uploads every bundle SBP produced and records (name → hash + size)
    // in IBlobUploadResults for downstream catalog construction.
    internal sealed class RoulinPublishBlobs : IBuildTask
    {
#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        private IBundleBuildResults _sbpResults;

        [InjectContext(ContextUsage.In)]
        private IBlobUploadResults _uploadResults;
#pragma warning restore 649

        public int Version => 1;

        public RoulinServerClient Server { get; set; }
        public string OutputDir { get; set; }
        public bool Verbose { get; set; }

        [DllImport("roulin_core", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe void rln_compute_blake3(void* data, UIntPtr len, byte* outHash);

        public static unsafe string Blake3Hex(byte[] data)
        {
            var hash = new byte[32];
            fixed (byte* dp = data, hp = hash)
            {
                rln_compute_blake3(dp, (UIntPtr)data.Length, hp);
            }
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public ReturnCode Run()
        {
            if (Server == null)
            {
                throw new InvalidOperationException(
                    "RoulinPublishBlobs.Server is null — set before adding to task list");
            }
            return RunCore();
        }

        // Cap in-flight HTTP. Unbounded fan-out exhausts HttpClient's connection
        // pool and surfaces as "invalid or unrecognized response" via stale sockets.
        private const int MaxParallel = 4;

        private ReturnCode RunCore()
        {
            var sink = (BlobUploadResults)_uploadResults;
            var counters = new Counters();
            var total = _sbpResults.BundleInfos.Count;

            using var srcToken = new CancellationTokenSource();
            using var sem = new SemaphoreSlim(0);
            using var throttle = new SemaphoreSlim(MaxParallel, MaxParallel);
            var tasks = new List<Task>(total);

            foreach (var kv in _sbpResults.BundleInfos)
            {
                var bundleName = kv.Key;
                var fileName = kv.Value.FileName;
                tasks.Add(Task.Run(async () =>
                {
                    await throttle.WaitAsync(srcToken.Token);
                    try
                    {
                        await UploadOne(bundleName, fileName, sink, counters, srcToken.Token);
                    }
                    finally
                    {
                        throttle.Release();
                        sem.Release();
                    }
                }, srcToken.Token));
            }

            for (int i = 0; i < total; i++)
            {
                sem.Wait(srcToken.Token);
                var done = i + 1;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Roulin Build",
                        $"Publishing blobs… [{done}/{total}]",
                        (float)done / Math.Max(1, total)))
                {
                    srcToken.Cancel();
                    break;
                }
            }

            Task.WaitAny(Task.WhenAll(tasks));

            var fatal = 0;
            foreach (var t in tasks)
            {
                if (t.Exception == null) continue;
                fatal++;
                Debug.LogException(t.Exception);
            }
            if (fatal > 0 || srcToken.IsCancellationRequested)
            {
                return ReturnCode.Error;
            }

            Debug.Log(
                $"[RoulinPublishBlobs] {counters.Uploaded} uploaded, " +
                $"{counters.Skipped} skipped (unchanged) — " +
                $"total {RoulinUtil.FormatBytes(counters.TotalBytes)}");

            return ReturnCode.Success;
        }

        private sealed class Counters
        {
            public int Uploaded;
            public int Skipped;
            public long TotalBytes;
        }

        private async Task UploadOne(
            string bundleName,
            string fileName,
            BlobUploadResults sink,
            Counters counters,
            CancellationToken ct)
        {
            var path = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(OutputDir, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"SBP reported bundle '{bundleName}' but file is missing", path);
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Blake3Hex(bytes);

            if (await Server.BlobExists(hash, ct))
            {
                Interlocked.Increment(ref counters.Skipped);
                if (Verbose)
                {
                    Debug.Log(
                        $"[RoulinPublishBlobs]   skipped  {bundleName,-32} {RoulinUtil.FormatBytes(bytes.LongLength),10} " +
                        $"→ {hash[..12]}… (unchanged)");
                }
            }
            else
            {
                await Server.PostBlob(bytes, ct);
                Interlocked.Increment(ref counters.Uploaded);
                if (Verbose)
                {
                    Debug.Log(
                        $"[RoulinPublishBlobs]   uploaded {bundleName,-32} {RoulinUtil.FormatBytes(bytes.LongLength),10} " +
                        $"→ {hash[..12]}…");
                }
            }

            // Sink mutation must be thread-safe; lock the dict directly
            // since Dictionary<,>.Add isn't.
            lock (sink)
            {
                sink.Add(bundleName, hash, bytes.LongLength);
            }
            Interlocked.Add(ref counters.TotalBytes, bytes.LongLength);
        }
    }
}
