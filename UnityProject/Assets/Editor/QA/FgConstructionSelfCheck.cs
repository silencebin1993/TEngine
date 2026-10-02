using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-02 虚影施工与返还的自动验收（FG03 FGR-LOG-006 / 007；FG04 工作单衔接；FGT-LOG-002 / 003）。
    /// 全部断言走真实入口：真实载入的家园（<see cref="WorldSimulation.LoadHome"/>：机器在战斗内核里真的走去仓库取料、再走到现场施工，
    /// 传送带内核真的在跑）、<see cref="HomeGridService"/> 放置 / 拆除入口、<see cref="HomeValleyBuildMode"/> 的按键与鼠标路径、真 UXML 的施工队列面板、真存档文件。
    /// A 数据：新调参、文本键中英齐全、新动作与通知类型、引导钩子、图鉴条目。
    /// B FGT-LOG-002 正向：库存为 0 放下虚影 → 等待材料（原因写明缺多少、通知、钩子）→ 到货 → 机器去仓库取料（一趟上限）→ 运到现场 → 进度不超过已到材料
    ///   → 材料用完回去再取 → 完工；全程材料守恒（库存 + 现场 + 货舱 = 常数），账本每趟一笔。
    /// C 取消全额退回：已到现场的材料、机器货舱里正在运的材料，取消后全部回到仓库；仓库满时变地面物 + 搬运单。
    /// D 负向“材料被挪用”：机器赶去仓库途中库存被别的任务预留 → 取到多少运多少、施工暂停、显示缺料、不重复扣料；挪用释放后自动继续。
    /// E 负向“施工中被摧毁”：已消耗材料按比例掉落为地面物、虚影保留、进度归零、机器之后搬回；材料守恒。
    /// F 负向“没有劳动力”：施工偏好全部为 0 → 队列 / 悬停写明、发通知；恢复后自动开工。
    /// G FGT-LOG-003：拆除全额返还（投入 + 内部缓存），仓满时返还变地面物、搬运单等空间、腾仓后机器搬回。
    /// H 传送带虚影（DEBT-FG3LOG01-01 / 02）：放下不扣料、格网占住、机器取料后按路径一格一格进内核；部分取消只退多余材料；带物品的传送带拆除时物品一并返还。
    /// I 优先级：队列排序、提高优先级让机器先建它、“优先建造这一片”只改框里的、不自动调整别处（FGR-BASE-020）。
    /// J 机器中断：取料途中机器阵亡（货舱材料落地 + 搬运单、虚影保留）；运料途中规划被挪走（材料退回仓库）。
    /// K 存读档：施工中途（现场材料、施工单腿与进度、机器货舱、传送带规划）真文件往返逐字段一致，读档后照常完工；两次读同一存档结果一致；旧存档“放置即预留”迁移。
    /// L 暂停与 0.5x～3x：暂停中可以放虚影、不推进；各档完工所需游戏时间相同。
    /// M 观察 / 不观察一致（FGR-BASE-021）。
    /// N 正式输入：建造模式里按“优先建造这一片”键拉框、右键退出；施工队列键开关面板；队列面板真 UXML（行、按钮点击、定位、取消、空状态）与布局探针。
    /// O 性能（Editor batchmode）：施工单 Tick、施工队列收集、劳动力统计与施工单数的关系。
    /// P 审查修复回归（第 1 轮）：右键另一个虚影 / 派去远征不删玩家的虚影；暂停 + 仓满连续逐格取消不吞材料；大份返还按趟拆开能搬回；
    ///   仓库存不了的物品不能点选搬运、交付失败不消失；队列先后 = 领单先后；库存不够分时按优先级只派够用的机器；
    ///   存档落在“下一腿”空档读档后照常出发；传送带只在机器身边施工；Alt+B 再按一次关闭队列；虚影画面索引不每帧全扫。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgConstructionSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 3;
        private const int SlotB = 4;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 虚影施工与返还")]
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
            Line("\n[虚影施工与返还] 取料施工、等待材料、取消与拆除全额返还、传送带虚影、优先级与施工队列（FG3-LOG-02）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgconstruct-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "编辑模式下直接起真实家园（机器在战斗内核里真的走路），没有渲染帧——性能数字是 Editor 托管代码，真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFetchAndBuild);
                Step(CheckCancelRefund);
                Step(CheckMaterialsDiverted);
                Step(CheckDestroyedDuringConstruction);
                Step(CheckNoLabor);
                Step(CheckDemolishFullRefund);
                Step(CheckBeltGhosts);
                Step(CheckPriority);
                Step(CheckMachineInterruptions);
                Step(CheckSaveLoad);
                Step(CheckLegacyMigration);
                Step(CheckTiming);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckFormalInputAndQueuePanel);
                Step(CheckReviewRegressions);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"虚影施工自检抛异常：{e}");
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
                ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [虚影施工与返还] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>真实载入家园（战斗内核、传送带内核、机器句柄都在）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = false, int scrap = 0)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgconstruct-" + seed, "Standard", seed);
            CampaignSession.Set(0, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        /// <summary>只有战役状态的家园（不载入地点，服务层断言用），带一台搬运机。</summary>
        private static CampaignState NewHome(int seed, int haulers = 1)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgconstructh-" + seed, "Standard", seed);
            System.Reflection.MethodInfo seedMethod = typeof(HomeValleyController).GetMethod("EnsureRegionSeeded",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            seedMethod.Invoke(null, new object[] { s });
            s.Scrap = 0;
            for (int i = 0; i < haulers; i++)
            {
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                    new Vector2(-10f - i, -6f), 100f, 100f);
            }
            CampaignSession.Set(0, s);
            return s;
        }

        private static bool StepUntil(Func<bool> done, int maxGameSeconds, Action perQuarter = null)
        {
            for (int i = 0; i < maxGameSeconds * 4; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 4));
                perQuarter?.Invoke();
            }
            return done();
        }

        /// <summary>服务层驱动：机器瞬时到达（到达回调立即触发），按 dt 推进工单。</summary>
        private static void TickOrders(CampaignState s, float seconds, float dt = 0.1f)
        {
            int steps = Mathf.CeilToInt(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
                HomeValleyWorkOrders.Tick(s, dt, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
            }
        }

        private static GridCell? FindValid(CampaignState s, string typeId, GridCell from, int radius, int rotation = 0)
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
                        if (HomeGridService.ValidatePlacement(s, typeId, c, rotation, checkCost: false).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        private static GridCell? FindBeltRow(CampaignState s, GridCell from, int length, int radius)
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
                        var start = new GridCell(from.X + dx, from.Y + dy);
                        bool ok = true;
                        for (int k = 0; k < length && ok; k++)
                        {
                            ok = HomeGridService.ValidateBeltCell(s, new GridCell(start.X + k, start.Y)).Ok
                                 && HomeGridService.ValidateBeltCell(s, new GridCell(start.X + k, start.Y + 1)).Ok;
                        }
                        if (ok)
                        {
                            return start;
                        }
                    }
                }
            }
            return null;
        }

        private static GridCell GenSpot(CampaignState s, int dx = -8, int dy = 26)
        {
            GridCell core = HomeGridService.CorePivot(s);
            return FindValid(s, "generator_2", new GridCell(core.X + dx, core.Y + dy), 14) ?? new GridCell(core.X + dx, core.Y + dy);
        }

        private static WorkOrderRecord BuildOrder(CampaignState s, string targetId) => HomeValleyWorkOrders.FindActiveBuild(s, targetId);

        private static int CargoTotal() => MachineRegistry.AllRecords.Where(m => m != null).Sum(HomeValleyConstruction.CargoScrap);

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        private static int SiteDelivered(CampaignState s) =>
            (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(b => b != null && HomeValleyController.IsPlannedGhost(b)).Sum(b => b.ConstructionDelivered)
            + (s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Sum(p => p.Delivered);

        private static int FetchTxCount(CampaignState s, string orderId) =>
            (s.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>()).Count(t => t != null && t.TransactionId.StartsWith(orderId + ":fetch:", StringComparison.Ordinal));

        private static int NotificationCount(string typeId) => NotificationCenter.History.Count(e => e.Type != null && e.Type.Id == typeId);

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.TuningInt("build.carry_per_trip") == 40 && Mathf.Approximately(GridContent.Tuning("build.belt_seconds_per_cell"), 1f);
            Expect(tuning, $"新调参已入表：每趟取料 {GridContent.TuningInt("build.carry_per_trip")}、传送带每格施工 {GridContent.Tuning("build.belt_seconds_per_cell")} 秒");
            string[] keys =
            {
                "build.material.scrap", "grid.warn.short_materials", "build.status.queued", "build.status.fetching", "build.status.delivering", "build.status.building",
                "build.status.waiting_materials", "build.status.no_labor", "build.status.path_blocked", "build.priority.low", "build.priority.normal", "build.priority.high",
                "build.priority.top", "build.hover.title", "build.hover.materials", "build.hover.priority", "build.queue.title", "build.queue.empty", "build.queue.hint",
                "ui.build.prioritize_mode", "ui.build.prioritize_done", "ui.build.btn_queue", "ui.build.belts_planned", "ui.build.belts_removed_full",
                "build.return.grounded", "build.site.destroyed", "build.notify.waiting", "codex.build.construction.title", "codex.build.construction.body",
            };
            var missing = new List<string>();
            foreach (string k in keys)
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    string v = GameText.Get(k);
                    if (string.IsNullOrEmpty(v) || GameText.ContainsMarker(v))
                    {
                        missing.Add($"{k}[{lang}]");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(missing.Count == 0, $"{keys.Length} 个新文本键中英齐全（缺：{string.Join(",", missing)}）");
            bool actions = InputDisplay.ForAction(GameActionId.ConstructionQueue).Contains("B") && InputDisplay.ForAction(GameActionId.PrioritizeArea).Contains("P");
            Expect(actions, $"新动作已接入且可重绑：施工队列 {InputDisplay.ForAction(GameActionId.ConstructionQueue)}、优先建造这一片 {InputDisplay.ForAction(GameActionId.PrioritizeArea)}");
            bool notify = NotificationCatalog.TryGetType("construction_waiting", out NotifyTypeDef w) && w.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("construction_no_labor", out NotifyTypeDef n) && n.Tier == NotifyLevel.Warning;
            Expect(notify, "两类新通知（施工等材料 / 没有劳动力）登记为警告级，可聚合、可定位");
            string[] hooks = { GuidanceHooks.BuildFirstGhost, GuidanceHooks.BuildFirstWaitingMaterials, GuidanceHooks.BuildFirstNoLabor, GuidanceHooks.BuildQueueFirstOpen, GuidanceHooks.BuildFirstPrioritize };
            Expect(hooks.All(h => GuidanceHooks.Known.Contains(h)), "五个新引导钩子已登记（引导内容在 FG15-UX-04）");
            GameConfig.fg.CodexEntry entry = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.build.construction");
            Expect(entry != null && entry.Hooks.Contains(GuidanceHooks.BuildFirstGhost) && entry.Links.Contains("codex.build.grid"),
                "图鉴有“虚影施工与返还”条目：第一次放虚影 / 等材料 / 打开施工队列时解锁，链接到格网建造");
        }

        // ── B. FGT-LOG-002 正向 ─────────────────────────────────────────────────────

        private static void CheckFetchAndBuild()
        {
            NotificationCenter.ResetForTests();
            CampaignState s = NewWorld(5101, scrap: 0);
            GridCell spot = GenSpot(s);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", spot, 0);
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            bool placed = r.Success && ghost != null && ghost.ConstructionState == BuildingConstructionState.Planned && ghost.ConstructionRequired == 60
                          && ghost.ConstructionDelivered == 0 && s.Scrap == 0 && order != null && order.Leg == 1
                          && r.Placement.Warnings.Any(x => x.Contains("还差 60")) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstGhost);
            Expect(placed, $"库存为 0 也能放发电机虚影（FGR-LOG-003）：所需 60、已到 0、库存不变，施工单第一腿去仓库取料；预览只警告“{r.Placement?.Warnings.FirstOrDefault()}”");

            bool waiting = StepUntil(() => order.State == WorkOrderState.Waiting, 20);
            string status = HomeValleyConstruction.DescribeStatus(s, order);
            Expect(waiting && order.AssignedMachineLogicId == 0 && status.Contains("等待材料") && status.Contains("×60") && status.Contains("库存 0")
                   && NotificationCount("construction_waiting") == 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstWaitingMaterials),
                $"没有货：施工单等待材料、不占机器，状态写“{status}”，发一条可定位的警告通知与首次钩子");
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Expect(order.State == WorkOrderState.Waiting && NotificationCount("construction_waiting") == 1 && ghost.ConstructionDelivered == 0,
                "继续等 10 秒：仍在等待、没有空跑、不重复发通知");

            // 到货：机器去仓库取料、运到现场、进度不超过已到材料、用完回去再取、完工。
            s.Scrap = 100;
            int total = 100;
            bool conserved = true;
            bool sawFetchLeg = false;
            bool sawCargo = false;
            bool sawCappedAt40 = false;
            float maxFraction = 0f;
            var storageLeg = new List<Vector2>();
            bool done = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 420, () =>
            {
                int inGhost = HomeValleyController.IsPlannedGhost(ghost) ? ghost.ConstructionDelivered : 0;
                int invested = ghost.ConstructionState == BuildingConstructionState.Operational ? ghost.InvestedScrap : 0;
                conserved &= s.Scrap + inGhost + CargoTotal() + invested == total;
                if (order.Leg == 1 && order.State == WorkOrderState.Reserved && order.AssignedMachineLogicId > 0)
                {
                    sawFetchLeg = true;
                    storageLeg.Add(HomeValleyWorkOrders.ResolveWorkPosition(s, order));
                }
                sawCargo |= CargoTotal() > 0;
                if (order.State == WorkOrderState.InProgress && ghost.ConstructionDelivered == 40)
                {
                    float cap = order.Duration * 40f / 60f;
                    sawCappedAt40 |= order.Progress <= cap + 1e-3f;
                }
                maxFraction = Mathf.Max(maxFraction, HomeValleyConstruction.Fraction(s, order));
            });
            Vector2 storage = HomeValleyConstruction.StoragePosition(s, ghost.Position);
            int trips = FetchTxCount(s, order.WorkOrderId);
            Expect(done && s.Scrap == 40 && ghost.InvestedScrap == 60 && ghost.ConstructionRequired == 0 && ghost.ConstructionDelivered == 0
                   && order.State == WorkOrderState.Completed && trips == 2 && conserved,
                $"有货后自动继续：机器取料两趟（一趟上限 40：40 + 20，账本 {trips} 笔）、完工，库存 100→{s.Scrap}，投入 60；全程库存 + 现场 + 货舱守恒");
            Expect(sawFetchLeg && storageLeg.All(p => (p - storage).sqrMagnitude < 1e-3f) && sawCargo && sawCappedAt40 && maxFraction >= 0.999f,
                "机器先走到离现场最近的仓库取料（取料腿目的地 = 仓库）、货舱真的装着材料运到现场；只到 40 时施工进度停在 40/60 处，回去再取后才继续");
        }

        // ── C. 取消全额退回 ───────────────────────────────────────────────────────

        private static void CheckCancelRefund()
        {
            // FG4-ECO-10：这一段逐件核对“库存 0 时”的取料 / 退回守恒，关掉核心应急产废料（库存为 0 且没有回收站时每分钟 1 件，由 FgSoftlockSelfCheck 覆盖）。
            GameLogic.Campaign.Economy.SoftlockService.CoreScrapSuppressedForTests = true;
            try
            {
                CheckCancelRefundCore();
            }
            finally
            {
                GameLogic.Campaign.Economy.SoftlockService.CoreScrapSuppressedForTests = false;
            }
        }

        private static void CheckCancelRefundCore()
        {
            // 已到现场的材料：机器运来 30、施工推进到 30/60 处等材料 → 取消 → 30 回到仓库。
            CampaignState s = NewWorld(5201, scrap: 30);
            GridCell spot = GenSpot(s);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", spot, 0);
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            bool stalled = StepUntil(() => order.State == WorkOrderState.Waiting && ghost.ConstructionDelivered == 30, 240);
            float progress = order.Progress;
            Expect(stalled && s.Scrap == 0 && ghost.ConstructionState == BuildingConstructionState.Building && Mathf.Abs(progress - order.Duration * 0.5f) < 0.05f,
                $"库存只有 30：机器运来 30、施工进度停在一半（{progress:F2}/{order.Duration} 秒）、转为等待材料");
            GridOpResult cancel = HomeGridService.TryToggleDemolish(s, r.BuildingId);
            Expect(cancel.Outcome == GridOpResult.Kind.PlanCancelled && HomeGridService.FindBuilding(s, r.BuildingId) == null && s.Scrap == 30
                   && order.State == WorkOrderState.Cancelled && HomeGridService.BuildingAt(s, spot) == null,
                $"取消施工中的虚影：已到现场的 30 全额退回仓库（库存 0→{s.Scrap}），记录与占格移除（FGR-LOG-006）");

            // 机器货舱里正在运的材料：取消 → 一起退回。
            GridOpResult r2 = HomeGridService.TryPlace(s, "generator_2", spot, 0);
            WorkOrderRecord order2 = BuildOrder(s, r2.BuildingId);
            bool carrying = StepUntil(() => order2.State == WorkOrderState.Reserved && order2.Leg == 0 && CargoTotal() == 30, 240);
            int machine = order2.AssignedMachineLogicId;
            HomeGridService.TryToggleDemolish(s, r2.BuildingId);
            Expect(carrying && s.Scrap == 30 && CargoTotal() == 0 && order2.State == WorkOrderState.Cancelled
                   && HomeValleyWorkOrders.FindActiveOrderForMachine(s, machine) == null,
                "机器扛着 30 往现场走时取消：货舱里的材料全额退回仓库，机器空出来");

            // 仓库满时退回：放不下的变成地面物 + 搬运单（不消失），腾仓后机器搬回。
            GridOpResult r3 = HomeGridService.TryPlace(s, "generator_2", spot, 0);
            WorkOrderRecord order3 = BuildOrder(s, r3.BuildingId);
            BuildingRecord ghost3 = HomeGridService.FindBuilding(s, r3.BuildingId);
            StepUntil(() => order3.State == WorkOrderState.Waiting && ghost3.ConstructionDelivered == 30, 240);
            int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            s.Scrap = cap - 10;
            HomeGridService.TryToggleDemolish(s, r3.BuildingId);
            WorkOrderRecord haul = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return");
            Expect(s.Scrap == cap && GroundScrap(s) == 20 && haul != null && HomeValleyConstruction.LastReturnedStored == 10 && HomeValleyConstruction.LastReturnedGrounded == 20,
                $"仓库只剩 10 空位时取消：10 入库、20 变成地面物并生成搬运单（{haul?.State}），不会消失");
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool heldWaiting = haul != null && haul.State == WorkOrderState.Waiting && haul.FailureReason == HomeValleyConstruction.ReturnWaitReason
                               && haul.AssignedMachineLogicId == 0 && GroundScrap(s) == 20;
            s.Scrap = cap - 100;
            bool hauled = StepUntil(() => GroundScrap(s) == 0 && haul.State == WorkOrderState.Completed, 240);
            Expect(heldWaiting && hauled && s.Scrap == cap - 80, $"仓满时搬运单等待、不占机器；腾出空间后机器把地上的 20 搬回仓库（库存 {cap - 100}→{s.Scrap}）");
        }

        // ── D. 负向：材料被挪用 ──────────────────────────────────────────────────────

        private static void CheckMaterialsDiverted()
        {
            CampaignState s = NewWorld(5301, scrap: 60);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            bool enRoute = StepUntil(() => order.State == WorkOrderState.Reserved && order.Leg == 1 && order.AssignedMachineLogicId > 0, 30);
            // 机器赶去仓库的途中，别的任务（装配站排产 / 维修的资源预留，同一个账本入口）先用掉了 55。
            const string divert = "fgconstruct:divert";
            CampaignEconomyLedger.ProposeConsume(s, divert, "home_valley:assembly_station", CampaignEconomyLedger.ResourceScrap, 55);
            bool reserved = CampaignEconomyLedger.Reserve(s, divert).Success && s.Scrap == 5;
            bool waiting = StepUntil(() => order.State == WorkOrderState.Waiting, 240);
            string status = HomeValleyConstruction.DescribeStatus(s, order);
            float cap = order.Duration * 5f / 60f;
            Expect(enRoute && reserved && waiting && ghost.ConstructionDelivered == 5 && s.Scrap == 0 && Mathf.Abs(order.Progress - cap) < 0.05f
                   && status.Contains("×55") && FetchTxCount(s, order.WorkOrderId) == 1,
                $"取料途中库存被别的任务用掉 55：机器只取到剩下的 5、运到现场，施工停在 {order.Progress:F2} 秒（5/60），显示“{status}”；只记一笔 5 的取料，不重复扣");
            int ledgerConsumed = (s.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
                .Where(t => t.TransactionId.StartsWith(order.WorkOrderId, StringComparison.Ordinal)).Sum(t => Mathf.RoundToInt(t.Consumed));
            Expect(ledgerConsumed == 5 && ghost.ConstructionDelivered + 55 + s.Scrap == 60, $"账目：施工只消费了 5（账本 {ledgerConsumed}），5 + 被挪用 55 + 库存 0 = 60");
            CampaignEconomyLedger.Cancel(s, divert); // 挪用方取消 → 55 回到库存
            bool done = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 300);
            Expect(done && s.Scrap == 0 && ghost.InvestedScrap == 60, "挪用的 55 退回库存后施工自动继续并完工，总共只用 60");
        }

        // ── E. 负向：施工中被摧毁 ──────────────────────────────────────────────────

        private static void CheckDestroyedDuringConstruction()
        {
            CampaignState s = NewWorld(5401, scrap: 100);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            bool midway = StepUntil(() => order.State == WorkOrderState.InProgress && order.Progress >= 10f, 300);
            float progress = order.Progress;
            int delivered = ghost.ConstructionDelivered;
            int expectDrop = Mathf.FloorToInt(60 * Mathf.Clamp01(progress / order.Duration));
            int ground0 = GroundScrap(s);
            bool destroyed = HomeValleyConstruction.OnSiteDestroyed(s, r.BuildingId);
            WorkOrderRecord haul = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return");
            Expect(midway && destroyed && GroundScrap(s) - ground0 == expectDrop && HomeValleyConstruction.LastDestroyedDropped == expectDrop
                   && ghost.ConstructionDelivered == delivered - expectDrop && HomeGridService.FindBuilding(s, r.BuildingId) != null
                   && order.Progress == 0f && order.State == WorkOrderState.Ready && haul != null && CargoTotal() == 0,
                $"施工到 {progress:F1} 秒时被摧毁：已消耗的 {expectDrop} 按比例掉在地上（生成搬运单），没消耗的 {ghost.ConstructionDelivered} 留在现场，虚影保留、进度归零、施工单回到待分配池");
            Expect(s.Scrap + ghost.ConstructionDelivered + GroundScrap(s) + CargoTotal() == 100, "材料守恒：库存 + 现场 + 地面 + 货舱 = 100（不凭空消失、不复制）");
            bool rebuilt = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational && GroundScrap(s) == 0, 400);
            Expect(rebuilt && s.Scrap == 40 && ghost.InvestedScrap == 60, $"机器重新施工并完工，地上的材料也被搬回：最终库存 {s.Scrap}（= 100 - 60）");
            Expect(!HomeValleyConstruction.OnSiteDestroyed(s, r.BuildingId), "已建成的建筑不走“施工中被摧毁”（返回 false，不掉料）");
        }

        // ── F. 负向：没有劳动力 ────────────────────────────────────────────────────

        private static void CheckNoLabor()
        {
            NotificationCenter.ResetForTests();
            CampaignState s = NewWorld(5501, scrap: 100);
            List<MachineRecord> machines = HomeMachines();
            foreach (MachineRecord m in machines)
            {
                MachineRegistry.TrySetWorkPriority(m.LogicId, WorkOrderKind.Build, 0); // 玩家把所有机器的施工偏好关掉（等同全部派去远征）
            }
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            string status = HomeValleyConstruction.DescribeStatus(s, order);
            Expect(machines.Count > 0 && HomeValleyConstruction.NoLabor && HomeValleyConstruction.LaborCount == 0 && order.State == WorkOrderState.Ready
                   && s.Scrap == 100 && status.Contains("没有能施工的机器") && NotificationCount("construction_no_labor") == 1
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstNoLabor),
                $"家园没有能施工的机器（{machines.Count} 台施工偏好都是 0）：施工单排着不动、库存不动，状态“{status}”，发一条警告通知（FG04 负向）");
            foreach (MachineRecord m in machines)
            {
                MachineRegistry.TrySetWorkPriority(m.LogicId, WorkOrderKind.Build, 3);
            }
            HomeValleyWorkOrders.MarkAssignmentDirty();
            bool done = StepUntil(() => HomeGridService.FindBuilding(s, r.BuildingId).ConstructionState == BuildingConstructionState.Operational, 400);
            Expect(done && !HomeValleyConstruction.NoLabor && s.Scrap == 40, "恢复施工偏好后自动开工并完工（不需要重新下令）");
        }

        // ── G. FGT-LOG-003 拆除全额返还 ───────────────────────────────────────────────

        private static void CheckDemolishFullRefund()
        {
            CampaignState s = NewWorld(5601, scrap: 100);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            BuildingRecord gen = HomeGridService.FindBuilding(s, r.BuildingId);
            StepUntil(() => gen.ConstructionState == BuildingConstructionState.Operational, 400);
            gen.Inventory = new[]
            {
                new CargoEntry { ResourceType = CampaignEconomyLedger.ResourceScrap, Amount = 7 },
                new CargoEntry { ResourceType = HomeGridService.BeltItemResource(3), Amount = 2 },
            };
            int scrap0 = s.Scrap;
            HomeGridService.TryToggleDemolish(s, r.BuildingId);
            bool gone = StepUntil(() => HomeGridService.FindBuilding(s, r.BuildingId) == null, 240);
            int items = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == HomeGridService.BeltItemResource(3)).Sum(g => g.Amount);
            Expect(gone && s.Scrap == scrap0 + 60 + 7 && HomeValleyWorkOrders.LastDemolishRefund == 60 && HomeValleyWorkOrders.LastDemolishCacheReturned == 9 && items == 2,
                $"拆除已建成的发电机：投入的 60 全额返还 + 内部缓存的 7 废料入库（{scrap0}→{s.Scrap}），缓存里仓库存不了的 2 件物品落在原地（FGR-LOG-007）");

            // 仓满：返还放不下的变成地面物 + 搬运单，腾仓后机器搬回。
            GridOpResult r2 = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            BuildingRecord gen2 = HomeGridService.FindBuilding(s, r2.BuildingId);
            s.Scrap = 100;
            StepUntil(() => gen2.ConstructionState == BuildingConstructionState.Operational, 400);
            int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            s.Scrap = cap - 5;
            HomeGridService.TryToggleDemolish(s, r2.BuildingId);
            int ground0 = GroundScrap(s);
            bool gone2 = StepUntil(() => HomeGridService.FindBuilding(s, r2.BuildingId) == null, 240);
            WorkOrderRecord haul = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return" && HomeValleyWorkOrders.IsActive(o));
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool held = haul != null && haul.AssignedMachineLogicId == 0 && GroundScrap(s) - ground0 == 55 && CargoTotal() == 0;
            Expect(gone2 && held && s.Scrap == cap && haul != null,
                $"仓库只剩 5 空位时拆除：5 入库、55 变成地面物并生成搬运单（{haul?.State}：{HomeValleyConstruction.ReturnWaitReason}）");
            s.Scrap = 0;
            bool back = StepUntil(() => GroundScrap(s) == ground0 && s.Scrap == 55, 300);
            back &= haul.AssignedMachineLogicId == 0 || haul.State == WorkOrderState.Completed;
            Expect(back && s.Scrap == 55, $"腾出仓位后机器把 55 搬回仓库（0→{s.Scrap}）");
        }

        // ── H. 传送带虚影 ────────────────────────────────────────────────────────

        private static void CheckBeltGhosts()
        {
            CampaignState s = NewWorld(5701, scrap: 0);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? row = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 26), 8, 14);
            if (row == null || !BeltNetworkService.IsRunning)
            {
                Fail("找不到能铺 8 格的空地或传送带内核没运行");
                return;
            }
            GridCell a = row.Value;
            List<GridCell> cells = Enumerable.Range(0, 6).Select(k => new GridCell(a.X + k, a.Y)).ToList();
            int kernel0 = BeltNetworkService.Kernel.CellCount;
            GridOpResult r = HomeGridService.TryPlaceBeltPath(s, "belt_t1", cells[0], cells[5], BeltDir.East);
            PlannedBeltRecord plan = s.Grid.PlannedBelts?.LastOrDefault();
            WorkOrderRecord order = plan != null ? BuildOrder(s, HomeValleyConstruction.BeltPlanPrefix + plan.PlanId) : null;
            HomeGridMap map = HomeGridService.MapFor(s);
            bool blocksBuilding = !HomeGridService.ValidateBeltCell(s, cells[2]).Ok && HomeGridService.ValidatePlacement(s, "signal_relay", cells[2], 0, checkCost: false).Has(GridBlockReason.OccupiedBelt);
            Expect(r.Outcome == GridOpResult.Kind.BeltsPlaced && plan != null && order != null && BeltNetworkService.Kernel.CellCount == kernel0 && s.Scrap == 0
                   && cells.All(c => HomeValleyConstruction.IsPlannedMarker(map.GetBelt(c))) && blocksBuilding && order.Leg == 1,
                "库存为 0 拖出 6 格传送带：放下的是虚影（规划 + 施工单），不扣材料、内核不变；虚影格占住格网，别的传送带 / 建筑放不上去（DEBT-FG3LOG01-01）");
            bool waiting = StepUntil(() => order.State == WorkOrderState.Waiting, 20);
            Expect(waiting && HomeValleyConstruction.DescribeStatus(s, order).Contains("×6") && HomeValleyWorkOrders.DescribeTarget(s, order).Contains("6 格"),
                $"传送带虚影同样等待材料：“{HomeValleyConstruction.DescribeStatus(s, order)}”，队列名“{HomeValleyWorkOrders.DescribeTarget(s, order)}”");

            s.Scrap = 6;
            var counts = new HashSet<int>();
            bool built = StepUntil(() => cells.All(c => BeltNetworkService.Kernel.HasCell(c.X, c.Y)), 300,
                () => counts.Add(BeltNetworkService.Kernel.CellCount - kernel0));
            bool inOrder = counts.Contains(1) && counts.Contains(3) && counts.Contains(5);
            Expect(built && inOrder && s.Scrap == 0 && order.State == WorkOrderState.Completed && (s.Grid.PlannedBelts?.Length ?? 0) == 0
                   && cells.All(c => !HomeValleyConstruction.IsPlannedMarker(map.GetBelt(c)) && map.GetBelt(c) != 0)
                   && BeltNetworkService.Kernel.TryGetCellInfo(cells[0].X, cells[0].Y, out BeltCellInfo info) && info.Dir == BeltDir.East,
                $"有货后机器取料施工：6 格按路径一格一格建成进内核（途中见到 {string.Join("/", counts.OrderBy(x => x))} 格），扣 6 废料，规划移除、格网层换成真传送带、方向朝东");

            // 部分取消：规划 6 格、库存 6，建了 2 格后取消剩下 4 格 → 已到现场、还没用掉的材料全额退回，建成的 2 格留着。
            List<GridCell> row2 = Enumerable.Range(0, 6).Select(k => new GridCell(a.X + k, a.Y + 1)).ToList();
            s.Scrap = 6;
            HomeGridService.TryPlaceBeltPath(s, "belt_t1", row2[0], row2[5], BeltDir.East);
            PlannedBeltRecord plan2 = s.Grid.PlannedBelts.Last();
            WorkOrderRecord order2 = BuildOrder(s, HomeValleyConstruction.BeltPlanPrefix + plan2.PlanId);
            bool two = StepUntil(() => HomeValleyConstruction.BuiltCells(plan2) >= 2, 300);
            int builtNow = HomeValleyConstruction.BuiltCells(plan2);
            int onSite = plan2.Delivered + CargoTotal();
            int scrapNow = s.Scrap;
            GridOpResult removed = HomeGridService.TryRemoveBelts(s, row2.Skip(builtNow).ToList());
            Expect(two && removed.Outcome == GridOpResult.Kind.BeltsRemoved && HomeGridService.LastPlannedBeltsCancelled == 6 - builtNow
                   && order2.State == WorkOrderState.Cancelled && s.Scrap == scrapNow + onSite
                   && row2.Take(builtNow).All(c => BeltNetworkService.Kernel.HasCell(c.X, c.Y)) && row2.Skip(builtNow).All(c => map.GetBelt(c) == 0)
                   && s.Scrap + builtNow == 6,
                $"建了 {builtNow} 格后拆掉剩下的虚影格：取消 {6 - builtNow} 格规划、施工单作废，现场和货舱里没用掉的 {onSite} 全额退回（{scrapNow}→{s.Scrap}），建成的格子留着");

            // 拆除模式框选传送带虚影：算作这次框选处理掉的传送带（状态行写明格数，不是“这里没有建筑”）。
            List<GridCell> row3 = row2.Skip(builtNow).Take(2).ToList(); // 刚取消的格子，已经空出来
            GridOpResult plan3 = HomeGridService.TryPlaceBeltPath(s, "belt_t1", row3[0], row3[1], BeltDir.East);
            DemolishBoxPlan box = HomeGridService.PlanDemolishBox(s, row3[0], row3[1]);
            GridOpResult boxed = HomeGridService.ExecuteDemolishBox(s, box);
            Expect(plan3.Success && box.Belts.Count == 2 && boxed.Outcome == GridOpResult.Kind.BatchDemolished && HomeGridService.LastBatchBelts == 2
                   && row3.All(c => HomeValleyConstruction.TryFindPlannedCell(s, c, out _, out _) == false && map.GetBelt(c) == 0),
                "拆除模式框选 2 格传送带虚影：取消规划并计入“框选处理掉的传送带”（2 格）");

            // 带物品的传送带：物品一并返还（DEBT-FG3LOG01-02）。
            GridCell busy = cells[3];
            BeltNetworkService.Kernel.InsertItem(busy.X, busy.Y, 2);
            BeltNetworkService.Kernel.InsertItem(cells[4].X, cells[4].Y, 2);
            int itemsOnBelt = cells.Sum(c => BeltNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out BeltCellInfo ci) ? ci.Count : 0);
            int scrapB = s.Scrap;
            GridOpResult all = HomeGridService.TryRemoveBelts(s, cells);
            int itemGround = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == HomeGridService.BeltItemResource(2)).Sum(g => g.Amount);
            Expect(all.Outcome == GridOpResult.Kind.BeltsRemoved && itemsOnBelt == 2 && itemGround == 2 && HomeGridService.LastBeltItemsReturned == 2 && s.Scrap == scrapB + 6
                   && cells.All(c => !BeltNetworkService.Kernel.HasCell(c.X, c.Y)),
                $"拆掉带着 {itemsOnBelt} 件物品的 6 格传送带：造价 6 全额返还入库，带上的物品按种类落在拆除处（{itemGround} 件，不消失）");
        }

        // ── I. 优先级 ─────────────────────────────────────────────────────────────

        private static void CheckPriority()
        {
            CampaignState s = NewHome(5801);
            s.Scrap = 500;
            List<MachineRecord> machines = HomeMachines();
            foreach (MachineRecord m in machines.Skip(1))
            {
                MachineRegistry.TrySetWorkPriority(m.LogicId, WorkOrderKind.Build, 0); // 只留一台能施工，看它先领哪一张
            }
            GridCell spotA = GenSpot(s, -10, 26);
            GridOpResult a = HomeGridService.TryPlace(s, "generator_2", spotA, 0);
            GridCell spotB = FindValid(s, "generator_2", new GridCell(spotA.X + 10, spotA.Y), 12) ?? spotA;
            GridOpResult b = HomeGridService.TryPlace(s, "generator_2", spotB, 0);
            WorkOrderRecord oa = BuildOrder(s, a.BuildingId);
            WorkOrderRecord ob = BuildOrder(s, b.BuildingId);
            var queue = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, queue);
            bool defaultOrder = queue.Count == 2 && queue[0].Order == oa && queue[1].Order == ob;
            bool raised = HomeValleyConstruction.SetPriority(s, ob.WorkOrderId, 1);
            HomeValleyConstruction.CollectQueue(s, queue);
            bool reordered = queue[0].Order == ob && ob.Priority == 1 && oa.Priority == 0;
            TickOrders(s, 0.2f);
            Expect(defaultOrder && raised && reordered && ob.AssignedMachineLogicId > 0 && oa.AssignedMachineLogicId == 0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstPrioritize),
                "施工队列默认按先放先建；把后放的 B 提到“高”：队列里排到前面，唯一的机器先去建 B、A 等着（ERD-WRK-002：偏好 → 优先级 → 先后）");
            Expect(!HomeValleyConstruction.SetPriority(s, ob.WorkOrderId, 99) || ob.Priority == HomeValleyConstruction.PriorityMax, "优先级有上下限（低 / 普通 / 高 / 最高）");

            // “优先建造这一片”：只改框里的，框外不动、系统也不会自动调整别的。
            GridOpResult c = HomeGridService.TryPlace(s, "generator_2", FindValid(s, "generator_2", new GridCell(spotA.X, spotA.Y + 12), 12) ?? spotA, 0);
            WorkOrderRecord oc = BuildOrder(s, c.BuildingId);
            int changed = HomeValleyConstruction.PrioritizeArea(s, new GridCell(spotA.X - 1, spotA.Y - 1), new GridCell(spotA.X + 1, spotA.Y + 1));
            Expect(changed == 1 && oa.Priority == HomeValleyConstruction.PriorityMax && oc.Priority == 0 && HomeValleyConstruction.CountSitesInBox(s, spotA, spotA) == 1,
                "“优先建造这一片”框住 A：只有 A 改成最高，框外的 C 仍是普通（不替玩家调整别处，FGR-BASE-020）");
            Expect(HomeValleyConstruction.PrioritizeArea(s, spotA, spotA) == 0, "再框一次已是最高的 A：没有可改的（状态行写明）");
        }

        // ── J. 机器中断 ──────────────────────────────────────────────────────────

        private static void CheckMachineInterruptions()
        {
            // 取料后机器阵亡：货舱的材料落在它倒下的地方（生成搬运单），虚影与施工单保留（回到待分配池）。
            CampaignState s = NewHome(5901, haulers: 2);
            s.Scrap = 100;
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId)); // 分配 + 瞬时到仓库取料
            int machine = order.AssignedMachineLogicId;
            bool carrying = order.State == WorkOrderState.Reserved && order.Leg == 0 && CargoTotal() == 40 && s.Scrap == 60;
            MachineRegistry.MarkDeadByLogicId(machine);
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, null); // 只结算阵亡，不在同一步重新派工
            WorkOrderRecord haul = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return");
            Expect(carrying && order.State == WorkOrderState.Ready && order.Leg == 1 && GroundScrap(s) == 40 && haul != null && HomeGridService.FindBuilding(s, r.BuildingId) != null
                   && s.Scrap + GroundScrap(s) == 100,
                "扛着 40 往现场走的机器阵亡：40 落在原地（生成搬运单），虚影保留，施工单回到待分配池重新取料（不作废玩家的规划）");
            TickOrders(s, 60f);
            Expect(HomeGridService.FindBuilding(s, r.BuildingId).ConstructionState == BuildingConstructionState.Operational && s.Scrap + GroundScrap(s) == 40,
                "另一台机器接着取料施工并完工，地上的 40 也被搬回（总共只用 60）");

            // 运料途中规划被挪走：货舱材料退回仓库，按新位置重新取料。
            CampaignState s2 = NewHome(5902);
            s2.Scrap = 100;
            GridCell from = GenSpot(s2);
            GridOpResult p = HomeGridService.TryPlace(s2, "generator_2", from, 0);
            WorkOrderRecord po = BuildOrder(s2, p.BuildingId);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s2, 0.1f, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s2, o.WorkOrderId));
            bool carrying2 = po.Leg == 0 && CargoTotal() == 40 && s2.Scrap == 60;
            GridCell to = FindValid(s2, "generator_2", new GridCell(from.X + 12, from.Y), 12) ?? from;
            GridOpResult moved = HomeGridService.TryRelocate(s2, p.BuildingId, to, 0);
            Expect(carrying2 && moved.Outcome == GridOpResult.Kind.PlanMoved && s2.Scrap == 100 && CargoTotal() == 0 && po.State == WorkOrderState.Ready && po.Leg == 1,
                "机器扛着 40 往旧位置走时把规划挪走：40 退回仓库、施工单回到待分配池、按新位置重新取料");
        }

        // ── K. 存读档与旧档迁移 ─────────────────────────────────────────────────────

        private static string ConstructionJson(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (BuildingRecord b in (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).OrderBy(x => x.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(b.BuildingId).Append('|').Append(b.ConstructionState).Append('|').Append(b.ConstructionRequired).Append('|').Append(b.ConstructionDelivered)
                    .Append('|').Append(b.InvestedScrap).Append('\n');
            }
            foreach (WorkOrderRecord o in (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o.Kind == WorkOrderKind.Build).OrderBy(o => o.WorkOrderId, StringComparer.Ordinal))
            {
                sb.Append(o.WorkOrderId).Append('|').Append(o.State).Append('|').Append(o.Leg).Append('|').Append(o.FetchCount).Append('|')
                    .Append(o.Progress.ToString("F3", CultureInfo.InvariantCulture)).Append('|').Append(o.Priority).Append('\n');
            }
            foreach (PlannedBeltRecord p in s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                sb.Append(p.PlanId).Append('|').Append(p.Delivered).Append('|').Append(string.Join(",", p.CellState)).Append('|').Append(string.Join(",", p.Dirs)).Append('\n');
            }
            sb.Append("scrap=").Append(s.Scrap).Append(" ground=").Append(GroundScrap(s));
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(6001, scrap: 50);
            GridOpResult g = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell a = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 30), 5, 14) ?? default;
            HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, new GridCell(a.X + 4, a.Y), BeltDir.East);
            WorkOrderRecord go = BuildOrder(s, g.BuildingId);
            HomeValleyConstruction.SetPriority(s, go.WorkOrderId, 1);
            bool mid = StepUntil(() => HomeGridService.FindBuilding(s, g.BuildingId).ConstructionDelivered > 0 && go.State == WorkOrderState.InProgress && go.Progress > 3f, 300);
            string before = ConstructionJson(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);

            string RunFromSave()
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                WorldSimulation.LoadHome(resume: true);
                string loaded = ConstructionJson(rr.State);
                if (loaded != before)
                {
                    return "往返不一致：\n" + before + "\n---\n" + loaded;
                }
                rr.State.Scrap += 100;
                StepUntil(() => HomeGridService.FindBuilding(rr.State, g.BuildingId)?.ConstructionState == BuildingConstructionState.Operational
                                && (rr.State.Grid.PlannedBelts?.Length ?? 0) == 0, 500);
                string belts = string.Join(",", Enumerable.Range(0, 5).Select(k => BeltNetworkService.Kernel.HasCell(a.X + k, a.Y) ? "1" : "0"));
                return ConstructionJson(rr.State) + "\n" + belts;
            }

            string first = RunFromSave();
            string second = RunFromSave();
            Expect(mid && save.Success && !first.StartsWith("往返", StringComparison.Ordinal) && !first.StartsWith("读档", StringComparison.Ordinal),
                "施工中途真文件存读档：虚影的所需 / 已到材料、施工单的腿 / 取料次数 / 进度 / 优先级、传送带规划逐字段往返一致"
                + (first.StartsWith("往返", StringComparison.Ordinal) || first.StartsWith("读档", StringComparison.Ordinal) ? "\n" + first : string.Empty));
            Expect(first == second && first.Contains("Operational") && first.EndsWith("1,1,1,1,1", StringComparison.Ordinal),
                "读档后照常完工（发电机建成、5 格传送带全部进内核）；同一存档读两次结果逐字段一致" + (first == second ? string.Empty : "\n" + first + "\n---\n" + second));
        }

        private static void CheckLegacyMigration()
        {
            // FG3-LOG-01 之前的存档：放置时全部废料已预留在施工单的资源事务里（Reserved）。
            CampaignState s = NewHome(6101);
            s.Scrap = 100;
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            WorkOrderRecord order = BuildOrder(s, r.BuildingId);
            string tx = order.WorkOrderId + ":tx";
            CampaignEconomyLedger.ProposeConsume(s, tx, r.BuildingId, CampaignEconomyLedger.ResourceScrap, 60);
            CampaignEconomyLedger.Reserve(s, tx); // 旧写法：放置那一刻扣 60
            order.ResourceTransactionId = tx;
            ghost.ConstructionRequired = 0;
            ghost.ConstructionDelivered = 0;
            order.Leg = 0;
            string json = JsonUtility.ToJson(s).Replace("\"ConstructionRequired\":0,", string.Empty).Replace("\"Leg\":0,", string.Empty);
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(json);
            CampaignSession.Set(0, legacy);
            HomeGridService.Invalidate();
            int migrated0 = HomeValleyConstruction.MigratedLegacyOrders;
            TickOrders(legacy, 0.1f);
            BuildingRecord lg = HomeGridService.FindBuilding(legacy, r.BuildingId);
            WorkOrderRecord lo = legacy.WorkOrders.First(o => o.WorkOrderId == order.WorkOrderId);
            ResourceTransactionRecord ltx = CampaignEconomyLedger.Find(legacy, tx);
            Expect(HomeValleyConstruction.MigratedLegacyOrders == migrated0 + 1 && lg.ConstructionRequired == 60 && lg.ConstructionDelivered == 60 && legacy.Scrap == 40
                   && lo.ResourceTransactionId == null && ltx.State == ResourceTransactionState.Committed && lo.Leg == 0,
                "旧存档“放置即预留 60”的施工单：读档后预留的 60 算作已到现场（事务确认消费）、所需补成 60、施工单直接去现场，库存 40 不变（不丢不重复）");
            TickOrders(legacy, 45f);
            Expect(lg.ConstructionState == BuildingConstructionState.Operational && legacy.Scrap == 40 && lg.InvestedScrap == 60,
                "迁移后照常完工，不再从库存多扣");
        }

        // ── L. 暂停与倍速 ────────────────────────────────────────────────────────

        private static void CheckTiming()
        {
            var results = new List<string>();
            var games = new List<double>();
            bool allOk = true;
            const float frame = 1f / 60f;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(6200, scrap: 100);
                GameClock.SetPaused(true); // 战略暂停：统一时钟不走步，家园模拟（含施工单）不推进
                GridOpResult r = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
                BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
                WorkOrderRecord order = BuildOrder(s, r.BuildingId);
                long steps0 = GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(frame);
                }
                bool pausedNoProgress = r.Success && GameClock.Ticks == steps0 && order.State == WorkOrderState.Ready && order.FetchCount == 0 && s.Scrap == 100;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                double g0 = GameClock.GameSeconds;
                int frames = 0;
                while (ghost.ConstructionState != BuildingConstructionState.Operational && frames < 60 * 900)
                {
                    WorldSimulation.Frame(frame);
                    frames++;
                }
                double game = GameClock.GameSeconds - g0;
                float real = frames * frame;
                games.Add(game);
                results.Add($"{speed}x：游戏 {game:F2} 秒 / 真实 {real:F1} 秒 / 库存 {s.Scrap}");
                allOk &= pausedNoProgress && ghost.ConstructionState == BuildingConstructionState.Operational && s.Scrap == 40
                         && Math.Abs(real * speed - game) < 0.2 + frame * speed;
                GameClock.SetSpeed(1f);
            }
            double spread = games.Count == 4 ? games.Max() - games.Min() : double.MaxValue;
            Expect(allOk && spread <= 2.0 / GameClock.StepHz + 1e-6,
                $"倍速矩阵（真实载入的家园、统一时钟）：暂停中可以放虚影、不走步不取料；0.5x / 1x / 2x / 3x 下取料 + 施工完工所需游戏时间相同（差 {spread:F3} 秒），真实时间按倍速缩放（{string.Join("；", results)}）");
        }

        // ── M. 观察 / 不观察一致 ─────────────────────────────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observed)
            {
                CampaignState s = NewWorld(6301, observe: observed, scrap: 30);
                GridCell spot = GenSpot(s);
                GridCell core = HomeGridService.CorePivot(s);
                GridCell a = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 34), 4, 14) ?? default;
                if (observed)
                {
                    HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
                    mode.Open();
                    mode.Select("generator_2");
                    mode.ClickCell(s, spot);
                    mode.Select("belt_t1");
                    mode.RotateGhost();
                    mode.PointerDown(s, a);
                    mode.SetHover(s, new GridCell(a.X + 3, a.Y));
                    mode.PointerUp(s, new GridCell(a.X + 3, a.Y));
                    mode.Close();
                }
                else
                {
                    HomeGridService.TryPlace(s, "generator_2", spot, 0);
                    HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, new GridCell(a.X + 3, a.Y), BeltDir.East);
                }
                WorldSimulation.StepMany(GameClock.StepHz * 60);
                s.Scrap += 100;
                WorldSimulation.StepMany(GameClock.StepHz * 120);
                string belts = string.Join(",", Enumerable.Range(0, 4).Select(i => BeltNetworkService.Kernel.HasCell(a.X + i, a.Y) ? "1" : "0"));
                string orders = string.Join(";", (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o.Kind == WorkOrderKind.Build || o.IssuerId == "return")
                    .Select(o => $"{o.Kind}:{o.State}:{o.Leg}:{o.FetchCount}:{o.Progress:F3}"));
                return string.Join("\n", (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).OrderBy(b => b.BuildingId, StringComparer.Ordinal)
                           .Select(b => $"{b.BuildingId}|{b.ConstructionState}|{b.ConstructionRequired}|{b.ConstructionDelivered}|{b.InvestedScrap}"))
                       + "\n" + s.Scrap + "\n" + GroundScrap(s) + "\n" + belts + "\n" + orders;
            }

            string seen = Run(true);
            string unseen = Run(false);
            Expect(seen == unseen && seen.Contains("Operational") && seen.Contains("1,1,1,1"),
                "同一组操作（库存 30 放发电机虚影与 4 格传送带虚影，等 60 秒后到货 100，再跑 120 秒）经建造模式（观察）与直接调服务（不观察）："
                + "虚影、材料、施工单、传送带逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── N. 正式输入与施工队列面板 ───────────────────────────────────────────────

        private static void CheckFormalInputAndQueuePanel()
        {
            CampaignState s = NewHome(6401);
            s.Scrap = 0;
            GridCell spotA = GenSpot(s, -10, 26);
            GridOpResult a = HomeGridService.TryPlace(s, "generator_2", spotA, 0);
            GridCell spotB = FindValid(s, "generator_2", new GridCell(spotA.X + 12, spotA.Y), 12) ?? spotA;
            GridOpResult b = HomeGridService.TryPlace(s, "generator_2", spotB, 0);
            TickOrders(s, 1.5f); // 没有货：两张都转为等待材料
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(false);
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            VisualElement root = MountUxml(UiKitFolder + "ConstructionQueuePanel.uxml", out GameObject go);
            ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ConstructionQueuePanelUIToolkit panel = go.AddComponent<ConstructionQueuePanelUIToolkit>();
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ConstructionQueueRow.uxml"));
                panel.BindView(root);

                // 施工队列键（默认 Alt+B）：战略视角（建造模式关着）也能打开。
                reader.Press(KeyCode.LeftAlt);
                reader.Press(KeyCode.B);
                mode.Tick(null, s, true);
                Frame(reader);
                bool opened = ConstructionQueuePanelUIToolkit.IsOpen && panel.PanelVisible && !mode.IsOpen;
                panel.Refresh();
                bool rows = panel.VisibleRowCount == 2 && panel.RowName(0).Contains("发电机") && panel.RowStatus(0).Contains("等待材料") && panel.RowStatus(0).Contains("×60")
                            && panel.RowPriority(0).Contains("普通") && panel.CountText.Contains("2") && panel.LaborText.Contains("库存废料 0")
                            && !GameText.ContainsMarker(panel.RowStatus(0) + panel.FooterText + panel.LaborText) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildQueueFirstOpen);
                Expect(opened && rows, $"按施工队列键打开面板（建造模式不必开着）：2 行，“{panel.RowName(0)}：{panel.RowStatus(0)}”，{panel.RowPriority(0)}；劳动力行“{panel.LaborText.Replace("\n", " ")}”");

                // 行内按钮：提高第二行 → 排到第一；取消 → 虚影消失（库存不变：还没取过料）。
                string bOrder = BuildOrder(s, b.BuildingId).WorkOrderId;
                int RowOf(string id) => Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowOrderId(i) == id);
                bool bSecond = panel.RowOrderId(1) == bOrder; // 同一时刻放下的两座按放下先后排：A 在前
                Click(panel.RowButton(RowOf(bOrder), "CqUp"));
                bool upOk = panel.RowOrderId(0) == bOrder && panel.RowPriority(0).Contains("高");
                Click(panel.RowButton(RowOf(bOrder), "CqDown"));
                Click(panel.RowButton(RowOf(bOrder), "CqDown"));
                bool downOk = panel.RowOrderId(1) == bOrder && panel.RowPriority(1).Contains("低");
                Click(panel.RowButton(RowOf(bOrder), "CqCancel"));
                bool cancelOk = HomeGridService.FindBuilding(s, b.BuildingId) == null && panel.VisibleRowCount == 1 && s.Scrap == 0;
                upOk &= bSecond;
                Expect(upOk && downOk && cancelOk, "行内“提高”把 B 排到第一（高）、两次“降低”排到最后（低）、“取消”把 B 的虚影取消（全额退回，这里还没取料）");

                Click(panel.RowButton(0, "CqLocate"));
                Expect(!ConstructionQueuePanelUIToolkit.IsOpen, "“定位”关掉队列、镜头飞到虚影（WorldView.FlyTo 同一入口）");

                // Esc 关闭；空队列有说明。
                ConstructionQueuePanelUIToolkit.Open();
                HomeGridService.TryToggleDemolish(s, a.BuildingId);
                panel.Refresh();
                bool empty = panel.VisibleRowCount == 0 && panel.EmptyText.Contains("没有正在进行的施工");
                bool esc = UiEscapeStack.CloseTop() && !ConstructionQueuePanelUIToolkit.IsOpen;
                Expect(empty && esc, $"空队列说明“{panel.EmptyText}”；Esc 关闭面板");

                // “优先建造这一片”键（默认 P，建造上下文）：拉框改优先级、右键退出。
                GridOpResult c = HomeGridService.TryPlace(s, "generator_2", spotA, 0);
                WorkOrderRecord oc = BuildOrder(s, c.BuildingId);
                mode.Open();
                mode.Tick(null, s, true);
                Frame(reader);
                reader.Press(KeyCode.P);
                mode.Tick(null, s, true);
                Frame(reader);
                bool prioritizeOn = mode.PrioritizeMode;
                mode.SetHover(s, new GridCell(spotA.X - 2, spotA.Y - 2));
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                mode.SetHover(s, new GridCell(spotA.X + 2, spotA.Y + 2));
                mode.RefreshVisuals(s);
                bool indicator = mode.Drag == HomeValleyBuildMode.DragKind.PrioritizeBox && mode.PrioritizeBoxCount == 1 && mode.BoxIndicatorVisible;
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool applied = oc.Priority == HomeValleyConstruction.PriorityMax && mode.StatusText.Contains("1 处") && mode.LastResult.Outcome == GridOpResult.Kind.Prioritized;
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(prioritizeOn && indicator && applied && !mode.PrioritizeMode && mode.IsOpen,
                    $"建造模式按 P 进入“优先建造这一片”、拖框（画出框、HUD 数出 1 处）、松开改成最高（“{mode.StatusText}”）；右键退出这个模式");

                // 队列面板布局探针（中英、缩放极值；有行的数据态）。
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "ConstructionQueuePanel.uxml", "ConstructionQueueWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_cq") { hideFlags = HideFlags.HideAndDontSave };
                            ConstructionQueuePanelUIToolkit p = probeGo.AddComponent<ConstructionQueuePanelUIToolkit>();
                            p.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ConstructionQueueRow.uxml"));
                            p.BindView(pr.panel.visualTree);
                            p.Refresh();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("ConstructionQueueRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 ConstructionQueuePanel.uxml#ConstructionQueueWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                ConstructionQueuePanelUIToolkit.Close();
                ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                mode.Shutdown();
                HomeValleyBuildMode.Unbind(mode);
                InputRouter.DebugSetReader(null);
                UiEscapeStack.Clear();
            }
        }

        // ── O. 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewHome(6501);
            s.Scrap = 0;
            GridCell core = HomeGridService.CorePivot(s);
            int placed = 0;
            for (int y = core.Y + 14; y <= core.Y + 60 && placed < 60; y += 4)
            {
                for (int x = core.X - 40; x <= core.X + 40 && placed < 60; x += 4)
                {
                    if (HomeGridService.ValidatePlacement(s, "generator_2", new GridCell(x, y), 0, checkCost: false).Ok
                        && HomeGridService.TryPlace(s, "generator_2", new GridCell(x, y), 0).Success)
                    {
                        placed++;
                    }
                }
            }
            TickOrders(s, 1f); // 全部转为等待材料
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                HomeValleyWorkOrders.Tick(s, 1f / 60f, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
            }
            double tickMs = sw.Elapsed.TotalMilliseconds / 200;
            var queue = new List<HomeValleyConstruction.QueueEntry>();
            sw.Restart();
            for (int i = 0; i < 50; i++)
            {
                HomeValleyConstruction.CollectQueue(s, queue);
            }
            double queueMs = sw.Elapsed.TotalMilliseconds / 50;
            PerfLines.Add($"{placed} 座虚影都在等材料：施工单 Tick 每次 {tickMs:F3} ms（每步 O(工单数)，个位数到百位数量级的战略层管理）；施工队列收集 {queueMs:F3} ms / 次（只在面板打开时每 0.25 秒）");
            ExpectPerf(placed >= 40 && queue.Count == placed,
                $"{placed} 座虚影：施工单 Tick {tickMs:F3} ms、队列收集 {queueMs:F3} ms（预算 2 / 5 ms，Editor 托管代码；真机由 FG15-SYS-02 复测）",
                PerfGate.Lt(tickMs, 2.0, "施工单 Tick ms"), PerfGate.Lt(queueMs, 5.0, "队列收集 ms"));
        }

        // ── P. 审查修复回归（第 1 轮）──────────────────────────────────────────────

        private static HomeValleyMachineMarker HomeMarker(int logicId) =>
            WorldSimulation.Home?.Combat != null && WorldSimulation.Home.Combat.TryGetMachineMarker(logicId, out HomeValleyMachineMarker m) ? m : null;

        /// <summary>只留一台能施工的机器（其余施工偏好 0），返回它。</summary>
        private static MachineRecord OnlyBuilder()
        {
            List<MachineRecord> machines = HomeMachines().Where(m => HomeValleyWorkOrders.CanDoKind(m.ChassisId, WorkOrderKind.Build)).ToList();
            foreach (MachineRecord m in machines.Skip(1))
            {
                MachineRegistry.TrySetWorkPriority(m.LogicId, WorkOrderKind.Build, 0);
            }
            return machines.FirstOrDefault();
        }

        private static void CheckReviewRegressions()
        {
            CheckRightClickKeepsGhost();
            CheckDepartureKeepsGhost();
            CheckPausedTrimDropsUnique();
            CheckChunkedReturnHaul();
            CheckUnsupportedHaul();
            CheckQueueOrderMatchesAssignment();
            CheckFetchBudgetByPriority();
            CheckPendingLegAfterLoad();
            CheckBeltBuiltNearMachine();
            CheckQueueKeyToggles();
            CheckSiteViewIndexIncremental();
        }

        /// <summary>P1：机器自动领了虚影 G1、正扛着材料；玩家选中它右键另一个虚影 G2（真实右键入口 TryContextWork：射线点中 G2 的虚影方块）。</summary>
        private static void CheckRightClickKeepsGhost()
        {
            CampaignState s = NewWorld(6501, observe: true, scrap: 100);
            MachineRecord m = OnlyBuilder();
            GridCell spot1 = GenSpot(s, -10, 26);
            GridOpResult g1 = HomeGridService.TryPlace(s, "generator_2", spot1, 0);
            GridCell spot2 = FindValid(s, "generator_2", new GridCell(spot1.X + 12, spot1.Y), 12) ?? spot1;
            GridOpResult g2 = HomeGridService.TryPlace(s, "generator_2", spot2, 0);
            WorkOrderRecord o1 = BuildOrder(s, g1.BuildingId);
            WorkOrderRecord o2 = BuildOrder(s, g2.BuildingId);
            bool carrying = m != null && StepUntil(() => (o1.AssignedMachineLogicId == m.LogicId || o2.AssignedMachineLogicId == m.LogicId)
                                                         && CargoTotal() > 0, 120);
            WorldSimulation.Frame(0.02f); // 建出虚影方块（观察中的家园画面）
            carrying &= CargoTotal() > 0;
            WorkOrderRecord mine = o1.AssignedMachineLogicId == m?.LogicId ? o1 : o2;
            WorkOrderRecord other = mine == o1 ? o2 : o1;
            BuildingRecord mineGhost = HomeGridService.FindBuilding(s, mine.TargetId);
            BuildingRecord otherGhost = HomeGridService.FindBuilding(s, other.TargetId);
            int cargo = CargoTotal();
            int scrap0 = s.Scrap;
            int delivered0 = mineGhost?.ConstructionDelivered ?? -1;

            // 真实右键入口：镜头看向 G2，把屏幕点换成射线，交给家园控制器的右键派工（与玩家右键同一个方法）。
            HomeValleyController home = WorldSimulation.Home;
            Transform ghostGo = GameObject.Find("[HomeValley]")?.transform.Find("Building_" + HomeValleyController.LocalKey(other.TargetId));
            Camera cam = WorldView.EnsureCamera();
            string hitName = "（没有虚影方块或镜头）";
            bool routed = false;
            if (ghostGo != null && cam != null && m != null)
            {
                cam.transform.position = ghostGo.position + new Vector3(0f, 25f, -12f);
                cam.transform.LookAt(ghostGo.position);
                Physics.SyncTransforms();
                Vector3 pointer = cam.WorldToScreenPoint(ghostGo.position + Vector3.up * 0.2f);
                hitName = Physics.Raycast(cam.ScreenPointToRay(pointer), out RaycastHit hit, 500f) ? hit.collider.gameObject.name : "（射线没打中）";
                System.Reflection.MethodInfo ctx = typeof(HomeValleyController).GetMethod("TryContextWork",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                routed = ctx != null && (bool)ctx.Invoke(home, new object[] { HomeMarker(m.LogicId), pointer });
            }
            bool keptMine = HomeGridService.FindBuilding(s, mine.TargetId) != null && HomeValleyWorkOrders.IsActive(mine)
                            && mine.AssignedMachineLogicId != m?.LogicId && mineGhost?.ConstructionDelivered == delivered0;
            bool tookOther = other.AssignedMachineLogicId == m?.LogicId && other.State == WorkOrderState.Reserved && otherGhost != null;
            Expect(carrying && routed && keptMine && tookOther && s.Scrap == scrap0 + cargo && CargoTotal() == 0,
                $"机器扛着 {cargo} 为 G1 施工时，玩家右键 G2 的虚影（射线点中“{hitName}”）：G1 的虚影与已到现场的 {delivered0} 保留、施工单回到待分配池，"
                + $"货舱材料退回仓库（{scrap0}→{s.Scrap}），G2 的施工单交给这台机器（不走“修复”、不被拒绝）——FGR-BASE-020 / FG-GAP-063");
            Expect(s.Scrap + SiteDelivered(s) + CargoTotal() == 100, "右键换目标前后材料守恒：库存 + 现场 + 货舱 = 100");

            // 缺料时右键虚影：拒绝并写明缺什么（不白跑）；已有别的机器在做：拒绝。
            s.Scrap = 0;
            GridCell spot3 = FindValid(s, "generator_2", new GridCell(spot1.X, spot1.Y + 12), 12) ?? spot1;
            GridOpResult g3 = HomeGridService.TryPlace(s, "generator_2", spot3, 0);
            MachineRecord idle = HomeMachines().FirstOrDefault(x => x.LogicId != m?.LogicId && HomeValleyWorkOrders.CanDoKind(x.ChassisId, WorkOrderKind.Build));
            HomeValleyWorkOrders.WorkOrderOpResult noStock = HomeValleyWorkOrders.TryAssignConstruction(s, g3.BuildingId, idle?.LogicId ?? 0);
            string noStockText = HomeValleyWorkOrders.DescribeCommandFailure(noStock.FailureReason);
            HomeValleyWorkOrders.WorkOrderOpResult busy = HomeValleyWorkOrders.TryAssignConstruction(s, other.TargetId, idle?.LogicId ?? 0);
            Expect(idle != null && !noStock.Success && noStockText.Contains("等待材料") && noStockText.Contains("×60") && !busy.Success
                   && HomeValleyWorkOrders.DescribeCommandFailure(busy.FailureReason).Contains("已经有机器在做"),
                $"库存为 0 时右键虚影：拒绝并提示“{noStockText}”；右键别的机器正在建的虚影：拒绝（“已经有机器在做”）");
        }

        /// <summary>P1：派正在施工的机器去远征（出发确认后的中断入口）：虚影与施工单保留，货舱材料退回仓库。</summary>
        private static void CheckDepartureKeepsGhost()
        {
            CampaignState s = NewHome(6502, haulers: 2);
            s.Scrap = 100;
            GridOpResult g = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            WorkOrderRecord o = BuildOrder(s, g.BuildingId);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, x => HomeValleyWorkOrders.OnArrivedAtWork(s, x.WorkOrderId)); // 分配 + 瞬时取料
            int machine = o.AssignedMachineLogicId;
            bool carrying = machine > 0 && o.Leg == 0 && CargoTotal() == 40 && s.Scrap == 60;
            ExpeditionDepartureService.InterruptWorkForDeparture(s, new[] { machine });
            Expect(carrying && HomeGridService.FindBuilding(s, g.BuildingId) != null && o.State == WorkOrderState.Ready && o.AssignedMachineLogicId == 0
                   && o.Leg == 1 && CargoTotal() == 0 && s.Scrap == 100,
                "机器扛着 40 施工时被派去远征（玩家确认中断）：虚影保留、施工单回到待分配池，40 退回仓库——不撤销玩家的规划（此前整份取消）");
            TickOrders(s, 60f);
            Expect(HomeGridService.FindBuilding(s, g.BuildingId).ConstructionState == BuildingConstructionState.Operational && s.Scrap == 40,
                "留守的机器接着取料施工并完工（总共只用 60）");
        }

        /// <summary>P1：战略暂停中、仓库满时，对同一份传送带规划连续两次逐格取消：退回的多余材料都在地上（此前第二份标识重复被吞掉）。</summary>
        private static void CheckPausedTrimDropsUnique()
        {
            CampaignState s = NewWorld(6503, scrap: 10);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? row = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 26), 10, 14);
            if (row == null)
            {
                Fail("找不到能铺 10 格的空地");
                return;
            }
            GridCell a = row.Value;
            List<GridCell> cells = Enumerable.Range(0, 10).Select(k => new GridCell(a.X + k, a.Y)).ToList();
            HomeGridService.TryPlaceBeltPath(s, "belt_t1", cells[0], cells[9], BeltDir.East);
            PlannedBeltRecord p = s.Grid.PlannedBelts.Last();
            bool ready = StepUntil(() => HomeValleyConstruction.BuiltCells(p) >= 2 && CargoTotal() == 0 && p.Delivered >= 3, 300);
            GameClock.SetPaused(true);
            try
            {
                int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
                s.Scrap = cap;
                int site0 = p.Delivered;
                int ground0 = GroundScrap(s);
                long ticks0 = GameClock.Ticks;
                HomeGridService.TryRemoveBelts(s, new List<GridCell> { cells[9] });
                int site1 = p.Delivered;
                HomeGridService.TryRemoveBelts(s, new List<GridCell> { cells[8] });
                int site2 = p.Delivered;
                int returned = site0 - site2;
                Expect(ready && GameClock.Ticks == ticks0 && returned >= 2 && site0 > site1 && site1 > site2 && GroundScrap(s) - ground0 == returned && s.Scrap == cap,
                    $"暂停中（时钟不走）仓库满时连续两次单格取消传送带虚影：现场 {site0}→{site1}→{site2}，退回的 {returned} 全部在地上（地面 +{GroundScrap(s) - ground0}），不凭空消失");
            }
            finally
            {
                GameClock.SetPaused(false);
            }
        }

        /// <summary>P2：仓满时一大份返还按每趟搬运量拆开落地，超过仓库总容量也能一份份搬回去。</summary>
        private static void CheckChunkedReturnHaul()
        {
            CampaignState s = NewWorld(6504, scrap: 0);
            int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            s.Scrap = cap;
            int big = cap + 20;
            HomeValleyConstruction.ReturnMaterials(s, HomeValleyLayout.Core.Position + new Vector2(6f, 0f), CampaignEconomyLedger.ResourceScrap, big, "fgconstruct:big");
            GroundItemRecord[] piles = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.SalvageInstanceId.StartsWith("fgconstruct:big", StringComparison.Ordinal)).ToArray();
            int per = HomeValleyConstruction.CarryPerTrip;
            int hauls = s.WorkOrders?.Count(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return") ?? 0;
            Expect(piles.Sum(g => g.Amount) == big && piles.All(g => g.Amount <= per) && piles.Length == (big + per - 1) / per && hauls == piles.Length,
                $"仓满时返还 {big}（超过仓库总容量 {cap}）：按每趟 {per} 拆成 {piles.Length} 份落地、各有一张返还搬运单");
            int Pile() => (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.SalvageInstanceId.StartsWith("fgconstruct:big", StringComparison.Ordinal)).Sum(g => g.Amount);
            s.Scrap = 0;
            bool most = StepUntil(() => s.Scrap > cap - per, 600);
            int got1 = s.Scrap;
            s.Scrap = 0;
            bool rest = StepUntil(() => Pile() == 0 && CargoTotal() == 0 && s.Scrap > 0, 300);
            Expect(most && rest && got1 + s.Scrap == big,
                $"腾出仓库后机器一份份搬回：先搬满仓库（{got1}），再腾一次把剩下的 {s.Scrap} 搬回，合计 {got1 + s.Scrap} = {big}（此前一整份超过总容量，永远等不到空位）");
        }

        /// <summary>P2：仓库存不了的返还物品（传送带物品、建筑缓存）：玩家点选搬运被拒并说明，物品不会在交付时消失。</summary>
        private static void CheckUnsupportedHaul()
        {
            CampaignState s = NewHome(6505);
            GroundItemRecord item = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(-8f, -6f), HomeGridService.BeltItemResource(900), 2, "fgconstruct:item"); // FG4-ECO-01 起物品表里的固体都能存：用表里没有的编号
            MachineRecord m = HomeMachines().First();
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateHaul(s, item.GroundItemId, m.LogicId);
            string text = HomeValleyWorkOrders.DescribeCommandFailure(r.FailureReason);
            HomeValleyCargo.StoreResult direct = HomeValleyCargo.CommitHaul(s, HomeValleyCargo.TryReserveHaul(s, item.GroundItemId));
            GroundItemRecord still = HomeValleyCargo.FindGroundItemBySalvageId(s, "fgconstruct:item");
            Expect(!r.Success && text.Contains("存不了") && !GameText.ContainsMarker(text) && !direct.Success && still != null && still.Amount == 2,
                $"点选机器搬运仓库存不了的物品：拒绝并提示“{text}”；直接交付失败时物品放回地面（仍是 {still?.Amount} 件），不凭空消失");
        }

        /// <summary>P2：同一时刻放下、同优先级的虚影，机器先领施工队列里排第一的那张（此前按带随机段的工单 ID）。</summary>
        private static void CheckQueueOrderMatchesAssignment()
        {
            bool allMatch = true;
            string detail = string.Empty;
            for (int seed = 0; seed < 4; seed++)
            {
                CampaignState s = NewHome(6510 + seed);
                s.Scrap = 500;
                OnlyBuilder();
                GridCell spot = GenSpot(s, -10, 26);
                for (int k = 0; k < 3; k++)
                {
                    GridCell c = FindValid(s, "generator_2", new GridCell(spot.X + k * 8, spot.Y), 12) ?? spot;
                    HomeGridService.TryPlace(s, "generator_2", c, 0);
                }
                var queue = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, queue);
                HomeValleyWorkOrders.MarkAssignmentDirty();
                HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, _ => { });
                bool match = queue.Count == 3 && queue[0].Order.AssignedMachineLogicId > 0 && queue.Skip(1).All(q => q.Order.AssignedMachineLogicId == 0);
                allMatch &= match;
                detail += match ? "✓" : "✗";
            }
            Expect(allMatch, $"4 局各放 3 座同优先级虚影（同一时刻）：唯一的机器每局都先领施工队列第一行（{detail}）——队列显示的先后就是领单先后");
        }

        /// <summary>P2：库存不够分：只放出够用的那张（按优先级），其余等待材料，不派多台机器空跑。</summary>
        private static void CheckFetchBudgetByPriority()
        {
            NotificationCenter.ResetForTests();
            CampaignState s = NewHome(6520, haulers: 3);
            s.Scrap = 40;
            GridCell spot = GenSpot(s, -10, 26);
            var orders = new List<WorkOrderRecord>();
            for (int k = 0; k < 3; k++)
            {
                GridCell c = FindValid(s, "generator_2", new GridCell(spot.X + k * 8, spot.Y), 12) ?? spot;
                orders.Add(BuildOrder(s, HomeGridService.TryPlace(s, "generator_2", c, 0).BuildingId));
            }
            HomeValleyConstruction.SetPriority(s, orders[2].WorkOrderId, 1); // 玩家把最后放的那座提到“高”
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, _ => { }); // 只分配、不到达：看派了几台机器去仓库
            int dispatched = orders.Count(o => o.State == WorkOrderState.Reserved && o.AssignedMachineLogicId > 0);
            int waiting = orders.Count(o => o.State == WorkOrderState.Waiting && o.AssignedMachineLogicId == 0);
            Expect(dispatched == 1 && orders[2].State == WorkOrderState.Reserved && waiting == 2 && s.Scrap == 40,
                $"库存 40 只够一趟、3 座虚影各缺 60：只派 1 台机器去取（给优先级“高”的那座），另外 2 座显示等待材料、不派机器空跑（派出 {dispatched}，等待 {waiting}）");
            HomeValleyWorkOrders.OnArrivedAtWork(s, orders[2].WorkOrderId); // 到了仓库：取走 40
            s.Scrap += 40; // 又进了 40
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, _ => { }, _ => false, _ => { });
            Expect(orders[0].State == WorkOrderState.Reserved && orders[1].State == WorkOrderState.Waiting,
                "又进了 40：按“同优先级先放先建”放出第一座去取料，第二座继续等待");
        }

        /// <summary>P2：存档落在“已分配、机器还没出发”的空档（取料后下一腿发出前）：读档后机器按当前这一腿出发，不误报“路径受阻”。</summary>
        private static void CheckPendingLegAfterLoad()
        {
            CampaignState s = NewWorld(6530, scrap: 100);
            MachineRecord m = OnlyBuilder();
            if (m == null)
            {
                Fail("家园里没有能施工的机器");
                return;
            }
            GridOpResult g = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            WorkOrderRecord o = BuildOrder(s, g.BuildingId);
            HomeValleyWorkOrders.WorkOrderOpResult assigned = HomeValleyWorkOrders.TryAssignConstruction(s, g.BuildingId, m.LogicId); // 已分配、没有下达移动
            bool gap = assigned.Success && o.State == WorkOrderState.Reserved && HomeMarker(m.LogicId) != null && !HomeMarker(m.LogicId).AwaitsArrivalAction;
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            int resumed = HomeValleyController.LastResumedPendingLegs;
            WorkOrderRecord lo = HomeValleyWorkOrders.FindActiveBuild(rr.State, g.BuildingId);
            bool sawBlocked = false;
            bool moved = StepUntil(() => lo != null && (lo.Leg == 0 || lo.State == WorkOrderState.InProgress), 30,
                () => sawBlocked |= lo != null && lo.FailureReason == "path-blocked");
            Expect(gap && save.Success && resumed >= 1 && moved && !sawBlocked,
                $"存档时施工单已分配但机器还没出发：读档后按当前这一腿重新出发（重发 {resumed} 条），30 秒内取到料，没有误报“路径受阻”");
        }

        /// <summary>P2：传送带虚影只在机器身边施工：每建一格时机器都在这一格的交互距离附近（此前站在第一格能把远处几十格全部建成）。</summary>
        private static void CheckBeltBuiltNearMachine()
        {
            CampaignState s = NewWorld(6540, scrap: 12);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? row = FindBeltRow(s, new GridCell(core.X - 14, core.Y + 26), 12, 14);
            if (row == null)
            {
                Fail("找不到能铺 12 格的空地");
                return;
            }
            GridCell a = row.Value;
            HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, new GridCell(a.X + 11, a.Y), BeltDir.East);
            PlannedBeltRecord p = s.Grid.PlannedBelts.Last();
            WorkOrderRecord o = BuildOrder(s, HomeValleyConstruction.BeltPlanPrefix + p.PlanId);
            var seen = new HashSet<int>();
            float worst = 0f;
            bool done = StepUntil(() => (s.Grid.PlannedBelts?.Length ?? 0) == 0, 400, () =>
            {
                for (int i = 0; i < 12; i++)
                {
                    if (seen.Contains(i) || !BeltNetworkService.Kernel.HasCell(a.X + i, a.Y))
                    {
                        continue;
                    }
                    seen.Add(i);
                    int id = o.AssignedMachineLogicId;
                    if (id > 0 && MachineRegistry.TryGetLivePosition(id, out Vector2 at))
                    {
                        worst = Mathf.Max(worst, Vector2.Distance(at, new Vector2(a.X + i, a.Y)));
                    }
                }
            });
            Expect(done && seen.Count == 12 && worst <= RegionInteractionSystem.InteractRange + 2.5f,
                $"12 格传送带虚影：机器一段一段走过去建，每格建成时机器离它最远 {worst:F1} 米（交互距离 {RegionInteractionSystem.InteractRange} 米 + 1/4 秒走动余量）");
        }

        /// <summary>P2：施工队列开着时，再按同一个键（默认 Alt+B）关闭（面板是模态，走全局键）。</summary>
        private static void CheckQueueKeyToggles()
        {
            CampaignState s = NewHome(6550);
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            VisualElement root = MountUxml(UiKitFolder + "ConstructionQueuePanel.uxml", out GameObject go);
            ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ConstructionQueuePanelUIToolkit panel = go.AddComponent<ConstructionQueuePanelUIToolkit>();
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ConstructionQueueRow.uxml"));
                panel.BindView(root);
                reader.Press(KeyCode.LeftAlt);
                reader.Press(KeyCode.B);
                mode.Tick(null, s, true);
                UiKitInputPump.ProcessWorldKeys();
                Frame(reader);
                bool opened = ConstructionQueuePanelUIToolkit.IsOpen;
                reader.Press(KeyCode.LeftAlt);
                reader.Press(KeyCode.B);
                mode.Tick(null, s, true);
                UiKitInputPump.ProcessWorldKeys();
                Frame(reader);
                Expect(opened && !ConstructionQueuePanelUIToolkit.IsOpen, "按 Alt+B 打开施工队列，再按一次 Alt+B 关闭（与信号核面板同一规则）");
            }
            finally
            {
                ConstructionQueuePanelUIToolkit.Close();
                ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                mode.Shutdown();
                HomeValleyBuildMode.Unbind(mode);
                InputRouter.DebugSetReader(null);
                UiEscapeStack.Clear();
            }
        }

        /// <summary>P2：虚影画面的施工单索引不再每帧全扫工单历史：没有变化的帧不重建。</summary>
        private static void CheckSiteViewIndexIncremental()
        {
            CampaignState s = NewHome(6560);
            s.Scrap = 0;
            GridOpResult g = HomeGridService.TryPlace(s, "generator_2", GenSpot(s), 0);
            var view = new ConstructionSiteView();
            view.BeginFrame(s);
            int first = view.IndexRebuilds;
            for (int i = 0; i < 100; i++)
            {
                view.BeginFrame(s);
            }
            int idle = view.IndexRebuilds - first;
            HomeGridService.TryToggleDemolish(s, g.BuildingId); // 取消：修订号变化
            view.BeginFrame(s);
            BuildingRecord probe = new BuildingRecord { BuildingId = g.BuildingId, ConstructionState = BuildingConstructionState.Planned };
            Expect(first == 1 && idle == 0 && view.IndexRebuilds == 2 && view.IconOf(probe) == null && view.FractionOf(s, g.BuildingId) == 0f,
                $"虚影画面索引：100 帧没有变化不重建（{idle} 次），取消后重建一次、已结束的施工单不再显示");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgConstructionSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public readonly HashSet<int> MouseDown = new HashSet<int>();
            public readonly HashSet<int> MouseUp = new HashSet<int>();

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                MouseDown.Clear();
                MouseUp.Clear();
            }

            public bool GetKey(KeyCode key) => _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => MouseDown.Contains(button);
            public bool GetMouseButtonUp(int button) => MouseUp.Contains(button);
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
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
