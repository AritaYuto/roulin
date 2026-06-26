using System.Collections.Generic;
using NUnit.Framework;
using Roulin.Editor.Build;

namespace Roulin.Editor.Tests
{
    public class VcsDiffPathNormalizerTests
    {
        [Test]
        public void Normalize_StripsProjectPrefix()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                gitRoot: "/repo",
                projectRoot: "/repo/client/game",
                repoRelativePaths: new[] { "client/game/Assets/foo.png" });

            Assert.AreEqual(new List<string> { "Assets/foo.png" }, result);
        }

        [Test]
        public void Normalize_DropsPathsOutsideProjectPrefix()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game",
                new[]
                {
                    "other/dir/file.txt",
                    "client/game/Assets/foo.png",
                    "tools/script.sh"
                });

            Assert.AreEqual(new List<string> { "Assets/foo.png" }, result);
        }

        [Test]
        public void Normalize_DropsPathsOutsideAssetsAndPackages()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game",
                new[]
                {
                    "client/game/Library/cache.db",
                    "client/game/ProjectSettings/foo.asset",
                    "client/game/Assets/foo.png",
                    "client/game/Packages/manifest.json"
                });

            CollectionAssert.AreEquivalent(
                new[] { "Assets/foo.png", "Packages/manifest.json" }, result);
        }

        [Test]
        public void Normalize_EmptyInput_ReturnsEmpty()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game", new string[0]);
            Assert.IsEmpty(result);
        }

        [Test]
        public void Normalize_NullInput_ReturnsEmpty()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game", null);
            Assert.IsEmpty(result);
        }

        [Test]
        public void Normalize_SkipsNullAndEmptyEntriesInInput()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game",
                new[] { null, "", "client/game/Assets/foo.png", "" });

            Assert.AreEqual(new List<string> { "Assets/foo.png" }, result);
        }

        [Test]
        public void Normalize_GitRootEqualsProjectRoot_NoPrefixStripping()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                gitRoot: "/standalone",
                projectRoot: "/standalone",
                repoRelativePaths: new[]
                {
                    "Assets/foo.png",
                    "Packages/com.example/bar.json",
                    "README.md"
                });

            CollectionAssert.AreEquivalent(
                new[] { "Assets/foo.png", "Packages/com.example/bar.json" }, result);
        }

        [Test]
        public void Normalize_HandlesBackslashPathSeparators()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                @"C:\repo",
                @"C:\repo\client\game",
                new[] { @"client\game\Assets\foo.png" });

            Assert.AreEqual(new List<string> { "Assets/foo.png" }, result);
        }

        [Test]
        public void Normalize_ToleratesTrailingSlashesOnRoots()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo/",
                "/repo/client/game/",
                new[] { "client/game/Assets/foo.png" });

            Assert.AreEqual(new List<string> { "Assets/foo.png" }, result);
        }

        [Test]
        public void Normalize_PreservesOrderOfMatchingPaths()
        {
            var result = VcsDiffPathNormalizer.Normalize(
                "/repo", "/repo/client/game",
                new[]
                {
                    "client/game/Assets/b.png",
                    "client/game/Assets/a.png",
                    "client/game/Assets/c.png"
                });

            Assert.AreEqual(
                new List<string> { "Assets/b.png", "Assets/a.png", "Assets/c.png" },
                result);
        }

        [Test]
        public void ComputeProjectPrefix_GitRootEqualsProjectRoot_Empty()
        {
            Assert.AreEqual(
                "",
                VcsDiffPathNormalizer.ComputeProjectPrefix("/repo", "/repo"));
        }

        [Test]
        public void ComputeProjectPrefix_NestedProject_ReturnsPrefixWithTrailingSlash()
        {
            Assert.AreEqual(
                "client/game/",
                VcsDiffPathNormalizer.ComputeProjectPrefix(
                    "/repo", "/repo/client/game"));
        }

        [Test]
        public void ComputeProjectPrefix_ProjectNotUnderGitRoot_ReturnsEmpty()
        {
            Assert.AreEqual(
                "",
                VcsDiffPathNormalizer.ComputeProjectPrefix("/repo", "/other/place"));
        }

        [Test]
        public void ComputeProjectPrefix_NullInputs_ReturnsEmpty()
        {
            Assert.AreEqual("", VcsDiffPathNormalizer.ComputeProjectPrefix(null, "/x"));
            Assert.AreEqual("", VcsDiffPathNormalizer.ComputeProjectPrefix("/x", null));
            Assert.AreEqual("", VcsDiffPathNormalizer.ComputeProjectPrefix(null, null));
        }
    }
}
