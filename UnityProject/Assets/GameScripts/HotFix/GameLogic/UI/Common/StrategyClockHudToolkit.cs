using Cysharp.Threading.Tasks;
using GameLogic.Core;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// ER2-INPUT-01 AC-UI-005：战略速度 0.5x/1x/2x 按钮 + 暂停按钮 + 实际值 HUD。
    ///
    /// 全代码构建视觉树，不依赖 UXML/USS 资产——内容只有几个按钮和一个标签，没有复杂布局，
    /// 不值得为它新增一条美术资产管线依赖（同仓库其余 UI Toolkit 面板大多走 UXML，这里是
    /// 刻意的例外，见 <see cref="BuildUi"/>）。仍复用既有的 "BattleHudPanelSettings" 资产，
    /// 不新建 PanelSettings——排序/缩放模式与其余 HUD 保持一致。
    ///
    /// 常驻单例：细胞阶段与归还谷地互斥运行，两边共用同一个 <see cref="StrategyClock"/> 和这一个
    /// HUD 实例，谁在跑就显示谁（<see cref="Update"/> 里判断），不随场景 Enter/Exit 反复增删。
    /// </summary>
    public sealed class StrategyClockHudToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private VisualElement _bar;
        private Label _statusLabel;
        private Button _pauseButton;

        public static StrategyClockHudToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 3; // 高于 GameShellUIToolkit(2)/BattleHudToolkit，常驻可见角标。

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[StrategyClockHudToolkit] rootVisualElement 等待超时，速度 HUD 未初始化。");
                return;
            }

            BuildUi();
        }

        private void BuildUi()
        {
            _bar = new VisualElement();
            _bar.style.position = Position.Absolute;
            _bar.style.top = 8;
            _bar.style.right = 8;
            _bar.style.flexDirection = FlexDirection.Row;
            _bar.style.alignItems = Align.Center;
            _bar.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            _bar.style.paddingLeft = 8;
            _bar.style.paddingRight = 8;
            _bar.style.paddingTop = 4;
            _bar.style.paddingBottom = 4;
            _bar.style.display = DisplayStyle.None; // 默认隐藏，Update() 按当前是否有活跃场景决定。
            _root.Add(_bar);

            foreach (float speed in StrategyClock.AllowedMultipliers)
            {
                float captured = speed; // 闭包捕获，避免 foreach 变量复用坑。
                var btn = new Button(() => StrategyClock.SetSpeed(captured)) { text = FormatSpeed(speed) };
                btn.style.marginLeft = 2;
                btn.style.marginRight = 2;
                btn.style.minWidth = 36;
                _bar.Add(btn);
            }

            _pauseButton = new Button(() => GameRoot.ToggleWorldPause()) { text = "暂停" };
            _pauseButton.style.marginLeft = 10;
            _bar.Add(_pauseButton);

            _statusLabel = new Label();
            _statusLabel.style.marginLeft = 10;
            _statusLabel.style.color = Color.white;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _bar.Add(_statusLabel);
        }

        private void Update()
        {
            if (_bar == null)
            {
                return;
            }

            bool active = (GameRoot.CellStage != null && GameRoot.CellStage.IsRunning) ||
                (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive);
            _bar.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
            {
                return;
            }

            bool paused = GameRoot.IsWorldPaused;
            _pauseButton.text = paused ? "继续" : "暂停";
            _statusLabel.text = paused ? "已暂停" : FormatSpeed(StrategyClock.SpeedMultiplier);
        }

        private static string FormatSpeed(float speed)
        {
            return $"{speed:0.#}x";
        }

        private void OnDestroy()
        {
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
