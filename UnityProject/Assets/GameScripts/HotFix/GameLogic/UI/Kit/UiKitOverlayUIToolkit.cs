using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：最上层浮层宿主（确认框、右键菜单、悬停提示、拖影）。sortingOrder 高于所有面板与全屏模态，
    /// 保证从任何面板弹出的确认框 / 提示都不会被盖住（分层表见 UI_WORKFLOW_GUIDE.md 第 4 节）。
    /// 主菜单阶段就挂载（主菜单的改键冲突确认也要用它）。
    /// </summary>
    public sealed class UiKitOverlayUIToolkit : UiKitPanelHost
    {
        public const int Order = 30200;

        public static UiKitOverlayUIToolkit Instance { get; private set; }

        protected override string UxmlLocation => "UiKitOverlay";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnReady(VisualElement root)
        {
            UiConfirmDialog.BindView(root);
            UiTooltip.BindView(root);
            UiContextMenu.BindView(root);
            UiDragDrop.BindView(root);
        }

        private void Update()
        {
            if (!IsReady)
            {
                return;
            }
            UiTooltip.Tick();
            UiConfirmDialog.Tick();
            UiShortcutHint.RefreshIfChanged();
        }

        protected override void OnDestroy()
        {
            UiConfirmDialog.UnbindView();
            UiTooltip.UnbindView();
            UiContextMenu.UnbindView();
            UiDragDrop.UnbindView();
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
