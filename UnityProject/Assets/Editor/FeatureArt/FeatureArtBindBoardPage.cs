using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.ArtBinding;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>与网页看板同口径的"机体卡片"：顶层分类下第一个非「分类」节点；整机 / 状态变体 / 图标 / 拆件都是它的成员。</summary>
    internal sealed class BoardCard
    {
        public FeatureArtNode Root;
        public string TopId, TopTitle;
        public readonly List<FeatureArtNode> Members = new List<FeatureArtNode>();
    }

    internal static class FeatureArtBoard
    {
        static readonly HashSet<string> LeafKinds = new HashSet<string> { "成品模型", "源部件", "特效", "图标", "贴图" };
        static FeatureArtWorkspace _ws;
        static int _rev = -1;
        static List<BoardCard> _cards = new List<BoardCard>();

        public static IReadOnlyList<BoardCard> Cards(FeatureArtWorkspace ws)
        {
            if (ReferenceEquals(ws, _ws) && ws.StructureRevision == _rev) return _cards;
            _ws = ws;
            _rev = ws.StructureRevision;
            var byRoot = new Dictionary<string, BoardCard>();
            var list = new List<BoardCard>();
            foreach (var n in ws.nodes)
            {
                if (n.archived || !LeafKinds.Contains(n.kind)) continue;
                var chain = new List<FeatureArtNode>();
                var hidden = false;
                for (var cur = n; cur != null; cur = ws.Find(cur.parentId))
                {
                    if (cur.archived) { hidden = true; break; }
                    chain.Add(cur);
                }
                if (hidden || chain.Count < 2) continue;
                chain.Reverse(); // 顶层 … 叶子
                var root = chain.Skip(1).FirstOrDefault(c => c.kind != "分类");
                if (root == null) continue;
                if (!byRoot.TryGetValue(root.id, out var card))
                {
                    card = byRoot[root.id] = new BoardCard { Root = root, TopId = chain[0].id, TopTitle = chain[0].title };
                    list.Add(card);
                }
                card.Members.Add(n);
            }
            foreach (var c in list) c.Members.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
            _cards = list;
            return _cards;
        }

        // 整机 → 状态变体 → 图标 → 拆件 → 其它
        static int Rank(FeatureArtNode n) =>
            n.kind == "成品模型" ? (n.id.EndsWith("_base", StringComparison.Ordinal) ? 0 : 1) : n.kind == "图标" ? 2 : n.kind == "源部件" ? 3 : 4;

        public static string RoleLabel(FeatureArtNode n) =>
            n.kind == "成品模型" ? (n.id.EndsWith("_base", StringComparison.Ordinal) ? "整机" : "状态") : n.kind == "图标" ? "图标" : n.kind == "源部件" ? "拆件" : n.kind;
    }

    /// <summary>左侧树第一页「绑定台」：按 模块 → 机体 直接列出要绑定的对象与它们在素材中心的状态，一行一个主操作。</summary>
    public sealed class FeatureArtHubPage
    {
        enum Stage { All, Draw, Imaged, Modeled, Bound }
        static readonly string[] StageLabels = { "全部阶段", "① 待出图", "② 有图", "③ 已生模", "④ 已绑定" };

        readonly FeatureArtBindingWindow _window;
        string _topId = "", _cardId = "", _search = "";
        int _stage;
        bool _onlyUnbound, _onlyImportable, _showSettings;
        readonly HashSet<string> _bindOpen = new HashSet<string>();
        Vector2 _scroll;
        const int MaxCards = 40;

        public FeatureArtHubPage(FeatureArtBindingWindow window) => _window = window;

        [OnInspectorGUI]
        void Draw()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                DrawHeader();
                DrawFilters();
                DrawRows();
            }
        }

        // ───────────── 顶部：服务状态 + 设置 ─────────────
        void DrawHeader()
        {
            var up = FeatureArtHub.Up;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(up ? "● 素材中心运行中" : "○ 素材中心未启动", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("打开网页", GUILayout.Width(80))) FeatureArtHub.OpenWeb();
                if (!up && GUILayout.Button("启动服务", GUILayout.Width(80))) FeatureArtHub.StartServer();
                if (GUILayout.Button("素材库文件夹", GUILayout.Width(100))) FeatureArtHub.OpenFolder();
            }
            _showSettings = EditorGUILayout.Foldout(_showSettings, "设置", true);
            if (!_showSettings) return;
            EditorGUI.BeginChangeCheck();
            var root = EditorGUILayout.DelayedTextField("素材库目录", FeatureArtHub.Root);
            if (EditorGUI.EndChangeCheck()) FeatureArtHub.Root = root;
            EditorGUI.BeginChangeCheck();
            var showAll = EditorGUILayout.ToggleLeft("左侧树显示全部类型（含需求 / 状态 / 固件等说明节点）", FeatureArtHub.ShowAllKinds);
            if (EditorGUI.EndChangeCheck()) { FeatureArtHub.ShowAllKinds = showAll; _window.RebuildWorkspaceTree(); }
        }

        // ───────────── 筛选：模块 → 机体 → 阶段 ─────────────
        void DrawFilters()
        {
            var cards = FeatureArtBoard.Cards(_window.Workspace);
            var tops = cards.GroupBy(c => c.TopId).Select(g => (id: g.Key, title: g.First().TopTitle, n: g.Count())).ToList();
            var topLabels = new[] { "全部模块" }.Concat(tops.Select(t => $"{t.title}（{t.n}）")).ToArray();
            var topIdx = Mathf.Max(0, tops.FindIndex(t => t.id == _topId) + 1);
            var newTop = EditorGUILayout.Popup("模块", topIdx, topLabels);
            if (newTop != topIdx) { _topId = newTop == 0 ? "" : tops[newTop - 1].id; _cardId = ""; }

            var inTop = cards.Where(c => _topId == "" || c.TopId == _topId).ToList();
            var cardLabels = new[] { "全部机体" }.Concat(inTop.Select(c => c.Root.title)).ToArray();
            var cardIdx = Mathf.Max(0, inTop.FindIndex(c => c.Root.id == _cardId) + 1);
            var newCard = EditorGUILayout.Popup("机体", cardIdx, cardLabels);
            if (newCard != cardIdx) _cardId = newCard == 0 ? "" : inTop[newCard - 1].Root.id;

            using (new EditorGUILayout.HorizontalScope())
            {
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                _stage = EditorGUILayout.Popup(_stage, StageLabels, GUILayout.Width(90));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _onlyUnbound = GUILayout.Toggle(_onlyUnbound, "只看未绑定", EditorStyles.miniButtonLeft);
                _onlyImportable = GUILayout.Toggle(_onlyImportable, "只看可导入（已有采用的 FBX/OBJ）", EditorStyles.miniButtonRight);
            }
        }

        // ───────────── 列表 ─────────────
        static Stage StageOf(BoardCard c, Func<FeatureArtNode, FeatureArtSlot> slotOf)
        {
            var main = c.Members.FirstOrDefault(m => m.kind == "成品模型" && m.id.EndsWith("_base", StringComparison.Ordinal))
                       ?? c.Members.FirstOrDefault(m => m.kind == "成品模型") ?? c.Members[0];
            var slot = slotOf(main);
            if (slot != null && !string.IsNullOrEmpty(slot.location)) return Stage.Bound;
            if ((FeatureArtHub.Summary(main.id)?.models ?? 0) > 0) return Stage.Modeled;
            return c.Members.Any(m => (FeatureArtHub.Summary(m.id)?.images ?? 0) > 0) ? Stage.Imaged : Stage.Draw;
        }

        static bool Importable(HubNodeSummary s, FeatureArtSlot slot)
        {
            if (s == null || slot == null || slot.bindKind != "InstancedMesh" || string.IsNullOrEmpty(s.adopted)) return false;
            var ext = Path.GetExtension(s.adopted).ToLowerInvariant();
            return ext == ".fbx" || ext == ".obj";
        }

        void DrawRows()
        {
            var tokens = _search.ToLowerInvariant().Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            var shown = 0;
            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = sv.scrollPosition;
                foreach (var card in FeatureArtBoard.Cards(_window.Workspace))
                {
                    if (_topId != "" && card.TopId != _topId) continue;
                    if (_cardId != "" && card.Root.id != _cardId) continue;
                    if (tokens.Length > 0)
                    {
                        var hay = (card.Root.title + " " + card.Root.id + " " + card.TopTitle + " " + card.Root.faction + " " +
                                   string.Join(" ", card.Members.Select(m => m.title + " " + m.id))).ToLowerInvariant();
                        if (tokens.Any(t => !hay.Contains(t))) continue;
                    }
                    var stage = StageOf(card, m => _window.FindSlot(m.slotId));
                    if (_stage != 0 && (int)stage != _stage) continue;
                    var rows = card.Members.Where(m => RowVisible(m)).ToList();
                    if (rows.Count == 0) continue;
                    if (shown++ >= MaxCards)
                    {
                        EditorGUILayout.HelpBox($"只显示前 {MaxCards} 个机体，请用模块 / 机体 / 搜索缩小范围。", MessageType.Info);
                        break;
                    }
                    DrawCard(card, stage, rows);
                }
            }
            if (shown == 0) EditorGUILayout.HelpBox("没有符合条件的机体。", MessageType.None);
        }

        bool RowVisible(FeatureArtNode n)
        {
            var slot = _window.FindSlot(n.slotId);
            var bound = slot != null && !string.IsNullOrEmpty(slot.location);
            if (_onlyUnbound && bound) return false;
            if (_onlyImportable && !Importable(FeatureArtHub.Summary(n.id), slot)) return false;
            return true;
        }

        void DrawCard(BoardCard card, Stage stage, List<FeatureArtNode> rows)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(card.Root.title, EditorStyles.boldLabel);
                GUILayout.Label($"{card.TopTitle}  ·  {StageLabels[(int)stage]}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("网页", EditorStyles.miniButton, GUILayout.Width(44))) FeatureArtHub.OpenWeb(card.Root.id);
            }
            foreach (var n in rows) DrawRow(n);
        }

        void DrawRow(FeatureArtNode n)
        {
            var slot = _window.FindSlot(n.slotId);
            var s = FeatureArtHub.Summary(n.id);
            var bound = slot != null && !string.IsNullOrEmpty(slot.location);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14);
                GUILayout.Label(new GUIContent($"{FeatureArtBoard.RoleLabel(n)}  {n.title}", n.id), GUILayout.MinWidth(120));
                GUILayout.FlexibleSpace();
                GUILayout.Label(s == null ? "无素材" : $"图{s.images}" + (s.views.Count > 0 ? $"/视角{s.views.Count}" : "") + (s.models > 0 ? $"  模型{s.models}" : ""),
                    EditorStyles.miniLabel, GUILayout.Width(120));
                GUILayout.Label(bound ? "✓ 已绑定" : "○ 未绑定", bound ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(62));
                if (Importable(s, slot) && GUILayout.Button(bound ? "重新导入" : "导入并绑定", EditorStyles.miniButton, GUILayout.Width(72)))
                    FeatureArtHub.ImportAdopted(n, slot);
                if (slot != null && GUILayout.Button(_bindOpen.Contains(n.id) ? "收起" : "手动绑定", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    if (!_bindOpen.Remove(n.id)) _bindOpen.Add(n.id);
                }
                if (GUILayout.Button("详情", EditorStyles.miniButton, GUILayout.Width(40))) _window.SelectWorkspaceNode(n);
            }
            if (slot != null && _bindOpen.Contains(n.id))
                using (new EditorGUILayout.VerticalScope())
                    _window.DrawBindField(slot);
        }
    }
}
