using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
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
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-05 管线与流体的自动验收（FG03 FGR-LOG-040～045；FGT-LOG-007“网络混合被拒绝、按优先级分配、储罐缓冲”——结冰与解冻由 FG7-ENV-03 接入，本 Story 验计时接口；
    /// 卡片负向“连接两种流体（拒绝）”“网络供给为 0”；必须同时交付“按流体上色并有图标”“悬停显示供给、需求、储量、瓶颈”“冲洗需要确认”）。
    /// 全部起真实系统：真实 AOT 内核（PipeKernel）、真实世界模拟与家园（WorldSimulation.LoadHome：建造模式、虚影施工、机器取料都在世界步里跑）、真实存读档文件、真 UXML 面板。
    /// 内核（K）
    /// K1 一网一种流体（接错流体拒绝、空网络接入后认定流体、阀门两侧不同拒绝）；K2 泵按固定速率（1 分钟恰好 300 升）、没人要不抽、配额精确；K3 吞吐 = 最低等级（混接 T1 限 600，
    /// 瓶颈管段定位，全 T2 限 1,200）；K4 按优先级分配（1 先满足、同级按比例、改优先级立刻生效、只进储罐按优先级）；K5 储罐缓冲（存余量、缺时放出、放空后缺口、容量上限、只进 / 只出）；
    /// K6 阀门单向（正向通、反向不通、关闭不通、调头、一步延迟的缓冲）；K7 冲洗（清空存量与流体、之后能接另一种流体、有泵时重新充入、阀门缓冲一并清空）；
    /// K8 负向“网络供给为 0”（根因：没有来源 / 没有流体）；K9 拓扑变化才重算（稳态零重算、零分配）；K10 确定性与存档（快照往返、接着跑逐位一致、坏记录丢弃、不认识的格式拒绝）；
    /// K11 结冰计时接口（静止计时、流动归零、拆分继承、存档保留）；K12 渲染实例（流体颜色编号、图标、阀门关闭 / 两侧不同标志、液位）；K13 性能（3,000 格管线单步与重算）。
    /// 正式（F）
    /// F1 数据（调参、菜单五个工具、流体表与源数据逐字段、文本、图鉴、钩子）；F2 建造菜单真实输入（三个种子：泵放在水源上、拖管线、放储罐、机器施工、泵不在水源上的原因、阀门箭头）；
    /// F3 家园里接错流体（预览标红写明两种流体、不放；建成时才冲突 → 通知、作废、材料退回）；F4 悬停读数（供给、需求、储量、瓶颈、根因）；F5 管线面板（真 UXML：储罐模式 / 优先级、阀门开关 / 调头、
    /// 冲洗先确认：取消不变、确认清空）与布局探针；F6 拆除（有存量的储罐先确认、全额返还、框选含管线）；F7 真文件存读档（接着跑一致、旧档、不认识的格式保留）；F8 暂停与 0.5x～3x；
    /// F9 观察 / 不观察一致；F10 结冰计时接口（游戏小时、寒潮开关进存档、本 Story 不阻断流动）；F11 120 帧节奏性能与稳态分配。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgPipeSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static int W;
        private static int C;

        [MenuItem("BinGames/QA/自检/FG 管线与流体")]
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
            Line("\n[管线与流体] 一网一种流体 / 泵 / 最低等级限流 / 优先级 / 储罐与阀门 / 冲洗 / 结冰计时接口（FG3-LOG-05）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpipe-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                W = PipeNetworkService.FluidId("water");
                C = PipeNetworkService.FluidId("crude");
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"图形设备 {SystemInfo.graphicsDeviceType}；管线内核是 AOT 程序集里的托管代码（Editor 下 Mono JIT，真机 IL2CPP AOT），热更层在真机走 HybridCLR 解释执行，真机另测（FG3-LOG-09 / FG15-SYS-02）");

                Step(CheckFluidRule);
                Step(CheckPump);
                Step(CheckMinTier);
                Step(CheckPriority);
                Step(CheckTank);
                Step(CheckValve);
                Step(CheckFlush);
                Step(CheckNoSupply);
                Step(CheckTopologyOnly);
                Step(CheckDeterminismAndSnapshot);
                Step(CheckIdleClock);
                Step(CheckRenderInstances);
                Step(CheckKernelPerformance);
                Step(CheckData);
                Step(CheckBuildMode);
                Step(CheckHomeConflict);
                Step(CheckHover);
                Step(CheckPanel);
                Step(CheckRemove);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckFreezeInterface);
                Step(CheckPerformance120);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"管线自检抛异常：{e}");
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
                StrategyClock.Reset();
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
                HomeValleyWorkOrders.ResetSessionState();
                PipePanelUIToolkit.InWorldOverrideForTests = false;
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
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
                GameSettings.SetLanguage(originalLanguage);
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
            Line($"  · [管线与流体] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 内核工具 ───────────────────────────────────────────────────────────

        private static PipeKernel NewKernel() => new PipeKernel(PipeNetworkService.ReadConfig());

        private static int Minute(PipeKernel k) => 60 * k.Config.StepHz;

        private static void Pipes(PipeKernel k, int x0, int y, int len, int tier = 0)
        {
            for (int i = 0; i < len; i++)
            {
                k.Place(x0 + i, y, PipePieceKind.Pipe, tier, 0, 0);
            }
        }

        private static void Run(PipeKernel k, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                k.Step();
            }
        }

        private static PipeNetInfo Net(PipeKernel k, int x, int y)
        {
            k.TryGetNetworkInfo(k.NetworkAt(x, y), out PipeNetInfo n);
            return n;
        }

        private static PipeCellInfo Cell(PipeKernel k, int x, int y)
        {
            k.TryGetCellInfo(x, y, out PipeCellInfo c);
            return c;
        }

        private static long Got(PipeKernel k, int id)
        {
            k.TryGetConsumer(id, out PipeConsumerInfo c);
            return c.TotalDeliveredMl;
        }

        // ── K1 一网一种流体 ─────────────────────────────────────────────────────────

        private static void CheckFluidRule()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 3);
            k.Place(10, 0, PipePieceKind.Pump, 0, 0, C);
            Pipes(k, 7, 0, 3);
            Run(k, 2);
            bool twoFluids = Net(k, 2, 0).Fluid == W && Net(k, 8, 0).Fluid == C && Cell(k, 3, 0).Fluid == W && Cell(k, 7, 0).Fluid == C;
            PipeResult r4 = k.Place(4, 0, PipePieceKind.Pipe, 0, 0, 0);
            PipeResult r5 = k.Place(5, 0, PipePieceKind.Pipe, 0, 0, 0);
            PipeResult r6 = k.Place(6, 0, PipePieceKind.Pipe, 0, 0, 0);
            k.CheckPlace(6, 0, PipePieceKind.Pipe, 0, 0, 0, out int fa, out int fb);
            PipeResult valve = k.Place(6, 0, PipePieceKind.Valve, 0, 1, 0);
            bool refused = r4 == PipeResult.Ok && r5 == PipeResult.Ok && r6 == PipeResult.FluidConflict && fa + fb == W + C && fa != fb && valve == PipeResult.FluidConflict
                           && !k.HasCell(6, 0) && k.NetworkCount == 2;
            Expect(twoFluids && refused,
                $"K1 一网一种流体（FGR-LOG-040）：水网络与原油网络各自认定；往中间补管线，第三格会把两者接在一起 → {r6}（{fa}/{fb}），阀门也被拒 → {valve}；被拒的格不进内核，仍是 2 个网络");
            // 负向：原油泵贴着水网络
            PipeResult oilPump = k.Place(1, 1, PipePieceKind.Pump, 0, 0, C);
            // 空网络接入后认定流体
            for (int y = 2; y <= 5; y++)
            {
                k.Place(1, y, PipePieceKind.Pipe, 0, 0, 0);
            }
            Run(k, 1);
            bool empty = (Net(k, 1, 4).Issues & PipeNetIssue.Empty) != 0 && Net(k, 1, 4).Fluid == 0;
            PipeResult join = k.Place(1, 1, PipePieceKind.Pipe, 1, 0, 0);
            Run(k, 1);
            bool adopted = join == PipeResult.Ok && Net(k, 1, 5).Fluid == W && Cell(k, 1, 5).Fluid == W && Net(k, 1, 5).Id == Net(k, 1, 0).Id;
            Expect(oilPump == PipeResult.FluidConflict && empty && adopted,
                $"K1 负向：原油泵贴着水网络放 → {oilPump}；一段还没有流体的管线（状态“网络还没有流体”）接上水网络后并进同一网络、每格都记成水");
        }

        // ── K2 泵 ───────────────────────────────────────────────────────────────

        private static void CheckPump()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 3, 1);
            int id = k.AddConsumer(3, 0, W, 1000, 1);
            Run(k, Minute(k));
            long got = Got(k, id);
            long pumped = Cell(k, 0, 0).PumpTotalMl;
            bool rate = got == 300_000 && pumped == 300_000 && k.TotalPumpedMl == 300_000;
            k.RemoveConsumer(id);
            Run(k, 200);
            bool idle = Cell(k, 0, 0).PumpTotalMl == pumped && (Net(k, 1, 0).Issues & PipeNetIssue.NoDemand) != 0 && Cell(k, 0, 0).PumpedMl == 0;
            long sum7 = 0;
            for (int s = 0; s < Minute(k); s++)
            {
                sum7 += k.Budget(7, s);
            }
            bool exact = sum7 == 7000 && k.Budget(300, 12345) == 250 && k.Budget(600, 7) == 500;
            Expect(rate && idle && exact,
                $"K2 泵按固定速率抽取（FGR-LOG-041）：下游要 1,000 升/分钟时 1 分钟恰好送到 {got / 1000.0} 升（泵累计 {pumped / 1000.0}）；没人要时泵待命、不再抽；" +
                $"配额按步序号取整差分，7 升/分钟 1 分钟累计 {sum7} 毫升（精确），300 升/分钟每步 250 毫升");
        }

        // ── K3 吞吐 = 最低等级 ───────────────────────────────────────────────────────

        private static void CheckMinTier()
        {
            PipeKernel k = NewKernel();
            for (int x = 0; x < 3; x++)
            {
                k.Place(x, -1, PipePieceKind.Pump, 0, 0, W);
            }
            for (int x = 0; x < 10; x++)
            {
                k.Place(x, 0, PipePieceKind.Pipe, x == 5 ? 0 : 1, 0, 0);
            }
            int id = k.AddConsumer(9, 0, W, 5000, 1);
            Run(k, Minute(k));
            long a = Got(k, id);
            PipeNetInfo n = Net(k, 9, 0);
            bool limited = a == 600_000 && n.Mixed && n.MinTier == 0 && n.MinTierCells == 1 && n.BottleneckX == 5 && n.BottleneckY == 0 && (n.Issues & PipeNetIssue.PipeLimited) != 0
                           && Math.Abs(n.CapLpm - 600) < 0.01;
            k.Remove(5, 0, out _);
            k.Place(5, 0, PipePieceKind.Pipe, 1, 0, 0);
            Run(k, Minute(k));
            long b = Got(k, id) - a;
            PipeNetInfo n2 = Net(k, 9, 0);
            bool upgraded = b == 900_000 && !n2.Mixed && n2.MinTier == 1 && (n2.Issues & PipeNetIssue.PipeLimited) == 0;
            for (int x = 3; x < 6; x++)
            {
                k.Place(x, -1, PipePieceKind.Pump, 0, 0, W);
            }
            Run(k, Minute(k));
            long c = Got(k, id) - a - b;
            PipeNetInfo n3 = Net(k, 9, 0);
            bool t2Cap = c == 1_200_000 && (n3.Issues & PipeNetIssue.PipeLimited) != 0 && Math.Abs(n3.CapLpm - 1200) < 0.01;
            Expect(limited, $"K3 吞吐 = 最低等级管线（FGR-LOG-042）：3 台泵（900 升/分钟）经 9 格 T2 + 1 格 T1 送到 {a / 1000.0} 升/分钟（T1 上限 600），瓶颈管段 ({n.BottleneckX},{n.BottleneckY}) 共 {n.MinTierCells} 格");
            Expect(upgraded && t2Cap, $"K3 把那一格换成 T2：送到 {b / 1000.0} 升/分钟（泵的上限，不再被管线卡）；6 台泵（1,800）时全 T2 网络卡在 {c / 1000.0} 升/分钟并标“被管线等级卡住”");
        }

        // ── K4 优先级 ──────────────────────────────────────────────────────────────

        private static void CheckPriority()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 4, 1);
            int a = k.AddConsumer(2, 0, W, 200, 1);
            int b = k.AddConsumer(3, 0, W, 200, 2);
            Run(k, Minute(k));
            long ga = Got(k, a), gb = Got(k, b);
            bool p1 = ga == 200_000 && gb == 100_000 && (Net(k, 2, 0).Issues & PipeNetIssue.Shortage) != 0 && Math.Abs(Net(k, 2, 0).UnmetLpm - 100) < 1.5;
            int c = k.AddConsumer(4, 0, W, 100, 2);
            Run(k, Minute(k));
            long ga2 = Got(k, a) - ga, gb2 = Got(k, b) - gb, gc2 = Got(k, c);
            bool share = ga2 == 200_000 && gb2 + gc2 == 100_000 && Math.Abs(gb2 - 2 * gc2) <= 2 * Minute(k);
            k.SetConsumer(a, 200, 4);
            Run(k, Minute(k));
            long ga3 = Got(k, a) - ga - ga2, gb3 = Got(k, b) - gb - gb2, gc3 = Got(k, c) - gc2;
            // 优先级 2 的两家每步合计要 249～251 毫升（配额取整），泵 250：偶尔剩 1 毫升给优先级 4 的第一家（1 分钟 < 1.2 升），其余全给优先级 2。
            bool reorder = ga3 <= Minute(k) && ga3 + gb3 + gc3 == 300_000 && gb3 >= 199_000 && gc3 >= 99_000;
            Expect(p1 && share && reorder,
                $"K4 按优先级分配（FGR-LOG-042，沿用电网 1～4）：泵 300 升/分钟，优先级 1 的 200 全拿、优先级 2 的拿剩下 {gb / 1000.0}；同一优先级两家按需求比例分（{gb2 / 1000.0} : {gc2 / 1000.0} ≈ 2:1，总和 100）；" +
                $"把第一家改成优先级 4 立刻生效（它拿 {ga3 / 1000.0}，另两家拿满 {gb3 / 1000.0} / {gc3 / 1000.0}）");
            // 只进储罐按优先级（1）先于优先级 2 的消费者
            PipeKernel t = NewKernel();
            t.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(t, 1, 0, 3, 1);
            t.Place(4, 0, PipePieceKind.Tank, 0, 0, 0);
            t.SetTankMode(4, 0, PipeTankMode.InOnly);
            t.SetTankPriority(4, 0, 1);
            int d = t.AddConsumer(2, 0, W, 200, 2);
            Run(t, Minute(t));
            bool tankFirst = Cell(t, 4, 0).TankStockMl == 300_000 && Got(t, d) == 0;
            t.SetTankPriority(4, 0, 3);
            Run(t, Minute(t));
            bool tankLater = Got(t, d) == 200_000 && Cell(t, 4, 0).TankStockMl == 400_000;
            Expect(tankFirst && tankLater && t.SetTankPriority(4, 0, 5) == PipeResult.InvalidPriority,
                $"K4 只进储罐按自己的优先级灌：优先级 1 时 1 分钟灌进 {Cell(t, 4, 0).TankStockMl / 1000.0 - 100} 升、优先级 2 的消费者拿 0；改成 3 后消费者先拿满 200、储罐只收余量 100；优先级 5 被拒");
        }

        // ── K5 储罐缓冲 ─────────────────────────────────────────────────────────────

        private static void CheckTank()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 3, 1);
            k.Place(4, 0, PipePieceKind.Tank, 0, 0, 0);
            int id = k.AddConsumer(2, 0, W, 200, 1);
            Run(k, 3 * Minute(k));
            bool stored = Cell(k, 4, 0).TankStockMl == 300_000 && Got(k, id) == 600_000 && Math.Abs(Net(k, 2, 0).BufferedLpm - 100) < 1.5;
            k.SetConsumer(id, 500, 1);
            long g0 = Got(k, id);
            Run(k, Minute(k));
            long g1 = Got(k, id) - g0;
            bool released = g1 == 500_000 && Cell(k, 4, 0).TankStockMl == 100_000;
            Run(k, Minute(k));
            long g2 = Got(k, id) - g0 - g1;
            bool drained = g2 == 400_000 && Cell(k, 4, 0).TankStockMl == 0 && (Net(k, 2, 0).Issues & PipeNetIssue.Shortage) != 0;
            Expect(stored && released && drained,
                $"K5 储罐缓冲（FGR-LOG-043 双向）：供 300 用 200 时余量 100 升/分钟存进储罐（3 分钟 {300} 升）；需求涨到 500 时泵 + 储罐一起送（1 分钟 {g1 / 1000.0} 升，储罐剩 100）；" +
                $"储罐放空后只剩泵的 300，下一分钟送到 {g2 / 1000.0} 升并标“供给不足”");
            // 容量上限、没人要时泵停
            PipeKernel f = NewKernel();
            f.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(f, 1, 0, 2);
            f.Place(3, 0, PipePieceKind.Tank, 0, 0, 0);
            Run(f, 20 * Minute(f));
            bool full = Cell(f, 3, 0).TankStockMl == 5_000_000 && Cell(f, 0, 0).PumpTotalMl == 5_000_000 && (Net(f, 1, 0).Issues & PipeNetIssue.NoDemand) != 0;
            // 只出：不再进；有需求时放出
            f.SetTankMode(3, 0, PipeTankMode.OutOnly);
            f.Remove(0, 0, out _);
            int use = f.AddConsumer(1, 0, W, 600, 1);
            Run(f, Minute(f));
            bool outOnly = Got(f, use) == 600_000 && Cell(f, 3, 0).TankStockMl == 4_400_000;
            f.SetTankMode(3, 0, PipeTankMode.InOnly);
            Run(f, 10);
            bool inOnlyNoRelease = Got(f, use) == 600_000 && (Net(f, 1, 0).Issues & PipeNetIssue.NoSupply) != 0;
            Expect(full && outOnly && inOnlyNoRelease,
                $"K5 储罐容量 5,000 升：只有泵和储罐时 20 分钟灌满即止（泵累计 {Cell(f, 0, 0).PumpTotalMl / 1000.0} 升后待命）；“只出”在没有泵时照样往外放（1 分钟 600 升）；“只进”不往外放（消费者拿不到，标“有需求却没有供给”）");
        }

        // ── K6 阀门 ─────────────────────────────────────────────────────────────────

        private static void CheckValve()
        {
            PipeKernel k = NewKernel();
            k.Place(0, -1, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 0, 0, 3, 1);
            k.Place(3, 0, PipePieceKind.Valve, 0, 1, 0); // 朝东：西 → 东
            Pipes(k, 4, 0, 3, 1);
            int east = k.AddConsumer(6, 0, W, 1000, 1);
            Run(k, Minute(k));
            long ge = Got(k, east);
            PipeCellInfo v = Cell(k, 3, 0);
            bool forward = ge >= 299_000 && ge <= 300_000 && v.ValveFrom == Net(k, 1, 0).Id && v.ValveTo == Net(k, 5, 0).Id && Net(k, 5, 0).Fluid == W;
            k.SetValveOpen(3, 0, false);
            Run(k, 10);
            long ge2 = Got(k, east);
            Run(k, Minute(k));
            bool closed = Got(k, east) == ge2 && !Cell(k, 3, 0).ValveOpen;
            k.SetValveOpen(3, 0, true);
            // 反向：消费者在上游一侧，泵搬到下游一侧——阀门不让倒流。
            PipeKernel r = NewKernel();
            Pipes(r, 0, 0, 3, 1);
            r.Place(3, 0, PipePieceKind.Valve, 0, 1, 0);
            Pipes(r, 4, 0, 3, 1);
            r.Place(6, -1, PipePieceKind.Pump, 0, 0, W);
            int west = r.AddConsumer(0, 0, W, 1000, 1);
            Run(r, Minute(r));
            bool noBackflow = Got(r, west) == 0 && (Net(r, 0, 0).Issues & PipeNetIssue.NoSupply) != 0;
            PipeResult rev = r.ReverseValve(3, 0);
            Run(r, Minute(r));
            long gw = Got(r, west);
            bool reversed = rev == PipeResult.Ok && Cell(r, 3, 0).Dir == 3 && gw >= 299_000 && gw <= 300_000;
            Expect(forward && closed && noBackflow && reversed,
                $"K6 阀门单向（FGR-LOG-043）：西 → 东 1 分钟送到 {ge / 1000.0} 升（缓冲带来一步延迟），下游网络认定为水；关闭后 1 分钟一滴不过；泵在前方、消费者在后方时不倒流（0，“有需求却没有供给”）；" +
                $"调头后反向送到 {gw / 1000.0} 升");
            // 满载（修复轮 P1）：6 台泵（1,800 升/分钟）、两侧 T2、下游要 1,200 → 阀门按配置的 1,200 升/分钟放行，不能只有一半；
            // 下游网络根因与阀门流量每步稳定（不在“有需求却没有供给”与正常之间来回跳）。两种铺设顺序（上游网络编号在前 / 在后）都要成立。
            for (int order = 0; order < 2; order++)
            {
                PipeKernel f = NewKernel();
                int valveLpm = f.Config.ValveLitersPerMinute;
                void Up()
                {
                    for (int x = 0; x < 6; x++)
                    {
                        f.Place(x, -1, PipePieceKind.Pump, 0, 0, W);
                    }
                    Pipes(f, 0, 0, 6, 1);
                }
                void Down() => Pipes(f, 7, 0, 4, 1);
                if (order == 0)
                {
                    Up();
                    Down();
                }
                else
                {
                    Down();
                    Up();
                }
                f.Place(6, 0, PipePieceKind.Valve, 0, 1, 0);
                int sink = f.AddConsumer(10, 0, W, 1200, 1);
                Run(f, Minute(f));
                long full = Got(f, sink);
                int flicker = 0;
                long flowMin = long.MaxValue, flowMax = 0;
                for (int i = 0; i < 40; i++)
                {
                    f.Step();
                    PipeNetInfo dn = Net(f, 8, 0);
                    if ((dn.Issues & (PipeNetIssue.NoSupply | PipeNetIssue.Shortage)) != 0 || f.LastNoSupplyNetworks != 0)
                    {
                        flicker++;
                    }
                    long vf = Cell(f, 6, 0).ValveFlowMl;
                    flowMin = Math.Min(flowMin, vf);
                    flowMax = Math.Max(flowMax, vf);
                }
                long expect = Math.Min(valveLpm, 1200) * 1000L;
                long perStep = f.Budget(valveLpm, 0);
                Expect(full >= expect - 2 * perStep && full <= expect && flicker == 0 && flowMin >= perStep - 1 && flowMax <= perStep + 1,
                    $"K6 阀门满载（铺设顺序 {(order == 0 ? "上游在前" : "下游在前")}）：6 台泵 + 两侧 T2、下游要 1,200 升/分钟，1 分钟经阀门送到 {full / 1000.0} 升（配置 {valveLpm}，只差首步缓冲延迟）；" +
                    $"之后 40 步下游根因出现“没有供给 / 不足”{flicker} 次、阀门每步流量 {flowMin}～{flowMax} 毫升（稳定）");
            }
            // 负向（修复轮 P2）：阀门不能前后直接串联；在阀门开口面接管线也要过两侧流体检查。
            PipeKernel c = NewKernel();
            c.Place(0, -1, PipePieceKind.Pump, 0, 0, W);
            Pipes(c, 0, 0, 3, 1);
            c.Place(3, 0, PipePieceKind.Valve, 0, 1, 0);
            c.Place(8, -1, PipePieceKind.Pump, 0, 0, C);
            Pipes(c, 5, 0, 4, 1);
            Run(c, 2);
            PipeResult chained = c.Place(4, 0, PipePieceKind.Valve, 0, 1, 0);
            PipeResult chainedBack = c.Place(2, 1, PipePieceKind.Valve, 0, 0, 0); // 另一个不与阀门对接的阀门：允许
            PipeResult viaValve = c.CheckPlace(4, 0, PipePieceKind.Pipe, 0, 0, 0, out int va, out int vb);
            var planFluids = new List<int>();
            c.AddAdjacentFluids(4, 0, PipePieceKind.Pipe, 0, planFluids);
            Expect(chained == PipeResult.ValveChained && !c.HasCell(4, 0) && chainedBack == PipeResult.Ok && viaValve == PipeResult.FluidConflict && va + vb == W + C
                   && planFluids.Count == 2,
                $"K6 负向：阀门前方直接再接阀门被拒（{chained}，中间至少隔一格管线）；不对接的阀门照常（{chainedBack}）；阀门后方是水、前方隔一格是原油时，往阀门前方补管线 → {viaValve}（{va}/{vb}），" +
                $"规划预览也看到两种流体（{planFluids.Count}）");
        }

        // ── K7 冲洗 ─────────────────────────────────────────────────────────────────

        private static void CheckFlush()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 3);
            k.Place(4, 0, PipePieceKind.Tank, 0, 0, 0);
            k.Place(5, 0, PipePieceKind.Valve, 0, 1, 0);
            Pipes(k, 6, 0, 2);
            Run(k, Minute(k));
            long stock = Cell(k, 4, 0).TankStockMl;
            k.Remove(0, 0, out _); // 拆掉泵：网络里只剩存量
            PipeResult valveFlush = k.Flush(5, 0, out _);
            PipeResult fr = k.Flush(2, 0, out long flushed);
            bool cleared = fr == PipeResult.Ok && flushed >= stock && Cell(k, 4, 0).TankStockMl == 0 && Net(k, 2, 0).Fluid == 0 && Cell(k, 1, 0).Fluid == 0
                           && Cell(k, 5, 0).ValveBufferMl == 0 && valveFlush == PipeResult.WrongKind && k.TotalFlushedMl == flushed;
            PipeResult oil = k.Place(0, 0, PipePieceKind.Pump, 0, 0, C);
            Run(k, 2);
            bool switched = oil == PipeResult.Ok && Net(k, 2, 0).Fluid == C;
            k.Flush(2, 0, out _);
            Run(k, 1);
            bool refilled = Net(k, 2, 0).Fluid == C;
            Expect(cleared && switched && refilled,
                $"K7 冲洗（FGR-LOG-044）：清掉 {flushed / 1000.0} 升（储罐 {stock / 1000.0} 升 + 阀门缓冲），网络回到“没有流体”；阀门本身不能冲洗（{valveFlush}）；冲洗后能接上原油泵、网络改为原油；" +
                "网络里还有泵时冲洗后下一步重新充入");
            // 负向（修复轮 P2）：冲洗有泵的网络后、下一步之前，网络立即按泵重新认定流体；这段时间里把它接到另一种流体的网络照样被拒，
            // 不会合并后把另一种流体静默改名。
            PipeKernel g = NewKernel();
            g.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(g, 1, 0, 3);
            g.Place(7, 0, PipePieceKind.Pump, 0, 0, C);
            Pipes(g, 5, 0, 2);
            g.Place(5, 1, PipePieceKind.Tank, 0, 0, 0);
            Run(g, Minute(g));
            long oilStock = Cell(g, 5, 1).TankStockMl;
            g.Flush(2, 0, out _);
            bool keptFluid = Net(g, 2, 0).Fluid == W && Cell(g, 1, 0).Fluid == W;
            PipeResult bridge = g.Place(4, 0, PipePieceKind.Pipe, 0, 0, 0);
            Run(g, 1);
            bool untouched = Net(g, 6, 0).Fluid == C && Cell(g, 5, 1).TankStockMl >= oilStock && g.FluidConflictsResolved == 0 && !g.HasCell(4, 0);
            Expect(keptFluid && bridge == PipeResult.FluidConflict && untouched,
                $"K7 负向：冲洗有泵的水网络后立即仍是水（不留“没有流体”的窗口）；此时往水网络与原油网络之间补管线 → {bridge}；原油网络与储罐里的 {oilStock / 1000.0} 升原油不被改名（统一流体次数 {g.FluidConflictsResolved}）");
        }

        // ── K8 网络供给为 0 ──────────────────────────────────────────────────────────

        private static void CheckNoSupply()
        {
            PipeKernel k = NewKernel();
            Pipes(k, 0, 0, 4);
            int id = k.AddConsumer(2, 0, W, 300, 1);
            Run(k, 50);
            PipeNetInfo n = Net(k, 0, 0);
            bool empty = Got(k, id) == 0 && k.LastNoSupplyNetworks == 1 && (n.Issues & PipeNetIssue.NoSupply) != 0 && (n.Issues & PipeNetIssue.NoSource) != 0
                         && (n.Issues & PipeNetIssue.Empty) != 0 && Math.Abs(n.DemandLpm - 300) < 0.01 && Math.Abs(n.UnmetLpm - 300) < 0.01;
            k.Place(0, -1, PipePieceKind.Pump, 0, 0, W);
            Run(k, Minute(k));
            k.Remove(0, -1, out _);
            long g = Got(k, id);
            Run(k, 50);
            PipeNetInfo n2 = Net(k, 0, 0);
            bool dry = Got(k, id) == g && n2.Fluid == W && (n2.Issues & PipeNetIssue.NoSupply) != 0 && (n2.Issues & PipeNetIssue.NoSource) != 0 && (n2.Issues & PipeNetIssue.Empty) == 0;
            Expect(empty && dry && g == 300_000,
                $"K8 负向“网络供给为 0”：没有泵的网络，消费者拿 0，根因“没有来源 + 没有流体”，需求 300 / 缺口 300 升/分钟、内核计数 {k.LastNoSupplyNetworks}；接上泵后 1 分钟送到 300 升；" +
                "拆掉泵后网络仍是水、根因“没有来源”");
        }

        // ── K9 拓扑变化才重算 ─────────────────────────────────────────────────────────

        private static void CheckTopologyOnly()
        {
            PipeKernel k = NewKernel();
            BuildPerfNetworks(k, 0, 0, 10);
            Run(k, 5);
            int r0 = k.RebuildCount;
            Run(k, 1000);
            int r1 = k.RebuildCount;
            k.SetTankMode(1, 0, PipeTankMode.InOnly);
            k.SetValveOpen(99, 0, false);
            Run(k, 10);
            int r2 = k.RebuildCount;
            k.Place(0, 50, PipePieceKind.Pipe, 0, 0, 0);
            Run(k, 10);
            int r3 = k.RebuildCount;
            GC.Collect();
            long heap0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int gc0 = GC.CollectionCount(0);
            Run(k, 20000);
            long heap1 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            // Mono 托管堆按约 8 KB 的堆块计量（与传送带自检同一口径：每步 < 1 字节 = 两万步不到一个堆块的噪声就算不分配）。
            bool noAlloc = GC.CollectionCount(0) == gc0 && heap1 - heap0 <= 20000;
            Expect(r1 == r0 && r2 == r0 && r3 == r0 + 1 && noAlloc,
                $"K9 拓扑变化时才重算（参照电网子网）：{k.CellCount} 格稳态 1,000 步重算 0 次、改储罐模式 / 开关阀门不重算、放一格才重算 1 次；稳态 20,000 步托管堆增量 {heap1 - heap0} 字节、无 GC");
        }

        /// <summary>性能 / 拓扑测试用的一组网络：每个网络 100 格（泵 + 储罐 + 96 格管线 + 阀门接到下一个网络），两个消费者。</summary>
        public static void BuildPerfNetworks(PipeKernel k, int x0, int y0, int networks)
        {
            int w = PipeNetworkService.FluidId("water");
            int wf = w > 0 ? w : 1;
            for (int n = 0; n < networks; n++)
            {
                int y = y0 + n * 2;
                k.Place(x0, y, PipePieceKind.Pump, 0, 0, wf);
                k.Place(x0 + 1, y, PipePieceKind.Tank, 0, 0, 0);
                for (int i = 2; i < 98; i++)
                {
                    k.Place(x0 + i, y, PipePieceKind.Pipe, i % 7 == 0 ? 0 : 1, 0, 0);
                }
                k.Place(x0 + 98, y, PipePieceKind.Pipe, 1, 0, 0);
                k.Place(x0 + 99, y, PipePieceKind.Valve, 0, 1, 0);
                k.AddConsumer(x0 + 40, y, wf, 150, 1 + n % 4);
                k.AddConsumer(x0 + 90, y, wf, 250, 2);
            }
        }

        // ── K10 确定性与存档 ──────────────────────────────────────────────────────────

        private static void BuildComposite(PipeKernel k)
        {
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 6, 1);
            k.Place(7, 0, PipePieceKind.Tank, 0, 0, 0);
            k.Place(3, 1, PipePieceKind.Valve, 0, 0, 0);
            Pipes(k, 3, 2, 4);
            k.Place(6, 3, PipePieceKind.Tank, 0, 0, 0);
            k.SetTankMode(6, 3, PipeTankMode.InOnly);
            k.SetTankPriority(6, 3, 2);
            k.AddConsumer(5, 0, W, 170, 1);
            k.AddConsumer(4, 2, W, 90, 3);
            k.Place(20, 0, PipePieceKind.Pump, 0, 0, C);
            Pipes(k, 21, 0, 5);
            k.AddConsumer(24, 0, C, 400, 2);
        }

        private static void CheckDeterminismAndSnapshot()
        {
            PipeKernel a = NewKernel();
            BuildComposite(a);
            Run(a, 700);
            PipeSnapshot snap = a.Serialize();
            PipeKernel b = NewKernel();
            bool loaded = b.Deserialize(snap, out string err) && err == null && b.Hash() == a.Hash();
            Run(a, 900);
            Run(b, 900);
            bool same = a.Hash() == b.Hash() && a.TotalDeliveredMl == b.TotalDeliveredMl;
            // 同一布置从头重跑一遍：结果相同（没有隐藏的随机或时间依赖）。
            PipeKernel c = NewKernel();
            BuildComposite(c);
            Run(c, 1600);
            bool repeat = c.Hash() == a.Hash();
            // 坏记录：一条坐标重复、一条种类越界 → 丢弃这两条，其余照常。
            PipeSnapshot bad = a.Serialize();
            bad.X[1] = bad.X[0];
            bad.Y[1] = bad.Y[0];
            bad.Kind[2] = 9;
            PipeKernel d = NewKernel();
            bool dropped = d.Deserialize(bad, out string derr) && d.DroppedOnLoad == 2 && derr == "records_dropped" && d.CellCount == a.CellCount - 2;
            PipeSnapshot future = a.Serialize();
            future.FormatVersion = 99;
            PipeKernel e = NewKernel();
            bool refused = !e.Deserialize(future, out string ferr) && ferr == "format_version" && e.CellCount == 0;
            Expect(loaded && same && repeat,
                $"K10 确定性：快照往返后指纹一致，接着跑 900 步与不存档一直跑逐位一致（累计送达 {a.TotalDeliveredMl / 1000.0} 升）；同一布置重跑一遍结果相同");
            Expect(dropped && refused,
                $"K10 读档防御：坐标重复 / 种类越界的记录各丢一条（丢弃 {d.DroppedOnLoad}），其余 {d.CellCount} 格照常；不认识的格式版本整体拒绝（{ferr}），内核保持空");
            // 修复轮 P2：调低储罐容量（logistics.pipe.tank_liters）后读老存档 → 存量夹到新容量，储罐不丢。
            PipeSnapshot full = a.Serialize();
            long maxStock = 0;
            for (int i = 0; i < full.Kind.Length; i++)
            {
                if (full.Kind[i] == (byte)PipePieceKind.Tank)
                {
                    maxStock = Math.Max(maxStock, full.Stock[i]);
                }
            }
            PipeConfig small = a.Config;
            small.TankLiters = (int)Math.Max(1, maxStock / 2000);
            PipeKernel sm = new PipeKernel(small);
            bool smLoaded = sm.Deserialize(full, out string smErr);
            long smCap = small.TankLiters * 1000L;
            long clampedExpect = 0;
            bool allCapped = true;
            for (int i = 0; i < full.Kind.Length; i++)
            {
                if (full.Kind[i] == (byte)PipePieceKind.Tank)
                {
                    clampedExpect += Math.Max(0, full.Stock[i] - smCap);
                    allCapped &= Cell(sm, full.X[i], full.Y[i]).TankStockMl == Math.Min(full.Stock[i], smCap);
                }
            }
            Expect(maxStock > 2000 && smLoaded && smErr == null && sm.DroppedOnLoad == 0 && sm.CellCount == a.CellCount && allCapped && sm.ClampedOnLoadMl == clampedExpect && clampedExpect > 0,
                $"K10 储罐容量调低后读老存档：存量夹到新容量 {small.TankLiters} 升、储罐一座不丢（{sm.CellCount} 格，丢弃 {sm.DroppedOnLoad}），倒掉 {sm.ClampedOnLoadMl / 1000.0} 升（热更层发通知）");
        }

        // ── K11 结冰计时接口 ─────────────────────────────────────────────────────────

        private static void CheckIdleClock()
        {
            PipeKernel k = NewKernel();
            Pipes(k, 0, 0, 6);
            Run(k, 10);
            long start = Net(k, 0, 0).LastFlowStep;
            Run(k, 3000);
            bool idle = Net(k, 0, 0).LastFlowStep == start && k.StepIndex - start >= 3000;
            k.Remove(3, 0, out _);
            Run(k, 1);
            bool inherit = Net(k, 0, 0).LastFlowStep == start && Net(k, 5, 0).LastFlowStep == start;
            PipeSnapshot s = k.Serialize();
            PipeKernel r = NewKernel();
            r.Deserialize(s, out _);
            bool saved = Net(r, 0, 0).LastFlowStep == start;
            k.Place(0, -1, PipePieceKind.Pump, 0, 0, W);
            k.AddConsumer(1, 0, W, 60, 1);
            Run(k, 3);
            bool reset = Net(k, 0, 0).LastFlowStep == k.StepIndex - 1 && Net(k, 5, 0).LastFlowStep == start;
            Expect(idle && inherit && saved && reset,
                $"K11 结冰计时接口（FGR-LOG-045 预留）：没有流动的网络记住最近一次流动的步（{start}），拆成两段后两段都继承、存档往返不变；一有流动就归零（另一段不受影响）");
        }

        // ── K12 渲染实例 ───────────────────────────────────────────────────────────────

        private static void CheckRenderInstances()
        {
            PipeKernel k = NewKernel();
            k.Place(0, 0, PipePieceKind.Pump, 0, 0, W);
            Pipes(k, 1, 0, 12, 1);
            k.Place(13, 0, PipePieceKind.Tank, 0, 0, 0);
            k.Place(6, 1, PipePieceKind.Valve, 0, 0, 0);
            k.SetValveOpen(6, 1, false);
            Run(k, Minute(k));
            PipeInstance[] inst = k.PrepareRender(6, out int count, out bool changed);
            int rev = k.Revision;
            k.PrepareRender(6, out _, out bool again);
            PipeInstance pump = inst[0];
            PipeInstance tank = inst.Take(count).First(i => (int)i.Az == 2);
            PipeInstance valve = inst.Take(count).First(i => (int)i.Az % 4 == 3);
            int pipeIcons = inst.Take(count).Count(i => (int)i.Az == 0 && ((int)i.Bw & 1) != 0);
            PipeInstance mid = inst.Take(count).First(i => (int)i.Az == 0 && (int)i.Ax == 3);
            bool ok = count == 15 && changed && !again && rev == k.Revision && (int)pump.Bx == W && ((int)pump.Bw & 1) != 0 && (int)mid.Bx == W && (int)mid.Aw == (1 << 1 | 1 << 3)
                      && tank.Bz > 0.05f && tank.Bz <= 1f && ((int)valve.Bw & 2) != 0 && (int)valve.Az == 3 + 4 * 0 && pipeIcons >= 1 && pipeIcons <= 3;
            var renderer = new PipeRenderer();
            renderer.SetPalette(W, PipeNetworkService.FluidColor(W), PipeNetworkService.FluidGlyph(W));
            renderer.SetPalette(C, PipeNetworkService.FluidColor(C), PipeNetworkService.FluidGlyph(C));
            bool palette = renderer.PaletteColor(W) != renderer.PaletteColor(C) && PipeNetworkService.FluidGlyph(W) != PipeNetworkService.FluidGlyph(C);
            renderer.Dispose();
            Expect(ok && palette,
                $"K12 渲染（FGR-LOG-040“按所载流体上色，并有图标”）：{count} 个实例；每格带流体编号（泵 / 管线 = 水）、连接掩码（东西相通）、泵 / 储罐 / 阀门都画图标、管线每 6 格一个（这段 {pipeIcons} 个）、" +
                $"阀门关闭标志、储罐液位 {tank.Bz:F2}；版本没变时不重填；水与原油颜色、图标形状都不同（颜色之外还有形状，B15）");
        }

        // ── K13 性能 ───────────────────────────────────────────────────────────────────

        private static void CheckKernelPerformance()
        {
            PipeKernel k = NewKernel();
            var sw = Stopwatch.StartNew();
            BuildPerfNetworks(k, 0, 0, 30);
            sw.Stop();
            var rb = Stopwatch.StartNew();
            k.Rebuild();
            rb.Stop();
            Run(k, 200);
            var steps = new List<double>(2000);
            for (int i = 0; i < 2000; i++)
            {
                k.Step();
                steps.Add(k.LastStepMs);
            }
            steps.Sort();
            double p95 = steps[(int)(steps.Count * 0.95)];
            double avg = steps.Average();
            // 编辑之后第一步含一次重算（O(格数)）
            k.Place(0, 100, PipePieceKind.Pipe, 0, 0, 0);
            var edit = Stopwatch.StartNew();
            k.Step();
            edit.Stop();
            PerfLines.Add($"管线内核（{k.CellCount:N0} 格 / {k.NetworkCount} 个网络 / {k.ConsumerCount} 个消费者，Editor）：单步 平均 {avg:F4} ms、p95 {p95:F4} ms；" +
                          $"整张重算 {rb.Elapsed.TotalMilliseconds:F3} ms（首次）/ 编辑后那一步 {edit.Elapsed.TotalMilliseconds:F3} ms（含重算 {k.LastRebuildMs:F3} ms）；搭建 {sw.Elapsed.TotalMilliseconds:F1} ms");
            ExpectPerf(k.CellCount >= 3000,
                $"K13 性能（FG03 第 7 节 管线 3,000 格；物流内核单步 ≤ 2 ms）：{k.CellCount:N0} 格单步 p95 {p95:F4} ms（阈值 0.5 ms）、编辑后重算 {k.LastRebuildMs:F3} ms（阈值 10 ms）",
                PerfGate.Le(p95, 0.5, "单步 p95 ms"), PerfGate.Le(k.LastRebuildMs, 10.0, "编辑后重算 ms"));
            // 修复轮 P1：框选拆除（一帧逐格拆几百格，热更层每格 TryGetKind + Remove）不能每格整网重算。3,000 格里框掉 400 格：
            // 拆的过程零重算、每格 O(1)；之后那一步只压紧 + 重算一次。再一次拆光全部格同理。
            int beforeRb = k.RebuildCount;
            int cells0 = k.CellCount;
            var box = Stopwatch.StartNew();
            int boxRemoved = 0;
            for (int y = 0; y < 16; y += 2)
            {
                for (int x = 10; x < 60; x++)
                {
                    if (k.TryGetKind(x, y, out _, out _) && k.Remove(x, y, out _) == PipeResult.Ok)
                    {
                        boxRemoved++;
                    }
                }
            }
            box.Stop();
            int rbDuring = k.RebuildCount - beforeRb;
            var after = Stopwatch.StartNew();
            k.Step();
            after.Stop();
            int rbAfter = k.RebuildCount - beforeRb;
            bool boxOk = boxRemoved == 400 && k.CellCount == cells0 - 400 && rbDuring == 0 && rbAfter == 1;
            // 拆光全部
            var all = Stopwatch.StartNew();
            int allRemoved = 0;
            for (int n = 0; n < 30; n++)
            {
                for (int x = 0; x < 100; x++)
                {
                    if (k.TryGetKind(x, n * 2, out _, out _) && k.Remove(x, n * 2, out _) == PipeResult.Ok)
                    {
                        allRemoved++;
                    }
                }
            }
            k.Remove(0, 100, out _);
            all.Stop();
            int rbAll0 = k.RebuildCount;
            k.Step();
            bool allOk = k.CellCount == 0 && k.NetworkCount == 0 && k.RebuildCount == rbAll0 + 1;
            PerfLines.Add($"管线框选拆除（Editor）：3,000 格里框掉 {boxRemoved} 格 {box.Elapsed.TotalMilliseconds:F3} ms（拆的过程重算 {rbDuring} 次）+ 之后那一步 {after.Elapsed.TotalMilliseconds:F3} ms；" +
                          $"一次拆光剩下 {allRemoved + 1} 格 {all.Elapsed.TotalMilliseconds:F3} ms");
            ExpectPerf(boxOk && allOk,
                $"K13 框选拆除（FGR-LOG-007 × 3,000 格，一帧 ≤ 8.3 ms）：框掉 {boxRemoved} 格用时 {box.Elapsed.TotalMilliseconds:F3} ms（阈值 4 ms）、期间重算 {rbDuring} 次；" +
                $"之后那一步（压紧 + 重算一次）{after.Elapsed.TotalMilliseconds:F3} ms（阈值 10 ms）；一次拆光 {allRemoved + 1} 格 {all.Elapsed.TotalMilliseconds:F3} ms（阈值 8 ms），之后内核为空、只重算 1 次",
                PerfGate.Le(box.Elapsed.TotalMilliseconds, 4.0, "框掉 400 格 ms"), PerfGate.Le(after.Elapsed.TotalMilliseconds, 10.0, "之后那一步 ms"), PerfGate.Le(all.Elapsed.TotalMilliseconds, 8.0, "一次拆光 ms"));
        }

        // ── 家园工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgpipe-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static bool StepUntil(Func<bool> done, int maxGameSeconds)
        {
            for (int i = 0; i < maxGameSeconds * 4; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 4));
            }
            return done();
        }

        private static bool FreePipe(CampaignState s, GridCell c) => HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok;

        /// <summary>
        /// 核心附近按圈找一格水源（泵能放），且朝某个方向连续 <paramref name="len"/> 格能铺管线（B25：按地形找，不写死坐标；起始区保证 24 格内有水源）。
        /// </summary>
        private static bool FindWaterSite(CampaignState s, int len, out GridCell water, out int dir)
        {
            GridCell core = HomeGridService.CorePivot(s);
            water = default;
            dir = 0;
            for (int r = 3; r <= 40; r++)
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
                        if (PipeNetworkService.SourceFluidAt(s, c) != W || !HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pump).Ok)
                        {
                            continue;
                        }
                        for (int d = 0; d < 4; d++)
                        {
                            bool ok = true;
                            for (int i = 1; i <= len && ok; i++)
                            {
                                var p = new GridCell(c.X + BeltDirs.Dx(d) * i, c.Y + BeltDirs.Dy(d) * i);
                                ok = FreePipe(s, p) && PipeNetworkService.SourceFluidAt(s, p) == 0;
                                // 旁边一格也要空（不被邻近管线件意外连接）
                            }
                            if (ok)
                            {
                                water = c;
                                dir = d;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        internal static GridCell Along(GridCell c, int dir, int i) => new GridCell(c.X + BeltDirs.Dx(dir) * i, c.Y + BeltDirs.Dy(dir) * i);

        /// <summary>测试捷径（经正式入口 PipeNetworkService.TryPlace，含格网与流体规则）：水源上一台泵 + 朝 dir 的 len 格管线 + 末端储罐。</summary>
        private static bool LayWaterLine(CampaignState s, GridCell water, int dir, int len, int tier, out string failure)
        {
            failure = null;
            PipeOpResult r = PipeNetworkService.TryPlace(s, water, PipePieceKind.Pump, 0, 0);
            for (int i = 1; i <= len && r.Ok; i++)
            {
                r = PipeNetworkService.TryPlace(s, Along(water, dir, i), i == len ? PipePieceKind.Tank : PipePieceKind.Pipe, tier, 0);
            }
            if (!r.Ok)
            {
                failure = r.Describe();
            }
            return r.Ok;
        }

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        // ── F1 数据 ────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            PipeConfig cfg = PipeNetworkService.ReadConfig();
            bool tuning = cfg.LitersPerMinuteT1 == 600 && cfg.LitersPerMinuteT2 == 1200 && cfg.PumpLitersPerMinute == 300 && cfg.TankLiters == 5000
                          && Math.Abs(GridContent.Tuning("logistics.pipe.freeze_idle_hours") - 1f) < 1e-4 && PipeNetworkService.ReadRenderSettings().IconEvery == 6;
            Expect(tuning, "F1 调参入表（FG03 第 10 章初值）：管线 T1 600 / T2 1,200 升/分钟、泵 300 升/分钟、储罐 5,000 升、结冰条件 1 游戏小时、每 6 格一个流体图标");
            bool tools = BuildCatalog.TryGet("pipe_t1", out BuildEntry p1) && p1.IsTool && p1.Tool.Kind == "pipe" && p1.Tool.Tier == 0 && p1.Tool.ScrapPerCell == 1 && p1.CategoryId == "logistics"
                         && BuildCatalog.TryGet("pipe_t2", out BuildEntry p2) && p2.Tool.Tier == 1 && p2.Tool.ScrapPerCell == 2
                         && BuildCatalog.TryGet("pump", out BuildEntry pu) && pu.Tool.Kind == "pump"
                         && BuildCatalog.TryGet("tank", out BuildEntry tk) && tk.Tool.Kind == "tank"
                         && BuildCatalog.TryGet("valve", out BuildEntry va) && va.Tool.Kind == "valve"
                         && HomeGridService.PipePieceCost(PipePieceKind.Tank, 0) == 10 && HomeGridService.PipePieceCost(PipePieceKind.Pipe, 1) == 2;
            Expect(tools, "F1 建造菜单“物流”页签五个新工具：管线 T1 / T2、泵、储罐、阀门（种类、等级、造价）");
            var fluids = new List<GameConfig.fg.Fluid>();
            PipeNetworkService.CollectFluids(fluids);
            string[] want = { "water", "crude", "coolant", "fuel", "acid" };
            bool five = fluids.Count == 5 && want.All(k => fluids.Any(f => f.Key == k)) && W == 1 && C == 2
                        && PipeNetworkService.FluidName(W) == "水" && PipeNetworkService.FluidName(C) == "原油"
                        && PipeNetworkService.SourceFluidOfTerrain(GridContent.TerrainCode("water")) == W && PipeNetworkService.SourceFluidOfTerrain(GridContent.TerrainCode("oil")) == C
                        && PipeNetworkService.SourceFluidOfTerrain(GridContent.TerrainCode("buildable")) == 0
                        && fluids.Select(f => f.Color + f.Glyph).Distinct().Count() == 5;
            Expect(five, "F1 流体表 fg.TbFluid（FGR-LOG-040 五种：水、原油、冷却液、燃油、酸液）：水源地形出水、油井出原油、空地不出；颜色 + 图标组合各不相同");
            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("FL\t", StringComparison.Ordinal)).ToArray();
            string[] rt = fluids.OrderBy(f => f.SortOrder).Select(f => string.Join("\t", "FL", f.Id, f.Key, f.NameKey, f.Color, f.Glyph, f.SourceTerrain, f.SortOrder)).ToArray();
            Expect(code == 0 && src.Length == 5 && src.SequenceEqual(rt), $"F1 流体表与源数据 fgdata_pipes.FLUIDS 逐字段一致（{src.Length} 行）" + (code == 0 ? string.Empty : "：" + Tail(output)));
            string[] keys =
            {
                "logistics.pipe.reason.fluid_conflict", "grid.reason.pump_needs_source", "logistics.pipe.hover.bottleneck", "logistics.pipe.state.no_supply",
                "logistics.pipepanel.flush_confirm_title", "codex.logistics.pipe.body", "codex.logistics.fluid.body", "build.tool.valve.desc", "ui.build.pipe_joins",
            };
            bool texts = keys.All(GameText.Has);
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = keys.All(key => !GameText.ContainsMarker(GameText.Get(key)) && GameText.Get(key).Length > 0) && PipeNetworkService.FluidName(C) == "Crude oil";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsFirstPipe) && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsPipeFirstFluidConflict)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsPipeFirstNoSupply) && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsPipePanelFirstOpen)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsPipeFirstFlush);
            var codex = ConfigSystem.Instance.Tables.TbCodexEntry;
            bool entries = codex.DataList.Any(e => e.Id == "codex.logistics.pipe" && e.Hooks.Contains(GuidanceHooks.LogisticsFirstPipe))
                           && codex.DataList.Any(e => e.Id == "codex.logistics.fluid" && e.Hooks.Contains(GuidanceHooks.LogisticsPipeFirstFluidConflict));
            Expect(texts && en && hooks && entries, "F1 新文本中英都有（原因、悬停、面板、图鉴）；5 个引导钩子已登记（第一次放管线件 / 接错流体 / 供给为 0 / 打开面板 / 冲洗）；图鉴有“管线”“流体网络”两条（FG03 第 4 节）");
        }

        // ── F2 建造菜单真实输入 ─────────────────────────────────────────────────────────

        private static void CheckBuildMode()
        {
            foreach (int seed in new[] { 9301, 9302, 9303 })
            {
                CampaignState s = NewWorld(seed, scrap: 300);
                HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
                try
                {
                    if (mode == null || !FindWaterSite(s, 7, out GridCell water, out int dir))
                    {
                        Fail($"F2 种子 {seed}：核心 40 格内找不到能放泵的水源（且旁边能铺 7 格管线）/ 建造模式没绑定");
                        continue;
                    }
                    mode.Open();
                    // 泵不在水源上：原因写明。
                    GridCell dry = Along(water, dir, 3);
                    mode.Select("pump");
                    mode.PointerDown(s, dry);
                    mode.PointerUp(s, dry);
                    string dryReason = mode.StatusText;
                    bool dryRefused = !HomeValleyConstruction.TryFindPlannedCell(s, dry, out _, out _) && dryReason.Contains("泵要放在水源或油井上");
                    // 泵放在水源上（虚影）。
                    mode.PointerDown(s, water);
                    mode.PointerUp(s, water);
                    bool pumpPlanned = HomeValleyConstruction.TryFindPlannedCell(s, water, out PlannedBeltRecord pp, out _) && pp.PipePiece == (int)PipePieceKind.Pump + 1
                                       && pp.PipeFluid == W && HomeValleyConstruction.IsPlannedMarker(HomeGridService.MapFor(s).GetPipe(water)) && mode.StatusText.Contains("泵");
                    // 拖一段 T1 管线（自动路径），拖的时候写长度、成本与“新的网络”。
                    mode.Select("pipe_t1");
                    GridCell a = Along(water, dir, 1);
                    GridCell b = Along(water, dir, 5);
                    mode.PointerDown(s, a);
                    mode.SetHover(s, b);
                    bool dragPlan = mode.BeltPlan != null && mode.BeltPlan.IsPipe && mode.BeltPlan.Ok && mode.BeltPlan.Length == 5 && mode.BeltPlan.TotalCost == 5;
                    mode.PointerUp(s, b);
                    bool pipesPlanned = Enumerable.Range(1, 5).All(i => HomeValleyConstruction.TryFindPlannedCell(s, Along(water, dir, i), out PlannedBeltRecord q, out _)
                                                                        && q.PipePiece == 1 && q.Tier == 0) && mode.StatusText.Contains("管线 T1");
                    // 储罐放在管线末端。
                    mode.Select("tank");
                    GridCell tankAt = Along(water, dir, 6);
                    mode.PointerDown(s, tankAt);
                    mode.PointerUp(s, tankAt);
                    bool tankPlanned = HomeValleyConstruction.TryFindPlannedCell(s, tankAt, out PlannedBeltRecord tp, out _) && tp.PipePiece == (int)PipePieceKind.Tank + 1;
                    // 阀门预览：进出两个箭头。
                    mode.Select("valve");
                    mode.SetHover(s, Along(water, dir, 7));
                    mode.RefreshPreview(s);
                    mode.RefreshVisuals(s);
                    bool valveArrows = mode.ToolPreview != null && mode.ToolPreview.IsPipe && mode.ToolPreview.Pipe == PipePieceKind.Valve && mode.ActiveArrowCount == 2;
                    mode.ClearSelection();
                    bool built = StepUntil(() => PipeNetworkService.Kernel.TryGetKind(water.X, water.Y, out PipePieceKind k1, out _) && k1 == PipePieceKind.Pump
                                                 && PipeNetworkService.Kernel.TryGetKind(tankAt.X, tankAt.Y, out PipePieceKind k2, out _) && k2 == PipePieceKind.Tank
                                                 && Enumerable.Range(1, 5).All(i => PipeNetworkService.Kernel.HasCell(Along(water, dir, i).X, Along(water, dir, i).Y)), 600);
                    WorldSimulation.StepMany(GameClock.StepHz * 30);
                    PipeCellInfo tank = Cell(PipeNetworkService.Kernel, tankAt.X, tankAt.Y);
                    bool flowing = tank.TankStockMl > 0 && Net(PipeNetworkService.Kernel, a.X, a.Y).Fluid == W && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstPipe)
                                   && MechanicCodex.IsUnlocked("codex.logistics.pipe") && HomeGridService.MapFor(s).GetPipe(a) == PipeNetworkService.LayerValue(PipePieceKind.Pipe, 0);
                    Expect(dryRefused && pumpPlanned && dragPlan && pipesPlanned && tankPlanned && valveArrows,
                        $"F2 [种子 {seed}] 建造菜单真实输入：泵放在非水源格被拒（“{dryReason}”）；泵放在水源 ({water.X},{water.Y}) 上 = 虚影（认定为水）；拖 5 格 T1 管线（成本 5）、放储罐都成虚影；阀门预览画进出两个箭头");
                    Expect(built && flowing,
                        $"F2 [种子 {seed}] 机器取料施工：泵、管线、储罐都建成进内核（格网管线层同步），30 游戏秒后储罐里有 {tank.TankStockMl / 1000.0} 升水；第一次放管线件的钩子与图鉴“管线”条目已解锁");
                }
                finally
                {
                    mode?.Close();
                }
            }
        }

        // ── F3 家园里接错流体 ─────────────────────────────────────────────────────────

        private static void CheckHomeConflict()
        {
            CampaignState s = NewWorld(9311, scrap: 300);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (!FindWaterSite(s, 9, out GridCell water, out int dir) || !LayWaterLine(s, water, dir, 3, 0, out string failure))
            {
                Fail("F3 放不下水网络");
                return;
            }
            // 测试捷径：把管线末端外 3 格的地形改成油井（真实世界里油井在 128 格内，迷雾挡着），经正式入口放原油泵。
            GridCell oil = Along(water, dir, 6);
            HomeGridService.MapFor(s).SetTerrain(oil, GridContent.TerrainCode("oil"));
            PipeOpResult oilPump = PipeNetworkService.TryPlace(s, oil, PipePieceKind.Pump, 0, 0);
            WorldSimulation.StepMany(GameClock.StepHz);
            try
            {
                mode.Open();
                mode.Select("pipe_t1");
                GridCell a = Along(water, dir, 4);
                GridCell b = Along(water, dir, 5);
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                string preview = mode.BeltPlan?.Describe() ?? string.Empty;
                bool red = mode.BeltPlan != null && !mode.BeltPlan.Ok && mode.BeltPlan.FirstBadIndex == 1;
                mode.PointerUp(s, b);
                bool nothing = !HomeValleyConstruction.TryFindPlannedCell(s, a, out _, out _) && !HomeValleyConstruction.TryFindPlannedCell(s, b, out _, out _);
                bool hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsPipeFirstFluidConflict) && MechanicCodex.IsUnlocked("codex.logistics.fluid");
                // 正式入口：先在原油泵旁放一格原油管线（只挨着原油，合法），再在水网络与它之间放一格 → 拒绝。
                PipeOpResult oilSide = PipeNetworkService.TryPlace(s, b, PipePieceKind.Pipe, 0, 0);
                PipeOpResult direct = PipeNetworkService.TryPlace(s, a, PipePieceKind.Pipe, 0, 0);
                PipeNetworkService.TryRemove(s, b, out _);
                Expect(oilPump.Ok && red && preview.Contains("水") && preview.Contains("原油") && nothing && hook && oilSide.Ok && !direct.Ok && direct.Describe().Contains("一个网络只能有一种流体"),
                    $"F3 家园里接错流体（FGR-LOG-040 负向）：拖一段会把水网络和原油网络接起来的管线，预览第 2 格标红“{preview}”，松开什么都不放；正式入口同样拒绝（“{direct.Describe()}”）；钩子与图鉴“流体网络”已解锁");
                // 规划时合法、建成时才冲突：先放一格虚影（只挨着水网络），再在它旁边放原油管线。
                mode.Select("pipe_t1");
                mode.PointerDown(s, a);
                mode.PointerUp(s, a);
                bool ghost = HomeValleyConstruction.TryFindPlannedCell(s, a, out _, out _);
                PipeOpResult oilPipe = PipeNetworkService.TryPlace(s, b, PipePieceKind.Pipe, 0, 0);
                int stock0 = s.Scrap + GroundScrap(s);
                bool resolved = StepUntil(() => !HomeValleyConstruction.TryFindPlannedCell(s, a, out _, out _), 400);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                bool notBuilt = !PipeNetworkService.Kernel.HasCell(a.X, a.Y) && HomeGridService.MapFor(s).GetPipe(a) == 0;
                bool noted = NotificationCenter.History.Any(n => n.Text != null && n.Text.Contains("没有建成") && n.Text.Contains("原油"));
                int stock1 = s.Scrap + GroundScrap(s);
                Expect(ghost && oilPipe.Ok && resolved && notBuilt && noted && stock1 == stock0,
                    $"F3 规划时合法、建成时才冲突：虚影那一格没有建成（“{HomeValleyConstruction.LastPipeCommitFailure}”），发“失败”通知写明两种流体，这一格作废、材料退回（库存 + 地面 {stock0} → {stock1}）");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── F4 悬停 ─────────────────────────────────────────────────────────────────

        private static void CheckHover()
        {
            CampaignState s = NewWorld(9321, scrap: 300);
            if (!FindWaterSite(s, 7, out GridCell water, out int dir) || !LayWaterLine(s, water, dir, 6, 1, out string failure))
            {
                Fail("F4 放不下水网络");
                return;
            }
            // 换一格成 T1：瓶颈管段。
            GridCell slow = Along(water, dir, 3);
            PipeNetworkService.TryRemove(s, slow, out _);
            PipeNetworkService.TryPlace(s, slow, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.Kernel.AddConsumer(Along(water, dir, 2).X, Along(water, dir, 2).Y, W, 500, 1); // 测试捷径：外部消费者（FG4 建筑配方接入前）
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool ok = PipeNetworkService.TryDescribeHover(s, Along(water, dir, 1), out string title, out string body);
            bool content = ok && title.Contains("管线 T2") && body.Contains("网络：水") && body.Contains("供给 300 升/分钟") && body.Contains("需求 500｜") && body.Contains("输送 300｜")
                           && body.Contains("还差 200 升/分钟") && body.Contains("储量")
                           && body.Contains($"瓶颈：管线 T1（{slow.X}, {slow.Y}）") && body.Contains("供给不足") && !GameText.ContainsMarker(title + body);
            bool pump = PipeNetworkService.TryDescribeHover(s, water, out string pt, out string pb) && pt.Contains("泵") && pb.Contains("抽取水") && pb.Contains("额定 300");
            GridCell tankAt = Along(water, dir, 6);
            bool tank = PipeNetworkService.TryDescribeHover(s, tankAt, out _, out string tb) && tb.Contains("储罐：") && tb.Contains("双向（缓冲）");
            Expect(content && pump && tank,
                $"F4 悬停（FGR-LOG-042“显示网络的供给、需求、储量和瓶颈管段”）：“{title}”\n{body}\n泵：{pb.Split('\n')[0]}；储罐：{tb.Split('\n')[0]}");
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = PipeNetworkService.TryDescribeHover(s, Along(water, dir, 1), out string et, out string eb) && eb.Contains("Network: Water") && eb.Contains("Bottleneck") && !GameText.ContainsMarker(et + eb);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en, "F4 英文悬停同样完整（Network / Bottleneck，无缺键）");
        }

        // ── F5 面板 ─────────────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9331, scrap: 300);
            if (!FindWaterSite(s, 9, out GridCell water, out int dir) || !LayWaterLine(s, water, dir, 6, 0, out string failure))
            {
                Fail("F5 放不下水网络");
                return;
            }
            GridCell valveAt = Along(water, dir, 7);
            PipeNetworkService.TryPlace(s, valveAt, PipePieceKind.Valve, 0, dir);
            PipeNetworkService.TryPlace(s, Along(water, dir, 8), PipePieceKind.Pipe, 0, 0);
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            GridCell tankAt = Along(water, dir, 6);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            VisualElement root = MountUxml(UiKitFolder + "PipePanel.uxml", out GameObject go);
            PipePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                PipePanelUIToolkit panel = go.AddComponent<PipePanelUIToolkit>();
                panel.BindView(root);
                mode.Open();
                mode.PointerDown(s, tankAt);
                mode.PointerUp(s, tankAt);
                panel.Refresh();
                bool opened = PipePanelUIToolkit.IsOpen && panel.PanelVisible && panel.TankSettingsVisible && !panel.ValveSettingsVisible && panel.TitleText.Contains("储罐")
                              && panel.DetailText.Contains("网络：水") && panel.ModeField.choices.Count == 3 && panel.PriorityField.choices.Count == 4
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsPipePanelFirstOpen) && !GameText.ContainsMarker(panel.TitleText + panel.DetailText + panel.StateText);
                Expect(opened, $"F5 建造模式里点一下储罐打开管线面板（真 UXML）：“{panel.TitleText}”，{panel.StateText}；模式 3 项、优先级 4 项");
                panel.ModeField.value = panel.ModeField.choices[1];
                panel.PriorityField.value = panel.PriorityField.choices[1];
                PipeCellInfo t = Cell(PipeNetworkService.Kernel, tankAt.X, tankAt.Y);
                Expect(t.TankMode == PipeTankMode.InOnly && t.Priority == 2 && panel.MessageText.Contains("已修改"),
                    $"F5 下拉框选中即生效：储罐改成“只进”、优先级 2 → 内核 {t.TankMode} / {t.Priority}（“{panel.MessageText}”）");
                // 冲洗：先确认；取消什么都不变，确认才清空。
                long stock = t.TankStockMl;
                panel.AskFlush();
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title.Contains("水") && panel.PendingFlushConfirm;
                UiConfirmDialog.Cancel();
                bool kept = Cell(PipeNetworkService.Kernel, tankAt.X, tankAt.Y).TankStockMl >= stock && !panel.PendingFlushConfirm;
                panel.AskFlush();
                UiConfirmDialog.Confirm();
                bool flushed = Cell(PipeNetworkService.Kernel, tankAt.X, tankAt.Y).TankStockMl == 0 && panel.MessageText.Contains("已冲洗")
                               && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsPipeFirstFlush);
                Expect(stock > 0 && asked && kept && flushed,
                    $"F5 冲洗需要确认（FGR-LOG-044；卡片必须同时交付）：点“冲洗网络”先弹确认框（写明流体、存量、网络里还有泵会重新充入），取消后储罐仍有 {stock / 1000.0} 升；确认后清空（“{panel.MessageText}”）");
                // 阀门：开关、调头。
                PipePanelUIToolkit.Open(valveAt);
                panel.Refresh();
                bool valveUi = panel.ValveSettingsVisible && !panel.TankSettingsVisible && !panel.FlushButton.enabledSelf;
                panel.SetValveOpen(false);
                bool closed = !Cell(PipeNetworkService.Kernel, valveAt.X, valveAt.Y).ValveOpen && panel.ValveToggleButton.text.Contains("打开");
                panel.ReverseValve();
                bool reversed = Cell(PipeNetworkService.Kernel, valveAt.X, valveAt.Y).Dir == BeltDirs.Opposite(dir);
                Expect(valveUi && closed && reversed, "F5 阀门面板：只显示阀门设置（冲洗按钮禁用：阀门不属于任何网络）；“关闭阀门”生效、按钮变“打开阀门”；“调头”生效");
                UiEscapeStack.CloseTop();
                Expect(!PipePanelUIToolkit.IsOpen, "F5 Esc 关闭管线面板");
            }
            finally
            {
                PipePanelUIToolkit.Close();
                PipePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                mode?.Close();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
            }
            // 布局探针：中英 × 三种缩放，有数据的储罐面板。
            PipePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "PipePanel.uxml", "PipePanelWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_pp") { hideFlags = HideFlags.HideAndDontSave };
                            PipePanelUIToolkit p = probeGo.AddComponent<PipePanelUIToolkit>();
                            p.BindView(pr.panel.visualTree);
                            PipePanelUIToolkit.Open(tankAt);
                            p.Refresh();
                            PipePanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("PipePanelRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"F5 布局探针 PipePanel.uxml#PipePanelWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                PipePanelUIToolkit.Close();
                PipePanelUIToolkit.InWorldOverrideForTests = false;
            }
        }

        // ── F6 拆除 ─────────────────────────────────────────────────────────────────

        private static void CheckRemove()
        {
            CampaignState s = NewWorld(9341, scrap: 300);
            if (!FindWaterSite(s, 7, out GridCell water, out int dir) || !LayWaterLine(s, water, dir, 6, 0, out string failure))
            {
                Fail("F6 放不下水网络");
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            GridCell tankAt = Along(water, dir, 6);
            long stock = Cell(PipeNetworkService.Kernel, tankAt.X, tankAt.Y).TankStockMl;
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                mode.Open();
                mode.SetDemolishMode(true);
                int before = s.Scrap + GroundScrap(s);
                mode.ClickCell(s, tankAt);
                bool asked = mode.PendingTankConfirm && UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title.Contains("储罐");
                UiConfirmDialog.Cancel();
                bool kept = PipeNetworkService.Kernel.HasCell(tankAt.X, tankAt.Y);
                mode.ClickCell(s, tankAt);
                UiConfirmDialog.Confirm();
                bool removed = !PipeNetworkService.Kernel.HasCell(tankAt.X, tankAt.Y) && HomeGridService.MapFor(s).GetPipe(tankAt) == 0
                               && s.Scrap + GroundScrap(s) == before + 10 && HomeGridService.LastPipeDrainedMl >= stock;
                string status = mode.StatusText;
                Expect(stock > 0 && asked && kept && removed && status.Contains("排空"),
                    $"F6 拆除有存量的储罐先确认（FG00 B04：{stock / 1000.0} 升会排空、不返还）：取消后还在；确认后拆掉、造价 10 废料全额返还（“{status}”）");
                // 框选拆除：框里的管线件一并拆掉（每格 1 废料返还）；泵 6 废料。
                mode.ClickCell(s, Along(water, dir, 1)); // 单点一格管线
                int b2 = s.Scrap + GroundScrap(s);
                GridCell from = Along(water, dir, 0);
                GridCell to = Along(water, dir, 5);
                DemolishBoxPlan box = HomeGridService.PlanDemolishBox(s, from, to);
                bool boxSees = box.Pipes == 5 && !box.NeedsConfirm;
                mode.PointerDown(s, from);
                mode.SetHover(s, to);
                mode.PointerUp(s, to);
                bool allGone = Enumerable.Range(0, 6).All(i => !PipeNetworkService.Kernel.HasCell(Along(water, dir, i).X, Along(water, dir, i).Y));
                int refund = s.Scrap + GroundScrap(s) - b2;
                Expect(boxSees && allGone && refund == 6 + 4,
                    $"F6 框选拆除包含管线层：框里 {box.Pipes} 件（泵 + 4 格管线，储罐已拆、没有存量不用确认）一并拆掉，返还 {refund} 废料（泵 6 + 管线 4）");
            }
            finally
            {
                mode?.SetDemolishMode(false);
                mode?.Close();
                UiConfirmDialog.DiscardAll();
            }
        }

        // ── F7 存读档 ───────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            PipeKernel k = PipeNetworkService.Kernel;
            return k == null ? "无内核" : $"{k.Hash():X16}|{k.CellCount}|{k.StepIndex}|{k.TotalDeliveredMl}|{k.TotalPumpedMl}|冷潮{s.Pipes?.ColdSnap}";
        }

        /// <summary>FG3-LOG-09：别的自检复用管线场景前先取流体编号（本自检的 Run 里也是这样初始化的）。</summary>
        internal static void PrepareFluidIds()
        {
            W = PipeNetworkService.FluidId("water");
            C = PipeNetworkService.FluidId("crude");
        }

        internal static bool LayScenario(CampaignState s, out GridCell water, out int dir)
        {
            if (!FindWaterSite(s, 9, out water, out dir) || !LayWaterLine(s, water, dir, 6, 0, out _))
            {
                return false;
            }
            GridCell v = Along(water, dir, 7);
            PipeNetworkService.TryPlace(s, v, PipePieceKind.Valve, 0, dir);
            PipeNetworkService.TryPlace(s, Along(water, dir, 8), PipePieceKind.Pipe, 1, 0);
            PipeNetworkService.TryPlace(s, Along(water, dir, 9), PipePieceKind.Pipe, 1, 0);
            PipeNetworkService.Kernel.AddConsumer(Along(water, dir, 9).X, Along(water, dir, 9).Y, W, 170, 2);
            PipeNetworkService.Kernel.AddConsumer(Along(water, dir, 2).X, Along(water, dir, 2).Y, W, 90, 1);
            PipeNetworkService.TrySetTankMode(s, Along(water, dir, 6), PipeTankMode.Both);
            return true;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9351, scrap: 250);
            if (!LayScenario(s, out GridCell water, out int dir))
            {
                Fail("F7 放不下管线");
                return;
            }
            PipeNetworkService.SetColdSnap(s, true);
            WorldSimulation.StepMany(GameClock.StepHz * 17 + 5);
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded, int formatOverride = 0)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                if (formatOverride != 0)
                {
                    rr.State.Pipes.FormatVersion = formatOverride;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            bool layer = HomeGridService.MapFor(CampaignSession.Current).GetPipe(water) == PipeNetworkService.LayerValue(PipePieceKind.Pump, 0);
            string second = RunFromSave(out _);
            Expect(save.Success && loaded1 == before && CampaignSession.Current.Pipes.DomainVersion == 2 && CampaignSession.Current.Pipes.ColdSnap && layer,
                "F7 真文件存读档：管线件、每格流体、储罐存量与模式、阀门缓冲、消费者累计、最近流动步、寒潮开关逐字段往返一致，格网管线层读档后套回" +
                (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "F7 读档后接着跑 30 游戏秒，与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            // 不认识的格式：原样保留、这局不能改管线、存档不覆盖。
            RunFromSave(out _, 99);
            CampaignState cur = CampaignSession.Current;
            int cells = cur.Pipes.Xs.Length;
            PipeOpResult refused = PipeNetworkService.TryPlace(cur, Along(water, dir, 11), PipePieceKind.Pipe, 0, 0);
            WorldSimulation.SyncAllForSave();
            Expect(PipeNetworkService.SavedDataPreserved && !refused.Ok && refused.Describe().Contains("更新的版本") && cur.Pipes.FormatVersion == 99 && cur.Pipes.Xs.Length == cells && cells > 0,
                $"F7 不认识的管线存档格式：原始数据原样保留（{cells} 格不被覆盖），这局放管线给原因（“{refused.Describe()}”）");
            // 旧档（FG0-SAVE-01 空骨架，FormatVersion 0）：空内核，照常能铺。
            CampaignState legacy = NewWorld(9352, scrap: 100);
            legacy.Pipes = new PipeFluidState();
            PipeNetworkService.Load(legacy);
            bool legacyOk = PipeNetworkService.IsRunning && PipeNetworkService.Kernel.CellCount == 0 && !PipeNetworkService.SavedDataPreserved;
            Expect(legacyOk, "F7 旧存档（管线域只有 DomainVersion 1 的空骨架）读入：空的管线内核，照常可用");
        }

        // ── F8 / F9 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 200);
            pausedHeld = true;
            if (!LayScenario(s, out _, out _))
            {
                return "放不下";
            }
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 45;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9361, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                shown.Add($"{speed}x");
            }
            Expect(paused && same && reference != "放不下",
                $"F8 暂停中（120 帧）管线网络不动；{string.Join(" / ", shown)} 跑同样的 45 游戏秒，内核指纹、累计抽取与送达逐位一致（{reference}）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9371, true, 1f, false, out _);
            string unseen = RunScenario(9371, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "F9 同一组管线（泵、储罐、阀门、两个消费者）在观察与不观察家园时跑 45 游戏秒逐字段一致（FGR-BASE-021）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── F10 结冰计时接口 ───────────────────────────────────────────────────────────

        private static void CheckFreezeInterface()
        {
            CampaignState s = NewWorld(9381, scrap: 200);
            if (!FindWaterSite(s, 7, out GridCell water, out int dir))
            {
                Fail("F10 找不到水源");
                return;
            }
            // 一段没有来源的管线：流量一直为 0。
            GridCell a = Along(water, dir, 2);
            for (int i = 2; i <= 5; i++)
            {
                PipeNetworkService.TryPlace(s, Along(water, dir, i), PipePieceKind.Pipe, 0, 0);
            }
            double hourSec = PipeNetworkService.GameHourSeconds;
            WorldSimulation.StepMany((int)(GameClock.StepHz * hourSec * 1.2));
            int net = PipeNetworkService.Kernel.NetworkAt(a.X, a.Y);
            double idle = PipeNetworkService.IdleGameHours(net);
            bool noSnap = !PipeNetworkService.MeetsFreezeCondition(net);
            PipeNetworkService.SetColdSnap(s, true);
            bool condition = PipeNetworkService.MeetsFreezeCondition(net);
            // 接上泵、挂上消费者：寒潮中也照常流动（本 Story 只预留接口，不阻断），计时归零。
            PipeNetworkService.TryPlace(s, water, PipePieceKind.Pump, 0, 0);
            PipeNetworkService.TryPlace(s, Along(water, dir, 1), PipePieceKind.Pipe, 0, 0);
            int id = PipeNetworkService.Kernel.AddConsumer(a.X, a.Y, W, 100, 1);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            PipeNetworkService.Kernel.TryGetConsumer(id, out PipeConsumerInfo ci);
            double idle2 = PipeNetworkService.IdleGameHours(PipeNetworkService.Kernel.NetworkAt(a.X, a.Y));
            Expect(idle >= 1.15 && idle <= 1.25 && noSnap && condition && ci.TotalDeliveredMl > 0 && idle2 < 0.01,
                $"F10 结冰计时接口（FGR-LOG-045，本 Story 只预留游戏时钟接口）：流量为 0 的网络静止 {idle:F2} 游戏小时（1 游戏小时 = {hourSec:F0} 游戏秒）；没有寒潮时不满足结冰条件、寒潮开关打开后满足；" +
                $"本 Story 不阻断流动（寒潮中接上泵照常送到 {ci.TotalDeliveredMl / 1000.0} 升），有流动后计时归零");
        }

        // ── F11 120 帧节奏性能 ─────────────────────────────────────────────────────────

        private static void CheckPerformance120()
        {
            CampaignState s = NewWorld(9391, scrap: 150);
            GridCell core = HomeGridService.CorePivot(s);
            PipeKernel k = PipeNetworkService.Kernel;
            BuildPerfNetworks(k, core.X + 3000, core.Y + 3000, 30);
            Camera cam = WorldView.Camera;
            int hz = GameClock.StepHz;
            long tick = GameClock.Ticks;
            for (int f = 0; f < 120; f++)
            {
                PipeNetworkService.WorldStep(s, tick++, hz);
                PipeNetworkService.Render(cam);
            }
            var lines = new List<string>();
            bool ok = true;
            var perf = new List<PerfGate.Metric>();
            foreach (float speed in new[] { 1f, 3f })
            {
                const int frames = 1200;
                var frameMs = new List<double>(frames);
                double acc = 0;
                var sw = new Stopwatch();
                for (int f = 0; f < frames; f++)
                {
                    sw.Restart();
                    acc += speed * hz / 120.0;
                    while (acc >= 1.0)
                    {
                        PipeNetworkService.WorldStep(s, tick++, hz);
                        acc -= 1.0;
                    }
                    PipeNetworkService.Render(cam);
                    sw.Stop();
                    frameMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                var hw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    PipeNetworkService.TryDescribeHover(s, new GridCell(core.X + 3000 + 40, core.Y + 3000), out _, out _);
                }
                hw.Stop();
                frameMs.Sort();
                double avg = frameMs.Average();
                double p99 = frameMs[(int)(frames * 0.99)];
                lines.Add($"{speed}x@120 帧（{frames} 帧，{k.CellCount:N0} 格管线 / {k.NetworkCount} 个网络）：管线每帧 平均 {avg:F4} ms、p99 {p99:F4} ms；悬停读数 {hw.Elapsed.TotalMilliseconds * 1000.0 / 200:F0} µs / 次");
                perf.Add(PerfGate.Le(p99, 1.0, $"{speed}x 管线每帧 p99 ms"));
                perf.Add(PerfGate.Le(avg, 0.3, $"{speed}x 管线每帧平均 ms"));
            }
            GC.Collect();
            long memA = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int gcA = GC.CollectionCount(0);
            double accA = 0;
            for (int f = 0; f < 3000; f++)
            {
                accA += 3.0 * hz / 120.0;
                while (accA >= 1.0)
                {
                    PipeNetworkService.WorldStep(s, tick++, hz);
                    accA -= 1.0;
                }
                PipeNetworkService.Render(cam);
            }
            long steadyAlloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - memA;
            bool noGc = GC.CollectionCount(0) == gcA;
            lines.Add($"稳态分配（3x 节奏 3,000 帧 = 4,500 个世界步 + 3,000 次 Render）：托管堆增量 {steadyAlloc} 字节{(noGc ? string.Empty : "（期间发生 GC）")}");
            ok &= steadyAlloc < 3000 * 8;
            PerfLines.AddRange(lines);
            ExpectPerf(ok && k.CellCount >= 3000,
                "F11 120 帧最低标准（8.33 ms / 帧）下 3,000 格管线：1x 与 3x 逐帧实测 p99 ≤ 1 ms、平均 ≤ 0.3 ms，稳态几乎不分配（Editor batchmode 无 GPU；GPU 画面由 FG3-LOG-09 真机复测）：" + string.Join("；", lines), perf.ToArray());
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgPipeSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static string LocateRepo()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "tools", "cell_tables", "fgdata.py")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            try
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
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    return (p.ExitCode, output);
                }
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }

        private static string Tail(string s)
        {
            string[] lines = (s ?? string.Empty).Replace("\r", string.Empty).Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - 2)));
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
                UiConfirmDialog.DiscardAll();
                UiEscapeStack.Clear();
                InputRouter.SetBuildMode(false);
                InputRouter.BuildDragActive = false;
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
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
