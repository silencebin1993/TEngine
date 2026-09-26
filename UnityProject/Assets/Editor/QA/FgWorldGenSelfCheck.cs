using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using Luban;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-05 世界生成与区块流式加载的自动验收（FG17 FGR-GEN-001～003、010、011、020、050～052、060～062；FG14 FGR-ARC-013、014；
    /// FGT-GEN-001、002、005～007；FG14 第 4 节通过标准）。全部走真实生产入口（WorldTerrainSource / WorldGenKernel 的 Burst 任务、
    /// WorldChunkStreamer、HomeGridService、HomeValleyBuildMode 的叠加层、CampaignSaveService 真读写文件、暂停菜单真 UXML），行为坏了会失败：
    /// A 数据：四张表与源数据逐字段一致；check_luban R17～R20 自测真跑；文本中英齐全。
    /// B 确定性（FGT-GEN-001）：同一（种子, 版本, 设置）按正序 / 倒序 / 乱序 / 流式四种访问顺序逐格一致；Burst 工作线程 = Burst 主线程 = 托管；
    ///   两个存档同一种子世界相同；换种子 / 换世界设置结果不同；逐格采样 = 整块生成。
    /// C 随机流分离（FGT-GEN-002）：多打一场仗（真实攻击入口 + 消耗玩法随机流）后，还没生成的区块与规划层不变；反过来生成地形也不改变玩法随机序列。
    /// D 表面（FGR-GEN-010、011）：星球无限、室内有限（墙、入口、越界不存在）；表面种子互相独立；新增星球不改变已有表面、不破坏存档；
    ///   不认识的表面的差异原样保留。
    /// E 规划层（FGR-GEN-020、021、090 原型）：50 个种子下四个阵营领地两两夹角 ≥ 60°、互不重叠、不进家园区、距离在范围内；确定性；
    ///   不进存档、读档重算相同；约束收紧时局部重试 / 兜底确定且写日志；领地内部与危害带的污染。
    /// F 坐标（FGR-GEN-051）：区块索引 + 区块内偏移（负数、±1,000,000）；相对原点区块的画面位置精度；超出上限给原因、流式加载不越界。
    /// G 流式加载与性能（FGR-GEN-050、FGT-GEN-007）：工作线程真的在后台跑；单区块生成 / 主线程接入耗时；快速平移与远距离飞跃时主线程
    ///   每帧开销、占位出现后消失、流式路径从不同步生成；常驻上限回收纯地形区块、回收后重生成相同；已修改 / 有建筑的区块不回收。
    /// H 差异存档（FGR-GEN-060、062，FGT-GEN-005）：只保存被修改的区块；区块边界两侧、负坐标、跨区块建筑存读档逐字段一致；改回原样不存；
    ///   未加载区块的差异原样写回；体积随修改面积增长、与探索面积无关；损坏的差异读档拒绝且不改文件。
    /// I 生成器版本（FGR-GEN-061，FGT-GEN-006）：v1 与原型（v0）回归哈希；改旧版本参数会被发现；旧版本存档用旧路径生成未修改区块；
    ///   更新的生成器版本读档拒绝（Newer）。
    /// J 暂停 / 倍速 / 后台：暂停与 0.5x / 1x / 2x 下流式加载结果相同；观察（流式 + 叠加层）与不观察（同步查询）数据逐字节一致。
    /// K 叠加层与建造栏：镜头周围按区块分块；没生成好显示占位与“正在生成地形”；生成后贴图按格网数据画出；跟随镜头；地形修改后重画。
    /// L 暂停菜单：世界种子、一键复制、世界设置（真 UXML）。
    /// M 活跃分级（FGR-GEN-052 第 1、2 条）与原生内存成对释放。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgWorldGenSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;

        [MenuItem("BinGames/自检：FG 世界生成")]
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
            Line("\n[世界生成] 世界生成与区块流式加载（FG0-ARCH-05）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgworld-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，Burst 编译={Unity.Burst.BurstCompiler.IsEnabled}，" +
                     $"工作线程 {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount} 个，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）");

                Step(CheckData);
                Step(CheckDeterminism);
                Step(CheckStreamSeparation);
                Step(CheckSurfaces);
                Step(CheckPlanLayer);
                Step(CheckCoordinates);
                Step(CheckStreamingAndPerformance);
                Step(CheckDiffSave);
                Step(CheckGeneratorVersions);
                Step(CheckGenerationInputsFrozen);
                Step(CheckDiffOrderAndSaveCard);
                Step(CheckPauseSpeedAndBackground);
                Step(CheckOverlayAndHud);
                Step(CheckPauseMenu);
                Step(CheckActivityAndLeaks);
            }
            catch (Exception e)
            {
                Fail($"世界生成自检抛异常：{e}");
            }
            finally
            {
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                GameSettings.SetLanguage(originalLanguage);
                StrategyClock.Reset();
                InputRouter.SetBuildMode(false);
                UiEscapeStack.Clear();
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
            return _fail;
        }

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
            finally
            {
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
            }
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>经家园控制器的真实播种入口建一个新战役的家园（与 FgGridBuildSelfCheck 同一入口）。</summary>
        private static CampaignState NewHome(int seed, bool withHauler = true)
        {
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgworld-" + seed, "Standard", seed);
            MethodInfo seedMethod = typeof(HomeValleyController).GetMethod("EnsureRegionSeeded", BindingFlags.NonPublic | BindingFlags.Static);
            seedMethod.Invoke(null, new object[] { s });
            s.Scrap = 500;
            if (withHauler)
            {
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                    new Vector2(-10f, -6f), 100f, 100f);
            }
            CampaignSession.Set(0, s);
            return s;
        }

        /// <summary>只有世界域的新战役（不播种家园）。</summary>
        private static CampaignState WorldState(int seed, string presetId = WorldGenContent.DefaultPresetId)
        {
            CampaignState s = CampaignState.CreateNew("fgworld-w" + seed, "Standard", seed);
            CampaignFgStateDomains.EnsureAll(s);
            s.World.WorldSettingsId = presetId;
            return s;
        }

        private static HomeGridMap FreshMap(CampaignState s, string surface = WorldGenContent.EarthSurfaceId) =>
            new HomeGridMap(GridContent.TuningInt("grid.chunk_size"), WorldGenService.CreateSource(s, surface), surface);

        private static ulong HashOf(IGridTerrainSource src, int cx, int cy)
        {
            int n = GridContent.TuningInt("grid.chunk_size");
            var t = new byte[n * n];
            var p = new byte[n * n];
            src.FillChunk(cx, cy, n, t, p);
            return WorldGenKernel.Hash64(t, p);
        }

        private static ulong HashOf(HomeGridMap.Chunk c) => WorldGenKernel.Hash64(c.Terrain, c.Pollution);

        private static Dictionary<long, (byte[] t, byte[] p)> Snapshot(HomeGridMap map) =>
            map.LoadedChunks.ToDictionary(c => HomeGridMap.Key(c.ChunkX, c.ChunkY), c => ((byte[])c.Terrain.Clone(), (byte[])c.Pollution.Clone()));

        // ── A. 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Expect(WorldGenContent.LoadError == null && WorldGenContent.Versions.Count >= 1 && WorldGenContent.TryGetVersion(WorldGenVersions.Current, out _)
                   && WorldGenContent.TryGetPreset(WorldGenVersions.Current, WorldGenContent.DefaultPresetId, out _) && WorldGenContent.TryGetSurface(WorldGenContent.EarthSurfaceId, out _)
                   && WorldGenContent.Territories.Count == 5,
                $"四张世界生成表已加载：生成器版本 {WorldGenContent.Versions.Count}（当前 v{WorldGenVersions.Current}）、世界设置 {WorldGenContent.Presets.Count}、表面 {WorldGenContent.Surfaces.Count}、领地 {WorldGenContent.Territories.Count}");
            Expect(WorldGenContent.Versions.Select(v => v.Version).OrderBy(v => v).SequenceEqual(Enumerable.Range(1, WorldGenContent.Versions.Count))
                   && WorldGenContent.Versions.Max(v => v.Version) == WorldGenVersions.Current,
                "生成器版本表从 v1 连续编号，最高版本 = 代码里的当前版本（升版本必须两边一起改）");

            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            if (code != 0)
            {
                Fail($"fgdata.py --dump 失败：{Tail(output)}");
                return;
            }
            var diffs = new List<string>();
            int rows = 0;
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length < 2)
                {
                    continue;
                }
                switch (f[0])
                {
                    case "WV":
                        rows++;
                        if (!WorldGenContent.TryGetVersion(int.Parse(f[1]), out WorldGenVersion v)
                            || !Eq(v.FeatureScale, f[2]) || !Eq(v.CliffThreshold, f[3]) || !Eq(v.WaterThreshold, f[4]) || !Eq(v.OreThreshold, f[5])
                            || !Eq(v.RareBias, f[6]) || !Eq(v.OilBias, f[7]) || !Eq(v.PollutionThreshold, f[8]) || !Eq(v.PollutionScale, f[9])
                            || v.PollutionDistanceStart != int.Parse(f[10]) || v.PollutionDistancePerLevel != int.Parse(f[11]) || v.ResourceDistancePerStep != int.Parse(f[12])
                            || !Eq(v.ResourceDistanceStep, f[13]) || v.ResourceDistanceMaxSteps != int.Parse(f[14]) || v.TerritoryPollutionBonus != int.Parse(f[15])
                            || v.HazardPollution != int.Parse(f[16]) || v.ProtectRadius != int.Parse(f[17]) || v.ProtectMargin != int.Parse(f[18])
                            || v.AnchorProtectRadius != int.Parse(f[19]) || v.InteriorPillarSpacing != int.Parse(f[20]) || !Eq(v.InteriorRuinThreshold, f[21])
                            || v.HomeZoneRadius != int.Parse(f[22]) || v.TerritoryMinAngle != int.Parse(f[23]) || v.PlanMaxAttempts != int.Parse(f[24])
                            || v.TerritorySet != f[25] || v.PresetSet != f[26])
                        {
                            diffs.Add("生成器版本 " + f[1]);
                        }
                        break;
                    case "WP":
                        rows++;
                        WorldPreset p = WorldGenContent.Presets.FirstOrDefault(x => x.Key == f[1]);
                        if (p == null || p.Set != f[2] || p.Id != f[3] || p.NameKey != f[4] || !Eq(p.ResourceAbundance, f[5])
                            || !Eq(p.PollutionIntensity, f[6]) || !Eq(p.TerritoryDistanceScale, f[7]) || p.StartZone != f[8])
                        {
                            diffs.Add("世界设置 " + f[1]);
                        }
                        break;
                    case "WS":
                        rows++;
                        if (!WorldGenContent.TryGetSurface(f[1], out Surface s) || s.Kind != f[2] || s.NameKey != f[3] || s.Salt != int.Parse(f[4])
                            || s.WidthChunks != int.Parse(f[5]) || s.HeightChunks != int.Parse(f[6]))
                        {
                            diffs.Add("表面 " + f[1]);
                        }
                        break;
                    case "WT":
                        rows++;
                        Territory t = WorldGenContent.Territories.FirstOrDefault(x => x.Key == f[1]);
                        if (t == null || t.Set != f[2] || t.Id != f[3] || t.NameKey != f[4] || t.Act != int.Parse(f[5]) || t.Faction != int.Parse(f[6])
                            || t.MinDistance != int.Parse(f[7]) || t.MaxDistance != int.Parse(f[8]) || t.Radius != int.Parse(f[9]) || t.HazardKind != f[10]
                            || t.HazardWidth != int.Parse(f[11]) || t.HazardKey != f[12])
                        {
                            diffs.Add("领地 " + f[1]);
                        }
                        break;
                }
            }
            int runtimeRows = WorldGenContent.Versions.Count + WorldGenContent.Presets.Count + WorldGenContent.Surfaces.Count + WorldGenContent.Territories.Count;
            Expect(diffs.Count == 0 && rows == runtimeRows && rows >= 10,
                $"源数据 fgdata_world.py 与运行时四张表逐字段一致（{rows} 行 / 运行时 {runtimeRows} 行）{(diffs.Count == 0 ? string.Empty : "——不一致：" + string.Join("，", diffs))}");

            (code, output) = RunPython(root, "tools/cell_tables/check_luban.py --selftest");
            Expect(code == 0 && output.Contains("生成器版本不连续") && output.Contains("领地必然压进家园区") && output.Contains("表面种子盐重复")
                   && output.Contains("缺默认世界设置") && output.Contains("版本引用的领地集合不存在") && output.Contains("区块边长被改（已冻结）"),
                $"check_luban 世界生成规则（R17～R21，含版本引用集合与区块边长冻结）自测真跑：{Tail(output)}");

            var keys = new[] { "ui.world.generating", "grid.reason.world_limit", "ui.pause.world_seed", "ui.pause.copy_seed", "ui.pause.seed_copied",
                "ui.pause.world_settings", "world.surface.earth.name", "world.territory.silent.name", "world.preset.default.name", "world.legacy_terrain", "save.card.world" };
            var bad = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                      || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            Expect(bad.Count == 0, $"世界生成的界面与原因文本中英两列齐全（{keys.Length} 条）{string.Join(",", bad)}");
            Expect(GridContent.TuningInt("world.pregen_margin_chunks") == 2 && GridContent.TuningInt("grid.chunk_size") == 32
                   && GridContent.TuningInt("world.coord_limit") == 1000000 && WorldGenContent.Version(1).TerritoryMinAngle == 60
                   && WorldGenContent.Version(1).HomeZoneRadius == 200 && WorldGenContent.Version(1).TerritorySet == "t1" && WorldGenContent.Version(1).PresetSet == "p1",
                "FG17 第 10 节初值入表：区块 32×32、预生成边距 2 个区块、坐标上限 ±1,000,000；规划层参数（家园区 200 格、领地最小夹角 60°）与领地 / 世界设置集合（t1 / p1）在 v1 版本行里（随版本走）");
        }

        // ── B. 确定性（FGT-GEN-001）────────────────────────────────────────────────

        private static void CheckDeterminism()
        {
            CampaignState s = WorldState(424242);
            var region = new List<(int cx, int cy)>();
            for (int cy = -3; cy <= 3; cy++)
            {
                for (int cx = -3; cx <= 3; cx++)
                {
                    region.Add((cx, cy));
                }
            }
            region.Add((37, -12));
            region.Add((-900, 450));
            region.Add((31000, -31000));

            HomeGridMap forward = FreshMap(s);
            foreach ((int cx, int cy) in region)
            {
                forward.ChunkAt(new GridCell(cx * 32, cy * 32), out _);
            }
            HomeGridMap backward = FreshMap(s);
            foreach ((int cx, int cy) in Enumerable.Reverse(region))
            {
                backward.ChunkAt(new GridCell(cx * 32 + 31, cy * 32 + 31), out _);
            }
            HomeGridMap shuffled = FreshMap(s);
            var rng = new System.Random(7);
            foreach ((int cx, int cy) in region.OrderBy(_ => rng.Next()))
            {
                shuffled.ChunkAt(new GridCell(cx * 32 + 5, cy * 32 + 9), out _);
            }
            // 流式：镜头依次经过这些区块，结果经工作线程 → 主线程接入。
            HomeGridMap streamed = FreshMap(s);
            var streamer = new WorldChunkStreamer(streamed);
            try
            {
                foreach ((int cx, int cy) in region.OrderBy(r => -r.cx * 7 + r.cy))
                {
                    streamer.DrainForTests(new GridCell(cx * 32 + 16, cy * 32 + 16), 2000);
                }
            }
            finally
            {
                streamer.Dispose();
            }
            int mismatch = 0;
            foreach ((int cx, int cy) in region)
            {
                ulong h = HashOf(forward.TryGetLoaded(cx, cy));
                if (HashOf(backward.TryGetLoaded(cx, cy)) != h || HashOf(shuffled.TryGetLoaded(cx, cy)) != h
                    || streamed.TryGetLoaded(cx, cy) == null || HashOf(streamed.TryGetLoaded(cx, cy)) != h)
                {
                    mismatch++;
                }
            }
            Expect(mismatch == 0 && streamed.SyncGeneratedCount == 0 && streamed.AdoptedCount > region.Count,
                $"FGT-GEN-001：同一（种子, 版本, 设置）{region.Count} 个区块（含负坐标与 ±31,000 区块处）按正序 / 倒序 / 乱序 / 流式（工作线程）四种访问顺序逐格一致" +
                $"（不一致 {mismatch}；流式接入 {streamed.AdoptedCount} 块、同步生成 {streamed.SyncGeneratedCount} 块）");

            // Burst 工作线程 = Burst 主线程 = 托管 Execute（整数定点，与平台 / 编译模式无关）。
            var src = (WorldTerrainSource)FreshMap(s).TerrainSource;
            int n = 32 * 32;
            int pathMismatch = 0;
            foreach ((int cx, int cy) in new[] { (0, 0), (-1, -1), (5, -8), (-700, 300), (31249, -31249) })
            {
                var t1 = new byte[n];
                var p1 = new byte[n];
                var t2 = new byte[n];
                var p2 = new byte[n];
                var t3 = new byte[n];
                var p3 = new byte[n];
                src.FillChunk(cx, cy, 32, t1, p1);
                src.FillChunkManaged(cx, cy, t2, p2);
                WorldGenJob job = src.Schedule(cx, cy, 0);
                WorldGenKernel.Kick();
                job.CopyResult(t3, p3);
                job.Release();
                if (!t1.SequenceEqual(t2) || !p1.SequenceEqual(p2) || !t1.SequenceEqual(t3) || !p1.SequenceEqual(p3))
                {
                    pathMismatch++;
                }
            }
            Expect(pathMismatch == 0, $"同一区块经 Burst 工作线程、Burst 主线程（Run）、托管 Execute 三条路径逐字节一致（不一致 {pathMismatch} / 5）");

            // 逐格采样 = 整块生成。
            HomeGridMap.Chunk c0 = forward.TryGetLoaded(2, -1);
            int sampleBad = 0;
            for (int i = 0; i < 64; i++)
            {
                int lx = (i * 7) % 32;
                int ly = (i * 13) % 32;
                src.Sample(2 * 32 + lx, -32 + ly, out byte tt, out byte pp);
                if (tt != c0.Terrain[ly * 32 + lx] || pp != c0.Pollution[ly * 32 + lx])
                {
                    sampleBad++;
                }
            }
            Expect(sampleBad == 0, $"逐格采样与整块生成一致（抽查 64 格，不一致 {sampleBad}）");

            // 两个存档用同一个种子：世界完全相同；换种子不同。
            HomeGridMap twin = FreshMap(WorldState(424242));
            HomeGridMap other = FreshMap(WorldState(424243));
            int twinBad = 0;
            int otherDiff = 0;
            foreach ((int cx, int cy) in region.Take(20))
            {
                ulong h = HashOf(forward.TryGetLoaded(cx, cy));
                twinBad += HashOf(twin.ChunkAt(new GridCell(cx * 32, cy * 32), out _)) == h ? 0 : 1;
                otherDiff += HashOf(other.ChunkAt(new GridCell(cx * 32, cy * 32), out _)) != h ? 1 : 0;
            }
            Expect(twinBad == 0 && otherDiff >= 15, $"两个战役用同一个种子：20 个区块全部相同（不同 {twinBad}）；换种子后 {otherDiff}/20 个区块不同");

            // 世界设置是生成身份的一部分：注入一个资源丰富的预设，结果不同；default 不受影响。
            WorldGenContent.OverrideForTests(presets: new TbWorldPreset(PresetBuf(("default", "world.preset.default.name", 1f, 1f, 1f, "standard"),
                ("rich", "world.preset.default.name", 2.5f, 1f, 1f, "standard"))));
            HomeGridMap rich = FreshMap(WorldState(424242, "rich"));
            HomeGridMap stillDefault = FreshMap(WorldState(424242));
            int richDiff = 0;
            int defaultSame = 0;
            int oreDefault = 0;
            int oreRich = 0;
            byte ore = GridContent.TerrainCode("ore_metal");
            foreach ((int cx, int cy) in region.Take(49))
            {
                HomeGridMap.Chunk a = forward.TryGetLoaded(cx, cy);
                HomeGridMap.Chunk b = rich.ChunkAt(new GridCell(cx * 32, cy * 32), out _);
                richDiff += HashOf(b) != HashOf(a) ? 1 : 0;
                defaultSame += HashOf(stillDefault.ChunkAt(new GridCell(cx * 32, cy * 32), out _)) == HashOf(a) ? 1 : 0;
                oreDefault += a.Terrain.Count(x => x == ore);
                oreRich += b.Terrain.Count(x => x == ore);
            }
            WorldGenContent.ResetForTests();
            Expect(richDiff > 20 && defaultSame == 49 && oreRich > oreDefault,
                $"世界设置属于生成身份：资源丰度 2.5 的预设改变了 {richDiff}/49 个区块、金属矿脉 {oreDefault} → {oreRich} 格；default 预设 49 个区块不变（{defaultSame}）");
        }

        // ── C. 随机流分离（FGT-GEN-002）──────────────────────────────────────────────

        private static void CheckStreamSeparation()
        {
            const int seed = 90210;
            var unvisited = new[] { (9, 9), (-14, 3), (120, -77), (-3000, 2500) };
            // 基准：一个新战役，没打过仗，直接生成这些区块。
            CampaignState fresh = NewHome(seed);
            HomeGridMap baseMap = HomeGridService.MapFor(fresh);
            Dictionary<(int, int), ulong> before = unvisited.ToDictionary(k => k, k => HashOf(baseMap.ChunkAt(new GridCell(k.Item1 * 32, k.Item2 * 32), out _)));
            string planBefore = WorldGenService.PlanFor(fresh).Fingerprint();

            // 同一种子的另一个战役：先“打一场仗”——经真实攻击入口反复攻击低威胁靶，并大量消耗玩法随机流，推进游戏时长与事件账本。
            CampaignState fought = NewHome(seed);
            HomeValleyFactory.EnsureBlueprintsSeeded(fought);
            HomeValleyCombatTargets.EnsureSeeded(fought);
            MachineOpResult attacker = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                new Vector2(-18f, 12f), 100f, 100f);
            BlueprintRecord combatBp = fought.BlueprintRecords.First(b => b.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
            GameLogic.Campaign.Blueprint.MachineLoadoutRegistry.Register(fought, attacker.LogicId, combatBp.BlueprintId, combatBp.ActiveVersion);
            int hits = 0;
            for (int i = 0; i < 40; i++)
            {
                HomeValleyCombatTargets.HitResult r = HomeValleyCombatTargets.TryAttack(fought, attacker.LogicId, HomeValleyCombatTargets.LowThreatTargetId,
                    fought.RandomSeed + i, isAiSource: false);
                hits += r.Success ? 1 : 0;
                CombatTargetRecord target = HomeValleyCombatTargets.Find(fought, HomeValleyCombatTargets.LowThreatTargetId);
                if (target != null && target.Health <= 0f)
                {
                    target.Health = target.MaxHealth; // 靶子打爆后直接复活，继续打（跳过 30 秒再生冷却）
                }
            }
            GameLogic.Campaign.Blueprint.MachineLoadoutRegistry.Clear();
            System.Random gameplay = CampaignRandomService.CreateRng(fought);
            long drawn = 0;
            for (int i = 0; i < 20000; i++)
            {
                drawn += gameplay.Next(1000);
            }
            fought.PlaySeconds += 1800f;
            fought.RandomSeed = unchecked(fought.RandomSeed * 31 + 7); // 玩法流被推进 / 重播种也不影响世界
            HomeGridService.Invalidate();
            HomeGridMap foughtMap = HomeGridService.MapFor(fought);
            int changed = 0;
            foreach ((int, int) k in unvisited)
            {
                changed += HashOf(foughtMap.ChunkAt(new GridCell(k.Item1 * 32, k.Item2 * 32), out _)) != before[k] ? 1 : 0;
            }
            string planAfter = WorldGenService.PlanFor(fought).Fingerprint();
            Expect(hits > 0 && changed == 0 && planAfter == planBefore,
                $"FGT-GEN-002：多打一场仗（{hits} 次命中、消耗玩法随机数 {drawn} 点、+30 分钟、玩法种子被推进）后，{unvisited.Length} 个还没去过的区块地形不变（变化 {changed}），规划层不变");

            // 反方向：生成大量地形不改变玩法随机序列。
            CampaignState g = NewHome(seed + 1);
            int[] seq1 = Enumerable.Range(0, 32).Select(_ => 0).ToArray();
            System.Random r1 = CampaignRandomService.CreateRng(g);
            for (int i = 0; i < seq1.Length; i++)
            {
                seq1[i] = r1.Next();
            }
            HomeGridMap gm = HomeGridService.MapFor(g);
            for (int i = 0; i < 100; i++)
            {
                gm.ChunkAt(new GridCell(i * 97, -i * 61), out _);
            }
            System.Random r2 = CampaignRandomService.CreateRng(g);
            bool same = seq1.All(v => v == r2.Next());
            Expect(same, "反过来：生成 100 个区块后，战役的玩法随机序列与生成前逐个相同（世界生成不读也不推进玩法随机流）");
        }

        // ── D. 表面（FGR-GEN-010、011 / FGR-ARC-014）──────────────────────────────────

        private static void CheckSurfaces()
        {
            CampaignState s = NewHome(5150);
            Surface hall = WorldGenContent.Surface("relay_hall");
            HomeGridMap earth = WorldGenService.MapFor(s, WorldGenContent.EarthSurfaceId);
            HomeGridMap relay = WorldGenService.MapFor(s, "relay_hall");
            HomeGridMap data = WorldGenService.MapFor(s, "data_center");
            byte cliff = GridContent.TerrainCode("cliff");
            int w = hall.WidthChunks * 32;
            int h = hall.HeightChunks * 32;
            bool walls = relay.GetTerrain(new GridCell(0, 5)) == cliff && relay.GetTerrain(new GridCell(w - 1, 5)) == cliff
                         && relay.GetTerrain(new GridCell(5, h - 1)) == cliff && relay.GetTerrain(new GridCell(w / 2, 0)) != cliff;
            bool bounded = !relay.TerrainSource.ChunkExists(-1, 0, 32) && !relay.TerrainSource.ChunkExists(hall.WidthChunks, 0, 32)
                           && relay.TerrainSource.ChunkExists(hall.WidthChunks - 1, hall.HeightChunks - 1, 32)
                           && earth.TerrainSource.ChunkExists(-30000, 30000, 32);
            Expect(walls && bounded && earth != relay && relay.SurfaceId == "relay_hall" && relay != data,
                $"FGR-GEN-010：地球是连续无限的星球（±30,000 区块处存在）；地下中继大厅是 {w}×{h} 格的有限室内表面（四周是墙、南墙正中是入口、越界区块不存在）；每个表面独立的区块存储");

            ulong e11 = HashOf(earth.ChunkAt(new GridCell(40, 40), out _));
            ulong r11 = HashOf(relay.ChunkAt(new GridCell(40, 40), out _));
            ulong d11 = HashOf(data.ChunkAt(new GridCell(40, 40), out _));
            Expect(e11 != r11 && r11 != d11, "每个表面有自己的种子（由世界种子与表面盐派生）：同一区块坐标在地球 / 中继大厅 / 数据中心生成结果互不相同");

            // 室内流式加载只生成表面内的区块。
            WorldChunkStreamer rs = WorldGenService.StreamerFor(s, "relay_hall");
            rs.DrainForTests(new GridCell(w / 2, h / 2));
            Expect(relay.LoadedChunkCount == hall.WidthChunks * hall.HeightChunks + 1 || relay.LoadedChunkCount == hall.WidthChunks * hall.HeightChunks,
                $"室内表面流式加载只生成表面内的区块：已加载 {relay.LoadedChunkCount}（表面 {hall.WidthChunks}×{hall.HeightChunks}）");
            WorldGenService.ShutdownInteriors();

            // 新增星球（候选里程碑 FG-X1）：只加一行表面，不改变已有表面的结果。
            ulong earthBefore = HashOf(WorldGenService.CreateSource(s, WorldGenContent.EarthSurfaceId), 3, -2);
            ulong relayBefore = HashOf(WorldGenService.CreateSource(s, "relay_hall"), 1, 1);
            var surfaces = WorldGenContent.Surfaces.Select(x => (x.Id, x.Kind, x.NameKey, x.Salt, x.WidthChunks, x.HeightChunks)).ToList();
            surfaces.Add(("mars", "planet", "world.surface.earth.name", 201, 0, 0));
            WorldGenContent.OverrideForTests(surfaces: new TbSurface(SurfaceBuf(surfaces)));
            ulong earthAfter = HashOf(WorldGenService.CreateSource(s, WorldGenContent.EarthSurfaceId), 3, -2);
            ulong relayAfter = HashOf(WorldGenService.CreateSource(s, "relay_hall"), 1, 1);
            ulong mars = HashOf(WorldGenService.CreateSource(s, "mars"), 3, -2);
            WorldGenContent.ResetForTests();
            Expect(earthBefore == earthAfter && relayBefore == relayAfter && mars != earthAfter,
                "FGR-GEN-011：表里新增一个星球表面后，地球与室内表面的生成结果不变；新星球有自己的地形");

            // 不认识的表面（例如以后版本新增的星球）的差异：读档不拒绝、存档原样写回。
            HomeGridService.MapFor(s);
            relay = WorldGenService.MapFor(s, "relay_hall");
            relay.SetTerrain(new GridCell(10, 10), GridContent.TerrainCode("water"));
            s.World.ChunkDiffs = s.World.ChunkDiffs.Append(new ChunkDiffRecord { SurfaceId = "future_planet", ChunkX = 4, ChunkY = -2, DiffPayload = "d9:whatever" }).ToArray();
            SaveResult saved = CampaignSaveService.Save(2, s, SaveReason.Manual);
            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(2);
            bool keptUnknown = loaded.Success && loaded.State.World.ChunkDiffs.Any(r => r.SurfaceId == "future_planet" && r.DiffPayload == "d9:whatever");
            byte relayCell = 0;
            byte earthCell = 0;
            if (loaded.Success)
            {
                CampaignSession.Set(0, loaded.State);
                relayCell = WorldGenService.MapFor(loaded.State, "relay_hall").GetTerrain(new GridCell(10, 10));
                earthCell = HomeGridService.MapFor(loaded.State).GetTerrain(new GridCell(10, 10));
                CampaignSaveService.Save(2, loaded.State, SaveReason.Manual);
            }
            LoadResult again = CampaignSaveService.Load(2);
            Expect(saved.Success && keptUnknown && relayCell == GridContent.TerrainCode("water") && earthCell == earth.GetTerrain(new GridCell(10, 10))
                   && again.Success && again.State.World.ChunkDiffs.Count(r => r.SurfaceId == "future_planet") == 1
                   && again.State.World.ChunkDiffs.Count(r => r.SurfaceId == "relay_hall") == 1,
                "存档：室内表面被修改的区块单独保存并恢复（地球同一坐标不受影响）；不认识的表面的差异读档不拒绝、再存档原样写回（加星球不破坏存档）");
            WorldGenService.Invalidate();
        }

        // ── E. 规划层（FGR-GEN-020、021、090 原型）────────────────────────────────────

        private static void CheckPlanLayer()
        {
            WorldPreset def = WorldGenContent.Preset(1, WorldGenContent.DefaultPresetId);
            int homeZone = V1().HomeZoneRadius;
            var problems = new List<string>();
            int retried = 0;
            for (int i = 0; i < 50; i++)
            {
                int seed = unchecked(i * 7919 + 17) * (i % 2 == 0 ? 1 : -1);
                WorldPlan p = WorldPlan.Compute(seed, V1(), def, 0, 0);
                WorldPlan p2 = WorldPlan.Compute(seed, V1(), def, 0, 0);
                if (p.Fingerprint() != p2.Fingerprint())
                {
                    problems.Add($"{seed}:不确定");
                }
                if (p.Failures.Count > 0)
                {
                    problems.Add($"{seed}:{p.Failures[0]}");
                }
                retried += p.Territories.Count(t => t.Attempts > 0);
                List<PlannedTerritory> factions = p.Territories.Where(t => t.IsFaction).ToList();
                if (factions.Count != 4 || p.Territories.Count != 5)
                {
                    problems.Add($"{seed}:数量 {factions.Count}/{p.Territories.Count}");
                }
                for (int a = 0; a < p.Territories.Count; a++)
                {
                    PlannedTerritory ta = p.Territories[a];
                    Territory row = WorldGenContent.Territories.First(x => x.Id == ta.Id);
                    int d = WorldGenMath.Isqrt((long)ta.CenterX * ta.CenterX + (long)ta.CenterY * ta.CenterY);
                    if (d - ta.OuterRadius <= homeZone)
                    {
                        problems.Add($"{seed}:{ta.Id} 进入家园区");
                    }
                    int lo = Math.Max(row.MinDistance, homeZone + ta.OuterRadius + 1);
                    if (d < lo - 2 || d > Math.Max(lo, row.MaxDistance) + 2)
                    {
                        problems.Add($"{seed}:{ta.Id} 距离 {d} 不在 {lo}～{row.MaxDistance}");
                    }
                    for (int b = a + 1; b < p.Territories.Count; b++)
                    {
                        PlannedTerritory tb = p.Territories[b];
                        long dx = ta.CenterX - tb.CenterX;
                        long dy = ta.CenterY - tb.CenterY;
                        long need = ta.OuterRadius + tb.OuterRadius;
                        if (dx * dx + dy * dy < need * need)
                        {
                            problems.Add($"{seed}:{ta.Id}/{tb.Id} 重叠");
                        }
                        if (ta.IsFaction && tb.IsFaction && WorldPlan.AngleBetween(ta.AngleDeg, tb.AngleDeg) < 60)
                        {
                            problems.Add($"{seed}:{ta.Id}/{tb.Id} 夹角 {WorldPlan.AngleBetween(ta.AngleDeg, tb.AngleDeg)}°");
                        }
                    }
                }
            }
            Expect(problems.Count == 0,
                $"FGR-GEN-021：50 个种子（含负数）下四个阵营领地 + 白潮滩头预留区全部放下：阵营方向两两夹角 ≥ 60°、互不重叠（含危害带）、不进入家园区（核心 {homeZone} 格）、中心距离在表的范围内、同一种子两次计算相同" +
                $"（其中 {retried} 个领地用到了确定性局部重试）{(problems.Count == 0 ? string.Empty : "——" + string.Join("；", problems.Take(5)))}");

            // 世界设置“领地距离”按比例缩放。
            WorldGenContent.OverrideForTests(presets: new TbWorldPreset(PresetBuf(("default", "world.preset.default.name", 1f, 1f, 1f, "standard"),
                ("far", "world.preset.default.name", 1f, 1f, 1.5f, "standard"))));
            WorldPlan near = WorldPlan.Compute(77, V1(), WorldGenContent.Preset(1, "default"), 0, 0);
            WorldPlan far = WorldPlan.Compute(77, V1(), WorldGenContent.Preset(1, "far"), 0, 0);
            WorldGenContent.ResetForTests();
            PlannedTerritory silentFar = far.Find("silent");
            Expect(silentFar != null && silentFar.Distance >= 600 && far.Find("overclock").Distance >= 2250 && near.Find("overclock").Distance <= 2000,
                $"世界设置“领地距离 ×1.5”：静默中心 {silentFar?.Distance} 格（≥ 600）、超频 {far.Find("overclock")?.Distance} 格（≥ 2250，标准下 {near.Find("overclock")?.Distance}）");

            // 约束收紧：局部重试与兜底都确定、写日志；约束无解时写失败日志而不是卡死。
            WorldGenContent.OverrideForTests(versions: new TbWorldGenVersion(VersionBuf(WorldGenContent.Versions.ToList(), ("territoryMinAngle", 85))));
            WorldPlan tightA = WorldPlan.Compute(31337, V1(), def, 0, 0);
            WorldPlan tightB = WorldPlan.Compute(31337, V1(), def, 0, 0);
            int tightRetries = 0;
            bool tightOk = true;
            for (int i = 0; i < 20; i++)
            {
                WorldPlan tp = WorldPlan.Compute(i * 131 + 5, V1(), def, 0, 0);
                tightRetries += tp.Territories.Count(t => t.Attempts > 0);
                List<PlannedTerritory> f = tp.Territories.Where(t => t.IsFaction).ToList();
                for (int a = 0; a < f.Count; a++)
                {
                    for (int b = a + 1; b < f.Count; b++)
                    {
                        tightOk &= tp.Failures.Count > 0 || WorldPlan.AngleBetween(f[a].AngleDeg, f[b].AngleDeg) >= 85;
                    }
                }
            }
            WorldGenContent.OverrideForTests(versions: new TbWorldGenVersion(VersionBuf(WorldGenContent.Versions.ToList(), ("territoryMinAngle", 100))));
            WorldPlan impossible = WorldPlan.Compute(31337, V1(), def, 0, 0);
            WorldPlan impossible2 = WorldPlan.Compute(31337, V1(), def, 0, 0);
            WorldGenContent.ResetForTests();
            Expect(tightA.Fingerprint() == tightB.Fingerprint() && tightRetries > 0 && tightOk && impossible.Failures.Count > 0
                   && impossible.Territories.Count == 5 && impossible.Fingerprint() == impossible2.Fingerprint() && impossible.Territories.Any(t => t.Fallback),
                $"FGR-GEN-090 原型：夹角收紧到 85° 时 20 个种子共 {tightRetries} 次确定性局部重试且全部满足约束；收紧到 100°（4×100 > 360 无解）时仍确定地放下 5 个领地并写失败日志（{impossible.Failures.FirstOrDefault()}）");

            // 不进存档、读档重算相同；领地内部与危害带的污染。
            CampaignState s = NewHome(2718);
            WorldPlan plan = WorldGenService.PlanFor(s);
            string fp = plan.Fingerprint();
            SaveResult saved = CampaignSaveService.Save(1, s, SaveReason.Manual);
            string json = File.ReadAllText(CampaignSaveService.SlotPath(1));
            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(1);
            string fp2 = loaded.Success ? WorldGenService.PlanFor(loaded.State).Fingerprint() : "读档失败";
            Expect(saved.Success && loaded.Success && fp2 == fp && !json.Contains("clarity") && !json.Contains("overclock") && !json.Contains("white_tide"),
                "规划层不进存档（文件里没有领地坐标），读档后按种子重算，与存档前逐项相同");
            PlannedTerritory clarity = plan.Find("clarity");
            PlannedTerritory silent = plan.Find("silent");
            HomeGridMap map = HomeGridService.MapFor(s);
            // 对照：同一种子 / 版本 / 设置，但不给规划层（没有领地与危害带）。
            var noPlan = new WorldTerrainSource(s.World.WorldSeed, s.World.GeneratorVersion, WorldGenContent.Preset(s.World.GeneratorVersion, s.World.WorldSettingsId),
                WorldGenContent.Surface(WorldGenContent.EarthSurfaceId), HomeGridService.CorePivot(s), 32, null);
            int ringAll3 = 0;
            int ringRaised = 0;
            bool ringInBand = true;
            for (int k = 0; k < 16; k++)
            {
                int ang = k * 360 / 16;
                int rr = clarity.Radius + clarity.HazardWidth / 2;
                int hx = clarity.CenterX + (int)(((long)rr * WorldPlan.Cos(ang)) >> 16);
                int hy = clarity.CenterY + (int)(((long)rr * WorldPlan.Cos(ang - 90)) >> 16);
                ringInBand &= plan.HazardAt(hx, hy)?.Id == "clarity";
                ringAll3 += map.GetPollution(new GridCell(hx, hy)) == 3 ? 1 : 0;
                noPlan.Sample(hx, hy, out _, out byte basePol);
                ringRaised += basePol < 3 ? 1 : 0;
            }
            int inside = 0;
            int insideRaised = 0;
            for (int k = 0; k < 64; k++)
            {
                int cx = silent.CenterX + (k % 8 - 4) * 20;
                int cy = silent.CenterY + (k / 8 - 4) * 20;
                if (plan.TerritoryAt(cx, cy)?.Id != "silent")
                {
                    continue;
                }
                inside++;
                noPlan.Sample(cx, cy, out _, out byte basePol);
                byte withPlan = map.GetPollution(new GridCell(cx, cy));
                insideRaised += withPlan == Math.Min(3, basePol + 1) ? 1 : 0;
            }
            PlannedTerritory at = plan.TerritoryAt(silent.CenterX, silent.CenterY);
            Expect(at?.Id == "silent" && ringInBand && ringAll3 == 16 && ringRaised > 0 && inside > 20 && insideRaised == inside && plan.TerritoryAt(0, 0) == null,
                $"规划层进入地形：澄净外圈酸沼带 16 个采样点污染全是 3 级（没有规划层时其中 {ringRaised} 个低于 3）；静默领地内 {inside} 个采样点污染都比没有领地时 +1 级（封顶 3）；核心不在任何领地");
        }

        // ── F. 坐标（FGR-GEN-051）──────────────────────────────────────────────────

        private static void CheckCoordinates()
        {
            WorldCoord a = WorldCoord.FromCell(new GridCell(-1, -32), 32);
            WorldCoord b = WorldCoord.FromCell(new GridCell(1000000, -1000000), 32);
            WorldCoord c = WorldCoord.FromCell(new GridCell(-1000000, 999999), 32);
            Expect(a.ChunkX == -1 && a.LocalX == 31 && a.ChunkY == -1 && a.LocalY == 0 && b.Cell(32) == new GridCell(1000000, -1000000)
                   && c.Cell(32) == new GridCell(-1000000, 999999) && b.ChunkX == 31250 && b.LocalX == 0,
                $"坐标 = 区块索引 + 区块内偏移：(-1,-32) → {a}；(1,000,000,-1,000,000) → {b}；往返精确");

            double wx = 999999.3;
            double wz = -999999.7;
            WorldCoord far = WorldCoord.FromWorld(wx, wz, 32);
            Vector3 rel = far.ToRenderPosition(far.ChunkX, far.ChunkY, 32);
            double expectX = wx - far.ChunkX * 32.0;
            double expectZ = wz - far.ChunkY * 32.0;
            double relErr = Math.Max(Math.Abs(rel.x - expectX), Math.Abs(rel.z - expectZ));
            float naive = (float)wx;
            double naiveErr = Math.Abs(naive - wx);
            Expect(relErr < 1e-3 && naiveErr > 1e-2,
                $"远处精度：({wx},{wz}) 相对所在区块的画面位置误差 {relErr:E1} 米；直接用单精度浮点表示误差 {naiveErr:F3} 米（不用区块坐标会抖动）");

            CampaignState s = NewHome(8080);
            int limit = GridContent.TuningInt("world.coord_limit");
            GridPlacementResult beyond = HomeGridService.ValidatePlacement(s, "generator_2", new GridCell(limit + 5, 0), 0);
            HomeGridMap map = HomeGridService.MapFor(s);
            int loadedBefore = map.LoadedChunkCount;
            HomeGridService.ValidatePlacement(s, "generator_2", new GridCell(limit + 5000, 0), 0);
            Expect(beyond.Has(GridBlockReason.WorldLimit) && beyond.Describe().Contains("超出世界范围") && beyond.Describe().Contains("1000000")
                   && map.LoadedChunkCount == loadedBefore,
                $"超出坐标上限：放置给出原因“{beyond.Describe()}”，而且不去生成那里的区块");

            var streamer = new WorldChunkStreamer(FreshMap(WorldState(8080)));
            try
            {
                streamer.DrainForTests(new GridCell(limit + 100000, limit + 100000), 500);
                bool within = streamer.Map.LoadedChunks.All(ch => (long)ch.ChunkX * 32 <= limit && (long)ch.ChunkY * 32 <= limit);
                Expect(within && streamer.Map.LoadedChunkCount > 0,
                    $"镜头焦点超过上限时，流式加载只生成上限内的区块（{streamer.Map.LoadedChunkCount} 块，全部在 ±{limit} 格内）");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        // ── G. 流式加载与性能（FGR-GEN-050、FGT-GEN-007）───────────────────────────────

        private static void CheckStreamingAndPerformance()
        {
            CampaignState s = WorldState(6061);
            var src = (WorldTerrainSource)WorldGenService.CreateSource(s, WorldGenContent.EarthSurfaceId);
            int n = 32 * 32;
            var t = new byte[n];
            var p = new byte[n];
            src.FillChunk(1000, 1000, 32, t, p); // 预热（Burst 同步编译）
            src.FillChunkManaged(1000, 1000, t, p);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 64; i++)
            {
                src.FillChunk(2000 + i, -3000, 32, t, p);
            }
            double burstMs = sw.Elapsed.TotalMilliseconds / 64.0;
            sw.Restart();
            for (int i = 0; i < 16; i++)
            {
                src.FillChunkManaged(2000 + i, -4000, t, p);
            }
            double managedMs = sw.Elapsed.TotalMilliseconds / 16.0;

            // 工作线程真的在后台跑：调度 16 个任务后主线程只轮询、不 Complete，任务自己完成。
            var jobs = new List<WorldGenJob>();
            for (int i = 0; i < 16; i++)
            {
                jobs.Add(src.Schedule(5000 + i, 5000, 0));
            }
            WorldGenKernel.Kick();
            sw.Restart();
            while (!jobs.All(j => j.IsCompleted) && sw.Elapsed.TotalMilliseconds < 3000)
            {
                System.Threading.Thread.Sleep(1);
            }
            double backgroundMs = sw.Elapsed.TotalMilliseconds;
            bool allDone = jobs.All(j => j.IsCompleted);
            foreach (WorldGenJob j in jobs)
            {
                j.Release();
            }
            Line($"  · 性能（Editor batchmode，影子工程，本机；真机 IL2CPP + Burst AOT 数字由 FG15-SYS-02 补）：单区块 32×32 生成 Burst {burstMs:F3} ms、托管 {managedMs:F3} ms；" +
                 $"16 个区块在工作线程后台完成用时 {backgroundMs:F1} ms（主线程只轮询）");
            Expect(burstMs <= 5.0, $"FG17 第 7 节：单个区块生成（Burst，与工作线程同一份编译代码）{burstMs:F3} ms ≤ 5 ms");
            Expect(allDone, $"生成任务在工作线程上完成：主线程不调用 Complete、只轮询，16 个任务 {backgroundMs:F1} ms 内全部完成");

            // 快速平移：镜头每帧移动 24 格（60 帧 ≈ 每秒 1,440 格），每帧 Tick 一次，帧间留 4 ms 给工作线程。
            HomeGridMap map = FreshMap(s);
            map.SetExplored(new[] { new ExploredAreaRecord { CenterX = 0, CenterY = 0, Radius = 40 } });
            var streamer = new WorldChunkStreamer(map);
            var frameMs = new List<double>();
            int pendingSeen = 0;
            try
            {
                streamer.DrainForTests(new GridCell(0, 0));
                streamer.ResetMetrics();
                int syncBefore = map.SyncGeneratedCount;
                for (int f = 0; f < 240; f++)
                {
                    var focus = new GridCell(f * 24, (f * 24) / 3);
                    streamer.Tick(focus);
                    frameMs.Add(streamer.LastTickMs);
                    pendingSeen = Math.Max(pendingSeen, streamer.PendingAround(focus, 2));
                    System.Threading.Thread.Sleep(4);
                }
                var endFocus = new GridCell(239 * 24, 239 * 24 / 3);
                int settle = 0;
                while (streamer.PendingAround(endFocus, 2) > 0 && settle < 400)
                {
                    streamer.Tick(endFocus);
                    System.Threading.Thread.Sleep(2);
                    settle++;
                }
                frameMs.Sort();
                double p95 = frameMs[(int)(frameMs.Count * 0.95)];
                double max = frameMs[frameMs.Count - 1];
                Line($"  · 快速平移 240 帧（每帧 24 格）：流式加载主线程每帧 p95 {p95:F3} ms、最大 {max:F3} ms；单区块主线程接入最大 {streamer.MaxIntegrateChunkMs:F3} ms、" +
                     $"平均 {(streamer.TotalIntegrated > 0 ? streamer.TotalIntegrateMs / streamer.TotalIntegrated : 0):F3} ms；接入 {streamer.TotalIntegrated} 块；停下后 {settle} 帧补齐");
                Expect(streamer.MaxIntegrateChunkMs <= 0.5, $"FG17 第 7 节：主线程接入一个区块最大 {streamer.MaxIntegrateChunkMs:F3} ms ≤ 0.5 ms");
                Expect(p95 <= 2.0 && max <= 8.0, $"快速平移不卡顿：流式加载主线程每帧 p95 {p95:F3} ms ≤ 2 ms、最大 {max:F3} ms ≤ 8 ms（半帧）");
                Expect(pendingSeen > 0 && streamer.PendingAround(endFocus, 2) == 0 && map.SyncGeneratedCount == syncBefore,
                    $"平移中镜头周围出现过“生成中”的区块（最多 {pendingSeen} 块），停下后 {settle} 帧内全部补齐；流式路径从不在主线程同步生成（同步生成 {map.SyncGeneratedCount - syncBefore} 块）");

                // 远距离飞跃：一帧从 (5760,1920) 跳到 (600000,-450000)。
                var flight = new GridCell(600000, -450000);
                streamer.ResetMetrics();
                streamer.Tick(flight);
                double flightTick = streamer.LastTickMs;
                int pendingAfterJump = streamer.PendingAround(flight, 2);
                int ticks = 0;
                while (streamer.PendingAround(flight, 2) > 0 && ticks < 400)
                {
                    System.Threading.Thread.Sleep(2);
                    streamer.Tick(flight);
                    ticks++;
                }
                Expect(flightTick <= 8.0 && pendingAfterJump > 0 && streamer.PendingAround(flight, 2) == 0 && streamer.MaxTickMs <= 8.0,
                    $"远距离飞跃（一帧跳 60 万格）：跳的那一帧主线程 {flightTick:F3} ms，落点 {pendingAfterJump} 个区块先显示“生成中”，{ticks} 帧后全部补齐（期间每帧最大 {streamer.MaxTickMs:F3} ms）");
            }
            finally
            {
                streamer.Dispose();
            }

            // 常驻上限：回收纯地形区块；回收后重新生成结果相同；已修改 / 有建筑的区块不回收。
            GridContent.OverrideForTests(tuning: new TbHomeTuning(TuningBuf(GridContent.Tuning, ("world.stream.max_resident_chunks", 64f))));
            CampaignState e = WorldState(6062);
            HomeGridMap emap = FreshMap(e);
            emap.SetExplored(new[] { new ExploredAreaRecord { CenterX = 0, CenterY = 0, Radius = 10 } });
            var es = new WorldChunkStreamer(emap);
            try
            {
                es.DrainForTests(new GridCell(0, 0));
                // 放在预生成区（探索半径 10 + 边距 2 = 区块 -3～2）之外：它们不受“需要集合”保护，只能靠回收规则保留。
                byte before = emap.GetPollution(new GridCell(330, 330));
                emap.SetPollution(new GridCell(330, 330), (byte)(before == 3 ? 0 : 3)); // 区块 (10,10) 已修改
                emap.SetBelt(new GridCell(-330, -330), 9);                              // 区块 (-11,-11) 有传送带
                ulong farHash = 0;
                for (int k = 0; k < 30; k++)
                {
                    es.DrainForTests(new GridCell(3000 + k * 200, -2000));
                    if (k == 0)
                    {
                        farHash = HashOf(emap.TryGetLoaded(93, -63));
                    }
                }
                bool keptModified = emap.TryGetLoaded(10, 10) != null && emap.StateOf(10, 10) == ChunkState.Modified;
                bool keptBelt = emap.TryGetLoaded(-11, -11) != null && emap.GetBelt(new GridCell(-330, -330)) == 9;
                bool evictedFar = emap.TryGetLoaded(93, -63) == null;
                ulong regenerated = HashOf(emap.ChunkAt(new GridCell(93 * 32, -63 * 32), out _));
                Expect(emap.EvictedCount > 0 && emap.LoadedChunkCount <= 64 + 64 + 25 && keptModified && keptBelt && evictedFar && regenerated == farHash && farHash != 0,
                    $"常驻上限 64 块：飞过 30 个远处后回收了 {emap.EvictedCount} 块纯地形区块（常驻 {emap.LoadedChunkCount}）；预生成区外的已修改区块与有传送带的区块保留；被回收的区块再访问时重新生成、哈希相同");
            }
            finally
            {
                es.Dispose();
                GridContent.ResetForTests();
            }
        }

        // ── H. 差异存档（FGR-GEN-060、062，FGT-GEN-005）─────────────────────────────────

        private static void CheckDiffSave()
        {
            CampaignState s = NewHome(31415);
            HomeGridMap map = HomeGridService.MapFor(s);
            WorldChunkStreamer streamer = HomeGridService.Streamer(s);
            streamer.DrainForTests(new GridCell(0, 0));
            byte water = GridContent.TerrainCode("water");
            byte ruin = GridContent.TerrainCode("ruin");
            // 区块边界两侧（x=31 | 32）、负坐标两侧（x=-1 | 0）、远处一格污染；另一个区块改了再改回（不应保存）。
            var edits = new[]
            {
                (new GridCell(31, 5), water), (new GridCell(32, 5), ruin), (new GridCell(-1, 7), water), (new GridCell(0, -33), ruin),
            };
            foreach ((GridCell c, byte v) in edits)
            {
                map.SetTerrain(c, v);
            }
            map.SetPollution(new GridCell(500, 500), 2);
            GridCell revert = new GridCell(100, 100);
            byte orig = map.GetTerrain(revert);
            map.SetTerrain(revert, orig == water ? ruin : water);
            map.SetTerrain(revert, orig);

            // 一座跨区块边界（x = 31 | 32）的建筑：放在枢轴格 x = 32，占地 31～33。
            GridCell? spanning = null;
            for (int y = -30; y <= 30 && spanning == null; y++)
            {
                if (HomeGridService.ValidatePlacement(s, "generator_2", new GridCell(32, y), 0).Ok)
                {
                    spanning = new GridCell(32, y);
                }
            }
            GridOpResult placed = spanning != null ? HomeGridService.TryPlace(s, "generator_2", spanning.Value, 0) : default;

            Dictionary<long, (byte[] t, byte[] p)> expect = Snapshot(map);
            Dictionary<GridCell, string> expectOcc = map.SnapshotOccupancy();
            SaveResult saved = CampaignSaveService.Save(0, s, SaveReason.Manual);
            var stateDiffs = s.World.ChunkDiffs.Where(r => r.SurfaceId == WorldGenContent.EarthSurfaceId).Select(r => (r.ChunkX, r.ChunkY)).OrderBy(k => k).ToList();
            // (0,-33) → 区块 (0,-2)；(-1,7) → (-1,0)；(31,5) → (0,0)；(32,5) → (1,0)；(500,500) → (15,15)
            var expectChunks = new List<(int, int)> { (-1, 0), (0, -2), (0, 0), (1, 0), (15, 15) }.OrderBy(k => k).ToList();
            Expect(saved.Success && stateDiffs.SequenceEqual(expectChunks) && map.StateOf(3, 3) == ChunkState.Generated,
                $"FGR-GEN-060：已加载 {map.LoadedChunkCount} 个区块，只保存被修改的 {stateDiffs.Count} 个（{string.Join(" ", stateDiffs)}）；改了又改回原样的区块 (3,3) 回到“未修改”、不保存");

            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(0);
            if (!loaded.Success)
            {
                Fail($"读档失败：{loaded.Message}");
                return;
            }
            CampaignSession.Set(0, loaded.State);
            HomeGridService.Streamer(loaded.State).DrainForTests(new GridCell(0, 0));

            // 没加载过的区块（远处 (15,15)）的差异：读档后不碰它，再存一次原样写回。
            string payload1 = s.World.ChunkDiffs.First(r => r.ChunkX == 15 && r.ChunkY == 15).DiffPayload;
            bool farUnloaded = !HomeGridService.MapFor(loaded.State).IsChunkLoaded(15, 15);
            CampaignSaveService.Save(0, loaded.State, SaveReason.Manual);
            string payload2 = loaded.State.World.ChunkDiffs.FirstOrDefault(r => r.ChunkX == 15 && r.ChunkY == 15)?.DiffPayload;
            Expect(farUnloaded && payload2 == payload1, "读档后没加载过的区块 (15,15)：差异原样保留、再存档写回（不因为没加载就丢）");

            int cellMismatch = 0;
            bool occSame = false;
            {
                HomeGridMap l = HomeGridService.MapFor(loaded.State);
                // 流式加载已把家园一片生成出来（接入时套差异），其余区块经同步查询生成（同样套差异），然后逐格比对。
                foreach (KeyValuePair<long, (byte[] t, byte[] p)> kv in expect)
                {
                    HomeGridMap.Unkey(kv.Key, out int cx, out int cy);
                    HomeGridMap.Chunk c = l.ChunkAt(new GridCell(cx * 32, cy * 32), out _);
                    if (!c.Terrain.SequenceEqual(kv.Value.t) || !c.Pollution.SequenceEqual(kv.Value.p))
                    {
                        cellMismatch++;
                    }
                }
                Dictionary<GridCell, string> gotOcc = l.SnapshotOccupancy();
                occSame = gotOcc.Count == expectOcc.Count && expectOcc.All(kv => gotOcc.TryGetValue(kv.Key, out string v) && v == kv.Value);
            }
            Expect(cellMismatch == 0 && occSame && placed.Success,
                $"FGT-GEN-005：读档后 {expect.Count} 个区块的地形 / 污染逐格一致（不一致 {cellMismatch}），占用层逐格一致（含跨区块边界 x=31|32 的建筑 {placed.BuildingId}、区块边界两侧与负坐标的修改）");


            // 体积随修改面积增长，与探索面积无关（FGR-GEN-062）。
            long sizeBase = new FileInfo(CampaignSaveService.SlotPath(0)).Length;
            WorldChunkStreamer ls = HomeGridService.Streamer(loaded.State);
            for (int k = 0; k < 10; k++)
            {
                ls.DrainForTests(new GridCell(k * 200, 4000)); // 探索约 250 个新区块
            }
            HomeGridMap lm = HomeGridService.MapFor(loaded.State);
            int explored = lm.LoadedChunkCount;
            CampaignSaveService.Save(0, loaded.State, SaveReason.Manual);
            long sizeExplored = new FileInfo(CampaignSaveService.SlotPath(0)).Length;
            for (int k = 0; k < 8; k++)
            {
                for (int i = 0; i < 1024; i++)
                {
                    lm.SetPollution(new GridCell(k * 200 + i % 32, 4000 + i / 32), 0); // 净化：远处原本是 3 级污染
                }
            }
            CampaignSaveService.Save(0, loaded.State, SaveReason.Manual);
            long sizeModified = new FileInfo(CampaignSaveService.SlotPath(0)).Length;
            Line($"  · 存档体积：基准 {sizeBase} 字节；多探索到 {explored} 个区块后 {sizeExplored} 字节（+{sizeExplored - sizeBase}）；再整块修改 8 个区块后 {sizeModified} 字节（+{sizeModified - sizeExplored}，约 {(sizeModified - sizeExplored) / 8} 字节 / 块）");
            Expect(Math.Abs(sizeExplored - sizeBase) < 2048 && sizeModified - sizeExplored > 8 * 2000,
                $"FGR-GEN-062：多探索 {explored} 个区块存档只变了 {sizeExplored - sizeBase} 字节（未修改的区块不存）；整块修改 8 个区块增加 {sizeModified - sizeExplored} 字节（随修改面积增长）");

            // 负向：已知表面的差异损坏 → 读档拒绝（正文损坏），原文件不改动；不认识的表面不校验。
            string file = File.ReadAllText(CampaignSaveService.SlotPath(0));
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(file);
            string badPayload = env.PayloadJson.Replace(payload1, "d1:!!notbase64!!");
            WriteEnvelope(1, env, badPayload);
            string badFileBefore = File.ReadAllText(CampaignSaveService.SlotPath(1));
            LoadResult bad = CampaignSaveService.Load(1);
            Expect(badPayload != env.PayloadJson && !bad.Success && bad.Reason == SaveFailureReason.Payload && bad.Message.Contains("15")
                   && File.ReadAllText(CampaignSaveService.SlotPath(1)) == badFileBefore,
                $"负向：区块差异被改坏（校验和仍对）→ 读档拒绝为正文损坏（“{bad.Message}”），原文件逐字节不变");
        }

        // ── I. 生成器版本（FGR-GEN-061，FGT-GEN-006）──────────────────────────────────────

        /// <summary>回归基准：（种子, 版本, 表面, 区块）→ 区块内容哈希。版本 0 = 原型地形（FG0-ARCH-04 存档的旧路径）。
        /// **改了生成算法或已发布版本的参数行而没有新增版本，这里会失败**——正确做法是新增 fg.TbWorldGenVersion 一行、把
        /// WorldGenVersions.Current 加 1，并为新版本补一组基准（旧版本的基准不许改）。</summary>
        private static readonly (int seed, int version, string surface, int cx, int cy, ulong hash)[] Baseline =
        {
            // 2026-09-25 FG0-ARCH-05 首次生成（v1 = 生成器首版；v0 = FG0-ARCH-04 原型地形）。
            (1, 1, "earth", 0, 0, 0x06A6DE7B1AB4286DUL),
            (1, 1, "earth", -1, -1, 0x93D3D5560C736F88UL),
            (1, 1, "earth", 3, -2, 0x63B9D218DA763939UL),
            (1, 1, "earth", 40, 17, 0xEDDE251B225DD88CUL),
            (1, 1, "earth", -500, 300, 0x869FB2F30353E75EUL),
            (42, 1, "earth", 0, 0, 0xC4B1A7002A5C818FUL),
            (42, 1, "earth", 31249, -31249, 0x8C0732E82B7AB180UL),
            (-7, 1, "earth", 2, 2, 0xEEDA223A7BF50880UL),
            (123456789, 1, "earth", -60, 45, 0xD5AFC68C58595ECCUL),
            (1, 1, "relay_hall", 0, 0, 0x24A001E3D9968BA1UL),
            (1, 1, "relay_hall", 2, 1, 0xF3C87636BC39B993UL),
            (42, 1, "data_center", 1, 2, 0xC40C68FF00E5688DUL),
            (1, 0, "earth", 0, 0, 0x6C0DE936AEB11472UL),
            (1, 0, "earth", 5, -3, 0x4AAAB6AC6F96CFFDUL),
            (42, 0, "earth", -2, 7, 0xD68A6BF004CE72F4UL),
            // 2026-09-25 FG0-ARCH-05 修复轮：种子 1 的静默领地内部（中心 (179,-423)）与澄净外圈酸沼带（整块落在带内）。
            (1, 1, "earth", 5, -14, 0xC0219FE70C8A0F15UL),
            (1, 1, "earth", -27, 21, 0xEB805D1D83145FF1UL),
        };

        private static readonly (int seed, int version, string surface, int cx, int cy)[] BaselineKeys =
        {
            (1, 1, "earth", 0, 0), (1, 1, "earth", -1, -1), (1, 1, "earth", 3, -2), (1, 1, "earth", 40, 17), (1, 1, "earth", -500, 300),
            (42, 1, "earth", 0, 0), (42, 1, "earth", 31249, -31249), (-7, 1, "earth", 2, 2), (123456789, 1, "earth", -60, 45),
            (1, 1, "relay_hall", 0, 0), (1, 1, "relay_hall", 2, 1), (42, 1, "data_center", 1, 2),
            (1, 0, "earth", 0, 0), (1, 0, "earth", 5, -3), (42, 0, "earth", -2, 7),
            (1, 1, "earth", 5, -14), (1, 1, "earth", -27, 21),
        };

        private static ulong BaselineHash(int seed, int version, string surface, int cx, int cy)
        {
            if (version == 0)
            {
                return HashOf(new GridTerrainPrototype(seed, new GridCell(0, 0)), cx, cy);
            }
            CampaignState s = WorldState(seed);
            s.World.GeneratorVersion = version;
            return HashOf(WorldGenService.CreateSource(s, surface), cx, cy);
        }

        private static void CheckGeneratorVersions()
        {
            var actual = BaselineKeys.Select(k => (k, h: BaselineHash(k.seed, k.version, k.surface, k.cx, k.cy))).ToList();
            var wrong = new List<string>();
            foreach (((int seed, int version, string surface, int cx, int cy) k, ulong h) in actual)
            {
                bool found = false;
                foreach ((int seed, int version, string surface, int cx, int cy, ulong hash) b in Baseline)
                {
                    if (b.seed == k.seed && b.version == k.version && b.surface == k.surface && b.cx == k.cx && b.cy == k.cy)
                    {
                        found = true;
                        if (b.hash != h)
                        {
                            wrong.Add($"({k.seed},{k.version},\"{k.surface}\",{k.cx},{k.cy}) 基准 0x{b.hash:X16} 实际 0x{h:X16}");
                        }
                    }
                }
                if (!found)
                {
                    wrong.Add($"缺基准 ({k.seed}, {k.version}, \"{k.surface}\", {k.cx}, {k.cy}, 0x{h:X16}UL)");
                }
            }
            Expect(wrong.Count == 0 && Baseline.Length == BaselineKeys.Length,
                $"FGR-GEN-061 回归哈希：{BaselineKeys.Length} 个（种子, 版本, 表面, 区块）样本（v1 星球 / 室内 + v0 原型地形）与基准逐个一致" +
                (wrong.Count == 0 ? string.Empty : "——" + string.Join("；", wrong)));

            // 改旧版本参数行而不升版本 → 回归哈希能发现。
            ulong before = BaselineHash(1, 1, "earth", 3, -2);
            var rows = WorldGenContent.Versions.ToList();
            WorldGenContent.OverrideForTests(versions: new TbWorldGenVersion(VersionBuf(rows, ("cliffThreshold", 0.70f))));
            ulong mutated = BaselineHash(1, 1, "earth", 3, -2);
            WorldGenContent.ResetForTests();
            Expect(mutated != before, "负向：把 v1 的悬崖阈值从 0.78 改成 0.70（不升版本）→ 同一区块哈希变化，回归基准会失败");

            // 旧版本存档（生成器 v0，地形来源 prototype-v1）：未修改区块用旧路径生成，与原型结果逐字节相同；已修改的区块按差异恢复。
            CampaignState legacy = NewHome(1);
            legacy.World.GeneratorVersion = WorldGenVersions.LegacyPrototype;
            legacy.Grid.TerrainSourceId = GridTerrainPrototype.Id;
            HomeGridService.Invalidate();
            HomeGridMap lm = HomeGridService.MapFor(legacy);
            lm.SetTerrain(new GridCell(170, -90), GridContent.TerrainCode("water"));
            SaveResult saved = CampaignSaveService.Save(1, legacy, SaveReason.Manual);
            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(1);
            bool legacyOk = false;
            string legacyDetail = "读档失败";
            if (loaded.Success)
            {
                HomeGridMap m = HomeGridService.MapFor(loaded.State);
                ulong got = HashOf(m.ChunkAt(new GridCell(5 * 32, -3 * 32), out _));
                ulong proto = BaselineHash(1, 0, "earth", 5, -3);
                ulong v1 = BaselineHash(1, 1, "earth", 5, -3);
                legacyOk = m.TerrainSource is GridTerrainPrototype && got == proto && got != v1
                           && m.GetTerrain(new GridCell(170, -90)) == GridContent.TerrainCode("water") && WorldGenService.PlanFor(loaded.State) == null;
                legacyDetail = $"来源 {m.TerrainSource.SourceId}，区块 (5,-3) 哈希 = 原型 {got == proto}、≠ v1 {got != v1}";
            }
            Expect(saved.Success && legacyOk, $"FGT-GEN-006：旧版本存档（生成器 v0 / 原型地形）读档后未修改区块按旧路径生成、与旧结果一致，已修改区块按差异恢复（{legacyDetail}）");

            // 更新的生成器版本：本版本游戏生成不了 → 读档拒绝（版本更新），原文件不动。
            CampaignState newer = NewHome(2);
            CampaignSaveService.Save(2, newer, SaveReason.Manual);
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(CampaignSaveService.SlotPath(2)));
            string payload = env.PayloadJson.Replace("\"GeneratorVersion\":" + WorldGenVersions.Current, "\"GeneratorVersion\":" + (WorldGenVersions.Current + 1));
            WriteEnvelope(2, env, payload);
            string fileBefore = File.ReadAllText(CampaignSaveService.SlotPath(2));
            LoadResult refused = CampaignSaveService.Load(2);
            Expect(payload != env.PayloadJson && !refused.Success && refused.Outcome == LoadOutcome.Incompatible && refused.Reason == SaveFailureReason.Newer
                   && File.ReadAllText(CampaignSaveService.SlotPath(2)) == fileBefore,
                $"负向：存档的生成器版本 v{WorldGenVersions.Current + 1} 比本版本新 → 读档拒绝为“版本更新”（“{refused.Message}”），原文件不变");
        }

        // ── I2. 生成输入冻结（FGR-GEN-061 / FGR-ARC-013，修复轮）──────────────────────────────

        private static WorldGenVersion V1() => WorldGenContent.Version(1);

        /// <summary>规划层固定指纹（核心 (0,0)、生成器 v1、default 预设）。改了领地集合 / 规划层参数 / 预设的领地距离而不升版本，这里失败。</summary>
        private static readonly (int seed, string fingerprint)[] PlanBaseline =
        {
            // 2026-09-25 FG0-ARCH-05 修复轮首次记录。
            (1, "silent@179,-423r250h0a293k0;foundry@768,192r300h0a14k0;clarity@-1245,690r350h100a151k0;overclock@-1342,-1251r350h100a223k0;white_tide@179,1017r250h0a80k0;"),
            (42, "silent@-478,-334r250h0a215k0;foundry@570,785r300h0a54k0;clarity@1023,-1136r350h100a312k2;overclock@-1169,1393r350h100a130k4;white_tide@1143,-202r250h0a350k0;"),
            (-7, "silent@577,155r250h0a15k0;foundry@-513,656r300h0a128k0;clarity@-274,-1186r350h100a257k5;overclock@-1836,-357r350h100a191k41;white_tide@533,886r250h0a59k0;"),
        };

        /// <summary>v1 全部生成输入的摘要基准（<see cref="WorldGenInputs.Manifest"/> 逐部分，FNV-1a 64）。
        /// **已有行永远不许改**：失败说明改了已发布的生成输入——A 类（版本行 / 领地集合 / 世界设置集合）应新增版本行与新集合，
        /// B 类（表面行 / 区块边长 / 地形码）根本不能改，C 类（起始区保护 = 开局布局 + 占地）要等 FG3-GEN-01 把起始区规则并入版本。
        /// 新增表面时补一行它的基准。</summary>
        private static readonly (string part, ulong digest)[] InputBaseline =
        {
            // 2026-09-25 FG0-ARCH-05 修复轮首次记录（v1 发布）。
            ("v1.version", 0xF229C0AFD8DF58D8UL),
            ("v1.territories[t1]", 0x2C40043172F1C2EEUL),
            ("v1.presets[p1]", 0xC72209BC11B9806DUL),
            ("v1.start_protection.standard", 0xEBD23BD380D71E1EUL),
            ("v1.start_protection.relaxed", 0xF02E541F754A6148UL),
            ("grid.chunk_size", 0x07FF7C07B4C001F0UL),
            ("terrain.codes", 0x899C025EA98B2A34UL),
            ("surface.earth", 0x3CC24D93573AAB34UL),
            ("surface.relay_hall", 0x629555782391BB2EUL),
            ("surface.data_center", 0x702C697E2C6F6AF8UL),
        };

        private static string PlanFp(int seed) =>
            WorldPlan.Compute(seed, V1(), WorldGenContent.Preset(1, WorldGenContent.DefaultPresetId), 0, 0).Fingerprint();

        private static List<string> ManifestDiff(int version)
        {
            var wrong = new List<string>();
            List<KeyValuePair<string, string>> actual = WorldGenInputs.Manifest(version);
            foreach (KeyValuePair<string, string> kv in actual)
            {
                ulong d = WorldGenInputs.Digest(kv.Value);
                int i = Array.FindIndex(InputBaseline, b => b.part == kv.Key);
                if (i < 0)
                {
                    wrong.Add($"缺基准 (\"{kv.Key}\", 0x{d:X16}UL) 〔{kv.Value}〕");
                }
                else if (InputBaseline[i].digest != d)
                {
                    wrong.Add($"{kv.Key} 基准 0x{InputBaseline[i].digest:X16} 实际 0x{d:X16}〔{kv.Value}〕");
                }
            }
            foreach ((string part, ulong _) in InputBaseline)
            {
                if (!actual.Any(kv => kv.Key == part))
                {
                    wrong.Add($"{part} 不见了");
                }
            }
            return wrong;
        }

        private static IEnumerable<GridCell> ChunkCorners(int cx, int cy)
        {
            yield return new GridCell(cx * 32, cy * 32);
            yield return new GridCell(cx * 32 + 31, cy * 32);
            yield return new GridCell(cx * 32, cy * 32 + 31);
            yield return new GridCell(cx * 32 + 31, cy * 32 + 31);
        }

        private static void CheckGenerationInputsFrozen()
        {
            // 1) 生成输入清单与冻结基准逐部分一致。
            List<string> wrong = ManifestDiff(1);
            int parts = WorldGenInputs.Manifest(1).Count;
            Expect(wrong.Count == 0 && parts >= 10,
                $"FGR-GEN-061 生成输入清单：v1 的 {parts} 个部分（版本行含规划层参数、领地集合 t1、世界设置集合 p1、起始区保护矩形、区块边长、地形码、各表面）与冻结基准逐个一致" +
                (wrong.Count == 0 ? string.Empty : "——" + string.Join("；", wrong)));

            // 2) 规划层固定指纹。
            var planWrong = new List<string>();
            foreach ((int seed, string fingerprint) b in PlanBaseline)
            {
                string fp = PlanFp(b.seed);
                if (fp != b.fingerprint)
                {
                    planWrong.Add($"种子 {b.seed}：基准 {b.fingerprint} 实际 {fp}");
                }
            }
            Expect(planWrong.Count == 0,
                $"规划层固定指纹：{PlanBaseline.Length} 个种子（v1、default、核心 (0,0)）的领地中心 / 半径 / 危害带 / 方向 / 重试次数与基准逐字一致" +
                (planWrong.Count == 0 ? string.Empty : "——" + string.Join("；", planWrong)));

            // 3) 回归样本真的覆盖规划层：整块落在领地内部 / 危害带里。
            CampaignState ws = WorldState(1);
            ws.World.GeneratorVersion = 1;
            WorldPlan plan1 = WorldGenService.PlanFor(ws);
            bool inSilent = ChunkCorners(5, -14).All(c => plan1.TerritoryAt(c.X, c.Y)?.Id == "silent");
            bool inBand = ChunkCorners(-27, 21).All(c => plan1.HazardAt(c.X, c.Y)?.Id == "clarity");
            Expect(inSilent && inBand,
                $"回归样本覆盖规划层：区块 (5,-14) 整块在种子 1 的静默领地内部（{inSilent}）、区块 (-27,21) 整块在澄净外圈酸沼带（{inBand}），四角都命中");

            // 4) 变异测试：不升版本直接改已发布的生成输入 → 基准失败。
            string fp1 = PlanFp(1);
            ulong sTer = BaselineHash(1, 1, "earth", 5, -14);
            ulong sBand = BaselineHash(1, 1, "earth", -27, 21);
            var samples = BaselineKeys.Where(k => k.version == 1 && k.surface == "earth").ToList();
            var before = samples.ToDictionary(k => k, k => BaselineHash(k.seed, k.version, k.surface, k.cx, k.cy));

            // a) 领地集合 t1：澄净危害带宽 100 → 20。
            WorldGenContent.OverrideForTests(territories: new TbTerritory(TerritoryBuf(mutate: r =>
            {
                if (r.Id == "clarity")
                {
                    r.HazardWidth = 20;
                }
                return r;
            })));
            bool terManifest = ManifestDiff(1).Any(x => x.StartsWith("v1.territories", StringComparison.Ordinal));
            bool terPlan = PlanFp(1) != fp1;
            bool terHash = BaselineHash(1, 1, "earth", -27, 21) != sBand;
            WorldGenContent.ResetForTests();
            Expect(terManifest && terPlan && terHash,
                $"负向：直接改已发布的领地集合 t1（澄净危害带 100 → 20 格）而不升版本 → 生成输入清单（{terManifest}）、规划层指纹（{terPlan}）、酸沼带样本哈希（{terHash}）三处基准都失败");

            // b) 世界设置集合 p1：default 的污染强度 1.0 → 2.0。
            WorldGenContent.OverrideForTests(presets: new TbWorldPreset(PresetBuf(("default", "world.preset.default.name", 1f, 2f, 1f, "standard"))));
            bool preManifest = ManifestDiff(1).Any(x => x.StartsWith("v1.presets", StringComparison.Ordinal));
            int preChanged = samples.Count(k => BaselineHash(k.seed, k.version, k.surface, k.cx, k.cy) != before[k]);
            WorldGenContent.ResetForTests();
            Expect(preManifest && preChanged > 0,
                $"负向：直接改已发布的世界设置 p1.default（污染强度 1.0 → 2.0）而不升版本 → 生成输入清单基准失败（{preManifest}），v1 星球回归样本 {preChanged}/{samples.Count} 个哈希变化");

            // c) 版本行里的规划层参数：家园区半径 200 → 400。
            WorldGenContent.OverrideForTests(versions: new TbWorldGenVersion(VersionBuf(WorldGenContent.Versions.ToList(), ("homeZoneRadius", 400))));
            bool hzManifest = ManifestDiff(1).Any(x => x.StartsWith("v1.version", StringComparison.Ordinal));
            bool hzPlan = PlanFp(1) != fp1;
            bool hzHash = BaselineHash(1, 1, "earth", 5, -14) != sTer;
            WorldGenContent.ResetForTests();
            Expect(hzManifest && hzPlan && hzHash,
                $"负向：直接改 v1 行的家园区半径 200 → 400（原 world.home_zone_radius，已移入版本行）而不升版本 → 生成输入清单（{hzManifest}）、规划层指纹（{hzPlan}）、静默领地样本哈希（{hzHash}）都失败");

            // d) 区块边长（B 类永久冻结）。
            GridContent.OverrideForTests(tuning: new TbHomeTuning(TuningBuf(GridContent.Tuning, ("grid.chunk_size", 16f))));
            bool csManifest = ManifestDiff(1).Any(x => x.StartsWith("grid.chunk_size", StringComparison.Ordinal));
            GridContent.ResetForTests();
            Expect(csManifest, "负向：区块边长 grid.chunk_size 32 → 16 → 生成输入清单基准失败（B 类永久冻结；出表时 check_luban R21 也会拦下）");

            // e) 正确做法：新增 v2 版本行 + 新领地集合 t2 → v1 一字不变（旧存档不受影响），v2 按新集合生成。
            WorldGenContent.OverrideForTests(
                versions: new TbWorldGenVersion(VersionBufPlus(WorldGenContent.Versions.ToList(), 2, new (string, object)[] { ("territorySet", "t2"), ("note", "selfcheck v2") })),
                territories: new TbTerritory(TerritoryBuf(cloneToSet: "t2", cloneMutate: r =>
                {
                    if (r.Id == "clarity")
                    {
                        r.HazardWidth = 20;
                    }
                    return r;
                })));
            bool v1Manifest = ManifestDiff(1).Count == 0;
            bool v1Plan = PlanFp(1) == fp1;
            bool v1Hash = BaselineHash(1, 1, "earth", -27, 21) == sBand && BaselineHash(1, 1, "earth", 5, -14) == sTer;
            string fp2 = WorldPlan.Compute(1, WorldGenContent.Version(2), WorldGenContent.Preset(2, WorldGenContent.DefaultPresetId), 0, 0).Fingerprint();
            bool v2Differs = fp2 != fp1 && BaselineHash(1, 2, "earth", -27, 21) != sBand;
            WorldGenContent.ResetForTests();
            Expect(v1Manifest && v1Plan && v1Hash && v2Differs,
                $"正确做法：新增 v2 版本行引用新领地集合 t2（澄净危害带 100 → 20）→ v1 的生成输入清单（{v1Manifest}）、规划层指纹（{v1Plan}）、样本哈希（{v1Hash}）一字不变，" +
                $"记录 v1 的旧存档不受影响；v2 的规划与地形按新集合生成（{v2Differs}）");
        }

        // ── I3. 差异稳定排序、存档卡世界设置摘要、大面积探索下的流式加载 ───────────────────────────

        private static void CheckDiffOrderAndSaveCard()
        {
            List<ChunkDiffRecord> Capture(bool aFirst)
            {
                CampaignState st = NewHome(5151);
                HomeGridMap m = HomeGridService.MapFor(st);
                var a = new GridCell(10 * 32 + 3, 10 * 32 + 4);
                var b = new GridCell(-5 * 32 + 1, 3 * 32 + 2);
                byte water = GridContent.TerrainCode("water");
                // 一条不认识的表面（以后的新星球）的差异：原样保留，也参与排序。
                st.World.ChunkDiffs = new[] { new ChunkDiffRecord { SurfaceId = "zz_future_planet", ChunkX = 0, ChunkY = 0, DiffPayload = "keep" } };
                if (aFirst)
                {
                    m.SetTerrain(a, water);
                    m.SetTerrain(b, water);
                }
                else
                {
                    m.SetTerrain(b, water);
                    m.SetTerrain(a, water);
                }
                WorldGenService.CaptureDiffs(st);
                return st.World.ChunkDiffs.ToList();
            }
            string Text(List<ChunkDiffRecord> l) => string.Join("|", l.Select(r => $"{r.SurfaceId},{r.ChunkX},{r.ChunkY},{r.DiffPayload}"));
            List<ChunkDiffRecord> ab = Capture(true);
            List<ChunkDiffRecord> ba = Capture(false);
            List<ChunkDiffRecord> sorted = ab.OrderBy(r => r.SurfaceId, StringComparer.Ordinal).ThenBy(r => r.ChunkX).ThenBy(r => r.ChunkY).ToList();
            Expect(Text(ab) == Text(ba) && Text(ab) == Text(sorted) && ab.Count >= 3 && ab.Any(r => r.SurfaceId == "zz_future_planet" && r.DiffPayload == "keep"),
                $"区块差异写盘前稳定排序（表面, 区块 X, 区块 Y）：先改 A 后改 B 与先改 B 后改 A 写出完全相同的 {ab.Count} 条记录（不认识的表面原样保留）——同一逻辑状态存档字节与修改历史无关（ER1-SAVE-01）");

            CampaignState cs = NewHome(6161);
            SaveResult sv = CampaignSaveService.Save(1, cs, SaveReason.Manual);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(1);
            string fields = CampaignSlotText.FgFields(meta);
            string worldText = GameText.Format("save.card.world", GameText.Get("world.preset.default.name"));
            Expect(sv.Success && meta.WorldSettingsId == WorldGenContent.DefaultPresetId && meta.GeneratorVersion == WorldGenVersions.Current
                   && fields.Contains(worldText) && fields.Contains("6161"),
                $"存档卡显示种子与世界设置摘要（FG17 第 4 节）：“{fields}”");

            // 大面积已探索：需要集合只含镜头窗口；跨区块不重算探索范围；预生成区的纯地形区块可以被回收；新增探索记录只增量触发。
            GridContent.OverrideForTests(tuning: new TbHomeTuning(TuningBuf(GridContent.Tuning, ("world.stream.max_resident_chunks", 64f))));
            CampaignState es = WorldState(7071);
            es.World.GeneratorVersion = WorldGenVersions.Current;
            HomeGridMap map = FreshMap(es);
            map.SetExplored(new[] { new ExploredAreaRecord { CenterX = 0, CenterY = 0, Radius = 3200 } });
            var streamer = new WorldChunkStreamer(map);
            try
            {
                var sw = Stopwatch.StartNew();
                streamer.Tick(new GridCell(0, 0));
                double firstMs = sw.Elapsed.TotalMilliseconds;
                int triggered = streamer.PregenTriggeredCount;
                int r = GridContent.TuningInt("world.view_radius_chunks");
                int window = (2 * r + 1) * (2 * r + 1);
                bool desiredIsWindow = streamer.DesiredCount == window;
                double maxCross = 0;
                for (int k = 1; k <= 40; k++)
                {
                    streamer.Tick(new GridCell(k * 32, 0)); // 每帧跨一个区块
                    maxCross = Math.Max(maxCross, streamer.LastTickMs);
                    desiredIsWindow &= streamer.DesiredCount == window;
                }
                for (int k = 0; k < 300; k++)
                {
                    streamer.Tick(new GridCell(40 * 32, 0));
                    System.Threading.Thread.Sleep(1);
                }
                int evicted = streamer.TotalEvicted;
                int resident = map.LoadedChunkCount;
                map.SetExplored(new[]
                {
                    new ExploredAreaRecord { CenterX = 0, CenterY = 0, Radius = 3200 },
                    new ExploredAreaRecord { CenterX = 200000, CenterY = 0, Radius = 64 },
                });
                sw.Restart();
                streamer.Tick(new GridCell(40 * 32, 0));
                double addMs = sw.Elapsed.TotalMilliseconds;
                int added = streamer.PregenTriggeredCount - triggered;
                // 新记录 (200000,0) 半径 64：区块 6248～6252 × -2～2，外扩边距 2 → 9 × 9。
                int margin = GridContent.TuningInt("world.pregen_margin_chunks");
                int expectAdded = (5 + 2 * margin) * (5 + 2 * margin);
                Line($"  · 大面积探索（半径 3200 格，预生成触发 {triggered} 个区块）：首次处理 {firstMs:F1} ms（只在探索范围变化时）；之后每帧跨一个区块最多 {maxCross:F3} ms；新增一条探索记录 {addMs:F3} ms、只触发 {added} 个区块");
                Expect(desiredIsWindow && triggered > 30000 && maxCross <= 2.0 && evicted > 0 && resident <= 64 + 64 + window + 16 && added == expectAdded,
                    $"FG17 第 7 节：已探索 {triggered} 个区块时，常驻不可回收的只有镜头窗口 {window} 块；每帧跨区块不重算探索范围（最多 {maxCross:F3} ms）；预生成区的纯地形区块照常回收（回收 {evicted}、常驻 {resident}）；" +
                    $"再加一条探索记录只增量触发 {added} 个区块（应为 {expectAdded}），已触发过的不重复生成");

                // 同一条探索记录半径变大（3200 → 3232 格，外扩一个区块）：只扫新增环带。
                int beforeGrow = streamer.PregenTriggeredCount;
                map.SetExplored(new[]
                {
                    new ExploredAreaRecord { CenterX = 0, CenterY = 0, Radius = 3232 },
                    new ExploredAreaRecord { CenterX = 200000, CenterY = 0, Radius = 64 },
                });
                sw.Restart();
                streamer.Tick(new GridCell(40 * 32, 0));
                double growMs = sw.Elapsed.TotalMilliseconds;
                int ring = streamer.PregenTriggeredCount - beforeGrow;
                int side = 2 * 101 + 1 + 2 * margin;           // 半径 3232 → 区块 -101～101，外扩边距
                int expectRing = side * side - (side - 2) * (side - 2);
                Expect(ring == expectRing && growMs <= 5.0,
                    $"探索记录半径 3200 → 3232 格：只处理新增环带，触发 {ring} 个区块（应为 {expectRing}），用时 {growMs:F3} ms（首次处理整片 {firstMs:F1} ms）");
            }
            finally
            {
                streamer.Dispose();
                GridContent.ResetForTests();
            }
        }

        // ── J. 暂停 / 倍速 / 后台一致（B09、B24）──────────────────────────────────────────

        private static void CheckPauseSpeedAndBackground()
        {
            var path = Enumerable.Range(0, 30).Select(i => new GridCell(i * 40 - 600, 300 - i * 25)).ToList();
            string Run(float? speed)
            {
                if (speed.HasValue)
                {
                    StrategyClock.SetSpeed(speed.Value);
                }
                InputRouter.SetGameplayPaused(!speed.HasValue);
                var map = FreshMap(WorldState(1234));
                var st = new WorldChunkStreamer(map);
                try
                {
                    foreach (GridCell c in path)
                    {
                        st.DrainForTests(c, 1000);
                    }
                    return string.Join(",", map.LoadedChunks.OrderBy(c => HomeGridMap.Key(c.ChunkX, c.ChunkY)).Select(c => HashOf(c).ToString("X")));
                }
                finally
                {
                    st.Dispose();
                    InputRouter.SetGameplayPaused(false);
                    StrategyClock.Reset();
                }
            }
            string paused = Run(null);
            string half = Run(0.5f);
            string one = Run(1f);
            string two = Run(2f);
            Expect(paused.Length > 0 && paused == half && half == one && one == two,
                "B09：战略暂停与 0.5x / 1x / 2x 下沿同一镜头路径流式加载，生成的区块集合与内容完全相同（流式加载与游戏时间无关；3x 档随统一时钟由 FG7-ENV-01 接入，DEBT-FG0UX01-03）");

            // 观察（流式 + 叠加层画面）与不观察（玩法同步查询）逐字节一致（FGR-BASE-021 / B24）。
            HomeGridMap observed = FreshMap(WorldState(1235));
            HomeGridMap unobserved = FreshMap(WorldState(1235));
            var so = new WorldChunkStreamer(observed);
            try
            {
                so.DrainForTests(new GridCell(-3000, 777));
            }
            finally
            {
                so.Dispose();
            }
            int diff = 0;
            foreach (HomeGridMap.Chunk c in observed.LoadedChunks)
            {
                diff += HashOf(unobserved.ChunkAt(new GridCell(c.ChunkX * 32 + 3, c.ChunkY * 32 + 3), out _)) != HashOf(c) ? 1 : 0;
            }
            Expect(diff == 0 && observed.LoadedChunkCount >= 25,
                $"B24：被观察（镜头在、工作线程流式生成）与不被观察（玩法查询主线程生成）的 {observed.LoadedChunkCount} 个区块逐字节一致（不一致 {diff}）");
        }

        // ── K. 叠加层与建造栏 ─────────────────────────────────────────────────────

        private static void CheckOverlayAndHud()
        {
            CampaignState s = NewHome(9001);
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            GameObject hudGo = null;
            try
            {
                mode.Open();
                int r = GridContent.TuningInt("world.view_radius_chunks");
                int tiles = (2 * r + 1) * (2 * r + 1);
                HomeGridMap map = HomeGridService.MapFor(s);
                var far = new GridCell(5000, 5000);
                int syncBefore = map.SyncGeneratedCount;
                mode.UpdateTerrainOverlay(s, far);
                WorldTerrainOverlay ov = mode.TerrainOverlay;
                ChunkAddress fa = GridMath.Address(far, 32);
                bool allPlaceholder = ov.TileCount == tiles && ov.PlaceholderCount == tiles && mode.GeneratingChunkCount == tiles
                                      && ov.ShownTexture(fa.ChunkX, fa.ChunkY) == ov.PlaceholderTexture;

                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
                VisualElement root = vta.CloneTree();
                hudGo = new GameObject("__fgworld_hud") { hideFlags = HideFlags.HideAndDontSave };
                BuildModeHudUIToolkit hud = hudGo.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                InputRouter.SetScope(InputScope.Strategy);
                hud.Refresh();
                bool hudShows = hud.GeneratingVisible && hud.GeneratingLabelText.Contains(tiles.ToString()) && hud.GeneratingLabelText.Contains("正在生成地形");
                bool hook = GuidanceHooks.Known.Contains(GuidanceHooks.WorldFirstGenerating) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstGenerating);
                Expect(allPlaceholder && hudShows && map.SyncGeneratedCount == syncBefore && hook,
                    $"镜头飞到还没生成的地方：{ov.TileCount} 块叠加层全部显示“生成中”占位（灰底斜条纹），建造栏显示“{hud.GeneratingLabelText}”；叠加层没有在主线程同步生成任何区块；发出引导钩子“首次看到生成中”（B14）");

                // 流式加载在后台补齐 → 叠加层换成真实地形贴图，提示消失。
                WorldChunkStreamer st = HomeGridService.Streamer(s);
                int frames = 0;
                var sw = new Stopwatch();
                double maxOverlayMs = 0;
                while ((ov.PlaceholderCount > 0 || mode.GeneratingChunkCount > 0) && frames < 600)
                {
                    st.Tick(far);
                    sw.Restart();
                    mode.UpdateTerrainOverlay(s, far);
                    maxOverlayMs = Math.Max(maxOverlayMs, sw.Elapsed.TotalMilliseconds);
                    System.Threading.Thread.Sleep(2);
                    frames++;
                }
                hud.Refresh();
                HomeGridMap.Chunk fc = map.TryGetLoaded(fa.ChunkX, fa.ChunkY);
                int idx = fc == null ? -1 : Array.IndexOf(fc.Terrain, (byte)0);
                GridCell probe = new GridCell(fa.ChunkX * 32 + idx % 32, fa.ChunkY * 32 + idx / 32);
                Color32 px = default;
                bool pixelOk = idx >= 0 && mode.TryGetOverlayPixel(probe, 2, 2, out px);
                ColorUtility.TryParseHtmlString(GridContent.Terrains.First(t => t.Code == 0).Color, out Color baseColor);
                Color32 b32 = baseColor;
                var fogged = new Color32((byte)(b32.r * 64 >> 8), (byte)(b32.g * 64 >> 8), (byte)(b32.b * 64 >> 8), 255);
                Expect(ov.PlaceholderCount == 0 && !hud.GeneratingVisible && pixelOk && Near(px, fogged) && ov.ShownTexture(fa.ChunkX, fa.ChunkY) != ov.PlaceholderTexture,
                    $"{frames} 帧后流式加载补齐：占位全部换成按格网数据画的贴图（未探索的空地 = 表颜色 × 迷雾变暗 {px}），“正在生成”提示消失；叠加层每帧主线程最多 {maxOverlayMs:F3} ms");

                // 跟随镜头：移动两个区块，窗口跟着走，新露出的区块先占位再补齐。
                var far2 = new GridCell(far.X + 64, far.Y);
                mode.UpdateTerrainOverlay(s, far2);
                bool moved = ov.WindowChunkX == fa.ChunkX + 2 && ov.HasTile(fa.ChunkX + 2 + r, fa.ChunkY) && !ov.HasTile(fa.ChunkX - r, fa.ChunkY);
                int newPending = mode.GeneratingChunkCount;
                frames = 0;
                while (mode.GeneratingChunkCount > 0 && frames < 600)
                {
                    st.Tick(far2);
                    mode.UpdateTerrainOverlay(s, far2);
                    System.Threading.Thread.Sleep(2);
                    frames++;
                }
                Expect(moved && newPending > 0 && mode.GeneratingChunkCount == 0 && ov.TileCount == tiles,
                    $"叠加层跟随镜头：焦点右移 2 个区块，窗口跟着移动（新露出 {newPending} 块先占位，{frames} 帧后补齐），块数保持 {tiles}");

                // 地形修改后这一块重画。
                GridCell target = new GridCell(far2.X, far2.Y);
                mode.TryGetOverlayPixel(target, 2, 2, out Color32 beforePx);
                map.SetTerrain(target, GridContent.TerrainCode("cliff"));
                mode.CompleteOverlayNow(s, far2);
                mode.TryGetOverlayPixel(target, 2, 2, out Color32 afterPx);
                Expect(!Near(beforePx, afterPx), $"地形被修改后这一块叠加层重画：{beforePx} → {afterPx}");

                // 快速来回移动窗口：移出窗口时还没画完的贴图任务挂到待释放列表，完成后在后续帧释放（主线程不为回收等待工作线程）。
                int maxRetiring = 0;
                for (int k = 0; k < 12; k++)
                {
                    map.SetTerrain(new GridCell(far.X + k, far.Y), GridContent.TerrainCode(k % 2 == 0 ? "water" : "cliff")); // 让两边窗口都有块要重画
                    map.SetTerrain(new GridCell(far2.X + k, far2.Y), GridContent.TerrainCode(k % 2 == 0 ? "cliff" : "water"));
                    mode.UpdateTerrainOverlay(s, k % 2 == 0 ? far : far2);
                    maxRetiring = Math.Max(maxRetiring, ov.RetiringJobCount);
                }
                int settleFrames = 0;
                while (ov.RetiringJobCount > 0 && settleFrames < 300)
                {
                    System.Threading.Thread.Sleep(2);
                    mode.UpdateTerrainOverlay(s, far2);
                    settleFrames++;
                }
                Expect(ov.RetiringJobCount == 0,
                    $"叠加层窗口快速来回移动 12 次：移出窗口时还没画完的贴图任务最多 {maxRetiring} 个挂起（不在主线程同步等待），{settleFrames} 帧内全部完成并释放");
            }
            finally
            {
                mode.Close();
                mode.Shutdown();
                HomeValleyBuildMode.Unbind(mode);
                if (hudGo != null)
                {
                    Object.DestroyImmediate(hudGo);
                }
                InputRouter.SetBuildMode(false);
            }
        }

        // ── L. 暂停菜单 ────────────────────────────────────────────────────────────

        private static void CheckPauseMenu()
        {
            CampaignState s = NewHome(-424242);
            HomeGridService.MapFor(s);
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/PauseMenu.uxml");
            VisualElement root = vta.CloneTree();
            var go = new GameObject("__fgworld_pause") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                PauseMenuUIToolkit menu = go.AddComponent<PauseMenuUIToolkit>();
                menu.BindView(root);
                menu.RefreshWorldInfo();
                string seedText = s.World.WorldSeed.ToString(CultureInfo.InvariantCulture);
                bool shows = menu.WorldSeedLabelText.Contains(seedText) && menu.WorldSettingsLabelText.Contains("标准")
                             && menu.WorldSettingsLabelText.Contains("v" + WorldGenVersions.Current) && menu.WorldSettingsLabelText.Contains("地球");
                Button copy = root.Q<Button>("PauseCopySeed");
                string oldClip = GUIUtility.systemCopyBuffer;
                menu.CopySeed();
                string clip = GUIUtility.systemCopyBuffer;
                GUIUtility.systemCopyBuffer = oldClip;
                Expect(shows && copy != null && copy.text == "复制种子" && clip == seedText && menu.FeedbackText.Contains(seedText),
                    $"FGR-GEN-001：暂停菜单显示“{menu.WorldSeedLabelText}”与“{menu.WorldSettingsLabelText}”；“复制种子”把 {clip} 写进剪贴板并提示");

                GameSettings.SetLanguage(GameLanguage.En);
                menu.RefreshWorldInfo();
                bool en = menu.WorldSeedLabelText.StartsWith("World seed") && menu.WorldSettingsLabelText.Contains("Standard");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                s.World.GeneratorVersion = 0;
                s.Grid.TerrainSourceId = GridTerrainPrototype.Id;
                menu.RefreshWorldInfo();
                bool legacy = menu.WorldSettingsLabelText.Contains("原型地形");
                Expect(en && legacy, $"英文界面显示“World seed / Standard”；旧版本存档显示“{menu.WorldSettingsLabelText}”");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── M. 活跃分级与原生内存 ───────────────────────────────────────────────────

        private static void CheckActivityAndLeaks()
        {
            CampaignState s = NewHome(4040);
            HomeGridMap map = HomeGridService.MapFor(s);
            ChunkActivity core = WorldActivity.Classify(s, map, 0, 0, new GridCell(3000, 3000), 2);
            ChunkActivity observed = WorldActivity.Classify(s, map, 94, 94, new GridCell(3000, 3000), 2);
            ChunkActivity terrain = WorldActivity.Classify(s, map, 60, -60, new GridCell(3000, 3000), 2);
            Vector2 hauler = WorldActivity.HomeMachinePositions(s).First(); // NewHome 在 (-10,-6) 生成的搬运机 → 区块 (-1,-1)
            ChunkAddress ha = GridMath.Address(GridCell.FromWorld(hauler), 32);
            ChunkActivity machine = WorldActivity.Classify(s, map, ha.ChunkX, ha.ChunkY, new GridCell(3000, 3000), 2);
            var active = new HashSet<long>();
            WorldActivity.ActiveChunks(s, map, new GridCell(3000, 3000), 2, active);
            Expect(core == ChunkActivity.Owned && observed == ChunkActivity.Observed && terrain == ChunkActivity.TerrainOnly && machine == ChunkActivity.Owned
                   && ha.ChunkX == -1 && ha.ChunkY == -1
                   && active.Contains(HomeGridMap.Key(0, 0)) && active.Contains(HomeGridMap.Key(94, 94)) && !active.Contains(HomeGridMap.Key(60, -60)),
                "FGR-GEN-052：有己方建筑 / 机器的区块 = 完整模拟（镜头不在也是），镜头窗口 = 被观察，其余纯地形区块不需要模拟");

            WorldChunkStreamer st = HomeGridService.Streamer(s);
            st.Tick(new GridCell(-20000, 20000));
            int inflight = st.InFlightCount;
            HomeGridService.ShutdownStreaming();
            Expect(inflight > 0 && st.IsDisposed && WorldGenKernel.LiveJobCount == 0,
                $"资源成对释放：区域卸载（ShutdownStreaming）时 {inflight} 个在飞生成任务全部完成并释放原生内存（剩余 {WorldGenKernel.LiveJobCount}）");
        }

        // ── 测试表构造 ────────────────────────────────────────────────────────────

        private static ByteBuf PresetBuf(params (string id, string nameKey, float res, float pol, float dist, string start)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString("p1." + r.id);
                buf.WriteString("p1");
                buf.WriteString(r.id);
                buf.WriteString(r.nameKey);
                buf.WriteFloat(r.res);
                buf.WriteFloat(r.pol);
                buf.WriteFloat(r.dist);
                buf.WriteString(r.start);
            }
            return buf;
        }

        private static ByteBuf SurfaceBuf(List<(string id, string kind, string nameKey, int salt, int w, int h)> rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach (var r in rows)
            {
                buf.WriteString(r.id);
                buf.WriteString(r.kind);
                buf.WriteString(r.nameKey);
                buf.WriteInt(r.salt);
                buf.WriteInt(r.w);
                buf.WriteInt(r.h);
            }
            return buf;
        }

        /// <summary>生成器版本表：原样写出 <paramref name="rows"/>，对 v1 行套用 <paramref name="v1Patch"/>（字段名 = 表列名）。</summary>
        private static ByteBuf VersionBuf(List<WorldGenVersion> rows, params (string field, object value)[] v1Patch) =>
            VersionBufPlus(rows, 0, null, v1Patch);

        /// <summary>同上；<paramref name="extraVersion"/> &gt; 0 时再追加一行：复制 v1、版本号改为 extraVersion，并套用 <paramref name="extraPatch"/>。</summary>
        private static ByteBuf VersionBufPlus(List<WorldGenVersion> rows, int extraVersion, (string field, object value)[] extraPatch,
            params (string field, object value)[] v1Patch)
        {
            var buf = new ByteBuf();
            WorldGenVersion v1 = rows.First(x => x.Version == 1);
            buf.WriteSize(rows.Count + (extraVersion > 0 ? 1 : 0));
            foreach (WorldGenVersion v in rows)
            {
                WriteVersion(buf, v, v.Version, v.Version == 1 ? v1Patch : null);
            }
            if (extraVersion > 0)
            {
                WriteVersion(buf, v1, extraVersion, extraPatch);
            }
            return buf;
        }

        private static void WriteVersion(ByteBuf buf, WorldGenVersion v, int version, (string field, object value)[] patch)
        {
            T P<T>(string field, T value)
            {
                if (patch != null)
                {
                    foreach ((string f, object x) in patch)
                    {
                        if (f == field)
                        {
                            return (T)Convert.ChangeType(x, typeof(T), CultureInfo.InvariantCulture);
                        }
                    }
                }
                return value;
            }
            buf.WriteInt(version);
            buf.WriteFloat(P("featureScale", v.FeatureScale));
            buf.WriteFloat(P("cliffThreshold", v.CliffThreshold));
            buf.WriteFloat(P("waterThreshold", v.WaterThreshold));
            buf.WriteFloat(P("oreThreshold", v.OreThreshold));
            buf.WriteFloat(P("rareBias", v.RareBias));
            buf.WriteFloat(P("oilBias", v.OilBias));
            buf.WriteFloat(P("pollutionThreshold", v.PollutionThreshold));
            buf.WriteFloat(P("pollutionScale", v.PollutionScale));
            buf.WriteInt(P("pollutionDistanceStart", v.PollutionDistanceStart));
            buf.WriteInt(P("pollutionDistancePerLevel", v.PollutionDistancePerLevel));
            buf.WriteInt(P("resourceDistancePerStep", v.ResourceDistancePerStep));
            buf.WriteFloat(P("resourceDistanceStep", v.ResourceDistanceStep));
            buf.WriteInt(P("resourceDistanceMaxSteps", v.ResourceDistanceMaxSteps));
            buf.WriteInt(P("territoryPollutionBonus", v.TerritoryPollutionBonus));
            buf.WriteInt(P("hazardPollution", v.HazardPollution));
            buf.WriteInt(P("protectRadius", v.ProtectRadius));
            buf.WriteInt(P("protectMargin", v.ProtectMargin));
            buf.WriteInt(P("anchorProtectRadius", v.AnchorProtectRadius));
            buf.WriteInt(P("interiorPillarSpacing", v.InteriorPillarSpacing));
            buf.WriteFloat(P("interiorRuinThreshold", v.InteriorRuinThreshold));
            buf.WriteInt(P("homeZoneRadius", v.HomeZoneRadius));
            buf.WriteInt(P("territoryMinAngle", v.TerritoryMinAngle));
            buf.WriteInt(P("planMaxAttempts", v.PlanMaxAttempts));
            buf.WriteString(P("territorySet", v.TerritorySet));
            buf.WriteString(P("presetSet", v.PresetSet));
            buf.WriteString(P("note", v.Note));
        }

        /// <summary>领地表：真实表的全部行，外加 <paramref name="extra"/>（例如把 t1 复制成 t2 集合再改某一行）。
        /// <paramref name="mutate"/> 可以就地改 t1 的行（模拟“不升版本直接改已发布集合”）。</summary>
        private static ByteBuf TerritoryBuf(Func<TRow, TRow> mutate = null, string cloneToSet = null, Func<TRow, TRow> cloneMutate = null)
        {
            List<TRow> rows = WorldGenContent.Territories.Select(t => new TRow
            {
                Set = t.Set, Id = t.Id, NameKey = t.NameKey, Act = t.Act, Faction = t.Faction, Min = t.MinDistance, Max = t.MaxDistance,
                Radius = t.Radius, HazardKind = t.HazardKind, HazardWidth = t.HazardWidth, HazardKey = t.HazardKey,
            }).ToList();
            var all = new List<TRow>();
            foreach (TRow r in rows)
            {
                all.Add(mutate != null && r.Set == "t1" ? mutate(r.Clone()) : r);
            }
            if (cloneToSet != null)
            {
                foreach (TRow r in rows.Where(x => x.Set == "t1"))
                {
                    TRow c = r.Clone();
                    c.Set = cloneToSet;
                    all.Add(cloneMutate != null ? cloneMutate(c) : c);
                }
            }
            var buf = new ByteBuf();
            buf.WriteSize(all.Count);
            foreach (TRow r in all)
            {
                buf.WriteString(r.Set + "." + r.Id);
                buf.WriteString(r.Set);
                buf.WriteString(r.Id);
                buf.WriteString(r.NameKey);
                buf.WriteInt(r.Act);
                buf.WriteInt(r.Faction);
                buf.WriteInt(r.Min);
                buf.WriteInt(r.Max);
                buf.WriteInt(r.Radius);
                buf.WriteString(r.HazardKind);
                buf.WriteInt(r.HazardWidth);
                buf.WriteString(r.HazardKey);
            }
            return buf;
        }

        private sealed class TRow
        {
            public string Set;
            public string Id;
            public string NameKey;
            public int Act;
            public int Faction;
            public int Min;
            public int Max;
            public int Radius;
            public string HazardKind;
            public int HazardWidth;
            public string HazardKey;

            public TRow Clone() => (TRow)MemberwiseClone();
        }

        private static ByteBuf TuningBuf(Func<string, float> real, params (string id, float value)[] overrides)
        {
            var ids = new List<string>();
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length >= 3 && f[0] == "H")
                {
                    ids.Add(f[1]);
                }
            }
            var buf = new ByteBuf();
            buf.WriteSize(ids.Count);
            foreach (string id in ids)
            {
                float v = real(id);
                foreach ((string oid, float ov) in overrides)
                {
                    if (oid == id)
                    {
                        v = ov;
                    }
                }
                buf.WriteString(id);
                buf.WriteFloat(v);
                buf.WriteString("selfcheck");
            }
            return buf;
        }

        // ── 存档信封（与 CampaignSaveService 的真实格式一致；校验和按正文与卡片重算）─────────────────

        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static void WriteEnvelope(int slot, TestEnvelope from, string payload)
        {
            var env = new TestEnvelope
            {
                SchemaVersion = from.SchemaVersion,
                ContentVersion = from.ContentVersion,
                Checksum = CampaignSaveService.ComputeChecksum(payload, from.CardJson),
                WrittenAtUtc = from.WrittenAtUtc,
                ProductVersion = from.ProductVersion,
                CardJson = from.CardJson,
                PayloadJson = payload,
            };
            File.WriteAllText(CampaignSaveService.SlotPath(slot), JsonUtility.ToJson(env));
        }

        // ── 小工具 ───────────────────────────────────────────────────────────────

        private static bool Near(Color32 a, Color32 b) => Math.Abs(a.r - b.r) <= 2 && Math.Abs(a.g - b.g) <= 2 && Math.Abs(a.b - b.b) <= 2;

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static bool Eq(float value, string text) => Mathf.Approximately(value, float.Parse(text, CultureInfo.InvariantCulture));

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
            }
        }

        private static string Tail(string output) =>
            string.Join(" / ", output.Replace("\r", string.Empty).Split('\n').Where(l => l.Length > 0).Reverse().Take(2).Reverse());

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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
            _report.AppendLine(text);
        }
    }
}
