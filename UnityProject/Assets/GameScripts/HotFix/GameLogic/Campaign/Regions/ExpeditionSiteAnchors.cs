using System;
using System.Collections.Generic;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.WorldGen;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG8-EXP-02 第 0 批（方案 A：FG8-GEN-02“远征地点迁入星球格网”的最小部分前移——锚点位置与可达性；覆盖根见下）：
    /// 两个远征地点（破碎都市、铸造前哨外围）在星球表面上的锚点。远征队从家园沿地形走到锚点，到达后才进入地点（<see cref="ExpeditionMarchService"/>）。
    ///
    /// 放置规则（FG17 FGR-GEN-024 的“在领地内的距离范围 + 保证从家园出发可达（层级寻路校验）”）：
    /// - 候选点只由（世界种子, 地点, 第几次试探）和规划层的领地决定（B25：内容不依赖固定坐标）：在地点所属领地（破碎都市 = silent、铸造前哨 = foundry）里，
    ///   从领地中心朝归还核心的方向（± expedition.anchor.jitter_deg）、离中心 expedition.anchor.radial_min～max 倍半径处。
    /// - 可达性：从归还核心枢轴格向候选点发一条层级寻路请求（允许部分路线，<see cref="NavService.OwnerExpedition"/>，后台通道当场取回，<see cref="EnsureResolved"/>）。走得到 → 锚点 = 寻路给出的实际终点；
    ///   走不到 → 最近可达点仍在领地内就挪到那里（<see cref="ExpeditionSiteAnchorRecord.Moved"/>），否则记下离领地最近的可达点、换下一个候选（确定性的局部重试，FGR-GEN-090）。
    ///   expedition.anchor.max_attempts 次都不合格时取离领地最近的可达点并记日志（<see cref="ExpeditionSiteAnchorRecord.Fallback"/>）；从核心出发一条路线都没有才算失败（派遣会被拦并说明）。
    /// - 结果与从核心到锚点的路线进存档（<see cref="ExpeditionTeamState.Anchors"/>）：之后探索、建造都不会让锚点漂移；派遣检查单的“路线走出覆盖”、预计时间按这条路线算。
    ///
    /// 覆盖（FGR-SIG-050 / 053）：星球上的行军与检查单的路线都按家园所在星球表面、与核心连通的覆盖网采样（走到锚点为止都是真实覆盖规则）。
    /// 远征地点内部的覆盖根本卡没有改：仍是 FG1-SIG-07 的“远征接入点”（恒与核心连通），不看锚点在不在核心连通的覆盖里——
    /// 改成按锚点判定会让现有的每一次远征在目的地都收不到命令（锚点离核心 300～600 格，核心覆盖 150 格），属于玩法取舍，退回 FG8-GEN-02（DEBT-FG8EXP02-02，待用户复核）。
    ///
    /// 开销：只在第一次用到锚点时（打开派遣检查单 / 出发）解析一次，每个地点每次试探一条寻路（AOT 寻路内核的后台通道，Burst 作业）；之后只读存档里的结果，O(1)。
    /// FG8-GEN-02 其余部分（领地模板、手工锚点场地、室内场地）不在这里。
    /// </summary>
    public static class ExpeditionSiteAnchors
    {
        public const int StatePending = 0;
        public const int StateResolved = 1;
        public const int StateFailed = 2;

        private const uint AnchorSalt = 0x45584150u; // "EXAP"

        /// <summary>有锚点的远征地点（顺序 = 存档里的顺序 = 寻路请求的 OwnerKey - 1）。</summary>
        public static readonly string[] SiteIds = { FracturedCityLayout.RegionId, FoundryOutpostLayout.RegionId };

        /// <summary>自检读点：发出的校验请求数、交回的结果数。</summary>
        public static int RequestCount { get; private set; }
        public static int DeliverCount { get; private set; }

        /// <summary>地点所属的规划层领地（fg.TbTerritory.id）。</summary>
        public static string TerritoryIdOf(string siteId) =>
            siteId == FoundryOutpostLayout.RegionId ? "foundry" : siteId == FracturedCityLayout.RegionId ? "silent" : null;

        private static int KeyOf(string siteId) => siteId == FoundryOutpostLayout.RegionId ? 2 : 1;

        public static int MaxAttempts => Math.Max(1, (int)Math.Round(Tuning("expedition.anchor.max_attempts", 8f)));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static ExpeditionSiteAnchorRecord Find(CampaignState s, string siteId)
        {
            ExpeditionSiteAnchorRecord[] list = s?.Grid?.ExpeditionTeam?.Anchors;
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null && list[i].SiteId == siteId)
                {
                    return list[i];
                }
            }
            return null;
        }

        private static ExpeditionSiteAnchorRecord FindByKey(CampaignState s, int key) =>
            key >= 1 && key <= SiteIds.Length ? Find(s, SiteIds[key - 1]) : null;

        /// <summary>锚点已定。</summary>
        public static bool IsResolved(CampaignState s, string siteId) => Find(s, siteId) is { State: StateResolved };

        /// <summary>锚点解析失败（从核心出发一条路线都没有）。</summary>
        public static bool IsFailed(CampaignState s, string siteId) => Find(s, siteId) is { State: StateFailed };

        /// <summary>锚点（星球格）。没定时返回 false。</summary>
        public static bool TryGet(CampaignState s, string siteId, out Vector2 anchor)
        {
            ExpeditionSiteAnchorRecord r = Find(s, siteId);
            if (r == null || r.State != StateResolved)
            {
                anchor = default;
                return false;
            }
            anchor = new Vector2(r.X, r.Y);
            return true;
        }

        /// <summary>从归还核心到锚点的路线（星球格，起点在前）。锚点没定时返回 null。每次新建列表（只在检查单 / 预计时间里调用）。</summary>
        public static List<Vector2> Route(CampaignState s, string siteId)
        {
            ExpeditionSiteAnchorRecord r = Find(s, siteId);
            if (r == null || r.State != StateResolved || r.RouteX == null || r.RouteX.Length == 0)
            {
                return null;
            }
            var list = new List<Vector2>(r.RouteX.Length);
            for (int i = 0; i < r.RouteX.Length && i < r.RouteY.Length; i++)
            {
                list.Add(new Vector2(r.RouteX[i], r.RouteY[i]));
            }
            return list;
        }

        /// <summary>路线长度（格）；锚点没定时返回 -1。</summary>
        public static float RouteLength(CampaignState s, string siteId)
        {
            ExpeditionSiteAnchorRecord r = Find(s, siteId);
            return r != null && r.State == StateResolved ? r.RouteLength : -1f;
        }

        /// <summary>
        /// 第 <paramref name="attempt"/> 次试探的候选点（纯函数：只取决于世界种子、规划层与核心枢轴格）。
        /// 有规划层：领地中心朝核心方向 ± 抖动、离中心 radial_min～max 倍半径；没有规划层（旧原型世界）：按种子方向、离核心 expedition.anchor.fallback_distance 格。
        /// </summary>
        public static GridCell Candidate(CampaignState s, string siteId, int attempt, out string territoryId)
        {
            GridCell core = HomeGridService.CorePivot(s);
            uint seed = unchecked((uint)(s?.World?.WorldSeed ?? s?.RandomSeed ?? 0));
            uint h = WorldGenMath.Hash(seed, KeyOf(siteId), attempt, AnchorSalt);
            PlannedTerritory t = WorldGenQuery.Plan(s)?.Find(TerritoryIdOf(siteId));
            territoryId = t?.Id ?? string.Empty;
            // FG8-GEN-02：生成器 v3 起 Demo 两张手工地图的关键部分是规划层的锚点模板（平整过的地面、入口朝向家园）——第 0 次试探就是模板的入口，
            // 远征队行军的目的地与星球上嵌入的模板是同一个点（卡片“以这两个锚点为入口位置，不另算一套”）。入口走不到时照旧换下一个候选 / 挪到最近可达点。
            PlannedAnchor planned = attempt == 0 ? WorldGen.TerritoryAnchorChecks.AnchorOfSite(s, siteId) : null;
            if (planned != null)
            {
                territoryId = planned.Territory?.Id ?? territoryId;
                return new GridCell(planned.EntryX, planned.EntryY);
            }
            if (t != null)
            {
                int toCore = WorldPlan.RadialAngle(core.X - t.CenterX, core.Y - t.CenterY);
                int jitterMax = Math.Max(0, (int)Math.Round(Tuning("expedition.anchor.jitter_deg", 35f)));
                int jitter = (int)(h % (uint)(2 * jitterMax + 1)) - jitterMax;
                int lo = Mathf.Clamp(Mathf.RoundToInt(Tuning("expedition.anchor.radial_min", 0.7f) * 100f), 0, 100);
                int hi = Mathf.Clamp(Mathf.RoundToInt(Tuning("expedition.anchor.radial_max", 0.9f) * 100f), lo, 100);
                int pct = lo + (int)((h >> 11) % (uint)(hi - lo + 1));
                int dist = t.Radius * pct / 100;
                int angle = toCore + jitter;
                return new GridCell(t.CenterX + (int)(((long)dist * WorldPlan.Cos(angle) + 32768) >> 16),
                    t.CenterY + (int)(((long)dist * WorldPlan.Cos(angle - 90) + 32768) >> 16));
            }
            int a = (int)(h % 360u);
            int d = Mathf.RoundToInt(Tuning(siteId == FoundryOutpostLayout.RegionId
                ? "expedition.anchor.fallback_distance.foundry"
                : "expedition.anchor.fallback_distance.city", siteId == FoundryOutpostLayout.RegionId ? 600f : 300f));
            return new GridCell(core.X + (int)(((long)d * WorldPlan.Cos(a) + 32768) >> 16), core.Y + (int)(((long)d * WorldPlan.Cos(a - 90) + 32768) >> 16));
        }

        // ─────────────────────────────── 解析（同步，后台寻路通道）───────────────────────────────

        private static readonly List<int2> PointScratch = new List<int2>(256);

        /// <summary>
        /// 确保两个地点的锚点都已解析（派遣检查单 / 出发事务 / 自检在用到锚点之前调用；不在模拟步内调用）。
        /// 每次试探向寻路内核的后台通道发一条请求并当场取回（后台通道是独立的工作副本，发出即取回、不跨步占用，不影响突袭导演按时使用它）；
        /// 后台通道正被别人占着（突袭导演的长路线还没取回）时这次先不解析，返回 false（调用方显示“目的地路线计算中”，下次刷新再试）。
        /// 结果只取决于此刻的格网（地形由种子决定；锚点在远处的领地里，家园建筑基本碰不到），解析后进存档、不再变。O(地点 × 试探次数) 条寻路，只在第一次。
        /// </summary>
        public static bool EnsureResolved(CampaignState s)
        {
            if (s?.Grid?.ExpeditionTeam == null)
            {
                return false;
            }
            bool all = true;
            for (int i = 0; i < SiteIds.Length; i++)
            {
                ExpeditionSiteAnchorRecord r = Ensure(s, SiteIds[i]);
                if (r.State != StatePending)
                {
                    continue;
                }
                if (!NavService.IsBound || !ReferenceEquals(NavService.BoundState, s))
                {
                    all = false;
                    continue;
                }
                while (r.State == StatePending)
                {
                    if (NavService.BackgroundActive)
                    {
                        return false;
                    }
                    GridCell c = Candidate(s, r.SiteId, r.Attempt, out string territoryId);
                    r.TerritoryId = territoryId;
                    r.CandidateX = c.X;
                    r.CandidateY = c.Y;
                    r.Serial++;
                    if (!NavService.StartBackground(NavService.OwnerExpedition, KeyOf(r.SiteId), r.Serial, NavConst.ClassPlayer, HomeGridService.CorePivot(s), c, allowPartial: true))
                    {
                        return false;
                    }
                    RequestCount++;
                    bool got = NavService.CollectBackground(PointScratch, out NavResult res);
                    if (!got)
                    {
                        // 后台通道这次没交回结果（不是“走不到”）：不算一次试探、不会因此记成失败，下次刷新再试（审查 P2：失败状态进存档后不再重试）。
                        r.Note = $"第 {r.Attempt + 1} 次试探 ({r.CandidateX},{r.CandidateY})：寻路结果还没交回，下次再试";
                        if (NavService.BackgroundActive && NavService.BackgroundOwnerTag == NavService.OwnerExpedition)
                        {
                            NavService.CancelBackground(); // 不占着后台通道（突袭导演按时要用）
                        }
                        return false;
                    }
                    DeliverCount++;
                    Evaluate(s, r, true, res, PointScratch);
                }
            }
            return all;
        }

        private static ExpeditionSiteAnchorRecord Ensure(CampaignState s, string siteId)
        {
            ExpeditionSiteAnchorRecord r = Find(s, siteId);
            if (r != null)
            {
                return r;
            }
            r = new ExpeditionSiteAnchorRecord { SiteId = siteId };
            var list = new List<ExpeditionSiteAnchorRecord>(s.Grid.ExpeditionTeam.Anchors ?? Array.Empty<ExpeditionSiteAnchorRecord>()) { r };
            // 存档里按 SiteIds 的顺序排（指纹不依赖创建顺序）。
            list.Sort((x, y) => Array.IndexOf(SiteIds, x.SiteId).CompareTo(Array.IndexOf(SiteIds, y.SiteId)));
            s.Grid.ExpeditionTeam.Anchors = list.ToArray();
            return r;
        }

        /// <summary>一次试探的结果：走得到 → 锚点 = 实际终点；部分路线的最近可达点仍在领地内 → 挪到那里；否则记下离领地最近的可达点、换下一个候选。</summary>
        private static void Evaluate(CampaignState s, ExpeditionSiteAnchorRecord r, bool got, in NavResult res, List<int2> points)
        {
            bool any = got && (res.Status == NavStatus.Ok || res.Status == NavStatus.Partial) && points.Count > 0;
            if (!any)
            {
                r.Note = $"第 {r.Attempt + 1} 次试探 ({r.CandidateX},{r.CandidateY})：寻路失败 {res.Status}/{res.Reason}";
                NextAttempt(r);
                return;
            }
            ReadRoute(points, res.End, out int[] xs, out int[] ys, out float length);
            int2 end = res.End;
            PlannedTerritory t = WorldGenQuery.Plan(s)?.Find(TerritoryIdOf(r.SiteId));
            float gap;
            if (t != null)
            {
                float dx = end.x - t.CenterX;
                float dy = end.y - t.CenterY;
                gap = Mathf.Max(0f, Mathf.Sqrt(dx * dx + dy * dy) - t.Radius);
            }
            else
            {
                gap = res.Status == NavStatus.Ok ? 0f : Vector2.Distance(new Vector2(end.x, end.y), new Vector2(r.CandidateX, r.CandidateY));
            }
            bool moved = res.Status != NavStatus.Ok || end.x != r.CandidateX || end.y != r.CandidateY;
            if (gap <= 0f)
            {
                Resolve(r, end.x, end.y, xs, ys, length, moved, fallback: false,
                    res.Status == NavStatus.Ok ? null : $"候选 ({r.CandidateX},{r.CandidateY}) 走不到，挪到领地内最近可达点 ({end.x},{end.y})");
                return;
            }
            if (!r.HasBest || gap < r.BestGap)
            {
                r.HasBest = true;
                r.BestGap = gap;
                r.BestX = end.x;
                r.BestY = end.y;
                r.BestRouteX = xs;
                r.BestRouteY = ys;
                r.BestRouteLength = length;
            }
            r.Note = $"第 {r.Attempt + 1} 次试探 ({r.CandidateX},{r.CandidateY})：最近可达点 ({end.x},{end.y}) 在领地外 {gap:0} 格";
            NextAttempt(r);
        }

        private static void NextAttempt(ExpeditionSiteAnchorRecord r)
        {
            r.Attempt++;
            if (r.Attempt < MaxAttempts)
            {
                return; // 下一步按新的子种子发下一个候选
            }
            if (r.HasBest)
            {
                Resolve(r, r.BestX, r.BestY, r.BestRouteX, r.BestRouteY, r.BestRouteLength, moved: true, fallback: true,
                    $"{MaxAttempts} 次试探都没有领地内的可达点，取离领地最近的可达点 ({r.BestX},{r.BestY})，距领地 {r.BestGap:0} 格（FGR-GEN-090 需要调表）");
                Log.Warning($"[ExpeditionSiteAnchors] {r.SiteId}：{r.Note}");
                return;
            }
            r.State = StateFailed;
            Log.Error($"[ExpeditionSiteAnchors] {r.SiteId}：从归还核心出发 {MaxAttempts} 次试探都没有可走的路线（{r.Note}）。远征派遣会被拦下并说明原因。");
        }

        private static void Resolve(ExpeditionSiteAnchorRecord r, int x, int y, int[] xs, int[] ys, float length, bool moved, bool fallback, string note)
        {
            r.State = StateResolved;
            r.X = x;
            r.Y = y;
            r.RouteX = xs ?? Array.Empty<int>();
            r.RouteY = ys ?? Array.Empty<int>();
            r.RouteLength = length;
            r.Moved = moved;
            r.Fallback = fallback;
            if (note != null)
            {
                r.Note = note;
            }
            // 兜底用的中间数据不再需要（存档里不留两条路线）。
            r.BestRouteX = Array.Empty<int>();
            r.BestRouteY = Array.Empty<int>();
            Log.Info($"[ExpeditionSiteAnchors] {r.SiteId} 锚点 ({x},{y})，领地 {r.TerritoryId}，第 {r.Attempt + 1} 次试探，路线 {length:0} 格{(moved ? "（已挪位）" : string.Empty)}{(fallback ? "（兜底）" : string.Empty)}。");
        }

        private static void ReadRoute(List<int2> points, int2 end, out int[] xs, out int[] ys, out float length)
        {
            int n = points.Count;
            bool appendEnd = n == 0 || !points[n - 1].Equals(end);
            xs = new int[n + (appendEnd ? 1 : 0)];
            ys = new int[xs.Length];
            for (int k = 0; k < n; k++)
            {
                xs[k] = points[k].x;
                ys[k] = points[k].y;
            }
            if (appendEnd)
            {
                xs[xs.Length - 1] = end.x;
                ys[ys.Length - 1] = end.y;
            }
            length = 0f;
            for (int k = 1; k < xs.Length; k++)
            {
                float dx = xs[k] - xs[k - 1];
                float dy = ys[k] - ys[k - 1];
                length += Mathf.Sqrt(dx * dx + dy * dy);
            }
        }
    }
}
