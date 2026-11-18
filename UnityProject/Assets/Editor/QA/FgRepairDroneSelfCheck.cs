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
    /// FG6-DEF-03 维修无人机与自动重建的自动验收（FG06 FGR-DEF-014 / 015；FGT-DEF-008；负向“重建时材料不足 / 站点本身被摧毁”；承接 DEBT-FG4ECO06-05、DEBT-FG3LOG03-05 / 06、DEBT-FG5RND01-01 防御最后两个节点）。
    /// 全部起真实系统：真实家园（世界模拟、电网、施工机器、传送带内核、工单）、家园战斗内核（无人机是内核里的己方单位，能被敌方弹体打下来）、真实建造模式与 HUD、真 UXML 规则面板、真文件存读档。
    /// A 数据；B 研发门控（站点 / 规则类型；旧档已启用的规则照常）；C 维修（正式施工建成 → 满编、范围、按比例收维修件、炮塔 / 屏障 / 传送带、挨打不丢、突袭优先、缺维修件、被击落与补充、站点被摧毁）；
    /// D 自动重建（区域开关、补排、按原设置、传送带虚影、材料不足不重复扣料、两条规则不重复派单、区域负向）；E 建造模式 / 规则面板 / 画面；F 存读档与读档接着跑；G 暂停与 0.5x～3x；H 观察 / 不观察；I 种子集；J 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgRepairDroneSelfCheck）。
    /// </summary>
    public static class FgRepairDroneSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 9;
        private const string Station = RepairDroneCatalog.StationTypeId;
        private const string Furnace = "refinery_furnace";
        private const string Kit = BuildingOps.RepairKitId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("QA/自检/FG 维修无人机与自动重建")]
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
            Line("\n[维修无人机与自动重建] 无人机站自动修理 / 突袭优先 / 击落补充 / 站点被毁；自动重建按区域开关、传送带虚影、材料不足不重复扣料（FG6-DEF-03）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgrepair-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；无人机的命中 / 阵亡在战斗内核（AOT），找目标 / 飞行 / 修理 / 重建规则在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckResearchGates);
                Step(CheckFormalBuildAndRepair);
                Step(CheckTargetKinds);
                Step(CheckRaidPriority);
                Step(CheckNoKits);
                Step(CheckNoKitsMidRepair);
                Step(CheckShotDownAndRespawn);
                Step(CheckStationDestroyed);
                Step(CheckZoneRebuild);
                Step(CheckBeltRebuild);
                Step(CheckMaterialShortage);
                Step(CheckRebuildWaitsForFire);
                Step(CheckUndergroundGhostWaitsForFire);
                Step(CheckRebuildQueueReserve);
                Step(CheckRebuildReserveUnattainable);
                Step(CheckFireAfterDispatchBuilding);
                Step(CheckFireAfterDispatchOnSite);
                Step(CheckFireAfterDispatchBelt);
                Step(CheckFireWaitSaveLoadAndIssues);
                Step(CheckLegacyIssueArg);
                Step(CheckZoneNegatives);
                Step(CheckBuildModeAndPanel);
                Step(CheckSaveLoad);
                Step(CheckLoadContinuation);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSeedSet);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"维修无人机自检抛异常：{e}");
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
                StandingRuleService.ResetForTests();
                RepairDroneService.ResetSessionState();
                RepairDroneViews.Clear();
                DefenseService.ResetSessionState();
                DefenseViews.Clear();
                TurretService.ResetSessionState();
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
                RulesPanelUIToolkit.InWorldOverrideForTests = false;
                RulesPanelUIToolkit.Close();
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
            Line($"  · [维修无人机与自动重建] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>一个有电的家园（发电机 2 两座），研发：维修无人机站、自动重建（以及屏障）已研究；仓库里放好维修件。按种子地形找空地，不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 2000, bool research = true, int kits = 40)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            SignalCoreService.ResetForTests();
            TestRangeService.ResetForTests();
            StandingRuleService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            SignalUplinkService.SilentNightProvider = null;
            _seq = 900;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            for (int i = 0; i < 2; i++)
            {
                GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
                if (g.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "rd_gen" + i, g.Value);
                }
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (research)
            {
                ResearchService.CompleteForTests(s, "defense.barrier", "defense.repair_drone", "defense.auto_rebuild");
            }
            if (HomeInventory.Capacity(s, ItemCatalog.Find(Kit)) < kits + 50)
            {
                GridCell? w = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 26f);
                if (w.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "rd_wh", w.Value);
                }
            }
            SetKits(s, kits);
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void SetKits(CampaignState s, int n)
        {
            ItemDef kit = ItemCatalog.Find(Kit);
            HomeInventory.RemoveUpTo(s, kit, HomeInventory.Stock(s, kit));
            if (n > 0)
            {
                HomeInventory.Add(s, kit, n, clampToSpace: false);
            }
        }

        private static int Kits(CampaignState s) => HomeInventory.Stock(s, Kit);

        /// <summary>测试捷径：直接登记一座建成、满耐久的建筑（真实放置 / 机器施工由 C1 覆盖）。</summary>
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

        /// <summary>在 <paramref name="center"/> 周围 [from, to] 米内按种子地形找空地登记一座（用电的要接得上电网且吃到电）。</summary>
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
                    float dist = Vector2.Distance(b.Position, center);
                    if (dist < from - 0.01f || dist > to + 0.01f || (powered && (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered)))
                    {
                        Unregister(s, b);
                        continue;
                    }
                    return b;
                }
            }
            Fail($"测试准备：{center} 周围 {from}～{to} 米找不到能放 {type} 的空地");
            return null;
        }

        private static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        /// <summary>一座有电、建成的维修无人机站（下一次找目标时满编）。</summary>
        private static BuildingRecord StationAt(CampaignState s, float from = 8f, float to = 24f)
        {
            BuildingRecord st = PlaceNear(s, Station, CoreCenter(s), from, to);
            if (st != null)
            {
                RepairDroneService.Sync(s);
                Seconds(1.1f); // 一次找目标：满编
            }
            return st;
        }

        private static RepairStationRecord Rec(CampaignState s, BuildingRecord st) => st != null ? RepairDroneService.Find(s, st.BuildingId) : null;

        private static int Out(RepairStationRecord r) => r?.Drones.Count(d => d.State != RepairDroneService.StateDocked) ?? 0;

        private static void Damage(CampaignState s, BuildingRecord b, float fraction) => BuildingOps.ApplyDamage(s, b.BuildingId, BuildingOps.MaxDurability(b.BuildingTypeId) * fraction);

        private static bool Full(CampaignState s, BuildingRecord b) => RepairDroneService.DurabilityOf(s, b) >= BuildingOps.MaxDurability(b.BuildingTypeId) - 0.05f;

        private static string Reason(CampaignState s, BuildingRecord b) => BuildingStatusService.Evaluate(s, b).Reason;

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        /// <summary>一个敌方射手：驻守开火，打真实飞行的弹体，射程内最近的己方单位。外部键 -1（测试结束清场）。</summary>
        private static int Shooter(CombatSite site, Vector2 at, float damage, float cooldown, float range)
        {
            int w = site.WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile,
                HasOutput = 1,
                Range = range,
                Damage = damage,
                Cooldown = cooldown,
                ProjectileSpeed = 30f,
                ProjectileRadius = 0.2f,
                ProjectileLife = 2f,
            });
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath | CombatUnitFlags.WeaponEnabled,
                Position = new double2(at.x, at.y),
                Home = new double2(at.x, at.y),
                Radius = 0.5f,
                Health = 100000f,
                MaxHealth = 100000f,
                Weapon = w,
                BehaviorProfile = -1,
                Priority = 1,
            });
        }

        private static void Clear(CombatSite site) => CombatBench.ClearPrototypeUnits(site);

        private static StandingRuleRecord NewRebuildRule(CampaignState s)
        {
            if (!StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord r, out string m))
            {
                Fail("测试准备：新建自动重建规则失败：" + m);
            }
            return r;
        }

        private static void ClearRules(CampaignState s)
        {
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s))
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
        }

        private static GridCell CellOf(BuildingRecord b) => new GridCell(b.GridX, b.GridY);

        /// <summary>离 <paramref name="center"/> [from, to] 米、能铺传送带的一格（按种子地形找）。</summary>
        private static GridCell? BeltCellNear(CampaignState s, Vector2 center, float from, float to)
        {
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 5f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(center.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(center.y + Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok && BeltNetworkService.HpOf(c) < 0)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(Station);
            GameConfig.fg.BuildingService svc = BuildingOps.Service(Station);
            bool building = g != null && g.FootprintW == 3 && g.FootprintH == 3 && g.Category == "defense" && svc != null && svc.RepairKits >= 1
                            && Mathf.Abs(BuildingOps.MaxDurability(Station) - 220f) < 0.01f && HomeValleyLayout.PowerProfile.ContainsKey(Station)
                            && HomeValleyLayout.BuildProfile.TryGetValue(Station, out (int ScrapCost, float Seconds) cost) && cost.ScrapCost == 70
                            && BuildCatalog.TryGet(Station, out BuildEntry e) && e.UnlockRule == "research:defense.repair_drone" && svc.CodexId == "codex.defense.repair_drone";
            Expect(building, $"A1 维修无人机站：3×3（FG04 建筑全表）、建造菜单防御分类、用电、耐久 220、维修件 {svc?.RepairKits}、造价 70 废料；由研发节点“防御 · 维修无人机站”解锁");

            bool nodes = ResearchCatalog.TryGet("defense.repair_drone", out ResearchNodeDef n1) && n1.IsReady && n1.Unlocks.Contains("build:" + Station)
                         && ResearchCatalog.TryGet("defense.auto_rebuild", out ResearchNodeDef n2) && n2.IsReady && n2.Unlocks.Contains("rule:auto_rebuild")
                         && n2.Prereqs.Contains("defense.repair_drone") && ResearchCatalog.GateOf("rule:auto_rebuild")?.Id == "defense.auto_rebuild"
                         && ResearchCatalog.Problems.Count == 0 && ResearchService.TargetName("rule:auto_rebuild").Contains("自动重建")
                         && ResearchText.UnlockLines(null, n2).Any(l => l.Text.Contains("常驻规则"))
                         && StandingRuleService.KindGateNode(StandingRuleService.KindRebuild) == "defense.auto_rebuild";
            Expect(nodes, "A2 研发树（DEBT-FG5RND01-01 防御最后两个节点）：维修无人机站节点解锁建造菜单条目；自动重建节点（前置维修无人机站）解锁常驻规则“自动重建”（unlocks = rule:auto_rebuild），节点详情写明在常驻规则面板新建；载入校验无问题");

            bool tuning = RepairDroneCatalog.Count == 3 && Mathf.Abs(RepairDroneCatalog.Range - 16f) < 0.01f && Mathf.Abs(RepairDroneCatalog.RespawnSeconds - 20f) < 0.01f
                          && RepairDroneCatalog.RespawnScrap == 6 && Mathf.Abs(RepairDroneCatalog.ScanSeconds - 1f) < 0.01f && RepairDroneCatalog.Hp > 0f
                          && GridContent.TryGetTuning("drone.repair_per_second", out _) && GridContent.TryGetTuning("rules.zone.max", out _) && StandingRuleService.ZoneMinCells == 4;
            Expect(tuning, $"A3 数值入表（fg.TbHomeTuning drone.* / rules.zone.*）：编制 {RepairDroneCatalog.Count} 架、范围 {RepairDroneCatalog.Range} 米、补充 {RepairDroneCatalog.RespawnSeconds} 秒 + {RepairDroneCatalog.RespawnScrap} 废料、找目标间隔 {RepairDroneCatalog.ScanSeconds} 秒、区域至少 {StandingRuleService.ZoneMinCells} 格");

            // 文本键：源码引用的 drone.* / bs.reason.drone_* / rules.zone* / ui.build.zone* / research.unlock.rule 中英都有。
            string root = FgProductionSelfCheck.LocateRepo();
            string[] files =
            {
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Defense/RepairDroneService.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/StandingRuleService.Rebuild.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyBuildMode.Zones.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/ResearchService.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/ResearchText.cs",
            };
            string src = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(root, f))));
            var keys = System.Text.RegularExpressions.Regex.Matches(src, "\"((?:drone\\.|bs\\.reason\\.drone|rules\\.zone|rules\\.msg\\.zone|rules\\.msg\\.kind|ui\\.build\\.zone|research\\.unlock|research\\.detail\\.unlock_rule|rules\\.log\\.rebuild_belt)[a-z_\\.]*)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Where(k => !k.EndsWith(".", StringComparison.Ordinal) && !GridContent.TryGetTuning(k, out _)).Distinct().ToList();
            var missing = keys.Where(k => string.IsNullOrEmpty(GameText.Get(k, GameLanguage.ZhCn)) || GameText.Get(k, GameLanguage.ZhCn).Contains("⟦")
                                          || string.IsNullOrEmpty(GameText.Get(k, GameLanguage.En)) || GameText.Get(k, GameLanguage.En).Contains("⟦")).ToList();
            Expect(keys.Count >= 20 && missing.Count == 0, $"A4 文本键（B16）：源码引用的 {keys.Count} 个键中英都有" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing.Take(10))));

            GameConfig.fg.NotifyType lost = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("drone_lost");
            GameConfig.fg.NotifyType kits = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("drone_kits");
            GameConfig.fg.NotifyType crashed = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("drone_crashed");
            GameConfig.fg.CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.defense.repair_drone");
            bool hooks = new[] { GuidanceHooks.DefenseRepairDroneFirstBuilt, GuidanceHooks.DefenseRepairDroneFirstRepair, GuidanceHooks.DefenseRepairDroneFirstLost,
                GuidanceHooks.RulesRebuildZoneFirstDrawn, GuidanceHooks.RulesRebuildFirstBelt }.All(GuidanceHooks.Known.Contains);
            Expect(lost != null && lost.Tier == "warning" && lost.AwaySection == "buildings" && kits != null && kits.Tier == "warning" && kits.AwaySection == "buildings" && crashed != null && crashed.AwaySection == "buildings"
                   && cx != null && cx.Hooks.Contains("defense.repair_drone_first_built") && hooks,
                "A5 通知类型 drone_lost / drone_crashed / drone_kits（警告级、聚合、可定位、进离家报告建筑段，B08 / B19）；图鉴“维修无人机站”由首次建成钩子解锁；五个引导钩子在册（内容在 FG15-UX-04）");
        }

        // ── B 研发门控 ─────────────────────────────────────────────────────────────

        private static void CheckResearchGates()
        {
            CampaignState s = NewWorld(6301, research: false);
            Func<bool> before = ResearchGate.TreeAvailableOverrideForTests;
            try
            {
                // 旧档：研发树开放前就建好并启用的自动重建规则（兼容开关下新建）。
                ResearchGate.TreeAvailableOverrideForTests = () => false;
                StandingRuleRecord legacy = NewRebuildRule(s);
                ResearchGate.TreeAvailableOverrideForTests = () => true;
                GridCell? c = FgProductionSelfCheck.FindFree(s, Station, 8f, 24f);
                GridPlacementResult locked = c.HasValue ? HomeGridService.ValidatePlacement(s, Station, c.Value, 0) : null;
                bool stationGate = locked != null && !locked.Ok && locked.Has(GridBlockReason.Locked) && locked.Describe().Contains("维修无人机站");
                bool created = StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out _, out string why);
                bool ruleGate = !created && why.Contains("防御 · 自动重建") && StandingRuleService.IsKindLocked(s, StandingRuleService.KindRebuild);
                Expect(stationGate && ruleGate,
                    $"B1 研发门控：没研究时放不下维修无人机站（“{locked?.Describe()}”）；不能新建自动重建规则，原因写明去研究哪个节点（“{why}”）");

                // 旧档里已经启用的规则照常运转：被摧毁的建筑照样派重建单；但停用后再启用被拒（写原因），研究完成后可以启用。
                BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 8f, 22f);
                Damage(s, f, 2f);
                Seconds(0.3f);
                bool keeps = legacy.Enabled && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) is WorkOrderRecord o && o.RuleSerial == legacy.Serial;
                StandingRuleService.TrySetEnabled(s, legacy.Serial, false, out _);
                bool refused = !StandingRuleService.TrySetEnabled(s, legacy.Serial, true, out string why2) && why2.Contains("防御 · 自动重建") && !legacy.Enabled;
                ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
                bool unlocked = StandingRuleService.TrySetEnabled(s, legacy.Serial, true, out _) && legacy.Enabled && !StandingRuleService.IsKindLocked(s, StandingRuleService.KindRebuild)
                                && c.HasValue && HomeGridService.ValidatePlacement(s, Station, c.Value, 0).Ok;
                Expect(keeps && refused && unlocked,
                    $"B2 旧档兼容：研发树开放前启用的自动重建规则照常运转（被摧毁的精炼炉派出重建单，由 R{legacy.Serial} 触发）（{keeps}）；停用后没研究不能再启用（“{why2}”）（{refused}）；研究完成后可以启用、可以放站点（{unlocked}）");
            }
            finally
            {
                ResearchGate.TreeAvailableOverrideForTests = before;
            }
        }

        // ── C 维修 ─────────────────────────────────────────────────────────────────

        private static void CheckFormalBuildAndRepair()
        {
            CampaignState s = NewWorld(6302, kits: 10);
            CombatSite site = Site;
            // 正式入口：玩家放置虚影 → 机器运料施工 → 建成那一刻满编（3 架停在站里，不进内核）。
            GridCell? c = FgProductionSelfCheck.FindFree(s, Station, 9f, 22f);
            GridOpResult placed = c.HasValue ? PlanHistory.Place(s, Station, c.Value, 0) : default;
            BuildingRecord st = placed.Success ? HomeGridService.FindBuilding(s, placed.BuildingId) : null;
            bool ghost = st != null && HomeValleyController.IsPlannedGhost(st);
            bool built = st != null && StepUntil(() => st.ConstructionState == BuildingConstructionState.Operational, 300);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1.1f);
            RepairStationRecord rec = Rec(s, st);
            bool full = built && rec != null && rec.Drones.Length == 3 && Out(rec) == 0 && site.DroneUnitCount == 0
                        && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseRepairDroneFirstBuilt) && Reason(s, st).Contains("待命");
            Expect(ghost && full, $"C1 正式入口：放下维修无人机站虚影（{ghost}），机器运料施工完工后满编 {rec?.Drones.Length} 架、都停在站里（内核里 {site?.DroneUnitCount} 架），状态“{(st != null ? Reason(s, st) : "无")}”，首次建成钩子");
            if (rec == null)
            {
                return;
            }

            // 范围内受损 → 无人机出动（内核里的己方单位）→ 修满 → 按修好的比例收维修件 → 返航停靠。范围外的不修（对照）。
            BuildingRecord near = PlaceNear(s, Furnace, st.Position, 5f, RepairDroneCatalog.Range - 3f);
            BuildingRecord far = PlaceNear(s, Furnace, st.Position, RepairDroneCatalog.Range + 6f, RepairDroneCatalog.Range + 16f);
            if (near == null || far == null)
            {
                return;
            }
            int kitsPerFull = BuildingOps.Service(Furnace).RepairKits;
            Damage(s, near, 0.5f);
            Damage(s, far, 0.5f);
            int kits0 = Kits(s);
            Seconds(1.1f);
            int unit = 0;
            DroneRecord flying = rec.Drones.FirstOrDefault(d => d.State != RepairDroneService.StateDocked);
            bool launched = flying != null && flying.Target == "b:" + near.BuildingId && site.TryGetDroneUnit(flying.Serial, out unit)
                            && site.Kernel.TryGetUnit(unit, out CombatUnitView v) && v.Faction == CombatFaction.Player && (v.Flags & CombatUnitFlags.Targetable) != 0 && (v.Flags & CombatUnitFlags.Instanced) != 0
                            && Out(rec) == 1;
            bool repairingSeen = StepUntil(() => rec.Drones.Any(d => d.State == RepairDroneService.StateRepairing), 20);
            string working = Reason(s, st);
            var rig = new GameObject("__fgrepair_views") { hideFlags = HideFlags.HideAndDontSave };
            int beams;
            try
            {
                RepairDroneViews.FrameUpdate(s, rig.transform, null);
                beams = RepairDroneViews.BeamCount;
            }
            finally
            {
                RepairDroneViews.Clear();
                Object.DestroyImmediate(rig);
            }
            bool repaired = StepUntil(() => Full(s, near), 60);
            bool docked = StepUntil(() => Out(rec) == 0 && site.DroneUnitCount == 0, 30);
            float expectKits = 0.5f * kitsPerFull;
            int taken = kits0 - Kits(s);
            bool paid = taken == Mathf.CeilToInt(expectKits - 1e-4f) && Mathf.Abs(rec.KitCredit - (taken - expectKits)) < 0.02f && rec.KitsUsed == taken && rec.Repaired > 0;
            bool farUntouched = !Full(s, far) && Mathf.Abs(BuildingOps.Durability(far) - BuildingOps.MaxDurability(Furnace) * 0.5f) < 0.5f;
            Expect(launched && repairingSeen && working.Contains("维修中") && beams >= 1,
                $"C2 范围内受损：下一次找目标派出 1 架（内核里的己方单位，可被命中，实例化画出），飞到后开始修理；状态“{working}”；修理光束 {beams} 条");
            Expect(repaired && docked && paid && farUntouched && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseRepairDroneFirstRepair),
                $"C3 修满（{repaired}）后返航停靠、退出内核（{docked}）；按修好的比例收维修件：修 50% × {kitsPerFull} 件 = {expectKits} 件 → 取走 {taken} 件、余额 {rec.KitCredit:F2}（不丢零头不多扣）（{paid}）；" +
                $"范围外（{RepairDroneCatalog.Range} 米外）的不修（{farUntouched}）；首次修理钩子");

            // 站点禁用：出动的返航、不再出动（FGR-BASE-020：玩家关了就停）。
            Damage(s, near, 0.4f);
            Seconds(1.1f);
            bool outAgain = Out(rec) >= 1;
            BuildingOps.TrySetEnabled(s, st.BuildingId, false, out _);
            Seconds(0.2f);
            bool returning = rec.Drones.All(d => d.State == RepairDroneService.StateDocked || d.State == RepairDroneService.StateReturning);
            bool stays = StepUntil(() => Out(rec) == 0, 30);
            float hpOff = BuildingOps.Durability(near);
            Seconds(5f);
            bool idleOff = Out(rec) == 0 && Mathf.Abs(BuildingOps.Durability(near) - hpOff) < 0.01f;
            BuildingOps.TrySetEnabled(s, st.BuildingId, true, out _);
            bool resumes = StepUntil(() => Full(s, near), 60);
            Expect(outAgain && returning && stays && idleOff && resumes,
                "C4 站点禁用：出动的无人机返航（不修到一半偷偷修完）、停靠后不再出动，目标耐久不再变化；重新启用后接着修满");
        }

        private static void CheckTargetKinds()
        {
            CampaignState s = NewWorld(6303, kits: 30);
            CombatSite site = Site;
            BuildingRecord st = StationAt(s);
            if (st == null)
            {
                return;
            }
            // 屏障（防御结构单位，耐久在内核）：维修直接加内核血量，对账间隔里挨的打不丢。
            BuildingRecord wall = PlaceNear(s, DefenseCatalog.BarrierT2, st.Position, 4f, 10f);
            BuildingRecord turret = PlaceNear(s, TurretCatalog.LightTypeId, st.Position, 4f, 12f);
            DefenseService.Sync(s);
            TurretService.Sync(s);
            DefenseRecord wr = DefenseService.Find(s, wall.BuildingId);
            float max = BuildingOps.MaxDurability(DefenseCatalog.BarrierT2);
            site.DamageDefense(wr.Serial, max * 0.5f);
            Seconds(0.6f); // 对账：建筑记录跟上内核
            site.DamageDefense(wr.Serial, 10f); // 还没对账的这一下
            site.TryGetDefenseHealth(wr.Serial, out float k0, out _, out _);
            float healed = DefenseService.Heal(s, wall, 5f);
            site.TryGetDefenseHealth(wr.Serial, out float k1, out _, out _);
            Seconds(0.6f);
            site.TryGetDefenseHealth(wr.Serial, out float k2, out _, out _);
            bool noLoss = Mathf.Abs(healed - 5f) < 0.01f && Mathf.Abs(k1 - (k0 + 5f)) < 0.01f && k2 <= k1 + RepairDroneCatalog.RepairPerSecond * 0.6f + 0.5f && k2 >= k1 - 0.01f
                          && Mathf.Abs(k0 - (max * 0.5f - 10f)) < 0.5f;
            Expect(noLoss, $"C5 屏障耐久在内核：对账后再挨 10 点（内核 {k0:F1}），维修 5 点 → 内核 {k1:F1}（加在内核读数上，不被建筑记录覆盖掉那 10 点伤害）；再对账后 {k2:F1}（只多了无人机这段时间修的）");
            bool wallFixed = StepUntil(() => site.TryGetDefenseHealth(wr.Serial, out float h, out _, out _) && h >= max - 0.05f, 60);

            // 炮塔（内核单位）。
            TurretRecord tr = TurretService.Find(s, turret.BuildingId);
            float tmax = BuildingOps.MaxDurability(TurretCatalog.LightTypeId);
            bool hasUnit = tr != null && site.TryGetTurretUnit(tr.Serial, out _);
            if (hasUnit)
            {
                site.SetTurretHealth(tr.Serial, tmax * 0.4f, tmax);
                Seconds(0.6f);
            }
            bool turretFixed = hasUnit && StepUntil(() => TurretService.DurabilityOf(s, turret) >= tmax - 0.05f, 90);

            // 传送带格（承接 DEBT-FG3LOG03-05）。
            GridCell? area = BeltCellNear(s, st.Position, 4f, RepairDroneCatalog.Range - 2f);
            bool beltFixed = false;
            int beltMax = 0, beltHp0 = 0;
            float credit0 = 0f;
            int used0 = 0;
            if (area.HasValue)
            {
                BeltNetworkService.TryPlace(s, area.Value, BeltDir.East, 0);
                beltMax = BeltNetworkService.MaxHp(0);
                BeltNetworkService.TryDamage(s, area.Value, beltMax / 2, out _);
                beltHp0 = BeltNetworkService.HpOf(area.Value);
                RepairStationRecord rec = Rec(s, st);
                StepUntil(() => wallFixed && Out(rec) == 0, 30);
                credit0 = rec.KitCredit;
                used0 = rec.KitsUsed;
                beltFixed = StepUntil(() => BeltNetworkService.HpOf(area.Value) == beltMax && BeltNetworkService.DamageOf(area.Value) == 0, 60);
            }
            RepairStationRecord r2 = Rec(s, st);
            float beltKits = (r2.KitsUsed - used0) + credit0 - r2.KitCredit;
            float expectBelt = (beltMax - beltHp0) / (float)Math.Max(1, beltMax) * RepairDroneCatalog.BeltRepairKits;
            Expect(wallFixed && turretFixed && beltFixed && Mathf.Abs(beltKits - expectBelt) < 0.02f,
                $"C6 FGR-DEF-014“建筑、炮塔和屏障”：屏障（内核结构单位）修满（{wallFixed}）、炮塔（内核炮塔单位）修满（{turretFixed}）、受损的传送带格 {beltHp0}/{beltMax} 修满（{beltFixed}，DEBT-FG3LOG03-05），" +
                $"传送带按 {RepairDroneCatalog.BeltRepairKits} 件 / 格记账（用了 {beltKits:F3} 件，应为 {expectBelt:F3}）");
        }

        private static void CheckRaidPriority()
        {
            string Run(bool raid, out string attackedKey)
            {
                CampaignState s = NewWorld(6304, kits: 40);
                StandingRuleService.RaidActiveProvider = _ => raid;
                BuildingRecord st = StationAt(s);
                attackedKey = string.Empty;
                if (st == null)
                {
                    return "no-station";
                }
                var targets = new List<BuildingRecord>();
                for (int i = 0; i < 4; i++)
                {
                    BuildingRecord b = PlaceNear(s, DefenseCatalog.BarrierT3, st.Position, 4f + i * 2f, 12f);
                    if (b != null)
                    {
                        targets.Add(b);
                    }
                }
                if (targets.Count < 4)
                {
                    return "no-targets";
                }
                // 三座伤得重（没在挨打），一座伤得轻但刚挨过打。
                float max = BuildingOps.MaxDurability(DefenseCatalog.BarrierT3);
                for (int i = 0; i < 3; i++)
                {
                    targets[i].Health = max * (0.3f + 0.05f * i);
                    targets[i].LastHitTick = 0;
                }
                targets[3].Health = max * 0.9f;
                targets[3].LastHitTick = GameClock.Ticks;
                attackedKey = "b:" + targets[3].BuildingId;
                DefenseService.Sync(s);
                Seconds(1.05f);
                RepairStationRecord rec = Rec(s, st);
                return string.Join(",", rec.Drones.Select(d => d.Target).Where(t => t.Length > 0).OrderBy(t => t, StringComparer.Ordinal));
            }
            string raidOn = Run(true, out string hitKey);
            string raidOff = Run(false, out string hitKey2);
            StandingRuleService.RaidActiveProvider = null;
            bool on = raidOn.Contains(hitKey) && raidOn.Split(',').Length == 3;
            bool off = !raidOff.Contains(hitKey2) && raidOff.Split(',').Length == 3;
            Expect(on && off, $"C7 FGR-DEF-014“突袭时优先修理正在受攻击的目标”：突袭中 3 架无人机里有一架先去修刚挨打（伤得最轻）的那座（{on}）；没有突袭时按耐久比例低的先修，不去修它（对照，{off}）");

            // 突袭中正修着不挨打目标的无人机会被调去修新挨打的目标（不等修完）。
            CampaignState s2 = NewWorld(6305, kits: 40);
            StandingRuleService.RaidActiveProvider = _ => true;
            BuildingRecord st2 = StationAt(s2);
            var t2 = new List<BuildingRecord>();
            for (int i = 0; i < 4 && st2 != null; i++)
            {
                BuildingRecord b = PlaceNear(s2, DefenseCatalog.BarrierT3, st2.Position, 4f + i * 2f, 12f);
                if (b != null)
                {
                    t2.Add(b);
                }
            }
            bool switched = false;
            if (t2.Count == 4)
            {
                float max = BuildingOps.MaxDurability(DefenseCatalog.BarrierT3);
                for (int i = 0; i < 3; i++)
                {
                    t2[i].Health = max * 0.2f;
                }
                DefenseService.Sync(s2);
                Seconds(1.05f);
                RepairStationRecord rec2 = Rec(s2, st2);
                bool allBusy = rec2.Drones.Count(d => d.Target.Length > 0) == 3;
                DefenseRecord dr = DefenseService.Find(s2, t2[3].BuildingId);
                Site.DamageDefense(dr.Serial, max * 0.3f); // 新挨打（内核扣血，对账时记下“挨打”）
                Seconds(1.6f);
                switched = allBusy && rec2.Drones.Any(d => d.Target == "b:" + t2[3].BuildingId);
            }
            StandingRuleService.RaidActiveProvider = null;
            Expect(switched, "C8 突袭中三架都在修不挨打的目标时，第四座刚挨打 → 下一次找目标就调一架过去（不等修完手上的）");
        }

        private static void CheckNoKits()
        {
            CampaignState s = NewWorld(6306, kits: 0);
            BuildingRecord st = StationAt(s);
            BuildingRecord f = st != null ? PlaceNear(s, Furnace, st.Position, 5f, 12f) : null;
            if (f == null)
            {
                return;
            }
            int n0 = NotifyCount("drone_kits");
            Damage(s, f, 0.5f);
            Seconds(1.1f);
            RepairStationRecord rec = Rec(s, st);
            BuildingStatus status = BuildingStatusService.Evaluate(s, st);
            bool waits = Out(rec) == 0 && rec.NoKits && status.Kind == BuildingStatusKind.NoMaterial && status.Reason.Contains("缺维修件") && status.Reason.Contains("零件工坊")
                         && NotifyCount("drone_kits") == n0 + 1 && LastNotify("drone_kits").Contains("维修件");
            Seconds(10f);
            bool noSpam = NotifyCount("drone_kits") == n0 + 1 && Out(rec) == 0;
            SetKits(s, 5);
            bool resumes = StepUntil(() => Full(s, f), 60) && !rec.NoKits;
            Expect(waits && noSpam && resumes,
                $"C9 负向“仓库没有维修件”：无人机不出动，站点状态“{status.Reason}”（缺料类，写明去零件工坊生产），发一次警告（10 秒内不刷屏）；放进维修件后下一次找目标就出动并修满");
        }

        /// <summary>
        /// 复审修复（P1）：维修件在修理中途用完——站里只剩不够修一步的零头余额。无人机返航停靠后不再被派出去来回空飞，目标耐久不变，“缺维修件”保持；
        /// 目标被修好（范围里没有受损目标）后放回维修件，下一次找目标就撤掉“缺维修件”（P2：不等真扣到件）；再受损就照常出动修满。
        /// </summary>
        private static void CheckNoKitsMidRepair()
        {
            CampaignState s = NewWorld(6316, kits: 0);
            BuildingRecord st = StationAt(s);
            BuildingRecord f = st != null ? PlaceNear(s, Furnace, st.Position, 5f, 12f) : null;
            if (f == null)
            {
                return;
            }
            RepairStationRecord rec = Rec(s, st);
            float max = BuildingOps.MaxDurability(Furnace);
            SetKits(s, 1);
            int kitsAtStart = Kits(s);
            Damage(s, f, 0.9f); // 精炼炉修满 2 件：修 90% 要 1.8 件，仓库只有 1 件 → 中途用完
            bool started = StepUntil(() => rec.KitsUsed >= 1, 20);
            bool ranOut = StepUntil(() => rec.NoKits, 40);
            bool docked = StepUntil(() => Out(rec) == 0, 30);
            // 最坏情况：浮点扣件后站里留下“大于 1e-4、却不够修一步”的零头（审查员指出的旧版死循环条件）。这里把余额钉在半步的费用上。
            float stepCost = BuildingOps.Service(Furnace).RepairKits / max * RepairDroneCatalog.RepairPerSecond / Mathf.Max(1, GameClock.StepHz);
            rec.KitCredit = Mathf.Max(rec.KitCredit, stepCost * 0.5f);
            float hp0 = RepairDroneService.DurabilityOf(s, f);
            int assigns0 = RepairDroneService.AssignCount;
            bool relaunched = StepUntil(() => Out(rec) > 0, 10);
            float hp1 = RepairDroneService.DurabilityOf(s, f);
            BuildingStatus status = BuildingStatusService.Evaluate(s, st);
            bool stopped = kitsAtStart == 1 && started && ranOut && docked && !relaunched && RepairDroneService.AssignCount == assigns0 && Mathf.Abs(hp1 - hp0) < 0.01f
                           && hp1 < max - 1f && hp1 > max * 0.1f + 1f && rec.NoKits && Kits(s) == 0 && status.Kind == BuildingStatusKind.NoMaterial;
            Expect(stopped,
                $"C9b 负向“维修件修到一半用完”：用掉 {rec.KitsUsed} 件后余额 {rec.KitCredit:F5}（一步要 {stepCost:F5}）不够修一步 → 返航停靠（{docked}），之后 10 秒不再出动（{!relaunched}、派单 {RepairDroneService.AssignCount - assigns0} 次），" +
                $"耐久停在 {hp1:F1}/{max:F0}，状态“{status.Reason.Split('\n')[0]}”（开始时仓库 {kitsAtStart} 件，现在 {Kits(s)} 件）");

            f.Health = max; // 目标被别的途径修好：范围里没有受损目标
            BuildingOps.Touch();
            SetKits(s, 2);
            Seconds(1.1f);
            bool cleared = !rec.NoKits && BuildingStatusService.Evaluate(s, st).Kind != BuildingStatusKind.NoMaterial;
            Damage(s, f, 0.5f);
            bool resumes = StepUntil(() => Full(s, f), 60) && !rec.NoKits;
            Expect(cleared && resumes,
                $"C9c 放回维修件时范围里没有受损目标：下一次找目标就撤掉“缺维修件”（{cleared}，状态“{Reason(s, st).Split('\n')[0]}”）；再受损照常出动修满（{resumes}）");
        }

        private static void CheckShotDownAndRespawn()
        {
            CampaignState s = NewWorld(6307, kits: 30, scrap: 200);
            CombatSite site = Site;
            BuildingRecord st = StationAt(s);
            BuildingRecord f = st != null ? PlaceNear(s, Furnace, st.Position, 8f, 13f) : null;
            if (f == null)
            {
                return;
            }
            RepairStationRecord rec = Rec(s, st);
            // 真实敌方弹体：射手站在目标旁（射程只够到目标附近），无人机飞到后被打下来。
            Shooter(site, f.Position + (st.Position - f.Position).normalized * 3.5f, 25f, 0.3f, 4f);
            int n0 = NotifyCount("drone_lost");
            Damage(s, f, 0.9f);
            bool lost = StepUntil(() => rec.Lost >= 1, 40);
            Clear(site);
            bool removed = lost && rec.Drones.Length <= 2 && site.DroneSerials().All(serial => rec.Drones.Any(d => d.Serial == serial))
                           && NotifyCount("drone_lost") >= n0 + 1 && LastNotify("drone_lost").Contains("被击落") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseRepairDroneFirstLost);
            Expect(removed, $"C10 无人机是可见的实体、能被击落：敌方射手的真实弹体把飞到目标旁的无人机打下来（损失 {rec.Lost}），从站里拿掉、内核单位清掉，警告“{LastNotify("drone_lost")}”，首次击落钩子");

            // 补充：开始时扣 6 废料，20 游戏秒（只在运转且有电时计时）后补回一架；断电期间计时暂停。
            StepUntil(() => rec.RespawnTicks >= 0, 5);
            int scrapAfterStart = s.Scrap;
            int paid = rec.RespawnPaid;
            Seconds(10f);
            int ticksMid = rec.RespawnTicks;
            BuildingOps.TrySetEnabled(s, st.BuildingId, false, out _);
            Seconds(5f);
            bool paused = rec.RespawnTicks == ticksMid;
            BuildingOps.TrySetEnabled(s, st.BuildingId, true, out _);
            int need = Mathf.RoundToInt(RepairDroneCatalog.RespawnSeconds * GameClock.StepHz);
            bool refilled = StepUntil(() => rec.Drones.Length == 3 || (rec.Drones.Length + rec.Lost >= 4 && rec.RespawnTicks < 0), 30);
            string respawnLine = Reason(s, st);
            Expect(paid == 6 && paused && refilled && s.Scrap <= scrapAfterStart,
                $"C11 被击落后由站点重新生产：开始补充时扣 {paid} 废料，计时 {need} 步（{RepairDroneCatalog.RespawnSeconds} 秒），禁用期间计时不走（{paused}），恢复后补满（{rec.Drones.Length} 架）");

            // 负向：废料不够补充 → 等着、不扣、状态写明要多少；够了自动开始。
            DroneRecord d0 = rec.Drones.FirstOrDefault(d => d.State == RepairDroneService.StateDocked);
            Damage(s, f, 0.5f);
            StepUntil(() => rec.Drones.Any(d => d.State != RepairDroneService.StateDocked), 5);
            DroneRecord outDrone = rec.Drones.FirstOrDefault(d => d.State != RepairDroneService.StateDocked);
            s.Scrap = 2;
            if (outDrone != null)
            {
                site.DamageDrone(outDrone.Serial, 10000f);
            }
            Seconds(1.2f);
            bool shortWait = outDrone != null && rec.RespawnTicks < 0 && s.Scrap == 2 && Reason(s, st).Contains("补充无人机缺废料");
            s.Scrap = 100;
            Seconds(0.2f);
            bool starts = rec.RespawnTicks >= 0 && s.Scrap == 94;
            Expect(shortWait && starts, $"C12 负向“补充无人机缺废料”：废料 2 < 6 时不开始、不扣，状态“{respawnLine.Replace("\n", " / ")}”写明要多少；补到 100 后下一步自动开始（扣 6 → 94）");
        }

        private static void CheckStationDestroyed()
        {
            CampaignState s = NewWorld(6308, kits: 30, scrap: 800);
            CombatSite site = Site;
            BuildingRecord st = StationAt(s);
            if (st == null)
            {
                return;
            }
            RepairStationRecord rec = Rec(s, st);
            var targets = new List<BuildingRecord>();
            for (int i = 0; i < 2; i++)
            {
                BuildingRecord b = PlaceNear(s, Furnace, st.Position, 6f + i * 4f, 14f);
                if (b != null)
                {
                    Damage(s, b, 0.8f);
                    targets.Add(b);
                }
            }
            // 一架被击落、补充中（已扣 6 废料），两架出动中。
            Seconds(1.2f);
            DroneRecord victim = rec.Drones.FirstOrDefault(d => d.State != RepairDroneService.StateDocked);
            if (victim != null)
            {
                site.DamageDrone(victim.Serial, 10000f);
            }
            StepUntil(() => rec.RespawnTicks >= 0, 5);
            Seconds(1.2f);
            int scrapMid = s.Scrap;
            int outBefore = Out(rec);
            int lostBefore = rec.Lost;
            int n0 = NotifyCount("drone_crashed");
            // 自动重建规则：圈一个区域盖住站点（站点被毁后按原样重建）。
            StandingRuleRecord rule = NewRebuildRule(s);
            StandingRuleService.TryAddZone(s, rule.Serial, new GridCell(st.GridX - 3, st.GridY - 3), new GridCell(st.GridX + 3, st.GridY + 3), out _, out _);
            Seconds(0.2f);
            BuildingOps.ApplyDamage(s, st.BuildingId, 10000f);
            bool crashed = st.ConstructionState == BuildingConstructionState.Damaged && rec.Wrecked && rec.Drones.Length == 0 && site.DroneUnitCount == 0
                           && rec.Lost == lostBefore + 2 && NotifyCount("drone_crashed") == n0 + 1 && LastNotify("drone_crashed").Contains("坠毁")
                           && s.Scrap == scrapMid + 6 && rec.RespawnTicks < 0;
            Expect(outBefore >= 1 && crashed,
                $"C13 负向“站点本身被摧毁”：出动中的 {outBefore} 架无人机连同站里的一起坠毁（内核里 {site.DroneUnitCount} 架、损失 {rec.Lost - lostBefore}），警告“{LastNotify("drone_crashed")}”；正在补充那一架的 6 废料全额退回（{scrapMid} → {s.Scrap}）");
            bool ordered = StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, st.BuildingId) is WorkOrderRecord o && o.RuleSerial == rule.Serial, 5);
            bool rebuilt = StepUntil(() => st.ConstructionState == BuildingConstructionState.Operational, 300);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1.2f);
            RepairStationRecord after = Rec(s, st);
            bool back = rebuilt && after != null && !after.Wrecked && after.Drones.Length == 3 && after.Drones.All(d => d.Hp >= RepairDroneCatalog.Hp - 0.01f);
            bool working = StepUntil(() => targets.All(t => Full(s, t)), 120);
            Expect(ordered && back && working,
                $"C14 FG06 第 5 节“自动重建规则开启时，站点会被重建”：重建区域盖住站点的自动重建规则派出重建单（由 R{rule.Serial} 触发）（{ordered}），机器重建完工后无人机满编回来（{after?.Drones.Length} 架）（{back}），接着把受损的修满（{working}）");
        }

        // ── D 自动重建 ─────────────────────────────────────────────────────────────

        private static void CheckZoneRebuild()
        {
            CampaignState s = NewWorld(6309, scrap: 1500);
            ClearRules(s);
            BuildingRecord a = PlaceNear(s, Furnace, CoreCenter(s), 9f, 14f);
            BuildingRecord b = a != null ? PlaceNear(s, Furnace, a.Position, 14f, 22f) : null;
            BuildingRecord trap = a != null ? PlaceNear(s, DefenseCatalog.TrapTypeId, a.Position, 3f, 6f) : null;
            if (a == null || b == null || trap == null)
            {
                return;
            }
            // 设置：改名、禁用；陷阱装固件 + 区域铺设。被摧毁后虚影保留设置，重建按原样（禁用的仍禁用）。
            BuildingOps.TryRename(s, a.BuildingId, "一号炉", out _);
            BuildingOps.TrySetEnabled(s, a.BuildingId, false, out _);
            if (!MechanicalContentUnlock.IsUnlocked(s, "fw_arcchain"))
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append("fw_arcchain").ToArray();
            }
            DefenseService.Sync(s);
            DefenseService.TrySetTrapFirmware(s, trap.BuildingId, "fw_arcchain");
            DefenseService.TrySetTrapPattern(s, trap.BuildingId, DefenseCatalog.PatternArea);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.BoundsOf(a, out GridCell amin, out GridCell amax);
            StandingRuleService.BoundsOf(trap, out GridCell tmin, out GridCell tmax);
            bool zoned = StandingRuleService.TryAddZone(s, r.Serial, new GridCell(Math.Min(amin.X, tmin.X) - 1, Math.Min(amin.Y, tmin.Y) - 1),
                new GridCell(Math.Max(amax.X, tmax.X) + 1, Math.Max(amax.Y, tmax.Y) + 1), out RebuildZoneRecord zone, out string zmsg);
            bool bOutside = !StandingRuleService.Covers(r, Furnace, CellOf(b), CellOf(b));
            Seconds(0.2f);
            BuildingOps.ApplyDamage(s, a.BuildingId, 10000f);
            BuildingOps.ApplyDamage(s, b.BuildingId, 10000f);
            DefenseRecord trec = DefenseService.Find(s, trap.BuildingId);
            Site.DamageDefense(trec.Serial, 10000f);
            Seconds(0.3f);
            WorkOrderRecord oa = HomeValleyWorkOrders.FindActiveRepair(s, a.BuildingId);
            bool ghosts = a.ConstructionState == BuildingConstructionState.Damaged && b.ConstructionState == BuildingConstructionState.Damaged && trap.ConstructionState == BuildingConstructionState.Damaged;
            bool orders = oa != null && oa.RuleSerial == r.Serial && HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) == null
                          && HomeValleyWorkOrders.FindActiveRepair(s, trap.BuildingId) is WorkOrderRecord ot && ot.RuleSerial == r.Serial
                          && StandingRuleService.DescribeBuilding(s, a.BuildingId).Contains("R" + r.Serial);
            Expect(zoned && bOutside && ghosts && orders,
                $"D1 FGT-DEF-008 自动重建按区域：区域 Z{zone?.Serial}（{zmsg}）盖住一号炉与陷阱、不盖另一座精炼炉；三座都被摧毁留下虚影（{ghosts}）；区域里的两座下一个模拟步派出重建单（写“由规则 R{r.Serial} 触发”），区域外的只留虚影（{orders}）");
            bool rebuilt = StepUntil(() => a.ConstructionState != BuildingConstructionState.Damaged && trap.ConstructionState != BuildingConstructionState.Damaged
                                           && HomeValleyWorkOrders.FindActiveRepair(s, a.BuildingId) == null, 300);
            DefenseRecord trec2 = DefenseService.Find(s, trap.BuildingId);
            bool settings = rebuilt && a.ConstructionState == BuildingConstructionState.Disabled && a.CustomName == "一号炉" && Mathf.Abs(a.Health - BuildingOps.MaxDurability(Furnace)) < 0.01f
                            && trec2 != null && trec2.TrapFirmware == "fw_arcchain" && trec2.TrapPattern == DefenseCatalog.PatternArea && b.ConstructionState == BuildingConstructionState.Damaged;
            Expect(settings, $"D2 材料到位后由机器按原设置重建：一号炉名字保留、被摧毁前是禁用的重建后仍禁用、耐久满；陷阱的固件（电弧链）与铺设方式（一片区域）保留；区域外的仍是虚影（{settings}）");

            // 区域开关：关掉后被摧毁的不重建；打开后补排（玩家显式打开 = 要求重建区域里已经被摧毁的）。
            StandingRuleService.TrySetZoneEnabled(s, r.Serial, zone.Serial, false, out _);
            BuildingOps.TrySetEnabled(s, a.BuildingId, true, out _);
            BuildingOps.ApplyDamage(s, a.BuildingId, 10000f);
            Seconds(1f);
            bool offIgnored = a.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.FindActiveRepair(s, a.BuildingId) == null
                              && StandingRuleService.ZoneSummary(r).Contains("都关着");
            int sweeps = StandingRuleService.SweepCount;
            StandingRuleService.TrySetZoneEnabled(s, r.Serial, zone.Serial, true, out string onMsg);
            Seconds(0.2f);
            bool swept = StandingRuleService.SweepCount == sweeps + 1 && HomeValleyWorkOrders.FindActiveRepair(s, a.BuildingId) is WorkOrderRecord o2 && o2.RuleSerial == r.Serial
                         && HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) == null;
            // 启用一条“整个家园”的规则 = 区域外那座也补排（承接 DEBT-FG4ECO06-05：规则启用前已经摧毁的不再要手动点）。
            StandingRuleRecord whole = NewRebuildRule(s);
            Seconds(0.2f);
            bool wholeSwept = HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) is WorkOrderRecord ob && ob.RuleSerial == whole.Serial;
            Expect(offIgnored && swept && wholeSwept,
                $"D3 区域开关：关掉的区域里被摧毁的不重建（面板写“区域都关着”）（{offIgnored}）；打开后下一个模拟步补排区域里已经被摧毁的（“{onMsg}”）（{swept}）；新建整个家园的规则补排区域外那座（DEBT-FG4ECO06-05）（{wholeSwept}）");

            // 复审修复（P2 补排触发面）：玩家取消了重建单（退料、留虚影）之后，去改别的规则——新建库存维持、改它的阈值、调自动重建规则的优先级、关掉一个区域——
            // 都不补排、不把那座重新派单（FGR-BASE-020：玩家没要求的事不做）。
            bool cancelled = BuildingOps.TryCancelRepair(s, b.BuildingId, out _) && HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) == null;
            int sweeps2 = StandingRuleService.SweepCount;
            StandingRuleService.TryCreate(s, StandingRuleService.KindStock, out StandingRuleRecord stockRule, out _);
            bool thresholdEdited = stockRule != null && StandingRuleService.TrySetThreshold(s, stockRule.Serial, stockRule.Threshold, out _);
            StandingRuleService.TryMove(s, whole.Serial, -1, out _);
            StandingRuleService.TrySetZoneEnabled(s, r.Serial, zone.Serial, false, out _);
            Seconds(1f);
            bool notRedispatched = cancelled && thresholdEdited && StandingRuleService.SweepCount == sweeps2
                                   && b.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) == null;
            Expect(notRedispatched,
                $"D3b 负向“取消重建单后改别的规则”：取消（{cancelled}）后新建库存维持并改阈值（{thresholdEdited}）、调自动重建规则优先级、关掉区域，补排次数不变（{StandingRuleService.SweepCount - sweeps2} 次）、那座仍是虚影且没有重建单");
        }

        private static void CheckBeltRebuild()
        {
            CampaignState s = NewWorld(6310, scrap: 1500);
            ClearRules(s);
            GridCell? area = FgProductionSelfCheck.FindArea(s, 3, 1, 9f, 20f);
            if (!area.HasValue)
            {
                Fail("D4 测试准备：找不到 3×1 的空地");
                return;
            }
            GridCell c0 = area.Value, c1 = FgProductionSelfCheck.At(area.Value, 1, 0), c2 = FgProductionSelfCheck.At(area.Value, 2, 0);
            BeltNetworkService.TryPlace(s, c0, BeltDir.East, 1);
            BeltNetworkService.TryPlace(s, c1, BeltDir.East, 1);
            BeltNetworkService.TryPlace(s, c2, BeltDir.North, 1);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _); // 范围只有精炼炉：传送带不重建
            int hp = BeltNetworkService.MaxHp(1);
            BeltNetworkService.TryDamage(s, c2, hp, out _);
            Seconds(0.3f);
            PlannedBeltRecord ghost = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).FirstOrDefault(p => p.Destroyed && p.Xs[0] == c2.X && p.Ys[0] == c2.Y);
            bool notInScope = ghost != null && ghost.Destroyed;
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out string addMsg);
            Seconds(0.3f);
            WorkOrderRecord o = ghost != null ? HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + ghost.PlanId) : null;
            bool ordered = ghost != null && !ghost.Destroyed && o != null && o.RuleSerial == r.Serial
                           && StandingRuleService.LogEntries(s).Any(l => l.Key == "rules.log.rebuild_belt" && l.Rule == r.Serial && l.HasPos);
            bool rebuilt = StepUntil(() => BeltNetworkService.HpOf(c2) == hp, 120);
            bool sameSettings = rebuilt && BeltNetworkService.IsRunning && BeltNetworkService.Kernel.TryGetCellInfo(c2.X, c2.Y, out BeltCellInfo info) && info.Dir == BeltDir.North && info.Tier == 1;
            // 一格中间的被毁：范围里有传送带时，摧毁的下一个模拟步就派单（事件路径，不靠补排）。
            BeltNetworkService.TryDamage(s, c1, hp, out _);
            Seconds(0.3f);
            bool direct = !(s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Any(p => p.Destroyed && p.Xs[0] == c1.X && p.Ys[0] == c1.Y)
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RulesRebuildFirstBelt);
            bool direct2 = StepUntil(() => BeltNetworkService.HpOf(c1) == hp, 120) && BeltNetworkService.Kernel.TryGetCellInfo(c1.X, c1.Y, out BeltCellInfo i1) && i1.Dir == BeltDir.East;
            Expect(notInScope && ordered && sameSettings && direct && direct2,
                $"D4 FGR-DEF-015 被摧毁的传送带：范围只含精炼炉时虚影留着（{notInScope}）；加上“传送带与物流节点”（{addMsg}）后补排、开施工单（由 R{r.Serial} 触发，日志可定位）（{ordered}），机器按原设置（朝北、T2）重建（{sameSettings}）；" +
                $"范围里的传送带再被摧毁时下一个模拟步直接派单（{direct}）并重建（{direct2}）（DEBT-FG3LOG03-06）");
        }

        private static void CheckMaterialShortage()
        {
            CampaignState s = NewWorld(6311, scrap: 0);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 9f, 20f);
            GridCell? area = FgProductionSelfCheck.FindArea(s, 1, 1, 9f, 20f);
            if (f == null || !area.HasValue)
            {
                return;
            }
            BeltNetworkService.TryPlace(s, area.Value, BeltDir.East, 0);
            // 两条规则都只管同一个区域（盖住精炼炉与这一格传送带；开局预置的残骸不在区域里）：只派一张单。
            StandingRuleService.BoundsOf(f, out GridCell fmin, out GridCell fmax);
            var zmin = new GridCell(Math.Min(fmin.X, area.Value.X) - 1, Math.Min(fmin.Y, area.Value.Y) - 1);
            var zmax = new GridCell(Math.Max(fmax.X, area.Value.X) + 1, Math.Max(fmax.Y, area.Value.Y) + 1);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleRecord r2 = NewRebuildRule(s);
            StandingRuleService.TryAddZone(s, r.Serial, zmin, zmax, out _, out _);
            StandingRuleService.TryAddZone(s, r2.Serial, zmin, zmax, out _, out _);
            int others = s.BuildingRecords.Count(x => x != f && x.ConstructionState == BuildingConstructionState.Damaged
                                                       && StandingRuleService.Covers(r, x.BuildingTypeId, CellOf(x), CellOf(x)));
            int cost = BuildingOps.RebuildCost(f, out _);
            Seconds(0.3f);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            BeltNetworkService.TryDamage(s, area.Value, BeltNetworkService.MaxHp(0), out _);
            Seconds(0.3f);
            bool waits = others == 0 && f.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null && s.Scrap == 0
                         && r.IssueKey == "rules.issue.rebuild_wait" && StandingRuleService.IssueText(r).Contains(cost.ToString(CultureInfo.InvariantCulture));
            PlannedBeltRecord ghost = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).FirstOrDefault(p => p.Xs[0] == area.Value.X && p.Ys[0] == area.Value.Y);
            WorkOrderRecord bo = ghost != null ? HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + ghost.PlanId) : null;
            Seconds(5f);
            bool beltWaits = bo != null && bo.State != WorkOrderState.Completed && BeltNetworkService.HpOf(area.Value) < 0 && s.Scrap == 0;
            Expect(waits && beltWaits,
                $"D5 负向“重建时材料不足”：建筑虚影等着、不派单、不扣料，规则写明原因“{StandingRuleService.IssueText(r)}”（{waits}）；传送带虚影开了施工单但等料（{bo?.State}），废料一直是 0（{beltWaits}）");
            int give = cost + 40;
            s.Scrap = give;
            bool once = StepUntil(() => f.ConstructionState != BuildingConstructionState.Damaged && BeltNetworkService.HpOf(area.Value) > 0, 300);
            Seconds(1f);
            int beltCost = HomeGridService.BeltCostPerCell(0);
            int orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.TargetId == f.BuildingId && o.Kind == WorkOrderKind.Repair);
            bool noDouble = once && s.Scrap == give - cost - beltCost && orders == 1 && r.IssueKey.Length == 0;
            Expect(noDouble,
                $"D6 材料到位后各重建一次：废料 {give} → {s.Scrap}（= 减去建筑重建造价 {cost} 与一格传送带 {beltCost}，不重复扣料），这座建筑只有 1 张重建单（两条规则都覆盖也不重复派）（{noDouble}）");
        }

        /// <summary>
        /// EMG-R 出口补缺口（FG-GAP-224；FGR-DEF-015“材料到位后由机器按原样重建”、FGR-BASE-020 自动动作可追溯）：被摧毁的建筑 / 传送带所在的格子还在烧
        /// （会烧蚀设施的物质斑块），自动重建不派单、不扣料、不把机器派进火里，写明“等火灭”（规则日志、规则原因、建筑面板、施工队列）；还在烧时的重查不重复写日志；
        /// 火灭了在下一次定时检查照常派单。负向：不伤设施的物质（水，structDps = 0）盖住的建筑照常立刻派单（按物质数值判，不认物质名）。
        /// </summary>
        private static void CheckRebuildWaitsForFire()
        {
            CampaignState s = NewWorld(6314, scrap: 2000);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 9f, 20f);
            BuildingRecord g = f != null ? PlaceNear(s, Furnace, f.Position, 12f, 22f) : null;
            GridCell? bc = f != null ? BeltCellNear(s, f.Position, 2f, 4f) : null;
            if (f == null || g == null || !bc.HasValue || Site == null)
            {
                Fail("D5b 测试准备：找不到两座精炼炉 / 火旁的一格传送带，或家园没有内核");
                return;
            }
            GridCell belt = bc.Value;
            BeltNetworkService.TryPlace(s, belt, BeltDir.East, 0);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _);
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            var fc = new Vector2(Mathf.Round(f.Position.x), Mathf.Round(f.Position.y));
            float rad = Mathf.Min(5f, Vector2.Distance(fc, new Vector2(belt.X, belt.Y)) + 1f);
            bool spawned = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(f.Position.x, f.Position.y), rad, 1f, 20f)
                           & Site.Kernel.SpawnPatch(EmgMatterConst.Water, new double2(g.Position.x, g.Position.y), 2.5f, 1f, 20f);
            Seconds(0.5f);
            StandingRuleService.BoundsOf(f, out GridCell fmin, out GridCell fmax);
            // 审查 P2：“在烧 / 火灭”用独立判据（直接查斑块与已知布置，见 HotIn / ColdIn），不借被测的 StructHazardIn。
            bool burning() => HotIn(fmin, fmax);
            bool cold() => ColdIn(fmin, fmax) && ColdAt(belt);
            StandingRuleService.BoundsOf(g, out GridCell gmin, out GridCell gmax);
            bool waterHazard = !WetIn(gmin, gmax) || !ColdIn(gmin, gmax); // 水盖住那座、附近没有会烧蚀设施的斑块
            bool fireHazard = HotAt(belt) && burning();
            bool wetNotHazard = !waterHazard && fireHazard;
            int scrap0 = s.Scrap;
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            BuildingOps.ApplyDamage(s, g.BuildingId, 10000f);
            BeltNetworkService.TryDamage(s, belt, BeltNetworkService.MaxHp(0), out _);
            Seconds(0.5f);
            PlannedBeltRecord ghost = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).FirstOrDefault(p => p.Destroyed && p.Xs[0] == belt.X && p.Ys[0] == belt.Y);
            int Logs(string key, string entity) => StandingRuleService.LogEntries(s).Count(l => l.Key == key && l.Rule == r.Serial && l.EntityId == entity);
            string fKey = StandingRuleService.BuildingKey(f.BuildingId);
            string bKey = "c:" + belt.X.ToString(CultureInfo.InvariantCulture) + "," + belt.Y.ToString(CultureInfo.InvariantCulture);
            bool BeltQueued() => ghost != null && HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + ghost.PlanId) != null;
            string QueueStatus()
            {
                var q = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, q);
                return ghost == null ? null : q.FirstOrDefault(e => e.DestroyedPlanId == ghost.PlanId).Status;
            }
            string trace = StandingRuleService.DescribeBuilding(s, f.BuildingId) ?? string.Empty;
            string queue = QueueStatus() ?? string.Empty;
            bool fWaits = f.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null
                          && Logs("rules.log.rebuild_fire", fKey) == 1 && Logs("rules.log.rebuild", fKey) == 0 && StandingRuleService.FireWaitRule(s, f.BuildingId) == r.Serial
                          && trace.Contains("R" + r.Serial) && trace.Contains("火");
            bool beltWaits = ghost != null && !BeltQueued() && Logs("rules.log.rebuild_belt_fire", bKey) == 1 && Logs("rules.log.rebuild_belt", bKey) == 0 && queue.Contains("等火灭");
            bool issue = r.IssueKey == StandingRuleService.IssueRebuildFire;
            WorkOrderRecord go = HomeValleyWorkOrders.FindActiveRepair(s, g.BuildingId);
            bool wetOrdered = go != null && go.RuleSerial == r.Serial && Logs("rules.log.rebuild", StandingRuleService.BuildingKey(g.BuildingId)) == 1;
            int gCost = BuildingOps.RebuildCost(g, out _);
            bool noCharge = s.Scrap == scrap0 - (go != null ? gCost : 0);
            Expect(spawned && wetNotHazard && fWaits && beltWaits && issue && wetOrdered && noCharge,
                $"D5b FG-GAP-224 着火的格子等火灭：明火（半径 {rad:0.#}）盖住被摧毁的精炼炉与一格传送带——都不派单、不扣料（废料 {scrap0} → {s.Scrap}，只扣了不在火里那座的 {gCost}）、" +
                $"规则日志各 1 条“等火灭”（{fWaits}/{beltWaits}），规则原因“{StandingRuleService.IssueText(r)}”（{issue}），建筑面板“{trace}”、施工队列“{queue}”；" +
                $"负向：水（不伤设施）盖住的那座照常立刻派单（{wetOrdered}；斑块布置：明火盖住精炼炉与传送带 {fireHazard}、水那座不对 {waterHazard}）");
            // 还在烧时的重查（定时检查与“有改动立即检查”同一路径）：仍不派单、日志不重复。
            StandingRuleService.RequestEvaluation(s);
            Seconds(1f);
            bool stillBurning = burning();
            bool holds = stillBurning && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null && !BeltQueued()
                         && Logs("rules.log.rebuild_fire", fKey) == 1 && Logs("rules.log.rebuild_belt_fire", bKey) == 1;
            // 火灭了：下一次定时检查照常派单（日志写“由规则 Rn 派机器重建”，原因清掉），机器按原样重建。
            bool out1 = StepUntil(cold, 60);
            bool dispatched = StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) != null && (ghost == null || !ghost.Destroyed), 75);
            WorkOrderRecord fo = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId);
            bool traced = fo != null && fo.RuleSerial == r.Serial && Logs("rules.log.rebuild", fKey) == 1 && Logs("rules.log.rebuild_belt", bKey) == 1
                          && Logs("rules.log.rebuild_fire", fKey) == 1 && StandingRuleService.FireWaitRule(s, f.BuildingId) == 0 && r.IssueKey != StandingRuleService.IssueRebuildFire;
            bool rebuilt = dispatched && StepUntil(() => f.ConstructionState == BuildingConstructionState.Operational && BeltNetworkService.HpOf(belt) > 0, 300);
            Expect(holds && out1 && dispatched && traced && rebuilt,
                $"D5c FG-GAP-224 火灭后照常重建：还在烧时重查不派单、日志不重复（{holds}）；火灭（{out1}）后下一次定时检查派单（{dispatched}），由 R{r.Serial} 触发、日志可追溯、“等火灭”原因清掉（{traced}，原因“{StandingRuleService.IssueText(r)}”），" +
                $"机器按原样重建好精炼炉与传送带（{rebuilt}）");
        }

        /// <summary>
        /// FG-GAP-224 审查修复：地下传送带被摧毁后的虚影是 [入口, 出口] 两格、两端一起重建（AddDestroyedNodeGhost / RebuildDestroyed）。
        /// 出口在火里、入口不在 → 同样不派单、不扣料、写“等火灭”（原因写在烧的出口那一格），火灭后照常派单、两端按原样建回（FGR-DEF-015、FGR-BASE-020）。
        /// 负向：只有两端之间的隧道格在烧（隧道不施工）→ 照常立刻派单（逐格判，不按外接矩形）。
        /// </summary>
        private static void CheckUndergroundGhostWaitsForFire()
        {
            CampaignState s = NewWorld(6315, scrap: 2000);
            ClearRules(s);
            const int span = 4;
            GridCell? a = FgProductionSelfCheck.FindArea(s, span + 1, 1, 9f, 20f);
            if (!a.HasValue || Site == null || !BeltNetworkService.TryPlaceUnderground(s, a.Value, BeltDir.East, 0, span).Ok)
            {
                Fail("D5d 测试准备：找不到 5×1 空地 / 放不下地下传送带，或家园没有内核");
                return;
            }
            GridCell inC = a.Value, outC = new GridCell(inC.X + span, inC.Y), mid = new GridCell(inC.X + span / 2, inC.Y);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            bool Hazard(GridCell c) => HotAt(c); // 审查 P2：独立判据（直接查斑块），不借被测的 StructHazardIn
            string key = "c:" + inC.X.ToString(CultureInfo.InvariantCulture) + "," + inC.Y.ToString(CultureInfo.InvariantCulture);
            int Logs(string k) => StandingRuleService.LogEntries(s).Count(l => l.Key == k && l.Rule == r.Serial && l.EntityId == key);
            PlannedBeltRecord Ghost() => HomeValleyConstruction.TryFindPlannedCell(s, outC, out PlannedBeltRecord g, out _) && HomeValleyConstruction.IsUnderground(g) ? g : null;
            bool Queued(PlannedBeltRecord g) => g != null && HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + g.PlanId) != null;
            bool Rebuilt() => BeltNetworkService.Kernel.TryGetUndergroundEnds(inC.X, inC.Y, out _, out _, out int d) && d == span;

            // 正向：火盖住出口、不盖入口 → 不派单、不扣料、写“等火灭”（写出口那一格）；火灭后照常派单、按原样建回。
            // 先做正向：机器还没进过火（进过火的机器过热后会沿路漏熔融，熔融也算在烧，会盖到入口）。
            bool spawnOut = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(outC.X, outC.Y), 1.5f, 1f, 20f);
            Seconds(0.5f);
            bool exitOnly = spawnOut && Hazard(outC) && ColdAt(inC);
            string setup = exitOnly ? string.Empty : $"【准备：生成 {spawnOut}、出口在烧 {Hazard(outC)}、入口在烧 {Hazard(inC)}；附近斑块 {PatchesNear(inC, 10)}】";
            BeltNetworkService.TryDamage(s, inC, 9999, out _);
            int scrap1 = s.Scrap;
            Seconds(2f);
            int scrap2 = s.Scrap;
            PlannedBeltRecord g2 = Ghost();
            var q = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, q);
            string queue = g2 == null ? string.Empty : q.FirstOrDefault(e => e.DestroyedPlanId == g2.PlanId).Status ?? string.Empty;
            string issue = StandingRuleService.IssueText(r);
            string outAt = "(" + outC.X.ToString(CultureInfo.InvariantCulture) + ", " + outC.Y.ToString(CultureInfo.InvariantCulture) + ")";
            bool waits = g2 != null && g2.Destroyed && g2.Xs.Length == 2 && !Queued(g2) && scrap2 == scrap1
                         && Logs("rules.log.rebuild_belt_fire") == 1 && Logs("rules.log.rebuild_belt") == 0
                         && r.IssueKey == StandingRuleService.IssueRebuildFire && issue.Contains(outAt) && queue.Contains("等火灭");
            bool out2 = StepUntil(() => ColdAt(outC), 60);
            bool dispatched = StepUntil(() => g2 != null && !g2.Destroyed && Queued(g2), 75);
            bool traced = dispatched && Logs("rules.log.rebuild_belt") == 1 && Logs("rules.log.rebuild_belt_fire") == 1 && r.IssueKey != StandingRuleService.IssueRebuildFire;
            bool rebuilt = dispatched && StepUntil(Rebuilt, 300);
            Expect(exitOnly && waits && out2 && traced && rebuilt,
                $"D5d FG-GAP-224 地下传送带出口在火里：入口 ({inC.X}, {inC.Y}) 不在烧、出口 ({outC.X}, {outC.Y}) 在烧（{exitOnly}）→ 两端一份的虚影不派单、不扣料（废料 {scrap1} → 2 秒后 {scrap2}）、规则日志 1 条“等火灭”，" +
                $"原因写出口那一格“{issue}”、施工队列“{queue}”（{waits}）；火灭（{out2}）后下一次定时检查由 R{r.Serial} 派单、原因清掉（{traced}），两端按原样建回（{rebuilt}）" + setup);

            // 负向：火只盖住隧道中间一格（入口 / 出口都不在烧；按外接矩形查会误判成在烧）→ 照常立刻派单。
            bool fireGone = StepUntil(() => !AnyPatchNear(mid, 8f, EmgMatterConst.Fire), 60); // 同物质斑块软合并半径 6 格：等上一块火没了，新火才不会并进去
            bool spawnMid = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(mid.X, mid.Y), 0.5f, 1f, 20f);
            Seconds(0.5f);
            bool bbox = HotIn(new GridCell(Math.Min(inC.X, outC.X), Math.Min(inC.Y, outC.Y)), new GridCell(Math.Max(inC.X, outC.X), Math.Max(inC.Y, outC.Y))); // 外接矩形里有一格在烧（隧道格）
            bool midOnly = Rebuilt() && fireGone && spawnMid && Hazard(mid) && ColdAt(inC) && ColdAt(outC) && bbox;
            string setup2 = midOnly ? string.Empty : $"【准备：上一步已建回 {Rebuilt()}、上一块火没了 {fireGone}、生成 {spawnMid}、隧道格在烧 {Hazard(mid)}、入口 {Hazard(inC)}、出口 {Hazard(outC)}；附近斑块 {PatchesNear(inC, 10)}】";
            int belt1 = Logs("rules.log.rebuild_belt"), fire1 = Logs("rules.log.rebuild_belt_fire");
            BeltNetworkService.TryDamage(s, inC, 9999, out _);
            Seconds(0.5f);
            PlannedBeltRecord g1 = Ghost();
            bool midOrdered = g1 != null && g1.Xs.Length == 2 && !g1.Destroyed && Queued(g1) && Logs("rules.log.rebuild_belt") == belt1 + 1 && Logs("rules.log.rebuild_belt_fire") == fire1;
            bool midRebuilt = midOrdered && StepUntil(Rebuilt, 300);
            Expect(midOnly && midOrdered && midRebuilt,
                $"D5e FG-GAP-224 负向（地下传送带隧道格在烧）：火只盖住入口与出口之间的隧道格（{midOnly}；外接矩形会判成在烧 {bbox}）→ 照常立刻派单、不写“等火灭”（逐格判，{midOrdered}），两端按原样建回（{midRebuilt}）" + setup2);
        }

        /// <summary>真文件存档 → 卸载 → 读档 → 载入家园（与 F1 同一路径）。失败返回 null 并写原因。</summary>
        private static CampaignState SaveAndReload(out string why) => SaveTo(Slot, out why) ? ReloadFrom(Slot, out why) : null;

        /// <summary>只存档（正式存档入口，整局同步 + 写盘），不卸载。</summary>
        private static bool SaveTo(int slot, out string why)
        {
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            why = save.Success ? null : $"存档 {slot} 失败";
            return save.Success;
        }

        /// <summary>卸载 → 读档 → 载入家园（与 F1 同一路径），不先存档（读回更早存的那份）。</summary>
        private static CampaignState ReloadFrom(int slot, out string why)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            if (!rr.Success)
            {
                why = $"读档 {slot} 失败（{rr.Message}）";
                return null;
            }
            CampaignSession.Set(slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            why = null;
            return rr.State;
        }

        private static string QueueStatusOf(CampaignState s, WorkOrderRecord o)
        {
            var q = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, q);
            return o == null ? string.Empty : q.FirstOrDefault(e => e.Order != null && e.Order.WorkOrderId == o.WorkOrderId).Status ?? string.Empty;
        }

        private static int RuleLogs(CampaignState s, StandingRuleRecord r, string key, string entity) =>
            StandingRuleService.LogEntries(s).Count(l => l.Key == key && l.Rule == r.Serial && (entity == null || l.EntityId == entity));

        private static string CellKey(GridCell c) => "c:" + c.X.ToString(CultureInfo.InvariantCulture) + "," + c.Y.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// EMG-R 出口补缺口第 5 轮（FG-GAP-189，总控更正 2026-11-17；按推荐默认执行（2026-11-17），待用户确认）。规格：FGR-DEF-015“材料到位后由机器按原样重建”、
        /// FG06 负向“自动重建时材料不足：虚影等待材料，不会重复扣料”、FGR-ECO-070 核心应急产废料、FGR-BASE-020 可追溯。规则：
        /// (a) 预留只挡逐份取料的传送带虚影——比它早被摧毁、要一次凑齐、按当前进账攒得齐的建筑在等材料时，虚影不取用那部分；比那座更早被摧毁的虚影不受限；
        /// (b) 整单之间不互相预留：后被摧毁、更便宜的建筑付得起就照常派单（不被饿死）；
        /// (d) 玩家手动下的单不受预留限制。中途存读档后照样挡住、照样续做；“等材料（预留给 X）”日志的去重随存档（读回更早存的档再触发照样写，审查 r5）。
        /// </summary>
        private static void CheckRebuildQueueReserve()
        {
            CampaignState s = NewWorld(6317, scrap: 0);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 9f, 20f);
            int cf = f != null ? BuildingOps.RebuildCost(f, out _) : 0;
            int cb = HomeGridService.BeltCostPerCell(0);
            BuildingRecord cheap = null;
            int cp = 0;
            foreach (string type in new[] { "power_pole", "barrier_t1" })
            {
                if (f == null || cheap != null)
                {
                    break;
                }
                BuildingRecord c = PlaceNear(s, type, f.Position, 5f, 16f);
                int cost = c != null ? BuildingOps.RebuildCost(c, out _) : 0;
                if (c != null && cost > 0 && cost + cb < cf)
                {
                    cheap = c;
                    cp = cost;
                }
                else if (c != null)
                {
                    Unregister(s, c);
                }
            }
            GridCell? early = f != null ? BeltCellNear(s, f.Position, 4f, 9f) : null;
            if (early.HasValue)
            {
                BeltNetworkService.TryPlace(s, early.Value, BeltDir.East, 0);
            }
            GridCell? late = early.HasValue ? BeltCellNear(s, f.Position, 10f, 16f) : null;
            if (late.HasValue)
            {
                BeltNetworkService.TryPlace(s, late.Value, BeltDir.East, 0);
            }
            GridCell? manual = late.HasValue ? BeltCellNear(s, f.Position, 17f, 24f) : null;
            if (f == null || cheap == null || !late.HasValue || !manual.HasValue || cb <= 0 || cf > SoftlockService.CoreScrapTarget)
            {
                Fail($"D5f 测试准备：精炼炉 {f != null}（重建 {cf}，要在核心应急攒得到的 {SoftlockService.CoreScrapTarget} 以内）、比它便宜且加一格传送带仍不到它的建筑 {cheap != null}、" +
                     $"三格传送带 {early.HasValue}/{late.HasValue}/{manual.HasValue}（每格 {cb}）");
                return;
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _);
            StandingRuleService.TryAddTarget(s, r.Serial, cheap.BuildingTypeId, out _);
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            int hp0 = BeltNetworkService.MaxHp(0);
            // 摧毁先后：传送带（早）→ 精炼炉 → 便宜的那座 → 传送带（晚）；仓库 0、没有回收站 → 核心应急开始产废料（这就是进账：精炼炉在它攒得到的范围里）。
            BeltNetworkService.TryDamage(s, early.Value, hp0, out _);
            Seconds(0.3f);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            Seconds(0.3f);
            BuildingOps.ApplyDamage(s, cheap.BuildingId, 10000f);
            Seconds(0.3f);
            BeltNetworkService.TryDamage(s, late.Value, hp0, out _);
            Seconds(0.3f);
            bool emergency = StepUntil(() => SoftlockService.StateOf(s).CoreScrapActive, 30);
            WorkOrderRecord Belt(CampaignState st, GridCell c) =>
                HomeValleyConstruction.TryFindPlannedCell(st, c, out PlannedBeltRecord p, out _) ? HomeValleyWorkOrders.FindActiveBuild(st, HomeValleyConstruction.BeltPlanPrefix + p.PlanId) : null;
            WorkOrderRecord lateOrder = Belt(s, late.Value);
            bool queued = emergency && Belt(s, early.Value) != null && lateOrder != null && lateOrder.RuleSerial == r.Serial && lateOrder.RuleQueueTick > 0
                          && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null && HomeValleyWorkOrders.FindActiveRepair(s, cheap.BuildingId) == null;
            const int PreSlot = Slot - 1;
            bool preSaved = SaveTo(PreSlot, out string preWhy); // D5f3 读回这份“还没给废料、还没写预留日志”的早档
            // 仓库给到“早传送带 + 便宜那座 + 1”（远不到精炼炉的重建费）：
            int give = cb + cp + 1;
            s.Scrap = give;
            StandingRuleService.RequestEvaluation(s); // 有改动立即检查（与定时检查同一路径）：等料的建筑这一步就重查
            bool earlyBuilt = StepUntil(() => BeltNetworkService.HpOf(early.Value) == hp0, 120);
            bool cheapUp = StepUntil(() => cheap.ConstructionState == BuildingConstructionState.Operational, 120);
            Seconds(3f);
            string fName = StandingRuleService.ReserveHolderName(s, f.BuildingId);
            string lateStatus = QueueStatusOf(s, lateOrder);
            bool fWaits = f.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null;
            bool held = earlyBuilt && cheapUp && fWaits && s.Scrap >= 1 && BeltNetworkService.HpOf(late.Value) < 0 && lateOrder.State == WorkOrderState.Waiting
                        && lateStatus.Contains("预留给") && lateStatus.Contains(fName) && RuleLogs(s, r, "rules.log.rebuild_reserved", CellKey(late.Value)) == 1;
            Expect(queued && held,
                $"D5f FG-GAP-189 (a)(b)：摧毁先后 = 传送带（早）→ 精炼炉（重建 {cf}）→ 便宜的那座（{cheap.BuildingTypeId}，{cp}）→ 传送带（晚），都由 R{r.Serial} 排上、核心应急在产废料（{queued}）；" +
                $"仓库给 {give} 后：早传送带照常取料重建（比精炼炉更早被摧毁的不受预留，{earlyBuilt}），便宜那座虽然后被摧毁、付得起就照常派单建好（整单之间不互相预留、不被饿死，{cheapUp}），" +
                $"精炼炉仍在等（{fWaits}），晚传送带不取用留给精炼炉的废料（仓库剩 {s.Scrap} ≥ 1、施工队列“{lateStatus}”、“等材料（预留给）”日志 {RuleLogs(s, r, "rules.log.rebuild_reserved", CellKey(late.Value))} 条）（{held}）");

            // (d) 负向：玩家自己放的施工不受预留限制——仓库剩下的废料照常被取走、建成；晚传送带仍挡着。
            GridOpResult placed = HomeGridService.TryPlaceBeltPath(s, "belt_t1", manual.Value, manual.Value, BeltDir.East);
            WorkOrderRecord manualOrder = Belt(s, manual.Value);
            bool manualBuilt = placed.Success && manualOrder != null && manualOrder.RuleQueueTick == 0 && StepUntil(() => BeltNetworkService.HpOf(manual.Value) == hp0, 120);
            bool lateStill = BeltNetworkService.HpOf(late.Value) < 0 && lateOrder.State == WorkOrderState.Waiting && f.ConstructionState == BuildingConstructionState.Damaged;
            Expect(manualBuilt && lateStill,
                $"D5f-d FG-GAP-189 (d) 负向：玩家自己放的一格传送带（{placed.Success}，不带排队位置 {manualOrder?.RuleQueueTick}）照常取用仓库里的废料建成（{manualBuilt}）；同一时刻自动重建的晚传送带仍为精炼炉挡着（{lateStill}）");

            // 中途存读档：读档后照样挡住（排队位置随存档），“等材料（预留给）”日志不因读档再写一条（去重随存档）；补足后精炼炉派单、晚传送带接着建好。
            string fId = f.BuildingId, lateId = lateOrder.WorkOrderId;
            long qTick = lateOrder.RuleQueueTick;
            CampaignState l = SaveAndReload(out string why);
            if (l == null)
            {
                Fail("D5f2 存读档失败：" + why);
                return;
            }
            WorkOrderRecord lo = HomeValleyWorkOrders.Find(l, lateId);
            StandingRuleRecord lr = StandingRuleService.Find(l, r.Serial);
            l.Scrap = Math.Max(1, l.Scrap); // 仓库有货：晚传送带会再去问一次预留
            Seconds(3f);
            RuleEventRecord fe = l.StandingRules.Pending.FirstOrDefault(e => e != null && e.EntityId == fId);
            bool stillHeld = lo != null && lr != null && lo.RuleQueueTick == qTick && lo.State == WorkOrderState.Waiting && BeltNetworkService.HpOf(late.Value) < 0
                             && HomeValleyWorkOrders.FindActiveRepair(l, fId) == null && fe != null && fe.ReserveNoted && RuleLogs(l, lr, "rules.log.rebuild_reserved", CellKey(late.Value)) == 1;
            l.Scrap += cf + cb;
            BuildingRecord lf = HomeGridService.FindBuilding(l, fId);
            bool all = StepUntil(() => lf.ConstructionState == BuildingConstructionState.Operational && BeltNetworkService.HpOf(late.Value) == hp0, 300);
            Seconds(1f);
            int ordersF = (l.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.TargetId == fId && o.Kind == WorkOrderKind.Repair);
            bool done = all && ordersF == 1 && lo.State == WorkOrderState.Completed && lr.IssueKey.Length == 0;
            Expect(stillHeld && done,
                $"D5f2 等材料（预留）途中存读档：读档后晚传送带排队位置原样（{lo?.RuleQueueTick} = {qTick}）、仍挡着、预留日志仍 1 条（去重记在精炼炉的等待记录上 {fe?.ReserveNoted}）（{stillHeld}）；" +
                $"补足后精炼炉派单（只派 1 次：{ordersF}）、晚传送带接着建好（{all}）、规则原因清掉（{done}）");

            // 读回更早存的那份档（还没写过预留日志）再触发：照样写一条（原来的进程级去重集合会把它吞掉，审查 r5）。
            CampaignState e0 = preSaved ? ReloadFrom(PreSlot, out preWhy) : null;
            if (e0 == null)
            {
                Fail("D5f3 读回早档失败：" + preWhy);
                return;
            }
            StandingRuleRecord er = StandingRuleService.Find(e0, r.Serial);
            int before = RuleLogs(e0, er, "rules.log.rebuild_reserved", CellKey(late.Value));
            e0.Scrap = give;
            StandingRuleService.RequestEvaluation(e0);
            bool again = StepUntil(() => RuleLogs(e0, er, "rules.log.rebuild_reserved", CellKey(late.Value)) == 1, 60);
            Seconds(2f);
            bool once = again && RuleLogs(e0, er, "rules.log.rebuild_reserved", CellKey(late.Value)) == 1 && BeltNetworkService.HpOf(late.Value) < 0;
            Expect(before == 0 && once,
                $"D5f3 读回更早存的档（预留日志 {before} 条）后同样给 {give} 废料：晚传送带照样被精炼炉挡住、照样写 1 条“等材料（预留给）”（{again}），之后不重复（{once}）——日志只取决于存档状态、不取决于这个进程之前跑过什么");
        }

        /// <summary>
        /// EMG-R 出口补缺口第 5 轮（FG-GAP-189 规则 (c)，审查 r5“预留与核心应急互相卡死”）：整单所需超过仓库可存上限、或当前进账下攒不齐时不为它预留——
        /// 后被摧毁的传送带虚影照常逐份取料建回（不卡死），那座的等待原因写明为什么不预留（规则面板 / 建筑面板）。
        /// </summary>
        private static void CheckRebuildReserveUnattainable()
        {
            // ① 超过仓库上限：拆掉自检世界里的仓库（只剩核心缓存），找一座重建费比上限还高的（按表里的等级数值，不认建筑名）。
            CampaignState s = NewWorld(6323, scrap: 0);
            ClearRules(s);
            foreach (BuildingRecord w in s.BuildingRecords.Where(x => x != null && x.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse).ToArray())
            {
                Unregister(s, w);
            }
            ItemDef scrapItem = ItemCatalog.Find(ItemCatalog.ScrapId);
            int cap = HomeInventory.Capacity(s, scrapItem);
            BuildingRecord big = null;
            int cbig = 0;
            foreach (string type in new[] { "drop_station", "override_array", "turret_heavy", "purifier", "sim_lab" })
            {
                BuildingRecord c = big == null ? PlaceNear(s, type, CoreCenter(s), 9f, 26f) : null;
                if (c == null)
                {
                    continue;
                }
                c.Tier = BuildingOps.MaxTier(type);
                int cost = BuildingOps.RebuildCost(c, out _);
                if (cost > cap)
                {
                    big = c;
                    cbig = cost;
                }
                else
                {
                    Unregister(s, c);
                }
            }
            GridCell? late = big != null ? BeltCellNear(s, big.Position, 6f, 16f) : null;
            int cb = HomeGridService.BeltCostPerCell(0);
            if (big == null || !late.HasValue)
            {
                Fail($"D5f4 测试准备：仓库上限 {cap}，找不到重建费比它高的建筑 {big != null}，或找不到能铺传送带的一格 {late.HasValue}");
                return;
            }
            BeltNetworkService.TryPlace(s, late.Value, BeltDir.East, 0);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, big.BuildingTypeId, out _);
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            int hp0 = BeltNetworkService.MaxHp(0);
            BuildingOps.ApplyDamage(s, big.BuildingId, 1000000f);
            Seconds(0.3f);
            BeltNetworkService.TryDamage(s, late.Value, hp0, out _);
            bool emergency = StepUntil(() => SoftlockService.StateOf(s).CoreScrapActive, 30);
            s.Scrap = cb + 1;
            StandingRuleService.RequestEvaluation(s);
            bool lateBuilt = StepUntil(() => BeltNetworkService.HpOf(late.Value) == hp0, 120);
            string capText = cap.ToString(CultureInfo.InvariantCulture);
            string issue = StandingRuleService.IssueText(r) ?? string.Empty;
            string panel = StandingRuleService.DescribeBuilding(s, big.BuildingId) ?? string.Empty;
            bool told = issue.Contains("超过仓库") && issue.Contains(capText) && panel.Contains("超过仓库") && panel.Contains("R" + r.Serial);
            Expect(emergency && lateBuilt && told && big.ConstructionState == BuildingConstructionState.Damaged,
                $"D5f4 FG-GAP-189 (c) 超过仓库上限不卡死：{big.BuildingTypeId} T{big.Tier} 重建要 {cbig}、仓库最多存 {cap}（核心应急在产 {emergency}）→ 不为它预留，后被摧毁的传送带照常取料建回（{lateBuilt}）；" +
                $"规则面板“{issue}”、建筑面板“{panel}”写明原因（{told}）");

            // ② 当前进账攒不齐：仓库一开始就有 5 份废料（核心应急只在库存为 0 时启动）、没有回收站在产 → 没有进账；精炼炉要 40 → 不为它预留，后被摧毁的传送带照常建回。
            CampaignState t = NewWorld(6324, scrap: 5);
            ClearRules(t);
            BuildingRecord f = PlaceNear(t, Furnace, CoreCenter(t), 9f, 20f);
            GridCell? tl = f != null ? BeltCellNear(t, f.Position, 6f, 14f) : null;
            if (f == null || !tl.HasValue)
            {
                Fail("D5f5 测试准备：找不到精炼炉 / 传送带格");
                return;
            }
            BeltNetworkService.TryPlace(t, tl.Value, BeltDir.East, 0);
            StandingRuleRecord tr = NewRebuildRule(t);
            StandingRuleService.TryAddTarget(t, tr.Serial, Furnace, out _);
            StandingRuleService.TryAddTarget(t, tr.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            int cf = BuildingOps.RebuildCost(f, out _);
            BuildingOps.ApplyDamage(t, f.BuildingId, 10000f);
            Seconds(0.3f);
            BeltNetworkService.TryDamage(t, tl.Value, hp0, out _);
            bool tBuilt = StepUntil(() => BeltNetworkService.HpOf(tl.Value) == hp0, 120);
            bool noIncome = !SoftlockService.StateOf(t).CoreScrapActive && !ProductionService.AnyRecyclerWorking(t);
            string tIssue = StandingRuleService.IssueText(tr) ?? string.Empty;
            string tPanel = StandingRuleService.DescribeBuilding(t, f.BuildingId) ?? string.Empty;
            bool tTold = tIssue.Contains("当前进账攒不到") && tPanel.Contains("当前进账攒不到") && f.ConstructionState == BuildingConstructionState.Damaged;
            Expect(noIncome && tBuilt && tTold,
                $"D5f5 FG-GAP-189 (c) 当前进账攒不齐不卡死：仓库 5、核心应急没在产、没有回收站在产（{noIncome}），精炼炉要 {cf} → 不为它预留，后被摧毁的传送带照常取料建回（{tBuilt}，仓库剩 {t.Scrap}）；" +
                $"规则面板“{tIssue}”、建筑面板“{tPanel}”（{tTold}）");

            // ③ 两条自动重建规则：R_a 管精炼炉、R_b 管传送带。精炼炉（R_a）先被摧毁、攒得齐（核心应急在产）→ 后被摧毁的传送带（R_b）被它挡住，
            // “等材料（预留给 精炼炉）”日志照样写在 R_b 名下（预留方属于另一条规则也要可追溯，FGR-BASE-020）。
            CampaignState m = NewWorld(6326, scrap: 0);
            ClearRules(m);
            BuildingRecord mf = PlaceNear(m, Furnace, CoreCenter(m), 9f, 20f);
            GridCell? ml = mf != null ? BeltCellNear(m, mf.Position, 6f, 14f) : null;
            if (mf == null || !ml.HasValue)
            {
                Fail("D5f6 测试准备：找不到精炼炉 / 传送带格");
                return;
            }
            BeltNetworkService.TryPlace(m, ml.Value, BeltDir.East, 0);
            StandingRuleRecord ra = NewRebuildRule(m);
            StandingRuleService.TryAddTarget(m, ra.Serial, Furnace, out _);
            StandingRuleRecord rb = NewRebuildRule(m);
            StandingRuleService.TryAddTarget(m, rb.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            BuildingOps.ApplyDamage(m, mf.BuildingId, 10000f);
            Seconds(0.3f);
            BeltNetworkService.TryDamage(m, ml.Value, hp0, out _);
            bool mEmergency = StepUntil(() => SoftlockService.StateOf(m).CoreScrapActive, 30);
            m.Scrap = 2;
            StandingRuleService.RequestEvaluation(m);
            bool noted = StepUntil(() => RuleLogs(m, rb, "rules.log.rebuild_reserved", CellKey(ml.Value)) == 1, 30);
            Seconds(1f);
            bool mHeld = noted && BeltNetworkService.HpOf(ml.Value) < 0 && RuleLogs(m, ra, "rules.log.rebuild_reserved", null) == 0
                         && mf.ConstructionState == BuildingConstructionState.Damaged && m.Scrap >= 2;
            Expect(mEmergency && mHeld,
                $"D5f6 两条规则：精炼炉归 R{ra.Serial}、传送带归 R{rb.Serial}（核心应急在产 {mEmergency}）→ 传送带被精炼炉挡住、“等材料（预留给）”日志写在 R{rb.Serial} 名下 1 条（{noted}），仓库 {m.Scrap} 没被取用（{mHeld}）");
        }

        /// <summary>
        /// EMG-R 出口补缺口第 4 轮（DEBT-EMGREXIT-06；FGR-DEF-015、FGR-BASE-020）：自动重建的单派出之后那里才起火（明火斑块，按物质数值 structDps &gt; 0 判）——
        /// 建筑重建单（机器在路上）：挂起、机器撤出、不再走进火里，预留的废料仍记在这张单名下（不退不扣），建筑面板写“起火了，施工暂停”，规则日志 1 条；
        /// 火灭后同一张单自动续做，按原样重建好，废料只扣一次。负向：玩家在挂起时手动取消照旧全额退回。
        /// </summary>
        private static void CheckFireAfterDispatchBuilding()
        {
            CampaignState s = NewWorld(6318, scrap: 2000);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 14f, 22f);
            BuildingRecord g = f != null ? PlaceNear(s, Furnace, CoreCenter(s), 14f, 22f) : null;
            if (f == null || g == null || Site == null || Vector2.Distance(f.Position, g.Position) < 9f)
            {
                Fail("D5g 测试准备：离核心 14～22 米找不到两座相隔 9 米以上的精炼炉，或家园没有内核");
                return;
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _);
            Seconds(0.5f);
            int cost = BuildingOps.RebuildCost(f, out _);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            WorkOrderRecord o = null;
            bool enRoute = StepUntil(() => (o = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId)) != null && o.State == WorkOrderState.Reserved && o.AssignedMachineLogicId > 0, 30);
            int scrapAfter = s.Scrap;
            int machine = o?.AssignedMachineLogicId ?? 0;
            float d0 = machine > 0 && Site.TryGetMachinePosition(machine, out Vector2 m0) ? Vector2.Distance(m0, f.Position) : -1f;
            const float rad = 2.5f;
            bool spawned = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(f.Position.x, f.Position.y), rad, 1f, 8f);
            StandingRuleService.BoundsOf(f, out GridCell fmin, out GridCell fmax);
            Seconds(1f);
            bool hot = HotIn(fmin, fmax);
            string trace = StandingRuleService.DescribeBuilding(s, f.BuildingId) ?? string.Empty;
            string fKey = StandingRuleService.BuildingKey(f.BuildingId);
            bool heldNow = HomeValleyWorkOrders.IsFireHeld(o);
            bool held = enRoute && spawned && hot && d0 > rad + 1f && heldNow && o.AssignedMachineLogicId == 0
                        && s.Scrap == scrapAfter && scrapAfter == 2000 - cost && trace.Contains("起火") && trace.Contains("R" + r.Serial)
                        && RuleLogs(s, r, "rules.log.rebuild_fire_hold", fKey) == 1;
            // 挂起期间机器一次都不进火：每 0.25 秒看一次它离火心多远（火灭前）。
            float minD = float.MaxValue;
            for (int i = 0; i < 80 && !ColdIn(fmin, fmax); i++)
            {
                Seconds(0.25f);
                if (Site.TryGetMachinePosition(machine, out Vector2 mp))
                {
                    minD = Mathf.Min(minD, Vector2.Distance(mp, f.Position));
                }
            }
            bool stayedOut = minD > rad;
            bool cold = StepUntil(() => ColdIn(fmin, fmax), 60);
            bool rebuilt = cold && StepUntil(() => f.ConstructionState == BuildingConstructionState.Operational, 300);
            int orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(x => x.TargetId == f.BuildingId && x.Kind == WorkOrderKind.Repair);
            bool once = rebuilt && orders == 1 && s.Scrap == 2000 - cost && RuleLogs(s, r, "rules.log.rebuild_fire_resume", fKey) == 1 && RuleLogs(s, r, "rules.log.rebuild", fKey) == 1;
            Expect(held && stayedOut && once,
                $"D5g DEBT-EMGREXIT-06 自动重建单派出后那里才起火（建筑）：机器在路上（离火心 {d0:0.#} 米，{enRoute}）时精炼炉上起明火（半径 {rad}，{spawned}/{hot}）→ 单子挂起等火灭、放掉机器（{heldNow}），" +
                $"预留的废料仍记在单名下（废料 {scrapAfter}，派单扣了 {cost}），建筑面板“{trace}”、规则日志 1 条（{held}）；挂起期间机器离火心最近 {minD:0.#} 米（不进火，{stayedOut}）；" +
                $"火灭（{cold}）后同一张单续做、按原样重建好（{rebuilt}），只有 1 张单、废料只扣一次（剩 {s.Scrap}）、日志写“火灭了，接着施工”（{once}）");

            // 负向：挂起时玩家手动取消照旧——全额退回废料、单子取消、留下虚影（之后不再自动派这一座以外的行为不在本条）。
            BuildingOps.ApplyDamage(s, g.BuildingId, 10000f);
            WorkOrderRecord go = null;
            bool gRoute = StepUntil(() => (go = HomeValleyWorkOrders.FindActiveRepair(s, g.BuildingId)) != null && go.State == WorkOrderState.Reserved, 30);
            int scrapG = s.Scrap;
            Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(g.Position.x, g.Position.y), rad, 1f, 8f);
            Seconds(1f);
            bool gHeld = gRoute && HomeValleyWorkOrders.IsFireHeld(go);
            bool cancelled = BuildingOps.TryCancelRepair(s, g.BuildingId, out string cmsg);
            int gCost = BuildingOps.RebuildCost(g, out _);
            bool refunded = cancelled && go.State == WorkOrderState.Cancelled && s.Scrap == scrapG + gCost && g.ConstructionState == BuildingConstructionState.Damaged;
            Expect(gHeld && refunded,
                $"D5g2 负向“挂起时玩家手动取消”：第二座派出后起火挂起（{gHeld}），玩家在面板取消（{cmsg}）→ 单子取消、预留的 {gCost} 废料全额退回（{scrapG} → {s.Scrap}）、留下虚影（{refunded}）");
        }

        /// <summary>
        /// EMG-R 出口补缺口第 4 轮（DEBT-EMGREXIT-06）：传送带虚影的自动重建单材料已送到、正在逐格施工时那一格起火 → 施工挂起（机器撤出），
        /// 已送到现场的材料留在原地、仍记在这张单名下，施工队列写“等火灭”；挂起途中存读档后照样挂着；火灭后自动续做，用现场的材料建完、不再从仓库多取。
        /// </summary>
        private static void CheckFireAfterDispatchBelt()
        {
            CampaignState s = NewWorld(6319, scrap: 2000);
            ClearRules(s);
            GridCell? bc = BeltCellNear(s, CoreCenter(s), 10f, 16f);
            if (!bc.HasValue || Site == null)
            {
                Fail("D5h 测试准备：找不到能铺传送带的一格，或家园没有内核");
                return;
            }
            GridCell c = bc.Value;
            BeltNetworkService.TryPlace(s, c, BeltDir.East, 0);
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, StandingRuleService.BeltTarget, out _);
            Seconds(0.5f);
            int hp0 = BeltNetworkService.MaxHp(0);
            BeltNetworkService.TryDamage(s, c, hp0, out _);
            Seconds(0.3f);
            PlannedBeltRecord p = HomeValleyConstruction.TryFindPlannedCell(s, c, out PlannedBeltRecord pp, out _) ? pp : null;
            WorkOrderRecord o = p != null ? HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + p.PlanId) : null;
            bool building = o != null && StepUntil(() => o.State == WorkOrderState.InProgress, 90);
            int delivered = p?.Delivered ?? 0;
            int scrap1 = s.Scrap;
            int mid = o?.AssignedMachineLogicId ?? 0;
            var cc = new Vector2(c.X, c.Y);
            float d0 = MachineDist(mid, cc);
            float rad = Mathf.Max(1.5f, d0 + 1f); // 火盖住机器脚下：机器正站在火里施工（审查 r5）
            bool spawned = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(c.X, c.Y), rad, 1f, 16f);
            Seconds(1f);
            string status = QueueStatusOf(s, o);
            bool heldNow = HomeValleyWorkOrders.IsFireHeld(o);
            bool held = building && spawned && HotAt(c) && delivered > 0 && HomeValleyWorkOrders.IsFireHeld(o) && o.AssignedMachineLogicId == 0
                        && p.Delivered == delivered && s.Scrap == scrap1 && status.Contains("等火灭") && RuleLogs(s, r, "rules.log.rebuild_fire_hold", CellKey(c)) == 1;
            // 审查 r5：机器撤到火圈外（离火心 > 半径），之后不回火里；离开时身上的明火沾染按物质表 coatSeconds 还会烧完（FG19 沾染规则），烧完以后不再掉血
            // （火还在烧：之后才存读档，验“挂起途中存读档”）。
            bool outNow = StepUntil(() => MachineDist(mid, cc) > rad, 6);
            float minD = float.MaxValue;
            float hpOut = AfterCoat(mid, cc, ref minD);
            for (int i = 0; i < 8; i++)
            {
                Seconds(0.25f);
                minD = Mathf.Min(minD, MachineDist(mid, cc) < 0f ? float.MaxValue : MachineDist(mid, cc));
            }
            float hpLater = MachineHp(mid);
            bool evacuated = d0 >= 0f && d0 < rad && outNow && minD > rad && hpOut > 0f && hpLater >= hpOut - 0.01f && HotAt(c) && HomeValleyWorkOrders.IsFireHeld(o);
            Expect(held && evacuated,
                $"D5h DEBT-EMGREXIT-06 传送带虚影施工中那一格起火：材料已送到（{delivered}）、逐格施工中（{building}）时起明火、盖住机器脚下（半径 {rad:0.#}，机器离火心 {d0:0.#} 米，{spawned}）→ 施工挂起、放掉机器（{heldNow}），" +
                $"现场材料原样（{p?.Delivered}）、仓库不变（{scrap1} → {s.Scrap}）、施工队列“{status}”、规则日志 1 条（{held}）；机器撤到火圈外（{outNow}），之后离火心最近 {minD:0.#} 米、" +
                $"身上的明火沾染（{FireCoatSeconds:0.#} 秒）烧完后血量 {hpOut:0.#} → 2 秒后 {hpLater:0.#}（不回火里、不再掉血，火仍在烧，{evacuated}）");

            string oid = o?.WorkOrderId, planId = p?.PlanId;
            CampaignState l = SaveAndReload(out string why);
            if (l == null)
            {
                Fail("D5h2 存读档失败：" + why);
                return;
            }
            WorkOrderRecord lo = HomeValleyWorkOrders.Find(l, oid);
            PlannedBeltRecord lp = HomeValleyConstruction.FindPlan(l, planId);
            Seconds(1f);
            bool heldAfter = lo != null && lp != null && HomeValleyWorkOrders.IsFireHeld(lo) && lp.Delivered == delivered && l.Scrap == scrap1 && (HotAt(c) || ColdAt(c));
            bool cold = StepUntil(() => ColdAt(c), 60);
            string diag = string.Empty; // 诊断：火灭后再次挂起那一刻附近的斑块与机器温度
            bool wasHeld = lo != null && HomeValleyWorkOrders.IsFireHeld(lo);
            bool done = cold && StepUntil(() =>
            {
                bool h = HomeValleyWorkOrders.IsFireHeld(lo);
                if (h && !wasHeld && diag.Length == 0)
                {
                    Site.TryGetMachineHeat(mid, out float heat, out bool over);
                    diag = $"【火灭续做后又挂起：附近斑块 {PatchesNear(c, 8)}；机器离格 {MachineDist(mid, cc):0.#} 米、温度 {heat:0} 过热 {over}】";
                }
                wasHeld = h;
                return BeltNetworkService.HpOf(c) == hp0;
            }, 120);
            Seconds(0.5f);
            // 每次挂起都有一次续做（FGR-BASE-020 每个自动动作可追溯）；火灭后回来的机器若已过热、身下漏熔融金属（FG19 熔穿），现场会再成危险格、再挂起一次，同样成对记日志。
            int holdsL = RuleLogs(l, StandingRuleService.Find(l, r.Serial), "rules.log.rebuild_fire_hold", CellKey(c));
            int resumesL = RuleLogs(l, StandingRuleService.Find(l, r.Serial), "rules.log.rebuild_fire_resume", CellKey(c));
            bool resumed = done && l.Scrap == scrap1 && lo.State == WorkOrderState.Completed && holdsL >= 1 && resumesL == holdsL;
            Expect(heldAfter && resumed,
                $"D5h2 等火灭（已派单挂起）途中存读档：读档后仍挂着、现场材料 {lp?.Delivered} 与仓库 {l.Scrap} 原样（{heldAfter}）；火灭（{cold}）后同一张单续做、按原样建回（{done}），" +
                $"用现场的材料建完、不再从仓库多取（仓库 {scrap1} → {l.Scrap}）、挂起 {holdsL} 次各有一条“火灭了，接着施工”（{resumesL} 条，单子 {lo?.State}，{resumed}）{diag}" +
                (resumed ? string.Empty : "【规则日志：" + RuleLogTail(l, StandingRuleService.Find(l, r.Serial)) + "】"));
        }

        /// <summary>诊断：这条规则最近 12 条日志（键 @ 实体）。</summary>
        private static string RuleLogTail(CampaignState s, StandingRuleRecord r) =>
            string.Join("；", StandingRuleService.LogEntries(s).Where(l => r != null && l.Rule == r.Serial).Reverse().Take(12).Reverse().Select(l => l.Key.Replace("rules.log.", string.Empty) + "@" + l.EntityId + "@" + l.Tick));

        private static float MachineDist(int machine, Vector2 at) => machine > 0 && Site.TryGetMachinePosition(machine, out Vector2 p) ? Vector2.Distance(p, at) : -1f;

        /// <summary>明火斑块的沾染保留秒（物质表 coatSeconds：离开斑块后身上的火还要烧这么久）。</summary>
        private static float FireCoatSeconds => GameLogic.Campaign.Emergence.EmgMatterCatalog.Current.Rows[EmgMatterConst.Fire].CoatSeconds;

        /// <summary>机器撤出火圈后等身上的明火沾染烧完（coatSeconds + 0.5 秒），期间记离火心最近的距离；返回那时的血量。</summary>
        private static float AfterCoat(int machine, Vector2 at, ref float minD)
        {
            int n = Mathf.CeilToInt((FireCoatSeconds + 0.5f) / 0.25f);
            for (int i = 0; i < n; i++)
            {
                Seconds(0.25f);
                float d = MachineDist(machine, at);
                minD = Mathf.Min(minD, d < 0f ? float.MaxValue : d);
            }
            return MachineHp(machine);
        }

        private static float MachineHp(int machine) =>
            machine > 0 && Site.TryGetMachineUnit(machine, out int u) && Site.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Health : -1f;

        /// <summary>
        /// EMG-R 出口补缺口第 5 轮（DEBT-EMGREXIT-06，审查 r5）：建筑重建单已经在现场施工（机器站在建筑旁）时那里起火、火盖住机器脚下 →
        /// 单子挂起，机器撤到火圈外（不是原地停下），之后不回火里、不再掉血；施工进度保留；火灭后同一张单续做、按原样重建好，废料只扣一次。
        /// </summary>
        private static void CheckFireAfterDispatchOnSite()
        {
            CampaignState s = NewWorld(6325, scrap: 2000);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 10f, 20f);
            if (f == null || Site == null)
            {
                Fail("D5g3 测试准备：找不到精炼炉，或家园没有内核");
                return;
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _);
            Seconds(0.5f);
            int cost = BuildingOps.RebuildCost(f, out _);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            WorkOrderRecord o = null;
            bool working = StepUntil(() => (o = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId)) != null && o.State == WorkOrderState.InProgress && o.AssignedMachineLogicId > 0, 90);
            int mid = o?.AssignedMachineLogicId ?? 0;
            Vector2 mp = mid > 0 && Site.TryGetMachinePosition(mid, out Vector2 p0) ? p0 : f.Position;
            Vector2 center = (mp + f.Position) * 0.5f;
            float rad = Vector2.Distance(mp, f.Position) * 0.5f + 2f; // 同时盖住机器脚下与建筑占地
            bool spawned = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(center.x, center.y), rad, 1f, 18f);
            float dIn = MachineDist(mid, center);
            Seconds(1f);
            StandingRuleService.BoundsOf(f, out GridCell fmin, out GridCell fmax);
            float progress = o?.Progress ?? 0f;
            bool held = working && spawned && dIn >= 0f && dIn < rad && HotIn(fmin, fmax) && HomeValleyWorkOrders.IsFireHeld(o) && o.AssignedMachineLogicId == 0 && s.Scrap == 2000 - cost;
            bool outNow = StepUntil(() => MachineDist(mid, center) > rad, 8);
            float minD = float.MaxValue;
            float hpOut = AfterCoat(mid, center, ref minD); // 离开时身上的明火沾染按物质表 coatSeconds 烧完（FG19 沾染规则），之后不再掉血
            for (int i = 0; i < 8; i++)
            {
                Seconds(0.25f);
                minD = Mathf.Min(minD, MachineDist(mid, center) < 0f ? float.MaxValue : MachineDist(mid, center));
            }
            float hpLater = MachineHp(mid);
            bool stayed = outNow && minD > rad && hpOut > 0f && hpLater >= hpOut - 0.01f && HomeValleyWorkOrders.IsFireHeld(o) && o.Progress >= progress - 0.001f && HotIn(fmin, fmax);
            bool cold = StepUntil(() => ColdIn(fmin, fmax), 60);
            string diag = string.Empty; // 火灭续做后若又挂起：记下那一刻现场附近的斑块与机器温度
            bool wasHeld = HomeValleyWorkOrders.IsFireHeld(o);
            bool rebuilt = cold && StepUntil(() =>
            {
                bool h = HomeValleyWorkOrders.IsFireHeld(o);
                if (h && !wasHeld && diag.Length == 0)
                {
                    Site.TryGetMachineHeat(mid, out float heat, out bool over);
                    diag = $"【火灭续做后又挂起：现场附近斑块 {PatchesNear(new GridCell(Mathf.RoundToInt(f.Position.x), Mathf.RoundToInt(f.Position.y)), 8)}；机器温度 {heat:0} 过热 {over}】";
                }
                wasHeld = h;
                return f.ConstructionState == BuildingConstructionState.Operational;
            }, 300);
            string fKey = StandingRuleService.BuildingKey(f.BuildingId);
            int orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(x => x.TargetId == f.BuildingId && x.Kind == WorkOrderKind.Repair);
            int holds = RuleLogs(s, r, "rules.log.rebuild_fire_hold", fKey), resumes = RuleLogs(s, r, "rules.log.rebuild_fire_resume", fKey);
            bool once = rebuilt && orders == 1 && s.Scrap == 2000 - cost && holds >= 1 && resumes == holds; // 每次挂起都有一次续做（FGR-BASE-020），过热漏熔融再挂起同样成对
            Expect(held && stayed && once,
                $"D5g3 DEBT-EMGREXIT-06 建筑重建已在现场施工（{working}）时起明火、盖住机器脚下（半径 {rad:0.#}，机器离火心 {dIn:0.#} 米，{spawned}）→ 挂起、放掉机器、预留的废料仍在单名下（{held}）；" +
                $"机器撤到火圈外（{outNow}），之后离火心最近 {minD:0.#} 米、身上的明火沾染（{FireCoatSeconds:0.#} 秒）烧完后血量 {hpOut:0.#} → 2 秒后 {hpLater:0.#}、施工进度 {progress:0.##} 保留、火仍在烧（{stayed}）；" +
                $"火灭（{cold}）后同一张单续做、按原样重建好（{rebuilt}），只有 {orders} 张单、废料只扣一次（剩 {s.Scrap}）、挂起 {holds} 次各有一次续做（{resumes}，{once}）{diag}" +
                (once ? string.Empty : "【规则日志：" + RuleLogTail(s, r) + "】"));
        }

        /// <summary>
        /// EMG-R 出口补缺口第 4 轮（审查 P2）：派单前“等火灭”途中存读档——读档后仍在等火灭（不派单、不扣料、日志不重复），火灭后照常派单重建，与不存读档一致。
        /// 同时验“按单记原因”：同一条规则下一座等材料、一座等火灭；等材料的那座派出后，规则原因换成还在等火灭的那座，不被清掉。
        /// </summary>
        private static void CheckFireWaitSaveLoadAndIssues()
        {
            CampaignState s = NewWorld(6320, scrap: 0);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 9f, 20f);
            BuildingRecord g = f != null ? PlaceNear(s, Furnace, f.Position, 12f, 22f) : null;
            if (f == null || g == null || Site == null)
            {
                Fail("D5i 测试准备：找不到两座精炼炉，或家园没有内核");
                return;
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _);
            Seconds(0.5f);
            int cost = BuildingOps.RebuildCost(f, out _);
            bool spawned = Site.Kernel.SpawnPatch(EmgMatterConst.Fire, new double2(g.Position.x, g.Position.y), 3f, 1f, 40f);
            StandingRuleService.BoundsOf(g, out GridCell gmin, out GridCell gmax);
            Seconds(0.5f);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f); // 先：不在火里，等材料（仓库 0）
            Seconds(0.3f);
            BuildingOps.ApplyDamage(s, g.BuildingId, 10000f); // 后：在火里，等火灭
            Seconds(0.5f);
            string gKey = StandingRuleService.BuildingKey(g.BuildingId);
            bool waits = spawned && HotIn(gmin, gmax) && StandingRuleService.FireWaitRule(s, g.BuildingId) == r.Serial && r.IssueKey == "rules.issue.rebuild_wait";
            s.Scrap = cost;
            StandingRuleService.RequestEvaluation(s); // 立即检查（B 还在烧）
            bool fOrdered = StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) != null, 75);
            string gName = StandingRuleService.ReserveHolderName(s, g.BuildingId);
            string issue = StandingRuleService.IssueText(r) ?? string.Empty;
            bool kept = fOrdered && HotIn(gmin, gmax) && r.IssueKey == StandingRuleService.IssueRebuildFire && issue.Contains(gName);
            Expect(waits && kept,
                $"D5i 按单记原因：同一条规则下精炼炉 A 等材料、B 在火里等火灭（{waits}）；补够 A 的 {cost} 后 A 派出（{fOrdered}），规则原因换成还在等火灭的 B“{issue}”、没被清掉（{kept}）");

            CampaignState l = SaveAndReload(out string why);
            if (l == null)
            {
                Fail("D5i2 存读档失败：" + why);
                return;
            }
            StandingRuleRecord lr = StandingRuleService.Find(l, r.Serial);
            BuildingRecord lg = HomeGridService.FindBuilding(l, g.BuildingId);
            int scrapL = l.Scrap;
            Seconds(2f);
            bool stillWaits = lr != null && lg != null && HotIn(gmin, gmax) && StandingRuleService.FireWaitRule(l, g.BuildingId) == r.Serial
                              && HomeValleyWorkOrders.FindActiveRepair(l, g.BuildingId) == null && lr.IssueKey == StandingRuleService.IssueRebuildFire;
            l.Scrap = scrapL + cost;
            bool cold = StepUntil(() => ColdIn(gmin, gmax), 60);
            bool dispatched = cold && StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(l, g.BuildingId) != null, 75);
            bool rebuilt = dispatched && StepUntil(() => lg.ConstructionState == BuildingConstructionState.Operational, 300);
            bool once = rebuilt && RuleLogs(l, lr, "rules.log.rebuild_fire", gKey) == 1 && RuleLogs(l, lr, "rules.log.rebuild", gKey) == 1 && lr.IssueKey.Length == 0
                        && l.Scrap == scrapL;
            Expect(stillWaits && once,
                $"D5i2 等火灭（派单前）途中存读档：读档后 B 仍在等火灭、不派单、规则原因仍写它（{stillWaits}）；火灭（{cold}）后照常派单（{dispatched}）、按原样重建好（{rebuilt}），" +
                $"“等火灭”日志仍只有 1 条、派单日志 1 条、只扣一次 {cost}、原因清掉（{once}）");
        }

        /// <summary>
        /// EMG-R 出口补缺口第 4 轮（按单记原因的旧档兼容）：旧档的等待记录没有 IssueArg、规则原因位为空——下一次处理事件（还没到定时重试）时，
        /// 规则原因按现在的状态补成同一格式（“精炼炉：废料不够：重建要 N，仓库里有 M（够了会自动派单）”），不出现缺参数的占位。
        /// </summary>
        private static void CheckLegacyIssueArg()
        {
            CampaignState s = NewWorld(6322, scrap: 0);
            ClearRules(s);
            BuildingRecord f = PlaceNear(s, Furnace, CoreCenter(s), 9f, 20f);
            BuildingRecord other = f != null ? PlaceNear(s, "power_pole", f.Position, 5f, 14f) : null;
            if (f == null || other == null)
            {
                Fail("D5j 测试准备：找不到精炼炉 / 电塔");
                return;
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddTarget(s, r.Serial, Furnace, out _); // 范围只有精炼炉：电塔被摧毁只是一条“不归它管”的事件
            Seconds(0.5f);
            int cost = BuildingOps.RebuildCost(f, out _);
            BuildingOps.ApplyDamage(s, f.BuildingId, 10000f);
            Seconds(0.5f);
            foreach (RuleEventRecord e in s.StandingRules.Pending)
            {
                if (e != null)
                {
                    e.IssueArg = null; // 旧档没有这个字段
                }
            }
            r.IssueKey = string.Empty;
            r.IssueArg = string.Empty;
            BuildingOps.ApplyDamage(s, other.BuildingId, 10000f); // 下一个模拟步处理这条事件（不是定时重试）
            Seconds(0.3f);
            string issue = StandingRuleService.IssueText(r) ?? string.Empty;
            bool ok = r.IssueKey == "rules.issue.rebuild_wait" && issue.Contains(cost.ToString(CultureInfo.InvariantCulture)) && !issue.Contains("{") && !issue.Contains("⟦")
                      && HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null;
            Expect(ok, $"D5j 旧档等待记录没有原因参数：下一次处理事件时规则原因补成“{issue}”（含重建费 {cost}、不缺参数）（{ok}）");
        }

        // ── 独立判据（EMG-R 出口补缺口第 4 轮，审查 P2）：直接查斑块状态与已知布置，不借被测函数 StructHazardIn ──────────────

        /// <summary>本段测试布置的会烧蚀设施的物质（明火 / 熔融 / 酸；进过火的机器过热会漏熔融）。产品按物质数值 structDps 判，这里按已知布置判。</summary>
        private static bool HotMatter(byte m) => m == EmgMatterConst.Fire || m == EmgMatterConst.Molten || m == EmgMatterConst.Acid;

        /// <summary>这一格的格心在某块明火 / 熔融 / 酸斑块的圆盘里（离边 0.25 格以内不算，避开取整差）。</summary>
        private static bool HotAt(GridCell c)
        {
            for (int i = 0; i < Site.Kernel.PatchSlots; i++)
            {
                if (Site.Kernel.TryGetPatch(i, out CombatZone z) && HotMatter(z.Matter) && z.Radius > 0f && math.distance(z.Pos, new double2(c.X, c.Y)) <= z.Radius - 0.25f)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>这一格周围（圆盘半径 + 1 格以内）一块明火 / 熔融 / 酸斑块都没有 = 火灭了。</summary>
        private static bool ColdAt(GridCell c)
        {
            for (int i = 0; i < Site.Kernel.PatchSlots; i++)
            {
                if (Site.Kernel.TryGetPatch(i, out CombatZone z) && HotMatter(z.Matter) && math.distance(z.Pos, new double2(c.X, c.Y)) <= z.Radius + 1f)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HotIn(GridCell min, GridCell max)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    if (HotAt(new GridCell(x, y)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ColdIn(GridCell min, GridCell max)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    if (!ColdAt(new GridCell(x, y)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>这片格子的格心都在某块水斑块的圆盘里。</summary>
        private static bool WetIn(GridCell min, GridCell max)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    bool wet = false;
                    for (int i = 0; i < Site.Kernel.PatchSlots && !wet; i++)
                    {
                        wet = Site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Water && math.distance(z.Pos, new double2(x, y)) <= z.Radius;
                    }
                    if (!wet)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool AnyPatchNear(GridCell c, float range, byte matter)
        {
            for (int i = 0; i < Site.Kernel.PatchSlots; i++)
            {
                if (Site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == matter && math.distance(z.Pos, new double2(c.X, c.Y)) <= range)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>诊断：离 <paramref name="c"/> <paramref name="range"/> 格内的活斑块（物质号 @ 中心 r 半径）。</summary>
        private static string PatchesNear(GridCell c, float range)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Site.Kernel.PatchSlots; i++)
            {
                if (Site.Kernel.TryGetPatch(i, out CombatZone z) && math.distance(z.Pos, new double2(c.X, c.Y)) <= range)
                {
                    sb.Append(z.Matter).Append('@').Append(z.Pos.x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(z.Pos.y.ToString("0.#", CultureInfo.InvariantCulture))
                        .Append(" r").Append(z.Radius.ToString("0.##", CultureInfo.InvariantCulture)).Append("; ");
                }
            }
            return sb.Length == 0 ? "无" : sb.ToString();
        }

        private static void CheckZoneNegatives()
        {
            CampaignState s = NewWorld(6312);
            ClearRules(s);
            StandingRuleRecord r = NewRebuildRule(s);
            bool small = !StandingRuleService.TryAddZone(s, r.Serial, new GridCell(0, 0), new GridCell(0, 2), out _, out string m1) && m1.Contains("至少");
            StandingRuleService.TryCreate(s, StandingRuleService.KindWar, out StandingRuleRecord war, out _);
            bool wrong = !StandingRuleService.TryAddZone(s, war.Serial, new GridCell(0, 0), new GridCell(3, 3), out _, out string m2) && m2.Contains("自动重建");
            int added = 0;
            for (int i = 0; i < StandingRuleService.ZoneMax; i++)
            {
                if (StandingRuleService.TryAddZone(s, r.Serial, new GridCell(i * 4, 0), new GridCell(i * 4 + 2, 2), out _, out _))
                {
                    added++;
                }
            }
            bool over = StandingRuleService.TryAddZone(s, r.Serial, new GridCell(0, 10), new GridCell(3, 13), out _, out string m3);
            bool max = added == StandingRuleService.ZoneMax && !over && m3.Contains("最多");
            int z0 = r.Zones[0].Serial;
            bool removed = StandingRuleService.TryRemoveZone(s, r.Serial, z0, out _) && r.Zones.Length == StandingRuleService.ZoneMax - 1 && StandingRuleService.FindZone(r, z0) == null;
            // 复制规则：区域一起复制、新编号。
            StandingRuleService.TryCopy(s, r.Serial, out StandingRuleRecord copy, out _);
            bool copied = copy != null && copy.Zones.Length == r.Zones.Length && copy.Zones.All(z => r.Zones.All(o => o.Serial != z.Serial)) && !copy.Enabled;
            // 坏存档：两角颠倒的摆正、重复编号重编、空项去掉，编号计数补齐。
            var st = new StandingRuleState();
            st.Rules = new[]
            {
                new StandingRuleRecord { Serial = 1, Kind = StandingRuleService.KindRebuild, Zones = new[] { new RebuildZoneRecord { Serial = 3, X0 = 5, Y0 = 9, X1 = 1, Y1 = 2 }, null, new RebuildZoneRecord { Serial = 3, X0 = 0, Y0 = 0, X1 = 2, Y1 = 2 } } },
                new StandingRuleRecord { Serial = 2, Kind = StandingRuleService.KindRebuild, Zones = null },
            };
            st.NextZoneSerial = 1;
            CampaignFgStateDomains.EnsureRules(st);
            StandingRuleRecord withZones = st.Rules.First(x => x.Kind == StandingRuleService.KindRebuild && x.Serial == 1);
            StandingRuleRecord noZones = st.Rules.First(x => x.Kind == StandingRuleService.KindRebuild && x.Serial == 2);
            RebuildZoneRecord[] zs = withZones.Zones;
            bool clamped = zs.Length == 2 && zs[0].X0 == 1 && zs[0].X1 == 5 && zs[0].Y0 == 2 && zs[0].Y1 == 9 && zs[1].Serial != zs[0].Serial && st.NextZoneSerial > Math.Max(zs[0].Serial, zs[1].Serial)
                           && noZones.Zones != null && noZones.Zones.Length == 0;
            Expect(small && wrong && max && removed && copied && clamped,
                $"D7 区域负向：小于 {StandingRuleService.ZoneMinCells} 格拒绝（“{m1}”）；不是自动重建规则拒绝（“{m2}”）；最多 {StandingRuleService.ZoneMax} 个（“{m3}”）；删掉一个；复制规则时区域一起复制（新编号、先停用）；" +
                $"坏存档：两角颠倒摆正、重复编号重编、空项去掉、旧档没有区域补空（{small}/{wrong}/{max}/{removed}/{copied}/{clamped}，加了 {added} 个）" + (clamped ? string.Empty : "\n" + Json(st)));
        }

        // ── E 建造模式 / 面板 / 画面 ─────────────────────────────────────────────

        private static void CheckBuildModeAndPanel()
        {
            CampaignState s = NewWorld(6313);
            ClearRules(s);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FgProductionSelfCheck.FindArea(s, 5, 4, 9f, 22f);
            if (mode == null || !area.HasValue)
            {
                Fail("E 测试准备：找不到家园建造模式或 5×4 空地");
                return;
            }
            Func<bool> before = ResearchGate.TreeAvailableOverrideForTests;
            GameObject panelGo = null, hudGo = null;
            var rig = new GameObject("__fgrepair_zone_views") { hideFlags = HideFlags.HideAndDontSave };
            RulesPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "RulesPanel.uxml", out panelGo);
                RulesPanelUIToolkit panel = panelGo.AddComponent<RulesPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RulesRow.uxml"));
                VisualElement hudRoot = FgProductionSelfCheck.MountUxml(UiKitFolder + "BuildModeHud.uxml", out hudGo);
                BuildModeHudUIToolkit hud = hudGo.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(hudRoot);
                RulesPanelUIToolkit.Open();
                panel.Refresh();
                // 没研究：新建下拉里写“（需要研究）”，选它写明原因。
                ResearchGate.TreeAvailableOverrideForTests = () => true;
                ResearchService.ResetForTests();
                s.Research.CompletedNodes = s.Research.CompletedNodes.Where(n => n != "defense.auto_rebuild").ToArray();
                panel.Select(0);
                panel.Refresh();
                int lockedIndex = panel.NewKindField.choices.FindIndex(c => c.Contains("自动重建"));
                bool lockedLabel = lockedIndex > 0 && panel.NewKindField.choices[lockedIndex].Contains("需要研究");
                RulesPanelUIToolkit.PickForTests(panel.NewKindField, lockedIndex);
                bool lockedMsg = panel.MessageText.Contains("防御 · 自动重建") && StandingRuleService.Ordered(s).All(x => x.Kind != StandingRuleService.KindRebuild);
                ResearchService.CompleteForTests(s, "defense.auto_rebuild");
                panel.Select(0);
                int kindIndex = panel.NewKindField.choices.FindIndex(c => c.Contains("自动重建") && !c.Contains("需要研究"));
                RulesPanelUIToolkit.PickForTests(panel.NewKindField, kindIndex);
                StandingRuleRecord r = StandingRuleService.Find(s, panel.SelectedSerial);
                panel.Select(panel.SelectedSerial); // 直接重建编辑区（异常照常抛出，不被下拉回调吞掉）
                bool rowShown = r != null && r.Kind == StandingRuleService.KindRebuild && panel.EditorRowVisible("RulesRowZones") && panel.ZonesText.Contains("还没有重建区域")
                                && !panel.ZoneToggleField.enabledSelf && panel.TargetAddField.choices.Any(c => c.Contains("传送带与物流节点"));
                Expect(lockedLabel && lockedMsg && rowShown,
                    $"E1 规则面板（{lockedLabel}/{lockedMsg}/{rowShown}，第 {lockedIndex} 项，选项：{string.Join("、", panel.NewKindField.choices)}）：没研究时“新建”下拉写“（需要研究）”，选它写明原因（“{panel.MessageText}”）；研究后新建自动重建规则，编辑区显示重建区域行（“{panel.ZonesText}”），范围候选含“传送带与物流节点”");

                if (r == null)
                {
                    return;
                }
                // “在地图上圈一块” → 面板关闭、建造模式进入重建区域模式；真实鼠标路径拖框圈区域，点区域里面开 / 关，右键退出。
                panel.DrawZone();
                bool entered = !RulesPanelUIToolkit.IsOpen && mode.IsOpen && mode.ZoneMode && mode.ZoneRuleSerial == r.Serial;
                hud.Refresh();
                string modeText = hud.ModeText;
                GridCell a = area.Value, b = FgProductionSelfCheck.At(area.Value, 3, 2);
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                mode.RefreshVisuals(s);
                hud.Refresh();
                bool dragging = mode.Drag == HomeValleyBuildMode.DragKind.ZoneBox && hud.DragInfoText.Contains("4×3") && mode.BoxIndicatorVisible;
                mode.PointerUp(s, b);
                bool added = r.Zones.Length == 1 && r.Zones[0].X0 == a.X && r.Zones[0].X1 == b.X && r.Zones[0].Enabled && mode.StatusText.Contains("Z" + r.Zones[0].Serial)
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RulesRebuildZoneFirstDrawn);
                RepairDroneViews.FrameUpdate(s, rig.transform, mode);
                bool drawn = RepairDroneViews.ZoneCount == 1 && RepairDroneViews.ZoneOnCount == 1;
                GridCell inside = FgProductionSelfCheck.At(area.Value, 1, 1);
                mode.PointerDown(s, inside);
                mode.PointerUp(s, inside);
                RepairDroneViews.FrameUpdate(s, rig.transform, mode);
                bool toggled = !r.Zones[0].Enabled && mode.StatusText.Contains("关") && RepairDroneViews.ZoneOnCount == 0 && RepairDroneViews.ZoneCount == 1;
                GridCell tiny = FgProductionSelfCheck.At(area.Value, 4, 3);
                mode.PointerDown(s, tiny);
                mode.PointerUp(s, tiny);
                bool tinyRefused = r.Zones.Length == 1 && mode.StatusIsError && mode.StatusText.Contains("至少");
                mode.RightClick();
                bool left = !mode.ZoneMode && mode.IsOpen;
                Expect(entered && modeText.Contains("重建区域") && dragging && added && drawn && toggled && tinyRefused && left,
                    $"E2 重建区域模式（正式输入）：面板“在地图上圈一块”进入（HUD“{modeText}”）；拖框 4×3（建造栏“{hud.DragInfoText}”）松开圈出区域、地面画出边框（{drawn}）；点区域里面 = 关掉（边框变细灰）（{toggled}）；" +
                    $"点空地一格 = 太小被拒、写原因（{tinyRefused}）；右键退出重建区域模式（{left}）");

                // 面板里开 / 关、看看在哪、删掉（下拉选中即生效）。
                RulesPanelUIToolkit.Open();
                panel.Select(r.Serial);
                panel.Refresh();
                bool listed = panel.ZonesText.Contains("Z" + r.Zones[0].Serial) && panel.ZonesText.Contains("都关着");
                RulesPanelUIToolkit.PickForTests(panel.ZoneToggleField, 1);
                bool onAgain = r.Zones[0].Enabled;
                panel.Refresh();
                RulesPanelUIToolkit.PickForTests(panel.ZoneLocateField, 1);
                Vector3 center = StandingRuleService.ZoneCenter(r.Zones[0]);
                bool located = Vector2.Distance(panel.LastJumpPosition, new Vector2(center.x, center.z)) < 0.01f;
                panel.Refresh();
                RulesPanelUIToolkit.PickForTests(panel.ZoneRemoveField, 1);
                panel.Refresh();
                bool removed = r.Zones.Length == 0 && panel.ZonesText.Contains("还没有重建区域");
                Expect(listed && onAgain && located && removed, "E3 面板的区域行：列出区域与开关状态（全关时写明）；“开 / 关”下拉打开它；“看看在哪”镜头飞到区域中心；“删掉”后回到“整个家园”");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "RulesPanel.uxml", "RulesPanelWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("RulesPanelRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("RulesRowZones")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("RulesRowTargets")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "E3b 布局探针（四种分辨率 + 超长文字 + USS 体检）：规则面板显示重建区域行 " + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe));

                // 放置预览：选中维修无人机站悬停时地面画出覆盖范围圈。
                RulesPanelUIToolkit.Close();
                mode.Select(Station);
                mode.SetHover(s, FgProductionSelfCheck.At(area.Value, 1, 1));
                RepairDroneViews.FrameUpdate(s, rig.transform, mode);
                bool ring = RepairDroneViews.RangeShown;
                mode.ClearSelection();
                RepairDroneViews.FrameUpdate(s, rig.transform, mode);
                bool ringOff = !RepairDroneViews.RangeShown;
                mode.Close();
                Expect(ring && ringOff, "E4 放置维修无人机站时地面画出覆盖范围（16 米圈），取消选择后不画");
            }
            finally
            {
                ResearchGate.TreeAvailableOverrideForTests = before;
                RulesPanelUIToolkit.Close();
                RulesPanelUIToolkit.InWorldOverrideForTests = false;
                RepairDroneViews.Clear();
                Object.DestroyImmediate(rig);
                if (panelGo != null)
                {
                    Object.DestroyImmediate(panelGo);
                }
                if (hudGo != null)
                {
                    Object.DestroyImmediate(hudGo);
                }
            }
        }

        // ── F 存读档 ─────────────────────────────────────────────────────────────

        private static string Signature(CampaignState s, CombatSite site)
        {
            var sb = new StringBuilder();
            foreach (RepairStationRecord r in RepairDroneService.All(s))
            {
                sb.Append(r.BuildingId).Append(':').Append(r.KitCredit.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(r.RespawnTicks).Append(':').Append(r.KitsUsed).Append(':').Append(r.Lost).Append('[');
                foreach (DroneRecord d in r.Drones)
                {
                    sb.Append(d.Serial).Append('/').Append(d.State).Append('/').Append(d.Target).Append('/').Append(d.X.ToString("R", CultureInfo.InvariantCulture)).Append('/')
                        .Append(d.Y.ToString("R", CultureInfo.InvariantCulture));
                    if (site.TryGetDroneState(d.Serial, out _, out float hp, out _, out bool alive))
                    {
                        sb.Append('/').Append(hp.ToString("R", CultureInfo.InvariantCulture)).Append(alive ? "A" : "D");
                    }
                    sb.Append(';');
                }
                sb.Append(']');
            }
            foreach (BuildingRecord b in s.BuildingRecords.Where(x => x.BuildingTypeId == Furnace).OrderBy(x => x.BuildingId, StringComparer.Ordinal))
            {
                sb.Append('|').Append(b.Health.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(b.ConstructionState);
            }
            return sb.Append("|kits=").Append(Kits(s)).Append("|scrap=").Append(s.Scrap).ToString();
        }

        /// <summary>存档时刻：两架出动（一架修理中）、一架补充中、维修件余额有零头、规则有区域、建筑挨打时间戳。</summary>
        private static CampaignState Scenario(int seed, out BuildingRecord st)
        {
            CampaignState s = NewWorld(seed, kits: 30, scrap: 600);
            st = StationAt(s);
            if (st == null)
            {
                return s;
            }
            for (int i = 0; i < 3; i++)
            {
                BuildingRecord f = PlaceNear(s, Furnace, st.Position, 5f + i * 3f, 14f);
                if (f != null)
                {
                    Damage(s, f, 0.7f);
                }
            }
            StandingRuleRecord r = NewRebuildRule(s);
            StandingRuleService.TryAddZone(s, r.Serial, new GridCell(st.GridX - 5, st.GridY - 5), new GridCell(st.GridX + 5, st.GridY + 5), out _, out _);
            Seconds(1.2f);
            RepairStationRecord rec = Rec(s, st);
            DroneRecord victim = rec.Drones.LastOrDefault(d => d.State != RepairDroneService.StateDocked);
            if (victim != null)
            {
                Site.DamageDrone(victim.Serial, 10000f);
            }
            Seconds(2.37f); // 停在两次找目标之间
            return s;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = Scenario(6314, out BuildingRecord st);
            if (st == null)
            {
                return;
            }
            CombatSite site = Site;
            WorldSimulation.SyncAllForSave();
            string domain = Json(DefenseService.StateOf(s));
            string rules = Json(s.StandingRules);
            string sig = Signature(s, site);
            int units = site.DroneUnitCount;
            RepairStationRecord rec = Rec(s, st);
            bool richState = rec.Drones.Any(d => d.State != RepairDroneService.StateDocked) && rec.RespawnTicks >= 0 && units >= 1;
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
                Fail("F1 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            CampaignState l = rr.State;
            string domainAfter = Json(DefenseService.StateOf(l));
            string rulesAfter = Json(l.StandingRules);
            string sigAfter = Signature(l, Site);
            Expect(save.Success && richState && domain == domainAfter && rules == rulesAfter && sig == sigAfter && Site.DroneUnitCount == units,
                $"F1 真文件存读档：维修无人机域（站点、无人机状态 / 位置 / 目标、补充计时、维修件余额、统计）逐字段一致（{domain == domainAfter}）；规则的重建区域一致（{rules == rulesAfter}）；" +
                $"内核里出动的无人机 {units} 架连同耐久找回（{sig == sigAfter}）" + (sig == sigAfter ? string.Empty : $"\n存档前：{sig}\n读档后：{sigAfter}"));

            Seconds(25f);
            RepairStationRecord lrec = RepairDroneService.Find(l, st.BuildingId);
            bool continues = lrec != null && lrec.Drones.Length == 3 && l.BuildingRecords.Where(x => x.BuildingTypeId == Furnace).All(x => Full(l, x));
            Expect(continues, "F2 读档后接着跑：补充计时接着走（补满 3 架），出动的无人机接着把受损的修满");

            // 旧档 / 坏值。
            CampaignState old = CampaignState.CreateNew("fgrepair-old", "Standard", 6315);
            old.Raids.Defense.Stations = null;
            CampaignFgStateDomains.EnsureAll(old);
            bool empty = old.Raids.Defense.Stations != null && old.Raids.Defense.Stations.Length == 0;
            old.Raids.Defense.Stations = new[]
            {
                new RepairStationRecord { BuildingId = "home:repair_drone_station#1", KitCredit = float.NaN, RespawnTicks = -7, Drones = new[]
                {
                    new DroneRecord { Serial = 4, State = 9, Hp = float.NaN, Target = null },
                    new DroneRecord { Serial = 4, State = 1, X = float.NaN, Hp = 5f },
                    null,
                } },
                new RepairStationRecord { BuildingId = "home:repair_drone_station#1" },
                null,
            };
            old.Raids.Defense.NextDroneSerial = 1;
            RepairDroneService.EnsureState(old);
            RepairStationRecord[] sts = old.Raids.Defense.Stations;
            DroneRecord[] ds = sts.Length == 1 ? sts[0].Drones : Array.Empty<DroneRecord>();
            bool clamped = empty && sts.Length == 1 && sts[0].KitCredit == 0f && sts[0].RespawnTicks == -1 && ds.Length == 2 && ds[0].State == 0 && ds[0].Hp == RepairDroneCatalog.Hp
                           && ds[0].Target == string.Empty && ds[1].State == 0 && ds[1].Serial != ds[0].Serial && old.Raids.Defense.NextDroneSerial > ds.Max(d => d.Serial);
            Expect(clamped, "F3 旧档没有维修无人机域补成空域；坏值：余额不是数归 0、补充计时钳回、状态越界归“停在站里”、耐久坏值补满、位置坏值回站、重复序号重编、重复站点只留第一条");
        }

        /// <summary>F4：同一份存档，“存完接着跑”与“读档后跑”逐字段一致（找目标按步序号、飞行 / 修理按步、补充按步数，读档不改变结果）。</summary>
        private static void CheckLoadContinuation()
        {
            CampaignState s = Scenario(6316, out BuildingRecord st);
            if (st == null)
            {
                return;
            }
            WorldSimulation.SyncAllForSave();
            long tickAtSave = GameClock.Ticks;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(8f);
            string straight = Signature(s, Site);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("F4 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            bool sameTick = GameClock.Ticks == tickAtSave;
            Seconds(8f);
            string loaded = Signature(rr.State, Site);
            Expect(save.Success && sameTick && straight == loaded,
                $"F4 读档接着跑 = 不存档一路跑：存档停在两次找目标之间（步 {tickAtSave}），之后各跑 8 游戏秒，无人机状态 / 位置 / 目标 / 耐久、补充计时、维修件、建筑耐久逐字段一致"
                + (straight == loaded ? string.Empty : $"\n不存档：{straight}\n读档后：{loaded}"));
        }

        // ── G / H 暂停、倍速、观察 ─────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe, kits: 30, scrap: 600);
            pausedHeld = true;
            BuildingRecord st = StationAt(s);
            if (st == null)
            {
                return "no-station";
            }
            for (int i = 0; i < 3; i++)
            {
                BuildingRecord f = PlaceNear(s, Furnace, st.Position, 5f + i * 3f, 14f);
                if (f != null)
                {
                    Damage(s, f, 0.8f);
                }
            }
            Seconds(1.2f);
            RepairStationRecord rec = Rec(s, st);
            DroneRecord victim = rec.Drones.FirstOrDefault(d => d.State != RepairDroneService.StateDocked);
            if (victim != null)
            {
                Site.DamageDrone(victim.Serial, 10000f);
            }
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Json(DefenseService.StateOf(s)) + Site.Kernel.StateHash() + RepairDroneService.ScanCount;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Json(DefenseService.StateOf(s)) + Site.Kernel.StateHash() + RepairDroneService.ScanCount == p0;
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
            return Signature(s, Site) + "|" + Site.Kernel.StateHash().ToString("X16");
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(6317, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != null && !reference.StartsWith("no-station", StringComparison.Ordinal),
                "G 暂停中（120 帧）维修无人机域、内核状态、找目标次数都不动；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒（出动 → 修理 → 返航 → 被击落 → 补充），无人机 / 补充 / 维修件 / 建筑耐久与内核状态哈希逐字段一致"
                + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6318, true, 1f, false, out _);
            string unseen = RunScenario(6318, false, 1f, false, out _);
            Expect(seen == unseen && !seen.StartsWith("no-station", StringComparison.Ordinal),
                "H 同一场维修（含击落与补充）在观察与不观察家园时跑 30 游戏秒，结果逐字段一致（FGR-BASE-021：远征时家园的维修无人机照常工作）"
                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        /// <summary>B25：种子测试集里几张不同地形——按地形找空地放站点与受损建筑，都能修满，不依赖固定坐标。</summary>
        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(4).ToArray();
            var bad = new List<string>();
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed, kits: 20);
                BuildingRecord st = StationAt(s);
                BuildingRecord f = st != null ? PlaceNear(s, Furnace, st.Position, 5f, 13f) : null;
                if (f == null)
                {
                    bad.Add($"{seed}：放不下");
                    continue;
                }
                Damage(s, f, 0.6f);
                if (!StepUntil(() => Full(s, f), 60))
                {
                    bad.Add($"{seed}：没修满（{BuildingOps.Durability(f):F0}）");
                }
            }
            Expect(seeds.Length == 4 && bad.Count == 0,
                $"B25 种子测试集 {string.Join(" / ", seeds)}：每张地形都按地形找到空地放下维修无人机站与受损建筑，无人机修满" + (bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad)));
        }

        // ── J 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6319, kits: 900, scrap: 9000);
            Vector2 core = CoreCenter(s);
            var stations = new List<BuildingRecord>();
            for (int i = 0; i < 10; i++)
            {
                BuildingRecord st = PlaceNear(s, Station, core, 8f, 40f);
                if (st != null)
                {
                    stations.Add(st);
                }
            }
            // 测试捷径：电力由电网自检覆盖，这里让全部站点吃到电（测的是找目标 / 飞行 / 修理，不是电网分配）。
            foreach (BuildingRecord b in stations)
            {
                b.PowerState = BuildingPowerState.Powered;
            }
            GridCell pivot = HomeGridService.CorePivot(s);
            var walls = new List<BuildingRecord>();
            for (int ringR = GridContent.TuningInt("grid.core_reserve_ring") + 6; ringR <= 60 && walls.Count < 300; ringR += 2)
            {
                for (int k = -ringR; k <= ringR && walls.Count < 300; k++)
                {
                    foreach (GridCell c in new[] { new GridCell(pivot.X + k, pivot.Y - ringR), new GridCell(pivot.X + k, pivot.Y + ringR), new GridCell(pivot.X - ringR, pivot.Y + k), new GridCell(pivot.X + ringR, pivot.Y + k) })
                    {
                        if (walls.Count < 300 && HomeGridService.ValidatePlacement(s, DefenseCatalog.BarrierT1, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            BuildingRecord w = Register(s, DefenseCatalog.BarrierT1, c);
                            w.Health = BuildingOps.MaxDurability(DefenseCatalog.BarrierT1) * 0.3f;
                            w.LastHitTick = 1;
                            walls.Add(w);
                        }
                    }
                }
            }
            StandingRuleService.RaidActiveProvider = _ => true;
            RepairDroneService.Sync(s);
            DefenseService.Sync(s);
            Seconds(2f);
            int hz = GameClock.StepHz;
            var stepMs = new List<double>(600);
            var droneMs = new List<double>(600);
            var sw = new Stopwatch();
            for (int i = 0; i < 600; i++)
            {
                sw.Restart();
                WorldSimulation.StepMany(1);
                sw.Stop();
                stepMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            // 只测维修无人机服务：600 步里有 600 / (scan × hz) 次找目标。
            long tick = GameClock.Ticks;
            long every = Math.Max(1, (long)Math.Round(RepairDroneCatalog.ScanSeconds * hz));
            var scanMs = new List<double>(16);
            for (int i = 0; i < 600; i++)
            {
                long t = tick + i;
                sw.Restart();
                RepairDroneService.WorldStep(s, t, hz);
                sw.Stop();
                (t % every == 0 ? scanMs : droneMs).Add(sw.Elapsed.TotalMilliseconds);
            }
            StandingRuleService.RaidActiveProvider = null;
            stepMs.Sort();
            double stepAvg = stepMs.Average();
            double stepP95 = stepMs[(int)(stepMs.Count * 0.95)];
            double droneAvg = droneMs.Count > 0 ? droneMs.Average() : 0;
            double scanAvg = scanMs.Count > 0 ? scanMs.Average() : 0;
            int outNow = RepairDroneService.All(s).Sum(r => Out(r));
            var rig = new GameObject("__fgrepair_perf_views") { hideFlags = HideFlags.HideAndDontSave };
            double viewMs;
            try
            {
                RepairDroneViews.FrameUpdate(s, rig.transform, null);
                var fsw = Stopwatch.StartNew();
                for (int i = 0; i < 600; i++)
                {
                    RepairDroneViews.FrameUpdate(s, rig.transform, null);
                }
                fsw.Stop();
                viewMs = fsw.Elapsed.TotalMilliseconds / 600.0;
            }
            finally
            {
                RepairDroneViews.Clear();
                Object.DestroyImmediate(rig);
            }
            PerfLines.Add($"家园 {stations.Count} 座维修无人机站（{outNow} 架出动）+ {walls.Count} 段受损屏障（突袭中、都在挨打）：世界单步 平均 {stepAvg:F3} ms / p95 {stepP95:F3} ms；" +
                          $"维修无人机每步（不找目标）{droneAvg:F4} ms；找目标一次 {scanAvg:F3} ms（每 {RepairDroneCatalog.ScanSeconds} 秒一次）；画面每帧 {viewMs:F4} ms；120 帧标准的帧预算 8.33 ms");
            PerfGate.Expect(stations.Count >= 8 && walls.Count >= 250 && outNow >= 20,
                $"J 性能：{stations.Count} 座站 + {walls.Count} 个受损目标，世界单步平均 {stepAvg:F3} ms / p95 {stepP95:F3} ms（阈值 8 ms，含全部系统）；维修无人机每步 {droneAvg:F4} ms（阈值 0.1 ms）；" +
                $"找目标 {scanAvg:F3} ms/次（阈值 2 ms，每秒一次）；画面每帧 {viewMs:F4} ms（阈值 0.05 ms）（Editor batchmode，真机另测 FG15-SYS-02）",
                new[]
                {
                    PerfGate.Le(stepAvg, 8, "世界单步平均 ms"), PerfGate.Le(droneAvg, 0.1, "维修无人机每步 ms"), PerfGate.Le(scanAvg, 2, "找目标 ms/次"), PerfGate.Le(viewMs, 0.05, "画面每帧 ms"),
                }, Expect, Line);
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
