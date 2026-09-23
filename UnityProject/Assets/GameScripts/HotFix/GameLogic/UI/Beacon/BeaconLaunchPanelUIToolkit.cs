using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Beacon
{
    /// <summary>ER7-BEACON-01 STORY-EXECUTION-CARDS.md 第3条："E 交互要求玩家二次确认……进入不可取消
    /// 10秒演出再进结算"——第一次确认是按住E（见 <see cref="HomeValleyController.BuildInteractCandidates"/>
    /// 的 beacon 候选），本面板的"确认启动"按钮是第二次。启动后（<see cref="HomeValleyBeacon.IsLaunching"/>）
    /// 按钮消失，只剩倒计时文案+关闭按钮——关闭面板不影响演出本身继续计时（<see cref="HomeValleyBeacon.Tick"/>
    /// 与面板是否打开无关，独立驱动），"不可取消"落在"没有任何 API 能清空已启动状态"这个事实本身。</summary>
    public sealed class BeaconLaunchPanelUIToolkit : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _statusLabel;
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

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[BeaconLaunchPanelUIToolkit] rootVisualElement 等待超时，信标启动面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("BeaconLaunchPanelRoot");
            _statusLabel = _root.Q<Label>("StatusLabel");
            _confirmButton = _root.Q<Button>("ConfirmButton");
            _closeButton = _root.Q<Button>("CloseButton");

            _confirmButton.clicked += OnConfirmClicked;
            _closeButton.clicked += OnCloseClicked;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            HomeValleyController hv = GameRoot.HomeValley;
            bool open = hv != null && hv.IsActive && hv.IsBeaconLaunchPanelOpen;
            _panel.RemoveFromClassList("blp-root-visible");
            if (open)
            {
                _panel.AddToClassList("blp-root-visible");
            }
            if (!open)
            {
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
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
            bool launched = HomeValleyBeacon.IsLaunched(state);
            bool launching = HomeValleyBeacon.IsLaunching(state);

            if (launched)
            {
                _statusLabel.text = "信标已启动。战役即将结算——可关闭本面板。";
            }
            else if (launching)
            {
                float remaining = HomeValleyBeacon.RemainingCutsceneSeconds(state);
                _statusLabel.text = $"演出进行中，剩余 {remaining:F0} 秒（不可取消）。";
            }
            else if (!HomeValleyBeacon.IsOperationalAndPowered(state))
            {
                _statusLabel.text = "信标尚未通电或未完工，暂时无法启动——先解决供电缺口（关停/降级部分建筑，" +
                    "或建造第二座发电机）。";
            }
            else
            {
                _statusLabel.text = "确认启动导航信标？启动后将进入10秒不可取消的返航演出，随即结算本次战役。";
            }

            bool showConfirm = !launched && !launching && HomeValleyBeacon.IsOperationalAndPowered(state);
            _confirmButton.style.display = showConfirm ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnConfirmClicked()
        {
            HomeValleyBeacon.ActionResult result = HomeValleyBeacon.TryStartLaunch(CampaignSession.Current);
            if (!result.Success)
            {
                Log.Warning($"[BeaconLaunchPanelUIToolkit] 启动失败：{result.FailureReason}");
            }
            _refreshTimer = 0f;
        }

        private void OnCloseClicked()
        {
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
