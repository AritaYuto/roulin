using NUnit.Framework;
using Roulin.Editor.Build;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Tests
{
    // Proves the core building block for VCS-diff incremental builds:
    // given the AssetBundleBuild[] that WalkAddressableGroups produces,
    // we can reverse-map assetPath → bundle in O(1). If this does not
    // hold, the whole VCS-diff approach is blocked.
    public class BundleLookupTests
    {
        private static AssetBundleBuild MakeBuild(string name, params string[] assets)
        {
            return new AssetBundleBuild
            {
                assetBundleName = name,
                assetNames = assets,
                addressableNames = new string[assets.Length]
            };
        }

        [Test]
        public void GetBundleFor_ResolvesAssetToOwningBundle()
        {
            var lookup = BundleLookup.From(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab", "Assets/UI/B.prefab"),
                MakeBuild("shared", "Assets/Shared/Atlas.png")
            });

            Assert.AreEqual("ui_main", lookup.GetBundleFor("Assets/UI/A.prefab"));
            Assert.AreEqual("ui_main", lookup.GetBundleFor("Assets/UI/B.prefab"));
            Assert.AreEqual("shared", lookup.GetBundleFor("Assets/Shared/Atlas.png"));
        }

        [Test]
        public void GetBundleFor_ReturnsNullForUnknownAsset()
        {
            var lookup = BundleLookup.From(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsNull(lookup.GetBundleFor("Assets/Unknown/Z.prefab"));
        }

        [Test]
        public void GetBundleFor_ReturnsNullForEmptyOrNullPath()
        {
            var lookup = BundleLookup.From(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsNull(lookup.GetBundleFor(null));
            Assert.IsNull(lookup.GetBundleFor(string.Empty));
        }

        [Test]
        public void From_ThrowsWhenSameAssetAppearsInTwoBundles()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                BundleLookup.From(new[]
                {
                    MakeBuild("ui_main", "Assets/UI/A.prefab"),
                    MakeBuild("ui_dup", "Assets/UI/A.prefab")
                }));

            StringAssert.Contains("Assets/UI/A.prefab", ex.Message);
            StringAssert.Contains("ui_main", ex.Message);
            StringAssert.Contains("ui_dup", ex.Message);
        }

        [Test]
        public void ResolveAffectedBundles_DeduplicatesAndDropsUnknown()
        {
            // Simulates: git diff returned 4 changed files. Two are in
            // ui_main, one in shared, one outside any bundle. Affected
            // bundle set should be {ui_main, shared} — deduplicated and
            // the orphan file dropped.
            var lookup = BundleLookup.From(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab", "Assets/UI/B.prefab"),
                MakeBuild("shared", "Assets/Shared/Atlas.png")
            });

            var changed = new List<string>
            {
                "Assets/UI/A.prefab",
                "Assets/UI/B.prefab",
                "Assets/Shared/Atlas.png",
                "Assets/Outside/Loose.txt"
            };

            var affected = lookup.ResolveAffectedBundles(changed);

            Assert.AreEqual(2, affected.Count);
            Assert.Contains("ui_main", new List<string>(affected));
            Assert.Contains("shared", new List<string>(affected));
        }

        [Test]
        public void ResolveAffectedBundles_NoChanges_ReturnsEmptySet()
        {
            var lookup = BundleLookup.From(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsEmpty(lookup.ResolveAffectedBundles(new string[0]));
            Assert.IsEmpty(lookup.ResolveAffectedBundles(null));
        }

        [Test]
        public void From_HandlesNullAssetNames()
        {
            var lookup = BundleLookup.From(new[]
            {
                new AssetBundleBuild { assetBundleName = "empty", assetNames = null },
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.AreEqual(1, lookup.Count);
            Assert.AreEqual("ui_main", lookup.GetBundleFor("Assets/UI/A.prefab"));
        }
    }
}
