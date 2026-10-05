using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Primitive;
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
    /// FG4-ECO-05 建筑通用状态、面板与升级的自动验收（FG04 FGR-ECO-010～013；FG13 FGU-09；FGT-ECO-002；卡片“建筑改名；启用和禁用；批量修改优先级；面板跳到上下游建筑；‘?’帮助”；
    /// 负向“升级中断电；升级中被摧毁”；承接 FG-GAP-091 / 093 / 094 / 095、DEBT-FG3LOG07-01 / FG4ECO01-03 / FG4ECO02-02 / FG4ECO04-04 / FG1SIG04-03）。
    /// 全部起真实系统：真实家园（世界模拟、电网内核、传送带 / 管线内核、生产服务、工单与机器施工）、真文件存读档、真 UXML 面板与建造模式状态行。
    /// A 数据（两张新表与源数据逐字段、每类建筑都有耐久 / 帮助行、文本中英、钩子、图鉴、通知类型、图标）；
    /// B 状态矩阵（FGT-ECO-002：26 类建筑逐类触发 建造中 / 工作或待机 / 缺电 / 禁用 / 受损 / 已摧毁 / 升级中 与生产建筑的缺料 / 缺流体 / 输出堵塞，原因码与文字正确、形状两两不同、头顶标记）；
    /// C 改名（正常 / 太长 / 控制字符 / 空 = 默认名 / 没变化；通知与施工队列用名字）；D 启用 / 禁用（生产停、不用电、发电停、电塔不导电、核心与虚影被拒）与同类批量（优先级 / 启停，计数与不能改的原因）；
    /// E 耐久与维修（受损效率不变、通知；没维修件被拒并写明；派维修单 → 预留维修件 → 机器修满；取消全额退回；归零摧毁 → 重建单（废料）→ 机器重建；维修中被摧毁退回）；
    /// F 原地升级（仓库 T1 → T2 → T3 容量随等级、名字 / 过滤保留、T3 不能再升；信号塔 T2 半径；负向：升级中断电照常完工、升级中被摧毁取消并全额退回差额）；
    /// G 仓库只存哪些物品（容量按过滤）与输出口保留 N 件（FG-GAP-093，真实传送带）；H 清空缓存（生产建筑 / 装配站，FG-GAP-095）；I 拆除确认写明流体排空（FG-GAP-094）；
    /// J 效率与最近 10 分钟产出（按桶聚合、停工拉低效率、窗口滑出）；K 上下游（按配方找、按距离、跳转）；L 设置带配方（吸管 / 复制设置 / 复制粘贴，DEBT-FG3LOG07-01）；
    /// M 建造模式状态行（FG-GAP-091）；N 通用面板（真 UXML：各节、按钮、下拉框选中即生效、批量确认、“?”、布局探针）；O 真文件存读档；P 暂停与 0.5x～3x；Q 观察 / 不观察一致；R 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgBuildingOpsSelfCheck）。
    /// </summary>
    public static class FgBuildingOpsSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 5000;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 建筑通用状态、面板与升级")]
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
            Line("\n[建筑通用状态、面板与升级] 通用状态、改名、启停与批量、耐久维修、原地升级、仓库过滤与留底、清空缓存、效率、上下游、设置带配方、面板（FG4-ECO-05）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbuildops-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                BuildingOps.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "状态汇总 / 建筑操作 / 生产统计在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网 / 传送带 / 管线内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckStatusMatrix);
                Step(CheckRename);
                Step(CheckEnableAndBatch);
                Step(CheckDurabilityAndRepair);
                Step(CheckUpgrade);
                Step(CheckUpgradeNegatives);
                Step(CheckStoreFilterAndKeep);
                Step(CheckClearBuffers);
                Step(CheckDemolishFluidConfirm);
                Step(CheckEfficiency);
                Step(CheckLinks);
                Step(CheckSettingsCarryRecipe);
                Step(CheckStatusLine);
                Step(CheckPanel);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"建筑通用自检抛异常：{e}");
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
                BuildingOps.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                BeltPortPanelUIToolkit.Close();
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
            Line($"  · [建筑通用状态、面板与升级] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 400)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            _seq = 5000;
            return FgProductionSelfCheck.NewWorld(seed, observe, scrap);
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（真实放置 / 施工由面板升级、重建与 L 段的吸管放置覆盖），ID 按正式格式“类型#序号”。</summary>
        private static BuildingRecord Built(CampaignState s, string typeId, GridCell pivot)
        {
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, bg.FootprintW, bg.FootprintH, 0),
                Health = BuildingOps.MaxDurability(typeId),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return r;
        }

        /// <summary>推进 <paramref name="sec"/> 游戏秒，每半秒把这些生产建筑的输出缓存清走（当作下游一直在取货：测的是建筑自己的速度，不是输出堵塞）。</summary>
        private static void SecondsDraining(CampaignState s, float sec, params BuildingRecord[] producers)
        {
            for (float t = 0f; t < sec - 0.001f; t += 0.5f)
            {
                Seconds(0.5f);
                foreach (BuildingRecord b in producers)
                {
                    ProductionService.Producer p = P(s, b);
                    if (p != null)
                    {
                        p.Rec.Out = Array.Empty<ItemStackRecord>();
                    }
                }
            }
        }

        private static string OrderInfo(CampaignState s, string buildingId)
        {
            WorkOrderRecord o = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).LastOrDefault(x => x.Kind == WorkOrderKind.Repair && x.TargetId == buildingId);
            return o == null ? "无维修单" : $"维修单 {o.State} / 机器 {o.AssignedMachineLogicId} / {o.FailureReason} / 进度 {o.Progress:0.0}/{o.Duration:0.0}；机器 {MachineRegistry.AllRecords.Count(m => m.IsAlive)} 台";
        }

        private static BuildingRecord Home(CampaignState s, string typeId) => HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + typeId);

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) => FgProductionSelfCheck.P(s, b);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static BuildingStatus St(CampaignState s, BuildingRecord b) => BuildingStatusService.Evaluate(s, b);

        private static int Stock(CampaignState s, string id) => HomeInventory.Stock(s, id);

        private static void GiveKits(CampaignState s, int n)
        {
            ItemCatalog.TryGet(BuildingOps.RepairKitId, out ItemDef kit);
            HomeInventory.Add(s, kit, n, clampToSpace: false);
        }

        /// <summary>开局仓库（Demo 起是待修的残骸）：经面板“重建”派单 → 机器修好。</summary>
        private static BuildingRecord RepairWarehouse(CampaignState s)
        {
            BuildingRecord wh = Home(s, HomeValleyLayout.BuildingTypeWarehouse);
            if (wh == null)
            {
                return null;
            }
            if (wh.ConstructionState == BuildingConstructionState.Damaged)
            {
                BuildingOps.TryOrderRepair(s, wh.BuildingId, out _);
                StepUntil(() => wh.ConstructionState == BuildingConstructionState.Operational, 300);
            }
            return wh;
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string Key(BuildingStatus st) => st.Kind + "/" + st.ReasonCode;

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] bs = lines.Where(l => l.StartsWith("BS\t", StringComparison.Ordinal)).ToArray();
            string[] bt = lines.Where(l => l.StartsWith("BT2\t", StringComparison.Ordinal)).ToArray();
            string[] rs = ConfigSystem.Instance.Tables.TbBuildingService.DataList
                .Select(r => string.Join("\t", "BS", r.TypeId, R(r.MaxDurability), r.RepairKits.ToString(CultureInfo.InvariantCulture), R(r.RepairSeconds), r.CodexId,
                    r.CanDisable.ToString(CultureInfo.InvariantCulture), r.RebuildScrap.ToString(CultureInfo.InvariantCulture), R(r.RebuildSeconds))).ToArray();
            string[] rt = ConfigSystem.Instance.Tables.TbBuildingTier.DataList
                .Select(r => string.Join("\t", "BT2", r.Id, r.TypeId, r.Tier.ToString(CultureInfo.InvariantCulture), r.DiffScrap.ToString(CultureInfo.InvariantCulture), R(r.Seconds),
                    r.UnlockRule, r.UnlockHintKey, R(r.Value), r.ValueKey, R(r.PowerDemand))).ToArray();
            // FG4-ECO-11：等级表多了超控阵列 T1～T3 三行与 powerDemand 列（8 行）；FG5-RND-01：再加仿真实验室 T1～T3（11 行），各等级的研究门槛由改表脚本回写。
            // FG6-DEF-01：再加轻型 / 重型炮塔座 T1～T3（17 行；T2 / T3 的研究门槛同样由改表脚本回写）。
            Expect(code == 0 && bs.Length >= 28 && bs.SequenceEqual(rs) && bt.Length == 17 && bt.SequenceEqual(rt),
                $"A1 fg.TbBuildingService（{bs.Length} 行）与 fg.TbBuildingTier（{bt.Length} 行）与源数据 fgdata_buildops 逐字段一致" + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));
            var missing = ConfigSystem.Instance.Tables.TbBuilding.DataList.Where(b => BuildingOps.Service(b.TypeId) == null).Select(b => b.TypeId).ToList();
            var badCodex = ConfigSystem.Instance.Tables.TbBuildingService.DataList.Where(r => !ConfigSystem.Instance.Tables.TbCodexEntry.DataList.Any(c => c.Id == r.CodexId)).Select(r => r.TypeId).ToList();
            Expect(missing.Count == 0 && badCodex.Count == 0 && !BuildingOps.CanDisableType("core") && BuildingOps.HasTiers("warehouse") && BuildingOps.MaxTier("warehouse") == 3
                   && BuildingOps.HasTiers("signal_tower") && BuildingOps.MaxTier("signal_tower") == 2 && !BuildingOps.HasTiers("refinery_furnace")
                   && Mathf.Approximately(BuildingOps.TierRow("signal_tower", 2).Value, SignalCoverageService.TowerT2Radius),
                $"A2 每类建筑都有耐久 / 维修件 / “?”条目（缺：{string.Join("、", missing)}；帮助条目悬空：{string.Join("、", badCodex)}）；核心不能禁用；仓库 3 级、信号塔 2 级（T2 半径 = 覆盖调参）");
            string[] keys =
            {
                "status.building.name", "bp.ident", "bp.rename", "bp.rename_too_long", "bp.disable", "bp.enable", "bp.batch_enable_all", "bp.batch_confirm_line",
                "bp.durability", "bp.repair", "bp.rebuild", "bp.repair_no_kits", "bp.upgrade", "bp.upgrade_line", "bp.efficiency", "bp.output10", "bp.upstream",
                "bp.downstream", "bp.clear", "bp.store_line", "bs.reason.destroyed", "bs.reason.worn", "bs.reason.disabled", "bs.reason.upgrading", "bs.reason.ghost",
                "ui.build.confirm_fluid_line", "logistics.port.keep_n", "logistics.port.state.keeping", "ui.build.last_result", "plan.settings.recipe",
                "building.notify.damaged", "building.notify.destroyed", "codex.build.building.body", "building.tier.value.warehouse", "prod.panel.stack_fluid_held", "bp.open_grid",
            };
            bool zh = keys.All(k => GameText.Has(k) && !GameText.ContainsMarker(GameText.Get(k)));
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = keys.All(k => !GameText.ContainsMarker(GameText.Get(k)) && GameText.Get(k).Length > 0 && !ContainsCjk(GameText.Get(k)));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.BuildingPanelFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.BuildingFirstUpgrade)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.BuildingFirstDamaged);
            bool codex = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.Any(x => x.Id == "codex.build.building" && x.Hooks.Contains(GuidanceHooks.BuildingPanelFirstOpen));
            bool notify = NotificationCatalog.TryGetType("building_damaged", out NotifyTypeDef t1) && t1.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("building_destroyed", out NotifyTypeDef t2) && t2.Tier == NotifyLevel.Urgent;
            bool icons = File.Exists("Assets/GameRes/Raw/UI/Icons/icon_state_disabled.png") && File.Exists("Assets/GameRes/Raw/UI/Icons/icon_state_upgrading.png")
                         && File.Exists("Assets/GameRes/Raw/UI/Icons/icon_state_worn.png") && File.Exists("Assets/GameRes/Raw/UI/Icons/icon_state_worn.png.meta");
            Expect(zh && en && hooks && codex && notify && icons, $"A3 新文本中英都有（{keys.Length} 个）；3 个引导钩子；图鉴“建筑”；“建筑受损”（警告）/“建筑被摧毁”（紧急）通知；三张新状态图标（禁用 / 升级中 / 受损）（{zh}/{en}/{hooks}/{codex}/{notify}/{icons}）");
        }

        // FG6-DEF-01：按单精度往返写法比（表里存 float；1.15 这类不能精确表示的值按 double 展开会多出尾数，与 Python repr 对不上）。
        private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture) is string x && !x.Contains(".") && !x.Contains("E") ? x + ".0" : v.ToString("R", CultureInfo.InvariantCulture);

        private static bool ContainsCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── B 状态矩阵（FGT-ECO-002）────────────────────────────────────────────────

        private static void CheckStatusMatrix()
        {
            CampaignState s = NewWorld(5521, scrap: 2000);
            FgProductionSelfCheck.PowerUp(s);
            var problems = new List<string>();
            var seen = new Dictionary<string, HashSet<BuildingStatusKind>>();
            int cases = 0;
            int rebuildable = 0;

            void See(string type, BuildingRecord b, BuildingStatusKind want, string label, string codePrefix = null)
            {
                cases++;
                BuildingStatus st = St(s, b);
                bool ok = st.Kind == want && !string.IsNullOrEmpty(st.Reason) && !GameText.ContainsMarker(st.Reason) && (codePrefix == null || (st.ReasonCode ?? string.Empty).StartsWith(codePrefix, StringComparison.Ordinal));
                if (!ok)
                {
                    problems.Add($"{type} {label}：得到 {Key(st)}「{st.Reason}」，应为 {want}{(codePrefix != null ? "/" + codePrefix : string.Empty)}");
                }
                if (!seen.TryGetValue(type, out HashSet<BuildingStatusKind> set))
                {
                    seen[type] = set = new HashSet<BuildingStatusKind>();
                }
                if (ok)
                {
                    set.Add(want);
                }
            }

            foreach (GameConfig.fg.Building row in ConfigSystem.Instance.Tables.TbBuilding.DataList)
            {
                string type = row.TypeId;
                BuildingRecord b = type == HomeValleyLayout.BuildingTypeCore ? Home(s, type) : FindOrPlace(s, type);
                if (b == null)
                {
                    problems.Add($"{type}：放不下");
                    continue;
                }
                // 1) 建造中（虚影）。
                BuildingConstructionState orig = b.ConstructionState;
                if (type != HomeValleyLayout.BuildingTypeCore)
                {
                    b.ConstructionState = BuildingConstructionState.Planned;
                    HomeValleyPowerGrid.Recompute(s);
                    See(type, b, BuildingStatusKind.Building, "虚影", "ghost");
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(type);
                    HomeValleyPowerGrid.Recompute(s);
                    FgProductionSelfCheck.Resync(s);
                }
                // 2) 功能状态（工作 / 待机 / 缺料……任一非通用状态）。
                ProductionService.Producer p = P(s, b);
                if (p != null)
                {
                    ProductionService.Step(s, ProductionService.SyncTicks(GameClock.StepHz), GameClock.StepHz);
                }
                BuildingStatus fn = St(s, b);
                if (fn.Kind == BuildingStatusKind.Disabled || fn.Kind == BuildingStatusKind.Destroyed || fn.Kind == BuildingStatusKind.Building
                    || fn.Kind == BuildingStatusKind.Upgrading || fn.Kind == BuildingStatusKind.Damaged || string.IsNullOrEmpty(fn.Reason))
                {
                    problems.Add($"{type} 运转中：得到 {Key(fn)}「{fn.Reason}」（应为功能状态）");
                }
                else
                {
                    See(type, b, fn.Kind, "运转中");
                }
                // 3) 缺电（用电建筑，不在任何电网里）。
                if (BuildingOps.HasPriority(type) && p == null)
                {
                    b.PowerState = BuildingPowerState.Unpowered;
                    See(type, b, BuildingStatusKind.NoPower, "未接电", "power.");
                    HomeValleyPowerGrid.Recompute(s);
                }
                // 4) 受损（照常工作）→ 5) 禁用 → 6) 已摧毁 → 重建状态复原。
                if (type != HomeValleyLayout.BuildingTypeCore)
                {
                    BuildingOps.ApplyDamage(s, b.BuildingId, BuildingOps.MaxDurability(type) * 0.3f);
                    BuildingStatus worn = St(s, b);
                    bool wornOk = worn.Kind == BuildingStatusKind.Damaged ? worn.ReasonCode == "worn" : worn.Reason.Contains(Mathf.RoundToInt(BuildingOps.Durability(b)).ToString(CultureInfo.InvariantCulture));
                    if (!wornOk || !BuildingOps.IsWorn(b))
                    {
                        problems.Add($"{type} 受损：{Key(worn)}「{worn.Reason}」");
                    }
                    else
                    {
                        seen[type].Add(BuildingStatusKind.Damaged);
                        cases++;
                    }
                    if (BuildingOps.TrySetEnabled(s, b.BuildingId, false, out _))
                    {
                        See(type, b, BuildingStatusKind.Disabled, "禁用", "disabled");
                        BuildingOps.TrySetEnabled(s, b.BuildingId, true, out _);
                    }
                    else
                    {
                        problems.Add($"{type} 禁用被拒");
                    }
                    if (HomeValleyPowerGrid.ApplyBuildingDestroyed(s, b.BuildingId))
                    {
                        See(type, b, BuildingStatusKind.Destroyed, "摧毁", "destroyed");
                        // 审查 P1：每一类可摧毁的建筑都要有重建路径（装配站 / 解析台 / 维修台没有修复造价也不能新建，靠表里的重建造价）。
                        if (BuildingOps.RebuildScrapFor(b) <= 0 || St(s, b).Reason.Contains("不能重建"))
                        {
                            problems.Add($"{type} 被摧毁后没有重建造价（{BuildingOps.RebuildScrapFor(b)}）：「{St(s, b).Reason}」");
                        }
                        else
                        {
                            rebuildable++;
                        }
                    }
                    else
                    {
                        problems.Add($"{type} 摧毁入口拒绝");
                    }
                    b.ConstructionState = orig == BuildingConstructionState.Damaged ? BuildingConstructionState.Operational : orig;
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(type);
                    HomeValleyPowerGrid.Recompute(s);
                    FgProductionSelfCheck.Resync(s);
                }
                // 7) 升级中（有等级 / 升级路线的建筑）。
                if (BuildingOps.HasTiers(type) || GridContent.TryGetUpgrade(type, out _))
                {
                    GridOpResult up = HomeGridService.TryUpgrade(s, b.BuildingId);
                    if (up.Success)
                    {
                        See(type, b, BuildingStatusKind.Upgrading, "升级中", "upgrading");
                        HomeGridService.TryToggleDemolish(s, up.BuildingId);
                    }
                    else
                    {
                        problems.Add($"{type} 升级被拒：{up.Describe()}");
                    }
                }
            }
            // FG6-DEF-01：逐类矩阵里多了两座炮塔座（耗电 8 + 15、优先级 1，电网吃紧时先保炮塔）——再接一座发电机 2，后面的生产建筑不因电网吃紧报缺电（测的是生产状态，电网分配由 FG4-ECO-04 覆盖）。
            GridCell? extraGen = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 24f);
            if (extraGen.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "matrix_gen_def", extraGen.Value);
                HomeValleyPowerGrid.Recompute(s);
            }
            // 生产建筑的专属状态：待机（没选配方）/ 缺料 / 缺流体 / 输出堵塞（真实推进得到）。
            BuildingRecord furnace = FindOrPlace(s, "refinery_furnace");
            ProductionService.Producer fp = P(s, furnace);
            ProductionService.TrySetRecipe(s, furnace.BuildingId, null, out _);
            ProductionService.Step(s, 3, GameClock.StepHz);
            See("refinery_furnace", furnace, BuildingStatusKind.Idle, "没选配方", "prod.NoRecipe");
            ProductionService.TrySetRecipe(s, furnace.BuildingId, "alloy_ore", out _);
            ProductionService.Step(s, 3, GameClock.StepHz);
            See("refinery_furnace", furnace, BuildingStatusKind.NoMaterial, "缺料", "prod.MissingItem");
            fp.Rec.Out = new[] { new ItemStackRecord { ItemId = "alloy", Amount = ProductionService.OutCapacity(fp, ItemCatalog.Items.First(i => i.Id == "alloy")) } };
            fp.Rec.In = new[] { new ItemStackRecord { ItemId = "metal_ore", Amount = 10 } };
            ProductionService.Step(s, 3, GameClock.StepHz);
            See("refinery_furnace", furnace, BuildingStatusKind.OutputBlocked, "输出放不下", "prod.NoRoom");
            BuildingRecord tower = FindOrPlace(s, "refinery_tower");
            ProductionService.Step(s, 3, GameClock.StepHz);
            See("refinery_tower", tower, BuildingStatusKind.NoFluid, "没有原油", "prod.MissingFluid");
            // 形状：11 种状态两两不同的形状类；头顶标记：禁用 / 升级中 / 受损各有自己的图标。
            var shapes = ((UiEntityStatus[])Enum.GetValues(typeof(UiEntityStatus))).Select(UiStatusIcon.ShapeOf).Distinct().Count();
            BuildingRecord pole = FindOrPlace(s, "power_pole");
            BuildingOps.TrySetEnabled(s, pole.BuildingId, false, out _);
            string disIcon = HomeValleyController.StateIconFor(pole);
            BuildingOps.TrySetEnabled(s, pole.BuildingId, true, out _);
            BuildingOps.ApplyDamage(s, pole.BuildingId, 10f);
            string wornIcon = HomeValleyController.StateIconFor(pole);
            GridOpResult pu = HomeGridService.TryUpgrade(s, pole.BuildingId);
            string upIcon = HomeValleyController.StateIconFor(pole);
            if (pu.Success)
            {
                HomeGridService.TryToggleDemolish(s, pu.BuildingId);
            }
            int types = ConfigSystem.Instance.Tables.TbBuilding.DataList.Count;
            int richTypes = seen.Count(kv => kv.Value.Count >= 4);
            Expect(problems.Count == 0 && seen.Count == types && richTypes >= types - 1 && rebuildable == types - 1 && shapes == UiStatusIcon.StatusCount && UiStatusIcon.StatusCount == 11
                   && disIcon == ContentIcons.StateDisabled && wornIcon == ContentIcons.StateWorn && upIcon == ContentIcons.StateUpgrading,
                $"B FGT-ECO-002：{types} 类建筑逐类触发 {cases} 个状态（建造中 / 运转 / 缺电 / 受损 / 禁用 / 已摧毁 / 升级中，生产建筑另有待机 / 缺料 / 缺流体 / 输出堵塞），原因码与文字都对；" +
                $"非核心的 {rebuildable}/{types - 1} 类被摧毁后都有重建造价；11 种状态形状两两不同；头顶标记 禁用 {disIcon} / 受损 {wornIcon} / 升级中 {upIcon}" + (problems.Count == 0 ? string.Empty : "\n问题：" + string.Join("；", problems.Take(12)))
                + $"（覆盖 {seen.Count}/{types} 类，状态少于 4 种的：{string.Join("、", seen.Where(kv => kv.Value.Count < 4).Select(kv => kv.Key + "=" + string.Join("+", kv.Value)))}）");
        }

        /// <summary>找一块能放下这类建筑的空地登记一座（满足资源点要求的按地形改脚下），失败时 null。按种子地形找空地（B25）。</summary>
        private static BuildingRecord FindOrPlace(CampaignState s, string type)
        {
            BuildingRecord existing = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == type && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b));
            if (existing != null && type != "generator")
            {
                if (existing.ConstructionState == BuildingConstructionState.Damaged)
                {
                    existing.ConstructionState = BuildingConstructionState.Operational;
                    existing.Health = BuildingOps.MaxDurability(type);
                    HomeValleyPowerGrid.Recompute(s);
                    FgProductionSelfCheck.Resync(s);
                }
                return existing;
            }
            if (!GridContent.TryGetBuilding(type, out BuildingGrid g))
            {
                return null;
            }
            string terrain = g.RequiredTerrain == "any" ? null : g.RequiredTerrain.Split('|')[0];
            GridCell? at = null;
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 7; r <= 30 && at == null; r++)
            {
                for (int a = 0; a < 48 && at == null; a++)
                {
                    float ang = a * 7.5f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * r), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * r));
                    if (terrain != null)
                    {
                        if (!HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            continue;
                        }
                        FgProductionSelfCheck.SetFootprint(s, type, c, terrain);
                    }
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        at = c;
                    }
                }
            }
            return at.HasValue ? Built(s, type, at.Value) : null;
        }

        // ── C 改名 ───────────────────────────────────────────────────────────────

        private static void CheckRename()
        {
            CampaignState s = NewWorld(5531);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            string def = HomeGridService.DisplayName("refinery_furnace");
            bool ok1 = BuildingOps.TryRename(s, f.BuildingId, "  一号炉  ", out string m1) && f.CustomName == "一号炉" && BuildingOps.NameOf(f) == "一号炉" && m1.Contains("一号炉");
            bool same = !BuildingOps.TryRename(s, f.BuildingId, "一号炉", out string m2) && m2.Contains("没有变化");
            bool tooLong = !BuildingOps.TryRename(s, f.BuildingId, new string('炉', BuildingOps.MaxNameChars + 1), out string m3) && m3.Contains(BuildingOps.MaxNameChars.ToString(CultureInfo.InvariantCulture)) && f.CustomName == "一号炉";
            bool control = !BuildingOps.TryRename(s, f.BuildingId, "a\nb", out string m4) && m4.Contains("换行") && f.CustomName == "一号炉";
            bool label = Campaign.Feedback.FeedbackCues.BuildingLabel(f.BuildingId) == "一号炉";
            string ident = BuildingOps.IdentLine(f);
            bool reset = BuildingOps.TryRename(s, f.BuildingId, "", out string m5) && f.CustomName == null && BuildingOps.NameOf(f) == def && m5.Contains(def);
            bool asDefault = BuildingOps.TryRename(s, f.BuildingId, "二号", out _) && BuildingOps.TryRename(s, f.BuildingId, def, out _) && f.CustomName == null;
            Expect(ok1 && same && tooLong && control && label && reset && asDefault && ident.Contains(def) && ident.Contains("#"),
                $"C 改名：去首尾空白后“一号炉”生效（通知标签也用它）；没变化 / 超过 {BuildingOps.MaxNameChars} 字 / 含换行被拒并写原因（“{m3}”“{m4}”）；留空或写回类型名 = 默认名；身份行“{ident}”（{ok1}/{same}/{tooLong}/{control}/{label}/{reset}/{asDefault}）");
        }

        // ── D 启用 / 禁用与批量 ───────────────────────────────────────────────────────

        private static void CheckEnableAndBatch()
        {
            CampaignState s = NewWorld(5541);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f1 = FindOrPlace(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f1.BuildingId, "alloy_scrap", out _);
            P(s, f1).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 40 } };
            Seconds(4f);
            long before = P(s, f1).Rec.Completed;
            float demandOn = HomeValleyPowerGrid.GetSummary(s).TotalDemand;
            bool dis = BuildingOps.TrySetEnabled(s, f1.BuildingId, false, out string dm);
            Seconds(8f);
            bool stopped = P(s, f1).Rec.Completed == before && P(s, f1).State == ProdState.Disabled && HomeValleyPowerGrid.GetSummary(s).TotalDemand < demandOn - 0.01f;
            bool again = !BuildingOps.TrySetEnabled(s, f1.BuildingId, false, out _);
            bool en = BuildingOps.TrySetEnabled(s, f1.BuildingId, true, out _);
            Seconds(8f);
            bool resumed = P(s, f1).Rec.Completed > before;
            BuildingRecord core = Home(s, HomeValleyLayout.BuildingTypeCore);
            bool coreNo = !BuildingOps.TrySetEnabled(s, core.BuildingId, false, out string cm) && cm.Contains("核心");
            BuildingRecord gen = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
            float supply = HomeValleyPowerGrid.GetSummary(s).TotalSupply;
            BuildingOps.TrySetEnabled(s, gen.BuildingId, false, out _);
            bool genOff = HomeValleyPowerGrid.GetSummary(s).TotalSupply < supply - 0.01f;
            BuildingOps.TrySetEnabled(s, gen.BuildingId, true, out _);
            Expect(dis && dm.Contains("已禁用") && stopped && again && en && resumed && coreNo && genOff,
                $"D1 启用 / 禁用（所有能禁用的建筑，DEBT-FG4ECO04-04）：精炼炉禁用后不再出产、不用电，启用后接着做；重复禁用不算改动；发电机禁用后电网少了它的供电；核心被拒（“{cm}”）（{dis}/{stopped}/{en}/{resumed}/{coreNo}/{genOff}）");

            // 同类批量：3 座精炼炉，其中 1 座在施工（不能改）。
            BuildingRecord f2 = FindOrPlace2(s, "refinery_furnace");
            BuildingRecord f3 = FindOrPlace2(s, "refinery_furnace");
            f3.ConstructionState = BuildingConstructionState.Planned;
            HomeValleyPowerGrid.Recompute(s);
            BuildingOps.BatchPlan pd = BuildingOps.PlanBatch(s, f1.BuildingId, BuildingOps.BatchOp.Disable, f1.PowerPriority);
            int changed = BuildingOps.ApplyBatch(s, pd, out string bm);
            bool batchDis = changed == 2 && pd.Refused == 1 && !string.IsNullOrEmpty(pd.FirstRefusal) && BuildingOps.IsDisabled(f1) && BuildingOps.IsDisabled(f2) && !BuildingOps.IsDisabled(f3) && bm.Contains("2");
            BuildingOps.BatchPlan pe = BuildingOps.PlanBatch(s, f2.BuildingId, BuildingOps.BatchOp.Enable, 0);
            int en2 = BuildingOps.ApplyBatch(s, pe, out _);
            BuildingOps.TrySetPriority(s, f1.BuildingId, 1, out _);
            BuildingOps.BatchPlan pp = BuildingOps.PlanBatch(s, f1.BuildingId, BuildingOps.BatchOp.Priority, 1);
            int pc = BuildingOps.ApplyBatch(s, pp, out _);
            bool prio = pc == 2 && pp.Same == 1 && f2.PowerPriority == 1 && f3.PowerPriority == 1 && en2 == 2 && !BuildingOps.IsDisabled(f1);
            BuildingRecord relay = FindOrPlace(s, "signal_relay");
            bool noPrio = !BuildingOps.TrySetPriority(s, relay.BuildingId, 2, out string nm) && nm.Contains("不用电");
            Expect(batchDis && prio && noPrio,
                $"D2 同类批量（FG04 第 4 节）：3 座精炼炉“全部禁用”改 2 座、施工中的 1 座不能改并写原因（“{pd.FirstRefusal}”）；“全部启用”恢复；“全部设为优先级 1”改 2 座、1 座本来就是；不用电的建筑没有优先级（“{nm}”）");
        }

        private static BuildingRecord FindOrPlace2(CampaignState s, string type)
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, type, 8f, 26f);
            return at.HasValue ? Built(s, type, at.Value) : null;
        }

        // ── E 耐久与维修 ─────────────────────────────────────────────────────────────

        private static void CheckDurabilityAndRepair()
        {
            CampaignState s = NewWorld(5551, scrap: 600);
            FgProductionSelfCheck.PowerUp(s);
            RepairWarehouse(s); // 维修件存在仓库里（核心缓存只收废料）：先把开局的仓库修好。
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 200 } };
            SecondsDraining(s, 2f, f);
            long c0 = P(s, f).Rec.Completed;
            SecondsDraining(s, 60f, f);
            long full = P(s, f).Rec.Completed - c0;
            int dmgNotes = NotifyCount("building_damaged");
            bool destroyedNow = BuildingOps.ApplyDamage(s, f.BuildingId, 60f);
            long c1 = P(s, f).Rec.Completed;
            SecondsDraining(s, 60f, f);
            long worn = P(s, f).Rec.Completed - c1;
            bool sameRate = !destroyedNow && Math.Abs(worn - full) <= 1 && full >= 8 && BuildingOps.IsWorn(f) && St(s, f).Kind != BuildingStatusKind.Destroyed
                            && NotifyCount("building_damaged") > dmgNotes && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildingFirstDamaged);
            Expect(sameRate, $"E1 受损效率不变（FGR-ECO-013）：耐久 {BuildingOps.Durability(f):0}/{BuildingOps.MaxDurability("refinery_furnace"):0} 时 60 游戏秒出 {worn} 份 = 满耐久时 {full} 份（相差不超过 1 份是周期相位）；发“建筑受损”警告与引导钩子");

            int kits = BuildingOps.RepairKitsFor(f);
            bool noKits = !BuildingOps.TryOrderRepair(s, f.BuildingId, out string nk) && nk.Contains("维修件") && nk.Contains("零件工坊");
            GiveKits(s, kits + 3);
            int stock0 = Stock(s, BuildingOps.RepairKitId);
            bool ordered = BuildingOps.TryOrderRepair(s, f.BuildingId, out string om) && Stock(s, BuildingOps.RepairKitId) == stock0 - kits;
            bool busy = !BuildingOps.TryOrderRepair(s, f.BuildingId, out string bm) && bm.Contains("已经有维修单");
            bool cancel = BuildingOps.TryCancelRepair(s, f.BuildingId, out _) && Stock(s, BuildingOps.RepairKitId) == stock0;
            bool reorder = BuildingOps.TryOrderRepair(s, f.BuildingId, out _);
            bool repaired = reorder && StepUntil(() => !BuildingOps.IsWorn(f), 300);
            bool consumed = Stock(s, BuildingOps.RepairKitId) == stock0 - kits && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null
                            && Mathf.Approximately(f.Health, BuildingOps.MaxDurability("refinery_furnace"));
            Expect(kits >= 1 && noKits && ordered && busy && cancel && repaired && consumed,
                $"E2 维修（机器执行、消耗维修件）：缺 {kits} 件时被拒并写明从哪来（“{nk}”）；派单即预留 {kits} 件，重复派被拒，取消全额退回；机器上门修满耐久、维修件消耗（{noKits}/{ordered}/{busy}/{cancel}/{repaired}/{consumed}；{OrderInfo(s, f.BuildingId)}）");

            // 耐久归零 → 摧毁（虚影）→ 重建单（废料）→ 机器重建；维修中被摧毁 → 维修件退回。
            BuildingOps.ApplyDamage(s, f.BuildingId, 40f);
            GiveKits(s, 5);
            int k1 = Stock(s, BuildingOps.RepairKitId);
            BuildingOps.TryOrderRepair(s, f.BuildingId, out _);
            int reserved = k1 - Stock(s, BuildingOps.RepairKitId);
            int destroyedNotes = NotifyCount("building_destroyed");
            bool destroyed = BuildingOps.ApplyDamage(s, f.BuildingId, 999f) && f.ConstructionState == BuildingConstructionState.Damaged && St(s, f).Kind == BuildingStatusKind.Destroyed;
            bool refunded = reserved > 0 && Stock(s, BuildingOps.RepairKitId) == k1 && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null
                            && NotifyCount("building_destroyed") > destroyedNotes;
            int scrapBefore = Mathf.FloorToInt(s.Scrap);
            int cost = BuildingOps.RebuildScrapFor(f);
            bool rebuildOrdered = BuildingOps.TryOrderRepair(s, f.BuildingId, out string rm) && rm.Contains("重建");
            bool rebuilt = rebuildOrdered && StepUntil(() => f.ConstructionState == BuildingConstructionState.Operational, 400);
            bool rebuiltFull = rebuilt && Mathf.Approximately(f.Health, BuildingOps.MaxDurability("refinery_furnace")) && Mathf.FloorToInt(s.Scrap) <= scrapBefore - cost + 1
                               && P(s, f).Recipe?.Id == "alloy_scrap";
            Expect(destroyed && refunded && rebuildOrdered && rebuilt && rebuiltFull,
                $"E3 耐久归零 = 摧毁（留下虚影、设置保留，发“建筑被摧毁”）；维修中被摧毁 → 维修单取消、维修件 {reserved} 件全额退回；“重建”派单（废料 ×{cost}）→ 机器重建、耐久回满、配方还在（{destroyed}/{refunded}/{rebuilt}/{rebuiltFull}；“{rm}”；{OrderInfo(s, f.BuildingId)}）");

            // E4（审查 P1）：开局预置、不能新建也没有 Demo 修复造价的三类（装配站 / 解析台 / 维修台）被摧毁后能重建（表里的重建造价），不会永久丢失。
            var lost = new List<string>();
            foreach (string t in new[] { "assembly_station", "analysis_bench", "repair_bay" })
            {
                BuildingRecord pb = Home(s, t);
                if (pb == null)
                {
                    lost.Add(t + "：开局没有");
                    continue;
                }
                if (pb.ConstructionState == BuildingConstructionState.Damaged)
                {
                    pb.ConstructionState = BuildingConstructionState.Operational;
                    pb.Health = BuildingOps.MaxDurability(t);
                }
                s.Scrap = Math.Max(s.Scrap, 300);
                bool gone = BuildingOps.ApplyDamage(s, pb.BuildingId, 999f) && pb.ConstructionState == BuildingConstructionState.Damaged;
                int rc = BuildingOps.RebuildScrapFor(pb);
                string why = St(s, pb).Reason;
                bool back = gone && rc == BuildingOps.Service(t).RebuildScrap && rc > 0 && BuildingOps.TryOrderRepair(s, pb.BuildingId, out _)
                            && StepUntil(() => pb.ConstructionState == BuildingConstructionState.Operational, 400)
                            && Mathf.Approximately(pb.Health, BuildingOps.MaxDurability(t));
                if (!back)
                {
                    lost.Add($"{t}：摧毁 {gone}、重建造价 {rc}、原因「{why}」、{OrderInfo(s, pb.BuildingId)}");
                }
            }
            Expect(lost.Count == 0, "E4 装配站 / 解析台 / 维修台被摧毁 → 原因写重建造价 → 面板“重建”派单 → 机器按表里的重建造价重建、耐久回满（不会永久丢失）" +
                                    (lost.Count == 0 ? string.Empty : "：" + string.Join("；", lost)));

            // E5（审查 P2，RTS 右键情境命令）：选中的机器右键受损 / 已摧毁的建筑 = 这台机器按实例去修 / 重建（与面板同一张单）。
            BuildingRecord worn2 = FindOrPlace(s, "refinery_furnace");
            BuildingOps.ApplyDamage(s, worn2.BuildingId, 30f);
            GiveKits(s, 5);
            MachineRecord fixer = null;
            HomeValleyWorkOrders.WorkOrderOpResult assigned = default;
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId))
            {
                assigned = HomeValleyWorkOrders.TryAssignRepair(s, worn2.BuildingId, m.LogicId);
                if (assigned.Success)
                {
                    fixer = m;
                    break;
                }
            }
            WorkOrderRecord wo = HomeValleyWorkOrders.FindActiveRepair(s, worn2.BuildingId);
            int reservedKits = wo?.ReservedItemAmount ?? 0;
            bool byThis = fixer != null && wo != null && wo.AssignedMachineLogicId == fixer.LogicId && reservedKits > 0;
            bool fixedByIt = byThis && StepUntil(() => !BuildingOps.IsWorn(worn2), 300);
            BuildingRecord fullAsm = Home(s, "assembly_station");
            HomeValleyWorkOrders.WorkOrderOpResult refuseFull = fixer != null ? HomeValleyWorkOrders.TryAssignRepair(s, fullAsm.BuildingId, fixer.LogicId) : default;
            Expect(byThis && fixedByIt && !refuseFull.Success && HomeValleyWorkOrders.DescribeCommandFailure(refuseFull.FailureReason).Contains("不用维修"),
                $"E5 右键情境命令按实例维修：机器 #{fixer?.LogicId} 接下受损精炼炉的维修单（预留维修件 {reservedKits}）并修满；满耐久的建筑右键被拒并写明原因（“{HomeValleyWorkOrders.DescribeCommandFailure(refuseFull.FailureReason)}”）");

            // E6（审查 P2）：归还核心不会被摧毁，耐久打到 0 时读数是 0（不是旧存档兼容规则的“满耐久”），算受损、可以维修。
            BuildingRecord core = Home(s, HomeValleyLayout.BuildingTypeCore);
            bool coreDestroyed = BuildingOps.ApplyDamage(s, core.BuildingId, 99999f);
            bool coreZero = !coreDestroyed && core.ConstructionState == BuildingConstructionState.Operational && Mathf.RoundToInt(BuildingOps.Durability(core)) == 0
                            && BuildingOps.IsWorn(core) && BuildingOps.RepairKitsFor(core) == BuildingOps.Service("core").RepairKits;
            core.Health = BuildingOps.MaxDurability("core");
            Expect(coreZero, $"E6 核心耐久打到 0：不摧毁、读数 0 / {BuildingOps.MaxDurability("core"):0}、算受损、修满要 {BuildingOps.Service("core").RepairKits} 件维修件（{coreDestroyed}/{coreZero}）");
        }

        // ── F 原地升级 ───────────────────────────────────────────────────────────────

        private static void CheckUpgrade()
        {
            CampaignState s = NewWorld(5561, scrap: 900);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord wh = RepairWarehouse(s);
            if (wh == null || wh.ConstructionState != BuildingConstructionState.Operational)
            {
                Fail("F 开局仓库没修好");
                return;
            }
            ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap);
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            int cap1 = HomeInventory.Capacity(s, alloy);
            BuildingOps.TryRename(s, wh.BuildingId, "主仓", out _);
            BuildingOps.TrySetStoreFilter(s, wh.BuildingId, null, out _);
            int prio = wh.PowerPriority;
            GridOpResult u1 = HomeGridService.TryUpgrade(s, wh.BuildingId);
            BuildingRecord ghost = u1.Success ? HomeGridService.FindBuilding(s, u1.BuildingId) : null;
            bool ghostOk = ghost != null && ghost.Tier == 2 && ghost.BuildingTypeId == "warehouse" && ghost.ConstructionRequired == BuildingOps.TierRow("warehouse", 2).DiffScrap
                           && St(s, wh).Kind == BuildingStatusKind.Upgrading && HomeInventory.Capacity(s, alloy) == cap1;
            bool done1 = ghostOk && StepUntil(() => BuildingOps.TierOf(Home(s, "warehouse")) == 2, 400);
            wh = Home(s, "warehouse");
            int cap2 = HomeInventory.Capacity(s, alloy);
            bool kept = wh.CustomName == "主仓" && wh.PowerPriority == prio && HomeGridService.FindRelocationGhost(s, wh.BuildingId) == null
                        && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildingFirstUpgrade);
            GridOpResult u2 = HomeGridService.TryUpgrade(s, wh.BuildingId);
            bool done2 = u2.Success && StepUntil(() => BuildingOps.TierOf(Home(s, "warehouse")) == 3, 400);
            wh = Home(s, "warehouse");
            int cap3 = HomeInventory.Capacity(s, alloy);
            bool top = !HomeGridService.CanUpgradeNow(s, wh, out _, out _, out GridReason why) && why.Code == GridBlockReason.NoUpgrade;
            Expect(cap1 == 300 && done1 && cap2 == 600 && kept && done2 && cap3 == 1000 && top,
                $"F1 仓库原地升级（FGR-ECO-012 / DEBT-FG4ECO01-03）：T1 容量 {cap1} → 升级虚影带 T2、差额 {ghost?.ConstructionRequired} 废料、期间照常运转显示“升级中” → 机器施工完成 T2 {cap2} → T3 {cap3}；名字 / 优先级保留；T3 没有更高等级（{ghostOk}/{done1}/{kept}/{done2}/{top}）");

            // 信号塔 T2：覆盖半径 300 → 500（DEBT-FG1SIG04-03）。
            BuildingRecord tower = Home(s, HomeValleyLayout.BuildingTypeSignalTower);
            BuildingOps.TryOrderRepair(s, tower.BuildingId, out _);
            StepUntil(() => tower.ConstructionState == BuildingConstructionState.Operational, 400);
            string r1 = St(s, tower).Reason;
            GridOpResult ut = HomeGridService.TryUpgrade(s, tower.BuildingId);
            bool towerDone = ut.Success && StepUntil(() => BuildingOps.TierOf(Home(s, HomeValleyLayout.BuildingTypeSignalTower)) == 2, 400);
            tower = Home(s, HomeValleyLayout.BuildingTypeSignalTower);
            string r2 = St(s, tower).Reason;
            Expect(towerDone && r2.Contains("500") && (r1.Contains("300") || St(s, tower).Kind != BuildingStatusKind.Working),
                $"F2 信号塔 T1 → T2：覆盖半径读数“{r1}” → “{r2}”（信号覆盖服务按等级取半径）");
        }

        private static void CheckUpgradeNegatives()
        {
            CampaignState s = NewWorld(5571, scrap: 900);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord wh = RepairWarehouse(s);
            if (wh == null || wh.ConstructionState != BuildingConstructionState.Operational)
            {
                Fail("F3 开局仓库没修好");
                return;
            }
            // 升级中断电（审查 P1：上一版只关了发电机，核心基础供电仍够仓库用，仓库其实一直有电）：
            // 关掉核心以外的发电，把开局的用电建筑（信号塔 / 装配站 / 解析台 / 维修台）都投入运转，仓库优先级设为 4（缺电时最先停）→
            // 核心基础供电不够分，仓库真正停电（PowerState = Brownout）→ 状态“升级中”、原因里写“缺电” → 升级照常完工 → 来电后恢复。
            var gens = s.BuildingRecords.Where(b => HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId) && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                                                    && b.ConstructionState == BuildingConstructionState.Operational).ToList();
            foreach (BuildingRecord g in gens)
            {
                BuildingOps.TrySetEnabled(s, g.BuildingId, false, out _);
            }
            foreach (string t in new[] { HomeValleyLayout.BuildingTypeSignalTower, "assembly_station", "analysis_bench", "repair_bay" })
            {
                BuildingRecord c = Home(s, t);
                if (c != null && c.ConstructionState == BuildingConstructionState.Damaged)
                {
                    c.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：开局残骸直接视为已修好（修复路径由 E / F1 覆盖）
                    c.Health = BuildingOps.MaxDurability(t);
                }
            }
            // 电网按优先级分配、分不下的跳过（小的低优先级用电仍可能分到余量）：再接一座刻录台（10）和回收站（6），把核心基础供电的余量吃掉。
            FindOrPlace(s, "firmware_burner");
            FindOrPlace(s, "recycler");
            HomeValleyPowerGrid.TrySetPriority(s, wh.BuildingId, 4);
            HomeValleyPowerGrid.Recompute(s);
            BuildingPowerState powerBefore = wh.PowerState;
            HomeValleyPowerGrid.GridSummary sum = HomeValleyPowerGrid.GetSummary(s);
            string diag = $"供 {sum.TotalSupply:0} / 需 {sum.TotalDemand:0}；" + string.Join("、", s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId && (HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId) || HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId))).Select(b => $"{b.BuildingTypeId}:{b.ConstructionState}/{b.PowerState}/p{b.PowerPriority}"));
            GridOpResult u = HomeGridService.TryUpgrade(s, wh.BuildingId);
            BuildingStatus during = St(s, wh);
            BuildingPowerState powerDuring = wh.PowerState;
            bool finished = u.Success && StepUntil(() => BuildingOps.TierOf(Home(s, "warehouse")) == 2, 400);
            wh = Home(s, "warehouse");
            bool stillDark = wh.PowerState != BuildingPowerState.Powered;
            foreach (BuildingRecord g in gens)
            {
                BuildingOps.TrySetEnabled(s, g.BuildingId, true, out _);
            }
            HomeValleyPowerGrid.TrySetPriority(s, wh.BuildingId, 1);
            HomeValleyPowerGrid.Recompute(s);
            Expect(u.Success && powerBefore != BuildingPowerState.Powered && powerDuring != BuildingPowerState.Powered && during.Kind == BuildingStatusKind.Upgrading
                   && during.Reason.Contains("缺电") && finished && stillDark && wh.ConstructionState == BuildingConstructionState.Operational && wh.PowerState == BuildingPowerState.Powered,
                $"F3 负向“升级中断电”：仓库优先级 4、核心基础供电不够分（{diag}）→ 仓库供电 {powerDuring}，状态“升级中”（“{during.Reason}”）；机器施工不靠电，断电中照常完工到 T2（{finished}，完工时仍 {(stillDark ? "断电" : "有电")}）；来电后 {wh.PowerState}");

            // 升级中被摧毁：差额材料已有一部分运到现场 → 摧毁 → 升级取消、虚影移除、运到的差额全额退回（废料总量守恒）、通知。
            GridOpResult u2 = HomeGridService.TryUpgrade(s, wh.BuildingId);
            BuildingRecord ghost = u2.Success ? HomeGridService.FindBuilding(s, u2.BuildingId) : null;
            StepUntil(() => ghost != null && ghost.ConstructionDelivered > 0, 200);
            int delivered = ghost?.ConstructionDelivered ?? 0;
            int total0 = Mathf.FloorToInt(s.Scrap) + delivered + CargoScrap(s) + GroundScrap(s);
            int notes = NotifyCount("building_destroyed");
            bool destroyed = HomeValleyPowerGrid.ApplyBuildingDestroyed(s, wh.BuildingId);
            int total1 = Mathf.FloorToInt(s.Scrap) + CargoScrap(s) + GroundScrap(s);
            bool cancelled = HomeGridService.FindBuilding(s, u2.BuildingId) == null && HomeGridService.FindRelocationGhost(s, wh.BuildingId) == null
                             && HomeValleyWorkOrders.FindActiveBuild(s, u2.BuildingId) == null && BuildingOps.TierOf(Home(s, "warehouse")) == 2;
            Expect(u2.Success && delivered > 0 && destroyed && cancelled && total1 == total0 && NotifyCount("building_destroyed") > notes,
                $"F4 负向“升级中被摧毁”：已运到 {delivered} 废料 → 摧毁回调取消升级、虚影与施工单移除、差额全额退回（废料总量 {total0} → {total1}）、仍是 T2、发紧急通知（{destroyed}/{cancelled}）");

            // F5（审查 P2）：被摧毁的 T2 仓库重建 = 基础造价 + 已升等级的差额，重建后仍是 T2；投入累加不覆盖；摧毁前禁用的重建后仍禁用。
            wh = Home(s, "warehouse");
            BuildingOps.TrySetEnabled(s, wh.BuildingId, true, out _);
            int expectCost = HomeValleyLayout.RepairProfile[wh.BuildingTypeId].ScrapCost + BuildingOps.TierRow("warehouse", 2).DiffScrap;
            int rebuildCost = BuildingOps.RebuildScrapFor(wh);
            int invested0 = wh.InvestedScrap;
            s.Scrap = Math.Max(s.Scrap, 400);
            bool rebuiltT2 = BuildingOps.TryOrderRepair(s, wh.BuildingId, out string rbm)
                             && StepUntil(() => Home(s, "warehouse").ConstructionState == BuildingConstructionState.Operational, 400);
            wh = Home(s, "warehouse");
            int invested1 = wh.InvestedScrap;
            bool investedSum = invested1 == invested0 + rebuildCost;
            BuildingOps.TrySetEnabled(s, wh.BuildingId, false, out _);
            HomeValleyPowerGrid.ApplyBuildingDestroyed(s, wh.BuildingId);
            bool disabledOrdered = BuildingOps.TryOrderRepair(s, wh.BuildingId, out _);
            bool keptDisabled = disabledOrdered && StepUntil(() => Home(s, "warehouse").ConstructionState != BuildingConstructionState.Damaged, 400)
                                && Home(s, "warehouse").ConstructionState == BuildingConstructionState.Disabled;
            wh = Home(s, "warehouse");
            BuildingOps.TrySetEnabled(s, wh.BuildingId, true, out _);
            Expect(rebuildCost == expectCost && rebuiltT2 && BuildingOps.TierOf(wh) == 2 && investedSum && keptDisabled && wh.ConstructionState == BuildingConstructionState.Operational,
                $"F5 被摧毁的 T2 仓库重建：造价 {rebuildCost}（= 修复 {HomeValleyLayout.RepairProfile[wh.BuildingTypeId].ScrapCost} + T2 差额 {BuildingOps.TierRow("warehouse", 2).DiffScrap}），重建后仍 T2；投入 {invested0} → {invested1}（累加）；" +
                $"禁用状态下被摧毁 → 重建后仍是禁用（{rebuiltT2}/{investedSum}/{keptDisabled}；“{rbm}”）");

            // F6（审查 P2）：面板升级完工后撤销（跳过：已完工）再重做 → 不会再多升一级、不再扣料。
            s.Scrap = Math.Max(s.Scrap, 400);
            var plan = new UpgradeBoxPlan();
            plan.Buildings.Add(wh.BuildingId);
            HomeGridService.CanUpgradeNow(s, wh, out string toType, out _, out _);
            plan.BuildingTargets.Add(toType);
            int queued = PlanHistory.Upgrade(s, plan, out _);
            bool t3 = queued == 1 && StepUntil(() => BuildingOps.TierOf(Home(s, "warehouse")) == 3, 400);
            int scrapAt3 = s.Scrap;
            PlanStepResult undo = PlanHistory.Undo(s);
            PlanStepResult redo = PlanHistory.Redo(s);
            wh = Home(s, "warehouse");
            bool noExtra = BuildingOps.TierOf(wh) == 3 && HomeGridService.FindRelocationGhost(s, wh.BuildingId) == null && s.Scrap == scrapAt3
                           && undo != null && undo.Skipped >= 1 && redo != null && redo.Skipped >= 1 && (redo.FirstSkip ?? string.Empty).Contains("已经是这一级");
            Expect(t3 && noExtra,
                $"F6 升级完工后撤销 / 重做：撤销跳过（“{undo?.FirstSkip}”），重做也跳过（“{redo?.FirstSkip}”），仍是 T3、没有新的升级虚影、废料不变（{t3}/{noExtra}）");
        }

        private static int CargoScrap(CampaignState s) =>
            MachineRegistry.AllRecords.Where(m => m.Cargo != null).Sum(m => m.Cargo.Where(c => c.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(c => c.Amount));

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        // ── G 仓库过滤与留底 ─────────────────────────────────────────────────────────

        private static void CheckStoreFilterAndKeep()
        {
            CampaignState s = NewWorld(5581, scrap: 400);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord wh = RepairWarehouse(s);
            FgProductionSelfCheck.Resync(s);
            ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap);
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            int scrapAll = HomeInventory.Capacity(s, scrap);
            int alloyAll = HomeInventory.Capacity(s, alloy);
            bool set = BuildingOps.TrySetStoreFilter(s, wh.BuildingId, "item:alloy", out string m);
            int scrapOnly = HomeInventory.Capacity(s, scrap);
            int alloyOnly = HomeInventory.Capacity(s, alloy);
            BuildingOps.TrySetStoreFilter(s, wh.BuildingId, "tier:raw", out _);
            int alloyRaw = HomeInventory.Capacity(s, alloy);
            BuildingOps.TrySetStoreFilter(s, wh.BuildingId, null, out _);
            Expect(set && m.Contains("合金") && alloyOnly == alloyAll && scrapOnly == scrapAll - BuildingOps.WarehouseTierCapacity(wh) && alloyRaw == 0 && HomeInventory.Capacity(s, alloy) == alloyAll,
                $"G1 仓库只存哪些物品（FG04 第 4 节）：只存合金 → 废料容量 {scrapAll} → {scrapOnly}（剩核心缓存）、合金 {alloyOnly}；只存原料类 → 合金 0；改回全部恢复（“{m}”）");

            // 输出口保留 N 件（FG-GAP-093）：仓库输出口接一条传送带到测试收货口，只推废料、保留 50。
            BeltPortService.Binding outB = BeltPortService.Find(wh.BuildingId, "warehouse.out0");
            if (outB == null)
            {
                Fail("G2 仓库输出口没登记");
                return;
            }
            GridCell bc = outB.BeltCell;
            BeltDir dir = (BeltDir)(int)outB.Face;
            bool laid = FgProductionSelfCheck.Belts(s, bc, dir, 3);
            int dx = dir == BeltDir.East ? 1 : dir == BeltDir.West ? -1 : 0;
            int dy = dir == BeltDir.North ? 1 : dir == BeltDir.South ? -1 : 0;
            FgProductionSelfCheck.TestSink(s, new GridCell(bc.X + dx * 2, bc.Y + dy * 2));
            FgProductionSelfCheck.Resync(s);
            ushort scrapBelt = scrap.BeltId;
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", scrapBelt, out _);
            s.Scrap = 120;
            bool keepSet = BeltPortService.TrySetKeep(s, wh.BuildingId, "warehouse.out0", 50, out _);
            // 下游一直在取货：每 1/4 游戏秒把输出口推上来的件拿走（真实的端口泵 + 传送带内核，测的是“保留 N 件”的推送量）。
            int pushed = 0;
            void Run(float sec)
            {
                for (float t = 0f; t < sec; t += 0.25f)
                {
                    Seconds(0.25f);
                    pushed += BeltNetworkService.Kernel.TakeSourcePending(outB.PortId);
                }
            }
            Run(40f);
            int left = Mathf.FloorToInt(s.Scrap);
            int pushedKeep = pushed;
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, wh, views);
            BeltPortService.PortView ov = views.FirstOrDefault(v => v.PortKey == "warehouse.out0");
            string issue = ov?.IssueLine ?? string.Empty;
            BeltPortService.TrySetKeep(s, wh.BuildingId, "warehouse.out0", 0, out _);
            Run(30f);
            int drained = Mathf.FloorToInt(s.Scrap);
            BeltNetworkService.Kernel.TryGetPortCounts(outB.PortId, out int pendingNow, out _, out _);
            pendingNow += FgProductionSelfCheck.OnBelts(bc, dir, 3); // 已经推上传送带、还没走到末端的件
            Expect(laid && keepSet && left == 50 && pushedKeep + pendingNow == 70 && ov?.Keep == 50
                   && (issue.Contains("保留") || !ov.Connected) && drained < left,
                $"G2 仓库输出口“保留 N 件”（FG-GAP-093）：每种至少留 50 → 推了 40 秒送出 {pushedKeep} 件（端口与传送带上还有 {pendingNow} 件）、仓库还剩 {left} 废料，端口写“{issue}”；改回不保留后继续推到 {drained}");
        }

        // ── H 清空缓存 ───────────────────────────────────────────────────────────────

        private static void CheckClearBuffers()
        {
            CampaignState s = NewWorld(5591, scrap: 100);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            BuildingOps.TrySetEnabled(s, f.BuildingId, false, out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 12 } };
            P(s, f).Rec.Out = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 3 } };
            int sc0 = Mathf.FloorToInt(s.Scrap);
            int al0 = Stock(s, "alloy");
            int n = BuildingOps.ClearBuffers(s, f.BuildingId, out string m);
            bool prod = n == 15 && ProductionService.Total(P(s, f).Rec.In) == 0 && ProductionService.Total(P(s, f).Rec.Out) == 0
                        && Mathf.FloorToInt(s.Scrap) + GroundScrap(s) >= sc0 + 12 && Stock(s, "alloy") + GroundCount(s, "alloy") == al0 + 3;
            bool empty = BuildingOps.ClearBuffers(s, f.BuildingId, out string m2) == 0 && m2.Contains("空");
            BuildingRecord st = AssemblyMaterials.Station(s);
            ItemStackRecord[] buf = AssemblyMaterials.Buffer(s);
            ProductionService.Add(ref s.Economy.AssemblyBuffer, "part", 7);
            int part0 = Stock(s, "part");
            int na = st != null ? BuildingOps.ClearBuffers(s, st.BuildingId, out _) : -1;
            bool asm = na == 7 && ProductionService.Count(AssemblyMaterials.Buffer(s), "part") == 0 && Stock(s, "part") + GroundCount(s, "part") == part0 + 7;
            Expect(prod && empty && asm && buf != null,
                $"H 清空缓存到仓库（FG-GAP-095）：精炼炉输入 / 输出缓存 15 件送回（“{m}”）、再点写明“缓存是空的”；装配站材料缓存 7 个零件送回仓库（{prod}/{empty}/{asm}）");
        }

        private static int GroundCount(CampaignState s, string itemId)
        {
            string rt = ItemCatalog.ResourceTypeOf(itemId);
            return (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == rt).Sum(g => g.Amount);
        }

        // ── I 拆除确认写明流体 ─────────────────────────────────────────────────────────

        private static void CheckDemolishFluidConfirm()
        {
            CampaignState s = NewWorld(5601, scrap: 400);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord t = FindOrPlace(s, "refinery_tower");
            ProductionService.Producer p = P(s, t);
            bool before = !HomeGridService.DemolishNeedsConfirm(s, t.BuildingId);
            int crude = Array.FindIndex(p.Fluids, f => !f.Def.IsOutput);
            p.Rec.FluidHeld[crude] = 30000;
            var held = new List<string>();
            long ml = ProductionService.HeldFluidMl(s, t, held);
            bool after = HomeGridService.DemolishNeedsConfirm(s, t.BuildingId);
            BuildingGrid tg = GridContent.Building("refinery_tower");
            GridMath.FootprintBounds(new GridCell(t.GridX, t.GridY), tg.FootprintW, tg.FootprintH, 0, out GridCell mn, out GridCell mx);
            DemolishBoxPlan plan = HomeGridService.PlanDemolishBox(s, mn, mx);
            Expect(before && ml == 30000 && held.Count == 1 && held[0].Contains("30") && after && plan.BuildingFluidMl == 30000 && plan.NeedsConfirm,
                $"I 拆除有流体存量的精炼塔（FG-GAP-094）：空的时候不确认；流体口里有 {held.FirstOrDefault()} 时单座拆除与框选拆除都先确认并写明排空多少（{before}/{after}/{plan.BuildingFluidMl}）");
        }

        // ── J 效率与最近 10 分钟 ───────────────────────────────────────────────────────

        private static void CheckEfficiency()
        {
            CampaignState s = NewWorld(5611, scrap: 100);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 400 } };
            SecondsDraining(s, 120f, f);
            var outs = new List<ItemStackRecord>();
            ProductionService.TryWindowStats(P(s, f), outs, out int done1, out long th1);
            float eff1 = th1 > 0 ? done1 * 1000f / th1 : 0f;
            int alloy1 = outs.FirstOrDefault(o => o.ItemId == "alloy")?.Amount ?? 0;
            BuildingOps.TrySetEnabled(s, f.BuildingId, false, out _);
            SecondsDraining(s, 120f, f);
            ProductionService.TryWindowStats(P(s, f), outs, out int done2, out long th2);
            float eff2 = th2 > 0 ? done2 * 1000f / th2 : 0f;
            BuildingOps.TrySetEnabled(s, f.BuildingId, true, out _);
            P(s, f).Rec.In = Array.Empty<ItemStackRecord>();
            Seconds(11 * 60f);
            ProductionService.TryWindowStats(P(s, f), outs, out int done3, out long th3);
            BuildingRecord tw = Home(s, HomeValleyLayout.BuildingTypeCore);
            bool na = !ProductionService.TryWindowStats(null, outs, out _, out _);
            Expect(eff1 > 0.85f && eff1 <= 1.05f && alloy1 == done1 && done2 == done1 && eff2 > 0.4f && eff2 < 0.6f && done3 == 0 && th3 > 0 && na,
                $"J 效率与最近 10 分钟产出（FGR-ECO-011，按桶聚合）：满料 2 分钟效率 {eff1:P0}（{done1} 份 = 合金 {alloy1}）；再禁用 2 分钟效率降到 {eff2:P0}；断料 11 分钟后窗口里 0 份（旧桶滑出）");

            // J2（审查 P1）：回收站满速分解送来的物品（每份 ItemSeconds，不是拆废墟的 CycleSeconds）、固件刻录台满速（20 秒配方，每生产步 2.5 千分份不能截断成 2）
            // 效率都约 100%，不会显示 300% / 125%。
            CampaignState s2 = NewWorld(5612, scrap: 100);
            FgProductionSelfCheck.PowerUp(s2);
            BuildingRecord rc = FindOrPlace(s2, "recycler");
            BuildingRecord fb = FindOrPlace(s2, "firmware_burner");
            List<string> burnable = SignalCoreService.PrintableFirmware(s2);
            bool burnSet = rc != null && fb != null && burnable.Count > 0 && ProductionService.TrySetBurnTarget(s2, fb.BuildingId, burnable[0], out _);
            float effR = 0f, effB = 0f;
            int doneR = 0, doneB = 0;
            if (burnSet)
            {
                for (float t = 0f; t < 240f - 0.001f; t += 0.5f)
                {
                    P(s2, rc).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 20 } };
                    P(s2, fb).Rec.In = new[] { new ItemStackRecord { ItemId = "chip_substrate", Amount = 20 } };
                    Seconds(0.5f);
                    P(s2, rc).Rec.Out = Array.Empty<ItemStackRecord>();
                    // 刻出的芯片进固件库：腾出存放（测的是刻录台自己的速度，不是存放满了的输出堵塞）。
                    s2.PrimitiveChips = (s2.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Where(c => c.Origin != PrimitiveInventory.OriginBurn).ToArray();
                }
                ProductionService.TryWindowStats(P(s2, rc), null, out doneR, out long thR);
                ProductionService.TryWindowStats(P(s2, fb), null, out doneB, out long thB);
                effR = thR > 0 ? doneR * 1000f / thR : 0f;
                effB = thB > 0 ? doneB * 1000f / thB : 0f;
            }
            Expect(burnSet && effR > 0.85f && effR <= 1.05f && effB > 0.8f && effB <= 1.05f && doneR >= 150 && doneB >= 10,
                $"J2 回收站满速分解 4 分钟：{doneR} 份、效率 {effR:P0}；固件刻录台满速 4 分钟：{doneB} 份、效率 {effB:P0}（都约 100%；刻录目标 {burnable.FirstOrDefault()}）");
        }

        // ── K 上下游 ─────────────────────────────────────────────────────────────────

        private static void CheckLinks()
        {
            CampaignState s = NewWorld(5621, scrap: 400);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord drill = FindOrPlace(s, "extraction_drill");
            BuildingRecord furnace = FindOrPlace(s, "refinery_furnace");
            BuildingRecord workshop = FindOrPlace(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, furnace.BuildingId, "alloy_ore", out _);
            ProductionService.TrySetRecipe(s, workshop.BuildingId, "part", out _);
            var links = new List<BuildingRecord>();
            bool upOk = BuildingOps.CollectLinks(s, furnace, true, links, out int upMore);
            bool up = upOk && links.Any(b => b.BuildingId == drill.BuildingId);
            bool downOk = BuildingOps.CollectLinks(s, furnace, false, links, out _);
            bool down = downOk && links.Any(b => b.BuildingId == workshop.BuildingId);
            bool wsDown = BuildingOps.CollectLinks(s, workshop, false, links, out _) && links.Any(b => AssemblyMaterials.IsStation(b));
            bool none = !BuildingOps.CollectLinks(s, Home(s, HomeValleyLayout.BuildingTypeCore), true, links, out _);
            // 五座同上游：最多列 4 座（按距离），其余记“还有 N 座”。
            int extra = 0;
            for (int i = 0; i < 4; i++)
            {
                GridCell? at = FgProductionSelfCheck.FindArea(s, 3, 3, 8f, 28f);
                if (at.HasValue)
                {
                    GridCell pv = FgProductionSelfCheck.At(at.Value, 1, 1);
                    FgProductionSelfCheck.SetFootprint(s, "extraction_drill", pv, "ore_metal");
                    if (HomeGridService.ValidatePlacement(s, "extraction_drill", pv, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        Built(s, "extraction_drill", pv);
                        extra++;
                    }
                }
            }
            BuildingOps.CollectLinks(s, furnace, true, links, out int more);
            Expect(up && down && wsDown && none && links.Count == 4 && more >= 1,
                $"K 上下游（FG04 第 4 节）：精炼炉（合金·矿）上游有提取钻、下游有零件工坊；零件工坊下游有装配站；核心不适用；超过 4 座时列最近的 4 座，其余写“还有 {more} 座”（{up}/{down}/{wsDown}/{none}/列出 {links.Count}/另放 {extra}）");
        }

        // ── L 设置带配方（DEBT-FG3LOG07-01）─────────────────────────────────────────────

        private static void CheckSettingsCarryRecipe()
        {
            CampaignState s = NewWorld(5631, scrap: 900);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f1 = FindOrPlace(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f1.BuildingId, "alloy_ore", out _);
            BuildingOps.TrySetPriority(s, f1.BuildingId, 2, out _);
            PlanSettings.BuildingSettings(s, f1, out int s0, out int s1, out int s2);
            bool read = s0 == 2 && s1 == PlanSettings.StableId("alloy_ore") && PlanSettings.RecipeIdOf(s1) == "alloy_ore" && s2 == 0;
            // 吸管 → 放下 = 新虚影带着配方与优先级。
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? at = FgProductionSelfCheck.FindFree(s, "refinery_furnace", 8f, 26f);
            bool eyed = false;
            if (mode != null && at.HasValue)
            {
                mode.Open();
                mode.Eyedrop(s, new GridCell(f1.GridX, f1.GridY));
                eyed = mode.SelectedTypeId == "refinery_furnace" && mode.PendingS1 == s1;
                mode.SetHover(s, at.Value);
                mode.PointerDown(s, at.Value);
                mode.PointerUp(s, at.Value);
                mode.ClearSelection();
                mode.Close();
            }
            BuildingRecord ghost = at.HasValue ? HomeGridService.BuildingAt(s, at.Value) : null;
            bool ghostCarries = ghost != null && ghost.PowerPriority == 2 && P(s, ghost)?.Recipe?.Id == "alloy_ore";
            // 复制设置（Alt+C / Alt+V）写到另一座建成的精炼炉：配方 + 优先级；撤销恢复。
            BuildingRecord f2 = FindOrPlace2(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f2.BuildingId, "alloy_scrap", out _); // 新建的同类建筑会沿用上一次的配方：先改成另一条，看撤销是否回到它。
            PlanSettings.TryRead(s, new GridCell(f1.GridX, f1.GridY), out PlanSettings.Clip clip, out _);
            bool applied = PlanHistory.ApplySettings(s, new GridCell(f2.GridX, f2.GridY), clip, out _, out _) && P(s, f2).Recipe?.Id == "alloy_ore" && f2.PowerPriority == 2;
            PlanHistory.Undo(s);
            bool undone = P(s, f2).Recipe?.Id == "alloy_scrap";
            // 不兼容：配方写到零件工坊上不生效，只写优先级。
            BuildingRecord ws = FindOrPlace(s, "parts_workshop");
            PlanSettings.ApplyBuildingSettings(s, ws, 3, s1, 0, out bool recipeApplied);
            bool incompatible = !recipeApplied && ws.PowerPriority == 3 && P(s, ws).Recipe == null;
            // 框选复制 → 粘贴：粘贴出来的虚影带配方。
            PlanEntryBlock block = PlanningService.Capture(s, new GridCell(f1.GridX - 1, f1.GridY - 1), new GridCell(f1.GridX + 1, f1.GridY + 1), out _);
            bool captured = block != null && Enumerable.Range(0, block.Ids.Length).Any(i => block.Ids[i] == "refinery_furnace" && block.S1[i] == s1);
            Expect(read && eyed && ghostCarries && applied && undone && incompatible && captured,
                $"L 设置带配方（DEBT-FG3LOG07-01）：读出 S0 优先级 2 / S1 配方编号；吸管带配方，放下的虚影建成前就有配方与优先级；复制设置写到另一座精炼炉（撤销恢复）；对零件工坊只写优先级；框选复制带配方（{read}/{eyed}/{ghostCarries}/{applied}/{undone}/{incompatible}/{captured}）");
        }

        // ── M 建造模式状态行（FG-GAP-091）────────────────────────────────────────────────

        private static void CheckStatusLine()
        {
            CampaignState s = NewWorld(5641, scrap: 400);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            GridCell? beltAt = FgProductionSelfCheck.FindArea(s, 3, 1, 10f, 26f);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (mode == null || !beltAt.HasValue || !FgProductionSelfCheck.Belts(s, beltAt.Value, BeltDir.East, 2))
            {
                Fail("M 放不下传送带 / 没有建造模式");
                return;
            }
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "BuildModeHud.uxml", out GameObject go);
            try
            {
                BuildModeHudUIToolkit hud = go.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                InputRouter.SetScope(InputScope.Strategy);
                mode.Open();
                var fc = new GridCell(f.GridX, f.GridY);
                mode.PointerDown(s, fc);
                mode.PointerUp(s, fc);
                ProductionPanelUIToolkit.Close();
                hud.Refresh();
                string onBuilding = hud.StatusLabelText;
                bool opened = ProductionPanelUIToolkit.LastRequestedId == f.BuildingId;
                mode.SetHover(s, beltAt.Value);
                hud.Refresh();
                string onBelt = hud.StatusLabelText;
                bool reading = onBelt.Contains("传送带") && onBelt.Contains("上一步");
                Expect(opened && onBuilding.Contains(HomeGridService.DisplayName("refinery_furnace")) && reading,
                    $"M 建造模式状态行（FG-GAP-091）：点精炼炉打开它的面板、状态行写它（“{onBuilding.Split('\n')[0]}”）；鼠标移到传送带上改写传送带读数，上一步结果另起一行（“{onBelt.Replace("\n", " / ")}”）");
            }
            finally
            {
                mode.Close();
                Object.DestroyImmediate(go);
            }
        }

        // ── N 通用面板（真 UXML）─────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(5651, scrap: 900);
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord wh = RepairWarehouse(s);
            BuildingRecord drill = FindOrPlace(s, "extraction_drill");
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            BuildingRecord f2 = FindOrPlace2(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_ore", out _);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                // 仓库：身份行、状态、等级与升级按钮、只存过滤、耐久；非生产建筑不显示配方 / 进度。
                ProductionPanelUIToolkit.Open(wh.BuildingId);
                panel.Refresh();
                bool whOk = panel.PanelVisible && panel.IdentText.Contains("仓库") && panel.IdentText.Contains("T1") && panel.ShownStatus == BuildingStatusKind.Working
                            && panel.TierBoxVisible && panel.TierText.Contains("T1 / T3") && panel.UpgradeButton.enabledSelf && panel.UpgradeLineText.Contains("差额")
                            && panel.StoreBoxVisible && panel.StoreLineText.Contains("容量 300") && !panel.RecipeBoxVisible && panel.DurabilityText.Contains("100 / 100")
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildingPanelFirstOpen) && panel.EfficiencyText.Contains("不适用");
                // 改名：输入框 + 回车 / 按钮；“默认名”。
                panel.NameField.value = "北仓";
                bool renamed = panel.Rename(panel.NameField.value) && wh.CustomName == "北仓" && panel.TitleText.Contains("北仓");
                // 只存过滤：下拉框选中即生效。
                int alloyIndex = panel.StoreField.choices.FindIndex(c => c.Contains("合金"));
                panel.StoreField.value = panel.StoreField.choices[alloyIndex];
                bool filtered = wh.StoreFilter == "item:alloy";
                panel.StoreField.value = panel.StoreField.choices[0];
                // 升级：按钮 → 升级中，“取消升级”全额退回。
                bool upgraded = panel.Upgrade() && panel.ShownStatus == BuildingStatusKind.Upgrading && Visible(panel.UpgradeCancelButton);
                bool cancelUp = panel.CancelUpgrade() && HomeGridService.FindRelocationGhost(s, wh.BuildingId) == null;
                // 精炼炉：状态 / 原因、配方、效率、上下游、优先级下拉、批量（确认框）、启用 / 禁用、维修、清空、“?”。
                ProductionPanelUIToolkit.Open(f.BuildingId);
                Seconds(2f);
                panel.Refresh();
                bool fOk = panel.RecipeBoxVisible && !panel.TierBoxVisible == false && panel.UpLabelText.Contains("上游") && panel.UpLinkId(0) == drill.BuildingId
                           && !panel.StoreBoxVisible && panel.EfficiencyText.Length > 0;
                bool jumped = panel.JumpTo(panel.UpLinkId(0)) && ProductionPanelUIToolkit.BuildingId == drill.BuildingId && panel.LastJumpPosition == drill.Position;
                ProductionPanelUIToolkit.Open(f.BuildingId);
                panel.PriorityField.value = panel.PriorityField.choices[0];
                bool prio = f.PowerPriority == 1;
                panel.BatchField.value = panel.BatchField.choices[2]; // 全部禁用（同类 2 座）→ 先确认
                bool asked = panel.PendingBatch != null && panel.PendingBatch.Change.Count == 2 && !BuildingOps.IsDisabled(f);
                UiConfirmDialog.Confirm();
                bool batch = BuildingOps.IsDisabled(f) && BuildingOps.IsDisabled(f2) && panel.ShownStatus == BuildingStatusKind.Disabled && panel.EnableButton.text.Contains("启用");
                panel.ToggleEnabled();
                bool enabled = !BuildingOps.IsDisabled(f);
                BuildingOps.ApplyDamage(s, f.BuildingId, 50f);
                panel.Refresh();
                bool repairBtn = panel.RepairButton.enabledSelf && panel.RepairButton.text.Contains("维修件") && panel.DurabilityText.Contains("50 / 100") && panel.DurabilityFillPercent < 60f;
                GiveKits(s, 5);
                bool repair = panel.OrderRepair() && Visible(panel.RepairCancelButton) && panel.CancelRepair();
                P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = "metal_ore", Amount = 2 } };
                panel.Refresh();
                bool clearEnabled = panel.ClearButton.enabledSelf;
                panel.OpenCodex();
                bool help = panel.LastCodexId == "codex.economy.processing" && MechanicCodex.LastOpenedId == "codex.economy.processing";
                bool markers = !GameText.ContainsMarker(panel.TitleText + panel.IdentText + panel.StateText + panel.ReasonText + panel.TierText + panel.UpgradeLineText
                                                        + panel.DurabilityText + panel.EfficiencyText + panel.UpLabelText + panel.DownLabelText + panel.HintText);
                Expect(whOk && renamed && filtered && upgraded && cancelUp && fOk && jumped && prio && asked && batch && enabled && repairBtn && repair && clearEnabled && help && markers,
                    $"N1 通用面板（真 UXML，FGU-09）：仓库 身份 / 状态 / 等级与升级 / 只存 / 耐久；改名；过滤与优先级下拉框选中即生效；升级 → 升级中 → 取消；精炼炉 上游链接跳转；" +
                    $"批量禁用先确认（2 座）；启用 / 禁用；受损后“维修（维修件）”派单与取消；清空缓存按钮；“?”打开“加工建筑”图鉴（{whOk}/{renamed}/{filtered}/{upgraded}/{cancelUp}/{fOk}/{jumped}/{prio}/{asked}/{batch}/{enabled}/{repairBtn}/{repair}/{clearEnabled}/{help}/{markers}）");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "ProductionPanel.uxml", "ProductionPanelWindow");
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "N2 布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n')[0]);
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        private static bool Visible(VisualElement e) => ProductionPanelUIToolkit.Visible(e);

        // ── O 存读档 ─────────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId).OrderBy(b => b.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(b.BuildingId).Append(':').Append((int)b.ConstructionState).Append('/').Append(Mathf.RoundToInt(b.Health * 10f)).Append('/').Append(b.Tier).Append('/')
                    .Append(b.CustomName).Append('/').Append(b.StoreFilter).Append('/').Append(b.PowerPriority).Append('|');
            }
            foreach (ProducerRecord r in s.Economy.Producers.OrderBy(r => r.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(r.BuildingId).Append('=').Append(r.Completed).Append('/').Append(r.Progress).Append('/')
                    .Append(string.Join(",", (r.Stats ?? Array.Empty<ProducerStatBucket>()).Where(x => x != null).Select(x => x.Index + ":" + x.Done + ":" + x.TheoryMilli))).Append('|');
            }
            foreach (WorkOrderRecord o in (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o.Kind == WorkOrderKind.Repair || o.Kind == WorkOrderKind.Build).OrderBy(o => o.WorkOrderId, StringComparer.Ordinal))
            {
                sb.Append(o.WorkOrderId.Split(':')[0] + ":" + o.Kind).Append('/').Append((int)o.State).Append('/').Append(o.ReservedItemId).Append(o.ReservedItemAmount).Append('|');
            }
            foreach (BeltPortBindingRecord pb in s.Belts?.PortBindings ?? Array.Empty<BeltPortBindingRecord>())
            {
                sb.Append(pb.PortKey).Append('k').Append(pb.Keep).Append('|');
            }
            sb.Append("scrap=").Append(Mathf.FloorToInt(s.Scrap)).Append(" kits=").Append(Stock(s, BuildingOps.RepairKitId)).Append(" alloy=").Append(Stock(s, "alloy"));
            return sb.ToString();
        }

        /// <summary>存读档 / 倍速 / 观察共用的场景：精炼炉（有料，产出统计）、受损的第二座精炼炉 + 维修单（维修件预留）、改名、仓库过滤、仓库重建 → 升级到 T2。</summary>
        private static bool LayScenario(CampaignState s)
        {
            FgProductionSelfCheck.PowerUp(s);
            BuildingRecord f = FindOrPlace(s, "refinery_furnace");
            BuildingRecord f2 = FindOrPlace2(s, "refinery_furnace");
            BuildingRecord wh = Home(s, HomeValleyLayout.BuildingTypeWarehouse);
            if (f == null || f2 == null || wh == null)
            {
                return false;
            }
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 120 } };
            BuildingOps.TryRename(s, f.BuildingId, "甲炉", out _);
            BuildingOps.ApplyDamage(s, f2.BuildingId, 70f);
            GiveKits(s, 6);
            BuildingOps.TryOrderRepair(s, f2.BuildingId, out _);
            BuildingOps.TryOrderRepair(s, wh.BuildingId, out _);
            BuildingOps.TrySetStoreFilter(s, wh.BuildingId, "tier:raw", out _);
            return true;
        }

        private static void ContinueScenario(CampaignState s)
        {
            BuildingRecord wh = Home(s, HomeValleyLayout.BuildingTypeWarehouse);
            if (wh != null && wh.ConstructionState == BuildingConstructionState.Operational && BuildingOps.TierOf(wh) == 1 && HomeGridService.FindRelocationGhost(s, wh.BuildingId) == null)
            {
                HomeGridService.TryUpgrade(s, wh.BuildingId);
            }
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(5661, scrap: 600);
            if (!LayScenario(s))
            {
                Fail("O 放不下场景");
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 20 + 7);
            ContinueScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3 + 1);
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                WorldSimulation.StepMany(GameClock.StepHz * 60);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            bool fields = before.Contains("甲炉") && before.Contains("tier:raw") && before.Contains("repair_kit");
            Expect(save.Success && loaded1 == before && fields,
                "O1 真文件存读档：名字、等级、仓库过滤、耐久、维修单的维修件预留、升级虚影、生产统计桶写进存档，读档后逐字段一致" + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "O2 读档后接着跑 60 游戏秒（机器修完、仓库升级完工、精炼炉出产与统计），与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        // ── P / Q 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 600);
            pausedHeld = true;
            if (!LayScenario(s))
            {
                return "放不下";
            }
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            ContinueScenario(s);
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
            long target = GameClock.Ticks + GameClock.StepHz * 60;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 500)
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
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(5671, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下",
                "P 暂停中（120 帧）维修、升级施工、生产与统计都不动；0.5x / 1x / 2x / 3x 跑同样的 60 游戏秒（机器维修、仓库重建与升级、精炼炉出产与统计桶）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(5672, true, 1f, false, out _);
            string unseen = RunScenario(5672, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "Q 同一组（维修、重建、升级、生产统计、改名 / 过滤）在观察与不观察家园时跑 60 游戏秒逐字段一致（FGR-BASE-021）"
                                                     + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── R 性能 ───────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(5681, scrap: 400);
            FgProductionSelfCheck.PowerUp(s);
            var furnaces = new List<BuildingRecord>();
            for (int i = 0; i < 60; i++)
            {
                BuildingRecord f = FindOrPlace2(s, "refinery_furnace");
                if (f == null)
                {
                    break;
                }
                ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
                P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
                furnaces.Add(f);
            }
            int n = s.BuildingRecords.Length;
            // 状态汇总：全部建筑各算一次（面板 / 悬停是按需单座，这里是上限）。
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < 10; r++)
            {
                foreach (BuildingRecord b in s.BuildingRecords)
                {
                    BuildingStatusService.Evaluate(s, b);
                }
            }
            sw.Stop();
            double perBuilding = sw.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, n * 10);
            // 生产推进带统计：60 座每生产步耗时（统计累加 O(1)，不随窗口变大）。
            ProductionService.ResetStepStats();
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            double step = ProductionService.MaxStepMs;
            Expect(furnaces.Count >= 30, $"R 场景：{furnaces.Count} 座精炼炉、共 {n} 座建筑");
            PerfLines.Add($"状态汇总每座 {perBuilding:F2} µs（{n} 座 × 10 轮）；{furnaces.Count} 座精炼炉带统计的生产步最大 {step:F3} ms");
            PerfGate.Expect(perBuilding <= 30.0 && step <= 2.0,
                $"R 性能：状态汇总每座 {perBuilding:F2} µs（阈值 30 µs，只在面板 / 悬停按需调用）；{furnaces.Count} 座生产建筑带效率统计的生产步最大 {step:F3} ms（阈值 2 ms，Editor batchmode；真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perBuilding, 30.0, "状态汇总每座 µs"), PerfGate.Le(step, 2.0, "生产步最大 ms") }, Expect, Line);
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
