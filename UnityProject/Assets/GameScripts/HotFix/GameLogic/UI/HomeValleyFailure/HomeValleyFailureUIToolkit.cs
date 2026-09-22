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
        private Button _backToMenuButton;

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
            _document.sortingOrder = 20; // 家园失败面板（UI_WORKFLOW_GUIDE.md 第4节）：全屏阻断式模态，必须盖过任何既有 HUD/面板。

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
