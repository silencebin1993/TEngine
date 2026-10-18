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
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
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
    /// FG8-DROP-01 投送站与投送舱的自动验收（FG08 FGR-EXP-013～016；FG13 FGU-46；FGT-EXP-003“投送与回收：着陆条件、防空拒绝、燃油、冷却、载量；静默夜中在飞的投送舱”；
    /// FG08 第 8 节负向“落点在防空覆盖内 / 静默夜开始时在飞 / 投送站被摧毁”；卡片负向“燃油不足 / 全部在冷却 / 落点在飞行途中被敌人占领”；承接 DEBT-FG1SIG03-02、DEBT-FG5RND01-01 投送站 T2 节点）。
    /// 全部起真实系统：真实家园（世界模拟、电网、管线内核、家园战斗内核里的机器）、正式入口（投送站面板按钮、战略地图选点、建筑面板）、真文件存读档。
    /// A 数据；B 研发门控 / 装填 / 自动补满 / T2 / 拆除退舱；C 投送（机器 + 货物、飞行时间、燃油、冷却、接入被拒）；D 负向矩阵（发射被拒不改世界）；E 飞行途中落点被占领；
    /// F 投送站被摧毁（改降 / 没有别的站）；G 静默夜；H 回收（机器 + 货舱卸货、地上货物、装不下、行军成员）；I 燃油管线；J 存读档与读档接着跑；K 暂停与 0.5x～3x；L 观察 / 不观察；
    /// M 种子集；N 界面（面板、建筑面板、战略地图选点 / 防空红区 / 悬停、布局探针）；O 性能；P 旧档 / 坏值。已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgDropPodSelfCheck）。
    /// </summary>
    public static class FgDropPodSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 9;
        private const string Station = DropPodService.StationTypeId;
        private const string Pod = DropPodService.PodItemId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 投送站与投送舱")]
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
            Line("\n[投送站与投送舱] 投送 / 回收、着陆条件、防空拒绝、燃油、冷却、载量、静默夜自动驾驶、投送站被毁改降（FG8-DROP-01）");
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
            Func<bool> treeBefore = ResearchGate.TreeAvailableOverrideForTests;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdrop-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                ResearchCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程）；" +
                     "投送舱的航线、着陆、改降在热更层（Editor 下 Mono JIT，按事件步 O(舱数)，真机另测 FG15-SYS-02）；舱里的机器不在任何战斗内核里，下舱后在家园内核里建出单位");

                Step(CheckData);
                Step(CheckGatesAndLoading);
                Step(CheckDelivery);
                Step(CheckNegatives);
                Step(CheckAntiAirFog);
                Step(CheckOccupiedInFlight);
                Step(CheckStationDestroyed);
                Step(CheckDivertRules);
                Step(CheckSilentNight);
                Step(CheckRecall);
                Step(CheckBusyMachines);
                Step(CheckFuelPipe);
                Step(CheckSaveLoad);
                Step(CheckLoadContinuation);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSilentNightObserved);
                Step(CheckSeedSet);
                Step(CheckUi);
                Step(CheckPerformance);
                Step(CheckOldSave);
                Step(CheckStrandedPayload);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"投送自检抛异常：{e}");
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
                ResearchGate.TreeAvailableOverrideForTests = treeBefore;
                SilentNightService.ResetForTests();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                DropStationPanelUIToolkit.InWorldOverrideForTests = false;
                DropStationPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                DropPodService.ResetSessionState();
                StandingRuleService.ResetForTests();
                TurretService.ResetSessionState();
                SignalUplinkService.SilentNightProvider = null;
                SignalCoreService.ResetForTests();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                HomeValleyCombatTargets.ResetSessionState();
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
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
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
            Line($"  · [投送站与投送舱] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>一个有电的家园，研发“远征 · 投送站”已研究，核心附近按种子地形放一座建成、有电的投送站（B25），站里 <paramref name="pods"/> 个舱、<paramref name="fuelLiters"/> 升油。</summary>
        private static CampaignState NewWorld(int seed, out BuildingRecord station, bool observe = true, int pods = 4, float fuelLiters = 1000f, bool research = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            SignalCoreService.ResetForTests();
            StandingRuleService.ResetForTests();
            SilentNightService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            SignalUplinkService.SilentNightProvider = null;
            _seq = 900;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 2000);
            FgProductionSelfCheck.PowerUp(s);
            for (int i = 0; i < 2; i++)
            {
                GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
                if (g.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "dp_gen" + i, g.Value);
                }
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (research)
            {
                ResearchService.CompleteForTests(s, "expedition.drop_station");
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            station = research ? PlaceStation(s, CoreCenter(s), 10f, 34f, pods, fuelLiters) : null;
            return s;
        }

        private static BuildingRecord PlaceStation(CampaignState s, Vector2 center, float from, float to, int pods, float fuelLiters)
        {
            BuildingRecord st = PlaceNear(s, Station, center, from, to);
            if (st == null)
            {
                return null;
            }
            DropPodService.Sync(s);
            DropStationRecord r = DropPodService.FindStation(s, st.BuildingId);
            if (r != null)
            {
                r.HeldFuelMl = (long)Math.Round(fuelLiters * 1000.0);
                r.AutoFill = false;
            }
            if (pods > 0)
            {
                HomeInventory.Add(s, ItemCatalog.Find(Pod), pods, clampToSpace: false);
                DropPodService.LoadPods(s, r, st, pods);
            }
            return st;
        }

        private static BuildingRecord Register(CampaignState s, string type, GridCell c, int rotation = 0)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, rotation),
                Health = BuildingOps.MaxDurability(type),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return r;
        }

        private static void Unregister(CampaignState s, BuildingRecord b)
        {
            s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
        }

        private static BuildingRecord PlaceNear(CampaignState s, string type, Vector2 center, float from, float to)
        {
            bool powered = HomeValleyLayout.PowerProfile.ContainsKey(type);
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(center.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(center.y + Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = Register(s, type, c);
                    if (powered && (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered))
                    {
                        Unregister(s, b);
                        continue;
                    }
                    return b;
                }
            }
            Fail($"测试准备：{center} 周围 {from}～{to} 格找不到能放 {type} 的空地");
            return null;
        }

        private static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        /// <summary>站旁一台机器（站着不动：工作优先级全 0，不让工单把它派走）。</summary>
        private static int SpawnMachine(CampaignState s, Vector2 at, int cargoScrap = 0)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                Fail("测试准备：刷机器失败：" + r.Message);
                return 0;
            }
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                if (!string.IsNullOrEmpty(rec.BlueprintId))
                {
                    MachineLoadoutRegistry.Register(s, r.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                }
                rec.WorkPriorities ??= WorkPriorities.Default();
                foreach (WorkOrderKind kind in Enum.GetValues(typeof(WorkOrderKind)))
                {
                    rec.WorkPriorities.Set(kind, 0);
                }
                if (cargoScrap > 0)
                {
                    rec.Cargo = new[] { new CargoEntry { ResourceType = ItemCatalog.ScrapId, Amount = cargoScrap } };
                }
            }
            WorldSimulation.StepMany(2);
            return r.LogicId;
        }

        private static Vector2 Beside(CampaignState s, BuildingRecord st, int i) => DropPodService.GroundSpot(s, st.Position, i, 5f);

        /// <summary>离 <paramref name="from"/> [minD, maxD] 格、能降落的落点（先揭开那一片，再按着陆条件查；按种子地形找，B25）。</summary>
        private static Vector2? FindTarget(CampaignState s, Vector2 from, float minD, float maxD, int skip = 0)
        {
            int found = 0;
            for (float d = minD; d <= maxD; d += 15f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float ang = (a * 15f + d * 3f) * Mathf.Deg2Rad;
                    Vector2 p = from + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    p = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));
                    HomeGridService.RevealArea(s, p, 30f);
                    if (DropPodService.CheckLanding(s, p.x, p.y, out _) == null && found++ == skip)
                    {
                        return p;
                    }
                }
            }
            Fail($"测试准备：{from} 周围 {minD}～{maxD} 格找不到能降落的落点");
            return null;
        }

        private static DropStationRecord Rec(CampaignState s, BuildingRecord st) => st != null ? DropPodService.FindStation(s, st.BuildingId) : null;

        private static int CountPods(CampaignState s, BuildingRecord st, int state) => DropPodService.CountState(s, st.BuildingId, state);

        private static MachineRecord M(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord r) ? r : null;

        private static Vector2 PosOf(int id) => MachineRegistry.TryGetLivePosition(id, out Vector2 p) ? p : M(id)?.WorldPosition ?? Vector2.zero;

        private static bool OnPlanet(int id) => M(id) is MachineRecord m && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId;

        private static bool InKernel(int id) => Site != null && Site.TryGetMachineMarker(id, out _);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        /// <summary>逐步走到 <paramref name="tick"/>（含），确定性地停在事件步之后。</summary>
        private static void StepTo(long tick)
        {
            while (GameClock.Ticks < tick)
            {
                WorldSimulation.StepMany((int)Math.Min(int.MaxValue, Math.Max(1, tick - GameClock.Ticks)));
            }
        }

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static int Ground(CampaignState s, string item, Vector2 near, float r) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null && g.ResourceType == item && Vector2.Distance(g.Position, near) <= r).Sum(g => g.Amount);

        private static string Sig(CampaignState s, params int[] machines)
        {
            // 世界签名：已探明的防空（玩家情报，被拒时会记下）不算世界改动，另由 D4 断言。
            DropPodState d = DropPodService.StateOf(s);
            string[] known = d?.KnownAntiAir;
            if (d != null)
            {
                d.KnownAntiAir = Array.Empty<string>();
            }
            var sb = new StringBuilder(Json(d));
            if (d != null)
            {
                d.KnownAntiAir = known;
            }
            foreach (int id in machines)
            {
                MachineRecord m = M(id);
                Vector2 p = PosOf(id);
                sb.Append('|').Append(id).Append(':').Append(m?.RegionId).Append(':').Append(p.x.ToString("0.00", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("0.00", CultureInfo.InvariantCulture));
            }
            foreach (GroundItemRecord g in (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("drop:", StringComparison.Ordinal)))
            {
                sb.Append("|g:").Append(g.SalvageInstanceId).Append('=').Append(g.Amount);
            }
            return sb.Append("|pods=").Append(HomeInventory.Stock(s, Pod)).Append("|structural=").Append(HomeInventory.Stock(s, "structural")).Append("|t=").Append(GameClock.Ticks).ToString();
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(Station);
            GameConfig.fg.BuildingService svc = BuildingOps.Service(Station);
            bool building = g != null && g.FootprintW == 5 && g.FootprintH == 5 && g.Category == "logistics" && svc != null && HomeValleyLayout.PowerProfile.ContainsKey(Station)
                            && BuildCatalog.TryGet(Station, out BuildEntry e) && e.UnlockRule == "research:expedition.drop_station" && svc.CodexId == "codex.expedition.drop"
                            && BuildingOps.MaxTier(Station) == 2 && BuildingOps.TierRow(Station, 1)?.Value == 4f && BuildingOps.TierRow(Station, 2)?.Value == 8f;
            Expect(building, "A1 投送站：5×5（FG04 建筑全表“物流”）、用电、由研发“远征 · 投送站”解锁；T1 容纳 4 个舱、T2 8 个（FGR-EXP-013）");

            bool nodes = ResearchCatalog.TryGet("expedition.drop_station", out ResearchNodeDef n1) && n1.IsReady && n1.Unlocks.Contains("build:" + Station)
                         && ResearchCatalog.TryGet("expedition.drop_t2", out ResearchNodeDef n2) && n2.IsReady && n2.Unlocks.Contains("tier:drop_station.t2") && n2.Prereqs.Contains("expedition.drop_station")
                         && string.IsNullOrEmpty(n2.OpensIn) && ResearchCatalog.Problems.Count == 0;
            Expect(nodes, "A2 研发树（DEBT-FG5RND01-01 远征 · 投送站 T2 节点开放）：投送站节点解锁建造条目，T2 节点（前置投送站）解锁原地升 T2，不再写“后续版本开放”");

            bool item = ItemCatalog.TryGet(Pod, out ItemDef pod) && pod.Form == ItemForm.Solid && pod.BeltId > 0
                        && ItemCatalog.TryGetRecipe(Pod, out RecipeDef rec) && rec.Building == "component_workshop" && rec.Lines.Any(l => l.Role == RecipeRole.Out && l.Item.Id == Pod)
                        && ProducerCatalog.TryGet("component_workshop", out ProducerDef cw) && cw.Recipes.Any(r => r.Id == Pod);
            Expect(item, "A3 投送舱是组件工坊生产的物品（固体，上传送带；配方结构材 ×4 + 电子件 ×2，组件工坊可选，FGR-EXP-013）");

            bool tuning = Mathf.Approximately(DropPodService.RangeCells, 3000f) && Mathf.Approximately(DropPodService.SpeedCells, 60f) && Mathf.Approximately(DropPodService.LandingSeconds, 5f)
                          && Mathf.Approximately(DropPodService.FuelPer1000, 30f) && Mathf.Approximately(DropPodService.CooldownSeconds, 60f) && DropPodService.CargoUnits == 20
                          && DropPodService.FuelPerPodMl(1000f) == 60000 && Math.Abs(DropPodService.LegSeconds(600f) - 15.0) < 1e-6;
            Expect(tuning, $"A4 FG08 第 12 节初值入表（fg.TbHomeTuning drop.*）：投送距离 {DropPodService.RangeCells}、速度 {DropPodService.SpeedCells} 格/秒 + 着陆 {DropPodService.LandingSeconds} 秒、每舱每 1000 格 {DropPodService.FuelPer1000} 升（往返 1000 格 = 60 升）、冷却 {DropPodService.CooldownSeconds} 秒、每舱 {DropPodService.CargoUnits} 个货物");

            string root = FgProductionSelfCheck.LocateRepo();
            string[] files =
            {
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/DropPodService.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/DropPodService.Missions.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/DropPodService.Step.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/DropPodService.View.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/DropStationPanelUIToolkit.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/StrategicMapUIToolkit.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/WorldMapShared.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/ProductionPanelUIToolkit.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/ExpeditionMarchService.cs",
            };
            string src = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(root, f))));
            var keys = System.Text.RegularExpressions.Regex.Matches(src, "\"((?:drop\\.|bs\\.reason\\.drop|env\\.silent\\.reject\\.drop|ui\\.map\\.pick\\.|ui\\.map\\.antiair|exp\\.cue\\.returned_by_pod)[a-z_\\.0-9]*)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)
                .Where(k => !k.EndsWith(".", StringComparison.Ordinal) && !GridContent.TryGetTuning(k, out _) && k != "drop.unknown" && k != "drop.no_pods" && k != "drop.no_fuel" && k != "drop.flying" && k != "drop.idle")
                .Distinct().ToList();
            foreach (string note in new[] { "diverted", "antiair", "enemy_outpost", "nothing_here" })
            {
                keys.Add("drop.note." + note);
            }
            var missing = keys.Where(k => string.IsNullOrEmpty(GameText.Get(k, GameLanguage.ZhCn)) || GameText.Get(k, GameLanguage.ZhCn).Contains("⟦")
                                          || string.IsNullOrEmpty(GameText.Get(k, GameLanguage.En)) || GameText.Get(k, GameLanguage.En).Contains("⟦")).ToList();
            Expect(keys.Count >= 80 && missing.Count == 0, $"A5 文本键（B16）：源码引用的 {keys.Count} 个投送键中英都有" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing.Take(10))));

            GameConfig.fg.NotifyType landed = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("drop_landed");
            GameConfig.fg.NotifyType alert = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("drop_alert");
            GameConfig.fg.CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.expedition.drop");
            bool hooks = new[] { GuidanceHooks.DropStationFirstBuilt, GuidanceHooks.DropPanelFirstOpen, GuidanceHooks.DropFirstLaunch, GuidanceHooks.DropFirstLanded,
                GuidanceHooks.DropFirstRecall, GuidanceHooks.DropFirstAntiAirRejected }.All(GuidanceHooks.Known.Contains);
            Expect(landed != null && landed.Tier == "info" && landed.AwaySection == "event" && alert != null && alert.Tier == "warning" && alert.AwaySection == "event"
                   && cx != null && cx.Hooks.Contains("drop.first_station_built") && hooks,
                "A6 通知类型 drop_landed（信息）/ drop_alert（警告：改降 / 中止），都进离家报告事件段（B08 / B19）；图鉴“投送站与投送舱”由首次建成 / 打开面板 / 防空被拒解锁（FG08 第 7 节“图鉴有投送条目”）；六个引导钩子在册（FG13“第一次解锁投送”，内容在 FG15-UX-04）");
        }

        // ── B 研发门控 / 装填 / 自动补满 / T2 / 拆除 ──────────────────────────────

        private static void CheckGatesAndLoading()
        {
            CampaignState s = NewWorld(7101, out _, research: false);
            Func<bool> before = ResearchGate.TreeAvailableOverrideForTests;
            try
            {
                ResearchGate.TreeAvailableOverrideForTests = () => true;
                GridCell? c = FgProductionSelfCheck.FindFree(s, Station, 10f, 34f);
                GridPlacementResult locked = c.HasValue ? HomeGridService.ValidatePlacement(s, Station, c.Value, 0) : null;
                bool gate = locked != null && !locked.Ok && locked.Has(GridBlockReason.Locked) && locked.Describe().Contains("投送站");
                ResearchService.CompleteForTests(s, "expedition.drop_station");
                bool unlocked = c.HasValue && HomeGridService.ValidatePlacement(s, Station, c.Value, 0).Ok;
                Expect(gate && unlocked, $"B1 研发门控：没研究“远征 · 投送站”时放不下投送站（“{locked?.Describe()}”），研究后可以放");
            }
            finally
            {
                ResearchGate.TreeAvailableOverrideForTests = before;
            }

            BuildingRecord st = PlaceStation(s, CoreCenter(s), 10f, 34f, 0, 0f);
            if (st == null)
            {
                return;
            }
            DropStationRecord r = Rec(s, st);
            ItemDef podItem = ItemCatalog.Find(Pod);
            HomeInventory.Add(s, podItem, 6, clampToSpace: false);
            Seconds(1.2f);
            bool manualOnly = DropPodService.PodCount(s, st.BuildingId) == 0 && HomeInventory.Stock(s, Pod) == 6; // 自动补满关着：不动仓库
            int loaded = DropPodService.LoadPods(s, r, st, int.MaxValue);
            bool capT1 = loaded == 4 && DropPodService.PodCount(s, st.BuildingId) == 4 && HomeInventory.Stock(s, Pod) == 2 && CountPods(s, st, DropPodService.StateDocked) == 4;
            st.Tier = 2;
            r.AutoFill = true;
            Seconds(1.2f);
            bool autoT2 = DropPodService.PodCount(s, st.BuildingId) == 6 && HomeInventory.Stock(s, Pod) == 0 && DropPodService.Capacity(st) == 8;
            string status = BuildingStatusService.Evaluate(s, st).Reason;
            Expect(manualOnly && capT1 && autoT2 && status.Contains("容纳 8"),
                $"B2 装填：自动补满关着时仓库里的舱不动（{manualOnly}）；“装填”装满 T1 的 4 个、剩 2 个留在仓库（{capT1}）；升 T2 后打开自动补满，仓库里的 2 个自动装进来（容纳 8）（{autoT2}）；状态行“{status.Replace("\n", " / ")}”");

            // 拆除：站里的舱退回仓库（放不下的落在站的原地，物品不丢）。
            Vector2 at = st.Position;
            Unregister(s, st);
            Seconds(1.2f);
            int stock = HomeInventory.Stock(s, Pod), ground = Ground(s, Pod, at, 4f);
            bool returned = DropPodService.FindStation(s, st.BuildingId) == null && DropPodService.PodCount(s, st.BuildingId) == 0 && stock + ground == 6;
            Expect(returned, $"B3 拆掉投送站：站里的 6 个投送舱当作物品退回（仓库 {stock} 个、仓库放不下落在原地 {ground} 个），记录拿掉");
        }

        // ── C 投送 ─────────────────────────────────────────────────────────────────

        private static void CheckDelivery()
        {
            CampaignState s = NewWorld(7102, out BuildingRecord st, fuelLiters: 1000f);
            if (st == null)
            {
                return;
            }
            int a = SpawnMachine(s, Beside(s, st, 0), cargoScrap: 3);
            Vector2? t = FindTarget(s, st.Position, 280f, 420f);
            if (a == 0 || t == null)
            {
                return;
            }
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 30, clampToSpace: false);
            int structural0 = HomeInventory.Stock(s, "structural");
            DropStationRecord r = Rec(s, st);
            long fuel0 = DropPodService.FuelMl(r);
            DropPreview p = DropPodService.Preview(s, st.BuildingId, t.Value, new[] { a }, "structural", 25);
            float dist = Vector2.Distance(st.Position, t.Value);
            long perPod = (long)Math.Ceiling(2.0 * dist / 1000.0 * 30.0 * 1000.0);
            bool preview = p.Ok && p.PodsNeeded == 3 && p.FuelMl == perPod * 3 && Math.Abs(p.LegSeconds - (dist / 60.0 + 5.0)) < 1e-4 && Mathf.Abs(p.Distance - dist) < 0.01f
                           && DropPodService.PreviewText(p).Contains("升") && DropPodService.PreviewText(p).Contains("含着陆 5 秒");
            Expect(preview, $"C1 预览（FGU-46“选择投送目标时显示飞行时间和燃油”）：{dist:0} 格 → 单程 {p.LegSeconds:0.0} 秒（距离 ÷ 60 + 5 秒着陆）、1 台机器 + 25 个结构材 = 3 舱（每舱 20 个货物）、燃油 {DropPodService.Liters(p.FuelMl)} 升（每舱往返 {DropPodService.Liters(perPod)} 升）：“{DropPodService.PreviewText(p).Replace("\n", " / ")}”");

            long t0 = GameClock.Ticks;
            bool launched = DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "structural", 25, out _);
            WorldSimulation.StepMany(2);
            DropPodRecord[] flying = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateOutbound).ToArray();
            long arrive = t0 + GameClock.TicksFor(DropPodService.LegSeconds(dist));
            bool boarded = launched && flying.Length == 3 && flying.All(x => x.ArriveTick == arrive) && M(a).RegionId == DropPodService.InPodRegionId && !InKernel(a)
                           && DropPodService.FuelMl(r) == fuel0 - perPod * 3 && HomeInventory.Stock(s, "structural") == structural0 - 25 && CountPods(s, st, DropPodService.StateDocked) == 1
                           && flying.Count(x => x.MachineId == a) == 1 && flying.Where(x => x.MachineId == 0).Select(x => x.CargoAmount).OrderBy(x => x).SequenceEqual(new[] { 5, 20 });
            UplinkFailure uf = SignalUplinkService.Validate(s, a, out string uplinkText);
            Expect(boarded && uf == UplinkFailure.InDelivery && !string.IsNullOrEmpty(uplinkText),
                $"C2 发射：机器离开地面进舱（家园内核里没有它的单位）、货物出库 25 个、燃油一次扣清往返（{DropPodService.Liters(fuel0)} → {DropPodService.Liters(DropPodService.FuelMl(r))} 升）、3 舱按同一到达步起飞，站里剩 1 舱；" +
                       $"舱里的机器拒绝接入、写明“{uplinkText}”（DEBT-FG1SIG03-02）");

            StepTo(arrive - 1);
            bool notYet = M(a).RegionId == DropPodService.InPodRegionId;
            StepTo(arrive + 2);
            WorldSimulation.StepMany(2);
            bool landed = OnPlanet(a) && InKernel(a) && Vector2.Distance(PosOf(a), t.Value) <= DropPodService.FlatRadius + 2f && Ground(s, "structural", t.Value, 6f) == 25
                          && DropPodService.Pods(s).Count(x => x.State == DropPodService.StateReturning) == 3 && NotifyCount("drop_landed") >= 1;
            Expect(notYet && landed, $"C3 着陆在到达步（{arrive - t0} 步 = {(arrive - t0) / (double)GameClock.StepHz:0.0} 游戏秒）：机器在落点下舱、在家园内核里建出单位（离落点 {Vector2.Distance(PosOf(a), t.Value):0.0} 格），25 个结构材落在落点地上；3 舱起飞返程；着陆通知“{LastNotify("drop_landed")}”");

            DropPodRecord any = DropPodService.Pods(s).First(x => x.State == DropPodService.StateReturning);
            long back = any.ArriveTick;
            StepTo(back + 1);
            bool cooling = CountPods(s, st, DropPodService.StateCooling) == 3 && DropPodService.Pods(s).All(x => x.MachineId == 0 && x.CargoAmount == 0);
            DropPreview busy = DropPodService.Preview(s, st.BuildingId, t.Value, Array.Empty<int>(), "structural", 5);
            long cool = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateCooling).Min(x => x.CooldownUntilTick);
            StepTo(cool + 1);
            bool docked = CountPods(s, st, DropPodService.StateDocked) == 4 && cool - back == GameClock.TicksFor(60.0);
            Expect(cooling && docked && busy.Ok,
                $"C4 回到站里冷却 60 秒（{(cool - back) / (double)GameClock.StepHz:0} 秒）后待命（{docked}）；冷却期间还能用剩下那一个舱发 1 舱（{busy.Ok}）");
        }

        // ── D 负向矩阵 ─────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            CampaignState s = NewWorld(7103, out BuildingRecord st, pods: 2, fuelLiters: 10f);
            if (st == null)
            {
                return;
            }
            int a = SpawnMachine(s, Beside(s, st, 0));
            Vector2? near = FindTarget(s, st.Position, 260f, 400f);
            if (a == 0 || near == null)
            {
                return;
            }
            DropStationRecord r = Rec(s, st);
            var rows = new List<(string Name, string Want, DropPreview P, bool Changed)>();
            int denied0 = DropPodService.LastRejectCount;

            void Try(string name, string want, Vector2 target, int[] machines, string item = "", int amount = 0)
            {
                string b = Sig(s, a);
                DropPodService.TryLaunch(s, st.BuildingId, target, machines, item, amount, out DropPreview p);
                rows.Add((name, want, p, Sig(s, a) != b));
            }

            Try("没装东西", "no-payload", near.Value, Array.Empty<int>());
            Try("燃油不足", "fuel", near.Value, new[] { a });
            r.HeldFuelMl = 2_000_000;
            // 迷雾：投送距离内一处还没探索的地方（按种子世界找，不写死坐标）。
            Vector2? fog = null;
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int k = 0; k < 72 && fog == null; k++)
            {
                float ang = k * 5f * Mathf.Deg2Rad;
                Vector2 p = st.Position + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 2200f;
                if (!map.IsExploredNoLoad(new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y))))
                {
                    fog = p;
                }
            }
            if (fog.HasValue)
            {
                Try("落点未探索", "unexplored", fog.Value, new[] { a });
            }
            Vector2 far = st.Position + (near.Value - st.Position).normalized * 3300f;
            HomeGridService.RevealArea(s, far, 20f);
            Try("超出投送距离", "out-of-range", far, new[] { a });
            // 不是平地：落点附近的一格水面 / 悬崖（种子地形里找；没有就把落点旁一格改成水面）。
            Vector2? rough = FindRough(s, st.Position);
            if (rough.HasValue)
            {
                Try("不是平地", "not-flat", rough.Value, new[] { a });
            }
            // 落点被建筑占着：家园里一座建筑的中心。
            BuildingRecord gen = s.BuildingRecords.First(x => x.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
            Try("建筑占着", "building", gen.Position, new[] { a });
            // 防空覆盖：在落点旁登记一座铸造据点（它的对空高炮覆盖落点）。
            OutpostRecord aa = WorldOutpostSystem.SpawnOutpost(s, "foundry", new GridCell(Mathf.RoundToInt(near.Value.x + 40f), Mathf.RoundToInt(near.Value.y)), false);
            DropPreview aaPrev = DropPodService.Preview(s, st.BuildingId, near.Value, new[] { a }, "", 0);
            Try("防空覆盖内", "antiair", near.Value, new[] { a });
            WorldOutpostSystem.MarkDestroyed(s, aa.OutpostId);
            // 敌方据点：静默据点（没有防空）就在落点旁。
            OutpostRecord silent = WorldOutpostSystem.SpawnOutpost(s, "silent", new GridCell(Mathf.RoundToInt(near.Value.x + 6f), Mathf.RoundToInt(near.Value.y)), false);
            Try("据点占着", "enemy-outpost", near.Value, new[] { a });
            WorldOutpostSystem.MarkDestroyed(s, silent.OutpostId);
            bool aaAfterDestroyed = DropPodService.CheckLanding(s, near.Value.x, near.Value.y, out _) == null;
            // 危害区中心（领地模板里的危害区；只查着陆条件，不管投送距离）。
            string hazard = HazardCenterCheck(s);
            // 站旁没有的机器 / 仓库不够的货物。
            int farMachine = SpawnMachine(s, st.Position + new Vector2(40f, 0f));
            Try("机器不在站旁", "machine-not-ready", near.Value, new[] { farMachine });
            Try("货物不够", "cargo-short", near.Value, Array.Empty<int>(), "structural", 999);
            // 舱：不够 / 全部在冷却。
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 60, clampToSpace: false);
            Try("舱不够（3 舱）", "not-enough-pods", near.Value, new[] { a }, "structural", 41);
            bool ok2 = DropPodService.TryLaunch(s, st.BuildingId, near.Value, new[] { a }, "structural", 20, out _);
            Seconds(0.5f);
            StepUntil(() => CountPods(s, st, DropPodService.StateCooling) == 2, 120);
            Try("全部在冷却", "all-cooling", near.Value, Array.Empty<int>(), "structural", 10);
            StepUntil(() => CountPods(s, st, DropPodService.StateDocked) == 2, 90);
            // 投送站被摧毁 / 缺电 / 禁用。
            st.PowerState = BuildingPowerState.Unpowered;
            Try("投送站缺电", "station-offline", near.Value, Array.Empty<int>(), "structural", 5);
            HomeValleyPowerGrid.Recompute(s);
            st.ConstructionState = BuildingConstructionState.Disabled;
            Try("投送站禁用", "station-offline", near.Value, Array.Empty<int>(), "structural", 5);
            st.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            // 没有舱。
            BuildingRecord empty = PlaceStation(s, CoreCenter(s), 12f, 40f, 0, 500f);
            if (empty != null)
            {
                string b = Sig(s, a);
                DropPodService.TryLaunch(s, empty.BuildingId, near.Value, Array.Empty<int>(), "structural", 5, out DropPreview np);
                rows.Add(("站里没有舱", "no-pods", np, Sig(s, a) != b));
            }

            var bad = rows.Where(x => x.P.Ok || x.P.Code != x.Want || x.Changed || string.IsNullOrEmpty(x.P.Reason)).Select(x => $"{x.Name}：{x.P.Code}（要 {x.Want}）{(x.Changed ? "改了世界" : string.Empty)}").ToList();
            Expect(bad.Count == 0 && rows.Count >= 14 && DropPodService.LastRejectCount - denied0 == rows.Count && ok2,
                $"D1 负向矩阵（{rows.Count} 种，FGT-EXP-003 着陆条件 / 防空拒绝 / 燃油 / 冷却 / 载量）：每种都被拒、写明原因、拒绝音（{DropPodService.LastRejectCount - denied0} 次）、不改世界（舱、燃油、机器、仓库逐字段不变；防空被拒只记下探明的防空情报，见 D4）" +
                (bad.Count == 0 ? "：" + string.Join("；", rows.Select(x => x.Name + "“" + x.P.Reason + "”").Take(6)) : "；不对：" + string.Join("；", bad)));
            Expect(aaPrev.AntiAir && DropPodService.PreviewText(aaPrev).Contains("红色区域") && aaAfterDestroyed,
                $"D2 防空：落点在铸造据点的防空覆盖里被拒，预览写明是哪个据点并提示“红色区域是敌方防空覆盖”；据点被摧毁后同一落点能降落（{aaAfterDestroyed}）");
            Line($"  · D3 危害区中心：本种子 {hazard}（“no-zone” = 这个种子没有领地模板危害区；M2 在种子集上断言 hazard-center）");
        }

        /// <summary>D4 防空口径（审查 P1）：落点已探索、防空据点还在迷雾里——拒绝与红色区域同一口径、原因不泄露迷雾里的据点名；瞄准 / 被拒后探明并画出红区，存档带着。</summary>
        private static void CheckAntiAirFog()
        {
            CampaignState s = NewWorld(7120, out BuildingRecord st);
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 300f, 460f) : null;
            if (a == 0 || t == null)
            {
                return;
            }
            // 铸造据点放在落点外 60～66 格（防空半径 70 盖住落点），据点格不在已揭开的 30 格里。
            OutpostRecord aa = null;
            for (float d = 60f; d <= 66f && aa == null; d += 2f)
            {
                for (int k = 0; k < 8 && aa == null; k++)
                {
                    float ang = k * 45f * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(t.Value.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(t.Value.y + Mathf.Sin(ang) * d));
                    if (!HomeGridService.MapFor(s).IsExploredNoLoad(c))
                    {
                        aa = WorldOutpostSystem.SpawnOutpost(s, "foundry", c, false);
                    }
                }
            }
            if (aa == null)
            {
                Fail("测试准备：落点外 60～66 格找不到还在迷雾里的格子放防空据点");
                return;
            }
            var circles = new List<(Vector2 Center, float Radius, string OutpostId)>();
            DropPodService.AntiAirCircles(s, t.Value.x, t.Value.y, 200f, circles);
            bool hiddenBefore = !DropPodService.IsOutpostSeen(s, aa) && circles.All(c => c.OutpostId != aa.OutpostId);
            DropPreview p = DropPodService.Preview(s, st.BuildingId, t.Value, new[] { a }, "", 0);
            string realName = TerritoryControlService.OutpostName(aa);
            bool sameRule = !p.Ok && p.Code == "antiair" && p.AntiAir && p.AntiAirOutpostId == aa.OutpostId;
            bool noLeak = p.Reason == GameText.Format("drop.reason.antiair", GameText.Get("drop.outpost.unknown")) && p.Reason.Contains("未探明的敌方据点");
            bool launched = DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out _);
            DropPodService.AntiAirCircles(s, t.Value.x, t.Value.y, 200f, circles);
            bool revealed = !launched && circles.Any(c => c.OutpostId == aa.OutpostId) && DropPodService.IsAntiAirKnown(s, aa) && OnPlanet(a);
            DropPodState copy = JsonUtility.FromJson<DropPodState>(JsonUtility.ToJson(DropPodService.StateOf(s)));
            bool saved = copy.KnownAntiAir != null && copy.KnownAntiAir.Contains(aa.OutpostId);
            HomeGridService.RevealArea(s, new Vector2(aa.CellX, aa.CellY), 6f); // 据点格探索之后，原因写出据点名
            bool named = DropPodService.Preview(s, st.BuildingId, t.Value, new[] { a }, "", 0).Reason == GameText.Format("drop.reason.antiair", realName);
            noLeak &= named;
            Expect(hiddenBefore && sameRule && noLeak && revealed && saved,
                $"D4 防空口径（FGR-EXP-014 / B06）：落点已探索、防空据点在迷雾里时同样按防空拒绝（{sameRule}），原因不写迷雾里的据点名（“{p.Reason}”）（{noLeak}）；" +
                $"瞄准 / 发射被拒即探明，战略地图画出它的红色区域（拒绝前不画 {hiddenBefore}，之后画 {revealed}），探明记录随存档（{saved}）");
        }

        private static Vector2? FindRough(CampaignState s, Vector2 from)
        {
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            for (float d = 30f; d <= 600f; d += 6f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    Vector2 p = from + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    var c = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    if (!map.IsExploredNoLoad(c))
                    {
                        continue;
                    }
                    byte t = map.GetTerrain(c);
                    if (t == ctx.Source.Params.CodeCliff || t == ctx.Source.Params.CodeWater)
                    {
                        return new Vector2(c.X, c.Y);
                    }
                }
            }
            // 种子地形里已探索范围没有水面 / 悬崖：把一处可降落的落点旁一格改成水面（玩家修改过的地形同样算）。
            Vector2? ok = FindTarget(s, from, 300f, 500f, 3);
            if (ok == null)
            {
                return null;
            }
            var w = new GridCell(Mathf.RoundToInt(ok.Value.x) + 1, Mathf.RoundToInt(ok.Value.y));
            map.SetTerrain(w, ctx.Source.Params.CodeWater);
            return ok;
        }

        private static string HazardCenterCheck(CampaignState s)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return "no-plan";
            }
            foreach (PlannedHazardZone z in plan.HazardZones)
            {
                var c = new Vector2(z.CenterX, z.CenterY);
                HomeGridService.RevealArea(s, c, z.Radius + 4);
                for (int k = 0; k < 12; k++)
                {
                    Vector2 p = c + new Vector2(Mathf.Cos(k * 0.5f), Mathf.Sin(k * 0.5f)) * (z.Radius * 0.2f);
                    string code = DropPodService.CheckLanding(s, Mathf.Round(p.x), Mathf.Round(p.y), out _);
                    if (code == "hazard-center")
                    {
                        return code;
                    }
                }
            }
            return "no-zone";
        }

        // ── E 飞行途中落点被占领 ───────────────────────────────────────────────────

        private static void CheckOccupiedInFlight()
        {
            CampaignState s = NewWorld(7104, out BuildingRecord st);
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 400f, 560f) : null;
            if (a == 0 || t == null)
            {
                return;
            }
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 20, clampToSpace: false);
            DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "structural", 20, out _);
            int stock0 = HomeInventory.Stock(s, "structural");
            Seconds(3f);
            int alerts = NotifyCount("drop_alert");
            int aborted0 = DropPodService.StateOf(s).Aborted;
            // 飞行途中铸造在落点旁建起据点（防空覆盖落点）。
            WorldOutpostSystem.SpawnOutpost(s, "foundry", new GridCell(Mathf.RoundToInt(t.Value.x), Mathf.RoundToInt(t.Value.y + 30f)), false);
            long arrive = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateOutbound).Max(x => x.ArriveTick);
            StepTo(arrive + 2);
            DropPodRecord[] back = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateReturning).ToArray();
            bool turned = back.Length == 2 && back.All(x => x.Note == "antiair") && back.Any(x => x.MachineId == a) && back.Any(x => x.CargoAmount == 20)
                          && M(a).RegionId == DropPodService.InPodRegionId && Ground(s, "structural", t.Value, 10f) == 0 && NotifyCount("drop_alert") == alerts + 1
                          && DropPodService.StateOf(s).Aborted == aborted0 + 2 && LastNotify("drop_alert").Contains("防空");
            StepTo(back.Max(x => x.ArriveTick) + 2);
            WorldSimulation.StepMany(2);
            int back20 = HomeInventory.Stock(s, "structural") - stock0 + Ground(s, "structural", st.Position, 16f);
            bool home = OnPlanet(a) && Vector2.Distance(PosOf(a), st.Position) < 14f && back20 == 20;
            Expect(turned && home,
                $"E1 卡片负向“落点在飞行途中被敌人占领”：飞行中落点被铸造据点的防空覆盖，到达时不降落（机器还在舱里、地上没有货物），2 舱带着载荷返回并发一条警告（“{LastNotify("drop_alert")}”）；回到站里机器在站旁下舱、货物回仓库（{home}）");
        }

        // ── F 投送站被摧毁 ─────────────────────────────────────────────────────────

        private static void CheckStationDestroyed()
        {
            CampaignState s = NewWorld(7105, out BuildingRecord st);
            if (st == null)
            {
                return;
            }
            BuildingRecord st2 = PlaceStation(s, CoreCenter(s), 14f, 44f, 1, 300f);
            int a = SpawnMachine(s, Beside(s, st, 0));
            Vector2? t = FindTarget(s, st.Position, 300f, 460f);
            if (st2 == null || a == 0 || t == null)
            {
                return;
            }
            DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out _);
            int serial = DropPodService.Pods(s).First(x => x.State == DropPodService.StateOutbound).Serial;
            Seconds(2f);
            BuildingOps.ApplyDamage(s, st.BuildingId, 100000f); // 突袭把投送站打掉（只剩虚影）
            bool destroyed = st.ConstructionState == BuildingConstructionState.Damaged || st.ConstructionState == BuildingConstructionState.Destroyed;
            DropPodRecord pod = DropPodService.FindPod(s, serial);
            StepTo(pod.ArriveTick + 2);
            bool landed = OnPlanet(a) && pod.State == DropPodService.StateReturning;
            bool diverted = pod.StationId == st2.BuildingId && Mathf.Abs(pod.ToX - st2.Position.x) < 0.01f && pod.Note == "diverted";
            StepTo(pod.ArriveTick + 2);
            bool docked2 = pod.State == DropPodService.StateCooling && pod.StationId == st2.BuildingId && DropPodService.PodCount(s, st2.BuildingId) == 2;
            Expect(destroyed && landed && diverted && docked2 && LastNotify("drop_alert").Contains("改降"),
                $"F1 FG08 负向“投送站在远征期间被突袭摧毁”：舱照常在落点放下机器；返程时原投送站已被摧毁，改降到最近的另一座投送站（{diverted}），在那里冷却、归它所有（{docked2}）；警告“{LastNotify("drop_alert")}”");

            // 返程途中才被摧毁：对账时改降（从当前位置飞向另一座）。
            CampaignState s2 = NewWorld(7106, out BuildingRecord sa);
            BuildingRecord sb = sa != null ? PlaceStation(s2, CoreCenter(s2), 14f, 44f, 1, 300f) : null;
            int b = sa != null ? SpawnMachine(s2, Beside(s2, sa, 0)) : 0;
            Vector2? t2 = sa != null ? FindTarget(s2, sa.Position, 360f, 520f) : null;
            if (sb == null || b == 0 || t2 == null)
            {
                return;
            }
            DropPodService.TryLaunch(s2, sa.BuildingId, t2.Value, new[] { b }, "", 0, out _);
            DropPodRecord p2 = DropPodService.Pods(s2).First(x => x.State == DropPodService.StateOutbound);
            StepTo(p2.ArriveTick + 2);
            Seconds(2f);
            var oldLeg = new DropPodRecord { State = p2.State, FromX = p2.FromX, FromY = p2.FromY, ToX = p2.ToX, ToY = p2.ToY, StartTick = p2.StartTick, ArriveTick = p2.ArriveTick, StationId = p2.StationId };
            long destroyTick = GameClock.Ticks;
            BuildingOps.ApplyDamage(s2, sa.BuildingId, 100000f);
            Seconds(1.2f);
            Vector2 expected = DropPodService.PositionOf(s2, oldLeg, p2.StartTick);
            bool midDivert = p2.StationId == sb.BuildingId && p2.StartTick >= destroyTick && p2.StartTick <= destroyTick + GameClock.StepHz * 2
                             && Vector2.Distance(new Vector2(p2.FromX, p2.FromY), expected) < 0.05f && Mathf.Abs(p2.ToX - sb.Position.x) < 0.01f;
            Expect(midDivert, $"F2 返程途中投送站被摧毁：下一次对账（{p2.StartTick - destroyTick} 步后）改降，从舱当时在航线上的位置（{expected}）飞向另一座投送站");

            // 没有别的投送站：降落在原定地点（被毁的站旁），舱等站重建。
            CampaignState s3 = NewWorld(7107, out BuildingRecord sc);
            int c = sc != null ? SpawnMachine(s3, Beside(s3, sc, 0)) : 0;
            Vector2? t3 = sc != null ? FindTarget(s3, sc.Position, 300f, 460f) : null;
            if (c == 0 || t3 == null)
            {
                return;
            }
            DropPodService.TryLaunch(s3, sc.BuildingId, t3.Value, new[] { c }, "", 0, out _);
            DropPodRecord p3 = DropPodService.Pods(s3).First(x => x.State == DropPodService.StateOutbound);
            BuildingOps.ApplyDamage(s3, sc.BuildingId, 100000f);
            StepTo(p3.ArriveTick + 2);
            StepTo(p3.ArriveTick + 2);
            DropPreview offline = DropPodService.Preview(s3, sc.BuildingId, t3.Value, Array.Empty<int>(), "", 0);
            bool waits = p3.State == DropPodService.StateCooling && p3.StationId == sc.BuildingId && Mathf.Abs(p3.ToX - sc.Position.x) < 0.01f && offline.Code == "station-offline"
                         && offline.Reason.Contains("重建") && DropPodService.PodCount(s3, sc.BuildingId) == 4;
            Expect(waits, $"F3 没有别的投送站：舱降落在原定地点（被毁的站旁），留在站的记录里（4 个舱都在）；站重建前发射被拒（“{offline.Reason}”）");
        }

        /// <summary>F4 / F5 改降规则（审查 P2）：另一座站满编时不改降过去（容量 T1 4）；归属站只缺电 / 被禁用时不改降、照常飞回原站。</summary>
        private static void CheckDivertRules()
        {
            CampaignState s = NewWorld(7123, out BuildingRecord st);
            BuildingRecord full = st != null ? PlaceStation(s, CoreCenter(s), 14f, 44f, 4, 300f) : null;
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 300f, 460f) : null;
            if (full == null || a == 0 || t == null)
            {
                return;
            }
            DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out _);
            DropPodRecord pod = DropPodService.Pods(s).First(x => x.State == DropPodService.StateOutbound);
            BuildingOps.ApplyDamage(s, st.BuildingId, 100000f);
            StepTo(pod.ArriveTick + 2);
            Seconds(1.5f);
            int fullCount = DropPodService.PodCount(s, full.BuildingId);
            bool noOverflow = pod.StationId == st.BuildingId && pod.Note != "diverted" && fullCount == DropPodService.Capacity(full) && Mathf.Abs(pod.ToX - st.Position.x) < 0.01f;
            Expect(noOverflow, $"F4 另一座投送站已满编（{fullCount} / {DropPodService.Capacity(full)}）：返程的舱不改降过去（不超过 FGR-EXP-013 容量），按原定飞回被毁的站旁（{noOverflow}）");

            CampaignState s2 = NewWorld(7124, out BuildingRecord sa);
            BuildingRecord sb = sa != null ? PlaceStation(s2, CoreCenter(s2), 14f, 44f, 1, 300f) : null;
            int b = sa != null ? SpawnMachine(s2, Beside(s2, sa, 0)) : 0;
            Vector2? t2 = sa != null ? FindTarget(s2, sa.Position, 300f, 460f) : null;
            if (sb == null || b == 0 || t2 == null)
            {
                return;
            }
            DropPodService.TryLaunch(s2, sa.BuildingId, t2.Value, new[] { b }, "", 0, out _);
            DropPodRecord p2 = DropPodService.Pods(s2).First(x => x.State == DropPodService.StateOutbound);
            int diverted0 = DropPodService.StateOf(s2).Diverted;
            sa.ConstructionState = BuildingConstructionState.Disabled; // 玩家为了省电把站禁用
            StepTo(p2.ArriveTick + 2);
            Seconds(1.5f);
            bool flyHome = p2.State == DropPodService.StateReturning && p2.StationId == sa.BuildingId && Mathf.Abs(p2.ToX - sa.Position.x) < 0.01f;
            StepTo(p2.ArriveTick + 2);
            bool docked = p2.State == DropPodService.StateCooling && p2.StationId == sa.BuildingId && DropPodService.StateOf(s2).Diverted == diverted0;
            sa.ConstructionState = BuildingConstructionState.Operational;
            Expect(flyHome && docked, $"F5 归属站只是被禁用 / 缺电（没被摧毁）：返程的舱不改降，照常飞回原站并在那里冷却（{flyHome} / {docked}）");
        }

        // ── G 静默夜 ───────────────────────────────────────────────────────────────

        private static void CheckSilentNight()
        {
            CampaignState s = NewWorld(7108, out BuildingRecord st);
            BuildingRecord st2 = st != null ? PlaceStation(s, CoreCenter(s), 14f, 44f, 1, 300f) : null;
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 1500f, 1800f) : null;
            if (st2 == null || a == 0 || t == null)
            {
                return;
            }
            SilentNightService.Install();
            try
            {
                DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out DropPreview p);
                DropPodRecord pod = DropPodService.Pods(s).First(x => x.State == DropPodService.StateOutbound);
                SilentNightService.ScheduleForTests(s, 1.0, 600.0, raid: false);
                bool started = StepUntil(() => SilentNightService.IsActiveNow(), 120) && GameClock.Ticks < pod.ArriveTick;
                DropPreview night = DropPodService.Preview(s, st2.BuildingId, t.Value, Array.Empty<int>(), "", 0);
                bool rejected = !DropPodService.TryLaunch(s, st2.BuildingId, t.Value, Array.Empty<int>(), "", 0, out _) && night.Code == "silent-night" && night.Reason.Contains("静默夜")
                                && !DropPodService.TryRecall(s, st2.BuildingId, t.Value, out DropPreview nr) && nr.Code == "silent-night";
                BuildingOps.ApplyDamage(s, st.BuildingId, 100000f); // 静默夜里投送站被毁：自动驾驶不改降
                StepTo(pod.ArriveTick + 2);
                bool autopilot = SilentNightService.IsActiveNow() && OnPlanet(a) && Vector2.Distance(PosOf(a), t.Value) <= DropPodService.FlatRadius + 2f
                                 && pod.State == DropPodService.StateReturning && pod.StationId == st.BuildingId && Mathf.Abs(pod.ToX - st.Position.x) < 0.01f;
                Expect(started && rejected && autopilot,
                    $"G1 FGR-EXP-016 / FGT-EXP-003：飞行途中静默夜开始（{started}）；静默夜里不能发起投送或回收（“{night.Reason}”）（{rejected}）；在飞的舱按自动驾驶飞完、降落在原定落点放下机器，" +
                    $"投送站被毁也不改降、按原定飞回原来的站（{autopilot}）");
            }
            finally
            {
                SilentNightService.ResetForTests();
            }
        }

        // ── H 回收 ─────────────────────────────────────────────────────────────────

        private static void CheckRecall()
        {
            CampaignState s = NewWorld(7109, out BuildingRecord st);
            Vector2? t = st != null ? FindTarget(s, st.Position, 300f, 460f) : null;
            if (t == null)
            {
                return;
            }
            // 三台机器在远处（其中一台满载货舱）、两件地上货物；站里 4 舱 → 3 舱接机器 + 1 舱装货物。
            int a = SpawnMachine(s, t.Value + new Vector2(2f, 0f), cargoScrap: 7);
            int b = SpawnMachine(s, t.Value + new Vector2(-2f, 1f));
            int c = SpawnMachine(s, t.Value + new Vector2(0f, -3f));
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, t.Value + new Vector2(3f, 3f), "structural", 12, "test:loose1");
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, t.Value + new Vector2(-3f, 3f), "structural", 5, "test:loose2");
            DropPreview p = DropPodService.PreviewRecall(s, st.BuildingId, t.Value);
            bool preview = p.Ok && p.Machines.Count == 3 && p.GroundCargo == 17 && p.PodsNeeded == 4 && p.Leftover == 0 && DropPodService.PreviewText(p).Contains("接回机器 3 台");
            int scrap0 = HomeInventory.Stock(s, ItemCatalog.ScrapId);
            int structural0 = HomeInventory.Stock(s, "structural");
            bool called = DropPodService.TryRecall(s, st.BuildingId, t.Value, out _);
            DropPodRecord any = DropPodService.Pods(s).First(x => x.State == DropPodService.StateOutbound);
            StepTo(any.ArriveTick + 2);
            bool picked = new[] { a, b, c }.All(id => M(id).RegionId == DropPodService.InPodRegionId) && Ground(s, "structural", t.Value, 8f) == 0
                          && DropPodService.Pods(s).Count(x => x.State == DropPodService.StateReturning && x.CargoAmount == 17) == 1;
            StepTo(DropPodService.Pods(s).Max(x => x.ArriveTick) + 2);
            Seconds(1.5f);
            int gotStructural = HomeInventory.Stock(s, "structural") - structural0 + Ground(s, "structural", st.Position, 16f);
            int gotScrap = HomeInventory.Stock(s, ItemCatalog.ScrapId) - scrap0 + Ground(s, ItemCatalog.ScrapId, st.Position, 16f);
            bool atStation = new[] { a, b, c }.All(id => OnPlanet(id) && Vector2.Distance(PosOf(id), st.Position) < 16f);
            bool unloaded = (M(a).Cargo == null || M(a).Cargo.Sum(x => x.Amount) == 0) && gotScrap >= 7;
            bool home = atStation && gotStructural == 17 && unloaded && CountPods(s, st, DropPodService.StateCooling) == 4 && LastNotify("drop_landed").Contains("回到");
            Expect(preview && called && picked && home,
                $"H1 回收（FGR-EXP-015）：预览写接回 3 台 + 地上货物 17 个、4 舱（“{DropPodService.PreviewText(p).Replace("\n", " / ")}”）；舱到回收点接上 3 台机器（连同货舱）与地上的货物（{picked}）；" +
                $"回到站里机器在站旁下舱（{atStation}），货舱里的 7 废料按远征卸货规则卸下（{gotScrap}，{unloaded}）、地上捡回的 17 个结构材入库（放不下的落在站旁）（{gotStructural}）；通知“{LastNotify("drop_landed")}”");

            // 舱不够：5 台在回收点，站里 4 舱待命 → 先接 4 台、写明还剩 1 台。
            CampaignState s2 = NewWorld(7110, out BuildingRecord st2);
            Vector2? t2 = st2 != null ? FindTarget(s2, st2.Position, 300f, 460f) : null;
            if (t2 == null)
            {
                return;
            }
            var ids = Enumerable.Range(0, 5).Select(i => SpawnMachine(s2, t2.Value + new Vector2((i % 3 - 1) * 2f, (i / 3) * 2f))).ToArray();
            DropPreview p2 = DropPodService.PreviewRecall(s2, st2.BuildingId, t2.Value);
            bool partial = p2.Ok && p2.PodsNeeded == 4 && p2.Machines.Count == 4 && p2.Leftover == 1 && DropPodService.PreviewText(p2).Contains("还有 1 台");
            // 行军中的远征队成员被回收 = 远征经投送舱返回（离家报告按返回结算）。
            ExpeditionMarchState m = s2.Grid.ExpeditionTeam.March;
            m.Phase = ExpeditionMarchService.PhaseOutbound;
            m.Members = ids.Take(4).ToArray();
            m.SiteId = FracturedCityLayout.RegionId;
            int podReturns = ExpeditionMarchService.PodReturnCount;
            DropPodService.TryRecall(s2, st2.BuildingId, t2.Value, out _);
            StepTo(DropPodService.Pods(s2).Where(x => x.State == DropPodService.StateOutbound).Max(x => x.ArriveTick) + 2);
            bool marchDone = m.Phase == ExpeditionMarchService.PhaseReturned && ExpeditionMarchService.PodReturnCount == podReturns + 1 && m.Members.Length == 0 && OnPlanet(ids[4]);
            Expect(partial && marchDone,
                $"H2 舱不够时先接 4 台、写明还剩 1 台（“{DropPodService.PreviewText(p2).Replace("\n", " / ")}”）；行军中的远征队全部上舱 = 远征按“返回”结算（阶段 {m.Phase}，FGR-EXP-015“回收是正式的撤离方式”）；没接上的那台留在原地（{OnPlanet(ids[4])}）");

            // 回收点没有东西 / 未探索。
            Vector2? empty = FindTarget(s2, st2.Position, 500f, 700f);
            DropPreview none = empty.HasValue ? DropPodService.PreviewRecall(s2, st2.BuildingId, empty.Value) : null;
            Expect(none != null && !none.Ok && none.Code == "nothing-to-recall" && none.Reason.Contains("没有要接的"), $"H3 回收点附近没有机器或货物：被拒并写明（“{none?.Reason}”）");
        }

        /// <summary>H4 / H5 装舱与回收时机器手上的工单（审查 P1）：默认工作优先级、正在搬运（货在货舱里）的机器装舱 / 被回收接走——工单取消、货物守恒、落地后空闲可再派工。</summary>
        private static void CheckBusyMachines()
        {
            CampaignState s = NewWorld(7125, out BuildingRecord st);
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 300f, 460f) : null;
            if (a == 0 || t == null)
            {
                return;
            }
            M(a).WorkPriorities = WorkPriorities.Default(); // 默认岗位与优先级（正常游戏里站旁的机器经常带着工单）
            string order = Haul(s, a, "test:busy-a", 6);
            if (order == null)
            {
                return;
            }
            DropPreview p = DropPodService.Preview(s, st.BuildingId, t.Value, new[] { a }, "", 0);
            bool warned = p.Ok && p.BusyMachines == 1 && DropPodService.PreviewText(p).Contains("手上有活");
            Vector2 boardAt = PosOf(a);
            bool launched = DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out _);
            WorkOrderRecord o = HomeValleyWorkOrders.Find(s, order);
            bool released = launched && o != null && o.State == WorkOrderState.Cancelled && HomeValleyWorkOrders.FindActiveOrderForMachine(s, a) == null
                            && (M(a).Cargo == null || M(a).Cargo.Length == 0) && Ground(s, ItemCatalog.ScrapId, boardAt, 3f) == 6 && M(a).RegionId == DropPodService.InPodRegionId;
            M(a).WorkPriorities.Set(WorkOrderKind.Haul, 0); // 只为让落地后的“可再派工”断言确定（不被自动分配抢先派走）
            StepUntil(() => OnPlanet(a), 40);
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, PosOf(a) + new Vector2(1f, 0f), ItemCatalog.ScrapId, 2, "test:busy-a2");
            bool idle = OnPlanet(a) && HomeValleyWorkOrders.FindActiveOrderForMachine(s, a) == null
                        && HomeValleyWorkOrders.TryCreateHaul(s, "ground:test:busy-a2", a).Success;
            Expect(warned && released && idle,
                $"H4 默认优先级、正在搬运（6 废料在货舱里）的机器装舱：预览写明“手上有活”（{warned}）；发射时搬运单取消、货在装舱处落地（守恒 6 个）、机器进舱（{released}）；" +
                $"落地后没有挂着旧工单，能再派工（{idle}）");

            // 回收：回收点那台机器正在搬运——舱接走它时同样取消工单、货物守恒（落在回收点，或被同批的货物舱捡走）。
            CampaignState s2 = NewWorld(7126, out BuildingRecord st2);
            Vector2? t2 = st2 != null ? FindTarget(s2, st2.Position, 300f, 460f) : null;
            int b = t2 != null ? SpawnMachine(s2, t2.Value + new Vector2(2f, 2f)) : 0;
            if (b == 0)
            {
                return;
            }
            M(b).WorkPriorities = WorkPriorities.Default();
            string order2 = Haul(s2, b, "test:busy-b", 5);
            if (order2 == null)
            {
                return;
            }
            bool called = DropPodService.TryRecall(s2, st2.BuildingId, t2.Value, out DropPreview rp);
            DropPodRecord rpod = DropPodService.Pods(s2).FirstOrDefault(x => x.State == DropPodService.StateOutbound && x.RecallMachineId == b);
            if (rpod != null)
            {
                StepTo(rpod.ArriveTick + 2);
            }
            WorkOrderRecord o2 = HomeValleyWorkOrders.Find(s2, order2);
            int scrap = Ground(s2, ItemCatalog.ScrapId, t2.Value, DropPodService.RecallRadius * 2f)
                        + DropPodService.Pods(s2).Where(x => x.CargoItem == ItemCatalog.ScrapId).Sum(x => x.CargoAmount);
            bool recalled = called && rp.BusyMachines == 1 && DropPodService.PreviewText(rp).Contains("手上有活") && rpod != null && rpod.MachineId == b
                            && o2 != null && o2.State == WorkOrderState.Cancelled && HomeValleyWorkOrders.FindActiveOrderForMachine(s2, b) == null
                            && (M(b).Cargo == null || M(b).Cargo.Length == 0) && scrap == 5;
            Expect(recalled, $"H5 回收接走正在搬运（5 废料在货舱里）的机器：预览写明“手上有活”，舱接走它时搬运单取消、货物守恒（{scrap} / 5），机器上舱（{recalled}）");
        }

        /// <summary>给机器派一张搬运单并走到“货已在货舱里、正走向交付点”（InProgress）。</summary>
        private static string Haul(CampaignState s, int machine, string salvageId, int amount)
        {
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, PosOf(machine) + new Vector2(1f, 0f), ItemCatalog.ScrapId, amount, salvageId);
            var r = HomeValleyWorkOrders.TryCreateHaul(s, "ground:" + salvageId, machine);
            if (!r.Success || !HomeValleyWorkOrders.OnArrivedAtHaulSource(s, r.WorkOrderId))
            {
                Fail($"测试准备：给机器 {machine} 派搬运单失败（{r.FailureReason}）");
                return null;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.Find(s, r.WorkOrderId);
            if (o == null || o.State != WorkOrderState.InProgress || M(machine).Cargo == null || M(machine).Cargo.Sum(c => c.Amount) != amount)
            {
                Fail($"测试准备：搬运单没有进入“货在货舱里”（{o?.State}）");
                return null;
            }
            return r.WorkOrderId;
        }

        // ── I 燃油管线 ─────────────────────────────────────────────────────────────

        private static void CheckFuelPipe()
        {
            CampaignState s = NewWorld(7111, out BuildingRecord st, fuelLiters: 0f);
            if (st == null || !PipeNetworkService.IsRunning)
            {
                Fail("I 测试准备：没有投送站或管线内核没在跑");
                return;
            }
            DropStationRecord r = Rec(s, st);
            string noFuel = BuildingStatusService.Evaluate(s, st).Reason;
            // 站边一格铺两格管线，远端接一个燃油源（测试捷径：源由管线内核直接给；精炼塔出油由 FgProductionSelfCheck 覆盖）。
            GridCell? near = null, far = null;
            var foot = new List<GridCell>();
            HomeGridService.FootprintOf(st, foot);
            foreach (GridCell f in foot)
            {
                foreach (var d in new[] { new GridCell(1, 0), new GridCell(-1, 0), new GridCell(0, 1), new GridCell(0, -1) })
                {
                    var n = new GridCell(f.X + d.X, f.Y + d.Y);
                    var n2 = new GridCell(n.X + d.X, n.Y + d.Y);
                    if (near == null && !foot.Contains(n) && HomeGridService.ValidatePipeCell(s, n, PipePieceKind.Pipe).Ok && HomeGridService.ValidatePipeCell(s, n2, PipePieceKind.Pipe).Ok)
                    {
                        near = n;
                        far = n2;
                    }
                }
            }
            if (near == null)
            {
                Fail("I 测试准备：投送站旁找不到能铺管线的两格");
                return;
            }
            bool placed = PipeNetworkService.TryPlace(s, near.Value, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, far.Value, PipePieceKind.Pipe, 0, 0).Ok;
            int fuel = PipeNetworkService.FluidId("fuel");
            int src = PipeNetworkService.Kernel.AddProducer(far.Value.X, far.Value.Y, fuel, 2_000_000, 2_000_000);
            bool filled = StepUntil(() => DropPodService.FuelLiters(r) >= 60f, 60);
            float liters = DropPodService.FuelLiters(r);
            PipeNetworkService.Kernel.RemoveProducer(src, out _);
            string withFuel = BuildingStatusService.Evaluate(s, st).Reason;
            Expect(placed && filled && r.ConsumerId > 0 && noFuel.Contains("没有燃油") && withFuel.Contains("燃油") && !withFuel.Contains("没有燃油"),
                $"I1 燃油走管线：没接管线时状态写“{noFuel.Split('\n')[0]}”；站边接上装燃油的管线后站在管线上挂一个消费者，油箱进油（{liters:0} 升，最多 {DropPodService.TankLiters:0} 升）；状态“{withFuel.Replace("\n", " / ")}”");
        }

        // ── J 存读档 ───────────────────────────────────────────────────────────────

        private static CampaignState Scenario(int seed, out BuildingRecord st, out int a, out Vector2 target)
        {
            CampaignState s = NewWorld(seed, out st);
            a = 0;
            target = default;
            if (st == null)
            {
                return s;
            }
            a = SpawnMachine(s, Beside(s, st, 0));
            Vector2? t = FindTarget(s, st.Position, 420f, 600f);
            if (t == null)
            {
                return s;
            }
            target = t.Value;
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 20, clampToSpace: false);
            DropPodService.TryLaunch(s, st.BuildingId, target, new[] { a }, "structural", 20, out _);
            Seconds(1.3f);
            // 另一舱飞去回收近处的一台（短程）。
            Vector2? r = FindTarget(s, st.Position, 60f, 120f);
            if (r.HasValue && SpawnMachine(s, r.Value) > 0)
            {
                DropPodService.TryRecall(s, st.BuildingId, r.Value, out _);
            }
            Seconds(3.7f);
            return s;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = Scenario(7112, out BuildingRecord st, out int a, out Vector2 target);
            if (st == null || a == 0)
            {
                return;
            }
            WorldSimulation.SyncAllForSave();
            string domain = Json(DropPodService.StateOf(s));
            bool rich = DropPodService.Pods(s).Any(x => x.State == DropPodService.StateOutbound && x.MachineId == a) && DropPodService.Pods(s).Any(x => x.Mission == DropPodService.MissionRecall);
            string inPod = M(a).RegionId;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Restore();
            CampaignState l = CampaignSession.Current;
            string after = Json(DropPodService.StateOf(l));
            Expect(save.Success && rich && domain == after && M(a).RegionId == inPod && inPod == DropPodService.InPodRegionId && !InKernel(a),
                $"J1 FG08 第 9 节 / FGT-EXP-011“投送途中存读档”：真文件存读档后投送域（站、舱的状态 / 航线 / 到达步 / 载荷 / 燃油 / 回收指定的机器、统计）逐字段一致（{domain == after}）；" +
                "舱里的机器读档后仍在舱里（不复制、不落地）");
            long arrive = DropPodService.Pods(l).Where(x => x.MachineId == a).Select(x => x.ArriveTick).First();
            StepTo(arrive + 3);
            Expect(OnPlanet(a) && Vector2.Distance(PosOf(a), target) <= DropPodService.FlatRadius + 2f, "J2 读档后接着飞：在原到达步把机器放到落点");

            // 旧档：没有投送域 = 空域。
            CampaignState old = CampaignState.CreateNew("fgdrop-old", "Standard", 7113);
            old.Grid.Drop = null;
            CampaignFgStateDomains.EnsureAll(old);
            Expect(old.Grid.Drop != null && old.Grid.Drop.Stations.Length == 0 && old.Grid.Drop.Pods.Length == 0 && old.Grid.Drop.NextPodSerial == 1, "J3 旧档没有投送域：补成空域（没有投送站）");
        }

        private static void Restore()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
        }

        private static void CheckLoadContinuation()
        {
            CampaignState s = Scenario(7114, out BuildingRecord st, out int a, out _);
            if (st == null || a == 0)
            {
                return;
            }
            WorldSimulation.SyncAllForSave();
            long tickAtSave = GameClock.Ticks;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(40f);
            string straight = Sig(s, a);
            Restore();
            bool sameTick = GameClock.Ticks == tickAtSave;
            Seconds(40f);
            string loaded = Sig(CampaignSession.Current, a);
            Expect(save.Success && sameTick && straight == loaded,
                $"J4 读档接着跑 = 不存档一路跑：存档停在舱在飞（投送 + 回收）时（步 {tickAtSave}），之后各跑 40 游戏秒（着陆、回收、返程、冷却），舱 / 机器位置 / 地上货物 / 仓库逐字段一致"
                + (straight == loaded ? string.Empty : $"\n不存档：{straight}\n读档后：{loaded}"));
        }

        // ── K / L 暂停、倍速、观察 ─────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, out BuildingRecord st, observe: observe);
            pausedHeld = true;
            if (st == null)
            {
                return "no-station";
            }
            int a = SpawnMachine(s, Beside(s, st, 0));
            Vector2? t = FindTarget(s, st.Position, 300f, 460f);
            if (t == null)
            {
                return "no-target";
            }
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 20, clampToSpace: false);
            DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "structural", 20, out _);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Sig(s, a);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Sig(s, a) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Sig(s, a);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7115, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != null && !reference.StartsWith("no-", StringComparison.Ordinal),
                "K 暂停中（120 帧）舱不动、不着陆；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒（起飞 → 着陆放下机器和货物 → 返程），舱 / 机器 / 地上货物逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7116, true, 1f, false, out _);
            string unseen = RunScenario(7116, false, 1f, false, out _);
            Expect(seen == unseen && !seen.StartsWith("no-", StringComparison.Ordinal),
                "L FGR-BASE-021 / FG00 基线：同一次投送在观察与不观察家园时跑 30 游戏秒，结果逐字段一致（离开家园时投送舱照常飞、着陆）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        /// <summary>静默夜场景：起飞后静默夜开始、途中归属站被毁（自动驾驶不改降），跑完整个往返。</summary>
        private static string RunSilentScenario(int seed, bool observe)
        {
            CampaignState s = NewWorld(seed, out BuildingRecord st, observe: observe);
            BuildingRecord st2 = st != null ? PlaceStation(s, CoreCenter(s), 14f, 44f, 1, 300f) : null;
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 1500f, 1800f) : null;
            if (st2 == null || a == 0 || t == null)
            {
                return "no-setup";
            }
            SilentNightService.Install();
            try
            {
                HomeInventory.Add(s, ItemCatalog.Find("structural"), 20, clampToSpace: false);
                DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "structural", 20, out _);
                DropPodRecord pod = DropPodService.Pods(s).First(x => x.State == DropPodService.StateOutbound);
                SilentNightService.ScheduleForTests(s, 1.0, 600.0, raid: false);
                if (!StepUntil(() => SilentNightService.IsActiveNow(), 120))
                {
                    return "no-night";
                }
                BuildingOps.ApplyDamage(s, st.BuildingId, 100000f);
                StepTo(pod.ArriveTick + 2);
                StepTo(DropPodService.Pods(s).Where(x => x.State == DropPodService.StateReturning).Select(x => x.ArriveTick).DefaultIfEmpty(GameClock.Ticks).Max() + 2);
                return (SilentNightService.IsActiveNow() ? "night|" : "day|") + Sig(s, a);
            }
            finally
            {
                SilentNightService.ResetForTests();
            }
        }

        private static void CheckSilentNightObserved()
        {
            string seen = RunSilentScenario(7127, true);
            string unseen = RunSilentScenario(7127, false);
            Expect(seen == unseen && seen.StartsWith("night|", StringComparison.Ordinal),
                "L2 FG00 基线 / FGR-BASE-021（静默夜）：起飞后静默夜开始、途中投送站被毁，舱按自动驾驶飞完往返（不改降）——观察与不观察家园时结果逐字段一致" +
                (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        /// <summary>B25：种子测试集里几张不同地形——按地形找空地放站、找落点，投送都能完成，不依赖固定坐标。</summary>
        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(3).ToArray();
            var bad = new List<string>();
            int hazards = 0;
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed, out BuildingRecord st);
                int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
                Vector2? t = st != null ? FindTarget(s, st.Position, 250f, 500f) : null;
                if (a == 0 || t == null)
                {
                    bad.Add($"{seed}：放不下站或找不到落点");
                    continue;
                }
                DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a }, "", 0, out _);
                StepUntil(() => OnPlanet(a), 30);
                if (!OnPlanet(a) || Vector2.Distance(PosOf(a), t.Value) > DropPodService.FlatRadius + 2f)
                {
                    bad.Add($"{seed}：没有在落点着陆");
                }
                hazards += HazardCenterCheck(s) == "hazard-center" ? 1 : 0;
            }
            Expect(bad.Count == 0, $"M1 B25 种子集 {string.Join("/", seeds)}：按地形放站、找落点，投送都在落点着陆" + (bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad)));
            Expect(hazards >= 1, $"M2 危害区中心不能着陆：{seeds.Length} 个种子里 {hazards} 个找到了危害区并在中心被拒（hazard-center）");
        }

        // ── N 界面 ─────────────────────────────────────────────────────────────────

        private static bool Click(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private static void CheckUi()
        {
            CampaignState s = NewWorld(7117, out BuildingRecord st);
            if (st == null)
            {
                return;
            }
            int a = SpawnMachine(s, Beside(s, st, 0), cargoScrap: 2);
            Vector2? t = FindTarget(s, st.Position, 300f, 460f);
            if (a == 0 || t == null)
            {
                return;
            }
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 30, clampToSpace: false);
            GameObject pgo = null, dgo = null, mgo = null;
            try
            {
                // 建筑面板“投送站…”只在投送站上出现，点它打开投送站面板。
                VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out pgo);
                ProductionPanelUIToolkit.InWorldOverrideForTests = true;
                ProductionPanelUIToolkit prod = pgo.AddComponent<ProductionPanelUIToolkit>();
                prod.BindView(proot);
                VisualElement droot = FgProductionSelfCheck.MountUxml(UiKitFolder + "DropStationPanel.uxml", out dgo);
                DropStationPanelUIToolkit.InWorldOverrideForTests = true;
                DropStationPanelUIToolkit panel = dgo.AddComponent<DropStationPanelUIToolkit>();
                panel.BindView(droot);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "DropStationRow.uxml"));
                ProductionPanelUIToolkit.Open(s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2).BuildingId);
                prod.Refresh();
                bool hiddenElsewhere = prod.DropButton.ClassListContains("bn-hidden");
                ProductionPanelUIToolkit.Open(st.BuildingId);
                prod.Refresh();
                bool shown = !prod.DropButton.ClassListContains("bn-hidden") && prod.DropButton.text == "投送站…";
                bool opened = Click(prod.DropButton) && DropStationPanelUIToolkit.IsOpen && DropStationPanelUIToolkit.BuildingId == st.BuildingId;
                panel.Refresh(force: true);
                Expect(hiddenElsewhere && shown && opened && panel.StatusText.Contains("投送舱") && panel.StatusText.Contains("燃油"),
                    $"N1 FGU-46 入口：投送站的建筑面板有“投送站…”（别的建筑没有），点它打开这座站的面板；状态写舱数量与燃油（“{panel.StatusText.Replace("\n", " / ")}”）");

                // 装载：点站旁的机器行选上；货物下拉选结构材、+20。
                int row = Enumerable.Range(0, panel.MachineRowCount).FirstOrDefault(i => panel.MachineRowId(i) == a);
                bool rowClick = panel.MachineRowCount >= 1 && Click(panel.MachineRowButton(row)) && panel.Selected.Contains(a) && panel.MachineRowText(row).StartsWith("☑", StringComparison.Ordinal)
                                && panel.MachineRowText(row).Contains("货舱 2");
                int sIndex = panel.CargoDropdown.choices.FindIndex(c => c.Contains(GameText.Get("item.structural.name")));
                panel.SelectCargo(sIndex);
                bool cargo = panel.CargoItem == "structural" && panel.CargoAmount == 20 && Click(panel.CargoPlusButton) && panel.CargoAmount == 30 && Click(panel.CargoMinusButton) && panel.CargoAmount == 10;
                bool launchOff = !panel.LaunchButton.enabledSelf; // 没选落点时不能发射
                Expect(rowClick && cargo && launchOff, $"N2 装载：点机器行选上（“{panel.MachineRowText(row)}”）；货物下拉选结构材默认一舱 20 个，+20 / -20 按舱增减、最多仓库里有的（{panel.CargoAmount}）；没选落点时“发射”不可点");

                // 选落点：战略地图选点模式（悬停写预览，左键选定回到面板）。
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                mgo = new GameObject("fgdrop-map");
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                StrategicMapUIToolkit map = mgo.AddComponent<StrategicMapUIToolkit>();
                map.BindView(mroot);
                OutpostRecord aa = WorldOutpostSystem.SpawnOutpost(s, "foundry", new GridCell(Mathf.RoundToInt(t.Value.x), Mathf.RoundToInt(t.Value.y + 150f)), false);
                HomeGridService.RevealArea(s, new Vector2(aa.CellX, aa.CellY), 30f);
                // 平时打开地图：防空覆盖画成红色区域，指着它写是哪个据点、不能着陆。
                StrategicMapUIToolkit.OpenCentered(aa.CellX, aa.CellY, 300.0);
                map.Tick(force: true);
                bool redZone = map.Model.Circles.Any(c => c.AntiAir && Math.Abs(c.X - aa.CellX) < 0.5 && c.Radius >= 60f);
                map.HoverAt(map.CanvasOf(new Vector2(aa.CellX, aa.CellY - 20f)));
                string hoverAa = map.HoverText;
                StrategicMapUIToolkit.Close();
                bool picking = Click(panel.PickTargetButton) && !DropStationPanelUIToolkit.IsOpen && StrategicMapUIToolkit.IsOpen && StrategicMapUIToolkit.PickActive;
                map.Tick(force: true);
                // 选点模式下关掉据点图层，红色区域照样画（选点时必须看得见哪里会被拒）。
                WorldMapFilters.Set(WorldMapLayer.Outposts, false);
                map.Tick(force: true);
                bool redWhenLayerOff = map.Model.Circles.Any(c => c.AntiAir && Math.Abs(c.X - aa.CellX) < 0.5);
                WorldMapFilters.Set(WorldMapLayer.Outposts, true);
                map.Tick(force: true);
                Expect(redWhenLayerOff, $"N3b 选点模式下关掉据点图层，防空红色区域照样画出（{redWhenLayerOff}）");
                string hint = mroot.Q<Label>("MapHint").text;
                map.HoverAt(map.CanvasOf(t.Value));
                string hoverOk = map.HoverText;
                map.ClickAt(map.CanvasOf(t.Value));
                bool picked = !StrategicMapUIToolkit.PickActive && !StrategicMapUIToolkit.IsOpen && DropStationPanelUIToolkit.IsOpen && panel.Target.HasValue
                              && Vector2.Distance(panel.Target.Value, t.Value) <= 1.5f;
                panel.Refresh(force: true);
                Expect(picking && hint.Contains("选择投送落点") && redZone && hoverAa.Contains("不能在这里着陆") && hoverOk.Contains("单程") && hoverOk.Contains("燃油") && picked && panel.PreviewLabel.Contains("燃油") && panel.LaunchButton.enabledSelf,
                    $"N3 选落点（FGU-46 目标选择 / 防空警告）：“在地图上选落点…”关面板、打开战略地图选点（提示“{hint}”）；铸造据点的防空覆盖画成红色区域（{redZone}），指着它写“{hoverAa}”；" +
                    $"悬停能降落的点写距离 / 飞行时间 / 燃油（“{hoverOk.Replace("\n", " / ")}”）；左键选定回到面板，预览同一份、“发射”可点（{picked}）");

                // 选点中右键 / Esc 取消也回到面板。
                Click(panel.PickRecallButton);
                bool recallPick = StrategicMapUIToolkit.PickActive && StrategicMapUIToolkit.IsOpen;
                StrategicMapUIToolkit.Close();
                bool cancelled = !StrategicMapUIToolkit.PickActive && DropStationPanelUIToolkit.IsOpen && panel.RecallPoint == null;
                Expect(recallPick && cancelled, "N4 选回收点时关掉地图（右键 / Esc / 关闭）= 取消，回到投送站面板，回收点不变");

                // 发射：面板按钮 → 舱在飞，面板列出每舱一行（去程 / 剩余时间），地图上有舱图标与航线。
                bool launched = Click(panel.LaunchButton) && DropPodService.Pods(s).Count(x => x.State == DropPodService.StateOutbound) == 2;
                WorldSimulation.StepMany(5);
                panel.Refresh(force: true);
                StrategicMapUIToolkit.OpenCentered(st.Position.x, st.Position.y, 700.0);
                map.Tick(force: true);
                bool podIcon = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.DropPod) && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.DropTarget);
                StrategicMapUIToolkit.Close();
                var rig = new GameObject("__fgdrop_views") { hideFlags = HideFlags.HideAndDontSave };
                DropPodViews.FrameUpdate(s, rig.transform);
                bool drawn = DropPodViews.VisibleCount == 2;
                DropPodViews.Clear();
                Object.DestroyImmediate(rig);
                Expect(launched && panel.FlyingText.Contains("投送中") && panel.MessageText.Contains("起飞") && podIcon && drawn,
                    $"N5 “发射”：2 舱起飞，面板“投送舱”逐舱写去程 / 载荷 / 剩余时间（“{panel.FlyingText.Replace("\n", " / ")}”）；战略地图上画出在飞的舱与落点（{podIcon}）；世界里画出 2 个在飞的舱（占位方块，{drawn}）");

                // 预览不通过：落点超出投送距离。
                panel.SelectCargo(panel.CargoDropdown.choices.FindIndex(c => c.Contains(GameText.Get("item.structural.name"))));
                panel.SetTarget(st.Position + (t.Value - st.Position).normalized * (DropPodService.RangeCells + 300f));
                panel.Refresh(force: true);
                bool deniedText = panel.LastPreview != null && !panel.LastPreview.Ok && panel.PreviewLabel.Contains("不能发射") && !panel.LaunchButton.enabledSelf;
                Expect(deniedText, $"N6 预览不通过时写明原因、“发射”不可点（“{panel.PreviewLabel.Replace("\n", " / ")}”）");

                // 呼叫回收（FGR-EXP-015 玩家入口，审查 P1）：面板“在地图上选回收点…”→ 地图悬停写回收预览 → 左键选定回到面板 → 预览通过时“呼叫回收”可点 → 点它，舱起飞去接 → 接回站旁。
                Vector2? rp = FindTarget(s, st.Position, 300f, 460f, skip: 3);
                int rc = rp.HasValue ? SpawnMachine(s, rp.Value + new Vector2(1f, 1f)) : 0;
                if (rc != 0)
                {
                    bool recallPicking = Click(panel.PickRecallButton) && StrategicMapUIToolkit.PickActive && !DropStationPanelUIToolkit.IsOpen;
                    map.Tick(force: true);
                    string recallHint = mroot.Q<Label>("MapHint").text;
                    map.HoverAt(map.CanvasOf(rp.Value));
                    string hoverRecall = map.HoverText;
                    map.ClickAt(map.CanvasOf(rp.Value));
                    panel.Refresh(force: true);
                    bool recallPicked = !StrategicMapUIToolkit.PickActive && DropStationPanelUIToolkit.IsOpen && panel.RecallPoint.HasValue && Vector2.Distance(panel.RecallPoint.Value, rp.Value) <= 1.5f
                                        && panel.RecallPreviewLabel.Contains("接回机器 1 台") && panel.RecallButton.enabledSelf;
                    bool recallGo = Click(panel.RecallButton) && panel.MessageText.Contains("飞去回收");
                    DropPodRecord rpod = DropPodService.Pods(s).FirstOrDefault(x => x.State == DropPodService.StateOutbound && x.Mission == DropPodService.MissionRecall);
                    bool recallFlying = rpod != null && rpod.RecallMachineId == rc;
                    if (rpod != null)
                    {
                        StepTo(rpod.ArriveTick + 2);
                        StepTo(rpod.ArriveTick + 2);
                        Seconds(1.5f);
                    }
                    bool recallHome = OnPlanet(rc) && Vector2.Distance(PosOf(rc), st.Position) < 16f;
                    Expect(recallPicking && recallHint.Contains("选择回收点") && hoverRecall.Contains("接回机器") && recallPicked && recallGo && recallFlying && recallHome,
                        $"N8 呼叫回收（面板入口）：“在地图上选回收点…”进入选点（“{recallHint}”），悬停写回收预览（“{hoverRecall.Replace("\n", " / ")}”）；左键选定回到面板，预览“{panel.RecallPreviewLabel.Replace("\n", " / ")}”、" +
                        $"“呼叫回收”可点（{recallPicked}）；点它舱起飞去接（{recallFlying}），机器被接回站旁（{recallHome}）");

                    // 被拒：回收点附近什么都没有——预览写原因、“呼叫回收”不可点；强行触发也被拒并出拒绝音。
                    Vector2? emptyPoint = FindTarget(s, st.Position, 520f, 700f, skip: 1);
                    if (emptyPoint.HasValue)
                    {
                        panel.SetRecallPoint(emptyPoint.Value);
                        panel.Refresh(force: true);
                        int denied0 = DropPodService.LastRejectCount;
                        bool off = !panel.RecallButton.enabledSelf && panel.RecallPreviewLabel.Contains("没有要接的");
                        panel.ClickRecall();
                        bool deniedSound = DropPodService.LastRejectCount == denied0 + 1 && panel.MessageText.Contains("没有要接的");
                        Expect(off && deniedSound, $"N9 回收被拒：回收点附近没有机器或货物时预览写明原因、“呼叫回收”不可点（{off}）；强行触发同样被拒、出拒绝音、面板写原因（“{panel.MessageText}”）（{deniedSound}）");
                    }
                }

                // 选点途中整体关闭世界界面（回主菜单 / 失败页读档都走 UiKitRuntime.CloseWorldUi，第 2 轮审查 P2）：
                // 选点状态被清掉、地图与投送站面板都关着；取消回调不能把投送站面板重新打开。
                if (!DropStationPanelUIToolkit.IsOpen)
                {
                    DropStationPanelUIToolkit.Open(st.BuildingId);
                }
                bool pickBeforeClose = Click(panel.PickRecallButton) && StrategicMapUIToolkit.PickActive && StrategicMapUIToolkit.IsOpen && !DropStationPanelUIToolkit.IsOpen;
                UiKitRuntime.CloseWorldUi();
                bool allClosed = !StrategicMapUIToolkit.PickActive && !StrategicMapUIToolkit.IsOpen && !DropStationPanelUIToolkit.IsOpen;
                Expect(pickBeforeClose && allClosed,
                    $"N10 选点途中整体关闭世界界面（CloseWorldUi）：选点状态清掉、地图关闭，投送站面板没有被取消回调重新打开（进入选点 {pickBeforeClose}，全部关闭 {allClosed}）");

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "DropStationPanel.uxml", "DropWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("DropRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "N7 布局探针（四种分辨率 + 超长文字 + USS 体检）：投送站面板 " + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe));
            }
            finally
            {
                WorldMapFilters.Set(WorldMapLayer.Outposts, true);
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                DropStationPanelUIToolkit.Close();
                DropStationPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                foreach (GameObject go in new[] { pgo, dgo, mgo })
                {
                    if (go != null)
                    {
                        Object.DestroyImmediate(go);
                    }
                }
            }
        }

        // ── O 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7118, out BuildingRecord st, pods: 8, fuelLiters: 1200f);
            if (st == null)
            {
                return;
            }
            st.Tier = 2;
            HomeInventory.Add(s, ItemCatalog.Find(Pod), 4, clampToSpace: false);
            DropPodService.LoadPods(s, Rec(s, st), st, 8);
            Vector2? t = FindTarget(s, st.Position, 600f, 800f);
            if (t == null)
            {
                return;
            }
            HomeInventory.Add(s, ItemCatalog.Find("structural"), 200, clampToSpace: false);
            bool ok = DropPodService.TryLaunch(s, st.BuildingId, t.Value, Array.Empty<int>(), "structural", 160, out _);
            int hz = GameClock.StepHz;
            const int N = 2000;
            var sw = Stopwatch.StartNew();
            long tk = GameClock.Ticks / 60 * 60 + 7; // 不是对账步、不是事件步：O(1)
            for (int i = 0; i < N; i++)
            {
                DropPodService.WorldStep(s, tk, hz);
            }
            sw.Stop();
            double idle = sw.Elapsed.TotalMilliseconds / N;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                DropPodService.Sync(s);
            }
            sw.Stop();
            double sync = sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (int i = 0; i < 200; i++)
            {
                DropPodService.FlyingForView(s, Scratch);
            }
            sw.Stop();
            double view = sw.Elapsed.TotalMilliseconds / 200;
            PerfLines.Add($"8 舱在飞：非事件步 WorldStep {idle * 1000:F2} µs / 步；对账（站点 + 燃油 + 自动补满 + 改降，{s.BuildingRecords.Length} 座建筑）{sync:F3} ms / 次（每游戏秒一次）；地图读舱 {view * 1000:F1} µs / 次");
            bool within = idle < 0.01 && sync < 0.5 && view < 0.2;
            bool warn = !within && idle < 0.02 && sync < 1.0 && view < 0.4;
            if (warn)
            {
                Line($"  ⚠ 性能警告（超线不到 2 倍）：非事件步 {idle * 1000:F2} µs（线 10 µs）、对账 {sync:F3} ms（线 0.5）、地图读 {view * 1000:F1} µs（线 200）");
            }
            Expect(ok && (within || warn), $"O 性能（单次测量，Editor batchmode / Mono JIT）：非事件步 {idle * 1000:F2} µs（线 10 µs，O(1)）、对账 {sync:F3} ms（线 0.5 ms）、地图读舱 {view * 1000:F1} µs（线 200 µs）");
        }

        private static readonly List<(int Serial, Vector2 Pos, Vector2 To, bool Returning, string Label)> Scratch = new List<(int, Vector2, Vector2, bool, string)>(16);

        // ── P 旧档 / 坏值 ───────────────────────────────────────────────────────────

        private static void CheckOldSave()
        {
            CampaignState old = CampaignState.CreateNew("fgdrop-bad", "Standard", 7119);
            old.Grid.Drop = new DropPodState
            {
                Stations = new[] { new DropStationRecord { BuildingId = "home:drop_station#1", HeldFuelMl = -5 }, new DropStationRecord { BuildingId = "home:drop_station#1" }, null },
                Pods = new[]
                {
                    new DropPodRecord { Serial = 3, State = 9, Mission = 7, CargoItem = null, StationId = null, Note = null },
                    new DropPodRecord { Serial = 3, State = DropPodService.StateOutbound, FromX = float.NaN, CargoAmount = -2 },
                    null,
                },
                NextPodSerial = 1,
                NextGroupSerial = 0,
            };
            DropPodService.EnsureState(old);
            DropPodState d = old.Grid.Drop;
            bool clamped = d.Stations.Length == 1 && d.Stations[0].HeldFuelMl == 0 && d.Pods.Length == 2 && d.Pods[0].State == DropPodService.StateDocked && d.Pods[0].Mission == 0
                           && d.Pods[0].CargoItem == string.Empty && d.Pods[0].StationId == string.Empty && d.Pods[1].Serial != d.Pods[0].Serial && d.Pods[1].State == DropPodService.StateReturning
                           && d.Pods[1].ArriveTick == 0 && !float.IsNaN(d.Pods[1].FromX) && d.Pods[1].CargoAmount == 0 && d.NextPodSerial > d.Pods.Max(p => p.Serial) && d.NextGroupSerial == 1;
            Expect(clamped, "P 坏值：重复站点只留第一条、燃油负数归 0、状态 / 任务越界归“在站待命”、空字符串补齐、航线坐标坏了按“此刻回到归属站”处理（下一事件步走回站卸载）、重复序号重编、序号计数补齐");
        }

        /// <summary>P2 坏档里舱载着机器（审查 P2）：航线 NaN 的在飞舱、在站却还装着机器的舱——读档补全后下一事件步按回站卸载，机器回到星球表面、不会永久困在舱里。</summary>
        private static void CheckStrandedPayload()
        {
            CampaignState s = NewWorld(7128, out BuildingRecord st);
            int a = st != null ? SpawnMachine(s, Beside(s, st, 0)) : 0;
            int b = st != null ? SpawnMachine(s, Beside(s, st, 1)) : 0;
            Vector2? t = st != null ? FindTarget(s, st.Position, 300f, 460f) : null;
            if (a == 0 || b == 0 || t == null)
            {
                return;
            }
            DropPodService.TryLaunch(s, st.BuildingId, t.Value, new[] { a, b }, "", 0, out _);
            DropPodRecord[] pods = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateOutbound).OrderBy(x => x.Serial).ToArray();
            if (pods.Length != 2)
            {
                Fail("测试准备：两台机器没有都上舱");
                return;
            }
            Seconds(1f);
            pods[0].FromX = float.NaN;                    // 坏档 1：在飞舱的航线坏了
            pods[1].State = DropPodService.StateCooling;  // 坏档 2：冷却中的舱里还装着机器
            DropPodService.EnsureState(s);
            WorldSimulation.StepMany(3);
            Seconds(1f);
            bool freed = OnPlanet(a) && OnPlanet(b) && Vector2.Distance(PosOf(a), st.Position) < 16f && Vector2.Distance(PosOf(b), st.Position) < 16f
                         && pods.All(x => x.MachineId == 0 && x.State == DropPodService.StateCooling) && !DropPodService.IsInPod(s, a) && !DropPodService.IsInPod(s, b);
            Expect(freed, $"P2 坏档里舱载着机器（航线 NaN / 在站却装着机器）：读档补全后按回站卸载，两台机器在站旁回到星球表面、舱进入冷却（{freed}）");
        }

        // ── 报告 ─────────────────────────────────────────────────────────────────

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
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                Pass(message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Pass(string message)
        {
            _pass++;
            Line("  ✓ " + message);
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text) => _report.AppendLine(text);
    }
}
