using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>一个固定步（Burst，单线程：规范顺序 + 确定性，与线程数 / 调度时机无关）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatStepJob : IJob
    {
        public CombatData D;
        public float Dt;
        public double Time;

        public void Execute()
        {
            CombatLogic.Step(ref D, Dt, Time);
        }
    }

    /// <summary>
    /// 战斗内核的全部规则。Burst 作业（<see cref="CombatStepJob"/>）与托管的即时调用（直控点击开火、编队命令下达）共用这一份代码。
    ///
    /// 一个固定步的顺序（与 Demo 各区域 SimStep 的调用顺序一致）：
    /// 1. 墓碑过多时压实（保持相对顺序）；记下上一步位置（渲染插值）；建空间网格。
    /// 2. 己方单位（槽位升序）：直控移动 / 工作赶路 / 编队命令（移动、攻击、守备、撤退）/ 训练靶自动交战 / 炮塔驻守开火。
    /// 3. 散热（重炮热量）。
    /// 4. 敌方单位：先优先级 0（主核心）再优先级 1，各自槽位升序：驻守开火、瞄准线、侦察、干扰、维修、突袭者。
    /// 5. 重建网格；弹体飞行与碰撞（弹体下标升序，命中取最早的交点、同时取槽位小者）。
    /// 6. 兴趣点发现；计数。
    /// Demo 的即时命中按执行顺序立刻生效（先打死的目标，同一步后面的单位就看不到它），与 Demo 逐调用结算一致。
    /// </summary>
    public static class CombatLogic
    {
        /// <summary>网格查询的额外余量（米）：覆盖步内移动造成的“格子过期”。</summary>
        private const double GridSlack = 2.0;

        // ─────────────────────────────── 步 ───────────────────────────────

        public static void Step(ref CombatData d, float dt, double time)
        {
            CombatScalars s = d.Scalars[0];
            s.Time = time;
            s.Revision++;
            d.Scalars[0] = s;

            MaybeCompact(ref d);

            int n = d.Count;
            for (int i = 0; i < n; i++)
            {
                d.Prev[i] = d.Pos[i];
            }

            var grid = new CombatGrid(d.Config.GridCell);
            grid.Build(ref d);

            // 2. 己方
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i) || d.Faction[i] != (byte)CombatFaction.Player)
                {
                    continue;
                }
                StepPlayerUnit(ref d, ref grid, i, dt);
            }

            // 3. 散热
            for (int i = 0; i < n; i++)
            {
                if (d.Heat[i] > 0f && d.IsAlive(i))
                {
                    Dissipate(ref d, i, dt);
                }
            }

            // 4. 敌方（优先级 0 先于 1）
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!d.IsAlive(i) || d.Faction[i] != (byte)CombatFaction.Hostile || d.Priority[i] != pass)
                    {
                        continue;
                    }
                    StepHostileUnit(ref d, ref grid, i, dt);
                }
            }
            grid.Dispose();

            // 5. 弹体
            if (d.Projectiles.Length > 0)
            {
                var grid2 = new CombatGrid(d.Config.GridCell);
                grid2.Build(ref d);
                StepProjectiles(ref d, ref grid2, dt);
                grid2.Dispose();
            }

            // 6. 兴趣点
            StepPois(ref d);

            s = d.Scalars[0];
            s.Steps++;
            d.Scalars[0] = s;
            CombatCounters c = d.Counters[0];
            c.Steps++;
            d.Counters[0] = c;
        }

        // ─────────────────────────────── 己方 ───────────────────────────────

        private static void StepPlayerUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            if (d.Has(i, CombatUnitFlags.Possessed))
            {
                // Demo：受控机排除在编队接管之外（命令保留，释放后恢复）；位移来自直控输入。
                float2 dir = d.Direct[i];
                if (math.lengthsq(dir) > 0.0001f)
                {
                    double2 step = (double2)(math.normalize(dir) * (d.Speed[i] * dt));
                    double2 p = d.Pos[i] + step;
                    CombatScalars s = d.Scalars[0];
                    p.y = math.clamp(p.y, s.DirectMinY, s.DirectMaxY);
                    d.Pos[i] = p;
                }
                return;
            }

            CombatCommand cmd = d.Cmd[i];
            if (cmd.Kind != CombatCommandKind.None)
            {
                d.Set(i, CombatUnitFlags.PendingCommand, false);
                bool done = TickCommand(ref d, i, ref cmd, dt, out CombatEndReason reason);
                if (done)
                {
                    int target = cmd.Target;
                    CombatCommandKind kind = cmd.Kind;
                    cmd = default;
                    d.Cmd[i] = cmd;
                    if (kind == CombatCommandKind.WorkMove)
                    {
                        Gameplay(ref d, CombatEventKind.WorkArrived, i, 0, 0f, 0f, d.Pos[i], 0, 0);
                    }
                    else
                    {
                        Gameplay(ref d, CombatEventKind.CommandEnded, i, target, 0f, 0f, d.Pos[i], (byte)reason, (byte)kind);
                    }
                }
                else
                {
                    d.Cmd[i] = cmd;
                }
            }

            CombatBehavior b = (CombatBehavior)d.Behavior[i];
            if (b == CombatBehavior.AutoEngage)
            {
                StepAutoEngage(ref d, i, dt);
            }
            else if (b == CombatBehavior.HoldFire)
            {
                StepHoldFire(ref d, ref grid, i, dt);
            }
        }

        /// <summary>返回 true = 命令结束（成功或失败都算），<paramref name="reason"/> 为原因。</summary>
        private static bool TickCommand(ref CombatData d, int i, ref CombatCommand cmd, float dt, out CombatEndReason reason)
        {
            reason = CombatEndReason.None;
            double2 pos = d.Pos[i];

            if (cmd.Kind == CombatCommandKind.WorkMove)
            {
                // Demo HomeValleyMachineMarker.Tick：先判到达（进入到达半径即对齐到目标点），否则直线走一步（不越过目标）。
                double2 to = cmd.Pos - pos;
                if (math.lengthsq(to) <= (double)cmd.Arrive * cmd.Arrive)
                {
                    d.Pos[i] = cmd.Pos;
                    reason = CombatEndReason.Arrived;
                    return true;
                }
                double2 stepW = math.normalize(to) * (d.Speed[i] * dt);
                if (math.lengthsq(stepW) > math.lengthsq(to))
                {
                    stepW = to;
                }
                d.Pos[i] = pos + stepW;
                return false;
            }

            if (cmd.Kind == CombatCommandKind.Attack)
            {
                int t = d.SlotOf(cmd.Target);
                if (t < 0 || !d.IsAlive(t) || !d.Has(t, CombatUnitFlags.Targetable))
                {
                    reason = CombatEndReason.TargetLost;
                    return true;
                }
                cmd.Pos = d.Pos[t];
                double distA = math.distance(pos, cmd.Pos);
                bool inRangeA = distA <= cmd.AttackRange;
                if (!inRangeA)
                {
                    StepTowards(ref d, i, pos, cmd.Pos, dt);
                }
                cmd.AtkCd -= dt;
                if (inRangeA && cmd.AtkCd <= 0f)
                {
                    cmd.AtkCd = cmd.AttackCooldown;
                    CombatFireResult r = FireAt(ref d, i, t);
                    bool destroyed = r == CombatFireResult.Ok && !d.IsAlive(t);
                    Gameplay(ref d, CombatEventKind.AttackOutcome, i, cmd.Target, 0f, 0f, d.Pos[t], (byte)r, (byte)(destroyed ? 1 : 0));
                    if (destroyed)
                    {
                        reason = CombatEndReason.TargetDestroyed;
                        return true;
                    }
                }
                return false;
            }

            double dist = math.distance(pos, cmd.Pos);
            if (dist <= cmd.Arrive)
            {
                if (cmd.Kind == CombatCommandKind.Guard)
                {
                    return false; // 持久命令：到位后一直守到取消。
                }
                reason = CombatEndReason.Arrived;
                return true;
            }
            bool blocked = StepTowards(ref d, i, pos, cmd.Pos, dt);
            // 路径受阻判定（Demo TrackProgressAndMaybeGiveUp）：用推进前的坐标每隔 ProgressInterval 秒核对一次。
            cmd.ProgTimer -= dt;
            if (cmd.ProgTimer > 0f)
            {
                return false;
            }
            cmd.ProgTimer = d.Config.ProgressInterval;
            float distNow = (float)math.distance(pos, cmd.Pos);
            bool progressed = cmd.HasLast == 0 || (cmd.LastDist - distNow) >= d.Config.MinProgress;
            cmd.LastDist = distNow;
            cmd.HasLast = 1;
            _ = blocked;
            if (progressed)
            {
                cmd.Stuck = 0;
                return false;
            }
            cmd.Stuck++;
            if (cmd.Stuck < d.Config.MaxStuckStrikes)
            {
                Gameplay(ref d, CombatEventKind.CommandStuckStrike, i, 0, cmd.Stuck, d.Config.MaxStuckStrikes, pos, 0, (byte)cmd.Kind);
                return false;
            }
            reason = CombatEndReason.Stuck;
            return true;
        }

        /// <summary>朝目标推进一步，带最小局部避障（Demo RegionSquadCommandSystem.StepTowards 逐行对应）。返回这一步是否在绕障。</summary>
        private static bool StepTowards(ref CombatData d, int i, double2 pos, double2 target, float dt)
        {
            double2 toTarget = target - pos;
            if (math.lengthsq(toTarget) < 0.0001)
            {
                return false;
            }
            float2 desired = (float2)math.normalize(toTarget);
            bool blocked = FindBlockingObstacle(ref d, pos, desired, out double2 obstacle);
            float2 moveDir = desired;
            if (blocked)
            {
                float2 toObstacle = (float2)(obstacle - pos);
                float2 perp = new float2(-desired.y, desired.x);
                float side = math.dot(perp, toObstacle) >= 0f ? -1f : 1f;
                moveDir = math.normalize(desired + perp * (side * d.Config.AvoidWeight));
            }
            double2 step = (double2)(moveDir * (d.Speed[i] * dt));
            if (math.lengthsq(step) > math.lengthsq(toTarget))
            {
                step = toTarget;
            }
            d.Pos[i] = pos + step;
            return blocked;
        }

        private static bool FindBlockingObstacle(ref CombatData d, double2 pos, float2 dir, out double2 obstaclePos)
        {
            obstaclePos = default;
            float best = float.MaxValue;
            bool found = false;
            float look = d.Config.Lookahead;
            float me = d.Config.AvoidRadius;
            for (int k = 0; k < d.Obstacles.Length; k++)
            {
                float3 o = d.Obstacles[k];
                float2 toO = (float2)(new double2(o.x, o.y) - pos);
                float along = math.dot(toO, dir);
                if (along <= 0f || along > look)
                {
                    continue;
                }
                float2 closest = dir * along;
                float perpDist = math.distance(closest, toO);
                if (perpDist > o.z + me)
                {
                    continue;
                }
                if (along < best)
                {
                    best = along;
                    obstaclePos = new double2(o.x, o.y);
                    found = true;
                }
            }
            return found;
        }

        private static void StepAutoEngage(ref CombatData d, int i, float dt)
        {
            CombatScalars s = d.Scalars[0];
            if (s.EngageTarget <= 0)
            {
                return;
            }
            float remaining = d.Cycle[i] - dt;
            if (remaining > 0f)
            {
                d.Cycle[i] = remaining;
                return;
            }
            d.Cycle[i] = remaining;
            if (d.Has(i, CombatUnitFlags.EngageHold))
            {
                return; // 厂内机器：不参与自动交战、不消耗冷却（Demo TickAutoEngage 的 IsInFactory 跳过）。驶出工厂后冷却已就绪。
            }
            int t = d.SlotOf(s.EngageTarget);
            if (t < 0 || !d.IsAlive(t) || d.Hp[t] <= 0f || math.distance(d.Pos[i], d.Pos[t]) > s.EngageRange)
            {
                return; // 不在射程内不算尝试，不消耗冷却（Demo TickAutoEngage）。
            }
            Gameplay(ref d, CombatEventKind.EngageRequest, i, d.Id[t], 0f, 0f, d.Pos[t], 0, 0);
            d.Cycle[i] = s.EngageInterval;
        }

        private static void Dissipate(ref CombatData d, int i, float dt)
        {
            int w = d.Weapon[i];
            if (w < 0 || w >= d.Weapons.Length)
            {
                d.Heat[i] = 0f;
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            float rate = wp.Dissipation + (d.Has(i, CombatUnitFlags.HeatSink) ? wp.HeatSinkBonus : 0f);
            float h = math.max(0f, d.Heat[i] - rate * dt);
            d.Heat[i] = h;
            if (d.Has(i, CombatUnitFlags.Overheated) && h < wp.RecoverBelow)
            {
                d.Set(i, CombatUnitFlags.Overheated, false);
            }
        }

        // ─────────────────────────────── 敌方 ───────────────────────────────

        private static void StepHostileUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            switch ((CombatBehavior)d.Behavior[i])
            {
                case CombatBehavior.HoldFire:
                    StepHoldFire(ref d, ref grid, i, dt);
                    break;
                case CombatBehavior.Telegraph:
                    StepTelegraph(ref d, ref grid, i, dt);
                    break;
                case CombatBehavior.Scout:
                    StepScout(ref d, ref grid, i, dt);
                    break;
                case CombatBehavior.Jammer:
                    StepJammer(ref d, i);
                    StepHoldFire(ref d, ref grid, i, dt);
                    break;
                case CombatBehavior.Repair:
                    StepRepair(ref d, ref grid, i, dt);
                    break;
                case CombatBehavior.Raider:
                    StepRaider(ref d, ref grid, i, dt);
                    break;
            }
        }

        /// <summary>驻守开火（Demo 护甲机 / 干扰机自卫 / 主核心；炮塔同一规则）：冷却到点才找目标；没有目标不重置冷却。</summary>
        private static void StepHoldFire(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int w = d.Weapon[i];
            if (w < 0 || !d.Has(i, CombatUnitFlags.WeaponEnabled))
            {
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            if (c > 0f)
            {
                return;
            }
            int t = FindTarget(ref d, ref grid, i, wp.Range, d.Has(i, CombatUnitFlags.NeedsLos), wp.TargetMode);
            if (t < 0)
            {
                return;
            }
            d.Cycle[i] = wp.Cooldown;
            UnitAttack(ref d, i, t, wp);
        }

        /// <summary>瞄准线两段式（Demo 步进炮）。</summary>
        private static void StepTelegraph(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int w = d.Weapon[i];
            if (w < 0 || !d.Has(i, CombatUnitFlags.WeaponEnabled))
            {
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            bool los = d.Has(i, CombatUnitFlags.NeedsLos);
            if (d.Secondary[i] > 0f)
            {
                float sec = d.Secondary[i] - dt;
                d.Secondary[i] = sec;
                if (sec > 0f)
                {
                    return;
                }
                int locked = FindTarget(ref d, ref grid, i, wp.Range, los, wp.TargetMode);
                d.Cycle[i] = wp.Cooldown;
                if (locked >= 0)
                {
                    Cue(ref d, CombatEventKind.Telegraph, i, d.Id[locked], 0f, d.Pos[i], 2);
                    UnitAttack(ref d, i, locked, wp);
                }
                else
                {
                    Cue(ref d, CombatEventKind.Telegraph, i, 0, 0f, d.Pos[i], 1);
                }
                return;
            }
            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            if (c > 0f)
            {
                return;
            }
            int t = FindTarget(ref d, ref grid, i, wp.Range, los, wp.TargetMode);
            if (t < 0)
            {
                return;
            }
            d.Secondary[i] = wp.AimSeconds;
            Cue(ref d, CombatEventKind.Telegraph, i, d.Id[t], 0f, d.Pos[i], 0);
        }

        /// <summary>侦察（Demo 静默侦察机）：受威胁后撤 / 出生点附近摆动巡逻；按周期标记视线内最近的机器。</summary>
        private static void StepScout(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            double2 pos = d.Pos[i];
            int nearest = FindTarget(ref d, ref grid, i, p.SenseRange, true, CombatTargetMode.Nearest);
            bool threat = nearest >= 0 && math.distance(pos, d.Pos[nearest]) <= p.FleeTrigger;
            double2 spawn = d.Home[i];
            if (threat)
            {
                double2 away = pos - d.Pos[nearest];
                if (math.lengthsq(away) > 0.0001)
                {
                    double2 proposed = pos + math.normalize(away) * (p.Speed * dt);
                    if (math.distance(proposed, spawn) <= p.Leash)
                    {
                        d.Pos[i] = proposed;
                    }
                }
            }
            else
            {
                CombatScalars s = d.Scalars[0];
                float phase = math.sin((float)s.Time * p.PatrolFreq);
                double2 patrol = spawn + new double2(phase * p.PatrolRadius, 0);
                double2 to = patrol - pos;
                if (math.lengthsq(to) > 0.01)
                {
                    double len = math.length(to);
                    d.Pos[i] = pos + to / len * math.min(p.Speed * dt, len);
                }
            }

            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            if (c > 0f)
            {
                return;
            }
            d.Cycle[i] = p.CycleSeconds;
            if (nearest >= 0)
            {
                CombatScalars s2 = d.Scalars[0];
                d.MarkedUntil[nearest] = s2.Time + p.EffectSeconds;
                Gameplay(ref d, CombatEventKind.MachineMarked, nearest, d.Id[i], p.EffectSeconds, 0f, d.Pos[nearest], 0, 0);
            }
            else
            {
                Gameplay(ref d, CombatEventKind.MarkMissed, i, 0, 0f, 0f, d.Pos[i], 0, 0);
            }
        }

        /// <summary>干扰（Demo 静默干扰机）：清掉半径内己方机器与友军身上的标记（不看视线）。开火另走驻守开火。</summary>
        private static void StepJammer(ref CombatData d, int i)
        {
            int bp = d.BProfile[i];
            if (bp < 0)
            {
                return;
            }
            float r = d.Profiles[bp].EffectRange;
            double2 pos = d.Pos[i];
            double now = d.Scalars[0].Time;
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == i || !d.IsAlive(k) || d.MarkedUntil[k] <= 0)
                {
                    continue;
                }
                if (math.distance(pos, d.Pos[k]) > r)
                {
                    continue;
                }
                bool wasActive = d.MarkedUntil[k] > now;
                d.MarkedUntil[k] = 0;
                if (d.Faction[k] == (byte)CombatFaction.Player && wasActive)
                {
                    Gameplay(ref d, CombatEventKind.MarkCleared, k, d.Id[i], 0f, 0f, d.Pos[k], 0, 0);
                }
            }
        }

        /// <summary>维修（Demo 铸造维修机）。</summary>
        private static void StepRepair(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int bp = d.BProfile[i];
            if (bp < 0)
            {
                return;
            }
            CombatBehaviorProfile p = d.Profiles[bp];
            double2 pos = d.Pos[i];
            // 威胁：最近的敌对单位（不看视线），进入触发距离就后撤，本步不救援。
            int threat = FindTarget(ref d, ref grid, i, p.FleeTrigger, false, CombatTargetMode.Nearest);
            if (threat >= 0)
            {
                double2 away = pos - d.Pos[threat];
                if (math.lengthsq(away) > 0.0001)
                {
                    double2 proposed = pos + math.normalize(away) * (p.Speed * dt);
                    if (math.distance(proposed, d.Home[i]) <= p.Leash)
                    {
                        d.Pos[i] = proposed;
                    }
                }
                return;
            }
            // 救援目标：同阵营（含自己）血量百分比最低的存活单位；并列取先出现的（Demo 严格小于）。
            int lowest = -1;
            float lowestPct = float.MaxValue;
            int n = d.Count;
            byte myFaction = d.Faction[i];
            for (int k = 0; k < n; k++)
            {
                if (!d.IsAlive(k) || d.Faction[k] != myFaction || d.MaxHp[k] <= 0f)
                {
                    continue;
                }
                float pct = d.Hp[k] / d.MaxHp[k];
                if (pct < lowestPct)
                {
                    lowestPct = pct;
                    lowest = k;
                }
            }
            if (lowest < 0 || lowestPct >= 1f)
            {
                return;
            }
            double dist = math.distance(pos, d.Pos[lowest]);
            if (dist > p.EffectRange)
            {
                if (lowest != i)
                {
                    double2 to = d.Pos[lowest] - pos;
                    if (math.lengthsq(to) > 0.0001)
                    {
                        double len = math.length(to);
                        d.Pos[i] = pos + to / len * math.min(p.Speed * dt, len);
                    }
                }
                return;
            }
            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            if (c > 0f)
            {
                return;
            }
            d.Cycle[i] = p.CycleSeconds;
            Heal(ref d, lowest, p.EffectAmount, i);
        }

        /// <summary>突袭者（FG06 原型）：扑向感知范围内的目标，进入射程开火；没有目标时朝目标点前进。每 0.5 游戏秒重选一次目标。</summary>
        private static void StepRaider(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int w = d.Weapon[i];
            int bp = d.BProfile[i];
            if (w < 0 || bp < 0)
            {
                return;
            }
            CombatWeapon wp = d.Weapons[w];
            CombatBehaviorProfile p = d.Profiles[bp];
            CombatCommand cmd = d.Cmd[i];
            int t = d.SlotOf(cmd.Target);
            float retarget = d.Secondary[i] - dt;
            if (t < 0 || !d.IsAlive(t) || !d.Has(t, CombatUnitFlags.Targetable) || retarget <= 0f)
            {
                t = FindTarget(ref d, ref grid, i, p.SenseRange, false, wp.TargetMode);
                cmd.Target = t >= 0 ? d.Id[t] : 0;
                retarget = 0.5f;
            }
            d.Secondary[i] = retarget;
            d.Cmd[i] = cmd;
            double2 pos = d.Pos[i];
            float c = d.Cycle[i] - dt;
            d.Cycle[i] = c;
            if (t < 0)
            {
                MoveToward(ref d, i, pos, d.Home[i], p.Speed * dt, 0.5);
                return;
            }
            double dist = math.distance(pos, d.Pos[t]);
            if (dist > wp.Range)
            {
                MoveToward(ref d, i, pos, d.Pos[t], p.Speed * dt, wp.Range * 0.9);
                return;
            }
            if (c > 0f)
            {
                return;
            }
            d.Cycle[i] = wp.Cooldown;
            UnitAttack(ref d, i, t, wp);
        }

        private static void MoveToward(ref CombatData d, int i, double2 pos, double2 target, double stepLen, double stopAt)
        {
            double2 to = target - pos;
            double len = math.length(to);
            if (len <= stopAt || len < 1e-6)
            {
                return;
            }
            d.Pos[i] = pos + to / len * math.min(stepLen, len - stopAt);
        }

        /// <summary>单位主动攻击（驻守开火 / 瞄准线 / 突袭者 / 炮塔）：弹体武器生成弹体，其余即时命中。</summary>
        private static void UnitAttack(ref CombatData d, int i, int t, in CombatWeapon wp)
        {
            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;
            if (wp.Mode == CombatWeaponMode.Projectile)
            {
                SpawnProjectile(ref d, i, t, d.Weapon[i], wp);
                return;
            }
            if (!d.Has(i, CombatUnitFlags.SilentFire))
            {
                Cue(ref d, d.Faction[i] == (byte)CombatFaction.Hostile ? CombatEventKind.EnemyFired : CombatEventKind.Fired, i, d.Id[t], 0f, d.Pos[i], 0);
            }
            DamageUnit(ref d, t, wp.Damage, i);
        }

        private static void SpawnProjectile(ref CombatData d, int i, int t, int weapon, in CombatWeapon wp)
        {
            if (d.Projectiles.Length >= d.Config.ProjectileCapacity)
            {
                CombatCounters cr = d.Counters[0];
                cr.ProjectilesRefused++;
                d.Counters[0] = cr;
                return;
            }
            double2 from = d.Pos[i];
            double2 to = d.Pos[t] - from;
            float2 dir = math.lengthsq(to) > 1e-12 ? (float2)math.normalize(to) : new float2(0f, 1f);
            // 起点放在开火者半径外，免得刚出膛就擦到自己一侧的单位。
            double2 start = from + (double2)(dir * (d.Radius[i] + wp.ProjectileRadius));
            d.Projectiles.Add(new CombatProjectile
            {
                Pos = start,
                Prev = start,
                Vel = dir * wp.ProjectileSpeed,
                Owner = d.Id[i],
                Weapon = weapon,
                Damage = wp.Damage,
                Radius = wp.ProjectileRadius,
                Life = wp.ProjectileLife > 0f ? wp.ProjectileLife : (wp.Range * 1.25f) / math.max(0.01f, wp.ProjectileSpeed),
                Faction = (CombatFaction)d.Faction[i],
            });
            CombatCounters c = d.Counters[0];
            c.ProjectilesSpawned++;
            d.Counters[0] = c;
        }

        // ─────────────────────────────── 开火与结算 ───────────────────────────────

        /// <summary>
        /// 己方一次开火尝试（编队攻击命令与直控点击的唯一结算入口；Demo FracturedCityRegion / FoundryOutpostRegion.TryAttackEnemy + CannonCombat.TryFire 逐条翻译）：
        /// 目标检查 → 重炮两段式（过热迟滞、冷却、1 秒瞄准线、积热、熔穿过载穿甲）或普通武器（正面装甲减伤、侧后加成、标记、标记跳转）。
        /// </summary>
        public static CombatFireResult FireAt(ref CombatData d, int a, int t)
        {
            if (a < 0 || a >= d.Count)
            {
                return CombatFireResult.NoAttacker;
            }
            if (!d.IsAlive(a))
            {
                return CombatFireResult.AttackerDead;
            }
            if (t < 0 || t >= d.Count)
            {
                return CombatFireResult.TargetMissing;
            }
            if (!d.IsAlive(t))
            {
                return CombatFireResult.TargetDead;
            }
            if (!d.Has(t, CombatUnitFlags.Targetable) || d.Faction[t] == d.Faction[a])
            {
                return CombatFireResult.NotHostile;
            }
            int w = d.Weapon[a];
            if (w < 0 || w >= d.Weapons.Length)
            {
                return CombatFireResult.NoWeapon;
            }
            CombatWeapon wp = d.Weapons[w];
            if (wp.Mode == CombatWeaponMode.Cannon)
            {
                return FireCannon(ref d, a, t, wp);
            }
            if (wp.HasOutput == 0)
            {
                return CombatFireResult.NoCombatOutput;
            }

            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;

            if (wp.Mode == CombatWeaponMode.Projectile)
            {
                // 弹体武器：护甲 / 侧后在命中那一刻按飞行方向结算（弹道唯一真相在内核）。
                SpawnProjectile(ref d, a, t, w, wp);
                return CombatFireResult.Ok;
            }

            float damage = math.max(0f, wp.Damage);
            if (IsFrontalArmored(ref d, t, d.Pos[a]))
            {
                damage *= math.max(0f, 1f - d.Armor[t].x);
                Cue(ref d, CombatEventKind.ArmorHit, t, d.Id[a], 0f, d.Pos[t], 0);
            }
            damage *= BackMultiplier(ref d, t, d.Pos[a]);

            Cue(ref d, CombatEventKind.Fired, a, d.Id[t], 0f, d.Pos[t], 0);
            double now = d.Scalars[0].Time;
            bool wasMarked = d.MarkedUntil[t] > now;
            double2 primaryPos = d.Pos[t];
            if (!DamageUnit(ref d, t, damage, a))
            {
                return CombatFireResult.Invulnerable; // Demo：首领阶段不可伤时结算失败，不打标记、不跳转。
            }
            if (wp.MarkSeconds > 0f && d.IsAlive(t))
            {
                d.MarkedUntil[t] = now + wp.MarkSeconds;
            }
            if (wp.Reaction == CombatReaction.MarkJump && wasMarked)
            {
                MarkJump(ref d, a, t, primaryPos, damage, wp);
            }
            return CombatFireResult.Ok;
        }

        private static CombatFireResult FireCannon(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            double now = d.Scalars[0].Time;
            if (d.Has(a, CombatUnitFlags.Overheated))
            {
                if (d.Heat[a] > wp.RecoverBelow)
                {
                    return CombatFireResult.Overheated;
                }
                d.Set(a, CombatUnitFlags.Overheated, false);
            }
            if (now < d.NextFireAt[a])
            {
                return CombatFireResult.Cooldown;
            }
            if (d.AimReadyAt[a] <= 0)
            {
                d.AimReadyAt[a] = now + wp.AimSeconds;
                Cue(ref d, CombatEventKind.CannonCharge, a, d.Id[t], 0f, d.Pos[a], 0);
                return CombatFireResult.StillAiming;
            }
            if (now < d.AimReadyAt[a])
            {
                return CombatFireResult.StillAiming;
            }
            if (!d.IsAlive(t))
            {
                d.AimReadyAt[a] = 0;
                return CombatFireResult.TargetDead;
            }
            d.AimReadyAt[a] = 0;
            d.NextFireAt[a] = now + wp.Cooldown;
            bool overload = wp.Reaction == CombatReaction.MeltOverload;
            float heat = d.Heat[a] + wp.HeatPerShot + (overload ? wp.OverloadExtraHeat : 0f);
            d.Heat[a] = heat;
            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;
            Cue(ref d, CombatEventKind.CannonFire, a, d.Id[t], 0f, d.Pos[t], 0);
            if (overload)
            {
                Cue(ref d, CombatEventKind.MeltOverload, a, d.Id[t], 0f, d.Pos[t], 0);
            }
            if (heat >= wp.OverheatAt)
            {
                d.Set(a, CombatUnitFlags.Overheated, true);
                Cue(ref d, CombatEventKind.Overheat, a, 0, heat, d.Pos[a], 0);
            }
            // 基础伤害不受过载影响；过载只改积热与穿甲（Demo CannonCombat 类注释）。
            float damage = wp.Damage;
            if (IsFrontalArmored(ref d, t, d.Pos[a]))
            {
                float frac = d.Armor[t].x;
                float reduction = (!overload || d.Has(t, CombatUnitFlags.HeatResistant)) ? frac : math.max(0f, frac - wp.PierceBonus);
                damage *= math.max(0f, 1f - reduction);
                Cue(ref d, CombatEventKind.ArmorHit, t, d.Id[a], 0f, d.Pos[t], 0);
            }
            damage *= BackMultiplier(ref d, t, d.Pos[a]);
            return DamageUnit(ref d, t, damage, a) ? CombatFireResult.Ok : CombatFireResult.Invulnerable;
        }

        /// <summary>命中方向是否落在目标的正面装甲锥内（Demo FoundryOutpostRegion.IsFrontalHit；贴脸算正面）。</summary>
        public static bool IsFrontalArmored(ref CombatData d, int t, double2 attackerPos)
        {
            float4 ar = d.Armor[t];
            if (ar.x <= 0f)
            {
                return false;
            }
            double2 toA = attackerPos - d.Pos[t];
            if (math.lengthsq(toA) < 1e-6)
            {
                return true;
            }
            float cos = math.dot(new float2(ar.z, ar.w), (float2)math.normalize(toA));
            return cos >= ar.y;
        }

        /// <summary>侧后命中加成（Demo 主核心阶段二“侧后 +20%”：不在正面锥内 = 侧后；贴脸算正面）。</summary>
        public static float BackMultiplier(ref CombatData d, int t, double2 attackerPos)
        {
            float bonus = d.BackHit[t];
            if (bonus <= 0f)
            {
                return 1f;
            }
            float4 ar = d.Armor[t];
            double2 toA = attackerPos - d.Pos[t];
            if (math.lengthsq(toA) < 1e-6)
            {
                return 1f;
            }
            float cos = math.dot(new float2(ar.z, ar.w), (float2)math.normalize(toA));
            return cos < ar.y ? 1f + bonus : 1f;
        }

        /// <summary>标记跳转（Demo ApplyMarkJump）：主目标附近已标记、视线可达的存活敌人，按距离升序至多跳 JumpMax 个，每跳伤害 × 衰减。</summary>
        private static void MarkJump(ref CombatData d, int a, int primary, double2 primaryPos, float primaryDamage, in CombatWeapon wp)
        {
            double now = d.Scalars[0].Time;
            int max = math.min(wp.JumpMax, CombatConst.MaxJumpTargets);
            var picked = new FixedList64Bytes<int>();
            var pickedDist = new FixedList64Bytes<float>();
            int n = d.Count;
            byte hostile = d.Faction[primary];
            for (int k = 0; k < n; k++)
            {
                if (k == primary || !d.IsAlive(k) || d.Faction[k] != hostile || d.MarkedUntil[k] <= now)
                {
                    continue;
                }
                float dist = (float)math.distance(primaryPos, d.Pos[k]);
                if (dist > wp.JumpRange || !LineOfSight(ref d, primaryPos, d.Pos[k]))
                {
                    continue;
                }
                // 按 (距离, 槽位) 升序插入，只留前 max 个。
                int at = pickedDist.Length;
                for (int q = 0; q < pickedDist.Length; q++)
                {
                    if (dist < pickedDist[q])
                    {
                        at = q;
                        break;
                    }
                }
                if (at >= max)
                {
                    continue;
                }
                pickedDist.Insert(at, dist);
                picked.Insert(at, k);
                if (picked.Length > max)
                {
                    picked.RemoveAt(picked.Length - 1);
                    pickedDist.RemoveAt(pickedDist.Length - 1);
                }
            }
            float jump = primaryDamage;
            for (int q = 0; q < picked.Length; q++)
            {
                jump *= wp.JumpFalloff;
                DamageUnit(ref d, picked[q], jump, a);
            }
            if (picked.Length > 0)
            {
                Cue(ref d, CombatEventKind.MarkJump, a, d.Id[primary], picked.Length, primaryPos, 0);
            }
        }

        /// <summary>唯一扣血入口。血量在热更层的单位只发伤害请求（护甲 / 加成已算好），其余在内核扣血、按需报告、归零即阵亡。</summary>
        public static bool DamageUnit(ref CombatData d, int t, float damage, int attacker)
        {
            if (!d.IsAlive(t) || d.Has(t, CombatUnitFlags.Invulnerable))
            {
                return false;
            }
            damage = math.max(0f, damage);
            CombatCounters c = d.Counters[0];
            if (d.Faction[t] == (byte)CombatFaction.Player)
            {
                c.DamageToPlayer += (long)math.round(damage);
            }
            else
            {
                c.DamageToHostile += (long)math.round(damage);
            }
            d.Counters[0] = c;
            int attackerId = attacker >= 0 && attacker < d.Count ? d.Id[attacker] : 0;
            if (d.Has(t, CombatUnitFlags.ExternalHealth))
            {
                Gameplay(ref d, CombatEventKind.DamageRequest, t, attackerId, damage, d.Hp[t], d.Pos[t], 0, 0);
                return true;
            }
            float hp = math.max(0f, d.Hp[t] - damage);
            d.Hp[t] = hp;
            if (d.Has(t, CombatUnitFlags.Report))
            {
                Gameplay(ref d, CombatEventKind.Damaged, t, attackerId, damage, hp, d.Pos[t], 0, 0);
            }
            if (hp <= 0f)
            {
                Kill(ref d, t, attackerId);
            }
            return true;
        }

        public static void Kill(ref CombatData d, int t, int killerId)
        {
            if (!d.IsAlive(t))
            {
                return;
            }
            d.Set(t, CombatUnitFlags.Alive, false);
            d.Set(t, CombatUnitFlags.Targetable, false);
            d.Hp[t] = 0f;
            d.Cmd[t] = default;
            CombatCounters c = d.Counters[0];
            if (d.Faction[t] == (byte)CombatFaction.Player)
            {
                c.KillsPlayer++;
            }
            else
            {
                c.KillsHostile++;
            }
            d.Counters[0] = c;
            if (d.Has(t, CombatUnitFlags.Report) || d.Has(t, CombatUnitFlags.ExternalHealth))
            {
                Gameplay(ref d, CombatEventKind.Killed, t, killerId, 0f, 0f, d.Pos[t], 0, 0);
            }
            if (d.Has(t, CombatUnitFlags.RemoveOnDeath))
            {
                CombatScalars s = d.Scalars[0];
                s.Tombstones++;
                d.Scalars[0] = s;
            }
        }

        public static void Heal(ref CombatData d, int t, float amount, int healer)
        {
            if (!d.IsAlive(t))
            {
                return;
            }
            int healerId = healer >= 0 && healer < d.Count ? d.Id[healer] : 0;
            if (d.Has(t, CombatUnitFlags.ExternalHealth))
            {
                Gameplay(ref d, CombatEventKind.HealRequest, t, healerId, amount, d.Hp[t], d.Pos[t], 0, 0);
                return;
            }
            if (d.Hp[t] >= d.MaxHp[t])
            {
                return;
            }
            float hp = math.min(d.MaxHp[t], d.Hp[t] + math.max(0f, amount));
            float healed = hp - d.Hp[t];
            d.Hp[t] = hp;
            if (d.Has(t, CombatUnitFlags.Report))
            {
                Gameplay(ref d, CombatEventKind.Healed, t, healerId, healed, hp, d.Pos[t], 0, 0);
            }
        }

        // ─────────────────────────────── 目标选择与视线 ───────────────────────────────

        /// <summary>
        /// 找射程内的敌对目标：己方找敌方、敌方找己方（中立单位不会被自动选中）。<paramref name="needsLos"/> 时过滤掉视线被障碍挡住的。
        /// 最近模式：距离最小，并列取槽位大者（与 Demo“按列表顺序扫描、距离 ≤ 当前最佳就替换”一致）；最低耐久模式：血量比例最小，并列取距离近者。
        /// </summary>
        public static int FindTarget(ref CombatData d, ref CombatGrid grid, int i, float range, bool needsLos, CombatTargetMode mode)
        {
            if (range <= 0f)
            {
                return -1;
            }
            byte want = d.Faction[i] == (byte)CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            double2 pos = d.Pos[i];
            int best = -1;
            double bestDist = range;
            float bestPct = float.MaxValue;
            // 网格在步首按当时的位置建好；同一步里单位还会移动（每步至多零点几米），查询范围多放 GridSlack 米，结果仍按真实距离过滤。
            double reach = range + GridSlack;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
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
                        if (k == i || d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                        {
                            continue;
                        }
                        double dist = math.distance(pos, d.Pos[k]);
                        if (dist > range)
                        {
                            continue;
                        }
                        if (mode == CombatTargetMode.LowestHealth)
                        {
                            float pct = d.MaxHp[k] > 0f ? d.Hp[k] / d.MaxHp[k] : 1f;
                            bool better = pct < bestPct || (pct == bestPct && (dist < bestDist || (dist == bestDist && k > best)));
                            if (!better)
                            {
                                continue;
                            }
                            if (needsLos && !LineOfSight(ref d, pos, d.Pos[k]))
                            {
                                continue;
                            }
                            bestPct = pct;
                            bestDist = dist;
                            best = k;
                        }
                        else
                        {
                            bool better = best < 0 ? dist <= bestDist : (dist < bestDist || (dist == bestDist && k > best));
                            if (!better)
                            {
                                continue;
                            }
                            if (needsLos && !LineOfSight(ref d, pos, d.Pos[k]))
                            {
                                continue;
                            }
                            bestDist = dist;
                            best = k;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>视线（Demo IsLineOfSightClear）：线段与障碍圆相交即被挡；起点、终点自己所在的障碍不算。</summary>
        public static bool LineOfSight(ref CombatData d, double2 from, double2 to)
        {
            for (int k = 0; k < d.Obstacles.Length; k++)
            {
                float3 o = d.Obstacles[k];
                double2 c = new double2(o.x, o.y);
                if (math.distance(c, to) <= o.z + 0.1)
                {
                    continue;
                }
                if (math.distance(c, from) <= o.z + 0.1)
                {
                    continue;
                }
                if (SegmentIntersectsCircle(from, to, c, o.z))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SegmentIntersectsCircle(double2 a, double2 b, double2 center, double radius)
        {
            double2 ab = b - a;
            double lenSq = math.lengthsq(ab);
            if (lenSq <= 0.0001)
            {
                return math.distance(a, center) <= radius;
            }
            double t = math.saturate(math.dot(center - a, ab) / lenSq);
            double2 closest = a + ab * t;
            return math.distance(closest, center) <= radius;
        }

        // ─────────────────────────────── 弹体 ───────────────────────────────

        private static void StepProjectiles(ref CombatData d, ref CombatGrid grid, float dt)
        {
            int m = d.Projectiles.Length;
            int write = 0;
            float maxR = grid.MaxRadius;
            for (int p = 0; p < m; p++)
            {
                CombatProjectile pr = d.Projectiles[p];
                pr.Prev = pr.Pos;
                double2 next = pr.Pos + (double2)(pr.Vel * dt);
                pr.Life -= dt;
                byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
                // 线段 prev→next 与单位圆（半径 = 单位半径 + 弹体半径）求最早交点。
                double reach = pr.Radius + maxR;
                int cx0 = grid.CellOf(math.min(pr.Prev.x, next.x) - reach);
                int cx1 = grid.CellOf(math.max(pr.Prev.x, next.x) + reach);
                int cy0 = grid.CellOf(math.min(pr.Prev.y, next.y) - reach);
                int cy1 = grid.CellOf(math.max(pr.Prev.y, next.y) + reach);
                int hit = -1;
                double hitT = 2.0;
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
                            if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                            {
                                continue;
                            }
                            double tHit = SegmentCircleT(pr.Prev, next, d.Pos[k], d.Radius[k] + pr.Radius);
                            if (tHit < 0)
                            {
                                continue;
                            }
                            if (tHit < hitT || (tHit == hitT && k < hit))
                            {
                                hitT = tHit;
                                hit = k;
                            }
                        }
                    }
                }
                if (hit >= 0)
                {
                    int owner = d.SlotOf(pr.Owner);
                    double2 hitPos = pr.Prev + (next - pr.Prev) * hitT;
                    // 命中方向：沿飞行方向反推“攻击者在哪一侧”（装甲 / 侧后判定用）。
                    double2 fromSide = d.Pos[hit] - (double2)(math.normalizesafe(pr.Vel) * 10f);
                    float dmg = pr.Damage;
                    if (IsFrontalArmored(ref d, hit, fromSide))
                    {
                        dmg *= math.max(0f, 1f - d.Armor[hit].x);
                    }
                    dmg *= BackMultiplier(ref d, hit, fromSide);
                    DamageUnit(ref d, hit, dmg, owner);
                    Cue(ref d, CombatEventKind.ProjectileHit, hit, pr.Owner, dmg, hitPos, 0);
                    CombatCounters c = d.Counters[0];
                    c.ProjectilesHit++;
                    d.Counters[0] = c;
                    continue;
                }
                if (pr.Life <= 0f)
                {
                    CombatCounters c = d.Counters[0];
                    c.ProjectilesExpired++;
                    d.Counters[0] = c;
                    continue;
                }
                pr.Pos = next;
                d.Projectiles[write++] = pr;
            }
            d.Projectiles.ResizeUninitialized(write);
        }

        /// <summary>线段 a→b 与圆的最早交点参数 t∈[0,1]；不相交返回 -1。起点已在圆内返回 0。</summary>
        private static double SegmentCircleT(double2 a, double2 b, double2 c, double r)
        {
            double2 f = a - c;
            if (math.lengthsq(f) <= r * r)
            {
                return 0;
            }
            double2 dv = b - a;
            double A = math.lengthsq(dv);
            if (A < 1e-12)
            {
                return -1;
            }
            double B = 2 * math.dot(f, dv);
            double C = math.lengthsq(f) - r * r;
            double disc = B * B - 4 * A * C;
            if (disc < 0)
            {
                return -1;
            }
            double t = (-B - math.sqrt(disc)) / (2 * A);
            return t >= 0 && t <= 1 ? t : -1;
        }

        // ─────────────────────────────── 兴趣点 ───────────────────────────────

        private static void StepPois(ref CombatData d)
        {
            for (int q = 0; q < d.Pois.Length; q++)
            {
                if (d.PoiReached[q] != 0)
                {
                    continue;
                }
                float3 poi = d.Pois[q];
                double2 c = new double2(poi.x, poi.y);
                for (int k = 0; k < d.Count; k++)
                {
                    if (!d.IsAlive(k) || d.Faction[k] != (byte)CombatFaction.Player || d.Kind[k] != (byte)CombatUnitKind.Machine)
                    {
                        continue;
                    }
                    if (math.distance(d.Pos[k], c) <= poi.z)
                    {
                        d.PoiReached[q] = 1;
                        Gameplay(ref d, CombatEventKind.PoiReached, k, q, 0f, 0f, c, 0, 0);
                        break;
                    }
                }
            }
        }

        // ─────────────────────────────── 压实 ───────────────────────────────

        /// <summary>“阵亡即移除”的单位积累到一定比例时压实：保持存活单位的相对顺序（规范顺序不变），ID 不变，只改槽位映射。</summary>
        public static void MaybeCompact(ref CombatData d)
        {
            CombatScalars s = d.Scalars[0];
            int n = d.Count;
            if (s.Tombstones <= 0 || s.Tombstones < math.max(16, (int)(n * d.Config.CompactRatio)))
            {
                return;
            }
            Compact(ref d);
        }

        public static void Compact(ref CombatData d)
        {
            int n = d.Count;
            int write = 0;
            for (int r = 0; r < n; r++)
            {
                bool drop = !d.IsAlive(r) && d.Has(r, CombatUnitFlags.RemoveOnDeath);
                int id = d.Id[r];
                if (drop || id <= 0)
                {
                    if (id > 0 && id < d.SlotOfId.Length)
                    {
                        d.SlotOfId[id] = -1;
                    }
                    continue;
                }
                if (write != r)
                {
                    d.MoveSlot(r, write);
                }
                d.SlotOfId[id] = write;
                write++;
            }
            d.Truncate(write);
            CombatScalars s = d.Scalars[0];
            s.Tombstones = 0;
            s.Revision++;
            d.Scalars[0] = s;
            CombatCounters c = d.Counters[0];
            c.Compactions++;
            d.Counters[0] = c;
        }

        // ─────────────────────────────── 事件 ───────────────────────────────

        public static void Gameplay(ref CombatData d, CombatEventKind kind, int slot, int other, float v, float v2, double2 pos, byte code, byte code2)
        {
            d.Gameplay.Add(new CombatEvent
            {
                Kind = kind,
                Seq = NextSeq(ref d),
                Code = code,
                Code2 = code2,
                Unit = slot >= 0 && slot < d.Count ? d.Id[slot] : 0,
                Other = other,
                Value = v,
                Value2 = v2,
                Pos = pos,
            });
        }

        private static int NextSeq(ref CombatData d)
        {
            CombatScalars s = d.Scalars[0];
            int seq = ++s.EventSeq;
            d.Scalars[0] = s;
            return seq;
        }

        public static void Cue(ref CombatData d, CombatEventKind kind, int slot, int other, float v, double2 pos, byte code)
        {
            CombatScalars s = d.Scalars[0];
            if (s.CuesThisStep >= d.Config.MaxCueEventsPerStep)
            {
                CombatCounters c = d.Counters[0];
                c.CuesDropped++;
                d.Counters[0] = c;
                return;
            }
            s.CuesThisStep++;
            d.Scalars[0] = s;
            d.Cues.Add(new CombatEvent
            {
                Kind = kind,
                Seq = NextSeq(ref d),
                Code = code,
                Unit = slot >= 0 && slot < d.Count ? d.Id[slot] : 0,
                Other = other,
                Value = v,
                Pos = pos,
            });
        }
    }

    /// <summary>
    /// 每步重建的均匀空间网格（Allocator.Temp）：可被选中的存活单位按 (格键, 槽位) 排序，格键 → (起点, 个数)。
    /// 查询结果只由“比较规则”决定（最小距离、并列取槽位），与遍历顺序无关，所以是确定性的。
    /// </summary>
    public struct CombatGrid : IDisposable
    {
        private struct Entry : IComparable<Entry>
        {
            public long Key;
            public int Slot;

            public int CompareTo(Entry o)
            {
                int c = Key.CompareTo(o.Key);
                return c != 0 ? c : Slot.CompareTo(o.Slot);
            }
        }

        public NativeArray<int> Slots;
        private NativeParallelHashMap<long, int2> _cells;
        private readonly float _cell;
        private readonly double _inv;
        public float MaxRadius;
        private bool _built;

        public CombatGrid(float cell)
        {
            _cell = math.max(CombatConst.MinGridCell, cell);
            _inv = 1.0 / _cell;
            Slots = default;
            _cells = default;
            MaxRadius = 0f;
            _built = false;
        }

        public int CellOf(double v) => (int)math.floor(v * _inv);

        private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public void Build(ref CombatData d)
        {
            int n = d.Count;
            var entries = new NativeList<Entry>(math.max(1, n), Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                if (!d.IsAlive(i) || !d.Has(i, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double2 p = d.Pos[i];
                entries.Add(new Entry { Key = Key(CellOf(p.x), CellOf(p.y)), Slot = i });
                MaxRadius = math.max(MaxRadius, d.Radius[i]);
            }
            entries.Sort();
            Slots = new NativeArray<int>(math.max(1, entries.Length), Allocator.Temp);
            _cells = new NativeParallelHashMap<long, int2>(math.max(1, entries.Length), Allocator.Temp);
            int k = 0;
            while (k < entries.Length)
            {
                long key = entries[k].Key;
                int start = k;
                while (k < entries.Length && entries[k].Key == key)
                {
                    Slots[k] = entries[k].Slot;
                    k++;
                }
                _cells.TryAdd(key, new int2(start, k - start));
            }
            entries.Dispose();
            _built = true;
        }

        public bool TryGetCell(int cx, int cy, out int start, out int count)
        {
            if (_built && _cells.TryGetValue(Key(cx, cy), out int2 v))
            {
                start = v.x;
                count = v.y;
                return true;
            }
            start = 0;
            count = 0;
            return false;
        }

        public void Dispose()
        {
            if (!_built)
            {
                return;
            }
            Slots.Dispose();
            _cells.Dispose();
            _built = false;
        }
    }
}
