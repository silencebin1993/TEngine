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

        static readonly string[] StatusOptions =
            { "todo", "concept", "tripo", "blender", "unity", "done" };

        static readonly string[] KindOptions =
            { "base", "module", "enemy", "boss", "env", "ui", "vfx", "anim" };

        static readonly string[] SlotOptions =
            { "none", "core", "membrane", "maw", "motility", "appendage", "territory" };

        static readonly string[] RouteOptions =
            { "none", "Devour", "Agile", "Electric", "Spore", "Nest", "Corrupt" };

        readonly FeatureArtBindingWindow _window;

        public FeatureArtSourceLibraryPage(FeatureArtBindingWindow window) => _window = window;

        [OnInspectorGUI]
        void Draw()
        {
            var data = _window.Registry;
            using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(_window.ContentMaxWidth()), GUILayout.ExpandWidth(true)))
            {
                if (data == null)
                {
                    EditorGUILayout.HelpBox("登记表未加载。确认 Assets/GameRes/Art/Cell/registry.json 存在。", MessageType.Error);
                    return;
                }

                DrawPageToolbar(data);
                DrawFilters();

                using (new EditorGUILayout.HorizontalScope())
                {
                    var listW = Mathf.Clamp(_window.ContentMaxWidth() * 0.42f, 260f, 420f);
                    DrawList(data, GUILayout.Width(listW), GUILayout.MinHeight(360f), GUILayout.ExpandHeight(true));
                    DrawDetail(data);
                }
            }
        }

        void DrawPageToolbar(CellArtRegistry data)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("扫盘预览", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    _window.RunRegistryScan(false);
                }

                if (GUILayout.Button("扫盘入列", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    if (EditorUtility.DisplayDialog("扫盘入列",
                            "将按命名约定扫描 Concepts/Meshes/Animations/VFX/Previews，\n自动登记新文件并绑定到已有 id。继续？",
                            "入列", "取消"))
                    {
                        _window.RunRegistryScan(true);
                    }
                }

                if (GUILayout.Button("生成图板", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    _window.WriteRegistryBoard();
                }

                if (GUILayout.Button("打开图板", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    _window.OpenRegistryBoard();
                }

                if (GUILayout.Button("资源目录", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    EditorUtility.RevealInFinder(CellArtRegistryService.CellAbs);
                }

                GUILayout.FlexibleSpace();
                var review = data.assets?.Count(a => a.needs_review) ?? 0;
                var count = data.assets?.Count ?? 0;
                var label = $"{count} 项 · 待审 {review}" + (_window.IsRegistryDirty ? " · 未保存" : "");
                GUILayout.Label(label, EditorStyles.miniLabel);
            }
        }

        void DrawFilters()
        {
            var s = _window.SourceLib;
            using (new EditorGUILayout.HorizontalScope())
            {
                s.Search = EditorGUILayout.TextField("搜索", s.Search);
                s.FilterStatus = FilterPopup("状态", s.FilterStatus, StatusOptions);
                s.FilterKind = FilterPopup("种类", s.FilterKind, KindOptions);
                s.FilterRoute = FilterPopup("路线", s.FilterRoute, RouteOptions);
            }
        }

        static string FilterPopup(string label, string current, string[] options)
        {
            var list = new List<string> { "all" };
            list.AddRange(options);
            var idx = Mathf.Max(0, list.IndexOf(current));
            idx = EditorGUILayout.Popup(label, idx, list.ToArray(), GUILayout.Width(160));
            return list[idx];
        }

        void DrawList(CellArtRegistry data, params GUILayoutOption[] opts)
        {
            var s = _window.SourceLib;
            using (new EditorGUILayout.VerticalScope(opts))
            {
                EditorGUILayout.LabelField("资源列表", EditorStyles.boldLabel);
                s.ListScroll = EditorGUILayout.BeginScrollView(s.ListScroll);
                var filtered = Filtered(data).ToList();
                for (var i = 0; i < filtered.Count; i++)
                {
                    var a = filtered[i];
                    var selected = a.id == s.SelectedId;
                    var title = $"{(a.needs_review ? "* " : "")}{a.name_zh}";
                    var sub = $"{a.id}  ·  {a.kind}/{a.slot}/{a.route}  ·  {a.status}";

                    var rect = GUILayoutUtility.GetRect(0, 54, GUILayout.ExpandWidth(true));
                    if (selected)
                    {
                        EditorGUI.DrawRect(rect, new Color(0.24f, 0.36f, 0.55f, 0.45f));
                    }
                    else if (a.needs_review)
                    {
                        EditorGUI.DrawRect(rect, new Color(0.45f, 0.35f, 0.1f, 0.25f));
                    }

                    var thumb = CellArtRegistryService.LoadPreviewTexture(a);
                    var thumbRect = new Rect(rect.x + 4, rect.y + 4, 46, 46);
                    if (thumb != null)
                    {
                        GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        EditorGUI.DrawRect(thumbRect, new Color(0.1f, 0.1f, 0.12f));
                    }

                    var textRect = new Rect(rect.x + 56, rect.y + 6, rect.width - 60, rect.height - 8);
                    GUI.Label(textRect, title, EditorStyles.boldLabel);
                    textRect.y += 18;
                    GUI.Label(textRect, sub, EditorStyles.miniLabel);

                    if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                    {
                        s.SelectedId = a.id;
                        GUI.FocusControl(null);
                        Event.current.Use();
                        _window.Repaint();
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        void DrawDetail(CellArtRegistry data)
        {
            var s = _window.SourceLib;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var a = data.assets?.FirstOrDefault(x => x.id == s.SelectedId);
                if (a == null)
                {
                    EditorGUILayout.LabelField("选中左侧条目查看详情");
                    return;
                }

                EditorGUILayout.LabelField("详情", EditorStyles.boldLabel);
                s.DetailScroll = EditorGUILayout.BeginScrollView(s.DetailScroll);

                EditorGUI.BeginChangeCheck();

                var tex = CellArtRegistryService.LoadPreviewTexture(a);
                if (tex != null)
                {
                    var r = GUILayoutUtility.GetRect(180, 180, GUILayout.ExpandWidth(false));
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                }

                GUI.enabled = false;
                EditorGUILayout.TextField("id", a.id);
                GUI.enabled = true;

                a.name_zh = EditorGUILayout.TextField("中文名", a.name_zh);
                a.kind = PopupString("种类", a.kind, KindOptions);
                a.slot = PopupString("槽位", a.slot, SlotOptions);
                a.route = PopupString("路线", a.route, RouteOptions);
                a.status = PopupString("状态", a.status, StatusOptions);
                a.rarity = EditorGUILayout.TextField("稀有度", a.rarity ?? "");
                a.needs_review = EditorGUILayout.Toggle("待审 needs_review", a.needs_review);

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("文件绑定", EditorStyles.boldLabel);
                a.concept = PathField("概念图 concept", a.concept, "Concepts");
                a.mesh = PathField("模型 mesh", a.mesh, "Meshes");
                a.anim = PathField("动画 anim", a.anim, "Animations");
                a.vfx = PathField("特效 vfx", a.vfx, "VFX");
                a.preview = PathField("缩略图 preview", a.preview, "Previews");
                a.raw = EditorGUILayout.TextField("Raw 路径", a.raw ?? "");

                var arch = a.archetype_id ?? -1;
                var newArch = EditorGUILayout.IntField("ArchetypeId (-1=空)", arch);
                a.archetype_id = newArch < 0 ? null : newArch;

                a.notes = EditorGUILayout.TextField("备注", a.notes ?? "");
                a.tripo_notes = EditorGUILayout.TextField("Tripo 备注", a.tripo_notes ?? "");

                if (EditorGUI.EndChangeCheck())
                {
                    _window.MarkRegistryDirty();
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("按文件自动推状态"))
                    {
                        a.status = CellArtRegistryService.GuessStatus(a);
                        _window.MarkRegistryDirty();
                    }

                    if (GUILayout.Button("清除待审"))
                    {
                        a.needs_review = false;
                        _window.MarkRegistryDirty();
                    }

                    if (GUILayout.Button("在 Project 中定位概念图") && !string.IsNullOrEmpty(a.concept))
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(CellArtRegistryService.AssetPathOf(a.concept));
                        if (obj != null)
                        {
                            EditorGUIUtility.PingObject(obj);
                            Selection.activeObject = obj;
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        string PathField(string label, string value, string folderHint)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var next = EditorGUILayout.TextField(label, value ?? "");
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
                                "请选择 Art/Cell 目录下的文件。", "OK");
                        }
                        else
                        {
                            next = rel;
                            _window.MarkRegistryDirty();
                        }
                    }
                }

                var exists = CellArtRegistryService.PathExists(next);
                GUILayout.Label(string.IsNullOrEmpty(next) ? "-" : (exists ? "OK" : "缺"), GUILayout.Width(28));
                return next;
            }
        }

        static string PopupString(string label, string current, string[] options)
        {
            var idx = Array.IndexOf(options, current);
            if (idx < 0)
            {
                idx = 0;
            }

            idx = EditorGUILayout.Popup(label, idx, options);
            return options[idx];
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
