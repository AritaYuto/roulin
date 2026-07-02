using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Roulin.Editor.PackRule
{
    public sealed class AasReconciler
    {
        private readonly AddressableAssetSettings _aas;
        private readonly Dictionary<string, AddressableAssetGroup> _groupsByName;
        private readonly HashSet<string> _modifiedGroupNames = new HashSet<string>(StringComparer.Ordinal);

        public int GroupsCreated { get; private set; }
        public int GroupsRemoved { get; private set; }
        public int EntriesAdded { get; private set; }
        public int EntriesRemoved { get; private set; }
        public int EntriesMoved { get; private set; }
        public int AddressesReassigned { get; private set; }
        public int LabelsChanged { get; private set; }

        public AasReconciler(AddressableAssetSettings aas)
        {
            _aas = aas;
            _groupsByName = new Dictionary<string, AddressableAssetGroup>(StringComparer.Ordinal);
            foreach (var group in aas.groups)
            {
                if (group != null) _groupsByName[group.Name] = group;
            }
        }

        public IReadOnlyList<string> SnapshotEntryPaths()
        {
            var paths = new List<string>();
            foreach (var group in _aas.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries) paths.Add(entry.AssetPath);
            }
            return paths;
        }

        public void EnsureEntry(string assetPath, PackAssignment want)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) 
                return;

            var group = GetOrCreateGroup(want.GroupName);
            var existing = _aas.FindAssetEntry(guid);
            var oldParent = existing?.parentGroup;
            var entry = _aas.CreateOrMoveEntry(guid, group, false, false);

            if (existing == null)
            {
                EntriesAdded++;
                _modifiedGroupNames.Add(want.GroupName);
            }
            else if (oldParent != group)
            {
                EntriesMoved++;
                _modifiedGroupNames.Add(want.GroupName);
                if (oldParent != null) _modifiedGroupNames.Add(oldParent.Name);
            }

            var wantAddress = want.Address ?? assetPath;
            if (entry.address != wantAddress)
            {
                entry.address = wantAddress;
                AddressesReassigned++;
                _modifiedGroupNames.Add(want.GroupName);
            }

            if (SyncLabels(entry, want.Labels))
            {
                LabelsChanged++;
                _modifiedGroupNames.Add(want.GroupName);
            }
        }

        public void RemoveEntryByPath(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) 
                return;
            
            var entry = _aas.FindAssetEntry(guid);
            if (entry == null) 
                return;
            
            var groupName = entry.parentGroup?.Name;
            _aas.RemoveAssetEntry(guid, false);
            EntriesRemoved++;

            if (groupName != null) 
                _modifiedGroupNames.Add(groupName);
        }

        public void RemoveEmptyGroups()
        {
            var snapshot = new List<AddressableAssetGroup>(_aas.groups);
            foreach (var group in snapshot)
            {
                if (group == null || group.ReadOnly) continue;
                if (group.entries.Count > 0) continue;
                var name = group.Name;
                _aas.RemoveGroup(group);
                _groupsByName.Remove(name);
                GroupsRemoved++;
                _modifiedGroupNames.Add(name);
            }
        }

        public PackRuleApplyResult ToResult()
        {
            var names = new string[_modifiedGroupNames.Count];
            _modifiedGroupNames.CopyTo(names);
            return new PackRuleApplyResult
            {
                GroupsCreated = GroupsCreated,
                GroupsRemoved = GroupsRemoved,
                EntriesAdded = EntriesAdded,
                EntriesRemoved = EntriesRemoved,
                EntriesMoved = EntriesMoved,
                AddressesReassigned = AddressesReassigned,
                LabelsChanged = LabelsChanged,
                ModifiedGroupNames = names,
            };
        }

        private AddressableAssetGroup GetOrCreateGroup(string name)
        {
            if (_groupsByName.TryGetValue(name, out var existing))
                return existing;

            List<AddressableAssetGroupSchema> template = null;
            var defaultGroup = _aas.DefaultGroup;
            if (defaultGroup != null
                && defaultGroup.HasSchema<BundledAssetGroupSchema>()
                && defaultGroup.Schemas.Count > 0)
            {
                template = new List<AddressableAssetGroupSchema>(defaultGroup.Schemas);
            }
            var created = template != null
                ? _aas.CreateGroup(name, false, false, false, template)
                : _aas.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema));
            GroupsCreated++;

            _groupsByName[name] = created;
            _modifiedGroupNames.Add(name);
            return created;
        }

        private static bool SyncLabels(AddressableAssetEntry entry, IReadOnlyList<string> want)
        {
            var current = entry.labels;
            var wantSet = want ?? (IReadOnlyList<string>)Array.Empty<string>();

            if (current.Count == wantSet.Count)
            {
                bool same = true;
                foreach (var label in wantSet)
                {
                    if (!current.Contains(label)) { same = false; break; }
                }
                if (same) return false;
            }

            var toRemove = new List<string>(current);
            foreach (var label in toRemove) 
                entry.SetLabel(label, false, false, false);
            foreach (var label in wantSet) 
                entry.SetLabel(label, true, true, false);
            return true;
        }
    }
}
