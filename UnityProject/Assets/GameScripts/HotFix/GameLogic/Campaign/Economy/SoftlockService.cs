using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-10（FG04 FGR-ECO-070～072；FGT-ECO-005；FG00 B11）：家园软锁保底与死锁检测。沿用并扩展 ER3-SOFTLOCK-01（<see cref="HomeValleySoftlockGuard"/>
    /// 每个世界步调用 <see cref="Step"/>，核心被毁时整个家园冻结、这里也不再走）。
    /// - 应急打印：家园机器（玩家的、活着的、能施工的；不含只会充电的战斗履带与 Demo 紧急救援机）少于 eco.softlock.print_min_machines 台时，归还核心每个游戏日免费打印一台搬运机（基础搬运轮式）。
    /// - 核心应急产废料：废料为 0 且没有能工作的回收站时进入，每分钟产 eco.softlock.core_scrap_per_minute 件，攒到够造一座回收站（读建筑表）或有回收站开始工作为止。
    /// - 传送带闭环卡死：有环的传送带网络里的物品连续 eco.softlock.loop_stall_seconds 既不减少也没有被取走（送进输入端口）就警告一次。
    /// - 施工 / 维修目标到不了：施工 / 维修工单的机器真实去过、寻路说到不了时，按间隔做一次泛洪确认（从归还核心与家园机器都走不到它），连续 eco.softlock.unreachable_seconds 就警告一次（写明被谁围住 / 走不到）。
    /// 全部按世界步序号判断要不要做（暂停不走、倍速一帧多走几步、与有没有人在看家园无关，FGR-BASE-021）；计时与已告警标记进存档（<see cref="SoftlockState"/>）。
    /// 开销：每个世界步 O(1)（取模）；每 eco.softlock.check_seconds 数一次机器 O(机器数)，废料为 0 时再看一次生产建筑 O(生产建筑数)；
    /// 闭环检查 O(传送带网络数)（网络重建后找一次代表格 O(有环网络数 × 格数)）；可达性检查只在有工单时做一次 Burst 泛洪。没有每帧全图扫描（FG00 B18）。
    /// </summary>
    public static class SoftlockService
    {
        // ── 自检读点 ──
        public static int Revision { get; private set; } = 1;
        public static int PrintChecks { get; private set; }
        public static int LoopChecks { get; private set; }
        public static int LoopRepresentativeScans { get; private set; }
        public static int ReachChecks { get; private set; }
        public static double LastReachMs { get; private set; }
        public static double LastLoopMs { get; private set; }

        private static bool _spawnWarned;
        private static int _loopRebuild = -1;
        private static CampaignState _loopState;
        private static readonly List<int> CycleNets = new List<int>(8);
        private static readonly List<int2> CycleKeys = new List<int2>(8);
        private static readonly List<int2> CellScratch = new List<int2>(256);
        private static readonly List<LoopWatchRecord> LoopScratch = new List<LoopWatchRecord>(8);
        private static readonly List<BuildingRecord> SiteScratch = new List<BuildingRecord>(8);
        private static readonly List<string> SiteKind = new List<string>(8);
        private static readonly List<GridCell> MachineCells = new List<GridCell>(16);
        private static readonly List<ReachWatchRecord> ReachScratch = new List<ReachWatchRecord>(8);
        private static readonly HashSet<string> SiteSeen = new HashSet<string>(StringComparer.Ordinal);

        // ── 调参 ──
        public static int MinMachines => Math.Max(1, GridContent.TuningInt("eco.softlock.print_min_machines"));
        public static float PrintHealth => Math.Max(1f, GridContent.Tuning("eco.softlock.print_health"));
        public static float CoreScrapPerMinute => Math.Max(0.01f, GridContent.Tuning("eco.softlock.core_scrap_per_minute"));
        private static long CheckTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(0.05f, GridContent.Tuning("eco.softlock.check_seconds"))));
        private static long LoopTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(0.05f, GridContent.Tuning("eco.softlock.loop_check_seconds"))));
        private static long ReachTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(0.05f, GridContent.Tuning("eco.softlock.reach_check_seconds"))));
        public static long LoopStallTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(1f, GridContent.Tuning("eco.softlock.loop_stall_seconds"))));
        public static long UnreachableTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(0f, GridContent.Tuning("eco.softlock.unreachable_seconds"))));

        /// <summary>每产 1 件应急废料要走的世界步（每分钟 N 件 = 60 / N 游戏秒一件）。</summary>
        public static long TicksPerCoreScrap => Math.Max(1L, GameClock.TicksFor(60.0 / CoreScrapPerMinute));

        /// <summary>应急产废料攒到这么多为止：一座回收站的造价（读建筑表；表里没有时 1）。</summary>
        public static int CoreScrapTarget =>
            HomeValleyLayout.BuildProfile.TryGetValue(RecyclerTypeId, out (int ScrapCost, float Seconds) cost) ? Math.Max(1, cost.ScrapCost) : 1;

        public const string RecyclerTypeId = "recycler";

        /// <summary>FG0-ARCH-01：会话开始时清空运行时缓存（存档里的计时不动）。</summary>
        public static void ResetSessionState()
        {
            _spawnWarned = false;
            _loopRebuild = -1;
            _loopState = null;
            CycleNets.Clear();
            CycleKeys.Clear();
            PrintChecks = 0;
            LoopChecks = 0;
            LoopRepresentativeScans = 0;
            ReachChecks = 0;
            Revision++;
        }

        public static SoftlockState StateOf(CampaignState state)
        {
            if (state == null)
            {
                return null;
            }
            if (state.Economy?.Softlock == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            return state.Economy.Softlock;
        }

        // ── 推进（每个世界步，O(1) 取模）─────────────────────────────────────────────

        /// <summary>家园模拟的一个固定步（<see cref="HomeValleySoftlockGuard.Tick"/> 调用；核心被毁时调用方已经停了）。只看世界步序号。</summary>
        public static void Step(CampaignState state) => StepAt(state, GameClock.Ticks);

        /// <summary>按给定的世界步序号决定这一步做哪些检查（自检量“每个世界步的平均开销”时逐步喂步序号；正式路径就是当前步序号）。</summary>
        public static void StepAt(CampaignState state, long now)
        {
            if (state == null)
            {
                return;
            }
            long check = CheckTicks;
            if (now % check == 0)
            {
                CheckPrint(state);
                CheckCoreScrap(state, check);
            }
            if (now % LoopTicks == 0)
            {
                CheckLoops(state);
            }
            if (now % ReachTicks == 0)
            {
                CheckReach(state);
            }
        }

        // ── 应急打印（FGR-ECO-070）────────────────────────────────────────────────────

        /// <summary>
        /// 家园机器台数：玩家的、活着的、<b>能施工</b>的机器（能力表里有 Build——搬运轮式等；含远征中、厂内待命的，它们会回来）。
        /// 只会充电的战斗履带（ERC-003）、只会维修的 Demo 紧急救援机、不在家园能力表里的底盘都不算：它们撑不起恢复路径
        /// （审查修复 P1：2 台 ERC-003 活着、搬运机全灭时此前算“够 2 台”而永不打印）。O(机器数)。
        /// </summary>
        public static int CountHomeMachines()
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && (string.IsNullOrEmpty(m.FactionId) || m.FactionId == "Player")
                    && HomeValleyWorkOrders.CanDoKind(m.ChassisId, WorkOrderKind.Build))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>今天（游戏日）的应急打印是否已经用掉。</summary>
        public static bool PrintedToday(CampaignState state) => StateOf(state) is SoftlockState st && st.LastPrintDay >= GameClock.DayOf(GameClock.GameSeconds);

        /// <summary>
        /// 今天的应急打印接下来<b>真的会发生</b>（核心没毁、今天还没打印、能施工的机器少于 <see cref="MinMachines"/> 台）。
        /// Demo 紧急救援机（<see cref="HomeValleySoftlockGuard"/>）只在这时让路；其余情况（今天已打印、或机器“够数”但都不在家 / 不能干活）照旧派救援机，
        /// 不会两层保底互相等待（审查修复 P1）。O(机器数)，0.5 秒一次。
        /// </summary>
        public static bool PrintPending(CampaignState state) =>
            state != null && !HomeValleySoftlockGuard.IsCoreDestroyed(state) && !PrintedToday(state) && CountHomeMachines() < MinMachines;

        private static void CheckPrint(CampaignState state)
        {
            PrintChecks++;
            if (HomeValleySoftlockGuard.IsCoreDestroyed(state))
            {
                return;
            }
            SoftlockState st = StateOf(state);
            int day = GameClock.DayOf(GameClock.GameSeconds);
            if (st.LastPrintDay >= day || CountHomeMachines() >= MinMachines)
            {
                return;
            }
            Print(state, st, day);
        }

        private static void Print(CampaignState state, SoftlockState st, int day)
        {
            HomeValleyFactory.EnsureBlueprintsSeeded(state);
            BlueprintRecord bp = null;
            foreach (BlueprintRecord b in state.BlueprintRecords ?? Array.Empty<BlueprintRecord>())
            {
                if (b != null && b.BlueprintId == HomeValleyLayout.BlueprintHaulerId)
                {
                    bp = b;
                    break;
                }
            }
            BlueprintVersionRecord version = null;
            foreach (BlueprintVersionRecord v in bp?.Versions ?? Array.Empty<BlueprintVersionRecord>())
            {
                if (v != null && v.Version == bp.ActiveVersion)
                {
                    version = v;
                }
            }
            Vector2 at = PrintPosition(state);
            float hp = PrintHealth;
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                at, hp, hp, "Player", version?.Version ?? 1, version?.CompileSignature);
            if (!r.Success)
            {
                if (!_spawnWarned)
                {
                    _spawnWarned = true;
                    Log.Warning($"[SoftlockService] 应急打印失败：{r.Message}");
                }
                return;
            }
            CircuitOpResult reg = MachineLoadoutRegistry.Register(state, r.LogicId, HomeValleyLayout.BlueprintHaulerId, version?.Version ?? 1);
            if (!reg.Success)
            {
                Log.Warning($"[SoftlockService] 应急打印的机器 {r.LogicId} 装配登记失败：{reg.Message}");
            }
            st.LastPrintDay = day;
            st.PrintCount++;
            st.LastPrintLogicId = r.LogicId;
            Revision++;
            if (ItemCatalog.TryGet(ItemCatalog.MachineId, out ItemDef machine))
            {
                ProductionStats.RecordUnits(state, machine, 1, produced: true); // 统计面板 / 离家报告：打印出来的机器也是产出（B19）。
            }
            GuidanceHooks.Raise(GuidanceHooks.SoftlockFirstPrint);
            // 声音 + 字幕 + 可定位通知（“核心应急”，同类聚合、进历史、离家期间记进报告“机器”段；B07 / B08 / B19）。
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.EmergencyPrint, at,
                GameText.Format("softlock.print.caption", MinMachines, MachineNaming.Short(r.LogicId)));
            Log.Info($"[SoftlockService] 家园机器少于 {MinMachines} 台，第 {day} 天归还核心免费打印搬运机 LogicId={r.LogicId}。");
        }

        /// <summary>打印出来的机器站在哪：开局搬运机的出生锚点（核心枢轴 + 开局布局表偏移，B25）；那一格被占了就找最近一格机器能站的。</summary>
        private static Vector2 PrintPosition(CampaignState state)
        {
            Vector2 anchor = HomeValleyLayout.Erc002Spawn.Position;
            if (!Nav.NavService.IsBound || !ReferenceEquals(Nav.NavService.BoundState, state))
            {
                return anchor;
            }
            GridCell c0 = Nav.NavService.CellOf(anchor.x, anchor.y);
            for (int r = 0; r <= 12; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        if (Nav.NavService.Kernel.Passable(c0.X + dx, c0.Y + dy, BinGames.Sim.Nav.NavConst.ClassPlayer))
                        {
                            return new Vector2(c0.X + dx, c0.Y + dy);
                        }
                    }
                }
            }
            return anchor;
        }

        // ── 核心应急产废料（FGR-ECO-070）──────────────────────────────────────────────

        /// <summary>自检专用：给“库存严格为 0 时逐件记账”的旧自检（FG3-LOG-02 施工取料 / 退回守恒）关掉应急产废料，免得每分钟 1 件打乱它们的账；正式路径从不设置。</summary>
        public static bool CoreScrapSuppressedForTests { get; set; }

        private static void CheckCoreScrap(CampaignState state, long ticks)
        {
            if (CoreScrapSuppressedForTests)
            {
                return;
            }
            SoftlockState st = StateOf(state);
            if (!st.CoreScrapActive)
            {
                if (state.Scrap > 0 || ProductionService.AnyRecyclerWorking(state))
                {
                    return;
                }
                st.CoreScrapActive = true;
                st.CoreScrapTicks = 0;
                Revision++;
                GuidanceHooks.Raise(GuidanceHooks.SoftlockFirstCoreScrap);
                NotificationCenter.Post("core_emergency_scrap",
                    GameText.Format("softlock.core_scrap.notify", Num(CoreScrapPerMinute), CoreScrapTarget), CorePosition(state));
            }
            else
            {
                string why = state.Scrap >= CoreScrapTarget ? "softlock.core_scrap.done_target"
                    : ProductionService.AnyRecyclerWorking(state) ? "softlock.core_scrap.done_recycler" : null;
                if (why != null)
                {
                    st.CoreScrapActive = false;
                    st.CoreScrapTicks = 0;
                    Revision++;
                    NotificationCenter.Post("core_emergency_scrap", GameText.Format("softlock.core_scrap.done", GameText.Get(why)), CorePosition(state));
                    return;
                }
            }
            st.CoreScrapTicks += ticks;
            long per = TicksPerCoreScrap;
            if (st.CoreScrapTicks < per || !ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap))
            {
                return;
            }
            while (st.CoreScrapTicks >= per)
            {
                st.CoreScrapTicks -= per;
                if (HomeInventory.Add(state, scrap, 1) <= 0)
                {
                    st.CoreScrapTicks = 0; // 放不下（核心缓存满）：不攒“欠产”，下一分钟再试。
                    break;
                }
                st.CoreScrapProduced++;
                ProductionStats.RecordUnits(state, scrap, 1, produced: true); // 统计面板 / 离家报告净产量（B19）。
            }
            Revision++;
        }

        /// <summary>核心面板的一行：应急产废料进度（不在进行时 null）。</summary>
        public static string CoreScrapStatus(CampaignState state)
        {
            SoftlockState st = StateOf(state);
            if (st == null || !st.CoreScrapActive)
            {
                return null;
            }
            long left = Math.Max(0L, TicksPerCoreScrap - st.CoreScrapTicks);
            int seconds = (int)Math.Ceiling(left / (double)Math.Max(1, GameClock.StepHz));
            return GameText.Format("softlock.core_scrap.status", Math.Max(0, state.Scrap), CoreScrapTarget, seconds);
        }

        /// <summary>核心面板的一行：家园机器不够、今天已经打印过时写明下一台哪天来（其余时候 null）。</summary>
        public static string PrintStatus(CampaignState state)
        {
            SoftlockState st = StateOf(state);
            if (st == null || CountHomeMachines() >= MinMachines || !PrintedToday(state))
            {
                return null;
            }
            return GameText.Format("softlock.print.next", MinMachines, st.LastPrintDay + 1);
        }

        private static Vector3? CorePosition(CampaignState state)
        {
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    return new Vector3(b.Position.x, 0f, b.Position.y);
                }
            }
            return null;
        }

        private static string Num(float v) => Math.Abs(v - Mathf.Round(v)) < 0.001f
            ? Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

        // ── 传送带闭环卡死（FGR-ECO-072）────────────────────────────────────────────────

        /// <summary>马上做一次闭环检查（自检用；正式路径按 eco.softlock.loop_check_seconds 由 <see cref="Step"/> 调）。</summary>
        public static void CheckLoopsNow(CampaignState state) => CheckLoops(state);

        private static void CheckLoops(CampaignState state)
        {
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                return;
            }
            LoopChecks++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeltKernel k = BeltNetworkService.Kernel;
            int nets = k.NetworkCount; // 先让拓扑跟上（RebuildCount 随之更新）
            if (_loopRebuild != k.RebuildCount || !ReferenceEquals(_loopState, state))
            {
                // 拓扑变了：重新找有环的网络和它们的代表格（网络编号会变，计时按代表格接回）。
                _loopRebuild = k.RebuildCount;
                _loopState = state;
                CycleNets.Clear();
                CycleKeys.Clear();
                SoftlockState prev = StateOf(state);
                for (int n = 0; n < nets; n++)
                {
                    if (!k.TryGetNetworkStats(n, out BeltNetworkStats ns) || !ns.HasCycle)
                    {
                        continue;
                    }
                    k.CollectNetworkCells(n, CellScratch);
                    if (CellScratch.Count == 0)
                    {
                        continue;
                    }
                    // 审查修复 P2：这个网络里还留着上一段的代表格时沿用它（玩家在环所在网络上接了一段坐标更小的支线，计时与已告警标记不丢、不重复告警）；
                    // 都不在了才取 (y, x) 最小的格。只在拓扑变化时做，O(已盯着的环数 × 格数)。
                    int2 best = CellScratch[0];
                    bool kept = false;
                    foreach (LoopWatchRecord w in prev.Loops)
                    {
                        if (w != null && CellScratch.Contains(new int2(w.X, w.Y)) && !CycleKeys.Contains(new int2(w.X, w.Y)))
                        {
                            best = new int2(w.X, w.Y);
                            kept = true;
                            break;
                        }
                    }
                    if (!kept)
                    {
                        foreach (int2 c in CellScratch)
                        {
                            if (c.y < best.y || (c.y == best.y && c.x < best.x))
                            {
                                best = c;
                            }
                        }
                    }
                    CycleNets.Add(n);
                    CycleKeys.Add(best);
                    LoopRepresentativeScans++;
                }
            }
            SoftlockState st = StateOf(state);
            long now = GameClock.Ticks;
            long stall = LoopStallTicks;
            LoopScratch.Clear();
            bool changed = false;
            for (int i = 0; i < CycleNets.Count; i++)
            {
                if (!k.TryGetNetworkStats(CycleNets[i], out BeltNetworkStats ns) || !ns.HasCycle)
                {
                    continue;
                }
                int2 key = CycleKeys[i];
                LoopWatchRecord w = FindLoop(st, key.x, key.y);
                if (w == null)
                {
                    w = new LoopWatchRecord { X = key.x, Y = key.y, SinceTick = now, LastItems = ns.Items };
                    changed = true;
                }
                // 有进展：环上没东西、窗口里有物品被送进输入端口（被取走）、或者物品比上一次检查时少（在减少）——重新计时。物品增加（还在往环上灌）不算进展。
                bool progress = ns.Items == 0 || ns.DeliveredInWindow > 0 || ns.Items < w.LastItems;
                if (progress)
                {
                    if (w.Alerted)
                    {
                        changed = true;
                    }
                    w.SinceTick = now;
                    w.Alerted = false;
                }
                else if (!w.Alerted && now - w.SinceTick >= stall)
                {
                    w.Alerted = true;
                    changed = true;
                    GuidanceHooks.Raise(GuidanceHooks.SoftlockFirstLoopStall);
                    NotificationCenter.Post("belt_loop_stall",
                        GameText.Format("softlock.loop.notify", key.x, key.y, ns.Items, AwayReportService.Duration(now - w.SinceTick)),
                        new Vector3(key.x, 0f, key.y));
                }
                w.LastItems = ns.Items;
                LoopScratch.Add(w);
            }
            if (changed || LoopScratch.Count != st.Loops.Length)
            {
                LoopScratch.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
                st.Loops = LoopScratch.ToArray();
                Revision++;
            }
            LastLoopMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static LoopWatchRecord FindLoop(SoftlockState st, int x, int y)
        {
            foreach (LoopWatchRecord w in st.Loops)
            {
                if (w != null && w.X == x && w.Y == y)
                {
                    return w;
                }
            }
            return null;
        }

        // ── 施工 / 维修目标到不了（FGR-ECO-072）──────────────────────────────────────────

        /// <summary>马上做一次可达性检查（自检用；正式路径按 eco.softlock.reach_check_seconds 由 <see cref="Step"/> 调）。</summary>
        public static void CheckReachNow(CampaignState state) => CheckReach(state);

        private static void CheckReach(CampaignState state)
        {
            SoftlockState st = StateOf(state);
            SiteScratch.Clear();
            SiteKind.Clear();
            SiteSeen.Clear();
            foreach (WorkOrderRecord o in state.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o == null || (o.Kind != WorkOrderKind.Build && o.Kind != WorkOrderKind.Repair)
                    || o.State == WorkOrderState.Completed || o.State == WorkOrderState.Cancelled || o.State == WorkOrderState.Failed)
                {
                    continue;
                }
                // 以真实工单结果为准：机器去过、寻路说到不了（UnreachableNotified 到达现场才清掉，重试期间一直在）或正等着重试“路径受阻 / 无法到达”的单才查。
                // 机器还能干完的（例如贴着一圈建筑、从旁边就能施工）不报；还没有机器去过的单等第一次真实尝试。
                bool failedTrip = o.UnreachableNotified
                                  || (o.State == WorkOrderState.Waiting && (HomeValleyWorkOrders.IsUnreachableReason(o.FailureReason) || o.FailureReason == HomeValleyWorkOrders.PathBlockedReason));
                if (!failedTrip)
                {
                    continue;
                }
                BuildingRecord b = ResolveTarget(state, o.TargetId);
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || !SiteSeen.Add(b.BuildingId))
                {
                    continue;
                }
                SiteScratch.Add(b);
                SiteKind.Add(o.Kind == WorkOrderKind.Repair ? "softlock.reach.kind.repair" : "softlock.reach.kind.build");
            }
            if (SiteScratch.Count == 0)
            {
                if (st.Sites.Length > 0)
                {
                    st.Sites = Array.Empty<ReachWatchRecord>();
                    Revision++;
                }
                return;
            }
            MachineCells.Clear();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && (string.IsNullOrEmpty(m.FactionId) || m.FactionId == "Player"))
                {
                    Vector2 p = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                    MachineCells.Add(Nav.NavService.CellOf(p.x, p.y));
                }
            }
            int n = SiteScratch.Count;
            var reached = new bool[n];
            var enclosed = new bool[n];
            var blockers = new List<string>[n];
            for (int i = 0; i < n; i++)
            {
                blockers[i] = new List<string>(2);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!Nav.NavService.SitesReachable(state, SiteScratch, MachineCells, reached, enclosed, blockers, GameText.Get("softlock.reach.terrain")))
            {
                return; // 寻路镜像没绑定：无法判断，什么都不改。
            }
            ReachChecks++;
            LastReachMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long now = GameClock.Ticks;
            long limit = UnreachableTicks;
            ReachScratch.Clear();
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                BuildingRecord b = SiteScratch[i];
                ReachWatchRecord w = FindSite(st, b.BuildingId);
                if (reached[i])
                {
                    changed |= w != null;
                    continue;
                }
                if (w == null)
                {
                    w = new ReachWatchRecord { BuildingId = b.BuildingId, SinceTick = now };
                    changed = true;
                }
                w.Enclosed = enclosed[i];
                string blockText = blockers[i].Count > 0 ? string.Join(GameText.Language == GameLanguage.En ? ", " : "、", blockers[i]) : GameText.Get("softlock.reach.terrain");
                if (w.Blockers != blockText)
                {
                    w.Blockers = blockText;
                    changed = true;
                }
                if (!w.Alerted && now - w.SinceTick >= limit)
                {
                    w.Alerted = true;
                    changed = true;
                    GuidanceHooks.Raise(GuidanceHooks.SoftlockFirstUnreachable);
                    GridCell cell = Nav.NavService.CellOf(b.Position.x, b.Position.y);
                    NotificationCenter.Post("site_unreachable",
                        GameText.Format("softlock.reach.notify", BuildingOps.NameOf(b), cell.X, cell.Y, GameText.Get(SiteKind[i]), ReachReason(w)),
                        new Vector3(b.Position.x, 0f, b.Position.y));
                }
                ReachScratch.Add(w);
            }
            if (changed || ReachScratch.Count != st.Sites.Length)
            {
                ReachScratch.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
                st.Sites = ReachScratch.ToArray();
                Revision++;
            }
        }

        /// <summary>到不了的原因（当前语言）：四周被谁挡住 / 走不到。</summary>
        public static string ReachReason(ReachWatchRecord w) =>
            w == null ? string.Empty
            : w.Enclosed ? GameText.Format("softlock.reach.enclosed", string.IsNullOrEmpty(w.Blockers) ? GameText.Get("softlock.reach.terrain") : w.Blockers)
            : GameText.Get("softlock.reach.disconnected");

        /// <summary>工单目标：建筑 ID；Demo 旧工单写的是建筑类型（取家园里第一座）。</summary>
        public static BuildingRecord ResolveTarget(CampaignState state, string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return null;
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, targetId);
            if (b != null)
            {
                return b;
            }
            foreach (BuildingRecord r in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (r != null && r.RegionId == HomeValleyLayout.RegionId && r.BuildingTypeId == targetId)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>某座施工 / 维修目标此刻要不要显示“工作受阻”的那张工单种类文本（告警栏用）。</summary>
        public static string SiteKindText(CampaignState state, string buildingId)
        {
            foreach (WorkOrderRecord o in state?.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && (o.Kind == WorkOrderKind.Build || o.Kind == WorkOrderKind.Repair)
                    && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed
                    && ResolveTarget(state, o.TargetId)?.BuildingId == buildingId)
                {
                    return GameText.Get(o.Kind == WorkOrderKind.Repair ? "softlock.reach.kind.repair" : "softlock.reach.kind.build");
                }
            }
            return GameText.Get("softlock.reach.kind.build");
        }

        private static ReachWatchRecord FindSite(SoftlockState st, string buildingId)
        {
            foreach (ReachWatchRecord w in st.Sites)
            {
                if (w != null && w.BuildingId == buildingId)
                {
                    return w;
                }
            }
            return null;
        }
    }
}
