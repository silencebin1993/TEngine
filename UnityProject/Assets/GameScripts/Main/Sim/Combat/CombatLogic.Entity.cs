using BinGames.Sim.Nav;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-ENT-01（FG19 FGR-EMG-110～114；ADR-EMG-001 ⑦）：实体公共属性、残骸、破片与撞击。全部逐实体的结算只在这里（Main/Sim，Burst 步进里跑）。
    /// - 公共属性：不合并数据结构，只统一语义——单位 / 弹体 / 无人机 / 哨戒桩 / 残骸 / 破片的质量、导电、可燃、磁性、材质、抗性取自实体档案表（<see cref="CombatData.EmgProfiles"/>），
    ///   动态量（热量、电荷、完整度）取各自结构；对外由 <see cref="CombatKernel.TryGetEntityProps"/> 拼成一个块。
    /// - 阵亡统一入口 <see cref="EmgOnDeath"/>：<see cref="Kill"/> 与 <see cref="CombatKernel.SetHealth"/> 的“存活 → 阵亡”都走它，同一单位只处理一次（看 Alive 位翻转）；
    ///   外部血量单位的击杀者取最后一次伤害请求的攻击者（<see cref="EmgUnitState.LastHitBy"/>）；“直接击毁”不留残骸（<see cref="EmgDestroyDirect"/>）。
    /// - 残骸：机器 / 敌人 / 炮塔阵亡生成（结构单位 = 建筑不生成）；挡弹（吸收发 evt.absorb、扣耐久，弹体有障碍面反弹次数时反弹）、单位穿过减速（不改寻路网格）、
    ///   被推铲 / 击退推动（滑行、撞上障碍 / 残骸 / 单位按速度发 evt.impact、扣血走 DamageFrom 来源 = 撞击）；每地点 256、野外 90 秒。
    ///   阶段 1 崩解 = 计数 + 消失（DEBT-EMG1ENT01-01：转化为 mat.debris 斑块归 EMG2-MAT-01）。
    /// - 破片：弹体池里 Flags.Fragment、Gen 2、Prog −1 的短寿命弹体（残骸受单次动能 ≥ 40 时崩出），命中发 evt.hit（链深 + 1）。
    /// 全部在 <see cref="CombatConfig.EmgEntity"/> = 1（emg.stage ≥ 1）时才生效；为 0 时没有任何残骸、行为与 M10 基线逐字一致。
    /// </summary>
    public static partial class CombatLogic
    {
        internal static bool EmgEntOn(ref CombatData d) => d.Config.EmgEntity != 0;

        private static float WreckSecondsOf(ref CombatData d) => d.Config.EmgWreckSeconds > 0f ? d.Config.EmgWreckSeconds : EmgConst.DefaultWreckSeconds;
        private static float WreckHpRatioOf(ref CombatData d) => d.Config.EmgWreckHpRatio > 0f ? d.Config.EmgWreckHpRatio : EmgConst.DefaultWreckHpRatio;
        private static float WreckSlowOf(ref CombatData d) => d.Config.EmgWreckSlow > 0f ? d.Config.EmgWreckSlow : EmgConst.DefaultWreckSlow;
        private static float ImpactVMinOf(ref CombatData d) => d.Config.EmgImpactVMin > 0f ? d.Config.EmgImpactVMin : EmgConst.DefaultImpactVMin;
        private static float PushDMinOf(ref CombatData d) => d.Config.EmgPushDMin > 0f ? d.Config.EmgPushDMin : EmgConst.DefaultPushDMin;
        private static float KnockSecondsOf(ref CombatData d) => d.Config.EmgKnockSeconds > 0f ? d.Config.EmgKnockSeconds : EmgConst.DefaultKnockSeconds;
        private static float ShoveRefMassOf(ref CombatData d) => d.Config.EmgShoveRefMass > 0f ? d.Config.EmgShoveRefMass : EmgConst.DefaultShoveRefMass;

        // ─────────────────────────────── 公共属性 ───────────────────────────────

        internal static EmgProfile EmgProfileAt(ref CombatData d, int index) =>
            index >= 0 && index < d.EmgProfiles.Length ? d.EmgProfiles[index] : EmgProfile.Steel(0f);

        /// <summary>单位的档案下标：热更层写过的（机器按底盘、敌人按材质）优先，否则按单位种类取默认槽（外部血量的敌人 = 首领）。</summary>
        internal static int EmgUnitProfileIndex(ref CombatData d, int i)
        {
            int p = d.EProfile[i];
            if (p >= 0 && p < d.EmgProfiles.Length)
            {
                return p;
            }
            switch ((CombatUnitKind)d.Kind[i])
            {
                case CombatUnitKind.Machine: return EmgConst.SlotMachine;
                case CombatUnitKind.Turret: return EmgConst.SlotTurret;
                case CombatUnitKind.Structure: return EmgConst.SlotStructure;
                default: return d.Has(i, CombatUnitFlags.ExternalHealth) ? EmgConst.SlotBoss : EmgConst.SlotEnemy;
            }
        }

        /// <summary>单位质量（FG19B 第 6.2 节）：单位自己的 > 档案的 > 敌人 8 × 半径²（限 1～80）。</summary>
        internal static float EmgUnitMass(ref CombatData d, int i)
        {
            float own = d.EmgUnit[i].Mass;
            if (own > 0f)
            {
                return own;
            }
            float pm = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, i)).Mass;
            return pm > 0f ? pm : math.clamp(8f * d.Radius[i] * d.Radius[i], 1f, 80f);
        }

        /// <summary>弹体质量：弹体自带（prop.mass 写端改过）> 破片 / 重炮根弹体（重型射弹档案，FG19B 6.2）/ 普通弹体档案。</summary>
        internal static float EmgProjMass(ref CombatData d, in CombatProjectile pr) =>
            pr.Mass > 0f ? pr.Mass : EmgProfileAt(ref d, (pr.Flags & EmgProjFlags.Fragment) != 0 ? EmgConst.SlotFragment
                : (pr.Flags & EmgProjFlags.Sub) == 0 && pr.Weapon >= 0 && pr.Weapon < d.Weapons.Length && d.Weapons[pr.Weapon].Mode == CombatWeaponMode.Cannon ? EmgConst.SlotHeavy
                : EmgConst.SlotProjectile).Mass;

        /// <summary>阵亡者材质码（evt.kill Code，FGR-EMG-060 第 9 行）。</summary>
        internal static byte EmgUnitMaterial(ref CombatData d, int i) => EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, i)).Material;

        /// <summary>弹体单次动能系数（FG19B 第 6.3 节）：破片 2.5、无人机子弹 0.5、重型（质量 ≥ 重型档案）2.0、其余 1.0。</summary>
        private static float EmgProjKinetic(ref CombatData d, in CombatProjectile pr)
        {
            EmgEntityParams ep = d.EmgEntParams[0];
            if ((pr.Flags & EmgProjFlags.Fragment) != 0)
            {
                return ep.KineticFragment;
            }
            if ((pr.Flags & EmgProjFlags.ViaDrone) != 0)
            {
                return ep.KineticDrone;
            }
            return EmgProjMass(ref d, pr) >= EmgProfileAt(ref d, EmgConst.SlotHeavy).Mass || (pr.Flags & EmgProjFlags.Overload) != 0 ? ep.KineticHeavy : ep.KineticProjectile;
        }

        internal static EmgEntityProps EmgUnitProps(ref CombatData d, int i)
        {
            EmgProfile p = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, i));
            EmgEntityProps e = FromProfile(p, EmgEntKind.Unit);
            e.Mass = EmgUnitMass(ref d, i);
            e.Heat = d.Heat[i];
            e.Integrity = d.MaxHp[i] > 0f ? math.saturate(d.Hp[i] / d.MaxHp[i]) : 0f;
            e.Displace = (byte)(p.Displace != 0 && CanDisplace(ref d, i) ? 1 : 0);
            return e;
        }

        internal static EmgEntityProps EmgProjProps(ref CombatData d, in CombatProjectile pr)
        {
            bool frag = (pr.Flags & EmgProjFlags.Fragment) != 0;
            EmgEntityProps e = FromProfile(EmgProfileAt(ref d, frag ? EmgConst.SlotFragment : EmgConst.SlotProjectile), EmgEntKind.Projectile);
            e.Sub = (byte)(frag ? 2 : 0);
            e.Mass = EmgProjMass(ref d, pr);
            e.Integrity = 1f;
            return e;
        }

        internal static EmgEntityProps EmgDroneProps(ref CombatData d, in CombatDrone dr)
        {
            bool sentry = dr.Anchored != 0;
            EmgEntityProps e = FromProfile(EmgProfileAt(ref d, sentry ? EmgConst.SlotSentry : EmgConst.SlotDrone), EmgEntKind.Drone);
            e.Sub = (byte)(sentry ? 1 : 0);
            e.Integrity = 1f;
            return e;
        }

        internal static EmgEntityProps EmgWreckProps(ref CombatData d, in EmgWreck k)
        {
            int pi = k.Profile;
            EmgEntityProps e = FromProfile(EmgProfileAt(ref d, pi >= 0 ? pi : EmgConst.SlotWreck), EmgEntKind.Wreck);
            e.Mass = k.Mass;
            e.Heat = k.Temp;
            e.Charge = k.Charge;
            e.Integrity = k.MaxHp > 0f ? math.saturate(k.Hp / k.MaxHp) : 0f;
            e.Displace = 1;
            return e;
        }

        private static EmgEntityProps FromProfile(in EmgProfile p, EmgEntKind kind) => new EmgEntityProps
        {
            Kind = kind, Mass = p.Mass, Conductive = p.Conductive, Flammable = p.Flammable, Magnetic = p.Magnetic, Material = p.Material, Displace = p.Displace,
            ResistCold = p.ResistCold, ResistHeat = p.ResistHeat, ResistElec = p.ResistElec, ResistCorrode = p.ResistCorrode, ResistMag = p.ResistMag, ResistHack = p.ResistHack,
        };

        // ─────────────────────────────── 阵亡统一入口（ADR ⑦“外部血量单位”）───────────────────────────────

        /// <summary>外部血量单位被判死时调用方给的击杀者是 0（热更层 OnMachineDied / SetHealth）：取内核记的最后一次伤害请求的攻击者（ADR ⑦ 第 2 条，“以内核为准”）。</summary>
        internal static int EmgResolveKiller(ref CombatData d, int t, int killerId) =>
            killerId == 0 && EmgEntOn(ref d) && d.Has(t, CombatUnitFlags.ExternalHealth) ? d.EmgUnit[t].LastHitBy : killerId;

        /// <summary>DamageUnit 的外部血量分支：记这次伤害请求的攻击者与链（击杀归属）。</summary>
        internal static void EmgNoteExternalHit(ref CombatData d, int t, int attackerId)
        {
            if (!EmgEntOn(ref d) || attackerId == 0)
            {
                return;
            }
            EmgUnitState u = d.EmgUnit[t];
            u.LastHitBy = attackerId;
            u.LastHitChain = d.EmgCtx.Prog >= 0 ? d.EmgCtx.Chain : u.LastHitChain;
            d.EmgUnit[t] = u;
        }

        /// <summary>
        /// 阵亡统一入口（Alive 位刚翻成 false 之后调用；调用方保证同一单位只进来一次）：生成残骸（实体层开、不是结构、没有“不留残骸”标志），再发 evt.death / evt.kill。
        /// </summary>
        internal static void EmgOnDeath(ref CombatData d, int t, int killerId, byte flags)
        {
            if (EmgEntOn(ref d))
            {
                EmgUnitState u = d.EmgUnit[t];
                flags |= u.DeathFlags;
                if (u.DeathFlags != 0)
                {
                    u.DeathFlags = 0;
                    d.EmgUnit[t] = u;
                }
                if ((flags & EmgConst.DeathNoWreck) == 0 && d.Kind[t] != (byte)CombatUnitKind.Structure)
                {
                    EmgSpawnWreck(ref d, t);
                }
            }
            EmgOnKill(ref d, t, killerId);
        }

        /// <summary>
        /// “直接击毁”（碎裂 / 湮灭 / 处决等不留残骸，ADR ⑦ 第 3 条）：普通单位当场阵亡、不留残骸；外部血量单位内核不自行判死，
        /// 记下“不留残骸”并发一条伤害值 = 镜像血量的致死请求，由热更层判死后经 SetHealth / Kill 回到 <see cref="EmgOnDeath"/>。返回 true = 请求 / 击毁已发出。
        /// </summary>
        internal static bool EmgDestroyDirect(ref CombatData d, int t, int attacker)
        {
            if (!d.IsAlive(t) || d.Has(t, CombatUnitFlags.Invulnerable))
            {
                return false;
            }
            EmgUnitState u = d.EmgUnit[t];
            u.DeathFlags |= EmgConst.DeathNoWreck;
            d.EmgUnit[t] = u;
            if (d.Has(t, CombatUnitFlags.ExternalHealth))
            {
                return DamageUnit(ref d, t, math.max(d.Hp[t], 0.001f), attacker);
            }
            Kill(ref d, t, attacker >= 0 && attacker < d.Count ? d.Id[attacker] : 0);
            return true;
        }

        /// <summary>ADR ⑦ 第 5 条：同一外部血量单位一步内的持续类伤害累加（攻击者取伤害最大的一份）。O(本步已记的外部血量单位数)。</summary>
        private static void EmgMergeAdd(ref CombatData d, int t, float damage, int attackerId)
        {
            for (int i = 0; i < d.EmgMerge.Length; i++)
            {
                EmgMergeReq m = d.EmgMerge[i];
                if (m.Slot != t)
                {
                    continue;
                }
                m.Damage += damage;
                if (damage > m.ByDamage)
                {
                    m.ByDamage = damage;
                    m.By = attackerId;
                }
                d.EmgMerge[i] = m;
                return;
            }
            d.EmgMerge.Add(new EmgMergeReq { Slot = t, Damage = damage, By = attackerId, ByDamage = damage });
        }

        /// <summary>步末：每个外部血量单位一条合并后的伤害请求（仍存活的才发）。</summary>
        private static void EmgMergeFlush(ref CombatData d)
        {
            for (int i = 0; i < d.EmgMerge.Length; i++)
            {
                EmgMergeReq m = d.EmgMerge[i];
                if (m.Slot >= 0 && m.Slot < d.Count && d.IsAlive(m.Slot) && m.Damage > 0f)
                {
                    Gameplay(ref d, CombatEventKind.DamageRequest, m.Slot, m.By, m.Damage, d.Hp[m.Slot], d.Pos[m.Slot], 0, 0);
                }
            }
            d.EmgMerge.Clear();
        }

        // ─────────────────────────────── 残骸 ───────────────────────────────

        private static void EmgSpawnWreck(ref CombatData d, int t)
        {
            float maxHp = d.MaxHp[t] * WreckHpRatioOf(ref d);
            if (!(maxHp > 0f) || !(d.Radius[t] > 0f))
            {
                return;
            }
            EmgScalars es = d.EmgS[0];
            if (d.Wrecks.Length >= EmgConst.DefaultWreckCapacity)
            {
                // 满了：最早的崩解（阶段 1 = 计数 + 消失，DEBT-EMG1ENT01-01 转化为碎屑斑块归 EMG2-MAT-01）。列表按生成先后排，下标 0 最早。
                for (int j = 1; j < d.Wrecks.Length; j++)
                {
                    d.Wrecks[j - 1] = d.Wrecks[j];
                }
                d.Wrecks.ResizeUninitialized(d.Wrecks.Length - 1);
                es.WrecksCapped++; // shortcut: 满容量最早的崩解 = 计数 + 消失（不转碎屑斑块），EMG2-MAT-01 改生成 mat.debris（DEBT-EMG1ENT01-01）
            }
            double now = d.Scalars[0].Time;
            var w = new EmgWreck
            {
                Pos = d.Pos[t], Born = now, Until = now + WreckSecondsOf(ref d), Radius = d.Radius[t] * EmgConst.WreckRadiusRatio, MaxHp = maxHp, Hp = maxHp,
                Mass = EmgUnitMass(ref d, t), Temp = d.Heat[t], SrcKind = d.Kind[t], SrcExtKey = d.ExtKey[t],
                NoSalvage = (byte)(d.Faction[t] == (byte)CombatFaction.Player ? 1 : 0), // T5：己方机器的残骸在回收修复（EMG2-RECL-01）定稿前不可拆解回收
                Chain = d.EmgCtx.Prog >= 0 ? d.EmgCtx.Chain : 0, Id = es.NextWreckId++,
            };
            w.Profile = (short)EmgUnitProfileIndex(ref d, t);
            // EMG1-CHAIN-01：残骸继承击毁它的那条链（Key = 残骸 Id，Aux = 来源单位种类，源 = 被击毁者，Other = 击毁者）；击毁者另持久存在残骸上
            int killer = d.EmgCtx.Owner >= 0 && d.EmgCtx.Owner < d.Count ? d.Id[d.EmgCtx.Owner] : d.EmgUnit[t].LastHitBy;
            w.Killer = killer;
            es.WrecksBorn++;
            es.HashDirty = 1;
            d.EmgS[0] = es;
            d.Wrecks.Add(w);
            EmgChainLog(ref d, EmgChainNode.Wreck, w.Chain, d.EmgCtx.Depth, w.Id, d.Id[t], w.Pos, killer, d.Kind[t]);
        }

        /// <summary>单位穿过残骸减速（FGR-EMG-113 作用 2）：单位中心在一块有耐久的残骸圆内时速度 × emg.wreck.slow；空中单位不受影响。不改寻路网格。</summary>
        internal static float EmgWreckSlow(ref CombatData d, int i)
        {
            if (d.Wrecks.Length == 0 || !EmgEntOn(ref d) || IsAir(ref d, i))
            {
                return 1f;
            }
            return EmgHashPoint(ref d, d.Pos[i], EmgHash.KindWreck) >= 0 ? WreckSlowOf(ref d) : 1f;
        }

        /// <summary>
        /// 飞行弹体本步线段先于单位交点撞上残骸（FGR-EMG-113 作用 1）：扣残骸耐久（单次动能够时崩出破片）；还有障碍面反弹次数就按残骸圆法线反弹（返回 1，弹体继续飞），
        /// 否则被吸收（evt.absorb + evt.expire Code = 被吸收，返回 2，弹体作废）。没撞上返回 0。起点已在残骸圆内（站在残骸上开火）的不算。
        /// </summary>
        internal static int EmgWreckHit(ref CombatData d, ref CombatProjectile pr, double2 next, double unitT)
        {
            int w = EmgHashSegment(ref d, pr.Prev, next, EmgHash.KindWreck, false, out double t, true);
            if (w < 0 || t > unitT)
            {
                return 0;
            }
            // 残骸上站着弹体的敌方地面单位：与即时命中（skipEnds）同一口径，站在残骸上的不算躲在它后面，残骸不替它挡弹。
            // O(单位数)，只在弹体撞上残骸的那一步（Main/Sim）。
            EmgWreck kw = d.Wrecks[w];
            double r2 = (double)kw.Radius * kw.Radius;
            for (int u = 0; u < d.Count; u++)
            {
                if (d.IsAlive(u) && d.Faction[u] != (byte)pr.Faction && !IsAir(ref d, u) && math.distancesq(d.Pos[u], kw.Pos) <= r2)
                {
                    return 0;
                }
            }
            double2 at = pr.Prev + (next - pr.Prev) * t;
            EmgWreck k = d.Wrecks[w];
            float dmg = math.max(0f, pr.Damage);
            k.Hp = math.max(0f, k.Hp - dmg);
            d.Wrecks[w] = k;
            EmgWreckStruck(ref d, w, dmg * EmgProjKinetic(ref d, pr), EmgDir((double2)pr.Vel), pr.Owner, pr.Faction, pr.Depth, pr.Chain);
            EmgBusStats st = d.EmgStats[0];
            if (EmgBounceFace(ref d, ref pr, at, k.Pos, EmgCode.FaceWreck))
            {
                st.WreckBounces++;
                d.EmgStats[0] = st;
                return 1;
            }
            st.WreckAbsorbs++;
            d.EmgStats[0] = st;
            if ((pr.Flags & EmgProjFlags.Sub) != 0)
            {
                EmgNodeEffects(ref d, pr.Prog, pr.Node, at, d.SlotOf(pr.Owner), pr.Faction); // 子弹药在撞点执行自己节点的指令（与撞障碍同一口径）
            }
            EmgProjEvent(ref d, ref pr, EmgEventKind.Absorb, k.Id, EmgEntKind.Wreck, dmg, 0f, 0, at);
            EmgProjEvent(ref d, ref pr, EmgEventKind.Expire, k.Id, EmgEntKind.Wreck, 0f, 0f, EmgCode.ExpireAbsorbed, at);
            CombatCounters c = d.Counters[0];
            c.ProjectilesExpired++;
            d.Counters[0] = c;
            return 2;
        }

        /// <summary>
        /// 残骸受到一次打击（弹体吸收 / 即时命中被挡 / 撞击）：单次动能 ≥ fragment.wreck_heavy 门槛（40）时崩出破片（FGR-EMG-114“残骸被重击”）——
        /// 从残骸背离受击方向的一侧放出，±15° 均布。链深已到 3 不放（计 DepthCut）。
        /// </summary>
        internal static void EmgWreckStruck(ref CombatData d, int w, float kinetic, float2 dir, int ownerId, CombatFaction faction, byte depth, int chain)
        {
            EmgEntityParams ep = d.EmgEntParams[0];
            if (ep.FragCount <= 0 || !(kinetic >= ep.FragThreshold) || w < 0 || w >= d.Wrecks.Length)
            {
                return;
            }
            if (depth >= EmgConst.MaxDepth)
            {
                EmgBusStats st = d.EmgStats[0];
                st.DepthCut++;
                st.LastDepthCutChain = chain;
                d.EmgStats[0] = st;
                EmgChainLog(ref d, EmgChainNode.DepthCut, chain, depth, d.Wrecks[w].Id, ownerId, d.Wrecks[w].Pos, 0, 0, d.Wrecks[w].Chain); // EMG1-CHAIN-01
                return;
            }
            EmgWreck k = d.Wrecks[w];
            // EMG1-CHAIN-01：破片走打击者的链，Parent = 残骸的链（跨机结果在因果链里同时显示各台机器）；Key = 残骸 Id，Aux = 片数
            EmgChainLog(ref d, EmgChainNode.Fragment, chain > 0 ? chain : k.Chain, (byte)(depth + 1), k.Id, ownerId, k.Pos, 0, (byte)math.min(ep.FragCount, 255), chain > 0 ? k.Chain : 0);
            float2 fwd = math.lengthsq(dir) > 1e-12f ? math.normalize(dir) : new float2(0f, 1f);
            double2 from = k.Pos + (double2)(fwd * (k.Radius + 0.15f));
            for (int q = 0; q < ep.FragCount; q++)
            {
                float spread = ep.FragCount > 1 ? math.radians(-15f + 30f * q / (ep.FragCount - 1)) : 0f;
                float cs = math.cos(spread), sn = math.sin(spread);
                float2 v = new float2(fwd.x * cs - fwd.y * sn, fwd.x * sn + fwd.y * cs);
                EmgSpawnFragment(ref d, from, v, ownerId, faction, (byte)(depth + 1), chain, ep.FragDamage, ep.FragSpeed, ep.FragRange);
            }
        }

        /// <summary>
        /// 破片（FGR-EMG-114）：弹体池里的短寿命弹体，2 代、不执行程序（Prog −1）、命中发 evt.hit、不分阵营（不打放出它的单位）。
        /// 弹体池满按 FGR-EMG-063 顺延（EMG1-FW-01，关闭 DEBT-EMG1ENT01-03）：进待生成表（种类 PendFrag），之后每步有空位先放出、方向不变；待生成表也满才丢弃并计 FragLost。
        /// 返回 true = 当步已放出。
        /// </summary>
        internal static bool EmgSpawnFragment(ref CombatData d, double2 at, float2 dir, int ownerId, CombatFaction faction, byte depth, int chain, float damage, float speed, float range)
        {
            if (speed > 0f && range > 0f)
            {
                if (EmgFragmentTry(ref d, at, dir, ownerId, faction, depth, chain, damage, speed, range))
                {
                    return true;
                }
                if (d.EmgPending.Length < EmgConst.PendingCapacity)
                {
                    EmgStat(ref d, 2);
                    d.EmgPending.Add(new EmgSpawn
                    {
                        Kind = EmgConst.PendFrag, Armed = 1, Count = 1, Pos = at, Dir = dir, Owner = ownerId, Weapon = -1, Prog = -1, Faction = (byte)faction,
                        Depth = depth, Chain = chain, Damage = damage, ZoneRadius = speed, ZoneLife = range,
                    });
                    return false;
                }
            }
            EmgBusStats lost = d.EmgStats[0];
            lost.FragLost++;
            d.EmgStats[0] = lost;
            return false;
        }

        /// <summary>弹体池有空位就放一片破片（待生成表补放与当步放出共用）。</summary>
        internal static bool EmgFragmentTry(ref CombatData d, double2 at, float2 dir, int ownerId, CombatFaction faction, byte depth, int chain, float damage, float speed, float range)
        {
            if (d.Projectiles.Length >= d.Config.ProjectileCapacity)
            {
                return false;
            }
            EmgBusStats st = d.EmgStats[0];
            EmgScalars es = d.EmgS[0];
            int id = es.NextProjId++;
            d.EmgS[0] = es;
            d.Projectiles.Add(new CombatProjectile
            {
                Id = id, Prog = -1, Chain = chain, Gen = EmgConst.MaxGen, Depth = depth, Flags = EmgProjFlags.Fragment,
                Pos = at, Prev = at, Vel = dir * speed, Owner = ownerId, Weapon = -1, Damage = damage, Radius = 0.1f, Life = range / speed, Faction = faction, IgnoreUnit = ownerId,
                Mass = EmgProfileAt(ref d, EmgConst.SlotFragment).Mass,
            });
            CombatCounters c = d.Counters[0];
            c.ProjectilesSpawned++;
            d.Counters[0] = c;
            st.FragSpawned++;
            d.EmgStats[0] = st;
            return true;
        }

        /// <summary>
        /// EMG1-FW-01（FGR-EMG-184 / 233 第 3 条）：破片伤到同阵营单位——因果链记“友伤”节点（SrcUnit = 受伤单位，Other = 破片归属单位），
        /// 并进友伤列表（不进快照；热更层只读它，己方的发警告级聚合通知、可定位）。O(1)。
        /// </summary>
        internal static void EmgFriendlyHit(ref CombatData d, in CombatProjectile pr, int victimId, double2 at)
        {
            EmgChainLog(ref d, EmgChainNode.Friendly, pr.Chain, pr.Depth, 0, victimId, at, pr.Owner);
            d.EmgFriendly.Add(new int2(victimId, pr.Owner));
        }

        /// <summary>破片命中一个单位：evt.hit（宿主 = 破片，Prog −1，链深 = 破片的链深）。按需：地点没有程序读 evt.hit 时不入队。</summary>
        internal static void EmgFragmentHit(ref CombatData d, in CombatProjectile pr, int hitId, float damage, double2 at)
        {
            if (!EmgSiteListens(ref d, EmgEventKind.Hit))
            {
                return;
            }
            EmgEmit(ref d, new EmgEvent
            {
                Kind = EmgEventKind.Hit, Depth = pr.Depth, EntKind = EmgEntKind.Projectile, Ent = pr.Id, OtherKind = EmgEntKind.Unit, Other = hitId, Prog = -1,
                Chain = pr.Chain, Code = EmgCode.HitDirect, Value = damage, Value2 = 1f, Pos = at,
            });
        }

        /// <summary>没有宿主程序的实体事件（残骸的撞击 / 位移 / 吸收）：按需（地点有程序读这一类）才入队。</summary>
        private static void EmgEntityEvent(ref CombatData d, EmgEventKind k, EmgEntKind ent, int entId, EmgEntKind other, int otherId, float value, float value2, ushort code, double2 pos)
        {
            if (!EmgSiteListens(ref d, k))
            {
                return;
            }
            EmgEmit(ref d, new EmgEvent
            {
                Kind = k, EntKind = ent, Ent = entId, OtherKind = otherId != 0 ? other : EmgEntKind.None, Other = otherId, Prog = -1,
                Chain = d.EmgCtx.Prog >= 0 ? d.EmgCtx.Chain : 0, Code = code, Value = value, Value2 = value2, Pos = pos,
            });
        }

        // ─────────────────────────────── 推动与撞击（FGR-EMG-112）───────────────────────────────

        /// <summary>
        /// 格斗 / 力场的击退读法一次出手（推铲）：出手者前方（格斗）/ 身边（力场）击退范围内的残骸被推开——速度 = 击退位移 ÷ emg.push.knock_seconds × min(1, 参考质量 ÷ 残骸质量)，
        /// 匀减速滑行 2 倍等效用时（滑行距离 = 击退位移 × 质量比）。每次出手只推一次。
        /// </summary>
        internal static void EmgShoveWrecks(ref CombatData d, int a, int t, double2 origin, float meters, bool cone)
        {
            if (d.Wrecks.Length == 0 || !EmgEntOn(ref d) || !(meters > 0f) || a < 0 || t < 0)
            {
                return;
            }
            double2 toT = d.Pos[t] - origin;
            double len = math.length(toT);
            double2 dir = len > 1e-6 ? toT / len : new double2(0, 1);
            double reach = len + d.Radius[t];
            float tau = KnockSecondsOf(ref d);
            float refMass = ShoveRefMassOf(ref d);
            for (int i = 0; i < d.Wrecks.Length; i++)
            {
                EmgWreck k = d.Wrecks[i];
                if (!(k.Hp > 0f))
                {
                    continue;
                }
                double2 rel = k.Pos - origin;
                double along = math.dot(rel, dir);
                double2 push;
                if (cone)
                {
                    double lateral = math.length(rel - dir * along);
                    if (along < -k.Radius || along > reach + k.Radius || lateral > k.Radius + 1.0)
                    {
                        continue;
                    }
                    push = dir;
                }
                else
                {
                    double dist = math.length(rel);
                    if (dist > reach + k.Radius)
                    {
                        continue;
                    }
                    push = dist > 1e-6 ? rel / dist : dir;
                }
                float v0 = meters / tau * math.min(1f, refMass / math.max(0.01f, k.Mass));
                k.Vel = (float2)(push * v0);
                k.Decel = v0 / (2f * tau);
                k.PushDist = 0f;
                k.PushBy = d.Id[a];
                k.PushCause = EmgConst.PushCauseKnock;
                d.Wrecks[i] = k;
            }
        }

        /// <summary>
        /// 单位被击退 / 牵引的一次瞬时位移（实体层开时代替原来的 MoveCollide）：等效速度 = 位移 ÷ emg.push.knock_seconds；途中先撞上障碍 / 残骸 / 另一单位就停在撞点，
        /// 速度 ≥ emg.impact.v_min 时发 evt.impact、按撞击动能（质量 × 速度² × 系数）扣双方的血（DamageFrom 来源 = 撞击；不伤推动者的己方）；位移 ≥ emg.push.d_min 发 evt.push。
        /// 档案不可位移（哨戒桩 / 固定底盘 / 首领）的不动。返回新位置。
        /// </summary>
        internal static double2 EmgDisplaceUnit(ref CombatData d, int k, double2 from, double2 want, int pusher, ushort cause)
        {
            if (EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, k)).Displace == 0)
            {
                return from;
            }
            double2 actual = MoveCollide(ref d, k, from, want);
            bool navBlocked = math.distancesq(actual, want) > 1e-6;
            double segLen = math.distance(from, actual);
            float speed = (float)(math.distance(from, want) / KnockSecondsOf(ref d));
            double best = navBlocked ? 1.0 : 2.0;
            int hitUnit = -1, hitWreck = -1;
            if (segLen > 1e-6)
            {
                float r = d.Radius[k];
                for (int o = 0; o < d.Obstacles.Length; o++)
                {
                    float3 ob = d.Obstacles[o];
                    double2 c = new double2(ob.x, ob.y);
                    if (math.distance(from, c) <= ob.z + r)
                    {
                        continue;
                    }
                    double tt = SegmentCircleT(from, actual, c, ob.z + r);
                    if (tt >= 0 && tt < best)
                    {
                        best = tt;
                        hitUnit = -1;
                        hitWreck = -1;
                    }
                }
                for (int w = 0; w < d.Wrecks.Length; w++)
                {
                    EmgWreck wk = d.Wrecks[w];
                    if (!(wk.Hp > 0f) || math.distance(from, wk.Pos) <= wk.Radius + r)
                    {
                        continue; // 站在残骸上被推开的不算撞（减速穿过）
                    }
                    double tt = SegmentCircleT(from, actual, wk.Pos, wk.Radius + r);
                    if (tt >= 0 && tt < best)
                    {
                        best = tt;
                        hitWreck = w;
                        hitUnit = -1;
                    }
                }
                for (int u = 0; u < d.Count; u++)
                {
                    if (u == k || !d.IsAlive(u) || IsAir(ref d, u) || math.distance(from, d.Pos[u]) <= d.Radius[u] + r)
                    {
                        continue;
                    }
                    double tt = SegmentCircleT(from, actual, d.Pos[u], d.Radius[u] + r);
                    if (tt >= 0 && tt < best)
                    {
                        best = tt;
                        hitUnit = u;
                        hitWreck = -1;
                    }
                }
            }
            double2 end = best <= 1.0 ? from + (actual - from) * math.max(0.0, best - 0.02 / math.max(segLen, 1e-6)) : actual;
            if (best <= 1.0 && speed >= ImpactVMinOf(ref d))
            {
                float mass = EmgUnitMass(ref d, k);
                float kinetic = mass * speed * speed * d.EmgEntParams[0].KineticImpact;
                int otherId = hitUnit >= 0 ? d.Id[hitUnit] : hitWreck >= 0 ? d.Wrecks[hitWreck].Id : 0;
                EmgEntKind otherKind = hitUnit >= 0 ? EmgEntKind.Unit : hitWreck >= 0 ? EmgEntKind.Wreck : EmgEntKind.None;
                EmgCountImpact(ref d);
                EmgUnitEvent(ref d, EmgEventKind.Impact, k, otherId, speed, mass, 0, end, otherKind);
                int capped = d.EmgS[0].WrecksCapped;
                EmgImpactDamage(ref d, k, kinetic, pusher);
                hitWreck -= d.EmgS[0].WrecksCapped - capped; // 被推者撞死、满 256 生成残骸会整表前移一位：撞上的那块下标跟着前移（< 0 = 它已被移出）
                if (hitUnit >= 0)
                {
                    EmgImpactDamage(ref d, hitUnit, kinetic, pusher);
                }
                else if (hitWreck >= 0)
                {
                    EmgWreck wk = d.Wrecks[hitWreck];
                    wk.Hp = math.max(0f, wk.Hp - kinetic);
                    d.Wrecks[hitWreck] = wk;
                }
            }
            float moved = (float)math.distance(from, end);
            if (moved >= PushDMinOf(ref d))
            {
                EmgBusStats st = d.EmgStats[0];
                st.Pushes++;
                d.EmgStats[0] = st;
                EmgUnitEvent(ref d, EmgEventKind.Push, k, pusher >= 0 && pusher < d.Count ? d.Id[pusher] : 0, moved, 0f, cause, end);
            }
            return end;
        }

        /// <summary>撞击扣血：走 DamageFrom（evt.hurt Code = 撞击 6）；推动者的己方不受伤（友伤通知归 FGR-EMG-184，阶段 3）。</summary>
        private static void EmgImpactDamage(ref CombatData d, int u, float kinetic, int pusher)
        {
            if (!(kinetic > 0f) || !d.IsAlive(u) || (pusher >= 0 && pusher < d.Count && d.Faction[pusher] == d.Faction[u]))
            {
                return;
            }
            DamageFrom(ref d, u, kinetic, pusher, EmgCode.SrcImpact);
        }

        private static void EmgCountImpact(ref CombatData d)
        {
            EmgBusStats st = d.EmgStats[0];
            st.Impacts++;
            d.EmgStats[0] = st;
        }

        // ─────────────────────────────── 每步：残骸滑行、被打掉、到期 ───────────────────────────────

        /// <summary>
        /// 5f：残骸每步（有残骸时）。① 滑行中的残骸移动、撞上障碍 / 格网不可走格 / 单位 / 别的残骸就停（≥ emg.impact.v_min 发 evt.impact、扣血）；停住时滑过 ≥ emg.push.d_min 发 evt.push。
        /// ② 耐久归零的移除（被打掉）、到期的崩解（阶段 1 = 计数 + 消失）。O(残骸数)；滑行中的每块另 O(单位 + 残骸 + 障碍)（只在被推动的那零点几秒）。
        /// </summary>
        internal static void StepWrecks(ref CombatData d, float dt)
        {
            bool dirty = false;
            for (int i = 0; i < d.Wrecks.Length; i++)
            {
                EmgWreck k = d.Wrecks[i];
                if (k.Hp > 0f && math.lengthsq(k.Vel) > 0f)
                {
                    int capped = d.EmgS[0].WrecksCapped;
                    EmgSlide(ref d, i, ref k, dt);
                    // 滑行撞死单位、满 256 时新残骸会移出最早的并整表前移：下标跟着前移；k 自己被移出（< 0）就不写回
                    i = math.max(i - (d.EmgS[0].WrecksCapped - capped), -1);
                    if (i >= 0)
                    {
                        d.Wrecks[i] = k;
                    }
                    dirty = true;
                }
            }
            double now = d.Scalars[0].Time;
            EmgScalars es = d.EmgS[0];
            int write = 0;
            for (int i = 0; i < d.Wrecks.Length; i++)
            {
                EmgWreck k = d.Wrecks[i];
                if (!(k.Hp > 0f))
                {
                    es.WrecksDestroyed++;
                    dirty = true;
                    continue;
                }
                if (now >= k.Until)
                {
                    es.WrecksAged++; // shortcut: 阶段 1 崩解 = 计数 + 消失；转化为 mat.debris 碎屑斑块随 EMG2-MAT-01（DEBT-EMG1ENT01-01）
                    dirty = true;
                    continue;
                }
                d.Wrecks[write++] = k;
            }
            d.Wrecks.ResizeUninitialized(write);
            if (dirty)
            {
                es.HashDirty = 1;
            }
            d.EmgS[0] = es;
        }

        private static void EmgSlide(ref CombatData d, int self, ref EmgWreck k, float dt)
        {
            float speed = math.length(k.Vel);
            double2 from = k.Pos;
            // 匀减速按解析位移积分（停在本步内时取整段停止距离），滑行距离与撞击速度不随步长（0.5x～3x）变化
            bool stops = speed - k.Decel * dt <= EmgConst.WreckRestSpeed;
            float travel = stops && k.Decel > 0f ? speed * speed / (2f * k.Decel) : speed * dt - 0.5f * k.Decel * dt * dt;
            double2 to = from + (double2)(k.Vel / speed * travel);
            double best = 2.0;
            int hitUnit = -1, hitWreck = -1;
            if (d.Config.NavEnabled != 0)
            {
                int2 cf = CellOf(from), ct = CellOf(to);
                if ((cf.x != ct.x || cf.y != ct.y) && Walkable(ref d, cf, NavConst.ClassHostile) && !Walkable(ref d, ct, NavConst.ClassHostile))
                {
                    best = 0.0; // 格网上撞到不可走的格（悬崖 / 建筑）
                }
            }
            for (int o = 0; o < d.Obstacles.Length; o++)
            {
                float3 ob = d.Obstacles[o];
                double2 c = new double2(ob.x, ob.y);
                if (math.distance(from, c) <= ob.z + k.Radius)
                {
                    continue;
                }
                double tt = SegmentCircleT(from, to, c, ob.z + k.Radius);
                if (tt >= 0 && tt < best)
                {
                    best = tt;
                    hitUnit = -1;
                    hitWreck = -1;
                }
            }
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck o = d.Wrecks[w];
                if (w == self || !(o.Hp > 0f) || math.distance(from, o.Pos) <= o.Radius + k.Radius)
                {
                    continue;
                }
                double tt = SegmentCircleT(from, to, o.Pos, o.Radius + k.Radius);
                if (tt >= 0 && tt < best)
                {
                    best = tt;
                    hitWreck = w;
                    hitUnit = -1;
                }
            }
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || IsAir(ref d, u) || math.distance(from, d.Pos[u]) <= d.Radius[u] + k.Radius)
                {
                    continue;
                }
                double tt = SegmentCircleT(from, to, d.Pos[u], d.Radius[u] + k.Radius);
                if (tt >= 0 && tt < best)
                {
                    best = tt;
                    hitUnit = u;
                    hitWreck = -1;
                }
            }
            if (best <= 1.0)
            {
                double2 end = from + (to - from) * best;
                float hitDist = (float)math.distance(from, end);
                k.PushDist += hitDist;
                k.Pos = end;
                speed = math.sqrt(math.max(0f, speed * speed - 2f * k.Decel * hitDist)); // 撞点处的速度
                if (speed >= ImpactVMinOf(ref d))
                {
                    float kinetic = k.Mass * speed * speed * d.EmgEntParams[0].KineticImpact;
                    int pusher = d.SlotOf(k.PushBy);
                    int otherId = hitUnit >= 0 ? d.Id[hitUnit] : hitWreck >= 0 ? d.Wrecks[hitWreck].Id : 0;
                    EmgCountImpact(ref d);
                    EmgEntityEvent(ref d, EmgEventKind.Impact, EmgEntKind.Wreck, k.Id, hitUnit >= 0 ? EmgEntKind.Unit : EmgEntKind.Wreck, otherId, speed, k.Mass, 0, end);
                    k.Hp = math.max(0f, k.Hp - kinetic); // 抛射物自己也受这次撞击
                    if (hitUnit >= 0)
                    {
                        EmgImpactDamage(ref d, hitUnit, kinetic, pusher);
                    }
                    else if (hitWreck >= 0)
                    {
                        EmgWreck o = d.Wrecks[hitWreck];
                        o.Hp = math.max(0f, o.Hp - kinetic);
                        d.Wrecks[hitWreck] = o;
                    }
                }
                EmgWreckRest(ref d, ref k);
                return;
            }
            k.Pos = to;
            k.PushDist += travel;
            if (stops)
            {
                EmgWreckRest(ref d, ref k);
                return;
            }
            k.Vel *= (speed - k.Decel * dt) / speed;
        }

        private static void EmgWreckRest(ref CombatData d, ref EmgWreck k)
        {
            if (k.PushDist >= PushDMinOf(ref d))
            {
                EmgBusStats st = d.EmgStats[0];
                st.Pushes++;
                d.EmgStats[0] = st;
                EmgEntityEvent(ref d, EmgEventKind.Push, EmgEntKind.Wreck, k.Id, EmgEntKind.Unit, k.PushBy, k.PushDist, 0f, k.PushCause, k.Pos);
            }
            k.Vel = float2.zero;
            k.Decel = 0f;
            k.PushDist = 0f;
            k.PushBy = 0;
            k.PushCause = 0;
        }

        /// <summary>点所在的那一种圆（有耐久的残骸 / 障碍）：返回原表下标（没有 −1）。只查点所在的哈希格，O(格内圆数)。</summary>
        private static int EmgHashPoint(ref CombatData d, double2 p, byte kind)
        {
            EmgHashEnsure(ref d);
            if (d.EmgHash.Entries.Length == 0
                || !d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(p.x), EmgHash.CellOf(p.y)), out int e, out Unity.Collections.NativeParallelMultiHashMapIterator<long> it))
            {
                return -1;
            }
            int best = -1;
            do
            {
                float4 c = d.EmgHash.Entries[e];
                if ((byte)c.w != kind)
                {
                    continue;
                }
                int idx = d.EmgHash.Refs[e];
                if (kind == EmgHash.KindWreck && (idx >= d.Wrecks.Length || !(d.Wrecks[idx].Hp > 0f)))
                {
                    continue;
                }
                if (math.lengthsq(p - new double2(c.x, c.y)) <= (double)c.z * c.z && (best < 0 || idx < best))
                {
                    best = idx;
                }
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
            return best;
        }
    }
}
