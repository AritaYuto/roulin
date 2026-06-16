#if !USE_ROULIN
using UnityEditor;
using UnityEngine;

namespace Roulin.Editor
{
    // Warns when USE_ROULIN is absent so developers know the define needs to be added manually.
    [InitializeOnLoad]
    static class SetupChecker
    {
        static SetupChecker()
        {
            Debug.LogWarning(
                "[roulin] USE_ROULIN is not defined in Scripting Define Symbols. " +
                "Add USE_ROULIN under ProjectSettings > Player > Scripting Define Symbols to enable the package.");
        }
    }
}
#endif
