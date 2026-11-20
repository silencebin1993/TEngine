using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG14-SBX-02（FG12 FGR-SBX-011、第 3 节“沙盒的设置在开局后可以部分修改，修改会记录在存档里”）：沙盒设置面板。
    /// - 开局后能改的四项（突袭 / 静默夜 / 天气 / 事件频率）：每项一排按钮，当前档“▸”前缀（颜色之外的标记，B15）；点了立即生效、记一条修改记录（<see cref="SandboxService.TryChange"/>）。
    /// - 开局时定下的项（起始资源 / 研发 / 区域 / 敌方强度 / 起始区）与世界设置、种子：只读一行，写明“开局时定下，不能再改”。
    /// - 关键材料换取（FGR-SBX-011“关掉了剧情，关键材料改为在研发树里用研究点换取”）：每件一行（名称 · 用途 · 价格 · 状态）+“换取”；研究点不够 / 已经有了写明原因（按钮禁用）。
    /// - 修改记录（最近的在前）。暂停菜单“沙盒设置”与研发树“关键材料换取”打开；Esc / 关闭 / 点遮罩关闭；模态。刷新只在版本 / 语言 / 研究点变化时重建，O(行数)。
    /// </summary>
    public sealed class SandboxPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30079; // 与坚守记录 / 事件历史同层：都是暂停菜单（或研发树）的子面板，不会同时打开

        public static SandboxPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _status;
        private Button _close;
        private Label _runtimeTitle;
        private VisualElement _rows;
        private Label _fixed;
        private Label _exchangeTitle;
        private Label _exchangeHint;
        private VisualElement _exchangeList;
        private Label _changesTitle;
        private Label _changesEmpty;
        private VisualElement _changeList;
        private Label _feedback;
        private readonly List<List<Button>> _optionButtons = new List<List<Button>>();
        private readonly List<string> _optionIds = new List<string>();
        private readonly List<Button> _exchangeButtons = new List<Button>();
        private readonly List<Label> _exchangeLabels = new List<Label>();
        private readonly List<Label> _changeRows = new List<Label>();
        private int _key;

        protected override string UxmlLocation => "SandboxPanel";
        protected override int SortingOrder => Order;

        // ── 自检 / 冒烟读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string StatusText => _status?.text ?? string.Empty;
        public string FixedText => _fixed?.text ?? string.Empty;
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public int RuntimeRowCount => _optionButtons.Count;
        public Button OptionButton(string id, int option)
        {
            int i = _optionIds.IndexOf(id);
            return i >= 0 && option >= 0 && option < _optionButtons[i].Count ? _optionButtons[i][option] : null;
        }
        public int ExchangeRowCount { get; private set; }
        public Button ExchangeButton(int i) => i >= 0 && i < ExchangeRowCount ? _exchangeButtons[i] : null;
        public string ExchangeText(int i) => i >= 0 && i < ExchangeRowCount ? _exchangeLabels[i].text : string.Empty;
        public int ChangeRowCount { get; private set; }
        public string ChangeText(int i) => i >= 0 && i < ChangeRowCount ? _changeRows[i].text : string.Empty;
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

        /// <summary>暂停菜单“沙盒设置” / 研发树“关键材料换取”。剧情档不打开。</summary>
        public static void Open()
        {
            if (!SandboxService.IsSandbox(CampaignSession.Current))
            {
                return;
            }
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
            _root = root.Q<VisualElement>("SandboxRoot");
            _title = root.Q<Label>("SandboxTitle");
            _status = root.Q<Label>("SandboxStatus");
            _close = root.Q<Button>("SandboxClose");
            _runtimeTitle = root.Q<Label>("SandboxRuntimeTitle");
            _rows = root.Q<VisualElement>("SandboxRows");
            _fixed = root.Q<Label>("SandboxFixed");
            _exchangeTitle = root.Q<Label>("SandboxExchangeTitle");
            _exchangeHint = root.Q<Label>("SandboxExchangeHint");
            _exchangeList = root.Q<VisualElement>("SandboxExchangeList");
            _changesTitle = root.Q<Label>("SandboxChangesTitle");
            _changesEmpty = root.Q<Label>("SandboxChangesEmpty");
            _changeList = root.Q<VisualElement>("SandboxChangeList");
            _feedback = root.Q<Label>("SandboxFeedback");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            BuildRows();
            _key = 0;
        }

        private void BuildRows()
        {
            _rows.Clear();
            _optionButtons.Clear();
            _optionIds.Clear();
            foreach (SandboxService.Def def in SandboxService.Defs)
            {
                if (!def.Runtime)
                {
                    continue;
                }
                var row = new VisualElement();
                row.AddToClassList("wg-axis-row");
                var label = new Label { name = "SandboxLabel_" + def.Id };
                label.AddToClassList("wg-axis-label");
                row.Add(label);
                var buttons = new List<Button>();
                for (int o = 0; o < def.Options; o++)
                {
                    string id = def.Id;
                    int option = o;
                    var b = new Button(() => Choose(id, option)) { name = $"SandboxOpt_{def.Id}_{o}" };
                    b.AddToClassList("mw-btn");
                    b.AddToClassList("wg-level");
                    row.Add(b);
                    buttons.Add(b);
                }
                _optionButtons.Add(buttons);
                _optionIds.Add(def.Id);
                _rows.Add(row);
            }
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _feedback.text = string.Empty;
                _key = 0;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        /// <summary>选某项的某档（按钮与自检同一入口）。</summary>
        public void Choose(string id, int option)
        {
            CampaignState state = CampaignSession.Current;
            SandboxService.ChangeResult r = SandboxService.TryChange(state, id, option);
            _feedback.text = r == SandboxService.ChangeResult.Changed
                ? GameText.Format("sandbox.panel.changed", SandboxService.SettingName(id), SandboxService.OptionName(id, option))
                : r == SandboxService.ChangeResult.Unchanged ? string.Empty : GameText.Get("sandbox.panel.locked");
            Campaign.Feedback.FeedbackCues.Raise(r == SandboxService.ChangeResult.Changed ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.UiClick);
            _key = 0;
            Refresh();
        }

        /// <summary>换一件关键材料（按钮与自检同一入口）。</summary>
        public bool Exchange(string keyId)
        {
            CampaignState state = CampaignSession.Current;
            bool ok = SandboxService.TryExchange(state, keyId, out string reason);
            _feedback.text = ok ? GameText.Get("sandbox.panel.exchanged") : GameText.Get(reason ?? "sandbox.exchange.deny.unknown");
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied);
            _key = 0;
            Refresh();
            return ok;
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
            Refresh();
        }

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int key = System.HashCode.Combine(SandboxService.Revision, Campaign.Env.EventDirectorService.Frequency(state), (int)GameText.Language,
                state != null ? state.GetHashCode() : 0, SandboxService.Points(state), Campaign.Economy.HomeInventory.Revision);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("sandbox.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            _status.text = GameText.Format("save.card.sandbox_settings", SandboxService.Describe(SandboxService.Code(state)));
            _runtimeTitle.text = GameText.Get("sandbox.panel.runtime");
            for (int i = 0; i < _optionIds.Count; i++)
            {
                string id = _optionIds[i];
                int cur = SandboxService.Get(state, id);
                _rows[i].Q<Label>("SandboxLabel_" + id).text = SandboxService.SettingName(id);
                for (int o = 0; o < _optionButtons[i].Count; o++)
                {
                    string name = SandboxService.OptionName(id, o);
                    _optionButtons[i][o].text = o == cur ? "▸ " + name : name;
                    _optionButtons[i][o].EnableInClassList("wg-level-selected", o == cur);
                }
            }
            var fixedParts = new List<string>();
            foreach (SandboxService.Def def in SandboxService.Defs)
            {
                if (!def.Runtime)
                {
                    fixedParts.Add(GameText.Format("world.setting.pair", SandboxService.SettingName(def.Id), SandboxService.OptionName(def.Id, SandboxService.Get(state, def.Id))));
                }
            }
            fixedParts.Add(GameText.Format("world.setting.pair", GameText.Get("world.setting.axis.start"),
                GameText.Get(state?.World != null && state.World.NoStartGuarantee ? "sandbox.opt.start_off" : "sandbox.opt.start_kept")));
            _fixed.text = GameText.Format("sandbox.panel.fixed", string.Join(GameText.Get("world.setting.sep"), fixedParts),
                Campaign.WorldGen.WorldGenService.DescribeSettings(state), state?.World?.WorldSeed ?? 0);

            _exchangeTitle.text = GameText.Get("sandbox.panel.exchange");
            _exchangeHint.text = GameText.Format("sandbox.panel.exchange_hint", SandboxService.Points(state));
            IReadOnlyList<GameConfig.fg.KeyMaterial> keys = QuestCatalog.KeyMaterials;
            while (_exchangeButtons.Count < keys.Count)
            {
                var row = new VisualElement();
                row.AddToClassList("wg-axis-row");
                var l = new Label();
                l.AddToClassList("rl-row");
                row.Add(l);
                int idx = _exchangeButtons.Count;
                var b = new Button(() => OnExchangeClicked(idx)) { name = "SandboxExchange_" + idx };
                b.AddToClassList("mw-btn");
                row.Add(b);
                _exchangeList.Add(row);
                _exchangeLabels.Add(l);
                _exchangeButtons.Add(b);
            }
            for (int i = 0; i < _exchangeButtons.Count; i++)
            {
                bool shown = i < keys.Count;
                _exchangeButtons[i].parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                GameConfig.fg.KeyMaterial k = keys[i];
                string block = SandboxService.ExchangeBlock(state, k);
                _exchangeLabels[i].text = GameText.Format("sandbox.panel.exchange_row", GameText.Get(k.NameKey), GameText.Get(k.UsageKey), SandboxService.ExchangeCost(k),
                    block == null ? GameText.Get("sandbox.exchange.ready") : GameText.Get(block));
                _exchangeButtons[i].text = GameText.Get("sandbox.panel.exchange_btn");
                _exchangeButtons[i].SetEnabled(block == null);
            }
            ExchangeRowCount = keys.Count;

            _changesTitle.text = GameText.Get("sandbox.panel.changes");
            IReadOnlyList<SandboxChangeRecord> hist = SandboxService.Changes(state);
            while (_changeRows.Count < hist.Count)
            {
                var l = new Label();
                l.AddToClassList("rl-row");
                _changeList.Add(l);
                _changeRows.Add(l);
            }
            for (int i = 0; i < _changeRows.Count; i++)
            {
                bool shown = i < hist.Count;
                _changeRows[i].EnableInClassList("uk-hidden", !shown);
                if (shown)
                {
                    _changeRows[i].text = SandboxService.ChangeRow(hist[hist.Count - 1 - i]); // 最近的在前
                }
            }
            ChangeRowCount = hist.Count;
            _changesEmpty.EnableInClassList("uk-hidden", hist.Count > 0);
            _changesEmpty.text = hist.Count == 0 ? GameText.Get("sandbox.panel.changes_empty") : string.Empty;
        }

        private void OnExchangeClicked(int i)
        {
            IReadOnlyList<GameConfig.fg.KeyMaterial> keys = QuestCatalog.KeyMaterials;
            if (i >= 0 && i < keys.Count)
            {
                Exchange(keys[i].Id);
            }
        }
    }
}
