using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG8-GEN-02（FG17 FGR-GEN-042；卡片“室内进出的过渡”）：室内场地的进出——唯一入口。
    ///
    /// - <b>进入</b>：地表入口（规划层的室内入口锚点，按种子落在领地里）旁 entryRadius 格内、收得到命令的家园机器，经入口面板“进入”：
    ///   ① 进入首领场地前先自动存档（沿用 SaveReason.BossEngageEnter；存档没成功就不进去，机器原地不动，B10）；② 机器转进室内地点
    ///   （<see cref="InteriorSiteController"/>，没载入就载入）；③ 镜头切到室内，画面过渡（interior.transition_seconds 真实秒，这是星球上唯一不无缝的地方）。
    /// - <b>离开</b>：室内出口旁 interior.exit_radius 格内的机器，经出口面板“离开室内”回到地表上同一个入口旁；室内一台都不剩时卸载。
    /// - 室内与地表同时运行（统一时钟的同一步里推进，FGR-ARC-002）；读档时有机器在室内就一起恢复（<see cref="ResumeAll"/>）。
    /// - 拒绝都给原因（B06）：旧版本世界没有入口、入口走不到、入口旁没有机器、机器在覆盖之外收不到命令、机器在远征行军中、存档失败。
    /// 开销：只在玩家点按钮时做，O(机器数)。
    /// </summary>
    public static class InteriorSiteService
    {
        /// <summary>入口 / 出口面板请求（界面订阅；参数 = 锚点 ID 或室内表面 ID、是不是出口）。</summary>
        public static event Action<string, bool> PanelRequested;

        /// <summary>自检读点：进出次数、最近一次拒绝原因、过渡。</summary>
        public static int EnterCount { get; private set; }
        public static int ExitCount { get; private set; }
        public static string LastReason { get; private set; } = string.Empty;
        public static float TransitionUntil { get; private set; } = -1f;
        public static string TransitionText { get; private set; } = string.Empty;
        public static bool TransitionActive => TransitionUntil > Time.unscaledTime;

        public static float EntrancePickRadius => Mathf.Max(1f, Tuning("interior.pick_radius", 5f));
        public static float ExitRadius => Mathf.Max(1f, Tuning("interior.exit_radius", 6f));
        public static float TransitionSeconds => Mathf.Max(0f, Tuning("interior.transition_seconds", 0.6f));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static void ResetSession()
        {
            TransitionUntil = -1f;
            TransitionText = string.Empty;
            LastReason = string.Empty;
        }

        // ── 查询 ───────────────────────────────────────────────────────────────

        /// <summary>通往 <paramref name="surfaceId"/> 的地表入口锚点（v3 起有；v1 / v2 世界返回 null）。</summary>
        public static PlannedAnchor EntranceFor(CampaignState s, string surfaceId)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return null;
            }
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a.IsIndoorEntrance && a.SurfaceId == surfaceId)
                {
                    return a;
                }
            }
            return null;
        }

        /// <summary>离 <paramref name="p"/> 最近、在 <paramref name="radius"/> 格内的室内入口（左键点入口 / 悬停用）。</summary>
        public static PlannedAnchor EntranceAt(CampaignState s, Vector2 p, float radius = -1f)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return null;
            }
            float r = radius > 0f ? radius : EntrancePickRadius;
            PlannedAnchor best = null;
            float bestD = r * r;
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (!a.IsIndoorEntrance)
                {
                    continue;
                }
                float d = (TerritoryAnchorChecks.EntryOf(s, a) - p).sqrMagnitude;
                if (d <= bestD)
                {
                    bestD = d;
                    best = a;
                }
            }
            return best;
        }

        public static bool IsInteriorSurface(string id) => !string.IsNullOrEmpty(id) && WorldGenContent.TryGetSurface(id, out GameConfig.fg.Surface s) && WorldGenContent.IsInterior(s);

        /// <summary>入口旁 entryRadius 格内、存活、在家园（星球表面）、没在工厂里的机器（按编号）。</summary>
        public static List<int> MachinesNearEntrance(CampaignState s, PlannedAnchor a)
        {
            var list = new List<int>();
            if (s == null || a == null)
            {
                return list;
            }
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, a);
            float r = Mathf.Max(1f, a.EntryRadius);
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.IsInFactory || m.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                Vector2 p = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                if ((p - e).sqrMagnitude <= r * r)
                {
                    list.Add(m.LogicId);
                }
            }
            list.Sort();
            return list;
        }

        /// <summary>室内出口旁 interior.exit_radius 格内的存活机器（按编号）。</summary>
        public static List<int> MachinesNearExit(CampaignState s, string surfaceId)
        {
            var list = new List<int>();
            InteriorSiteController site = WorldSimulation.Interior(surfaceId);
            if (s == null || site == null || !site.IsLoaded)
            {
                return list;
            }
            float r = ExitRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != surfaceId)
                {
                    continue;
                }
                Vector2 p = site.LivePosition(m.LogicId) ?? m.WorldPosition;
                if ((p - site.ExitPoint).sqrMagnitude <= r * r)
                {
                    list.Add(m.LogicId);
                }
            }
            list.Sort();
            return list;
        }

        /// <summary>
        /// 能不能从这个入口进去：能返回 null，否则返回给玩家看的原因；<paramref name="ready"/> = 收得到命令、可以进去的机器。
        /// <paramref name="verify"/>：玩家动作（点入口打开面板、点“进入”）传 true，入口可达校验失败过的会重新校验一次；面板定时刷新传 false，只读校验结论（不发寻路请求）。
        /// </summary>
        public static string CheckEnter(CampaignState s, PlannedAnchor a, out List<int> ready, bool verify = true)
        {
            ready = new List<int>();
            if (s?.World == null)
            {
                return GameText.Get("interior.reason.not_found");
            }
            if (a == null || !a.IsIndoorEntrance)
            {
                return WorldGenService.PlanFor(s) != null && !WorldGenService.PlanFor(s).HasTemplates
                    ? GameText.Format("interior.reason.legacy_world", s.World.GeneratorVersion)
                    : GameText.Get("interior.reason.not_found");
            }
            int st = TerritoryAnchorChecks.Check(s, a, retryFailed: verify);
            if (st == TerritoryAnchorChecks.StateFailed)
            {
                return GameText.Format("interior.reason.unreachable", TerritoryAnchorChecks.Find(s, a.Id)?.Note ?? string.Empty);
            }
            List<int> near = MachinesNearEntrance(s, a);
            if (near.Count == 0)
            {
                return GameText.Format("interior.reason.none_near", a.EntryRadius);
            }
            int outside = 0;
            int marching = 0;
            int busy = 0;
            foreach (int id in near)
            {
                if (ExpeditionMarchService.IsMarchMember(s, id))
                {
                    marching++;
                    continue;
                }
                if (!Signal.SignalCoverageService.CanReceiveCommand(id))
                {
                    outside++;
                    continue;
                }
                // 与远征出发同一条中断规则（审查 P2）：手上是不可中断工作（紧急修复）的机器不跟着进去；其余的在办工作进入时交还 / 取消（见 TryEnter）。
                if (ExpeditionDepartureService.IsUninterruptibleOrder(HomeValleyWorkOrders.FindActiveOrderForMachine(s, id)))
                {
                    busy++;
                    continue;
                }
                ready.Add(id);
            }
            if (ready.Count == 0)
            {
                return marching > 0 && outside == 0 && busy == 0
                    ? GameText.Format("interior.reason.marching", marching)
                    : busy > 0 && outside == 0
                        ? GameText.Format("interior.reason.busy", busy)
                        : GameText.Format("interior.reason.out_of_coverage", outside);
            }
            return null;
        }

        /// <summary>这些机器里有几台手上有（可中断的）在办工作——进入时会被交还 / 取消（面板写明）。</summary>
        public static int CountInterrupting(CampaignState s, List<int> ids)
        {
            int n = 0;
            if (s == null || ids == null)
            {
                return 0;
            }
            foreach (int id in ids)
            {
                if (HomeValleyWorkOrders.FindActiveOrderForMachine(s, id) != null)
                {
                    n++;
                }
            }
            return n;
        }

        // ── 进入 / 离开 ─────────────────────────────────────────────────────────

        /// <summary>从入口 <paramref name="anchorId"/> 进入（入口面板“进入”）。成功返回 true；失败时 <paramref name="reason"/> 是给玩家看的原因（拒绝音由调用方发）。</summary>
        public static bool TryEnter(CampaignState s, string anchorId, out string reason)
        {
            PlannedAnchor a = WorldGenService.PlanFor(s)?.FindAnchor(anchorId);
            reason = CheckEnter(s, a, out List<int> ready);
            if (reason != null)
            {
                LastReason = reason;
                return false;
            }
            // ① 进入首领场地前的自动存档（FGR-GEN-042）：存在机器还在门外的这一刻；读这个档 = 回到入口外面重新进。
            SaveResult save = CampaignAutoSaveService.SaveAuto(SaveReason.BossEngageEnter);
            if (!save.Success)
            {
                reason = GameText.Get("interior.reason.save_failed");
                LastReason = reason;
                Log.Warning($"[InteriorSiteService] 进入 {a.SurfaceId} 前的自动存档没有成功（{save.Outcome} {save.Message}），已取消进入。");
                return false;
            }
            // ② 机器手上的在办工作按远征出发的规则交还 / 取消（施工单让给留守的机器，其余按玩家取消：资源退款、货舱落地；面板事先写明会中断几台）——
            //    不让工单跟着机器进室内一直挂着、占着预留资源（审查 P2）。
            ExpeditionDepartureService.InterruptWorkForDeparture(s, ready);
            // ③ 机器转进室内（同一步里：家园下一次名册对账时移走它们的句柄，室内地点按记录建单位）。
            InteriorSiteController site = WorldSimulation.LoadInterior(a.SurfaceId, ready, resume: false);
            if (site == null || !site.IsLoaded)
            {
                reason = GameText.Get("interior.reason.not_found");
                LastReason = reason;
                return false;
            }
            EnterCount++;
            LastReason = string.Empty;
            string name = SurfaceName(a.SurfaceId);
            // ④ 镜头切到室内 + 过渡（真实时间；世界照常运行）。
            BeginTransition(GameText.Format("ui.interior.transition.enter", name));
            WorldView.Observe(a.SurfaceId);
            // 反馈时刻按表（fg.TbNotifyType cues 列）转成一条“室内进出”通知，带入口坐标（通知中心可跳转）；不另发一条，避免重复。
            FeedbackCues.RaiseLocated(FeedbackCueId.InteriorEnter, new Vector2(a.EntryX, a.EntryY), GameText.Format("interior.notify.entered", ready.Count, name));
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstInteriorEntered);
            Log.Info($"[InteriorSiteService] {ready.Count} 台机器从 {a.Id} 进入 {a.SurfaceId}。");
            return true;
        }

        /// <summary>离开室内 <paramref name="surfaceId"/>（出口面板“离开室内”）：出口旁的机器回到地表入口旁。</summary>
        public static bool TryExit(CampaignState s, string surfaceId, out string reason)
        {
            InteriorSiteController site = WorldSimulation.Interior(surfaceId);
            List<int> near = MachinesNearExit(s, surfaceId);
            if (site == null || !site.IsLoaded)
            {
                reason = GameText.Get("interior.reason.not_found");
                LastReason = reason;
                return false;
            }
            if (near.Count == 0)
            {
                reason = GameText.Format("interior.reason.exit_none_near", Mathf.RoundToInt(ExitRadius));
                LastReason = reason;
                return false;
            }
            PlannedAnchor a = EntranceFor(s, surfaceId);
            Vector2 e = a != null ? TerritoryAnchorChecks.EntryOf(s, a) : HomeValleyLayout.Core.Position;
            Vector2 outward = a != null ? OutwardOf(s, a) : Vector2.down;
            site.RemoveMachines(near);
            float spacing = Mathf.Max(1.5f, Tuning("interior.spawn_spacing", 2.5f));
            for (int i = 0; i < near.Count; i++)
            {
                if (!MachineRegistry.TryGetRecord(near[i], out MachineRecord rec) || rec == null)
                {
                    continue;
                }
                MachineRegistry.MoveToRegion(rec, HomeValleyLayout.RegionId);
                // 落在入口朝家园的一侧排开（入口在平整范围里，脚下一定可走）。
                Vector2 side = new Vector2(-outward.y, outward.x);
                rec.WorldPosition = e - outward * (3f + (i / 5) * spacing) + side * ((i % 5) - 2) * spacing;
            }
            ExitCount++;
            reason = null;
            LastReason = string.Empty;
            string name = SurfaceName(surfaceId);
            bool observed = WorldView.IsObserved(surfaceId);
            if (site.LiveMachineCount == 0 && CountIn(surfaceId) == 0)
            {
                WorldSimulation.UnloadInterior(surfaceId, dropRecord: true);
            }
            if (observed)
            {
                BeginTransition(GameText.Get("ui.interior.transition.exit"));
                WorldView.FlyTo(HomeValleyLayout.RegionId, e);
            }
            FeedbackCues.RaiseLocated(FeedbackCueId.InteriorExit, e, GameText.Format("interior.notify.exited", near.Count, name));
            return true;
        }

        /// <summary>入口的“朝外”方向（模板 +Y，背向归还核心）。</summary>
        public static Vector2 OutwardOf(CampaignState s, PlannedAnchor a)
        {
            float rad = a.ForwardDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }

        /// <summary>室内还有几台存活机器（记录侧）。</summary>
        public static int CountIn(string surfaceId)
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == surfaceId)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>读档：有存活机器在室内的室内地点一起恢复（从战斗快照）；存档时正在看室内就把镜头放回去。返回恢复了几个室内。</summary>
        public static int ResumeAll(CampaignState s)
        {
            if (s?.MachineRecords == null)
            {
                return 0;
            }
            var bySurface = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (MachineRecord m in s.MachineRecords)
            {
                if (m != null && m.IsAlive && IsInteriorSurface(m.RegionId))
                {
                    if (!bySurface.TryGetValue(m.RegionId, out List<int> list))
                    {
                        list = new List<int>();
                        bySurface[m.RegionId] = list;
                    }
                    list.Add(m.LogicId);
                }
            }
            int n = 0;
            foreach (KeyValuePair<string, List<int>> kv in bySurface)
            {
                InteriorSiteController site = WorldSimulation.LoadInterior(kv.Key, kv.Value, resume: true);
                if (site != null && site.IsLoaded)
                {
                    n++;
                }
            }
            string focus = s.World?.InteriorFocusId;
            if (!string.IsNullOrEmpty(focus) && WorldSimulation.Interior(focus) is InteriorSiteController f && f.IsLoaded)
            {
                WorldView.Observe(focus);
            }
            return n;
        }

        public static string SurfaceName(string surfaceId) =>
            WorldGenContent.TryGetSurface(surfaceId, out GameConfig.fg.Surface s) ? GameText.Get(s.NameKey) : surfaceId;

        /// <summary>
        /// FG8-GEN-02：悬停 / 地图说明——离 <paramref name="p"/> 最近的手工锚点（室内入口：入口 pick 半径内；远征地点 / 首领场地：平整范围内），只说已探索的（不泄露情报），
        /// 预留区永不说（FGR-GEN-041）。标题“名字（种类）”，正文：说明、入口位置与距离、怎么进、挪过位 / 走不到时写明。
        /// </summary>
        public static bool TryDescribeAnchorAt(CampaignState s, Vector2 p, out string title, out string body)
        {
            title = body = null;
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null || plan.Anchors.Count == 0)
            {
                return false;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a.IsReserve)
                {
                    continue;
                }
                Vector2 e = TerritoryAnchorChecks.EntryOf(s, a);
                float r = a.IsIndoorEntrance ? EntrancePickRadius : a.FootprintRadius;
                Vector2 c = a.IsIndoorEntrance ? e : new Vector2(a.OriginX, a.OriginY);
                if ((p - c).sqrMagnitude > r * r || !map.IsExploredNoLoad(new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y))))
                {
                    continue;
                }
                title = GameText.Format("anchor.hover.title", GameText.Get(a.Row.NameKey), GameText.Get(a.KindKey));
                var sb = new System.Text.StringBuilder(160);
                sb.Append(GameText.Get(a.Row.DescKey)).Append('\n');
                GridCell core = HomeGridService.CorePivot(s);
                int dist = Mathf.RoundToInt(Vector2.Distance(e, new Vector2(core.X, core.Y)));
                sb.Append(GameText.Format("anchor.hover.entry", Mathf.RoundToInt(e.x), Mathf.RoundToInt(e.y), dist)).Append('\n');
                sb.Append(a.IsIndoorEntrance ? GameText.Format("anchor.hover.indoor", a.EntryRadius, a.ProtectRadius)
                    : a.IsDemoSite ? GameText.Get("anchor.hover.demo") : GameText.Get("anchor.hover.boss"));
                int st = TerritoryAnchorChecks.StateOf(s, a);
                if (st == TerritoryAnchorChecks.StateMoved)
                {
                    sb.Append('\n').Append(GameText.Get("anchor.hover.moved"));
                }
                else if (st == TerritoryAnchorChecks.StateFailed)
                {
                    sb.Append('\n').Append(GameText.Format("anchor.hover.unreachable", TerritoryAnchorChecks.Find(s, a.Id)?.Note ?? string.Empty));
                }
                body = sb.ToString();
                return true;
            }
            return false;
        }

        // ── 面板与过渡 ──────────────────────────────────────────────────────────

        public static void RequestEntrancePanel(string anchorId) => PanelRequested?.Invoke(anchorId, false);

        public static void RequestExitPanel(string surfaceId) => PanelRequested?.Invoke(surfaceId, true);

        public static void BeginTransition(string text)
        {
            TransitionText = text ?? string.Empty;
            TransitionUntil = Time.unscaledTime + TransitionSeconds;
        }
    }
}
