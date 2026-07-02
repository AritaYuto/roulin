using UnityEditor;
using UnityEngine;

namespace Roulin.Editor.Build
{
    // One-shot switch: when set, the next build skips the incremental delta
    // path and publishes a full parcel even if the server returns a base
    // revision. Cleared automatically after the next build consumes it.
    //
    // Needed for schema migrations: when the schema gains a new field that
    // server-side merge depends on, the existing base Index lacks that field
    // and merge cannot map current bundles back to base entries. The escape
    // hatch is to force one full publish so the new base carries the new
    // schema; subsequent incremental builds work as normal.
    public static class RoulinForceFullPublish
    {
        private const string PrefKey = "Roulin.ForceFullPublish";
        private const string CliFlag = "-roulinForceFullPublish";

        public static bool ConsumeForNextBuild()
        {
            if (HasCliFlag())
            {
                return true;
            }
            if (EditorPrefs.GetBool(PrefKey, false))
            {
                EditorPrefs.DeleteKey(PrefKey);
                return true;
            }
            return false;
        }

        private static bool HasCliFlag()
        {
            foreach (var a in System.Environment.GetCommandLineArgs())
            {
                if (a == CliFlag) return true;
            }
            return false;
        }

        [MenuItem("Roulin/BuildOptions/Force Full Publish on Next Build")]
        private static void Arm()
        {
            EditorPrefs.SetBool(PrefKey, true);
            Debug.Log(
                "[Roulin] Next build will publish a full parcel " +
                "(base_revision omitted). Flag clears after one build.");
        }

        [MenuItem("Roulin/BuildOptions/Force Full Publish on Next Build", validate = true)]
        private static bool ArmValidate()
        {
            return !EditorPrefs.GetBool(PrefKey, false);
        }

        [MenuItem("Roulin/BuildOptions/Cancel Force Full Publish")]
        private static void Cancel()
        {
            EditorPrefs.DeleteKey(PrefKey);
            Debug.Log("[Roulin] Force-full-publish flag cleared.");
        }

        [MenuItem("Roulin/BuildOptions/Cancel Force Full Publish", validate = true)]
        private static bool CancelValidate()
        {
            return EditorPrefs.GetBool(PrefKey, false);
        }
    }
}
