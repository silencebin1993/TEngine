using Cysharp.Threading.Tasks;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.LineageWorkshop;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.GameShell
{
    /// <summary>
    /// 常驻的玩家运行中枢。它只负责跨页面导航、开局与实际显示设置；具体玩法仍由各领域面板处理。
    /// </summary>
    public sealed class GameShellUIToolkit : MonoBehaviour
    {
        private const string FullscreenPreferenceKey = "GameUi.Fullscreen";
        private const string TargetFpsPreferenceKey = "GameUi.TargetFps";

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private VisualElement _homeView;
        private VisualElement _runNavigation;
        private VisualElement _settingsOverlay;
        private Label _lastOutcomeLabel;
        private Label _runStateLabel;
        private Label _walletLabel;
        private Label _settingsFeedbackLabel;
        private Button _resumeRunButton;
        private Toggle _fullscreenToggle;
        private bool _settingsOpen;

        public static GameShellUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("GameShellUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 2;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[GameShellUIToolkit] rootVisualElement 等待超时，运行中枢未初始化。");
                return;
            }

            CacheNodes();
            ApplyStoredSettings();
            RefreshShell();
        }

        private void CacheNodes()
        {
            _homeView = _root.Q<VisualElement>("homeView");
            _runNavigation = _root.Q<VisualElement>("runNavigation");
            _settingsOverlay = _root.Q<VisualElement>("settingsOverlay");
            _lastOutcomeLabel = _root.Q<Label>("lastOutcomeLabel");
            _runStateLabel = _root.Q<Label>("runStateLabel");
            _walletLabel = _root.Q<Label>("walletLabel");
            _settingsFeedbackLabel = _root.Q<Label>("settingsFeedbackLabel");
            _resumeRunButton = _root.Q<Button>("resumeRunButton");
            _fullscreenToggle = _root.Q<Toggle>("fullscreenToggle");

            BindClick("startRunButton", StartNewRun);
            BindClick("resumeRunButton", ResumeRun);
            BindClick("homeCodexButton", OpenCodex);
            BindClick("homeSettingsButton", OpenSettings);
            BindClick("workshopButton", OpenWorkshop);
            BindClick("carrierButton", OpenCarrier);
            BindClick("germinationButton", OpenGermination);
            BindClick("deckButton", OpenDeck);
            BindClick("shopButton", OpenShop);
            BindClick("codexButton", OpenCodex);
            BindClick("pauseButton", OpenPause);
            BindClick("settingsButton", OpenSettings);
            BindClick("fps60Button", () => SetTargetFrameRate(60));
            BindClick("fps120Button", () => SetTargetFrameRate(120));
            BindClick("closeSettingsButton", CloseSettings);

            if (_fullscreenToggle != null)
            {
                _fullscreenToggle.RegisterValueChangedCallback(evt => SetFullscreen(evt.newValue));
            }

            VisualElement shell = _root.Q<VisualElement>("gameShellRoot");
            if (shell != null)
            {
                shell.pickingMode = PickingMode.Ignore;
            }
            UiWindowFocus.Attach(_document, _root.Q<VisualElement>(className: "home-panel"),
                _root.Q<Label>(className: "home-title"), "home");
            UiWindowFocus.Attach(_document, _runNavigation, _runStateLabel, "run-navigation");
            VisualElement settingsDialog = _root.Q<VisualElement>(className: "settings-dialog");
            UiWindowFocus.Attach(_document, settingsDialog, settingsDialog?.Q<Label>(className: "dialog-title"), "settings");
        }

        private void BindClick(string nodeName, System.Action action)
        {
            Button button = _root.Q<Button>(nodeName);
            if (button != null)
            {
                button.clicked += action;
            }
        }

        private void Update()
        {
            if (_root != null)
            {
                RefreshShell();
            }
        }

        private void RefreshShell()
        {
            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning;
            // ER2-BOOT-01：homeView（旧运行中枢首页，"开始新局/继续"等仍是切产品前的旧生物题材按钮）
            // 永久隐藏——正式主菜单 MainMenuUI 才是唯一的"无战役进行中"入口，见 GameApp.MountGameplayUi
            // 与 GameRoot.EndRun 的注释。这里不判断 running，避免它在 MainMenuUI 背后可见/可点。
            _homeView?.EnableInClassList("is-hidden", true);
            _runNavigation?.EnableInClassList("is-hidden", !running);
            _settingsOverlay?.EnableInClassList("is-hidden", !_settingsOpen);

            if (_resumeRunButton != null)
            {
                // 当前没有跨进程续局存档；不要把“继续”伪装成可用功能。
                _resumeRunButton.SetEnabled(false);
                _resumeRunButton.text = "继续当前局（暂无可继续进度）";
            }

            if (running)
            {
                if (_runStateLabel != null)
                {
                    _runStateLabel.text = cell.Paused ? "归还阶段 · 已暂停" : "归还阶段 · 进行中";
                }
                if (_walletLabel != null && cell.Wallet != null)
                {
                    _walletLabel.text = $"营养质 {cell.Wallet.Nutrient:F0} · 突变质 {cell.Wallet.Mutagen:F0} · 进化能 {cell.Wallet.EvoEnergy:F0}";
                }
                return;
            }

            if (_lastOutcomeLabel != null)
            {
                StageOutcome outcome = GameRoot.Director?.LastOutcome;
                _lastOutcomeLabel.text = outcome == null || outcome.StageId == StageId.None
                    ? "尚未开始一局。"
                    : (outcome.Victory
                        ? $"上局完成：等级 {outcome.Level} · 用时 {outcome.DurationSeconds:F0} 秒"
                        : $"上局结束：{outcome.DeathCause ?? "未记录原因"} · 等级 {outcome.Level}");
            }
        }

        private void StartNewRun()
        {
            CloseAllGameplayPanels();
            CloseSettings();
            GameRoot.StartCellStage();
        }

        private void ResumeRun()
        {
            if (GameRoot.CellStage?.IsRunning == true)
            {
                GameRoot.ResumeCellStage();
            }
        }

        private void OpenWorkshop()
        {
            CloseAllGameplayPanels();
            LineageWorkshopUIToolkit.Instance?.SetPanelOpen(true);
        }

        private void OpenCarrier()
        {
            CloseAllGameplayPanels();
            BattleCarrierUIToolkit.Instance?.SetPanelOpen(true);
        }

        private void OpenGermination()
        {
            CloseAllGameplayPanels();
            BattleGerminationUIToolkit.Instance?.SetPanelOpen(true);
        }

        private void OpenDeck()
        {
            CloseAllGameplayPanels();
            BattleOverlayUIToolkit.Instance?.ShowDeck();
        }

        private void OpenShop()
        {
            CloseAllGameplayPanels();
            BattleOverlayUIToolkit.Instance?.ShowShop();
        }

        private void OpenCodex()
        {
            CloseAllGameplayPanels();
            if (GameRoot.CellStage?.IsRunning == true)
            {
                BattleOverlayUIToolkit.Instance?.ShowCodex();
            }
        }

        private void OpenPause()
        {
            CloseAllGameplayPanels();
            BattleOverlayUIToolkit.Instance?.ShowPause();
        }

        private void CloseAllGameplayPanels()
        {
            BattleOverlayUIToolkit.Instance?.CloseAll();
            BattleCarrierUIToolkit.Instance?.SetPanelOpen(false);
            BattleGerminationUIToolkit.Instance?.SetPanelOpen(false);
            LineageWorkshopUIToolkit.Instance?.SetPanelOpen(false);
        }

        private void OpenSettings()
        {
            CloseAllGameplayPanels();
            _settingsOpen = true;
            InputRouter.SetModalUi(true);
        }

        private void CloseSettings()
        {
            _settingsOpen = false;
            InputRouter.SetModalUi(false);
        }

        private void ApplyStoredSettings()
        {
            bool fullscreen = PlayerPrefs.GetInt(FullscreenPreferenceKey, Screen.fullScreen ? 1 : 0) == 1;
            int targetFps = PlayerPrefs.GetInt(TargetFpsPreferenceKey, 60);
            if (_fullscreenToggle != null)
            {
                _fullscreenToggle.SetValueWithoutNotify(fullscreen);
            }
            Application.targetFrameRate = targetFps;
        }

        private void SetFullscreen(bool fullscreen)
        {
            Screen.fullScreen = fullscreen;
            PlayerPrefs.SetInt(FullscreenPreferenceKey, fullscreen ? 1 : 0);
            ShowSettingsFeedback(fullscreen ? "已切换为全屏。" : "已切换为窗口模式。");
        }

        private void SetTargetFrameRate(int fps)
        {
            Application.targetFrameRate = fps;
            PlayerPrefs.SetInt(TargetFpsPreferenceKey, fps);
            ShowSettingsFeedback($"目标帧率已设为 {fps} FPS。");
        }

        private void ShowSettingsFeedback(string message)
        {
            if (_settingsFeedbackLabel != null)
            {
                _settingsFeedbackLabel.text = message;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
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
