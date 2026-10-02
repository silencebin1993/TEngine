using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
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
using GameLogic.UI.SignalCore;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-11 超控阵列与多材料施工造价的自动验收（FG04 FGR-ECO-020；FG01 FGR-SIG-010；FGT-ECO-010；卡片“关键材料缺失时，面板说明从哪里获得；
    /// HUD 上失效槽位的显示”、负向“阵列被摧毁时槽里有固件（固件保留，不生效）”；承接 DEBT-FG1SIG01-03、DEBT-FG3LOG02-02）。
    /// 全部起真实系统：真实家园（世界模拟、电网内核、工单与机器施工、核心保管库）、真实信号核与接入结算、真文件存读档、真 UXML 面板。
    /// A 数据（新表与源数据逐字段、三级行、关键材料与来源文字、文本中英、钩子、图鉴、通知类型）；
    /// B 多材料施工（正式放置入口 → 机器取废料 → 缺关键材料时等待并写明从哪里获得 → 放进核心保管库后机器取来 → 完工投入与解锁第 3 槽）；
    /// C 槽位生效与断电（FGT-ECO-010：第 3 槽的固件插进接入口 → 断电失效（固件保留、不插入、HUD / 面板 / 通知写原因）→ 来电自动恢复；禁用；未接入电网）；
    /// D 升级 T2 / T3（研究门槛、关键材料、耗电 120 / 160、第 4 / 5 槽）；E 负向（被摧毁时固件保留不生效、重建只收废料；拆除退回关键材料、槽锁回、固件可卸不可装；
    /// 取消虚影 / 取消升级全额退回关键材料；家园只能有一座；施工中被摧毁关键材料不丢）；F 真文件存读档；G 暂停与 0.5x～3x；H 观察 / 不观察一致；
    /// I 面板（真 UXML：建筑面板的槽位行与下一级要求、信号核面板失效槽与 HUD 按钮）；J 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOverrideArraySelfCheck）。
    /// </summary>
    public static class FgOverrideArraySelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string T = OverrideArrayService.TypeId;
        private const string KeyT1 = "listening_array_core";
        private const string KeyT2 = "furnace_heart";
        private const string KeyT3 = "supercomputer_core";
        private const string BpUplink = "bp_selfcheck_eco11_cannon_up";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 7000;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 超控阵列与多材料造价")]
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
            Line("\n[超控阵列与多材料造价] 三级超控阵列、信号核第 3～5 槽、断电失效 / 来电恢复、关键材料与多材料施工（FG4-ECO-11）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgoverride-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                BuildMaterials.Reload();
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
                     "超控阵列 / 信号核 / 施工在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckBuildMultiMaterial);
                Step(CheckSlotsAndPower);
                Step(CheckUnconnected);
                Step(CheckUpgradeTiers);
                Step(CheckDestroyedAndRebuild);
                Step(CheckDemolishAndRefunds);
                Step(CheckCarrierKilled);
                Step(CheckPanels);
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
                Fail($"超控阵列自检抛异常：{e}");
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
                ResearchGate.ResetForTests();
                SignalCoreService.ResetForTests();
                SignalUplinkService.ResetForTests();
                OverrideArrayService.ResetForTests();
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
                BuildMaterials.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
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
            Line($"  · [超控阵列与多材料造价] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 900)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            OverrideArrayService.ResetForTests();
            SignalCoreService.ResetForTests();
            ResearchGate.ResetForTests();
            MachineLoadoutRegistry.Clear();
            _seq = 7000;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            // 再接一座发电机 2：开局的用电建筑 + 超控阵列（T3 160）都分得到电（测的是阵列，不是电网分配；电网本身由 FG3-LOG-06 / FG4-ECO-04 覆盖）。
            GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (g.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "ov_gen", g.Value);
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyPowerGrid.Recompute(s);
            return s;
        }

        /// <summary>核心附近一块能放超控阵列（4×4）的空地，按种子地形找（B25）。</summary>
        private static GridCell? Spot(CampaignState s, float from = 7f, float to = 24f) => FgProductionSelfCheck.FindFree(s, T, from, to);

        /// <summary>测试捷径：直接登记一座已建成的超控阵列（真实放置 / 机器施工 / 关键材料取料由 B 段与 G 段覆盖）。投入记成表里的关键材料。</summary>
        private static BuildingRecord BuiltArray(CampaignState s, int tier, GridCell? at = null)
        {
            GridCell? c = at ?? Spot(s);
            if (!c.HasValue)
            {
                Fail("测试准备：核心附近没有能放超控阵列的空地");
                return null;
            }
            BuildingGrid bg = GridContent.Building(T);
            HomeValleyLayout.PowerProfile.TryGetValue(T, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + T + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = T,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.Value.X,
                GridY = c.Value.Y,
                Position = GridMath.FootprintCenter(c.Value, bg.FootprintW, bg.FootprintH, 0),
                Health = BuildingOps.MaxDurability(T),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                Tier = tier,
                InvestedScrap = 120,
            };
            for (int t = 1; t <= tier; t++)
            {
                foreach (BuildMaterialNeed n in BuildMaterials.For(T, t))
                {
                    BuildMaterials.AddInvested(r, n.ResourceType, n.Amount);
                }
            }
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return r;
        }

        private static BuildingRecord Arr(CampaignState s) => s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == T && !HomeGridService.IsRelocationGhost(b));

        private static void Give(CampaignState s, string itemId, int n) => HomeInventory.Add(s, itemId, n, clampToSpace: false);

        private static int Stock(CampaignState s, string itemId) => HomeInventory.Stock(s, itemId);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static List<BuildingRecord> Generators(CampaignState s) =>
            s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                                         && HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId) && b.ConstructionState == BuildingConstructionState.Operational).ToList();

        /// <summary>关掉核心以外的全部发电：只剩核心基础供电 20，超控阵列（优先级 2、耗电 ≥ 80）分不到电 → 缺电停机。</summary>
        private static List<BuildingRecord> CutPower(CampaignState s)
        {
            List<BuildingRecord> gens = Generators(s);
            foreach (BuildingRecord g in gens)
            {
                BuildingOps.TrySetEnabled(s, g.BuildingId, false, out _);
            }
            return gens;
        }

        private static void RestorePower(CampaignState s, List<BuildingRecord> gens)
        {
            foreach (BuildingRecord g in gens)
            {
                BuildingOps.TrySetEnabled(s, g.BuildingId, true, out _);
            }
        }

        private static WorkOrderRecord Build(CampaignState s, string targetId) => HomeValleyWorkOrders.FindActiveBuild(s, targetId);

        /// <summary>把基元仓里（没有就刻印一枚）这种固件装进第 <paramref name="slot"/> 号槽（下标 0 起）。</summary>
        private static string EquipAt(CampaignState s, string firmwareId, int slot)
        {
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            try
            {
                string chip = s.PrimitiveChips?.FirstOrDefault(c => c != null && c.CardDefId == firmwareId && c.State == PrimitiveChipState.Bag)?.PartId;
                if (chip == null)
                {
                    SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
                    if (!pr.Success)
                    {
                        Fail($"测试准备：刻印 {firmwareId} 失败（{pr.Message}）");
                        return null;
                    }
                    chip = pr.CreatedId;
                }
                SignalCoreResult r = SignalCoreService.TryEquip(s, chip, slot);
                if (!r.Success)
                {
                    Fail($"测试准备：{firmwareId} 装入 {slot + 1} 号槽失败：{r.Message}");
                    return null;
                }
                return chip;
            }
            finally
            {
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            }
        }

        /// <summary>一台带接入口的机器（重炮蓝图 2 号格标接入口；接入口配额 2）。</summary>
        private static int UplinkMachine(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            if (!cannon.TrySetUplink(2).Success)
            {
                Fail("测试准备：重炮蓝图 2 号格标接入口失败");
            }
            BlueprintVersionRecord version = cannon.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != BpUplink)
                .Append(new BlueprintRecord { BlueprintId = BpUplink, DisplayName = BpUplink, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            Vector2 at = HomeValleyLayout.Core.Position + new Vector2(5f, -5f);
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpUplink, HomeValleyLayout.RegionId, at, 400f, 400f, "Player", 1);
            if (!m.Success)
            {
                Fail("测试准备：登记机器失败：" + m.Message);
                return 0;
            }
            CircuitOpResult reg = MachineLoadoutRegistry.Register(s, m.LogicId, BpUplink, 1);
            if (!reg.Success)
            {
                Fail("测试准备：登记装配失败：" + reg.Message);
            }
            return m.LogicId;
        }

        /// <summary>经机器列表同一入口（<see cref="SignalUplinkService.Request"/>）真实接入这台机器，推真实帧直到过渡完成。</summary>
        private static bool UplinkFormally(CampaignState s, int logicId)
        {
            WorldSimulation.StepMany(2); // 新登记的机器在下一模拟步才有地点里的表现对象（与 FgSignalUplinkSelfCheck 同一准备）
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList, startCamera: false);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return false;
            }
            for (int n = 0; SignalUplinkService.IsPending && n < 200; n++)
            {
                WorldSimulation.Frame(0.05f);
            }
            return SignalUplinkService.IsUplinked(s, logicId);
        }

        /// <summary>接入中的机器现在真实编进去的接入口固件（战斗内核与直控读的同一个出口 <see cref="MachineLoadoutRegistry.ResolveForPilot"/>）。</summary>
        private static string[] Piloted(CampaignState s, int logicId)
        {
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, logicId, s.RandomSeed);
            return r.Success && r.Preview?.UplinkFirmwareIds != null ? r.Preview.UplinkFirmwareIds : Array.Empty<string>();
        }

        /// <summary>信号核按“生效的槽”插进这台机器的接入口（真实接入与预览同一条计算），返回插进去的固件。</summary>
        private static string[] Inserted(CampaignState s, int logicId) =>
            MachineLoadoutRegistry.PlanForUplink(s, logicId, SignalCoreService.ActiveContentIds(s))?.InsertedIds ?? Array.Empty<string>();

        private static string[] Compiled(CampaignState s, int logicId)
        {
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForUplink(s, logicId, s.RandomSeed, SignalCoreService.ActiveContentIds(s));
            return r.Success && r.Preview?.UplinkFirmwareIds != null ? r.Preview.UplinkFirmwareIds : Array.Empty<string>();
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] bm = lines.Where(l => l.StartsWith("BM\t", StringComparison.Ordinal)).ToArray();
            string[] rbm = ConfigSystem.Instance.Tables.TbBuildMaterial.DataList
                .Select(r => string.Join("\t", "BM", r.Id, r.TypeId, r.Tier.ToString(CultureInfo.InvariantCulture), r.ItemId, r.Amount.ToString(CultureInfo.InvariantCulture))).ToArray();
            string cxSrc = lines.FirstOrDefault(l => l.StartsWith("CX\tcodex.signal.override_array\t", StringComparison.Ordinal));
            CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.FirstOrDefault(x => x.Id == "codex.signal.override_array");
            Expect(code == 0 && bm.Length == 3 && bm.SequenceEqual(rbm) && cxSrc != null && cx != null && cx.Hooks.Contains(GuidanceHooks.OverrideArrayFirstBuilt),
                $"A1 新表 fg.TbBuildMaterial（{bm.Length} 行）与源数据 fgdata_override 逐字段一致；图鉴“超控阵列”入表、由“第一次建成”钩子解锁" + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));

            BuildingTier t1 = BuildingOps.TierRow(T, 1), t2 = BuildingOps.TierRow(T, 2), t3 = BuildingOps.TierRow(T, 3);
            bool tiers = BuildingOps.HasTiers(T) && BuildingOps.MaxTier(T) == 3 && t1 != null && t2 != null && t3 != null
                         && Mathf.Approximately(t1.PowerDemand, 80f) && Mathf.Approximately(t2.PowerDemand, 120f) && Mathf.Approximately(t3.PowerDemand, 160f)
                         && Mathf.Approximately(t1.Value, 1f) && Mathf.Approximately(t2.Value, 2f) && Mathf.Approximately(t3.Value, 3f)
                         && t1.UnlockRule == "research:signal.override_t1" && t2.UnlockRule == "research:signal.override_t2" && t3.UnlockRule == "research:signal.override_t3";
            BuildingGrid g = GridContent.Building(T);
            bool grid = g != null && g.FootprintW == 4 && g.FootprintH == 4 && g.MaxCount == 1 && g.Critical == 1 && g.Category == "signal" && g.Placeable == 1
                        && HomeValleyLayout.BuildProfile.TryGetValue(T, out (int ScrapCost, float Seconds) bp) && bp.ScrapCost == 120
                        && HomeValleyLayout.PowerProfile.TryGetValue(T, out (float PowerDemand, int PowerPriority) pp) && Mathf.Approximately(pp.PowerDemand, 80f);
            Expect(tiers && grid && SignalCoreService.InitialSlots + 3 == SignalCoreService.MaxSlots,
                $"A2 超控阵列：4×4、家园只能一座、关键建筑、建造菜单“信号”页；三级 T1～T3 每级 +1 槽（初始 {SignalCoreService.InitialSlots} + 3 = 最多 {SignalCoreService.MaxSlots}），" +
                $"耗电 80 / 120 / 160（FGR-ECO-020 初值），每级研究节点 research:signal.override_tN（{tiers}/{grid}）");

            IReadOnlyList<BuildMaterialNeed> n1 = BuildMaterials.NewBuild(T), n2 = BuildMaterials.Upgrade(T, 2), n3 = BuildMaterials.Upgrade(T, 3);
            bool keys = BuildMaterials.Problems.Count == 0 && n1.Count == 1 && n1[0].Item.Id == KeyT1 && n2.Count == 1 && n2[0].Item.Id == KeyT2 && n3.Count == 1 && n3[0].Item.Id == KeyT3
                        && new[] { n1[0], n2[0], n3[0] }.All(n => n.Item.Form == ItemForm.Vault && n.Amount == 1) && BuildMaterials.NewBuild("refinery_furnace").Count == 0;
            string s1 = BuildMaterials.SourceOf(n1[0].Item), s2 = BuildMaterials.SourceOf(n2[0].Item), s3 = BuildMaterials.SourceOf(n3[0].Item);
            bool sources = s1.Contains("寂听主脑") && s2.Contains("铸造主核心") && s3.Contains("余烬主机");
            Expect(keys && sources,
                $"A3 关键材料：新建 监听阵列核、升 T2 熔炉心、升 T3 超算残核（核心保管库，各 1 件）；来源文字写明哪个首领给出（“{s1}”“{s2}”“{s3}”）；没有额外材料的建筑为空（{keys}/{sources}）");

            string[] textKeys =
            {
                "building.override_array.name", "building.override_array.desc", "building.tier.value.override_array", "research.gate.pending", "research.gate.locked",
                "build.cost.extra", "build.cost.extra_short", "build.status.waiting_item", "build.status.waiting_item_returning", "build.hover.materials_extra", "bp.upgrade_extra", "bp.rebuild_keeps_key",
                "override.reason.no_power", "override.reason.unconnected", "override.reason.disabled", "override.reason.destroyed", "override.reason.removed",
                "signal.core.slot_offline", "signal.core.slot_locked_how", "signal.hud.core_offline", "uplink.hud.state.offline", "override.notify.offline",
                "override.notify.online", "override.panel.slots", "override.panel.offline", "override.panel.next", "override.panel.max", "bs.reason.working_override",
                "bs.reason.override_offline", "ui.build.confirm_refund_extra", "ui.build.confirm_override", "codex.signal.override_array.body", "research.node.signal.override_t2",
            };
            bool zh = textKeys.All(k => GameText.Has(k) && !GameText.ContainsMarker(GameText.Get(k)));
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = textKeys.All(k => !GameText.ContainsMarker(GameText.Get(k)) && GameText.Get(k).Length > 0 && !ContainsCjk(GameText.Get(k)));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.OverrideArrayFirstBuilt) && GuidanceHooks.Known.Contains(GuidanceHooks.OverrideArrayFirstOffline);
            bool notify = NotificationCatalog.TryGetType("override_offline", out NotifyTypeDef off) && off.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("override_online", out NotifyTypeDef on) && on.Tier == NotifyLevel.Info;
            Expect(zh && en && hooks && notify, $"A4 新文本中英都有（{textKeys.Length} 个）；2 个引导钩子；“超控阵列失效”（警告）/“超控阵列恢复”（信息）通知（{zh}/{en}/{hooks}/{notify}）");
        }

        private static bool ContainsCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── B 多材料施工（正式入口）──────────────────────────────────────────────────

        private static void CheckBuildMultiMaterial()
        {
            CampaignState s = NewWorld(7101);
            GridCell? at = Spot(s);
            if (!at.HasValue)
            {
                Fail("B 核心附近没有能放超控阵列的空地");
                return;
            }
            string research = ResearchGate.Describe(s, GridContent.Building(T).UnlockRule);
            bool unlocked = BuildCatalog.IsUnlocked(s, GridContent.Building(T).UnlockRule);
            GridOpResult placed = PlanHistory.Place(s, T, at.Value, 0);
            BuildingRecord ghost = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
            bool ghostOk = ghost != null && ghost.ConstructionRequired == 120 && ghost.ExtraMaterialIds != null && ghost.ExtraMaterialIds.SequenceEqual(new[] { KeyT1 })
                           && ghost.ExtraRequired[0] == 1 && ghost.ExtraDelivered[0] == 0 && Stock(s, KeyT1) == 0;
            Expect(unlocked && research.Contains("后续版本") && ghostOk,
                $"B1 研发树开放前研究门槛视为已满足并写明（“{research}”）；建造模式同一入口放下虚影：所需 废料 120 + 监听阵列核 ×1（核心保管库 0，可以先放，FGR-LOG-003）（{ghostOk}）");
            if (ghost == null)
            {
                return;
            }

            // 机器先把废料运齐，缺关键材料 → 等待材料，原因写是哪种、从哪里获得；进度不超过“每种材料已到 / 所需”的最小值（0）。
            int waitingNotes = NotifyCount("construction_waiting");
            bool waited = StepUntil(() => ghost.ConstructionDelivered >= 120 && Build(s, ghost.BuildingId)?.State == WorkOrderState.Waiting, 300);
            WorkOrderRecord order = Build(s, ghost.BuildingId);
            string status = order != null ? HomeValleyConstruction.DescribeStatus(s, order) : string.Empty;
            string hover = order != null ? HomeValleyConstruction.ExtraHoverLines(s, order) : string.Empty;
            bool capped = order != null && order.Progress <= 1e-3f && ghost.ConstructionState != BuildingConstructionState.Operational;
            bool reason = order != null && order.FailureReason != null && order.FailureReason.EndsWith(":" + KeyT1, StringComparison.Ordinal);
            string note = LastNotify("construction_waiting");
            Expect(waited && capped && reason && status.Contains("监听阵列核") && status.Contains("寂听主脑") && hover.Contains("监听阵列核 已到 0 / 1")
                   && NotifyCount("construction_waiting") > waitingNotes && note.Contains("监听阵列核"),
                $"B2 废料 120 运齐、缺关键材料：施工单“等待材料”（原因码 {order?.FailureReason}），进度停在 {order?.Progress:0.00} 秒；状态“{status}”；悬停“{hover.Replace("\n", " / ")}”；通知“{note}”");
            int machine = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId)?.LogicId ?? 0;
            HomeValleyWorkOrders.WorkOrderOpResult assign = HomeValleyWorkOrders.TryAssignConstruction(s, ghost.BuildingId, machine);
            string refused = assign.Success ? string.Empty : HomeValleyWorkOrders.DescribeCommandFailure(assign.FailureReason);
            Expect(!assign.Success && refused.Contains("监听阵列核"), $"B3 负向：右键派机器去建也被拒，写明缺什么、从哪里获得（“{refused}”）");

            // 关键材料进了核心保管库 → 有货自动继续：机器去取、送到现场、完工；投入记账、解锁第 3 槽、第一次建成钩子。
            Give(s, KeyT1, 1);
            bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 300);
            HomeValleyPowerGrid.Recompute(s);
            bool invested = ghost.InvestedScrap == 120 && BuildMaterials.InvestedCount(ghost) == 1 && ghost.InvestedExtraIds[0] == KeyT1 && ghost.InvestedExtraAmounts[0] == 1
                            && BuildMaterials.SiteCount(ghost) == 0 && Stock(s, KeyT1) == 0;
            bool slots = OverrideArrayService.BuiltTier(s) == 1 && SignalCoreService.UnlockedSlots(s) == 3 && SignalCoreService.ActiveSlots(s) == 3
                         && ghost.PowerState == BuildingPowerState.Powered && Mathf.Approximately(HomeValleyPowerGrid.DemandOf(ghost), 80f);
            BuildingStatus st = BuildingStatusService.Evaluate(s, ghost);
            Expect(built && invested && slots && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OverrideArrayFirstBuilt) && st.Kind == BuildingStatusKind.Working && st.Reason.Contains("第 3 槽"),
                $"B4 监听阵列核放进核心保管库后自动继续：机器取来、完工；投入 废料 120 + 监听阵列核 ×1（拆除全额退回），保管库 0；信号核 {SignalCoreService.UnlockedSlots(s)} 槽、生效 {SignalCoreService.ActiveSlots(s)}；" +
                $"有电、耗电 {HomeValleyPowerGrid.DemandOf(ghost)}；状态“{st.Reason}”（{built}/{invested}/{slots}）");
        }

        // ── C 槽位生效、断电失效、来电恢复（FGT-ECO-010）──────────────────────────────

        private static void CheckSlotsAndPower()
        {
            CampaignState s = NewWorld(7201);
            BuildingRecord arr = BuiltArray(s, 1);
            if (arr == null)
            {
                return;
            }
            // 只在第 3 槽装固件（1、2 号槽空着：接入口配额 2，第 3 槽的固件能不能插进去只看它生不生效）。
            string fw = FirmwareCatalog.FwOverloadId;
            bool lockedBefore = !SignalCoreService.IsSlotUnlocked(s, 3);
            string chip = EquipAt(s, fw, 2);
            int machine = UplinkMachine(s);
            // 审查修复（P2）：真实接入（机器列表同一入口 → 过渡 → 提交），之后断电 / 来电走真实的重编译链：
            // 电网结算出口 → 信号核版本 +1 → 下一模拟步 SignalUplinkService.SyncCoreEdits → NotifyChanged → ResolveForPilot。
            SignalUplinkService.ResetForTests();
            bool uplinked = UplinkFormally(s, machine);
            bool powered = arr.PowerState == BuildingPowerState.Powered;
            string[] in1 = Inserted(s, machine);
            string[] c1 = Compiled(s, machine);
            string[] p1 = Piloted(s, machine);
            string[] e1 = SignalUplinkService.EffectiveInserted(s, machine);
            var snap = new UplinkHudSnapshot();
            UplinkHudModel.BuildSlots(s, machine, snap);
            bool hudActive = snap.Slots.Any(x => x.Index == 2 && x.State == UplinkSlotState.Active);
            Expect(chip != null && uplinked && powered && lockedBefore && in1.Contains(fw) && c1.Contains(fw) && p1.Contains(fw) && e1.Contains(fw) && hudActive,
                $"C1 T1 有电：第 3 槽解锁；机器经正式入口真实接入（{uplinked}），装进的过载插进接入口并编进机器（插入 [{string.Join(",", in1)}]，" +
                $"驾驶结算 ResolveForPilot [{string.Join(",", p1)}]），接入 HUD 第 3 槽“生效”；第 4 槽仍锁着");

            // 断电：只剩核心基础供电 → 阵列缺电停机 → 第 3 槽失效：固件留在槽里、不插入、HUD / 信号核面板 / 通知写原因。
            int rev0 = SignalCoreService.Revision;
            int off0 = NotifyCount("override_offline");
            int rc0 = SignalUplinkService.RecompileNotifyCount;
            List<BuildingRecord> gens = CutPower(s);
            bool dark = arr.PowerState == BuildingPowerState.Brownout;
            Seconds(0.25f); // 下一个模拟步：接入中的机器按新的生效槽重编译（按信号核版本号，不是轮询）
            string[] p2 = Piloted(s, machine);
            bool recompiled = SignalUplinkService.RecompileNotifyCount > rc0 && SignalUplinkService.IsUplinked(s, machine) && !p2.Contains(fw)
                              && !SignalUplinkService.EffectiveInserted(s, machine).Contains(fw);
            string[] in2 = Inserted(s, machine);
            UplinkHudModel.BuildSlots(s, machine, snap);
            UplinkHudSlot hs = snap.Slots.FirstOrDefault(x => x.Index == 2);
            string hudText = UplinkHudModel.SlotStateText(hs);
            string line = SignalCoreService.SlotStatusLine(s, 2);
            string offNote = LastNotify("override_offline");
            bool kept = SignalCoreService.SlotPartId(s, 2) == chip && PrimitiveInventory.Find(s, chip)?.State == PrimitiveChipState.SignalCore
                        && SignalCoreService.CurrentContentIds(s)[2] == fw && SignalCoreService.ActiveContentIds(s)[2].Length == 0;
            Expect(gens.Count > 0 && dark && SignalCoreService.ActiveSlots(s) == 2 && SignalCoreService.UnlockedSlots(s) == 3 && !in2.Contains(fw) && kept
                   && hs.State == UplinkSlotState.Offline && hudText.Contains("缺电") && line.Contains("失效") && line.Contains("缺电")
                   && SignalCoreService.SummaryText(s).Contains("1 槽失效")
                   && NotifyCount("override_offline") == off0 + 1 && offNote.Contains("第 3 槽") && offNote.Contains("缺电") && SignalCoreService.Revision != rev0
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OverrideArrayFirstOffline) && recompiled,
                $"C2 断电（关掉 {gens.Count} 座发电）：阵列 {arr.PowerState}；接入中的机器真实重编译（重编译通知 {rc0} → {SignalUplinkService.RecompileNotifyCount}，" +
                $"驾驶结算 [{string.Join(",", p2)}] 不再含过载）；生效 {SignalCoreService.ActiveSlots(s)} / 解锁 {SignalCoreService.UnlockedSlots(s)}；固件留在第 3 槽但不插入（插入 [{string.Join(",", in2)}]）；" +
                $"HUD“{hudText}”；面板“{line}”；通知“{offNote}”；信号核版本变化让接入中的机器重编译");

            // 失效的槽仍可以装卸（只在家园）：卸下再装回。
            bool unload = SignalCoreService.TryUnequip(s, 2).Success && SignalCoreService.SlotPartId(s, 2).Length == 0;
            string again = EquipAt(s, fw, 2);
            Expect(unload && again != null, "C3 失效的槽仍是已解锁的槽：卸下回基元仓、再装回都可以（固件不会被系统挪走）");

            // 来电：自动恢复，不用重新装；发“恢复”通知。
            int on0 = NotifyCount("override_online");
            int rc1 = SignalUplinkService.RecompileNotifyCount;
            RestorePower(s, gens);
            string[] in3 = Inserted(s, machine);
            string onNote = LastNotify("override_online");
            Seconds(0.25f);
            string[] p3 = Piloted(s, machine);
            Expect(arr.PowerState == BuildingPowerState.Powered && SignalCoreService.ActiveSlots(s) == 3 && in3.Contains(fw) && NotifyCount("override_online") == on0 + 1 && onNote.Contains("第 3 槽")
                   && SignalUplinkService.RecompileNotifyCount > rc1 && p3.Contains(fw) && SignalUplinkService.EffectiveInserted(s, machine).Contains(fw),
                $"C4 来电后自动恢复：第 3 槽的固件重新插进接入口（[{string.Join(",", in3)}]），接入中的机器再次重编译（{rc1} → {SignalUplinkService.RecompileNotifyCount}，驾驶结算 [{string.Join(",", p3)}]），通知“{onNote}”");

            // 禁用：同样失效（原因写“已禁用”与办法），启用后恢复；重复结算不重复发通知。
            BuildingOps.TrySetEnabled(s, arr.BuildingId, false, out _);
            string disabledLine = SignalCoreService.SlotStatusLine(s, 2);
            int offAfterDisable = NotifyCount("override_offline");
            HomeValleyPowerGrid.Recompute(s);
            HomeValleyPowerGrid.Recompute(s);
            bool noDup = NotifyCount("override_offline") == offAfterDisable;
            BuildingOps.TrySetEnabled(s, arr.BuildingId, true, out _);
            Expect(disabledLine.Contains("已禁用") && OverrideArrayService.OfflineReason(s) == OverrideOfflineReason.None && SignalCoreService.ActiveSlots(s) == 3 && noDup,
                $"C5 禁用阵列：第 3 槽失效（“{disabledLine}”），启用后恢复；状态不变的重复结算不重复发通知（{noDup}）");
        }

        private static void CheckUnconnected()
        {
            CampaignState s = NewWorld(7251);
            // 离核心远、不在任何电塔覆盖里的一块空地（按地形找）：登记上去看电网结算，接上了就拿掉换更远的地方。
            BuildingRecord arr = null;
            for (float d = 30f; d <= 90f && arr == null; d += 6f)
            {
                GridCell? c = FgProductionSelfCheck.FindFree(s, T, d, d + 5f);
                if (!c.HasValue)
                {
                    continue;
                }
                BuildingRecord tryArr = BuiltArray(s, 1, c);
                if (tryArr != null && tryArr.PowerState == BuildingPowerState.Unpowered)
                {
                    arr = tryArr;
                }
                else if (tryArr != null)
                {
                    s.BuildingRecords = s.BuildingRecords.Where(b => b != tryArr).ToArray();
                    HomeGridService.MapFor(s);
                    HomeValleyPowerGrid.Recompute(s);
                }
            }
            if (arr == null)
            {
                Fail("C6 测试准备：核心 30～90 格内找不到电网覆盖外能放超控阵列的空地");
                return;
            }
            string line = SignalCoreService.SlotStatusLine(s, 2);
            Expect(arr != null && arr.PowerState == BuildingPowerState.Unpowered && OverrideArrayService.OfflineReason(s) == OverrideOfflineReason.Unconnected
                   && line.Contains("没接入电网") && SignalCoreService.UnlockedSlots(s) == 3 && SignalCoreService.ActiveSlots(s) == 2
                   && OverrideArrayService.FixText(OverrideOfflineReason.Unconnected).Contains("电塔"),
                $"C6 不在电网覆盖里：第 3 槽已解锁但失效（“{line}”），办法写“放电塔”");
        }

        // ── D 升级 T2 / T3 ─────────────────────────────────────────────────────────

        private static void CheckUpgradeTiers()
        {
            CampaignState s = NewWorld(7301);
            BuildingRecord arr = BuiltArray(s, 1);
            if (arr == null)
            {
                return;
            }
            // 研究门槛（FG5-RND-01 开放研发树后生效）：注入“研发树已开放”→ 没研究就不能升级 / 不能新建；研究完成后可以。
            ResearchGate.TreeAvailableOverrideForTests = () => true;
            bool lockedUp = !HomeGridService.CanUpgradeNow(s, arr, out _, out _, out GridReason why) && why.Code == GridBlockReason.UpgradeLocked;
            string whyText = why.Describe();
            bool lockedNew = !BuildCatalog.IsUnlocked(s, GridContent.Building(T).UnlockRule);
            s.Research.CompletedNodes = new[] { "signal.override_t1", "signal.override_t2" };
            bool openUp = HomeGridService.CanUpgradeNow(s, arr, out _, out int toTier, out _) && toTier == 2;
            string gateDone = ResearchGate.Describe(s, BuildingOps.TierRow(T, 2).UnlockRule);
            ResearchGate.TreeAvailableOverrideForTests = null;
            Expect(lockedUp && whyText.Contains("超控阵列 T2") && lockedNew && openUp && gateDone.Contains("已完成"),
                $"D1 研究门槛：研发树开放后没研究“信号 · 超控阵列 T2”不能升级（“{whyText}”）、没研究 T1 不能新建；研究完成后可以升（“{gateDone}”）");

            // 升 T2 不给熔炉心：废料差额运齐后等待熔炉心（写明从哪里获得），升级期间阵列照常运转（第 3 槽生效）。
            string upLine = ProductionPanelUIToolkit.UpgradeExtrasLine(s, T, 2);
            GridOpResult u2 = HomeGridService.TryUpgrade(s, arr.BuildingId);
            BuildingRecord ghost = u2.Success ? HomeGridService.FindBuilding(s, u2.BuildingId) : null;
            bool ghostOk = ghost != null && ghost.Tier == 2 && ghost.ConstructionRequired == 80 && ghost.ExtraMaterialIds != null && ghost.ExtraMaterialIds.SequenceEqual(new[] { KeyT2 });
            bool waiting = ghostOk && StepUntil(() => ghost.ConstructionDelivered >= 80 && Build(s, ghost.BuildingId)?.State == WorkOrderState.Waiting, 300);
            string st = Build(s, ghost?.BuildingId)?.FailureReason ?? string.Empty;
            bool running = SignalCoreService.ActiveSlots(s) == 3 && BuildingOps.TierOf(Arr(s)) == 1;
            Expect(upLine.Contains("熔炉心") && upLine.Contains("铸造主核心") && ghostOk && waiting && st.EndsWith(":" + KeyT2, StringComparison.Ordinal) && running,
                $"D2 升 T2：面板写“另需 熔炉心”与获取途径（“{upLine.Replace("\n", " / ")}”）；虚影带 T2、差额 80 废料 + 熔炉心 ×1；废料运齐后等熔炉心（{st}）；升级期间阵列照常运转（生效 {SignalCoreService.ActiveSlots(s)}）");

            float demand1 = HomeValleyPowerGrid.GetSummary(s).TotalDemand;
            Give(s, KeyT2, 1);
            bool t2 = StepUntil(() => BuildingOps.TierOf(Arr(s)) == 2, 300);
            arr = Arr(s);
            HomeValleyPowerGrid.Recompute(s);
            float demand2 = HomeValleyPowerGrid.GetSummary(s).TotalDemand;
            bool invested2 = BuildMaterials.InvestedCount(arr) == 2 && arr.InvestedExtraIds.Contains(KeyT2) && Stock(s, KeyT2) == 0;
            Expect(t2 && SignalCoreService.UnlockedSlots(s) == 4 && SignalCoreService.ActiveSlots(s) == 4 && Mathf.Approximately(HomeValleyPowerGrid.DemandOf(arr), 120f)
                   && Mathf.Approximately(demand2 - demand1, 40f) && invested2,
                $"D3 熔炉心到了自动完工：T2、信号核 4 槽全部生效；耗电 80 → {HomeValleyPowerGrid.DemandOf(arr)}（电网总需求 {demand1:0} → {demand2:0}）；投入记上熔炉心（{t2}/{invested2}）");

            Give(s, KeyT3, 1);
            GridOpResult u3 = HomeGridService.TryUpgrade(s, arr.BuildingId);
            bool t3 = u3.Success && StepUntil(() => BuildingOps.TierOf(Arr(s)) == 3, 300);
            arr = Arr(s);
            HomeValleyPowerGrid.Recompute(s);
            bool top = !HomeGridService.CanUpgradeNow(s, arr, out _, out _, out GridReason w3) && w3.Code == GridBlockReason.NoUpgrade;
            Expect(t3 && SignalCoreService.UnlockedSlots(s) == 5 && SignalCoreService.ActiveSlots(s) == 5 && Mathf.Approximately(HomeValleyPowerGrid.DemandOf(arr), 160f)
                   && top && Stock(s, KeyT3) == 0 && ProductionPanelUIToolkit.OverrideArrayLines(s, arr).Contains("最高级"),
                $"D4 超算残核已在保管库：升 T3 直接取料完工，信号核 5 槽全部生效、耗电 {HomeValleyPowerGrid.DemandOf(arr)}；T3 没有更高等级（{t3}/{top}）");
        }

        // ── E 负向：被摧毁、重建、拆除、取消、唯一 ─────────────────────────────────────

        private static void CheckDestroyedAndRebuild()
        {
            CampaignState s = NewWorld(7401);
            BuildingRecord arr = BuiltArray(s, 2);
            if (arr == null)
            {
                return;
            }
            string fw = FirmwareCatalog.FwTrailId;
            string chip = EquipAt(s, fw, 3); // 第 4 槽
            int machine = UplinkMachine(s);
            bool before = Inserted(s, machine).Contains(fw);
            int off0 = NotifyCount("override_offline");
            bool destroyed = HomeValleyPowerGrid.ApplyBuildingDestroyed(s, arr.BuildingId);
            arr = Arr(s);
            string line = SignalCoreService.SlotStatusLine(s, 3);
            bool kept = SignalCoreService.SlotPartId(s, 3) == chip && PrimitiveInventory.Find(s, chip)?.State == PrimitiveChipState.SignalCore;
            Expect(before && destroyed && kept && !Inserted(s, machine).Contains(fw) && SignalCoreService.UnlockedSlots(s) == 4 && SignalCoreService.ActiveSlots(s) == 2
                   && OverrideArrayService.OfflineReason(s) == OverrideOfflineReason.Destroyed && line.Contains("被摧毁") && NotifyCount("override_offline") == off0 + 1
                   && LastNotify("override_offline").Contains("第 3～4 槽"),
                $"E1 负向“阵列被摧毁时槽里有固件”：固件保留在第 4 槽（实例仍在信号核）但不再插入；第 3～4 槽失效（“{line}”），通知“{LastNotify("override_offline")}”");

            // 重建只收废料（关键材料留在建筑里，不要求再找一件）；重建完工后自动恢复，等级保留。
            int cost = BuildingOps.RebuildCost(arr, out _);
            bool ordered = BuildingOps.TryOrderRepair(s, arr.BuildingId, out string msg);
            bool rebuilt = ordered && StepUntil(() => Arr(s).ConstructionState == BuildingConstructionState.Operational, 400);
            arr = Arr(s);
            HomeValleyPowerGrid.Recompute(s);
            bool keys = BuildMaterials.InvestedCount(arr) == 2 && Stock(s, KeyT1) == 0 && Stock(s, KeyT2) == 0;
            Expect(cost == 120 + 80 && ordered && rebuilt && BuildingOps.TierOf(arr) == 2 && SignalCoreService.ActiveSlots(s) == 4 && Inserted(s, machine).Contains(fw) && keys,
                $"E2 重建：只收废料 {cost}（= 120 + T2 差额 80，不要关键材料，“{msg}”）→ 机器重建完工仍是 T2、第 4 槽的固件自动恢复生效；关键材料投入仍记在建筑上（{rebuilt}/{keys}）");
        }

        private static void CheckDemolishAndRefunds()
        {
            // E3 拆除：关键材料全额退回核心保管库，多出的槽锁回去，里面的固件可以卸、不能再装。
            CampaignState s = NewWorld(7501);
            BuildingRecord arr = BuiltArray(s, 2);
            if (arr == null)
            {
                return;
            }
            string chip = EquipAt(s, FirmwareCatalog.FwTrailId, 3);
            string extra = SignalCoreService.TryPrintFirmwareChip(s, FirmwareCatalog.FwOverloadId).CreatedId;
            int off0 = NotifyCount("override_offline");
            GridOpResult mark = HomeGridService.TryToggleDemolish(s, arr.BuildingId);
            bool gone = mark.Success && StepUntil(() => Arr(s) == null, 300);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(30f); // 退回的关键材料若落地，等机器搬回保管库
            bool refunded = Stock(s, KeyT1) == 1 && Stock(s, KeyT2) == 1 && HomeValleyWorkOrders.LastDemolishExtraRefund == 2;
            SignalCoreResult equip = extra != null ? SignalCoreService.TryEquip(s, extra, 2) : default;
            SignalCoreResult unequip = SignalCoreService.TryUnequip(s, 3);
            Expect(gone && refunded && SignalCoreService.UnlockedSlots(s) == 2 && SignalCoreService.ActiveSlots(s) == 2 && !equip.Success && equip.Code == SignalCoreService.CodeSlotLocked
                   && unequip.Success && PrimitiveInventory.Find(s, chip)?.State == PrimitiveChipState.Bag && NotifyCount("override_offline") == off0 + 1
                   && LastNotify("override_offline").Contains("已拆除"),
                $"E3 拆除：监听阵列核、熔炉心各 1 件退回核心保管库（{refunded}）；信号核回到 2 槽；锁回去的第 4 槽里的固件能卸下（{unequip.Message}），不能再往第 3 槽装（{equip.Message}）；通知“{LastNotify("override_offline")}”");

            // E4 家园只能有一座（虚影也算）。
            GridCell? at = Spot(s);
            GridOpResult p1 = at.HasValue ? PlanHistory.Place(s, T, at.Value, 0) : default;
            GridCell? at2 = Spot(s, 10f, 30f);
            GridPlacementResult second = at2.HasValue ? HomeGridService.ValidatePlacement(s, T, at2.Value, 0) : null;
            Expect(p1.Success && second != null && !second.Ok, $"E4 家园只能有一座超控阵列：已有一座虚影时第二座被拒（“{second?.Describe()}”）");

            // E5 取消虚影：已运到现场的关键材料全额退回核心保管库（废料同样守恒）。
            BuildingRecord ghost = p1.Success ? HomeGridService.FindBuilding(s, p1.BuildingId) : null;
            int scrap0 = Mathf.FloorToInt(s.Scrap);
            bool delivered = ghost != null && StepUntil(() => ghost.ExtraDelivered != null && ghost.ExtraDelivered[0] == 1, 300);
            int scrapOnSite = ghost?.ConstructionDelivered ?? 0;
            int keyInVault = Stock(s, KeyT1);
            GridOpResult cancel = ghost != null ? HomeGridService.TryToggleDemolish(s, ghost.BuildingId) : default;
            Seconds(30f);
            int ground = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == KeyT1).Sum(g => g.Amount);
            Expect(delivered && keyInVault == 0 && cancel.Outcome == GridOpResult.Kind.PlanCancelled && Stock(s, KeyT1) + ground == 1 && Stock(s, KeyT1) == 1,
                $"E5 取消虚影：监听阵列核已运到现场（保管库 {keyInVault}）→ 取消 → 全额退回核心保管库（{Stock(s, KeyT1)}）；现场的 {scrapOnSite} 废料同样退回（取消前库存 {scrap0} → {Mathf.FloorToInt(s.Scrap)}）");

            // E6 取消升级：已运到的熔炉心退回；E7 施工中虚影被摧毁：关键材料按进度比例掉落、不消失（没开工 = 全部留在现场）。
            CampaignState s2 = NewWorld(7511);
            BuildingRecord a2 = BuiltArray(s2, 1);
            Give(s2, KeyT2, 1);
            GridOpResult up = a2 != null ? HomeGridService.TryUpgrade(s2, a2.BuildingId) : default;
            BuildingRecord ug = up.Success ? HomeGridService.FindBuilding(s2, up.BuildingId) : null;
            bool upDelivered = ug != null && StepUntil(() => ug.ExtraDelivered != null && ug.ExtraDelivered[0] == 1, 300);
            GridOpResult upCancel = ug != null ? HomeGridService.TryToggleDemolish(s2, a2.BuildingId) : default;
            Seconds(30f);
            Expect(upDelivered && upCancel.Success && Stock(s2, KeyT2) == 1 && HomeGridService.FindRelocationGhost(s2, a2.BuildingId) == null && BuildingOps.TierOf(Arr(s2)) == 1,
                $"E6 取消升级：已运到的熔炉心全额退回核心保管库（{Stock(s2, KeyT2)}），阵列仍是 T1");

            CampaignState s3 = NewWorld(7521);
            Give(s3, KeyT1, 1);
            GridCell? at3 = Spot(s3);
            GridOpResult p3 = at3.HasValue ? PlanHistory.Place(s3, T, at3.Value, 0) : default;
            BuildingRecord g3 = p3.Success ? HomeGridService.FindBuilding(s3, p3.BuildingId) : null;
            bool keyThere = g3 != null && StepUntil(() => g3.ExtraDelivered != null && g3.ExtraDelivered[0] == 1, 300);
            bool siteHit = g3 != null && HomeValleyConstruction.OnSiteDestroyed(s3, g3.BuildingId);
            int keyKept = g3?.ExtraDelivered?[0] ?? 0;
            int keyGround = (s3.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == KeyT1).Sum(g => g.Amount);
            Expect(keyThere && siteHit && keyKept + keyGround == 1 && Stock(s3, KeyT1) == 0,
                $"E7 施工中的虚影被摧毁：关键材料不消失（留在现场 {keyKept} + 掉在地上 {keyGround} = 1）；之后照常施工");
        }

        // ── I 面板（真 UXML）──────────────────────────────────────────────────────

        private static void CheckPanels()
        {
            CampaignState s = NewWorld(7601);
            BuildingRecord arr = BuiltArray(s, 1);
            if (arr == null)
            {
                return;
            }
            string fw = FirmwareCatalog.FwOverloadId;
            EquipAt(s, fw, 2);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(arr.BuildingId);
                panel.Refresh();
                string tier = panel.TierText;
                string upLine = panel.UpgradeLineText;
                bool ok1 = panel.PanelVisible && tier.Contains("已解锁 3，生效 3") && tier.Contains("下一级 T2") && tier.Contains("熔炉心") && tier.Contains("铸造主核心")
                           && upLine.Contains("另需") && upLine.Contains("熔炉心") && panel.ReasonText.Contains("第 3 槽");
                List<BuildingRecord> gens = CutPower(s);
                panel.Refresh();
                string tierDark = panel.TierText;
                bool ok2 = tierDark.Contains("多出的槽失效") && tierDark.Contains("缺电") && panel.ReasonText.Contains("失效");
                RestorePower(s, gens);
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, arr.BuildingId);
                panel.Refresh();
                bool ok3 = panel.RepairButton.tooltip.Contains("只收废料") && panel.TierText.Contains("被摧毁");
                bool markers = !GameText.ContainsMarker(tier + upLine + tierDark + panel.TierText + panel.ReasonText);
                Expect(ok1 && ok2 && ok3 && markers,
                    $"I1 建筑面板（真 UXML）：槽位行“{tier.Replace("\n", " / ")}”；升级行“{upLine.Replace("\n", " / ")}”；断电后“{tierDark.Replace("\n", " / ")}”；被摧毁后“重建”写只收废料（{ok1}/{ok2}/{ok3}/{markers}）");
                BuildingOps.TryOrderRepair(s, arr.BuildingId, out _);
                StepUntil(() => Arr(s).ConstructionState == BuildingConstructionState.Operational, 400);
                HomeValleyPowerGrid.Recompute(s);
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }

            // 信号核面板与 HUD 按钮：失效槽写原因、按钮写“（1 槽失效）”；锁定槽悬停写解锁要什么、从哪里获得。
            VisualElement hudRoot = FgProductionSelfCheck.MountUxml(UiKitFolder + "SignalCorePanel.uxml", out GameObject hudGo);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            try
            {
                var hud = hudGo.AddComponent<SignalCoreHudUIToolkit>();
                hud.BindView(hudRoot);
                hud.SetOpen(true);
                hud.Refresh();
                string onSlot = hud.SlotText(2);
                string onEntry = hud.EntryText;
                List<BuildingRecord> gens = CutPower(s);
                hud.Refresh();
                string offSlot = hud.SlotText(2);
                string offEntry = hud.EntryText;
                string lockedTip = SignalCoreService.SlotStatusLine(s, 3);
                RestorePower(s, gens);
                hud.Refresh();
                Expect(onSlot.Contains(FirmwareKinds.DisplayName(fw)) && !onSlot.Contains("失效") && onEntry.EndsWith("1/3", StringComparison.Ordinal)
                       && offSlot.Contains("失效") && offSlot.Contains("缺电") && offEntry.Contains("1 槽失效") && hud.SlotText(3).Contains("未解锁")
                       && lockedTip.Contains("T2") && lockedTip.Contains("熔炉心") && lockedTip.Contains("铸造主核心") && !hud.EntryText.Contains("失效"),
                    $"I2 信号核面板（真 UXML）：有电“{onSlot}”/按钮“{onEntry}”；断电“{offSlot.Replace("\n", " / ")}”/按钮“{offEntry}”；锁定的第 4 槽说明“{lockedTip}”；来电后按钮“{hud.EntryText}”");
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                UiEscapeStack.Clear();
                Object.DestroyImmediate(hudGo);
            }

            // I3 审查修复（P2）：建筑面板电力行按等级写耗电（T3 = 160），与状态行、电网结算同一口径。
            CampaignState s3 = NewWorld(7611);
            BuildingRecord a3 = BuiltArray(s3, 3);
            if (a3 == null)
            {
                return;
            }
            VisualElement root3 = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go3);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel3 = go3.AddComponent<ProductionPanelUIToolkit>();
                panel3.BindView(root3);
                ProductionPanelUIToolkit.Open(a3.BuildingId);
                panel3.Refresh();
                string power = panel3.PowerText;
                Expect(power.Contains("耗电 160") && !power.Contains("耗电 80") && panel3.ReasonText.Contains("160") && Mathf.Approximately(HomeValleyPowerGrid.DemandOf(a3), 160f),
                    $"I3 T3 阵列的面板电力行按等级写耗电：“{power}”；状态行“{panel3.ReasonText}”");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go3);
            }
        }

        // ── E8 负向：取着关键材料的机器在去现场途中阵亡 ─────────────────────────────────────

        private static void CheckCarrierKilled()
        {
            CampaignState s = NewWorld(7531);
            Give(s, KeyT1, 1);
            GridCell? at = Spot(s);
            GridOpResult placed = at.HasValue ? PlanHistory.Place(s, T, at.Value, 0) : default;
            BuildingRecord ghost = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
            if (ghost == null)
            {
                Fail("E8 测试准备：放不下超控阵列虚影");
                return;
            }
            MachineRecord carrier = null;
            Vector2 fetchAt = Vector2.zero;
            bool picked = StepUntil(() =>
            {
                // 取料腿出发前记下目的地：关键材料只在核心保管库，取料点是归还核心（不是最近的仓库）。
                WorkOrderRecord o = Build(s, ghost.BuildingId);
                if (o != null && o.Leg == 1 && HomeValleyConstruction.NextFetchMaterial(s, o) == KeyT1)
                {
                    fetchAt = HomeValleyWorkOrders.ResolveWorkPosition(s, o);
                }
                carrier = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive && m.Cargo != null && m.Cargo.Any(c => c.ResourceType == KeyT1 && c.Amount > 0));
                return carrier != null;
            }, 300);
            int others = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m != carrier);
            if (!picked || others == 0)
            {
                Fail($"E8 测试准备：没有机器取到监听阵列核（{picked}），或家园里没有别的机器（{others}）");
                return;
            }
            // 货舱里装着关键材料、往现场走的途中被打死。
            MachineRegistry.ApplyDamage(carrier.LogicId, 1e6f);
            Seconds(0.5f);
            int returning = HomeValleyWorkOrders.ReturningAmount(s, KeyT1);
            int onGround = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == KeyT1).Sum(g => g.Amount);
            bool dropped = !carrier.IsAlive && Stock(s, KeyT1) == 0 && ghost.ExtraDelivered[0] == 0 && returning == 1;
            // 观察等待期间的状态文字：关键材料正被搬回时，不能写“去打首领”。
            var seen = new List<string>();
            // FG4-E2E-01（DEBT-FG4ECO11-06）：工单面板的原因行、右键被拒的说明与施工队列 / 悬停同一口径（同一时刻逐字相同，也写“搬回”）。
            var panelSeen = new List<string>();
            int panelSame = 0;
            int panelChecks = 0;
            bool back = StepUntil(() =>
            {
                WorkOrderRecord o = Build(s, ghost.BuildingId);
                if (o != null && o.State == WorkOrderState.Waiting && HomeValleyWorkOrders.ReturningAmount(s, KeyT1) > 0)
                {
                    string st = HomeValleyConstruction.DescribeStatus(s, o);
                    if (!seen.Contains(st))
                    {
                        seen.Add(st);
                    }
                    if (!string.IsNullOrEmpty(o.FailureReason) && o.FailureReason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, StringComparison.Ordinal))
                    {
                        string panel = GameLogic.UI.WorkOrder.WorkOrderPanelUIToolkit.ReasonText(s, o);
                        string cmd = HomeValleyConstruction.DescribeMaterialsReason(s, o.FailureReason);
                        panelChecks++;
                        panelSame += panel == st ? 1 : 0;
                        foreach (string t in new[] { panel, cmd })
                        {
                            if (!panelSeen.Contains(t))
                            {
                                panelSeen.Add(t);
                            }
                        }
                    }
                }
                return ghost.ConstructionState == BuildingConstructionState.Operational;
            }, 400);
            bool honest = seen.All(t => t.Contains("搬回") && !t.Contains("寂听主脑"));
            bool panelHonest = panelChecks > 0 && panelSame == panelChecks && panelSeen.All(t => t.Contains("搬回") && !t.Contains("寂听主脑"));
            Expect(panelHonest,
                $"E8b DEBT-FG4ECO11-06：关键材料正被搬回时，工单面板原因行与施工队列逐字相同（{panelSame}/{panelChecks} 次），工单面板 / 右键被拒说明都写“搬回”、不提示去打首领（“{string.Join(" | ", panelSeen)}”）");
            int groundAfter = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == KeyT1).Sum(g => g.Amount);
            bool stuck = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Any(o => o.Kind == WorkOrderKind.Haul && o.IssuerId == "return" && o.FailureReason == HomeValleyConstruction.ReturnWaitReason);
            bool coreFetch = (fetchAt - HomeValleyLayout.Core.Position).sqrMagnitude < 0.01f;
            Expect(dropped && back && honest && groundAfter == 0 && !stuck && BuildMaterials.InvestedCount(ghost) == 1 && Stock(s, KeyT1) == 0 && coreFetch,
                $"E8 负向“取着关键材料的机器途中阵亡”：货舱里的监听阵列核落地（地上 {onGround}，正被搬回 {returning}），系统返还搬运单把它搬回核心保管库（没停在“等空间”：{!stuck}），" +
                $"别的机器再取来、阵列完工（{back}），投入记上关键材料；等待期间状态“{string.Join(" | ", seen)}”（不提示再去打首领：{honest}）；关键材料取料点是归还核心（{coreFetch}）");
        }

        // ── F 存读档 ─────────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == T).OrderBy(b => b.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(b.BuildingId).Append(':').Append((int)b.ConstructionState).Append('/').Append(b.Tier).Append('/').Append((int)b.PowerState).Append('/')
                    .Append(b.ConstructionRequired).Append('+').Append(b.ConstructionDelivered).Append('/')
                    .Append(string.Join(",", b.ExtraMaterialIds ?? Array.Empty<string>())).Append('=').Append(string.Join(",", b.ExtraDelivered ?? Array.Empty<int>()))
                    .Append('/').Append(string.Join(",", b.ExtraRequired ?? Array.Empty<int>())).Append("/inv:")
                    .Append(string.Join(",", (b.InvestedExtraIds ?? Array.Empty<string>()).Select((x, i) => x + "x" + (b.InvestedExtraAmounts != null && i < b.InvestedExtraAmounts.Length ? b.InvestedExtraAmounts[i] : 0))))
                    .Append('/').Append(b.InvestedScrap).Append('|');
            }
            foreach (WorkOrderRecord o in (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o)).OrderBy(o => o.TargetId, StringComparer.Ordinal))
            {
                sb.Append(o.TargetId).Append('#').Append((int)o.State).Append('/').Append(o.Leg).Append('/').Append(o.FailureReason).Append('|');
            }
            sb.Append("slots=").Append(SignalCoreService.UnlockedSlots(s)).Append('/').Append(SignalCoreService.ActiveSlots(s))
                .Append(" seen=").Append(s.SignalCore.OverrideBuiltTierSeen).Append('/').Append(s.SignalCore.OverrideActiveTierSeen)
                .Append(" core=").Append(string.Join(",", SignalCoreService.CurrentContentIds(s)))
                .Append(" vault=").Append(Stock(s, KeyT1)).Append(',').Append(Stock(s, KeyT2)).Append(',').Append(Stock(s, KeyT3))
                .Append(" scrap=").Append(Mathf.FloorToInt(s.Scrap))
                .Append(" research=").Append(string.Join(",", s.Research?.CompletedNodes ?? Array.Empty<string>()));
            return sb.ToString();
        }

        /// <summary>存读档 / 倍速 / 观察共用的场景：正式入口放超控阵列虚影（关键材料在保管库）→ 机器施工 → 完工后装固件 → 升 T2（熔炉心在保管库）。</summary>
        private static bool LayScenario(CampaignState s)
        {
            GridCell? at = Spot(s);
            if (!at.HasValue)
            {
                return false;
            }
            Give(s, KeyT1, 1);
            Give(s, KeyT2, 1);
            s.Research.CompletedNodes = new[] { "signal.override_t1" };
            return PlanHistory.Place(s, T, at.Value, 0).Success;
        }

        private static void ContinueScenario(CampaignState s)
        {
            BuildingRecord a = Arr(s);
            if (a != null && a.ConstructionState == BuildingConstructionState.Operational && BuildingOps.TierOf(a) == 1 && HomeGridService.FindRelocationGhost(s, a.BuildingId) == null)
            {
                if (SignalCoreService.SlotPartId(s, 2).Length == 0)
                {
                    EquipAt(s, FirmwareCatalog.FwOverloadId, 2);
                }
                HomeGridService.TryUpgrade(s, a.BuildingId);
            }
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7701, scrap: 900);
            if (!LayScenario(s))
            {
                Fail("F 放不下场景");
                return;
            }
            bool t1 = StepUntil(() => Arr(s)?.ConstructionState == BuildingConstructionState.Operational, 300);
            ContinueScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 6 + 3); // 升级取料进行到一半时存档
            // 断电后存档：失效状态与“上次看到的等级”进存档，读档后不重复发通知。
            List<BuildingRecord> gens = CutPower(s);
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            RestorePower(s, gens);
            WorldSimulation.StepMany(GameClock.StepHz * 90);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded, out int notesAfterLoad)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                OverrideArrayService.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                notesAfterLoad = -1;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                int n0 = NotifyCount("override_offline") + NotifyCount("override_online");
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                notesAfterLoad = NotifyCount("override_offline") + NotifyCount("override_online") - n0;
                foreach (BuildingRecord g in rr.State.BuildingRecords.Where(b => b != null && b.ConstructionState == BuildingConstructionState.Disabled
                                                                                && HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId)))
                {
                    BuildingOps.TrySetEnabled(rr.State, g.BuildingId, true, out _);
                }
                WorldSimulation.StepMany(GameClock.StepHz * 90);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1, out int notes1);
            string second = RunFromSave(out _, out _);
            Expect(t1 && save.Success && loaded1 == before && before.Contains("furnace_heart") && before.Contains("inv:listening_array_corex1") && notes1 == 0,
                "F1 真文件存读档：超控阵列等级、升级虚影的额外材料（所需 / 已到）、投入的关键材料、生效 / 解锁槽数、“上次看到的等级”、核心保管库、研究节点写进存档，读档后逐字段一致；读档不补发失效通知"
                + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}") + $"（读档时新通知 {notes1}）");
            Expect(first == continuous && second == first && continuous.Contains("slots=4/4"),
                "F2 读档后接着跑 90 游戏秒（来电恢复、熔炉心取料、升级完工到 T2、第 4 槽生效），与不存档一直跑逐位一致；同一存档读两次结果一致"
                + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        // ── G / H 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 900);
            pausedHeld = true;
            if (!LayScenario(s))
            {
                return "放不下";
            }
            WorldSimulation.StepMany(GameClock.StepHz * 20);
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
            long target = GameClock.Ticks + GameClock.StepHz * 150;
            int frames = 0;
            bool continued = false;
            while (GameClock.Ticks < target && frames < 60 * 900)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
                if (!continued && Arr(s)?.ConstructionState == BuildingConstructionState.Operational)
                {
                    // 完工后的那一步（与倍速无关的游戏时刻）接着装固件、下升级单。
                    ContinueScenario(s);
                    continued = true;
                }
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
                string snap = RunScenario(7801, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下" && reference.Contains("slots=4/4"),
                "G 暂停中（120 帧）超控阵列施工（废料与关键材料的取料）不动；0.5x / 1x / 2x / 3x 跑同样的 150 游戏秒（新建 → 装固件 → 升 T2）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7802, true, 1f, false, out _);
            string unseen = RunScenario(7802, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "H 同一组（关键材料取料、新建、升级、槽位生效）在观察与不观察家园时跑 150 游戏秒逐字段一致（FGR-BASE-021）"
                                                     + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── J 性能 ───────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7901);
            BuildingRecord arr = BuiltArray(s, 3);
            for (int i = 0; i < 300; i++)
            {
                // 家园后期规模：再登记 300 条小建筑记录（电塔），检验信号核每帧查询与建筑数无关。
                GridCell? c = FgProductionSelfCheck.FindFree(s, "power_pole", 5f, 60f);
                if (!c.HasValue)
                {
                    break;
                }
                FgProductionSelfCheck.Built(s, "power_pole", "ovperf" + i, c.Value);
            }
            int n = s.BuildingRecords.Length;
            SignalCoreService.ActiveSlots(s); // 建缓存
            var sw = Stopwatch.StartNew();
            int sink = 0;
            for (int r = 0; r < 20000; r++)
            {
                sink += SignalCoreService.ActiveSlots(s) + SignalCoreService.UnlockedSlots(s) + OverrideArrayService.StateKey(s);
            }
            sw.Stop();
            double perQuery = sw.Elapsed.TotalMilliseconds * 1000.0 / 20000.0;
            sw.Restart();
            for (int r = 0; r < 20; r++)
            {
                HomeValleyPowerGrid.Recompute(s);
            }
            sw.Stop();
            double recompute = sw.Elapsed.TotalMilliseconds / 20.0;
            Expect(arr != null && n >= 100 && sink != 0, $"J 场景：T3 超控阵列 + {n} 座建筑");
            PerfLines.Add($"信号核每帧查询（生效槽 + 解锁槽 + 阵列状态键）每次 {perQuery:F3} µs（{n} 座建筑，阵列列表按建筑数组缓存）；电网结算（含超控阵列变化检测）每次 {recompute:F3} ms");
            PerfGate.Expect(perQuery <= 5.0 && recompute <= 8.0,
                $"J 性能：信号核每帧查询每次 {perQuery:F3} µs（阈值 5 µs，与建筑数无关）；电网结算每次 {recompute:F3} ms（阈值 8 ms，只在状态变化时调用；Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perQuery, 5.0, "信号核查询 µs"), PerfGate.Le(recompute, 8.0, "电网结算 ms") }, Expect, Line);
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
                PowerEnvironment.ResetForTests();
                ResearchGate.ResetForTests();
                SignalCoreService.ResetForTests();
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
