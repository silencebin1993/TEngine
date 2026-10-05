using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG6-DEF-01（FG06 第 6 章“炮塔（作为机器：蓝图、耐久、热量、目标模式）”）：炮塔的存档域，挂在 <see cref="RaidState.Turrets"/>。
    /// 唯一写入口 <see cref="Defense.TurretService"/>。耐久的真相是炮塔座建筑（<see cref="BuildingRecord.Health"/>，建成后与内核单位双向对账）；
    /// 热量、补给存量、炮口朝向随家园战斗内核快照进存档；本域存“这座炮塔是什么、怎么设置、打了多少”。
    /// 只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 没有炮塔。
    /// </summary>
    [Serializable]
    public sealed class TurretState
    {
        public TurretRecord[] Turrets = Array.Empty<TurretRecord>();
        /// <summary>下一个炮塔序号（内核单位的外部键，&gt; 0，不复用）。</summary>
        public int NextSerial = 1;
        /// <summary>建造模式里最近选的轻型 / 重型炮塔蓝图（放置时没有吸管设置就装它；玩家便利，不影响已建成的炮塔）。</summary>
        public string PlacementLight = string.Empty;
        public string PlacementHeavy = string.Empty;
        /// <summary>信号在哪座炮塔里（炮塔座建筑 ID；空 = 不在炮塔里）。读档后按它恢复接入（炮塔已不能接入时回到归还核心并说明）。</summary>
        public string UplinkTurretId = string.Empty;
        /// <summary>默认炮塔蓝图已经补进蓝图库（新档开局 / 旧档第一次读档，幂等；玩家归档或改名后不再补）。</summary>
        public bool DefaultsSeeded;
        /// <summary>默认重型炮塔蓝图（固定底盘 + 铸造重炮）已经补进蓝图库：重炮解锁之后才补（不提前送出没解锁的组件），只补一次。</summary>
        public bool HeavyDefaultSeeded;
        /// <summary>统计：全部炮塔累计击毁的敌对单位（拆掉的炮塔也算在内）。</summary>
        public int TotalKills;
    }

    /// <summary>FG6-DEF-01：一座炮塔（身份 = 炮塔座建筑 ID；内核单位的外部键 = <see cref="Serial"/>）。</summary>
    [Serializable]
    public sealed class TurretRecord
    {
        public string BuildingId = string.Empty;
        public int Serial;
        /// <summary>装的固定底盘蓝图与版本（换蓝图 = 改这两项，立即生效）。</summary>
        public string BlueprintId = string.Empty;
        public int BlueprintVersion;
        /// <summary>目标模式 = 内核 CombatTargetMode 的值（0 最近 / 1 最低耐久 / 2 最高威胁 / 3 精英优先 / 4 先打拆建筑的）。</summary>
        public int TargetMode;
        /// <summary>击毁数（FG06 第 4 节“炮塔可以改名，记录击杀数”；名字在炮塔座建筑的 CustomName）。</summary>
        public int KillCount;
        public int EliteKills;
        /// <summary>管线消费者句柄（与 <see cref="ConsumerFluids"/> 平行；管线内核的消费者随管线快照进存档，缓存 = 炮塔存着的流体）。</summary>
        public int[] ConsumerIds = Array.Empty<int>();
        public int[] ConsumerFluids = Array.Empty<int>();
        /// <summary>没有消费者时（没接管线 / 被摧毁 / 换了蓝图）炮塔自己留着的流体（毫升，与 <see cref="HeldFluids"/> 平行），重新接上时放回消费者缓存。</summary>
        public int[] HeldFluids = Array.Empty<int>();
        public long[] HeldMl = Array.Empty<long>();
    }
}
