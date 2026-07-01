using System.Collections.Generic;

namespace Roulin.Editor.PackRule
{
    // Project-provided asset → logical group name resolver.
    //
    // AddressablesGroupsView (the AAS walk) covers direct entries and folder-
    // entry expansion, but a project with rule-based packing (e.g. "everything
    // under Assets/foo/**/*.prefab goes to group X") knows the mapping for
    // paths that never surface as direct entries. When incremental build hands
    // a VCS-diff path set that AAS lookup can't resolve, IRoulinPackRule fills
    // the gap.
    //
    // Consumer contract:
    //   - Called at most once per build, with the VCS-diff subset (small).
    //   - Returned dict contains only paths this rule owns; unknown paths are
    //     absent (not null-valued).
    //   - Value is the *logical* group name; Roulin sanitizes it into the same
    //     bundle name AddressablesGroupsView would produce for the group, and
    //     appends "_scenes" for .unity paths.
    public interface IRoulinPackRule
    {
        IReadOnlyDictionary<string, string> ResolveGroupsForPaths(IReadOnlyList<string> assetPaths);
    }
}
