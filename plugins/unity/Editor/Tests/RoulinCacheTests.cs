// Covers only the no-Locator, FS-only paths (tempdir of fake blobs).
// Pin-aware behaviour needs a real Parcel handle and lives in RoulinE2ETest.

using NUnit.Framework;
using System;
using System.IO;

namespace Roulin.Editor.Tests
{
    public class RoulinCacheTests
    {
        private string _blobsDir;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "roulin-cache-test-" + Guid.NewGuid().ToString("N"));
            _blobsDir = Path.Combine(_root, "blobs");
            Directory.CreateDirectory(_blobsDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        // Drops a fake blob with the given hash hex name and size.
        private string DropBlob(string hashHex, long sizeBytes)
        {
            var sub = Path.Combine(_blobsDir, hashHex.Substring(0, 2));
            Directory.CreateDirectory(sub);
            var path = Path.Combine(sub, hashHex);
            File.WriteAllBytes(path, new byte[sizeBytes]);
            return path;
        }

        [Test]
        public void GetTotalSize_SumsFiles()
        {
            DropBlob("aa" + new string('1', 62), 1000);
            DropBlob("bb" + new string('2', 62), 2500);
            var cache = new RoulinCache(_blobsDir);
            Assert.AreEqual(3500, cache.GetTotalSize());
        }

        [Test]
        public void GetTotalSize_MissingDirReturnsZero()
        {
            var cache = new RoulinCache(Path.Combine(_root, "nope"));
            Assert.AreEqual(0, cache.GetTotalSize());
        }

        // Pin-aware branch of ClearAsync is covered in RoulinE2ETest.
    }
}