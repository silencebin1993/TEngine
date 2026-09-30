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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-04 分流、合流、地下带、过滤的自动验收（FG03 FGR-LOG-022、023；FGT-LOG-005 的分流比例与过滤部分；第 5 节负向“分流器两个输出口都堵”；
    /// 卡片负向“地下带跨度超限”、必须同时交付“过滤器预设”“分流比例在悬停中显示”）。全部起真实系统：真实 Burst 内核（BeltKernel）、真实世界模拟与家园
    /// （WorldSimulation.LoadHome：建造模式、虚影施工、机器取料、端口对账都在世界步里跑）、真实存读档文件、真 UXML 面板。行为坏了会失败：
    /// 内核（K）
    /// K1 分流器默认 1:1（左右各一半、实测窗口一致、分出件数 = 送达 + 在途）；K2 比例 1:1 / 2:1 / 3:1 / 1:2 / 3:2 逐件精确；K3 优先输出口与溢流（优先口有空就只走它，
    /// 满了才走另一个，恢复后回到优先口）；K4 每口过滤（分拣不串、只放某种的口堵了就等、关闭的口、没有口收）；K5 负向：两个输出口都堵——分流器停下、上游堵塞、
    /// 原因、物品不丢不增，接通后恢复；K6 一侧没接 / 都没接、从错误的一侧顶着节点（分流器侧面、合流器后方、地下出口、地下入口侧面）；
    /// K7 合流器默认交替、优先输入口（优先口满载时另一路等、优先口稀疏时另一路补空）、改优先口立刻生效；K8 地下传送带跨过另一条传送带（两路互不串、三档吞吐）；
    /// K9 地下负向（太远、同方向重叠、垂直交叉可以、两端被占、拆任一端拆整条、不能原地转、改等级整条改）；K10 地下出口堵住：地下段装满后入口与上游堵塞，
    /// 原因写“地下段已满（通往出口）”，接通后恢复；K11 经过地下段的环一直转、经过分流器的回路（另一口堵死时填满停下，接通后恢复）；
    /// K12 确定性（放置顺序打乱 / 中途序列化接着跑）、格式 3 往返节点设置与状态、格式 2 旧网络块照常读、地下传送带不完整的网络块整块丢弃；
    /// K13 渲染（地下段不画、节点种类编码）；K14 性能（15,000 格含 340 个节点 / ≥ 30,000 件，单步 p95 ≤ 2 ms）。
    /// 正式（F）
    /// F1 数据（调参、文本键中英、建造菜单五个新工具、钩子、图鉴条目、过滤器预设表与源数据逐字段、check_luban R33 自测）；
    /// F2 建造菜单真实输入（放置预览与进出口箭头、单击放分流器 / 转向放合流器、拖地下传送带穿过归还核心、跨度超限 / 不是直线 / 没拖 / 同方向重叠的原因、
    /// 取消地下虚影两端一起、机器施工建成、三个种子）；F3 家园真实端口：仓库输出 → 分流器 3:1 → 仓库与核心输入口，守恒；
    /// F4 悬停与堵塞原因文字（比例、实测、输出口、优先口、跨度、地下段已满、错误侧）；F5 节点面板（真 UXML：比例 / 优先口 / 过滤 / 预设套用 / 存为预设 /
    /// 删除先确认 / 合流器 / 地下传送带 / Esc）与布局探针；F6 拆除返还（分流器一座、地下两端与地下物品）与摧毁留虚影（保留设置）→ 重建恢复设置；
    /// F7 真文件存读档（节点设置、比例轮次、累计、自定义预设、节点虚影）与读档后接着跑一致；F8 暂停与 0.5x～3x；F9 观察 / 不观察一致；F10 120 帧节奏性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgBeltNodeSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 6;
        private const int CL = BeltConst.CellLength;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 分流、合流、地下带、过滤")]
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
            Line("\n[分流、合流、地下带、过滤] 分流器 / 合流器 / 地下传送带 / 每口过滤与预设（FG3-LOG-04）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbeltnode-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                     $"Burst={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}，图形设备 {SystemInfo.graphicsDeviceType}；内核数字是 Editor 下的 Burst 作业，热更层是 Mono 托管代码，真机另测（FG15-SYS-02）");

                Step(CheckSplitterEven);
                Step(CheckSplitterRatios);
                Step(CheckSplitterPriority);
                Step(CheckSplitterFilters);
                Step(CheckSplitterBothBlocked);
                Step(CheckDisconnectedAndWrongSide);
                Step(CheckMerger);
                Step(CheckUndergroundCrossing);
                Step(CheckUndergroundNegatives);
                Step(CheckUndergroundBackup);
                Step(CheckLoopsThroughNodes);
                Step(CheckDeterminismAndSnapshots);
                Step(CheckRender);
                Step(CheckKernelPerformance);
                Step(CheckData);
                Step(CheckBuildModePlacement);
                Step(CheckHomePortsWithSplitter);
                Step(CheckHoverTexts);
                Step(CheckNodePanel);
                Step(CheckRemoveAndDestroy);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance120);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"分流 / 合流 / 地下带自检抛异常：{e}");
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
                BeltNodePanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [分流、合流、地下带、过滤] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 内核工具 ───────────────────────────────────────────────────────────

        private static BeltKernel NewKernel() => new BeltKernel(BeltNetworkService.ReadConfig());

        private static void AddLine(BeltKernel k, int x0, int y0, int len, BeltDir dir, int tier)
        {
            for (int i = 0; i < len; i++)
            {
                k.AddCell(x0 + BeltDirs.Dx((int)dir) * i, y0 + BeltDirs.Dy((int)dir) * i, dir, tier);
            }
        }

        private static BeltCellInfo Info(BeltKernel k, int x, int y)
        {
            k.TryGetCellInfo(x, y, out BeltCellInfo i);
            return i;
        }

        private static BeltNodeInfo Node(BeltKernel k, int x, int y)
        {
            k.TryGetNodeInfo(x, y, out BeltNodeInfo n);
            return n;
        }

        private static BeltPortInfo PortOf(BeltKernel k, int id)
        {
            k.TryGetPortInfo(id, out BeltPortInfo p);
            return p;
        }

        /// <summary>一条线上（从 (x0,y0) 朝 dir 共 len 格）的件数。</summary>
        private static int ItemsOnLine(BeltKernel k, int x0, int y0, int len, BeltDir dir)
        {
            int n = 0;
            for (int i = 0; i < len; i++)
            {
                n += Info(k, x0 + BeltDirs.Dx((int)dir) * i, y0 + BeltDirs.Dy((int)dir) * i).Count;
            }
            return n;
        }

        /// <summary>每步不变量：账本平衡、容量从未越界、O(1) 计数 = 逐格重数、每格（含地下段）物品位置合法且间距 ≥ 1 / 容量 格。</summary>
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
            k.CollectCells(cells, includeUnderground: true);
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
            }
            why = null;
            return true;
        }

        private static string Why(string why) => why != null ? "；" + why : string.Empty;

        /// <summary>两个内核的第一处差别（逐格：坐标、朝向、等级、种类、轮次、物品；节点设置与状态；端口；账本）。没有差别返回 null。</summary>
        private static string FirstDiff(BeltKernel a, BeltKernel b)
        {
            var ca = new List<int3>();
            var cb = new List<int3>();
            a.CollectCells(ca, includeUnderground: true);
            b.CollectCells(cb, includeUnderground: true);
            if (ca.Count != cb.Count)
            {
                return $"格数 {ca.Count} / {cb.Count}";
            }
            for (int i = 0; i < ca.Count; i++)
            {
                if (!ca[i].Equals(cb[i]))
                {
                    return $"第 {i} 格 {ca[i]} / {cb[i]}";
                }
                BeltCellInfo x = Info(a, ca[i].x, ca[i].y);
                BeltCellInfo y = Info(b, ca[i].x, ca[i].y);
                if (x.Count != y.Count || x.Turn != y.Turn || x.Kind != y.Kind)
                {
                    return $"格 ({ca[i].x},{ca[i].y}) 件数 {x.Count}/{y.Count} 轮次 {x.Turn}/{y.Turn} 种类 {x.Kind}/{y.Kind}";
                }
                for (int s = 0; s < x.Count; s++)
                {
                    if (x.PosAt(s) != y.PosAt(s) || x.ItemAt(s) != y.ItemAt(s))
                    {
                        return $"格 ({ca[i].x},{ca[i].y}) 第 {s} 件 {x.ItemAt(s)}@{x.PosAt(s)} / {y.ItemAt(s)}@{y.PosAt(s)}";
                    }
                }
                if (a.TryGetNodeInfo(ca[i].x, ca[i].y, out BeltNodeInfo na) != b.TryGetNodeInfo(ca[i].x, ca[i].y, out BeltNodeInfo nb))
                {
                    return $"格 ({ca[i].x},{ca[i].y}) 节点有无不同";
                }
                if (na.RatioL != nb.RatioL || na.RatioR != nb.RatioR || na.PriorityOut != nb.PriorityOut || na.FilterL != nb.FilterL || na.FilterR != nb.FilterR
                    || na.SentL != nb.SentL || na.SentR != nb.SentR || na.PriorityIn != nb.PriorityIn || na.Distance != nb.Distance)
                {
                    return $"节点 ({ca[i].x},{ca[i].y}) {na.Kind}：{na.RatioL}:{na.RatioR}/{nb.RatioL}:{nb.RatioR} 优先 {na.PriorityOut}/{nb.PriorityOut} 过滤 {na.FilterL},{na.FilterR}/{nb.FilterL},{nb.FilterR} " +
                           $"分出 {na.SentL},{na.SentR}/{nb.SentL},{nb.SentR} 合流 {na.PriorityIn}/{nb.PriorityIn} 距离 {na.Distance}/{nb.Distance}";
                }
            }
            var pa = new List<int>();
            var pb = new List<int>();
            a.CollectPortIds(pa);
            b.CollectPortIds(pb);
            pa.Sort();
            pb.Sort();
            if (!pa.SequenceEqual(pb))
            {
                return $"端口 {string.Join(",", pa)} / {string.Join(",", pb)}";
            }
            foreach (int id in pa)
            {
                BeltPortInfo x = PortOf(a, id);
                BeltPortInfo y = PortOf(b, id);
                if (x.Pending != y.Pending || x.Buffered != y.Buffered || x.Total != y.Total || x.Consumed != y.Consumed || x.BlockedSteps != y.BlockedSteps || x.Phase != y.Phase
                    || x.Face != y.Face || x.Accept != y.Accept || x.ItemType != y.ItemType)
                {
                    return $"端口 {id}：待推 {x.Pending}/{y.Pending} 缓存 {x.Buffered}/{y.Buffered} 累计 {x.Total}/{y.Total} 消耗 {x.Consumed}/{y.Consumed} 堵 {x.BlockedSteps}/{y.BlockedSteps} 节拍 {x.Phase}/{y.Phase}";
                }
            }
            BeltLedger la = a.Ledger;
            BeltLedger lb = b.Ledger;
            if (la.Emitted != lb.Emitted || la.Inserted != lb.Inserted || la.Delivered != lb.Delivered || la.Removed != lb.Removed || a.StepIndex != b.StepIndex)
            {
                return $"账本 推上 {la.Emitted}/{lb.Emitted} 放入 {la.Inserted}/{lb.Inserted} 收下 {la.Delivered}/{lb.Delivered} 移出 {la.Removed}/{lb.Removed} 步 {a.StepIndex}/{b.StepIndex}";
            }
            return a.ComputeStateHash() == b.ComputeStateHash() ? null : "逐字段看不出差别，但状态哈希不同";
        }

        /// <summary>
        /// 标准分流场景：输入（源 → 5 格朝东）→ 分流器 (5,0) 朝东 → 左口朝北 4 格到输入口 (5,5)、右口朝南 4 格到输入口 (5,-5)。
        /// 端口号：源 1、左输入口 2、右输入口 3。<paramref name="sinks"/> = false 时两个输出口都到头（没接）。
        /// </summary>
        private static void BuildSplit(BeltKernel k, int tierIn, int tierOut, bool sinks = true, int srcInterval = 1, ushort item = 1)
        {
            AddLine(k, 0, 0, 5, BeltDir.East, tierIn);
            k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
            AddLine(k, 5, 1, 4, BeltDir.North, tierOut);
            AddLine(k, 5, -1, 4, BeltDir.South, tierOut);
            k.AddSource(1, 0, 0, item, srcInterval, BeltConst.Unlimited);
            if (sinks)
            {
                k.AddSink(2, 5, 5, BeltConst.Unlimited, 0);
                k.AddSink(3, 5, -5, BeltConst.Unlimited, 0);
            }
        }

        // ── K1 默认 1:1 ───────────────────────────────────────────────────────────

        private static void CheckSplitterEven()
        {
            using (BeltKernel k = NewKernel())
            {
                BuildSplit(k, 0, 0);
                BeltNodeInfo fresh = Node(k, 5, 0);
                k.StepMany(20 * 180); // 3 游戏分钟
                BeltNodeInfo n = Node(k, 5, 0);
                long l = n.SentL;
                long r = n.SentR;
                int inLeft = ItemsOnLine(k, 5, 1, 4, BeltDir.North);
                int inRight = ItemsOnLine(k, 5, -1, 4, BeltDir.South);
                bool branch = PortOf(k, 2).Total + inLeft == l && PortOf(k, 3).Total + inRight == r;
                bool inv = Invariants(k, out string why);
                Expect(fresh.Kind == BeltNodeKind.Splitter && fresh.RatioL == 1 && fresh.RatioR == 1 && fresh.PriorityOut == BeltSide.None
                       && fresh.FilterL == BeltConst.FilterAny && fresh.FilterR == BeltConst.FilterAny && fresh.OutLConnected && fresh.OutRConnected && fresh.InputConnected
                       && Math.Abs(l - r) <= 1 && l + r >= 155 && branch && n.Block == BeltBlock.None && k.BlockedSplitters == 0 && inv,
                    $"K1 分流器默认 1:1（FGR-LOG-022）：T1 满载输入 3 分钟（头一件约 20 秒后才走到分流器），左 {l} 件、右 {r} 件（差 ≤ 1）；每一侧 分出 = 送达 + 在途（{PortOf(k, 2).Total}+{inLeft} / {PortOf(k, 3).Total}+{inRight}）；" +
                    $"新放的分流器 = 1:1、不设优先口、两口放全部物品{Why(why)}");
                Expect(n.WindowSeconds >= 59f && Math.Abs(n.SentLPerMinute - 30f) <= 2f && Math.Abs(n.SentRPerMinute - 30f) <= 2f,
                    $"K1 实测分出窗口（悬停里的“实测比例”）：最近 {n.WindowSeconds:0} 秒 左 {n.SentLPerMinute:0.#} / 右 {n.SentRPerMinute:0.#} 件每分钟（输入 60 件每分钟）");
            }
        }

        // ── K2 比例 ───────────────────────────────────────────────────────────────

        private static void CheckSplitterRatios()
        {
            var parts = new List<string>();
            bool ok = true;
            foreach ((int a, int b) in new[] { (1, 1), (2, 1), (3, 1), (1, 2), (3, 2), (9, 1) })
            {
                using (BeltKernel k = NewKernel())
                {
                    BuildSplit(k, 2, 2);
                    BeltResult set = k.SetSplitter(5, 0, a, b, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny);
                    k.StepMany(20 * 120);
                    BeltNodeInfo n = Node(k, 5, 0);
                    long err = Math.Abs(n.SentL * b - n.SentR * a);
                    bool inv = Invariants(k, out string why);
                    bool good = set == BeltResult.Ok && n.SentL + n.SentR >= 440 && err <= Math.Max(a, b) && n.RatioL == a && n.RatioR == b && inv;
                    ok &= good;
                    parts.Add($"{a}:{b} → 左 {n.SentL} / 右 {n.SentR}{(good ? string.Empty : "（不符" + Why(why) + "）")}");
                }
            }
            using (BeltKernel k = NewKernel())
            {
                BuildSplit(k, 0, 0);
                var bad = new[]
                {
                    k.SetSplitter(5, 0, 0, 1, BeltSide.None, 0, 0), k.SetSplitter(5, 0, 1, 10, BeltSide.None, 0, 0),
                    k.SetSplitter(5, 0, 1, 1, (BeltSide)3, 0, 0), k.SetSplitter(4, 0, 1, 1, BeltSide.None, 0, 0), k.SetMergerPriority(5, 0, BeltSide.Left),
                };
                ok &= bad[0] == BeltResult.InvalidArgument && bad[1] == BeltResult.InvalidArgument && bad[2] == BeltResult.InvalidArgument
                      && bad[3] == BeltResult.NotFound && bad[4] == BeltResult.NotSupported;
                parts.Add($"非法设置：0 份 / 10 份 / 优先口 3 → {bad[0]} / {bad[1]} / {bad[2]}，不是分流器的格 → {bad[3]}，对分流器设合流器优先口 → {bad[4]}");
            }
            Expect(ok, "K2 分流比例（FGT-LOG-005“分流比例”）：T3 满载 2 分钟（约 480 件），左右件数逐件符合比例（|左×b − 右×a| ≤ max(a,b)）：" + string.Join("；", parts));
        }

        // ── K3 优先输出口与溢流 ───────────────────────────────────────────────────────

        private static void CheckSplitterPriority()
        {
            using (BeltKernel k = NewKernel())
            {
                BuildSplit(k, 0, 0);
                k.SetSplitter(5, 0, 1, 1, BeltSide.Left, BeltConst.FilterAny, BeltConst.FilterAny);
                k.StepMany(1200);
                BeltNodeInfo a = Node(k, 5, 0);
                bool allLeft = a.SentR == 0 && a.SentL >= 35; // 头一件约 20 秒后才到分流器
                // 左口没了输入口 → 左边那条带装满 → 溢流到右口。
                k.RemovePort(2);
                k.StepMany(2400);
                BeltNodeInfo b0 = Node(k, 5, 0);
                k.StepMany(1200);
                BeltNodeInfo b1 = Node(k, 5, 0);
                bool overflow = b1.SentL == b0.SentL && b1.SentR - b0.SentR >= 57 && b1.OutLState == BeltOutletState.Full
                                && ItemsOnLine(k, 5, 1, 4, BeltDir.North) == 16;
                // 左口恢复：优先口一有空位就只走它（排空后右口不再分到）。
                k.AddSink(2, 5, 5, BeltConst.Unlimited, 0);
                k.StepMany(2400);
                BeltNodeInfo c0 = Node(k, 5, 0);
                k.StepMany(1200);
                BeltNodeInfo c1 = Node(k, 5, 0);
                bool back = c1.SentR == c0.SentR && c1.SentL - c0.SentL >= 57;
                // 改成右口优先：立刻生效。
                k.SetSplitter(5, 0, 1, 1, BeltSide.Right, BeltConst.FilterAny, BeltConst.FilterAny);
                k.StepMany(400);
                BeltNodeInfo d0 = Node(k, 5, 0);
                k.StepMany(1200);
                BeltNodeInfo d1 = Node(k, 5, 0);
                bool right = d1.SentL == d0.SentL && d1.SentR - d0.SentR >= 57 && d1.PriorityOut == BeltSide.Right;
                bool inv = Invariants(k, out string why);
                Expect(allLeft && overflow && back && right && inv,
                    $"K3 优先输出口（FGR-LOG-022）：左口优先且有空位时全部走左口（左 {a.SentL} / 右 {a.SentR}）；左口的带装满后溢流到右口（1 分钟内右 +{b1.SentR - b0.SentR}、左 +{b1.SentL - b0.SentL}，分流器不停）；" +
                    $"左口恢复后回到只走左口（左 +{c1.SentL - c0.SentL}、右 +{c1.SentR - c0.SentR}）；改成右口优先立刻生效（右 +{d1.SentR - d0.SentR}、左 +{d1.SentL - d0.SentL}）{Why(why)}");
            }
        }

        // ── K4 每口过滤 ─────────────────────────────────────────────────────────────

        /// <summary>输入 10 格朝东，每格放 4 件交替的物品 1 / 2（共 40 件：各 20），分流器 (10,0) 朝东，左口朝北 3 格、右口朝南 3 格。</summary>
        private static void BuildSort(BeltKernel k, int leftAccept, int rightAccept, bool leftSink = true)
        {
            AddLine(k, 0, 0, 10, BeltDir.East, 0);
            k.AddNode(10, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
            AddLine(k, 10, 1, 3, BeltDir.North, 0);
            AddLine(k, 10, -1, 3, BeltDir.South, 0);
            if (leftSink)
            {
                k.AddSink(2, 10, 4, BeltConst.Unlimited, 0, 0, BeltConst.AnyFace, (ushort)leftAccept);
            }
            k.AddSink(3, 10, -4, BeltConst.Unlimited, 0, 0, BeltConst.AnyFace, (ushort)rightAccept);
            for (int x = 0; x < 10; x++)
            {
                for (int s = 0; s < 4; s++)
                {
                    k.InsertItemAt(x, 0, s * 12000, (ushort)(1 + ((x * 4 + s) & 1)));
                }
            }
        }

        private static void CheckSplitterFilters()
        {
            using (BeltKernel k = NewKernel())
            {
                // 左口只放 1、右口全部物品；两个输入口只收各自那一种——分错一件就会停在末端（原因“不收”），送达数凑不齐。
                BuildSort(k, 1, 2);
                k.SetSplitter(10, 0, 1, 1, BeltSide.None, 1, BeltConst.FilterAny);
                k.StepMany(3000);
                bool inv = Invariants(k, out string why);
                Expect(PortOf(k, 2).Total == 20 && PortOf(k, 3).Total == 20 && k.ItemCount == 0 && inv,
                    $"K4 分拣（FGR-LOG-022“每个输出口可以设置物品过滤”）：左口只放物品 1、右口放全部物品——“只放”的那种只走左口，其余走右口：左收 {PortOf(k, 2).Total}、右收 {PortOf(k, 3).Total}（各 20，没有一件分错）{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 左口只放 1 但左边没接输入口（会装满）：物品 1 在出口处等（严格分拣，不改走右口），后面的物品 2 也被挡住。
                BuildSort(k, 1, 0, leftSink: false);
                k.SetSplitter(10, 0, 1, 1, BeltSide.None, 1, BeltConst.FilterAny);
                k.StepMany(3000);
                BeltNodeInfo n = Node(k, 10, 0);
                int total = k.ItemCount + (int)PortOf(k, 3).Total;
                bool inv = Invariants(k, out string why);
                Expect(n.Block == BeltBlock.SplitterFull && n.OutLState == BeltOutletState.Full && n.OutRState == BeltOutletState.Filtered && n.HeadItem == 1
                       && PortOf(k, 3).Total < 20 && total == 40 && k.BlockedSplitters == 1 && inv,
                    $"K4 只放某种的口堵了：物品 {n.HeadItem} 停在出口等（{n.Block}：左口 {n.OutLState}、右口 {n.OutRState}），不改走右口；右口只收到 {PortOf(k, 3).Total} 件，共 {total} 件不丢{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 关闭左口：全部走右口。
                BuildSort(k, 0, 0);
                k.SetSplitter(10, 0, 1, 1, BeltSide.None, BeltConst.FilterNone, BeltConst.FilterAny);
                k.StepMany(3000);
                BeltNodeInfo closed = Node(k, 10, 0);
                bool okClosed = PortOf(k, 2).Total == 0 && PortOf(k, 3).Total == 40 && closed.OutLState == BeltOutletState.Filtered;
                // 两口都关 / 两口都只放不存在的物品：没有口收（SplitterNoOutlet），物品不动、不丢。
                k.InsertItemAt(0, 0, 0, 1);
                k.SetSplitter(10, 0, 1, 1, BeltSide.None, BeltConst.FilterNone, BeltConst.FilterNone);
                k.StepMany(1200);
                BeltNodeInfo none = Node(k, 10, 0);
                int held = k.ItemCount;
                k.SetSplitter(10, 0, 1, 1, BeltSide.None, 7, 7);
                k.StepMany(200);
                BeltNodeInfo other = Node(k, 10, 0);
                bool inv = Invariants(k, out string why);
                Expect(okClosed && none.Block == BeltBlock.SplitterNoOutlet && held == 1 && other.Block == BeltBlock.SplitterNoOutlet && k.ItemCount == 1 && inv,
                    $"K4 关闭的口（“不出”）：左口关闭时 40 件全走右口（左 {PortOf(k, 2).Total}）；两口都关 → {none.Block}；两口都只放物品 7 而来的是物品 1 → {other.Block}；物品留在出口不丢{Why(why)}");
            }
        }

        // ── K5 两个输出口都堵（FG03 第 5 节负向）──────────────────────────────────────────

        private static void CheckSplitterBothBlocked()
        {
            using (BeltKernel k = NewKernel())
            {
                BuildSplit(k, 0, 0, sinks: false);
                k.StepMany(6000);
                BeltNodeInfo n = Node(k, 5, 0);
                BeltCellInfo up = Info(k, 4, 0);
                long blockedBefore = PortOf(k, 1).BlockedSteps;
                int held = k.ItemCount;
                k.StepMany(400);
                bool still = k.ItemCount == held && PortOf(k, 1).BlockedSteps > blockedBefore && Node(k, 5, 0).SentL == n.SentL && Node(k, 5, 0).SentR == n.SentR;
                bool inv = Invariants(k, out string why);
                bool stopped = n.Block == BeltBlock.SplitterFull && n.OutLState == BeltOutletState.Full && n.OutRState == BeltOutletState.Full && k.BlockedSplitters == 1
                               && up.Block == BeltBlock.DownstreamFull && up.HasNext && up.NextX == 5 && up.NextY == 0
                               && ItemsOnLine(k, 5, 1, 4, BeltDir.North) == 16 && ItemsOnLine(k, 5, -1, 4, BeltDir.South) == 16 && n.Count == 4 && inv;
                // 接通左口：恢复流动，分流器不再停。
                k.AddSink(2, 5, 5, BeltConst.Unlimited, 0);
                k.StepMany(1200);
                BeltNodeInfo after = Node(k, 5, 0);
                bool resumed = after.SentL > n.SentL + 50 && after.SentR == n.SentR && Invariants(k, out _);
                Expect(stopped && still && resumed,
                    $"K5 负向“分流器两个输出口都堵”：两侧都到头 → 两侧各装满 16 件、分流器里 4 件，分流器停下（{n.Block}，左 {n.OutLState} / 右 {n.OutRState}），上游 (4,0) 堵塞（{up.Block}，下游 = 分流器）；" +
                    $"再跑 400 步件数不变（{held}）、输出端口一直推不上去；接通左口后恢复（1 分钟左 +{after.SentL - n.SentL}），右口仍满不分{Why(why)}");
            }
        }

        // ── K6 没接 / 错误的一侧 ───────────────────────────────────────────────────────

        private static void CheckDisconnectedAndWrongSide()
        {
            using (BeltKernel k = NewKernel())
            {
                // 只接右口。
                AddLine(k, 0, 0, 5, BeltDir.East, 0);
                k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
                AddLine(k, 5, -1, 4, BeltDir.South, 0);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.AddSink(3, 5, -5, BeltConst.Unlimited, 0);
                k.StepMany(1200);
                BeltNodeInfo n = Node(k, 5, 0);
                bool oneSide = n.SentL == 0 && n.SentR >= 35 && !n.OutLConnected && n.OutLState == BeltOutletState.Disconnected && n.OutLX == 5 && n.OutLY == 1
                               && n.Block == BeltBlock.None;
                // 一侧也没接：没有口收。
                k.RemoveCell(5, -1);
                k.StepMany(400);
                BeltNodeInfo none = Node(k, 5, 0);
                bool inv = Invariants(k, out string why);
                Expect(oneSide && none.Block == BeltBlock.SplitterNoOutlet && none.OutRState == BeltOutletState.Disconnected && inv,
                    $"K6 分流器只接右口：全部走右口（右 {n.SentR}、左 {n.SentL}），左口写“没接”与该铺的位置 ({n.OutLX},{n.OutLY})；两侧都没接 → {none.Block}，物品留在出口{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 从错误的一侧顶着节点：分流器侧面、合流器后方、地下出口、地下入口侧面；普通带头对头仍是“到头”。
                k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
                k.AddCell(5, 1, BeltDir.South, 0);
                k.InsertItemAt(5, 1, 30000, 1);
                k.AddNode(20, 0, BeltDir.East, 2, BeltNodeKind.Merger);
                k.AddCell(19, 0, BeltDir.East, 0);
                k.InsertItemAt(19, 0, 30000, 1);
                k.AddUnderground(30, 0, BeltDir.East, 0, 3);
                k.AddCell(33, 1, BeltDir.South, 0);
                k.InsertItemAt(33, 1, 30000, 1);
                k.AddCell(30, 1, BeltDir.South, 0);
                k.InsertItemAt(30, 1, 30000, 1);
                k.AddCell(40, 0, BeltDir.East, 0);
                k.AddCell(41, 0, BeltDir.West, 0);
                k.InsertItemAt(40, 0, 30000, 1);
                k.StepMany(200);
                BeltCellInfo a = Info(k, 5, 1);
                BeltCellInfo b = Info(k, 19, 0);
                BeltCellInfo c = Info(k, 33, 1);
                BeltCellInfo d = Info(k, 30, 1);
                BeltCellInfo e = Info(k, 40, 0);
                bool inv = Invariants(k, out string why);
                bool ok = a.Block == BeltBlock.WrongSide && a.FrontKind == BeltNodeKind.Splitter && a.FrontX == 5 && a.FrontY == 0 && !a.HasNext
                          && b.Block == BeltBlock.WrongSide && b.FrontKind == BeltNodeKind.Merger
                          && c.Block == BeltBlock.WrongSide && c.FrontKind == BeltNodeKind.UndergroundOut
                          && d.Block == BeltBlock.WrongSide && d.FrontKind == BeltNodeKind.UndergroundIn
                          && e.Block == BeltBlock.EndOfBelt && Node(k, 5, 0).OutLConnected == false && k.ItemCount == 5 && inv;
                Expect(ok, $"K6 从错误的一侧顶着节点：分流器侧面 → {a.Block}（前方 {a.FrontKind} ({a.FrontX},{a.FrontY})）、合流器后方 → {b.Block}（{b.FrontKind}）、" +
                           $"地下出口 → {c.Block}（{c.FrontKind}）、地下入口侧面 → {d.Block}（{d.FrontKind}）；指回分流器的带不算它的输出口；普通带头对头仍是 {e.Block}；物品都停在原地{Why(why)}");
            }
        }

        // ── K7 合流器 ──────────────────────────────────────────────────────────────

        /// <summary>合流器 (5,0) 朝东；左侧（北）一路从 (5,4) 朝南、右侧（南）一路从 (5,-4) 朝北；输出 (6..9,0) 朝东 T1 到输入口 (10,0)。源：左 1、右 2；输入口 3。</summary>
        private static void BuildMerge(BeltKernel k, int leftInterval = 1, int rightInterval = 1)
        {
            k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Merger);
            AddLine(k, 5, 4, 4, BeltDir.South, 0);
            AddLine(k, 5, -4, 4, BeltDir.North, 0);
            AddLine(k, 6, 0, 4, BeltDir.East, 0);
            k.AddSource(1, 5, 4, 1, leftInterval, BeltConst.Unlimited);
            k.AddSource(2, 5, -4, 2, rightInterval, BeltConst.Unlimited);
            k.AddSink(3, 10, 0, BeltConst.Unlimited, 0);
        }

        private static (long l, long r) Deltas(BeltKernel k, int steps)
        {
            long l0 = PortOf(k, 1).Total;
            long r0 = PortOf(k, 2).Total;
            k.StepMany(steps);
            return (PortOf(k, 1).Total - l0, PortOf(k, 2).Total - r0);
        }

        private static void CheckMerger()
        {
            using (BeltKernel k = NewKernel())
            {
                BuildMerge(k);
                k.StepMany(2400);
                (long l, long r) alt = Deltas(k, 2400);
                BeltNodeInfo n = Node(k, 5, 0);
                bool alternate = Math.Abs(alt.l - alt.r) <= 2 && alt.l + alt.r >= 117 && n.InLConnected && n.InRConnected && n.PriorityIn == BeltSide.None;
                k.SetMergerPriority(5, 0, BeltSide.Left);
                k.StepMany(2400);
                (long l, long r) prio = Deltas(k, 2400);
                BeltCellInfo waiting = Info(k, 5, -1);
                bool left = prio.r == 0 && prio.l >= 117 && waiting.Block == BeltBlock.MergeWait && Node(k, 5, 0).PriorityIn == BeltSide.Left;
                k.SetMergerPriority(5, 0, BeltSide.Right);
                k.StepMany(2400);
                (long l, long r) toRight = Deltas(k, 2400);
                bool right = toRight.l == 0 && toRight.r >= 117;
                bool inv = Invariants(k, out string why);
                Expect(alternate && left && right && inv,
                    $"K7 合流器（FGR-LOG-022）：两路 T1 满载进、T1 出（出口是瓶颈）——默认交替 2 分钟 左 {alt.l} / 右 {alt.r}；设左口优先 → 左 {prio.l} / 右 {prio.r}（右路等，原因 {waiting.Block}）；" +
                    $"改右口优先立刻生效 → 左 {toRight.l} / 右 {toRight.r}{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 优先口稀疏（左路 30 件每分钟）：优先口没货时另一路照常补空，出口仍跑满。
                BuildMerge(k, leftInterval: 40);
                k.SetMergerPriority(5, 0, BeltSide.Left);
                k.StepMany(2400);
                (long l, long r) sparse = Deltas(k, 2400);
                bool inv = Invariants(k, out string why);
                Expect(Math.Abs(sparse.l - 60) <= 2 && sparse.r >= 55 && sparse.l + sparse.r >= 117 && inv,
                    $"K7 优先口稀疏时不空等：左路（优先）30 件每分钟全部走出（2 分钟 {sparse.l}），右路补上空档（{sparse.r}），出口满载 {sparse.l + sparse.r} / 120{Why(why)}");
            }
        }

        // ── K8 地下传送带跨过另一条传送带 ─────────────────────────────────────────────────

        private static void CheckUndergroundCrossing()
        {
            var parts = new List<string>();
            bool ok = true;
            for (int tier = 0; tier < 3; tier++)
            {
                using (BeltKernel k = NewKernel())
                {
                    // 地下：源 (0,0) → (0..1,0) 朝东 → 入口 (2,0) → 地下 3..6 → 出口 (7,0) → (8..10,0) → 输入口 (11,0) 只收物品 1。
                    AddLine(k, 0, 0, 2, BeltDir.East, tier);
                    BeltResult r = k.AddUnderground(2, 0, BeltDir.East, tier, 5);
                    AddLine(k, 8, 0, 3, BeltDir.East, tier);
                    k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                    k.AddSink(2, 11, 0, BeltConst.Unlimited, 0, 0, BeltConst.AnyFace, 1);
                    // 地面上一条朝北的带从 x = 4 经过（正好在地下段上方）：源 (4,-3) 物品 9 → 输入口 (4,4) 只收物品 9。
                    AddLine(k, 4, -3, 7, BeltDir.North, tier);
                    k.AddSource(3, 4, -3, 9, 1, BeltConst.Unlimited);
                    k.AddSink(4, 4, 4, BeltConst.Unlimited, 0, 0, BeltConst.AnyFace, 9);
                    k.StepMany(20 * 120);
                    BeltPortInfo under = PortOf(k, 2);
                    BeltPortInfo cross = PortOf(k, 4);
                    BeltNodeInfo n = Node(k, 2, 0);
                    int expect = tier == 0 ? 60 : tier == 1 ? 120 : 240;
                    bool inv = Invariants(k, out string why);
                    bool good = r == BeltResult.Ok && Math.Abs(under.PerMinute - expect) <= 1.5f && Math.Abs(cross.PerMinute - expect) <= 1.5f
                                && n.Kind == BeltNodeKind.UndergroundIn && n.Distance == 5 && n.Intact && n.Capacity == 24 && n.ExitX == 7 && n.ExitY == 0
                                && Node(k, 7, 0).Kind == BeltNodeKind.UndergroundOut && Node(k, 7, 0).EntranceX == 2
                                && Info(k, 1, 0).Block != BeltBlock.SinkRejects && Info(k, 10, 0).Block != BeltBlock.SinkRejects && Info(k, 4, 3).Block != BeltBlock.SinkRejects && inv;
                    ok &= good;
                    parts.Add($"T{tier + 1}：地下 {under.PerMinute:0.#} / 地面 {cross.PerMinute:0.#} 件每分钟（设计 {expect}）{(good ? string.Empty : "（不符" + Why(why) + "）")}");
                }
            }
            Expect(ok, "K8 地下传送带跨过另一条传送带（FGR-LOG-023）：入口 → 地下 4 格 → 出口，正上方另一条带照常走；两路各自只收自己的物品（没有一件串线），三档吞吐都满：" + string.Join("；", parts));
        }

        // ── K9 地下负向 ────────────────────────────────────────────────────────────

        private static void CheckUndergroundNegatives()
        {
            using (BeltKernel k = NewKernel())
            {
                var r = new Dictionary<string, BeltResult>
                {
                    ["距离 0"] = k.AddUnderground(0, 0, BeltDir.East, 0, 0),
                    ["距离 33"] = k.AddUnderground(0, 0, BeltDir.East, 0, BeltConst.MaxUndergroundDistance + 1),
                    ["等级 3"] = k.AddUnderground(0, 0, BeltDir.East, 3, 4),
                    ["第一条"] = k.AddUnderground(0, 10, BeltDir.East, 0, 5),
                };
                r["同方向重叠"] = k.AddUnderground(2, 10, BeltDir.East, 0, 4);
                r["反方向重叠"] = k.AddUnderground(6, 10, BeltDir.West, 0, 3);
                r["垂直交叉"] = k.AddUnderground(2, 8, BeltDir.North, 0, 4);
                k.AddCell(20, 0, BeltDir.East, 0);
                r["入口被占"] = k.AddUnderground(20, 0, BeltDir.East, 0, 3);
                r["出口被占"] = k.AddUnderground(17, 0, BeltDir.East, 0, 3);
                r["拆地下段"] = k.RemoveCell(3, BeltDirs.UnderY(10, (int)BeltDir.East));
                r["入口原地转"] = k.SetDirection(0, 10, BeltDir.West);
                r["分流器改等级"] = k.AddNode(40, 0, BeltDir.East, 2, BeltNodeKind.Splitter) == BeltResult.Ok ? k.SetTier(40, 0, 0) : BeltResult.Ok;
                var expected = new Dictionary<string, BeltResult>
                {
                    ["距离 0"] = BeltResult.InvalidArgument, ["距离 33"] = BeltResult.TooFar, ["等级 3"] = BeltResult.InvalidTier, ["第一条"] = BeltResult.Ok,
                    ["同方向重叠"] = BeltResult.UndergroundOccupied, ["反方向重叠"] = BeltResult.UndergroundOccupied, ["垂直交叉"] = BeltResult.Ok,
                    ["入口被占"] = BeltResult.Occupied, ["出口被占"] = BeltResult.Occupied, ["拆地下段"] = BeltResult.NotSupported,
                    ["入口原地转"] = BeltResult.NotSupported, ["分流器改等级"] = BeltResult.NotSupported,
                };
                var wrong = expected.Where(e => r[e.Key] != e.Value).Select(e => $"{e.Key}={r[e.Key]}").ToList();
                bool inv = Invariants(k, out string why);
                Expect(wrong.Count == 0 && inv && k.NodeCount == 5,
                    $"K9 地下传送带负向：{expected.Count} 种情况都给稳定原因码（距离 0 / 超过硬上限 / 等级非法 / 同方向与反方向重叠 → {BeltResult.UndergroundOccupied} / 两端被占 / 拆地下段 / 原地转 / 分流器改等级 → {BeltResult.NotSupported}），" +
                    $"南北向与东西向可以交叉（不符：{string.Join("、", wrong)}）{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 拆任一端 = 拆整条，地下的物品按流向交还；改等级整条改。
                k.AddUnderground(0, 0, BeltDir.East, 0, 5);
                k.InsertItemAt(0, 0, 1000, 1);
                k.InsertItemAt(2, BeltDirs.UnderY(0, (int)BeltDir.East), 1000, 2);
                k.InsertItemAt(5, 0, 1000, 3);
                BeltResult tier = k.SetTier(5, 0, 2);
                bool allT3 = Enumerable.Range(0, 6).All(s => Info(k, s, s == 0 || s == 5 ? 0 : BeltDirs.UnderY(0, (int)BeltDir.East)).Tier == 2);
                int inside = Node(k, 0, 0).ItemsInside;
                var back = new List<ushort>();
                BeltResult rm = k.RemoveCell(5, 0, back);
                bool gone = !k.HasCell(0, 0) && !k.HasCell(5, 0) && !k.HasCell(2, BeltDirs.UnderY(0, (int)BeltDir.East)) && k.NodeCount == 0;
                bool inv = Invariants(k, out string why);
                Expect(tier == BeltResult.Ok && allT3 && rm == BeltResult.Ok && gone && inside == 3 && back.Count == 3 && back[0] == 1 && back[1] == 2 && back[2] == 3
                       && k.Ledger.Removed == 3 && inv,
                    $"K9 改等级整条改（入口、地下段、出口都成 T3）；拆出口 = 拆整条（两端与地下段都没了），{back.Count} 件物品按流向交还（{string.Join(",", back)}）并记“移出”{Why(why)}");
            }
        }

        // ── K10 地下出口堵住 ─────────────────────────────────────────────────────────

        private static void CheckUndergroundBackup()
        {
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 2, BeltDir.East, 0);
                k.AddUnderground(2, 0, BeltDir.East, 0, 5);
                k.AddSource(1, 0, 0, 1, 1, BeltConst.Unlimited);
                k.StepMany(6000);
                BeltNodeInfo n = Node(k, 2, 0);
                BeltCellInfo entrance = Info(k, 2, 0);
                BeltCellInfo exit = Info(k, 7, 0);
                int held = k.ItemCount;
                k.StepMany(400);
                bool full = n.ItemsInside == 24 && n.Capacity == 24 && exit.Block == BeltBlock.EndOfBelt && entrance.Block == BeltBlock.DownstreamFull
                            && entrance.NextUnderground && entrance.NextX == 7 && entrance.NextY == 0 && Info(k, 1, 0).Block == BeltBlock.DownstreamFull
                            && k.ItemCount == held && held == 24 + 8;
                AddLine(k, 8, 0, 2, BeltDir.East, 0);
                k.AddSink(2, 10, 0, BeltConst.Unlimited, 0);
                k.StepMany(2400);
                bool flows = PortOf(k, 2).PerMinute >= 58f;
                bool inv = Invariants(k, out string why);
                Expect(full && flows && inv,
                    $"K10 地下出口没接：地下段与两端装满 {n.ItemsInside} / {n.Capacity} 件，入口堵塞（{entrance.Block}，下游写出口 ({entrance.NextX},{entrance.NextY})，不写地下层坐标），上游也堵，" +
                    $"件数不变；出口接上后恢复满载（{PortOf(k, 2).PerMinute:0.#} 件每分钟）{Why(why)}");
            }
        }

        // ── K11 经过节点的环 / 回路 ─────────────────────────────────────────────────────

        private static void CheckLoopsThroughNodes()
        {
            using (BeltKernel k = NewKernel())
            {
                // 8×4 的矩形环，下边 (1,0)→(6,0) 是一条地下传送带（地下 4 格）。
                k.AddCell(0, 0, BeltDir.East, 0);
                k.AddUnderground(1, 0, BeltDir.East, 0, 5);
                for (int y = 0; y < 3; y++)
                {
                    k.AddCell(7, y, BeltDir.North, 0);
                }
                for (int x = 7; x > 0; x--)
                {
                    k.AddCell(x, 3, BeltDir.West, 0);
                }
                for (int y = 3; y > 0; y--)
                {
                    k.AddCell(0, y, BeltDir.South, 0);
                }
                var cells = new List<int3>();
                k.CollectCells(cells, includeUnderground: true);
                int total = 0;
                foreach (int3 c in cells)
                {
                    for (int s = 0; s < 4; s++)
                    {
                        if (k.InsertItemAt(c.x, c.y, s * 12000, (ushort)(1 + total)) == BeltResult.Ok)
                        {
                            total++;
                        }
                    }
                }
                ulong h0 = k.ComputeStateHash();
                BeltCellInfo before = Info(k, 0, 2);
                k.StepMany(600);
                BeltCellInfo after = Info(k, 0, 2);
                BeltCellInfo hidden = Info(k, 3, BeltDirs.UnderY(0, (int)BeltDir.East));
                bool inv = Invariants(k, out string why);
                bool rotates = k.ItemCount == total && k.ComputeStateHash() != h0 && Info(k, 0, 0).InLoop && hidden.InLoop
                               && (before.Item0 != after.Item0 || before.Pos0 != after.Pos0) && k.TryGetNetworkStats(k.NetworkOf(0, 0), out BeltNetworkStats st) && st.HasCycle;
                Expect(rotates && inv,
                    $"K11 经过地下段的环（FG03 第 5 节“环上的物品一直转”）：环含地下 4 格，装满 {total} 件，600 步后件数不变、物品整体转动（地下段也在环上），网络“有环”{Why(why)}");
            }
            using (BeltKernel k = NewKernel())
            {
                // 回路：分流器 (5,0) 朝东，左口回到它自己的输入（(5,1)→(5,2)→(4,2)→(3,2)→(3,1)→(3,0)→(4,0)→分流器），
                // 源从 (2,0) 从后方汇入 (3,0)；右口 (5,-1)→(5,-2) → 输入口 (5,-3)。
                k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
                k.AddCell(5, 1, BeltDir.North, 0);
                k.AddCell(5, 2, BeltDir.West, 0);
                k.AddCell(4, 2, BeltDir.West, 0);
                k.AddCell(3, 2, BeltDir.South, 0);
                k.AddCell(3, 1, BeltDir.South, 0);
                k.AddCell(3, 0, BeltDir.East, 0);
                k.AddCell(4, 0, BeltDir.East, 0);
                k.AddCell(2, 0, BeltDir.East, 0);
                AddLine(k, 5, -1, 2, BeltDir.South, 0);
                k.AddSource(1, 2, 0, 1, 1, BeltConst.Unlimited);
                k.AddSink(2, 5, -3, BeltConst.Unlimited, 0);
                k.StepMany(2400);
                long d0 = PortOf(k, 2).Total;
                k.StepMany(1200);
                bool flowing = PortOf(k, 2).Total - d0 >= 25;
                // 右口断开：回路被源一直填，填满后分流器停下（原因写明），物品不丢不增。
                k.RemovePort(2);
                k.StepMany(20000);
                BeltNodeInfo stuck = Node(k, 5, 0);
                int held = k.ItemCount;
                k.StepMany(400);
                bool stopped = stuck.Block == BeltBlock.SplitterFull && k.ItemCount == held && Invariants(k, out _);
                // 右口接回：回路恢复流动（不会永久卡死）。
                k.AddSink(2, 5, -3, BeltConst.Unlimited, 0);
                long r0 = PortOf(k, 2).Total;
                k.StepMany(2400);
                bool inv = Invariants(k, out string why);
                bool resumed = PortOf(k, 2).Total - r0 >= 40;
                Expect(flowing && stopped && resumed && inv,
                    $"K11 经过分流器的回路（左口绕回自己的输入，右口出去）：正常时右口持续出料；右口断开后回路被源填满（{held} 件），分流器停下（{stuck.Block}），件数不变；" +
                    $"右口接回后恢复（2 分钟出 {PortOf(k, 2).Total - r0} 件），不会永久卡死{Why(why)}");
            }
        }

        // ── K12 确定性与存档格式 ───────────────────────────────────────────────────────

        /// <summary>组合场景：分流器（3:1、左口只放 2）、合流器（右口优先）、地下传送带、环、源与输入口。<paramref name="seed"/> 打乱放置顺序。</summary>
        private static void BuildComposite(BeltKernel k, int seed)
        {
            var ops = new List<Action>
            {
                () => AddLine(k, 0, 0, 5, BeltDir.East, 1),
                () => k.AddNode(5, 0, BeltDir.East, 2, BeltNodeKind.Splitter),
                () => AddLine(k, 5, 1, 4, BeltDir.North, 0),
                () => AddLine(k, 5, -1, 3, BeltDir.South, 2),
                () => k.AddNode(5, -4, BeltDir.East, 2, BeltNodeKind.Merger),
                () => AddLine(k, 5, -8, 4, BeltDir.North, 0),
                () => AddLine(k, 6, -4, 2, BeltDir.East, 0),
                () => k.AddUnderground(8, -4, BeltDir.East, 1, 6),
                () => AddLine(k, 15, -4, 3, BeltDir.East, 1),
                () => AddLine(k, 20, 20, 6, BeltDir.East, 0),
            };
            if (seed != 0)
            {
                var rng = new System.Random(seed);
                ops = ops.OrderBy(_ => rng.Next()).ToList();
            }
            foreach (Action op in ops)
            {
                op();
            }
            k.SetSplitter(5, 0, 3, 1, BeltSide.None, 2, BeltConst.FilterAny);
            k.SetMergerPriority(5, -4, BeltSide.Right);
            k.AddSource(1, 0, 0, 2, 1, BeltConst.Unlimited);
            k.AddSource(2, 5, -8, 5, 3, 400);
            k.AddSink(3, 5, 5, 6, 9);
            k.AddSink(4, 18, -4, BeltConst.Unlimited, 0);
            for (int x = 0; x < 5; x++)
            {
                k.InsertItemAt(x, 0, 30000, (ushort)(1 + (x & 1)));
            }
        }

        private static void CheckDeterminismAndSnapshots()
        {
            var hashes = new List<ulong>();
            foreach (int seed in new[] { 0, 17, 99 })
            {
                using (BeltKernel k = NewKernel())
                {
                    BuildComposite(k, seed);
                    k.StepMany(3000);
                    hashes.Add(k.ComputeStateHash());
                }
            }
            ulong resumed;
            ulong direct;
            bool settings;
            string loadErr;
            string diff;
            using (BeltKernel a = NewKernel())
            using (BeltKernel b = NewKernel())
            {
                BuildComposite(a, 0);
                a.StepMany(1500);
                BeltSnapshot snap = a.Serialize();
                bool ok = b.Deserialize(snap, out loadErr);
                diff = FirstDiff(a, b);
                BeltNodeInfo na = Node(a, 5, 0);
                BeltNodeInfo nb = Node(b, 5, 0);
                settings = ok && loadErr == null && snap.FormatVersion == BeltKernel.FormatVersion && a.ComputeStateHash() == b.ComputeStateHash()
                           && nb.RatioL == 3 && nb.RatioR == 1 && nb.FilterL == 2 && nb.SentL == na.SentL && nb.SentR == na.SentR && nb.SentL > 0
                           && Node(b, 5, -4).PriorityIn == BeltSide.Right && Node(b, 8, -4).Distance == 6 && Node(b, 14, -4).Kind == BeltNodeKind.UndergroundOut;
                a.StepMany(1500);
                b.StepMany(1500);
                direct = a.ComputeStateHash();
                resumed = b.ComputeStateHash();
            }
            Expect(hashes.Distinct().Count() == 1 && settings && direct == resumed,
                $"K12 确定性（三种顺序哈希相同 {hashes.Distinct().Count() == 1}、往返设置 {settings}、续跑一致 {direct == resumed}；往返差别：{diff ?? "无"}）：含分流器 / 合流器 / 地下传送带的组合场景按三种放置顺序各跑 3,000 步，状态哈希相同；当前格式（{BeltKernel.FormatVersion}）往返后节点设置（3:1、左口只放 2、合流器右口优先、地下距离 6）与" +
                $"状态（比例轮次、累计分出件数）逐字段一致，接着跑 1,500 步与不存档一直跑哈希相同（{loadErr ?? "无问题"}）");

            // 格式 2 的旧网络块（FG3-LOG-03 存档：每格没有种类字节）照常读成普通传送带。
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 6, BeltDir.East, 1);
                k.AddSource(1, 0, 0, 3, 1, 60);
                k.AddSink(2, 6, 0, 4, 0);
                k.StepMany(300);
                BeltSnapshot snap = k.Serialize();
                ulong h = k.ComputeStateHash();
                foreach (BeltSnapshot.Chunk c in snap.Networks)
                {
                    c.Bytes = ToFormat2Chunk(c.Bytes);
                }
                snap.Ports[2] = 2;
                RewriteChecksum(snap.Ports);
                snap.FormatVersion = 2;
                using (BeltKernel d = NewKernel())
                {
                    bool ok = d.Deserialize(snap, out string err);
                    Expect(ok && err == null && d.ComputeStateHash() == h && d.CellCount == 6 && d.NodeCount == 0,
                        $"K12 格式 2 的旧网络块（没有种类字节）照常读成普通传送带，状态哈希与原内核一致（{err ?? "无问题"}）");
                }
            }

            // 地下传送带不完整（入口记的距离与出口对不上）的网络块整块丢弃，别的网络照常恢复，物品数计入“移出”。
            using (BeltKernel k = NewKernel())
            {
                AddLine(k, 0, 0, 2, BeltDir.East, 0);
                k.AddUnderground(2, 0, BeltDir.East, 0, 5);
                k.InsertItemAt(0, 0, 1000, 1);
                k.InsertItemAt(3, BeltDirs.UnderY(0, (int)BeltDir.East), 1000, 1);
                AddLine(k, 0, 10, 3, BeltDir.East, 0);
                k.InsertItemAt(1, 10, 1000, 4);
                BeltSnapshot snap = k.Serialize();
                int patched = 0;
                foreach (BeltSnapshot.Chunk c in snap.Networks)
                {
                    patched += PatchUndergroundSpan(c.Bytes, 6) ? 1 : 0;
                }
                using (BeltKernel d = NewKernel())
                {
                    bool ok = d.Deserialize(snap, out string err);
                    bool dropped = ok && patched == 1 && d.CorruptChunksDropped == 1 && err != null && err.Contains("chunk_node") && !d.HasCell(2, 0)
                                   && d.HasCell(1, 10) && d.Ledger.Balanced && d.Ledger.Removed == d.CorruptItemsDropped && d.CorruptItemsDropped == 2;
                    Expect(dropped, $"K12 地下传送带不完整的网络块（入口距离改成 6、出口仍记 5）整块丢弃（{err}），另一个网络照常恢复，{d.CorruptItemsDropped} 件计入“移出”、账本平衡");
                }
            }
        }

        /// <summary>把格式 3 的网络块（只含普通传送带：种类字节 = 0）改写成格式 2：去掉每格的种类字节，版本字节改 2，重算校验和。</summary>
        private static byte[] ToFormat2Chunk(byte[] v3)
        {
            var o = new List<byte>(v3.Length);
            o.AddRange(new[] { v3[0], v3[1], (byte)2, v3[3] });
            int cells = BitConverter.ToInt32(v3, 4);
            o.AddRange(v3.Skip(4).Take(8));
            int at = 12;
            for (int c = 0; c < cells; c++)
            {
                o.AddRange(v3.Skip(at).Take(11)); // x y dir tier turn
                at += 11;
                at += 1; // 种类（0 = 普通传送带，没有节点设置）
                int cnt = v3[at];
                o.Add(v3[at]);
                at += 1;
                for (int s = 0; s < cnt; s++)
                {
                    o.AddRange(v3.Skip(at).Take(6)); // 位置 + 种类；格式 3 多出的上一步位移（2 字节）去掉
                    at += 8;
                }
            }
            o.AddRange(BitConverter.GetBytes(Fnv32(o)));
            return o.ToArray();
        }

        /// <summary>找到网络块里的地下入口，把它记的距离改成 <paramref name="span"/> 并重算校验和。返回是否找到。</summary>
        private static bool PatchUndergroundSpan(byte[] b, int span)
        {
            int cells = BitConverter.ToInt32(b, 4);
            int at = 12;
            bool found = false;
            for (int c = 0; c < cells; c++)
            {
                at += 11;
                byte kind = b[at++];
                if (kind == (byte)BeltNodeKind.Splitter)
                {
                    at += 31;
                }
                else if (kind == (byte)BeltNodeKind.Merger)
                {
                    at += 1;
                }
                else if (kind == (byte)BeltNodeKind.UndergroundIn || kind == (byte)BeltNodeKind.UndergroundOut)
                {
                    if (kind == (byte)BeltNodeKind.UndergroundIn)
                    {
                        Array.Copy(BitConverter.GetBytes(span), 0, b, at, 4);
                        found = true;
                    }
                    at += 4;
                }
                int cnt = b[at++];
                at += cnt * 8;
            }
            if (found)
            {
                RewriteChecksum(b);
            }
            return found;
        }

        private static void RewriteChecksum(byte[] b)
        {
            uint sum = Fnv32(b.Take(b.Length - 4));
            Array.Copy(BitConverter.GetBytes(sum), 0, b, b.Length - 4, 4);
        }

        private static uint Fnv32(IEnumerable<byte> bytes)
        {
            uint h = 2166136261u;
            foreach (byte x in bytes)
            {
                h ^= x;
                h *= 16777619u;
            }
            return h;
        }

        // ── K13 渲染 ───────────────────────────────────────────────────────────────

        private static void CheckRender()
        {
            using (BeltKernel k = NewKernel())
            {
                k.AddCell(0, 0, BeltDir.East, 1);
                k.AddNode(1, 0, BeltDir.East, 2, BeltNodeKind.Splitter);
                k.AddNode(3, 0, BeltDir.North, 2, BeltNodeKind.Merger);
                k.AddUnderground(10, 0, BeltDir.East, 1, 5);
                k.InsertItemAt(0, 0, 1000, 1);
                k.InsertItemAt(12, BeltDirs.UnderY(0, (int)BeltDir.East), 1000, 2);
                k.InsertItemAt(13, BeltDirs.UnderY(0, (int)BeltDir.East), 1000, 2);
                k.PrepareRender(1f, out NativeArray<BeltInstance> cells, out int cellCount, out NativeArray<BeltInstance> items, out int itemCount, force: true);
                var kinds = new Dictionary<(float, float), float>();
                for (int i = 0; i < cellCount; i++)
                {
                    kinds[(cells[i].A.x, cells[i].A.y)] = cells[i].B.w;
                }
                float W(float x) => kinds.TryGetValue((x, 0f), out float w) ? w : -1f;
                float w0 = W(0f), w1 = W(1f), w2 = W(3f), w3 = W(10f), w4 = W(15f);
                bool codes = Mathf.Approximately(w0, 1f) && Mathf.Approximately(w1, 2f + 4f * 1f) && Mathf.Approximately(w2, 2f + 4f * 2f)
                             && Mathf.Approximately(w3, 1f + 4f * 3f) && Mathf.Approximately(w4, 1f + 4f * 4f);
                bool noUnder = cellCount == 5 && k.CellCount == 9 && itemCount == 1 && kinds.Keys.All(p => p.Item2 < 100f);
                // 状态没变时第二次准备不重填（地下段不画时格实例数少于格数，也不能被误判成“格数变了”）。
                int before = k.RenderItemFills;
                k.PrepareRender(1f, out _, out _, out _, out _);
                Expect(codes && noUnder && k.RenderItemFills == before,
                    $"K13 渲染：地下段（4 格）不画格也不画上面的 2 件物品（格实例 {cellCount} / 格 {k.CellCount}，物品实例 {itemCount}）；节点按种类编码（B.w = 等级 + 4 × 种类：分流器 {w1}、合流器 {w2}、入口 {w3}、出口 {w4}）；" +
                    "状态没变不重填缓冲");
            }
        }

        // ── K14 性能 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 85 个单元，每个 177 格：源 → 40 格 → 分流器（1:1）→ 左右各绕 23 格 → 合流器 → 1 格 → 地下传送带（地下 5 格）→ 81 格 → 输入口。
        /// 共 15,045 格（其中地下段 425 格）、340 个节点；每格（含地下段）预放 2 件，约 30,000 件。
        /// </summary>
        internal static void BuildHugeWithNodes(BeltKernel k, int ox, int oy)
        {
            for (int u = 0; u < 85; u++)
            {
                int y = oy + u * 5;
                int t = u % 3;
                AddLine(k, ox, y, 40, BeltDir.East, t);
                k.AddNode(ox + 40, y, BeltDir.East, 2, BeltNodeKind.Splitter);
                k.AddCell(ox + 40, y + 1, BeltDir.North, t);
                AddLine(k, ox + 40, y + 2, 20, BeltDir.East, t);
                k.AddCell(ox + 60, y + 2, BeltDir.South, t);
                k.AddCell(ox + 60, y + 1, BeltDir.South, t);
                k.AddCell(ox + 40, y - 1, BeltDir.South, t);
                AddLine(k, ox + 40, y - 2, 20, BeltDir.East, t);
                k.AddCell(ox + 60, y - 2, BeltDir.North, t);
                k.AddCell(ox + 60, y - 1, BeltDir.North, t);
                k.AddNode(ox + 60, y, BeltDir.East, 2, BeltNodeKind.Merger);
                k.AddCell(ox + 61, y, BeltDir.East, t);
                k.AddUnderground(ox + 62, y, BeltDir.East, t, 6);
                AddLine(k, ox + 69, y, 81, BeltDir.East, t);
                k.AddSource(5000 + u, ox, y, (ushort)(1 + u % 7), 1, BeltConst.Unlimited);
                k.AddSink(6000 + u, ox + 150, y, BeltConst.Unlimited, 0);
            }
            var cells = new List<int3>();
            k.CollectCells(cells, includeUnderground: true);
            foreach (int3 c in cells)
            {
                k.InsertItemAt(c.x, c.y, 3000, (ushort)(1 + (c.x & 7)));
                k.InsertItemAt(c.x, c.y, 27000, (ushort)(1 + (c.y & 7)));
            }
        }

        private static void CheckKernelPerformance()
        {
            using (BeltKernel k = NewKernel())
            {
                var build = Stopwatch.StartNew();
                BuildHugeWithNodes(k, 0, 0);
                k.EnsureTopology();
                build.Stop();
                double rebuild = k.LastRebuildMs;
                k.StepMany(200); // 预热
                var ms = new List<double>(600);
                for (int i = 0; i < 600; i++)
                {
                    k.Step();
                    ms.Add(k.LastStepMs);
                }
                ms.Sort();
                double avg = ms.Average();
                double p95 = ms[(int)(ms.Count * 0.95)];
                double max = ms[ms.Count - 1];
                var ser = Stopwatch.StartNew();
                BeltSnapshot snap = k.Serialize();
                ser.Stop();
                bool round;
                string roundDiff;
                double load;
                using (BeltKernel d = NewKernel())
                {
                    var sw = Stopwatch.StartNew();
                    round = d.Deserialize(snap, out string err) && err == null && d.ComputeStateHash() == k.ComputeStateHash();
                    roundDiff = round ? null : (err ?? string.Empty) + " " + FirstDiff(k, d);
                    sw.Stop();
                    load = sw.Elapsed.TotalMilliseconds;
                }
                int moving = 0;
                for (int u = 0; u < 85; u++)
                {
                    moving += PortOf(k, 6000 + u).Total > 0 ? 1 : 0;
                }
                bool inv = Invariants(k, out string why);
                string line = $"节点场景 {k.CellCount:N0} 格（含地下段 425 格）/ {k.NodeCount} 个节点 / {k.ItemCount:N0} 件：内核单步 600 步 平均 {avg:F3} ms、p95 {p95:F3} ms、最大 {max:F3} ms；" +
                              $"拓扑重建 {rebuild:F2} ms；序列化 {ser.Elapsed.TotalMilliseconds:F1} ms / {snap.TotalBytes / 1024} KB，读回 {load:F1} ms";
                PerfLines.Add(line);
                Expect(k.CellCount >= 15000 && k.NodeCount == 340 && k.ItemCount >= 29000 && p95 <= 2.0 && round && moving == 85 && inv,
                    $"K14 性能（FG03 第 7 节：单步 ≤ 2 ms）：{line}；{moving} / 85 个单元的输入口收到物品，格式 3 往返哈希一致 {round}{(roundDiff != null ? "（" + roundDiff + "）" : string.Empty)}{Why(why)}");
            }
        }

        // ── 正式：公共准备 ─────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgbeltnode-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static BuildingRecord Building(CampaignState s, string typeId) =>
            (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).FirstOrDefault(b => b != null && b.BuildingTypeId == typeId && b.RegionId == HomeValleyLayout.RegionId);

        private static void WaitSync() => WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);

        private static BuildingRecord ActivateWarehouse(CampaignState s)
        {
            BuildingRecord wh = Building(s, "warehouse");
            wh.ConstructionState = BuildingConstructionState.Operational;
            WaitSync();
            return wh;
        }

        private static BeltPortService.Binding Port(CampaignState s, string typeId, string portKey)
        {
            BuildingRecord b = Building(s, typeId);
            return b != null ? BeltPortService.Find(b.BuildingId, portKey) : null;
        }

        private static BeltPortInfo PortInfo(BeltPortService.Binding b)
        {
            BeltPortInfo info = default;
            if (b != null)
            {
                BeltNetworkService.Kernel.TryGetPortInfo(b.PortId, out info);
            }
            return info;
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

        private static bool Free(CampaignState s, GridCell c) => HomeGridService.ValidateBeltCell(s, c).Ok;

        /// <summary>从 <paramref name="from"/> 附近按圈找一块 w×h 全部能铺带的空地（左下角）。</summary>
        internal static GridCell? FindArea(CampaignState s, GridCell from, int w, int h, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var o = new GridCell(from.X + dx, from.Y + dy);
                        bool ok = true;
                        for (int y = 0; y < h && ok; y++)
                        {
                            for (int x = 0; x < w && ok; x++)
                            {
                                ok = Free(s, new GridCell(o.X + x, o.Y + y));
                            }
                        }
                        if (ok)
                        {
                            return o;
                        }
                    }
                }
            }
            return null;
        }

        private static BeltDir DirTo(GridCell a, GridCell b) =>
            b.X > a.X ? BeltDir.East : b.X < a.X ? BeltDir.West : b.Y > a.Y ? BeltDir.North : BeltDir.South;

        /// <summary>广度优先找一条只走能铺带的格的路线（固定 N/E/S/W 展开顺序，同一地图同一路线），每格朝向下一格，最后一格朝 <paramref name="lastDir"/>。</summary>
        private static List<(GridCell cell, BeltDir dir)> Route(CampaignState s, GridCell from, GridCell to, BeltDir lastDir, int margin = 16)
        {
            if (!Free(s, from) || !Free(s, to))
            {
                return null;
            }
            int minX = Math.Min(from.X, to.X) - margin, maxX = Math.Max(from.X, to.X) + margin;
            int minY = Math.Min(from.Y, to.Y) - margin, maxY = Math.Max(from.Y, to.Y) + margin;
            var parent = new Dictionary<GridCell, GridCell> { [from] = from };
            var q = new Queue<GridCell>();
            q.Enqueue(from);
            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { 1, 0, -1, 0 };
            while (q.Count > 0)
            {
                GridCell c = q.Dequeue();
                if (c == to)
                {
                    break;
                }
                for (int d = 0; d < 4; d++)
                {
                    var n = new GridCell(c.X + dx[d], c.Y + dy[d]);
                    if (n.X < minX || n.X > maxX || n.Y < minY || n.Y > maxY || parent.ContainsKey(n) || !Free(s, n))
                    {
                        continue;
                    }
                    parent[n] = c;
                    q.Enqueue(n);
                }
            }
            if (!parent.ContainsKey(to))
            {
                return null;
            }
            var cells = new List<GridCell>();
            for (GridCell c = to; ; c = parent[c])
            {
                cells.Add(c);
                if (c == from)
                {
                    break;
                }
            }
            cells.Reverse();
            var path = new List<(GridCell, BeltDir)>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                path.Add((cells[i], i + 1 < cells.Count ? DirTo(cells[i], cells[i + 1]) : lastDir));
            }
            return path;
        }

        private static bool Lay(CampaignState s, List<(GridCell cell, BeltDir dir)> path, int tier, out string failure)
        {
            failure = null;
            if (path == null)
            {
                failure = "找不到路线";
                return false;
            }
            foreach ((GridCell cell, BeltDir dir) in path)
            {
                BeltOpResult r = BeltNetworkService.TryPlace(s, cell, dir, tier);
                if (!r.Ok)
                {
                    failure = $"{cell}：{r.Describe()}";
                    return false;
                }
            }
            return true;
        }

        private static long Conserved(CampaignState s)
        {
            long ports = 0;
            foreach (BeltPortService.Binding b in BeltPortService.All)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(b.PortId, out int pending, out int buffered))
                {
                    ports += Math.Max(0, pending) + Math.Max(0, buffered);
                }
            }
            return s.Scrap + ports + BeltNetworkService.Kernel.CountItemsSlow() + GroundScrap(s);
        }

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        /// <summary>
        /// 家园里一组测试用的节点（测试捷径，经正式入口 TryPlace / TryPlaceNode / TryPlaceUnderground 含格网校验）：在 <paramref name="o"/> 起的 16×9 空地上
        /// 放一个分流器（左右各 3 格）、一个合流器（两路各 2 格进）、一条地下传送带（跨 3 格）。返回分流器 / 合流器 / 地下入口的格。
        /// </summary>
        internal static bool LayNodeSet(CampaignState s, GridCell o, out GridCell splitter, out GridCell merger, out GridCell under, out string failure)
        {
            failure = null;
            splitter = new GridCell(o.X + 3, o.Y + 4);
            merger = new GridCell(o.X + 9, o.Y + 4);
            under = new GridCell(o.X + 11, o.Y + 4);
            var belts = new List<(GridCell, BeltDir)>();
            for (int x = 0; x < 3; x++)
            {
                belts.Add((new GridCell(o.X + x, o.Y + 4), BeltDir.East));
            }
            for (int k = 1; k <= 3; k++)
            {
                belts.Add((new GridCell(splitter.X, splitter.Y + k), BeltDir.North));
                belts.Add((new GridCell(splitter.X, splitter.Y - k), BeltDir.South));
            }
            belts.Add((new GridCell(merger.X, merger.Y + 2), BeltDir.South));
            belts.Add((new GridCell(merger.X, merger.Y + 1), BeltDir.South));
            belts.Add((new GridCell(merger.X, merger.Y - 2), BeltDir.North));
            belts.Add((new GridCell(merger.X, merger.Y - 1), BeltDir.North));
            belts.Add((new GridCell(merger.X + 1, merger.Y), BeltDir.East));
            if (!Lay(s, belts, 0, out failure))
            {
                return false;
            }
            BeltOpResult a = BeltNetworkService.TryPlaceNode(s, splitter, BeltDir.East, 2, BeltNodeKind.Splitter);
            BeltOpResult b = BeltNetworkService.TryPlaceNode(s, merger, BeltDir.East, 2, BeltNodeKind.Merger);
            BeltOpResult c = BeltNetworkService.TryPlaceUnderground(s, under, BeltDir.East, 0, 4);
            if (!a.Ok || !b.Ok || !c.Ok)
            {
                failure = $"分流器 {a.Describe()} / 合流器 {b.Describe()} / 地下 {c.Describe()}";
                return false;
            }
            return true;
        }

        // ── F1 数据 ────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.TuningInt("logistics.underground.span_t1") == 4 && GridContent.TuningInt("logistics.underground.span_t2") == 6
                          && GridContent.TuningInt("logistics.underground.span_t3") == 8 && GridContent.TuningInt("logistics.splitter.custom_presets_max") == 8
                          && BeltNetworkService.UndergroundSpan(0) == 4 && BeltNetworkService.UndergroundSpan(2) == 8;
            Expect(tuning, "F1 新调参入表：地下传送带跨度 T1 4 / T2 6 / T3 8 格（FG03 第 10 章初值）、自定义预设最多 8 个");
            bool tools = BuildCatalog.TryGet("splitter", out BuildEntry sp) && sp.IsTool && sp.Tool.Kind == "splitter" && sp.CategoryId == "logistics" && sp.Tool.ScrapPerCell == 4
                         && BuildCatalog.TryGet("merger", out BuildEntry mg) && mg.Tool.Kind == "merger"
                         && Enumerable.Range(1, 3).All(t => BuildCatalog.TryGet("underground_t" + t, out BuildEntry u) && u.Tool.Kind == "underground" && u.Tool.Tier == t - 1)
                         && HomeGridService.PieceCost(BeltNodeKind.Splitter, 2) == 4 && HomeGridService.PieceRefund(BeltNodeKind.UndergroundIn, 0) == 10
                         && HomeGridService.PieceRefund(BeltNodeKind.UndergroundOut, 2) == 24;
            Expect(tools, "F1 建造菜单“物流”有分流器、合流器（每座 4 废料）与三级地下传送带（每端 5 / 8 / 12，拆除返还两端）");
            string[] hooks = { GuidanceHooks.LogisticsFirstSplitter, GuidanceHooks.LogisticsFirstMerger, GuidanceHooks.LogisticsFirstUnderground,
                GuidanceHooks.LogisticsSplitterFirstBlocked, GuidanceHooks.LogisticsNodePanelFirstOpen };
            GameConfig.fg.CodexEntry entry = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.logistics.splitter");
            Expect(hooks.All(h => GuidanceHooks.Known.Contains(h)) && entry != null && entry.Hooks.Contains(GuidanceHooks.LogisticsFirstSplitter)
                   && entry.Hooks.Contains(GuidanceHooks.LogisticsFirstUnderground) && entry.Links.Contains("codex.logistics.belt"),
                "F1 五个新引导钩子已登记（第一次放分流器 / 合流器 / 地下传送带、第一次分流器停下、第一次打开节点面板；引导内容在 FG15-UX-04）；图鉴有“分流器、合流器与地下传送带”条目（FG03 第 4 节），链接到传送带条目");
            var presets = new List<BeltNodeService.Preset>();
            BeltNodeService.CollectPresets(null, presets);
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var diffs = new List<string>();
            int rows = 0;
            if (code == 0)
            {
                foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
                {
                    string[] f = raw.Split('\t');
                    if (f.Length < 10 || f[0] != "FP")
                    {
                        continue;
                    }
                    rows++;
                    GameConfig.fg.BeltFilterPreset r = ConfigSystem.Instance.Tables.TbBeltFilterPreset.GetOrDefault(f[1]);
                    if (r == null || r.NameKey != f[2] || r.DescKey != f[3] || r.RatioL != int.Parse(f[4]) || r.RatioR != int.Parse(f[5]) || r.Priority != f[6]
                        || r.FilterL != f[7] || r.FilterR != f[8] || r.SortOrder != int.Parse(f[9]))
                    {
                        diffs.Add(f[1]);
                    }
                }
            }
            BeltNodeService.Preset even = presets.FirstOrDefault(p => p.Id == "split.even");
            BeltNodeService.Preset sort = presets.FirstOrDefault(p => p.Id == "split.sort_scrap_left");
            Expect(code == 0 && diffs.Count == 0 && rows == presets.Count && rows >= 9 && even != null && even.RatioL == 1 && sort != null && sort.FilterL == BeltItems.ScrapId
                   && sort.FilterR == BeltConst.FilterAny && presets.All(p => !string.IsNullOrEmpty(p.Name) && !GameText.ContainsMarker(p.Name)),
                $"F1 过滤器预设表（卡片“必须同时交付：过滤器预设”）：源数据 fgdata_logistics.py 与运行时 fg.TbBeltFilterPreset 逐字段一致（{rows} 行，{string.Join(",", diffs)}）；" +
                $"均分 1:1、分拣“废料走左口”解析为只放废料 / 全部");
            (code, output) = RunPython(LocateRepo(), "tools/cell_tables/check_luban.py --selftest");
            Expect(code == 0 && output.Contains("预设比例为 0") && output.Contains("地下传送带缺跨度调参"), $"F1 check_luban R33（过滤器预设与地下跨度）规则自测真跑：{Tail(output)}");
            string[] keys =
            {
                "logistics.node.splitter", "logistics.node.merger", "logistics.underground.t1", "logistics.underground.t3", "logistics.underground.entrance_name",
                "logistics.side.left", "logistics.filter.item", "logistics.reason.not_supported", "logistics.reason.underground_occupied", "logistics.reason.too_far",
                "logistics.reason.under_no_rotate", "logistics.reason.span_too_far", "logistics.block.wrong_side", "logistics.side_rule.splitter", "logistics.side_rule.under_out",
                "logistics.block.splitter_full", "logistics.block.splitter_no_outlet", "logistics.block.under_full", "logistics.outlet.full", "logistics.outlet.disconnected",
                "logistics.outlet.filtered", "logistics.outlet.closed", "logistics.hover.splitter_title", "logistics.hover.split_ratio", "logistics.hover.split_ratio_ignored",
                "logistics.hover.split_measured", "logistics.hover.split_measuring", "logistics.hover.merge_priority", "logistics.hover.under_span", "logistics.hover.under_inside",
                "logistics.hover.node_actions", "logistics.hover.under_actions", "logistics.hover.placeholder_node", "logistics.nodepanel.title_splitter", "logistics.nodepanel.preset",
                "logistics.nodepanel.preset_custom", "logistics.nodepanel.preset_full", "logistics.nodepanel.preset_delete_confirm", "logistics.nodepanel.hint_splitter",
                "logistics.nodepanel.hint_merger", "logistics.nodepanel.hint_under", "logistics.preset.even", "logistics.preset.sort_scrap_left.desc",
                "codex.logistics.splitter.title", "codex.logistics.splitter.body", "codex.logistics.splitter.hint", "build.tool.splitter.desc", "build.tool.underground_t2.desc",
                "ui.build.tool_cost_node", "ui.build.tool_cost_under", "ui.build.hint_splitter", "ui.build.hint_underground", "ui.build.drag_info_under", "ui.build.node_planned",
                "ui.build.node_rotated", "grid.reason.under_same_cell", "grid.reason.under_not_straight", "grid.reason.under_too_far", "grid.reason.under_occupied",
                "logistics.destroyed.notify_piece", "build.piece.done",
            };
            var missing = new List<string>();
            foreach (string key in keys)
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    string v = GameText.Get(key);
                    if (string.IsNullOrEmpty(v) || GameText.ContainsMarker(v))
                    {
                        missing.Add($"{key}[{lang}]");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(missing.Count == 0, $"F1 {keys.Length} 个新文本键中英齐全（缺：{string.Join(",", missing)}）");
        }

        // ── F2 建造菜单真实输入 ───────────────────────────────────────────────────────

        private static void CheckBuildModePlacement()
        {
            foreach (int seed in new[] { 9101, 9102, 9103 })
            {
                CampaignState s = NewWorld(seed, scrap: 400);
                HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
                var reader = new FakeReader();
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                try
                {
                    GridCell core = HomeGridService.CorePivot(s);
                    GridCell? area = FindArea(s, new GridCell(core.X - 14, core.Y + 22), 12, 5, 20);
                    if (mode == null || area == null)
                    {
                        Fail($"F2 种子 {seed}：找不到 12×5 的空地 / 建造模式没绑定");
                        continue;
                    }
                    GridCell a = area.Value;
                    mode.Open();
                    // 放置预览：选中分流器、鼠标指着一格 → 虚影 + 三个进出口箭头（后方进、左右出）。先把朝向转到东。
                    for (int i = 0; i < 4 && HomeGridService.BeltDirOf(mode.GhostRotation) != BeltDir.East; i++)
                    {
                        mode.RotateGhost();
                    }
                    mode.Select("splitter");
                    GridCell sp = new GridCell(a.X + 2, a.Y + 2);
                    mode.SetHover(s, sp);
                    mode.RefreshPreview(s);
                    mode.RefreshVisuals(s);
                    bool preview = mode.ToolPreview != null && mode.ToolPreview.Ok && mode.ToolPreview.Kind == BeltNodeKind.Splitter && mode.ActiveArrowCount == 3 && mode.ActiveTileCount == 1;
                    Vector3 inArrow = mode.ArrowForward(0);
                    mode.PointerDown(s, sp);
                    mode.PointerUp(s, sp);
                    bool planned = HomeValleyConstruction.TryFindPlannedCell(s, sp, out PlannedBeltRecord plan, out _) && plan.NodeKind == (int)BeltNodeKind.Splitter
                                   && plan.ScrapPerCell == 4 && mode.StatusText.Contains("分流器");
                    // 合流器：先按旋转键转成朝北再放。
                    mode.Select("merger");
                    GridCell mg = new GridCell(a.X + 6, a.Y + 2);
                    mode.SetHover(s, mg);
                    reader.Press(KeyCode.R);
                    mode.Tick(null, s, true);
                    Frame(reader);
                    BeltDir turned = HomeGridService.BeltDirOf(mode.GhostRotation);
                    mode.PointerDown(s, mg);
                    mode.PointerUp(s, mg);
                    bool mergerPlanned = turned == BeltDir.South && HomeValleyConstruction.TryFindPlannedCell(s, mg, out PlannedBeltRecord mplan, out _)
                                         && mplan.NodeKind == (int)BeltNodeKind.Merger && mplan.Dirs[0] == (int)BeltDir.South;
                    // 地下传送带沿拖动方向，与旋转键无关；转回东，后面的放置都朝东。
                    for (int i = 0; i < 4 && HomeGridService.BeltDirOf(mode.GhostRotation) != BeltDir.East; i++)
                    {
                        mode.RotateGhost();
                    }
                    // 地下传送带：没拖（单击）→ 原因“要拖到出口”；斜着拖 → “只能走直线”；跨 6 格（T1 上限 4）→ “跨度超限”；都不放东西。
                    mode.Select("underground_t1");
                    GridCell u0 = new GridCell(a.X, a.Y);
                    mode.PointerDown(s, u0);
                    mode.PointerUp(s, u0);
                    string clickReason = mode.StatusText;
                    mode.PointerDown(s, u0);
                    mode.SetHover(s, new GridCell(a.X + 3, a.Y + 2));
                    string diagonal = mode.BeltPlan?.Describe() ?? string.Empty;
                    mode.PointerUp(s, new GridCell(a.X + 3, a.Y + 2));
                    mode.PointerDown(s, u0);
                    mode.SetHover(s, new GridCell(a.X + 7, a.Y));
                    string tooFar = mode.BeltPlan?.Describe() ?? string.Empty;
                    bool drawnUnder = mode.BeltPlan != null && mode.BeltPlan.Kind == BeltNodeKind.UndergroundIn && mode.BeltPlan.Distance == 7 && mode.BeltPlan.MaxSpan == 4 && !mode.BeltPlan.Ok;
                    mode.PointerUp(s, new GridCell(a.X + 7, a.Y));
                    string tooFarStatus = mode.StatusText;
                    bool nothing = !HomeValleyConstruction.TryFindPlannedCell(s, u0, out _, out _);
                    // 正确地拖：跨 4 格（上限）。
                    mode.PointerDown(s, u0);
                    mode.SetHover(s, new GridCell(a.X + 5, a.Y));
                    mode.RefreshVisuals(s);
                    int tiles = mode.ActiveTileCount;
                    mode.PointerUp(s, new GridCell(a.X + 5, a.Y));
                    bool underPlanned = HomeValleyConstruction.TryFindPlannedCell(s, u0, out PlannedBeltRecord uplan, out _) && HomeValleyConstruction.IsUnderground(uplan)
                                        && uplan.Xs.Length == 2 && uplan.Xs[1] == a.X + 5 && uplan.Dirs[0] == (int)BeltDir.East;
                    // 同方向重叠（虚影阶段）：从 (a.X−1, a.Y) 朝东跨 2 格的地下段会撞上刚放的那份虚影规划的地下段。
                    bool overlapPlanned = HomeGridService.TryFindUndergroundOverlap(s, new GridCell(a.X - 1, a.Y), BeltDir.East, 3, out GridCell hit) && hit.X == a.X + 1;
                    bool built = StepUntil(() => BeltNetworkService.Kernel.TryGetKind(sp.X, sp.Y, out BeltNodeKind k1) && k1 == BeltNodeKind.Splitter
                                                 && BeltNetworkService.Kernel.TryGetKind(mg.X, mg.Y, out BeltNodeKind k2) && k2 == BeltNodeKind.Merger
                                                 && BeltNetworkService.Kernel.TryGetUndergroundEnds(u0.X, u0.Y, out _, out _, out int dist) && dist == 5, 400);
                    Expect(preview && inArrow.x > 0.5f && planned && mergerPlanned,
                        $"F2 [种子 {seed}] 选中分流器：鼠标指着的格有虚影与 3 个进出口箭头（后方进、左右出；进口箭头朝东 {inArrow}），单击放下分流器虚影（每座 4 废料）；" +
                        $"合流器按旋转键（R）转向后放下（朝 {turned}）");
                    Expect(clickReason.Contains("拖到出口") && diagonal.Contains("直线") && tooFar.Contains("跨度超限") && tooFar.Contains("最多跨 4 格") && tooFar.Contains("跨 6 格")
                           && drawnUnder && tooFarStatus.Contains("跨度超限") && nothing,
                        $"F2 [种子 {seed}] 负向“地下带跨度超限”（FGR-LOG-023）：单击“{clickReason}”；斜拖“{diagonal}”；拖 7 格（跨 6）时预览标红“{tooFar}”，松开也不放（“{tooFarStatus}”）");
                    Expect(underPlanned && tiles >= 2 + 4 && overlapPlanned,
                        $"F2 [种子 {seed}] 正确地拖出地下传送带 T1（入口 → 跨 4 格 → 出口）：两端一份规划、朝东，拖的时候画出地下段；同方向的虚影规划也不能重叠（在 ({hit.X},{hit.Y}) 撞上）");
                    Expect(built, $"F2 [种子 {seed}] 机器取料施工：分流器、合流器、地下传送带（两端一起）都建成进内核");
                    if (seed == 9101)
                    {
                        // 取消地下虚影：拆除模式点一端 = 两端一起取消、全额退回（还没取料 → 库存不变）。
                        mode.Select("underground_t1");
                        GridCell c0 = new GridCell(a.X, a.Y + 4);
                        mode.PointerDown(s, c0);
                        mode.SetHover(s, new GridCell(a.X + 3, a.Y + 4));
                        mode.PointerUp(s, new GridCell(a.X + 3, a.Y + 4));
                        GameClock.SetPaused(true);
                        int scrap = s.Scrap + GroundScrap(s);
                        mode.SetDemolishMode(true);
                        mode.ClickCell(s, new GridCell(a.X + 3, a.Y + 4));
                        bool bothGone = !HomeValleyConstruction.TryFindPlannedCell(s, c0, out _, out _) && !HomeValleyConstruction.TryFindPlannedCell(s, new GridCell(a.X + 3, a.Y + 4), out _, out _)
                                        && HomeGridService.MapFor(s).GetBelt(c0) == 0;
                        GameClock.SetPaused(false);
                        Expect(bothGone && s.Scrap + GroundScrap(s) == scrap, $"F2 取消地下传送带虚影：拆除模式点出口那一端，两端一起取消（格网标记清空），材料不丢（{scrap} → {s.Scrap + GroundScrap(s)}）");
                        // 穿过归还核心：T2（跨 6）从核心西边的通道拖到东边的通道。
                        GridCell? west = null;
                        GridCell? east = null;
                        for (int dy = -2; dy <= 2 && west == null; dy++)
                        {
                            for (int off = 3; off <= 4 && west == null; off++)
                            {
                                var w = new GridCell(core.X - off, core.Y + dy);
                                var e = new GridCell(core.X + off, core.Y + dy);
                                if (Free(s, w) && Free(s, e) && HomeGridService.BuildingAt(s, new GridCell(core.X, core.Y + dy)) != null)
                                {
                                    west = w;
                                    east = e;
                                }
                            }
                        }
                        mode.SetDemolishMode(false);
                        mode.Select("underground_t2");
                        bool underCore = false;
                        if (west != null)
                        {
                            mode.PointerDown(s, west.Value);
                            mode.SetHover(s, east.Value);
                            mode.PointerUp(s, east.Value);
                            underCore = StepUntil(() => BeltNetworkService.Kernel.TryGetUndergroundEnds(west.Value.X, west.Value.Y, out _, out int2 ex, out _) && ex.x == east.Value.X, 400);
                        }
                        Expect(underCore, $"F2 地下传送带跨过建筑（FGR-LOG-023“用来跨越其他传送带或建筑”）：T2 从归还核心西侧通道 {west} 拖到东侧通道 {east}，建成后从核心下面穿过");
                    }
                }
                finally
                {
                    mode?.Close();
                    InputRouter.DebugSetReader(null);
                }
            }
        }

        // ── F3 家园真实端口 ──────────────────────────────────────────────────────────

        private static void CheckHomePortsWithSplitter()
        {
            CampaignState s = NewWorld(9201, scrap: 150);
            ActivateWarehouse(s);
            BeltPortService.Binding whOut = Port(s, "warehouse", "warehouse.out0");
            BeltPortService.Binding whIn = Port(s, "warehouse", "warehouse.in0");
            BeltPortService.Binding coreIn = Port(s, "core", "core.in0");
            if (whOut == null || whIn == null || coreIn == null)
            {
                Fail("F3 仓库 / 核心端口没有登记");
                return;
            }
            // 分流器放在仓库输出口外侧往外 3 格处（按朝外方向），它的后方就是来料，两侧各先铺一格占住。
            var face = (BeltDir)(int)whOut.Face;
            GridCell sp = default;
            bool found = false;
            for (int dist = 3; dist <= 8 && !found; dist++)
            {
                var c = new GridCell(whOut.BeltCell.X + BeltDirs.Dx((int)face) * dist, whOut.BeltCell.Y + BeltDirs.Dy((int)face) * dist);
                var l = new GridCell(c.X + BeltDirs.Dx(BeltDirs.Left((int)face)), c.Y + BeltDirs.Dy(BeltDirs.Left((int)face)));
                var r = new GridCell(c.X + BeltDirs.Dx(BeltDirs.Right((int)face)), c.Y + BeltDirs.Dy(BeltDirs.Right((int)face)));
                if (Free(s, c) && Free(s, l) && Free(s, r))
                {
                    sp = c;
                    found = true;
                }
            }
            if (!found)
            {
                Fail("F3 仓库输出口外侧找不到放分流器的空地");
                return;
            }
            var back = new GridCell(sp.X - BeltDirs.Dx((int)face), sp.Y - BeltDirs.Dy((int)face));
            List<(GridCell cell, BeltDir dir)> feed = Route(s, whOut.BeltCell, back, face);
            bool ok = Lay(s, feed, 1, out string f1) && BeltNetworkService.TryPlaceNode(s, sp, face, 2, BeltNodeKind.Splitter).Ok;
            int left = BeltDirs.Left((int)face);
            int right = BeltDirs.Right((int)face);
            var lc = new GridCell(sp.X + BeltDirs.Dx(left), sp.Y + BeltDirs.Dy(left));
            var rc = new GridCell(sp.X + BeltDirs.Dx(right), sp.Y + BeltDirs.Dy(right));
            ok &= BeltNetworkService.TryPlace(s, lc, (BeltDir)left, 1).Ok && BeltNetworkService.TryPlace(s, rc, (BeltDir)right, 1).Ok;
            var lNext = new GridCell(lc.X + BeltDirs.Dx(left), lc.Y + BeltDirs.Dy(left));
            var rNext = new GridCell(rc.X + BeltDirs.Dx(right), rc.Y + BeltDirs.Dy(right));
            // 左口 → 仓库输入口、右口 → 核心输入口（末端正对着端口）。
            List<(GridCell cell, BeltDir dir)> toWh = Route(s, lNext, whIn.BeltCell, (BeltDir)(((int)whIn.Face + 2) & 3));
            ok &= Lay(s, toWh, 1, out string f2);
            List<(GridCell cell, BeltDir dir)> toCore = Route(s, rNext, coreIn.BeltCell, (BeltDir)(((int)coreIn.Face + 2) & 3), 24);
            ok &= Lay(s, toCore, 1, out string f3);
            if (!ok)
            {
                Fail($"F3 铺不下仓库 → 分流器 → 仓库 / 核心：{f1} {f2} {f3}");
                return;
            }
            BeltNetworkService.TrySetSplitter(s, sp, 3, 1, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny);
            long total = Conserved(s);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            long wh0 = PortInfo(whIn).Total;
            long core0 = PortInfo(coreIn).Total;
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            long dWh = PortInfo(whIn).Total - wh0;
            long dCore = PortInfo(coreIn).Total - core0;
            BeltNetworkService.TryGetNode(sp, out BeltNodeInfo n);
            bool ratio = dCore > 0 && Math.Abs(dWh - 3 * dCore) <= 6 && dWh + dCore >= 100;
            Expect(ratio && Conserved(s) == total && Math.Abs(n.SentL - n.SentR * 3) <= 3 && BeltNetworkService.Kernel.Ledger.Balanced,
                $"F3 家园真实端口：仓库输出口 → 分流器（3:1）→ 左口回仓库输入口、右口进归还核心输入口；2 分钟里仓库收 {dWh}、核心收 {dCore}（3:1），" +
                $"“库存 + 端口 + 带上 + 地面” = {total} 全程守恒（分流器累计 左 {n.SentL} / 右 {n.SentR}）");
        }

        // ── F4 悬停与原因文字 ─────────────────────────────────────────────────────────

        private static void CheckHoverTexts()
        {
            CampaignState s = NewWorld(9301, scrap: 200);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? area = FindArea(s, new GridCell(core.X - 16, core.Y + 20), 16, 9, 24);
            string failure = null;
            if (area == null || !LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out failure))
            {
                Fail("F4 家园里放不下测试用的节点：" + (area == null ? "没有空地" : failure));
                return;
            }
            GridCell o = area.Value;
            BeltKernel k = BeltNetworkService.Kernel;
            BeltNetworkService.TrySetSplitter(s, sp, 3, 1, BeltSide.None, BeltItems.ScrapId, BeltConst.FilterAny);
            BeltNetworkService.TryAddSource(s, 90001, new GridCell(o.X, o.Y + 4), BeltItems.ScrapId, 1, BeltConst.Unlimited);
            BeltNetworkService.TryAddSink(s, 90002, new GridCell(sp.X, sp.Y + 4), BeltConst.Unlimited, 0);
            BeltNetworkService.TryAddSink(s, 90003, new GridCell(sp.X, sp.Y - 4), BeltConst.Unlimited, 0);
            WorldSimulation.StepMany(GameClock.StepHz * 70);
            BeltNetworkService.TryDescribeHover(s, sp, out string title, out string body);
            bool splitterHover = title.Contains("分流器") && body.Contains("分流比例 左 : 右 = 3 : 1") && body.Contains("优先输出口：不设") && body.Contains("左口只放废料")
                                 && body.Contains("实测分出") && body.Contains("件/分钟") && body.Contains("输出口：") && body.Contains("进料：后方已接传送带") && !GameText.ContainsMarker(title + body);
            Expect(splitterHover, $"F4 分流器悬停（卡片“分流比例在悬停中显示”；FGR-LOG-081）：“{title}”{body.Replace("\n", " / ")}");
            // 两个口都堵（把两个输入口都删掉）：原因逐口写明。
            BeltNetworkService.TryRemovePort(s, 90002);
            BeltNetworkService.TryRemovePort(s, 90003);
            bool stuck = StepUntil(() => k.TryGetCellInfo(sp.X, sp.Y, out BeltCellInfo c) && c.Block == BeltBlock.SplitterFull, 120);
            WorldSimulation.StepMany(GameClock.StepHz * 10); // 让上游也堵满
            k.TryGetCellInfo(sp.X, sp.Y, out BeltCellInfo spc);
            string full = BeltNetworkService.DescribeBlock(spc);
            k.TryGetCellInfo(o.X + 2, o.Y + 4, out BeltCellInfo upc);
            string upstream = BeltNetworkService.DescribeBlock(upc);
            bool hookRaised = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsSplitterFirstBlocked);
            Expect(stuck && full.Contains("能出的口都满了") && full.Contains("左口") && full.Contains("已满") && upstream.Contains($"({sp.X}, {sp.Y})") && hookRaised,
                $"F4 负向“分流器两个输出口都堵”（FG03 第 5 节）：分流器原因“{full}”；上游原因“{upstream}”；第一次分流器停下的引导钩子已发");
            BeltNetworkService.TrySetMergerPriority(s, mg, BeltSide.Left);
            BeltNetworkService.TryDescribeHover(s, mg, out string mt, out string mb);
            BeltNetworkService.TryDescribeHover(s, un, out string ut, out string ub);
            bool others = mt.Contains("合流器") && mb.Contains("优先输入口：左口") && mb.Contains("左侧进料：已接") && ut.Contains("地下传送带 T1") && ut.Contains("入口")
                          && ub.Contains("地下跨 3 格") && ub.Contains("最多跨 4 格") && ub.Contains("拆掉任意一端");
            Expect(others, $"F4 合流器悬停“{mt}：{mb.Replace("\n", " / ")}”；地下传送带悬停“{ut}：{ub.Replace("\n", " / ")}”");
            // 错误的一侧：在分流器正上方的输出格上改成朝南（顶着分流器的侧面）。
            GridCell top = new GridCell(sp.X, sp.Y + 1);
            k.ClearCell(top.X, top.Y);
            BeltNetworkService.TrySetDirection(s, top, BeltDir.South);
            k.InsertItemAt(top.X, top.Y, 1000, BeltItems.ScrapId);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            k.TryGetCellInfo(top.X, top.Y, out BeltCellInfo wc);
            string wrong = BeltNetworkService.DescribeBlock(wc);
            Expect(wc.Block == BeltBlock.WrongSide && wrong.Contains("分流器") && wrong.Contains("只从后方进料"), $"F4 从侧面顶着分流器的带：原因“{wrong}”");
        }

        // ── F5 节点面板（真 UXML）──────────────────────────────────────────────────────

        private static void CheckNodePanel()
        {
            CampaignState s = NewWorld(9401, scrap: 200);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? area = FindArea(s, new GridCell(core.X - 16, core.Y + 20), 16, 9, 24);
            string failure = null;
            if (area == null || !LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out failure))
            {
                Fail("F5 家园里放不下测试用的节点：" + (area == null ? "没有空地" : failure));
                return;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            VisualElement root = MountUxml(UiKitFolder + "BeltNodePanel.uxml", out GameObject go);
            BeltNodePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                BeltNodePanelUIToolkit panel = go.AddComponent<BeltNodePanelUIToolkit>();
                panel.BindView(root);
                mode.Open();
                mode.PointerDown(s, sp);
                mode.PointerUp(s, sp);
                panel.Refresh();
                bool opened = BeltNodePanelUIToolkit.IsOpen && panel.PanelVisible && BeltNodePanelUIToolkit.Cell == sp && panel.SplitterSettingsVisible && !panel.MergerSettingsVisible
                              && panel.TitleText.Contains("分流器") && panel.DetailText.Contains("分流比例") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsNodePanelFirstOpen)
                              && panel.RatioLField.choices.Count == 9 && panel.PriorityOutField.choices.Count == 3 && panel.FilterLField.choices.Count >= 3
                              && !GameText.ContainsMarker(panel.TitleText + panel.DetailText + panel.HintText + panel.StateText);
                Expect(opened, $"F5 建造模式里点一下分流器打开节点面板（真 UXML）：“{panel.TitleText}”，{panel.StateText}；比例 1～9、优先口 3 项、过滤 {panel.FilterLField.choices.Count} 项");
                // 下拉框选中即生效。
                panel.RatioLField.value = "3";
                panel.PriorityOutField.value = panel.PriorityOutField.choices[1];
                panel.FilterRField.value = panel.FilterRField.choices[panel.FilterRField.choices.Count - 1];
                BeltNetworkService.TryGetNode(sp, out BeltNodeInfo n1);
                bool applied = n1.RatioL == 3 && n1.RatioR == 1 && n1.PriorityOut == BeltSide.Left && n1.FilterR == BeltConst.FilterNone && panel.MessageText.Contains("设置已更新");
                Expect(applied, $"F5 下拉框选中即生效：左口份数 3、优先输出口左口、右口过滤“关闭”→ 内核里 {n1.RatioL}:{n1.RatioR}、优先 {n1.PriorityOut}、右口 {n1.FilterR}（“{panel.MessageText}”）");
                // 预设：套用“左 1 : 右 2” → 存为自定义 → 列表里有它 → 删除先确认。
                int oneTwo = panel.PresetField.choices.FindIndex(c => c.Contains("左 1 : 右 2"));
                if (oneTwo > 0)
                {
                    panel.PresetField.value = panel.PresetField.choices[oneTwo];
                }
                BeltNetworkService.TryGetNode(sp, out BeltNodeInfo n2);
                bool preset = oneTwo > 0 && n2.RatioL == 1 && n2.RatioR == 2 && n2.PriorityOut == BeltSide.None && n2.FilterR == BeltConst.FilterAny && panel.MessageText.Contains("已套用预设");
                BeltNetworkService.TrySetSplitter(s, sp, 2, 3, BeltSide.Right, BeltItems.ScrapId, BeltConst.FilterAny);
                panel.Refresh();
                bool saved = panel.SaveCustomPreset() && s.Belts.FilterPresets.Length == 1 && s.Belts.FilterPresets[0].RatioL == 2 && s.Belts.FilterPresets[0].Priority == (int)BeltSide.Right
                             && panel.PresetField.choices.Any(c => c.StartsWith("自定义 1")) && panel.PresetDeleteField.choices.Count == 2;
                BeltNetworkService.TrySetSplitter(s, sp, 1, 1, BeltSide.None, 0, 0);
                panel.Refresh();
                panel.PresetField.value = panel.PresetField.choices.First(c => c.StartsWith("自定义 1"));
                BeltNetworkService.TryGetNode(sp, out BeltNodeInfo n3);
                bool customApplied = n3.RatioL == 2 && n3.RatioR == 3 && n3.PriorityOut == BeltSide.Right && n3.FilterL == BeltItems.ScrapId;
                for (int i = 0; i < BeltNodeService.CustomMax; i++)
                {
                    panel.SaveCustomPreset();
                }
                bool capped = s.Belts.FilterPresets.Length == BeltNodeService.CustomMax && panel.MessageText.Contains("最多");
                panel.PresetDeleteField.value = panel.PresetDeleteField.choices[1];
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title.Contains("自定义 1");
                UiConfirmDialog.Cancel();
                bool keptOnCancel = s.Belts.FilterPresets.Length == BeltNodeService.CustomMax;
                panel.AskDeleteCustom(1);
                UiConfirmDialog.Confirm();
                bool deleted = s.Belts.FilterPresets.Length == BeltNodeService.CustomMax - 1 && s.Belts.FilterPresets.All(p => p.Serial != 1);
                Expect(preset && saved && customApplied && capped && asked && keptOnCancel && deleted,
                    $"F5 过滤器预设：选“左 1 : 右 2”立刻套用；“存为自定义预设”记进存档并出现在预设列表（“自定义 1：…”），选它恢复 2:3、右口优先、左口只放废料；" +
                    $"存满 {BeltNodeService.CustomMax} 个后再存给原因；删除先弹确认（取消不删，确认才删）");
                // 合流器与地下传送带的面板。
                BeltNodePanelUIToolkit.Open(mg);
                panel.Refresh();
                panel.PriorityInField.value = panel.PriorityInField.choices[2];
                BeltNetworkService.TryGetNode(mg, out BeltNodeInfo mn);
                bool merger = panel.MergerSettingsVisible && !panel.SplitterSettingsVisible && mn.PriorityIn == BeltSide.Right && panel.TitleText.Contains("合流器");
                BeltNodePanelUIToolkit.Open(un);
                panel.Refresh();
                bool under = !panel.MergerSettingsVisible && !panel.SplitterSettingsVisible && panel.DetailText.Contains("地下跨 3 格") && panel.HintText.Contains("没有设置");
                bool esc = UiEscapeStack.CloseTop() && !BeltNodePanelUIToolkit.IsOpen && mode.IsOpen;
                Expect(merger && under && esc, "F5 合流器面板只有“优先输入口”（选右口立刻生效）；地下传送带面板只有读数（跨度、两端、地下件数）；Esc 只关面板，建造模式还开着");
                // 节点被拆了：面板自己收起。
                BeltNodePanelUIToolkit.Open(sp);
                panel.Refresh();
                BeltNetworkService.TryRemove(s, sp, new List<ushort>());
                panel.Refresh();
                Expect(!BeltNodePanelUIToolkit.IsOpen, "F5 面板开着时节点被拆掉：面板自动收起（不指向已经不存在的东西）");
            }
            finally
            {
                BeltNodePanelUIToolkit.Close();
                BeltNodePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                mode?.Close();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
            }
            // 布局探针：中英 × 三种缩放，有数据的分流器面板。
            CampaignState ps = NewWorld(9402, scrap: 200);
            GridCell pcore = HomeGridService.CorePivot(ps);
            GridCell? parea = FindArea(ps, new GridCell(pcore.X - 16, pcore.Y + 20), 16, 9, 24);
            if (parea == null || !LayNodeSet(ps, parea.Value, out GridCell psp, out _, out _, out _))
            {
                Fail("F5 布局探针：放不下节点");
                return;
            }
            BeltNodePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "BeltNodePanel.uxml", "BeltNodeWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_bn") { hideFlags = HideFlags.HideAndDontSave };
                            BeltNodePanelUIToolkit p = probeGo.AddComponent<BeltNodePanelUIToolkit>();
                            p.BindView(pr.panel.visualTree);
                            BeltNodePanelUIToolkit.Open(psp);
                            p.Refresh();
                            BeltNodePanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("BeltNodeRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"F5 布局探针 BeltNodePanel.uxml#BeltNodeWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                BeltNodePanelUIToolkit.Close();
                BeltNodePanelUIToolkit.InWorldOverrideForTests = false;
            }
        }

        // ── F6 拆除返还与摧毁留虚影 ─────────────────────────────────────────────────────

        private static void CheckRemoveAndDestroy()
        {
            CampaignState s = NewWorld(9501, scrap: 300);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? area = FindArea(s, new GridCell(core.X - 16, core.Y + 20), 16, 9, 24);
            string failure = null;
            if (area == null || !LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out failure))
            {
                Fail("F6 家园里放不下测试用的节点：" + (area == null ? "没有空地" : failure));
                return;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            // 地下传送带里放 3 件废料（入口、地下段、出口各一件）。
            k.InsertItemAt(un.X, un.Y, 1000, BeltItems.ScrapId);
            k.InsertItemAt(un.X + 2, BeltDirs.UnderY(un.Y, (int)BeltDir.East), 1000, BeltItems.ScrapId);
            k.InsertItemAt(un.X + 4, un.Y, 1000, BeltItems.ScrapId);
            long total = Conserved(s) + 4 + 10; // 拆除会返还一座分流器 4 + 地下两端 10 废料（造价是另外的账）
            try
            {
                mode.Open();
                mode.SetDemolishMode(true);
                mode.ClickCell(s, sp);
                bool spGone = !k.HasCell(sp.X, sp.Y) && HomeGridService.MapFor(s).GetBelt(sp) == 0;
                mode.ClickCell(s, new GridCell(un.X + 4, un.Y));
                bool unGone = !k.HasCell(un.X, un.Y) && !k.HasCell(un.X + 4, un.Y) && HomeGridService.MapFor(s).GetBelt(un) == 0 && HomeGridService.MapFor(s).GetBelt(new GridCell(un.X + 4, un.Y)) == 0;
                bool refunds = HomeGridService.LastBeltScrap == 10 && HomeGridService.LastBeltItemsReturned == 3 && Conserved(s) == total;
                Expect(spGone && unGone && refunds,
                    $"F6 拆除全额返还（FGR-LOG-007）：拆除模式点分流器 → 返还一座的造价；点地下传送带出口 → 两端与地下段一起拆掉，返还两端造价 {HomeGridService.LastBeltScrap} 与地下的 {HomeGridService.LastBeltItemsReturned} 件物品，" +
                    $"“库存 + 带上 + 地面” = {Conserved(s)}（= 拆前 + 造价返还）");
                mode.SetDemolishMode(false);
            }
            finally
            {
                mode?.Close();
            }
            // 摧毁：合流器（优先口右）与另放的分流器（3:1、左口只放废料）→ 留下保留设置的虚影 → 重建后设置恢复。
            BeltNetworkService.TryPlaceNode(s, sp, BeltDir.East, 2, BeltNodeKind.Splitter);
            BeltNetworkService.TrySetSplitter(s, sp, 3, 1, BeltSide.Right, BeltItems.ScrapId, BeltConst.FilterNone);
            BeltNetworkService.TrySetMergerPriority(s, mg, BeltSide.Right);
            BeltNetworkService.TryPlaceUnderground(s, un, BeltDir.East, 0, 4);
            bool d1 = BeltNetworkService.TryDamage(s, sp, 9999, out _);
            bool d2 = BeltNetworkService.TryDamage(s, mg, 9999, out _);
            bool d3 = BeltNetworkService.TryDamage(s, un, 9999, out _);
            bool ghosts = HomeValleyConstruction.TryFindPlannedCell(s, sp, out PlannedBeltRecord gs, out _) && gs.Destroyed && gs.NodeKind == (int)BeltNodeKind.Splitter
                          && gs.RatioL == 3 && gs.RatioR == 1 && gs.PriorityOut == (int)BeltSide.Right && gs.FilterL == BeltItems.ScrapId && gs.FilterR == BeltConst.FilterNone
                          && HomeValleyConstruction.TryFindPlannedCell(s, mg, out PlannedBeltRecord gm, out _) && gm.PriorityIn == (int)BeltSide.Right
                          && HomeValleyConstruction.TryFindPlannedCell(s, new GridCell(un.X + 4, un.Y), out PlannedBeltRecord gu, out _) && HomeValleyConstruction.IsUnderground(gu) && gu.Destroyed
                          && !k.HasCell(un.X + 4, un.Y);
            int arranged = HomeValleyConstruction.RebuildAllDestroyed(s);
            bool rebuilt = StepUntil(() => k.TryGetKind(sp.X, sp.Y, out BeltNodeKind a) && a == BeltNodeKind.Splitter && k.TryGetKind(mg.X, mg.Y, out BeltNodeKind b) && b == BeltNodeKind.Merger
                                           && k.TryGetUndergroundEnds(un.X, un.Y, out _, out _, out int d) && d == 4, 500);
            BeltNetworkService.TryGetNode(sp, out BeltNodeInfo ns);
            BeltNetworkService.TryGetNode(mg, out BeltNodeInfo nm);
            Expect(d1 && d2 && d3 && ghosts && arranged == 3 && rebuilt && ns.RatioL == 3 && ns.PriorityOut == BeltSide.Right && ns.FilterL == BeltItems.ScrapId && ns.FilterR == BeltConst.FilterNone
                   && nm.PriorityIn == BeltSide.Right,
                "F6 摧毁留虚影（FGR-LOG-027 的节点版）：分流器 / 合流器 / 地下传送带被摧毁后原位置留下保留原设置的虚影（地下两端一份）；施工队列“全部重建”后机器建回，" +
                $"分流器恢复 {ns.RatioL}:{ns.RatioR}、右口优先、左口只放废料、右口关闭，合流器恢复右口优先");
        }

        // ── F7 真文件存读档 ─────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s, GridCell sp, GridCell mg, GridCell un)
        {
            var sb = new StringBuilder();
            sb.Append("hash=").Append(BeltNetworkService.Kernel.ComputeStateHash()).Append(" scrap=").Append(s.Scrap).Append(" ground=").Append(GroundScrap(s)).Append('\n');
            foreach (GridCell c in new[] { sp, mg, un })
            {
                if (BeltNetworkService.TryGetNode(c, out BeltNodeInfo n))
                {
                    sb.Append(n.Kind).Append(' ').Append(n.RatioL).Append(':').Append(n.RatioR).Append(' ').Append(n.PriorityOut).Append(' ').Append(n.FilterL).Append('/').Append(n.FilterR)
                        .Append(' ').Append(n.SentL).Append('/').Append(n.SentR).Append(' ').Append(n.PriorityIn).Append(' ').Append(n.Distance).Append(' ').Append(n.ItemsInside).Append('\n');
                }
                else
                {
                    sb.Append("none\n");
                }
            }
            foreach (BeltFilterPresetRecord p in s.Belts.FilterPresets ?? Array.Empty<BeltFilterPresetRecord>())
            {
                sb.Append("preset ").Append(p.Serial).Append(' ').Append(p.RatioL).Append(':').Append(p.RatioR).Append(' ').Append(p.Priority).Append(' ').Append(p.FilterL).Append('/').Append(p.FilterR).Append('\n');
            }
            foreach (PlannedBeltRecord p in s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                sb.Append("plan ").Append(p.PlanId).Append(' ').Append(p.NodeKind).Append(' ').Append(p.Destroyed).Append(' ').Append(string.Join(",", p.Xs)).Append(' ')
                    .Append(p.RatioL).Append(':').Append(p.RatioR).Append(' ').Append(p.PriorityOut).Append(' ').Append(p.FilterL).Append('/').Append(p.FilterR).Append(' ').Append(p.PriorityIn).Append('\n');
            }
            sb.Append("nextPreset=").Append(s.Belts.NextFilterPresetSerial);
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9601, scrap: 250);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? area = FindArea(s, new GridCell(core.X - 16, core.Y + 20), 16, 9, 24);
            string failure = null;
            if (area == null || !LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out failure))
            {
                Fail("F7 家园里放不下测试用的节点：" + (area == null ? "没有空地" : failure));
                return;
            }
            GridCell o = area.Value;
            BeltNetworkService.TrySetSplitter(s, sp, 2, 1, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny);
            BeltNetworkService.TrySetMergerPriority(s, mg, BeltSide.Left);
            BeltNetworkService.TryAddSource(s, 90011, new GridCell(o.X, o.Y + 4), BeltItems.ScrapId, 1, BeltConst.Unlimited);
            BeltNetworkService.TryAddSink(s, 90012, new GridCell(sp.X, sp.Y + 4), BeltConst.Unlimited, 0);
            BeltNetworkService.TryAddSink(s, 90013, new GridCell(sp.X, sp.Y - 4), BeltConst.Unlimited, 0);
            BeltNodeService.TrySaveCustom(s, sp, out _, out _);
            // 一个节点虚影（还没建）与一个被摧毁的节点虚影。
            GridCell ghostAt = new GridCell(o.X + 14, o.Y + 7);
            HomeGridService.TryPlaceBeltPath(s, "splitter", ghostAt, ghostAt, BeltDir.North);
            GridCell wreck = new GridCell(o.X + 14, o.Y + 1);
            BeltNetworkService.TryPlaceNode(s, wreck, BeltDir.West, 2, BeltNodeKind.Splitter);
            BeltNetworkService.TrySetSplitter(s, wreck, 4, 1, BeltSide.Left, BeltItems.ScrapId, BeltConst.FilterAny);
            BeltNetworkService.TryDamage(s, wreck, 9999, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 17 + 5);
            string before = Snapshot(s, sp, mg, un);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            string continuous = Snapshot(s, sp, mg, un);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State, sp, mg, un);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                return Snapshot(rr.State, sp, mg, un);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            Expect(save.Success && loaded1 == before && CampaignSession.Current.Belts.FormatVersion == BeltKernel.FormatVersion,
                "F7 满载中途真文件存读档：分流器设置与比例轮次 / 累计分出、合流器优先口、地下传送带（距离、地下的物品）、自定义预设、节点虚影逐字段往返一致"
                + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "F7 读档后接着跑 30 游戏秒，与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        // ── F8 暂停与倍速 / F9 观察一致 ───────────────────────────────────────────────────

        private static string RunNodeScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 200);
            pausedHeld = true;
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? area = FindArea(s, new GridCell(core.X - 16, core.Y + 20), 16, 9, 24);
            if (area == null || !LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out _))
            {
                return "放不下";
            }
            GridCell o = area.Value;
            BeltNetworkService.TrySetSplitter(s, sp, 3, 2, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny);
            BeltNetworkService.TrySetMergerPriority(s, mg, BeltSide.Right);
            BeltNetworkService.TryAddSource(s, 90021, new GridCell(o.X, o.Y + 4), BeltItems.ScrapId, 1, BeltConst.Unlimited);
            BeltNetworkService.TryAddSink(s, 90022, new GridCell(sp.X, sp.Y + 4), 3, 25);
            BeltNetworkService.TryAddSink(s, 90023, new GridCell(sp.X, sp.Y - 4), BeltConst.Unlimited, 0);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s, sp, mg, un);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snapshot(s, sp, mg, un) == p0;
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
            return Snapshot(s, sp, mg, un);
        }

        private static void CheckTimingMatrix()
        {
            var results = new List<string>();
            string reference = null;
            bool same = true;
            bool paused = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunNodeScenario(9701, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                results.Add($"{speed}x");
            }
            Expect(paused && same && reference != "放不下",
                $"F8 暂停中（120 帧）分流器 / 合流器 / 地下传送带都不动；{string.Join(" / ", results)} 跑同样的 45 游戏秒，内核哈希、比例轮次与累计分出逐位一致");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunNodeScenario(9801, true, 1f, false, out _);
            string unseen = RunNodeScenario(9801, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "F9 同一组节点（3:2 分流、右口优先合流、地下传送带）在观察与不观察家园时跑 45 游戏秒逐字段一致（FGR-BASE-021）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── F10 120 帧节奏性能 ───────────────────────────────────────────────────────

        private static void CheckPerformance120()
        {
            CampaignState s = NewWorld(9901, scrap: 150);
            GridCell core = HomeGridService.CorePivot(s);
            BeltKernel k = BeltNetworkService.Kernel;
            BuildHugeWithNodes(k, core.X + 3000, core.Y + 3000);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            k.EnsureTopology();
            Camera cam = WorldView.Camera;
            int hz = GameClock.StepHz;
            long tick = GameClock.Ticks;
            for (int f = 0; f < 120; f++)
            {
                BeltNetworkService.WorldStep(s, tick++, hz);
                BeltNetworkService.Render(cam);
            }
            var lines = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 1f, 3f })
            {
                const int frames = 1200;
                var frameMs = new List<double>(frames);
                var stepMs = new List<double>(frames);
                double acc = 0;
                var sw = new Stopwatch();
                GC.Collect();
                long mem0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                for (int f = 0; f < frames; f++)
                {
                    sw.Restart();
                    acc += speed * hz / 120.0;
                    while (acc >= 1.0)
                    {
                        long before = k.StepIndex;
                        BeltNetworkService.WorldStep(s, tick, hz);
                        tick++;
                        acc -= 1.0;
                        if (k.StepIndex != before)
                        {
                            stepMs.Add(k.LastStepMs);
                        }
                    }
                    BeltNetworkService.Render(cam);
                    sw.Stop();
                    frameMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                long alloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - mem0;
                var hw = Stopwatch.StartNew();
                GridCell hover = new GridCell(core.X + 3000 + 40, core.Y + 3000);
                for (int i = 0; i < 200; i++)
                {
                    BeltNetworkService.TryDescribeHover(s, hover, out _, out _);
                }
                hw.Stop();
                frameMs.Sort();
                stepMs.Sort();
                double avg = frameMs.Average();
                double p95 = frameMs[(int)(frames * 0.95)];
                double p99 = frameMs[(int)(frames * 0.99)];
                double stepP95 = stepMs.Count > 0 ? stepMs[(int)(stepMs.Count * 0.95)] : 0;
                lines.Add($"{speed}x@120 帧（{frames} 帧，{k.CellCount:N0} 格 / {k.NodeCount} 个节点 / {k.ItemCount:N0} 件）：物流每帧 平均 {avg:F3} ms、p95 {p95:F3}、p99 {p99:F3} ms；" +
                          $"内核步 {stepMs.Count} 次 p95 {stepP95:F3} ms；分流器悬停读数 {hw.Elapsed.TotalMilliseconds * 1000.0 / 200:F0} µs / 次；托管堆增量 {alloc} 字节");
                ok &= p99 <= 4.0 && avg <= 1.5 && stepP95 <= 2.0;
            }
            // 稳态分配单独测（与 FgBeltFormalSelfCheck Q 段同一口径）：3x 节奏 3,000 帧，托管堆增量按每帧 < 8 字节（Mono 堆块约 8 KB，单次测量有一个堆块的噪声）。
            GC.Collect();
            long memA = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            double accA = 0;
            for (int f = 0; f < 3000; f++)
            {
                accA += 3.0 * hz / 120.0;
                while (accA >= 1.0)
                {
                    BeltNetworkService.WorldStep(s, tick++, hz);
                    accA -= 1.0;
                }
                BeltNetworkService.Render(cam);
            }
            long steadyAlloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - memA;
            lines.Add($"稳态分配（3x 节奏 3,000 帧 = 4,500 个世界步 + 3,000 次 Render，含 340 个节点）：托管堆增量 {steadyAlloc} 字节");
            ok &= steadyAlloc < 3000 * 8;
            PerfLines.AddRange(lines);
            Expect(ok && k.CellCount >= 15000 && k.NodeCount >= 340,
                "F10 120 帧最低标准（8.33 ms / 帧）下含 340 个节点的满载物流：1x 与 3x 逐帧实测 p99 ≤ 4 ms、平均 ≤ 1.5 ms、内核单步 p95 ≤ 2 ms、稳态几乎不分配（Editor batchmode 无 GPU）：" + string.Join("；", lines));
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgBeltNodeSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public Vector3 Mouse = new Vector3(-10f, -10f, 0f);

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame() => _down.Clear();

            public bool GetKey(KeyCode key) => _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
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
