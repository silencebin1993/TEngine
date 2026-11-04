using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG2-PERSIST-01（FG19 FGR-EMG-150、154；ADR-EMG-001 ⑦ ⑩ T1）：家园（含己方前哨——前哨只是家园内核里的建筑群）长期留存物的热更门面。
    /// - 存档：残骸与固态斑块都在家园内核快照里（T1 核实：家园内核在一局内常驻、随 <see cref="CombatSites.WriteTo"/> 存档；不另开战役存档域）。这里只存警告记录（<see cref="SoftlockState.PersistBlocks"/>）。
    /// - 清除工作单：沿用 FG04 工作单与优先级（复用 Salvage 种类，TargetId = “sitepersist:w:Id / p:Id”，同遗迹回收），开单预留废料、执行前取消全额返还；目标先没了也全额返还。
    /// - 堵路警告：<see cref="SoftlockService"/> 每 eco.softlock.reach_check_seconds 调一次 <see cref="Check"/>；只有长期留存物或建筑变了才做一次 Burst 泛洪（从归还核心到仓库与家园外框），不按帧。
    /// 全部按世界步序号走，与有没有人在看家园无关（B24）。
    /// </summary>
    public static class SitePersistService
    {
        public const string TargetPrefix = "sitepersist:";
        public const string NotifyBlocked = "persist_blocked";
        public const string NotifyCollapsed = "persist_collapsed";
        public const string ExitKey = "exit";

        private static readonly List<EmgPersistItem> Items = new List<EmgPersistItem>(64);
        private static readonly List<int2> Sources = new List<int2>(64);
        private static readonly List<int2> Probes = new List<int2>(1024);
        private static readonly List<int> Owners = new List<int>(1024);
        private static readonly List<int2> Extra = new List<int2>(256);
        private static readonly HashSet<long> ExtraSet = new HashSet<long>();
        private static readonly List<int2> TargetCells = new List<int2>(1024);
        private static readonly List<int> TargetOwners = new List<int>(1024);
        private static readonly List<int2> BlockerCells = new List<int2>(1024);
        private static readonly List<int> BlockerOwners = new List<int>(1024);
        private static uint _floodSig;
        private static BuildingRecord[] _floodBuildings;
        private static CampaignState _floodState;

        /// <summary>自检读点：泛洪次数（没变化不泛洪）与最近一次耗时。</summary>
        public static int Floods { get; private set; }
        public static double LastFloodMs { get; private set; }


        public static float ClearSeconds => Mathf.Max(0.1f, GridContent.Tuning("emg.persist.clear_seconds"));
        public static int ClearScrap => Mathf.Max(0, GridContent.TuningInt("emg.persist.clear_scrap"));

        public static void ResetSessionState()
        {
            _floodSig = 0u;
            _floodBuildings = null;
            _floodState = null;
            Floods = 0;
            LastFloodMs = 0;
        }

        private static CombatSite Home => CombatSites.Get(HomeValleyLayout.RegionId);

        // ── 目标 ID ──────────────────────────────────────────────────────

        public static bool IsClearTarget(string targetId) => targetId != null && targetId.StartsWith(TargetPrefix, StringComparison.Ordinal);

        public static string TargetOf(byte kind, int id) =>
            TargetPrefix + (kind == EmgPersistItem.KindWreck ? "w:" : "p:") + id.ToString(CultureInfo.InvariantCulture);

        private static bool TryParse(string targetId, out byte kind, out int id)
        {
            kind = 0;
            id = 0;
            if (!IsClearTarget(targetId) || targetId.Length < TargetPrefix.Length + 3)
            {
                return false;
            }
            char c = targetId[TargetPrefix.Length];
            kind = c == 'w' ? EmgPersistItem.KindWreck : EmgPersistItem.KindPatch;
            return (c == 'w' || c == 'p') && int.TryParse(targetId.Substring(TargetPrefix.Length + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        public static bool TryGetTarget(string targetId, out EmgPersistItem item)
        {
            item = default;
            CombatSite site = Home;
            return site != null && TryParse(targetId, out byte kind, out int id) && site.PersistSite && site.TryGetPersist(kind, id, out item);
        }

        /// <summary>工单 / 通知里写的目标名：“固化胶块（x, y）”；目标已经没了只写“残骸”或“固态斑块”。</summary>
        public static string DescribeTarget(string targetId)
        {
            if (!TryGetTarget(targetId, out EmgPersistItem it))
            {
                return GameText.Get(TryParse(targetId, out byte gone, out _) && gone == EmgPersistItem.KindWreck ? "emg.hover.wreck.title" : "emg.persist.target_solid");
            }
            GridCell c = NavService.CellOf(it.Pos.x, it.Pos.y);
            string name = it.Kind == EmgPersistItem.KindWreck ? GameText.Get("emg.hover.wreck.title") : EmgMatterCatalog.NameOf(it.Matter);
            return GameText.Format("emg.persist.work_target", name, c.X, c.Y);
        }

        /// <summary>
        /// 机器干活站的位置：阻路的走不进去，取圆外一格、朝归还核心那一侧优先、能走的第一个方向（8 个方向，确定性）。目标没了返回归还核心（下一次检查会取消并返还）。
        /// </summary>
        public static Vector2 WorkPosition(string targetId)
        {
            if (!TryGetTarget(targetId, out EmgPersistItem item))
            {
                return HomeValleyLayout.Core.Position;
            }
            Vector2 at = new Vector2((float)item.Pos.x, (float)item.Pos.y);
            GridCell own = NavService.CellOf(at.x, at.y);
            if (!item.Blocks && (!NavService.IsBound || NavService.PassableNow(own.X, own.Y, NavConst.ClassPlayer)))
            {
                return at;
            }
            Vector2 toCore = HomeValleyLayout.Core.Position - at;
            float start = toCore.sqrMagnitude > 1e-4f ? Mathf.Atan2(toCore.y, toCore.x) : 0f;
            float reach = item.Radius + 1f;
            for (int i = 0; i < 8; i++)
            {
                int step = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1); // 0, +1, −1, +2, −2 …：从朝核心的方向往两边找
                float angle = start + step * Mathf.PI / 4f;
                Vector2 p = at + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach;
                GridCell c = NavService.CellOf(p.x, p.y);
                if (!NavService.IsBound || NavService.PassableNow(c.X, c.Y, NavConst.ClassPlayer))
                {
                    return p;
                }
            }
            return at;
        }

        // ── 下单 / 取消 ───────────────────────────────────────────────────

        /// <summary>对一件长期留存物下清除工作单（进待分配池，按工作优先级领单）。</summary>
        public static HomeValleyWorkOrders.WorkOrderOpResult OrderClear(CampaignState state, string targetId)
        {
            if (!TryGetTarget(targetId, out _))
            {
                return HomeValleyWorkOrders.WorkOrderOpResult.Fail("sitepersist-not-found");
            }
            return HomeValleyWorkOrders.TryCreateSiteClear(state, targetId, ClearSeconds, ClearScrap);
        }

        /// <summary>这件长期留存物还没开工的清除单（已经在干的不算：FGR-EMG-154 只承诺“清除前可取消”）。</summary>
        public static WorkOrderRecord ActiveOrder(CampaignState state, string targetId) =>
            state == null ? null : HomeValleyWorkOrders.FindActiveByTarget(state, WorkOrderKind.Salvage, targetId);

        /// <summary>取消清除单：全额返还预留材料（B03）。正在干的也可以取消（同样全额返还：料在完工时才消耗）。</summary>
        public static bool CancelClear(CampaignState state, string targetId)
        {
            WorkOrderRecord o = ActiveOrder(state, targetId);
            if (o == null)
            {
                return false;
            }
            Vector2 at = TryGetTarget(targetId, out EmgPersistItem item) ? new Vector2((float)item.Pos.x, (float)item.Pos.y) : HomeValleyLayout.Core.Position;
            HomeValleyWorkOrders.CancelOrder(state, o.WorkOrderId, at);
            return true;
        }

        /// <summary>工作单完工：移除目标。目标已经没了返回 false。</summary>
        public static bool TryClearTarget(string targetId)
        {
            CombatSite site = Home;
            return site != null && TryParse(targetId, out byte kind, out int id) && site.ClearPersist(kind, id);
        }

        /// <summary>框里（闭区间，任意两角）的长期留存物：圆心在框内，或点一格时圆盖住这一格。</summary>
        public static void CollectInBox(CampaignState state, GridCell a, GridCell b, List<EmgPersistItem> into)
        {
            into.Clear();
            CombatSite site = Home;
            if (site == null || !site.PersistSite)
            {
                return;
            }
            site.CollectPersist(Items);
            int minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            bool single = minX == maxX && minY == maxY;
            foreach (EmgPersistItem it in Items)
            {
                GridCell c = NavService.CellOf(it.Pos.x, it.Pos.y);
                bool inside = c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY;
                if (inside || (single && math.distance(it.Pos, new double2(minX, minY)) <= it.Radius + 0.5))
                {
                    into.Add(it);
                }
            }
        }

        /// <summary>框选预览（复用清带模式的预览框与状态行）：Cells = 框里每件长期留存物的中心格。</summary>
        public static BeltClearPlan PlanBox(CampaignState state, GridCell a, GridCell b, BeltClearPlan into)
        {
            into.Reset();
            into.Min = new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            into.Max = new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            var list = new List<EmgPersistItem>(16);
            CollectInBox(state, a, b, list);
            foreach (EmgPersistItem it in list)
            {
                GridCell c = NavService.CellOf(it.Pos.x, it.Pos.y);
                into.Cells.Add(new int2(c.X, c.Y));
            }
            return into;
        }

        /// <summary>
        /// 建造模式“清除场地物”的一次点选 / 框选（B02）：框里没有单子的都下单；只点中一件且它已有单子 = 取消（全额返还）。返回给状态行的文本，<paramref name="denied"/> = 什么也没做。
        /// </summary>
        public static string ApplyBox(CampaignState state, GridCell a, GridCell b, out bool denied)
        {
            var list = new List<EmgPersistItem>(16);
            CollectInBox(state, a, b, list);
            denied = false;
            if (list.Count == 0)
            {
                denied = true;
                return GameText.Get("emg.persist.clear_none");
            }
            if (list.Count == 1 && ActiveOrder(state, TargetOf(list[0].Kind, list[0].Id)) != null)
            {
                CancelClear(state, TargetOf(list[0].Kind, list[0].Id));
                return GameText.Get("emg.persist.clear_cancelled");
            }
            int ordered = 0, already = 0;
            foreach (EmgPersistItem it in list)
            {
                string target = TargetOf(it.Kind, it.Id);
                if (ActiveOrder(state, target) != null)
                {
                    already++;
                }
                else if (OrderClear(state, target).Success)
                {
                    ordered++;
                }
            }
            denied = ordered == 0;
            return ordered > 0 ? GameText.Format("emg.persist.clear_ordered", ordered, already) : GameText.Format("emg.persist.clear_already", already);
        }

        /// <summary>悬停补一行：长期留存的怎么清除 / 已排单怎么取消。不是长期留存地点返回空串。</summary>
        public static string HoverHint(CampaignState state, CombatSite site, byte kind, int id)
        {
            if (site == null || !site.PersistSite || state == null)
            {
                return string.Empty;
            }
            return GameText.Get(ActiveOrder(state, TargetOf(kind, id)) != null ? "emg.persist.hover_queued" : "emg.persist.hover_clear");
        }

        // ── 定时检查（SoftlockService 调）────────────────────────────────────

        /// <summary>目标先没了（被摧毁 / 融化 / 回收 / 崩解）的清除单取消并全额返还；崩解计数涨了通知一次；长期留存物或建筑变了重算堵路。</summary>
        public static void Check(CampaignState state)
        {
            CombatSite site = Home;
            if (state == null || site == null || !site.PersistSite)
            {
                return;
            }
            foreach (WorkOrderRecord o in state.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Salvage && IsClearTarget(o.TargetId) && HomeValleyWorkOrders.IsActive(o) && !TryGetTarget(o.TargetId, out _))
                {
                    HomeValleyWorkOrders.CancelOrder(state, o.WorkOrderId, HomeValleyLayout.Core.Position);
                }
            }
            SoftlockState st = SoftlockService.StateOf(state);
            if (st == null)
            {
                return;
            }
            int capped = site.PersistCappedTotal;
            if (st.PersistCappedSeen >= 0 && capped > st.PersistCappedSeen)
            {
                NotificationCenter.Post(NotifyCollapsed, GameText.Format("emg.persist.collapsed", capped - st.PersistCappedSeen));
            }
            st.PersistCappedSeen = capped;
            CheckBlocks(state, st, site);
        }

        private static void CheckBlocks(CampaignState state, SoftlockState st, CombatSite site)
        {
            uint sig = site.PersistSignature();
            if (ReferenceEquals(_floodState, state) && sig == _floodSig && ReferenceEquals(_floodBuildings, state.BuildingRecords))
            {
                return;
            }
            if (!NavService.IsBound || !ReferenceEquals(NavService.BoundState, state) || !HomeGridService.TryGetCoreBounds(state, out GridCell coreMin, out GridCell coreMax))
            {
                return; // 寻路镜像没绑定：无法判断，什么都不改（下一次再看）。
            }
            _floodState = state;
            _floodSig = sig;
            _floodBuildings = state.BuildingRecords;
            site.CollectPersist(Items);
            Items.RemoveAll(it => it.Kind != EmgPersistItem.KindWreck && !it.Blocks); // 冷渣 / 冰 / 玻化层能走，不算堵路
            if (Items.Count == 0)
            {
                SetBlocks(st, Array.Empty<PersistBlockRecord>());
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            NavService.SyncGridChanges();
            int margin = Math.Max(2, (int)Math.Round(NavService.Tuning("nav.preview_margin_cells", 16f)));
            int minX = coreMin.X, minY = coreMin.Y, maxX = coreMax.X, maxY = coreMax.Y;
            var targets = new List<BuildingRecord>(4);
            var rects = new List<int4>(4); // 每座仓库的占地（x0, y0, x1, y1）
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && BuildingOps.IsWarehouse(b) && GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g))
                {
                    GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation), out GridCell bmin, out GridCell bmax);
                    targets.Add(b);
                    rects.Add(new int4(bmin.X, bmin.Y, bmax.X, bmax.Y));
                    minX = Math.Min(minX, bmin.X);
                    minY = Math.Min(minY, bmin.Y);
                    maxX = Math.Max(maxX, bmax.X);
                    maxY = Math.Max(maxY, bmax.Y);
                }
            }
            minX -= margin;
            minY -= margin;
            maxX += margin;
            maxY += margin;
            // 泛洪只在这个框里走：框外的留存物（远处野地里的残骸）堵不到框内的路，不用泛洪。
            Items.RemoveAll(it => it.Pos.x + it.Radius + 1.5 < minX || it.Pos.x - it.Radius - 1.5 > maxX || it.Pos.y + it.Radius + 1.5 < minY || it.Pos.y - it.Radius - 1.5 > maxY);
            if (Items.Count == 0)
            {
                SetBlocks(st, Array.Empty<PersistBlockRecord>());
                return;
            }
            // 覆盖格（残骸能减速穿过，算“堵”只用于警告；阻路斑块本来就走不了，外扩半格让相切的圆也连成墙）
            Extra.Clear();
            ExtraSet.Clear();
            foreach (EmgPersistItem it in Items)
            {
                ForCircle(it, it.Radius + 0.5f, c =>
                {
                    if (ExtraSet.Add(CellKey(c)))
                    {
                        Extra.Add(c);
                    }
                });
            }
            // 泛洪目标：每座仓库外一圈（owner = 下标）+ 外框（owner = 仓库数）
            void TargetRings(List<int2> cells, List<int> owners, bool raw)
            {
                for (int i = 0; i < rects.Count; i++)
                {
                    AddRing(rects[i].x - 1, rects[i].y - 1, rects[i].z + 1, rects[i].w + 1, i, cells, owners, raw);
                }
                AddRing(minX, minY, maxX, maxY, rects.Count, cells, owners, raw);
            }
            TargetCells.Clear();
            TargetOwners.Clear();
            TargetRings(TargetCells, TargetOwners, false);
            // 每件留存物外一圈（owner = 目标数 + 下标）：归因用，见 PickBlocker
            int targetCount = targets.Count + 1;
            BlockerCells.Clear();
            BlockerOwners.Clear();
            for (int i = 0; i < Items.Count; i++)
            {
                EmgPersistItem it = Items[i];
                int r = (int)Math.Ceiling(it.Radius + 1.5f);
                GridCell c = NavService.CellOf(it.Pos.x, it.Pos.y);
                AddRing(c.X - r, c.Y - r, c.X + r, c.Y + r, targetCount + i, BlockerCells, BlockerOwners);
            }
            Sources.Clear();
            var ignore = new List<int>(4);
            AddRing(coreMin.X - 1, coreMin.Y - 1, coreMax.X + 1, coreMax.Y + 1, 0, Sources, ignore); // 核心贴身被盖满时 Sources 为空：所有目标到不了，由下面的基线判是不是留存物造成的
            Probes.Clear();
            Owners.Clear();
            Probes.AddRange(TargetCells);
            Owners.AddRange(TargetOwners);
            Probes.AddRange(BlockerCells);
            Owners.AddRange(BlockerOwners);
            var fromCore = new bool[targetCount + Items.Count];
            var min = new int2(minX, minY);
            var max = new int2(maxX, maxY);
            NavService.Kernel.Reachability(NavConst.ClassPlayer, min, max, Sources, Extra, Probes, Owners, fromCore);
            Floods++;
            var blocks = new List<PersistBlockRecord>(2);
            bool[] possible = null;
            for (int t = 0; t < targetCount; t++)
            {
                if (fromCore[t])
                {
                    continue;
                }
                if (possible == null)
                {
                    // 基线（第一个到不了的目标才算）：假设留存物都不在。这样也到不了 = 地形 / 建筑造成的，不归留存物；到得了 = 就是留存物堵的，必须报警
                    possible = new bool[targetCount];
                    Sources.Clear();
                    ignore.Clear();
                    AddRing(coreMin.X - 1, coreMin.Y - 1, coreMax.X + 1, coreMax.Y + 1, 0, Sources, ignore, true);
                    Probes.Clear();
                    Owners.Clear();
                    TargetRings(Probes, Owners, true);
                    NavService.Kernel.Reachability(NavConst.ClassPlayer, min, max, Sources, null, Probes, Owners, possible, ignoreDynamic: true);
                    Floods++;
                }
                if (!possible[t])
                {
                    continue;
                }
                // 从目标一侧再泛洪一次（目标外一圈全被盖住时这一侧什么也碰不到）
                Sources.Clear();
                for (int k = 0; k < TargetCells.Count; k++)
                {
                    if (TargetOwners[k] == t)
                    {
                        Sources.Add(TargetCells[k]);
                    }
                }
                var fromTarget = new bool[targetCount + Items.Count];
                if (Sources.Count > 0)
                {
                    Probes.Clear();
                    Owners.Clear();
                    Probes.AddRange(BlockerCells);
                    Owners.AddRange(BlockerOwners);
                    NavService.Kernel.Reachability(NavConst.ClassPlayer, min, max, Sources, Extra, Probes, Owners, fromTarget);
                    Floods++;
                }
                bool exit = t >= targets.Count;
                var core = new double2((coreMin.X + coreMax.X) * 0.5, (coreMin.Y + coreMax.Y) * 0.5);
                var goal = exit ? core : new double2((rects[t].x + rects[t].z) * 0.5, (rects[t].y + rects[t].w) * 0.5);
                EmgPersistItem hit = Items[PickBlocker(fromCore, fromTarget, targetCount, core, goal, exit)];
                string key = exit ? ExitKey : targets[t].BuildingId;
                blocks.Add(new PersistBlockRecord
                {
                    Key = key, Kind = hit.Kind, ItemId = hit.Id, Blocks = hit.Blocks, X = (float)hit.Pos.x, Y = (float)hit.Pos.y,
                    Alerted = Array.Exists(st.PersistBlocks, r => r != null && r.Key == key && r.Alerted),
                });
            }
            LastFloodMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            blocks.Sort((a, c) => string.CompareOrdinal(a.Key, c.Key));
            foreach (PersistBlockRecord r in blocks)
            {
                if (!r.Alerted)
                {
                    r.Alerted = true;
                    GridCell c = NavService.CellOf(r.X, r.Y);
                    NotificationCenter.Post(NotifyBlocked, GameText.Format(r.Blocks ? "emg.persist.blocked_solid" : "emg.persist.blocked_wreck", TargetName(state, r.Key), c.X, c.Y),
                        new Vector3(r.X, 0f, r.Y));
                }
            }
            SetBlocks(st, blocks.ToArray());
        }

        private static void SetBlocks(SoftlockState st, PersistBlockRecord[] blocks)
        {
            if (st.PersistBlocks.Length != blocks.Length || blocks.Length > 0)
            {
                SoftlockService.TouchRevision();
            }
            st.PersistBlocks = blocks;
        }

        /// <summary>被堵的是哪个目标（当前语言）。</summary>
        public static string TargetName(CampaignState state, string key)
        {
            if (key == ExitKey)
            {
                return GameText.Get("emg.persist.target_exit");
            }
            BuildingRecord b = HomeGridService.FindBuilding(state, key);
            return b != null ? BuildingOps.NameOf(b) : key;
        }

        /// <summary>
        /// 警告标哪一件（基线已确认是留存物堵的）：① 单层墙：两侧都碰得到、离核心最近的那件；② 墙厚了没有哪件两侧都碰得到：核心一侧碰得到、离目标最近的那件（出入口按离核心最远）——清了它就往里推进一层；
        /// ③ 核心贴身被盖满、这一侧什么也碰不到：离核心最近的那件。Items 非空才调用，一定选得出。
        /// </summary>
        private static int PickBlocker(bool[] fromCore, bool[] fromTarget, int offset, double2 core, double2 goal, bool exit)
        {
            int both = -1, near = -1, any = -1;
            for (int i = 0; i < Items.Count; i++)
            {
                if (fromCore[offset + i])
                {
                    if (fromTarget[offset + i] && (both < 0 || Better(Items[i], Items[both], core, false)))
                    {
                        both = i;
                    }
                    if (near < 0 || Better(Items[i], Items[near], goal, exit))
                    {
                        near = i;
                    }
                }
                if (any < 0 || Better(Items[i], Items[any], core, false))
                {
                    any = i;
                }
            }
            return both >= 0 ? both : near >= 0 ? near : any;
        }

        private static bool Better(in EmgPersistItem a, in EmgPersistItem b, double2 to, bool farthest)
        {
            double da = math.distancesq(a.Pos, to), db = math.distancesq(b.Pos, to);
            return (farthest ? da > db : da < db) || (da == db && (a.Kind < b.Kind || (a.Kind == b.Kind && a.Id < b.Id)));
        }

        private static long CellKey(int2 c) => ((long)c.x << 32) ^ (uint)c.y;

        private static void ForCircle(in EmgPersistItem it, float r, Action<int2> visit)
        {
            int x0 = (int)Math.Floor(it.Pos.x - r), x1 = (int)Math.Ceiling(it.Pos.x + r), y0 = (int)Math.Floor(it.Pos.y - r), y1 = (int)Math.Ceiling(it.Pos.y + r);
            double r2 = (double)r * r;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if ((x - it.Pos.x) * (x - it.Pos.x) + (y - it.Pos.y) * (y - it.Pos.y) <= r2)
                    {
                        visit(new int2(x, y));
                    }
                }
            }
        }

        /// <summary>矩形外框上能走、且没被留存物盖住的格；<paramref name="raw"/> = 不筛（基线泛洪自己判能不能走）。</summary>
        private static void AddRing(int x0, int y0, int x1, int y1, int owner, List<int2> cells, List<int> owners, bool raw = false)
        {
            for (int x = x0; x <= x1; x++)
            {
                AddCell(x, y0, owner, cells, owners, raw);
                if (y1 != y0)
                {
                    AddCell(x, y1, owner, cells, owners, raw);
                }
            }
            for (int y = y0 + 1; y < y1; y++)
            {
                AddCell(x0, y, owner, cells, owners, raw);
                if (x1 != x0)
                {
                    AddCell(x1, y, owner, cells, owners, raw);
                }
            }
        }

        private static void AddCell(int x, int y, int owner, List<int2> cells, List<int> owners, bool raw)
        {
            var c = new int2(x, y);
            if (raw || (!ExtraSet.Contains(CellKey(c)) && NavService.PassableNow(x, y, NavConst.ClassPlayer)))
            {
                cells.Add(c);
                owners.Add(owner);
            }
        }

        // ── 离家报告 / 调试 ──────────────────────────────────────────────

        /// <summary>此刻家园的残骸数 / 固态斑块数 / 累计崩解数（突袭结算开始与结束各取一次）。</summary>
        public static (int Wrecks, int Solids, int Capped) Snapshot(CombatSite site)
        {
            if (site == null || site.IsDisposed)
            {
                return (0, 0, 0);
            }
            (int w, int s) = site.PersistTally();
            return (w, s, site.PersistCappedTotal);
        }
    }
}
