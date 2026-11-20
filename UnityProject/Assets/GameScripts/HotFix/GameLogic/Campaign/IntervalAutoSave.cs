namespace GameLogic.Campaign
{
    /// <summary>
    /// FG15-UX-01（FGR-SYS-020 游戏性“自动存档间隔”）：世界运行时按真实时间累计，到设置的间隔（5 / 10 / 15 / 30 分钟；0 = 关闭）就自动存一次档。
    /// - 暂停、在主菜单、没有活动战役时不累计（暂停时没有进度可丢）；倍速不影响间隔（按真实时间，0.5x～3x 一样）；
    /// - 存档被拒或写盘失败时不清零，退避 settings.autosave_retry_seconds 真实秒再试（不每帧重做整份导出与写盘）；
    ///   连续失败的第一次给一条失败提示（含原因），成功后清零；成功不弹确认（自动存档的轻提示照常）；
    /// - 每帧 O(1)，由 GameRoot.OnUpdate 调用；自检直接调 <see cref="Tick"/>。
    /// </summary>
    public static class IntervalAutoSave
    {
        public static float Elapsed { get; private set; }
        public static int SaveCount { get; private set; }
        /// <summary>连续失败次数（成功清零）。</summary>
        public static int FailStreak { get; private set; }
        public static float RetrySeconds => GameLogic.Core.UiTuningValues.TryGet("settings.autosave_retry_seconds", out float v) && v > 0f ? v : 30f;
        public static System.Func<SaveResult> SaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.Interval);

        public static void Tick(float realDt, bool worldRunning, bool paused)
        {
            int minutes = Settings.GameSettings.AutoSaveMinutes;
            if (minutes <= 0 || !worldRunning || !CampaignSession.HasActiveCampaign)
            {
                Elapsed = 0f;
                return;
            }
            if (paused || realDt <= 0f)
            {
                return;
            }
            Elapsed += realDt;
            if (Elapsed < minutes * 60f)
            {
                return;
            }
            SaveResult r = SaveHandler();
            if (r.Success)
            {
                Elapsed = 0f;
                SaveCount++;
                FailStreak = 0;
                return;
            }
            Elapsed = System.Math.Max(0f, minutes * 60f - RetrySeconds);
            if (FailStreak++ == 0)
            {
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, Localization.GameText.Format("settings.gameplay.autosave_failed", r.Message ?? string.Empty));
            }
        }

        public static void ResetForTests()
        {
            Elapsed = 0f;
            SaveCount = 0;
            FailStreak = 0;
            SaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.Interval);
        }
    }
}
