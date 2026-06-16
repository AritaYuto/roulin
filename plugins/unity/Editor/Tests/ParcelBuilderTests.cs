using Roulin.Editor.Build;
using Roulin.Editor.Build.CustomBuildTasks;
using NUnit.Framework;
using System.Collections.Generic;

namespace Roulin.Editor.Tests
{
    public class ParcelBuilderTests
    {
        [Test]
        public void Build_PropagatesSizeBytes()
        {
            var inputs = new Dictionary<string, BundleInput>
            {
                ["ui_icons"] = new()
                {
                    Name = "ui_icons",
                    BinaryHash = new string('a', 64),
                    SizeBytes = 12345,
                    Entries = new List<EntryInput>
                    {
                        new("ui/icons/player", null, null, null),
                        new("ui/icons/enemy", null, null, null)
                    }
                }
            };

            var parcel = ParcelBuilder.Build(inputs);

            Assert.AreEqual(1, parcel.bundles.Count);
            Assert.AreEqual(12345, parcel.bundles[0].size_bytes,
                "Bundle.SizeBytes must round-trip into Parcel.bundles[0].size_bytes; " +
                "this powers Addressables.GetDownloadSizeAsync via ILocationSizeData.");
        }

        [Test]
        public void Build_ZeroSizeIsPreserved()
        {
            // Some legacy callers may not set SizeBytes — verify the field
            // round-trips zero rather than silently picking a default.
            var inputs = new Dictionary<string, BundleInput>
            {
                ["legacy"] = new()
                {
                    Name = "legacy",
                    BinaryHash = new string('b', 64),
                    SizeBytes = 0,
                    Entries = new List<EntryInput> { new("x", null, null, null) }
                }
            };

            var parcel = ParcelBuilder.Build(inputs);

            Assert.AreEqual(0, parcel.bundles[0].size_bytes);
        }
    }
}
