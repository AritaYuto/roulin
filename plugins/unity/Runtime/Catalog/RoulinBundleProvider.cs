using System;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Roulin
{
    public class RoulinBundleProvider : ResourceProviderBase
    {
        public const string Id = nameof(RoulinBundleProvider);
        public override string ProviderId => Id;
        public override Type GetDefaultType(IResourceLocation location) => typeof(IAssetBundleResource);

        public override void Provide(ProvideHandle handle)
        {
            var data = handle.Location.Data as RoulinBundleData;
            if (data == null)
            {
                handle.Complete<IAssetBundleResource>(null, false,
                    new Exception("RoulinBundleProvider: missing RoulinBundleData"));
                return;
            }

            long totalBytes      = data.BundleSize;
            long downloadedBytes = 0;
            handle.SetProgressCallback(() =>
                totalBytes > 0 ? (float)downloadedBytes / totalBytes : 0f);
            handle.SetDownloadProgressCallbacks(() => new DownloadStatus
            {
                TotalBytes      = totalBytes,
                DownloadedBytes = downloadedBytes,
                IsDone          = false,
            });
            var progress = Progress.Create<float>(p =>
                downloadedBytes = totalBytes > 0 ? (long)(totalBytes * p) : 0);
            ProvideAsync(handle, data, progress).Forget();
        }

        static async UniTaskVoid ProvideAsync(ProvideHandle     handle,
                                                RoulinBundleData data,
                                                IProgress<float>  progress)
        {
            string blobsDir = Path.Combine(Roulin.LocalDir, "blobs");
            try
            {
                await Roulin.Fetcher.DownloadBlobAsync(data.BlobHashHex, blobsDir, progress: progress);
            }
            catch (Exception e)
            {
                handle.Complete<IAssetBundleResource>(null, false, e);
                return;
            }

            string localPath = Path.Combine(blobsDir,
                data.BlobHashHex.Substring(0, 2), data.BlobHashHex);

            // Dep resources provided in dep-tree order; GetAssetBundle walks them.
            int depCount = handle.Location.HasDependencies
                ? handle.Location.Dependencies.Count
                : 0;
            IAssetBundleResource[] deps = depCount > 0
                ? new IAssetBundleResource[depCount]
                : null;
            for (int i = 0; i < depCount; i++)
                deps[i] = handle.GetDependency<IAssetBundleResource>(i);

            handle.Complete<IAssetBundleResource>(
                new RoulinBundleResource(data.BlobHashHex, localPath, deps),
                true, null);
        }

        public override void Release(IResourceLocation location, object asset)
        {
            if (asset is RoulinBundleResource res)
                res.Unload();
        }
    }
}
