// Output contract:
//   - Immediate: one-hop union over an asset's file list; may include the
//     primary bundle (its file appears at index 0 of its own list).
//   - Expanded: BFS closure minus Immediate.
//   - Ordering within each list is unspecified; assert as sets.

using Roulin.Editor.Build.CustomBuildTasks;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEditor;

namespace Roulin.Editor.Tests
{
    public class RoulinBundleDepClosureTests
    {


        private static GUID G(int n)
        {
            return new GUID(n.ToString("x32"));
        }

        // Helper: build a fileToBundle map from bundleName -> file list.
        // Each bundle's first file is "f_<bundle>_primary".
        private static (Dictionary<string, string> fileToBundle,
            Dictionary<GUID, List<string>> assetToFiles)
            Graph(params (int assetId, string primaryBundle, string[] depBundles)[] assets)
        {
            var fileToBundle = new Dictionary<string, string>();
            var assetToFiles = new Dictionary<GUID, List<string>>();

            foreach (var (id, primary, deps) in assets)
            {
                // Per-asset primary file → assets in the same bundle still get
                // distinct files[0] entries (matches SBP).
                var primaryFile = $"f_a{id}_primary";
                fileToBundle[primaryFile] = primary;

                var files = new List<string> { primaryFile };
                foreach (var d in deps)
                {
                    var depFile = $"f_a{id}_via_{d}";
                    fileToBundle[depFile] = d;
                    files.Add(depFile);
                }

                assetToFiles[G(id)] = files;
            }

            return (fileToBundle, assetToFiles);
        }

        private static void AssertSet(IEnumerable<string> actual, params string[] expected)
        {
            var a = new HashSet<string>(actual);
            var e = new HashSet<string>(expected);
            Assert.IsTrue(
                a.SetEquals(e),
                $"expected=[{string.Join(",", e)}], actual=[{string.Join(",", a)}]");
        }



        [Test]
        public void Empty_ReturnsEmptyDicts()
        {
            var (f2b, a2f) = (new Dictionary<string, string>(), new Dictionary<GUID, List<string>>());

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            Assert.AreEqual(0, r.Immediate.Count);
            Assert.AreEqual(0, r.Expanded.Count);
        }

        [Test]
        public void SingleBundle_OneAssetNoExternalDeps_ImmediateContainsSelf()
        {
            // Asset's file list always includes its primary file → self ∈ Immediate.
            var (f2b, a2f) = Graph(
                (assetId: 1, primaryBundle: "A", depBundles: new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A");
            AssertSet(r.Expanded["A"]); // empty
        }

        [Test]
        public void LinearChain_AtoBtoC_ExpandedMinusImmediate()
        {
            // A's asset references a file in B; B's asset references a file in C.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B" }),
                (2, "B", new[] { "C" }),
                (3, "C", new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A", "B");
            AssertSet(r.Immediate["B"], "B", "C");
            AssertSet(r.Immediate["C"], "C");

            AssertSet(r.Expanded["A"], "C"); // C reachable via B, not in Immediate
            AssertSet(r.Expanded["B"]);
            AssertSet(r.Expanded["C"]);
        }

        [Test]
        public void Diamond_NoDoubleCountInExpanded()
        {
            // A → {B, C}, both → D. A's expanded should be just {D}, not {B,C,D}.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B", "C" }),
                (2, "B", new[] { "D" }),
                (3, "C", new[] { "D" }),
                (4, "D", new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A", "B", "C");
            AssertSet(r.Expanded["A"], "D");
        }

        [Test]
        public void Cycle_AtoBtoA_SelfReachableThroughCycle()
        {
            // Expanded subtracts Immediate, so self doesn't reappear via the cycle.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B" }),
                (2, "B", new[] { "A" }));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A", "B");
            AssertSet(r.Immediate["B"], "A", "B");
            AssertSet(r.Expanded["A"]);
            AssertSet(r.Expanded["B"]);
        }

        [Test]
        public void MultiAssetSameBundle_UnionDeps()
        {
            // Two assets in bundle A — one referencing B, one referencing C.
            // A's Immediate should be the union {A, B, C}.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B" }),
                (2, "A", new[] { "C" }),
                (3, "B", new string[0]),
                (4, "C", new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A", "B", "C");
            AssertSet(r.Expanded["A"]); // B and C have no further deps
        }

        [Test]
        public void DisconnectedComponents_NoCrossPollination()
        {
            // Two separate sub-graphs: A→B and X→Y.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B" }),
                (2, "B", new string[0]),
                (3, "X", new[] { "Y" }),
                (4, "Y", new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            AssertSet(r.Immediate["A"], "A", "B");
            AssertSet(r.Expanded["A"]);
            AssertSet(r.Immediate["X"], "X", "Y");
            AssertSet(r.Expanded["X"]);

            // Cross-graph names must not appear in either side.
            Assert.IsFalse(new HashSet<string>(r.Immediate["A"]).Contains("X"));
            Assert.IsFalse(new HashSet<string>(r.Expanded["A"]).Contains("X"));
        }

        [Test]
        public void Output_KeyedByAllReachableBundles_IncludingLeaves()
        {
            // Leaf bundles must still appear as keys.
            var (f2b, a2f) = Graph(
                (1, "A", new[] { "B" }),
                (2, "B", new string[0]));

            var r = RoulinBundleDepClosure.Compute(f2b, a2f);

            Assert.IsTrue(r.Immediate.ContainsKey("A"));
            Assert.IsTrue(r.Immediate.ContainsKey("B"));
            Assert.IsTrue(r.Expanded.ContainsKey("A"));
            Assert.IsTrue(r.Expanded.ContainsKey("B"));
        }
    }
}