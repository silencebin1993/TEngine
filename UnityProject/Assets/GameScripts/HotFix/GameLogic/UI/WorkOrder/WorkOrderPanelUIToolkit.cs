using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.WorkOrder
{
    /// <summary>ER3-WRK-01 STORY-EXECUTION-CARDS.md 第4条："工作面板显示 Ready/Reserved/InProgress/
    /// Waiting/Completed/Failed、原因和下一恢复动作"——本类是该要求在本 Story 范围内的最小落地：
    /// 列出当前归还谷地全部非终态 <see cref="WorkOrderRecord"/>（Ready/Reserved/InProgress/Waiting），
    /// 每行显示种类/目标/状态/阻塞原因。按优先级筛选、点击定位、取消预览释放/保留哪些材料这类完整
    /// 交互属于 ER3-WRK-02（AC-UI-003）/ER5-INT-01/UI-04，这里只做只读展示——与
    /// <see cref="Common.EconomyHudToolkit"/>/<see cref="Common.StrategyClockHudToolkit"/>"只读展示、
    /// 正式交互留后续 Story"的既定范围裁剪一致。
    ///
    /// 与前两个 HUD 不同：结构落在 UXML/USS（<c>unity-ui-toolkit.md</c> 硬规则起效后的新面板一律如此），
    /// C# 只做数据绑定，不手搭 VisualElement 树。</summary>
    public sealed class WorkOrderPanelUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 12;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private ScrollView _list;
        private Label _emptyLabel;
        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);

        public static WorkOrderPanelUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("WorkOrderPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("WorkOrderRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 0; // HUD 层（UI_WORKFLOW_GUIDE.md 第4节），与左上/右上两个既有 HUD 不重叠。

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[WorkOrderPanelUIToolkit] rootVisualElement 等待超时，工作单面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("WorkOrderPanelRoot");
            _list = _root.Q<ScrollView>("OrderList");
            _emptyLabel = _root.Q<Label>("EmptyLabel");

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

            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            _panel.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
            {
                return;
            }

            CampaignState state = CampaignSession.Current;
            WorkOrderRecord[] orders = state?.WorkOrders;
            List<WorkOrderRecord> live = orders == null
                ? new List<WorkOrderRecord>(0)
                : orders.Where(o => o.State == WorkOrderState.Ready || o.State == WorkOrderState.Reserved
                    || o.State == WorkOrderState.InProgress || o.State == WorkOrderState.Waiting)
                    .Take(MaxRows)
                    .ToList();

            _emptyLabel.style.display = live.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= live.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }

                WorkOrderRecord order = live[i];
                row.style.display = DisplayStyle.Flex;
                row.Q<Label>("Kind").text = order.Kind.ToString();
                row.Q<Label>("Target").text = order.TargetId;

                Label stateLabel = row.Q<Label>("State");
                stateLabel.text = order.State.ToString();
                stateLabel.RemoveFromClassList("wop-row-state-waiting");
                stateLabel.RemoveFromClassList("wop-row-state-failed");
                if (order.State == WorkOrderState.Waiting)
                {
                    stateLabel.AddToClassList("wop-row-state-waiting");
                }

                Label reasonLabel = row.Q<Label>("Reason");
                bool hasReason = !string.IsNullOrEmpty(order.FailureReason);
                reasonLabel.text = hasReason ? order.FailureReason : string.Empty;
                if (hasReason)
                {
                    reasonLabel.AddToClassList("wop-row-reason-visible");
                }
                else
                {
                    reasonLabel.RemoveFromClassList("wop-row-reason-visible");
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
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
