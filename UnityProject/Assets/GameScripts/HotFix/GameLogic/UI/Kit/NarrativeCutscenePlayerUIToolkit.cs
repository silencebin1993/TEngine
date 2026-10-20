using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG9-NAR-01（FG11 FGR-NAR-002、第 8 / 9 节）：演出播放器——接管画面播放演出（上下黑边 + 居中逐字字幕）。
    /// - 首次演出：战役里有待播演出（<see cref="NarrativeService.PendingCutscene"/>）、世界已载入、没有别的模态界面 / 确认框时自动开始；
    ///   开始前由 <see cref="NarrativeService.BeginPending"/> 自动存档（中途退出游戏 → 读档后重新播放）；播放期间世界暂停，结束后恢复。
    /// - 字幕逐字出现，速度跟随“文本显示速度”（拉到最大 = 整行立即出现），字号跟随“字幕大小”；整行出完停留后自动下一行。
    /// - 点画面 / “继续”：补全当前行，已补全则下一行。“跳过” / Esc：首次观看弹确认（“跳过后可以随时在资料库里重看”），资料库里重看直接跳过。
    /// - 计时用真实时间（<see cref="Time.unscaledDeltaTime"/>）：与 0.5x～3x 倍速、世界暂停无关。
    /// - 战役被换掉 / 世界卸载（回主菜单、读档）时中止：不收录、不改存档。
    /// 分层 30190：所有面板之上、浮层（确认框 30200）之下——跳过确认框盖在演出上面。
    /// 开销：播放中每帧 O(1)（一行字幕的子串）；不播放时每帧只比较待播字段。
    /// </summary>
    public sealed class NarrativeCutscenePlayerUIToolkit : UiKitPanelHost
    {
        public const int Order = 30190;

        public static NarrativeCutscenePlayerUIToolkit Instance { get; private set; }
        public static bool IsShowing => Instance != null && Instance._cutscene != null;

        /// <summary>自检：编辑模式下没有载入的地点也允许自动开始 / 不自动中止。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _tag;
        private Label _title;
        private Label _line;
        private Label _hint;
        private Label _progress;
        private Button _next;
        private Button _skip;
        private NarrativeCutscene _cutscene;
        private bool _confirming;
        private bool _escapePending;

        protected override string UxmlLocation => "NarrativeCutscene";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public NarrativeCutscene Cutscene => _cutscene;
        public string LineText => _line?.text ?? string.Empty;
        public string TitleText => _title?.text ?? string.Empty;
        public string ProgressText => _progress?.text ?? string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public bool ReplayTagVisible => _tag != null && !_tag.ClassListContains("uk-hidden");
        public float LineFontSize => _line != null ? _line.style.fontSize.value.value : 0f;
        public Button NextButton => _next;
        public Button SkipButton => _skip;
        public bool Confirming => _confirming;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (_cutscene != null && Instance == this)
            {
                Hide();
                NarrativeService.AbortPlaying();
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
            _root = root.Q<VisualElement>("NcRoot");
            _tag = root.Q<Label>("NcTag");
            _title = root.Q<Label>("NcTitle");
            _line = root.Q<Label>("NcLine");
            _hint = root.Q<Label>("NcHint");
            _progress = root.Q<Label>("NcProgress");
            _next = root.Q<Button>("NcNext");
            _skip = root.Q<Button>("NcSkip");
            _next.clicked += Advance;
            _skip.clicked += RequestSkip;
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root || evt.target is Label || (evt.target is VisualElement v && v.name == "NcStage"))
                {
                    Advance();
                }
            });
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>每帧推进（播放器的 Update 与自检同一入口；<paramref name="realDeltaSeconds"/> 是真实秒）。</summary>
        public void Tick(float realDeltaSeconds)
        {
            CampaignState s = CampaignSession.Current;
            bool worldReady = InWorldOverrideForTests || (s != null && GameRoot.AnyRegionActive);
            if (_cutscene == null)
            {
                if (worldReady && s?.Progress != null && !string.IsNullOrEmpty(s.Progress.PendingCutscene) && NarrativeService.Playing == null
                    && !UiConfirmDialog.IsOpen && !InputRouter.PanelModalOpen)
                {
                    NarrativeCutscene c = NarrativeService.BeginPending(s);
                    if (c != null)
                    {
                        Show(c);
                    }
                }
                return;
            }
            if (!worldReady || !ReferenceEquals(NarrativeService.Playing, _cutscene) || !ReferenceEquals(NarrativeService.PlayingState, s))
            {
                // 战役被换掉 / 世界卸载：中止，不收录。
                Hide();
                NarrativeService.AbortPlaying();
                return;
            }
            if (_escapePending)
            {
                // Esc 栈在关闭回调之后会移除本层（连同挂在本层下面的确认框）：下一帧补回本层再弹确认。
                _escapePending = false;
                if (!UiEscapeStack.Contains(this))
                {
                    UiEscapeStack.Push(this, OnEscape);
                }
                RequestSkip();
            }
            if (!_confirming)
            {
                _cutscene.Tick(realDeltaSeconds, GameSettings.NarrativeTextSpeed, GameSettings.NarrativeTextInstant);
            }
            if (_cutscene.Finished)
            {
                Finish(skipped: false);
                return;
            }
            Render();
        }

        /// <summary>显示一段已经开始的演出（首次播放由 <see cref="Tick"/> 自动调用；资料库“重看”直接调用）。</summary>
        public void Show(NarrativeCutscene c)
        {
            if (_root == null || c == null)
            {
                return;
            }
            _cutscene = c;
            _confirming = false;
            _escapePending = false;
            _root.EnableInClassList("uk-hidden", false);
            InputRouter.PushModal(this);
            UiEscapeStack.Push(this, OnEscape);
            Render();
        }

        private void Hide()
        {
            _cutscene = null;
            _confirming = false;
            _escapePending = false;
            if (_root != null)
            {
                _root.EnableInClassList("uk-hidden", true);
            }
            InputRouter.PopModal(this);
            UiEscapeStack.Remove(this);
        }

        public void Advance()
        {
            if (_cutscene == null || _confirming)
            {
                return;
            }
            _cutscene.Advance(GameSettings.NarrativeTextSpeed, GameSettings.NarrativeTextInstant);
            if (_cutscene.Finished)
            {
                Finish(skipped: false);
                return;
            }
            Render();
        }

        /// <summary>Esc：重看直接跳过；首次观看下一帧弹确认（见 <see cref="Tick"/>）。</summary>
        private void OnEscape()
        {
            if (_cutscene == null || _confirming)
            {
                return;
            }
            if (!NarrativeService.SkipNeedsConfirm)
            {
                Finish(skipped: true);
                return;
            }
            _escapePending = true;
        }

        /// <summary>“跳过”按钮：首次观看弹确认，重看直接跳过。确认框弹出期间本层保留（取消后继续观看）。</summary>
        public void RequestSkip()
        {
            if (_cutscene == null || _confirming)
            {
                return;
            }
            if (!NarrativeService.SkipNeedsConfirm)
            {
                Finish(skipped: true);
                return;
            }
            _confirming = true;
            var req = new ConfirmRequest
            {
                Title = GameText.Get("narrative.cutscene.skip_title"),
                ConfirmText = GameText.Get("narrative.cutscene.skip_confirm"),
                CancelText = GameText.Get("narrative.cutscene.skip_cancel"),
                OnConfirm = () =>
                {
                    _confirming = false;
                    Finish(skipped: true);
                },
                OnCancel = () =>
                {
                    _confirming = false;
                    if (_cutscene != null)
                    {
                        // 取消 = 继续观看：Esc 那一层已被确认框之前的 Esc 消费掉时补回来。
                        if (!UiEscapeStack.Contains(this))
                        {
                            UiEscapeStack.Push(this, OnEscape);
                        }
                    }
                },
            };
            req.Lines.Add(GameText.Get("narrative.cutscene.skip_line"));
            UiConfirmDialog.Show(req);
        }

        private void Finish(bool skipped)
        {
            if (_cutscene == null)
            {
                return;
            }
            Hide();
            NarrativeService.FinishPlaying(skipped);
        }

        private void Render()
        {
            if (_cutscene == null)
            {
                return;
            }
            float speed = GameSettings.NarrativeTextSpeed;
            bool instant = GameSettings.NarrativeTextInstant;
            _line.style.fontSize = UiTuningValues.Get("narrative.subtitle_font_px") * GameSettings.SubtitleScale;
            _line.text = _cutscene.VisibleText(speed, instant);
            _title.text = NarrativeService.TitleOf(_cutscene.Def);
            _tag.text = GameText.Get("narrative.cutscene.replay_tag");
            _tag.EnableInClassList("uk-hidden", !_cutscene.Replay);
            _progress.text = $"{_cutscene.LineIndex + 1} / {_cutscene.LineCount}";
            _hint.text = GameText.Get("narrative.cutscene.hint");
            _next.text = GameText.Get("narrative.cutscene.next");
            _skip.text = GameText.Get("narrative.cutscene.skip");
        }
    }
}
