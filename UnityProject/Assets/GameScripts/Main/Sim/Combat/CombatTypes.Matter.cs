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
}
