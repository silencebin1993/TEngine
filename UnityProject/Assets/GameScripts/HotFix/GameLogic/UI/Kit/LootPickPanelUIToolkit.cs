using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG8-LOOT-02（FG08 FGR-LOOT-020～022；FG13 FGU-34“三选一数据包：候选卡片、比较、货舱占用”）：待分拣数据包面板。
    /// - 入口：顶栏“待分拣 ×N”（<see cref="WorldBarHudUIToolkit"/>，只在有数据包时显示）。<b>从不自己弹出</b>（FGR-LOOT-020“不在战斗中弹出”）。
    /// - 顶部：第几个 / 共几个（上一个 / 下一个）、来源、携带者或在地上的位置；现在不能分拣时写原因并给“暂停并分拣”（进入战略暂停，FGR-LOOT-020）；
    ///   选中的去向（入库 / 装进货舱）与货舱读数。
    /// - 三张候选卡：类型 · 稀有度 · 协议 · 占货舱，从没见过的标“新”，与已有同类物品的对比，选它时没选的转成多少技术数据，“选这个”。
    ///   被拒（货舱 / 仓库放不下）时写明原因，数据包原样留着。
    /// - 下部“货舱里（分拣得到的）”：每份一行 + “丢弃”（落在机器旁边，可以再捡回）。
    /// 模态；Esc / 关闭 / 点遮罩关闭。刷新：数据包版本、物资版本、暂停状态、语言、所选序号变化或游戏时间每过 1 秒才重建；O(数据包数 + 货舱行数)。
    /// </summary>
    public sealed class LootPickPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30060;
        private const int Cards = 3;

        public static LootPickPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Button _prev;
        private Button _next;
        private Button _pause;
        private Label _counter;
        private Label _status;
        private Label _reason;
        private Label _where;
        private Label _cargo;
        private Label _empty;
        private VisualElement _cards;
        private readonly VisualElement[] _card = new VisualElement[Cards];
        private readonly Label[] _new = new Label[Cards];
        private readonly Label[] _name = new Label[Cards];
        private readonly Label[] _meta = new Label[Cards];
        private readonly Label[] _cmp = new Label[Cards];
        private readonly Label[] _tech = new Label[Cards];
        private readonly Button[] _choose = new Button[Cards];
        private Label _message;
        private Label _heldTitle;
        private ScrollView _heldList;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<(VisualElement Row, Label Label, Button Store, Button Discard)> _rows = new List<(VisualElement, Label, Button, Button)>();
        private readonly List<string> _heldIds = new List<string>();
        private int _index;
        private string _messageText;
        private int _key;

        protected override string UxmlLocation => "LootPickPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int Index => _index;
        public string CurrentPacketId { get; private set; }
        public string CounterText => _counter?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string ReasonText => _reason != null && !_reason.ClassListContains("uk-hidden") ? _reason.text : string.Empty;
        public string WhereText => _where?.text ?? string.Empty;
        public string CargoText => _cargo?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public bool CardsVisible => _cards != null && !_cards.ClassListContains("uk-hidden");
        public string CardName(int i) => i >= 0 && i < Cards ? _name[i]?.text ?? string.Empty : string.Empty;
        public string CardMeta(int i) => i >= 0 && i < Cards ? _meta[i]?.text ?? string.Empty : string.Empty;
        public string CardCompare(int i) => i >= 0 && i < Cards ? _cmp[i]?.text ?? string.Empty : string.Empty;
        public string CardTech(int i) => i >= 0 && i < Cards ? _tech[i]?.text ?? string.Empty : string.Empty;
        public bool CardIsNew(int i) => i >= 0 && i < Cards && _new[i] != null && !_new[i].ClassListContains("uk-hidden");
        public bool CardHasClass(int i, string cls) => i >= 0 && i < Cards && _card[i] != null && _card[i].ClassListContains(cls);
        public bool ChooseEnabled(int i) => i >= 0 && i < Cards && _choose[i] != null && _choose[i].enabledSelf;
        public Button ChooseButton(int i) => i >= 0 && i < Cards ? _choose[i] : null;
        public Button PrevButton => _prev;
        public Button NextButton => _next;
        public Button PauseButton => _pause;
        public bool PauseVisible => _pause != null && !_pause.ClassListContains("uk-hidden");
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public int HeldRowCount => _heldIds.Count;
        public string HeldRowText(int i) => i >= 0 && i < _heldIds.Count ? _rows[i].Label.text : string.Empty;
        public Button HeldDiscardButton(int i) => i >= 0 && i < _heldIds.Count ? _rows[i].Discard : null;
        public Button HeldStoreButton(int i) => i >= 0 && i < _heldIds.Count ? _rows[i].Store : null;
        public bool HeldStoreEnabled(int i) => i >= 0 && i < _heldIds.Count && _rows[i].Store.enabledSelf;

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
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开待分拣面板（玩家点顶栏计数）。已开着就保持。</summary>
        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            if (!IsOpen)
            {
                Instance.SetOpen(true);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open();
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("LootPickRow");
            _templateLoading = false;
            if (this == null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            _rowTemplate = t;
            if (t == null)
            {
                Log.Error("[LootPickPanelUIToolkit] 加载 LootPickRow 失败，货舱物品行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("LootPickRoot");
            _title = root.Q<Label>("LootPickTitle");
            _help = root.Q<Button>("LootPickHelp");
            _close = root.Q<Button>("LootPickClose");
            _prev = root.Q<Button>("LootPickPrev");
            _next = root.Q<Button>("LootPickNext");
            _pause = root.Q<Button>("LootPickPause");
            _counter = root.Q<Label>("LootPickCounter");
            _status = root.Q<Label>("LootPickStatus");
            _reason = root.Q<Label>("LootPickReason");
            _where = root.Q<Label>("LootPickWhere");
            _cargo = root.Q<Label>("LootPickCargo");
            _empty = root.Q<Label>("LootPickEmpty");
            _cards = root.Q<VisualElement>("LootPickCards");
            for (int i = 0; i < Cards; i++)
            {
                _card[i] = root.Q<VisualElement>("LootPickCard" + i);
                _new[i] = root.Q<Label>("LootPickNew" + i);
                _name[i] = root.Q<Label>("LootPickName" + i);
                _meta[i] = root.Q<Label>("LootPickMeta" + i);
                _cmp[i] = root.Q<Label>("LootPickCmp" + i);
                _tech[i] = root.Q<Label>("LootPickTech" + i);
                _choose[i] = root.Q<Button>("LootPickChoose" + i);
                int index = i;
                _choose[i].clicked += () => Choose(index);
            }
            _message = root.Q<Label>("LootPickMessage");
            _heldTitle = root.Q<Label>("LootPickHeldTitle");
            _heldList = root.Q<ScrollView>("LootPickHeldList");
            _footer = root.Q<Label>("LootPickFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _prev.clicked += () => Step(-1);
            _next.clicked += () => Step(1);
            _pause.clicked += PauseForSorting;
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _messageText = null;
                _key = 0;
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

        public void OpenCodex() => MechanicCodex.Open("codex.expedition.pick");

        public void Step(int delta)
        {
            int n = LootPickService.Count(CampaignSession.Current);
            if (n <= 0)
            {
                return;
            }
            _index = ((_index + delta) % n + n) % n;
            _messageText = null;
            Refresh(force: true);
        }

        /// <summary>“暂停并分拣”：进入战略暂停（与顶栏暂停同一个开关），之后在野外也能分拣。</summary>
        public void PauseForSorting()
        {
            if (!GameClock.Paused)
            {
                GameClock.SetPaused(true);
            }
            _messageText = null;
            Refresh(force: true);
        }

        /// <summary>选当前数据包的第 <paramref name="i"/> 个候选（与点“选这个”同一条路）。</summary>
        public void Choose(int i)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || string.IsNullOrEmpty(CurrentPacketId))
            {
                return;
            }
            LootPickService.PickResult r = LootPickService.TryPick(s, CurrentPacketId, i);
            _messageText = r.Message;
            if (!r.Success)
            {
                GameLogic.Campaign.Feedback.FeedbackCues.Raise(GameLogic.Campaign.Feedback.FeedbackCueId.Denied, r.Message);
            }
            Refresh(force: true);
        }

        public void Discard(int row)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || row < 0 || row >= _heldIds.Count)
            {
                return;
            }
            if (LootPickService.TryDiscard(s, _heldIds[row], out string msg))
            {
                _messageText = msg;
            }
            Refresh(force: true);
        }

        /// <summary>货舱里第 <paramref name="row"/> 份分拣物品手动入库（机器在家园 / 仓库旁时；“远征卸货”规则关着时回来的货走这里）。</summary>
        public void Store(int row)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || row < 0 || row >= _heldIds.Count)
            {
                return;
            }
            bool ok = LootPickService.TryStoreHeld(s, _heldIds[row], out string msg);
            _messageText = msg;
            if (!ok && !string.IsNullOrEmpty(msg))
            {
                GameLogic.Campaign.Feedback.FeedbackCues.Raise(GameLogic.Campaign.Feedback.FeedbackCueId.Denied, msg);
            }
            Refresh(force: true);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine(LootPickService.Revision, HomeInventory.Revision, GameClock.Paused, _index, (int)GameText.Language,
                HashCode.Combine(GameSettings.Revision, s != null ? s.GetHashCode() : 0, GameClock.Ticks / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0, _messageText));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("loot.pick.title");
            _help.text = GameText.Get("loot.pick.help");
            _close.text = GameText.Get("loot.pick.close");
            _prev.text = GameText.Get("loot.pick.prev");
            _next.text = GameText.Get("loot.pick.next");
            _pause.text = GameText.Get("loot.pick.pause");
            _footer.text = GameText.Get("loot.pick.footer");
            _heldTitle.text = GameText.Get("loot.pick.held.title");

            IReadOnlyList<LootPackageRecord> packets = s != null ? LootPickService.Packets(s) : Array.Empty<LootPackageRecord>();
            int n = packets.Count;
            if (_index >= n)
            {
                _index = Math.Max(0, n - 1);
            }
            LootPackageRecord p = n > 0 ? packets[_index] : null;
            CurrentPacketId = p?.PacketId;
            bool empty = p == null;
            _empty.EnableInClassList("uk-hidden", !empty);
            _cards.EnableInClassList("uk-hidden", empty);
            _prev.SetEnabled(n > 1);
            _next.SetEnabled(n > 1);
            if (empty)
            {
                _empty.text = GameText.Get("loot.pick.empty");
                _counter.text = string.Empty;
                _status.text = string.Empty;
                _where.text = string.Empty;
                _cargo.text = string.Empty;
                _reason.EnableInClassList("uk-hidden", true);
                _pause.EnableInClassList("uk-hidden", true);
            }
            else
            {
                _counter.text = GameText.Format("loot.pick.counter", _index + 1, n);
                _status.text = LootPickService.OriginText(p) + "  ·  " + LootPickService.HolderText(p);
                bool can = LootPickService.CanProcess(s, p, out string why, out bool atDepot);
                _reason.EnableInClassList("uk-hidden", can);
                if (!can)
                {
                    _reason.text = GameText.Get(why);
                }
                // 不能分拣、携带机器还在（不在地上）、还没暂停：给“暂停并分拣”。
                _pause.EnableInClassList("uk-hidden", can || GameClock.Paused || why == "loot.pick.reason.ground" || why == LootPickService.ReasonGoHome);
                _where.text = can ? GameText.Get(atDepot ? "loot.pick.where.depot" : "loot.pick.where.cargo") : string.Empty;
                _cargo.text = can && !atDepot ? LootPickService.CargoText(s, p) : string.Empty;
                for (int i = 0; i < Cards; i++)
                {
                    LootCandidateRecord c = i < p.Candidates.Length ? p.Candidates[i] : null;
                    _card[i].EnableInClassList("uk-hidden", c == null);
                    if (c == null)
                    {
                        continue;
                    }
                    _name[i].text = LootPickService.CandidateName(c);
                    _meta[i].text = LootPickService.MetaText(s, c);
                    _cmp[i].text = LootPickService.CompareText(s, c);
                    _tech[i].text = GameText.Format("loot.pick.leftover", LootPickService.LeftoverTechIf(p, i));
                    bool isNew = LootPickService.IsNew(s, c);
                    _new[i].EnableInClassList("uk-hidden", !isNew);
                    _new[i].text = GameText.Get("loot.pick.new");
                    _card[i].EnableInClassList("lp-card-fine", c.Tier == "fine");
                    _card[i].EnableInClassList("lp-card-rare", c.Tier == "rare");
                    _card[i].EnableInClassList("lp-card-prototype", c.Tier == "prototype");
                    _choose[i].text = GameText.Get("loot.pick.choose");
                    _choose[i].SetEnabled(can);
                }
            }
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            _message.text = _messageText ?? string.Empty;
            RebuildHeld(s);
        }

        private void RebuildHeld(CampaignState s)
        {
            IReadOnlyList<LootHeldRecord> held = s != null ? LootPickService.Held(s) : Array.Empty<LootHeldRecord>();
            _heldTitle.EnableInClassList("uk-hidden", held.Count == 0);
            while (_rowTemplate != null && _rows.Count < held.Count)
            {
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("lp-row-host");
                _heldList.Add(host);
                Button discard = host.Q<Button>("LpDiscard");
                Button store = host.Q<Button>("LpStore");
                int row = _rows.Count;
                discard.clicked += () => Discard(row);
                store.clicked += () => Store(row);
                _rows.Add((host.Q<VisualElement>("LpRow"), host.Q<Label>("LpLabel"), store, discard));
            }
            _heldIds.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < held.Count;
                (VisualElement row, Label label, Button store, Button discard) = _rows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                _heldIds.Add(held[i].HeldId);
                label.text = LootPickService.HeldRowText(held[i]);
                discard.text = GameText.Get("loot.pick.discard");
                store.text = GameText.Get("loot.pick.store");
                store.SetEnabled(LootPickService.CanStoreHeld(s, held[i]));
            }
        }
    }
}
