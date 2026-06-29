using NUnit.Framework;
using Roulin.Editor.Build;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Tests
{
    // Exercises the path → bundle reverse lookup that drives VCS-diff
    // incremental builds. AddressablesGroupsView.FromBundleBuilds is the
    // test-only factory that bypasses AddressableAssetSettings and builds
    // just the reverse index from hand-rolled AssetBundleBuild[].
    public class AddressablesGroupsViewTests
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
        public void GetBundle_ResolvesAssetToOwningBundle()
        {
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab", "Assets/UI/B.prefab"),
                MakeBuild("shared", "Assets/Shared/Atlas.png")
            });

            Assert.AreEqual("ui_main", view.GetBundle("Assets/UI/A.prefab"));
            Assert.AreEqual("ui_main", view.GetBundle("Assets/UI/B.prefab"));
            Assert.AreEqual("shared", view.GetBundle("Assets/Shared/Atlas.png"));
        }

        [Test]
        public void GetBundle_ReturnsNullForUnknownAsset()
        {
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsNull(view.GetBundle("Assets/Unknown/Z.prefab"));
        }

        [Test]
        public void GetBundle_ReturnsNullForEmptyOrNullPath()
        {
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsNull(view.GetBundle(null));
            Assert.IsNull(view.GetBundle(string.Empty));
        }

        [Test]
        public void FromBundleBuilds_ThrowsWhenSameAssetAppearsInTwoBundles()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                AddressablesGroupsView.FromBundleBuilds(new[]
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
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
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

            var affected = view.ResolveAffectedBundles(changed);

            Assert.AreEqual(2, affected.Count);
            Assert.Contains("ui_main", new List<string>(affected));
            Assert.Contains("shared", new List<string>(affected));
        }

        [Test]
        public void ResolveAffectedBundles_NoChanges_ReturnsEmptySet()
        {
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
            {
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.IsEmpty(view.ResolveAffectedBundles(new string[0]));
            Assert.IsEmpty(view.ResolveAffectedBundles(null));
        }

        [Test]
        public void FromBundleBuilds_HandlesNullAssetNames()
        {
            var view = AddressablesGroupsView.FromBundleBuilds(new[]
            {
                new AssetBundleBuild { assetBundleName = "empty", assetNames = null },
                MakeBuild("ui_main", "Assets/UI/A.prefab")
            });

            Assert.AreEqual(1, view.LookupCount);
            Assert.AreEqual("ui_main", view.GetBundle("Assets/UI/A.prefab"));
        }
    }
}
