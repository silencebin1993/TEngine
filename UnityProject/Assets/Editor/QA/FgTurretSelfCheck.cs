using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-01 炮塔（固定底盘）的自动验收（FG06 FGR-DEF-001～005；FG02 FGR-FW-012；FGT-DEF-001；DEBT-FG2FW02-04 / -07）。
    /// 全部起真实系统：真实家园（世界模拟、电网、管线、施工机器）、真实炮塔座建筑、家园战斗内核（选目标 / 转向 / 弹体 / 补给扣减 / 击杀都在 Main/Sim）、
    /// 真文件存读档、真 UXML 面板。
    /// A 数据；B 正式放置与施工（建造模式选中 → 放置预览射程圈 → 建造栏选蓝图 → 放下虚影 → 机器施工 → 进内核）；C 射程 / 转速 / 弹体 / 击毁数（内核开火）；
    /// D 五种目标模式（定点布置 + 随机布置与暴力扫描逐个对照）、批量设置、复制设置；E 补给（只用电 / 缺管线 / 错流体 / 管线干了 / 流体逐毫升守恒 / 补给中断 / 缺电 / 禁用）；
    /// F 蓝图负向（不是固定底盘、占地不符、不存在 / 归档、核心固件与裸跑固件放进炮塔蓝图被拒、旧蓝图里的核心固件、版本）；G 改名、名册“炮塔”分类、炮塔面板与布局探针；
    /// H 受伤与维修的耐久对账、等级（射程 / 减伤）、攻城标记与“先打拆建筑的”、被摧毁留虚影与重建保留设置；I 接入（负向、亲自瞄准开火、不自动开火、静默夜 / 断电 / 被毁 / 信号移走时离开）；
    /// J 真文件存读档（内核格式 9 往返、格式 8 旧快照、旧档没有炮塔域、坏值钳回）；K 暂停与 0.5x～3x；L 观察 / 不观察一致；M 家园训练靶迁进战斗内核（DEBT-FG2FW02-07）；N 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgTurretSelfCheck）。
    /// </summary>
    public static class FgTurretSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 6;
        private const string L = TurretCatalog.LightTypeId;
        private const string H = TurretCatalog.HeavyTypeId;
        private const string BpLight = TurretService.DefaultLightBlueprintId;
        private const string BpHeavy = TurretService.DefaultHeavyBlueprintId;
        private const string BpBeam = "bp_tu_beam";
        private const string BpWet = "bp_tu_wet";
        private const string BpPort = "bp_tu_cannon_port";
        private const string BpMobile = "bp_tu_mobile";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 炮塔（固定底盘）")]
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
            Line("\n[炮塔（固定底盘）] 放置 / 施工 / 目标模式 / 补给 / 接入 / 击毁数 / 改名，射击全部在战斗内核（FG6-DEF-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgturret-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                TestRangeCatalog.Reload();
                TurretCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；选目标 / 转向 / 弹体 / 补给扣减在 AOT 战斗内核（Burst），对账与面板在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckPlacementAndBuild);
                Step(CheckWeaponAndKernel);
                Step(CheckTargetModes);
                Step(CheckBatchAndCopy);
                Step(CheckSupply);
                Step(CheckPowerAndDisable);
                Step(CheckBlueprintNegatives);
                Step(CheckRenameRosterPanel);
                Step(CheckDamageTierSiegeDestroy);
                Step(CheckUplink);
                Step(CheckUplinkSupplyInput);
                Step(CheckSaveLoad);
                Step(CheckLoadContinuation);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckTrainingTarget);
                Step(CheckEngageReentry);
                Step(CheckSeedSet);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"炮塔自检抛异常：{e}");
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
                TurretService.ResetSessionState();
                TurretViews.Clear();
                TestRangeService.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
                FirmwareKinds.ResetForTests();
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
                TurretPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [炮塔（固定底盘）] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>一个有电的家园（开局发电机修好 + 两座发电机 2；按种子地形找空地，不写死坐标，B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 900)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            SignalCoreService.ResetForTests();
            TestRangeService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            SignalUplinkService.SilentNightProvider = null;
            _seq = 900;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (g.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "tu_gen", g.Value);
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：直接登记一座建成、满耐久的炮塔座（真实放置 / 机器施工由 B 段覆盖）。建筑 ID 按正式格式“类型#序号”。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c)
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
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, 0),
                Health = BuildingOps.MaxDurability(type),
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

        private static void Unregister(CampaignState s, BuildingRecord b)
        {
            s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
        }

        /// <summary>在核心附近按种子地形找空地登记一座炮塔（<paramref name="powered"/> = 接得上电网且吃到电；false = 故意放在电网外）。装 <paramref name="bp"/>（null = 默认）。</summary>
        private static BuildingRecord PlaceTurret(CampaignState s, string type, string bp = null, bool powered = true, float from = 5f, float to = 26f)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = Register(s, type, c);
                    HomeValleyPowerGrid.Recompute(s);
                    bool ok = powered
                        ? HomeValleyPowerGrid.IsConnected(s, b.BuildingId) && b.PowerState == BuildingPowerState.Powered
                        : !HomeValleyPowerGrid.IsConnected(s, b.BuildingId) && b.PowerState != BuildingPowerState.Powered;
                    if (!ok)
                    {
                        Unregister(s, b);
                        continue;
                    }
                    TurretService.Sync(s);
                    if (bp != null)
                    {
                        TurretOpResult r = TurretService.TryAssignBlueprint(s, b.BuildingId, bp);
                        if (!r.Ok)
                        {
                            Fail($"测试准备：给 {b.BuildingId} 装蓝图 {bp} 失败：{r.Message}");
                        }
                    }
                    TurretService.Sync(s);
                    return b;
                }
            }
            Fail($"测试准备：家园里找不到能放 {type}（{(powered ? "接得上电网" : "电网外")}）的空地");
            return null;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board, string name = null)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = name ?? id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static BlueprintCircuitBoard Fixed(string primary, params string[] firmware) =>
            BlueprintCircuitBoard.CreateDefault(CarrierReadings.FixedChassisId, primary, null, null, firmware ?? Array.Empty<string>());

        private static TurretRecord Rec(CampaignState s, BuildingRecord b) => b != null ? TurretService.Find(s, b.BuildingId) : null;

        private static int Unit(CampaignState s, BuildingRecord b)
        {
            TurretRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetTurretUnit(r.Serial, out int u) ? u : 0;
        }

        private static TurretUnitState UnitState(CampaignState s, BuildingRecord b)
        {
            TurretRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetTurretState(r.Serial, out TurretUnitState st) ? st : default;
        }

        private static CombatWeapon Weapon(CampaignState s, BuildingRecord b)
        {
            TurretRecord r = Rec(s, b);
            return r != null && Site != null && Site.TryGetTurretWeapon(r.Serial, out CombatWeapon w) ? w : default;
        }

        private static TurretReadout Readout(CampaignState s, BuildingRecord b)
        {
            TurretService.TryGetReadout(s, b.BuildingId, out TurretReadout ro);
            return ro;
        }

        /// <summary>一个敌方目标单位（不动、不开火；<paramref name="dps"/> &gt; 0 时带一件武器 = 威胁）。外部键 -1（原型单位，测试结束清场）。</summary>
        private static int Hostile(CombatSite site, Vector2 at, float hp, float maxHp, float dps = 0f, CombatUnitFlags extra = CombatUnitFlags.None)
        {
            int w = -1;
            if (dps > 0f)
            {
                w = site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 0.5f, Damage = dps, Cooldown = 1f });
            }
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath | extra,
                Position = new double2(at.x, at.y),
                Home = new double2(at.x, at.y),
                Radius = 0.5f,
                Speed = 0f,
                Health = hp,
                MaxHealth = maxHp,
                Weapon = w,
                BehaviorProfile = -1,
                Priority = 1,
            });
        }

        private static float Hp(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) && v.Alive ? v.Health : 0f;

        private static Vector2 Dir(float deg) => new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));

        private static void Clear(CombatSite site) => CombatBench.ClearPrototypeUnits(site);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        private static long Shots(CombatSite site) => site.Kernel.Counters.ShotsFired;

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static bool ContainsCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        /// <summary>与 Python repr(float) 对齐（表里的 float 按单精度往返写法）。</summary>
        private static string Fl(float v)
        {
            string t = v.ToString("R", CultureInfo.InvariantCulture);
            return t.Contains(".") || t.Contains("E") ? t : t + ".0";
        }

        private static int FluidOf(string firmwareId)
        {
            var needs = new List<TurretFluidNeed>(2);
            TurretCatalog.FluidNeeds(new[] { firmwareId }, needs);
            return needs.Count > 0 ? needs[0].FluidId : 0;
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            var t = ConfigSystem.Instance.Tables;
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] tp = lines.Where(l => l.StartsWith("TP\t", StringComparison.Ordinal)).ToArray();
            string[] tf = lines.Where(l => l.StartsWith("TF\t", StringComparison.Ordinal)).ToArray();
            string[] tm = lines.Where(l => l.StartsWith("TM\t", StringComparison.Ordinal)).ToArray();
            string[] rtp = t.TbTurretProfile.DataList.Select(r => string.Join("\t", "TP", r.ComponentId, r.Size, Fl(r.Range), Fl(r.TurnRate), Fl(r.ProjectileSpeed), Fl(r.ProjectileRadius))).ToArray();
            string[] rtf = t.TbTurretFluid.DataList.Select(r => string.Join("\t", "TF", r.FirmwareId, r.Fluid, Fl(r.LitersPerShot))).ToArray();
            string[] rtm = t.TbTurretTargetMode.DataList.Select(r => string.Join("\t", "TM", r.Id, r.Code.ToString(CultureInfo.InvariantCulture), r.NameKey, r.DescKey,
                r.SortOrder.ToString(CultureInfo.InvariantCulture))).ToArray();
            Expect(code == 0 && tp.Length == 13 && tp.SequenceEqual(rtp) && tf.Length == 9 && tf.SequenceEqual(rtf) && tm.Length == 5 && tm.SequenceEqual(rtm)
                   && TurretCatalog.Problems.Count == 0,
                $"A1 三张新表 fg.TbTurretProfile（{tp.Length} 行）/ fg.TbTurretFluid（{tf.Length} 行）/ fg.TbTurretTargetMode（{tm.Length} 行）与源数据 fgdata_defense 逐字段一致；目录无问题（{string.Join("；", TurretCatalog.Problems)}）"
                + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));

            bool modes = TurretCatalog.Modes.Count == 5 && TurretCatalog.Modes.Select(m => m.Code).OrderBy(c => c).SequenceEqual(new[] { 0, 1, 2, 3, 4 })
                         && Enum.GetValues(typeof(CombatTargetMode)).Length == 5 && (int)CombatTargetMode.SiegeFirst == 4 && TurretCatalog.Modes[0].Code == 0
                         && TurretCatalog.Modes.All(m => !string.IsNullOrEmpty(m.Name) && !GameText.ContainsMarker(m.Description));
            GameConfig.fg.BuildingGrid gl = GridContent.Building(L);
            GameConfig.fg.BuildingGrid gh = GridContent.Building(H);
            HomeValleyLayout.PowerProfile.TryGetValue(L, out (float PowerDemand, int PowerPriority) pl);
            HomeValleyLayout.PowerProfile.TryGetValue(H, out (float PowerDemand, int PowerPriority) ph);
            bool buildings = gl != null && gl.FootprintW == 2 && gl.FootprintH == 2 && gl.Category == "defense" && gh != null && gh.FootprintW == 3 && gh.FootprintH == 3
                             && gh.Category == "defense" && Mathf.Approximately(pl.PowerDemand, 8f) && Mathf.Approximately(ph.PowerDemand, 15f)
                             && Mathf.Approximately(BuildingOps.MaxDurability(L), 150f) && Mathf.Approximately(BuildingOps.MaxDurability(H), 260f)
                             && BuildingOps.MaxTier(L) == 3 && BuildingOps.MaxTier(H) == 3
                             && Mathf.Approximately(BuildingOps.TierRow(L, 2).Value, 1.15f) && Mathf.Approximately(BuildingOps.TierRow(H, 3).Value, 1.3f)
                             && Mathf.Approximately(TurretCatalog.TierArmor(1), 0f) && Mathf.Approximately(TurretCatalog.TierArmor(2), 0.2f) && Mathf.Approximately(TurretCatalog.TierArmor(3), 0.35f);
            bool research = ResearchCatalog.TryGet("defense.turret_t2", out ResearchNodeDef t2) && t2.IsReady && t2.Unlocks.Contains("tier:turret_light.t2") && t2.Unlocks.Contains("tier:turret_heavy.t2")
                            && ResearchCatalog.TryGet("defense.turret_t3", out ResearchNodeDef t3) && t3.IsReady && t3.Unlocks.Contains("tier:turret_heavy.t3")
                            && ResearchService.MigratedNodes().All(n => !n.Id.StartsWith("defense.turret", StringComparison.Ordinal))
                            && ResearchService.LegacyOpenNodes().All(n => !n.Id.StartsWith("defense.turret", StringComparison.Ordinal))
                            && ResearchCatalog.TryGet("logistics.heat_trace", out ResearchNodeDef later) && !later.IsReady; // FG6-DEF-03 防御分支已全部开放，“后续版本”改用伴热管（FG7-ENV-03）
            Expect(modes && buildings && research && CombatConst.FormatVersion >= 9,
                $"A2 五种目标模式（code 与内核 CombatTargetMode 一一对应，按 FGR-DEF-003 列举排序）；轻型 2×2 / 重型 3×3 炮塔座（防御页签、耗电 8 / 15、耐久 150 / 260、T1～T3 射程 ×1 / 1.15 / 1.3、减伤 0 / 20% / 35%）；" +
                $"研发 defense.turret_t2 / t3 已就绪并解锁炮塔座等级（旧档迁移与开局技术数据换算不计；“后续版本”的对照改用伴热管）；战斗内核快照格式 {CombatConst.FormatVersion}（{modes}/{buildings}/{research}）");

            string[] keys = t.TbLocText.DataList.Select(r => r.Key).Where(k => k.StartsWith("turret.", StringComparison.Ordinal) || k.StartsWith("bs.reason.turret_", StringComparison.Ordinal)
                || k.StartsWith("roster.turret.", StringComparison.Ordinal) || k.StartsWith("roster.tab.turrets", StringComparison.Ordinal) || k.StartsWith("build.turret.", StringComparison.Ordinal)
                || k.StartsWith("codex.defense.turret", StringComparison.Ordinal) || k.StartsWith("building.turret_", StringComparison.Ordinal) || k == "reaction.log.turret_named"
                || k.StartsWith("combat.dummy.", StringComparison.Ordinal)).ToArray();
            bool zh = keys.All(k => !GameText.ContainsMarker(GameText.Get(k)) && GameText.Get(k).Length > 0);
            GameSettings.SetLanguage(GameLanguage.En);
            string[] badEn = keys.Where(k => GameText.ContainsMarker(GameText.Get(k)) || GameText.Get(k).Length == 0 || ContainsCjk(GameText.Get(k))).ToArray();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = new[] { GuidanceHooks.TurretFirstBuilt, GuidanceHooks.TurretFirstOpen, GuidanceHooks.TurretFirstMode, GuidanceHooks.TurretFirstUplink,
                GuidanceHooks.TurretFirstSupplyShort, GuidanceHooks.TurretFirstKill }.All(h => GuidanceHooks.Known.Contains(h));
            bool codex = t.TbCodexEntry.DataList.Any(c => c.Id == "codex.defense.turret") && BuildingOps.Service(L)?.CodexId == "codex.defense.turret";
            bool notify = NotificationCatalog.TryGetType("turret_supply", out NotifyTypeDef nt) && nt.Tier == NotifyLevel.Warning;
            bool chassis = ChassisCatalog.All.TryGetValue(CarrierReadings.FixedChassisId, out MechanicalContentDef fixedDef) && fixedDef.Source == MechanicalContentSource.BaseBlueprint;
            Expect(keys.Length >= 90 && zh && badEn.Length == 0 && hooks && codex && notify && chassis,
                $"A3 新文本 {keys.Length} 个中英都有（英文里有中文 / 缺的：{string.Join("、", badEn.Take(6))}）；6 个引导钩子；图鉴“炮塔”（建筑面板“?”指向它）；“炮塔缺补给”（警告）通知；固定底盘进基础蓝图库（{hooks}/{codex}/{notify}/{chassis}）");
        }

        // ── B 正式放置与施工 ───────────────────────────────────────────────────────

        private static void CheckPlacementAndBuild()
        {
            CampaignState s = NewWorld(6101, scrap: 900);
            CombatSite site = Site;
            BlueprintRecord light = BlueprintEditorService.Find(s, BpLight);
            bool seededLight = light != null && light.Versions[0].ChassisId == CarrierReadings.FixedChassisId && light.Versions[0].PrimaryId == ComponentCatalog.CompGunId
                               && TurretService.StateOf(s).DefaultsSeeded;
            bool heavyLocked = !MechanicalContentUnlock.IsUnlocked(s, ComponentCatalog.CompCannonId) && BlueprintEditorService.Find(s, BpHeavy) == null;
            GridCell? heavySpot = FgProductionSelfCheck.FindFree(s, H, 7f, 22f);
            GridPlacementResult heavyRefused = heavySpot.HasValue ? HomeGridService.ValidatePlacement(s, H, heavySpot.Value, 0, asPlayerPlacement: true, checkCost: false) : null;
            bool refused = heavyRefused != null && !heavyRefused.Ok && heavyRefused.Has(GridBlockReason.TurretNoBlueprint) && heavyRefused.Describe().Contains("重型")
                           && heavyRefused.Describe().Contains("固定底盘");
            Expect(seededLight && heavyLocked && refused,
                $"B1 新档蓝图库里补了默认轻型炮塔蓝图（固定底盘 + 连射器 + 寻的，开局就能放炮塔）；铸造重炮还没解锁时不送默认重型蓝图，放重型炮塔座被拒并写明怎么办（“{heavyRefused?.Describe()}”）");

            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            TurretService.Sync(s);
            bool heavySeeded = BlueprintEditorService.Find(s, BpHeavy) != null && TurretService.StateOf(s).HeavyDefaultSeeded
                               && heavySpot.HasValue && HomeGridService.ValidatePlacement(s, H, heavySpot.Value, 0, asPlayerPlacement: true, checkCost: false).Ok;
            int bpCount = s.BlueprintRecords.Length;
            TurretService.Sync(s);
            Expect(heavySeeded && s.BlueprintRecords.Length == bpCount, "B2 重炮解锁后默认重型炮塔蓝图补进蓝图库（只补一次），重型炮塔座可以放了");

            // 建造模式真实入口：选中轻型炮塔 → 建造栏出现“炮塔蓝图”下拉与射程 → 鼠标悬停 → 地面射程圈 = 选中蓝图的射程。
            AddBlueprint(s, BpBeam, Fixed(ComponentCatalog.CompBeamId), "切割束炮塔");
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GameObject hudGo = null;
            var rig = new GameObject("__fgturret_views") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // 挂到真实面板上（下拉框的选中回调只在面板里派发，与玩家点选同一路径）。
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "BuildModeHud.uxml", out hudGo);
                BuildModeHudUIToolkit hud = hudGo.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                mode.Open();
                mode.Select(L);
                hud.Refresh();
                bool hudShown = mode.SelectedTypeId == L && hud.TurretBlueprintVisible && hud.TurretBlueprintChoices.Count == 2 && hud.TurretBlueprintChoices[0].Contains("基础炮塔")
                                && hud.TurretRangeText.Contains("18");
                GridCell? at = FgProductionSelfCheck.FindFree(s, L, 7f, 18f);
                if (!at.HasValue)
                {
                    Fail("B3 核心附近没有能放轻型炮塔的空地");
                    return;
                }
                mode.SetHover(s, at.Value);
                TurretViews.FrameUpdate(s, site, rig.transform, mode);
                bool ring18 = TurretViews.RingShown && Mathf.Approximately(TurretViews.RingRadius, 18f);
                int beam = hud.TurretBlueprintChoices.ToList().FindIndex(c => c.Contains("切割束"));
                hud.SelectTurretBlueprint(beam);
                hud.Refresh();
                TurretViews.FrameUpdate(s, site, rig.transform, mode);
                bool ring14 = TurretService.StateOf(s).PlacementLight == BpBeam && TurretViews.RingShown && Mathf.Approximately(TurretViews.RingRadius, 14f) && hud.TurretRangeText.Contains("14");
                Expect(hudShown && ring18 && ring14,
                    $"B3 放置时预览射程：建造栏选中轻型炮塔出现“炮塔蓝图”下拉（{string.Join(" / ", hud.TurretBlueprintChoices)}）与“{hud.TurretRangeText}”；悬停时地面射程圈半径 18 米（{ring18}）；" +
                    $"下拉选中即生效换成切割束炮塔 → 射程圈 14 米（{ring14}）");

                int scrap0 = s.Scrap;
                GridOpResult placed = mode.ClickCell(s, at.Value);
                BuildingRecord ghost = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
                TurretService.Sync(s);
                TurretRecord gr = ghost != null ? TurretService.Find(s, ghost.BuildingId) : null;
                bool ghostOk = ghost != null && ghost.BuildingTypeId == L && HomeValleyController.IsPlannedGhost(ghost) && gr != null && gr.BlueprintId == BpBeam
                               && !site.TryGetTurretUnit(gr.Serial, out _) && BuildingStatusService.Evaluate(s, ghost).Kind == BuildingStatusKind.Building;
                Expect(ghostOk, $"B4 左键放下炮塔虚影（施工前不进战斗内核），记下建造栏选的蓝图（{gr?.BlueprintId}）；状态“{(ghost != null ? BuildingStatusService.Evaluate(s, ghost).Reason : "无")}”（{placed.Describe()}）");
                mode.Close();
                if (ghost == null)
                {
                    return;
                }
                bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 300);
                Seconds(1f);
                TurretReadout ro = Readout(s, ghost);
                bool inKernel = built && ro.HasUnit && ro.Valid && Mathf.Approximately(ro.Range, 14f) && UnitState(s, ghost).WeaponEnabled && ghost.InvestedScrap >= 50
                                && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstBuilt) && ro.Status.Kind == BuildingStatusKind.Idle && ro.Status.Reason.Contains("射程 14 米");
                Expect(inKernel, $"B5 机器运来 50 废料施工完工（像建筑一样：投入 {ghost.InvestedScrap}，废料 {scrap0}→{s.Scrap}）；建成后进家园战斗内核（射程 {ro.Range} 米、能开火、“首次建成”钩子），状态“{ro.Status.Reason}”");

                // DEBT-FG2VFX02-03：炮塔头挂机器同一套形变部件（MachineMorphView），按生效固件的类别展开；换成带冷却液（流体类）的蓝图后展开对应那组。
                TurretViews.FrameUpdate(s, site, rig.transform, null);
                bool head = TurretViews.TryGetHead(ghost.BuildingId, out Transform ht) && ht != null && TurretViews.PartCountOf(ghost.BuildingId) > 0;
                TurretService.TryGetMorph(s, ghost.BuildingId, out _, out _, out MorphMask m0);
                MorphMask shown0 = TurretViews.MaskOf(ghost.BuildingId);
                AddBlueprint(s, BpWet, Fixed(ComponentCatalog.CompGunId, "fw_coolant"), "冷却炮塔");
                TurretOpResult swap = TurretService.TryAssignBlueprint(s, ghost.BuildingId, BpWet);
                TurretViews.FrameUpdate(s, site, rig.transform, null);
                TurretService.TryGetMorph(s, ghost.BuildingId, out string prim1, out _, out MorphMask m1);
                MorphMask shown1 = TurretViews.MaskOf(ghost.BuildingId);
                bool morph = head && shown0 == m0 && swap.Ok && prim1 == ComponentCatalog.CompGunId && shown1 == m1 && m1 != m0 && m1 != MorphMask.None
                             && TurretViews.PartCountOf(ghost.BuildingId) > 0 && TurretViews.HeadCount >= 1;
                Expect(morph, $"B6 炮塔的机身状态：炮塔头（占位圆顶 + 炮管）挂机器同一套形变部件（{TurretViews.PartCountOf(ghost.BuildingId)} 件），类别集合 {shown0}；换成冷却炮塔蓝图后部件按连射器重建、展开 {shown1}（与炮塔生效固件一致）");
            }
            finally
            {
                if (hudGo != null)
                {
                    Object.DestroyImmediate(hudGo);
                }
                TurretViews.Clear();
                Object.DestroyImmediate(rig);
                mode?.Close();
            }
        }

        // ── C 射程 / 转速 / 弹体 / 击毁数 ─────────────────────────────────────────────

        private static void CheckWeaponAndKernel()
        {
            CampaignState s = NewWorld(6102);
            CombatSite site = Site;
            BuildingRecord t = PlaceTurret(s, L);
            if (t == null)
            {
                return;
            }
            TurretRecord r = Rec(s, t);
            TurretReadout ro = Readout(s, t);
            CombatWeapon w = Weapon(s, t);
            site.Kernel.TryGetUnit(Unit(s, t), out CombatUnitView v);
            bool weapon = ro.Valid && ro.HasUnit && r.BlueprintId == BpLight && Mathf.Approximately(ro.Range, 18f) && Mathf.Approximately(ro.TurnRate, 180f) && ro.Projectile
                          && w.Mode == CombatWeaponMode.Projectile && Mathf.Approximately(w.ProjectileSpeed, 30f) && Mathf.Approximately(w.Range, 18f) && Mathf.Approximately(w.TurnRate, 180f)
                          && w.TargetMode == CombatTargetMode.Nearest && w.AmmoPerShot == 0f && v.Kind == CombatUnitKind.Turret && v.Faction == CombatFaction.Player
                          && v.Behavior == CombatBehavior.HoldFire && v.ExtKey == r.Serial && (v.Flags & CombatUnitFlags.CountKills) != 0 && (v.Flags & CombatUnitFlags.ReportDeath) != 0;
            Expect(weapon, $"C1 炮塔 = 家园战斗内核里一个固定底盘单位（炮塔种类、驻守开火、外部键 = 炮塔序号 {r.Serial}）；武器与机器同一个翻译 + 炮塔参数：射程 {w.Range} 米、转速 {w.TurnRate}°/秒、" +
                           $"真实弹体（弹速 {w.ProjectileSpeed}）、目标模式“最近”、只用电（每发补给 {w.AmmoPerShot}）");

            // 转向：目标在炮口正后方，先转（180°/秒 → 约 1 秒）再开火；转完炮口对着目标。
            Vector2 c = t.Position;
            int e = Hostile(site, c + new Vector2(0f, -10f), 5000f, 5000f);
            long shots0 = Shots(site);
            Seconds(0.5f);
            TurretUnitState half = UnitState(s, t);
            float turned = Vector2.Angle(Vector2.up, half.Facing);
            bool notYet = Shots(site) == shots0 && turned > 60f && turned < 120f;
            int maxProj = 0;
            for (int i = 0; i < GameClock.StepHz * 2; i++)
            {
                WorldSimulation.StepMany(1);
                maxProj = Math.Max(maxProj, site.Kernel.ProjectileCount);
            }
            TurretUnitState aimed = UnitState(s, t);
            float off = Vector2.Angle(aimed.Facing, (c + new Vector2(0f, -10f)) - c);
            bool fired = Shots(site) > shots0 && off <= TurretCatalog.AimToleranceDeg + 0.5f && maxProj > 0 && Hp(site, e) < 5000f;
            Expect(notYet && fired,
                $"C2 FGR-DEF-002 转速：目标在正后方，0.5 秒时炮口转了 {turned:F0}°、还没开火（{notYet}）；转到位（偏差 {off:F1}° ≤ 容差 {TurretCatalog.AimToleranceDeg}°）后开火，" +
                $"内核里有飞行中的弹体（最多 {maxProj} 枚），目标耐久 {Hp(site, e):F0}/5000（{fired}）");

            // FGR-DEF-002“己方屏障不阻挡己方炮塔的弹道”：炮口与目标之间站着一个己方结构单位，弹体穿过它打到敌人（己方不掉血）。
            Clear(site);
            int foe = Hostile(site, c + new Vector2(0f, -12f), 5000f, 5000f);
            int wall = site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Structure,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
                Position = new double2(c.x, c.y - 6f),
                Home = new double2(c.x, c.y - 6f),
                Radius = 1f,
                Health = 100f,
                MaxHealth = 100f,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            });
            Seconds(3f);
            Expect(Hp(site, foe) < 5000f && Mathf.Approximately(Hp(site, wall), 100f),
                $"C2b 己方挡在弹道上不挡己方炮塔：炮口与敌人之间站着己方结构单位，弹体穿过去（敌人耐久 {Hp(site, foe):F0}/5000，己方 {Hp(site, wall):F0}/100）；屏障建筑本身在 FG6-DEF-02 按同一规则复验");

            // 击毁数：近处 3 个脆弱目标（其中 1 个精英）被打掉 → 击毁 3、精英 1；全局累计；“首次击毁”钩子。
            Clear(site);
            int k0 = r.KillCount;
            int total0 = TurretService.StateOf(s).TotalKills;
            Hostile(site, c + Dir(30f) * 5f, 6f, 6f);
            Hostile(site, c + Dir(150f) * 6f, 6f, 6f);
            Hostile(site, c + Dir(270f) * 7f, 6f, 6f, 0f, CombatUnitFlags.Elite);
            bool killed = StepUntil(() => site.Kernel.CountAlive(CombatFaction.Hostile) == 0, 20);
            Seconds(0.5f);
            TurretReadout ro2 = Readout(s, t);
            Expect(killed && r.KillCount == k0 + 3 && r.EliteKills == 1 && ro2.Kills == r.KillCount && TurretService.StateOf(s).TotalKills == total0 + 3
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstKill) && ro2.Status.Reason.Contains("击毁 " + r.KillCount),
                $"C3 炮塔记击毁数：打掉 3 个（精英 1）→ 击毁 {r.KillCount}（精英 / 首领 {r.EliteKills}），全局累计 {TurretService.StateOf(s).TotalKills}；状态“{ro2.Status.Reason}”");

            // 结构：炮塔的热更层代码不碰战斗内核、不另起弹道（射击走 CombatSite 门面 → 内核）。
            string dir = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Campaign/Defense");
            var offenders = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*.cs"))
            {
                string[] src = File.ReadAllLines(f);
                for (int i = 0; i < src.Length; i++)
                {
                    string ln = src[i];
                    if (ln.TrimStart().StartsWith("//", StringComparison.Ordinal) || ln.TrimStart().StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if ((Regex.IsMatch(ln, @"\.Kernel\b") && !ln.Contains("PipeNetworkService.Kernel")) || Regex.IsMatch(ln, @"\b(SpawnProjectile|ApplyDamage|Raycast)\b") && !ln.Contains("Plane("))
                    {
                        offenders.Add(Path.GetFileName(f) + ":" + (i + 1));
                    }
                }
            }
            Expect(offenders.Count == 0, $"C4 炮塔热更层（Campaign/Defense）不直接碰战斗内核、不另起弹道 / 伤害（命中 {offenders.Count} 处{(offenders.Count > 0 ? "：" + string.Join("，", offenders.Take(8)) : string.Empty)}）");
        }

        // ── D 五种目标模式 ─────────────────────────────────────────────────────────

        private static int Expected(CombatSite site, int turretUnit, List<int> units, CombatTargetMode mode, float range)
        {
            site.Kernel.TryGetUnit(turretUnit, out CombatUnitView tv);
            int best = 0;
            double bestDist = double.MaxValue;
            float bestKey = float.MaxValue;
            foreach (int u in units)
            {
                if (!site.Kernel.TryGetUnit(u, out CombatUnitView v) || !v.Alive)
                {
                    continue;
                }
                double dist = math.distance(tv.Position, v.Position);
                if (dist > range)
                {
                    continue;
                }
                float key;
                switch (mode)
                {
                    case CombatTargetMode.LowestHealth: key = v.MaxHealth > 0f ? v.Health / v.MaxHealth : 1f; break;
                    case CombatTargetMode.HighestThreat: key = -site.ThreatOfUnit(u); break;
                    case CombatTargetMode.EliteFirst: key = (v.Flags & CombatUnitFlags.Elite) != 0 ? 0f : 1f; break;
                    case CombatTargetMode.SiegeFirst: key = (v.Flags & CombatUnitFlags.SiegeAttack) != 0 ? 0f : 1f; break;
                    default: key = 0f; break;
                }
                if (key < bestKey || (key == bestKey && dist < bestDist))
                {
                    bestKey = key;
                    bestDist = dist;
                    best = u;
                }
            }
            return best;
        }

        private static void CheckTargetModes()
        {
            CampaignState s = NewWorld(6103);
            CombatSite site = Site;
            BuildingRecord t = PlaceTurret(s, L);
            if (t == null)
            {
                return;
            }
            TurretRecord r = Rec(s, t);
            Vector2 c = t.Position;
            // 定点布置：每种模式各有唯一正确答案（不同方位，弹道互不遮挡）；射程外放一个“什么都占”的诱饵。
            int a = Hostile(site, c + Dir(0f) * 4f, 100f, 100f);
            int b = Hostile(site, c + Dir(72f) * 10f, 10f, 100f);
            int th = Hostile(site, c + Dir(144f) * 12f, 100f, 100f, 50f);
            int el = Hostile(site, c + Dir(216f) * 14f, 100f, 100f, 0f, CombatUnitFlags.Elite);
            int sg = Hostile(site, c + Dir(288f) * 15f, 100f, 100f);
            site.Kernel.SetFlag(sg, CombatUnitFlags.SiegeAttack, true);
            int decoy = Hostile(site, c + Dir(330f) * 25f, 1f, 100f, 90f, CombatUnitFlags.Elite);
            site.Kernel.SetFlag(decoy, CombatUnitFlags.SiegeAttack, true);
            var expect = new Dictionary<int, int> { [0] = a, [1] = b, [2] = th, [3] = el, [4] = sg };
            var got = new List<string>();
            bool all = true;
            foreach (KeyValuePair<int, int> kv in expect)
            {
                TurretOpResult set = TurretService.TrySetMode(s, t.BuildingId, kv.Key);
                int pick = site.TurretTargetUnit(r.Serial);
                bool ok = set.Ok && r.TargetMode == kv.Key && Weapon(s, t).TargetMode == (CombatTargetMode)kv.Key && pick == kv.Value;
                all &= ok;
                got.Add($"{TurretCatalog.ModeName(kv.Key)}→{(pick == a ? "最近" : pick == b ? "残血" : pick == th ? "高威胁" : pick == el ? "精英" : pick == sg ? "拆建筑" : pick == decoy ? "射程外诱饵" : pick.ToString())}");
            }
            Expect(all && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstMode),
                $"D1 FGR-DEF-003 五种目标模式各选中唯一正确的目标（{string.Join("，", got)}）；射程外的诱饵（精英 + 拆建筑 + 残血 + 高威胁）从不被选；改模式立即写进内核武器参数");

            // 真打：最低耐久模式下第一发打在残血目标上，最近的目标不掉血。
            TurretService.TrySetMode(s, t.BuildingId, (int)CombatTargetMode.LowestHealth);
            long shots0 = Shots(site);
            bool hitB = StepUntil(() => Hp(site, b) < 10f, 10);
            Expect(hitB && Mathf.Approximately(Hp(site, a), 100f) && Mathf.Approximately(Hp(site, th), 100f) && Shots(site) > shots0,
                $"D2 内核按模式真打：最低耐久模式下残血目标耐久 10→{Hp(site, b):F1}，最近的目标仍 {Hp(site, a):F0}（不做额外判断）");

            // 随机布置 × 5 种模式，与独立的暴力扫描逐个对照。
            var rng = new System.Random(6103);
            int layouts = 0;
            int compared = 0;
            var mismatches = new List<string>();
            float range = Weapon(s, t).Range;
            for (int lay = 0; lay < 20; lay++)
            {
                Clear(site);
                var units = new List<int>();
                for (int i = 0; i < 30; i++)
                {
                    float dist = 2f + (float)rng.NextDouble() * 22f;
                    float ang = (float)rng.NextDouble() * 360f;
                    float max = 50f + (float)rng.NextDouble() * 150f;
                    float hp = max * (rng.Next(4) == 0 ? 1f : 0.05f + (float)rng.NextDouble() * 0.95f);
                    float dps = rng.Next(3) == 0 ? 0f : 1f + (float)rng.NextDouble() * 60f;
                    CombatUnitFlags flags = rng.Next(6) == 0 ? CombatUnitFlags.Elite : CombatUnitFlags.None;
                    int u = Hostile(site, c + Dir(ang) * dist, hp, max, dps, flags);
                    if (rng.Next(5) == 0)
                    {
                        site.Kernel.SetFlag(u, CombatUnitFlags.SiegeAttack, true);
                    }
                    units.Add(u);
                }
                layouts++;
                for (int m = 0; m < 5; m++)
                {
                    TurretService.TrySetMode(s, t.BuildingId, m);
                    int want = Expected(site, Unit(s, t), units, (CombatTargetMode)m, range);
                    int pick = site.TurretTargetUnit(r.Serial);
                    compared++;
                    if (want != pick)
                    {
                        mismatches.Add($"布置 {lay} 模式 {m}：暴力 {want} / 内核 {pick}");
                    }
                }
            }
            TurretOpResult unknown = TurretService.TrySetMode(s, t.BuildingId, 9);
            TurretOpResult missing = TurretService.TrySetMode(s, "home:no_such_turret", 1);
            Expect(mismatches.Count == 0 && compared == 100 && !unknown.Ok && unknown.Failure == TurretFailure.ModeUnknown && !missing.Ok && missing.Failure == TurretFailure.NotFound,
                $"D3 随机布置 {layouts} 组 × 5 种模式（每组 30 个敌人，部分在射程外、随机耐久 / 威胁 / 精英 / 拆建筑标记）：内核选的目标与独立暴力扫描逐个一致（{compared - mismatches.Count}/{compared}）" +
                $"{(mismatches.Count > 0 ? "：" + string.Join("；", mismatches.Take(5)) : string.Empty)}；负向：不存在的模式 / 炮塔被拒（“{unknown.Message}” / “{missing.Message}”）");
            Clear(site);
        }

        // ── D' 批量设置与复制设置 ──────────────────────────────────────────────────

        private static void CheckBatchAndCopy()
        {
            CampaignState s = NewWorld(6104);
            AddBlueprint(s, BpBeam, Fixed(ComponentCatalog.CompBeamId), "切割束炮塔");
            BuildingRecord t1 = PlaceTurret(s, L);
            BuildingRecord t2 = PlaceTurret(s, L);
            BuildingRecord t3 = PlaceTurret(s, L, BpBeam);
            if (t1 == null || t2 == null || t3 == null)
            {
                return;
            }
            TurretService.TrySetMode(s, t1.BuildingId, (int)CombatTargetMode.EliteFirst);
            TurretOpResult same = TurretService.TrySetModeBatch(s, (int)CombatTargetMode.EliteFirst, t1.BuildingId);
            bool sameOk = same.Ok && same.Count == 1 && Rec(s, t2).TargetMode == 3 && Rec(s, t3).TargetMode == 0 && Weapon(s, t2).TargetMode == CombatTargetMode.EliteFirst
                          && same.Message.Contains("1 座");
            TurretOpResult allR = TurretService.TrySetModeBatch(s, (int)CombatTargetMode.SiegeFirst);
            bool allOk = allR.Ok && allR.Count == 3 && TurretService.All(s).All(x => x.TargetMode == 4) && Weapon(s, t3).TargetMode == CombatTargetMode.SiegeFirst;
            TurretOpResult none = TurretService.TrySetModeBatch(s, (int)CombatTargetMode.SiegeFirst);
            Expect(sameOk && allOk && none.Ok && none.Count == 0 && none.Message.Contains("没有其它"),
                $"D4 批量设置目标模式：“同蓝图的炮塔”只改装同一张蓝图的（“{same.Message}”）；“全部炮塔”改 3 座（“{allR.Message}”）；都已经是这个模式时说明没有要改的（“{none.Message}”）");

            // 复制设置（吸管 / 复制设置 / 布局粘贴同一编码）：蓝图 + 目标模式。
            PlanSettings.BuildingSettings(s, t3, out int s0, out int s1, out int s2);
            string desc = PlanSettings.Describe(PlanSettings.Family.Power, s0, s1, s2);
            TurretService.TrySetMode(s, t1.BuildingId, 0);
            bool applied = PlanSettings.ApplyBuildingSettings(s, t1, s0, s1, s2, out bool recipeApplied);
            bool copyOk = applied && recipeApplied && Rec(s, t1).BlueprintId == BpBeam && Rec(s, t1).TargetMode == 4 && Mathf.Approximately(Readout(s, t1).Range, 14f)
                          && desc.Contains("切割束炮塔") && desc.Contains("先打拆建筑的");
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            TurretService.Sync(s);
            BuildingRecord heavy = PlaceTurret(s, H);
            bool heavyKeeps = heavy != null && PlanSettings.ApplyBuildingSettings(s, heavy, s0, s1, s2, out _) && Rec(s, heavy).BlueprintId == BpHeavy && Rec(s, heavy).TargetMode == 4;
            GridCell? spot = FgProductionSelfCheck.FindFree(s, L, 6f, 24f);
            GridOpResult ghostPlace = spot.HasValue ? PlanHistory.Place(s, L, spot.Value, 0, s0, s1, s2) : default;
            BuildingRecord ghost = ghostPlace.Success ? HomeGridService.FindBuilding(s, ghostPlace.BuildingId) : null;
            TurretRecord gr = ghost != null ? TurretService.Find(s, ghost.BuildingId) : null;
            Expect(copyOk && heavyKeeps && gr != null && gr.BlueprintId == BpBeam && gr.TargetMode == 4,
                $"D5 复制炮塔设置：说明“{desc}”；粘到另一座轻型炮塔 → 蓝图与模式都换（射程 {Readout(s, t1).Range} 米）；粘到重型炮塔座只换模式、蓝图保留（占地不同）（{heavyKeeps}）；" +
                $"带设置放下的虚影直接记下蓝图与模式（{gr?.BlueprintId} / {gr?.TargetMode}）");
        }

        // ── E 补给 ─────────────────────────────────────────────────────────────────

        /// <summary>一块 6×4 空地：炮塔放在中间偏东，西边留两格铺管线（第一格挨着炮塔、第二格挂测试供给点）。要接得上电网。</summary>
        private static BuildingRecord TurretWithPipeRoom(CampaignState s, string bp, out GridCell near, out GridCell far)
        {
            near = default;
            far = default;
            for (float d = 8f; d <= 22f; d += 1f)
            {
                GridCell? o = FgProductionSelfCheck.FindArea(s, 6, 4, d, d);
                if (!o.HasValue)
                {
                    continue;
                }
                GridCell pivot = FgProductionSelfCheck.At(o.Value, 3, 1);
                if (!HomeGridService.ValidatePlacement(s, L, pivot, 0, asPlayerPlacement: false, checkCost: false).Ok)
                {
                    continue;
                }
                BuildingRecord b = Register(s, L, pivot);
                HomeValleyPowerGrid.Recompute(s);
                if (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered)
                {
                    Unregister(s, b);
                    continue;
                }
                var cells = new List<GridCell>();
                HomeGridService.FootprintOf(b, cells);
                int minX = cells.Min(x => x.X);
                int y = cells.Where(x => x.X == minX).Min(x => x.Y);
                near = new GridCell(minX - 1, y);
                far = new GridCell(minX - 2, y);
                if (!HomeGridService.ValidatePipeCell(s, near, PipePieceKind.Pipe).Ok || !HomeGridService.ValidatePipeCell(s, far, PipePieceKind.Pipe).Ok)
                {
                    Unregister(s, b);
                    continue;
                }
                TurretService.Sync(s);
                if (bp != null)
                {
                    TurretService.TryAssignBlueprint(s, b.BuildingId, bp);
                }
                TurretService.Sync(s);
                return b;
            }
            Fail("测试准备：找不到能放炮塔并在旁边铺管线的空地");
            return null;
        }

        private static void CheckSupply()
        {
            CampaignState s = NewWorld(6105, scrap: 2000);
            CombatSite site = Site;
            AddBlueprint(s, BpWet, Fixed(ComponentCatalog.CompGunId, "fw_coolant"), "冷却炮塔");
            int coolant = FluidOf("fw_coolant");
            int water = FluidOf("fw_trail");
            // 只用电的炮塔：补给一行写“只用电”。
            BuildingRecord dry = PlaceTurret(s, L);
            bool powerOnly = dry != null && Readout(s, dry).SupplyLine.Contains("只用电") && !Readout(s, dry).SupplyShort;
            string dryLine = dry != null ? Readout(s, dry).SupplyLine : string.Empty;
            if (dry != null)
            {
                Unregister(s, dry); // 下面数开火次数只数补给炮塔自己的（只用电的炮塔不留在场上）
                TurretService.Sync(s);
            }

            // 没接管线：停火，原因写“没接管线：接一条装着冷却液的管线”，发一条可定位的通知。
            int notes0 = NotifyCount("turret_supply");
            BuildingRecord wet = TurretWithPipeRoom(s, BpWet, out GridCell near, out GridCell far);
            if (wet == null)
            {
                return;
            }
            Vector2 c = wet.Position;
            int target = Hostile(site, c + Dir(200f) * 8f, 100000f, 100000f);
            long shots0 = Shots(site);
            Seconds(3f);
            BuildingStatus st = BuildingStatusService.Evaluate(s, wet);
            CombatWeapon w = Weapon(s, wet);
            bool reasonOk = st.Kind == BuildingStatusKind.NoFluid && st.ReasonCode == "turret.no_supply.nopipe" && st.Reason.Contains("没接管线") && st.Reason.Contains(PipeNetworkService.FluidName(coolant));
            bool held = Shots(site) == shots0 && Mathf.Approximately(w.AmmoPerShot, 1f) && UnitState(s, wet).Ammo < 1f;
            bool told = NotifyCount("turret_supply") > notes0 && LastNotify("turret_supply").Contains(PipeNetworkService.FluidName(coolant))
                        && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstSupplyShort) && Readout(s, wet).SupplyShort;
            bool noPipe = reasonOk && held && told;
            Line($"    · E1 分项：只用电 {powerOnly} / 原因 {reasonOk}（{st.Kind} {st.ReasonCode}）/ 停火 {held}（每发 {w.AmmoPerShot}、内核里 {UnitState(s, wet).Ammo}）/ 通知与钩子 {told}");
            Expect(powerOnly && noPipe,
                $"E1 FGR-DEF-004：只用电的炮塔写“{dryLine}”；装了冷却液固件的炮塔每发要 2 升冷却液——没接管线时停火（开火 {Shots(site) - shots0} 次），" +
                $"状态“{st.Reason}”，通知“{LastNotify("turret_supply")}”，首次缺补给钩子");

            // E6（审查 P2：B08 通知节流原来只有实现没有断言）：一直缺补给不重复发；补给来了一发又断（还在 30 秒冷却里）也不再发；冷却过后再断才再发一条。
            int wetSerial = Rec(s, wet).Serial;
            int n1 = NotifyCount("turret_supply");
            Seconds(8f);
            bool steady = NotifyCount("turret_supply") == n1 && Readout(s, wet).SupplyShort;
            // 测试捷径：装进一发（管线装填由 E3 / E4 覆盖）。先撤掉目标，让对账看到“不缺了”，再放回目标——打完这一发又断。
            Clear(site);
            site.SetTurretAmmo(wetSerial, 1f);
            Seconds(1f);
            bool recovered = !Readout(s, wet).SupplyShort;
            long s5 = Shots(site);
            target = Hostile(site, c + Dir(200f) * 8f, 100000f, 100000f);
            Seconds(2f);
            bool toggled = recovered && Shots(site) - s5 == 1 && Readout(s, wet).SupplyShort;
            bool cooled = NotifyCount("turret_supply") == n1;
            Seconds(TurretCatalog.NotifyCooldownSeconds + 1f);
            bool stillQuiet = NotifyCount("turret_supply") == n1; // 冷却过了但一直在缺：不重复发
            Clear(site);
            site.SetTurretAmmo(wetSerial, 1f);
            Seconds(1f);
            target = Hostile(site, c + Dir(200f) * 8f, 100000f, 100000f);
            Seconds(2f);
            bool again = NotifyCount("turret_supply") == n1 + 1;
            Expect(steady && toggled && cooled && stillQuiet && again,
                $"E6 B08 通知节流：持续缺补给 8 秒不重复发（{steady}）；装进一发打完又断、还在 {TurretCatalog.NotifyCooldownSeconds:0} 秒冷却里不发（开火 {Shots(site) - s5} / {toggled} / {cooled}）；" +
                $"冷却过后一直缺也不发（{stillQuiet}）；冷却过后再断一次才再发一条（{again}，累计 {NotifyCount("turret_supply") - n1} 条）");

            // 错流体：另一座同样的炮塔边上接一条水管。
            BuildingRecord wrong = TurretWithPipeRoom(s, BpWet, out GridCell wNear, out GridCell wFar);
            bool wrongOk = false;
            string wrongReason = string.Empty;
            if (wrong != null)
            {
                bool pipes = PipeNetworkService.TryPlace(s, wNear, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, wFar, PipePieceKind.Pipe, 0, 0).Ok;
                int feedW = PipeNetworkService.Kernel.AddProducer(wFar.X, wFar.Y, water, 50000, 50000);
                Seconds(3f);
                BuildingStatus ws = BuildingStatusService.Evaluate(s, wrong);
                wrongReason = ws.Reason;
                wrongOk = pipes && feedW >= 0 && ws.Kind == BuildingStatusKind.NoFluid && ws.ReasonCode == "turret.no_supply.wrongfluid" && ws.Reason.Contains(PipeNetworkService.FluidName(water));
                PipeNetworkService.Kernel.RemoveProducer(feedW, out _);
            }
            Expect(wrongOk, $"E2 边上的管线里是别的流体：停火并写明“{wrongReason}”");

            // 接上冷却液：消费者挂上、按整发装进内核 → 开火；流体逐毫升守恒（供给 = 剩余 + 炮塔存着 + 内核里的整发 + 打出去的发数 × 2 升）。
            bool placed = PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0).Ok;
            const long Supply = 60000;
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, coolant, Supply, Supply);
            long fired0 = Shots(site);
            bool firing = StepUntil(() => Shots(site) > fired0 + 3, 20);
            TurretReadout running = Readout(s, wet);
            bool supplied = placed && feed >= 0 && firing && !running.SupplyShort && running.SupplyLine.Contains(PipeNetworkService.FluidName(coolant)) && running.SupplyLine.Contains("每发 2 升")
                            && Rec(s, wet).ConsumerIds.Length == 1 && BuildingStatusService.Evaluate(s, wet).Kind == BuildingStatusKind.Working;
            Expect(supplied, $"E3 边上接一条冷却液管线：炮塔在管线上挂一个消费者，补给按整发装进内核后开火（状态“{BuildingStatusService.Evaluate(s, wet).Reason}”，补给“{running.SupplyLine}”）");

            // 补给中断：供给点拿掉，存着的打完后停火，原因“管线里没有冷却液了”；守恒。
            Seconds(4f);
            PipeNetworkService.Kernel.RemoveProducer(feed, out long left);
            bool stopped = StepUntil(() => BuildingStatusService.Evaluate(s, wet).ReasonCode == "turret.no_supply.dry", 120);
            long shotsAtStop = Shots(site);
            Seconds(3f);
            long totalShots = Shots(site) - fired0;
            long stored = (long)Math.Round(TurretService.StoredLiters(s, wet.BuildingId, coolant) * 1000f);
            float ammo = UnitState(s, wet).Ammo;
            long accounted = left + stored + (long)Math.Round(ammo * 2000f) + totalShots * 2000 + PipeNetworkService.Kernel.TotalStoredSlow();
            BuildingStatus dryStatus = BuildingStatusService.Evaluate(s, wet);
            Expect(stopped && Shots(site) == shotsAtStop && dryStatus.Reason.Contains("没有") && accounted == Supply && totalShots > 3,
                $"E4 负向“补给中断”：拿掉供给点（还剩 {left / 1000f:F1} 升）后炮塔把存着的打完就停火，状态“{dryStatus.Reason}”；冷却液逐毫升守恒：供给 {Supply / 1000f:F0} 升 = 剩余 {left / 1000f:F1}" +
                $" + 炮塔存着 {stored / 1000f:F1} + 内核里 {ammo:F0} 发 + 打出去 {totalShots} 发 × 2 升（合计 {accounted / 1000f:F1} 升）");
            Clear(site);
        }

        private static void CheckPowerAndDisable()
        {
            CampaignState s = NewWorld(6106);
            CombatSite site = Site;
            BuildingRecord off = PlaceTurret(s, L, powered: false, from: 24f, to: 60f);
            BuildingRecord on = PlaceTurret(s, L);
            if (off == null || on == null)
            {
                return;
            }
            int e1 = Hostile(site, off.Position + Dir(90f) * 6f, 500f, 500f);
            Seconds(3f);
            BuildingStatus st = BuildingStatusService.Evaluate(s, off);
            bool noPower = st.Kind == BuildingStatusKind.NoPower && !UnitState(s, off).WeaponEnabled && Mathf.Approximately(Hp(site, e1), 500f) && Readout(s, off).HasUnit;
            Expect(noPower, $"E5 没接上电网的炮塔在内核里（能被打），但不开火；状态“{st.Reason}”");

            int e2 = Hostile(site, on.Position + Dir(90f) * 6f, 500f, 500f);
            BuildingOps.TrySetEnabled(s, on.BuildingId, false, out string msg);
            TurretService.Sync(s);
            float hp0 = Hp(site, e2);
            Seconds(3f);
            BuildingStatus ds = BuildingStatusService.Evaluate(s, on);
            bool disabled = on.ConstructionState == BuildingConstructionState.Disabled && ds.Kind == BuildingStatusKind.Disabled && !UnitState(s, on).WeaponEnabled
                            && Mathf.Approximately(Hp(site, e2), hp0);
            BuildingOps.TrySetEnabled(s, on.BuildingId, true, out _);
            bool resumed = StepUntil(() => Hp(site, e2) < hp0, 10);
            Expect(disabled && resumed, $"E6 禁用的炮塔停火（状态“{ds.Reason}”）；重新启用后接着开火（{resumed}）");
            Clear(site);
        }

        // ── F 蓝图负向 ─────────────────────────────────────────────────────────────

        private static void CheckBlueprintNegatives()
        {
            CampaignState s = NewWorld(6107);
            CombatSite site = Site;
            BuildingRecord t = PlaceTurret(s, L);
            if (t == null)
            {
                return;
            }
            AddBlueprint(s, BpMobile, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()), "战斗履带");
            AddBlueprint(s, "bp_tu_heavy", Fixed(ComponentCatalog.CompCannonId), "重炮塔");
            AddBlueprint(s, "bp_tu_archived", Fixed(ComponentCatalog.CompBeamId), "归档的");
            BlueprintEditorService.Find(s, "bp_tu_archived").Archived = true;
            TurretOpResult mobile = TurretService.TryAssignBlueprint(s, t.BuildingId, BpMobile);
            TurretOpResult size = TurretService.TryAssignBlueprint(s, t.BuildingId, "bp_tu_heavy");
            TurretOpResult missing = TurretService.TryAssignBlueprint(s, t.BuildingId, "bp_not_exist");
            TurretOpResult archived = TurretService.TryAssignBlueprint(s, t.BuildingId, "bp_tu_archived");
            TurretOpResult place = TurretService.SetPlacementBlueprint(s, BpMobile);
            var choices = new List<string>();
            TurretService.BlueprintChoices(s, TurretCatalog.SizeLight, choices);
            Expect(!mobile.Ok && mobile.Failure == TurretFailure.NotFixedChassis && mobile.Message.Contains("不是固定底盘")
                   && !size.Ok && size.Failure == TurretFailure.SizeMismatch && size.Message.Contains("重型") && size.Message.Contains("轻型")
                   && !missing.Ok && missing.Failure == TurretFailure.BlueprintMissing && !archived.Ok && archived.Failure == TurretFailure.BlueprintMissing
                   && !place.Ok && Rec(s, t).BlueprintId == BpLight && choices.SequenceEqual(new[] { BpLight }),
                $"F1 换蓝图负向：移动底盘（“{mobile.Message}”）、占地不符（“{size.Message}”）、不存在 / 已归档（“{missing.Message}”）都拒绝，炮塔仍装原蓝图；建造栏选不能装炮塔的蓝图同样被拒；下拉里只列能装的");

            // 负向“核心固件或裸跑固件放进炮塔蓝图”：固定底盘蓝图的固件槽按炮塔判定，拒绝并写明“不能装进炮塔”。
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                BlueprintCircuitBoard board = Fixed(ComponentCatalog.CompGunId);
                CircuitOpResult core = board.TrySetFirmware(s, 0, FirmwareCatalog.FwOverloadId);
                string raw = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.IsRaw(s, id));
                CircuitOpResult rawR = raw != null ? board.TrySetFirmware(s, 1, raw) : default;
                CircuitOpResult regular = board.TrySetFirmware(s, 0, FirmwareCatalog.FwHomingId);
                Expect(!core.Success && core.Code == BlueprintCircuitBoard.CoreSignalOnlyCode && core.Message.Contains("不能装进炮塔") && raw != null && !rawR.Success
                       && rawR.Message.Contains("不能装进炮塔") && regular.Success && board.FirmwareSlots[0] == FirmwareCatalog.FwHomingId && string.IsNullOrEmpty(board.FirmwareSlots[1]),
                    $"F2 负向：核心固件放进炮塔蓝图被拒（“{core.Message}”）；未破解的敌方固件（裸跑固件 {raw}）被拒（“{rawR.Message}”）；常规固件（寻的）照常装上（{regular.Success} {regular.Message}）");

                // 旧蓝图（种类表改了之前保存的）里带核心固件：装不上；已经装着的炮塔停火并写明原因。
                BlueprintCircuitBoard legacy = Fixed(ComponentCatalog.CompGunId);
                legacy.FirmwareSlots[0] = FirmwareCatalog.FwOverloadId;
                AddBlueprint(s, "bp_tu_legacy_core", legacy, "旧核心蓝图");
                TurretOpResult legacyAssign = TurretService.TryAssignBlueprint(s, t.BuildingId, "bp_tu_legacy_core");
                Rec(s, t).BlueprintId = "bp_tu_legacy_core";
                Rec(s, t).BlueprintVersion = 1;
                TurretService.Sync(s);
                int e = Hostile(site, t.Position + Dir(45f) * 6f, 300f, 300f);
                Seconds(3f);
                BuildingStatus st = BuildingStatusService.Evaluate(s, t);
                Expect(!legacyAssign.Ok && legacyAssign.Failure == TurretFailure.BadFirmware && st.Kind == BuildingStatusKind.Idle && st.ReasonCode == "turret.bad_blueprint"
                       && st.Reason.Contains("不能装进炮塔") && !UnitState(s, t).WeaponEnabled && Mathf.Approximately(Hp(site, e), 300f),
                    $"F3 旧蓝图里带核心固件：换上被拒（“{legacyAssign.Message}”）；已经装着的炮塔停火，状态“{st.Reason}”");
                // 审查 P2：“蓝图用不了”的原因是按当前语言格式化好的——切到英文后状态与读数跟着换（运行时缓存的键含语言）。
                GameSettings.SetLanguage(GameLanguage.En);
                string enReason = BuildingStatusService.Evaluate(s, t).Reason;
                string enInvalid = Readout(s, t).InvalidReason;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string zhInvalid = Readout(s, t).InvalidReason;
                // 蓝图名是玩家起的（中文），只比文本键本身的文字。
                Expect(enReason.Contains("cannot go in a turret") && enInvalid.Contains("cannot go in a turret") && zhInvalid.Contains("装不进炮塔"),
                    $"F3b 切语言后“蓝图用不了”的原因跟着换：英文“{enInvalid}”；切回中文“{zhInvalid}”");
                TurretService.TryAssignBlueprint(s, t.BuildingId, BpLight);
                Clear(site);
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }

            // 版本：蓝图存了新版本，炮塔仍按装的版本（面板写 v1，最新 v2）；重新选中才换。
            BlueprintRecord rec = BlueprintEditorService.Find(s, BpLight);
            BlueprintVersionRecord v2 = Fixed(ComponentCatalog.CompBeamId).ToVersion(2, 1f);
            rec.Versions = rec.Versions.Append(v2).ToArray();
            rec.ActiveVersion = 2;
            TurretService.Sync(s);
            TurretReadout before = Readout(s, t);
            TurretOpResult re = TurretService.TryAssignBlueprint(s, t.BuildingId, BpLight);
            TurretReadout after = Readout(s, t);
            Expect(before.BlueprintVersion == 1 && before.LatestVersion == 2 && Mathf.Approximately(before.Range, 18f) && re.Ok && after.BlueprintVersion == 2
                   && Mathf.Approximately(after.Range, 14f),
                $"F4 蓝图存了新版本：炮塔仍按装的 v{before.BlueprintVersion}（最新 v{before.LatestVersion}，射程 {before.Range}）；重新选中这张蓝图才换成 v{after.BlueprintVersion}（射程 {after.Range}）");
        }

        // ── G 改名、名册、面板 ─────────────────────────────────────────────────────

        private static void CheckRenameRosterPanel()
        {
            CampaignState s = NewWorld(6108);
            CombatSite site = Site;
            int machines0 = MachineRegistry.AllRecords.Count;
            AddBlueprint(s, BpBeam, Fixed(ComponentCatalog.CompBeamId), "切割束炮塔");
            BuildingRecord t = PlaceTurret(s, L);
            BuildingRecord t2 = PlaceTurret(s, L);
            if (t == null || t2 == null)
            {
                return;
            }
            bool renamed = BuildingOps.TryRename(s, t.BuildingId, "东门炮塔", out string renameMsg);
            TurretService.Sync(s);
            bool label = site.TryGetUnitLabel(Unit(s, t), out string key, out string arg, out _) && key == "reaction.log.turret_named" && arg == "东门炮塔";
            bool tooLong = !BuildingOps.TryRename(s, t.BuildingId, new string('炮', 60), out string longMsg);
            Expect(renamed && Readout(s, t).Name == "东门炮塔" && label && tooLong,
                $"G1 炮塔改名（与建筑同一入口）：读数、反应日志 / 弹字的名字一起改（内核标签“{arg}”）；过长的名字被拒（“{longMsg}”）");

            Hostile(site, t.Position + Dir(10f) * 5f, 5f, 5f);
            StepUntil(() => site.Kernel.CountAlive(CombatFaction.Hostile) == 0, 15);
            Seconds(0.5f);

            // 名册“炮塔”分类：每座一行（名字 · 状态 · 模式 · 击毁），点一行打开炮塔面板；炮塔不是机器记录（不能参加远征）。
            VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out GameObject rgo);
            RosterPanelUIToolkit.InWorldOverrideForTests = true;
            VisualElement troot = FgProductionSelfCheck.MountUxml(UiKitFolder + "TurretPanel.uxml", out GameObject tgo);
            TurretPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                TurretPanelUIToolkit panel = tgo.AddComponent<TurretPanelUIToolkit>();
                panel.BindView(troot);
                RosterPanelUIToolkit roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                RosterPanelUIToolkit.Open();
                roster.ShowTurrets(true);
                bool rows = roster.ShowingTurrets && roster.TurretRowCount == 2 && roster.TurretRowText(0).Contains("东门炮塔") && roster.TurretRowText(0).Contains("最近")
                            && roster.TurretRowText(0).Contains("击毁 1") && roster.TurretNoteText.Contains("不能参加远征") && roster.TurretEmptyText.Length == 0
                            && MachineRegistry.AllRecords.Count == machines0; // 炮塔不是机器记录：放了两座炮塔，机器名册的机器数不变
                roster.OpenTurretRow(0);
                bool jumped = !RosterPanelUIToolkit.IsOpen && TurretPanelUIToolkit.IsOpen && TurretPanelUIToolkit.BuildingId == t.BuildingId;
                Expect(rows && jumped,
                    $"G2 FGR-DEF-001 名册“炮塔”分类：每座炮塔一行（“{roster.TurretRowText(0)}”），说明“{roster.TurretNoteText}”；点一行关掉名册、打开那座炮塔的面板（{jumped}）");

                panel.Refresh(force: true);
                int lowSlot = Enumerable.Range(0, 5).FirstOrDefault(i => panel.ModeCodeAt(i) == (int)CombatTargetMode.LowestHealth);
                bool opened = panel.PanelVisible && panel.TitleText.Contains("东门炮塔") && panel.BlueprintChoices.Count == 2 && panel.BlueprintChoices[0].Contains("v1")
                              && Enumerable.Range(0, 5).All(i => panel.ModeButton(i).text.Length > 0) && panel.ModeButton(0).text.StartsWith("● ", StringComparison.Ordinal)
                              && panel.KillsText.Contains("击毁 1") && panel.WeaponText.Contains("射程 18") && panel.WeaponText.Contains("真实弹体") && panel.SupplyText.Contains("只用电")
                              && panel.StatusText.Length > 0 && panel.UplinkHintText.Contains("没有接入口") && !panel.UplinkButton.enabledSelf
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstOpen) && !GameText.ContainsMarker(panel.FooterText) && panel.FooterText.Length > 5;
                panel.ClickMode(lowSlot);
                bool modeClicked = Rec(s, t).TargetMode == 1 && panel.MessageText.Contains("最低耐久") && panel.ModeButton(lowSlot).text.StartsWith("● ", StringComparison.Ordinal);
                panel.ClickModeAll();
                bool allClicked = Rec(s, t2).TargetMode == 1 && panel.MessageText.Contains("1 座");
                int beamIndex = panel.BlueprintChoices.ToList().FindIndex(x => x.Contains("切割束"));
                panel.SelectBlueprint(beamIndex);
                bool bpChanged = Rec(s, t).BlueprintId == BpBeam && panel.MessageText.Contains("切割束炮塔") && panel.WeaponText.Contains("射程 14");
                Expect(opened && modeClicked && allClicked && bpChanged,
                    $"G3 炮塔面板（真 UXML）：标题“{panel.TitleText}”、蓝图下拉（{string.Join(" / ", panel.BlueprintChoices)}）、五个模式按钮（选中的带●）、击毁“{panel.KillsText}”、" +
                    $"武器“{panel.WeaponText.Split('\n')[0]}”、补给“{panel.SupplyText}”、没有接入口时接入按钮不可点（{opened}）；点模式按钮立即生效（{modeClicked}）；“全部炮塔”（{allClicked}）；下拉换蓝图（{bpChanged}）");

                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh(force: true);
                bool en = !ContainsCjk(panel.FooterText) && !ContainsCjk(panel.ModeButton(1).text) && !ContainsCjk(panel.WeaponText) && !GameText.ContainsMarker(panel.StatusText);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.Refresh(force: true);
                Expect(en, "G4 切到英文：面板文字全部换成英文（页脚 / 模式 / 武器 / 状态）");
                TurretPanelUIToolkit.Close();

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "TurretPanel.uxml", "TurretWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("TurretRoot")?.RemoveFromClassList("uk-hidden");
                });
                string rprobe = UiToolkitLayoutProbe.Probe(UiKitFolder + "RosterPanel.uxml", "RosterPanelWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("RosterPanelRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("RosterPageList")?.AddToClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("RosterPageTurrets")?.RemoveFromClassList("uk-hidden");
                });
                string bprobe = UiToolkitLayoutProbe.Probe(UiKitFolder + "BuildModeHud.uxml", "BuildPanel", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("BuildTurretBlueprint")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("BuildTurretRange")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal) && rprobe.StartsWith("PASS", StringComparison.Ordinal) && bprobe.StartsWith("PASS", StringComparison.Ordinal),
                    "G5 布局探针（四种分辨率 + 超长文字 + USS 体检）：炮塔面板 " + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe) +
                    "；名册炮塔分类 " + (rprobe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : rprobe) + "；建造栏炮塔蓝图行 " + (bprobe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : bprobe));

                // 建筑面板“炮塔…”按钮只在炮塔上出现，点它打开炮塔面板。
                VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
                try
                {
                    ProductionPanelUIToolkit.InWorldOverrideForTests = true;
                    ProductionPanelUIToolkit prod = pgo.AddComponent<ProductionPanelUIToolkit>();
                    prod.BindView(proot);
                    ProductionPanelUIToolkit.Open(t2.BuildingId);
                    prod.Refresh();
                    bool onTurret = !prod.TurretButton.ClassListContains("bn-hidden") && prod.TurretButton.text == "炮塔…";
                    ProductionPanelUIToolkit.Open(s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse).BuildingId);
                    prod.Refresh();
                    bool hiddenElsewhere = prod.TurretButton.ClassListContains("bn-hidden");
                    ProductionPanelUIToolkit.Open(t2.BuildingId);
                    prod.Refresh();
                    prod.OpenTurret();
                    Expect(onTurret && hiddenElsewhere && TurretPanelUIToolkit.BuildingId == t2.BuildingId && TurretPanelUIToolkit.IsOpen,
                        $"G6 炮塔的建筑面板有“炮塔…”按钮（别的建筑没有），点它打开这座炮塔的面板（{onTurret} / {hiddenElsewhere}）");
                }
                finally
                {
                    ProductionPanelUIToolkit.Close();
                    ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                    Object.DestroyImmediate(pgo);
                }
            }
            finally
            {
                TurretPanelUIToolkit.Close();
                RosterPanelUIToolkit.Close();
                TurretPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                InputRouter.Reset();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(tgo);
                Object.DestroyImmediate(rgo);
                Clear(site);
            }
        }

        // ── H 受伤 / 维修 / 等级 / 攻城 / 被摧毁 ─────────────────────────────────────

        private static void CheckDamageTierSiegeDestroy()
        {
            CampaignState s = NewWorld(6109);
            CombatSite site = Site;
            BuildingRecord t = PlaceTurret(s, L);
            if (t == null)
            {
                return;
            }
            TurretRecord r = Rec(s, t);
            TurretService.TrySetMode(s, t.BuildingId, (int)CombatTargetMode.HighestThreat);
            BuildingOps.TrySetEnabled(s, t.BuildingId, false, out _);
            TurretService.Sync(s);
            CombatBench.Spec spec = CombatBench.FromTuning();
            int rw = CombatBench.RaiderWeapon(site, spec);
            int rp = CombatBench.RaiderProfile(site, spec);
            Vector2 c = t.Position;
            int raider = CombatBench.SpawnRaider(site, c + Dir(60f) * 3.5f, c, spec, rw, rp);
            float max = BuildingOps.MaxDurability(L);
            bool siege = StepUntil(() => site.Kernel.TryGetUnit(raider, out CombatUnitView v) && (v.Flags & CombatUnitFlags.SiegeAttack) != 0 && UnitState(s, t).Health < max - 1f, 20);
            TurretService.Sync(s);
            float hurt = t.Health;
            bool pulled = siege && hurt < max - 1f && Mathf.Abs(hurt - UnitState(s, t).Health) < 0.01f;

            // “先打拆建筑的”：旁边另一座炮塔，诱饵更近也不打它。
            BuildingRecord guard = PlaceTurret(s, L);
            bool siegeFirst = false;
            if (guard != null)
            {
                Hostile(site, guard.Position + Dir(200f) * 2f, 400f, 400f);
                TurretService.TrySetMode(s, guard.BuildingId, (int)CombatTargetMode.SiegeFirst);
                float dRaider = Vector2.Distance(guard.Position, UnitPos(site, raider));
                siegeFirst = dRaider <= Weapon(s, guard).Range && Site.TurretTargetUnit(Rec(s, guard).Serial) == raider;
                BuildingOps.TrySetEnabled(s, guard.BuildingId, false, out _);
                TurretService.Sync(s);
            }

            // 维修 = 建筑耐久被别处改（维修完工）→ 推给内核。
            t.Health = max;
            TurretService.Sync(s);
            bool repaired = Mathf.Abs(UnitState(s, t).Health - max) < 0.01f;
            Expect(pulled && siegeFirst && repaired,
                $"H1 突袭者打炮塔：内核给它置“正在破坏建筑”标记，炮塔耐久 {max}→{hurt:F1} 写回炮塔座建筑（{pulled}）；旁边“先打拆建筑的”炮塔选它而不是更近的诱饵（{siegeFirst}）；维修后的耐久推回内核（{repaired}）");

            // 等级：T2 / T3 射程 × 1.15 / 1.3，受到的伤害 -20% / -35%（内核全方位减伤）。
            t.Tier = 3;
            TurretService.Sync(s);
            TurretReadout ro3 = Readout(s, t);
            float armor = UnitState(s, t).Armor;
            float h0 = UnitState(s, t).Health;
            float firstHit = 0f;
            for (int i = 0; i < GameClock.StepHz * 6 && firstHit <= 0f; i++)
            {
                WorldSimulation.StepMany(1);
                float h = UnitState(s, t).Health;
                if (h < h0 - 1e-3f)
                {
                    firstHit = h0 - h;
                }
            }
            bool tier = Mathf.Approximately(ro3.Range, 18f * 1.3f) && Mathf.Abs(armor - 0.35f) < 1e-4f && Mathf.Abs(firstHit - spec.RaiderDamage * 0.65f) < 0.05f;
            t.Tier = 1;
            TurretService.Sync(s);
            Expect(tier, $"H2 炮塔座 T3：射程 {ro3.Range:F1} 米（18 × 1.3）、内核减伤 {armor:P0}，突袭者一发 {spec.RaiderDamage} 只扣 {firstHit:F2}");

            // 被摧毁：留下虚影（可以重建），内核单位拿掉，设置 / 名字 / 击毁数保留；重建后照旧。
            BuildingOps.TryRename(s, t.BuildingId, "前哨炮塔", out _);
            r.KillCount = 7;
            int destroyedNotes = NotifyCount("building_destroyed");
            t.Health = 1f;
            TurretService.Sync(s);
            bool gone = StepUntil(() => !site.TryGetTurretUnit(r.Serial, out _), 20);
            BuildingStatus st = BuildingStatusService.Evaluate(s, t);
            TurretRecord kept = Rec(s, t);
            bool destroyed = gone && st.Kind == BuildingStatusKind.Destroyed && kept != null && kept.BlueprintId == BpLight && kept.TargetMode == (int)CombatTargetMode.HighestThreat
                             && kept.KillCount == 7 && NotifyCount("building_destroyed") > destroyedNotes && BuildingOps.NameOf(t) == "前哨炮塔";
            CombatBench.ClearPrototypeUnits(site);
            t.ConstructionState = BuildingConstructionState.Operational;
            t.Health = max;
            HomeValleyPowerGrid.Recompute(s);
            TurretService.Sync(s);
            TurretReadout back = Readout(s, t);
            bool rebuilt = back.HasUnit && back.TargetMode == (int)CombatTargetMode.HighestThreat && back.Name == "前哨炮塔" && back.Kills == 7 && Mathf.Abs(back.Health - max) < 0.01f;
            Expect(destroyed && rebuilt,
                $"H3 炮塔耐久打空 = 炮塔座被摧毁：状态“{st.Reason}”，内核单位拿掉，“建筑被摧毁”通知；蓝图 / 模式 / 名字 / 击毁数保留（{destroyed}）；重建后照旧进内核（{rebuilt}）");
        }

        private static Vector2 UnitPos(CombatSite site, int unit) =>
            site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? new Vector2((float)v.Position.x, (float)v.Position.y) : Vector2.zero;

        // ── I 接入 ─────────────────────────────────────────────────────────────────

        private static void EquipOverload(CampaignState s)
        {
            SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, FirmwareCatalog.FwOverloadId);
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, print.CreatedId, 0);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!print.Success || !r.Success)
            {
                Fail("测试准备：过载装入信号核 1 号槽失败：" + print.Message + " / " + r.Message);
            }
        }

        private static void CheckUplink()
        {
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                CampaignState s = NewWorld(6110);
                CombatSite site = Site;
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
                EquipOverload(s);
                BlueprintCircuitBoard cannon = Fixed(ComponentCatalog.CompCannonId);
                cannon.TrySetUplink(2);
                AddBlueprint(s, BpPort, cannon, "接入重炮塔");
                BuildingRecord th = PlaceTurret(s, H, BpPort);
                BuildingRecord tl = PlaceTurret(s, L);
                if (th == null || tl == null)
                {
                    return;
                }
                TurretOpResult noPort = TurretUplink.Request(s, tl.BuildingId);
                s.SignalCore.UplinkMachineLogicId = 999;
                TurretOpResult inMachine = TurretUplink.Request(s, th.BuildingId);
                s.SignalCore.UplinkMachineLogicId = 0;
                SignalUplinkService.SilentNightProvider = () => true;
                TurretOpResult night = TurretUplink.Request(s, th.BuildingId);
                SignalUplinkService.SilentNightProvider = null;
                TurretOpResult notUp = TurretUplink.Leave(s);
                Expect(!noPort.Ok && noPort.Failure == TurretFailure.NoPort && noPort.Message.Contains("接入口") && !inMachine.Ok && inMachine.Failure == TurretFailure.SignalInMachine
                       && inMachine.Message.Contains("先退出接入") && !night.Ok && night.Failure == TurretFailure.SilentNight && !notUp.Ok && notUp.Failure == TurretFailure.NotUplinked,
                    $"I1 接入负向：蓝图没有接入口（“{noPort.Message}”）、信号在机器里（“{inMachine.Message}”）、静默夜（“{night.Message}”）、没接入时退出（“{notUp.Message}”）");

                Unregister(s, tl); // 下面数开火次数只数接入的炮塔（没有接入口的轻型炮塔不留在场上）
                TurretService.Sync(s);
                int weaponLocal = UnitState(s, th).Weapon;
                Vector2 c = th.Position;
                int e = Hostile(site, c + Dir(0f) * 12f, 5000f, 5000f);
                TurretOpResult up = TurretUplink.Request(s, th.BuildingId);
                TurretUnitState us = UnitState(s, th);
                site.Kernel.TryGetUnit(us.UnitId, out CombatUnitView uv);
                bool uplinked = up.Ok && TurretUplink.IsUplinkedTo(s, th.BuildingId) && us.Possessed && us.Weapon != weaponLocal && (uv.Flags & CombatUnitFlags.ReactionGated) != 0
                                && BuildingStatusService.Evaluate(s, th).ReasonCode == "turret.uplinked" && SignalUplinkService.StatusLine(s).Contains("信号在炮塔里")
                                && GameSettings.HasSeenGuidanceHook(GuidanceHooks.TurretFirstUplink) && Readout(s, th).Uplinked;
                long shots0 = Shots(site);
                Seconds(3f);
                bool noAuto = Shots(site) == shots0 && Mathf.Approximately(Hp(site, e), 5000f);
                Expect(uplinked && noAuto,
                    $"I2 FGR-DEF-005 接入炮塔：信号进入（“{up.Message}”），按接入态重新编译（武器参数 {weaponLocal}→{us.Weapon}，核心固件门控），HUD“{SignalUplinkService.StatusLine(s)}”；" +
                    $"接入后不再按模式自动开火（3 秒开火 {Shots(site) - shots0} 次）");

                int meltLogged0 = ReactionLog.Filtered(ReactionLogFilter.All, s).Count(x => x.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId);
                TurretOpResult miss = TurretUplink.FireAt(s, c + Dir(180f) * 10f);
                TurretOpResult first = TurretUplink.FireAt(s, c + Dir(0f) * 12f);
                Seconds(1.2f);
                TurretOpResult second = TurretUplink.FireAt(s, c + Dir(0f) * 12f);
                Seconds(0.5f);
                bool aimed = !miss.Ok && miss.Failure == TurretFailure.NoTarget && miss.Message.Contains("没有敌人") && first.Ok && second.Ok && Hp(site, e) < 5000f;
                Expect(aimed, $"I3 亲自瞄准：朝没有敌人的方向开火给原因（“{miss.Message}”）；朝敌人开火（重炮两段式：先瞄准 {first.Ok}、再开火 {second.Ok}，最近一次 {TurretUplink.LastFireResult}），目标耐久 {Hp(site, e):F0}/5000");
                // 审查 P2（B07）：接入炮塔打出的装配反应（核心固件“过载”→ 熔穿过载）照常进反应日志，触发者写“炮塔·名字”，同时按信号规则开始冷却。
                List<ReactionLogEntry> melts = ReactionLog.Filtered(ReactionLogFilter.All, s).Where(x => x.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId).ToList();
                string meltBy = melts.Count > 0 ? ReactionLog.PartyLabel(melts[0].Source) : string.Empty;
                Expect(melts.Count > meltLogged0 && meltBy.Contains("炮塔") && SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId) > 0f,
                    $"I3b 接入炮塔的熔穿过载进反应日志（{meltLogged0} → {melts.Count} 条，触发者“{meltBy}”），核心固件按信号规则开始冷却（剩 {SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId):F1} 秒）");

                TurretOpResult leave = TurretUplink.Leave(s);
                long shots1 = Shots(site);
                bool resumed = leave.Ok && !UnitState(s, th).Possessed && UnitState(s, th).Weapon == weaponLocal && StepUntil(() => Shots(site) > shots1, 10);
                Expect(resumed, $"I4 退出接入：信号回到归还核心（“{leave.Message}”），炮塔回到本地配置、按目标模式自动开火（{resumed}）");

                // 接入后炮塔失去作用 / 静默夜开始 / 信号去了别的机器：自动离开并说明。
                TurretUplink.Request(s, th.BuildingId);
                BuildingOps.TrySetEnabled(s, th.BuildingId, false, out _);
                WorldSimulation.StepMany(GameClock.StepHz);
                bool lostDisabled = !TurretUplink.IsUplinkedTo(s, th.BuildingId) && TurretService.LastFeedback.Contains("失去作用");
                BuildingOps.TrySetEnabled(s, th.BuildingId, true, out _);
                TurretService.Sync(s);
                TurretUplink.Request(s, th.BuildingId);
                SignalUplinkService.SilentNightProvider = () => true;
                WorldSimulation.StepMany(2);
                SignalUplinkService.SilentNightProvider = null;
                bool lostNight = !TurretUplink.IsUplinkedTo(s, th.BuildingId) && TurretService.LastFeedback.Contains("静默夜");
                TurretUplink.Request(s, th.BuildingId);
                s.SignalCore.UplinkMachineLogicId = 999;
                WorldSimulation.StepMany(2);
                s.SignalCore.UplinkMachineLogicId = 0;
                bool lostMachine = !TurretUplink.IsUplinkedTo(s, th.BuildingId) && TurretService.LastFeedback.Contains("回到归还核心");
                TurretUplink.Request(s, th.BuildingId);
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, th.BuildingId);
                WorldSimulation.StepMany(GameClock.StepHz);
                bool lostDestroyed = !TurretUplink.IsUplinkedTo(s, th.BuildingId) && string.IsNullOrEmpty(TurretUplink.ActiveTurretId(s)) && !site.TryGetTurretUnit(Rec(s, th).Serial, out _);
                Expect(lostDisabled && lostNight && lostMachine && lostDestroyed,
                    $"I5 接入中炮塔被禁用（{lostDisabled}）/ 静默夜开始（{lostNight}）/ 信号去了别的机器（{lostMachine}）/ 炮塔被摧毁（{lostDestroyed}）：信号回到归还核心并说明原因");
                Clear(site);
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
            }
        }

        // ── I6～I9 审查修复：接入重炮的补给、正式输入入口（面板“接入”按钮 / 左键 / 接入键）、战略暂停、信号覆盖 ──────────────

        private const string BpWetPort = "bp_tu_wet_port";

        /// <summary>与玩家点击同一个回调（Clickable.Invoke）；按钮不可点时返回 false。</summary>
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

        /// <summary>可编程的硬件输入（左键按下 + 指针位置 + 一个按键），注入 <see cref="InputRouter.DebugSetReader"/>。</summary>
        private sealed class AimReader : IInputReader
        {
            public bool Down0;
            public KeyCode KeyDown = KeyCode.None;
            public Vector3 Mouse;
            public bool GetKey(KeyCode key) => key != KeyCode.None && key == KeyDown;
            public bool GetKeyDown(KeyCode key) => key != KeyCode.None && key == KeyDown;
            public bool GetMouseButtonDown(int button) => Down0 && button == 0;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
        }

        private static bool PressLeft(AimReader reader, Camera cam, CampaignState s, bool pointerBlocked = false)
        {
            InputRouter.DebugClearConsumedKeys();
            reader.Down0 = true;
            bool owned = TurretUplink.HandleInput(cam, s, pointerBlocked);
            reader.Down0 = false;
            return owned;
        }

        private static void CheckUplinkSupplyInput()
        {
            CampaignState s = NewWorld(6117);
            CombatSite site = Site;
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            string coolantName = PipeNetworkService.FluidName(FluidOf("fw_coolant"));
            BlueprintCircuitBoard wetCannon = Fixed(ComponentCatalog.CompCannonId, "fw_coolant");
            wetCannon.TrySetUplink(2);
            AddBlueprint(s, BpWetPort, wetCannon, "接入冷却重炮塔");
            BuildingRecord th = PlaceTurret(s, H, BpWetPort);
            if (th == null)
            {
                return;
            }
            int serial = Rec(s, th).Serial;
            Vector2 c = th.Position;
            Vector2 ep = c + Dir(0f) * 12f;
            int e = Hostile(site, ep, 5000f, 5000f);
            var reader = new AimReader();
            var camGo = new GameObject("__fgturret_aim_cam") { hideFlags = HideFlags.HideAndDontSave };
            VisualElement troot = FgProductionSelfCheck.MountUxml(UiKitFolder + "TurretPanel.uxml", out GameObject tgo);
            TurretPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                // I6（审查 P1）：接入后重炮保留两段式——没接管线（补给 0）时左键开火被拒（缺补给），不瞄准、不积热、不扣血；状态行写明接入中缺冷却液。
                Seconds(3f);
                TurretOpResult up = TurretUplink.Request(s, th.BuildingId);
                CombatWeapon w = Weapon(s, th);
                float heat0 = UnitState(s, th).Heat;
                TurretOpResult dry1 = TurretUplink.FireAt(s, ep);
                CombatFireResult dryRes1 = TurretUplink.LastFireResult;
                Seconds(1.2f);
                TurretOpResult dry2 = TurretUplink.FireAt(s, ep);
                CombatFireResult dryRes2 = TurretUplink.LastFireResult;
                BuildingStatus upStatus = BuildingStatusService.Evaluate(s, th);
                bool refused = up.Ok && w.Mode == CombatWeaponMode.Cannon && Mathf.Approximately(w.AmmoPerShot, 1f) && !dry1.Ok && dryRes1 == CombatFireResult.NoAmmo
                               && !dry2.Ok && dryRes2 == CombatFireResult.NoAmmo && dry1.Message.Contains("缺补给") && Mathf.Approximately(Hp(site, e), 5000f)
                               && UnitState(s, th).Ammo < 1f && UnitState(s, th).Heat <= heat0 + 1e-3f
                               && upStatus.Kind == BuildingStatusKind.NoFluid && upStatus.ReasonCode.StartsWith("turret.uplinked.no_supply", StringComparison.Ordinal)
                               && upStatus.Reason.Contains(coolantName) && upStatus.Reason.Contains("接入");
                // 补给到了（测试捷径：装进两发；管线装填由 E 段覆盖）→ 两段式能打，真的开火那一刻扣一发。
                site.SetTurretAmmo(serial, 2f);
                TurretOpResult aim = TurretUplink.FireAt(s, ep);
                CombatFireResult aimRes = TurretUplink.LastFireResult;
                Seconds(1.2f);
                TurretOpResult fire = TurretUplink.FireAt(s, ep);
                CombatFireResult fireRes = TurretUplink.LastFireResult;
                float ammoAfter = UnitState(s, th).Ammo;
                bool fed = aim.Ok && aimRes == CombatFireResult.StillAiming && fire.Ok && fireRes == CombatFireResult.Ok && Mathf.Abs(ammoAfter - 1f) < 1e-3f && Hp(site, e) < 5000f;
                Expect(refused && fed,
                    $"I6 审查修复 FGR-DEF-004“补给中断”：接入的重型炮塔（重炮 + 冷却液，保留两段式）没接管线时左键开火被拒（{dryRes1} / {dryRes2}，“{dry1.Message}”），不瞄准、不积热、目标耐久不变；" +
                    $"状态“{upStatus.Reason}”；补给到了以后先瞄准（{aimRes}）再开火（{fireRes}），开火那一刻扣一发（2 → {ammoAfter:0.##}），目标耐久 {Hp(site, e):F0}/5000（{refused} / {fed}）");

                // I7（审查 P1：正式输入入口没有自动测试；P1：战略暂停中左键开火）：注入硬件输入，走 HomeValleyController 每帧调的 TurretUplink.HandleInput。
                site.SetTurretAmmo(serial, 5f);
                Seconds(5f); // 等上一发（I6）的重炮冷却走完
                Camera cam = camGo.AddComponent<Camera>();
                cam.transform.position = new Vector3(c.x + 6f, 40f, c.y);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                reader.Mouse = cam.WorldToScreenPoint(new Vector3(ep.x, 0f, ep.y));
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                GameClock.SetPaused(true);
                InputRouter.SetGameplayPaused(true, strategic: true);
                ulong hash0 = site.Kernel.StateHash();
                int pausedClicks0 = TurretUplink.PausedClicks;
                int fires0 = TurretUplink.InputFires;
                bool ownedPaused = PressLeft(reader, cam, s);
                bool frozen = ownedPaused && site.Kernel.StateHash() == hash0 && TurretUplink.PausedClicks == pausedClicks0 + 1 && TurretUplink.InputFires == fires0
                              && TurretService.LastFeedback.Contains("暂停");
                string pausedMsg = TurretService.LastFeedback;
                GameClock.SetPaused(false);
                InputRouter.SetGameplayPaused(false);
                bool ownedBlocked = PressLeft(reader, cam, s, pointerBlocked: true);
                bool blocked = ownedBlocked && TurretUplink.InputFires == fires0;
                float hp0 = Hp(site, e);
                bool owned1 = PressLeft(reader, cam, s);
                CombatFireResult r1 = TurretUplink.LastFireResult;
                Seconds(1.2f);
                bool owned2 = PressLeft(reader, cam, s);
                CombatFireResult r2 = TurretUplink.LastFireResult;
                Seconds(0.3f);
                bool clicked = owned1 && owned2 && r1 == CombatFireResult.StillAiming && r2 == CombatFireResult.Ok && TurretUplink.InputFires == fires0 + 2 && Hp(site, e) < hp0;
                Expect(frozen && blocked && clicked,
                    $"I7 FGR-DEF-005 正式输入入口（注入鼠标，走每帧的 TurretUplink.HandleInput）：战略暂停中左键被吃掉、不开火，内核状态哈希不变，提示“{pausedMsg}”（{frozen}）；" +
                    $"鼠标在界面上（建造模式 / 面板拿着鼠标）时不开火（{blocked}）；继续后左键瞄准 → 再左键开火（{r1} → {r2}），目标耐久 {hp0:F0} → {Hp(site, e):F0}（{clicked}）");

                // I8：炮塔面板“接入”按钮（真 UXML，与玩家点击同一个回调）——接入中点它退出、再点接入；接入键（战略视角）离开炮塔（CameraDirector）。
                TurretPanelUIToolkit panel = tgo.AddComponent<TurretPanelUIToolkit>();
                panel.BindView(troot);
                TurretPanelUIToolkit.Open(th.BuildingId);
                panel.Refresh(force: true);
                string leaveText = panel.UplinkButton?.text ?? string.Empty;
                bool leftByButton = Click(panel.UplinkButton) && !TurretUplink.IsUplinkedTo(s, th.BuildingId);
                panel.Refresh(force: true);
                string uplinkText = panel.UplinkButton?.text ?? string.Empty;
                bool upByButton = Click(panel.UplinkButton) && TurretUplink.IsUplinkedTo(s, th.BuildingId) && UnitState(s, th).Possessed;
                TurretPanelUIToolkit.Close();
                var director = new CameraDirector();
                director.Bind(cam, (out float2 an) => { an = new float2(c.x, c.y); return true; }, new Vector3(0f, 30f, -10f), 400f, startInStrategy: true);
                InputRouter.DebugClearConsumedKeys();
                reader.KeyDown = GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView);
                director.Tick(false);
                reader.KeyDown = KeyCode.None;
                bool leftByKey = !TurretUplink.IsActive && director.Mode == ViewMode.Strategy && !UnitState(s, th).Possessed && TurretService.LastFeedback.Contains("离开炮塔");
                Expect(leftByButton && upByButton && leftByKey,
                    $"I8 炮塔面板的接入按钮（“{leaveText}” → 退出 {leftByButton}；“{uplinkText}” → 接入 {upByButton}）；接入键（{GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView)}）在战略视角离开炮塔、镜头留在战略视角（{leftByKey}，“{TurretService.LastFeedback}”）");

                // I9（审查 P2：FGR-SIG-053）：炮塔座不在信号覆盖里时不能接入；接入中覆盖没了自动回到归还核心并说明。
                SignalCoverageService.OverrideForTests = (rid, pos) => new SignalCoverageSample(false, true, -5f, Vector2.zero, 150f, SignalCoverageSourceKind.None);
                TurretOpResult outside = TurretUplink.Request(s, th.BuildingId);
                SignalCoverageService.OverrideForTests = null;
                TurretOpResult inside = TurretUplink.Request(s, th.BuildingId);
                SignalCoverageService.OverrideForTests = (rid, pos) => new SignalCoverageSample(false, true, -5f, Vector2.zero, 150f, SignalCoverageSourceKind.None);
                WorldSimulation.StepMany(2);
                SignalCoverageService.OverrideForTests = null;
                bool coverage = !outside.Ok && outside.Failure == TurretFailure.OutOfCoverage && outside.Message.Contains("覆盖") && inside.Ok
                                && !TurretUplink.IsUplinkedTo(s, th.BuildingId) && TurretService.LastFeedback.Contains("覆盖");
                Expect(coverage, $"I9 FGR-SIG-053 炮塔座在信号覆盖外不能接入（“{outside.Message}”）；接入中覆盖没了自动回到归还核心（“{TurretService.LastFeedback}”）");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetPaused(false);
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                TurretPanelUIToolkit.Close();
                TurretPanelUIToolkit.InWorldOverrideForTests = false;
                UiEscapeStack.Clear();
                Object.DestroyImmediate(tgo);
                Object.DestroyImmediate(camGo);
                Clear(site);
            }
        }

        // ── J 存读档 ───────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(6111, scrap: 2000);
            CombatSite site = Site;
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            AddBlueprint(s, BpWet, Fixed(ComponentCatalog.CompGunId, "fw_coolant"), "冷却炮塔");
            BlueprintCircuitBoard cannon = Fixed(ComponentCatalog.CompCannonId);
            cannon.TrySetUplink(2);
            AddBlueprint(s, BpPort, cannon, "接入重炮塔");
            BuildingRecord t1 = PlaceTurret(s, L);
            BuildingRecord wet = TurretWithPipeRoom(s, BpWet, out GridCell near, out GridCell far);
            BuildingRecord th = PlaceTurret(s, H, BpPort);
            if (t1 == null || wet == null || th == null)
            {
                return;
            }
            BuildingOps.TryRename(s, t1.BuildingId, "存档炮塔", out _);
            TurretService.TrySetMode(s, t1.BuildingId, (int)CombatTargetMode.EliteFirst);
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            int coolant = FluidOf("fw_coolant");
            PipeNetworkService.Kernel.AddProducer(far.X, far.Y, coolant, 40000, 40000);
            Hostile(site, t1.Position + Dir(40f) * 6f, 4f, 4f, 0f, CombatUnitFlags.Elite);
            Hostile(site, wet.Position + Dir(220f) * 7f, 100000f, 100000f);
            Seconds(5f);
            TurretOpResult upSave = TurretUplink.Request(s, th.BuildingId);
            t1.Health = 120f;
            TurretService.Sync(s);
            Seconds(1f);
            bool upBefore = TurretUplink.IsUplinkedTo(s, th.BuildingId) && UnitState(s, th).Possessed;
            WorldSimulation.SyncAllForSave();
            string domain = Json(TurretService.StateOf(s));
            string unitsBefore = string.Join("|", TurretService.All(s).Select(r => site.TryGetTurretState(r.Serial, out TurretUnitState u)
                ? $"{r.Serial}:{u.Health:F2}:{u.Ammo:F2}:{u.Facing.x:F3},{u.Facing.y:F3}:{u.Heat:F2}" : r.Serial + ":-"));
            float storedBefore = TurretService.StoredLiters(s, wet.BuildingId, coolant);
            byte[] snap9 = site.Kernel.Serialize();
            byte[] snap8 = site.Kernel.SerializeFormatForTests(8);
            ulong hash = site.Kernel.StateHash();
            long steps0 = site.Kernel.Steps;
            var views0 = Enumerable.Range(0, site.Kernel.SlotCount).Select(i => site.Kernel.ViewAt(i)).ToList();
            var nav0 = views0.Select(v => site.Kernel.TryGetNavState(v.Id, out CombatNavState ns, out NavFailReason nf) ? $"{ns}/{nf}/{site.Kernel.RemainingRoute(v.Id):F3}" : "-").ToList();
            int kills = Rec(s, t1).KillCount;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("J1 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            CampaignState l = rr.State;
            CombatSite ls = Site;
            string domainAfter = Json(TurretService.StateOf(l));
            string unitsAfter = string.Join("|", TurretService.All(l).Select(r => ls.TryGetTurretState(r.Serial, out TurretUnitState u)
                ? $"{r.Serial}:{u.Health:F2}:{u.Ammo:F2}:{u.Facing.x:F3},{u.Facing.y:F3}:{u.Heat:F2}" : r.Serial + ":-"));
            float storedAfter = TurretService.StoredLiters(l, wet.BuildingId, coolant);
            BuildingRecord lt1 = HomeGridService.FindBuilding(l, t1.BuildingId);
            BuildingRecord lth = HomeGridService.FindBuilding(l, th.BuildingId);
            TurretService.Sync(l); // 读档后第一次对账：接入态（Possessed）按存档里的“信号在哪座炮塔”补回内核
            bool upAfter = TurretUplink.IsUplinkedTo(l, th.BuildingId) && UnitState(l, lth).Possessed;
            bool domainEq = domain == domainAfter;
            bool unitsEq = unitsBefore == unitsAfter;
            bool named = lt1 != null && BuildingOps.NameOf(lt1) == "存档炮塔" && Mathf.Abs(lt1.Health - 120f) < 0.01f;
            Expect(save.Success && domainEq && unitsEq && Mathf.Abs(storedBefore - storedAfter) < 0.001f && kills >= 1 && upBefore && upAfter && named,
                $"J1 真文件存读档：炮塔域逐字段一致（蓝图 / 版本 / 模式 / 击毁 {kills} / 管线消费者 / 信号在哪座炮塔）（{domainEq}）；内核里每座炮塔的耐久 / 整发 / 炮口朝向 / 热量一致（{unitsEq}：{unitsBefore} → {unitsAfter}）；" +
                $"炮塔存着的冷却液 {storedBefore:F1}→{storedAfter:F1} 升；接入存档前 {upBefore}（{upSave.Message}）/ 读档后 {upAfter}；名字与受伤耐久在炮塔座建筑上（{named}）" +
                (domainEq ? string.Empty : $"\n存档前：{domain}\n读档后：{domainAfter}"));

            Seconds(3f);
            bool continues = TurretService.TryGetReadout(l, wet.BuildingId, out TurretReadout wro) && wro.HasUnit && TurretService.SyncCount > 0 && TurretUplink.IsUplinkedTo(l, th.BuildingId)
                             && Readout(l, t1).TargetMode == (int)CombatTargetMode.EliteFirst;
            Expect(continues, "J2 读档后接着跑：对账照常、接入保持、设置不丢");

            // 内核快照：格式 9 往返哈希一致；格式 8 的旧快照照常读（补给 / 朝向没有 = 0，对账时补上）。
            CombatConfig cfg = ls.Kernel.Config;
            using var k9 = new CombatKernel(cfg, 256);
            CombatLoadResult r9 = k9.Load(snap9);
            using var k8 = new CombatKernel(cfg, 256);
            CombatLoadResult r8 = k8.Load(snap8);
            int turrets8 = k8.CountAlive(CombatFaction.Player, CombatUnitKind.Turret);
            int f9 = CombatKernel.PeekFormat(snap9);
            int f8 = CombatKernel.PeekFormat(snap8);
            // 接入态（Possessed）按设计不进内核快照：读档清掉，由炮塔对账按存档里的“信号在哪座炮塔”补回（J1 已断言）。这里先确认它被清掉，再补回后比哈希。
            bool possessedDropped = views0.Where(v => (v.Flags & CombatUnitFlags.Possessed) != 0)
                .All(v => k9.TryGetUnit(v.Id, out CombatUnitView u) && (u.Flags & CombatUnitFlags.Possessed) == 0);
            foreach (CombatUnitView v in views0.Where(v => (v.Flags & CombatUnitFlags.Possessed) != 0))
            {
                k9.SetFlag(v.Id, CombatUnitFlags.Possessed, true);
            }
            ulong h9 = k9.StateHash();
            if (h9 != hash)
            {
                // 诊断：逐单位对照存档前的视图（只在哈希不一致时输出前几处差别）。
                var diffs = new List<string>();
                byte[] again = k9.Serialize();
                diffs.Add($"重新序列化逐字节一致 {again.SequenceEqual(snap9)}，槽位 {views0.Count}→{k9.SlotCount}，步数 {steps0}→{k9.Steps}，弹体 {k9.ProjectileCount}");
                for (int i = 0; i < views0.Count && diffs.Count < 8; i++)
                {
                    CombatUnitView a = views0[i];
                    if (!k9.TryGetUnit(a.Id, out CombatUnitView b))
                    {
                        diffs.Add($"单位 {a.Id}（{a.Kind}/{a.ExtKey}）读档后不在");
                        continue;
                    }
                    string sa = $"{a.Flags}|{a.Position.x:R},{a.Position.y:R}|{a.Health:R}|{a.Heat:R}|{a.Ammo:R}|{a.Facing.x:R},{a.Facing.y:R}|{a.Cycle:R}|{a.Secondary:R}|{a.NextFireAt:R}|{a.AimReadyAt:R}|{a.Command}|{a.CommandTarget}|{a.Weapon}|{nav0[i]}";
                    string nb = k9.TryGetNavState(b.Id, out CombatNavState ns, out NavFailReason nf) ? $"{ns}/{nf}/{k9.RemainingRoute(b.Id):F3}" : "-";
                    string sb = $"{b.Flags}|{b.Position.x:R},{b.Position.y:R}|{b.Health:R}|{b.Heat:R}|{b.Ammo:R}|{b.Facing.x:R},{b.Facing.y:R}|{b.Cycle:R}|{b.Secondary:R}|{b.NextFireAt:R}|{b.AimReadyAt:R}|{b.Command}|{b.CommandTarget}|{b.Weapon}|{nb}";
                    if (sa != sb)
                    {
                        diffs.Add($"单位 {a.Id}（{a.Kind}/{a.ExtKey}）：{sa} → {sb}");
                    }
                }
                Line("    · J3 诊断：" + string.Join("；", diffs));
            }
            Expect(possessedDropped && f9 == CombatConst.FormatVersion && r9 == CombatLoadResult.Ok && h9 == hash && f8 == 8 && r8 == CombatLoadResult.Ok && turrets8 == 3,
                $"J3 战斗内核快照：格式 {f9} 往返（{r9}）状态哈希一致（含补给与朝向；接入态按设计不进快照、读档清掉 {possessedDropped}）{h9:X16} = {hash:X16}；格式 {f8} 的旧快照照常读（{r8}，炮塔 {turrets8} 座，补给 / 转速补零后由对账补上）");

            // 旧档没有炮塔域 / 坏值：补成空域、钳回合法范围、重复记录与序号整理。
            CampaignState old = CampaignState.CreateNew("fgturret-old", "Standard", 6112);
            old.Raids.Turrets = null;
            CampaignFgStateDomains.EnsureAll(old);
            bool empty = old.Raids.Turrets != null && old.Raids.Turrets.Turrets.Length == 0 && !old.Raids.Turrets.DefaultsSeeded;
            old.Raids.Turrets.Turrets = new[]
            {
                new TurretRecord { BuildingId = "home:turret_light#1", Serial = 3, TargetMode = 9, KillCount = 2, EliteKills = 5 },
                new TurretRecord { BuildingId = "home:turret_light#1", Serial = 4 },
                new TurretRecord { BuildingId = "home:turret_light#2", Serial = 3, ConsumerIds = new[] { 1 }, ConsumerFluids = Array.Empty<int>() },
                null,
            };
            old.Raids.Turrets.NextSerial = 1;
            TurretService.EnsureState(old);
            TurretRecord[] fixedRecs = old.Raids.Turrets.Turrets;
            bool clamped = empty && fixedRecs.Length == 2 && fixedRecs[0].TargetMode == 4 && fixedRecs[0].EliteKills == 2 && fixedRecs[1].Serial != fixedRecs[0].Serial
                           && fixedRecs[1].ConsumerIds.Length == 0 && old.Raids.Turrets.NextSerial > fixedRecs.Max(x => x.Serial);
            Expect(clamped, $"J4 旧档没有炮塔域补成空域（{empty}）；坏值：模式钳到 0～4、精英击毁不超过总数、重复的建筑只留第一条、重复序号重新编号、消费者句柄不配对清空、下一个序号大于已用的");
            Clear(ls);
        }

        /// <summary>炮塔这一侧的模拟状态签名（内核里每座炮塔的耐久 / 整发 / 热量 / 炮口朝向 / 接入态、击毁数、炮塔座耐久、存着的流体、目标耐久、这段时间的开火数）。</summary>
        private static string TurretSignature(CampaignState s, CombatSite site, IReadOnlyList<int> hostiles, long shotsBase, int fluid)
        {
            var sb = new StringBuilder();
            foreach (TurretRecord r in TurretService.All(s).OrderBy(x => x.Serial))
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, r.BuildingId);
                sb.Append(r.Serial).Append(':').Append(r.KillCount).Append(':');
                if (site.TryGetTurretState(r.Serial, out TurretUnitState u))
                {
                    sb.Append(u.Health.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(u.Ammo.ToString("R", CultureInfo.InvariantCulture)).Append('/')
                        .Append(u.Heat.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(u.Facing.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(u.Facing.y.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(u.Possessed ? "P" : "-");
                }
                sb.Append('/').Append(b != null ? b.Health.ToString("R", CultureInfo.InvariantCulture) : "-");
                sb.Append('/').Append(TurretService.StoredLiters(s, r.BuildingId, fluid).ToString("R", CultureInfo.InvariantCulture)).Append('|');
            }
            foreach (int h in hostiles)
            {
                sb.Append(Hp(site, h).ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.Append("shots+").Append(Shots(site) - shotsBase).ToString();
        }

        /// <summary>
        /// J5（审查 P2：存档不改变结果）：同一份存档，“存完接着跑 N 秒”与“读档后跑 N 秒”的炮塔状态逐字段一致——对账只在整拍做（读档后第一步不再多对账一次），
        /// 接入态（不进内核快照）在家园开内核时就补回（读档后的第一步也不自动开火）。
        /// </summary>
        private static void CheckLoadContinuation()
        {
            CampaignState s = NewWorld(6119, scrap: 2000);
            CombatSite site = Site;
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).ToArray();
            AddBlueprint(s, BpWet, Fixed(ComponentCatalog.CompGunId, "fw_coolant"), "冷却炮塔");
            BlueprintCircuitBoard cannon = Fixed(ComponentCatalog.CompCannonId);
            cannon.TrySetUplink(2);
            AddBlueprint(s, BpPort, cannon, "接入重炮塔");
            BuildingRecord wet = TurretWithPipeRoom(s, BpWet, out GridCell near, out GridCell far);
            BuildingRecord th = PlaceTurret(s, H, BpPort);
            BuildingRecord t1 = PlaceTurret(s, L);
            if (wet == null || th == null || t1 == null)
            {
                return;
            }
            int coolant = FluidOf("fw_coolant");
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, coolant, 30000, 30000);
            var hostiles = new List<int>
            {
                Hostile(site, wet.Position + Dir(220f) * 7f, 100000f, 100000f),
                Hostile(site, t1.Position + Dir(40f) * 6f, 100000f, 100000f),
                Hostile(site, th.Position + Dir(130f) * 8f, 100000f, 100000f),
            };
            TurretUplink.Request(s, th.BuildingId);
            Seconds(4f);
            // 测试供给点不进存档：存档前拿掉（两条分支都只烧炮塔与管线里存着的冷却液）。
            PipeNetworkService.Kernel.RemoveProducer(feed, out _);
            Seconds(0.35f); // 停在两次对账之间（不在整拍上）存档
            WorldSimulation.SyncAllForSave();
            long tickAtSave = GameClock.Ticks;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long shotsAtSave = Shots(site);
            Seconds(6f);
            string straight = TurretSignature(s, site, hostiles, shotsAtSave, coolant);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("J5 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            CampaignState l = rr.State;
            CombatSite ls = Site;
            BuildingRecord lth = HomeGridService.FindBuilding(l, th.BuildingId);
            bool possessedAtLoad = lth != null && UnitState(l, lth).Possessed; // 读档后、第一步之前：接入态已补回
            bool sameTick = GameClock.Ticks == tickAtSave;
            long lshots = Shots(ls);
            Seconds(6f);
            string loaded = TurretSignature(l, ls, hostiles, lshots, coolant);
            Expect(save.Success && possessedAtLoad && sameTick && straight == loaded && !straight.Contains("shots+0"),
                $"J5 读档接着跑 = 不存档一路跑：存档停在两次对账之间（步 {tickAtSave}），之后各跑 6 游戏秒，炮塔耐久 / 整发 / 热量 / 炮口朝向 / 接入态 / 击毁数 / 存着的冷却液 / 目标耐久 / 开火数逐字段一致；" +
                $"读档后第一步之前接入态已补回（{possessedAtLoad}）" + (straight == loaded ? string.Empty : $"\n不存档：{straight}\n读档后：{loaded}"));
            Clear(ls);
        }

        // ── K / L 暂停、倍速、观察 ─────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            CombatSite site = Site;
            AddBlueprint(s, BpBeam, Fixed(ComponentCatalog.CompBeamId), "切割束炮塔");
            BuildingRecord a = PlaceTurret(s, L);
            BuildingRecord b = PlaceTurret(s, L, BpBeam);
            if (a == null || b == null)
            {
                return "no-turret";
            }
            TurretService.TrySetMode(s, b.BuildingId, (int)CombatTargetMode.LowestHealth);
            CombatBench.Spec spec = CombatBench.FromTuning();
            for (int i = 0; i < 6; i++)
            {
                Hostile(site, a.Position + Dir(i * 60f + 15f) * (5f + i), 30f + i * 20f, 200f, i % 2 == 0 ? 10f : 0f, i == 3 ? CombatUnitFlags.Elite : CombatUnitFlags.None);
            }
            CombatBench.SpawnRaider(site, b.Position + Dir(100f) * 8f, b.Position, spec, CombatBench.RaiderWeapon(site, spec), CombatBench.RaiderProfile(site, spec));
            WorldSimulation.StepMany(GameClock.StepHz);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Json(TurretService.StateOf(s)) + site.Kernel.StateHash() + TurretService.SyncCount;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Json(TurretService.StateOf(s)) + site.Kernel.StateHash() + TurretService.SyncCount == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 20;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 300)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            string result = Json(TurretService.StateOf(s)) + "|" + site.Kernel.StateHash().ToString("X16") + "|" + Shots(site) + "|" + a.Health.ToString("F2", CultureInfo.InvariantCulture)
                            + "|" + b.Health.ToString("F2", CultureInfo.InvariantCulture);
            Clear(site);
            return result;
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(6113, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != null && !reference.StartsWith("no-turret", StringComparison.Ordinal) && reference.Contains("\"KillCount\":"),
                "K 暂停中（120 帧）炮塔记录、内核状态与对账次数都不动；0.5x / 1x / 2x / 3x 跑同样的 20 游戏秒，炮塔记录（击毁数）、家园内核状态哈希、开火数与炮塔耐久逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6114, true, 1f, false, out _);
            string unseen = RunScenario(6114, false, 1f, false, out _);
            Expect(seen == unseen && !seen.StartsWith("no-turret", StringComparison.Ordinal),
                "L 同一场炮塔战斗在观察与不观察家园时跑 20 游戏秒，炮塔记录、内核状态哈希、开火数与耐久逐字段一致（FGR-BASE-021：远征时家园炮塔照常防守）"
                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── M 家园训练靶迁进战斗内核（DEBT-FG2FW02-07）─────────────────────────────────

        private static void CheckTrainingTarget()
        {
            CampaignState s = NewWorld(6115);
            CombatSite site = Site;
            HomeValleyCombatTargets.EnsureSeeded(s);
            int dummy = HomeValleyCombatTargets.EnsureDummyUnit(site, s);
            CombatTargetRecord target = HomeValleyCombatTargets.Find(s, HomeValleyCombatTargets.LowThreatTargetId);
            AddBlueprint(s, "bp_tu_dummy_wet", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_coolant" }), "湿身练习机");
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, "bp_tu_dummy_wet", HomeValleyLayout.RegionId, target.Position + new Vector2(3f, 0f), 100f, 100f);
            MachineLoadoutRegistry.Register(s, m.LogicId, "bp_tu_dummy_wet", 1);
            WorldSimulation.StepMany(2);
            bool machineIn = site.TryGetMachineUnit(m.LogicId, out int mu);
            if (machineIn)
            {
                site.SetUnitPosition(mu, target.Position + new Vector2(3f, 0f));
                site.RefreshMachineWeapon(s, m.LogicId);
            }
            site.Kernel.TryGetUnit(dummy, out CombatUnitView v0);
            float hp0 = target.Health;
            int zones0 = site.Kernel.ZoneCount;
            HomeValleyCombatTargets.HitResult hit = HomeValleyCombatTargets.TryAttack(s, m.LogicId, HomeValleyCombatTargets.LowThreatTargetId, s.RandomSeed, isAiSource: true);
            site.Kernel.TryGetUnit(dummy, out CombatUnitView v1);
            bool viaKernel = machineIn && hit.Success && hit.DamageApplied > 0f && hit.IsAiSource && Mathf.Abs(v1.Health - target.Health) < 0.01f
                             && Mathf.Abs(hp0 - target.Health - hit.DamageApplied) < 0.01f && (v1.Flags & CombatUnitFlags.ExternalHealth) == 0 && (v1.Flags & CombatUnitFlags.Report) != 0
                             && HomeValleyCombatTargets.RecentEvents.Count > 0 && HomeValleyCombatTargets.RecentEvents[HomeValleyCombatTargets.RecentEvents.Count - 1].Success;
            WorldSimulation.StepMany(GameClock.StepHz / 2);
            int zones1 = site.Kernel.ZoneCount;
            Expect(viaKernel && zones1 > zones0,
                $"M1 DEBT-FG2FW02-07 家园训练靶迁进战斗内核：攻击经内核开火入口（血量在内核、逐次报告，伤害 {hit.DamageApplied:F1}，靶子 {hp0:F0}→{target.Health:F1} 与内核 {v1.Health:F1} 一致）；" +
                $"固件读法在靶上生效（冷却液“留下一片”在命中点生成区域 {zones0}→{zones1}）");
            bool dead = false;
            for (int i = 0; i < 40 && !dead; i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
                HomeValleyCombatTargets.TryAttack(s, m.LogicId, HomeValleyCombatTargets.LowThreatTargetId, s.RandomSeed + i, isAiSource: true);
                dead = target.Health <= 0f;
            }
            bool regen = dead && target.RegenCooldownRemaining > 0f;
            HomeValleyCombatTargets.HitResult refused = HomeValleyCombatTargets.TryAttack(s, m.LogicId, HomeValleyCombatTargets.LowThreatTargetId, s.RandomSeed, isAiSource: true);
            Expect(regen && !refused.Success && refused.FailureReason.Contains("再生冷却"),
                $"M2 打空进入再生冷却（{target.RegenCooldownRemaining:F1} 秒），冷却中再打被拒（“{refused.FailureReason}”）");
        }

        /// <summary>
        /// M3（审查 P0 回归）：训练靶迁进内核后，自动交战要同步开火——同一步里两台机器先后发出自动交战请求时，第一台的开火不能在事件处理中重入排空、
        /// 把同一批里排在后面的事件（第二台的请求）清掉。断言：两台都恰好攻击一次、每条事件恰好处理一次（序号不重复）、请求是在这一轮事件处理完之后结算的。
        /// </summary>
        private static void CheckEngageReentry()
        {
            CampaignState s = NewWorld(6118);
            CombatSite site = Site;
            HomeValleyCombatTargets.EnsureSeeded(s);
            int dummy = HomeValleyCombatTargets.EnsureDummyUnit(site, s);
            CombatTargetRecord target = HomeValleyCombatTargets.Find(s, HomeValleyCombatTargets.LowThreatTargetId);
            AddBlueprint(s, "bp_tu_dummy_gun", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()), "练习机");
            Vector2 far = target.Position + new Vector2(40f, 0f);
            MachineOpResult m1 = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, "bp_tu_dummy_gun", HomeValleyLayout.RegionId, far, 100f, 100f);
            MachineOpResult m2 = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, "bp_tu_dummy_gun", HomeValleyLayout.RegionId, far + new Vector2(0f, 3f), 100f, 100f);
            MachineLoadoutRegistry.Register(s, m1.LogicId, "bp_tu_dummy_gun", 1);
            MachineLoadoutRegistry.Register(s, m2.LogicId, "bp_tu_dummy_gun", 1);
            WorldSimulation.StepMany(2);
            bool inKernel = site.TryGetMachineUnit(m1.LogicId, out int u1) & site.TryGetMachineUnit(m2.LogicId, out int u2);
            if (!inKernel)
            {
                Fail("M3 测试准备：两台练习机没进家园战斗内核");
                return;
            }
            site.RefreshMachineWeapon(s, m1.LogicId);
            site.RefreshMachineWeapon(s, m2.LogicId);
            // 先在射程外待够一个交战间隔（射程外不消耗冷却：两台都“就绪”），再同时放进射程——下一步两台都发请求。
            site.SetUnitPosition(u1, far);
            site.SetUnitPosition(u2, far + new Vector2(0f, 3f));
            Seconds(HomeValleyController.AiEngageIntervalSeconds + 1f);
            target.Health = target.MaxHealth;
            target.RegenCooldownRemaining = 0f;
            HomeValleyCombatTargets.SyncDummy(site, s);
            site.SetUnitPosition(u1, target.Position + new Vector2(3f, 0f));
            site.SetUnitPosition(u2, target.Position + new Vector2(-3f, 0f));
            int a1 = HomeValleyCombatTargets.AttemptCount(m1.LogicId);
            int a2 = HomeValleyCombatTargets.AttemptCount(m2.LogicId);
            long deferred0 = site.DeferredEngageCount;
            var seen = new List<long>();
            var engaged = new List<int>();
            bool engageDuringBatch = false;
            Action<CombatSite, CombatEvent> inner = site.EventObserver;
            site.EventObserver = (cs, ev) =>
            {
                inner?.Invoke(cs, ev);
                seen.Add(ev.Seq);
                if (ev.Kind == CombatEventKind.EngageRequest)
                {
                    engaged.Add(ev.Unit);
                    // 修复前：第一台的请求在这里同步开火、内层排空清掉同一批；修复后：请求延后，遍历期间攻击次数不变。
                    engageDuringBatch |= HomeValleyCombatTargets.AttemptCount(m1.LogicId) != a1 || HomeValleyCombatTargets.AttemptCount(m2.LogicId) != a2;
                }
            };
            try
            {
                WorldSimulation.StepMany(1);
            }
            finally
            {
                site.EventObserver = inner;
            }
            int d1 = HomeValleyCombatTargets.AttemptCount(m1.LogicId) - a1;
            int d2 = HomeValleyCombatTargets.AttemptCount(m2.LogicId) - a2;
            bool bothRequested = engaged.Contains(u1) && engaged.Contains(u2);
            bool unique = seen.Count == seen.Distinct().Count();
            long deferred = site.DeferredEngageCount - deferred0;
            Expect(bothRequested && d1 == 1 && d2 == 1 && unique && deferred >= 2 && !engageDuringBatch && dummy > 0,
                $"M3 审查 P0 回归：同一步两台机器都发自动交战请求（{bothRequested}），两台都恰好攻击一次（{d1} / {d2}）；这一步处理的 {seen.Count} 条事件序号不重复（{unique}）；" +
                $"请求延后到这一轮事件处理完才结算（延后 {deferred} 条，遍历中没有开火 {!engageDuringBatch}）");
        }

        /// <summary>B25（审查 P2：种子无关性）：从种子测试集里取几张不同地形——按地形找空地放炮塔、进内核、按模式开火打掉射程内的敌人，不依赖固定坐标。</summary>
        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(4).ToArray();
            var bad = new List<string>();
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed);
                CombatSite site = Site;
                BuildingRecord t = PlaceTurret(s, L);
                if (t == null)
                {
                    bad.Add($"{seed}：放不下");
                    continue;
                }
                int e = Hostile(site, t.Position + Dir(seed % 360) * 7f, 30f, 30f);
                bool killed = StepUntil(() => Hp(site, e) <= 0f, 15);
                if (!killed || Unit(s, t) == 0 || Rec(s, t).KillCount < 1)
                {
                    bad.Add($"{seed}：击毁 {Rec(s, t)?.KillCount}，敌人耐久 {Hp(site, e):F0}");
                }
                Clear(site);
            }
            Expect(seeds.Length == 4 && bad.Count == 0,
                $"B25 种子测试集 {string.Join(" / ", seeds)}：每张地形都按地形找到空地放下炮塔、进内核、开火打掉 7 米外的敌人并记击毁" + (bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad)));
        }

        // ── N 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6116, scrap: 5000);
            CombatSite site = Site;
            int want = (int)CombatSite.Tuning("combat.perf.turrets", 80);
            int enemies = (int)CombatSite.Tuning("combat.perf.enemies", 200);
            float budget = CombatSite.Tuning("combat.perf.step_budget_ms", 6f);
            GridCell core = HomeGridService.CorePivot(s);
            var cells = new List<GridCell>();
            for (int dy = -14; dy <= 14; dy++)
            {
                for (int dx = -14; dx <= 14; dx++)
                {
                    cells.Add(new GridCell(core.X + dx * 3, core.Y + dy * 3));
                }
            }
            cells.Sort((p, q) => ((p.X - core.X) * (p.X - core.X) + (p.Y - core.Y) * (p.Y - core.Y)).CompareTo((q.X - core.X) * (q.X - core.X) + (q.Y - core.Y) * (q.Y - core.Y)));
            var turrets = new List<BuildingRecord>(want);
            foreach (GridCell cell in cells)
            {
                if (turrets.Count >= want)
                {
                    break;
                }
                if (HomeGridService.ValidatePlacement(s, L, cell, 0, asPlayerPlacement: false, checkCost: false).Ok)
                {
                    turrets.Add(Register(s, L, cell));
                }
            }
            // 测试捷径：电力由电网自检覆盖，这里让全部炮塔吃到电（测的是炮塔对账与内核，不是电网分配）。
            foreach (BuildingRecord b in turrets)
            {
                b.PowerState = BuildingPowerState.Powered;
            }
            TurretService.Sync(s);
            CombatBench.Spec spec = CombatBench.PerfSpec();
            Vector2 center = new Vector2(core.X, core.Y);
            int perGroup = Math.Max(1, enemies / 4);
            for (int g = 0, spawned = 0; g < 4 && spawned < enemies; g++)
            {
                float ang = g * Mathf.PI * 0.5f + 0.4f;
                Vector2 arrival = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 55f;
                int n = g == 3 ? enemies - spawned : Math.Min(perGroup, enemies - spawned);
                spawned += CombatBench.SpawnRaidGroup(site, arrival, center, n, spec).Count;
            }
            float dt = GameClock.StepSeconds;
            double time = site.Kernel.Time;
            int every = Math.Max(1, Mathf.RoundToInt(TurretCatalog.SyncSeconds * GameClock.StepHz));
            for (int i = 0; i < 120; i++)
            {
                site.Step(dt, time);
                time += dt;
                if (i % every == 0)
                {
                    TurretService.Sync(s);
                }
            }
            var kernelMs = new List<double>(600);
            var syncMs = new List<double>(64);
            var sw = new Stopwatch();
            long shots0 = Shots(site);
            int inKernel = site.TurretUnitCount;
            for (int i = 0; i < 600; i++)
            {
                site.Step(dt, time);
                time += dt;
                kernelMs.Add(site.LastKernelMs);
                if (i % every == 0)
                {
                    sw.Restart();
                    TurretService.Sync(s);
                    sw.Stop();
                    syncMs.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            kernelMs.Sort();
            double avg = kernelMs.Average();
            double p95 = kernelMs[(int)(kernelMs.Count * 0.95)];
            double syncAvg = syncMs.Average();
            double perStep = syncAvg / every;
            long fired = Shots(site) - shots0;
            int hostiles = site.Kernel.CountAlive(CombatFaction.Hostile);
            PerfLines.Add($"家园 {turrets.Count} 座真实炮塔（内核 {inKernel}）+ {enemies} 突袭者：内核单步 平均 {avg:F3} ms / p95 {p95:F3} ms；炮塔对账 {syncAvg:F3} ms/次（每 {every} 步一次，摊到每步 {perStep:F4} ms）；" +
                          $"600 步开火 {fired} 次，剩余突袭者 {hostiles}");
            PerfGate.Expect(turrets.Count >= want * 3 / 4 && inKernel == turrets.Count && fired > 0,
                $"N 性能：{turrets.Count} 座炮塔 + {enemies} 突袭者，内核单步平均 {avg:F3} ms / p95 {p95:F3} ms（预算 {budget} ms），炮塔对账 {syncAvg:F3} ms/次（阈值 1.5 ms；O(炮塔数)，逐发 / 逐弹体在 AOT 内核）（Editor batchmode，真机另测 FG15-SYS-02）",
                new[] { PerfGate.Le(avg, budget, "内核单步平均 ms"), PerfGate.Le(p95, budget, "内核单步 p95 ms"), PerfGate.Le(syncAvg, 1.5, "炮塔对账 ms/次") }, Expect, Line);

            // N2（审查 P1：热更层每帧开销）：家园被观察时每帧调的炮塔表现（TurretViews.FrameUpdate）——炮塔头的位置 / 朝向由地点画面同步的 Burst 作业写，
            // 热更层平时每帧只比较一次结构键；放置预览的射程半径按输入缓存。测 600 帧：结构对账次数、射程半径重算次数、每帧耗时。
            var rig = new GameObject("__fgturret_perf_views") { hideFlags = HideFlags.HideAndDontSave };
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                var first = Stopwatch.StartNew();
                TurretViews.FrameUpdate(s, site, rig.transform, null); // 第一次：建炮塔头、绑进画面同步
                first.Stop();
                int resync0 = TurretViews.ResyncCount;
                List<BuildingRecord> standing = turrets.Where(b => Unit(s, b) > 0).ToList(); // 突袭中被摧毁的炮塔不在内核里，没有炮塔头
                bool bound = standing.Count > 0 && standing.All(b => TurretViews.BoundUnitOf(b.BuildingId) == Unit(s, b)) && TurretViews.HeadCount == standing.Count;
                var fsw = Stopwatch.StartNew();
                for (int i = 0; i < 600; i++)
                {
                    TurretViews.FrameUpdate(s, site, rig.transform, null);
                }
                fsw.Stop();
                double headMs = fsw.Elapsed.TotalMilliseconds / 600.0;
                int resyncs = TurretViews.ResyncCount - resync0;
                mode.Open();
                mode.Select(L);
                mode.SetHover(s, HomeGridService.CorePivot(s));
                int computes0 = TurretViews.PlacementRangeComputes;
                var rsw = Stopwatch.StartNew();
                for (int i = 0; i < 600; i++)
                {
                    TurretViews.FrameUpdate(s, site, rig.transform, mode);
                }
                rsw.Stop();
                double ringMs = rsw.Elapsed.TotalMilliseconds / 600.0;
                int computes = TurretViews.PlacementRangeComputes - computes0;
                bool ring = TurretViews.RingShown && TurretViews.RingRadius > 0f;
                mode.Close();
                PerfLines.Add($"炮塔表现（{turrets.Count} 座）：首帧建头 + 绑定 {first.Elapsed.TotalMilliseconds:F2} ms；之后每帧 {headMs:F4} ms（600 帧结构对账 {resyncs} 次）；" +
                              $"放置预览每帧 {ringMs:F4} ms（600 帧射程半径重算 {computes} 次）");
                PerfGate.Expect(bound && resyncs == 0 && computes <= 1 + 600 / 60 && ring,
                    $"N2 炮塔表现每帧开销与炮塔数无关：{turrets.Count} 座炮塔头全部绑进地点画面同步（位置 / 朝向由 Burst 作业写，{bound}），结构不变的 600 帧对账 {resyncs} 次、每帧 {headMs:F4} ms；" +
                    $"建造模式悬停炮塔座 600 帧射程半径只重算 {computes} 次、每帧 {ringMs:F4} ms（阈值各 0.05 ms）",
                    new[] { PerfGate.Le(headMs, 0.05, "炮塔头每帧 ms"), PerfGate.Le(ringMs, 0.05, "放置射程圈每帧 ms") }, Expect, Line);
            }
            finally
            {
                mode?.Close();
                TurretViews.Clear();
                Object.DestroyImmediate(rig);
            }
            Clear(site);
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

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
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
