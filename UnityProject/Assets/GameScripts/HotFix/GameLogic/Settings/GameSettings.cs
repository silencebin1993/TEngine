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

        public bool ScreenShakeEnabled = true;
        public bool FlashReductionEnabled = false; // AC-ACC-003：降低闪光。

        public float MasterVolume = 1f;
        public float MusicVolume = 1f;
        public float SfxVolume = 1f;
        public float UiVolume = 1f;

        public bool SubtitlesEnabled = true;
        public bool ColorblindSafeIconsEnabled = false; // AC-ACC-002。

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
        public static bool ScreenShakeEnabled => Data.ScreenShakeEnabled;
        public static bool FlashReductionEnabled => Data.FlashReductionEnabled;
        public static float MasterVolume => Data.MasterVolume;
        public static float MusicVolume => Data.MusicVolume;
        public static float SfxVolume => Data.SfxVolume;
        public static float UiVolume => Data.UiVolume;
        public static bool SubtitlesEnabled => Data.SubtitlesEnabled;
        public static bool ColorblindSafeIconsEnabled => Data.ColorblindSafeIconsEnabled;
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

        public static void SetScreenShakeEnabled(bool enabled)
        {
            Data.ScreenShakeEnabled = enabled;
            Save();
        }

        public static void SetFlashReductionEnabled(bool enabled)
        {
            Data.FlashReductionEnabled = enabled;
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

        public static void SetColorblindSafeIconsEnabled(bool enabled)
        {
            Data.ColorblindSafeIconsEnabled = enabled;
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
    }
}
