using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG9-FAC-02（FG09 FGR-FAC-008 铸造专属语法）：齐射弹幕（对空高炮 / 空爆炮台 / 主核心对空形态）、布雷与子母雷、装填光环、正面推进冲锋（攻城重甲）、
    /// 架炮驻锄与高抛曲射的近距盲区（步进炮）。全部是进入仿真结算的机制（不是动画），参数来自行为参数（<see cref="CombatBehaviorProfile"/> 的 Salvo* / Mine* / Reload* /
    /// Charge* / Deploy* / MinRange，热更层按敌人表填），状态在 <see cref="CombatData.Foundry"/>（随快照进存档，格式 14）。
    ///
    /// - 齐射弹幕：装填（SalvoReload）→ 发现目标 → 装填完成灯 + 炮塔转向（SalvoWarn ≥ 0.8 秒，提示事件 FoundryCue/1）→ 一轮 SalvoCount 发、每发间隔 SalvoGap。
    ///   模式 1（对空）：每发击落射程内最近的一个己方无人机或飞行中的己方弹体，不打地面单位（“地面近战”就是应对）；模式 2（空爆）：每发在锁定目标处空爆，
    ///   半径 SalvoRadius 内的己方地面单位受伤、无人机被击落（“对无人机和密集阵型伤害很高”）。
    /// - 子母雷：带 Mine* 参数的敌方单位（布雷车；攻城中的布雷车也一样）每 MineCycle 秒在脚下铺一颗（自己最多 MineMax 颗、整个地点最多 MineCap 颗）。
    ///   雷是内核单位（<see cref="CombatBehavior.Mine"/>，<see cref="CombatUnitFlags.AreaOnly"/>：不能被单体锁定）：己方单位进入半径 → 引信 MineFuse 秒（预警，FoundryCue/4）→
    ///   爆炸；被任何伤害打到（范围伤害）或被牵引挪动 → 提前引爆；MineLife 秒后自行失效。
    /// - 装填光环：带 ReloadRange 的敌方单位让半径内的友军火炮（瞄准线 / 齐射读法、对空形态）冷却与装填额外走 ReloadRate 倍；同一步只加速一次（不叠加）。
    /// - 正面推进冲锋：转向受限（TurnRate 度/秒）、只沿正面推进；目标在正前方（≤ 15°）且在感知距离内 → 蓄力 ChargeWarn 秒（FoundryCue/6）→ 以 ChargeSpeed 冲锋 ChargeSeconds 秒，
    ///   撞到的己方单位吃武器伤害并被击退 ChargeKnock 米（一次冲锋每个单位只撞一次）→ 冷却 ChargeCooldown；贴身时用武器（撞角）攻击。正面高减伤、侧后加成由装甲与 BackHit 结算。
    /// - 架炮驻锄：瞄准线读法带 DeploySeconds 时，第一次发现目标先架设（FoundryCue/7），架设中被挪动（击退 / 牵引）或被冻结（减速 ≥ 90%）→ 回到未架设、重新计时（FG09 第 10 节负向）；
    ///   高抛曲射：目标在 MinRange 内打不到（盲区）。
    /// 每步 O(带铸造参数的单位 × 局部查询)；与是否被观察无关（FGR-BASE-021）。
    /// </summary>
    public static partial class CombatLogic
    {
        public const int DefaultMineCap = 12;
        /// <summary>冲锋要求目标在正前方的半角（度）。</summary>
        private const float ChargeAimDeg = 15f;

        // ─────────────────────────────── 每步：与行为无关的部分 ───────────────────────────────

        /// <summary>敌方单位行动之后：装填光环、铺雷、对空形态的齐射（O(带参数单位)）。</summary>
        private static void StepFoundry(ref CombatData d, ref CombatGrid grid, float dt)
        {
            int n = d.Count;
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i) || d.Faction[i] != (byte)CombatFaction.Hostile || IsFrozen(ref d, i))
                {
                    continue; // FG10-FAC-01：冻住的单位不铺雷、不加速装填、不齐射
                }
                int bp = d.BProfile[i];
                if (bp < 0 || bp >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[bp];
                byte beh = d.Behavior[i];
                if (p.ReloadRange > 0f && p.ReloadRate > 0f)
                {
                    StepReloadAura(ref d, i, in p, dt);
                }
                if (p.MineMax > 0f && beh != (byte)CombatBehavior.Mine)
                {
                    StepLayMines(ref d, i, in p, dt);
                }
                if (p.SalvoCount > 0f && beh != (byte)CombatBehavior.Salvo && d.Foundry[i].AirMode != 0)
                {
                    // 对空形态的齐射不改朝向：主核心是固定结构，侧后加成按出生朝向判定（与直控路径 FoundryOutpostCoreBoss.IsBackHit 同一个朝向）。
                    StepSalvoCore(ref d, ref grid, i, in p, dt, 1f, false);
                }
            }
        }

        /// <summary>熔渣热区内的散热倍率（不在区里 = 1）。O(区数，个位数)。</summary>
        internal static float SlagFactor(ref CombatData d, int i)
        {
            if (d.SlagZones.Length == 0)
            {
                return 1f;
            }
            double2 pos = d.Pos[i];
            for (int k = 0; k < d.SlagZones.Length; k++)
            {
                float3 c = d.SlagZones[k];
                double dx = pos.x - c.x;
                double dy = pos.y - c.y;
                if (dx * dx + dy * dy <= (double)c.z * c.z)
                {
                    return d.SlagMult > 0f ? d.SlagMult : 1f;
                }
            }
            return 1f;
        }

        // ─────────────────────────────── 齐射弹幕 ───────────────────────────────

        private static void StepSalvoUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length || !d.Has(i, CombatUnitFlags.WeaponEnabled))
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            if (p.SalvoCount <= 0f)
            {
                return;
            }
            StepSalvoCore(ref d, ref grid, i, in p, dt, p.SalvoMode, true);
        }

        /// <summary>齐射状态机（mode 1 = 对空，2 = 空爆）。<paramref name="turn"/> = 预警时转向目标（齐射单位）；对空形态的主核心传 false（朝向不变）。</summary>
        private static void StepSalvoCore(ref CombatData d, ref CombatGrid grid, int i, in CombatBehaviorProfile p, float dt, float mode, bool turn)
        {
            int w = d.Weapon[i];
            if (w < 0 || w >= d.Weapons.Length)
            {
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            float range = wp.Range;
            bool air = mode < 1.5f;
            CombatFoundryUnit f = d.Foundry[i];
            switch (f.Salvo)
            {
                case 0:
                {
                    if (f.SalvoT > 0f)
                    {
                        f.SalvoT = math.max(0f, f.SalvoT - dt);
                        break;
                    }
                    int target;
                    double2 at;
                    if (air)
                    {
                        if (!NearestAirTarget(ref d, i, range, out at, out _, out _))
                        {
                            break;
                        }
                        target = 0;
                    }
                    else
                    {
                        int t = FindTarget(ref d, ref grid, i, range, d.Has(i, CombatUnitFlags.NeedsLos), CombatTargetMode.Nearest);
                        if (t < 0)
                        {
                            break;
                        }
                        target = d.Id[t];
                        at = d.Pos[t];
                    }
                    f.Salvo = 1;
                    f.SalvoT = math.max(0.8f, p.SalvoWarn);
                    f.SalvoTarget = target;
                    if (turn)
                    {
                        TurnToward(ref d, i, at, 360f);
                    }
                    Cue2(ref d, i, target, f.SalvoT, d.Pos[i], 1, air ? (byte)1 : (byte)2);
                    break;
                }
                case 1:
                {
                    f.SalvoT -= dt;
                    if (f.SalvoT > 0f)
                    {
                        break;
                    }
                    f.Salvo = 2;
                    f.SalvoLeft = math.max(1, (int)math.round(p.SalvoCount));
                    f.SalvoT = 0f;
                    CombatCounters c = d.Counters[0];
                    c.Salvos++;
                    d.Counters[0] = c;
                    break;
                }
                default:
                {
                    f.SalvoT -= dt;
                    int guard = 0;
                    while (f.SalvoT <= 0f && f.SalvoLeft > 0 && guard++ < 16)
                    {
                        if (air)
                        {
                            InterceptOne(ref d, i, range);
                        }
                        else
                        {
                            AirburstRound(ref d, ref grid, i, in p, in wp, ref f, range);
                        }
                        f.SalvoLeft--;
                        f.SalvoT += math.max(0.05f, p.SalvoGap);
                    }
                    if (f.SalvoLeft <= 0)
                    {
                        f.Salvo = 0;
                        f.SalvoLeft = 0;
                        f.SalvoTarget = 0;
                        f.SalvoT = math.max(0.1f, p.SalvoReload);
                    }
                    break;
                }
            }
            d.Foundry[i] = f;
        }

        /// <summary>射程内最近的空中目标：己方无人机（还在场）或飞行中的己方弹体。kind 0 = 无人机，1 = 弹体；index = 在各自列表里的下标。</summary>
        private static bool NearestAirTarget(ref CombatData d, int i, float range, out double2 at, out int kind, out int index)
        {
            double2 pos = d.Pos[i];
            double best = (double)range * range;
            at = pos;
            kind = -1;
            index = -1;
            double now = d.Scalars[0].Time;
            for (int q = 0; q < d.Drones.Length; q++)
            {
                CombatDrone dr = d.Drones[q];
                if (dr.Faction != CombatFaction.Player || dr.Until <= now)
                {
                    continue;
                }
                double ds = math.distancesq(pos, dr.Pos);
                if (ds <= best)
                {
                    best = ds;
                    at = dr.Pos;
                    kind = 0;
                    index = q;
                }
            }
            for (int q = 0; q < d.Projectiles.Length; q++)
            {
                CombatProjectile pr = d.Projectiles[q];
                if (pr.Faction != CombatFaction.Player)
                {
                    continue;
                }
                double ds = math.distancesq(pos, pr.Pos);
                if (ds < best)
                {
                    best = ds;
                    at = pr.Pos;
                    kind = 1;
                    index = q;
                }
            }
            return kind >= 0;
        }

        /// <summary>对空一发：击落射程内最近的一个空中目标（无人机到期、弹体移除）。没有目标这一发打空。</summary>
        private static void InterceptOne(ref CombatData d, int i, float range)
        {
            if (!NearestAirTarget(ref d, i, range, out double2 at, out int kind, out int index))
            {
                return;
            }
            if (kind == 0)
            {
                CombatDrone dr = d.Drones[index];
                dr.Until = d.Scalars[0].Time;
                d.Drones[index] = dr;
            }
            else
            {
                RemoveProjectileAt(ref d, index);
            }
            CombatCounters c = d.Counters[0];
            c.AirIntercepts++;
            d.Counters[0] = c;
            Cue2(ref d, i, 0, 0f, at, 2, (byte)(kind + 1));
        }

        private static void RemoveProjectileAt(ref CombatData d, int index)
        {
            int m = d.Projectiles.Length;
            for (int q = index; q < m - 1; q++)
            {
                d.Projectiles[q] = d.Projectiles[q + 1];
            }
            d.Projectiles.ResizeUninitialized(m - 1);
        }

        /// <summary>空爆一发：在锁定目标（不在了 / 出了射程就取最近的）处爆开，半径内己方地面单位受伤、无人机被击落。</summary>
        private static void AirburstRound(ref CombatData d, ref CombatGrid grid, int i, in CombatBehaviorProfile p, in CombatWeapon wp, ref CombatFoundryUnit f, float range)
        {
            int t = d.SlotOf(f.SalvoTarget);
            if (t < 0 || !d.IsAlive(t) || math.distance(d.Pos[t], d.Pos[i]) > range + d.Radius[t] || d.Has(t, CombatUnitFlags.Stealthed))
            {
                t = FindTarget(ref d, ref grid, i, range, d.Has(i, CombatUnitFlags.NeedsLos), CombatTargetMode.Nearest);
                f.SalvoTarget = t >= 0 ? d.Id[t] : 0;
            }
            if (t < 0)
            {
                return;
            }
            double2 center = d.Pos[t];
            float radius = math.max(0.5f, p.SalvoRadius);
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], center) <= radius + d.Radius[k])
                {
                    DamageFrom(ref d, k, wp.Damage, i, EmgCode.SrcProjectile);
                }
            }
            double now = d.Scalars[0].Time;
            for (int q = 0; q < d.Drones.Length; q++)
            {
                CombatDrone dr = d.Drones[q];
                if (dr.Faction == CombatFaction.Player && dr.Until > now && math.distance(dr.Pos, center) <= radius)
                {
                    dr.Until = now;
                    d.Drones[q] = dr;
                    CombatCounters c = d.Counters[0];
                    c.AirIntercepts++;
                    d.Counters[0] = c;
                }
            }
            CombatCounters sc = d.Counters[0];
            sc.ShotsFired++;
            d.Counters[0] = sc;
            Cue2(ref d, i, f.SalvoTarget, radius, center, 3, 0);
        }

        // ─────────────────────────────── 装填光环 ───────────────────────────────

        private static void StepReloadAura(ref CombatData d, int i, in CombatBehaviorProfile p, float dt)
        {
            long step = d.Scalars[0].Steps;
            double r2 = (double)p.ReloadRange * p.ReloadRange;
            float extra = dt * p.ReloadRate;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == i || !d.IsAlive(k) || d.Faction[k] != d.Faction[i] || math.distancesq(d.Pos[k], d.Pos[i]) > r2)
                {
                    continue;
                }
                CombatFoundryUnit f = d.Foundry[k];
                bool artillery = d.Behavior[k] == (byte)CombatBehavior.Telegraph || d.Behavior[k] == (byte)CombatBehavior.Salvo || f.AirMode != 0;
                if (!artillery || f.BoostStep == step + 1)
                {
                    continue;
                }
                f.BoostStep = step + 1;
                if (d.Cycle[k] > 0f)
                {
                    d.Cycle[k] = math.max(0f, d.Cycle[k] - extra);
                }
                if (f.Salvo == 0 && f.SalvoT > 0f)
                {
                    f.SalvoT = math.max(0f, f.SalvoT - extra);
                }
                d.Foundry[k] = f;
            }
        }

        // ─────────────────────────────── 布雷与子母雷 ───────────────────────────────

        private static void StepLayMines(ref CombatData d, int i, in CombatBehaviorProfile p, float dt)
        {
            CombatFoundryUnit f = d.Foundry[i];
            f.LayT -= dt;
            if (f.LayT > 0f)
            {
                d.Foundry[i] = f;
                return;
            }
            f.LayT = math.max(0.5f, p.MineCycle);
            d.Foundry[i] = f;
            int mine = d.Id[i];
            int own = 0;
            int total = 0;
            double2 pos = d.Pos[i];
            bool crowded = false;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (!d.IsAlive(k) || d.Behavior[k] != (byte)CombatBehavior.Mine)
                {
                    continue;
                }
                total++;
                if (d.Foundry[k].MineOwner == mine)
                {
                    own++;
                }
                if (math.distancesq(d.Pos[k], pos) < (double)p.MineRadius * p.MineRadius)
                {
                    crowded = true; // 不在已有的雷区里叠雷（雷区之间留出可辨认的间隔）
                }
            }
            int cap = d.MineCap > 0 ? d.MineCap : DefaultMineCap;
            if (own >= (int)math.round(p.MineMax) || total >= cap || crowded)
            {
                return;
            }
            CombatScalars s = d.Scalars[0];
            int id = s.NextId++;
            s.Revision++;
            d.Scalars[0] = s;
            int slot = d.Append(new CombatSpawn
            {
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.Mine,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.AreaOnly
                        | CombatUnitFlags.HealthFloor | CombatUnitFlags.RemoveOnDeath,
                Position = pos,
                Home = pos,
                Radius = 0.6f,
                Health = 1f,
                MaxHealth = 1f,
                Weapon = -1,
                BehaviorProfile = d.BProfile[i],
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, -1f),
            }, id);
            CombatFoundryUnit mf = default;
            mf.MineOwner = mine;
            mf.MineLife = p.MineLife > 0f ? p.MineLife : 120f;
            d.Foundry[slot] = mf;
            CombatCounters c = d.Counters[0];
            c.MinesLaid++;
            d.Counters[0] = c;
            Cue2(ref d, slot, mine, p.MineRadius, pos, 8, 0);
        }

        private static void StepMine(ref CombatData d, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                Kill(ref d, i, 0);
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            CombatFoundryUnit f = d.Foundry[i];
            bool early = d.Hp[i] < d.MaxHp[i] || math.distancesq(d.Pos[i], d.Home[i]) > 0.0025;
            if (early)
            {
                Detonate(ref d, i, in p);
                return;
            }
            f.MineLife -= dt;
            if (f.MineState == 0)
            {
                if (f.MineLife <= 0f)
                {
                    Kill(ref d, i, 0); // 失效：不爆炸，直接消失
                    return;
                }
                if (AnyPlayerWithin(ref d, d.Pos[i], p.MineRadius))
                {
                    f.MineState = 1;
                    f.MineT = math.max(0.8f, p.MineFuse);
                    Cue2(ref d, i, 0, f.MineT, d.Pos[i], 4, 0);
                }
                d.Foundry[i] = f;
                return;
            }
            f.MineT -= dt;
            d.Foundry[i] = f;
            if (f.MineT <= 0f)
            {
                Detonate(ref d, i, in p);
            }
        }

        private static bool AnyPlayerWithin(ref CombatData d, double2 pos, float radius)
        {
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] == (byte)CombatFaction.Player && d.IsAlive(k) && math.distance(d.Pos[k], pos) <= radius + d.Radius[k])
                {
                    return true;
                }
            }
            return false;
        }

        private static void Detonate(ref CombatData d, int i, in CombatBehaviorProfile p)
        {
            double2 pos = d.Pos[i];
            float radius = math.max(0.5f, p.MineRadius);
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], pos) <= radius + d.Radius[k])
                {
                    DamageFrom(ref d, k, p.MineDamage, i, EmgCode.SrcZone);
                }
            }
            CombatCounters c = d.Counters[0];
            c.MinesDetonated++;
            d.Counters[0] = c;
            Cue2(ref d, i, 0, radius, pos, 5, 0);
            d.Set(i, CombatUnitFlags.HealthFloor, false);
            Kill(ref d, i, 0);
        }

        // ─────────────────────────────── 布雷车 / 装填机的移动 ───────────────────────────────

        /// <summary>受威胁后撤（不超出牵引半径）；否则在牵引中心附近摆动巡逻（布雷车）或回到牵引中心（装填机）。</summary>
        private static void StepSupportMove(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            float speed = SlowedSpeed(ref d, i, p.Speed);
            if (speed <= 0f)
            {
                return;
            }
            double2 pos = d.Pos[i];
            int threat = p.FleeTrigger > 0f ? FindTarget(ref d, ref grid, i, p.FleeTrigger, false, CombatTargetMode.Nearest) : -1;
            if (threat >= 0)
            {
                double2 away = pos - d.Pos[threat];
                if (math.lengthsq(away) > 0.0001)
                {
                    double2 proposed = pos + math.normalize(away) * (speed * dt);
                    if (p.Leash <= 0f || math.distance(proposed, d.Home[i]) <= p.Leash)
                    {
                        d.Pos[i] = MoveCollide(ref d, i, pos, proposed);
                    }
                }
                return;
            }
            double2 goal = d.Home[i];
            if (p.PatrolRadius > 0f && p.PatrolFreq > 0f)
            {
                double ph = d.Scalars[0].Time * p.PatrolFreq;
                goal += new double2(math.cos(ph), math.sin(ph * 0.7)) * p.PatrolRadius;
            }
            MoveToward(ref d, i, pos, goal, speed * dt, 0.3);
        }

        // ─────────────────────────────── 正面推进冲锋 ───────────────────────────────

        private static void StepCharger(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            int w = d.Weapon[i];
            if (bp < 0 || bp >= d.Profiles.Length || w < 0 || w >= d.Weapons.Length)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            CombatWeapon wp = d.Weapons[w];
            CombatFoundryUnit f = d.Foundry[i];
            double2 pos = d.Pos[i];
            switch (f.Charge)
            {
                case 1:
                    f.ChargeT -= dt;
                    if (f.ChargeT <= 0f)
                    {
                        f.Charge = 2;
                        f.ChargeT = math.max(0.1f, p.ChargeSeconds);
                        f.ChargeSeq++;
                        CombatCounters c = d.Counters[0];
                        c.Charges++;
                        d.Counters[0] = c;
                    }
                    d.Foundry[i] = f;
                    return;
                case 2:
                {
                    double2 next = pos + (double2)(f.ChargeDir * (p.ChargeSpeed * dt));
                    double2 moved = MoveCollide(ref d, i, pos, next);
                    bool blocked = math.distancesq(moved, pos) < 1e-6;
                    d.Pos[i] = moved;
                    RamOverlaps(ref d, i, in p, in wp, ref f);
                    f.ChargeT -= dt;
                    if (f.ChargeT <= 0f || blocked)
                    {
                        f.Charge = 0;
                        d.Secondary[i] = math.max(0.1f, p.ChargeCooldown);
                    }
                    d.Foundry[i] = f;
                    return;
                }
            }
            // 推进：冷却、找目标、受限转向、只沿正面前进。
            if (d.Secondary[i] > 0f)
            {
                d.Secondary[i] = math.max(0f, d.Secondary[i] - dt);
            }
            float c0 = d.Cycle[i] - dt;
            d.Cycle[i] = c0;
            int t = FindTarget(ref d, ref grid, i, p.SenseRange, false, CombatTargetMode.Nearest);
            if (t < 0)
            {
                if (p.Leash > 0f && math.distance(pos, d.Home[i]) > 1.0)
                {
                    bool aligned = TurnToward(ref d, i, d.Home[i], math.max(1f, p.TurnRate) * dt);
                    if (aligned)
                    {
                        MoveToward(ref d, i, pos, d.Home[i], SlowedSpeed(ref d, i, p.Speed) * dt, 0.5);
                    }
                }
                return;
            }
            bool facing = TurnToward(ref d, i, d.Pos[t], math.max(1f, p.TurnRate) * dt);
            float4 ar = d.Armor[i];
            float2 dirF = math.lengthsq(ar.zw) > 1e-12f ? math.normalize(ar.zw) : new float2(0f, 1f);
            double2 to = d.Pos[t] - pos;
            double dist = math.length(to);
            float reach = wp.Range + d.Radius[i] + d.Radius[t];
            float cos = dist > 1e-6 ? math.dot(dirF, (float2)(to / dist)) : 1f;
            if (dist <= reach)
            {
                if (c0 <= 0f && facing)
                {
                    d.Cycle[i] = EffectiveCooldown(wp);
                    UnitAttack(ref d, i, t, wp);
                }
                return;
            }
            if (p.ChargeSpeed > 0f && d.Secondary[i] <= 0f && cos >= math.cos(math.radians(ChargeAimDeg)))
            {
                f.Charge = 1;
                f.ChargeT = math.max(0.8f, p.ChargeWarn);
                f.ChargeDir = dirF;
                f.AnchorPos = pos;
                d.Foundry[i] = f;
                Cue2(ref d, i, d.Id[t], f.ChargeT, pos, 6, 0);
                return;
            }
            // 正面推进：只沿当前朝向前进（转向半径大），对准之前不横移。
            if (cos > 0.5f)
            {
                double step = SlowedSpeed(ref d, i, p.Speed) * dt;
                d.Pos[i] = MoveCollide(ref d, i, pos, pos + (double2)(dirF * (float)math.min(step, dist - reach * 0.9)));
            }
        }

        /// <summary>冲锋中：与本体重叠的己方单位各吃一次撞击（武器伤害，正面装甲照常结算）并被沿冲锋方向击退。</summary>
        private static void RamOverlaps(ref CombatData d, int i, in CombatBehaviorProfile p, in CombatWeapon wp, ref CombatFoundryUnit f)
        {
            int self = d.Id[i];
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], d.Pos[i]) > d.Radius[i] + d.Radius[k] + 0.3)
                {
                    continue;
                }
                CombatFoundryUnit v = d.Foundry[k];
                if (v.RamBy == self && v.RamSeq == f.ChargeSeq)
                {
                    continue;
                }
                v.RamBy = self;
                v.RamSeq = f.ChargeSeq;
                d.Foundry[k] = v;
                float dmg = wp.Damage;
                if (IsFrontalArmored(ref d, k, d.Pos[i]))
                {
                    dmg *= math.max(0f, 1f - d.Armor[k].x);
                }
                DamageFrom(ref d, k, dmg, i, EmgCode.SrcImpact);
                if (p.ChargeKnock > 0f && CanDisplace(ref d, k))
                {
                    double2 from = d.Pos[k];
                    d.Pos[k] = MoveCollide(ref d, k, from, from + (double2)(f.ChargeDir * p.ChargeKnock));
                }
                Cue2(ref d, i, d.Id[k], dmg, d.Pos[k], 9, 0);
            }
        }

        // ─────────────────────────────── 架炮驻锄 / 盲区（瞄准线读法） ───────────────────────────────

        /// <summary>
        /// 瞄准线读法每步开头调用：带架设参数时管理架设状态。返回 true = 本步不进入瞄准 / 开火（未架设、正在架设或刚被打断）。
        /// </summary>
        private static bool DeployHold(ref CombatData d, ref CombatGrid grid, int i, float dt, in CombatWeapon wp, bool los)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                return false;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            if (p.DeploySeconds <= 0f)
            {
                return false;
            }
            CombatFoundryUnit f = d.Foundry[i];
            bool disturbed = f.Deploy != 0 && (math.distancesq(d.Pos[i], f.AnchorPos) > 0.0025 || Frozen(ref d, i));
            if (disturbed)
            {
                // FG09 第 10 节负向：架设到一半（或已架好）被击退 / 冻结 → 回到未架设，重新计算架设时间；瞄准中的这一发作废。
                f.Deploy = 0;
                f.DeployT = 0f;
                d.Secondary[i] = 0f;
                d.Foundry[i] = f;
                return true;
            }
            if (f.Deploy == 2)
            {
                return false;
            }
            if (f.Deploy == 1)
            {
                f.DeployT -= dt;
                if (f.DeployT <= 0f)
                {
                    f.Deploy = 2;
                    f.DeployT = 0f;
                }
                d.Foundry[i] = f;
                return true;
            }
            if (Frozen(ref d, i))
            {
                return true;
            }
            int t = FindTarget(ref d, ref grid, i, EngageRange(ref d, i, wp), los, wp.TargetMode);
            if (t < 0)
            {
                return true;
            }
            f.Deploy = 1;
            f.DeployT = math.max(0.8f, p.DeploySeconds);
            f.AnchorPos = d.Pos[i];
            d.Foundry[i] = f;
            Cue2(ref d, i, d.Id[t], f.DeployT, d.Pos[i], 7, 0);
            return true;
        }

        /// <summary>被冻结（减速状态 ≥ 90%，或 FG10-FAC-01 液氮受寒满）。</summary>
        private static bool Frozen(ref CombatData d, int i) => (d.StatusSlow[i] >= 0.9f && d.StatusActive(i, d.Scalars[0].Time)) || IsFrozen(ref d, i);

        /// <summary>高抛曲射的近距盲区：目标比 MinRange 更近（中心距减去两者半径）就打不到。</summary>
        private static bool InBlindSpot(ref CombatData d, int i, int t)
        {
            int bp = d.BProfile[i];
            if (t < 0 || bp < 0 || bp >= d.Profiles.Length)
            {
                return false;
            }
            float min = d.Profiles[bp].MinRange;
            return min > 0f && math.distance(d.Pos[i], d.Pos[t]) - d.Radius[t] < min;
        }

        internal static void Cue2(ref CombatData d, int slot, int other, float v, double2 pos, byte code, byte code2)
        {
            int before = d.Cues.Length;
            Cue(ref d, CombatEventKind.FoundryCue, slot, other, v, pos, code);
            if (d.Cues.Length > before)
            {
                CombatEvent e = d.Cues[d.Cues.Length - 1];
                e.Code2 = code2;
                d.Cues[d.Cues.Length - 1] = e;
            }
        }
    }
}
