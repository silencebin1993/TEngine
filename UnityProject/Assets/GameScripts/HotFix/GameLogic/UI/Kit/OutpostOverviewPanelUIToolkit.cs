using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG8-OUT-01（FG13 FGU-47 前哨站总览；FG08 FGR-EXP-017～019；卡片“前哨站的统计和离家报告分组；前哨站的供电、覆盖是否连通在总览里一目了然”）：前哨站总览。
    /// - 汇总：前哨站数、建筑、有问题的、前哨劳动、统计（建立 / 全毁 / 施工包 / 传送带送进现场）。
    /// - 列表（虚拟化 ListView，B18）：<see cref="OwnOutpostService.CollectViews"/> 每个前哨站一行（建筑、供电是否连通、防御与覆盖是否连通、补给、突袭 / 全毁）；点行选中。
    /// - 详情：选中前哨站的改名 / 恢复默认名、“投送施工包”（<see cref="OwnOutpostService.TryLaunchPack"/>，走投送站正式发射入口）、“定位”（镜头飞过去）、“派驻机器”（打开名册）。
    /// 入口：快捷键（默认 Alt+U，全部上下文）、战略地图“前哨站”按钮、“建立前哨站”通知里写着快捷键。模态；Esc / 关闭 / 点遮罩关闭。只在家园载入时有数据（否则写明原因）。
    /// 开销：打开时每 outpost.overview_refresh_seconds 真实秒刷新一次 O(前哨站的建筑数 + 机器数)；关着时什么都不做。
    /// </summary>
    public sealed class OutpostOverviewPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30059;

        public static OutpostOverviewPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title, _summary, _message, _secList, _empty, _secDetail, _detail, _secAbout, _about, _footer;
        private Button _help, _close, _rename, _resetName, _pack, _locate, _roster;
        private TextField _nameField;
        private ListView _list;
        private VisualElement _nameRow, _actions;

        private readonly List<OutpostView> _views = new List<OutpostView>(8);
        private string _selectedId;
        private int _staticKey;
        private float _nextRefresh;
        private string _lastNameFor;

        protected override string UxmlLocation => "OutpostOverviewPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public int RowsShown => _views.Count;
        public OutpostView Row(int i) => i >= 0 && i < _views.Count ? _views[i] : null;
        public string SelectedId => _selectedId;
        public ListView List => _list;
        public TextField NameField => _nameField;
        public Button RenameButton => _rename;
        public Button ResetNameButton => _resetName;
        public Button PackButton => _pack;
        public Button LocateButton => _locate;
        public Button RosterButton => _roster;
        public Button CloseButton => _close;
        public VisualElement RootElement => _root;
        public int Refreshes { get; private set; }

        private static float RefreshSeconds => Mathf.Max(0.1f, GameClock.TuningOr("outpost.overview_refresh_seconds", 0.5f));

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

        /// <summary>打开并选中某个前哨站（通知 / 地图跳转用）。</summary>
        public static void Open(string outpostId)
        {
            Open();
            if (Instance != null)
            {
                Instance._selectedId = outpostId;
                Instance.Refresh(force: true);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

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

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("OpRoot");
            _title = root.Q<Label>("OpTitle");
            _summary = root.Q<Label>("OpSummary");
            _help = root.Q<Button>("OpHelp");
            _close = root.Q<Button>("OpClose");
            _message = root.Q<Label>("OpMessage");
            _secList = root.Q<Label>("OpSecList");
            _empty = root.Q<Label>("OpEmpty");
            _list = root.Q<ListView>("OpList");
            _secDetail = root.Q<Label>("OpSecDetail");
            _detail = root.Q<Label>("OpDetail");
            _nameRow = root.Q<VisualElement>("OpNameRow");
            _nameField = root.Q<TextField>("OpNameField");
            _rename = root.Q<Button>("OpRename");
            _resetName = root.Q<Button>("OpResetName");
            _actions = root.Q<VisualElement>("OpActions");
            _pack = root.Q<Button>("OpPack");
            _locate = root.Q<Button>("OpLocate");
            _roster = root.Q<Button>("OpRoster");
            _secAbout = root.Q<Label>("OpSecAbout");
            _about = root.Q<Label>("OpAbout");
            _footer = root.Q<Label>("OpFooter");

            _close.clicked += () => SetOpen(false);
            _help.clicked += () => MechanicCodex.Open("codex.expedition.outpost");
            _rename.clicked += () => ClickRename(_nameField.value);
            _resetName.clicked += () => ClickResetName();
            _pack.clicked += () => ClickPack();
            _locate.clicked += () => ClickLocate();
            _roster.clicked += () => ClickRoster();
            UiTooltip.Attach(_pack, () => new TooltipContent { Title = GameText.Get("outpost.btn.pack"), Body = GameText.Get("outpost.btn.pack_tip") });
            UiTooltip.Attach(_roster, () => new TooltipContent { Title = GameText.Get("outpost.btn.roster"), Body = GameText.Get("outpost.btn.roster_tip"), Shortcut = GameActionId.OpenRoster });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _views;
            _staticKey = 0;
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
                GuidanceHooks.Raise(GuidanceHooks.OutpostPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                SetMessage(string.Empty, false);
                _staticKey = 0;
                _lastNameFor = null;
                Refresh(force: true);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh(force: false);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            int key = HashCode.Combine((int)GameText.Language, GameSettings.Revision);
            if (force || key != _staticKey)
            {
                _staticKey = key;
                RebuildStatic();
            }
            float now = Time.unscaledTime;
            if (!force && now < _nextRefresh && Application.isPlaying)
            {
                return;
            }
            _nextRefresh = now + RefreshSeconds;
            Refreshes++;
            CampaignState s = CampaignSession.Current;
            bool homeReady = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded;
            if (s == null || (!homeReady && !InWorldOverrideForTests))
            {
                _views.Clear();
                _list.RefreshItems();
                _summary.text = string.Empty;
                _empty.text = GameText.Get("outpost.error_world");
                SetVisible(_empty, true);
                ShowDetail(null);
                return;
            }
            OwnOutpostService.CollectViews(s, _views);
            _summary.text = OwnOutpostService.SummaryText(s, _views);
            bool none = _views.Count == 0;
            _empty.text = none ? GameText.Format("outpost.empty", Mathf.RoundToInt(GridDistance()), InputDisplay.ForAction(GameActionId.OpenBuildMenu)) : string.Empty;
            SetVisible(_empty, none);
            if (_selectedId == null || _views.FindIndex(v => v.Id == _selectedId) < 0)
            {
                _selectedId = none ? null : _views[0].Id;
            }
            _list.RefreshItems();
            ShowDetail(_views.Find(v => v.Id == _selectedId));
        }

        private static float GridDistance() => Campaign.Grid.GridContent.Tuning("map.own_outpost_distance");

        private void RebuildStatic()
        {
            _title.text = GameText.Get("outpost.title");
            _close.text = GameText.Get("outpost.close");
            _help.text = GameText.Get("prod.panel.help");
            _secList.text = GameText.Get("outpost.sec.list");
            _secDetail.text = GameText.Get("outpost.sec.detail");
            _secAbout.text = GameText.Get("outpost.sec.about");
            _about.text = GameText.Format("outpost.about", Mathf.RoundToInt(GridDistance()), Mathf.RoundToInt(OwnOutpostService.PackRadius));
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("outpost.footer"));
            _nameField.label = GameText.Get("outpost.name.label");
            _rename.text = GameText.Get("outpost.btn.rename");
            _resetName.text = GameText.Get("outpost.btn.reset_name");
            _pack.text = GameText.Get("outpost.btn.pack");
            _locate.text = GameText.Get("outpost.btn.locate");
            _roster.text = GameText.Get("outpost.btn.roster");
        }

        private void ShowDetail(OutpostView v)
        {
            SetVisible(_nameRow, v != null);
            SetVisible(_actions, v != null);
            if (v == null)
            {
                _detail.text = GameText.Get("outpost.detail.none");
                return;
            }
            _detail.text = OwnOutpostService.RowText(v);
            if (_lastNameFor != v.Id)
            {
                _lastNameFor = v.Id;
                _nameField.SetValueWithoutNotify(v.Name);
            }
            _resetName.SetEnabled(v.CustomNamed);
            _pack.SetEnabled(v.MaterialNeed > 0);
        }

        // ─────────────────────────────── 行 ───────────────────────────────

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("opv-item");
            var main = new Button { name = "OpRowMain" };
            main.AddToClassList("mw-btn");
            main.AddToClassList("opv-item-main");
            main.clicked += () => Select(row.userData is int i ? i : -1);
            UiTooltip.Attach(main, () => new TooltipContent { Title = GameText.Get("outpost.sec.list"), Body = GameText.Get("outpost.row.tip") });
            row.Add(main);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            row.userData = index;
            OutpostView v = Row(index);
            var main = row.Q<Button>("OpRowMain");
            main.text = v != null ? OwnOutpostService.RowBrief(v) : string.Empty; // 审查修复：行高固定，行里只放精简摘要（最要紧的警告不被截掉），完整内容在详情里。
            main.EnableInClassList("opv-item-selected", v != null && v.Id == _selectedId);
            main.EnableInClassList("opv-item-problem", v != null && v.Problem && !v.Wiped);
            main.EnableInClassList("opv-item-wiped", v != null && v.Wiped);
        }

        // ─────────────────────────────── 操作（按钮，自检直接调）───────────────────────────────

        public bool Select(int index)
        {
            OutpostView v = Row(index);
            if (v == null)
            {
                return false;
            }
            _selectedId = v.Id;
            _list.RefreshItems();
            ShowDetail(v);
            return true;
        }

        public bool ClickRename(string raw)
        {
            bool ok = OwnOutpostService.TryRename(CampaignSession.Current, _selectedId, raw, out string message);
            Say(ok, message);
            _lastNameFor = null;
            Refresh(force: true);
            return ok;
        }

        public bool ClickResetName()
        {
            bool ok = OwnOutpostService.TryResetName(CampaignSession.Current, _selectedId, out string message);
            Say(ok, message);
            _lastNameFor = null;
            Refresh(force: true);
            return ok;
        }

        public bool ClickPack()
        {
            bool ok = OwnOutpostService.TryLaunchPack(CampaignSession.Current, _selectedId, out string message);
            Say(ok, message);
            Refresh(force: true);
            return ok;
        }

        /// <summary>“定位”：镜头飞到这个前哨站（面板关掉，方便看清那一片）。</summary>
        public bool ClickLocate()
        {
            OutpostView v = _views.Find(x => x.Id == _selectedId);
            IWorldSite home = WorldSimulation.Home;
            bool ok = v != null && home != null && home.IsLoaded && WorldView.FlyTo(home.SiteId, v.Center);
            if (ok)
            {
                SetOpen(false);
            }
            else if (v != null)
            {
                Say(false, GameText.Get("outpost.err.locate"));
            }
            return ok;
        }

        /// <summary>“派驻机器”：作为子页打开机器名册（在名册里把机器设为“前哨劳动”并选这个前哨站；关掉名册回到总览）。</summary>
        public bool ClickRoster()
        {
            UiEscapeStack.OpenChild(this, RosterPanelUIToolkit.Open);
            return true;
        }

        private void Say(bool ok, string message)
        {
            SetMessage(message, !ok);
            if (!ok)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("opv-message-error", error);
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            e?.EnableInClassList("uk-hidden", !visible);
        }
    }
}
