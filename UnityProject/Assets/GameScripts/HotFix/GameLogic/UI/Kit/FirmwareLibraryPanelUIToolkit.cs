using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-05（FG02 FGR-FW-060、061；FG13 FGU-20）：固件库面板——所有存放位置里固件芯片的统一视图。
    /// - 工具条：6 个筛选（类别 / 种类 / 协议 / 破解 / 兼容载体 / 稀有度）、排序、搜索（名称与说明）；
    /// - 左栏：虚拟化列表（每行一枚芯片：勾选、图标、名称、类别与位置、锁定标记），选择工具条（全选当前列表 / 清空 / 已选数 / 分解所选）；
    /// - 右栏：详情（<see cref="FirmwareLibrary.BuildDetail"/>：各载体读法、标签、参与的反应、获取途径、持有数量与位置、这一枚的来源）、
    ///   取用路线（仓储 → 装配站，被堵时写明被什么堵住）、操作（锁定 / 领取 / 查看图鉴 / 设为比较 A、B）、两条固件并排比较；
    /// - 批量分解：先弹确认框（写明件数、返还废料、跳过的锁定 / 在用件数、无法再刻印的件数），确认后才执行；锁定的一律跳过；
    /// - 悬停列表行：提示带图鉴链接，按图鉴键跳到该固件的图鉴条目（<see cref="CodexHoverLink"/>）；
    /// - 入口：固件库键（默认 I，开着再按一次关闭）、暂停菜单“固件库”。Esc / 关闭按钮 / 点遮罩关闭；模态（输入上下文 = 界面）。
    /// - 分层 30072：暂停菜单 30070 之上、图鉴 30075 之下（在固件库里点“查看图鉴”，图鉴盖在上面，关掉回到固件库）。
    /// 刷新：数据版本 / 筛选 / 选择 / 语言 / 键位变化时立即重建；另外按 firmware.library.refresh_seconds（真实秒）重读一次（仓库容量、路线）。
    /// 每次重建 O(芯片数)，列表只为可见行建元素；不按帧遍历芯片。
    /// </summary>
    public sealed class FirmwareLibraryPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30072;

        public static FirmwareLibraryPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        /// <summary>真实时间来源（定时重读；自检可注入）。</summary>
        public static Func<double> Clock = () => UnityEngine.Time.realtimeSinceStartupAsDouble;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Label _warn;
        private Label _error;
        private DropdownField _fCategory;
        private DropdownField _fKind;
        private DropdownField _fProtocol;
        private DropdownField _fCracked;
        private DropdownField _fCarrier;
        private DropdownField _fRarity;
        private DropdownField _fSort;
        private UiSearchBox _search;
        private Button _selectAll;
        private Button _selectNone;
        private Label _selected;
        private Button _disassemble;
        private Label _feedback;
        private Label _empty;
        private UiVirtualList _list;
        private VisualElement _detailIcon;
        private Label _detailTitle;
        private Label _detailBody;
        private Label _route;
        private VisualElement _actions;
        private Button _lock;
        private Button _claim;
        private Button _codex;
        private Button _compareA;
        private Button _compareB;
        private Label _compareTitle;
        private Button _compareClear;
        private Label _compareEmpty;
        private VisualElement _compare;
        private Label _footer;

        private readonly FirmwareLibraryFilter _filter = new FirmwareLibraryFilter();
        private readonly List<FirmwareLibraryRow> _rows = new List<FirmwareLibraryRow>();
        private readonly HashSet<string> _picked = new HashSet<string>(StringComparer.Ordinal);
        private string _selectedPartId;
        private string _compareIdA;
        private string _compareIdB;
        private string _feedbackText = string.Empty;
        private int _pickRevision;
        private int _key;
        private int _labelsKey;
        private int _total;
        private double _nextReadAt;

        protected override string UxmlLocation => "FirmwareLibraryPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public FirmwareLibraryFilter Filter => _filter;
        public int RowCount => _rows.Count;
        public int TotalCount => _total;
        public FirmwareLibraryRow Row(int i) => i >= 0 && i < _rows.Count ? _rows[i] : null;
        public string RowText(int i) => i >= 0 && i < _rows.Count ? DescribeRow(CampaignSession.Current, _rows[i]) : string.Empty;
        public string SelectedPartId => _selectedPartId;
        public int PickedCount => _picked.Count;
        public bool IsPicked(string partId) => partId != null && _picked.Contains(partId);
        public string CountText => _count?.text ?? string.Empty;
        public string WarnText => _warn != null && !_warn.ClassListContains("uk-hidden") ? _warn.text : string.Empty;
        public string ErrorText => _error != null && !_error.ClassListContains("uk-hidden") ? _error.text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string DetailTitleText => _detailTitle?.text ?? string.Empty;
        public string DetailBodyText => _detailBody?.text ?? string.Empty;
        public string RouteText => _route?.text ?? string.Empty;
        public bool RouteBlocked => _route != null && _route.ClassListContains("fl-route-blocked");
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string SelectedText => _selected?.text ?? string.Empty;
        public string CompareEmptyText => _compareEmpty != null && !_compareEmpty.ClassListContains("uk-hidden") ? _compareEmpty.text : string.Empty;
        public int CompareRowCount => _compare?.childCount ?? 0;
        public string CompareRowText(int i)
        {
            if (_compare == null || i < 0 || i >= _compare.childCount)
            {
                return string.Empty;
            }
            var parts = new List<string>();
            foreach (VisualElement c in _compare[i].Children())
            {
                if (c is Label l)
                {
                    parts.Add(l.text);
                }
            }
            return string.Join(" | ", parts);
        }
        public bool CompareRowDiff(int i) => _compare != null && i >= 0 && i < _compare.childCount && _compare[i].Q<Label>(className: "fl-compare-diff") != null;
        public Button CloseButton => _close;
        public Button DisassembleButton => _disassemble;
        public Button SelectAllButton => _selectAll;
        public Button SelectNoneButton => _selectNone;
        public Button LockButton => _lock;
        public Button ClaimButton => _claim;
        public Button CodexButton => _codex;
        public Button CompareAButton => _compareA;
        public Button CompareBButton => _compareB;
        public Button CompareClearButton => _compareClear;
        public bool ClaimVisible => _claim != null && !_claim.ClassListContains("uk-hidden");
        public bool ActionsVisible => _actions != null && !_actions.ClassListContains("uk-hidden");
        public DropdownField CategoryDropdown => _fCategory;
        public DropdownField KindDropdown => _fKind;
        public DropdownField ProtocolDropdown => _fProtocol;
        public DropdownField CrackedDropdown => _fCracked;
        public DropdownField CarrierDropdown => _fCarrier;
        public DropdownField RarityDropdown => _fRarity;
        public DropdownField SortDropdown => _fSort;
        public ListView ListView => _list?.View;
        public string DetailIconId => _detailIcon?.userData as string;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        /// <summary>固件库键：开着就关，关着就打开。</summary>
        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        /// <summary>绑定 UXML（运行时与自检共用）。</summary>
        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("FwLibRoot");
            _title = root.Q<Label>("FwLibTitle");
            _count = root.Q<Label>("FwLibCount");
            _close = root.Q<Button>("FwLibClose");
            _warn = root.Q<Label>("FwLibWarn");
            _error = root.Q<Label>("FwLibError");
            _fCategory = root.Q<DropdownField>("FwLibFilterCategory");
            _fKind = root.Q<DropdownField>("FwLibFilterKind");
            _fProtocol = root.Q<DropdownField>("FwLibFilterProtocol");
            _fCracked = root.Q<DropdownField>("FwLibFilterCracked");
            _fCarrier = root.Q<DropdownField>("FwLibFilterCarrier");
            _fRarity = root.Q<DropdownField>("FwLibFilterRarity");
            _fSort = root.Q<DropdownField>("FwLibSort");
            _selectAll = root.Q<Button>("FwLibSelectAll");
            _selectNone = root.Q<Button>("FwLibSelectNone");
            _selected = root.Q<Label>("FwLibSelected");
            _disassemble = root.Q<Button>("FwLibDisassemble");
            _feedback = root.Q<Label>("FwLibFeedback");
            _empty = root.Q<Label>("FwLibEmpty");
            _detailIcon = root.Q<VisualElement>("FwLibDetailIcon");
            _detailTitle = root.Q<Label>("FwLibDetailTitle");
            _detailBody = root.Q<Label>("FwLibDetailBody");
            _route = root.Q<Label>("FwLibRoute");
            _actions = root.Q<VisualElement>("FwLibActions");
            _lock = root.Q<Button>("FwLibLock");
            _claim = root.Q<Button>("FwLibClaim");
            _codex = root.Q<Button>("FwLibCodex");
            _compareA = root.Q<Button>("FwLibCompareA");
            _compareB = root.Q<Button>("FwLibCompareB");
            _compareTitle = root.Q<Label>("FwLibCompareTitle");
            _compareClear = root.Q<Button>("FwLibCompareClear");
            _compareEmpty = root.Q<Label>("FwLibCompareEmpty");
            _compare = root.Q<VisualElement>("FwLibCompare");
            _footer = root.Q<Label>("FwLibFooter");

            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false); // 点遮罩 = 关闭（与暂停菜单一致）。
                }
            });
            HookDropdown(_fCategory, i => _filter.Category = i <= 0 ? (FirmwareCategory?)null : CategoryOptions[i - 1]);
            HookDropdown(_fKind, i => _filter.Kind = i <= 0 ? (FirmwareKind?)null : i == 1 ? FirmwareKind.Regular : FirmwareKind.Core);
            HookDropdown(_fProtocol, i => _filter.EnemyProtocol = i <= 0 ? (bool?)null : i == 2);
            HookDropdown(_fCracked, i => _filter.Cracked = i <= 0 ? (bool?)null : i == 1);
            HookDropdown(_fCarrier, i => _filter.Carrier = i <= 0 ? (FirmwareCarrier?)null : CarrierReadings.AllCarriers[i - 1]);
            HookDropdown(_fRarity, i => _filter.Rarity = i <= 0 ? null : RarityOptions[i - 1]);
            HookDropdown(_fSort, i => _filter.Sort = (FirmwareLibrarySort)Math.Max(0, i));
            _search = new UiSearchBox(root.Q<TextField>("FwLibSearch"), root.Q<Label>("FwLibSearchPlaceholder"), root.Q<Button>("FwLibSearchClear"),
                "fwlib.search", SetSearch);
            _list = new UiVirtualList(root.Q<ListView>("FwLibList"), _empty, MakeRow, BindRow);
            _list.View.selectedIndicesChanged += indices =>
            {
                foreach (int i in indices)
                {
                    if (i >= 0 && i < _rows.Count)
                    {
                        Select(_rows[i].Chip.PartId);
                    }
                    break;
                }
            };
            _selectAll.clicked += PickAllShown;
            _selectNone.clicked += ClearPicks;
            _disassemble.clicked += RequestDisassemble;
            _lock.clicked += ToggleLockSelected;
            _claim.clicked += ClaimSelected;
            _codex.clicked += OpenCodexForSelected;
            _compareA.clicked += () => SetCompare(true);
            _compareB.clicked += () => SetCompare(false);
            _compareClear.clicked += ClearCompare;
            UiTooltip.Attach(_disassemble, () => new TooltipContent
            {
                Title = GameText.Format("fwlib.disassemble", _picked.Count),
                Body = GameText.Format("fwlib.disassemble_tip", FirmwareLibrary.DisassembleScrap),
            });
            UiTooltip.Attach(_lock, () => new TooltipContent { Title = _lock.text, Body = GameText.Get("fwlib.lock_tip") });
            _key = 0;
            _labelsKey = 0;
        }

        private static readonly FirmwareCategory[] CategoryOptions =
            { FirmwareCategory.Fuse, FirmwareCategory.Limiter, FirmwareCategory.Fluid, FirmwareCategory.Electromagnetic };

        private static readonly string[] RarityOptions = { "common", "rare", "epic" };

        /// <summary>类别枚举 → 文本键段（与 fg.TbFirmwareKind category 列一致）。</summary>
        public static string CategoryKey(FirmwareCategory c) => c switch
        {
            FirmwareCategory.Fuse => "fuse",
            FirmwareCategory.Limiter => "limiter",
            FirmwareCategory.Fluid => "fluid",
            FirmwareCategory.Electromagnetic => "em",
            _ => "unknown",
        };

        /// <summary>类别的显示文本键（写全键名，文本键扫描能核对到）。</summary>
        public static string CategoryTextKey(FirmwareCategory c) => c switch
        {
            FirmwareCategory.Fuse => "firmware.category.fuse",
            FirmwareCategory.Limiter => "firmware.category.limiter",
            FirmwareCategory.Fluid => "firmware.category.fluid",
            FirmwareCategory.Electromagnetic => "firmware.category.em",
            _ => "fwlib.any",
        };

        /// <summary>稀有度的显示文本键。</summary>
        public static string RarityTextKey(string rarity) => rarity switch
        {
            "common" => "firmware.rarity.common",
            "rare" => "firmware.rarity.rare",
            "epic" => "firmware.rarity.epic",
            _ => "fwlib.any",
        };

        private void HookDropdown(DropdownField d, Action<int> apply)
        {
            d?.RegisterValueChangedCallback(_ =>
            {
                apply(d.index);
                _key = 0;
                Refresh();
            });
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                // FG00 B14：新系统第一次出现时发引导钩子（引导内容在 FG15-UX-04）；无论第几次都会解锁图鉴“固件库”系统说明（MechanicCodex.OnHook）。
                GuidanceHooks.Raise(GuidanceHooks.FirmwareLibraryFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                FirmwareLibrary.SyncCodex(CampaignSession.Current);
                _feedbackText = string.Empty;
                _key = 0;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                UiTooltip.Hide();
                // 勾选只在这一次打开里有效：关掉再开不会带着上次（可能已被筛掉、看不见）的勾选去批量分解。
                if (_picked.Count > 0)
                {
                    _picked.Clear();
                    _pickRevision++;
                }
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            // 固件库属于游戏世界里的界面：世界没了（回主菜单）就收起，不带着模态残留。
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Tick(Clock());
        }

        /// <summary>定时重读（仓库容量、取用路线会随建筑变化，不在芯片版本号里）；其余变化靠版本号立即重建。</summary>
        public void Tick(double now)
        {
            if (now >= _nextReadAt)
            {
                _nextReadAt = now + FirmwareLibrary.RefreshSeconds;
                _key = 0;
            }
            Refresh();
        }

        // ── 操作 ────────────────────────────────────────────────────────────────

        public void SetSearch(string text)
        {
            _filter.Search = text ?? string.Empty;
            _key = 0;
            Refresh();
        }

        /// <summary>自检 / 代码设置搜索框文字（与玩家输入同一路径）。</summary>
        public void SetSearchText(string text) => _search?.SetText(text);

        public void Select(string partId)
        {
            if (partId == _selectedPartId)
            {
                return;
            }
            _selectedPartId = partId;
            _key = 0;
            Refresh();
        }

        public void TogglePick(string partId)
        {
            if (string.IsNullOrEmpty(partId))
            {
                return;
            }
            if (!_picked.Remove(partId))
            {
                _picked.Add(partId);
            }
            _pickRevision++;
            _feedbackText = string.Empty;
            _key = 0;
            Refresh();
        }

        public void PickAllShown()
        {
            foreach (FirmwareLibraryRow r in _rows)
            {
                _picked.Add(r.Chip.PartId);
            }
            _pickRevision++;
            _feedbackText = string.Empty;
            _key = 0;
            Refresh();
        }

        public void ClearPicks()
        {
            _picked.Clear();
            _pickRevision++;
            _key = 0;
            Refresh();
        }

        /// <summary>“分解所选”：先按此刻状态算计划并弹确认框，确认后才执行（FGR-FW-061“需要确认，锁定的跳过”）。没有可分解的不弹框，直接说明原因。</summary>
        public void RequestDisassemble()
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || _picked.Count == 0)
            {
                return;
            }
            var ids = new List<string>(_picked);
            FirmwareDisassemblePlan plan = FirmwareLibrary.PlanDisassemble(s, ids);
            if (plan.Eligible.Count == 0)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied); // FG00 B07 / B17：拒绝音 + 文字原因
                SetFeedback(GameText.Get("fwlib.result.nothing"));
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Get("fwlib.confirm.title"),
                Irreversible = true,
                ConfirmText = GameText.Get("fwlib.confirm.ok"),
                CancelText = GameText.Get("fwlib.confirm.cancel"),
            };
            req.Consequences.Add(GameText.Format("fwlib.confirm.lose", plan.Eligible.Count, plan.Scrap));
            // 不只写件数：按固件名汇总列出要拆的是什么；勾选里有当前筛选之外（看不见）的，单独写明，防误拆。
            req.Lines.Add(GameText.Format("fwlib.confirm.names", SummarizeNames(s, plan.Eligible)));
            int hidden = HiddenCount(plan.Eligible);
            if (hidden > 0)
            {
                req.Lines.Add(GameText.Format("fwlib.confirm.hidden", hidden));
            }
            if (plan.Unprintable > 0)
            {
                req.Consequences.Add(GameText.Format("fwlib.confirm.unprintable", plan.Unprintable));
            }
            if (plan.SkippedLocked > 0)
            {
                req.Lines.Add(GameText.Format("fwlib.confirm.skip_locked", plan.SkippedLocked));
            }
            if (plan.SkippedBusy > 0)
            {
                req.Lines.Add(GameText.Format("fwlib.confirm.skip_busy", plan.SkippedBusy));
            }
            req.OnConfirm = () =>
            {
                CampaignState now = CampaignSession.Current;
                if (now == null)
                {
                    return;
                }
                // 确认时按此刻状态重新计算（确认框开着期间被锁定 / 装进信号核的照样跳过）。
                FirmwareDisassemblePlan done = FirmwareLibrary.Disassemble(now, ids);
                foreach (string id in done.Eligible)
                {
                    _picked.Remove(id);
                    if (id == _selectedPartId)
                    {
                        _selectedPartId = null;
                    }
                }
                _pickRevision++;
                Campaign.Feedback.FeedbackCues.Raise(done.Eligible.Count == 0 ? Campaign.Feedback.FeedbackCueId.Denied : Campaign.Feedback.FeedbackCueId.CommandAck);
                SetFeedback(done.Eligible.Count == 0
                    ? GameText.Get("fwlib.result.nothing")
                    : GameText.Format("fwlib.result.done", done.Eligible.Count, done.Scrap, done.Skipped));
            };
            UiConfirmDialog.Show(req);
        }

        /// <summary>确认框里最多按名称列出几种固件（其余写“等 N 种”）。</summary>
        public const int ConfirmNameGroups = 4;

        /// <summary>“过载 ×3、冰封 ×1……”：按件数从多到少、同数按名称；超过 <see cref="ConfirmNameGroups"/> 种时末尾写“等 N 种”。只在点“分解所选”时算一次。</summary>
        public static string SummarizeNames(CampaignState s, IReadOnlyList<string> partIds)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in partIds)
            {
                PrimitiveChipRecord chip = PrimitiveInventory.Find(s, id);
                string name = chip == null ? id : FirmwareKinds.DisplayName(chip.CardDefId) ?? chip.CardDefId;
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
            var groups = new List<KeyValuePair<string, int>>(counts);
            groups.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            var parts = new List<string>(Math.Min(groups.Count, ConfirmNameGroups) + 1);
            for (int i = 0; i < groups.Count && i < ConfirmNameGroups; i++)
            {
                parts.Add(GameText.Format("fwlib.confirm.name_count", groups[i].Key, groups[i].Value));
            }
            if (groups.Count > ConfirmNameGroups)
            {
                parts.Add(GameText.Format("fwlib.confirm.name_more", groups.Count - ConfirmNameGroups));
            }
            return string.Join(FirmwareLibrary.Sep, parts);
        }

        /// <summary>要拆的实例里有多少不在当前列表（被筛选 / 搜索挡住、看不见）。</summary>
        private int HiddenCount(IReadOnlyList<string> partIds)
        {
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (FirmwareLibraryRow r in _rows)
            {
                shown.Add(r.Chip.PartId);
            }
            int n = 0;
            foreach (string id in partIds)
            {
                if (!shown.Contains(id))
                {
                    n++;
                }
            }
            return n;
        }

        public void ToggleLockSelected()
        {
            CampaignState s = CampaignSession.Current;
            PrimitiveChipRecord chip = PrimitiveInventory.Find(s, _selectedPartId);
            if (chip == null)
            {
                return;
            }
            FirmwareLibrary.TrySetLocked(s, chip.PartId, !chip.Locked);
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.UiClick);
            _key = 0;
            Refresh();
        }

        public void ClaimSelected()
        {
            CampaignState s = CampaignSession.Current;
            PrimitiveChipRecord chip = PrimitiveInventory.Find(s, _selectedPartId);
            if (chip == null || chip.State != PrimitiveChipState.Pending)
            {
                return;
            }
            Campaign.Blueprint.CircuitOpResult r = PrimitiveInventory.TryClaimPending(s, chip.PartId);
            if (!r.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                SetFeedback(GameText.Get("signal.reason.bag_full"));
            }
            _key = 0;
            Refresh();
        }

        public void OpenCodexForSelected()
        {
            PrimitiveChipRecord chip = PrimitiveInventory.Find(CampaignSession.Current, _selectedPartId);
            if (chip != null)
            {
                MechanicCodex.Open(MechanicCodex.FirmwareEntryId(chip.CardDefId), unlock: false);
            }
        }

        public void SetCompare(bool slotA)
        {
            PrimitiveChipRecord chip = PrimitiveInventory.Find(CampaignSession.Current, _selectedPartId);
            if (chip == null)
            {
                return;
            }
            if (slotA)
            {
                _compareIdA = chip.CardDefId;
            }
            else
            {
                _compareIdB = chip.CardDefId;
            }
            _key = 0;
            Refresh();
        }

        public void ClearCompare()
        {
            _compareIdA = null;
            _compareIdB = null;
            _key = 0;
            Refresh();
        }

        private void SetFeedback(string text)
        {
            _feedbackText = text ?? string.Empty;
            _key = 0;
            Refresh();
        }

        // ── 刷新 ────────────────────────────────────────────────────────────────

        /// <summary>按数据版本 / 筛选 / 选择 / 语言 / 键位重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int filterKey = HashCode.Combine(_filter.Category, _filter.Kind, _filter.EnemyProtocol, _filter.Cracked, _filter.Carrier, _filter.Rarity, _filter.Search, (int)_filter.Sort);
            int key = HashCode.Combine(FirmwareLibrary.Revision, filterKey, _selectedPartId, _pickRevision, HashCode.Combine(_compareIdA, _compareIdB, _feedbackText),
                (int)GameText.Language, GameSettings.Revision, HashCode.Combine(MechanicCodex.Revision, s != null ? s.GetHashCode() : 0));
            if (key == _key)
            {
                return;
            }
            _key = key;
            RefreshLabels();

            string loadError = FirmwareKinds.LoadError;
            _error.EnableInClassList("uk-hidden", loadError == null);
            _error.text = loadError == null ? string.Empty : GameText.Format("fwlib.error", loadError);

            _total = FirmwareLibrary.Query(s, _filter, _rows);
            // 选择只保留仍然存在的芯片（分解 / 消耗后自动去掉）。
            if (_picked.Count > 0)
            {
                var alive = new HashSet<string>(StringComparer.Ordinal);
                foreach (PrimitiveChipRecord p in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                {
                    if (p != null)
                    {
                        alive.Add(p.PartId);
                    }
                }
                _picked.RemoveWhere(id => !alive.Contains(id));
            }
            if (_selectedPartId != null && _rows.Find(r => r.Chip.PartId == _selectedPartId) == null)
            {
                _selectedPartId = null;
            }
            if (_selectedPartId == null && _rows.Count > 0)
            {
                _selectedPartId = _rows[0].Chip.PartId;
            }

            int bag = PrimitiveInventory.BagCount(s);
            int cap = PrimitiveInventory.CapacityOf(s);
            _count.text = GameText.Format("fwlib.count", _total, _rows.Count, bag, cap);
            bool full = s != null && bag >= cap;
            _warn.EnableInClassList("uk-hidden", !full);
            _warn.text = full ? GameText.Get("fwlib.over_capacity") : string.Empty;

            _list.SetItems(_rows, GameText.Get(_total == 0 ? "fwlib.empty" : "fwlib.empty_filtered"));
            int selIndex = _rows.FindIndex(r => r.Chip.PartId == _selectedPartId);
            if (selIndex >= 0)
            {
                _list.View.SetSelectionWithoutNotify(new[] { selIndex });
            }
            else
            {
                _list.View.ClearSelection();
            }

            _selected.text = GameText.Format("fwlib.selected", _picked.Count);
            _disassemble.text = GameText.Format("fwlib.disassemble", _picked.Count);
            _disassemble.SetEnabled(_picked.Count > 0);
            _feedback.text = _feedbackText;
            _feedback.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_feedbackText));

            RefreshDetail(s);
            RefreshCompare();
        }

        /// <summary>页眉、工具条、下拉选项、脚注的文字（语言 / 键位变化时重写；下拉只重建选项，不改当前选择）。</summary>
        private void RefreshLabels()
        {
            int labelsKey = HashCode.Combine((int)GameText.Language, GameSettings.Revision);
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("fwlib.footer"));
            if (labelsKey == _labelsKey)
            {
                return;
            }
            _labelsKey = labelsKey;
            _title.text = GameText.Get("fwlib.title");
            _close.text = GameText.Get("fwlib.close");
            _selectAll.text = GameText.Get("fwlib.select_all");
            _selectNone.text = GameText.Get("fwlib.select_none");
            _codex.text = GameText.Get("fwlib.codex");
            _compareA.text = GameText.Get("fwlib.compare_a");
            _compareB.text = GameText.Get("fwlib.compare_b");
            _compareTitle.text = GameText.Get("fwlib.compare.title");
            _compareClear.text = GameText.Get("fwlib.compare.clear");
            _claim.text = GameText.Get("fwlib.claim");
            string any = GameText.Get("fwlib.any");
            var cat = new List<string> { any };
            foreach (FirmwareCategory c in CategoryOptions)
            {
                cat.Add(GameText.Get(CategoryTextKey(c)));
            }
            SetChoices(_fCategory, "fwlib.filter.category", cat);
            SetChoices(_fKind, "fwlib.filter.kind", new List<string> { any, GameText.Get("fwlib.kind.regular"), GameText.Get("fwlib.kind.core") });
            SetChoices(_fProtocol, "fwlib.filter.protocol", new List<string> { any, GameText.Get("fwlib.protocol.own"), GameText.Get("firmware.protocol.enemy") });
            SetChoices(_fCracked, "fwlib.filter.cracked", new List<string> { any, GameText.Get("fwlib.cracked.yes"), GameText.Get("fwlib.cracked.no") });
            var car = new List<string> { any };
            foreach (FirmwareCarrier c in CarrierReadings.AllCarriers)
            {
                car.Add(CarrierReadings.CarrierName(c));
            }
            SetChoices(_fCarrier, "fwlib.filter.carrier", car);
            var rar = new List<string> { any };
            foreach (string r in RarityOptions)
            {
                rar.Add(GameText.Get(RarityTextKey(r)));
            }
            SetChoices(_fRarity, "fwlib.filter.rarity", rar);
            SetChoices(_fSort, "fwlib.sort", new List<string>
            {
                GameText.Get("fwlib.sort.name"), GameText.Get("fwlib.sort.category"), GameText.Get("fwlib.sort.rarity"),
                GameText.Get("fwlib.sort.load"), GameText.Get("fwlib.sort.newest"), GameText.Get("fwlib.sort.location"),
            });
        }

        private static void SetChoices(DropdownField d, string labelKey, List<string> choices)
        {
            if (d == null)
            {
                return;
            }
            // 换语言时选项文字全变：按序号保持当前选择（DropdownChoices 负责去掉“/”“#”等菜单语法并保证选项唯一）。
            int index = Math.Max(0, d.index);
            d.label = GameText.Get(labelKey);
            DropdownChoices.Apply(d, choices, GameText.Get("fwlib.any"));
            if (d.choices.Count > 0)
            {
                d.SetValueWithoutNotify(d.choices[Math.Min(index, d.choices.Count - 1)]);
            }
        }

        private void RefreshDetail(CampaignState s)
        {
            FirmwareLibraryRow row = _rows.Find(r => r.Chip.PartId == _selectedPartId);
            _actions.EnableInClassList("uk-hidden", row == null);
            _route.EnableInClassList("uk-hidden", row == null);
            if (row == null)
            {
                ContentIcons.ApplyIcon(_detailIcon, null);
                _detailIcon.EnableInClassList("uk-hidden", true);
                _detailTitle.text = GameText.Get("fwlib.detail.none");
                _detailBody.text = string.Empty;
                _route.text = string.Empty;
                return;
            }
            _detailIcon.EnableInClassList("uk-hidden", false);
            ContentIcons.ApplyIcon(_detailIcon, FirmwareCatalog.TryGet(row.FirmwareId, out MechanicalContentDef def) ? def.IconId : null);
            _detailTitle.text = row.Chip.Locked ? row.Name + "  " + GameText.Get("fwlib.row.locked") : row.Name;
            _detailBody.text = FirmwareLibrary.BuildDetail(s, row.FirmwareId, row.Chip);
            FirmwareRouteInfo route = FirmwareLibrary.EvaluateRoute(s);
            _route.text = FirmwareLibrary.RouteText(route);
            _route.EnableInClassList("fl-route-blocked", !route.Ok && route.Reach != Campaign.Nav.NavService.BuildingReach.Unknown || route.NoStation);
            _lock.text = GameText.Get(row.Chip.Locked ? "fwlib.unlock" : "fwlib.lock");
            _claim.EnableInClassList("uk-hidden", row.Location != FirmwareLocationKind.Pending);
        }

        private void RefreshCompare()
        {
            _compare.Clear();
            bool both = !string.IsNullOrEmpty(_compareIdA) && !string.IsNullOrEmpty(_compareIdB);
            _compareEmpty.EnableInClassList("uk-hidden", both);
            _compareEmpty.text = both ? string.Empty : GameText.Get("fwlib.compare.empty");
            _compareClear.SetEnabled(!string.IsNullOrEmpty(_compareIdA) || !string.IsNullOrEmpty(_compareIdB));
            if (!both)
            {
                return;
            }
            foreach ((string label, string a, string b) in FirmwareLibrary.CompareFields(_compareIdA, _compareIdB))
            {
                var line = new VisualElement();
                line.AddToClassList("fl-compare-row");
                bool diff = !string.Equals(a, b, StringComparison.Ordinal);
                line.Add(Cell(label, "fl-compare-label", false));
                line.Add(Cell(a, "fl-compare-cell", diff));
                line.Add(Cell(diff ? b : GameText.Get("fwlib.compare.same"), "fl-compare-cell", diff));
                _compare.Add(line);
            }
        }

        private static Label Cell(string text, string cls, bool diff)
        {
            var l = new Label(text ?? string.Empty);
            l.AddToClassList(cls);
            if (diff)
            {
                l.AddToClassList("fl-compare-diff");
            }
            return l;
        }

        // ── 列表行 ──────────────────────────────────────────────────────────────

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("fl-row");
            var pick = new Button { name = "FwLibRowPick" };
            pick.AddToClassList("mw-btn");
            pick.AddToClassList("fl-row-pick");
            pick.clicked += () =>
            {
                if (row.userData is int i && i >= 0 && i < _rows.Count)
                {
                    TogglePick(_rows[i].Chip.PartId);
                }
            };
            var icon = new VisualElement { name = "FwLibRowIcon", pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mw-icon");
            icon.AddToClassList("fl-row-icon");
            var name = new Label { name = "FwLibRowName", pickingMode = PickingMode.Ignore };
            name.AddToClassList("fl-row-name");
            var meta = new Label { name = "FwLibRowMeta", pickingMode = PickingMode.Ignore };
            meta.AddToClassList("fl-row-meta");
            var lck = new Label { name = "FwLibRowLock", pickingMode = PickingMode.Ignore };
            lck.AddToClassList("fl-row-lock");
            row.Add(pick);
            row.Add(icon);
            row.Add(name);
            row.Add(meta);
            row.Add(lck);
            // FG2-FW-05（FG02 第 4 章“悬停固件时按一个键打开它的图鉴条目”）：行提示带图鉴链接。
            UiTooltip.Attach(row, () => row.userData is int i && i >= 0 && i < _rows.Count ? RowTooltip(_rows[i]) : null);
            return row;
        }

        private void BindRow(VisualElement row, int i)
        {
            row.userData = i;
            if (i < 0 || i >= _rows.Count)
            {
                return;
            }
            FirmwareLibraryRow r = _rows[i];
            var pick = row.Q<Button>("FwLibRowPick");
            bool picked = _picked.Contains(r.Chip.PartId);
            pick.text = GameText.Get(picked ? "fwlib.row.picked" : "fwlib.row.pick");
            pick.EnableInClassList("fl-row-picked", picked);
            ContentIcons.ApplyIcon(row.Q<VisualElement>("FwLibRowIcon"), FirmwareCatalog.TryGet(r.FirmwareId, out MechanicalContentDef def) ? def.IconId : null);
            var name = row.Q<Label>("FwLibRowName");
            name.text = RowName(r);
            name.EnableInClassList("fl-row-raw", r.Raw);
            row.Q<Label>("FwLibRowMeta").text = RowMeta(CampaignSession.Current, r);
            row.Q<Label>("FwLibRowLock").text = r.Chip.Locked ? GameText.Get("fwlib.row.locked") : string.Empty;
        }

        private static string RowName(FirmwareLibraryRow r) =>
            (r.Kind == FirmwareKind.Core ? "◆ " : "● ") + r.Name + (r.Raw ? "  " + GameText.Get("signal.core.raw_tag") : string.Empty);

        private static string RowMeta(CampaignState s, FirmwareLibraryRow r) =>
            GameText.Get(CategoryTextKey(r.Category)) + " · " + FirmwareLibrary.LocationText(s, r);

        /// <summary>一行的完整文字（名称 · 类别与位置 · 锁定），自检与读屏用。</summary>
        public static string DescribeRow(CampaignState s, FirmwareLibraryRow r) =>
            RowName(r) + " · " + RowMeta(s, r) + (r.Chip.Locked ? " " + GameText.Get("fwlib.row.locked") : string.Empty);

        private static TooltipContent RowTooltip(FirmwareLibraryRow r) => new TooltipContent
        {
            Title = r.Name,
            Body = FirmwareKinds.KindTip(r.Kind) + "\n" + CarrierReadings.DetailLines(r.FirmwareId),
            CodexEntryId = MechanicCodex.FirmwareEntryId(r.FirmwareId),
        };
    }
}
