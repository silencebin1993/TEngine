using GameLogic.Stage;

namespace GameLogic.Settings
{
    /// <summary>
    /// FG15-UX-01（FG00 B09 / FG-GAP-017；FGR-SYS-020“失焦时静音”）：窗口失焦 / 回焦时按设置暂停或继续、静音或照常。
    /// - 失焦暂停：只在世界运行中、且当时没有暂停时由它暂停，并记下“是我停的”；回焦时只恢复它自己停的（玩家失焦前已暂停 / 打开了暂停菜单就保持暂停）；
    /// - 失焦静音：失焦期间音量桥把总音量推成 0（<see cref="AudioMuted"/>），回焦后恢复，不改玩家的音量设置；
    /// - 每帧由 GameRoot 用 Application.isFocused 调一次（O(1)），自检直接调 <see cref="OnFocusChanged"/>。
    /// </summary>
    public static class FocusPolicy
    {
        public static bool Focused { get; private set; } = true;
        public static bool PausedByFocus { get; private set; }
        /// <summary>焦点每变一次 +1（音量桥据此重新应用）。</summary>
        public static int Revision { get; private set; }

        public static bool AudioMuted => !Focused && GameSettings.MuteOnFocusLoss;

        /// <summary>自检可替换：世界是否在运行 / 是否暂停 / 设置暂停。</summary>
        public static System.Func<bool> WorldRunning = () => GameRoot.AnyRegionActive && Campaign.CampaignSession.Current != null;
        public static System.Func<bool> IsPaused = () => GameRoot.IsWorldPaused;
        public static System.Action<bool> SetPaused = GameRoot.SetWorldPaused;

        public static void Tick(bool focused)
        {
            if (focused != Focused)
            {
                OnFocusChanged(focused);
            }
        }

        public static void OnFocusChanged(bool focused)
        {
            if (focused == Focused)
            {
                return;
            }
            Focused = focused;
            Revision++;
            if (!focused)
            {
                if (GameSettings.PauseOnFocusLoss && WorldRunning() && !IsPaused())
                {
                    SetPaused(true);
                    PausedByFocus = true;
                }
                return;
            }
            if (PausedByFocus)
            {
                PausedByFocus = false;
                if (WorldRunning() && IsPaused())
                {
                    SetPaused(false);
                }
            }
        }

        public static void ResetForTests()
        {
            Focused = true;
            PausedByFocus = false;
            Revision++;
            WorldRunning = () => GameRoot.AnyRegionActive && Campaign.CampaignSession.Current != null;
            IsPaused = () => GameRoot.IsWorldPaused;
            SetPaused = GameRoot.SetWorldPaused;
        }
    }
}
