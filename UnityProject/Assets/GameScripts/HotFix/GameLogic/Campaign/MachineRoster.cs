using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG4-ECO-07（FG04 FGR-ECO-040 岗位 / 041 名册 / 042 劳动力提示；FG13 FGU-16 / 17；FG00 FGR-BASE-020；FGT-ECO-004）：机器岗位与名册的唯一业务入口。
    /// 名册面板、顶栏、远征准备与自检都调这里（与自检同一入口）。
    ///
    /// 岗位（fg.TbRosterRole，七种）：
    /// - 存档里只存玩家设定的五种（<see cref="MachineRecord.Role"/>：劳动 / 驻防 / 前哨劳动 / 远征预备 / 闲置），旧档缺字段 = 劳动；
    /// - “远征中”= 活着且不在家园（出发 / 返回自动进出，名册里不能选，改岗位被拒绝并说明）；“维修中”= 手上有送修单（名册里选它 = 送去维修台，修满回到原岗位）。
    /// 行为（<see cref="TakesLabor"/> / <see cref="AllowsRuleDispatch"/> / <see cref="KeepRoles"/>）：
    /// - 劳动：在家园按工作单优先级接活（沿用 ER3-WRK-02 的确定性分配，<c>HomeValleyWorkOrders.TickAssignment</c> 只派劳动岗）；
    /// - 驻防：不接劳动工作单，没有在办工单时由岗位维持器派到驻防点待命（玩家给它下了别的命令 = 暂停，直到在名册里再设一次）；
    /// - 远征预备：不接劳动工作单，远征准备面板自动勾选；
    /// - 闲置：什么都不做——不接工作单，常驻规则也不派它，只执行玩家直接下的命令（FGR-BASE-020）；
    /// - 前哨劳动：要有己方前哨站（FG8-OUT-01 之前一律拒绝并说明）。
    /// 开销：改岗位 / 改名只在玩家操作时 O(工单数)；岗位维持器在派工评估时（每 0.5 秒或有脏标记）O(机器数)；顶栏劳动力最多每 roster.labor_refresh_seconds 真实秒 O(机器数 + 工单数)。
    /// </summary>
    public static class MachineRoster
    {
        /// <summary>岗位 / 驻防点变更版本号（界面据此重建）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>有没有己方前哨站（FG8-OUT-01 接上；之前恒为“没有”，前哨劳动一律拒绝并说明）。</summary>
        public static Func<CampaignState, bool> HasOutpostProvider;

        /// <summary>真实时间来源（劳动力缓存；自检可注入）。</summary>
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        // ── 岗位表 ───────────────────────────────────────────────────────────────

        private static Tables _rowsFrom;
        private static readonly Dictionary<int, RosterRole> Rows = new Dictionary<int, RosterRole>();
        private static readonly List<MachineRole> Ordered = new List<MachineRole>(8);

        /// <summary>全部七种岗位，按表里的排序。</summary>
        public static IReadOnlyList<MachineRole> AllRoles
        {
            get
            {
                EnsureRows();
                return Ordered;
            }
        }

        public static RosterRole Row(MachineRole role)
        {
            EnsureRows();
            return Rows.TryGetValue((int)role, out RosterRole r) ? r : null;
        }

        private static void EnsureRows()
        {
            Tables tables = null;
            try
            {
                tables = ConfigSystem.Instance.Tables;
            }
            catch (Exception e)
            {
                Log.Error("[MachineRoster] 读取配置表失败：" + e.Message);
            }
            if (tables != null && ReferenceEquals(tables, _rowsFrom) && Ordered.Count > 0)
            {
                return;
            }
            _rowsFrom = tables;
            Rows.Clear();
            Ordered.Clear();
            var sorted = new List<RosterRole>();
            if (tables?.TbRosterRole != null)
            {
                foreach (RosterRole r in tables.TbRosterRole.DataList)
                {
                    if (r != null && Enum.IsDefined(typeof(MachineRole), r.Code))
                    {
                        Rows[r.Code] = r;
                        sorted.Add(r);
                    }
                }
            }
            sorted.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : a.Code.CompareTo(b.Code));
            foreach (RosterRole r in sorted)
            {
                Ordered.Add((MachineRole)r.Code);
            }
            if (Ordered.Count == 0)
            {
                foreach (MachineRole m in (MachineRole[])Enum.GetValues(typeof(MachineRole)))
                {
                    Ordered.Add(m);
                }
            }
        }

        /// <summary>表换了（自检重载）时下次读取重建。</summary>
        public static void Reload()
        {
            _rowsFrom = null;
            Ordered.Clear();
        }

        public static string RoleName(MachineRole role)
        {
            RosterRole r = Row(role);
            return r != null ? GameText.Get(r.NameKey) : role.ToString();
        }

        public static string RoleDesc(MachineRole role)
        {
            RosterRole r = Row(role);
            return r != null ? GameText.Get(r.DescKey) : string.Empty;
        }

        /// <summary>玩家能不能在名册里选这种岗位（远征中不能）。</summary>
        public static bool IsSettable(MachineRole role)
        {
            RosterRole r = Row(role);
            return r != null ? r.Settable == 1 : role != MachineRole.OnExpedition;
        }

        /// <summary>存档里只存可设定的五种；远征中 / 维修中 / 越界整数一律按劳动读（旧档缺字段本来就是 0 = 劳动）。</summary>
        public static MachineRole SanitizeStoredRole(MachineRole role)
        {
            switch (role)
            {
                case MachineRole.Labor:
                case MachineRole.Garrison:
                case MachineRole.OutpostLabor:
                case MachineRole.ExpeditionReserve:
                case MachineRole.Idle:
                    return role;
                default:
                    return MachineRole.Labor;
            }
        }

        // ── 岗位的行为 ─────────────────────────────────────────────────────────────

        /// <summary>这台机器接不接劳动工作单（只有“劳动”岗接；以存档里的岗位为准——远征 / 维修中另有地点与工单把它排除在外）。</summary>
        public static bool TakesLabor(MachineRecord m)
        {
            if (m == null)
            {
                return false;
            }
            RosterRole r = Row(m.Role);
            return r != null ? r.TakesLabor == 1 : m.Role == MachineRole.Labor;
        }

        /// <summary>常驻规则能不能自动派遣它（送修、驻防）。闲置 = 不能（FGR-BASE-020：闲置的机器不做任何事）。</summary>
        public static bool AllowsRuleDispatch(MachineRecord m)
        {
            if (m == null)
            {
                return false;
            }
            RosterRole r = Row(m.Role);
            return r != null ? r.AutoActions == 1 : m.Role != MachineRole.Idle;
        }

        public static bool IsHome(MachineRecord m) => m != null && m.RegionId == HomeValleyLayout.RegionId;

        /// <summary>活着、不在家园 = 远征中。</summary>
        public static bool IsAway(MachineRecord m) => m != null && m.IsAlive && !IsHome(m);

        /// <summary>手上有送修单（规则或名册派的）。</summary>
        public static WorkOrderRecord RepairOrder(CampaignState s, MachineRecord m)
        {
            WorkOrderRecord o = s == null || m == null ? null : HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId);
            return o != null && o.Kind == WorkOrderKind.MachineRepair ? o : null;
        }

        /// <summary>玩家看到的岗位：远征中 &gt; 维修中 &gt; 存档里的岗位。</summary>
        public static MachineRole EffectiveRole(CampaignState s, MachineRecord m)
        {
            if (m == null)
            {
                return MachineRole.Labor;
            }
            if (IsAway(m))
            {
                return MachineRole.OnExpedition;
            }
            if (m.IsAlive && RepairOrder(s, m) != null)
            {
                return MachineRole.InRepair;
            }
            return SanitizeStoredRole(m.Role);
        }

        /// <summary>驻防岗被玩家接管过（右键别处 / 接入后下了别的命令）：维持器不再把它拉回驻防点，直到在名册里再设一次“驻防”。</summary>
        public static bool GarrisonSuspended(CampaignState s, MachineRecord m)
        {
            if (m == null || m.Role != MachineRole.Garrison || string.IsNullOrEmpty(m.RoleOrderId))
            {
                return false;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.Find(s, m.RoleOrderId);
            return o != null && o.State == WorkOrderState.Cancelled && o.FailureReason == HomeValleyWorkOrders.PlayerTookOverReason;
        }

        /// <summary>
        /// FG4-ECO-07 审查修复（P1）：驻防岗上一张名册驻防单因为到不了驻防点而失败（无法到达 / 路径持续受阻）——维持器不再补派（否则每次评估都开一张新单、
        /// 每张发一条“无法到达”通知），直到玩家在名册里再设一次“驻防”或换个驻防点。<paramref name="failed"/> = 那张失败的单（详情页据此写原因）。
        /// </summary>
        public static bool GarrisonBlocked(CampaignState s, MachineRecord m, out WorkOrderRecord failed)
        {
            failed = null;
            if (m == null || m.Role != MachineRole.Garrison || string.IsNullOrEmpty(m.RoleOrderId))
            {
                return false;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.Find(s, m.RoleOrderId);
            if (o == null || o.Kind != WorkOrderKind.Garrison || o.State != WorkOrderState.Failed
                || !(HomeValleyWorkOrders.IsUnreachableReason(o.FailureReason) || o.FailureReason == HomeValleyWorkOrders.PathBlockedReason))
            {
                return false;
            }
            failed = o;
            return true;
        }

        public static bool GarrisonBlocked(CampaignState s, MachineRecord m) => GarrisonBlocked(s, m, out _);

        /// <summary>驻防受阻的玩家可见原因（当前语言）：“驻防暂停：到不了驻防点 X（原因）……”。</summary>
        public static string GarrisonBlockedText(CampaignState s, MachineRecord m, WorkOrderRecord failed) =>
            GameText.Format("roster.garrison.blocked", GarrisonPointLabel(s, m), TripFailWhy(s, failed));

        /// <summary>驻防 / 巡逻这一趟为什么失败（寻路的原因，或“路径持续受阻”）。</summary>
        private static string TripFailWhy(CampaignState s, WorkOrderRecord failed)
        {
            if (failed != null && HomeValleyWorkOrders.IsUnreachableReason(failed.FailureReason))
            {
                Vector2 p = HomeValleyWorkOrders.ResolveWorkPosition(s, failed);
                return Nav.NavService.FailText(HomeValleyWorkOrders.ParseUnreachable(failed.FailureReason), Nav.NavService.CellOf(p.x, p.y));
            }
            return GameText.Get("roster.garrison.path_blocked");
        }

        public static bool HasOutpost(CampaignState s) => HasOutpostProvider != null && HasOutpostProvider(s);

        // ── 改岗位 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 改一台机器的岗位（名册行内下拉框、详情页、批量都走这里）。成功返回 true；失败返回 false 且什么都不改，<paramref name="message"/> 写明原因（当前语言）：
        /// 阵亡、远征中（返回家园后才能改）、厂内、“远征中”不能选、没有前哨站、送修时没有可用维修台 / 耐久已满、和现在一样。
        /// 成功时：维修中 = 开一张名册送修单（原岗位不变，修满回到它）；其余 = 写岗位，让出与新岗位不符的工单并让机器停下（见 <c>HomeValleyWorkOrders.ReleaseForRoleChange</c>）。
        /// </summary>
        public static bool TrySetRole(CampaignState s, int logicId, MachineRole role, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || s == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            string who = MachineNaming.Short(m);
            if (!m.IsAlive)
            {
                message = GameText.Format("roster.err.dead", who);
                return false;
            }
            if (IsAway(m))
            {
                message = GameText.Format("roster.err.on_expedition", who);
                return false;
            }
            if (!IsSettable(role))
            {
                message = GameText.Format("roster.err.not_settable", RoleName(role));
                return false;
            }
            if (m.IsInFactory)
            {
                message = GameText.Format("roster.err.in_factory", who);
                return false;
            }
            if (role == MachineRole.OutpostLabor && !HasOutpost(s))
            {
                message = GameText.Get("roster.err.no_outpost");
                return false;
            }
            MachineRole current = EffectiveRole(s, m);
            if (role == MachineRole.InRepair)
            {
                return TrySendToRepair(s, m, out message);
            }
            bool resumeGarrison = role == MachineRole.Garrison && (GarrisonSuspended(s, m) || GarrisonBlocked(s, m));
            if (current == role && !resumeGarrison)
            {
                message = GameText.Format("roster.err.same", who, RoleName(role));
                return false;
            }
            HomeValleyWorkOrders.ReleaseForRoleChange(s, logicId, role);
            m.Role = role;
            m.RoleOrderId = null;
            ResetPatrolSkips(m); // FG6-DEF-06 审查修复：重设驻防 / 改岗位 = 到不了的巡逻点马上再试
            Revision++;
            HomeValleyWorkOrders.MarkAssignmentDirty();
            GuidanceHooks.Raise(GuidanceHooks.RosterFirstRoleChange);
            message = GameText.Format("roster.ok.role", who, RoleName(role));
            return true;
        }

        private static bool TrySendToRepair(CampaignState s, MachineRecord m, out string message)
        {
            string who = MachineNaming.Short(m);
            if (m.MaxHealth <= 0f || m.Health >= m.MaxHealth - 0.0001f)
            {
                message = GameText.Format("roster.err.not_injured", who);
                return false;
            }
            if (RepairOrder(s, m) != null)
            {
                message = GameText.Format("roster.err.same", who, RoleName(MachineRole.InRepair));
                return false;
            }
            BuildingRecord bay = StandingRuleService.PickBay(s, m);
            if (bay == null)
            {
                message = GameText.Get("roster.err.no_bay");
                return false;
            }
            // 基础 ID；暂停中时钟不走，同一步里反复送修会重名——开单时撞号追加序号（HomeValleyWorkOrders.FreshOrderId），以返回的 ID 为准（审查 P0）。
            string id = "roster-repair-" + m.LogicId.ToString(CultureInfo.InvariantCulture) + "-" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture);
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateMachineRepair(s, id, m.LogicId, bay.BuildingId, 0, HomeValleyWorkOrders.RosterIssuer);
            if (!r.Success)
            {
                message = HomeValleyWorkOrders.DescribeCommandFailure(r.FailureReason);
                return false;
            }
            m.RoleOrderId = r.WorkOrderId;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.RosterFirstRoleChange);
            message = GameText.Format("roster.ok.repair", who, StandingRuleService.BuildingLabel(s, bay.BuildingId), RoleName(SanitizeStoredRole(m.Role)));
            return true;
        }

        /// <summary>批量改岗位（名册勾选）。返回改成功的台数；<paramref name="message"/> 汇总（“3 台改为「闲置」，1 台没改：#4 正在远征……”）。</summary>
        public static int TrySetRoles(CampaignState s, IReadOnlyList<int> logicIds, MachineRole role, out string message)
        {
            if (logicIds == null || logicIds.Count == 0)
            {
                message = GameText.Get("roster.err.batch_empty");
                return 0;
            }
            int max = Math.Max(1, Grid.GridContent.TryGetTuning("roster.batch_max", out float v) ? (int)Math.Round(v) : 64);
            if (logicIds.Count > max)
            {
                message = GameText.Format("roster.err.batch_too_many", max);
                return 0;
            }
            int changed = 0;
            var reasons = new List<string>();
            var ids = new List<int>(logicIds);
            ids.Sort();
            foreach (int id in ids)
            {
                if (TrySetRole(s, id, role, out string why))
                {
                    changed++;
                }
                else
                {
                    reasons.Add(why);
                }
            }
            string sep = GameText.Get("rules.sep_list");
            message = reasons.Count == 0
                ? GameText.Format("roster.ok.batch", changed, RoleName(role))
                : GameText.Format("roster.ok.batch_partial", changed, RoleName(role), reasons.Count, string.Join(sep, reasons));
            return changed;
        }

        /// <summary>驻防点（建筑 ID；空 = 归还核心）。驻防岗且有在办的名册驻防单时，按新的点重新派。</summary>
        public static bool TrySetGarrisonPoint(CampaignState s, int logicId, string buildingId, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || s == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            string point = string.IsNullOrEmpty(buildingId) || Grid.HomeGridService.FindBuilding(s, buildingId) == null ? null : buildingId;
            if (string.Equals(m.RolePointId ?? string.Empty, point ?? string.Empty, StringComparison.Ordinal))
            {
                message = GameText.Get("roster.name.same");
                return false;
            }
            m.RolePointId = point;
            ResetPatrolSkips(m);
            if (GarrisonBlocked(s, m))
            {
                m.RoleOrderId = null; // 原来的驻防点到不了而暂停：换了点就按新点再试一次（审查 P1）。
            }
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(s, logicId);
            if (o != null && o.Kind == WorkOrderKind.Garrison && o.IssuerId == HomeValleyWorkOrders.RosterIssuer)
            {
                HomeValleyWorkOrders.ReleaseForRoleChange(s, logicId, MachineRole.Labor); // 结束旧的驻防单（原因“改岗位”），维持器下一次评估按新点再派。
            }
            Revision++;
            HomeValleyWorkOrders.MarkAssignmentDirty();
            message = GameText.Format("roster.ok.point", MachineNaming.Short(m), GarrisonPointLabel(s, m));
            return true;
        }

        // ── FG6-DEF-06（FGR-DEF-043“按玩家画的巡逻路线巡逻”，承接 DEBT-FG4ECO07-02）：驻防巡逻路线 ─────────────────────

        /// <summary>“到不了的巡逻点先跳过”的通知条数（自检：每个点只发一次）。</summary>
        public static int PatrolSkipNotices { get; private set; }

        public static int PatrolMaxPoints => Math.Max(1, (int)Math.Round(Grid.GridContent.TryGetTuning("roster.patrol_max_points", out float v) ? v : 6f));

        /// <summary>巡逻点要是家园里建成的建筑（不是规划虚影 / 搬迁虚影 / 已摧毁的废墟）。</summary>
        private static bool PatrolPointUsable(CampaignState s, string buildingId)
        {
            BuildingRecord b = string.IsNullOrEmpty(buildingId) ? null : Grid.HomeGridService.FindBuilding(s, buildingId);
            return b != null && b.RegionId == HomeValleyLayout.RegionId && !Grid.HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b)
                   && b.ConstructionState != BuildingConstructionState.Damaged;
        }

        public static float PatrolArriveSlack => Math.Max(0f, Grid.GridContent.TryGetTuning("roster.patrol_arrive_slack", out float v) ? v : 1.5f);
        public static float PatrolRetrySeconds => Math.Max(1f, Grid.GridContent.TryGetTuning("roster.patrol_retry_seconds", out float v) ? v : 60f);

        /// <summary>
        /// FG6-DEF-06 审查修复（P1）：这个巡逻点是不是因为到不了而正在跳过（跳过后 roster.patrol_retry_seconds 游戏秒内不去；之后再试一次）。
        /// 只看步序号（与观察无关）。O(跳过表长度) ≤ 巡逻点上限。
        /// </summary>
        public static bool PatrolSkipped(MachineRecord m, string buildingId)
        {
            string[] ids = m?.PatrolSkipIds;
            long[] ticks = m?.PatrolSkipTicks;
            if (ids == null || ticks == null || string.IsNullOrEmpty(buildingId))
            {
                return false;
            }
            long window = (long)Math.Round(PatrolRetrySeconds * GameClock.StepHz);
            for (int i = 0; i < ids.Length && i < ticks.Length; i++)
            {
                if (ids[i] == buildingId)
                {
                    return GameClock.Ticks - ticks[i] < window;
                }
            }
            return false;
        }

        /// <summary>记下“到不了，先跳过”。返回 true = 这一点第一次进跳过表（该发通知）；已经在表里（重试又失败）只刷新时间、返回 false（不再通知）。</summary>
        public static bool MarkPatrolSkipped(MachineRecord m, string buildingId)
        {
            if (m == null || string.IsNullOrEmpty(buildingId))
            {
                return false;
            }
            string[] ids = m.PatrolSkipIds ?? Array.Empty<string>();
            long[] ticks = m.PatrolSkipTicks != null && m.PatrolSkipTicks.Length == ids.Length ? m.PatrolSkipTicks : new long[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == buildingId)
                {
                    ticks[i] = GameClock.Ticks;
                    m.PatrolSkipTicks = ticks;
                    return false;
                }
            }
            var nids = new string[ids.Length + 1];
            var nticks = new long[ids.Length + 1];
            Array.Copy(ids, nids, ids.Length);
            Array.Copy(ticks, nticks, ids.Length);
            nids[ids.Length] = buildingId;
            nticks[ids.Length] = GameClock.Ticks;
            m.PatrolSkipIds = nids;
            m.PatrolSkipTicks = nticks;
            return true;
        }

        /// <summary>机器走到了这个巡逻点（驻防单进行中）：移出跳过表——以后再到不了会重新通知一次。表空时 O(1)。</summary>
        public static void NotePatrolReached(MachineRecord m, string buildingId)
        {
            string[] ids = m?.PatrolSkipIds;
            if (ids == null || ids.Length == 0 || string.IsNullOrEmpty(buildingId))
            {
                return;
            }
            int at = Array.IndexOf(ids, buildingId);
            if (at < 0)
            {
                return;
            }
            if (ids.Length == 1)
            {
                ResetPatrolSkips(m);
                return;
            }
            var nids = new List<string>(ids);
            var nticks = new List<long>(m.PatrolSkipTicks ?? new long[ids.Length]);
            nids.RemoveAt(at);
            if (at < nticks.Count)
            {
                nticks.RemoveAt(at);
            }
            m.PatrolSkipIds = nids.ToArray();
            m.PatrolSkipTicks = nticks.ToArray();
        }

        public static void ResetPatrolSkips(MachineRecord m)
        {
            if (m != null)
            {
                m.PatrolSkipIds = null;
                m.PatrolSkipTicks = null;
            }
        }

        /// <summary>这张单是不是名册为巡逻路线上的某个巡逻点（不是驻防点）开的驻防单（失败通知据此改由维持器发巡逻专用的一条，只发一次）。</summary>
        public static bool IsPatrolLeg(MachineRecord m, WorkOrderRecord o)
        {
            return m != null && o != null && o.Kind == WorkOrderKind.Garrison && o.IssuerId == HomeValleyWorkOrders.RosterIssuer && m.PatrolIndex > 0
                   && m.PatrolPoints != null && m.RoleOrderId == o.WorkOrderId && !string.IsNullOrEmpty(o.TargetId) && Array.IndexOf(m.PatrolPoints, o.TargetId) >= 0;
        }

        /// <summary>
        /// 给驻防机器的巡逻路线末尾加一个巡逻点（可逆：可以清空重设，不弹确认，B04）。路线 = 驻防点 → 巡逻点… → 回驻防点循环；
        /// 敌人进入守点半径（siege.guard_radius_cells，以当前去的点为中心）就地交战，打完接着巡逻（内核守点交战，FG6-DEF-05）。
        /// </summary>
        public static bool TryAddPatrolPoint(CampaignState s, int logicId, string buildingId, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || s == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            if (m.Role != MachineRole.Garrison || !IsHome(m) || !m.IsAlive)
            {
                message = GameText.Get("roster.err.patrol_not_garrison");
                return false;
            }
            if (!PatrolPointUsable(s, buildingId))
            {
                message = GameText.Get("roster.err.patrol_bad_point");
                return false;
            }
            string[] cur = m.PatrolPoints ?? Array.Empty<string>();
            if (cur.Length >= PatrolMaxPoints)
            {
                message = GameText.Format("roster.err.patrol_full", PatrolMaxPoints);
                return false;
            }
            if (cur.Length > 0 && cur[cur.Length - 1] == buildingId)
            {
                message = GameText.Format("roster.err.patrol_dup", StandingRuleService.BuildingLabel(s, buildingId));
                return false;
            }
            var next = new string[cur.Length + 1];
            Array.Copy(cur, next, cur.Length);
            next[cur.Length] = buildingId;
            m.PatrolPoints = next;
            ResetPatrolSkips(m);
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.RosterFirstPatrol);
            message = GameText.Format("roster.ok.patrol_add", MachineNaming.Short(m), StandingRuleService.BuildingLabel(s, buildingId), next.Length);
            return true;
        }

        /// <summary>清空巡逻路线：机器回到守驻防点。</summary>
        public static bool TryClearPatrol(CampaignState s, int logicId, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            if (m.PatrolPoints == null || m.PatrolPoints.Length == 0)
            {
                message = GameText.Get("roster.patrol.none");
                return false;
            }
            bool onLeg = m.PatrolIndex > 0;
            m.PatrolPoints = null;
            m.PatrolIndex = 0;
            ResetPatrolSkips(m);
            if (onLeg && !string.IsNullOrEmpty(m.RoleOrderId))
            {
                // 正走在去巡逻点的路上 / 守在巡逻点：结束这张单，维持器按驻防点再派（回到守点）。
                WorkOrderRecord o = HomeValleyWorkOrders.Find(s, m.RoleOrderId);
                if (o != null && o.Kind == WorkOrderKind.Garrison && o.IssuerId == HomeValleyWorkOrders.RosterIssuer)
                {
                    HomeValleyWorkOrders.EndRuleOrder(s, o.WorkOrderId);
                    m.RoleOrderId = null;
                }
            }
            Revision++;
            HomeValleyWorkOrders.MarkAssignmentDirty();
            message = GameText.Format("roster.ok.patrol_clear", MachineNaming.Short(m));
            return true;
        }

        /// <summary>这台机器有没有可用的巡逻路线（至少一个现在还用得了、没在“到不了先跳过”期间的巡逻点）。全部被拆 / 被毁 / 跳过时 = 守驻防点（不反复结束、重派驻防单）。</summary>
        public static bool HasPatrol(CampaignState s, MachineRecord m)
        {
            if (m?.PatrolPoints == null)
            {
                return false;
            }
            foreach (string p in m.PatrolPoints)
            {
                if (PatrolPointUsable(s, p) && !PatrolSkipped(m, p))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 巡逻路线上第 <paramref name="index"/> 个点的位置（0 = 驻防点；巡逻点被拆 / 被毁 / 到不了先跳过时跳到下一个可用的点）。返回规范化后的序号（-1 = 没有可用路线）。
        /// </summary>
        public static int PatrolTarget(CampaignState s, MachineRecord m, int index, out Vector2 position, out string buildingId)
        {
            position = Vector2.zero;
            buildingId = null;
            string[] pts = m?.PatrolPoints;
            if (pts == null || pts.Length == 0)
            {
                return -1;
            }
            int n = pts.Length + 1;
            for (int k = 0; k < n; k++)
            {
                int i = ((index + k) % n + n) % n;
                string id = i == 0 ? (string.IsNullOrEmpty(m.RolePointId) || Grid.HomeGridService.FindBuilding(s, m.RolePointId) == null ? null : m.RolePointId) : pts[i - 1];
                if (i == 0 && id == null)
                {
                    BuildingRecord core = null;
                    foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
                    {
                        if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                        {
                            core = b;
                            break;
                        }
                    }
                    if (core != null)
                    {
                        position = core.Position;
                        buildingId = core.BuildingId;
                        return i;
                    }
                    continue;
                }
                if (i > 0 && (!PatrolPointUsable(s, id) || PatrolSkipped(m, id)))
                {
                    continue; // 被拆 / 被毁 / 到不了先跳过
                }
                BuildingRecord t = Grid.HomeGridService.FindBuilding(s, id);
                if (t == null)
                {
                    continue;
                }
                position = t.Position;
                buildingId = t.BuildingId;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// 岗位维持器为巡逻路线上正在去的那一点开驻防单用的点（建筑 ID；空 = 归还核心）：第 0 个 = 驻防点（与不巡逻时同一口径），之后 = 巡逻点；被拆的跳过。
        /// </summary>
        public static string PatrolOrderPoint(CampaignState s, MachineRecord m)
        {
            int i = PatrolTarget(s, m, m?.PatrolIndex ?? 0, out _, out string id);
            if (i <= 0)
            {
                return string.IsNullOrEmpty(m?.RolePointId) || Grid.HomeGridService.FindBuilding(s, m.RolePointId) == null ? string.Empty : m.RolePointId;
            }
            return id ?? string.Empty;
        }

        /// <summary>巡逻路线的文字：“核心 → 仓库 #3 → 炮塔 #2（按顺序循环……）”或“不巡逻：守在驻防点”。</summary>
        public static string PatrolText(CampaignState s, MachineRecord m)
        {
            if (m?.PatrolPoints == null || m.PatrolPoints.Length == 0)
            {
                return GameText.Get("roster.patrol.none");
            }
            var parts = new List<string>(m.PatrolPoints.Length + 1) { GarrisonPointLabel(s, m) };
            foreach (string p in m.PatrolPoints)
            {
                parts.Add(StandingRuleService.BuildingLabel(s, p));
            }
            return GameText.Format("roster.patrol.route", string.Join(GameText.Get("roster.patrol.sep"), parts));
        }

        public static string GarrisonPointLabel(CampaignState s, MachineRecord m) =>
            StandingRuleService.BuildingLabel(s, string.IsNullOrEmpty(m?.RolePointId) || Grid.HomeGridService.FindBuilding(s, m.RolePointId) == null
                ? HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeCore
                : m.RolePointId);

        /// <summary>
        /// 岗位维持器（由 <c>HomeValleyWorkOrders.TickAssignment</c> 在派工评估时调用，观察 / 不观察一样）：驻防岗、在家园、活着、不在厂内、玩家没在用、
        /// 在信号覆盖内、没有在办工单、没被玩家接管过的机器——开一张名册驻防单去驻防点。O(机器数)，只有驻防岗的机器才查一次工单。
        /// </summary>
        public static int KeepRoles(CampaignState s, Func<int, bool> takenByPlayer)
        {
            if (s == null)
            {
                return 0;
            }
            int issued = 0;
            List<MachineRecord> garrison = null;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.Role == MachineRole.Garrison && m.IsAlive && IsHome(m) && !m.IsInFactory)
                {
                    (garrison ??= new List<MachineRecord>()).Add(m);
                }
            }
            if (garrison == null)
            {
                return 0;
            }
            garrison.Sort((a, b) => a.LogicId.CompareTo(b.LogicId));
            foreach (MachineRecord m in garrison)
            {
                if ((takenByPlayer != null && takenByPlayer(m.LogicId)) || Signal.SignalCoverageService.IsMachineOutOfCoverage(m.LogicId)
                    || HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId) != null || GarrisonSuspended(s, m))
                {
                    continue;
                }
                if (m.PatrolPoints != null && m.PatrolPoints.Length > 0 && m.PatrolIndex > 0 && GarrisonBlocked(s, m, out WorkOrderRecord legFailed))
                {
                    // FG6-DEF-06：巡逻路线上的某个巡逻点到不了——跳过它去下一个点，不让整条巡逻停在“驻防受阻”。驻防点本身到不了仍按原来的受阻处理。
                    // 审查修复（P1）：跳过的点记进存档（roster.patrol_retry_seconds 内不再去，免得每绕一圈开单 / 失败一次），通知每个点只发一次（直到走到过它）；
                    // 判失败时的通用“去不了”通知对巡逻腿不发（HomeValleyWorkOrders.NotifySelfTripFailed），由这里发巡逻专用的一条（不再附驻防的恢复提示）。
                    string skipped = !string.IsNullOrEmpty(legFailed.TargetId) && Array.IndexOf(m.PatrolPoints, legFailed.TargetId) >= 0 ? legFailed.TargetId : PatrolOrderPoint(s, m);
                    legFailed.UnreachableNotified = true;
                    bool first = MarkPatrolSkipped(m, skipped);
                    m.PatrolIndex = PatrolTarget(s, m, m.PatrolIndex + 1, out _, out _);
                    m.RoleOrderId = null;
                    if (first)
                    {
                        PatrolSkipNotices++;
                        Vector2 at = HomeValleyWorkOrders.ResolveWorkPosition(s, legFailed);
                        GameLogic.Notifications.NotificationCenter.Post("unreachable", GameText.Format("roster.patrol.skipped", MachineNaming.Short(m),
                            StandingRuleService.BuildingLabel(s, skipped), TripFailWhy(s, legFailed), Mathf.RoundToInt(PatrolRetrySeconds)), new Vector3(at.x, 0f, at.y));
                    }
                    if (m.PatrolIndex < 0)
                    {
                        m.PatrolIndex = 0;
                    }
                }
                if (GarrisonBlocked(s, m, out WorkOrderRecord failed))
                {
                    // 审查修复（P1）：上一张驻防单到不了驻防点而失败——不再补派（否则每次评估一张新单、每张一条通知），只通知一次（标记随存档保留）。
                    // 两种失败的通知都在判失败时发过了（OnWorkUnreachable / 赶路看门狗，HomeValleyWorkOrders.NotifySelfTripFailed）；
                    // 这里只兜底没发过的（例如旧存档里已失败、标记为 false 的单），写明怎么恢复。
                    if (!failed.UnreachableNotified)
                    {
                        failed.UnreachableNotified = true;
                        Vector2 at = HomeValleyWorkOrders.ResolveWorkPosition(s, failed);
                        GameLogic.Notifications.NotificationCenter.Post("unreachable", GameText.Format("roster.garrison.blocked_note", MachineNaming.Short(m), GarrisonBlockedText(s, m, failed)),
                            new Vector3(at.x, 0f, at.y));
                    }
                    continue;
                }
                bool patrol = HasPatrol(s, m);
                if (!patrol && m.PatrolIndex != 0)
                {
                    m.PatrolIndex = 0; // 巡逻点全部被拆 / 被毁 / 跳过：回到守驻防点，路线恢复后从驻防点重新开始
                }
                string point = patrol ? PatrolOrderPoint(s, m) // FG6-DEF-06：巡逻中 = 路线上正在去的那一点
                    : string.IsNullOrEmpty(m.RolePointId) || Grid.HomeGridService.FindBuilding(s, m.RolePointId) == null ? string.Empty : m.RolePointId;
                // 基础 ID；同一步里的重名由 HomeValleyWorkOrders.FreshOrderId 追加序号，以返回的 ID 为准（审查 P0）。
                string id = "roster-garrison-" + m.LogicId.ToString(CultureInfo.InvariantCulture) + "-" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture);
                HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateGarrison(s, id, m.LogicId, point, 0, HomeValleyWorkOrders.RosterIssuer);
                if (r.Success)
                {
                    m.RoleOrderId = r.WorkOrderId;
                    issued++;
                }
            }
            return issued;
        }

        // ── 劳动力（顶栏、远征准备）──────────────────────────────────────────────

        public readonly struct LaborCount
        {
            /// <summary>家园里岗位为劳动、活着、不在厂内的机器数。</summary>
            public readonly int Labor;
            /// <summary>其中手上有在办工单的台数。</summary>
            public readonly int Busy;

            public LaborCount(int labor, int busy)
            {
                Labor = labor;
                Busy = busy;
            }

            public int BusyPercent => Labor <= 0 ? 0 : (int)Math.Round(Busy * 100.0 / Labor);
        }

        private static readonly HashSet<int> BusyScratch = new HashSet<int>();
        private static LaborCount _cached;
        private static double _cachedAt = double.NegativeInfinity;
        private static bool _cacheValid;
        private static int _cachedRosterRevision;
        private static int _cachedRevision;
        private static CampaignState _cachedState;

        /// <summary>现算（O(机器数 + 工单数)）。<paramref name="leaving"/>：假设这些机器离开家园（远征准备的预估）。</summary>
        public static LaborCount ComputeLabor(CampaignState s, ICollection<int> leaving = null)
        {
            if (s == null)
            {
                return default;
            }
            BusyScratch.Clear();
            foreach (WorkOrderRecord o in s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.AssignedMachineLogicId > 0 && HomeValleyWorkOrders.IsActive(o))
                {
                    BusyScratch.Add(o.AssignedMachineLogicId);
                }
            }
            int labor = 0;
            int busy = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || !IsHome(m) || m.IsInFactory || !TakesLabor(m) || (leaving != null && leaving.Contains(m.LogicId)))
                {
                    continue;
                }
                labor++;
                if (BusyScratch.Contains(m.LogicId))
                {
                    busy++;
                }
            }
            return new LaborCount(labor, busy);
        }

        /// <summary>顶栏用：机器名册 / 岗位变化时立刻重算，否则最多每 roster.labor_refresh_seconds 真实秒重算一次（不每帧 O(机器数)）。</summary>
        public static LaborCount Labor(CampaignState s)
        {
            // 审查修复（P2）：缓存键改为整数版本号 + 战役引用比较——顶栏每帧调用，不再每帧拼字符串（热更层每帧零分配）。
            double now = Clock();
            int rosterRevision = MachineRegistry.RosterRevision;
            double every = Math.Max(0.1, Grid.GridContent.TryGetTuning("roster.labor_refresh_seconds", out float v) ? v : 1.0);
            if (_cacheValid && rosterRevision == _cachedRosterRevision && Revision == _cachedRevision && ReferenceEquals(s, _cachedState) && now - _cachedAt < every)
            {
                return _cached;
            }
            _cached = ComputeLabor(s);
            _cachedAt = now;
            _cacheValid = true;
            _cachedRosterRevision = rosterRevision;
            _cachedRevision = Revision;
            _cachedState = s;
            return _cached;
        }

        public static string LaborBarText(LaborCount c) => GameText.Format("roster.labor.bar", c.Labor, c.Busy, c.BusyPercent);

        /// <summary>
        /// 远征准备的提示（FGR-ECO-042）：“出发后家园劳动力 5 → 2，施工速度约下降 60%”。施工速度按劳动岗台数等比例估算（每台机器一次只做一张单）。
        /// </summary>
        public static string LaborForecastText(CampaignState s, IReadOnlyCollection<int> selected)
        {
            var leaving = new HashSet<int>(selected ?? Array.Empty<int>());
            LaborCount before = ComputeLabor(s);
            LaborCount after = ComputeLabor(s, leaving);
            if (before.Labor == after.Labor)
            {
                return GameText.Format("roster.labor.forecast_same", before.Labor);
            }
            if (after.Labor == 0)
            {
                return GameText.Format("roster.labor.forecast", before.Labor, 0, 100) + GameText.Get("rules.sep_dot") + GameText.Get("roster.labor.forecast_none");
            }
            return GameText.Format("roster.labor.forecast", before.Labor, after.Labor, ForecastDropPercent(before.Labor, after.Labor));
        }

        public static int ForecastDropPercent(int before, int after) => before <= 0 ? 0 : (int)Math.Round((before - after) * 100.0 / before);

        /// <summary>家园里岗位为“远征预备”的活机器（远征准备面板打开时自动勾选），按编号。</summary>
        public static List<int> ReserveIds(CampaignState s)
        {
            var list = new List<MachineRecord>();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && IsHome(m) && !m.IsInFactory && m.Role == MachineRole.ExpeditionReserve)
                {
                    list.Add(m);
                }
            }
            list.Sort((a, b) => a.DisplayNumber.CompareTo(b.DisplayNumber));
            return list.ConvertAll(m => m.LogicId);
        }

        // ── 名册查询（排序、筛选）──────────────────────────────────────────────

        public enum Status
        {
            Working = 0,
            Waiting = 1,
            Uplinked = 2,
            InFactory = 3,
            Away = 4,
            Dead = 5,
        }

        public enum StatusFilter
        {
            All = 0,
            Active = 1,
            Working = 2,
            Waiting = 3,
            Away = 4,
            Dead = 5,
        }

        public enum SortKey
        {
            Number = 0,
            Name = 1,
            Role = 2,
            Chassis = 3,
            Status = 4,
            Injury = 5,
            Experience = 6,
        }

        public static readonly SortKey[] AllSortKeys = { SortKey.Number, SortKey.Name, SortKey.Role, SortKey.Chassis, SortKey.Status, SortKey.Injury, SortKey.Experience };
        public static readonly StatusFilter[] AllStatusFilters = { StatusFilter.All, StatusFilter.Active, StatusFilter.Working, StatusFilter.Waiting, StatusFilter.Away, StatusFilter.Dead };

        public static Status StatusOf(CampaignState s, MachineRecord m)
        {
            if (!m.IsAlive)
            {
                return Status.Dead;
            }
            if (!IsHome(m))
            {
                return Status.Away;
            }
            if (m.IsInFactory)
            {
                return Status.InFactory;
            }
            if (Signal.SignalUplinkService.IsUplinked(s, m.LogicId))
            {
                return Status.Uplinked;
            }
            return HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId) != null ? Status.Working : Status.Waiting;
        }

        public static string StatusName(Status st)
        {
            switch (st)
            {
                case Status.Working: return GameText.Get("roster.status.working");
                case Status.Waiting: return GameText.Get("roster.status.waiting");
                case Status.Uplinked: return GameText.Get("roster.status.uplinked");
                case Status.InFactory: return GameText.Get("roster.status.in_factory");
                case Status.Away: return GameText.Get("roster.status.away");
                default: return GameText.Get("roster.status.dead");
            }
        }

        public static string StatusFilterName(StatusFilter f)
        {
            switch (f)
            {
                case StatusFilter.All: return GameText.Get("roster.panel.all");
                case StatusFilter.Active: return GameText.Get("roster.status.active");
                case StatusFilter.Working: return GameText.Get("roster.status.working");
                case StatusFilter.Waiting: return GameText.Get("roster.status.waiting");
                case StatusFilter.Away: return GameText.Get("roster.status.away");
                default: return GameText.Get("roster.status.dead");
            }
        }

        public static string SortKeyName(SortKey k)
        {
            switch (k)
            {
                case SortKey.Name: return GameText.Get("roster.sort.name");
                case SortKey.Role: return GameText.Get("roster.sort.role");
                case SortKey.Chassis: return GameText.Get("roster.sort.chassis");
                case SortKey.Status: return GameText.Get("roster.sort.status");
                case SortKey.Injury: return GameText.Get("roster.sort.injury");
                case SortKey.Experience: return GameText.Get("roster.sort.experience");
                default: return GameText.Get("roster.sort.number");
            }
        }

        private static bool Matches(StatusFilter f, Status st)
        {
            switch (f)
            {
                case StatusFilter.All: return true;
                case StatusFilter.Active: return st != Status.Dead;
                case StatusFilter.Working: return st == Status.Working || st == Status.Uplinked;
                case StatusFilter.Waiting: return st == Status.Waiting;
                case StatusFilter.Away: return st == Status.Away;
                default: return st == Status.Dead;
            }
        }

        /// <summary>伤势百分比（0～100；阵亡 100）。</summary>
        public static int InjuryPercent(MachineRecord m) => m == null ? 0 : !m.IsAlive ? 100 : StandingRuleService.InjuryPercent(m);

        /// <summary>“经历”排序：经历条数 → 远征次数 → 击杀 → 工作次数（都是越多越“老练”）。</summary>
        private static int CompareExperience(MachineRecord a, MachineRecord b)
        {
            int c = (a.ExperienceFlags?.Length ?? 0).CompareTo(b.ExperienceFlags?.Length ?? 0);
            if (c != 0)
            {
                return c;
            }
            c = a.ExpeditionsCompleted.CompareTo(b.ExpeditionsCompleted);
            if (c != 0)
            {
                return c;
            }
            c = a.KillCount.CompareTo(b.KillCount);
            return c != 0 ? c : a.JobsCompleted.CompareTo(b.JobsCompleted);
        }

        private static int RoleOrder(MachineRole r)
        {
            RosterRole row = Row(r);
            return row != null ? row.SortOrder : (int)r;
        }

        /// <summary>
        /// 名册列表：按岗位（<paramref name="role"/> 为 null = 全部）、底盘（null / 空 = 全部）、状态筛选，按 <paramref name="key"/> 排序（同值按编号升序，结果确定）。
        /// O(n log n)，只在名册刷新时调用。
        /// </summary>
        public static List<MachineRecord> Query(CampaignState s, MachineRole? role, string chassisId, StatusFilter status, SortKey key, bool descending)
        {
            var list = new List<MachineRecord>();
            var roles = new Dictionary<int, MachineRole>();
            var states = new Dictionary<int, Status>();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null)
                {
                    continue;
                }
                Status st = StatusOf(s, m);
                if (!Matches(status, st))
                {
                    continue;
                }
                MachineRole er = EffectiveRole(s, m);
                if (role.HasValue && (er != role.Value || !m.IsAlive))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(chassisId) && m.ChassisId != chassisId)
                {
                    continue;
                }
                roles[m.LogicId] = er;
                states[m.LogicId] = st;
                list.Add(m);
            }
            Comparison<MachineRecord> primary;
            switch (key)
            {
                case SortKey.Name:
                    primary = (a, b) => string.Compare(MachineNaming.Long(a), MachineNaming.Long(b), StringComparison.OrdinalIgnoreCase);
                    break;
                case SortKey.Role:
                    primary = (a, b) => RoleOrder(roles[a.LogicId]).CompareTo(RoleOrder(roles[b.LogicId]));
                    break;
                case SortKey.Chassis:
                    primary = (a, b) => string.CompareOrdinal(a.ChassisId ?? string.Empty, b.ChassisId ?? string.Empty);
                    break;
                case SortKey.Status:
                    primary = (a, b) => states[a.LogicId].CompareTo(states[b.LogicId]);
                    break;
                case SortKey.Injury:
                    primary = (a, b) => InjuryPercent(a).CompareTo(InjuryPercent(b));
                    break;
                case SortKey.Experience:
                    primary = CompareExperience;
                    break;
                default:
                    primary = (a, b) => a.DisplayNumber.CompareTo(b.DisplayNumber);
                    break;
            }
            list.Sort((a, b) =>
            {
                int c = primary(a, b);
                if (descending)
                {
                    c = -c;
                }
                return c != 0 ? c : a.DisplayNumber.CompareTo(b.DisplayNumber);
            });
            return list;
        }

        /// <summary>名册里出现过的底盘（筛选下拉框），按编号排序。</summary>
        public static List<string> ChassisIds()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && !string.IsNullOrEmpty(m.ChassisId))
                {
                    set.Add(m.ChassisId);
                }
            }
            return new List<string>(set);
        }

        /// <summary>底盘的玩家可见名（“战斗履带”）；查不到时用型号。</summary>
        public static string ChassisLabel(string chassisId)
        {
            string label = MechanicalContentFacade.ResolveChassisLabel(chassisId);
            return string.IsNullOrEmpty(label) || label == chassisId ? (chassisId ?? string.Empty).ToUpperInvariant().Replace('_', '-') : label;
        }

        /// <summary>这台机器手上的工作（详情页 “当前：…”）：规则 / 名册派的写来源，其余写工单类型与目标。</summary>
        public static string CurrentWorkText(CampaignState s, MachineRecord m)
        {
            WorkOrderRecord o = s == null || m == null ? null : HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId);
            if (o == null)
            {
                if (m != null && GarrisonSuspended(s, m))
                {
                    return GameText.Get("roster.garrison.suspended");
                }
                return m != null && GarrisonBlocked(s, m, out WorkOrderRecord failed) ? GarrisonBlockedText(s, m, failed) : GameText.Get("roster.detail.order_none");
            }
            string trace = StandingRuleService.DescribeOrder(s, o);
            if (!string.IsNullOrEmpty(trace))
            {
                return trace;
            }
            return StandingRuleService.KindTextOf(o.Kind) + GameText.Get("rules.sep_dot") + HomeValleyWorkOrders.DescribeTarget(s, o);
        }

        public static void ResetForTests()
        {
            HasOutpostProvider = null;
            Clock = () => Time.realtimeSinceStartupAsDouble;
            _cacheValid = false;
            _cachedState = null;
            _cachedAt = double.NegativeInfinity;
            Reload();
        }
    }
}
