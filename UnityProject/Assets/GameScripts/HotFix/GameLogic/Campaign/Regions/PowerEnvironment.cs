using System;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG4-ECO-04（FG04 第 3.3 节“太阳能阵列：白天供电 60，夜晚 0，沙暴时 -60%”）：太阳能的<b>昼夜 / 天气接口</b>。
    /// 本 Story 不新造昼夜系统（卡片补充说明）：昼夜由 FG7-ENV-01 接 <see cref="DaylightProvider"/>（FG-GAP-096），沙暴由 FG7-ENV-03 接 <see cref="SandstormProvider"/>（FG-GAP-097）。
    /// 没接时按“白天、无沙暴”算（系数 1）——太阳能阵列恒发 60，与“昼夜系统还没有”的现状一致，玩家看到的读数写明当前光照。
    /// 只读游戏状态的纯函数（同一游戏时刻结果相同）：电网每游戏秒查一次，系数变了才重新结算（HomeValleyPowerGrid.WorldStep），与观察无关。
    /// </summary>
    public static class PowerEnvironment
    {
        /// <summary>光照（0 = 夜晚 … 1 = 白天）。null = 昼夜系统还没接入（按白天）。FG7-ENV-01 负责赋值。</summary>
        public static Func<CampaignState, float> DaylightProvider;

        /// <summary>是否沙暴中。null = 天气系统还没接入（无沙暴）。FG7-ENV-03 负责赋值。</summary>
        public static Func<CampaignState, bool> SandstormProvider;

        /// <summary>昼夜接口是否已接入（读数里写“白天 / 夜晚”，没接入时也按白天写）。</summary>
        public static bool DaylightWired => DaylightProvider != null;

        /// <summary>这一刻的光照（0～1）。</summary>
        public static float Daylight(CampaignState state)
        {
            if (DaylightProvider == null || state == null)
            {
                return 1f;
            }
            float v = DaylightProvider(state);
            return float.IsNaN(v) ? 1f : Math.Max(0f, Math.Min(1f, v));
        }

        public static bool Sandstorm(CampaignState state) => SandstormProvider != null && state != null && SandstormProvider(state);

        /// <summary>沙暴时太阳能的系数（fg.TbHomeTuning power.solar.sandstorm_factor，初值 0.4 = “-60%”）。</summary>
        public static float SandstormFactor => GridContent.TryGetTuning("power.solar.sandstorm_factor", out float f) ? Math.Max(0f, Math.Min(1f, f)) : 0.4f;

        /// <summary>太阳能这一刻的发电系数 = 光照 ×（沙暴时 × 沙暴系数）。</summary>
        public static float SolarFactor(CampaignState state)
        {
            float f = Daylight(state);
            if (Sandstorm(state))
            {
                f *= SandstormFactor;
            }
            return f;
        }

        /// <summary>自检之间复位（不留注入的接口）。</summary>
        public static void ResetForTests()
        {
            DaylightProvider = null;
            SandstormProvider = null;
        }
    }
}
