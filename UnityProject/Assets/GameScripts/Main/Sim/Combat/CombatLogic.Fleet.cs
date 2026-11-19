using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG13-FAC-01（FG09 第 8 章、FGR-FAC-008 白潮专属语法）：编队飞行（完整编队跟随长机的目标、打散后射速 / 速度下降）、灭活光束（预瞄线 → 光束扫过的格变成灭活焦土，
    /// 来源被毁后逐格恢复）、轨道打击（信标标记区域 → 倒计时 → 打击；倒计时内信标被毁 = 取消）、轨道锚点空降（下落中不可被攻击，落地发玩法事件由热更层投放）、
    /// 护卫护盾（先吸收伤害；带电荷时 × 电磁倍率、不回充）、屏蔽塔（只登记，接入判定由热更层经 <see cref="CombatKernel.AnyUplinkBlockAt"/> 读）。
    /// 规则只看行为参数（表列），不认任何单位 ID（IC-REQ-012、FGR-EMG-160）；敌我同一规则（FGR-FAC-003）。
    /// 开销：三张稀疏表，全空时每步三次长度比较；编队 O(编队单位²)；打击伤害 O(N)，只在打击那一步；焦土 O(焦土格数 ≤ 256)。
    /// </summary>
    public static partial class CombatLogic
    {
        internal static int FlIndexOf(ref CombatData d, int unitId)
        {
            for (int k = 0; k < d.Fleets.Length; k++)
            {
                if (d.Fleets[k].Unit == unitId)
                {
                    return k;
                }
            }
            return -1;
        }

        /// <summary>生成 / 换参数行后登记运行状态（没有白潮参数 → 删掉旧记录）。登陆舱从这一刻开始下落：不可被攻击、不可选中，落点预警提示。</summary>
        internal static void FlRegister(ref CombatData d, int slot)
        {
            int k = FlIndexOf(ref d, d.Id[slot]);
            bool want = OcProfile(ref d, slot, out CombatBehaviorProfile p) && p.HasFleet;
            if (!want)
            {
                if (k >= 0)
                {
                    d.Fleets.RemoveAt(k);
                }
                return;
            }
            if (k >= 0)
            {
                return;
            }
            // Leader = 0：还没做过编队判定（第一次判定不发“打散”提示，落单生成的不算被打散）
            var r = new CombatFleet { Unit = d.Id[slot], Shield = p.FlShieldHp, Intact = 1, Leader = 0, Gate = p.FlGateFamily > 0f ? p.FlGateHp : 0f };
            if (p.FlPodWarn > 0f)
            {
                float warn = math.max(0.8f, p.FlPodWarn);
                r.State = CombatFleetConst.StatePodFalling;
                r.Until = d.Scalars[0].Time + warn;
                d.Set(slot, CombatUnitFlags.Invulnerable, true);
                d.Set(slot, CombatUnitFlags.Targetable, false);
                Cue(ref d, CombatEventKind.FleetCue, slot, 0, warn, d.Pos[slot], CombatFleetConst.CuePodWarn);
            }
            d.Fleets.Add(r);
        }

        /// <summary>编队被打散 → 出手间隔 ÷ 效率。</summary>
        internal static float FlCooldownFactor(ref CombatData d, int i)
        {
            if (d.Fleets.Length == 0)
            {
                return 1f;
            }
            int k = FlIndexOf(ref d, d.Id[i]);
            if (k < 0 || d.Fleets[k].Intact != 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p) || !(p.FlFormRange > 0f) || !(p.FlFormBroken > 0f))
            {
                return 1f;
            }
            return 1f / math.clamp(p.FlFormBroken, 0.05f, 1f);
        }

        /// <summary>编队被打散 → 速度 × 效率；登陆舱下落中不动。</summary>
        internal static float FlSpeedFactor(ref CombatData d, int i)
        {
            if (d.Fleets.Length == 0)
            {
                return 1f;
            }
            int k = FlIndexOf(ref d, d.Id[i]);
            if (k < 0)
            {
                return 1f;
            }
            CombatFleet r = d.Fleets[k];
            if (r.State == CombatFleetConst.StatePodFalling)
            {
                return 0f;
            }
            if (r.Intact != 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p) || !(p.FlFormRange > 0f))
            {
                return 1f;
            }
            return math.clamp(p.FlFormBroken, 0.05f, 1f);
        }

        /// <summary>护卫护盾先吸收伤害（带电荷时打在护盾上 × 电磁倍率），返回打到耐久上的剩余伤害；受击推迟回充。</summary>
        internal static float FlAbsorb(ref CombatData d, int t, float damage)
        {
            if (d.Fleets.Length == 0 || !(damage > 0f))
            {
                return damage;
            }
            int k = FlIndexOf(ref d, d.Id[t]);
            if (k < 0 || !OcProfile(ref d, t, out CombatBehaviorProfile p))
            {
                return damage;
            }
            if (p.FlGateFamily > 0f)
            {
                // FG13-END-05（FGR-EMG-161）：护盾节点对其余端口没有读取——任何伤害都不扣耐久；只有引信族认“grm.trig 放出的子载荷（1 代及以上）命中”，按这一下的伤害削减闸门
                if ((byte)p.FlGateFamily == CombatFleetConst.GateFuse && FlTriggeredHit(ref d))
                {
                    FlGateCut(ref d, k, damage * Rate(d.Config.FlGateHitScale, CombatFleetConst.DefaultGateHitScale), d.EmgCtx.OwnerId);
                }
                return 0f;
            }
            if (!(p.FlShieldHp > 0f))
            {
                return damage;
            }
            CombatFleet r = d.Fleets[k];
            r.ShieldAt = d.Scalars[0].Time + p.FlShieldDelay;
            if (r.Shield > 0f)
            {
                float mult = FlCharged(ref d, t) ? math.max(1f, p.FlShieldElec) : 1f;
                float eff = damage * mult;
                if (eff < r.Shield)
                {
                    r.Shield -= eff;
                    damage = 0f;
                }
                else
                {
                    damage = (eff - r.Shield) / mult;
                    r.Shield = 0f;
                    Cue(ref d, CombatEventKind.FleetCue, t, 0, 0f, d.Pos[t], CombatFleetConst.CueShieldBreak);
                }
            }
            d.Fleets[k] = r;
            return damage;
        }

        // ─────────────────────────────── FG13-END-05 端口闸门（FG19 FGR-EMG-161）───────────────────────────────

        private static float Rate(float v, float fallback) => v > 0f ? v : fallback;

        internal static bool FlGateOf(ref CombatData d, int i, out byte family, out float max)
        {
            family = CombatFleetConst.GateNone;
            max = 0f;
            if (!OcProfile(ref d, i, out CombatBehaviorProfile p) || !(p.FlGateFamily > 0f))
            {
                return false;
            }
            family = (byte)p.FlGateFamily;
            max = p.FlGateHp;
            return true;
        }

        /// <summary>当前结算的伤害出自触发器放出的子载荷：载荷上下文有程序、它所在的程序节点有父节点（由 grm.trig 展开），代数 ≥ 1。</summary>
        private static bool FlTriggeredHit(ref CombatData d)
        {
            EmgCtx c = d.EmgCtx;
            if (c.Prog < 0 || c.Prog >= d.EmgPrograms.Length || c.Depth < 1)
            {
                return false;
            }
            EmgProgram pg = d.EmgPrograms[c.Prog];
            int n = pg.FirstNode + c.Node;
            return c.Node < pg.NodeCount && n >= 0 && n < d.EmgNodes.Length && d.EmgNodes[n].Parent >= 0;
        }

        private static void FlGateCut(ref CombatData d, int k, float amount, int by)
        {
            CombatFleet r = d.Fleets[k];
            if (!(amount > 0f) || !(r.Gate > 0f))
            {
                return;
            }
            float before = r.Gate;
            r.Gate = math.max(0f, r.Gate - amount);
            if (by > 0)
            {
                r.GateBy = by;
            }
            d.Fleets[k] = r;
            int i = d.SlotOf(r.Unit);
            // 提示只在跨过每 10% 时发一次（场 / 物质 / 过热是每步持续削减，不逐步刷提示）
            if (i >= 0 && r.Gate > 0f && FlGateOf(ref d, i, out _, out float max) && max > 0f && (int)(before * 10f / max) != (int)(r.Gate * 10f / max))
            {
                Cue(ref d, CombatEventKind.FleetCue, i, 0, r.Gate / max, d.Pos[i], CombatFleetConst.CueGateHit);
            }
        }

        /// <summary>
        /// 端口作用在单位 <paramref name="u"/> 身上：它是这一族的护盾节点时削减闸门（到 0 由白潮每步当场击毁）。<paramref name="family"/> 由调用点按端口定：
        /// 电荷 / 磁化累积量与敌对场覆盖 = 电磁，站在物质里 = 流体，积热累积量与过热相态 = 限制器。没有白潮记录时一条比较返回。
        /// </summary>
        internal static void FlGateTouch(ref CombatData d, int u, byte family, float amount, int sourceId)
        {
            if (d.Fleets.Length == 0 || !(amount > 0f) || u < 0 || u >= d.Count || !OcProfile(ref d, u, out CombatBehaviorProfile p) || (byte)p.FlGateFamily != family)
            {
                return;
            }
            int k = FlIndexOf(ref d, d.Id[u]);
            if (k >= 0)
            {
                FlGateCut(ref d, k, amount, sourceId);
            }
        }

        /// <summary>累积量增加的削减口径：电荷 / 磁化 → 电磁，积热 → 限制器。</summary>
        internal static void FlGateAcc(ref CombatData d, int u, int k, float gain, int sourceId)
        {
            if (d.Fleets.Length == 0)
            {
                return;
            }
            byte fam = k == EmgPhaseConst.Charge || k == EmgPhaseConst.Magnetize ? CombatFleetConst.GateEm : k == EmgPhaseConst.Overheat ? CombatFleetConst.GateLimiter : CombatFleetConst.GateNone;
            if (fam != CombatFleetConst.GateNone)
            {
                FlGateTouch(ref d, u, fam, gain * Rate(d.Config.FlGateAccScale, CombatFleetConst.DefaultGateAccScale), sourceId);
            }
        }

        internal static float FlGateFieldRate(ref CombatData d) => Rate(d.Config.FlGateFieldRate, CombatFleetConst.DefaultGateFieldRate);

        internal static float FlGateMatterRate(ref CombatData d) => Rate(d.Config.FlGateMatterRate, CombatFleetConst.DefaultGateMatterRate);

        private static bool FlCharged(ref CombatData d, int i) => i >= 0 && i < d.EmgUnit.Length && d.EmgUnit[i].AccA.z > 1e-4f;

        /// <summary>灭活光束预瞄线：第一次瞄上这个目标（或换了目标）只拉预瞄线（≥ 0.8 秒），到点之后的那一次出手才放行。返回 true = 这次不出手。</summary>
        internal static bool FlHoldBeam(ref CombatData d, int i, int t)
        {
            int k = FlIndexOf(ref d, d.Id[i]);
            if (k < 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p) || !(p.FlBeamLength > 0f) || t < 0)
            {
                return false;
            }
            CombatFleet r = d.Fleets[k];
            double now = d.Scalars[0].Time;
            if (r.AimTarget == d.Id[t] && r.Until > 0.0 && now >= r.Until)
            {
                return false; // 预瞄到点：放光束（FlOnFired 清预瞄）
            }
            if (r.AimTarget != d.Id[t] || !(r.Until > 0.0))
            {
                float warn = math.max(0.8f, p.FlBeamWarn);
                r.AimTarget = d.Id[t];
                r.Until = now + warn;
                d.Fleets[k] = r;
                Cue(ref d, CombatEventKind.FleetCue, i, d.Id[t], warn, d.Pos[t], CombatFleetConst.CueBeamAim);
            }
            return true;
        }

        /// <summary>真的出手之后：带光束参数的单位从本体朝目标扫过 FlBeamLength 米，逐格（1 米一格）写灭活焦土，来源 = 本单位。</summary>
        internal static void FlOnFired(ref CombatData d, int i, int t)
        {
            if (d.Fleets.Length == 0 || t < 0)
            {
                return;
            }
            int k = FlIndexOf(ref d, d.Id[i]);
            if (k < 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p) || !(p.FlBeamLength > 0f))
            {
                return;
            }
            CombatFleet r = d.Fleets[k];
            r.AimTarget = 0;
            r.Until = 0.0;
            d.Fleets[k] = r;
            double2 from = d.Pos[i];
            double2 to = d.Pos[t] - from;
            double len = math.length(to);
            double2 dir = len > 1e-6 ? to / len : new double2(1, 0);
            int steps = (int)math.ceil(p.FlBeamLength);
            for (int s = 1; s <= steps; s++)
            {
                double2 at = from + dir * s;
                FlAddMark(ref d, CellOf(at), d.Id[i], p.FlBeamRecover, p.FlBeamStagger); // 格心在整数（与 GridMath.FromWorld 同一口径）
            }
            Cue(ref d, CombatEventKind.FleetCue, i, d.Id[t], p.FlBeamLength, d.Pos[t], CombatFleetConst.CueBeam);
        }

        /// <summary>写一格灭活焦土：同一格已有 → 换成这个来源、停止恢复；满了先挤掉正在恢复的最早一格（没有就挤掉最早一格）。</summary>
        internal static void FlAddMark(ref CombatData d, int2 cell, int source, float recover, float stagger)
        {
            for (int m = 0; m < d.FleetMarks.Length; m++)
            {
                CombatFleetMark x = d.FleetMarks[m];
                if (x.Cell.x == cell.x && x.Cell.y == cell.y)
                {
                    x.Source = source;
                    x.Recover = recover;
                    x.Stagger = stagger;
                    x.RecoverAt = 0.0;
                    d.FleetMarks[m] = x;
                    return;
                }
            }
            if (d.FleetMarks.Length >= CombatFleetConst.MarkMax)
            {
                int drop = 0;
                for (int m = 0; m < d.FleetMarks.Length; m++)
                {
                    if (d.FleetMarks[m].RecoverAt > 0.0)
                    {
                        drop = m;
                        break;
                    }
                }
                d.FleetMarks.RemoveAt(drop);
            }
            d.FleetMarks.Add(new CombatFleetMark { Cell = cell, Source = source, Recover = math.max(0f, recover), Stagger = math.max(0f, stagger) });
        }

        /// <summary>排一次轨道打击（信标 / 热更层共用；返回打击 Id，0 = 排不下）。倒计时开始时发标记提示（地图 / 地面画圈与倒计时）。</summary>
        internal static int FlScheduleStrike(ref CombatData d, double2 center, float radius, float damage, float delay, float building, int beacon)
        {
            if (d.FleetStrikes.Length >= CombatFleetConst.StrikeMax || !(radius > 0f) || !(delay >= 0f))
            {
                return 0;
            }
            int id = NextSeq(ref d);
            d.FleetStrikes.Add(new CombatFleetStrike
            {
                Id = id, Beacon = beacon, Center = center, Radius = radius, Damage = math.max(0f, damage), Building = math.saturate(building), At = d.Scalars[0].Time + delay,
            });
            int slot = beacon > 0 ? d.SlotOf(beacon) : -1;
            Cue(ref d, CombatEventKind.FleetCue, slot, id, delay, center, CombatFleetConst.CueStrikeMark);
            return id;
        }

        /// <summary>信标挑打击区：打击范围内最近的己方建筑 / 炮塔，没有再取最近的己方机器；都没有 → 不排打击（返回 false，不往自己脚下打）。O(N)，只在信标没有在排打击时。</summary>
        private static bool FlPickStrikeCenter(ref CombatData d, int beacon, float range, out double2 center)
        {
            double2 at = d.Pos[beacon];
            double bestS = double.MaxValue, bestM = double.MaxValue;
            double2 s = at, m = at;
            double r2 = (double)range * range;
            for (int j = 0; j < d.Count; j++)
            {
                if (!d.IsAlive(j) || d.Faction[j] != (byte)CombatFaction.Player)
                {
                    continue;
                }
                double dd = math.distancesq(d.Pos[j], at);
                if (dd > r2)
                {
                    continue;
                }
                byte kind = d.Kind[j];
                if (kind == (byte)CombatUnitKind.Structure || kind == (byte)CombatUnitKind.Turret)
                {
                    if (dd < bestS)
                    {
                        bestS = dd;
                        s = d.Pos[j];
                    }
                }
                else if (kind == (byte)CombatUnitKind.Machine && dd < bestM)
                {
                    bestM = dd;
                    m = d.Pos[j];
                }
            }
            center = bestS < double.MaxValue ? s : m;
            return bestS < double.MaxValue || bestM < double.MaxValue;
        }

        private static bool FlHasStrikeFrom(ref CombatData d, int beacon)
        {
            for (int s = 0; s < d.FleetStrikes.Length; s++)
            {
                if (d.FleetStrikes[s].Beacon == beacon)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>打击落下：圈内（含单位半径）活着的机器与敌人各受一次伤害（evt.hurt 来源 = 冲击）；建筑由热更层按玩法事件结算。返回受击单位数。</summary>
        private static int FlStrikeUnits(ref CombatData d, double2 pos, float radius, float damage)
        {
            int hits = 0;
            if (!(damage > 0f))
            {
                return 0;
            }
            for (int i = 0; i < d.Count; i++)
            {
                if (!d.IsAlive(i))
                {
                    continue;
                }
                byte kind = d.Kind[i];
                if (kind != (byte)CombatUnitKind.Machine && kind != (byte)CombatUnitKind.Enemy)
                {
                    continue;
                }
                double reach = radius + d.Radius[i];
                if (math.distancesq(d.Pos[i], pos) > reach * reach)
                {
                    continue;
                }
                DamageFrom(ref d, i, damage, -1, EmgCode.SrcImpact);
                hits++;
            }
            return hits;
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        private static void StepFleet(ref CombatData d, float dt)
        {
            if (d.Fleets.Length == 0 && d.FleetStrikes.Length == 0 && d.FleetMarks.Length == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            // 单位：阵亡 / 移除的删记录；登陆舱落地；信标排打击；护盾回充；编队判定；FG13-END-05 端口闸门（过热相态持续削减限制器、削到 0 当场击毁）
            for (int k = d.Fleets.Length - 1; k >= 0; k--)
            {
                if (k >= d.Fleets.Length)
                {
                    continue;
                }
                CombatFleet r = d.Fleets[k];
                int i = d.SlotOf(r.Unit);
                if (i < 0 || !d.IsAlive(i) || !OcProfile(ref d, i, out CombatBehaviorProfile p))
                {
                    d.Fleets.RemoveAt(k);
                    continue;
                }
                if (r.State == CombatFleetConst.StatePodFalling)
                {
                    if (now >= r.Until)
                    {
                        r.State = CombatFleetConst.StateNormal;
                        d.Set(i, CombatUnitFlags.Invulnerable, false);
                        d.Set(i, CombatUnitFlags.Targetable, true);
                        Gameplay(ref d, CombatEventKind.FleetEvent, i, d.Siege[i].Group, 0f, 0f, d.Pos[i], CombatFleetConst.EvtPodLanded, 0);
                        Cue(ref d, CombatEventKind.FleetCue, i, 0, 0f, d.Pos[i], CombatFleetConst.CuePodLand);
                    }
                    d.Fleets[k] = r;
                    continue;
                }
                if (p.FlStrikeDelay > 0f && !FlHasStrikeFrom(ref d, r.Unit) && FlPickStrikeCenter(ref d, i, p.FlStrikeRange, out double2 c))
                {
                    FlScheduleStrike(ref d, c, p.FlStrikeRadius, p.FlStrikeDamage, math.max(0.8f, p.FlStrikeDelay), p.FlStrikeBuilding, r.Unit);
                }
                if (p.FlShieldHp > 0f && r.Shield < p.FlShieldHp && now >= r.ShieldAt && !FlCharged(ref d, i))
                {
                    r.Shield = math.min(p.FlShieldHp, r.Shield + p.FlShieldRegen * dt);
                }
                if (p.FlFormRange > 0f)
                {
                    FlFormation(ref d, i, ref r, p);
                }
                d.Fleets[k] = r;
                if (p.FlGateFamily > 0f)
                {
                    if ((byte)p.FlGateFamily == CombatFleetConst.GateLimiter && InPhase(ref d, i, EmgPhaseConst.Overheat))
                    {
                        FlGateCut(ref d, k, Rate(d.Config.FlGatePhaseRate, CombatFleetConst.DefaultGatePhaseRate) * dt, 0); // phs.overheat：处在过热相态
                    }
                    if (!(d.Fleets[k].Gate > 0f))
                    {
                        Cue(ref d, CombatEventKind.FleetCue, i, 0, 0f, d.Pos[i], CombatFleetConst.CueGateBreak);
                        Kill(ref d, i, d.Fleets[k].GateBy);
                        if (k < d.Fleets.Length && d.Fleets[k].Unit == r.Unit)
                        {
                            d.Fleets.RemoveAt(k);
                        }
                    }
                }
            }
            // 轨道打击：信标在倒计时内阵亡 → 取消；到点 → 打击
            for (int s = d.FleetStrikes.Length - 1; s >= 0; s--)
            {
                CombatFleetStrike x = d.FleetStrikes[s];
                if (x.Beacon != 0 && !d.IsAlive(d.SlotOf(x.Beacon)))
                {
                    d.FleetStrikes.RemoveAt(s);
                    Cue(ref d, CombatEventKind.FleetCue, -1, x.Id, 0f, x.Center, CombatFleetConst.CueStrikeCancel);
                    continue;
                }
                if (now < x.At)
                {
                    continue;
                }
                d.FleetStrikes.RemoveAt(s);
                int hits = FlStrikeUnits(ref d, x.Center, x.Radius, x.Damage);
                Gameplay(ref d, CombatEventKind.FleetEvent, -1, x.Id, x.Radius, x.Building, x.Center, CombatFleetConst.EvtStrike, 0);
                Cue(ref d, CombatEventKind.FleetCue, -1, x.Id, hits, x.Center, CombatFleetConst.CueStrike);
            }
            // 灭活焦土：来源不在了 → 按灼烧先后排恢复时刻；到点 → 恢复
            bool cued = false;
            for (int m = 0; m < d.FleetMarks.Length; m++)
            {
                CombatFleetMark x = d.FleetMarks[m];
                if (x.RecoverAt > 0.0 || d.IsAlive(d.SlotOf(x.Source)))
                {
                    continue;
                }
                int rank = 0;
                for (int j = 0; j < m; j++)
                {
                    if (d.FleetMarks[j].Source == x.Source)
                    {
                        rank++;
                    }
                }
                x.RecoverAt = now + x.Recover + rank * x.Stagger;
                d.FleetMarks[m] = x;
                if (!cued)
                {
                    cued = true;
                    Cue(ref d, CombatEventKind.FleetCue, -1, x.Source, x.Recover, new double2(x.Cell.x, x.Cell.y), CombatFleetConst.CueInertRecover);
                }
            }
            for (int m = d.FleetMarks.Length - 1; m >= 0; m--)
            {
                CombatFleetMark x = d.FleetMarks[m];
                if (x.RecoverAt > 0.0 && now >= x.RecoverAt)
                {
                    d.FleetMarks.RemoveAt(m);
                }
            }
        }

        /// <summary>空中单位（每步自己重选目标）的编队跟随：完整编队、自己不是长机、长机记着的目标还能打 → 换成长机的目标；否则保持 <paramref name="t"/>。O(白潮表长)。</summary>
        internal static int FlFollowTarget(ref CombatData d, int i, int t)
        {
            int k = FlIndexOf(ref d, d.Id[i]);
            if (k < 0)
            {
                return t;
            }
            CombatFleet r = d.Fleets[k];
            if (r.Intact == 0 || r.Leader == r.Unit)
            {
                return t;
            }
            int ls = d.SlotOf(r.Leader);
            int lt = ls >= 0 && d.IsAlive(ls) ? d.SlotOf(d.Cmd[ls].Target) : -1;
            return lt >= 0 && d.IsAlive(lt) && d.Has(lt, CombatUnitFlags.Targetable) && d.Faction[lt] != d.Faction[i] ? lt : t;
        }

        /// <summary>编队：编队半径内同阵营、带编队参数的活单位（含自己）≥ 下限 = 完整；长机 = 其中单位号最小的那台。完整编队里的突袭者跟随长机的目标（整体转向）。</summary>
        private static void FlFormation(ref CombatData d, int i, ref CombatFleet r, in CombatBehaviorProfile p)
        {
            double r2 = (double)p.FlFormRange * p.FlFormRange;
            int count = 1;
            int leader = r.Unit;
            for (int j = 0; j < d.Fleets.Length; j++)
            {
                int u = d.Fleets[j].Unit;
                if (u == r.Unit)
                {
                    continue;
                }
                int s = d.SlotOf(u);
                if (s < 0 || !d.IsAlive(s) || d.Faction[s] != d.Faction[i] || !OcProfile(ref d, s, out CombatBehaviorProfile q) || !(q.FlFormRange > 0f)
                    || math.distancesq(d.Pos[s], d.Pos[i]) > r2)
                {
                    continue;
                }
                count++;
                leader = math.min(leader, u);
            }
            byte intact = (byte)(count >= math.max(2f, p.FlFormMin) ? 1 : 0);
            if (r.Intact != 0 && intact == 0 && r.Leader != 0)
            {
                Cue(ref d, CombatEventKind.FleetCue, i, 0, count, d.Pos[i], CombatFleetConst.CueFormationBroken);
            }
            r.Intact = intact;
            r.Leader = intact != 0 ? leader : r.Unit;
            if (intact == 0 || leader == r.Unit || d.Behavior[i] != (byte)CombatBehavior.Raider || CombatSiegeLogic.UsesSiege(ref d, i))
            {
                return;
            }
            int ls = d.SlotOf(leader);
            if (ls < 0 || d.Behavior[ls] != (byte)CombatBehavior.Raider)
            {
                return;
            }
            int lt = d.SlotOf(d.Cmd[ls].Target);
            if (lt < 0 || !d.IsAlive(lt) || !d.Has(lt, CombatUnitFlags.Targetable))
            {
                return;
            }
            CombatCommand cmd = d.Cmd[i];
            if (cmd.Target != d.Cmd[ls].Target)
            {
                cmd.Target = d.Cmd[ls].Target;
                d.Cmd[i] = cmd;
                d.Secondary[i] = math.max(d.Secondary[i], 0.5f); // 跟随长机后半秒内不自己重选（与突袭者重选间隔同一口径）
            }
        }
    }
}
