using NUnit.Framework;
using Roulin.Editor.Build;
using System.Collections.Generic;

namespace Roulin.Editor.Tests
{
    // Pins the field-by-field translation from RoulinCatalog into the wire
    // Parcel form. Most importantly: SizeBytes round-trips into
    // Parcel.bundles[0].size_bytes — Addressables.GetDownloadSizeAsync
    // depends on it via ILocationSizeData at runtime.
    public class RoulinCatalogTests
    {
        [Test]
        public void ToParcel_PropagatesSizeBytes()
        {
            var catalog = new RoulinCatalog();
            var entry = new RoulinCatalog.Entry
            {
                Name = "ui_icons",
                BlobHash = new string('a', 64),
                SizeBytes = 12345,
            };
            entry.Addresses.Add(new AddressableEntry("ui/icons/player", null, null, null));
            entry.Addresses.Add(new AddressableEntry("ui/icons/enemy", null, null, null));
            catalog.Add(entry);

            var parcel = catalog.ToParcel();

            Assert.AreEqual(1, parcel.bundles.Count);
            Assert.AreEqual(12345, parcel.bundles[0].size_bytes,
                "RoulinCatalog.Entry.SizeBytes must round-trip into " +
                "Parcel.bundles[0].size_bytes; this powers " +
                "Addressables.GetDownloadSizeAsync via ILocationSizeData.");
        }

        [Test]
        public void ToParcel_ZeroSizeIsPreserved()
        {
            // Verify the size field round-trips 0 rather than silently defaulting.
            var catalog = new RoulinCatalog();
            var entry = new RoulinCatalog.Entry
            {
                Name = "legacy",
                BlobHash = new string('b', 64),
                SizeBytes = 0,
            };
            entry.Addresses.Add(new AddressableEntry("x", null, null, null));
            catalog.Add(entry);

            var parcel = catalog.ToParcel();

            Assert.AreEqual(0, parcel.bundles[0].size_bytes);
        }

        [Test]
        public void Add_RejectsDuplicateName()
        {
            var catalog = new RoulinCatalog();
            catalog.Add(new RoulinCatalog.Entry { Name = "x", BlobHash = "h1", SizeBytes = 1 });
            Assert.Throws<System.InvalidOperationException>(() =>
                catalog.Add(new RoulinCatalog.Entry { Name = "x", BlobHash = "h2", SizeBytes = 2 }));
        }

        [Test]
        public void ToParcel_CopiesDepBundleNames()
        {
            var catalog = new RoulinCatalog();
            var entry = new RoulinCatalog.Entry
            {
                Name = "main",
                BlobHash = new string('c', 64),
                SizeBytes = 100,
            };
            entry.DepBundleNames.Add("dep1");
            entry.DepBundleNames.Add("dep2");
            catalog.Add(entry);

            var parcel = catalog.ToParcel();

            CollectionAssert.AreEqual(
                new List<string> { "dep1", "dep2" },
                parcel.bundles[0].dep_bundle_names);
        }
    }
}
