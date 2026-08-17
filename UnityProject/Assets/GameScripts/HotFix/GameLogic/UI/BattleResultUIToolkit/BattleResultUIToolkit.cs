using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Battle;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版结算回顾面板（battle-ui-polish/story-003）。不接 [Window]/CellStageFlow._hub，
    /// 照抄 <see cref="BattleOverlayUIToolkit"/> 的"常驻单例、每帧轮询自控显隐"模式（D1）。
    /// 显示条件与其余四个既有控制器相反——本面板只在"不在战斗中且有结算结果"时显示（D3）。
    /// 正文直接复用 <see cref="CellDebugHud.BuildResultText"/>，保证与旧 IMGUI 结算块文案永远同源，
    /// 不重写字符串拼接逻辑（D4）。旧 IMGUI 结算块改绑 I 键作对照入口，默认关闭（D7）。
    /// </summary>
    public class BattleResultUIToolkit : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Label _resultBody;

        private bool _visible;

        /// <summary>供 execute_code 验收探针只读访问。</summary>
        public bool IsVisible => _visible;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleResultUI");
            // D1：PanelSettings 复用 story-001 已实证 ScaleWithScreenSize 的资源，本 story 零新建 .asset。
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // sortingOrder 数值表：HUD=0/Metabolic=3/Draft=6/Overlay=10 递增序列的下一档（D1）。
            _document.sortingOrder = 12;

            // UIDocument.rootVisualElement 在刚赋值 panelSettings 后偶发仍为 null
            // （面板尚未在本帧完成挂载，实测复现），有限帧数轮询等它就绪，避免
            // CacheNodes() 对 null 根节点查询直接崩溃、控制器永久半初始化。
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleResultUIToolkit] rootVisualElement 等待超时，结算面板未初始化。");
                return;
            }
            CacheNodes();
            _root.style.display = DisplayStyle.None;
        }

        private void CacheNodes()
        {
            _resultBody = _root.Q<Label>("ResultBody");
        }

        private void Update()
        {
            bool running = GameRoot.CellStage?.IsRunning ?? false;
            StageOutcome last = GameRoot.Director?.LastOutcome;
            bool hasResult = last != null && last.StageId != StageId.None;
            bool show = !running && hasResult;
            _visible = show;

            if (_root == null)
            {
                return;
            }
            _root.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }

            if (_resultBody != null)
            {
                _resultBody.text = CellDebugHud.BuildResultText(last);
            }
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
