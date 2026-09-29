using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using BinGames.Sim.Nav;

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
    public static partial class CombatLogic
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
            MaybeCompactRoutes(ref d);

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

            // 5b. FG2-FW-02：读法生成的无人机、区域、回波与状态标签（统一时钟，暂停不走、倍速按游戏时间）。
            StepReadings(ref d, dt);

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
                    double2 p0 = d.Pos[i];
                    double2 p = p0 + step;
                    CombatScalars s = d.Scalars[0];
                    p.y = math.clamp(p.y, s.DirectMinY, s.DirectMaxY);
                    // FG0-ARCH-06：星球格网上直控也不能穿过悬崖、水和建筑（贴着边滑动）。
                    d.Pos[i] = MoveCollide(ref d, i, p0, p);
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
                    double2 targetPos = cmd.Pos;
                    cmd = default;
                    d.Cmd[i] = cmd;
                    byte navFail = d.NavFail[i];
                    DropRoute(ref d, i);
                    d.NavSt[i] = (byte)CombatNavState.None;
                    if (kind == CombatCommandKind.WorkMove)
                    {
                        if (reason == CombatEndReason.Unreachable)
                        {
                            // FG0-ARCH-06：目标无法到达——不原地发呆，把原因交给热更层（工单转等待并通知）。
                            // 事件位置 = 赶路目标点（没有工单的直接移动命令靠它把通知定位到玩家想去的地方）。
                            Gameplay(ref d, CombatEventKind.WorkBlocked, i, 0, navFail, 0f, targetPos, navFail, (byte)kind);
                        }
                        else
                        {
                            Gameplay(ref d, CombatEventKind.WorkArrived, i, 0, 0f, 0f, d.Pos[i], 0, 0);
                        }
                    }
                    else
                    {
                        // 无法到达时事件位置 = 命令目标点（通知定位到玩家想去的地方）；其余 = 执行者位置（与 Demo 一致）。
                        Gameplay(ref d, CombatEventKind.CommandEnded, i, target, navFail, 0f,
                            reason == CombatEndReason.Unreachable ? targetPos : d.Pos[i], (byte)reason, (byte)kind);
                    }
                }
                else
                {
                    d.Cmd[i] = cmd;
                }
            }

            CombatCommandKind ck = d.Cmd[i].Kind;
            if (ck == CombatCommandKind.Move || ck == CombatCommandKind.Retreat || ck == CombatCommandKind.Guard || ck == CombatCommandKind.Attack)
            {
                Separate(ref d, ref grid, i, d.Speed[i], dt);
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

            if (d.Config.NavEnabled != 0 && cmd.Kind != CombatCommandKind.Attack)
            {
                return TickNavMove(ref d, i, ref cmd, dt, out reason);
            }

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
                // FG2-FW-02：格斗 / 力场这类要贴近的载体按武器的接近距离收紧命令射程（目标半径算进去）；固件读法的出手间隔倍率（电容蓄力）乘在冷却上。
                float attackRange = cmd.AttackRange;
                int wi = d.Weapon[i];
                float cdScale = 1f;
                if (wi >= 0 && wi < d.Weapons.Length)
                {
                    CombatReading rd = d.Weapons[wi].Reading;
                    if (rd.Approach > 0f)
                    {
                        attackRange = math.min(attackRange, rd.Approach + d.Radius[t]);
                    }
                    if (rd.CooldownScale > 0f)
                    {
                        cdScale = rd.CooldownScale;
                    }
                }
                bool inRangeA = distA <= attackRange;
                if (!inRangeA)
                {
                    StepTowards(ref d, i, pos, cmd.Pos, dt);
                }
                cmd.AtkCd -= dt;
                if (inRangeA && cmd.AtkCd <= 0f)
                {
                    cmd.AtkCd = cmd.AttackCooldown * cdScale;
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
            d.Pos[i] = MoveCollide(ref d, i, pos, pos + step);
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
            int t = FindTarget(ref d, ref grid, i, EngageRange(ref d, i, wp), d.Has(i, CombatUnitFlags.NeedsLos), wp.TargetMode);
            if (t < 0)
            {
                return;
            }
            d.Cycle[i] = EffectiveCooldown(wp);
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
                int locked = FindTarget(ref d, ref grid, i, EngageRange(ref d, i, wp), los, wp.TargetMode);
                d.Cycle[i] = EffectiveCooldown(wp);
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
            int t = FindTarget(ref d, ref grid, i, EngageRange(ref d, i, wp), los, wp.TargetMode);
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
                    double2 proposed = pos + math.normalize(away) * (SlowedSpeed(ref d, i, p.Speed) * dt);
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
                    d.Pos[i] = pos + to / len * math.min(SlowedSpeed(ref d, i, p.Speed) * dt, len);
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
            p.Speed = SlowedSpeed(ref d, i, p.Speed); // FG2-FW-02：减速状态
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
            p.Speed = SlowedSpeed(ref d, i, p.Speed); // FG2-FW-02：减速状态
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
                if (d.Config.NavEnabled != 0)
                {
                    // FG0-ARCH-06：没有目标时沿寻路路线朝目标点前进（允许部分路线：堵死时走到最近处，攻城在 FG6-DEF-05）。
                    RaiderRouteMove(ref d, i, p.Speed, dt);
                }
                else
                {
                    MoveToward(ref d, i, pos, d.Home[i], p.Speed * dt, 0.5);
                }
                Separate(ref d, ref grid, i, p.Speed, dt);
                return;
            }
            if (d.Config.NavEnabled != 0 && (d.NavSt[i] == (byte)CombatNavState.Following || d.NavSt[i] == (byte)CombatNavState.Awaiting))
            {
                // 追击目标会离开路线：目标丢失后从当前位置重新要路线（在等的结果按序号作废）。
                DropRoute(ref d, i);
                d.NavSt[i] = (byte)CombatNavState.NeedRoute;
            }
            double dist = math.distance(pos, d.Pos[t]);
            float engage = EngageRange(ref d, i, wp);
            if (dist > engage)
            {
                MoveToward(ref d, i, pos, d.Pos[t], p.Speed * dt, engage * 0.9);
                Separate(ref d, ref grid, i, p.Speed, dt);
                return;
            }
            if (c > 0f)
            {
                return;
            }
            d.Cycle[i] = EffectiveCooldown(wp);
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
            d.Pos[i] = MoveCollide(ref d, i, pos, pos + to / len * math.min(stepLen, len - stopAt));
        }

        /// <summary>单位主动攻击（驻守开火 / 瞄准线 / 突袭者 / 炮塔）：弹体武器生成弹体，其余即时命中。</summary>
        private static void UnitAttack(ref CombatData d, int i, int t, in CombatWeapon wp)
        {
            // FG2-FW-02：与编队攻击 / 直控同一道开火门槛（格斗 / 力场的触及、过热迟滞）与同一份积热——驻守开火的炮塔也不例外。
            if (PreFireGate(ref d, i, t, wp) != CombatFireResult.Ok)
            {
                return;
            }
            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;
            AddShotHeat(ref d, i, wp);
            if (wp.Mode == CombatWeaponMode.Projectile)
            {
                SpawnProjectile(ref d, i, t, d.Weapon[i], wp);
                return;
            }
            if (wp.Reading.Carrier != CombatCarrier.Projectile || wp.Reading.HasFirmwareReading)
            {
                // FG2-FW-02：带载体 / 读法的即时武器（炮塔用的推铲、带固件的炮塔）走与机器开火同一套投送。
                DeliverInstant(ref d, i, t, wp, false);
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
        /// 直控点击的开火入口：非重炮武器一次点击一发（Demo 语义，没有出手间隔）；带蓄力读法（出手间隔倍率 &gt; 1，电容蓄力）的武器
        /// 要等上一发之后蓄满（基础出手间隔 × 倍率）才能再点出——否则直控会白拿“单发伤害 ×”而不付“出手间隔 ×”。重炮的冷却在 <see cref="FireCannon"/>。
        /// </summary>
        public static CombatFireResult FireDirect(ref CombatData d, int a, int t)
        {
            if (a >= 0 && a < d.Count && d.IsAlive(a))
            {
                int w = d.Weapon[a];
                if (w >= 0 && w < d.Weapons.Length)
                {
                    CombatWeapon wp = d.Weapons[w];
                    if (wp.Mode != CombatWeaponMode.Cannon && IsCharged(wp) && d.Scalars[0].Time < d.NextFireAt[a])
                    {
                        return CombatFireResult.Cooldown;
                    }
                }
            }
            return FireAt(ref d, a, t);
        }

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

            CombatFireResult gate = PreFireGate(ref d, a, t, wp);
            if (gate != CombatFireResult.Ok)
            {
                return gate;
            }

            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;
            RawFired(ref d, a, t);
            AddShotHeat(ref d, a, wp);
            if (IsCharged(wp))
            {
                // FG2-FW-02：蓄力读法（出手间隔倍率 > 1）记下这一发之后多久才蓄满；直控点击按它拦（FireDirect），编队攻击的冷却本身已乘同一倍率。
                d.NextFireAt[a] = d.Scalars[0].Time + EffectiveCooldown(wp);
            }

            if (wp.Mode == CombatWeaponMode.Projectile)
            {
                // 弹体武器：护甲 / 侧后在命中那一刻按飞行方向结算（弹道唯一真相在内核）；读法在命中时结算。
                SpawnProjectile(ref d, a, t, w, wp);
                if (wp.Reading.EscortDrones > 0)
                {
                    LaunchDrones(ref d, a, wp, wp.Reading.EscortDrones);
                }
                return CombatFireResult.Ok;
            }

            // FG2-FW-02：载体投送 + 读法（射弹 / 格斗 / 无人机 / 力场 / 布区）；没有读法的射弹载体与 Demo 逐字同一结算（正面装甲、侧后、标记、标记跳转）。
            return DeliverInstant(ref d, a, t, wp, true);
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
            d.NextFireAt[a] = now + EffectiveCooldown(wp);
            // FG1-SIG-03：门控反应（信号带进来的核心固件）发动过一次后就被压住，直到热更层按冷却重新下发武器参数。
            bool overload = wp.Reaction == CombatReaction.MeltOverload && !d.Has(a, CombatUnitFlags.ReactionSpent);
            float heat = d.Heat[a] + wp.HeatPerShot + (overload ? wp.OverloadExtraHeat : 0f);
            d.Heat[a] = heat;
            CombatCounters c = d.Counters[0];
            c.ShotsFired++;
            d.Counters[0] = c;
            RawFired(ref d, a, t);
            Cue(ref d, CombatEventKind.CannonFire, a, d.Id[t], 0f, d.Pos[t], 0);
            if (overload)
            {
                Cue(ref d, CombatEventKind.MeltOverload, a, d.Id[t], 0f, d.Pos[t], 0);
                ReactionFired(ref d, a, t, CombatReaction.MeltOverload);
            }
            if (heat >= wp.OverheatAt)
            {
                d.Set(a, CombatUnitFlags.Overheated, true);
                Cue(ref d, CombatEventKind.Overheat, a, 0, heat, d.Pos[a], 0);
            }
            // 基础伤害不受过载影响；过载只改积热与穿甲（Demo CannonCombat 类注释）。FG2-FW-02：固件读法（穿甲、伤害倍率、增幅……）同一处结算。
            float pierce = (!overload || d.Has(t, CombatUnitFlags.HeatResistant)) ? 0f : wp.PierceBonus;
            bool wasMarked = d.MarkedUntil[t] > now;
            double2 hitPos = d.Pos[t];
            float damage = StrikeDamage(ref d, a, t, wp.Damage, wp.Reading, d.Pos[a], true, pierce);
            if (!DamageUnit(ref d, t, damage, a))
            {
                return CombatFireResult.Invulnerable;
            }
            CannonReadingHit(ref d, a, t, hitPos, damage, wp, wasMarked);
            if (wp.Reading.EscortDrones > 0)
            {
                LaunchDrones(ref d, a, wp, wp.Reading.EscortDrones);
            }
            return CombatFireResult.Ok;
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
            int jumped = MarkJumpCore(ref d, a, primary, primaryPos, primaryDamage, wp.JumpRange, wp.JumpFalloff, wp.JumpMax);
            if (jumped > 0)
            {
                Cue(ref d, CombatEventKind.MarkJump, a, d.Id[primary], jumped, primaryPos, 0);
                ReactionFired(ref d, a, primary, CombatReaction.MarkJump);
            }
        }

        /// <summary>
        /// FG1-SIG-03（FGR-SIG-033）：具名反应真正发动了一次。发不丢的玩法事件（核心固件冷却、暴露按它结算，不受提示事件每步上限影响）；
        /// 这台单位的反应若来自信号带进来的核心固件（<see cref="CombatUnitFlags.ReactionGated"/>），当场压住（<see cref="CombatUnitFlags.ReactionSpent"/>），
        /// 事件被顺延处理的那几步里也不会再发动。
        /// </summary>
        private static void ReactionFired(ref CombatData d, int a, int t, CombatReaction reaction)
        {
            bool gated = d.Has(a, CombatUnitFlags.ReactionGated);
            if (gated)
            {
                d.Set(a, CombatUnitFlags.ReactionSpent, true);
            }
            Gameplay(ref d, CombatEventKind.ReactionFired, a, d.Id[t], 0f, 0f, d.Pos[t], (byte)reaction, (byte)(gated ? 1 : 0));
        }

        /// <summary>
        /// FG1-SIG-06（FGR-SIG-061）：开火的单位带 <see cref="CombatUnitFlags.RawGated"/>（接入口里插着信号裸跑的未破解常规固件、计次间隔已过）时，
        /// 这一发算一次“发动”：当场置 <see cref="CombatUnitFlags.RawSpent"/>（等热更层按间隔重新下发之前不再计），发不丢的玩法事件。O(1)，只有被接入的那一台可能带这个标志。
        /// </summary>
        private static void RawFired(ref CombatData d, int a, int t)
        {
            if (!d.Has(a, CombatUnitFlags.RawGated) || d.Has(a, CombatUnitFlags.RawSpent))
            {
                return;
            }
            d.Set(a, CombatUnitFlags.RawSpent, true);
            Gameplay(ref d, CombatEventKind.RawFirmwareFired, a, d.Id[t], 0f, 0f, d.Pos[t], 0, 0);
        }

        /// <summary>唯一扣血入口。血量在热更层的单位只发伤害请求（护甲 / 加成已算好），其余在内核扣血、按需报告、归零即阵亡。</summary>
        public static bool DamageUnit(ref CombatData d, int t, float damage, int attacker)
        {
            if (!d.IsAlive(t) || d.Has(t, CombatUnitFlags.Invulnerable))
            {
                return false;
            }
            // FG2-FW-02：易伤状态（冻结等）统一在这里乘上——任何来源的伤害都吃。
            damage = math.max(0f, damage) * VulnMultiplier(ref d, t);
            CombatCounters c = d.Counters[0];
            if (d.Faction[t] == (byte)CombatFaction.Player)
            {
                c.DamageToPlayer += (long)math.round(damage);
            }
            else
            {
                c.DamageToHostile += (long)math.round(damage);
                d.DamageDealtHostile[0] = d.DamageDealtHostile[0] + damage; // FG2-FW-04：伤害归因的分母（精确值，不进快照）。
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
                    bool landed = DamageUnit(ref d, hit, dmg, owner);
                    Cue(ref d, CombatEventKind.ProjectileHit, hit, pr.Owner, dmg, hitPos, 0);
                    if (landed && pr.Weapon >= 0 && pr.Weapon < d.Weapons.Length)
                    {
                        // FG2-FW-02：弹体武器的读法在命中那一刻结算（开火者阵亡后照常，回收修复 / 跃击跳过）。
                        ProjectileReadingHit(ref d, owner, hit, hitPos, dmg, d.Weapons[pr.Weapon], fromSide);
                    }
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

        // ─────────────────────────────── 寻路（FG0-ARCH-06）───────────────────────────────

        /// <summary>世界坐标 → 格子（格心在整数处，四舍五入到最近格心）。与寻路内核、热更层的换算一致。</summary>
        public static int2 CellOf(double2 p) => new int2((int)math.floor(p.x + 0.5), (int)math.floor(p.y + 0.5));

        public static byte ClassOf(ref CombatData d, int i) => d.Faction[i] == (byte)CombatFaction.Player ? NavConst.ClassPlayer : NavConst.ClassHostile;

        private static bool Walkable(ref CombatData d, int2 cell, int cls) => NavGridOps.Passable(ref d.Nav, cell, cls);

        /// <summary>
        /// 一步移动的格网碰撞（只在 <see cref="CombatConfig.NavEnabled"/> 的地点）：目标格走不了时先试只沿 x、再试只沿 y（贴边滑动），都不行就不动；
        /// 斜着跨格时两侧都被挡也算撞上（不从两个障碍之间的缝里挤过去）。单位本身站在不可走的格子里（被新建筑压住）时放行，让它走出来。
        /// </summary>
        public static double2 MoveCollide(ref CombatData d, int i, double2 from, double2 to)
        {
            if (d.Config.NavEnabled == 0)
            {
                return to;
            }
            int cls = ClassOf(ref d, i);
            int2 cf = CellOf(from);
            int2 ct = CellOf(to);
            if (ct.x == cf.x && ct.y == cf.y)
            {
                return to;
            }
            if (!Walkable(ref d, cf, cls))
            {
                return to;
            }
            bool diag = ct.x != cf.x && ct.y != cf.y;
            if (Walkable(ref d, ct, cls) && (!diag || (Walkable(ref d, new int2(ct.x, cf.y), cls) && Walkable(ref d, new int2(cf.x, ct.y), cls))))
            {
                return to;
            }
            var ax = new double2(to.x, from.y);
            if (Walkable(ref d, CellOf(ax), cls))
            {
                return ax;
            }
            var ay = new double2(from.x, to.y);
            if (Walkable(ref d, CellOf(ay), cls))
            {
                return ay;
            }
            return from;
        }

        private static void DropRoute(ref CombatData d, int i)
        {
            CombatScalars s = d.Scalars[0];
            s.RouteGarbage += d.RouteLen[i];
            d.Scalars[0] = s;
            d.RouteLen[i] = 0;
            d.RouteIdx[i] = 0;
        }

        /// <summary>发出一条寻路请求（序号递增；单位转为“等路线”，原地等待固定延迟后的结果）。</summary>
        private static void EmitNavRequest(ref CombatData d, int i, int2 goal, byte flags)
        {
            CombatScalars s = d.Scalars[0];
            s.NextNavSerial++;
            d.Scalars[0] = s;
            DropRoute(ref d, i);
            d.NavSerial[i] = s.NextNavSerial;
            d.NavSt[i] = (byte)CombatNavState.Awaiting;
            d.NavFail[i] = 0;
            d.NavOut.Add(new CombatNavRequest
            {
                UnitId = d.Id[i],
                Serial = s.NextNavSerial,
                Start = CellOf(d.Pos[i]),
                Goal = goal,
                Class = ClassOf(ref d, i),
                Flags = flags,
            });
        }

        /// <summary>当前要走向的点：路线的下一个路点；最后一段走向精确终点。</summary>
        private static double2 RouteTarget(ref CombatData d, int i, int idx)
        {
            int len = d.RouteLen[i];
            if (idx >= len - 1)
            {
                return d.RouteEnd[i];
            }
            int2 c = d.RoutePts[d.RouteOff[i] + idx];
            return new double2(c.x, c.y);
        }

        /// <summary>沿路线走 speed × dt（一步里可以连续越过几个路点），终点处停住；最后按格网碰撞落位。</summary>
        private static void FollowRoute(ref CombatData d, int i, float speed, float dt)
        {
            double remaining = speed * dt;
            double2 p0 = d.Pos[i];
            double2 p = p0;
            int idx = d.RouteIdx[i];
            int len = d.RouteLen[i];
            for (int guard = 0; guard < 8 && remaining > 1e-12; guard++)
            {
                double2 target = RouteTarget(ref d, i, idx);
                double2 to = target - p;
                double dist = math.length(to);
                if (dist <= remaining)
                {
                    p = target;
                    remaining -= dist;
                    if (idx < len)
                    {
                        idx++;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    p += to / dist * remaining;
                    remaining = 0;
                }
            }
            d.RouteIdx[i] = idx;
            d.Pos[i] = MoveCollide(ref d, i, p0, p);
        }

        /// <summary>沿路线剩余的长度（受阻判定、热更层赶路看门狗用）。</summary>
        public static double RemainingRouteLength(ref CombatData d, int i)
        {
            double2 p = d.Pos[i];
            int len = d.RouteLen[i];
            int idx = d.RouteIdx[i];
            double sum = 0;
            for (int k = idx; ; k++)
            {
                double2 t = RouteTarget(ref d, i, k);
                sum += math.distance(p, t);
                p = t;
                if (k >= len - 1)
                {
                    break;
                }
            }
            return sum;
        }

        /// <summary>
        /// 寻路地点的移动命令（移动 / 守备 / 撤退 / 工作赶路）：需要路线时发请求并原地等；拿到路线沿路点走；失败以“无法到达”结束。
        /// 到达判定与 Demo 一致（进入到达半径；工作赶路对齐到目标点），但只在最后一段（直线可见终点）上判，不会隔着墙“到达”。
        /// </summary>
        private static bool TickNavMove(ref CombatData d, int i, ref CombatCommand cmd, float dt, out CombatEndReason reason)
        {
            reason = CombatEndReason.None;
            double2 pos = d.Pos[i];
            double arrive2 = (double)cmd.Arrive * cmd.Arrive;
            var st = (CombatNavState)d.NavSt[i];
            if (st == CombatNavState.None || st == CombatNavState.NeedRoute)
            {
                if (math.distancesq(pos, cmd.Pos) <= arrive2 && NavSearch.LineClear(ref d.Nav, CellOf(pos), CellOf(cmd.Pos), ClassOf(ref d, i)))
                {
                    if (cmd.Kind == CombatCommandKind.Guard)
                    {
                        return false;
                    }
                    if (cmd.Kind == CombatCommandKind.WorkMove)
                    {
                        d.Pos[i] = cmd.Pos;
                    }
                    reason = CombatEndReason.Arrived;
                    return true;
                }
                EmitNavRequest(ref d, i, CellOf(cmd.Pos), 0);
                return false;
            }
            if (st == CombatNavState.Awaiting)
            {
                return false;
            }
            if (st == CombatNavState.Failed)
            {
                reason = CombatEndReason.Unreachable;
                return true;
            }
            int len = d.RouteLen[i];
            int idx = d.RouteIdx[i];
            double2 end = d.RouteEnd[i];
            if (idx >= len - 1 && math.distancesq(pos, end) <= arrive2)
            {
                if (cmd.Kind == CombatCommandKind.Guard)
                {
                    return false; // 持久命令：到位后一直守到取消。
                }
                if (cmd.Kind == CombatCommandKind.WorkMove)
                {
                    d.Pos[i] = end;
                }
                reason = CombatEndReason.Arrived;
                return true;
            }
            FollowRoute(ref d, i, d.Speed[i], dt);
            if (cmd.Kind == CombatCommandKind.WorkMove)
            {
                return false; // 工作赶路的停滞看门狗在热更层（按剩余路线长度）。
            }
            // 路径受阻判定（Demo TrackProgressAndMaybeGiveUp）：按剩余路线长度每隔 ProgressInterval 秒核对一次。
            cmd.ProgTimer -= dt;
            if (cmd.ProgTimer > 0f)
            {
                return false;
            }
            cmd.ProgTimer = d.Config.ProgressInterval;
            float distNow = (float)RemainingRouteLength(ref d, i);
            bool progressed = cmd.HasLast == 0 || (cmd.LastDist - distNow) >= d.Config.MinProgress || distNow <= cmd.Arrive;
            cmd.LastDist = distNow;
            cmd.HasLast = 1;
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

        /// <summary>突袭者没有目标时沿路线走向目标点（到了就停；路线失败则原地待命，计数由寻路内核给出）。</summary>
        private static void RaiderRouteMove(ref CombatData d, int i, float speed, float dt)
        {
            double2 pos = d.Pos[i];
            double2 home = d.Home[i];
            var st = (CombatNavState)d.NavSt[i];
            if (st == CombatNavState.None || st == CombatNavState.NeedRoute)
            {
                if (math.distancesq(pos, home) <= 0.25)
                {
                    return;
                }
                EmitNavRequest(ref d, i, CellOf(home), (byte)NavRequestFlags.AllowPartial);
                return;
            }
            if (st != CombatNavState.Following)
            {
                return;
            }
            if (d.RouteIdx[i] >= d.RouteLen[i] && math.distancesq(pos, d.RouteEnd[i]) <= 0.25)
            {
                return;
            }
            FollowRoute(ref d, i, speed, dt);
        }

        /// <summary>
        /// 分离（DEBT-FG0ARCH03-03）：同阵营、会移动的单位互相重叠时各自推开一半重叠量，每步至多 移动速度 × dt × SeparationFactor。
        /// 只看这一步开始时的位置（Prev），与遍历顺序无关；完全重合时按两者 ID 派生的固定方向推开。推开也受格网碰撞约束。
        /// </summary>
        private static void Separate(ref CombatData d, ref CombatGrid grid, int i, float speed, float dt)
        {
            if (d.Config.NavEnabled == 0 || d.Config.SeparationFactor <= 0f || d.Has(i, CombatUnitFlags.Possessed))
            {
                return;
            }
            double2 pi = d.Prev[i];
            float ri = d.Radius[i];
            double reach = ri + grid.MaxRadius;
            int x0 = grid.CellOf(pi.x - reach);
            int x1 = grid.CellOf(pi.x + reach);
            int y0 = grid.CellOf(pi.y - reach);
            int y1 = grid.CellOf(pi.y + reach);
            byte faction = d.Faction[i];
            double2 push = double2.zero;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int k = start; k < start + count; k++)
                    {
                        int j = grid.Slots[k];
                        if (j == i || d.Faction[j] != faction || !d.IsAlive(j))
                        {
                            continue;
                        }
                        byte bj = d.Behavior[j];
                        if (bj != (byte)CombatBehavior.Raider && bj != (byte)CombatBehavior.Commanded && bj != (byte)CombatBehavior.AutoEngage)
                        {
                            continue;
                        }
                        double2 dv = pi - d.Prev[j];
                        double dist = math.length(dv);
                        double min = ri + d.Radius[j];
                        if (dist >= min)
                        {
                            continue;
                        }
                        double2 dir;
                        if (dist > 1e-6)
                        {
                            dir = dv / dist;
                        }
                        else
                        {
                            int a = math.min(d.Id[i], d.Id[j]);
                            int b = math.max(d.Id[i], d.Id[j]);
                            double ang = (math.hash(new int2(a, b)) & 0xFFFF) / 65536.0 * 6.283185307179586;
                            dir = new double2(math.cos(ang), math.sin(ang)) * (d.Id[i] < d.Id[j] ? 1.0 : -1.0);
                        }
                        push += dir * ((min - dist) * 0.5);
                    }
                }
            }
            double len = math.length(push);
            if (len < 1e-9)
            {
                return;
            }
            double maxStep = speed * dt * d.Config.SeparationFactor;
            if (len > maxStep)
            {
                push *= maxStep / len;
            }
            double2 from = d.Pos[i];
            d.Pos[i] = MoveCollide(ref d, i, from, from + push);
        }

        /// <summary>路线池里作废的路点过半时按槽位顺序整理（确定性）。</summary>
        public static void MaybeCompactRoutes(ref CombatData d)
        {
            CombatScalars s = d.Scalars[0];
            if (s.RouteGarbage < 1024 || s.RouteGarbage * 2 < d.RoutePts.Length)
            {
                return;
            }
            var live = new NativeList<int2>(math.max(16, d.RoutePts.Length - s.RouteGarbage), Allocator.Temp);
            for (int i = 0; i < d.Count; i++)
            {
                int off = d.RouteOff[i];
                int len = d.RouteLen[i];
                d.RouteOff[i] = live.Length;
                for (int k = 0; k < len; k++)
                {
                    live.Add(d.RoutePts[off + k]);
                }
            }
            d.RoutePts.Clear();
            for (int k = 0; k < live.Length; k++)
            {
                d.RoutePts.Add(live[k]);
            }
            live.Dispose();
            s.RouteGarbage = 0;
            d.Scalars[0] = s;
        }

        /// <summary>地形变化后：沿路线走的单位，剩余路线上出现了走不了的格子就重新要路线（从当前位置）。返回失效的单位数。</summary>
        public static int InvalidateBlockedRoutes(ref CombatData d)
        {
            int n = 0;
            for (int i = 0; i < d.Count; i++)
            {
                if (!d.IsAlive(i) || d.NavSt[i] != (byte)CombatNavState.Following)
                {
                    continue;
                }
                int len = d.RouteLen[i];
                int idx = d.RouteIdx[i];
                int cls = ClassOf(ref d, i);
                int2 prev = CellOf(d.Pos[i]);
                bool clear = true;
                bool inside = !Walkable(ref d, prev, cls);
                for (int k = idx; k < len && clear; k++)
                {
                    int2 q = d.RoutePts[d.RouteOff[i] + k];
                    if (!(inside && k == idx) && !NavSearch.LineClear(ref d.Nav, prev, q, cls))
                    {
                        clear = false;
                    }
                    prev = q;
                }
                if (clear)
                {
                    continue;
                }
                DropRoute(ref d, i);
                d.NavSt[i] = (byte)CombatNavState.NeedRoute;
                n++;
            }
            return n;
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
