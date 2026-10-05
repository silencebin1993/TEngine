using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    public enum DefenseRowKind : byte
    {
        Turret = 0,
        Trap = 1,
        Shield = 2,
        Garrison = 3,
    }

    /// <summary>防御总览列表的一行（炮塔 / 陷阱 / 护盾 / 驻防机器）。界面只读这里，不另算。</summary>
    public sealed class DefenseRow
    {
        public DefenseRowKind Kind;
        /// <summary>建筑 ID（炮塔 / 陷阱 / 护盾）或机器 LogicId 的字符串（驻防机器）。</summary>
        public string Id = string.Empty;
        public int LogicId;
        public string Name = string.Empty;
        public string Text = string.Empty;
        public bool Problem;
        public bool Destroyed;
        public Vector2 Position;
        public int Kills;
        public string ModeName = string.Empty;
        public bool SupplyShort;
    }

    /// <summary>防御总览的汇总一行。</summary>
    public struct DefenseSummary
    {
        public int Turrets;
        public int TurretsOk;
        public int TurretsProblem;
        public int TurretsDestroyed;
        public int Traps;
        public int Shields;
        public int Garrison;
        public int TotalKills;
    }

    /// <summary>一处薄弱点（沿预测路线 / 外围没有火力覆盖的区段）。</summary>
    public struct WeakPoint
    {
        public CoverageRun Run;
        /// <summary>沿哪一波的预测路线（0 = 外围检查）。</summary>
        public int Wave;
        public float NearDist;
        public float FarDist;
        public string Direction;
    }

    /// <summary>覆盖热力图 + 薄弱点（一次计算的结果；防线 / 突袭计划不变时复用）。</summary>
    public sealed class DefenseCoverageMap
    {
        public int Ox;
        public int Oy;
        public int W;
        public int H;
        public byte[] Counts = Array.Empty<byte>();
        public int Covered;
        public int Covered2;
        /// <summary>计入覆盖的炮塔数（建成、装了可用蓝图）与不计入的（被摧毁 / 蓝图不可用 / 还在施工）。</summary>
        public int Counted;
        public int Skipped;
        public Vector2 Core;
        public readonly List<Vector2> TurretPoints = new List<Vector2>();
        /// <summary>预测路线（格坐标拐点；每条一个数组对）与它属于哪一波。</summary>
        public readonly List<int[]> RouteXs = new List<int[]>();
        public readonly List<int[]> RouteYs = new List<int[]>();
        public readonly List<int> RouteWaves = new List<int>();
        public readonly List<WeakPoint> Weak = new List<WeakPoint>();
        /// <summary>没有列出的薄弱点数（超过 defov.weak_max）。</summary>
        public int WeakHidden;
        /// <summary>没有预测路线：按外围一圈检查。</summary>
        public bool RingMode;
        public float RingRadius;
        public int RouteCellsWalked;
        public int Key;
        public double BuildMs;
    }

    /// <summary>
    /// FG6-DEF-06（FG06 FGR-DEF-070 防御面板；FG13 FGU-27 防御总览；承接 DEBT-FG6DEF01-05 / DEBT-FG6DEF02-07 的“防御总览”部分、DEBT-FG4ECO07-02“防御总览列出驻防机器”）：
    /// - 列表：所有炮塔（状态与原因、补给、目标模式、击毁 / 精英击毁）、陷阱发射器（固件、补给、铺设轮数）、护盾发生器（护盾值、过载、累计吸收）、驻防机器（驻防点 / 巡逻、耐久、击毁）。
    ///   读数一律来自各服务的 Readout（与炮塔面板 / 防御面板同一份数据）。O(炮塔 + 防御建筑 + 机器)，只在面板开着时按间隔调用。
    /// - 覆盖热力图：建成且装了可用蓝图的炮塔按当前射程（含等级倍率）画圆叠加（缺补给的照样算覆盖、在列表里标出）；栅格化在 AOT（<see cref="CombatCoverage"/>）。
    ///   范围 = 家园己方建筑外接矩形 + defov.margin_cells，最大 defov.max_cells（超出以核心为中心截取）。
    /// - 薄弱点：沿预测的突袭路线（已预警 / 有情报的计划的排定路线；已出发的按队伍剩下的路线）逐格走，栅格内连续 ≥ defov.weak_min_cells 格没有覆盖的区段；
    ///   没有预测路线时按家园外围一圈（建筑外接矩形半对角线 + defov.ring_extra_cells）检查。按离核心由近到远，最多 defov.weak_max 处。
    /// 只读，不改任何状态；结果只取决于防线与计划（与观察 / 倍速无关）。
    /// </summary>
    public static class DefenseOverviewService
    {
        public static int MarginCells => Math.Max(0, (int)Math.Round(T("defov.margin_cells", 24f)));
        public static int MaxCells => Math.Max(64, (int)Math.Round(T("defov.max_cells", 256f)));
        public static int WeakMinCells => Math.Max(1, (int)Math.Round(T("defov.weak_min_cells", 4f)));
        public static int WeakMax => Math.Max(1, (int)Math.Round(T("defov.weak_max", 12f)));
        public static float RingExtraCells => Math.Max(0f, T("defov.ring_extra_cells", 10f));
        public static int RingSamples => Math.Max(16, (int)Math.Round(T("defov.ring_samples", 96f)));
        public static float RefreshSeconds => Math.Max(0.1f, T("defov.refresh_seconds", 1f));
        public static float PerfBudgetMs => Math.Max(0.1f, T("defov.perf_ms", 8f));

        // ─────────────────────────────── 列表 ───────────────────────────────

        public static void CollectRows(CampaignState s, List<DefenseRow> into, out DefenseSummary sum)
        {
            into.Clear();
            sum = default;
            if (s == null)
            {
                return;
            }
            foreach (TurretRecord r in TurretService.All(s))
            {
                if (r == null || !TurretService.TryGetReadout(s, r.BuildingId, out TurretReadout ro))
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(s, r.BuildingId);
                bool destroyed = ro.Status.Kind == BuildingStatusKind.Destroyed;
                bool problem = destroyed || ro.SupplyShort || !ro.Valid || ro.Overheated || IsProblemKind(ro.Status.Kind);
                string mode = TurretCatalog.ModeName(ro.TargetMode);
                string supply = string.IsNullOrEmpty(ro.SupplyLine) ? GameText.Get("defov.supply_power_only") : ro.SupplyLine;
                string status = destroyed ? GameText.Get("defov.status.destroyed") : ro.Status.Reason;
                into.Add(new DefenseRow
                {
                    Kind = DefenseRowKind.Turret, Id = r.BuildingId, Name = ro.Name, Problem = problem, Destroyed = destroyed, Position = b?.Position ?? Vector2.zero,
                    Kills = ro.Kills, ModeName = mode, SupplyShort = ro.SupplyShort,
                    Text = GameText.Format("defov.row.turret", Prefix(problem, destroyed), ro.Name, status, supply, mode, ro.Kills, ro.EliteKills),
                });
                sum.Turrets++;
                if (destroyed)
                {
                    sum.TurretsDestroyed++;
                }
                else if (problem)
                {
                    sum.TurretsProblem++;
                }
                else
                {
                    sum.TurretsOk++;
                }
            }
            sum.TotalKills = TurretService.StateOf(s)?.TotalKills ?? 0;
            foreach (DefenseRecord d in DefenseService.All(s))
            {
                BuildingRecord b = d != null ? HomeGridService.FindBuilding(s, d.BuildingId) : null;
                if (b == null)
                {
                    continue;
                }
                BuildingStatus st = BuildingStatusService.Evaluate(s, b);
                bool destroyed = st.Kind == BuildingStatusKind.Destroyed;
                string status = destroyed ? GameText.Get("defov.status.destroyed") : st.Reason;
                if (DefenseService.TryGetTrapReadout(s, d.BuildingId, out TrapReadout tr))
                {
                    bool problem = destroyed || tr.SupplyShort || !tr.Valid || IsProblemKind(st.Kind);
                    string fw = string.IsNullOrEmpty(tr.FirmwareName) ? GameText.Get("defov.status.no_trap_fw") : tr.FirmwareName;
                    into.Add(new DefenseRow
                    {
                        Kind = DefenseRowKind.Trap, Id = d.BuildingId, Name = tr.Name, Problem = problem, Destroyed = destroyed, Position = b.Position, SupplyShort = tr.SupplyShort,
                        Text = GameText.Format("defov.row.trap", Prefix(problem, destroyed), tr.Name, status, fw,
                            string.IsNullOrEmpty(tr.SupplyLine) ? "-" : tr.SupplyLine, tr.Lays.ToString(CultureInfo.InvariantCulture)),
                    });
                    sum.Traps++;
                }
                else if (DefenseService.TryGetShieldReadout(s, d.BuildingId, out ShieldReadout sr))
                {
                    bool problem = destroyed || sr.Capped || !sr.Powered || IsProblemKind(st.Kind);
                    into.Add(new DefenseRow
                    {
                        Kind = DefenseRowKind.Shield, Id = d.BuildingId, Name = sr.Name, Problem = problem, Destroyed = destroyed, Position = b.Position,
                        Text = GameText.Format("defov.row.shield", Prefix(problem, destroyed), sr.Name, status, Mathf.RoundToInt(sr.Hp), Mathf.RoundToInt(sr.MaxHp), sr.Overloads,
                            Mathf.RoundToInt((float)sr.Absorbed)),
                    });
                    sum.Shields++;
                }
            }
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.Role != MachineRole.Garrison || !MachineRoster.IsHome(m))
                {
                    continue;
                }
                bool suspended = MachineRoster.GarrisonSuspended(s, m) || MachineRoster.GarrisonBlocked(s, m);
                string where = MachineRoster.HasPatrol(s, m) ? MachineRoster.PatrolText(s, m) : MachineRoster.GarrisonPointLabel(s, m);
                into.Add(new DefenseRow
                {
                    Kind = DefenseRowKind.Garrison, Id = m.LogicId.ToString(CultureInfo.InvariantCulture), LogicId = m.LogicId, Name = MachineNaming.Short(m), Problem = suspended,
                    Position = m.WorldPosition, Kills = m.KillCount,
                    Text = GameText.Format("defov.row.garrison", Prefix(suspended, false), MachineNaming.Short(m), where, MachineRoster.CurrentWorkText(s, m),
                        Mathf.RoundToInt(m.Health), Mathf.RoundToInt(m.MaxHealth), m.KillCount),
                });
                sum.Garrison++;
            }
            // 有问题的排前面，同类按名字（稳定、与建造顺序无关）。
            into.Sort((a, b) =>
            {
                if (a.Problem != b.Problem)
                {
                    return a.Problem ? -1 : 1;
                }
                if (a.Kind != b.Kind)
                {
                    return a.Kind.CompareTo(b.Kind);
                }
                int c = string.CompareOrdinal(a.Name, b.Name);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
        }

        private static bool IsProblemKind(BuildingStatusKind k) =>
            k == BuildingStatusKind.NoPower || k == BuildingStatusKind.NoFluid || k == BuildingStatusKind.NoMaterial || k == BuildingStatusKind.Disabled
            || k == BuildingStatusKind.Destroyed;

        private static string Prefix(bool problem, bool destroyed) =>
            GameText.Get(destroyed ? "defov.row.destroyed" : problem ? "defov.row.problem" : "defov.row.ok");

        public static string SummaryText(in DefenseSummary s) =>
            GameText.Format("defov.summary", s.Turrets, s.TurretsOk, s.TurretsProblem, s.TurretsDestroyed, s.Traps, s.Shields, s.Garrison, s.TotalKills);

        // ─────────────────────────────── 覆盖与薄弱点 ───────────────────────────────

        /// <summary>覆盖要不要重算的键（炮塔 / 防御 / 建筑 / 突袭计划 / 行进队伍变了才变）。O(计划数 + 队伍数)。</summary>
        public static int CoverageKey(CampaignState s)
        {
            if (s == null)
            {
                return 0;
            }
            int h = HashCode.Combine(TurretService.Revision, s.BuildingRecords?.Length ?? 0, RaidDirectorService.Revision, HomeGridService.MapFor(s)?.Revision ?? 0);
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (g != null && g.Kind == TransitGroupKind.Raid)
                {
                    h = HashCode.Combine(h, g.State, g.RouteIndex, g.RouteX?.Length ?? 0);
                }
            }
            foreach (TurretRecord r in TurretService.All(s))
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(s, r.BuildingId) : null;
                h = HashCode.Combine(h, b != null ? (int)b.ConstructionState : -1, r?.BlueprintVersion ?? 0);
            }
            return h;
        }

        private static readonly List<CoverageRun> RunScratch = new List<CoverageRun>(32);

        /// <summary>算一次覆盖热力图与薄弱点（只在面板打开 / 防线或计划变化时）。</summary>
        public static DefenseCoverageMap BuildCoverage(CampaignState s, DefenseCoverageMap reuse = null)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            DefenseCoverageMap map = reuse ?? new DefenseCoverageMap();
            map.TurretPoints.Clear();
            map.RouteXs.Clear();
            map.RouteYs.Clear();
            map.RouteWaves.Clear();
            map.Weak.Clear();
            map.WeakHidden = 0;
            map.Counted = 0;
            map.Skipped = 0;
            map.RingMode = false;
            map.RouteCellsWalked = 0;
            map.Key = CoverageKey(s);
            if (s == null)
            {
                return map;
            }
            HomeGridService.TryGetCoreBounds(s, out GridCell ca, out GridCell cb);
            map.Core = new Vector2((ca.X + cb.X) * 0.5f, (ca.Y + cb.Y) * 0.5f);

            // 范围：家园己方建筑外接矩形 + 边距（最大边长截到以核心为中心）。
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                x0 = Math.Min(x0, Mathf.FloorToInt(b.Position.x));
                y0 = Math.Min(y0, Mathf.FloorToInt(b.Position.y));
                x1 = Math.Max(x1, Mathf.CeilToInt(b.Position.x));
                y1 = Math.Max(y1, Mathf.CeilToInt(b.Position.y));
            }
            if (x0 > x1)
            {
                x0 = x1 = Mathf.RoundToInt(map.Core.x);
                y0 = y1 = Mathf.RoundToInt(map.Core.y);
            }
            int margin = MarginCells;
            x0 -= margin;
            y0 -= margin;
            x1 += margin;
            y1 += margin;
            int max = MaxCells;
            if (x1 - x0 + 1 > max)
            {
                x0 = Mathf.RoundToInt(map.Core.x) - max / 2;
                x1 = x0 + max - 1;
            }
            if (y1 - y0 + 1 > max)
            {
                y0 = Mathf.RoundToInt(map.Core.y) - max / 2;
                y1 = y0 + max - 1;
            }
            map.Ox = x0;
            map.Oy = y0;
            map.W = x1 - x0 + 1;
            map.H = y1 - y0 + 1;
            if (map.Counts.Length < map.W * map.H)
            {
                map.Counts = new byte[map.W * map.H];
            }

            // 炮塔：建成、蓝图可用的按当前射程计入。
            var xs = new List<float>();
            var ys = new List<float>();
            var rs = new List<float>();
            foreach (TurretRecord r in TurretService.All(s))
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(s, r.BuildingId) : null;
                if (b == null)
                {
                    continue;
                }
                bool built = b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled;
                float range = built && !HomeGridService.IsRelocationGhost(b) ? TurretService.RangeOfTurret(s, r.BuildingId) : 0f;
                if (range <= 0f)
                {
                    map.Skipped++;
                    continue;
                }
                xs.Add(b.Position.x);
                ys.Add(b.Position.y);
                rs.Add(range);
                map.TurretPoints.Add(b.Position);
                map.Counted++;
            }
            map.Covered = CombatCoverage.Rasterize(map.Ox, map.Oy, map.W, map.H, xs.ToArray(), ys.ToArray(), rs.ToArray(), xs.Count, map.Counts, out map.Covered2);

            // 预测路线：已预警 / 有情报的计划（排定路线）、已出发还没到的队伍（剩下的路线）。目标是前哨站的不算（防御总览看家园）。
            foreach (RaidWaveView v in RaidDirectorService.IncomingWaves(s, GameClock.Ticks))
            {
                if (v.Arrived || v.Lead == null)
                {
                    continue;
                }
                foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
                {
                    if (p == null || p.Wave != v.Wave || p.TargetKind == RaidDirectorService.TargetOutpost || p.State >= RaidDirectorService.StateEnded)
                    {
                        continue;
                    }
                    if (p.State == RaidDirectorService.StateDeparted)
                    {
                        TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
                        if (g == null || g.State != TransitGroupState.Marching || g.RouteX == null || g.RouteX.Length == 0)
                        {
                            continue;
                        }
                        int from = Math.Max(0, Math.Min(g.RouteIndex, g.RouteX.Length - 1));
                        int len = g.RouteX.Length - from + 1;
                        var rx = new int[len];
                        var ry = new int[len];
                        rx[0] = (int)Math.Round(g.PosX);
                        ry[0] = (int)Math.Round(g.PosY);
                        Array.Copy(g.RouteX, from, rx, 1, len - 1);
                        Array.Copy(g.RouteY, from, ry, 1, len - 1);
                        AddRoute(map, rx, ry, p.Wave);
                    }
                    else if (p.RouteX.Length > 0 && (p.State >= RaidDirectorService.StateWarned || v.IntelKnown))
                    {
                        var rx = new int[p.RouteX.Length + 1];
                        var ry = new int[p.RouteY.Length + 1];
                        rx[0] = p.OriginX;
                        ry[0] = p.OriginY;
                        Array.Copy(p.RouteX, 0, rx, 1, p.RouteX.Length);
                        Array.Copy(p.RouteY, 0, ry, 1, p.RouteY.Length);
                        AddRoute(map, rx, ry, p.Wave);
                    }
                }
            }

            var weak = new List<WeakPoint>();
            int minRun = WeakMinCells;
            for (int k = 0; k < map.RouteXs.Count; k++)
            {
                RunScratch.Clear();
                CombatCoverage.UncoveredRuns(map.Counts, map.Ox, map.Oy, map.W, map.H, map.RouteXs[k], map.RouteYs[k], map.RouteXs[k].Length, minRun, RunScratch, out int walked);
                map.RouteCellsWalked += walked;
                foreach (CoverageRun run in RunScratch)
                {
                    weak.Add(MakeWeak(map, run, map.RouteWaves[k]));
                }
            }
            if (map.RouteXs.Count == 0)
            {
                map.RingMode = true;
                float halfDiag = 0.5f * Mathf.Sqrt((float)(x1 - x0 - 2 * margin) * (x1 - x0 - 2 * margin) + (float)(y1 - y0 - 2 * margin) * (y1 - y0 - 2 * margin));
                map.RingRadius = Mathf.Max(8f, halfDiag + RingExtraCells);
                RunScratch.Clear();
                CombatCoverage.RingRuns(map.Counts, map.Ox, map.Oy, map.W, map.H, map.Core.x, map.Core.y, map.RingRadius, RingSamples, minRun, RunScratch);
                foreach (CoverageRun run in RunScratch)
                {
                    weak.Add(MakeWeak(map, run, 0));
                }
            }
            weak.Sort((a, b) => a.NearDist != b.NearDist ? a.NearDist.CompareTo(b.NearDist) : a.Run.StartIndex.CompareTo(b.Run.StartIndex));
            int cap = WeakMax;
            for (int i = 0; i < weak.Count; i++)
            {
                if (i < cap)
                {
                    map.Weak.Add(weak[i]);
                }
                else
                {
                    map.WeakHidden++;
                }
            }
            if (map.Weak.Count > 0)
            {
                GuidanceHooks.Raise(GuidanceHooks.DefenseFirstWeakPoint);
            }
            map.BuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return map;
        }

        private static void AddRoute(DefenseCoverageMap map, int[] rx, int[] ry, int wave)
        {
            map.RouteXs.Add(rx);
            map.RouteYs.Add(ry);
            map.RouteWaves.Add(wave);
        }

        private static WeakPoint MakeWeak(DefenseCoverageMap map, in CoverageRun run, int wave)
        {
            float d0 = Vector2.Distance(new Vector2(run.StartX, run.StartY), map.Core);
            float d1 = Vector2.Distance(new Vector2(run.EndX, run.EndY), map.Core);
            var mid = new Vector2(run.MidX, run.MidY);
            return new WeakPoint
            {
                Run = run, Wave = wave, NearDist = Mathf.Min(d0, d1), FarDist = Mathf.Max(d0, d1),
                Direction = IntelService.DirectionName(mid.x - map.Core.x, mid.y - map.Core.y),
            };
        }

        /// <summary>一处薄弱点的文字（第几处、沿哪一波的路线、离核心多远、方向、长度）。</summary>
        public static string WeakText(DefenseCoverageMap map, int index)
        {
            if (map == null || index < 0 || index >= map.Weak.Count)
            {
                return string.Empty;
            }
            WeakPoint w = map.Weak[index];
            string tag = GameText.Format("defov.weak.tag", index + 1);
            return w.Wave > 0
                ? GameText.Format("defov.weak.route", tag, w.Wave, Mathf.RoundToInt(w.NearDist), Mathf.RoundToInt(w.FarDist), w.Run.Cells, w.Direction)
                : GameText.Format("defov.weak.ring", tag, w.Direction, w.Run.Cells, Mathf.RoundToInt(w.NearDist));
        }

        /// <summary>热力图下方的统计一行。</summary>
        public static string StatsText(DefenseCoverageMap map)
        {
            if (map == null || map.W <= 0)
            {
                return string.Empty;
            }
            if (map.Counted == 0)
            {
                return GameText.Get("defov.heat.none");
            }
            int area = Math.Max(1, map.W * map.H);
            return GameText.Format("defov.heat.stats", map.W, map.H, Mathf.RoundToInt(100f * map.Covered / area), Mathf.RoundToInt(100f * map.Covered2 / area), map.Counted, map.Skipped);
        }

        /// <summary>热力图某格的覆盖数（栅格外 = -1；自检读）。</summary>
        public static int CoverageAt(DefenseCoverageMap map, int x, int y) =>
            map == null ? -1 : CombatCoverage.At(map.Counts, map.Ox, map.Oy, map.W, map.H, x, y);

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
