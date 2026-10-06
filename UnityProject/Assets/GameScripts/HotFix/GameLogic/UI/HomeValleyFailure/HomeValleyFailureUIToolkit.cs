using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

namespace GameLogic.UI.HomeValleyFailure
{
    /// <summary>ER3-SOFTLOCK-01 AC-ECO-011 / ER7-FAIL-01 / FG6-DEF-08（FGR-DEF-053）："核心被毁只能失败界面"——全屏阻断式模态。
    /// 显示死因（被哪一波突袭摧毁）与最近安全自动档；动作：“读取最近自动存档”（<see cref="GameRoot.ReloadActiveSlot"/>，槽位可读时才可点）与“返回主菜单”（<see cref="GameRoot.EndRun"/>）。
    /// 与其它 HUD 一样每帧轮询 <see cref="HomeValleySoftlockGuard.IsCoreDestroyed"/> 决定显示/隐藏；核心被毁是终态，一旦显示就不会再隐藏（直到读档 / 回主菜单把整个世界清场）。
    /// 核心被摧毁之后不再写存档（<see cref="CampaignAutoSaveService"/>），所以槽位里一定是被摧毁之前的安全档。</summary>
    public sealed class HomeValleyFailureUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Label _title;
        private Label _subtitle;
        private Label _causeLabel;
        private Label _lastSaveLabel;
        private Button _loadButton;
        private Button _backToMenuButton;
        private bool _wasShown;

        public static HomeValleyFailureUIToolkit Instance { get; private set; }

        // ── 自检 / 冒烟读点 ──
        public bool Shown => _root != null && _root.style.display.value == DisplayStyle.Flex;
        public string CauseText => _causeLabel?.text ?? string.Empty;
        public string LastSaveText => _lastSaveLabel?.text ?? string.Empty;
        public Button LoadButton => _loadButton;
        public Button MenuButton => _backToMenuButton;
        public int LoadClicks { get; private set; }
        public string LastLoadFailure { get; private set; } = string.Empty;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("HomeValleyFailure");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 家园失败面板（UI_WORKFLOW_GUIDE.md 第4节）：全屏阻断式模态，必须盖过任何既有 HUD/面板——包括被点到
            // 前面的浮动窗口（浮动层 33～30000；此前固定 20，点过的装配站/蓝图窗口会盖在失败页上面）。
            _document.sortingOrder = Common.UiWindowFocus.ModalSortingOrder;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            if (_document.rootVisualElement == null)
            {
                Log.Error("[HomeValleyFailureUIToolkit] rootVisualElement 等待超时，家园失败面板未初始化。");
                return;
            }
            BindView(_document.rootVisualElement);
        }

        /// <summary>绑定视觉树（正式流程在 Start 里；自检编辑模式下直接给克隆出来的树）。</summary>
        public void BindView(VisualElement root)
        {
            _root = root;
            _root.style.display = DisplayStyle.None;
            _title = _root.Q<Label>("Title");
            _subtitle = _root.Q<Label>("Subtitle");
            _causeLabel = _root.Q<Label>("CauseLabel");
            _lastSaveLabel = _root.Q<Label>("LastSaveLabel");
            _loadButton = _root.Q<Button>("LoadAutosaveButton");
            _backToMenuButton = _root.Q<Button>("BackToMenuButton");
            _backToMenuButton.clicked += () => GameRoot.EndRun();
            if (_loadButton != null)
            {
                _loadButton.clicked += () => LoadAutosave();
            }
            _wasShown = false;
        }

        /// <summary>“读取最近自动存档”（按钮 / 自检）：读回本局存档槽。失败时回到主菜单（GameRoot 处理），这里记下原因。</summary>
        public bool LoadAutosave()
        {
            if (_loadButton != null && !_loadButton.enabledSelf)
            {
                return false;
            }
            LoadClicks++;
            RestoreResult r = GameRoot.ReloadActiveSlot();
            LastLoadFailure = r.Success ? string.Empty : GameText.Format("failure.load_failed", r.Message ?? r.FailedStep.ToString());
            if (!r.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Failure, LastLoadFailure);
            }
            _wasShown = false;
            return r.Success;
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }

            // FG0-ARCH-01：家园一直在运行（镜头在远征地点时也一样），核心被毁的失败页不看镜头在哪里。
            bool shouldShow = GameRoot.HomeValley != null && GameRoot.HomeValley.IsLoaded
                && HomeValleySoftlockGuard.IsCoreDestroyed(CampaignSession.Current);
            Apply(shouldShow);
        }

        /// <summary>按“该不该显示”刷新（Update 每帧调用；自检直接调）。</summary>
        public void Apply(bool shouldShow)
        {
            _root.style.display = shouldShow ? DisplayStyle.Flex : DisplayStyle.None;
            // FG0-UX-01（FGR-UX-001）：失败页不能 Esc 关掉，Esc 也不许穿透去开一个被它盖住的暂停菜单。
            UiEscapeStack.SyncBlocking(this, shouldShow);

            // ER7-FAIL-01 STORY-EXECUTION-CARDS.md 第1条："显示死因、最近安全自动档与返回菜单；无可
            // 读档时给清晰提示，不能留在不可操作世界"——只在刚刚从隐藏变可见时算一次（面板显示期间
            // 存档槽状态不会再变化：核心被摧毁后不再写存档）。
            if (shouldShow && !_wasShown)
            {
                RefreshTexts();
                // ER8-CONTENT-01 AC-AUD-001 失败：挂在“失败页第一次出现”这个边沿上，玩家同时听到并看到。
                Campaign.Feedback.FeedbackCues.RaiseLocated(Campaign.Feedback.FeedbackCueId.CoreDestroyed, Campaign.Regions.HomeValleyLayout.Core.Position);
            }
            _wasShown = shouldShow;
        }

        private void RefreshTexts()
        {
            CampaignState state = CampaignSession.Current;
            _title.text = GameText.Get("failure.title");
            _subtitle.text = GameText.Get("failure.subtitle");
            _backToMenuButton.text = GameText.Get("failure.menu");
            RaidResultRecord by = RaidResultService.CoreLostBy(state);
            RaidResultState rs = RaidResultService.StateOf(state);
            if (_causeLabel != null)
            {
                bool hasCause = rs != null && rs.CoreLost;
                _causeLabel.EnableInClassList("hvf-hidden", !hasCause);
                if (hasCause)
                {
                    double sec = rs.CoreLostTick / (double)System.Math.Max(1, GameClock.StepHz);
                    string faction = by != null ? Campaign.WorldSim.WorldTransitSystem.OriginName(by.Faction) : string.Empty;
                    _causeLabel.text = GameText.Format("failure.cause_raid", faction, RaidResultService.WaveLabel(by), GameClock.DayOf(sec), GameClock.FormatHhMm(sec));
                }
            }
            bool loadable = RefreshLastSaveInfo();
            if (_loadButton != null)
            {
                _loadButton.text = GameText.Get("failure.load");
                _loadButton.SetEnabled(loadable);
            }
        }

        /// <summary>写“最近安全自动档”一行；返回槽位是否可读。</summary>
        private bool RefreshLastSaveInfo()
        {
            int slot = CampaignSession.ActiveSlotIndex;
            if (slot < 0)
            {
                _lastSaveLabel.text = GameText.Get("failure.no_slot");
                return false;
            }
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            if (meta.State != CampaignSlotState.Ready)
            {
                // "无可读档时给清晰提示"——不能让玩家以为"返回主菜单"还能接着玩这局。
                _lastSaveLabel.text = GameText.Format("failure.no_save", CampaignSlotText.StateName(meta.State));
                return false;
            }
            // 槽位号与主菜单一致从 1 开始；时间、阶段、时长都是玩家文字。
            _lastSaveLabel.text = GameText.Format("failure.lastsave", CampaignSlotText.SlotNumber(slot), CampaignSlotText.SavedAt(meta.WrittenAtUtc),
                CampaignSlotText.PhaseName(meta.CampaignPhase), CampaignSlotText.PlayTime(meta.PlaySeconds));
            return true;
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
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
