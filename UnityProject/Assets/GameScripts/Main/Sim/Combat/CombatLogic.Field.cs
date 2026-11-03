using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-FLD-01（FG19 FGR-EMG-112、120～122；FG19B 第 5 节 fg.TbFieldCarrierRule；ADR-EMG-001 ⑤“弹体 × 斑块 / 场”、⑦“位移分段”）：场与载体互作常量。
    /// 规则表 20 行、行序 = FG19B 第 5 节表序，内核按下标读（与 fg.TbPhaseRule 同一做法），不认规则 ID 与任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public static class EmgFieldConst
    {
        public const int RuleCount = 20;
        public const int CoronaPayload = 0, CoronaUnit = 1, MagnetPayload = 2, MagnetUnit = 3, DeflectPayload = 4, ForcePayload = 5, ForceUnit = 6, FirePayload = 7,
            OilPayload = 8, WaterPayload = 9, CryoPayload = 10, AcidPayload = 11, PolymerPayload = 12, DustPayload = 13, SmokePayload = 14, SteamPayload = 15,
            ObscureUnit = 16, GlassPayload = 17, IcePayload = 18, SolidAbsorb = 19;
        /// <summary>阵营关系（场 / 物质的阵营 与 载荷阵营）：任意 / 同阵营 / 敌对。</summary>
        public const byte FacAny = 0, FacOwn = 1, FacHostile = 2;
        /// <summary>执行者：0 = 承接卡（落表不结算）、1 = 本卡内核按下标结算、2 = 由物质沾染通用路径结算（EMG2-MAT-01 EmgProjMatter / EmgCoatOnHit）。</summary>
        public const byte ImplDeferred = 0, ImplKernel = 1, ImplCoat = 2;
        /// <summary>场端口码（fg.TbChannelPort fld.*，FG19 第 2.6 节）。</summary>
        public const ushort PortPull = 701, PortPush = 702, PortVortex = 703, PortMagnet = 704, PortDeflect = 705, PortCorona = 706;
        /// <summary>弹体 FieldOnce 位（“每发弹体每种沾染只记一次”，FGR-EMG-120）：带电 / 被场反射 / 烟失锁 / 蒸汽失锁 / 玻化层反弹 / 冰面反弹 / 已挂拖尾。</summary>
        public const byte OnceCharged = 1, OnceDeflect = 2, OnceSmoke = 4, OnceSteam = 8, OnceGlass = 16, OnceIce = 32, OnceTrail = 64, OnceIgnite = 128;
        /// <summary>牵引 / 磁场离场心这么近就不再拉（防止在场心来回抖）。</summary>
        public const float PullKeep = 0.5f;
        /// <summary>无人机载体的“小场”半径上限（FG19A 第 1.4 节电磁场控表“无人机命中处小牵引 / 磁 / 涡旋场（3 米）”）。shortcut: 内核常量未入表（同 PullKeep），FG15-BAL-01 调参时再进 fg.TbFieldCarrierRule。</summary>
        public const float DroneFieldRadius = 3f;

        public static bool IsFieldPort(int port) => port >= PortPull && port <= PortCorona;

        /// <summary>缺省值 = FG19B 第 5 节（力的大小 FG19B 写“—”，按“位移 = 力 ÷ 质量”取初值，FG15-BAL-01 再调）；热更层按 fg.TbFieldCarrierRule 覆盖。</summary>
        public static EmgFieldRule Default(int k) => k switch
        {
            CoronaPayload => new EmgFieldRule { Amount = 0.25f, Once = 1, Faction = FacOwn, Impl = ImplKernel },
            CoronaUnit => new EmgFieldRule { Amount = 0.3f, Faction = FacHostile, Impl = ImplKernel },
            MagnetPayload => new EmgFieldRule { Amount = 90f, Faction = FacAny, FactionAlt = FacAny, Impl = ImplKernel },
            MagnetUnit => new EmgFieldRule { Amount = 16f, Param = 0.2f, Faction = FacHostile, FactionAlt = FacAny, Impl = ImplKernel },
            DeflectPayload => new EmgFieldRule { Amount = 1f, Param = 3f, Once = 1, Faction = FacHostile, Impl = ImplKernel }, // Param = 被击偏折的内置冷却秒（FG19A 第 43 行）
            ForcePayload => new EmgFieldRule { Amount = 40f, Faction = FacHostile, FactionAlt = FacAny, Impl = ImplKernel },
            ForceUnit => new EmgFieldRule { Amount = 16f, Faction = FacHostile, FactionAlt = FacAny, Impl = ImplKernel },
            FirePayload => new EmgFieldRule { Amount = 2f, Param = 6f, Once = 1, Impl = ImplKernel },
            WaterPayload => new EmgFieldRule { Once = 1, Impl = ImplKernel },
            CryoPayload => new EmgFieldRule { Amount = 0.3f, Once = 1, Impl = ImplKernel },
            SmokePayload => new EmgFieldRule { Amount = 0.5f, Once = 1, Impl = ImplKernel },
            SteamPayload => new EmgFieldRule { Amount = 0.3f, Once = 1, Impl = ImplKernel },
            GlassPayload => new EmgFieldRule { Amount = 0.4f, Once = 1, Impl = ImplKernel },
            IcePayload => new EmgFieldRule { Amount = 0.25f, Once = 1, Impl = ImplKernel },
            OilPayload => new EmgFieldRule { Amount = 200f, Once = 1, Impl = ImplKernel },
            DustPayload => new EmgFieldRule { Amount = 300f, Once = 1, Impl = ImplKernel },
            PolymerPayload => new EmgFieldRule { Amount = 0.6f, Param = 1f, Once = 1, Impl = ImplKernel },
            ObscureUnit => new EmgFieldRule { Amount = 3f, Param = 6f, Impl = ImplKernel },
            AcidPayload => new EmgFieldRule { Once = 1, Impl = ImplCoat },
            SolidAbsorb => new EmgFieldRule { Param = 1.5f, Once = 1, Impl = ImplKernel }, // Param = 冷渣掩护距离（其后 1.5 米内的单位，EMG2-PERSIST-01）
            _ => new EmgFieldRule { Once = 1, Impl = ImplDeferred },
        };
    }

    /// <summary>一条编好的场 × 载体规则（fg.TbFieldCarrierRule 一行）。表数据，不进快照（读档沿用当前表）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgFieldRule
    {
        /// <summary>主幅度（注电量 / 每秒累积量 / 转向度每秒 / 力 / 反射比例 / 失锁秒 / 拖尾间距米……按行）。</summary>
        public float Amount;
        /// <summary>副参数（磁化每秒 / 拖尾块数）。</summary>
        public float Param;
        public byte Once, Faction, FactionAlt, Impl;
    }

    public static partial class CombatLogic
    {
        private static EmgFieldRule FieldRule(ref CombatData d, int k) => d.EmgFieldRules.IsCreated && k >= 0 && k < d.EmgFieldRules.Length ? d.EmgFieldRules[k] : EmgFieldConst.Default(k);

        private static bool FieldLive(in EmgFieldRule r) => r.Impl == EmgFieldConst.ImplKernel;

        private static bool FieldRelation(byte rel, CombatFaction field, CombatFaction other) => rel switch
        {
            EmgFieldConst.FacOwn => field == other,
            EmgFieldConst.FacHostile => field != other,
            _ => true,
        };

        /// <summary>
        /// 生成一个活跃场（FGR-EMG-112）。同一来源同一场端口：<paramref name="refresh"/>（部件的持续场）= 移到新位置并续期；否则同一步同一处（1 米内）只生成一次（同一次出手打中多个目标不叠场）。
        /// 满 emg.field.capacity（64）时新场不生效、计数 FieldsCapped（预览与调试层可见，FG19 第 18 节）。返回是否生效。
        /// </summary>
        internal static bool EmgSpawnField(ref CombatData d, int port, double2 pos, float radius, float seconds, int ownerId, CombatFaction faction, int chain, byte depth, bool refresh)
        {
            if (!EmgFieldConst.IsFieldPort(port) || !(radius > 0f) || !(seconds > 0f) || !math.isfinite(radius) || !math.isfinite(seconds) || !math.all(math.isfinite(pos)))
            {
                return false;
            }
            double now = d.Scalars[0].Time;
            for (int i = 0; i < d.Fields.Length; i++)
            {
                EmgField f = d.Fields[i];
                if (ownerId == 0 || f.Owner != ownerId || f.Port != port)
                {
                    continue;
                }
                if (refresh)
                {
                    f.Pos = pos;
                    f.Radius = radius;
                    f.Until = math.max(f.Until, now + seconds);
                    d.Fields[i] = f;
                    return true;
                }
                if (f.Born >= now && math.distancesq(f.Pos, pos) < 1.0)
                {
                    return true;
                }
            }
            EmgScalars es = d.EmgS[0];
            if (d.Fields.Length >= EmgMatterConst.FieldCapacity)
            {
                es.FieldsCapped++;
                d.EmgS[0] = es;
                return false;
            }
            int id = math.max(1, es.NextFieldId);
            es.NextFieldId = id + 1;
            es.FieldsBorn++;
            d.EmgS[0] = es;
            d.Fields.Add(new EmgField
            {
                Pos = pos, Born = now, Until = now + seconds, Radius = radius, Strength = 1f, Id = id, Owner = ownerId, Chain = chain, Port = (ushort)port,
                Faction = (byte)faction, Depth = depth,
            });
            return true;
        }

        /// <summary>声明轨 fld.* 写端（动词 Field）：半径 = 声明幅度、存续 = 声明 param2（FG19A 第 1.4 节“4 米，0.5 秒”）；缺值取 2 米 / 1 秒。</summary>
        private static bool EmgFieldFromInstr(ref CombatData d, in EmgInstr x, double2 pos, int ownerId, CombatFaction faction, int chain, byte depth)
        {
            float r = x.Amount > 0f ? x.Amount : 2f;
            float s = x.Param2 > 0f ? x.Param2 : 1f;
            return EmgSpawnField(ref d, x.Port, pos, r, s, ownerId, faction, chain, depth, false);
        }

        /// <summary>
        /// 事件门控的场写端（EmgRootGated）。被击（evt.hurt）× 宿主载体（FG19A 第 1.4 节偏折的载体表）：射弹 = 场心偏向攻击者半个半径（朝攻击者一侧展开）；
        /// 格斗 = 场在本机处，场内的近身攻击者被弹到场外；无人机 = 场在离攻击者最近的一架本机无人机处（没有无人机时在本机处）；
        /// 力场 = 半径取本机力场半径；布区 = 场落在本机与攻击者正中、存续取本机布区时长。其余事件在事件位置。按事件端口 × 载体判，不认内容 ID（FGR-EMG-013）。
        /// </summary>
        private static bool EmgGatedField(ref CombatData d, in EmgInstr x, in EmgEvent e, in EmgCtx host)
        {
            float r = x.Amount > 0f ? x.Amount : 2f;
            float s = x.Param2 > 0f ? x.Param2 : 1f;
            double now = d.Scalars[0].Time;
            // 修复轮：偏折场的内置冷却（fieldrule_deflect_payload.param 秒，FG19A 第 43 行“内置冷却 3 秒”）按宿主记——冷却中再挨打不展开、不弹开，持续挨打不会挤满 64 个场位
            int hs = x.Port == EmgFieldConst.PortDeflect ? d.SlotOf(host.OwnerId) : -1;
            float cd = hs >= 0 ? FieldRule(ref d, EmgFieldConst.DeflectPayload).Param : 0f;
            bool cdOn = cd > 0f && d.EmgUnit.IsCreated && hs < d.EmgUnit.Length;
            long tick = (long)math.floor(now * 10.0);
            ushort cq = cdOn ? d.EmgUnit[hs].FieldCdQ : (ushort)0;
            if (cq != 0 && ((tick - (cq - 1)) % 65535 + 65535) % 65535 < (long)math.ceil(cd * 10f))
            {
                return false;
            }
            double2 at = e.Pos;
            int os = e.Kind == EmgEventKind.Hurt && e.Other != 0 ? d.SlotOf(e.Other) : -1;
            if (os >= 0)
            {
                double2 to = d.Pos[os] - e.Pos;
                double len = math.length(to);
                switch ((CombatCarrier)host.Carrier)
                {
                    case CombatCarrier.Projectile:
                        at += len > 1e-6 ? to / len * (r * 0.5) : double2.zero;
                        break;
                    case CombatCarrier.Melee:
                        if (len < r)
                        {
                            Displace(ref d, os, e.Pos, -(float)(r - len), 0.0, host.Owner, EmgConst.PushCauseKnock);
                        }
                        break;
                    case CombatCarrier.Summon:
                        double best = double.MaxValue;
                        for (int i = 0; i < d.Drones.Length; i++)
                        {
                            CombatDrone dr = d.Drones[i];
                            double q = dr.Owner == host.OwnerId ? math.distancesq(dr.Pos, d.Pos[os]) : double.MaxValue;
                            if (q < best)
                            {
                                best = q;
                                at = dr.Pos;
                            }
                        }
                        break;
                    case CombatCarrier.Aura:
                        r = host.ZoneRadius > 0f ? host.ZoneRadius : r;
                        break;
                    case CombatCarrier.Field:
                        at += to * 0.5;
                        s = host.ZoneLife > 0f ? host.ZoneLife : s;
                        break;
                }
            }
            bool ok = EmgSpawnField(ref d, x.Port, at, r, s, host.OwnerId, (CombatFaction)host.Faction, e.Chain, e.Depth, false);
            if (ok && cdOn)
            {
                EmgUnitState us = d.EmgUnit[hs];
                us.FieldCdQ = (ushort)(tick % 65535 + 1);
                d.EmgUnit[hs] = us;
            }
            return ok;
        }

        /// <summary>
        /// 命中写端的场 × 宿主载体（FG19A 第 1.4 节电磁场控 5 载体表：牵引 / 磁聚 / 涡流 / 冲击波）：射弹 = 命中点、声明半径；格斗 = 身前（本机朝主目标、至多半个半径处）；
        /// 无人机 = 命中点的小场（半径 ≤ 3 米）；力场 = 以本机为心、半径取本机力场半径、同一宿主同一端口只留一个场（每次脉冲续期，力场“变成”这个场）；
        /// 布区 = 落区中心、半径与存续取落区的、同样只留一个续期。只按端口 × 载体判，不认内容 ID（FGR-EMG-013）；力场一次脉冲打中多个目标仍只有一个场。
        /// </summary>
        private static void EmgHitField(ref CombatData d, in EmgInstr x, int t, in EmgCtx c)
        {
            float r = x.Amount > 0f ? x.Amount : 2f;
            float s = x.Param2 > 0f ? x.Param2 : 1f;
            double2 at = d.Pos[t];
            int os = d.SlotOf(c.OwnerId);
            bool single = false;
            switch ((CombatCarrier)c.Carrier)
            {
                case CombatCarrier.Melee when os >= 0:
                {
                    int mt = c.MainTarget != 0 ? d.SlotOf(c.MainTarget) : -1;
                    double2 to = d.Pos[mt >= 0 ? mt : t] - d.Pos[os];
                    double len = math.length(to);
                    at = d.Pos[os] + (len > 1e-6 ? to / len * math.min(len, r * 0.5) : double2.zero);
                    break;
                }
                case CombatCarrier.Summon:
                    r = math.min(r, EmgFieldConst.DroneFieldRadius);
                    break;
                case CombatCarrier.Aura when os >= 0:
                    at = d.Pos[os];
                    r = c.ZoneRadius > 0f ? c.ZoneRadius : r;
                    single = true;
                    break;
                case CombatCarrier.Field:
                    if (c.EntKind == EmgEntKind.Zone)
                    {
                        for (int i = 0; i < d.Zones.Length; i++)
                        {
                            if (d.Zones[i].EmgId == c.Ent)
                            {
                                at = d.Zones[i].Pos;
                                break;
                            }
                        }
                    }
                    r = c.ZoneRadius > 0f ? c.ZoneRadius : r;
                    s = c.ZoneLife > 0f ? c.ZoneLife : s;
                    single = true;
                    break;
            }
            EmgSpawnField(ref d, x.Port, at, r, s, c.OwnerId, (CombatFaction)c.Faction, c.Chain, c.Depth, single);
        }

        /// <summary>同一条声明（序号）有没有 trigger 读端：有的由事件门控（EmgRootGated / 子节点触发）执行，命中注能处不再重复生成。</summary>
        private static bool EmgOrdinalTriggered(ref CombatData d, int first, int end, byte ordinal)
        {
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ordinal && r.Role == EmgConst.RoleTrigger)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>部件的场（fg.TbChannelDecl 部件行写 fld.* spawn，热更层写进武器 FieldPort / FieldRadius）：每次出手续期一个持续场（格斗在身前、力场在本机、其余在目标处）。</summary>
        internal static void EmgWeaponField(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            if (wp.FieldPort == 0 || !(wp.FieldRadius > 0f) || a < 0 || a >= d.Count)
            {
                return;
            }
            double2 at = d.Pos[a];
            if (wp.Reading.Carrier == CombatCarrier.Melee && t >= 0)
            {
                double2 to = d.Pos[t] - d.Pos[a];
                double len = math.length(to);
                at += len > 1e-6 ? to / len * (wp.FieldRadius * 0.5) : double2.zero;
            }
            else if (wp.Reading.Carrier != CombatCarrier.Aura && t >= 0)
            {
                at = d.Pos[t];
            }
            float sec = math.max(0.3f, EffectiveCooldown(wp) * 1.25f);
            EmgSpawnField(ref d, wp.FieldPort, at, wp.FieldRadius, sec, d.Id[a], (CombatFaction)d.Faction[a], 0, 0, true);
        }

        private static float FieldRadiusNow(in EmgField f, double now)
        {
            if (f.Port != EmgFieldConst.PortPush)
            {
                return f.Radius;
            }
            double life = f.Until - f.Born; // 推斥场 = 扩张环（FG19A 第 1.4 节第 44 行“0.8 秒扩到 3 米”）
            return life > 1e-6 ? f.Radius * (float)math.clamp((now - f.Born) / life, 0.25, 1.0) : f.Radius;
        }

        /// <summary>场对一点的受力方向：推斥向外、牵引 / 磁场向心、涡旋 = 向心 + 切向（边转边卷向中心）。</summary>
        private static double2 FieldDir(in EmgField f, double2 p, out double dist)
        {
            double2 rel = p - f.Pos;
            dist = math.length(rel);
            double2 o = dist > 1e-6 ? rel / dist : new double2(0, 1);
            return f.Port switch
            {
                EmgFieldConst.PortPush => o,
                EmgFieldConst.PortVortex => math.normalize(-o + new double2(-o.y, o.x)),
                _ => -o,
            };
        }

        /// <summary>
        /// 每步（StepWrecks 之前）：到期的场移除；场对单位、残骸、无人机的作用（FGR-EMG-112 / 121，fg.TbFieldCarrierRule target = 单位 / 残骸）。
        /// 位移 = 力 ÷ 质量（米 / 秒，磁场再乘磁性）；单位走 CanDisplace + 档案可位移（固定底盘、炮塔、结构、首领不动），途中撞障碍 / 残骸 / 单位按这个速度结算 evt.impact；
        /// 哨戒桩（定点无人机）插地不动。两个场同时作用时速度相加。O(场 × (单位 + 残骸 + 无人机))，场 ≤ 64，没有场时 O(1)。
        /// </summary>
        internal static void StepFields(ref CombatData d, float dt)
        {
            double now = d.Scalars[0].Time;
            EmgScalars es = d.EmgS[0];
            int n = 0;
            for (int i = 0; i < d.Fields.Length; i++)
            {
                EmgField f = d.Fields[i];
                if (now >= f.Until)
                {
                    es.FieldsExpired++;
                    continue;
                }
                d.Fields[n++] = f;
            }
            d.Fields.ResizeUninitialized(n);
            d.EmgS[0] = es;
            if (n == 0 || !(dt > 0f))
            {
                return;
            }
            if (d.Patches.Length > 0 && EmgPatchesOn(ref d))
            {
                for (int j = 0; j < n; j++)
                {
                    EmgField mf = d.Fields[j];
                    if (mf.Port == EmgFieldConst.PortMagnet && mf.Swept == 0)
                    {
                        mf.Swept = 1; // 修复轮：每个磁场只扫一次（原“Born 在最近一步内”会让步内展开的磁场连扫两步）
                        d.Fields[j] = mf;
                        EmgMagSweep(ref d, mf); // F02 磁扫：磁场展开后的第一个场步把重叠的金属碎屑吸起成破片
                    }
                }
            }
            EmgFieldRule fu = FieldRule(ref d, EmgFieldConst.ForceUnit), mu = FieldRule(ref d, EmgFieldConst.MagnetUnit), cu = FieldRule(ref d, EmgFieldConst.CoronaUnit);
            bool ent = EmgEntOn(ref d);
            int pushes = 0;
            int annihilate = EmgAnnihilateRule(ref d);
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u))
                {
                    continue;
                }
                double2 pos = d.Pos[u];
                CombatFaction uf = (CombatFaction)d.Faction[u];
                double2 v = double2.zero;
                float mass = 0f, mag = -1f;
                int pusher = 0;
                ushort cause = 0;
                int movable = -1;
                for (int j = 0; j < n; j++)
                {
                    EmgField f = d.Fields[j];
                    double r = FieldRadiusNow(f, now) + d.Radius[u];
                    if (math.distancesq(pos, f.Pos) > r * r)
                    {
                        continue;
                    }
                    CombatFaction ff = (CombatFaction)f.Faction;
                    if (f.Port == EmgFieldConst.PortDeflect)
                    {
                        // EMG2-REACT-02（F01 湮灭）：低完整度单位在敌对偏折场里被压溃起爆；敌我同一条（FGR-EMG-160）
                        if (annihilate >= 0 && FieldRelation(EmgFieldConst.FacHostile, ff, uf) && EmgAnnihilate(ref d, u, d.EmgRules[annihilate], f))
                        {
                            break;
                        }
                        continue;
                    }
                    if (f.Port == EmgFieldConst.PortCorona)
                    {
                        if (FieldLive(cu) && FieldRelation(cu.Faction, ff, uf))
                        {
                            EmgAddAcc(ref d, u, EmgPhaseConst.Charge, cu.Amount * dt, f.Owner);
                        }
                        continue;
                    }
                    bool magnet = f.Port == EmgFieldConst.PortMagnet;
                    EmgFieldRule rule = magnet ? mu : fu;
                    if (!FieldLive(rule) || !FieldRelation(rule.Faction, ff, uf))
                    {
                        continue;
                    }
                    if (magnet && rule.Param > 0f)
                    {
                        EmgAddAcc(ref d, u, EmgPhaseConst.Magnetize, rule.Param * dt, f.Owner);
                    }
                    if (movable < 0)
                    {
                        movable = CanDisplace(ref d, u) && EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, u)).Displace != 0 ? 1 : 0;
                    }
                    if (movable == 0)
                    {
                        continue;
                    }
                    double2 dir = FieldDir(f, pos, out double dist);
                    if (f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex && dist < EmgFieldConst.PullKeep)
                    {
                        continue;
                    }
                    if (mass <= 0f)
                    {
                        mass = math.max(0.1f, EmgUnitMass(ref d, u));
                    }
                    float force = rule.Amount * f.Strength;
                    if (magnet)
                    {
                        mag = mag < 0f ? EmgUnitProps(ref d, u).Magnetic : mag;
                        force *= mag;
                    }
                    v += dir * (force / mass);
                    pusher = pusher == 0 ? f.Owner : pusher;
                    cause = cause == 0 ? f.Port : cause; // 修复轮：evt.push 原因 = 第一个起作用的场端口（与残骸一侧同口径，不再一律记推斥）
                }
                double sp = math.length(v);
                if (sp < 1e-6)
                {
                    continue;
                }
                double2 want = pos + v * dt;
                d.Pos[u] = ent ? EmgDisplaceUnit(ref d, u, pos, want, d.SlotOf(pusher), cause, (float)sp) : MoveCollide(ref d, u, pos, want);
                pushes++;
            }
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck k = d.Wrecks[w];
                if (!(k.Hp > 0f))
                {
                    continue;
                }
                float2 fv = float2.zero;
                int by = 0;
                ushort cause = 0;
                float kmag = -1f;
                for (int j = 0; j < n; j++)
                {
                    EmgField f = d.Fields[j];
                    double r = FieldRadiusNow(f, now) + k.Radius;
                    if (f.Port == EmgFieldConst.PortCorona || f.Port == EmgFieldConst.PortDeflect || math.distancesq(k.Pos, f.Pos) > r * r)
                    {
                        continue;
                    }
                    bool magnet = f.Port == EmgFieldConst.PortMagnet;
                    EmgFieldRule rule = magnet ? mu : fu;
                    if (!FieldLive(rule) || !FieldRelation(rule.FactionAlt, (CombatFaction)f.Faction, (CombatFaction)f.Faction))
                    {
                        continue; // 残骸没有阵营：只看 FactionAlt 是不是“任意”（FG19B：残骸 any）
                    }
                    double2 dir = FieldDir(f, k.Pos, out double dist);
                    if (f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex && dist < EmgFieldConst.PullKeep)
                    {
                        continue;
                    }
                    float force = rule.Amount * f.Strength;
                    if (magnet)
                    {
                        kmag = kmag < 0f ? EmgWreckProps(ref d, k).Magnetic : kmag;
                        force *= kmag;
                    }
                    fv += (float2)(dir * (force / math.max(0.1f, k.Mass)));
                    by = by == 0 ? f.Owner : by;
                    cause = f.Port;
                }
                if (math.lengthsq(fv) < 1e-8f)
                {
                    continue;
                }
                // 被推铲推着的残骸（速度更大）只被场带偏一点；否则按场速度滑行，离场后 0.25 秒内停住
                float cur = math.length(k.Vel), add = math.length(fv);
                k.Vel = cur > add ? k.Vel + fv * (dt * 4f) : fv;
                k.Decel = math.max(k.Decel, math.length(k.Vel) * 4f);
                k.PushBy = by;
                k.PushCause = cause;
                d.Wrecks[w] = k;
                pushes++;
            }
            for (int i = 0; i < d.Drones.Length; i++)
            {
                CombatDrone dr = d.Drones[i];
                if (dr.Anchored != 0)
                {
                    continue; // 哨戒桩插地，不被场推动（FGR-EMG-112）
                }
                double2 v = double2.zero;
                EmgEntityProps dp = default;
                bool props = false;
                for (int j = 0; j < n; j++)
                {
                    EmgField f = d.Fields[j];
                    double r = FieldRadiusNow(f, now);
                    if (f.Port == EmgFieldConst.PortCorona || f.Port == EmgFieldConst.PortDeflect || math.distancesq(dr.Pos, f.Pos) > r * r)
                    {
                        continue;
                    }
                    bool magnet = f.Port == EmgFieldConst.PortMagnet;
                    EmgFieldRule rule = magnet ? mu : fu;
                    if (!FieldLive(rule) || !FieldRelation(rule.Faction, (CombatFaction)f.Faction, dr.Faction))
                    {
                        continue;
                    }
                    if (!props)
                    {
                        dp = EmgDroneProps(ref d, dr);
                        props = true;
                    }
                    double2 dir = FieldDir(f, dr.Pos, out double dist);
                    if (f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex && dist < EmgFieldConst.PullKeep)
                    {
                        continue;
                    }
                    v += dir * (rule.Amount * f.Strength * (magnet ? dp.Magnetic : 1f) / math.max(0.1f, dp.Mass));
                }
                if (math.lengthsq(v) > 1e-12)
                {
                    dr.Pos += v * dt;
                    d.Drones[i] = dr;
                    pushes++;
                }
            }
            if (d.Patches.Length > 0 && EmgPatchesOn(ref d))
            {
                pushes += EmgFieldGas(ref d, n, now, dt, fu);
            }
            es = d.EmgS[0];
            es.FieldPushes += pushes;
            d.EmgS[0] = es;
        }

        /// <summary>
        /// 修复轮（FGR-EMG-112“推斥、牵引、涡旋场也推动气体层斑块，涡旋把粉尘聚浓”；FG19B 第 9 节第 4 条 / 第 12 节 #4）：气体层斑块（烟、蒸汽、粉尘、液氮雾……）被推斥 / 牵引 / 涡旋场整体推动，
        /// 速度 = 力 ÷ 质量（力取 fieldrule_force_unit，气体没有阵营，同残骸只看 FactionAlt；气体的质量 = 份量，至少 1）；涡旋再把云团以同一向心速度收紧到初始半径，浓度回到 (R0 ÷ 半径)²（≤ 1），
        /// 粉尘因此能重新浓到尘爆阈值（E05 / E17）。磁场、电晕、偏折不推气体；常驻世界源与旧区域不动。O(场 × 斑块)，只在有场且斑块层开着时。返回被推动的块数。
        /// </summary>
        private static int EmgFieldGas(ref CombatData d, int n, double now, float dt, in EmgFieldRule fu)
        {
            if (!FieldLive(fu))
            {
                return 0;
            }
            int moved = 0;
            for (int k = 0; k < d.Patches.Length; k++)
            {
                CombatZone z = d.Patches[k];
                if (!PatchAlive(z) || IsLegacy(z) || PatchWorld(z) || z.Layer != EmgMatterConst.LayerGas || now >= z.Until)
                {
                    continue;
                }
                double2 v = double2.zero;
                double curl = 0.0;
                float mass = math.max(1f, z.Amount);
                for (int j = 0; j < n; j++)
                {
                    EmgField f = d.Fields[j];
                    if (f.Port != EmgFieldConst.PortPull && f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex)
                    {
                        continue;
                    }
                    double r = FieldRadiusNow(f, now) + z.Radius;
                    if (math.distancesq(z.Pos, f.Pos) > r * r || !FieldRelation(fu.FactionAlt, (CombatFaction)f.Faction, (CombatFaction)f.Faction))
                    {
                        continue;
                    }
                    double2 dir = FieldDir(f, z.Pos, out double dist);
                    double sp = fu.Amount * f.Strength / mass;
                    if (f.Port == EmgFieldConst.PortVortex)
                    {
                        curl += sp;
                    }
                    if (f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex && dist < EmgFieldConst.PullKeep)
                    {
                        continue;
                    }
                    v += dir * sp;
                }
                if (math.lengthsq(v) < 1e-12 && curl <= 0.0)
                {
                    continue;
                }
                z.Pos += v * dt;
                if (curl > 0.0 && z.R0 > 0f && z.Radius > z.R0)
                {
                    z.Radius = math.max(z.R0, z.Radius - (float)(curl * dt));
                    z.Conc = math.max(z.Conc, math.min(1f, (z.R0 / z.Radius) * (z.R0 / z.Radius)));
                }
                d.Patches[k] = z;
                moved++;
            }
            if (moved > 0)
            {
                EmgScalars es = d.EmgS[0];
                es.HashDirty = 1;
                es.GasPushed += moved;
                d.EmgS[0] = es;
            }
            return moved;
        }

        /// <summary>
        /// F02 磁扫（FG19B 第 2.8 节 reaction_magsweep；FGR-EMG-112“磁场把燃烧碎屑吸成火团”）：磁场展开时与它重叠的非固态、磁性 ≥ 阈值（0.6，即金属碎屑）的斑块被吸起——
        /// 每 2 份 1 片、最多 8 片，以 14 米 / 秒飞向场心、沿途命中；碎屑温度 ≥ 300 时破片带火。消耗对应份量（吸空即消失），按规则稳定键计一次（与 P01 / P02 同一做法：规则行结算在本卡，计数走 EmgRuleSettle）。
        /// </summary>
        private static void EmgMagSweep(ref CombatData d, in EmgField f)
        {
            int ri = -1;
            for (int i = 0; i < d.EmgRules.Length && ri < 0; i++)
            {
                EmgRule r = d.EmgRules[i];
                ri = r.Pair == EmgRuleConst.PairPropField && r.B == EmgFieldConst.PortMagnet ? i : -1;
            }
            float thr = ri >= 0 && d.EmgRules[ri].C1 > 0f ? d.EmgRules[ri].C1 : 0.6f;
            double now = d.Scalars[0].Time;
            sbyte fireBit = MatterOf(ref d, EmgMatterConst.Fire).CoatBit;
            for (int k = 0; k < d.Patches.Length; k++)
            {
                CombatZone z = d.Patches[k];
                if (!PatchAlive(z) || IsLegacy(z) || now >= z.Until)
                {
                    continue;
                }
                EmgMatter m = MatterOf(ref d, z.Matter);
                if (m.Solid != 0 || m.Magnetic < thr || math.distance(z.Pos, f.Pos) > f.Radius + z.Radius)
                {
                    continue;
                }
                int pieces = math.min(8, (int)math.floor(z.Amount / 2f));
                if (pieces <= 0)
                {
                    continue;
                }
                double2 to = f.Pos - z.Pos;
                double len = math.length(to);
                float2 dir = len > 1e-6 ? (float2)(to / len) : new float2(0f, 1f);
                bool hot = z.Temp >= 300f && fireBit >= 0 && fireBit < 31;
                for (int p = 0; p < pieces; p++)
                {
                    float a = math.radians((p - (pieces - 1) * 0.5f) * 6f); // 6° 间隔的窄扇，汇向场心
                    float2 dv = new float2(dir.x * math.cos(a) - dir.y * math.sin(a), dir.x * math.sin(a) + dir.y * math.cos(a));
                    int before = d.Projectiles.Length;
                    EmgSpawnFragment(ref d, z.Pos, dv, f.Owner, (CombatFaction)f.Faction, (byte)math.min(EmgConst.MaxDepth, f.Depth + 1), f.Chain, 6f, 14f, (float)len + f.Radius);
                    if (hot && d.Projectiles.Length > before)
                    {
                        CombatProjectile fp = d.Projectiles[d.Projectiles.Length - 1];
                        fp.Coat |= 1u << fireBit;
                        d.Projectiles[d.Projectiles.Length - 1] = fp;
                    }
                }
                z.Amount -= pieces * 2f;
                if (!(z.Amount > 0.01f))
                {
                    z.Until = now;
                }
                d.Patches[k] = z;
                EmgScalars es = d.EmgS[0];
                es.HashDirty = 1;
                es.MagSweeps++;
                d.EmgS[0] = es;
                if (ri >= 0)
                {
                    EmgRuleSettle(ref d, d.EmgRules[ri], f.Chain, f.Depth, f.Owner, z.Pos, z.EmgId);
                }
            }
        }

        /// <summary>
        /// 弹体本步线段经过的活跃场（FGR-EMG-120；fg.TbFieldCarrierRule target = 弹体 / 无人机子弹 / 破片）：电晕 → 带电（命中注电）、磁场 → 弹道转向场心（× 磁性）、
        /// 偏折 → 反射并改为场方阵营、牵引 / 推斥 / 涡旋 → 速度改变（力 ÷ 质量）。抛射弹在空中不受场影响。返回 true = 本步被反射（调用方按新阵营保留弹体）。O(活跃场)。
        /// </summary>
        internal static bool EmgProjFields(ref CombatData d, ref CombatProjectile pr, double2 next, bool airborne, float dt)
        {
            if (d.Fields.Length == 0 || airborne)
            {
                return false;
            }
            double now = d.Scalars[0].Time;
            bool frag = (pr.Flags & EmgProjFlags.Fragment) != 0;
            EmgEntityProps props = default;
            bool haveProps = false;
            EmgScalars es = d.EmgS[0];
            bool reflected = false;
            for (int j = 0; j < d.Fields.Length && !reflected; j++)
            {
                EmgField f = d.Fields[j];
                double r = FieldRadiusNow(f, now) + pr.Radius;
                if (math.distancesq(next, f.Pos) > r * r && SegmentCircleT(pr.Prev, next, f.Pos, (float)r) < 0)
                {
                    continue;
                }
                CombatFaction ff = (CombatFaction)f.Faction;
                switch (f.Port)
                {
                    case EmgFieldConst.PortCorona:
                    {
                        EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.CoronaPayload);
                        if (FieldLive(rule) && (pr.FieldOnce & EmgFieldConst.OnceCharged) == 0 && FieldRelation(rule.Faction, ff, pr.Faction))
                        {
                            pr.FieldOnce |= EmgFieldConst.OnceCharged;
                            es.ProjCharged++;
                        }
                        break;
                    }
                    case EmgFieldConst.PortDeflect:
                    {
                        EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.DeflectPayload);
                        if (!frag && FieldLive(rule) && (pr.FieldOnce & EmgFieldConst.OnceDeflect) == 0 && FieldRelation(rule.Faction, ff, pr.Faction) && !EmgEvadesDeflect(ref d, pr))
                        {
                            EmgReflectCore(ref d, ref pr, f.Owner, ff, rule.Amount);
                            pr.FieldOnce |= EmgFieldConst.OnceDeflect;
                            es = d.EmgS[0];
                            es.ProjReflected++;
                            reflected = true;
                        }
                        break;
                    }
                    case EmgFieldConst.PortMagnet:
                    {
                        EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.MagnetPayload);
                        if (!FieldLive(rule) || !FieldRelation(frag ? rule.FactionAlt : rule.Faction, ff, pr.Faction))
                        {
                            break;
                        }
                        if (!haveProps)
                        {
                            props = EmgProjProps(ref d, pr);
                            haveProps = true;
                        }
                        float sp = math.length(pr.Vel);
                        float2 want = (float2)(f.Pos - pr.Pos);
                        if (!(props.Magnetic > 0f) || sp < 1e-4f || math.lengthsq(want) < 1e-6f)
                        {
                            break;
                        }
                        float turn = math.radians(rule.Amount * props.Magnetic * f.Strength) * dt;
                        float a0 = math.atan2(pr.Vel.y, pr.Vel.x);
                        float da = math.atan2(want.y, want.x) - a0;
                        da = math.atan2(math.sin(da), math.cos(da));
                        a0 += math.clamp(da, -turn, turn);
                        pr.Vel = new float2(math.cos(a0), math.sin(a0)) * sp;
                        es.ProjRedirected++;
                        break;
                    }
                    default:
                    {
                        EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.ForcePayload);
                        if (!FieldLive(rule) || !FieldRelation(frag ? rule.FactionAlt : rule.Faction, ff, pr.Faction))
                        {
                            break;
                        }
                        double2 dir = FieldDir(f, pr.Pos, out double dist);
                        if (f.Port != EmgFieldConst.PortPush && f.Port != EmgFieldConst.PortVortex && dist < EmgFieldConst.PullKeep)
                        {
                            break;
                        }
                        if (!haveProps)
                        {
                            props = EmgProjProps(ref d, pr);
                            haveProps = true;
                        }
                        pr.Vel += (float2)(dir * (rule.Amount * f.Strength / math.max(0.05f, props.Mass) * dt));
                        es.ProjPushed++;
                        break;
                    }
                }
            }
            d.EmgS[0] = es;
            return reflected;
        }

        /// <summary>反射一发弹体：掉头、发 evt.bounce（面 = 场）、改为场方阵营并清掉原程序与弹道修饰（与静默偏折场 TryReflectProjectile 同一口径）。</summary>
        internal static void EmgReflectCore(ref CombatData d, ref CombatProjectile pr, int sourceId, CombatFaction to, float reflect)
        {
            float2 nrm = math.normalizesafe(-pr.Vel);
            // EMG1-FUZZ-01：先掉头再发事件——反弹触发的子载荷沿“反弹后的飞行方向”放出
            pr.Vel = -pr.Vel;
            EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, sourceId, EmgEntKind.Unit, nrm.x, nrm.y, EmgCode.FaceField, pr.Pos);
            pr.Prog = -1;
            pr.Faction = to;
            pr.Owner = sourceId;
            pr.Weapon = -1; // 反弹回来的弹体不再带原方读法
            pr.Flags &= ~(EmgProjFlags.Flight | EmgProjFlags.Heal | EmgProjFlags.Sub | EmgProjFlags.Overload | EmgProjFlags.ViaDrone | EmgProjFlags.Lob);
            pr.IgnoreUnit = 0;
            pr.Mods = EmgProjMods.None;
            pr.ModQ = 0;
            pr.HomingTarget = 0;
            pr.PierceLeft = 0;
            pr.BounceLeft = 0;
            pr.BounceObs = 0;
            pr.Intercepts = 0;
            pr.Damage = math.max(0f, pr.Damage) * reflect;
            CombatCounters c = d.Counters[0];
            c.ShotsReflected++;
            d.Counters[0] = c;
            Cue(ref d, CombatEventKind.Reflected, d.SlotOf(sourceId), 0, pr.Damage, pr.Pos, 1); // 修复轮：与静默偏折场 TryReflectProjectile 同一提示（弹字 / 音效）
        }

        /// <summary>
        /// 弹体穿过一块斑块时的载荷改写（FG19B 第 5 节物质来源行；沾染位本身由 EmgProjMatter 挂）：烟 / 蒸汽 → 寻的失效 0.5 / 0.3 秒；水 / 冷却液 / 液氮 → 去掉带火；
        /// 带油的弹体穿过明火 / 熔融金属 → 沿途拖出明火（每 2 米一块，≤ 6 块）；破片与带反弹修饰的弹体撞玻化层 / 冰面 → 按斑块边缘法线反射、发 evt.bounce（直射弹体飞越不反射）。每种每发只记一次。
        /// </summary>
        internal static void EmgProjPatchRules(ref CombatData d, ref CombatProjectile pr, in CombatZone z, double2 next)
        {
            EmgScalars es;
            switch (z.Matter)
            {
                case EmgMatterConst.Smoke:
                case EmgMatterConst.Steam:
                {
                    bool smoke = z.Matter == EmgMatterConst.Smoke;
                    EmgFieldRule rule = FieldRule(ref d, smoke ? EmgFieldConst.SmokePayload : EmgFieldConst.SteamPayload);
                    byte bit = smoke ? EmgFieldConst.OnceSmoke : EmgFieldConst.OnceSteam;
                    if (FieldLive(rule) && (pr.FieldOnce & bit) == 0 && rule.Amount > 0f)
                    {
                        pr.FieldOnce |= bit;
                        pr.LockQ = (ushort)math.max(pr.LockQ, math.min(65535, (int)math.round(rule.Amount * 1000f)));
                        es = d.EmgS[0];
                        es.ProjLockLoss++;
                        d.EmgS[0] = es;
                    }
                    break;
                }
                case EmgMatterConst.Water:
                case EmgMatterConst.Coolant:
                case EmgMatterConst.Cryo:
                {
                    EmgFieldRule rule = FieldRule(ref d, z.Matter == EmgMatterConst.Cryo ? EmgFieldConst.CryoPayload : EmgFieldConst.WaterPayload);
                    sbyte fire = MatterOf(ref d, EmgMatterConst.Fire).CoatBit;
                    if (FieldLive(rule) && fire >= 0 && fire < 31)
                    {
                        pr.Coat &= ~(1u << fire);
                        pr.TrailLeft = 0;
                    }
                    break;
                }
                case EmgMatterConst.Oil:
                case EmgMatterConst.Dust:
                {
                    // EMG2-REACT-02（DEBT-EMG2FLD01-01）：已带火的弹体穿过油 / 粉尘 → 向这块注热（油 +200 立即 E01；粉尘 +300 走 E04 / E05）
                    EmgFieldRule rule = FieldRule(ref d, z.Matter == EmgMatterConst.Oil ? EmgFieldConst.OilPayload : EmgFieldConst.DustPayload);
                    sbyte fire = MatterOf(ref d, EmgMatterConst.Fire).CoatBit;
                    if (FieldLive(rule) && rule.Amount > 0f && (pr.FieldOnce & EmgFieldConst.OnceIgnite) == 0 && fire >= 0 && fire < 31 && (pr.Coat & (1u << fire)) != 0u)
                    {
                        pr.FieldOnce |= EmgFieldConst.OnceIgnite;
                        EmgInjectPatches(ref d, next, 0.5f, EmgConst.PortHeat, rule.Amount, pr.Chain, pr.Depth, pr.Owner);
                    }
                    break;
                }
                case EmgMatterConst.Polymer:
                {
                    // EMG2-REACT-02（DEBT-EMG2FLD01-01）：穿过聚合胶速度 × 0.6，持续 1 秒（到时恢复，CombatLogic.cs 弹体步）；破片不走这行（表：弹体、无人机子弹）
                    EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.PolymerPayload);
                    if (FieldLive(rule) && pr.SlowQ == 0 && (pr.Flags & EmgProjFlags.Fragment) == 0 && rule.Amount > 0f && rule.Amount < 1f)
                    {
                        pr.SlowQ = (ushort)math.clamp((int)math.round(rule.Param * 1000f), 1, 65535);
                        pr.Vel *= rule.Amount;
                    }
                    break;
                }
                case EmgMatterConst.Fire:
                case EmgMatterConst.Molten:
                {
                    EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.FirePayload);
                    sbyte oil = MatterOf(ref d, EmgMatterConst.Oil).CoatBit;
                    if (FieldLive(rule) && (pr.FieldOnce & EmgFieldConst.OnceTrail) == 0 && oil >= 0 && oil < 31 && (pr.Coat & (1u << oil)) != 0u)
                    {
                        pr.FieldOnce |= EmgFieldConst.OnceTrail;
                        pr.TrailLeft = (byte)math.clamp((int)math.round(rule.Param), 0, 255);
                    }
                    break;
                }
                case EmgMatterConst.Glass:
                case EmgMatterConst.Ice:
                {
                    bool glass = z.Matter == EmgMatterConst.Glass;
                    EmgFieldRule rule = FieldRule(ref d, glass ? EmgFieldConst.GlassPayload : EmgFieldConst.IcePayload);
                    byte bit = glass ? EmgFieldConst.OnceGlass : EmgFieldConst.OnceIce;
                    bool eligible = (pr.Flags & EmgProjFlags.Fragment) != 0 || (glass && pr.BounceLeft > 0);
                    if (!FieldLive(rule) || !eligible || (pr.FieldOnce & bit) != 0)
                    {
                        break;
                    }
                    double2 rel = next - z.Pos;
                    float2 nrm = math.lengthsq(rel) > 1e-12 ? (float2)math.normalize(rel) : math.normalizesafe(-pr.Vel);
                    float dot = math.dot(pr.Vel, nrm);
                    if (dot >= 0f)
                    {
                        break; // 从里往外飞（在斑块里出生的破片）不反射
                    }
                    pr.FieldOnce |= bit;
                    pr.Vel -= 2f * dot * nrm;
                    EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, z.EmgId, EmgEntKind.Patch, nrm.x, nrm.y, EmgCode.FaceMatter, next);
                    es = d.EmgS[0];
                    es.ProjPatchBounce++;
                    d.EmgS[0] = es;
                    break;
                }
            }
        }

        /// <summary>
        /// fieldrule_obscure_unit（FG19B 第 5 节，DEBT-EMG2FLD01-02）：目标身处烟里（遮蔽 ≥ emg.obscure.full）只有 Amount（3）米内看得到；身处蒸汽里（部分遮蔽）锁定距离上限 Param（6）米。
        /// 所有索敌都经 <see cref="FindTarget"/>（编队、驻守、敌人 AI、炮塔）；玩家显式下的攻击命令不重新索敌，不受这条影响（RTS 右键敌人 = 攻击）。O(1)，读每步更新的物质掩码。
        /// </summary>
        internal static bool EmgObscured(ref CombatData d, int k, double dist)
        {
            uint mask = d.EmgUnit[k].PrevMatterMask;
            if ((mask & ((1u << EmgMatterConst.Smoke) | (1u << EmgMatterConst.Steam))) == 0u)
            {
                return false;
            }
            EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.ObscureUnit);
            if (!FieldLive(rule))
            {
                return false;
            }
            return (mask & (1u << EmgMatterConst.Smoke)) != 0u ? dist > rule.Amount : dist > rule.Param;
        }

        /// <summary>带火又带油的弹体沿途拖出明火：本步线段每跨过一个“间距”刻度落一块小明火斑块（物质中立），直到块数用完（FG19B fieldrule_fire_payload）。</summary>
        internal static void EmgProjTrail(ref CombatData d, ref CombatProjectile pr, double2 next)
        {
            EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.FirePayload);
            float spacing = rule.Amount > 0.1f ? rule.Amount : 2f;
            float2 u = math.normalizesafe(pr.Vel);
            if (math.lengthsq(u) < 0.5f)
            {
                return;
            }
            long k0 = (long)math.floor(math.dot((float2)pr.Prev, u) / spacing), k1 = (long)math.floor(math.dot((float2)next, u) / spacing);
            if (k0 == k1)
            {
                return;
            }
            pr.TrailLeft--;
            if (EmgRequestPatch(ref d, EmgMatterConst.Fire, next, 0.8f, 1f, 0f, pr.Owner, pr.Chain, pr.Depth))
            {
                EmgScalars es = d.EmgS[0];
                es.TrailPatches++;
                d.EmgS[0] = es;
            }
        }
    }
}
