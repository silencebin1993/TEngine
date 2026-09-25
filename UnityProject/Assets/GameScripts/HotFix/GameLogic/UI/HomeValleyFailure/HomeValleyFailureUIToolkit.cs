using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.HomeValleyFailure
{
    /// <summary>ER3-SOFTLOCK-01 AC-ECO-011："核心被毁只能失败界面"——全屏阻断式模态，唯一动作是
    /// 返回主菜单（<see cref="GameRoot.EndRun"/>）。与其它 HUD 一样每帧轮询
    /// <see cref="HomeValleySoftlockGuard.IsCoreDestroyed"/> 决定显示/隐藏，不需要
    /// <see cref="HomeValleyController"/> 显式命令它出现——核心被毁是终态，一旦显示就不会再隐藏
    /// （直到 EndRun 把整个区域清场）。</summary>
    public sealed class HomeValleyFailureUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Label _lastSaveLabel;
        private Button _backToMenuButton;
        private bool _wasShown;

        public static HomeValleyFailureUIToolkit Instance { get; private set; }

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

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[HomeValleyFailureUIToolkit] rootVisualElement 等待超时，家园失败面板未初始化。");
                return;
            }

            _root.style.display = DisplayStyle.None;
            _lastSaveLabel = _root.Q<Label>("LastSaveLabel");
            _backToMenuButton = _root.Q<Button>("BackToMenuButton");
            _backToMenuButton.clicked += () => GameRoot.EndRun();
        }

        private void Update()
        {
            if (_root == null)
            {
                return;
            }

            bool shouldShow = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive
                && HomeValleySoftlockGuard.IsCoreDestroyed(CampaignSession.Current);
            _root.style.display = shouldShow ? DisplayStyle.Flex : DisplayStyle.None;

            // ER7-FAIL-01 STORY-EXECUTION-CARDS.md 第1条："显示死因、最近安全自动档与返回菜单；无可
            // 读档时给清晰提示，不能留在不可操作世界"——只在刚刚从隐藏变可见时算一次（面板显示期间
            // 存档槽状态不会再变化，不需要每帧重算），失败后玩家唯一能做的事仍是"返回主菜单"，
            // 这里只是让按钮旁边的文字准确反映"回去之后能不能继续"。
            if (shouldShow && !_wasShown)
            {
                RefreshLastSaveInfo();
                // ER8-CONTENT-01 AC-AUD-001 失败：核心被毁至今没有真实战斗触发源（只有
                // HomeValleySoftlockGuard 的调试入口），挂在“失败页第一次出现”这个边沿上，
                // 将来无论哪条路径把核心打爆，玩家都会同时听到并看到。
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CoreDestroyed);
            }
            _wasShown = shouldShow;
        }

        private void RefreshLastSaveInfo()
        {
            int slot = CampaignSession.ActiveSlotIndex;
            if (slot < 0)
            {
                _lastSaveLabel.text = "无法定位本局存档槽位，返回主菜单后请从存档列表手动选择。";
                return;
            }
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            if (meta.State != CampaignSlotState.Ready)
            {
                // "无可读档时给清晰提示"——不能让玩家以为"返回主菜单"还能接着玩这局。
                _lastSaveLabel.text = $"未找到可读取的安全存档（{CampaignSlotText.StateName(meta.State)}）。返回主菜单后需要新建战役，本局无法继续。";
                return;
            }
            // 槽位号与主菜单一致从 1 开始（此前写成“第 0 槽”）；时间、阶段、时长都是玩家文字。
            _lastSaveLabel.text = $"最近安全自动档：槽位 {CampaignSlotText.SlotNumber(slot)} · 保存于 {CampaignSlotText.SavedAt(meta.WrittenAtUtc)} · " +
                $"{CampaignSlotText.PhaseName(meta.CampaignPhase)} · 游戏时长 {CampaignSlotText.PlayTime(meta.PlaySeconds)}。" +
                "返回主菜单后可从该存档继续（本次核心被毁前的进度已保留）。";
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
