using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
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
    /// FG0-QA-01：后期规模性能场景与基线对比（FG15 FGR-SYS-041 规模预算、FGR-SYS-043 性能场景：每个里程碑出口跑一次，与上一次比较，退化超过 10% 视为未通过）。
    /// 场景由固定种子确定性搭出（同一版本的代码每次搭出同一个场景，不依赖固定坐标）：
    /// 800 座建筑、约 15,000 格传送带 / 30,000 件物品（在正式传送带内核里，随存档保存）、3,000 格管线（30 个流体网络，泵 / 储罐 / 阀门 / 消费者，v2 起，FG3-LOG-05）、
    /// 120 台机器、80 座炮塔 + 200 个突袭者、12 支行进中的突袭、每个表面 400 个区块。
    /// 搭好后走真实存档写入（性能测试存档），再走真实读档恢复，在读回来的局面上测量——存读档本身也是被测量的一项。
    /// 测量：世界步（热更层 + 战斗内核 + 传送带内核 + 寻路流水线）平均 / p95、各内核分项、每步托管分配、2,000 格寻路冷 / 热、存档耗时与大小、读档耗时、区块同步生成、托管堆。
    /// 基线：production/qa/perf-baseline/FG-PERF-LATE.json（入库；没有时本次结果写为首个基线）。基线来自另一台机器 / 另一 Unity 版本时只报告不判定。
    /// 用法：<c>bash tools/unity-perf-baseline.sh</c>（对比）/ <c>bash tools/unity-perf-baseline.sh --update</c>（对比后写入新基线）；或菜单“BinGames/性能基线/…”。
    /// 场景里还没有的（静默夜 FG7-ENV-02、沙暴 FG7-ENV-03）在报告里逐项写明，见 ADR-ARC-016 与 DEBT-FG0QA01-*（管线已于 FG3-LOG-05 加入，场景 v2）。
    /// </summary>
    public static class FgPerfBaseline
    {
        public const string SceneId = "FG-PERF-LATE";
        /// <summary>场景搭法的版本：改了场景内容（数量、布局、种子）就 +1，旧基线只报告不判定。</summary>
        public const int SceneVersion = 2;
        public const int Seed = 42;
        public const double RegressionLimit = 0.10;

        // FGR-SYS-041 规模预算（后期）。
        public const int BuildingTarget = 800;
        public const int BeltCellTarget = 15000;
        public const int BeltItemTarget = 30000;
        /// <summary>FG3-LOG-05（FG03 第 7 节 / FGR-SYS-041）：管线 3,000 格。</summary>
        public const int PipeCellTarget = 3000;
        public const int MachineTarget = 120;
        public const int TurretTarget = 80;
        public const int EnemyTarget = 200;
        public const int ProjectileTarget = 1500;
        public const int ChunkTarget = 400;
        public const int TransitTarget = 12;

        private const string PerfBuildingType = "generator_2";
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const int WarmSteps = 300;
        private const int Rounds = 3;
        private const int StepsPerRound = 600;
        private const int AllocSteps = 600;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static BaselineFile _last;

        /// <summary>一项测量。<see cref="gated"/> = 参与 10% 退化判定（数值越小越好）；<see cref="floor"/> = 绝对容差（增量不超过它不算退化，压住毫秒级噪声）。</summary>
        [Serializable]
        public sealed class Metric
        {
            public string id;
            public string name;
            public string unit;
            public double value;
            public bool gated;
            public double floor;
        }

        [Serializable]
        public sealed class BaselineFile
        {
            public string scene;
            public int sceneVersion;
            public int seed;
            public string machine;
            public string unity;
            public string label;
            public string createdUtc;
            public List<Metric> metrics = new List<Metric>();
        }

        [MenuItem("BinGames/性能基线/运行并与基线对比（FG-PERF-LATE）")]
        public static void RunFromMenu() => RunAndReport(Env("BINGAMES_PERF_UPDATE") == "1");

        [MenuItem("BinGames/性能基线/运行、对比并写入新基线")]
        public static void RunAndUpdateFromMenu() => RunAndReport(true);

        private static void RunAndReport(bool update)
        {
            var report = new StringBuilder();
            string baseline = Env("BINGAMES_PERF_BASELINE");
            if (string.IsNullOrEmpty(baseline))
            {
                baseline = DefaultBaselinePath();
            }
            int fail = Run(report, update, baseline, Env("BINGAMES_PERF_LABEL") ?? string.Empty);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            string text = report.ToString();
            Debug.Log(text);
            string outPath = Env("BINGAMES_PERF_OUT");
            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
                    File.WriteAllText(outPath, text);
                    if (_last != null)
                    {
                        File.WriteAllText(Path.ChangeExtension(outPath, ".json"), JsonUtility.ToJson(_last, true));
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[性能基线] 写报告失败：" + e.Message);
                }
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        /// <summary>搭场景、测量、与 <paramref name="baselinePath"/> 对比；<paramref name="update"/> = 对比后把本次结果写为新基线。返回失败项数。</summary>
        public static int Run(StringBuilder report, bool update, string baselinePath, string label)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            _last = null;
            Line($"\n[性能基线] 后期规模性能场景 {SceneId} v{SceneVersion}（FG0-QA-01；FG15 FGR-SYS-041 / 042 / 043）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgperf-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 1f / 60f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                var cur = new BaselineFile
                {
                    scene = SceneId,
                    sceneVersion = SceneVersion,
                    seed = Seed,
                    machine = MachineKey(),
                    unity = Application.unityVersion,
                    label = label,
                    createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                _last = cur;
                Line($"  · 环境：{cur.machine}，batchmode={Application.isBatchMode}。数字是 Editor 下的：内核是 Burst（与真机同为 AOT），" +
                     "热更层是 Editor Mono JIT（真机是 HybridCLR 解释执行，另测：FG15-SYS-02）；没有 GPU 帧时间（-nographics，见 FgCombatPerfProbe）");
                Line($"  · 基线文件：{baselinePath}");

                MeasureLongRoutes(cur);
                if (BuildAndSave(cur) && RestoreAndMeasure(cur))
                {
                    Compare(cur, baselinePath, update);
                }
            }
            catch (Exception e)
            {
                Fail($"性能基线抛异常：{e}");
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
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                UiEscapeStack.Clear();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
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
            Line($"  · [性能基线] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 1. 长距离寻路（独立内核，与寻路自检同一测法，场景种子）──────────────────────

        private static void MeasureLongRoutes(BaselineFile cur)
        {
            CampaignState s = NewCampaign();
            HomeGridMap map = HomeGridService.MapFor(s);
            var src = map.TerrainSource as WorldTerrainSource;
            if (src == null)
            {
                Fail("寻路：地形来源不是世界生成器");
                return;
            }
            int cells = (int)Math.Round(NavService.Tuning("nav.perf.route_cells", 2000f));
            GridCell core = HomeGridService.CorePivot(s);
            var warm = new List<double>();
            var cold = new List<double>();
            float minLen = float.MaxValue;
            bool allOk = true;
            // 先用一条很短的请求把寻路作业编译好（Editor 下 Burst 首次调度是同步编译）：否则第一条“冷启动”量到的是编译时间，随 Burst 缓存冷热跳变。
            using (var burstWarm = new NavKernel(NavService.ConfigFromTuning(), NavService.TerrainTable(), true, src.Params, src.Rects, src.Zones))
            {
                burstWarm.FindNow(new NavRequest
                {
                    OwnerTag = 9,
                    OwnerKey = 99,
                    Serial = 1,
                    Class = NavConst.ClassPlayer,
                    Flags = NavRequestFlags.None,
                    Start = new int2(core.X + 8, core.Y + 8),
                    Goal = new int2(core.X + 14, core.Y + 8),
                }, new List<int2>(), onWorker: true, out _);
            }
            foreach (int dir in new[] { 0, 1 })
            {
                using var k = new NavKernel(NavService.ConfigFromTuning(), NavService.TerrainTable(), true, src.Params, src.Rects, src.Zones);
                var goal = new GridCell(core.X + (dir == 0 ? cells + 40 : -40), core.Y + (dir == 0 ? 60 : cells + 40));
                for (int r = 0; r < 64 && !k.Passable(goal.X, goal.Y, NavConst.ClassPlayer); r++)
                {
                    goal = new GridCell(goal.X + 1, goal.Y + (r % 2));
                }
                var pts = new List<int2>();
                var req = new NavRequest
                {
                    OwnerTag = 9,
                    OwnerKey = 1 + dir,
                    Serial = 1,
                    Class = NavConst.ClassPlayer,
                    Flags = NavRequestFlags.None,
                    Start = new int2(core.X + 8, core.Y + 8),
                    Goal = new int2(goal.X, goal.Y),
                };
                NavResult rc = k.FindNow(req, pts, onWorker: true, out double msCold);
                NavResult rw = k.FindNow(req, pts, onWorker: true, out double msWarm);
                allOk &= rc.Status == NavStatus.Ok && rw.Status == NavStatus.Ok && rw.Length >= cells;
                cold.Add(msCold);
                warm.Add(msWarm);
                minLen = Math.Min(minLen, rw.Length);
            }
            Expect(allOk, $"2,000 格寻路两条都找到了（最短 {minLen:F0} 格）");
            Add(cur, "nav_2000_warm_ms", "2,000 格寻路（热缓存，工作线程，两条取最大）", "ms", warm.Max(), true, 0.5);
            Add(cur, "nav_2000_cold_ms", "2,000 格寻路（冷启动：途经区块当场生成，两条取最大）", "ms", cold.Max(), true, 2.0);
        }

        // ── 2. 搭场景并写性能测试存档 ─────────────────────────────────────────────

        private static CampaignState NewCampaign()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgperf-" + Seed, "Standard", Seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            return s;
        }

        private static bool BuildAndSave(BaselineFile cur)
        {
            CampaignState s = NewCampaign();
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (home == null || !home.IsLoaded || home.Combat == null || !BeltNetworkService.IsRunning || NavService.Kernel == null)
            {
                Fail("性能场景：家园 / 战斗内核 / 传送带内核 / 寻路内核没有载入");
                return false;
            }
            WorldView.Observe(HomeValleyLayout.RegionId);
            GridCell core = HomeGridService.CorePivot(s);
            var build = Stopwatch.StartNew();

            // 建筑：核心南面按 4 格间距铺 3×3 的建筑（只铺在可通行、无占用的地面上；按行列顺序，确定性）。直接写已建成的记录——
            // 与格网自检的 800 座规模测试同一做法：测的是“有 800 座建筑时的每步开销”，放置 / 工单流程由各自的自检覆盖。
            int buildings = AddBuildings(s, core, BuildingTarget);

            // 机器：核心周围由近及远找空地，补到 120 台（含开局机器），正常参与工单引擎。
            int machines = SpawnMachines(s, core, MachineTarget);

            // 传送带：正式传送带内核（随存档保存），北面约 15,000 格 / 30,000 件，布局同传送带自检的规模测试。
            BeltKernel belts = BeltNetworkService.Kernel;
            FgBeltKernelSelfCheck.BuildHuge(belts, core.X - 160, core.Y + 48);
            belts.EnsureTopology();
            BeltNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
            int beltCells = belts.CellCount;
            int beltItems = belts.ItemCount;

            // 管线（FG3-LOG-05，场景 v2）：正式管线内核（随存档保存），核心西面 30 个流体网络共 3,000 格（泵、储罐、阀门、两级管线、按优先级的消费者）。
            PipeKernel pipes = PipeNetworkService.Kernel;
            FgPipeSelfCheck.BuildPerfNetworks(pipes, core.X - 700, core.Y + 48, 30);
            PipeNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
            int pipeCells = pipes.CellCount;

            // 战斗：家园东面一块开阔地，80 座炮塔一圈 + 200 个突袭者四路压上（战斗性能场景的数值）。
            Vector2? center = FindCombatCenter(core);
            if (center == null)
            {
                Fail("性能场景：核心东面 300～420 格内找不到开阔地放战斗场景");
                return false;
            }
            CombatBench.SpawnPerfScenario(home.Combat, center.Value, EnemyTarget, TurretTarget, CombatBench.PerfSpec());

            // 12 支行进中的突袭：从各领地轮流派出（远征队属于 FG8-EXP-02，现在只有突袭一种行进队伍）。
            WorldPlan plan = WorldGenService.PlanFor(s);
            int raids = 0;
            string raidFailure = null;
            for (int i = 0; plan != null && plan.Territories.Count > 0 && i < TransitTarget * 2 && raids < TransitTarget; i++)
            {
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, plan.Territories[i % plan.Territories.Count].Id, 8, out string failure);
                if (g != null)
                {
                    raids++;
                }
                else
                {
                    raidFailure ??= failure;
                }
            }
            WorldSimulation.StepMany(2);
            int turrets = home.Combat.Kernel.CountAlive(CombatFaction.Player, CombatUnitKind.Turret);
            int enemies = home.Combat.Kernel.CountAlive(CombatFaction.Hostile, CombatUnitKind.Enemy);
            build.Stop();

            Expect(buildings >= BuildingTarget && machines >= MachineTarget && beltCells >= BeltCellTarget && beltItems >= BeltItemTarget && pipeCells >= PipeCellTarget
                   && turrets >= TurretTarget && enemies >= EnemyTarget && raids >= TransitTarget,
                $"场景规模达到 FGR-SYS-041：建筑 +{buildings}（共 {s.BuildingRecords.Length}）、机器 {machines}、传送带 {beltCells:N0} 格 / {beltItems:N0} 件、管线 {pipeCells:N0} 格、" +
                $"炮塔 {turrets}、突袭者 {enemies}、行进中的突袭 {raids} 支{(raidFailure != null && raids < TransitTarget ? "（派遣失败：" + raidFailure + "）" : string.Empty)}；搭建 {build.Elapsed.TotalSeconds:F1} 秒");

            // 预热：让战斗打起来、工单引擎与寻路进入稳态，再写存档（性能测试存档是“正在打”的局面）。
            WorldSimulation.StepMany(WarmSteps);

            var sw = Stopwatch.StartNew();
            SaveResult saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            sw.Stop();
            if (!saved.Success)
            {
                Fail("性能测试存档写入失败：" + saved.Message);
                return false;
            }
            long bytes = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            Add(cur, "save_ms", "写性能测试存档（整局同步 + 序列化 + 写盘）", "ms", sw.Elapsed.TotalMilliseconds, true, 20);
            Add(cur, "save_kb", "性能测试存档大小", "KB", bytes / 1024.0, true, 16);
            return true;
        }

        private static int AddBuildings(CampaignState s, GridCell core, int target)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            BuildingGrid g = GridContent.Building(PerfBuildingType);
            if (g == null)
            {
                Fail("性能场景：建筑表里没有 " + PerfBuildingType);
                return 0;
            }
            HomeValleyLayout.PowerProfile.TryGetValue(PerfBuildingType, out (float, int) power);
            HomeValleyLayout.BuildProfile.TryGetValue(PerfBuildingType, out (int ScrapCost, float Seconds) profile);
            var cells = new List<GridCell>(g.FootprintW * g.FootprintH);
            var extra = new List<BuildingRecord>(target);
            for (int row = 0; row < 120 && extra.Count < target; row++)
            {
                for (int col = 0; col < 60 && extra.Count < target; col++)
                {
                    var pivot = new GridCell(core.X - 118 + col * 4, core.Y - 44 - row * 4);
                    cells.Clear();
                    GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, 0, cells);
                    bool ok = true;
                    foreach (GridCell c in cells)
                    {
                        if (!Passable(c) || map.OccupantAt(c) != null)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok)
                    {
                        continue;
                    }
                    extra.Add(new BuildingRecord
                    {
                        BuildingId = HomeGridService.Prefix + PerfBuildingType + "#" + s.Grid.NextInstanceSerial++,
                        BuildingTypeId = PerfBuildingType,
                        RegionId = HomeValleyLayout.RegionId,
                        Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                        Rotation = 0f,
                        GridX = pivot.X,
                        GridY = pivot.Y,
                        Health = 100f,
                        ConstructionState = BuildingConstructionState.Operational,
                        PowerPriority = power.Item2 > 0 ? power.Item2 : 1,
                        PowerState = BuildingPowerState.NotApplicable,
                        Inventory = Array.Empty<CargoEntry>(),
                        QueueIds = Array.Empty<string>(),
                        InvestedScrap = profile.ScrapCost,
                    });
                }
            }
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Concat(extra).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return extra.Count;
        }

        private static int SpawnMachines(CampaignState s, GridCell core, int target)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            var spots = new List<GridCell>();
            for (int y = -36; y <= 36; y += 3)
            {
                for (int x = -36; x <= 36; x += 3)
                {
                    spots.Add(new GridCell(core.X + x, core.Y + y));
                }
            }
            spots.Sort((a, b) =>
            {
                int da = (a.X - core.X) * (a.X - core.X) + (a.Y - core.Y) * (a.Y - core.Y);
                int db = (b.X - core.X) * (b.X - core.X) + (b.Y - core.Y) * (b.Y - core.Y);
                return da != db ? da.CompareTo(db) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);
            });
            int count = MachineRegistry.RecordCount;
            foreach (GridCell c in spots)
            {
                if (count >= target)
                {
                    break;
                }
                bool inCore = c.X >= cmin.X - 2 && c.X <= cmax.X + 2 && c.Y >= cmin.Y - 2 && c.Y <= cmax.Y + 2;
                if (inCore || !Passable(c) || map.OccupantAt(c) != null)
                {
                    continue;
                }
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId,
                    new Vector2(c.X, c.Y), 100f, 100f);
                if (!r.Success)
                {
                    Fail("性能场景：登记机器失败：" + r.Message);
                    break;
                }
                count++;
            }
            WorldSimulation.StepMany(2);
            return MachineRegistry.RecordCount;
        }

        /// <summary>核心东面 300～420 格找一块开阔地：中心与四路突袭者的出发点周围都可通行（按种子的地形，不写死坐标；确定性）。</summary>
        private static Vector2? FindCombatCenter(GridCell core)
        {
            for (int d = 300; d <= 420; d += 6)
            {
                for (int a = -4; a <= 4; a++)
                {
                    double ang = a * Math.PI / 16;
                    var c = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * d), core.Y + (int)Math.Round(Math.Sin(ang) * d));
                    bool ok = Open(c, 6);
                    for (int g = 0; g < 4 && ok; g++)
                    {
                        float ga = g * Mathf.PI * 0.5f + 0.4f;
                        ok = Open(new GridCell(c.X + Mathf.RoundToInt(Mathf.Cos(ga) * 45f), c.Y + Mathf.RoundToInt(Mathf.Sin(ga) * 45f)), 3);
                    }
                    if (ok)
                    {
                        return new Vector2(c.X, c.Y);
                    }
                }
            }
            return null;
        }

        private static bool Open(GridCell c, int half)
        {
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    if (!Passable(new GridCell(c.X + x, c.Y + y)) || !Passable(new GridCell(c.X + x, c.Y + y), NavConst.ClassHostile))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool Passable(GridCell c, int cls = NavConst.ClassPlayer) => NavService.Kernel != null && NavService.Kernel.Passable(c.X, c.Y, cls);

        // ── 3. 读性能测试存档并在读回的局面上测量 ─────────────────────────────────

        private static bool RestoreAndMeasure(BaselineFile cur)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            var sw = Stopwatch.StartNew();
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读性能测试存档失败：" + r.Message);
                return false;
            }
            CampaignSession.Set(RunSlot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            sw.Stop();
            if (home == null || !home.IsLoaded || home.Combat == null || !BeltNetworkService.IsRunning)
            {
                Fail("读性能测试存档后家园 / 战斗内核 / 传送带内核没有载入");
                return false;
            }
            WorldView.Observe(HomeValleyLayout.RegionId);
            Add(cur, "restore_ms", "读性能测试存档（恢复 + 载入家园）", "ms", sw.Elapsed.TotalMilliseconds, true, 50);

            // 每个表面 400 个区块（FG17 第 7 节）：核心周围 20×20 区块同步生成（读档后的格网是新建的）。
            CampaignState s = CampaignSession.Current;
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            ChunkAddress a = GridMath.Address(core, map.ChunkSize);
            int before = map.LoadedChunkCount;
            sw.Restart();
            for (int dy = -10; dy < 10; dy++)
            {
                for (int dx = -10; dx < 10; dx++)
                {
                    map.ChunkAt(new GridCell((a.ChunkX + dx) * map.ChunkSize, (a.ChunkY + dy) * map.ChunkSize), out _);
                }
            }
            sw.Stop();
            int generated = map.LoadedChunkCount - before;
            if (generated > 0)
            {
                Add(cur, "chunk_load_ms", "区块同步生成（每块）", "ms", sw.Elapsed.TotalMilliseconds / generated, true, 0.2);
            }

            WorldSimulation.StepMany(120);
            CombatKernel ck = home.Combat.Kernel;
            BeltKernel bk = BeltNetworkService.Kernel;
            int buildings = s.BuildingRecords?.Length ?? 0;
            int machines = home.LiveMachineCount;
            int turrets = ck.CountAlive(CombatFaction.Player, CombatUnitKind.Turret);
            int enemies = ck.CountAlive(CombatFaction.Hostile, CombatUnitKind.Enemy);
            int beltCells = bk.CellCount;
            int beltItems = bk.ItemCount;
            PipeKernel pk = PipeNetworkService.Kernel;
            int pipeCells = pk?.CellCount ?? 0;
            int chunks = map.LoadedChunkCount;
            int transit = s.Raids?.InTransit?.Count(g => g != null && g.State == TransitGroupState.Marching) ?? 0;

            var samples = new List<double>(Rounds * StepsPerRound);
            var roundAvg = new List<double>(Rounds);
            double combatSum = 0;
            int combatSteps = 0;
            double beltSum = 0;
            int beltSteps = 0;
            double pipeSum = 0;
            int pipeSteps = 0;
            double navSum = 0;
            int peakProj = 0;
            var step = new Stopwatch();
            GC.Collect(); // 计时轮之前先收一次：别让前面搭场景留下的垃圾在测量中途触发 GC，把 p95 打穿。
            for (int round = 0; round < Rounds; round++)
            {
                double sum = 0;
                for (int i = 0; i < StepsPerRound; i++)
                {
                    long c0 = ck.Steps;
                    long b0 = BeltNetworkService.KernelStepsThisSession;
                    long p0 = PipeNetworkService.KernelStepsThisSession;
                    step.Restart();
                    WorldSimulation.StepMany(1);
                    step.Stop();
                    double ms = step.Elapsed.TotalMilliseconds;
                    samples.Add(ms);
                    sum += ms;
                    if (ck.Steps > c0)
                    {
                        combatSum += ck.LastStepMs;
                        combatSteps++;
                    }
                    if (BeltNetworkService.KernelStepsThisSession > b0)
                    {
                        beltSum += bk.LastStepMs;
                        beltSteps++;
                    }
                    if (pk != null && PipeNetworkService.KernelStepsThisSession > p0)
                    {
                        pipeSum += pk.LastStepMs;
                        pipeSteps++;
                    }
                    navSum += NavService.LastBeginStepMs;
                    peakProj = Math.Max(peakProj, ck.ProjectileCount);
                }
                roundAvg.Add(sum / StepsPerRound);
            }
            int n = samples.Count;
            roundAvg.Sort();
            samples.Sort();
            double worldAvg = roundAvg[roundAvg.Count / 2];
            double worldP95 = samples[(int)(n * 0.95)];
            double combatPerStep = combatSum / n;
            double beltPerStep = beltSum / n;
            double pipePerStep = pipeSum / n;
            double hotfixPerStep = Math.Max(0, samples.Average() - combatPerStep - beltPerStep - pipePerStep);

            // 托管分配：稳态步的托管堆增量（Unity Mono 不实现按线程分配计数，用托管堆已用字节，同传送带 / 数据管线自检）。
            // 堆增量只在窗口里没有发生 GC 时才有意义（GC 会把增量吃掉，分配越多越容易触发、读数反而越小——假绿）：
            // 切成若干个窗口，丢掉发生过 GC 的窗口，只用干净窗口；干净窗口太少 = 分配多到频繁触发 GC，判失败而不是拿偏低的数去比基线。
            const int allocWindows = 6;
            int windowSteps = Math.Max(1, AllocSteps / allocWindows);
            long cleanBytes = 0;
            int cleanSteps = 0;
            int dirtyWindows = 0;
            for (int wnd = 0; wnd < allocWindows; wnd++)
            {
                GC.Collect();
                int gc0 = GC.CollectionCount(0);
                long heap0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                for (int i = 0; i < windowSteps; i++)
                {
                    WorldSimulation.StepMany(1);
                }
                long heap1 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                if (GC.CollectionCount(0) != gc0)
                {
                    dirtyWindows++;
                    continue;
                }
                cleanBytes += Math.Max(0, heap1 - heap0);
                cleanSteps += windowSteps;
            }
            bool allocValid = allocWindows - dirtyWindows >= allocWindows / 2;
            double allocPerStep = cleanSteps > 0 ? cleanBytes / (double)cleanSteps : 0;
            GC.Collect();
            double monoMb = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024.0 * 1024.0);

            Add(cur, "world_step_avg_ms", "世界步平均（3 轮 × 600 步取中位轮）", "ms", worldAvg, true, 0.2);
            Add(cur, "world_step_p95_ms", "世界步 p95", "ms", worldP95, true, 0.3);
            Add(cur, "hotfix_step_ms", "热更层每步（世界步 − 战斗内核 − 传送带内核 − 管线内核）", "ms", hotfixPerStep, true, 0.2);
            Add(cur, "combat_kernel_ms", "战斗内核每步", "ms", combatSteps > 0 ? combatSum / combatSteps : 0, true, 0.2);
            Add(cur, "belt_kernel_ms", "传送带内核每个内核步（20 Hz）", "ms", beltSteps > 0 ? beltSum / beltSteps : 0, true, 0.2);
            Add(cur, "pipe_kernel_ms", "管线内核每个内核步（20 Hz）", "ms", pipeSteps > 0 ? pipeSum / pipeSteps : 0, true, 0.1);
            Add(cur, "nav_main_ms", "寻路主线程流水线每步", "ms", navSum / n, true, 0.1);
            Add(cur, "alloc_bytes_per_step", "每步托管堆增量（不含发生 GC 的窗口）", "B", allocPerStep, true, 64);
            Add(cur, "alloc_gc_windows", $"分配测量中发生 GC 的窗口（共 {allocWindows} 个）", "个", dirtyWindows, true, 1);
            Add(cur, "mono_used_mb", "托管堆已用（GC 后，含编辑器）", "MB", monoMb, true, 32);
            Add(cur, "scale_buildings", "建筑", "座", buildings, false, 0);
            Add(cur, "scale_machines", "机器（战斗内核里活着的）", "台", machines, false, 0);
            Add(cur, "scale_belt_cells", "传送带", "格", beltCells, false, 0);
            Add(cur, "scale_belt_items", "传送带上的物品", "件", beltItems, false, 0);
            Add(cur, "scale_pipe_cells", "管线", "格", pipeCells, false, 0);
            Add(cur, "scale_turrets", "炮塔", "座", turrets, false, 0);
            Add(cur, "scale_enemies", "同时存在的敌人", "个", enemies, false, 0);
            Add(cur, "scale_projectiles_peak", "同时存在的弹体（测量期间峰值）", "枚", peakProj, false, 0);
            Add(cur, "scale_chunks", "表面已加载区块", "块", chunks, false, 0);
            Add(cur, "scale_transit", "行进中的突袭", "支", transit, false, 0);

            Line($"  · 读回的局面：建筑 {buildings}、机器 {machines}、传送带 {beltCells:N0} 格 / {beltItems:N0} 件、炮塔 {turrets}、敌人 {enemies}、弹体峰值 {peakProj:N0}、" +
                 $"区块 {chunks}、行进中的突袭 {transit} 支");
            Line($"  · 世界步：平均 {worldAvg:F3} ms / p95 {worldP95:F3} ms；其中战斗内核 {combatPerStep:F3}、传送带内核 {beltPerStep:F3}、管线内核 {pipePerStep:F4}（折到每世界步）、" +
                 $"寻路主线程 {navSum / n:F3}、热更层其余 {hotfixPerStep:F3} ms；每步托管堆增量 {allocPerStep:F1} B" +
                 $"{(dirtyWindows > 0 ? $"（{allocWindows} 个窗口里 {dirtyWindows} 个发生过 GC，不计入）" : string.Empty)}；托管堆 {monoMb:F0} MB");
            Line($"  · 预算参照（FGR-SYS-042，推荐配置 60 帧、1x 一帧一步；Editor 数字不等于真机，不判定）：热更层 {hotfixPerStep:F2} / 4 ms{Over(hotfixPerStep, 4)}；" +
                 $"内核（物流 + 战斗）{combatPerStep + beltPerStep + pipePerStep:F2} / 6 ms{Over(combatPerStep + beltPerStep + pipePerStep, 6)}；每帧托管分配 {allocPerStep:F0} B（目标接近 0）");
            Line("  · 场景里还没有的（不判定，见 DEBT-FG0QA01-*）：沙暴、静默夜（FG7-ENV-03 / 02）；远征队（FG8-EXP-02，行进队伍目前只有突袭）；GPU 帧时间（-nographics）");

            Expect(buildings >= BuildingTarget && machines >= MachineTarget && beltCells >= BeltCellTarget && pipeCells >= PipeCellTarget && turrets >= TurretTarget
                   && chunks >= ChunkTarget && transit >= TransitTarget,
                $"读档后规模不丢：建筑 {buildings}、机器 {machines}、传送带 {beltCells:N0} 格、管线 {pipeCells:N0} 格、炮塔 {turrets}、区块 {chunks}、行进中的突袭 {transit} 支（都达到 FGR-SYS-041）");
            Expect(enemies >= EnemyTarget * 3 / 4 && peakProj >= ProjectileTarget,
                $"战斗在持续：测量开始时敌人 {enemies}（开打后会有伤亡，≥ {EnemyTarget * 3 / 4}），测量期间弹体峰值 {peakProj:N0}（≥ {ProjectileTarget:N0}）");
            Expect(allocValid,
                $"托管分配读数有效：{allocWindows} 个 {windowSteps} 步的窗口里 {allocWindows - dirtyWindows} 个没有发生 GC（至少一半；否则分配多到频繁触发 GC，堆增量读数偏低不能拿去比基线）");
            return true;
        }

        private static string Over(double v, double budget) => v > budget ? "（⚠ 超出）" : string.Empty;

        // ── 4. 与基线对比 ─────────────────────────────────────────────────────────

        private static void Compare(BaselineFile cur, string path, bool update)
        {
            BaselineFile old = null;
            try
            {
                if (File.Exists(path))
                {
                    old = JsonUtility.FromJson<BaselineFile>(File.ReadAllText(path));
                }
            }
            catch (Exception e)
            {
                Fail($"基线文件读不了（{e.Message}）：修好或删掉后重跑（删掉 = 本次写为首个基线）");
                return;
            }
            // 本次测量已有失败（规模没达标、寻路没找到、战斗没打起来……）：这份数字不能当基线——写进去以后所有对比都建立在坏基线上。
            int failedBeforeCompare = _fail;
            if (old == null || old.metrics == null || old.metrics.Count == 0)
            {
                if (failedBeforeCompare > 0)
                {
                    Fail($"没有基线，但本次测量有 {failedBeforeCompare} 项失败：不写首个基线（修好场景后重跑）");
                    return;
                }
                WriteBaseline(cur, path);
                Line("  · 没有基线：本次结果已写为首个基线。把它提交进仓库，下一个里程碑出口与它对比");
                return;
            }
            if (old.scene != cur.scene || old.sceneVersion != cur.sceneVersion || old.seed != cur.seed)
            {
                Line($"  · 场景变了（基线 {old.scene} v{old.sceneVersion} 种子 {old.seed}，当前 {cur.scene} v{cur.sceneVersion} 种子 {cur.seed}）：只报告不判定");
                ReportOnly(cur, old);
            }
            else if (old.machine != cur.machine)
            {
                Line($"  · 基线来自另一台机器 / 另一 Unity 版本（{old.machine}），只报告不判定；在本机跑一次“写入新基线”后才能对比");
                ReportOnly(cur, old);
            }
            else
            {
                Line($"  · 对比基线（{old.createdUtc}{(string.IsNullOrEmpty(old.label) ? string.Empty : "，" + old.label)}）：退化超过 {RegressionLimit:P0} 且超出绝对容差即未通过");
                foreach (Metric m in cur.metrics)
                {
                    Metric b = old.metrics.FirstOrDefault(x => x.id == m.id);
                    if (b == null)
                    {
                        Line($"  · 新指标（基线里没有）：{m.name} {m.value:0.###} {m.unit}");
                        continue;
                    }
                    string text = $"{m.name}：{b.value:0.###} → {m.value:0.###} {m.unit}（{Delta(m.value, b.value)}）";
                    if (!m.gated)
                    {
                        Expect(m.value >= b.value * 0.9, "规模 " + text);
                        continue;
                    }
                    bool regressed = m.value > b.value * (1 + RegressionLimit) && m.value - b.value > m.floor;
                    Expect(!regressed, text + (regressed ? $"，退化超过 {RegressionLimit:P0}" : string.Empty));
                }
                // 基线里有、这次却没有的指标（例如这次没生成新区块，区块生成耗时就没量）：静默跳过等于少比一项。
                foreach (Metric b in old.metrics)
                {
                    if (!cur.metrics.Any(x => x.id == b.id))
                    {
                        Fail($"基线里的指标这次没有量到：{b.name}（{b.id}）——场景或测量流程变了，确认后用 --update 重写基线");
                    }
                }
            }
            if (update)
            {
                if (_fail > 0)
                {
                    Fail($"本次有 {_fail} 项失败：不按 --update 写入新基线");
                    return;
                }
                WriteBaseline(cur, path);
                Line("  · 已按要求写入新基线（提交进仓库，下一个里程碑出口与它对比）");
            }
        }

        private static void ReportOnly(BaselineFile cur, BaselineFile old)
        {
            foreach (Metric m in cur.metrics)
            {
                Metric b = old.metrics.FirstOrDefault(x => x.id == m.id);
                Line(b == null
                    ? $"    {m.name}：{m.value:0.###} {m.unit}（基线里没有）"
                    : $"    {m.name}：{b.value:0.###} → {m.value:0.###} {m.unit}（{Delta(m.value, b.value)}）");
            }
        }

        private static string Delta(double v, double b) => b > 0 ? ((v - b) / b).ToString("+0.0%;-0.0%;0%") : "基线为 0";

        private static void WriteBaseline(BaselineFile cur, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                File.WriteAllText(path, JsonUtility.ToJson(cur, true));
            }
            catch (Exception e)
            {
                Fail($"写基线失败（{path}）：{e.Message}");
            }
        }

        // ── 工具 ────────────────────────────────────────────────────────────────────

        private static void Add(BaselineFile f, string id, string name, string unit, double value, bool gated, double floor) =>
            f.metrics.Add(new Metric { id = id, name = name, unit = unit, value = Math.Round(value, 4), gated = gated, floor = floor });

        private static string MachineKey() =>
            $"{SystemInfo.processorType.Trim()} × {SystemInfo.processorCount} 线程 | Unity {Application.unityVersion} | Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}" +
            $" | {(Application.isBatchMode ? "batchmode" : "编辑器菜单")}";

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>仓库根（有 tools/cell_tables/gen_all.py 的目录）下的 production/qa/perf-baseline/FG-PERF-LATE.json；
        /// 从工程目录往上找（真工程在 TEngine/UnityProject，影子工程在仓库根下的 .unity-validate-clone）。找不到就放临时目录并在报告里说明。</summary>
        private static string DefaultBaselinePath()
        {
            string dir = Path.GetDirectoryName(Application.dataPath);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "tools", "cell_tables", "gen_all.py")))
                {
                    return Path.Combine(dir, "production", "qa", "perf-baseline", SceneId + ".json");
                }
                dir = Path.GetDirectoryName(dir);
            }
            return Path.Combine(Path.GetTempPath(), "bingames-" + SceneId + ".json");
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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
