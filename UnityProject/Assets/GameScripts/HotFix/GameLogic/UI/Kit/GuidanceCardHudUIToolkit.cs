using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-UX-04（FG13 FGR-UX-040，FGT-UX-005）：引导卡片。显示 <see cref="GuidanceQueue.Current"/>：标题、一两句说明（多步的按“下一步”翻）、
    /// “还有 N 条排队”、在图鉴中查看（以该钩子解锁的系统说明）、跳过其余、关闭引导。
    /// 非阻塞：不压模态、不进 Esc 栈、根节点不拦截点击，只有卡片本身接收点击；不随游戏时间推进或消失（暂停 / 倍速都不影响）。
    /// 层级 30195：盖在所有面板之上（新游戏设置、按键面板、结局结算 30185、演出播放器 30190 里第一次打开时也看得见），浮层 / 确认框（30200）之下。
    /// 刷新只在队列版本 / 语言 / 开关变化时重建，O(1)。
    /// </summary>
    public sealed class GuidanceCardHudUIToolkit : UiKitPanelHost
    {
        public const int Order = 30195;

        public static GuidanceCardHudUIToolkit Instance { get; private set; }

        private VisualElement _card;
        private Label _title;
        private Label _tag;
        private Label _body;
        private Label _more;
        private Button _ok;
        private Button _codex;
        private Button _skip;
        private Button _disable;
        private int _key;

        protected override string UxmlLocation => "GuidanceCard";
        protected override int SortingOrder => Order;

        // ── 自检 / 冒烟读点 ──
        public bool CardVisible => _card != null && !_card.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string BodyText => _body?.text ?? string.Empty;
        public string MoreText => _more != null && !_more.ClassListContains("uk-hidden") ? _more.text : string.Empty;
        public string TagText => _tag?.text ?? string.Empty;
        public Button OkButton => _ok;
        public Button CodexButton => _codex;
        public Button SkipButton => _skip;
        public Button DisableButton => _disable;
        public VisualElement Card => _card;

        private void Awake()
        {
            Instance = this;
            GuidanceQueue.EnsureSubscribed();
        }

        protected override void OnDestroy()
        {
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
            root.Q<VisualElement>("GuideRoot").pickingMode = PickingMode.Ignore;
            _card = root.Q<VisualElement>("GuideCard");
            _title = root.Q<Label>("GuideTitle");
            _tag = root.Q<Label>("GuideTag");
            _body = root.Q<Label>("GuideBody");
            _more = root.Q<Label>("GuideMore");
            _ok = root.Q<Button>("GuideOk");
            _codex = root.Q<Button>("GuideCodex");
            _skip = root.Q<Button>("GuideSkip");
            _disable = root.Q<Button>("GuideDisable");
            _ok.clicked += GuidanceQueue.Advance;
            _codex.clicked += OpenCodex;
            _skip.clicked += GuidanceQueue.SkipAll;
            _disable.clicked += GuidanceQueue.DisableAll;
            UiTooltip.Attach(_disable, () => new TooltipContent { Title = GameText.Get("guide.card.disable"), Body = GameText.Get("guide.card.disabled_note") });
            _key = 0;
            Refresh();
        }

        /// <summary>“在图鉴中查看”：打开当前这条对应的系统说明（钩子触发时已解锁，这里不再解锁）。</summary>
        public void OpenCodex()
        {
            string id = GuidanceQueue.CurrentCodexId;
            if (id != null)
            {
                // 卡片常在“第一次打开某面板”时弹出，那个面板可能高于图鉴（难度、货舱、结算……）：图鉴一律开在卡片正下方，盖住所有面板。
                MechanicCodexPanelUIToolkit.RequestAbove(Order - 2);
                MechanicCodex.Open(id, unlock: false);
            }
        }

        private void Update()
        {
            GuidanceQueue.Tick();
            Refresh();
        }

        public void Refresh()
        {
            if (_card == null)
            {
                return;
            }
            int key = System.HashCode.Combine(GuidanceQueue.Revision, (int)GameText.Language, GameSettings.GuidanceEnabled, GameText.Revision);
            if (key == _key)
            {
                return;
            }
            _key = key;
            string hook = GuidanceQueue.Current;
            _card.EnableInClassList("uk-hidden", hook == null);
            if (hook == null)
            {
                return;
            }
            int steps = GuidanceQueue.CurrentStepCount;
            int pending = GuidanceQueue.Pending.Count;
            _title.text = GuidanceQueue.CurrentTitle;
            _tag.text = GameText.Get(GuidanceQueue.CurrentIsReplay ? "guide.card.replay" : "guide.card.header");
            _body.text = GuidanceQueue.CurrentBody;
            _more.EnableInClassList("uk-hidden", pending == 0);
            _more.text = pending > 0 ? GameText.Format("guide.card.more", pending) : string.Empty;
            _ok.text = GuidanceQueue.CurrentStep < steps ? GameText.Format("guide.card.next_step", GuidanceQueue.CurrentStep + 1, steps) : GameText.Get("guide.card.ok");
            _codex.text = GameText.Get("guide.card.codex");
            _codex.EnableInClassList("uk-hidden", GuidanceQueue.CurrentCodexId == null);
            _skip.text = GameText.Format("guide.card.skip_rest", pending);
            _skip.EnableInClassList("uk-hidden", pending == 0);
            _disable.text = GameText.Get("guide.card.disable");
            _disable.EnableInClassList("uk-hidden", GuidanceQueue.CurrentIsReplay || !GameSettings.GuidanceEnabled);
        }
    }
}
