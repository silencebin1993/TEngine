using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-001）：Esc 逐层返回的最后一层——暂停菜单。
    /// 打开：世界暂停（记下打开前是否已暂停，关闭时恢复原状）、输入上下文切到“界面”、压一层 Esc 栈；
    /// 入口：继续游戏、按键设置、通知中心、保存并返回主菜单（二次确认，写明会先保存到哪个槽位；保存失败留在游戏里并说明原因）；
    /// 开发构建另有“界面基础件样例”。
    /// </summary>
    public sealed class PauseMenuUIToolkit : UiKitPanelHost
    {
        public const int Order = 30070;

        public static PauseMenuUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private VisualElement _root;
        private Label _feedback;
        private bool _wasPausedBeforeOpen;

        protected override string UxmlLocation => "PauseMenu";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        public static void Open() => Instance?.SetOpen(true);

        public static void Close() => Instance?.SetOpen(false);

        /// <summary>离开世界时收起：不再去改一个即将卸载的世界的暂停状态。</summary>
        public static void CloseForExit()
        {
            if (Instance != null && IsOpen)
            {
                Instance._wasPausedBeforeOpen = true;
                Instance.SetOpen(false);
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            _root = root.Q<VisualElement>("PauseMenuRoot");
            _feedback = root.Q<Label>("PauseFeedback");
            root.Q<Label>("PauseMenuTitle").text = GameText.Get("ui.pause.title");
            Bind(root, "PauseResume", "ui.pause.resume", () => SetOpen(false));
            Bind(root, "PauseKeyBindings", "ui.pause.keybinds", KeyBindingsPanelUIToolkit.Open);
            Bind(root, "PauseNotifications", "ui.pause.notifications", () =>
            {
                SetOpen(false);
                NotificationHudUIToolkit.OpenCenter();
            });
            Bind(root, "PauseSaveQuit", "ui.pause.save_and_quit", AskSaveAndQuit);
            Button gallery = Bind(root, "PauseGallery", "ui.pause.gallery", UiKitGalleryUIToolkit.Open);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            gallery?.RemoveFromClassList("uk-hidden");
#endif
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
        }

        private static Button Bind(VisualElement root, string name, string textKey, System.Action onClick)
        {
            Button b = root.Q<Button>(name);
            if (b == null)
            {
                return null;
            }
            b.text = GameText.Get(textKey);
            b.clicked += onClick;
            return b;
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.PauseMenuFirstOpen);
                _wasPausedBeforeOpen = GameRoot.IsWorldPaused;
                GameRoot.SetWorldPaused(true);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                if (_feedback != null)
                {
                    _feedback.text = string.Empty;
                }
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                if (!_wasPausedBeforeOpen)
                {
                    GameRoot.SetWorldPaused(false);
                }
            }
        }

        private void AskSaveAndQuit()
        {
            int slot = CampaignSession.ActiveSlotIndex;
            var request = new ConfirmRequest
            {
                Title = GameText.Get("ui.pause.quit_title"),
                OnConfirm = () => SaveAndQuit(slot),
            };
            request.Lines.Add(GameText.Format("ui.pause.quit_line", CampaignSlotText.SlotTitle(slot)));
            UiConfirmDialog.Show(request);
        }

        private void SaveAndQuit(int slot)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || slot < 0)
            {
                SetOpen(false);
                GameRoot.EndRun();
                return;
            }
            SaveResult result = SaveForQuit(slot);
            if (!result.Success)
            {
                if (_feedback != null)
                {
                    _feedback.text = GameText.Format("ui.pause.quit_failed", result.Message ?? string.Empty);
                }
                return;
            }
            _wasPausedBeforeOpen = true; // 马上离开区域，不要在关闭时再去改一个即将销毁的世界的暂停状态。
            SetOpen(false);
            GameRoot.EndRun();
        }

        /// <summary>“保存并返回主菜单”的存档部分（自检直接调）：先把区域实时状态写回记录，再走与自动存档同一个
        /// “导出机器记录 → 写盘”入口——不能直接 CampaignSaveService.Save，MachineRecords 只是存档前才刷新的快照，
        /// 跳过导出会把上次自动存档之后的新机器、阵亡、经历写丢。</summary>
        public static SaveResult SaveForQuit(int slot)
        {
            GameRoot.SyncActiveRegionForSave();
            return CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
        }

        private void Update()
        {
            // 暂停菜单只属于游戏世界：世界没了（回主菜单、区域被卸载）就收起，不带着暂停与模态残留盖在主菜单上。
            if (IsOpen && !(CampaignSession.Current != null && GameRoot.AnyRegionActive))
            {
                _wasPausedBeforeOpen = true;
                SetOpen(false);
            }
        }

        protected override void OnDestroy()
        {
            if (IsOpen)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
