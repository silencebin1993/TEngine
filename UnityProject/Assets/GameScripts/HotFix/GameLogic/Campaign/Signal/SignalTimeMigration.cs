using System;
using GameLogic.Core;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// FG1-E2E-01（DEBT-FG1SIG07-05）：信号相关的计时字段从“游戏秒（double）”改存“统一时钟步（long）”后的旧档迁移。
    ///
    /// 为什么改：游戏秒是 1/StepHz 的倍数，存成 double 经 JsonUtility 读回偶尔差 1 ulp（FGJ-M0 的存读档往返逐字段对照报过
    /// HighPowerElapsedSeconds 26.416675867512823 ≠ …827），里程碑出口旅程要求逐位一致，所以一律存整数步。
    ///
    /// 涉及字段（旧字段保留在类里只为读旧档，迁移后清成默认值，新代码不再读写）：
    /// - 核心固件冷却 <see cref="SignalCoreCooldownRecord.ReadyAtGameSeconds"/> → <see cref="SignalCoreCooldownRecord.ReadyTick"/>；
    /// - 常规裸跑计次 <see cref="SignalCoreState.RawChargeReadyAtGameSeconds"/> → <see cref="SignalCoreState.RawChargeReadyTick"/>；
    /// - 安全模式进入 / 条件消失时刻 <see cref="SignalSafeModeRecord.SinceGameSeconds"/> / <see cref="SignalSafeModeRecord.ClearSinceGameSeconds"/>
    ///   → <see cref="SignalSafeModeRecord.SinceTick"/> / <see cref="SignalSafeModeRecord.ClearSinceTick"/>；
    /// - 高功率生产窗口 <see cref="CampaignState.HighPowerElapsedSeconds"/> → <see cref="CampaignState.HighPowerElapsedTicks"/>。
    ///
    /// 换算用存档里时钟的步长频率（<see cref="GameClockState.StepHz"/>，没有时用当前的），四舍五入到整步。幂等：新字段已有值时不覆盖，
    /// 旧字段迁移后清零，第二次读档什么也不做。由 <c>CampaignSaveService.Load</c> 在读档对账之后调用。O(冷却条数 + 安全模式条数)。
    /// </summary>
    public static class SignalTimeMigration
    {
        /// <summary>自检读点：累计迁移了几个字段。</summary>
        public static int MigratedFields { get; private set; }

        /// <summary>把旧档的游戏秒字段换成整数步。返回这次迁移的字段数。</summary>
        public static int Migrate(CampaignState s)
        {
            if (s == null)
            {
                return 0;
            }
            int hz = s.Clock != null && s.Clock.StepHz > 0 ? s.Clock.StepHz : GameClock.StepHz;
            int n = 0;
            if (s.HighPowerElapsedSeconds > 0)
            {
                if (s.HighPowerElapsedTicks == 0)
                {
                    s.HighPowerElapsedTicks = ToTicks(s.HighPowerElapsedSeconds, hz);
                    n++;
                }
                s.HighPowerElapsedSeconds = 0;
            }
            SignalCoreState core = s.SignalCore;
            if (core != null)
            {
                if (core.RawChargeReadyAtGameSeconds > 0)
                {
                    if (core.RawChargeReadyTick == 0)
                    {
                        core.RawChargeReadyTick = ToTicks(core.RawChargeReadyAtGameSeconds, hz);
                        n++;
                    }
                    core.RawChargeReadyAtGameSeconds = 0;
                }
                SignalCoreCooldownRecord[] cds = core.CoreCooldowns ?? Array.Empty<SignalCoreCooldownRecord>();
                for (int i = 0; i < cds.Length; i++)
                {
                    SignalCoreCooldownRecord c = cds[i];
                    if (c == null || c.ReadyAtGameSeconds <= 0)
                    {
                        continue;
                    }
                    if (c.ReadyTick == 0)
                    {
                        c.ReadyTick = ToTicks(c.ReadyAtGameSeconds, hz);
                        n++;
                    }
                    c.ReadyAtGameSeconds = 0;
                }
                SignalSafeModeRecord[] modes = core.SafeModes ?? Array.Empty<SignalSafeModeRecord>();
                for (int i = 0; i < modes.Length; i++)
                {
                    SignalSafeModeRecord m = modes[i];
                    if (m == null)
                    {
                        continue;
                    }
                    if (m.SinceGameSeconds > 0)
                    {
                        if (m.SinceTick == 0)
                        {
                            m.SinceTick = ToTicks(m.SinceGameSeconds, hz);
                            n++;
                        }
                        m.SinceGameSeconds = 0;
                    }
                    if (m.ClearSinceGameSeconds >= 0)
                    {
                        if (m.ClearSinceTick < 0)
                        {
                            m.ClearSinceTick = ToTicks(m.ClearSinceGameSeconds, hz);
                            n++;
                        }
                        m.ClearSinceGameSeconds = -1;
                    }
                }
            }
            MigratedFields += n;
            return n;
        }

        private static long ToTicks(double seconds, int hz) => (long)Math.Round(seconds * hz);
    }
}
