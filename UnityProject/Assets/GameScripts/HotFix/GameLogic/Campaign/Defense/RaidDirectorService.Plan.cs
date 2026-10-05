using System;
using System.Collections.Generic;
using System.Diagnostics;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// 突袭计划的排定（FGR-DEF-021～024）：
    /// - 阵营：暴露贡献最多、本幕已出现、编成表里有单位的敌方阵营（<see cref="MostStimulatedFaction"/>）；没有任何贡献时取离家园最近的可用阵营。
    /// - 目标：家园（归还核心），或离阵营领地近、建筑多的前哨站（地图上离核心 300 格外的己方建筑群；正式前哨站实体在 FG8-OUT-01）。
    /// - 出发地：所选阵营离目标最近的活跃据点（家园区侦察巢归最近的本幕领地）；超过 raid.origin_max_distance 或一个都没有 → 迷雾外（领地朝目标一侧、未探索处）。
    /// - 预算 = 等级基础值 × 幕系数 × 难度规模 × 间隔系数（后日谈 = 基数 × 增长^n）；第一次突袭固定 1 级、× raid.first_raid_scale。
    /// - 编成：按点数从阵营编成表抽（带对策标签的单位权重 × boost）；单位数到上限后剩余预算把普通换成精英。
    /// - 反制：玩家防线（炮塔蓝图里的固件 + 陷阱固件）里用得最多的固件类别 → 阵营自己的对策（fg.TbRaidCounter；阵营没有对策 = 不反制）。
    /// - 预警：先预热途经区块，隔 raid.route_delay_seconds 再沿地形寻路（FGR-ARC-015）；路程 / 速度 = 行进时间。预警在筹备期满时发出，
    ///   行进时间不足最短预警时推迟出发（部队在出发地集结）；普通突袭的抵达不早于开局宽限终点、上一波普通突袭 + 最短间隔。
    /// </summary>
    public static partial class RaidDirectorService
    {
        private static readonly List<RaidUnitDef> UnitScratch = new List<RaidUnitDef>(8);
        private static readonly List<double> WeightScratch = new List<double>(8);

        // ─────────────────────────────── 新计划 ───────────────────────────────

        private static void CreatePlan(CampaignState s, RaidDirectorState d, RaidTriggerDef def, RaidTriggerRecord t, int level, string faction, long tick)
        {
            var p = new RaidPlanRecord
            {
                PlanId = "raid-" + d.NextPlanSerial,
                Serial = d.NextPlanSerial,
                Wave = d.NextWaveSerial,
                State = StatePlanning,
                Trigger = def.Id,
                Triggers = new[] { def.Id },
                Exempt = def.Exempt,
                StoryId = t.StoryId ?? string.Empty,
                Level = level,
                Faction = faction,
                CreatedTick = tick,
                RouteState = RouteNeed,
            };
            d.NextPlanSerial++;
            d.NextWaveSerial++;
            if (!def.Exempt)
            {
                if (d.RaidCount == 0)
                {
                    // FG06 第 4 节：第一次突袭固定 1 级、规模很小。
                    p.FirstRaid = true;
                    p.Level = 1;
                }
                d.RaidCount++;
            }
            if (s.Progress?.IsPostgame == true)
            {
                p.PostgameIndex = d.PostgameCount++;
            }
            SelectTarget(s, p);
            SelectOrigin(s, d, p);
            ChooseCounter(s, p);
            p.Budget = ComputeBudget(s, d, p);
            Compose(s, p);
            p.RouteRequestTick = tick;
            var list = new List<RaidPlanRecord>(d.Plans.Length + 1);
            list.AddRange(d.Plans);
            list.Add(p);
            d.Plans = list.ToArray();
            GuidanceHooks.Raise(GuidanceHooks.RaidFirstPlanned);
            Touch();
        }

        /// <summary>普通触发并进还没出发的普通计划：记下触发；还没预警时等级取高者并重新编成（第一次突袭保持 1 级）。</summary>
        private static void MergeTrigger(CampaignState s, RaidPlanRecord p, string kind, int level)
        {
            if (Array.IndexOf(p.Triggers, kind) < 0)
            {
                var list = new List<string>(p.Triggers) { kind };
                p.Triggers = list.ToArray();
            }
            if (p.State < StateWarned && !p.FirstRaid && level > p.Level)
            {
                p.Level = level;
                p.Budget = ComputeBudget(s, StateOf(s), p);
                Compose(s, p);
                PlanChanged(s, p); // 等级与编成变了：出发前的预报作废、重新破译（复审 P1）
            }
            Touch();
        }

        /// <summary>
        /// 计划的预报相关内容（等级、编成、出发地、路线与抵达时刻）变了：变更序号 +1，这条计划已有的突袭预报立刻标已过时（计划有变），
        /// 监听站重新破译（FGT-DEF-004“情报与实际突袭一致”；复审 P1）。
        /// </summary>
        private static void PlanChanged(CampaignState s, RaidPlanRecord p)
        {
            p.Revision++;
            Economy.IntelService.OnRaidPlanChanged(s, p);
        }

        // ─────────────────────────────── 阵营 / 目标 / 出发地 ───────────────────────────────

        /// <summary>本幕已出现、编成表里有单位的敌方阵营领地。</summary>
        private static bool Eligible(PlannedTerritory t, int act) => t != null && t.IsFaction && t.Act <= act && RaidCatalog.HasUnits(t.Id);

        /// <summary>
        /// “最受刺激”的阵营（FGR-DEF-021）：暴露贡献最多的、本幕已出现、有突袭编成的敌方阵营（<see cref="CampaignExposureLedger.FactionContributions"/> 从多到少）；
        /// 没有任何敌方贡献时取离家园最近的可用阵营。都没有（旧原型地形没有规划层）时取编成表里第一个阵营。
        /// </summary>
        public static string MostStimulatedFaction(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            int act = Math.Max(1, s.Progress?.Act ?? 1);
            WorldPlan plan = WorldGenService.PlanFor(s);
            foreach ((string faction, float _) in CampaignExposureLedger.FactionContributions(s))
            {
                if (plan == null ? RaidCatalog.HasUnits(faction) : Eligible(plan.Find(faction), act))
                {
                    return faction;
                }
            }
            if (plan != null)
            {
                PlannedTerritory best = null;
                foreach (PlannedTerritory t in plan.Territories)
                {
                    if (Eligible(t, act) && (best == null || t.Distance < best.Distance || (t.Distance == best.Distance && string.CompareOrdinal(t.Id, best.Id) < 0)))
                    {
                        best = t;
                    }
                }
                if (best != null)
                {
                    return best.Id;
                }
            }
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                return u.Faction;
            }
            return null;
        }

        /// <summary>据点属于哪个阵营：领地里的据点 = 那个阵营；家园区侦察巢与零散据点 = 离它最近的本幕阵营领地（到领地边缘的距离）。</summary>
        public static string FactionOfOutpost(CampaignState s, OutpostRecord o)
        {
            if (o == null)
            {
                return null;
            }
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return RaidCatalog.HasUnits(o.TerritoryId) ? o.TerritoryId : null;
            }
            PlannedTerritory own = plan.Find(o.TerritoryId);
            if (own != null && own.IsFaction)
            {
                return own.Id;
            }
            int act = Math.Max(1, s.Progress?.Act ?? 1);
            PlannedTerritory best = null;
            double bestEdge = double.MaxValue;
            foreach (PlannedTerritory t in plan.Territories)
            {
                if (!Eligible(t, act))
                {
                    continue;
                }
                double dx = o.CellX - t.CenterX;
                double dy = o.CellY - t.CenterY;
                double edge = Math.Sqrt(dx * dx + dy * dy) - t.Radius;
                if (edge < bestEdge - 1e-9 || (Math.Abs(edge - bestEdge) <= 1e-9 && best != null && string.CompareOrdinal(t.Id, best.Id) < 0))
                {
                    best = t;
                    bestEdge = edge;
                }
            }
            return best?.Id;
        }

        /// <summary>
        /// 目标（FGR-DEF-023）：家园（权重 raid.target_home_weight）或前哨站（权重 = 座数 / 10 × (1 + P / (P + 到阵营领地边缘的距离))，
        /// 至少 raid.target_outpost_min_buildings 座）——按（世界种子, 计划序号）抽一次。前哨站 = 地图上离核心超过 map.own_outpost_distance 的己方建筑群。
        /// </summary>
        private static void SelectTarget(CampaignState s, RaidPlanRecord p)
        {
            GridCell core = HomeGridService.CorePivot(s);
            p.TargetKind = TargetHome;
            p.TargetId = TargetHome;
            p.TargetX = core.X;
            p.TargetY = core.Y;
            double homeW = RaidCatalog.TargetHomeWeight;
            PlannedTerritory terr = WorldGenService.PlanFor(s)?.Find(p.Faction);
            double prox = RaidCatalog.TargetProximityCells;
            int minB = RaidCatalog.TargetOutpostMinBuildings;
            var picks = new List<(string Id, double X, double Y, double W, int Count)>(4);
            foreach (WorldMapOwnClusters.Cluster c in WorldMapOwnClusters.For(s))
            {
                if (!c.IsOutpost || c.Count < minB)
                {
                    continue;
                }
                double edge = 1e9;
                if (terr != null)
                {
                    double dx = c.X - terr.CenterX;
                    double dy = c.Y - terr.CenterY;
                    edge = Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - terr.OuterRadius);
                }
                double w = c.Count / 10.0 * (1.0 + prox / (prox + edge));
                picks.Add((c.Id, c.X, c.Y, w, c.Count));
            }
            if (picks.Count == 0)
            {
                return;
            }
            double sum = homeW;
            foreach (var x in picks)
            {
                sum += x.W;
            }
            double r = Hash01(s, p.Serial, 0, SaltTarget) * sum;
            if (r < homeW)
            {
                return;
            }
            r -= homeW;
            for (int i = 0; i < picks.Count; i++)
            {
                var x = picks[i];
                if (r < x.W || i == picks.Count - 1)
                {
                    p.TargetKind = TargetOutpost;
                    p.TargetId = x.Id;
                    p.TargetX = (int)Math.Round(x.X);
                    p.TargetY = (int)Math.Round(x.Y);
                    return;
                }
                r -= x.W;
            }
        }

        /// <summary>出发地（FGR-DEF-023 / FGR-GEN-034）：所选阵营离目标最近的活跃（没被摧毁的）据点；没有合适的 → 迷雾外。</summary>
        private static void SelectOrigin(CampaignState s, RaidDirectorState d, RaidPlanRecord p)
        {
            OutpostRecord best = null;
            double bestD = double.MaxValue;
            double max = RaidCatalog.OriginMaxDistance;
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o == null || o.Destroyed || FactionOfOutpost(s, o) != p.Faction)
                {
                    continue;
                }
                double dx = o.CellX - p.TargetX;
                double dy = o.CellY - p.TargetY;
                double dd = Math.Sqrt(dx * dx + dy * dy);
                if (dd > max)
                {
                    continue;
                }
                if (dd < bestD - 1e-9 || (Math.Abs(dd - bestD) <= 1e-9 && best != null && string.CompareOrdinal(o.OutpostId, best.OutpostId) < 0))
                {
                    best = o;
                    bestD = dd;
                }
            }
            if (best != null)
            {
                p.OriginKind = OriginOutpost;
                p.OriginId = best.OutpostId;
                p.OriginX = best.CellX;
                p.OriginY = best.CellY;
            }
            else
            {
                FogOrigin(s, p);
            }
            // 方位一律从家园核心看（与 OnOutpostDestroyed 记被毁据点同一参照）：目标是前哨站时降频也落在正确的方向上（复审 P2）。
            GridCell core = HomeGridService.CorePivot(s);
            p.Octant = Economy.IntelService.Octant(p.OriginX - core.X, p.OriginY - core.Y);
            int destroyed = d.DestroyedByOctant != null && p.Octant >= 0 && p.Octant < d.DestroyedByOctant.Length ? d.DestroyedByOctant[p.Octant] : 0;
            p.IntervalMul = 1f + RaidCatalog.DestroyedOutpostIntervalBonus * destroyed;
        }

        /// <summary>
        /// 迷雾外来袭：阵营领地朝目标一侧的边缘（与测试捷径 DispatchRaidFromTerritory 同一取点），那一点已经探索过就沿连线往领地中心挪，直到未探索；
        /// 没有规划层（旧原型地形）时按（世界种子, 计划序号）取 600 格外的一个方向。
        /// </summary>
        private static void FogOrigin(CampaignState s, RaidPlanRecord p)
        {
            p.OriginKind = OriginFog;
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(p.Faction);
            if (t == null)
            {
                double ang = Hash01(s, p.Serial, 1, SaltTarget) * Math.PI * 2.0;
                p.OriginId = p.Faction;
                p.OriginX = p.TargetX + (int)Math.Round(Math.Cos(ang) * 600.0);
                p.OriginY = p.TargetY + (int)Math.Round(Math.Sin(ang) * 600.0);
                return;
            }
            p.OriginId = t.Id;
            double dx = p.TargetX - t.CenterX;
            double dy = p.TargetY - t.CenterY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ux = len > 1e-6 ? dx / len : 0;
            double uy = len > 1e-6 ? dy / len : 0;
            double edge = Math.Min(t.OuterRadius, len * 0.5);
            HomeGridMap map = HomeGridService.MapFor(s);
            double e = edge;
            for (int guard = 0; guard < 64 && e > 0; guard++)
            {
                var c = new GridCell((int)Math.Round(t.CenterX + ux * e), (int)Math.Round(t.CenterY + uy * e));
                if (map == null || !map.IsExploredNoLoad(c))
                {
                    break;
                }
                e = Math.Max(0, e - 16);
            }
            p.OriginX = (int)Math.Round(t.CenterX + ux * e);
            p.OriginY = (int)Math.Round(t.CenterY + uy * e);
        }

        // ─────────────────────────────── 预算 / 编成 / 反制 ───────────────────────────────

        /// <summary>间隔系数：距上一波任何突袭（没有 = 开局）超过最短间隔后，每多 1 天 + raid.interval_bonus_per_day，至多 + raid.interval_bonus_max。</summary>
        public static double IntervalCoef(RaidDirectorState d, long tick)
        {
            long since = d.LastAnyArrivalTick >= 0 ? Math.Max(0, tick - d.LastAnyArrivalTick) : Math.Max(0, tick);
            double days = since / (double)Math.Max(1, DayTicks(1));
            double bonus = Math.Max(0, days - RaidCatalog.MinIntervalDays) * RaidCatalog.IntervalBonusPerDay;
            return 1.0 + Math.Min(RaidCatalog.IntervalBonusMax, bonus);
        }

        /// <summary>预算（FG16 第 5 节）：等级基础值 × 幕系数 × 难度规模 × 间隔系数（第一次突袭再 × raid.first_raid_scale）；后日谈 = 基数 × 增长^n × 难度规模。</summary>
        public static int ComputeBudget(CampaignState s, RaidDirectorState d, RaidPlanRecord p)
        {
            double scale = RaidCatalog.DifficultyScale(s.DifficultyId);
            double b;
            if (p.PostgameIndex >= 0)
            {
                b = RaidCatalog.PostgameBase * Math.Pow(RaidCatalog.PostgameGrowth, p.PostgameIndex) * scale;
            }
            else
            {
                int act = Math.Max(1, s.Progress?.Act ?? 1);
                b = RaidCatalog.LevelBudget(p.Level) * RaidCatalog.ActCoef(act) * scale * IntervalCoef(d, p.CreatedTick);
                if (p.FirstRaid)
                {
                    b *= RaidCatalog.FirstRaidScale;
                }
            }
            return Math.Max(1, (int)Math.Round(b));
        }

        private static double Hash01(CampaignState s, int serial, int k, uint salt) =>
            WorldGenMath.Hash(SeedOf(s), serial, k, salt) / 4294967296.0;

        /// <summary>
        /// 编成（FGR-DEF-021 / 022，FG16 第 5 节）：从阵营编成表（等级够的单位）按点数抽，权重 × 对策倍率；剩余预算不够最便宜的单位就停；
        /// 单位数到 raid.max_units 后剩余预算把普通单位换成精英（按表顺序，每次花 精英点数 − 普通点数）。至少 1 台。只在排定 / 升级 / 合并时算。
        /// </summary>
        public static void Compose(CampaignState s, RaidPlanRecord p)
        {
            long t0 = Stopwatch.GetTimestamp();
            RaidCatalog.UnitsFor(p.Faction, Math.Max(0, p.Level), UnitScratch);
            if (UnitScratch.Count == 0)
            {
                RaidCatalog.UnitsFor(p.Faction, 4, UnitScratch);
            }
            int n = UnitScratch.Count;
            if (n == 0)
            {
                p.UnitIds = Array.Empty<string>();
                p.UnitCounts = Array.Empty<int>();
                p.EliteCounts = Array.Empty<int>();
                p.UnitTotal = 0;
                return;
            }
            RaidCounter counter = RaidCatalog.CounterById(p.Counter);
            WeightScratch.Clear();
            int cheapest = 0;
            for (int i = 0; i < n; i++)
            {
                RaidUnitDef u = UnitScratch[i];
                WeightScratch.Add(u.Weight * (counter != null && u.HasTag(counter.Tag) ? counter.Boost : 1.0));
                if (u.Points < UnitScratch[cheapest].Points)
                {
                    cheapest = i;
                }
            }
            var counts = new int[n];
            var elites = new int[n];
            int remaining = Math.Max(0, p.Budget);
            int total = 0;
            int cap = RaidCatalog.MaxUnits;
            int draw = 0;
            while (total < cap && remaining >= UnitScratch[cheapest].Points)
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (UnitScratch[i].Points <= remaining)
                    {
                        sum += WeightScratch[i];
                    }
                }
                double r = Hash01(s, p.Serial, draw++, SaltCompose) * sum;
                int pick = -1;
                for (int i = 0; i < n; i++)
                {
                    if (UnitScratch[i].Points > remaining)
                    {
                        continue;
                    }
                    pick = i;
                    if (r < WeightScratch[i])
                    {
                        break;
                    }
                    r -= WeightScratch[i];
                }
                counts[pick]++;
                remaining -= UnitScratch[pick].Points;
                total++;
            }
            if (total == 0)
            {
                counts[cheapest] = 1;
                total = 1;
                remaining = 0;
            }
            if (total >= cap)
            {
                bool changed = true;
                while (changed && remaining > 0)
                {
                    changed = false;
                    for (int i = 0; i < n; i++)
                    {
                        int diff = UnitScratch[i].ElitePoints - UnitScratch[i].Points;
                        if (counts[i] - elites[i] > 0 && remaining >= diff)
                        {
                            elites[i]++;
                            remaining -= diff;
                            changed = true;
                        }
                    }
                }
            }
            var ids = new List<string>(n);
            var cs = new List<int>(n);
            var es = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (counts[i] > 0)
                {
                    ids.Add(UnitScratch[i].Id);
                    cs.Add(counts[i]);
                    es.Add(elites[i]);
                }
            }
            p.UnitIds = ids.ToArray();
            p.UnitCounts = cs.ToArray();
            p.EliteCounts = es.ToArray();
            p.UnitTotal = total;
            LastComposeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (LastComposeMs > MaxComposeMs)
            {
                MaxComposeMs = LastComposeMs;
            }
        }

        private static readonly string[] CategoryKeys = { "fuse", "limiter", "fluid", "em" };

        /// <summary>
        /// 玩家防线里用得最多的固件类别（FGR-DEF-022）：数全部炮塔（装的蓝图版本里的固件）与陷阱发射器（装的固件）各自的固件类别，取最多的；
        /// 并列按 引信 → 限制器 → 流体 → 电磁 的固定顺序。一枚都没有 = null（不反制）。O(炮塔数 × 9 + 防御建筑数)，只在排定时算。
        /// </summary>
        public static string TopDefenseCategory(CampaignState s, int[] countsOut = null)
        {
            var counts = countsOut ?? new int[4];
            Array.Clear(counts, 0, counts.Length);
            foreach (TurretRecord r in TurretService.All(s))
            {
                if (r == null || string.IsNullOrEmpty(r.BlueprintId))
                {
                    continue;
                }
                BlueprintRecord rec = BlueprintEditorService.Find(s, r.BlueprintId);
                BlueprintVersionRecord ver = null;
                foreach (BlueprintVersionRecord v in rec?.Versions ?? Array.Empty<BlueprintVersionRecord>())
                {
                    if (v != null && v.Version == r.BlueprintVersion)
                    {
                        ver = v;
                    }
                }
                foreach (string id in ver?.CircuitSlotContentIds ?? Array.Empty<string>())
                {
                    Count(counts, id);
                }
            }
            foreach (DefenseRecord dr in DefenseService.All(s))
            {
                if (dr != null && !string.IsNullOrEmpty(dr.TrapFirmware))
                {
                    Count(counts, dr.TrapFirmware);
                }
            }
            int best = -1;
            for (int i = 0; i < 4; i++)
            {
                if (counts[i] > 0 && (best < 0 || counts[i] > counts[best]))
                {
                    best = i;
                }
            }
            return best < 0 ? null : CategoryKeys[best];
        }

        private static void Count(int[] counts, string contentId)
        {
            if (string.IsNullOrEmpty(contentId) || !FirmwareKinds.IsFirmware(contentId))
            {
                return;
            }
            switch (FirmwareKinds.CategoryOf(contentId))
            {
                case FirmwareCategory.Fuse:
                    counts[0]++;
                    break;
                case FirmwareCategory.Limiter:
                    counts[1]++;
                    break;
                case FirmwareCategory.Fluid:
                    counts[2]++;
                    break;
                case FirmwareCategory.Electromagnetic:
                    counts[3]++;
                    break;
            }
        }

        private static void ChooseCounter(CampaignState s, RaidPlanRecord p)
        {
            string cat = TopDefenseCategory(s);
            RaidCounter c = cat != null ? RaidCatalog.CounterFor(p.Faction, cat) : null;
            p.CounterCategory = cat ?? string.Empty;
            p.Counter = c?.Id ?? string.Empty;
        }

        // ─────────────────────────────── 寻路与排定 ───────────────────────────────

        private static bool NavReady(CampaignState s) => NavService.IsBound && ReferenceEquals(NavService.BoundState, s);

        private static readonly List<Unity.Mathematics.int2> RoutePoints = new List<Unity.Mathematics.int2>(64);

        /// <summary>后台长路线通道是不是正替这个计划算着。</summary>
        private static bool LaneOwns(RaidPlanRecord p) =>
            NavService.BackgroundActive && NavService.BackgroundOwnerTag == NavService.OwnerRaidPlan && NavService.BackgroundOwnerKey == p.Serial
            && NavService.BackgroundSerial == p.NavSerial;

        /// <summary>
        /// 沿地形要路线（FGR-ARC-015；DEBT-FG0ARCH06-09）：走后台长路线通道，在 raid.route_latency_seconds 之后的固定一步采纳——冷的长路线（途经区块当场按种子生成 + 建抽象图）
        /// 几百毫秒早已算完，采纳不硬等；常规批次（单位的路线）不受影响。通道被别的计划占着（到它的采纳步为止，<see cref="RaidDirectorState.LaneBusyUntilTick"/>，
        /// 随存档保存）就等：占用只取决于采纳步——存档时提前取出结果、通道空出来，也不让后面的计划提早开始，存档与否逐步一致（复审 P2）。
        /// 没有寻路服务（没有家园的场景）：按直线估算（队伍出发后同样走直线）。
        /// </summary>
        private static void TryStartRoute(CampaignState s, RaidPlanRecord p, long tick)
        {
            if (!NavReady(s))
            {
                p.RouteX = new[] { p.TargetX };
                p.RouteY = new[] { p.TargetY };
                p.RouteState = RouteReady;
                p.RouteReadyTick = tick;
                return;
            }
            RaidDirectorState d = StateOf(s);
            if (tick <= d.LaneBusyUntilTick || NavService.BackgroundActive)
            {
                return;
            }
            int serial = s.Raids.NextNavSerial;
            if (NavService.StartBackground(NavService.OwnerRaidPlan, p.Serial, serial, NavConst.ClassHostile,
                    new GridCell(p.OriginX, p.OriginY), new GridCell(p.TargetX, p.TargetY), allowPartial: true))
            {
                s.Raids.NextNavSerial++;
                p.NavSerial = serial;
                p.RouteState = RouteAwaiting;
                p.RouteAdoptTick = tick + Math.Max(1, GameClock.TicksFor(RaidCatalog.RouteLatencySeconds));
                p.PendingRoute = false;
                d.LaneBusyUntilTick = p.RouteAdoptTick;
            }
        }

        /// <summary>
        /// 到了采纳步：取后台通道的结果（存档时已经取出来存在计划里的，用存的那份）；路线在这期间被新建筑挡住了就重新要（按镜像检查，确定）。
        /// 失败 = 按直线估算（出发时队伍再自己要路线，到不了按“到达（受阻）”结算）。通道没替它算（读档时没有存下结果）也重新要。
        /// </summary>
        private static void AdoptRoute(CampaignState s, RaidPlanRecord p, long tick)
        {
            int status;
            int reason;
            if (p.PendingRoute)
            {
                status = p.PendingStatus;
                reason = p.PendingReason;
                RoutePoints.Clear();
                for (int k = 0; k < p.PendingX.Length; k++)
                {
                    RoutePoints.Add(new Unity.Mathematics.int2(p.PendingX[k], p.PendingY[k]));
                }
                p.PendingRoute = false;
                p.PendingX = Array.Empty<int>();
                p.PendingY = Array.Empty<int>();
            }
            else if (LaneOwns(p) && NavService.CollectBackground(RoutePoints, out NavResult r))
            {
                status = (int)r.Status;
                reason = (int)r.Reason;
            }
            else
            {
                p.RouteState = RouteNeed;
                return;
            }
            bool ok = status == (int)NavStatus.Ok || status == (int)NavStatus.Partial;
            if (ok && NavReady(s) && !NavService.RouteClear(new Unity.Mathematics.int2(p.OriginX, p.OriginY), RoutePoints, 0, NavConst.ClassHostile))
            {
                p.RouteState = RouteNeed; // 在路上的这段时间里被新建筑 / 屏障挡住了：从出发地重新要
                return;
            }
            if (ok)
            {
                var xs = new int[RoutePoints.Count];
                var ys = new int[RoutePoints.Count];
                for (int k = 0; k < RoutePoints.Count; k++)
                {
                    xs[k] = RoutePoints[k].x;
                    ys[k] = RoutePoints[k].y;
                }
                p.RouteX = xs;
                p.RouteY = ys;
                p.RouteState = RouteReady;
                p.NavReason = status == (int)NavStatus.Partial ? reason : 0;
            }
            else
            {
                p.RouteX = new[] { p.TargetX };
                p.RouteY = new[] { p.TargetY };
                p.RouteState = RouteFailed;
                p.NavReason = reason;
            }
            p.RouteReadyTick = tick;
        }

        /// <summary>
        /// 存档前（WorldSimulation.SyncAllForSave）：后台通道正替某个计划算的那一条，现在取出来存进计划（还没到采纳步就先不用）——读档后在同一采纳步交到，与不存档逐步一致。
        /// </summary>
        public static void WriteTo(CampaignState s)
        {
            foreach (RaidPlanRecord p in StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                if (p == null || p.RouteState != RouteAwaiting || p.PendingRoute || !LaneOwns(p))
                {
                    continue;
                }
                if (NavService.CollectBackground(RoutePoints, out NavResult r))
                {
                    p.PendingRoute = true;
                    p.PendingStatus = (int)r.Status;
                    p.PendingReason = (int)r.Reason;
                    p.PendingX = new int[RoutePoints.Count];
                    p.PendingY = new int[RoutePoints.Count];
                    for (int k = 0; k < RoutePoints.Count; k++)
                    {
                        p.PendingX[k] = RoutePoints[k].x;
                        p.PendingY[k] = RoutePoints[k].y;
                    }
                }
            }
        }

        /// <summary>寻路快照读不了时：在等路线、又没有存下结果、通道也没替它算的计划重新要。</summary>
        public static void ReissueAwaiting(CampaignState s)
        {
            RaidDirectorState d = StateOf(s);
            foreach (RaidPlanRecord p in d?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                if (p != null && p.RouteState == RouteAwaiting && !p.PendingRoute && !LaneOwns(p))
                {
                    p.RouteState = RouteNeed;
                    d.LaneBusyUntilTick = -1; // 那一条的结果已经拿不到了：通道视为空闲，重新要
                }
            }
        }

        /// <summary>
        /// 路线就绪：行进时间 = 沿路线走到第一次进入到达半径的长度 / 速度（与队伍逐步推进同一套几何）。预警在筹备期满时发出，
        /// 行进时间不足最短预警时推迟出发（集结）；普通突袭的抵达不早于开局宽限终点、上一波普通突袭 + 最短间隔 × 方向倍率 ÷ 难度频率——不够就整体往后推。
        /// 排定后处理与剧情 / 静默夜 / 巨构突袭的重叠（合并为一波）。
        /// </summary>
        private static void Schedule(CampaignState s, RaidDirectorState d, RaidPlanRecord p, long tick)
        {
            double arrival = WorldTransitSystem.ArrivalRadius;
            double along = WorldTransitSystem.PathUntilArrival(p.OriginX, p.OriginY, p.RouteX, p.RouteY, 0, p.TargetX, p.TargetY, arrival,
                out double ax, out double ay, out bool entered);
            if (!entered)
            {
                // 部分路线 / 直线：从路线末端按直线补到到达半径上（与 PredictArrival 同一取点）。
                double ex = ax - p.TargetX;
                double ey = ay - p.TargetY;
                double len = Math.Sqrt(ex * ex + ey * ey);
                if (len > arrival)
                {
                    along += len - arrival;
                    ax = p.TargetX + ex / len * arrival;
                    ay = p.TargetY + ey / len * arrival;
                }
            }
            double perTick = WorldTransitSystem.RaidSpeed / (double)Math.Max(1, GameClock.StepHz);
            p.TravelTicks = (long)Math.Ceiling(along / perTick) + 1;
            p.ArriveX = (float)ax;
            p.ArriveY = (float)ay;
            long lead = DayTicks(p.Exempt ? RaidCatalog.StoryPlanLeadDays : RaidCatalog.PlanLeadDays);
            long warn = Math.Max(tick, p.CreatedTick + lead);
            long depart = warn + Math.Max(0, MinWarningTicks(s) - p.TravelTicks);
            long arrive = depart + p.TravelTicks;
            if (!p.Exempt)
            {
                if (p.Reroutes == 0)
                {
                    p.IntervalBaseTick = d.LastArrivalTick;
                }
                long earliest = GraceEndTick;
                if (p.IntervalBaseTick >= 0)
                {
                    double gap = DayTicks(RaidCatalog.MinIntervalDays) * p.IntervalMul / RaidCatalog.DifficultyFrequency(s.DifficultyId);
                    earliest = Math.Max(earliest, p.IntervalBaseTick + (long)Math.Round(gap));
                }
                if (arrive < earliest)
                {
                    long shift = earliest - arrive;
                    warn += shift;
                    depart += shift;
                    arrive += shift;
                }
            }
            p.WarnTick = warn;
            p.DepartTick = depart;
            p.ArrivalTick = arrive;
            p.State = StateScheduled;
            if (!p.Exempt)
            {
                d.LastArrivalTick = Math.Max(d.LastArrivalTick, arrive);
            }
            d.LastAnyArrivalTick = Math.Max(d.LastAnyArrivalTick, arrive);
            PlanChanged(s, p); // （重新）排定：抵达时刻与抵达点定下来；改道后重新排定时旧预报作废
            ResolveOverlaps(s, d, p);
            Touch();
        }

        private static bool Overlaps(RaidPlanRecord a, RaidPlanRecord b) =>
            a.ArrivalTick >= 0 && b.ArrivalTick >= 0 && Math.Abs(a.ArrivalTick - b.ArrivalTick) <= GameClock.TicksFor(RaidCatalog.MergeWindowSeconds);

        /// <summary>
        /// FG06 第 5 章负向“两次突袭时间重叠：剧情突袭与普通突袭合并为一波”：刚排定的计划与时间重叠（抵达相差不超过 raid.merge_window_seconds）的另一类计划——
        /// 都还没出发 → 剧情（不受限）的一方吸收普通一方的预算、重新编成，普通一方标“已合并”；普通一方已经出发 → 剧情一方并进同一波、抵达对齐到它（来不及就尽快）；
        /// 剧情一方已经出发 → 普通一方比它早到时不并波（普通突袭不能提前，各自一行），否则推迟到与它同时抵达、并进同一波（复审 P2：先到的一支不再把整行写成“已到达”）。
        /// 抵达改期的一方变更序号 +1（预报随之作废）。
        /// </summary>
        private static void ResolveOverlaps(CampaignState s, RaidDirectorState d, RaidPlanRecord p)
        {
            foreach (RaidPlanRecord q in d.Plans)
            {
                if (q == null || ReferenceEquals(q, p) || q.Exempt == p.Exempt || q.State >= StateEnded || !Overlaps(p, q))
                {
                    continue;
                }
                RaidPlanRecord exempt = p.Exempt ? p : q;
                RaidPlanRecord normal = p.Exempt ? q : p;
                bool exemptPending = exempt.State >= StateScheduled && exempt.State <= StateWarned;
                bool normalPending = normal.State >= StateScheduled && normal.State <= StateWarned;
                if (exemptPending && normalPending)
                {
                    Fold(s, d, exempt, normal);
                }
                else if (exemptPending && normal.State == StateDeparted)
                {
                    // 普通突袭已经在路上：剧情一方并进同一波，抵达对齐到它（剧情一方晚到时不再提前）。
                    exempt.Wave = normal.Wave;
                    if (normal.ArrivalTick > exempt.ArrivalTick)
                    {
                        long shift = normal.ArrivalTick - exempt.ArrivalTick;
                        exempt.WarnTick += shift;
                        exempt.DepartTick += shift;
                        exempt.ArrivalTick += shift;
                        d.LastAnyArrivalTick = Math.Max(d.LastAnyArrivalTick, exempt.ArrivalTick);
                        PlanChanged(s, exempt);
                    }
                }
                else if (normalPending && exempt.State == StateDeparted && exempt.ArrivalTick >= normal.ArrivalTick)
                {
                    // 剧情一方已经在路上、比普通一方晚到：普通一方推迟到同时抵达（只往后推，最短间隔照样满足），并进同一波（预警条显示成一波）。
                    // 普通一方会先到时不并波：它不能提前，两支分开显示各自的倒计时。
                    long shift = exempt.ArrivalTick - normal.ArrivalTick;
                    if (shift > 0)
                    {
                        normal.WarnTick += shift;
                        normal.DepartTick += shift;
                        normal.ArrivalTick += shift;
                        d.LastArrivalTick = Math.Max(d.LastArrivalTick, normal.ArrivalTick);
                        d.LastAnyArrivalTick = Math.Max(d.LastAnyArrivalTick, normal.ArrivalTick);
                        PlanChanged(s, normal);
                    }
                    normal.Wave = exempt.Wave;
                }
                if (p.State == StateMerged)
                {
                    return;
                }
            }
        }

        private static void Fold(CampaignState s, RaidDirectorState d, RaidPlanRecord keeper, RaidPlanRecord absorbed)
        {
            bool announced = absorbed.State == StateWarned;
            keeper.Budget += absorbed.Budget;
            var trig = new List<string>(keeper.Triggers);
            foreach (string t in absorbed.Triggers)
            {
                if (!trig.Contains(t))
                {
                    trig.Add(t);
                }
            }
            keeper.Triggers = trig.ToArray();
            Compose(s, keeper);
            PlanChanged(s, keeper); // 保留方的规模变了：它已有的预报作废、重新破译（复审 P1）
            absorbed.State = StateMerged;
            absorbed.MergedInto = keeper.PlanId;
            absorbed.EndReason = EndMerged;
            d.TriggersMerged++;
            if (announced)
            {
                NotificationCenter.Post("raid_warning", GameText.Format("raid.warning.notify_merged", RaidCatalog.TriggerName(absorbed.Trigger)),
                    new Vector3(keeper.ArriveX, 0f, keeper.ArriveY));
            }
            Touch();
        }

        // ─────────────────────────────── 推进 ───────────────────────────────

        private static void AdvancePlans(CampaignState s, RaidDirectorState d, long tick)
        {
            bool cleanup = false;
            RaidPlanRecord[] plans = d.Plans;
            for (int i = 0; i < plans.Length; i++)
            {
                RaidPlanRecord p = plans[i];
                if (p == null)
                {
                    cleanup = true;
                    continue;
                }
                if (p.State == StatePlanning)
                {
                    if (p.RouteState == RouteNeed && tick >= p.RouteRequestTick)
                    {
                        TryStartRoute(s, p, tick);
                    }
                    if (p.RouteState == RouteAwaiting && tick >= p.RouteAdoptTick)
                    {
                        AdoptRoute(s, p, tick);
                    }
                    if (p.RouteState == RouteReady || p.RouteState == RouteFailed)
                    {
                        Schedule(s, d, p, tick);
                    }
                }
                if (p.State == StateScheduled && tick >= p.WarnTick)
                {
                    Warn(s, p, tick);
                }
                if (p.State == StateWarned && tick >= p.DepartTick)
                {
                    Depart(s, d, p, tick);
                }
                if (p.State == StateDeparted)
                {
                    Track(s, p, tick);
                }
                if (p.State >= StateEnded)
                {
                    cleanup = true;
                }
            }
            if (cleanup)
            {
                Cleanup(d, tick);
            }
        }

        /// <summary>发预警（FGR-DEF-024）：倒计时、方向、目标；有集结时写明多久后出发。定位到预计抵达点（点通知镜头飞过去）。</summary>
        private static void Warn(CampaignState s, RaidPlanRecord p, long tick)
        {
            p.State = StateWarned;
            string dir = DirectionText(p);
            string target = TargetText(p);
            string eta = Duration(p.ArrivalTick - tick);
            string text = p.DepartTick > tick
                ? GameText.Format("raid.warning.notify_assembling", dir, Duration(p.DepartTick - tick), eta, target)
                : p.Reroutes > 0
                    ? GameText.Format("raid.warning.notify_rerouted", dir, eta)
                    : GameText.Format("raid.warning.notify", dir, target, eta);
            NotificationCenter.Post("raid_warning", text, new Vector3(p.ArriveX, 0f, p.ArriveY));
            GuidanceHooks.Raise(GuidanceHooks.RaidFirstWarning);
            if (p.DepartTick > p.WarnTick)
            {
                GuidanceHooks.Raise(GuidanceHooks.RaidFirstAssembling);
            }
            Touch();
        }

        /// <summary>出发：出发据点在这期间被摧毁 → 改道（重新选出发地、重新寻路与排定、再发一次预警，最短预警照样保证）；否则派出行进队伍。</summary>
        private static void Depart(CampaignState s, RaidDirectorState d, RaidPlanRecord p, long tick)
        {
            if (p.OriginKind == OriginOutpost)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, p.OriginId);
                if (o == null || o.Destroyed)
                {
                    p.Reroutes++;
                    SelectOrigin(s, d, p);
                    p.State = StatePlanning;
                    p.RouteState = RouteNeed;
                    p.RouteRequestTick = tick;
                    p.WarnTick = -1;
                    p.DepartTick = -1;
                    p.ArrivalTick = -1;
                    PlanChanged(s, p); // 出发地、方向与抵达都要重算：旧预报作废，重新排定后再破译（复审 P1）
                    Touch();
                    return;
                }
            }
            TransitGroupRecord g = WorldTransitSystem.DispatchPlanned(s, p);
            p.GroupId = g?.GroupId ?? string.Empty;
            p.State = g != null ? StateDeparted : StateCancelled;
            if (g == null)
            {
                p.EndReason = EndCancelled;
            }
            Touch();
        }

        private static void Track(CampaignState s, RaidPlanRecord p, long tick)
        {
            TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
            if (g == null)
            {
                // 队伍离场：撤回出发地；FG6-DEF-05 起还有“被全歼”（攻城服务移除队伍前已写进 EndReason）。
                p.State = StateEnded;
                p.EndReason = p.EndReason == EndDestroyed ? EndDestroyed : p.ArrivedTick >= 0 ? EndWithdrawn : EndGone;
                Touch();
                return;
            }
            if (p.ArrivedTick < 0 && g.State != TransitGroupState.Marching && g.ArrivedAtTick >= 0)
            {
                p.ArrivedTick = g.ArrivedAtTick;
                Touch();
            }
        }

        private static void Cleanup(RaidDirectorState d, long tick)
        {
            var keep = new List<RaidPlanRecord>(d.Plans.Length);
            var hist = new List<RaidHistoryRecord>(d.History);
            foreach (RaidPlanRecord p in d.Plans)
            {
                if (p == null)
                {
                    continue;
                }
                if (p.State < StateEnded)
                {
                    keep.Add(p);
                    continue;
                }
                if (LaneOwns(p))
                {
                    NavService.CancelBackground(); // 不再要的路线：释放后台通道
                }
                if (p.RouteState == RouteAwaiting && p.RouteAdoptTick == d.LaneBusyUntilTick)
                {
                    d.LaneBusyUntilTick = -1; // 占着通道的计划结束了（不管结果是否已在存档时取出）：通道空闲
                }
                hist.Add(new RaidHistoryRecord
                {
                    PlanId = p.PlanId, Wave = p.Wave, Trigger = p.Trigger, Faction = p.Faction, Level = p.Level, Units = p.UnitTotal,
                    TargetKind = p.TargetKind, OriginKind = p.OriginKind, WarnTick = p.WarnTick, ArrivedTick = p.ArrivedTick, EndTick = tick,
                    EndReason = string.IsNullOrEmpty(p.EndReason) ? EndGone : p.EndReason,
                });
            }
            int max = RaidCatalog.HistoryMax;
            if (hist.Count > max)
            {
                hist.RemoveRange(0, hist.Count - max);
            }
            d.Plans = keep.ToArray();
            d.History = hist.ToArray();
            Touch();
        }
    }
}
