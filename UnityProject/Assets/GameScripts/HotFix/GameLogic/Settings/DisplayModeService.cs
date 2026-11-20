using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using Log = TEngine.Log;

namespace GameLogic.Settings
{
    /// <summary>显示设置的引擎接口（自检换成假的，验证确认 / 回退 / 失败流程）。</summary>
    public interface IDisplayBackend
    {
        int Width { get; }
        int Height { get; }
        FullScreenMode Mode { get; }
        /// <summary>显示器支持的分辨率（去重、从小到大）。</summary>
        List<Vector2Int> Supported();
        /// <summary>请求切换。返回 false 并给出原因 = 引擎拒绝（例如参数非法）。</summary>
        bool Apply(int width, int height, FullScreenMode mode, out string error);
        /// <summary>切换后是否需要核对实际尺寸（编辑器里 Screen.SetResolution 不生效，不核对）。</summary>
        bool VerifyAfterApply { get; }
    }

    public sealed class UnityDisplayBackend : IDisplayBackend
    {
        public int Width => Screen.width;
        public int Height => Screen.height;
        public FullScreenMode Mode => Screen.fullScreenMode;
        public bool VerifyAfterApply => !Application.isEditor && !Application.isBatchMode;

        public List<Vector2Int> Supported()
        {
            var list = new List<Vector2Int>();
            foreach (Resolution r in Screen.resolutions)
            {
                var v = new Vector2Int(r.width, r.height);
                if (!list.Contains(v))
                {
                    list.Add(v);
                }
            }
            if (list.Count == 0)
            {
                list.Add(new Vector2Int(Screen.width, Screen.height)); // 编辑器 / 无头环境下没有列表：至少有当前尺寸。
            }
            list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return list;
        }

        public bool Apply(int width, int height, FullScreenMode mode, out string error)
        {
            error = null;
            try
            {
                Screen.SetResolution(width, height, mode);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public enum DisplayRequestResult
    {
        Applied = 0,
        Unchanged = 1,
        Unsupported = 2,
        Failed = 3,
    }

    /// <summary>
    /// FG15-UX-01（FGR-SYS-021“修改分辨率和窗口模式后，给 15 秒确认，超时自动回退”；FGT-SYS-005）：
    /// - 改分辨率 / 窗口模式立即生效，但不落盘；屏幕上出现“保留 / 还原（N 秒）”，按真实时间倒数（暂停、倍速、慢放都不影响）；
    /// - 点“保留”才写进设置；点“还原”、倒数到 0、关闭设置面板、回主菜单时都回到上一次确认过的显示设置；退出游戏 / 崩溃时没写盘，下次启动仍是旧设置；
    /// - 倒数期间再改一次：回退目标仍是最初确认过的那一份；
    /// - 失败（显示器不支持的分辨率、引擎拒绝、切完 1 秒后实际尺寸不对）：显示“显示设置失败”并保持 / 回到原设置（负向）。
    /// </summary>
    public static class DisplayModeService
    {
        public static IDisplayBackend Backend = new UnityDisplayBackend();
        public static Func<float> RealTime = () => Time.realtimeSinceStartup;

        public static bool Pending { get; private set; }
        public static float Deadline { get; private set; }
        public static int RevertCount { get; private set; }
        public static int KeepCount { get; private set; }
        /// <summary>最近一次失败的原因（玩家可见文本；空 = 没有失败）。</summary>
        public static string LastError { get; private set; } = string.Empty;
        public static int Revision { get; private set; }

        private static int _prevW, _prevH;
        private static FullScreenMode _prevMode;
        private static int _reqW, _reqH;
        private static FullScreenMode _reqMode;
        private static float _appliedAt;
        private static bool _verified;

        public static float ConfirmSeconds => UiTuningValues.TryGet("settings.display_confirm_seconds", out float v) && v > 0f ? v : 15f;

        public static float SecondsLeft => Pending ? Mathf.Max(0f, Deadline - RealTime()) : 0f;

        // 等待确认期间下拉框显示请求中的值：Screen.SetResolution 要到下一帧才生效，读 Backend 会在这 15 秒里弹回旧值。
        public static int ShownWidth => Pending ? _reqW : Backend.Width;
        public static int ShownHeight => Pending ? _reqH : Backend.Height;
        public static FullScreenMode ShownMode => Pending ? _reqMode : Backend.Mode;

        public static readonly FullScreenMode[] Modes = { FullScreenMode.ExclusiveFullScreen, FullScreenMode.FullScreenWindow, FullScreenMode.Windowed };

        public static string ModeName(FullScreenMode m) => GameText.Get(m == FullScreenMode.ExclusiveFullScreen ? "settings.display.mode_fullscreen"
            : m == FullScreenMode.FullScreenWindow ? "settings.display.mode_borderless" : "settings.display.mode_windowed");

        /// <summary>改显示设置（分辨率与窗口模式一起传；只改其一时另一个传当前值）。</summary>
        public static DisplayRequestResult Request(int width, int height, FullScreenMode mode)
        {
            if (!Pending && width == Backend.Width && height == Backend.Height && mode == Backend.Mode)
            {
                return DisplayRequestResult.Unchanged;
            }
            if (!IsSupported(width, height, mode))
            {
                Fail(GameText.Format("settings.display.unsupported", width, height));
                return DisplayRequestResult.Unsupported;
            }
            if (!Pending)
            {
                _prevW = Backend.Width;
                _prevH = Backend.Height;
                _prevMode = Backend.Mode;
            }
            if (!Backend.Apply(width, height, mode, out string error))
            {
                Fail(GameText.Format("settings.display.failed", error ?? string.Empty));
                Backend.Apply(_prevW, _prevH, _prevMode, out _);
                Pending = false;
                Revision++;
                return DisplayRequestResult.Failed;
            }
            _reqW = width;
            _reqH = height;
            _reqMode = mode;
            _appliedAt = RealTime();
            _verified = false;
            Deadline = _appliedAt + ConfirmSeconds;
            Pending = true;
            LastError = string.Empty;
            Revision++;
            return DisplayRequestResult.Applied;
        }

        /// <summary>窗口模式下可以是不超过最大支持尺寸的任意尺寸；全屏只认显示器列表里的分辨率。</summary>
        public static bool IsSupported(int width, int height, FullScreenMode mode)
        {
            if (width < 640 || height < 360)
            {
                return false;
            }
            List<Vector2Int> list = Backend.Supported();
            if (mode == FullScreenMode.Windowed)
            {
                Vector2Int max = list[list.Count - 1];
                return width <= max.x && height <= max.y;
            }
            return list.Contains(new Vector2Int(width, height));
        }

        public static void Keep()
        {
            if (!Pending)
            {
                return;
            }
            Pending = false;
            KeepCount++;
            GameSettings.SetDisplayMode(_reqW, _reqH, (int)_reqMode);
            Revision++;
        }

        public static void Revert()
        {
            if (!Pending)
            {
                return;
            }
            Pending = false;
            RevertCount++;
            Backend.Apply(_prevW, _prevH, _prevMode, out _);
            Revision++;
        }

        /// <summary>每帧（GameRoot.OnUpdate，主菜单里也跑）：倒数到 0 自动还原；切完 1 秒核对实际尺寸，不对按失败还原。</summary>
        public static void Tick()
        {
            if (!Pending)
            {
                return;
            }
            float now = RealTime();
            if (!_verified && Backend.VerifyAfterApply && now - _appliedAt >= 1f)
            {
                _verified = true;
                if (Backend.Width != _reqW || Backend.Height != _reqH)
                {
                    Revert();
                    Fail(GameText.Format("settings.display.failed", $"{Backend.Width}×{Backend.Height}"));
                    return;
                }
            }
            if (now >= Deadline)
            {
                Revert();
            }
        }

        /// <summary>启动时套用玩家确认过的显示设置（没设过就不动；不再支持的分辨率不套用并记一条失败原因）。</summary>
        public static void ApplyStored()
        {
            int w = GameSettings.ResolutionWidth, h = GameSettings.ResolutionHeight, m = GameSettings.WindowMode;
            if (w <= 0 || h <= 0 || m < 0)
            {
                // 从没确认过：把启动时的显示设置记成已确认基线——否则第一次改了分辨率没确认就退出（黑屏时 Alt+F4），
                // 引擎自己记住的那个未确认分辨率下次启动会留下来。
                GameSettings.SetDisplayMode(Backend.Width, Backend.Height, (int)Backend.Mode);
                return;
            }
            var mode = (FullScreenMode)m;
            if (!IsSupported(w, h, mode))
            {
                LastError = GameText.Format("settings.display.unsupported", w, h);
                return;
            }
            Backend.Apply(w, h, mode, out _);
        }

        private static void Fail(string message)
        {
            LastError = message;
            Revision++;
            Log.Warning("[DisplayModeService] " + message);
        }

        public static void ResetForTests()
        {
            Backend = new UnityDisplayBackend();
            RealTime = () => Time.realtimeSinceStartup;
            Pending = false;
            RevertCount = 0;
            KeepCount = 0;
            LastError = string.Empty;
            Revision++;
        }
    }
}
