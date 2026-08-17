using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.Ability;
using GameLogic.MetabolicSlice.Combat;
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
        private Label _runTimer;
        private VisualElement _vitalBlock;
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
        private VisualElement _ecoEventBlock;
        private Label _ecoEventText;

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
            _ecoEventBlock = _root.Q<VisualElement>("EcoEventBlock");
            _ecoEventText = _root.Q<Label>("EcoEventText");

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

            RefreshHud(cell);
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

                int rm = (int)(tl.RunElapsed / 60f);
                int rs = (int)(tl.RunElapsed % 60f);
                _runTimer.text = $"本局 {rm:00}:{rs:00}";
            }

            float maxHp = st.Get(StatId.MaxHealth);
            float hp = cell.Sim.PlayerHealth;
            _hpText.text = $"生命 {hp:F0}/{maxHp:F0}";
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
            RefreshSkillSlots(cell);
            RefreshAxisTouchAndChain(cell);
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
