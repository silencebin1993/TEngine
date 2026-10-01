using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
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
    /// FG4-ECO-04 能源扩展的自动验收（FG04 第 3.3 节能源三行：燃油发电机 / 太阳能阵列 / 储能站；卡片“各类发电设施在电力曲线里分开显示”“储能站的充放电设置”；
    /// 负向“燃油耗尽”“储能站满或空”；承接 DEBT-FG2FW01-02 的范围变更“开火不扣电池、预览不出现耗电”；验收 FGT-ECO-002 的能源建筑部分）。
    /// 全部起真实系统：真实 AOT 电网内核（PowerKernel）、真实管线内核与生产建筑运行时（燃油从管线进燃油口、按负荷烧）、真实世界模拟与家园、真文件存读档、真 UXML 面板、真战斗内核开火。
    /// 内核（K）
    /// K1 发电类别：各类别发电分开记、类别系数（太阳能夜晚 0 / 沙暴 40%）、曲线每点存各类别、存档格式 2 往返、格式 1 旧档能读；K2 按负荷出力：燃油只补缺口与充电，负荷比例正确，没油时按优先级断电；
    /// K3 储能设置：关充电 / 关放电 / 放电只留给高优先级（窄档先取）、电量逐秒守恒；K4 储能满 / 空：满了不再充、空了按优先级断电；K5 性能。
    /// 正式（F）
    /// F1 数据（发电类别表逐字段、建筑 / 格网 / 生产参数 / 流体口 / 储能节点行、建造菜单“能源”、文本中英、钩子、图鉴、通知类型、调参）；
    /// F2 燃油发电机（真实家园：没接管线的原因、来油后发电、按负荷烧油逐毫升守恒、燃油耗尽 → 停机 + 低优先级断电 + 可定位警告 + 根因诊断、来油自动重启）；
    /// F3 产线：油井 → 流体泵 → 精炼塔 → 燃油管线 → 燃油发电机（FGT-ECO-001 的能源部分）；F4 太阳能（昼夜 / 沙暴接口、读数、HUD 来源、根因）；
    /// F5 储能站（充电、充满、放电保供、放电只给高优先级、关充电、放空警告）；F6 电网面板（真 UXML：分类发电图例、储能站一节的设置、点储能站打开面板；布局探针）；
    /// F7 状态矩阵（FGT-ECO-002：三座能源建筑的各状态都触发并写对原因）；F8 真文件存读档（燃油状态、储能设置、各类别曲线；接着跑一致）；F9 暂停与 0.5x～3x；F10 观察 / 不观察一致；
    /// F11 固件：44 条逐条核对“原本只有耗电”的折算进热量、预览 / 摘要 / 固件库不出现耗电、真实开火不扣电池；F12 建造菜单真实放置与施工。
    /// 地下管线（U，FG-GAP-082，由 FG4-ECO-02 改派到本 Story：燃油发电机的燃油管要穿过传送带主干线）
    /// U1 内核：两口朝向相对、跨度以内配对成同一网络；中间的格子放别的流体管线互不影响；超跨度 / 被同方向的口挡住不配对；配对会接错流体时拒绝；
    /// 中间插一口改配对；拆一口网络断开；存档往返；渲染编码与没配对的花纹；T2 跨度更长；流体经地下段送达。
    /// U2 真实家园：建造菜单选地下管线、旋转、单击放两口虚影（预览写配对）→ 机器施工 → 燃油从地下穿过一条传送带送到燃油发电机；悬停写连到哪一口。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEnergySelfCheck）。
    /// </summary>
    public static class FgEnergySelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string Fuel = "fuel_generator";
        private const string Solar = "solar_array";
        private const string Storage = "energy_storage";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly HashSet<string> SeenStates = new HashSet<string>();

        [MenuItem("BinGames/QA/自检/FG 能源扩展")]
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
            SeenStates.Clear();
            Line("\n[能源扩展] 燃油发电机 / 太阳能阵列 / 储能站、分类发电曲线、储能充放电设置、燃油耗尽、储能满空、固件不扣电池（FG4-ECO-04）");
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
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgenergy-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                HomeValleyPowerGrid.ResetForTests();
                PowerEnvironment.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "电网 / 管线内核在 AOT（Editor 下 Mono JIT，真机 IL2CPP），燃油发电机的烧油与电网写回在热更层（真机 HybridCLR 解释执行），真机另测（FG15-SYS-02）");

                Step(CheckKernelClasses);
                Step(CheckKernelDispatch);
                Step(CheckKernelStorageSettings);
                Step(CheckKernelFullEmpty);
                Step(CheckKernelPerformance);
                Step(CheckData);
                Step(CheckFuelGenerator);
                Step(CheckRefineryChain);
                Step(CheckSolar);
                Step(CheckStorageStation);
                Step(CheckPanel);
                Step(CheckStateMatrix);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckFirmwarePower);
                Step(CheckFiringNoBattery);
                Step(CheckBuildMenu);
                Step(CheckUndergroundKernel);
                Step(CheckUndergroundHome);
                Step(CheckUndergroundRemoveHome);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"能源扩展自检抛异常：{e}");
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
                PowerEnvironment.ResetForTests();
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
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
                PowerPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
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
            Line($"  · [能源扩展] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 内核工具 ─────────────────────────────────────────────────────────────

        private static int _nextKey = 100;
        private const byte ClsCore = 0, ClsGen = 1, ClsFuel = 2, ClsSolar = 3;

        private static PowerEntity Node(int x, int y, float r, float supply = 0f)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, NodeRadius = r, Conducts = true, Supply = supply, SupplyOn = supply > 0f, SourceClass = ClsCore };
        }

        private static PowerEntity Consumer(int x, int y, float demand, int priority)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, Demand = demand, DemandOn = true, Priority = priority };
        }

        private static PowerEntity Gen(int x, int y, float supply, byte cls, bool dispatch = false)
        {
            return new PowerEntity { Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, Supply = supply, SupplyOn = true, SourceClass = cls, Dispatchable = dispatch };
        }

        private static PowerEntity Store(int x, int y, double capacitySeconds, float rate, byte reserve = 0, bool noCharge = false, bool noDischarge = false)
        {
            return new PowerEntity
            {
                Key = _nextKey++, MinX = x, MinY = y, MaxX = x, MaxY = y, StorageCapacity = capacitySeconds, StorageRate = rate, StorageOn = true,
                StorageReserve = reserve, StorageNoCharge = noCharge, StorageNoDischarge = noDischarge,
            };
        }

        private static PowerKernel Build(PowerEntity[] e, int curve = 16)
        {
            var k = new PowerKernel(curve);
            k.Rebuild(e, e.Length);
            return k;
        }

        /// <summary>把储能实体的存量设成 <paramref name="seconds"/>（电·秒）：经快照恢复（内核唯一的写存量入口），再按同一组实体重建。</summary>
        private static PowerKernel WithStored(PowerEntity[] e, Dictionary<int, double> storedByIndex)
        {
            var k0 = new PowerKernel(16);
            var snap = new PowerSnapshot
            {
                StorageKeys = storedByIndex.Keys.Select(i => e[i].Key).ToArray(),
                StorageStored = storedByIndex.Values.ToArray(),
            };
            k0.Restore(snap, out _, out _);
            k0.Rebuild(e, e.Length);
            return k0;
        }

        private static bool Near(double a, double b, double eps = 0.01) => Math.Abs(a - b) <= eps;

        // ── K1 发电类别、类别系数、曲线、存档格式 ──────────────────────────────────────

        private static void CheckKernelClasses()
        {
            PowerEntity[] e =
            {
                Node(0, 0, 30f, 20f), Gen(3, 0, 80f, ClsGen), Gen(5, 0, 60f, ClsSolar), Gen(7, 0, 300f, ClsFuel, dispatch: true),
                Consumer(1, 1, 100f, 1), Consumer(2, 1, 150f, 2),
            };
            PowerKernel k = Build(e);
            PowerSubnetInfo n = k.Subnet(0);
            bool classes = k.SubnetCount == 1 && Near(k.ClassSupply(0, ClsCore), 20) && Near(k.ClassSupply(0, ClsGen), 80) && Near(k.ClassSupply(0, ClsSolar), 60)
                           && Near(k.ClassSupply(0, ClsFuel), 300) && Near(n.Supply, 460) && Near(n.DispatchSupply, 300)
                           && Near(n.DispatchLoad, (250f - 160f) / 300f, 1e-4);
            Expect(classes, $"K1 各类发电分开记：核心 {k.ClassSupply(0, ClsCore)}、发电机 {k.ClassSupply(0, ClsGen)}、太阳能 {k.ClassSupply(0, ClsSolar)}、燃油 {k.ClassSupply(0, ClsFuel)}；" +
                            $"燃油按负荷出力：需要 250、其余发电 160 → 负荷 {n.DispatchLoad:P1}（= 90 / 300）");
            bool night = k.SetClassFactor(ClsSolar, 0f);
            k.Settle();
            PowerSubnetInfo nn = k.Subnet(0);
            bool nightOk = night && Near(k.ClassSupply(0, ClsSolar), 0) && Near(nn.Supply, 400) && Near(nn.DispatchLoad, 150f / 300f, 1e-4) && Near(k.EffectiveSupply(2), 0);
            k.SetClassFactor(ClsSolar, 0.4f);
            k.Settle();
            bool storm = Near(k.ClassSupply(0, ClsSolar), 24) && Near(k.Subnet(0).DispatchLoad, (250f - 124f) / 300f, 1e-4) && !k.SetClassFactor(ClsSolar, 0.4f);
            Expect(nightOk && storm, $"K1 太阳能类别系数：夜晚 0 → 太阳能 0、燃油负荷升到 {nn.DispatchLoad:P0}；沙暴 40% → 太阳能 24、燃油负荷 {k.Subnet(0).DispatchLoad:P1}；同一系数再设不算变化");
            // 曲线：每点存各类别；快照（格式 2）往返逐字段一致；格式 1（FG3-LOG-06 的旧档，没有类别列）照常读，类别读成 0。
            k.Sample();
            k.SetClassFactor(ClsSolar, 1f);
            k.Settle();
            k.Sample();
            k.TryGetCurve(k.Subnet(0).Serial, out PowerCurve c);
            bool curve = c != null && c.Count == 2 && Near(c.GetClass(0, ClsSolar), 24) && Near(c.GetClass(1, ClsSolar), 60) && Near(c.GetClass(1, ClsFuel), 300)
                         && Near(c.GetClass(1, ClsGen), 80);
            PowerSnapshot snap = k.Serialize();
            var k2 = new PowerKernel(16);
            bool restored = snap.FormatVersion == 2 && snap.CurveClassSupply.Length == 2 * PowerKernel.MaxSourceClasses && k2.Restore(snap, out string err, out int dropped) && dropped == 0;
            k2.Rebuild(e, e.Length);
            k2.TryGetCurve(k2.Subnet(0).Serial, out PowerCurve c2);
            bool same = restored && c2 != null && c2.Count == 2 && Near(c2.GetClass(0, ClsSolar), 24) && Near(c2.GetClass(1, ClsFuel), 300);
            var v1 = new PowerSnapshot
            {
                FormatVersion = 1, NextSerial = snap.NextSerial, SubnetSerials = snap.SubnetSerials, SubnetAnchorKeys = snap.SubnetAnchorKeys, CurveCounts = snap.CurveCounts,
                CurveSupply = snap.CurveSupply, CurveDemand = snap.CurveDemand, CurveDelivered = snap.CurveDelivered, CurveStored = snap.CurveStored,
            };
            var k3 = new PowerKernel(16);
            bool oldOk = k3.Restore(v1, out _, out int d1) && d1 == 0;
            k3.Rebuild(e, e.Length);
            k3.TryGetCurve(k3.Subnet(0).Serial, out PowerCurve c3);
            bool oldCurve = oldOk && c3 != null && c3.Count == 2 && Near(c3.GetClass(1, ClsFuel), 0) && c3.Count == c.Count;
            var future = new PowerSnapshot { FormatVersion = 3 };
            bool newer = !new PowerKernel(16).Restore(future, out string why, out _) && why.Contains("format");
            var bad = new PowerSnapshot
            {
                FormatVersion = 2, NextSerial = snap.NextSerial, SubnetSerials = snap.SubnetSerials, SubnetAnchorKeys = snap.SubnetAnchorKeys, CurveCounts = snap.CurveCounts,
                CurveSupply = snap.CurveSupply, CurveDemand = snap.CurveDemand, CurveDelivered = snap.CurveDelivered, CurveStored = snap.CurveStored, CurveClassSupply = new float[3],
            };
            bool shape = !new PowerKernel(16).Restore(bad, out string why2, out _) && why2 == "shape";
            Expect(curve && same && oldOk && oldCurve && newer && shape,
                $"K1 曲线每个点存各类别发电（太阳能 24 → 60、燃油 300）；存档格式 2 往返逐字段一致；格式 1 旧档照常读（类别读成 0，{c3?.Count} 个点）；更新的格式 3 整体拒绝（{why}）；类别列长度不对拒绝（{why2}）");
        }

        // ── K2 按负荷出力、没油时按优先级断电 ─────────────────────────────────────────

        private static void CheckKernelDispatch()
        {
            PowerEntity[] e =
            {
                Node(0, 0, 30f, 20f), Gen(7, 0, 300f, ClsFuel, dispatch: true),
                Consumer(1, 1, 15f, 1), Consumer(2, 1, 50f, 2), Consumer(3, 1, 60f, 4),
            };
            PowerKernel k = Build(e);
            bool full = k.UseOf(2) == PowerUse.Powered && k.UseOf(3) == PowerUse.Powered && k.UseOf(4) == PowerUse.Powered && Near(k.LoadOf(1), 105f / 300f, 1e-4)
                        && Near(k.LoadOf(0), 1f);
            bool off = k.SetSupplyOn(1, false);
            k.Settle();
            bool cut = off && k.UseOf(2) == PowerUse.Powered && k.UseOf(3) == PowerUse.Brownout && k.UseOf(4) == PowerUse.Brownout && Near(k.Subnet(0).DispatchSupply, 0)
                       && Near(k.LoadOf(1), 0) && !k.SetSupplyOn(1, false);
            k.SetSupplyOn(1, true);
            k.Settle();
            bool back = k.UseOf(4) == PowerUse.Powered;
            // 没有缺口时燃油不出力（负荷 0）。
            PowerKernel idle = Build(new[] { Node(0, 0, 30f, 20f), Gen(7, 0, 300f, ClsFuel, dispatch: true), Consumer(1, 1, 15f, 1) });
            Expect(full && cut && back && Near(idle.LoadOf(1), 0),
                $"K2 燃油按负荷出力：核心 20 + 燃油 300，需要 125 → 燃油负荷 {k.Subnet(0).DispatchLoad:P1}（= 105 / 300）；没油（发电关）→ 只剩 20，按优先级先断 4、再断 2，优先级 1 照常；来油恢复；用电不超过核心时燃油负荷 0（不烧油）");
            // 有可充的储能：燃油也为充电出力；关掉充电后只补缺口。
            PowerKernel ch = Build(new[] { Node(0, 0, 30f, 20f), Gen(7, 0, 300f, ClsFuel, dispatch: true), Consumer(1, 1, 30f, 1), Store(2, 2, 6000, 50f) });
            PowerKernel noCh = Build(new[] { Node(0, 0, 30f, 20f), Gen(7, 0, 300f, ClsFuel, dispatch: true), Consumer(1, 1, 30f, 1), Store(2, 2, 6000, 50f, noCharge: true) });
            Expect(Near(ch.Subnet(0).StorageFlow, 50) && Near(ch.LoadOf(1), (30f + 50f - 20f) / 300f, 1e-4) && Near(noCh.Subnet(0).StorageFlow, 0) && Near(noCh.LoadOf(1), 10f / 300f, 1e-4),
                $"K2 储能充电也由燃油补：充电 {ch.Subnet(0).StorageFlow}（功率上限 50）→ 燃油负荷 {ch.Subnet(0).DispatchLoad:P1}；关掉充电 → 负荷 {noCh.Subnet(0).DispatchLoad:P1}（只补 10 的缺口）");
        }

        // ── K3 储能设置（关充电 / 关放电 / 放电只给高优先级），电量守恒 ─────────────────────────

        private static void CheckKernelStorageSettings()
        {
            // 发电 20、优先级 1 要 30、优先级 3 要 30；储能 A 只给优先级 1，存 600 电·秒、功率 50。
            PowerEntity[] e1 = { Node(0, 0, 30f, 20f), Consumer(1, 1, 30f, 1), Consumer(2, 1, 30f, 3), Store(3, 3, 6000, 50f, reserve: 1) };
            PowerKernel k = WithStored(e1, new Dictionary<int, double> { { 3, 600 } });
            PowerSubnetInfo n = k.Subnet(0);
            bool reserve = k.UseOf(1) == PowerUse.Powered && k.UseOf(2) == PowerUse.Brownout && n.ReserveHeld == 1 && Near(n.StorageFlow, -10) && Near(k.StorageFlowOf(3), -10);
            k.StepSecond();
            bool drained = Near(k.StoredOf(3), 590);
            Expect(reserve && drained,
                $"K3 放电只给优先级 1：优先级 1 从储能取 10 补上缺口、优先级 3 停机（储能里还有电但留给了高优先级，ReserveHeld {n.ReserveHeld}）；一秒后储能 600 → {k.StoredOf(3)}");
            // 两座储能：A 只给优先级 1、B 给所有建筑。优先级 1 先取窄档 A（10），优先级 3 从 B 取 30——窄档先取，宽档留给低优先级。
            PowerEntity[] e2 = { Node(0, 0, 30f, 20f), Consumer(1, 1, 30f, 1), Consumer(2, 1, 30f, 3), Store(3, 3, 6000, 50f, reserve: 1), Store(4, 3, 6000, 50f) };
            PowerKernel k2 = WithStored(e2, new Dictionary<int, double> { { 3, 600 }, { 4, 600 } });
            bool both = k2.UseOf(1) == PowerUse.Powered && k2.UseOf(2) == PowerUse.Powered && Near(k2.StorageFlowOf(3), -10) && Near(k2.StorageFlowOf(4), -30);
            double before = k2.StoredOf(3) + k2.StoredOf(4);
            float flow = k2.Subnet(0).StorageFlow;
            k2.StepSecond();
            bool conserved = Near(k2.StoredOf(3), 590) && Near(k2.StoredOf(4), 570) && Near(before + flow, k2.StoredOf(3) + k2.StoredOf(4), 1e-3);
            Expect(both && conserved,
                $"K3 窄档先取：优先级 1 从“只给优先级 1”的 A 取 10，优先级 3 从“给所有建筑”的 B 取 30，两座都有电；一秒后 A 590、B 570，总存量减少 = 放电功率 {-flow}（逐秒守恒）");
            // 关掉 A 的放电：优先级 1 也从 B 取，A 不动。
            PowerEntity[] e3 = { Node(0, 0, 30f, 20f), Consumer(1, 1, 30f, 1), Consumer(2, 1, 30f, 3), Store(3, 3, 6000, 50f, noDischarge: true), Store(4, 3, 6000, 50f) };
            PowerKernel k3 = WithStored(e3, new Dictionary<int, double> { { 3, 600 }, { 4, 600 } });
            k3.StepSecond();
            bool noDis = k3.UseOf(1) == PowerUse.Powered && k3.UseOf(2) == PowerUse.Powered && Near(k3.StoredOf(3), 600) && Near(k3.StoredOf(4), 560);
            // 只有一座关了放电的储能：缺口照样停机（不放电）。
            PowerEntity[] e3b = { Node(0, 0, 30f, 20f), Consumer(1, 1, 30f, 1), Store(3, 3, 6000, 50f, noDischarge: true) };
            PowerKernel k3b = WithStored(e3b, new Dictionary<int, double> { { 2, 600 } });
            bool held = k3b.UseOf(1) == PowerUse.Brownout && Near(k3b.Subnet(0).StorageDischargeAvailable, 0);
            // 关掉充电：盈余只充另一座；功率按上限。
            PowerEntity[] e4 = { Node(0, 0, 30f, 200f), Consumer(1, 1, 30f, 1), Store(3, 3, 6000, 50f, noCharge: true), Store(4, 3, 6000, 50f) };
            PowerKernel k4 = Build(e4);
            k4.StepSecond();
            bool noCh = Near(k4.StoredOf(2), 0) && Near(k4.StoredOf(3), 50) && Near(k4.Subnet(0).StorageChargeAvailable, 50);
            // 设置入口：改了才返回 true；放电对象超范围夹到 3。
            bool setter = k4.SetStorageSettings(2, false, false, 9) && k4.Entity(2).StorageReserve == 3 && !k4.SetStorageSettings(2, false, false, 3) && !k4.SetStorageSettings(1, true, true, 0);
            Expect(noDis && held && noCh && setter,
                "K3 关掉放电：这座不动（缺口改由另一座补，只有它时照常停机）；关掉充电：盈余只充另一座（功率 50）；设置入口改了才算变化、放电对象超范围夹到 3、不是储能的实体拒绝");
            // 修复轮 P2：电网满载（优先级 4 停机）时，剩下的 20 只充“放电只给优先级 1～2”的 A（够不着停机的优先级 4，不会反复启停），
            // “给所有建筑”的 B 不充（充进去马上放给优先级 4），并标明“因停机不充电”。
            PowerEntity[] e5 = { Node(0, 0, 30f, 80f), Consumer(1, 1, 30f, 1), Consumer(2, 1, 30f, 2), Consumer(3, 1, 50f, 4), Store(4, 3, 6000, 50f, reserve: 2), Store(5, 3, 6000, 50f) };
            PowerKernel k5 = Build(e5);
            bool brown5 = k5.UseOf(3) == PowerUse.Brownout && k5.UseOf(1) == PowerUse.Powered && k5.UseOf(2) == PowerUse.Powered;
            bool flows5 = Near(k5.Subnet(0).StorageFlow, 20) && Near(k5.StorageFlowOf(4), 20) && Near(k5.StorageFlowOf(5), 0)
                          && !k5.StorageChargeHeldByBrownout(4) && k5.StorageChargeHeldByBrownout(5);
            k5.StepSecond();
            bool stepped5 = Near(k5.StoredOf(4), 20) && Near(k5.StoredOf(5), 0) && k5.UseOf(3) == PowerUse.Brownout;
            // 只有“给所有建筑”的储能时照旧不充（防反复启停）。
            PowerEntity[] e6 = { Node(0, 0, 30f, 80f), Consumer(1, 1, 30f, 1), Consumer(2, 1, 30f, 2), Consumer(3, 1, 50f, 4), Store(4, 3, 6000, 50f) };
            PowerKernel k6 = Build(e6);
            bool allHeld = Near(k6.Subnet(0).StorageFlow, 0) && k6.StorageChargeHeldByBrownout(4) && !k6.StepSecond();
            Expect(brown5 && flows5 && stepped5 && allHeld,
                $"K3 满载时充窄档：优先级 4 停机，剩下的 20 只充“放电只给 1～2”的储能（{k5.StoredOf(4):0.##}），“给所有建筑”的不充并标明因停机不充（{k5.StoredOf(5):0.##}）；只有宽档储能时照旧不充");
        }

        // ── K4 储能满 / 空 ───────────────────────────────────────────────────────────

        private static void CheckKernelFullEmpty()
        {
            PowerEntity[] full = { Node(0, 0, 30f, 200f), Consumer(1, 1, 30f, 1), Store(3, 3, 600, 50f) };
            PowerKernel k = WithStored(full, new Dictionary<int, double> { { 2, 600 } });
            bool atCap = Near(k.Subnet(0).StorageFlow, 0) && Near(k.Subnet(0).StorageChargeAvailable, 0) && !k.StepSecond() && Near(k.StoredOf(2), 600);
            // 接近满：只充到容量（不溢出）。
            PowerKernel k2 = WithStored(full, new Dictionary<int, double> { { 2, 580 } });
            k2.StepSecond();
            bool capped = Near(k2.StoredOf(2), 600) && Near(k2.Subnet(0).StorageFlow, 0);
            Expect(atCap && capped, $"K4 储能满了：不再充（功率 0、积分不动）；差 20 时只充 20 到 600 为止，不溢出");
            // 放空：每秒放 40，存 100 → 第 3 秒放不满 40 → 优先级低的停机；存量不为负。
            PowerEntity[] empty = { Node(0, 0, 30f, 10f), Consumer(1, 1, 20f, 1), Consumer(2, 1, 30f, 2), Store(3, 3, 6000, 50f) };
            PowerKernel k3 = WithStored(empty, new Dictionary<int, double> { { 3, 100 } });
            bool start = k3.UseOf(1) == PowerUse.Powered && k3.UseOf(2) == PowerUse.Powered;
            k3.StepSecond();
            k3.StepSecond();
            bool mid = k3.UseOf(2) == PowerUse.Brownout && k3.UseOf(1) == PowerUse.Powered && Near(k3.StoredOf(3), 20);
            k3.StepSecond();
            bool end = k3.StoredOf(3) >= 0 && Near(k3.StoredOf(3), 10) && k3.UseOf(1) == PowerUse.Powered;
            k3.StepSecond();
            bool dry = Near(k3.StoredOf(3), 0) && k3.UseOf(1) == PowerUse.Brownout && k3.UseOf(2) == PowerUse.Brownout;
            Expect(start && mid && end && dry,
                "K4 储能放空：发电 10、需要 50，储能补 40/秒；存 100 用两秒后只剩 20（放不满 40）→ 优先级 2 先停机、优先级 1 由储能补 10；再一秒放到 0 → 优先级 1 也停机，存量不为负");
        }

        // ── K5 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckKernelPerformance()
        {
            var list = new List<PowerEntity>();
            var rng = new System.Random(4404);
            for (int i = 0; i < 120; i++)
            {
                list.Add(Node(i % 12 * 14, i / 12 * 14, 8f));
            }
            list.Add(Node(80, 70, 26f, 20f));
            for (int i = 0; i < 800; i++)
            {
                int x = rng.Next(0, 165), y = rng.Next(0, 135);
                list.Add(i % 16 == 0 ? Gen(x, y, 300f, ClsFuel, dispatch: true) : i % 16 == 1 ? Gen(x, y, 60f, ClsSolar) : i % 40 == 2 ? Store(x, y, 120000, 100f, (byte)(i % 4)) : Consumer(x, y, 10f, 1 + i % 4));
            }
            PowerEntity[] arr = list.ToArray();
            PowerKernel k = Build(arr, 90);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                k.SetClassFactor(ClsSolar, i % 2 == 0 ? 0.4f : 1f);
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
            PerfLines.Add($"内核（921 实体：800 建筑含 50 燃油 / 50 太阳能 / 20 储能，{k.SubnetCount} 个电网）：结算 {settleMs:F3} ms，储能积分 {stepMs:F3} ms/游戏秒，采样（含 8 类发电）{sampleMs:F4} ms");
            ExpectPerf(true,
                $"K5 性能（Editor batchmode，AOT 内核 Mono JIT）：结算 {settleMs:F3} ms（阈值 1 ms，只在燃油启停 / 光照变化 / 设置变化 / 储能积分时）；储能积分 {stepMs:F3} ms/游戏秒（2 ms）；采样 {sampleMs:F4} ms（0.5 ms）",
                PerfGate.Le(settleMs, 1.0, "结算 ms"), PerfGate.Le(stepMs, 2.0, "储能积分 ms"), PerfGate.Le(sampleMs, 0.5, "采样 ms"));
        }

        // ── F1 数据 ────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("PS\t", StringComparison.Ordinal)).ToArray();
            string[] rt = ConfigSystem.Instance.Tables.TbPowerSource.DataList
                .Select(r => string.Join("\t", "PS", r.TypeId, r.SourceClass, r.Kind, r.LegendKey, r.SortOrder.ToString(CultureInfo.InvariantCulture))).ToArray();
            Expect(code == 0 && src.Length == 5 && src.SequenceEqual(rt), $"F1 发电类别表 fg.TbPowerSource 与源数据 fgdata_energy.POWER_SOURCES 逐字段一致（{src.Length} 行）" + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));
            bool classes = HomeValleyPowerGrid.SourceClassCount == 4 && HomeValleyPowerGrid.SourceClassName(0) == "归还核心" && HomeValleyPowerGrid.SourceClassName(1) == "发电机"
                           && HomeValleyPowerGrid.SourceClassName(2) == "燃油发电机" && HomeValleyPowerGrid.SourceClassName(3) == "太阳能"
                           && HomeValleyPowerGrid.SourceOf("generator").ClassIndex == HomeValleyPowerGrid.SourceOf("generator_2").ClassIndex
                           && HomeValleyPowerGrid.IsFuelGeneratorType(Fuel) && HomeValleyPowerGrid.IsSolarType(Solar) && HomeValleyPowerGrid.IsStorageType(Storage)
                           && !HomeValleyPowerGrid.IsFuelGeneratorType("generator");
            Expect(classes, "F1 四个发电类别：归还核心 / 发电机（两座 Demo 发电机同一类）/ 燃油发电机（按负荷）/ 太阳能（昼夜系数）");
            bool rows = FgContentTables.TryGetBuilding(Fuel, out Building bf) && Mathf.Approximately(bf.PowerSupply, 300f) && bf.PowerDemand == 0f && bf.BuildScrap > 0
                        && FgContentTables.TryGetBuilding(Solar, out Building bs) && Mathf.Approximately(bs.PowerSupply, 60f)
                        && FgContentTables.TryGetBuilding(Storage, out Building bt) && bt.PowerSupply == 0f && bt.BuildScrap > 0
                        && GridContent.TryGetBuilding(Fuel, out BuildingGrid gf) && gf.FootprintW == 3 && gf.FootprintH == 3 && gf.Category == "energy"
                        && GridContent.TryGetBuilding(Solar, out BuildingGrid gs) && gs.FootprintW == 3 && gs.FootprintH == 3 && gs.Category == "energy"
                        && GridContent.TryGetBuilding(Storage, out BuildingGrid gt) && gt.FootprintW == 2 && gt.FootprintH == 2 && gt.Category == "energy"
                        && BuildCatalog.TryGet(Fuel, out BuildEntry e1) && e1.CategoryId == "energy" && BuildCatalog.TryGet(Solar, out _) && BuildCatalog.TryGet(Storage, out _);
            Expect(rows, "F1 建筑行（FG04 第 3.3 节：燃油发电机 3×3 供电 300、太阳能阵列 3×3 白天 60、储能站 2×2）进建造菜单“能源”页签");
            bool prod = ProducerCatalog.TryGet(Fuel, out ProducerDef pd) && pd.Mode == ProducerMode.Generator && Mathf.Approximately(pd.FluidLpm, 120f) && pd.InBatches == 30
                        && pd.FluidPorts.Count == 1 && !pd.FluidPorts[0].IsOutput && pd.FluidPorts[0].Fluid?.Id == "fuel" && ProducerCatalog.Problems.Count == 0;
            bool node = HomeValleyPowerGrid.TryGetNodeDef(Storage, out PowerNodeDef nd) && Mathf.Approximately(nd.StorageCapacity, 2000f) && nd.StorageRate > 0f && nd.CoverRadius == 0f;
            bool hasSf = GridContent.TryGetTuning("power.solar.sandstorm_factor", out float sf);
            bool hasRs = GridContent.TryGetTuning("power.fuel.restart_seconds", out float rs);
            bool tuning = hasSf && Mathf.Approximately(sf, 0.4f) && hasRs && rs > 0f && HomeValleyPowerGrid.MaxReserveLevel == 3;
            Expect(prod && node && tuning, $"F1 燃油发电机走生产建筑运行时（mode generator，满负荷 120 升/分钟、机内 30 秒燃油，南侧燃油口只收燃油）；储能站节点行 2,000 电·分钟；调参：沙暴系数 {sf}、重新发电 {rs} 秒");
            string[] keys =
            {
                "building.fuel_generator.name", "building.fuel_generator.desc", "building.solar_array.desc", "building.energy_storage.desc", "power.source.fuel",
                "power.hover.sources", "power.hover.fuel_load", "power.state.solar", "power.state.storage", "power.storage.discharge_upto", "power.panel.storage_title",
                "power.panel.curve_sources", "prod.reason.no_fuel", "prod.reason.working_generator", "prod.panel.generator", "power.notify.fuel_out", "power.notify.storage_empty",
                "diag.step.supply_no_fuel", "diag.step.supply_dark", "codex.economy.energy.body", "tooltip.power.storage_of",
            };
            bool texts = keys.All(GameText.Has);
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = keys.All(key => !GameText.ContainsMarker(GameText.Get(key)) && GameText.Get(key).Length > 0) && GameText.Get("building.fuel_generator.name") == "Fuel Generator";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.EnergyFirstBuilt) && GuidanceHooks.Known.Contains(GuidanceHooks.EnergyFirstFuelOut)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EnergyFirstStorageEmpty);
            bool codex = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.Any(x => x.Id == "codex.economy.energy" && x.Hooks.Contains(GuidanceHooks.EnergyFirstBuilt) && x.Hooks.Contains(GuidanceHooks.EnergyFirstFuelOut));
            bool notify = NotificationCatalog.TryGetType("power_fuel_out", out NotifyTypeDef t1) && t1.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("power_storage_empty", out NotifyTypeDef t2) && t2.Tier == NotifyLevel.Warning;
            Expect(texts && en && hooks && codex && notify, "F1 新文本中英都有；3 个引导钩子（第一次建成能源建筑 / 燃油耗尽 / 储能放空）；图鉴“能源”；“燃油耗尽”“储能放空”两种警告级通知");
        }

        // ── 家园工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            _fixtureSeq = 9000;
            return FgProductionSelfCheck.NewWorld(seed, observe, scrap);
        }

        private sealed class FuelRig
        {
            public BuildingRecord Generator;
            public GridCell Pivot;
            public GridCell FeedCell;
            public int Feed = -1;
        }

        /// <summary>核心配电半径内放一座燃油发电机，南侧燃油口外铺两格管线（不接燃料：由 <see cref="Feed"/> 挂测试供油点）。按种子地形找空地（B25）。</summary>
        private static FuelRig LayFuel(CampaignState s, string key)
        {
            GridCell? o = FgProductionSelfCheck.FindArea(s, 5, 7, 8f, 22f);
            if (o == null)
            {
                return null;
            }
            GridCell pivot = FgProductionSelfCheck.At(o.Value, 2, 4);
            var rig = new FuelRig { Pivot = pivot, FeedCell = FgProductionSelfCheck.At(pivot, 0, -3) };
            rig.Generator = Built(s, Fuel, key, pivot);
            return rig;
        }

        private static bool Pipes(CampaignState s, FuelRig rig) =>
            PipeNetworkService.TryPlace(s, FgProductionSelfCheck.At(rig.Pivot, 0, -2), PipePieceKind.Pipe, 0, 0).Ok
            && PipeNetworkService.TryPlace(s, rig.FeedCell, PipePieceKind.Pipe, 0, 0).Ok;

        /// <summary>测试供油点：在供油管线格挂一个只出燃油的供给者（相当于“上游有一座储罐 / 精炼塔”），存量 <paramref name="liters"/> 升。</summary>
        private static void Feed(FuelRig rig, long liters)
        {
            ItemCatalog.TryGet("fuel", out ItemDef fuel);
            rig.Feed = PipeNetworkService.Kernel.AddProducer(rig.FeedCell.X, rig.FeedCell.Y, fuel.FluidId, Math.Max(1000L, liters * 1000L), liters * 1000L);
        }

        private static long StopFeed(FuelRig rig)
        {
            long left = 0;
            if (rig.Feed >= 0)
            {
                PipeNetworkService.Kernel.RemoveProducer(rig.Feed, out left);
                rig.Feed = -1;
            }
            return left;
        }

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) => FgProductionSelfCheck.P(s, b);

        private static int _fixtureSeq = 9000;

        /// <summary>测试捷径：直接登记一座已建成的建筑（真实放置 / 施工由 F12 覆盖）。建筑 ID 按正式格式“类型#序号”，名字、通知、悬停按类型显示。</summary>
        private static BuildingRecord Built(CampaignState s, string typeId, string key, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_fixtureSeq++).ToString(CultureInfo.InvariantCulture),
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
            _ = key;
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return r;
        }

        private static void Seen(ProductionService.Producer p)
        {
            if (p != null)
            {
                SeenStates.Add(p.Def.TypeId + ":" + p.State);
            }
        }

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        /// <summary>FG4-ECO-05：通用面板“电网…”按钮做的事（ProductionPanelUIToolkit.OpenGrid：收起通用面板、打开电网面板并选中这座）。</summary>
        private static bool OpenGridFromBuildingPanel(string buildingId)
        {
            ProductionPanelUIToolkit.Close();
            PowerPanelUIToolkit.OpenFor(buildingId);
            return true;
        }

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId);

        private static BuildingRecord Rec(CampaignState s, string anchor) => HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + anchor);

        /// <summary>家园里运转中的用电建筑（按分配顺序：优先级 → 编号）。</summary>
        private static List<BuildingRecord> Consumers(CampaignState s) =>
            s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational
                                         && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId))
                .OrderBy(b => b.PowerPriority).ThenBy(b => b.BuildingId, StringComparer.Ordinal).ToList();

        private static long ConsumerTotal(ProductionService.Producer p) =>
            p.Rec.FluidHandles[0] >= 0 && PipeNetworkService.Kernel.TryGetConsumer(p.Rec.FluidHandles[0], out PipeConsumerInfo i) ? i.TotalDeliveredMl : 0;

        // ── F2 燃油发电机 ─────────────────────────────────────────────────────────

        private static void CheckFuelGenerator()
        {
            CampaignState s = NewWorld(4421, scrap: 300);
            FuelRig rig = LayFuel(s, "fuel");
            if (rig == null)
            {
                Fail("F2 核心附近放不下燃油发电机");
                return;
            }
            ProductionService.Producer p = P(s, rig.Generator);
            Seconds(2f);
            Seen(p);
            string noPipe = FgProductionSelfCheck.Reason(s, p);
            bool hookBuilt = GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnergyFirstBuilt);
            bool unfueled = p.State == ProdState.MissingFluid && p.Reason == ProdReason.NoFuel && noPipe.Contains("没有燃油") && noPipe.Contains("燃油口没接管线")
                            && HomeValleyPowerGrid.AvailableSupplyOf(s, rig.Generator.BuildingId) == 0f
                            && HomeValleyPowerGrid.TryDescribeBuilding(s, rig.Generator, out string hover0) && hover0.Contains("没有燃油");
            List<BuildingRecord> cons = Consumers(s);
            float demand = cons.Sum(b => HomeValleyLayout.PowerProfile[b.BuildingTypeId].PowerDemand);
            bool shortBefore = cons.Any(b => b.PowerState == BuildingPowerState.Brownout);
            Expect(unfueled && hookBuilt && shortBefore,
                $"F2 燃油发电机建成、没接燃油：“{ProductionService.StateText(p)}”（“{noPipe.Replace("\n", " · ")}”），不发电；家园只有核心 20、需要 {demand} → 有建筑缺电；第一次建成能源建筑的钩子埋了");
            bool laid = Pipes(s, rig);
            Feed(rig, 200);
            bool fueled = StepUntil(() => p.Rec.Fueled, 30);
            Seconds(3f);
            Seen(p);
            float load = HomeValleyPowerGrid.GeneratorLoad(s, rig.Generator.BuildingId);
            float expectLoad = Mathf.Clamp01((demand - HomeValleyLayout.BaseCoreSupply) / 300f);
            bool powered = cons.All(b => b.PowerState == BuildingPowerState.Powered) && Mathf.Abs(load - expectLoad) < 1e-3f && p.State == ProdState.Working
                           && FgProductionSelfCheck.Reason(s, p).Contains("发电中") && Mathf.Approximately(s.PowerCapacity, HomeValleyLayout.BaseCoreSupply + 300f);
            Expect(laid && fueled && powered,
                $"F2 接上燃油（管线 + 供油点）：机内攒够 {ProductionService.GeneratorRestartMl(p) / 1000} 升后开始发电，全部用电建筑有电；负荷 {load:P1} =（需要 {demand} − 核心 20）÷ 300；" +
                $"“{FgProductionSelfCheck.Reason(s, p)}”；HUD 发电 {s.PowerCapacity}");
            // 按负荷烧油，逐毫升守恒：整整 60 游戏秒烧掉 = 满负荷 120 升 × 负荷；送进燃油口的 = 烧掉的 + 机内存的。
            long burned0 = p.Rec.FuelBurnedMl;
            long perMinute = (long)Math.Round(120000.0 * load);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            long burned = p.Rec.FuelBurnedMl - burned0;
            long delivered = ConsumerTotal(p);
            long inside = ProductionService.GeneratorFuelMl(p);
            Expect(burned == perMinute && delivered == p.Rec.FuelBurnedMl + inside,
                $"F2 按负荷烧油：60 游戏秒烧掉 {burned} 毫升 = 120 升 × 负荷 {load:P1} = {perMinute}；送进燃油口 {delivered} = 累计烧掉 {p.Rec.FuelBurnedMl} + 机内 {inside}（逐毫升守恒）");
            // 燃油耗尽：断掉供油 → 机内的烧完 → 停机，低优先级先断电，发“燃油耗尽”（可定位）+ 钩子；根因诊断追到“燃油发电机没有燃油”。
            int out0 = NotifyCount("power_fuel_out");
            int lost0 = NotifyCount("power_lost");
            StopFeed(rig);
            bool ranDry = StepUntil(() => !p.Rec.Fueled, 400);
            Seconds(1f);
            Seen(p);
            NotificationEntry fo = LastOf("power_fuel_out");
            BuildingRecord dark = cons.LastOrDefault(b => b.PowerState == BuildingPowerState.Brownout);
            DiagReport rep = dark != null ? RootCauseDiagnosis.DiagnoseBuilding(s, dark) : null;
            DiagChain chain = rep?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Power);
            bool diag = chain != null && chain.Steps.Any(st => st.Code == DiagCode.SupplyNoFuel && st.TargetId == rig.Generator.BuildingId);
            bool order = cons.Where(b => b.PowerState == BuildingPowerState.Brownout).All(b => cons.Where(x => x.PowerState == BuildingPowerState.Powered).All(x => x.PowerPriority <= b.PowerPriority));
            bool outOk = ranDry && fo != null && fo.Latest.HasLocation && NotifyCount("power_fuel_out") == out0 + 1 && NotifyCount("power_lost") > lost0
                         && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnergyFirstFuelOut) && p.State == ProdState.MissingFluid && p.Reason == ProdReason.NoFuel
                         && dark != null && order && diag && Mathf.Approximately(s.PowerCapacity, HomeValleyLayout.BaseCoreSupply);
            Expect(outOk,
                $"F2 燃油耗尽（卡片负向）：停机、发电回到 {s.PowerCapacity}；按优先级从低到高断电；警告“{fo?.Text}”可定位；钩子；缺电建筑的根因链追到“{chain?.Steps.LastOrDefault()?.Text}”");
            // 来油自动重启，供电恢复。
            int rest0 = NotifyCount("power_restored");
            Feed(rig, 200);
            bool restarted = StepUntil(() => p.Rec.Fueled, 30);
            Seconds(1f);
            Expect(restarted && cons.All(b => b.PowerState == BuildingPowerState.Powered) && NotifyCount("power_restored") > rest0 && NotifyCount("power_fuel_out") == out0 + 1,
                "F2 重新送燃油：攒够 5 秒满负荷的燃油后自动重新发电，全部恢复供电并发“供电恢复”（燃油耗尽只报一次）");
            // 拆掉供油、断电网：没接入电网的燃油发电机不烧油、写明原因。
            StopFeed(rig);
            Feed(rig, 200);
            Seconds(2f);
            long b0 = p.Rec.FuelBurnedMl;
            foreach (BuildingRecord b in cons.Where(b => b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore))
            {
                b.ConstructionState = BuildingConstructionState.Disabled;
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(10f);
            Seen(p);
            bool idle = p.State == ProdState.Idle && p.Reason == ProdReason.GeneratorIdle && p.Rec.FuelBurnedMl == b0 && HomeValleyPowerGrid.GeneratorLoad(s, rig.Generator.BuildingId) == 0f;
            foreach (BuildingRecord b in cons.Where(b => b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore))
            {
                b.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            Expect(idle, $"F2 用电只剩核心（核心自带 20 够用）：燃油发电机“{ProductionService.StateText(p)}”（“{FgProductionSelfCheck.Reason(s, p)}”），10 游戏秒一滴不烧");
        }

        // ── F3 产线：油井 → 流体泵 → 精炼塔 → 燃油发电机 ───────────────────────────────

        private static void CheckRefineryChain()
        {
            CampaignState s = NewWorld(4431, scrap: 500);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 9, 18, 6f, 24f);
            if (o == null)
            {
                Fail("F3 找不到 9×18 的空地");
                return;
            }
            // 南→北：流体泵（压油井）→ 管线 → 精炼塔（南口原油、北口燃油）→ 管线 → 燃油发电机南口。酸液东口接废液池。
            GridCell tower = FgProductionSelfCheck.At(o.Value, 3, 6);
            GridCell pumpPivot = FgProductionSelfCheck.At(tower, -1, -5);
            FgProductionSelfCheck.SetFootprint(s, "fluid_pump", pumpPivot, "oil");
            BuildingRecord pump = Built(s, "fluid_pump", "f3pump", pumpPivot);
            BuildingRecord tw = Built(s, "refinery_tower", "f3tower", tower);
            GridCell genPivot = FgProductionSelfCheck.At(tower, 0, 6);
            BuildingRecord gen = Built(s, Fuel, "f3gen", genPivot);
            ProductionService.Producer pp = P(s, pump);
            ProductionService.Producer tp = P(s, tw);
            ProductionService.Producer gp = P(s, gen);
            // 泵东口（随朝向）出 → 往北接到塔南口外那一格。
            GridCell pumpOut = pp.Fluids[0].PipeCell;
            GridCell towerIn = tp.Fluids[ProductionService.FluidPortIndex(tp, ItemCatalog.TryGet("crude", out ItemDef crude) ? crude : null, false)].PipeCell;
            GridCell towerOut = tp.Fluids[ProductionService.FluidPortIndex(tp, ItemCatalog.TryGet("fuel", out ItemDef fuel) ? fuel : null, true)].PipeCell;
            GridCell genIn = gp.Fluids[0].PipeCell;
            bool laid = PipeLine(s, pumpOut, towerIn) && PipeLine(s, towerOut, genIn);
            GridCell acidCell = tp.Fluids[ProductionService.FluidPortIndex(tp, ItemCatalog.TryGet("acid", out ItemDef acid) ? acid : null, true)].PipeCell;
            BuildingRecord pond = PipeNetworkService.TryPlace(s, acidCell, PipePieceKind.Pipe, 0, 0).Ok
                ? Built(s, "waste_pond", "f3pond", FgProductionSelfCheck.At(acidCell, 2, 0)) : null;
            bool ran = StepUntil(() => gp.Rec.Fueled, 180);
            // 来油后关掉两座 Demo 发电机：泵（5）与精炼塔（15）的电改由燃油发电机发——燃油产线自己养活自己。
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2))
            {
                b.ConstructionState = BuildingConstructionState.Disabled;
            }
            HomeValleyPowerGrid.Recompute(s);
            long made0 = tp.Rec.Completed;
            Seconds(40f);
            tp = P(s, tw);
            gp = P(s, gen);
            pp = P(s, pump);
            Seen(gp);
            bool chain = laid && pond != null && ran && tp.Rec.Completed > made0 && gp.Rec.FuelBurnedMl > 0 && pp.Rec.PumpedMl > 0
                         && HomeValleyPowerGrid.AvailableSupplyOf(s, gen.BuildingId) > 0f && pump.PowerState == BuildingPowerState.Powered && tw.PowerState == BuildingPowerState.Powered;
            Expect(chain,
                $"F3 产线（FGT-ECO-001 能源部分）：油井上的流体泵抽原油 {pp.Rec.PumpedMl / 1000} 升 → 精炼塔做了 {tp.Rec.Completed} 份燃油 → 管线 → 燃油发电机烧掉 {gp.Rec.FuelBurnedMl / 1000} 升发电；" +
                "关掉两座 Demo 发电机后泵与精炼塔仍有电（燃油发电机自己养活这条产线），精炼塔接着出燃油" +
                (chain ? string.Empty : $"（铺管 {laid}、废液池 {pond != null}、来油 {ran}；塔“{FgProductionSelfCheck.Reason(s, tp)}”；发电机“{FgProductionSelfCheck.Reason(s, gp)}”）"));
        }

        /// <summary>从 a 到 b 铺一条 L 形管线（先竖后横），两端都铺。</summary>
        private static bool PipeLine(CampaignState s, GridCell a, GridCell b)
        {
            bool ok = true;
            int x = a.X, y = a.Y;
            ok &= Place(s, x, y);
            while (y != b.Y)
            {
                y += Math.Sign(b.Y - y);
                ok &= Place(s, x, y);
            }
            while (x != b.X)
            {
                x += Math.Sign(b.X - x);
                ok &= Place(s, x, y);
            }
            return ok;
        }

        private static bool Place(CampaignState s, int x, int y) =>
            PipeNetworkService.TryGetPiece(new GridCell(x, y), out _, out _) || PipeNetworkService.TryPlace(s, new GridCell(x, y), PipePieceKind.Pipe, 0, 0).Ok;

        // ── F4 太阳能 ─────────────────────────────────────────────────────────────

        private static void CheckSolar()
        {
            CampaignState s = NewWorld(4441, scrap: 300);
            GridCell? at = FgProductionSelfCheck.FindFree(s, Solar, 8f, 18f);
            if (!at.HasValue)
            {
                Fail("F4 核心附近放不下太阳能阵列");
                return;
            }
            BuildingRecord sol = Built(s, Solar, "solar", at.Value);
            Seconds(1f);
            float cap0 = s.PowerCapacity;
            string t1 = null;
            bool day = Mathf.Approximately(HomeValleyPowerGrid.AvailableSupplyOf(s, sol.BuildingId), 60f) && Mathf.Approximately(s.PowerCapacity, HomeValleyLayout.BaseCoreSupply + 60f)
                       && HomeValleyPowerGrid.TryDescribeBuilding(s, sol, out t1) && t1.Contains("光照系数 100%") && t1.Contains("白天") && t1.Contains("供电 60");
            TooltipContent tip = HomeValueBreakdown.Power(s);
            string sources = string.Join("，", tip.Sources.Select(x => x.Label + " " + x.Value));
            bool tipOk = tip.Sources.Any(x => x.Label.Contains(GameText.Get("building.solar_array.name")) && x.Value == "+60");
            Expect(day && tipOk, $"F4 昼夜系统接入前（接口默认白天）：太阳能阵列供电 60，HUD 发电 {cap0}；悬停“{t1?.Replace("\n", " · ")}”；HUD 电力来源“{sources}”列出太阳能 +60（{day}/{tipOk}）");
            // 修好 Demo 发电机、加三座精炼塔（每座 15）：白天 20 + 80 + 60 够用，夜里 100 不够——缺电的根源只能是太阳能。
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
            {
                b.ConstructionState = BuildingConstructionState.Operational;
            }
            for (int i = 0; i < 3; i++)
            {
                GridCell? tAt = FgProductionSelfCheck.FindFree(s, "refinery_tower", 8f, 24f);
                if (tAt.HasValue)
                {
                    Built(s, "refinery_tower", "f4tower" + i, tAt.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            bool dayAll = Consumers(s).All(b => b.PowerState == BuildingPowerState.Powered);
            // 接口：夜晚（光照 0）→ 下一个游戏秒太阳能为 0；缺电建筑的根因追到太阳能光照不足。
            PowerEnvironment.DaylightProvider = _ => 0f;
            Seconds(1.1f);
            bool night = Mathf.Approximately(HomeValleyPowerGrid.AvailableSupplyOf(s, sol.BuildingId), 0f) && Mathf.Approximately(s.PowerCapacity, HomeValleyLayout.BaseCoreSupply + 80f)
                         && HomeValleyPowerGrid.TryDescribeBuilding(s, sol, out string t2) && t2.Contains("夜晚：不发电");
            // 修复轮 P2（B05）：夜里的太阳能在世界里不再涂“在工作”的绿，头顶挂待机标记（与悬停“夜晚：不发电”一致）。
            bool sessionIsS = CampaignSession.Current == s;
            Color nightColor = HomeValleyController.ColorForBuilding(sol);
            string nightIcon = HomeValleyController.StateIconFor(sol);
            BuildingRecord dark = Consumers(s).LastOrDefault(b => b.PowerState == BuildingPowerState.Brownout);
            DiagChain chain = dark != null ? RootCauseDiagnosis.DiagnoseBuilding(s, dark).Chains.FirstOrDefault(c => c.Category == DiagCategory.Power) : null;
            bool diag = chain != null && chain.Steps.Any(st => st.Code == DiagCode.SupplyDark && st.TargetId == sol.BuildingId);
            // 沙暴（白天）→ 60 × 0.4 = 24。
            PowerEnvironment.DaylightProvider = _ => 1f;
            PowerEnvironment.SandstormProvider = _ => true;
            Seconds(1.1f);
            bool storm = Mathf.Approximately(HomeValleyPowerGrid.AvailableSupplyOf(s, sol.BuildingId), 24f) && HomeValleyPowerGrid.TryDescribeBuilding(s, sol, out string t3) && t3.Contains("光照系数 40%");
            PowerEnvironment.ResetForTests();
            Seconds(1.1f);
            bool back = Mathf.Approximately(HomeValleyPowerGrid.AvailableSupplyOf(s, sol.BuildingId), 60f);
            bool look = sessionIsS && nightIcon == ContentIcons.StateIdle && HomeValleyController.StateIconFor(sol) == null
                        && nightColor != HomeValleyController.ColorForBuilding(sol);
            Expect(look, $"F4 外观（B05）：夜里太阳能涂灰、头顶待机标记（{nightIcon ?? "无"}），白天恢复“在工作”色、无标记（会话一致 {sessionIsS}）");
            Expect(dayAll && night && diag && storm && back,
                $"F4 昼夜 / 天气接口（FG7-ENV-01 / 03 接入点 PowerEnvironment）：白天全部有电（{dayAll}）；夜晚 → 太阳能 0（{night}），悬停写“夜晚：不发电”，缺电建筑的根因链追到“{chain?.Steps.LastOrDefault()?.Text}”（{diag}）；" +
                $"沙暴 → 24（−60%，{storm}）；接口清掉回到 60（{back}）");
        }

        // ── F5 储能站 ─────────────────────────────────────────────────────────────

        /// <summary>小容量储能（测试夹具：把储能站的容量注入成 1 电·分钟 = 60 电·秒，功率 30），满 / 空在几秒内发生。真表的 2,000 电·分钟由 F1 断言。</summary>
        private static void SmallStorage()
        {
            var defs = ConfigSystem.Instance.Tables.TbPowerNode.DataList.Select(r => new PowerNodeDef(r.TypeId, r.CoverRadius, r.StorageCapacity, r.StorageRate)).ToList();
            defs.RemoveAll(d => d.TypeId == Storage);
            defs.Add(new PowerNodeDef(Storage, 0f, 1f, 30f));
            HomeValleyPowerGrid.OverrideNodesForTests(defs);
        }

        private static void CheckStorageStation()
        {
            CampaignState s = NewWorld(4451, scrap: 300);
            SmallStorage();
            GridCell? at = FgProductionSelfCheck.FindFree(s, Storage, 8f, 18f);
            GridCell? sAt = FgProductionSelfCheck.FindFree(s, Solar, 8f, 18f);
            if (!at.HasValue || !sAt.HasValue)
            {
                Fail("F5 核心附近放不下储能站 / 太阳能");
                return;
            }
            BuildingRecord st = Built(s, Storage, "store", at.Value);
            BuildingRecord sol = Built(s, Solar, "store_solar", FgProductionSelfCheck.FindFree(s, Solar, 8f, 18f).Value);
            // 白天：核心 20 + 太阳能 60，用电只留核心（10）与仓库（5）——盈余充电。
            List<BuildingRecord> cons = Consumers(s);
            var off = cons.Where(b => b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore && b.BuildingTypeId != HomeValleyLayout.BuildingTypeWarehouse).ToList();
            foreach (BuildingRecord b in off)
            {
                b.ConstructionState = BuildingConstructionState.Disabled;
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            string h1 = null;
            double fc = 0;
            bool charging = HomeValleyPowerGrid.StorageStateText(s, st.BuildingId).StartsWith("充电", StringComparison.Ordinal)
                            && HomeValleyPowerGrid.TryDescribeBuilding(s, st, out h1) && h1.Contains("储能") && h1.Contains("充电开");
            bool full = StepUntil(() => HomeValleyPowerGrid.TryGetStorage(s, st.BuildingId, out double m, out double c) && m >= c - 1e-6, 10);
            Seconds(1f);
            bool fullText = HomeValleyPowerGrid.StorageStateText(s, st.BuildingId) == "已充满" && HomeValleyPowerGrid.TryGetStorage(s, st.BuildingId, out double fm, out fc) && Math.Abs(fm - fc) < 1e-6;
            Expect(charging && full && fullText, $"F5 储能站：盈余充电（“{h1?.Replace("\n", " · ")}”）；充到 {fc} 电·分钟后“已充满”，不再充（卡片负向“储能站满”）（{charging}/{full}/{fullText}）");
            // 夜晚：只剩核心 20，用电恢复全部（需要 > 20）→ 储能放电补缺口；放电只给优先级 1～2 时优先级 3 的停机；放空后发“储能放空”。
            foreach (BuildingRecord b in off)
            {
                b.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            PowerEnvironment.DaylightProvider = _ => 0f;
            Seconds(1.1f);
            float need = cons.Sum(b => HomeValleyLayout.PowerProfile[b.BuildingTypeId].PowerDemand);
            bool discharging = HomeValleyPowerGrid.StorageStateText(s, st.BuildingId).StartsWith("放电", StringComparison.Ordinal);
            HomeValleyPowerGrid.GridResult r1 = HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, reserve: 2);
            bool reserved = r1.Success && cons.Where(b => b.PowerPriority >= 3).All(b => b.PowerState != BuildingPowerState.Powered)
                            && HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).Reserve == 2 && s.Power.StorageSettingIds.Contains(st.BuildingId);
            int empty0 = NotifyCount("power_storage_empty");
            bool emptied = StepUntil(() => HomeValleyPowerGrid.TryGetStorage(s, st.BuildingId, out double m2, out _) && m2 <= 1e-6, 30);
            Seconds(1.1f);
            NotificationEntry ne = LastOf("power_storage_empty");
            // 修复轮 P2（B06）：放空时若电网里还有它够得着的停机建筑，状态写“已放空，不充电：电网有建筑停机……”。
            string emptyText = HomeValleyPowerGrid.StorageStateText(s, st.BuildingId);
            bool emptyOk = emptied && NotifyCount("power_storage_empty") == empty0 + 1 && ne != null && ne.Latest.HasLocation && emptyText.StartsWith("已放空", StringComparison.Ordinal)
                           && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnergyFirstStorageEmpty) && cons.Any(b => b.PowerState == BuildingPowerState.Brownout);
            Seconds(3f);
            bool once = NotifyCount("power_storage_empty") == empty0 + 1;
            Expect(discharging && reserved && emptyOk && once,
                $"F5 夜晚只剩核心 20、需要 {need}：储能放电补缺口；放电改成“只给优先级 1～2”→ 优先级 3 及以下停机（卡片负向“停电期间按优先级断电，储能站按设置放电”）；" +
                $"放空后“{emptyText}”、发警告“{ne?.Text}”（可定位，只报一次）、钩子（{discharging}/{reserved}/{emptyOk}/{once}）");
            // 关掉充电：白天盈余也不充；打开后恢复充电。
            PowerEnvironment.DaylightProvider = _ => 1f;
            foreach (BuildingRecord b in off)
            {
                b.ConstructionState = BuildingConstructionState.Disabled;
            }
            HomeValleyPowerGrid.Recompute(s);
            HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, noCharge: true);
            Seconds(3f);
            HomeValleyPowerGrid.TryGetStorage(s, st.BuildingId, out double m3, out _);
            bool noCharge = m3 <= 1e-6 && HomeValleyPowerGrid.StorageStateText(s, st.BuildingId) == "已放空";
            HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, noCharge: false);
            Seconds(2f);
            HomeValleyPowerGrid.TryGetStorage(s, st.BuildingId, out double m4, out _);
            HomeValleyPowerGrid.GridResult bad1 = HomeValleyPowerGrid.TrySetStorageSettings(s, sol.BuildingId, noCharge: true);
            HomeValleyPowerGrid.GridResult bad2 = HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, reserve: 5);
            HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, reserve: 0);
            bool defaultsPruned = !s.Power.StorageSettingIds.Contains(st.BuildingId);
            foreach (BuildingRecord b in off)
            {
                b.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            Expect(noCharge && m4 > 0 && !bad1.Success && bad1.FailureReason.StartsWith("not-storage", StringComparison.Ordinal) && !bad2.Success && defaultsPruned,
                $"F5 关掉充电：白天盈余也不充（3 秒后仍 {m3:0.##}）；打开后恢复充电（{m4:0.##}）；对太阳能改储能设置被拒绝、放电对象 5 超范围被拒绝；设回默认后不再占存档记录（{noCharge}/{bad1.Success}/{bad2.Success}/{defaultsPruned}）");
        }

        // ── F6 电网面板 ───────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(4461, scrap: 300);
            FuelRig rig = LayFuel(s, "pfuel");
            GridCell? at = FgProductionSelfCheck.FindFree(s, Storage, 8f, 18f);
            if (rig == null || !at.HasValue)
            {
                Fail("F6 放不下燃油发电机 / 储能站");
                return;
            }
            Pipes(s, rig);
            Feed(rig, 500);
            BuildingRecord st = Built(s, Storage, "pstore", at.Value);
            BuildingRecord sol = Built(s, Solar, "psolar", FgProductionSelfCheck.FindFree(s, Solar, 8f, 18f).Value);
            StepUntil(() => P(s, rig.Generator).Rec.Fueled, 30);
            WorldSimulation.StepMany(GameClock.StepHz * 25); // 曲线记两点以上
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "PowerPanel.uxml", out GameObject go);
            PowerPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                PowerPanelUIToolkit panel = go.AddComponent<PowerPanelUIToolkit>();
                panel.BindView(root);
                PowerPanelUIToolkit.OpenFor(st.BuildingId);
                PowerKernel k = HomeValleyPowerGrid.Kernel;
                k.TryGetCurve(panel.SelectedSerial, out PowerCurve c);
                string legend = panel.CurveSourcesText;
                bool legendOk = legend.Contains("◆ 归还核心") && legend.Contains("▲ 燃油发电机") && legend.Contains("■ 太阳能") && c != null && c.Count >= 2
                                && c.GetClass(c.Count - 1, HomeValleyPowerGrid.SourceOf(Fuel).ClassIndex) > 0f && c.GetClass(c.Count - 1, HomeValleyPowerGrid.SourceOf(Solar).ClassIndex) > 0f;
                bool detail = panel.DetailText.Contains("发电构成") && panel.DetailText.Contains("燃油发电机 300") && panel.DetailText.Contains("太阳能 60")
                              && panel.DetailText.Contains("燃油发电负载");
                Expect(PowerPanelUIToolkit.IsOpen && legendOk && detail,
                    $"F6 点储能站打开电网面板（选中它所在的电网）：曲线的分类图例“{legend}”（颜色之外用形状标记区分，B15），曲线每点存各类发电；读数“{panel.DetailText.Replace("\n", " · ")}”");
                bool storageBox = panel.StorageBoxVisible && panel.SelectedStorageId == st.BuildingId && panel.StorageField.choices.Count == 1 && panel.StoragesText.Contains("电·分钟")
                                  && panel.StorageTitleText.Contains("储能站（1 座）") && panel.DischargeField.choices.Count == 5 && panel.ChargeButton.text == "关掉充电";
                panel.DischargeField.value = panel.DischargeField.choices[2]; // 只给优先级 1～2
                bool d2 = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).Reserve == 2 && !HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).NoDischarge
                          && panel.MessageText.Contains("只给优先级 1～2");
                panel.DischargeField.value = panel.DischargeField.choices[4]; // 不放电
                bool dOff = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).NoDischarge && panel.MessageText.Contains("不放电");
                bool cOff = panel.ToggleCharge() && HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).NoCharge && panel.ChargeButton.text == "打开充电" && panel.StoragesText.Contains("充电关");
                panel.DischargeField.value = panel.DischargeField.choices[0];
                panel.ToggleCharge();
                bool reset = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).IsDefault;
                bool locate = panel.LocateStorage();
                Expect(storageBox && d2 && dOff && cOff && reset && locate,
                    $"F6 储能站一节（卡片“储能站的充放电设置”）：列出“{panel.StoragesText}”；放电对象下拉框 5 项（所有建筑 / 只给优先级 1～3 / 1～2 / 1 / 不放电），选中即生效；“关掉充电 / 打开充电”；“定位储能站”");
                // 点太阳能阵列也打开面板并选中它的电网；没有储能的电网写“没有储能站”。
                PowerPanelUIToolkit.Close();
                bool viaClick = HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.OpenPortPanel(s, sol.BuildingId)
                                && ProductionPanelUIToolkit.LastRequestedId == sol.BuildingId && OpenGridFromBuildingPanel(sol.BuildingId) && PowerPanelUIToolkit.IsOpen
                                && panel.SelectedSerial == k.Subnet(HomeValleyPowerGrid.TryGetBuildingPower(s, sol.BuildingId, out BuildingPowerInfo si) ? si.Subnet : 0).Serial;
                Expect(viaClick, "F6 建造模式里点太阳能阵列 / 储能站：打开它的通用面板，面板上的“电网…”打开电网面板并选中它所在的电网（FG4-ECO-05 起经通用面板）");
                UiEscapeStack.CloseTop();
            }
            finally
            {
                PowerPanelUIToolkit.Close();
                PowerPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            // 布局探针：有储能站与多类发电的面板，中英 × 三种缩放。
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
                            var probeGo = new GameObject("__probe_pw_energy") { hideFlags = HideFlags.HideAndDontSave };
                            PowerPanelUIToolkit p = probeGo.AddComponent<PowerPanelUIToolkit>();
                            p.BindView(pr.panel.visualTree);
                            PowerPanelUIToolkit.OpenFor(st.BuildingId);
                            p.Refresh(force: true);
                            PowerPanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("PowerPanelRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"F6 布局探针 PowerPanel.uxml#PowerPanelWindow（含储能站一节与分类图例）[{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
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

        // ── F7 状态矩阵（FGT-ECO-002 能源建筑部分）───────────────────────────────────

        private static void CheckStateMatrix()
        {
            CampaignState s = NewWorld(4471, scrap: 300);
            FuelRig rig = LayFuel(s, "mfuel");
            if (rig == null)
            {
                Fail("F7 放不下燃油发电机");
                return;
            }
            ProductionService.Producer p = P(s, rig.Generator);
            var states = new List<string>();
            void Note(string what, bool ok)
            {
                states.Add((ok ? "✓" : "✗") + what);
            }
            // 燃油发电机：建造中 / 没油 / 发电 / 待机 / 未接入 / 受损 / 关停。
            BuildingConstructionState keep = rig.Generator.ConstructionState;
            rig.Generator.ConstructionState = BuildingConstructionState.Building;
            ProductionService.Step(s, 3, GameClock.StepHz);
            Note("建造中", p.State == ProdState.Building && FgProductionSelfCheck.Reason(s, p).Contains("还没建成"));
            rig.Generator.ConstructionState = keep;
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            ProductionService.Step(s, 3, GameClock.StepHz);
            Note("没油", p.State == ProdState.MissingFluid && p.Reason == ProdReason.NoFuel);
            Pipes(s, rig);
            Feed(rig, 300);
            StepUntil(() => p.Rec.Fueled, 30);
            Seconds(1f);
            Note("发电", p.State == ProdState.Working && p.Reason == ProdReason.WorkingGenerator);
            rig.Generator.ConstructionState = BuildingConstructionState.Damaged;
            HomeValleyPowerGrid.Recompute(s);
            ProductionService.Step(s, 3, GameClock.StepHz);
            Note("受损", p.State == ProdState.Damaged && FgProductionSelfCheck.Reason(s, p).Contains("受损") && HomeValleyPowerGrid.AvailableSupplyOf(s, rig.Generator.BuildingId) == 0f);
            rig.Generator.ConstructionState = BuildingConstructionState.Disabled;
            HomeValleyPowerGrid.Recompute(s);
            ProductionService.Step(s, 3, GameClock.StepHz);
            Note("关停", p.State == ProdState.Disabled && FgProductionSelfCheck.Reason(s, p).Contains("被关停") && HomeValleyPowerGrid.AvailableSupplyOf(s, rig.Generator.BuildingId) == 0f);
            rig.Generator.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            Seconds(2f);
            Note("恢复发电", p.State == ProdState.Working);
            // 未接入：把核心配电挪开（测试捷径：注入核心半径 1 格），发电机送不出电 → 不烧油、写原因。
            var defs = ConfigSystem.Instance.Tables.TbPowerNode.DataList.Select(r => new PowerNodeDef(r.TypeId, r.TypeId == "core" ? 1f : r.CoverRadius, r.StorageCapacity, r.StorageRate)).ToList();
            HomeValleyPowerGrid.OverrideNodesForTests(defs);
            HomeValleyPowerGrid.Recompute(s);
            long b0 = p.Rec.FuelBurnedMl;
            Seconds(3f);
            Note("未接入", p.State == ProdState.OutputBlocked && p.Reason == ProdReason.GeneratorUnconnected && FgProductionSelfCheck.Reason(s, p).Contains("未接入电网") && p.Rec.FuelBurnedMl == b0
                         && HomeValleyPowerGrid.TryDescribeBuilding(s, rig.Generator, out string hu) && hu.Contains("送不出去"));
            HomeValleyPowerGrid.OverrideNodesForTests(null);
            HomeValleyPowerGrid.Recompute(s);
            // 太阳能：白天 / 夜晚 / 沙暴 / 未接入；储能站：充电 / 放电 / 已满 / 已空 / 未接入（F4 / F5 逐项断言过，这里补“未接入”）。
            GridCell? far = FgProductionSelfCheck.FindFree(s, Storage, 34f, 48f);
            GridCell? farS = FgProductionSelfCheck.FindFree(s, Solar, 34f, 48f);
            if (far.HasValue && farS.HasValue)
            {
                BuildingRecord fs = Built(s, Storage, "farstore", far.Value);
                BuildingRecord fsol = Built(s, Solar, "farsolar", FgProductionSelfCheck.FindFree(s, Solar, 34f, 48f).Value);
                Note("储能站未接入", HomeValleyPowerGrid.StorageStateText(s, fs.BuildingId) == GameText.Get("power.storage.unconnected"));
                Note("太阳能未接入", HomeValleyPowerGrid.TryDescribeBuilding(s, fsol, out string fsx) && fsx.Contains("送不出去"));
            }
            else
            {
                Note("远处空地", false);
            }
            // 储能站施工中 / 受损 / 关停：不充不放，状态写明是哪一种（不误报“未接入电网”）。
            GridCell? nearS = FgProductionSelfCheck.FindFree(s, Storage, 8f, 18f);
            if (nearS.HasValue)
            {
                BuildingRecord ns = Built(s, Storage, "nearstore", nearS.Value);
                var seen = new List<string>();
                foreach (BuildingConstructionState cs in new[] { BuildingConstructionState.Building, BuildingConstructionState.Damaged, BuildingConstructionState.Disabled })
                {
                    ns.ConstructionState = cs;
                    HomeValleyPowerGrid.Recompute(s);
                    seen.Add(HomeValleyPowerGrid.StorageStateText(s, ns.BuildingId));
                }
                ns.ConstructionState = BuildingConstructionState.Operational;
                HomeValleyPowerGrid.Recompute(s);
                Note("储能站建造中 / 受损 / 关停", seen[0].Contains("建造中") && seen[1].Contains("损坏") && seen[2].Contains("已关停"));
            }
            Expect(states.All(x => x.StartsWith("✓", StringComparison.Ordinal)),
                $"F7 FGT-ECO-002（能源建筑部分）：燃油发电机 建造中 / 没油 / 发电 / 受损 / 关停 / 恢复 / 未接入，储能站 建造中 / 受损 / 关停 / 未接入，太阳能 未接入，各自显示正确原因：{string.Join("、", states)}");
        }

        // ── F8 存读档 ─────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            var sb = new StringBuilder();
            sb.Append(k != null ? k.Fingerprint() : "no-kernel").Append(" |cap=").Append(s.PowerCapacity.ToString("R", CultureInfo.InvariantCulture));
            foreach (ProducerRecord r in s.Economy.Producers.Where(r => r.BuildingId.Contains(Fuel)).OrderBy(r => r.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(" |").Append(r.BuildingId).Append(':').Append(r.Fueled).Append('/').Append(r.FuelBurnedMl).Append('/').Append(r.Progress);
            }
            sb.Append(" |set=").Append(string.Join(",", s.Power.StorageSettingIds ?? Array.Empty<string>())).Append(':')
                .Append(string.Join(",", (s.Power.StorageReserve ?? Array.Empty<int>()).Select(x => x.ToString(CultureInfo.InvariantCulture))));
            sb.Append(" |pipe=").Append(PipeNetworkService.IsRunning ? PipeNetworkService.Kernel.Hash() : 0UL);
            sb.Append(" |").Append(string.Join(",", s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId).OrderBy(b => b.BuildingId, StringComparer.Ordinal)
                .Select(b => (int)b.ConstructionState + "" + (int)b.PowerState)));
            return sb.ToString();
        }

        /// <summary>存读档 / 倍速 / 观察共用的场景：燃油发电机（有限的燃油，会烧完）+ 小储能站（放电只给优先级 1～2）+ 太阳能（光照随游戏时间：20 秒白天 / 20 秒夜晚）。</summary>
        private static bool LayScenario(CampaignState s, string tag, out FuelRig rig)
        {
            SmallStorage();
            PowerEnvironment.DaylightProvider = _ => (GameClock.Ticks / Math.Max(1, GameClock.StepHz)) % 40 < 20 ? 1f : 0f;
            rig = LayFuel(s, tag + "fuel");
            GridCell? at = FgProductionSelfCheck.FindFree(s, Storage, 8f, 18f);
            if (rig == null || !at.HasValue)
            {
                return false;
            }
            BuildingRecord st = Built(s, Storage, tag + "store", at.Value);
            GridCell? sAt = FgProductionSelfCheck.FindFree(s, Solar, 8f, 18f);
            if (!sAt.HasValue)
            {
                return false;
            }
            Built(s, Solar, tag + "solar", sAt.Value);
            HomeValleyPowerGrid.TrySetStorageSettings(s, st.BuildingId, reserve: 2);
            Pipes(s, rig);
            Feed(rig, 40);
            return true;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(4481, scrap: 300);
            if (!LayScenario(s, "sv", out FuelRig rig))
            {
                Fail("F8 放不下场景");
                return;
            }
            try
            {
                WorldSimulation.StepMany(GameClock.StepHz * 27 + 5);
                string before = Snapshot(s);
                WorldSimulation.SyncAllForSave();
                SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                string continuous = Snapshot(s);

                string RunFromSave(out string loaded, out CampaignState state)
                {
                    WorldSimulation.UnloadAll();
                    GameClock.ResetSession();
                    HomeValleyPowerGrid.ResetForTests();
                    SmallStorage();
                    PowerEnvironment.DaylightProvider = _ => (GameClock.Ticks / Math.Max(1, GameClock.StepHz)) % 40 < 20 ? 1f : 0f;
                    ProductionService.ResetForTests();
                    RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                    loaded = null;
                    state = rr.State;
                    if (!rr.Success)
                    {
                        return "读档失败：" + rr.Message;
                    }
                    CampaignSession.Set(Slot, rr.State);
                    HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                    WorldView.Observe(home.SiteId);
                    loaded = Snapshot(rr.State);
                    WorldSimulation.StepMany(GameClock.StepHz * 30);
                    return Snapshot(rr.State);
                }

                string first = RunFromSave(out string loaded1, out CampaignState l);
                bool domain = l != null && l.Power.DomainVersion == 3 && l.Power.FormatVersion == PowerKernel.FormatVersion && l.Power.StorageSettingIds.Length == 1
                              && l.Power.StorageReserve[0] == 2 && l.Power.CurveClassSupply.Length > 0
                              && l.Economy.Producers.Any(r => r.BuildingId == rig.Generator.BuildingId && r.FuelBurnedMl > 0);
                string second = RunFromSave(out _, out _);
                Expect(save.Success && loaded1 == before && domain,
                    "F8 真文件存读档：燃油发电机的有油 / 累计烧油 / 零头、储能站设置（放电只给优先级 1～2）、各类别曲线（电网域 v3、内核格式 2）写进存档，读档后逐字段一致" +
                    (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
                Expect(first == continuous && second == first,
                    "F8 读档后接着跑 30 游戏秒（烧油、燃油耗尽、储能放电、昼夜切换、曲线采样），与不存档一直跑逐位一致；同一存档读两次结果一致" +
                    (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            }
            finally
            {
                HomeValleyPowerGrid.OverrideNodesForTests(null);
                PowerEnvironment.ResetForTests();
            }
        }

        // ── F9 / F10 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 300);
            pausedHeld = true;
            try
            {
                if (!LayScenario(s, "tm", out _))
                {
                    return "放不下";
                }
                WorldSimulation.StepMany(GameClock.StepHz * 7);
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
                long target = GameClock.Ticks + GameClock.StepHz * 50;
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
                PowerEnvironment.ResetForTests();
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
                string snap = RunScenario(4491, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                shown.Add($"{speed}x");
            }
            Expect(paused && same && reference != "放不下",
                $"F9 暂停中（120 帧）不烧油、储能不动、太阳能不变；{string.Join(" / ", shown)} 跑同样的 50 游戏秒（烧油到耗尽、储能放电、昼夜切换），电网指纹、烧油量与每座建筑的供电结果逐位一致");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(4492, true, 1f, false, out _);
            string unseen = RunScenario(4492, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "F10 同一组能源（烧油、燃油耗尽、储能放电、昼夜切换）在观察与不观察家园时跑 50 游戏秒逐字段一致（FGR-BASE-021）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── F11 固件：耗电折算进热量、界面不出现耗电 ─────────────────────────────────

        private static void CheckFirmwarePower()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_firmware as f;print('\\n'.join('RAW\\t%s\\t%d\\t%r' % (r[0], r[9], r[10]) for r in f._F))\"");
            var raw = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("RAW\t", StringComparison.Ordinal)).Select(l => l.Split('\t'))
                .ToDictionary(f => f[1], f => (power: int.Parse(f[2], CultureInfo.InvariantCulture), heat: float.Parse(f[3], CultureInfo.InvariantCulture)));
            var rows = FirmwareKinds.Rows;
            var bad = new List<string>();
            int converted = 0;
            foreach (GameConfig.fg.FirmwareKind r in rows)
            {
                if (!raw.TryGetValue(r.Id, out var src))
                {
                    bad.Add(r.Id + "（源数据里没有）");
                    continue;
                }
                float expect = src.heat > 0f || src.power <= 0 ? src.heat : src.power * 1f;
                if (src.heat <= 0f && src.power > 0)
                {
                    converted++;
                }
                if (Math.Abs(r.Heat - expect) > 1e-4f || r.Power != src.power)
                {
                    bad.Add($"{r.Id}（表 热 {r.Heat} 电 {r.Power}，应 热 {expect} 电 {src.power}）");
                }
            }
            bool noFreeLunch = rows.All(r => r.Heat > 0f || r.Power == 0);
            Expect(code == 0 && raw.Count == 44 && rows.Count == 44 && bad.Count == 0 && converted == 28 && noFreeLunch,
                $"F11 逐条核对 44 条固件（DEBT-FG2FW01-02 范围变更）：原本只有耗电、热量为 0 的 {converted} 条按 1 电 = 1 热折算进每发积热，其余热量不变；能耗列原样保留在数据层；" +
                "现在没有“既不积热又只靠耗电”的固件" + (bad.Count == 0 ? string.Empty : "；不符：" + string.Join("、", bad.Take(6))) + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));
            // 预览 / 摘要 / 固件库：中英都不出现“耗电”。
            var leaks = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                FirmwareCatalog.Invalidate();
                string word = lang == GameLanguage.En ? "Power per shot" : "耗电";
                BlueprintCircuitBoard gun = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
                gun.TrySetUplink(2);
                UplinkDualPreview dual = UplinkCompiler.CompileDual(gun, new[] { "fw_vortex" });
                if (dual.AiLines.Concat(dual.UplinkedLines).Any(x => x.Text.Contains(word)) || dual.Diff.Any(d => d.Text.Contains(word) || d.Kind == UplinkDiffKind.PowerChanged))
                {
                    leaks.Add(lang + " 双态预览");
                }
                if (!dual.Diff.Any(d => d.Kind == UplinkDiffKind.HeatChanged))
                {
                    leaks.Add(lang + " 双态预览没有热量差异");
                }
                foreach (MechanicalContentDef d in FirmwareCatalog.All.Values)
                {
                    if (d.ValuesSummary.Contains(word))
                    {
                        leaks.Add(lang + " 摘要 " + d.Id);
                        break;
                    }
                }
                string fwlib = FirmwareLibrary.BuildDetail(CampaignState.CreateNew("fgenergy-fw", "Standard", 1), "fw_vortex");
                if (fwlib != null && fwlib.Contains(word))
                {
                    leaks.Add(lang + " 固件库");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            FirmwareCatalog.Invalidate();
            Expect(leaks.Count == 0, "F11 界面不再出现“每发耗电”（中英）：双态预览两栏与差异只比热量、电路面板的固件数值摘要、固件库详情" + (leaks.Count == 0 ? string.Empty : "；漏了：" + string.Join("、", leaks)));
        }

        /// <summary>
        /// 开火不扣电池：① 真实家园里一台装寻的（原耗电 1、现每发积热 1）的连射器机器靠近训练靶，AI 按交战间隔自动出手 20 游戏秒，电池从不减少；
        /// ② 这台机器的装配经正式翻译（CombatSite.MachineWeaponFrom）成内核武器，在真实战斗内核里开一发：积热 = 固件热量（逐发代价只有热量）。
        /// </summary>
        private static void CheckFiringNoBattery()
        {
            CampaignState s = NewWorld(4501, scrap: 300);
            const string bp = "bp_selfcheck_energy_fire";
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_homing" });
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != bp)
                .Append(new BlueprintRecord { BlueprintId = bp, DisplayName = bp, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            MachineOpResult spawn = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, HomeValleyLayout.RegionId, dummy + new Vector2(6f, 0f), 400f, 400f, "Player", 1);
            if (!spawn.Success || !MachineLoadoutRegistry.Register(s, spawn.LogicId, bp, 1).Success)
            {
                Fail("F11 测试准备：登记机器 / 装配失败");
                return;
            }
            MachineRegistry.TryGetRecord(spawn.LogicId, out MachineRecord rec);
            float cap = HomeValleyLayout.BatteryCapacity.TryGetValue(HomeValleyLayout.Erc003ChassisId, out float c) ? c : 100f;
            rec.Battery = cap * 0.5f;
            int before = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == spawn.LogicId);
            float last = rec.Battery;
            bool neverDown = true;
            for (int i = 0; i < 40; i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz / 2);
                neverDown &= rec.Battery >= last - 1e-4f;
                last = rec.Battery;
            }
            int attacks = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == spawn.LogicId) - before;
            MachineCombatResolution res = MachineLoadoutRegistry.ResolveForAi(s, spawn.LogicId, s.RandomSeed);
            float homingHeat = FirmwareKinds.HeatOf("fw_homing");
            float shot = -1f;
            float perShot = -1f;
            if (res.Success)
            {
                CombatWeapon weapon = CombatSite.MachineWeaponFrom(res.Preview);
                perShot = weapon.HeatPerShot;
                using var k = new CombatKernel(CombatSite.ConfigFromTuning(), 16);
                int wi = k.AddWeapon(weapon);
                int m = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.HoldFire,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled, Position = new Unity.Mathematics.double2(0, 0),
                    Home = new Unity.Mathematics.double2(0, 0), Radius = 0.8f, Health = 400f, MaxHealth = 400f, Weapon = wi, BehaviorProfile = -1, Priority = 1,
                });
                int t = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new Unity.Mathematics.double2(0, 4), Home = new Unity.Mathematics.double2(0, 4),
                    Radius = 0.8f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                k.SetWeaponState(m, 0f, false, 1.0, 0);
                CombatFireResult r = k.FireAt(m, t, 2.0);
                shot = r == CombatFireResult.Ok && k.TryGetUnit(m, out CombatUnitView v) ? v.Heat : -1f;
            }
            Expect(board.FirmwareSlots.Contains("fw_homing") && attacks >= 2 && neverDown && homingHeat > 0f && FirmwareKinds.PowerOf("fw_homing") > 0
                   && Mathf.Approximately(perShot, homingHeat) && Mathf.Approximately(shot, homingHeat),
                $"F11 开火不扣电池（DEBT-FG2FW01-02 范围变更）：装寻的（能耗 {FirmwareKinds.PowerOf("fw_homing")} 只在数据层、每发积热 {homingHeat}）的连射器在家园对训练靶自动出手 {attacks} 次，" +
                $"20 游戏秒里电池从不减少（{cap * 0.5f:0.#} → {rec.Battery:0.#}）；同一装配翻译成的内核武器每发积热 {perShot}，真实内核开一发积热 {shot}（逐发代价只有热量）");
        }

        // ── F12 建造菜单真实放置 ─────────────────────────────────────────────────────

        private static void CheckBuildMenu()
        {
            CampaignState s = NewWorld(4511, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? at = FgProductionSelfCheck.FindFree(s, Storage, 8f, 18f);
            if (mode == null || !at.HasValue)
            {
                Fail("F12 没有建造模式 / 放不下储能站");
                return;
            }
            try
            {
                mode.Open();
                mode.Select(Storage);
                mode.SetHover(s, at.Value);
                mode.RefreshPreview(s);
                GridPlacementResult preview = mode.Preview;
                string note = preview?.Notes.FirstOrDefault(n => n.Contains("接入")) ?? string.Empty;
                bool notes = preview != null && preview.Ok && note.Contains("电网 1");
                mode.PointerDown(s, at.Value);
                mode.PointerUp(s, at.Value);
                BuildingRecord ghost = HomeGridService.BuildingAt(s, at.Value);
                bool placed = ghost != null && ghost.BuildingTypeId == Storage && ghost.ConstructionState != BuildingConstructionState.Operational;
                mode.ClearSelection();
                bool built = placed && StepUntil(() => HomeGridService.BuildingAt(s, at.Value)?.ConstructionState == BuildingConstructionState.Operational, 600);
                BuildingRecord done = HomeGridService.BuildingAt(s, at.Value);
                bool joined = built && HomeValleyPowerGrid.TryGetBuildingPower(s, done.BuildingId, out BuildingPowerInfo info) && info.IsStorage && info.Subnet >= 0
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnergyFirstBuilt) && MechanicCodex.IsUnlocked("codex.economy.energy");
                Expect(notes && placed && built && joined,
                    $"F12 建造菜单“能源”页签选储能站：放置预览写“{note}”，单击放下虚影 → 机器取料施工建成 → 接入电网成为储能；钩子“第一次建成能源建筑”与图鉴“能源”解锁（{notes}/{placed}/{built}/{joined}）");
            }
            finally
            {
                mode.Close();
            }
        }

        // ── U1 地下管线（内核）────────────────────────────────────────────────────

        private static void CheckUndergroundKernel()
        {
            var k = new PipeKernel(PipeConfig.Default);
            const int N = 0, E = 1, W = 3;
            const PipePieceKind U = PipePieceKind.Underground;
            // 水：泵 (0,0) → 管线 (1,0) → 地下口 A (2,0) 朝东 …… 地下口 B (8,0) 朝西 → 管线 (9,0)。中间 (3..7, 0) 是地下段。
            bool laid = k.Place(0, 0, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(1, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                        && k.Place(2, 0, U, 0, E, 0) == PipeResult.Ok && k.Place(8, 0, U, 0, W, 0) == PipeResult.Ok && k.Place(9, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok;
            bool joined = laid && k.NetworkAt(0, 0) >= 0 && k.NetworkAt(0, 0) == k.NetworkAt(9, 0) && k.TryGetCellInfo(2, 0, out PipeCellInfo a) && a.UndergroundLinked
                          && a.PartnerX == 8 && a.PartnerY == 0 && a.Dir == E;
            // 原油从地下段上方横穿：(5,1) 泵 → (5,0) → (5,-1)，与水网络互不相连（同一格只隔着地下段）。
            bool cross = k.Place(5, 1, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(5, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                         && k.Place(5, -1, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok;
            bool apart = cross && k.NetworkAt(5, 0) != k.NetworkAt(0, 0) && k.TryGetNetworkInfo(k.NetworkAt(5, 0), out PipeNetInfo crudeNet) && crudeNet.Fluid == 2
                         && k.TryGetNetworkInfo(k.NetworkAt(9, 0), out PipeNetInfo waterNet) && waterNet.Fluid == 1;
            Expect(joined && apart,
                "U1 地下管线：两口朝向相对、中间隔 5 格（T1 上限 8）→ 配对成同一个网络（泵的水送得到 (9, 0)）；原油管线从地下段上方横穿，两种流体互不相连（同一列只隔着地下段）");
            // 流体经地下段送达：(9,0) 挂一个消费者，走 10 秒。
            int cons = k.AddConsumer(9, 0, 1, 300, 1);
            for (int i = 0; i < PipeConfig.Default.StepHz * 10; i++)
            {
                k.Step();
            }
            bool flowed = k.TryGetConsumer(cons, out PipeConsumerInfo ci) && ci.TotalDeliveredMl > 40000;
            Expect(flowed, $"U1 水经地下段送到另一头的消费者：10 秒送达 {ci.TotalDeliveredMl / 1000} 升（泵 300 升/分钟）");
            // 超跨度：C (20,0) 朝东、D (30,0) 朝西，中间 9 格 > 8 → 不配对；渲染给没配对的口打花纹（flags 第 3 位），编码 = 种类 4 + 8 × 方向。
            k.Place(20, 0, U, 0, E, 0);
            k.Place(30, 0, U, 0, W, 0);
            bool far = k.TryGetCellInfo(20, 0, out PipeCellInfo c) && !c.UndergroundLinked && k.NetworkAt(20, 0) != k.NetworkAt(30, 0);
            PipeInstance[] inst = k.PrepareRender(6, out int count, out _);
            PipeInstance ri = inst.Take(count).First(x => (int)x.Ax == 20 && (int)x.Ay == 0);
            PipeInstance rl = inst.Take(count).First(x => (int)x.Ax == 2 && (int)x.Ay == 0);
            bool render = (int)ri.Az == 4 + 8 * E && ((int)ri.Bw & 8) != 0 && ((int)rl.Bw & 8) == 0 && (int)rl.Az == 4 + 8 * E;
            // T2：同样隔 9 格，T2（上限 12）配得上。
            k.Place(20, 5, U, 1, E, 0);
            k.Place(30, 5, U, 1, W, 0);
            bool t2 = k.TryGetCellInfo(20, 5, out PipeCellInfo c2) && c2.UndergroundLinked;
            // 被同方向的口挡住：E (40,0)→东、F (43,0)→东、G (46,0)→西：E 往东第一件是同向的 F，不配对；F 与 G 配对。
            k.Place(40, 0, U, 0, E, 0);
            k.Place(43, 0, U, 0, E, 0);
            k.Place(46, 0, U, 0, W, 0);
            bool blocked = k.TryGetCellInfo(40, 0, out PipeCellInfo ce) && !ce.UndergroundLinked && k.TryGetCellInfo(43, 0, out PipeCellInfo cf) && cf.UndergroundLinked && cf.PartnerX == 46;
            Expect(far && render && t2 && blocked,
                "U1 跨度与配对规则：T1 隔 9 格（上限 8）不配对、渲染给它打红色斜纹，T2（上限 12）配得上；往前第一件是同方向的口时被挡住、不配对（只和朝回来的第一件配对）");
            // 接错流体：原油那头的口 H (62,0) 朝东，水那头的口 I (68,0) 朝西已在——放 H 会把原油与水接起来 → 拒绝并写出两种流体。
            k.Place(60, 0, PipePieceKind.Pump, 0, 0, 2);
            k.Place(61, 0, PipePieceKind.Pipe, 0, 0, 0);
            k.Place(70, 0, PipePieceKind.Pump, 0, 0, 1);
            k.Place(69, 0, PipePieceKind.Pipe, 0, 0, 0);
            k.Place(68, 0, U, 0, W, 0);
            PipeResult conflict = k.CheckPlace(62, 0, U, 0, E, 0, out int fa, out int fb);
            var tmp = new List<int>();
            k.AddAdjacentFluids(62, 0, U, E, tmp);
            Expect(conflict == PipeResult.FluidConflict && ((fa == 2 && fb == 1) || (fa == 1 && fb == 2)) && tmp.Count == 2 && k.Place(62, 0, U, 0, E, 0) == PipeResult.FluidConflict,
                $"U1 地下段会把两种流体接起来：放置被拒绝（{conflict}，流体 {fa} / {fb}）；规划预览的流体检查也查到两种");
            // 中间插一口：K (100,0)→东 与 L (106,0)→西 已配对；在 (103,0) 放朝西的 M → K 改与 M 配对，L 没配对。
            k.Place(100, 0, U, 0, E, 0);
            k.Place(106, 0, U, 0, W, 0);
            bool before = k.TryGetCellInfo(100, 0, out PipeCellInfo k0) && k0.PartnerX == 106;
            k.Place(103, 0, U, 0, W, 0);
            bool repaired = before && k.TryGetCellInfo(100, 0, out PipeCellInfo k1) && k1.PartnerX == 103 && k.TryGetCellInfo(106, 0, out PipeCellInfo l1) && !l1.UndergroundLinked;
            // 存档往返：配对与网络一致。
            PipeSnapshot snap = k.Serialize();
            var k2 = new PipeKernel(PipeConfig.Default);
            bool loaded = k2.Deserialize(snap, out string err) && k2.DroppedOnLoad == 0 && k2.TryGetCellInfo(2, 0, out PipeCellInfo la) && la.UndergroundLinked && la.PartnerX == 8
                          && k2.NetworkAt(0, 0) == k2.NetworkAt(9, 0) && k2.Hash() == k.Hash();
            // 拆一口：A 没了配对，网络断成两段。
            k.Remove(8, 0, out _);
            bool split = k.TryGetCellInfo(2, 0, out PipeCellInfo a2) && !a2.UndergroundLinked && k.NetworkAt(1, 0) != k.NetworkAt(9, 0);
            Expect(repaired && loaded && split,
                $"U1 中间插一口改配对（K 改连 M，L 落单）；存档往返后配对与网络、内核指纹一致（{err ?? "无错误"}）；拆掉一口后另一口落单、网络断开");
            // 修复轮 P1：拆掉中间那一口会让被它隔开的两口重新配对。同流体（这里都没流体）照常拆：拆 M 后 K 改回与 L 配对。
            bool relinkOk = k.CheckRemove(103, 0, out _, out _) == PipeResult.Ok && k.Remove(103, 0, out _) == PipeResult.Ok
                            && k.TryGetCellInfo(100, 0, out PipeCellInfo k2c) && k2c.UndergroundLinked && k2c.PartnerX == 106;
            // 两种流体：水 泵 (200,0) → (201,0) → A (202,0) 朝东，与 M (205,0) 朝西配对；B (208,0) 朝西、地面一侧接原油泵 (210,0)。
            // B 往西第一件是同向的 M（被挡住、不配对），所以能放；拆 M 会让 A 与 B 配对、把水和原油接在一起 → 拒绝，写出两种流体，内核从没“改名”流体。
            int resolvedBefore = k.FluidConflictsResolved;
            bool laid2 = k.Place(200, 0, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(201, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                         && k.Place(202, 0, U, 0, E, 0) == PipeResult.Ok && k.Place(205, 0, U, 0, W, 0) == PipeResult.Ok
                         && k.Place(210, 0, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(209, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                         && k.Place(208, 0, U, 0, W, 0) == PipeResult.Ok;
            PipeResult rmConflict = k.CheckRemove(205, 0, out int ra, out int rb);
            bool refused = laid2 && rmConflict == PipeResult.FluidConflict && ((ra == 1 && rb == 2) || (ra == 2 && rb == 1))
                           && k.Remove(205, 0, out _) == PipeResult.FluidConflict && k.HasCell(205, 0);
            k.Step();
            bool noRename = k.FluidConflictsResolved == resolvedBefore && k.TryGetNetworkInfo(k.NetworkAt(209, 0), out PipeNetInfo oilNet) && oilNet.Fluid == 2
                            && k.TryGetNetworkInfo(k.NetworkAt(201, 0), out PipeNetInfo waterNet2) && waterNet2.Fluid == 1;
            // 先拆 B（原油那一头）就没有冲突：再拆 M 照常成功，A 落单。
            bool orderOk = k.Remove(208, 0, out _) == PipeResult.Ok && k.Remove(205, 0, out _) == PipeResult.Ok
                           && k.TryGetCellInfo(202, 0, out PipeCellInfo a3) && !a3.UndergroundLinked;
            Expect(relinkOk && refused && noRename && orderOk,
                $"U1 拆掉中间口：同流体照常拆、两口改配对；会把水与原油接起来时拒绝（{rmConflict}，流体 {ra} / {rb}），流体从没被改名（解决冲突次数 {k.FluidConflictsResolved - resolvedBefore}）；先拆另一头后再拆照常成功");
            // 修复轮 P2：配对要互为第一件——T1 口 X (300,0) 朝西（水），T2 新口 Y (290,0) 朝东（原油）相距 10 格：Y 看得到 X（T2 上限 12），
            // 但 X 看不到 Y（T1 上限 8），实际不配对，所以放 Y 不算接错流体、放下后也不配对，预览也不显示相连。
            bool laid3 = k.Place(302, 0, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(301, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                         && k.Place(300, 0, U, 0, W, 0) == PipeResult.Ok
                         && k.Place(288, 0, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(289, 0, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok;
            bool noPreview = !k.TryPreviewUnderground(290, 0, E, 1, out _, out _);
            PipeResult asym = k.CheckPlace(290, 0, U, 1, E, 0, out _, out _);
            bool asymOk = laid3 && noPreview && asym == PipeResult.Ok && k.Place(290, 0, U, 1, E, 0) == PipeResult.Ok
                          && k.TryGetCellInfo(290, 0, out PipeCellInfo y1) && !y1.UndergroundLinked;
            Expect(asymOk,
                $"U1 跨度不对称：T2 口对着 10 格外的 T1 口——对方看不到它，不配对、不按对方流体判冲突（{asym}），放置预览也不显示相连");
            CheckUndergroundCrossRemove();
        }

        /// <summary>
        /// 复审 P1：十字交叉处的地下口 I 同时挡着 x 轴、y 轴两对口。拆 I 会放出 A–B（水 ↔ 空网）和 C–D（空网 ↔ 原油）两对新配对，
        /// B、C 的地面一侧用一段没有泵的管线连着，于是“水 → 空网 → 原油”串成一个网络。逐对看每对都有一侧没流体，必须合起来看才拒绝。
        /// </summary>
        private static void CheckUndergroundCrossRemove()
        {
            var k = new PipeKernel(PipeConfig.Default);
            const int N = 0, E = 1, S = 2, W = 3;
            const PipePieceKind U = PipePieceKind.Underground, P = PipePieceKind.Pipe;
            // I (400,400) 朝西，与 A (397,400) 朝东配对；A 的地面一侧接水泵 (395,400)。
            // B (403,400) 朝西：往西第一件是同向的 I，不配对。C (400,403) 朝南、D (400,397) 朝北：往 I 方向第一件都是朝西的 I，不配对。
            // D 的地面一侧接原油泵 (400,395)；B 的地面一侧 (404,400) 用一段没有泵的管线绕到 C 的地面一侧 (400,404)。
            bool laid = k.Place(395, 400, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(396, 400, P, 0, 0, 0) == PipeResult.Ok
                        && k.Place(397, 400, U, 0, E, 0) == PipeResult.Ok && k.Place(400, 400, U, 0, W, 0) == PipeResult.Ok
                        && k.Place(403, 400, U, 0, W, 0) == PipeResult.Ok && k.Place(400, 403, U, 0, S, 0) == PipeResult.Ok
                        && k.Place(400, 395, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(400, 396, P, 0, 0, 0) == PipeResult.Ok
                        && k.Place(400, 397, U, 0, N, 0) == PipeResult.Ok;
            (int x, int y)[] loop = { (404, 400), (404, 401), (404, 402), (404, 403), (404, 404), (403, 404), (402, 404), (401, 404), (400, 404) };
            foreach ((int x, int y) c in loop)
            {
                laid &= k.Place(c.x, c.y, P, 0, 0, 0) == PipeResult.Ok;
            }
            k.Step();
            bool shape = laid && k.TryGetCellInfo(397, 400, out PipeCellInfo a0) && a0.UndergroundLinked && a0.PartnerX == 400
                         && k.TryGetCellInfo(403, 400, out PipeCellInfo b0) && !b0.UndergroundLinked
                         && k.TryGetCellInfo(400, 403, out PipeCellInfo c0) && !c0.UndergroundLinked
                         && k.TryGetCellInfo(400, 397, out PipeCellInfo d0) && !d0.UndergroundLinked
                         && k.NetworkAt(403, 400) == k.NetworkAt(400, 403);
            int resolvedBefore = k.FluidConflictsResolved;
            PipeResult check = k.CheckRemove(400, 400, out int fa, out int fb);
            bool refused = shape && check == PipeResult.FluidConflict && ((fa == 1 && fb == 2) || (fa == 2 && fb == 1))
                           && k.Remove(400, 400, out _) == PipeResult.FluidConflict && k.HasCell(400, 400);
            k.Step();
            bool noRename = k.FluidConflictsResolved == resolvedBefore
                            && k.TryGetNetworkInfo(k.NetworkAt(396, 400), out PipeNetInfo water) && water.Fluid == 1
                            && k.TryGetNetworkInfo(k.NetworkAt(400, 396), out PipeNetInfo oil) && oil.Fluid == 2;
            // 不过度拒绝：先拆掉中间那段空管线的一格（B、C 不再相连），两对新配对各自只连一种流体，拆 I 照常成功。
            bool cut = k.Remove(404, 402, out _) == PipeResult.Ok;
            PipeResult after = k.CheckRemove(400, 400, out _, out _);
            bool removed = cut && after == PipeResult.Ok && k.Remove(400, 400, out _) == PipeResult.Ok;
            k.Step();
            bool relinked = removed && k.TryGetCellInfo(397, 400, out PipeCellInfo a1) && a1.UndergroundLinked && a1.PartnerX == 403
                            && k.TryGetCellInfo(400, 403, out PipeCellInfo c1) && c1.UndergroundLinked && c1.PartnerY == 397
                            && k.FluidConflictsResolved == resolvedBefore
                            && k.TryGetNetworkInfo(k.NetworkAt(396, 400), out PipeNetInfo water2) && water2.Fluid == 1
                            && k.TryGetNetworkInfo(k.NetworkAt(400, 396), out PipeNetInfo oil2) && oil2.Fluid == 2;
            Expect(refused && noRename && relinked,
                $"U1 十字交叉处拆口：两对新配对经一段空管线把水与原油串起来 → 拒绝（{check}，流体 {fa} / {fb}），流体没被改名；断开那段空管线后照常拆、两对各自改配对（{after}）");
        }

        // ── U2 地下管线（真实家园）──────────────────────────────────────────────────

        private static void CheckUndergroundHome()
        {
            CampaignState s = NewWorld(4521, scrap: 400);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 10, 6, 8f, 22f);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (o == null || mode == null)
            {
                Fail("U2 找不到 10×6 的空地 / 没有建造模式");
                return;
            }
            // 燃油发电机在右边，燃油口外那一格 (8,2)；地下口 B (7,2) 朝西、A (2,2) 朝东；A 的地面一侧 (1,2) 接供油管线；中间 x = 4 有一条南北向传送带横穿。
            GridCell pivot = FgProductionSelfCheck.At(o.Value, 8, 4);
            BuildingRecord gen = Built(s, Fuel, "ugen", pivot);
            GridCell genPipe = FgProductionSelfCheck.At(o.Value, 8, 2);
            GridCell cellB = FgProductionSelfCheck.At(o.Value, 7, 2);
            GridCell cellA = FgProductionSelfCheck.At(o.Value, 2, 2);
            GridCell feed = FgProductionSelfCheck.At(o.Value, 1, 2);
            bool belts = FgProductionSelfCheck.Belts(s, FgProductionSelfCheck.At(o.Value, 4, 1), BeltDir.North, 3);
            bool pipes = PipeNetworkService.TryPlace(s, genPipe, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, feed, PipePieceKind.Pipe, 0, 0).Ok;
            string previewA = null, previewB = null;
            bool placedA, placedB, hintOk = false;
            try
            {
                mode.Open();
                mode.Select("pipe_underground_t1");
                string hint = GridContent.TryGetTool("pipe_underground_t1", out BuildTool tool) ? BuildModeHudUIToolkit.ToolHint(tool, "R") : string.Empty;
                while (HomeGridService.BeltDirOf(mode.GhostRotation) != BeltDir.West)
                {
                    mode.RotateGhost();
                }
                mode.SetHover(s, cellB);
                mode.RefreshPreview(s);
                previewB = PreviewText(mode, s, cellB);
                placedB = mode.ClickCell(s, cellB).Success;
                while (HomeGridService.BeltDirOf(mode.GhostRotation) != BeltDir.East)
                {
                    mode.RotateGhost();
                }
                mode.SetHover(s, cellA);
                mode.RefreshPreview(s);
                placedA = mode.ClickCell(s, cellA).Success;
                hintOk = hint.Contains("最多隔 8 格") && hint.Contains("单击放一口");
            }
            finally
            {
                mode.Close();
            }
            bool built = placedA && placedB && StepUntil(() => PipeNetworkService.TryGetPiece(cellA, out PipePieceKind ka, out _) && ka == PipePieceKind.Underground
                                                             && PipeNetworkService.TryGetPiece(cellB, out PipePieceKind kb, out _) && kb == PipePieceKind.Underground, 600);
            previewA = PipeNetworkService.TryPreviewUnderground(cellA, 1, 0, out GridCell mateA) ? $"({mateA.X}, {mateA.Y})" : "无";
            bool sameNet = built && PipeNetworkService.Kernel.NetworkAt(feed.X, feed.Y) >= 0 && PipeNetworkService.Kernel.NetworkAt(feed.X, feed.Y) == PipeNetworkService.Kernel.NetworkAt(genPipe.X, genPipe.Y);
            ItemCatalog.TryGet("fuel", out ItemDef fuel);
            int feedId = PipeNetworkService.Kernel.AddProducer(feed.X, feed.Y, fuel.FluidId, 300000, 300000);
            ProductionService.Producer gp = P(s, gen);
            bool fueled = sameNet && StepUntil(() => P(s, gen).Rec.Fueled, 60);
            bool hover = PipeNetworkService.TryDescribeHover(s, cellA, out string title, out string body) && body.Contains("连到") && title.Contains("地下管线");
            bool beltsIntact = BeltNetworkService.Kernel.HasCell(FgProductionSelfCheck.At(o.Value, 4, 2).X, FgProductionSelfCheck.At(o.Value, 4, 2).Y);
            PipeNetworkService.Kernel.RemoveProducer(feedId, out _);
            Expect(belts && pipes && built && sameNet && fueled && hover && beltsIntact && hintOk && previewB != null && previewB.Contains("还没配对"),
                $"U2 建造菜单选地下管线 T1：先放朝西的一口（预览“{previewB}”），再放朝东的一口，机器施工建成后两口配对（{previewA}）——燃油从地下穿过一条传送带送到燃油发电机（发电机有油：{fueled}）；" +
                $"传送带照常在中间；悬停“{title}：{body?.Split('\n')[0]}”");
        }

        // ── U3 拆地下口会接错流体（真实家园拆除入口，修复轮 P1）──────────────────────────────

        private static void CheckUndergroundRemoveHome()
        {
            CampaignState s = NewWorld(4523, scrap: 400);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 12, 3, 8f, 22f);
            if (o == null || !PipeNetworkService.IsRunning)
            {
                Fail("U3 找不到 12×3 的空地 / 管线未运行");
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            GridCell C(int dx) => FgProductionSelfCheck.At(o.Value, dx, 1);
            const PipePieceKind U = PipePieceKind.Underground;
            const int E = 1, W = 3;
            // 水泵 (0) → 管线 (1) → A (2) 朝东 ⇄ M (5) 朝西；B (8) 朝西被 M 挡住，地面一侧 (9) 管线接原油泵 (10)。
            // 泵直接放进内核（这一片空地不是水源 / 油井；拆除走的是真实家园的拆除入口，与放泵的格网规则无关）。
            bool laid = k.Place(C(0).X, C(0).Y, PipePieceKind.Pump, 0, 0, 1) == PipeResult.Ok && k.Place(C(1).X, C(1).Y, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                        && k.Place(C(2).X, C(2).Y, U, 0, E, 0) == PipeResult.Ok && k.Place(C(5).X, C(5).Y, U, 0, W, 0) == PipeResult.Ok
                        && k.Place(C(10).X, C(10).Y, PipePieceKind.Pump, 0, 0, 2) == PipeResult.Ok && k.Place(C(9).X, C(9).Y, PipePieceKind.Pipe, 0, 0, 0) == PipeResult.Ok
                        && k.Place(C(8).X, C(8).Y, U, 0, W, 0) == PipeResult.Ok;
            int resolvedBefore = k.FluidConflictsResolved;
            // 单拆 M（建造模式拆除 / 规划拆除 / 撤销共用的入口）→ 拒绝，原因写出水与原油。
            GridOpResult single = HomeGridService.TryRemoveBelts(s, new List<GridCell> { C(5) });
            string why = single.Describe();
            bool refused = laid && !single.Success && HomeGridService.LastPipesRefused == 1 && k.HasCell(C(5).X, C(5).Y)
                           && why.Contains(PipeNetworkService.FluidName(1)) && why.Contains(PipeNetworkService.FluidName(2)) && why.Contains("重新配对");
            // 直接走服务层拆除也拒绝。
            PipeOpResult direct = PipeNetworkService.TryRemove(s, C(5), out _);
            bool directRefused = !direct.Ok && direct.Code == PipeResult.FluidConflict;
            k.Step();
            bool noRename = k.FluidConflictsResolved == resolvedBefore;
            // 框里同时有 M 与 B（M 先轮到）：B 拆掉后 M 就能拆，两口都拆掉，与顺序无关。
            GridOpResult both = HomeGridService.TryRemoveBelts(s, new List<GridCell> { C(5), C(8) });
            bool bothOk = both.Outcome == GridOpResult.Kind.BeltsRemoved && HomeGridService.LastPipesRemoved == 2 && HomeGridService.LastPipesRefused == 0
                          && !k.HasCell(C(5).X, C(5).Y) && !k.HasCell(C(8).X, C(8).Y);
            Expect(refused && directRefused && noRename && bothOk,
                $"U3 真实家园拆除：拆掉中间的地下口会让水与原油两口重新配对 → 拒绝、口留在原地，原因“{why}”；流体没被改名；框里连另一头一起拆时两口都拆掉（拆 {HomeGridService.LastPipesRemoved} 口）");
        }

        private static string PreviewText(HomeValleyBuildMode mode, CampaignState s, GridCell cell)
        {
            BeltPathPlan plan = mode.ToolPreview;
            if (plan == null || !plan.IsPipe || plan.Pipe != PipePieceKind.Underground)
            {
                return null;
            }
            return PipeNetworkService.TryPreviewUnderground(cell, (int)plan.Dirs[0], plan.Tier, out GridCell mate)
                ? GameText.Format("ui.build.preview_underground_linked", mate.X, mate.Y, Math.Abs(mate.X - cell.X) + Math.Abs(mate.Y - cell.Y) - 1)
                : GameText.Format("ui.build.preview_underground_unlinked", PipeNetworkService.UndergroundSpan(plan.Tier));
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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
                PowerEnvironment.ResetForTests();
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
