using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-02 传送带内核原型的自动验收（FG14 FGR-ARC-004；FG03 FGR-LOG-020/021/025/081/090、第 5 节负向矩阵、FGT-LOG-005/006/010/011/012 原型；
    /// FG15 FGR-SYS-041/042）。全部起真实系统：真实 Burst 内核（BeltKernel）、真实世界模拟（WorldSimulation 固定步、统一时钟、倍速 / 暂停）、
    /// 真实格网校验（HomeGridService.ValidateBeltCell）、真实存读档（CampaignAutoSaveService 写文件 → CampaignRestoreOrchestrator 读回）、
    /// 真实表现层路径（WorldPlanetView → BeltNetworkService.Render → BeltRenderer）与真实滚轮缩放输入。行为坏了会失败：
    /// A 数据：logistics.* 调参行 = 规格初值；文本键中英齐全；引导钩子登记。
    /// B 内核语义：三档吞吐 60/120/240 件每分钟；满载直线整体匀速（下游优先）；到头 / 下游满 / 输入端口满时停下且不丢物品、原因正确；
    ///   环形满载也一直转（物品顺序整体平移一格）、吞吐正确、不刷物品；侧向汇入严格交替（两路 / 三路）、轮到的一路没货时别路不空等；
    ///   迎面相对不相连；原地反转；拆除带物品按账移出；等级混排；每步不变量（账本平衡、间距、容量）。
    /// C 确定性：放置顺序打乱 / 重跑 / 中途序列化再接着跑，状态哈希逐步一致。
    /// D 物品守恒：闭合产线跑 10 个游戏日（240,000 步）账目每 10,000 步平衡。
    /// E 超大网络（15,000 格 / 30,000 件）：单步耗时（≤ 2 ms）、拓扑重建、渲染缓冲、序列化；经正式存档路径存读档后状态哈希一致、
    ///   接着跑与不存档的对照一致；坏块只丢一块并告知、账本平衡；不认识的格式不覆盖原数据；旧档（域版本 1）得到空网络。
    /// F 世界集成：20 Hz 节拍（60 Hz 世界每 3 步 1 步）；暂停零步；0.5x～3x 同一段游戏时间结果一致；观察 / 不观察 / 远景一致；
    ///   格网规则（迷雾 / 占用 / 已有带 / 建筑不能压在带上 / 核心通道可放）；种子无关（三个种子下按核心相对位置铺设都能运转）。
    /// G 渲染：实例位置 = 内核状态；近景逐物品、远景流动贴图（回差）；真实滚轮缩放切换远景；堵塞格带标记。
    /// H 热更层：每帧 / 每步开销与格数无关（托管分配为零、调用次数常数）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgBeltKernelSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const int CL = BeltConst.CellLength;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static FakeReader _reader;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 传送带内核")]
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
            Line("\n[传送带] 传送带内核原型（FG0-ARCH-02）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbelt-selfcheck-" + Guid.NewGuid().ToString("N"));
            _reader = new FakeReader();
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
                InputRouter.DebugSetReader(_reader);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"Burst 编译={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}（作业声明同步编译）；图形设备 {SystemInfo.graphicsDeviceType}。" +
                     "数字是 Editor 下的 Burst 作业 + Mono 托管调用；真机（IL2CPP，Burst AOT）内核部分相同或更快，托管部分另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckTierThroughput);
                Step(CheckSaturatedLineMovesTogether);
                Step(CheckEndOfBeltAndSinkFull);
                Step(CheckLoop);
                Step(CheckMergeFairness);
                Step(CheckRingMergeNoDeadlock);
                Step(CheckLaneSpacingAcrossCells);
                Step(CheckEdgeRules);
                Step(CheckDeterminism);
                Step(CheckConservationTenDays);
                Step(CheckRenderBuffers);
                Step(CheckScaleAndSnapshot);
                Step(CheckWorldCadencePauseSpeed);
                Step(CheckObservationIndependence);
                Step(CheckGridRulesAndHooks);
                Step(CheckSeedIndependence);
                Step(CheckFullSaveHugeNetwork);
                Step(CheckCorruptAndLegacySaves);
                Step(CheckRenderPathAndZoom);
                Step(CheckHotLayerConstantCost);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"传送带自检抛异常：{e}");
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
                BeltNetworkService.Unload();
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
                GameSettings.SetLanguage(originalLanguage);
                UiKitResetSafe();
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
            Line($"  · [传送带] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void UiKitResetSafe()
        {
            try
            {
                GameLogic.UI.Kit.UiEscapeStack.Clear();
            }
            catch
            {
                // 没有 UI 栈时忽略。
            }
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            var expect = new Dictionary<string, float>
            {
                ["logistics.step_hz"] = 20f,
                ["logistics.slots_per_cell"] = 4f,
                ["logistics.items_per_minute_t1"] = 60f,
                ["logistics.items_per_minute_t2"] = 120f,
                ["logistics.items_per_minute_t3"] = 240f,
                ["logistics.stats_bucket_steps"] = 300f,
                ["logistics.render.flow_ortho_enter"] = 38f,
                ["logistics.render.flow_ortho_exit"] = 34f,
                ["logistics.render.item_size"] = 0.22f,
                ["logistics.render.height"] = 0.04f,
            };
            var bad = new List<string>();
            foreach (KeyValuePair<string, float> kv in expect)
            {
                if (!GridContent.TryGetTuning(kv.Key, out float v) || Math.Abs(v - kv.Value) > 1e-4f)
                {
                    bad.Add(kv.Key);
                }
            }
            BeltConfig c = BeltNetworkService.ReadConfig();
            Expect(bad.Count == 0 && c.StepHz == 20 && c.SlotsPerCell == 4 && c.ItemsPerMinuteT1 == 60 && c.ItemsPerMinuteT2 == 120 && c.ItemsPerMinuteT3 == 240,
                $"logistics.* 调参在 fg.TbHomeTuning 且等于规格初值（FGR-ARC-004 20 Hz；FG03 第 10 章 4 件/格、60/120/240 件/分钟）；不符 {bad.Count} 项 {string.Join("、", bad)}");
            using (var k = new BeltKernel(c))
            {
                Expect(k.UnitsPerStep(0) == 600 && k.UnitsPerStep(1) == 1200 && k.UnitsPerStep(2) == 2400 && k.Spacing == 12000,
                    $"定点换算无舍入：每步 {k.UnitsPerStep(0)}/{k.UnitsPerStep(1)}/{k.UnitsPerStep(2)} 单位（一格 {CL}），物品间距 {k.Spacing}");
            }
            BeltConfig broken = c;
            broken.SlotsPerCell = 9;
            BeltConfig tooFast = c;
            tooFast.ItemsPerMinuteT3 = 100000;
            bool v1 = broken.IsValid(out string w1);
            bool v2 = tooFast.IsValid(out string w2);
            Expect(!v1 && !v2,
                $"非法调参被识别（服务回落到规格初值并报错，不会让内核跑出重叠）：{w1}；{w2}");

            string[] keys =
            {
                "logistics.tier.t1", "logistics.tier.t2", "logistics.tier.t3", "logistics.block.none", "logistics.block.end_of_belt",
                "logistics.block.downstream_full", "logistics.block.sink_full", "logistics.block.merge_wait", "logistics.hover.summary", "logistics.hover.loop",
                "logistics.hover.measuring",
                "logistics.reason.occupied", "logistics.reason.not_found", "logistics.reason.invalid_tier", "logistics.reason.invalid_direction",
                "logistics.reason.no_space", "logistics.reason.port_occupied", "logistics.reason.port_not_found", "logistics.reason.invalid_argument",
                "logistics.reason.out_of_range", "logistics.reason.not_running", "logistics.load.corrupt", "logistics.placeholder",
                "logistics.block.sink_unnamed", "logistics.reason.save_preserved", "logistics.load.unreadable",
                "logistics.load.reason.separator", "logistics.load.reason.unknown", "logistics.load.reason.snapshot_null",
                "logistics.load.reason.not_empty", "logistics.load.reason.format_version", "logistics.load.reason.chunk_empty",
                "logistics.load.reason.chunk_checksum", "logistics.load.reason.chunk_magic", "logistics.load.reason.chunk_header",
                "logistics.load.reason.chunk_truncated", "logistics.load.reason.chunk_cell", "logistics.load.reason.chunk_item",
                "logistics.load.reason.chunk_count", "logistics.load.reason.ports_corrupt",
            };
            var missing = new List<string>();
            foreach (string key in keys)
            {
                if (!GameText.TryGet(key, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                    || !GameText.TryGet(key, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    missing.Add(key);
                }
            }
            var codes = (BeltResult[])Enum.GetValues(typeof(BeltResult));
            var unmapped = codes.Where(r => r != BeltResult.Ok && !GameText.Has(BeltNetworkService.ReasonKey(r))).ToList();
            Expect(missing.Count == 0 && unmapped.Count == 0,
                $"{keys.Length} 个新文本键中英齐全（缺：{string.Join("、", missing)}）；内核每个拒绝码都映射到文本键（未映射 {unmapped.Count}）");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsFirstBelt) && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsFirstBlocked),
                "引导钩子登记：第一次放传送带、第一次出现堵塞（内容由 FG15-UX-04 统一做）");
        }

        // ── B 内核语义 ───────────────────────────────────────────────────────────

        private static BeltKernel NewKernel() => new BeltKernel(BeltNetworkService.ReadConfig());

        private static void AddLine(BeltKernel k, int x0, int y0, int len, BeltDir dir, int tier)
        {
            for (int i = 0; i < len; i++)
            {
                k.AddCell(x0 + BeltDirs.Dx((int)dir) * i, y0 + BeltDirs.Dy((int)dir) * i, dir, tier);
            }
        }

        /// <summary>矩形环（逆时针：下边向东、右边向北、上边向西、左边向南），周长 2(w+h)−4 格；返回按环顺序的格。</summary>
        private static List<int2> AddRing(BeltKernel k, int x0, int y0, int w, int h, int tier)
        {
            var cells = new List<int2>();
            for (int x = x0; x < x0 + w - 1; x++)
            {
                cells.Add(new int2(x, y0));
                k.AddCell(x, y0, BeltDir.East, tier);
            }
            for (int y = y0; y < y0 + h - 1; y++)
            {
                cells.Add(new int2(x0 + w - 1, y));
                k.AddCell(x0 + w - 1, y, BeltDir.North, tier);
            }
            for (int x = x0 + w - 1; x > x0; x--)
            {
                cells.Add(new int2(x, y0 + h - 1));
                k.AddCell(x, y0 + h - 1, BeltDir.West, tier);
            }
            for (int y = y0 + h - 1; y > y0; y--)
            {
                cells.Add(new int2(x0, y));
                k.AddCell(x0, y, BeltDir.South, tier);
            }
            return cells;
        }

        private static BeltCellInfo Info(BeltKernel k, int x, int y)
        {
            k.TryGetCellInfo(x, y, out BeltCellInfo i);
            return i;
        }

        /// <summary>每步不变量：账本平衡、容量从未越界、每格物品位置合法且间距 ≥ 1 / 容量 格。</summary>
        private static bool Invariants(BeltKernel k, out string why)
        {
            BeltLedger l = k.Ledger;
            if (!l.Balanced)
            {
                why = $"账本不平：推上 {l.Emitted} + 放入 {l.Inserted} − 收下 {l.Delivered} − 移出 {l.Removed} = {l.Expected} ≠ 在带 {l.OnBelts}";
                return false;
            }
            if (k.CapacityViolations != 0)
            {
                why = $"容量越界 {k.CapacityViolations} 次";
                return false;
            }
            if (k.ItemCount != l.OnBelts)
            {
                why = $"O(1) 物品计数 {k.ItemCount} ≠ 逐格重数 {l.OnBelts}";
                return false;
            }
            var cells = new List<int3>();
            k.CollectCells(cells);
            foreach (int3 c in cells)
            {
                BeltCellInfo i = Info(k, c.x, c.y);
                int last = int.MaxValue;
                for (int s = 0; s < i.Count; s++)
                {
                    int p = i.PosAt(s);
                    if (p < 0 || p >= CL || (s > 0 && last - p < k.Spacing))
                    {
                        why = $"格 ({c.x},{c.y}) 物品位置非法：第 {s} 件 {p}（上一件 {last}）";
                        return false;
                    }
                    last = p;
                }
                // 车道跨格间距：车道上紧挨着的两格（f → n），f 的头一件与 n 的最后一件沿车道至少隔一个间距。
                if (i.Count > 0 && i.HasNext && !LaneGapOk(k, i, out int gap, out BeltCellInfo n))
                {
                    why = $"车道跨格间距不足：({c.x},{c.y}) 头件 {i.Pos0} → ({n.X},{n.Y}) 末件 {n.PosAt(n.Count - 1)}，间距 {gap} < {k.Spacing}";
                    return false;
                }
            }
            why = null;
            return true;
        }

        /// <summary>
        /// <paramref name="f"/> 是否在下游格 n 的车道上（n 在环上：f 是环上的前一格；否则 f 在 n 正后方同向，或 n 只有 f 这一路上游——拐弯）；
        /// 是的话 f 的头一件与 n 的最后一件沿车道的距离要 ≥ 间距。侧向汇入的物品在汇入之前不在车道上，不算。
        /// </summary>
        private static bool LaneGapOk(BeltKernel k, in BeltCellInfo f, out int gap, out BeltCellInfo n)
        {
            gap = int.MaxValue;
            n = default;
            if (f.Count == 0 || !f.HasNext || !k.TryGetCellInfo(f.NextX, f.NextY, out n) || n.Count == 0)
            {
                return true;
            }
            bool inLane = n.InLoop
                ? f.InLoop
                : (f.X == n.X - BeltDirs.Dx((int)n.Dir) && f.Y == n.Y - BeltDirs.Dy((int)n.Dir)) || n.Feeders == 1;
            if (!inLane)
            {
                return true;
            }
            gap = CL - f.Pos0 + n.PosAt(n.Count - 1);
            return gap >= k.Spacing;
        }

        /// <summary>全部车道相邻格对里最小的跨格间距（没有这样的对时 = int.MaxValue）。</summary>
        private static int MinLaneGap(BeltKernel k, List<int3> cells)
        {
            int min = int.MaxValue;
            foreach (int3 c in cells)
            {
                if (k.TryGetCellInfo(c.x, c.y, out BeltCellInfo f))
                {
                    LaneGapOk(k, f, out int gap, out _);
                    min = Math.Min(min, gap);
                }
            }
            return min;
        }

        private static void CheckTierThroughput()
        {
            var parts = new List<string>();
            bool ok = true;
            for (int tier = 0; tier < 3; tier++)
            {
                using (BeltKernel k = NewKernel())
                {
                    AddLine(k, 0, 0, 30, BeltDir.East, tier);
                    k.AddSource(1, 0, 0, (ushort)(tier + 1), 1, BeltConst.Unlimited);
                    k.AddSink(2, 30, 0, BeltConst.Unlimited, 0);
                    // T1 走完 30 格要 120 游戏秒，再留满一个 60 秒统计窗口。
                    k.StepMany(20 * 200);
                    k.TryGetPortInfo(1, out BeltPortInfo src);
                    k.TryGetPortInfo(2, out BeltPortInfo sink);
                    BeltCellInfo mid = Info(k, 15, 0);
                    k.TryGetNetworkStats(mid.Network, out BeltNetworkStats net);
                    int rated = new[] { 60, 120, 240 }[tier];
                    string hover = BeltNetworkService.DescribeCellOf(k, 15, 0);
                    bool tierOk = Math.Abs(sink.InWindow - rated) <= 1 && Math.Abs(src.InWindow - rated) <= 1
                                  && Math.Abs(mid.ThroughputPerMinute - rated) <= 1.01f && mid.RatedItemsPerMinute == rated
                                  && Math.Abs(net.DeliveredPerMinute - rated) <= 1.01f && Math.Abs(sink.WindowSeconds - 60f) < 0.01f
                                  && hover != null && hover.Contains("实测 " + rated + " 件/分钟") && Invariants(k, out _);
                    ok &= tierOk;
                    parts.Add($"T{tier + 1}：输入端口 {sink.PerMinute:0.#}/分、格悬停实测 {mid.ThroughputPerMinute:0.#}/分（设计 {mid.RatedItemsPerMinute}）、网络 {net.DeliveredPerMinute:0.#}/分");
                }
            }
            Expect(ok, "三档传送带满载吞吐 = 60 / 120 / 240 件每分钟（FGT-LOG-005 速度；窗口 60 游戏秒）：" + string.Join("；", parts));
        }

        private static void CheckSaturatedLineMovesTogether()
        {
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 0);
                k.AddSink(9, 10, 0, BeltConst.Unlimited, 0);
                ushort id = 1;
                for (int x = 0; x < 10; x++)
                {
                    for (int s = 0; s < 4; s++)
                    {
                        k.InsertItemAt(x, 0, s * 12000, id++);
                    }
                }
                long sum0 = SumPositions(k, 10);
                k.Step();
                long sum1 = SumPositions(k, 10);
                k.StepMany(19);
                var counts = Enumerable.Range(0, 10).Select(x => Info(k, x, 0).Count).ToArray();
                k.TryGetPortInfo(9, out BeltPortInfo sink);
                bool inv = Invariants(k, out string why);
                Expect(sum1 - sum0 == 40L * 600 && sink.Total == 1 && counts[0] == 3 && counts.Skip(1).All(c => c == 4) && inv,
                    $"满载直线整体匀速（下游优先）：一步后 40 件每件都前进 600 单位（位置和 +{sum1 - sum0}）；20 步后末件送进输入端口 1 件、" +
                    $"每格仍满（{string.Join(",", counts)}）——不会一格一格“蠕动”出空隙{(why != null ? "；" + why : string.Empty)}");
            }
        }

        private static long SumPositions(BeltKernel k, int len)
        {
            long sum = 0;
            for (int x = 0; x < len; x++)
            {
                BeltCellInfo i = Info(k, x, 0);
                for (int s = 0; s < i.Count; s++)
                {
                    sum += i.PosAt(s);
                }
            }
            return sum;
        }

        private static void CheckEndOfBeltAndSinkFull()
        {
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 5, BeltDir.East, 0);
                k.AddSource(1, 0, 0, 7, 1, BeltConst.Unlimited);
                k.StepMany(3000);
                var infos = Enumerable.Range(0, 5).Select(x => Info(k, x, 0)).ToArray();
                k.TryGetPortInfo(1, out BeltPortInfo src);
                bool headStop = infos[4].Block == BeltBlock.EndOfBelt && infos.Take(4).All(i => i.Block == BeltBlock.DownstreamFull);
                string text = BeltNetworkService.DescribeBlock(infos[4]);
                string text2 = BeltNetworkService.DescribeBlock(infos[2]);
                bool inv = Invariants(k, out string why);
                Expect(infos.All(i => i.Count == 4) && src.Total == 20 && src.BlockedSteps > 2000 && headStop && infos[4].Pos0 == CL - 1
                       && text.Contains("到头") && text2.Contains("(3, 0)") && inv && k.BlockedCells == 5,
                    $"末端没接东西：带满 20 件后停下，物品不消失（每格 {string.Join(",", infos.Select(i => i.Count))}）；输出端口推不上去计 {src.BlockedSteps} 步；" +
                    $"原因：末格“{text}”、中间“{text2}”（FGR-LOG-025 下游 X 已满）{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 5, BeltDir.East, 0);
                k.AddSource(1, 0, 0, 7, 1, BeltConst.Unlimited);
                k.AddSink(2, 5, 0, 5, 0);
                k.StepMany(3000);
                k.TryGetPortInfo(2, out BeltPortInfo sink);
                BeltCellInfo head = Info(k, 4, 0);
                int onBelt = k.ItemCount;
                int took = k.TakeFromSink(2, 5);
                k.StepMany(400);
                k.TryGetPortInfo(2, out BeltPortInfo after);
                bool inv = Invariants(k, out string why);
                Expect(sink.Buffered == 5 && sink.Total == 5 && head.Block == BeltBlock.SinkFull && onBelt == 20 && took == 5
                       && after.Total == 10 && after.Buffered == 5 && after.Consumed == 5 && inv,
                    $"输入端口缓存满（5/5）：上游停下、原因“下游输入已满”，带上 {onBelt} 件不丢；建筑取走 {took} 件后恢复流动，又收下 5 件（累计 {after.Total}）{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 5, BeltDir.East, 0);
                k.AddSource(1, 0, 0, 7, 1, BeltConst.Unlimited);
                k.AddSink(2, 5, 0, 3, 40);
                k.StepMany(4000);
                k.TryGetPortInfo(2, out BeltPortInfo sink);
                // 建筑每 40 步（2 游戏秒）消耗一件 → 稳态 30 件/分钟，低于 T1 带速：带满、按消耗节拍放行。
                bool inv = Invariants(k, out string why);
                Expect(Math.Abs(sink.PerMinute - 30f) <= 1.01f && k.ItemCount == 20 && inv,
                    $"输入端口按节拍消耗（每 2 游戏秒 1 件）：稳态收下 {sink.PerMinute:0.#} 件/分钟，带保持满载 {k.ItemCount} 件{(why != null ? "；" + why : string.Empty)}");
            }
        }

        private static List<ushort> RingSequence(BeltKernel k, List<int2> ring)
        {
            // 环顺序：从第 0 格入口起，按前进方向（每格内位置升序）。
            var seq = new List<ushort>();
            foreach (int2 c in ring)
            {
                BeltCellInfo i = Info(k, c.x, c.y);
                for (int s = i.Count - 1; s >= 0; s--)
                {
                    seq.Add(i.ItemAt(s));
                }
            }
            return seq;
        }

        private static void CheckLoop()
        {
            using (BeltKernel k = NewKernel())
            {
                List<int2> ring = AddRing(k, 0, 0, 5, 5, 0);
                ushort id = 1;
                foreach (int2 c in ring)
                {
                    for (int s = 0; s < 4; s++)
                    {
                        k.InsertItemAt(c.x, c.y, s * 12000, id++);
                    }
                }
                List<ushort> before = RingSequence(k, ring);
                k.StepMany(20);
                List<ushort> after = RingSequence(k, ring);
                bool rotatedByOne = before.Count == 64 && after.Count == 64 && Enumerable.Range(0, 64).All(i => after[(i + 1) % 64] == before[i]);
                BeltCellInfo c0 = Info(k, 0, 0);
                k.TryGetNetworkStats(c0.Network, out BeltNetworkStats net);
                k.StepMany(20 * 70);
                BeltCellInfo c1 = Info(k, 2, 0);
                bool multiset = RingSequence(k, ring).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, 64).Select(x => (ushort)x));
                bool inv = Invariants(k, out string why);
                Expect(ring.Count == 16 && net.HasCycle && net.Cells == 16 && c0.InLoop && rotatedByOne && multiset && k.ItemCount == 64
                       && Math.Abs(c1.ThroughputPerMinute - 60f) <= 1.01f && c1.Block == BeltBlock.None && inv,
                    $"首尾相连的环（16 格）装满 64 件：一直转，20 步后环上物品顺序整体平移一个间距（{rotatedByOne}）；吞吐统计 {c1.ThroughputPerMinute:0.#} 件/分钟（满载 T1）；" +
                    $"没有刷出或吞掉物品（64 件、种类集合不变）；不报堵塞{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                List<int2> ring = AddRing(k, 0, 0, 6, 4, 2);
                ushort id = 100;
                foreach (int2 c in ring.Where((_, i) => i % 2 == 0))
                {
                    k.InsertItemAt(c.x, c.y, 6000, id++);
                    k.InsertItemAt(c.x, c.y, 30000, id++);
                }
                // 一条 T1 支线从下方侧向汇入环的第 2 格；环为 T3、半满。
                AddLine(k, 2, -6, 6, BeltDir.North, 0);
                k.AddSource(5, 2, -6, 9, 1, BeltConst.Unlimited);
                int start = k.ItemCount;
                long violations = 0;
                bool balanced = true;
                for (int t = 0; t < 30; t++)
                {
                    k.StepMany(200);
                    balanced &= Invariants(k, out _);
                    violations += k.CapacityViolations;
                }
                int ringItems = ring.Sum(c => Info(k, c.x, c.y).Count);
                k.TryGetPortInfo(5, out BeltPortInfo src);
                BeltCellInfo feederHead = Info(k, 2, -1);
                Expect(balanced && violations == 0 && ringItems == ring.Count * 4 && k.ItemCount == start + src.Total && src.BlockedSteps > 0
                       && (feederHead.Block == BeltBlock.DownstreamFull || feederHead.Block == BeltBlock.MergeWait),
                    $"支线侧向汇入混排等级的环（T3 环 {ring.Count} 格 + T1 支线）：环被逐步填满到 {ringItems} 件（容量 {ring.Count * 4}），之后支线堵住（原因 {feederHead.Block}）；" +
                    $"全程每 200 步核对账本平衡、间距与容量；物品数 = 初始 {start} + 推上 {src.Total}");
            }
        }

        private static void CheckMergeFairness()
        {
            // 两路：主线（西）+ 支线（南）汇入同一格，下游接满速输入端口。
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 0);
                AddLine(k, 10, 0, 11, BeltDir.East, 0);
                AddLine(k, 10, -10, 10, BeltDir.North, 0);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.AddSource(2, 10, -10, 2, 1, BeltConst.Unlimited);
                k.AddSink(3, 21, 0, BeltConst.Unlimited, 0);
                k.StepMany(20 * 200);
                bool sawWait = false;
                string waitText = null;
                for (int t = 0; t < 60; t++)
                {
                    k.Step();
                    foreach (BeltCellInfo w in new[] { Info(k, 9, 0), Info(k, 10, -1) })
                    {
                        if (w.Block == BeltBlock.MergeWait)
                        {
                            sawWait = true;
                            waitText ??= BeltNetworkService.DescribeBlock(w);
                        }
                    }
                }
                var seq = new List<ushort>();
                for (int x = 20; x >= 11; x--)
                {
                    BeltCellInfo i = Info(k, x, 0);
                    for (int s = 0; s < i.Count; s++)
                    {
                        seq.Add(i.ItemAt(s));
                    }
                }
                bool alternates = seq.Count >= 30 && Enumerable.Range(1, seq.Count - 1).All(i => seq[i] != seq[i - 1]);
                k.TryGetPortInfo(1, out BeltPortInfo a);
                k.TryGetPortInfo(2, out BeltPortInfo b);
                k.TryGetPortInfo(3, out BeltPortInfo o);
                BeltCellInfo merge = Info(k, 10, 0);
                bool inv = Invariants(k, out string why);
                Expect(alternates && Math.Abs(a.InWindow - 30) <= 1 && Math.Abs(b.InWindow - 30) <= 1 && Math.Abs(o.PerMinute - 60f) <= 1.01f && merge.Feeders == 2
                       && sawWait && inv,
                    $"侧向汇入严格交替（FGR-LOG-021 公平）：下游 {seq.Count} 件种类逐件交替；两路各 {a.InWindow}/{b.InWindow} 件每分钟，下游满速 {o.PerMinute:0.#}；" +
                    $"等待的一路原因“{waitText}”{(why != null ? "；" + why : string.Empty)}");
            }

            // 三路：北、南、西三路汇入。
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 1);
                AddLine(k, 10, 0, 11, BeltDir.East, 1);
                AddLine(k, 10, -10, 10, BeltDir.North, 1);
                AddLine(k, 10, 10, 10, BeltDir.South, 1);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.AddSource(2, 10, -10, 2, 1, BeltConst.Unlimited);
                k.AddSource(3, 10, 10, 3, 1, BeltConst.Unlimited);
                k.AddSink(4, 21, 0, BeltConst.Unlimited, 0);
                k.StepMany(20 * 200);
                int[] win = new int[3];
                for (int p = 1; p <= 3; p++)
                {
                    k.TryGetPortInfo(p, out BeltPortInfo info);
                    win[p - 1] = info.InWindow;
                }
                k.TryGetPortInfo(4, out BeltPortInfo o);
                bool inv = Invariants(k, out string why);
                Expect(win.All(w => Math.Abs(w - 40) <= 1) && Math.Abs(o.PerMinute - 120f) <= 1.01f && Info(k, 10, 0).Feeders == 3 && inv,
                    $"三路汇入（T2）：每路 {string.Join("/", win)} 件每分钟（各 1/3），下游满速 {o.PerMinute:0.#}{(why != null ? "；" + why : string.Empty)}");
            }

            // 轮到的一路没货：别的一路不空等。
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 0);
                AddLine(k, 10, 0, 11, BeltDir.East, 0);
                AddLine(k, 10, -10, 10, BeltDir.North, 0);
                k.AddSource(2, 10, -10, 2, 1, BeltConst.Unlimited);
                k.AddSink(3, 21, 0, BeltConst.Unlimited, 0);
                k.StepMany(20 * 200);
                k.TryGetPortInfo(3, out BeltPortInfo o);
                bool inv = Invariants(k, out string why);
                Expect(Math.Abs(o.PerMinute - 60f) <= 1.01f && inv,
                    $"汇入点另一路空着：有货的一路满速通过（{o.PerMinute:0.#} 件每分钟），不会因为“轮到对方”而空等{(why != null ? "；" + why : string.Empty)}");
            }
        }

        /// <summary>环上全部物品按环顺序的（格序号、格内位置、种类）序列：用来判断环这一段时间里有没有在转。</summary>
        private static List<long> RingSignature(BeltKernel k, List<int2> ring)
        {
            var sig = new List<long>();
            for (int idx = 0; idx < ring.Count; idx++)
            {
                BeltCellInfo i = Info(k, ring[idx].x, ring[idx].y);
                for (int s = 0; s < i.Count; s++)
                {
                    sig.Add(((long)idx << 40) | ((long)i.PosAt(s) << 16) | i.ItemAt(s));
                }
            }
            return sig;
        }

        /// <summary>
        /// 审查 P1 回归：环上有侧向汇入、差一件就满时不能整环卡死（FG03 第 5 节“环上的物品一直转”；B11）。
        /// 让路名额 = 环上空位数（按环顺序分给想让路的汇入点），让路停位 = 入口后正好一个间距。
        /// </summary>
        private static void CheckRingMergeNoDeadlock()
        {
            // (a) 构造：16 格环放 63 件，唯一的双倍间隙正好在 (1,0) 头件前方；支线从南侧汇入 (2,0)，头件已在入口前。三档环。
            var parts = new List<string>();
            bool okA = true;
            for (int tier = 0; tier < 3; tier++)
            {
                using (BeltKernel k = NewKernel())
                {
                    List<int2> ring = AddRing(k, 0, 0, 5, 5, tier);
                    long L = (long)ring.Count * CL;
                    long head = 1L * CL + 30000;
                    for (int j = 0; j < 63; j++)
                    {
                        long rx = ((head - (long)j * k.Spacing) % L + L) % L;
                        int2 c = ring[(int)(rx / CL)];
                        k.InsertItemAt(c.x, c.y, (int)(rx % CL), (ushort)(1000 + j));
                    }
                    AddLine(k, 2, -6, 6, BeltDir.North, 0);
                    k.InsertItemAt(2, -1, CL - 1, 7);
                    k.AddSource(1, 2, -6, 9, 1, BeltConst.Unlimited);
                    bool inv = true;
                    string why = null;
                    var sigs = new List<List<long>>();
                    for (int t = 0; t < 2000; t++)
                    {
                        k.Step();
                        if (t % 50 == 0 && !Invariants(k, out string w))
                        {
                            inv = false;
                            why ??= w;
                        }
                        if (t >= 1900)
                        {
                            sigs.Add(RingSignature(k, ring));
                        }
                    }
                    int n = ring.Sum(c => Info(k, c.x, c.y).Count);
                    bool moving = sigs.Skip(1).Any(sg => !sg.SequenceEqual(sigs[0]));
                    bool thisOk = n == 64 && moving && inv && Invariants(k, out _);
                    okA &= thisOk;
                    parts.Add($"T{tier + 1} 环 {n}/64{(moving ? "、在转" : "、不动")}{(why != null ? "（" + why + "）" : string.Empty)}");
                }
            }
            Expect(okA, "环差一件就满、唯一空隙正对支线汇入点：让路停在入口后正好一个间距，支线插进去把环填满，环接着转（旧实现停在 63/64 永久卡死）：" + string.Join("；", parts));

            // (b) 两条支线汇入同一个 18 格环（审查员复现：两处同时让路把仅剩的空隙摊薄 → 整环停在 71/72）：各档等级组合、两种节拍，从空环填到满。
            int combos = 0;
            var bad = new List<string>();
            for (int rt = 0; rt < 3; rt++)
            {
                for (int s1 = 0; s1 < 3; s1++)
                {
                    for (int s2 = 0; s2 < 3; s2++)
                    {
                        foreach ((int i1, int i2) in new[] { (1, 1), (3, 1), (1, 7) })
                        {
                            combos++;
                            using (BeltKernel k = NewKernel())
                            {
                                List<int2> ring = AddRing(k, 0, 0, 6, 5, rt);
                                AddLine(k, 2, -6, 6, BeltDir.North, s1);
                                k.AddSource(1, 2, -6, 9, i1, BeltConst.Unlimited);
                                AddLine(k, 3, 10, 6, BeltDir.South, s2);
                                k.AddSource(2, 3, 10, 8, i2, BeltConst.Unlimited);
                                bool inv = true;
                                string why = null;
                                var sigs = new List<List<long>>();
                                for (int t = 0; t < 3000; t++)
                                {
                                    k.Step();
                                    if (t % 100 == 0 && !Invariants(k, out string w))
                                    {
                                        inv = false;
                                        why ??= w;
                                    }
                                    if (t >= 2900)
                                    {
                                        sigs.Add(RingSignature(k, ring));
                                    }
                                }
                                int n = ring.Sum(c => Info(k, c.x, c.y).Count);
                                bool moving = sigs.Skip(1).Any(sg => !sg.SequenceEqual(sigs[0]));
                                if (n != ring.Count * 4 || !moving || !inv || k.CapacityViolations != 0)
                                {
                                    bad.Add($"环 T{rt + 1} + 支线 T{s1 + 1}(每 {i1} 步)/T{s2 + 1}(每 {i2} 步)：{n}/{ring.Count * 4}{(moving ? string.Empty : "、环不动")}{(why != null ? "、" + why : string.Empty)}");
                                }
                            }
                        }
                    }
                }
            }
            Expect(bad.Count == 0,
                $"两条支线汇入同一个环（18 格、容量 72）：{combos} 种等级 / 节拍组合从空环开始全部填到 72/72，填满后环照常转、支线停下；" +
                $"每 100 步核对账本、容量、格内与车道跨格间距（不符 {bad.Count}：{string.Join("；", bad.Take(3))}）");
        }

        /// <summary>
        /// 审查 P2 回归：车道跨格间距在汇入点与等级接缝处都成立（车道上游不能“追”进下游格最后一件的一个间距以内，
        /// 包括同一步刚从侧向汇入的那件）。旧实现在树形汇入点最多差 2,400、在 T3 直接接 T1 的接缝最多差 9,000。
        /// </summary>
        private static void CheckLaneSpacingAcrossCells()
        {
            var parts = new List<string>();
            bool ok = true;
            foreach ((int main, int side) in new[] { (0, 0), (1, 0), (2, 0), (0, 2), (2, 2) })
            {
                using (BeltKernel k = NewKernel())
                {
                    // 车道在西、支线在南（支线先于车道处理——正是审查员指出的顺序）。
                    AddLine(k, 0, 0, 10, BeltDir.East, main);
                    AddLine(k, 10, 0, 11, BeltDir.East, main);
                    AddLine(k, 10, -10, 10, BeltDir.North, side);
                    k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                    k.AddSource(2, 10, -10, 2, 1, BeltConst.Unlimited);
                    k.AddSink(3, 21, 0, BeltConst.Unlimited, 0);
                    var cells = new List<int3>();
                    k.CollectCells(cells);
                    int min = int.MaxValue;
                    for (int t = 0; t < 4000; t++)
                    {
                        k.Step();
                        min = Math.Min(min, MinLaneGap(k, cells));
                    }
                    k.TryGetPortInfo(3, out BeltPortInfo o);
                    bool thisOk = min >= k.Spacing && o.Total > 50 && Invariants(k, out _);
                    ok &= thisOk;
                    parts.Add($"主线 T{main + 1} / 支线 T{side + 1} 汇入点最小 {min}");
                }
            }
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 2);
                AddLine(k, 10, 0, 10, BeltDir.East, 0);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.AddSink(2, 20, 0, BeltConst.Unlimited, 0);
                var cells = new List<int3>();
                k.CollectCells(cells);
                int min = int.MaxValue;
                for (int t = 0; t < 3000; t++)
                {
                    k.Step();
                    min = Math.Min(min, MinLaneGap(k, cells));
                }
                ok &= min >= k.Spacing && Invariants(k, out _);
                parts.Add($"T3 直接接 T1 的接缝最小 {min}");
            }
            Expect(ok, $"车道跨格间距（ADR-ARC-004 第 2 节）：每步逐对核对车道上紧挨着的两格，间距 ≥ {12000}：{string.Join("；", parts)}");
        }

        private static void CheckEdgeRules()
        {
            using (BeltKernel k = NewKernel())
            {
                k.AddCell(0, 0, BeltDir.East, 0);
                k.AddCell(1, 0, BeltDir.West, 0);
                k.InsertItem(0, 0, 1);
                k.StepMany(200);
                BeltCellInfo a = Info(k, 0, 0);
                bool inv = Invariants(k, out string why);
                Expect(!a.HasNext && a.Block == BeltBlock.EndOfBelt && a.Count == 1 && inv,
                    $"两条传送带头对头：不相连（各自到头，原因“{BeltNetworkService.DescribeBlock(a)}”），物品不会被推到对面{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 3, BeltDir.East, 0);
                k.InsertItemAt(1, 0, 3000, 1);
                k.InsertItemAt(1, 0, 40000, 2);
                BeltResult r = k.SetDirection(1, 0, BeltDir.West);
                BeltCellInfo mid = Info(k, 1, 0);
                bool mirrored = mid.Count == 2 && mid.Pos0 == CL - 1 - 3000 && mid.Item0 == 1 && mid.Pos1 == CL - 1 - 40000 && mid.Item1 == 2;
                k.SetDirection(0, 0, BeltDir.West);
                k.SetDirection(2, 0, BeltDir.West);
                k.AddSink(8, -1, 0, BeltConst.Unlimited, 0);
                k.StepMany(600);
                k.TryGetPortInfo(8, out BeltPortInfo sink);
                bool inv = Invariants(k, out string why);
                Expect(r == BeltResult.Ok && mirrored && sink.Total == 2 && k.ItemCount == 0 && inv,
                    $"原地反转方向（FGR-LOG-020）：格内物品位置镜像、头尾对调、不丢不增；整条反转后物品反向流出，西端输入端口收下 {sink.Total} 件{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 4, BeltDir.East, 0);
                k.InsertItemAt(2, 0, 0, 3);
                k.InsertItemAt(2, 0, 20000, 4);
                k.InsertItemAt(3, 0, 1000, 5);
                var removed = new List<ushort>();
                BeltResult r = k.RemoveCell(2, 0, removed);
                BeltResult again = k.RemoveCell(2, 0, removed);
                k.StepMany(10);
                BeltCellInfo left = Info(k, 1, 0);
                bool inv = Invariants(k, out string why);
                Expect(r == BeltResult.Ok && again == BeltResult.NotFound && removed.Count == 2 && removed[0] == 4 && removed[1] == 3 && k.Ledger.Removed == 2
                       && !left.HasNext && k.ItemCount == 1 && inv,
                    $"拆掉带物品的一格：{removed.Count} 件按顺序交还调用方并记“移出”（FG3-LOG-02 接全额返还），上游变成末端；重复拆除给“没有传送带”{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 10, BeltDir.East, 2);
                AddLine(k, 10, 0, 10, BeltDir.East, 0);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.AddSink(2, 20, 0, BeltConst.Unlimited, 0);
                k.StepMany(20 * 200);
                k.TryGetPortInfo(2, out BeltPortInfo slowEnd);
                k.TryGetPortInfo(1, out BeltPortInfo fastStart);
                bool inv = Invariants(k, out string why);
                Expect(Math.Abs(slowEnd.PerMinute - 60f) <= 1.01f && Math.Abs(fastStart.PerMinute - 60f) <= 1.01f && inv,
                    $"等级混排（T3 接 T1）：整条按最慢一段 {slowEnd.PerMinute:0.#} 件每分钟，快段自然压实排队，不重叠{(why != null ? "；" + why : string.Empty)}");
            }

            using (BeltKernel k = NewKernel())
            {
                k.AddCell(0, 0, BeltDir.East, 0);
                var results = new Dictionary<string, BeltResult>
                {
                    ["重复放"] = k.AddCell(0, 0, BeltDir.North, 1),
                    ["等级 3"] = k.AddCell(1, 1, BeltDir.North, 3),
                    ["方向 7"] = k.AddCell(1, 1, (BeltDir)7, 0),
                    ["坐标超限"] = k.AddCell(1 << 25, 0, BeltDir.North, 0),
                    ["没有格"] = k.InsertItem(5, 5, 1),
                    ["位置越界"] = k.InsertItemAt(0, 0, CL, 1),
                    ["端口号重复"] = k.AddSource(1, 0, 0, 1, 1, 1) == BeltResult.Ok ? k.AddSink(1, 1, 0, 1, 0) : BeltResult.Ok,
                    ["删不存在的端口"] = k.RemovePort(42),
                    ["节拍 0"] = k.AddSource(9, 3, 3, 1, 0, 1),
                    ["缓存上限 -5"] = k.AddSink(10, 3, 3, -5, 0),
                    ["改不存在的等级"] = k.SetTier(9, 9, 1),
                };
                k.InsertItemAt(0, 0, 10000, 1);
                results["重叠放入"] = k.InsertItemAt(0, 0, 15000, 2);
                var expected = new Dictionary<string, BeltResult>
                {
                    ["重复放"] = BeltResult.Occupied, ["等级 3"] = BeltResult.InvalidTier, ["方向 7"] = BeltResult.InvalidDirection,
                    ["坐标超限"] = BeltResult.OutOfRange, ["没有格"] = BeltResult.NotFound, ["位置越界"] = BeltResult.InvalidArgument,
                    ["端口号重复"] = BeltResult.PortOccupied, ["删不存在的端口"] = BeltResult.PortNotFound, ["节拍 0"] = BeltResult.InvalidArgument,
                    ["缓存上限 -5"] = BeltResult.InvalidArgument, ["改不存在的等级"] = BeltResult.NotFound, ["重叠放入"] = BeltResult.NoSpace,
                };
                var wrong = expected.Where(e => results[e.Key] != e.Value).Select(e => $"{e.Key}={results[e.Key]}").ToList();
                bool inv = Invariants(k, out string why);
                Expect(wrong.Count == 0 && inv,
                    $"内核负向：{expected.Count} 种非法编辑都给稳定原因码并映射到文本（如“{BeltOpResult.Kernel(BeltResult.NoSpace).Describe()}”），状态不变（不符：{string.Join("、", wrong)}）{(why != null ? "；" + why : string.Empty)}");
            }
        }

        // ── C 确定性 ─────────────────────────────────────────────────────────────

        /// <summary>组合场景：两条带汇入、一个环、支线、三档混排、有限与无限供货、有缓存上限的输入端口。<paramref name="shuffleSeed"/> 打乱放置顺序。</summary>
        private static void BuildComposite(BeltKernel k, int shuffleSeed)
        {
            var ops = new List<Action>();
            for (int i = 0; i < 12; i++)
            {
                int x = i;
                ops.Add(() => k.AddCell(x, 0, BeltDir.East, x < 6 ? 2 : 0));
            }
            for (int i = 0; i < 6; i++)
            {
                int y = -6 + i;
                ops.Add(() => k.AddCell(6, y, BeltDir.North, 1));
            }
            for (int i = 0; i < 8; i++)
            {
                int y = 8 - i;
                ops.Add(() => k.AddCell(9, y, BeltDir.South, 0));
            }
            if (shuffleSeed != 0)
            {
                var rng = new System.Random(shuffleSeed);
                ops = ops.OrderBy(_ => rng.Next()).ToList();
            }
            foreach (Action op in ops)
            {
                op();
            }
            AddRing(k, 20, 0, 6, 5, 1);
            k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
            k.AddSource(2, 6, -6, 2, 3, 500);
            k.AddSource(3, 9, 8, 3, 2, BeltConst.Unlimited);
            k.AddSink(4, 12, 0, 12, 7);
            for (int x = 20; x < 25; x++)
            {
                k.InsertItemAt(x, 0, 1000, (ushort)(40 + x));
                k.InsertItemAt(x, 0, 30000, (ushort)(60 + x));
            }
        }

        private static void CheckDeterminism()
        {
            const int total = 3000;
            var seqA = new List<ulong>();
            var seqB = new List<ulong>();
            var seqC = new List<ulong>();
            using (BeltKernel a = NewKernel())
            using (BeltKernel b = NewKernel())
            using (BeltKernel c = NewKernel())
            {
                BuildComposite(a, 0);
                BuildComposite(b, 777);
                BuildComposite(c, 0);
                for (int s = 0; s < total; s += 100)
                {
                    a.StepMany(100);
                    b.StepMany(100);
                    c.StepMany(100);
                    seqA.Add(a.ComputeStateHash());
                    seqB.Add(b.ComputeStateHash());
                    seqC.Add(c.ComputeStateHash());
                }
                bool moved = seqA.Distinct().Count() > 20;
                bool inv = Invariants(a, out string why);
                Expect(moved && seqA.SequenceEqual(seqB) && seqA.SequenceEqual(seqC) && inv,
                    $"确定性回放：同一组传送带以两种放置顺序建成、以及原样重跑，每 100 步的状态哈希全部一致（{seqA.Count} 个检查点，状态确实在变化：{seqA.Distinct().Count()} 种）{(why != null ? "；" + why : string.Empty)}");

                BeltSnapshot snap = a.Serialize();
                using (BeltKernel d = NewKernel())
                {
                    bool loaded = d.Deserialize(snap, out string err);
                    bool same = d.ComputeStateHash() == a.ComputeStateHash();
                    a.StepMany(1500);
                    d.StepMany(1500);
                    bool inv_why2 = Invariants(d, out string why2);
                    Expect(loaded && err == null && same && a.ComputeStateHash() == d.ComputeStateHash() && d.CellCount == a.CellCount && inv_why2,
                        $"中途序列化（{snap.Networks.Count} 个网络块，{snap.TotalBytes} 字节）→ 新内核恢复：哈希一致；两边各再跑 1,500 步仍逐位一致（端口节拍、汇入轮次都进了存档）{(why2 != null ? "；" + why2 : string.Empty)}");
                }
            }
        }

        // ── D 物品守恒 ───────────────────────────────────────────────────────────

        private static void CheckConservationTenDays()
        {
            using (BeltKernel k = NewKernel())
            {
                BuildComposite(k, 0);
                // 闭合产线：再接一个“取料→回送”的建筑（输入端口缓存满后由测试按节拍取出、再从输出端口补货），物品在系统里循环。
                k.AddSource(20, 20, 0, 99, 5, 0);
                int days = 10;
                int stepsPerDay = (int)Math.Round(GameClock.DaySeconds * k.Config.StepHz);
                int checks = 0;
                bool ok = true;
                string firstWhy = null;
                var sw = Stopwatch.StartNew();
                int totalSteps = days * stepsPerDay;
                for (int s = 0; s < totalSteps; s += 1000)
                {
                    k.StepMany(Math.Min(1000, totalSteps - s));
                    int taken = k.TakeFromSink(4, 3);
                    k.AddSourceItems(20, taken);
                    checks++;
                    if (!Invariants(k, out string why))
                    {
                        ok = false;
                        firstWhy ??= why;
                    }
                }
                sw.Stop();
                BeltLedger l = k.Ledger;
                Expect(ok && l.Delivered > 1000 && l.Emitted > 1000 && k.StepIndex == totalSteps,
                    $"物品守恒（FGT-LOG-006）：闭合产线跑 {days} 个游戏日（{totalSteps:N0} 步，{sw.Elapsed.TotalSeconds:F1} 秒），{checks} 次核对账目全部平衡——" +
                    $"推上 {l.Emitted:N0} + 放入 {l.Inserted} − 收下 {l.Delivered:N0} − 移出 {l.Removed} = 在带 {l.OnBelts}{(firstWhy != null ? "；首个失败：" + firstWhy : string.Empty)}");
            }
        }

        // ── G 渲染缓冲 ───────────────────────────────────────────────────────────

        private static void CheckRenderBuffers()
        {
            using (BeltKernel k = NewKernel())
            {
                k.AddCell(5, 7, BeltDir.East, 1);
                k.AddCell(6, 7, BeltDir.North, 1);
                k.InsertItemAt(5, 7, 0, 3);
                k.InsertItemAt(5, 7, 24000, 4);
                k.InsertItemAt(6, 7, 36000, 5);
                k.PrepareRender(1f, out NativeArray<BeltInstance> cells, out int cellCount, out NativeArray<BeltInstance> items, out int itemCount, force: true);
                var pos = new List<float2>();
                for (int i = 0; i < itemCount; i++)
                {
                    pos.Add(items[i].A.xy);
                }
                bool cellOk = cellCount == 2 && Approximately(cells[0].A, new float4(5, 7, 1, 0)) && Approximately(cells[1].A, new float4(6, 7, 0, 1))
                              && Math.Abs(cells[0].B.x - 0.5f) < 1e-4f && Math.Abs(cells[0].B.y - 0.5f) < 1e-4f;
                bool itemOk = itemCount == 3 && pos.Any(p => Near(p, new float2(5.0f, 7f))) && pos.Any(p => Near(p, new float2(4.5f, 7f)))
                              && pos.Any(p => Near(p, new float2(6f, 7.25f)));
                // 堵塞格带标记：跑到 (6,7) 末端堵住。
                k.StepMany(400);
                k.PrepareRender(1f, out cells, out cellCount, out items, out itemCount);
                BeltCellInfo end = Info(k, 6, 7);
                bool blockFlag = end.Block == BeltBlock.EndOfBelt && Math.Abs(cells[1].B.z - (float)BeltBlock.EndOfBelt) < 1e-4f;
                Expect(cellOk && itemOk && blockFlag,
                    $"渲染缓冲 = 内核状态（IC-REQ-010 表现不另算）：格实例的中心 / 朝向 / 带速（T2 = 0.5 格每秒）/ 占用率，物品实例的世界位置（格中心 ± 位置比例）；" +
                    $"堵塞格的实例带堵塞码（着色器画斜纹 + 红色）");
            }

            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 6, BeltDir.East, 0);
                k.InsertItem(0, 0, 1);
                var r = new BeltRenderer();
                BeltRenderer.Settings st = BeltNetworkService.ReadRenderSettings();
                var modes = new List<string>();
                float[] zooms = { 12f, st.FlowOrthoEnter + 1f, st.FlowOrthoExit + 2f, st.FlowOrthoExit - 1f, st.FlowOrthoEnter + 2f };
                var far = new List<bool>();
                foreach (float z in zooms)
                {
                    r.Draw(k, null, z, 0.5f, st);
                    far.Add(r.FarMode);
                    modes.Add($"{z}→{(r.FarMode ? "远景" : "近景")}({r.LastItemInstances} 件实例)");
                }
                bool expected = far.SequenceEqual(new[] { false, true, true, false, true }) && r.ModeSwitches == 3 && r.LastItemInstances == 0 && r.LastCellInstances == 6;
                // 远景下内核每步照走，但物品实例缓冲不重填、不上传（审查 P2：大网络每步省约 1 MB）；切回近景时补一次。
                int fillsFar0 = k.RenderItemFills;
                int itemUploadsFar0 = r.ItemUploads;
                int cellUploadsFar0 = r.Uploads;
                for (int t = 0; t < 5; t++)
                {
                    k.Step();
                    r.Draw(k, null, st.FlowOrthoEnter + 2f, 0.5f, st);
                }
                bool farSkips = k.RenderItemFills == fillsFar0 && r.ItemUploads == itemUploadsFar0 && (!r.GpuAvailable || r.Uploads == cellUploadsFar0 + 5);
                r.Draw(k, null, 12f, 0.5f, st);
                expected &= r.LastItemInstances == 1;
                bool nearRefills = k.RenderItemFills == fillsFar0 + 1 && (!r.GpuAvailable || r.ItemUploads == itemUploadsFar0 + 1);
                string gpu = r.GpuAvailable ? "有图形设备，真实提交绘制" : "本环境 " + r.GpuUnavailableReason + "：只准备实例、跳过 GPU 调用";
                r.Dispose();
                Expect(expected,
                    $"近景逐物品实例化、远景切流动贴图（进入 {st.FlowOrthoEnter} / 退出 {st.FlowOrthoExit}，带回差不来回闪）：{string.Join("，", modes)}；{gpu}");
                Expect(farSkips && nearRefills,
                    $"远景不重填物品实例：内核走 5 步、远景画 5 帧，物品缓冲重填 {k.RenderItemFills - fillsFar0 - (nearRefills ? 1 : 0)} 次、上传 {r.ItemUploads - itemUploadsFar0 - (nearRefills && r.GpuAvailable ? 1 : 0)} 次（应为 0；格实例照常更新）；" +
                    $"切回近景补一次（{(nearRefills ? "是" : "否")}）");
            }
        }

        private static bool Approximately(float4 a, float4 b) => math.all(math.abs(a - b) < 1e-4f);

        private static bool Near(float2 a, float2 b) => math.distance(a, b) < 1e-3f;

        // ── E 规模 / 快照 ────────────────────────────────────────────────────────

        /// <summary>FGR-SYS-041 规模：96 条 150 格直线（三档轮换、每条有输出与输入端口）+ 12 个 50 格的环 = 15,000 格；每格 2 件 = 30,000 件。</summary>
        private static void BuildHuge(BeltKernel k, int ox, int oy)
        {
            for (int line = 0; line < 96; line++)
            {
                AddLine(k, ox, oy + line, 150, BeltDir.East, line % 3);
                k.AddSource(1000 + line, ox, oy + line, (ushort)(1 + line % 7), 1, BeltConst.Unlimited);
                k.AddSink(2000 + line, ox + 150, oy + line, BeltConst.Unlimited, 0);
            }
            for (int r = 0; r < 12; r++)
            {
                AddRing(k, ox + 200 + (r % 6) * 20, oy + (r / 6) * 20, 13, 14, r % 3);
            }
            var cells = new List<int3>();
            k.CollectCells(cells);
            foreach (int3 c in cells)
            {
                k.InsertItemAt(c.x, c.y, 3000, (ushort)(1 + (c.x & 7)));
                k.InsertItemAt(c.x, c.y, 27000, (ushort)(1 + (c.y & 7)));
            }
        }

        private static void CheckScaleAndSnapshot()
        {
            using (BeltKernel k = NewKernel())
            {
                var build = Stopwatch.StartNew();
                BuildHuge(k, 0, 0);
                k.EnsureTopology();
                build.Stop();
                int cells = k.CellCount;
                int items = k.ItemCount;
                double rebuild = k.LastRebuildMs;
                k.StepMany(60);
                k.ResetMaxStepMs();
                var sw = Stopwatch.StartNew();
                const int n = 600;
                var samples = new List<double>(n);
                for (int i = 0; i < n; i++)
                {
                    k.Step();
                    samples.Add(k.LastStepMs);
                }
                sw.Stop();
                samples.Sort();
                double avg = samples.Average();
                double p95 = samples[(int)(n * 0.95)];
                double max = k.MaxStepMs;
                int itemsAfter = k.ItemCount;
                int netCount = k.NetworkCount;
                k.PrepareRender(1f, out _, out int rc, out _, out int ri, force: true);
                double prep = k.LastRenderPrepMs;
                var ser = Stopwatch.StartNew();
                BeltSnapshot snap = k.Serialize();
                ser.Stop();
                ulong h = k.ComputeStateHash();
                double deMs;
                ulong h2;
                using (BeltKernel d = NewKernel())
                {
                    var de = Stopwatch.StartNew();
                    d.Deserialize(snap, out _);
                    de.Stop();
                    deMs = de.Elapsed.TotalMilliseconds;
                    h2 = d.ComputeStateHash();
                }
                PerfLines.Add($"FGR-SYS-041 规模 {cells:N0} 格 / {items:N0} 件（{snap.Networks.Count} 个网络，含 12 个环）：单步平均 {avg:F3} ms、P95 {p95:F3} ms、最大 {max:F3} ms（{n} 步）；" +
                              $"拓扑重建 {rebuild:F1} ms（只在编辑后）；渲染缓冲 {prep:F2} ms（{rc:N0} 格 + {ri:N0} 件实例，只在状态变化后）；序列化 {ser.Elapsed.TotalMilliseconds:F1} ms / " +
                              $"{snap.TotalBytes / 1024.0:F0} KB，反序列化 {deMs:F1} ms");
                Expect(cells == 15000 && items == 30000 && itemsAfter >= 30000 && netCount == 108 && avg <= 2.0 && p95 <= 2.0,
                    $"FGR-ARC-004 / FGR-SYS-042：15,000 格、起始 30,000 件（测量期间 96 个输出端口持续补货，件数 {itemsAfter:N0} ≥ 30,000），内核单步平均 {avg:F3} ms、P95 {p95:F3} ms（预算 ≤ 2 ms）");
                Expect(h == h2 && snap.Networks.Count > 100,
                    $"超大网络按网络分块序列化（{snap.Networks.Count} 块）→ 恢复：状态哈希逐位一致");
            }
        }

        // ── F 世界集成 ───────────────────────────────────────────────────────────

        /// <summary>新战役：经真实入口载入家园、镜头在家园。</summary>
        private static CampaignState NewWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgbelt-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            return s;
        }

        /// <summary>在核心附近找一块 w×h 的矩形，每一格都能放传送带（种子无关：按规则搜索，不写死坐标，B25）。</summary>
        private static bool FindFreeBox(CampaignState s, int w, int h, out GridCell origin)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 6; r <= 30; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var o = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        for (int y = 0; y < h && ok; y++)
                        {
                            for (int x = 0; x < w && ok; x++)
                            {
                                ok = HomeGridService.ValidateBeltCell(s, new GridCell(o.X + x, o.Y + y)).Ok;
                            }
                        }
                        if (ok)
                        {
                            origin = o;
                            return true;
                        }
                    }
                }
            }
            origin = default;
            return false;
        }

        /// <summary>经正式入口（BeltNetworkService.TryPlace，含格网校验）在家园铺一套：直线 + 支线汇入 + 环，带端口与物品。返回格数。</summary>
        private static int LayHomeNetwork(CampaignState s, out GridCell origin, out string failure)
        {
            failure = null;
            if (!FindFreeBox(s, 13, 9, out origin))
            {
                failure = "核心附近找不到 13×9 的空地";
                return 0;
            }
            int ox = origin.X;
            int oy = origin.Y;
            int n = 0;
            var fails = new List<string>();
            void Place(int x, int y, BeltDir d, int tier)
            {
                BeltOpResult r = BeltNetworkService.TryPlace(s, new GridCell(x, y), d, tier);
                if (r.Ok)
                {
                    n++;
                }
                else
                {
                    fails.Add($"({x},{y}) {r.Describe()}");
                }
            }
            for (int x = 0; x < 12; x++)
            {
                Place(ox + x, oy + 1, BeltDir.East, x < 6 ? 1 : 0);
            }
            for (int y = 5; y >= 2; y--)
            {
                Place(ox + 6, oy + y, BeltDir.South, 0);
            }
            for (int x = 8; x <= 10; x++)
            {
                Place(ox + x, oy + 4, BeltDir.East, 1);
            }
            for (int y = 4; y <= 6; y++)
            {
                Place(ox + 11, oy + y, BeltDir.North, 1);
            }
            for (int x = 11; x >= 9; x--)
            {
                Place(ox + x, oy + 7, BeltDir.West, 1);
            }
            for (int y = 7; y >= 5; y--)
            {
                Place(ox + 8, oy + y, BeltDir.South, 1);
            }
            BeltNetworkService.TryAddSource(s, 1, new GridCell(ox, oy + 1), 1, 1, BeltConst.Unlimited);
            BeltNetworkService.TryAddSource(s, 2, new GridCell(ox + 6, oy + 5), 2, 2, BeltConst.Unlimited);
            BeltNetworkService.TryAddSink(s, 3, new GridCell(ox + 12, oy + 1), 6, 8, "logistics.tier.t1");
            BeltKernel k = BeltNetworkService.Kernel;
            for (int x = 8; x <= 10; x++)
            {
                k.InsertItemAt(ox + x, oy + 4, 2000, (ushort)(10 + x));
                k.InsertItemAt(ox + x, oy + 4, 26000, (ushort)(20 + x));
            }
            if (fails.Count > 0)
            {
                failure = string.Join("；", fails);
            }
            return n;
        }

        private static void CheckWorldCadencePauseSpeed()
        {
            CampaignState s = NewWorld(515151);
            int placed = LayHomeNetwork(s, out GridCell origin, out string failure);
            BeltKernel k = BeltNetworkService.Kernel;
            long s0 = k.StepIndex;
            long t0 = GameClock.Ticks;
            WorldSimulation.StepMany(600);
            long steps = k.StepIndex - s0;
            k.TryGetPortInfo(1, out BeltPortInfo src10);
            WorldSimulation.StepMany(60 * 50);
            bool delivered = k.TryGetPortInfo(3, out BeltPortInfo sink) && sink.Total > 0;
            Expect(placed == 28 && failure == null && GameClock.StepHz == 60 && steps == 200 && GameClock.Ticks - t0 == 3600 && src10.Total > 0 && delivered,
                $"固定步长：经正式入口在家园铺 {placed} 格（原点 {origin}，按核心相对位置搜索空地）；世界跑 600 个 60 Hz 步（10 游戏秒）= 内核 {steps} 个 20 Hz 步，" +
                $"输出端口已推上 {src10.Total} 件；60 游戏秒时输入端口收下 {sink.Total} 件{(failure != null ? "；放置失败：" + failure : string.Empty)}");

            // 暂停：整个世界（含传送带）零步。
            long before = k.StepIndex;
            ulong hBefore = k.ComputeStateHash();
            GameClock.SetPaused(true);
            for (int i = 0; i < 30; i++)
            {
                FrameOnce(0.1f);
            }
            bool frozen = k.StepIndex == before && k.ComputeStateHash() == hBefore;
            GameClock.SetPaused(false);
            FrameOnce(0.1f);
            Expect(frozen && k.StepIndex > before, $"战略暂停：3 真实秒内传送带内核零步、状态不变；继续后照常推进（{k.StepIndex - before} 步）");

            // 倍速矩阵：同一存档，0.5x / 1x / 2x / 3x 跑同样 90 游戏秒，结果逐位一致。
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long baseTicks = GameClock.Ticks;
            long target = baseTicks + 60 * 90;
            var hashes = new List<ulong>();
            var reals = new List<string>();
            foreach (float speed in GameClock.Speeds)
            {
                RestoreSlot(Slot);
                GameClock.SetSpeed(speed);
                int frames = 0;
                while (GameClock.Ticks < target && frames < 100000)
                {
                    FrameOnce(1f / 60f, target);
                    frames++;
                }
                hashes.Add(BeltNetworkService.Kernel.ComputeStateHash());
                reals.Add($"{speed}x：{frames / 60f:F1} 真实秒");
                GameClock.SetSpeed(1f);
            }
            Expect(save.Success && hashes.Count == 4 && hashes.Distinct().Count() == 1 && GameClock.Ticks == target,
                $"倍速矩阵（B09）：0.5x / 1x / 2x / 3x 跑同样 90 游戏秒，传送带状态哈希完全一致；真实时间按倍速缩放（{string.Join("，", reals)}）");
            WorldSimulation.UnloadAll();
        }

        private static void RestoreSlot(int slot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            File.Copy(CampaignSaveService.SlotPath(slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
        }

        private static void CheckObservationIndependence()
        {
            CampaignState s = NewWorld(626262);
            LayHomeNetwork(s, out _, out _);
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long target = GameClock.Ticks + 60 * 120;

            // A：镜头在家园、逐帧驱动（每帧画传送带），中途缩放到远景又拉回。
            RestoreSlot(Slot);
            int frames = 0;
            while (GameClock.Ticks < target)
            {
                _reader.Scroll = frames % 400 < 6 ? -1f : frames % 400 >= 200 && frames % 400 < 206 ? 1f : 0f;
                FrameOnce(1f / 60f, target);
                frames++;
            }
            _reader.Scroll = 0f;
            ulong observed = BeltNetworkService.Kernel.ComputeStateHash();
            int renders = BeltNetworkService.RenderCalls;
            int switches = BeltNetworkService.Renderer?.ModeSwitches ?? 0;

            // B：无头推进（不经过镜头、输入与表现层）。
            RestoreSlot(Slot);
            int rendersBefore = BeltNetworkService.RenderCalls;
            WorldSimulation.StepMany((int)(target - GameClock.Ticks));
            ulong headless = BeltNetworkService.Kernel.ComputeStateHash();
            bool noRender = BeltNetworkService.RenderCalls == rendersBefore;

            // C：3x 倍速、镜头一直拉在远景（只画流动贴图，从不逐物品绘制）。
            RestoreSlot(Slot);
            GameClock.SetSpeed(3f);
            int cf = 0;
            while (GameClock.Ticks < target)
            {
                _reader.Scroll = cf < 8 ? -1f : 0f;
                FrameOnce(1f / 60f, target);
                cf++;
            }
            _reader.Scroll = 0f;
            GameClock.SetSpeed(1f);
            bool farWhole = BeltNetworkService.Renderer != null && BeltNetworkService.Renderer.FarMode;
            ulong away = BeltNetworkService.Kernel.ComputeStateHash();
            Expect(observed == headless && headless == away && renders > 100 && noRender && switches >= 2 && farWhole,
                $"观察不改变结果（FGR-BASE-021 / B24）：同一存档跑 120 游戏秒——A 镜头在家园逐帧绘制（{renders} 次，远近景切换 {switches} 次）、B 无头推进（零次绘制）、" +
                $"C 3x 且镜头一直在远景（只画流动贴图），三遍传送带状态哈希完全一致");
            WorldSimulation.UnloadAll();
        }

        private static void CheckGridRulesAndHooks()
        {
            PlayerPrefs.DeleteKey(SettingsPrefsKey);
            GameSettings.Load();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignState s = NewWorld(737373);
            bool hookBefore = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstBelt);
            FindFreeBox(s, 4, 3, out GridCell o);
            BeltOpResult first = BeltNetworkService.TryPlace(s, o, BeltDir.East, 0);
            bool hookAfter = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstBelt);
            HomeGridMap map = HomeGridService.MapFor(s);
            ushort layer = map.GetBelt(o);
            BeltOpResult dup = BeltNetworkService.TryPlace(s, o, BeltDir.North, 1);
            BeltOpResult badTier = BeltNetworkService.TryPlace(s, new GridCell(o.X + 1, o.Y), BeltDir.East, 5);

            // 建筑不能压在带上（格网既有规则读传送带层）。
            GridPlacementResult onBelt = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeGenerator, o, 0);
            // 带不能压在建筑上。
            BuildingRecord gen = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            BeltOpResult onBuilding = BeltNetworkService.TryPlace(s, new GridCell(gen.GridX, gen.GridY), BeltDir.East, 0);
            // 迷雾里不能放。
            GridCell core = HomeGridService.CorePivot(s);
            int explored = GridContent.TuningInt("grid.explored_radius_start");
            BeltOpResult fog = BeltNetworkService.TryPlace(s, new GridCell(core.X + explored + 20, core.Y), BeltDir.East, 0);
            // 核心外的保留通道：建筑不能放，传送带可以（否则核心输入端口接不上带）。
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            int ring = GridContent.TuningInt("grid.core_reserve_ring");
            GridCell channel = default;
            bool channelFound = false;
            for (int y = cmin.Y - ring; y <= cmax.Y + ring && !channelFound; y++)
            {
                for (int x = cmin.X - ring; x <= cmax.X + ring && !channelFound; x++)
                {
                    var c = new GridCell(x, y);
                    if (x >= cmin.X && x <= cmax.X && y >= cmin.Y && y <= cmax.Y)
                    {
                        continue;
                    }
                    if (HomeGridService.ValidateBeltCell(s, c).Ok && HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeGenerator, c, 0).Has(GridBlockReason.CoreReserve))
                    {
                        channel = c;
                        channelFound = true;
                    }
                }
            }
            bool buildingRefused = channelFound;
            GridPlacementResult channelBelt = HomeGridService.ValidateBeltCell(s, channel);
            var returned = new List<ushort>();
            BeltNetworkService.Kernel.InsertItem(o.X, o.Y, 5);
            BeltOpResult removed = BeltNetworkService.TryRemove(s, o, returned);
            ushort layerAfter = map.GetBelt(o);
            BeltOpResult notRunning;
            WorldSimulation.UnloadAll();
            notRunning = BeltNetworkService.TryPlace(s, o, BeltDir.East, 0);

            Expect(first.Ok && layer == 1 && !dup.Ok && dup.Grid != null && dup.Grid.Has(GridBlockReason.OccupiedBelt) && !badTier.Ok && badTier.Code == BeltResult.InvalidTier
                   && onBelt.Has(GridBlockReason.OccupiedBelt) && !onBuilding.Ok && onBuilding.Grid.Has(GridBlockReason.Occupied) && !fog.Ok && fog.Grid.Has(GridBlockReason.Fog)
                   && buildingRefused && channelBelt.Ok && removed.Ok && returned.Count == 1 && layerAfter == 0
                   && !notRunning.Ok && notRunning.ReasonKey == "logistics.reason.not_running",
                $"格网规则（B06 原因可解释）：放下后格网传送带层 = {layer}；重复放“{dup.Describe()}”；等级非法“{badTier.Describe()}”；建筑压带“{onBelt.Reasons.FirstOrDefault(x => x.Code == GridBlockReason.OccupiedBelt).Describe()}”；" +
                $"带压建筑“{onBuilding.Describe()}”；迷雾“{fog.Describe()}”；核心保留通道 {channel} 上建筑被拒（通道）、传送带可放；拆除交还 {returned.Count} 件、清掉格网层；家园未载入“{notRunning.Describe()}”");
            Expect(!hookBefore && hookAfter,
                "引导钩子：第一次放下传送带发出 logistics.belt.first_placed（清空已看过记录后验证）");

            // 第一次堵塞的钩子：末端没接东西的一段跑满。
            CampaignState s2 = NewWorld(737374);
            FindFreeBox(s2, 4, 3, out GridCell o2);
            BeltNetworkService.TryPlace(s2, o2, BeltDir.East, 0);
            BeltNetworkService.TryAddSource(s2, 1, o2, 3, 1, BeltConst.Unlimited);
            bool blockedBefore = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstBlocked);
            WorldSimulation.StepMany(60 * 10);
            Expect(!blockedBefore && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstBlocked) && BeltNetworkService.Kernel.BlockedCells == 1,
                "引导钩子：第一次出现堵塞（末端没接东西、带满）发出 logistics.belt.first_blocked");
            WorldSimulation.UnloadAll();
        }

        private static void CheckSeedIndependence()
        {
            var parts = new List<string>();
            bool ok = true;
            foreach (int seed in new[] { 11, 20250925, 987654321 })
            {
                CampaignState s = NewWorld(seed);
                int placed = LayHomeNetwork(s, out GridCell origin, out string failure);
                WorldSimulation.StepMany(60 * 70);
                BeltNetworkService.Kernel.TryGetPortInfo(3, out BeltPortInfo sink);
                GridCell core = HomeGridService.CorePivot(s);
                bool thisOk = placed == 28 && failure == null && sink.Total >= 10 && Invariants(BeltNetworkService.Kernel, out _);
                ok &= thisOk;
                parts.Add($"种子 {seed}：核心 {core}、铺设原点 {origin}（相对 {origin.X - core.X},{origin.Y - core.Y}）、70 秒收下 {sink.Total} 件");
                WorldSimulation.UnloadAll();
            }
            Expect(ok, "种子无关（B25）：铺设位置按“核心附近、每格都能放”的规则搜索，不写死坐标；三个种子下都能铺完并运转：" + string.Join("；", parts));
        }

        private static void CheckFullSaveHugeNetwork()
        {
            CampaignState s = NewWorld(848484);
            BeltKernel k = BeltNetworkService.Kernel;
            GridCell core = HomeGridService.CorePivot(s);
            // 超大网络直接经内核接口建在远处（格网校验在 F 段单独验证；这里验证存读档的规模与一致性）。
            int ox = core.X + 2000;
            int oy = core.Y + 2000;
            BuildHuge(k, ox, oy);
            WorldSimulation.StepMany(60 * 20);
            ulong hSave = k.ComputeStateHash();
            int cells = k.CellCount;
            int items = k.ItemCount;
            var sw = Stopwatch.StartNew();
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            sw.Stop();
            double saveMs = sw.Elapsed.TotalMilliseconds;
            long fileBytes = new FileInfo(CampaignSaveService.SlotPath(Slot)).Length;
            int records = s.Belts.Networks.Length;
            // 不存档的对照：接着跑 30 游戏秒。
            WorldSimulation.StepMany(60 * 30);
            ulong hContinue = BeltNetworkService.Kernel.ComputeStateHash();

            var lw = Stopwatch.StartNew();
            RestoreSlot(Slot);
            lw.Stop();
            BeltKernel r = BeltNetworkService.Kernel;
            ulong hLoaded = r.ComputeStateHash();
            int loadedCells = r.CellCount;
            int loadedItems = r.ItemCount;
            bool loadedInv = Invariants(r, out string why);
            HomeGridMap map = HomeGridService.MapFor(CampaignSession.Current);
            bool layer = map.GetBelt(new GridCell(ox + 75, oy + 40)) != 0 && map.GetBelt(new GridCell(ox + 200, oy)) != 0;
            // 换图（表 / 生成器变化、会话重建）后传送带层也要回来：强制重建格网再查。
            HomeGridService.Invalidate();
            HomeGridMap rebuilt = HomeGridService.MapFor(CampaignSession.Current);
            layer &= rebuilt.GetBelt(new GridCell(ox + 75, oy + 40)) != 0 && !ReferenceEquals(rebuilt, map);
            // 格网传送带层的重新套用是热更层逐格写（只在建图 / 读档时一次，不是每帧）：记下耗时作为装载期例外的依据（ADR-ARC-004）。
            double applyMs = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                var aw = Stopwatch.StartNew();
                BeltNetworkService.ApplyGridLayer(CampaignSession.Current, rebuilt);
                aw.Stop();
                applyMs = Math.Min(applyMs, aw.Elapsed.TotalMilliseconds);
            }
            PerfLines.Add($"格网传送带层重新套用（建图 / 读档时一次；热更层逐格写 {loadedCells:N0} 格）：{applyMs:F2} ms（Editor Mono，三次取最小；真机 HybridCLR 解释执行另测，FG15-SYS-02）");
            Expect(applyMs < 50.0,
                $"格网传送带层重新套用是装载期一次性开销（{loadedCells:N0} 格 {applyMs:F2} ms < 50 ms），不在每帧 / 每步路径上");
            WorldSimulation.StepMany(60 * 30);
            ulong hAfter = BeltNetworkService.Kernel.ComputeStateHash();
            PerfLines.Add($"超大网络经正式存档路径：{cells:N0} 格 / {items:N0} 件 → {records} 个网络块，存档文件 {fileBytes / 1024.0:F0} KB；写回 + 写盘 {saveMs:F0} ms，读档 + 恢复世界 {lw.Elapsed.TotalMilliseconds:F0} ms（Editor）");
            Expect(save.Success && records > 100 && hLoaded == hSave && loadedCells == cells && loadedItems == items && loadedInv,
                $"超大网络存读档（FG03 第 5 节“存档时传送带上满是物品”）：{cells:N0} 格 / {items:N0} 件经 SaveWithExport 写盘、Restore 读回，状态哈希逐位一致（保存 {records} 块，读回 {loadedCells:N0} 格 / {loadedItems:N0} 件）{(why != null ? "；" + why : string.Empty)}");
            Expect(layer && BeltNetworkService.GridLayerApplyCount > 0,
                "读档后格网传送带层由内核重新推导（派生缓存，建筑不能压在读回的带上；有带的区块不被回收）");
            Expect(hAfter == hContinue,
                "读档后接着跑 30 游戏秒，与不存档一直跑下去的对照逐位一致（端口节拍、汇入轮次、内核步序号都在存档里）");
            WorldSimulation.UnloadAll();
        }

        private static void CheckCorruptAndLegacySaves()
        {
            CampaignState s = NewWorld(959595);
            LayHomeNetwork(s, out GridCell origin, out _);
            BeltKernel k = BeltNetworkService.Kernel;
            // 再建两段独立的小网络，确保至少三个网络块。
            GridCell core = HomeGridService.CorePivot(s);
            AddLine(k, core.X + 500, core.Y + 500, 6, BeltDir.East, 0);
            AddLine(k, core.X + 500, core.Y + 510, 6, BeltDir.East, 0);
            k.InsertItemAt(core.X + 500, core.Y + 510, 100, 77);
            WorldSimulation.StepMany(120);
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            BeltItemState saved = s.Belts;
            int netCount = saved.Networks.Length;
            int cellsTotal = saved.CellCount;

            // 坏一块（改掉 payload 里的一个字节）：其余网络照常恢复，这一块丢弃并记账、发通知。
            int victim = Array.FindIndex(saved.Networks, n => n.Items > 0 && n.Cells == 6);
            LoadResult onDisk = CampaignSaveService.Load(Slot);
            CampaignState damaged = onDisk.State;
            byte[] bytes = Convert.FromBase64String(damaged.Belts.Networks[victim].Payload);
            bytes[bytes.Length / 2] ^= 0x5A;
            damaged.Belts.Networks[victim].Payload = Convert.ToBase64String(bytes);
            int historyBefore = NotificationCenter.History.Count;
            // 走存档服务重写外层校验和：只模拟“网络块里一个字节坏了”，不让整个存档因外层校验失败被拒。
            CampaignSaveService.Save(Slot, damaged, SaveReason.Manual);
            RestoreSlot(Slot);
            BeltKernel r = BeltNetworkService.Kernel;
            bool notified = NotificationCenter.History.Count > historyBefore
                            && NotificationCenter.History.Any(h => h.Type?.Id == "save_migrated" && h.Members.Any(m => m.DetailText.Contains("传送带")));
            Expect(onDisk.Success && victim >= 0 && r != null && r.CorruptChunksDropped == 1 && r.CellCount == cellsTotal - 6 && r.Ledger.Balanced && r.Ledger.Removed >= 1 && notified
                   && BeltNetworkService.LastLoadError == GameText.Get("logistics.load.reason.chunk_checksum"),
                $"读档负向——一个网络块损坏（校验和不符）：只丢这一块（{netCount} 块恢复 {netCount - 1} 块、{r?.CellCount} 格），其件数记入“移出”账本仍平衡，并发“读档变更”通知：{BeltNetworkService.LastLoadError}");

            // 输入端口所属建筑的名字随存档保存：读档后“下游 X 的输入已满”仍写建筑名；没登记名字的端口写“输入端口 #编号”（文本键，不硬编码）；
            // 建筑可以只改名字（同一端口号再登记会得到“已有同类端口”）。
            var namedSink = new BeltCellInfo { Block = BeltBlock.SinkFull, SinkPortId = 3 };
            var unnamedSink = new BeltCellInfo { Block = BeltBlock.SinkFull, SinkPortId = 4321 };
            string namedText = BeltNetworkService.DescribeBlock(namedSink);
            string unnamedText = BeltNetworkService.DescribeBlock(unnamedSink);
            bool savedName = saved.SinkNames != null && saved.SinkNames.Any(n => n.PortId == 3 && n.NameKey == "logistics.tier.t1");
            CampaignState restored = CampaignSession.Current;
            BeltOpResult rename = BeltNetworkService.TrySetSinkOwnerName(restored, 3, "logistics.tier.t2");
            string renamedText = BeltNetworkService.DescribeBlock(namedSink);
            BeltOpResult renameMissing = BeltNetworkService.TrySetSinkOwnerName(restored, 999, "logistics.tier.t2");
            Expect(savedName && namedText == GameText.Format("logistics.block.sink_full", GameText.Get("logistics.tier.t1"))
                   && unnamedText == GameText.Format("logistics.block.sink_full", GameText.Format("logistics.block.sink_unnamed", 4321))
                   && rename.Ok && renamedText.Contains(GameText.Get("logistics.tier.t2")) && !renameMissing.Ok && renameMissing.Code == BeltResult.PortNotFound,
                $"输入端口的建筑名随存档保存（{saved.SinkNames?.Length ?? 0} 条）：读档后堵塞原因“{namedText}”；未登记名字“{unnamedText}”；只改名字 → “{renamedText}”；改不存在的端口 → {renameMissing.Describe()}");

            // 不认识的格式版本：内核留空，存档原始数据不被下一次存档覆盖；通知文案不自相矛盾；这局编辑一律拒绝并给原因（不会铺了带却悄悄存不进去）。
            LoadResult again = CampaignSaveService.Load(Slot);
            CampaignState future = again.State;
            future.Belts.FormatVersion = 99;
            CampaignSaveService.Save(Slot, future, SaveReason.Manual);
            RestoreSlot(Slot);
            // 同类通知会合并进最近一条（聚合窗口内），按成员逐条找这一次读档发的那句。
            string expectedFuture = GameText.Format("logistics.load.unreadable", BeltNetworkService.LastLoadError, future.Belts.Networks.Length, future.Belts.ItemCount);
            string futureText = NotificationCenter.History.Where(h => h.Type?.Id == "save_migrated").SelectMany(h => h.Members)
                .Select(m => m.DetailText).LastOrDefault(t => t == expectedFuture);
            CampaignState live = CampaignSession.Current;
            BeltOpResult place = BeltNetworkService.TryPlace(live, new GridCell(core.X + 600, core.Y + 600), BeltDir.East, 0);
            BeltOpResult port = BeltNetworkService.TryAddSink(live, 88, new GridCell(core.X + 601, core.Y + 600), 3, 0, "logistics.tier.t1");
            WorldSimulation.SyncAllForSave();
            BeltItemState kept = CampaignSession.Current.Belts;
            Expect(BeltNetworkService.Kernel.CellCount == 0 && kept.FormatVersion == 99 && kept.Networks.Length == netCount && BeltNetworkService.LastLoadError != null
                   && BeltNetworkService.SavedDataPreserved && futureText != null && !futureText.Contains("损坏") && !futureText.Contains("丢弃")
                   && !place.Ok && place.ReasonKey == "logistics.reason.save_preserved" && !port.Ok && port.ReasonKey == "logistics.reason.save_preserved",
                $"读档负向——不认识的传送带格式版本：内核留空，通知“{futureText}”（不再说“损坏已丢弃”）；存档里的原始数据原样保留、不被下一次存档覆盖（{kept.Networks.Length} 块）；" +
                $"这局放带 / 登记端口都被拒绝并说明原因“{place.Describe()}”");

            // base64 本身坏掉（不是合法 base64）：同样只丢那一块。
            LoadResult third = CampaignSaveService.Load(Slot);
            CampaignState garbled = third.State;
            garbled.Belts.FormatVersion = 1;
            garbled.Belts.Networks[victim].Payload = "@@not-base64@@";
            CampaignSaveService.Save(Slot, garbled, SaveReason.Manual);
            RestoreSlot(Slot);
            Expect(BeltNetworkService.Kernel.CorruptChunksDropped == 1 && BeltNetworkService.Kernel.Ledger.Balanced,
                "读档负向——网络块不是合法 base64：当作坏块丢弃并记账，其余网络恢复");

            // 旧档（域版本 1 的空骨架）：读出来是空网络，不报错。
            var parsed = JsonUtility.FromJson<CampaignState>("{\"Belts\":{\"DomainVersion\":1}}");
            CampaignFgStateDomains.EnsureAll(parsed);
            Expect(parsed.Belts != null && parsed.Belts.FormatVersion == 0 && parsed.Belts.Networks != null && parsed.Belts.Networks.Length == 0 && parsed.Belts.Ports != null,
                "旧档迁移：FG0-ARCH-02 之前的传送带域（版本 1、没有数据字段）补成空网络，不报错");
            WorldSimulation.UnloadAll();
        }

        // ── G 渲染路径 / H 热更层 ────────────────────────────────────────────────

        private static void CheckRenderPathAndZoom()
        {
            CampaignState s = NewWorld(171717);
            int placed = LayHomeNetwork(s, out _, out _);
            WorldView.FocusOn("home");
            for (int i = 0; i < 30; i++)
            {
                FrameOnce(1f / 60f);
            }
            BeltRenderer r = BeltNetworkService.Renderer;
            Camera cam = WorldView.Camera;
            float ortho0 = cam != null ? cam.orthographicSize : -1f;
            bool nearOk = r != null && !r.FarMode && r.LastCellInstances == placed && r.LastItemInstances == BeltNetworkService.Kernel.ItemCount;
            int nearItems = r?.LastItemInstances ?? -1;
            // 真实滚轮输入（可重绑的“缩小”动作）拉远到远景。
            int frames = 0;
            while (cam != null && cam.orthographicSize < BeltNetworkService.RenderSettings.FlowOrthoEnter + 1f && frames < 120)
            {
                _reader.Scroll = -1f;
                FrameOnce(1f / 60f);
                frames++;
            }
            _reader.Scroll = 0f;
            FrameOnce(1f / 60f);
            bool farOk = r != null && r.FarMode && r.LastItemInstances == 0 && r.LastCellInstances == placed;
            float orthoFar = cam != null ? cam.orthographicSize : -1f;
            frames = 0;
            while (cam != null && cam.orthographicSize > BeltNetworkService.RenderSettings.FlowOrthoExit - 1f && frames < 120)
            {
                _reader.Scroll = 1f;
                FrameOnce(1f / 60f);
                frames++;
            }
            _reader.Scroll = 0f;
            FrameOnce(1f / 60f);
            bool backOk = r != null && !r.FarMode && r.LastItemInstances > 0;
            Expect(placed == 28 && nearOk && farOk && backOk,
                $"真实表现层路径（WorldPlanetView → BeltNetworkService.Render）：近景（正交 {ortho0:F1}）{placed} 格 + {nearItems} 件实例；" +
                $"滚轮拉远到 {orthoFar:F1} 切远景流动贴图（物品实例 0）；拉回近景恢复逐物品（{r?.LastItemInstances}）");
            float alphaOk = BeltNetworkService.InterpolationAlpha;
            Expect(alphaOk >= 0f && alphaOk <= 1f && r != null && (r.GpuAvailable || r.GpuUnavailableReason != null),
                $"插值比例 {alphaOk:F2} ∈ [0,1]（20 Hz 内核在 60 帧画面上按上一步位移插值）；GPU：{(r != null && r.GpuAvailable ? "可用" : r?.GpuUnavailableReason)}");
            WorldSimulation.UnloadAll();
        }

        private static void CheckHotLayerConstantCost()
        {
            var results = new List<(int cells, double renderUs, double stepUs, long alloc)>();
            foreach (bool huge in new[] { false, true })
            {
                CampaignState s = NewWorld(282828);
                LayHomeNetwork(s, out _, out _);
                if (huge)
                {
                    GridCell core = HomeGridService.CorePivot(s);
                    BuildHuge(BeltNetworkService.Kernel, core.X + 3000, core.Y + 3000);
                }
                BeltKernel k = BeltNetworkService.Kernel;
                k.EnsureTopology();
                Camera cam = WorldView.Camera;
                for (int i = 0; i < 10; i++)
                {
                    BeltNetworkService.Render(cam);
                }
                // 热更层每帧：内核状态未变时的 Render（只做常数次调用；缓冲不重填）。
                const int n = 2000;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++)
                {
                    BeltNetworkService.Render(cam);
                }
                sw.Stop();
                double renderUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
                // 热更层每个世界步：WorldStep 的托管开销（不含内核作业本身）= 总耗时 − 内核报告的作业耗时。
                long ticks = GameClock.Ticks;
                double kernelMs = 0;
                var sw2 = Stopwatch.StartNew();
                for (int i = 0; i < 300; i++)
                {
                    BeltNetworkService.WorldStep(s, ticks + i, GameClock.StepHz);
                    if ((ticks + i + 1) * 20 / 60 > (ticks + i) * 20 / 60)
                    {
                        kernelMs += k.LastStepMs;
                    }
                }
                sw2.Stop();
                double stepUs = (sw2.Elapsed.TotalMilliseconds - kernelMs) * 1000.0 / 300;
                // 托管分配：3,000 次“步 + 画”的托管堆增量（Unity Mono 不实现按线程分配计数，用托管堆已用字节，容差按每次 < 8 字节）。
                GC.Collect();
                long before = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                for (int i = 0; i < 3000; i++)
                {
                    BeltNetworkService.WorldStep(s, ticks + 300 + i, GameClock.StepHz);
                    BeltNetworkService.Render(cam);
                }
                long alloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - before;
                results.Add((k.CellCount, renderUs, stepUs, alloc));
                WorldSimulation.UnloadAll();
            }
            (int cells, double renderUs, double stepUs, long alloc) small = results[0];
            (int cells, double renderUs, double stepUs, long alloc) big = results[1];
            PerfLines.Add($"热更层每帧 Render（状态未变）：{small.cells} 格 {small.renderUs:F2} µs / {big.cells:N0} 格 {big.renderUs:F2} µs；每世界步 WorldStep 托管开销 " +
                          $"{small.stepUs:F2} µs / {big.stepUs:F2} µs；3,000 次“步 + 画”托管堆增量 {small.alloc} / {big.alloc} 字节");
            Expect(big.renderUs < small.renderUs + 20.0 && big.stepUs < small.stepUs + 20.0 && small.alloc < 3000 * 8 && big.alloc < 3000 * 8,
                $"热更层开销与数量无关（FGR-SYS-042）：{small.cells} 格与 {big.cells:N0} 格下，每帧 Render {small.renderUs:F2} / {big.renderUs:F2} µs、每步 {small.stepUs:F2} / {big.stepUs:F2} µs（差值 < 20 µs）；" +
                $"稳态托管分配 ≈ 0（{small.alloc} / {big.alloc} 字节 / 3,000 次）");
        }

        // ── 公共 ─────────────────────────────────────────────────────────────────

        private static void FrameOnce(float realDt, long tickLimit = long.MaxValue)
        {
            if (!ReferenceEquals(InputRouter.Reader, _reader))
            {
                InputRouter.DebugSetReader(_reader);
            }
            WorldSimulation.Frame(realDt, tickLimit);
            _reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
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
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch
                {
                    // 已经记失败。
                }
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
            _report.AppendLine(text);
        }

        private sealed class FakeReader : IInputReader
        {
            public float Scroll;

            public void EndFrame()
            {
            }

            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            // 光标在窗口外：不触发镜头的屏幕边缘推屏。
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => Scroll;
        }
    }
}
