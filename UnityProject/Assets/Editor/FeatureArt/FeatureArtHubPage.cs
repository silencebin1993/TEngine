using System;
using System.Collections.Generic;
using System.Linq;
using Sirenix.OdinInspector;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>左侧树里的「素材中心」页：服务状态、快速定位（搜名字直接跳到对象）、待办。</summary>
    public sealed class FeatureArtHubPage
    {
        readonly FeatureArtBindingWindow _window;
        string _search = "";
        bool _onlyModels = true, _onlyMissing;
        Vector2 _scroll;
        const int MaxRows = 60;

        public FeatureArtHubPage(FeatureArtBindingWindow window) => _window = window;

        [OnInspectorGUI]
        void Draw()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                DrawServer();
                EditorGUILayout.Space();
                DrawLocator();
            }
        }

        void DrawServer()
        {
            EditorGUILayout.LabelField("素材中心（网页）", EditorStyles.boldLabel);
            var up = FeatureArtHub.Up;
            EditorGUILayout.LabelField(up ? "● 服务运行中  " + FeatureArtHub.Url : "○ 服务未启动（点「打开网页」会自动启动）");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开网页")) FeatureArtHub.OpenWeb();
                if (GUILayout.Button("启动服务")) FeatureArtHub.StartServer();
                if (GUILayout.Button("打开素材库文件夹")) FeatureArtHub.OpenFolder();
            }
            var all = FeatureArtHub.AllSummaries().ToList();
            EditorGUILayout.LabelField($"已入库：{all.Count(s => s.images > 0)} 个对象有图，{all.Count(s => s.models > 0)} 个对象有模型"
                + (all.Sum(s => s.unsorted) > 0 ? $"，{all.Sum(s => s.unsorted)} 张待分配视角" : ""));
            EditorGUI.BeginChangeCheck();
            var root = EditorGUILayout.DelayedTextField("素材库目录", FeatureArtHub.Root);
            if (EditorGUI.EndChangeCheck()) FeatureArtHub.Root = root;
            EditorGUI.BeginChangeCheck();
            var showAll = EditorGUILayout.ToggleLeft("左侧树显示全部类型（含需求 / 状态 / 固件等说明节点）", FeatureArtHub.ShowAllKinds);
            if (EditorGUI.EndChangeCheck())
            {
                FeatureArtHub.ShowAllKinds = showAll;
                _window.RebuildWorkspaceTree();
            }
        }

        void DrawLocator()
        {
            EditorGUILayout.LabelField("快速定位", EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            using (new EditorGUILayout.HorizontalScope())
            {
                _onlyModels = GUILayout.Toggle(_onlyModels, "只看成品模型 / 拆件", EditorStyles.miniButtonLeft);
                _onlyMissing = GUILayout.Toggle(_onlyMissing, "只看还缺素材", EditorStyles.miniButtonRight);
            }
            var ws = _window.Workspace;
            var tokens = _search.ToLowerInvariant().Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 && !_onlyMissing)
            {
                EditorGUILayout.HelpBox("输入名称 / id / 阵营，空格分词，例如「履带 玩家」或「连射器」。", MessageType.None);
                return;
            }
            var shown = 0;
            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll, GUILayout.MaxHeight(420)))
            {
                _scroll = sv.scrollPosition;
                foreach (var n in ws.nodes)
                {
                    if (n.archived || !FeatureArtHub.IsArtKind(n.kind)) continue;
                    if (_onlyModels && n.kind != "成品模型" && n.kind != "源部件") continue;
                    var s = FeatureArtHub.Summary(n.id);
                    if (_onlyMissing && s != null && s.images > 0) continue;
                    var parentTitle = ws.Find(n.parentId)?.title ?? "";
                    var hay = (n.title + " " + n.id + " " + parentTitle + " " + n.faction + " " + n.kind).ToLowerInvariant();
                    if (tokens.Any(t => !hay.Contains(t))) continue;
                    if (shown++ >= MaxRows) { EditorGUILayout.LabelField($"…只显示前 {MaxRows} 条，请再输入关键词缩小范围。"); break; }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(new GUIContent(n.title + "  ·  " + parentTitle, n.id), GUILayout.MinWidth(120));
                        GUILayout.Label(Chip(s), EditorStyles.miniLabel, GUILayout.Width(150));
                        if (GUILayout.Button("定位", GUILayout.Width(48))) _window.SelectWorkspaceNode(n);
                    }
                }
            }
            if (shown == 0) EditorGUILayout.LabelField("没有匹配的对象。");
        }

        internal static string Chip(HubNodeSummary s) =>
            s == null ? "无素材" : $"概念{s.concept} 视图{s.views.Count} 模型{s.models}" + (s.unsorted > 0 ? $" 待分{s.unsorted}" : "");
    }

    /// <summary>对象页里的「素材库」折叠块：缩略图、缺哪些视角、模型版本、一键导入 / 网页定位 / 复制关键词。</summary>
    internal static class FeatureArtHubPanel
    {
        static readonly HashSet<string> Closed = new HashSet<string>();
        static readonly string[] MainViews = { "front", "back", "left", "right" };
        const float Thumb = 76f;

        public static void Draw(FeatureArtNode node, FeatureArtSlot slot)
        {
            var s = FeatureArtHub.Summary(node.id);
            var open = !Closed.Contains(node.id);
            var head = "素材库（网页）  " + FeatureArtHubPage.Chip(s);
            var now = EditorGUILayout.Foldout(open, head, true);
            if (now != open) { if (now) Closed.Remove(node.id); else Closed.Add(node.id); }
            if (!now) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("在网页中打开此对象")) FeatureArtHub.OpenWeb(node.id);
                if (GUILayout.Button("复制关键词")) Copy(node.id, false);
                if (GUILayout.Button("复制整支关键词")) Copy(node.id, true);
            }
            var man = FeatureArtHub.Manifest(node.id);
            if (man == null || (man.images.Count == 0 && man.models.Count == 0))
            {
                EditorGUILayout.HelpBox("素材中心里还没有这个对象的图。复制关键词给出图 AI，下载后拖进网页即可。", MessageType.None);
                return;
            }

            var picked = man.images.Where(i => i.picked).ToList();
            using (new EditorGUILayout.HorizontalScope())
                foreach (var img in picked.OrderBy(i => i.role == "concept" ? 0 : 1).ThenBy(i => Array.IndexOf(MainViews, i.view) < 0 ? 9 : Array.IndexOf(MainViews, i.view)))
                    DrawThumb(img);
            var missing = MainViews.Where(v => !picked.Any(i => i.role == "view" && i.view == v)).ToArray();
            if (missing.Length > 0 && (node.kind == "成品模型" || node.kind == "源部件"))
                EditorGUILayout.LabelField("缺视角：" + string.Join(" ", missing), EditorStyles.miniLabel);
            if (s != null && s.unsorted > 0)
                EditorGUILayout.HelpBox($"{s.unsorted} 张图还没分配视角，请到网页里处理。", MessageType.Warning);

            foreach (var m in man.models.OrderBy(m => m.ver))
                EditorGUILayout.LabelField($"模型 v{m.ver:000}  {m.stage}{(m.adopted ? "  ✓采用" : "")}  {m.orig}", EditorStyles.miniLabel);
            if (slot != null && slot.bindKind == "InstancedMesh" && man.models.Any(m => m.adopted))
            {
                var bound = !string.IsNullOrEmpty(slot.location);
                if (GUILayout.Button(bound ? "用采用的模型重新导入并绑定（会覆盖母带）" : "导入采用的模型并绑定")) FeatureArtHub.ImportAdopted(node, slot);
            }
        }

        static void DrawThumb(HubImage img)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(Thumb)))
            {
                var r = GUILayoutUtility.GetRect(Thumb, Thumb, GUILayout.Width(Thumb), GUILayout.Height(Thumb));
                EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.25f));
                var tex = FeatureArtHub.Thumb(img.file);
                if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
                {
                    EditorUtility.RevealInFinder(FeatureArtHub.Root + "/" + img.file);
                    Event.current.Use();
                }
                GUILayout.Label(img.role == "concept" ? "概念" : img.view, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(Thumb));
            }
        }

        static void Copy(string id, bool branch)
        {
            var text = FeatureArtHub.FetchPrompt(id, branch);
            if (text == null) { EditorUtility.DisplayDialog("复制关键词", "素材中心服务没有启动。先点「在网页中打开此对象」启动它。", "好"); return; }
            EditorGUIUtility.systemCopyBuffer = text;
            Debug.Log("[素材中心] 关键词已复制（" + text.Length + " 字）。");
        }
    }
}
