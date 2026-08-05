using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools.CellArt
{
    /// <summary>
    /// 细胞阶段美术资源板：概念图 / 模型 / 动画 / 特效对照与扫盘入列。
    /// 菜单：BinGames → 美术资源板；Unity 6 工具栏也有 Cell Art 按钮。
    /// </summary>
    public sealed class CellArtBoardWindow : EditorWindow
    {
        static readonly string[] StatusOptions =
            { "todo", "concept", "tripo", "blender", "unity", "done" };

        static readonly string[] KindOptions =
            { "base", "module", "enemy", "boss", "env", "ui", "vfx", "anim" };

        static readonly string[] SlotOptions =
            { "none", "core", "membrane", "maw", "motility", "appendage", "territory" };

        static readonly string[] RouteOptions =
            { "none", "Devour", "Agile", "Electric", "Spore", "Nest", "Corrupt" };

        CellArtRegistry _data;
        Vector2 _listScroll;
        Vector2 _detailScroll;
        string _search = "";
        string _filterStatus = "all";
        string _filterKind = "all";
        string _filterRoute = "all";
        int _selected;
        string _lastLog = "";
        bool _dirty;

        public static void Open()
        {
            var w = GetWindow<CellArtBoardWindow>("Cell Art");
            w.minSize = new Vector2(880, 520);
            w.Show();
            w.Reload();
        }

        void OnEnable() => Reload();

        void OnGUI()
        {
            DrawToolbar();
            if (_data == null)
            {
                EditorGUILayout.HelpBox("登记表未加载。确认 Assets/GameRes/Art/Cell/registry.json 存在。", MessageType.Error);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawList(GUILayout.Width(position.width * 0.42f));
                DrawDetail();
            }

            if (!string.IsNullOrEmpty(_lastLog))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastLog, MessageType.None);
            }
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    if (_dirty && !EditorUtility.DisplayDialog("未保存", "有未保存修改，丢弃并刷新？", "丢弃", "取消"))
                    {
                        return;
                    }

                    Reload();
                }

                GUI.enabled = _dirty;
                if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    Save();
                }

                GUI.enabled = true;

                if (GUILayout.Button("扫盘预览", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    RunScan(false);
                }

                if (GUILayout.Button("扫盘入列", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    if (EditorUtility.DisplayDialog("扫盘入列",
                            "将按命名约定扫描 Concepts/Meshes/Animations/VFX/Previews，\n自动登记新文件并绑定到已有 id。继续？",
                            "入列", "取消"))
                    {
                        RunScan(true);
                    }
                }

                if (GUILayout.Button("生成图板", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    try
                    {
                        CellArtRegistryService.WriteBoardHtml(_data);
                        _lastLog = $"已生成 {CellArtRegistryService.BoardAbs}";
                    }
                    catch (Exception e)
                    {
                        _lastLog = e.Message;
                    }
                }

                if (GUILayout.Button("打开图板", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    OpenBoard();
                }

                if (GUILayout.Button("资源目录", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    EditorUtility.RevealInFinder(CellArtRegistryService.CellAbs);
                }

                GUILayout.FlexibleSpace();
                var review = _data?.assets?.Count(a => a.needs_review) ?? 0;
                var label = _data == null
                    ? ""
                    : $"{_data.assets.Count} 项 · 待审 {review}" + (_dirty ? " · 未保存" : "");
                GUILayout.Label(label, EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _search = EditorGUILayout.TextField("搜索", _search);
                _filterStatus = FilterPopup("状态", _filterStatus, StatusOptions);
                _filterKind = FilterPopup("种类", _filterKind, KindOptions);
                _filterRoute = FilterPopup("路线", _filterRoute, RouteOptions);
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

        void DrawList(params GUILayoutOption[] opts)
        {
            using (new EditorGUILayout.VerticalScope(opts))
            {
                EditorGUILayout.LabelField("资源列表", EditorStyles.boldLabel);
                _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
                var filtered = Filtered().ToList();
                for (var i = 0; i < filtered.Count; i++)
                {
                    var a = filtered[i];
                    var realIndex = _data.assets.IndexOf(a);
                    var selected = realIndex == _selected;
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
                        _selected = realIndex;
                        GUI.FocusControl(null);
                        Event.current.Use();
                        Repaint();
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        void DrawDetail()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_selected < 0 || _selected >= _data.assets.Count)
                {
                    EditorGUILayout.LabelField("选中左侧条目查看详情");
                    return;
                }

                var a = _data.assets[_selected];
                EditorGUILayout.LabelField("详情", EditorStyles.boldLabel);
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

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
                    _dirty = true;
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("按文件自动推状态"))
                    {
                        a.status = CellArtRegistryService.GuessStatus(a);
                        _dirty = true;
                    }

                    if (GUILayout.Button("清除待审"))
                    {
                        a.needs_review = false;
                        _dirty = true;
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
                    var start = Path.Combine(CellArtRegistryService.CellAbs, folderHint);
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
                            _dirty = true;
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

        IEnumerable<CellArtAsset> Filtered()
        {
            IEnumerable<CellArtAsset> q = _data.assets;
            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                q = q.Where(a =>
                    (a.id?.IndexOf(s, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (a.name_zh?.IndexOf(s, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            if (_filterStatus != "all")
            {
                q = q.Where(a => a.status == _filterStatus);
            }

            if (_filterKind != "all")
            {
                q = q.Where(a => a.kind == _filterKind);
            }

            if (_filterRoute != "all")
            {
                q = q.Where(a => a.route == _filterRoute);
            }

            return q;
        }

        void Reload()
        {
            try
            {
                _data = CellArtRegistryService.Load();
                CellArtRegistryService.EnsureFolders(_data);
                _dirty = false;
                _lastLog = $"已加载 {_data.assets.Count} 项 · {_data.updated}";
                if (_selected >= _data.assets.Count)
                {
                    _selected = 0;
                }
            }
            catch (Exception e)
            {
                _data = null;
                _lastLog = e.Message;
                Debug.LogError(e);
            }

            Repaint();
        }

        void Save()
        {
            try
            {
                CellArtRegistryService.Save(_data);
                _dirty = false;
                _lastLog = $"已保存 {CellArtRegistryService.RegistryAbs}";
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        void RunScan(bool apply)
        {
            try
            {
                if (_dirty)
                {
                    Save();
                }

                var actions = CellArtRegistryService.Scan(_data, apply);
                if (apply)
                {
                    CellArtRegistryService.Save(_data);
                    _dirty = false;
                    Reload();
                }

                _lastLog = actions.Count == 0
                    ? "扫盘：没有新文件"
                    : $"{(apply ? "已入列" : "预览")} {actions.Count} 项：\n" + string.Join("\n", actions.Take(30))
                      + (actions.Count > 30 ? $"\n…共 {actions.Count}" : "");
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        void OpenBoard()
        {
            try
            {
                if (!File.Exists(CellArtRegistryService.BoardAbs))
                {
                    CellArtRegistryService.WriteBoardHtml(_data);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = CellArtRegistryService.BoardAbs,
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
            }
        }
    }
}
