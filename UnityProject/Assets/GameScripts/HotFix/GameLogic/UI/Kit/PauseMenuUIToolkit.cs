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
    /// 入口：继续游戏、按键设置、通知中心、图鉴、统计、保存并返回主菜单（二次确认，写明会先保存到哪个槽位；保存失败留在游戏里并说明原因）；
    /// 开发构建另有“界面基础件样例”。
    /// </summary>
    public sealed class PauseMenuUIToolkit : UiKitPanelHost
    {
        public const int Order = 30070;

        public static PauseMenuUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private VisualElement _root;
        private Label _feedback;
        private Label _worldSeed;
        private Label _worldSettings;
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
            // FG1-HUD-01（FGU-05）：图鉴入口——机制图鉴盖在暂停菜单上面，关掉回到暂停菜单。
            _codexButton = Bind(root, "PauseCodex", "pause.codex", () => GameLogic.Progression.MechanicCodex.Open(null, unlock: false));
            // FG2-FW-05（FGU-20）：固件库入口——盖在暂停菜单上面，关掉回到暂停菜单。
            FirmwareButton = Bind(root, "PauseFirmware", "pause.firmware", FirmwareLibraryPanelUIToolkit.Open);
            // FG2-FW-04（卡片“伤害归因进入统计面板”）：统计面板盖在暂停菜单上面，关掉回到暂停菜单。
            StatsButton = Bind(root, "PauseStats", "pause.stats", StatsPanelUIToolkit.Open);
            // FG4-ECO-01：物资面板盖在暂停菜单上面，关掉回到暂停菜单。
            ItemsButton = Bind(root, "PauseItems", "pause.items", ItemsPanelUIToolkit.Open);
            // FG4-ECO-06（FGU-15）：常驻规则面板盖在暂停菜单上面，关掉回到暂停菜单。
            RulesButton = Bind(root, "PauseRules", "pause.rules", RulesPanelUIToolkit.Open);
            Bind(root, "PauseSaveQuit", "ui.pause.save_and_quit", AskSaveAndQuit);
            BindCamera(root);
            BindReactionFeedback(root);
            _worldSeed = root.Q<Label>("PauseWorldSeed");
            _worldSettings = root.Q<Label>("PauseWorldSettings");
            Button copy = Bind(root, "PauseCopySeed", "ui.pause.copy_seed", CopySeed);
            if (copy != null)
            {
                UiTooltip.Attach(copy, () => new TooltipContent { Title = GameText.Get("ui.pause.copy_seed"), Body = SeedText() });
            }
            Button share = Bind(root, "PauseCopyShare", "ui.pause.copy_share", CopyShareCode);
            if (share != null)
            {
                UiTooltip.Attach(share, () => new TooltipContent
                {
                    Title = GameText.Get("ui.pause.copy_share"),
                    Body = (Campaign.WorldGen.WorldGenService.ShareCode(CampaignSession.Current) ?? string.Empty) + "\n" + GameText.Get("ui.newgame.share_hint"),
                });
            }
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

        // ── FG1-HUD-01：接入镜头设置（FG01 第 4 章“接入时镜头的缩放和跟随力度可以在设置里调”）──
        private Button _codexButton;
        private Slider _cameraZoom;
        private Slider _cameraFollow;
        private Label _cameraZoomLabel;
        private Label _cameraFollowLabel;
        private Button _cameraReset;

        public Button CodexButton => _codexButton;
        public Button StatsButton { get; private set; }
        public Button ItemsButton { get; private set; }
        public Button RulesButton { get; private set; }
        public Button FirmwareButton { get; private set; }
        public Slider CameraZoomSlider => _cameraZoom;
        public Slider CameraFollowSlider => _cameraFollow;
        public Button CameraResetButton => _cameraReset;
        public string CameraZoomLabelText => _cameraZoomLabel?.text ?? string.Empty;
        public string CameraFollowLabelText => _cameraFollowLabel?.text ?? string.Empty;

        private void BindCamera(VisualElement root)
        {
            Label title = root.Q<Label>("PauseCameraTitle");
            if (title != null)
            {
                title.text = GameText.Get("pause.camera_title");
            }
            _cameraZoomLabel = root.Q<Label>("PauseCameraZoomLabel");
            _cameraFollowLabel = root.Q<Label>("PauseCameraFollowLabel");
            _cameraZoom = root.Q<Slider>("PauseCameraZoom");
            _cameraFollow = root.Q<Slider>("PauseCameraFollow");
            if (_cameraZoom != null)
            {
                _cameraZoom.lowValue = Settings.GameSettings.UplinkCameraZoomMin;
                _cameraZoom.highValue = Settings.GameSettings.UplinkCameraZoomMax;
                _cameraZoom.RegisterValueChangedCallback(evt =>
                {
                    Settings.GameSettings.SetUplinkCameraZoom(evt.newValue);
                    RefreshCameraLabels();
                });
                UiTooltip.Attach(_cameraZoom, () => new TooltipContent
                {
                    Title = GameText.Get("pause.camera_title"),
                    Body = GameText.Format("pause.camera_zoom_tip", Percent(Settings.GameSettings.UplinkCameraZoomMin), Percent(Settings.GameSettings.UplinkCameraZoomMax)),
                });
            }
            if (_cameraFollow != null)
            {
                _cameraFollow.lowValue = Settings.GameSettings.UplinkFollowMin;
                _cameraFollow.highValue = Settings.GameSettings.UplinkFollowMax;
                _cameraFollow.RegisterValueChangedCallback(evt =>
                {
                    Settings.GameSettings.SetUplinkFollowStrength(evt.newValue);
                    RefreshCameraLabels();
                });
                UiTooltip.Attach(_cameraFollow, () => new TooltipContent
                {
                    Title = GameText.Get("pause.camera_title"),
                    Body = GameText.Format("pause.camera_follow_tip", Percent(Settings.GameSettings.UplinkFollowMin), Percent(Settings.GameSettings.UplinkFollowMax)),
                });
            }
            _cameraReset = Bind(root, "PauseCameraReset", "pause.camera_reset", () =>
            {
                Settings.GameSettings.ResetUplinkCamera();
                SyncCameraSliders();
            });
            SyncCameraSliders();
        }

        // ── FG2-FW-04：战斗反馈（FGR-FW-043“慢放和镜头推动都可以在设置里关闭”；卡片“慢放、镜头推动、弹字三个设置开关”；FGR-SYS-020 游戏性）──
        private Toggle _reactionPopups;
        private Toggle _reactionSlowMotion;
        private Toggle _reactionNudge;

        public Toggle ReactionPopupsToggle => _reactionPopups;
        public Toggle ReactionSlowMotionToggle => _reactionSlowMotion;
        public Toggle ReactionNudgeToggle => _reactionNudge;
        public Button ReactionResetButton { get; private set; }
        public Button ReactionLogButton { get; private set; }

        private void BindReactionFeedback(VisualElement root)
        {
            Label title = root.Q<Label>("PauseReactionTitle");
            if (title != null)
            {
                title.text = GameText.Get("pause.reaction_title");
            }
            _reactionPopups = BindToggle(root, "PauseReactionPopups", "pause.reaction_popups", Settings.GameSettings.SetReactionPopupsEnabled,
                () => GameText.Format("pause.reaction_popups_tip", GameLogic.Campaign.Feedback.ReactionPopups.PerSecondCap));
            _reactionSlowMotion = BindToggle(root, "PauseReactionSlowMotion", "pause.reaction_slowmo", Settings.GameSettings.SetReactionSlowMotionEnabled,
                () => GameText.Format("pause.reaction_slowmo_tip", GameLogic.Campaign.Combat.ReactionFeedback.SlowMotionSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
            _reactionNudge = BindToggle(root, "PauseReactionNudge", "pause.reaction_nudge", Settings.GameSettings.SetReactionCameraNudgeEnabled,
                () => GameText.Get("pause.reaction_nudge_tip"));
            ReactionResetButton = Bind(root, "PauseReactionReset", "pause.reaction_reset", () =>
            {
                Settings.GameSettings.ResetReactionFeedback();
                SyncReactionToggles();
            });
            ReactionLogButton = Bind(root, "PauseReactionLog", "pause.reaction_log", ReactionLogPanelUIToolkit.Open);
            SyncReactionToggles();
        }

        private static Toggle BindToggle(VisualElement root, string name, string labelKey, System.Action<bool> setter, System.Func<string> tip)
        {
            Toggle t = root.Q<Toggle>(name);
            if (t == null)
            {
                return null;
            }
            t.label = GameText.Get(labelKey);
            t.RegisterValueChangedCallback(evt => setter(evt.newValue));
            UiTooltip.Attach(t, () => new TooltipContent { Title = GameText.Get(labelKey), Body = tip() });
            return t;
        }

        /// <summary>三个开关按当前设置同步（打开菜单、恢复默认时）。</summary>
        public void SyncReactionToggles()
        {
            _reactionPopups?.SetValueWithoutNotify(Settings.GameSettings.ReactionPopupsEnabled);
            _reactionSlowMotion?.SetValueWithoutNotify(Settings.GameSettings.ReactionSlowMotionEnabled);
            _reactionNudge?.SetValueWithoutNotify(Settings.GameSettings.ReactionCameraNudgeEnabled);
        }

        /// <summary>滑条与标签按当前设置同步（打开菜单、恢复默认时）。</summary>
        public void SyncCameraSliders()
        {
            _cameraZoom?.SetValueWithoutNotify(Settings.GameSettings.UplinkCameraZoom);
            _cameraFollow?.SetValueWithoutNotify(Settings.GameSettings.UplinkFollowStrength);
            RefreshCameraLabels();
        }

        private void RefreshCameraLabels()
        {
            if (_cameraZoomLabel != null)
            {
                _cameraZoomLabel.text = GameText.Format("pause.camera_zoom", Percent(Settings.GameSettings.UplinkCameraZoom));
            }
            if (_cameraFollowLabel != null)
            {
                _cameraFollowLabel.text = GameText.Format("pause.camera_follow", Percent(Settings.GameSettings.UplinkFollowStrength));
            }
        }

        private static string Percent(float v) => UnityEngine.Mathf.RoundToInt(v * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
                RefreshWorldInfo();
                SyncCameraSliders();
                SyncReactionToggles();
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

        /// <summary>种子与世界设置（FGR-GEN-001：种子显示在暂停菜单、可以一键复制；FG17 第 4 节：暂停菜单里可以查看当前世界的设置）。</summary>
        public void RefreshWorldInfo()
        {
            CampaignState state = CampaignSession.Current;
            if (_worldSeed != null)
            {
                _worldSeed.text = state?.World != null ? GameText.Format("ui.pause.world_seed", SeedText()) : string.Empty;
            }
            if (_worldSettings != null)
            {
                _worldSettings.text = Campaign.WorldGen.WorldGenService.DescribeSettings(state);
            }
        }

        public string WorldSeedLabelText => _worldSeed?.text ?? string.Empty;
        public string WorldSettingsLabelText => _worldSettings?.text ?? string.Empty;
        public string FeedbackText => _feedback?.text ?? string.Empty;

        private static string SeedText() =>
            CampaignSession.Current?.World != null
                ? CampaignSession.Current.World.WorldSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;

        /// <summary>把世界种子复制到系统剪贴板（按钮与自检同一入口）。</summary>
        public void CopySeed()
        {
            string seed = SeedText();
            if (string.IsNullOrEmpty(seed))
            {
                return;
            }
            UnityEngine.GUIUtility.systemCopyBuffer = seed;
            if (_feedback != null)
            {
                _feedback.text = GameText.Format("ui.pause.seed_copied", seed);
            }
        }

        /// <summary>FG3-GEN-01（FGR-GEN-071）：把分享短码复制到系统剪贴板（按钮与自检同一入口）。原型地形的旧存档没有短码，按钮什么也不做。</summary>
        public void CopyShareCode()
        {
            string code = Campaign.WorldGen.WorldGenService.ShareCode(CampaignSession.Current);
            if (string.IsNullOrEmpty(code))
            {
                return;
            }
            UnityEngine.GUIUtility.systemCopyBuffer = code;
            if (_feedback != null)
            {
                _feedback.text = GameText.Format("ui.pause.share_copied", code);
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
