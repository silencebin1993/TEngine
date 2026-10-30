using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-FW-01（DEBT-EMG1GRM01-05；FG19A 第 3.2 节、第 1.5 节）：子节点（触发固件之后的修饰 / 数值行）由子载荷自己结算。
    /// · 子弹药（飞行）：多份载荷分成真实弹丸（读 ter.cover 时绕开掩体）、伤害倍率、破甲、命中处爆圈 / 余击。
    /// · 子区 / 小脉冲：多块、落点游移、触及加半径、伤害倍率；落下时击退 / 牵引 / 环扫 / 连锁各一次，收回时回响一次；
    ///   阶段 1 掩体规则开时节拍伤害被障碍 / 残骸挡（抛射 / 贯穿越过）、吃正面装甲（破甲减免）。
    /// 参数都是指令行上热更层解析好的数（<see cref="EmgVerb"/> 的说明），内核不认任何内容 ID（FGR-EMG-013）；没有这些行时逐字走旧逻辑。
    /// </summary>
    public static partial class CombatLogic
    {
        private const uint SaltSubZone = 0x5A2Du;

        /// <summary>子弹药命中正面装甲时的减伤系数：1 − 装甲 × (1 − 破甲)。</summary>
        internal static float EmgSubArmorFactor(ref CombatData d, in CombatProjectile pr, int hit)
        {
            float pierce = pr.Prog >= 0 && EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m) ? m.ArmorPierce : 0f;
            return math.max(0f, 1f - d.Armor[hit].x * (1f - math.saturate(pierce)));
        }

        /// <summary>子弹药命中后：爆圈（命中处 r 米内其他敌对单位各吃 b 倍）与余击（c 米内最近的 a 个其他目标各吃 b 倍）。O(单位数 × 余击数)。</summary>
        internal static void EmgSubSplash(ref CombatData d, in CombatProjectile pr, int owner, int hit, double2 hitPos, float dmg)
        {
            if (pr.Prog < 0 || !EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m) || !(dmg > 0f))
            {
                return;
            }
            byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            if (m.BlastR > 0f && m.BlastRatio > 0f)
            {
                EmgRingDamage(ref d, hitPos, m.BlastR, want, dmg * m.BlastRatio, owner, hit);
            }
            if (m.ExtraHits > 0 && m.ExtraR > 0f && m.ExtraRatio > 0f)
            {
                var hitList = new FixedList128Bytes<int>();
                hitList.Add(hit);
                for (int q = 0; q < m.ExtraHits; q++)
                {
                    int k = EmgChainPick(ref d, hitPos, m.ExtraR, want, ref hitList, EmgInstrFlags.None);
                    if (k < 0)
                    {
                        break;
                    }
                    hitList.Add(k);
                    DamageUnit(ref d, k, dmg * m.ExtraRatio, owner);
                }
            }
        }

        /// <summary><paramref name="center"/> 周围 <paramref name="radius"/> 米内的敌对地面单位各吃 <paramref name="damage"/>（<paramref name="exclude"/> 除外）。</summary>
        private static void EmgRingDamage(ref CombatData d, double2 center, float radius, byte want, float damage, int owner, int exclude)
        {
            for (int k = 0; k < d.Count; k++)
            {
                if (k == exclude || d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], center) <= radius + d.Radius[k])
                {
                    DamageUnit(ref d, k, damage, owner);
                }
            }
        }

        /// <summary>
        /// 生成一份子区 / 小脉冲（<paramref name="code"/> = SubPulse / SubZone），按自己节点的修饰：触及加半径、伤害倍率、多块（半径 × b、伤害 × a，绕中心均分一圈）、
        /// 落点游移（± 米，无状态哈希）、抛射 / 贯穿越过掩体落点；落下时一次性的击退 / 牵引 / 环扫 / 连锁。区域放不下返回 false（整份留到下一步，多块不拆开放）。
        /// </summary>
        private static bool EmgSpawnSubZone(ref CombatData d, ref EmgSpawn s, int code, in EmgSubForm form, float damage, int ownerSlot, CombatFaction faction)
        {
            EmgModsOf(ref d, s.Prog, s.Node, out EmgMods m);
            int parts = m.Multi > 1 ? m.Multi : 1;
            if (d.Zones.Length + parts > d.ZoneCap && CountZones(ref d, CombatConst.ZoneKindReading) + parts > d.ZoneCap)
            {
                return false;
            }
            CombatReading r = s.Weapon >= 0 && s.Weapon < d.Weapons.Length ? d.Weapons[s.Weapon].Reading : default;
            float radius = form.RadiusRatio > 0f ? s.ZoneRadius * form.RadiusRatio : form.Radius;
            float seconds = form.LifeRatio > 0f ? s.ZoneLife * form.LifeRatio : form.Life;
            if (!(radius > 0f) || !(seconds > 0f))
            {
                s.Count = 0; // 母落区参数缺失：生成不了，整条请求作废（不会因为等待而好转）
                EmgStat(ref d, 3);
                return false;
            }
            radius += m.Reach;
            damage *= m.DmgMul > 0f ? m.DmgMul : 1f;
            float baseRadius = radius;
            if (parts > 1)
            {
                radius *= m.MultiRadius > 0f ? m.MultiRadius : 0.5f;
                damage *= m.MultiRatio > 0f ? m.MultiRatio : 0.4f;
            }
            float dps, tickScale;
            if (code == EmgConst.SubPulse)
            {
                // 小场脉冲：存续内恰好结算一次，一次的伤害 = 母载荷伤害 × 比例。
                float interval = seconds * 0.6f;
                tickScale = ZoneTickOf(ref d) / interval;
                dps = damage / interval;
            }
            else
            {
                tickScale = 1f;
                dps = damage; // 母落区的每秒伤害 × 比例
            }
            uint seed = (uint)s.Chain * 31u + (uint)s.Seq * 7u + (uint)s.Done;
            double2 center = s.Pos;
            if (m.DriftAmount > 0f)
            {
                center += new double2(2f * EmgRng.Unit(seed, 1u, SaltSubZone, 0u) - 1f, 2f * EmgRng.Unit(seed, 2u, SaltSubZone, 0u) - 1f) * m.DriftAmount;
            }
            float start = EmgRng.Unit(seed, 3u, SaltSubZone, 0u) * (2f * math.PI);
            byte want = faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            bool any = false;
            for (int q = 0; q < parts; q++)
            {
                double2 zp = center;
                if (parts > 1)
                {
                    float ang = start + 2f * math.PI * q / parts;
                    zp += new double2(math.cos(ang), math.sin(ang)) * (baseRadius * 0.8f);
                }
                zp = EmgDropPoint(ref d, s.Pos, zp, m); // 掩体规则：游移 / 多块偏出去的落点途中撞障碍（抛射越过）或残骸（贯穿越过）就落在撞点前
                if (!SpawnZone(ref d, ownerSlot, faction, zp, radius, seconds, dps, 0f, tickScale, r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln))
                {
                    continue;
                }
                any = true;
                SetLastZoneLook(ref d, code == EmgConst.SubPulse ? CombatZoneLook.Pulse : CombatZoneLook.Pool);
                int last = d.Zones.Length - 1;
                CombatZone z = d.Zones[last];
                EmgScalars es = d.EmgS[0];
                z.EmgProgRef = s.Prog + 1;
                z.EmgId = es.NextZoneId++;
                d.EmgS[0] = es;
                z.EmgChain = s.Chain;
                z.EmgNode = s.Node;
                z.EmgDepth = s.Depth;
                z.EmgGen = s.Gen;
                z.EmgSub = 1;
                z.EmgWeapon = s.Weapon;
                d.Zones[last] = z;
                if (m.Knock > 0f || m.PullA > 0f || (m.SweepR > 0f && m.SweepRatio > 0f) || m.Chain > 0)
                {
                    EmgSubZoneLanding(ref d, ref z, in m, want, ownerSlot, damage);
                    d.Zones[last] = z;
                }
            }
            return any;
        }

        /// <summary>子区落下那一刻的一次性动作（以子区为宿主）：击退（区内推开）、牵引（b 米内拉近 a 米）、环扫（a 米内各吃 b 倍）、连锁（区内最近的目标起跳）。</summary>
        private static void EmgSubZoneLanding(ref CombatData d, ref CombatZone z, in EmgMods m, byte want, int owner, float damage)
        {
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = EmgZoneHost(ref d, z);
            for (int k = 0; k < d.Count; k++)
            {
                if (d.Faction[k] != want || !d.IsAlive(k) || IsAir(ref d, k))
                {
                    continue;
                }
                double dist = math.distance(d.Pos[k], z.Pos);
                if (m.Knock > 0f && dist <= z.Radius + d.Radius[k])
                {
                    Displace(ref d, k, z.Pos, -m.Knock, 0.0, owner, EmgConst.PushCauseKnock);
                }
                else if (m.PullA > 0f && dist <= (m.PullR > 0f ? m.PullR : 4f) && dist >= 0.6)
                {
                    Displace(ref d, k, z.Pos, math.min(m.PullA, (float)dist - 0.5f), 0.5, owner, EmgConst.PushCausePull);
                }
            }
            if (m.SweepR > 0f && m.SweepRatio > 0f)
            {
                EmgRingDamage(ref d, z.Pos, m.SweepR, want, damage * m.SweepRatio, owner, -1);
            }
            if (m.Chain > 0 && m.ChainRange > 0f)
            {
                var hitList = new FixedList128Bytes<int>();
                int k = EmgChainPick(ref d, z.Pos, z.Radius, want, ref hitList, m.ChainPref);
                double2 at = z.Pos;
                float dmg = damage;
                ushort hc = d.EmgCtx.HitCode;
                d.EmgCtx.HitCode = EmgCode.HitChain;
                for (int q = 0; q < m.Chain && k >= 0; q++)
                {
                    hitList.Add(k);
                    at = d.Pos[k];
                    k = EmgChainPick(ref d, at, m.ChainRange, want, ref hitList, m.ChainPref);
                    if (k < 0)
                    {
                        break;
                    }
                    dmg *= m.ChainFalloff > 0f ? m.ChainFalloff : 1f;
                    DamageUnit(ref d, k, dmg, owner);
                    EmgMarkJumpReact(ref d, owner, k, m.ChainPref);
                }
                d.EmgCtx.HitCode = hc;
            }
            z.EmgTrigUsed = d.EmgCtx.TrigUsed;
            d.EmgCtx = saved;
        }

        /// <summary>子区收回（到期）时的回响：区内敌对单位再吃一个节拍 × c（evt.hit Code = 回响）。</summary>
        private static void EmgSubZoneEcho(ref CombatData d, in CombatZone z)
        {
            if (!EmgModsOf(ref d, z.EmgProgRef - 1, z.EmgNode, out EmgMods m) || !(m.EchoRatio > 0f) || !(z.Dps > 0f))
            {
                return;
            }
            byte want = z.Faction == CombatFaction.Hostile ? (byte)CombatFaction.Player : (byte)CombatFaction.Hostile;
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = EmgZoneHost(ref d, z);
            d.EmgCtx.HitCode = EmgCode.HitEcho;
            EmgRingDamage(ref d, z.Pos, z.Radius, want, z.Dps * z.TickInterval * m.EchoRatio, d.SlotOf(z.Owner), -1);
            d.EmgCtx = saved;
        }

        /// <summary>阶段 1 掩体规则下子区节拍对单位 <paramref name="k"/> 的伤害系数：被掩体挡 0（抛射 / 贯穿越过），正面装甲按破甲减免。规则关 / 不是子区为 1。</summary>
        private static float EmgSubZoneTickFactor(ref CombatData d, in CombatZone z, in EmgMods m, int k)
        {
            if (EmgPulseBlocked(ref d, z.Pos, d.Pos[k], m))
            {
                return 0f;
            }
            return IsFrontalArmored(ref d, k, z.Pos) ? math.max(0f, 1f - d.Armor[k].x * (1f - math.saturate(m.ArmorPierce))) : 1f;
        }
    }
}
