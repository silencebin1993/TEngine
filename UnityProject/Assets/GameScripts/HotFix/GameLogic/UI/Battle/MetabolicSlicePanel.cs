using System.Collections.Generic;
using ChemEngine.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Digestion;
using GameLogic.MetabolicSlice.Graph;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.MetabolicSlice.Transfer;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using UnityEngine;

namespace GameLogic.UI.Battle
{
    /// <summary>
    /// 局内代谢切片面板（Story 002；003 起兼作玩家真实网格的唯一持有者）：M 键开关，
    /// 储备囊列表 + 3x3 切片槽，装/卸、画/删有向边；常驻显示当前 source→…→sink 链路摘要
    /// （不需要打开面板也能看见，随装卸/改边同帧刷新）。
    ///
    /// 本面板持有的 <see cref="Grid"/>/<see cref="Bag"/> 就是"玩家状态"本身：
    /// <see cref="Combat.MetabolicSliceBridge"/> 通过 <see cref="Instance"/> 读同一份 SlotGrid
    /// （003 起不再各自持有一份）。由 <see cref="Stage.GameRoot"/> 自动挂载并 DontDestroyOnLoad，
    /// 不要求手动 AddComponent，生命周期跨越单局 CellStageFlow。
    /// </summary>
    public sealed class MetabolicSlicePanel : MonoBehaviour
    {
        /// <summary>面板随 GameRoot 常驻挂载，早于每局 CellStageFlow.Enter()；Bridge 在 OnEnter/OnUpdate 里读它。</summary>
        public static MetabolicSlicePanel Instance { get; private set; }

        private BagInventory _bag;
        private SlotGrid _grid;
        private readonly List<IContract> _geneContracts = new List<IContract>();
        private PartInstance _pendingOverflow;
        private string _selectedPartId;
        private EdgeMode _edgeMode;
        private int? _edgeFrom;

        private GUIStyle _label;
        private GUIStyle _summaryLabel;
        private bool _showPanel;

        private enum EdgeMode { None, Add, Remove }

        /// <summary>玩家真实切片网格。Bridge 每 Tick 读它，而不是自己造一份演示网格。</summary>
        public SlotGrid Grid => _grid;
        public BagInventory Bag => _bag;

        /// <summary>已获得的基因契约（story-005）。基因非 IModule，不进 Bag/Grid，
        /// 直接作为全局法则传给 Bridge 的 NormalizeContracts；抽同一基因两次会叠加两份契约实例。</summary>
        public IReadOnlyList<IContract> GeneContracts => _geneContracts;

        /// <summary>抽卡领取基因后调用（CellStageFlow.ConfirmDraft）。geneId 查 GeneCatalog 失败时忽略。</summary>
        public void AddGene(string geneId)
        {
            System.Func<IContract> factory = GeneCatalog.Get(geneId);
            if (factory == null)
            {
                TEngine.Log.Warning($"[MetabolicSlicePanel] 未知基因 id: {geneId}");
                return;
            }
            _geneContracts.Add(factory());
        }

        private void Awake()
        {
            Instance = this;
            _bag = new BagInventory(8);
            _grid = new SlotGrid(SlotType.Cytoplasm);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnGUI()
        {
            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                return;
            }

            EnsureStyles();

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.M)
            {
                _showPanel = !_showPanel;
            }

            DrawChainSummary();
            DrawAxisTouchSummary(cell);

            if (_showPanel)
            {
                DrawPanel();
            }
        }

        /// <summary>
        /// story-007：轴 A（地形/残留）+ 轴 C1（消化泡）局内可摸触点。常驻显示，不需要打开 M 面板也能看见，
        /// 白话名词（残留/消化泡）满足验收要求。数据源直接读已接线的 <see cref="MetabolicSliceBridge"/>/
        /// <see cref="MetabolicDigestionSystem"/>，本方法只负责拼字符串，不含玩法逻辑。
        /// </summary>
        private void DrawAxisTouchSummary(CellStageFlow cell)
        {
            var r = new Rect(Screen.width - 300f, 12f, 288f, 118f);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height - 16f));

            MetabolicSliceBridge bridge = cell.MetabolicBridge;
            GUILayout.Label("<b>轴A 地形/残留</b>", _label);
            if (bridge != null)
            {
                var tags = new List<string>();
                foreach (string tag in bridge.ArenaTags)
                {
                    tags.Add(MetabolicSliceBridge.DisplayTag(tag));
                }
                GUILayout.Label(tags.Count > 0 ? string.Join("、", tags) : "（无）", _label);
                GUILayout.Label(bridge.LastEnvironmentPrompt, _label);
            }

            GUILayout.Space(6f);
            GUILayout.Label("<b>轴C1 消化泡</b>", _label);
            MetabolicDigestionSystem digestion = cell.Digestion;
            if (digestion != null)
            {
                GUILayout.Label($"{digestion.ChamberCount}/{digestion.ChamberCapacity}", _label);
                IReadOnlyList<string> log = digestion.RecentLog;
                GUILayout.Label(log.Count > 0 ? log[log.Count - 1] : "（尚未捕食）", _label);
            }

            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_label != null)
            {
                return;
            }
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            _summaryLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, richText = true, wordWrap = true,
            };
        }

        private void DrawChainSummary()
        {
            var r = new Rect(Screen.width - 320f, Screen.height - 96f, 300f, 84f);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height - 16f));
            GUILayout.Label("<b>代谢链路</b>　M 装/卸 画边", _label);
            GUILayout.Label(BuildChainSummary(), _summaryLabel);
            GUILayout.EndArea();
        }

        private string BuildChainSummary()
        {
            List<PathCompiler.CompiledPath> paths = PathCompiler.Compile(_grid);
            if (paths.Count == 0)
            {
                return "无输出链";
            }

            var lines = new List<string>(paths.Count);
            foreach (PathCompiler.CompiledPath path in paths)
            {
                var names = new List<string>(path.SlotPath.Count);
                foreach (int slotId in path.SlotPath)
                {
                    PartInstance part = _grid.Slots[slotId].Part;
                    if (part == null)
                    {
                        continue;
                    }
                    CardDef def = CardCatalog.Get(part.CardDefId);
                    names.Add(def?.DisplayName ?? part.CardDefId);
                }
                lines.Add(string.Join("→", names));
            }
            return string.Join("\n", lines);
        }

        private void DrawPanel()
        {
            var r = new Rect(12f, Screen.height - 420f, 380f, 408f);
            GUI.Box(r, "");
            GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 10f, r.width - 20f, r.height - 20f));

            GUILayout.Label("<b>代谢切片面板</b>", _label);

            if (_pendingOverflow != null)
            {
                GUILayout.Label($"囊已满，新元素 {DisplayName(_pendingOverflow.CardDefId)} 待抉择：", _label);
                if (GUILayout.Button("丢弃新件"))
                {
                    _pendingOverflow = null;
                }
                if (GUILayout.Button("丢弃囊内第一件后收下") && _bag.Items.Count > 0)
                {
                    _bag.Remove(_bag.Items[0].PartId);
                    _bag.TryAdd(_pendingOverflow);
                    _pendingOverflow = null;
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("掉落 线粒体")) AddDrop("org_mito");
                if (GUILayout.Button("掉落 晶状聚焦")) AddDrop("org_lens");
                if (GUILayout.Button("掉落 分泌喷射器")) AddDrop("org_emitter");
                // story-007 轴A：过氧化物酶接进 Source→…→Sink 链后会给流经事件打 Fire tag，
                // 撞上战场地形常驻的 Wet（见 MetabolicSliceBridge.OnEnter），触发环境残留反应可观察。
                if (GUILayout.Button("掉落 过氧化物酶")) AddDrop("org_perox");
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("重置为演示三件套（调试，覆盖当前网格）"))
            {
                ResetDemoTriple();
            }

            GUILayout.Space(6f);
            GUILayout.Label($"储备囊 {_bag.Items.Count}/{_bag.Cap}（点选后再点空槽装入）", _label);
            foreach (PartInstance p in _bag.Items)
            {
                GUILayout.BeginHorizontal();
                bool selected = p.PartId == _selectedPartId;
                string text = (selected ? "▶ " : "") + DisplayName(p.CardDefId);
                if (GUILayout.Button(text))
                {
                    _selectedPartId = selected ? null : p.PartId;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            GUILayout.Label("切片 3x3（空槽点=装选中件；实槽点=卸下；边模式下点两格画/删边）", _label);
            for (int y = 0; y < SlotGrid.Height; y++)
            {
                GUILayout.BeginHorizontal();
                for (int x = 0; x < SlotGrid.Width; x++)
                {
                    int id = y * SlotGrid.Width + x;
                    SlotNode node = _grid.Slots[id];
                    bool isEdgeFrom = _edgeFrom == id;
                    string label = (isEdgeFrom ? "* " : "") +
                                   (node.IsEmpty ? $"[{id}] 空" : $"[{id}] {DisplayName(node.Part.CardDefId)}");
                    if (GUILayout.Button(label, GUILayout.Width(118)))
                    {
                        OnSlotClicked(id);
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_edgeMode == EdgeMode.Add ? "▶ 画边中" : "画边"))
            {
                _edgeMode = _edgeMode == EdgeMode.Add ? EdgeMode.None : EdgeMode.Add;
                _edgeFrom = null;
            }
            if (GUILayout.Button(_edgeMode == EdgeMode.Remove ? "▶ 删边中" : "删边"))
            {
                _edgeMode = _edgeMode == EdgeMode.Remove ? EdgeMode.None : EdgeMode.Remove;
                _edgeFrom = null;
            }
            GUILayout.EndHorizontal();

            if (_grid.Edges.Count > 0)
            {
                var edgeText = new List<string>(_grid.Edges.Count);
                foreach (DirectedEdge e in _grid.Edges)
                {
                    edgeText.Add($"{e.From}→{e.To}");
                }
                GUILayout.Label("箭头：" + string.Join("　", edgeText), _label);
            }

            GUILayout.EndArea();
        }

        private void OnSlotClicked(int slotId)
        {
            if (_edgeMode != EdgeMode.None)
            {
                if (_edgeFrom == null)
                {
                    _edgeFrom = slotId;
                    return;
                }
                int from = _edgeFrom.Value;
                _edgeFrom = null;
                if (from == slotId)
                {
                    return;
                }
                if (_edgeMode == EdgeMode.Add)
                {
                    _grid.TryAddEdge(from, slotId);
                }
                else
                {
                    _grid.RemoveEdge(from, slotId);
                }
                return;
            }

            SlotNode node = _grid.Slots[slotId];
            if (node.IsEmpty)
            {
                if (_selectedPartId == null)
                {
                    return;
                }
                TransferResult result = TransferService.Equip(_bag, _grid, _selectedPartId, slotId);
                if (result == TransferResult.Ok)
                {
                    _selectedPartId = null;
                }
            }
            else
            {
                TransferService.Unequip(_bag, _grid, slotId);
            }
        }

        /// <summary>
        /// 调试快捷键：覆盖玩家网格为旧 Bridge 演示装配（organ_core→organ_focus→organ_actuator，
        /// 0→1→4）。不删这套旧 Draft（story 要求），但只在手动点击时生效——默认仍是玩家自己装的网格。
        /// </summary>
        private void ResetDemoTriple()
        {
            for (int i = 0; i < SlotGrid.SlotCount; i++)
            {
                _grid.Slots[i].Part = null;
            }
            var existingEdges = new List<DirectedEdge>(_grid.Edges);
            foreach (var e in existingEdges)
            {
                _grid.RemoveEdge(e.From, e.To);
            }

            _grid.Slots[0].Part = new PartInstance("demo_core", "organ_core", PartLocation.Slot(0));
            _grid.Slots[1].Part = new PartInstance("demo_focus", "organ_focus", PartLocation.Slot(1));
            _grid.Slots[4].Part = new PartInstance("demo_actuator", "organ_actuator", PartLocation.Slot(4));
            _grid.TryAddEdge(0, 1);
            _grid.TryAddEdge(1, 4);

            _selectedPartId = null;
            _edgeMode = EdgeMode.None;
            _edgeFrom = null;
        }

        private void AddDrop(string cardDefId)
        {
            var part = new PartInstance(System.Guid.NewGuid().ToString("N"), cardDefId, PartLocation.Bag());
            AddResult result = _bag.TryAdd(part);
            if (result == AddResult.NeedDecision)
            {
                _pendingOverflow = part;
            }
        }

        private static string DisplayName(string cardDefId) => CardCatalog.Get(cardDefId)?.DisplayName ?? cardDefId;
    }
}
