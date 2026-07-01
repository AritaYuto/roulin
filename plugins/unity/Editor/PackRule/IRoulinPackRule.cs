using System.Collections.Generic;

namespace Roulin.Editor.PackRule
{
    // Project-provided asset → logical group name resolver. Values are group
    // names; Roulin sanitises them to bundle names on its side.
    public interface IRoulinPackRule
    {
        IReadOnlyDictionary<string, string> ResolveGroupsForPaths(IReadOnlyList<string> assetPaths);
    }
}
