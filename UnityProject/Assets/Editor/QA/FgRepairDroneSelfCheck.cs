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

        [MenuItem("BinGames/QA/自检/FG 维修无人机与自动重建")]
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
