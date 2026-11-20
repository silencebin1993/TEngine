using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Settings
{
    public enum SettingsCategory
    {
        Display = 0,
        Audio = 1,
        Controls = 2,
        Gameplay = 3,
        Difficulty = 4,
        Accessibility = 5,
    }

    public enum SettingKind
    {
        Toggle = 0,
        Slider = 1,
        Choice = 2,
        Action = 3,
        /// <summary>只读一行（例如“静默夜：每 5 日一次（随难度）”）。</summary>
        Info = 4,
    }

    /// <summary>一条设置：名字、怎么读写、范围 / 选项。两个界面（游戏内 UI Toolkit 设置面板、主菜单 uGUI 设置页）都只通过它读写，保证一致。</summary>
    public sealed class SettingItem
    {
        public string Id;
        public SettingsCategory Category;
        public SettingKind Kind;
        public string NameKey;
        public Func<bool> GetBool;
        public Action<bool> SetBool;
        public Func<float> GetFloat;
        public Action<float> SetFloat;
        public float Min;
        public float Max = 1f;
        public Func<float, string> Format;
        public Func<List<string>> Choices;
        public Func<int> GetIndex;
        public Action<int> SetIndex;
        public Action Invoke;
        public Func<string> InfoText;
        /// <summary>当前能不能改（例如难度只在战役里能改）；不能改时界面禁用这一行并显示 <see cref="UnavailableKey"/>。</summary>
        public Func<bool> Available = () => true;
        public string UnavailableKey;

        public string Name => GameText.Get(NameKey);

        /// <summary>当前值的显示文字（两个界面同一份；一致性自检逐项比较）。</summary>
        public string ValueText
        {
            get
            {
                switch (Kind)
                {
                    case SettingKind.Toggle:
                        return GameText.Get(GetBool() ? "settings.on" : "settings.off");
                    case SettingKind.Slider:
                        return Format != null ? Format(GetFloat()) : SettingsCatalog.Percent(GetFloat());
                    case SettingKind.Choice:
                    {
                        List<string> c = Choices();
                        int i = GetIndex();
                        return i >= 0 && i < c.Count ? c[i] : string.Empty;
                    }
                    case SettingKind.Info:
                        return InfoText?.Invoke() ?? string.Empty;
                    default:
                        return string.Empty;
                }
            }
        }
    }

    /// <summary>
    /// FG15-UX-01（FGR-SYS-020 设置全集 / FGR-SYS-021 行为）：全部设置项的唯一登记表。
    /// - 画面 / 音频 / 控制 / 游戏性 / 难度（游戏中可改，随存档）/ 可访问性（FG13 第 10 节的已有项；其余由 FG15-UX-02 补进本分类）；
    /// - 每项立即生效、写本机设置（难度类写存档）；每个分类有“恢复默认”（<see cref="ResetCategory"/>）；
    /// - 控制分类里“按键设置…”打开全部动作的重绑面板（动作清单按 fg.TbInputAction 动态生成，以后加动作不用改这里）。
    /// 纯数据层，界面只是薄壳：UI Toolkit 设置面板与主菜单 uGUI 设置页读写同一批 <see cref="SettingItem"/>。
    /// </summary>
    public static class SettingsCatalog
    {
        public static readonly SettingsCategory[] Categories =
        {
            SettingsCategory.Display, SettingsCategory.Audio, SettingsCategory.Controls,
            SettingsCategory.Gameplay, SettingsCategory.Difficulty, SettingsCategory.Accessibility,
        };

        public static string CategoryKey(SettingsCategory c) => "settings.category." + c.ToString().ToLowerInvariant();

        private static List<SettingItem> _all;

        /// <summary>界面层注入：打开按键面板 / 难度面板 / 反应记录（数据层不直接依赖面板类型）。</summary>
        public static Action OpenKeyBindings;
        public static Action OpenDifficulty;
        public static Action OpenReactionLog;
        /// <summary>FGR-SYS-020 游戏性“自动暂停规则”（FGR-UX-021）：打开通知中心的“按通知类型自动暂停”设置区（由设置面板接线）。</summary>
        public static Action OpenAutoPauseRules;

        public static IReadOnlyList<SettingItem> All
        {
            get
            {
                if (_all == null)
                {
                    _all = Build();
                }
                return _all;
            }
        }

        public static SettingItem Find(string id)
        {
            foreach (SettingItem i in All)
            {
                if (i.Id == id)
                {
                    return i;
                }
            }
            return null;
        }

        public static void Items(SettingsCategory c, List<SettingItem> into)
        {
            into.Clear();
            foreach (SettingItem i in All)
            {
                if (i.Category == c)
                {
                    into.Add(i);
                }
            }
        }

        public static string Percent(float v) => Mathf.RoundToInt(v * 100f).ToString(CultureInfo.InvariantCulture) + "%";
        private static string Times(float v) => v.ToString("0.00", CultureInfo.InvariantCulture) + "x";

        private static bool InCampaign() => CampaignSession.Current != null;

        // ── 画面：分辨率 / 窗口模式（走 15 秒确认）──
        private static List<Vector2Int> _resolutions;

        public static List<Vector2Int> Resolutions()
        {
            _resolutions = DisplayModeService.Backend.Supported();
            return _resolutions;
        }

        private static List<string> ResolutionChoices()
        {
            var list = new List<string>();
            foreach (Vector2Int r in Resolutions())
            {
                list.Add(r.x.ToString(CultureInfo.InvariantCulture) + " × " + r.y.ToString(CultureInfo.InvariantCulture));
            }
            return list;
        }

        private static int ResolutionIndex()
        {
            List<Vector2Int> list = _resolutions ?? Resolutions();
            return list.IndexOf(new Vector2Int(DisplayModeService.ShownWidth, DisplayModeService.ShownHeight));
        }

        private static List<string> Names(params string[] keys)
        {
            var list = new List<string>(keys.Length);
            foreach (string k in keys)
            {
                list.Add(GameText.Get(k));
            }
            return list;
        }

        private static List<string> FpsChoices()
        {
            var list = new List<string>();
            foreach (int f in FramePacing.Choices)
            {
                list.Add(f < 0 ? GameText.Get("settings.display.fps_unlimited") : f.ToString(CultureInfo.InvariantCulture) + " FPS");
            }
            return list;
        }

        private static List<string> AutoSaveNames()
        {
            var list = new List<string>();
            foreach (int m in GameSettings.AutoSaveChoices)
            {
                list.Add(m == 0 ? GameText.Get("settings.gameplay.autosave_off") : GameText.Format("settings.gameplay.autosave_minutes", m));
            }
            return list;
        }

        private static List<SettingItem> Build()
        {
            var l = new List<SettingItem>();
            // ── 画面 ──
            l.Add(new SettingItem
            {
                Id = "display.resolution", Category = SettingsCategory.Display, Kind = SettingKind.Choice, NameKey = "settings.display.resolution",
                Choices = ResolutionChoices, GetIndex = ResolutionIndex,
                SetIndex = i =>
                {
                    List<Vector2Int> r = _resolutions ?? Resolutions();
                    if (i >= 0 && i < r.Count)
                    {
                        DisplayModeService.Request(r[i].x, r[i].y, DisplayModeService.ShownMode);
                    }
                },
            });
            l.Add(new SettingItem
            {
                Id = "display.window_mode", Category = SettingsCategory.Display, Kind = SettingKind.Choice, NameKey = "settings.display.window_mode",
                Choices = () => Names("settings.display.mode_fullscreen", "settings.display.mode_borderless", "settings.display.mode_windowed"),
                GetIndex = () => Array.IndexOf(DisplayModeService.Modes, DisplayModeService.ShownMode),
                SetIndex = i =>
                {
                    if (i >= 0 && i < DisplayModeService.Modes.Length)
                    {
                        DisplayModeService.Request(DisplayModeService.ShownWidth, DisplayModeService.ShownHeight, DisplayModeService.Modes[i]);
                    }
                },
            });
            l.Add(Toggle("display.vsync", SettingsCategory.Display, "settings.display.vsync", () => GameSettings.VSyncEnabled, GameSettings.SetVSyncEnabled));
            l.Add(new SettingItem
            {
                Id = "display.fps_cap", Category = SettingsCategory.Display, Kind = SettingKind.Choice, NameKey = "settings.display.fps_cap",
                Choices = FpsChoices, GetIndex = () => Array.IndexOf(FramePacing.Choices, FramePacing.TargetFps),
                SetIndex = i =>
                {
                    if (i >= 0 && i < FramePacing.Choices.Length)
                    {
                        FramePacing.Set(FramePacing.Choices[i]);
                    }
                },
            });
            l.Add(new SettingItem
            {
                Id = "display.quality", Category = SettingsCategory.Display, Kind = SettingKind.Choice, NameKey = "settings.display.quality",
                Choices = () => Names("settings.display.quality_low", "settings.display.quality_medium", "settings.display.quality_high", "settings.display.quality_custom"),
                GetIndex = () => GameSettings.QualityPreset, SetIndex = GameSettings.SetQualityPreset,
            });
            l.Add(new SettingItem
            {
                Id = "display.shadows", Category = SettingsCategory.Display, Kind = SettingKind.Choice, NameKey = "settings.display.shadows",
                Choices = () => Names("settings.display.shadows_off", "settings.display.shadows_hard", "settings.display.shadows_all"),
                GetIndex = () => GameSettings.ShadowQuality, SetIndex = GameSettings.SetShadowQuality,
            });
            l.Add(Slider("display.effects_density", SettingsCategory.Display, "settings.display.effects_density", () => GameSettings.EffectsDensity, GameSettings.SetEffectsDensity,
                GameSettings.EffectsDensityMin, 1f, Percent));
            l.Add(Toggle("display.post_process", SettingsCategory.Display, "settings.display.post_process", () => GameSettings.PostProcessEnabled, GameSettings.SetPostProcessEnabled));
            l.Add(Slider("display.brightness", SettingsCategory.Display, "settings.display.brightness", () => GameSettings.Brightness, GameSettings.SetBrightness,
                GameSettings.BrightnessMin, GameSettings.BrightnessMax, Percent));
            l.Add(Slider("display.night_floor", SettingsCategory.Display, "settings.display.night_floor", () => GameSettings.NightBrightnessFloor, GameSettings.SetNightBrightnessFloor,
                GameSettings.NightBrightnessMin, GameSettings.NightBrightnessMax, Percent));
            l.Add(Slider("display.silent_dim", SettingsCategory.Display, "settings.display.silent_dim", () => GameSettings.SilentNightDim, GameSettings.SetSilentNightDim,
                GameSettings.SilentNightDimMin, GameSettings.SilentNightDimMax, Percent));
            l.Add(Slider("display.weather_fx", SettingsCategory.Display, "settings.display.weather_fx", () => GameSettings.WeatherFx, GameSettings.SetWeatherFx,
                GameSettings.WeatherFxMin, GameSettings.WeatherFxMax, Percent));

            // ── 音频 ──
            l.Add(Slider("audio.master", SettingsCategory.Audio, "settings.audio.master", () => GameSettings.MasterVolume, GameSettings.SetMasterVolume, 0f, 1f, Percent));
            l.Add(Slider("audio.music", SettingsCategory.Audio, "settings.audio.music", () => GameSettings.MusicVolume, GameSettings.SetMusicVolume, 0f, 1f, Percent));
            l.Add(Slider("audio.sfx", SettingsCategory.Audio, "settings.audio.sfx", () => GameSettings.SfxVolume, GameSettings.SetSfxVolume, 0f, 1f, Percent));
            l.Add(Slider("audio.ui", SettingsCategory.Audio, "settings.audio.ui", () => GameSettings.UiVolume, GameSettings.SetUiVolume, 0f, 1f, Percent));
            l.Add(Slider("audio.ambient", SettingsCategory.Audio, "settings.audio.ambient", () => GameSettings.AmbientVolume, GameSettings.SetAmbientVolume, 0f, 1f, Percent));
            l.Add(Toggle("audio.mute_unfocused", SettingsCategory.Audio, "settings.audio.mute_unfocused", () => GameSettings.MuteOnFocusLoss, GameSettings.SetMuteOnFocusLoss));

            // ── 控制 ──
            l.Add(new SettingItem
            {
                Id = "controls.keybindings", Category = SettingsCategory.Controls, Kind = SettingKind.Action, NameKey = "settings.controls.keybindings",
                Invoke = () => OpenKeyBindings?.Invoke(),
            });
            l.Add(Slider("controls.camera_speed", SettingsCategory.Controls, "settings.controls.camera_speed", () => GameSettings.CameraSpeedMultiplier, GameSettings.SetCameraSpeedMultiplier, 0.25f, 2.5f, Times));
            l.Add(Toggle("controls.edge_pan", SettingsCategory.Controls, "settings.controls.edge_pan", () => GameSettings.EdgePanEnabled, GameSettings.SetEdgePanEnabled));
            l.Add(Slider("controls.edge_pan_speed", SettingsCategory.Controls, "settings.controls.edge_pan_speed", () => GameSettings.EdgePanSpeedMultiplier, GameSettings.SetEdgePanSpeedMultiplier, 0.25f, 2.5f, Times));
            l.Add(Slider("controls.zoom_speed", SettingsCategory.Controls, "settings.controls.zoom_speed", () => GameSettings.ZoomSpeedMultiplier, GameSettings.SetZoomSpeedMultiplier, 0.25f, 2.5f, Times));
            l.Add(Toggle("controls.invert_scroll", SettingsCategory.Controls, "settings.controls.invert_scroll", () => GameSettings.InvertScroll, GameSettings.SetInvertScroll));
            l.Add(Slider("controls.uplink_mouse", SettingsCategory.Controls, "settings.controls.uplink_mouse", () => GameSettings.UplinkMouseSensitivity, GameSettings.SetUplinkMouseSensitivity, 0f, 2f, Times));
            l.Add(Slider("controls.uplink_zoom", SettingsCategory.Controls, "settings.controls.uplink_zoom", () => GameSettings.UplinkCameraZoom, GameSettings.SetUplinkCameraZoom,
                GameSettings.UplinkCameraZoomMin, GameSettings.UplinkCameraZoomMax, Times));
            l.Add(Slider("controls.uplink_follow", SettingsCategory.Controls, "settings.controls.uplink_follow", () => GameSettings.UplinkFollowStrength, GameSettings.SetUplinkFollowStrength,
                GameSettings.UplinkFollowMin, GameSettings.UplinkFollowMax, Times));
            l.Add(Toggle("controls.hold_toggle", SettingsCategory.Controls, "settings.controls.hold_toggle", () => GameSettings.HoldActionsToggle, v =>
            {
                GameSettings.SetHoldActionsToggle(v);
                Core.InputRouter.ClearHoldLatches();
            }));

            // ── 游戏性 ──
            l.Add(Toggle("gameplay.pause_unfocused", SettingsCategory.Gameplay, "settings.gameplay.pause_unfocused", () => GameSettings.PauseOnFocusLoss, GameSettings.SetPauseOnFocusLoss));
            l.Add(new SettingItem
            {
                Id = "gameplay.autosave", Category = SettingsCategory.Gameplay, Kind = SettingKind.Choice, NameKey = "settings.gameplay.autosave",
                Choices = AutoSaveNames, GetIndex = () => Array.IndexOf(GameSettings.AutoSaveChoices, GameSettings.AutoSaveMinutes),
                SetIndex = i =>
                {
                    if (i >= 0 && i < GameSettings.AutoSaveChoices.Length)
                    {
                        GameSettings.SetAutoSaveMinutes(GameSettings.AutoSaveChoices[i]);
                    }
                },
            });
            l.Add(Toggle("gameplay.away_report", SettingsCategory.Gameplay, "settings.gameplay.away_report", () => GameSettings.AwayReportAutoOpen, GameSettings.SetAwayReportAutoOpen));
            l.Add(Toggle("gameplay.guidance", SettingsCategory.Gameplay, "settings.gameplay.guidance", () => GameSettings.GuidanceEnabled, GameSettings.SetGuidanceEnabled));
            l.Add(Toggle("gameplay.home_guide", SettingsCategory.Gameplay, "settings.gameplay.home_guide", () => GameSettings.HomeGuideQuestsEnabled, GameSettings.SetHomeGuideQuestsEnabled));
            l.Add(Toggle("gameplay.reaction_popups", SettingsCategory.Gameplay, "settings.gameplay.reaction_popups", () => GameSettings.ReactionPopupsEnabled, GameSettings.SetReactionPopupsEnabled));
            l.Add(Toggle("gameplay.reaction_slowmo", SettingsCategory.Gameplay, "settings.gameplay.reaction_slowmo", () => GameSettings.ReactionSlowMotionEnabled, GameSettings.SetReactionSlowMotionEnabled));
            l.Add(Toggle("gameplay.reaction_nudge", SettingsCategory.Gameplay, "settings.gameplay.reaction_nudge", () => GameSettings.ReactionCameraNudgeEnabled, GameSettings.SetReactionCameraNudgeEnabled));
            l.Add(new SettingItem
            {
                Id = "gameplay.reaction_log", Category = SettingsCategory.Gameplay, Kind = SettingKind.Action, NameKey = "settings.gameplay.reaction_log",
                Invoke = () => OpenReactionLog?.Invoke(), Available = InCampaign, UnavailableKey = "settings.need_campaign",
            });
            l.Add(new SettingItem
            {
                Id = "gameplay.auto_pause_rules", Category = SettingsCategory.Gameplay, Kind = SettingKind.Action, NameKey = "settings.gameplay.auto_pause_rules",
                Invoke = () => OpenAutoPauseRules?.Invoke(), Available = InCampaign, UnavailableKey = "settings.need_campaign",
            });
            l.Add(Toggle("gameplay.causal_chain", SettingsCategory.Gameplay, "settings.gameplay.causal_chain", () => GameSettings.CausalChainOverlayEnabled, GameSettings.SetCausalChainOverlayEnabled));
            l.Add(Toggle("gameplay.damage_numbers", SettingsCategory.Gameplay, "settings.gameplay.damage_numbers", () => GameSettings.DamageNumbersEnabled, GameSettings.SetDamageNumbersEnabled));
            l.Add(new SettingItem
            {
                Id = "gameplay.language", Category = SettingsCategory.Gameplay, Kind = SettingKind.Choice, NameKey = "settings.gameplay.language",
                Choices = () => new List<string> { "简体中文", "English" },
                GetIndex = () => (int)GameSettings.Language,
                SetIndex = i => GameSettings.SetLanguage(i == 1 ? GameLanguage.En : GameLanguage.ZhCn),
            });

            // ── 难度（游戏中可改，每次修改记入存档）──
            l.Add(new SettingItem
            {
                Id = "difficulty.panel", Category = SettingsCategory.Difficulty, Kind = SettingKind.Action, NameKey = "settings.difficulty.open",
                Invoke = () => OpenDifficulty?.Invoke(), Available = InCampaign, UnavailableKey = "settings.need_campaign",
            });
            l.Add(new SettingItem
            {
                Id = "difficulty.raid", Category = SettingsCategory.Difficulty, Kind = SettingKind.Info, NameKey = "settings.difficulty.raid",
                InfoText = () => InCampaign() ? Campaign.Defense.DifficultyService.Label(CampaignSession.Current) : GameText.Get("settings.need_campaign"),
            });
            l.Add(new SettingItem
            {
                Id = "difficulty.silent_night", Category = SettingsCategory.Difficulty, Kind = SettingKind.Info, NameKey = "settings.difficulty.silent_night",
                InfoText = () => InCampaign()
                    ? GameText.Format("settings.difficulty.silent_night_value", Campaign.Env.SilentNightService.IntervalDays(CampaignSession.Current))
                    : GameText.Get("settings.need_campaign"),
            });
            l.Add(new SettingItem
            {
                Id = "difficulty.events", Category = SettingsCategory.Difficulty, Kind = SettingKind.Choice, NameKey = "settings.difficulty.events",
                Choices = () =>
                {
                    var c = new List<string>();
                    for (int i = 0; i <= Campaign.Env.EventDirectorService.FrequencyHigh; i++)
                    {
                        c.Add(Campaign.Env.EventDirectorService.FrequencyName(i));
                    }
                    return c;
                },
                GetIndex = () => InCampaign() ? Campaign.Env.EventDirectorService.Frequency(CampaignSession.Current) : Campaign.Env.EventDirectorService.FrequencyStandard,
                SetIndex = SetEventFrequency, Available = InCampaign, UnavailableKey = "settings.need_campaign",
            });

            // ── 可访问性（FG13 第 10 节；已有项，其余由 FG15-UX-02 补进本分类）──
            l.Add(Slider("access.ui_scale", SettingsCategory.Accessibility, "settings.access.ui_scale", () => GameSettings.UiScale, GameSettings.SetUiScale,
                GameSettings.UiScaleMin, GameSettings.UiScaleMax, Percent));
            l.Add(Slider("access.toast", SettingsCategory.Accessibility, "settings.access.toast", () => GameSettings.NotificationToastScale, GameSettings.SetNotificationToastScale,
                0.5f, 3f, Times));
            l.Add(Toggle("access.subtitles", SettingsCategory.Accessibility, "settings.access.subtitles", () => GameSettings.SubtitlesEnabled, GameSettings.SetSubtitlesEnabled));
            l.Add(Slider("access.text_speed", SettingsCategory.Accessibility, "settings.access.text_speed", () => GameSettings.NarrativeTextSpeed, GameSettings.SetNarrativeTextSpeed,
                GameSettings.NarrativeTextSpeedMin, GameSettings.NarrativeTextSpeedMax, v => Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture)));
            l.Add(Slider("access.subtitle_scale", SettingsCategory.Accessibility, "settings.access.subtitle_scale", () => GameSettings.SubtitleScale, GameSettings.SetSubtitleScale,
                GameSettings.SubtitleScaleMin, GameSettings.SubtitleScaleMax, Percent));
            l.Add(Toggle("access.flash", SettingsCategory.Accessibility, "settings.access.flash", () => GameSettings.FlashReductionEnabled, GameSettings.SetFlashReductionEnabled));
            l.Add(Toggle("access.shake", SettingsCategory.Accessibility, "settings.access.shake", () => GameSettings.ScreenShakeEnabled, GameSettings.SetScreenShakeEnabled));
            l.Add(Toggle("access.colorblind_icons", SettingsCategory.Accessibility, "settings.access.colorblind_icons", () => GameSettings.ColorblindSafeIconsEnabled, GameSettings.SetColorblindSafeIconsEnabled));
            return l;
        }

        private static void SetEventFrequency(int level)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                return;
            }
            if (SandboxService.IsSandbox(s))
            {
                SandboxService.TryChange(s, SandboxService.Events, level); // 沙盒里改事件频率同样记进沙盒修改记录（同暂停菜单）。
            }
            else
            {
                Campaign.Env.EventDirectorService.SetFrequency(s, level);
            }
        }

        private static SettingItem Toggle(string id, SettingsCategory c, string nameKey, Func<bool> get, Action<bool> set) =>
            new SettingItem { Id = id, Category = c, Kind = SettingKind.Toggle, NameKey = nameKey, GetBool = get, SetBool = set };

        private static SettingItem Slider(string id, SettingsCategory c, string nameKey, Func<float> get, Action<float> set, float min, float max, Func<float, string> format) =>
            new SettingItem { Id = id, Category = c, Kind = SettingKind.Slider, NameKey = nameKey, GetFloat = get, SetFloat = set, Min = min, Max = max, Format = format };

        /// <summary>FGR-SYS-021“每个分类都有恢复默认”。可逆、无代价（再改回来即可）→ 不弹确认（FGR-UX-002）；按键的“全部恢复默认”在按键面板里（会丢改键，二次确认）。
        /// 画面分类里的分辨率 / 窗口模式恢复到“全屏窗口 + 显示器最大分辨率”，同样走 15 秒确认。语言不在恢复范围（避免把玩家切到看不懂的语言）。
        /// 难度分类：事件频率回到“标准”，记入存档（难度预设请在难度面板里选，那里写清后果）。</summary>
        public static void ResetCategory(SettingsCategory c)
        {
            switch (c)
            {
                case SettingsCategory.Display:
                    GameSettings.ResetFields("VSyncEnabled", "Brightness", "QualityPreset", "ShadowQuality", "EffectsDensity", "PostProcessEnabled",
                        "NightBrightnessFloor", "SilentNightDim", "WeatherFx");
                    FramePacing.ResetToDefault();
                    List<Vector2Int> r = Resolutions();
                    Vector2Int max = r[r.Count - 1];
                    DisplayModeService.Request(max.x, max.y, FullScreenMode.FullScreenWindow);
                    break;
                case SettingsCategory.Audio:
                    GameSettings.ResetFields("MasterVolume", "MusicVolume", "SfxVolume", "UiVolume", "AmbientVolume", "MuteOnFocusLoss");
                    break;
                case SettingsCategory.Controls:
                    GameSettings.ResetFields("CameraSpeedMultiplier", "EdgePanEnabled", "EdgePanSpeedMultiplier", "ZoomSpeedMultiplier", "InvertScroll",
                        "UplinkMouseSensitivity", "UplinkCameraZoom", "UplinkFollowStrength", "HoldActionsToggle");
                    Core.InputRouter.ClearHoldLatches();
                    break;
                case SettingsCategory.Gameplay:
                    GameSettings.ResetFields("PauseOnFocusLoss", "AutoSaveMinutes", "AwayReportAutoOpen", "GuidanceEnabled", "HomeGuideQuestsEnabled",
                        "ReactionPopupsEnabled", "ReactionSlowMotionEnabled", "ReactionCameraNudgeEnabled", "CausalChainOverlayEnabled", "DamageNumbersEnabled");
                    GameSettings.ResetNotifyAutoPause();
                    Campaign.Feedback.DamageNumbers.Clear();
                    break;
                case SettingsCategory.Difficulty:
                    SetEventFrequency(Campaign.Env.EventDirectorService.FrequencyStandard);
                    break;
                case SettingsCategory.Accessibility:
                    GameSettings.ResetFields("UiScale", "NotificationToastScale", "SubtitlesEnabled", "NarrativeTextSpeed", "SubtitleScale",
                        "FlashReductionEnabled", "ScreenShakeEnabled", "ColorblindSafeIconsEnabled");
                    break;
            }
        }
    }
}
