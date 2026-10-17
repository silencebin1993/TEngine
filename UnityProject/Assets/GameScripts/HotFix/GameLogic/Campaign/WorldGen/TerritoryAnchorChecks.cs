using System;
using System.Collections.Generic;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG8-GEN-02（FGR-GEN-024“保证从家园出发可达（层级寻路校验，FGR-ARC-015）”、FGR-GEN-090“锚点可达、室内入口可达、白潮滩头预留区可达”）：
    /// 手工锚点的可达性校验。规划层只给出（种子的纯函数）锚点与入口；这里用层级寻路从归还核心枢轴格走到入口，结果进存档（WorldGenState.AnchorChecks），之后只读。
    ///
    /// - 走得到 → 可达（State 1），入口就是规划的位置。
    /// - 走不到、但部分路线的终点落在这个锚点的平整范围里 → 挪位（State 2）：入口挪到那个最近可达点（FG17 第 5 节“按规则挪位”，确定：只取决于此刻的格网），写日志。
    /// - 都不是 → 失败（State 3），写错误日志（同一结论只报一次）；入口仍按规划位置显示，界面写明“从家园出发走不到”（不悄悄放行）。失败不是永久结论：玩家再次点入口 / 点“进入”时重新校验一次
    ///   （例如玩家拆掉了挡路的墙）；面板定时刷新只读结论、不重算。失败后不换位置重新生成（规划层是种子的纯函数、地形平整由它决定），这一缺口登记 DEBT-FG8GEN02-09。
    /// - 远征地点（Demo 两张手工地图）的入口就是 FG8-EXP-02 的远征锚点（<see cref="ExpeditionSiteAnchors"/>，v3 起第 0 次试探 = 规划层的模板入口）：这里直接读它的结果，不另算一套。
    ///
    /// 时机：按需、同步（后台通道发出后当场取回，不跨步占用，突袭导演按时使用后台通道不受影响）——打开室内入口面板 / 进入室内、地图要画“已挪位”、自检时。
    /// 新战役不在开局一口气校验全部（每条最远的寻路在编辑器里要几百毫秒）。开销：每个锚点一条寻路（AOT Burst），之后 O(1) 读存档。
    /// </summary>
    public static class TerritoryAnchorChecks
    {
        public const int StatePending = 0;
        public const int StateOk = 1;
        public const int StateMoved = 2;
        public const int StateFailed = 3;

        private static readonly List<int2> PointScratch = new List<int2>(256);

        /// <summary>自检读点：发出的校验请求数。</summary>
        public static int RequestCount { get; private set; }

        public static AnchorCheckRecord Find(CampaignState s, string anchorId)
        {
            AnchorCheckRecord[] list = s?.World?.AnchorChecks;
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null && list[i].AnchorId == anchorId)
                {
                    return list[i];
                }
            }
            return null;
        }

        /// <summary>远征地点锚点 → FG8-EXP-02 的远征地点 ID（不是远征地点返回 null）。</summary>
        public static string ExpeditionSiteOf(string anchorId) =>
            anchorId == "fractured_city" ? FracturedCityLayout.RegionId : anchorId == "foundry_outpost" ? FoundryOutpostLayout.RegionId : null;

        /// <summary>远征地点 → 它在规划层里的锚点（v3 起有；v1 / v2 返回 null）。</summary>
        public static PlannedAnchor AnchorOfSite(CampaignState s, string siteId)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return null;
            }
            string id = siteId == FracturedCityLayout.RegionId ? "fractured_city" : siteId == FoundryOutpostLayout.RegionId ? "foundry_outpost" : null;
            return id != null ? plan.FindAnchor(id) : null;
        }

        /// <summary>入口的当前位置：校验过（可达 / 挪位）用校验结果，否则用规划层的入口。远征地点读远征锚点。</summary>
        public static Vector2 EntryOf(CampaignState s, PlannedAnchor a)
        {
            if (a == null)
            {
                return Vector2.zero;
            }
            string site = ExpeditionSiteOf(a.Id);
            if (site != null && ExpeditionSiteAnchors.TryGet(s, site, out Vector2 anchor))
            {
                return anchor;
            }
            AnchorCheckRecord r = Find(s, a.Id);
            if (r != null && (r.State == StateOk || r.State == StateMoved))
            {
                return new Vector2(r.X, r.Y);
            }
            return new Vector2(a.EntryX, a.EntryY);
        }

        public static int StateOf(CampaignState s, PlannedAnchor a)
        {
            if (a == null)
            {
                return StateFailed;
            }
            string site = ExpeditionSiteOf(a.Id);
            if (site != null)
            {
                ExpeditionSiteAnchorRecord e = ExpeditionSiteAnchors.Find(s, site);
                if (e == null || e.State == ExpeditionSiteAnchors.StatePending)
                {
                    return StatePending;
                }
                return e.State == ExpeditionSiteAnchors.StateFailed ? StateFailed : e.Moved ? StateMoved : StateOk;
            }
            return Find(s, a.Id)?.State ?? StatePending;
        }

        public static bool IsMoved(CampaignState s, string anchorId) => StateOf(s, WorldGenService.PlanFor(s)?.FindAnchor(anchorId)) == StateMoved;

        public static bool IsReachable(CampaignState s, PlannedAnchor a)
        {
            int st = StateOf(s, a);
            return st == StateOk || st == StateMoved;
        }

        /// <summary>自检注入：返回 true 的锚点把寻路结果当作“一条路线都没有”（请求照常发出、照常计数），用来驱动失败态的负向断言。</summary>
        public static Func<string, bool> ForceUnreachableForTests;

        /// <summary>
        /// 校验一个锚点（已有结论且不是失败时直接返回；<paramref name="force"/> 时重新校验）。寻路服务没绑定、后台通道正被别人占着时这次不校验（返回待定）。
        /// 失败态：只有 <paramref name="retryFailed"/>（玩家动作——点入口打开面板、点“进入”、自检）才重新校验一次；面板定时刷新传 false，直接读存档里的结论，
        /// 不发寻路请求、不写日志（审查 P1：失败态下面板每 0.25 秒同步跑一条长寻路并刷错误日志）。
        /// 远征地点交给 <see cref="ExpeditionSiteAnchors.EnsureResolved"/>。
        /// </summary>
        public static int Check(CampaignState s, PlannedAnchor a, bool force = false, bool retryFailed = true)
        {
            if (s?.World == null || a == null)
            {
                return StateFailed;
            }
            if (ExpeditionSiteOf(a.Id) != null)
            {
                // 远征锚点：待定时解析一次；失败 / 已定都进存档、之后只读（不会每次刷新重跑）。
                if (StateOf(s, a) == StatePending)
                {
                    ExpeditionSiteAnchors.EnsureResolved(s);
                }
                return StateOf(s, a);
            }
            AnchorCheckRecord r = Ensure(s, a.Id);
            if (!force && (r.State == StateOk || r.State == StateMoved))
            {
                return r.State;
            }
            if (!force && !retryFailed && r.State == StateFailed)
            {
                return StateFailed;
            }
            if (!NavService.IsBound || !ReferenceEquals(NavService.BoundState, s) || NavService.BackgroundActive)
            {
                return r.State == StateFailed && !force ? StateFailed : StatePending;
            }
            int tries = Math.Max(1, (int)Math.Round(GridContent.TryGetTuning("anchor.reach_max_attempts", out float t) ? t : 3f));
            for (int k = 0; k < tries; k++)
            {
                r.Serial++;
                r.Attempts++;
                GridCell goal = new GridCell(a.EntryX, a.EntryY);
                if (!NavService.StartBackground(NavService.OwnerAnchorCheck, a.Index + 1, r.Serial, NavConst.ClassPlayer, HomeGridService.CorePivot(s), goal, allowPartial: true))
                {
                    return StatePending;
                }
                RequestCount++;
                if (!NavService.CollectBackground(PointScratch, out NavResult res))
                {
                    if (NavService.BackgroundActive && NavService.BackgroundOwnerTag == NavService.OwnerAnchorCheck)
                    {
                        NavService.CancelBackground();
                    }
                    r.Note = $"第 {r.Attempts} 次寻路没交回结果";
                    continue;
                }
                Evaluate(s, a, r, res, PointScratch);
                return r.State;
            }
            return r.State;
        }

        /// <summary>校验规划层里全部要校验的锚点（自检 / FGT-GEN-004 用）。返回全部有结论（不是待定）。</summary>
        public static bool EnsureAll(CampaignState s)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return true;
            }
            bool all = true;
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a.Row.ReachCheck != 1)
                {
                    continue;
                }
                if (Check(s, a) == StatePending)
                {
                    all = false;
                }
            }
            return all;
        }

        private static AnchorCheckRecord Ensure(CampaignState s, string anchorId)
        {
            AnchorCheckRecord r = Find(s, anchorId);
            if (r != null)
            {
                return r;
            }
            CampaignFgStateDomains.EnsureAll(s);
            r = new AnchorCheckRecord { AnchorId = anchorId };
            var list = new List<AnchorCheckRecord>(s.World.AnchorChecks ?? Array.Empty<AnchorCheckRecord>()) { r };
            list.Sort((x, y) => string.CompareOrdinal(x.AnchorId, y.AnchorId)); // 存档里按 ID 排（指纹不依赖校验先后）
            s.World.AnchorChecks = list.ToArray();
            return r;
        }

        private static void Evaluate(CampaignState s, PlannedAnchor a, AnchorCheckRecord r, in NavResult res, List<int2> points)
        {
            int prevState = r.State;
            string prevNote = r.Note;
            bool any = (res.Status == NavStatus.Ok || res.Status == NavStatus.Partial) && points.Count > 0
                       && !(ForceUnreachableForTests != null && ForceUnreachableForTests(a.Id));
            int2 end = res.End;
            float length = 0f;
            int2 prev = new int2(HomeGridService.CorePivot(s).X, HomeGridService.CorePivot(s).Y);
            for (int i = 0; i < points.Count; i++)
            {
                length += math.distance(new float2(prev), new float2(points[i]));
                prev = points[i];
            }
            if (any && res.Status == NavStatus.Ok)
            {
                r.State = StateOk;
                r.X = a.EntryX;
                r.Y = a.EntryY;
                r.RouteLength = length;
                r.Note = string.Empty;
                return;
            }
            if (any)
            {
                long dx = end.x - a.OriginX;
                long dy = end.y - a.OriginY;
                long fp = Math.Max(a.FootprintRadius, a.EntryRadius) + a.BlendWidth;
                if (dx * dx + dy * dy <= fp * fp)
                {
                    r.State = StateMoved;
                    r.X = end.x;
                    r.Y = end.y;
                    r.RouteLength = length;
                    r.Note = $"规划入口 ({a.EntryX},{a.EntryY}) 走不到，挪到平整范围里最近的可达点 ({end.x},{end.y})";
                    Log.Warning($"[TerritoryAnchorChecks] {a.Id}：{r.Note}");
                    return;
                }
            }
            r.State = StateFailed;
            r.X = a.EntryX;
            r.Y = a.EntryY;
            r.RouteLength = length;
            r.Note = any
                ? $"从核心出发最远只能走到 ({end.x},{end.y})，离入口还有 {Mathf.Sqrt((end.x - a.EntryX) * (float)(end.x - a.EntryX) + (end.y - a.EntryY) * (float)(end.y - a.EntryY)):0} 格"
                : $"寻路失败 {res.Status}/{res.Reason}";
            // 同一结论只报一次错（玩家反复点入口重新校验、结论没变时不刷日志）。
            if (prevState != StateFailed || prevNote != r.Note)
            {
                Log.Error($"[TerritoryAnchorChecks] {a.Id}：从家园出发走不到入口（{r.Note}；FGR-GEN-090，需要调表或检查地形；失败后不做局部重生成，见 DEBT-FG8GEN02-09）");
            }
        }
    }
}
