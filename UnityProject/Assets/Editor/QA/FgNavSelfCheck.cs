using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-06 层级寻路与休眠唤醒的自动验收（FG14 FGR-ARC-015 / 016；FG17 FGR-GEN-052 第 3 条、FGT-GEN-010；FG15 长距离寻路 2,000 格 ≤ 20 毫秒；
    /// DEBT-FG0ARCH04-04 / DEBT-FG0ARCH03-03 / DEBT-FG0ARCH05-08 / DEBT-FG0ARCH01-04 的寻路部分）。全部起真实系统：真实 Burst 寻路内核（NavKernel）、
    /// 真实战斗内核（CombatKernel，家园配置）、真实家园（WorldSimulation 载入，工单引擎、编队命令、建造模式）、真实世界生成地形、真实存读档。行为坏了会失败：
    /// A 数据：nav.* / outpost.* 调参行、地形通行代价、文本键中英、通知类型。
    /// B 内核语义（合成地图）：直线 / 绕墙 / 围死 / 目标被占 / 起点被占 / 部分路线 / 越界 / 地形未知 / 超出范围；与暴力 Dijkstra 对照（可达性一致、长度接近最优）；
    ///   冷热缓存结果逐格相同；同一目标共享路线；地形变化增量更新、采纳前被截断的结果重新排队；待处理状态快照往返与负向。
    /// C 战斗内核接入：工作赶路绕墙且从不踏进不可通行格；直控撞墙贴边滑动；无法到达给出原因事件；路线中途被截断重规划；中途存读档逐位一致；分离。
    /// D 家园集成：机器绕开核心；工单目标被围死 → 工单转等待 + 通知一次 + 30 秒重试 + 解围后开工；编队命令无法到达的原因；迷雾里的远距离目标；
    ///   放置预览“会让建筑无法到达”只警告不阻止；突袭沿地形行进、中途被截断重规划、核心被围死时停在最近处并说明；200 个单位同时请求长路线；
    ///   飞行中存读档 = 不存档；观察 / 不观察一致；暂停与 0.5x～3x 一致。
    /// E 休眠与唤醒（FGT-GEN-010）：据点休眠期间跨过多个增援点，唤醒后与“一直在模拟”逐字段一致；己方单位靠近唤醒；突袭导演唤醒；休眠中存读档；
    ///   只读查看不改状态；唤醒排队分帧、单个唤醒 ≤ 2 毫秒；三个种子（B25）。
    /// F 性能：2,000 格路线工作线程耗时（冷 / 热）、大量请求时主线程每步开销。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgNavSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const float Dt = 1f / 60f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 层级寻路与休眠唤醒")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[层级寻路] 层级寻路与休眠唤醒（FG0-ARCH-06）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgnav-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                // FG1-SIG-07：寻路自检不测信号覆盖——注入“全图都在与核心连通的覆盖里”，编队走到几百格外之后的命令不会因“覆盖外收不到命令”被拒
                // （覆盖规则本身见 FgSignalNetworkSelfCheck）。
                GameLogic.Campaign.Signal.SignalCoverageService.OverrideForTests = (site, pos) =>
                    new GameLogic.Campaign.Signal.SignalCoverageSample(true, true, float.PositiveInfinity, Vector2.zero, 0f, GameLogic.Campaign.Signal.SignalCoverageSourceKind.None);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"Burst 编译={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}（作业声明同步编译）。寻路批次在工作线程（Schedule），" +
                     "耗时是 Editor 下的 Burst 作业；真机 IL2CPP 下内核同为 Burst AOT（相同或更快），热更层走 HybridCLR 解释执行另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSynthBasics);
                Step(CheckAgainstBruteForce);
                Step(CheckCacheIndependence);
                Step(CheckSharing);
                Step(CheckIncrementalAndRequeue);
                Step(CheckDetourAndRecheck);
                Step(CheckIslandUnreachable);
                Step(CheckRouteClearAfterExit);
                Step(CheckPendingSnapshot);
                Step(CheckCombatFollow);
                Step(CheckCombatRetarget);
                Step(CheckCombatRouteCutAndSnapshot);
                Step(CheckSeparation);
                Step(CheckHomeDetourAndCut);
                Step(CheckCommandDestinationVisual);
                Step(CheckWorkOrderUnreachable);
                Step(CheckSquadUnreachableAndFog);
                Step(CheckSquadCommandNotHijacked);
                Step(CheckSquadCommandDropsHaulCargo);
                Step(CheckBuildPreviewWarning);
                Step(CheckRaidRoute);
                Step(CheckRaidBlockedCore);
                Step(CheckMassRequests);
                Step(CheckSaveLoadMidFlight);
                Step(CheckObservationAndSpeed);
                Step(CheckDormancyEquivalence);
                Step(CheckProximityWake);
                Step(CheckWakeQueueBudget);
                Step(CheckSeedIndependence);
                Step(CheckLongRoutePerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"层级寻路自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                GameLogic.Campaign.Signal.SignalCoverageService.ResetForTests();
                GameClock.ResetSession();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                GameSettings.SetLanguage(originalLanguage);
                UiEscapeStack.Clear();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [层级寻路] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] ids =
            {
                "nav.latency_steps", "nav.batch_max_requests", "nav.batch_distance_budget", "nav.search_margin_chunks", "nav.max_expansions",
                "nav.goal_search_radius", "nav.start_search_radius", "nav.smooth_lookahead", "nav.separation_factor", "nav.preview_margin_cells",
                "nav.perf.route_cells", "nav.perf.budget_ms", "nav.perf.mass_requests", "outpost.dormant_distance_cells", "outpost.wake_hysteresis_cells",
                "outpost.wakes_per_step", "outpost.wake_budget_ms", "outpost.reinforce_interval_days", "outpost.garrison_cap", "outpost.garrison_start",
                "outpost.patrol_radius_cells", "outpost.patrol_speed_cells_per_second", "outpost.patrol_units",
            };
            var missing = ids.Where(id => !GridContent.TryGetTuning(id, out _)).ToList();
            Expect(missing.Count == 0, $"nav.* / outpost.* 调参行都在 fg.TbHomeTuning（{ids.Length} 行{(missing.Count == 0 ? string.Empty : "；缺：" + string.Join(",", missing))}）");
            NavConfig cfg = NavService.ConfigFromTuning();
            Expect(cfg.ChunkSize == 32 && cfg.LatencySteps == 6 && cfg.GoalSearchRadius == 6 && cfg.CoordLimit == GridContent.TuningInt("world.coord_limit")
                   && Mathf.Approximately(NavService.Tuning("outpost.dormant_distance_cells", 0), 300f) && Mathf.Approximately(NavService.Tuning("nav.perf.budget_ms", 0), 20f),
                $"寻路配置来自表：区块 {cfg.ChunkSize}、采纳延迟 {cfg.LatencySteps} 步、目标就近半径 {cfg.GoalSearchRadius}、坐标上限 {cfg.CoordLimit}；休眠距离 300 格、2,000 格路线预算 20 毫秒（规格初值）");
            byte[] table = NavService.TerrainTable();
            byte water = GridContent.TerrainCode("water");
            byte cliff = GridContent.TerrainCode("cliff");
            byte ruin = GridContent.TerrainCode("ruin");
            byte ground = GridContent.TerrainCode("buildable");
            Expect(NavGridOps.IsBlockedClass(table[water], NavConst.ClassPlayer) && NavGridOps.IsBlockedClass(table[cliff], NavConst.ClassHostile)
                   && !NavGridOps.IsBlockedClass(table[ground], NavConst.ClassPlayer) && NavGridOps.CostOf(table[ruin]) == 2 && NavGridOps.CostOf(table[ground]) == 1
                   && NavGridOps.IsBlockedClass(table[250], NavConst.ClassPlayer),
                "地形通行代价来自 fg.TbGridTerrain.navCost：水源、悬崖不可通行（两个类别），废墟代价 2，空地 1，表里没有的地形码不可通行");
            string[] keys =
            {
                "nav.fail.start_blocked", "nav.fail.goal_blocked", "nav.fail.unreachable", "nav.fail.search_limit", "nav.fail.out_of_world", "nav.fail.unknown_terrain",
                "nav.fail.in_fog", "nav.fail.fix", "nav.squad.unreachable", "nav.work.unreachable", "nav.work.reason", "nav.build.unreachable_warning",
                "nav.transit.blocked", "nav.route.awaiting", "nav.load.rebuilt", "outpost.name", "outpost.patrol.name", "outpost.marker", "outpost.patrol.marker",
                "outpost.wake.proximity", "outpost.wake.raid", "outpost.wake.event", "outpost.placeholder", "notify.type.unreachable.name",
            };
            var noText = new List<string>();
            foreach (string k in keys)
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string zh = GameText.Get(k);
                GameSettings.SetLanguage(GameLanguage.En);
                string en = GameText.Get(k);
                if (!GameText.Has(k) || string.IsNullOrEmpty(zh) || string.IsNullOrEmpty(en) || zh == en || zh.Contains("⟦"))
                {
                    noText.Add(k);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(noText.Count == 0 && NotificationCatalog.TryGetType("unreachable", out _),
                $"{keys.Length} 个 nav / outpost 文本键中英齐全，通知类型“无法到达”已登记{(noText.Count == 0 ? string.Empty : "；缺：" + string.Join(",", noText))}");
        }

        // ── B 内核语义（合成地图）──────────────────────────────────────────────────

        private static NavKernel Synth(int wChunks, int hChunks, Func<int, int, bool> blocked, NavConfig? cfgOverride = null)
        {
            NavConfig cfg = cfgOverride ?? NavService.ConfigFromTuning();
            var k = new NavKernel(cfg, NavService.TerrainTable(), false, default, null, null);
            PushSynth(k, wChunks, hChunks, blocked, false);
            return k;
        }

        private static void PushSynth(NavKernel k, int wChunks, int hChunks, Func<int, int, bool> blocked, bool asChange)
        {
            int size = k.Config.ChunkSize;
            byte cliff = GridContent.TerrainCode("cliff");
            var terrain = new byte[size * size];
            var occ = new int[size * size];
            for (int cy = 0; cy < hChunks; cy++)
            {
                for (int cx = 0; cx < wChunks; cx++)
                {
                    for (int i = 0; i < size * size; i++)
                    {
                        int x = cx * size + i % size;
                        int y = cy * size + i / size;
                        terrain[i] = blocked(x, y) ? cliff : (byte)0;
                    }
                    k.PushChunk(cx, cy, terrain, occ, null, 0, asChange);
                }
            }
        }

        private static NavRequest Req(int sx, int sy, int gx, int gy, byte cls = NavConst.ClassPlayer, bool partial = false, int key = 1, int serial = 1) => new NavRequest
        {
            OwnerTag = 9,
            OwnerKey = key,
            Serial = serial,
            Class = cls,
            Flags = partial ? NavRequestFlags.AllowPartial : NavRequestFlags.None,
            Start = new int2(sx, sy),
            Goal = new int2(gx, gy),
        };

        private static float PolyLen(int2 start, List<int2> pts)
        {
            float len = 0f;
            int2 prev = start;
            foreach (int2 p in pts)
            {
                len += math.distance((float2)prev, (float2)p);
                prev = p;
            }
            return len;
        }

        private static bool Ring(int x, int y, int cx, int cy, int r) => Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) == r;

        private static void CheckSynthBasics()
        {
            var pts = new List<int2>();
            // 1) 空地：一段直线。
            using (NavKernel k = Synth(2, 1, (x, y) => false))
            {
                NavResult r = k.FindNow(Req(2, 16, 60, 16), pts, false, out _);
                Expect(r.Status == NavStatus.Ok && pts.Count == 1 && pts[0].Equals(new int2(60, 16)) && Mathf.Abs(r.Length - 58f) < 0.01f,
                    $"空地：一段直线直达（{pts.Count} 个路点，长 {r.Length:F1} 格）");
            }
            // 2) 一堵墙只在 y=3～4 留缺口：路线穿过缺口，每一段都不压障碍。
            Func<int, int, bool> wall = (x, y) => x == 32 && (y < 3 || y > 4);
            using (NavKernel k = Synth(2, 1, wall))
            {
                NavResult r = k.FindNow(Req(10, 16, 50, 16), pts, false, out _);
                bool clear = k.RouteClear(new int2(10, 16), pts, 0, NavConst.ClassPlayer);
                bool viaGap = pts.Any(p => p.x >= 31 && p.x <= 33 && p.y >= 2 && p.y <= 5);
                Expect(r.Status == NavStatus.Ok && clear && viaGap && r.Length > 44f && pts.Last().Equals(new int2(50, 16)),
                    $"墙只留一个缺口：绕到缺口通过（{pts.Count} 个路点，长 {r.Length:F1} 格 > 直线 40），每段视线都不穿墙");

                // 3) 目标被悬崖围死：玩家单位失败（原因“被完全阻断”）；敌方允许部分路线：走到最近处。
                Func<int, int, bool> ringed = (x, y) => wall(x, y) || Ring(x, y, 50, 16, 3);
                using (NavKernel k2 = Synth(2, 1, ringed))
                {
                    NavResult fail = k2.FindNow(Req(10, 16, 50, 16), pts, false, out _);
                    NavResult part = k2.FindNow(Req(10, 16, 50, 16, NavConst.ClassHostile, partial: true), pts, false, out _);
                    int2 end = part.End;
                    bool partClear = k2.RouteClear(new int2(10, 16), pts, 0, NavConst.ClassHostile);
                    Expect(fail.Status == NavStatus.Failed && fail.Reason == NavFailReason.Unreachable && fail.PointCount == 0
                           && part.Status == NavStatus.Partial && partClear && Math.Max(Math.Abs(end.x - 50), Math.Abs(end.y - 16)) == 4,
                        $"目标被围死：玩家单位失败（{fail.Reason}），敌方部分路线停在围墙外一圈（终点 {end}，离目标 4 格）");
                }
            }
            // 4) 目标压在障碍上：就近落到可走格；起点压在障碍上：先走出来。
            Func<int, int, bool> blob = (x, y) => Math.Abs(x - 40) <= 1 && Math.Abs(y - 16) <= 1;
            using (NavKernel k = Synth(2, 1, blob))
            {
                NavResult r = k.FindNow(Req(10, 16, 40, 16), pts, false, out _);
                int2 end = r.End;
                NavResult r2 = k.FindNow(Req(40, 16, 10, 16), pts, false, out _);
                Expect(r.Status == NavStatus.Ok && !end.Equals(new int2(40, 16)) && math.distance((float2)end, new float2(40, 16)) <= 2.01f
                       && r2.Status == NavStatus.Ok && pts.Count >= 1 && math.distance((float2)pts[0], new float2(40, 16)) <= 2.01f,
                    $"目标压在障碍上：落到最近的可走格 {end}；起点在障碍里：第一个路点是最近的可走格 {(pts.Count > 0 ? pts[0].ToString() : "无")}，从那里出发");
            }
            // 5) 起点 / 目标周围一大片都走不了；越界；地形未知；超出搜索范围。
            Func<int, int, bool> bigBlob = (x, y) => Math.Abs(x - 16) <= 9 && Math.Abs(y - 16) <= 9;
            using (NavKernel k = Synth(2, 1, bigBlob))
            {
                NavResult sb = k.FindNow(Req(16, 16, 50, 16), pts, false, out _);
                NavResult gb = k.FindNow(Req(50, 16, 16, 16), pts, false, out _);
                NavResult ow = k.FindNow(Req(50, 16, 1_000_010, 16), pts, false, out _);
                NavResult uk = k.FindNow(Req(50, 16, 200, 16), pts, false, out _);
                Expect(sb.Reason == NavFailReason.StartBlocked && gb.Reason == NavFailReason.GoalBlocked && ow.Reason == NavFailReason.OutOfWorld
                       && uk.Reason == NavFailReason.UnknownTerrain && sb.Status == NavStatus.Failed,
                    $"失败原因分得清：起点被围 → {sb.Reason}；目标处无处可站 → {gb.Reason}；越过世界边界 → {ow.Reason}；地形未知 → {uk.Reason}（B06 各有文本键）");
            }
            NavConfig tiny = NavService.ConfigFromTuning();
            tiny.MaxExpansions = 4;
            Func<int, int, bool> comb = (x, y) => (x % 16 == 8) && (y % 64 != (x / 16 % 2 == 0 ? 2 : 61));
            using (NavKernel k = Synth(8, 2, comb, tiny))
            {
                NavResult sl = k.FindNow(Req(2, 10, 250, 10), pts, false, out _);
                Expect(sl.Status == NavStatus.Failed && sl.Reason == NavFailReason.SearchLimit,
                    $"展开节点数超过上限：失败原因“超出寻路范围”（{sl.Reason}，展开 {sl.Expanded}）");
            }
        }

        /// <summary>随机障碍地图（确定性）：若干矩形块 + 两堵长墙。</summary>
        private static Func<int, int, bool> RandomMap(int seed, int w, int h, int blocks)
        {
            var rnd = new System.Random(seed);
            var rects = new List<(int x0, int y0, int x1, int y1)>();
            for (int i = 0; i < blocks; i++)
            {
                int x0 = rnd.Next(0, w);
                int y0 = rnd.Next(0, h);
                rects.Add((x0, y0, x0 + rnd.Next(1, 12), y0 + rnd.Next(1, 12)));
            }
            int wallY = h / 3;
            int wallX = w * 2 / 3;
            return (x, y) =>
            {
                if (y == wallY && x > 5 && x < w - 20)
                {
                    return true;
                }
                if (x == wallX && y > 20 && y < h - 5)
                {
                    return true;
                }
                foreach ((int x0, int y0, int x1, int y1) in rects)
                {
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                    {
                        return true;
                    }
                }
                return false;
            };
        }

        /// <summary>整张合成地图上的 8 向 Dijkstra（斜走不切角，代价与内核相同的整数代价）：可达性与最优长度的对照组。</summary>
        private static bool BruteForce(Func<int, int, bool> blocked, int w, int h, int2 s, int2 t, out float length)
        {
            length = 0f;
            var dist = new int[w * h];
            var par = new int[w * h];
            for (int i = 0; i < dist.Length; i++)
            {
                dist[i] = int.MaxValue;
                par[i] = -1;
            }
            bool Open(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && !blocked(x, y);
            if (!Open(s.x, s.y) || !Open(t.x, t.y))
            {
                return false;
            }
            var open = new SortedSet<(int d, int i)>();
            int si = s.y * w + s.x;
            dist[si] = 0;
            open.Add((0, si));
            int ti = t.y * w + t.x;
            while (open.Count > 0)
            {
                (int d, int i) = open.Min;
                open.Remove(open.Min);
                if (i == ti)
                {
                    break;
                }
                int x = i % w;
                int y = i / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 dir = NavSearch.Dir8(k);
                    int nx = x + dir.x;
                    int ny = y + dir.y;
                    if (!Open(nx, ny))
                    {
                        continue;
                    }
                    bool diag = k >= 4;
                    if (diag && (!Open(nx, y) || !Open(x, ny)))
                    {
                        continue;
                    }
                    int j = ny * w + nx;
                    int nd = d + (diag ? 14 : 10);
                    if (nd < dist[j])
                    {
                        if (dist[j] != int.MaxValue)
                        {
                            open.Remove((dist[j], j));
                        }
                        dist[j] = nd;
                        par[j] = i;
                        open.Add((nd, j));
                    }
                }
            }
            if (dist[ti] == int.MaxValue)
            {
                return false;
            }
            length = dist[ti] / 10f;
            return true;
        }

        private static void CheckAgainstBruteForce()
        {
            const int wc = 4;
            const int hc = 4;
            int w = wc * 32;
            int h = hc * 32;
            Func<int, int, bool> randomMap = RandomMap(4242, w, h, 70);
            // 随机障碍不一定围出封闭区域：加一圈封死的悬崖围栏，保证对照里有“真不可达”的起终点对。
            const int penX = 20;
            const int penY = 100;
            const int penR = 6;
            Func<int, int, bool> map = (x, y) => randomMap(x, y) || Ring(x, y, penX, penY, penR);
            using NavKernel k = Synth(wc, hc, map);
            var rnd = new System.Random(99);
            var pts = new List<int2>();
            int agree = 0;
            int reachable = 0;
            int unreachable = 0;
            int allClear = 0;
            double worstRatio = 0;
            double sumRatio = 0;
            const int randomPairs = 160;
            var pairList = new List<(int2 s, int2 t)>();
            while (pairList.Count < randomPairs)
            {
                var s = new int2(rnd.Next(0, w), rnd.Next(0, h));
                var t = new int2(rnd.Next(0, w), rnd.Next(0, h));
                if (!map(s.x, s.y) && !map(t.x, t.y))
                {
                    pairList.Add((s, t));
                }
            }
            var inside = new List<int2>();
            for (int y = penY - penR + 1; y < penY + penR; y++)
            {
                for (int x = penX - penR + 1; x < penX + penR; x++)
                {
                    if (!map(x, y))
                    {
                        inside.Add(new int2(x, y));
                    }
                }
            }
            if (inside.Count < 2)
            {
                Fail($"暴力对照：围栏里只剩 {inside.Count} 格空地，凑不出不可达对");
                return;
            }
            // 强制对：起点在围栏外、目标在里面（不可达，两个方向各 4 条）；起终点都在里面（可达）。
            for (int i = 0; i < 4; i++)
            {
                int2 outside = pairList[i * 7].s;
                int2 pen = inside[(i * 5) % inside.Count];
                pairList.Add((outside, pen));
                pairList.Add((pen, outside));
            }
            pairList.Add((inside[0], inside[inside.Count - 1]));
            int pairs = pairList.Count;
            foreach ((int2 s, int2 t) in pairList)
            {
                bool bf = BruteForce(map, w, h, s, t, out float best);
                NavResult r = k.FindNow(Req(s.x, s.y, t.x, t.y), pts, false, out _);
                bool hpa = r.Status == NavStatus.Ok;
                if (bf == hpa)
                {
                    agree++;
                }
                if (hpa)
                {
                    reachable++;
                    if (k.RouteClear(s, pts, 0, NavConst.ClassPlayer) && (pts.Count == 0 || pts.Last().Equals(t)))
                    {
                        allClear++;
                    }
                    if (bf && best > 0.5f)
                    {
                        double ratio = r.Length / best;
                        worstRatio = Math.Max(worstRatio, ratio);
                        sumRatio += ratio;
                    }
                }
                else
                {
                    unreachable++;
                }
            }
            double avg = reachable > 0 ? sumRatio / reachable : 0;
            Expect(agree == pairs && allClear == reachable && reachable > 20 && unreachable > 0 && worstRatio <= 1.2 && avg <= 1.06,
                $"与整图暴力 Dijkstra 对照（128×128 随机障碍 + 一圈封死的围栏，{pairs} 对起终点）：可达性一致 {agree}/{pairs}（可达 {reachable}、不可达 {unreachable}）；" +
                $"全部路线每段不穿障碍、终点就是目标；路线长度 / 最优格路径长度 平均 {avg:F3}、最差 {worstRatio:F3}（门槛 1.2，拉直后常比格路径更短）");
        }

        private static void CheckCacheIndependence()
        {
            Func<int, int, bool> map = RandomMap(777, 128, 128, 60);
            var rnd = new System.Random(5);
            var reqs = new List<NavRequest>();
            while (reqs.Count < 60)
            {
                var s = new int2(rnd.Next(0, 128), rnd.Next(0, 128));
                var t = new int2(rnd.Next(0, 128), rnd.Next(0, 128));
                if (!map(s.x, s.y) && !map(t.x, t.y))
                {
                    reqs.Add(Req(s.x, s.y, t.x, t.y));
                }
            }
            string Run(NavKernel k, IEnumerable<NavRequest> order)
            {
                var pts = new List<int2>();
                var res = new Dictionary<string, string>();
                foreach (NavRequest r in order)
                {
                    NavResult x = k.FindNow(r, pts, false, out _);
                    res[$"{r.Start}->{r.Goal}"] = $"{x.Status}:{x.Reason}:{string.Join(";", pts)}";
                }
                return string.Join("|", res.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            }
            using NavKernel warm = Synth(4, 4, map);
            string a = Run(warm, reqs);
            string b = Run(warm, reqs);
            warm.ResetWorkGraphsForTests();
            string c = Run(warm, reqs.AsEnumerable().Reverse());
            using NavKernel fresh = Synth(4, 4, map);
            string d = Run(fresh, reqs.AsEnumerable().Reverse());
            Expect(a == b && a == c && a == d,
                "同一格网上：热缓存、清空抽象图缓存后倒序、全新内核倒序，60 条请求逐格相同（并列按格坐标决胜，与节点构建顺序无关 → 读档接着跑与不存档一致）");
        }

        private static void CheckSharing()
        {
            Func<int, int, bool> wall = (x, y) => x == 64 && (y < 40 || y > 44);
            using NavKernel k = Synth(4, 2, wall);
            NavCounters before = k.Counters;
            for (int i = 0; i < 40; i++)
            {
                k.Enqueue(Req(4 + i % 20, 4 + i / 20 * 3 + i % 7, 110, 50, key: i + 1, serial: 1));
            }
            k.TrySchedule(0);
            k.BeginAdoption();
            int ok = 0;
            int shared = 0;
            int valid = 0;
            for (int i = 0; i < k.ResultCount; i++)
            {
                NavResult r = k.Result(i);
                ok += r.Status == NavStatus.Ok && r.End.Equals(new int2(110, 50)) ? 1 : 0;
                shared += r.Shared;
                valid += k.ResultValid(i) ? 1 : 0;
            }
            int results = k.ResultCount;
            k.EndAdoption();
            NavCounters after = k.Counters;
            long searches = after.Searches - before.Searches;
            Expect(results == 40 && ok == 40 && valid == 40 && shared >= 35 && searches <= 5,
                $"同一目标的 40 个单位（同一起点区块）一批处理：40 条都到达目标并通过采纳校验，{shared} 条复用了代表路线，实际抽象搜索只有 {searches} 次（FGR-ARC-015 共享路线）");
        }

        private static void CheckIncrementalAndRequeue()
        {
            bool gapOpen = true;
            Func<int, int, bool> wall = (x, y) => x == 32 && (!gapOpen || y < 3 || y > 4);
            using NavKernel k = Synth(2, 1, wall);
            k.Enqueue(Req(10, 16, 50, 16, key: 1, serial: 1));
            k.TrySchedule(0);
            // 批次在飞时关掉缺口：算出来的路线在采纳时校验不过 → 原请求重新排队（不会把穿墙的路线交出去）。
            gapOpen = false;
            PushSynth(k, 2, 1, wall, asChange: true);
            bool due = k.IsDue(6) && !k.IsDue(5);
            k.BeginAdoption();
            bool invalid = k.ResultCount == 1 && k.Result(0).Status == NavStatus.Ok && !k.ResultValid(0);
            long requeued = k.Requeued;
            int queued = k.QueueCount;
            k.EndAdoption();
            k.TrySchedule(6);
            k.BeginAdoption();
            NavResult blocked = k.Result(0);
            k.EndAdoption();
            // 重新开口：再次请求能找到路（区块图增量失效重建）。
            gapOpen = true;
            PushSynth(k, 2, 1, wall, asChange: true);
            k.Enqueue(Req(10, 16, 50, 16, key: 1, serial: 2));
            k.TrySchedule(12);
            k.BeginAdoption();
            NavResult reopened = k.Result(0);
            bool clear = k.ResultValid(0);
            k.EndAdoption();
            Expect(due && invalid && requeued == 1 && queued == 1 && blocked.Status == NavStatus.Failed && blocked.Reason == NavFailReason.Unreachable
                   && reopened.Status == NavStatus.Ok && clear && k.ChangedCount > 0,
                $"固定延迟：第 6 步才采纳（第 5 步不采纳）；批次在飞时缺口被堵上 → 采纳校验不过、原请求重新排队（{requeued} 条）→ 下一批按新地形判“被完全阻断”；" +
                "缺口重新打开 → 区块图增量失效，再请求又能找到路");
        }

        /// <summary>云端复审修复（2026-09-27）：出框绕路不再误报阻断、直线检测不越过终点、在飞期间地形变化后失败结果重算一次。</summary>
        private static void CheckDetourAndRecheck()
        {
            var pts = new List<int2>();
            int cs = NavService.ConfigFromTuning().ChunkSize;

            // 1) 一堵长墙只在第 13 排区块开口：起终点区块外接矩形 + 边距的框里没有路，要绕出框。
            int openRow = 13 * cs;
            Func<int, int, bool> longWall = (x, y) => x == cs + cs / 2 && y < openRow;
            using (NavKernel k = Synth(3, 14, longWall))
            {
                NavResult r = k.FindNow(Req(10, 16, 3 * cs - 10, 16), pts, false, out _);
                bool clear = k.RouteClear(new int2(10, 16), pts, 0, NavConst.ClassPlayer);
                bool around = pts.Any(p => p.y >= openRow);
                Expect(r.Status == NavStatus.Ok && clear && around,
                    $"绕路要走出搜索框（长墙只在 y ≥ {openRow} 开口）：放大边距重搜后找到路（{r.Status}，长 {r.Length:F0} 格），不误报“被完全阻断”");
            }

            // 2) 直线检测到终点就停：正好 45° 或起终点同格时，不检查终点外侧的格子。
            using (NavKernel k = Synth(1, 1, (x, y) => (x == 11 && y == 10) || (x == 10 && y == 11)))
            using (NavKernel k2 = Synth(1, 1, (x, y) => (x == 9 && y == 10) || (x == 10 && y == 9)))
            {
                bool diag = k.RouteClear(new int2(5, 5), new List<int2> { new int2(10, 10) }, 0, NavConst.ClassPlayer);
                bool same = k2.RouteClear(new int2(10, 10), new List<int2> { new int2(10, 10) }, 0, NavConst.ClassPlayer);
                Expect(diag && same, "直线检测：45° 线段终点外侧有障碍、单位正站在路点格上且旁边有障碍，路线都判为畅通（不误判被挡）");
            }

            // 3) 批次在飞时缺口被打开：旧副本上算出的“无法到达”不交出，重算一次后给出通路。
            bool gapOpen = false;
            bool toggle = false;
            Func<int, int, bool> wall = (x, y) => (x == 32 && (!gapOpen || y < 3 || y > 4)) || (toggle && x == 5 && y == 30);
            using (NavKernel k = Synth(2, 1, wall))
            {
                k.Enqueue(Req(10, 16, 50, 16, key: 1, serial: 1));
                k.TrySchedule(0);
                gapOpen = true;
                PushSynth(k, 2, 1, wall, asChange: true);
                k.BeginAdoption();
                bool held = k.ResultCount == 1 && k.Result(0).Status == NavStatus.Failed && !k.ResultValid(0) && k.QueueCount == 1;
                k.EndAdoption();
                k.TrySchedule(6);
                k.BeginAdoption();
                bool reached = k.ResultCount == 1 && k.Result(0).Status == NavStatus.Ok && k.ResultValid(0);
                k.EndAdoption();

                // 4) 地形一直在变也不会让单位永远等：同一请求的失败只重算一次，第二次如实交出。
                gapOpen = false;
                PushSynth(k, 2, 1, wall, asChange: true);
                k.Enqueue(Req(10, 16, 50, 16, key: 1, serial: 2));
                k.TrySchedule(12);
                toggle = !toggle;
                PushSynth(k, 2, 1, wall, asChange: true);
                k.BeginAdoption();
                bool firstHeld = k.ResultCount == 1 && !k.ResultValid(0);
                k.EndAdoption();
                k.TrySchedule(18);
                toggle = !toggle;
                PushSynth(k, 2, 1, wall, asChange: true);
                k.BeginAdoption();
                NavResult last = k.Result(0);
                bool delivered = k.ResultCount == 1 && last.Status == NavStatus.Failed && last.Reason == NavFailReason.Unreachable && k.ResultValid(0);
                k.EndAdoption();
                Expect(held && reached && firstHeld && delivered,
                    "批次在飞时屏障被拆：旧地形上的“无法到达”不交出、重算后给出通路；地形持续变化时同一请求只重算一次，第二次如实报“被完全阻断”");
            }
        }

        private static void CheckIslandUnreachable()
        {
            // 湖心岛：起点所在的陆地很大、伸出搜索框（前向搜索“被框挡住”），目标在一圈 4 格宽的水域（悬崖码）中间的小岛上；
            // 岛跨区块边界，目标能走到本区块入口（旧的“起终点被困在自己区块里”判定兜不住）。放大边距之前从目标一侧泛洪，
            // 判定岛自成封闭 → 如实报“被完全阻断”，一轮就结束。展开上限压到 700：不做封闭判定、照常重搜三轮会超，被报成“超出寻路范围”。
            NavConfig cfg = NavService.ConfigFromTuning();
            cfg.MaxExpansions = 700;
            Func<int, int, bool> lake = (x, y) =>
            {
                int d = Math.Max(Math.Abs(x - 128), Math.Abs(y - 48));
                return d >= 7 && d <= 10;
            };
            using NavKernel k = Synth(16, 3, lake, cfg);
            var pts = new List<int2>();
            long flood0 = k.Counters.FloodVisited;
            long exp0 = k.Counters.Expanded;
            NavResult island = k.FindNow(Req(40, 48, 128, 48), pts, false, out _);
            long flooded = k.Counters.FloodVisited - flood0;
            long expanded = k.Counters.Expanded - exp0;
            // 对照：同一片陆地上绕过湖的远点照样能到（封闭判定没有误伤可达的目标）。
            NavResult shore = k.FindNow(Req(40, 48, 300, 48), pts, false, out _);
            bool shoreClear = shore.Status == NavStatus.Ok && k.RouteClear(new int2(40, 48), pts, 0, NavConst.ClassPlayer) && pts.Last().Equals(new int2(300, 48));
            Expect(island.Status == NavStatus.Failed && island.Reason == NavFailReason.Unreachable && island.Expanded < cfg.MaxExpansions && shoreClear,
                $"湖心岛：目标在水域围住的小岛上 → 如实报“被完全阻断”（{island.Reason}，展开 {island.Expanded} < 上限 {cfg.MaxExpansions}，没有因为重搜累计被报成“超出寻路范围”）；" +
                $"同一片陆地上绕过湖的 260 格外的点照样能到（{shore.Status}，每段不穿障碍）");
            // FG3-LOG-09（DEBT-FG0ARCH06-11 ①）：封闭判定的泛洪计入展开数与计数器（受 nav.max_expansions 总量约束，性能基线看得到）。
            Expect(flooded > 0 && island.Expanded >= flooded && expanded == island.Expanded,
                $"湖心岛：封闭判定泛洪访问 {flooded} 个抽象节点，已计入这条请求的展开数 {island.Expanded}（计数器 Expanded 增加 {expanded}、FloodVisited 增加 {flooded}；DEBT-FG0ARCH06-11 ①）");
        }

        /// <summary>
        /// FG3-LOG-09（DEBT-FG0ARCH06-11 ②）：单位恰好站在新障碍里时，路线检查只豁免“走出来”的那几格，从脚下第一个可走格起照常检查——
        /// 同一段前方的新障碍这次就判“被挡”触发重规划（修复前整段跳过，要等撞上后靠卡住检测）。
        /// </summary>
        private static void CheckRouteClearAfterExit()
        {
            // 起点 (10,10) 在一块 3×3 的障碍里（新建筑刚好压在脚下），路线直线向东到 (40,10)。
            Func<int, int, bool> start = (x, y) => Math.Abs(x - 10) <= 1 && Math.Abs(y - 10) <= 1;
            using NavKernel clear = Synth(2, 2, start);
            using NavKernel ahead = Synth(2, 2, (x, y) => start(x, y) || (x == 25 && y == 10));
            using NavKernel onlyInside = Synth(2, 2, (x, y) => start(x, y) || (x == 12 && y == 10));
            var pts = new List<int2> { new int2(40, 10) };
            bool a = clear.RouteClear(new int2(10, 10), pts, 0, NavConst.ClassPlayer);
            bool b = ahead.RouteClear(new int2(10, 10), pts, 0, NavConst.ClassPlayer);
            bool c = onlyInside.RouteClear(new int2(10, 10), pts, 0, NavConst.ClassPlayer);
            // 第二段照旧全程检查：起点不在障碍里时，第一段前方的障碍一样判挡。
            using NavKernel plain = Synth(2, 2, (x, y) => x == 25 && y == 10);
            bool d = plain.RouteClear(new int2(10, 10), pts, 0, NavConst.ClassPlayer);
            Expect(a && !b && c && !d,
                $"路线检查从脚下第一个可走格起查（DEBT-FG0ARCH06-11 ②）：起点在障碍里、前方同一段没有障碍 → 畅通 {a}；同一段前方 15 格有新障碍 → 被挡 {!b}（修复前整段跳过判畅通）；" +
                $"障碍只是起点所在那块的延伸（还没走出来）→ 畅通 {c}；起点不在障碍里时前方障碍照样被挡 {!d}");
        }

        private static void CheckPendingSnapshot()
        {
            Func<int, int, bool> map = RandomMap(31, 64, 64, 20);
            using NavKernel k = Synth(2, 2, map);
            k.Enqueue(Req(2, 2, 60, 60, key: 1, serial: 1));
            k.Enqueue(Req(3, 60, 60, 3, key: 2, serial: 7));
            k.TrySchedule(100);
            k.Enqueue(Req(5, 5, 50, 50, key: 3, serial: 9));
            k.MarkChanged(NavGridOps.Key(1, 1));
            byte[] snap = k.SerializePending();
            ulong h = k.PendingHash();
            using NavKernel k2 = Synth(2, 2, map);
            NavLoadResult lr = k2.LoadPending(snap);
            ulong h2 = k2.PendingHash();
            bool sameDue = k2.BatchActive && k2.DueTick == k.DueTick && k2.QueueCount == 1 && k2.ChangedCount == 1;
            // 两边在采纳步采纳：结果逐条相同。
            k.BeginAdoption();
            k2.BeginAdoption();
            bool same = k.ResultCount == k2.ResultCount;
            for (int i = 0; same && i < k.ResultCount; i++)
            {
                NavResult a = k.Result(i);
                NavResult b = k2.Result(i);
                same = a.Status == b.Status && a.PointCount == b.PointCount && a.End.Equals(b.End) && a.Request.Serial == b.Request.Serial;
                for (int p = 0; same && p < a.PointCount; p++)
                {
                    same = k.ResultPoint(a.PointStart + p).Equals(k2.ResultPoint(b.PointStart + p));
                }
            }
            k.EndAdoption();
            k2.EndAdoption();
            byte[] flipped = (byte[])snap.Clone();
            flipped[flipped.Length / 2] ^= 0x5A;
            byte[] future = (byte[])snap.Clone();
            future[4] = 99;
            byte[] truncated = snap.Take(snap.Length / 2).ToArray();
            using NavKernel k3 = Synth(2, 2, map);
            NavLoadResult r1 = k3.LoadPending(flipped);
            NavLoadResult r2 = k3.LoadPending(future);
            NavLoadResult r3 = k3.LoadPending(truncated);
            NavLoadResult r4 = k3.LoadPending(new byte[] { 1, 2, 3 });
            bool untouched = k3.QueueCount == 0 && !k3.BatchActive;
            Expect(lr == NavLoadResult.Ok && h == h2 && sameDue && same && r1 == NavLoadResult.BadChecksum && r2 == NavLoadResult.UnknownFormat
                   && (r3 == NavLoadResult.BadChecksum || r3 == NavLoadResult.Truncated) && r4 == NavLoadResult.BadMagic && untouched && NavKernel.PeekFormat(future) == 99,
                $"待处理状态快照：排队请求、在飞批次（强制完成后带结果与采纳步）、变化区块往返一致（哈希 {h:X16}），读回的一边在同一采纳步交出逐格相同的结果；" +
                $"负向：翻一个字节 → {r1}，不认识的版本 → {r2}，截断 → {r3}，乱码 → {r4}，都不改动内核");
        }

        // ── C 战斗内核接入 ──────────────────────────────────────────────────────────

        private sealed class Pipe : IDisposable
        {
            public NavKernel Nav;
            public CombatKernel K;
            public long Tick;
            public double T;
            public readonly List<CombatEvent> Events = new List<CombatEvent>();

            public Pipe(NavKernel nav, float separation = 0.5f)
            {
                Nav = nav;
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 1;
                cfg.SeparationFactor = separation;
                K = new CombatKernel(cfg, 64);
                K.BindNav(nav.Mirror);
            }

            /// <summary>与 NavService.BeginStep 同一顺序：采纳 → 失效检查 → 收集 → 调度，然后内核一步。</summary>
            public void Step()
            {
                if (Nav.IsDue(Tick))
                {
                    Nav.BeginAdoption();
                    Nav.DeliverCombat(K, 1);
                    Nav.EndAdoption();
                }
                if (Nav.ChangedCount > 0)
                {
                    K.InvalidateBlockedRoutes();
                    Nav.ClearChanged();
                }
                Nav.CollectFromCombat(K, 1, Tick);
                Nav.TrySchedule(Tick);
                K.Step(Dt, T);
                CombatEvent[] ev = K.DrainGameplay(1 << 20, out int n);
                for (int i = 0; i < n; i++)
                {
                    Events.Add(ev[i]);
                }
                K.ClearCues();
                T += Dt;
                Tick++;
            }

            public void Dispose()
            {
                K.UnbindNav();
                K.Dispose();
            }
        }

        private static int Machine(CombatKernel k, double2 pos, float speed = 6f) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = CombatUnitKind.Machine,
            Faction = CombatFaction.Player,
            Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.ExternalHealth,
            Position = pos,
            Home = pos,
            Radius = 0.9f,
            Speed = speed,
            Health = 100f,
            MaxHealth = 100f,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
            ArmorHalfAngleDeg = 90f,
            ArmorFacing = new float2(0f, -1f),
        });

        private static bool UnitCellOk(NavKernel nav, CombatKernel k, int id, int cls)
        {
            k.TryGetPosition(id, out double2 p);
            int2 c = CombatLogic.CellOf(p);
            return nav.Passable(c.x, c.y, cls);
        }

        private static void CheckCombatFollow()
        {
            Func<int, int, bool> wall = (x, y) => x == 32 && (y < 3 || y > 4);
            using NavKernel nav = Synth(2, 1, wall);
            using var pipe = new Pipe(nav);
            int m = Machine(pipe.K, new double2(10, 16));
            var target = new double2(50.3, 16.4);
            pipe.K.IssueCommand(m, CombatCommandKind.WorkMove, target, 0, 1.2f, 0f, 0f, false);
            bool neverBlocked = true;
            int arrivedAt = -1;
            bool awaited = false;
            for (int i = 0; i < 1200 && arrivedAt < 0; i++)
            {
                pipe.Step();
                neverBlocked &= UnitCellOk(nav, pipe.K, m, NavConst.ClassPlayer);
                if (pipe.K.TryGetNavState(m, out CombatNavState st, out _) && st == CombatNavState.Awaiting && i < 6)
                {
                    awaited = true;
                }
                if (pipe.Events.Any(e => e.Kind == CombatEventKind.WorkArrived))
                {
                    arrivedAt = i;
                }
            }
            pipe.K.TryGetPosition(m, out double2 finalPos);
            Expect(awaited && neverBlocked && arrivedAt > 0 && finalPos.Equals(target) && arrivedAt < 60 * 9,
                $"工作赶路走寻路路线：先原地等固定延迟（不走直线），拿到路线后绕到缺口，全程每一步都在可通行格上，第 {arrivedAt} 步到达并对齐到目标点（DEBT-FG0ARCH04-04：机器不再穿墙）");

            // 目标被围死：工作赶路以“无法到达”事件结束（带原因），编队移动命令以“无法到达”结束、事件位置 = 命令目标点；单位不走直线去撞墙。
            pipe.Events.Clear();
            Func<int, int, bool> ringed = (x, y) => wall(x, y) || Ring(x, y, 50, 24, 3);
            PushSynth(nav, 2, 1, ringed, asChange: true);
            pipe.K.TryGetPosition(m, out double2 before);
            pipe.K.IssueCommand(m, CombatCommandKind.WorkMove, new double2(50, 24), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 60; i++)
            {
                pipe.Step();
            }
            CombatEvent wb = pipe.Events.FirstOrDefault(e => e.Kind == CombatEventKind.WorkBlocked);
            pipe.K.TryGetPosition(m, out double2 after);
            pipe.Events.Clear();
            pipe.K.IssueCommand(m, CombatCommandKind.Move, new double2(50, 24), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 60; i++)
            {
                pipe.Step();
            }
            CombatEvent ce = pipe.Events.FirstOrDefault(e => e.Kind == CombatEventKind.CommandEnded);
            bool noCmd = !pipe.K.TryGetCommand(m, out _);
            Expect(wb.Kind == CombatEventKind.WorkBlocked && (NavFailReason)wb.Code == NavFailReason.Unreachable && math.distance(before, after) < 1e-9
                   && ce.Kind == CombatEventKind.CommandEnded && (CombatEndReason)ce.Code == CombatEndReason.Unreachable
                   && (NavFailReason)(int)ce.Value == NavFailReason.Unreachable && math.distance(ce.Pos, new double2(50, 24)) < 1e-6 && noCmd,
                "目标被完全阻断：工作赶路发“无法到达”事件（原因 Unreachable），编队移动以“无法到达”结束（事件位置 = 目标点，供通知定位）；单位原地不动、不去撞墙，命令清空不挂着");

            // 直控撞墙：沿 +x 顶墙不越过；斜着顶墙时贴墙滑动。
            using var pipe2 = new Pipe(nav);
            int d = Machine(pipe2.K, new double2(28, 16));
            pipe2.K.SetFlag(d, CombatUnitFlags.Possessed, true);
            pipe2.K.SetDirectInput(d, new float2(1f, 0f));
            double maxX = 0;
            for (int i = 0; i < 120; i++)
            {
                pipe2.Step();
                pipe2.K.TryGetPosition(d, out double2 p);
                maxX = Math.Max(maxX, p.x);
            }
            pipe2.K.SetDirectInput(d, new float2(1f, 1f));
            pipe2.K.TryGetPosition(d, out double2 slideFrom);
            for (int i = 0; i < 60; i++)
            {
                pipe2.Step();
            }
            pipe2.K.TryGetPosition(d, out double2 slideTo);
            Expect(maxX < 31.5 && slideTo.y > slideFrom.y + 3 && slideTo.x < 31.5,
                $"直控：顶着悬崖走不过去（x 最大 {maxX:F2} < 31.5），斜着顶墙时贴墙滑动（y {slideFrom.y:F1} → {slideTo.y:F1}）");
        }

        private static void CheckCombatRetarget()
        {
            foreach (CombatCommandKind kind in new[] { CombatCommandKind.Move, CombatCommandKind.WorkMove, CombatCommandKind.Retreat, CombatCommandKind.Guard })
            {
                using NavKernel nav = Synth(2, 1, (x, y) => false);
                using var pipe = new Pipe(nav, 0f);
                int m = Machine(pipe.K, new double2(10, 10));
                pipe.K.IssueCommand(m, kind, new double2(50, 10), 0, 1.2f, 0f, 0f, false);
                pipe.Step();
                pipe.K.TryGetPosition(m, out double2 initial);
                bool initialWait = initial.Equals(new double2(10, 10));
                for (int i = 0; i < 29; i++)
                {
                    pipe.Step();
                }
                pipe.K.TryGetNavState(m, out CombatNavState before, out _);
                var target = new double2(50, 24);
                pipe.K.IssueCommand(m, kind, target, 0, 1.2f, 0f, 0f, false);
                int waitingSteps = 0;
                double minMove = double.MaxValue;
                bool clear = true;
                for (int i = 0; i < 30; i++)
                {
                    pipe.K.TryGetPosition(m, out double2 prev);
                    pipe.Step();
                    pipe.K.TryGetPosition(m, out double2 now);
                    clear &= UnitCellOk(nav, pipe.K, m, NavConst.ClassPlayer);
                    pipe.K.TryGetNavState(m, out CombatNavState st, out _);
                    if (st != CombatNavState.Awaiting)
                    {
                        break;
                    }
                    waitingSteps++;
                    minMove = Math.Min(minMove, math.distance(prev, now));
                }
                for (int i = 0; i < 600; i++)
                {
                    pipe.Step();
                    clear &= UnitCellOk(nav, pipe.K, m, NavConst.ClassPlayer);
                }
                pipe.K.TryGetPosition(m, out double2 end);
                Expect(initialWait && before == CombatNavState.Following && waitingSteps >= nav.Config.LatencySteps
                       && minMove > 0.09 && clear && math.distance(end, target) <= 1.2,
                    $"移动中切换目的地（{kind}）：首次寻路仍原地等；换点等待 {waitingSteps} 步，每步最小位移 {minMove:F3} 米，无停顿且最终到达新目标");
            }

            using NavKernel navA = Synth(2, 1, (x, y) => false);
            using var a = new Pipe(navA, 0f);
            int unit = Machine(a.K, new double2(10, 10));
            a.K.IssueCommand(unit, CombatCommandKind.WorkMove, new double2(50, 10), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 30; i++)
            {
                a.Step();
            }
            double rapidMin = double.MaxValue;
            var lastGoal = new double2(45, 26);
            for (int i = 0; i < 3; i++)
            {
                a.K.TryGetPosition(unit, out double2 prev);
                a.K.IssueCommand(unit, CombatCommandKind.WorkMove, i == 2 ? lastGoal : new double2(40, 18 + i * 3), 0, 1.2f, 0f, 0f, false);
                a.Step();
                a.K.TryGetPosition(unit, out double2 now);
                rapidMin = Math.Min(rapidMin, math.distance(prev, now));
            }
            a.K.TryGetNavState(unit, out CombatNavState awaiting, out _);
            byte[] combatSnap = a.K.Serialize();
            byte[] navSnap = navA.SerializePending();
            using NavKernel navB = Synth(2, 1, (x, y) => false);
            NavLoadResult navLoad = navB.LoadPending(navSnap);
            using var b = new Pipe(navB, 0f);
            CombatLoadResult combatLoad = b.K.Load(combatSnap);
            b.Tick = a.Tick;
            b.T = a.T;
            bool same = a.K.StateHash() == b.K.StateHash();
            for (int i = 0; i < 600; i++)
            {
                a.Step();
                b.Step();
                same &= a.K.StateHash() == b.K.StateHash();
            }
            a.K.TryGetPosition(unit, out double2 final);
            Expect(rapidMin > 0.09 && awaiting == CombatNavState.Awaiting && final.Equals(lastGoal),
                $"连续三次换点：每次当步都移动（最小 {rapidMin:F3} 米），过期结果不覆盖最后目标，最终对齐最后目标");
            Expect(navLoad == NavLoadResult.Ok && combatLoad == CombatLoadResult.Ok && same,
                "换点等待期间保留旧路线的存档：读回后连续 600 步与不存档逐步哈希一致，无需更改存档格式");

            a.K.IssueCommand(unit, CombatCommandKind.Move, new double2(10, 10), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 30; i++)
            {
                a.Step();
            }
            a.K.TryGetPosition(unit, out double2 held);
            a.K.IssueCommand(unit, CombatCommandKind.Guard, new double2(double.NaN, double.NaN), 0, 1.2f, 0f, 0f, false);
            a.Step();
            a.K.IssueCommand(unit, CombatCommandKind.Move, new double2(20, 20), 0, 1.2f, 0f, 0f, false);
            a.Step();
            a.K.TryGetPosition(unit, out double2 afterHold);
            Expect(held.Equals(afterHold), "移动中改为原地守备：立即停住并释放旧路线，后续新移动不会沿守备前的路线空跑");
            for (int i = 0; i < 30; i++)
            {
                a.Step();
            }
            a.K.IssueCommand(unit, CombatCommandKind.Move, new double2(25, 15), 0, 1.2f, 0f, 0f, false);
            a.Step();
            a.K.ClearCommand(unit);
            a.K.TryGetPosition(unit, out double2 cancelled);
            for (int i = 0; i < 30; i++)
            {
                a.Step();
            }
            a.K.TryGetPosition(unit, out double2 afterCancel);
            Expect(cancelled.Equals(afterCancel) && !a.K.TryGetCommand(unit, out _),
                "换点等待期间取消：立即停止，后续旧批次结果不恢复移动");

            a.K.IssueCommand(unit, CombatCommandKind.WorkMove, new double2(10, 10), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 30; i++)
            {
                a.Step();
            }
            var teleportGoal = new double2(15, 15);
            a.K.IssueCommand(unit, CombatCommandKind.WorkMove, teleportGoal, 0, 1.2f, 0f, 0f, false);
            a.Step();
            var teleport = new double2(5, 5);
            a.K.SetPosition(unit, teleport);
            a.Step();
            a.K.TryGetPosition(unit, out double2 afterTeleport);
            for (int i = 0; i < 600; i++)
            {
                a.Step();
            }
            a.K.TryGetPosition(unit, out double2 teleportEnd);
            Expect(afterTeleport.Equals(teleport) && teleportEnd.Equals(teleportGoal),
                "换点等待期间传送：丢弃旧路线与过期结果，从新位置规划并到达最后目标");

            using NavKernel cutNav = Synth(2, 1, (x, y) => false);
            using var cut = new Pipe(cutNav, 0f);
            int cm = Machine(cut.K, new double2(10, 10));
            cut.K.IssueCommand(cm, CombatCommandKind.WorkMove, new double2(50, 10), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 30; i++)
            {
                cut.Step();
            }
            var cutTarget = new double2(8, 24);
            cut.K.IssueCommand(cm, CombatCommandKind.WorkMove, cutTarget, 0, 1.2f, 0f, 0f, false);
            cut.Step();
            PushSynth(cutNav, 2, 1, (x, y) => x == 16, asChange: true);
            cut.K.TryGetPosition(cm, out double2 beforeCut);
            cut.Step();
            cut.K.TryGetPosition(cm, out double2 afterCut);
            cut.K.TryGetNavState(cm, out CombatNavState cutState, out _);
            bool passable = UnitCellOk(cutNav, cut.K, cm, NavConst.ClassPlayer);
            for (int i = 0; i < 300; i++)
            {
                cut.Step();
                passable &= UnitCellOk(cutNav, cut.K, cm, NavConst.ClassPlayer);
            }
            cut.K.TryGetPosition(cm, out double2 cutEnd);
            Expect(beforeCut.Equals(afterCut) && cutState == CombatNavState.Awaiting && passable && cutEnd.Equals(cutTarget),
                "换点等待时旧路线被新障碍截断：丢弃旧路线但保留新请求，全程不穿墙且到达新目标");
        }

        private static void CheckCombatRouteCutAndSnapshot()
        {
            int gap = 3;
            Func<int, int, bool> wall = (x, y) => x == 32 && (y < gap || y > gap + 1);
            using NavKernel nav = Synth(2, 1, wall);
            using var pipe = new Pipe(nav);
            int m = Machine(pipe.K, new double2(10, 10));
            pipe.K.IssueCommand(m, CombatCommandKind.WorkMove, new double2(50, 10), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 40; i++)
            {
                pipe.Step();
            }
            // 路线中途被截断：缺口换到 y=26（原缺口堵上）。
            gap = 26;
            PushSynth(nav, 2, 1, wall, asChange: true);
            bool neverBlocked = true;
            int arrived = -1;
            int reroutes = 0;
            bool sawNeed = false;
            for (int i = 0; i < 1500 && arrived < 0; i++)
            {
                pipe.Step();
                if (pipe.K.TryGetNavState(m, out CombatNavState st, out _) && st == CombatNavState.Awaiting && !sawNeed)
                {
                    sawNeed = true;
                    reroutes++;
                }
                neverBlocked &= UnitCellOk(nav, pipe.K, m, NavConst.ClassPlayer);
                if (pipe.Events.Any(e => e.Kind == CombatEventKind.WorkArrived))
                {
                    arrived = i;
                }
            }
            Expect(sawNeed && neverBlocked && arrived > 0,
                $"路线中途被新障碍截断：剩余路线上的格子变得不可通行 → 从当前位置重新要路线，改走新缺口，第 {arrived} 步到达，全程不踏进不可通行格");

            // 中途存读档：沿路线走到一半存（内核快照 + 寻路待处理状态），读回后接着跑与不存档逐位一致。
            using NavKernel navA = Synth(2, 1, wall);
            using var a = new Pipe(navA);
            int ma = Machine(a.K, new double2(5, 5));
            int mb = Machine(a.K, new double2(6, 20));
            a.K.IssueCommand(ma, CombatCommandKind.WorkMove, new double2(55, 28), 0, 1.2f, 0f, 0f, false);
            for (int i = 0; i < 90; i++)
            {
                a.Step();
            }
            a.K.IssueCommand(mb, CombatCommandKind.Move, new double2(58, 5), 0, 1.2f, 0f, 0f, false);
            a.Step();
            a.Step();
            byte[] kSnap = a.K.Serialize();
            byte[] nSnap = navA.SerializePending();
            long tick = a.Tick;
            double t = a.T;
            bool inFlight = navA.BatchActive;
            for (int i = 0; i < 400; i++)
            {
                a.Step();
            }
            ulong continuous = a.K.StateHash();
            using NavKernel navB = Synth(2, 1, wall);
            navB.LoadPending(nSnap);
            using var b = new Pipe(navB);
            CombatLoadResult lr = b.K.Load(kSnap);
            b.Tick = tick;
            b.T = t;
            for (int i = 0; i < 400; i++)
            {
                b.Step();
            }
            Expect(lr == CombatLoadResult.Ok && inFlight && continuous == b.K.StateHash() && CombatKernel.PeekFormat(kSnap) == CombatConst.FormatVersion,
                $"沿路线走到一半、另有寻路批次在飞时存档（内核快照格式 {CombatConst.FormatVersion}：单位寻路状态与剩余路线 + 待交请求；寻路快照：在飞批次结果与采纳步）：" +
                $"读档接着跑 400 步与不存档逐位一致（{continuous:X16}）");
        }

        private static int Raider(CombatKernel k, double2 pos, double2 goal, int weapon, int profile) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = CombatBehavior.Raider,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.RemoveOnDeath | CombatUnitFlags.Instanced,
            Position = pos,
            Home = goal,
            Radius = 0.6f,
            Speed = 3.5f,
            Health = 100f,
            MaxHealth = 100f,
            Weapon = weapon,
            BehaviorProfile = profile,
            Priority = 1,
            ArmorHalfAngleDeg = 90f,
            ArmorFacing = new float2(0f, -1f),
        });

        private static void CheckSeparation()
        {
            (double minDist, ulong hash, bool clear, double progress) RunOnce(float factor)
            {
                using NavKernel nav = Synth(2, 1, (x, y) => x == 32 && (y < 12 || y > 20));
                using var pipe = new Pipe(nav, factor);
                int w = pipe.K.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 5f, Damage = 1f, Cooldown = 1f });
                int prof = pipe.K.AddProfile(new CombatBehaviorProfile { Speed = 3.5f, SenseRange = 0f });
                var ids = new List<int>();
                for (int i = 0; i < 16; i++)
                {
                    ids.Add(Raider(pipe.K, new double2(50, 16), new double2(8, 16), w, prof));
                }
                bool ok = true;
                for (int s = 0; s < 360; s++)
                {
                    pipe.Step();
                    foreach (int id in ids)
                    {
                        ok &= UnitCellOk(nav, pipe.K, id, NavConst.ClassHostile);
                    }
                }
                double min = double.MaxValue;
                double sumX = 0;
                for (int i = 0; i < ids.Count; i++)
                {
                    pipe.K.TryGetPosition(ids[i], out double2 pi);
                    sumX += pi.x;
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        pipe.K.TryGetPosition(ids[j], out double2 pj);
                        min = Math.Min(min, math.distance(pi, pj));
                    }
                }
                return (min, pipe.K.StateHash(), ok, 50 - sumX / ids.Count);
            }
            var separated = RunOnce(0.5f);
            var again = RunOnce(0.5f);
            var without = RunOnce(0f);
            // 满速上限：速度 3.5 × (360 步 − 等路线的固定延迟) / 60。分离不该把队伍拖慢（至少走到上限的 85%）。
            double maxProgress = 3.5 * (360 - NavService.ConfigFromTuning().LatencySteps) / 60.0;
            Expect(separated.minDist > 0.6 && without.minDist < 0.05 && separated.hash == again.hash && separated.clear && separated.progress > maxProgress * 0.85,
                $"分离（DEBT-FG0ARCH03-03）：16 个突袭者在同一点出生、同一目标，开分离后 6 秒内两两最小间距 {separated.minDist:F2} 米（关掉分离 {without.minDist:F3} 米，叠在一处），" +
                $"平均前进 {separated.progress:F1} 格（满速上限 {maxProgress:F1} 格）、全程不进不可通行格，两次运行哈希一致（确定性）");
        }

        // ── D 家园集成 ──────────────────────────────────────────────────────────────

        private static CampaignState NewCampaign(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgnav-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            return s;
        }

        private static int SpawnHomeMachine(CampaignState s, Vector2 at, bool noAutoWork = true, int settleSteps = 2)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                Fail("登记测试机器失败：" + r.Message);
                return 0;
            }
            // 与正式生成路径一致（工厂出厂 / 进家园时 RegisterAllRegionMachineLoadouts）：登记装配。否则这台机器在内核里是“没武器”，
            // 读档后家园按蓝图补登记、刷新武器，存读档对照就会在武器序号上分叉——那是测试造出了游戏里不存在的状态。
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord spawned) && !string.IsNullOrEmpty(spawned.BlueprintId))
            {
                MachineLoadoutRegistry.Register(s, r.LogicId, spawned.BlueprintId, spawned.BlueprintVersion);
            }
            if (noAutoWork && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                // 只测移动：不让工单引擎把它派去干活（打断测试命令）。
                rec.WorkPriorities ??= WorkPriorities.Default();
                foreach (WorkOrderKind kind in Enum.GetValues(typeof(WorkOrderKind)))
                {
                    rec.WorkPriorities.Set(kind, 0);
                }
            }
            if (settleSteps > 0)
            {
                WorldSimulation.StepMany(settleSteps);
            }
            return r.LogicId;
        }

        private static bool Passable(GridCell c, int cls = NavConst.ClassPlayer) => NavService.Kernel != null && NavService.Kernel.Passable(c.X, c.Y, cls);

        private static GridCell CellOf(double2 p) => NavService.CellOf(p.x, p.y);

        private static bool TryUnitPos(HomeValleyMachineMarker marker, out double2 p)
        {
            p = default;
            return marker != null && marker.Site != null && marker.Site.TryGetUnitPosition(marker.UnitId, out p);
        }

        private static string _openAreaMiss = string.Empty;

        /// <summary>
        /// 找一块全部可走、没有建筑、不压开局障碍（残骸 / 靶 / 出口的净空）、已探索的方形平地（核心周围按环由近及远，确定性）。
        /// 开局只探明核心附近、地形起伏按种子而定，有的种子附近没有够大的天然平地：这时退而找一块已探索、没有建筑与障碍的方形区域，
        /// 把里面的地形整平成空地（测试布景，同 <see cref="CliffRing"/>），不让用例依赖某个种子恰好有平地。
        /// </summary>
        internal static GridCell? FindOpenArea(CampaignState s, int half, int minR, int maxR)
        {
            GridCell? natural = FindOpenAreaCore(s, half, minR, maxR, flatten: false);
            if (natural != null)
            {
                return natural;
            }
            string naturalMiss = _openAreaMiss;
            GridCell? made = FindOpenAreaCore(s, half, minR, maxR, flatten: true);
            if (made == null)
            {
                _openAreaMiss = naturalMiss + "；整平也找不到：" + _openAreaMiss;
            }
            return made;
        }

        private static GridCell? FindOpenAreaCore(CampaignState s, int half, int minR, int maxR, bool flatten)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            var obstacles = new List<(GridCell cell, int radius)>();
            foreach (var row in GridContent.StartLayout)
            {
                if (row.Kind == "wreckage" || row.Kind == "target" || row.Kind == "exit")
                {
                    obstacles.Add((new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY), Mathf.FloorToInt(row.Clearance)));
                }
            }
            int centers = 0;
            int fog = 0;
            int occupied = 0;
            int blocked = 0;
            int rough = 0;
            for (int r = minR; r <= maxR; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        centers++;
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        for (int y = -half; y <= half && ok; y++)
                        {
                            for (int x = -half; x <= half && ok; x++)
                            {
                                var q = new GridCell(c.X + x, c.Y + y);
                                bool explored = map.IsExplored(q);
                                bool free = map.OccupantAt(q) == null
                                            && !obstacles.Any(o => Math.Max(Math.Abs(q.X - o.cell.X), Math.Abs(q.Y - o.cell.Y)) <= o.radius);
                                bool pass = flatten || Passable(q);
                                // 平地 = 可建空地且污染低于不可建等级（FG3-GEN-01 起家园区外圈的污染随种子更常见，只看地形会挑到放不了建筑的空地）。
                                bool plain = flatten || (map.GetTerrain(q) == 0 && map.GetPollution(q) < GridContent.TuningInt("grid.pollution_block_level"));
                                ok = pass && free && explored && plain;
                                if (!ok)
                                {
                                    if (!explored)
                                    {
                                        fog++;
                                    }
                                    else if (!free)
                                    {
                                        occupied++;
                                    }
                                    else if (!pass)
                                    {
                                        blocked++;
                                    }
                                    else
                                    {
                                        rough++;
                                    }
                                }
                            }
                        }
                        if (ok)
                        {
                            if (flatten)
                            {
                                for (int y = -half; y <= half; y++)
                                {
                                    for (int x = -half; x <= half; x++)
                                    {
                                        var q = new GridCell(c.X + x, c.Y + y);
                                        if (map.GetTerrain(q) != 0)
                                        {
                                            map.SetTerrain(q, 0);
                                        }
                                        if (map.GetPollution(q) != 0)
                                        {
                                            map.SetPollution(q, 0);
                                        }
                                    }
                                }
                            }
                            return c;
                        }
                    }
                }
            }
            _openAreaMiss = $"（核心外 {minR}～{maxR} 格的 {centers} 个候选中心：碰到未探索 {fog}、建筑或开局障碍 {occupied}、不可走 {blocked}、非平地 {rough}）";
            return null;
        }

        internal static void CliffRing(CampaignState s, GridCell center, int r, int thickness = 1, Func<int, int, bool> keep = null)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            for (int t = 0; t < thickness; t++)
            {
                int rr = r + t;
                for (int dy = -rr; dy <= rr; dy++)
                {
                    for (int dx = -rr; dx <= rr; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != rr || (keep != null && keep(dx, dy)))
                        {
                            continue;
                        }
                        map.SetTerrain(new GridCell(center.X + dx, center.Y + dy), cliff);
                    }
                }
            }
        }

        private static void ClearRing(CampaignState s, GridCell center, int r, int thickness = 1)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int t = 0; t < thickness; t++)
            {
                int rr = r + t;
                for (int dy = -rr; dy <= rr; dy++)
                {
                    for (int dx = -rr; dx <= rr; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == rr)
                        {
                            map.SetTerrain(new GridCell(center.X + dx, center.Y + dy), 0);
                        }
                    }
                }
            }
        }

        private static void CheckHomeDetourAndCut()
        {
            CampaignState s = NewCampaign(8801);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            // 机器在核心西侧，目标在核心东侧：直线穿过核心占地，必须绕行。
            var start = new Vector2(cmin.X - 3, core.Y);
            var target = new Vector2(cmax.X + 3, core.Y);
            int id = SpawnHomeMachine(s, start);
            home.Combat.TryGetMachineMarker(id, out HomeValleyMachineMarker marker);
            bool arrived = false;
            marker.CommandMoveTo(new Vector3(target.x, 1f, target.y), () => arrived = true);
            bool neverBlocked = true;
            int maxWaypoints = 0;
            var route = new List<double2>();
            int steps = 0;
            for (; steps < 1200 && !arrived; steps++)
            {
                WorldSimulation.StepMany(1);
                if (TryUnitPos(marker, out double2 p))
                {
                    neverBlocked &= Passable(CellOf(p));
                }
                maxWaypoints = Math.Max(maxWaypoints, home.Combat.CopyRoute(marker.UnitId, route));
            }
            TryUnitPos(marker, out double2 fin);
            Expect(arrived && neverBlocked && maxWaypoints >= 2 && math.distance(fin, new double2(target.x, target.y)) < 1e-4,
                $"家园机器绕开核心：直线会穿过核心占地，实际沿 {maxWaypoints} 个路点绕行，第 {steps} 步到达并对齐目标点，全程不踏进建筑 / 悬崖 / 水（DEBT-FG0ARCH04-04）");

            // 路线中途被新建筑截断：赶路途中在前方放一座建筑（正式放置入口），机器重新规划后到达。
            s.Scrap = 5000;
            // 开局只探明核心附近：先找 13×13 的平地，没有就退到 11×11（起终点跟着收一格；3×3 的发电机仍横在正中）。
            int half = 6;
            GridCell? open = FindOpenArea(s, half, 12, 40);
            if (open == null)
            {
                half = 5;
                open = FindOpenArea(s, half, 12, 40);
            }
            if (open == null)
            {
                Fail("找不到空地做“中途截断”" + _openAreaMiss);
                return;
            }
            GridCell o = open.Value;
            marker.SetPosition(new Vector2(o.X - half, o.Y));
            WorldSimulation.StepMany(1);
            arrived = false;
            marker.CommandMoveTo(new Vector3(o.X + half, 1f, o.Y), () => arrived = true);
            WorldSimulation.StepMany(NavService.ConfigFromTuning().LatencySteps + 4);
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", new GridCell(o.X + 1, o.Y), 0);
            long invalidBefore = NavService.Kernel.Invalidated;
            neverBlocked = true;
            steps = 0;
            for (; steps < 1200 && !arrived; steps++)
            {
                WorldSimulation.StepMany(1);
                if (TryUnitPos(marker, out double2 p))
                {
                    neverBlocked &= Passable(CellOf(p));
                }
            }
            Expect(placed.Success && arrived && neverBlocked && NavService.Kernel.Invalidated > invalidBefore,
                $"赶路途中前方新放了一座建筑（{placed.Outcome}）：路线被截断 → 重新规划（失效 {NavService.Kernel.Invalidated - invalidBefore} 次），第 {steps} 步绕过去到达，不穿过新建筑" +
                Why((placed.Success, "放置失败：" + placed.Describe()), (arrived, "没到达"), (neverBlocked, "踏进了不可通行格"), (NavService.Kernel.Invalidated > invalidBefore, "路线没失效重算")));
        }

        private static void CheckCommandDestinationVisual()
        {
            CampaignState s = NewCampaign(8801);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            var start = new Vector2(cmin.X - 3, core.Y);
            var target = new Vector2(cmax.X + 3, core.Y);
            int id = SpawnHomeMachine(s, start);
            if (!home.Combat.TryGetMachineMarker(id, out HomeValleyMachineMarker marker))
            {
                Fail("目的地指示线测试机器未进入战斗内核");
                return;
            }

            home.SetObserved(true);
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.DebugSetReader(new SilentReader());
            try
            {
                home.SquadCommands.SelectSingle(id);
                long clickedAt = GameClock.Ticks;
                home.SquadCommands.IssueMoveTo(target, paused: false);
                home.FrameUpdate(0f, 0f);
                LineRenderer line = GameObject.Find($"SquadCommandPath_{id}")?.GetComponent<LineRenderer>();
                GameObject end = GameObject.Find($"SquadCommandDestination_{id}");
                bool Matches(Vector2 destination)
                {
                    if (line == null || end == null || line.positionCount != 2)
                    {
                        return false;
                    }
                    Vector3 from = line.GetPosition(0);
                    Vector3 to = line.GetPosition(1);
                    Vector3 pin = end.transform.position;
                    return Vector2.Distance(new Vector2(from.x, from.z), marker.Position) < 0.001f
                        && Vector2.Distance(new Vector2(to.x, to.z), destination) < 0.001f
                        && Vector2.Distance(new Vector2(pin.x, pin.z), destination) < 0.001f;
                }
                Expect(GameClock.Ticks == clickedAt && Matches(target),
                    "目的地指示线：命令当帧、尚未推进寻路模拟步时，连线和终点已落在点击目标");

                bool stable = true;
                bool neverBlocked = true;
                int maxWaypoints = 0;
                var route = new List<double2>();
                for (int step = 0; step < 30; step++)
                {
                    WorldSimulation.StepMany(1);
                    home.FrameUpdate(0f, 0f);
                    stable &= Matches(target);
                    neverBlocked &= TryUnitPos(marker, out double2 p) && Passable(CellOf(p));
                    home.Combat.CopyRoute(marker.UnitId, route);
                    maxWaypoints = Math.Max(maxWaypoints, route.Count);
                }
                Expect(stable && neverBlocked && maxWaypoints >= 2 && Vector2.Distance(marker.Position, start) > 0.1f,
                    $"目的地指示线：后台生成绕核心路线（最多 {maxWaypoints} 路点）后线形不二次刷新，起点随机器移动，实际移动未踏入障碍");

                clickedAt = GameClock.Ticks;
                home.SquadCommands.IssueMoveTo(start, paused: false);
                home.FrameUpdate(0f, 0f);
                bool retargeted = GameClock.Ticks == clickedAt && Matches(start);
                for (int step = 0; step < 20; step++)
                {
                    WorldSimulation.StepMany(1);
                    home.FrameUpdate(0f, 0f);
                    retargeted &= Matches(start);
                }
                Expect(retargeted,
                    "目的地指示线：移动中换点当帧更新终点，新寻路结果回传后保持新目标，不闪回旧路线");

                home.SquadCommands.CancelCommandFor(id);
                home.FrameUpdate(0f, 0f);
                bool cancelled = home.SquadCommands.VisibleRouteCount == 0 && line == null && end == null;
                home.SquadCommands.IssueMoveTo(target, paused: false);
                home.FrameUpdate(0f, 0f);
                home.SquadCommands.ClearSelection();
                home.FrameUpdate(0f, 0f);
                bool deselected = home.SquadCommands.VisibleRouteCount == 0;
                home.SquadCommands.SelectSingle(id);
                home.FrameUpdate(0f, 0f);
                bool restored = home.SquadCommands.VisibleRouteCount == 1;
                home.SquadCommands.ReleaseVisuals();
                Expect(cancelled && deselected && restored && home.SquadCommands.VisibleRouteCount == 0,
                    "目的地指示线：取消命令、取消选择与释放观察表现均清除指示；重选能恢复当前目标");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        private static void CheckWorkOrderUnreachable()
        {
            CampaignState s = NewCampaign(8802);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 5000;
            GridCell? open = FindOpenArea(s, 5, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做“工单目标被围死”" + _openAreaMiss);
                return;
            }
            GridCell p = open.Value;
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", p, 0);
            CliffRing(s, p, 3);
            int notesBefore = NotificationCenter.History.Count(e => e.Type?.Id == "unreachable");
            WorkOrderRecord order = s.WorkOrders?.LastOrDefault(w => w.Kind == WorkOrderKind.Build);
            int steps = 0;
            for (; steps < 1800 && !(order != null && HomeValleyWorkOrders.IsUnreachableReason(order.FailureReason)); steps++)
            {
                WorldSimulation.StepMany(1);
            }
            bool waiting = order != null && order.State == WorkOrderState.Waiting && order.AssignedMachineLogicId == 0 && order.UnreachableNotified;
            NotificationEntry note = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == "unreachable");
            int notesAfter = NotificationCenter.History.Count(e => e.Type?.Id == "unreachable");
            string detail = note?.Latest?.DetailText ?? string.Empty;
            bool located = note?.Latest != null && note.Latest.HasLocation;
            // 30 秒后重试仍然到不了：不重复刷通知。
            int retries = 0;
            for (int i = 0; i < 60 * 40; i++)
            {
                WorldSimulation.StepMany(1);
                if (order != null && order.State == WorkOrderState.Reserved)
                {
                    retries++;
                }
            }
            int notesRetry = NotificationCenter.History.Count(e => e.Type?.Id == "unreachable");
            int memberCount = note?.Members.Count ?? 0;
            // 解围：再过一次 30 秒重试就能到达并开工。
            ClearRing(s, p, 3);
            int reached = -1;
            for (int i = 0; i < 60 * 45 && reached < 0; i++)
            {
                WorldSimulation.StepMany(1);
                if (order != null && order.State == WorkOrderState.InProgress)
                {
                    reached = i;
                }
            }
            string panelText = GameText.Format("nav.work.reason", GameText.Get("nav.fail.unreachable"));
            string why = Why((placed.Success, $"放置失败（{placed.Describe()}，放在 {p.X},{p.Y}）"), (order != null, "没有建造工单"),
                (waiting, $"工单状态 {order?.State} 原因 {order?.FailureReason} 指派 {order?.AssignedMachineLogicId} 已通知 {order?.UnreachableNotified}（等了 {steps} 步）"),
                (notesAfter == notesBefore + 1, $"通知 {notesBefore}→{notesAfter}"), (located, "通知无定位"),
                (detail.Contains(GameText.Get("nav.fail.unreachable")), "通知正文无原因"), (retries > 0, "30 秒后没有重试"),
                (notesRetry == notesAfter, $"重试后通知 {notesRetry}"), (memberCount == 1, $"通知成员 {memberCount}"), (reached > 0, $"解围后工单状态 {order?.State} 原因 {order?.FailureReason}"));
            Expect(placed.Success && order != null && waiting && notesAfter == notesBefore + 1 && located && detail.Contains(GameText.Get("nav.fail.unreachable"))
                   && retries > 0 && notesRetry == notesAfter && memberCount == 1 && reached > 0,
                $"工单目标被悬崖围死：机器寻路失败 → 工单立即转为等待（原因“{panelText}”），机器空出来，发一条可定位的“无法到达”通知（{detail}）；" +
                $"30 秒后重试仍到不了不再刷屏（通知仍 1 条）；解围后下一次重试第 {reached} 步到达开工（不原地发呆、不永远卡住）{why}");
        }

        private static void CheckSquadUnreachableAndFog()
        {
            CampaignState s = NewCampaign(8803);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell? open = FindOpenArea(s, 5, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做编队命令无法到达" + _openAreaMiss);
                return;
            }
            GridCell p = open.Value;
            CliffRing(s, p, 3);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out _);
            int id = SpawnHomeMachine(s, new Vector2(cmin.X - 4, core.Y));
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(p.X, p.Y), paused: false);
            int notesBefore = NotificationCenter.History.Count(e => e.Type?.Id == "unreachable");
            WorldSimulation.StepMany(30);
            string ev = home.SquadCommands.RecentEvents.LastOrDefault() ?? string.Empty;
            int notesAfter = NotificationCenter.History.Count(e => e.Type?.Id == "unreachable");
            home.Combat.TryGetMachineMarker(id, out HomeValleyMachineMarker marker);
            bool noCmd = !home.Combat.TryGetCommand(marker.UnitId, out _);
            Expect(ev.Contains(GameText.Get("nav.fail.unreachable")) && notesAfter > notesBefore && noCmd,
                $"编队移动到被围死的点：命令以明确原因结束（“{ev}”），发可定位通知，机器交还 AI、不挂着命令发呆");

            // 迷雾里的远距离目标：地形由种子就地生成（格网还没生成这些区块），照样规划出路线并走到。
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell far = default;
            bool found = false;
            for (int r = 0; r < 60 && !found; r++)
            {
                for (int a = 0; a < 8 && !found; a++)
                {
                    double ang = a * Math.PI / 4;
                    var c = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * (260 + r)), core.Y + (int)Math.Round(Math.Sin(ang) * (260 + r)));
                    if (Passable(c) && !map.IsChunkLoaded(GridMath.Address(c, map.ChunkSize).ChunkX, GridMath.Address(c, map.ChunkSize).ChunkY))
                    {
                        far = c;
                        found = true;
                    }
                }
            }
            bool fogged = found && !map.IsExplored(far);
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(far.X, far.Y), paused: false);
            bool neverBlocked = true;
            bool arrived = false;
            int steps = 0;
            for (; steps < 60 * 90 && !arrived; steps++)
            {
                WorldSimulation.StepMany(1);
                if (TryUnitPos(marker, out double2 q))
                {
                    neverBlocked &= Passable(CellOf(q));
                    arrived = math.distance(q, new double2(far.X, far.Y)) <= 1.25 && !home.Combat.TryGetCommand(marker.UnitId, out _);
                }
            }
            Expect(found && fogged && arrived && neverBlocked,
                $"目标在未探索区域（约 {Math.Round(Math.Sqrt((far.X - core.X) * (far.X - core.X) + (far.Y - core.Y) * (far.Y - core.Y)))} 格外，区块还没生成）：" +
                $"寻路按种子就地生成地形规划路线，第 {steps} 步走到，全程不踏进不可通行格");

            // 迷雾里的目标被围死：原因文本写明“目标在未探索区域”。机器此刻就站在上一个目标旁（那里已被它探明），
            // 所以另找一个离它很远、仍未探索的可走点，用悬崖围死后再下令（机器在围栏外）。
            // 围栏放在区块正中（整圈落在一个区块里）：目标一侧够不着任何区块入口，内核一轮就能判“被完全阻断”。
            GridCell pen = default;
            bool penFound = false;
            int cs = NavService.ConfigFromTuning().ChunkSize;
            for (int r = 0; r < 60 && !penFound; r++)
            {
                for (int a = 0; a < 8 && !penFound; a++)
                {
                    double ang = a * Math.PI / 4 + Math.PI / 8;
                    int px = core.X + (int)Math.Round(Math.Cos(ang) * (200 + r));
                    int py = core.Y + (int)Math.Round(Math.Sin(ang) * (200 + r));
                    var c = new GridCell((int)Math.Floor(px / (double)cs) * cs + cs / 2, (int)Math.Floor(py / (double)cs) * cs + cs / 2);
                    if (Math.Abs(c.X - far.X) + Math.Abs(c.Y - far.Y) > 120 && !map.IsExploredNoLoad(c) && Passable(c))
                    {
                        pen = c;
                        penFound = true;
                    }
                }
            }
            CliffRing(s, pen, 3);
            bool penFogged = penFound && !map.IsExploredNoLoad(pen);
            string lastBefore = home.SquadCommands.RecentEvents.LastOrDefault();
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(pen.X, pen.Y), paused: false);
            string ev2 = string.Empty;
            string failMark = GameText.Get("nav.fail.unreachable");
            string limitMark = GameText.Get("nav.fail.search_limit");
            for (int i = 0; i < 900 && ev2.Length == 0; i++)
            {
                WorldSimulation.StepMany(1);
                // 下令之后出现的失败事件（“移动已下达”那条不算）。
                IReadOnlyList<string> evs = home.SquadCommands.RecentEvents;
                for (int e = evs.Count - 1; e >= 0 && ev2.Length == 0; e--)
                {
                    if (ReferenceEquals(evs[e], lastBefore))
                    {
                        break;
                    }
                    if (evs[e].Contains(failMark) || evs[e].Contains(limitMark))
                    {
                        ev2 = evs[e];
                    }
                }
            }
            Expect(penFound && penFogged && ev2.Contains(GameText.Get("nav.fail.unreachable")) && ev2.Contains(GameText.Get("nav.fail.in_fog").Trim()),
                $"未探索区域里的目标被围死：命令以“无法到达”结束，原因里写明目标在未探索区域（“{ev2}”）" +
                Why((penFound, "找不到未探索的可走点"), (penFogged, "目标点已被探索")));
        }

        private static void CheckSquadCommandNotHijacked()
        {
            // FGR-BASE-020：机器在执行玩家的命令（编队移动）时，工单分配引擎不能把它当成空闲机器派去施工。
            // FGJ-M0 旅程实测发现：编队行军途中一台机器被自动派去建刚规划的发电机，玩家的移动命令被静默覆盖。
            CampaignState s = NewCampaign(8830);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 5000;
            GridCell? open = FindOpenArea(s, 5, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做“编队命令不被工单抢走”" + _openAreaMiss);
                return;
            }
            List<int> ids = MachineRegistry.AllRecords.Where(m => m.RegionId == HomeValleyLayout.RegionId && m.IsAlive).Select(m => m.LogicId).OrderBy(x => x).ToList();
            GridCell core = HomeGridService.CorePivot(s);
            home.SquadCommands.DebugSelectMany(ids);
            home.SquadCommands.IssueMoveTo(new Vector2(core.X + 200, core.Y), paused: false);
            WorldSimulation.StepMany(30);
            // 编队走着的时候规划一座发电机：出现一张待分配的建造单，家园里没有别的空闲机器。
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", open.Value, 0);
            WorkOrderRecord order = s.WorkOrders?.LastOrDefault(w => w.Kind == WorkOrderKind.Build);
            bool allStillMoving = true;
            for (int i = 0; i < 60 * 5; i++)
            {
                WorldSimulation.StepMany(1);
                foreach (int id in ids)
                {
                    allStillMoving &= home.Combat.TryGetMachineMarker(id, out HomeValleyMachineMarker mk)
                                      && home.Combat.TryGetCommand(mk.UnitId, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Move;
                }
            }
            bool notTaken = order != null && order.State == WorkOrderState.Ready && order.AssignedMachineLogicId == 0;
            // 反向：玩家让其中一台停下（命令结束 = 交还 AI），下一次分配就轮到它——规则没有把机器永久锁住。
            int freed = ids[0];
            home.Combat.TryGetMachineMarker(freed, out HomeValleyMachineMarker fm);
            home.Combat.ClearCommand(fm.UnitId);
            for (int i = 0; i < 90 && order != null && order.AssignedMachineLogicId == 0; i++)
            {
                WorldSimulation.StepMany(1);
            }
            bool takenAfterFree = order != null && order.AssignedMachineLogicId == freed;
            // 再给接了建造单的这台机器下一道编队命令：工单交还分配池（Ready、解除绑定），玩家规划的建筑保留——编队命令不是“取消建造”。
            home.SquadCommands.DebugSelectMany(new[] { freed });
            home.SquadCommands.IssueMoveTo(new Vector2(core.X - 120, core.Y), paused: false);
            WorldSimulation.StepMany(2);
            string plannedId = placed.BuildingId;
            bool planKept = s.BuildingRecords.Any(b => b.BuildingId == plannedId);
            bool released = order != null && order.State == WorkOrderState.Ready && order.AssignedMachineLogicId == 0;
            Expect(placed.Success && ids.Count >= 2 && allStillMoving && notTaken && takenAfterFree && planKept && released,
                $"编队行军途中规划一座发电机：{ids.Count} 台编队机器 5 秒里一直执行玩家的移动命令，建造单留在待分配（不抢正在执行命令的机器）；" +
                $"其中一台的命令结束后，下一次分配就把建造单交给它（#{freed}）；再给它下编队命令：建造单交还分配池、规划的发电机保留（不当成取消建造）" +
                Why((placed.Success, "放置失败：" + placed.Describe()), (ids.Count >= 2, $"只有 {ids.Count} 台机器"), (allStillMoving, "有机器的移动命令被换掉了"),
                    (notTaken, "编队行军时建造单被派出去了"), (takenAfterFree, "命令结束后建造单没派给空出来的机器"),
                    (planKept, "下编队命令后规划的发电机被撤掉了"), (released, $"下编队命令后建造单状态 {order?.State}、指派 #{order?.AssignedMachineLogicId}")));
        }

        private static void CheckSquadCommandDropsHaulCargo()
        {
            // 搬运单的货已在货舱里时，玩家给这台机器下编队命令：搬运单按取消处理，货放回机器脚下的地面。
            // 不能像其他工单那样交还分配池——货已从地面拿起、只挂在这台机器身上，交还后接单的机器去原处取不到货，货就永久丢了。
            CampaignState s = NewCampaign(8831);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out _);
            int id = SpawnHomeMachine(s, new Vector2(cmin.X - 4, core.Y));
            GroundItemRecord pile = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(cmin.X - 6, core.Y),
                CampaignEconomyLedger.ResourceScrap, 7, "fgnav-haul-pile");
            HomeValleyWorkOrders.WorkOrderOpResult created = HomeValleyWorkOrders.TryCreateHaul(s, pile.GroundItemId, id);
            WorkOrderRecord order = created.Success ? s.WorkOrders?.FirstOrDefault(w => w.WorkOrderId == created.WorkOrderId) : null;
            // 取货那一腿到达（测试直接调到达回调：这里测的是“下命令时怎么处理手上的货”，不是赶路）。
            bool picked = order != null && HomeValleyWorkOrders.OnArrivedAtHaulSource(s, order.WorkOrderId);
            MachineRegistry.TryGetRecord(id, out MachineRecord rec);
            bool inHold = rec?.Cargo != null && rec.Cargo.Length == 1 && rec.Cargo[0].Amount == 7;
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(core.X - 40, core.Y + 20), paused: false);
            WorldSimulation.StepMany(2);
            bool cancelled = order != null && order.State == WorkOrderState.Cancelled;
            bool holdEmpty = rec != null && (rec.Cargo == null || rec.Cargo.Length == 0);
            GroundItemRecord dropped = order == null ? null : HomeValleyCargo.FindGroundItemBySalvageId(s, order.SourceId + ":redrop:" + order.WorkOrderId);
            bool onGround = dropped != null && dropped.Amount == 7;
            Expect(created.Success && picked && inHold && cancelled && holdEmpty && onGround,
                "货在货舱里的搬运机器接到编队命令：搬运单按取消处理，7 份废料放回它脚下的地面（不交还分配池、不凭空消失）" +
                Why((created.Success, "建搬运单失败：" + created.FailureReason), (picked, "取货失败"), (inHold, "货没进货舱"),
                    (cancelled, $"搬运单状态 {order?.State}"), (holdEmpty, "货还在货舱里"), (onGround, dropped == null ? "地上没有放回的货" : $"地上的货 {dropped.Amount} 份")));
        }

        private static void CheckBuildPreviewWarning()
        {
            CampaignState s = NewCampaign(8804);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 5000;
            GridCell? open = FindOpenArea(s, 7, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做放置预览" + _openAreaMiss);
                return;
            }
            GridCell p = open.Value;
            GridOpResult g1 = HomeGridService.TryPlace(s, "generator_2", p, 0);
            // 围一圈悬崖，东侧留 3 格缺口：G1 仍可到达；把第二座发电机放在缺口上就会把它堵死。
            CliffRing(s, p, 3, keep: (dx, dy) => dx == 3 && Math.Abs(dy) <= 1);
            WorldSimulation.StepMany(1);
            var names = new List<string>();
            int cut = NavService.PlacementCutsOff(s, "generator_2", new GridCell(p.X + 3, p.Y), 0, names);
            int notCut = NavService.PlacementCutsOff(s, "generator_2", new GridCell(p.X - 10, p.Y + 10), 0, null);
            // 走正式的建造模式预览：同一警告出现在预览里，而且不阻止放置。
            home.BuildMode.Open();
            home.BuildMode.Select("generator_2");
            home.BuildMode.SetHover(s, new GridCell(p.X + 3, p.Y));
            home.BuildMode.RefreshPreview(s);
            GridPlacementResult preview = home.BuildMode.Preview;
            bool warned = preview != null && preview.Ok && preview.Warnings.Count == 1 && preview.Warnings[0].Contains(HomeGridService.DisplayName("generator_2"));
            GridOpResult g2 = HomeGridService.TryPlace(s, "generator_2", new GridCell(p.X + 3, p.Y), 0);
            home.BuildMode.Close();
            Expect(g1.Success && cut == 1 && names.Count == 1 && notCut == 0 && warned && g2.Success,
                $"放置预览：放在缺口上会让“{string.Join("、", names)}”机器无法到达 → 预览给出警告“{(preview != null && preview.Warnings.Count > 0 ? preview.Warnings[0] : "无")}”，" +
                "但仍可放置（FGR-LOG-012 只警告不阻止）；放在别处没有警告" +
                (g1.Success && cut == 1 && names.Count == 1 && notCut == 0 && warned && g2.Success ? string.Empty
                    : $"〔空地 {p}，G1 {g1.Success}（{g1.Reason}），缺口处断开 {cut}、别处 {notCut}，预览 {preview?.Ok}，G2 {g2.Success}（{g2.Reason}）〕"));
        }

        private static void CheckRaidRoute()
        {
            CampaignState s = NewCampaign(8805);
            WorldSimulation.LoadHome(resume: false);
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 4, out string failure);
            if (g == null)
            {
                Fail("派不出突袭：" + failure);
                return;
            }
            g.Speed = 20f; // 测试加速（行进规则不变）。
            bool neverBlocked = true;
            int steps = 0;
            bool gotRoute = false;
            bool cut = false;
            long invalidBefore = NavService.Kernel.Invalidated;
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            for (; steps < 60 * 120 && g.State == TransitGroupState.Marching; steps++)
            {
                WorldSimulation.StepMany(1);
                GridCell c = NavService.CellOf(g.PosX, g.PosY);
                neverBlocked &= Passable(c, NavConst.ClassHostile);
                gotRoute |= g.RouteState == WorldTransitSystem.RouteFollowing;
                if (!cut && g.RouteState == WorldTransitSystem.RouteFollowing && g.RouteIndex < g.RouteX.Length && WorldTransitSystem.RemainingDistance(g) > 80)
                {
                    // 中途被截断：在前方 15 格处横着拉一道 41 格长、2 格厚的悬崖墙。
                    double dx = g.RouteX[g.RouteIndex] - g.PosX;
                    double dy = g.RouteY[g.RouteIndex] - g.PosY;
                    double len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
                    double ux = dx / len;
                    double uy = dy / len;
                    double cx0 = g.PosX + ux * 15;
                    double cy0 = g.PosY + uy * 15;
                    for (int k = -20; k <= 20; k++)
                    {
                        for (int th = 0; th < 2; th++)
                        {
                            map.SetTerrain(NavService.CellOf(cx0 - uy * k + ux * th, cy0 + ux * k + uy * th), cliff);
                        }
                    }
                    cut = true;
                }
            }
            double eta = 0;
            Expect(gotRoute && cut && neverBlocked && g.State == TransitGroupState.Arrived && !g.Blocked && NavService.Kernel.Invalidated > invalidBefore,
                $"突袭沿地形行进（FGR-ARC-015；DEBT-FG0ARCH01-04 的寻路部分）：拿到敌方类别的路线，中途前方被拉起一道悬崖墙 → 重新规划绕过，" +
                $"第 {steps} 步进入核心到达半径，全程不踏进不可通行格{(eta > 0 ? string.Empty : string.Empty)}");
        }

        private static void CheckRaidBlockedCore()
        {
            CampaignState s = NewCampaign(8806);
            WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            // 核心四周 40 格处围一圈 2 格厚的悬崖：通往核心的路被完全堵死。
            CliffRing(s, core, 40, thickness: 2);
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 4, out string failure);
            if (g == null)
            {
                Fail("派不出突袭：" + failure);
                return;
            }
            g.Speed = 20f;
            int before = NotificationCenter.History.Count(e => e.Type?.Id == "raid_arrival");
            int steps = 0;
            for (; steps < 60 * 120 && g.State == TransitGroupState.Marching; steps++)
            {
                WorldSimulation.StepMany(1);
            }
            NotificationEntry note = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == "raid_arrival");
            string detail = note?.Latest?.DetailText ?? string.Empty;
            double d = Math.Sqrt((g.PosX - core.X) * (g.PosX - core.X) + (g.PosY - core.Y) * (g.PosY - core.Y));
            Expect(g.State == TransitGroupState.Arrived && g.Blocked && d >= 40 && d <= 50 && detail.Contains(GameText.Format("nav.transit.blocked", string.Empty).Trim().TrimEnd('：', ':'))
                   && NotificationCenter.History.Count(e => e.Type?.Id == "raid_arrival") > before,
                $"通往核心的路被完全堵死：突袭走到围墙外最近处停下（离核心 {d:F0} 格），“突袭到达”通知写明原因（“{detail}”）；攻城属于 FG6-DEF-05（DEBT-FG0ARCH06-03）");
        }

        private static void CheckMassRequests()
        {
            CampaignState s = NewCampaign(8807);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            // 在核心约 150 格外找一个可走的出生点（按种子的地形，不写死坐标）。200 个突袭者按每圈 16 个排开，
            // 最外圈半径约 16 格（CombatBench.SpawnRaidGroup）：把出生区整平成空地（测试布景），否则“出生就站在悬崖里”会被算成踏进不可通行格；
            // 出生区以外的一路上仍是种子生成的真实地形。
            const int spawnHalf = 17;
            GridCell spawn = default;
            bool found = false;
            for (int r = 0; r < 60 && !found; r++)
            {
                for (int a = 0; a < 16 && !found; a++)
                {
                    double ang = a * Math.PI / 8;
                    var c = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * (150 + r)), core.Y + (int)Math.Round(Math.Sin(ang) * (150 + r)));
                    if (Passable(c, NavConst.ClassHostile))
                    {
                        spawn = c;
                        found = true;
                    }
                }
            }
            if (found)
            {
                HomeGridMap spawnMap = HomeGridService.MapFor(s);
                for (int y = -spawnHalf; y <= spawnHalf; y++)
                {
                    for (int x = -spawnHalf; x <= spawnHalf; x++)
                    {
                        var q = new GridCell(spawn.X + x, spawn.Y + y);
                        if (spawnMap.GetTerrain(q) != 0)
                        {
                            spawnMap.SetTerrain(q, 0);
                        }
                    }
                }
                NavService.SyncGridChanges();
            }
            int count = (int)Math.Round(NavService.Tuning("nav.perf.mass_requests", 200f));
            CombatBench.Spec spec = CombatBench.FromTuning();
            spec.RaiderSense = 0f; // 只测寻路：不去追核心附近的机器。
            List<int> ids = CombatBench.SpawnRaidGroup(home.Combat, new Vector2(spawn.X, spawn.Y), new Vector2(core.X, core.Y), count, spec);
            NavCounters before = NavService.Kernel.Counters;
            long lateBefore = NavService.Kernel.LateCompletes;
            double maxBegin = 0;
            double sumBegin = 0;
            int stepsToAll = -1;
            bool neverBlocked = true;
            string firstBlocked = string.Empty;
            // 按游戏里最快的 3 倍速真实节奏推进（每步 1/180 秒墙钟；固定延迟 6 步 = 2 帧 ≈ 33 毫秒留给工作线程）。
            // 不控节奏时一步只要零点几毫秒，6 步延迟几乎不给工作线程时间，采纳步只能硬等——量到的是“自检跑得比游戏快”，不是主线程开销。
            const double stepWallMs = 1000.0 / (60.0 * 3.0);
            var pace = Stopwatch.StartNew();
            for (int i = 0; i < 600 && stepsToAll < 0; i++)
            {
                while (pace.Elapsed.TotalMilliseconds < i * stepWallMs)
                {
                    System.Threading.Thread.SpinWait(64);
                }
                WorldSimulation.StepMany(1);
                maxBegin = Math.Max(maxBegin, NavService.LastBeginStepMs);
                sumBegin += NavService.LastBeginStepMs;
                int following = 0;
                foreach (int id in ids)
                {
                    if (home.Combat.TryGetNavState(id, out CombatNavState st, out _) && st == CombatNavState.Following)
                    {
                        following++;
                    }
                    if (home.Combat.TryGetUnitPosition(id, out double2 p) && !Passable(CellOf(p), NavConst.ClassHostile))
                    {
                        if (neverBlocked)
                        {
                            home.Combat.TryGetNavState(id, out CombatNavState bst, out _);
                            firstBlocked = $"第 {i + 1} 步单位 {id} 在 ({p.x:F2},{p.y:F2}) 格 {CellOf(p).X},{CellOf(p).Y}（寻路状态 {bst}；出生中心 {spawn.X},{spawn.Y}）";
                        }
                        neverBlocked = false;
                    }
                }
                if (following == ids.Count)
                {
                    stepsToAll = i + 1;
                }
            }
            NavCounters after = NavService.Kernel.Counters;
            long searches = after.Searches - before.Searches;
            long shared = after.Shared - before.Shared;
            long late = NavService.Kernel.LateCompletes - lateBefore;
            CombatBench.ClearPrototypeUnits(home.Combat);
            PerfLines.Add($"{count} 个单位同一步请求 ~150 格长路线（按 3 倍速真实节奏推进）：{stepsToAll} 步内全部拿到路线；实际抽象搜索 {searches} 次、共享 {shared} 条；" +
                          $"主线程寻路流水线每步平均 {sumBegin / Math.Max(1, stepsToAll):F3} ms、最大 {maxBegin:F3} ms；采纳步被迫等工作线程 {late} 次");
            ExpectPerf(found && ids.Count == count && stepsToAll > 0 && stepsToAll <= 60 && searches < count / 4 && neverBlocked,
                $"负向：{count} 个单位同时请求长路线——分批（每批条数上限 + 距离预算，共享的只计一次）在 {stepsToAll} 步（{stepsToAll / 60f:F2} 游戏秒）内全部拿到路线，" +
                $"抽象搜索只有 {searches} 次；主线程每步开销最大 {maxBegin:F3} ms（< 4 ms，3 倍速节奏下采纳步等工作线程 {late} 次），全程不踏进不可通行格" +
                Why((found, "找不到出生点"), (ids.Count == count, $"只生成 {ids.Count} 个"), (stepsToAll > 0 && stepsToAll <= 60, $"拿到路线用了 {stepsToAll} 步"),
                    (searches < count / 4, $"抽象搜索 {searches} 次"), (neverBlocked, "踏进了不可通行格：" + firstBlocked), (maxBegin < 4.0, $"主线程最大 {maxBegin:F3} ms")),
                PerfGate.Lt(maxBegin, 4.0, "主线程每步最大 ms"));
        }

        // ── 存读档 / 观察 / 倍速（家园 + 突袭 + 巡逻一起）────────────────────────────────

        private static string Digest(CampaignState s)
        {
            var sb = new StringBuilder();
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            sb.Append("tick=").Append(GameClock.Ticks).Append(";home=").Append(home?.Kernel.StateHash().ToString("X16"));
            sb.Append(";nav=").Append(NavService.Kernel?.PendingHash().ToString("X16"));
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                sb.Append(";g=").Append(JsonUtility.ToJson(g));
            }
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                sb.Append(";o=").Append(JsonUtility.ToJson(o));
            }
            foreach (PatrolRecord p in WorldOutpostSystem.Patrols(s))
            {
                sb.Append(";p=").Append(JsonUtility.ToJson(p));
            }
            foreach (MachineRecord m in s.MachineRecords ?? Array.Empty<MachineRecord>())
            {
                sb.Append(";m=").Append(m.LogicId).Append('@').Append(m.RegionId);
            }
            return sb.ToString();
        }

        private static void BuildNavSave(int seed)
        {
            CampaignState s = NewCampaign(seed);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            int a = SpawnHomeMachine(s, new Vector2(cmin.X - 3, core.Y));
            int b = SpawnHomeMachine(s, new Vector2(cmin.X - 3, core.Y + 3));
            home.Combat.TryGetMachineMarker(a, out HomeValleyMachineMarker ma);
            ma.CommandMoveTo(new Vector3(cmax.X + 20, 1f, core.Y + 4));
            home.SquadCommands.DebugSelectMany(new[] { b });
            home.SquadCommands.IssueMoveTo(new Vector2(cmax.X + 18, core.Y - 12), paused: false);
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out _);
            if (g != null)
            {
                g.Speed = 12f;
            }
            WorldOutpostSystem.SpawnOutpostInTerritory(s, "foundry", withPatrol: true, reinforceIntervalTicks: 700);
            CombatBench.Spec spec = CombatBench.FromTuning();
            spec.RaiderSense = 0f;
            CombatBench.SpawnRaidGroup(home.Combat, new Vector2(core.X + 60, core.Y + 60), new Vector2(core.X, core.Y), 12, spec);
            int c = SpawnHomeMachine(s, new Vector2(cmin.X - 3, core.Y - 3));
            home.Combat.TryGetMachineMarker(c, out HomeValleyMachineMarker mc);
            // 走到有批次在飞、且有单位在沿路线走的时刻存档：开局那批路线早就交回了，第 30 步再给第三台机器下一道移动命令，
            // 它的请求进下一批，批次在飞（还没到采纳步）时存档。
            int guard = 0;
            do
            {
                if (guard == 30)
                {
                    mc.CommandMoveTo(new Vector3(cmax.X + 22, 1f, core.Y - 8));
                }
                WorldSimulation.StepMany(1);
            } while (guard++ < 400 && !(guard > 30 && NavService.Kernel.BatchActive && !NavService.Kernel.IsDue(GameClock.Ticks + 1)));
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("寻路存档写入失败：" + r.Message);
            }
        }

        private static void RestoreSave(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读寻路存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
        }

        private static void CheckSaveLoadMidFlight()
        {
            BuildNavSave(8810);
            CampaignState s0 = CampaignSession.Current;
            bool payload = !string.IsNullOrEmpty(s0.Nav?.Payload);
            bool inFlightAtSave = NavService.Kernel.BatchActive;
            const int steps = 900;
            // A：不存档接着跑（存档就是 A 的“现在”）。逐步记下家园内核的逐单位状态：不一致时指出第一处分叉在哪一步、哪个单位。
            var trace = new List<string>(steps + 1) { StepTrace() };
            for (int i = 0; i < steps; i++)
            {
                WorldSimulation.StepMany(1);
                trace.Add(StepTrace());
            }
            string a = Digest(s0);
            // B：读档接着跑同样的步数。
            RestoreSave(Slot);
            CampaignState s1 = CampaignSession.Current;
            string fork = string.Empty;
            string now0 = StepTrace();
            if (now0 != trace[0])
            {
                fork = Fork(0, trace[0], now0);
            }
            for (int i = 0; i < steps; i++)
            {
                WorldSimulation.StepMany(1);
                if (fork.Length == 0)
                {
                    string now = StepTrace();
                    if (now != trace[i + 1])
                    {
                        fork = Fork(i + 1, trace[i + 1], now);
                    }
                }
            }
            string b = Digest(s1);
            Expect(payload && inFlightAtSave && a == b,
                "有寻路批次在飞、机器沿路线赶路、突袭沿路线行进、据点巡逻、12 个突袭者要路线时存档：读档接着跑 900 步与不存档逐字段一致" +
                $"（家园内核哈希、寻路待处理状态、队伍 / 据点 / 巡逻记录）{(a == b ? string.Empty : "\n    A=" + a + "\n    B=" + b)}" +
                (fork.Length == 0 ? string.Empty : "\n    第一处分叉：" + fork) + Why((payload, "存档里没有寻路快照"), (inFlightAtSave, "存档时没有批次在飞")));
        }

        /// <summary>家园战斗内核此刻的逐单位状态（位置、命令、寻路状态、剩余路线），一单位一行；首行是步序号与整体哈希。</summary>
        private static string StepTrace()
        {
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            CombatKernel k = home?.Kernel;
            var sb = new StringBuilder();
            sb.Append("tick=").Append(GameClock.Ticks).Append(" hash=").Append(k?.StateHash().ToString("X16") ?? "-").Append('\n');
            if (k == null)
            {
                return sb.ToString();
            }
            sb.Append("steps=").Append(k.Steps).Append(" time=").Append(k.Time.ToString("R")).Append(" slots=").Append(k.SlotCount)
              .Append(" proj=").Append(k.ProjectileCount).Append(" events=").Append(k.GameplayPending)
              .Append(" weapons=").Append(k.WeaponCount).Append(" profiles=").Append(k.ProfileCount).Append('\n');
            for (int i = 0; i < k.SlotCount; i++)
            {
                CombatUnitView v = k.ViewAt(i);
                k.TryGetNavState(v.Id, out CombatNavState ns, out NavFailReason nf);
                k.TryGetCommand(v.Id, out CombatCommand c);
                sb.Append('#').Append(v.Id).Append(" ext=").Append(v.ExtKey).Append(' ').Append(v.Kind).Append(" f=").Append((uint)v.Flags)
                  .Append(" p=").Append(v.Position.x.ToString("R")).Append(',').Append(v.Position.y.ToString("R"))
                  .Append(" hp=").Append(v.Health.ToString("R")).Append('/').Append(v.MaxHealth.ToString("R")).Append(" b=").Append(v.Behavior)
                  .Append(" w=").Append(v.Weapon).Append(" heat=").Append(v.Heat.ToString("R")).Append(" aim=").Append(v.AimReadyAt.ToString("R"))
                  .Append(" fire=").Append(v.NextFireAt.ToString("R")).Append(" cyc=").Append(v.Cycle.ToString("R")).Append('/').Append(v.Secondary.ToString("R"))
                  .Append(" mark=").Append(v.MarkedUntil.ToString("R")).Append(" tgt=").Append(v.CommandTarget)
                  .Append(" cmd=").Append(c.Kind).Append('@').Append(c.Pos.x.ToString("R")).Append(',').Append(c.Pos.y.ToString("R"))
                  .Append(" atk=").Append(c.AtkCd.ToString("R"))
                  .Append(" prog=").Append(c.ProgTimer.ToString("R")).Append(" last=").Append(c.LastDist.ToString("R")).Append('/').Append(c.HasLast)
                  .Append(" stuck=").Append(c.Stuck).Append(" nav=").Append(ns).Append('/').Append(nf)
                  .Append(" rem=").Append(k.RemainingRoute(v.Id).ToString("R")).Append('\n');
            }
            return sb.ToString();
        }

        private static string Fork(int step, string a, string b)
        {
            string[] la = a.Split('\n');
            string[] lb = b.Split('\n');
            var sb = new StringBuilder().Append("读档后第 ").Append(step).Append(" 步");
            int shown = 0;
            for (int i = 0; i < Math.Max(la.Length, lb.Length) && shown < 5; i++)
            {
                string x = i < la.Length ? la[i] : "（无）";
                string y = i < lb.Length ? lb[i] : "（无）";
                if (x != y)
                {
                    sb.Append("\n      A: ").Append(x).Append("\n      B: ").Append(y);
                    shown++;
                }
            }
            return sb.ToString();
        }

        private sealed class SilentReader : IInputReader
        {
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static void CheckObservationAndSpeed()
        {
            BuildNavSave(8811);
            // A：从不观察，无头推进。
            RestoreSave(Slot);
            long target = GameClock.Ticks + 900;
            WorldSimulation.StepMany(900);
            string headless = Digest(CampaignSession.Current);
            // B：镜头在家园逐帧推进，其间在核心、突袭、远处之间来回飞跃（流式加载、表现对象、路线折线都在跑）。
            RestoreSave(Slot);
            var reader = new SilentReader();
            InputRouter.DebugSetReader(reader);
            int frame = 0;
            while (GameClock.Ticks < target && frame < 5000)
            {
                if (frame % 90 == 0)
                {
                    GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
                    WorldView.FlyTo(HomeValleyLayout.RegionId, frame % 180 == 0 ? new Vector2(core.X + 200, core.Y - 150) : new Vector2(core.X, core.Y));
                }
                WorldSimulation.Frame(1f / 60f, target);
                frame++;
            }
            InputRouter.DebugSetReader(null);
            string observed = Digest(CampaignSession.Current);
            // C：0.5x / 2x / 3x 与暂停。
            var bySpeed = new List<string>();
            foreach (float speed in new[] { 0.5f, 2f, 3f })
            {
                RestoreSave(Slot);
                GameClock.SetSpeed(speed);
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                bySpeed.Add(Digest(CampaignSession.Current));
            }
            RestoreSave(Slot);
            string beforePause = Digest(CampaignSession.Current);
            bool due = NavService.Kernel.BatchActive;
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            string paused = Digest(CampaignSession.Current);
            bool stillPending = NavService.Kernel.BatchActive == due;
            GameClock.SetPaused(false);
            Expect(observed == headless && bySpeed.All(x => x == headless) && beforePause == paused && stillPending,
                $"观察不改变结果（FGR-BASE-021）：镜头飞跃 {frame / 90} 次、逐帧渲染的一遍与从不观察的无头一遍逐字段一致；0.5x / 2x / 3x 跑到同一步结果相同；" +
                "暂停 2 秒真实时间：不走步、在飞的寻路批次不被采纳（采纳只看步序号）");
        }

        // ── E 休眠与唤醒（FGT-GEN-010）────────────────────────────────────────────────

        private static string SimFields(OutpostRecord o) =>
            $"{o.OutpostId}|{o.Garrison}|{o.GarrisonCap}|{o.NextReinforceTick}|{o.ReinforcementsApplied}|{o.SimTick}|{o.CellX},{o.CellY}";

        private static string SimFields(PatrolRecord p) =>
            $"{p.PatrolId}|{p.ProgressMilli}|{p.LoopMilli}|{p.RouteState}|{p.RouteReadyTick}|{string.Join(",", p.RouteX)}|{string.Join(",", p.RouteY)}|{p.PosX:R},{p.PosY:R}";

        private static (OutpostRecord o, PatrolRecord p) SetupOutpost(int seed, bool control, int interval, int cap)
        {
            CampaignState s = NewCampaign(seed);
            WorldSimulation.LoadHome(resume: false);
            OutpostRecord o = WorldOutpostSystem.SpawnOutpostInTerritory(s, "silent", withPatrol: true, reinforceIntervalTicks: interval);
            o.GarrisonCap = cap;
            o.AlwaysSimulate = control;
            PatrolRecord p = WorldOutpostSystem.Patrols(s).First(x => x.OutpostId == o.OutpostId);
            return (o, p);
        }

        private static void CheckDormancyEquivalence()
        {
            const int seed = 8820;
            const int interval = 600;   // 10 游戏秒一次增援（测试压缩；规则同 3 游戏日）
            const int cap = 10;          // 初始 6，休眠期间会跨过 8 个增援点、在第 4 个时满员
            const int dormantSteps = 60 * 80;
            // 对照组：一直在模拟。
            (OutpostRecord oc, PatrolRecord pc) = SetupOutpost(seed, control: true, interval, cap);
            WorldSimulation.StepMany(dormantSteps);
            WorldOutpostSystem.WakeNow(CampaignSession.Current, oc.OutpostId, WorldOutpostSystem.WakeEvent);
            string controlAtWake = SimFields(oc) + "#" + SimFields(pc);
            WorldSimulation.StepMany(60 * 20);
            string controlLater = SimFields(oc) + "#" + SimFields(pc);
            int controlGarrisonMid = -1;

            // 实验组：离家园很远 → 休眠；中途存读档一次；事件唤醒。
            (OutpostRecord od, PatrolRecord pd) = SetupOutpost(seed, control: false, interval, cap);
            WorldSimulation.StepMany(dormantSteps / 2);
            bool dormantMid = od.Dormant && od.DormantSinceTick >= 0;
            long simTickFrozen = od.SimTick;
            int garrisonFrozen = od.Garrison;
            // 只读查看：按补算规则现算“此刻”的驻军，不改任何状态。
            string beforePeek = JsonUtility.ToJson(od) + JsonUtility.ToJson(pd);
            WorldOutpostSystem.Peek(CampaignSession.Current, od, out int peekGarrison, out long peekNext);
            Vector2 peekPatrol = WorldOutpostSystem.PeekPatrolPosition(CampaignSession.Current, pd);
            bool peekPure = beforePeek == JsonUtility.ToJson(od) + JsonUtility.ToJson(pd);
            SaveResult sr = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            RestoreSave(Slot);
            CampaignState s2 = CampaignSession.Current;
            // 按编号找回同一个据点（新建战役还登记了家园区侦察巢，不能取“第一个”）。
            string odId = od.OutpostId;
            od = WorldOutpostSystem.Outposts(s2).First(x => x.OutpostId == odId);
            pd = WorldOutpostSystem.Patrols(s2).First(x => x.OutpostId == odId);
            bool stillDormant = od.Dormant;
            WorldSimulation.StepMany(dormantSteps - dormantSteps / 2);
            long reinforcePointsCrossed = (GameClock.Ticks - 1 - (simTickFrozen - 1)) / interval;
            bool frozenWhileDormant = od.Dormant && od.SimTick == simTickFrozen && od.Garrison == garrisonFrozen;
            string frozenInfo = $"Dormant={od.Dormant} SimTick {od.SimTick} vs {simTickFrozen}，驻军 {od.Garrison} vs {garrisonFrozen}";
            WorldOutpostSystem.WakeNow(s2, od.OutpostId, WorldOutpostSystem.WakeEvent);
            string dormantAtWake = SimFields(od) + "#" + SimFields(pd);
            int wakesAfterEvent = od.WakeCount;
            string reasonAfterEvent = od.LastWakeReason;
            WorldSimulation.StepMany(60 * 20);
            // 事件唤醒后它仍离所有己方实体很远：下一次休眠判定就按 FGR-GEN-052 回到休眠（冻结，不参与每步模拟）。
            // 20 秒后再用事件唤醒补算一次，补算结果要与一直在模拟的对照组逐字段一致。
            bool resleptAfterEvent = od.Dormant;
            WorldOutpostSystem.WakeNow(s2, od.OutpostId, WorldOutpostSystem.WakeEvent);
            string dormantLater = SimFields(od) + "#" + SimFields(pd);

            // Peek 的值 = 对照组在同一步的真实值：再跑一遍对照组到同一步取值。
            (OutpostRecord oc2, PatrolRecord pc2) = SetupOutpost(seed, control: true, interval, cap);
            WorldSimulation.StepMany(dormantSteps / 2);
            controlGarrisonMid = oc2.Garrison;
            bool peekMatches = peekGarrison == oc2.Garrison && peekNext == oc2.NextReinforceTick
                               && Vector2.Distance(peekPatrol, new Vector2((float)pc2.PosX, (float)pc2.PosY)) < 1e-3f;
            Expect(dormantMid && stillDormant && frozenWhileDormant && sr.Success && controlAtWake == dormantAtWake && controlLater == dormantLater && resleptAfterEvent
                   && wakesAfterEvent == 1 && reasonAfterEvent == WorldOutpostSystem.WakeEvent && reinforcePointsCrossed >= 7 && oc.Garrison == cap,
                $"FGT-GEN-010 休眠与唤醒：据点离家园很远 → 休眠（{dormantSteps} 步里不推进，SimTick 冻结在 {simTickFrozen}），期间跨过 {reinforcePointsCrossed} 个增援点、中途存读档一次；" +
                $"事件唤醒后按确定性规则补算，与“一直在模拟”的对照组逐字段一致（驻军 {oc.Garrison}/{cap}、下次增援、累计增援、巡逻进度与位置）；" +
                "仍离家园很远，下一次判定回到休眠，20 游戏秒后再唤醒补算仍与对照组一致" +
                $"{(controlAtWake == dormantAtWake ? string.Empty : "\n    对照=" + controlAtWake + "\n    休眠=" + dormantAtWake)}" +
                $"{(controlLater == dormantLater ? string.Empty : "\n    20 秒后对照=" + controlLater + "\n    20 秒后休眠=" + dormantLater)}" +
                Why((dormantMid, $"半程没休眠（Dormant={od.Dormant} DormantSince={od.DormantSinceTick}）"), (stillDormant, "读档后不再休眠"),
                    (frozenWhileDormant, $"休眠期间被推进（{frozenInfo}）"), (sr.Success, "存档失败 " + sr.Message),
                    (wakesAfterEvent == 1, $"唤醒次数 {wakesAfterEvent}"), (reasonAfterEvent == WorldOutpostSystem.WakeEvent, "唤醒原因 " + reasonAfterEvent),
                    (resleptAfterEvent, "事件唤醒 20 秒后没有回到休眠"),
                    (reinforcePointsCrossed >= 7, $"跨过增援点 {reinforcePointsCrossed}"), (oc.Garrison == cap, $"对照组驻军 {oc.Garrison}")));
            Expect(peekPure && peekMatches,
                $"只读查看休眠据点（表现层用）：不改任何状态，现算的驻军 {peekGarrison}（对照组同一步 {controlGarrisonMid}）、巡逻位置与对照组相同——观察不改变结果");
        }

        private static void CheckProximityWake()
        {
            const int seed = 8821;
            string RunOnce(bool control, out OutpostRecord o, out int wakeTick, out string reason)
            {
                CampaignState s = NewCampaign(seed);
                HomeValleyController home = WorldSimulation.LoadHome(resume: false);
                GridCell core = HomeGridService.CorePivot(s);
                // 据点放在核心 360 格外（> 休眠距离 + 滞回）的可走格上，方向由种子派生。
                uint h = BinGames.Sim.WorldGen.WorldGenMath.Hash(unchecked((uint)seed), 7, 7, 0x1234u);
                double ang = (h & 0xFFFF) / 65536.0 * 2 * Math.PI;
                GridCell at = default;
                for (int r = 0; r < 40; r++)
                {
                    var c = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * (360 + r)), core.Y + (int)Math.Round(Math.Sin(ang) * (360 + r)));
                    if (Passable(c, NavConst.ClassHostile))
                    {
                        at = c;
                        break;
                    }
                }
                o = WorldOutpostSystem.SpawnOutpost(s, "silent", at, withPatrol: true, reinforceIntervalTicks: 450);
                o.AlwaysSimulate = control;
                WorldSimulation.StepMany(120);
                // 一台机器朝据点走 90 格（真实编队命令），进入 300 格以内。
                HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
                int id = SpawnHomeMachine(s, new Vector2(core.X, cmax.Y + 3));
                var toward = new Vector2((float)(core.X + Math.Cos(ang) * 95), (float)(core.Y + Math.Sin(ang) * 95));
                home.SquadCommands.DebugSelectMany(new[] { id });
                home.SquadCommands.IssueMoveTo(toward, paused: false);
                wakeTick = -1;
                for (int i = 0; i < 60 * 60; i++)
                {
                    WorldSimulation.StepMany(1);
                    if (wakeTick < 0 && o.WakeCount > 0)
                    {
                        wakeTick = (int)GameClock.Ticks;
                    }
                }
                reason = o.LastWakeReason;
                string oid = o.OutpostId; // out 参数不能被 lambda 捕获（CS1628）
                PatrolRecord p = WorldOutpostSystem.Patrols(s).First(x => x.OutpostId == oid);
                return SimFields(o) + "#" + SimFields(p);
            }
            string ctl = RunOnce(true, out OutpostRecord oc, out _, out _);
            string exp = RunOnce(false, out OutpostRecord od, out int wake, out string why);
            Expect(ctl == exp && wake > 0 && why == WorldOutpostSystem.WakeProximity && !od.Dormant && od.WakeCount == 1,
                $"己方单位靠近唤醒：机器按编队命令朝休眠据点走，进入 300 格后在第 {wake} 步（休眠判定周期 + 分帧队列）唤醒（原因“{GameText.Get(why ?? string.Empty)}”），" +
                $"之后与一直在模拟的对照组逐字段一致{(ctl == exp ? string.Empty : "\n    对照=" + ctl + "\n    实验=" + exp)}");

            // 突袭导演调用：从休眠据点派出突袭 → 先唤醒补算再抽兵（驻军 = 补算后的驻军 − 派出数）。
            CampaignState s2 = NewCampaign(8822);
            WorldSimulation.LoadHome(resume: false);
            OutpostRecord o2 = WorldOutpostSystem.SpawnOutpostInTerritory(s2, "foundry", withPatrol: false, reinforceIntervalTicks: 300);
            WorldSimulation.StepMany(60 * 30);
            bool dormant = o2.Dormant;
            WorldOutpostSystem.Peek(s2, o2, out int expectedGarrison, out _);
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromOutpost(s2, o2.OutpostId, 3, out string failure);
            Expect(dormant && g != null && !o2.Dormant && o2.LastWakeReason == WorldOutpostSystem.WakeRaid && o2.Garrison == expectedGarrison - 3
                   && (int)Math.Round(g.PosX) == o2.CellX && (int)Math.Round(g.PosY) == o2.CellY,
                $"突袭导演调用：从休眠据点派出 3 台 → 据点先按规则唤醒补算（驻军 {expectedGarrison}），再派出（剩 {o2.Garrison}），队伍从据点所在格出发{(failure == null ? string.Empty : "；失败：" + failure)}");
        }

        private static void CheckWakeQueueBudget()
        {
            CampaignState s = NewCampaign(8823);
            WorldSimulation.LoadHome(resume: false);
            GridCell core = HomeGridService.CorePivot(s);
            var p = new GridCell(core.X + 460, core.Y);
            var ids = new List<OutpostRecord>();
            for (int i = 0; i < 10; i++)
            {
                ids.Add(WorldOutpostSystem.SpawnOutpost(s, "silent", new GridCell(p.X + (i % 5) * 8, p.Y + (i / 5) * 8), withPatrol: true, reinforceIntervalTicks: 200));
            }
            WorldSimulation.StepMany(60 * 3);
            bool allDormant = ids.All(o => o.Dormant);
            // 一台机器出现在据点群中间（远征回来的机器落点）：10 个据点同时符合唤醒条件。
            // 登记后不预走步（settleSteps: 0）：唤醒可能就发生在紧接着的那几步里，逐步计数要从登记后的第一步开始。
            int evalBefore = WorldOutpostSystem.Evaluations;
            int start = WorldOutpostSystem.WakesProcessed;
            int machineId = SpawnHomeMachine(s, new Vector2(p.X + 16, p.Y + 4), settleSteps: 0);
            var perStep = new List<int>();
            int last = start;
            for (int i = 0; i < 200 && WorldOutpostSystem.WakesProcessed - start < 10; i++)
            {
                WorldSimulation.StepMany(1);
                int now = WorldOutpostSystem.WakesProcessed;
                if (now != last)
                {
                    perStep.Add(now - last);
                }
                last = now;
            }
            int perStepCap = WorldOutpostSystem.WakesPerStep;
            double budget = NavService.Tuning("outpost.wake_budget_ms", 2f);
            PerfLines.Add($"据点唤醒：单个唤醒（补算增援 + 巡逻）最长 {WorldOutpostSystem.MaxWakeMs:F4} ms（预算 {budget} ms）；10 个同时符合条件分 {perStep.Count} 步处理（{string.Join("+", perStep)}）");
            ExpectPerf(allDormant && perStep.Sum() == 10 && perStep.All(n => n <= perStepCap) && perStep.Count >= 3 && ids.All(o => !o.Dormant),
                $"唤醒分帧：10 个据点同时符合唤醒条件，每步至多 {perStepCap} 个（{string.Join("+", perStep)}），单个唤醒最长 {WorldOutpostSystem.MaxWakeMs:F4} ms ≤ {budget} ms（FG17 休眠唤醒行）" +
                Why((allDormant, "机器出现前并非全部休眠"),
                    (perStep.Sum() == 10 && ids.All(o => !o.Dormant),
                        $"只唤醒 {perStep.Sum()} 个、仍休眠 {ids.Count(o => o.Dormant)} 个；机器实时位置 " +
                        (MachineRegistry.TryGetLivePosition(machineId, out Vector2 mp) ? $"({mp.x:F1},{mp.y:F1})" : "无") + $"（下令落点 ({p.X + 16},{p.Y + 4})）；" +
                        $"休眠判定 {WorldOutpostSystem.Evaluations - evalBefore} 次；排队 {s.Raids?.PendingWakeIds?.Length}；据点唤醒次数 {string.Join(",", ids.Select(o => o.WakeCount))}"),
                    (perStep.Count >= 3, $"只分了 {perStep.Count} 步")),
                PerfGate.Le(WorldOutpostSystem.MaxWakeMs, budget, "单个唤醒最长 ms"));
        }

        private static void CheckSeedIndependence()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 17, 2929, 404040 })
            {
                CampaignState s = NewCampaign(seed);
                WorldSimulation.LoadHome(resume: false);
                OutpostRecord o = WorldOutpostSystem.SpawnOutpostInTerritory(s, "silent", withPatrol: true);
                PatrolRecord p = WorldOutpostSystem.Patrols(s).First(x => x.OutpostId == o.OutpostId);
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "foundry", 3, out _);
                WorldSimulation.StepMany(60);
                bool patrolRoute = p.RouteState == WorldOutpostSystem.RouteReady && p.LoopMilli > 0 && p.RouteX.Length >= 2;
                bool raidRoute = g != null && g.RouteState == WorldTransitSystem.RouteFollowing && g.RouteX.Length >= 1;
                bool routeClear = raidRoute && NavService.Kernel.RouteClear(new int2((int)Math.Round(g.PosX), (int)Math.Round(g.PosY)),
                    g.RouteX.Select((x, i) => new int2(x, g.RouteY[i])).ToList(), g.RouteIndex, NavConst.ClassHostile);
                all &= patrolRoute && raidRoute && routeClear && o.Dormant;
                lines.Add($"种子 {seed}：据点 ({o.CellX},{o.CellY}) 休眠={o.Dormant}、巡逻路线 {p.RouteX.Length} 点 / 一圈 {p.LoopMilli / 1000.0:F0} 格；突袭路线 {g?.RouteX.Length} 点");
            }
            Expect(all, $"种子无关（B25）：据点位置来自规划层、巡逻折返点由（世界种子, 序号）派生，三个种子下巡逻与突袭都拿到畅通的路线。{string.Join("；", lines)}");
        }

        // ── F 性能 ──────────────────────────────────────────────────────────────────

        private static void CheckLongRoutePerformance()
        {
            double budget = NavService.Tuning("nav.perf.budget_ms", 20f);
            int cells = (int)Math.Round(NavService.Tuning("nav.perf.route_cells", 2000f));
            var warm = new List<double>();
            var cold = new List<double>();
            var lens = new List<float>();
            bool allOk = true;
            foreach (int seed in new[] { 501, 60602, 7070703 })
            {
                CampaignState s = NewCampaign(seed);
                HomeGridMap map = HomeGridService.MapFor(s);
                var src = map.TerrainSource as WorldTerrainSource;
                if (src == null)
                {
                    Fail("性能：地形来源不是世界生成器");
                    return;
                }
                GridCell core = HomeGridService.CorePivot(s);
                foreach (int dir in new[] { 0, 1 })
                {
                    using var k = new NavKernel(NavService.ConfigFromTuning(), NavService.TerrainTable(), true, src.Params, src.Rects, src.Zones);
                    var goal = new GridCell(core.X + (dir == 0 ? cells + 40 : -40), core.Y + (dir == 0 ? 60 : cells + 40));
                    for (int r = 0; r < 64 && !k.Passable(goal.X, goal.Y, NavConst.ClassPlayer); r++)
                    {
                        goal = new GridCell(goal.X + 1, goal.Y + (r % 2));
                    }
                    var pts = new List<int2>();
                    NavRequest req = Req(core.X + 8, core.Y + 8, goal.X, goal.Y);
                    NavResult rc = k.FindNow(req, pts, onWorker: true, out double msCold);
                    NavResult rw = k.FindNow(req, pts, onWorker: true, out double msWarm);
                    bool clear = k.RouteClear(req.Start, pts, 0, NavConst.ClassPlayer);
                    allOk &= rc.Status == NavStatus.Ok && rw.Status == NavStatus.Ok && rw.Length >= cells && clear;
                    cold.Add(msCold);
                    warm.Add(msWarm);
                    lens.Add(rw.Length);
                }
            }
            PerfLines.Add($"长距离寻路（3 个种子 × 2 个方向，路线长 {lens.Min():F0}～{lens.Max():F0} 格）：热缓存（区块已生成、抽象图已建）工作线程耗时 平均 {warm.Average():F2} / 最大 {warm.Max():F2} ms；" +
                          $"冷启动（途经区块当场按种子生成 + 建抽象图）平均 {cold.Average():F2} / 最大 {cold.Max():F2} ms（预算 {budget} ms）");
            ExpectPerf(allOk,
                $"FG15 长距离寻路：{cells} 格以上的路线（实测 {lens.Min():F0}～{lens.Max():F0} 格）在工作线程上 {warm.Max():F2} ms ≤ {budget} ms（热缓存）；冷启动最大 {cold.Max():F2} ms" +
                "（途经的区块第一次要按种子生成地形，见证据文件与 ADR）；路线每段都畅通",
                PerfGate.Le(warm.Max(), budget, "热缓存长路线最大 ms"));
        }

        // ── 工具 ────────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        /// <summary>失败时点名没满足的子条件（全满足时返回空串），接在断言文字后面。</summary>
        private static string Why(params (bool ok, string what)[] conds)
        {
            List<string> bad = conds.Where(c => !c.ok).Select(c => c.what).ToList();
            return bad.Count == 0 ? string.Empty : "【未满足：" + string.Join("；", bad) + "】";
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
