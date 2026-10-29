using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG9-FAC-01（FG09 FGR-FAC-008 静默专属语法）：静默潜伏、偏折场、断链束、伏击（回声猎手）。全部是进入仿真结算的机制（不是动画），
    /// 参数来自行为参数（<see cref="CombatBehaviorProfile"/> 的 Lurk* / Deflect* / Sever*，热更层按敌人表填），状态在 <see cref="CombatData.Silent"/>（随快照进存档，格式 13）。
    ///
    /// - 静默潜伏：静止（不移动、不开火）满 LurkSeconds 秒 → 置 <see cref="CombatUnitFlags.Stealthed"/>：不能被远程锁定（<see cref="FindTarget"/> / 编队攻击 / 直控点击都选不中，
    ///   开火结果 <see cref="CombatFireResult.Hidden"/>）；范围伤害照常命中并立即现形（<see cref="DamageUnit"/>）。移动、开火、被装有信号中继或标记器的己方机器侦测到
    ///   （RevealRange 内）→ 现形；现形后 RevealSeconds 秒内不开火（展桅预警，≥ 0.8 秒）。
    /// - 偏折场：带 <see cref="CombatUnitFlags.DeflectArmed"/> 时按 间隙 → 预警 → 展开 循环；展开时，目标在场内的己方“射弹载体”攻击被反弹：
    ///   即时命中 / 重炮 = 伤害 × DeflectReflect 打回开火者，目标不受伤；飞进场内的己方弹体掉头、改为敌方弹体。格斗 / 力场 / 布区 / 无人机不受影响（近战与场地类构筑占优）。
    /// - 断链束：射程内、视线不被遮挡的“被接入的机器”（<see cref="CombatUnitFlags.Possessed"/>）被持续照射，累计满 SeverSeconds 秒 → 不丢的玩法事件
    ///   <see cref="CombatEventKind.LinkSevered"/>（热更层强制断链）；照射中断即清零；断链后冷却 SeverCooldown 秒。光束本身就是预警线（照射全程可见）。
    /// - 伏击（<see cref="CombatBehavior.Stalker"/>）：见 <see cref="StepStalker"/>。
    /// 每步 O(带静默参数的单位 × 局部网格查询)；与是否被观察无关。
    /// </summary>
    public static partial class CombatLogic
    {
        private static bool HasSilentParams(in CombatBehaviorProfile p) => p.LurkSeconds > 0f || p.DeflectRadius > 0f || p.SeverRange > 0f;

        /// <summary>每步（敌方单位行动之后、网格释放之前）：潜伏 / 偏折场 / 断链束的状态推进。</summary>
        private static void StepSilent(ref CombatData d, ref CombatGrid grid, float dt)
        {
            int n = d.Count;
            int possessed = -2; // 懒查：第一次遇到断链束单位时才扫一遍
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i) || d.Faction[i] != (byte)CombatFaction.Hostile)
                {
                    continue;
                }
                int bp = d.BProfile[i];
                if (bp < 0 || bp >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[bp];
                if (!HasSilentParams(p))
                {
                    continue;
                }
                CombatSilentUnit sl = d.Silent[i];
                if (p.LurkSeconds > 0f)
                {
                    StepLurk(ref d, ref grid, i, in p, ref sl, dt);
                }
                if (p.DeflectRadius > 0f)
                {
                    StepDeflect(ref d, i, in p, ref sl, dt);
                }
                if (p.SeverRange > 0f)
                {
                    if (possessed == -2)
                    {
                        possessed = FindPossessed(ref d);
                    }
                    StepSever(ref d, i, in p, ref sl, possessed, dt);
                }
                d.Silent[i] = sl;
            }
        }

        private static void StepLurk(ref CombatData d, ref CombatGrid grid, int i, in CombatBehaviorProfile p, ref CombatSilentUnit sl, float dt)
        {
            if (sl.Reveal > 0f)
            {
                sl.Reveal = math.max(0f, sl.Reveal - dt);
            }
            bool moved = math.distancesq(d.Pos[i], d.Prev[i]) > 1e-8;
            bool detected = p.RevealRange > 0f && DetectorNear(ref d, ref grid, i, p.RevealRange);
            if (moved || detected)
            {
                sl.Still = 0f;
                if (d.Has(i, CombatUnitFlags.Stealthed))
                {
                    RevealInto(ref d, i, in p, ref sl);
                }
                return;
            }
            sl.Still += dt;
            if (!d.Has(i, CombatUnitFlags.Stealthed) && sl.Reveal <= 0f && sl.Still >= p.LurkSeconds)
            {
                d.Set(i, CombatUnitFlags.Stealthed, true);
                Cue(ref d, CombatEventKind.StealthChanged, i, 0, 0f, d.Pos[i], 1);
            }
        }

        /// <summary>现形：清潜伏位、开始展桅预警（RevealSeconds 秒内不开火）。</summary>
        private static void RevealInto(ref CombatData d, int i, in CombatBehaviorProfile p, ref CombatSilentUnit sl)
        {
            d.Set(i, CombatUnitFlags.Stealthed, false);
            sl.Still = 0f;
            sl.Reveal = math.max(sl.Reveal, p.RevealSeconds);
            Cue(ref d, CombatEventKind.StealthChanged, i, 0, p.RevealSeconds, d.Pos[i], 0);
        }

        /// <summary>按槽位现形（受伤 / 外部调用）：读参数与状态、写回。</summary>
        internal static void RevealSlot(ref CombatData d, int i)
        {
            if (i < 0 || i >= d.Count || !d.Has(i, CombatUnitFlags.Stealthed))
            {
                return;
            }
            int bp = d.BProfile[i];
            CombatBehaviorProfile p = bp >= 0 && bp < d.Profiles.Length ? d.Profiles[bp] : default;
            CombatSilentUnit sl = d.Silent[i];
            RevealInto(ref d, i, in p, ref sl);
            d.Silent[i] = sl;
        }

        /// <summary>
        /// 有潜伏参数的单位找到目标、准备出手时调用：潜伏中 → 现形（开始预警）并返回 true（这一步不出手）；预警还没结束 → true；否则 false（可以出手）。
        /// 让“潜伏 → 出手”之间一定隔着 ≥ RevealSeconds 秒的可见预警（FGR-FAC-008 ①）。
        /// </summary>
        private static bool SilentHold(ref CombatData d, int i)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                return false;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            if (p.LurkSeconds <= 0f)
            {
                return false;
            }
            CombatSilentUnit sl = d.Silent[i];
            if (d.Has(i, CombatUnitFlags.Stealthed))
            {
                RevealInto(ref d, i, in p, ref sl);
                d.Silent[i] = sl;
                return true;
            }
            return sl.Reveal > 0f;
        }

        /// <summary>敌方单位出手：静止计时清零（开火即暴露，下一次潜伏要重新静止满时长）。</summary>
        private static void SilentNoteFired(ref CombatData d, int i)
        {
            if (d.BProfile[i] < 0)
            {
                return;
            }
            CombatSilentUnit sl = d.Silent[i];
            if (sl.Still != 0f)
            {
                sl.Still = 0f;
                d.Silent[i] = sl;
            }
        }

        /// <summary>半径内有没有能揭示潜伏的己方机器（装有信号中继，或主武器带标记功能）。</summary>
        private static bool DetectorNear(ref CombatData d, ref CombatGrid grid, int i, float range)
        {
            double2 pos = d.Pos[i];
            double reach = range + GridSlack;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            double r2 = (double)range * range;
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || math.distancesq(pos, d.Pos[k]) > r2)
                        {
                            continue;
                        }
                        if (d.Has(k, CombatUnitFlags.SignalRelay))
                        {
                            return true;
                        }
                        int w = d.Weapon[k];
                        if (w >= 0 && w < d.Weapons.Length && d.Weapons[w].MarkSeconds > 0f)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static void StepDeflect(ref CombatData d, int i, in CombatBehaviorProfile p, ref CombatSilentUnit sl, float dt)
        {
            if (!d.Has(i, CombatUnitFlags.DeflectArmed))
            {
                if (sl.Deflect != 0)
                {
                    Cue(ref d, CombatEventKind.DeflectPhase, i, 0, p.DeflectRadius, d.Pos[i], 0);
                }
                sl.Deflect = 0;
                sl.DeflectT = p.DeflectGap;
                return;
            }
            sl.DeflectT -= dt;
            if (sl.DeflectT > 0f)
            {
                return;
            }
            // 每段从“这一步”起整段计时（不把上一段的超出量抵进下一段）：玩家从看到预警的那一步起，至少有满额的预警时长（FGR-FAC-008 ①）。
            switch (sl.Deflect)
            {
                case 0:
                    sl.Deflect = 1;
                    sl.DeflectT = p.DeflectWarn;
                    Cue(ref d, CombatEventKind.DeflectPhase, i, 0, p.DeflectRadius, d.Pos[i], 1);
                    break;
                case 1:
                    sl.Deflect = 2;
                    sl.DeflectT = p.DeflectUp;
                    Cue(ref d, CombatEventKind.DeflectPhase, i, 0, p.DeflectRadius, d.Pos[i], 2);
                    break;
                default:
                    sl.Deflect = 0;
                    sl.DeflectT = p.DeflectGap;
                    Cue(ref d, CombatEventKind.DeflectPhase, i, 0, p.DeflectRadius, d.Pos[i], 0);
                    break;
            }
        }

        private static int FindPossessed(ref CombatData d)
        {
            for (int k = 0; k < d.Count; k++)
            {
                if (d.IsAlive(k) && d.Faction[k] == (byte)CombatFaction.Player && d.Has(k, CombatUnitFlags.Possessed))
                {
                    return k;
                }
            }
            return -1;
        }

        private static void StepSever(ref CombatData d, int i, in CombatBehaviorProfile p, ref CombatSilentUnit sl, int possessed, float dt)
        {
            if (sl.SeverT < 0f)
            {
                sl.SeverT = math.min(0f, sl.SeverT + dt);
                sl.SeverTarget = 0;
                return;
            }
            bool valid = possessed >= 0 && d.IsAlive(possessed) && d.Has(possessed, CombatUnitFlags.Possessed)
                         && math.distance(d.Pos[i], d.Pos[possessed]) <= p.SeverRange
                         && (!d.Has(i, CombatUnitFlags.NeedsLos) || LineOfSight(ref d, d.Pos[i], d.Pos[possessed]));
            if (!valid)
            {
                sl.SeverT = 0f;
                sl.SeverTarget = 0;
                return;
            }
            if (d.Has(i, CombatUnitFlags.Stealthed))
            {
                RevealInto(ref d, i, in p, ref sl); // 照射即现形（光束本身就是预警线）
            }
            int targetId = d.Id[possessed];
            if (sl.SeverTarget != targetId)
            {
                sl.SeverTarget = targetId;
                sl.SeverT = 0f;
            }
            sl.Still = 0f;
            sl.SeverT += dt;
            if (sl.SeverT + 1e-6f >= p.SeverSeconds)
            {
                Gameplay(ref d, CombatEventKind.LinkSevered, possessed, d.Id[i], sl.SeverT, 0f, d.Pos[possessed], 0, 0);
                sl.SeverT = -math.max(0f, p.SeverCooldown);
                sl.SeverTarget = 0;
            }
        }

        /// <summary>本步展开中的偏折场（敌方、存活、带 DeflectArmed、阶段 = 展开）。压实之后与每步末尾重算；O(单位)。</summary>
        public static void RebuildDeflectActive(ref CombatData d)
        {
            d.DeflectActive.Clear();
            for (int i = 0; i < d.Count; i++)
            {
                if (d.Silent[i].Deflect == 2 && d.IsAlive(i) && d.Faction[i] == (byte)CombatFaction.Hostile && d.Has(i, CombatUnitFlags.DeflectArmed))
                {
                    d.DeflectActive.Add(i);
                }
            }
        }

        /// <summary>目标 <paramref name="t"/> 是否在某个展开中的偏折场里（返回场源槽位与反弹比例）。O(展开中的偏折场数)。</summary>
        private static bool Deflected(ref CombatData d, double2 at, out int source, out float reflect)
        {
            source = -1;
            reflect = 0f;
            for (int k = 0; k < d.DeflectActive.Length; k++)
            {
                int s = d.DeflectActive[k];
                if (s < 0 || s >= d.Count || !d.IsAlive(s) || d.Silent[s].Deflect != 2)
                {
                    continue;
                }
                int bp = d.BProfile[s];
                if (bp < 0 || bp >= d.Profiles.Length)
                {
                    continue;
                }
                CombatBehaviorProfile p = d.Profiles[bp];
                if (math.distance(d.Pos[s], at) <= p.DeflectRadius)
                {
                    source = s;
                    reflect = p.DeflectReflect;
                    return true;
                }
            }
            return false;
        }

        /// <summary>己方射弹载体的即时 / 重炮攻击打进展开的偏折场：伤害 × 反弹比例打回开火者，目标不受伤。返回是否被反弹。</summary>
        private static bool TryReflectInstant(ref CombatData d, int a, int t, float damage, in CombatWeapon wp)
        {
            if (d.DeflectActive.Length == 0 || d.Faction[a] != (byte)CombatFaction.Player || wp.Reading.Carrier != CombatCarrier.Projectile
                || !Deflected(ref d, d.Pos[t], out int source, out float reflect))
            {
                return false;
            }
            float back = math.max(0f, damage) * reflect;
            // EMG1-BUS-01：即时 / 重炮射弹打进偏折场也是一次反弹（宿主 = 本次出手；法线 = 场源指向开火者）。
            double2 bn = d.Pos[a] - d.Pos[t];
            float2 nrm = math.lengthsq(bn) > 1e-12 ? (float2)math.normalize(bn) : new float2(0f, 1f);
            EmgShotEvent(ref d, EmgEventKind.Bounce, d.Id[source], nrm.x, nrm.y, EmgCode.FaceField, d.Pos[t]);
            if (back > 0f)
            {
                DamageUnit(ref d, a, back, source);
            }
            Cue(ref d, CombatEventKind.Reflected, source, d.Id[a], back, d.Pos[t], 0);
            CombatCounters c = d.Counters[0];
            c.ShotsReflected++;
            d.Counters[0] = c;
            return true;
        }

        /// <summary>飞进展开的偏折场的己方弹体掉头、改为敌方弹体（伤害 × 反弹比例；归属记在场源）。返回是否被反弹。</summary>
        private static bool TryReflectProjectile(ref CombatData d, ref CombatProjectile pr, double2 next)
        {
            if (d.DeflectActive.Length == 0 || pr.Faction != CombatFaction.Player || !Deflected(ref d, next, out int source, out float reflect))
            {
                return false;
            }
            // EMG1-BUS-01：evt.bounce（面 = 场；Value / Value2 = 法线，取掉头后的飞行方向）。反弹回来的弹体归场源、不再带己方读法，也不再带己方程序。
            float2 nrm = math.normalizesafe(-pr.Vel);
            EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, d.Id[source], EmgEntKind.Unit, nrm.x, nrm.y, EmgCode.FaceField, pr.Pos);
            pr.Prog = -1;
            pr.Vel = -pr.Vel;
            pr.Faction = CombatFaction.Hostile;
            pr.Owner = d.Id[source];
            pr.Weapon = -1; // 反弹回来的弹体不再带己方读法
            // EMG1-TRIG-01 审查修复：也不再是己方的 SpawnFlight 根弹体 / 治疗或子弹药 / 过载这一发——否则治疗弹会去修敌人、命中致死会给场源补发击毁回报。
            pr.Flags &= ~(EmgProjFlags.Flight | EmgProjFlags.Heal | EmgProjFlags.Sub | EmgProjFlags.Overload | EmgProjFlags.ViaDrone);
            pr.IgnoreUnit = 0;
            // EMG1-GRM-01 审查修复：己方程序给的弹道修饰（寻的 / 穿透 / 反弹 / 折返 / 绕行 / 抛射）一并清掉，反弹回来的弹体沿反射方向直飞。
            pr.Mods = EmgProjMods.None;
            pr.ModQ = 0;
            pr.HomingTarget = 0;
            pr.PierceLeft = 0;
            pr.BounceLeft = 0;
            pr.BounceObs = 0;
            pr.Intercepts = 0;
            pr.Flags &= ~EmgProjFlags.Lob;
            pr.Damage = math.max(0f, pr.Damage) * reflect;
            Cue(ref d, CombatEventKind.Reflected, source, 0, pr.Damage, pr.Pos, 1);
            CombatCounters c = d.Counters[0];
            c.ShotsReflected++;
            d.Counters[0] = c;
            return true;
        }

        /// <summary>
        /// 伏击（回声猎手）：在出生点潜伏；感知范围内出现己方机器（装有信号中继的优先，其次最近）且离出生点不超过牵引半径 + 感知范围 → 现形（预警期间原地不动）→ 追上去，
        /// 进入射程后按瞄准线两段式出手（AimSeconds，≥ 0.8 秒预警）；目标丢失 / 被拉出牵引范围 → 回出生点，静止后重新潜伏。
        /// </summary>
        private static void StepStalker(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int w = d.Weapon[i];
            int bp = d.BProfile[i];
            if (w < 0 || bp < 0 || !d.Has(i, CombatUnitFlags.WeaponEnabled))
            {
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            CombatBehaviorProfile p = d.Profiles[bp];
            float speed = SlowedSpeed(ref d, i, p.Speed);
            double2 pos = d.Pos[i];
            CombatCommand cmd = d.Cmd[i];
            if (d.Secondary[i] > 0f)
            {
                float sec = d.Secondary[i] - dt;
                d.Secondary[i] = math.max(0f, sec);
                if (sec > 0f)
                {
                    return;
                }
                int locked = d.SlotOf(cmd.Target);
                d.Cycle[i] = EffectiveCooldown(wp);
                if (locked >= 0 && d.IsAlive(locked) && d.Has(locked, CombatUnitFlags.Targetable)
                    && math.distance(pos, d.Pos[locked]) <= EngageRange(ref d, i, wp) + d.Radius[locked] + 0.5f)
                {
                    Cue(ref d, CombatEventKind.Telegraph, i, d.Id[locked], 0f, pos, 2);
                    UnitAttack(ref d, i, locked, wp);
                }
                else
                {
                    Cue(ref d, CombatEventKind.Telegraph, i, 0, 0f, pos, 1);
                }
                return;
            }
            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            int t = FindStalkTarget(ref d, ref grid, i, p.SenseRange);
            if (t >= 0 && p.Leash > 0f && math.distance(d.Home[i], d.Pos[t]) > p.Leash + p.SenseRange)
            {
                t = -1;
            }
            if (t < 0)
            {
                cmd.Target = 0;
                d.Cmd[i] = cmd;
                MoveToward(ref d, i, pos, d.Home[i], speed * dt, 0.3);
                return;
            }
            cmd.Target = d.Id[t];
            d.Cmd[i] = cmd;
            if (SilentHold(ref d, i))
            {
                return;
            }
            double dist = math.distance(pos, d.Pos[t]);
            float engage = EngageRange(ref d, i, wp);
            if (dist > engage)
            {
                MoveToward(ref d, i, pos, d.Pos[t], speed * dt, engage * 0.9);
                Separate(ref d, ref grid, i, speed, dt);
                return;
            }
            if (c > 0f)
            {
                return;
            }
            if (wp.AimSeconds > 0f)
            {
                d.Secondary[i] = wp.AimSeconds;
                Cue(ref d, CombatEventKind.Telegraph, i, d.Id[t], 0f, pos, 0);
                return;
            }
            d.Cycle[i] = EffectiveCooldown(wp);
            UnitAttack(ref d, i, t, wp);
        }

        /// <summary>伏击目标：感知范围内的己方机器，装有信号中继的排在前面，同一类里最近（视线按单位标志）。</summary>
        private static int FindStalkTarget(ref CombatData d, ref CombatGrid grid, int i, float range)
        {
            if (range <= 0f)
            {
                return -1;
            }
            double2 pos = d.Pos[i];
            bool needsLos = d.Has(i, CombatUnitFlags.NeedsLos);
            double reach = range + GridSlack;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            int best = -1;
            int bestRank = 2;
            double bestDist = double.MaxValue;
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable)
                            || d.Kind[k] != (byte)CombatUnitKind.Machine)
                        {
                            continue;
                        }
                        double dist = math.distance(pos, d.Pos[k]);
                        if (dist > range)
                        {
                            continue;
                        }
                        int rank = d.Has(k, CombatUnitFlags.SignalRelay) ? 0 : 1;
                        bool better = rank < bestRank || (rank == bestRank && (dist < bestDist || (dist == bestDist && k > best)));
                        if (!better || (needsLos && !LineOfSight(ref d, pos, d.Pos[k])))
                        {
                            continue;
                        }
                        best = k;
                        bestRank = rank;
                        bestDist = dist;
                    }
                }
            }
            return best;
        }
    }
}
