using UnityEditor;
using UnityEngine;

namespace Roulin.Editor
{
    [FilePath("UserSettings/RoulinEditorSettings.asset",
        FilePathAttribute.Location.ProjectFolder)]
    public sealed class RoulinEditorSettings : ScriptableSingleton<RoulinEditorSettings>
    {
        [SerializeField]
        private string _serverUrl = "http://localhost:8765";

        [SerializeField]
        private string _manualRevision = "";

        [SerializeField]
        private string _bundleOutputDir = "Library/roulin/build";

        [SerializeField]
        private bool _verbose;

        public string ServerUrl { get => _serverUrl; set => SetField(ref _serverUrl, value); }

        // Empty = auto-derive (git SHA, fallback UTC timestamp). Non-empty = verbatim.
        public string ManualRevision { get => _manualRevision; set => SetField(ref _manualRevision, value); }

        public string BundleOutputDir { get => _bundleOutputDir; set => SetField(ref _bundleOutputDir, value); }

        // Per-bundle / per-step detail logs. Aggregate summaries always emit.
        public bool Verbose { get => _verbose; set => SetField(ref _verbose, value); }

        private void SetField<T>(ref T storage, T value)
        {
            if (Equals(storage, value))
            {
                return;
            }

            storage = value;
            Save(true);
        }
    }

    internal static class RoulinEditorSettingsProvider
    {
        private const string Path = "Project/Roulin";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.Project)
            {
                label = "Roulin",
                guiHandler = _ =>
                {
                    var s = RoulinEditorSettings.instance;

                    EditorGUILayout.LabelField("roulin-server", EditorStyles.boldLabel);
                    EditorGUI.BeginChangeCheck();
                    var url = EditorGUILayout.TextField(
                        new GUIContent("Server URL",
                            "roulin-server base URL. Local dev default: http://localhost:8765"),
                        s.ServerUrl);
                    if (EditorGUI.EndChangeCheck())
                    {
                        s.ServerUrl = url;
                    }

                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();
                    var mr = EditorGUILayout.TextField(
                        new GUIContent("Manual Revision",
                            "Override the revision id sent as POST /parcels/{revision}. " +
                            "Leave empty to auto-derive from git rev-parse HEAD " +
                            "(falls back to UTC timestamp if git is unavailable)."),
                        s.ManualRevision);
                    if (EditorGUI.EndChangeCheck())
                    {
                        s.ManualRevision = mr;
                    }

                    EditorGUI.BeginChangeCheck();
                    var od = EditorGUILayout.TextField(
                        new GUIContent("Bundle Output Dir",
                            "Where SBP writes intermediate .bundle files before they are uploaded."),
                        s.BundleOutputDir);
                    if (EditorGUI.EndChangeCheck())
                    {
                        s.BundleOutputDir = od;
                    }

                    EditorGUI.BeginChangeCheck();
                    var vb = EditorGUILayout.Toggle(
                        new GUIContent("Verbose logging",
                            "Emit per-bundle / per-SBP-task detail logs. Off = aggregate " +
                            "summaries only (~70 lines/build vs ~3000)."),
                        s.Verbose);
                    if (EditorGUI.EndChangeCheck())
                    {
                        s.Verbose = vb;
                    }

                    EditorGUILayout.Space(8);
                    EditorGUILayout.HelpBox(
                        "Stored in UserSettings/RoulinEditorSettings.asset (per-developer, " +
                        "git-ignored by Unity convention).",
                        MessageType.Info);
                },
                keywords = new[]
                {
                    "roulin", "server", "url", "asset", "roulin-server",
                    "revision", "build", "bundle"
                }
            };
        }
    }
}