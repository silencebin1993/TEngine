using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-015；FG04 3.5 自动重建；FGT-DEF-008；承接 DEBT-FG4ECO06-05 / DEBT-FG3LOG03-06）：自动重建按区域开关、覆盖传送带与物流节点、补排已经被摧毁的东西、研发门控。
    /// - 重建区域（<see cref="RebuildZoneRecord"/>）：自动重建规则可以圈出若干格网矩形，每个区域单独开 / 关。规则没有区域 = 整个家园；有区域 = 只重建落在开着的区域里的东西
    ///   （建筑按占地与区域相交判定；传送带按那一格）。区域全关 = 这条规则暂时什么都不重建（面板写明）。
    /// - 范围：建筑类型（原有）+ “传送带与物流节点”（<see cref="BeltTarget"/>）；空 = 全部（含传送带）。
    /// - 被摧毁的传送带 / 分流器 / 合流器 / 地下传送带（保留设置的虚影）：下一个模拟步按原设置开施工单（与施工队列“重建”同一入口 <see cref="HomeValleyConstruction.RebuildDestroyed"/>），
    ///   之后机器取料施工；缺料时虚影等料（施工系统的“等待材料”），不会重复扣料。
    /// - 补排（<see cref="SweepRebuild"/>）：玩家启用规则、改范围、圈 / 打开区域之后，下一个模拟步把已经被摧毁、现在落在范围里的东西排进重建（玩家的显式操作，FGR-BASE-020）。
    /// - 研发门控：规则类型“自动重建”由研发节点 defense.auto_rebuild 解锁（fg.TbResearchNode.unlocks = rule:auto_rebuild）；没研究时不能新建 / 启用，原因写明；
    ///   旧存档里已经启用的规则照常运转（不替玩家停掉，待用户复核）。
    /// 只在世界模拟步里执行（与观察无关，FGR-BASE-021）；每个动作都写“由规则 Rn 触发”（工单 RuleSerial + 触发日志）。
    /// </summary>
    public static partial class StandingRuleService
    {
        /// <summary>自动重建范围里的“传送带与物流节点”（不是建筑类型 ID）。</summary>
        public const string BeltTarget = "@belts";

        private const string EventBeltGhost = "belt_ghost";

        /// <summary>EMG-R 出口补缺口（FG-GAP-224）：自动重建在等火灭（规则原因键）。</summary>
        public const string IssueRebuildFire = "rules.issue.rebuild_fire";

        public static int ZoneMax => Math.Max(1, GridContent.TuningInt("rules.zone.max"));
        public static int ZoneMinCells => Math.Max(1, GridContent.TuningInt("rules.zone.min_cells"));

        /// <summary>自检读：补排执行的次数与最近一次排进的件数（本会话）。</summary>
        public static int SweepCount { get; private set; }
        public static int LastSweepQueued { get; private set; }

        // ── 研发门控 ─────────────────────────────────────────────────────────────

        /// <summary>解锁这类规则的研究节点（没有门控 = null）。</summary>
        public static string KindGateNode(string kind) => ResearchCatalog.GateOf("rule:" + kind)?.Id;

        public static bool IsKindLocked(CampaignState state, string kind)
        {
            string node = KindGateNode(kind);
            return node != null && !ResearchGate.IsCompleted(state, node);
        }

        /// <summary>这类规则还没解锁时的原因（“自动重建”还没解锁：先在研发树研究「防御 · 自动重建」）；解锁了 = null。</summary>
        public static string KindLockedMessage(CampaignState state, string kind)
        {
            string node = KindGateNode(kind);
            if (node == null || ResearchGate.IsCompleted(state, node))
            {
                return null;
            }
            RuleKind k = KindRow(kind);
            return GameText.Format("rules.msg.kind_locked", k != null ? GameText.Get(k.NameKey) : kind, ResearchGate.NodeName(node));
        }

        // ── 事件钩子 ─────────────────────────────────────────────────────────────

        /// <summary>一格传送带 / 一个物流节点刚被摧毁、留下了保留设置的虚影（<c>BeltNetworkService</c>）：排给下一个模拟步，自动重建在那里处理。</summary>
        public static void OnBeltGhost(CampaignState state, string planId)
        {
            if (state == null || string.IsNullOrEmpty(planId))
            {
                return;
            }
            Enqueue(state, new RuleEventRecord { Kind = EventBeltGhost, EntityId = planId, Tick = GameClock.Ticks });
        }

        /// <summary>FG7-ENV-03 审查修复（P1）：一批 <see cref="OnBeltGhost"/>（酸雨同一分钟摧毁多格），待处理事件数组只追加一次。</summary>
        public static void OnBeltGhosts(CampaignState state, IReadOnlyList<string> planIds)
        {
            if (state == null || planIds == null || planIds.Count == 0)
            {
                return;
            }
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return;
            }
            var list = new List<RuleEventRecord>((d.Pending?.Length ?? 0) + planIds.Count);
            if (d.Pending != null)
            {
                list.AddRange(d.Pending);
            }
            long tick = GameClock.Ticks;
            foreach (string id in planIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    list.Add(new RuleEventRecord { Kind = EventBeltGhost, EntityId = id, Tick = tick });
                }
            }
            d.Pending = list.ToArray();
        }

        /// <summary>下一个模拟步补排一次（有启用的自动重建规则时才记）。</summary>
        public static void RequestRebuildSweep(CampaignState state)
        {
            StandingRuleState d = Domain(state);
            if (d == null)
            {
                return;
            }
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r != null && r.Enabled && r.Kind == KindRebuild)
                {
                    d.RebuildSweep = true;
                    return;
                }
            }
        }

        // ── 覆盖判定 ─────────────────────────────────────────────────────────────

        /// <summary>建筑的占地外框（格网坐标，含两端）。</summary>
        public static void BoundsOf(BuildingRecord b, out GridCell min, out GridCell max)
        {
            if (b != null && GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g))
            {
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, Mathf.RoundToInt(b.Rotation), out min, out max);
                return;
            }
            GridCell c = b != null ? GridCell.FromWorld(b.Position) : default;
            min = c;
            max = c;
        }

        /// <summary>这条规则覆盖不覆盖这件东西：启用的自动重建规则、类型在范围里（空 = 全部；传送带看 <see cref="BeltTarget"/>）、
        /// 没有区域或与一个开着的区域相交。</summary>
        public static bool Covers(StandingRuleRecord r, string typeId, GridCell min, GridCell max)
        {
            if (r == null || !r.Enabled || r.Kind != KindRebuild)
            {
                return false;
            }
            if (r.Targets.Length > 0 && Array.IndexOf(r.Targets, typeId) < 0)
            {
                return false;
            }
            RebuildZoneRecord[] zones = r.Zones ?? Array.Empty<RebuildZoneRecord>();
            if (zones.Length == 0)
            {
                return true;
            }
            foreach (RebuildZoneRecord z in zones)
            {
                if (z != null && z.Enabled && min.X <= z.X1 && max.X >= z.X0 && min.Y <= z.Y1 && max.Y >= z.Y0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Covers(StandingRuleRecord r, string typeId, (GridCell Min, GridCell Max) box) => Covers(r, typeId, box.Min, box.Max);

        private static (GridCell, GridCell) CellOf(BuildingRecord b)
        {
            BoundsOf(b, out GridCell min, out GridCell max);
            return (min, max);
        }

        private static StandingRuleRecord RebuildRuleFor(CampaignState state, string typeId, (GridCell Min, GridCell Max) box)
        {
            foreach (StandingRuleRecord r in Ordered(state))
            {
                if (Covers(r, typeId, box.Min, box.Max))
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>覆盖这座建筑的第一条自动重建规则编号（0 = 没有）。面板 / 自检读。</summary>
        public static int RebuildRuleSerialFor(CampaignState state, BuildingRecord b) =>
            b == null ? 0 : RebuildRuleFor(state, b.BuildingTypeId, CellOf(b))?.Serial ?? 0;

        // ── 等火灭（EMG-R 出口补缺口，FG-GAP-224）──────────────────────────────────

        /// <summary>
        /// 这片格子此刻还在烧：家园内核里有会烧蚀设施的物质斑块（物质行 structDps &gt; 0：明火 / 酸 / 熔融……按物质数值判，不认物质名）或燃烧液洼盖住其中一格
        /// （与家园对账烧设施同一几何）。自动重建在这里不派单，等火灭——否则机器被派进火里烧掉、刚重建好的又被烧毁，废料白白耗光（FGJ-M6 实测）。
        /// 家园没载入 / 没有内核 = 不在烧。O(斑块数 + 区域数)，在内核里算；只在派单 / 定时重试时调用。
        /// </summary>
        private static bool Burning(GridCell min, GridCell max)
        {
            Combat.CombatSite site = WorldSim.WorldSimulation.Home != null && WorldSim.WorldSimulation.Home.IsLoaded ? WorldSim.WorldSimulation.Home.Combat : null;
            return site != null && site.StructHazardIn(min.X, min.Y, max.X, max.Y); // 经桥接层查内核（FG14 §5 硬约束 3）
        }

        private static bool BurningAt(BuildingRecord b)
        {
            BoundsOf(b, out GridCell min, out GridCell max);
            return Burning(min, max);
        }

        /// <summary>
        /// 传送带虚影里第一格还在烧的下标（-1 = 都不在烧）。逐格查、不用外接矩形：地下传送带虚影是 [入口, 出口] 两格、两端一起重建，
        /// 出口在火里同样不能派单；两端之间的隧道格不施工，不算（FG-GAP-224 审查修复）。
        /// </summary>
        private static int BurningCell(PlannedBeltRecord p)
        {
            for (int i = 0; i < p.Xs.Length && i < (p.Ys?.Length ?? 0); i++)
            {
                var c = new GridCell(p.Xs[i], p.Ys[i]);
                if (Burning(c, c))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>自动重建此刻在等火灭的建筑 / 传送带虚影规划 ID（施工队列与建筑面板写“等火灭”）；没有返回 null。O(待处理事件数)，只在面板刷新时。</summary>
        public static HashSet<string> FireWaitIds(CampaignState state)
        {
            HashSet<string> ids = null;
            foreach (RuleEventRecord e in Domain(state)?.Pending ?? Array.Empty<RuleEventRecord>())
            {
                if (e != null && e.FireWait)
                {
                    (ids ??= new HashSet<string>(StringComparer.Ordinal)).Add(e.EntityId);
                }
            }
            return ids;
        }

        /// <summary>这座建筑正被自动重建规则按“等火灭”挂着：返回规则编号（0 = 没有）。O(待处理事件数)，建筑面板 / 自检读。</summary>
        public static int FireWaitRule(CampaignState state, string entityId)
        {
            foreach (RuleEventRecord e in Domain(state)?.Pending ?? Array.Empty<RuleEventRecord>())
            {
                if (e != null && e.FireWait && e.EntityId == entityId)
                {
                    return e.Rule;
                }
            }
            return 0;
        }

        // ── 按单记原因（EMG-R 出口补缺口第 4 轮，审查 P2）──────────────────────────────

        /// <summary>
        /// 自动重建的“等材料 / 等火灭”原因按单记（每张在等的单在 <see cref="RuleEventRecord.IssueArg"/> 里记自己的原因）；规则面板只有一个原因位，
        /// 显示排在最前面还在等的那一张。某一张派出 / 不用再等之后换成下一张还在等的，一张都没有了才清掉——不因为别的单派出去就清掉还在等的单的原因。
        /// 只改“等材料 / 等火灭”这两类原因（别的原因照旧由各自的规则写）。每次处理完待处理事件后跑一次，O(规则数 × 待处理事件数)，不分配。
        /// </summary>
        private static void SyncRebuildIssues(CampaignState state, StandingRuleState d)
        {
            foreach (StandingRuleRecord r in d.Rules)
            {
                if (r == null || r.Kind != KindRebuild || !(string.IsNullOrEmpty(r.IssueKey) || IsRebuildWaitIssue(r.IssueKey)))
                {
                    continue;
                }
                RuleEventRecord head = null;
                foreach (RuleEventRecord e in d.Pending)
                {
                    if (e != null && e.Rule == r.Serial && (e.Kind == EventRebuildRetry || e.FireWait))
                    {
                        head = e;
                        break;
                    }
                }
                if (head == null)
                {
                    SetIssue(state, r, null, null);
                    continue;
                }
                SetIssue(state, r, head.FireWait ? IssueRebuildFire : "rules.issue.rebuild_wait", !string.IsNullOrEmpty(head.IssueArg) ? head.IssueArg : LegacyIssueArg(state, head));
            }
        }

        /// <summary>旧档的等待记录没有 IssueArg（下一次定时重试才补上）：按现在的状态补一份同格式的原因参数（等材料写重建费与库存；等火灭写建筑 / 传送带那一格）。</summary>
        private static string LegacyIssueArg(CampaignState state, RuleEventRecord e)
        {
            if (e.Kind != EventRebuildRetry)
            {
                return PieceArg(HomeValleyConstruction.FindPlan(state, e.EntityId));
            }
            if (e.FireWait)
            {
                return "@b:" + e.EntityId;
            }
            BuildingRecord b = HomeValleyConstruction.FindBuildingFast(state, e.EntityId);
            string msg = b != null ? GameText.Format("bp.repair_no_scrap", BuildingOps.RebuildScrapFor(b), Mathf.FloorToInt(state.Scrap)) : string.Empty;
            return "@b:" + e.EntityId + "|@raw:" + msg;
        }

        // ── 预留（EMG-R 出口补缺口第 5 轮，FG-GAP-189；总控更正 2026-11-17，按推荐默认执行（2026-11-17），待用户确认）──────────────
        // (a) 预留只挡“逐份取料”的传送带 / 管线虚影：有要一次凑齐废料的建筑在等材料（不论它比虚影早还是晚被摧毁），且那座在当前仓库上限内、按当前进账攒得齐时，
        //     虚影不得取用那部分（不然逐份取料会一直把废料吃光，整单永远凑不齐）。FG11-E2E-01 修复轮 3（FG-GAP-243，总控定方案 A；按推荐默认执行（2026-11-18），待用户确认）：
        //     原来只为“比虚影更早被摧毁”的建筑预留，能不能先建回一座取决于攒够那一拍是谁先拿（FGJ-M6 实测竞速），改为不看摧毁先后。
        // (b) 整单之间不互相预留：建筑按摧毁先后检查，谁的材料现在够谁先派单（TryRebuild）；前面的付不起，后面付得起的照常派。
        // (c) 整单所需超过仓库可存上限、或当前进账下攒不齐（没有在产的回收站，核心应急也到不了那么多）时不为它预留，原因写进它的等待原因（规则面板 / 规则日志 / 建筑面板）。
        // (d) 玩家手动下的单（施工队列点“重建”、自己放的施工、面板里修）不受预留限制：只有自动重建派出、带排队位置（RuleQueueTick）的虚影单才查。
        // 第 4 轮“严格按摧毁先后排队、建筑之间也预留”会和核心应急产废料（FGR-ECO-070：库存严格为 0 才启动、只攒到回收站造价）互相卡死（审查 r5 / FGJ-M6 实测），已撤。

        /// <summary>
        /// 这份整单的废料（一次凑齐）在当前进账下攒不攒得齐：仓库存得下，并且现在就够 / 有回收站在产废料 / 核心应急正在产且它的目标够这么多（<see cref="SoftlockService.CoreScrapTarget"/>）。
        /// 攒不齐时 <paramref name="why"/> 写原因（超出仓库上限 / 当前进账攒不到）。O(生产者数)，只在取料分配（≤ 每 0.5 秒）/ 派单 / 面板刷新时，按需才算。
        /// </summary>
        public static bool ScrapAttainable(CampaignState state, int need, out string why)
        {
            why = null;
            int cap = ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap) ? HomeInventory.Capacity(state, scrap) : int.MaxValue;
            if (need > cap)
            {
                why = GameText.Format("rules.reason.no_reserve_cap", need, cap);
                return false;
            }
            if (Mathf.FloorToInt(state.Scrap) >= need || ProductionService.AnyRecyclerWorking(state)
                || SoftlockService.StateOf(state)?.CoreScrapActive == true && need <= SoftlockService.CoreScrapTarget)
            {
                return true;
            }
            why = GameText.Format("rules.reason.no_reserve_income", need, SoftlockService.CoreScrapTarget);
            return false;
        }

        /// <summary>这是自动重建规则派出的单（被摧毁建筑的重建单 / 传送带虚影的施工单）。</summary>
        public static bool IsRuleRebuild(CampaignState state, WorkOrderRecord o) =>
            o != null && o.RuleSerial > 0 && (o.Kind == WorkOrderKind.Repair || o.Kind == WorkOrderKind.Build) && Find(state, o.RuleSerial)?.Kind == KindRebuild;

        /// <summary>
        /// 自动重建派出的传送带虚影施工单这一刻不得取用的废料（规则 (a)）：还在等材料（不是等火灭）、攒得齐（<see cref="ScrapAttainable"/>）的建筑重建费合计，
        /// 不看它与虚影谁先被摧毁（FG-GAP-243）；<paramref name="holder"/> = 其中最早被摧毁的那座。
        /// 不是自动重建 / 玩家手动下的单 / 旧存档（RuleQueueTick = 0）= 0（规则 (d)）。O(待处理事件数)，只在取料分配 / 取料 / 面板刷新时。
        /// </summary>
        public static int ReservedScrapFor(CampaignState state, WorkOrderRecord o, out string holder)
        {
            holder = null;
            if (o == null || o.Kind != WorkOrderKind.Build || o.RuleQueueTick <= 0 || o.RuleSerial <= 0)
            {
                return 0;
            }
            int sum = 0;
            long best = long.MaxValue;
            foreach (RuleEventRecord e in Domain(state)?.Pending ?? Array.Empty<RuleEventRecord>())
            {
                if (e == null || e.Kind != EventRebuildRetry || e.FireWait)
                {
                    continue; // 等火灭的不预留：那里可能长期有火（常驻斑块），它派不出去也不该一直占着额度
                }
                BuildingRecord b = HomeValleyConstruction.FindBuildingFast(state, e.EntityId); // O(1)：取料分配 / 面板刷新会调
                if (b == null || b.ConstructionState != BuildingConstructionState.Damaged || HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId) != null)
                {
                    continue;
                }
                int cost = BuildingOps.RebuildScrapFor(b);
                if (cost <= 0 || !ScrapAttainable(state, cost, out _))
                {
                    continue;
                }
                sum += cost;
                if (e.Tick < best)
                {
                    best = e.Tick;
                    holder = b.BuildingId;
                }
            }
            return sum;
        }

        /// <summary>建筑名（被预留挡住时写“预留给 X”）。</summary>
        public static string ReserveHolderName(CampaignState state, string buildingId) => BuildingLabel(state, buildingId);

        /// <summary>
        /// 传送带虚影的自动重建单因为废料预留给前面的建筑而转为等材料：规则日志写“等材料（预留给 X）”——同一条规则、同一座预留（同一次摧毁）只写一条（同类聚合，B08），
        /// 被它挡住的其余虚影在施工队列里逐条写明。逐张都写会把只留最近 100 条的规则日志刷满、挤掉建筑“等料”的记录（第 4 轮 FGJ-M6 实测一局 42 条）。
        /// 去重记在那座建筑的等待记录上（<see cref="RuleEventRecord.ReserveNoted"/>，随存档、重试时带着）：同一存档状态不论在哪个进程读回都写同样的日志（审查 r5：原来是进程级静态集合）。
        /// </summary>
        public static void NoteReservedWait(CampaignState state, WorkOrderRecord o, string holder)
        {
            if (o == null || string.IsNullOrEmpty(holder))
            {
                return;
            }
            RuleEventRecord h = null;
            foreach (RuleEventRecord e in Domain(state)?.Pending ?? Array.Empty<RuleEventRecord>())
            {
                if (e != null && e.Kind == EventRebuildRetry && !e.FireWait && e.EntityId == holder) // 预留方可以是另一条自动重建规则的单（ReservedScrapFor 不分规则）
                {
                    h = e;
                    break;
                }
            }
            if (h == null || h.ReserveNoted)
            {
                return;
            }
            h.ReserveNoted = true;
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, o.TargetId);
            AddLog(state, o.RuleSerial, "rules.log.rebuild_reserved", p != null ? CellKeyOf(p) : null, LabelArg(o.RuleSerial), PieceArg(p), "@b:" + holder);
        }

        /// <summary>被摧毁、自动重建在等材料的建筑：返回它的等待原因（“废料不够：重建要 N，仓库里有 M”，攒不齐时附不预留的原因）与规则编号；没有 = null。O(待处理事件数)，建筑面板读。</summary>
        public static string RebuildWaitReason(CampaignState state, string buildingId, out int rule)
        {
            rule = 0;
            foreach (RuleEventRecord e in Domain(state)?.Pending ?? Array.Empty<RuleEventRecord>())
            {
                if (e != null && e.Kind == EventRebuildRetry && !e.FireWait && e.EntityId == buildingId)
                {
                    rule = e.Rule;
                    string arg = e.IssueArg ?? string.Empty;
                    int at = arg.IndexOf("|@raw:", StringComparison.Ordinal);
                    return at >= 0 ? arg.Substring(at + 6) : string.Empty;
                }
            }
            return null;
        }

        // ── 已派出的单那里后来起火（EMG-R 出口补缺口第 4 轮，DEBT-EMGREXIT-06）──────────────────────

        /// <summary>
        /// 自动重建派出的单此刻目标还在烧：建筑重建单 = 占地里有会烧蚀设施的斑块 / 燃烧液洼；传送带虚影施工单 = 还没建成的格子里有一格在烧（逐格，不用外接矩形）。
        /// 判据与派单那一刻同一个（<see cref="Burning"/>，按物质数值判、不认物质名）。O(斑块数 + 区域数) 一次，在内核里算；只在派工评估（≤ 每 0.5 秒）与到达时调用。
        /// </summary>
        public static bool RebuildSiteBurning(CampaignState state, WorkOrderRecord o)
        {
            if (o.Kind == WorkOrderKind.Repair)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, o.TargetId);
                return b != null && BurningAt(b);
            }
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, o.TargetId);
            if (p?.Xs == null)
            {
                return false;
            }
            for (int i = 0; i < p.Xs.Length && i < (p.Ys?.Length ?? 0); i++)
            {
                if ((p.CellState == null || i >= p.CellState.Length || p.CellState[i] == 0) && Burning(new GridCell(p.Xs[i], p.Ys[i]), new GridCell(p.Xs[i], p.Ys[i])))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>已派出的自动重建单因为那里起火而挂起 / 火灭后续做：规则日志各写一条（可定位；挂起时再发一条通知）。</summary>
        public static void NoteFireHold(CampaignState state, WorkOrderRecord o, bool hold)
        {
            string key = hold ? "rules.log.rebuild_fire_hold" : "rules.log.rebuild_fire_resume";
            string entity;
            string what;
            Vector3 at;
            if (o.Kind == WorkOrderKind.Repair)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, o.TargetId);
                entity = BuildingKey(o.TargetId);
                what = "@b:" + o.TargetId;
                at = b != null ? new Vector3(b.Position.x, 0f, b.Position.y) : Vector3.zero;
            }
            else
            {
                PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, o.TargetId);
                entity = p != null ? CellKeyOf(p) : null;
                what = PieceArg(p);
                at = p?.Xs != null && p.Xs.Length > 0 ? new Vector3(p.Xs[0], 0f, p.Ys[0]) : Vector3.zero;
            }
            AddLog(state, o.RuleSerial, key, entity, LabelArg(o.RuleSerial), what);
            if (hold)
            {
                NotificationCenter.Post("standing_rule", Expand(state, key, new[] { LabelArg(o.RuleSerial), what }), at);
            }
        }

        private static string CellKeyOf(PlannedBeltRecord p) =>
            p?.Xs == null || p.Xs.Length == 0 ? null : "c:" + p.Xs[0].ToString(CultureInfo.InvariantCulture) + "," + p.Ys[0].ToString(CultureInfo.InvariantCulture);

        private static string PieceArg(PlannedBeltRecord p) =>
            p?.Xs == null || p.Xs.Length == 0 ? "@raw:?" : "@raw:" + HomeValleyConstruction.PieceName(p) + " (" + p.Xs[0].ToString(CultureInfo.InvariantCulture) + ", " + p.Ys[0].ToString(CultureInfo.InvariantCulture) + ")";

        // ── 传送带虚影 ─────────────────────────────────────────────────────────────

        /// <param name="retry">还在烧时把等火灭的记录放进这里（定时检查步再查）。</param>
        /// <param name="wasFire">上一次检查就在等火灭（不重复写日志）。</param>
        /// <param name="since">这份虚影被摧毁的那一步（排队顺序；补排时 = 补排这一步）。</param>
        private static void HandleBeltGhost(CampaignState state, string planId, List<RuleEventRecord> retry, bool wasFire = false, long since = -1)
        {
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, planId);
            if (p == null || !p.Destroyed || p.Xs == null || p.Xs.Length == 0)
            {
                return; // 已被玩家重建 / 移除：不用管（规则原因由 SyncRebuildIssues 换成下一张还在等的）
            }
            var cell = new GridCell(p.Xs[0], p.Ys[0]);
            StandingRuleRecord r = RebuildRuleFor(state, BeltTarget, (cell, cell));
            if (r == null)
            {
                return; // 没有覆盖这里的自动重建规则：留下虚影，等玩家在施工队列里点“重建”（FGR-BASE-020）
            }
            long tick = since >= 0 ? since : GameClock.Ticks;
            string cellKey = CellKeyOf(p);
            string where = PieceArg(p);
            int burn = BurningCell(p);
            if (burn >= 0)
            {
                // EMG-R 出口补缺口（FG-GAP-224）：虚影有一格还在烧——不开施工单（不扣料、不把机器派进火里），等火灭再派；开始等的那一次写日志，原因写在烧的那一格。
                string fireAt = "@raw:" + HomeValleyConstruction.PieceName(p) + " (" + p.Xs[burn].ToString(CultureInfo.InvariantCulture) + ", " + p.Ys[burn].ToString(CultureInfo.InvariantCulture) + ")";
                retry.Add(new RuleEventRecord { Kind = EventBeltGhost, EntityId = p.PlanId, Rule = r.Serial, Tick = tick, FireWait = true, IssueArg = fireAt });
                if (!wasFire)
                {
                    AddLog(state, r.Serial, "rules.log.rebuild_belt_fire", cellKey, LabelArg(r.Serial), fireAt);
                }
                return;
            }
            if (!HomeValleyConstruction.RebuildDestroyed(state, p.PlanId))
            {
                return;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveBuild(state, HomeValleyConstruction.BeltPlanPrefix + p.PlanId);
            if (o != null)
            {
                o.RuleSerial = r.Serial;
                o.RuleQueueTick = Math.Max(1, tick); // FG-GAP-189 / 243：标记“自动重建派出的虚影单”（取料时不取用在等一次凑齐、攒得齐的建筑还差的废料）
            }
            Fired(state, r, "rules.log.rebuild_belt", cellKey, LabelArg(r.Serial), where);
            GuidanceHooks.Raise(GuidanceHooks.RulesRebuildFirstBelt);
        }

        // ── 补排 ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// 补排：已经被摧毁（只剩虚影）的建筑与传送带虚影，现在落在某条启用的自动重建规则范围里、又还没有重建单的，按普通流程排进重建
        /// （建筑：废料够了派单，不够就排进“等材料”重试；传送带：开施工单）。已经在等材料的不重复排。O(建筑数 + 虚影数)，只在玩家改动后一次。
        /// </summary>
        private static void SweepRebuild(CampaignState state)
        {
            SweepCount++;
            StandingRuleState d = Domain(state);
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (RuleEventRecord e in d.Pending)
            {
                if (e != null && (e.Kind == EventRebuildRetry || e.FireWait))
                {
                    waiting.Add(e.EntityId); // 等材料的建筑、等火灭的建筑 / 传送带虚影：已在排队，不重复排
                }
            }
            int queued = 0;
            var retry = new List<RuleEventRecord>();
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.ConstructionState != BuildingConstructionState.Damaged || HomeGridService.IsRelocationGhost(b)
                    || waiting.Contains(b.BuildingId) || HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId) != null)
                {
                    continue;
                }
                StandingRuleRecord r = RebuildRuleFor(state, b.BuildingTypeId, CellOf(b));
                if (r == null)
                {
                    continue;
                }
                TryRebuild(state, r, b, retry, first: true);
                queued++;
            }
            PlannedBeltRecord[] plans = state.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            var ghosts = new List<string>();
            foreach (PlannedBeltRecord p in plans)
            {
                if (p != null && p.Destroyed && !waiting.Contains(p.PlanId))
                {
                    ghosts.Add(p.PlanId);
                }
            }
            foreach (string id in ghosts)
            {
                PlannedBeltRecord before = HomeValleyConstruction.FindPlan(state, id);
                HandleBeltGhost(state, id, retry);
                if (before != null && !before.Destroyed)
                {
                    queued++;
                }
            }
            if (retry.Count > 0)
            {
                var list = new List<RuleEventRecord>(d.Pending);
                list.AddRange(retry);
                d.Pending = list.ToArray();
            }
            SyncRebuildIssues(state, d);
            LastSweepQueued = queued;
        }

        // ── 重建区域 ─────────────────────────────────────────────────────────────

        private static RebuildZoneRecord[] CopyZones(StandingRuleState d, RebuildZoneRecord[] src)
        {
            if (src == null || src.Length == 0)
            {
                return Array.Empty<RebuildZoneRecord>();
            }
            var list = new List<RebuildZoneRecord>(src.Length);
            foreach (RebuildZoneRecord z in src)
            {
                if (z != null)
                {
                    list.Add(new RebuildZoneRecord { Serial = d.NextZoneSerial++, X0 = z.X0, Y0 = z.Y0, X1 = z.X1, Y1 = z.Y1, Enabled = z.Enabled });
                }
            }
            return list.ToArray();
        }

        public static RebuildZoneRecord FindZone(StandingRuleRecord r, int zoneSerial)
        {
            foreach (RebuildZoneRecord z in r?.Zones ?? Array.Empty<RebuildZoneRecord>())
            {
                if (z != null && z.Serial == zoneSerial)
                {
                    return z;
                }
            }
            return null;
        }

        /// <summary>这条规则在这一格上的区域（最后圈的优先；没有 = null）。</summary>
        public static RebuildZoneRecord ZoneAt(StandingRuleRecord r, GridCell cell)
        {
            RebuildZoneRecord[] zones = r?.Zones ?? Array.Empty<RebuildZoneRecord>();
            for (int i = zones.Length - 1; i >= 0; i--)
            {
                RebuildZoneRecord z = zones[i];
                if (z != null && cell.X >= z.X0 && cell.X <= z.X1 && cell.Y >= z.Y0 && cell.Y <= z.Y1)
                {
                    return z;
                }
            }
            return null;
        }

        /// <summary>给自动重建规则圈一个重建区域（两角任意顺序，含两端；开着）。小于 rules.zone.min_cells 格 / 超过 rules.zone.max 个时拒绝并写原因。</summary>
        public static bool TryAddZone(CampaignState state, int serial, GridCell a, GridCell b, out RebuildZoneRecord zone, out string message)
        {
            if (Env.SilentNightService.RulesLocked(state, out message)) // FG7-ENV-02：静默夜期间不可用（FGR-ENV-011，写明原因）
            {
                zone = null;
                return false;
            }
            zone = null;
            StandingRuleRecord r = Find(state, serial);
            if (r == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            if (r.Kind != KindRebuild)
            {
                message = GameText.Get("rules.msg.zone_not_rebuild");
                return false;
            }
            int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            if (cells < ZoneMinCells)
            {
                message = GameText.Format("rules.msg.zone_small", ZoneMinCells);
                return false;
            }
            if ((r.Zones?.Length ?? 0) >= ZoneMax)
            {
                message = GameText.Format("rules.msg.zone_max", ZoneMax);
                return false;
            }
            StandingRuleState d = Domain(state);
            zone = new RebuildZoneRecord { Serial = d.NextZoneSerial++, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Enabled = true };
            var list = new List<RebuildZoneRecord>(r.Zones ?? Array.Empty<RebuildZoneRecord>()) { zone };
            r.Zones = list.ToArray();
            AfterEdit(state, r); // 圈了新区域：补排区域里已经被摧毁的东西
            GuidanceHooks.Raise(GuidanceHooks.RulesRebuildZoneFirstDrawn);
            message = GameText.Format("rules.msg.zone_added", Label(r), zone.Serial, x1 - x0 + 1, y1 - y0 + 1);
            return true;
        }

        /// <summary>打开 / 关掉一个重建区域（可逆，不弹确认）。打开后下一个模拟步补排区域里已经被摧毁的东西。</summary>
        public static bool TrySetZoneEnabled(CampaignState state, int serial, int zoneSerial, bool enabled, out string message)
        {
            if (Env.SilentNightService.RulesLocked(state, out message)) // FG7-ENV-02：静默夜期间不可用（FGR-ENV-011，写明原因）
            {
                return false;
            }
            StandingRuleRecord r = Find(state, serial);
            RebuildZoneRecord z = FindZone(r, zoneSerial);
            if (z == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            z.Enabled = enabled;
            AfterEdit(state, enabled ? r : null); // 打开区域才补排；关掉不补排
            message = GameText.Format("rules.msg.zone_toggled", z.Serial, GameText.Get(enabled ? "rules.zone.on" : "rules.zone.off"));
            return true;
        }

        public static bool TryToggleZone(CampaignState state, int serial, int zoneSerial, out string message)
        {
            if (Env.SilentNightService.RulesLocked(state, out message)) // FG7-ENV-02：静默夜期间不可用（FGR-ENV-011，写明原因）
            {
                return false;
            }
            RebuildZoneRecord z = FindZone(Find(state, serial), zoneSerial);
            if (z == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            return TrySetZoneEnabled(state, serial, zoneSerial, !z.Enabled, out message);
        }

        /// <summary>删掉一个重建区域（可逆的设置改动：区域可以重新圈，已经派出的重建单不撤）。</summary>
        public static bool TryRemoveZone(CampaignState state, int serial, int zoneSerial, out string message)
        {
            if (Env.SilentNightService.RulesLocked(state, out message)) // FG7-ENV-02：静默夜期间不可用（FGR-ENV-011，写明原因）
            {
                return false;
            }
            StandingRuleRecord r = Find(state, serial);
            RebuildZoneRecord z = FindZone(r, zoneSerial);
            if (z == null)
            {
                message = GameText.Get("rules.msg.not_found");
                return false;
            }
            var list = new List<RebuildZoneRecord>(r.Zones);
            list.Remove(z);
            r.Zones = list.ToArray();
            AfterEdit(state, r.Zones.Length == 0 ? r : null); // 删掉最后一个区域 = 整个家园（范围放宽）才补排
            message = GameText.Format("rules.msg.zone_removed", z.Serial);
            return true;
        }

        /// <summary>“Z3（12×8 格，开）”。</summary>
        public static string ZoneLabel(RebuildZoneRecord z) =>
            z == null ? string.Empty : GameText.Format("rules.zone.item", z.Serial, z.X1 - z.X0 + 1, z.Y1 - z.Y0 + 1, GameText.Get(z.Enabled ? "rules.zone.on" : "rules.zone.off"));

        /// <summary>规则编辑区的区域说明：没有区域 / 全关 / 列表。</summary>
        public static string ZoneSummary(StandingRuleRecord r)
        {
            RebuildZoneRecord[] zones = r?.Zones ?? Array.Empty<RebuildZoneRecord>();
            if (zones.Length == 0)
            {
                return GameText.Get("rules.zone.none");
            }
            var names = new List<string>(zones.Length);
            bool anyOn = false;
            foreach (RebuildZoneRecord z in zones)
            {
                names.Add(ZoneLabel(z));
                anyOn |= z.Enabled;
            }
            string list = GameText.Format("rules.zone.list", string.Join(ListSeparator, names));
            return anyOn ? list : list + "\n" + GameText.Get("rules.zone.all_off");
        }

        /// <summary>区域中心的世界位置（“看看在哪”飞镜头）。</summary>
        public static Vector3 ZoneCenter(RebuildZoneRecord z) => z == null ? Vector3.zero : new Vector3((z.X0 + z.X1) * 0.5f, 0f, (z.Y0 + z.Y1) * 0.5f);
    }
}
