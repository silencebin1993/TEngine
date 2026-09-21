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

        public float UiScale = 1f; // AC-UI-004：80%/100%/140% 三档，允许任意连续值。
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
            _keyBindings = InputBindingSet.FromEntries(_data.KeyBindings);
        }

        /// <summary>落盘并广播 <see cref="ISettingsEvent.OnSettingsChanged"/>。所有 Set* 方法与
        /// <see cref="TryRebindKey"/>/<see cref="ResetKeyBindingsToDefault"/> 最终都走这里，
        /// 保证"改值即预览"不会有遗漏路径。</summary>
        private static void Save()
        {
            _data.KeyBindings = _keyBindings.ToEntries();
            string json = JsonUtility.ToJson(_data);
            PlayerPrefs.SetString(PrefsKey, json);
            PlayerPrefs.Save();

            // GameEventHelper.Init() 正常在 GameApp.Entrance() 最先调用，玩法期间广播必然安全；
            // 这里仍判空——本类是纯数据层，允许编辑器工具/单元测试在完整游戏启动之前调用
            // Set*/ResetAllToDefault（不应该因为事件系统还没初始化就崩），广播只是"锦上添花"的
            // 即时预览，跳过它不影响设置本身已经落盘这个事实。
            ISettingsEvent listener = GameEvent.Get<ISettingsEvent>();
            listener?.OnSettingsChanged();
        }

        public static void SetUiScale(float value)
        {
            Data.UiScale = Mathf.Clamp(value, 0.8f, 1.4f);
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

        public static void SetColorblindSafeIconsEnabled(bool enabled)
        {
            Data.ColorblindSafeIconsEnabled = enabled;
            Save();
        }

        /// <summary>true=成功重绑；false 且 <paramref name="conflict"/> 有值=与另一动作撞键，
        /// 调用方（设置 UI）应弹确认，确认后调用 <see cref="ForceRebindKey"/>。</summary>
        public static bool TryRebindKey(GameActionId action, KeyCode key, out GameActionId conflict)
        {
            EnsureLoaded();
            bool ok = _keyBindings.TryRebind(action, key, out conflict);
            if (ok)
            {
                Save();
            }
            return ok;
        }

        public static void ForceRebindKey(GameActionId action, KeyCode key, GameActionId conflict)
        {
            EnsureLoaded();
            _keyBindings.ForceRebind(action, key, conflict);
            Save();
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
