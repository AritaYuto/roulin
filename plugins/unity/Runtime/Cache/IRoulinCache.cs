using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Roulin
{
    public interface IRoulinCache
    {
        public string CacheBlobsRoot { get; }

        // Short-circuits on first hit; cheaper than GetBlobCount() > 0.
        bool HasAny();

        int GetBlobCount();
        long GetTotalSize();

        // Deletes blobs reachable from the address key; skips files held by
        // a live AssetBundle. Returns count removed.
        UniTask<int> ClearAsync(string key);

        // Wipe everything (debug / recovery).
        UniTask<int> ClearAllAsync();

        // Deletes blobs not in pinnedHashes; live-bundle files are skipped
        // and retried at the next process boot. Returns count removed.
        UniTask<int> PurgeOrphansAsync(IEnumerable<string> pinnedHashes);
    }
}
