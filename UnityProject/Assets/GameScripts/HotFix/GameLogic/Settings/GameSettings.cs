using System;
using GameLogic.Core;
using TEngine;
using UnityEngine;
using Log = TEngine.Log;

namespace GameLogic.Settings
{
    /// <summary>PlayerPrefs JSON 载荷。字段全公开、给默认值——旧存档缺字段时 JsonUtility 按
    /// 声明的默认值补齐，不会因为新增一项设置就让老玩家读档失败（同 CampaignState 的迁移纪律，
    /// 但这里没有 schemaVersion：设置是本机偏好，不是跨机器分发的战役存档，坏了大不了复位）。</summary>
    [Serializable]
    public sealed class GameSettingsData
    {
        public InputBindingEntry[] KeyBindings;

        /// <summary>FG0-UX-01：<see cref="KeyBindings"/> 的格式版本。初值必须是 0——旧 JSON 没有这个字段时
        /// JsonUtility 保留初值，0 正好表示“ER2 全表快照格式”，读入时迁移；保存时写
        /// <see cref="InputBindingSet.CurrentFormatVersion"/>。</summary>
        public int KeyBindingsFormat;

        public float UiScale = 1f; // FG0-UX-01：范围 80%～150%（fg.TbUiTuning ui.scale_min / ui.scale_max，FGR-UX-060）。
        public bool EdgePanEnabled = true;
        public float EdgePanSpeedMultiplier = 1f;
        public float CameraSpeedMultiplier = 1f;
        /// <summary>FG1-HUD-01（FG01 第 4 章“接入时镜头的缩放和跟随力度可以在设置里调”）：接入时镜头距离倍率（乘在接入视野正交半高上；
        /// 范围 fg.TbUiTuning camera.uplink_zoom_min / max；1 = 原手感）。旧设置 JSON 没有这个字段时按默认 1 补齐。</summary>
        public float UplinkCameraZoom = 1f;
        /// <summary>FG1-HUD-01：接入时镜头跟随力度倍率（乘在跟随平滑系数上；范围 camera.uplink_follow_min / max；1 = 原手感）。</summary>
        public float UplinkFollowStrength = 1f;

        public bool ScreenShakeEnabled = true;
        public bool FlashReductionEnabled = false; // AC-ACC-003：降低闪光。

        /// <summary>FG2-FW-04（FGR-FW-043 / FGR-SYS-020“游戏性：反应弹字、慢放、镜头推动”）：三个开关，默认开。
        /// 关掉弹字：反应名不再打在目标头上（字幕 / 日志 / 统计照常）；关掉慢放 / 镜头推动：首次触发仍解锁图鉴、记首次触发，只是不慢放 / 不推镜头。</summary>
        public bool ReactionPopupsEnabled = true;
        public bool ReactionSlowMotionEnabled = true;
        public bool ReactionCameraNudgeEnabled = true;

        public float MasterVolume = 1f;
        public float MusicVolume = 1f;
        public float SfxVolume = 1f;
        public float UiVolume = 1f;

        public bool SubtitlesEnabled = true;
        public bool ColorblindSafeIconsEnabled = false; // AC-ACC-002。

        /// <summary>FG3-LOG-01：建造模式叠加层的格线开关（默认开；旧设置 JSON 没有这个字段时按默认补齐）。</summary>
        public bool BuildGridLinesEnabled = true;

        /// <summary>FG4-ECO-09（FGR-ECO-060“远征回来时自动打开（可以在设置里关闭）”）：远征回来时自动打开离家报告（默认开；旧设置 JSON 没有这个字段时按默认补齐）。</summary>
        public bool AwayReportAutoOpen = true;

        /// <summary>FG8-QST-01（FGR-QST-001“家园指引……可以在设置里关闭”）：家园指引任务（默认开；旧设置 JSON 没有这个字段时按默认补齐）。</summary>
        public bool HomeGuideQuestsEnabled = true;

        /// <summary>FG7-ENV-01（FG07 FGR-ENV-003“设置里提供夜间亮度下限，保证可读性”）：夜里画面最暗是白天的多少（0～1，范围 fg.TbUiTuning env.night_brightness_min / max）。
        /// 初值 -1 = 没设过，按表里 env.night_brightness_default（旧设置 JSON 没有这个字段时同样按默认）。</summary>
        public float NightBrightnessFloor = -1f;
        /// <summary>FG7-ENV-02（FG07 第 4 节“静默夜画面压暗强度”）：静默夜期间在昼夜亮度之上再暗多少（-1 = 跟随表里的默认值）。</summary>
        public float SilentNightDim = -1f;
        /// <summary>FG7-ENV-03（FG07 第 4 节“设置：天气视觉效果强度（沙暴可能影响可读性）”）：雨 / 沙 / 雪粒子与沙暴雾的强度（-1 = 跟随表里的默认值）。</summary>
        public float WeatherFx = -1f;
        /// <summary>FG9-NAR-01（FG11 第 8 节 / FGR-UX-060“文本显示速度、字幕大小可以设置”）：演出字幕逐字出现的速度（每真实秒几个字；-1 = 跟随表里的默认值，
        /// 范围 fg.TbUiTuning narrative.text_speed_min / max，拉到最大 = 整行立即出现）。旧设置 JSON 没有这个字段时按默认补齐。</summary>
        public float NarrativeTextSpeed = -1f;
        /// <summary>FG9-NAR-01：字幕大小（乘在演出字幕与资料库正文的基准字号上；-1 = 默认，范围 narrative.subtitle_scale_min / max）。</summary>
        public float SubtitleScale = -1f;

        /// <summary>FG0-DATA-01（FGR-ARC-006）：界面语言代码，见 <see cref="GameLogic.Localization.GameLanguageCodes"/>。
        /// 存语言代码而不是枚举整数——以后加语言、调整枚举顺序都不会把老玩家的设置读成别的语言；
        /// 读不懂的代码回落简体中文（<see cref="GameSettings.Language"/>）。旧设置 JSON 没有这个字段时按默认值补齐。</summary>
        public string Language = GameLogic.Localization.GameLanguageCodes.ZhCn;

        /// <summary>FG0-UX-01（FGR-UX-060）：通知弹出提示停留时长倍率（乘在各等级的默认秒数上）。</summary>
        public float NotificationToastScale = 1f;

        /// <summary>FG0-UX-01（FGR-UX-021）：玩家改过的“触发时自动暂停”勾选。没有条目的类型按表里的默认值。</summary>
        public NotifyAutoPauseEntry[] NotifyAutoPause;

        /// <summary>FG0-UX-01（FG00 B14）：已经触发过的引导钩子（每个钩子对每个玩家只触发一次）。</summary>
        public string[] SeenGuidanceHooks;

        // ── FG15-UX-01（FGR-SYS-020 设置全集）：新增项，旧设置 JSON 没有这些字段时按这里的初值补齐 ──
        /// <summary>画面：分辨率（0 = 没设过，沿用启动时的分辨率）。只在玩家点“保留”后写入（FGR-SYS-021 15 秒回退）。</summary>
        public int ResolutionWidth;
        public int ResolutionHeight;
        /// <summary>画面：窗口模式（-1 = 没设过；其余为 <see cref="FullScreenMode"/> 的整数值）。</summary>
        public int WindowMode = -1;
        /// <summary>画面：垂直同步（默认关：开发标准最低 120 帧，开着会锁在显示器刷新率）。</summary>
        public bool VSyncEnabled;
        /// <summary>画面：亮度倍率（乘在昼夜调光上；范围 fg.TbUiTuning settings.brightness_min / max）。</summary>
        public float Brightness = 1f;
        /// <summary>画面：画质预设（0 低 / 1 中 / 2 高 / 3 自定义）；改了下面任一子项即变为自定义。</summary>
        public int QualityPreset = 2;
        /// <summary>画面：阴影（0 关 / 1 硬阴影 / 2 全部）。</summary>
        public int ShadowQuality = 2;
        /// <summary>画面：特效密度（反馈特效生成比例；范围 settings.effects_density_min～1）。</summary>
        public float EffectsDensity = 1f;
        /// <summary>画面：后处理开关。Built-in 管线下没有后处理栈，值经 <see cref="GameSettings.PostProcessSink"/> 交给渲染侧（FG15-PL-01 URP 合流后接上）。</summary>
        public bool PostProcessEnabled = true;
        /// <summary>音频：环境音量（混音分类“环境”；没有单独分类时乘在音效上播放环境声）。</summary>
        public float AmbientVolume = 1f;
        /// <summary>音频：窗口失焦时静音。</summary>
        public bool MuteOnFocusLoss;
        /// <summary>游戏性（FG00 B09 / FG-GAP-017）：窗口失焦时暂停世界（默认继续运行，同 Demo 的 runInBackground）。</summary>
        public bool PauseOnFocusLoss;
        /// <summary>控制：缩放速度倍率（乘在镜头缩放量上）。</summary>
        public float ZoomSpeedMultiplier = 1f;
        /// <summary>控制：反转滚轮（滚轮缩放方向反过来）。</summary>
        public bool InvertScroll;
        /// <summary>控制：接入时的鼠标灵敏度（接入镜头朝光标方向前探的力度倍率）。</summary>
        public float UplinkMouseSensitivity = 1f;
        /// <summary>控制（FGR-UX-060“所有按住操作可以改为切换”）：按住类的界面动作（固定悬停提示）改为按一下切换。镜头平移 / 机器移动不受影响。</summary>
        public bool HoldActionsToggle;
        /// <summary>游戏性：自动存档间隔（分钟，真实时间、世界运行时累计；0 = 关闭）。</summary>
        public int AutoSaveMinutes = 10;
        /// <summary>游戏性：引导开关（关掉后引导钩子不再触发；内容在 FG15-UX-04）。</summary>
        public bool GuidanceEnabled = true;
        /// <summary>游戏性（FGR-EMG-181）：因果链叠加显示开关。</summary>
        public bool CausalChainOverlayEnabled = true;
        /// <summary>游戏性：伤害数字（被观察地点里机器 / 敌人受伤时在头上飘数字；默认关）。</summary>
        public bool DamageNumbersEnabled;

        // ── FG15-UX-02（FGR-UX-060 可访问性全集）：旧设置 JSON 没有这些字段时按这里的初值补齐 ──
        /// <summary>色盲模式（<see cref="ColorblindMode"/> 的整数值）；-1 = 没设过：按旧布尔 <see cref="ColorblindSafeIconsEnabled"/> 迁移（开 = 红绿）。</summary>
        public int ColorblindMode = -1;
        /// <summary>高对比度：界面面板不透明深底 + 亮边框 + 纯白文字。</summary>
        public bool HighContrastEnabled;
        /// <summary>文字大小倍率（与 UI 缩放独立；范围 fg.TbUiTuning ui.text_scale_min / max）。</summary>
        public float TextScale = 1f;
        /// <summary>键盘指针（小键盘移动虚拟指针、5 = 左键、0 = 右键；只用键盘也能悬停 / 点选 / 拉框）。</summary>
        public bool KeyboardPointerEnabled;
    }

    /// <summary>FG15-UX-02（FGR-UX-060）：色盲模式。红绿 = 红色盲 / 绿色盲（最常见），蓝黄 = 蓝色盲，全色盲 = 只看明暗。</summary>
    public enum ColorblindMode
    {
        Off = 0,
        RedGreen = 1,
        BlueYellow = 2,
        Achromat = 3,
    }

    /// <summary>一条“某类通知触发时是否自动暂停”的玩家设置。</summary>
    [Serializable]
    public struct NotifyAutoPauseEntry
    {
        public string TypeId;
        public bool Enabled;
    }

    /// <summary>
    /// ER2-INPUT-01：跨战役持久化的玩家偏好设置（AC-ACC-001~003、AC-UI-004/005 的数据层）。
    ///
    /// 刻意独立于 <see cref="GameLogic.Campaign.CampaignState"/>：设置是本机玩家偏好，不属于任何一局
    /// 战役存档，切换/删除战役存档不得影响它，这也是"设置跨战役保存"这条验收成立的结构性原因。
    /// 走 PlayerPrefs——仓库里 GameShellUIToolkit/CellDebugHud 等已用它存本机小型偏好，同一纪律。
    ///
    /// 每次修改即写盘（设置项改动频率低，不必攒批），并广播 <see cref="ISettingsEvent"/>
    /// 供订阅方即时预览。
    /// </summary>
    public static class GameSettings
    {
        private const string PrefsKey = "BinGames.GameSettings.v1";

        private static GameSettingsData _data;
        private static InputBindingSet _keyBindings;

        private static GameSettingsData Data
        {
            get
            {
                EnsureLoaded();
                return _data;
            }
        }

        public static InputBindingSet KeyBindings
        {
            get
            {
                EnsureLoaded();
                return _keyBindings;
            }
        }

        /// <summary>每次读盘/改值 +1。消费方（音量桥 <c>FeedbackCues.Tick</c> 等）比较版本号决定要不要
        /// 重新应用，不必订阅事件，也不会漏掉“读盘后第一次应用”。</summary>
        public static int Revision { get; private set; }

        public static float UiScale => Data.UiScale;
        public static bool EdgePanEnabled => Data.EdgePanEnabled;
        public static float EdgePanSpeedMultiplier => Data.EdgePanSpeedMultiplier;
        public static float CameraSpeedMultiplier => Data.CameraSpeedMultiplier;
        public static float UplinkCameraZoom => Data.UplinkCameraZoom > 0f ? Data.UplinkCameraZoom : 1f;
        public static float UplinkFollowStrength => Data.UplinkFollowStrength > 0f ? Data.UplinkFollowStrength : 1f;
        public static bool ScreenShakeEnabled => Data.ScreenShakeEnabled;
        public static bool ReactionPopupsEnabled => Data.ReactionPopupsEnabled;
        public static bool ReactionSlowMotionEnabled => Data.ReactionSlowMotionEnabled;
        public static bool ReactionCameraNudgeEnabled => Data.ReactionCameraNudgeEnabled;
        public static bool FlashReductionEnabled => Data.FlashReductionEnabled;
        public static float MasterVolume => Data.MasterVolume;
        public static float MusicVolume => Data.MusicVolume;
        public static float SfxVolume => Data.SfxVolume;
        public static float UiVolume => Data.UiVolume;
        public static bool SubtitlesEnabled => Data.SubtitlesEnabled;
        public static bool ColorblindSafeIconsEnabled => ColorblindMode != ColorblindMode.Off;
        /// <summary>FG15-UX-02：当前色盲模式（没设过时由旧布尔迁移：开 = 红绿）。</summary>
        public static ColorblindMode ColorblindMode => Data.ColorblindMode >= 0
            ? (ColorblindMode)Mathf.Clamp(Data.ColorblindMode, 0, 3)
            : Data.ColorblindSafeIconsEnabled ? ColorblindMode.RedGreen : ColorblindMode.Off;
        public static bool HighContrastEnabled => Data.HighContrastEnabled;
        public static float TextScaleMin => UiTuningValues.Get("ui.text_scale_min");
        public static float TextScaleMax => UiTuningValues.Get("ui.text_scale_max");
        public static float TextScale => Mathf.Clamp(Data.TextScale > 0f ? Data.TextScale : 1f, TextScaleMin, TextScaleMax);
        public static float NotificationToastScale => Data.NotificationToastScale;

        /// <summary>FG0-UX-01：读旧设置时的按键迁移结果（只在本次进程读盘时有值）。通知中心取走一次后清空，
        /// 用来告诉玩家“按键方案已更新，保留了几个、恢复了几个”。</summary>
        public static InputBindingSet.LegacyMigrationReport PendingKeyMigration { get; private set; }

        public static void ClearPendingKeyMigration() => PendingKeyMigration = default;

        /// <summary>FG0-DATA-01：当前界面语言。<see cref="GameLogic.Localization.GameText"/> 每次取文本都读这里，
        /// 所以切换后下一次刷新界面就是新语言，不需要重载表。</summary>
        public static GameLogic.Localization.GameLanguage Language =>
            GameLogic.Localization.GameLanguageCodes.Parse(Data.Language);

        private static void EnsureLoaded()
        {
            if (_data != null)
            {
                return;
            }
            Load();
        }

        public static void Load()
        {
            string json = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                _data = new GameSettingsData();
            }
            else
            {
                try
                {
                    _data = JsonUtility.FromJson<GameSettingsData>(json) ?? new GameSettingsData();
                }
                catch (Exception ex)
                {
                    Log.Warning($"[GameSettings] 设置 JSON 解析失败，回退默认值：{ex.Message}");
                    _data = new GameSettingsData();
                }
            }
            _keyBindings = InputBindingSet.FromEntries(_data.KeyBindings, _data.KeyBindingsFormat,
                out InputBindingSet.LegacyMigrationReport report);
            PendingKeyMigration = report;
            if (report.Migrated || report.DroppedCount > 0)
            {
                // 迁移过的格式立刻写回，下次读盘不再迁移（迁移只发生一次，通知也只发一次）。
                Log.Info($"[GameSettings] 按键设置已迁移到格式 {InputBindingSet.CurrentFormatVersion}：保留 {report.KeptCount}，恢复默认 {report.DroppedCount}");
                Save();
            }
            Revision++;
        }

        /// <summary>落盘并广播 <see cref="ISettingsEvent.OnSettingsChanged"/>。所有 Set* 方法与
        /// <see cref="TryRebindKey"/>/<see cref="ResetKeyBindingsToDefault"/> 最终都走这里，
        /// 保证"改值即预览"不会有遗漏路径。</summary>
        private static void Save()
        {
            _data.KeyBindings = _keyBindings.ToEntries();
            _data.KeyBindingsFormat = InputBindingSet.CurrentFormatVersion;
            string json = JsonUtility.ToJson(_data);
            PlayerPrefs.SetString(PrefsKey, json);
            PlayerPrefs.Save();
            Revision++;

            // GameEventHelper.Init() 正常在 GameApp.Entrance() 最先调用，玩法期间广播必然安全；
            // 这里仍判空——本类是纯数据层，允许编辑器工具/单元测试在完整游戏启动之前调用
            // Set*/ResetAllToDefault（不应该因为事件系统还没初始化就崩），广播只是"锦上添花"的
            // 即时预览，跳过它不影响设置本身已经落盘这个事实。
            ISettingsEvent listener = GameEvent.Get<ISettingsEvent>();
            listener?.OnSettingsChanged();
        }

        /// <summary>UI 缩放的允许范围（fg.TbUiTuning，FGR-UX-060：80%～150%）。</summary>
        public static float UiScaleMin => UiTuningValues.Get("ui.scale_min");
        public static float UiScaleMax => UiTuningValues.Get("ui.scale_max");

        public static void SetUiScale(float value)
        {
            Data.UiScale = Mathf.Clamp(value, UiScaleMin, UiScaleMax);
            Save();
        }

        /// <summary>FG0-UX-01（FGR-UX-060）：通知停留时长倍率，范围取自 fg.TbUiTuning。</summary>
        public static void SetNotificationToastScale(float value)
        {
            Data.NotificationToastScale = Mathf.Clamp(value, UiTuningValues.Get("notify.toast_scale_min"),
                UiTuningValues.Get("notify.toast_scale_max"));
            Save();
        }

        /// <summary>某类通知是否触发自动暂停：玩家改过的优先，否则用表里的默认值。</summary>
        public static bool IsNotifyAutoPauseEnabled(string typeId, bool tableDefault)
        {
            NotifyAutoPauseEntry[] entries = Data.NotifyAutoPause;
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i].TypeId == typeId)
                    {
                        return entries[i].Enabled;
                    }
                }
            }
            return tableDefault;
        }

        /// <summary>FG6-DEF-06：玩家改过这一类的“触发时自动暂停”吗（没改过 = 按默认值，突袭到达的默认值随本存档的突袭次数变）。</summary>
        public static bool HasNotifyAutoPauseChoice(string typeId)
        {
            NotifyAutoPauseEntry[] entries = Data.NotifyAutoPause;
            return entries != null && Array.FindIndex(entries, e => e.TypeId == typeId) >= 0;
        }

        /// <summary>FG6-DEF-06（FGR-UX-021“设置里可以按通知类型勾选”）：清掉玩家改过的全部“触发时自动暂停”，回到默认（可逆：再勾一次即可，不弹确认，B04）。</summary>
        public static void ResetNotifyAutoPause()
        {
            if (Data.NotifyAutoPause == null || Data.NotifyAutoPause.Length == 0)
            {
                return;
            }
            Data.NotifyAutoPause = Array.Empty<NotifyAutoPauseEntry>();
            Save();
        }

        public static bool HasSeenGuidanceHook(string hookId) =>
            Data.SeenGuidanceHooks != null && Array.IndexOf(Data.SeenGuidanceHooks, hookId) >= 0;

        public static void MarkGuidanceHookSeen(string hookId)
        {
            if (string.IsNullOrEmpty(hookId) || HasSeenGuidanceHook(hookId))
            {
                return;
            }
            var list = new System.Collections.Generic.List<string>(Data.SeenGuidanceHooks ?? Array.Empty<string>()) { hookId };
            Data.SeenGuidanceHooks = list.ToArray();
            Save();
        }

        public static void SetNotifyAutoPause(string typeId, bool enabled)
        {
            if (string.IsNullOrEmpty(typeId))
            {
                return;
            }
            var list = new System.Collections.Generic.List<NotifyAutoPauseEntry>(Data.NotifyAutoPause ?? Array.Empty<NotifyAutoPauseEntry>());
            int index = list.FindIndex(e => e.TypeId == typeId);
            var entry = new NotifyAutoPauseEntry { TypeId = typeId, Enabled = enabled };
            if (index >= 0)
            {
                list[index] = entry;
            }
            else
            {
                list.Add(entry);
            }
            Data.NotifyAutoPause = list.ToArray();
            Save();
        }

        public static void SetEdgePanEnabled(bool enabled)
        {
            Data.EdgePanEnabled = enabled;
            Save();
        }

        public static void SetEdgePanSpeedMultiplier(float value)
        {
            Data.EdgePanSpeedMultiplier = Mathf.Clamp(value, 0.25f, 2.5f);
            Save();
        }

        public static void SetCameraSpeedMultiplier(float value)
        {
            Data.CameraSpeedMultiplier = Mathf.Clamp(value, 0.25f, 2.5f);
            Save();
        }

        public static float UplinkCameraZoomMin => UiTuningValues.Get("camera.uplink_zoom_min");
        public static float UplinkCameraZoomMax => UiTuningValues.Get("camera.uplink_zoom_max");
        public static float UplinkFollowMin => UiTuningValues.Get("camera.uplink_follow_min");
        public static float UplinkFollowMax => UiTuningValues.Get("camera.uplink_follow_max");

        /// <summary>FG1-HUD-01：接入时镜头距离（倍率，钳在调参表范围内；立即生效）。</summary>
        public static void SetUplinkCameraZoom(float value)
        {
            Data.UplinkCameraZoom = Mathf.Clamp(value, UplinkCameraZoomMin, UplinkCameraZoomMax);
            Save();
        }

        /// <summary>FG1-HUD-01：接入时镜头跟随力度（倍率，钳在调参表范围内；立即生效）。</summary>
        public static void SetUplinkFollowStrength(float value)
        {
            Data.UplinkFollowStrength = Mathf.Clamp(value, UplinkFollowMin, UplinkFollowMax);
            Save();
        }

        /// <summary>FG1-HUD-01：接入镜头两项恢复默认（1 = 原手感）。</summary>
        public static void ResetUplinkCamera()
        {
            Data.UplinkCameraZoom = 1f;
            Data.UplinkFollowStrength = 1f;
            Save();
        }

        public static void SetScreenShakeEnabled(bool enabled)
        {
            Data.ScreenShakeEnabled = enabled;
            Save();
        }

        public static void SetReactionPopupsEnabled(bool enabled)
        {
            Data.ReactionPopupsEnabled = enabled;
            if (!enabled)
            {
                GameLogic.Campaign.Feedback.ReactionPopups.Clear(); // 关掉时屏幕上已有的弹字也立刻收起。
            }
            Save();
        }

        public static void SetReactionSlowMotionEnabled(bool enabled)
        {
            Data.ReactionSlowMotionEnabled = enabled;
            if (!enabled)
            {
                GameLogic.Core.GameClock.CancelSlowMotion(); // 关掉时正在慢放的也立刻恢复。
            }
            Save();
        }

        public static void SetReactionCameraNudgeEnabled(bool enabled)
        {
            Data.ReactionCameraNudgeEnabled = enabled;
            if (!enabled)
            {
                GameLogic.Campaign.Feedback.CameraNudge.Reset();
            }
            Save();
        }

        /// <summary>FG2-FW-04：三个反应反馈开关恢复默认（全开）。</summary>
        public static void ResetReactionFeedback()
        {
            Data.ReactionPopupsEnabled = true;
            Data.ReactionSlowMotionEnabled = true;
            Data.ReactionCameraNudgeEnabled = true;
            Save();
        }

        public static void SetFlashReductionEnabled(bool enabled)
        {
            Data.FlashReductionEnabled = enabled;
            Save();
        }

        /// <summary>FG3-LOG-01（FG03 第 4 节“可以开关格线显示”）：建造模式叠加层画不画格线。本机偏好，跨战役保留。</summary>
        public static bool BuildGridLinesEnabled => Data.BuildGridLinesEnabled;

        // ── FG7-ENV-01：夜间亮度下限（FGR-ENV-003）──
        public static float NightBrightnessMin => UiTuningValues.Get("env.night_brightness_min");
        public static float NightBrightnessMax => UiTuningValues.Get("env.night_brightness_max");
        public static float NightBrightnessDefault => Mathf.Clamp(UiTuningValues.Get("env.night_brightness_default"), NightBrightnessMin, NightBrightnessMax);

        /// <summary>夜里画面最暗是白天的多少（没设过 = 表里的默认值；读到的值总在范围内）。</summary>
        public static float NightBrightnessFloor => Data.NightBrightnessFloor < 0f ? NightBrightnessDefault : Mathf.Clamp(Data.NightBrightnessFloor, NightBrightnessMin, NightBrightnessMax);

        public static void SetNightBrightnessFloor(float value)
        {
            Data.NightBrightnessFloor = Mathf.Clamp(value, NightBrightnessMin, NightBrightnessMax);
            Save();
        }

        /// <summary>恢复默认（写 -1 = 跟随表里的默认值）。</summary>
        public static void ResetNightBrightnessFloor()
        {
            Data.NightBrightnessFloor = -1f;
            Save();
        }

        // ── FG7-ENV-02：静默夜画面压暗强度（FG07 第 4 节）──
        public static float SilentNightDimMin => UiTuningValues.Get("env.silent_dim_min");
        public static float SilentNightDimMax => UiTuningValues.Get("env.silent_dim_max");
        public static float SilentNightDimDefault => Mathf.Clamp(UiTuningValues.Get("env.silent_dim_default"), SilentNightDimMin, SilentNightDimMax);

        /// <summary>静默夜期间画面在昼夜亮度之上再暗多少（没设过 = 表里的默认值；读到的值总在范围内）。</summary>
        public static float SilentNightDim => Data.SilentNightDim < 0f ? SilentNightDimDefault : Mathf.Clamp(Data.SilentNightDim, SilentNightDimMin, SilentNightDimMax);

        public static void SetSilentNightDim(float value)
        {
            Data.SilentNightDim = Mathf.Clamp(value, SilentNightDimMin, SilentNightDimMax);
            Save();
        }

        public static void ResetSilentNightDim()
        {
            Data.SilentNightDim = -1f;
            Save();
        }

        // ── FG7-ENV-03：天气视觉效果强度（FG07 第 4 节）──
        public static float WeatherFxMin => UiTuningValues.Get("env.weather_fx_min");
        public static float WeatherFxMax => UiTuningValues.Get("env.weather_fx_max");
        public static float WeatherFxDefault => Mathf.Clamp(UiTuningValues.Get("env.weather_fx_default"), WeatherFxMin, WeatherFxMax);

        /// <summary>天气画面（粒子与沙暴雾）的强度（没设过 = 表里的默认值；读到的值总在范围内）。只影响画面，天气效果不变。</summary>
        public static float WeatherFx => Data.WeatherFx < 0f ? WeatherFxDefault : Mathf.Clamp(Data.WeatherFx, WeatherFxMin, WeatherFxMax);

        public static void SetWeatherFx(float value)
        {
            Data.WeatherFx = Mathf.Clamp(value, WeatherFxMin, WeatherFxMax);
            Save();
        }

        public static void ResetWeatherFx()
        {
            Data.WeatherFx = -1f;
            Save();
        }

        // ── FG9-NAR-01：文本显示速度与字幕大小（FG11 第 8 节、FGR-UX-060）──
        public static float NarrativeTextSpeedMin => UiTuningValues.Get("narrative.text_speed_min");
        public static float NarrativeTextSpeedMax => UiTuningValues.Get("narrative.text_speed_max");
        public static float NarrativeTextSpeedDefault => Mathf.Clamp(UiTuningValues.Get("narrative.text_speed_default"), NarrativeTextSpeedMin, NarrativeTextSpeedMax);

        /// <summary>演出字幕每真实秒显示几个字（没设过 = 表里的默认值；读到的值总在范围内）。等于最大值时整行立即出现。只影响显示，不影响剧情推进。</summary>
        public static float NarrativeTextSpeed => Data.NarrativeTextSpeed < 0f ? NarrativeTextSpeedDefault : Mathf.Clamp(Data.NarrativeTextSpeed, NarrativeTextSpeedMin, NarrativeTextSpeedMax);

        /// <summary>文本显示速度拉到最大 = 整行立即出现。</summary>
        public static bool NarrativeTextInstant => NarrativeTextSpeed >= NarrativeTextSpeedMax - 0.001f;

        public static void SetNarrativeTextSpeed(float value)
        {
            Data.NarrativeTextSpeed = Mathf.Clamp(value, NarrativeTextSpeedMin, NarrativeTextSpeedMax);
            Save();
        }

        public static float SubtitleScaleMin => UiTuningValues.Get("narrative.subtitle_scale_min");
        public static float SubtitleScaleMax => UiTuningValues.Get("narrative.subtitle_scale_max");
        public static float SubtitleScaleDefault => Mathf.Clamp(UiTuningValues.Get("narrative.subtitle_scale_default"), SubtitleScaleMin, SubtitleScaleMax);

        /// <summary>字幕大小倍率（演出字幕与资料库正文；没设过 = 默认；总在范围内）。</summary>
        public static float SubtitleScale => Data.SubtitleScale < 0f ? SubtitleScaleDefault : Mathf.Clamp(Data.SubtitleScale, SubtitleScaleMin, SubtitleScaleMax);

        public static void SetSubtitleScale(float value)
        {
            Data.SubtitleScale = Mathf.Clamp(value, SubtitleScaleMin, SubtitleScaleMax);
            Save();
        }

        /// <summary>文本与字幕两项恢复默认。</summary>
        public static void ResetNarrativeText()
        {
            Data.NarrativeTextSpeed = -1f;
            Data.SubtitleScale = -1f;
            Save();
        }

        /// <summary>FG4-ECO-09：远征回来时是否自动打开离家报告。本机偏好，跨战役保留；关掉后报告照常生成，只发一条通知。</summary>
        public static bool AwayReportAutoOpen => Data.AwayReportAutoOpen;

        /// <summary>FG8-QST-01：家园指引任务开关（本机偏好，跨战役保留）。关掉后这类任务不出现、不判定、不发通知；已经拿到的奖励不收回，重新打开接着进行。</summary>
        public static bool HomeGuideQuestsEnabled => Data.HomeGuideQuestsEnabled;

        public static void SetHomeGuideQuestsEnabled(bool enabled)
        {
            if (Data.HomeGuideQuestsEnabled == enabled)
            {
                return;
            }
            Data.HomeGuideQuestsEnabled = enabled;
            Save();
        }

        public static void SetAwayReportAutoOpen(bool enabled)
        {
            if (Data.AwayReportAutoOpen == enabled)
            {
                return;
            }
            Data.AwayReportAutoOpen = enabled;
            Save();
        }

        public static void SetBuildGridLinesEnabled(bool enabled)
        {
            if (Data.BuildGridLinesEnabled == enabled)
            {
                return;
            }
            Data.BuildGridLinesEnabled = enabled;
            Save();
        }

        public static void SetMasterVolume(float value)
        {
            Data.MasterVolume = Mathf.Clamp01(value);
            Save();
        }

        public static void SetMusicVolume(float value)
        {
            Data.MusicVolume = Mathf.Clamp01(value);
            Save();
        }

        public static void SetSfxVolume(float value)
        {
            Data.SfxVolume = Mathf.Clamp01(value);
            Save();
        }

        public static void SetUiVolume(float value)
        {
            Data.UiVolume = Mathf.Clamp01(value);
            Save();
        }

        public static void SetSubtitlesEnabled(bool enabled)
        {
            Data.SubtitlesEnabled = enabled;
            Save();
        }

        /// <summary>FG0-DATA-01：切换界面语言并落盘；广播 <see cref="ISettingsEvent.OnSettingsChanged"/>，
        /// 订阅方按新语言刷新文本。</summary>
        public static void SetLanguage(GameLogic.Localization.GameLanguage language)
        {
            Data.Language = GameLogic.Localization.GameLanguageCodes.ToCode(language);
            Save();
        }

        /// <summary>旧入口（主菜单 uGUI 开关）：开 = 保留当前模式（没开过时用红绿），关 = 关闭色盲模式。</summary>
        public static void SetColorblindSafeIconsEnabled(bool enabled)
        {
            SetColorblindMode(enabled ? (ColorblindMode != ColorblindMode.Off ? ColorblindMode : ColorblindMode.RedGreen) : ColorblindMode.Off);
        }

        /// <summary>FG15-UX-02：设色盲模式；旧布尔一并写（降级到旧版本读同一份设置时仍是“开 / 关”）。</summary>
        public static void SetColorblindMode(ColorblindMode mode)
        {
            Data.ColorblindMode = (int)mode;
            Data.ColorblindSafeIconsEnabled = mode != ColorblindMode.Off;
            Save();
        }

        public static bool KeyboardPointerEnabled => Data.KeyboardPointerEnabled;

        public static void SetKeyboardPointerEnabled(bool enabled)
        {
            Data.KeyboardPointerEnabled = enabled;
            Save();
        }

        public static void SetHighContrastEnabled(bool enabled)
        {
            Data.HighContrastEnabled = enabled;
            Save();
        }

        public static void SetTextScale(float value)
        {
            Data.TextScale = Mathf.Clamp(value, TextScaleMin, TextScaleMax);
            Save();
        }

        /// <summary>FG15-UX-02（FGR-UX-060“闪光减弱、震屏、慢放、镜头推动统称减少动态效果”）：四项全在“少动”一侧时为开。</summary>
        public static bool ReduceMotion => Data.FlashReductionEnabled && !Data.ScreenShakeEnabled && !Data.ReactionSlowMotionEnabled && !Data.ReactionCameraNudgeEnabled;

        /// <summary>一次切换四项：开 = 闪光减弱、关震屏、关慢放、关镜头推动；关 = 四项回到默认。四项仍可单独再改。</summary>
        public static void SetReduceMotion(bool enabled)
        {
            var d = new GameSettingsData();
            Data.FlashReductionEnabled = enabled || d.FlashReductionEnabled;
            Data.ScreenShakeEnabled = !enabled && d.ScreenShakeEnabled;
            Data.ReactionSlowMotionEnabled = !enabled && d.ReactionSlowMotionEnabled;
            Data.ReactionCameraNudgeEnabled = !enabled && d.ReactionCameraNudgeEnabled;
            Save();
        }

        /// <summary>FG0-UX-01：尝试重绑。Conflict 时不落地，<paramref name="conflicts"/> 给出冲突方，
        /// 调用方弹确认框（覆盖 / 取消），选覆盖再调 <see cref="ForceRebind"/>。</summary>
        public static RebindResult TryRebind(GameActionId action, InputChord chord, System.Collections.Generic.List<GameActionId> conflicts)
        {
            EnsureLoaded();
            RebindResult result = _keyBindings.TryRebind(action, chord, conflicts);
            if (result == RebindResult.Ok)
            {
                Save();
            }
            return result;
        }

        /// <summary>确认覆盖：冲突方变为未绑定。冲突方里有必须保留按键的动作时返回 RequiredBlocked，什么都不改。</summary>
        public static RebindResult ForceRebind(GameActionId action, InputChord chord, System.Collections.Generic.List<GameActionId> blockedBy = null)
        {
            EnsureLoaded();
            RebindResult result = _keyBindings.ForceRebind(action, chord, blockedBy);
            if (result == RebindResult.Ok)
            {
                Save();
            }
            return result;
        }

        /// <summary>单个动作恢复默认（默认键与别的改过的动作撞键时返回 Conflict，不落地）。</summary>
        public static RebindResult ResetKeyBinding(GameActionId action, System.Collections.Generic.List<GameActionId> conflicts = null)
        {
            EnsureLoaded();
            RebindResult result = _keyBindings.ResetToDefault(action, conflicts);
            if (result == RebindResult.Ok)
            {
                Save();
            }
            return result;
        }

        public static void ResetKeyBindingsToDefault()
        {
            EnsureLoaded();
            _keyBindings.ResetAllToDefault();
            Save();
        }

        public static void ResetAllToDefault()
        {
            _data = new GameSettingsData();
            _keyBindings = InputBindingSet.CreateDefault();
            Save();
        }

        // ── FG15-UX-01（FGR-SYS-020）新增设置项 ──
        private static float Tune(string id, float fallback)
        {
            return UiTuningValues.TryGet(id, out float v) && v > 0f ? v : fallback;
        }

        public static int ResolutionWidth => Data.ResolutionWidth;
        public static int ResolutionHeight => Data.ResolutionHeight;
        public static int WindowMode => Data.WindowMode;

        /// <summary>只由 <see cref="DisplayModeService"/> 在玩家确认“保留”后调用（未确认的修改不落盘，崩溃 / 退出后仍是旧设置）。</summary>
        public static void SetDisplayMode(int width, int height, int windowMode)
        {
            Data.ResolutionWidth = Math.Max(0, width);
            Data.ResolutionHeight = Math.Max(0, height);
            Data.WindowMode = windowMode;
            Save();
        }

        public static bool VSyncEnabled => Data.VSyncEnabled;

        public static void SetVSyncEnabled(bool enabled)
        {
            Data.VSyncEnabled = enabled;
            Save();
            FramePacing.ApplyStored();
        }

        public static float BrightnessMin => Tune("settings.brightness_min", 0.5f);
        public static float BrightnessMax => Tune("settings.brightness_max", 1.5f);
        public static float Brightness => Mathf.Clamp(Data.Brightness > 0f ? Data.Brightness : 1f, BrightnessMin, BrightnessMax);

        public static void SetBrightness(float value)
        {
            Data.Brightness = Mathf.Clamp(value, BrightnessMin, BrightnessMax);
            Save();
        }

        public const int QualityLow = 0, QualityMedium = 1, QualityHigh = 2, QualityCustom = 3;
        public static int QualityPreset => Mathf.Clamp(Data.QualityPreset, QualityLow, QualityCustom);
        public static int ShadowQuality => Mathf.Clamp(Data.ShadowQuality, 0, 2);
        public static float EffectsDensityMin => Tune("settings.effects_density_min", 0.25f);
        public static float EffectsDensity => Mathf.Clamp(Data.EffectsDensity, EffectsDensityMin, 1f);
        public static bool PostProcessEnabled => Data.PostProcessEnabled;

        /// <summary>后处理开关的渲染侧接收口：Built-in 管线下没有后处理栈，默认什么都不做；FG15-PL-01（URP 合流）把它接到 Volume 上（DEBT-FG15UX01-01）。</summary>
        public static Action<bool> PostProcessSink;

        /// <summary>画质预设：低 / 中 / 高 一次改三个子项；选“自定义”不动子项。</summary>
        public static void SetQualityPreset(int preset)
        {
            preset = Mathf.Clamp(preset, QualityLow, QualityCustom);
            Data.QualityPreset = preset;
            if (preset != QualityCustom)
            {
                Data.ShadowQuality = preset == QualityLow ? 0 : preset == QualityMedium ? 1 : 2;
                Data.EffectsDensity = preset == QualityLow ? EffectsDensityMin : preset == QualityMedium ? 0.6f : 1f;
                Data.PostProcessEnabled = preset != QualityLow;
            }
            Save();
            ApplyQuality();
        }

        public static void SetShadowQuality(int value)
        {
            Data.ShadowQuality = Mathf.Clamp(value, 0, 2);
            Data.QualityPreset = QualityCustom;
            Save();
            ApplyQuality();
        }

        public static void SetEffectsDensity(float value)
        {
            Data.EffectsDensity = Mathf.Clamp(value, EffectsDensityMin, 1f);
            Data.QualityPreset = QualityCustom;
            Save();
        }

        public static void SetPostProcessEnabled(bool enabled)
        {
            Data.PostProcessEnabled = enabled;
            Data.QualityPreset = QualityCustom;
            Save();
            ApplyQuality();
        }

        /// <summary>把画质子项推给引擎（启动时与每次修改时）：阴影 → QualitySettings.shadows；后处理 → <see cref="PostProcessSink"/>；特效密度由 FeedbackVfxPresenter 每次生成时读。</summary>
        public static void ApplyQuality()
        {
            QualitySettings.shadows = ShadowQuality == 0 ? UnityEngine.ShadowQuality.Disable
                : ShadowQuality == 1 ? UnityEngine.ShadowQuality.HardOnly : UnityEngine.ShadowQuality.All;
            PostProcessSink?.Invoke(PostProcessEnabled);
        }

        public static float AmbientVolume => Data.AmbientVolume;

        public static void SetAmbientVolume(float value)
        {
            Data.AmbientVolume = Mathf.Clamp01(value);
            Save();
        }

        public static bool MuteOnFocusLoss => Data.MuteOnFocusLoss;

        public static void SetMuteOnFocusLoss(bool enabled)
        {
            Data.MuteOnFocusLoss = enabled;
            Save();
        }

        public static bool PauseOnFocusLoss => Data.PauseOnFocusLoss;

        public static void SetPauseOnFocusLoss(bool enabled)
        {
            Data.PauseOnFocusLoss = enabled;
            Save();
        }

        public static float ZoomSpeedMultiplier => Mathf.Clamp(Data.ZoomSpeedMultiplier > 0f ? Data.ZoomSpeedMultiplier : 1f, 0.25f, 2.5f);

        public static void SetZoomSpeedMultiplier(float value)
        {
            Data.ZoomSpeedMultiplier = Mathf.Clamp(value, 0.25f, 2.5f);
            Save();
        }

        public static bool InvertScroll => Data.InvertScroll;

        public static void SetInvertScroll(bool enabled)
        {
            Data.InvertScroll = enabled;
            Save();
        }

        public static float UplinkMouseSensitivity => Mathf.Clamp(Data.UplinkMouseSensitivity, 0f, 2f);

        public static void SetUplinkMouseSensitivity(float value)
        {
            Data.UplinkMouseSensitivity = Mathf.Clamp(value, 0f, 2f);
            Save();
        }

        public static bool HoldActionsToggle => Data.HoldActionsToggle;

        public static void SetHoldActionsToggle(bool enabled)
        {
            Data.HoldActionsToggle = enabled;
            Save();
        }

        /// <summary>自动存档间隔可选值（分钟；0 = 关闭）。</summary>
        public static readonly int[] AutoSaveChoices = { 0, 5, 10, 15, 30 };
        public static int AutoSaveMinutes => Array.IndexOf(AutoSaveChoices, Data.AutoSaveMinutes) >= 0 ? Data.AutoSaveMinutes : 10;

        public static void SetAutoSaveMinutes(int minutes)
        {
            Data.AutoSaveMinutes = Array.IndexOf(AutoSaveChoices, minutes) >= 0 ? minutes : 10;
            Save();
        }

        public static bool GuidanceEnabled => Data.GuidanceEnabled;

        public static void SetGuidanceEnabled(bool enabled)
        {
            Data.GuidanceEnabled = enabled;
            Save();
        }

        public static bool CausalChainOverlayEnabled => Data.CausalChainOverlayEnabled;

        public static void SetCausalChainOverlayEnabled(bool enabled)
        {
            Data.CausalChainOverlayEnabled = enabled;
            Save();
        }

        public static bool DamageNumbersEnabled => Data.DamageNumbersEnabled;

        public static void SetDamageNumbersEnabled(bool enabled)
        {
            Data.DamageNumbersEnabled = enabled;
            if (!enabled)
            {
                GameLogic.Campaign.Feedback.DamageNumbers.Clear();
            }
            Save();
        }

        /// <summary>FG15-UX-01（FGR-SYS-021“每个分类都有恢复默认”）：把一组字段恢复成 <see cref="GameSettingsData"/> 的初值（用一份新建的默认对象逐字段拷回）。
        /// 字段名写错会在自检里失败（SettingsCatalog 每个分类的恢复默认都有行为断言）。</summary>
        public static void ResetFields(params string[] fieldNames)
        {
            var defaults = new GameSettingsData();
            Type t = typeof(GameSettingsData);
            foreach (string name in fieldNames)
            {
                System.Reflection.FieldInfo f = t.GetField(name);
                if (f == null)
                {
                    Log.Error($"[GameSettings] 恢复默认：没有字段 {name}");
                    continue;
                }
                f.SetValue(Data, f.GetValue(defaults));
            }
            Save();
            ApplyQuality();
            FramePacing.ApplyStored();
        }
    }

    /// <summary>目标帧率（本机偏好）。开发标准是最低 120 帧，默认即 120；必须同时关掉垂直同步，
    /// 否则 Ultra 等画质档自带的 vSyncCount=1 会让 targetFrameRate 被忽略、锁在显示器刷新率。
    /// 键名带 v2：旧键里存过的 60 不再沿用。冷启动（GameApp）与设置面板共用这里。</summary>
    public static class FramePacing
    {
        public const int DefaultFps = 120;
        private const string PrefsKey = "GameUi.TargetFps.v2";

        public static int TargetFps => PlayerPrefs.GetInt(PrefsKey, DefaultFps);

        public static void ApplyStored() => Apply(TargetFps);

        public static void Set(int fps)
        {
            PlayerPrefs.SetInt(PrefsKey, fps);
            Apply(fps);
        }

        /// <summary>帧率上限可选值（-1 = 不限）。</summary>
        public static readonly int[] Choices = { 30, 60, 120, 144, 165, 240, -1 };

        public static void ResetToDefault() => Set(DefaultFps);

        /// <summary>FG15-UX-01：垂直同步改由设置决定（默认关）；开着时 targetFrameRate 被引擎忽略，帧率锁在刷新率。</summary>
        private static void Apply(int fps)
        {
            QualitySettings.vSyncCount = GameSettings.VSyncEnabled ? 1 : 0;
            Application.targetFrameRate = fps;
        }
    }
}
