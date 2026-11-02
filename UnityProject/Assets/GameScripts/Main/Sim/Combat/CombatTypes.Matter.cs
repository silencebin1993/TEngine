using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-MAT-01（FG19 FGR-EMG-080～083；FG19B 第 1、8 节；ADR-EMG-001 ⑤ B′ 方案）：物质层常量。
    /// 斑块 = 追加了物质字段的 <see cref="CombatZone"/>，放在独立列表 <see cref="CombatData.Patches"/>（不与读法区域混放，容量分开计），恒为中立；
    /// 内核只认物质码（= 端口码 − 400）与 <see cref="EmgMatter"/> 数值行（热更层按 fg.TbMatter 写），不认物质名与任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public static class EmgMatterConst
    {
        /// <summary>物质码 1～17（mat.oil 1 …… mat.sludge 17）；物质端口码 = 400 + 物质码（FG19 第 2.5 节）。</summary>
        public const int Count = 17;
        public const int PortBase = 400;
        public const byte Oil = 1, Water = 2, Coolant = 3, Cryo = 4, Acid = 5, Polymer = 6, Cured = 7, Molten = 8, Slag = 9, Fire = 10, Smoke = 11, Steam = 12,
            Debris = 13, Dust = 14, Ice = 15, Glass = 16, Sludge = 17;
        public const byte LayerGround = 0, LayerGas = 1;
        /// <summary>衰减方式（fg.TbMatter decayMode）：蒸干（存续最后 25% 半径线性缩到 0）/ 保持 / 扩散稀释（气体）/ 燃尽 / 吸热蒸发（液氮）/ 冷却（熔融金属；转冷渣是规则 E11，归 EMG2-RULE-01）。</summary>
        public const byte DecayEvaporate = 0, DecayKeep = 1, DecayDilute = 2, DecayBurn = 3, DecayAbsorb = 4, DecayCool = 5;
        /// <summary>斑块标志（<see cref="CombatZone.PFlags"/>）：常驻世界源（不衰减、不合并、不被挤掉，占世界源保留额）/ 被油覆盖（FG19B §1.4 第 12 条，规则引擎读）/ 长期留存（家园 / 前哨，EMG2-PERSIST-01）。</summary>
        public const byte FlagWorld = 1, FlagOilCovered = 2, FlagPersist = 4;
        /// <summary>
        /// EMG2-MAT-02（FGR-EMG-087 等价迁移）：从现有区域迁入的斑块——带着迁入时的区域载荷（<see cref="CombatZone.Dps"/> / StatusMask / 状态秒数 / 持续伤害 / 减速 / 易伤 /
        /// 受寒 / 阵营 / 来源种类 Kind / 阶段 Phase）按区域节拍结算（与迁移前逐字同一段代码），不走 fg.TbMatter 的通用接触 / 衰减 / 合并、不给弹体沾染；
        /// 物质码只用于识别（悬停 / 渲染 / evt.enter·leave / 冲洗与整片反应的“遇到的物质”）。按新规则改写（去掉本标志）归 EMG2-RULE-01 / REACT-02。
        /// </summary>
        public const byte FlagLegacy = 8;
        /// <summary>EMG2-MAT-02（ADR ⑤ 建筑受击聚合）：每步最多发布给热更层的设施受击条目数；超出的留在待发布表（按键合并），下一步优先，不丢。</summary>
        public const int StructHitsPerStep = 64;
        /// <summary>已发布、热更层还没取走的条目上限（没有消费者的地点不会无限增长：满了就留在待发布表里按键合并）。</summary>
        public const int StructHitsOutCap = 256;
        /// <summary>待发布表上限：满了再来新键就丢最旧的一条（计数 <see cref="EmgScalars.StructHitsDropped"/>）；有消费者（家园每步取走）时到不了。</summary>
        public const int StructHitsPendingCap = 256;
        /// <summary>设施受击条目标志：来源是明火（mat.fire / 燃烧残留）/ 来源是管线液洼（键 = 液洼号的相反数）。</summary>
        public const byte HitFire = 1, HitLeak = 2;
        /// <summary>容量初值（运行时取 CombatConfig 里热更层按 fg.TbHomeTuning emg.patch.* 写的值，≤ 0 用这里）。</summary>
        public const int DefaultCapacity = 1024;
        public const int DefaultReserveWorld = 96;
        public const int DefaultPerStep = 69;
        public const int DefaultSpawnPerStep = 32;
        public const int DefaultMaxNeighbors = 8;
        public const float DefaultTick = 0.25f;
        public const float DefaultMaxRadius = 8f;
        public const float DefaultSoftMergeRadius = 6f;
        public const float DefaultMinConc = 0.2f;
        /// <summary>活跃场容量 emg.field.capacity（FGR-EMG-202；场本身由 EMG2-FLD-01 生成，阶段 2 状态段本卡铺好）。</summary>
        public const int FieldCapacity = 64;
        /// <summary>单位接触最多结算几块（FG19B §8.3“每个 ≤ 4 块”）。</summary>
        public const int ContactPerUnit = 4;
        /// <summary>斑块种类码（与 <see cref="CombatZone.Kind"/> 的读法 / 场地 / 液洼 / 喷淋池 / 油迹区分开，只在 Patches 里用）。</summary>
        public const byte ZoneKindPatch = 7;
        /// <summary>渲染实例种类：40 + 纹样码（0～16），与区域 20～29 分开（CombatInstanced.shader）。</summary>
        public const float EffectKindPatch = 40f;
        /// <summary>热更层残骸崩解来源键（<see cref="CombatZone.Owner"/> = 0、本标志位于 <see cref="CombatZone.Phase"/>）：悬停写“来自 残骸崩解”。</summary>
        public const byte PhaseFromWreck = 1;

        public static byte MatterOfPort(int port) => port > PortBase && port <= PortBase + Count ? (byte)(port - PortBase) : (byte)0;
    }

    /// <summary>一种物质的内核数值行（fg.TbMatter；下标 = 物质码，0 号不用）。全部值类型，Burst 可读。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgMatter
    {
        public float Flammable, Conductive, Cooling, Corrosive, Viscous, Obscure, Slippery, Magnetic, Reflective;
        public float Life, LifePerAmount, LifeMax, Spread, CoatSeconds, TempInit, Hp, MergeOverlap, ContactDps, ContactSlow, EmitEvery;
        /// <summary>EMG2-MAT-02（fg.TbMatter structDps）：每秒对斑块范围里建筑 / 传送带 / 管线的伤害（内核按斑块聚合进设施受击脏列表，热更层按格结算）。</summary>
        public float StructDps;
        public int Color;
        public short Quota;
        /// <summary>沾染状态位（−1 = 不沾染）。</summary>
        public sbyte CoatBit;
        public byte Layer, Solid, FixedTemp, Decay, Emit, Pattern;
        public byte Pad0;

        public bool Valid => Life > 0f;

        /// <summary>这块斑块（份量 <paramref name="amount"/>）的默认存续秒（明火 6 + 0.3 × 份量，≤ 20）。</summary>
        public float LifeFor(float amount)
        {
            float l = Life + LifePerAmount * math.max(0f, amount);
            return LifeMax > 0f ? math.min(l, LifeMax) : l;
        }
    }

    /// <summary>
    /// EMG2-MAT-02（ADR-EMG-001 ⑤“建筑、传送带、管线（血量在热更层）”）：一条设施受击聚合条目。内核按键合并（同一斑块 / 同一液洼同一步合并为一条），
    /// 每步最多发布 <see cref="EmgMatterConst.StructHitsPerStep"/> 条、超出顺延不丢；热更层只处理取走的条目（O(本步变化条目)，不遍历斑块）。
    /// 键：斑块 Id（&gt; 0）；管线液洼 = 液洼号的相反数（&lt; 0，<see cref="EmgMatterConst.HitLeak"/>）。不进快照（每步派生，读档最多少结算一个斑块节拍）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgStructHit
    {
        public double2 Pos;
        public float Radius;
        /// <summary>累计伤害（耐久点；燃烧残留的液洼条目为 0，热更层按 logistics.leak.fire_dps 自己结算，与迁移前同一节奏）。</summary>
        public float Damage;
        public int Key;
        public byte Matter;
        public byte Flags;
        public short Pad;
    }

    /// <summary>
    /// EMG2-MAT-01（FGR-EMG-120～122 / 211；ADR ⑩ 版本 17）：活跃场的内核状态（推斥 / 牵引 / 涡旋 / 磁场 / 电晕 / 偏折……由 EMG2-FLD-01 生成与结算）。
    /// 本卡只把结构、容量与快照段铺好（阶段 2 状态段一次铺齐）：列表恒为空，读 ≤ 16 版快照时为空。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgField
    {
        public double2 Pos;
        public double Born;
        public double Until;
        public float Radius;
        public float Strength;
        public int Id;
        public int Owner;
        public int Chain;
        /// <summary>场端口码（fld.* 的码，fg.TbChannelPort）。</summary>
        public ushort Port;
        public byte Faction;
        public byte Depth;
    }

    /// <summary>
    /// EMG2-RULE-01（FG19 FGR-EMG-084 / 085 / 091～093；ADR-EMG-001 ⑥）：通道规则引擎常量。规则表 fg.TbChannelRule 由热更层编成 <see cref="EmgRule"/>（≤ <see cref="Capacity"/> 条），
    /// 内核只认 pairKind / 条件种类 / 结算方式这几个码与端口码、物质码、阈值，不认规则名与任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public static class EmgRuleConst
    {
        public const int Capacity = 256;
        /// <summary>沾染位在规则两侧码里的表示：<see cref="CoatBase"/> + 位号（端口码都 &lt; 1200，不冲突）。</summary>
        public const ushort CoatBase = 2000;
        public const byte PairMatterMatter = 1, PairMatterEnergy = 2, PairEventMatter = 3, PairCoatMatter = 4, PairPhaseEnergy = 5, PairPropField = 6, PairMatterEntity = 7;
        public const byte CondNone = 0, CondOverlap = 1, CondOverlapDry = 2, CondTempGe = 3, CondTempLt = 4, CondTempGeConcLt = 5, CondTempGeConcGe = 6, CondTempLeFor = 7,
            CondTempGtFor = 8, CondChargeGe = 9, CondChargeGeConcGe = 10, CondKineticGe = 11, CondExpire = 12, CondExpireTempLt = 13, CondOverheatIn = 14;
        /// <summary>结算方式：0 = 承接卡接上（本卡落表、占容量、不结算）。</summary>
        public const byte OpDeferred = 0, OpConvert = 1, OpMoveOver = 2, OpDilute = 3, OpDouse = 4, OpErode = 5, OpSplit = 6, OpSpawn = 7, OpBurst = 8, OpArc = 9, OpFragment = 10, OpVent = 11;
        public const byte MainS = 1, MainT = 2, MainB = 3, MainP = 4;
        public const byte SideNone = 0, SideDamage = 1, SideMult = 2;
        /// <summary>“不改温度”的哨兵（表里写 −999）。</summary>
        public const float KeepTemp = -998f;
        /// <summary>按物质码建的两张索引：[0, 18) = a 侧物质（matter_matter / matter_energy），[18, 36) = b 侧物质（event_matter）。</summary>
        public const int IndexSlots = (EmgMatterConst.Count + 1) * 2;
        // 传导 / 热（FG19B 第 1.4 节、第 10 节初值；运行时取 CombatConfig 里热更层写的值，≤ 0 用这里）
        public const int DefaultMaxHops = 6, DefaultMaxVisits = 256;
        public const float DefaultFalloff = 0.7f, DefaultQMin = 0.05f, DefaultUnitShare = 0.6f, DefaultPatchDecay = 0.5f, DefaultBigRadius = 8f, DefaultConductMinStrength = 0.3f;
        public const float DefaultAmbient = 20f, DefaultExchange = 0.4f, DefaultPatchGain = 10f, DefaultCoolRate = 40f, DefaultUnitOverheatScale = 300f, DefaultUnitGain = 0.02f;
        public const float DefaultAvoidMargin = 1f, DefaultAvoidWeight = 1.5f;
    }

    /// <summary>一条编好的通道规则（fg.TbChannelRule 一行）。全部值类型，Burst 可读；表数据，不进快照（读档沿用当前表）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgRule
    {
        /// <summary>计数键：named 的稳定键（none 时用本行 id 的稳定键）。可靠计数、evt.react 的槽位、因果链“规则”节点都按它。</summary>
        public int Key;
        /// <summary>本行 id 的稳定键（调试 / 悬停文本反查）。</summary>
        public int IdKey;
        public float C1, C2, P1, P2, P3, P4, Life, LifeMul, ByAmount, ByTemp, ByLife, ConsA, ConsB, SideValue;
        public ushort A, B;
        public short Priority;
        public byte Pair, Cond, Main, Op;
        /// <summary>产物物质码（S / T 类结算出的物质；P 类为 0）。</summary>
        public byte Product;
        /// <summary>伴生物质码（0 = 没有）。</summary>
        public byte By;
        public byte Side, Stage;
    }
}
