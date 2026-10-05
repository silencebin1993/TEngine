using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG6-DEF-02（FG06 第 6 章“防御建筑”）：屏障 / 闸门 / 护盾发生器 / 陷阱发射器的存档域，挂在 <see cref="RaidState.Defense"/>。
    /// 唯一写入口 <see cref="Defense.DefenseService"/>。耐久的真相是建筑记录（<see cref="BuildingRecord.Health"/>，建成后与内核结构单位双向对账）；
    /// 护盾值与累计读数随家园战斗内核快照（格式 10）进存档，本域存护盾状态机的状态与到期步、陷阱的固件 / 铺设方式 / 存着的流体。
    /// 只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 没有这些防御建筑。
    /// </summary>
    [Serializable]
    public sealed class DefenseState
    {
        public DefenseRecord[] Records = Array.Empty<DefenseRecord>();
        /// <summary>下一个序号（内核结构单位的外部键 / 护盾的外部键，&gt; 0，不复用）。</summary>
        public int NextSerial = 1;
        /// <summary>统计：护盾累计过载次数、陷阱累计铺设轮数（拆掉的也算）。</summary>
        public int TotalOverloads;
        public long TotalLays;

        // ── FG6-DEF-03：维修无人机站（FGR-DEF-014）。唯一写入口 Defense.RepairDroneService；只加字段、不升域版本（旧档没有 = 没有维修无人机站）──
        public RepairStationRecord[] Stations = Array.Empty<RepairStationRecord>();
        /// <summary>下一架无人机的序号（内核单位外部键 = −(1000000 + 序号)，不复用）。</summary>
        public int NextDroneSerial = 1;
        /// <summary>统计：维修无人机累计修好的耐久、用掉的维修件、被击落 / 坠毁的架数（拆掉的站也算）。</summary>
        public double TotalRepaired;
        public int TotalKitsUsed;
        public int TotalDronesLost;
    }

    /// <summary>FG6-DEF-03：一座维修无人机站（身份 = 建筑 ID）。无人机数组 = 现存的无人机（被击落的从数组里拿掉，站点按编制补充）。</summary>
    [Serializable]
    public sealed class RepairStationRecord
    {
        public string BuildingId = string.Empty;
        public DroneRecord[] Drones = Array.Empty<DroneRecord>();
        /// <summary>维修件余额（件，0～1 的零头）：修理按修好的耐久比例记账，不够一次时从共用库存取 1 件补进来——不丢零头、不多扣。</summary>
        public float KitCredit;
        /// <summary>正在补充的这一架已经走过的步数（只在站点运转且有电时累加；-1 = 没在补充）。补充开始时已扣废料。</summary>
        public int RespawnTicks = -1;
        /// <summary>正在补充的这一架开始时扣的废料（站点在补充中被摧毁 / 拆除时全额退回）。</summary>
        public int RespawnPaid;
        /// <summary>站点被摧毁过、出动的无人机都坠毁了：重建完工那一刻按满编恢复（含在重建造价里）。</summary>
        public bool Wrecked;
        /// <summary>没有维修件、无人机停工（只在变化时写原因、发一次通知）。</summary>
        public bool NoKits;
        /// <summary>上一次“缺维修件 / 被击落”通知的步（同一座站有冷却）。</summary>
        public long NotifyTick;
        // 读数（面板）
        public double Repaired;
        public int KitsUsed;
        public int Lost;
    }

    /// <summary>FG6-DEF-03：一架维修无人机。停在站里时不在战斗内核里；出动后是内核里的己方单位（耐久的真相在内核，存档前写回 <see cref="Hp"/>）。</summary>
    [Serializable]
    public sealed class DroneRecord
    {
        public int Serial;
        /// <summary>0 停在站里 / 1 飞往目标 / 2 修理中 / 3 返航。</summary>
        public int State;
        public float X;
        public float Y;
        public float Hp;
        /// <summary>目标：b:建筑 ID / c:x,y（一格传送带）；空 = 没有目标。</summary>
        public string Target = string.Empty;
        /// <summary>修传送带时还没落到整数点的修理量（传送带耐久按整数记）。</summary>
        public float Work;
    }

    /// <summary>FG6-DEF-02：一座防御建筑（身份 = 建筑 ID；内核结构单位 / 护盾的外部键 = <see cref="Serial"/>）。不用的字段保持默认值。</summary>
    [Serializable]
    public sealed class DefenseRecord
    {
        public string BuildingId = string.Empty;
        public int Serial;

        // ── 护盾（FGR-DEF-012）──
        /// <summary>护盾状态机的当前状态（fg.TbShieldState.id；空 = 还没建成过）。</summary>
        public string ShieldState = string.Empty;
        /// <summary>当前状态到期的世界步（&lt;= 0 = 不会自己结束）。按步序号判定，与观察 / 倍速无关。</summary>
        public long ShieldUntilTick;
        /// <summary>内核不在时（家园没载入 / 被摧毁）的护盾值（内核在时以内核为准，存档前写回）。</summary>
        public float ShieldHp;
        /// <summary>额外耗电档位（按 shield.power_band 取整后的档数；变了才重新结算电网）。</summary>
        public int ShieldLoadBand;
        /// <summary>上一次统计“最近承受的伤害”的累计吸收量与步序号（每秒伤害 = 差 ÷ 窗口）。</summary>
        public double ShieldLoadAbsorbed;
        public long ShieldLoadTick;
        public int ShieldOverloads;
        /// <summary>内核不在时的累计吸收量（读数）。</summary>
        public double ShieldAbsorbed;

        // ── 陷阱（FGR-DEF-013）──
        /// <summary>装的固件（fg.TbTrapProfile.firmwareId；空 = 没装）。</summary>
        public string TrapFirmware = string.Empty;
        /// <summary>铺设方式：0 一条线（沿朝向）/ 1 一片区域。</summary>
        public int TrapPattern;
        /// <summary>管线消费者句柄与流体（消费者随管线快照进存档，缓存 = 发射器存着的流体）。</summary>
        public int ConsumerId;
        public int ConsumerFluid;
        /// <summary>没有消费者时（没接管线 / 被摧毁 / 换了固件）发射器自己留着的流体（按种类，两数组平行；换回原来的固件时放回消费者缓存）。</summary>
        public int[] HeldFluids = Array.Empty<int>();
        public long[] HeldMl = Array.Empty<long>();
        /// <summary>累计铺设轮数（面板读数）。</summary>
        public long TrapLays;
    }
}
