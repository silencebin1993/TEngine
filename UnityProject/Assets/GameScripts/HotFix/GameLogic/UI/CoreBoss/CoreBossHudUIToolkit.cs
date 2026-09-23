using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
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

        private float _refreshTimer;

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

            _panel = _root.Q<VisualElement>("CoreBossHudRoot");
            _phaseLabel = _root.Q<Label>("PhaseLabel");
            _coreHealthFill = _root.Q<VisualElement>("CoreHealthFill");
            _coreHealthLabel = _root.Q<Label>("CoreHealthLabel");
            _node1Fill = _root.Q<VisualElement>("Node1Fill");
            _node2Fill = _root.Q<VisualElement>("Node2Fill");
            _heatLabel = _root.Q<Label>("HeatLabel");
            _lockoutWarningLabel = _root.Q<Label>("LockoutWarningLabel");
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
            bool visible = region != null && FoundryOutpostCoreBoss.IsInitialized(region);

            _panel.RemoveFromClassList("boss-root-visible");
            if (visible)
            {
                _panel.AddToClassList("boss-root-visible");
            }
            if (!visible)
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

        private void Refresh(CampaignState state, RegionRecord region)
        {
            _phaseLabel.text = "主核心：" + FoundryOutpostCoreBoss.DisplayPhaseText(region);

            RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.MainCoreId);
            if (core != null)
            {
                float pct = core.MaxHealth > 0f ? Mathf.Clamp01(core.Health / core.MaxHealth) : 0f;
                _coreHealthFill.style.width = new Length(pct * 100f, LengthUnit.Percent);
                _coreHealthLabel.text = $"核心 HP {core.Health:F0}/{core.MaxHealth:F0}";
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
                ? $"重炮热量 #{hottest.DisplayNumber}：{hottest.WeaponHeat:F0}/{FracturedCityLayout.WeaponHeatOverheatThreshold:F0}" +
                  (hottest.IsWeaponOverheated ? "（过热停火）" : string.Empty)
                : string.Empty;

            bool lockoutVisible = region.CoreLockoutWarnAtPlaySeconds > 0f && !region.CoreLockoutActive;
            bool lockoutActive = region.CoreLockoutActive;
            _lockoutWarningLabel.RemoveFromClassList("boss-lockout-warning-visible");
            if (lockoutVisible || lockoutActive)
            {
                _lockoutWarningLabel.AddToClassList("boss-lockout-warning-visible");
                _lockoutWarningLabel.text = lockoutActive
                    ? "区域封锁已生效：核心分区入口已锁死，无法退回外围。"
                    : "警告：核心分区即将封锁，2秒后无法退回外围！";
            }
            else
            {
                _lockoutWarningLabel.text = string.Empty;
            }
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
