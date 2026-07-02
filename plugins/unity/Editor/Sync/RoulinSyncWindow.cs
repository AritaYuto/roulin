using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Roulin.Editor.Sync
{
    public sealed class RoulinSyncWindow : EditorWindow
    {
        private CancellationTokenSource _cts;
        private Vector2 _scroll;
        private string _status = "(idle)";

        private void OnEnable()
        {
            RoulinAssetWatcher.OnDirtyChanged += Repaint;
            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            RoulinAssetWatcher.OnDirtyChanged -= Repaint;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("roulin-server", EditorStyles.boldLabel);
            var settings = RoulinEditorSettings.instance;
            using (new EditorGUI.DisabledScope(_cts != null))
            {
                EditorGUI.BeginChangeCheck();
                var url = EditorGUILayout.TextField("URL", settings.ServerUrl);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.ServerUrl = url;
                }
            }

            EditorGUILayout.Space(8);
            var dirty = RoulinAssetWatcher.Dirty.ToArray();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"Pending changes ({dirty.Length})",
                    EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(_cts != null))
                {
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    {
                        _ = RefreshAsync();
                    }
                }
            }

            using (var scope = new EditorGUILayout.ScrollViewScope(_scroll, GUILayout.MinHeight(120)))
            {
                _scroll = scope.scrollPosition;
                if (dirty.Length == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No uncommitted, pack-rule-claimed assets. Edit an Addressables asset and Refresh.",
                        MessageType.Info);
                }
                else
                {
                    foreach (var path in dirty)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(path, GUILayout.ExpandWidth(true));
                            using (new EditorGUI.DisabledScope(_cts != null))
                            {
                                if (GUILayout.Button("×", GUILayout.Width(24)))
                                {
                                    RoulinAssetWatcher.Remove(path);
                                }
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_cts != null || dirty.Length == 0))
                {
                    if (GUILayout.Button($"Sync {dirty.Length} change(s)", GUILayout.Height(28)))
                    {
                        Run(dirty);
                    }
                }

                using (new EditorGUI.DisabledScope(_cts == null))
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(28)))
                    {
                        _cts?.Cancel();
                    }
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("status", _status, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("build target",
                EditorUserBuildSettings.activeBuildTarget.ToString(),
                EditorStyles.miniLabel);
        }

        [MenuItem("Roulin/Sync")]
        public static void Open()
        {
            var window = GetWindow<RoulinSyncWindow>("Roulin Sync");
            window.minSize = new Vector2(420, 280);
            window.Show();
        }

        private async Task RefreshAsync()
        {
            _cts = new CancellationTokenSource();
            _status = "refreshing…";
            Repaint();
            try
            {
                await RoulinAssetWatcher.RefreshAsync(
                    RoulinEditorSettings.instance.ServerUrl, _cts.Token);
                _status = $"refreshed ({RoulinAssetWatcher.Dirty.Count} pending)";
            }
            catch (OperationCanceledException)
            {
                _status = "refresh cancelled";
            }
            catch (Exception ex)
            {
                _status = $"refresh failed: {ex.Message}";
                Debug.LogException(ex);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                Repaint();
            }
        }

        private async void Run(string[] paths)
        {
            _cts = new CancellationTokenSource();
            _status = $"syncing {paths.Length} change(s)…";
            Repaint();
            try
            {
                var relayed = await RoulinSyncService.SyncAsync(
                    paths, RoulinEditorSettings.instance.ServerUrl, _cts.Token);
                _status = $"synced {relayed} change(s) → server broadcast";
                await RoulinAssetWatcher.RefreshAsync(
                    RoulinEditorSettings.instance.ServerUrl, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                _status = "cancelled";
            }
            catch (Exception ex)
            {
                _status = $"failed: {ex.Message}";
                Debug.LogException(ex);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                Repaint();
            }
        }
    }
}
