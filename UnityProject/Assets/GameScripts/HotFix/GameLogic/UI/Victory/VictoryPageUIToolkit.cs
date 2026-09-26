using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.UI.Common;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

namespace GameLogic.UI.Victory
{
    /// <summary>ER7-CREDITS-01 STORY-EXECUTION-CARDS.md："胜利页可返回主菜单、查看本次统计与机器纪念
    /// 记录，不被困在结算界面"。全屏阻断式模态，只在信标真正启动完成（<see cref="HomeValleyBeacon.IsLaunched"/>）
    /// 且不在10秒演出中（<see cref="HomeValleyBeacon.IsLaunching"/> 为假——演出期间由
    /// <c>BeaconLaunchPanelUIToolkit</c> 的倒计时文案覆盖，两者不同时出现）时显示。"已完成档再次加载
    /// 不自动重演"天然成立：<see cref="HomeValleyBeacon.IsLaunching"/> 一旦 <see cref="HomeValleyBeacon.IsLaunched"/>
    /// 为真就恒为假（见该类判定），读档后不会重新进入倒计时态，只会直接展示本页。</summary>
    public sealed class VictoryPageUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 10;
        private const float RefreshIntervalSeconds = 0.5f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _summaryLabel;
        private ScrollView _list;
        private Label _crossFactionLabel;
        private Label _regionLabel;
        private Button _backToMenuButton;

        private readonly System.Collections.Generic.List<TemplateContainer> _rowPool =
            new System.Collections.Generic.List<TemplateContainer>(MaxRows);

        private float _refreshTimer;
        private bool _wasShown;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("VictoryPage");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("VictoryMachineRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 全屏阻断式模态，同家园失败面板（UI_WORKFLOW_GUIDE.md 第4节）同一量级——两者互斥（一个
            // 是核心被毁失败，一个是信标启动成功），不会同时显示，共用 sortingOrder 不冲突；同样要盖过浮动窗口。
            _document.sortingOrder = Common.UiWindowFocus.ModalSortingOrder;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[VictoryPageUIToolkit] rootVisualElement 等待超时，胜利页未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("VictoryPageRoot");
            _summaryLabel = _root.Q<Label>("SummaryLine");
            _list = _root.Q<ScrollView>("MachineList");
            _crossFactionLabel = _root.Q<Label>("CrossFactionLine");
            _regionLabel = _root.Q<Label>("RegionLine");
            _backToMenuButton = _root.Q<Button>("BackToMenuButton");
            _backToMenuButton.clicked += () => GameRoot.EndRun();

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _list.Add(row);
                _rowPool.Add(row);
            }
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            CampaignState state = CampaignSession.Current;
            bool shouldShow = GameRoot.HomeValley != null && GameRoot.HomeValley.IsLoaded
                && HomeValleyBeacon.IsLaunched(state) && !HomeValleyBeacon.IsLaunching(state);
            // FG0-UX-01（FGR-UX-001）：胜利页不能 Esc 关掉，Esc 也不许穿透去开一个被它盖住的暂停菜单。
            UiEscapeStack.SyncBlocking(this, shouldShow);

            _panel.RemoveFromClassList("vp-root-visible");
            if (shouldShow)
            {
                _panel.AddToClassList("vp-root-visible");
            }
            if (!shouldShow)
            {
                _wasShown = false;
                return;
            }

            // 数据不会在展示期间变化（战役已结算），只在刚显示的那一帧刷新一次，之后按低频轮询兜底
            // （防止极端情况下第一帧数据还没就绪）。
            if (!_wasShown)
            {
                Refresh(state);
            }
            _wasShown = true;

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;
            Refresh(state);
        }

        private void Refresh(CampaignState state)
        {
            CampaignCredits.Snapshot snapshot = CampaignCredits.Build(state);

            int aliveCount = snapshot.Machines.Count(m => m.IsAlive);
            int deadCount = snapshot.Machines.Length - aliveCount;
            _summaryLabel.text = $"幸存 {aliveCount} 台｜阵亡 {deadCount} 台｜接管 {snapshot.ControlTakeovers} 次｜" +
                $"出征 {snapshot.TotalExpeditionCount} 次｜完成时间 {snapshot.CompletionPlaySeconds:F0} 秒";

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= snapshot.Machines.Length)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                CampaignCredits.MachineSummary m = snapshot.Machines[i];
                row.style.display = DisplayStyle.Flex;
                ContentIcons.Apply(row.Q<VisualElement>("Icon"), m.ChassisId); // ER8-CONTENT-01：行首底盘图标。
                Label numberLabel = row.Q<Label>("NumberLabel");
                numberLabel.text = $"#{m.DisplayNumber}（{(m.IsAlive ? "幸存" : "阵亡")}）";
                numberLabel.RemoveFromClassList("vp-machine-alive");
                numberLabel.RemoveFromClassList("vp-machine-dead");
                numberLabel.AddToClassList(m.IsAlive ? "vp-machine-alive" : "vp-machine-dead");
                row.Q<Label>("ExpLabel").text = m.ExperienceDisplayNames.Length > 0
                    ? string.Join("、", m.ExperienceDisplayNames)
                    : "无记录经历";
            }

            _crossFactionLabel.text = snapshot.CrossFactionBlueprintIds.Length > 0
                ? "使用过的跨派系蓝图：" + string.Join("、", snapshot.CrossFactionBlueprintIds)
                : "使用过的跨派系蓝图：无";
            _regionLabel.text = $"破碎都市：{snapshot.SilentRuinsState}｜铸造前哨：{snapshot.FoundryOutpostState}";
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
