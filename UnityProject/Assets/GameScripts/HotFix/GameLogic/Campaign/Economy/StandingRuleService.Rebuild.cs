using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
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

        // ── 传送带虚影 ─────────────────────────────────────────────────────────────

        private static void HandleBeltGhost(CampaignState state, string planId)
        {
            PlannedBeltRecord p = HomeValleyConstruction.FindPlan(state, planId);
            if (p == null || !p.Destroyed || p.Xs == null || p.Xs.Length == 0)
            {
                return; // 已被玩家重建 / 移除：不用管
            }
            var cell = new GridCell(p.Xs[0], p.Ys[0]);
            StandingRuleRecord r = RebuildRuleFor(state, BeltTarget, (cell, cell));
            if (r == null)
            {
                return; // 没有覆盖这里的自动重建规则：留下虚影，等玩家在施工队列里点“重建”（FGR-BASE-020）
            }
            if (!HomeValleyConstruction.RebuildDestroyed(state, p.PlanId))
            {
                return;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveBuild(state, HomeValleyConstruction.BeltPlanPrefix + p.PlanId);
            if (o != null)
            {
                o.RuleSerial = r.Serial;
            }
            Fired(state, r, "rules.log.rebuild_belt", "c:" + cell.X.ToString(CultureInfo.InvariantCulture) + "," + cell.Y.ToString(CultureInfo.InvariantCulture),
                LabelArg(r.Serial), "@raw:" + HomeValleyConstruction.PieceName(p) + " (" + cell.X.ToString(CultureInfo.InvariantCulture) + ", " + cell.Y.ToString(CultureInfo.InvariantCulture) + ")");
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
                if (e != null && e.Kind == EventRebuildRetry)
                {
                    waiting.Add(e.EntityId);
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
            if (retry.Count > 0)
            {
                var list = new List<RuleEventRecord>(d.Pending);
                list.AddRange(retry);
                d.Pending = list.ToArray();
            }
            PlannedBeltRecord[] plans = state.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            var ghosts = new List<string>();
            foreach (PlannedBeltRecord p in plans)
            {
                if (p != null && p.Destroyed)
                {
                    ghosts.Add(p.PlanId);
                }
            }
            foreach (string id in ghosts)
            {
                PlannedBeltRecord before = HomeValleyConstruction.FindPlan(state, id);
                HandleBeltGhost(state, id);
                if (before != null && !before.Destroyed)
                {
                    queued++;
                }
            }
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
