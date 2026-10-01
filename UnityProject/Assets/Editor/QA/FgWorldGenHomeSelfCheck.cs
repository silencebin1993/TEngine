using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using Luban;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-GEN-01 世界生成器：规划层与家园区的自动验收（FG17 FGR-GEN-020～022、030～034、070、071、080、081、090；FG14 FGR-ARC-001；
    /// FGT-GEN-001 家园区版、003、004、008、009 的生成部分；FG-GAP-021 地貌起伏）。全部走真实生产入口：WorldGenContext / WorldGenService
    /// （规划层 + 起始区保证 + 地形来源）、Burst 生成与校验任务、HomeValleyController 的新战役播种、CampaignSaveService 真读写文件、
    /// 真 UXML 的新游戏设置 / 战略地图 / 小地图、WorldTerrainOverlay 的起伏网格。行为坏了会失败：
    /// A 数据：三张新表与源数据逐字段一致、版本行新增列、check_luban R29～R31 自测真跑、文本中英齐全。
    /// B 起始区四级保证（FGT-GEN-003）：50 个种子 × 标准 / 极端（资源低 + 据点高 + 污染高 + 领地近）/ 宽松起始区，**独立于生产校验器**
    ///   用托管代码逐格重算（第 1 级无悬崖与污染上限、开局布局平地、每项资源的连通区 + 整块、64 格内无侦察巢），与生产报告一致。
    /// C 局部重生成（FGR-GEN-031 末条 / 090）：强制某项不满足 → 只对这一项放保证点、其它项与远处区块逐字节不变、同一种子两次相同、写日志。
    /// D 领地布局与可达（FGT-GEN-004）：50 个种子 × 标准 / 近 / 远：夹角 ≥ 60°、互不重叠、不进家园区；8 个种子真寻路（层级寻路内核）
    ///   从核心走到每个阵营领地、白潮滩头预留区、全部侦察巢与家园区遗迹点（河流有浅滩，不隔断）。
    /// E 家园区确定性（FGT-GEN-001 家园区版）：家园区 196 个区块正序 / 倒序 / 乱序 / 流式一致；先去远处再回家、点位按不同顺序查询，结果相同。
    /// F 河流与矿带（FGR-GEN-020）：河道是水、浅滩可走、起点在起始区之外且向外流；矿带里对应矿种更密；v1 世界没有河流矿带。
    /// G 距离与威胁、遗迹点与侦察巢（FGR-GEN-030、032～034）：离核心越远污染越重、矿越多、侦察巢等级越高；点位落在可走格上、据点密度随设置变化。
    /// H 世界设置与分享短码（FGR-GEN-070、071，FGT-GEN-008）：分项代码规范化、文字种子、200 组短码往返世界相同、各种坏短码的原因。
    /// I 新战役、存读档与负向：新游戏世界进存档、侦察巢登记成据点（幂等）、标记与设置存读档逐字段一致、坏设置读档拒绝且原文件不变、存档卡摘要。
    /// J 新游戏设置界面（真 UXML）：默认种子、随机种子、文字种子、空种子禁用开始、分项按钮、复制 / 导入短码（含旧版本与各种错误）、返回 / 开始。
    /// K 战略地图与小地图（真 UXML）：领地按幕显示、预留区不显示、迷雾外的据点 / 遗迹不显示、筛选、标记加 / 改 / 删 / 上限、底图迷雾、连续缩放关图。
    /// L 地貌起伏（FG-GAP-021）：可走的格四角与格心都在 0 高度、悬崖隆起、水面下陷、相邻区块边界无裂缝、查询与画面一致。
    /// M 性能（FG17 第 7 节）、暂停 / 倍速 / 后台无关、生成器 v2 冻结基准（生成输入清单、规划层完整指纹、变异测试）、布局探针。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgWorldGenHomeSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;
        private const int ChunkSize = 32;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";

        /// <summary>种子测试集（FGR-GEN-091 初值 50 个）：正负交替，另含旅程用的 1 与 42。</summary>
        public static readonly int[] SeedSet = BuildSeedSet();

        private static int[] BuildSeedSet()
        {
            var list = new List<int> { 1, 42 };
            for (int i = 0; list.Count < 50; i++)
            {
                int s = unchecked(i * 7919 + 17) * (i % 2 == 0 ? 1 : -1);
                if (!list.Contains(s))
                {
                    list.Add(s);
                }
            }
            return list.ToArray();
        }

        public const string ExtremeCode = "R0O2P2D0S0";
        public const string RelaxedCode = "R1O1P1D1S1";

        [MenuItem("BinGames/QA/自检/FG 家园区生成（FG3-GEN-01）")]
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
            Line("\n[家园区生成] 世界生成器：规划层与家园区（FG3-GEN-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fghome-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                     $"工作线程 {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount} 个，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；生成器当前 v{WorldGenVersions.Current}");

                Step(CheckData);
                Step(CheckStartGuarantee);
                Step(CheckLocalRegeneration);
                Step(CheckTerritoryLayoutAndReach);
                Step(CheckHomeZoneDeterminism);
                Step(CheckRiversAndBelts);
                Step(CheckDistanceAndFeatures);
                Step(CheckSettingsAndShareCodes);
                Step(CheckNewGameFlowAndSave);
                Step(CheckNewGamePanel);
                Step(CheckStrategicMapAndMinimap);
                Step(CheckRelief);
                Step(CheckPerformanceAndClock);
                Step(CheckV2Frozen);
                Step(CheckLayoutProbes);
            }
            catch (Exception e)
            {
                Fail($"家园区生成自检抛异常：{e}");
            }
            finally
            {
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CampaignRandomService.SeedOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                GameSettings.SetLanguage(originalLanguage);
                StrategyClock.Reset();
                InputRouter.SetBuildMode(false);
                UiEscapeStack.Clear();
                WorldMapFilters.ResetAll();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                MinimapHudUIToolkit.InWorldOverrideForTests = false;
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

        /// <summary>自检用按键读取器：<see cref="Down"/> 这个键处于“刚按下”。</summary>
        private sealed class KeyReader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static WorldGenContext Ctx(int seed, string code = WorldGenContent.DefaultPresetId, int version = -1)
        {
            int v = version < 0 ? WorldGenVersions.Current : version;
            return WorldGenContext.Build(seed, v, WorldSettings.Resolve(v, code), new GridCell(0, 0), ChunkSize);
        }

        /// <summary>新战役（经主菜单同一入口 ApplyNewGameWorld 写世界设置）+ 家园控制器的真实播种（开局建筑、侦察巢）。</summary>
        private static CampaignState NewCampaign(int seed, string code = WorldGenContent.DefaultPresetId)
        {
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fghome-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, code));
            MethodInfo seedMethod = typeof(HomeValleyController).GetMethod("EnsureRegionSeeded", BindingFlags.NonPublic | BindingFlags.Static);
            seedMethod.Invoke(null, new object[] { s });
            CampaignSession.Set(0, s);
            return s;
        }

        private static ulong HashOf(IGridTerrainSource src, int cx, int cy)
        {
            var t = new byte[ChunkSize * ChunkSize];
            var p = new byte[ChunkSize * ChunkSize];
            src.FillChunk(cx, cy, ChunkSize, t, p);
            return WorldGenKernel.Hash64(t, p);
        }

        /// <summary>核心周围 [lo, hi] 区块范围的地形 / 污染（Burst 整块生成）。</summary>
        private sealed class Region
        {
            public readonly Dictionary<long, (byte[] t, byte[] p)> Chunks = new Dictionary<long, (byte[] t, byte[] p)>();

            public Region(WorldTerrainSource src, int lo, int hi)
            {
                for (int cy = lo; cy <= hi; cy++)
                {
                    for (int cx = lo; cx <= hi; cx++)
                    {
                        var t = new byte[ChunkSize * ChunkSize];
                        var p = new byte[ChunkSize * ChunkSize];
                        src.FillChunk(cx, cy, ChunkSize, t, p);
                        Chunks[HomeGridMap.Key(cx, cy)] = (t, p);
                    }
                }
            }

            public byte T(int x, int y)
            {
                int cx = (int)WorldGenMath.FloorDiv(x, ChunkSize);
                int cy = (int)WorldGenMath.FloorDiv(y, ChunkSize);
                (byte[] t, byte[] _) = Chunks[HomeGridMap.Key(cx, cy)];
                return t[(y - cy * ChunkSize) * ChunkSize + x - cx * ChunkSize];
            }

            public byte P(int x, int y)
            {
                int cx = (int)WorldGenMath.FloorDiv(x, ChunkSize);
                int cy = (int)WorldGenMath.FloorDiv(y, ChunkSize);
                (byte[] _, byte[] p) = Chunks[HomeGridMap.Key(cx, cy)];
                return p[(y - cy * ChunkSize) * ChunkSize + x - cx * ChunkSize];
            }
        }

        /// <summary>托管代码独立重算一项保证（不经生产的 Burst 校验器）：半径 r 的圆内，地形 code 的 4 邻接连通区（只数圆内）
        /// 至少 minCells 格、且含 sq×sq 的整块。</summary>
        private static bool ItemHolds(Region reg, int code, int r, int minCells, int sq, out int best)
        {
            int n = 2 * r + 1;
            var label = new int[n * n];
            for (int i = 0; i < label.Length; i++)
            {
                label[i] = -1;
            }
            var sizes = new List<int>();
            long r2 = (long)r * r;
            bool In(int i, int j) => (long)(i - r) * (i - r) + (long)(j - r) * (j - r) <= r2;
            var queue = new Queue<int>();
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (label[j * n + i] >= 0 || !In(i, j) || reg.T(i - r, j - r) != code)
                    {
                        continue;
                    }
                    int id = sizes.Count;
                    int size = 0;
                    label[j * n + i] = id;
                    queue.Enqueue(j * n + i);
                    while (queue.Count > 0)
                    {
                        int k = queue.Dequeue();
                        size++;
                        int ki = k % n;
                        int kj = k / n;
                        foreach ((int di, int dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int ni = ki + di;
                            int nj = kj + dj;
                            if (ni < 0 || nj < 0 || ni >= n || nj >= n || label[nj * n + ni] >= 0 || !In(ni, nj) || reg.T(ni - r, nj - r) != code)
                            {
                                continue;
                            }
                            label[nj * n + ni] = id;
                            queue.Enqueue(nj * n + ni);
                        }
                    }
                    sizes.Add(size);
                }
            }
            best = 0;
            bool ok = false;
            for (int j = 0; j + sq <= n; j++)
            {
                for (int i = 0; i + sq <= n; i++)
                {
                    int l = label[j * n + i];
                    if (l < 0)
                    {
                        continue;
                    }
                    bool all = true;
                    for (int b = 0; b < sq && all; b++)
                    {
                        for (int a = 0; a < sq && all; a++)
                        {
                            all = label[(j + b) * n + i + a] >= 0 && In(i + a, j + b);
                        }
                    }
                    if (all)
                    {
                        best = Math.Max(best, sizes[l]);
                        ok |= sizes[l] >= minCells;
                    }
                }
            }
            return ok;
        }

        /// <summary>对一个世界独立验证四级保证；返回不满足的原因（空 = 全部满足）。</summary>
        private static List<string> VerifyStart(WorldGenContext ctx, Region reg, Dictionary<string, bool> itemOk)
        {
            var why = new List<string>();
            StartGuaranteeReport rep = ctx.Plan.StartReport;
            int flat = rep.FlatRadius;
            byte cliff = GridContent.TerrainCode("cliff");
            byte water = GridContent.TerrainCode("water");
            byte buildable = GridContent.TerrainCode("buildable");
            for (int y = -flat; y <= flat; y++)
            {
                for (int x = -flat; x <= flat; x++)
                {
                    if (x * x + y * y > flat * flat)
                    {
                        continue;
                    }
                    if (reg.T(x, y) == cliff)
                    {
                        why.Add($"第 1 级：({x},{y}) 是悬崖");
                        goto flatDone;
                    }
                    if (reg.P(x, y) > rep.FlatPollutionCap)
                    {
                        why.Add($"第 1 级：({x},{y}) 污染 {reg.P(x, y)} > {rep.FlatPollutionCap}");
                        goto flatDone;
                    }
                }
            }
            flatDone:
            var cells = new List<GridCell>();
            foreach (StartLayout row in GridContent.StartLayout)
            {
                if ((row.Kind != "building" && row.Kind != "site") || !GridContent.TryGetBuilding(row.TypeId, out BuildingGrid g))
                {
                    continue;
                }
                GridMath.FootprintCells(new GridCell(row.OffsetX, row.OffsetY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(row.Rotation), cells);
                foreach (GridCell c in cells)
                {
                    if (reg.T(c.X, c.Y) != buildable || reg.P(c.X, c.Y) != 0)
                    {
                        why.Add($"第 2 级：开局布局 {row.AnchorId} 的占地格 {c} 不是无污染的可建平地");
                        break;
                    }
                }
            }
            foreach (StartGuaranteeItem i in rep.Items)
            {
                bool ok = ItemHolds(reg, GridContent.TerrainCode(i.Terrain), i.Radius, i.MinCells, i.MinSquare, out int best);
                itemOk[i.Item] = ok;
                if (!ok)
                {
                    why.Add($"{i.Item}：{i.Radius} 格内最大合格连通区 {best} 格（要 ≥{i.MinCells} 且含 {i.MinSquare}×{i.MinSquare}）");
                }
                if (ok != i.Satisfied)
                {
                    why.Add($"{i.Item}：独立重算（{ok}）与生产报告（{i.Satisfied}）不一致");
                }
            }
            foreach (WorldFeature f in WorldFeatures.HomeZoneNests(ctx))
            {
                if (f.Distance <= rep.NoOutpostRadius)
                {
                    why.Add($"第 3 级：侦察巢 {f.Id} 离核心 {f.Distance} 格 ≤ {rep.NoOutpostRadius}");
                }
                byte t = reg.Chunks.ContainsKey(HomeGridMap.Key((int)WorldGenMath.FloorDiv(f.X, ChunkSize), (int)WorldGenMath.FloorDiv(f.Y, ChunkSize)))
                    ? reg.T(f.X, f.Y)
                    : Sample(ctx, f.X, f.Y);
                if (t == cliff || t == water)
                {
                    why.Add($"侦察巢 {f.Id} 落在悬崖 / 水面上");
                }
            }
            if (!rep.AllSatisfied || rep.Failures.Count > 0)
            {
                why.Add("生产报告：" + string.Join("；", rep.Failures));
            }
            return why;
        }

        private static byte Sample(WorldGenContext ctx, int x, int y)
        {
            ctx.Source.Sample(x, y, out byte t, out _);
            return t;
        }

        // ── A. 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            WorldGenVersion v2 = WorldGenContent.Version(2);
            Expect(WorldGenVersions.Current == 2 && WorldGenContent.Versions.Count == 2 && v2.StartFlatRadius == 12 && v2.StartFlatPollution == 1
                   && v2.StartNoOutpostRadius == 64 && v2.GuaranteeSet == "g2" && v2.SettingSet == "a2" && v2.StartClearSet == "c1" && v2.TerritorySet == "t1"
                   && v2.PresetSet == WorldGenContent.NoSet && WorldGenContent.Version(1).StartClearSet == "c1" && WorldGenContent.Version(1).GuaranteeSet == WorldGenContent.NoSet,
                "生成器 v2 行入表：起始区第 1 级 12 格 / 污染 ≤ 1、第 3 级 64 格无据点、保证集合 g2、分项设置 a2、起始区快照 c1、领地集合沿用 t1；v1 行只多了快照 c1（其余新列不生效）");

            List<WorldStartGuarantee> g = WorldGenContent.GuaranteesFor(v2);
            bool fg17 = g.Count == 5 && g.Any(x => x.Terrain == "water" && x.Radius == 24) && g.Any(x => x.Terrain == "ore_metal" && x.Radius == 24)
                        && g.Any(x => x.Terrain == "ruin" && x.Radius == 24 && x.MinSquare >= 4) && g.Any(x => x.Terrain == "ore_rare" && x.Radius == 64)
                        && g.Any(x => x.Terrain == "oil" && x.Radius == 128);
            Expect(fg17, $"FGR-GEN-031 四级保证的资源项与表一致：24 格内水源 / 金属矿脉 / 废墟群（含回收站 4×4 整块）、64 格内稀土、128 格内油井（{g.Count} 项）");

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
                    case "WC":
                        rows++;
                        WorldStartClear c = WorldGenContent.StartClearRows.FirstOrDefault(x => x.Key == f[1]);
                        if (c == null || c.Set != f[2] || c.Variant != f[3] || c.Idx != int.Parse(f[4]) || c.MinX != int.Parse(f[5]) || c.MinY != int.Parse(f[6])
                            || c.MaxX != int.Parse(f[7]) || c.MaxY != int.Parse(f[8]))
                        {
                            diffs.Add("起始区快照 " + f[1]);
                        }
                        break;
                    case "WG":
                        rows++;
                        WorldStartGuarantee gg = WorldGenContent.GuaranteeRows.FirstOrDefault(x => x.Key == f[1]);
                        if (gg == null || gg.Set != f[2] || gg.Item != f[3] || gg.Terrain != f[4] || gg.Radius != int.Parse(f[5]) || gg.MinCells != int.Parse(f[6])
                            || gg.MinSquare != int.Parse(f[7]) || gg.StampRadius != int.Parse(f[8]) || gg.NameKey != f[9])
                        {
                            diffs.Add("起始区保证 " + f[1]);
                        }
                        break;
                    case "WA":
                        rows++;
                        WorldSettingAxis a = WorldGenContent.AxisRows.FirstOrDefault(x => x.Key == f[1]);
                        if (a == null || a.Set != f[2] || a.Axis != f[3] || a.Level != int.Parse(f[4]) || a.LevelId != f[5] || a.IsDefault != int.Parse(f[6])
                            || a.NameKey != f[7] || Math.Abs(a.Value - float.Parse(f[8], CultureInfo.InvariantCulture)) > 1e-6f)
                        {
                            diffs.Add("分项设置 " + f[1]);
                        }
                        break;
                    case "WV":
                        if (int.Parse(f[1]) == 2)
                        {
                            // 版本行新增列（第 27 列起）逐个比对。
                            bool bad = v2.StartClearSet != f[27] || v2.SettingSet != f[28] || v2.GuaranteeSet != f[29] || v2.StartFlatRadius != int.Parse(f[30])
                                       || v2.RiverCount != int.Parse(f[34]) || v2.RiverFordPeriod != int.Parse(f[41]) || v2.BeltCount != int.Parse(f[43])
                                       || Math.Abs(v2.BeltOreBonus - float.Parse(f[49], CultureInfo.InvariantCulture)) > 1e-6f || v2.PoiCellSize != int.Parse(f[50])
                                       || Math.Abs(v2.NestChance - float.Parse(f[52], CultureInfo.InvariantCulture)) > 1e-6f || v2.PoiShiftRadius != int.Parse(f[56])
                                       || v2.RiverOutwardMaxAngle != int.Parse(f[57]) || v2.RiverWidenTotal != int.Parse(f[58]) || v2.RelicCoreExclusion != int.Parse(f[59]);
                            if (bad)
                            {
                                diffs.Add("生成器 v2 新增列");
                            }
                        }
                        break;
                }
            }
            int runtime = WorldGenContent.StartClearRows.Count + WorldGenContent.GuaranteeRows.Count + WorldGenContent.AxisRows.Count;
            Expect(diffs.Count == 0 && rows == runtime && rows >= 40,
                $"源数据 fgdata_world.py 与运行时三张新表（起始区快照 / 起始区保证 / 分项设置）及版本行新增列逐字段一致（{rows} 行 / 运行时 {runtime} 行）" +
                (diffs.Count == 0 ? string.Empty : "——不一致：" + string.Join("，", diffs)));

            // FG-TOOL-01：同一轮全量自检里 check_luban 自测只真跑一次（输入 = tools/cell_tables 全部文件，指纹相同才复用），各段在同一份输出里核对自己的规则。
            (code, output) = QaPython.RunShared(root, "tools/cell_tables/check_luban.py --selftest", out bool selftestReused);
            if (selftestReused)
            {
                Line($"    · check_luban 自测：本轮前面的段已真跑过、输入指纹相同，复用同一次的输出（本轮真跑 {QaPython.SharedRuns} 次、复用 {QaPython.SharedHits} 次）");
            }
            Expect(code == 0 && output.Contains("改了开局布局却没有新快照") && output.Contains("保证点装不下回收站整块") && output.Contains("分项设置缺一个分项")
                   && output.Contains("浅滩周期不大于宽度") && output.Contains("标准档倍率不是 1"),
                $"check_luban 世界生成器 v2 规则（R29～R31：起始区快照罩住开局布局、保证点装得下、分项设置完整、河流浅滩可通行）自测真跑：{Tail(output)}");

            var keys = new[]
            {
                "world.guarantee.water", "world.guarantee.ruin", "world.poi.relic", "world.poi.scout_nest", "world.setting.axis.resource", "world.setting.axis.start",
                "world.setting.level.near", "world.setting.level.relaxed", "ui.newgame.title", "ui.newgame.random_seed", "ui.newgame.code_bad_checksum",
                "ui.newgame.code_newer", "ui.newgame.start", "ui.pause.copy_share", "ui.map.title", "ui.map.hint", "ui.map.marker_limit", "ui.minimap.title",
                "ui.map.filter.territory", "ui.map.filter.signal", "ui.map.territory_label", "ui.map.guarantee",
            };
            var bad2 = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                        || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            Expect(bad2.Count == 0, $"新游戏设置 / 分项设置 / 地图 / 保证项的文本中英两列齐全（抽查 {keys.Length} 条）{string.Join(",", bad2)}");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.StrategicMapFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.NewGameSetupFirstOpen)
                   && GuidanceHooks.Known.Contains(GuidanceHooks.MapMarkerFirstAdded),
                "引导钩子（只埋钩子，内容在 FG15-UX-04）：第一次打开战略地图 / 新游戏设置 / 第一次加地图标记");
        }

        // ── B. 起始区四级保证（FGT-GEN-003）────────────────────────────────────────

        private static void CheckStartGuarantee()
        {
            foreach ((string code, string label) in new[] { (WorldGenContent.DefaultPresetId, "标准"), (ExtremeCode, "极端（资源低 + 据点高 + 污染高 + 领地近）"), (RelaxedCode, "宽松起始区") })
            {
                var failures = new List<string>();
                var stamped = new Dictionary<string, int>();
                int nests = 0;
                double analyzeMs = 0;
                int flatRadius = 0;
                int noOutpost = 0;
                foreach (int seed in SeedSet)
                {
                    WorldGenContext ctx = Ctx(seed, code);
                    var reg = new Region(ctx.Source, -5, 4);
                    var ok = new Dictionary<string, bool>();
                    List<string> why = VerifyStart(ctx, reg, ok);
                    if (why.Count > 0)
                    {
                        failures.Add($"种子 {seed}：{string.Join("；", why.Take(3))}");
                    }
                    foreach (StartGuaranteeItem i in ctx.Plan.StartReport.Items)
                    {
                        stamped[i.Item] = (stamped.TryGetValue(i.Item, out int n) ? n : 0) + (i.Stamped ? 1 : 0);
                    }
                    nests += WorldFeatures.HomeZoneNests(ctx).Count;
                    analyzeMs += ctx.Plan.StartReport.AnalyzeMs;
                    flatRadius = ctx.Plan.StartReport.FlatRadius;
                    noOutpost = ctx.Plan.StartReport.NoOutpostRadius;
                }
                Expect(failures.Count == 0,
                    $"FGT-GEN-003【{label}】{SeedSet.Length} 个种子全部满足起始区四级保证（托管代码独立逐格重算，与生产报告一致）：{flatRadius} 格内无悬崖且污染 ≤ 1、" +
                    $"开局布局是无污染平地、24 格内水源 / 金属矿脉 / 废墟群、64 格内稀土且 {noOutpost} 格内没有侦察巢、128 格内油井" +
                    (failures.Count == 0 ? string.Empty : "——" + string.Join(" | ", failures.Take(5))));
                Line($"  · 【{label}】需要局部重生成的种子数：{string.Join("，", stamped.Select(kv => $"{kv.Key} {kv.Value}/{SeedSet.Length}"))}；" +
                     $"家园区侦察巢平均 {nests / (double)SeedSet.Length:F1} 个；起始区校验平均 {analyzeMs / SeedSet.Length:F1} ms / 世界（两遍 Burst 采样 + 连通区）");
            }
        }

        // ── C. 局部重生成（FGR-GEN-031 末条 / FGR-GEN-090）────────────────────────────────

        private static ByteBuf GuaranteeBuf(IEnumerable<(string item, string terrain, int radius, int cells, int sq, int stamp)> rows, string set = "g2")
        {
            var list = rows.ToList();
            var buf = new ByteBuf();
            buf.WriteSize(list.Count);
            foreach (var r in list)
            {
                buf.WriteString(set + "." + r.item);
                buf.WriteString(set);
                buf.WriteString(r.item);
                buf.WriteString(r.terrain);
                buf.WriteInt(r.radius);
                buf.WriteInt(r.cells);
                buf.WriteInt(r.sq);
                buf.WriteInt(r.stamp);
                buf.WriteString("world.guarantee." + r.terrain);
            }
            return buf;
        }

        private static List<(string item, string terrain, int radius, int cells, int sq, int stamp)> RealGuarantees() =>
            WorldGenContent.GuaranteesFor(WorldGenContent.Version(2)).Select(x => (x.Item, x.Terrain, x.Radius, x.MinCells, x.MinSquare, x.StampRadius)).ToList();

        private static void CheckLocalRegeneration()
        {
            const int seed = 424242;
            WorldGenContext baseCtx = Ctx(seed);
            var baseStamps = baseCtx.Plan.StartReport.Items.ToDictionary(i => i.Item, i => (i.Stamped, i.StampX, i.StampY));
            var baseHash = new Dictionary<long, ulong>();
            for (int cy = -5; cy <= 4; cy++)
            {
                for (int cx = -5; cx <= 4; cx++)
                {
                    baseHash[HomeGridMap.Key(cx, cy)] = HashOf(baseCtx.Source, cx, cy);
                }
            }
            // 强制油井一项不满足：保证半径 128 → 40、连通区至少 12 格（自然生成的油井在 40 格内很少这么大）。
            var rows = RealGuarantees().Select(r => r.item == "oil" ? (r.item, r.terrain, 40, 12, 3, 2) : r).ToList();
            WorldGenContent.OverrideForTests(guarantees: new TbWorldStartGuarantee(GuaranteeBuf(rows)));
            WorldGenContext forced = Ctx(seed);
            WorldGenContext forced2 = Ctx(seed);
            StartGuaranteeItem oil = forced.Plan.StartReport.Find("oil");
            bool othersSame = forced.Plan.StartReport.Items.Where(i => i.Item != "oil")
                .All(i => baseStamps.TryGetValue(i.Item, out var b) && b.Stamped == i.Stamped && b.StampX == i.StampX && b.StampY == i.StampY);
            int changed = 0;
            int changedOutside = 0;
            foreach (KeyValuePair<long, ulong> kv in baseHash)
            {
                HomeGridMap.Unkey(kv.Key, out int cx, out int cy);
                if (HashOf(forced.Source, cx, cy) == kv.Value)
                {
                    continue;
                }
                changed++;
                bool touches = oil != null && oil.Stamped && oil.StampX + oil.StampRadius >= cx * 32 && oil.StampX - oil.StampRadius <= cx * 32 + 31
                               && oil.StampY + oil.StampRadius >= cy * 32 && oil.StampY - oil.StampRadius <= cy * 32 + 31;
                if (!touches)
                {
                    changedOutside++;
                }
            }
            var reg = new Region(forced.Source, -5, 4);
            bool oilHolds = oil != null && ItemHolds(reg, GridContent.TerrainCode("oil"), 40, 12, 3, out _);
            bool logged = forced.Plan.StartReport.Log.Any(l => l.Contains("oil") && l.Contains("局部重生成"));
            WorldGenContent.ResetForTests();
            Expect(oil != null && !oil.NaturallySatisfied && oil.Stamped && oil.Satisfied && oilHolds && othersSame && changed >= 1 && changedOutside == 0 && logged
                   && forced.Plan.FullFingerprint() == forced2.Plan.FullFingerprint(),
                $"局部重生成：把油井保证收紧到 40 格内至少 12 格且含 3×3 → 自然生成不满足（{!oil?.NaturallySatisfied}），只对油井这一项按子种子放保证点 " +
                $"({oil?.StampX},{oil?.StampY}) 半径 {oil?.StampRadius}（第 {oil?.StampAttempt} 次试探）后满足（独立重算 {oilHolds}）；其它项的保证点不变（{othersSame}）；" +
                $"只有碰到这个圆盘的 {changed} 个区块变化、其它区块逐字节不变（圆盘外变化 {changedOutside}）；同一种子两次结果相同；写了日志（{logged}）");

            // 放不下时如实报失败、不卡死：保证半径小到装不下保证点。
            var impossible = RealGuarantees().Select(r => r.item == "ruin" ? (r.item, r.terrain, 5, 60, 4, 4) : r).ToList();
            WorldGenContent.OverrideForTests(guarantees: new TbWorldStartGuarantee(GuaranteeBuf(impossible)));
            var sw = Stopwatch.StartNew();
            WorldGenContext imp = Ctx(seed);
            sw.Stop();
            StartGuaranteeReport ir = imp.Plan.StartReport;
            WorldGenContent.ResetForTests();
            ExpectPerf(!ir.AllSatisfied && ir.Failures.Any(f => f.Contains("ruin")),
                $"负向：废墟群保证改成 5 格内装 60 格（放不下）→ 校验如实失败并写失败日志（“{ir.Failures.FirstOrDefault()}”），{sw.ElapsedMilliseconds} ms 内结束、不卡死",
                PerfGate.Lt(sw.ElapsedMilliseconds, 5000, "放不下时校验结束 ms"));
        }

        // ── D. 领地布局与可达（FGT-GEN-004）──────────────────────────────────────────

        private static void CheckTerritoryLayoutAndReach()
        {
            WorldGenVersion v2 = WorldGenContent.Version(2);
            foreach ((string code, string label) in new[] { (WorldGenContent.DefaultPresetId, "标准"), ("R1O1P1D0S0", "领地近"), ("R1O1P1D2S0", "领地远") })
            {
                var problems = new List<string>();
                int fallback = 0;
                foreach (int seed in SeedSet)
                {
                    WorldPlan p = WorldPlan.Compute(seed, v2, WorldSettings.Resolve(2, code), 0, 0);
                    var ts = p.Territories;
                    for (int a = 0; a < ts.Count; a++)
                    {
                        PlannedTerritory t = ts[a];
                        fallback += t.Fallback ? 1 : 0;
                        long d = WorldGenMath.Isqrt((long)t.CenterX * t.CenterX + (long)t.CenterY * t.CenterY);
                        if (d - t.OuterRadius <= v2.HomeZoneRadius)
                        {
                            problems.Add($"种子 {seed} {t.Id} 进入家园区");
                        }
                        for (int b = a + 1; b < ts.Count; b++)
                        {
                            PlannedTerritory u = ts[b];
                            long dx = t.CenterX - u.CenterX;
                            long dy = t.CenterY - u.CenterY;
                            long need = (long)t.OuterRadius + u.OuterRadius;
                            if (dx * dx + dy * dy < need * need)
                            {
                                problems.Add($"种子 {seed} {t.Id}/{u.Id} 重叠");
                            }
                            if (t.IsFaction && u.IsFaction && WorldPlan.AngleBetween(t.AngleDeg, u.AngleDeg) < v2.TerritoryMinAngle)
                            {
                                problems.Add($"种子 {seed} {t.Id}/{u.Id} 夹角 {WorldPlan.AngleBetween(t.AngleDeg, u.AngleDeg)}°");
                            }
                        }
                    }
                    if (p.Failures.Count > 0)
                    {
                        problems.Add($"种子 {seed} 规划失败：{p.Failures[0]}");
                    }
                }
                Expect(problems.Count == 0,
                    $"FGT-GEN-004【{label}】{SeedSet.Length} 个种子：四个阵营领地方向两两夹角 ≥ {v2.TerritoryMinAngle}°、全部领地与白潮滩头预留区（含危害带）互不重叠、都不进家园区（{v2.HomeZoneRadius} 格）" +
                    $"（兜底放置 {fallback} 次）" + (problems.Count == 0 ? string.Empty : "——" + string.Join("；", problems.Take(5))));
            }

            // 可达：层级寻路内核在真实地形（含河流、矿带、保证点）上从核心走到各处。
            var unreached = new List<string>();
            int searched = 0;
            long expanded = 0;
            double worstMs = 0;
            foreach (int seed in SeedSet.Take(8))
            {
                WorldGenContext ctx = Ctx(seed);
                NavConfig cfg = NavService.ConfigFromTuning();
                cfg.MaxExpansions = Math.Max(cfg.MaxExpansions, 400000);
                using var k = new NavKernel(cfg, NavService.TerrainTable(), true, ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones);
                var targets = new List<(string what, int x, int y)>();
                foreach (PlannedTerritory t in ctx.Plan.Territories)
                {
                    targets.Add((t.Id, t.CenterX, t.CenterY));
                }
                foreach (WorldFeature f in WorldFeatures.HomeZoneNests(ctx))
                {
                    targets.Add((f.Id, f.X, f.Y));
                }
                var relics = new List<WorldFeature>();
                WorldFeatures.Query(ctx, -200, -200, 200, 200, relics);
                foreach (WorldFeature f in relics.Where(x => x.Kind == WorldFeatureKind.Relic).Take(4))
                {
                    targets.Add((f.Id, f.X, f.Y));
                }
                var pts = new List<int2>();
                foreach ((string what, int x, int y) in targets)
                {
                    if (!NearestWalkable(ctx, x, y, 40, out int gx, out int gy))
                    {
                        unreached.Add($"种子 {seed} {what}：40 格内找不到可走的格");
                        continue;
                    }
                    var req = new NavRequest
                    {
                        OwnerTag = 9, OwnerKey = searched + 1, Serial = 1, Class = NavConst.ClassPlayer, Flags = NavRequestFlags.None,
                        Start = new int2(0, 0), Goal = new int2(gx, gy),
                    };
                    NavResult r = k.FindNow(req, pts, false, out double ms);
                    searched++;
                    expanded += r.Expanded;
                    worstMs = Math.Max(worstMs, ms);
                    if (r.Status != NavStatus.Ok)
                    {
                        unreached.Add($"种子 {seed} {what}（{gx},{gy}）：{r.Status}/{r.Reason}");
                    }
                }
            }
            Expect(unreached.Count == 0 && searched > 60,
                $"FGT-GEN-004 可达：8 个种子共 {searched} 条层级寻路（从核心出发）全部走到——每个阵营领地、白潮滩头预留区、全部家园区侦察巢与家园区遗迹点；" +
                $"河流按周期留浅滩不隔断（最长单次 {worstMs:F1} ms，共展开 {expanded} 个节点）" + (unreached.Count == 0 ? string.Empty : "——" + string.Join("；", unreached.Take(5))));
        }

        private static bool NearestWalkable(WorldGenContext ctx, int x, int y, int r, out int wx, out int wy)
        {
            byte cliff = ctx.Source.Params.CodeCliff;
            byte water = ctx.Source.Params.CodeWater;
            for (int ring = 0; ring <= r; ring++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }
                        byte t = Sample(ctx, x + dx, y + dy);
                        if (t != cliff && t != water)
                        {
                            wx = x + dx;
                            wy = y + dy;
                            return true;
                        }
                    }
                }
            }
            wx = x;
            wy = y;
            return false;
        }

        // ── E. 家园区确定性（FGT-GEN-001 家园区版）────────────────────────────────────────

        private static void CheckHomeZoneDeterminism()
        {
            const int seed = 777001;
            var region = new List<(int cx, int cy)>();
            for (int cy = -7; cy <= 6; cy++)
            {
                for (int cx = -7; cx <= 6; cx++)
                {
                    region.Add((cx, cy));
                }
            }
            CampaignState s = CampaignState.CreateNew("fghome-det", "Standard", seed);
            CampaignFgStateDomains.EnsureAll(s);
            HomeGridMap Fresh() => new HomeGridMap(ChunkSize, WorldGenService.CreateSource(s, WorldGenContent.EarthSurfaceId), WorldGenContent.EarthSurfaceId);
            HomeGridMap forward = Fresh();
            foreach ((int cx, int cy) in region)
            {
                forward.ChunkAt(new GridCell(cx * 32, cy * 32), out _);
            }
            HomeGridMap backward = Fresh();
            foreach ((int cx, int cy) in Enumerable.Reverse(region))
            {
                backward.ChunkAt(new GridCell(cx * 32 + 31, cy * 32 + 31), out _);
            }
            var rng = new System.Random(11);
            HomeGridMap shuffled = Fresh();
            foreach ((int cx, int cy) in region.OrderBy(_ => rng.Next()))
            {
                shuffled.ChunkAt(new GridCell(cx * 32 + 3, cy * 32 + 7), out _);
            }
            HomeGridMap streamed = Fresh();
            var streamer = new WorldChunkStreamer(streamed);
            try
            {
                foreach ((int cx, int cy) in region.Where(r => (r.cx + r.cy) % 3 == 0))
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
                ulong h = WorldGenKernel.Hash64(forward.TryGetLoaded(cx, cy).Terrain, forward.TryGetLoaded(cx, cy).Pollution);
                HomeGridMap.Chunk b = backward.TryGetLoaded(cx, cy);
                HomeGridMap.Chunk c = shuffled.TryGetLoaded(cx, cy);
                HomeGridMap.Chunk d = streamed.TryGetLoaded(cx, cy);
                if (WorldGenKernel.Hash64(b.Terrain, b.Pollution) != h || WorldGenKernel.Hash64(c.Terrain, c.Pollution) != h
                    || (d != null && WorldGenKernel.Hash64(d.Terrain, d.Pollution) != h))
                {
                    mismatch++;
                }
            }
            Expect(mismatch == 0 && streamed.AdoptedCount > 50,
                $"FGT-GEN-001（家园区）：核心周围 {region.Count} 个区块（家园区 200 格，含河流起点、矿带、起始区保证点）正序 / 倒序 / 乱序 / 流式（工作线程）逐格一致（不一致 {mismatch}）");

            // 规划与点位与访问顺序无关：先生成远处 → 再回家，规划层完整指纹与点位相同；点位按不同矩形顺序查询结果相同。
            WorldGenContext a = Ctx(seed);
            string fpA = a.Plan.FullFingerprint();
            WorldGenContext b2 = Ctx(seed);
            HashOf(b2.Source, 2000, -1500);
            HashOf(b2.Source, -40, 60);
            var q1 = new List<WorldFeature>();
            WorldFeatures.Query(a, -400, -400, 400, 400, q1);
            var q2 = new List<WorldFeature>();
            WorldFeatures.Query(b2, 0, 0, 400, 400, q2);
            WorldFeatures.Query(b2, -400, -400, -1, 400, q2);
            WorldFeatures.Query(b2, 0, -400, 400, -1, q2);
            string Key(IEnumerable<WorldFeature> fs) => string.Join(";", fs.Select(f => $"{f.Id}@{f.X},{f.Y}t{f.Tier}").OrderBy(x => x, StringComparer.Ordinal));
            Expect(fpA == b2.Plan.FullFingerprint() && Key(q1) == Key(q2) && q1.Count > 5,
                $"规划层（领地 / 河流 / 矿带 / 起始区保证点）与点位（遗迹点 / 侦察巢）只由种子决定：先去远处再回家、按不同矩形顺序查询，结果逐个相同（{q1.Count} 个点位）");
        }

        // ── F. 河流与矿带（FGR-GEN-020）────────────────────────────────────────────

        private static void CheckRiversAndBelts()
        {
            var problems = new List<string>();
            int riverWater = 0;
            int riverCells = 0;
            int fordCells = 0;
            int beltOre = 0;
            int beltCells = 0;
            int outOre = 0;
            int outCells = 0;
            WorldGenVersion v2 = WorldGenContent.Version(2);
            foreach (int seed in SeedSet.Take(10))
            {
                WorldGenContext ctx = Ctx(seed);
                WorldPlan p = ctx.Plan;
                if (p.Rivers.Count != v2.RiverCount || p.Belts.Count != v2.BeltCount)
                {
                    problems.Add($"种子 {seed}：河流 {p.Rivers.Count}、矿带 {p.Belts.Count}");
                    continue;
                }
                foreach (PlannedRiver r in p.Rivers)
                {
                    GridPoint first = r.Points[0];
                    GridPoint last = r.Points[r.Points.Count - 1];
                    long d0 = WorldGenMath.Isqrt((long)first.X * first.X + (long)first.Y * first.Y);
                    long d1 = WorldGenMath.Isqrt((long)last.X * last.X + (long)last.Y * last.Y);
                    if (d0 < v2.RiverStartDistance - 1 || d1 <= d0 + v2.RiverSegmentLength)
                    {
                        problems.Add($"种子 {seed} 河 {r.Index}：起点离核心 {d0}、终点 {d1}（应从起始区外向外流）");
                    }
                    // 沿河道中线采样：水面，浅滩处可走。
                    for (int i = 0; i + 1 < r.Points.Count; i++)
                    {
                        GridPoint a = r.Points[i];
                        GridPoint b = r.Points[i + 1];
                        for (int k = 0; k < 8; k++)
                        {
                            int x = a.X + (b.X - a.X) * k / 8;
                            int y = a.Y + (b.Y - a.Y) * k / 8;
                            if (x * x + y * y < 40 * 40)
                            {
                                continue;
                            }
                            byte t = Sample(ctx, x, y);
                            riverCells++;
                            riverWater += t == ctx.Source.Params.CodeWater ? 1 : 0;
                            fordCells += t == ctx.Source.Params.CodeBuildable ? 1 : 0;
                        }
                    }
                }
                byte metal = GridContent.TerrainCode("ore_metal");
                foreach (PlannedBelt b in p.Belts.Where(x => x.Terrain == "ore_metal"))
                {
                    for (int k = 0; k <= 20; k++)
                    {
                        int cx = b.X0 + (b.X1 - b.X0) * k / 20;
                        int cy = b.Y0 + (b.Y1 - b.Y0) * k / 20;
                        for (int d = -6; d <= 6; d += 3)
                        {
                            beltCells++;
                            beltOre += Sample(ctx, cx + d, cy - d) == metal ? 1 : 0;
                            // 对照：同一距离、方向转 90° 的点（矿带外）。
                            outCells++;
                            outOre += Sample(ctx, -(cy - d), cx + d) == metal ? 1 : 0;
                        }
                    }
                }
            }
            double waterRatio = riverWater / (double)Math.Max(1, riverCells);
            Expect(problems.Count == 0 && waterRatio > 0.8 && fordCells > 0,
                $"河流：10 个种子各 {v2.RiverCount} 条，从离核心 {v2.RiverStartDistance} 格处向外流；河道中线 {riverCells} 个采样点里 {waterRatio:P0} 是水面，{fordCells} 个是浅滩（可走可建）" +
                (problems.Count == 0 ? string.Empty : "——" + string.Join("；", problems.Take(4))));
            double inRate = beltOre / (double)Math.Max(1, beltCells);
            double outRate = outOre / (double)Math.Max(1, outCells);
            Expect(inRate > outRate * 1.5 && inRate > 0.05,
                $"矿带：金属矿带内金属矿脉占 {inRate:P1}，同距离矿带外 {outRate:P1}（矿带把矿集中成带，FGR-GEN-020）");

            WorldGenContext v1 = Ctx(1, WorldGenContent.DefaultPresetId, 1);
            Expect(v1.Plan.Rivers.Count == 0 && v1.Plan.Belts.Count == 0 && v1.Plan.StartReport == null && v1.Source.Zones.All(z => z.Kind == WorldGenZoneKind.Territory)
                   && v1.Source.Params.FlatRadius == 0 && WorldFeatures.HomeZoneNests(v1).Count == 0,
                "生成器 v1 世界（旧存档）不受影响：没有河流、矿带、起始区保证点、侦察巢，内核形状只有领地（v1 回归哈希见 [世界生成] I 段）");
        }

        // ── G. 距离与威胁、遗迹点与侦察巢（FGR-GEN-030、032～034）──────────────────────────

        private static void CheckDistanceAndFeatures()
        {
            WorldGenContext ctx = Ctx(31337);
            double Ring(int r0, int r1, Func<byte, byte, int> f)
            {
                int n = 0;
                int sum = 0;
                for (int a = 0; a < 360; a += 3)
                {
                    for (int r = r0; r <= r1; r += 17)
                    {
                        int x = (int)(r * Math.Cos(a * Math.PI / 180));
                        int y = (int)(r * Math.Sin(a * Math.PI / 180));
                        ctx.Source.Sample(x, y, out byte t, out byte p);
                        sum += f(t, p);
                        n++;
                    }
                }
                return sum / (double)Math.Max(1, n);
            }
            byte metal = GridContent.TerrainCode("ore_metal");
            byte rare = GridContent.TerrainCode("ore_rare");
            double polNear = Ring(40, 180, (t, p) => p);
            double polFar = Ring(1800, 2400, (t, p) => p);
            double oreNear = Ring(40, 180, (t, p) => t == metal || t == rare ? 1 : 0);
            double oreFar = Ring(1800, 2400, (t, p) => t == metal || t == rare ? 1 : 0);
            Expect(polFar > polNear + 0.5 && oreFar > oreNear,
                $"FGR-GEN-032：离核心越远污染越重（平均等级 {polNear:F2} → {polFar:F2}）、资源越丰富（矿脉比例 {oreNear:P1} → {oreFar:P1}）；中间有局部浓淡（噪声）");

            var nestTiers = new List<(int d, int tier)>();
            var relics = 0;
            var wrongPlace = new List<string>();
            foreach (int seed in SeedSet.Take(20))
            {
                WorldGenContext c = Ctx(seed);
                foreach (WorldFeature f in WorldFeatures.HomeZoneNests(c))
                {
                    nestTiers.Add((f.Distance, f.Tier));
                }
                var list = new List<WorldFeature>();
                WorldFeatures.Query(c, -600, -600, 600, 600, list);
                foreach (WorldFeature f in list)
                {
                    relics += f.Kind == WorldFeatureKind.Relic ? 1 : 0;
                    byte t = Sample(c, f.X, f.Y);
                    if (t == c.Source.Params.CodeCliff || t == c.Source.Params.CodeWater)
                    {
                        wrongPlace.Add($"{seed}:{f.Id}");
                    }
                }
            }
            bool tiersRise = nestTiers.Count > 20 && nestTiers.Where(x => x.d < 110).All(x => x.tier == 1) && nestTiers.Any(x => x.tier >= 2)
                             && nestTiers.Where(x => x.tier == 3).All(x => x.d >= 64 + 2 * WorldGenContent.Version(2).NestTierDistance);
            Expect(tiersRise && relics > 20 && wrongPlace.Count == 0,
                $"FGR-GEN-030 / 032 / 033 / 034：20 个种子家园区共 {nestTiers.Count} 个侦察巢，离核心越远等级越高（1 级都在 110 格内侧起步、3 级都在 {64 + 2 * WorldGenContent.Version(2).NestTierDistance} 格外）；" +
                $"±600 格内 {relics} 个遗迹点；全部点位都在可走的格上（落在悬崖 / 水面的按规则挪位）{(wrongPlace.Count == 0 ? string.Empty : "——落错 " + string.Join(",", wrongPlace.Take(5)))}");

            int Count(string code) => SeedSet.Take(20).Sum(sd => WorldFeatures.HomeZoneNests(Ctx(sd, code)).Count);
            int low = Count("R1O0P1D1S0");
            int std = Count(WorldGenContent.DefaultPresetId);
            int high = Count("R1O2P1D1S0");
            Expect(low < std && std < high, $"世界设置“敌方据点密度”生效：20 个种子家园区侦察巢 低 {low} < 标准 {std} < 高 {high}");

            WorldGenContext relaxed = Ctx(SeedSet[3], RelaxedCode);
            Expect(relaxed.Plan.StartReport.FlatRadius == 18 && relaxed.Plan.StartReport.NoOutpostRadius == 96
                   && WorldFeatures.HomeZoneNests(relaxed).All(f => f.Distance > 96),
                $"世界设置“起始区：宽松”：第 1 级半径 12 → {relaxed.Plan.StartReport.FlatRadius} 格、无据点半径 64 → {relaxed.Plan.StartReport.NoOutpostRadius} 格，侦察巢都在其外");
        }

        // ── H. 世界设置与分享短码（FGR-GEN-070、071，FGT-GEN-008）──────────────────────────────

        private static void CheckSettingsAndShareCodes()
        {
            WorldGenVersion v2 = WorldGenContent.Version(2);
            WorldSettings std = WorldSettings.Resolve(2, WorldGenContent.DefaultPresetId);
            WorldSettings ext = WorldSettings.Resolve(2, ExtremeCode);
            bool canon = WorldSettings.CodeOf(v2, WorldSettings.DefaultLevels(v2)) == WorldGenContent.DefaultPresetId
                         && WorldSettings.TryResolve(2, "R1O1P1D1S0", out WorldSettings same, out _) && same.Id == WorldGenContent.DefaultPresetId
                         && !WorldSettings.TryResolve(2, "R3O1P1D1S0", out _, out _) && !WorldSettings.TryResolve(2, "rich", out _, out _)
                         && !WorldSettings.TryResolve(2, "R1O1P1D1", out _, out _) && WorldSettings.TryResolve(1, WorldGenContent.DefaultPresetId, out WorldSettings v1s, out _) && !v1s.IsAxisBased;
            Expect(canon && Math.Abs(ext.ResourceAbundance - 0.7f) < 1e-6f && Math.Abs(ext.OutpostDensity - 1.6f) < 1e-6f && Math.Abs(ext.PollutionIntensity - 1.4f) < 1e-6f
                   && Math.Abs(ext.TerritoryDistanceScale - 0.8f) < 1e-6f && Math.Abs(std.StartScale - 1f) < 1e-6f,
                "FGR-GEN-070：五个分项（资源丰度 / 据点密度 / 污染强度 / 领地距离 / 起始区）按档取数值；分项代码规范化（全标准 = default）；越界档 / 旧预设名 / 残缺代码拒绝；v1 仍按整套预设");
            Expect(ext.DisplayName().Contains("资源") && ext.DisplayName().Contains("低") && std.DisplayName() == GameText.Get("world.setting.all_standard"),
                $"设置显示名：极端 = “{ext.DisplayName()}”，标准 = “{std.DisplayName()}”（存档卡 / 暂停菜单 / 新游戏界面共用）");

            int n2 = 0;
            bool seedOk = WorldSettings.TryParseSeed(" -123 ", out int n1, out bool t1) && n1 == -123 && !t1
                          && WorldSettings.TryParseSeed("归还之地", out n2, out bool t2) && t2 && WorldSettings.TryParseSeed("归还之地", out int n3, out _) && n2 == n3
                          && WorldSettings.TryParseSeed("归还之地 ", out int n4, out _) && n4 == n2 && !WorldSettings.TryParseSeed("  ", out _, out _)
                          && WorldSettings.TryParseSeed("99999999999", out int n5, out bool t5) && t5 && n5 != 0;
            Expect(seedOk, $"FGR-GEN-001 种子输入：整数原样、任意文字按 UTF-8 FNV-1a 换算（“归还之地” → {n2}，同一段文字永远同一种子）、首尾空白忽略、超出 int 的数字按文字处理、空白拒绝");

            var rng = new System.Random(20260929);
            int roundTripBad = 0;
            int worldBad = 0;
            for (int i = 0; i < 200; i++)
            {
                int seed = rng.Next(int.MinValue, int.MaxValue);
                var levels = new int[WorldSettings.Axes.Length];
                for (int a = 0; a < levels.Length; a++)
                {
                    levels[a] = rng.Next(WorldSettings.LevelCount(v2, a));
                }
                WorldSettings s = WorldSettings.FromLevels(v2, levels);
                string code = WorldSettings.EncodeShareCode(seed, s);
                WorldSettings.ShareError e = WorldSettings.TryDecodeShareCode(code, out int back, out WorldSettings bs, out int ver);
                if (e != WorldSettings.ShareError.None || back != seed || ver != 2 || bs.Id != s.Id || code.Length != 18 || !code.StartsWith("BGR-", StringComparison.Ordinal))
                {
                    roundTripBad++;
                }
                if (i < 6)
                {
                    WorldGenContext a1 = WorldGenContext.Build(seed, 2, s, new GridCell(0, 0), ChunkSize);
                    WorldGenContext a2 = WorldGenContext.Build(back, ver, bs, new GridCell(0, 0), ChunkSize);
                    if (HashOf(a1.Source, 0, 0) != HashOf(a2.Source, 0, 0) || HashOf(a1.Source, 3, -4) != HashOf(a2.Source, 3, -4)
                        || a1.Plan.FullFingerprint() != a2.Plan.FullFingerprint())
                    {
                        worldBad++;
                    }
                }
            }
            string sample = WorldSettings.EncodeShareCode(123456, ext);
            string sloppy = sample.ToLowerInvariant().Replace("-", " ").Replace("bgr", string.Empty).Replace('0', 'o');
            bool tolerant = WorldSettings.TryDecodeShareCode(sloppy, out int ts, out WorldSettings tset, out _) == WorldSettings.ShareError.None && ts == 123456 && tset.Id == ext.Id;
            Expect(roundTripBad == 0 && worldBad == 0 && tolerant,
                $"FGT-GEN-008：200 组随机（种子, 分项设置）导出短码（如 {sample}）再导入逐个还原，其中 6 组两边生成的区块哈希与规划层完整指纹相同（坏 {roundTripBad} / 世界不同 {worldBad}）；" +
                "容错：小写、空格代替短横、省略 BGR 前缀、字母 O 当 0");

            char[] chars = sample.ToCharArray();
            chars[6] = chars[6] == 'A' ? 'B' : 'A';
            WorldSettings.ShareError eChecksum = WorldSettings.TryDecodeShareCode(new string(chars), out _, out _, out _);
            WorldSettings.ShareError eFormat = WorldSettings.TryDecodeShareCode("BGR-12-34", out _, out _, out _);
            WorldSettings.ShareError eChar = WorldSettings.TryDecodeShareCode("BGR-UUUU-UUUU-UUUU", out _, out _, out _);
            WorldSettings.ShareError eNewer = WorldSettings.TryDecodeShareCode(ForgeCode(123, 63, 0), out _, out _, out int newerVer);
            WorldSettings.ShareError eBad = WorldSettings.TryDecodeShareCode(ForgeCode(123, 2, 4000), out _, out _, out _);
            WorldSettings.ShareError eV0 = WorldSettings.TryDecodeShareCode(ForgeCode(123, 0, 0), out _, out _, out _);
            string v1Code = WorldSettings.EncodeShareCode(-77, WorldSettings.Resolve(1, WorldGenContent.DefaultPresetId));
            WorldSettings.ShareError eV1 = WorldSettings.TryDecodeShareCode(v1Code, out int v1Seed, out WorldSettings v1Set, out int v1Ver);
            Expect(eChecksum == WorldSettings.ShareError.BadChecksum && eFormat == WorldSettings.ShareError.BadFormat && eChar == WorldSettings.ShareError.BadFormat
                   && eNewer == WorldSettings.ShareError.Newer && newerVer == 63 && eBad == WorldSettings.ShareError.BadSettings && eV0 == WorldSettings.ShareError.BadSettings
                   && eV1 == WorldSettings.ShareError.None && v1Seed == -77 && v1Ver == 1 && !v1Set.IsAxisBased,
                "负向：抄错一位 → 校验失败；长度 / 字符（U 不在字母表）不对 → 格式错；来自更新版本的游戏 → “版本更新”；设置序号越界 / 版本 0 → 设置不认识；" +
                "生成器 v1 的短码照样导入（世界按 v1 生成，与分享者相同）");
        }

        /// <summary>按短码格式伪造（种子, 版本, 设置序号）的短码（校验码正确），测试解码的语义检查。</summary>
        private static string ForgeCode(int seed, int version, int index)
        {
            ulong payload = ((ulong)(uint)seed << 18) | ((ulong)(version & 0x3F) << 12) | (ulong)(index & 0xFFF);
            MethodInfo checksum = typeof(WorldSettings).GetMethod("Checksum", BindingFlags.NonPublic | BindingFlags.Static);
            ulong code = (payload << 10) | (ulong)checksum.Invoke(null, new object[] { payload });
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var chars = new char[12];
            for (int i = 11; i >= 0; i--)
            {
                chars[i] = alphabet[(int)(code & 31UL)];
                code >>= 5;
            }
            return "BGR-" + new string(chars, 0, 4) + "-" + new string(chars, 4, 4) + "-" + new string(chars, 8, 4);
        }

        // ── I. 新战役、存读档与负向 ──────────────────────────────────────────────────

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

        private static void CheckNewGameFlowAndSave()
        {
            const int seed = 8675309;
            CampaignState s = NewCampaign(seed, ExtremeCode);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            List<WorldFeature> nests = WorldFeatures.HomeZoneNests(ctx);
            OutpostRecord[] outposts = WorldOutpostSystem.Outposts(s).ToArray();
            bool seeded = s.World.HomeOutpostsSeeded && outposts.Length == nests.Count && nests.Count > 0
                          && nests.All(f => outposts.Any(o => o.OutpostId == f.Id && o.CellX == f.X && o.CellY == f.Y && o.Kind == "scout_nest" && o.Tier == f.Tier
                                                               && o.Garrison == Math.Min(o.GarrisonCap, 2 * f.Tier)));
            int again = WorldGenService.SeedHomeZoneOutposts(s);
            Expect(s.World.WorldSeed == seed && s.RandomSeed == seed && s.World.GeneratorVersion == 2 && s.World.WorldSettingsId == ExtremeCode && seeded && again == 0
                   && s.BuildingRecords.Any(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore),
                $"新战役（主菜单同一入口 ApplyNewGameWorld）：种子 / 生成器 v2 / 世界设置 {ExtremeCode} 写进存档域；家园控制器播种开局建筑，并把家园区 {nests.Count} 个侦察巢登记成据点" +
                "（据点 ID = 点位 ID、坐标 / 等级 / 驻军 = 等级 × 2 一致）；再登记一次什么也不加（幂等）");

            string NestKey(CampaignState st) => string.Join(";", WorldOutpostSystem.Outposts(st).Select(o => $"{o.OutpostId}@{o.CellX},{o.CellY}t{o.Tier}g{o.Garrison}/{o.GarrisonCap}"));
            string BuildingKey(CampaignState st) => string.Join(";", st.BuildingRecords.Select(b => $"{b.BuildingId}@{b.GridX},{b.GridY}r{b.Rotation}"));
            string nestsA = NestKey(s);
            string buildingsA = BuildingKey(s);
            string fpA = WorldGenService.PlanFor(s).FullFingerprint();
            ulong hA = HashOf(WorldGenService.ContextFor(s).Source, 1, 1);

            // 存读档：设置、据点、标记、区块修改都进存档；读档后规划层重算、世界逐字节相同。
            MapMarkerRecord m = WorldMapMarkers.Add(s, WorldGenContent.EarthSurfaceId, 55, -33);
            WorldMapMarkers.SetNote(s, m.MarkerId, "这里有废墟");
            HomeGridMap map = HomeGridService.MapFor(s);
            map.SetTerrain(new GridCell(70, 70), GridContent.TerrainCode("water"));
            string fp = ctx.Plan.FullFingerprint();
            ulong h00 = HashOf(ctx.Source, 0, 0);
            SaveResult saved = CampaignSaveService.Save(1, s, SaveReason.Manual);
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(1);
            bool same = false;
            string detail = "读档失败";
            if (loaded.Success)
            {
                CampaignState l = loaded.State;
                CampaignSession.Set(1, l);
                WorldGenContext lc = WorldGenService.ContextFor(l);
                MapMarkerRecord lm = WorldMapMarkers.All(l).FirstOrDefault();
                same = l.World.WorldSettingsId == ExtremeCode && l.World.GeneratorVersion == 2 && l.World.HomeOutpostsSeeded
                       && WorldOutpostSystem.Outposts(l).Count == outposts.Length && lm != null && lm.Note == "这里有废墟" && lm.CellX == 55 && l.World.NextMapMarkerSerial == 2
                       && lc.Plan.FullFingerprint() == fp && HashOf(lc.Source, 0, 0) == h00
                       && HomeGridService.MapFor(l).GetTerrain(new GridCell(70, 70)) == GridContent.TerrainCode("water")
                       && WorldGenService.SeedHomeZoneOutposts(l) == 0;
                detail = $"设置 {l.World.WorldSettingsId}、据点 {WorldOutpostSystem.Outposts(l).Count}、标记“{lm?.Note}”";
            }
            Expect(saved.Success && same,
                $"存读档：世界设置、侦察巢据点、玩家标记与备注、被修改的格子逐字段恢复；规划层（不存档）读档后按种子重算、完整指纹与区块哈希相同；读档后不会重复登记侦察巢（{detail}）");

            string card = CampaignSlotText.CardText(CampaignSaveService.GetSlotMetadata(1));
            Expect(card.Contains(WorldSettings.Resolve(2, ExtremeCode).DisplayName()),
                $"存档卡显示种子与世界设置摘要（FG17 第 4 节）：“{card.Replace("\n", " / ")}”");

            // 负向：存档里的世界设置代码坏了 → 读档拒绝（正文损坏），原文件不变。
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(CampaignSaveService.SlotPath(1)));
            string payload = env.PayloadJson.Replace("\"WorldSettingsId\":\"" + ExtremeCode + "\"", "\"WorldSettingsId\":\"R9O9P9D9S9\"");
            env.Checksum = CampaignSaveService.ComputeChecksum(payload, env.CardJson);
            env.PayloadJson = payload;
            File.WriteAllText(CampaignSaveService.SlotPath(1), JsonUtility.ToJson(env));
            string before = File.ReadAllText(CampaignSaveService.SlotPath(1));
            LoadResult refused = CampaignSaveService.Load(1);
            Expect(payload.Contains("R9O9P9D9S9") && !refused.Success && refused.Reason == SaveFailureReason.Payload && File.ReadAllText(CampaignSaveService.SlotPath(1)) == before,
                $"负向：存档的世界设置代码在生成器 v2 里不存在 → 读档拒绝为“正文损坏”（“{refused.Message}”），原文件不变");

            // 负向“同一种子两次开局完全相同”：再开一次同种子、同设置的新战役，侦察巢据点、规划层、开局建筑与区块逐个相同。
            CampaignState twin = NewCampaign(seed, ExtremeCode);
            bool twinSame = NestKey(twin) == nestsA && BuildingKey(twin) == buildingsA && WorldGenService.PlanFor(twin).FullFingerprint() == fpA
                            && HashOf(WorldGenService.ContextFor(twin).Source, 1, 1) == hA;
            Expect(twinSame, $"负向：同一种子、同一设置两次开局完全相同——侦察巢据点、开局建筑、规划层完整指纹、区块内容逐个相同（{outposts.Length} 个据点）");

            // 旧世界（生成器 v1）：不登记侦察巢，规划层没有河流 / 保证点。
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState old = CampaignState.CreateNew("fghome-v1", "Standard", 5);
            WorldGenService.ApplyNewGameWorld(old, 5, WorldSettings.Resolve(1, WorldGenContent.DefaultPresetId));
            typeof(HomeValleyController).GetMethod("EnsureRegionSeeded", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { old });
            Expect(old.World.GeneratorVersion == 1 && WorldOutpostSystem.Outposts(old).Count == 0 && WorldGenService.PlanFor(old).Rivers.Count == 0,
                "导入旧版本短码开的 v1 世界：按 v1 生成（没有侦察巢、河流、保证点），与分享者的世界相同");
        }

        // ── J. 新游戏设置界面（真 UXML）──────────────────────────────────────────────

        private static bool InvokeClickable(UnityEngine.UIElements.Button b)
        {
            if (b == null || b.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private static void CheckNewGamePanel()
        {
            var go = new GameObject("__fghome_newgame") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "NewGamePanel.uxml").CloneTree();
                NewGamePanelUIToolkit panel = go.AddComponent<NewGamePanelUIToolkit>();
                panel.BindView(root);
                int started = 0;
                int backed = 0;
                int gotSeed = 0;
                WorldSettings gotSettings = null;
                CampaignRandomService.SeedOverrideForTests = 777;
                NewGamePanelUIToolkit.Open((sd, st) => { started++; gotSeed = sd; gotSettings = st; }, () => backed++);
                bool opened = NewGamePanelUIToolkit.IsOpen && panel.PanelVisible && panel.SeedFieldText == "777" && panel.AxisRowCount == 5
                              && WorldSettings.TryDecodeShareCode(panel.ShareFieldText, out int ds, out WorldSettings dset, out int dv) == WorldSettings.ShareError.None
                              && ds == 777 && dv == 2 && dset.Id == WorldGenContent.DefaultPresetId && panel.GeneratorText.Contains("v2")
                              && panel.LevelButton(0, 1).text.StartsWith("▸", StringComparison.Ordinal) && InputRouter.ModalUiOpen;
                Expect(opened, $"新游戏设置打开：默认种子来自 CampaignRandomService（旅程固定种子 777 生效），五个分项各一排按钮（标准档有“▸”），分享短码 {panel.ShareFieldText} 随之生成，生成器 v2，模态");

                InvokeClickable(panel.RandomButton);
                bool randomChanged = panel.SeedFieldText != "777" && panel.TryCurrentSeed(out _, out bool fromText) && !fromText;
                panel.SetSeedText("归还之地");
                WorldSettings.TryParseSeed("归还之地", out int textSeed, out _);
                bool textOk = panel.TryCurrentSeed(out int ps, out bool pt) && ps == textSeed && pt && panel.SeedHintText.Contains(textSeed.ToString(CultureInfo.InvariantCulture));
                panel.SetSeedText("   ");
                bool emptyBlocked = !panel.StartButton.enabledSelf && !panel.CopyButton.enabledSelf && panel.SeedHintText == GameText.Get("ui.newgame.seed_empty")
                                    && !InvokeClickable(panel.StartButton) && started == 0;
                Expect(randomChanged && textOk && emptyBlocked,
                    $"种子：“随机种子”换一颗新的（不受测试覆盖值影响）；输入文字“归还之地”提示换算结果 {textSeed}；清空后“开始”与“复制短码”不可用并说明原因（点了也不开始）");

                panel.SetSeedText("20260929");
                InvokeClickable(panel.LevelButton(0, 0)); // 资源 低
                InvokeClickable(panel.LevelButton(1, 2)); // 据点 高
                InvokeClickable(panel.LevelButton(2, 2)); // 污染 高
                InvokeClickable(panel.LevelButton(3, 0)); // 领地 近
                bool levels = panel.CurrentSettings().Id == ExtremeCode && panel.LevelButton(0, 0).text.StartsWith("▸", StringComparison.Ordinal)
                              && !panel.LevelButton(0, 1).text.StartsWith("▸", StringComparison.Ordinal)
                              && WorldSettings.TryDecodeShareCode(panel.ShareFieldText, out int s2, out WorldSettings set2, out _) == WorldSettings.ShareError.None
                              && s2 == 20260929 && set2.Id == ExtremeCode;
                GUIUtility.systemCopyBuffer = string.Empty;
                InvokeClickable(panel.CopyButton);
                bool copied = GUIUtility.systemCopyBuffer == panel.ShareFieldText && panel.FeedbackText.Contains(panel.ShareFieldText);
                Expect(levels && copied, $"分项按钮：点选后当前档换成“▸”、短码实时更新为 {panel.ShareFieldText}（种子 20260929 + {ExtremeCode}）；“复制短码”放进剪贴板并提示");

                string code = panel.ShareFieldText;
                panel.SetShareText("BGR-12");
                bool badFormat = !panel.ImportCode() && panel.FeedbackText == GameText.Get("ui.newgame.code_bad_format") && panel.SeedFieldText == "20260929";
                char[] cc = code.ToCharArray();
                cc[5] = cc[5] == 'A' ? 'B' : 'A';
                panel.SetShareText(new string(cc));
                bool badSum = !panel.ImportCode() && panel.FeedbackText == GameText.Get("ui.newgame.code_bad_checksum");
                panel.SetShareText(ForgeCode(5, 60, 0));
                bool newer = !panel.ImportCode() && panel.FeedbackText.Contains("v60");
                Expect(badFormat && badSum && newer, "导入负向：格式不对 / 抄错一位 / 来自更新版本的游戏，各有说明，原输入（种子与设置）不变");

                string other = WorldSettings.EncodeShareCode(-4242, WorldSettings.Resolve(2, RelaxedCode));
                panel.SetShareText(other);
                bool imported = panel.ImportCode() && panel.SeedFieldText == "-4242" && panel.CurrentSettings().Id == RelaxedCode && panel.Levels[4] == 1
                                && panel.LevelButton(4, 1).text.StartsWith("▸", StringComparison.Ordinal) && panel.FeedbackText.Contains("-4242");
                string v1Code = WorldSettings.EncodeShareCode(31, WorldSettings.Resolve(1, WorldGenContent.DefaultPresetId));
                panel.SetShareText(v1Code);
                bool importedV1 = panel.ImportCode() && panel.Version == 1 && panel.AxisRowCount == 0 && panel.GeneratorText.Contains("v1");
                InvokeClickable(panel.StartButton);
                bool startV1 = started == 1 && gotSeed == 31 && gotSettings != null && gotSettings.Version == 1 && !NewGamePanelUIToolkit.IsOpen && !InputRouter.ModalUiOpen;
                Expect(imported && importedV1 && startV1,
                    "导入：别人的短码（种子 -4242 + 宽松起始区）一键换好种子与设置；生成器 v1 的短码切到 v1（分项按钮收起、写明与短码一致）；“开始”把（种子, 设置）交回主菜单并关面板");

                NewGamePanelUIToolkit.Open((sd, st) => started++, () => backed++);
                bool reset = panel.Version == 2 && panel.AxisRowCount == 5 && panel.CurrentSettings().Id == WorldGenContent.DefaultPresetId;
                UiEscapeStack.CloseTop();
                Expect(reset && backed == 1 && started == 1 && !NewGamePanelUIToolkit.IsOpen,
                    "再次打开回到当前版本、全部标准档；Esc（Esc 栈）= 返回：交回主菜单、不开始、不写任何存档");
            }
            finally
            {
                NewGamePanelUIToolkit.Close();
                CampaignRandomService.SeedOverrideForTests = null;
                Object.DestroyImmediate(go);
            }
        }

        // ── K. 战略地图与小地图（真 UXML）────────────────────────────────────────────

        private static void CheckStrategicMapAndMinimap()
        {
            var go = new GameObject("__fghome_map") { hideFlags = HideFlags.HideAndDontSave };
            var mgo = new GameObject("__fghome_minimap") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                CampaignState s = NewCampaign(42);
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                MinimapHudUIToolkit.InWorldOverrideForTests = true;
                VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                StrategicMapUIToolkit map = go.AddComponent<StrategicMapUIToolkit>();
                map.BindView(root);
                StrategicMapUIToolkit.Open();
                map.SetViewForTests(0, 0, 1400, 900, 700);
                map.Tick(force: true);
                WorldModelNames(map.Model, out HashSet<string> circles, out HashSet<WorldMapItemKind> kinds);
                WorldPlan plan = WorldGenService.PlanFor(s);
                bool actFilter = plan.Territories.Where(t => t.IsFaction && t.Act == 1).All(t => map.Model.Items.Any(i => i.Id == "territory:" + t.Id))
                                 && plan.Territories.Where(t => t.Act >= 2 || !t.IsFaction).All(t => !map.Model.Items.Any(i => i.Id == "territory:" + t.Id))
                                 && map.Model.Circles.Count(c => c.Layer == WorldMapLayer.Territory) == plan.Territories.Count(t => t.IsFaction && t.Act == 1);
                bool home = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home && i.X == 0 && i.Y == 0);
                bool noFogLeak = !map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Outpost || i.Kind == WorldMapItemKind.Relic);
                Expect(StrategicMapUIToolkit.IsOpen && map.PanelVisible && actFilter && home && noFogLeak && map.VisibleIconCount > 0,
                    $"战略地图打开：归还核心图标；第一幕的两个阵营领地（圈 + 名字 + 控制度“待侦察”）显示，第二幕领地与白潮滩头预留区不显示（揭示前没有任何提示）；" +
                    $"迷雾外的侦察巢 / 遗迹点不显示（图标 {map.VisibleIconCount} 个）");

                // 迷雾外 → 探索后出现。
                WorldFeature nest = WorldFeatures.HomeZoneNests(WorldGenService.ContextFor(s)).First();
                HomeGridService.RevealArea(s, new Vector2(nest.X, nest.Y), 12f);
                var relics = new List<WorldFeature>();
                WorldGenQuery.FeaturesIn(s, -300, -300, 300, 300, relics);
                WorldFeature relic = relics.FirstOrDefault(f => f.Kind == WorldFeatureKind.Relic);
                if (relic != null)
                {
                    HomeGridService.RevealArea(s, new Vector2(relic.X, relic.Y), 12f);
                }
                StartGuaranteeItem water = plan.StartReport.Find("water");
                HomeGridService.RevealArea(s, new Vector2(0, 0), 60f);
                map.SetViewForTests(0, 0, 320, 900, 700);
                map.Tick(force: true);
                bool nestShown = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Outpost && i.Id == nest.Id && i.Label.Contains(nest.Tier.ToString(CultureInfo.InvariantCulture)));
                bool relicShown = relic == null || Math.Abs(relic.X) > 320 || map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Relic && i.Id == relic.Id);
                bool resShown = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Resource && i.Id == "res:water" && i.Label.Contains(GameText.Get("world.guarantee.water")));
                Expect(nestShown && relicShown && resShown,
                    $"探索到之后才显示：侦察巢（带等级）、遗迹点、起始区资源点（“起始区水源”在 ({water?.SiteX},{water?.SiteY})）");

                map.ToggleFilter(WorldMapLayer.Outposts);
                map.Tick(force: true);
                bool hidden = !map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Outpost) && map.FilterButton(WorldMapLayer.Outposts).ClassListContains("wg-filter-off")
                              && !map.FilterButton(WorldMapLayer.Outposts).text.StartsWith("▸", StringComparison.Ordinal);
                InvokeClickable(map.FilterButton(WorldMapLayer.Outposts));
                map.Tick(force: true);
                bool back = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Outpost) && WorldMapFilters.IsOn(WorldMapLayer.Outposts);
                Expect(hidden && back, "筛选：点“据点”关掉这一层（按钮去掉“▸”、变淡），再点恢复；战略地图与小地图共用同一份筛选");

                // 己方建筑群与前哨（FGR-GEN-080）：核心那一群由核心图标代表；离核心不远的一群 = “建筑群（N 座）”；超过 map.own_outpost_distance = “前哨站”；已摧毁的不算。
                BuildingRecord[] ownBefore = s.BuildingRecords;
                BuildingRecord Own(string id, int x, int y, BuildingConstructionState st = BuildingConstructionState.Operational) => new BuildingRecord
                {
                    BuildingId = id, BuildingTypeId = "__map_cluster_probe", RegionId = HomeValleyLayout.RegionId, GridX = x, GridY = y,
                    Position = new Vector2(x, y), ConstructionState = st,
                };
                s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Concat(new[]
                {
                    Own("probe_a1", 120, 40), Own("probe_a2", 124, 44), Own("probe_a3", 130, 40),
                    Own("probe_b1", 420, -380), Own("probe_b2", 426, -376),
                    Own("probe_dead", 700, 700, BuildingConstructionState.Destroyed),
                }).ToArray();
                IReadOnlyList<WorldMapOwnClusters.Cluster> clusters = WorldMapOwnClusters.For(s);
                int rebuilds = WorldMapOwnClusters.RebuildCount;
                WorldMapOwnClusters.For(s);
                bool cached = WorldMapOwnClusters.RebuildCount == rebuilds;
                map.SetViewForTests(0, 0, 1400, 900, 700);
                map.Tick(force: true);
                List<WorldMapItem> own = map.Model.Items.Where(i => i.Kind == WorldMapItemKind.OwnCluster).ToList();
                bool near = own.Any(i => i.Label == GameText.Format("ui.map.own_cluster", 3) && Math.Abs(i.X - 124.67) < 1 && Math.Abs(i.Y - 41.33) < 1);
                bool far = own.Any(i => i.Label == GameText.Format("ui.map.own_outpost", 2) && Math.Abs(i.X - 423) < 1 && Math.Abs(i.Y + 378) < 1);
                bool noDead = !own.Any(i => Math.Abs(i.X - 700) < 20 && Math.Abs(i.Y - 700) < 20);
                bool coreOnce = clusters.Count(c => c.ContainsCore) == 1 && !own.Any(i => clusters.Any(c => c.ContainsCore && c.Id == i.Id))
                                && map.Model.Items.Count(i => i.Kind == WorldMapItemKind.Home) == 1;
                bool iconClass = Enumerable.Range(0, map.VisibleIconCount).Any(i => map.IconAt(i).ClassListContains("wg-icon-own"));
                map.ToggleFilter(WorldMapLayer.Own);
                map.Tick(force: true);
                bool ownFiltered = !map.Model.Items.Any(i => i.Kind == WorldMapItemKind.OwnCluster || i.Kind == WorldMapItemKind.Home);
                map.ToggleFilter(WorldMapLayer.Own);
                Expect(near && far && noDead && coreOnce && cached && iconClass && ownFiltered,
                    $"己方建筑群与前哨：{own.Count} 个建筑群图标（近处“{GameText.Format("ui.map.own_cluster", 3)}”{near}，300 格外“{GameText.Format("ui.map.own_outpost", 2)}”{far}），" +
                    $"已摧毁的不算（{noDead}），核心那一群只由核心图标代表（{coreOnce}），聚合结果缓存、建筑没变不重算（{cached}），“己方”筛选一起隐藏（{ownFiltered}）");

                // 行进中的突袭：迷雾里的不显示（图标与路线都不泄露），走进已探索区域后才出现。
                RaidState raidsBefore = s.Raids;
                var hiddenRaid = new TransitGroupRecord { GroupId = "probe_raid_fog", PosX = 900, PosY = 900, TargetX = 0, TargetY = 0, UnitCount = 3 };
                var shownRaid = new TransitGroupRecord { GroupId = "probe_raid_seen", PosX = 20, PosY = 20, TargetX = 0, TargetY = 0, UnitCount = 3 };
                s.Raids = new RaidState { InTransit = new[] { hiddenRaid, shownRaid } };
                map.Tick(force: true);
                bool raidFog = !map.Model.Items.Any(i => i.Id == hiddenRaid.GroupId) && map.Model.Items.Any(i => i.Id == shownRaid.GroupId)
                               && map.Model.Lines.Count == 1 && Math.Abs(map.Model.Lines[0].X0 - 20) < 0.01;
                s.Raids = raidsBefore;
                s.BuildingRecords = ownBefore;
                map.Tick(force: true);
                Expect(raidFog, "行进中的突袭：迷雾里的（900, 900）图标与路线都不显示，已探索区域里的（20, 20）显示图标与路线（与据点 / 遗迹同一条迷雾规则）");

                // 玩家标记：右键加、点选、改备注、删；上限。
                Vector2 at = map.View.ToCanvas(40, -25);
                MapMarkerRecord mk = map.AddMarkerAt(at);
                map.Tick(force: true);
                bool editorShown = mk != null && map.SelectedMarker == mk.MarkerId && map.MarkerEditorVisible;
                // 编辑模式下克隆出的树不在面板里、输入框的值变化事件不派发：直接走备注框回调调用的同一入口（冒烟在真实 Play 里改输入框）。
                map.SetSelectedMarkerNote("矿很多");
                map.Tick(force: true);
                bool noted = editorShown && WorldMapMarkers.Find(s, mk.MarkerId)?.Note == "矿很多"
                             && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Marker && i.Label.Contains("矿很多"));
                // 多词备注（B16 英文界面）：逐键输入——输入框显示“iron ”（末尾空格），刷新后不能被改回“iron”；接着打“ore”= “iron ore”，不是“ironore”。
                map.MarkerNoteField.value = "iron ";
                map.SetSelectedMarkerNote(map.MarkerNoteField.value);
                map.Tick(force: true);
                bool keptSpace = map.MarkerNoteField.value == "iron ";
                map.MarkerNoteField.value = map.MarkerNoteField.value + "ore";
                map.SetSelectedMarkerNote(map.MarkerNoteField.value);
                map.Tick(force: true);
                bool multiWord = keptSpace && WorldMapMarkers.Find(s, mk.MarkerId)?.Note == "iron ore" && map.MarkerNoteField.value == "iron ore"
                                 && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Marker && i.Label.Contains("iron ore"));
                Expect(multiWord, $"备注能打空格：输入“iron ”后刷新，输入框仍是“iron ”（{keptSpace}）；接着输入“ore”，存档里是“iron ore”（实际“{WorldMapMarkers.Find(s, mk.MarkerId)?.Note}”）");
                map.ClickAt(new Vector2(5, 5)); // 点空白处（离标记远）→ 飞过去（编辑模式没有镜头，飞不了）并关地图
                bool closedOnClick = !StrategicMapUIToolkit.IsOpen;
                StrategicMapUIToolkit.Open();
                map.SetViewForTests(0, 0, 320, 900, 700);
                map.Tick(force: true);
                map.ClickAt(map.View.ToCanvas(mk.CellX, mk.CellY));
                bool selected = map.SelectedMarker == mk.MarkerId && StrategicMapUIToolkit.IsOpen;
                InvokeClickable(map.MarkerDeleteButton);
                bool deleted = WorldMapMarkers.Find(s, mk.MarkerId) == null && map.SelectedMarker == null;
                for (int i = WorldMapMarkers.All(s).Count; i < WorldMapMarkers.Limit; i++)
                {
                    WorldMapMarkers.Add(s, WorldGenContent.EarthSurfaceId, i, i);
                }
                MapMarkerRecord over = map.AddMarkerAt(at);
                bool limit = over == null && map.FeedbackText.Contains(WorldMapMarkers.Limit.ToString(CultureInfo.InvariantCulture)) && WorldMapMarkers.All(s).Count == WorldMapMarkers.Limit;
                s.World.MapMarkers = Array.Empty<MapMarkerRecord>();
                Expect(noted && closedOnClick && selected && deleted && limit,
                    $"玩家标记：右键加标记并选中、编辑框出现（{editorShown}）、改备注立即写进存档域、地图上带备注（{noted}）；左键点空白处 = 飞过去并关地图（{closedOnClick}）；" +
                    $"点标记 = 选中编辑（{selected}）；删除（{deleted}）；到上限（{WorldMapMarkers.Limit}）时如实拒绝（{limit}）");

                // 底图：Burst 工作线程画，迷雾外是暗色棋盘纹。
                map.SetViewForTests(0, 0, 600, 600, 600);
                map.MapTexture.CompleteNow(s, map.View);
                Texture2D tex = map.MapTexture.Texture;
                bool painted = map.MapTexture.Painted && tex != null;
                Color32 center = painted ? tex.GetPixel(tex.width / 2, tex.height / 2) : default;
                Color32 corner = painted ? tex.GetPixel(2, 2) : default;
                bool fog = painted && corner.r < 40 && corner.g < 45 && corner.b < 50 && (center.r > 45 || center.g > 45 || center.b > 45);
                Expect(painted && fog, $"底图：已探索区域画地形颜色（中心 {center}），未探索是迷雾色（角落 {corner}，不泄露地形）；贴图任务在工作线程上跑（{map.MapTexture.LastJobMs:F1} ms）");

                // 连续缩放：拉远、拉近到底回到镜头。
                double before = map.View.HalfWidth;
                map.ZoomAt(new Vector2(300, 300), +1);
                bool zoomOut = map.View.HalfWidth > before * 1.2;
                int steps = 0;
                while (StrategicMapUIToolkit.IsOpen && steps < 60)
                {
                    map.ZoomAt(new Vector2(300, 300), -1);
                    steps++;
                }
                Expect(zoomOut && !StrategicMapUIToolkit.IsOpen && steps > 1,
                    $"连续缩放：滚轮拉远以光标为中心放大比例；一路拉近 {steps} 次到最小比例以下时地图关掉、回到镜头（镜头仍停在最远缩放，衔接连续）");

                // 地图键（真实 Update 路径）：旧版本原型地形的存档没有战略地图——地图不读这个键，留给任务日志（回退打开任务日志兼战役地图），
                // 不再静默无反应（B06）；生成器世界照常打开战略地图。
                MethodInfo mapUpdate = typeof(StrategicMapUIToolkit).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                var keys = new KeyReader();
                KeyCode mapKey = GameSettings.KeyBindings.GetKey(GameActionId.OpenMap);
                CampaignState legacy = CampaignState.CreateNew("fghome-legacy", "Standard", 7);
                legacy.World.GeneratorVersion = 0;
                bool legacyNoMap = !StrategicMapUIToolkit.HasWorldMap(legacy) && StrategicMapUIToolkit.HasWorldMap(s);
                bool legacyKeyLeft;
                bool v2Opens;
                try
                {
                    InputRouter.DebugSetReader(keys);
                    InputRouter.DebugClearConsumedKeys();
                    CampaignSession.Set(0, legacy);
                    keys.Down = mapKey;
                    mapUpdate.Invoke(map, null);
                    legacyKeyLeft = !StrategicMapUIToolkit.IsOpen && InputRouter.ConsumeGlobalAction(GameActionId.OpenMap);
                    InputRouter.DebugClearConsumedKeys();
                    CampaignSession.Set(0, s);
                    mapUpdate.Invoke(map, null);
                    v2Opens = StrategicMapUIToolkit.IsOpen;
                }
                finally
                {
                    keys.Down = KeyCode.None;
                    InputRouter.DebugClearConsumedKeys();
                    InputRouter.DebugSetReader(null);
                    CampaignSession.Set(0, s);
                    StrategicMapUIToolkit.Close();
                }
                Expect(legacyNoMap && legacyKeyLeft && v2Opens,
                    $"地图键：旧版本原型地形存档（生成器 v0）没有战略地图，地图不吞这个键、留给任务日志回退打开（{legacyKeyLeft}）；生成器世界按地图键打开战略地图（{v2Opens}）");

                // 小地图。
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "Minimap.uxml").CloneTree();
                MinimapHudUIToolkit mini = mgo.AddComponent<MinimapHudUIToolkit>();
                mini.BindView(mroot);
                mini.Tick(force: true);
                mini.MapTexture.CompleteNow(s, mini.View);
                mini.Tick(force: true);
                bool miniOk = mini.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home) && mini.View.HalfWidth == GridContent.Tuning("minimap.radius_cells")
                              && mini.MapTexture.Painted && mini.VisibleIconCount >= 1;
                Expect(miniOk, $"小地图：以镜头焦点为中心显示 {GridContent.Tuning("minimap.radius_cells")} 格范围、同样的图标与筛选、底图已画（{mini.VisibleIconCount} 个图标）");
            }
            finally
            {
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                MinimapHudUIToolkit.InWorldOverrideForTests = false;
                WorldMapFilters.ResetAll();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mgo);
                WorldGenKernel.ReleaseAll();
            }
        }

        private static void WorldModelNames(WorldMapModel model, out HashSet<string> circles, out HashSet<WorldMapItemKind> kinds)
        {
            circles = new HashSet<string>(model.Circles.Select(c => c.Label ?? string.Empty));
            kinds = new HashSet<WorldMapItemKind>(model.Items.Select(i => i.Kind));
        }

        // ── L. 地貌起伏（FG-GAP-021）──────────────────────────────────────────────

        private static void CheckRelief()
        {
            CampaignState s = NewCampaign(42);
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            byte water = GridContent.TerrainCode("water");
            // 找一个同时有 2×2 悬崖块与 2×2 水面的区块（家园区附近）。
            int bx = int.MinValue;
            int by = int.MinValue;
            for (int r = 1; r < 8 && bx == int.MinValue; r++)
            {
                for (int cy = -r; cy <= r && bx == int.MinValue; cy++)
                {
                    for (int cx = -r; cx <= r && bx == int.MinValue; cx++)
                    {
                        HomeGridMap.Chunk c = map.ChunkAt(new GridCell(cx * 32, cy * 32), out _);
                        if (HasBlock(c.Terrain, cliff) && HasBlock(c.Terrain, water))
                        {
                            bx = cx;
                            by = cy;
                        }
                    }
                }
            }
            if (bx == int.MinValue)
            {
                Fail("家园区附近找不到同时有悬崖块与水面块的区块（起伏测试没有样本）");
                return;
            }
            var parent = new GameObject("__fghome_relief") { hideFlags = HideFlags.HideAndDontSave };
            var overlay = new WorldTerrainOverlay(parent.transform, alpha: 0.4f, height: -0.05f, terrainView: true);
            try
            {
                if (map.TerrainSource is WorldTerrainSource src)
                {
                    overlay.SetReliefSeed(src.Params.SurfaceSeed);
                }
                var focus = new GridCell(bx * 32 + 16, by * 32 + 16);
                var sw = Stopwatch.StartNew();
                overlay.Update(s, focus, completeNow: true);
                overlay.Update(s, focus, completeNow: true);
                sw.Stop();
                Mesh mesh = overlay.ReliefMesh(bx, by);
                Mesh right = overlay.ReliefMesh(bx + 1, by);
                HomeGridMap.Chunk chunk = map.TryGetLoaded(bx, by);
                bool built = overlay.IsRelief && mesh != null && right != null && overlay.ReliefTileCount == overlay.TileCount && overlay.TileCount >= 9;
                if (!built)
                {
                    Fail($"起伏网格没有建好（{overlay.ReliefTileCount}/{overlay.TileCount}）");
                    return;
                }
                Vector3[] v = mesh.vertices;
                int s1 = 33;
                int corners = s1 * s1;
                int walkBad = 0;
                float maxCliff = 0f;
                float minWater = 0f;
                for (int j = 0; j < 32; j++)
                {
                    for (int i = 0; i < 32; i++)
                    {
                        byte t = chunk.Terrain[j * 32 + i];
                        float hc = v[corners + j * 32 + i].y;
                        if (t != cliff && t != water)
                        {
                            float h4 = Mathf.Abs(v[j * s1 + i].y) + Mathf.Abs(v[j * s1 + i + 1].y) + Mathf.Abs(v[(j + 1) * s1 + i].y) + Mathf.Abs(v[(j + 1) * s1 + i + 1].y);
                            if (h4 > 1e-5f || Mathf.Abs(hc) > 1e-5f)
                            {
                                walkBad++;
                            }
                        }
                        else if (t == cliff)
                        {
                            maxCliff = Mathf.Max(maxCliff, hc);
                        }
                        else
                        {
                            minWater = Mathf.Min(minWater, hc);
                        }
                    }
                }
                Vector3[] rv = right.vertices;
                int seamBad = 0;
                for (int j = 0; j <= 32; j++)
                {
                    if (Mathf.Abs(v[j * s1 + 32].y - rv[j * s1].y) > 1e-5f)
                    {
                        seamBad++;
                    }
                }
                int cx0 = -1;
                int cy0 = -1;
                for (int k = 0; k < 1024 && cx0 < 0; k++)
                {
                    if (chunk.Terrain[k] == cliff)
                    {
                        cx0 = k % 32;
                        cy0 = k / 32;
                    }
                }
                float q = WorldGenQuery.ReliefHeight(s, bx * 32 + cx0, by * 32 + cy0);
                float qWalk = WorldGenQuery.ReliefHeight(s, 0, 0);
                Expect(walkBad == 0 && maxCliff > 2f && minWater < -0.3f && seamBad == 0 && Mathf.Abs(q - v[corners + cy0 * 32 + cx0].y) < 1e-4f && qWalk == 0f,
                    $"FG-GAP-021 地貌起伏：区块 ({bx},{by}) 的起伏网格（Burst 工作线程）——可走的格四角与格心都在 0 高度（不在 0 的 {walkBad} 格），悬崖隆起到 {maxCliff:F1} 米、水面下陷到 {minWater:F1} 米；" +
                    $"与右邻区块共用的 33 个格角高度逐个相同（裂缝 {seamBad}）；查询 WorldGenQuery.ReliefHeight 与画面一致（悬崖格 {q:F2} 米、核心 0 米）；{overlay.TileCount} 块用时 {sw.ElapsedMilliseconds} ms（含同步完成）");

                // 修改地形（悬崖 → 可走）后这一块重建，被改的格回到 0 高度。
                var cell = new GridCell(bx * 32 + cx0, by * 32 + cy0);
                map.SetTerrain(cell, GridContent.TerrainCode("buildable"));
                overlay.Update(s, focus, completeNow: true);
                overlay.Update(s, focus, completeNow: true);
                Mesh after = overlay.ReliefMesh(bx, by);
                float hAfter = after != null ? after.vertices[corners + cy0 * 32 + cx0].y : 99f;
                Expect(Mathf.Abs(hAfter) < 1e-5f, $"地形被修改（悬崖拆平）后这一块的起伏网格重建，那一格回到 0 高度（{hAfter:F2}）");
            }
            finally
            {
                overlay.Dispose();
                Object.DestroyImmediate(parent);
                WorldGenKernel.ReleaseAll();
            }
        }

        private static bool HasBlock(byte[] t, byte code)
        {
            for (int j = 1; j < 31; j++)
            {
                for (int i = 1; i < 31; i++)
                {
                    if (t[j * 32 + i] == code && t[j * 32 + i + 1] == code && t[(j + 1) * 32 + i] == code && t[(j + 1) * 32 + i + 1] == code
                        && t[(j - 1) * 32 + i] == code && t[j * 32 + i - 1] == code)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ── M1. 性能与时钟无关 ───────────────────────────────────────────────────

        private static void CheckPerformanceAndClock()
        {
            WorldGenContext ctx = Ctx(42);
            var t = new byte[1024];
            var p = new byte[1024];
            var sw = new Stopwatch();
            double maxMs = 0;
            double total = 0;
            int n = 0;
            for (int cy = -6; cy <= 6; cy++)
            {
                for (int cx = -6; cx <= 6; cx++)
                {
                    sw.Restart();
                    ctx.Source.FillChunk(cx, cy, 32, t, p);
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (n > 0)
                    {
                        maxMs = Math.Max(maxMs, ms);
                        total += ms;
                    }
                    n++;
                }
            }
            sw.Restart();
            WorldGenJob job = ctx.Source.Schedule(3, 3, 0);
            WorldGenKernel.Kick();
            job.Complete();
            job.Release();
            sw.Stop();
            double workerMs = sw.Elapsed.TotalMilliseconds;
            var build = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                Ctx(SeedSet[i]);
            }
            build.Stop();

            // 修复轮：保证点落位的试探与“候选兜底”（逐格枚举 (2×半径+1)² 个候选）在 AOT Burst 里做——
            // 逐个种子计时组装世界，含走到候选兜底的种子，最慢的一个也要在预算内（Editor 参照；真机数字 → FG15-SYS-02）。
            double worstBuild = 0;
            int worstSeed = 0;
            int fallbackWorlds = 0;
            var one = new Stopwatch();
            for (int i = 0; i < 20; i++)
            {
                one.Restart();
                WorldGenContext c = Ctx(SeedSet[i]);
                one.Stop();
                if (c.Plan.StartReport.Items.Any(it => it.Stamped && it.StampAttempt == -1))
                {
                    fallbackWorlds++;
                }
                if (one.Elapsed.TotalMilliseconds > worstBuild)
                {
                    worstBuild = one.Elapsed.TotalMilliseconds;
                    worstSeed = SeedSet[i];
                }
            }
            ExpectPerf(fallbackWorlds >= 1,
                $"组装世界含保证点候选兜底（Burst JobPlaceStamp）：20 个种子里 {fallbackWorlds} 个走到候选兜底，最慢一个（种子 {worstSeed}）{worstBuild:F1} ms（Editor 预算 120 ms；只在开局 / 读档时一次）",
                PerfGate.Lt(worstBuild, 120.0, "最慢组装世界 ms"));
            ExpectPerf(true,
                $"FG17 第 7 节：生成器 v2 单区块生成（Burst，与工作线程同一份编译代码；家园区 169 块，含领地 / 河流 / 矿带 / 保证点形状）平均 {total / Math.Max(1, n - 1):F3} ms、最大 {maxMs:F3} ms（目标 ≤ 5 ms）；" +
                $"工作线程调度到完成 {workerMs:F2} ms；开局 / 读档组装世界（规划层 + 起始区两遍校验 + 地形来源）平均 {build.Elapsed.TotalMilliseconds / 10:F1} ms / 世界（只在开局 / 读档时一次）",
                PerfGate.Lt(maxMs, 5.0, "单区块最大 ms"), PerfGate.Lt(total / Math.Max(1, n - 1), 2.0, "单区块平均 ms"));

            // 生成与游戏时钟无关：暂停、3x、0.5x 下组装的世界完全相同（生成不读时钟与玩法随机流）。
            string fp = ctx.Plan.FullFingerprint();
            ulong h = HashOf(ctx.Source, 1, 1);
            GameClock.SetPaused(true);
            string fpPaused = Ctx(42).Plan.FullFingerprint();
            GameClock.SetPaused(false);
            StrategyClock.SetSpeed(3f);
            ulong h3 = HashOf(Ctx(42).Source, 1, 1);
            StrategyClock.SetSpeed(0.5f);
            ulong hHalf = HashOf(Ctx(42).Source, 1, 1);
            StrategyClock.Reset();
            Expect(fpPaused == fp && h3 == h && hHalf == h,
                "暂停 / 3x / 0.5x：世界生成、起始区保证与点位分布与游戏时钟无关，结果逐字节相同（地图飞跃过渡按真实时间，暂停中也能看地图、飞镜头）");
        }

        // ── M2. 生成器 v2 冻结基准 ──────────────────────────────────────────────────

        /// <summary>v2 全部生成输入的摘要基准（<see cref="WorldGenInputs.Manifest"/>(2) 中以 v2. 开头的部分）。**已有行永远不许改**。</summary>
        private static readonly (string part, ulong digest)[] InputBaselineV2 =
        {
            // 2026-09-29 FG3-GEN-01 发布 v2 时记录。起始区保护两部分与 v1 的基准逐位相同：快照 c1 = 原先按开局布局现算的矩形。
            ("v2.version", 0x87BD38628E020F6CUL),
            ("v2.territories[t1]", 0x2C40043172F1C2EEUL),
            ("v2.presets[none]", 0xCBF29CE484222325UL),
            ("v2.start_protection.standard", 0xEBD23BD380D71E1EUL),
            ("v2.start_protection.relaxed", 0xF02E541F754A6148UL),
            // 修复轮（v2 发布前）：原写死在代码里的河流离心夹角 75°、沿河加宽 3 格、遗迹点离核心 32 格移入版本行，本部分基准随之更新（值不变，生成结果不变）。
            ("v2.version.fg3", 0x9B4A335DBDB2DA32UL),
            ("v2.guarantees[g2]", 0x0971E13C86E0A632UL),
            ("v2.settings[a2]", 0xBBFA81E4238B7089UL),
        };

        /// <summary>v2 规划层完整指纹（领地 + 河流 + 矿带 + 起始区保证点，核心 (0,0)、default）的 FNV-1a 64 摘要。</summary>
        private static readonly (int seed, ulong digest)[] PlanBaselineV2 =
        {
            // 2026-09-29 FG3-GEN-01 发布 v2 时记录（种子 1 / 42 的废墟群、种子 42 的水源、种子 -7 的废墟群是局部重生成的保证点）。
            (1, 0xD878F2F14AAB8C95UL),
            (42, 0x687D2B774F6D0545UL),
            (-7, 0x24EF956ED7514E45UL),
        };

        /// <summary>
        /// v2 点位分布指纹（遗迹点 + 家园区侦察巢：ID、种类、位置、等级、是否挪位；核心 (0,0)、default、±600 格）的 FNV-1a 64 摘要。
        /// 点位不存档（读档按种子重算），改分布代码 / 参数而不升版本会悄悄挪动已有 v2 存档里的遗迹点——这里守住。**已有行永远不许改**。
        /// </summary>
        private static readonly (int seed, ulong digest)[] FeatureBaselineV2 =
        {
            // 2026-09-29 FG3-GEN-01 修复轮记录（v2 发布前；三个种子 ±600 格共 173 个点位）。
            (1, 0x8E4E3DB50EF33F18UL),
            (42, 0x8A23CFCA231077A3UL),
            (-7, 0xB964BA316DBD9362UL),
        };

        private static string FeatureFingerprint(int seed)
        {
            var list = new List<WorldFeature>();
            WorldFeatures.Query(Ctx(seed), -600, -600, 600, 600, list);
            var sb = new System.Text.StringBuilder();
            foreach (WorldFeature f in list)
            {
                sb.Append(f.Id).Append(',').Append((int)f.Kind).Append(',').Append(f.X).Append(',').Append(f.Y).Append(',').Append(f.Tier)
                    .Append(',').Append(f.Shifted ? 1 : 0).Append(';');
            }
            return sb.ToString();
        }

        private static void CheckV2Frozen()
        {
            var wrong = new List<string>();
            List<KeyValuePair<string, string>> parts = WorldGenInputs.Manifest(2).Where(kv => kv.Key.StartsWith("v2.", StringComparison.Ordinal)).ToList();
            foreach (KeyValuePair<string, string> kv in parts)
            {
                ulong d = WorldGenInputs.Digest(kv.Value);
                int i = Array.FindIndex(InputBaselineV2, b => b.part == kv.Key);
                if (i < 0)
                {
                    wrong.Add($"缺基准 (\"{kv.Key}\", 0x{d:X16}UL)");
                }
                else if (InputBaselineV2[i].digest != d)
                {
                    wrong.Add($"{kv.Key} 基准 0x{InputBaselineV2[i].digest:X16} 实际 0x{d:X16}〔{kv.Value}〕");
                }
            }
            Expect(wrong.Count == 0 && parts.Count >= 8 && parts.Any(p => p.Key == "v2.guarantees[g2]") && parts.Any(p => p.Key == "v2.settings[a2]"),
                $"FGR-GEN-061 生成输入清单 v2：{parts.Count} 个部分（版本行、新增列、领地 t1、起始区快照、保证集合 g2、分项设置 a2）与冻结基准逐个一致" +
                (wrong.Count == 0 ? string.Empty : "——" + string.Join("；", wrong)));

            var planWrong = new List<string>();
            foreach (int seed in new[] { 1, 42, -7 })
            {
                string fp = Ctx(seed).Plan.FullFingerprint();
                ulong d = WorldGenInputs.Digest(fp);
                int i = Array.FindIndex(PlanBaselineV2, b => b.seed == seed);
                if (i < 0)
                {
                    planWrong.Add($"缺基准 ({seed}, 0x{d:X16}UL)");
                }
                else if (PlanBaselineV2[i].digest != d)
                {
                    planWrong.Add($"种子 {seed}：基准 0x{PlanBaselineV2[i].digest:X16} 实际 0x{d:X16}〔{fp}〕");
                }
            }
            Expect(planWrong.Count == 0, "v2 规划层完整指纹（种子 1 / 42 / -7）与基准逐字一致" + (planWrong.Count == 0 ? string.Empty : "——" + string.Join("；", planWrong)));

            var featureWrong = new List<string>();
            int featureCount = 0;
            foreach (int seed in new[] { 1, 42, -7 })
            {
                string fp = FeatureFingerprint(seed);
                featureCount += fp.Split(';').Length - 1;
                ulong d = WorldGenInputs.Digest(fp);
                int i = Array.FindIndex(FeatureBaselineV2, b => b.seed == seed);
                if (i < 0)
                {
                    featureWrong.Add($"缺基准 ({seed}, 0x{d:X16}UL)");
                }
                else if (FeatureBaselineV2[i].digest != d)
                {
                    featureWrong.Add($"种子 {seed}：基准 0x{FeatureBaselineV2[i].digest:X16} 实际 0x{d:X16}〔{fp.Substring(0, Math.Min(400, fp.Length))}…〕");
                }
            }
            Expect(featureWrong.Count == 0 && featureCount >= 30,
                $"v2 点位分布指纹（遗迹点 + 侦察巢，种子 1 / 42 / -7，±600 格共 {featureCount} 个点位）与基准逐字一致——点位不存档、读档按种子重算，不升版本改分布就会挪动已有存档里的遗迹点" +
                (featureWrong.Count == 0 ? string.Empty : "——" + string.Join("；", featureWrong)));

            // 变异：不升版本直接改 g2（水源至少 6 → 7 格）→ 清单基准失败；改开局布局快照 c1 → v1 与 v2 的起始区保护部分都失败。
            var rows = RealGuarantees().Select(r => r.item == "water" ? (r.item, r.terrain, r.radius, r.cells + 1, r.sq, r.stamp) : r).ToList();
            string before = WorldGenInputs.Manifest(2).First(kv => kv.Key == "v2.guarantees[g2]").Value;
            WorldGenContent.OverrideForTests(guarantees: new TbWorldStartGuarantee(GuaranteeBuf(rows)));
            string after = WorldGenInputs.Manifest(2).First(kv => kv.Key == "v2.guarantees[g2]").Value;
            WorldGenContent.ResetForTests();
            Expect(before != after, "负向：直接改已发布的保证集合 g2（水源至少 6 → 7 格）而不升版本 → v2 生成输入清单变化，冻结基准会失败（正确做法：新集合 + 新版本行）");
        }

        // ── M3. 布局探针 ───────────────────────────────────────────────────────────

        private static void CheckLayoutProbes()
        {
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach ((string uxml, string rootName, Action<VisualElement> prepare) in new (string, string, Action<VisualElement>)[]
                         {
                             ("NewGamePanel.uxml", "NewGameWindow", r =>
                             {
                                 var g = new GameObject("__probe_ng") { hideFlags = HideFlags.HideAndDontSave };
                                 NewGamePanelUIToolkit p = g.AddComponent<NewGamePanelUIToolkit>();
                                 p.BindView(r.panel.visualTree);
                                 NewGamePanelUIToolkit.Open((_, __) => { }, () => { });
                                 NewGamePanelUIToolkit.Close();
                                 Object.DestroyImmediate(g);
                                 r.panel.visualTree.Q<VisualElement>("NewGameRoot")?.RemoveFromClassList("uk-hidden");
                             }),
                             ("StrategicMap.uxml", "MapWindow", r => r.panel.visualTree.Q<VisualElement>("MarkerEditor")?.RemoveFromClassList("uk-hidden")),
                             ("Minimap.uxml", "MinimapRoot", null),
                         })
                {
                    string result = UiToolkitLayoutProbe.Probe(UiKitFolder + uxml, rootName, stressFill: true, prepare: prepare);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 {uxml}#{rootName} [{lang}]：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

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

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

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
