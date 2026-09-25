using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.PrimitiveCraft
{
    /// <summary>ER4-PRIM-04 STORY-EXECUTION-CARDS.md：合成台正式 UI——显示配方、材料实例、费用、
    /// 8秒升级/3秒拆解、电力、队列、取消、产物去向和满仓待领取；战斗/远征中入口禁用（归还谷地当前无
    /// 真实战斗触发源，见 <c>PrimitiveCraftStation</c> 类注释同类范围说明，本面板始终只在归还谷地激活时
    /// 可见，与该说明一致）。所有写操作都经 <see cref="PrimitiveCraftStation"/>/
    /// <see cref="PrimitiveInventory"/>，本类不直接改 <see cref="CraftQueueItemRecord"/>/
    /// <see cref="PrimitiveChipRecord"/> 字段。结构镜像 <see cref="Factory.FactoryPanelUIToolkit"/>/
    /// <see cref="CircuitBoard.CircuitBoardPanelUIToolkit"/> 同一套刷新降频/行池写法。</summary>
    public sealed class PrimitiveCraftPanelUIToolkit : MonoBehaviour
    {
        private const int MaxQueueRows = PrimitiveCraftStation.MaxActiveQueueItems + 4; // 含终态项短暂可见的余量
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _queueRowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Button _entryToggleButton;

        private Label _bagCapacityLabel;
        private DropdownField _upgradeMaterialADropdown;
        private DropdownField _upgradeMaterialBDropdown;
        private Button _enqueueUpgradeButton;
        private DropdownField _disassembleMaterialDropdown;
        private Button _enqueueDisassembleButton;
        private Label _craftResultLabel;

        private ScrollView _queueList;
        private DropdownField _cancelQueueDropdown;
        private Button _cancelQueueButton;

        private DropdownField _pendingOutputDropdown;
        private Button _claimOutputButton;

        private Button _closeButton;

        private readonly List<TemplateContainer> _queueRowPool = new List<TemplateContainer>(MaxQueueRows);
        private readonly List<string> _upgradeChoicePartIds = new List<string>();
        private readonly List<string> _disassembleChoicePartIds = new List<string>();
        private readonly List<string> _cancelChoiceQueueIds = new List<string>();
        private readonly List<string> _pendingChoicePartIds = new List<string>();

        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("PrimitiveCraftPanel");
            _queueRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CraftQueueRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：电路板(8)之后，覆盖面板(10)之前，取 9。
            _document.sortingOrder = 9;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[PrimitiveCraftPanelUIToolkit] rootVisualElement 等待超时，合成台面板未初始化。");
                return;
            }

            BindElements();
            WireEvents();
            SetPanelOpen(false);
        }

        private void BindElements()
        {
            _entryToggleButton = _root.Q<Button>("EntryToggleButton");
            _panel = _root.Q<VisualElement>("CraftPanelRoot");

            _bagCapacityLabel = _root.Q<Label>("BagCapacityLabel");
            _upgradeMaterialADropdown = _root.Q<DropdownField>("UpgradeMaterialADropdown");
            _upgradeMaterialBDropdown = _root.Q<DropdownField>("UpgradeMaterialBDropdown");
            _enqueueUpgradeButton = _root.Q<Button>("EnqueueUpgradeButton");
            _disassembleMaterialDropdown = _root.Q<DropdownField>("DisassembleMaterialDropdown");
            _enqueueDisassembleButton = _root.Q<Button>("EnqueueDisassembleButton");
            _craftResultLabel = _root.Q<Label>("CraftResultLabel");

            _queueList = _root.Q<ScrollView>("QueueList");
            _cancelQueueDropdown = _root.Q<DropdownField>("CancelQueueDropdown");
            _cancelQueueButton = _root.Q<Button>("CancelQueueButton");

            _pendingOutputDropdown = _root.Q<DropdownField>("PendingOutputDropdown");
            _claimOutputButton = _root.Q<Button>("ClaimOutputButton");

            _closeButton = _root.Q<Button>("CloseButton");

            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = _queueRowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _queueList.Add(row);
                _queueRowPool.Add(row);
            }
        }

        private void WireEvents()
        {
            _entryToggleButton.clicked += () => SetPanelOpen(!(GameRoot.HomeValley?.IsCraftStationPanelOpen ?? false));

            _enqueueUpgradeButton.clicked += () => RunCraftOp(() =>
            {
                if (_upgradeMaterialADropdown.index < 0 || _upgradeMaterialADropdown.index >= _upgradeChoicePartIds.Count
                    || _upgradeMaterialBDropdown.index < 0 || _upgradeMaterialBDropdown.index >= _upgradeChoicePartIds.Count)
                {
                    return "material-not-selected";
                }
                string a = _upgradeChoicePartIds[_upgradeMaterialADropdown.index];
                string b = _upgradeChoicePartIds[_upgradeMaterialBDropdown.index];
                PrimitiveCraftStation.CraftOpResult r = PrimitiveCraftStation.TryEnqueueUpgrade(CampaignSession.Current, a, b);
                return r.Success ? null : r.FailureReason;
            });

            _enqueueDisassembleButton.clicked += () => RunCraftOp(() =>
            {
                if (_disassembleMaterialDropdown.index < 0 || _disassembleMaterialDropdown.index >= _disassembleChoicePartIds.Count)
                {
                    return "material-not-selected";
                }
                string partId = _disassembleChoicePartIds[_disassembleMaterialDropdown.index];
                PrimitiveCraftStation.CraftOpResult r = PrimitiveCraftStation.TryEnqueueDisassemble(CampaignSession.Current, partId);
                return r.Success ? null : r.FailureReason;
            });

            _cancelQueueButton.clicked += () => RunCraftOp(() =>
            {
                if (_cancelQueueDropdown.index < 0 || _cancelQueueDropdown.index >= _cancelChoiceQueueIds.Count)
                {
                    return "no-queue-item-selected";
                }
                string queueItemId = _cancelChoiceQueueIds[_cancelQueueDropdown.index];
                PrimitiveCraftStation.CraftOpResult r = PrimitiveCraftStation.TryCancel(CampaignSession.Current, queueItemId);
                return r.Success ? null : r.FailureReason;
            });

            _claimOutputButton.clicked += () => RunCraftOp(() =>
            {
                if (_pendingOutputDropdown.index < 0 || _pendingOutputDropdown.index >= _pendingChoicePartIds.Count)
                {
                    return "no-pending-selected";
                }
                string partId = _pendingChoicePartIds[_pendingOutputDropdown.index];
                CircuitOpResult r = PrimitiveInventory.TryClaimPending(CampaignSession.Current, partId);
                return r.Success ? null : r.Message;
            });

            _closeButton.clicked += () => SetPanelOpen(false);
        }

        private void RunCraftOp(System.Func<string> op)
        {
            string failureReason = op();
            _craftResultLabel.text = string.IsNullOrEmpty(failureReason) ? string.Empty : $"操作失败[{failureReason}]。";
            RefreshAll();
        }

        private void SetPanelOpen(bool open)
        {
            GameRoot.HomeValley?.SetCraftStationPanelOpen(open);
            RefreshAll();
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool regionActive = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            _entryToggleButton.parent.EnableInClassList("craft-hidden", !regionActive);
            if (!regionActive)
            {
                _panel.EnableInClassList("craft-hidden", true);
                return;
            }

            bool open = GameRoot.HomeValley.IsCraftStationPanelOpen;
            _panel.EnableInClassList("craft-hidden", !open);
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
            RefreshAll();
        }

        private void RefreshAll()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;

            int bagCount = PrimitiveInventory.BagCount(state);
            _bagCapacityLabel.text = $"仓 {bagCount}/{PrimitiveInventory.Capacity}";

            // 升级材料下拉：只列 CardDefId==DefaultChipContentId（聚焦镜）且未被预留的仓内实例——
            // TryEnqueueUpgrade 本身也会校验内容/预留，这里预先过滤是"合法目标"UI 要求的具体落点。
            _upgradeChoicePartIds.Clear();
            var upgradeChoices = new List<string>();
            foreach (PrimitiveChipRecord item in PrimitiveInventory.BagItems(state))
            {
                if (item.CardDefId != PrimitiveInventory.DefaultChipContentId || !string.IsNullOrEmpty(item.ReservedByTransactionId))
                {
                    continue;
                }
                upgradeChoices.Add(ShortLabel(item));
                _upgradeChoicePartIds.Add(item.PartId);
            }
            DropdownChoices.Apply(_upgradeMaterialADropdown, upgradeChoices, "仓内没有可用的聚焦镜");
            DropdownChoices.Apply(_upgradeMaterialBDropdown, new List<string>(upgradeChoices), "仓内没有可用的聚焦镜");
            // 两个材料默认不同：同一实例填两次必然被 TryEnqueueUpgrade 拒绝，默认值应当是一组能直接排入的合法组合。
            if (upgradeChoices.Count >= 2 && _upgradeMaterialBDropdown.index == _upgradeMaterialADropdown.index)
            {
                _upgradeMaterialBDropdown.SetValueWithoutNotify(
                    _upgradeMaterialBDropdown.choices[_upgradeMaterialADropdown.index == 0 ? 1 : 0]);
            }
            _enqueueUpgradeButton.SetEnabled(upgradeChoices.Count >= 2);

            // 拆解材料下拉：任意未被预留的仓内实例。
            _disassembleChoicePartIds.Clear();
            var disassembleChoices = new List<string>();
            foreach (PrimitiveChipRecord item in PrimitiveInventory.BagItems(state))
            {
                if (!string.IsNullOrEmpty(item.ReservedByTransactionId))
                {
                    continue;
                }
                disassembleChoices.Add(ShortLabel(item));
                _disassembleChoicePartIds.Add(item.PartId);
            }
            DropdownChoices.Apply(_disassembleMaterialDropdown, disassembleChoices, "仓内没有可拆解的芯片");
            _enqueueDisassembleButton.SetEnabled(disassembleChoices.Count > 0);

            // 队列展示 + 可取消项下拉。
            CraftQueueItemRecord[] queues = state?.CraftQueues ?? System.Array.Empty<CraftQueueItemRecord>();
            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = _queueRowPool[i];
                if (i >= queues.Length)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                CraftQueueItemRecord q = queues[i];
                row.Q<Label>("Kind").text = q.Kind == CraftQueueKind.Upgrade ? "升级" : "拆解";
                // ER8-CONTENT-01：行首图标＝升级产物 / 被拆解的芯片；原因列此前直接显示原因码。
                ContentIcons.Apply(row.Q<VisualElement>("Icon"), q.Kind == CraftQueueKind.Upgrade
                    ? PrimitiveCraftStation.UpgradeOutputContentId
                    : MaterialChipContentId(state, q));
                row.Q<Label>("State").text = DescribeState(q.State);
                row.Q<Label>("Progress").text = $"{q.Progress:F1}/{q.Duration:F1}s";
                row.Q<Label>("Reason").text = QueueText.Reason(q.BlockedReason);
            }

            _cancelChoiceQueueIds.Clear();
            var cancelChoices = new List<string>();
            foreach (CraftQueueItemRecord q in queues)
            {
                if (q.State != CraftQueueState.Queued && q.State != CraftQueueState.WaitingResources
                    && q.State != CraftQueueState.WaitingPower && q.State != CraftQueueState.Running)
                {
                    continue;
                }
                cancelChoices.Add($"{(q.Kind == CraftQueueKind.Upgrade ? "升级" : "拆解")} · {DescribeState(q.State)} #{DropdownChoices.ShortId(q.QueueItemId)}");
                _cancelChoiceQueueIds.Add(q.QueueItemId);
            }
            DropdownChoices.Apply(_cancelQueueDropdown, cancelChoices, "没有可取消的任务");
            _cancelQueueButton.SetEnabled(cancelChoices.Count > 0);

            // 待领取产物下拉（全局 Pending 池，与电路板面板共享同一份数据源）。
            _pendingChoicePartIds.Clear();
            var pendingChoices = new List<string>();
            foreach (PrimitiveChipRecord item in PrimitiveInventory.PendingItems(state))
            {
                pendingChoices.Add(ShortLabel(item));
                _pendingChoicePartIds.Add(item.PartId);
            }
            DropdownChoices.Apply(_pendingOutputDropdown, pendingChoices, "没有待领取的产物");
            _claimOutputButton.SetEnabled(pendingChoices.Count > 0);
        }

        private static string ShortLabel(PrimitiveChipRecord item)
        {
            string source = string.IsNullOrEmpty(item.SourceSalvageId) ? "补印/合成" : "解析";
            string reserved = string.IsNullOrEmpty(item.ReservedByTransactionId) ? string.Empty : " · 已预留";
            return $"{BlueprintCircuitChipCatalog.DisplayNameFor(item.CardDefId)}（{source} #{DropdownChoices.ShortId(item.PartId)}{reserved}）";
        }

        private static string MaterialChipContentId(CampaignState state, CraftQueueItemRecord q)
        {
            string partId = q.MaterialPartIds != null && q.MaterialPartIds.Length > 0 ? q.MaterialPartIds[0] : null;
            if (string.IsNullOrEmpty(partId) || state?.PrimitiveChips == null)
            {
                return null;
            }
            foreach (PrimitiveChipRecord chip in state.PrimitiveChips)
            {
                if (chip != null && chip.PartId == partId)
                {
                    return chip.CardDefId;
                }
            }
            return null;
        }

        private static string DescribeState(CraftQueueState state)
        {
            switch (state)
            {
                case CraftQueueState.Queued: return "排队中";
                case CraftQueueState.WaitingResources: return "缺材料";
                case CraftQueueState.WaitingPower: return "缺电力";
                case CraftQueueState.Running: return "进行中";
                case CraftQueueState.Committing: return "结算中";
                case CraftQueueState.OutputWaiting: return "待领取";
                case CraftQueueState.Completed: return "已完成";
                case CraftQueueState.Cancelled: return "已取消";
                case CraftQueueState.Failed: return "失败";
                default: return state.ToString();
            }
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_queueRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_queueRowTemplate);
                _queueRowTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
