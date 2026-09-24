using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Expedition
{
    /// <summary>ER5-RETURN-01 STORY-EXECUTION-CARDS.md 第1条："撤离点至少一台可移动友军到达后显示确认
    /// 面板：已上车/遗留货物、幸存/阵亡、未完成目标、当前技术是否仍不可解析；玩家可取消继续战斗。
    /// 确认后才进入回城事务。"第3条："全队失去移动/战斗能力显示'放弃远征'。"结构落在 UXML/USS
    /// （<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件；全部业务判断委托
    /// <see cref="ExpeditionReturnService"/>，本类不重新实现任何规则。
    ///
    /// 两种模式共用同一面板：<see cref="ExpeditionReturnService.ReturnSnapshot.IsWipe"/> 为假时显示
    /// "确认撤离/取消"（<see cref="FracturedCityController.IsEvacPanelOpen"/> 由到达撤离点的 E 交互
    /// 打开）；为真时自动显示（不需要任何交互——全灭后没有存活机器可以按 E）"放弃远征"单按钮。</summary>
    public sealed class ExpeditionReturnPanelUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 8;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _wipeBanner;
        private Label _keyTechLabel;
        private Label _objectiveLabel;
        private Label _groundLabel;
        private ScrollView _list;
        private Label _summaryLabel;
        private VisualElement _evacButtons;
        private VisualElement _abandonButtons;
        private Button _confirmButton;
        private Button _cancelButton;
        private Button _abandonButton;

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);
        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ExpeditionReturnPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ExpeditionReturnRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：ER5-EXP-01 的准备面板在 8，本面板紧邻其后取 9。
            _document.sortingOrder = 9;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[ExpeditionReturnPanelUIToolkit] rootVisualElement 等待超时，撤离确认面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("ExpeditionReturnPanelRoot");
            _wipeBanner = _root.Q<Label>("WipeBanner");
            _keyTechLabel = _root.Q<Label>("KeyTechLabel");
            _objectiveLabel = _root.Q<Label>("ObjectiveLabel");
            _groundLabel = _root.Q<Label>("GroundLabel");
            _list = _root.Q<ScrollView>("RosterList");
            _summaryLabel = _root.Q<Label>("SummaryLabel");
            _evacButtons = _root.Q<VisualElement>("EvacButtons");
            _abandonButtons = _root.Q<VisualElement>("AbandonButtons");
            _confirmButton = _root.Q<Button>("ConfirmButton");
            _cancelButton = _root.Q<Button>("CancelButton");
            _abandonButton = _root.Q<Button>("AbandonButton");

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _list.Add(row);
                _rowPool.Add(row);
            }

            _confirmButton.clicked += OnConfirmClicked;
            _cancelButton.clicked += OnCancelClicked;
            _abandonButton.clicked += OnAbandonClicked;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            // ER6-REGION-01：两个远征区域共用同一面板——分别读各自 Controller 的 IsEvacPanelOpen/
            // IsWiped，与 ExpeditionReturnService.ResolveActive 同一判定口径（"哪个 Controller
            // IsActive 就是当前活动远征"），不假设永远是破碎都市。
            FracturedCityController fc = GameRoot.FracturedCity;
            FoundryOutpostController fo = GameRoot.FoundryOutpost;
            bool open = (fc != null && fc.IsActive && (fc.IsEvacPanelOpen || fc.IsWiped)) ||
                (fo != null && fo.IsActive && (fo.IsEvacPanelOpen || fo.IsWiped));
            _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (!open)
            {
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            Refresh();
        }

        private void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            ExpeditionReturnService.ReturnSnapshot snapshot = ExpeditionReturnService.BuildSnapshot(state);
            if (!snapshot.Available)
            {
                return; // Update() 的 open 判定已经保证这里不该发生；防御性早退，不刷渲染半份数据。
            }

            _wipeBanner.RemoveFromClassList("exp-wipe-banner-visible");
            if (snapshot.IsWipe)
            {
                _wipeBanner.AddToClassList("exp-wipe-banner-visible");
            }
            _evacButtons.style.display = snapshot.IsWipe ? DisplayStyle.None : DisplayStyle.Flex;
            _abandonButtons.style.display = snapshot.IsWipe ? DisplayStyle.Flex : DisplayStyle.None;

            int aliveCount = snapshot.Roster.Count(m => m.IsAlive);
            int deadCount = snapshot.Roster.Length - aliveCount;

            string keyTechText = string.Join("；", snapshot.KeyTech.Select(k =>
                $"{DisplayNameFor(k.ContentId)}：{DescribeQuestState(k.State)}"));
            _keyTechLabel.text = "关键技术：" + keyTechText;

            // ER6-REGION-01（DEMO-CONTENT-LOCK.md §4.2第4条）："允许不打Boss就撤离，且结算文本称为
            // 成功侦察而非失败逃跑"——铸造前哨外围撤离不管重炮是否带回都不该读作"未完成/失败"；只有
            // 破碎都市沿用旧有"待补回收"用语（该区域是双关键物任务，语义不同）。
            bool isFoundry = snapshot.RegionId == FoundryOutpostLayout.RegionId;
            if (isFoundry)
            {
                _objectiveLabel.text = snapshot.IsWipe
                    ? "任务目标：侦察未能完成（外围全灭，可再次侦察，一次性废料/缓存已领取部分不会重刷）"
                    : snapshot.ObjectivesComplete
                        ? "任务目标：侦察成功——重炮已归档为货物。核心区仍封锁；回城解析，完成第二次编译后再进攻。"
                        : "任务目标：侦察成功（重炮未带回，可再次侦察补回；核心区仍封锁）";
            }
            else
            {
                _objectiveLabel.text = snapshot.ObjectivesComplete ? "任务目标：已完成" : "任务目标：尚未完成（撤离后保持\"待补回收\"）";
            }
            _groundLabel.text = snapshot.GroundScrapItemCount > 0
                ? $"遗留货物：地面仍有 {snapshot.GroundScrapItemCount} 处未装载物资（撤离后不自动入账）"
                : "遗留货物：无";

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= snapshot.Roster.Length)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                ExpeditionReturnService.ManifestEntry m = snapshot.Roster[i];
                row.style.display = DisplayStyle.Flex;
                row.RemoveFromClassList("exp-row-alive");
                row.RemoveFromClassList("exp-row-dead");
                row.AddToClassList(m.IsAlive ? "exp-row-alive" : "exp-row-dead");

                row.Q<Label>("Number").text = $"#{m.DisplayNumber}";
                row.Q<Label>("Chassis").text = ChassisCatalog.TryGet(m.ChassisId, out MechanicalContentDef chassisDef)
                    ? chassisDef.DisplayName
                    : m.ChassisId;
                row.Q<Label>("Health").text = m.IsAlive ? $"HP{m.Health:F0}/{m.MaxHealth:F0}" : "—";
                row.Q<Label>("Status").text = m.IsAlive ? "幸存，将随撤离返回家园" : "阵亡（纪念记录，黑匣子保留经历，机体不复活）";
            }

            _summaryLabel.text = snapshot.IsWipe
                ? $"全队 {snapshot.Roster.Length} 台机器全部失去战斗/移动能力，已无法继续远征。已装车关键物随全灭结算为丢失，已归档的技术与家园不受影响。"
                : $"幸存 {aliveCount} 台，阵亡 {deadCount} 台。确认撤离后，幸存机器携带的关键物视为已带回，未装车的物资留在原地。";
        }

        /// <summary>ER6-REGION-01：改查 <see cref="HomeValleyAnalysis.YieldTable"/> 统一权威展示名
        /// （已覆盖破碎都市两件+铸造前哨外围四件），不再各区域各写一份硬编码映射，查不到才回退原始 id。</summary>
        private static string DisplayNameFor(string contentId) =>
            HomeValleyAnalysis.YieldTable.TryGetValue(contentId, out HomeValleyAnalysis.YieldInfo info)
                ? info.DisplayName
                : contentId;

        private static string DescribeQuestState(RegionQuestItemState state)
        {
            switch (state)
            {
                case RegionQuestItemState.Recovered: return "已带回";
                case RegionQuestItemState.Carried: return "已装车（撤离后带回）";
                case RegionQuestItemState.OnGround: return "尚未拾取";
                case RegionQuestItemState.Lost: return "已丢失（下次进入由恢复柜补发）";
                default: return "未知";
            }
        }

        private void OnConfirmClicked()
        {
            ExpeditionReturnService.ReturnResult result = ExpeditionReturnService.TryConfirmEvacuation();
            if (!result.Success)
            {
                Log.Warning($"[ExpeditionReturnPanelUIToolkit] 确认撤离未成功：{result.FailureReason}");
            }
            _refreshTimer = 0f;
        }

        private void OnCancelClicked()
        {
            // 两区域中哪个真正 IsEvacPanelOpen 就关哪个——全灭时(IsWiped)面板自动显示但没有
            // "取消"意义（全队已无法继续），Cancel 按钮结构上只在非全灭分支渲染，这里双路调用是
            // 防御性写法，成对 SetEvacPanelOpen(false) 在另一路上是安全的 no-op。
            GameRoot.FracturedCity?.SetEvacPanelOpen(false);
            GameRoot.FoundryOutpost?.SetEvacPanelOpen(false);
        }

        private void OnAbandonClicked()
        {
            ExpeditionReturnService.ReturnResult result = ExpeditionReturnService.TryConfirmAbandon();
            if (!result.Success)
            {
                Log.Warning($"[ExpeditionReturnPanelUIToolkit] 放弃远征未成功：{result.FailureReason}");
            }
            _refreshTimer = 0f;
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
