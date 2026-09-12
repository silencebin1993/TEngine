using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Digestion;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.Stats;
using GameLogic.UI.Battle;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版战斗 HUD（battle-ui-toolkit/story-001）。第一片：只做 BattleHud 静态还原，
    /// 不含拖拽交互。不继承 UIWindow/不用 [Window]（本仓 UI Toolkit 无框架先例），照抄
    /// <see cref="BattleMainUI"/> 的"常驻单例、轮询 IsRunning 自控显隐"模式。
    /// story-001 验收通过（5 档分辨率排版核实）后改为默认显示，旧 UGUI <see cref="BattleMainUI"/>
    /// 通过 <see cref="NewHudActive"/> 静态标记降级为按 U 键切回去的对照面板。
    /// 数据绑定严格对齐 BattleMainUI.RefreshHud 的 10 项。
    /// </summary>
    public class BattleHudToolkit : MonoBehaviour
    {
        private const int SkillSlotCount = 5;

        /// <summary>组合反应播报行的驻留时长（ui-visual-overhaul story-006 D3）。到期后整块
        /// display:none，不常驻脏文本。用 unscaled 时间：暂停/慢放不该把播报永久钉在屏幕上。</summary>
        private const float ReactionFeedbackHoldSeconds = 2.5f;

        /// <summary>true=显示本 UI Toolkit HUD（默认）；false=按 U 键切回旧 UGUI BattleMainUI 对照。
        /// 供 BattleMainUI 读取决定自己是否显示，避免两套 HUD 同屏重叠。</summary>
        public static bool NewHudActive { get; private set; } = true;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _tagChipTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Label _phaseName;
        private Label _phaseIndex;
        private ProgressBar _phaseProgress;
        private Button _advanceButton;
        private Label _runTimer;
        private VisualElement _vitalBlock;
        private VisualElement _statusBlock;
        /// <summary>状态 chip 池：每帧复用，避免 new Label 的 GC。</summary>
        private readonly List<Label> _statusChips = new List<Label>(8);
        /// <summary>SimStatus 各位缓存，避免每帧 Enum.GetValues 装箱。</summary>
        private static readonly SimStatus[] AllStatuses = BuildAllStatuses();
        private Label _hpText;
        private Label _volumeText;
        private VisualElement _hpFill;
        private VisualElement _evoBlock;
        private Label _levelText;
        private Label _evoText;
        private ProgressBar _evoBar;
        private Label _nutrientChip;
        private Label _mutagenChip;
        private VisualElement _pollutionBlock;
        private Label _pollutionText;
        private ProgressBar _pollutionBar;
        private Label _metaStats;
        private Label _threatBlock;
        /// <summary>story-008 R7②：Spin/Orbit 挂起命中数（<see cref="MetabolicSliceBridge.PendingMotionCount"/>）
        /// 直接绑定的只读文本，零新增字段，只做 UI 绑定。</summary>
        private Label _mechanismCount;
        private VisualElement _ecoEventBlock;
        private Label _ecoEventText;

        /// <summary>ui-visual-overhaul story-006：最近一次具名组合反应的 HUD 播报行。</summary>
        private VisualElement _reactionFeedbackBlock;
        private Label _reactionFeedbackText;
        /// <summary>已排版好的播报文案；null = 当前无待显示反应。信号回调里就拼好，
        /// 避免 <see cref="RefreshReactionFeedback"/> 每帧重新拼串产生 GC。</summary>
        private string _pendingReactionText;
        /// <summary>播报过期时刻（<see cref="Time.unscaledTime"/> 口径）。</summary>
        private float _reactionExpireTime;
        /// <summary>文案有更新、待写进 Label。只在真正变化的那一帧写 UI。</summary>
        private bool _reactionTextDirty;
        /// <summary>ComposeCastSignal 订阅作用域，Start 建、OnDestroy 释放（D2）。</summary>
        private SignalScope _scope;

        /// <summary>ui-visual-overhaul story-007：生效中的规则开关一行。</summary>
        private VisualElement _ruleFlagsBlock;
        private Label _ruleFlagsText;
        /// <summary>上次据以拼串的 <see cref="RuleFlags.Version"/>；-1 = 尚未拼过。
        /// 只在版本变化时重建文案，逐帧遍历 12 个 flag 拼字符串是白烧 GC。</summary>
        private int _ruleFlagsVersion = -1;

        /// <summary>story-002 D11：story-001 遗留，右上轴A/消化泡摘要节点。</summary>
        private VisualElement _arenaTags;
        private Label _envPrompt;
        private Label _chamberText;
        private Label _digestLog;

        /// <summary>story-002 D11：story-001 遗留，右下代谢链路摘要节点。</summary>
        private Label _chainText;

        private readonly VisualElement[] _skillSlots = new VisualElement[SkillSlotCount];
        private readonly Label[] _skillName = new Label[SkillSlotCount];
        private readonly Label[] _skillState = new Label[SkillSlotCount];
        private readonly Label[] _skillCharge = new Label[SkillSlotCount];
        private readonly VisualElement[] _skillCooldownOverlay = new VisualElement[SkillSlotCount];

        private bool _visible;

        /// <summary>供 execute_code 验收探针只读访问，不参与显示逻辑本身。</summary>
        public bool IsVisible => _visible;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            // 订阅放在 await 之前：HUD 三份资源要异步加载若干帧，期间打出去的反应不该被吞掉。
            // 回调只写字段（不碰 VisualElement），节点尚未 Q 出来也安全。
            _scope = new SignalScope().On<ComposeCastSignal>(OnComposeCast);

            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleHud");
            _tagChipTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("TagChip");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 多个 UIDocument 共用同一份 PanelSettings 时，兄弟节点绘制顺序取决于
            // 各自异步加载完成的竞态顺序（非确定性）。显式给一个数值表锁定的
            // sortingOrder，让四个控制器的叠放关系确定（story-004 已验证的手法）。
            _document.sortingOrder = 0;

            // UIDocument.rootVisualElement 在刚赋值 panelSettings 后偶发仍为 null
            // （面板尚未在本帧完成挂载，实测复现），有限帧数轮询等它就绪，避免
            // CacheNodes() 对 null 根节点查询直接崩溃、控制器永久半初始化。
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleHudToolkit] rootVisualElement 等待超时，HUD 未初始化。");
                return;
            }
            CacheNodes();
            SetVisible(true);
        }

        private void CacheNodes()
        {
            _phaseName = _root.Q<Label>("PhaseName");
            _phaseIndex = _root.Q<Label>("PhaseIndex");
            _phaseProgress = _root.Q<ProgressBar>("PhaseProgress");
            _advanceButton = _root.Q<Button>("AdvanceButton");
            if (_advanceButton != null)
            {
                _advanceButton.clicked += OnAdvanceButtonClicked;
            }
            _runTimer = _root.Q<Label>("RunTimer");
            _vitalBlock = _root.Q<VisualElement>("VitalBlock");
            _hpText = _root.Q<Label>("HpText");
            _volumeText = _root.Q<Label>("VolumeText");
            _hpFill = _root.Q<VisualElement>("HpFill");
            _evoBlock = _root.Q<VisualElement>("EvoBlock");
            _levelText = _root.Q<Label>("LevelText");
            _evoText = _root.Q<Label>("EvoText");
            _evoBar = _root.Q<ProgressBar>("EvoBar");
            _nutrientChip = _root.Q<Label>("NutrientChip");
            _mutagenChip = _root.Q<Label>("MutagenChip");
            _pollutionBlock = _root.Q<VisualElement>("PollutionBlock");
            _pollutionText = _root.Q<Label>("PollutionText");
            _pollutionBar = _root.Q<ProgressBar>("PollutionBar");
            _metaStats = _root.Q<Label>("MetaStats");
            _threatBlock = _root.Q<Label>("ThreatBlock");
            _mechanismCount = _root.Q<Label>("MechanismCount");
            _ecoEventBlock = _root.Q<VisualElement>("EcoEventBlock");
            _ecoEventText = _root.Q<Label>("EcoEventText");

            _reactionFeedbackBlock = _root.Q<VisualElement>("ReactionFeedbackBlock");
            _reactionFeedbackText = _root.Q<Label>("ReactionFeedbackText");
            if (_reactionFeedbackBlock != null)
            {
                // 初始态即隐藏（D3）：没打出反应前不该有一条空行占位。
                _reactionFeedbackBlock.style.display = DisplayStyle.None;
            }

            _ruleFlagsBlock = _root.Q<VisualElement>("RuleFlagsBlock");
            _ruleFlagsText = _root.Q<Label>("RuleFlagsText");

            _statusBlock = _root.Q<VisualElement>("StatusBlock");
            _arenaTags = _root.Q<VisualElement>("ArenaTags");
            _envPrompt = _root.Q<Label>("EnvPrompt");
            _chamberText = _root.Q<Label>("ChamberText");
            _digestLog = _root.Q<Label>("DigestLog");
            _chainText = _root.Q<Label>("ChainText");

            for (int i = 0; i < SkillSlotCount; i++)
            {
                VisualElement slot = _root.Q<VisualElement>("SkillSlot" + i);
                _skillSlots[i] = slot;
                if (slot == null)
                {
                    continue;
                }
                _skillName[i] = slot.Q<Label>("Name");
                _skillState[i] = slot.Q<Label>("StateText");
                _skillCharge[i] = slot.Q<Label>("ChargeBadge");
                _skillCooldownOverlay[i] = slot.Q<VisualElement>("CooldownOverlay");
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.U))
            {
                SetVisible(!_visible);
            }

            if (_root == null || !_visible)
            {
                return;
            }

            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning;
            _root.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            if (!running)
            {
                return;
            }

            if (!cell.Paused && Input.GetKeyDown(KeyCode.N))
            {
                cell.Timeline.RequestAdvance();
            }

            RefreshHud(cell);
        }

        /// <summary>推进按钮点击入口，与 N 键共用同一目标方法（R4）。</summary>
        private void OnAdvanceButtonClicked()
        {
            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning || cell.Paused)
            {
                return;
            }
            cell.Timeline.RequestAdvance();
        }

        /// <summary>新旧 HUD 切换开关。story-001 验收通过后默认显示本 HUD（D11 已由人改口）。</summary>
        private void SetVisible(bool visible)
        {
            _visible = visible;
            NewHudActive = visible;
            if (_root != null)
            {
                _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>严格对齐 BattleMainUI.RefreshHud 的 10 项（D7/D8），不多做不少做。</summary>
        private void RefreshHud(CellStageFlow cell)
        {
            StatSheet st = cell.Stats;
            PhaseTimeline tl = cell.Timeline;

            if (tl?.Current != null)
            {
                _phaseName.text = tl.Current.Name;
                _phaseIndex.text = $"{tl.CurrentIndex + 1}/6";
                _phaseProgress.value = tl.PhaseProgress * 100f;

                if (_advanceButton != null)
                {
                    _advanceButton.RemoveFromClassList("phase-can-advance");
                    if (tl.CanAdvance)
                    {
                        _advanceButton.AddToClassList("phase-can-advance");
                    }
                }

                int rm = (int)(tl.RunElapsed / 60f);
                int rs = (int)(tl.RunElapsed % 60f);
                _runTimer.text = $"本局 {rm:00}:{rs:00}";
            }

            float maxHp = st.Get(StatId.MaxHealth);
            SimControlledUnitView controlled = default;
            bool hasControlled = cell.Sim != null &&
                cell.Sim.TryGetControlledPresentation(out controlled);
            float hp = hasControlled ? controlled.Health : 0f;
            _hpText.text = hasControlled ? $"生命 {hp:F0}/{maxHp:F0}" : "生命 --（无控制目标）";
            _volumeText.text = $"体积 {st.Get(StatId.Volume):F2}";

            float hpPct = maxHp > 0f ? Mathf.Clamp01(hp / maxHp) : 0f;
            _hpFill.style.width = new Length(hpPct * 100f, LengthUnit.Percent);
            _vitalBlock.RemoveFromClassList("hp-full");
            _vitalBlock.RemoveFromClassList("hp-mid");
            _vitalBlock.RemoveFromClassList("hp-crit");
            _vitalBlock.AddToClassList(hpPct >= 0.66f ? "hp-full" : hpPct >= 0.33f ? "hp-mid" : "hp-crit");

            ProgressionModule prog = cell.Progression;
            _levelText.text = $"等级 {prog.Level}";
            _evoText.text = $"进化能 {cell.Wallet.EvoEnergy:F0}/{prog.CurrentThreshold:F0}";
            _evoBar.value = prog.Progress * 100f;
            _evoBlock.RemoveFromClassList("evo-full");
            if (prog.Progress >= 1f)
            {
                _evoBlock.AddToClassList("evo-full");
            }

            _nutrientChip.text = $"营养质 {cell.Wallet.Nutrient:F0}";
            _mutagenChip.text = $"突变质 {cell.Wallet.Mutagen:F0}";

            RefreshPollution(cell, st);

            _metaStats.text = $"卡牌 {cell.Deck.TotalCards}　连吃 {cell.Devour.Combo}";
            _threatBlock.text =
                $"敌人 {cell.Director.LiveHostiles}　压力 {cell.Director.CurrentPressure:F0}/{cell.Director.Budget:F0}";

            RefreshEcoEvent(cell);
            RefreshReactionFeedback();
            RefreshRuleFlags();
            RefreshStatuses(cell);
            RefreshSkillSlots(cell);
            RefreshAxisTouchAndChain(cell);
        }

        /// <summary>
        /// 玩家状态上屏（ui-visual-overhaul story-005）。此前 <see cref="StatusSystem"/>
        /// 全仓零 UI 消费方，玩家完全看不到自己中了什么 buff/debuff。
        ///
        /// 数据刻意取自两处：
        /// **有哪些状态** → 桥接层按 ControlledUnitId 解析的受控只读视图，O(1)，且能覆盖
        /// 冲刺无敌那种直接 <c>ApplyStatusUnit</c>、不进 StatusSystem 计时表的永久状态；
        /// **剩余秒数** → <see cref="StatusSystem.PlayerTimers"/>，只有限时状态才有。
        ///
        /// 不遍历 <c>StatusSystem</c> 的内部条目表：它会随 <c>ApplyTimedArea</c> 涨到
        /// 敌人数量级，每帧遍历就违反了「热更层每帧与敌人数无关」的红线。本方法
        /// 外层 O(状态种类数)、内层 O(玩家状态数)，均为常数。
        /// </summary>
        private void RefreshStatuses(CellStageFlow cell)
        {
            if (_statusBlock == null)
            {
                return;
            }

            uint mask = 0u;
            SimBridge sim = cell.Sim;
            if (sim != null && sim.TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                mask = (uint)controlled.Status;
            }

            if (mask == 0u)
            {
                _statusBlock.style.display = DisplayStyle.None;
                return;
            }
            _statusBlock.style.display = DisplayStyle.Flex;

            IReadOnlyList<StatusSystem.PlayerStatusTimer> timers =
                cell.Status != null ? cell.Status.PlayerTimers : null;

            int used = 0;
            for (int i = 0; i < AllStatuses.Length; i++)
            {
                SimStatus s = AllStatuses[i];
                if ((mask & (uint)s) == 0u)
                {
                    continue;
                }

                float timeLeft = -1f;
                if (timers != null)
                {
                    for (int t = 0; t < timers.Count; t++)
                    {
                        if (timers[t].Status != s)
                        {
                            continue;
                        }
                        timeLeft = timers[t].TimeLeft;
                        break;
                    }
                }

                Label chip = GetStatusChip(used);
                string name = CodexTaxonomy.StatusName(s);
                chip.text = timeLeft > 0f ? $"{name} {timeLeft:F0}s" : name;
                chip.EnableInClassList("status-buff", IsBuff(s));
                used++;
            }

            for (int i = used; i < _statusChips.Count; i++)
            {
                _statusChips[i].style.display = DisplayStyle.None;
            }
        }

        private Label GetStatusChip(int index)
        {
            while (_statusChips.Count <= index)
            {
                Label made = new Label();
                made.AddToClassList("status-chip");
                _statusBlock.Add(made);
                _statusChips.Add(made);
            }

            Label chip = _statusChips[index];
            chip.style.display = DisplayStyle.Flex;
            return chip;
        }

        /// <summary>
        /// 增益判定，只影响 chip 配色（绿=好事 / 琥珀=坏事），不参与任何数值逻辑。
        /// 名单按 <see cref="CodexTaxonomy"/> 里的中文描述语义定：
        /// 无敌=免疫伤害、硬化=受击阈值提高、不可吞噬=护壳完整，均对玩家有利。
        /// 其余（含 OnMycelium/Overloaded/Telegraphing 这类中性标记）一律按默认样式，
        /// 宁可把中性显示成警示色，也不要把 debuff 误染成绿色让玩家以为是好事。
        /// </summary>
        private static bool IsBuff(SimStatus s) =>
            s == SimStatus.Invulnerable || s == SimStatus.Hardened || s == SimStatus.Unedible;

        private static SimStatus[] BuildAllStatuses()
        {
            List<SimStatus> list = new List<SimStatus>(24);
            foreach (SimStatus s in System.Enum.GetValues(typeof(SimStatus)))
            {
                if (s != SimStatus.None)
                {
                    list.Add(s);
                }
            }
            return list.ToArray();
        }

        /// <summary>story-002 D11：接 story-001 遗留的 AxisTouchPanel（轴A/消化泡）+ ChainSummary（代谢链路）。</summary>
        private void RefreshAxisTouchAndChain(CellStageFlow cell)
        {
            MetabolicSliceBridge bridge = cell.MetabolicBridge;
            if (bridge != null)
            {
                if (_arenaTags != null && _tagChipTemplate != null)
                {
                    _arenaTags.Clear();
                    foreach (string tag in bridge.ArenaTags)
                    {
                        TemplateContainer clone = _tagChipTemplate.CloneTree();
                        Label label = clone.Q<Label>("TagChip");
                        if (label != null)
                        {
                            label.text = MetabolicSliceBridge.DisplayTag(tag);
                        }
                        _arenaTags.Add(clone);
                    }
                }
                if (_envPrompt != null)
                {
                    _envPrompt.text = bridge.LastEnvironmentPrompt;
                }
                if (_mechanismCount != null)
                {
                    _mechanismCount.text = $"轨迹 ×{bridge.PendingMotionCount}";
                }
            }

            MetabolicDigestionSystem digestion = cell.Digestion;
            if (digestion != null)
            {
                if (_chamberText != null)
                {
                    _chamberText.text = $"{digestion.ChamberCount}/{digestion.ChamberCapacity}";
                }
                if (_digestLog != null)
                {
                    IReadOnlyList<string> log = digestion.RecentLog;
                    _digestLog.text = log.Count > 0 ? log[log.Count - 1] : "（尚未捕食）";
                }
            }

            if (_chainText != null)
            {
                _chainText.text = MetabolicSlicePanel.Instance != null
                    ? MetabolicSlicePanel.Instance.BuildChainSummary()
                    : "无输出链";
            }
        }

        /// <summary>三选一 class：poll-hidden/poll-mid/poll-nearcap（D8），非简单布尔隐藏。</summary>
        private void RefreshPollution(CellStageFlow cell, StatSheet st)
        {
            float pollution = cell.Wallet.Pollution;
            float cap = st.Get(StatId.PollutionCap);

            _pollutionBlock.RemoveFromClassList("poll-hidden");
            _pollutionBlock.RemoveFromClassList("poll-mid");
            _pollutionBlock.RemoveFromClassList("poll-nearcap");

            if (pollution <= 0f)
            {
                _pollutionBlock.AddToClassList("poll-hidden");
                return;
            }

            bool nearCap = cap > 0f && pollution >= 0.85f * cap;
            _pollutionBlock.AddToClassList(nearCap ? "poll-nearcap" : "poll-mid");
            _pollutionText.text = $"污染度 {pollution:F0}/{cap:F0}";
            _pollutionBar.value = cap > 0f ? pollution / cap * 100f : 0f;
        }

        private void RefreshEcoEvent(CellStageFlow cell)
        {
            bool active = cell.Events.Active != null;
            _ecoEventBlock.RemoveFromClassList("eco-active");
            _ecoEventBlock.RemoveFromClassList("eco-idle");
            _ecoEventBlock.AddToClassList(active ? "eco-active" : "eco-idle");
            _ecoEventText.text = active
                ? $"生态事件：{cell.Events.Active.Name}"
                : $"下次事件 {cell.Events.NextEventCountdown:F0}s";
        }

        /// <summary>
        /// 组合反应 HUD 播报（ui-visual-overhaul story-006）。事件驱动写入、这里只做 O(1) 的
        /// 上屏与过期回收——挂在既有 <see cref="RefreshHud"/> 链上，不新开协程/不新开 Update，
        /// 也不在这里向判定层反查反应（那等于新造一个事件源）。
        ///
        /// 与 <c>WhiteboxComposeProjectileFeedback</c> 的世界空间飘字并存：飘字钉在施法点、
        /// 会飞出视野也会被连续触发挤掉，这条 HUD 行是固定屏幕位的"最新一条"兜底。
        /// </summary>
        private void RefreshReactionFeedback()
        {
            if (_reactionFeedbackBlock == null)
            {
                return;
            }

            if (_pendingReactionText == null)
            {
                return;
            }

            if (Time.unscaledTime >= _reactionExpireTime)
            {
                _pendingReactionText = null;
                _reactionTextDirty = false;
                if (_reactionFeedbackText != null)
                {
                    _reactionFeedbackText.text = string.Empty;
                }
                _reactionFeedbackBlock.style.display = DisplayStyle.None;
                return;
            }

            if (_reactionTextDirty)
            {
                _reactionTextDirty = false;
                if (_reactionFeedbackText != null)
                {
                    _reactionFeedbackText.text = _pendingReactionText;
                }
                _reactionFeedbackBlock.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>
        /// 生效中的规则开关一行（ui-visual-overhaul story-007）。
        ///
        /// 在此之前全仓没有任何 UI/HUD/图鉴读过 <see cref="RuleFlags.Current"/>：玩家装到
        /// `ComboNeverResets`/`CorpseEdible` 这类卡之后规则**真的变了**，却没有任何地方告诉他。
        ///
        /// 每帧只比一个 int（<see cref="RuleFlags.Version"/>），版本没动就直接返回——
        /// 逐帧遍历 flag 集合拼字符串是纯 GC 浪费，且热更层每帧只允许 O(1)。
        /// </summary>
        private void RefreshRuleFlags()
        {
            if (_ruleFlagsBlock == null)
            {
                return;
            }

            RuleFlags rules = RuleFlags.Current;
            if (rules == null)
            {
                return;
            }

            if (rules.Version == _ruleFlagsVersion)
            {
                return;
            }
            _ruleFlagsVersion = rules.Version;

            if (rules.Active.Count == 0)
            {
                // 一条规则都没生效时不留空行占位——大多数局的大多数时间都是这个状态。
                _ruleFlagsBlock.style.display = DisplayStyle.None;
                return;
            }

            var sb = new System.Text.StringBuilder("规则：");
            bool first = true;
            foreach (RuleFlag flag in rules.Active)
            {
                if (!first)
                {
                    sb.Append(' ').Append('/').Append(' ');
                }
                sb.Append(RuleFlagNames.Get(flag));
                first = false;
            }

            if (_ruleFlagsText != null)
            {
                _ruleFlagsText.text = sb.ToString();
            }
            _ruleFlagsBlock.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// 已在广播的 <see cref="ComposeCastSignal"/> 订阅端（不改判定层、不改信号字段）。
        /// D4 节流：只保留最新一条，后到的覆盖先到的并重置计时，不做队列/历史。
        /// D5：未收录 id 走 <see cref="ReactionFeedbackCatalog.GetLabel"/> 现有回落（原样显示 raw id），
        /// 与世界飘字同源同行为，这里不额外加隐藏分支。
        /// </summary>
        private void OnComposeCast(ComposeCastSignal s)
        {
            if (string.IsNullOrEmpty(s.ReactionName))
            {
                // 绝大多数组合出口没有具名反应，此时保持上一条播报的既有计时，不清空也不刷新。
                return;
            }

            _pendingReactionText = "反应：" + ReactionFeedbackCatalog.GetLabel(s.ReactionName);
            _reactionExpireTime = Time.unscaledTime + ReactionFeedbackHoldSeconds;
            _reactionTextDirty = true;
        }

        /// <summary>
        /// 冷却环高度用 AbilitySystem.EffectiveCooldown 的同款公式重算（该方法私有，
        /// 无法直接复用），而非 D9 允许的近似口径——CooldownReduction 可从 cell.Stats 读到，
        /// 精确值总是够用。skill-cast 施放脉冲本 story 不接（D9，需订阅施放事件，超出静态还原范围）。
        /// </summary>
        private void RefreshSkillSlots(CellStageFlow cell)
        {
            var slots = cell.Abilities.Slots;
            float cdr = cell.Stats.Get(StatId.CooldownReduction);

            for (int i = 0; i < SkillSlotCount; i++)
            {
                VisualElement slot = _skillSlots[i];
                if (slot == null)
                {
                    continue;
                }

                if (i >= slots.Count)
                {
                    slot.style.display = DisplayStyle.None;
                    continue;
                }
                slot.style.display = DisplayStyle.Flex;

                AbilityRuntime rt = slots[i];
                slot.RemoveFromClassList("skill-ready");
                slot.RemoveFromClassList("skill-cd");
                slot.RemoveFromClassList("skill-empty-charge");
                slot.AddToClassList(rt.Ready ? "skill-ready" : "skill-cd");
                if (rt.ChargesLeft == 0 && !rt.Ready)
                {
                    slot.AddToClassList("skill-empty-charge");
                }

                if (_skillName[i] != null)
                {
                    _skillName[i].text = rt.Spec.Name;
                }
                if (_skillState[i] != null)
                {
                    _skillState[i].text = rt.Ready ? "就绪" : $"{rt.CooldownLeft:F1}s";
                }
                if (_skillCharge[i] != null)
                {
                    _skillCharge[i].text = $"x{rt.ChargesLeft}";
                }
                if (_skillCooldownOverlay[i] != null)
                {
                    float effectiveCooldown = Mathf.Max(0.05f, rt.Spec.Cooldown * (1f - cdr));
                    float overlayPct = rt.Ready ? 0f : Mathf.Clamp01(rt.CooldownLeft / effectiveCooldown) * 100f;
                    _skillCooldownOverlay[i].style.height = new Length(overlayPct, LengthUnit.Percent);
                }
            }
        }

        private void OnDestroy()
        {
            _scope?.Dispose();
            _scope = null;

            if (_advanceButton != null)
            {
                _advanceButton.clicked -= OnAdvanceButtonClicked;
            }
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_tagChipTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_tagChipTemplate);
                _tagChipTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
