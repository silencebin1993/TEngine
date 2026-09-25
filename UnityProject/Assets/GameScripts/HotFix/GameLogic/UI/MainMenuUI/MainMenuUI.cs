using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Settings;
using Log = TEngine.Log;

namespace GameLogic
{
    /// <summary>ER1-SAVE-01 骨架 + ER2-BOOT-01 战役入口收尾：《地球归还》正式主菜单——
    /// 新建/继续/读取/设置/退出五个入口 + 存档卡四态（空槽/正常/坏档/版本不兼容）。
    /// 走 TEngine UIWindow 规范；完整机械美术主题由 ER2-THEME-01 接手打磨，本类不做视觉终稿。
    ///
    /// 冷启动第一屏由 <c>GameApp.StartGameLogic</c> 直接 <c>ShowUIAsync&lt;MainMenuUI&gt;()</c> 打开
    /// （见该方法注释），归还谷地/细胞阶段自然结束后 <see cref="GameLogic.Stage.GameRoot"/> 也会
    /// 重新打开本窗口，"返回菜单"因此复用同一入口。
    ///
    /// ER2-SCENE-01 起：新建/继续/读取成功后，把结果写入 <see cref="CampaignSession"/> 后关闭主菜单
    /// 并调用 <see cref="GameLogic.Stage.GameRoot.StartHomeValley()"/>/
    /// <see cref="GameLogic.Stage.GameRoot.ResumeHomeValley()"/> 进入归还谷地正式场景
    /// （<see cref="GameLogic.Campaign.Regions.HomeValleyController"/>）。</summary>
    [Window(UILayer.UI, location: "MainMenuUI")]
    public class MainMenuUI : UIWindow
    {
        private enum MenuView
        {
            Root,
            SlotList,
            ConfirmOverwrite,
            Settings,
        }

        #region 脚本工具生成的代码

        private Transform _tfRoot;
        private Button _btnNew;
        private Button _btnContinue;
        private Text _textContinueReason;
        private Button _btnLoad;
        private Button _btnSettings;
        private Button _btnQuit;

        private Transform _tfSettings;
        private Button _btnSettingsBack;
        private Button _btnResetAllDefaults;

        /// <summary>ER2-INPUT-01：可重绑动作 → 按钮文字。顺序即 UI 顺序，也是
        /// <see cref="InputBindingSet.RebindableActions"/> 在设置面板里展示的全部动作。</summary>
        private readonly Dictionary<GameActionId, Text> _rebindLabels = new Dictionary<GameActionId, Text>();
        private static readonly (GameActionId Action, string RowLabel)[] RebindRows =
        {
            (GameActionId.Interact, "世界交互"),
            (GameActionId.CycleControlTarget, "循环接管目标"),
            (GameActionId.ToggleCameraView, "切换镜头视角"),
            (GameActionId.TogglePause, "暂停/继续"),
            (GameActionId.Cancel, "取消/返回"),
            (GameActionId.ToggleMissionLog, "任务日志与地图"),
            // FG0-UX-01：原“冲刺”行（DirectSkillSlot0）在 0.2 是尚未开放的“接入技能 1”，换成已接入玩法的“通知中心”。
            (GameActionId.ToggleNotificationCenter, "通知中心"),
        };

        private Toggle _toggleEdgePan;
        private Toggle _toggleScreenShake;
        private Toggle _toggleFlashReduction;
        private Toggle _toggleSubtitles;
        private Toggle _toggleColorblindIcons;

        private Slider _sliderUiScale;
        private Slider _sliderCameraSpeed;
        private Slider _sliderMasterVolume;
        private Slider _sliderMusicVolume;
        private Slider _sliderSfxVolume;
        private Slider _sliderUiVolume;

        /// <summary>正在等待玩家按下新键的动作；null＝当前没有在监听重绑。</summary>
        private GameActionId? _rebindListening;
        /// <summary>FG0-UX-01：改键采集（组合键、单独修饰键、滚轮、鼠标键；Esc 取消）。与 UI Toolkit 按键面板共用同一实现。</summary>
        private readonly InputCapture _rebindCapture = new InputCapture();

        /// <summary>FG0-UX-01：设置页上“全部按键设置…”按钮（运行时从“恢复默认”按钮复制一个，放在它后面，由同一个布局组排版）。
        /// 打开 UI Toolkit 的完整按键面板（全部动作、按上下文分页、搜索、冲突确认）。</summary>
        private Button _btnAllKeyBindings;

        private Transform _tfSlotList;
        private Text[] _slotInfoTexts;
        private Button[] _slotActionButtons;
        private Text[] _slotActionLabels;
        private Button _btnBack;

        private Transform _tfConfirmOverwrite;
        private Text _textConfirmInfo;
        private Button _btnConfirmYes;
        private Button _btnConfirmNo;
        private bool _visualSystemApplied;

        protected override void ScriptGenerator()
        {
            _tfRoot = FindChild("m_tf_Root");
            _btnNew = FindChildComponent<Button>("m_tf_Root/m_btn_New");
            _btnContinue = FindChildComponent<Button>("m_tf_Root/m_btn_Continue");
            _textContinueReason = FindChildComponent<Text>("m_tf_Root/m_text_ContinueReason");
            _btnLoad = FindChildComponent<Button>("m_tf_Root/m_btn_Load");
            _btnSettings = FindChildComponent<Button>("m_tf_Root/m_btn_Settings");
            _btnQuit = FindChildComponent<Button>("m_tf_Root/m_btn_Quit");
            _btnNew.onClick.AddListener(OnNewClicked);
            _btnContinue.onClick.AddListener(OnContinueClicked);
            _btnLoad.onClick.AddListener(OnLoadClicked);
            _btnSettings.onClick.AddListener(OnSettingsClicked);
            _btnQuit.onClick.AddListener(OnQuitClicked);

            _tfSettings = FindChild("m_tf_Settings");
            _btnSettingsBack = FindChildComponent<Button>("m_tf_Settings/m_btn_SettingsBack");
            _btnSettingsBack.onClick.AddListener(OnSettingsBackClicked);
            _btnResetAllDefaults = FindChildComponent<Button>("m_tf_Settings/m_btn_ResetAllDefaults");
            _btnResetAllDefaults.onClick.AddListener(OnResetAllDefaultsClicked);
            _btnAllKeyBindings = CreateAllKeyBindingsButton(_btnResetAllDefaults);
            _btnAllKeyBindings?.onClick.AddListener(OnAllKeyBindingsClicked);

            // ER2-INPUT-01 附带修复：ScrollRect 补了真正的 Viewport（RectMask2D）后，Content
            // (m_tf_SettingsColumns) 多套了一层 "Viewport" 节点，这里的路径常量必须跟着改，
            // 否则 FindChildComponent 全部找空，ScriptGenerator 会在这里 NullReferenceException
            // （2026-09-21 实锤过一次：加 Viewport 时漏改了这两个路径常量）。
            const string colLeft = "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColLeft";
            const string colRight = "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColRight";
            foreach ((GameActionId action, string _) in RebindRows)
            {
                Button btn = FindChildComponent<Button>(colLeft + "/m_row_Rebind_" + action + "/m_btn_Rebind_" + action);
                if (btn == null)
                {
                    // 2026-09-25 实锤：代码里新增了重绑行、预制体没加对应节点，这里空引用让整个主菜单初始化中断
                    // （后面的开关/滑条/存档槽全没绑上，新建进不了游戏）。缺一行只影响这一行，不能拖垮整页；
                    // 自检 MainMenuSelfCheck 会因为缺行直接失败。
                    Log.Error($"[MainMenuUI] 预制体缺少重绑行 m_row_Rebind_{action}，该动作暂时无法在设置里重绑。");
                    continue;
                }
                _rebindLabels[action] = btn.GetComponentInChildren<Text>();
                // FG0-UX-01：行名跟动作登记表同一个名字（文本键，随语言切换），不再用预制体里写死的中文。
                foreach (Text rowText in btn.transform.parent.GetComponentsInChildren<Text>(true))
                {
                    if (!rowText.transform.IsChildOf(btn.transform) && InputActionCatalog.TryGet(action, out InputActionDef rowDef))
                    {
                        rowText.text = rowDef.DisplayName;
                    }
                }
                GameActionId captured = action; // 闭包捕获，避免 foreach 变量复用坑。
                btn.onClick.AddListener(() => OnRebindButtonClicked(captured));
            }

            _toggleEdgePan = FindChildComponent<Toggle>(colLeft + "/m_row_Toggle_EdgePan/m_toggle_EdgePan");
            _toggleScreenShake = FindChildComponent<Toggle>(colLeft + "/m_row_Toggle_ScreenShake/m_toggle_ScreenShake");
            _toggleFlashReduction = FindChildComponent<Toggle>(colRight + "/m_row_Toggle_FlashReduction/m_toggle_FlashReduction");
            _toggleSubtitles = FindChildComponent<Toggle>(colRight + "/m_row_Toggle_Subtitles/m_toggle_Subtitles");
            _toggleColorblindIcons = FindChildComponent<Toggle>(colRight + "/m_row_Toggle_ColorblindIcons/m_toggle_ColorblindIcons");
            _toggleEdgePan.onValueChanged.AddListener(GameSettings.SetEdgePanEnabled);
            _toggleScreenShake.onValueChanged.AddListener(GameSettings.SetScreenShakeEnabled);
            _toggleFlashReduction.onValueChanged.AddListener(GameSettings.SetFlashReductionEnabled);
            _toggleSubtitles.onValueChanged.AddListener(GameSettings.SetSubtitlesEnabled);
            _toggleColorblindIcons.onValueChanged.AddListener(GameSettings.SetColorblindSafeIconsEnabled);

            _sliderUiScale = FindChildComponent<Slider>(colRight + "/m_row_Slider_UiScale/m_slider_UiScale");
            _sliderCameraSpeed = FindChildComponent<Slider>(colRight + "/m_row_Slider_CameraSpeed/m_slider_CameraSpeed");
            _sliderMasterVolume = FindChildComponent<Slider>(colRight + "/m_row_Slider_MasterVolume/m_slider_MasterVolume");
            _sliderMusicVolume = FindChildComponent<Slider>(colRight + "/m_row_Slider_MusicVolume/m_slider_MusicVolume");
            _sliderSfxVolume = FindChildComponent<Slider>(colRight + "/m_row_Slider_SfxVolume/m_slider_SfxVolume");
            _sliderUiVolume = FindChildComponent<Slider>(colRight + "/m_row_Slider_UiVolume/m_slider_UiVolume");
            // FG0-UX-01（FGR-UX-060）：UI 缩放范围 80%～150% 来自 fg.TbUiTuning，不信预制体里写的滑条上下限。
            _sliderUiScale.minValue = GameSettings.UiScaleMin;
            _sliderUiScale.maxValue = GameSettings.UiScaleMax;
            _sliderUiScale.onValueChanged.AddListener(GameSettings.SetUiScale);
            _sliderCameraSpeed.onValueChanged.AddListener(GameSettings.SetCameraSpeedMultiplier);
            _sliderMasterVolume.onValueChanged.AddListener(GameSettings.SetMasterVolume);
            _sliderMusicVolume.onValueChanged.AddListener(GameSettings.SetMusicVolume);
            _sliderSfxVolume.onValueChanged.AddListener(GameSettings.SetSfxVolume);
            _sliderUiVolume.onValueChanged.AddListener(GameSettings.SetUiVolume);

            _tfSlotList = FindChild("m_tf_SlotList");
            _slotInfoTexts = new[]
            {
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot0/m_text_Slot0Info"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot1/m_text_Slot1Info"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot2/m_text_Slot2Info"),
            };
            _slotActionButtons = new[]
            {
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot0/m_btn_Slot0Action"),
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot1/m_btn_Slot1Action"),
                FindChildComponent<Button>("m_tf_SlotList/m_tf_Slot2/m_btn_Slot2Action"),
            };
            _slotActionLabels = new[]
            {
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot0/m_btn_Slot0Action/m_text_Slot0ActionLabel"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot1/m_btn_Slot1Action/m_text_Slot1ActionLabel"),
                FindChildComponent<Text>("m_tf_SlotList/m_tf_Slot2/m_btn_Slot2Action/m_text_Slot2ActionLabel"),
            };
            _btnBack = FindChildComponent<Button>("m_tf_SlotList/m_btn_Back");
            _slotActionButtons[0].onClick.AddListener(() => OnSlotActionClicked(0));
            _slotActionButtons[1].onClick.AddListener(() => OnSlotActionClicked(1));
            _slotActionButtons[2].onClick.AddListener(() => OnSlotActionClicked(2));
            _btnBack.onClick.AddListener(OnBackClicked);

            _tfConfirmOverwrite = FindChild("m_tf_ConfirmOverwrite");
            _textConfirmInfo = FindChildComponent<Text>("m_tf_ConfirmOverwrite/m_text_ConfirmInfo");
            _btnConfirmYes = FindChildComponent<Button>("m_tf_ConfirmOverwrite/m_btn_ConfirmYes");
            _btnConfirmNo = FindChildComponent<Button>("m_tf_ConfirmOverwrite/m_btn_ConfirmNo");
            _btnConfirmYes.onClick.AddListener(OnConfirmYesClicked);
            _btnConfirmNo.onClick.AddListener(OnConfirmNoClicked);
        }

        #endregion

        /// <summary>覆盖写确认目标槽位；仅在"新建"遇到三槽全满时使用。-1 表示当前没有待确认的覆盖。</summary>
        private int _pendingOverwriteSlot = -1;
        /// <summary>覆盖确认框取消后回到哪一页（从存档列表点"新建于此槽"时回列表，从主菜单"新建"时回主菜单）。</summary>
        private MenuView _confirmReturnView = MenuView.Root;

        protected override void OnCreate()
        {
            ApplyVisualSystem();
            AttachClickSounds();
            SetView(MenuView.Root);
        }

        /// <summary>FG0-UX-01：在“恢复默认”按钮后面复制出“全部按键设置…”。两者同属设置页的布局组，排版由布局组负责，
        /// 不写坐标；文字走文本键。预制体没有这个节点（GameRes 子仓的预制体只能在编辑器里安全改，
        /// 这里复制既有按钮避免手改 YAML），MainMenuSelfCheck 断言它存在、可点、能打开面板。</summary>
        private static Button CreateAllKeyBindingsButton(Button template)
        {
            if (template == null)
            {
                return null;
            }
            GameObject copy = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            copy.name = "m_btn_AllKeyBindings";
            copy.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            Button button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            Text label = copy.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = Localization.GameText.Get("ui.keybind.open_all");
            }
            return button;
        }

        /// <summary>ER8（DEBT-ER2BOOT01-04 音效部分）：主菜单与设置页此前按任何按钮都没有声音。UI Toolkit 面板的
        /// 点击音挂在共享面板根上（<c>FeedbackCaptionHudUIToolkit</c>），uGUI 菜单不在那个面板里，这里给窗口内
        /// 每个按钮补一次（菜单按钮都在预制体里，运行时不新建按钮）。</summary>
        private void AttachClickSounds()
        {
            AttachClickSounds(gameObject);
        }

        /// <summary>给 <paramref name="root"/> 下每个按钮挂点击音，返回挂了几个（自检直接对预制体调用）。</summary>
        public static int AttachClickSounds(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }
            Button[] buttons = root.GetComponentsInChildren<Button>(true);
            foreach (Button button in buttons)
            {
                button.onClick.AddListener(PlayClickSound);
            }
            return buttons.Length;
        }

        private static void PlayClickSound() => Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.UiClick);

        protected override void OnRefresh()
        {
            ApplyVisualSystem();
            SetView(MenuView.Root);
        }

        // 正式菜单保留既有节点与所有业务绑定，只在运行时统一整理视觉层级。
        // 这样存档、重绑、无障碍和音量设置的功能不依赖预制体美术改动，也不会因改名失联。
        private void ApplyVisualSystem()
        {
            if (_visualSystemApplied)
            {
                return;
            }

            _visualSystemApplied = true;

            Color page = new Color(0.025f, 0.055f, 0.075f, 1f);
            Color surface = new Color(0.055f, 0.12f, 0.15f, 0.96f);
            Color raised = new Color(0.075f, 0.17f, 0.20f, 0.98f);
            Color edge = new Color(0.20f, 0.64f, 0.62f, 0.42f);
            Color text = new Color(0.88f, 0.96f, 0.95f, 1f);
            Color muted = new Color(0.55f, 0.70f, 0.71f, 1f);
            Color accent = new Color(0.25f, 0.88f, 0.76f, 1f);
            Color warning = new Color(0.96f, 0.61f, 0.29f, 1f);
            Color danger = new Color(0.90f, 0.31f, 0.31f, 1f);

            RectTransform canvasRoot = _tfRoot.parent as RectTransform;
            if (canvasRoot != null)
            {
                ConfigureMenuCanvas(canvasRoot);
                EnsureSurface(canvasRoot, "MenuBackdrop", page, Color.clear, 0, true);
            }

            ConfigureView(_tfRoot as RectTransform, new Vector2(560f, 560f), surface, edge, 34);
            ConfigureView(_tfSlotList as RectTransform, new Vector2(1120f, 720f), surface, edge, 34);
            ConfigureView(_tfConfirmOverwrite as RectTransform, new Vector2(620f, 460f), raised, new Color(warning.r, warning.g, warning.b, 0.76f), 30);
            ConfigureView(_tfSettings as RectTransform, new Vector2(1320f, 790f), surface, edge, 30);
            FitViewsToCanvas(force: true);

            Font menuFont = _textContinueReason.font;
            EnsureViewHeader(_tfRoot, "VisualMenuHeader", "地球归还", "EARTH RECLAMATION  ·  CAMPAIGN COMMAND", menuFont, accent);
            EnsureViewHeader(_tfSlotList, "VisualSlotHeader", "战役档案", "选择一个档案继续，或在空槽创建新战役", menuFont, accent);
            EnsureViewHeader(_tfConfirmOverwrite, "VisualConfirmHeader", "覆盖确认", "这项操作不可撤销", menuFont, warning);

            ConfigureMainActions(accent, text, muted, warning, danger);
            ConfigureSaveCards(text, muted, accent, warning);
            ConfigureSettings(text, muted, accent, edge, raised);
            ConfigureTypography(text, muted, accent, warning);

            Canvas.ForceUpdateCanvases();
            RebuildLayout(_tfRoot as RectTransform);
            RebuildLayout(_tfSlotList as RectTransform);
            RebuildLayout(_tfConfirmOverwrite as RectTransform);
            RebuildLayout(_tfSettings as RectTransform);
        }

        // ER8-CONTENT-01 AC-UI-004：四个视图的设计尺寸（1920×1080 参考坐标）。UI 缩放 140% 时根画布只剩约
        // 1371×771（5:4 屏幕更窄），固定尺寸会越出屏幕——实际尺寸取“设计尺寸”与“画布减边距”的较小值，
        // 设置页主体本来就在滚动视图里，变矮不丢内容。
        private readonly Dictionary<RectTransform, Vector2> _viewDesignSizes = new Dictionary<RectTransform, Vector2>(4);
        private Vector2 _fittedCanvasSize;
        private const float ViewScreenMargin = 24f;

        private void ConfigureView(RectTransform view, Vector2 size, Color fill, Color edge, int padding)
        {
            if (view == null)
            {
                return;
            }

            _viewDesignSizes[view] = size;
            view.anchorMin = new Vector2(0.5f, 0.5f);
            view.anchorMax = new Vector2(0.5f, 0.5f);
            view.pivot = new Vector2(0.5f, 0.5f);
            view.anchoredPosition = Vector2.zero;
            view.sizeDelta = size;

            EnsureSurface(view, "VisualSurface", fill, edge, 2, true);

            VerticalLayoutGroup layout = view.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(padding, padding, padding, padding);
                layout.spacing = 12;
                layout.childAlignment = TextAnchor.UpperCenter;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }
        }

        // MainMenuUI 的 Canvas 实际是嵌套在共享 UIRoot/UICanvas 之下的子 Canvas，不是独立根 Canvas。
        // 嵌套 Canvas 的 renderMode 由 Unity 强制跟随根 Canvas——这里如果手动赋值 renderMode，赋值会被
        // Unity 转发改写共享的根 Canvas（实测会把全局 UICanvas 从 ScreenSpaceCamera 冲成
        // ScreenSpaceOverlay，波及其他所有窗口）；这里再加的 CanvasScaler 对嵌套 Canvas 也完全不生效
        // （只有根 Canvas 的 CanvasScaler 真正参与缩放计算）。参考分辨率的纠正统一放在
        // GameApp.FixUiRootReferenceResolution() 里对共享根 Canvas 做一次，这里只保留对嵌套 Canvas
        // 真正有效的部分：同级绘制顺序。
        private static void ConfigureMenuCanvas(RectTransform canvasRoot)
        {
            Canvas canvas = canvasRoot.GetComponent<Canvas>();
            if (canvas == null)
            {
                return;
            }

            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;
        }

        private void ConfigureMainActions(Color accent, Color text, Color muted, Color warning, Color danger)
        {
            StyleButton(_btnNew, accent, new Color(0.72f, 1f, 0.90f, 1f), new Color(0.02f, 0.10f, 0.10f, 1f), 58, true);
            StyleButton(_btnContinue, new Color(0.08f, 0.25f, 0.28f, 1f), accent, text, 46, false);
            StyleButton(_btnLoad, new Color(0.06f, 0.16f, 0.20f, 1f), new Color(0.22f, 0.54f, 0.58f, 1f), text, 46, false);
            StyleButton(_btnSettings, new Color(0.06f, 0.16f, 0.20f, 1f), new Color(0.22f, 0.54f, 0.58f, 1f), text, 46, false);
            StyleButton(_btnQuit, new Color(0.15f, 0.075f, 0.09f, 1f), danger, new Color(1f, 0.80f, 0.80f, 1f), 40, false);
            StyleButton(_btnBack, new Color(0.06f, 0.16f, 0.20f, 1f), new Color(0.22f, 0.54f, 0.58f, 1f), text, 42, false);
            StyleButton(_btnConfirmYes, new Color(0.40f, 0.14f, 0.12f, 1f), danger, new Color(1f, 0.85f, 0.84f, 1f), 48, true);
            StyleButton(_btnConfirmNo, new Color(0.06f, 0.16f, 0.20f, 1f), new Color(0.22f, 0.54f, 0.58f, 1f), text, 48, false);
            StyleButton(_btnSettingsBack, accent, new Color(0.72f, 1f, 0.90f, 1f), new Color(0.02f, 0.10f, 0.10f, 1f), 46, true);
            StyleButton(_btnResetAllDefaults, new Color(0.18f, 0.12f, 0.06f, 1f), warning, new Color(1f, 0.87f, 0.67f, 1f), 42, false);

            _textContinueReason.color = warning;
            _textContinueReason.alignment = TextAnchor.MiddleCenter;
            _textContinueReason.fontSize = 14;
            SetLayoutHeight(_textContinueReason.transform, 24, false);

            _textConfirmInfo.color = new Color(1f, 0.84f, 0.55f, 1f);
            _textConfirmInfo.alignment = TextAnchor.UpperCenter;
            _textConfirmInfo.horizontalOverflow = HorizontalWrapMode.Wrap;
            _textConfirmInfo.verticalOverflow = VerticalWrapMode.Overflow;
            SetLayoutHeight(_textConfirmInfo.transform, 150, false);
        }

        private void ConfigureSaveCards(Color text, Color muted, Color accent, Color warning)
        {
            for (int i = 0; i < _slotInfoTexts.Length; i++)
            {
                Transform card = _slotInfoTexts[i].transform.parent;
                EnsureSurface(card as RectTransform, "VisualSurface", new Color(0.035f, 0.10f, 0.13f, 0.96f), new Color(accent.r, accent.g, accent.b, 0.30f), 1, true);
                SetLayoutHeight(card, 142, false);
                _slotInfoTexts[i].color = text;
                _slotInfoTexts[i].fontSize = 16;
                _slotInfoTexts[i].alignment = TextAnchor.UpperLeft;
                _slotInfoTexts[i].horizontalOverflow = HorizontalWrapMode.Wrap;
                _slotInfoTexts[i].verticalOverflow = VerticalWrapMode.Overflow;
                StyleButton(_slotActionButtons[i], new Color(0.07f, 0.24f, 0.25f, 1f), accent, text, 42, false);
                // 槽位行是"信息文本(flexible) + 操作按钮(紧凑宽度)"布局；StyleButton 统一把按钮
                // flexibleWidth 设成 1 会让按钮抢占一半行宽，这里改回紧凑宽度，把空间让给信息文本。
                LayoutElement actionLayout = _slotActionButtons[i].GetComponent<LayoutElement>();
                actionLayout.flexibleWidth = 0;
                actionLayout.preferredWidth = 180;
                actionLayout.minWidth = 160;
            }
        }

        private void ConfigureSettings(Color text, Color muted, Color accent, Color edge, Color raised)
        {
            Transform columns = _tfSettings.Find("m_scroll_Settings/Viewport/m_tf_SettingsColumns");
            if (columns != null)
            {
                EnsureSurface(columns as RectTransform, "VisualSurface", new Color(0.025f, 0.075f, 0.095f, 0.78f), new Color(edge.r, edge.g, edge.b, 0.55f), 1, true);
            }

            SetLayoutHeight(_tfSettings.Find("m_text_SettingsInfo"), 42, false);
            SetLayoutHeight(_tfSettings.Find("m_scroll_Settings"), 0, true);

            string[] rows =
            {
                "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColLeft",
                "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColRight",
            };
            foreach (string path in rows)
            {
                Transform column = FindChild(path);
                if (column == null)
                {
                    continue;
                }

                foreach (Transform row in column)
                {
                    if (row.name.StartsWith("m_row_", StringComparison.Ordinal))
                    {
                        EnsureSurface(row as RectTransform, "VisualRow", raised, new Color(edge.r, edge.g, edge.b, 0.38f), 1, true);
                    }
                }
            }

            foreach (Toggle toggle in _tfSettings.GetComponentsInChildren<Toggle>(true))
            {
                Image background = toggle.targetGraphic as Image;
                if (background != null)
                {
                    background.color = new Color(0.04f, 0.13f, 0.16f, 1f);
                }
                Image checkmark = toggle.graphic as Image;
                if (checkmark != null)
                {
                    checkmark.color = accent;
                }
            }

            foreach (Slider slider in _tfSettings.GetComponentsInChildren<Slider>(true))
            {
                if (slider.fillRect != null)
                {
                    Image fill = slider.fillRect.GetComponent<Image>();
                    if (fill != null) fill.color = accent;
                }
                if (slider.handleRect != null)
                {
                    Image handle = slider.handleRect.GetComponent<Image>();
                    if (handle != null) handle.color = new Color(0.82f, 1f, 0.94f, 1f);
                }
            }

            foreach (Button rebind in _tfSettings.GetComponentsInChildren<Button>(true))
            {
                if (rebind.name.StartsWith("m_btn_Rebind_", StringComparison.Ordinal))
                {
                    StyleButton(rebind, new Color(0.06f, 0.21f, 0.23f, 1f), accent, text, 34, false);
                    // 同上：重绑按钮是行内的"当前键位"展示控件，不该抢占一半行宽。
                    LayoutElement rebindLayout = rebind.GetComponent<LayoutElement>();
                    rebindLayout.flexibleWidth = 0;
                    rebindLayout.preferredWidth = 140;
                    rebindLayout.minWidth = 120;
                }
            }
        }

        private void ConfigureTypography(Color text, Color muted, Color accent, Color warning)
        {
            foreach (Text label in _tfRoot.parent.GetComponentsInChildren<Text>(true))
            {
                label.raycastTarget = false;
                if (label.transform.parent != null && label.transform.parent.name.StartsWith("Visual", StringComparison.Ordinal))
                {
                    continue;
                }
                if (label.name.StartsWith("m_text_Header", StringComparison.Ordinal) || label.name == "m_text_SettingsInfo")
                {
                    label.color = accent;
                    label.fontStyle = FontStyle.Bold;
                    label.fontSize = 19;
                }
                else if (label.name.StartsWith("m_text_Label", StringComparison.Ordinal))
                {
                    label.color = muted;
                    label.fontSize = 15;
                }
                else if (label.name.Contains("Confirm"))
                {
                    label.color = warning;
                    label.fontSize = 17;
                }
                else if (label != _textContinueReason)
                {
                    label.color = text;
                }
            }
        }

        private static void StyleButton(Button button, Color fill, Color border, Color labelColor, int height, bool primary)
        {
            if (button == null)
            {
                return;
            }

            Image image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = fill;
                image.raycastTarget = true;
            }

            Outline outline = button.GetComponent<Outline>();
            if (outline == null)
            {
                outline = button.gameObject.AddComponent<Outline>();
            }
            outline.effectColor = border;
            outline.effectDistance = new Vector2(1f, -1f);

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.78f, 0.90f, 0.88f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.50f, 0.54f, 0.55f, 0.65f);
            colors.colorMultiplier = 1f;
            button.colors = colors;

            LayoutElement element = button.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = button.gameObject.AddComponent<LayoutElement>();
            }
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1;

            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.color = labelColor;
                label.fontSize = primary ? 18 : 16;
                label.fontStyle = primary ? FontStyle.Bold : FontStyle.Normal;
                label.alignment = TextAnchor.MiddleCenter;
                label.raycastTarget = false;
            }
        }

        private static void EnsureViewHeader(Transform parent, string name, string title, string subtitle, Font font, Color accent)
        {
            if (parent == null || parent.Find(name) != null)
            {
                return;
            }

            GameObject header = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            header.transform.SetParent(parent, false);
            header.transform.SetSiblingIndex(1);

            VerticalLayoutGroup layout = header.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 3;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            LayoutElement size = header.GetComponent<LayoutElement>();
            size.preferredHeight = 82;
            size.minHeight = 82;

            Text titleLabel = CreateHeaderText(header.transform, "Title", title, font, 34, FontStyle.Bold, accent);
            Text subtitleLabel = CreateHeaderText(header.transform, "Subtitle", subtitle, font, 13, FontStyle.Normal, new Color(accent.r, accent.g, accent.b, 0.72f));
            titleLabel.GetComponent<LayoutElement>().preferredHeight = 48;
            subtitleLabel.GetComponent<LayoutElement>().preferredHeight = 22;
        }

        private static Text CreateHeaderText(Transform parent, string name, string value, Font font, int fontSize, FontStyle style, Color color)
        {
            GameObject labelObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            labelObject.transform.SetParent(parent, false);
            Text label = labelObject.GetComponent<Text>();
            label.font = font;
            label.text = value;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = color;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            return label;
        }

        private static void SetLayoutHeight(Transform transform, int height, bool flexible)
        {
            if (transform == null)
            {
                return;
            }

            LayoutElement element = transform.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = transform.gameObject.AddComponent<LayoutElement>();
            }
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleHeight = flexible ? 1f : 0f;
            element.flexibleWidth = 1f;
        }

        private static void RebuildLayout(RectTransform transform)
        {
            if (transform != null && transform.gameObject.activeInHierarchy)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(transform);
            }
        }

        private static Image EnsureSurface(RectTransform parent, string name, Color fill, Color edge, int edgeWidth, bool firstSibling)
        {
            if (parent == null)
            {
                return null;
            }

            Transform existing = parent.Find(name);
            Image image;
            if (existing == null)
            {
                GameObject surface = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                surface.transform.SetParent(parent, false);
                image = surface.GetComponent<Image>();
                image.raycastTarget = false;
                LayoutElement layoutElement = surface.AddComponent<LayoutElement>();
                layoutElement.ignoreLayout = true;
                RectTransform rect = surface.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                image = existing.GetComponent<Image>();
            }

            LayoutElement existingLayoutElement = image.GetComponent<LayoutElement>();
            if (existingLayoutElement != null)
            {
                existingLayoutElement.ignoreLayout = true;
            }

            if (firstSibling)
            {
                image.transform.SetAsFirstSibling();
            }
            image.color = fill;

            if (edgeWidth > 0)
            {
                Outline outline = image.GetComponent<Outline>();
                if (outline == null)
                {
                    outline = image.gameObject.AddComponent<Outline>();
                }
                outline.effectColor = edge;
                outline.effectDistance = new Vector2(edgeWidth, -edgeWidth);
            }

            return image;
        }

        private void SetView(MenuView view)
        {
            _tfRoot.gameObject.SetActive(view == MenuView.Root);
            _tfSlotList.gameObject.SetActive(view == MenuView.SlotList);
            _tfConfirmOverwrite.gameObject.SetActive(view == MenuView.ConfirmOverwrite);
            _tfSettings.gameObject.SetActive(view == MenuView.Settings);

            if (view == MenuView.Root)
            {
                RefreshRootView();
            }
            else if (view == MenuView.SlotList)
            {
                RefreshSlotList();
            }
            else if (view == MenuView.Settings)
            {
                RefreshSettingsView();
            }
            else
            {
                // 离开设置视图（进 SlotList/ConfirmOverwrite 都不该发生，但 Root 会）：
                // 取消任何正在进行的重绑监听/冲突确认，避免切走之后按键还在悄悄改键位。
                CancelRebindState();
            }
        }

        /// <summary>ER2-INPUT-01 AC-ACC-001：设置面板每次打开都从 <see cref="GameSettings"/>
        /// 拉最新值刷新控件——用 SetValueWithoutNotify，避免"读回填充"触发一次多余的 Save+广播。</summary>
        private void RefreshSettingsView()
        {
            _toggleEdgePan.SetIsOnWithoutNotify(GameSettings.EdgePanEnabled);
            _toggleScreenShake.SetIsOnWithoutNotify(GameSettings.ScreenShakeEnabled);
            _toggleFlashReduction.SetIsOnWithoutNotify(GameSettings.FlashReductionEnabled);
            _toggleSubtitles.SetIsOnWithoutNotify(GameSettings.SubtitlesEnabled);
            _toggleColorblindIcons.SetIsOnWithoutNotify(GameSettings.ColorblindSafeIconsEnabled);

            _sliderUiScale.SetValueWithoutNotify(GameSettings.UiScale);
            _sliderCameraSpeed.SetValueWithoutNotify(GameSettings.CameraSpeedMultiplier);
            _sliderMasterVolume.SetValueWithoutNotify(GameSettings.MasterVolume);
            _sliderMusicVolume.SetValueWithoutNotify(GameSettings.MusicVolume);
            _sliderSfxVolume.SetValueWithoutNotify(GameSettings.SfxVolume);
            _sliderUiVolume.SetValueWithoutNotify(GameSettings.UiVolume);

            RefreshKeybindLabels();
        }

        private void RefreshKeybindLabels()
        {
            foreach ((GameActionId action, string _) in RebindRows)
            {
                if (_rebindLabels.TryGetValue(action, out Text label))
                {
                    label.text = InputDisplay.ForAction(action);
                }
            }
        }

        // ── ER2-INPUT-01 / FG0-UX-01：键位重绑（点击→监听下一次按键→冲突弹确认框：覆盖或取消）──────

        private void OnRebindButtonClicked(GameActionId action)
        {
            CancelRebindState();
            _rebindListening = action;
            _rebindCapture.Reset();
            InputRouter.SetRebindCapture(true);
            _rebindLabels[action].text = Localization.GameText.Get("input.rebind.listening");
        }

        /// <summary>打开完整按键面板（UI Toolkit）。</summary>
        private void OnAllKeyBindingsClicked()
        {
            CancelRebindState();
            UI.Kit.KeyBindingsPanelUIToolkit.Open();
        }

        /// <summary>按根画布当前尺寸收紧各视图（尺寸没变时 O(1) 早退）。UI 缩放/窗口分辨率变化都会改变
        /// 根画布的参考坐标尺寸，所以在每帧回调里比较一次即可覆盖两种情况。</summary>
        private void FitViewsToCanvas(bool force)
        {
            Canvas canvas = (_tfRoot as RectTransform)?.GetComponentInParent<Canvas>();
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            if (root == null)
            {
                return;
            }
            Vector2 canvasSize = ((RectTransform)root.transform).rect.size;
            if (!force && (canvasSize - _fittedCanvasSize).sqrMagnitude < 1f)
            {
                return;
            }
            _fittedCanvasSize = canvasSize;
            Vector2 limit = new Vector2(Mathf.Max(200f, canvasSize.x - ViewScreenMargin * 2f),
                Mathf.Max(200f, canvasSize.y - ViewScreenMargin * 2f));
            foreach (KeyValuePair<RectTransform, Vector2> pair in _viewDesignSizes)
            {
                if (pair.Key != null)
                {
                    pair.Key.sizeDelta = Vector2.Min(pair.Value, limit);
                }
            }
        }

        /// <summary>UIWindow 每帧回调；只有重绑监听/冲突确认中才做事，其余帧 O(1) 早退。</summary>
        protected override void OnUpdate()
        {
            FitViewsToCanvas(force: false);
            if (_rebindListening == null)
            {
                return;
            }

            GameActionId action = _rebindListening.Value;
            InputCapture.Result result = _rebindCapture.Poll(out InputChord chord);
            if (result == InputCapture.Result.Waiting)
            {
                return;
            }
            _rebindListening = null;
            InputRouter.SetRebindCapture(false);
            if (result == InputCapture.Result.Cancelled)
            {
                RefreshKeybindLabels();
                return;
            }
            // 与 UI Toolkit 按键面板同一套流程：冲突时弹确认框（覆盖 / 取消），必须保留按键的动作不能被抢。
            UI.Kit.KeyBindingFlow.Rebind(action, chord, _ => RefreshKeybindLabels());
            RefreshKeybindLabels();
        }

        /// <summary>取消监听/冲突确认。同时刷新键位标签——若上一个正在监听/等确认的按钮
        /// 不是本次触发者（玩家中途点了别的重绑按钮/离开设置面板），它的文字会卡在
        /// "按任意键…"/"覆盖…？"，必须在这里统一复位，不能指望调用方各自记得刷新。</summary>
        private void CancelRebindState()
        {
            if (_rebindListening != null)
            {
                InputRouter.SetRebindCapture(false);
            }
            _rebindListening = null;
            if (_rebindLabels.Count > 0)
            {
                RefreshKeybindLabels();
            }
        }

        private void OnResetAllDefaultsClicked()
        {
            CancelRebindState();
            GameSettings.ResetAllToDefault();
            RefreshSettingsView();
        }

        private void RefreshRootView()
        {
            // FG0-SAVE-01：一次刷新只读一遍各槽位（大存档读头部要几百毫秒），"继续"与原因都用这一份元数据。
            CampaignSlotMetadata[] metas = CampaignSaveService.GetAllSlotMetadata();
            int continueSlot = CampaignSaveService.ResolveContinueSlot(metas);
            bool canContinue = continueSlot >= 0;
            _btnContinue.interactable = canContinue;
            // AC-UI-002：禁用按钮必须同时给出不可用原因，不能只是灰掉。
            // FG0-SAVE-01：只有 Demo 存档时说明"Demo 存档不能继续"，而不是笼统的"没有存档"。
            _textContinueReason.text = canContinue ? string.Empty : CampaignSlotText.ContinueUnavailable(CampaignSaveService.AnyDemoSave(metas));
        }

        private void RefreshSlotList()
        {
            for (int i = 0; i < CampaignSaveService.SlotCount; i++)
            {
                CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(i);
                RefreshSlotCard(i, meta);
            }
        }

        private void RefreshSlotCard(int slotIndex, CampaignSlotMetadata meta)
        {
            // ERD-UI-001 + FG0-SAVE-01（FGR-SYS-007 / FGR-SYS-003）：存档卡显示幕、难度、种子、阶段、时长、区域、
            // 保存时间；坏档显示稳定原因与备份状态；Demo 存档明确提示不迁移。文字全部经 CampaignSlotText（文本键）。
            _slotInfoTexts[slotIndex].text = CampaignSlotText.CardText(meta);
            _slotActionButtons[slotIndex].interactable = CampaignSlotText.ActionEnabled(meta);
            _slotActionLabels[slotIndex].text = CampaignSlotText.ActionLabel(meta);
        }

        #region 事件

        private void OnNewClicked()
        {
            int emptySlot = FindFirstEmptySlot();
            if (emptySlot >= 0)
            {
                StartNewCampaign(emptySlot);
                return;
            }

            // 三槽全满：按 STORY-EXECUTION-CARDS.md 要求，覆盖写入前先展示原档信息并二次确认；
            // 取消不改任何文件（Save 只在 OnConfirmYesClicked 里才会被调用）。
            // FG0-SAVE-01：优先占用读不了、也没有可读备份的槽位（Demo 存档 / 坏档 / 版本更新的存档，原文件另存保留），
            // 都没有时才覆盖槽位 1 的可读战役（多槽选择 UI 属于 FG15-SYS-01，DEBT-FG0SAVE01-02）。
            CampaignSlotMetadata[] metas = CampaignSaveService.GetAllSlotMetadata();
            CampaignSlotMetadata target = metas.FirstOrDefault(CampaignSlotText.StartsNewInSlot) ?? metas[0];
            ConfirmNewInSlot(target, MenuView.Root);
        }

        /// <summary>在已有文件的槽位新建战役前先确认（B04）：可读存档显示摘要；Demo / 读不出的存档写明原文件会另存为
        /// *.keep-* 保留（永不自动删除存档）。</summary>
        private void ConfirmNewInSlot(CampaignSlotMetadata meta, MenuView returnView)
        {
            _pendingOverwriteSlot = meta.SlotIndex;
            _confirmReturnView = returnView;
            _textConfirmInfo.text = CampaignSlotText.OverwriteConfirm(meta);
            SetView(MenuView.ConfirmOverwrite);
        }

        private void OnContinueClicked()
        {
            int slot = CampaignSaveService.ResolveContinueSlot();
            if (slot < 0)
            {
                return;
            }

            LoadIntoSession(slot);
        }

        private void OnLoadClicked()
        {
            SetView(MenuView.SlotList);
        }

        private void OnSettingsClicked()
        {
            SetView(MenuView.Settings);
        }

        private void OnSettingsBackClicked()
        {
            SetView(MenuView.Root);
        }

        private void OnBackClicked()
        {
            SetView(MenuView.Root);
        }

        private void OnQuitClicked()
        {
            Log.Info("[MainMenuUI] 玩家点击退出。");
            // 编辑器内 Application.Quit() 官方文档定义为无操作（不会中断 Play Mode），
            // 正式 Windows 构建里会真正退出进程。
            Application.Quit();
        }

        private void OnSlotActionClicked(int slotIndex)
        {
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slotIndex);
            switch (meta.State)
            {
                case CampaignSlotState.Empty:
                    StartNewCampaign(slotIndex);
                    break;
                case CampaignSlotState.Ready:
                    LoadIntoSession(slotIndex);
                    break;
                case CampaignSlotState.Corrupt:
                case CampaignSlotState.Incompatible:
                    // FGR-SYS-003"提供读取备份的选项"：备份可读取时恢复备份，坏掉的主档另存为 *.keep-corrupt-*。
                    // 头部可读但读档失败过的主档，GetSlotMetadata 已按读档结果报成损坏（不会再走 Ready 反复读坏档）。
                    if (meta.HasBackup)
                    {
                        bool restored = CampaignSaveService.RestoreFromBak(slotIndex, out string kept);
                        Log.Info($"[MainMenuUI] 槽位 {slotIndex} 读取备份：{(restored ? "成功" : "失败")}；原主档保留为 {kept ?? "（无）"}");
                        RefreshSlotList();
                    }
                    else
                    {
                        // 读不了、备份也不可用：新建于此槽（先确认，原文件另存保留）。
                        ConfirmNewInSlot(meta, MenuView.SlotList);
                    }
                    break;
                case CampaignSlotState.DemoSave:
                    // Demo 存档不迁移（FGR-ARC-008）：不能读取，但可以在此槽新建（先确认，Demo 文件另存保留）。
                    ConfirmNewInSlot(meta, MenuView.SlotList);
                    break;
            }
        }

        private void OnConfirmYesClicked()
        {
            int slot = _pendingOverwriteSlot;
            _pendingOverwriteSlot = -1;
            if (slot >= 0)
            {
                StartNewCampaign(slot);
            }
            else
            {
                SetView(_confirmReturnView);
            }
        }

        private void OnConfirmNoClicked()
        {
            // 取消不改文件：这里全程没有调用过 CampaignSaveService.Save。
            _pendingOverwriteSlot = -1;
            SetView(_confirmReturnView);
        }

        #endregion

        private int FindFirstEmptySlot()
        {
            for (int i = 0; i < CampaignSaveService.SlotCount; i++)
            {
                if (CampaignSaveService.GetSlotMetadata(i).State == CampaignSlotState.Empty)
                {
                    return i;
                }
            }

            return -1;
        }

        private void StartNewCampaign(int slotIndex)
        {
            string campaignId = Guid.NewGuid().ToString("N");
            // ER1-SAVE-02：不再用 Environment.TickCount 占位（DEBT-ER1SAVE01-05，精度约 15ms，
            // 连续快速新建可能撞种子）；CampaignRandomService.GenerateSeed() 用 CSPRNG 生成一次性种子，
            // 之后全部确定性消费统一走 CampaignRandomService.CreateRng(state)。
            int seed = CampaignRandomService.GenerateSeed();
            CampaignState state = CampaignState.CreateNew(campaignId, "Standard", seed);

            SaveResult result = CampaignSaveService.Save(slotIndex, state, SaveReason.NewCampaign);
            if (!result.Success)
            {
                Log.Warning($"[MainMenuUI] 新建战役保存失败（{result.Outcome}）：{result.Message}");
                SetView(MenuView.Root);
                return;
            }

            // ER1-ID-01：新战役必须清空上一局残留的机器身份分配器状态（AC-LIFE-002：
            // 新战役绝不继承上一战役的机器/控制记录）。哪怕本机进程从未跑过上一局，
            // 这里也无条件重置，不依赖"这是第一次进程内新建"这种脆弱判断。
            MachineRegistry.ResetForNewCampaign();

            CampaignSession.Set(slotIndex, state);
            Log.Info($"[MainMenuUI] 新战役已创建：campaignId={campaignId} slot={slotIndex} phase={state.CampaignPhase}");

            // ER2-SCENE-01：新战役进入归还谷地正式场景。GameApp.MountGameplayUi() 幂等，
            // 首次开局才真正挂载常驻 UI 壳层（战斗 HUD 等），与归还谷地自身的场景构建互不相关。
            GameApp.MountGameplayUi();
            Close();
            GameLogic.Stage.GameRoot.StartHomeValley();
        }

        private void LoadIntoSession(int slotIndex)
        {
            // ER1-SAVE-02：不再自己串 CampaignSaveService.Load + MachineRegistry.LoadFromCampaignState——
            // 统一走 CampaignRestoreOrchestrator，固定 ERD-SAV-003 恢复顺序，任一步致命失败原地中止，
            // 不触碰 CampaignSession，磁盘原档/备份不受影响。
            RestoreResult result = CampaignRestoreOrchestrator.Restore(slotIndex);
            if (!result.Success)
            {
                Log.Warning($"[MainMenuUI] 槽位 {slotIndex} 恢复编排在 {result.FailedStep} 步骤失败：{result.Message}");
                SetView(MenuView.SlotList);
                RefreshSlotList();
                // 安全错误页：复用槽位卡片显示**稳定原因**（FG0-SAVE-01：文本键，不再把开发用异常串给玩家看）+
                // "读取备份"（备份不可读则禁用），不做任何写入或状态切换。
                // 头部可读但升级 / 正文 / 结构校验失败的主档，读档与恢复编排已登记为"读档失败"：GetSlotMetadata 把它报成
                // 损坏并完整校验备份，列表、"继续"与按钮点击三处一致（点"读取备份"真正恢复备份）。
                CampaignSlotMetadata failMeta = CampaignSaveService.GetSlotMetadata(slotIndex);
                if (failMeta.State == CampaignSlotState.Ready)
                {
                    CampaignSaveService.RecordLoadFailure(slotIndex, result.Reason, result.FileSchemaVersion);
                    failMeta = CampaignSaveService.GetSlotMetadata(slotIndex);
                }
                RefreshSlotCard(slotIndex, failMeta);
                return;
            }

            if (result.Warnings.Length > 0)
            {
                Log.Info($"[MainMenuUI] 槽位 {slotIndex} 恢复编排完成，{result.Warnings.Length} 条非致命提示：" +
                    string.Join(" | ", result.Warnings));
            }

            CampaignSession.Set(slotIndex, result.State);
            Log.Info($"[MainMenuUI] 已读取战役：campaignId={result.State.CampaignId} slot={slotIndex} " +
                $"phase={result.State.CampaignPhase}");

            // ER2-SCENE-01："继续"与"读取"都走恢复语义（不是新局）——Controller 内部按已有
            // RegionRecord/MachineRecord 复用，不重复播种。DEBT-ER6REGION01-01：按存档所在区域恢复，
            // 在远征区域存档退出的玩家不再被送回归还谷地。
            GameApp.MountGameplayUi();
            Close();
            GameLogic.Stage.GameRoot.ResumeCampaign();

            // FG0-SAVE-01（FGR-SYS-004）：读档时内容迁移（已移除内容转成废料等）的通知，进入游戏后逐条提示（超过字幕上限时
            // 最后一条合并为"另有 N 条"）；同一批通知已写入 CampaignState.SaveHistory，供通知中心历史回看（FG0-UX-01）。
            SaveContentReconciler.RaiseLoadNotices(result.Notices);
        }
    }
}
