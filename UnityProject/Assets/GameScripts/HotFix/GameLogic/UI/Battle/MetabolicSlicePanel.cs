using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.Digestion;
using GameLogic.MetabolicSlice.Graph;
using GameLogic.MetabolicSlice.Grid;
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
        // 006 后仅剩显示用途（唯一读者 CellStageFlow.cs:605 提及该路径），待独立清理 story（F3，preflight-decisions.md）。
        private readonly List<IContract> _geneContracts = new List<IContract>();
        private readonly List<string> _geneIds = new List<string>();
        private readonly GeneReserve _geneReserve = new GeneReserve();
        private readonly CarrierRegistry _carrierRegistry = new CarrierRegistry();
        private PartInstance _pendingOverflow;
        private string _selectedPartId;
        private EdgeMode _edgeMode;
        private int? _edgeFrom;

        private GUIStyle _label;
        private GUIStyle _summaryLabel;

        /// <summary>story-005：调试预览用阶段皮层级，F8 循环 Cell→Mech→Cosmic→Cell。不接真阶段 FSM。</summary>
        private SkinTier _skinTier = SkinTier.Cell;

        private enum EdgeMode { None, Add, Remove }

        /// <summary>玩家真实切片网格。Bridge 每 Tick 读它，而不是自己造一份演示网格。</summary>
        public SlotGrid Grid => _grid;
        public BagInventory Bag => _bag;

        /// <summary>已获得的基因契约（story-005）。基因非 IModule，不进 Bag/Grid，
        /// 直接作为全局法则传给 Bridge 的 NormalizeContracts；抽同一基因两次会叠加两份契约实例。</summary>
        public IReadOnlyList<IContract> GeneContracts => _geneContracts;

        /// <summary>与 <see cref="GeneContracts"/> 一一对应的 geneId 列表（story-002 新增，供新 UI 反查显示名）。</summary>
        public IReadOnlyList<string> GeneIds => _geneIds;

        /// <summary>基因储备囊（story-002 D1）：未装备基因只在这里，装备后归属某 Carrier 插槽。</summary>
        public GeneReserve GeneReserve => _geneReserve;

        /// <summary>Carrier 持有容器（story-002 D8/D9），多持有单激活。</summary>
        public CarrierRegistry CarrierRegistry => _carrierRegistry;

        /// <summary>
        /// 抽卡领取基因后调用（CellStageFlow.ConfirmDraft）。geneId 查 GeneCatalog 失败时忽略。
        /// story-002 D5：不再当场建 IContract 塞全局法则表，改为建 GeneInstance 塞进 GeneReserve
        /// （未装备＝只在囊里，装备后才归属某 Carrier 插槽——新数据层）。旧 _geneContracts/GeneContracts
        /// 本 story 保留不删，003 编译层过渡期仍要靠它们，新旧两条路径并行、互不冲突。
        /// story-008 D1：先查 Contract 表，命中走原三行；未命中再查 Module 表（GeneCatalog.GetModule），
        /// 命中则只塞 GeneReserve（Module 基因没有 IContract 工厂，_geneContracts 对它无意义，不写）；
        /// 两查都未命中才是真未知 id，落 D2 的 Warning 分支。
        /// </summary>
        public void AddGene(string geneId)
        {
            System.Func<IContract> factory = GeneCatalog.Get(geneId);
            if (factory != null)
            {
                _geneContracts.Add(factory());
                _geneIds.Add(geneId);
                _geneReserve.TryAdd(new GeneInstance(System.Guid.NewGuid().ToString("N"), geneId, GeneLocation.Reserve()));
                return;
            }

            System.Func<IModule> moduleFactory = GeneCatalog.GetModule(geneId);
            if (moduleFactory != null)
            {
                _geneReserve.TryAdd(new GeneInstance(System.Guid.NewGuid().ToString("N"), geneId, GeneLocation.Reserve()));
                return;
            }

            TEngine.Log.Warning($"[MetabolicSlicePanel] 未知基因 id: {geneId}");
        }

        /// <summary>
        /// 器官入囊统一钩子（story-002 D8）：AddDrop 与 CellStageFlow.ApplyMetabolicContent
        /// 的 Organelle 分支都改调用它，取代各自直接 bag.TryAdd，消除重复逻辑。
        /// 若该器官是 Carrier（OrganelleCatalog.IsCarrier），入囊那一刻起自动拥有一份独立
        /// CarrierInstance（3 空槽）——不经过旧 3×3 装备动作触发。
        /// </summary>
        public AddResult AddOrganPart(PartInstance part)
        {
            AddResult result = _bag.TryAdd(part);
            if (result == AddResult.Added && OrganelleCatalog.Get(part.CardDefId)?.IsCarrier == true)
            {
                _carrierRegistry.EnsureCarrier(part.PartId, part.CardDefId);
            }
            return result;
        }

        private void Awake()
        {
            Instance = this;
            // 储备囊真无限（覆盖冻结总案 §6 的 8/16 软硬上限，产品要求改为不限容）。
            _bag = new BagInventory(int.MaxValue);
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

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F8)
            {
                _skinTier = _skinTier == SkinTier.Cell ? SkinTier.Mech
                    : _skinTier == SkinTier.Mech ? SkinTier.Cosmic
                    : SkinTier.Cell;
            }

            // story-002 D9⑩：新 HUD（BattleHudToolkit）默认显示时已接管这两块摘要，避免同屏重复。
            if (!BattleHudToolkit.NewHudActive)
            {
                DrawChainSummary();
                DrawAxisTouchSummary(cell);
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
            GUILayout.Label("<b>代谢链路</b>　L 装/卸 画边", _label);
            GUILayout.Label(BuildChainSummary(), _summaryLabel);
            GUILayout.EndArea();
        }

        /// <summary>source→…→sink 链路文本，供旧 IMGUI 摘要与 BattleHudToolkit（D11）共用。</summary>
        public string BuildChainSummary()
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

        /// <summary>拖拽装入基因到 Carrier 插槽（story-005 D7）：取 ActiveCarrier 后转调 CarrierGeneService，
        /// ActiveCarrier 为 null 时返回 CarrierNotFound（不抛）。</summary>
        public CarrierGeneResult DragEquipGene(string geneInstanceId, int slotIndex)
        {
            CarrierInstance carrier = _carrierRegistry.ActiveCarrier;
            if (carrier == null)
            {
                return CarrierGeneResult.CarrierNotFound;
            }
            return CarrierGeneService.EquipGene(_geneReserve, carrier, geneInstanceId, slotIndex);
        }

        /// <summary>拖拽卸下基因从 Carrier 插槽（story-005 D7）：取 ActiveCarrier 后转调 CarrierGeneService，
        /// ActiveCarrier 为 null 时返回 CarrierNotFound（不抛）。</summary>
        public CarrierGeneResult DragUnequipGene(int slotIndex)
        {
            CarrierInstance carrier = _carrierRegistry.ActiveCarrier;
            if (carrier == null)
            {
                return CarrierGeneResult.CarrierNotFound;
            }
            return CarrierGeneService.UnequipGene(_geneReserve, carrier, slotIndex);
        }

        /// <summary>当前选中的储备囊件 id（story-002 D9④，null=未选中）。</summary>
        public string SelectedPartId => _selectedPartId;

        /// <summary>点选/取消选中储备囊内一件（story-002 D9④）。</summary>
        public void ToggleSelectPart(string partId)
        {
            _selectedPartId = _selectedPartId == partId ? null : partId;
        }

        /// <summary>画边模式是否激活（story-002 D9⑤）。</summary>
        public bool IsEdgeAddMode => _edgeMode == EdgeMode.Add;

        /// <summary>删边模式是否激活（story-002 D9⑤）。</summary>
        public bool IsEdgeRemoveMode => _edgeMode == EdgeMode.Remove;

        /// <summary>画边/删边模式下已选中的起点槽位，未选择时为 null（story-002 D9⑤）。</summary>
        public int? EdgeFromSlot => _edgeFrom;

        /// <summary>切换画边模式（story-002 D9②），逻辑照抄 IMGUI 按钮原分支。</summary>
        public void ToggleEdgeAddMode()
        {
            _edgeMode = _edgeMode == EdgeMode.Add ? EdgeMode.None : EdgeMode.Add;
            _edgeFrom = null;
        }

        /// <summary>切换删边模式（story-002 D9②），逻辑照抄 IMGUI 按钮原分支。</summary>
        public void ToggleEdgeRemoveMode()
        {
            _edgeMode = _edgeMode == EdgeMode.Remove ? EdgeMode.None : EdgeMode.Remove;
            _edgeFrom = null;
        }

        /// <summary>囊满溢出待抉择的新元素，null=当前无溢出（story-002 D9③）。</summary>
        public PartInstance PendingOverflow => _pendingOverflow;

        /// <summary>囊满溢出抉择：丢弃新件（story-002 D9③），逻辑照抄 IMGUI 分支。</summary>
        public void ResolveOverflowDiscardNew()
        {
            _pendingOverflow = null;
        }

        /// <summary>囊满溢出抉择：丢弃囊内第一件后收下新件（story-002 D9③），逻辑照抄 IMGUI 分支（含判空）。</summary>
        public void ResolveOverflowKeepNewDiscardOld()
        {
            if (_pendingOverflow == null || _bag.Items.Count == 0)
            {
                return;
            }
            _bag.Remove(_bag.Items[0].PartId);
            _bag.TryAdd(_pendingOverflow);
            _pendingOverflow = null;
        }

        /// <summary>
        /// story-005：org_* 卡按当前 <see cref="_skinTier"/> 走 <see cref="StageSkinCatalog"/> 换皮；
        /// 非 org_*（如旧演示卡 organ_core/organ_focus/organ_actuator）无皮映射，保持原 CardCatalog 名。
        /// </summary>
        public string DisplayName(string cardDefId)
        {
            if (cardDefId != null && cardDefId.StartsWith("org_"))
            {
                string skinned = StageSkinCatalog.GetDisplayName(cardDefId, _skinTier);
                if (skinned != null)
                {
                    return skinned;
                }
            }
            return CardCatalog.Get(cardDefId)?.DisplayName ?? cardDefId;
        }
    }
}
