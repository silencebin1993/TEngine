using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG8-EXP-02（FG08 FGR-EXP-010 行军、FGR-EXP-011 远程指挥与信号覆盖、FGR-EXP-012 远征队状态；FG01 FGR-SIG-053）：远征队在星球表面上的行军。
    ///
    /// 做法（接在 FG0-ARCH-06 已有的无缝世界机制上，不另起模拟循环）：
    /// - 派遣（<see cref="ExpeditionDepartureService.TryDepart"/>）不再立即载入远征地点：成员留在家园所在的星球表面上，是家园战斗内核里的真实单位，
    ///   出发时给每台下一条“移动到目的地锚点（<see cref="ExpeditionSiteAnchors"/>）”的内核命令——路线是每台自己的层级寻路（FGR-ARC-015），
    ///   沿地形绕开悬崖 / 水 / 建筑；途中与醒着的巡逻、突袭真实交战（内核里，按 AI 教义）；镜头在不在都一样推进（FGR-BASE-021）。
    /// - 行进速度取最慢的那台（FGR-EXP-010 初值：搬运轮式 6、维修悬浮 5、战斗履带 3.5 格/秒，入表 expedition.march.speed.*）：出发时把每台的内核速度都设成这个值（随内核快照存档）。
    /// - 全部存活成员走到锚点附近（expedition.march.arrive_radius）= 到达：步末走到达事务——先存“到达前档”，再载入远征地点（机器随之转入地点）；
    ///   中途出错就整体回滚到到达前档（不瞬移回家，不丢机器）。没进去时队伍在集结点等候并说明原因，最后一条命令仍是玩家下的“前进”：
    ///   每 expedition.march.arrival_retry_seconds 游戏秒按它自动重试进入（不替玩家改命令；集结点通常在核心覆盖外，玩家此刻下不了命令，FG00 B11 不软锁）。
    ///   到达不抢镜头：只有玩家正在“跟随远征队”时镜头才跟进地点，否则发到达通知。
    /// - 远程命令（前进 / 待命 / 返回）只在全部存活成员都能收到远程命令时生效（与核心连通的覆盖里、不在静默夜，<see cref="SignalCoverageService.CanReceiveCommand"/>）；
    ///   覆盖外被拒并写明原因，队伍继续执行最后一条命令（FGR-SIG-053）。没有任何自动返程之类玩家没下的命令（FGR-BASE-020）。
    /// - 返程走到家园核心附近 = 已返回：离家报告按“返回”结算、货舱按远征卸货规则卸货（StandingRuleService.OnExpeditionReturned）、存档。
    ///   从远征地点撤离仍是 FG8-EXP-01 的瞬间结算（走回家园与到家结算归 FG8-EXP-05，DEBT-FG8EXP02-04）。
    /// - 行军中的成员是“远征中”：不接家园工作单与规则派遣（<see cref="IsMarchMember(int)"/>，HomeValleyController.IsMachineTakenByPlayer / MachineRoster.IsHome），名册显示“远征中”。
    ///
    /// 开销：每步 O(成员数)（到达判定）；每游戏秒一次 O(成员数) 的覆盖 / 交战 / 速度对账与 O(据点 + 巡逻 + 突袭队伍) 的遇敌提示——全部只看步序号，与观察无关。
    /// 逐单位的移动、寻路、交战都在 AOT 内核里（Main/Sim）。
    /// </summary>
    public static class ExpeditionMarchService
    {
        public const int PhaseNone = 0;
        public const int PhaseOutbound = 1;
        public const int PhaseAtSite = 2;
        public const int PhaseReturning = 3;
        public const int PhaseReturned = 4;

        public const int HaltNone = 0;
        public const int HaltRouteTruncated = 1;
        public const int HaltArrivalFailed = 2;

        /// <summary>远征命令（FGR-EXP-011）。</summary>
        public enum Command
        {
            Advance = 0,
            Hold = 1,
            Return = 2,
        }

        /// <summary>远征队五态（FGR-EXP-012）。</summary>
        public enum TeamStatus
        {
            Standby = 0,
            Marching = 1,
            Engaged = 2,
            Returning = 3,
            Returned = 4,
        }

        private const float UnitArriveRadius = 1.5f;
        /// <summary>
        /// FG9-NAR-01：远征队到达远征地点（参数：地点 ID）。叙事框架据此投放“第一次到达这个地点”的剧情节点。
        /// 审查修复（P1）：在载入地点<b>之前</b>发出，这样地点 Enter 里的自动存档已经带着这条资料（中途退出读档不会缺节点）；
        /// 载入失败时紧接着发 <see cref="SiteArrivalRevertedEvent"/> 撤回。
        /// </summary>
        public const string SiteArrivedEvent = "Expedition.SiteArrived";
        /// <summary>FG9-NAR-01 审查修复：到达事务在载入地点时失败（队伍退回途中），撤回刚才 <see cref="SiteArrivedEvent"/> 投放的内容（参数：地点 ID）。</summary>
        public const string SiteArrivalRevertedEvent = "Expedition.SiteArrivalReverted";
        private const float GuardArriveRadius = 0.3f;

        // ── 自检读点 ──
        public static int BeginCount { get; private set; }
        public static int ArriveCount { get; private set; }
        public static int ArrivalFailCount { get; private set; }
        public static int ArrivalRollbackCount { get; private set; }
        public static int ReturnHomeCount { get; private set; }
        public static int WipedCount { get; private set; }
        public static int CommandCount { get; private set; }
        public static int CommandRejectCount { get; private set; }
        public static int EncounterCueCount { get; private set; }
        public static int HazardCueCount { get; private set; }
        /// <summary>FG8-GEN-02：“进入敌方领地”提醒次数（自检读）。</summary>
        public static int TerritoryCueCount { get; private set; }
        public static int EdgeCueCount { get; private set; }
        public static int CoverageCueCount { get; private set; }
        public static int HaltCueCount { get; private set; }
        /// <summary>FG8-E2E-01：AI 教义自卫交战下达的内核攻击命令次数（自检读）。</summary>
        public static int SelfDefenseCount { get; private set; }
        public static string LastRejectReason { get; private set; }

        /// <summary>
        /// 自检故障注入：到达事务里“地点已载入、事务还没提交”这一刻调用（抛异常 = 模拟载入中途失败，验证整体回滚）。
        /// 默认为 null；只有自检会赋值（正式代码没有赋值点），不改变任何正式行为。
        /// </summary>
        public static Action ArrivalFaultInjectionForTests;

        /// <summary>自检故障注入：出发时行军已半开（记录已写、内核命令已下）这一刻调用（抛异常 = 验证出发事务整体回滚）。默认 null，只有自检赋值。</summary>
        public static Action BeginFaultInjectionForTests;

        private static readonly List<int> AliveScratch = new List<int>(16);
        private static readonly List<double2> RouteScratch = new List<double2>(64);
        private static ExpeditionMarchState _arrivalQueuedFor;

        // ─────────────────────────────── 数值（入表 fg.TbHomeTuning，fgdata_expedition.py）───────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;

        /// <summary>底盘的行军速度（格/秒）：搬运轮式 6、维修悬浮 5、战斗履带 3.5（FG08 FGR-EXP-010 初值）；系统占位机（应急搬运机等）按最慢的 3.5。</summary>
        public static float ChassisSpeed(string machineChassisId)
        {
            switch (ChassisCatalog.ResolveArchetype(machineChassisId))
            {
                case ChassisCatalog.ChassisWheelId:
                    return Tuning("expedition.march.speed.wheel", 6f);
                case ChassisCatalog.ChassisHoverId:
                    return Tuning("expedition.march.speed.hover", 5f);
                default:
                    return Tuning("expedition.march.speed.track", 3.5f);
            }
        }

        /// <summary>一组机器的行军速度 = 其中存活的最慢那台；没有存活的返回 0。</summary>
        public static float MarchSpeed(IReadOnlyList<int> logicIds)
        {
            float speed = float.MaxValue;
            if (logicIds != null)
            {
                for (int i = 0; i < logicIds.Count; i++)
                {
                    if (MachineRegistry.TryGetRecord(logicIds[i], out MachineRecord rec) && rec != null && rec.IsAlive)
                    {
                        speed = Math.Min(speed, ChassisSpeed(rec.ChassisId));
                    }
                }
            }
            return speed == float.MaxValue ? 0f : speed;
        }

        /// <summary>全部存活成员走到锚点这么近就算到达（格）。</summary>
        public static float ArriveRadius => Tuning("expedition.march.arrive_radius", 10f);

        /// <summary>返程走到归还核心这么近就算回到家园（格）。</summary>
        public static float HomeArriveRadius => Tuning("expedition.march.home_radius", 24f);

        /// <summary>成员这么近有敌对单位 = 交战中（格）。</summary>
        public static float EngageRadius => Tuning("expedition.march.engage_radius", 12f);

        /// <summary>途中敌方据点 / 巡逻 / 突袭队伍进入这么近就提示一次（格）。</summary>
        public static float EncounterRadius => Tuning("expedition.encounter.radius_cells", 40f);

        /// <summary>到达失败后，队伍照最后的“前进”隔这么久（游戏秒）自动重试进入。</summary>
        public static float ArrivalRetrySeconds => Tuning("expedition.march.arrival_retry_seconds", 15f);

        /// <summary>
        /// 到达 / 回家判定半径（格）：表里的半径与编队站位外沿（最远那台的站位 + 单位到达误差）取大——人多时站位排得更远，
        /// 半径跟着放大，不会因为站位超出半径而永远“到不了”、被误判为截断（B12）。O(成员数)。
        /// </summary>
        public static float GoalRadius(int phase, int alive)
        {
            float extent = 0f;
            for (int i = 0; i < alive; i++)
            {
                extent = Math.Max(extent, FormationOffset(i).magnitude);
            }
            return Math.Max(phase == PhaseReturning ? HomeArriveRadius : ArriveRadius, extent + UnitArriveRadius + 1f);
        }

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static ExpeditionMarchState Current(CampaignState s) => s?.Grid?.ExpeditionTeam?.March;

        /// <summary>远征队在星球上走着（去程或返程，含待命 / 截断停下）。</summary>
        public static bool IsMarching(CampaignState s) => Current(s) is { Phase: PhaseOutbound or PhaseReturning };

        /// <summary>
        /// 远征在外的统一判据（FGR-SIG-011 远征途中不能改信号核、“同一时刻一支远征队”、离家报告）：星球上行军中，或远征地点已载入。
        /// 所有读“远征在外”的地方都走这里——行军阶段 <see cref="WorldSimulation.ActiveExpedition"/> 为 null，只看它会漏判。
        /// </summary>
        public static bool IsAway(CampaignState s) => IsMarching(s) || WorldSimulation.ActiveExpedition != null;

        /// <summary>星球上行军时的目的地地点；不在行军中返回 null（已进入地点时由 <see cref="WorldSimulation.ActiveExpedition"/> 给出）。</summary>
        public static string AwaySiteId(CampaignState s) => IsMarching(s) ? Current(s).SiteId : null;

        /// <summary>这台机器是不是正在星球上行军的远征队成员（不接家园工作单 / 规则派遣，名册显示“远征中”）。O(成员数)。</summary>
        public static bool IsMarchMember(CampaignState s, int logicId)
        {
            ExpeditionMarchState m = Current(s);
            if (m == null || (m.Phase != PhaseOutbound && m.Phase != PhaseReturning) || m.Members == null)
            {
                return false;
            }
            return Array.IndexOf(m.Members, logicId) >= 0;
        }

        public static bool IsMarchMember(int logicId) => IsMarchMember(CampaignSession.Current, logicId);

        /// <summary>这台机器是不是在外的远征队成员（星球上行军中，或在远征地点里）。名册写远征队状态用。O(成员数)。</summary>
        public static bool IsTeamMember(CampaignState s, int logicId)
        {
            ExpeditionMarchState m = Current(s);
            return m?.Members != null && (m.Phase == PhaseOutbound || m.Phase == PhaseReturning || m.Phase == PhaseAtSite)
                && Array.IndexOf(m.Members, logicId) >= 0;
        }

        /// <summary>行军目的地（去程 = 锚点，返程 = 归还核心）。</summary>
        public static bool TryGetGoal(CampaignState s, out Vector2 goal)
        {
            ExpeditionMarchState m = Current(s);
            goal = default;
            if (m == null)
            {
                return false;
            }
            if (m.Phase == PhaseReturning)
            {
                // FG8-EXP-05：返程去下令那一刻最近的家园 / 带仓库的前哨站（旧档没有 = 归还核心）。
                goal = m.HasReturnGoal ? new Vector2(m.ReturnX, m.ReturnY) : HomeValleyLayout.Core.Position;
                return true;
            }
            return ExpeditionSiteAnchors.TryGet(s, m.SiteId, out goal);
        }

        /// <summary>存活成员（在家园所在星球表面上的）。共享列表，调用方不要留着。O(成员数)。</summary>
        public static IReadOnlyList<int> AlivePlanetMembers(CampaignState s)
        {
            AliveScratch.Clear();
            ExpeditionMarchState m = Current(s);
            if (m?.Members == null)
            {
                return AliveScratch;
            }
            for (int i = 0; i < m.Members.Length; i++)
            {
                if (MachineRegistry.TryGetRecord(m.Members[i], out MachineRecord rec) && rec != null && rec.IsAlive && rec.RegionId == HomeValleyLayout.RegionId)
                {
                    AliveScratch.Add(rec.LogicId);
                }
            }
            return AliveScratch;
        }

        /// <summary>队伍中心（存活成员实时位置的平均；星球格）。行军中才有。</summary>
        public static bool TryTeamCenter(CampaignState s, out Vector2 center, out int alive)
        {
            center = default;
            alive = 0;
            if (!IsMarching(s))
            {
                return false;
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < members.Count; i++)
            {
                Vector2 p;
                if (home == null || !home.TryGetMachinePosition(members[i], out p))
                {
                    if (!MachineRegistry.TryGetRecord(members[i], out MachineRecord rec))
                    {
                        continue;
                    }
                    p = rec.WorldPosition;
                }
                sum += p;
                alive++;
            }
            if (alive == 0)
            {
                return false;
            }
            center = sum / alive;
            return true;
        }

        /// <summary>远征队五态（FGR-EXP-012）：交战优先；待命 = 下了“待命”、路线被截断、到达失败、或在地点里没有交战；没有远征在外也是待命。</summary>
        public static TeamStatus StatusOf(CampaignState s)
        {
            ExpeditionMarchState m = Current(s);
            switch (m?.Phase ?? PhaseNone)
            {
                case PhaseOutbound:
                    return m.Engaged ? TeamStatus.Engaged : m.Command == (int)Command.Hold || m.Halted != HaltNone ? TeamStatus.Standby : TeamStatus.Marching;
                case PhaseReturning:
                    return m.Engaged ? TeamStatus.Engaged : m.Command == (int)Command.Hold || m.Halted != HaltNone ? TeamStatus.Standby : TeamStatus.Returning;
                case PhaseAtSite:
                    return m.Engaged ? TeamStatus.Engaged : TeamStatus.Standby;
                case PhaseReturned:
                    return TeamStatus.Returned;
                default:
                    return TeamStatus.Standby;
            }
        }

        public static string StatusTextKey(TeamStatus status) => ExpeditionTextKeys.StatusKey(status);

        /// <summary>
        /// 预计到达时间（游戏秒）：存活成员里剩余路线最长的那台 ÷ 行军速度。还在等寻路结果、待命、停下或不在行军中时返回 -1（界面写“规划中 / 待命”）。
        /// O(成员数)，每台读内核一次。
        /// </summary>
        public static double EtaSeconds(CampaignState s)
        {
            ExpeditionMarchState m = Current(s);
            if (m == null || (m.Phase != PhaseOutbound && m.Phase != PhaseReturning) || m.Command == (int)Command.Hold || m.Halted != HaltNone || m.Speed <= 0f)
            {
                return -1;
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (home == null || home.IsDisposed)
            {
                return -1;
            }
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            double longest = 0;
            for (int i = 0; i < members.Count; i++)
            {
                if (!home.TryGetMachineUnit(members[i], out int unit))
                {
                    return -1;
                }
                double rem = home.RemainingRoute(unit);
                if (rem < 0)
                {
                    // 没有在沿路线走：已经走到（没有命令）就不算，还在等路线就整体按“规划中”。
                    if (home.TryGetCommand(unit, out CombatCommand cmd) && cmd.Kind != CombatCommandKind.None)
                    {
                        return -1;
                    }
                    continue;
                }
                longest = Math.Max(longest, rem);
            }
            return longest / m.Speed;
        }

        /// <summary>剩余路线（剩余最长那台的内核路线，星球格；没有返回 0）。战略地图画路线用。</summary>
        public static int CopyRoute(CampaignState s, List<Vector2> into)
        {
            into.Clear();
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (!IsMarching(s) || home == null || home.IsDisposed)
            {
                return 0;
            }
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            int best = 0;
            double bestRem = -1;
            for (int i = 0; i < members.Count; i++)
            {
                if (home.TryGetMachineUnit(members[i], out int unit))
                {
                    double rem = home.RemainingRoute(unit);
                    if (rem > bestRem)
                    {
                        bestRem = rem;
                        best = unit;
                    }
                }
            }
            if (bestRem < 0 || home.CopyRoute(best, RouteScratch) == 0)
            {
                return 0;
            }
            if (home.TryGetUnitPosition(best, out double2 at))
            {
                into.Add(new Vector2((float)at.x, (float)at.y));
            }
            for (int i = 0; i < RouteScratch.Count; i++)
            {
                into.Add(new Vector2((float)RouteScratch[i].x, (float)RouteScratch[i].y));
            }
            return into.Count;
        }

        /// <summary>已走到集结点（锚点附近）的存活成员数（界面“已到达 k / n 台”）。</summary>
        public static int ArrivedCount(CampaignState s, out int alive)
        {
            alive = 0;
            int near = 0;
            if (!IsMarching(s) || !TryGetGoal(s, out Vector2 goal))
            {
                return 0;
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            float r = GoalRadius(Current(s).Phase, members.Count);
            for (int i = 0; i < members.Count; i++)
            {
                alive++;
                if (home != null && home.TryGetMachinePosition(members[i], out Vector2 p) && (p - goal).sqrMagnitude <= r * r)
                {
                    near++;
                }
            }
            return near;
        }

        // ─────────────────────────────── 命令 ───────────────────────────────

        /// <summary>
        /// FGR-EXP-011 / FGR-SIG-053：现在能不能给远征队下远程命令。行军中（去程 / 返程）、不在静默夜、全部存活成员都在与核心连通的覆盖里才行；
        /// 每台下令这一刻现采样（<see cref="SignalCoverageService.CanReceiveCommand"/>，与编队命令同一口径）。在远征地点里的命令走地点自己的编队命令。
        /// </summary>
        public static bool CanCommand(CampaignState s, out string reason)
        {
            reason = null;
            ExpeditionMarchState m = Current(s);
            if (m == null || m.Phase == PhaseNone || m.Phase == PhaseReturned)
            {
                reason = "no-march";
                return false;
            }
            if (m.Phase == PhaseAtSite)
            {
                reason = "at-site";
                return false;
            }
            if (Env.SilentNightService.IsActiveNow())
            {
                reason = "silent-night";
                return false;
            }
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            if (members.Count == 0)
            {
                reason = "no-members";
                return false;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (!SignalCoverageService.CanReceiveCommand(members[i]))
                {
                    reason = "out-of-coverage";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 下远征命令（FGR-EXP-011）：前进（往目的地锚点）、待命（每台守在当前位置）、返回（往归还核心）。覆盖外 / 静默夜被拒（<paramref name="failure"/> 写原因码），
        /// 此时队伍继续执行最后一条命令（<see cref="ExpeditionMarchState.Command"/> 不变）。成功时清掉“截断 / 到达失败”的停下状态。
        /// </summary>
        public static bool IssueCommand(CampaignState s, Command cmd, out string failure)
        {
            if (!CanCommand(s, out failure))
            {
                CommandRejectCount++;
                LastRejectReason = failure;
                return false;
            }
            ExpeditionMarchState m = Current(s);
            if (cmd == Command.Advance && !ExpeditionSiteAnchors.IsResolved(s, m.SiteId))
            {
                failure = "destination-pending";
                CommandRejectCount++;
                LastRejectReason = failure;
                return false;
            }
            int phase = cmd == Command.Advance ? PhaseOutbound : cmd == Command.Return ? PhaseReturning : m.Phase;
            if (phase != m.Phase)
            {
                m.Phase = phase;
                m.PhaseTick = GameClock.Ticks;
            }
            if (cmd == Command.Return)
            {
                ChooseReturnGoal(s, m);
            }
            m.Command = (int)cmd;
            m.Halted = HaltNone;
            m.ArrivalFailReason = string.Empty; // 玩家重新下了令：之后再没进去照常提示
            m.OrdersPending = !IssueOrders(s, m);
            CommandCount++;
            return true;
        }

        /// <summary>按 <see cref="ExpeditionMarchState.Command"/> 给存活成员下内核命令（并把速度设成行军速度）。有成员还没进家园内核（刚出厂）返回 false，下一步补。</summary>
        private static bool IssueOrders(CampaignState s, ExpeditionMarchState m)
        {
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (home == null || home.IsDisposed)
            {
                return false;
            }
            Command cmd = (Command)m.Command;
            Vector2 goal = default;
            if (cmd != Command.Hold && !TryGetGoal(s, out goal))
            {
                return false;
            }
            bool all = true;
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            for (int k = 0; k < members.Count; k++)
            {
                if (!home.TryGetMachineMarker(members[k], out HomeValleyMachineMarker marker))
                {
                    all = false;
                    continue;
                }
                marker.CancelCommandMove();
                home.SetUnitSpeed(marker.UnitId, m.Speed);
                bool ok = cmd == Command.Hold
                    ? home.IssueCommand(marker.UnitId, CombatCommandKind.Guard, new Vector2(float.NaN, float.NaN), 0, GuardArriveRadius,
                        HomeValleyCombatTargets.EngageRange, CombatSite.MachineAttackInterval, GameClock.Paused)
                    : home.IssueCommand(marker.UnitId, CombatCommandKind.Move, goal + FormationOffset(k), 0, UnitArriveRadius,
                        HomeValleyCombatTargets.EngageRange, CombatSite.MachineAttackInterval, GameClock.Paused);
                all &= ok;
            }
            return all;
        }

        /// <summary>编队站位（相对目的地，星球格）：三台一排、行距 2.5 格（与进入地点时的站位同一思路），每台各自寻路。</summary>
        private static Vector2 FormationOffset(int i) => new Vector2((i % 3 - 1) * 2.5f, -(i / 3) * 2.5f);

        // ─────────────────────────────── 出发 ───────────────────────────────

        /// <summary>
        /// 出发（<see cref="ExpeditionDepartureService.TryDepart"/> 的事务里调用；失败时由调用方整体回滚）：记行军、给存活成员下“前进”的内核命令、设行军速度。
        /// 出发命令是在家园下达的（成员此刻都在核心旁），不经覆盖判定；之后的命令都按覆盖判定（FGR-SIG-053）。
        /// 失败（已有远征在外 / 家园没在运行 / 锚点未定 / 没有存活成员）返回 false，不改任何状态。
        /// </summary>
        public static bool Begin(CampaignState s, string siteId, IReadOnlyList<int> manifest, out string failure)
        {
            failure = null;
            if (s?.Grid?.ExpeditionTeam == null)
            {
                failure = "no-campaign";
                return false;
            }
            if (IsAway(s))
            {
                failure = "expedition-underway";
                return false;
            }
            if (WorldSimulation.Home == null || !WorldSimulation.Home.IsLoaded || CombatSites.Get(HomeValleyLayout.RegionId) == null)
            {
                failure = "home-not-loaded";
                return false;
            }
            if (!ExpeditionSiteAnchors.IsResolved(s, siteId))
            {
                failure = ExpeditionSiteAnchors.IsFailed(s, siteId) ? "destination-unreachable" : "destination-pending";
                return false;
            }
            var ids = new List<int>(manifest?.Count ?? 0);
            if (manifest != null)
            {
                foreach (int id in manifest)
                {
                    if (MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null && rec.IsAlive && rec.RegionId == HomeValleyLayout.RegionId && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            if (ids.Count == 0)
            {
                failure = "no-members";
                return false;
            }
            ids.Sort();
            ExpeditionMarchState m = s.Grid.ExpeditionTeam.March ??= new ExpeditionMarchState();
            m.Phase = PhaseOutbound;
            m.SiteId = siteId;
            m.Members = ids.ToArray();
            m.Speed = MarchSpeed(ids);
            m.Command = (int)Command.Advance;
            m.DepartTick = GameClock.Ticks;
            m.PhaseTick = GameClock.Ticks;
            m.InCoverage = true;
            m.EdgeWarned = false;
            m.Engaged = false;
            m.Halted = HaltNone;
            m.ArrivalFailReason = string.Empty;
            m.ArrivalRetryTick = 0;
            m.HazardZoneId = string.Empty;
            m.EncounteredIds = Array.Empty<string>();
            m.VisitedSite = false; // FG8-EXP-05：这一趟还没进过地点
            m.HasReturnGoal = false;
            m.OrdersPending = !IssueOrders(s, m);
            BeginFaultInjectionForTests?.Invoke();
            BeginCount++;
            Log.Info($"[ExpeditionMarchService] 远征队出发行军：{ids.Count} 台 → {siteId}，速度 {m.Speed:0.0} 格/秒（最慢的那台），锚点路线 {ExpeditionSiteAnchors.RouteLength(s, siteId):0} 格{(m.OrdersPending ? "（有成员还没进家园内核，下一步补下命令）" : string.Empty)}。");
            return true;
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>
        /// 每个固定模拟步（<see cref="WorldSimulation"/> 在家园与行进队伍之后调用；只看步序号，与镜头、帧率无关）：
        /// 到达 / 回家判定（每步），覆盖 / 交战 / 遇敌 / 危害 / 截断 / 速度对账（每游戏秒）。
        /// </summary>
        public static void WorldStep(CampaignState s, float dt)
        {
            if (s?.Grid?.ExpeditionTeam == null)
            {
                return;
            }
            ExpeditionMarchState m = s.Grid.ExpeditionTeam.March;
            if (m == null)
            {
                return;
            }
            Reconcile(s, m);
            bool everySecond = GameClock.Ticks % Math.Max(1, GameClock.StepHz) == 0;
            switch (m.Phase)
            {
                case PhaseOutbound:
                case PhaseReturning:
                    StepOnPlanet(s, m, everySecond);
                    break;
                case PhaseAtSite:
                    if (everySecond)
                    {
                        m.Engaged = AnyEngagedAtSite(m);
                    }
                    break;
            }
        }

        /// <summary>
        /// 与世界对齐（O(1)）：远征地点在运行但记录不是“在地点”（旧档读进来、自检直接载入地点）→ 记为在地点；
        /// 记录是“在地点”但地点已卸载（撤离 / 放弃 / 卸载）→ 有成员活着回到家园 = 已返回，否则 = 没有远征在外。
        /// </summary>
        private static void Reconcile(CampaignState s, ExpeditionMarchState m)
        {
            IWorldSite exp = WorldSimulation.ActiveExpedition;
            if (exp != null && m.Phase != PhaseAtSite)
            {
                AdoptAtSite(m, exp.SiteId);
            }
            else if (exp == null && m.Phase == PhaseAtSite)
            {
                bool anyHome = false;
                foreach (int id in m.Members ?? Array.Empty<int>())
                {
                    anyHome |= MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null && rec.IsAlive && rec.RegionId == HomeValleyLayout.RegionId;
                }
                SetPhase(m, anyHome ? PhaseReturned : PhaseNone);
            }
        }

        private static void AdoptAtSite(ExpeditionMarchState m, string siteId)
        {
            var ids = new List<int>();
            foreach (MachineRecord rec in MachineRegistry.AllRecords)
            {
                if (rec != null && rec.IsAlive && rec.RegionId == siteId)
                {
                    ids.Add(rec.LogicId);
                }
            }
            ids.Sort();
            m.SiteId = siteId;
            if (ids.Count > 0 || m.Members == null || m.Members.Length == 0)
            {
                m.Members = ids.ToArray();
            }
            SetPhase(m, PhaseAtSite);
            m.Halted = HaltNone;
            m.VisitedSite = true; // FG8-EXP-05：结算只在进过地点时列地点里留下的东西
        }

        private static void SetPhase(ExpeditionMarchState m, int phase)
        {
            if (m.Phase != phase)
            {
                m.Phase = phase;
                m.PhaseTick = GameClock.Ticks;
            }
            if (phase != PhaseOutbound && phase != PhaseReturning)
            {
                m.Engaged = false;
            }
        }

        private static void StepOnPlanet(CampaignState s, ExpeditionMarchState m, bool everySecond)
        {
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (home == null || home.IsDisposed)
            {
                return;
            }
            bool advancing = m.Command == (int)Command.Advance && m.Phase == PhaseOutbound;
            bool returning = m.Command == (int)Command.Return && m.Phase == PhaseReturning;
            // 到达失败后（FG00 B11）：最后一条命令仍是玩家下的“前进”，到点就照它重试——重新下一遍“走到站位”的内核命令（走散的回到集结点），
            // 全部到位后照常走到达事务。不替玩家改命令（FGR-BASE-020），也不经覆盖判定（集结点通常在核心覆盖外）。只看步序号，与观察无关。
            if (advancing && m.Halted == HaltArrivalFailed && GameClock.Ticks >= m.ArrivalRetryTick)
            {
                m.Halted = HaltNone;
                m.OrdersPending = true;
            }
            if (m.OrdersPending)
            {
                m.OrdersPending = !IssueOrders(s, m);
            }
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            int alive = members.Count;
            if (alive == 0)
            {
                OnWipedOnPlanet(s, m);
                return;
            }
            bool hasGoal = TryGetGoal(s, out Vector2 goal);
            float r = GoalRadius(m.Phase, alive);
            int near = 0;
            int idle = 0;
            for (int i = 0; i < alive; i++)
            {
                int id = members[i];
                if (hasGoal && home.TryGetMachinePosition(id, out Vector2 p) && (p - goal).sqrMagnitude <= r * r)
                {
                    near++;
                }
                if (home.TryGetMachineUnit(id, out int unit) && (!home.TryGetCommand(unit, out CombatCommand cmd) || cmd.Kind == CombatCommandKind.None))
                {
                    idle++;
                }
            }
            if (hasGoal && near == alive && !m.OrdersPending)
            {
                if (advancing && m.Halted == HaltNone)
                {
                    QueueArrival(s, m);
                    return;
                }
                if (returning)
                {
                    ReturnHome(s, m, home, members);
                    return;
                }
            }
            if (!everySecond)
            {
                return;
            }
            // 每游戏秒：速度对账（行军速度 = 存活成员里最慢的那台：最慢的阵亡后队伍提速；刚出厂 / 读档后的单位以行军速度走）、覆盖、交战、遇敌 / 危害、截断。
            float speed = MarchSpeed(members);
            if (speed > 0f && Math.Abs(speed - m.Speed) > 0.001f)
            {
                Log.Info($"[ExpeditionMarchService] 远征队行军速度 {m.Speed:0.0} → {speed:0.0} 格/秒（按存活成员里最慢的那台）。");
                m.Speed = speed;
            }
            for (int i = 0; i < alive; i++)
            {
                if (home.TryGetMachineUnit(members[i], out int unit) && Math.Abs(home.UnitSpeed(unit) - m.Speed) > 0.001f && m.Speed > 0f)
                {
                    home.SetUnitSpeed(unit, m.Speed);
                }
            }
            if (returning && m.HasReturnGoal && !DepotStillThere(s, new Vector2(m.ReturnX, m.ReturnY)))
            {
                // FG8-EXP-05：要去的那座仓库途中被毁 / 拆了：改去下一处最近的家园 / 带仓库的前哨站（不替玩家改命令，命令仍是“返回”）。
                ChooseReturnGoal(s, m);
                m.OrdersPending = true;
            }
            EvaluateCoverage(m, members);
            bool doctrineBusy = MaintainSelfDefense(home, m, members);
            m.Engaged = AnyEngaged(home, members);
            if (TryTeamCenter(s, out Vector2 center, out _))
            {
                EvaluateEncounters(s, m, center);
            }
            // 截断：在执行“前进 / 返回”、没人还有命令在跑、又没全部走到 → 路线被截断（寻路只到最近可达点 / 被挡），原地停下等新命令。
            // 复审修复：成员正被自卫教义占用（交战中 / 打完走回驻防点）时命令为空是正常的，不算截断。
            if ((advancing || returning) && !m.OrdersPending && !doctrineBusy && idle == alive && near < alive && m.Halted == HaltNone)
            {
                m.Halted = HaltRouteTruncated;
                HaltCueCount++;
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ExpeditionMarchHalted, center,
                    GameText.Format("exp.cue.halted_detail", alive - near));
                Log.Warning($"[ExpeditionMarchService] 远征队路线被截断：{alive - near} 台没能走到目的地，原地待命（等新命令）。");
            }
        }

        /// <summary>
        /// FG8-E2E-01（FGJ-M8 实跑发现：星球上的远征队对据点驻军 / 精英残部 / 巡逻完全不还手——家园内核的自动交战只认 Demo 的低威胁训练靶）：
        /// AI 教义的自卫交战，复用内核守点教义（FG6-DEF-05 StepGuard：有武器才开火、射程外只在守点半径内靠近、半径内没敌人回驻防点）。
        /// 复审修复（P1）：不再由热更层直接下内核攻击命令——没有攻击出口的成员会接到一条永远完不成的攻击、追击没有边界、“待命”被顶掉、命令栏刷拒绝行。现在每游戏秒：
        /// 被接入（直控）或当前装配打不出伤害（<see cref="CombatSite.CanMachineAttack"/>）的成员不交给教义（清掉守点，“待命”的守备命令原样保留）；
        /// 正在执行命令（玩家的移动 / 攻击、行军的前进 / 返回）的成员清掉守点、听命令；没有命令的成员（走到了站位、被截断停下）驻防点 = 停下的位置（已在守点就不改），
        /// 半径 expedition.march.engage_radius；“待命”的成员驻防点 = 守备点：守备点半径内出现敌人时让出守备命令交给教义，敌人没了按原守备点恢复“待命”。
        /// 只看步序号（每游戏秒），与观察无关；O(成员数) 次 AOT 查询。不改行军命令（<see cref="ExpeditionMarchState.Command"/>）。
        /// 返回：有成员正被教义占用（驻防点半径内有敌人，或打完还在走回驻防点）——这时不判“路线被截断”。
        /// </summary>
        private static bool MaintainSelfDefense(CombatSite home, ExpeditionMarchState m, IReadOnlyList<int> members)
        {
            float radius = EngageRadius;
            bool hold = m.Command == (int)Command.Hold;
            bool busy = false;
            for (int i = 0; i < members.Count; i++)
            {
                int id = members[i];
                if (!home.TryGetMachineUnit(id, out int unit) || !home.TryGetMachinePosition(id, out Vector2 p))
                {
                    continue;
                }
                home.TryGetMachineGuard(id, out Vector2 post, out float guardRadius);
                if (home.IsMachinePossessed(id) || !home.CanMachineAttack(id))
                {
                    if (guardRadius > 0f)
                    {
                        home.SetMachineGuard(id, default, 0f);
                    }
                    continue;
                }
                bool hasCommand = home.TryGetCommand(unit, out CombatCommand cmd) && cmd.Kind != CombatCommandKind.None;
                if (hasCommand && cmd.Kind != CombatCommandKind.Guard)
                {
                    if (guardRadius > 0f)
                    {
                        home.SetMachineGuard(id, default, 0f); // 听命令；驻防点会过时，命令做完后按停下的位置重设
                    }
                    continue;
                }
                if (hasCommand)
                {
                    // “待命”：守备点半径内有敌人 → 让出守备命令，交给内核守点教义（同一守备点、同一半径）。
                    var guardPost = new Vector2((float)cmd.Pos.x, (float)cmd.Pos.y);
                    if (home.FindNearestHostile(guardPost, radius) > 0)
                    {
                        home.SetMachineGuard(id, guardPost, radius);
                        home.ClearCommand(unit);
                        SelfDefenseCount++;
                        busy = true;
                    }
                    continue;
                }
                if (guardRadius <= 0f)
                {
                    home.SetMachineGuard(id, p, radius);
                    post = p;
                }
                if (home.FindNearestHostile(post, radius) > 0)
                {
                    SelfDefenseCount++;
                    busy = true;
                }
                else if (hold)
                {
                    // 敌人没了：按原守备点恢复“待命”（玩家最后一条命令仍是“待命”）。
                    home.IssueCommand(unit, CombatCommandKind.Guard, post, 0, GuardArriveRadius, HomeValleyCombatTargets.EngageRange,
                        CombatSite.MachineAttackInterval, GameClock.Paused);
                }
                else if ((p - post).sqrMagnitude > SelfDefenseHomeSlack * SelfDefenseHomeSlack)
                {
                    busy = true; // 打完正走回驻防点
                }
            }
            return busy;
        }

        /// <summary>离驻防点这么远（格）还算“在走回驻防点”（内核守点教义离驻防点 1 格内就停）。</summary>
        private const float SelfDefenseHomeSlack = 1.5f;

        /// <summary>
        /// FG8-E2E-01 复审修复：行军中（前进 / 返回阶段）星球上的远征成员由本服务按 AI 教义管守点（<see cref="MaintainSelfDefense"/>），
        /// 攻城服务的驻防对账（只给驻防机器设守点、其余清零）跳过它们。O(成员数)。
        /// </summary>
        public static bool ManagesGuard(CampaignState s, int logicId)
        {
            ExpeditionMarchState m = Current(s);
            return m?.Members != null && (m.Phase == PhaseOutbound || m.Phase == PhaseReturning) && Array.IndexOf(m.Members, logicId) >= 0;
        }

        private static bool AnyEngaged(CombatSite site, IReadOnlyList<int> members)
        {
            float radius = EngageRadius;
            for (int i = 0; i < members.Count; i++)
            {
                if (site.TryGetMachinePosition(members[i], out Vector2 p) && site.HostileNear(p, radius))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AnyEngagedAtSite(ExpeditionMarchState m)
        {
            CombatSite site = CombatSites.Get(m.SiteId);
            if (site == null || site.IsDisposed || m.Members == null)
            {
                return false;
            }
            AliveScratch.Clear();
            for (int i = 0; i < m.Members.Length; i++)
            {
                if (MachineRegistry.TryGetRecord(m.Members[i], out MachineRecord rec) && rec != null && rec.IsAlive && rec.RegionId == m.SiteId)
                {
                    AliveScratch.Add(rec.LogicId);
                }
            }
            return AnyEngaged(site, AliveScratch);
        }

        /// <summary>
        /// 覆盖进出与“快走出覆盖”的提醒（FGR-SIG-053、FG08 §7）：整队都在覆盖里 ↔ 有成员出了覆盖，各提醒一次；
        /// 整队还在覆盖里、但覆盖余量最小的那台跌破 signal.coverage.edge_warn_cells 时提醒一次（余量回升后复位）。
        /// 在不在覆盖读内核每 0.5 秒的评估（O(1)/台），余量逐台采样（O(覆盖源数)/台）。
        /// </summary>
        private static void EvaluateCoverage(ExpeditionMarchState m, IReadOnlyList<int> members)
        {
            bool covered = true;
            float minMargin = float.PositiveInfinity;
            for (int i = 0; i < members.Count; i++)
            {
                if (SignalCoverageService.IsMachineOutOfCoverage(members[i]))
                {
                    covered = false;
                }
                SignalCoverageSample sample = SignalCoverageService.SampleMachine(members[i]);
                if (sample.Bounded)
                {
                    minMargin = Math.Min(minMargin, sample.MarginCells);
                }
            }
            if (covered != m.InCoverage)
            {
                m.InCoverage = covered;
                CoverageCueCount++;
                Feedback.FeedbackCues.Raise(covered ? Feedback.FeedbackCueId.ExpeditionMarchBackInCoverage : Feedback.FeedbackCueId.ExpeditionMarchOutOfCoverage);
                if (covered)
                {
                    m.EdgeWarned = minMargin < SignalCoverageService.EdgeWarnCells;
                }
                return;
            }
            if (!covered)
            {
                return;
            }
            float edge = SignalCoverageService.EdgeWarnCells;
            if (!m.EdgeWarned && minMargin < edge)
            {
                m.EdgeWarned = true;
                EdgeCueCount++;
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchEdge, GameText.Format("exp.cue.edge_detail", Mathf.Max(0, Mathf.FloorToInt(minMargin))));
            }
            else if (m.EdgeWarned && minMargin >= edge)
            {
                m.EdgeWarned = false;
            }
        }

        /// <summary>
        /// 途中遇敌与危害的提示（FGR-EXP-010“途中会遇到敌方巡逻、危害带”、FG8-EXP-02 必须同时交付）：敌方据点、巡逻、突袭队伍进入队伍 expedition.encounter.radius_cells 格内各提醒一次
        /// （ID 记进存档，读档后不重复）；队伍中心进入领地外圈危害带时提醒一次，离开后复位。只提示、不改命令（FGR-BASE-020）。每游戏秒 O(据点 + 巡逻 + 队伍)。
        /// </summary>
        private static void EvaluateEncounters(CampaignState s, ExpeditionMarchState m, Vector2 center)
        {
            PlannedTerritory belt = WorldGenQuery.HazardAt(s, Mathf.FloorToInt(center.x), Mathf.FloorToInt(center.y));
            string zone = belt != null && belt.HazardWidth > 0 ? belt.Id : string.Empty;
            if (zone.Length > 0 && zone != m.HazardZoneId)
            {
                HazardCueCount++;
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ExpeditionMarchHazard, center,
                    GameText.Format("exp.cue.hazard_detail", GameText.Get(belt.HazardKey)));
            }
            m.HazardZoneId = zone;
            // FG8-GEN-02（DEBT-FG8EXP02-05；FG08 第 7 节 / FG17 第 4 节）：队伍中心进入阵营领地时提醒一次“进入 ××”（领地名 + 介绍），离开后复位；只提示、不改命令（FGR-BASE-020）。存档去重。
            PlannedTerritory inside = WorldGenQuery.TerritoryAt(s, Mathf.FloorToInt(center.x), Mathf.FloorToInt(center.y));
            string territory = inside != null && inside.IsFaction ? inside.Id : string.Empty;
            m.TerritoryId ??= string.Empty;
            if (territory.Length > 0 && territory != m.TerritoryId)
            {
                TerritoryCueCount++;
                GameConfig.fg.TerritoryTemplate tpl = WorldGenQuery.Plan(s)?.TemplateFor(territory);
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ExpeditionMarchTerritory, center,
                    GameText.Format("exp.cue.territory_detail", GameText.Get(inside.NameKey), tpl != null ? GameText.Get(tpl.IntroKey) : string.Empty));
                GuidanceHooks.Raise(GuidanceHooks.WorldFirstTerritoryEntered);
            }
            m.TerritoryId = territory;

            float r2 = EncounterRadius * EncounterRadius;
            IReadOnlyList<OutpostRecord> outposts = WorldOutpostSystem.Outposts(s);
            for (int i = 0; outposts != null && i < outposts.Count; i++)
            {
                OutpostRecord o = outposts[i];
                if (o != null && !o.Destroyed && (new Vector2(o.CellX, o.CellY) - center).sqrMagnitude <= r2)
                {
                    TryRecordEncounter(m, o.OutpostId, new Vector2(o.CellX, o.CellY), "exp.cue.encounter.outpost");
                }
            }
            IReadOnlyList<PatrolRecord> patrols = WorldOutpostSystem.Patrols(s);
            for (int i = 0; patrols != null && i < patrols.Count; i++)
            {
                PatrolRecord p = patrols[i];
                if (p == null)
                {
                    continue;
                }
                Vector2 at = WorldOutpostSystem.PeekPatrolPosition(s, p);
                if ((at - center).sqrMagnitude <= r2)
                {
                    TryRecordEncounter(m, p.PatrolId, at, "exp.cue.encounter.patrol");
                }
            }
            IReadOnlyList<TransitGroupRecord> groups = WorldTransitSystem.Groups(s);
            for (int i = 0; groups != null && i < groups.Count; i++)
            {
                TransitGroupRecord g = groups[i];
                if (g == null)
                {
                    continue;
                }
                Vector2 at = WorldTransitSystem.Position(g);
                if ((at - center).sqrMagnitude <= r2)
                {
                    TryRecordEncounter(m, g.GroupId, at, "exp.cue.encounter.raid");
                }
            }
        }

        private static void TryRecordEncounter(ExpeditionMarchState m, string id, Vector2 at, string detailKey)
        {
            if (string.IsNullOrEmpty(id) || Array.IndexOf(m.EncounteredIds, id) >= 0)
            {
                return;
            }
            var next = new string[m.EncounteredIds.Length + 1];
            Array.Copy(m.EncounteredIds, next, m.EncounteredIds.Length);
            next[next.Length - 1] = id;
            m.EncounteredIds = next;
            EncounterCueCount++;
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ExpeditionMarchEncounter, at, GameText.Get(detailKey));
        }

        // ─────────────────────────────── 到达（事务）───────────────────────────────

        private static void QueueArrival(CampaignState s, ExpeditionMarchState m)
        {
            if (ReferenceEquals(_arrivalQueuedFor, m))
            {
                return;
            }
            _arrivalQueuedFor = m;
            WorldSimulation.RunAfterStep(() => Arrive(s, m));
        }

        /// <summary>
        /// 到达事务（步末执行；自检快进 <see cref="ArriveNowForTests"/> 也走这里）：存到达前档 → 载入远征地点（存活成员转入地点，地点 Enter 里存档）→ 提交。
        /// 地点不开放（区域在途中被重新锁定）→ 不存档、不载入，队伍在集结点等候并说明；载入中途出错 → 整体回滚到到达前档，同样在集结点等候（不瞬移回家，不丢机器）；
        /// 两种情况最后一条命令都仍是“前进”，到点自动重试（<see cref="FailArrival"/>）。
        /// 不抢镜头：只有玩家正在“跟随远征队”时镜头才跟进地点，否则只发到达通知。
        /// </summary>
        private static void Arrive(CampaignState s, ExpeditionMarchState m)
        {
            if (ReferenceEquals(_arrivalQueuedFor, m))
            {
                _arrivalQueuedFor = null;
            }
            if (!ReferenceEquals(CampaignSession.Current, s) || !ReferenceEquals(Current(s), m) || m.Phase != PhaseOutbound)
            {
                return;
            }
            int slot = CampaignSession.ActiveSlotIndex;
            string siteId = m.SiteId;
            // 地点现在不开放（区域在途中被重新锁定）：不存档、不载入，机器一台都不动（自动重试时也不反复写盘）。
            RegionRecord region = siteId == FoundryOutpostLayout.RegionId ? FoundryOutpostRegion.Find(s) : FracturedCityRegion.Find(s);
            if (region == null || region.State == RegionState.Locked)
            {
                FailArrival(s, m, "site-unavailable");
                return;
            }
            SaveResult pre = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
            // 回滚基线另存一份：地点载入（Enter）里会再自动存一次档，覆盖同一个槽位——回滚要读的是到达前的那份，不是载入后的。
            string baseline = BaselinePath(slot);
            bool haveBaseline = pre.Success && TryCopy(CampaignSaveService.SlotPath(slot), baseline);
            if (!pre.Success || !haveBaseline)
            {
                Log.Warning($"[ExpeditionMarchService] 到达前存档未成功（{pre.Outcome} {pre.Message}，基线副本 {haveBaseline}）：不载入远征地点，队伍在集结点待命。");
                TryDelete(baseline);
                FailArrival(s, m, "autosave-failed");
                return;
            }
            bool follow = WorldView.FollowingExpedition;
            var alive = new List<int>(AlivePlanetMembers(s));
            try
            {
                SetPhase(m, PhaseAtSite);
                m.Halted = HaltNone;
                m.VisitedSite = true; // FG8-EXP-05
                // FG9-NAR-01（FGR-NAR-010“第一次远征破碎都市 / 第一次进入铸造前哨”）：叙事投放按地点触发（资料库同一条只收一次）。
                // 必须在载入地点之前：地点 Enter 会自动存档，存档里要已经有这条（审查修复 P1）；载入失败在下面撤回，异常则整档回滚。
                GameEvent.Send(SiteArrivedEvent, siteId);
                IWorldSite site = siteId == FoundryOutpostLayout.RegionId
                    ? (IWorldSite)WorldSimulation.LoadFoundryOutpost(alive, resume: false)
                    : WorldSimulation.LoadFracturedCity(alive, resume: false);
                ArrivalFaultInjectionForTests?.Invoke();
                if (site == null || !site.IsLoaded)
                {
                    foreach (int id in alive)
                    {
                        if (MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null && rec.RegionId != HomeValleyLayout.RegionId)
                        {
                            throw new InvalidOperationException("远征地点没有载入，但已有机器转进了地点（半提交）");
                        }
                    }
                    SetPhase(m, PhaseOutbound);
                    m.VisitedSite = false;
                    GameEvent.Send(SiteArrivalRevertedEvent, siteId);
                    FailArrival(s, m, "site-unavailable");
                    return;
                }
                ArriveCount++;
                m.ArrivalFailReason = string.Empty;
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchArrived, GameText.Get(ExpeditionTextKeys.SiteNameKey(siteId)));
                if (follow)
                {
                    // 玩家正在跟随远征队：镜头跟进地点（玩家要求的跟随，不是抢镜头）。
                    WorldView.Observe(siteId);
                }
                Log.Info($"[ExpeditionMarchService] 远征队到达 {siteId}：{alive.Count} 台进入地点（行军 {(GameClock.Ticks - m.DepartTick) / (double)GameClock.StepHz:0.0} 游戏秒）。");
            }
            catch (Exception e)
            {
                Log.Error($"[ExpeditionMarchService] 到达事务中途异常，回滚到到达前档：{e}");
                RollbackArrival(slot, baseline);
            }
            finally
            {
                TryDelete(baseline);
            }
        }

        private static string BaselinePath(int slot) => slot >= 0 ? CampaignSaveService.SlotPath(slot) + ".prearrival" : null;

        private static bool TryCopy(string from, string to)
        {
            try
            {
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || !System.IO.File.Exists(from))
                {
                    return false;
                }
                System.IO.File.Copy(from, to, true);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"[ExpeditionMarchService] 复制到达前档失败：{e.Message}");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 临时副本删不掉不影响游戏（下一次到达会覆盖它）。
            }
        }

        /// <summary>
        /// 到达没成（地点不开放 / 存档失败 / 载入中途出错已回滚）：队伍留在集结点（Halted = 到达失败）并说明原因。不替玩家改命令——最后一条仍是“前进”，
        /// 到 <see cref="ExpeditionMarchState.ArrivalRetryTick"/>（expedition.march.arrival_retry_seconds 游戏秒后）按它自动重试；玩家在覆盖内再下“前进”立即重试。
        /// 同一原因重试再失败只记日志、不重复提示（原因变了才提示）。
        /// </summary>
        private static void FailArrival(CampaignState s, ExpeditionMarchState m, string reason)
        {
            ArrivalFailCount++;
            string code = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            bool repeat = m.ArrivalFailReason == code;
            m.Halted = HaltArrivalFailed;
            m.ArrivalFailReason = code;
            m.ArrivalRetryTick = GameClock.Ticks + Math.Max(1L, (long)Math.Round(ArrivalRetrySeconds * GameClock.StepHz));
            if (!repeat)
            {
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchArrivalFailed, GameText.Get(ExpeditionTextKeys.ReasonKey(code)));
            }
            Log.Warning($"[ExpeditionMarchService] 远征队没能进入 {m.SiteId}（{code}）：在集结点等候，第 {m.ArrivalRetryTick} 步按“前进”自动重试{(repeat ? "（同一原因，不重复提示）" : string.Empty)}。");
        }

        private static void RollbackArrival(int slot, string baseline)
        {
            ArrivalRollbackCount++;
            if (slot < 0)
            {
                Log.Error("[ExpeditionMarchService] 到达回滚失败：没有有效的存档槽位。");
                return;
            }
            WorldSimulation.UnloadAll();
            // 把到达前档的副本放回槽位（地点载入时的自动存档已经覆盖了槽位），再按读档流程读回。
            if (!TryCopy(baseline, CampaignSaveService.SlotPath(slot)))
            {
                Log.Error("[ExpeditionMarchService] 到达回滚：到达前档的副本不在，改读槽位里现有的档。");
            }
            RestoreResult restore = CampaignRestoreOrchestrator.Restore(slot);
            if (!restore.Success)
            {
                Log.Error($"[ExpeditionMarchService] 到达回滚失败：从到达前档重新读取未成功（{restore.FailedStep} {restore.Message}）。建议从主菜单重新读档。");
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchArrivalFailed, GameText.Get(ExpeditionTextKeys.ReasonKey("rollback-failed")));
                return;
            }
            CampaignSession.Set(slot, restore.State);
            Signal.FirmwareLibrary.OnCampaignEntered(restore.State);
            Env.HeritageService.OnCampaignEntered(restore.State); // FG8-EXP-04
            GameLogic.Narrative.NarrativeService.OnCampaignEntered(restore.State); // FG9-NAR-01：新游戏触发开场演出、待播演出接着播（已玩过的存档不补播）
            GameRoot.ReenterHomeValley();
            ExpeditionMarchState m = Current(restore.State);
            if (m != null && m.Phase == PhaseOutbound)
            {
                FailArrival(restore.State, m, "transaction-failed");
                SaveResult after = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
                if (!after.Success)
                {
                    Log.Warning($"[ExpeditionMarchService] 到达回滚后存档未成功（{after.Outcome}）：下次自然存档点会补上“到达失败、待命”。");
                }
            }
            Log.Info("[ExpeditionMarchService] 已回滚到到达前档：远征队留在集结点待命，没有复制或丢失机器。");
        }

        /// <summary>
        /// 自检专用快进（正式代码没有调用点）：把去程中的远征队直接放到目的地锚点旁，然后走与正式到达同一条到达事务（<see cref="Arrive"/>）。
        /// 给沿用“出发后在地点里继续测”口径的既有自检用；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证。返回是否已进入远征地点。
        /// <paramref name="observeSite"/>：进入后镜头切到地点（相当于玩家跟着远征队进了地点——旧自检“出发即镜头在地点”的口径；正式到达不抢镜头）。
        /// </summary>
        public static bool ArriveNowForTests(CampaignState s, bool observeSite = true)
        {
            ExpeditionMarchState m = Current(s);
            if (m == null || m.Phase != PhaseOutbound || !ExpeditionSiteAnchors.TryGet(s, m.SiteId, out Vector2 anchor))
            {
                return false;
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            IReadOnlyList<int> members = AlivePlanetMembers(s);
            for (int k = 0; k < members.Count; k++)
            {
                Vector2 at = anchor + FormationOffset(k);
                if (home != null && home.TryGetMachineMarker(members[k], out HomeValleyMachineMarker marker))
                {
                    home.ClearCommand(marker.UnitId);
                    home.SetUnitPosition(marker.UnitId, at);
                }
                else if (MachineRegistry.TryGetRecord(members[k], out MachineRecord rec))
                {
                    rec.WorldPosition = at;
                }
            }
            m.Command = (int)Command.Advance;
            m.Halted = HaltNone;
            m.OrdersPending = false;
            Arrive(s, m);
            CampaignState now = CampaignSession.Current;
            bool arrived = Current(now) is { Phase: PhaseAtSite } && WorldSimulation.ActiveExpedition != null;
            if (arrived && observeSite)
            {
                WorldView.Observe(WorldSimulation.ActiveExpedition.SiteId);
            }
            return arrived;
        }

        // ─────────────────────────────── 返回 / 全灭 / 离开地点 ───────────────────────────────

        /// <summary>
        /// 返程走回家园（FGR-EXP-012“已返回”）：恢复速度、离家报告按“返回”结算、货舱按远征卸货规则卸货（与撤离同一入口）、提示、步末存档。
        /// 这是行军途中被召回的远征（没进过地点）；从远征地点撤离的结算仍在 <see cref="ExpeditionReturnService"/>。
        /// </summary>
        private static void ReturnHome(CampaignState s, ExpeditionMarchState m, CombatSite home, IReadOnlyList<int> members)
        {
            var returning = new List<int>(members);
            // FG8-EXP-05（FGR-EXP-050）：到家才结算——携带的关键物在这里带回、写远征结算（带回的物品、三选一、任务、线索、暴露、耗时、经历、留在原地的东西），
            // 然后结算离家报告、按远征卸货规则卸货。
            Economy.ExpeditionSettlementService.Settle(s, Economy.ExpeditionSettlementService.ViaFoot, returning);
            foreach (int id in returning)
            {
                if (home.TryGetMachineUnit(id, out int unit))
                {
                    home.SetUnitSpeed(unit, HomeValleyMachineMarker.MoveSpeed);
                    home.ClearCommand(unit);
                }
                home.SetMachineGuard(id, default, 0f); // 回到家园：不再按远征的自卫教义守点
            }
            SetPhase(m, PhaseReturned);
            m.Halted = HaltNone;
            m.Command = (int)Command.Advance;
            ReturnHomeCount++;
            Economy.AwayReportService.End(s, Economy.AwayReportService.OutcomeEvacuated);
            Economy.StandingRuleService.OnExpeditionReturned(s, returning);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchReturned, GameText.Format("exp.cue.returned_detail", returning.Count));
            Log.Info($"[ExpeditionMarchService] 远征队返回家园：{returning.Count} 台。");
            WorldSimulation.RunAfterStep(() =>
            {
                if (ReferenceEquals(CampaignSession.Current, s))
                {
                    SaveResult r = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
                    if (!r.Success)
                    {
                        Log.Warning($"[ExpeditionMarchService] 返回后存档未成功（{r.Outcome}），下次自然存档点会补上。");
                    }
                }
            });
        }

        /// <summary>行军途中全部阵亡：远征结束（离家报告按全灭结算、全灭提示）。阵亡掉落、黑匣子照家园机器阵亡的正式流程（FG8-EXP-05 / LOOT-03 补全灭结算）。</summary>
        private static void OnWipedOnPlanet(CampaignState s, ExpeditionMarchState m)
        {
            // FG8-EXP-05（FGR-EXP-031 / 032，承接 DEBT-FG8EXP02-04）：行军途中全灭——货物、黑匣子、关键物、数据包已在阵亡那一刻掉在原地（KeyItemRetention），
            // 结算里列出留在原地的东西与回收倒计时、写明全灭的处理（远征结算 + 离家报告），提示写明“东西留在原地、可以再派队取回”。
            AwayReportRecord rec = Economy.ExpeditionSettlementService.Settle(s, Economy.ExpeditionSettlementService.ViaWiped, Array.Empty<int>());
            // 修复第 4 轮（审查 P1）：分批接回、有成员已随投送舱生还（落地或还在舱里）时，星球上其余成员阵亡不算全灭——结算改按“随投送舱返回”，离家报告写“返回”。
            if (rec?.Settlement?.Via == Economy.ExpeditionSettlementService.ViaPod)
            {
                SetPhase(m, PhaseReturned);
                m.Halted = HaltNone;
                m.Command = (int)Command.Advance;
                PodReturnCount++;
                Economy.AwayReportService.End(s, Economy.AwayReportService.OutcomeEvacuated);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchReturned, GameText.Get("exp.cue.returned_by_pod"));
                Log.Info("[ExpeditionMarchService] 星球上的远征成员全部失去行动能力，其余成员已随投送舱返回，按“返回”结算。");
                return;
            }
            SetPhase(m, PhaseNone);
            m.Halted = HaltNone;
            WipedCount++;
            Economy.AwayReportService.End(s, Economy.AwayReportService.OutcomeWiped);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionWiped, GameText.Get("exp.cue.wiped_on_march") + GameText.Get("settle.wipe.cue_tail"));
            Log.Warning("[ExpeditionMarchService] 远征队在行军途中全部失去行动能力。");
        }

        /// <summary>FG8-DROP-01：经投送舱回收返回的远征次数（自检读）。</summary>
        public static int PodReturnCount { get; private set; }

        /// <summary>
        /// FG8-DROP-01（FGR-EXP-015“回收是正式的撤离方式”）：行军中的成员上了投送舱——离开行军名单（不再接远征命令）；名单上没有还在星球上的存活成员时 = 远征经投送舱返回：
        /// 离家报告按“返回”结算（卸货在舱落回投送站时按远征卸货规则做）。不在行军中 / 不是成员时什么都不做。
        /// </summary>
        public static void OnMembersBoardedPod(CampaignState s, IReadOnlyList<int> ids)
        {
            ExpeditionMarchState m = Current(s);
            if (m?.Members == null || ids == null || ids.Count == 0 || (m.Phase != PhaseOutbound && m.Phase != PhaseReturning))
            {
                return;
            }
            var keep = new List<int>(m.Members.Length);
            bool any = false;
            foreach (int id in m.Members)
            {
                bool boarded = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    boarded |= ids[i] == id;
                }
                if (boarded)
                {
                    any = true;
                    continue;
                }
                keep.Add(id);
            }
            if (!any)
            {
                return;
            }
            m.Members = keep.ToArray();
            if (AlivePlanetMembers(s).Count > 0)
            {
                return;
            }
            // FG8-EXP-05：投送舱接回 = 一种返回（FGR-EXP-031）：最后一台登舱时结算（货舱里的货、携带的关键物随舱返回，落回投送站时按远征卸货规则卸下 / 带回）。
            Economy.ExpeditionSettlementService.Settle(s, Economy.ExpeditionSettlementService.ViaPod, Economy.AwayReportService.Current(s)?.Members);
            SetPhase(m, PhaseReturned);
            m.Halted = HaltNone;
            m.Command = (int)Command.Advance;
            PodReturnCount++;
            Economy.AwayReportService.End(s, Economy.AwayReportService.OutcomeEvacuated);
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchReturned, GameText.Get("exp.cue.returned_by_pod"));
            Log.Info("[ExpeditionMarchService] 远征队全部登上投送舱，按“返回”结算。");
        }

        /// <summary>FG8-EXP-05：从远征地点撤离、开始走回家园的次数（自检读）。</summary>
        public static int SiteReturnCount { get; private set; }

        /// <summary>
        /// FG8-EXP-05（关闭 DEBT-FG8EXP02-01；FGR-EXP-031“命令远征队走回家园或前哨站”、FGR-EXP-050“回到家园……时结算”）：从远征地点撤离之后，
        /// 幸存者出现在星球上的地点入口（锚点旁，三台一排），状态“返程中”，沿地形走回最近的家园 / 带仓库的前哨站（层级寻路，与行军同一套），到家才结算。
        /// 由 <see cref="ExpeditionReturnService.TryConfirmEvacuation"/> 在地点卸载、家园恢复之后调用；成员的位置在恢复家园前已写到锚点旁（<see cref="ExitPosition"/>）。
        /// 途中可以被拦截、全灭（按全灭结算，东西留在原地），也可以呼叫投送舱接回（DEBT-FG8DROP01-01：地点里的队伍先撤离到入口，再从入口被接回）。
        /// </summary>
        public static void BeginReturnFromSite(CampaignState s, string siteId, IReadOnlyList<int> survivors)
        {
            if (s?.Grid?.ExpeditionTeam == null)
            {
                return;
            }
            ExpeditionMarchState m = s.Grid.ExpeditionTeam.March ??= new ExpeditionMarchState();
            var ids = new List<int>(survivors ?? Array.Empty<int>());
            ids.Sort();
            m.SiteId = siteId ?? m.SiteId;
            m.Members = ids.ToArray();
            SetPhase(m, PhaseReturning);
            m.Command = (int)Command.Return;
            m.Halted = HaltNone;
            m.ArrivalFailReason = string.Empty;
            m.Speed = MarchSpeed(ids);
            m.InCoverage = true;
            m.EdgeWarned = false;
            m.Engaged = false;
            m.HazardZoneId = string.Empty;
            m.VisitedSite = true;
            m.HasReturnGoal = false;
            ChooseReturnGoal(s, m);
            m.OrdersPending = true; // 成员刚在家园内核里出现，下一步补下“返回”的内核命令
            SiteReturnCount++;
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionMarchReturned, GameText.Format("settle.cue.walking_home", ids.Count));
            Log.Info($"[ExpeditionMarchService] 远征队从 {siteId} 撤离：{ids.Count} 台从地点入口走回家园（到家才结算）。");
        }

        /// <summary>FG8-EXP-05：撤离出来的第 <paramref name="index"/> 台在星球上的位置（地点入口锚点旁三台一排；锚点不在时归还核心旁）。</summary>
        public static Vector2 ExitPosition(CampaignState s, string siteId, int index) =>
            (ExpeditionSiteAnchors.TryGet(s, siteId, out Vector2 anchor) ? anchor : HomeValleyLayout.Core.Position + new Vector2(2f, -2f)) + FormationOffset(index);

        /// <summary>返程目的地：离队伍中心最近的家园 / 带仓库的前哨站（核心或仓库；同距离按建筑顺序）。O(建筑数)，只在下令 / 目的地消失时。</summary>
        private static void ChooseReturnGoal(CampaignState s, ExpeditionMarchState m)
        {
            Vector2 from = HomeValleyLayout.Core.Position;
            if (TryTeamCenter(s, out Vector2 c, out _))
            {
                from = c;
            }
            else if (m.Members != null && m.Members.Length > 0 && MachineRegistry.TryGetRecord(m.Members[0], out MachineRecord rec) && rec != null)
            {
                from = rec.WorldPosition;
            }
            DepotScratch.Clear();
            Economy.LootPickService.CollectDepotPositions(s, DepotScratch);
            Vector2 best = HomeValleyLayout.Core.Position;
            float bestD = (best - from).sqrMagnitude;
            foreach (Vector2 d in DepotScratch)
            {
                float dd = (d - from).sqrMagnitude;
                if (dd < bestD - 1e-3f)
                {
                    bestD = dd;
                    best = d;
                }
            }
            DepotScratch.Clear();
            m.HasReturnGoal = true;
            m.ReturnX = best.x;
            m.ReturnY = best.y;
        }

        private static readonly List<Vector2> DepotScratch = new List<Vector2>(8);

        private static bool DepotStillThere(CampaignState s, Vector2 at)
        {
            if ((at - HomeValleyLayout.Core.Position).sqrMagnitude < 0.01f)
            {
                return true;
            }
            DepotScratch.Clear();
            Economy.LootPickService.CollectDepotPositions(s, DepotScratch);
            bool found = false;
            foreach (Vector2 d in DepotScratch)
            {
                found |= (d - at).sqrMagnitude < 0.01f;
            }
            DepotScratch.Clear();
            return found;
        }

#if UNITY_EDITOR
        // 修复第 2 轮（审查 P2）：只在编辑器里编译，玩家构建没有这条瞬移回家的入口。
        /// <summary>
        /// 自检专用快进（正式代码没有调用点）：返程中的远征队直接放到返程目的地旁，然后走与正式到家同一条结算（<see cref="ReturnHome"/>）。
        /// 给“撤离后在家园里接着测”的既有自检与冒烟用；返程本身由 FgExpeditionSettlementSelfCheck 真实推进验证。返回是否已结算为“已返回”。
        /// </summary>
        public static bool ReturnNowForTests(CampaignState s)
        {
            ExpeditionMarchState m = Current(s);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            if (m == null || m.Phase != PhaseReturning || home == null || !TryGetGoal(s, out Vector2 goal))
            {
                return false;
            }
            var ids = new List<int>(AlivePlanetMembers(s));
            if (ids.Count == 0)
            {
                OnWipedOnPlanet(s, m);
                return false;
            }
            for (int k = 0; k < ids.Count; k++)
            {
                Vector2 at = goal + FormationOffset(k);
                if (home.TryGetMachineMarker(ids[k], out HomeValleyMachineMarker marker))
                {
                    home.ClearCommand(marker.UnitId);
                    home.SetUnitPosition(marker.UnitId, at);
                }
                if (MachineRegistry.TryGetRecord(ids[k], out MachineRecord rec))
                {
                    rec.WorldPosition = at;
                }
            }
            m.OrdersPending = false;
            ReturnHome(s, m, home, ids);
            return Current(s) is { Phase: PhaseReturned };
        }
#endif

        /// <summary>离开远征地点（<see cref="ExpeditionReturnService"/> 撤离 / 全灭放弃之后调用）：撤离 = 已返回；全灭放弃 = 没有远征在外。</summary>
        public static void OnLeftSite(CampaignState s, bool wiped)
        {
            ExpeditionMarchState m = Current(s);
            if (m == null)
            {
                return;
            }
            SetPhase(m, wiped ? PhaseNone : PhaseReturned);
            m.Halted = HaltNone;
        }
    }

    /// <summary>FG8-EXP-02：行军相关文本键的唯一来源（数据在 <c>tools/cell_tables/fgdata_expedition.py</c>，生成到 fg.TbText）。自检逐个核对每个键都能查到。</summary>
    public static class ExpeditionTextKeys
    {
        public static string StatusKey(ExpeditionMarchService.TeamStatus status)
        {
            switch (status)
            {
                case ExpeditionMarchService.TeamStatus.Marching: return "exp.state.marching";
                case ExpeditionMarchService.TeamStatus.Engaged: return "exp.state.engaged";
                case ExpeditionMarchService.TeamStatus.Returning: return "exp.state.returning";
                case ExpeditionMarchService.TeamStatus.Returned: return "exp.state.returned";
                default: return "exp.state.standby";
            }
        }

        /// <summary>远征地点名的文本键（world.site.&lt;地点&gt;.name，FG0-ARCH-01 既有）。</summary>
        public static string SiteNameKey(string siteId) => "world.site." + siteId + ".name";

        /// <summary>命令被拒 / 到达失败的原因码 → 文本键 exp.march.reason.&lt;下划线&gt;。</summary>
        public static string ReasonKey(string reason) => "exp.march.reason." + (string.IsNullOrEmpty(reason) ? "unknown" : reason).Replace('-', '_');

        /// <summary>界面会显示的原因码全集（自检核对文本键都在）。</summary>
        public static readonly string[] ReasonCodes =
        {
            "no-march", "at-site", "silent-night", "out-of-coverage", "no-members", "destination-pending", "site-unavailable",
            "autosave-failed", "transaction-failed", "rollback-failed", "unknown",
        };

        /// <summary>五态全集。</summary>
        public static readonly ExpeditionMarchService.TeamStatus[] AllStatuses =
        {
            ExpeditionMarchService.TeamStatus.Standby, ExpeditionMarchService.TeamStatus.Marching, ExpeditionMarchService.TeamStatus.Engaged,
            ExpeditionMarchService.TeamStatus.Returning, ExpeditionMarchService.TeamStatus.Returned,
        };
    }
}
