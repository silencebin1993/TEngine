namespace GameLogic.Core
{
    /// <summary>
    /// ER2-INPUT-01 AC-UI-005 的战略速度入口。FG0-ARCH-01 起只是 <see cref="GameClock"/>（统一游戏时钟）的外观：
    /// 倍率、档位（0.5x / 1x / 2x / 3x，FGR-ARC-009 加 3x）都读写统一时钟，不再有第二份状态。
    /// 暂停也在统一时钟里（<see cref="GameClock.Paused"/>），整个世界同时暂停；旧细胞阶段仍用自己的 _paused（Demo 之前的产品，不接入世界）。
    /// </summary>
    public static class StrategyClock
    {
        /// <summary>允许的倍率档（与 <see cref="GameClock.Speeds"/> 是同一个数组）。</summary>
        public static float[] AllowedMultipliers => GameClock.Speeds;

        public static float SpeedMultiplier => GameClock.Speed;

        public static void SetSpeed(float multiplier) => GameClock.SetSpeed(multiplier);

        /// <summary>下一档（0.5x→1x→2x→3x→0.5x 循环）。</summary>
        public static void CycleSpeed() => GameClock.CycleSpeed();

        /// <summary>旧细胞阶段按帧缩放用（<paramref name="directLocked"/>=true 时锁 1x）。世界里的系统不用它：走固定步。</summary>
        public static float GetScaledDt(float rawDt, bool directLocked)
        {
            return directLocked ? rawDt : rawDt * GameClock.Speed;
        }

        /// <summary>新战役 / 回主菜单时把倍率复位到 1x（暂停与时间轴由 <see cref="GameClock.ResetSession"/> 复位）。</summary>
        public static void Reset()
        {
            GameClock.SetSpeed(1f);
        }
    }
}
