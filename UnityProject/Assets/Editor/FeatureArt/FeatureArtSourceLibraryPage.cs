using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.EditorTools.CellArt;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>源文件库页 UI 状态（挂在绑定窗上，避免左树重建丢筛选/选中）。</summary>
    public sealed class FeatureArtSourceLibraryState
    {
        public string Search = "";
        public string FilterStatus = "all";
        public string FilterKind = "all";
        public string FilterRoute = "all";
        public string SelectedId;
        public Vector2 ListScroll;
        public Vector2 DetailScroll;
    }

    /// <summary>左树顶级叶子「源文件库」：列表/过滤/详情/扫盘/图板，
    /// 读写同一份 <see cref="FeatureArtBindingWindow.Registry"/>，扫盘/图板只调 CellArtRegistryService。</summary>
    public sealed class FeatureArtSourceLibraryPage
    {
        public const string MenuPath = "源文件库";

        readonly FeatureArtBindingWindow _window;
        CellArtRegistry _cachedData;
        int _revision = -1, _assetCount = -1, _reviewCount;
        int _pathRevision = -1;
        string _search, _status, _kind, _route;
        string[] _statuses, _kinds, _routes;
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, bool> _pathExists = new Dictionary<string, bool>();
        readonly FeatureArtGui.TextArea _notes = new FeatureArtGui.TextArea(), _tripoNotes = new FeatureArtGui.TextArea();
        static GUIStyle _rowTitle, _rowSubtitle;
        const float RowHeight = 72f;
        static readonly string[] ToolbarLabels = { "扫盘预览", "扫盘入列", "生成图板", "打开图板", "资源目录" };
        internal Rect ListContentRect { get; private set; }
        internal int VisibleRowCount { get; private set; }
        sealed class Row { public CellArtAsset Asset; public string Title, Subtitle; }

        public FeatureArtSourceLibraryPage(FeatureArtBindingWindow window) => _window = window;

        [OnInspectorGUI]
        void Draw()
        {
            var data = _window.Registry;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                if (data == null)
                {
                    EditorGUILayout.HelpBox("登记表未加载。请检查 " + CellArtRegistryService.RegistryAbs, MessageType.Error);
                    return;
                }

                PrepareRows(data);
                DrawPageToolbar(data);
                DrawFilters();
                PrepareRows(data);
                var width = FeatureArtGui.Width;
                var height = Mathf.Clamp(_window.position.height - 330f, 180f, 650f);
                if (width >= 760f)
                {
                    using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
                    {
                        const float listWidth = 280f;
                        using (new FeatureArtGui.WidthScope(listWidth)) DrawList(height);
                        using (new FeatureArtGui.WidthScope(width - listWidth - 20f)) DrawDetail(data, height);
                    }
                }
                else { DrawList(Mathf.Min(height, 270f)); DrawDetail(data, height); }
            }
        }

        void DrawPageToolbar(CellArtRegistry data)
        {
            var columns = Mathf.Clamp(Mathf.FloorToInt(FeatureArtGui.Width / 78f), 1, ToolbarLabels.Length);
            for (var row = 0; row < ToolbarLabels.Length; row += columns)
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                for (var i = row; i < Mathf.Min(row + columns, ToolbarLabels.Length); i++)
                {
                    if (!GUILayout.Button(ToolbarLabels[i], EditorStyles.toolbarButton, GUILayout.Width(72))) continue;
                    switch (i)
                    {
                        case 0: _window.RunRegistryScan(false); break;
                        case 1:
                            if (EditorUtility.DisplayDialog("扫盘入列",
                            "将按命名约定扫描 Concepts/Meshes/Animations/VFX/Previews，\n自动登记新文件并绑定到已有 id。继续？",
                            "入列", "取消"))
                                _window.RunRegistryScan(true);
                            break;
                        case 2: _window.WriteRegistryBoard(); break;
                        case 3: _window.OpenRegistryBoard(); break;
                        case 4: EditorUtility.RevealInFinder(CellArtRegistryService.CellAbs); break;
                    }
                }
            }
            FeatureArtGui.Label($"{data.assets?.Count ?? 0} 项 · 待审 {_reviewCount}" + (_window.IsRegistryDirty ? " · 未保存" : ""));
        }

        void DrawFilters()
        {
            var s = _window.SourceLib;
            s.Search = FeatureArtGui.Field("搜索", s.Search);
            var width = FeatureArtGui.Width;
            if (width >= 510f)
                using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
                using (new FeatureArtGui.WidthScope((width - 12f) / 3f))
                {
                    using (new EditorGUILayout.VerticalScope()) s.FilterStatus = FilterPopup("状态", s.FilterStatus, _statuses);
                    using (new EditorGUILayout.VerticalScope()) s.FilterKind = FilterPopup("种类", s.FilterKind, _kinds);
                    using (new EditorGUILayout.VerticalScope()) s.FilterRoute = FilterPopup("用途", s.FilterRoute, _routes);
                }
            else
            {
                s.FilterStatus = FilterPopup("状态", s.FilterStatus, _statuses);
                s.FilterKind = FilterPopup("种类", s.FilterKind, _kinds);
                s.FilterRoute = FilterPopup("用途", s.FilterRoute, _routes);
            }
        }

        static string FilterPopup(string label, string current, string[] options)
        {
            var idx = Mathf.Max(0, Array.IndexOf(options, current));
            return options[FeatureArtGui.Popup(label, idx, options)];
        }

        void DrawList(float height)
        {
            var s = _window.SourceLib;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                FeatureArtGui.Label("资源列表 · " + _rows.Count + " 项");
                s.ListScroll = GUILayout.BeginScrollView(s.ListScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                    GUILayout.Width(FeatureArtGui.Width), GUILayout.Height(height));
                // Reserve the entire list once. Only visible rows need paint/thumbnail work.
                var content = GUILayoutUtility.GetRect(FeatureArtGui.Width - 20f, _rows.Count * RowHeight,
                    GUILayout.Width(FeatureArtGui.Width - 20f), GUILayout.Height(_rows.Count * RowHeight));
                ListContentRect = content;
                var first = Mathf.Max(0, Mathf.FloorToInt(s.ListScroll.y / RowHeight) - 1);
                var last = Mathf.Min(_rows.Count, first + Mathf.CeilToInt(height / RowHeight) + 3);
                VisibleRowCount = Mathf.Max(0, last - first);
                _rowTitle ??= new GUIStyle(EditorStyles.boldLabel) { wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.UpperLeft };
                _rowSubtitle ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.UpperLeft };
                for (var i = first; i < last; i++)
                {
                    var row = _rows[i];
                    var a = row.Asset;
                    var selected = a.id == s.SelectedId;
                    var rect = new Rect(content.x, content.y + i * RowHeight, content.width, RowHeight);
                    if (Event.current.type == EventType.Repaint)
                    {
                    if (selected)
                    {
                        EditorGUI.DrawRect(rect, new Color(0.24f, 0.36f, 0.55f, 0.45f));
                    }
                    else if (a.needs_review)
                    {
                        EditorGUI.DrawRect(rect, new Color(0.45f, 0.35f, 0.1f, 0.25f));
                    }

                    var thumb = FeatureArtAssetCache.Preview(a);
                    var thumbRect = new Rect(rect.x + 4, rect.y + 4, 46, 46);
                    if (thumb != null)
                    {
                        GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        EditorGUI.DrawRect(thumbRect, new Color(0.1f, 0.1f, 0.12f));
                    }

                    var textRect = new Rect(rect.x + 56, rect.y + 4, rect.width - 60, 30);
                    GUI.Label(textRect, row.Title, _rowTitle);
                    textRect.y += 32;
                    textRect.height = 32;
                    GUI.Label(textRect, row.Subtitle, _rowSubtitle);
                    }
                    if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                    {
                        s.SelectedId = a.id;
                        GUI.FocusControl(null);
                        Event.current.Use();
                        _window.Repaint();
                    }
                }

                GUILayout.EndScrollView();
            }
        }

        void DrawDetail(CellArtRegistry data, float height)
        {
            var s = _window.SourceLib;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(FeatureArtGui.Width)))
            using (new FeatureArtGui.WidthScope(FeatureArtGui.Width - 20f))
            {
                var a = _window.FindRegistryAsset(s.SelectedId);
                if (a == null)
                {
                    EditorGUILayout.LabelField("选中左侧条目查看详情");
                    return;
                }

                EditorGUILayout.LabelField("详情", EditorStyles.boldLabel);
                s.DetailScroll = GUILayout.BeginScrollView(s.DetailScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                    GUILayout.Width(FeatureArtGui.Width), GUILayout.Height(height));
                using (new FeatureArtGui.WidthScope(FeatureArtGui.Width - 20f))
                {

                EditorGUI.BeginChangeCheck();

                var tex = FeatureArtAssetCache.Preview(a);
                if (tex != null)
                {
                    var size = Mathf.Min(180f, FeatureArtGui.Width);
                    var r = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                }

                using (new EditorGUI.DisabledScope(true)) FeatureArtGui.Field("id", a.id);

                a.name_zh = FeatureArtGui.Field("中文名", a.name_zh);
                a.kind = FeatureArtGui.Field("种类（自定义）", a.kind);
                a.slot = FeatureArtGui.Field("部位（自定义）", a.slot);
                a.route = FeatureArtGui.Field("用途（自定义）", a.route);
                a.status = FeatureArtGui.Field("状态（自定义）", a.status);
                a.rarity = FeatureArtGui.Field("稀有度", a.rarity);
                a.needs_review = EditorGUILayout.Toggle("待审 needs_review", a.needs_review);

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("文件绑定", EditorStyles.boldLabel);
                a.concept = PathField("概念图 concept", a.concept, data.dirs.concepts);
                a.mesh = PathField("模型 mesh", a.mesh, data.dirs.meshes);
                a.anim = PathField("动画 anim", a.anim, data.dirs.animations);
                a.vfx = PathField("特效 vfx", a.vfx, data.dirs.vfx);
                a.preview = PathField("缩略图 preview", a.preview, data.dirs.previews);
                a.raw = FeatureArtGui.Field("Raw 路径", a.raw);

                var arch = a.archetype_id ?? -1;
                FeatureArtGui.Label("ArchetypeId (-1=空)");
                var newArch = EditorGUILayout.IntField(arch, GUILayout.Width(FeatureArtGui.Width));
                a.archetype_id = newArch < 0 ? null : newArch;

                a.notes = _notes.Draw("备注", a.notes);
                a.tripo_notes = _tripoNotes.Draw("Tripo 备注", a.tripo_notes);

                if (EditorGUI.EndChangeCheck())
                {
                    _window.MarkRegistryDirty();
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope())
                {
                    if (FeatureArtGui.Button("按文件自动推状态"))
                    {
                        a.status = CellArtRegistryService.GuessStatus(a);
                        _window.MarkRegistryDirty();
                    }

                    if (FeatureArtGui.Button("清除待审"))
                    {
                        a.needs_review = false;
                        _window.MarkRegistryDirty();
                    }

                    if (FeatureArtGui.Button("在 Project 中定位概念图") && !string.IsNullOrEmpty(a.concept))
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(CellArtRegistryService.AssetPathOf(a.concept));
                        if (obj != null)
                        {
                            EditorGUIUtility.PingObject(obj);
                            Selection.activeObject = obj;
                        }
                    }
                }

                }
                GUILayout.EndScrollView();
            }
        }

        string PathField(string label, string value, string folderHint)
        {
            FeatureArtGui.Label(label);
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                var next = EditorGUILayout.TextField(value ?? "", GUILayout.Width(Mathf.Max(20f, FeatureArtGui.Width - 72f)));
                if (GUILayout.Button("选", GUILayout.Width(32)))
                {
                    var start = System.IO.Path.Combine(CellArtRegistryService.CellAbs, folderHint);
                    var picked = EditorUtility.OpenFilePanel($"选择 {label}", start, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        var rel = CellArtRegistryService.RelOf(picked);
                        if (rel == null)
                        {
                            EditorUtility.DisplayDialog("路径无效",
                                "请选择 " + CellArtRegistryService.CellRelative + " 目录下的文件。", "好");
                        }
                        else
                        {
                            next = rel;
                            _window.MarkRegistryDirty();
                        }
                    }
                }

                if (!_pathExists.TryGetValue(next ?? "", out var exists)) _pathExists[next ?? ""] = exists = CellArtRegistryService.PathExists(next);
                GUILayout.Label(string.IsNullOrEmpty(next) ? "-" : (exists ? "OK" : "缺"), GUILayout.Width(28));
                return next;
            }
        }

        static string[] Options(IEnumerable<string> values, string current) => new[] { "all" }
            .Concat(values).Append(current).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x == "all" ? "" : x).ToArray();

        internal void PrepareRows(CellArtRegistry data)
        {
            if (_pathRevision != FeatureArtAssetCache.Revision)
            { _pathRevision = FeatureArtAssetCache.Revision; _pathExists.Clear(); }
            var state = _window.SourceLib;
            var changed = !ReferenceEquals(_cachedData, data) || _revision != _window.RegistryRevision || _assetCount != (data.assets?.Count ?? 0);
            if (changed)
            {
                _cachedData = data;
                _revision = _window.RegistryRevision;
                _assetCount = data.assets?.Count ?? 0;
                var assets = data.assets ?? new List<CellArtAsset>();
                _reviewCount = assets.Count(a => a.needs_review);
                _statuses = Options(assets.Select(a => a.status), state.FilterStatus);
                _kinds = Options(assets.Select(a => a.kind), state.FilterKind);
                _routes = Options(assets.Select(a => a.route), state.FilterRoute);
                _pathExists.Clear();
            }
            if (!changed && _search == state.Search && _status == state.FilterStatus && _kind == state.FilterKind && _route == state.FilterRoute) return;
            var filterChanged = _search != state.Search || _status != state.FilterStatus || _kind != state.FilterKind || _route != state.FilterRoute;
            _search = state.Search; _status = state.FilterStatus; _kind = state.FilterKind; _route = state.FilterRoute;
            _rows.Clear();
            foreach (var asset in Filtered(data)) _rows.Add(new Row { Asset = asset,
                Title = (asset.needs_review ? "* " : "") + asset.name_zh,
                Subtitle = $"{asset.id} · {asset.kind}/{asset.slot}/{asset.route} · {asset.status}" });
            if (filterChanged) state.ListScroll = Vector2.zero;
        }

        IEnumerable<CellArtAsset> Filtered(CellArtRegistry data)
        {
            IEnumerable<CellArtAsset> q = data.assets ?? Enumerable.Empty<CellArtAsset>();
            var s = _window.SourceLib;
            if (!string.IsNullOrWhiteSpace(s.Search))
            {
                var term = s.Search.Trim();
                q = q.Where(a =>
                    (a.id?.IndexOf(term, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (a.name_zh?.IndexOf(term, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            if (s.FilterStatus != "all")
            {
                q = q.Where(a => a.status == s.FilterStatus);
            }

            if (s.FilterKind != "all")
            {
                q = q.Where(a => a.kind == s.FilterKind);
            }

            if (s.FilterRoute != "all")
            {
                q = q.Where(a => a.route == s.FilterRoute);
            }

            return q;
        }
    }
}
