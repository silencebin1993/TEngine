using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Endgame;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG13-END-06（FG11 FGR-END-031 / 032；FG13 FGU-42 “演出、统计、纪念、继续或返回”）：结局结算与纪念。
    /// - 打开：战役结局已达成、还没选“继续”（<see cref="EndingService.SettlementDue"/>）、世界已载入、没有演出在播 / 待播、没有确认框时自动打开（读档后同样回到这里）；
    ///   打开即 <see cref="EndingService.OpenSettlement"/>：世界暂停、结算期间拒绝任何存档。
    /// - 内容：标题与副标题 → 统计（归还面积、游戏天数、发现的反应、熔合配方、击退的突袭）→ 结语（随统计变化）→ 幸存机器（名字 · 编号 · 型号、经历）与纪念墙（黑匣子陈列馆同一份名单）。
    /// - 按钮：重看结局演出（演出播放器盖在本面板上面，看完回到结算）、继续（后日谈）、返回主菜单（不存档）。Esc 不关闭（必须二选一）。
    /// 分层 30185：所有面板之上、演出播放器 30190 与浮层 30200 之下。开销：只在打开时建一次行（O(机器数)），不开时每帧只做 O(1) 判断（<see cref="EndingService.Reached"/> 按账本数组引用缓存）。
    /// </summary>
    public sealed class EndingSettlementPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30185;

        public static EndingSettlementPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        /// <summary>自检：编辑模式下没有载入的地点也允许自动打开 / 不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _subtitle;
        private Label _statsHead;
        private VisualElement _stats;
        private Label _epilogueHead;
        private VisualElement _epilogue;
        private Label _survivorsHead;
        private VisualElement _survivors;
        private Label _memorialHead;
        private VisualElement _memorial;
        private Label _hint;
        private Button _replay;
        private Button _menu;
        private Button _continue;
        private bool _open;

        protected override string UxmlLocation => "EndingSettlement";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public EndingStats Shown { get; private set; }
        public Button ReplayButton => _replay;
        public Button MenuButton => _menu;
        public Button ContinueButton => _continue;
        public string TitleText => _title?.text ?? string.Empty;
        public List<string> StatTexts => Texts(_stats);
        public List<string> EpilogueTexts => Texts(_epilogue);
        public List<string> SurvivorTexts => Texts(_survivors);
        public List<string> MemorialTexts => Texts(_memorial);

        private static List<string> Texts(VisualElement box)
        {
            var list = new List<string>();
            if (box != null)
            {
                foreach (VisualElement c in box.Children())
                {
                    if (c is Label l)
                    {
                        list.Add(l.text);
                    }
                }
            }
            return list;
        }

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (_open && Instance == this)
            {
                Hide();
                EndingService.AbortSettlement();
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        protected override void OnReady(VisualElement root) => BindView(root);

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("EsRoot");
            _title = root.Q<Label>("EsTitle");
            _subtitle = root.Q<Label>("EsSubtitle");
            _statsHead = root.Q<Label>("EsStatsHead");
            _stats = root.Q<VisualElement>("EsStats");
            _epilogueHead = root.Q<Label>("EsEpilogueHead");
            _epilogue = root.Q<VisualElement>("EsEpilogue");
            _survivorsHead = root.Q<Label>("EsSurvivorsHead");
            _survivors = root.Q<VisualElement>("EsSurvivors");
            _memorialHead = root.Q<Label>("EsMemorialHead");
            _memorial = root.Q<VisualElement>("EsMemorial");
            _hint = root.Q<Label>("EsHint");
            _replay = root.Q<Button>("EsReplay");
            _menu = root.Q<Button>("EsMenu");
            _continue = root.Q<Button>("EsContinue");
            _replay.clicked += Replay;
            _menu.clicked += () =>
            {
                Hide();
                EndingService.ReturnToMenu();
            };
            _continue.clicked += () =>
            {
                Hide();
                EndingService.ContinuePostgame();
            };
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }
            Tick();
        }

        /// <summary>每帧检查（Update 与自检同一入口）。</summary>
        public void Tick()
        {
            CampaignState s = CampaignSession.Current;
            bool worldReady = InWorldOverrideForTests || (s != null && GameRoot.AnyRegionActive);
            if (_open)
            {
                if (!worldReady || !EndingService.SettlementOpen || !EndingService.SettlementDue(s))
                {
                    // 战役被换掉 / 世界卸载：收起，不改存档。
                    Hide();
                    EndingService.AbortSettlement();
                    return;
                }
                // 重看演出期间撤掉吞 Esc 的阻挡层（否则演出页一压栈就被阻挡层当成“遮罩下的页”立即关掉；Esc 先跳过重看），看完再补回。
                UiEscapeStack.SyncBlocking(this, NarrativeService.Playing == null);
                return;
            }
            if (worldReady && EndingService.SettlementDue(s) && NarrativeService.Playing == null && string.IsNullOrEmpty(s.Progress.PendingCutscene)
                && !UiConfirmDialog.IsOpen && EndingService.OpenSettlement(s))
            {
                Show(s);
            }
        }

        private void Show(CampaignState s)
        {
            _open = true;
            _root.EnableInClassList("uk-hidden", false);
            InputRouter.PushModal(this);
            UiEscapeStack.SyncBlocking(this, true); // Esc 被吞掉：不关结算、也不穿透到暂停菜单（与失败页同一做法）
            Render(s);
        }

        private void Hide()
        {
            _open = false;
            if (_root != null)
            {
                _root.EnableInClassList("uk-hidden", true);
            }
            InputRouter.PopModal(this);
            UiEscapeStack.SyncBlocking(this, false);
        }

        /// <summary>重看结局演出：演出播放器盖在结算上面（重看不存档、不改资料库），看完回到结算。</summary>
        public void Replay()
        {
            CampaignState s = CampaignSession.Current;
            if (!_open || NarrativeCutscenePlayerUIToolkit.Instance == null)
            {
                return;
            }
            UiEscapeStack.SyncBlocking(this, false); // 见 Tick：阻挡层在时演出页压不上栈
            NarrativeCutscene c = NarrativeService.BeginReplay(s, EndingService.EntryId);
            if (c != null)
            {
                NarrativeCutscenePlayerUIToolkit.Instance.Show(c);
            }
            else
            {
                UiEscapeStack.SyncBlocking(this, true);
            }
        }

        private static Label Row(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        private void Render(CampaignState s)
        {
            EndingStats st = EndingService.Stats(s);
            Shown = st;
            _title.text = GameText.Get("ending.title");
            _subtitle.text = GameText.Get("ending.subtitle");
            _statsHead.text = GameText.Get("ending.section.stats");
            _epilogueHead.text = GameText.Get("ending.section.epilogue");
            _survivorsHead.text = GameText.Format("ending.section.survivors", st.Survivors.Count);
            _memorialHead.text = GameText.Format("ending.section.memorial", st.Fallen.Count);
            _hint.text = GameText.Get("ending.hint");
            _replay.text = GameText.Get("ending.btn.replay");
            _menu.text = GameText.Get("ending.btn.menu");
            _continue.text = GameText.Get("ending.btn.continue");

            _stats.Clear();
            _stats.Add(Row(GameText.Format("ending.stat.area", st.ReturnedArea.ToString("N0", CultureInfo.InvariantCulture)), "es-stat"));
            _stats.Add(Row(GameText.Format("ending.stat.days", st.Days), "es-stat"));
            _stats.Add(Row(GameText.Format("ending.stat.reactions", st.Reactions), "es-stat"));
            _stats.Add(Row(GameText.Format("ending.stat.fusion", st.FusionRecipes), "es-stat"));
            _stats.Add(Row(GameText.Format("ending.stat.raids", st.RaidsRepelled), "es-stat"));
            _epilogue.Clear();
            foreach (string line in st.Epilogue)
            {
                _epilogue.Add(Row(line, "es-line"));
            }
            _survivors.Clear();
            foreach (MachineRecord m in st.Survivors)
            {
                _survivors.Add(Row(EndingService.SurvivorText(s, m), "es-row"));
            }
            if (st.Survivors.Count == 0)
            {
                _survivors.Add(Row(GameText.Get("ending.survivors.empty"), "es-empty"));
            }
            _memorial.Clear();
            foreach (MachineRecord m in st.Fallen)
            {
                _memorial.Add(Row(Campaign.Economy.BlackBoxService.MemorialText(s, m), "es-row"));
            }
            if (st.Fallen.Count == 0)
            {
                _memorial.Add(Row(GameText.Get("ending.memorial.empty"), "es-empty"));
            }
        }
    }
}
