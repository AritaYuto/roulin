using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Roulin.Editor.PackRule
{
    public struct PackRuleApplyResult
    {
        public int GroupsCreated { get; set; }
        public int GroupsRemoved { get; set; }
        public int EntriesAdded { get; set; }
        public int EntriesRemoved { get; set; }
        public int EntriesMoved { get; set; }
        public int AddressesReassigned { get; set; }
        public int LabelsChanged { get; set; }
        public IReadOnlyList<string> ModifiedGroupNames { get; set; }
    }

    public readonly struct PackAssignment
    {
        public string GroupName { get; }
        public string Address { get; }
        public IReadOnlyList<string> Labels { get; }

        public PackAssignment(
            string groupName,
            string address = null,
            IReadOnlyList<string> labels = null)
        {
            GroupName = groupName;
            Address = address;
            Labels = labels;
        }
    }

    public interface IRoulinRuleSet
    {
        PackAssignment? ResolveAssignment(string assetPath);
    }

    public interface IRoulinPackRule
    {
        IReadOnlyDictionary<string, string> ResolveGroupsForPaths(
            IReadOnlyList<string> assetPaths);
    }

    public interface IRoulinPackRuleApplier : IRoulinPackRule
    {
        List<IRoulinRuleSet> RuleSets { get; }
        PackRuleApplyResult Apply(AddressableAssetSettings aas);
    }

    public sealed class RoulinPackRule : IRoulinPackRuleApplier
    {
        public List<IRoulinRuleSet> RuleSets { get; } = new List<IRoulinRuleSet>();

        public PackRuleApplyResult Apply(AddressableAssetSettings aas)
        {
            var paths = CollectUnityAssetPaths();
            var (desired, conflicts) = Bucketize(paths);
            if (conflicts != null) ThrowConflicts(conflicts);

            var reconciler = new AasReconciler(aas);
            try
            {
                AssetDatabase.StartAssetEditing();

                var actualPaths = reconciler.SnapshotEntryPaths();
                foreach (var (path, assignment) in desired)
                {
                    reconciler.EnsureEntry(path, assignment);
                }
                foreach (var path in actualPaths)
                {
                    if (!desired.ContainsKey(path)) reconciler.RemoveEntryByPath(path);
                }
                reconciler.RemoveEmptyGroups();
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }
            return reconciler.ToResult();
        }

        public IReadOnlyDictionary<string, string> ResolveGroupsForPaths(
            IReadOnlyList<string> assetPaths)
        {
            var (assigned, conflicts) = Bucketize(assetPaths);
            if (conflicts != null) ThrowConflicts(conflicts);
            var result = new Dictionary<string, string>(assigned.Count, StringComparer.Ordinal);
            foreach (var (path, assignment) in assigned) result[path] = assignment.GroupName;
            return result;
        }

        private (Dictionary<string, PackAssignment> assigned, List<string> conflicts)
            Bucketize(IReadOnlyList<string> paths)
        {
            var assigned = new Dictionary<string, PackAssignment>(StringComparer.Ordinal);
            List<string> conflicts = null;
            foreach (var path in paths)
            {
                PackAssignment? first = null;
                bool conflict = false;
                foreach (var ruleSet in RuleSets)
                {
                    var assignment = ruleSet.ResolveAssignment(path);
                    if (assignment == null) continue;
                    if (first == null) { first = assignment; continue; }
                    conflict = true;
                    break;
                }
                if (conflict) (conflicts ??= new List<string>()).Add(path);
                else if (first != null) assigned[path] = first.Value;
            }
            return (assigned, conflicts);
        }

        private static IReadOnlyList<string> CollectUnityAssetPaths()
        {
            var allPaths = AssetDatabase.GetAllAssetPaths();
            var result = new List<string>(allPaths.Length);
            foreach (var path in allPaths)
            {
                if (path.StartsWith("Assets/", StringComparison.Ordinal)) result.Add(path);
            }
            return result;
        }

        private static void ThrowConflicts(List<string> conflicts) =>
            throw new InvalidOperationException(
                "RoulinPackRule: asset path(s) claimed by more than one rule set. " +
                $"Conflicting paths ({conflicts.Count}): {string.Join(", ", conflicts)}");
    }
}
