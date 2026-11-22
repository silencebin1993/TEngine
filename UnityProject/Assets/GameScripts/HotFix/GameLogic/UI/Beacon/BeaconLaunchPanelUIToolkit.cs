using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

namespace GameLogic.UI.Beacon
{
    /// <summary>ER7-BEACON-01 STORY-EXECUTION-CARDS.md 第3条："E 交互要求玩家二次确认……进入不可取消
    /// 10秒演出再进结算"——第一次确认是按住E（见 <see cref="HomeValleyController.BuildInteractCandidates"/>
    /// 的 beacon 候选），本面板的"确认启动"按钮是第二次。启动后（<see cref="HomeValleyBeacon.IsLaunching"/>）
    /// 按钮消失，只剩倒计时文案+关闭按钮——关闭面板不影响演出本身继续计时（<see cref="HomeValleyBeacon.Tick"/>
    /// 与面板是否打开无关，独立驱动），"不可取消"落在"没有任何 API 能清空已启动状态"这个事实本身。</summary>
    /// <remarks>FG13-END-02（FG11 FGR-END-010“沿用现有的二次确认面板”）：同一个面板另有“点亮信标”模式——返航工地五个阶段全部完工后，
    /// 生产面板的“点亮信标…”（第一次确认）打开本面板（<see cref="OpenReveal"/>），“确认点亮”（第二次）→ <see cref="RevealService.Confirm"/> 投放揭示演出。
    /// 这个模式不碰 Demo 信标的 10 秒不可取消演出与 <c>BeaconLaunchStartedAtPlaySeconds</c>；揭示演出走叙事框架（可跳过、可在资料库重看）。文字走文本键。</remarks>
    public sealed class BeaconLaunchPanelUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.2f;

        /// <summary>FG13-END-02：“点亮信标”模式开着（只在世界里有效；离开世界自动收起）。</summary>
        public static bool RevealOpen { get; private set; }
        public static int RevealConfirmClicks { get; private set; }
        public static string LastRevealDeny { get; private set; }

        public static void OpenReveal() => RevealOpen = true;
        public static void CloseReveal() => RevealOpen = false;

        /// <summary>“点亮信标”模式的说明文字（面板与自检同一来源）。</summary>
        public static string RevealStatusText(CampaignState s)
        {
            if (RevealService.Revealed(s))
            {
                return GameText.Get("mega.reveal.panel_done");
            }
            if (RevealService.Pending(s))
            {
                return GameText.Get("mega.reveal.panel_pending");
            }
            return RevealService.CanConfirm(s, out string reason)
                ? GameText.Get("mega.reveal.panel_status") + "\n" + GameText.Get("mega.reveal.panel_saves")
                : RevealService.DenyText(reason);
        }

        /// <summary>“确认点亮”（面板按钮与自检同一入口）：成功 → 收起面板（揭示演出下一帧开始，开始前自动存档）；不成功 → 拒绝提示写明原因。</summary>
        public static bool ConfirmReveal()
        {
            RevealConfirmClicks++;
            if (RevealService.Confirm(CampaignSession.Current, out string reason))
            {
                LastRevealDeny = null;
                CloseReveal();
                return true;
            }
            LastRevealDeny = reason;
            Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, RevealService.DenyText(reason));
            return false;
        }

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _statusLabel;
        private Label _titleLabel;
        private string _demoTitle;
        private string _demoConfirm;
        private string _demoClose;
        private bool _wasOpen;

        /// <summary>自检挂真 UXML 时视为“在世界里”（离开世界自动收起的判定不误伤）。</summary>
        public static bool InWorldOverrideForTests;
        private Button _confirmButton;
        private Button _closeButton;

        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BeaconLaunchPanel");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：本模态阻断式确认框，取覆盖面板(10)同一量级。
            _document.sortingOrder = 10;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            if (_document.rootVisualElement == null)
            {
                Log.Error("[BeaconLaunchPanelUIToolkit] rootVisualElement 等待超时，信标启动面板未初始化。");
                return;
            }
            GameLogic.Localization.UiText.Localize(_document.rootVisualElement); // FG15-UX-03：UXML 的 @文本键
            BindView(_document.rootVisualElement);
        }

        /// <summary>绑定面板元素（正式流程在 Start 里；自检挂真 UXML 直接调）。Demo 模式的文字取 UXML 原文，切回时还原。</summary>
        public void BindView(VisualElement root)
        {
            _root = root;
            _panel = _root.Q<VisualElement>("BeaconLaunchPanelRoot");
            _statusLabel = _root.Q<Label>("StatusLabel");
            _titleLabel = _root.Q<Label>("Title");
            _demoTitle = _titleLabel?.text;
            _confirmButton = _root.Q<Button>("ConfirmButton");
            _closeButton = _root.Q<Button>("CloseButton");
            _demoConfirm = _confirmButton.text;
            _demoClose = _closeButton.text;

            _confirmButton.clicked += OnConfirmClicked;
            _closeButton.clicked += OnCloseClicked;
        }

        /// <summary>FG0-UX-01（FGR-UX-001）：Esc 逐层返回——面板开着时在 Esc 栈里占一层（缓存委托，不每帧分配）。</summary>
        private System.Action _escClose;

        private void Update() => Tick();

        /// <summary>每帧：显隐、Esc 栈、定时刷新文字（打开那一帧立即刷新，不残留上一次的文字）。自检直接调。</summary>
        public void Tick()
        {
            if (_panel == null)
            {
                return;
            }

            HomeValleyController hv = GameRoot.HomeValley;
            if (RevealOpen && !GameRoot.AnyRegionActive && !InWorldOverrideForTests)
            {
                CloseReveal(); // 离开世界（回主菜单 / 读档换战役）自动收起
            }
            bool open = RevealOpen || (hv != null && hv.IsActive && hv.IsBeaconLaunchPanelOpen);
            UiEscapeStack.Sync(this, open, _escClose ??= OnCloseClicked);
            _panel.RemoveFromClassList("blp-root-visible");
            if (open)
            {
                _panel.AddToClassList("blp-root-visible");
            }
            bool opened = open && !_wasOpen;
            _wasOpen = open;
            if (!open)
            {
                return;
            }

            _refreshTimer = opened ? 0f : _refreshTimer - Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            Refresh();
        }

        private void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            if (RevealOpen)
            {
                if (_titleLabel != null)
                {
                    _titleLabel.text = GameText.Get("mega.reveal.panel_title");
                }
                _statusLabel.text = RevealStatusText(state);
                _confirmButton.text = GameText.Get("mega.reveal.confirm");
                _closeButton.text = GameText.Get("mega.reveal.close");
                _confirmButton.style.display = RevealService.CanConfirm(state, out _) ? DisplayStyle.Flex : DisplayStyle.None;
                return;
            }
            if (_titleLabel != null)
            {
                _titleLabel.text = _demoTitle;
            }
            _confirmButton.text = _demoConfirm;
            _closeButton.text = _demoClose;
            bool launched = HomeValleyBeacon.IsLaunched(state);
            bool launching = HomeValleyBeacon.IsLaunching(state);

            if (launched)
            {
                _statusLabel.text = GameText.Get("beacon.panel.beacon_launched_the_campaign");
            }
            else if (launching)
            {
                float remaining = HomeValleyBeacon.RemainingCutsceneSeconds(state);
                _statusLabel.text = GameText.Format("beacon.panel.launch_sequence_in_progress", remaining);
            }
            else if (!HomeValleyBeacon.IsOperationalAndPowered(state))
            {
                _statusLabel.text = GameText.Get("beacon.panel.the_beacon_is_not") +
                    GameText.Get("beacon.panel.or_build_a_second");
            }
            else
            {
                _statusLabel.text = GameText.Get("beacon.panel.launch_the_navigation_beacon");
            }

            bool showConfirm = !launched && !launching && HomeValleyBeacon.IsOperationalAndPowered(state);
            _confirmButton.style.display = showConfirm ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnConfirmClicked()
        {
            if (RevealOpen)
            {
                ConfirmReveal();
                _refreshTimer = 0f;
                return;
            }
            HomeValleyBeacon.ActionResult result = HomeValleyBeacon.TryStartLaunch(CampaignSession.Current);
            if (!result.Success)
            {
                Log.Warning($"[BeaconLaunchPanelUIToolkit] 启动失败：{result.FailureReason}");
                // ER8-CONTENT-01 AC-AUD-001 拒绝：此前只写日志，玩家点了没反应。原因码翻成玩家可读文字。
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, LaunchFailureText(result.FailureReason));
            }
            _refreshTimer = 0f;
        }

        private static string LaunchFailureText(string reason)
        {
            switch (reason)
            {
                case "already-launched": return GameText.Get("beacon.panel.the_beacon_has_already");
                case "already-launching": return GameText.Get("beacon.panel.the_beacon_launch_sequence");
                case "not-unlocked": return GameText.Get("beacon.panel.the_beacon_is_locked");
                case "not-operational-or-powered": return GameText.Get("beacon.panel.the_beacon_is_unfinished");
                default: return GameText.Get("beacon.panel.the_beacon_cannot_be");
            }
        }

        private void OnCloseClicked()
        {
            if (RevealOpen)
            {
                CloseReveal();
                return;
            }
            GameRoot.HomeValley?.SetBeaconLaunchPanelOpen(false);
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
