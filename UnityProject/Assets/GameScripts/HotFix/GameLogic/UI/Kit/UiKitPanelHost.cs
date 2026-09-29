using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：UI 基础件面板的公共宿主。每个面板一个 GameObject、一个 UIDocument（UI Toolkit 红线 3），
    /// 共用 <c>BattleHudPanelSettings</c>，按 <c>UI_WORKFLOW_GUIDE.md</c> 分层表的 sortingOrder 摆放；
    /// UXML / PanelSettings 经 <see cref="GameModule.Resource"/> 异步加载，销毁时成对释放。
    /// 子类只做绑定（<see cref="OnReady"/>）。
    /// </summary>
    public abstract class UiKitPanelHost : MonoBehaviour
    {
        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        protected abstract string UxmlLocation { get; }
        protected abstract int SortingOrder { get; }

        public VisualElement Root { get; private set; }
        public bool IsReady => Root != null;

        /// <summary>本面板的分层（sortingOrder）。</summary>
        public int Layer => SortingOrder;

        /// <summary>
        /// 有没有层级高于 <paramref name="order"/> 的 UI 基础件面板正以模态开着（例如图鉴 30075、统计 30076、反应记录 30077、按键 30080）。
        /// 全局开关键（固件库键、图鉴键）据此判断：上面盖着更高层的面板时不在它下面开 / 关一个看不见的模态（否则 Esc 先关掉的是看不见的那个）。
        /// 只在按键当下调用，O(模态数)。
        /// </summary>
        public static bool AnyModalAbove(int order)
        {
            foreach (object owner in GameLogic.Core.InputRouter.ModalOwnerList)
            {
                if (owner is UiKitPanelHost host && host != null && host.Layer > order)
                {
                    return true;
                }
            }
            return false;
        }
        public UIDocument Document => _document;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>(UxmlLocation);
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }
            if (_visualTree == null || _panelSettings == null)
            {
                Log.Error($"[{GetType().Name}] 加载 {UxmlLocation} / BattleHudPanelSettings 失败，面板不可用。");
                return;
            }
            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = SortingOrder;
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }
            if (this == null || _document.rootVisualElement == null)
            {
                Log.Error($"[{GetType().Name}] rootVisualElement 等待超时。");
                return;
            }
            Root = _document.rootVisualElement;
            Root.pickingMode = PickingMode.Ignore;
            UiTextFocusProbe.Register(Root);
            OnReady(Root);
        }

        /// <summary>UXML 已实例化。子类在这里 Q 节点、注册事件。</summary>
        protected abstract void OnReady(VisualElement root);

        protected virtual void OnDestroy()
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
            UiTextFocusProbe.Unregister(Root);
            Root = null;
        }
    }
}
