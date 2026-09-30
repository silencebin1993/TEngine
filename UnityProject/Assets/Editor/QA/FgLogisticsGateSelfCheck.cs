using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
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
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-09 存读档、后台一致性与性能门禁（FG03 第 5～7 节；FG15 FGR-SYS-004 / 005 / 041～043；验收 FGT-LOG-010～012）。
    ///
    /// 在真实载入的家园里搭一条“满载产线”（全部经正式入口：仓库输出口 → 传送带闭环 → 仓库输入口；分流器 / 合流器 / 地下传送带与测试端口；
    /// 跨区块边界的传送带与管线；泵 → 管线 → 储罐 → 阀门 → 消费者；三座电塔的电网链 + 发电机 + 用电建筑；两座虚影施工 + 传送带规划 + 搬运机；
    /// 装配站排产；传送带耐久；污染改过的区块），然后：
    /// S 任意时刻存读档（FGT-LOG-010）：按剧本（传送带改线、分流比例、电塔被毁断网、寒潮、放虚影、到货、带子受损……）一直跑，
    ///   在 8 个时刻（内核节拍中途、刚改完拓扑同一步、刚断网同一步、搬运机货舱有料、施工进行中、寒潮计时中、暂停中、随机步）真文件存档；
    ///   每个存档读回来：读档瞬间与存档瞬间全状态逐字段一致（格网 / 虚影与施工 / 传送带与物品与统计窗口 / 流体与储量 / 结冰计时 / 电网拓扑与曲线 / 施工队列 / 机器与货舱），
    ///   再按剩下的剧本接着跑到终点，与一直不存档跑的结果逐字段一致；同一存档读两次结果一致；存读档不复制也不丢失物品。
    /// T 倍速（FG03 第 5 节“3x 速度下吞吐按比例提高，结果与 1x 跑同样的游戏时间一致”）：暂停 / 0.5x / 1x / 2x / 3x 帧驱动跑同样的游戏时间，
    ///   全状态逐字段一致，真实时间按倍速缩放、每真实秒的产量按倍速放大。
    /// D 后台一致性（FGT-LOG-011）：同一存档跑 1 个游戏日——一直看家园 / 离开再返回（镜头切到远征地点、飞到 900 格外、再回来，中间暂停与变速）/ 无头推进，
    ///   三遍全状态逐字段一致；离开期间家园真的在生产；回来后画面按当前状态重建。
    /// P 性能门禁（FGT-LOG-012）：FG03 第 7 节规模（传送带 15,000 格 / 30,000 件含节点、管线 3,000 格、建筑 800 座含电塔与虚影）下的内核单步、
    ///   热更层与画面对账每帧开销与数量无关、存档体积 / 存读时长（FGR-SYS-005）、3x 帧时间、改线重建、托管分配。
    /// 另：已移除建筑类型读档转废料（DEBT-FG0SAVE01-07）、画面对账变化驱动（DEBT-FG0ARCH04-09）、接入计时按模拟步（DEBT-FG0ARCH01-09）、
    ///   寻路桥接边界（DEBT-FG0ARCH06-07）、存档体积与探索面积无关（FGR-SYS-005 / FGR-GEN-062）、种子无关（B25）。
    /// 测量环境写进报告：Editor batchmode（影子工程），内核 Burst / AOT，热更层 Mono JIT（真机 HybridCLR 解释执行另测：FG15-SYS-02）。
    /// </summary>
    public static partial class FgLogisticsGateSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 3;
        private const int RunSlot = 4;
        private const int ReplaySlot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static FakeReader _reader;

        [MenuItem("BinGames/自检：FG 存读档、后台一致性与性能门禁")]
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
            Line("\n[存读档、后台一致性与性能门禁] 满载产线任意时刻存读档 / 3x 产量 / 1 个游戏日观察与离开再返回一致 / FG03 第 7 节规模性能（FG3-LOG-09）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fglog09-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                CameraDirector.RealDeltaTime = () => 1f / 60f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                FgPipeSelfCheck.PrepareFluidIds();
                Line($"  · 测量环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，{SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"图形设备 {SystemInfo.graphicsDeviceType}，Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；传送带内核是 Burst 作业（与真机同为 AOT 原生），管线 / 电网内核是 AOT 程序集的托管代码" +
                     "（Editor 下 Mono JIT，真机 IL2CPP AOT），热更层 Editor 下 Mono JIT、真机走 HybridCLR 解释执行（真机另测：FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFormatAndStatsWindows);
                Step(CheckArbitraryMomentSaveLoad);
                Step(CheckSeedMatrix);
                Step(CheckSpeedMatrixAndThroughput);
                Step(CheckObservedLeaveReturnOneDay);
                Step(CheckRemovedBuildingTypes);
                Step(CheckVisualSyncEventDriven);
                Step(CheckInteractionTimersFollowSteps);
                Step(CheckNavBridgeBoundary);
                Step(CheckSaveSizeVsExploration);
                Step(CheckScalePerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"门禁自检抛异常：{e}");
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
                SaveContentReconciler.ResetForTests();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                HomeValleyPowerGrid.ResetForTests();
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
            Line($"  · [存读档、后台一致性与性能门禁] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.TryGetTuning("home.visual_slice_buildings", out float slice) && Mathf.Approximately(slice, 48f);
            bool text = GameText.Has("save.notice.building_removed") && !GameText.ContainsMarker(GameText.Format("save.notice.building_removed", "A", "1", "2"));
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = GameText.Format("save.notice.building_removed", "A", "1", "2").Contains("scrap");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(tuning && text && en && BeltKernel.FormatVersion == 4,
                $"数据：画面对账轮询调参 home.visual_slice_buildings = {slice} 入 fg.TbHomeTuning；读档通知“建筑已移除”文本键中英两套；传送带内核存档格式升到 {BeltKernel.FormatVersion}（统计窗口进存档）");
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            NotificationCenter.ResetForTests(); // 通知的聚合窗口按真实时间：每一遍从干净的通知中心开始，不串到上一遍的条目里
            NotificationCenter.AutoPauseHandler = null;
            InputRouter.DebugSetReader(_reader);
        }

        /// <summary>真实载入家园（传送带 / 管线 / 电网 / 寻路 / 战斗内核、端口对账、机器都在）。</summary>
        private static CampaignState NewWorld(int seed, bool observe, int scrap)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fglog09-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            NotificationCenter.Bind(s); // 与正式流程一样：换战役时通知中心绑定到这一局
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static BuildingRecord Anchor(CampaignState s, string typeId) =>
            (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).FirstOrDefault(b => b != null && b.BuildingTypeId == typeId && b.RegionId == HomeValleyLayout.RegionId);

        private static bool Free(CampaignState s, GridCell c) => HomeGridService.ValidateBeltCell(s, c).Ok;

        private static bool FreePipe(CampaignState s, GridCell c) => HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok;

        private static GridCell? FindValid(CampaignState s, string typeId, GridCell from, int radius)
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
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, typeId, c, 0, checkCost: false).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>从 <paramref name="from"/> 附近找一段横跨区块边界的 <paramref name="len"/> 格东西向直线（每格 <paramref name="ok"/>），返回起点。B25：按格网规则找。</summary>
        private static GridCell? FindChunkCrossingRow(CampaignState s, GridCell from, int len, int radius, Func<GridCell, bool> ok)
        {
            int cs = HomeGridService.MapFor(s).ChunkSize;
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
                        var start = new GridCell(from.X + dx, from.Y + dy);
                        // 必须跨过一条区块边界：起点与终点在不同区块。
                        if (FloorDiv(start.X, cs) == FloorDiv(start.X + len - 1, cs))
                        {
                            continue;
                        }
                        bool all = true;
                        for (int k = -1; k <= len && all; k++)
                        {
                            all = ok(new GridCell(start.X + k, start.Y)) && ok(new GridCell(start.X + k, start.Y + 1)) && ok(new GridCell(start.X + k, start.Y - 1));
                        }
                        if (all)
                        {
                            return start;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>运行中新增一台机器：与正式的新增路径（装配站出厂）一样登记名册与机器配置（蓝图 → 武器 / 组件），下一个模拟步进战斗内核。</summary>
        private static int SpawnRegistered(CampaignState s, string chassis, string blueprint, Vector2 at, float hp)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, hp, hp);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>核心附近找一格水源（泵能放），朝某个方向连续 <paramref name="len"/> 格能铺管线、不是流体源，且这段管线跨过区块边界（B25：按地形找）。</summary>
        private static bool FindWaterCrossing(CampaignState s, int len, out GridCell pump, out int dir)
        {
            GridCell core = HomeGridService.CorePivot(s);
            int cs = HomeGridService.MapFor(s).ChunkSize;
            int w = PipeNetworkService.FluidId("water");
            pump = default;
            dir = 0;
            for (int r = 3; r <= 90; r++)
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
                        if (PipeNetworkService.SourceFluidAt(s, c) != w || !HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pump).Ok)
                        {
                            continue;
                        }
                        for (int d = 0; d < 4; d++)
                        {
                            GridCell a = FgPipeSelfCheck.Along(c, d, 1);
                            GridCell b = FgPipeSelfCheck.Along(c, d, len);
                            if (FloorDiv(a.X, cs) == FloorDiv(b.X, cs) && FloorDiv(a.Y, cs) == FloorDiv(b.Y, cs))
                            {
                                continue;
                            }
                            bool ok = true;
                            for (int i = 1; i <= len && ok; i++)
                            {
                                GridCell p = FgPipeSelfCheck.Along(c, d, i);
                                // 两侧也要空：不和旁边已有的管线件意外连成一个网络。
                                GridCell l = FgPipeSelfCheck.Along(p, (d + 1) & 3, 1);
                                GridCell rr = FgPipeSelfCheck.Along(p, (d + 3) & 3, 1);
                                ok = FreePipe(s, p) && PipeNetworkService.SourceFluidAt(s, p) == 0 && FreePipe(s, l) && FreePipe(s, rr);
                            }
                            if (ok && FreePipe(s, FgPipeSelfCheck.Along(c, d, len + 1)))
                            {
                                pump = c;
                                dir = d;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        // ── 满载产线 ───────────────────────────────────────────────────────────

        /// <summary>产线里各部件的坐标与 ID（剧本按坐标 / ID 操作，读档后同样找得到）。</summary>
        private sealed class Line3
        {
            public string WarehouseId;
            public int WarehouseInPort;
            public int WarehouseOutPort;
            public GridCell NodeOrigin;
            public GridCell Splitter;
            public GridCell Merger;
            public GridCell Underground;
            public GridCell ChunkBeltStart;
            public int ChunkBeltLen;
            public GridCell Water;
            public int WaterDir;
            public GridCell PipeCrossStart;
            public int PipeCrossLen;
            public string PoleP2;
            public string GhostA;
            public string GhostB;
            public GridCell GhostSpotC;
            public GridCell BeltPlanStart;
            public GridCell LoopDamageCell;
            public GridCell StubStart;
            public GridCell PollutedCell;
            public string Failure;
            public readonly List<(GridCell cell, BeltDir dir)> Loop = new List<(GridCell, BeltDir)>();
        }

        private const int PortSrcMain = 91001;
        private const int PortSrcMergeN = 91002;
        private const int PortSrcMergeS = 91003;
        private const int PortSinkSplitN = 91004;
        private const int PortSinkSplitS = 91005;
        private const int PortSinkUnder = 91006;
        private const int PortSrcChunk = 91007;
        private const int PortSinkChunk = 91008;
        private const ushort ItemB = 2;
        private const ushort ItemC = 3;

        /// <summary>
        /// 满载产线（B25：全部按种子地形现找位置，不写死坐标）。返回 null = 放不下（写进 <see cref="Line3.Failure"/>）。
        /// 测试捷径（与前序 Story 的自检同一做法）：开局受损的仓库 / 发电机 / 信号塔 / 装配站直接改成运转；电网链的三座电塔与发电机是“已建成”夹具；
        /// 用电的维修台挪到链尾；测试端口经传送带内核入口登记。其余全部走正式入口。
        /// </summary>
        private static Line3 BuildFullLine(CampaignState s, bool withExpeditionMachines = false)
        {
            var L = new Line3();
            GridCell core = HomeGridService.CorePivot(s);
            foreach (string t in new[] { HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeSignalTower,
                         HomeValleyLayout.BuildingTypeAssemblyStation })
            {
                BuildingRecord anchor = Anchor(s, t);
                if (anchor != null)
                {
                    anchor.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);
            BuildingRecord wh = Anchor(s, HomeValleyLayout.BuildingTypeWarehouse);
            L.WarehouseId = wh?.BuildingId;

            // 1) 仓库输出口 → T2 传送带闭环 → 仓库输入口（真实建筑端口，输出过滤只放废料）。
            List<(GridCell cell, BeltDir dir)> loop = FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out string loopFail);
            if (loop == null)
            {
                L.Failure = "仓库闭环铺不下：" + loopFail;
                return L;
            }
            L.Loop.AddRange(loop);
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltItems.ScrapId, out _);
            L.WarehouseInPort = BeltPortService.Find(wh.BuildingId, "warehouse.in0")?.PortId ?? 0;
            L.WarehouseOutPort = BeltPortService.Find(wh.BuildingId, "warehouse.out0")?.PortId ?? 0;
            L.LoopDamageCell = loop[Math.Min(3, loop.Count - 1)].cell;

            // 2) 分流器（3:1，左口只放废料）/ 合流器（右口优先）/ 地下传送带（跨 4 格），三路测试端口持续供货与收货。
            GridCell? area = FgBeltNodeSelfCheck.FindArea(s, new GridCell(core.X - 16, core.Y + 20), 18, 9, 30);
            if (area == null)
            {
                L.Failure = "节点组放不下：没有空地";
                return L;
            }
            if (!FgBeltNodeSelfCheck.LayNodeSet(s, area.Value, out GridCell sp, out GridCell mg, out GridCell un, out string nodeFail))
            {
                L.Failure = "节点组放不下：" + nodeFail;
                return L;
            }
            GridCell o = area.Value;
            L.NodeOrigin = o;
            L.Splitter = sp;
            L.Merger = mg;
            L.Underground = un;
            BeltNetworkService.TrySetSplitter(s, sp, 3, 1, BeltSide.None, BeltItems.ScrapId, BeltConst.FilterAny);
            BeltNetworkService.TrySetMergerPriority(s, mg, BeltSide.Right);
            bool ports = BeltNetworkService.TryAddSource(s, PortSrcMain, new GridCell(o.X, o.Y + 4), BeltItems.ScrapId, 2, BeltConst.Unlimited).Ok
                         && BeltNetworkService.TryAddSource(s, PortSrcMergeN, new GridCell(mg.X, mg.Y + 2), ItemB, 5, BeltConst.Unlimited).Ok
                         && BeltNetworkService.TryAddSource(s, PortSrcMergeS, new GridCell(mg.X, mg.Y - 2), ItemC, 7, BeltConst.Unlimited).Ok
                         && BeltNetworkService.TryAddSink(s, PortSinkSplitN, new GridCell(sp.X, sp.Y + 4), 40, 11).Ok
                         && BeltNetworkService.TryAddSink(s, PortSinkSplitS, new GridCell(sp.X, sp.Y - 4), BeltConst.Unlimited, 0).Ok
                         && BeltNetworkService.TryAddSink(s, PortSinkUnder, new GridCell(un.X + 5, un.Y), 30, 13).Ok;
            if (!ports)
            {
                L.Failure = "节点组测试端口登记失败";
                return L;
            }
            // 合流器输出与地下入口之间补一格（节点组把 merger.X+1 铺好了；地下出口外侧一格放给输入端口）。

            // 3) 跨区块边界的传送带：12 格 T1，源头推满、末端缓存 20 件慢慢消耗（FG03 第 5 节“传送带跨越区块边界”）。
            GridCell? row = FindChunkCrossingRow(s, new GridCell(core.X + 20, core.Y - 30), 12, 40, c => Free(s, c));
            if (row == null)
            {
                L.Failure = "核心附近找不到横跨区块边界的 12 格空地（传送带）";
                return L;
            }
            L.ChunkBeltStart = row.Value;
            L.ChunkBeltLen = 12;
            var chunkPath = new List<(GridCell, BeltDir)>();
            for (int k = 0; k < 12; k++)
            {
                chunkPath.Add((new GridCell(row.Value.X + k, row.Value.Y), BeltDir.East));
            }
            if (!FgBeltFormalSelfCheck.Lay(s, chunkPath, 0, out string chunkFail)
                || !BeltNetworkService.TryAddSource(s, PortSrcChunk, row.Value, ItemB, 3, BeltConst.Unlimited).Ok
                || !BeltNetworkService.TryAddSink(s, PortSinkChunk, new GridCell(row.Value.X + 12, row.Value.Y), 20, 30).Ok)
            {
                L.Failure = "跨区块传送带铺不下：" + chunkFail;
                return L;
            }

            // 4) 流体：泵 → 管线 → 储罐（双向）→ 阀门 → T2 管线 → 两个消费者（FG3-LOG-05 的场景）+ 横跨区块边界的一段管线接在储罐另一侧。
            if (!FgPipeSelfCheck.LayScenario(s, out GridCell water, out int wdir))
            {
                L.Failure = "流体场景放不下";
                return L;
            }
            L.Water = water;
            L.WaterDir = wdir;
            // 跨区块的第二个流体网络：另找一处水源，泵 + 9 格管线跨过区块边界，末端一个消费者（FG03 第 5 节“管线跨越区块边界”）。
            if (!FindWaterCrossing(s, 9, out GridCell pump, out int pdir))
            {
                L.Failure = "核心附近找不到能让管线横跨区块边界的水源";
                return L;
            }
            L.PipeCrossStart = pump;
            L.PipeCrossLen = 10;
            bool pipeOk = PipeNetworkService.TryPlace(s, pump, PipePieceKind.Pump, 0, 0).Ok;
            for (int k = 1; k <= 9 && pipeOk; k++)
            {
                pipeOk = PipeNetworkService.TryPlace(s, FgPipeSelfCheck.Along(pump, pdir, k), PipePieceKind.Pipe, 0, 0).Ok;
            }
            if (!pipeOk)
            {
                L.Failure = "跨区块管线铺不下";
                return L;
            }
            GridCell pend = FgPipeSelfCheck.Along(pump, pdir, 9);
            PipeNetworkService.Kernel.AddConsumer(pend.X, pend.Y, PipeNetworkService.FluidId("water"), 60, 1);

            // 5) 电网：核心 → P1 → P2 → P3 三座电塔的链，链尾一座发电机 2，维修台挪到链尾用电（FG3-LOG-06 的场景）。
            if (!FgPowerGridSelfCheck.LayChain(s, out FgPowerGridSelfCheck.Chain chain, out BuildingRecord p1, out BuildingRecord p2, out BuildingRecord p3, out BuildingRecord gen))
            {
                L.Failure = "电塔链放不下";
                return L;
            }
            L.PoleP2 = p2.BuildingId;

            // 6) 施工：两座虚影（发电机 2；材料不够时等料）+ 一段传送带规划；两台搬运机。
            for (int i = 0; i < 2; i++)
            {
                SpawnRegistered(s, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, new Vector2(core.X - 6 - i * 2, core.Y - 6), 100f);
            }
            if (withExpeditionMachines)
            {
                SpawnRegistered(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(core.X + 4, core.Y - 4), 120f);
                SpawnRegistered(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(core.X + 6, core.Y - 4), 100f);
                SpawnRegistered(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(core.X + 8, core.Y - 4), 100f);
            }
            WorldSimulation.StepMany(2);
            GridCell? ga = FindValid(s, HomeValleyLayout.BuildingTypeGenerator2, new GridCell(core.X - 8, core.Y + 36), 16);
            GridOpResult? a = ga.HasValue ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, ga.Value, 0) : (GridOpResult?)null;
            GridCell? gb = FindValid(s, HomeValleyLayout.BuildingTypeGenerator2, new GridCell(core.X + 12, core.Y + 36), 16);
            GridOpResult? b = gb.HasValue ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, gb.Value, 0) : (GridOpResult?)null;
            GridCell? gc = FindValid(s, HomeValleyLayout.BuildingTypeGenerator2, new GridCell(core.X - 30, core.Y + 10), 16);
            if (a == null || !a.Value.Success || b == null || !b.Value.Success || gc == null)
            {
                L.Failure = "虚影放不下";
                return L;
            }
            L.GhostA = a.Value.BuildingId;
            L.GhostB = b.Value.BuildingId;
            L.GhostSpotC = gc.Value;
            HomeValleyConstruction.SetPriority(s, HomeValleyWorkOrders.FindActiveBuild(s, L.GhostB).WorkOrderId, 1);
            GridCell? plan = FgBeltNodeSelfCheck.FindArea(s, new GridCell(core.X + 20, core.Y + 20), 6, 2, 30);
            if (plan == null)
            {
                L.Failure = "传送带规划放不下";
                return L;
            }
            L.BeltPlanStart = plan.Value;
            GridOpResult bp = HomeGridService.TryPlaceBeltPath(s, "belt_t1", plan.Value, new GridCell(plan.Value.X + 4, plan.Value.Y), BeltDir.East);
            if (!bp.Success)
            {
                L.Failure = "传送带规划失败：" + bp.Describe();
                return L;
            }
            GridCell? stub = FgBeltNodeSelfCheck.FindArea(s, new GridCell(core.X - 24, core.Y - 12), 8, 2, 30);
            if (stub == null)
            {
                L.Failure = "剧本改线用的空地找不到";
                return L;
            }
            L.StubStart = stub.Value;

            // 7) 装配站排产一台搬运机（耗废料与电）。
            HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHaulerId);

            // 8) 模拟一次改地面的事件（污染：FG7 之前没有正式来源，写格网的唯一入口），让存档里有“只存被修改的区块”的差异（FGR-GEN-060）。
            GridCell? dirty = FgBeltNodeSelfCheck.FindArea(s, new GridCell(core.X + 30, core.Y + 30), 1, 1, 20);
            if (dirty.HasValue)
            {
                L.PollutedCell = dirty.Value;
                HomeGridMap pm = HomeGridService.MapFor(s);
                pm.SetPollution(dirty.Value, (byte)((pm.GetPollution(dirty.Value) + 1) % 4));
            }

            // 9) 传送带受损（耐久表进存档）。
            BeltNetworkService.TryDamage(s, L.LoopDamageCell, 20, out _);
            return L;
        }

        /// <summary>剧本：在相对起点的第几步做什么（全部经正式入口；只按坐标 / ID 找对象，读档后同样找得到）。</summary>
        private static List<(long at, string name, Action<CampaignState> act)> Script(Line3 L)
        {
            return new List<(long, string, Action<CampaignState>)>
            {
                (301, "改线：在空地上铺 6 格传送带（拓扑变化）", s =>
                {
                    for (int k = 0; k < 6; k++)
                    {
                        BeltNetworkService.TryPlace(s, new GridCell(L.StubStart.X + k, L.StubStart.Y), BeltDir.East, 0);
                    }
                }),
                (905, "分流比例改 2:1、左口不设过滤", s => BeltNetworkService.TrySetSplitter(s, L.Splitter, 2, 1, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny)),
                (1502, "电塔 P2 被毁：电网断成两段", s => HomeValleyPowerGrid.ApplyBuildingDestroyed(s, L.PoleP2)),
                (2104, "寒潮开始（流体网络结冰计时）", s => PipeNetworkService.SetColdSnap(s, true)),
                (2700, "战略规划：再放一座发电机 2 虚影（缺料等待）", s => HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, L.GhostSpotC, 0)),
                (3301, "仓库到货 60 废料（测试捷径：代表回收所得）", s => s.Scrap += 60),
                (3905, "传送带受损 30 点", s => BeltNetworkService.TryDamage(s, L.LoopDamageCell, 30, out _)),
                (4502, "改线：拆掉刚才铺的第 3 格（拆除返还带上的物品）", s => BeltNetworkService.TryRemove(s, new GridCell(L.StubStart.X + 2, L.StubStart.Y), new List<ushort>())),
                (5104, "寒潮结束", s => PipeNetworkService.SetColdSnap(s, false)),
                (5700, "仓库输出口过滤改“全部可存物品”", s => BeltPortService.TrySetFilter(s, L.WarehouseId, "warehouse.out0", BeltPortService.FilterAll, out _)),
            };
        }

        /// <summary>
        /// 按剧本推进到第 <paramref name="toRel"/> 步（相对 <paramref name="origin"/>）。每一步：先做这一步的剧本事件，再（若是存档点）回调，再走一个模拟步。
        /// <paramref name="fromRel"/> 之前的事件不再做（读档接着跑时它们已经在存档里）。帧驱动时用 tickLimit 精确停在事件 / 存档点上。
        /// </summary>
        private static void RunScript(long origin, List<(long at, string name, Action<CampaignState> act)> script, long fromRel, long toRel,
            Func<long, bool> onTick, float speed = 1f, bool frames = false)
        {
            var stops = new SortedSet<long>(script.Select(e => e.at).Where(t => t >= fromRel && t <= toRel)) { toRel };
            long rel = GameClock.Ticks - origin;
            foreach (long stop in stops)
            {
                if (stop < rel)
                {
                    continue;
                }
                AdvanceTo(origin + stop, speed, frames, onTick == null ? null : (Func<long, bool>)(t => onTick(t - origin)));
                rel = GameClock.Ticks - origin;
                if (rel == stop && stop < toRel)
                {
                    foreach ((long at, string _, Action<CampaignState> act) in script)
                    {
                        if (at == stop)
                        {
                            act(CampaignSession.Current);
                        }
                    }
                }
            }
        }

        /// <summary>推进到绝对步 <paramref name="target"/>；<paramref name="onTick"/> 在每个步序号（执行这一步之前）被问一次，返回 true = 在这里停下交给调用方（存档点）。</summary>
        private static void AdvanceTo(long target, float speed, bool frames, Func<long, bool> onTick)
        {
            GameClock.SetSpeed(speed);
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 4_000_000)
            {
                if (onTick != null && onTick(GameClock.Ticks))
                {
                    // 调用方在这一步前做了存档；继续。
                }
                if (frames)
                {
                    FrameOnce(1f / 60f, onTick != null ? GameClock.Ticks + 1 : target);
                }
                else
                {
                    WorldSimulation.StepMany(1);
                }
            }
            GameClock.SetSpeed(1f);
        }

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

        // ── 全状态快照 ───────────────────────────────────────────────────────────

        private static Dictionary<FeedbackCueId, int> CueCounts()
        {
            var d = new Dictionary<FeedbackCueId, int>();
            foreach (FeedbackCueId id in Enum.GetValues(typeof(FeedbackCueId)))
            {
                d[id] = FeedbackCues.CountOf(id);
            }
            return d;
        }

        private static Dictionary<string, int> NoteCounts()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (NotificationEntry e in NotificationCenter.History)
            {
                string k = e.Type?.Id ?? "?";
                d[k] = d.TryGetValue(k, out int n) ? n + e.Count : e.Count;
            }
            return d;
        }

        /// <summary>
        /// 全状态逐字段快照：真实存档路径（写回全部实时状态 + 导出机器记录）→ JsonUtility → 拍平成“路径 = 值”（GUID 片段按出现顺序编号；
        /// 通知历史与存档历史按真实时间记录，排除，只比各类通知条数与反馈时刻的增量）；再加内核状态哈希与玩家看得到的读数
        /// （传送带每个网络的实测收下 / 推上、每个端口的实测件数、分流器实测左右口；管线每个网络的供给 / 需求 / 送达 / 缓冲；电网每个子网）。
        /// <paramref name="pipeReadings"/> = false：管线读数窗口是最近 1 游戏秒的派生量、不进存档（读档后 1 游戏秒内按最近一步显示），读档瞬间的对照不比它。
        /// </summary>
        private static Dictionary<string, string> Snap(Dictionary<FeedbackCueId, int> cueBase, Dictionary<string, int> noteBase, Line3 L, bool pipeReadings = true)
        {
            CampaignState s = CampaignSession.Current;
            WorldSimulation.SyncAllForSave();
            MachineRegistry.ExportToCampaignState(s);
            GameLogic.Campaign.WorldGen.WorldGenService.CaptureDiffs(s); // 与存档同一步：把被修改的区块写进区块差异
            // 比较“写进存档的内容”：在克隆上做存档规范化（按稳定 ID 排序；ERD-SAV-004“未排序列表不得影响结算”），不改运行中的状态。
            CampaignState canon = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(s));
            canon.NormalizeForSave();
            string json = JsonUtility.ToJson(canon);
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            FgWorldSimSelfCheck.MiniJson.Flatten(json, flat);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in flat.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                // 通知 / 存档历史按真实时间记录；LastSaveReason 是“上一次存档的原因”（存档元数据，存档那一刻才写）：都不比。
                if (kv.Key.StartsWith("Notifications", StringComparison.Ordinal) || kv.Key.StartsWith("SaveHistory", StringComparison.Ordinal)
                    || kv.Key == "LastSaveReason")
                {
                    continue;
                }
                result[kv.Key] = FgWorldSimSelfCheck.HexId.Replace(kv.Value, m =>
                {
                    if (!ids.TryGetValue(m.Value, out string token))
                    {
                        token = "#id" + ids.Count;
                        ids[m.Value] = token;
                    }
                    return token;
                });
            }
            if (cueBase != null)
            {
                foreach (KeyValuePair<FeedbackCueId, int> kv in CueCounts())
                {
                    result["·cue." + kv.Key] = (kv.Value - cueBase[kv.Key]).ToString(CultureInfo.InvariantCulture);
                }
            }
            if (noteBase != null)
            {
                foreach (KeyValuePair<string, int> kv in NoteCounts())
                {
                    result["·notify." + kv.Key] = (kv.Value - (noteBase.TryGetValue(kv.Key, out int b) ? b : 0)).ToString(CultureInfo.InvariantCulture);
                }
            }
            result["·clock.ticks"] = GameClock.Ticks.ToString(CultureInfo.InvariantCulture);
            // 战斗内核（家园的机器 / 炮塔 / 敌人）：状态哈希、计数器、逐单位读数（按单位稳定编号），快照不同时能看出是哪一层不同。
            GameLogic.Campaign.Combat.CombatSite cs = WorldSimulation.Home?.Combat;
            if (cs?.Kernel != null && !cs.IsDisposed)
            {
                BinGames.Sim.Combat.CombatKernel ck = cs.Kernel;
                result["·combat.hash"] = ck.StateHash().ToString("X16");
                BinGames.Sim.Combat.CombatCounters cc = ck.Counters;
                result["·combat.counters"] = $"{cc.Steps}|{cc.ShotsFired}|{cc.ProjectilesSpawned}|{cc.KillsPlayer}|{cc.KillsHostile}|{cc.CuesDropped}|{cc.GameplayEventsDeferred}|{cc.Compactions}";
                result["·combat.weapons"] = ck.WeaponCount + "|" + ck.ProfileCount + "|" + ck.SlotCount;
                for (int slot = 0; slot < ck.SlotCount; slot++)
                {
                    BinGames.Sim.Combat.CombatUnitView v = ck.ViewAt(slot);
                    result["·combat.u#" + v.Id] = $"{v.ExtKey}|{v.Kind}|{v.Faction}|{v.Behavior}|{(uint)v.Flags}|{v.Position.x.ToString("R", CultureInfo.InvariantCulture)},{v.Position.y.ToString("R", CultureInfo.InvariantCulture)}|" +
                                                  $"{v.Health.ToString("R", CultureInfo.InvariantCulture)}|{v.Weapon}|{v.Command}|{v.CommandTarget}|slot{slot}";
                }
            }
            BeltKernel bk = BeltNetworkService.Kernel;
            if (bk != null)
            {
                result["·belt.hash"] = bk.ComputeStateHash().ToString("X16");
                var anchors = new List<int3>();
                bk.CollectNetworkAnchors(anchors);
                foreach (int3 a in anchors)
                {
                    if (bk.TryGetNetworkStats(a.z, out BeltNetworkStats n))
                    {
                        result[$"·belt.net@{a.x},{a.y}"] = $"{n.Cells}|{n.Items}|{n.BlockedCells}|{n.DeliveredInWindow}|{n.EmittedInWindow}|{n.WindowSeconds.ToString("R", CultureInfo.InvariantCulture)}|{n.HasCycle}";
                    }
                }
                var portIds = new List<int>();
                bk.CollectPortIds(portIds);
                foreach (int id in portIds)
                {
                    if (bk.TryGetPortInfo(id, out BeltPortInfo p))
                    {
                        result["·belt.port#" + id] = $"{p.Total}|{p.Pending}|{p.Buffered}|{p.Consumed}|{p.InWindow}|{p.BlockedSteps}|{p.Connected}";
                    }
                }
                if (L != null)
                {
                    foreach (GridCell c in new[] { L.Splitter, L.Merger, L.Underground })
                    {
                        if (BeltNetworkService.TryGetNode(c, out BeltNodeInfo ni))
                        {
                            result[$"·belt.node@{c.X},{c.Y}"] = $"{ni.Kind}|{ni.SentL}|{ni.SentR}|{ni.SentLInWindow}|{ni.SentRInWindow}|{ni.WindowSeconds.ToString("R", CultureInfo.InvariantCulture)}|{ni.Count}";
                        }
                    }
                }
            }
            PipeKernel pk = PipeNetworkService.Kernel;
            if (pk != null)
            {
                result["·pipe.hash"] = pk.Hash().ToString("X16");
                if (pipeReadings && s.Pipes?.Xs != null)
                {
                    var seen = new HashSet<int>();
                    for (int i = 0; i < s.Pipes.Xs.Length; i++)
                    {
                        int net = pk.NetworkAt(s.Pipes.Xs[i], s.Pipes.Ys[i]);
                        if (net < 0 || !seen.Add(net) || !pk.TryGetNetworkInfo(net, out PipeNetInfo n))
                        {
                            continue;
                        }
                        result[$"·pipe.net@{s.Pipes.Xs[i]},{s.Pipes.Ys[i]}"] = string.Join("|", n.Fluid, n.Cells, n.SupplyLpm.ToString("R", CultureInfo.InvariantCulture),
                            n.DemandLpm.ToString("R", CultureInfo.InvariantCulture), n.DeliveredLpm.ToString("R", CultureInfo.InvariantCulture),
                            n.BufferedLpm.ToString("R", CultureInfo.InvariantCulture), n.StoredMl, n.Issues, n.LastFlowStep);
                    }
                }
            }
            if (HomeValleyPowerGrid.Kernel != null)
            {
                BinGames.Sim.Logistics.PowerKernel pw = HomeValleyPowerGrid.Kernel;
                for (int i = 0; i < pw.SubnetCount; i++)
                {
                    PowerSubnetInfo n = pw.Subnet(i);
                    result["·power.net#" + n.Serial] = $"{n.Supply.ToString("R", CultureInfo.InvariantCulture)}|{n.Demand.ToString("R", CultureInfo.InvariantCulture)}|" +
                                                      $"{n.Delivered.ToString("R", CultureInfo.InvariantCulture)}|{n.Nodes}|{n.Consumers}|{n.Brownouts}|{n.Producers}";
                }
            }
            result["·machines.home"] = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).ToString(CultureInfo.InvariantCulture);
            return result;
        }

        private static string Sample(IEnumerable<string> diffs, int n = 8)
        {
            List<string> list = diffs.Take(n).ToList();
            return list.Count == 0 ? string.Empty : "：" + string.Join(" | ", list);
        }

        /// <summary>战斗单位读数的完整差异（通用比较器把值截到 40 字，看不出是哪个字段）。</summary>
        private static string CombatUnitDiffs(Dictionary<string, string> a, Dictionary<string, string> b, int n = 4)
        {
            var list = new List<string>();
            foreach (KeyValuePair<string, string> kv in a)
            {
                if (!kv.Key.StartsWith("·combat.", StringComparison.Ordinal) || !b.TryGetValue(kv.Key, out string o) || o == kv.Value)
                {
                    continue;
                }
                list.Add($"{kv.Key}: {kv.Value} ≠ {o}");
                if (list.Count >= n)
                {
                    break;
                }
            }
            foreach (KeyValuePair<string, string> kv in b)
            {
                if (kv.Key.StartsWith("·combat.u", StringComparison.Ordinal) && !a.ContainsKey(kv.Key))
                {
                    list.Add($"只在后一边：{kv.Key} = {kv.Value}");
                }
            }
            return list.Count == 0 ? string.Empty : "【" + string.Join(" ‖ ", list) + "】";
        }

        /// <summary>家园里的物品总账：仓库库存 + 各端口待推 / 缓存 + 带上的物品（逐格重数）+ 地面物 + 机器货舱 + 虚影已到现场的材料 + 传送带规划已到的材料。
        /// 同一瞬间存档前与读档后应当相等（存读档不复制、不丢失物品）。</summary>
        private static long ItemLedger(CampaignState s)
        {
            long ports = 0;
            var ids = new List<int>();
            BeltNetworkService.Kernel.CollectPortIds(ids);
            foreach (int id in ids)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(id, out int pending, out int buffered))
                {
                    ports += Math.Max(0, pending) + Math.Max(0, buffered);
                }
            }
            long ground = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Sum(g => (long)g.Amount);
            long cargo = MachineRegistry.AllRecords.Where(m => m != null).Sum(m => (long)HomeValleyConstruction.CargoScrap(m));
            long delivered = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Sum(b => (long)b.ConstructionDelivered)
                             + (s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Sum(p => (long)p.Delivered);
            return s.Scrap + ports + BeltNetworkService.Kernel.CountItemsSlow() + ground + cargo + delivered;
        }

        /// <summary>真实读档（与主菜单“继续”同一顺序：恢复编排 → 家园载入；存档时在外的远征一起载入）。读的是拷贝槽（跑的过程中可能自动存档）。</summary>
        private static RestoreResult LoadCopy(int fromSlot, string observe)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            NotificationCenter.ResetForTests();
            NotificationCenter.AutoPauseHandler = null;
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(ReplaySlot), true);
            string bak = CampaignSaveService.BakPath(ReplaySlot);
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(ReplaySlot);
            if (!rr.Success)
            {
                return rr;
            }
            CampaignSession.Set(ReplaySlot, rr.State);
            NotificationCenter.Bind(rr.State); // 与正式读档一样：通知中心绑定到读回的战役，恢复存档里的通知历史
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            int[] ruinsIds = rr.State.MachineRecords.Where(m => m != null && m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
            if (ruinsIds.Length > 0)
            {
                WorldSimulation.LoadFracturedCity(ruinsIds, resume: true);
            }
            if (observe != null)
            {
                WorldView.Observe(observe);
            }
            InputRouter.DebugSetReader(_reader);
            return rr;
        }

        private static bool SaveTo(int slot)
        {
            SaveResult r = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail($"存档写入失败（槽 {slot}）：{r.Message}");
            }
            return r.Success;
        }

        // ── S 任意时刻存读档（FGT-LOG-010）─────────────────────────────────────────

        private const long WarmTicks = 60 * 30;
        private const long EndRel = 6300;

        private static void CheckArbitraryMomentSaveLoad()
        {
            CampaignState s0 = NewWorld(90901, observe: true, scrap: 260);
            Line3 L = BuildFullLine(s0);
            if (L.Failure != null)
            {
                Fail("S 满载产线搭不起来：" + L.Failure);
                return;
            }
            WorldSimulation.StepMany((int)WarmTicks);
            if (!SaveTo(Slot))
            {
                return;
            }
            long origin = GameClock.Ticks;
            List<(long at, string name, Action<CampaignState> act)> script = Script(L);

            // 满载核对：带上物品占到格容量的一半以上、有堵塞的格（端口 / 下游慢）、三类节点都在走货、电网两个子网之前是一个、施工在进行。
            BeltKernel bk0 = BeltNetworkService.Kernel;
            int cap = bk0.CellCount * bk0.Config.SlotsPerCell;
            Expect(bk0.ItemCount * 2 >= cap && bk0.BlockedCells > 0 && bk0.NodeCount >= 4,
                $"S 产线是满载的：传送带 {bk0.CellCount} 格上 {bk0.ItemCount} 件（容量 {cap}），{bk0.BlockedCells} 格堵着，节点 {bk0.NodeCount} 个；流体 {PipeNetworkService.Kernel.CellCount} 格；" +
                $"建筑 {s0.BuildingRecords.Length} 座（虚影 {s0.BuildingRecords.Count(HomeValleyController.IsPlannedGhost)}）");

            // 存档点：按条件在剧本里找时刻（找到第一个满足条件的步就在那里存）。
            var picks = new List<(string name, Func<CampaignState, long, bool> when, long from)>
            {
                ("刚改完拓扑（改线后的同一步，内核还没重建）", (s, rel) => rel == 301, 301),
                ("内核节拍中途（世界步 ≡ 1 mod 3，传送带 / 管线内核这一步不走）", (s, rel) => rel >= 600 && GameClock.Ticks % 3 == 1, 600),
                ("刚断网（电塔被毁的同一步）", (s, rel) => rel == 1502, 1502),
                ("搬运机货舱里有施工材料", (s, rel) => rel >= 1600 && MachineRegistry.AllRecords.Any(m => m != null && HomeValleyConstruction.CargoScrap(m) > 0), 1600),
                ("施工进行中（虚影已到料、进度过半前）", (s, rel) => rel >= 1800 && s.BuildingRecords.Any(b => b.ConstructionState == BuildingConstructionState.Building && b.ConstructionDelivered > 0), 1800),
                ("寒潮计时中", (s, rel) => rel == 2900, 2900),
                ("统计窗口桶中途（传送带统计桶走了一半）", (s, rel) => rel >= 4100 && BeltNetworkService.Kernel.StepIndex % Math.Max(2, BeltNetworkService.Kernel.Config.BucketSteps) == BeltNetworkService.Kernel.Config.BucketSteps / 2, 4100),
                ("随机步（种子 7）", (s, rel) => rel == 4800 + new System.Random(7).Next(0, 400), 4800),
            };

            // A：一直跑，在每个存档点真文件存档（槽 10+i）并拍“存档瞬间”快照。
            var atSave = new Dictionary<int, Dictionary<string, string>>();
            var ledgerAtSave = new Dictionary<int, long>();
            var saveRel = new Dictionary<int, long>();
            var cueA = CueCounts();
            var noteA = NoteCounts();
            RunScript(origin, script, 0, EndRel, rel =>
            {
                for (int i = 0; i < picks.Count; i++)
                {
                    if (saveRel.ContainsKey(i) || rel < picks[i].from || !picks[i].when(CampaignSession.Current, rel))
                    {
                        continue;
                    }
                    atSave[i] = Snap(cueA, noteA, L, pipeReadings: false);
                    ledgerAtSave[i] = ItemLedger(CampaignSession.Current);
                    saveRel[i] = rel;
                    SaveTo(10 + i);
                }
                return false;
            });
            Dictionary<string, string> endA = Snap(cueA, noteA, L);
            string endAScrap = CampaignSession.Current.Scrap.ToString(CultureInfo.InvariantCulture);
            Expect(saveRel.Count == picks.Count,
                $"S 八个存档点都找到了：{string.Join("；", picks.Select((p, i) => $"{p.name} @+{(saveRel.TryGetValue(i, out long r) ? r.ToString() : "未出现")}"))}");

            // A0：同一个出发存档一直跑、一次都不存：与 A 终态一致（存档本身不扰动模拟）。
            LoadCopy(Slot, HomeValleyLayout.RegionId);
            var cue0 = CueCounts();
            var note0 = NoteCounts();
            RunScript(origin, script, 0, EndRel, null);
            Dictionary<string, string> endA0 = Snap(cue0, note0, L);
            List<string> d0 = FgWorldSimSelfCheck.DiffKeys(endA0, endA);
            Expect(d0.Count == 0 && endA.Count > 800,
                $"S 存档本身不扰动模拟：一直跑不存档与中途存 8 次档的终态逐字段一致（{endA.Count} 个字段）{Sample(d0)}");

            // B：每个存档读回来，读档瞬间与存档瞬间一致，再按剩下的剧本跑到终点与 A 一致。
            var lines = new List<string>();
            bool allRound = true;
            bool allEnd = true;
            bool allLedger = true;
            foreach (int i in saveRel.Keys.OrderBy(k => k))
            {
                RestoreResult rr = LoadCopy(10 + i, HomeValleyLayout.RegionId);
                if (!rr.Success)
                {
                    Fail($"S 读存档点 {i} 失败：{rr.Message}");
                    allRound = false;
                    continue;
                }
                var cueB = CueCounts();
                var noteB = NoteCounts();
                string loadNotes = string.Join("、", NotificationCenter.History.Select(e => e.Type?.Id + "×" + e.Count));
                Dictionary<string, string> loaded = Snap(null, null, L, pipeReadings: false);
                long ledger = ItemLedger(CampaignSession.Current);
                List<string> round = FgWorldSimSelfCheck.DiffKeys(Strip(atSave[i]), loaded);
                RunScript(origin, script, saveRel[i] + 1, EndRel, null);
                Dictionary<string, string> endB = Snap(cueB, noteB, L);
                // 反馈 / 通知计数：B 只数读档之后的，A 从头数；把 A 在存档点之前的部分减掉再比。
                List<string> end = FgWorldSimSelfCheck.DiffKeys(Rebase(endA, atSave[i]), Rebase(endB, null));
                allRound &= round.Count == 0;
                allEnd &= end.Count == 0;
                allLedger &= ledger == ledgerAtSave[i];
                lines.Add($"#{i + 1} {picks[i].name} @+{saveRel[i]}：读档瞬间差异 {round.Count}{Sample(round, 4)}{CombatUnitDiffs(Strip(atSave[i]), loaded)}；终点差异 {end.Count}{Sample(end, 4)}{CombatUnitDiffs(endA, endB)}；物品总账 {ledgerAtSave[i]}→{ledger}；读档瞬间已有通知：{loadNotes}");
            }
            foreach (string l in lines)
            {
                Line("    " + l);
            }
            Expect(allRound && saveRel.Count == picks.Count,
                "S 八个时刻真文件存档后读回：读档瞬间与存档瞬间全状态逐字段一致（格网建筑与朝向、虚影与已到材料、施工单进度与优先级、传送带物品位置与种类、端口待推 / 缓存、统计窗口、" +
                "节点比例轮次、流体每格存量与模式、阀门缓冲、寒潮开关与结冰计时、电网编号与曲线、机器位置与货舱、被修改的区块、耐久表）");
            Expect(allEnd, "S 每个存档读回后按剩下的剧本接着跑到终点（+6300 步），与一直不停跑逐字段一致（含实测吞吐 / 实测比例 / 端口实测读数与管线读数）");
            Expect(allLedger, "S 存读档不复制也不丢失物品：每个存档点“库存 + 端口 + 带上 + 地面 + 货舱 + 现场材料”在存档前与读档后相等");

            // 同一存档读两次：结果一致。
            int twice = saveRel.Keys.Max();
            LoadCopy(10 + twice, HomeValleyLayout.RegionId);
            RunScript(origin, script, saveRel[twice] + 1, EndRel, null);
            string first = CampaignSession.Current.Scrap + "|" + BeltNetworkService.Kernel.ComputeStateHash() + "|" + PipeNetworkService.Kernel.Hash();
            LoadCopy(10 + twice, HomeValleyLayout.RegionId);
            RunScript(origin, script, saveRel[twice] + 1, EndRel, null);
            string second = CampaignSession.Current.Scrap + "|" + BeltNetworkService.Kernel.ComputeStateHash() + "|" + PipeNetworkService.Kernel.Hash();
            Expect(first == second && CampaignSession.Current.Scrap.ToString(CultureInfo.InvariantCulture) == endAScrap,
                $"S 同一存档读两次、各跑到终点：结果相同（库存 {endAScrap}、传送带 / 管线内核哈希一致）");
        }

        /// <summary>读档瞬间的对照：存档前快照里的反馈 / 通知计数是从 A 开头数的，读档后的会话从 0 数——这两类不比（终点对照里另行对齐）。</summary>
        private static Dictionary<string, string> Strip(Dictionary<string, string> snap) =>
            snap.Where(kv => !kv.Key.StartsWith("·cue.", StringComparison.Ordinal) && !kv.Key.StartsWith("·notify.", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        /// <summary>反馈 / 通知计数改成“存档点之后的增量”（<paramref name="atSave"/> = null：本来就是从读档开始数的）。</summary>
        private static Dictionary<string, string> Rebase(Dictionary<string, string> end, Dictionary<string, string> atSave)
        {
            var r = new Dictionary<string, string>(end, StringComparer.Ordinal);
            if (atSave == null)
            {
                return r;
            }
            foreach (string k in end.Keys.ToList())
            {
                if ((k.StartsWith("·cue.", StringComparison.Ordinal) || k.StartsWith("·notify.", StringComparison.Ordinal))
                    && long.TryParse(end[k], out long e))
                {
                    long b = atSave.TryGetValue(k, out string bs) && long.TryParse(bs, out long bv) ? bv : 0;
                    r[k] = (e - b).ToString(CultureInfo.InvariantCulture);
                }
            }
            return r;
        }

        // ── B25 种子矩阵 ───────────────────────────────────────────────────────

        private static void CheckSeedMatrix()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 90911, 424242, 20260930 })
            {
                CampaignState s = NewWorld(seed, observe: true, scrap: 260);
                Line3 L = BuildFullLine(s);
                if (L.Failure != null)
                {
                    all = false;
                    lines.Add($"种子 {seed}：搭不起来（{L.Failure}）");
                    continue;
                }
                WorldSimulation.StepMany(60 * 20 + 2);
                Dictionary<string, string> before = Snap(null, null, L, pipeReadings: false);
                long ledger = ItemLedger(s);
                SaveTo(Slot);
                WorldSimulation.StepMany(60 * 20);
                string cont = BeltNetworkService.Kernel.ComputeStateHash() + "|" + PipeNetworkService.Kernel.Hash() + "|" + CampaignSession.Current.Scrap;
                LoadCopy(Slot, HomeValleyLayout.RegionId);
                Dictionary<string, string> loaded = Snap(null, null, L, pipeReadings: false);
                long ledger2 = ItemLedger(CampaignSession.Current);
                WorldSimulation.StepMany(60 * 20);
                string resumed = BeltNetworkService.Kernel.ComputeStateHash() + "|" + PipeNetworkService.Kernel.Hash() + "|" + CampaignSession.Current.Scrap;
                List<string> d = FgWorldSimSelfCheck.DiffKeys(before, loaded);
                bool ok = d.Count == 0 && ledger == ledger2 && cont == resumed;
                all &= ok;
                lines.Add($"种子 {seed}：往返差异 {d.Count}{Sample(d, 3)}{CombatUnitDiffs(before, loaded)}、总账 {ledger}→{ledger2}、续跑一致 {cont == resumed}");
            }
            Expect(all, "B25 种子矩阵：同一条满载产线在 3 个种子的家园里按地形现找位置都搭得起来，存读档往返逐字段一致、续跑一致（" + string.Join("；", lines) + "）");
        }

        // ── T 暂停与倍速（FG03 第 5 节“3x 速度下的产量”）──────────────────────────────

        private static void CheckSpeedMatrixAndThroughput()
        {
            CampaignState s0 = NewWorld(90921, observe: true, scrap: 260);
            Line3 L = BuildFullLine(s0);
            if (L.Failure != null)
            {
                Fail("T 满载产线搭不起来：" + L.Failure);
                return;
            }
            WorldSimulation.StepMany((int)WarmTicks);
            SaveTo(Slot);
            long origin = GameClock.Ticks;
            List<(long at, string name, Action<CampaignState> act)> script = Script(L);
            const long span = 60 * 90; // 90 游戏秒（含剧本前 90 秒的改线、分流、断网、寒潮、放虚影）
            Dictionary<string, string> reference = null;
            var rows = new List<string>();
            bool same = true;
            bool scaled = true;
            bool paused = true;
            double baseRate = 0;
            foreach (float speed in new[] { 1f, 0.5f, 2f, 3f })
            {
                LoadCopy(Slot, HomeValleyLayout.RegionId);
                var cue = CueCounts();
                var note = NoteCounts();
                // 暂停：真实时间走 120 帧，全状态不变。
                GameClock.SetPaused(true);
                Dictionary<string, string> p0 = Snap(cue, note, L);
                for (int i = 0; i < 120; i++)
                {
                    FrameOnce(1f / 60f);
                }
                paused &= FgWorldSimSelfCheck.DiffKeys(p0, Snap(cue, note, L)).Count == 0;
                GameClock.SetPaused(false);
                long in0 = PortTotal(L.WarehouseInPort);
                long sink0 = PortTotal(PortSinkSplitS) + PortTotal(PortSinkUnder);
                int frames = 0;
                var stops = new SortedSet<long>(script.Select(e => e.at).Where(t => t < span)) { span };
                GameClock.SetSpeed(speed);
                foreach (long stop in stops)
                {
                    while (GameClock.Ticks < origin + stop && frames < 60 * 3000)
                    {
                        FrameOnce(1f / 60f, origin + stop);
                        frames++;
                    }
                    if (stop < span)
                    {
                        foreach ((long at, string _, Action<CampaignState> act) in script)
                        {
                            if (at == stop)
                            {
                                act(CampaignSession.Current);
                            }
                        }
                    }
                }
                GameClock.SetSpeed(1f);
                Dictionary<string, string> snap = Snap(cue, note, L);
                long delivered = PortTotal(L.WarehouseInPort) - in0 + PortTotal(PortSinkSplitS) + PortTotal(PortSinkUnder) - sink0;
                double real = frames / 60.0;
                double perRealSecond = delivered / Math.Max(1e-6, real);
                if (reference == null)
                {
                    reference = snap;
                    baseRate = perRealSecond;
                }
                List<string> d = FgWorldSimSelfCheck.DiffKeys(reference, snap);
                same &= d.Count == 0;
                scaled &= Math.Abs(real * speed - span / 60.0) < 0.1 && Math.Abs(perRealSecond / Math.Max(1e-6, baseRate) - speed) < 0.02 * speed;
                rows.Add($"{speed}x：{frames} 帧 ≈ {real:F1} 真实秒，送达 {delivered} 件 = 每真实秒 {perRealSecond:F2} 件（1x 的 {perRealSecond / Math.Max(1e-6, baseRate):F2} 倍）{(d.Count > 0 ? "，差异" + Sample(d, 3) : string.Empty)}");
            }
            Expect(paused, "T 暂停：真实时间走 120 帧，全状态（传送带 / 流体 / 电网 / 施工 / 机器 / 统计窗口）逐字段不变");
            Expect(same && scaled,
                $"T 倍速矩阵：同一段 90 游戏秒（带剧本事件）在 1x / 0.5x / 2x / 3x 下全状态逐字段一致；真实时间按倍速缩放，每真实秒送达的件数按倍速放大（{string.Join("；", rows)}）");
        }

        private static long PortTotal(int portId) =>
            BeltNetworkService.Kernel != null && BeltNetworkService.Kernel.TryGetPortInfo(portId, out BeltPortInfo p) ? p.Total : 0;

        // ── 通用 ───────────────────────────────────────────────────────────────

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
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail($"{check.Method.Name} 收尾抛异常：{e.Message}");
                }
                GameClock.ResetSession();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                _reader.EndFrame();
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

        private static void Line(string text) => _report?.AppendLine(text);

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame() => _down.Clear();

            public bool GetKey(KeyCode key) => _down.Contains(key) || Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            // 光标在窗口外：不触发镜头的屏幕边缘推屏。
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }
    }
}
