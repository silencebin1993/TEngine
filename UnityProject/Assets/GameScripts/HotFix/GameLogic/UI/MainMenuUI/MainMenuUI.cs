using System;
using System.Collections.Generic;
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
            (GameActionId.DirectSkillSlot0, "冲刺"),
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
        /// <summary>已检测到冲突、等待玩家再点一次同一按钮确认覆盖。三者必须同时有效才允许确认。</summary>
        private GameActionId? _rebindConflictAction;
        private GameActionId _rebindConflictWith;
        private KeyCode _rebindConflictKey;

        /// <summary>重绑监听时轮询的候选键，不遍历全部 ~500 个 KeyCode——只覆盖玩家实际按得到、
        /// 也说得清楚"按了哪个键"的常用集合。监听只在玩家主动点了重绑按钮后才短暂发生，
        /// 一帧 O(候选数) 不构成性能问题（不是每帧默认发生的路径）。</summary>
        private static readonly KeyCode[] RebindCandidateKeys = BuildRebindCandidateKeys();

        private static KeyCode[] BuildRebindCandidateKeys()
        {
            var list = new List<KeyCode>();
            for (KeyCode k = KeyCode.A; k <= KeyCode.Z; k++) { list.Add(k); }
            for (KeyCode k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) { list.Add(k); }
            for (KeyCode k = KeyCode.F1; k <= KeyCode.F12; k++) { list.Add(k); }
            list.AddRange(new[]
            {
                KeyCode.Space, KeyCode.Tab, KeyCode.Escape, KeyCode.Return, KeyCode.Backspace,
                KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.CapsLock,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2, KeyCode.Mouse3, KeyCode.Mouse4,
            });
            return list.ToArray();
        }

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

            // ER2-INPUT-01 附带修复：ScrollRect 补了真正的 Viewport（RectMask2D）后，Content
            // (m_tf_SettingsColumns) 多套了一层 "Viewport" 节点，这里的路径常量必须跟着改，
            // 否则 FindChildComponent 全部找空，ScriptGenerator 会在这里 NullReferenceException
            // （2026-09-21 实锤过一次：加 Viewport 时漏改了这两个路径常量）。
            const string colLeft = "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColLeft";
            const string colRight = "m_tf_Settings/m_scroll_Settings/Viewport/m_tf_SettingsColumns/m_tf_SettingsColRight";
            foreach ((GameActionId action, string _) in RebindRows)
            {
                Button btn = FindChildComponent<Button>(colLeft + "/m_row_Rebind_" + action + "/m_btn_Rebind_" + action);
                _rebindLabels[action] = btn.GetComponentInChildren<Text>();
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

        protected override void OnCreate()
        {
            ApplyVisualSystem();
            SetView(MenuView.Root);
        }

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

        private static void ConfigureView(RectTransform view, Vector2 size, Color fill, Color edge, int padding)
        {
            if (view == null)
            {
                return;
            }

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
                KeyCode key = GameSettings.KeyBindings.GetKey(action);
                _rebindLabels[action].text = KeyDisplayName(key);
            }
        }

        private static string KeyDisplayName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift: return "LShift";
                case KeyCode.RightShift: return "RShift";
                case KeyCode.LeftControl: return "LCtrl";
                case KeyCode.RightControl: return "RCtrl";
                case KeyCode.LeftAlt: return "LAlt";
                case KeyCode.RightAlt: return "RAlt";
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                default: return key.ToString();
            }
        }

        // ── ER2-INPUT-01：键位重绑（点击→监听下一次按键→冲突需再点一次确认）──────

        private void OnRebindButtonClicked(GameActionId action)
        {
            // 正在等待"再点一次确认覆盖"，且点的就是同一个按钮＝确认。
            if (_rebindConflictAction == action)
            {
                GameSettings.ForceRebindKey(action, _rebindConflictKey, _rebindConflictWith);
                CancelRebindState(); // 内部会用刚落地的新绑定刷新全部标签。
                return;
            }

            CancelRebindState();
            _rebindListening = action;
            _rebindLabels[action].text = "按任意键…";
        }

        /// <summary>UIWindow 每帧回调；只有重绑监听/冲突确认中才做事，其余帧 O(1) 早退。</summary>
        protected override void OnUpdate()
        {
            if (_rebindListening == null)
            {
                return;
            }

            GameActionId action = _rebindListening.Value;
            for (int i = 0; i < RebindCandidateKeys.Length; i++)
            {
                KeyCode key = RebindCandidateKeys[i];
                if (!Input.GetKeyDown(key))
                {
                    continue;
                }

                _rebindListening = null;
                GameActionId conflict;
                if (GameSettings.TryRebindKey(action, key, out conflict))
                {
                    RefreshKeybindLabels();
                }
                else
                {
                    // 冲突：不落地，等玩家再点一次同一按钮确认覆盖（见 OnRebindButtonClicked）。
                    _rebindConflictAction = action;
                    _rebindConflictWith = conflict;
                    _rebindConflictKey = key;
                    _rebindLabels[action].text = "覆盖 " + ConflictRowLabel(conflict) + "？再点一次";
                }
                return;
            }
        }

        private static string ConflictRowLabel(GameActionId action)
        {
            foreach ((GameActionId a, string label) in RebindRows)
            {
                if (a == action)
                {
                    return label;
                }
            }
            return action.ToString();
        }

        /// <summary>取消监听/冲突确认。同时刷新键位标签——若上一个正在监听/等确认的按钮
        /// 不是本次触发者（玩家中途点了别的重绑按钮/离开设置面板），它的文字会卡在
        /// "按任意键…"/"覆盖…？"，必须在这里统一复位，不能指望调用方各自记得刷新。</summary>
        private void CancelRebindState()
        {
            _rebindListening = null;
            _rebindConflictAction = null;
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
            int continueSlot = CampaignSaveService.ResolveContinueSlot();
            bool canContinue = continueSlot >= 0;
            _btnContinue.interactable = canContinue;
            // AC-UI-002：禁用按钮必须同时给出不可用原因，不能只是灰掉。
            _textContinueReason.text = canContinue ? string.Empty : "没有可读取的安全存档，请先新建战役";
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
            Text info = _slotInfoTexts[slotIndex];
            Button action = _slotActionButtons[slotIndex];
            Text label = _slotActionLabels[slotIndex];

            switch (meta.State)
            {
                case CampaignSlotState.Empty:
                    info.text = $"槽位 {slotIndex + 1}：空槽";
                    action.interactable = true;
                    label.text = "新建于此槽";
                    break;
                case CampaignSlotState.Ready:
                    // ERD-UI-001：存档项必须展示时间、战役阶段、游戏时长、最后区域和内容版本。
                    info.text = $"槽位 {slotIndex + 1}：{meta.CampaignId}\n阶段 {meta.CampaignPhase}　" +
                        $"游戏时长 {meta.PlaySeconds:F0}s\n存档时间 {meta.WrittenAtUtc}\n" +
                        $"最后区域 {(string.IsNullOrEmpty(meta.LastRegionId) ? "（尚未进入任何区域）" : meta.LastRegionId)}　" +
                        $"内容版本 {meta.ContentVersion}";
                    action.interactable = true;
                    label.text = "读取";
                    break;
                case CampaignSlotState.Corrupt:
                    info.text = $"槽位 {slotIndex + 1}：存档损坏（{meta.ErrorMessage}）" +
                        (meta.HasBackup ? "\n可尝试恢复备份" : "\n无可用备份，无法恢复");
                    action.interactable = meta.HasBackup;
                    label.text = "恢复备份";
                    break;
                case CampaignSlotState.Incompatible:
                    info.text = $"槽位 {slotIndex + 1}：存档版本（{meta.SchemaVersion}）比当前客户端更新，无法读取" +
                        (meta.HasBackup ? "\n可尝试恢复备份" : "");
                    action.interactable = meta.HasBackup;
                    label.text = meta.HasBackup ? "恢复备份" : "不可用";
                    break;
            }
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
            // 取消不改任何文件（Save 只在 OnConfirmYesClicked 里才会被调用）。固定取槽位 0，
            // 多槽选择 UI 留给 ER2-BOOT-01 的美术终稿。
            _pendingOverwriteSlot = 0;
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(0);
            _textConfirmInfo.text = $"槽位 1 已有存档：\ncampaignId={meta.CampaignId}\n阶段 {meta.CampaignPhase}　" +
                $"时长 {meta.PlaySeconds:F0}s\n存档时间 {meta.WrittenAtUtc}\n\n新建战役将覆盖此存档，是否继续？";
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
                    if (meta.HasBackup)
                    {
                        bool restored = CampaignSaveService.RestoreFromBak(slotIndex);
                        Log.Info($"[MainMenuUI] 槽位 {slotIndex} 恢复备份：{(restored ? "成功" : "失败")}");
                    }

                    RefreshSlotList();
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
                SetView(MenuView.Root);
            }
        }

        private void OnConfirmNoClicked()
        {
            // 取消不改文件：这里全程没有调用过 CampaignSaveService.Save。
            _pendingOverwriteSlot = -1;
            SetView(MenuView.Root);
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
                // 安全错误页占位（ER2-BOOT-01 建正式错误页前）：复用槽位卡片显示失败原因 +
                // 保留"恢复备份"（无备份则禁用），不做任何写入或状态切换。
                _slotInfoTexts[slotIndex].text =
                    $"槽位 {slotIndex + 1}：恢复编排失败（{result.FailedStep}）：{result.Message}" +
                    (result.HasBackup ? "\n可尝试恢复备份" : "\n无可用备份，无法恢复");
                _slotActionButtons[slotIndex].interactable = result.HasBackup;
                _slotActionLabels[slotIndex].text = result.HasBackup ? "恢复备份" : "不可用";
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

            // ER2-SCENE-01："继续"与"读取"都走恢复语义（GameRoot.ResumeHomeValley，不是新局）——
            // Controller 内部按已有 RegionRecord/MachineRecord 复用，不重复播种。
            GameApp.MountGameplayUi();
            Close();
            GameLogic.Stage.GameRoot.ResumeHomeValley();
        }
    }
}
