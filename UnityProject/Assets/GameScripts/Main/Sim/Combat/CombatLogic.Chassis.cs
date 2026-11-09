using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG3-CHS-01（FG19 FGR-EMG-140 / 141，ADR-EMG-001 ⑧）：底盘特性机制枚举与常量。与热更层 <c>GameLogic.Campaign.Emergence.EmgTraitKind</c> 同值（FgEmgChassisSelfCheck A 段逐个比对）。
    /// 内核只认机制、不认底盘 ID：“哪种底盘用哪条特性”只在 fg.TbChassisTrait（FGR-EMG-013）。
    /// </summary>
    public static class EmgTraitConst
    {
        public const byte None = 0, TrackDrag = 1, WheelTow = 2, HoverFloat = 3, FixedAnchor = 4, LeggedStomp = 5, AirDrop = 6, HullDraw = 7, Count = 8;
        /// <summary>chs.* 状态位（<see cref="CombatLogic.ChsMask"/>）。</summary>
        public const byte ChsGround = 1, ChsAirborne = 2, ChsMoving = 4, ChsWater = 8;
        /// <summary>本步位移超过这个距离（米）才算 chs.moving（“本步位移超阈值”，ADR ⑧）。</summary>
        public const double MoveEps = 1e-3;
        /// <summary>参数缺省（运行时取热更层按 fg.TbHomeTuning emg.trait.* 写进 <see cref="CombatData.EmgTraitParams"/> 的值，≤ 0 用这里）。</summary>
        public const float DefaultTrackStep = 1.5f, DefaultTrackShare = 0.1f, DefaultWheelShare = 0.3f, DefaultWheelSeconds = 12f;
        /// <summary>拖挂：在地面层斑块里停留多久带走一块（FGR-EMG-140“≥ 1 秒”）、随车斑块半径上限（“半径上限 2 米”）。规格原文的定值，不是调参键。</summary>
        public const float WheelDwell = 1f, WheelRadiusCap = 2f;
        /// <summary>拖带：一次分出的份量低于它就停（源斑块被拖空 / 太小，线到头）。shortcut: 临时定值，FG15-BAL-01 调参时如需可调再进 emg.trait.*。</summary>
        public const float TrackMinSplit = 0.02f;
        /// <summary>拖带分出的小斑块半径 = 步长 × 它（一条点线，相邻两块不重叠，FGR-EMG-140“连成一条线”）。</summary>
        public const float TrackDotRatio = 0.4f;
        /// <summary>哨戒桩 / 无人机电荷每秒保留比例（DEBT-EMG2REACT02-04；只作导体跳点，不放电）。shortcut: 临时定值，与单位电荷衰减不同源；需要时并进 fg.TbPhaseRule。</summary>
        public const float DroneChargeKeep = 0.5f;
        /// <summary>拖带 / 拖挂认的地面层液体与明火（与 tools/cell_tables/fgdata_emg.py 的 _GROUND 同一组物质：油、水、冷却液、液氮、酸、聚合物、熔融金属、明火、污泥）。</summary>
        public const uint GroundFluidMask = (1u << EmgMatterConst.Oil) | (1u << EmgMatterConst.Water) | (1u << EmgMatterConst.Coolant) | (1u << EmgMatterConst.Cryo)
                                            | (1u << EmgMatterConst.Acid) | (1u << EmgMatterConst.Polymer) | (1u << EmgMatterConst.Molten) | (1u << EmgMatterConst.Fire)
                                            | (1u << EmgMatterConst.Sludge);
        /// <summary>碾过会被注入动能碎裂的层（冰、玻化层；FGR-EMG-140）。</summary>
        public const uint ShatterMask = (1u << EmgMatterConst.Ice) | (1u << EmgMatterConst.Glass);
        /// <summary>冲刺 / 冲撞位移的原因码（evt.push Code；冲撞沿用击退原因 <see cref="EmgConst.PushCauseKnock"/>）。</summary>
        public const ushort PushCauseDash = EmgConst.PortChsMoving;

        /// <summary>冲刺参数缺省（运行时取 <see cref="CombatData.EmgTraitParams"/>[0] = emg.dash.speed / seconds / cooldown；下标 0 = 机制 None，借来放冲刺器参数）。</summary>
        public const float DefaultDashSpeed = 14f, DefaultDashSeconds = 0.4f, DefaultDashCooldown = 6f;
        /// <summary>运行态行标志：装着冲刺器（FG-GAP-176）。</summary>
        public const byte FlagDash = 1;

        /// <summary>EMG3-CHS-02 预留特性参数缺省（运行时取 fg.TbChassisTrait paramKeys 的 emg.trait.* 值）：震地间隔 / 溅出份量比例 / 扬尘份量；空投半径系数；取水排放系数。</summary>
        public const float DefaultStompStep = 0.8f, DefaultStompSplash = 0.15f, DefaultStompDust = 0.5f, DefaultAirRadius = 0.6f, DefaultHullAmount = 1.5f;
        /// <summary>震地扬起的粉尘 / 溅出小斑块的半径（米）。shortcut: 临时定值，多足底盘落地（FG17-CHS-01）调参时如需可调再进 emg.trait.*。</summary>
        public const float StompDustRadius = 1f, StompSplashMinRadius = 0.3f;

        public static bool IsAirborneKind(byte k) => k == HoverFloat || k == AirDrop;
        /// <summary>有运行态行的机制：拖带 / 拖挂（站位相关）与震地（Odo 借作“移动中累计秒数”，快照段 22 已有这一列，不升格式）。</summary>
        public static bool HasRun(byte k) => k == TrackDrag || k == WheelTow || k == LeggedStomp;
    }

    /// <summary>
    /// EMG3-CHS-01：一台带“与站位有关”特性（碾压拖带 / 拖挂）或装着冲刺器的单位的运行态（快照段 22）。只有这些单位有一行，按单位 ID 指向（单位压实不影响）。
    /// 随车斑块不在斑块表里（装在车上，不与世界互作），落下时才生成——存读档时天然不会复制（卡片负向）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgTraitRun
    {
        public double2 Last;
        public int Unit;
        /// <summary>拖带：正在拖的源斑块 Id（0 = 没有）。</summary>
        public int DragId;
        /// <summary>拖挂：正在停留的斑块 Id 与停留秒数（ADR ⑧ 的 TowSince）。</summary>
        public int DwellOn;
        /// <summary>拖挂：随车斑块取自哪块（驶入“另一块”才落下）与它的因果链。</summary>
        public int TowFrom;
        public int TowChain;
        public float Odo;
        public float Dwell;
        public float TowAmount;
        public float TowRadius;
        /// <summary>随车剩余秒（&gt; 0 且 TowMatter ≠ 0 = 车上有一块）。</summary>
        public float TowT;
        /// <summary>FG-GAP-176 冲刺：方向、剩余冲刺秒、冷却剩余秒。</summary>
        public float2 DashDir;
        public float DashT;
        public float DashCd;
        public byte Kind;
        public byte DragMatter;
        public byte TowMatter;
        /// <summary><see cref="EmgTraitConst.FlagDash"/>。</summary>
        public byte Flags;
    }

    /// <summary>
    /// EMG3-CHS-01（ADR ⑩ 版本 18 一次铺好阶段 3 状态段）：元程序运行态（循环计数、延后队列，快照段 23）。
    /// EMG3-META-01（FG19 第 3.4 节）接上执行：一条记录 = 到 <see cref="DueAt"/> 时按宿主执行一次节点 <see cref="Node"/> 里元程序 <see cref="Ordinal"/> 的下游段
    /// （<see cref="Mode"/>：循环 = 宿主存续期间周期执行，<see cref="Loop"/> = 已执行次数；重放 = 复制 / 延后到点再结算一次，<see cref="Mask"/> = 要执行的程序序号位）。
    /// 宿主还在时取它当时的位置，已结束时在 <see cref="Pos"/>（最后已知位置 / 结束点）执行。格式 18 段内只追加字段（短记录读到取缺省），不升版。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgMetaRun
    {
        public double DueAt;
        /// <summary>出手者单位 ID。</summary>
        public int Unit;
        public int Prog;
        public int Chain;
        public byte Node;
        public byte Ordinal;
        public ushort Loop;
        // ── EMG3-META-01 追加（段 23 记录尾部）──
        /// <summary>宿主稳定 ID（弹体 / 区域 / 单位 ID）与种类（<see cref="EmgEntKind"/>）。</summary>
        public int Host;
        public byte HostKind;
        /// <summary><see cref="EmgMetaConst"/>.Run*。</summary>
        public byte Mode;
        /// <summary>结算事件码（重放按它门控下游指令；循环 = 0，下游段不论各自的事件都执行）。</summary>
        public byte Ev;
        public byte Depth;
        public ulong Mask;
        public double2 Pos;
        public float2 Dir;
        /// <summary>结算目标单位 ID（命中目标；没有 = 0）。</summary>
        public int Other;
        public int Weapon;
        public float Damage;
        public byte Faction;
        public byte Carrier;
        public ushort PFlags;
        public float ZoneRadius;
        public float ZoneLife;
        /// <summary>循环锚点（格斗 / 力场 / 即时射弹，FGR-EMG-043）的结束时刻；0 = 跟随宿主存续。</summary>
        public double Until;
    }

    public static partial class CombatLogic
    {
        /// <summary>这个单位的底盘特性机制（没有 = 0）。</summary>
        internal static byte EmgTraitOf(ref CombatData d, int i) => d.EmgUnit.IsCreated && i >= 0 && i < d.EmgUnit.Length ? d.EmgUnit[i].Trait : EmgTraitConst.None;

        /// <summary>
        /// chs.airborne（FGR-EMG-141）：离地类特性（悬浮离地 / 飞行空投）或云端空中单位（<see cref="IsAir"/>，ADR ⑭ #8）。地面层物质（接触、沾染、相变累积、evt.enter、避险、
        /// 溅射到地面单位的规则产物）一律读它，所以悬浮与云端空中单位同一读法。<b>不改索敌</b>：“谁能被打到”仍读 <see cref="IsAir"/>（待用户复核的决策，证据 emg3-chs-01）。
        /// </summary>
        internal static bool ChsAirborne(ref CombatData d, int i) => IsAir(ref d, i) || EmgTraitConst.IsAirborneKind(EmgTraitOf(ref d, i));

        /// <summary>chs.* 状态位（内核派生，O(1)，按需读、不存）：着地 = 非离地；移动 = 本步位移 &gt; <see cref="EmgTraitConst.MoveEps"/>；
        /// 水面 = 站在水面上（正式水面地形 FG18-SEA-01 落地前只有自检的测试水域 <see cref="CombatData.WaterTest"/>，正式路径恒 0）。</summary>
        internal static byte ChsMask(ref CombatData d, int i)
        {
            if (i < 0 || i >= d.Count)
            {
                return 0;
            }
            byte m = ChsAirborne(ref d, i) ? EmgTraitConst.ChsAirborne : EmgTraitConst.ChsGround;
            if (math.distancesq(d.Pos[i], d.Prev[i]) > EmgTraitConst.MoveEps * EmgTraitConst.MoveEps)
            {
                m |= EmgTraitConst.ChsMoving;
            }
            if (d.WaterTest.z > 0f && (m & EmgTraitConst.ChsGround) != 0 && math.distance(d.Pos[i], new double2(d.WaterTest.x, d.WaterTest.y)) <= d.WaterTest.z)
            {
                m |= EmgTraitConst.ChsWater; // shortcut: 测试水域只有一圈、不进快照，水面地形（FG18-SEA-01）落地时换成“站在 ter.water 上”
            }
            return m;
        }

        /// <summary>EMG3-CHS-02 取水（FGR-EMG-140 trait.hull.draw）：带取水特性、此刻在水面上（chs.water）。离水立即为假（按需派生，不存状态）。</summary>
        internal static bool HullDrawing(ref CombatData d, int i) =>
            i >= 0 && i < d.Count && EmgTraitOf(ref d, i) == EmgTraitConst.HullDraw && (ChsMask(ref d, i) & EmgTraitConst.ChsWater) != 0;

        /// <summary>EMG3-CHS-02 空投（trait.air.drop）：出手者带空投特性。</summary>
        internal static bool AirDropping(ref CombatData d, int i) => i >= 0 && i < d.Count && EmgTraitOf(ref d, i) == EmgTraitConst.AirDrop;

        /// <summary>空投落区 / 排放的半径系数（emg.trait.air.radius；不是空投 = 1）。</summary>
        internal static float AirDropRadius(ref CombatData d, int i) =>
            AirDropping(ref d, i) ? TraitParam(ref d, EmgTraitConst.AirDrop, 0, EmgTraitConst.DefaultAirRadius) : 1f;

        /// <summary>
        /// 一发的积热（FGR-EMG-016 + 取水）：在水面上的取水单位，写 mat.water / mat.coolant 的指令那份热量（<see cref="CombatWeapon.DrawHeat"/>，编译期算好）不计。
        /// 其余单位逐字同旧（<see cref="CombatWeapon.HeatPerShot"/>）。O(1)。
        /// </summary>
        internal static float ShotHeat(ref CombatData d, int a, in CombatWeapon wp) =>
            wp.DrawHeat > 0f && HullDrawing(ref d, a) ? math.max(0f, wp.HeatPerShot - wp.DrawHeat) : wp.HeatPerShot;

        /// <summary>排放份量 / 半径按出手者特性改（空投半径 × emg.trait.air.radius；在水面上的取水单位排水 / 冷却液份量 × emg.trait.hull.amount）。</summary>
        internal static void EmgTraitEmit(ref CombatData d, int ownerSlot, byte matter, ref float radius, ref float amount)
        {
            if (ownerSlot < 0 || ownerSlot >= d.Count)
            {
                return;
            }
            byte kind = EmgTraitOf(ref d, ownerSlot);
            if (kind == EmgTraitConst.AirDrop)
            {
                radius *= TraitParam(ref d, EmgTraitConst.AirDrop, 0, EmgTraitConst.DefaultAirRadius);
            }
            else if (kind == EmgTraitConst.HullDraw && (matter == EmgMatterConst.Water || matter == EmgMatterConst.Coolant) && HullDrawing(ref d, ownerSlot))
            {
                amount *= TraitParam(ref d, EmgTraitConst.HullDraw, 0, EmgTraitConst.DefaultHullAmount);
            }
        }

        private static byte ChsBitOfPort(ushort port) => port switch
        {
            EmgConst.PortChsGround => EmgTraitConst.ChsGround,
            EmgConst.PortChsAirborne => EmgTraitConst.ChsAirborne,
            EmgConst.PortChsMoving => EmgTraitConst.ChsMoving,
            EmgConst.PortChsWater => EmgTraitConst.ChsWater,
            _ => 0,
        };

        /// <summary>
        /// 固件读 chs.*（FGR-EMG-141）：同一程序序号里有 chs.* condition 读端、而宿主单位此刻没有这个状态 → 这条固件本次不生效（返回 true）。
        /// 只认端口码，不认底盘 / 固件 ID。<paramref name="host"/> 不是单位（&lt; 0）时不拦。O(节点指令数)，只在事件唤起 / 根门控时。
        /// </summary>
        internal static bool EmgChsBlocked(ref CombatData d, int host, int first, int end, byte ordinal)
        {
            byte need = EmgChsNeed(ref d, first, end, ordinal);
            return need != 0 && host >= 0 && host < d.Count && (ChsMask(ref d, host) & need) != need;
        }

        /// <summary>同一程序序号里 chs.* condition 读端要求的状态位（多条按“且”合成一个掩码；没有 = 0，调用方据此早退出、不去找宿主）。O(节点指令数)。</summary>
        internal static byte EmgChsNeed(ref CombatData d, int first, int end, byte ordinal)
        {
            byte need = 0;
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ordinal && r.Role == EmgConst.RoleCondition)
                {
                    need |= ChsBitOfPort(r.Port);
                }
            }
            return need;
        }

        /// <summary>节点 [first, end) 上有没有 chs.* condition 读端（没有 = 调用方不必逐行 <see cref="EmgChsBlocked"/>，DEBT-EMG3CHS01-02）。O(节点指令数)。</summary>
        internal static bool EmgChsAny(ref CombatData d, int first, int end)
        {
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Role == EmgConst.RoleCondition && ChsBitOfPort(r.Port) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static float TraitParam(ref CombatData d, byte kind, int k, float fallback)
        {
            if (!d.EmgTraitParams.IsCreated || kind >= d.EmgTraitParams.Length)
            {
                return fallback;
            }
            float v = d.EmgTraitParams[kind][k];
            return v > 0f ? v : fallback;
        }

        private static int TraitRunOf(ref CombatData d, int unitId)
        {
            for (int r = 0; r < d.TraitRun.Length; r++)
            {
                if (d.TraitRun[r].Unit == unitId)
                {
                    return r;
                }
            }
            return -1;
        }

        /// <summary>热更层写特性（KernelApi）：同步运行态行（拖带 / 拖挂才有行；换成别的特性时把车上的随车斑块就地落下，不吞份量）。</summary>
        internal static void EmgSetTrait(ref CombatData d, int i, byte kind)
        {
            EmgUnitState st = d.EmgUnit[i];
            st.Trait = kind;
            d.EmgUnit[i] = st;
            int r = TraitRunOf(ref d, d.Id[i]);
            byte flags = r >= 0 ? d.TraitRun[r].Flags : (byte)0;
            SyncRun(ref d, i, r, kind, flags);
        }

        /// <summary>FG-GAP-176：装 / 卸冲刺器（热更层按装配写，与 SetUnitWeapon 同时机）。</summary>
        internal static void EmgSetDash(ref CombatData d, int i, bool on)
        {
            int r = TraitRunOf(ref d, d.Id[i]);
            byte flags = r >= 0 ? d.TraitRun[r].Flags : (byte)0;
            flags = on ? (byte)(flags | EmgTraitConst.FlagDash) : (byte)(flags & ~EmgTraitConst.FlagDash);
            SyncRun(ref d, i, r, d.EmgUnit[i].Trait, flags);
        }

        /// <summary>按特性 + 标志建 / 改 / 删运行态行；换特性或删行时把车上的随车斑块就地落下（不吞份量）。</summary>
        private static void SyncRun(ref CombatData d, int i, int r, byte kind, byte flags)
        {
            bool need = EmgTraitConst.HasRun(kind) || flags != 0;
            if (need)
            {
                if (r < 0)
                {
                    // 按单位 Id 升序插入：内存表顺序 = 快照段 22 的写出顺序，读档后 StepChassis 的处理先后不变（审查 P2）
                    int id = d.Id[i], at = d.TraitRun.Length;
                    while (at > 0 && d.TraitRun[at - 1].Unit > id)
                    {
                        at--;
                    }
                    d.TraitRun.InsertRangeWithBeginEnd(at, at + 1);
                    d.TraitRun[at] = new EmgTraitRun { Unit = id, Kind = kind, Flags = flags, Last = d.Pos[i] };
                    return;
                }
                EmgTraitRun x = d.TraitRun[r];
                if (x.Kind != kind)
                {
                    TowDrop(ref d, ref x, d.Pos[i]);
                    x.Odo = 0f;
                    x.DragId = 0;
                    x.DragMatter = 0;
                    x.Kind = kind;
                }
                x.Flags = flags;
                if ((flags & EmgTraitConst.FlagDash) == 0)
                {
                    x.DashT = 0f;
                }
                d.TraitRun[r] = x;
            }
            else if (r >= 0)
            {
                EmgTraitRun x = d.TraitRun[r];
                TowDrop(ref d, ref x, d.Pos[i]);
                d.TraitRun.RemoveAt(r);
            }
        }


        /// <summary>按 Id 找活斑块的下标（−1 = 没有 / 已消失）。O(斑块数)，只在拖带分出 / 拖挂取块的那一步。</summary>
        private static int PatchSlotOfId(ref CombatData d, int id)
        {
            if (id <= 0)
            {
                return -1;
            }
            for (int j = 0; j < d.Patches.Length; j++)
            {
                if (d.Patches[j].EmgId == id && PatchAlive(d.Patches[j]))
                {
                    return j;
                }
            }
            return -1;
        }

        /// <summary>点 <paramref name="p"/> 处最近的地面层液体 / 明火斑块（非固态、非迁入；−1 = 没有）。O(本格登记数)。</summary>
        private static int GroundFluidAt(ref CombatData d, double2 p, uint mask)
        {
            if (d.EmgS[0].PatchLive <= 0)
            {
                return -1; // 场上没有斑块：一条比较（120 台轮式车每步都会问，性能门禁“数量为 0 早退出”）
            }
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            PatchesAt(ref d, p, false, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
            int best = -1;
            for (int k = 0; k < hits.Length && best < 0; k++)
            {
                CombatZone z = d.Patches[hits[k].x];
                if (z.Layer == EmgMatterConst.LayerGround && (mask & (1u << z.Matter)) != 0u && MatterOf(ref d, z.Matter).Solid == 0)
                {
                    best = hits[k].x;
                }
            }
            hits.Dispose();
            return best;
        }

        /// <summary>从斑块 <paramref name="j"/> 取走 <paramref name="ratio"/> 的份量（面积守恒缩半径；取空就删）。返回取走的份量。</summary>
        private static float TakeFromPatch(ref CombatData d, int j, float ratio)
        {
            CombatZone z = d.Patches[j];
            float take = z.Amount * math.saturate(ratio);
            if (!(take > 0f))
            {
                return 0f;
            }
            float left = z.Amount - take;
            if (left < EmgTraitConst.TrackMinSplit)
            {
                KillPatch(ref d, j);
                return z.Amount;
            }
            z.Radius *= math.sqrt(left / z.Amount);
            z.Amount = left;
            d.Patches[j] = z;
            EmgScalars es = d.EmgS[0];
            es.HashDirty = 1;
            d.EmgS[0] = es;
            return take;
        }

        private static void TowDrop(ref CombatData d, ref EmgTraitRun x, double2 at)
        {
            if (x.TowMatter != 0 && x.TowAmount > 0f)
            {
                EmgRequestPatch(ref d, x.TowMatter, at, math.max(0.3f, x.TowRadius), x.TowAmount, 0f, x.Unit, math.max(0, x.TowChain), 0);
            }
            x.TowMatter = 0;
            x.TowAmount = 0f;
            x.TowRadius = 0f;
            x.TowT = 0f;
            x.TowFrom = 0;
            x.TowChain = 0;
            x.Dwell = 0f;
            x.DwellOn = 0;
        }

        /// <summary>
        /// EMG3-CHS-01：碾压拖带与拖挂（与“站在哪块斑块上”有关的特性，ADR ⑧ 机制落点）。只碰运行态表里的行：O(带特性单位 × 本格斑块)；表空时 O(1)（性能门禁“数量为 0 早退出”）。
        /// 按模拟时间走（dt），暂停不走、倍速只改每帧步数，0.5x～3x 结果一致。
        /// </summary>
        internal static void StepChassis(ref CombatData d, float dt)
        {
            if (d.TraitRun.Length == 0)
            {
                return;
            }
            bool patches = EmgPatchesOn(ref d);
            if (patches)
            {
                EmgHashEnsure(ref d);
            }
            for (int r = d.TraitRun.Length - 1; r >= 0; r--)
            {
                EmgTraitRun x = d.TraitRun[r];
                int u = d.SlotOf(x.Unit);
                if (u < 0 || !d.IsAlive(u))
                {
                    TowDrop(ref d, ref x, x.Last); // 车没了：车上的那块落在最后的位置（不吞份量）
                    d.TraitRun.RemoveAt(r);
                    continue;
                }
                if ((x.Flags & EmgTraitConst.FlagDash) != 0)
                {
                    StepDash(ref d, u, ref x, dt);
                }
                double2 pos = d.Pos[u];
                x.Last = pos;
                if (x.Kind == EmgTraitConst.LeggedStomp)
                {
                    StepLeggedStomp(ref d, u, ref x, dt, patches);
                    d.TraitRun[r] = x;
                    continue;
                }
                if (!patches || ChsAirborne(ref d, u))
                {
                    d.TraitRun[r] = x;
                    continue;
                }
                if (x.Kind == EmgTraitConst.TrackDrag)
                {
                    StepTrackDrag(ref d, u, ref x, dt);
                }
                else if (x.Kind == EmgTraitConst.WheelTow)
                {
                    StepWheelTow(ref d, u, ref x, dt);
                }
                d.TraitRun[r] = x;
            }
        }

        /// <summary>
        /// EMG3-CHS-02 震地（FGR-EMG-140 trait.legged.stomp）：移动中每 emg.trait.legged.step 秒（模拟时间，暂停不走、倍速只改每帧步数）对脚下发一次 evt.impact
        /// （宿主 = 自己，Value = 速度，Value2 = 质量）；脚下的冰 / 玻化层注入足以碎裂的动能（= 两者耐久的较大值，E18 / E19 在它的节拍结算）；
        /// 站在地面层液体斑块里 → 从该斑块分出 emg.trait.legged.splash 份量，溅成一块同物质小斑块落在斑块外缘（朝脚的方向）；不在液体里 → 扬起 emg.trait.legged.dust 份量的粉尘。
        /// 停住时计时清零（“移动中”）；离地不震。计时借 <see cref="EmgTraitRun.Odo"/>（快照段 22 已有，存读档后节拍连续）。
        /// </summary>
        private static void StepLeggedStomp(ref CombatData d, int u, ref EmgTraitRun x, float dt, bool patches)
        {
            double moved = math.distance(d.Pos[u], d.Prev[u]);
            if (moved <= EmgTraitConst.MoveEps || ChsAirborne(ref d, u))
            {
                x.Odo = 0f;
                return;
            }
            float step = TraitParam(ref d, EmgTraitConst.LeggedStomp, 0, EmgTraitConst.DefaultStompStep);
            x.Odo += dt;
            if (x.Odo < step - 1e-4f)
            {
                return;
            }
            x.Odo = math.max(0f, x.Odo - step);
            double2 p = d.Pos[u];
            float speed = (float)(moved / math.max(dt, 1e-4f));
            EmgCountImpact(ref d);
            EmgUnitEvent(ref d, EmgEventKind.Impact, u, 0, speed, EmgUnitMass(ref d, u), 0, p, EmgEntKind.None);
            if (!patches || !d.IsAlive(u))
            {
                return;
            }
            if (ShatterUnder(ref d, p))
            {
                EmgKineticAt(ref d, p, math.max(MatterOf(ref d, EmgMatterConst.Ice).Hp, MatterOf(ref d, EmgMatterConst.Glass).Hp));
            }
            int liquid = GroundFluidAt(ref d, p, EmgTraitConst.GroundFluidMask & ~(1u << EmgMatterConst.Fire));
            if (liquid < 0)
            {
                EmgRequestPatch(ref d, EmgMatterConst.Dust, p, EmgTraitConst.StompDustRadius, TraitParam(ref d, EmgTraitConst.LeggedStomp, 2, EmgTraitConst.DefaultStompDust), 0f, d.Id[u], 0, 0);
                return;
            }
            CombatZone z = d.Patches[liquid];
            float take = TakeFromPatch(ref d, liquid, TraitParam(ref d, EmgTraitConst.LeggedStomp, 1, EmgTraitConst.DefaultStompSplash));
            if (take < EmgTraitConst.TrackMinSplit)
            {
                return;
            }
            double2 dir = p - z.Pos;
            dir = math.lengthsq(dir) > 1e-8 ? math.normalize(dir) : math.normalize(d.Pos[u] - d.Prev[u]);
            float sr = math.max(EmgTraitConst.StompSplashMinRadius, z.Radius * math.sqrt(take / math.max(z.Amount, 1e-4f)));
            EmgRequestPatch(ref d, z.Matter, z.Pos + dir * (z.Radius + sr + 0.05), sr, take, 0f, d.Id[u], z.EmgChain, z.EmgDepth);
        }

        /// <summary>
        /// 碾压拖带：移动中经过地面层液体 / 明火斑块就“挂上”它；每走 emg.trait.track.step 米从源斑块分出 emg.trait.track.share 的份量，在身后（上一拍的位置）落一块同物质小斑块，
        /// 连成点线（燃烧区被拖成火线）；分出量低于 <see cref="EmgTraitConst.TrackMinSplit"/> 或源斑块没了就停。碾过冰 / 玻化层时按撞击口径（质量 × 速度² × emg.impact.k）注入动能使其碎裂。
        /// 停住不拖（卡片负向）。
        /// </summary>
        private static void StepTrackDrag(ref CombatData d, int u, ref EmgTraitRun x, float dt)
        {
            double moved = math.distance(d.Pos[u], d.Prev[u]);
            if (moved <= EmgTraitConst.MoveEps)
            {
                return;
            }
            float step = TraitParam(ref d, EmgTraitConst.TrackDrag, 0, EmgTraitConst.DefaultTrackStep);
            float share = TraitParam(ref d, EmgTraitConst.TrackDrag, 1, EmgTraitConst.DefaultTrackShare);
            int here = GroundFluidAt(ref d, d.Pos[u], EmgTraitConst.GroundFluidMask);
            if (here >= 0 && d.Patches[here].EmgId != x.DragId && (x.DragId == 0 || d.Patches[here].Matter != x.DragMatter || PatchSlotOfId(ref d, x.DragId) < 0))
            {
                x.DragId = d.Patches[here].EmgId; // 换到另一种物质的斑块（或原来的源没了）：改拖这一块
                x.DragMatter = d.Patches[here].Matter;
            }
            bool crossed = false;
            x.Odo += (float)moved;
            if (x.Odo >= step)
            {
                x.Odo -= step * math.floor(x.Odo / step);
                crossed = true;
            }
            if (!crossed)
            {
                return;
            }
            if (x.DragId != 0)
            {
                int src = PatchSlotOfId(ref d, x.DragId);
                float want = src >= 0 ? d.Patches[src].Amount * share : 0f;
                if (src < 0 || want < EmgTraitConst.TrackMinSplit)
                {
                    x.DragId = 0;
                    x.DragMatter = 0;
                }
                else
                {
                    CombatZone sz = d.Patches[src];
                    float take = TakeFromPatch(ref d, src, share);
                    EmgRequestPatch(ref d, sz.Matter, d.Prev[u], step * EmgTraitConst.TrackDotRatio, take, 0f, d.Id[u], sz.EmgChain, sz.EmgDepth);
                }
            }
            // 冰 / 玻化层：撞击口径的动能落到脚下的固态斑块（E18 / E19 在它的节拍结算碎裂）
            float speed = (float)(moved / math.max(dt, 1e-4f));
            EmgKineticAt(ref d, d.Pos[u], EmgUnitMass(ref d, u) * speed * speed * d.EmgEntParams[0].KineticImpact * (ShatterUnder(ref d, d.Pos[u]) ? 1f : 0f));
        }

        /// <summary>
        /// FG-GAP-176 冲刺器（FG19A func_dash：chs.ground condition → en.kinetic inject；撞击 = 本机质量 × 速度²）：只在执行玩家的攻击命令时用——目标在武器射程外、
        /// 但在冲刺距离（速度 × 时长）内，且本机着地（离地不冲，读 chs.ground）、冷却好了 → 朝目标冲 emg.dash.seconds 秒。冲刺位移走统一位移入口 <see cref="EmgDisplaceUnit"/>
        /// （按冲刺速度结算撞击：撞上单位 / 残骸 / 障碍发 evt.impact、扣撞击伤害，位移发 evt.push）；撞上即停。玩家没下攻击命令时不冲（不替玩家补动作）。
        /// </summary>
        private static void StepDash(ref CombatData d, int u, ref EmgTraitRun x, float dt)
        {
            x.DashCd = math.max(0f, x.DashCd - dt);
            if (IsImmobile(ref d, u) || d.Has(u, CombatUnitFlags.Possessed))
            {
                x.DashT = 0f; // 冻结 / 电麻痹“这一步什么都不做”、受控机不执行命令（与 StepPlayerUnit 同口径）；冲刺途中被冻住当场停
                return;
            }
            float4 p = d.EmgTraitParams.IsCreated ? d.EmgTraitParams[0] : float4.zero;
            float speed = p.x > 0f ? p.x : EmgTraitConst.DefaultDashSpeed;
            float secs = p.y > 0f ? p.y : EmgTraitConst.DefaultDashSeconds;
            float cd = p.z > 0f ? p.z : EmgTraitConst.DefaultDashCooldown;
            bool ground = (ChsMask(ref d, u) & EmgTraitConst.ChsGround) != 0;
            if (x.DashT > 0f)
            {
                if (!ground || !EmgEntOn(ref d))
                {
                    x.DashT = 0f;
                    return;
                }
                double2 from = d.Pos[u];
                double2 want = from + (double2)(x.DashDir * (speed * dt));
                double2 got = EmgDisplaceUnit(ref d, u, from, want, u, EmgTraitConst.PushCauseDash, speed);
                d.Pos[u] = got;
                x.DashT = math.max(0f, x.DashT - dt);
                if (math.distancesq(got, want) > 1e-6)
                {
                    x.DashT = 0f; // 撞上 / 被挡：冲刺结束
                }
                return;
            }
            CombatCommand cmd = d.Cmd[u];
            if (x.DashCd > 0f || !ground || !EmgEntOn(ref d) || cmd.Kind != CombatCommandKind.Attack)
            {
                return;
            }
            int t = d.SlotOf(cmd.Target);
            if (t < 0 || !d.IsAlive(t) || d.Faction[t] == d.Faction[u])
            {
                return;
            }
            double2 to = d.Pos[t] - d.Pos[u];
            double dist = math.length(to);
            double gap = dist - d.Radius[t] - d.Radius[u];
            if (gap <= cmd.AttackRange || gap > cmd.AttackRange + speed * secs || dist < 1e-6)
            {
                return;
            }
            x.DashDir = (float2)(to / dist);
            x.DashT = secs;
            x.DashCd = cd;
        }

        private static bool ShatterUnder(ref CombatData d, double2 p)
        {
            if (d.EmgS[0].PatchLive <= 0)
            {
                return false;
            }
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            PatchesAt(ref d, p, false, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
            bool any = false;
            for (int k = 0; k < hits.Length && !any; k++)
            {
                any = (EmgTraitConst.ShatterMask & (1u << d.Patches[hits[k].x].Matter)) != 0u;
            }
            hits.Dispose();
            return any;
        }

        /// <summary>
        /// 拖挂：在一块地面层液体 / 明火斑块里停留 ≥ 1 秒，带走 emg.trait.wheel.share 的份量（半径上限 2 米）装在车上；随车 emg.trait.wheel.seconds 秒到期，或驶入另一块斑块时落下。
        /// 车上已有一块时不再带第二块（同时最多 1 块，卡片负向）。
        /// </summary>
        private static void StepWheelTow(ref CombatData d, int u, ref EmgTraitRun x, float dt)
        {
            int here = GroundFluidAt(ref d, d.Pos[u], EmgTraitConst.GroundFluidMask);
            int hereId = here >= 0 ? d.Patches[here].EmgId : 0;
            if (x.TowMatter != 0)
            {
                x.TowT -= dt;
                if (x.TowT <= 0f || (hereId != 0 && hereId != x.TowFrom))
                {
                    TowDrop(ref d, ref x, d.Pos[u]);
                }
                return;
            }
            if (hereId == 0)
            {
                x.Dwell = 0f;
                x.DwellOn = 0;
                return;
            }
            if (x.DwellOn != hereId)
            {
                x.DwellOn = hereId;
                x.Dwell = 0f;
            }
            x.Dwell += dt;
            if (x.Dwell + 1e-4f < EmgTraitConst.WheelDwell)
            {
                return;
            }
            CombatZone z = d.Patches[here];
            float share = TraitParam(ref d, EmgTraitConst.WheelTow, 0, EmgTraitConst.DefaultWheelShare);
            float take = TakeFromPatch(ref d, here, share);
            if (!(take > 0f))
            {
                return;
            }
            x.TowMatter = z.Matter;
            x.TowAmount = take;
            x.TowRadius = math.min(EmgTraitConst.WheelRadiusCap, z.Radius * math.sqrt(math.saturate(share)));
            x.TowT = TraitParam(ref d, EmgTraitConst.WheelTow, 1, EmgTraitConst.DefaultWheelSeconds);
            x.TowFrom = hereId;
            x.TowChain = z.EmgChain;
            x.Dwell = 0f;
        }

        /// <summary>
        /// DEBT-EMG2REACT02-04：哨戒桩 / 无人机电荷每秒衰减（只作 C04 绝缘闪络的导体跳点）。没有带电无人机时一条长度比较。
        /// </summary>
        internal static void StepDroneCharge(ref CombatData d, float dt)
        {
            float keep = math.pow(EmgTraitConst.DroneChargeKeep, dt);
            for (int k = 0; k < d.Drones.Length; k++)
            {
                CombatDrone dr = d.Drones[k];
                if (dr.Charge > 0f)
                {
                    dr.Charge = dr.Charge * keep < 1e-3f ? 0f : dr.Charge * keep;
                    d.Drones[k] = dr;
                }
            }
        }
    }
}
