using System;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG7-ENV-01（FG07 FGR-ENV-003“光照随时间变化；夜晚画面变暗，但所有 UI 信息不变；设置里提供夜间亮度下限”）：昼夜的画面表现。
    /// - 只改场景光照（主方向光的强度与颜色、环境光），不碰任何界面：UI Toolkit 面板与世界标签不受光照影响，夜里信息照常可读。
    /// - 画面亮度 = 夜间亮度下限 +（1 − 下限）× 光照（<see cref="DayNightCycle.Daylight"/>）；下限是玩家设置（<see cref="GameSettings.NightBrightnessFloor"/>，暂停菜单“画面”）。
    /// - 夜里方向光偏冷色（fg.TbHomeTuning env.light.night_tint_*），黄昏 / 黎明平滑过渡。
    /// - 每帧 O(1)：亮度变化小于 env.light.apply_epsilon 时不写光照；离开世界（回主菜单）时恢复场景原来的光照。
    /// - 纯表现：读统一时钟，不影响模拟（太阳能发电走 <see cref="DayNightCycle.DaylightOf"/>，与画面无关）。
    /// </summary>
    public static class DayNightLighting
    {
        private static Light _sun;
        private static float _baseIntensity = 1f;
        private static Color _baseColor = Color.white;
        private static Color _baseAmbient = new Color(0.212f, 0.227f, 0.259f, 1f);
        private static bool _captured;
        private static bool _applied;
        private static float _lastBrightness = -1f;
        private static Color _lastTint = Color.white;
        private static int _lastSettingsRevision = -1;
        private static float _nextSearch;

        /// <summary>当前写进场景的画面亮度（0～1；没在世界里 = 1）。自检 / 冒烟读。</summary>
        public static float Brightness { get; private set; } = 1f;

        /// <summary>实际写光照的次数（自检：亮度不变的帧不写）。</summary>
        public static int ApplyCount { get; private set; }

        /// <summary>被调光的方向光（自检读）。</summary>
        public static Light Sun => _sun;

        /// <summary>自检注入：替代真实时间（真实帧不改）。</summary>
        public static Func<float> RealTimeForTests;

        /// <summary>画面亮度：下限 +（1 − 下限）× 光照。</summary>
        public static float BrightnessFor(float daylight, float floor)
        {
            floor = Mathf.Clamp01(floor);
            return floor + (1f - floor) * Mathf.Clamp01(daylight);
        }

        /// <summary>夜里的方向光色（表里 env.light.night_tint_r / g / b）。</summary>
        public static Color NightTint => new Color(Tuning("env.light.night_tint_r", 0.55f), Tuning("env.light.night_tint_g", 0.65f), Tuning("env.light.night_tint_b", 1f), 1f);

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        /// <summary>每帧（<c>GameRoot.OnUpdate</c>）：在世界里按统一时钟调光；不在世界里恢复原光照。O(1)。</summary>
        public static void Tick(bool inWorld)
        {
            if (!inWorld)
            {
                Restore();
                return;
            }
            Apply(DayNightCycle.Daylight(GameClock.GameSeconds));
        }

        /// <summary>按给定光照调光（自检直接调）。返回这次是否真的写了光照。</summary>
        public static bool Apply(float daylight)
        {
            if (!EnsureSun())
            {
                return false;
            }
            float floor = GameSettings.NightBrightnessFloor;
            float b = BrightnessFor(daylight, floor);
            Color tint = Color.Lerp(NightTint, Color.white, Mathf.Clamp01(daylight));
            float eps = Math.Max(0.0005f, Tuning("env.light.apply_epsilon", 0.003f));
            bool settingsChanged = _lastSettingsRevision != GameSettings.Revision;
            if (_applied && !settingsChanged && Math.Abs(b - _lastBrightness) < eps && Math.Abs(tint.b - _lastTint.b) < eps && Math.Abs(tint.r - _lastTint.r) < eps)
            {
                return false;
            }
            _lastSettingsRevision = GameSettings.Revision;
            _lastBrightness = b;
            _lastTint = tint;
            _applied = true;
            Brightness = b;
            _sun.intensity = _baseIntensity * b;
            _sun.color = _baseColor * tint;
            RenderSettings.ambientLight = _baseAmbient * b;
            ApplyCount++;
            return true;
        }

        /// <summary>恢复场景原来的光照（回主菜单、自检收尾）。</summary>
        public static void Restore()
        {
            if (_applied && _sun != null)
            {
                _sun.intensity = _baseIntensity;
                _sun.color = _baseColor;
                RenderSettings.ambientLight = _baseAmbient;
            }
            _applied = false;
            _lastBrightness = -1f;
            Brightness = 1f;
        }

        private static bool EnsureSun()
        {
            if (_sun != null)
            {
                return true;
            }
            if (_captured)
            {
                // 原来的方向光被销毁（场景切换）：重新找，原光照按新的灯记。
                _captured = false;
                _applied = false;
            }
            float now = RealTimeForTests != null ? RealTimeForTests() : Time.realtimeSinceStartup;
            if (now < _nextSearch)
            {
                return false;
            }
            _nextSearch = now + 1f; // 找不到时每真实秒最多找一次（不每帧遍历场景）。
            Light found = RenderSettings.sun;
            if (found == null || found.type != LightType.Directional)
            {
                found = null;
                foreach (Light l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled)
                    {
                        found = l;
                        break;
                    }
                }
            }
            if (found == null)
            {
                return false;
            }
            _sun = found;
            _baseIntensity = found.intensity;
            _baseColor = found.color;
            _baseAmbient = RenderSettings.ambientLight;
            _captured = true;
            _applied = false;
            return true;
        }

        /// <summary>自检之间复位（先恢复光照，再忘掉记住的灯）。</summary>
        public static void ResetForTests()
        {
            Restore();
            _sun = null;
            _captured = false;
            _nextSearch = 0f;
            _lastSettingsRevision = -1;
            ApplyCount = 0;
            RealTimeForTests = null;
        }
    }
}
