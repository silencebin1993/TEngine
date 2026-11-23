using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-UX-01 设置全集与重绑（FGT-SYS-005“所有设置项生效；分辨率修改超时回退”、FGT-UX-003“所有动作可以重绑，同一上下文没有冲突”）。
    /// A 登记表覆盖 FGR-SYS-020 全部行 / B 每项真的生效（引擎值、输入、光照、音量、特效、存档、引导、因果链、伤害数字）/ C 显示设置 15 秒确认、还原、超时、失败、暂停与倍速无关 /
    /// D 每类恢复默认 / E 本机持久化与旧设置补默认 / F 面板（分类行数、开关 / 滑条 / 下拉生效、关闭时还原、布局探针）/ G 主菜单与游戏内一致 /
    /// H 重绑与冲突（动作清单动态生成、因果链正式键、接入技能删除）/ I 失焦暂停 / 静音 / J 自动存档间隔（暂停、倍速）/ K 难度类随存档。
    /// </summary>
    public static class FgSettingsSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("QA/自检/设置/设置全集与重绑（FG15-UX-01）")]
        public static void RunFromMenu()
        {
            var sb = new StringBuilder();
            int fail = Run(sb);
            Debug.Log(sb.ToString());
            Debug.Log(fail == 0 ? "FgSettingsSelfCheck 全部通过" : $"FgSettingsSelfCheck 失败 {fail} 项");
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[设置全集与重绑] FG15-UX-01（FGR-SYS-020 / 021、FG13 第 5 节；FGT-SYS-005 / FGT-UX-003）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写本机设置）");
                return 0;
            }
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            int savedFps = FramePacing.TargetFps;
            int savedVSync = QualitySettings.vSyncCount;
            ShadowQuality savedShadows = QualitySettings.shadows;
            CampaignState savedSession = CampaignSession.Current;
            int savedSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Run("A 登记表", CheckCatalog);
                Run("B 每项生效", CheckEffects);
                Run("C 显示设置确认 / 还原 / 失败", CheckDisplay);
                Run("D 每类恢复默认", CheckResetCategories);
                Run("E 持久化", CheckPersistence);
                Run("F 设置面板", CheckPanel);
                Run("F2 设置里打开的子面板盖在设置之上", CheckSubPanels);
                Run("G 主菜单与游戏内一致", CheckMainMenu);
                Run("H 重绑与冲突", CheckRebind);
                Run("I 失焦暂停 / 静音", CheckFocus);
                Run("J 自动存档间隔", CheckAutoSave);
                Run("K 难度类随存档", CheckDifficulty);
                Run("L 命令栏与直控反馈", CheckCommandFeedback);
                Run("M 战斗教义：避开危险物质开关", CheckHazardDoctrine);
            }
            catch (Exception ex)
            {
                Fail("自检异常：" + ex);
            }
            finally
            {
                DisplayModeService.ResetForTests();
                FocusPolicy.ResetForTests();
                IntervalAutoSave.ResetForTests();
                EffectsDensityGate.ResetForTests();
                DamageNumbers.ResetForTests();
                InputRouter.DebugSetReader(null);
                InputRouter.ClearHoldLatches();
                InputRouter.Reset();
                UiEscapeStack.Clear();
                GameSettings.PostProcessSink = null;
                CampaignSession.Set(savedSlot, savedSession);
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                FramePacing.Set(savedFps);
                QualitySettings.vSyncCount = savedVSync;
                QualitySettings.shadows = savedShadows;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
            Line($"  合计：通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Run(string name, Action body)
        {
            Line("  · " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail(name + " 异常：" + ex.GetType().Name + " " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        // ── A ──
        private static readonly string[] RequiredIds =
        {
            // 画面
            "display.resolution", "display.window_mode", "display.vsync", "display.fps_cap", "display.quality", "display.shadows", "display.effects_density",
            "display.post_process", "display.brightness",
            // 音频
            "audio.master", "audio.music", "audio.sfx", "audio.ui", "audio.ambient", "audio.mute_unfocused",
            // 控制
            "controls.keybindings", "controls.camera_speed", "controls.edge_pan", "controls.zoom_speed", "controls.invert_scroll", "controls.uplink_mouse", "controls.hold_toggle",
            // 游戏性
            "gameplay.pause_unfocused", "gameplay.autosave", "gameplay.away_report", "gameplay.guidance", "gameplay.reaction_popups", "gameplay.reaction_slowmo",
            "gameplay.reaction_nudge", "gameplay.causal_chain", "gameplay.damage_numbers", "gameplay.language",
            // 难度
            "difficulty.panel", "difficulty.silent_night", "difficulty.events",
            // 主菜单曾缺的游戏内项（DEBT-FG1HUD01-03 / FG4ECO09-02 / FG7ENV01-03 / 02-04 / 03-03 / FG9NAR01-03）
            "controls.uplink_zoom", "controls.uplink_follow", "display.night_floor", "display.silent_dim", "display.weather_fx", "access.text_speed", "access.subtitle_scale",
            "gameplay.reaction_log",
            // FGR-SYS-020 游戏性“自动暂停规则”（FGR-UX-021 按通知类型）
            "gameplay.auto_pause_rules",
            // FG15-SYS-03：本地试玩统计（FG-GAP-047，FGR-SYS-100）与反馈包（FGR-SYS-061）
            "gameplay.local_stats", "gameplay.export_stats", "gameplay.feedback_pack",
        };

        private static void CheckCatalog()
        {
            var missing = RequiredIds.Where(id => SettingsCatalog.Find(id) == null).ToList();
            Expect(missing.Count == 0, $"FGR-SYS-020 全部设置项都在登记表里（{RequiredIds.Length} 项必需，共 {SettingsCatalog.All.Count} 项）" + (missing.Count > 0 ? "——缺：" + string.Join("、", missing) : string.Empty));
            var dup = SettingsCatalog.All.GroupBy(i => i.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Expect(dup.Count == 0, "设置项 ID 不重复" + (dup.Count > 0 ? "：" + string.Join("、", dup) : string.Empty));
            var empty = SettingsCatalog.Categories.Where(c => SettingsCatalog.All.All(i => i.Category != c)).ToList();
            Expect(empty.Count == 0, "六个分类（画面 / 音频 / 控制 / 游戏性 / 难度 / 可访问性）都有设置项");
            var badText = new List<string>();
            foreach (SettingItem i in SettingsCatalog.All)
            {
                if (string.IsNullOrEmpty(i.Name) || i.Name.Contains("⟦"))
                {
                    badText.Add(i.Id);
                }
            }
            foreach (SettingsCategory c in SettingsCatalog.Categories)
            {
                if (GameText.Get(SettingsCatalog.CategoryKey(c)).Contains("⟦"))
                {
                    badText.Add(c.ToString());
                }
            }
            GameSettings.SetLanguage(GameLanguage.En);
            int enBad = SettingsCatalog.All.Count(i => i.Name.Contains("⟦"));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(badText.Count == 0 && enBad == 0, $"每项名称与分类名中英文本键齐全（中文缺 {badText.Count}、英文缺 {enBad}）" + (badText.Count > 0 ? "：" + string.Join("、", badText) : string.Empty));
        }

        // ── B ──
        private static void CheckEffects()
        {
            // 垂直同步 / 帧率上限
            SettingsCatalog.Find("display.vsync").SetBool(true);
            bool vsOn = QualitySettings.vSyncCount == 1;
            SettingsCatalog.Find("display.vsync").SetBool(false);
            bool vsOff = QualitySettings.vSyncCount == 0;
            SettingItem fps = SettingsCatalog.Find("display.fps_cap");
            fps.SetIndex(Array.IndexOf(FramePacing.Choices, 60));
            bool fps60 = Application.targetFrameRate == 60 && FramePacing.TargetFps == 60;
            fps.SetIndex(Array.IndexOf(FramePacing.Choices, 120));
            Expect(vsOn && vsOff && fps60 && Application.targetFrameRate == 120, $"垂直同步开 / 关 → vSyncCount 1 / 0；帧率上限 60 / 120 → targetFrameRate（{vsOn}/{vsOff}/{fps60}）");

            // 画质预设与子项
            bool? sink = null;
            GameSettings.PostProcessSink = v => sink = v;
            GameSettings.SetQualityPreset(GameSettings.QualityLow);
            bool low = QualitySettings.shadows == ShadowQuality.Disable && Mathf.Approximately(GameSettings.EffectsDensity, GameSettings.EffectsDensityMin) && sink == false;
            GameSettings.SetQualityPreset(GameSettings.QualityHigh);
            bool high = QualitySettings.shadows == ShadowQuality.All && Mathf.Approximately(GameSettings.EffectsDensity, 1f) && sink == true;
            GameSettings.SetShadowQuality(1);
            bool custom = GameSettings.QualityPreset == GameSettings.QualityCustom && QualitySettings.shadows == ShadowQuality.HardOnly;
            Expect(low && high && custom, $"画质预设：低 = 关阴影 + 最低特效密度 + 关后处理，高 = 全部阴影 + 满密度 + 后处理；改子项（阴影 → 硬阴影）变为“自定义”并生效；后处理值交给渲染侧接收口（{low}/{high}/{custom}）");

            // 特效密度：真实生成路径上的放行比例
            EffectsDensityGate.ResetForTests();
            int admitted = 0;
            for (int i = 0; i < 20; i++)
            {
                admitted += EffectsDensityGate.Admit(0.5f) ? 1 : 0;
            }
            int full = 0;
            for (int i = 0; i < 20; i++)
            {
                full += EffectsDensityGate.Admit(1f) ? 1 : 0;
            }
            Expect(admitted == 10 && full == 20, $"特效密度 50% → 20 个反馈特效放行 {admitted} 个；100% → 放行 {full} 个（FeedbackVfxPresenter.Tick 生成前经这道闸）");

            // 亮度：乘在昼夜调光上
            var sunGo = new GameObject("__SettingsSelfCheckSun") { hideFlags = HideFlags.HideAndDontSave };
            Light sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1f;
            Light prevSun = RenderSettings.sun;
            RenderSettings.sun = sun;
            try
            {
                DayNightLighting.ResetForTests();
                GameSettings.SetBrightness(1f);
                DayNightLighting.Apply(1f);
                float b1 = DayNightLighting.Brightness;
                GameSettings.SetBrightness(1.4f);
                DayNightLighting.Apply(1f);
                float b2 = DayNightLighting.Brightness;
                Expect(b1 > 0f && Mathf.Abs(b2 / b1 - 1.4f) < 0.01f && Mathf.Abs(sun.intensity - b2) < 0.05f * Mathf.Max(1f, b2),
                    $"亮度 100% → 140%：画面亮度 {b1:0.00} → {b2:0.00}（太阳光强跟着变）");
            }
            finally
            {
                DayNightLighting.Restore();
                DayNightLighting.ResetForTests();
                RenderSettings.sun = prevSun;
                Object.DestroyImmediate(sunGo);
                GameSettings.SetBrightness(1f);
            }

            // 音量：环境分类与失焦静音
            GameSettings.SetMasterVolume(0.8f);
            GameSettings.SetAmbientVolume(0.3f);
            FeedbackCues.ComputeVolumes(out float m1, out float a1);
            Expect(Mathf.Approximately(m1, 0.8f) && Mathf.Approximately(a1, 0.3f), $"主音量 80% / 环境 30% 推给音频模块（主 {m1:0.00}、环境分类 {a1:0.00}）");

            // 缩放速度 / 反转滚轮（真实输入路径 InputRouter.GetZoomDelta）
            var reader = new FakeReader { Scroll = 1f };
            InputRouter.Reset();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            GameSettings.SetZoomSpeedMultiplier(1f);
            float z1 = InputRouter.GetZoomDelta(InputScope.Strategy);
            GameSettings.SetZoomSpeedMultiplier(2f);
            float z2 = InputRouter.GetZoomDelta(InputScope.Strategy);
            GameSettings.SetInvertScroll(true);
            float z3 = InputRouter.GetZoomDelta(InputScope.Strategy);
            GameSettings.SetInvertScroll(false);
            GameSettings.SetZoomSpeedMultiplier(1f);
            Expect(z1 > 0f && Mathf.Approximately(z2, 2f * z1) && Mathf.Approximately(z3, -z2),
                $"滚轮向上 1 格：缩放量 {z1:0.00}；缩放速度 2x → {z2:0.00}；反转滚轮 → {z3:0.00}");

            // 按住 → 切换（固定悬停提示）
            KeyCode pin = GameSettings.KeyBindings.GetKey(GameActionId.PinTooltip);
            GameSettings.SetHoldActionsToggle(false);
            reader.Held.Add(pin);
            bool holdHeld = InputRouter.IsActionHeld(GameActionId.PinTooltip);
            reader.Held.Clear();
            bool holdReleased = !InputRouter.IsActionHeld(GameActionId.PinTooltip);
            GameSettings.SetHoldActionsToggle(true);
            InputRouter.ClearHoldLatches();
            reader.Down.Add(pin);
            bool t1 = InputRouter.IsActionHeld(GameActionId.PinTooltip);
            bool sameFrame = InputRouter.IsActionHeld(GameActionId.PinTooltip);
            reader.Down.Clear();
            InputRouter.ClearHoldLatches(framesOnly: true);
            bool t2 = InputRouter.IsActionHeld(GameActionId.PinTooltip);
            reader.Down.Add(pin);
            InputRouter.ClearHoldLatches(framesOnly: true);
            bool t3 = !InputRouter.IsActionHeld(GameActionId.PinTooltip);
            reader.Down.Clear();
            // FG15-UX-02（FGR-UX-060“所有按住操作可以改为切换”，DEBT-FG0UX01-06）：镜头平移 / 直控移动也可切换（原 FG15-UX-01 排除，规格要求“所有”）。
            bool panNotToggle = InputRouter.IsToggleableHold(GameActionId.MoveForward) && InputRouter.IsToggleableHold(GameActionId.PinTooltip);
            GameSettings.SetHoldActionsToggle(false);
            InputRouter.ClearHoldLatches();
            Expect(holdHeld && holdReleased && t1 && sameFrame && t2 && t3 && panNotToggle,
                $"“按住改为切换”：关 = 按住才固定提示；开 = 按一下固定、松手仍固定、再按一下解除（同帧多次读取只翻一次）；镜头平移同样可切换（{holdHeld}/{holdReleased}/{t1}/{t2}/{t3}/{panNotToggle}）");
            InputRouter.DebugSetReader(null);
            InputRouter.Reset();

            // 接入鼠标灵敏度：镜头朝光标前探
            float2Eq(CameraDirector.UplinkLookAhead(new Vector2(0.2f, 0.1f), 10f, 1f), 0f, out bool center);
            Unity.Mathematics.float2 edge1 = CameraDirector.UplinkLookAhead(new Vector2(1f, 0f), 10f, 1f);
            Unity.Mathematics.float2 edge2 = CameraDirector.UplinkLookAhead(new Vector2(1f, 0f), 10f, 2f);
            float2Eq(CameraDirector.UplinkLookAhead(new Vector2(1f, 0f), 10f, 0f), 0f, out bool off);
            Expect(center && edge1.x > 0.5f && Mathf.Abs(edge2.x - 2f * edge1.x) < 1e-4f && off,
                $"接入鼠标灵敏度：光标在中央死区不前探；光标在右缘前探 {edge1.x:0.00} 米，灵敏度 2x → {edge2.x:0.00}；0 = 关（{center}/{off}）");

            // 引导开关
            string hook = "fg15ux01.selfcheck." + Guid.NewGuid().ToString("N");
            GameSettings.SetGuidanceEnabled(false);
            int suppressed = GuidanceHooks.SuppressedCount;
            bool blocked = !GuidanceHooks.Raise(hook) && GuidanceHooks.SuppressedCount == suppressed + 1 && !GameSettings.HasSeenGuidanceHook(hook);
            GameSettings.SetGuidanceEnabled(true);
            bool raised = GuidanceHooks.Raise(hook) && GameSettings.HasSeenGuidanceHook(hook);
            Expect(blocked && raised, $"引导开关：关着时钩子不触发、不记“已看过”；重新打开后第一次接触照样触发（{blocked}/{raised}）");

            // 因果链叠加开关
            CausalChainOverlay.ResetForTests();
            CausalChainOverlay.Toggle(null);
            bool opened = CausalChainOverlay.IsOpen;
            GameSettings.SetCausalChainOverlayEnabled(false);
            var chainReader = new FakeReader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(chainReader);
            InputRouter.SetScope(InputScope.Strategy);
            int pressesBefore = CausalChainOverlay.DisabledPresses;
            CausalChainOverlay.Tick(null, null);
            bool closed = !CausalChainOverlay.IsOpen;
            InputChord chainChord = GameSettings.KeyBindings.GetChord(GameActionId.CausalChain);
            chainReader.Down.Add(chainChord.Key);
            if ((chainChord.Mods & InputModifier.Ctrl) != 0)
            {
                chainReader.Held.Add(KeyCode.LeftControl);
            }
            InputRouter.DebugClearConsumedKeys();
            CausalChainOverlay.Tick(null, null);
            bool pressFeedback = CausalChainOverlay.DisabledPresses == pressesBefore + 1 && !CausalChainOverlay.IsOpen;
            GameSettings.SetCausalChainOverlayEnabled(true);
            InputRouter.DebugSetReader(null);
            InputRouter.Reset();
            CausalChainOverlay.ResetForTests();
            Expect(opened && closed && pressFeedback, $"因果链叠加开关（FGR-EMG-181）：关掉时已开的叠加立即收起；再按因果链键不打开、给出“已在设置里关闭”的提示（{opened}/{closed}/{pressFeedback}）");

            // 伤害数字
            DamageNumbers.ResetForTests();
            GameSettings.SetDamageNumbersEnabled(false);
            bool rejected = !DamageNumbers.Push(1, 12f, Vector3.zero, 0f) && DamageNumbers.Active.Count == 0;
            GameSettings.SetDamageNumbersEnabled(true);
            DamageNumbers.Push(1, 12f, Vector3.zero, 0f);
            DamageNumbers.Push(1, 8f, Vector3.zero, 0.1f);
            DamageNumbers.Push(2, 5f, Vector3.one, 0.1f);
            bool merged = DamageNumbers.Active.Count == 2 && DamageNumbers.Active[0].Text == "20";
            Func<string> observedBefore = FeedbackCues.ObservedSiteProvider;
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "ReactionPopupHud.uxml", out GameObject hudGo);
            int drawn;
            try
            {
                var hud = hudGo.AddComponent<ReactionPopupHudUIToolkit>();
                hud.BindView(root);
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = true;
                FeedbackCues.ObservedSiteProvider = () => null;
                hud.Tick(null, 0.2f);
                drawn = hud.DamageVisibleCount;
                hud.Tick(null, 0.2f + DamageNumbers.LifeSeconds + 0.5f);
                drawn = drawn * 10 + hud.DamageVisibleCount;
            }
            finally
            {
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = false;
                FeedbackCues.ObservedSiteProvider = observedBefore;
                Object.DestroyImmediate(hudGo);
            }
            GameSettings.SetDamageNumbersEnabled(false);
            bool clearedOnOff = DamageNumbers.Active.Count == 0;
            Expect(rejected && merged && drawn == 20 && clearedOnOff,
                $"伤害数字：默认关、关着时不收；打开后同一单位 0.35 秒内连续受伤合成一条（12 + 8 = {(DamageNumbers.Active.Count > 0 ? DamageNumbers.Active[0].Text : "20")}），两台各一条画在弹字层，过期消失；关掉立即清空（{rejected}/{merged}/画出 {drawn / 10}→{drawn % 10}/{clearedOnOff}）");

            // 语言
            SettingItem lang = SettingsCatalog.Find("gameplay.language");
            lang.SetIndex(1);
            bool en = GameSettings.Language == GameLanguage.En;
            lang.SetIndex(0);
            Expect(en && GameSettings.Language == GameLanguage.ZhCn, "语言：切到 English 再切回简体中文立即生效");

            // 其余开关写的就是消费方读的那一个（消费方各自的自检已断言行为）
            var pairs = new (string Id, Func<bool> Read)[]
            {
                ("gameplay.away_report", () => GameSettings.AwayReportAutoOpen), ("gameplay.home_guide", () => GameSettings.HomeGuideQuestsEnabled),
                ("gameplay.reaction_popups", () => GameSettings.ReactionPopupsEnabled), ("gameplay.reaction_slowmo", () => GameSettings.ReactionSlowMotionEnabled),
                ("gameplay.reaction_nudge", () => GameSettings.ReactionCameraNudgeEnabled), ("controls.edge_pan", () => GameSettings.EdgePanEnabled),
                ("access.subtitles", () => GameSettings.SubtitlesEnabled), ("access.flash", () => GameSettings.FlashReductionEnabled),
                ("access.shake", () => GameSettings.ScreenShakeEnabled), ("audio.mute_unfocused", () => GameSettings.MuteOnFocusLoss),
                ("gameplay.pause_unfocused", () => GameSettings.PauseOnFocusLoss), ("display.post_process", () => GameSettings.PostProcessEnabled),
            };
            var wrong = new List<string>();
            foreach ((string id, Func<bool> read) in pairs)
            {
                SettingItem it = SettingsCatalog.Find(id);
                bool before = it.GetBool();
                it.SetBool(!before);
                if (read() == before)
                {
                    wrong.Add(id);
                }
                it.SetBool(before);
            }
            Expect(wrong.Count == 0, $"{pairs.Length} 个开关：设置页写入即是消费方读取的同一个值" + (wrong.Count > 0 ? "——不一致：" + string.Join("、", wrong) : string.Empty));
            GameSettings.PostProcessSink = null;
        }

        private static void float2Eq(Unity.Mathematics.float2 v, float expect, out bool ok)
        {
            ok = Mathf.Abs(v.x - expect) < 1e-5f && Mathf.Abs(v.y - expect) < 1e-5f;
        }

        // ── C ──
        private sealed class FakeDisplay : IDisplayBackend
        {
            public int W = 1920, H = 1080;
            public FullScreenMode M = FullScreenMode.FullScreenWindow;
            public bool Refuse;
            public bool Verify;
            public bool IgnoreApply;
            public int Applies;
            public int Width => W;
            public int Height => H;
            public FullScreenMode Mode => M;
            public bool VerifyAfterApply => Verify;

            public List<Vector2Int> Supported() => new List<Vector2Int> { new Vector2Int(1280, 720), new Vector2Int(1600, 900), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440) };

            public bool Apply(int width, int height, FullScreenMode mode, out string error)
            {
                Applies++;
                error = null;
                if (Refuse)
                {
                    error = "驱动拒绝";
                    return false;
                }
                if (!IgnoreApply)
                {
                    W = width;
                    H = height;
                    M = mode;
                }
                return true;
            }
        }

        private static void CheckDisplay()
        {
            var fake = new FakeDisplay();
            float now = 100f;
            DisplayModeService.Backend = fake;
            DisplayModeService.RealTime = () => now;

            // 改分辨率 → 立即生效、不落盘；保留 → 落盘
            DisplayRequestResult r1 = DisplayModeService.Request(1600, 900, FullScreenMode.FullScreenWindow);
            bool applied = r1 == DisplayRequestResult.Applied && fake.W == 1600 && DisplayModeService.Pending && GameSettings.ResolutionWidth == 0
                           && Mathf.Approximately(DisplayModeService.SecondsLeft, 15f);
            DisplayModeService.Keep();
            bool kept = !DisplayModeService.Pending && GameSettings.ResolutionWidth == 1600 && GameSettings.ResolutionHeight == 900
                        && GameSettings.WindowMode == (int)FullScreenMode.FullScreenWindow;
            Expect(applied && kept, $"改分辨率 1920×1080 → 1600×900：立即生效、15 秒确认、确认前不写设置；点“保留”才写入（{applied}/{kept}）");

            // 还原
            DisplayModeService.Request(1280, 720, FullScreenMode.Windowed);
            DisplayModeService.Revert();
            bool reverted = fake.W == 1600 && fake.M == FullScreenMode.FullScreenWindow && GameSettings.ResolutionWidth == 1600 && !DisplayModeService.Pending;
            Expect(reverted, "点“还原”：回到上一次确认过的 1600×900 全屏窗口，设置不变");

            // 超时：倒数期间再改一次，回退目标仍是最初确认的那一份；暂停与 3x 倍速不影响倒数（真实时间）
            GameClock.SetPaused(true);
            GameClock.SetSpeed(3f);
            DisplayModeService.Request(1280, 720, FullScreenMode.FullScreenWindow);
            now += 5f;
            DisplayModeService.Request(1920, 1080, FullScreenMode.Windowed);
            now += 14.9f;
            DisplayModeService.Tick();
            bool notYet = DisplayModeService.Pending && fake.W == 1920;
            now += 0.2f;
            DisplayModeService.Tick();
            bool timedOut = !DisplayModeService.Pending && fake.W == 1600 && fake.H == 900 && fake.M == FullScreenMode.FullScreenWindow && GameSettings.ResolutionWidth == 1600;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            Expect(notYet && timedOut, $"超时自动还原：世界暂停 + 3x 倍速下照样按真实 15 秒倒数；倒数中再改一次，第二次改动 15 秒后还原到最初确认的 1600×900（{notYet}/{timedOut}）");

            // 负向：不支持的分辨率、引擎拒绝、切完尺寸不对
            int appliesBefore = fake.Applies;
            DisplayRequestResult bad = DisplayModeService.Request(1234, 777, FullScreenMode.ExclusiveFullScreen);
            bool unsupported = bad == DisplayRequestResult.Unsupported && fake.Applies == appliesBefore && !DisplayModeService.Pending
                               && DisplayModeService.LastError.Contains("1234") && !DisplayModeService.LastError.Contains("⟦");
            fake.Refuse = true;
            DisplayRequestResult refused = DisplayModeService.Request(1280, 720, FullScreenMode.FullScreenWindow);
            fake.Refuse = false;
            bool failed = refused == DisplayRequestResult.Failed && !DisplayModeService.Pending && DisplayModeService.LastError.Contains("驱动拒绝") && fake.W == 1600;
            fake.Verify = true;
            fake.IgnoreApply = true;
            DisplayModeService.Request(1280, 720, FullScreenMode.FullScreenWindow);
            // 真机上 Screen.SetResolution 下一帧才生效：等待确认期间下拉框显示请求中的值，不弹回旧值
            List<Vector2Int> resList = SettingsCatalog.Resolutions();
            bool shownPending = fake.W == 1600 && SettingsCatalog.Find("display.resolution").GetIndex() == resList.IndexOf(new Vector2Int(1280, 720))
                                && SettingsCatalog.Find("display.window_mode").GetIndex() == Array.IndexOf(DisplayModeService.Modes, FullScreenMode.FullScreenWindow);
            Expect(shownPending, $"等待确认期间（引擎还没切过去）分辨率 / 窗口模式下拉框显示请求中的 1280×720，不弹回旧值（{shownPending}）");
            now += 1.1f;
            DisplayModeService.Tick();
            bool verifyFail = !DisplayModeService.Pending && DisplayModeService.RevertCount > 0 && DisplayModeService.LastError.Length > 0;
            fake.Verify = false;
            fake.IgnoreApply = false;
            Expect(unsupported && failed && verifyFail,
                $"显示设置失败（负向）：显示器不支持 1234×777 → 不切、提示“{DisplayModeService.LastError}”；引擎拒绝 → 提示原因并保持原设置；切完 1 秒实际尺寸不对 → 自动还原并提示（{unsupported}/{failed}/{verifyFail}）");

            // 窗口模式下可以是不超过最大的任意尺寸；全屏只认列表
            Expect(DisplayModeService.IsSupported(1500, 800, FullScreenMode.Windowed) && !DisplayModeService.IsSupported(1500, 800, FullScreenMode.ExclusiveFullScreen)
                   && !DisplayModeService.IsSupported(4000, 3000, FullScreenMode.Windowed), "窗口模式允许不超过最大分辨率的任意尺寸；全屏只认显示器列表里的分辨率");

            // 从没确认过的冷启动：把启动时的显示设置记成已确认基线（第一次改了没确认就退出，下次启动按这份还原，不留未确认的分辨率）
            GameSettings.SetDisplayMode(0, 0, -1);
            fake.W = 1280;
            fake.H = 720;
            fake.M = FullScreenMode.Windowed;
            DisplayModeService.ApplyStored();
            bool baseline = GameSettings.ResolutionWidth == 1280 && GameSettings.ResolutionHeight == 720 && GameSettings.WindowMode == (int)FullScreenMode.Windowed;
            Expect(baseline, $"没确认过显示设置的冷启动：启动时的 1280×720 窗口记成已确认基线（{baseline}）");
            GameSettings.SetDisplayMode(1600, 900, (int)FullScreenMode.FullScreenWindow);

            // 启动时套用确认过的显示设置
            fake.W = 1920;
            fake.H = 1080;
            DisplayModeService.ApplyStored();
            Expect(fake.W == 1600 && fake.H == 900, "冷启动套用玩家确认过的 1600×900（没确认的改动不会被套用）");
        }

        // ── D ──
        private static void CheckResetCategories()
        {
            var fake = new FakeDisplay { W = 1280, H = 720 };
            DisplayModeService.Backend = fake;
            float now = 500f;
            DisplayModeService.RealTime = () => now;
            GameSettings.SetBrightness(1.3f);
            GameSettings.SetVSyncEnabled(true);
            GameSettings.SetQualityPreset(GameSettings.QualityLow);
            FramePacing.Set(60);
            GameSettings.SetMasterVolume(0.2f);
            GameSettings.SetAmbientVolume(0.1f);
            GameSettings.SetZoomSpeedMultiplier(2f);
            GameSettings.SetInvertScroll(true);
            GameSettings.SetUplinkMouseSensitivity(0.3f);
            GameSettings.SetAutoSaveMinutes(20);
            GameSettings.SetDamageNumbersEnabled(true);
            GameSettings.SetCausalChainOverlayEnabled(false);
            GameSettings.SetUiScale(1.4f);
            GameSettings.SetFlashReductionEnabled(true);

            SettingsCatalog.ResetCategory(SettingsCategory.Audio);
            bool audio = Mathf.Approximately(GameSettings.MasterVolume, 1f) && Mathf.Approximately(GameSettings.AmbientVolume, 1f)
                         && Mathf.Approximately(GameSettings.Brightness, 1.3f) && GameSettings.InvertScroll; // 别的分类不受影响
            SettingsCatalog.ResetCategory(SettingsCategory.Display);
            bool display = Mathf.Approximately(GameSettings.Brightness, 1f) && !GameSettings.VSyncEnabled && GameSettings.QualityPreset == GameSettings.QualityHigh
                           && FramePacing.TargetFps == FramePacing.DefaultFps && DisplayModeService.Pending && fake.W == 2560 && fake.M == FullScreenMode.FullScreenWindow;
            DisplayModeService.Revert();
            SettingsCatalog.ResetCategory(SettingsCategory.Controls);
            bool controls = Mathf.Approximately(GameSettings.ZoomSpeedMultiplier, 1f) && !GameSettings.InvertScroll && Mathf.Approximately(GameSettings.UplinkMouseSensitivity, 1f);
            SettingsCatalog.ResetCategory(SettingsCategory.Gameplay);
            bool gameplay = GameSettings.AutoSaveMinutes == 10 && !GameSettings.DamageNumbersEnabled && GameSettings.CausalChainOverlayEnabled;
            SettingsCatalog.ResetCategory(SettingsCategory.Accessibility);
            bool access = Mathf.Approximately(GameSettings.UiScale, 1f) && !GameSettings.FlashReductionEnabled;
            Expect(audio && display && controls && gameplay && access,
                $"每类“恢复默认”只复位本类：音频 / 画面（分辨率回到最大 + 全屏窗口并走 15 秒确认）/ 控制 / 游戏性 / 可访问性（{audio}/{display}/{controls}/{gameplay}/{access}）");
        }

        // ── E ──
        private static void CheckPersistence()
        {
            GameSettings.SetZoomSpeedMultiplier(1.75f);
            GameSettings.SetPauseOnFocusLoss(true);
            GameSettings.SetAutoSaveMinutes(5);
            GameSettings.SetShadowQuality(0);
            GameSettings.Load();
            bool kept = Mathf.Approximately(GameSettings.ZoomSpeedMultiplier, 1.75f) && GameSettings.PauseOnFocusLoss && GameSettings.AutoSaveMinutes == 5 && GameSettings.ShadowQuality == 0;
            PlayerPrefs.SetString(SettingsPrefsKey, "{\"UiScale\":1.2,\"MasterVolume\":0.5}");
            GameSettings.Load();
            bool defaults = Mathf.Approximately(GameSettings.UiScale, 1.2f) && Mathf.Approximately(GameSettings.ZoomSpeedMultiplier, 1f) && !GameSettings.PauseOnFocusLoss
                            && GameSettings.AutoSaveMinutes == 10 && !GameSettings.DamageNumbersEnabled && GameSettings.GuidanceEnabled && GameSettings.QualityPreset == GameSettings.QualityHigh
                            && GameSettings.WindowMode == -1;
            PlayerPrefs.DeleteKey(SettingsPrefsKey);
            GameSettings.Load();
            Expect(kept && defaults, $"设置写本机（不进战役存档）：重读后保留；旧设置 JSON 没有新字段时按默认补齐（{kept}/{defaults}）");
        }

        // ── F ──
        private static void CheckPanel()
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "SettingsPanel.uxml", out GameObject go);
            var fake = new FakeDisplay();
            DisplayModeService.Backend = fake;
            float now = 900f;
            DisplayModeService.RealTime = () => now;
            try
            {
                var panel = go.AddComponent<SettingsPanelUIToolkit>();
                panel.BindView(root);
                panel.SetOpen(true);
                var counts = new List<string>();
                bool rowsOk = true;
                var items = new List<SettingItem>();
                foreach (SettingsCategory c in SettingsCatalog.Categories)
                {
                    panel.SelectCategory(c);
                    SettingsCatalog.Items(c, items);
                    rowsOk &= panel.Rows.Count == items.Count && panel.Category == c;
                    counts.Add($"{GameText.Get(SettingsCatalog.CategoryKey(c))} {panel.Rows.Count}");
                }
                Expect(rowsOk, "面板按分类生成全部行：" + string.Join("、", counts));

                panel.SelectCategory(SettingsCategory.Gameplay);
                VisualElement dmgRow = panel.Rows.First(r => ((SettingItem)r.userData).Id == "gameplay.damage_numbers");
                bool before = GameSettings.DamageNumbersEnabled;
                Click(dmgRow.Q<Button>("Control"));
                bool toggled = GameSettings.DamageNumbersEnabled != before && dmgRow.Q<Button>("Control").text == GameText.Get(GameSettings.DamageNumbersEnabled ? "settings.on" : "settings.off");
                Click(dmgRow.Q<Button>("Control"));

                panel.SelectCategory(SettingsCategory.Audio);
                VisualElement master = panel.Rows.First(r => ((SettingItem)r.userData).Id == "audio.master");
                master.Q<Slider>("Control").value = 0.35f;
                bool slid = Mathf.Approximately(GameSettings.MasterVolume, 0.35f) && master.Q<Label>("Value").text == "35%";
                GameSettings.SetMasterVolume(0.9f);
                panel.Tick();
                bool follows = Mathf.Approximately(master.Q<Slider>("Control").value, 0.9f);

                panel.SelectCategory(SettingsCategory.Display);
                VisualElement res = panel.Rows.First(r => ((SettingItem)r.userData).Id == "display.resolution");
                var dd = res.Q<DropdownField>("Control");
                dd.index = 1; // 1600×900
                panel.Tick();
                bool banner = fake.W == 1600 && panel.ConfirmVisible && panel.ConfirmText.Contains("15");
                now += 6.2f;
                panel.Tick();
                bool countdown = panel.ConfirmText.Contains("9");
                Click(panel.KeepButton);
                bool keptUi = !panel.ConfirmVisible && GameSettings.ResolutionWidth == 1600;
                dd.index = 0; // 1280×720
                panel.Tick();
                panel.SetOpen(false); // 带着没确认的改动关面板 → 还原
                bool closeRevert = fake.W == 1600 && !DisplayModeService.Pending;
                panel.SetOpen(true);
                panel.SelectCategory(SettingsCategory.Difficulty);
                VisualElement ev = panel.Rows.First(r => ((SettingItem)r.userData).Id == "difficulty.events");
                bool difficultyLocked = CampaignSession.Current != null || !ev.Q<VisualElement>("Control").enabledSelf;
                Expect(toggled && slid && follows && banner && countdown && keptUi && closeRevert && difficultyLocked,
                    $"面板操作：开关点一下生效、滑条拖动生效并显示百分比、别处改了设置面板跟上、选分辨率出现“保留 / 还原（15 秒）”倒数、点保留写入、带着未确认改动关面板即还原、没进战役时难度行禁用（{toggled}/{slid}/{follows}/{banner}/{countdown}/{keptUi}/{closeRevert}/{difficultyLocked}）");
                panel.SetOpen(false);
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
            string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "SettingsPanel.uxml", "SettingsRoot");
            Expect(!probe.Contains("FAIL"), "设置面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n')[0]);
            string pause = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow");
            Expect(!pause.Contains("FAIL"), "暂停菜单加了“设置…”后布局探针仍通过：" + pause.Split('\n')[0]);
        }

        /// <summary>F2：设置 → 游戏性里的“反应记录 / 通知自动暂停规则”两块面板层级低于设置面板，必须作为设置的子页打开：
        /// 点了之后子面板可见且是当前页、设置暂时隐藏；Esc（关闭最上层）回到设置；“恢复本类默认”恢复的通知自动暂停规则在这一页有入口。</summary>
        private static void CheckSubPanels()
        {
            var state = new CampaignState();
            CampaignSession.Set(3, state);
            VisualElement sroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "SettingsPanel.uxml", out GameObject sgo);
            VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ReactionLogPanel.uxml", out GameObject rgo);
            VisualElement nroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "NotificationHud.uxml", out GameObject ngo);
            ReactionLogPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = sgo.AddComponent<SettingsPanelUIToolkit>();
                panel.BindView(sroot);
                var log = rgo.AddComponent<ReactionLogPanelUIToolkit>();
                log.BindView(rroot);
                var hud = ngo.AddComponent<NotificationHudUIToolkit>();
                hud.BindView(nroot);
                panel.SetOpen(true);
                panel.SelectCategory(SettingsCategory.Gameplay);

                Click(panel.Rows.First(r => ((SettingItem)r.userData).Id == "gameplay.reaction_log").Q<Button>("Control"));
                bool logTop = ReactionLogPanelUIToolkit.IsOpen && log.PanelVisible && ReferenceEquals(UiEscapeStack.CurrentPage, log) && UiEscapeStack.IsPageSuspended(panel);
                bool logBack = UiEscapeStack.CloseTop() && !ReactionLogPanelUIToolkit.IsOpen && SettingsPanelUIToolkit.IsOpen && ReferenceEquals(UiEscapeStack.CurrentPage, panel);

                Click(panel.Rows.First(r => ((SettingItem)r.userData).Id == "gameplay.auto_pause_rules").Q<Button>("Control"));
                bool apTop = NotificationHudUIToolkit.CenterOpen && hud.AutoPauseSettingsVisible && hud.AutoPauseList.childCount > 0
                             && ReferenceEquals(UiEscapeStack.CurrentPage, hud) && UiEscapeStack.IsPageSuspended(panel);
                bool apBack = UiEscapeStack.CloseTop() && !NotificationHudUIToolkit.CenterOpen && SettingsPanelUIToolkit.IsOpen && ReferenceEquals(UiEscapeStack.CurrentPage, panel);
                Expect(logTop && logBack && apTop && apBack,
                    $"设置 → 游戏性：点“反应记录”后反应记录可见且在最上层、设置暂时隐藏（{logTop}），Esc 回到设置（{logBack}）；点“通知自动暂停规则”直接打开通知中心的按类型自动暂停区（{apTop}），Esc 回到设置（{apBack}）");
                panel.SetOpen(false);
            }
            finally
            {
                ReactionLogPanelUIToolkit.InWorldOverrideForTests = false;
                ReactionLogPanelUIToolkit.Close();
                NotificationHudUIToolkit.CloseCenter();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(sgo);
                Object.DestroyImmediate(rgo);
                Object.DestroyImmediate(ngo);
                CampaignSession.Clear();
            }
        }

        private static void Click(Button b)
        {
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        // ── G ──
        private static void CheckMainMenu()
        {
            // 暂停菜单入口
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject go);
            try
            {
                var menu = go.AddComponent<PauseMenuUIToolkit>();
                menu.BindView(root);
                Expect(menu.SettingsButton != null && menu.SettingsButton.text == GameText.Get("ui.pause.settings"), "游戏内暂停菜单有“设置…”入口");
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }

            // 主菜单（uGUI）走真实初始化：“全部设置…”按钮打开同一个面板；设置页控件与登记表同一个值
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameRes/Raw/UI/MainMenuUI.prefab");
            GameObject instance = Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var window = (MainMenuUI)Activator.CreateInstance(typeof(MainMenuUI), true);
                typeof(UIWindow).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, instance);
                typeof(MainMenuUI).GetMethod("ScriptGenerator", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var btn = (UnityEngine.UI.Button)typeof(MainMenuUI).GetField("_btnAllSettings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                string label = btn != null ? btn.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text : null;
                Expect(btn != null && label == GameText.Get("settings.open_all") && btn.onClick.GetPersistentEventCount() == 0,
                    $"主菜单设置页有“{label}”按钮（打开与游戏内同一个 UI Toolkit 设置面板）");

                // 主菜单 uGUI 控件 → 登记表项：改登记表项后设置页控件跟着变（同一份设置），改控件后登记表项跟着变
                var map = new (string Field, string Id)[]
                {
                    ("_toggleEdgePan", "controls.edge_pan"), ("_toggleScreenShake", "access.shake"), ("_toggleFlashReduction", "access.flash"),
                    ("_toggleSubtitles", "access.subtitles"),
                    ("_sliderUiScale", "access.ui_scale"), ("_sliderCameraSpeed", "controls.camera_speed"), ("_sliderMasterVolume", "audio.master"),
                    ("_sliderMusicVolume", "audio.music"), ("_sliderSfxVolume", "audio.sfx"), ("_sliderUiVolume", "audio.ui"),
                };
                MethodInfo refresh = typeof(MainMenuUI).GetMethod("RefreshSettingsView", BindingFlags.Instance | BindingFlags.NonPublic);
                // FG15-UX-02：主菜单旧开关“色盲安全图标”对应三档色盲模式（开 = 保留当前模式 / 没开过时红绿，关 = 关闭）。
                {
                    var cvd = (UnityEngine.UI.Toggle)typeof(MainMenuUI).GetField("_toggleColorblindIcons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                    GameSettings.SetColorblindMode(ColorblindMode.BlueYellow);
                    refresh.Invoke(window, null);
                    bool shownOn = cvd.isOn;
                    cvd.isOn = false;
                    bool off = GameSettings.ColorblindMode == ColorblindMode.Off;
                    cvd.isOn = true;
                    bool rg = GameSettings.ColorblindMode == ColorblindMode.RedGreen;
                    GameSettings.SetColorblindMode(ColorblindMode.Off);
                    Expect(shownOn && off && rg, $"主菜单“色盲安全图标”开关与色盲模式同一份设置（蓝黄 → 显示开 {shownOn}；关 → 关闭 {off}；开 → 红绿 {rg}）");
                }
                var mismatch = new List<string>();
                foreach ((string field, string id) in map)
                {
                    object control = typeof(MainMenuUI).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                    SettingItem item = SettingsCatalog.Find(id);
                    if (control is UnityEngine.UI.Toggle t)
                    {
                        bool v = !item.GetBool();
                        item.SetBool(v);
                        refresh.Invoke(window, null);
                        bool a = t.isOn == v;
                        t.isOn = !v; // uGUI 控件回调写设置
                        if (!a || item.GetBool() != !v)
                        {
                            mismatch.Add(id);
                        }
                        item.SetBool(!v);
                    }
                    else if (control is UnityEngine.UI.Slider s)
                    {
                        float v = Mathf.Lerp(item.Min, item.Max, 0.3f);
                        item.SetFloat(v);
                        refresh.Invoke(window, null);
                        bool a = Mathf.Abs(s.value - item.GetFloat()) < 1e-3f;
                        s.value = Mathf.Lerp(item.Min, item.Max, 0.7f);
                        if (!a || Mathf.Abs(item.GetFloat() - Mathf.Lerp(item.Min, item.Max, 0.7f)) > 1e-3f)
                        {
                            mismatch.Add(id);
                        }
                    }
                    else
                    {
                        mismatch.Add(id + "(控件不存在)");
                    }
                }
                Expect(mismatch.Count == 0, $"主菜单设置页 {map.Length} 个控件与游戏内设置面板读写同一份设置（两边互改互见）" + (mismatch.Count > 0 ? "——不一致：" + string.Join("、", mismatch) : string.Empty));
            }
            finally
            {
                Object.DestroyImmediate(instance);
                GameSettings.ResetAllToDefault();
            }
        }

        // ── H ──
        private static void CheckRebind()
        {
            var rows = new List<InputActionDef>();
            KeyBindingsPanelUIToolkit.Filter(InputContext.None, string.Empty, rows);
            int enumCount = Enum.GetValues(typeof(GameActionId)).Cast<GameActionId>().Count(a => InputActionCatalog.TryGet(a, out _));
            Expect(rows.Count == InputActionCatalog.All.Count && rows.Count == enumCount && rows.Count > 80,
                $"按键面板按 fg.TbInputAction 动态列出全部 {rows.Count} 个动作（以后加动作不用改界面），每个都可重绑");

            InputBindingSet defaults = InputBindingSet.CreateDefault();
            var conflicts = defaults.FindAllConflicts();
            Expect(conflicts.Count == 0, "默认键位在每个上下文里没有冲突（FGT-UX-003）" + (conflicts.Count > 0 ? "：" + string.Join("、", conflicts.Select(c => c.A + "↔" + c.B)) : string.Empty));

            bool skillsGone = !Enum.GetNames(typeof(GameActionId)).Any(n => n.StartsWith("DirectSkillSlot", StringComparison.Ordinal));
            Expect(skillsGone, "“接入技能 1～5”（0.2 规格没有）已删除，不再占用 LeftShift / Q / Z / R / F（FG-GAP-046）");

            InputActionDef chain = InputActionCatalog.All.First(d => d.Action == GameActionId.CausalChain);
            InputChord chainChord = defaults.GetChord(GameActionId.CausalChain);
            bool chainKey = chainChord == new InputChord(KeyCode.T, InputModifier.Ctrl) && (chain.Contexts & InputContext.Strategy) != 0 && (chain.Contexts & InputContext.Uplink) != 0
                            && chain.Status == InputActionStatus.Wired;
            Expect(chainKey, $"因果链正式默认键 Ctrl+T（战略 + 接入上下文，可重绑，DEBT-EMG1CHAIN01-01）");

            // FG15-UX-01 新增快捷键（DEBT-FG2FW04-01 / FG6DEF08-03 / FG6DEF01-06 / FG6DEF02-08 / FG8EXP02-06）：表里登记、已接入、默认键如约，生产代码按动作消费（不读字面量）
            var added = new (GameActionId A, KeyCode K, InputModifier M, InputContext C)[]
            {
                (GameActionId.OpenReactionLog, KeyCode.X, InputModifier.Alt, InputContext.Strategy), (GameActionId.OpenRaidHistory, KeyCode.Y, InputModifier.Alt, InputContext.Strategy),
                (GameActionId.TurretCycleMode, KeyCode.M, InputModifier.Ctrl, InputContext.Interface), (GameActionId.DefenseTogglePattern, KeyCode.L, InputModifier.Ctrl, InputContext.Interface),
                (GameActionId.FollowExpedition, KeyCode.E, InputModifier.Ctrl, InputContext.Strategy), (GameActionId.ExpeditionAdvance, KeyCode.R, InputModifier.Ctrl, InputContext.Strategy),
                (GameActionId.ExpeditionHold, KeyCode.G, InputModifier.Ctrl, InputContext.Strategy), (GameActionId.ExpeditionReturn, KeyCode.J, InputModifier.Ctrl, InputContext.Strategy),
                (GameActionId.OpenBuildingDefensePanel, KeyCode.M, InputModifier.Alt, InputContext.Interface),
            };
            string pump = System.IO.File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/UiKitInputPump.cs");
            var badKeys = new List<string>();
            foreach ((GameActionId a, KeyCode k, InputModifier m, InputContext c) in added)
            {
                bool ok = InputActionCatalog.TryGet(a, out InputActionDef d) && d.Status == InputActionStatus.Wired && (d.Contexts & c) != 0
                          && defaults.GetChord(a) == new InputChord(k, m) && pump.Contains("ConsumeContextAction(GameActionId." + a + ")");
                if (!ok)
                {
                    badKeys.Add(a.ToString());
                }
            }
            Expect(badKeys.Count == 0, $"新增 {added.Length} 个快捷键（反应记录 Alt+X、突袭历史 Alt+Y、炮塔目标模式 Ctrl+M、陷阱铺设方式 Ctrl+L、远征跟随 / 前进 / 待命 / 返回 Ctrl+E / R / G / J）登记、默认键、按动作消费" + (badKeys.Count > 0 ? "——不符：" + string.Join("、", badKeys) : string.Empty));

            // 冲突：把因果链改成 T → 与“命令：攻击”冲突，不落地；确认覆盖 → 攻击变未绑定；恢复默认
            GameSettings.ResetKeyBindingsToDefault();
            var hit = new List<GameActionId>();
            RebindResult r = GameSettings.TryRebind(GameActionId.CausalChain, new InputChord(KeyCode.T, InputModifier.None), hit);
            bool conflict = r == RebindResult.Conflict && hit.Contains(GameActionId.CommandAttack) && GameSettings.KeyBindings.GetChord(GameActionId.CausalChain) == chainChord;
            RebindResult forced = GameSettings.ForceRebind(GameActionId.CausalChain, new InputChord(KeyCode.T, InputModifier.None));
            bool overwritten = forced == RebindResult.Ok && !GameSettings.KeyBindings.GetChord(GameActionId.CommandAttack).IsBound;
            // 不同上下文不算冲突：建造上下文的“旋转”R 与战略的“命令：移动”R 同键共存
            bool crossContextOk = defaults.GetKey(GameActionId.Rotate) == defaults.GetKey(GameActionId.CommandMove);
            GameSettings.ResetKeyBindingsToDefault();
            Expect(conflict && overwritten && crossContextOk && GameSettings.KeyBindings.CustomizedCount == 0,
                $"重绑冲突（负向）：同一上下文撞键 → 提示冲突、不落地；选覆盖 → 被抢的动作变未绑定；不同上下文同键不算冲突；全部恢复默认（{conflict}/{overwritten}/{crossContextOk}）");
        }

        // ── I ──
        private static void CheckFocus()
        {
            bool paused = false;
            FocusPolicy.ResetForTests();
            FocusPolicy.WorldRunning = () => true;
            FocusPolicy.IsPaused = () => paused;
            FocusPolicy.SetPaused = v => paused = v;
            GameSettings.SetPauseOnFocusLoss(false);
            FocusPolicy.OnFocusChanged(false);
            bool keepRunning = !paused;
            FocusPolicy.OnFocusChanged(true);
            GameSettings.SetPauseOnFocusLoss(true);
            FocusPolicy.OnFocusChanged(false);
            bool pausedOnLoss = paused && FocusPolicy.PausedByFocus;
            FocusPolicy.OnFocusChanged(true);
            bool resumed = !paused && !FocusPolicy.PausedByFocus;
            paused = true; // 玩家失焦前自己暂停了
            FocusPolicy.OnFocusChanged(false);
            FocusPolicy.OnFocusChanged(true);
            bool stayPaused = paused;
            Expect(keepRunning && pausedOnLoss && resumed && stayPaused,
                $"失焦暂停（FG00 B09 / FG-GAP-017）：默认继续运行；打开后失焦即暂停、回焦恢复；失焦前玩家已暂停的回焦后仍暂停（{keepRunning}/{pausedOnLoss}/{resumed}/{stayPaused}）");

            GameSettings.SetMasterVolume(0.7f);
            GameSettings.SetMuteOnFocusLoss(true);
            FocusPolicy.OnFocusChanged(false);
            FeedbackCues.ComputeVolumes(out float muted, out _);
            FocusPolicy.OnFocusChanged(true);
            FeedbackCues.ComputeVolumes(out float back, out _);
            GameSettings.SetMuteOnFocusLoss(false);
            FocusPolicy.OnFocusChanged(false);
            FeedbackCues.ComputeVolumes(out float noMute, out _);
            FocusPolicy.OnFocusChanged(true);
            Expect(Mathf.Approximately(muted, 0f) && Mathf.Approximately(back, 0.7f) && Mathf.Approximately(noMute, 0.7f) && Mathf.Approximately(GameSettings.MasterVolume, 0.7f),
                $"失焦静音：失焦时推给音频模块的总音量 {muted}、回焦 {back}；不勾选时失焦照常 {noMute}；玩家的音量设置不变");
            FocusPolicy.ResetForTests();
            GameSettings.SetPauseOnFocusLoss(false);
        }

        // ── J ──
        private static void CheckAutoSave()
        {
            CampaignState state = new CampaignState();
            CampaignSession.Set(3, state);
            IntervalAutoSave.ResetForTests();
            int saves = 0;
            IntervalAutoSave.SaveHandler = () =>
            {
                saves++;
                return SaveResult.Ok;
            };
            GameSettings.SetAutoSaveMinutes(5);
            for (int i = 0; i < 299; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            bool notYet = saves == 0;
            IntervalAutoSave.Tick(1f, true, false);
            bool fired = saves == 1 && IntervalAutoSave.Elapsed == 0f;
            for (int i = 0; i < 400; i++)
            {
                IntervalAutoSave.Tick(1f, true, true); // 暂停：不累计
            }
            bool pausedHold = saves == 1;
            GameClock.SetSpeed(3f);
            for (int i = 0; i < 300; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            GameClock.SetSpeed(0.5f);
            for (int i = 0; i < 300; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            GameClock.SetSpeed(1f);
            bool speedIndependent = saves == 3;
            GameSettings.SetAutoSaveMinutes(0);
            for (int i = 0; i < 2000; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            bool off = saves == 3;
            GameSettings.SetAutoSaveMinutes(5);
            int refusals = 0;
            IntervalAutoSave.SaveHandler = () =>
            {
                refusals++;
                return new SaveResult(SaveOutcome.NoActiveCampaign, "拒绝");
            };
            float backoff = IntervalAutoSave.RetrySeconds;
            for (int i = 0; i < 300 + 2 * (int)backoff; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            // 被拒：不清零（不丢已累计的间隔），退避 RetrySeconds 再试，不每帧重试；连续失败只提示一次；成功后清零
            bool retry = refusals == 3 && IntervalAutoSave.Elapsed >= 300f - backoff && IntervalAutoSave.FailStreak == 3;
            IntervalAutoSave.SaveHandler = () => SaveResult.Ok;
            for (int i = 0; i < (int)backoff; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            retry &= IntervalAutoSave.FailStreak == 0 && IntervalAutoSave.Elapsed == 0f;
            GameSettings.SetAutoSaveMinutes(10);
            IntervalAutoSave.ResetForTests();
            CampaignSession.Clear();
            Expect(notYet && fired && pausedHold && speedIndependent && off && retry,
                $"自动存档间隔 5 分钟：满 300 真实秒存一次；暂停不累计；0.5x / 3x 倍速下仍按真实时间（共 {saves} 次）；“关闭”不存；被拒时不清零、退避再试（不每帧重试）、成功后清零（{notYet}/{fired}/{pausedHold}/{speedIndependent}/{off}/{retry}）");
        }

        // ── K ──
        private static void CheckDifficulty()
        {
            var state = new CampaignState();
            CampaignSession.Set(2, state);
            SettingItem ev = SettingsCatalog.Find("difficulty.events");
            bool available = ev.Available();
            ev.SetIndex(0);
            int f0 = Campaign.Env.EventDirectorService.Frequency(state);
            ev.SetIndex(3);
            string json = JsonUtility.ToJson(state);
            var loaded = JsonUtility.FromJson<CampaignState>(json);
            int f3 = Campaign.Env.EventDirectorService.Frequency(loaded);
            SettingsCatalog.ResetCategory(SettingsCategory.Difficulty);
            int fStd = Campaign.Env.EventDirectorService.Frequency(state);
            string silent = SettingsCatalog.Find("difficulty.silent_night").ValueText;
            CampaignSession.Clear();
            bool lockedOutside = !ev.Available();
            Expect(available && f0 == 0 && f3 == 3 && fStd == Campaign.Env.EventDirectorService.FrequencyStandard && silent.Length > 0 && !silent.Contains("⟦") && lockedOutside,
                $"难度类（游戏中可改、随存档）：事件频率 关闭 → 多 写进战役存档、存读档后仍是“多”；恢复默认回到“标准”；静默夜频率显示“{silent}”；不在战役里时禁用（{available}/{f0}/{f3}/{fStd}/{lockedOutside}）");
        }

        // ── L ──
        private static void CheckCommandFeedback()
        {
            var hit = new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.AttackOutcome, Other = 6 };
            var ended = new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.CommandEnded, Code2 = (byte)BinGames.Sim.Combat.CombatCommandKind.Attack, Other = 6 };
            var moved = new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.CommandEnded, Code2 = (byte)BinGames.Sim.Combat.CombatCommandKind.Move };
            string n1 = GameLogic.Campaign.Combat.CombatSite.KernelHostileName(hit);
            string n2 = GameLogic.Campaign.Combat.CombatSite.KernelHostileName(ended);
            string line = GameText.Format("squad.ev.hit", "#6", n1);
            Expect(!string.IsNullOrEmpty(n1) && n1 == n2 && !n1.Contains("⟦") && GameLogic.Campaign.Combat.CombatSite.KernelHostileName(moved) == null && !line.Contains(" 。"),
                $"FG-GAP-141：右键攻击内核敌对单位时命令栏目标名不再留空——“{line}”；移动命令结束仍不带目标");
            // 内核里查得到 → 按阵营 + 种类取名（敌对驻军 / 敌对炮塔不同名）
            var kernel = new BinGames.Sim.Combat.CombatKernel(GameLogic.Campaign.Combat.CombatSite.ConfigFromTuning(), 8);
            const BinGames.Sim.Combat.CombatUnitFlags live = BinGames.Sim.Combat.CombatUnitFlags.Alive | BinGames.Sim.Combat.CombatUnitFlags.Targetable;
            int foe = kernel.Spawn(new BinGames.Sim.Combat.CombatSpawn { ExtKey = -1, Kind = BinGames.Sim.Combat.CombatUnitKind.Enemy, Faction = BinGames.Sim.Combat.CombatFaction.Hostile,
                Flags = live, Radius = 0.5f, Health = 10f, MaxHealth = 10f, Weapon = -1, BehaviorProfile = -1, Priority = 1 });
            int tur = kernel.Spawn(new BinGames.Sim.Combat.CombatSpawn { ExtKey = -1, Kind = BinGames.Sim.Combat.CombatUnitKind.Turret, Faction = BinGames.Sim.Combat.CombatFaction.Hostile,
                Flags = live, Radius = 0.5f, Health = 10f, MaxHealth = 10f, Weapon = -1, BehaviorProfile = -1, Priority = 1 });
            string kFoe = GameLogic.Campaign.Combat.CombatSite.KernelHostileName(new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.AttackOutcome, Other = foe }, kernel);
            string kTur = GameLogic.Campaign.Combat.CombatSite.KernelHostileName(new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.AttackOutcome, Other = tur }, kernel);
            string kGone = GameLogic.Campaign.Combat.CombatSite.KernelHostileName(new BinGames.Sim.Combat.CombatEvent { Kind = BinGames.Sim.Combat.CombatEventKind.AttackOutcome, Other = 9999 }, kernel);
            kernel.Dispose();
            Expect(kFoe == GameText.Format("squad.target.kernel_unit", GameText.Get("squad.faction.hostile"), GameText.Get("squad.unitkind.enemy")) && kTur != kFoe && !kTur.Contains("⟦") && kGone == n1,
                $"FG-GAP-141：内核里查得到的目标按阵营 + 种类取名（“{kFoe}” / “{kTur}”），已移除的退回“{kGone}”");

            MethodInfo notify = typeof(GameLogic.Campaign.Regions.HomeValleyCombatTargets).GetMethod("NotifyPlayerFireFailure", BindingFlags.Static | BindingFlags.NonPublic);
            FeedbackCues.ResetForTests();
            int before = GameLogic.Campaign.Regions.HomeValleyCombatTargets.PlayerFireFailureHints;
            string why = GameText.Get("squad.target.hostile_unit") + "：射程外";
            notify.Invoke(null, new object[] { true, why });
            bool aiQuiet = GameLogic.Campaign.Regions.HomeValleyCombatTargets.PlayerFireFailureHints == before && FeedbackCues.CountOf(FeedbackCueId.Denied) == 0;
            notify.Invoke(null, new object[] { false, why });
            bool shown = GameLogic.Campaign.Regions.HomeValleyCombatTargets.PlayerFireFailureHints == before + 1 && FeedbackCues.LastCue == FeedbackCueId.Denied
                         && (FeedbackCues.LastCaptionText ?? string.Empty).Contains("射程外");
            string src = System.IO.File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyCombatTargets.cs");
            int sites = src.Split(new[] { "NotifyPlayerFireFailure(isAiSource" }, StringSplitOptions.None).Length - 1;
            FeedbackCues.ResetForTests();
            Expect(aiQuiet && shown && sites >= 2,
                $"FG-GAP-182：玩家直控开火没打出去（未命中 / 被残骸挡下两处）→ HUD 字幕条“{FeedbackCues.LastCaptionText ?? why}”+ 拒绝提示音；AI 自己开火失败不打扰（{aiQuiet}/{shown}/接入点 {sites}）");

            var marching = new GameLogic.Campaign.Regions.DropPreview { Recall = true, Ok = true, MarchWarning = true };
            marching.Machines.Add(1);
            var still = new GameLogic.Campaign.Regions.DropPreview { Recall = true, Ok = true };
            still.Machines.Add(1);
            string warnText = GameLogic.Campaign.Regions.DropPodService.PreviewText(marching);
            string calmText = GameLogic.Campaign.Regions.DropPodService.PreviewText(still);
            string hold = InputDisplay.ForAction(GameActionId.ExpeditionHold);
            string missions = System.IO.File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/DropPodService.Missions.cs");
            int recallAt = missions.IndexOf("public static DropPreview PreviewRecall(", StringComparison.Ordinal);
            bool setInRecall = recallAt >= 0 && missions.IndexOf("p.MarchWarning = ExpeditionMarchService.IsMarching(s);", recallAt, StringComparison.Ordinal) > recallAt;
            Expect(warnText.Contains(hold) && !warnText.Contains("⟦") && !calmText.Contains(hold) && setInRecall,
                $"DEBT-FG8EXP05-04：远征队还在行军时，回收预览提醒“先待命（{hold}）”，没在行军时不提醒（回收预览按 IsMarching 设置）");
        }

        private sealed class FakeReader : IInputReader
        {
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public readonly HashSet<KeyCode> Down = new HashSet<KeyCode>();
            public float Scroll;
            public bool GetKey(KeyCode key) => Held.Contains(key) || Down.Contains(key);
            public bool GetKeyDown(KeyCode key) => Down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => Scroll;
        }

        // ── M ── DEBT-EMG2RULE01-04（FGR-EMG-088）
        private sealed class BenchRules : GameLogic.Campaign.Combat.CombatSiteRules
        {
        }

        private static bool KernelAvoids(GameLogic.Campaign.Combat.CombatSite site, int logicId)
        {
            if (!site.TryGetMachineUnit(logicId, out int unit))
            {
                return false;
            }
            for (int i = 0; i < site.Kernel.SlotCount; i++)
            {
                BinGames.Sim.Combat.CombatUnitView v = site.Kernel.ViewAt(i);
                if (v.Id == unit)
                {
                    return (v.Flags & BinGames.Sim.Combat.CombatUnitFlags.NoHazardAvoid) == 0;
                }
            }
            return false;
        }

        private static void CheckHazardDoctrine()
        {
            const string bench = "selfcheck_hazard_doctrine";
            var state = new CampaignState();
            CampaignSession.Set(3, state);
            MachineRegistry.ResetForNewCampaign();
            GameLogic.Campaign.Combat.CombatSites.Close(bench, state, dropRecord: true);
            GameLogic.Campaign.Combat.CombatSite site = GameLogic.Campaign.Combat.CombatSites.Open(bench, state, false, new BenchRules(), out _);
            VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out GameObject rgo);
            try
            {
                MachineOpResult r = MachineRegistry.SpawnMachine(GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId, GameLogic.Campaign.Regions.HomeValleyLayout.BlueprintErc001Id,
                    GameLogic.Campaign.Regions.HomeValleyLayout.RegionId, Vector2.zero, 100f, 100f);
                MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec);
                site.SpawnMachine(state, rec, new Vector2(4f, 4f), false);
                bool defaultOn = rec != null && !rec.NoHazardAvoid && KernelAvoids(site, rec.LogicId);

                // 名册详情里的开关（玩家点的同一个控件）
                var roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                RosterPanelUIToolkit.OpenDetail(rec.LogicId);
                Toggle t = roster.HazardToggle;
                bool shown = t != null && t.value && t.text == GameText.Get("roster.hazard.label");
                t.value = false;
                bool off = rec.NoHazardAvoid && !KernelAvoids(site, rec.LogicId);

                // 存档往返走真实路径：MachineRegistry.ExportToCampaignState（SaveWithExport 同一步）→ 存档同款 JsonUtility 整份 CampaignState
                // → LoadFromCampaignState（读档同一步，两头都过 CloneRecord）→ 重新进地点按读回的记录写内核；记录、内核、名册开关三者一致仍为关。
                int id = rec.LogicId;
                MachineRegistry.ExportToCampaignState(state);
                CampaignState loaded = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(state));
                site.RemoveMachine(id, export: false);
                MachineRegistry.LoadFromCampaignState(loaded);
                MachineRegistry.TryGetRecord(id, out rec);
                if (rec != null)
                {
                    site.SpawnMachine(state, rec, new Vector2(4f, 4f), false);
                }
                t.SetValueWithoutNotify(true); // 先弄脏控件，确认是读回的记录把它刷成“关”
                RosterPanelUIToolkit.OpenDetail(id);
                roster.Refresh();
                bool persisted = rec != null && rec.NoHazardAvoid && !KernelAvoids(site, id) && t.value == false;

                t.value = true;
                bool onAgain = !rec.NoHazardAvoid && KernelAvoids(site, rec.LogicId);
                RosterPanelUIToolkit.Close();

                // 负向：不存在 / 阵亡的机器拒绝并写原因，不改记录
                bool unknown = !MachineRoster.TrySetHazardAvoid(987654, false, out string why1) && !string.IsNullOrEmpty(why1);
                rec.IsAlive = false;
                bool dead = !MachineRoster.TrySetHazardAvoid(rec.LogicId, false, out string why2) && !string.IsNullOrEmpty(why2) && !rec.NoHazardAvoid;
                rec.IsAlive = true;
                bool oldSave = !JsonUtility.FromJson<MachineRecord>("{\"LogicId\":5}").NoHazardAvoid;
                Expect(defaultOn && shown && off && persisted && onAgain && unknown && dead && oldSave,
                    $"名册详情“避开危险物质”：缺省开且内核避险（{defaultOn}）；开关显示当前值（{shown}）；点关 → 记录与内核同时关（{off}）；存档往返 + 重进地点仍关（{persisted}）；点开恢复（{onAgain}）；未知 / 阵亡机器拒绝写原因（{unknown}/{dead}）；旧档缺字段 = 开（{oldSave}）");
            }
            finally
            {
                Object.DestroyImmediate(rgo);
                GameLogic.Campaign.Combat.CombatSites.Close(bench, state, dropRecord: true);
                MachineRegistry.ResetForNewCampaign();
                CampaignSession.Clear();
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
