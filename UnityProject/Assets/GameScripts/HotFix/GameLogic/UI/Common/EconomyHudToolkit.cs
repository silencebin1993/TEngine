using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// ER3-ECO-01 AC-ECO-009："玩家能从 HUD 追溯资源来源、预留、消费和阻塞；无静默数值变化"——
    /// 本类是 <see cref="CampaignEconomyLedger.GetSummary"/>（纯数据查询 API）的唯一 UI 消费方。
    ///
    /// 全代码构建视觉树，不依赖 UXML/USS 资产，写法与 <see cref="StrategyClockHudToolkit"/> 同构
    /// （同一个 <c>_hudHost</c> 常驻挂载点，同一份 "BattleHudPanelSettings"）；两者一左一右分布，
    /// 不互相遮挡。常驻单例，只在归还谷地激活时可见（当前只有归还谷地接入了经济账本——ER5-EXP-01/
    /// ER6/ER7 等区域接入后再扩展 <see cref="Update"/> 里的可见性判断）。
    ///
    /// 只读固定 3 条最近流水的 Label 做轻量对象复用，不逐帧新增/销毁 VisualElement——账本变更
    /// 频率低（修复/拆解完工才变），但仍遵守"UI 层不逐帧分配 GC"的习惯写法。
    ///
    /// ER3-PWR-01 起额外挂了一个"电网"分区，展示 <see cref="HomeValleyPowerGrid.GetSummary"/>
    /// 的供给/需求/差额/被停建筑（STORY-EXECUTION-CARDS.md #ER3-PWR-01"显示供给/需求/差额/
    /// 被停建筑"要求的展示部分）。改优先级/主动关停的正式 UI 交互入口留给 ER5-INT-01/UI-04——
    /// 那部分依赖尚未实现的 E 交互系统，本类只做只读展示，不新增按钮。</summary>
    public sealed class EconomyHudToolkit : MonoBehaviour
    {
        private const int RecentCount = 3;

        private UIDocument _document;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private VisualElement _panel;
        private Label _titleLabel;
        private Label _availableLabel;
        private Label _reservedLabel;
        private readonly Label[] _recentLabels = new Label[RecentCount];
        private Label _powerLabel;
        private Label _brownoutLabel;
        private Label _signalLabel;
        private Label _storageLabel;
        private Label _groundItemsLabel;

        public static EconomyHudToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 4; // StrategyClockHudToolkit 用 3（右上角），本面板左上角，不重叠。

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[EconomyHudToolkit] rootVisualElement 等待超时，经济账本 HUD 未初始化。");
                return;
            }

            BuildUi();
        }

        private void BuildUi()
        {
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.top = 8;
            _panel.style.left = 8;
            _panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            _panel.style.paddingLeft = 8;
            _panel.style.paddingRight = 8;
            _panel.style.paddingTop = 4;
            _panel.style.paddingBottom = 4;
            _panel.style.minWidth = 220;
            _panel.style.display = DisplayStyle.None; // 默认隐藏，Update() 按当前是否在归还谷地决定。
            _root.Add(_panel);

            _titleLabel = new Label("废料账本");
            _titleLabel.style.color = Color.white;
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _panel.Add(_titleLabel);

            _availableLabel = new Label();
            _availableLabel.style.color = Color.white;
            _panel.Add(_availableLabel);

            _reservedLabel = new Label();
            _reservedLabel.style.color = new Color(0.85f, 0.85f, 0.6f);
            _panel.Add(_reservedLabel);

            for (int i = 0; i < RecentCount; i++)
            {
                var label = new Label();
                label.style.color = new Color(0.75f, 0.75f, 0.75f);
                label.style.fontSize = 11;
                label.style.display = DisplayStyle.None;
                _recentLabels[i] = label;
                _panel.Add(label);
            }

            _powerLabel = new Label();
            _powerLabel.style.color = Color.white;
            _powerLabel.style.marginTop = 4;
            _panel.Add(_powerLabel);

            _brownoutLabel = new Label();
            _brownoutLabel.style.color = new Color(0.95f, 0.4f, 0.35f);
            _brownoutLabel.style.fontSize = 11;
            _brownoutLabel.style.display = DisplayStyle.None;
            _panel.Add(_brownoutLabel);

            // ER5-SIG-01：信号带宽此前只有数据（HomeValleyPowerGrid.Recompute 正确计算），
            // 从未在任何 HUD 展示——"塔停电时带宽回落"缺一个真实可见的地方。
            _signalLabel = new Label();
            _signalLabel.style.color = Color.white;
            _signalLabel.style.marginTop = 4;
            _panel.Add(_signalLabel);

            _storageLabel = new Label();
            _storageLabel.style.color = Color.white;
            _storageLabel.style.marginTop = 4;
            _panel.Add(_storageLabel);

            // FG0-UX-01（FGR-UX-030 / FG-GAP-003）：电力与信号带宽是复合数值，悬停展开来源（与电网仲裁同一份数据）。
            Kit.UiTooltip.Attach(_powerLabel, () => HomeValueBreakdown.Power(CampaignSession.Current));
            Kit.UiTooltip.Attach(_signalLabel, () => HomeValueBreakdown.Signal(CampaignSession.Current));

            _groundItemsLabel = new Label();
            _groundItemsLabel.style.color = new Color(0.95f, 0.75f, 0.35f);
            _groundItemsLabel.style.fontSize = 11;
            _groundItemsLabel.style.display = DisplayStyle.None;
            _panel.Add(_groundItemsLabel);
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            _panel.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
            {
                return;
            }

            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }

            CampaignEconomyLedger.ResourceSummary summary =
                CampaignEconomyLedger.GetSummary(state, CampaignEconomyLedger.ResourceScrap, RecentCount);

            _availableLabel.text = $"可用：{summary.Available:0}";
            _reservedLabel.text = summary.OpenReserved > 0f
                ? $"预留中：{summary.OpenReserved:0}"
                : "预留中：0";

            for (int i = 0; i < RecentCount; i++)
            {
                Label label = _recentLabels[i];
                if (i >= summary.RecentEntries.Length)
                {
                    label.style.display = DisplayStyle.None;
                    continue;
                }

                ResourceTransactionRecord entry = summary.RecentEntries[i];
                label.text = FormatEntry(entry);
                label.style.display = DisplayStyle.Flex;
            }

            HomeValleyPowerGrid.GridSummary power = HomeValleyPowerGrid.GetSummary(state);
            _powerLabel.text = power.Shortfall > 0f
                ? $"电力：供给{power.TotalSupply:0}/需求{power.TotalDemand:0}（差额{power.Shortfall:0}）"
                : $"电力：供给{power.TotalSupply:0}/需求{power.TotalDemand:0}";

            if (power.BrownoutBuildingIds.Length > 0)
            {
                // ER8-CONTENT-01 AC-THEME-001：此前直接拼接 BuildingId（“home_valley:signal_tower”），
                // 把内部 ID 显示给了玩家；改为内容目录展示名。
                _brownoutLabel.text = "断电：" + string.Join("、",
                    System.Array.ConvertAll(power.BrownoutBuildingIds, MechanicalContentFacade.ResolveWorkOrderTargetLabel));
                _brownoutLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _brownoutLabel.style.display = DisplayStyle.None;
            }

            float bandwidth = HomeValleySignal.BandwidthCapacity(state);
            string bandwidthSource = HomeValleySignal.TowerContributing(state) ? "基础3+塔5" : "基础3（塔未通电）";
            RegionRecord silentRuins = HomeValleySignal.Find(state);
            string unlockNote = silentRuins != null && silentRuins.State != RegionState.Locked ? "｜破碎都市已解锁" : string.Empty;
            _signalLabel.text = $"信号带宽：{bandwidth:0}（{bandwidthSource}）{unlockNote}";

            int capacity = HomeValleyCargo.GetStorageCapacity(state, CampaignEconomyLedger.ResourceScrap);
            int used = HomeValleyCargo.GetStorageUsed(state, CampaignEconomyLedger.ResourceScrap);
            _storageLabel.text = $"仓储：{used}/{capacity}";

            int groundCount = state.GroundItems?.Length ?? 0;
            if (groundCount > 0)
            {
                _groundItemsLabel.text = $"地面待收集：{groundCount} 处";
                _groundItemsLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                _groundItemsLabel.style.display = DisplayStyle.None;
            }
        }

        /// <summary>消费型（Requested&gt;=0）显示为负收支，生产型（Requested&lt;0）显示为正收支——
        /// 与 <see cref="CampaignEconomyLedger"/> 的符号约定一致，见该类注释。</summary>
        private static string FormatEntry(ResourceTransactionRecord entry)
        {
            // ER8-CONTENT-01 AC-THEME-001：此前显示“OwnerId ±n [Committed]”——内部 ID 与英文枚举名
            // 直接漏给了玩家。改为资源展示名 + 数量 + 中文状态。
            float delta = -entry.Requested;
            string sign = delta >= 0f ? "+" : "";
            return $"{CampaignEconomyLedger.ResourceDisplayName(entry.ResourceType)} {sign}{delta:0}（{CampaignEconomyLedger.StateDisplayName(entry.State)}）";
        }

        private void OnDestroy()
        {
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
