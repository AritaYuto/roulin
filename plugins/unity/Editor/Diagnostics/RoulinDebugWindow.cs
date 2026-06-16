using Roulin.Editor;
using Roulin.Editor.Build;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Roulin.Editor.Diagnostics
{
    public class RoulinDebugWindow : EditorWindow
    {
        private const string ReportPath =
            "Library/roulin/build/roulin-build-report.json";

        private readonly List<BuildReportBundle> _filtered = new();
        private ListView _bundleList;
        private ScrollView _detailScroll;
        private readonly Dictionary<string, string> _hashToBundleName = new(StringComparer.OrdinalIgnoreCase);



        private BuildReport _report;



        private Label _revLabel;
        private TextField _searchField;
        private BuildReportBundle _selected;
        private Label _totalsLabel;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            BuildHeader(root);
            BuildSearch(root);
            BuildSplit(root);

            LoadAndRefresh();
        }



        [MenuItem("Roulin/Debug Window")]
        public static void Open()
        {
            var w = GetWindow<RoulinDebugWindow>("Roulin Debug");
            w.minSize = new Vector2(640, 360);
            w.Show();
        }



        private void BuildHeader(VisualElement parent)
        {
            var row = NewRow();
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;

            row.Add(new Button(LoadAndRefresh) { text = "↻ Refresh" });

            _revLabel = new Label("revision: -")
            {
                style =
                {
                    marginLeft = 12,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            row.Add(_revLabel);

            _totalsLabel = new Label(string.Empty)
            {
                style = { marginLeft = 12, color = new StyleColor(new Color(0.6f, 0.6f, 0.6f)) }
            };
            row.Add(_totalsLabel);

            parent.Add(row);
        }

        private void BuildSearch(VisualElement parent)
        {
            _searchField = new TextField("Search")
            {
                style = { marginBottom = 6 },
                tooltip = "matches address / bundle / label (substring, case-insensitive)"
            };
            _searchField.RegisterValueChangedCallback(_ => ApplyFilter());
            parent.Add(_searchField);
        }

        private void BuildSplit(VisualElement parent)
        {
            var split = new TwoPaneSplitView(0, 280f, TwoPaneSplitViewOrientation.Horizontal)
            {
                style = { flexGrow = 1 }
            };

            // Left: bundle ListView with virtualization built-in.
            _bundleList = new ListView
            {
                fixedItemHeight = 38,
                makeItem = MakeBundleRow,
                bindItem = BindBundleRow,
                selectionType = SelectionType.Single,
                style = { flexGrow = 1 }
            };
            _bundleList.selectionChanged += OnBundleSelectionChanged;

            var leftPane = new VisualElement { style = { flexGrow = 1 } };
            leftPane.Add(_bundleList);
            split.Add(leftPane);

            // Right: free-form ScrollView.
            _detailScroll = new ScrollView { style = { flexGrow = 1, paddingLeft = 8 } };
            split.Add(_detailScroll);

            parent.Add(split);
        }



        private static VisualElement MakeBundleRow()
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Column,
                    paddingLeft = 4,
                    paddingRight = 4,
                    paddingTop = 2
                }
            };
            row.Add(new Label(string.Empty)
            {
                name = "name",
                style = { unityFontStyleAndWeight = FontStyle.Bold }
            });
            row.Add(new Label(string.Empty)
            {
                name = "info",
                style =
                {
                    fontSize = 10,
                    color = new StyleColor(new Color(0.55f, 0.55f, 0.55f))
                }
            });
            return row;
        }

        private void BindBundleRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _filtered.Count)
            {
                return;
            }

            var b = _filtered[index];
            row.Q<Label>("name").text = b.name ?? "(unnamed)";
            row.Q<Label>("info").text =
                $"{RoulinUtil.FormatBytes(b.size_bytes)}    {b.entries.Count} entries / {b.dependency_hashes.Count} dep";
        }

        private void OnBundleSelectionChanged(IEnumerable<object> selection)
        {
            _selected = null;
            foreach (var o in selection)
            {
                if (o is BuildReportBundle b)
                {
                    _selected = b;
                    break;
                }
            }

            RebuildDetailPane();
        }



        private void RebuildDetailPane()
        {
            _detailScroll.Clear();
            if (_selected == null)
            {
                _detailScroll.Add(new Label("(select a bundle)")
                {
                    style =
                    {
                        marginTop = 12, marginLeft = 4,
                        color = new StyleColor(new Color(0.55f, 0.55f, 0.55f))
                    }
                });
                return;
            }

            var b = _selected;
            _detailScroll.Add(Title(b.name ?? "(unnamed)"));
            _detailScroll.Add(KV("Binary hash", b.binary_hash));
            _detailScroll.Add(KV("Size", RoulinUtil.FormatBytes(b.size_bytes)));

            _detailScroll.Add(SectionLabel($"Entries ({b.entries.Count})"));
            foreach (var e in b.entries)
            {
                _detailScroll.Add(EntryRow(e));
            }

            if (b.dependency_hashes.Count > 0)
            {
                _detailScroll.Add(SectionLabel($"Dependencies ({b.dependency_hashes.Count})"));
                foreach (var h in b.dependency_hashes)
                {
                    var name = _hashToBundleName.TryGetValue(h, out var n) ? n : "<unknown>";
                    _detailScroll.Add(new Label($"  {name}   ({Truncate(h, 16)}…)"));
                }
            }
        }



        private void LoadAndRefresh()
        {
            _report = LoadReport();
            _hashToBundleName.Clear();
            if (_report != null)
            {
                foreach (var b in _report.bundles)
                {
                    if (!string.IsNullOrEmpty(b.binary_hash))
                    {
                        _hashToBundleName[b.binary_hash] = b.name;
                    }
                }
            }

            UpdateHeader();
            ApplyFilter();
        }

        private static BuildReport LoadReport()
        {
            if (!File.Exists(ReportPath))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<BuildReport>(File.ReadAllText(ReportPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RoulinDebug] failed to read {ReportPath}: {e.Message}");
                return null;
            }
        }

        private void UpdateHeader()
        {
            if (_report == null)
            {
                _revLabel.text = "revision: (no build report)";
                _totalsLabel.text = $"expected at: {ReportPath} — run Roulin > Build Parcel first";
                return;
            }

            _revLabel.text = $"revision: {_report.revision}";
            long total = 0;
            foreach (var b in _report.bundles)
            {
                total += b.size_bytes;
            }

            _totalsLabel.text =
                $"{_report.bundle_count} bundles · {_report.entry_count} entries · {RoulinUtil.FormatBytes(total)}";
        }

        private void ApplyFilter()
        {
            var q = (_searchField?.value ?? string.Empty).Trim();
            _filtered.Clear();
            if (_report != null)
            {
                foreach (var b in _report.bundles)
                {
                    if (q.Length == 0 || BundleMatches(b, q))
                    {
                        _filtered.Add(b);
                    }
                }
            }

            _bundleList.itemsSource = _filtered;
            _bundleList.RefreshItems();

            // Drop selection if it fell out of the filter.
            if (_selected != null && !_filtered.Contains(_selected))
            {
                _selected = null;
                RebuildDetailPane();
            }
        }

        private static bool BundleMatches(BuildReportBundle b, string q)
        {
            if (Contains(b.name, q))
            {
                return true;
            }

            foreach (var e in b.entries)
            {
                if (Contains(e.address, q))
                {
                    return true;
                }

                if (e.labels != null)
                {
                    foreach (var l in e.labels)
                    {
                        if (Contains(l, q))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool Contains(string s, string q)
        {
            return !string.IsNullOrEmpty(s) &&
                   s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }



        private static VisualElement NewRow()
        {
            return new VisualElement
            {
                style = { flexDirection = FlexDirection.Row }
            };
        }

        private static Label Title(string text)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = 14,
                    marginTop = 4,
                    marginBottom = 4
                }
            };
        }

        private static VisualElement KV(string key, string value)
        {
            var row = NewRow();
            row.Add(new Label(key + ":")
            {
                style = { width = 110, color = new StyleColor(new Color(0.55f, 0.55f, 0.55f)) }
            });
            row.Add(new Label(value ?? string.Empty) { style = { flexGrow = 1 } });
            return row;
        }

        private static Label SectionLabel(string text)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 10,
                    marginBottom = 4
                }
            };
        }

        private static VisualElement EntryRow(BuildReportEntry e)
        {
            var row = NewRow();
            row.style.paddingLeft = 8;
            row.Add(new Label(e.address) { style = { flexGrow = 1 } });
            if (e.labels != null && e.labels.Count > 0)
            {
                row.Add(new Label("[" + string.Join(", ", e.labels) + "]")
                {
                    style = { color = new StyleColor(new Color(0.6f, 0.75f, 0.45f)) }
                });
            }

            return row;
        }


        private static string Truncate(string s, int n)
        {
            return string.IsNullOrEmpty(s) ? string.Empty :
                s.Length <= n ? s : s.Substring(0, n);
        }
    }
}