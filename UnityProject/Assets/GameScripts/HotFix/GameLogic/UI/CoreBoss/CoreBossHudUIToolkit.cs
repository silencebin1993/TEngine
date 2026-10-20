using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.CoreBoss
{
    /// <summary>ER7-CORE-01 STORY-EXECUTION-CARDS.md 第3条："Boss血条/阶段/节点/预警/热量不只打印
    /// 日志"——独立 UI Toolkit 面板（不复用共享 <c>RegionCommandBarUIToolkit</c>，避免把 Boss 专属
    /// 内容混进两区域通用 HUD），只在 <see cref="GameRoot.FoundryOutpost"/> 激活且
    /// <see cref="FoundryOutpostCoreBoss.IsInitialized"/> 时显示。全部数据只读展示，不做任何业务
    /// 判断——业务全部委托 <see cref="FoundryOutpostCoreBoss"/>，本类只绑定。</summary>
    public sealed class CoreBossHudUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _phaseLabel;
        private VisualElement _coreHealthFill;
        private Label _coreHealthLabel;
        private VisualElement _node1Fill;
        private VisualElement _node2Fill;
        private Label _heatLabel;
        private Label _lockoutWarningLabel;
        // FG9-FAC-02：阶段分界、阶段三 / 狂暴说明、场地边界倒计时、精英血条、上次止步阶段
        private VisualElement _markTransition;
        private VisualElement _markAntiAir;
        private Label _stageLabel;
        private Label _arenaLabel;
        private VisualElement _eliteRow;
        private Label _eliteLabel;
        private VisualElement _eliteFill;
        private Label _lastPhaseLabel;

        private float _refreshTimer;

        /// <summary>FG9-FAC-02 修复轮：冒烟 / 自检读——UXML 里的节点是否全部绑上（名字取错会静默不显示），最近一帧是否可见、写了什么。</summary>
        public static bool LastBound { get; private set; }
        public static bool LastVisible { get; private set; }
        public static string LastText { get; private set; } = string.Empty;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CoreBossHud");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：远征准备面板(8)与覆盖面板(10)之间的 9。
            _document.sortingOrder = 9;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[CoreBossHudUIToolkit] rootVisualElement 等待超时，Boss HUD 未初始化。");
                return;
            }

            LastBound = Bind(_root);
        }

        /// <summary>按 UXML 节点名绑定（运行时 Start 与自检共用同一个入口）。返回 true = 全部节点都找到了。</summary>
        public bool Bind(VisualElement root)
        {
            _root = root;
            if (_root == null)
            {
                return false;
            }
            _panel = _root.Q<VisualElement>("CoreBossHudRoot");
            _phaseLabel = _root.Q<Label>("PhaseLabel");
            _coreHealthFill = _root.Q<VisualElement>("CoreHealthFill");
            _coreHealthLabel = _root.Q<Label>("CoreHealthLabel");
            _node1Fill = _root.Q<VisualElement>("Node1Fill");
            _node2Fill = _root.Q<VisualElement>("Node2Fill");
            _heatLabel = _root.Q<Label>("HeatLabel");
            _lockoutWarningLabel = _root.Q<Label>("LockoutWarningLabel");
            _markTransition = _root.Q<VisualElement>("MarkTransition");
            _markAntiAir = _root.Q<VisualElement>("MarkAntiAir");
            _stageLabel = _root.Q<Label>("StageLabel");
            _arenaLabel = _root.Q<Label>("ArenaLabel");
            _eliteRow = _root.Q<VisualElement>("EliteRow");
            _eliteLabel = _root.Q<Label>("EliteLabel");
            _eliteFill = _root.Q<VisualElement>("EliteFill");
            _lastPhaseLabel = _root.Q<Label>("LastPhaseLabel");
            return _panel != null && _phaseLabel != null && _coreHealthFill != null && _coreHealthLabel != null && _node1Fill != null && _node2Fill != null
                   && _heatLabel != null && _lockoutWarningLabel != null && _markTransition != null && _markAntiAir != null && _stageLabel != null
                   && _arenaLabel != null && _eliteRow != null && _eliteLabel != null && _eliteFill != null && _lastPhaseLabel != null;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            FoundryOutpostController fo = GameRoot.FoundryOutpost;
            CampaignState state = CampaignSession.Current;
            RegionRecord region = fo != null && fo.IsActive && state != null ? FoundryOutpostRegion.Find(state) : null;
            if (!ApplyVisible(region))
            {
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            Refresh(state, region);
        }

        /// <summary>FG9-FAC-02：首领战失败过（有“上次止步于”）时，进场后也显示，重试前就能看到上次止步的阶段（FG09 第 9 节）。</summary>
        private bool ApplyVisible(RegionRecord region)
        {
            bool visible = region != null && (FoundryOutpostCoreBoss.IsInitialized(region) || !string.IsNullOrEmpty(region.CoreLastReachedState));
            LastVisible = visible;
            _panel.RemoveFromClassList("boss-root-visible");
            if (visible)
            {
                _panel.AddToClassList("boss-root-visible");
            }
            return visible;
        }

        /// <summary>立即刷新一次（不等 0.2 秒节流）：自检用真实 UXML 绑定后逐阶段读标签内容。返回是否可见。</summary>
        public bool RefreshNow(CampaignState state, RegionRecord region)
        {
            if (_panel == null || state == null || !ApplyVisible(region))
            {
                return false;
            }
            Refresh(state, region);
            return true;
        }

        private void Refresh(CampaignState state, RegionRecord region)
        {
            _phaseLabel.text = GameText.Format("boss.foundry.hud.title", FoundryOutpostCoreBoss.DisplayPhaseText(region));

            RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.MainCoreId);
            if (core != null)
            {
                float pct = core.MaxHealth > 0f ? Mathf.Clamp01(core.Health / core.MaxHealth) : 0f;
                _coreHealthFill.style.width = new Length(pct * 100f, LengthUnit.Percent);
                _coreHealthLabel.text = GameText.Format("boss.foundry.hud.hp", core.Health.ToString("F0"), core.MaxHealth.ToString("F0"));
            }
            // FGR-FAC-006：血条上标出阶段分界（阶段一 → 过渡、阶段二 → 对空形态）。
            if (_markTransition != null)
            {
                _markTransition.style.left = new Length(FoundryOutpostLayout.Phase1TransitionHealthFraction * 100f, LengthUnit.Percent);
            }
            if (_markAntiAir != null)
            {
                _markAntiAir.style.left = new Length(FoundryOutpostCoreBoss.Phase3HealthFraction * 100f, LengthUnit.Percent);
            }
            RefreshStage(state, region);
            RefreshElite(state);
            if (_lastPhaseLabel != null)
            {
                _lastPhaseLabel.text = !string.IsNullOrEmpty(region.CoreLastReachedState) && System.Enum.TryParse(region.CoreLastReachedState, out CoreBossState last)
                    ? GameText.Format("boss.foundry.hud.last_phase", FoundryOutpostCoreBoss.PhaseName(last))
                    : string.Empty;
            }

            SetNodeFill(_node1Fill, FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode1Id));
            SetNodeFill(_node2Fill, FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode2Id));

            // "热量"——本场战斗玩家自己武器的过热风险（熔穿过载代价），不是敌方数值；取本区域存活
            // 机器里当前热量最高的一台展示，没有任何机器有热量时不显示误导性的0。
            MachineRecord hottest = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == FoundryOutpostLayout.RegionId && m.WeaponHeat > 0f)
                .OrderByDescending(m => m.WeaponHeat)
                .FirstOrDefault();
            _heatLabel.text = hottest != null
                ? GameText.Format("boss.foundry.hud.heat", MachineNaming.Short(hottest), hottest.WeaponHeat.ToString("F0"),
                      FracturedCityLayout.WeaponHeatOverheatThreshold.ToString("F0")) +
                  (hottest.IsWeaponOverheated ? GameText.Get("boss.foundry.hud.overheated") : string.Empty)
                : string.Empty;

            bool lockoutVisible = region.CoreLockoutWarnAtPlaySeconds > 0f && !region.CoreLockoutActive;
            bool lockoutActive = region.CoreLockoutActive;
            _lockoutWarningLabel.RemoveFromClassList("boss-lockout-warning-visible");
            if (lockoutVisible || lockoutActive)
            {
                _lockoutWarningLabel.AddToClassList("boss-lockout-warning-visible");
                _lockoutWarningLabel.text = GameText.Get(lockoutActive ? "boss.foundry.hud.lockout_active" : "boss.foundry.hud.lockout_warn");
            }
            else
            {
                _lockoutWarningLabel.text = string.Empty;
            }
            LastText = string.Join("\n", _phaseLabel.text, _coreHealthLabel.text, _stageLabel?.text, _arenaLabel?.text,
                _eliteRow != null && _eliteRow.style.display.value == DisplayStyle.Flex ? _eliteLabel?.text : string.Empty, _lastPhaseLabel?.text);
        }

        /// <summary>FG9-FAC-02：阶段二常显阶段三情报；对空形态倒计时；狂暴开火倍率；场地边界倒计时。</summary>
        private void RefreshStage(CampaignState state, RegionRecord region)
        {
            CoreBossState s = FoundryOutpostCoreBoss.GetState(region);
            string stage;
            switch (s)
            {
                case CoreBossState.Phase2:
                    stage = GameText.Format("boss.foundry.hud.next_phase", Mathf.RoundToInt(FoundryOutpostCoreBoss.Phase3HealthFraction * 100f).ToString(),
                        Mathf.RoundToInt(FoundryOutpostCoreBoss.AntiAirSeconds).ToString());
                    break;
                case CoreBossState.AntiAir:
                    stage = GameText.Format("boss.foundry.hud.antiair", FoundryOutpostCoreBoss.ExposedDamageMult.ToString("0.##"),
                        Mathf.Max(0f, region.CoreAntiAirEndAtPlaySeconds - state.PlaySeconds).ToString("F0"));
                    break;
                case CoreBossState.Berserk:
                    stage = GameText.Format("boss.foundry.hud.berserk", FoundryOutpostCoreBoss.BerserkCooldownMult.ToString("0.##"));
                    break;
                default:
                    stage = string.Empty;
                    break;
            }
            if (_stageLabel != null)
            {
                _stageLabel.text = stage;
            }
            if (_arenaLabel != null)
            {
                _arenaLabel.text = FoundryOutpostCoreBoss.IsFighting(region) && region.CoreArenaOutsideSeconds > 0f
                    ? GameText.Format("boss.foundry.hud.arena_leave", Mathf.Max(0f, FoundryOutpostCoreBoss.ArenaLeaveSeconds - region.CoreArenaOutsideSeconds).ToString("F0"))
                    : string.Empty;
            }
        }

        /// <summary>FG09 第 9 节“精英和首领的血条显示名称和阶段”：本地点存活的精英（敌人表层级 elite）的名字与耐久（取第一台）。</summary>
        private void RefreshElite(CampaignState state)
        {
            if (_eliteRow == null)
            {
                return;
            }
            RegionEnemyRecord elite = null;
            if (state.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord e in state.RegionEnemies)
                {
                    if (e != null && e.IsAlive && e.RegionId == FoundryOutpostLayout.RegionId
                        && Campaign.Content.EnemyDefs.TryGet(e.EnemyTypeId, out GameConfig.fg.MechEnemy row) && Campaign.Content.EnemyDefs.IsElite(row))
                    {
                        elite = e;
                        break;
                    }
                }
            }
            _eliteRow.style.display = elite != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (elite == null)
            {
                return;
            }
            _eliteLabel.text = GameText.Format("boss.foundry.hud.elite", Campaign.Content.EnemyDefs.NameOf(elite.EnemyTypeId), elite.Health.ToString("F0"),
                elite.MaxHealth.ToString("F0"));
            float pct = elite.MaxHealth > 0f ? Mathf.Clamp01(elite.Health / elite.MaxHealth) : 0f;
            _eliteFill.style.width = new Length(pct * 100f, LengthUnit.Percent);
        }

        private static void SetNodeFill(VisualElement fill, RegionEnemyRecord node)
        {
            if (fill == null)
            {
                return;
            }
            float pct = node != null && node.IsAlive && node.MaxHealth > 0f ? Mathf.Clamp01(node.Health / node.MaxHealth) : 0f;
            fill.style.width = new Length(pct * 100f, LengthUnit.Percent);
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
