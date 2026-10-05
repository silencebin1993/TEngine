using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Signal;
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
    /// FG6-DEF-02（FG06 FGR-DEF-010～013；卡片“护盾值和重启倒计时的显示”）：防御建筑面板（护盾发生器 / 陷阱发射器 / 屏障 / 闸门共用，按种类显示对应的一段）。
    /// - 护盾：护盾值条（填充比例 = 护盾值 / 上限；收起时变灰，另有文字）、状态与重启倒计时（按步序号换算的游戏秒）、耗电构成、累计吸收 / 过载次数、规则说明。
    /// - 陷阱：固件下拉（只列能装进陷阱发射器的固件，选中即生效）、铺设方式（一条线 / 一片区域，选中的描边 + “●”）、补给（缺什么、怎么办）、已铺轮数。
    /// - 屏障 / 闸门：耐久与规则说明（闸门写死只放行己方）。
    /// 入口：建筑面板“护盾… / 陷阱…”（<see cref="Open"/>）。模态；Esc / 关闭 / 点遮罩关闭。改名、维修、启停在建筑面板（同一座建筑）。
    /// 刷新：结构（下拉选项、按钮文字）在 <see cref="DefenseService.Revision"/> / 语言 / 建筑变化时重建；读数节流 0.2 秒（倒计时跟着走）。O(陷阱参数数) + O(1)。
    /// </summary>
    public sealed class DefensePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30064;
        private const float ReadingsInterval = 0.2f;

        public static DefensePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _state;
        private Button _help;
        private Button _close;
        private Label _message;
        private Label _status;
        private Label _durability;
        private VisualElement _shieldSection;
        private Label _secShield;
        private VisualElement _shieldFill;
        private Label _shieldValue;
        private Label _shieldCountdown;
        private Label _shieldPower;
        private Label _shieldStats;
        private Label _shieldRule;
        private VisualElement _trapSection;
        private Label _secTrap;
        private DropdownField _firmware;
        private Label _patternTitle;
        private Button _patternLine;
        private Button _patternArea;
        private Label _trapSupply;
        private Label _trapStats;
        private Label _trapHint;
        private VisualElement _barrierSection;
        private Label _secBarrier;
        private Label _barrierRule;
        private Label _footer;

        private readonly List<string> _firmwareIds = new List<string>();
        private int _key;
        private float _nextReadings;
        private bool _suppress;
        private DefenseKind _kind;

        protected override string UxmlLocation => "DefensePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string DurabilityText => _durability?.text ?? string.Empty;
        public bool ShieldSectionVisible => _shieldSection != null && !_shieldSection.ClassListContains("uk-hidden");
        public bool TrapSectionVisible => _trapSection != null && !_trapSection.ClassListContains("uk-hidden");
        public bool BarrierSectionVisible => _barrierSection != null && !_barrierSection.ClassListContains("uk-hidden");
        public string ShieldValueText => _shieldValue?.text ?? string.Empty;
        public string ShieldCountdownText => _shieldCountdown?.text ?? string.Empty;
        public string ShieldPowerText => _shieldPower?.text ?? string.Empty;
        public string ShieldStatsText => _shieldStats?.text ?? string.Empty;
        public float ShieldFillPercent { get; private set; }
        public bool ShieldFillDown => _shieldFill != null && _shieldFill.ClassListContains("df-bar-fill-down");
        public string TrapSupplyText => _trapSupply?.text ?? string.Empty;
        public string TrapStatsText => _trapStats?.text ?? string.Empty;
        public string BarrierRuleText => _barrierRule?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public IReadOnlyList<string> FirmwareChoices => _firmware?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<string> FirmwareIds => _firmwareIds;
        public DropdownField FirmwareField => _firmware;
        public Button PatternLineButton => _patternLine;
        public Button PatternAreaButton => _patternArea;
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public VisualElement RootElement => _root;

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

        /// <summary>打开某座防御建筑的面板（已开着就切到这座）。</summary>
        public static void Open(string buildingId)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingId = buildingId;
                BuildingId = buildingId;
                return;
            }
            BuildingId = buildingId;
            Instance._key = 0;
            if (IsOpen)
            {
                Instance.Refresh(force: true);
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingId = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingId != null)
            {
                string id = _pendingId;
                _pendingId = null;
                Open(id);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("DefenseRoot");
            _title = root.Q<Label>("DefenseTitle");
            _state = root.Q<Label>("DefenseState");
            _help = root.Q<Button>("DefenseHelp");
            _close = root.Q<Button>("DefenseClose");
            _message = root.Q<Label>("DefenseMessage");
            _status = root.Q<Label>("DefenseStatus");
            _durability = root.Q<Label>("DefenseDurability");
            _shieldSection = root.Q<VisualElement>("DefenseShieldSection");
            _secShield = root.Q<Label>("DefenseSecShield");
            _shieldFill = root.Q<VisualElement>("DefenseShieldFill");
            _shieldValue = root.Q<Label>("DefenseShieldValue");
            _shieldCountdown = root.Q<Label>("DefenseShieldCountdown");
            _shieldPower = root.Q<Label>("DefenseShieldPower");
            _shieldStats = root.Q<Label>("DefenseShieldStats");
            _shieldRule = root.Q<Label>("DefenseShieldRule");
            _trapSection = root.Q<VisualElement>("DefenseTrapSection");
            _secTrap = root.Q<Label>("DefenseSecTrap");
            _firmware = root.Q<DropdownField>("DefenseTrapFirmware");
            _patternTitle = root.Q<Label>("DefenseTrapPatternTitle");
            _patternLine = root.Q<Button>("DefensePatternLine");
            _patternArea = root.Q<Button>("DefensePatternArea");
            _trapSupply = root.Q<Label>("DefenseTrapSupply");
            _trapStats = root.Q<Label>("DefenseTrapStats");
            _trapHint = root.Q<Label>("DefenseTrapHint");
            _barrierSection = root.Q<VisualElement>("DefenseBarrierSection");
            _secBarrier = root.Q<Label>("DefenseSecBarrier");
            _barrierRule = root.Q<Label>("DefenseBarrierRule");
            _footer = root.Q<Label>("DefenseFooter");

            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _patternLine.clicked += () => ClickPattern(DefenseCatalog.PatternLine);
            _patternArea.clicked += () => ClickPattern(DefenseCatalog.PatternArea);
            _firmware.RegisterValueChangedCallback(_ => OnFirmwareChanged());
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
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
                GuidanceHooks.Raise(GuidanceHooks.DefensePanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                SetMessage(string.Empty, false);
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

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        public void ClickPattern(int pattern) => Show(DefenseService.TrySetTrapPattern(CampaignSession.Current, BuildingId, pattern));

        /// <summary>固件下拉选第 <paramref name="index"/> 项（与玩家点选同一回调：选中即生效）。</summary>
        public void SelectFirmware(int index)
        {
            if (index >= 0 && index < _firmware.choices.Count)
            {
                _firmware.index = index;
            }
        }

        private void OnFirmwareChanged()
        {
            if (_suppress)
            {
                return;
            }
            int i = _firmware.index;
            if (i < 0 || i >= _firmwareIds.Count)
            {
                return;
            }
            DefenseRecord r = DefenseService.Find(CampaignSession.Current, BuildingId);
            if (r != null && r.TrapFirmware == _firmwareIds[i])
            {
                return; // 选的就是现在装的：不算一次操作
            }
            Show(DefenseService.TrySetTrapFirmware(CampaignSession.Current, BuildingId, _firmwareIds[i]));
        }

        public void OpenCodex() => MechanicCodex.Open(_kind == DefenseKind.Shield ? "codex.defense.shield" : _kind == DefenseKind.Trap ? "codex.defense.trap" : "codex.defense.barrier");

        private void Show(DefenseOpResult r)
        {
            SetMessage(r.Message, !r.Ok);
            if (!r.Ok)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _key = 0;
            Refresh(force: true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("tu-message-error", error);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            BuildingRecord b = HomeGridService.FindBuilding(s, BuildingId);
            int key = HashCode.Combine(DefenseService.Revision, (int)GameText.Language, GameSettings.Revision, BuildingId,
                b?.ConstructionState ?? 0, b?.PowerState ?? 0, s != null ? s.GetHashCode() : 0, FirmwareKinds.Revision);
            if (force || key != _key)
            {
                _key = key;
                RebuildStructure(s, b);
                _nextReadings = 0f;
            }
            float now = Time.unscaledTime;
            if (force || now >= _nextReadings || !Application.isPlaying)
            {
                _nextReadings = now + ReadingsInterval;
                RefreshReadings(s, b);
            }
        }

        private void RebuildStructure(CampaignState s, BuildingRecord b)
        {
            _suppress = true;
            try
            {
                _kind = b != null ? DefenseCatalog.KindOf(b.BuildingTypeId) : DefenseKind.None;
                _title.text = GameText.Format("defense.panel.title", b != null ? BuildingOps.NameOf(b) : string.Empty);
                _close.text = GameText.Get("defense.panel.close");
                _help.text = GameText.Get("prod.panel.help");
                _secShield.text = GameText.Get("defense.panel.sec.shield");
                _secTrap.text = GameText.Get("defense.panel.sec.trap");
                _secBarrier.text = GameText.Get("defense.panel.sec.barrier");
                _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("defense.panel.footer"));
                _shieldSection.EnableInClassList("uk-hidden", _kind != DefenseKind.Shield);
                _trapSection.EnableInClassList("uk-hidden", _kind != DefenseKind.Trap);
                _barrierSection.EnableInClassList("uk-hidden", _kind != DefenseKind.Barrier && _kind != DefenseKind.Gate);
                _shieldRule.text = GameText.Format("defense.panel.shield_rule", Mathf.RoundToInt(OverloadSeconds()));
                _barrierRule.text = GameText.Get(_kind == DefenseKind.Gate ? "defense.panel.gate_rule" : "defense.panel.barrier_rule");
                _trapHint.text = GameText.Get("defense.panel.trap_hint");
                _patternTitle.text = GameText.Get("defense.panel.trap_pattern");
                _firmware.label = GameText.Get("defense.panel.trap_firmware");

                // 固件下拉：能装进陷阱发射器的（选中项 = 现在装的；现在装的不能用了也列出来，选别的就换掉）。
                _firmwareIds.Clear();
                var choices = new List<string>();
                // 复审修复（P2）：面板只读读数，不补记录（补记录只在对账 / 玩家操作入口）。
                bool isTrap = _kind == DefenseKind.Trap && b != null && DefenseService.TryGetTrapReadout(s, b.BuildingId, out _trapRo);
                string fitted = isTrap ? _trapRo.FirmwareId : null;
                if (isTrap)
                {
                    DefenseService.TrapChoices(s, _firmwareIds);
                    if (!string.IsNullOrEmpty(fitted) && !_firmwareIds.Contains(fitted))
                    {
                        _firmwareIds.Insert(0, fitted);
                    }
                    foreach (string id in _firmwareIds)
                    {
                        string field = DefenseCatalog.TryGetTrap(id, out TrapProfileDef d) ? d.FieldName : string.Empty;
                        choices.Add(GameText.Format("defense.panel.trap_row", FirmwareKinds.DisplayName(id) ?? id, field));
                    }
                }
                DropdownChoices.Apply(_firmware, choices, GameText.Get("defense.panel.trap_none"));
                int cur = isTrap && !string.IsNullOrEmpty(fitted) ? _firmwareIds.IndexOf(fitted) : -1;
                if (choices.Count > 0 && cur >= 0)
                {
                    _firmware.SetValueWithoutNotify(_firmware.choices[cur]);
                }
                else if (choices.Count > 0)
                {
                    _firmware.SetValueWithoutNotify(string.Empty); // 没装固件：不预选（选中任何一项都算一次操作）
                }
                int pattern = isTrap ? _trapRo.Pattern : DefenseCatalog.PatternLine;
                SetPatternButton(_patternLine, DefenseCatalog.PatternName(DefenseCatalog.PatternLine), pattern == DefenseCatalog.PatternLine, isTrap);
                SetPatternButton(_patternArea, DefenseCatalog.PatternName(DefenseCatalog.PatternArea), pattern == DefenseCatalog.PatternArea, isTrap);
            }
            finally
            {
                _suppress = false;
            }
        }

        private TrapReadout _trapRo;

        /// <summary>复审修复：过载时长从吸收状态的耗尽去向读（不认状态 ID）。</summary>
        private static float OverloadSeconds() => DefenseCatalog.OverloadSeconds;

        private static void SetPatternButton(Button btn, string text, bool selected, bool enabled)
        {
            btn.text = (selected ? "● " : string.Empty) + text;
            btn.EnableInClassList("tu-mode-selected", selected);
            btn.SetEnabled(enabled);
        }

        private void RefreshReadings(CampaignState s, BuildingRecord b)
        {
            if (b == null || !DefenseService.IsDefense(b))
            {
                _state.text = GameText.Get("defense.reason.not_found");
                _status.text = string.Empty;
                _durability.text = string.Empty;
                return;
            }
            BuildingStatus st = BuildingStatusService.Evaluate(s, b);
            _state.text = GameText.Get(BuildingOps.UiStatusNameKey(st.Kind));
            _status.text = st.Reason;
            _durability.text = GameText.Format("defense.panel.durability", Mathf.RoundToInt(DefenseService.DurabilityOf(s, b)),
                Mathf.RoundToInt(BuildingOps.MaxDurability(b.BuildingTypeId)));
            if (_kind == DefenseKind.Shield && DefenseService.TryGetShieldReadout(s, BuildingId, out ShieldReadout ro))
            {
                float pct = ro.MaxHp > 0f ? Mathf.Clamp01(ro.Hp / ro.MaxHp) * 100f : 0f;
                ShieldFillPercent = pct;
                _shieldFill.style.width = Length.Percent(pct); // 数据驱动的运行时数值（UI Toolkit 红线 2 允许）
                _shieldFill.EnableInClassList("df-bar-fill-down", !ro.Absorbs);
                _shieldValue.text = GameText.Format("defense.panel.shield_value", Mathf.RoundToInt(ro.Hp), Mathf.RoundToInt(ro.MaxHp), ro.StateName ?? string.Empty);
                _shieldCountdown.text = ro.SecondsLeft >= 0f
                    ? GameText.Format("defense.panel.shield_countdown", ro.StateDescription ?? string.Empty, Mathf.CeilToInt(ro.SecondsLeft))
                    : GameText.Format("defense.panel.shield_no_countdown", ro.StateDescription ?? string.Empty);
                _shieldPower.text = GameText.Format("defense.panel.shield_power", Campaign.Regions.HomeValleyPowerGrid.Num(ro.PowerDemand),
                    Campaign.Regions.HomeValleyPowerGrid.Num(ro.BasePower), Mathf.RoundToInt(ro.LoadDps), ro.ExtraPower.ToString("0.#", CultureInfo.InvariantCulture));
                _shieldStats.text = GameText.Format("defense.panel.shield_stats", ro.Radius.ToString("0.#", CultureInfo.InvariantCulture), Mathf.RoundToInt((float)ro.Absorbed), ro.Overloads);
            }
            if (_kind == DefenseKind.Trap && DefenseService.TryGetTrapReadout(s, BuildingId, out TrapReadout tr))
            {
                _trapSupply.text = GameText.Format("defense.panel.trap_supply", string.IsNullOrEmpty(tr.FirmwareId) ? GameText.Get("bs.reason.trap_no_firmware") : tr.SupplyLine);
                _trapSupply.EnableInClassList("tu-readings-warn", tr.SupplyShort);
                // 复审修复：场地已满（陷阱场地上限）时写明上一轮几块没铺、怎么办（B06 / B12）。
                _trapStats.text = GameText.Format("defense.panel.trap_stats", tr.Lays) + (tr.FieldsRefused > 0 ? "\n" + tr.FieldsFullLine : string.Empty);
                _trapStats.EnableInClassList("tu-readings-warn", tr.FieldsRefused > 0);
            }
        }
    }
}
