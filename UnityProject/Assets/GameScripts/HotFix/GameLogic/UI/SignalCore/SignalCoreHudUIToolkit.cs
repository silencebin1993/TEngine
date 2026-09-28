using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.SignalCore
{
    /// <summary>
    /// FG1-SIG-01（FG01 FGR-SIG-001、010～012、第 4 章预设；FG13 FGU-19；FG00 B01/B02/B04/B05/B06/B12/B15/B16/B18）：
    /// 信号位置 HUD 与信号核面板（UI Toolkit，一个 UIDocument）。
    /// - HUD（顶部居中，游戏世界里常驻）：“信号：归还核心 / 信号：ERC-003 #5”+“信号核 1/2”按钮（远征途中写“远征中锁定”）。
    /// - 面板入口：HUD 按钮、按键（默认 P，可重绑）、家园里左键点归还核心、远征准备面板“编辑信号核”（后两者经 GameEvent）。
    /// - 槽位：按顺序列出（1 号槽优先插入接入口）；未解锁的写明需要超控阵列 T 几；点选槽位后“卸下”“前移”。
    /// - 基元仓里的固件：形状 + 文字标种类（◆ 核心 / ● 常规），拖到槽位或“装入 N 号槽”；把槽位拖回基元仓 = 卸下；槽位拖到槽位 = 对调。
    /// - 刻印：装配站刻印已解锁固件的芯片（过渡渠道，DEBT-FG1SIG01-02）。
    /// - 预设：下拉选择、切换、另存为新预设、覆盖（确认框列新旧配置）、重命名、删除（确认框）。
    /// - 远征途中顶部红框说明原因；装 / 卸 / 换位 / 切换仍可点，结果是拒绝并给原因（不静默失效，B06）。
    /// 刷新：HUD 每帧只算 O(槽位数) 的整数键；面板打开时键不变的帧 O(基元仓实例数)，与机器数量无关（B18）。
    /// </summary>
    public sealed class SignalCoreHudUIToolkit : UiKitPanelHost
    {
        /// <summary>建造栏 30030 之上、通知 30040 与字幕 30050 之下：拒绝原因的字幕要盖得住本面板（UI_WORKFLOW_GUIDE.md 第 4 节）。</summary>
        public const int Order = 30035;

        private const string PartPrefix = "part:";
        private const string SlotPrefix = "slot:";

        public static SignalCoreHudUIToolkit Instance { get; private set; }

        /// <summary>自检注入“是否在游戏世界里”（编辑模式没有载入的地点）；为 null 时读 <see cref="GameRoot.AnyRegionActive"/>。</summary>
        public static Func<bool> InWorldOverrideForTests;
        public static bool IsOpen { get; private set; }
        private static bool _openRequested;

        private VisualElement _hudBar;
        private Label _location;
        private Label _uplinkStatus;
        private int _uplinkKey;
        /// <summary>FG1-SIG-03：接入过渡中 Esc = 取消接入（取消栈里的一层）。</summary>
        private static readonly object UplinkEscToken = new object();
        private static readonly Action CancelUplink = () => SignalUplinkService.CancelByPlayer();
        private Button _entry;
        // FG1-SIG-07（FGR-SIG-050、051）：跳回家园 / 上一台 / 覆盖网络叠加层开关。
        private VisualElement _jumpBar;
        private Button _jumpHome;
        private Button _jumpPrev;
        private Button _coverageToggle;
        private int _jumpKey;
        private VisualElement _panel;
        private Label _title;
        private Label _status;
        private Button _close;
        private Label _lock;
        private Label _hint;
        private Label _slotsTitle;
        private VisualElement _slotList;
        private Button _unequip;
        private Button _moveUp;
        private Label _bagTitle;
        private VisualElement _bagList;
        private Label _bagEmpty;
        private Button _equip;
        private Label _printTitle;
        private DropdownField _printChoice;
        private Button _print;
        private Label _presetsTitle;
        private Label _presetActive;
        private DropdownField _presetChoice;
        private Button _presetApply;
        private Label _presetNameLabel;
        private TextField _presetName;
        private Button _presetSave;
        private Button _presetOverwrite;
        private Button _presetRename;
        private Button _presetDelete;
        private Label _feedback;

        private readonly List<Button> _slotButtons = new List<Button>();
        private readonly List<Button> _bagButtons = new List<Button>();
        private readonly List<string> _bagPartIds = new List<string>();
        private readonly List<PrimitiveChipRecord> _bagScratch = new List<PrimitiveChipRecord>();
        private readonly List<string> _printIds = new List<string>();
        private readonly List<string> _presetIds = new List<string>();

        private int _selectedSlot = -1;
        private string _selectedPartId;
        private string _selectedPrintId;
        private string _selectedPresetId;
        private string _feedbackText = string.Empty;
        private bool _feedbackError;
        private int _uiSerial;
        private int _hudKey;
        private int _panelKey;
        /// <summary>FG1-SIG-06（FGU-44）：暴露面板与 HUD“暴露 N”按钮（同一个 UIDocument）。模态 / Esc 用自己的令牌，与信号核面板互不干扰。</summary>
        private readonly ExposurePanelView _exposure = new ExposurePanelView(new object());
        private static bool _exposureOpenRequested;

        public ExposurePanelView Exposure => _exposure;

        /// <summary>FG1-HUD-01（FGU-33）：接入 HUD（同一个 UIDocument，信号在机器里时显示）。</summary>
        private readonly UplinkHudView _uplinkHud = new UplinkHudView();
        public UplinkHudView UplinkHud => _uplinkHud;
        public static bool IsExposureOpen => Instance != null && Instance._exposure.IsOpen;

        public static void ToggleExposure()
        {
            if (IsExposureOpen)
            {
                CloseExposure();
            }
            else
            {
                OpenExposure();
            }
        }

        public static void OpenExposure()
        {
            if (Instance == null || !Instance._exposure.IsBound)
            {
                _exposureOpenRequested = true;
                return;
            }
            Instance._exposure.SetOpen(true);
        }

        public static void CloseExposure()
        {
            _exposureOpenRequested = false;
            Instance?._exposure.SetOpen(false);
        }

        protected override string UxmlLocation => "SignalCorePanel";
        protected override int SortingOrder => Order;

        // ── 自检可读 ─────────────────────────────────────────────────────────────
        public bool HudVisible => _hudBar != null && !_hudBar.ClassListContains("uk-hidden");
        /// <summary>FG1-SIG-07：跳转条（跳回家园 / 上一台 / 覆盖网络）是否显示。</summary>
        public bool JumpBarVisible => _jumpBar != null && !_jumpBar.ClassListContains("uk-hidden");
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public string LocationText => _location?.text ?? string.Empty;
        /// <summary>FG1-SIG-03：接入状态行的文字（隐藏时为空）。</summary>
        public string UplinkStatusText => _uplinkStatus != null && !_uplinkStatus.ClassListContains("uk-hidden") ? _uplinkStatus.text ?? string.Empty : string.Empty;
        public string EntryText => _entry?.text ?? string.Empty;
        /// <summary>FG1-SIG-07：HUD 上的跳转与叠加层按钮（自检 / 冒烟点它们，走按钮自己的 Clickable）。</summary>
        public Button JumpHomeButton => _jumpHome;
        public Button JumpPrevButton => _jumpPrev;
        public Button CoverageToggleButton => _coverageToggle;
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public bool FeedbackIsError => _feedbackError;
        public bool LockVisible => _lock != null && !_lock.ClassListContains("uk-hidden");
        public string LockText => _lock?.text ?? string.Empty;
        public int SelectedSlot => _selectedSlot;
        public string SelectedPartId => _selectedPartId;
        public int VisibleBagItemCount => _bagPartIds.Count;
        public string SlotText(int i) => i >= 0 && i < _slotButtons.Count ? _slotButtons[i].text : string.Empty;
        public string BagItemText(int i) => i >= 0 && i < _bagButtons.Count && i < _bagPartIds.Count ? _bagButtons[i].text : string.Empty;
        public string BagItemPartId(int i) => i >= 0 && i < _bagPartIds.Count ? _bagPartIds[i] : null;
        public Button SlotButton(int i) => i >= 0 && i < _slotButtons.Count ? _slotButtons[i] : null;
        public Button BagButton(int i) => i >= 0 && i < _bagButtons.Count ? _bagButtons[i] : null;
        public VisualElement BagListElement => _bagList;
        public string PresetActiveText => _presetActive?.text ?? string.Empty;
        public IReadOnlyList<string> PresetChoiceIds => _presetIds;
        public IReadOnlyList<string> PrintChoiceIds => _printIds;
        public string HintText => _hint?.text ?? string.Empty;

        private void Awake()
        {
            Instance = this;
            GameEvent.AddEventListener(SignalCoreService.PanelToggleEvent, OnToggleRequested);
            GameEvent.AddEventListener(SignalCoreService.PanelOpenEvent, OnOpenRequested);
        }

        protected override void OnDestroy()
        {
            GameEvent.RemoveEventListener(SignalCoreService.PanelToggleEvent, OnToggleRequested);
            GameEvent.RemoveEventListener(SignalCoreService.PanelOpenEvent, OnOpenRequested);
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            _exposure.OnDestroy();
            // FG1-SIG-03：接入过渡中 HUD 被销毁（卸载界面、回主菜单）时，别把“Esc 取消接入”这一层留在静态 Esc 栈里吃掉下一次 Esc。
            UiEscapeStack.Remove(UplinkEscToken);
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        private static void OnToggleRequested() => Toggle();
        private static void OnOpenRequested() => Open();

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

        public static void Open()
        {
            if (Instance == null || Instance._panel == null)
            {
                _openRequested = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _openRequested = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_openRequested)
            {
                _openRequested = false;
                SetOpen(true);
            }
            if (_exposureOpenRequested)
            {
                _exposureOpenRequested = false;
                _exposure.SetOpen(true);
            }
        }

        /// <summary>绑定 UXML（运行时与自检共用：自检把同一份 UXML 挂到临时面板上直接调这里）。</summary>
        public void BindView(VisualElement root)
        {
            _hudBar = root.Q<VisualElement>("SignalHudBar");
            _location = root.Q<Label>("SignalLocation");
            _uplinkStatus = root.Q<Label>("SignalUplinkStatus");
            _entry = root.Q<Button>("SignalCoreEntry");
            _jumpBar = root.Q<VisualElement>("SignalJumpBar");
            _jumpHome = root.Q<Button>("SignalJumpHome");
            _jumpPrev = root.Q<Button>("SignalJumpPrev");
            _coverageToggle = root.Q<Button>("SignalCoverageToggle");
            _panel = root.Q<VisualElement>("SignalCorePanel");
            _title = root.Q<Label>("SignalCoreTitle");
            _status = root.Q<Label>("SignalCoreStatus");
            _close = root.Q<Button>("SignalCoreClose");
            _lock = root.Q<Label>("SignalCoreLock");
            _hint = root.Q<Label>("SignalCoreHint");
            _slotsTitle = root.Q<Label>("SignalSlotsTitle");
            _slotList = root.Q<VisualElement>("SignalSlotList");
            _unequip = root.Q<Button>("SignalUnequip");
            _moveUp = root.Q<Button>("SignalMoveUp");
            _bagTitle = root.Q<Label>("SignalBagTitle");
            _bagList = root.Q<VisualElement>("SignalBagList");
            _bagEmpty = root.Q<Label>("SignalBagEmpty");
            _equip = root.Q<Button>("SignalEquip");
            _printTitle = root.Q<Label>("SignalPrintTitle");
            _printChoice = root.Q<DropdownField>("SignalPrintChoice");
            _print = root.Q<Button>("SignalPrint");
            _presetsTitle = root.Q<Label>("SignalPresetsTitle");
            _presetActive = root.Q<Label>("SignalPresetActive");
            _presetChoice = root.Q<DropdownField>("SignalPresetChoice");
            _presetApply = root.Q<Button>("SignalPresetApply");
            _presetNameLabel = root.Q<Label>("SignalPresetNameLabel");
            _presetName = root.Q<TextField>("SignalPresetName");
            _presetSave = root.Q<Button>("SignalPresetSave");
            _presetOverwrite = root.Q<Button>("SignalPresetOverwrite");
            _presetRename = root.Q<Button>("SignalPresetRename");
            _presetDelete = root.Q<Button>("SignalPresetDelete");
            _feedback = root.Q<Label>("SignalCoreFeedback");

            _entry.clicked += Toggle;
            // FG1-SIG-07：鼠标与键盘都能完成（B02）；按钮与快捷键走同一个入口。
            _jumpHome.clicked += () => SignalUplinkService.RequestJumpHome();
            _jumpPrev.clicked += () => SignalUplinkService.RequestJumpPrevious();
            _coverageToggle.clicked += GameLogic.View.SignalCoverageOverlayView.Toggle;
            UiTooltip.Attach(_jumpHome, JumpHomeTooltip);
            UiTooltip.Attach(_jumpPrev, JumpPrevTooltip);
            UiTooltip.Attach(_coverageToggle, CoverageTooltip);
            _close.clicked += () => SetOpen(false);
            _unequip.clicked += () => Apply(SignalCoreService.TryUnequip(CampaignSession.Current, _selectedSlot));
            _moveUp.clicked += () => Apply(SignalCoreService.TrySwapSlots(CampaignSession.Current, _selectedSlot, _selectedSlot - 1), _selectedSlot - 1);
            _equip.clicked += () => Apply(SignalCoreService.TryEquip(CampaignSession.Current, _selectedPartId, _selectedSlot));
            _print.clicked += () =>
            {
                SignalCoreResult r = SignalCoreService.TryPrintFirmwareChip(CampaignSession.Current, _selectedPrintId);
                if (r.Success)
                {
                    _selectedPartId = r.CreatedId; // 刻好的芯片直接选中，下一步“装入”即可。
                }
                Apply(r);
            };
            _printChoice.RegisterValueChangedCallback(_ =>
            {
                int i = _printChoice.index;
                _selectedPrintId = i >= 0 && i < _printIds.Count ? _printIds[i] : null;
                _uiSerial++;
            });
            _presetChoice.RegisterValueChangedCallback(_ =>
            {
                int i = _presetChoice.index;
                _selectedPresetId = i >= 0 && i < _presetIds.Count ? _presetIds[i] : null;
                SignalCorePresetRecord p = SignalCoreService.FindPreset(CampaignSession.Current, _selectedPresetId);
                if (p != null)
                {
                    _presetName.SetValueWithoutNotify(p.Name);
                }
                _uiSerial++;
            });
            _presetName.maxLength = SignalCoreService.PresetNameMaxChars;
            _presetApply.clicked += () => Apply(SignalCoreService.TryApplyPreset(CampaignSession.Current, _selectedPresetId));
            _presetSave.clicked += () =>
            {
                SignalCoreResult r = SignalCoreService.TrySavePreset(CampaignSession.Current, _presetName.value);
                if (r.Success)
                {
                    _selectedPresetId = r.CreatedId;
                }
                Apply(r);
            };
            _presetRename.clicked += () => Apply(SignalCoreService.TryRenamePreset(CampaignSession.Current, _selectedPresetId, _presetName.value));
            _presetOverwrite.clicked += AskOverwrite;
            _presetDelete.clicked += AskDelete;

            // 槽位与基元仓条目：按最多数量建好按钮池（数量来自调参表与基元仓容量），按需显示。
            _slotList.Clear();
            _slotButtons.Clear();
            for (int i = 0; i < SignalCoreService.MaxSlots; i++)
            {
                int index = i;
                var b = new Button { name = "SignalSlot" + i };
                b.AddToClassList("mw-btn");
                b.AddToClassList("sc-item");
                b.clicked += () =>
                {
                    _selectedSlot = index;
                    _uiSerial++;
                };
                UiDragDrop.MakeSource(b, () => SignalCoreService.SlotPartId(CampaignSession.Current, index).Length > 0 ? SlotPrefix + index : null,
                    () => FirmwareKinds.DisplayName(SignalCoreService.SlotContentId(CampaignSession.Current, index)) ?? string.Empty);
                UiDragDrop.MakeTarget(b, payload => JudgeSlotDrop(payload, index), payload => OnSlotDrop(payload, index));
                UiTooltip.Attach(b, () => SlotTooltip(index));
                _slotList.Add(b);
                _slotButtons.Add(b);
            }
            _bagList.Clear();
            _bagButtons.Clear();
            for (int i = 0; i < PrimitiveInventory.Capacity; i++)
            {
                int index = i;
                var b = new Button { name = "SignalBagItem" + i };
                b.AddToClassList("mw-btn");
                b.AddToClassList("sc-item");
                b.AddToClassList("sc-item-hidden");
                b.clicked += () =>
                {
                    _selectedPartId = index < _bagPartIds.Count ? _bagPartIds[index] : null;
                    _uiSerial++;
                };
                UiDragDrop.MakeSource(b, () => index < _bagPartIds.Count ? PartPrefix + _bagPartIds[index] : null,
                    () => index < _bagPartIds.Count ? FirmwareKinds.DisplayName(PrimitiveInventory.Find(CampaignSession.Current, _bagPartIds[index])?.CardDefId) : string.Empty);
                UiTooltip.Attach(b, () => BagTooltip(index));
                _bagList.Add(b);
                _bagButtons.Add(b);
            }
            UiDragDrop.MakeTarget(_bagList, JudgeBagDrop, OnBagDrop);

            UiTooltip.Attach(_entry, () => new TooltipContent
            {
                Title = GameText.Get("signal.hud.tip_title"),
                Body = GameText.Format("signal.hud.tip", InputDisplay.ForAction(GameActionId.OpenSignalCore)),
                Shortcut = GameActionId.OpenSignalCore,
            });
            _exposure.Bind(root, () => _exposure.SetOpen(!_exposure.IsOpen));
            _uplinkHud.Bind(root);
            _hudKey = 0;
            _jumpKey = 0;
            _panelKey = 0;
        }

        /// <summary>FG1-SIG-07：跳转与叠加层按钮的文字 / 状态（只在上一台、冷却整秒、叠加层开关、语言或键位变化时改写；每帧 O(1)）。</summary>
        private void RefreshJumpButtons(CampaignState s)
        {
            if (_jumpHome == null || _jumpPrev == null || _coverageToggle == null)
            {
                return;
            }
            int prev = SignalUplinkService.PreviousMachine(s);
            int cooldown = (int)Math.Ceiling(SignalUplinkService.JumpCooldownRemaining(s));
            bool overlay = GameLogic.View.SignalCoverageOverlayView.Enabled;
            int key = HashCode.Combine(prev, cooldown, overlay, (int)GameText.Language, GameSettings.Revision, SignalUplinkService.IsJumpingHome);
            if (key == _jumpKey)
            {
                return;
            }
            _jumpKey = key;
            _jumpHome.text = GameText.Get("signal.jump.home_button");
            _jumpPrev.text = cooldown > 0
                ? GameText.Get("signal.jump.prev_button") + GameText.Format("signal.jump.cooldown_suffix", cooldown.ToString(CultureInfo.InvariantCulture))
                : GameText.Get("signal.jump.prev_button");
            _jumpPrev.EnableInClassList("sc-hud-jump-cooling", cooldown > 0);
            _jumpPrev.SetEnabled(prev != 0);
            _coverageToggle.text = GameText.Get(overlay ? "signal.overlay.button_on" : "signal.overlay.button_off");
            _coverageToggle.EnableInClassList("sc-hud-coverage-on", overlay);
        }

        /// <summary>自检：跳转 / 叠加层按钮当前的文字。</summary>
        public string JumpHomeText => _jumpHome?.text ?? string.Empty;
        public string JumpPrevText => _jumpPrev?.text ?? string.Empty;
        public string CoverageToggleText => _coverageToggle?.text ?? string.Empty;

        private TooltipContent JumpHomeTooltip() => new TooltipContent
        {
            Title = GameText.Get("signal.jump.home_tip_title"),
            Body = GameText.Format("signal.jump.home_tip", InputDisplay.ForAction(GameActionId.JumpHome),
                SignalUplinkService.FarDistanceCells.ToString("0", CultureInfo.InvariantCulture),
                SignalUplinkService.FarTransitionSeconds.ToString("0.#", CultureInfo.InvariantCulture),
                SignalUplinkService.FarCooldownSeconds.ToString("0", CultureInfo.InvariantCulture)),
            Shortcut = GameActionId.JumpHome,
        };

        private TooltipContent JumpPrevTooltip()
        {
            CampaignState s = CampaignSession.Current;
            int prev = SignalUplinkService.PreviousMachine(s);
            string body = GameText.Format("signal.jump.prev_tip", InputDisplay.ForAction(GameActionId.JumpPreviousMachine),
                prev != 0 ? SignalPresence.MachineLabel(prev) : GameText.Get("signal.jump.prev_none_name"),
                SignalUplinkService.FarDistanceCells.ToString("0", CultureInfo.InvariantCulture),
                SignalUplinkService.FarTransitionSeconds.ToString("0.#", CultureInfo.InvariantCulture),
                SignalUplinkService.FarCooldownSeconds.ToString("0", CultureInfo.InvariantCulture));
            double cd = SignalUplinkService.JumpCooldownRemaining(s);
            if (cd > 0)
            {
                body += "\n" + GameText.Format("signal.jump.cooldown_line", Math.Ceiling(cd).ToString("0", CultureInfo.InvariantCulture));
            }
            return new TooltipContent { Title = GameText.Get("signal.jump.prev_tip_title"), Body = body, Shortcut = GameActionId.JumpPreviousMachine };
        }

        private TooltipContent CoverageTooltip()
        {
            string site = WorldView.ObservedSiteId;
            return new TooltipContent
            {
                Title = GameText.Get("signal.overlay.tip_title"),
                Body = GameText.Format("signal.overlay.tip", InputDisplay.ForAction(GameActionId.ToggleOverlay),
                    SignalCoverageService.SiteSourceCount(site).ToString(CultureInfo.InvariantCulture),
                    SignalCoverageService.DisconnectedCount(site).ToString(CultureInfo.InvariantCulture)),
                Shortcut = GameActionId.ToggleOverlay,
            };
        }

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            Refresh();
        }

        public void SetOpen(bool open)
        {
            if (_panel == null)
            {
                return;
            }
            if (open == IsOpen && open == PanelVisible)
            {
                return;
            }
            IsOpen = open;
            _panel.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.SignalCoreFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                CampaignState s = CampaignSession.Current;
                _selectedSlot = FirstFreeUnlockedSlot(s);
                _selectedPartId = null;
                _feedbackText = string.Empty;
                _feedbackError = false;
                _panelKey = 0;
                Refresh();
            }
            else
            {
                if (UiDragDrop.IsDragging)
                {
                    UiDragDrop.Cancel();
                }
                _presetName?.Blur();
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        /// <summary>按当前战役刷新（自检可直接调用）。</summary>
        public void Refresh()
        {
            CampaignState s = CampaignSession.Current;
            bool inWorld = s != null && (InWorldOverrideForTests?.Invoke() ?? GameRoot.AnyRegionActive);
            if (!inWorld && IsOpen)
            {
                SetOpen(false);
            }
            SetVisible(_hudBar, s != null && (inWorld || IsOpen));
            SetVisible(_jumpBar, s != null && inWorld);
            RefreshUplinkStatus(s, inWorld);
            _uplinkHud.Refresh(s, inWorld);
            _exposure.Refresh(s, inWorld);
            if (s == null)
            {
                _hudKey = 0;
                return;
            }

            bool locked = SignalCoreService.ExpeditionUnderway;
            int equipped = SignalCoreService.EquippedCount(s);
            int unlocked = SignalCoreService.UnlockedSlots(s);
            int hudKey = HashCode.Combine(SignalPresence.CurrentMachineLogicId, equipped, unlocked, locked,
                (int)GameText.Language, GameSettings.Revision, SignalCoreService.Revision);
            if (hudKey != _hudKey)
            {
                _hudKey = hudKey;
                _location.text = SignalPresence.LocationText();
                _entry.text = locked
                    ? GameText.Format("signal.hud.core_button_locked", equipped, unlocked)
                    : GameText.Format("signal.hud.core_button", equipped, unlocked);
            }
            RefreshJumpButtons(s);
            if (!IsOpen)
            {
                return;
            }

            PrimitiveChipRecord[] chips = s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>();
            int panelKey = HashCode.Combine(SignalCoreService.Revision, chips.Length, PrimitiveInventory.BagCount(s), Mathf.FloorToInt(s.Scrap), locked,
                (int)GameText.Language, GameSettings.Revision, HashCode.Combine(_uiSerial, _selectedSlot, _selectedPartId, s.SignalCore?.Presets?.Length ?? 0,
                    FirmwareKinds.Revision, SignalCoreService.OverrideArrayTier(s)));
            if (panelKey == _panelKey)
            {
                return;
            }
            _panelKey = panelKey;
            RebuildPanel(s, locked, equipped, unlocked);
        }

        private void RebuildPanel(CampaignState s, bool locked, int equipped, int unlocked)
        {
            int max = SignalCoreService.MaxSlots;
            if (_selectedSlot < 0 || _selectedSlot >= max)
            {
                _selectedSlot = FirstFreeUnlockedSlot(s);
            }

            _title.text = GameText.Get("signal.core.title");
            _status.text = GameText.Format("signal.core.slot_count", equipped, unlocked);
            _close.text = GameText.Get("signal.core.close");
            SetVisible(_lock, locked);
            _lock.text = locked ? GameText.Get("signal.reason.expedition") : string.Empty;
            _hint.text = GameText.Get("signal.core.hint");
            _slotsTitle.text = GameText.Get("signal.core.slots_title");

            for (int i = 0; i < _slotButtons.Count; i++)
            {
                Button b = _slotButtons[i];
                bool open = SignalCoreService.IsSlotUnlocked(s, i);
                string content = SignalCoreService.SlotContentId(s, i);
                string body;
                if (content.Length > 0)
                {
                    FirmwareKind kind = FirmwareKinds.KindOf(content);
                    bool raw = FirmwareKinds.IsRaw(s, content);
                    body = FirmwareKinds.KindLabel(kind) + " " + (FirmwareKinds.DisplayName(content) ?? content)
                           + (raw ? "  " + GameText.Get("signal.core.raw_tag") : string.Empty);
                    b.EnableInClassList("sc-item-raw", raw);
                    if (!open)
                    {
                        body += "\n" + GameText.Format("signal.core.slot_locked", SignalCoreService.TierForSlot(i));
                    }
                    b.EnableInClassList("sc-item-core", kind == FirmwareKind.Core);
                }
                else
                {
                    body = open ? GameText.Get("signal.core.slot_empty") : GameText.Format("signal.core.slot_locked", SignalCoreService.TierForSlot(i));
                    b.EnableInClassList("sc-item-core", false);
                    b.EnableInClassList("sc-item-raw", false);
                }
                b.text = GameText.Format("signal.core.slot_index", i + 1) + "  " + body;
                b.EnableInClassList("sc-item-locked", !open);
                b.EnableInClassList("sc-item-selected", i == _selectedSlot);
            }
            _unequip.text = GameText.Get("signal.core.unequip");
            _moveUp.text = GameText.Get("signal.core.move_up");

            SignalCoreService.BagFirmware(s, _bagScratch);
            _bagPartIds.Clear();
            for (int i = 0; i < _bagScratch.Count && i < _bagButtons.Count; i++)
            {
                _bagPartIds.Add(_bagScratch[i].PartId);
            }
            if (_selectedPartId != null && !_bagPartIds.Contains(_selectedPartId))
            {
                _selectedPartId = null;
            }
            _bagTitle.text = GameText.Format("signal.core.bag_title", PrimitiveInventory.BagCount(s), PrimitiveInventory.Capacity);
            for (int i = 0; i < _bagButtons.Count; i++)
            {
                Button b = _bagButtons[i];
                bool shown = i < _bagPartIds.Count;
                b.EnableInClassList("sc-item-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                PrimitiveChipRecord chip = _bagScratch[i];
                FirmwareKind kind = FirmwareKinds.KindOf(chip.CardDefId);
                string reserved = string.IsNullOrEmpty(chip.ReservedByTransactionId) ? string.Empty : "  " + GameText.Get("signal.core.reserved");
                bool rawChip = FirmwareKinds.IsRaw(s, chip.CardDefId);
                b.text = FirmwareKinds.KindLabel(kind) + " " + (FirmwareKinds.DisplayName(chip.CardDefId) ?? chip.CardDefId)
                         + (rawChip ? "  " + GameText.Get("signal.core.raw_tag") : string.Empty)
                         + "  #" + ShortId(chip.PartId) + reserved;
                b.EnableInClassList("sc-item-core", kind == FirmwareKind.Core);
                b.EnableInClassList("sc-item-raw", rawChip);
                b.EnableInClassList("sc-item-selected", chip.PartId == _selectedPartId);
            }
            SetVisible(_bagEmpty, _bagPartIds.Count == 0);
            _bagEmpty.text = GameText.Get("signal.core.bag_empty");
            _equip.text = GameText.Format("signal.core.equip", _selectedSlot + 1);

            // 刻印
            _printTitle.text = GameText.Format("signal.core.print_title", SignalCoreService.FirmwareChipPrintScrap);
            _printIds.Clear();
            _printIds.AddRange(SignalCoreService.PrintableFirmware(s));
            var printChoices = new List<string>(_printIds.Count);
            foreach (string id in _printIds)
            {
                printChoices.Add(FirmwareKinds.KindLabel(FirmwareKinds.KindOf(id)) + " " + (FirmwareKinds.DisplayName(id) ?? id));
            }
            if (_selectedPrintId == null || !_printIds.Contains(_selectedPrintId))
            {
                _selectedPrintId = _printIds.Count > 0 ? _printIds[0] : null;
            }
            // 下拉选项一律走 DropdownChoices（去掉会被当成子菜单 / 快捷键的字符、同名去重、空列表占位并禁用）。
            DropdownChoices.Apply(_printChoice, printChoices, GameText.Get("signal.core.print_none"));
            int printIndex = _printIds.IndexOf(_selectedPrintId ?? string.Empty);
            if (printIndex >= 0)
            {
                _printChoice.SetValueWithoutNotify(_printChoice.choices[printIndex]);
            }
            _print.text = GameText.Get("signal.core.print");
            _print.SetEnabled(_printIds.Count > 0);

            // 预设
            _presetsTitle.text = GameText.Get("signal.core.presets_title");
            SignalCoreState core = s.SignalCore;
            _presetIds.Clear();
            var presetChoices = new List<string>();
            foreach (SignalCorePresetRecord p in core?.Presets ?? Array.Empty<SignalCorePresetRecord>())
            {
                _presetIds.Add(p.PresetId);
                presetChoices.Add(GameText.Format("signal.core.preset.item", p.Name, SignalCoreService.DescribeLoadout(p.SlotContentIds)));
            }
            if (_selectedPresetId == null || !_presetIds.Contains(_selectedPresetId))
            {
                _selectedPresetId = !string.IsNullOrEmpty(core?.ActivePresetId) && _presetIds.Contains(core.ActivePresetId)
                    ? core.ActivePresetId
                    : _presetIds.Count > 0 ? _presetIds[0] : null;
            }
            DropdownChoices.Apply(_presetChoice, presetChoices, GameText.Get("signal.core.preset.none"));
            int presetIndex = _presetIds.IndexOf(_selectedPresetId ?? string.Empty);
            if (presetIndex >= 0)
            {
                _presetChoice.SetValueWithoutNotify(_presetChoice.choices[presetIndex]);
            }
            bool hasPresets = _presetIds.Count > 0;
            _presetApply.SetEnabled(hasPresets);
            _presetOverwrite.SetEnabled(hasPresets);
            _presetRename.SetEnabled(hasPresets);
            _presetDelete.SetEnabled(hasPresets);
            SignalCorePresetRecord active = SignalCoreService.FindPreset(s, core?.ActivePresetId);
            _presetActive.text = presetChoices.Count == 0
                ? GameText.Get("signal.core.preset.none")
                : active == null
                    ? GameText.Get("signal.core.preset.no_active")
                    : SignalCoreService.PresetMatchesCurrent(s, active)
                        ? GameText.Format("signal.core.preset.active", active.Name)
                        : GameText.Format("signal.core.preset.modified", active.Name);
            _presetNameLabel.text = GameText.Get("signal.core.preset.name_label");
            _presetApply.text = GameText.Get("signal.core.preset.apply");
            _presetSave.text = GameText.Get("signal.core.preset.save_new");
            _presetOverwrite.text = GameText.Get("signal.core.preset.overwrite");
            _presetRename.text = GameText.Get("signal.core.preset.rename");
            _presetDelete.text = GameText.Get("signal.core.preset.delete");

            _feedback.text = _feedbackText;
            _feedback.EnableInClassList("sc-feedback-error", _feedbackError);
        }

        // ── 操作结果 ─────────────────────────────────────────────────────────────

        /// <summary>显示结果；失败时给“拒绝”音与原因字幕（FG00 B06，不静默）。</summary>
        private void Apply(SignalCoreResult r, int selectSlotOnSuccess = -1)
        {
            _feedbackText = r.Message ?? string.Empty;
            _feedbackError = !r.Success;
            if (!r.Success)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied, r.Message);
            }
            else if (selectSlotOnSuccess >= 0)
            {
                _selectedSlot = selectSlotOnSuccess;
            }
            _uiSerial++;
            _panelKey = 0;
            Refresh();
        }

        private void AskOverwrite()
        {
            CampaignState s = CampaignSession.Current;
            SignalCorePresetRecord p = SignalCoreService.FindPreset(s, _selectedPresetId);
            if (p == null)
            {
                Apply(SignalCoreResult.Fail(SignalCoreService.CodePresetNotFound, GameText.Get("signal.reason.preset_not_selected")));
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Format("signal.core.preset.confirm_overwrite", p.Name),
                ConfirmText = GameText.Get("signal.core.preset.overwrite"),
                CancelText = GameText.Get("ui.common.cancel"),
                OnConfirm = () => Apply(SignalCoreService.TryOverwritePreset(CampaignSession.Current, p.PresetId)),
            };
            req.Lines.Add(GameText.Format("signal.core.preset.old_line", SignalCoreService.DescribeLoadout(p.SlotContentIds)));
            req.Lines.Add(GameText.Format("signal.core.preset.new_line", SignalCoreService.DescribeLoadout(SignalCoreService.CurrentContentIds(s))));
            UiConfirmDialog.Show(req);
        }

        private void AskDelete()
        {
            SignalCorePresetRecord p = SignalCoreService.FindPreset(CampaignSession.Current, _selectedPresetId);
            if (p == null)
            {
                Apply(SignalCoreResult.Fail(SignalCoreService.CodePresetNotFound, GameText.Get("signal.reason.preset_not_selected")));
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Format("signal.core.preset.confirm_delete", p.Name),
                ConfirmText = GameText.Get("signal.core.preset.delete"),
                CancelText = GameText.Get("ui.common.cancel"),
                Irreversible = true,
                OnConfirm = () => Apply(SignalCoreService.TryDeletePreset(CampaignSession.Current, p.PresetId)),
            };
            req.Lines.Add(GameText.Format("signal.core.preset.old_line", SignalCoreService.DescribeLoadout(p.SlotContentIds)));
            UiConfirmDialog.Show(req);
        }

        // ── 拖放 ─────────────────────────────────────────────────────────────────

        private static DropVerdict JudgeSlotDrop(object payload, int slot)
        {
            CampaignState s = CampaignSession.Current;
            if (!(payload is string p) || !(p.StartsWith(PartPrefix, StringComparison.Ordinal) || p.StartsWith(SlotPrefix, StringComparison.Ordinal)))
            {
                return new DropVerdict(false, GameText.Get("signal.reason.nothing_selected"));
            }
            if (!SignalCoreService.CanEdit(s, out SignalCoreResult denial))
            {
                return new DropVerdict(false, denial.Message);
            }
            if (!SignalCoreService.IsSlotUnlocked(s, slot))
            {
                return new DropVerdict(false, GameText.Format("signal.reason.slot_locked", slot + 1, SignalCoreService.TierForSlot(slot)));
            }
            if (p.StartsWith(PartPrefix, StringComparison.Ordinal))
            {
                PrimitiveChipRecord chip = PrimitiveInventory.Find(s, p.Substring(PartPrefix.Length));
                if (chip == null || !FirmwareKinds.IsFirmware(chip.CardDefId))
                {
                    return new DropVerdict(false, GameText.Get("signal.reason.not_firmware"));
                }
            }
            else if (int.TryParse(p.Substring(SlotPrefix.Length), out int from) && from == slot)
            {
                return new DropVerdict(false, GameText.Get("signal.reason.same_slot"));
            }
            return new DropVerdict(true);
        }

        private void OnSlotDrop(object payload, int slot)
        {
            string p = payload as string ?? string.Empty;
            CampaignState s = CampaignSession.Current;
            if (p.StartsWith(PartPrefix, StringComparison.Ordinal))
            {
                Apply(SignalCoreService.TryEquip(s, p.Substring(PartPrefix.Length), slot), slot);
            }
            else if (int.TryParse(p.Substring(Math.Min(p.Length, SlotPrefix.Length)), out int from))
            {
                Apply(SignalCoreService.TrySwapSlots(s, from, slot), slot);
            }
        }

        private static DropVerdict JudgeBagDrop(object payload)
        {
            CampaignState s = CampaignSession.Current;
            if (!(payload is string p) || !p.StartsWith(SlotPrefix, StringComparison.Ordinal))
            {
                return new DropVerdict(false, GameText.Get("signal.reason.already_in_bag"));
            }
            if (!SignalCoreService.CanEdit(s, out SignalCoreResult denial))
            {
                return new DropVerdict(false, denial.Message);
            }
            if (PrimitiveInventory.BagCount(s) >= PrimitiveInventory.Capacity)
            {
                return new DropVerdict(false, GameText.Get("signal.reason.bag_full"));
            }
            return new DropVerdict(true);
        }

        private void OnBagDrop(object payload)
        {
            string p = payload as string ?? string.Empty;
            if (p.StartsWith(SlotPrefix, StringComparison.Ordinal) && int.TryParse(p.Substring(SlotPrefix.Length), out int from))
            {
                Apply(SignalCoreService.TryUnequip(CampaignSession.Current, from));
            }
        }

        // ── 提示 ─────────────────────────────────────────────────────────────────

        private static TooltipContent SlotTooltip(int index)
        {
            CampaignState s = CampaignSession.Current;
            string content = SignalCoreService.SlotContentId(s, index);
            FirmwareKind kind = FirmwareKinds.KindOf(content);
            return new TooltipContent
            {
                Title = GameText.Format("signal.core.slot_index", index + 1),
                Body = content.Length > 0
                    ? (FirmwareKinds.DisplayName(content) ?? content) + "\n" + FirmwareKinds.KindTip(kind) + RawTip(s, content)
                    : SignalCoreService.IsSlotUnlocked(s, index)
                        ? GameText.Get("signal.core.hint")
                        : GameText.Format("signal.core.slot_locked", SignalCoreService.TierForSlot(index)),
            };
        }

        private TooltipContent BagTooltip(int index)
        {
            PrimitiveChipRecord chip = index < _bagPartIds.Count ? PrimitiveInventory.Find(CampaignSession.Current, _bagPartIds[index]) : null;
            if (chip == null)
            {
                return null;
            }
            return new TooltipContent
            {
                Title = FirmwareKinds.DisplayName(chip.CardDefId) ?? chip.CardDefId,
                Body = FirmwareKinds.KindTip(FirmwareKinds.KindOf(chip.CardDefId)) + RawTip(CampaignSession.Current, chip.CardDefId),
            };
        }

        /// <summary>FG1-SIG-06：未破解固件的悬停说明（裸跑代价与破解后的变化，数值读调参表）；已破解 / 己方固件为空。</summary>
        public static string RawTip(CampaignState s, string contentId) =>
            FirmwareKinds.IsRaw(s, contentId)
                ? "\n" + GameText.Get("signal.core.raw_tip_title") + "：" + GameText.Format("signal.core.raw_tip",
                    RawFirmwareService.ExposurePerFire.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    RawFirmwareService.HeatMultiplier.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    GameText.Get(FirmwareKinds.AfterCrackKey(contentId))) // 破解后能装到哪里按种类说（核心 / 无机器实现 / 可装机器），不给假承诺
                : string.Empty;

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int FirstFreeUnlockedSlot(CampaignState s)
        {
            int unlocked = SignalCoreService.UnlockedSlots(s);
            for (int i = 0; i < unlocked; i++)
            {
                if (SignalCoreService.SlotPartId(s, i).Length == 0)
                {
                    return i;
                }
            }
            return 0;
        }

        private static string ShortId(string partId) =>
            string.IsNullOrEmpty(partId) ? string.Empty : partId.Length <= 4 ? partId : partId.Substring(partId.Length - 4);

        /// <summary>FG1-SIG-03：接入状态行 + 过渡中 Esc 取消。每帧 O(1)（键不变不重建文字）。</summary>
        private void RefreshUplinkStatus(CampaignState s, bool inWorld)
        {
            UiEscapeStack.Sync(UplinkEscToken, inWorld && SignalUplinkService.HasCancellableTransition, CancelUplink); // FG1-SIG-07 审查修复：跳回家园的远距离过渡也能 Esc 取消。
            if (_uplinkStatus == null)
            {
                return;
            }
            if (s == null || !inWorld)
            {
                SetVisible(_uplinkStatus, false);
                _uplinkKey = 0;
                return;
            }
            int key = SignalUplinkService.StatusKey(s);
            if (key == _uplinkKey)
            {
                return;
            }
            _uplinkKey = key;
            string text = SignalUplinkService.StatusLine(s);
            _uplinkStatus.text = text;
            SetVisible(_uplinkStatus, !string.IsNullOrEmpty(text));
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            e?.EnableInClassList("uk-hidden", !visible);
        }
    }
}
