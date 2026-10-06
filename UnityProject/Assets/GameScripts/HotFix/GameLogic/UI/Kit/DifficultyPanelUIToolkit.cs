using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-09（FG06 FGR-DEF-060 突袭强度：预设 + 自定义滑条、游戏中途可以修改且记入存档；FG09 FGR-FAC-003 难度对敌人的加成公开写出）：游戏中的难度面板（UI Toolkit）。
    ///
    /// 入口：暂停菜单“难度”（盖在暂停菜单上面，关掉回到暂停菜单）。内容：
    /// - 顶部：当前难度（自定义写出三个倍率）；
    /// - 预设按钮 / 自定义滑条 / 公开说明（<see cref="DifficultyPickerView"/>，与新游戏界面同一套）；
    /// - 修改记录：开局难度、每次修改的时刻与前后难度（最多 difficulty.history_max 条，更早的写“更早还有 n 次”）；
    /// - “应用修改”：与当前相同 → 写明“没有修改”（不弹框）；否则二次确认（写明会记入存档、影响“全程严酷”成就、还没预警的突袭怎么处理、
    ///   改成建造者会取消几波普通突袭），确认后 <see cref="DifficultyService.Change"/>。取消 / Esc 不改任何东西。
    /// 打开时世界保持暂停（暂停菜单已经暂停）；模态：输入上下文为“界面”。
    /// </summary>
    public sealed class DifficultyPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>按键面板 30080 之上、新游戏设置 30082 之下（从暂停菜单 30070 打开时盖在它上面）。</summary>
        public const int Order = 30081;

        public static DifficultyPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title, _current, _historyTitle, _feedback;
        private VisualElement _history;
        private Button _close, _apply;
        private readonly DifficultyPickerView _picker = new DifficultyPickerView();
        private int _seenRevision = -1;

        protected override string UxmlLocation => "DifficultyPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public DifficultyPickerView Picker => _picker;
        public string CurrentText => _current?.text ?? string.Empty;
        public string FeedbackText => _feedback?.text ?? string.Empty;
        public Button ApplyButton => _apply;
        public Button CloseButton => _close;
        public int HistoryRowCount => _history?.childCount ?? 0;
        public string HistoryText(int i) => _history != null && i >= 0 && i < _history.childCount && _history[i] is Label l ? l.text : string.Empty;

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
            _root = root.Q<VisualElement>("DifficultyRoot");
            _title = root.Q<Label>("DifficultyTitle");
            _current = root.Q<Label>("DifficultyCurrent");
            _historyTitle = root.Q<Label>("DifficultyHistoryTitle");
            _history = root.Q<VisualElement>("DifficultyHistory");
            _feedback = root.Q<Label>("DifficultyFeedback");
            _close = root.Q<Button>("DifficultyClose");
            _apply = root.Q<Button>("DifficultyApply");
            _picker.Bind(root, "Diff");
            _picker.Changed += () =>
            {
                if (_feedback != null)
                {
                    _feedback.text = string.Empty;
                }
            };
            if (_close != null)
            {
                _close.clicked += () => SetOpen(false);
            }
            if (_apply != null)
            {
                _apply.clicked += Apply;
            }
            _root?.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
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
                GuidanceHooks.Raise(GuidanceHooks.DifficultyFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _feedback.text = string.Empty;
                BuildTexts();
                _picker.BuildPresets();
                CampaignState s = CampaignSession.Current;
                _picker.Set(s != null ? DifficultyService.Current(s) : DifficultyService.Preset(DifficultyService.Standard));
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        private void BuildTexts()
        {
            _title.text = GameText.Get("ui.difficulty.title");
            _historyTitle.text = GameText.Get("ui.difficulty.history_title");
            _close.text = GameText.Get("ui.difficulty.close");
            _apply.text = GameText.Get("ui.difficulty.apply");
        }

        /// <summary>当前难度与修改记录（打开时、修改后、存档里的难度在别处变了时）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            _seenRevision = DifficultyService.Revision;
            CampaignState s = CampaignSession.Current;
            _current.text = s != null ? GameText.Format("ui.difficulty.current", DifficultyService.Label(s)) : GameText.Get("ui.difficulty.no_campaign");
            _apply.SetEnabled(s != null);
            _history.Clear();
            RaidDifficultyState d = DifficultyService.StateOf(s);
            if (d == null)
            {
                return;
            }
            AddHistory(GameText.Format("ui.difficulty.history_start", DifficultyService.Name(d.StartDifficultyId)));
            if (d.Changes.Length == 0)
            {
                AddHistory(GameText.Get("ui.difficulty.history_none"));
                return;
            }
            int earlier = d.ChangeCount - d.Changes.Length;
            if (earlier > 0)
            {
                AddHistory(GameText.Format("ui.difficulty.history_more", earlier));
            }
            for (int i = d.Changes.Length - 1; i >= 0; i--)
            {
                AddHistory(DifficultyService.HistoryRow(d.Changes[i]));
            }
        }

        private void AddHistory(string text)
        {
            var l = new Label(text);
            l.AddToClassList("dif-history-row");
            _history.Add(l);
        }

        /// <summary>“应用修改”（按钮与自检同一入口）：与当前相同 → 说明没有修改；否则弹二次确认。</summary>
        public void Apply()
        {
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                _feedback.text = GameText.Get("ui.difficulty.no_campaign");
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                return;
            }
            DifficultyChoice to = DifficultyService.Normalize(_picker.Choice);
            DifficultyChoice from = DifficultyService.Current(s);
            if (to.Id == from.Id && System.Math.Abs(to.Frequency - from.Frequency) < 1e-4f && System.Math.Abs(to.Scale - from.Scale) < 1e-4f
                && System.Math.Abs(to.Warning - from.Warning) < 1e-4f)
            {
                _feedback.text = GameText.Get("ui.difficulty.unchanged");
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                return;
            }
            var request = new ConfirmRequest
            {
                Title = GameText.Get("ui.difficulty.confirm_title"),
                OnConfirm = () => Commit(to),
            };
            request.Lines.Add(GameText.Format("ui.difficulty.confirm_line", DifficultyService.Label(from), DifficultyService.Label(to)));
            request.Lines.Add(GameText.Get("ui.difficulty.confirm_record"));
            request.Lines.Add(GameText.Get("ui.difficulty.confirm_pending"));
            int cancel = DifficultyService.PendingNormalRaidsToCancel(s, to);
            if (cancel > 0)
            {
                request.Consequences.Add(GameText.Format("ui.difficulty.confirm_cancel", cancel));
            }
            UiConfirmDialog.Show(request);
        }

        private void Commit(DifficultyChoice to)
        {
            CampaignState s = CampaignSession.Current;
            DifficultyChangeResult r = DifficultyService.Change(s, to, out _);
            switch (r)
            {
                case DifficultyChangeResult.Changed:
                    _feedback.text = GameText.Format("ui.difficulty.applied", DifficultyService.Label(s));
                    Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck);
                    break;
                case DifficultyChangeResult.Unchanged:
                    _feedback.text = GameText.Get("ui.difficulty.unchanged");
                    break;
                default:
                    _feedback.text = GameText.Get("ui.difficulty.no_campaign");
                    break;
            }
            if (s != null)
            {
                _picker.Set(DifficultyService.Current(s));
            }
            Refresh();
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            // 难度面板只属于游戏世界：世界没了（回主菜单、区域被卸载）就收起。
            if (!InWorldOverrideForTests && !(CampaignSession.Current != null && GameRoot.AnyRegionActive))
            {
                SetOpen(false);
                return;
            }
            if (_seenRevision != DifficultyService.Revision)
            {
                Refresh();
            }
        }
    }
}
