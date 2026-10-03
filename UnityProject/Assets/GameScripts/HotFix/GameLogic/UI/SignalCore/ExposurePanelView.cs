using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using UnityEngine.UIElements;

namespace GameLogic.UI.SignalCore
{
    /// <summary>
    /// FG1-SIG-06（FG01 FGR-SIG-070、071；FG13 FGU-44）：暴露面板与 HUD 上的“暴露 N”按钮（与信号核同一个 UIDocument，SignalCorePanel.uxml）。
    /// - 暴露值 / 上限、进度条、三个阈值与下一个阈值会触发什么；
    /// - 最近的来源（新的在上，来源名按当前语言、带时刻与“→ 结算后”），旧档迁移来的标“旧规则”；
    /// - 各阵营贡献（累计增加，明细截断不影响）；怎么计 / 怎么降（数值读调参表）。
    /// 状态：正常 / 空（还没有记录）/ 错误（没有战役）。入口：HUD 按钮、Alt+P（可重绑）；Esc / 同一个键 / 关闭按钮关闭。
    /// 刷新：HUD 每帧只比较整数键；面板打开且键变化时 O(明细条数 ≤ 12 + 阵营数)，与机器数无关（B18）。
    /// </summary>
    public sealed class ExposurePanelView
    {
        public const int RecentShown = 12;

        private readonly object _owner;
        private Button _entry;
        private VisualElement _panel;
        private Label _title;
        private Label _status;
        private Button _help;
        private Button _close;
        private Label _value;
        private VisualElement _barFill;
        private Label _thresholds;
        private Label _next;
        private Label _recentTitle;
        private VisualElement _recentList;
        private Label _recentEmpty;
        private Label _factionTitle;
        private VisualElement _factionList;
        private Label _factionEmpty;
        private Label _rulesTitle;
        private Label _rules;
        private Label _lower;
        private Label _legacy;
        private Label _keyHint;
        private int _hudKey;
        private int _panelKey;

        public bool IsOpen { get; private set; }
        public bool IsBound => _panel != null;

        // ── 自检读点 ─────────────────────────────────────────────────────────────
        public string EntryText => _entry?.text ?? string.Empty;
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public string ValueText => _value?.text ?? string.Empty;
        public string NextText => _next?.text ?? string.Empty;
        public string RulesText => _rules?.text ?? string.Empty;
        public string LowerText => _lower?.text ?? string.Empty;
        public bool RecentEmptyVisible => _recentEmpty != null && !_recentEmpty.ClassListContains("uk-hidden");
        public bool LegacyNoteVisible => _legacy != null && !_legacy.ClassListContains("uk-hidden");
        public int RecentCount => _recentList?.childCount ?? 0;
        public string RecentText(int i) => _recentList != null && i >= 0 && i < _recentList.childCount && _recentList[i] is Label l ? l.text : string.Empty;
        public int FactionCount => _factionList?.childCount ?? 0;
        public string FactionText(int i) => _factionList != null && i >= 0 && i < _factionList.childCount && _factionList[i] is Label l ? l.text : string.Empty;
        public Button EntryButton => _entry;
        public Button CloseButton => _close;

        public ExposurePanelView(object owner)
        {
            _owner = owner;
        }

        public void Bind(VisualElement root, Action toggle)
        {
            _entry = root.Q<Button>("SignalExposureEntry");
            _panel = root.Q<VisualElement>("ExposurePanel");
            _title = root.Q<Label>("ExposureTitle");
            _status = root.Q<Label>("ExposureStatus");
            _help = root.Q<Button>("ExposureHelp");
            _close = root.Q<Button>("ExposureClose");
            _value = root.Q<Label>("ExposureValue");
            _barFill = root.Q<VisualElement>("ExposureBarFill");
            _thresholds = root.Q<Label>("ExposureThresholds");
            _next = root.Q<Label>("ExposureNext");
            _recentTitle = root.Q<Label>("ExposureRecentTitle");
            _recentList = root.Q<VisualElement>("ExposureRecentList");
            _recentEmpty = root.Q<Label>("ExposureRecentEmpty");
            _factionTitle = root.Q<Label>("ExposureFactionTitle");
            _factionList = root.Q<VisualElement>("ExposureFactionList");
            _factionEmpty = root.Q<Label>("ExposureFactionEmpty");
            _rulesTitle = root.Q<Label>("ExposureRulesTitle");
            _rules = root.Q<Label>("ExposureRules");
            _lower = root.Q<Label>("ExposureLower");
            _legacy = root.Q<Label>("ExposureLegacy");
            _keyHint = root.Q<Label>("ExposureKeyHint");
            if (_entry == null || _panel == null)
            {
                return;
            }
            UiEscapeStack.RegisterPage(_owner, _panel);
            _entry.clicked += toggle;
            _close.clicked += () => SetOpen(false);
            UiTooltip.Attach(_entry, () => new TooltipContent
            {
                Title = GameText.Get("exposure.hud.tip_title"),
                Body = GameText.Format("exposure.hud.tip", F0(CampaignExposureLedger.MaxExposure), InputDisplay.ForAction(GameActionId.OpenExposure)),
                Shortcut = GameActionId.OpenExposure,
            });
            UiTooltip.Attach(_help, () => new TooltipContent
            {
                Title = GameText.Get("exposure.panel.title"),
                Body = GameText.Format("exposure.panel.help_tip", F0(CampaignExposureLedger.ThresholdScoutTip),
                    F0(CampaignExposureLedger.ThresholdAdaptationIntel), F0(CampaignExposureLedger.ThresholdCoreReinforcement))
                       + "\n" + GameText.Format("codex.help.tip", GameText.Get("codex.signal.exposure.title")),
            });
            // FG1-HUD-01（DEBT-FG1SIG06-06）：“?”点开图鉴“信号暴露”条目（悬停说明照旧）。
            if (_help != null)
            {
                _help.clicked += () => GameLogic.Progression.MechanicCodex.Open("codex.signal.exposure");
            }
            _hudKey = 0;
            _panelKey = 0;
        }

        public void SetOpen(bool open)
        {
            if (_panel == null || (open == IsOpen && open == PanelVisible))
            {
                return;
            }
            IsOpen = open;
            _panel.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.ExposurePanelFirstOpen);
                InputRouter.PushModal(_owner);
                UiEscapeStack.Push(_owner, () => SetOpen(false));
                _panelKey = 0;
                Refresh(CampaignSession.Current, true);
            }
            else
            {
                InputRouter.PopModal(_owner);
                UiEscapeStack.Remove(_owner);
            }
        }

        /// <summary>每帧：HUD 按钮（整数键不变就不动）；面板打开时键变化才重建。</summary>
        public void Refresh(CampaignState s, bool inWorld)
        {
            if (_entry == null)
            {
                return;
            }
            if (!inWorld && IsOpen)
            {
                SetOpen(false);
            }
            float exposure = s?.SignalExposure ?? 0f;
            int hudKey = HashCode.Combine(s != null, (int)Math.Round(exposure * 10f), (int)GameText.Language, GameSettings.Revision);
            if (hudKey != _hudKey)
            {
                _hudKey = hudKey;
                _entry.text = GameText.Format("exposure.hud.button", F0(exposure));
                _entry.EnableInClassList("sc-hud-exposure-warn", exposure >= CampaignExposureLedger.ThresholdScoutTip && exposure < CampaignExposureLedger.ThresholdAdaptationIntel);
                _entry.EnableInClassList("sc-hud-exposure-high", exposure >= CampaignExposureLedger.ThresholdAdaptationIntel);
            }
            if (!IsOpen)
            {
                return;
            }
            int panelKey = HashCode.Combine(CampaignExposureLedger.Revision, (int)Math.Round(exposure * 100f), s?.SignalExposureEvents?.Length ?? -1,
                (int)GameText.Language, GameSettings.Revision, FirmwareKinds.Revision, s != null);
            if (panelKey == _panelKey)
            {
                return;
            }
            _panelKey = panelKey;
            Rebuild(s);
        }

        private void Rebuild(CampaignState s)
        {
            float max = CampaignExposureLedger.MaxExposure;
            _title.text = GameText.Get("exposure.panel.title");
            _close.text = GameText.Get("exposure.panel.close");
            _help.text = GameText.Get("exposure.panel.help");
            _keyHint.text = GameText.Format("exposure.panel.key_hint", InputDisplay.ForAction(GameActionId.OpenExposure));
            _recentList.Clear();
            _factionList.Clear();
            if (s == null)
            {
                // 错误态：没有战役（例如主菜单）。
                _status.text = string.Empty;
                _value.text = GameText.Get("exposure.panel.no_campaign");
                _barFill.style.width = Length.Percent(0f);
                _thresholds.text = _next.text = _rules.text = _lower.text = string.Empty;
                _recentTitle.text = _factionTitle.text = _rulesTitle.text = string.Empty;
                SetHidden(_recentEmpty, true);
                SetHidden(_factionEmpty, true);
                SetHidden(_legacy, true);
                return;
            }

            float e = s.SignalExposure;
            _status.text = GameText.Format("exposure.panel.value", F0(e), F0(max));
            _value.text = GameText.Format("exposure.panel.value", F1(e), F0(max));
            // 进度条宽度是数据驱动的百分比（UI_PIPELINE：只有这类运行时数值允许直接写 style）。
            _barFill.style.width = Length.Percent(max > 0f ? Math.Max(0f, Math.Min(100f, e / max * 100f)) : 0f);
            _thresholds.text = GameText.Format("exposure.panel.thresholds",
                F0(CampaignExposureLedger.ThresholdScoutTip), GameText.Get("exposure.threshold.scout"),
                F0(CampaignExposureLedger.ThresholdAdaptationIntel), GameText.Get("exposure.threshold.adapt"),
                F0(CampaignExposureLedger.ThresholdCoreReinforcement), GameText.Get("exposure.threshold.reinforce"));
            _next.text = e < CampaignExposureLedger.ThresholdScoutTip
                ? GameText.Format("exposure.panel.next", F0(CampaignExposureLedger.ThresholdScoutTip), GameText.Get("exposure.threshold.scout"))
                : e < CampaignExposureLedger.ThresholdAdaptationIntel
                    ? GameText.Format("exposure.panel.next", F0(CampaignExposureLedger.ThresholdAdaptationIntel), GameText.Get("exposure.threshold.adapt"))
                    : e < CampaignExposureLedger.ThresholdCoreReinforcement
                        ? GameText.Format("exposure.panel.next", F0(CampaignExposureLedger.ThresholdCoreReinforcement), GameText.Get("exposure.threshold.reinforce"))
                        : GameText.Get("exposure.panel.all_passed");

            _recentTitle.text = GameText.Get("exposure.panel.recent_title");
            SignalExposureEventRecord[] recent = CampaignExposureLedger.RecentEvents(s, RecentShown);
            bool anyLegacy = false;
            foreach (SignalExposureEventRecord r in recent)
            {
                bool legacy = ExposureSourceKind.IsLegacy(r.Kind);
                anyLegacy |= legacy;
                var label = new Label(GameText.Format("exposure.panel.recent_item", GameLogic.Core.GameClock.FormatDayTime(r.AtPlaySeconds),
                    CampaignExposureLedger.SourceText(r), Signed(r.Delta), F1(r.ResultingExposure)));
                label.AddToClassList("ex-item");
                label.AddToClassList(r.Delta >= 0f ? "ex-item-up" : "ex-item-down");
                label.EnableInClassList("ex-item-legacy", legacy);
                _recentList.Add(label);
            }
            SetHidden(_recentEmpty, recent.Length > 0);
            _recentEmpty.text = GameText.Get("exposure.panel.recent_empty");
            SetHidden(_legacy, !anyLegacy);
            _legacy.text = GameText.Get("exposure.panel.legacy_note");

            _factionTitle.text = GameText.Get("exposure.panel.faction_title");
            IReadOnlyList<(string Faction, float Added)> factions = CampaignExposureLedger.FactionContributions(s);
            foreach ((string faction, float added) in factions)
            {
                var label = new Label(GameText.Format("exposure.panel.faction_item", CampaignExposureLedger.FactionName(faction), F1(added)));
                label.AddToClassList("ex-item");
                _factionList.Add(label);
            }
            // 己方 / 家园活动单列一行，不算进任何阵营（FG6-DEF-04 选突袭阵营只看敌方阵营）。
            float own = CampaignExposureLedger.OwnActivityAdded(s);
            if (own > 0f)
            {
                var ownLabel = new Label(GameText.Format("exposure.panel.own_item", F1(own)));
                ownLabel.AddToClassList("ex-item");
                ownLabel.AddToClassList("ex-item-own");
                _factionList.Add(ownLabel);
            }
            SetHidden(_factionEmpty, factions.Count > 0);
            _factionEmpty.text = GameText.Get("exposure.panel.faction_empty");

            _rulesTitle.text = GameText.Get("exposure.panel.rules_title");
            _rules.text = GameText.Format("exposure.panel.rules", F1(CampaignExposureLedger.HighPowerCapPerHour), F1(CampaignExposureLedger.CoreFireDelta),
                F1(CampaignExposureLedger.RawFireDelta), F1(CampaignExposureLedger.AlienTechDelta), F1(CampaignExposureLedger.NodeDestroyedDelta));
            _lower.text = GameText.Format("exposure.panel.lower", F1(CampaignExposureLedger.TowerBroadcastOffDeltaPer10Seconds),
                F0(CampaignExposureLedger.TowerBroadcastOffBandwidthPenalty), F1(CampaignExposureLedger.NodeLinkCutDelta));
        }

        public void OnDestroy()
        {
            if (IsOpen)
            {
                InputRouter.PopModal(_owner);
                UiEscapeStack.Remove(_owner);
                IsOpen = false;
            }
            UiEscapeStack.UnregisterPage(_owner);
        }

        private static void SetHidden(VisualElement e, bool hidden) => e?.EnableInClassList("uk-hidden", hidden);

        private static string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);

        private static string F1(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        private static string Signed(float v) => v.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
    }
}
