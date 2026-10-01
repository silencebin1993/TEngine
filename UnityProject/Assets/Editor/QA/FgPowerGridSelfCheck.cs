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
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameConfig.fg;
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
    /// FG3-LOG-06 电力子网与电塔的自动验收（FG03 FGR-LOG-060、061；FGT-LOG-008“电网分成子网后各自结算和断电”；卡片负向“电塔被摧毁，电网一分为二”；
    /// 必须同时交付“放置时预览电力覆盖”“子网断开时发出警告”“沿用并扩展 HomeValleyPowerGrid”）。
    /// 全部起真实系统：真实 AOT 内核（PowerKernel）、真实世界模拟与家园（WorldSimulation.LoadHome：建造模式、机器取料施工、修复工单都在世界步里跑）、真实存读档文件、真 UXML 面板。
    /// 内核（K）
    /// K1 覆盖与相连（半径、较大半径相连、最近节点接入、未接入）；K2 子网各自结算与优先级（不跨网借电）；K3 电塔失去 → 一分为二（断开记录、编号、曲线复制、各自断电）、修好合并；
    /// K4 编号稳定与确定性；K5 储能（充电、放电保供、放空后按优先级断电、容量上限、按键继承）；K6 曲线（采样、环形上限）；K7 快照往返与坏数据；K8 查询（覆盖、可连电网、可覆盖数、拆除影响）；K9 性能（800 建筑 + 300 电塔）。
    /// 正式（F）
    /// F1 数据（节点表逐字段、调参、电塔建筑行、菜单、文本中英、钩子、图鉴、按键、通知类型）；F2 新战役兼容（三个种子：开局建筑全在核心配电内、结果与旧全局仲裁逐座一致）；
    /// F3 建造菜单真实输入（超出覆盖警告不阻止、放电塔预览接入电网 + 覆盖数 + 预览圈、机器施工、钩子与图鉴）；F4 电塔被摧毁一分为二（真实家园：断开警告可定位、各自结算、失去连接警告、修复合并、供电恢复）；
    /// F5 拆除确认（会断开才确认、确认框写明后果、取消不变）；F6 电网面板（真 UXML：汇总、选电网、读数、曲线、改优先级、关停、定位、叠加层；布局探针）；F7 悬停与叠加层（建造模式自动显示、预览圈、未接入标记）；
    /// F8 真文件存读档（编号、曲线、储能逐字段往返、接着跑一致、不认识的格式保留、旧存档提示）；F9 暂停与 0.5x～3x；F10 观察 / 不观察一致；F11 性能（800 建筑家园：重算、稳态每步零重算、世界步开销）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgPowerGridSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 电力子网与电塔")]
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
            Line("\n[电力子网与电塔] 电塔覆盖 / 子网各自结算 / 优先级 1～4 / 一分为二 / 储能与曲线 / 覆盖叠加层 / 电网面板（FG3-LOG-06）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpower-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                HomeValleyPowerGrid.ResetForTests();
                PowerCoverageOverlayView.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"图形设备 {SystemInfo.graphicsDeviceType}；电网内核是 AOT 程序集里的托管代码（Editor 下 Mono JIT，真机 IL2CPP AOT），热更层在真机走 HybridCLR 解释执行，真机另测（FG3-LOG-09 / FG15-SYS-02）");

                Step(CheckCoverageAndLinks);
                Step(CheckIndependentSettlement);
                Step(CheckSplitAndMerge);
                Step(CheckSerialsAndDeterminism);
                Step(CheckStorage);
                Step(CheckCurves);
                Step(CheckSnapshot);
                Step(CheckQueries);
                Step(CheckBatchRemovalAndIndex);
                Step(CheckKernelPerformance);
                Step(CheckData);
                Step(CheckNewCampaignCompat);
                Step(CheckBuildMode);
                Step(CheckHomeSplit);
                Step(CheckDemolishConfirm);
                Step(CheckPanel);
                Step(CheckHoverAndOverlay);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckHomePerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"电网自检抛异常：{e}");
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
                HomeValleyPowerGrid.ResetForTests();
                PowerCoverageOverlayView.ResetForTests();
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
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [电力子网与电塔] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 内核工具 ───────────────────────────────────────────────────────────

        private static int _nextKey = 100;

        private static PowerEntity Node(int x, int y, float r, float supply = 0f, bool conducts = true)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, NodeRadius = r, Conducts = conducts, Supply = supply, SupplyOn = supply > 0f };
        }

        private static PowerEntity Consumer(int x, int y, float demand, int priority, int size = 1)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x + size - 1, MaxY = y + size - 1, Demand = demand, DemandOn = true, Priority = priority };
        }

        private static PowerEntity Producer(int x, int y, float supply, int size = 1)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x + size - 1, MaxY = y + size - 1, Supply = supply, SupplyOn = true };
        }

        private static PowerEntity Storage(int x, int y, double capacitySeconds, float rate)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, StorageCapacity = capacitySeconds, StorageRate = rate, StorageOn = true };
        }

        private static PowerKernel Build(PowerEntity[] e, int curve = 16)
        {
            var k = new PowerKernel(curve);
            k.Rebuild(e, e.Length);
            return k;
        }

        // ── K1 覆盖与相连 ─────────────────────────────────────────────────────────

        private static void CheckCoverageAndLinks()
        {
            PowerEntity[] e =
            {
                Node(0, 0, 26f, 20f),               // 0 归还核心配电（基础 20）
                Node(24, 0, 8f),                    // 1 电塔 T1：离核心 24 ≤ 26 → 相连
                Node(40, 0, 8f),                    // 2 电塔 T1：离 1 号 16 > 8 → 不相连
                Consumer(30, -1, 10f, 1, 3),        // 3 占地 30..32：离 1 号最近 6 ≤ 8 → 接 1 号
                Consumer(50, 0, 10f, 1, 3),         // 4 离 2 号最近 10 > 8 → 未接入
                Consumer(44, 0, 10f, 2),            // 5 离 2 号 4 → 接 2 号（孤立电网，没有发电）
            };
            PowerKernel k = Build(e);
            bool ok1 = k.SubnetCount == 2 && k.SubnetOf(1) == k.SubnetOf(0) && k.SubnetOf(2) != k.SubnetOf(0) && k.SubnetOf(2) >= 0
                       && k.CoverNodeOf(3) == 1 && k.SubnetOf(4) < 0 && k.CoverNodeOf(5) == 2 && k.LinkOf(1) == 0 && k.LinkOf(2) < 0;
            bool use1 = k.UseOf(3) == PowerUse.Powered && k.UseOf(4) == PowerUse.Unconnected && k.UseOf(5) == PowerUse.Brownout;
            Expect(ok1 && use1, "K1 电塔覆盖（FGR-LOG-060）：核心配电 26 格内的电塔自动相连（连通树 1→0），相距 16 格的两座 T1 不相连（各成电网）；" +
                                "建筑接到覆盖它、最近的节点；覆盖外的建筑“未接入电网”；孤立电网没有发电 → 它覆盖的建筑缺电停机（不是未接入）");
            e[2].NodeRadius = 16f;
            k.Rebuild(e, e.Length);
            bool ok2 = k.SubnetCount == 1 && k.CoverNodeOf(4) == 2 && k.UseOf(3) == PowerUse.Powered && k.UseOf(4) == PowerUse.Powered && k.UseOf(5) == PowerUse.Brownout;
            Expect(ok2, "K1 换成 T2（半径 16）：两塔相距 16 ≤ 较大半径 → 连成一个电网；原来未接入的建筑接入；供给 20 按顺序分给前两座，第三座缺电");
            e[2].NodeRadius = 8f;
            e[1].Conducts = false;
            k.Rebuild(e, e.Length);
            bool ok3 = k.SubnetOf(1) < 0 && k.LinkOf(1) < 0 && k.SubnetCount == 2 && k.CoverNodeOf(3) == 2 && k.UseOf(3) == PowerUse.Brownout;
            e[2].Conducts = false;
            k.Rebuild(e, e.Length);
            bool ok4 = k.SubnetCount == 1 && k.UseOf(3) == PowerUse.Unconnected && k.UseOf(5) == PowerUse.Unconnected;
            Expect(ok3 && ok4, "K1 电塔不运转（受损 / 施工中）就不导电：它不连接别的节点、不覆盖任何建筑——它覆盖的建筑改接到还够得着的另一座电塔（那座在孤立电网里，缺电停机）；两座都停了就未接入电网");
        }

        // ── K2 各自结算与优先级 ─────────────────────────────────────────────────────

        private static void CheckIndependentSettlement()
        {
            // 输入顺序 = 分配顺序（热更层按优先级 → 建筑编号排好）：A 电网 p1 20 / p2 20 / p4 20；B 电网 p1 30 / p3 30。
            PowerEntity[] e =
            {
                Node(0, 0, 26f, 20f),
                Consumer(2, 2, 20f, 1),
                Consumer(3, 3, 20f, 2),
                Producer(-5, 0, 30f, 3),
                Consumer(4, 4, 20f, 4),
                Node(200, 0, 8f),
                Producer(203, 0, 80f, 3),
                Consumer(200, 3, 30f, 1),
                Consumer(200, -3, 30f, 3),
            };
            PowerKernel k = Build(e);
            int a = k.SubnetOf(0);
            int b = k.SubnetOf(5);
            PowerSubnetInfo na = k.Subnet(a);
            PowerSubnetInfo nb = k.Subnet(b);
            bool sep = a != b && k.SubnetCount == 2 && Mathf.Approximately(na.Supply, 50f) && Mathf.Approximately(nb.Supply, 80f)
                       && Mathf.Approximately(na.Demand, 60f) && Mathf.Approximately(nb.Demand, 60f);
            bool prio = k.UseOf(1) == PowerUse.Powered && k.UseOf(2) == PowerUse.Powered && k.UseOf(4) == PowerUse.Brownout
                        && k.UseOf(7) == PowerUse.Powered && k.UseOf(8) == PowerUse.Powered && Mathf.Approximately(na.Delivered, 40f) && Mathf.Approximately(nb.Delivered, 60f);
            Expect(sep && prio, $"K2 互不相连的电网各自结算（FGT-LOG-008）：A 发电 {na.Supply}（核心 20 + 发电机 30）/ 需要 {na.Demand}，优先级 4 的停机；" +
                                $"B 发电 {nb.Supply} / 需要 {nb.Demand} 全部供上、盈余 20 也不借给 A（两网合计本来够用）");
            // 优先级：A 缺 10，把 p4 那座挪到最前（热更层按新优先级重排 = 输入顺序）→ 它先供上、原 p2 那座停机。
            PowerEntity[] re = { e[0], e[4], e[1], e[3], e[2], e[5], e[6], e[7], e[8] };
            re[1].Priority = 1;
            k.Rebuild(re, re.Length);
            bool reorder = k.UseOf(1) == PowerUse.Powered && k.UseOf(2) == PowerUse.Powered && k.UseOf(4) == PowerUse.Brownout;
            Expect(reorder, "K2 优先级 1～4（FGR-LOG-060“按优先级依次断电”）：改了优先级重排后，缺电时停的是排在最后的一座（同一套“优先级 → 建筑编号”贪心，按电网分开）");
        }

        // ── K3 电塔失去 → 一分为二；修好合并 ─────────────────────────────────────────────

        private static PowerEntity[] SplitScenario()
        {
            return new[]
            {
                Node(0, 0, 26f, 20f),          // 0 核心（基础 20）
                Consumer(3, 3, 50f, 1, 3),     // 1 核心旁 50（p1）
                Node(20, 0, 8f),               // 2 P1
                Node(27, 0, 8f),               // 3 P2（中间）
                Node(34, 0, 8f),               // 4 P3
                Producer(35, 3, 80f, 3),       // 5 P3 旁发电机 80
                Consumer(35, -5, 30f, 2, 3),   // 6 P3 旁 30（p2）
                Consumer(38, -2, 30f, 4, 2),   // 7 P3 旁 30（p4）
            };
        }

        private static void CheckSplitAndMerge()
        {
            PowerEntity[] e = SplitScenario();
            // 注意顺序：热更层按优先级排好（p1 → p2 → p4）；这里输入已经是这个顺序。
            PowerKernel k = Build(e, 8);
            int serial = k.Subnet(k.SubnetOf(0)).Serial;
            k.Sample();
            k.Sample();
            bool before = k.SubnetCount == 1 && k.UseOf(1) == PowerUse.Powered && k.UseOf(6) == PowerUse.Powered && k.UseOf(7) == PowerUse.Brownout
                          && k.LastSplits.Count == 0;
            Expect(before, "K3 断开前：一个电网，发电 100 / 需要 110 → 优先级 4 的那座先停（低优先级先断）");
            e[3].Conducts = false; // P2 被摧毁
            k.Rebuild(e, e.Length);
            int main = k.SubnetOf(0);
            int far = k.SubnetOf(4);
            PowerSplit split = k.LastSplits.Count == 1 ? k.LastSplits[0] : default;
            bool two = k.SubnetCount == 2 && main != far && far >= 0 && k.SubnetOf(3) < 0
                       && k.Subnet(main).Serial == serial && k.Subnet(far).Serial != serial
                       && split.OldSerial == serial && split.Parts == 2 && split.NewSerials.Length == 2 && split.NewSerials[0] == serial && split.NewSerials[1] == k.Subnet(far).Serial;
            bool settle = k.UseOf(1) == PowerUse.Brownout && k.UseOf(6) == PowerUse.Powered && k.UseOf(7) == PowerUse.Powered
                          && Mathf.Approximately(k.Subnet(main).Supply, 20f) && Mathf.Approximately(k.Subnet(far).Supply, 80f);
            bool history = k.TryGetCurve(k.Subnet(far).Serial, out PowerCurve fc) && fc.Count == 2 && k.TryGetCurve(serial, out PowerCurve mc) && mc.Count == 2;
            Expect(two && settle && history,
                $"K3 电塔被摧毁，电网一分为二（卡片负向 / FG03 第 5 节）：断开记录 电网 {split.OldSerial} → {string.Join("、", split.NewSerials ?? Array.Empty<int>())}（{split.Parts} 段），多的一段保留编号；" +
                "两段各自结算：核心那段只有 20 → 50 的那座停机；发电机那段 80 → 两座都供上；断出去的一段带着断开前的曲线");
            e[3].Conducts = true; // 修好
            k.Rebuild(e, e.Length);
            bool merged = k.SubnetCount == 1 && k.Subnet(0).Serial == serial && k.LastSplits.Count == 0 && k.UseOf(7) == PowerUse.Brownout && k.UseOf(1) == PowerUse.Powered;
            Expect(merged, $"K3 修好后重新连成一个电网，沿用原编号 {serial}（多数节点原来在它里面），不报断开；结算回到断开前");
        }

        // ── K4 编号稳定与确定性 ─────────────────────────────────────────────────────

        private static void CheckSerialsAndDeterminism()
        {
            PowerEntity[] e = SplitScenario();
            PowerKernel k = Build(e);
            string f0 = k.Fingerprint();
            int rebuilds = k.RebuildCount;
            k.Rebuild(e, e.Length);
            bool stable = k.Fingerprint() == f0 && k.LastSplits.Count == 0 && k.RebuildCount == rebuilds + 1 && k.NextSerial == 2;
            PowerKernel k2 = Build(SplitScenarioWithKeysOf(e));
            Expect(stable && k2.Fingerprint() == f0, "K4 同样的输入重算两次、两个内核各算一次，编号与每座建筑的结果逐位一致（确定性）；拓扑没变不产生新编号");
            // 新节点把两个电网连起来：多数节点所在的编号保留，另一个编号的曲线丢弃，不报断开。
            PowerEntity[] iso = e.Concat(new[] { Node(60, 0, 8f), Node(67, 0, 8f) }).ToArray();
            iso[3].Conducts = false;
            k.Rebuild(iso, iso.Length);
            int before = k.SubnetCount;
            iso[3].Conducts = true;
            PowerEntity bridge = Node(41, 0, 8f);
            PowerEntity bridge2 = Node(48, 0, 8f);
            PowerEntity bridge3 = Node(55, 0, 8f);
            PowerEntity[] joined = iso.Concat(new[] { bridge, bridge2, bridge3 }).ToArray();
            k.Rebuild(joined, joined.Length);
            Expect(before == 3 && k.SubnetCount == 1 && k.LastSplits.Count == 0 && k.Subnet(0).Serial == 1,
                $"K4 合并：三个电网（{before} 个）被新电塔连成一个，沿用节点最多的原编号 1，不报断开");
        }

        private static PowerEntity[] SplitScenarioWithKeysOf(PowerEntity[] src) => (PowerEntity[])src.Clone();

        // ── K5 储能 ─────────────────────────────────────────────────────────────

        private static void CheckStorage()
        {
            PowerEntity[] e =
            {
                Node(0, 0, 26f, 20f),
                Consumer(2, 2, 40f, 1),
                Producer(-4, 0, 50f),
                Storage(4, -4, 600.0, 30f),     // 10 电·分钟，充放电 30
                Consumer(3, 5, 20f, 4),
            };
            PowerKernel k = Build(e);
            bool full = k.UseOf(1) == PowerUse.Powered && k.UseOf(4) == PowerUse.Powered && Mathf.Approximately(k.Subnet(0).StorageFlow, 10f);
            for (int i = 0; i < 10; i++)
            {
                k.StepSecond();
            }
            double after10 = k.StoredOf(3);
            Expect(full && Math.Abs(after10 - 100.0) < 1e-6, $"K5 盈余充电：发电 70 / 用电 60 → 储能每游戏秒 +10，10 秒后 {after10} 电·秒（= 100）");
            e[2].SupplyOn = false; // 发电机停了
            k.Rebuild(e, e.Length);
            bool kept = k.StoredOf(3) == after10 && k.UseOf(1) == PowerUse.Powered && k.UseOf(4) == PowerUse.Brownout
                        && Mathf.Approximately(k.Subnet(0).StorageFlow, -20f);
            Expect(kept, "K5 缺电时放电保供：发电只剩 20，储能按功率上限 30 放电 → 优先级 1 的 40 供上，优先级 4 的 20 停机；拓扑重算按实体键继承存量");
            bool flipped = false;
            for (int i = 0; i < 5; i++)
            {
                flipped |= k.StepSecond();
            }
            bool empty = Math.Abs(k.StoredOf(3)) < 1e-9 && flipped && k.UseOf(1) == PowerUse.Brownout && k.UseOf(4) == PowerUse.Powered;
            Expect(empty, "K5 放空后：5 游戏秒放掉 100 电·秒，存量 0；只剩 20 → 40 的那座停机，20 的那座能供上（同一套贪心：分不到就跳过，后面放得下的照样供）");
            e[2].SupplyOn = true;
            k.Rebuild(e, e.Length);
            for (int i = 0; i < 200; i++)
            {
                k.StepSecond();
            }
            Expect(Math.Abs(k.StoredOf(3) - 600.0) < 1e-6 && k.Subnet(0).StorageFlow == 0f, $"K5 容量上限：充满 600 电·秒后不再充（存量 {k.StoredOf(3)}）");
            var noStore = Build(new[] { Node(0, 0, 26f, 20f), Consumer(1, 1, 10f, 1) });
            string fp = noStore.Fingerprint();
            bool noop = !noStore.AnyStorage && !noStore.StepSecond() && noStore.Fingerprint() == fp;
            Expect(noop, "K5 没有储能的电网：每游戏秒的积分什么都不做（热更层每步 O(1)）");
        }

        // ── K6 曲线 ─────────────────────────────────────────────────────────────

        private static void CheckCurves()
        {
            PowerEntity[] e = { Node(0, 0, 26f, 20f), Consumer(2, 2, 15f, 1), Storage(3, -3, 120.0, 10f) };
            PowerKernel k = Build(e, 4);
            k.Sample();
            k.StepSecond();
            k.Sample();
            bool two = k.TryGetCurve(1, out PowerCurve c) && c.Count == 2;
            c.Get(1, out float s, out float d, out float dl, out float st);
            bool values = Mathf.Approximately(s, 20f) && Mathf.Approximately(d, 15f) && Mathf.Approximately(dl, 15f) && Mathf.Approximately(st, 5f / 60f);
            for (int i = 0; i < 10; i++)
            {
                k.Sample();
            }
            Expect(two && values && c.Count == 4 && c.Capacity == 4,
                $"K6 每个电网的曲线（FGR-LOG-061）：发电 {s}、需要 {d}、实际用电 {dl}、储能 {st:0.###} 电·分钟（充了 5 电·秒）；环形缓冲只留最近 {c.Capacity} 个点");
        }

        // ── K7 快照 ─────────────────────────────────────────────────────────────

        private static void CheckSnapshot()
        {
            PowerEntity[] e = SplitScenario().Concat(new[] { Storage(36, 6, 300.0, 20f) }).ToArray();
            PowerKernel k = Build(e, 8);
            for (int i = 0; i < 7; i++)
            {
                k.StepSecond();
                k.Sample();
            }
            e[3].Conducts = false;
            k.Rebuild(e, e.Length);
            k.Sample();
            PowerSnapshot snap = k.Serialize();
            var r = new PowerKernel(8);
            bool restored = r.Restore(snap, out string err, out int dropped);
            r.Rebuild(e, e.Length);
            bool same = restored && dropped == 0 && r.Fingerprint() == k.Fingerprint() && r.NextSerial == k.NextSerial;
            Expect(same, "K7 快照往返：电网编号（锚点节点）、每个电网的曲线、储能存量、下一个编号 → 新内核恢复后按同样的实体重算，指纹逐位一致" + (same ? string.Empty : $"\n原：{k.Fingerprint()}\n新：{r.Fingerprint()}（{err}）"));
            for (int i = 0; i < 3; i++)
            {
                k.StepSecond();
                r.StepSecond();
                k.Sample();
                r.Sample();
            }
            Expect(r.Fingerprint() == k.Fingerprint(), "K7 读档后接着跑 3 游戏秒（储能积分 + 采样）与不存档一直跑逐位一致");
            snap.FormatVersion = 99;
            bool refused = !new PowerKernel(8).Restore(snap, out string why, out _) && why.Contains("format");
            snap.FormatVersion = PowerKernel.FormatVersion;
            snap.StorageStored[0] = double.NaN;
            snap.CurveSupply[0] = float.NaN;
            var bad = new PowerKernel(8);
            bool partial = bad.Restore(snap, out _, out int d2) && d2 == 2;
            snap.CurveCounts = new int[] { 1 };
            bool shape = !new PowerKernel(8).Restore(snap, out string sw, out _) && sw == "shape";
            Expect(refused && partial && shape, $"K7 负向：不认识的格式拒绝（“{why}”）；坏的单条（NaN 存量 / NaN 曲线点）丢弃并计数 {d2}；形状对不上整体拒绝（调用方保留原始数据）");
        }

        // ── K8 查询 ─────────────────────────────────────────────────────────────

        private static void CheckQueries()
        {
            PowerEntity[] e = SplitScenario().Concat(new[] { Node(200, 0, 8f) }).ToArray();
            PowerKernel k = Build(e);
            k.RemovalImpact(3, out int partsP2, out int orphansP2);
            k.RemovalImpact(4, out int partsP3, out int orphansP3);
            k.RemovalImpact(8, out int partsIso, out int orphansIso);
            Expect(partsP2 == 2 && orphansP2 == 0 && partsP3 == 1 && orphansP3 == 3 && partsIso == 0 && orphansIso == 0,
                $"K8 拆除影响（拆除确认用）：拆中间的 P2 → 断成 {partsP2} 段；拆末端的 P3 → 不断开但 {orphansP3} 座建筑失去连接（发电机 + 两座用电）；孤立电塔 → 没影响");
            int cover = k.CoveringNode(22, 3, 22, 3, out float dist);
            int none = k.CoveringNode(100, 100, 102, 102, out _);
            float near = k.NearestNodeDistance(100, 0, 100, 0);
            var nets = new List<int>();
            k.SubnetsReachableFrom(100f, 0f, 100f, nets);
            k.CountCoverable(36f, 0f, 8f, out int covered, out int unconnected);
            bool q = cover == 2 && Math.Abs(dist - Mathf.Sqrt(13f)) < 1e-3 && none < 0 && Math.Abs(near - 66f) < 1e-3 && nets.Count == 2
                     && covered == 3 && unconnected == 0;
            Expect(q, $"K8 放置预览查询：接到最近的覆盖节点（距离 {dist:0.##}）；覆盖外返回 -1、最近节点 {near} 格；一座 100 格半径的节点能连到 {nets.Count} 个电网；在 P3 放一座 T1 能覆盖 {covered} 座");
        }

        /// <summary>K8 修复轮：批量拆除按整体拓扑算（两座互为备份的电塔一起拆才断开）；空间索引（核心等大半径节点单列）与暴力算法逐实体一致。</summary>
        private static void CheckBatchRemovalAndIndex()
        {
            // A(0,0) 与 C(14,0) 相距 14 > 8 不直接相连，靠 B(7,0) 与 B2(7,2) 两条并联的桥。
            var e = new[]
            {
                Node(0, 0, 8f, 30f), Node(7, 0, 8f), Node(7, 2, 8f), Node(14, 0, 8f),
                Consumer(18, 0, 5f, 1), Consumer(-3, 0, 5f, 1),
            };
            PowerKernel k = Build(e);
            k.RemovalImpact(1, out int partsB, out _);
            k.RemovalImpact(new List<int> { 1 }, out int splitB, out int orphB);
            k.RemovalImpact(new List<int> { 1, 2 }, out int splitBoth, out int orphBoth);
            k.RemovalImpact(new List<int> { 3, 4 }, out int splitEnd, out int orphEnd);
            k.RemovalImpact(new List<int> { 3 }, out int splitC, out int orphC);
            Expect(partsB == 1 && splitB == 0 && orphB == 0 && splitBoth == 1 && orphBoth == 0 && splitEnd == 0 && orphEnd == 0 && splitC == 0 && orphC == 1,
                $"K8 批量拆除影响（框选拆除确认用）：并联的两座桥只拆一座 → 不断开；两座一起拆 → {splitBoth} 个电网断开；末端电塔连同它唯一覆盖的建筑一起拆 → 没有别的建筑失去连接（{orphEnd}），只拆电塔 → {orphC} 座失去连接");

            // 暴力参照：节点两两比较相连、建筑逐节点找最近覆盖；与内核（平铺桶 + 大半径节点单列）逐实体比。
            PowerEntity[] big = BigHome(900, 260, 0);
            PowerKernel bk = Build(big);
            int n = big.Length;
            var root = new int[n];
            for (int i = 0; i < n; i++)
            {
                root[i] = i;
            }
            int F(int i) { while (root[i] != i) { i = root[i] = root[root[i]]; } return i; }
            bool IsNode(int i) => big[i].NodeRadius > 0f && big[i].Conducts;
            float Cx(int i) => (big[i].MinX + big[i].MaxX) * 0.5f;
            float Cy(int i) => (big[i].MinY + big[i].MaxY) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                if (!IsNode(i))
                {
                    continue;
                }
                for (int j = i + 1; j < n; j++)
                {
                    if (!IsNode(j))
                    {
                        continue;
                    }
                    float lim = Math.Max(big[i].NodeRadius, big[j].NodeRadius);
                    float dx = Cx(j) - Cx(i), dy = Cy(j) - Cy(i);
                    if (dx * dx + dy * dy <= lim * lim + 1e-4f)
                    {
                        root[F(i)] = F(j);
                    }
                }
            }
            var map = new Dictionary<int, int>();
            int mismatch = 0;
            for (int i = 0; i < n; i++)
            {
                int expect;
                if (IsNode(i))
                {
                    expect = F(i);
                }
                else if (big[i].NodeRadius > 0f)
                {
                    continue;
                }
                else
                {
                    int best = -1;
                    float bestD = float.PositiveInfinity;
                    for (int j = 0; j < n; j++)
                    {
                        if (!IsNode(j))
                        {
                            continue;
                        }
                        float x = Math.Max(big[i].MinX, Math.Min(big[i].MaxX, Cx(j))) - Cx(j);
                        float y = Math.Max(big[i].MinY, Math.Min(big[i].MaxY, Cy(j))) - Cy(j);
                        float d = x * x + y * y;
                        float r = big[j].NodeRadius;
                        if (d <= r * r + 1e-4f && d < bestD)
                        {
                            bestD = d;
                            best = j;
                        }
                    }
                    expect = best < 0 ? -1 : F(best);
                }
                int got = bk.SubnetOf(i);
                if (expect < 0 || got < 0)
                {
                    if (expect < 0 != got < 0)
                    {
                        mismatch++;
                    }
                    continue;
                }
                if (map.TryGetValue(expect, out int m))
                {
                    if (m != got)
                    {
                        mismatch++;
                    }
                }
                else
                {
                    map[expect] = got;
                }
            }
            bool bijective = map.Values.Distinct().Count() == map.Count && map.Count == bk.SubnetCount;
            Expect(mismatch == 0 && bijective,
                $"K8 空间索引与暴力算法一致：{n} 个实体（含半径 26 的核心、T1 / T2 电塔）的连通与接入逐实体相同（不一致 {mismatch}，电网 {bk.SubnetCount} 个）");
        }

        // ── K9 性能 ─────────────────────────────────────────────────────────────

        private static PowerEntity[] BigHome(int buildings, int poles, int storages)
        {
            var list = new List<PowerEntity> { Node(0, 0, 26f, 20f) };
            int side = (int)Math.Ceiling(Math.Sqrt(poles));
            for (int i = 0; i < poles; i++)
            {
                list.Add(Node((i % side) * 12 - side * 6, (i / side) * 12 - side * 6, i % 5 == 0 ? 16f : 8f));
            }
            var rng = new System.Random(1234);
            float span = side * 12f;
            for (int i = 0; i < buildings; i++)
            {
                int x = rng.Next(-(int)(span / 2), (int)(span / 2));
                int y = rng.Next(-(int)(span / 2), (int)(span / 2));
                list.Add(i % 8 == 0 ? Producer(x, y, 60f, 3) : Consumer(x, y, 10f + i % 20, 1 + i % 4, 3));
            }
            for (int i = 0; i < storages; i++)
            {
                list.Add(Storage(rng.Next(-(int)(span / 2), (int)(span / 2)), rng.Next(-(int)(span / 2), (int)(span / 2)), 1200.0, 20f));
            }
            return list.ToArray();
        }

        private static void CheckKernelPerformance()
        {
            PowerEntity[] e = BigHome(800, 300, 20);
            var k = new PowerKernel(90);
            k.Rebuild(e, e.Length);
            var sw = Stopwatch.StartNew();
            const int N = 20;
            for (int i = 0; i < N; i++)
            {
                e[1 + i].Conducts = i % 2 == 0; // 每次都真的改拓扑
                k.Rebuild(e, e.Length);
            }
            sw.Stop();
            double rebuildMs = sw.Elapsed.TotalMilliseconds / N;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                k.Settle();
            }
            sw.Stop();
            double settleMs = sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                k.StepSecond();
            }
            sw.Stop();
            double stepMs = sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                k.Sample();
            }
            sw.Stop();
            double sampleMs = sw.Elapsed.TotalMilliseconds / 200;
            PerfLines.Add($"内核（800 建筑 + 300 电塔 + 20 储能，{k.SubnetCount} 个电网）：拓扑重算 {rebuildMs:F3} ms/次，结算 {settleMs:F3} ms，储能积分 {stepMs:F3} ms/游戏秒，采样 {sampleMs:F4} ms");
            ExpectPerf(true,
                $"K9 性能（FG03 第 7 节 800 座建筑）：拓扑重算 {rebuildMs:F3} ms/次（阈值 6 ms，只在拓扑变化时）；结算 {settleMs:F3} ms（1 ms）；储能积分 {stepMs:F3} ms/游戏秒（2 ms）；曲线采样 {sampleMs:F4} ms（0.5 ms）",
                PerfGate.Le(rebuildMs, 6.0, "拓扑重算 ms"), PerfGate.Le(settleMs, 1.0, "结算 ms"), PerfGate.Le(stepMs, 2.0, "储能积分 ms"), PerfGate.Le(sampleMs, 0.5, "曲线采样 ms"));
        }

        // ── 家园工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgpower-" + seed, "Standard", seed);
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

        private static bool Placeable(CampaignState s, string typeId, GridCell c, string ignore = null)
        {
            GridPlacementResult r = HomeGridService.ValidatePlacement(s, typeId, c, 0, asPlayerPlacement: false, ignoreBuildingId: ignore, checkCost: false);
            return r.Ok || r.Reasons.All(x => x.Code == GridBlockReason.Fog);
        }

        internal struct Chain
        {
            public GridCell P1;
            public GridCell P2;
            public GridCell P3;
            public GridCell Gen;
            public GridCell Bay;
        }

        /// <summary>从核心朝某个方向找一条电塔链（B25：按格网规则找，不写死坐标）：P1 离核心 21 格、P2 再 7 格、P3 再 7 格；P3 外侧放发电机与维修台。</summary>
        private static bool FindChain(CampaignState s, string bayId, out Chain chain)
        {
            GridCell core = HomeGridService.CorePivot(s);
            var dirs = new[] { new Vector2Int(0, 1), new Vector2Int(-1, 0), new Vector2Int(0, -1), new Vector2Int(1, 0) };
            foreach (Vector2Int d in dirs)
            {
                var side = new Vector2Int(-d.y, d.x);
                for (int shift = -6; shift <= 6; shift += 3)
                {
                    GridCell At(int along, int across) => new GridCell(core.X + d.x * along + side.x * (across + shift), core.Y + d.y * along + side.y * (across + shift));
                    // 发电机与维修台放在 P3 外侧：离 P2 超过 8 格（只靠 P3 接入），离核心超过 26 格。
                    var c = new Chain { P1 = At(21, 0), P2 = At(28, 0), P3 = At(35, 0), Gen = At(40, 0), Bay = At(39, -5) };
                    if (Placeable(s, "power_pole", c.P1) && Placeable(s, "power_pole", c.P2) && Placeable(s, "power_pole", c.P3)
                        && Placeable(s, HomeValleyLayout.BuildingTypeGenerator2, c.Gen) && Placeable(s, HomeValleyLayout.BuildingTypeRepairBay, c.Bay, bayId))
                    {
                        chain = c;
                        return true;
                    }
                }
            }
            chain = default;
            return false;
        }

        /// <summary>场景夹具：一座已建成的建筑（真实建造路径见 F3）。</summary>
        internal static BuildingRecord AddBuilt(CampaignState s, string typeId, string key, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":selfcheck_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            return r;
        }

        private static void MoveTo(CampaignState s, BuildingRecord b, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(b.BuildingTypeId);
            b.GridX = pivot.X;
            b.GridY = pivot.Y;
            b.Rotation = 0;
            b.Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0);
            HomeGridService.Invalidate();
            HomeGridService.MapFor(s);
        }

        private static BuildingRecord Rec(CampaignState s, string anchor) => HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + anchor);

        /// <summary>链式场景：P1、P2、P3 三座电塔 + P3 旁一座发电机 2（80），维修台挪到 P3 旁（测试捷径：真实搬迁由 FG3-LOG-01 覆盖）。</summary>
        internal static bool LayChain(CampaignState s, out Chain c, out BuildingRecord p1, out BuildingRecord p2, out BuildingRecord p3, out BuildingRecord gen)
        {
            p1 = p2 = p3 = gen = null;
            BuildingRecord bay = Rec(s, "repair_bay");
            if (bay == null || !FindChain(s, bay.BuildingId, out c))
            {
                c = default;
                return false;
            }
            p1 = AddBuilt(s, "power_pole", "p1", c.P1);
            p2 = AddBuilt(s, "power_pole", "p2", c.P2);
            p3 = AddBuilt(s, "power_pole", "p3", c.P3);
            gen = AddBuilt(s, HomeValleyLayout.BuildingTypeGenerator2, "gen", c.Gen);
            MoveTo(s, bay, c.Bay);
            HomeValleyPowerGrid.Recompute(s);
            return true;
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId);

        private static int RepairMachine(CampaignState s) =>
            MachineRegistry.AllRecords.Where(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && HomeValleyWorkOrders.CanDoKind(m.ChassisId, WorkOrderKind.Repair))
                .Select(m => m.LogicId).DefaultIfEmpty(0).First();

        // ── F1 数据 ────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool nodes = HomeValleyPowerGrid.TryGetNodeDef("core", out PowerNodeDef core) && Mathf.Approximately(core.CoverRadius, 26f)
                         && Mathf.Approximately(HomeValleyPowerGrid.CoverRadiusOf("power_pole"), 8f) && Mathf.Approximately(HomeValleyPowerGrid.CoverRadiusOf("power_pole_t2"), 16f)
                         && HomeValleyPowerGrid.CoverRadiusOf("generator") == 0f && HomeValleyPowerGrid.IsPoleType("power_pole") && !HomeValleyPowerGrid.IsPoleType("core");
            bool tuning = Mathf.Approximately(HomeValleyPowerGrid.SampleSeconds, 10f) && HomeValleyPowerGrid.CurveSamples == 90;
            Expect(nodes && tuning, "F1 电力节点表 fg.TbPowerNode（FG03 第 10 章“电塔覆盖 T1 半径 8 格，T2 16 格”）+ 核心自带配电 26 格；曲线每 10 游戏秒一点、留 90 点（15 游戏分钟）");
            bool buildings = FgContentTables.TryGetBuilding("power_pole", out Building b1) && b1.BuildScrap == 3 && b1.RepairScrap == 2 && b1.PowerDemand == 0f && b1.PowerSupply == 0f
                             && FgContentTables.TryGetBuilding("power_pole_t2", out Building b2) && b2.BuildScrap == 8
                             && GridContent.TryGetBuilding("power_pole", out BuildingGrid g1) && g1.FootprintW == 1 && g1.FootprintH == 1 && g1.Placeable == 1 && g1.Category == "energy"
                             && HomeValleyLayout.BuildProfile.ContainsKey("power_pole") && HomeValleyLayout.RepairProfile.ContainsKey("power_pole")
                             && BuildCatalog.TryGet("power_pole", out BuildEntry be) && be.CategoryId == "energy" && BuildCatalog.TryGet("power_pole_t2", out _);
            Expect(buildings, "F1 电塔 T1 / T2 建筑行（FG04“电塔 1×1”，能源页签，3 / 8 废料，可修复）进建造菜单");
            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("PN\t", StringComparison.Ordinal)).ToArray();
            string[] rt = ConfigSystem.Instance.Tables.TbPowerNode.DataList
                .Select(r => string.Join("\t", "PN", r.TypeId, Py(r.CoverRadius), Py(r.StorageCapacity), Py(r.StorageRate))).ToArray();
            Expect(code == 0 && src.Length == 3 && src.SequenceEqual(rt), $"F1 电力节点表与源数据 fgdata_power.POWER_NODES 逐字段一致（{src.Length} 行）" + (code == 0 ? string.Empty : "：" + Tail(output)));
            string[] keys =
            {
                "building.power_pole.name", "building.power_pole.desc", "power.subnet.name", "power.preview.uncovered", "power.preview.pole_covers", "power.notify.split",
                "power.notify.unconnected", "power.panel.summary", "power.panel.curve_legend", "power.demolish.split", "codex.logistics.power.body", "tooltip.power.unconnected_of",
            };
            bool texts = keys.All(GameText.Has);
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = keys.All(key => !GameText.ContainsMarker(GameText.Get(key)) && GameText.Get(key).Length > 0) && GameText.Get("building.power_pole.name") == "Power Pole T1";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.PowerPoleFirstPlaced) && GuidanceHooks.Known.Contains(GuidanceHooks.PowerFirstBrownout)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.PowerPanelFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.PowerFirstSplit);
            var codex = ConfigSystem.Instance.Tables.TbCodexEntry;
            bool entry = codex.DataList.Any(x => x.Id == "codex.logistics.power" && x.Hooks.Contains(GuidanceHooks.PowerFirstSplit) && x.Hooks.Contains(GuidanceHooks.PowerPoleFirstPlaced));
            bool key = InputActionCatalog.TryGet(GameActionId.OpenPowerGrid, out InputActionDef def) && def.DefaultChord.Key == KeyCode.G && def.DefaultChord.Mods == InputModifier.Alt
                       && def.Status == InputActionStatus.Wired;
            bool notify = NotificationCatalog.TryGetType("power_split", out NotifyTypeDef t1) && t1.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("power_unconnected", out NotifyTypeDef t2) && t2.Tier == NotifyLevel.Warning;
            Expect(texts && en && hooks && entry && key && notify,
                "F1 新文本中英都有；4 个引导钩子（第一次建成电塔 / 缺电 / 打开电网面板 / 电网断开）；图鉴“电塔与电网”（FG03 第 4 节）；电网面板默认 Alt+G 可重绑；“电网断开”“失去电网连接”两种警告级通知");
        }

        private static string Py(float v)
        {
            // 与 fgdata.py 的 repr(float) 一致（整数值写成 26.0）。
            string s = ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("E") ? s : s + ".0";
        }

        // ── F2 新战役兼容 ─────────────────────────────────────────────────────────

        private static void CheckNewCampaignCompat()
        {
            foreach (int seed in new[] { 9601, 9602, 9603 })
            {
                CampaignState s = NewWorld(seed);
                // 修好发电机：开局唯一的发电建筑（真实修复工单 + 机器施工）。
                int machine = RepairMachine(s);
                HomeValleyWorkOrders.WorkOrderOpResult order = HomeValleyWorkOrders.TryCreateRepair(s, HomeValleyLayout.BuildingTypeGenerator, machine);
                bool repaired = order.Success && StepUntil(() => Rec(s, "generator")?.ConstructionState == BuildingConstructionState.Operational, 400);
                HomeValleyPowerGrid.GridSummary sum = HomeValleyPowerGrid.Recompute(s);
                // 旧的全局仲裁（ER3-PWR-01）独立重算一遍：核心 20 + 运转的发电机；消费者按优先级 → 编号贪心。
                float supply = HomeValleyLayout.BaseCoreSupply + s.BuildingRecords.Where(b => b.ConstructionState == BuildingConstructionState.Operational
                    && HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId)).Sum(b => HomeValleyLayout.PowerSupplyProfile[b.BuildingTypeId]);
                float remaining = supply;
                var expect = new Dictionary<string, BuildingPowerState>();
                foreach (BuildingRecord b in s.BuildingRecords.Where(b => b.ConstructionState == BuildingConstructionState.Operational && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
                             .OrderBy(b => b.PowerPriority).ThenBy(b => b.BuildingId, StringComparer.Ordinal))
                {
                    float need = HomeValleyLayout.PowerProfile[b.BuildingTypeId].PowerDemand;
                    if (remaining >= need)
                    {
                        remaining -= need;
                        expect[b.BuildingId] = BuildingPowerState.Powered;
                    }
                    else
                    {
                        expect[b.BuildingId] = BuildingPowerState.Brownout;
                    }
                }
                bool same = expect.All(kv => HomeGridService.FindBuilding(s, kv.Key).PowerState == kv.Value);
                Expect(repaired && sum.SubnetCount == 1 && sum.UnconnectedBuildingIds.Length == 0 && Mathf.Approximately(sum.TotalSupply, supply) && same && expect.Count >= 4,
                    $"F2 [种子 {seed}] 新战役：开局建筑与建议建造位全在归还核心的配电半径里（一个电网、0 座未接入），发电 {sum.TotalSupply}（核心 20 + 修好的发电机）；" +
                    $"{expect.Count} 座用电建筑的结果与旧的全局仲裁逐座一致（沿用并扩展 HomeValleyPowerGrid）");
            }
        }

        // ── F3 建造菜单真实输入 ─────────────────────────────────────────────────────────

        private static GridCell? FindFree(CampaignState s, string typeId, float fromCore, float toCore)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = fromCore; d <= toCore; d += 1f)
                {
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, typeId, c, 0).Ok)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static void CheckBuildMode()
        {
            CampaignState s = NewWorld(9611, scrap: 300);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                mode.Open();
                // 发电机 2 放在覆盖外：预览仍是合法的（只警告，不阻止，FGR-LOG-003）。
                GridCell? far = FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 31f, 40f);
                mode.Select(HomeValleyLayout.BuildingTypeGenerator2);
                if (far.HasValue)
                {
                    mode.SetHover(s, far.Value);
                    mode.RefreshPreview(s);
                }
                GridPlacementResult pv = mode.Preview;
                bool warned = far.HasValue && pv != null && pv.Ok && pv.Warnings.Any(w => w.Contains("超出电力覆盖") && w.Contains("格外"));
                bool overlayAuto = PowerCoverageOverlayView.AutoShown;
                PowerCoverageOverlayView.FrameTick();
                bool drawn = PowerCoverageOverlayView.Visible && PowerCoverageOverlayView.DrawnRings == 1 && !PowerCoverageOverlayView.PreviewShown;
                Expect(warned && overlayAuto && drawn,
                    $"F3 放置时预览电力覆盖（卡片必须同时交付 / FGR-LOG-003）：发电机放在覆盖外仍是合法预览，状态行警告“{pv?.Warnings.FirstOrDefault(w => w.Contains("电力"))}”；叠加层自动显示归还核心的覆盖圈（{PowerCoverageOverlayView.DrawnRings} 个）");
                // 电塔：预览写接入哪个电网、覆盖几座；光标处画 8 格的预览圈。
                GridCell? poleAt = FindFree(s, "power_pole", 22f, 25f);
                mode.Select("power_pole");
                if (poleAt.HasValue)
                {
                    mode.SetHover(s, poleAt.Value);
                    mode.RefreshPreview(s);
                }
                PowerCoverageOverlayView.FrameTick();
                GridPlacementResult pp = mode.Preview;
                bool notes = poleAt.HasValue && pp != null && pp.Ok && pp.Notes.Any(n => n.Contains("接入 电网 1")) && pp.Notes.Any(n => n.Contains("覆盖") && n.Contains("座建筑"));
                bool ring = PowerCoverageOverlayView.PreviewShown && Mathf.Approximately(PowerCoverageOverlayView.PreviewRadius, 8f);
                string hudStatus = pp != null ? string.Join(" / ", pp.Notes) : string.Empty;
                Expect(notes && ring, $"F3 选电塔指着核心附近：预览“{hudStatus}”；光标处画半径 {PowerCoverageOverlayView.PreviewRadius} 的预览圈");
                // 真实放下 → 机器取料施工 → 接入电网 1。
                mode.PointerDown(s, poleAt.Value);
                mode.PointerUp(s, poleAt.Value);
                BuildingRecord ghost = HomeGridService.BuildingAt(s, poleAt.Value);
                bool planned = ghost != null && ghost.BuildingTypeId == "power_pole" && ghost.ConstructionState != BuildingConstructionState.Operational;
                mode.ClearSelection();
                bool built = StepUntil(() => HomeGridService.BuildingAt(s, poleAt.Value)?.ConstructionState == BuildingConstructionState.Operational, 600);
                BuildingRecord pole = HomeGridService.BuildingAt(s, poleAt.Value);
                bool joined = built && HomeValleyPowerGrid.TryGetBuildingPower(s, pole.BuildingId, out BuildingPowerInfo info) && info.IsNode && info.Subnet >= 0
                              && HomeValleyPowerGrid.Kernel.Subnet(info.Subnet).Serial == 1 && HomeValleyPowerGrid.Kernel.Subnet(info.Subnet).Nodes == 2
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PowerPoleFirstPlaced) && MechanicCodex.IsUnlocked("codex.logistics.power");
                PowerCoverageOverlayView.FrameTick();
                Expect(planned && joined, $"F3 建造菜单单击放下电塔虚影 → 机器取料施工 → 建成后接入电网 1（节点 2 个：核心 + 电塔）；钩子“第一次建成电塔”与图鉴“电塔与电网”已解锁");
                // 电塔覆盖之外的核心配电之外：发电机放在电塔旁 → 预览写接入电网 1（不再警告）。
                GridCell? near = null;
                for (int dy = -7; dy <= 7 && near == null; dy++)
                {
                    for (int dx = -7; dx <= 7 && near == null; dx++)
                    {
                        var c = new GridCell(poleAt.Value.X + dx, poleAt.Value.Y + dy);
                        GridCell core = HomeGridService.CorePivot(s);
                        float dCore = Mathf.Sqrt((c.X - core.X) * (c.X - core.X) + (c.Y - core.Y) * (c.Y - core.Y));
                        if (dCore >= 28f && HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeGenerator2, c, 0).Ok)
                        {
                            near = c;
                        }
                    }
                }
                mode.Select(HomeValleyLayout.BuildingTypeGenerator2);
                if (near.HasValue)
                {
                    mode.SetHover(s, near.Value);
                    mode.RefreshPreview(s);
                }
                GridPlacementResult pg = mode.Preview;
                bool viaPole = near.HasValue && pg != null && pg.Ok && pg.Notes.Any(n => n.Contains("接入 电网 1")) && !pg.Warnings.Any(w => w.Contains("电力覆盖"));
                Expect(viaPole, $"F3 发电机放在电塔覆盖里（离核心 ≥ 28 格，只靠电塔）：预览写“接入电网 1”，不再警告");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── F4 电塔被摧毁一分为二（真实家园）───────────────────────────────────────────

        private static void CheckHomeSplit()
        {
            CampaignState s = NewWorld(9621, scrap: 300);
            if (!LayChain(s, out Chain c, out BuildingRecord p1, out BuildingRecord p2, out BuildingRecord p3, out BuildingRecord gen))
            {
                Fail("F4 家园附近放不下电塔链");
                return;
            }
            BuildingRecord bay = Rec(s, "repair_bay");
            BuildingRecord bench = Rec(s, "analysis_bench");
            BuildingRecord assembly = Rec(s, "assembly_station");
            WorldSimulation.StepMany(GameClock.StepHz * 25); // 曲线记两点
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            bool one = k.SubnetCount == 1 && bay.PowerState == BuildingPowerState.Powered && bench.PowerState == BuildingPowerState.Powered
                       && assembly.PowerState == BuildingPowerState.Powered && Mathf.Approximately(s.PowerCapacity, 100f);
            Expect(one, $"F4 断开前：核心—P1—P2—P3 一个电网，发电 {s.PowerCapacity}（核心 20 + P3 旁的发电机 80），维修台（挪到 P3 旁）、解析台、装配站都有电");
            int splits0 = NotifyCount("power_split");
            int lost0 = NotifyCount("power_lost");
            bool destroyed = HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p2.BuildingId);
            k = HomeValleyPowerGrid.Kernel;
            NotificationEntry split = LastOf("power_split");
            bool warned = destroyed && NotifyCount("power_split") == splits0 + 1 && split != null && split.Latest.HasLocation
                          && split.Text.Contains("电网 1") && split.Text.Contains("2 段") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PowerFirstSplit);
            HomeValleyPowerGrid.TryGetBuildingPower(s, p3.BuildingId, out BuildingPowerInfo farInfo);
            HomeValleyPowerGrid.TryGetBuildingPower(s, p1.BuildingId, out BuildingPowerInfo mainInfo);
            bool settle = k.SubnetCount == 2 && farInfo.Subnet != mainInfo.Subnet && bay.PowerState == BuildingPowerState.Powered
                          && bench.PowerState == BuildingPowerState.Brownout && assembly.PowerState == BuildingPowerState.Brownout
                          && Mathf.Approximately(k.Subnet(mainInfo.Subnet).Supply, 20f) && Mathf.Approximately(k.Subnet(farInfo.Subnet).Supply, 80f)
                          && NotifyCount("power_lost") > lost0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PowerFirstBrownout);
            Expect(warned && settle,
                $"F4 电塔被摧毁，电网一分为二（卡片负向 / FGT-LOG-008）：发“电网断开”警告（“{split?.Text}”，可点击定位），两段各自结算——" +
                "核心那段只剩 20：解析台、装配站缺电停机（发“缺电”）；发电机那段 80：维修台照常有电（盈余不跨网）");
            // 末端电塔也没了：维修台与发电机失去电网连接 → 警告、可定位。
            int cut0 = NotifyCount("power_unconnected");
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p3.BuildingId);
            NotificationEntry cut = LastOf("power_unconnected");
            bool unconnected = bay.PowerState == BuildingPowerState.Unpowered && NotifyCount("power_unconnected") == cut0 + 1 && cut != null && cut.Latest.HasLocation
                               && cut.Text.Contains(Campaign.Feedback.FeedbackCues.BuildingLabel(bay.BuildingId))
                               && HomeValleyPowerGrid.TryDescribeBuilding(s, bay, out string bayText) && bayText.Contains("未接入电网");
            HomeValleyPowerGrid.GridSummary sum = HomeValleyPowerGrid.GetSummary(s);
            Expect(unconnected && sum.UnconnectedBuildingIds.Contains(bay.BuildingId),
                $"F4 失去电网连接：P3 也没了，维修台“未接入电网”并发警告（“{cut?.Text}”）；HUD 汇总列出它");
            // 修复（真实修复工单 + 机器施工）：两座电塔依次修好 → 合并回电网 1，供电恢复。
            int machine = RepairMachine(s);
            int restored0 = NotifyCount("power_restored");
            bool r1 = HomeValleyWorkOrders.TryCreateRepair(s, "power_pole", machine).Success
                      && StepUntil(() => p2.ConstructionState == BuildingConstructionState.Operational || p3.ConstructionState == BuildingConstructionState.Operational, 600);
            bool r2 = HomeValleyWorkOrders.TryCreateRepair(s, "power_pole", machine).Success
                      && StepUntil(() => p2.ConstructionState == BuildingConstructionState.Operational && p3.ConstructionState == BuildingConstructionState.Operational, 600);
            k = HomeValleyPowerGrid.Kernel;
            bool merged = r1 && r2 && k.SubnetCount == 1 && k.Subnet(0).Serial == 1 && bay.PowerState == BuildingPowerState.Powered
                          && bench.PowerState == BuildingPowerState.Powered && NotifyCount("power_restored") > restored0;
            Expect(merged, "F4 修复电塔（修复工单优先修受损的那一座，机器取料施工）：修好后合并回电网 1，全部恢复供电并发“供电恢复”");
        }

        // ── F5 拆除确认 ─────────────────────────────────────────────────────────────

        private static void CheckDemolishConfirm()
        {
            CampaignState s = NewWorld(9631, scrap: 300);
            if (!LayChain(s, out Chain c, out BuildingRecord p1, out BuildingRecord p2, out BuildingRecord p3, out _))
            {
                Fail("F5 放不下电塔链");
                return;
            }
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? lone = FindFree(s, "power_pole", 10f, 18f);
            BuildingRecord extra = lone.HasValue ? AddBuilt(s, "power_pole", "lone", lone.Value) : null;
            HomeValleyPowerGrid.Recompute(s);
            bool needP2 = HomeGridService.DemolishNeedsConfirm(s, p2.BuildingId);
            bool needP3 = HomeGridService.DemolishNeedsConfirm(s, p3.BuildingId);
            bool noNeed = extra != null && !HomeGridService.DemolishNeedsConfirm(s, extra.BuildingId);
            Expect(needP2 && needP3 && noNeed, "F5 拆除确认按电网后果判定（FG00 B04）：中间的电塔（会断开）与末端电塔（建筑会失去连接）要确认；核心配电范围里的多余电塔直接拆");
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                mode.Open();
                mode.SetDemolishMode(true);
                // 框选拆除（FG-GAP-086 修复轮）：从 P2 旁的空地拖一个只框住 P2 的小框——数量没超 20、没有关键建筑，也要因为会断网先确认。
                GridCell? boxStart = null;
                foreach (GridCell d in new[] { new GridCell(-1, -1), new GridCell(1, -1), new GridCell(-1, 1), new GridCell(1, 1) })
                {
                    var cand = new GridCell(c.P2.X + d.X, c.P2.Y + d.Y);
                    if (HomeGridService.BuildingAt(s, cand) == null && HomeGridService.BuildingAt(s, new GridCell(c.P2.X - d.X, c.P2.Y - d.Y)) == null)
                    {
                        boxStart = cand;
                        break;
                    }
                }
                bool boxAsked = false, boxUntouched = false, boxPlanOk = false;
                string boxLine = null;
                if (boxStart.HasValue)
                {
                    var boxEnd = new GridCell(2 * c.P2.X - boxStart.Value.X, 2 * c.P2.Y - boxStart.Value.Y);
                    DemolishBoxPlan plan = HomeGridService.PlanDemolishBox(s, boxStart.Value, boxEnd);
                    boxPlanOk = plan.ToMark.Count == 1 && plan.ToMark[0] == p2.BuildingId && plan.CriticalNames.Count == 0 && plan.PowerSplitGrids == 1 && plan.NeedsConfirm;
                    mode.PointerDown(s, boxStart.Value);
                    mode.SetHover(s, boxEnd);
                    mode.RefreshVisuals(s);
                    mode.PointerUp(s, boxEnd);
                    boxLine = UiConfirmDialog.Current?.Consequences.FirstOrDefault(l => l.Contains("电网会断开"));
                    boxAsked = mode.PendingBatchConfirm && UiConfirmDialog.IsOpen && boxLine != null;
                    UiConfirmDialog.Cancel();
                    boxUntouched = !HomeGridService.IsMarkedForDemolish(s, p2.BuildingId) && p2.ConstructionState == BuildingConstructionState.Operational;
                }
                Expect(boxStart.HasValue && boxPlanOk && boxAsked && boxUntouched,
                    $"F5 框选拆除只框住中间电塔（1 座，不超 20、非关键）：按拆完后的整体拓扑判定会断网，先确认（“{boxLine}”），取消后什么都不变");
                mode.PointerDown(s, c.P2);
                mode.PointerUp(s, c.P2);
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Consequences.Any(l => l.Contains("断成 2 段"));
                UiConfirmDialog.Cancel();
                bool untouched = !HomeGridService.IsMarkedForDemolish(s, p2.BuildingId) && p2.ConstructionState == BuildingConstructionState.Operational;
                mode.PointerDown(s, c.P3);
                mode.PointerUp(s, c.P3);
                bool askedP3 = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Consequences.Any(l => l.Contains("失去电网连接"));
                UiConfirmDialog.Confirm();
                bool marked = HomeGridService.IsMarkedForDemolish(s, p3.BuildingId);
                Expect(asked && untouched && askedP3 && marked,
                    "F5 拆除模式点中间电塔：确认框写“拆掉后电网 1 会断成 2 段”，取消后什么都不变；点末端电塔：写“N 座建筑失去电网连接”，确认后标记拆除");
                bool gone = StepUntil(() => HomeGridService.FindBuilding(s, p3.BuildingId) == null, 600);
                BuildingRecord bay = Rec(s, "repair_bay");
                Expect(gone && bay.PowerState == BuildingPowerState.Unpowered && NotificationCenter.History.Any(n => n.Type?.Id == "power_unconnected"),
                    "F5 机器拆掉末端电塔后维修台未接入电网，并发“失去电网连接”警告");
            }
            finally
            {
                mode?.SetDemolishMode(false);
                mode?.Close();
                UiConfirmDialog.DiscardAll();
            }
        }

        // ── F6 电网面板 ─────────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9641, scrap: 300);
            if (!LayChain(s, out Chain c, out _, out BuildingRecord p2, out _, out _))
            {
                Fail("F6 放不下电塔链");
                return;
            }
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p2.BuildingId);
            WorldSimulation.StepMany(GameClock.StepHz * 35); // 每个电网记三点曲线
            VisualElement root = MountUxml(UiKitFolder + "PowerPanel.uxml", out GameObject go);
            PowerPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                PowerPanelUIToolkit panel = go.AddComponent<PowerPanelUIToolkit>();
                panel.BindView(root);
                PowerPanelUIToolkit.Open();
                bool opened = PowerPanelUIToolkit.IsOpen && panel.PanelVisible && panel.TitleText == "电网" && panel.SummaryText.Contains("2 个电网")
                              && panel.GridField.choices.Count == 2 && panel.GridField.choices[0].StartsWith("电网 1", StringComparison.Ordinal)
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PowerPanelFirstOpen) && !GameText.ContainsMarker(panel.SummaryText + panel.DetailText + panel.MembersText);
                Expect(opened, $"F6 电网面板（真 UXML）：“{panel.SummaryText}”；下拉框 {panel.GridField.choices.Count} 个电网");
                panel.GridField.value = panel.GridField.choices[1];
                int serial2 = panel.SelectedSerial;
                bool curve = HomeValleyPowerGrid.Kernel.TryGetCurve(serial2, out PowerCurve cv) && cv.Count >= 3 && panel.CurveTitleText.Contains("最近") && panel.DetailText.Contains("发电 80");
                Expect(serial2 == 2 && curve && panel.MembersText.Contains("维修台"),
                    $"F6 选电网 2：读数“{panel.DetailText.Split('\n')[0]}”；曲线 {cv?.Count} 个点（每 10 游戏秒一点，FGR-LOG-061）；用电建筑列出维修台");
                // 面板开着、世界照常走（没有储能、拓扑不变 → StateVersion 不变）：跨过一个采样周期后，定时刷新（Update 调的 Refresh(false)）也要把新点画上。
                PowerKernel pk = HomeValleyPowerGrid.Kernel;
                int stateV0 = pk.StateVersion, topoV0 = pk.TopologyVersion, samples0 = pk.SampleCount, points0 = cv?.Count ?? 0;
                panel.Refresh(force: false);
                int refresh0 = panel.RefreshCount; // 没有新采样时定时刷新什么都不做
                WorldSimulation.StepMany(GameClock.StepHz * 11);
                panel.Refresh(force: false);
                bool sampled = pk.SampleCount > samples0 && pk.StateVersion == stateV0 && pk.TopologyVersion == topoV0;
                bool redrawn = panel.RefreshCount == refresh0 + 1 && HomeValleyPowerGrid.Kernel.TryGetCurve(serial2, out PowerCurve cv2) && cv2.Count == points0 + 1
                               && panel.CurveTitleText.Contains("最近");
                panel.Refresh(force: false);
                bool idle = panel.RefreshCount == refresh0 + 1;
                Expect(sampled && redrawn && idle,
                    $"F6 面板保持打开跨过一个采样周期（内核状态版本不变、采样 {samples0}→{pk.SampleCount}）：定时刷新重画曲线（{points0}→{points0 + 1} 个点），之后没有新采样就不再刷新");
                panel.GridField.value = panel.GridField.choices[0];
                BuildingRecord bench = Rec(s, "analysis_bench");
                int bi = panel.MemberField.choices.FindIndex(x => x.Contains("解析台"));
                if (bi >= 0)
                {
                    panel.MemberField.value = panel.MemberField.choices[bi];
                }
                panel.PriorityField.value = panel.PriorityField.choices[3];
                bool prio = bench.PowerPriority == 4 && panel.MessageText.Contains("优先级改为 4");
                HomeValleyPowerGrid.GridSummary sum = HomeValleyPowerGrid.Recompute(s);
                bool order = sum.AllocationOrderBuildingIds.Last() == bench.BuildingId;
                Expect(bi >= 0 && prio && order, $"F6 选解析台、优先级下拉框选“优先级 4”即生效（“{panel.MessageText}”）：分配顺序里它排到最后");
                panel.ToggleShutdown();
                bool off = bench.ConstructionState == BuildingConstructionState.Disabled && panel.ShutdownButton.text == "重新启用";
                panel.ToggleShutdown();
                bool on = bench.ConstructionState == BuildingConstructionState.Operational;
                bool locate = panel.LocateGrid() && panel.LocateMember();
                bool overlayOn = panel.ToggleOverlay() && PowerCoverageOverlayView.Enabled && panel.OverlayButton.text == "隐藏电力覆盖";
                bool overlayOff = !panel.ToggleOverlay() && panel.OverlayButton.text == "显示电力覆盖";
                Expect(off && on && locate && overlayOn && overlayOff,
                    "F6 关停 / 重新启用（按钮文字随状态变，关停的建筑仍列在电网里）；“定位”电网与建筑时镜头飞过去；“显示 / 隐藏电力覆盖”开关叠加层");
                UiEscapeStack.CloseTop();
                Expect(!PowerPanelUIToolkit.IsOpen, "F6 Esc 关闭电网面板");
            }
            finally
            {
                PowerPanelUIToolkit.Close();
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            // 布局探针：中英 × 三种缩放，有数据的面板。
            PowerPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "PowerPanel.uxml", "PowerPanelWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_pw") { hideFlags = HideFlags.HideAndDontSave };
                            PowerPanelUIToolkit p = probeGo.AddComponent<PowerPanelUIToolkit>();
                            p.BindView(pr.panel.visualTree);
                            PowerPanelUIToolkit.Open();
                            p.Refresh(force: true);
                            PowerPanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("PowerPanelRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"F6 布局探针 PowerPanel.uxml#PowerPanelWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                PowerPanelUIToolkit.Close();
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
            }
        }

        // ── F7 悬停与叠加层 ─────────────────────────────────────────────────────────

        private static void CheckHoverAndOverlay()
        {
            CampaignState s = NewWorld(9651, scrap: 300);
            if (!LayChain(s, out Chain c, out BuildingRecord p1, out BuildingRecord p2, out BuildingRecord p3, out BuildingRecord gen))
            {
                Fail("F7 放不下电塔链");
                return;
            }
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p3.BuildingId);
            BuildingRecord bay = Rec(s, "repair_bay");
            bool poleText = HomeValleyPowerGrid.TryDescribeBuilding(s, p1, out string t1) && t1.Contains("电网 1 的节点") && t1.Contains("覆盖半径 8");
            bool bayText = HomeValleyPowerGrid.TryDescribeBuilding(s, bay, out string t2) && t2.Contains("未接入电网");
            bool genText = HomeValleyPowerGrid.TryDescribeBuilding(s, gen, out string t3) && t3.Contains("送不出去");
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            string status = HomeValleyBuildMode.DescribeBuilding(s, p1);
            Expect(poleText && bayText && genText && status.Contains("电网 1") && status.Contains("电网面板"),
                $"F7 悬停读数：电塔“{t1.Split('\n')[0]}”；维修台“{t2.Split('\n')[0]}”；发电机“{t3.Split('\n')[0]}”；建造模式指着建筑时状态行写电网与“按 Alt+G 打开电网面板”");
            PowerCoverageOverlayView.ResetForTests();
            PowerCoverageOverlayView.FrameTick();
            bool hidden = !PowerCoverageOverlayView.Visible;
            PowerCoverageOverlayView.SetEnabled(true);
            PowerCoverageOverlayView.FrameTick();
            // 期望的覆盖圈 = 归还核心 + 运转中的电塔（按建筑记录数，不读内核）。
            int nodes = 1 + s.BuildingRecords.Count(b => HomeValleyPowerGrid.IsPoleType(b.BuildingTypeId) && b.ConstructionState == BuildingConstructionState.Operational);
            int rings = PowerCoverageOverlayView.DrawnRings;
            int links = PowerCoverageOverlayView.DrawnLinks;
            int badges = PowerCoverageOverlayView.DrawnUnconnected;
            bool shown = PowerCoverageOverlayView.Visible && rings == nodes && links == nodes - HomeValleyPowerGrid.Kernel.SubnetCount && badges == 2;
            int redraws = PowerCoverageOverlayView.RedrawCount;
            for (int i = 0; i < 60; i++)
            {
                PowerCoverageOverlayView.FrameTick();
            }
            bool noRedraw = PowerCoverageOverlayView.RedrawCount == redraws;
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p2.BuildingId);
            PowerCoverageOverlayView.FrameTick();
            bool redrawn = PowerCoverageOverlayView.RedrawCount == redraws + 1;
            Expect(hidden && shown && noRedraw && redrawn,
                $"F7 电力覆盖叠加层（FGR-LOG-061）：默认不显示；打开后每个导电节点一个覆盖圈（{rings} / 期望 {nodes}：核心 + 运转中的电塔）、相连的节点之间连线（{links}）、" +
                $"没接入电网的建筑头顶“断电”标记（{badges} 座：维修台与发电机）；拓扑不变 60 帧不重画，电塔被摧毁后重画一次");
            PowerCoverageOverlayView.SetEnabled(false);
            try
            {
                mode.Open();
                mode.Select("power_pole_t2");
                mode.SetHover(s, c.P1);
                PowerCoverageOverlayView.FrameTick();
                bool preview = PowerCoverageOverlayView.Visible && PowerCoverageOverlayView.PreviewShown && Mathf.Approximately(PowerCoverageOverlayView.PreviewRadius, 16f);
                mode.Select("signal_relay");
                PowerCoverageOverlayView.FrameTick();
                bool notForRelay = !PowerCoverageOverlayView.Visible;
                Expect(preview && notForRelay, "F7 建造模式选电塔 T2：叠加层自动显示，光标处画 16 格的预览圈；选和电网无关的建筑（信号中继塔）不显示");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── F8 存读档 ─────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            string records = string.Join(",", s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId).OrderBy(b => b.BuildingId, StringComparer.Ordinal)
                .Select(b => b.BuildingId + ":" + (int)b.ConstructionState + (int)b.PowerState + b.PowerPriority));
            return (k != null ? k.Fingerprint() : "no-kernel") + " |cap=" + s.PowerCapacity.ToString("R") + " |" + records;
        }

        /// <summary>带储能的场景（自检注入：信号中继塔兼做 10 电·分钟储能，充放电 30；真实储能站在 FG4-ECO-04）。</summary>
        private static void InjectStorage()
        {
            var defs = ConfigSystem.Instance.Tables.TbPowerNode.DataList.Select(r => new PowerNodeDef(r.TypeId, r.CoverRadius, r.StorageCapacity, r.StorageRate)).ToList();
            defs.Add(new PowerNodeDef(HomeValleyLayout.BuildingTypeSignalRelay, 0f, 10f, 30f));
            HomeValleyPowerGrid.OverrideNodesForTests(defs);
        }

        private static bool LayStorageScenario(CampaignState s, out BuildingRecord p2, out BuildingRecord gen)
        {
            InjectStorage();
            p2 = gen = null;
            if (!LayChain(s, out Chain c, out _, out p2, out _, out gen))
            {
                return false;
            }
            GridCell? relay = FindFree(s, HomeValleyLayout.BuildingTypeSignalRelay, 8f, 18f);
            if (!relay.HasValue)
            {
                return false;
            }
            AddBuilt(s, HomeValleyLayout.BuildingTypeSignalRelay, "store", relay.Value);
            HomeValleyPowerGrid.Recompute(s);
            return true;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9661, scrap: 250);
            if (!LayStorageScenario(s, out BuildingRecord p2, out BuildingRecord gen))
            {
                Fail("F8 放不下场景");
                return;
            }
            try
            {
                WorldSimulation.StepMany(GameClock.StepHz * 22);
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p2.BuildingId); // 断开：核心那段靠储能放电
                WorldSimulation.StepMany(GameClock.StepHz * 13 + 7);
                string before = Snapshot(s);
                WorldSimulation.SyncAllForSave();
                SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                string continuous = Snapshot(s);

                string RunFromSave(out string loaded, int formatOverride = 0)
                {
                    WorldSimulation.UnloadAll();
                    GameClock.ResetSession();
                    HomeValleyPowerGrid.ResetForTests();
                    InjectStorage();
                    RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                    loaded = null;
                    if (!rr.Success)
                    {
                        return "读档失败：" + rr.Message;
                    }
                    if (formatOverride != 0)
                    {
                        rr.State.Power.FormatVersion = formatOverride;
                    }
                    CampaignSession.Set(Slot, rr.State);
                    HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                    WorldView.Observe(home.SiteId);
                    loaded = Snapshot(rr.State);
                    WorldSimulation.StepMany(GameClock.StepHz * 30);
                    return Snapshot(rr.State);
                }

                string first = RunFromSave(out string loaded1);
                CampaignState l = CampaignSession.Current;
                bool domain = l.Power.DomainVersion == 2 && l.Power.SubnetSerials.Length == 2 && l.Power.StorageIds.Length == 1 && l.Power.CurveCounts.Sum() > 0;
                string second = RunFromSave(out _);
                Expect(save.Success && loaded1 == before && domain,
                    "F8 真文件存读档（FG03 第 6 节“电网拓扑”）：电网编号、每个电网的曲线、储能存量写进存档；读档后按建筑记录重算拓扑，编号与结果逐字段一致" +
                    (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
                Expect(first == continuous && second == first,
                    "F8 读档后接着跑 30 游戏秒（储能放电 + 曲线采样），与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
                // 不认识的格式：原数据保留不覆盖，电网照常结算（曲线从零开始）。
                int n0 = NotifyCount("save_migrated");
                RunFromSave(out _, 99);
                CampaignState cur = CampaignSession.Current;
                int curves = cur.Power.CurveCounts.Length;
                WorldSimulation.SyncAllForSave();
                Expect(HomeValleyPowerGrid.SavedDataPreserved && cur.Power.FormatVersion == 99 && cur.Power.CurveCounts.Length == curves && curves > 0
                       && NotifyCount("save_migrated") > n0 && HomeValleyPowerGrid.Kernel.SubnetCount == 2,
                    "F8 不认识的电网存档格式：发“读档变更”说明、原始数据原样保留不覆盖；拓扑照常按建筑记录重算（2 个电网）");
            }
            finally
            {
                HomeValleyPowerGrid.OverrideNodesForTests(null);
            }
            // 旧存档（本 Story 之前：电网域 v1，建筑离核心太远）：读档后那座建筑未接入，发一条说明，不自动补电塔。
            CampaignState legacy = NewWorld(9662, scrap: 100);
            GridCell? far = FindFree(legacy, HomeValleyLayout.BuildingTypeGenerator2, 32f, 44f);
            BuildingRecord bay = Rec(legacy, "repair_bay");
            GridCell? farBay = FindFreeFor(legacy, HomeValleyLayout.BuildingTypeRepairBay, bay.BuildingId, 32f, 44f);
            if (farBay.HasValue)
            {
                MoveTo(legacy, bay, farBay.Value);
            }
            bay.PowerState = BuildingPowerState.Powered; // 旧全局电网下它是有电的
            legacy.Power = new PowerGridState();
            WorldSimulation.SyncAllForSave();
            legacy.Power = new PowerGridState(); // SyncAllForSave 会写新域：再次抹成旧档
            CampaignSaveService.Save(Slot, legacy, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            HomeValleyPowerGrid.ResetForTests();
            int m0 = NotifyCount("save_migrated");
            int u0 = NotifyCount("power_unconnected");
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignSession.Set(Slot, r.State);
            WorldSimulation.LoadHome(resume: true);
            BuildingRecord lb = HomeGridService.FindBuilding(r.State, bay.BuildingId);
            Expect(far.HasValue && farBay.HasValue && r.Success && lb.PowerState == BuildingPowerState.Unpowered && NotifyCount("save_migrated") == m0 + 1
                   && NotifyCount("power_unconnected") == u0
                   && NotificationCenter.History.Any(n => n.Type?.Id == "save_migrated" && n.Text.Contains("电塔覆盖")),
                "F8 旧存档（没有电网域）读档：离核心太远的维修台未接入电网，只发一条“电网改为电塔覆盖：N 座建筑……”说明（不逐座刷警告、不替玩家补电塔）");
        }

        private static GridCell? FindFreeFor(CampaignState s, string typeId, string ignore, float fromCore, float toCore)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = fromCore; d <= toCore; d += 1f)
                {
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (Placeable(s, typeId, c, ignore))
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        // ── F9 / F10 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 200);
            pausedHeld = true;
            try
            {
                if (!LayStorageScenario(s, out BuildingRecord p2, out _))
                {
                    return "放不下";
                }
                WorldSimulation.StepMany(GameClock.StepHz * 21);
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, p2.BuildingId);
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
            finally
            {
                HomeValleyPowerGrid.OverrideNodesForTests(null);
            }
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9671, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                shown.Add($"{speed}x");
            }
            Expect(paused && same && reference != "放不下",
                $"F9 暂停中（120 帧）电网与储能不动、不记曲线；{string.Join(" / ", shown)} 跑同样的 45 游戏秒（储能放电、曲线采样），内核指纹与每座建筑的供电结果逐位一致");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9681, true, 1f, false, out _);
            string unseen = RunScenario(9681, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "F10 同一组电网（两个子网、储能放电、曲线）在观察与不观察家园时跑 45 游戏秒逐字段一致（FGR-BASE-021）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── F11 家园性能 ─────────────────────────────────────────────────────────────

        /// <summary>单次家园重算（Editor JIT）上限：120 帧整帧预算 8.3 ms 的一半。</summary>
        private const double RecomputeBudgetMs = 4.0;
        /// <summary>热更层真机走 HybridCLR 解释执行的保守折算倍数（内核在 AOT，不折算）。</summary>
        private const double HotfixInterpretFactor = 5.0;
        /// <summary>折算后的真机单次重算上限：仍要留出大半帧给其余系统。</summary>
        private const double DeviceRecomputeBudgetMs = 6.0;

        private static void CheckHomePerformance()
        {
            CampaignState s = NewWorld(9691, scrap: 100);
            GridCell core = HomeGridService.CorePivot(s);
            var list = new List<BuildingRecord>(s.BuildingRecords);
            var rng = new System.Random(77);
            int side = 18; // 18 × 18 = 324 座电塔
            for (int i = 0; i < side * side; i++)
            {
                var pivot = new GridCell(core.X + 60 + (i % side) * 10, core.Y + (i / side) * 10);
                list.Add(Fixture("power_pole", "perf_pole_" + i, pivot));
            }
            string[] kinds = { HomeValleyLayout.BuildingTypeGenerator2, HomeValleyLayout.BuildingTypeRepairBay, HomeValleyLayout.BuildingTypeAnalysisBench, HomeValleyLayout.BuildingTypeAssemblyStation };
            int others = 800 - list.Count;
            for (int i = 0; i < others; i++)
            {
                var pivot = new GridCell(core.X + 62 + rng.Next(0, side * 10), core.Y + 2 + rng.Next(0, side * 10));
                list.Add(Fixture(kinds[i % kinds.Length], "perf_b_" + i, pivot));
            }
            s.BuildingRecords = list.ToArray();
            int total = s.BuildingRecords.Length;
            HomeValleyPowerGrid.Recompute(s); // 预热（首次调用含 JIT，不计入）
            const int N = 10;
            // 三种重算分开量：①只改状态（完工 / 损毁 / 修复 / 关停 / 旋转：记录数组不变，沿用分配顺序）；
            // ②游戏中最重的一种：新增一座建筑（放虚影），记录数组换了，在上一次顺序上增量插入；③首次绑定 / 读档：整体按字符串重排（一次性，载入时）。
            // 门槛：②③在 Editor 都不超过半帧；②（游戏进行中会发生）的热更层按解释执行折算后也要留出大半帧。
            Measure(0, out double recomputeMs, out double kernelMs, out double assembleMs, out double applyMs, out bool reused);
            Measure(1, out double addMs, out double addKernelMs, out double addAssembleMs, out double addApplyMs, out bool incremental);
            Measure(2, out double fullMs, out double fullKernelMs, out double fullAssembleMs, out double fullApplyMs, out bool fullOk);
            double addHotMs = Math.Max(0.0, addMs - addKernelMs);
            double fullHotMs = Math.Max(0.0, fullMs - fullKernelMs);
            // 真机热更层走 HybridCLR 解释执行：按 5 倍折算热更层那部分（内核在 AOT，不折算），估算真机单次重算。
            double deviceEstimateMs = addKernelMs + addHotMs * HotfixInterpretFactor;
            double deviceFullMs = fullKernelMs + fullHotMs * HotfixInterpretFactor;
            var sw = new Stopwatch();

            void Measure(int mode, out double total_, out double kernel_, out double assemble_, out double apply_, out bool path_)
            {
                total_ = kernel_ = assemble_ = apply_ = 0.0;
                path_ = true;
                for (int i = 0; i < N; i++)
                {
                    if (mode == 1)
                    {
                        var grown = new List<BuildingRecord>(s.BuildingRecords);
                        var at = new GridCell(core.X + 62 + (i * 7) % (side * 10), core.Y + 3 + (i * 13) % (side * 10));
                        grown.Add(Fixture(kinds[i % kinds.Length], "perf_add_" + i, at));
                        s.BuildingRecords = grown.ToArray();
                    }
                    else if (mode == 2)
                    {
                        HomeValleyPowerGrid.ForgetOrderForTests();
                        s.BuildingRecords = (BuildingRecord[])s.BuildingRecords.Clone();
                    }
                    long t0 = Stopwatch.GetTimestamp();
                    HomeValleyPowerGrid.Recompute(s);
                    total_ += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    kernel_ += HomeValleyPowerGrid.LastKernelRebuildMs;
                    assemble_ += HomeValleyPowerGrid.LastAssembleMs;
                    apply_ += HomeValleyPowerGrid.LastApplyMs;
                    path_ &= mode == 0 ? HomeValleyPowerGrid.LastAssembleReusedOrder
                        : mode == 1 ? HomeValleyPowerGrid.LastAssembleIncremental
                        : !HomeValleyPowerGrid.LastAssembleReusedOrder && !HomeValleyPowerGrid.LastAssembleIncremental;
                }
                total_ /= N;
                kernel_ /= N;
                assemble_ /= N;
                apply_ /= N;
            }
            // 增量插入的顺序必须和整体重排逐位相同（分配顺序 = 结算结果，AC-ECO-004）。
            HomeValleyPowerGrid.GridSummary incr = HomeValleyPowerGrid.Recompute(s);
            HomeValleyPowerGrid.ForgetOrderForTests();
            HomeValleyPowerGrid.GridSummary full = HomeValleyPowerGrid.Recompute(s);
            bool sameOrder = incr.AllocationOrderBuildingIds.SequenceEqual(full.AllocationOrderBuildingIds);
            int rebuilds = HomeValleyPowerGrid.Kernel.RebuildCount;
            int subnets = HomeValleyPowerGrid.Kernel.SubnetCount;
            // 稳态：拓扑不变时世界步不重算；世界步里电网的开销 = 每游戏秒一次积分（没储能 = 什么都不做）+ 每 10 秒一次采样。
            sw.Restart();
            for (int i = 0; i < GameClock.StepHz * 60; i++)
            {
                HomeValleyPowerGrid.WorldStep(s, GameClock.Ticks + i, GameClock.StepHz);
            }
            sw.Stop();
            double perStepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / (GameClock.StepHz * 60);
            bool steady = HomeValleyPowerGrid.Kernel.RebuildCount == rebuilds && HomeValleyPowerGrid.Kernel.SampleCount >= 6;
            // HUD 读汇总缓存（O(1)）：稳态里结算结果不变，汇总版本号不动（HUD 不重拼文本），缓存与逐座遍历的结果一致。
            bool cachedOk = HomeValleyPowerGrid.TryGetCachedSummary(s, out HomeValleyPowerGrid.GridSummary cachedSum);
            HomeValleyPowerGrid.GridSummary fullSum = HomeValleyPowerGrid.GetSummary(s);
            int summaryV0 = HomeValleyPowerGrid.SummaryVersion;
            for (int i = 0; i < GameClock.StepHz * 5; i++)
            {
                HomeValleyPowerGrid.WorldStep(s, GameClock.Ticks + GameClock.StepHz * 60 + i, GameClock.StepHz);
            }
            bool summaryStable = HomeValleyPowerGrid.SummaryVersion == summaryV0;
            bool summaryMatch = cachedOk && Mathf.Approximately(cachedSum.TotalSupply, fullSum.TotalSupply) && Mathf.Approximately(cachedSum.TotalDemand, fullSum.TotalDemand)
                                && new HashSet<string>(cachedSum.BrownoutBuildingIds).SetEquals(fullSum.BrownoutBuildingIds)
                                && new HashSet<string>(cachedSum.UnconnectedBuildingIds).SetEquals(fullSum.UnconnectedBuildingIds) && cachedSum.SubnetCount == fullSum.SubnetCount;
            PerfLines.Add($"家园（{total} 座建筑，其中 {side * side} 座电塔，{subnets} 个电网）：只改状态的一次重算 {recomputeMs:F3} ms（内核 {kernelMs:F3}、热更层组装 {assembleMs:F3}、写回 {applyMs:F3}）；" +
                          $"新增一座建筑（增量插入）{addMs:F3} ms（内核 {addKernelMs:F3}、组装 {addAssembleMs:F3}、写回 {addApplyMs:F3}）；首次绑定 / 读档（整体重排）{fullMs:F3} ms（组装 {fullAssembleMs:F3}）；" +
                          $"热更层按 HybridCLR 解释执行 ×{HotfixInterpretFactor} 折算的真机估计：新增建筑 {deviceEstimateMs:F3} ms、读档 {deviceFullMs:F3} ms；稳态世界步里电网平均 {perStepUs:F2} µs/步");
            ExpectPerf(total >= 800 && reused && incremental && fullOk && sameOrder && steady,
                $"F11 性能（FG03 第 7 节 800 座建筑；120 帧整帧预算 8.3 ms）：{total} 座建筑的家园只改状态的重算 {recomputeMs:F3} ms（沿用分配顺序）、新增一座建筑 {addMs:F3} ms（增量插入，顺序与整体重排逐位相同）、" +
                $"读档整体重排 {fullMs:F3} ms（阈值都是 {RecomputeBudgetMs} ms = 半帧，只在拓扑 / 状态变化点）；新增建筑的热更层 {addHotMs:F3} ms ×{HotfixInterpretFactor} 折算真机 {deviceEstimateMs:F3} ms（阈值 {DeviceRecomputeBudgetMs} ms）；" +
                $"拓扑不变的 60 游戏秒里重算 0 次、采样 {HomeValleyPowerGrid.Kernel.SampleCount} 次，世界步里电网平均 {perStepUs:F2} µs/步（阈值 50 µs；热更层每步 O(1)，积分与采样在 AOT 内核里 O(电网数)）",
                PerfGate.Le(recomputeMs, RecomputeBudgetMs, "只改状态重算 ms"), PerfGate.Le(addMs, RecomputeBudgetMs, "新增一座 ms"), PerfGate.Le(fullMs, RecomputeBudgetMs, "读档整体重排 ms"),
                PerfGate.Le(deviceEstimateMs, DeviceRecomputeBudgetMs, "新增建筑真机折算 ms"), PerfGate.Le(perStepUs, 50.0, "世界步电网 µs"));
            Expect(summaryMatch && summaryStable,
                $"F11 HUD 电网汇总读结算缓存（每帧 O(1)，不再逐帧遍历 {total} 座建筑）：缓存与逐座遍历一致（发电 {cachedSum.TotalSupply:0}、需要 {cachedSum.TotalDemand:0}、未接入 {cachedSum.UnconnectedBuildingIds.Length} 座）；稳态 5 游戏秒汇总版本不变（HUD 不重拼文本）");
        }

        private static BuildingRecord Fixture(string typeId, string key, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":selfcheck_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
        }

        // ── 通用 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgPowerGridSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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
                HomeValleyPowerGrid.OverrideNodesForTests(null);
                PowerCoverageOverlayView.SetEnabled(false);
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
