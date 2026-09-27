using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
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

        [MenuItem("BinGames/自检：FG 层级寻路与休眠唤醒")]
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
                Step(CheckPendingSnapshot);
                Step(CheckCombatFollow);
                Step(CheckCombatRouteCutAndSnapshot);
                Step(CheckSeparation);
                Step(CheckHomeDetourAndCut);
                Step(CheckWorkOrderUnreachable);
                Step(CheckSquadUnreachableAndFog);
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
            Func<int, int, bool> map = RandomMap(4242, w, h, 70);
            using NavKernel k = Synth(wc, hc, map);
            var rnd = new System.Random(99);
            var pts = new List<int2>();
            int agree = 0;
            int reachable = 0;
            int unreachable = 0;
            int allClear = 0;
            double worstRatio = 0;
            double sumRatio = 0;
            const int pairs = 160;
            int tried = 0;
            while (tried < pairs)
            {
                var s = new int2(rnd.Next(0, w), rnd.Next(0, h));
                var t = new int2(rnd.Next(0, w), rnd.Next(0, h));
                if (map(s.x, s.y) || map(t.x, t.y))
                {
                    continue;
                }
                tried++;
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
                $"与整图暴力 Dijkstra 对照（128×128 随机障碍，{pairs} 对起终点）：可达性一致 {agree}/{pairs}（可达 {reachable}、不可达 {unreachable}）；" +
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
            var with = RunOnce(0.5f);
            var again = RunOnce(0.5f);
            var without = RunOnce(0f);
            Expect(with.minDist > 0.6 && without.minDist < 0.05 && with.hash == again.hash && with.clear && with.progress > 25,
                $"分离（DEBT-FG0ARCH03-03）：16 个突袭者在同一点出生、同一目标，开分离后 6 秒内两两最小间距 {with.minDist:F2} 米（关掉分离 {without.minDist:F3} 米，叠在一处），" +
                $"平均前进 {with.progress:F1} 格、全程不进不可通行格，两次运行哈希一致（确定性）");
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

        private static int SpawnHomeMachine(CampaignState s, Vector2 at, bool noAutoWork = true)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                Fail("登记测试机器失败：" + r.Message);
                return 0;
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
            WorldSimulation.StepMany(2);
            return r.LogicId;
        }

        private static bool Passable(GridCell c, int cls = NavConst.ClassPlayer) => NavService.Kernel != null && NavService.Kernel.Passable(c.X, c.Y, cls);

        private static GridCell CellOf(double2 p) => NavService.CellOf(p.x, p.y);

        private static bool TryUnitPos(HomeValleyMachineMarker marker, out double2 p)
        {
            p = default;
            return marker != null && marker.Site != null && marker.Site.TryGetUnitPosition(marker.UnitId, out p);
        }

        /// <summary>找一块全部可走、没有建筑、已探索的方形空地（核心周围按环由近及远，确定性）。</summary>
        private static GridCell? FindOpenArea(CampaignState s, int half, int minR, int maxR)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
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
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        for (int y = -half; y <= half && ok; y++)
                        {
                            for (int x = -half; x <= half && ok; x++)
                            {
                                var q = new GridCell(c.X + x, c.Y + y);
                                ok = Passable(q) && map.OccupantAt(q) == null && map.IsExplored(q) && map.GetTerrain(q) == 0;
                            }
                        }
                        if (ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        private static void CliffRing(CampaignState s, GridCell center, int r, int thickness = 1, Func<int, int, bool> keep = null)
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
            GridCell? open = FindOpenArea(s, 6, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做“中途截断”");
                return;
            }
            GridCell o = open.Value;
            marker.SetPosition(new Vector2(o.X - 6, o.Y));
            WorldSimulation.StepMany(1);
            arrived = false;
            marker.CommandMoveTo(new Vector3(o.X + 6, 1f, o.Y), () => arrived = true);
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
                $"赶路途中前方新放了一座建筑（{placed.Outcome}）：路线被截断 → 重新规划（失效 {NavService.Kernel.Invalidated - invalidBefore} 次），第 {steps} 步绕过去到达，不穿过新建筑");
        }

        private static void CheckWorkOrderUnreachable()
        {
            CampaignState s = NewCampaign(8802);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 5000;
            GridCell? open = FindOpenArea(s, 5, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做“工单目标被围死”");
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
            Expect(placed.Success && order != null && waiting && notesAfter == notesBefore + 1 && located && detail.Contains(GameText.Get("nav.fail.unreachable"))
                   && retries > 0 && notesRetry == notesAfter && memberCount == 1 && reached > 0,
                $"工单目标被悬崖围死：机器寻路失败 → 工单立即转为等待（原因“{panelText}”），机器空出来，发一条可定位的“无法到达”通知（{detail}）；" +
                $"30 秒后重试仍到不了不再刷屏（通知仍 1 条）；解围后下一次重试第 {reached} 步到达开工（不原地发呆、不永远卡住）");
        }

        private static void CheckSquadUnreachableAndFog()
        {
            CampaignState s = NewCampaign(8803);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell? open = FindOpenArea(s, 5, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做编队命令无法到达");
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

            // 迷雾里的目标被围死：原因文本写明“目标在未探索区域”。
            CliffRing(s, far, 3);
            home.SquadCommands.DebugSelectMany(new[] { id });
            home.SquadCommands.IssueMoveTo(new Vector2(far.X + 40, far.Y), paused: false);
            WorldSimulation.StepMany(5);
            home.SquadCommands.IssueMoveTo(new Vector2(far.X, far.Y), paused: false);
            WorldSimulation.StepMany(30);
            string ev2 = home.SquadCommands.RecentEvents.LastOrDefault() ?? string.Empty;
            Expect(ev2.Contains(GameText.Get("nav.fail.in_fog").Trim()),
                $"未探索区域里的目标无法到达：原因里写明目标在未探索区域（“{ev2}”）");
        }

        private static void CheckBuildPreviewWarning()
        {
            CampaignState s = NewCampaign(8804);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 5000;
            GridCell? open = FindOpenArea(s, 7, 12, 40);
            if (open == null)
            {
                Fail("找不到空地做放置预览");
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
                "但仍可放置（FGR-LOG-012 只警告不阻止）；放在别处没有警告");
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
            // 在核心约 150 格外找一块可走的出生点（按种子的地形，不写死坐标）。
            GridCell spawn = default;
            bool found = false;
            for (int r = 0; r < 60 && !found; r++)
            {
                for (int a = 0; a < 16 && !found; a++)
                {
                    double ang = a * Math.PI / 8;
                    var c = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * (150 + r)), core.Y + (int)Math.Round(Math.Sin(ang) * (150 + r)));
                    bool ok = true;
                    for (int y = -5; y <= 5 && ok; y++)
                    {
                        for (int x = -5; x <= 5 && ok; x++)
                        {
                            ok = Passable(new GridCell(c.X + x, c.Y + y), NavConst.ClassHostile);
                        }
                    }
                    if (ok)
                    {
                        spawn = c;
                        found = true;
                    }
                }
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
            for (int i = 0; i < 600 && stepsToAll < 0; i++)
            {
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
                    if (home.Combat.TryGetUnitPosition(id, out double2 p))
                    {
                        neverBlocked &= Passable(CellOf(p), NavConst.ClassHostile);
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
            PerfLines.Add($"{count} 个单位同一步请求 ~150 格长路线：{stepsToAll} 步内全部拿到路线；实际抽象搜索 {searches} 次、共享 {shared} 条；" +
                          $"主线程寻路流水线每步平均 {sumBegin / Math.Max(1, stepsToAll):F3} ms、最大 {maxBegin:F3} ms；采纳步被迫等工作线程 {late} 次");
            Expect(found && ids.Count == count && stepsToAll > 0 && stepsToAll <= 60 && searches < count / 4 && neverBlocked && maxBegin < 4.0,
                $"负向：{count} 个单位同时请求长路线——分批（每批条数上限 + 距离预算，共享的只计一次）在 {stepsToAll} 步（{stepsToAll / 60f:F2} 游戏秒）内全部拿到路线，" +
                $"抽象搜索只有 {searches} 次；主线程每步开销最大 {maxBegin:F3} ms（< 4 ms），全程不踏进不可通行格");
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
            // 走到有批次在飞、且有单位在沿路线走的时刻存档。
            int guard = 0;
            do
            {
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
            // A：不存档接着跑（存档就是 A 的“现在”）。
            WorldSimulation.StepMany(900);
            string a = Digest(s0);
            // B：读档接着跑同样的步数。
            RestoreSave(Slot);
            CampaignState s1 = CampaignSession.Current;
            WorldSimulation.StepMany(900);
            string b = Digest(s1);
            Expect(payload && inFlightAtSave && a == b,
                "有寻路批次在飞、机器沿路线赶路、突袭沿路线行进、据点巡逻、12 个突袭者要路线时存档：读档接着跑 900 步与不存档逐字段一致" +
                $"（家园内核哈希、寻路待处理状态、队伍 / 据点 / 巡逻记录）{(a == b ? string.Empty : "\n    A=" + a + "\n    B=" + b)}");
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
            od = WorldOutpostSystem.Outposts(s2).First();
            pd = WorldOutpostSystem.Patrols(s2).First();
            bool stillDormant = od.Dormant;
            WorldSimulation.StepMany(dormantSteps - dormantSteps / 2);
            long reinforcePointsCrossed = (GameClock.Ticks - 1 - (simTickFrozen - 1)) / interval;
            bool frozenWhileDormant = od.Dormant && od.SimTick == simTickFrozen && od.Garrison == garrisonFrozen;
            WorldOutpostSystem.WakeNow(s2, od.OutpostId, WorldOutpostSystem.WakeEvent);
            string dormantAtWake = SimFields(od) + "#" + SimFields(pd);
            WorldSimulation.StepMany(60 * 20);
            string dormantLater = SimFields(od) + "#" + SimFields(pd);

            // Peek 的值 = 对照组在同一步的真实值：再跑一遍对照组到同一步取值。
            (OutpostRecord oc2, PatrolRecord pc2) = SetupOutpost(seed, control: true, interval, cap);
            WorldSimulation.StepMany(dormantSteps / 2);
            controlGarrisonMid = oc2.Garrison;
            bool peekMatches = peekGarrison == oc2.Garrison && peekNext == oc2.NextReinforceTick
                               && Vector2.Distance(peekPatrol, new Vector2((float)pc2.PosX, (float)pc2.PosY)) < 1e-3f;
            Expect(dormantMid && stillDormant && frozenWhileDormant && sr.Success && controlAtWake == dormantAtWake && controlLater == dormantLater
                   && od.WakeCount == 1 && od.LastWakeReason == WorldOutpostSystem.WakeEvent && reinforcePointsCrossed >= 7 && oc.Garrison == cap,
                $"FGT-GEN-010 休眠与唤醒：据点离家园很远 → 休眠（{dormantSteps} 步里不推进，SimTick 冻结在 {simTickFrozen}），期间跨过 {reinforcePointsCrossed} 个增援点、中途存读档一次；" +
                $"事件唤醒后按确定性规则补算，与“一直在模拟”的对照组逐字段一致（驻军 {oc.Garrison}/{cap}、下次增援、累计增援、巡逻进度与位置），再跑 20 游戏秒仍一致" +
                $"{(controlAtWake == dormantAtWake ? string.Empty : "\n    对照=" + controlAtWake + "\n    休眠=" + dormantAtWake)}");
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
            SpawnHomeMachine(s, new Vector2(p.X + 16, p.Y + 4));
            int start = WorldOutpostSystem.WakesProcessed;
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
            Expect(allDormant && perStep.Sum() == 10 && perStep.All(n => n <= perStepCap) && perStep.Count >= 3 && WorldOutpostSystem.MaxWakeMs <= budget && ids.All(o => !o.Dormant),
                $"唤醒分帧：10 个据点同时符合唤醒条件，每步至多 {perStepCap} 个（{string.Join("+", perStep)}），单个唤醒最长 {WorldOutpostSystem.MaxWakeMs:F4} ms ≤ {budget} ms（FG17 休眠唤醒行）");
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
                PatrolRecord p = WorldOutpostSystem.Patrols(s).First();
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
            Expect(allOk && warm.Max() <= budget,
                $"FG15 长距离寻路：{cells} 格以上的路线（实测 {lens.Min():F0}～{lens.Max():F0} 格）在工作线程上 {warm.Max():F2} ms ≤ {budget} ms（热缓存）；冷启动最大 {cold.Max():F2} ms" +
                "（途经的区块第一次要按种子生成地形，见证据文件与 ADR）；路线每段都畅通");
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
