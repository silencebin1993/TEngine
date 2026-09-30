using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-04（FG03 FGR-LOG-022“分流器：默认 1:1；可以设置优先输出口；每个输出口可以设置物品过滤。合流器：可以设置优先输入口”；
    /// FGR-LOG-023；卡片“必须同时交付：过滤器预设；分流比例在悬停中显示”；FG00 B05 / B06 / B12 / B13）：物流节点面板。
    /// - 分流器：左 / 右口份数（1～9）、优先输出口、左右口过滤（全部物品 / 只放某种 / 关闭）、过滤器预设（内置 + 自定义，选中即套用）、
    ///   “存为自定义预设”、删除自定义预设（先确认）；合流器：优先输入口；地下传送带：跨度 / 上限、两端、地下件数（没有设置）。
    /// - 上方状态行与读数行与悬停同一来源（<see cref="BeltNetworkService.DescribeBlock"/>、<see cref="BeltNetworkService.AppendNodeLines"/>）。
    /// 入口：建造模式里空闲时点一下已建成的节点。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 秒（真实时间）刷新，O(1)；关着时每帧 O(1)。
    /// 所有下拉框选中即生效（UI Toolkit 红线 8）。
    /// </summary>
    public sealed class BeltNodePanelUIToolkit : UiKitPanelHost
    {
        /// <summary>端口面板 30046 之上、字幕 30050 之下（拒绝原因字幕盖得住它），暂停菜单 30070 之下。</summary>
        public const int Order = 30047;

        private const float RefreshSeconds = 0.25f;

        public static BeltNodePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static GridCell Cell { get; private set; }
        private static GridCell? _pendingCell;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _close;
        private Label _state;
        private VisualElement _splitterBox;
        private VisualElement _mergerBox;
        private DropdownField _ratioL;
        private DropdownField _ratioR;
        private DropdownField _prioOut;
        private DropdownField _filterL;
        private DropdownField _filterR;
        private DropdownField _preset;
        private DropdownField _presetDelete;
        private Button _presetSave;
        private DropdownField _prioIn;
        private Label _detail;
        private Label _message;
        private Label _hint;
        private float _timer;
        private readonly List<ushort> _filterChoicesL = new List<ushort>(4);
        private readonly List<ushort> _filterChoicesR = new List<ushort>(4);
        private readonly List<BeltNodeService.Preset> _presets = new List<BeltNodeService.Preset>(16);
        private readonly List<BeltNodeService.Preset> _customPresets = new List<BeltNodeService.Preset>(8);
        private BeltNodeInfo _node;
        private bool _hasNode;

        protected override string UxmlLocation => "BeltNodePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public string MessageText => _message?.text ?? string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public bool SplitterSettingsVisible => _splitterBox != null && !_splitterBox.ClassListContains("bn-hidden");
        public bool MergerSettingsVisible => _mergerBox != null && !_mergerBox.ClassListContains("bn-hidden");
        public DropdownField RatioLField => _ratioL;
        public DropdownField RatioRField => _ratioR;
        public DropdownField PriorityOutField => _prioOut;
        public DropdownField FilterLField => _filterL;
        public DropdownField FilterRField => _filterR;
        public DropdownField PresetField => _preset;
        public DropdownField PresetDeleteField => _presetDelete;
        public DropdownField PriorityInField => _prioIn;
        public Button PresetSaveButton => _presetSave;
        public Button CloseButton => _close;

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

        /// <summary>打开 (cell) 上节点的面板（建造模式点节点与自检同一入口）。</summary>
        public static void Open(GridCell cell)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingCell = cell;
                return;
            }
            Cell = cell;
            if (IsOpen)
            {
                Instance.Refresh();
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingCell = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingCell.HasValue)
            {
                GridCell c = _pendingCell.Value;
                _pendingCell = null;
                Open(c);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("BeltNodeRoot");
            _title = root.Q<Label>("BeltNodeTitle");
            _close = root.Q<Button>("BeltNodeClose");
            _state = root.Q<Label>("BnState");
            _splitterBox = root.Q<VisualElement>("BnSplitterBox");
            _mergerBox = root.Q<VisualElement>("BnMergerBox");
            _ratioL = root.Q<DropdownField>("BnRatioL");
            _ratioR = root.Q<DropdownField>("BnRatioR");
            _prioOut = root.Q<DropdownField>("BnPrioOut");
            _filterL = root.Q<DropdownField>("BnFilterL");
            _filterR = root.Q<DropdownField>("BnFilterR");
            _preset = root.Q<DropdownField>("BnPreset");
            _presetDelete = root.Q<DropdownField>("BnPresetDelete");
            _presetSave = root.Q<Button>("BnPresetSave");
            _prioIn = root.Q<DropdownField>("BnPrioIn");
            _detail = root.Q<Label>("BnDetail");
            _message = root.Q<Label>("BnMessage");
            _hint = root.Q<Label>("BeltNodeHint");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _ratioL.RegisterValueChangedCallback(evt => OnRatioChosen(true, evt.newValue));
            _ratioR.RegisterValueChangedCallback(evt => OnRatioChosen(false, evt.newValue));
            _prioOut.RegisterValueChangedCallback(evt => SetPriorityOut((BeltSide)Math.Max(0, _prioOut.choices.IndexOf(evt.newValue))));
            _filterL.RegisterValueChangedCallback(evt => OnFilterChosen(true, evt.newValue));
            _filterR.RegisterValueChangedCallback(evt => OnFilterChosen(false, evt.newValue));
            _preset.RegisterValueChangedCallback(evt => OnPresetChosen(evt.newValue));
            _presetDelete.RegisterValueChangedCallback(evt => OnDeleteChosen(evt.newValue));
            _presetSave.clicked += () => SaveCustomPreset();
            _prioIn.RegisterValueChangedCallback(evt => SetPriorityIn((BeltSide)Math.Max(0, _prioIn.choices.IndexOf(evt.newValue))));
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
                GuidanceHooks.Raise(GuidanceHooks.LogisticsNodePanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                _message.text = string.Empty;
                Refresh();
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh();
        }

        /// <summary>按内核读数刷新（打开时每 0.25 秒一次，O(1)）。节点不在了（被拆 / 被摧毁）就收起。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            _hasNode = BeltNetworkService.TryGetNode(Cell, out _node);
            if (IsOpen && !_hasNode)
            {
                SetOpen(false);
                return;
            }
            _close.text = GameText.Get("logistics.nodepanel.close");
            if (!_hasNode)
            {
                return;
            }
            bool splitter = _node.Kind == BeltNodeKind.Splitter;
            bool merger = _node.Kind == BeltNodeKind.Merger;
            _title.text = splitter ? GameText.Format("logistics.nodepanel.title_splitter", _node.X, _node.Y)
                : merger ? GameText.Format("logistics.nodepanel.title_merger", _node.X, _node.Y)
                : GameText.Format("logistics.nodepanel.title_under", BeltNetworkService.PieceName(_node.Kind, _node.Tier), _node.EntranceX, _node.EntranceY, _node.ExitX, _node.ExitY);
            BeltNetworkService.Kernel.TryGetCellInfo(_node.X, _node.Y, out BeltCellInfo cell);
            _state.text = GameText.Format("logistics.nodepanel.state", BeltNetworkService.DescribeBlock(cell));
            var sb = new StringBuilder(256);
            BeltNetworkService.AppendNodeLines(sb, _node);
            if (splitter)
            {
                sb.Append('\n').Append(GameText.Format("logistics.nodepanel.stats", _node.SentL, _node.SentR,
                    GameText.Format("logistics.port.per_minute", _node.SentLPerMinute.ToString("0.#", CultureInfo.InvariantCulture)),
                    GameText.Format("logistics.port.per_minute", _node.SentRPerMinute.ToString("0.#", CultureInfo.InvariantCulture))));
            }
            _detail.text = sb.ToString().TrimStart('\n');
            _splitterBox.EnableInClassList("bn-hidden", !splitter);
            _mergerBox.EnableInClassList("bn-hidden", !merger);
            _hint.text = GameText.Get(splitter ? "logistics.nodepanel.hint_splitter" : merger ? "logistics.nodepanel.hint_merger" : "logistics.nodepanel.hint_under");
            if (splitter)
            {
                RefreshSplitter(state);
            }
            else if (merger)
            {
                SetLabel(_mergerBox, "BnPrioInLabel", "logistics.nodepanel.priority_in");
                FillSides(_prioIn, _node.PriorityIn);
            }
            // 地下传送带（under）没有设置：两个设置区都藏起来，读数行已写全。
        }

        private static void SetLabel(VisualElement box, string name, string key)
        {
            Label l = box?.Q<Label>(name);
            if (l != null)
            {
                l.text = GameText.Get(key);
            }
        }

        private void RefreshSplitter(CampaignState state)
        {
            SetLabel(_splitterBox, "BnRatioLLabel", "logistics.nodepanel.ratio_left");
            SetLabel(_splitterBox, "BnRatioRLabel", "logistics.nodepanel.ratio_right");
            SetLabel(_splitterBox, "BnPrioOutLabel", "logistics.nodepanel.priority_out");
            SetLabel(_splitterBox, "BnFilterLLabel", "logistics.nodepanel.filter_left");
            SetLabel(_splitterBox, "BnFilterRLabel", "logistics.nodepanel.filter_right");
            SetLabel(_splitterBox, "BnPresetLabel", "logistics.nodepanel.preset");
            SetLabel(_splitterBox, "BnItemsNote", "logistics.nodepanel.items_note");
            _presetSave.text = GameText.Get("logistics.nodepanel.preset_save");
            var ratios = new List<string>(BeltConst.RatioMax);
            for (int i = 1; i <= BeltConst.RatioMax; i++)
            {
                ratios.Add(i.ToString(CultureInfo.InvariantCulture));
            }
            DropdownChoices.Apply(_ratioL, new List<string>(ratios), "1");
            DropdownChoices.Apply(_ratioR, new List<string>(ratios), "1");
            _ratioL.SetValueWithoutNotify(_ratioL.choices[Mathf.Clamp(_node.RatioL - 1, 0, _ratioL.choices.Count - 1)]);
            _ratioR.SetValueWithoutNotify(_ratioR.choices[Mathf.Clamp(_node.RatioR - 1, 0, _ratioR.choices.Count - 1)]);
            FillSides(_prioOut, _node.PriorityOut);
            FillFilter(_filterL, _filterChoicesL, _node.FilterL);
            FillFilter(_filterR, _filterChoicesR, _node.FilterR);
            BeltNodeService.CollectPresets(state, _presets);
            var names = new List<string>(_presets.Count + 1) { GameText.Get("logistics.nodepanel.preset_placeholder") };
            _customPresets.Clear();
            foreach (BeltNodeService.Preset p in _presets)
            {
                names.Add(p.Name);
                if (p.Custom)
                {
                    _customPresets.Add(p);
                }
            }
            DropdownChoices.Apply(_preset, names, GameText.Get("logistics.nodepanel.preset_placeholder"));
            _preset.SetValueWithoutNotify(_preset.choices[0]);
            var deletes = new List<string>(_customPresets.Count + 1);
            deletes.Add(GameText.Get(_customPresets.Count > 0 ? "logistics.nodepanel.preset_delete_placeholder" : "logistics.nodepanel.preset_none_custom"));
            foreach (BeltNodeService.Preset p in _customPresets)
            {
                deletes.Add(p.Name);
            }
            DropdownChoices.Apply(_presetDelete, deletes, GameText.Get("logistics.nodepanel.preset_none_custom"));
            _presetDelete.SetValueWithoutNotify(_presetDelete.choices[0]);
            _presetDelete.SetEnabled(_customPresets.Count > 0);
        }

        private static void FillSides(DropdownField field, BeltSide current)
        {
            var sides = new List<string>
            {
                BeltNodeService.SideName(BeltSide.None), BeltNodeService.SideName(BeltSide.Left), BeltNodeService.SideName(BeltSide.Right),
            };
            DropdownChoices.Apply(field, sides, sides[0]);
            field.SetValueWithoutNotify(field.choices[Mathf.Clamp((int)current, 0, 2)]);
        }

        private static void FillFilter(DropdownField field, List<ushort> values, ushort current)
        {
            BeltNodeService.CollectFilterChoices(values, current);
            var names = new List<string>(values.Count);
            foreach (ushort v in values)
            {
                names.Add(BeltNodeService.FilterName(v));
            }
            DropdownChoices.Apply(field, names, names[0]);
            int i = values.IndexOf(current);
            field.SetValueWithoutNotify(field.choices[Mathf.Max(0, i)]);
        }

        // ── 设置（下拉框与自检同一入口；选中即生效）──────────────────────────────────

        private void OnRatioChosen(bool left, string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return;
            }
            SetRatio(left ? n : _node.RatioL, left ? _node.RatioR : n);
        }

        private void OnFilterChosen(bool left, string value)
        {
            DropdownField f = left ? _filterL : _filterR;
            List<ushort> values = left ? _filterChoicesL : _filterChoicesR;
            int i = f.choices.IndexOf(value);
            if (i < 0 || i >= values.Count)
            {
                return;
            }
            SetFilter(left, values[i]);
        }

        /// <summary>设分流比例（左 : 右 份数，各 1～9）。</summary>
        public bool SetRatio(int left, int right) => ApplySplitter(left, right, _node.PriorityOut, _node.FilterL, _node.FilterR);

        /// <summary>设优先输出口（不设 / 左 / 右）。</summary>
        public bool SetPriorityOut(BeltSide side) => ApplySplitter(_node.RatioL, _node.RatioR, side, _node.FilterL, _node.FilterR);

        /// <summary>设左 / 右口的过滤（<see cref="BeltConst.FilterAny"/> / <see cref="BeltConst.FilterNone"/> / 物品编号）。</summary>
        public bool SetFilter(bool left, ushort filter) =>
            ApplySplitter(_node.RatioL, _node.RatioR, _node.PriorityOut, left ? filter : _node.FilterL, left ? _node.FilterR : filter);

        private bool ApplySplitter(int ratioL, int ratioR, BeltSide prio, ushort filterL, ushort filterR)
        {
            if (!_hasNode || _node.Kind != BeltNodeKind.Splitter)
            {
                return false;
            }
            BeltOpResult r = BeltNetworkService.TrySetSplitter(CampaignSession.Current, Cell, ratioL, ratioR, prio, filterL, filterR);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.nodepanel.changed", BeltNetworkService.PieceName(_node.Kind, _node.Tier)) : r.Describe());
        }

        /// <summary>设合流器的优先输入口（不设 / 左 / 右）。</summary>
        public bool SetPriorityIn(BeltSide side)
        {
            if (!_hasNode || _node.Kind != BeltNodeKind.Merger)
            {
                return false;
            }
            BeltOpResult r = BeltNetworkService.TrySetMergerPriority(CampaignSession.Current, Cell, side);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.nodepanel.changed", BeltNetworkService.PieceName(_node.Kind, _node.Tier)) : r.Describe());
        }

        private void OnPresetChosen(string value)
        {
            int i = _preset.choices.IndexOf(value) - 1; // 第 0 项是占位“选一个预设”
            if (i >= 0 && i < _presets.Count)
            {
                ApplyPreset(_presets[i].Id);
            }
        }

        /// <summary>套用预设（ID：内置为表 ID，自定义为 custom.N）。</summary>
        public bool ApplyPreset(string presetId)
        {
            CampaignState state = CampaignSession.Current;
            BeltNodeService.CollectPresets(state, _presets);
            BeltNodeService.Preset p = _presets.Find(x => x.Id == presetId);
            bool ok = p != null && BeltNodeService.TryApplyPreset(state, Cell, p, out _);
            return Report(ok, ok ? BeltNodeService.LastMessage : GameText.Get("logistics.reason.not_found"));
        }

        /// <summary>“存为自定义预设”（按钮与自检同一入口）。</summary>
        public bool SaveCustomPreset()
        {
            bool ok = BeltNodeService.TrySaveCustom(CampaignSession.Current, Cell, out _, out string reason);
            return Report(ok, ok ? BeltNodeService.LastMessage : reason);
        }

        private void OnDeleteChosen(string value)
        {
            int i = _presetDelete.choices.IndexOf(value) - 1;
            if (i >= 0 && i < _customPresets.Count)
            {
                AskDeleteCustom(_customPresets[i].Serial);
            }
        }

        /// <summary>删除自定义预设前先确认（删除不可恢复，FG00 B04）；确认后删除。</summary>
        public void AskDeleteCustom(int serial)
        {
            BeltNodeService.Preset p = _customPresets.Find(x => x.Serial == serial);
            if (p == null)
            {
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Format("logistics.nodepanel.preset_delete_confirm", p.Name),
                Irreversible = true,
                ConfirmText = GameText.Get("logistics.nodepanel.preset_delete_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    bool ok = BeltNodeService.TryDeleteCustom(CampaignSession.Current, serial, out string reason);
                    Report(ok, ok ? BeltNodeService.LastMessage : reason);
                },
                OnCancel = () => Refresh(),
            };
            req.Consequences.Add(GameText.Get("logistics.nodepanel.preset_delete_consequence"));
            UiConfirmDialog.Show(req);
        }

        private bool Report(bool ok, string message)
        {
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
            Refresh();
            return ok;
        }
    }
}
