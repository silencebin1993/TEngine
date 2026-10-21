using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
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
    /// FG8-OUT-01 前哨站与远程施工的自动验收（FG08 FGR-EXP-017～019；FG04 FGR-ECO-040 前哨劳动；FG13 FGU-47；FGT-EXP-004“前哨站：自动分组、远程施工、补给、被突袭、全毁不导致失败”；
    /// 卡片负向“前哨站与家园电网断开 / 前哨劳动的机器全部阵亡 / 前哨站被突袭时玩家在远处（后台一致）”；承接 DEBT-FG4ECO07-01、DEBT-FG6DEF04-14、DEBT-FG3LOG02-04）。
    /// 全部起真实系统：按种子生成的星球世界、家园战斗内核里的机器、真实派工（岗位维持器 / 派工评估）、真实传送带内核与端口对账、真实投送舱、真实突袭派出与攻城、真文件存读档。
    /// A 数据；B 自动分组 / 改名 / 身份稳定 / 合并 / 删除；C 前哨劳动（回驻点、只接本前哨的活、全部阵亡后由劳动岗接手）；D 远程施工三种来源（机器取料 / 传送带施工现场输入口 / 投送舱施工包）；
    /// E 供电与覆盖是否连通；F 突袭（目标读前哨站实体、全毁不选、全毁不导致失败、观察 / 不观察一致）；G 离家报告分组；H 存读档与读档接着跑；I 暂停与 0.5x～3x；J 种子集；K 界面；L 性能；M 旧档。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOutpostSelfCheck）。
    /// </summary>
    public static class FgOutpostSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 9;
        private const string Gen2 = HomeValleyLayout.BuildingTypeGenerator2;
        private const float OutpostDistance = 340f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 前哨站与远程施工")]
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
            Line("\n[前哨站与远程施工] 自动分组、前哨劳动、远程施工三种来源、供电 / 覆盖连通、突袭与全毁、离家报告分组、总览（FG8-OUT-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgoutpost-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                RaidCatalog.Reload();
                MachineRoster.Reload();
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
                     "前哨站对账 / 索引在热更层（Editor 下 Mono JIT，按世界步每游戏秒一次，O(前哨站的建筑数)；索引只在建筑列表变化时 O(建筑数) 重建），真机另测 FG15-SYS-02");

                Step(CheckData);
                Step(CheckGrouping);
                Step(CheckIdentity);
                Step(CheckLabor);
                Step(CheckLaborWiped);
                Step(CheckMachineCarry);
                Step(CheckBeltInlet);
                Step(CheckPack);
                Step(CheckDirectFillRace);
                Step(CheckPowerAndCoverage);
                Step(CheckRaidTargets);
                Step(CheckWipedNoLoss);
                Step(CheckWipedAutoRebuild);
                Step(CheckRaidObserved);
                Step(CheckIntercept);
                Step(CheckAwayReport);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckSeedSet);
                Step(CheckUi);
                Step(CheckPerformance);
                Step(CheckOldSave);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"前哨站自检抛异常：{e}");
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
                OutpostOverviewPanelUIToolkit.InWorldOverrideForTests = false;
                OutpostOverviewPanelUIToolkit.Close();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Close();
                OwnOutpostService.ResetSessionState();
                DropPodService.ResetSessionState();
                StandingRuleService.ResetForTests();
                TurretService.ResetSessionState();
                SignalUplinkService.SilentNightProvider = null;
                SignalCoreService.ResetForTests();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                HomeValleyCombatTargets.ResetSessionState();
                MachineRoster.ResetForTests();
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
                WorldGenKernel.ReleaseAll();
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
            Line($"  · [前哨站与远程施工] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>与主菜单“新建”同一入口：按种子生成星球世界、载入家园；开局的机器改为闲置（不让它们跑去前哨站干活，干扰各段对照），家园有电。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
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
            MachineRoster.ResetForTests();
            SignalUplinkService.SilentNightProvider = null;
            OwnOutpostService.ResetSessionState();
            DropPodService.ResetSessionState();
            NotificationCenter.ResetForTests();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            _seq = 100;
            CampaignState s = CampaignState.CreateNew("fgoutpost-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 2000;
            FgProductionSelfCheck.PowerUp(s);
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            WorldSimulation.StepMany(2);
            foreach (MachineRecord m in MachineRegistry.AllRecords.ToList())
            {
                if (m != null && m.IsAlive)
                {
                    MachineRoster.TrySetRole(s, m.LogicId, MachineRole.Idle, out _);
                }
            }
            WorldSimulation.StepMany(2);
            return s;
        }

        private static GridCell Core(CampaignState s) => HomeGridService.CorePivot(s);

        /// <summary>离核心约 <paramref name="dist"/> 格、能放下至少 <paramref name="need"/> 座发电机 2 的一片（先揭开那一片；按种子地形找，B25）。第 <paramref name="skip"/> 个合格的方向。</summary>
        private static Vector2? FindSpot(CampaignState s, float dist, int need = 6, int skip = 0, float angleOffset = 0f)
        {
            GridCell core = Core(s);
            int found = 0;
            for (int a = 0; a < 36; a++)
            {
                float ang = (a * 10f + angleOffset) * Mathf.Deg2Rad;
                var p = new Vector2(Mathf.Round(core.X + Mathf.Cos(ang) * dist), Mathf.Round(core.Y + Mathf.Sin(ang) * dist));
                HomeGridService.RevealArea(s, p, 50f);
                int ok = 0;
                for (int r = 0; r <= 18 && ok < need; r += 3)
                {
                    for (int k = 0; k < 12 && ok < need; k++)
                    {
                        float t = k * 30f * Mathf.Deg2Rad;
                        var c = new GridCell(Mathf.RoundToInt(p.x + Mathf.Cos(t) * r), Mathf.RoundToInt(p.y + Mathf.Sin(t) * r));
                        ok += HomeGridService.ValidatePlacement(s, Gen2, c, 0, asPlayerPlacement: false, checkCost: false).Ok ? 1 : 0;
                    }
                }
                if (ok >= need && found++ == skip)
                {
                    return p;
                }
            }
            Fail($"测试准备：离核心 {dist} 格找不到能放 {need} 座建筑的一片");
            return null;
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（施工流程由 D 段与 FG3-LOG-02 覆盖），电网 / 端口对账跟上。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c, int rotation = 0)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#op" + (_seq++).ToString(CultureInfo.InvariantCulture),
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

        /// <summary><paramref name="center"/> 周围 [from, to] 格按格网规则找空地放一座（建成）。</summary>
        private static BuildingRecord PlaceNear(CampaignState s, string type, Vector2 center, float from, float to)
        {
            GridCell? c = FreeNear(s, type, center, from, to);
            return c.HasValue ? Register(s, type, c.Value) : null;
        }

        private static GridCell? FreeNear(CampaignState s, string type, Vector2 center, float from, float to)
        {
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(center.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(center.y + Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return c;
                    }
                }
            }
            Fail($"测试准备：{center} 周围 {from}～{to} 格找不到能放 {type} 的空地");
            return null;
        }

        /// <summary>真实玩家入口放一座虚影（施工单 + 机器取料施工），返回虚影。</summary>
        private static BuildingRecord Ghost(CampaignState s, string type, Vector2 center, float from, float to)
        {
            GridCell? c = FreeNear(s, type, center, from, to);
            if (!c.HasValue)
            {
                return null;
            }
            GridOpResult r = HomeGridService.TryPlace(s, type, c.Value, 0);
            if (!r.Success)
            {
                Fail("测试准备：放虚影失败：" + r.Reason?.TextKey);
                return null;
            }
            return HomeGridService.FindBuilding(s, r.BuildingId);
        }

        private static WorkOrderRecord BuildOrder(CampaignState s, string buildingId) => HomeValleyWorkOrders.FindActiveBuild(s, buildingId);

        /// <summary>一个 3 座建成建筑的前哨站（离核心约 340 格），对账一次，返回记录。</summary>
        private static OwnOutpostRecord MakeOutpost(CampaignState s, out Vector2 center, int skip = 0, int buildings = 3, float angleOffset = 0f, bool cover = false)
        {
            center = default;
            Vector2? p = FindSpot(s, OutpostDistance, 6, skip, angleOffset);
            if (!p.HasValue)
            {
                return null;
            }
            center = p.Value;
            for (int i = 0; i < buildings; i++)
            {
                PlaceNear(s, Gen2, center, i * 4f, 18f);
            }
            if (cover)
            {
                CoverOutpost(s, center);
            }
            OwnOutpostService.Sync(s);
            return OwnOutpostService.Nearest(s, center);
        }

        private static int SpawnMachine(CampaignState s, Vector2 at, MachineRole role = MachineRole.Labor)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                Fail("测试准备：刷机器失败：" + r.Message);
                return 0;
            }
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec) && !string.IsNullOrEmpty(rec.BlueprintId))
            {
                MachineLoadoutRegistry.Register(s, r.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            WorldSimulation.StepMany(2);
            if (role != MachineRole.Labor)
            {
                MachineRoster.TrySetRole(s, r.LogicId, role, out _);
            }
            return r.LogicId;
        }

        private static MachineRecord M(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord r) ? r : null;

        private static Vector2 PosOf(int id) => MachineRegistry.TryGetLivePosition(id, out Vector2 p) ? p : M(id)?.WorldPosition ?? Vector2.zero;

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        /// <summary>FG9-UX-01：图鉴回到空白（临时图鉴文件删掉重读、清本局遭遇去重）。图鉴跨存档，不随 NewWorld 复位。</summary>
        private static void FreshCodex()
        {
            string codexFile = MechanicCodex.FilePathOverrideForTests;
            if (codexFile != null && File.Exists(codexFile))
            {
                File.Delete(codexFile);
            }
            MechanicCodex.Reload();
            MechanicCodex.ResetContentForTests();
        }

        private static string UnlockedEnemies() =>
            string.Join(",", EnemyDefs.Rows.Where(r => MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId(r.Id))).Select(r => r.Id));

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        /// <summary>某类通知最近一条的正文（同类通知在窗口内会聚合成“N 条…”，这里取最近那一条成员的详情）。</summary>
        private static string LastNotify(string typeId) =>
            NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Latest?.DetailText ?? string.Empty;

        /// <summary>
        /// 把信号覆盖延伸到 <paramref name="center"/>（前哨站）：沿核心 → 前哨站方向在离核心约 130 格、280 格各放一座中继塔（建成即提供覆盖、不用电；
        /// 都在 300 格以内，不会自己成为前哨站）。FGR-SIG-053：覆盖外的机器收不到家园派工，前哨劳动要靠覆盖接活（FG08“前哨站常见用途：延伸信号覆盖”）。
        /// </summary>
        private static void CoverOutpost(CampaignState s, Vector2 center)
        {
            GridCell core = Core(s);
            var c0 = new Vector2(core.X, core.Y);
            Vector2 dir = (center - c0).normalized;
            foreach (float d in new[] { 130f, 280f })
            {
                Vector2 p = c0 + dir * d;
                HomeGridService.RevealArea(s, p, 25f);
                PlaceNear(s, HomeValleyLayout.BuildingTypeSignalRelay, p, 0f, 12f);
            }
            SignalCoverageService.Invalidate();
            WorldSimulation.StepMany(3);
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length > 90 ? s.Substring(0, 90) + "…" : s;

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.Tuning("map.own_outpost_distance") == 300f && OwnOutpostService.SyncSeconds > 0f && OwnOutpostService.PackRadius > 0f
                          && OwnOutpostService.StationRadius > 0f && OwnOutpostService.MinBuildings == 1;
            bool texts = new[] { "outpost.title", "outpost.notify.founded", "outpost.notify.wiped", "outpost.about", "roster.outpost.ok", "away.section.outpost", "raid.target.outpost_named" }
                .All(k => GameText.Has(k));
            bool hooks = new[] { GuidanceHooks.OutpostFirstFounded, GuidanceHooks.OutpostPanelFirstOpen, GuidanceHooks.OutpostFirstRenamed, GuidanceHooks.OutpostFirstLabor,
                GuidanceHooks.OutpostFirstPack, GuidanceHooks.OutpostFirstWiped }.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType(OwnOutpostService.NotifyFounded, out _) && NotificationCatalog.TryGetType(OwnOutpostService.NotifyWiped, out NotifyTypeDef wiped)
                          && wiped.AwaySection == "raid" && NotificationCatalog.TryGetType(OwnOutpostService.NotifyRemoved, out _);
            KeyCode key = GameSettings.KeyBindings.GetKey(GameActionId.OpenOutposts);
            bool action = key == KeyCode.U && InputDisplay.ForAction(GameActionId.OpenOutposts).Contains("U");
            Expect(tuning && texts && hooks && notify && action,
                $"A 数据：前哨站距离 300 格（FG08 初值）与 outpost.* 调参、文本键、6 个引导钩子、3 种通知（全毁进离家报告“突袭”段）、总览快捷键 {InputDisplay.ForAction(GameActionId.OpenOutposts)}（可重绑）");
        }

        // ── B 自动分组 / 改名 / 身份 ─────────────────────────────────────────────────

        private static void CheckGrouping()
        {
            CampaignState s = NewWorld(8101);
            Vector2? near = FindSpot(s, 220f, 3);
            if (near.HasValue)
            {
                PlaceNear(s, Gen2, near.Value, 0f, 12f);
                PlaceNear(s, Gen2, near.Value, 4f, 12f);
            }
            Seconds(1.2f);
            bool nearNone = OwnOutpostService.All(s).Count == 0;
            int founded0 = NotifyCount(OwnOutpostService.NotifyFounded);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            bool one = OwnOutpostService.All(s).Count == 1 && o.OutpostId == "op-1" && OwnOutpostService.NameOf(o) == "前哨站 1" && o.Count == 3 && o.Intact == 3
                       && OwnOutpostService.Members(s, o.OutpostId).Count == 3;
            bool atMember = OwnOutpostService.Members(s, o.OutpostId).All(b => OwnOutpostService.OutpostIdAt(s, b.Position) == o.OutpostId)
                            && OwnOutpostService.OutpostIdAt(s, new Vector2(Core(s).X, Core(s).Y)) == null;
            float dist = OwnOutpostService.DistanceFromCore(s, o);
            Expect(nearNone && one && atMember && dist > 300f,
                $"B1 FGR-EXP-017 自动分组：离核心约 220 格的建筑群不算前哨站（{nearNone}）；约 {dist:0} 格的 3 座建筑自动归为“{OwnOutpostService.NameOf(o)}”（{o.OutpostId}），按位置认得出成员（{atMember}）");
            // 第一次对账是开档补登（静默），之后新出现的前哨站发通知（写快捷键）。
            OwnOutpostRecord o2 = MakeOutpost(s, out Vector2 c2, skip: 0, angleOffset: 180f);
            string text = LastNotify(OwnOutpostService.NotifyFounded);
            bool notified = o2 != null && o2 != o && o2.OutpostId == "op-2" && NotifyCount(OwnOutpostService.NotifyFounded) >= founded0 + 1 && text.Contains("前哨站 2")
                            && text.Contains(InputDisplay.ForAction(GameActionId.OpenOutposts)) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostFirstFounded);
            Expect(notified, $"B2 另一个方向的建筑群成为“前哨站 2”，发“建立前哨站”通知（“{Short(text)}”）与首次钩子");

            // 改名：校验规则同机器改名；名字出现在地图标签、突袭目标文字。
            bool emptyRej = !OwnOutpostService.TryRename(s, o.OutpostId, "   ", out string m1);
            bool longRej = !OwnOutpostService.TryRename(s, o.OutpostId, new string('北', 17), out string m2);
            bool badRej = !OwnOutpostService.TryRename(s, o.OutpostId, "北<坡>", out string m3);
            bool ok = OwnOutpostService.TryRename(s, o.OutpostId, "北坡", out string m4) && OwnOutpostService.NameOf(o) == "北坡" && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostFirstRenamed);
            var map = new GameObject("fgoutpost-map");
            string label = string.Empty;
            try
            {
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                StrategicMapUIToolkit sm = map.AddComponent<StrategicMapUIToolkit>();
                sm.BindView(mroot);
                sm.SetViewForTests(c.x, c.y, 1400, 900, 700);
                sm.Tick(force: true);
                label = sm.Model.Items.FirstOrDefault(i => i.Kind == WorldMapItemKind.OwnCluster && Math.Abs(i.X - o.X) < 1 && Math.Abs(i.Y - o.Y) < 1).Label ?? string.Empty;
            }
            finally
            {
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(map);
            }
            bool reset = OwnOutpostService.TryResetName(s, o.OutpostId, out _) && OwnOutpostService.NameOf(o) == "前哨站 1";
            Expect(emptyRej && longRej && badRej && m2.Contains("16") && ok && label.Contains("北坡") && reset,
                $"B3 改名：空名 / 超过 16 字 / 带 < > 被拒且写原因（“{m1}”“{m2}”“{m3}”）；改成“北坡”（“{m4}”），战略地图标签写“{label}”；恢复默认名回到“前哨站 1”");
        }

        private static void CheckIdentity()
        {
            CampaignState s = NewWorld(8102);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            OwnOutpostService.TryRename(s, o.OutpostId, "矿点", out _);
            // 加一座（建筑数组换新）→ 仍是同一个前哨站。
            PlaceNear(s, Gen2, c, 6f, 20f);
            OwnOutpostService.Sync(s);
            bool same = OwnOutpostService.All(s).Count == 1 && OwnOutpostService.Find(s, o.OutpostId) == o && o.Count == 4 && OwnOutpostService.NameOf(o) == "矿点";
            // 拆掉锚点建筑 → 按上次位置就近认领，名字与编号不变，锚点换人。
            string anchor = o.AnchorBuildingId;
            Unregister(s, HomeGridService.FindBuilding(s, anchor));
            OwnOutpostService.Sync(s);
            bool rematch = OwnOutpostService.Find(s, o.OutpostId) == o && o.AnchorBuildingId != anchor && o.Count == 3 && OwnOutpostService.NameOf(o) == "矿点";
            Expect(same && rematch, $"B4 身份稳定：加建一座后仍是“矿点”（{same}）；拆掉锚点建筑后按上次位置重新认领，编号 / 名字不变、换锚点（{rematch}）");

            // 合并：另一个前哨站紧挨着建起来，用一排建筑连成一片 → 合并为编号小的那个，前哨劳动的机器改指向它。
            // 审查修复：电塔只传电、不连群（FGR-EXP-017 可以用电塔接回家园电网），先用一排电塔证明不会合并，再用一排实体建筑（发电机 2）连成一片。
            Vector2 c2 = c + new Vector2(40f, 0f);
            HomeGridService.RevealArea(s, c2, 30f);
            BuildingRecord far = PlaceNear(s, Gen2, c2, 0f, 10f);
            OwnOutpostService.Sync(s);
            OwnOutpostRecord o2 = far != null ? OwnOutpostService.Find(s, OwnOutpostService.OutpostIdAt(s, far.Position)) : null;
            int w = SpawnMachine(s, c2 + new Vector2(2f, 2f), MachineRole.OutpostLabor);
            bool split = o2 != null && o2 != o && M(w)?.OutpostId == o2.OutpostId;
            for (float x = 8f; x < 40f; x += 5f)
            {
                GridCell? cell = FreeNear(s, "power_pole", c + new Vector2(x, 0f), 0f, 6f);
                if (cell.HasValue)
                {
                    Register(s, "power_pole", cell.Value);
                }
            }
            OwnOutpostService.Sync(s);
            bool polesKeepApart = o2 != null && OwnOutpostService.Find(s, o.OutpostId) == o && OwnOutpostService.Find(s, o2.OutpostId) == o2 && M(w)?.OutpostId == o2.OutpostId;
            for (float x = 8f; x <= 36f; x += 4f) // 相邻两座最多相距 4 + 2 × 6 = 16 格：聚合格（16 格）一定八邻接
            {
                GridCell? cell = FreeNear(s, Gen2, c + new Vector2(x, 0f), 0f, 6f);
                if (cell.HasValue)
                {
                    Register(s, Gen2, cell.Value);
                }
            }
            OwnOutpostService.Sync(s);
            bool merged = polesKeepApart && OwnOutpostService.Find(s, o.OutpostId) == o && (o2 == null || OwnOutpostService.Find(s, o2.OutpostId) == null) && M(w)?.OutpostId == o.OutpostId
                          && LastNotify(OwnOutpostService.NotifyRemoved).Contains("合并");
            Expect(split && merged, $"B5 合并：旁边 40 格的建筑群先是单独的前哨站（{split}），中间放一排电塔仍是两个前哨站（电塔只传电不连群 {polesKeepApart}），再用一排建筑连成一片后合并为编号小的“矿点”，常驻那里的前哨劳动机器改为常驻“矿点”（{merged}；“{Short(LastNotify(OwnOutpostService.NotifyRemoved))}”）");

            // 删除：建筑全部拆掉 → 前哨站不存在，前哨劳动的机器停下待命并通知，名册写明。
            foreach (BuildingRecord b in OwnOutpostService.Members(s, o.OutpostId).ToList())
            {
                Unregister(s, b);
            }
            OwnOutpostService.Sync(s);
            bool gone = OwnOutpostService.All(s).Count == 0 && MachineRoster.OutpostMissing(s, M(w)) && LastNotify(OwnOutpostService.NotifyRemoved).Contains("已不存在")
                        && !MachineRoster.TakesOutpostLabor(s, M(w));
            Seconds(2f);
            bool stands = HomeValleyWorkOrders.FindActiveOrderForMachine(s, w) == null;
            Expect(gone && stands, $"B6 前哨站的建筑全部拆掉：前哨站不存在，前哨劳动的机器停下待命（不接活、不乱跑，{stands}），通知写“{Short(LastNotify(OwnOutpostService.NotifyRemoved))}”");
        }

        // ── C 前哨劳动 ─────────────────────────────────────────────────────────────

        private static void CheckLabor()
        {
            CampaignState s = NewWorld(8103);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, cover: true);
            if (o == null)
            {
                return;
            }
            // 没有前哨站时选“前哨劳动”被拒（DEBT-FG4ECO07-01 原行为）在名册自检 D5；这里：有前哨站 → 设岗位即常驻最近的前哨站。
            Vector2 away = c + (new Vector2(Core(s).X, Core(s).Y) - c).normalized * 45f;
            HomeGridService.RevealArea(s, away, 20f);
            int a = SpawnMachine(s, away);
            bool set = MachineRoster.TrySetRole(s, a, MachineRole.OutpostLabor, out string msg) && M(a).OutpostId == o.OutpostId && msg.Contains(OwnOutpostService.NameOf(o))
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostFirstLabor);
            // 回驻点：人不在前哨站里 → 岗位维持器开回驻点单，走回前哨站。
            bool walked = StepUntil(() => OwnOutpostService.OutpostIdAt(s, PosOf(a)) == o.OutpostId
                                          || Vector2.Distance(PosOf(a), OwnOutpostService.StationBuilding(s, o).Position) <= OwnOutpostService.StationRadius, 120);
            Expect(set && walked && MachineRoster.OutpostStationOrders > 0,
                $"C1 FGR-ECO-040 前哨劳动：名册设岗位即常驻最近的前哨站（“{msg}”）；离前哨站 45 格的机器由岗位维持器派回驻点（回驻点单 {MachineRoster.OutpostStationOrders} 张，走到了 {walked}）");

            // 只接自己前哨站里的活：家园的施工不接（劳动岗接），前哨站的施工接。
            int home = SpawnMachine(s, new Vector2(Core(s).X + 6f, Core(s).Y + 6f));
            BuildingRecord homeGhost = Ghost(s, Gen2, new Vector2(Core(s).X, Core(s).Y), 10f, 30f);
            Seconds(3f);
            WorkOrderRecord ho = homeGhost != null ? BuildOrder(s, homeGhost.BuildingId) : null;
            bool homeTaken = ho != null && ho.AssignedMachineLogicId == home;
            MachineRoster.TrySetRole(s, home, MachineRole.Idle, out _);
            int assigned0 = HomeValleyWorkOrders.OutpostLaborAssignments;
            BuildingRecord opGhost = Ghost(s, Gen2, c, 3f, 20f);
            WorkOrderRecord oo = opGhost != null ? BuildOrder(s, opGhost.BuildingId) : null;
            Seconds(2f);
            bool opTaken = oo != null && oo.AssignedMachineLogicId == a && HomeValleyWorkOrders.OutpostLaborAssignments > assigned0;
            bool built = opGhost != null && StepUntil(() => opGhost.ConstructionState == BuildingConstructionState.Operational, 400);
            Expect(homeTaken && opTaken && built && (ho == null || ho.AssignedMachineLogicId != a),
                $"C2 只接自己前哨站的活：家园的虚影由劳动岗接（{homeTaken}），前哨劳动不接；前哨站里的虚影由前哨劳动接（{opTaken}）、建成（{built}，材料从家园仓库取，远就慢）");
        }

        private static void CheckLaborWiped()
        {
            CampaignState s = NewWorld(8104);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            int a = SpawnMachine(s, c + new Vector2(3f, 3f), MachineRole.OutpostLabor);
            int b = SpawnMachine(s, c + new Vector2(-3f, 3f), MachineRole.OutpostLabor);
            bool two = OwnOutpostService.LaborCount(s, o.OutpostId) == 2;
            MachineRegistry.MarkDeadByLogicId(a);
            MachineRegistry.MarkDeadByLogicId(b);
            WorldSimulation.StepMany(2);
            OutpostView v = OwnOutpostService.ViewOf(s, o.OutpostId);
            // 负向：前哨劳动的机器全部阵亡——前哨站的施工照常开单，由家园的劳动岗走过去做（远就慢，但一定能建成）。
            int home = SpawnMachine(s, new Vector2(Core(s).X + 5f, Core(s).Y + 5f));
            BuildingRecord ghost = Ghost(s, Gen2, c, 3f, 20f);
            WorkOrderRecord order = ghost != null ? BuildOrder(s, ghost.BuildingId) : null;
            Seconds(2f);
            bool homeTook = order != null && order.AssignedMachineLogicId == home;
            Expect(two && v != null && v.Labor == 0 && OwnOutpostService.RowText(v).Contains("前哨劳动 0 台") && homeTook,
                $"C3 负向“前哨劳动的机器全部阵亡”：总览写前哨劳动 0 台（“{(v != null ? Short(OwnOutpostService.RowText(v).Split('\n').Last()) : "?")}”）；前哨站的施工单照常由家园的劳动岗接（{homeTook}）");
        }

        // ── D 远程施工三种来源 ─────────────────────────────────────────────────────

        private static void CheckMachineCarry()
        {
            CampaignState s = NewWorld(8105);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, cover: true);
            if (o == null)
            {
                return;
            }
            BuildingRecord wh = PlaceNear(s, HomeValleyLayout.BuildingTypeWarehouse, c, 4f, 20f);
            int a = SpawnMachine(s, c + new Vector2(2f, -2f), MachineRole.OutpostLabor);
            BuildingRecord ghost = Ghost(s, Gen2, c, 4f, 20f);
            WorkOrderRecord order = ghost != null ? BuildOrder(s, ghost.BuildingId) : null;
            if (wh == null || order == null)
            {
                return;
            }
            Vector2 fetch = HomeValleyConstruction.FetchPosition(s, order, ghost.Position);
            bool local = Vector2.Distance(fetch, wh.Position) < 0.5f;
            bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 200);
            Expect(local && built && order.FetchCount > 0,
                $"D1 FGR-EXP-018 机器运送：前哨站建了仓库，施工单的取料点就是前哨站的仓库（{local}，库存与核心共用），前哨劳动的机器就近取料运到现场、建成（{built}，取料 {order.FetchCount} 趟）");
        }

        /// <summary>前哨站旁一座虚影 + 一条正对着它南边的传送带（5 格，朝北），带首放一个测试料源（家园库存里扣同样的废料，守恒）。返回料源推出的件数。</summary>
        private static int BeltScenario(CampaignState s, out OwnOutpostRecord o, out BuildingRecord ghost, out Vector2 c)
        {
            ghost = null;
            o = MakeOutpost(s, out c, cover: true);
            if (o == null)
            {
                return 0;
            }
            for (float d = 6f; d <= 22f && ghost == null; d += 2f)
            {
                BuildingRecord g = Ghost(s, Gen2, c, d, d + 1f);
                if (g == null)
                {
                    continue;
                }
                GameConfig.fg.BuildingGrid gg = GridContent.Building(Gen2);
                GridMath.FootprintBounds(new GridCell(g.GridX, g.GridY), gg.FootprintW, gg.FootprintH, 0, out GridCell min, out GridCell _);
                int x = min.X;
                bool ok = true;
                for (int k = 1; k <= 5 && ok; k++)
                {
                    ok = BeltNetworkService.TryPlace(s, new GridCell(x, min.Y - k), BeltDir.North, 0).Ok;
                }
                if (!ok)
                {
                    HomeValleyWorkOrders.CancelOrder(s, BuildOrder(s, g.BuildingId).WorkOrderId, g.Position);
                    continue;
                }
                ghost = g;
                int need = g.ConstructionRequired;
                s.Scrap -= need;
                BeltNetworkService.TryAddSource(s, 77, new GridCell(x, min.Y - 5), BeltItems.ScrapId, 1, need); // 测试捷径：带首料源（端口推料由 FG3-LOG-03 覆盖）
                OwnOutpostService.Sync(s);
                return need;
            }
            return 0;
        }

        private static void CheckBeltInlet()
        {
            CampaignState s = NewWorld(8106);
            int need = BeltScenario(s, out OwnOutpostRecord o, out BuildingRecord ghost, out Vector2 c);
            if (ghost == null)
            {
                Fail("D2 测试准备：前哨站旁铺不下正对虚影的传送带");
                return;
            }
            long poured0 = BeltPortService.SitePoured;
            bool full = StepUntil(() => ghost.ConstructionDelivered >= ghost.ConstructionRequired, 90);
            WorkOrderRecord order = BuildOrder(s, ghost.BuildingId);
            bool noMachine = order != null && order.AssignedMachineLogicId == 0 && order.FetchCount == 0;
            int inlets = BeltPortService.SiteInletCount(ghost.BuildingId);
            OutpostView v = OwnOutpostService.ViewOf(s, o.OutpostId);
            Expect(full && noMachine && inlets == 1 && BeltPortService.SitePoured - poured0 == need && v != null && v.Inlets == 1 && OwnOutpostService.StateOf(s).BeltUnitsPoured == need,
                $"D2 FGR-EXP-018 传送带铺过去：带的末端正对施工中的建筑 = 施工现场输入口（{inlets} 个），{need} 件废料不经机器直接进现场（已到 {ghost.ConstructionDelivered}/{ghost.ConstructionRequired}，没有机器取料 {noMachine}），总览写“传送带输入口 1”");
            // 现场齐了：带停下（不再收），机器来只管施工；建成后输入口删掉。
            int a = SpawnMachine(s, c + new Vector2(2f, 2f), MachineRole.OutpostLabor);
            bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 120);
            Seconds(2f);
            Expect(built && order.FetchCount == 0 && BeltPortService.SiteInletCount(ghost.BuildingId) == 0,
                $"D3 材料已齐：前哨劳动的机器直接去现场施工、不去取料（取料 {order.FetchCount} 趟），建成（{built}）；建成后施工现场输入口删掉（{BeltPortService.SiteInletCount(ghost.BuildingId)}）");
        }

        private static BuildingRecord DropStation(CampaignState s, int pods)
        {
            ResearchService.CompleteForTests(s, "expedition.drop_station");
            for (int i = 0; i < 2; i++)
            {
                GridCell? g = FgProductionSelfCheck.FindFree(s, Gen2, 6f, 26f);
                if (g.HasValue)
                {
                    Register(s, Gen2, g.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            BuildingRecord st = null;
            GridCell core = Core(s);
            for (float d = 10f; d <= 40f && st == null; d += 1f)
            {
                for (int a = 0; a < 36 && st == null; a++)
                {
                    float ang = (a * 10f + d * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(core.X + Mathf.Cos(ang) * d), Mathf.RoundToInt(core.Y + Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, DropPodService.StationTypeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = Register(s, DropPodService.StationTypeId, c);
                    if (HomeValleyPowerGrid.IsConnected(s, b.BuildingId) && b.PowerState == BuildingPowerState.Powered)
                    {
                        st = b;
                    }
                    else
                    {
                        Unregister(s, b);
                    }
                }
            }
            if (st == null)
            {
                Fail("测试准备：核心附近放不下有电的投送站");
                return null;
            }
            DropPodService.Sync(s);
            DropStationRecord r = DropPodService.FindStation(s, st.BuildingId);
            r.HeldFuelMl = 1000L * 1000L;
            r.AutoFill = false;
            HomeInventory.Add(s, ItemCatalog.Find(DropPodService.PodItemId), pods, clampToSpace: false);
            DropPodService.LoadPods(s, r, st, pods);
            return st;
        }

        private static void CheckPack()
        {
            CampaignState s = NewWorld(8107);
            BuildingRecord st = DropStation(s, 4);
            OwnOutpostRecord o = st != null ? MakeOutpost(s, out Vector2 c) : null;
            if (o == null)
            {
                return;
            }
            BuildingRecord ghost = Ghost(s, Gen2, new Vector2(o.X, o.Y), 4f, 20f);
            if (ghost == null)
            {
                return;
            }
            OwnOutpostService.Sync(s);
            int scrap0 = Mathf.FloorToInt(s.Scrap);
            int ground0 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Length;
            bool launched = OwnOutpostService.TryLaunchPack(s, o.OutpostId, out string msg);
            int sent = scrap0 - Mathf.FloorToInt(s.Scrap);
            DropPodRecord pod = DropPodService.Pods(s).FirstOrDefault(p => p.State == DropPodService.StateOutbound && p.ConstructionPack);
            bool landed = pod != null && StepUntil(() => pod.State != DropPodService.StateOutbound, 60);
            int ground1 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Length;
            OwnOutpostState d = OwnOutpostService.StateOf(s);
            Expect(launched && sent == ghost.ConstructionRequired && landed && ghost.ConstructionDelivered == ghost.ConstructionRequired && ground1 == ground0
                   && d.PacksDelivered >= 1 && d.PackUnitsPoured == sent && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostFirstPack),
                $"D4 FGR-EXP-018 投送舱送施工包：总览“投送施工包”从投送站发射（“{Short(msg)}”，扣库存 {sent} 件废料），着陆后材料直接进施工现场（已到 {ghost.ConstructionDelivered}/{ghost.ConstructionRequired}），地上不留货（{ground1 == ground0}）");
            // 负向：没有缺料的现场 / 没有库存 → 被拒，什么都不改。
            int pods0 = DropPodService.Pods(s).Count(p => p.State == DropPodService.StateOutbound);
            bool noNeed = !OwnOutpostService.TryLaunchPack(s, o.OutpostId, out string m1) && m1.Contains("没有缺材料");
            BuildingRecord ghost2 = Ghost(s, Gen2, new Vector2(o.X, o.Y), 4f, 22f);
            OwnOutpostService.Sync(s);
            var keep = s.Scrap;
            s.Scrap = 0;
            bool noStock = ghost2 != null && !OwnOutpostService.TryLaunchPack(s, o.OutpostId, out string m2) && m2.Contains("仓库里都没有");
            s.Scrap = keep;
            Expect(noNeed && noStock && DropPodService.Pods(s).Count(p => p.State == DropPodService.StateOutbound) == pods0,
                $"D5 施工包被拒写原因、不发射：没有缺料的现场（“{m1}”）、缺的材料仓库里没有（{noStock}）");
        }

        private static float GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null && g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => (float)g.Amount);

        /// <summary>
        /// 审查修复（FG8-OUT-01 第 1 轮，守恒）：机器载着废料在路上时，施工包 / 传送带施工现场输入口（同一入口 DeliverToSite）先把现场补满——
        /// 机器到场后现场不超收，多出来的退回仓库；直接送料也把虚影从“规划”转为“施工中”。之后照常建成。
        /// </summary>
        private static void CheckDirectFillRace()
        {
            CampaignState s = NewWorld(8121);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, cover: true);
            if (o == null)
            {
                return;
            }
            BuildingRecord wh = PlaceNear(s, HomeValleyLayout.BuildingTypeWarehouse, c, 4f, 14f);
            int a = SpawnMachine(s, c + new Vector2(2f, -2f), MachineRole.OutpostLabor);
            BuildingRecord ghost = wh != null ? Ghost(s, Gen2, c, 12f, 24f) : null;
            WorkOrderRecord order = ghost != null ? BuildOrder(s, ghost.BuildingId) : null;
            if (order == null)
            {
                Fail("D6 测试准备：前哨站旁放不下仓库 + 虚影");
                return;
            }
            Func<int> cargo = () => MachineRegistry.TryGetRecord(a, out MachineRecord m) ? HomeValleyConstruction.CargoScrap(m) : 0;
            int required = ghost.ConstructionRequired; // 建成后现场计数清零，先记下
            bool loaded = StepUntil(() => cargo() > 0 && ghost.ConstructionDelivered == 0, 120);
            int carried = cargo();
            bool plannedBefore = ghost.ConstructionState == BuildingConstructionState.Planned;
            int fill = HomeValleyConstruction.SiteNeed(ghost, CampaignEconomyLedger.ResourceScrap);
            s.Scrap -= fill; // 施工包 / 传送带送来的料同样出自共用库存
            int put = HomeValleyConstruction.DeliverToSite(s, ghost, CampaignEconomyLedger.ResourceScrap, fill);
            bool building = ghost.ConstructionState == BuildingConstructionState.Building;
            // 逐步盯住机器卸货的那一步：库存 + 地面废料的增量 = 退回的量。
            float prev = s.Scrap + GroundScrap(s);
            float back = -1f;
            int maxDelivered = ghost.ConstructionDelivered;
            bool unloaded = StepUntil(() =>
            {
                maxDelivered = Math.Max(maxDelivered, ghost.ConstructionDelivered);
                float now = s.Scrap + GroundScrap(s);
                if (cargo() == 0)
                {
                    back = now - prev;
                    return true;
                }
                prev = now;
                return false;
            }, 120);
            bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 200);
            Expect(loaded && carried > 0 && plannedBefore && put == fill && fill > 0 && building && unloaded && maxDelivered <= required
                   && Mathf.Abs(back - carried) < 0.5f && built,
                $"D6 三种来源抢着补同一现场（守恒）：机器载 {carried} 件废料在路上时，施工包 / 传送带入口先补满现场（{put} 件，虚影随即从“规划”转为“施工中” {building}）；" +
                $"机器到场后现场不超收（最多 {maxDelivered}/{required}），多出来的 {back:0} 件退回仓库；之后照常建成（{built}）");
        }

        // ── E 供电与覆盖 ─────────────────────────────────────────────────────────

        /// <summary>从核心朝 <paramref name="target"/> 一路放电塔 T2（相邻不超过覆盖半径，贪心绕开放不下的格子），最后一座离目标不超过半径；放不通返回 null。</summary>
        private static List<BuildingRecord> PoleChain(CampaignState s, Vector2 target)
        {
            const string pole = "power_pole_t2";
            float reach = HomeValleyPowerGrid.CoverRadiusOf(pole);
            GridCell core = Core(s);
            var at = new Vector2(core.X, core.Y);
            var placed = new List<BuildingRecord>();
            for (int guard = 0; guard < 80 && Vector2.Distance(at, target) > reach - 3f; guard++)
            {
                Vector2 dir = (target - at).normalized;
                BuildingRecord next = null;
                for (float r = reach - 3f; r >= 5f && next == null; r -= 2f)
                {
                    for (int k = 0; k < 13 && next == null; k++)
                    {
                        float ang = (k % 2 == 0 ? 1f : -1f) * ((k + 1) / 2) * 10f * Mathf.Deg2Rad;
                        var d = new Vector2(dir.x * Mathf.Cos(ang) - dir.y * Mathf.Sin(ang), dir.x * Mathf.Sin(ang) + dir.y * Mathf.Cos(ang));
                        Vector2 q = at + d * r;
                        var cell = new GridCell(Mathf.RoundToInt(q.x), Mathf.RoundToInt(q.y));
                        HomeGridService.RevealArea(s, q, 4f);
                        if (HomeGridService.ValidatePlacement(s, pole, cell, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            next = Register(s, pole, cell);
                        }
                    }
                }
                if (next == null)
                {
                    return null;
                }
                placed.Add(next);
                at = next.Position;
            }
            return placed;
        }

        private static string ConsumerType()
        {
            foreach (KeyValuePair<string, (float PowerDemand, int PowerPriority)> kv in HomeValleyLayout.PowerProfile.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value.PowerDemand > 0f && !HomeValleyLayout.PowerSupplyProfile.ContainsKey(kv.Key) && GridContent.TryGetBuilding(kv.Key, out GameConfig.fg.BuildingGrid g)
                    && g.Placeable == 1 && g.FootprintW <= 3 && g.FootprintH <= 3 && !HomeValleyPowerGrid.IsPoleType(kv.Key) && kv.Key != HomeValleyLayout.BuildingTypeCore)
                {
                    return kv.Key;
                }
            }
            return null;
        }

        private static void CheckPowerAndCoverage()
        {
            CampaignState s = NewWorld(8108);
            Vector2? p = FindSpot(s, OutpostDistance, 6);
            string consumer = ConsumerType();
            if (!p.HasValue || consumer == null)
            {
                Fail("E 测试准备：找不到前哨站空地或用电建筑类型");
                return;
            }
            BuildingRecord user = PlaceNear(s, consumer, p.Value, 0f, 12f);
            HomeValleyPowerGrid.Recompute(s);
            OwnOutpostService.Sync(s);
            OwnOutpostRecord o = OwnOutpostService.Nearest(s, p.Value);
            OutpostView v1 = OwnOutpostService.ViewOf(s, o?.OutpostId);
            bool cut = v1 != null && v1.Power == OutpostPowerLink.Disconnected && v1.Problem && OwnOutpostService.PowerText(v1).Contains("没接上有电源的电网");
            // 审查修复（P2，FGR-EXP-017“也可以用电塔接回家园电网”）：一路电塔接回核心电网 → “接回家园电网”；拆掉中间一座 → 与家园电网断开、标为有问题
            // （负向“前哨站与家园电网断开”的原意：原本连着、后来断开）。测完拆掉这条线，下面再测本地电网。
            List<BuildingRecord> chain = PoleChain(s, user.Position);
            HomeValleyPowerGrid.Recompute(s);
            OwnOutpostService.Sync(s);
            OutpostView vh = OwnOutpostService.ViewOf(s, o?.OutpostId);
            bool home = chain != null && vh != null && vh.Power == OutpostPowerLink.HomeGrid && vh.OnHomeGrid >= 1 && OwnOutpostService.PowerText(vh).Contains("接回家园电网");
            if (chain != null && chain.Count >= 3)
            {
                Unregister(s, chain[chain.Count / 2]);
            }
            HomeValleyPowerGrid.Recompute(s);
            OwnOutpostService.Sync(s);
            OutpostView vb = OwnOutpostService.ViewOf(s, o?.OutpostId);
            bool broken = chain != null && chain.Count >= 3 && vb != null && vb.Power == OutpostPowerLink.Disconnected && vb.Problem && OwnOutpostService.PowerText(vb).Contains("与家园电网断开");
            Expect(home && broken,
                $"E1b 接回家园电网（电塔只传电不连群，前哨站仍是前哨站 {vh != null}）：从核心一路放 {chain?.Count ?? 0} 座电塔 T2 接到前哨用电建筑，总览写“{(vh != null ? OwnOutpostService.PowerText(vh) : "?")}”；" +
                $"拆掉中间一座 → 与家园电网断开，写“{(vb != null ? OwnOutpostService.PowerText(vb) : "?")}”并标为有问题（{vb?.Problem}）");
            foreach (BuildingRecord pole in chain ?? new List<BuildingRecord>())
            {
                if (s.BuildingRecords.Contains(pole))
                {
                    Unregister(s, pole);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            OwnOutpostService.Sync(s);
            // 本地发电 + 电塔：接在自己的电网上（不是家园电网）。
            PlaceNear(s, Gen2, user.Position, 3f, 10f);
            PlaceNear(s, "power_pole", user.Position, 1f, 5f);
            HomeValleyPowerGrid.Recompute(s);
            OwnOutpostService.Sync(s);
            OutpostView v2 = OwnOutpostService.ViewOf(s, o?.OutpostId);
            bool own = v2 != null && v2.Power == OutpostPowerLink.OwnGrid && v2.OnOwnGrid >= 1;
            Expect(cut && own,
                $"E1 负向“前哨站与家园电网断开”：用电建筑没接上有电源的电网时总览写“{(v1 != null ? OwnOutpostService.PowerText(v1) : "?")}”并标为有问题；建本地发电机 + 电塔后写“{(v2 != null ? OwnOutpostService.PowerText(v2) : "?")}”");
            // 覆盖：340 格外不在核心覆盖里 → 无覆盖；前哨站建一座中继塔但没接进网络 → 断开；中途再建两座接上 → 连通。
            OutpostCoverageLink c0 = OwnOutpostService.CoverageOf(s, o);
            BuildingRecord tower = PlaceNear(s, HomeValleyLayout.BuildingTypeSignalRelay, p.Value, 2f, 16f);
            SignalCoverageService.Invalidate();
            WorldSimulation.StepMany(3);
            OwnOutpostService.Sync(s);
            OutpostCoverageLink c1 = OwnOutpostService.CoverageOf(s, o);
            CoverOutpost(s, p.Value);
            OutpostCoverageLink c2 = OwnOutpostService.CoverageOf(s, o);
            Expect(tower != null && c0 == OutpostCoverageLink.None && c1 == OutpostCoverageLink.LocalCut && c2 == OutpostCoverageLink.Connected,
                $"E2 覆盖是否连通：不在覆盖里 = “{OwnOutpostService.CoverageText(c0)}”；前哨站自己的信号塔没接进网络 = “{OwnOutpostService.CoverageText(c1)}”；中途再建两座中继塔接上 = “{OwnOutpostService.CoverageText(c2)}”");
        }

        // ── F 突袭 ─────────────────────────────────────────────────────────────────

        private static void PassGrace(CampaignState s)
        {
            long target = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if ((RaidDirectorService.StateOf(s)?.Plans?.Length ?? 0) == 0 && GameClock.Ticks < target)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
        }

        private static void CheckRaidTargets()
        {
            CampaignState s = NewWorld(8109);
            PassGrace(s);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, buildings: 4);
            if (o == null)
            {
                return;
            }
            OwnOutpostService.TryRename(s, o.OutpostId, "前线", out _);
            for (int i = 0; i < 40; i++)
            {
                RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            }
            WorldSimulation.StepMany(2);
            RaidPlanRecord[] plans = RaidDirectorService.StateOf(s).Plans;
            RaidPlanRecord op = plans.FirstOrDefault(p => p.TargetKind == RaidDirectorService.TargetOutpost);
            bool entity = op != null && op.TargetId == o.OutpostId && RaidDirectorService.TargetText(op).Contains("前线");
            OutpostView v = OwnOutpostService.ViewOf(s, o.OutpostId);
            Expect(entity && v.RaidIncoming && OwnOutpostService.RowText(v).Contains("突袭"),
                $"F1 FGR-EXP-019 前哨站会被突袭（关闭 DEBT-FG6DEF04-14）：突袭目标读正式前哨站（{op?.TargetId}，“{(op != null ? RaidDirectorService.TargetText(op) : "?")}”），总览标出“有突袭正冲着这个前哨站来”");
            // 全毁的前哨站不再被选为目标；修好后恢复。
            foreach (BuildingRecord b in OwnOutpostService.Members(s, o.OutpostId).ToList())
            {
                BuildingOps.ApplyDamage(s, b.BuildingId, 100000f);
            }
            OwnOutpostService.Sync(s);
            int before = plans.Length;
            for (int i = 0; i < 40; i++)
            {
                RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            }
            WorldSimulation.StepMany(2);
            bool none = RaidDirectorService.StateOf(s).Plans.Skip(before).All(p => p.TargetKind != RaidDirectorService.TargetOutpost);
            Expect(OwnOutpostService.IsWipedOut(s, o) && none, $"F2 全毁的前哨站（只剩废墟）不再被选为突袭目标：之后 40 波里打前哨站的 {RaidDirectorService.StateOf(s).Plans.Skip(before).Count(p => p.TargetKind == RaidDirectorService.TargetOutpost)} 波");
        }

        private static void CheckWipedNoLoss()
        {
            CampaignState s = NewWorld(8110);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            int wiped0 = NotifyCount(OwnOutpostService.NotifyWiped);
            foreach (BuildingRecord b in OwnOutpostService.Members(s, o.OutpostId).ToList())
            {
                BuildingOps.ApplyDamage(s, b.BuildingId, 100000f);
            }
            Seconds(2.5f);
            Seconds(2f);
            bool once = NotifyCount(OwnOutpostService.NotifyWiped) == wiped0 + 1 && o.WipedNotified && LastNotify(OwnOutpostService.NotifyWiped).Contains("不会导致战役失败");
            bool notLost = !RaidResultService.IsCoreLost(s) && OwnOutpostService.Find(s, o.OutpostId) == o && o.Count == 3 && o.Intact == 0;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            OutpostView v = OwnOutpostService.ViewOf(s, o.OutpostId);
            // 修好一座 → 不再全毁（之后再全毁会再通知一次）。
            BuildingRecord one = OwnOutpostService.Members(s, o.OutpostId)[0];
            one.ConstructionState = BuildingConstructionState.Operational;
            one.Health = BuildingOps.MaxDurability(one.BuildingTypeId);
            Seconds(1.5f);
            bool restored = !o.WipedNotified && o.Intact == 1;
            Expect(once && notLost && save.Success && v.Wiped && OwnOutpostService.RowText(v).Contains("全部被摧毁") && restored && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostFirstWiped),
                $"F3 FGR-EXP-019 / 负向“前哨站被全部摧毁”：不导致战役失败（核心没丢、照常存档 {save.Success}）；废墟保留、前哨站记录还在；只通知一次（“{Short(LastNotify(OwnOutpostService.NotifyWiped))}”）；修好一座后恢复（{restored}）");
        }

        /// <summary>审查修复（P2，FG08“前哨站被全部摧毁：虚影保留，自动重建规则照常生效”）：开着自动重建规则时，全毁的前哨站每座废墟都由规则派出重建单。</summary>
        private static void CheckWipedAutoRebuild()
        {
            CampaignState s = NewWorld(8122);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            s.Scrap = Math.Max(s.Scrap, 5000);
            bool created = StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord r, out string why);
            List<BuildingRecord> members = OwnOutpostService.Members(s, o.OutpostId).ToList();
            foreach (BuildingRecord b in members)
            {
                BuildingOps.ApplyDamage(s, b.BuildingId, 100000f);
            }
            Seconds(2.5f);
            int ordered = members.Count(b => HomeValleyWorkOrders.FindActiveRepair(s, b.BuildingId) is WorkOrderRecord w && r != null && w.RuleSerial == r.Serial);
            bool kept = OwnOutpostService.Find(s, o.OutpostId) == o && !RaidResultService.IsCoreLost(s);
            Expect(created && members.Count == 3 && ordered == members.Count && kept,
                $"F6 全毁 + 自动重建规则：前哨站 {members.Count} 座建筑全毁后，规则 R{r?.Serial} 给每座废墟派出重建单（{ordered}/{members.Count}{(created ? string.Empty : "，建规则失败：" + why)}）；前哨站记录保留、战役没失败（{kept}）");
        }

        /// <summary>一支突袭部队从前哨站外 30 格出发打前哨站，跑 <paramref name="seconds"/> 游戏秒；返回签名（前哨建筑耐久 / 状态、队伍状态 / 人数、展开数、步数）。</summary>
        private static string RaidScenario(int seed, bool observe, float seconds, out bool unfolded)
        {
            unfolded = false;
            CampaignState s = NewWorld(seed, observe);
            PassGrace(s);
            // FG9-UX-01（复审修复 P1）：图鉴是跨存档的元进度（不在存档里），每遍都从同一份空白图鉴起步，再比较攻城单位来到时的首次遭遇解锁。
            FreshCodex();
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, buildings: 4);
            if (o == null)
            {
                return "no-outpost";
            }
            GridCell core = Core(s);
            Vector2 dir = (c - new Vector2(core.X, core.Y)).normalized;
            var plan = new RaidPlanRecord
            {
                PlanId = "raid-probe-op", Faction = "foundry", UnitTotal = 4, OriginKind = RaidDirectorService.OriginFog, OriginId = "foundry",
                TargetKind = RaidDirectorService.TargetOutpost, TargetId = o.OutpostId, TargetX = Mathf.RoundToInt(o.X), TargetY = Mathf.RoundToInt(o.Y),
                OriginX = Mathf.RoundToInt(o.X + dir.x * 30f), OriginY = Mathf.RoundToInt(o.Y + dir.y * 30f),
            };
            int unfold0 = SiegeService.UnfoldCount;
            TransitGroupRecord g = WorldTransitSystem.DispatchPlanned(s, plan);
            if (g == null)
            {
                return "no-dispatch";
            }
            Seconds(seconds);
            unfolded = SiegeService.UnfoldCount > unfold0;
            var sb = new StringBuilder();
            foreach (BuildingRecord b in OwnOutpostService.Members(s, o.OutpostId))
            {
                sb.Append(b.BuildingTypeId).Append(':').Append((int)b.ConstructionState).Append(':').Append(b.Health.ToString("0.0", CultureInfo.InvariantCulture)).Append('|');
            }
            sb.Append("g=").Append((int)g.State).Append(':').Append(g.UnitCount).Append(":unf=").Append(SiegeService.UnfoldCount - unfold0).Append(":t=").Append(GameClock.Ticks);
            sb.Append(":lost=").Append(RaidResultService.IsCoreLost(s));
            // 攻城单位不是区域敌人记录（交火走具名目标路径、不解锁），图鉴里的敌人条目只能来自 SiegeService 展开时的首次遭遇。
            sb.Append(":codex=").Append(UnlockedEnemies());
            sb.Append(":codexNotes=").Append(MechanicCodex.NotifyCount);
            return sb.ToString();
        }

        private static void CheckRaidObserved()
        {
            string seen = RaidScenario(8111, true, 60f, out bool u1);
            string unseen = RaidScenario(8111, false, 60f, out bool u2);
            Expect(seen == unseen && u1 && u2 && !seen.StartsWith("no-", StringComparison.Ordinal) && seen.Contains("lost=False") && seen.Contains(":codex=enemy_"),
                "F4 负向“前哨站被突袭时玩家在远处（后台一致）”/ FGR-BASE-021：突袭部队到达前哨站展开攻城（复用 FG6 的攻城与家园战斗内核结算），镜头在 / 不在家园各跑 60 游戏秒，前哨建筑耐久 / 状态、队伍状态 / 人数逐字段一致，核心没丢；FG9-UX-01：攻城单位展开即首次遭遇，两遍解锁的敌人条目与“图鉴新增”通知数一致且不为空" +
                (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        /// <summary>
        /// F5（DEBT-FG6DEF05-05 星球部分）：离家园很远的前哨站附近，己方机器拦截行进中的突袭——家园战斗内核覆盖整个星球表面，前哨站的机器与家园的机器同一套拦截规则。
        /// </summary>
        private static void CheckIntercept()
        {
            CampaignState s = NewWorld(8119);
            PassGrace(s);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c, buildings: 3);
            if (o == null)
            {
                return;
            }
            GridCell core = Core(s);
            Vector2 dir = (c - new Vector2(core.X, core.Y)).normalized;
            Vector2 origin = c + dir * 150f;
            HomeGridService.RevealArea(s, origin, 40f);
            HomeGridService.RevealArea(s, c + dir * 90f, 40f);
            int guard = SpawnMachine(s, c + dir * 95f, MachineRole.Idle);
            var plan = new RaidPlanRecord
            {
                PlanId = "raid-probe-intercept", Faction = "foundry", UnitTotal = 3, OriginKind = RaidDirectorService.OriginFog, OriginId = "foundry",
                TargetKind = RaidDirectorService.TargetOutpost, TargetId = o.OutpostId, TargetX = Mathf.RoundToInt(o.X), TargetY = Mathf.RoundToInt(o.Y),
                OriginX = Mathf.RoundToInt(origin.x), OriginY = Mathf.RoundToInt(origin.y),
            };
            int before = SiegeService.InterceptCount;
            FreshCodex();
            TransitGroupRecord g = WorldTransitSystem.DispatchPlanned(s, plan);
            bool intercepted = g != null && StepUntil(() => g.Intercepted || SiegeService.InterceptCount > before, 120);
            float fromHome = Vector2.Distance(PosOf(guard), new Vector2(core.X, core.Y));
            // FG9-UX-01（FGR-FAC-005）：被拦截就地展开 = 己方机器遇上了它们，首次遭遇解锁（攻城单位不是区域敌人记录，只能从展开处解锁）。
            string seenEnemies = UnlockedEnemies();
            Expect(guard != 0 && intercepted && fromHome > 300f && seenEnemies.Length > 0,
                $"F5 DEBT-FG6DEF05-05（星球部分）：离核心 {fromHome:0} 格、前哨站外侧的一台己方机器拦下行进中冲着前哨站来的突袭（就地展开交战 {intercepted}），与家园范围内同一套拦截规则；FG9-UX-01：拦截展开即首次遭遇，图鉴解锁 {seenEnemies}");
        }

        // ── G 离家报告分组 ─────────────────────────────────────────────────────────

        private static void CheckAwayReport()
        {
            CampaignState s = NewWorld(8112);
            OwnOutpostRecord o = MakeOutpost(s, out Vector2 c);
            if (o == null)
            {
                return;
            }
            OwnOutpostService.TryRename(s, o.OutpostId, "南岭", out _);
            AwayReportRecord r = AwayReportService.Begin(s, HomeValleyLayout.RegionId, Array.Empty<int>());
            BuildingRecord homeOne = PlaceNear(s, Gen2, new Vector2(Core(s).X, Core(s).Y), 8f, 30f);
            BuildingOps.ApplyDamage(s, homeOne.BuildingId, 100000f);
            foreach (BuildingRecord b in OwnOutpostService.Members(s, o.OutpostId).ToList())
            {
                BuildingOps.ApplyDamage(s, b.BuildingId, 100000f);
            }
            Seconds(2.5f);
            var lines = new List<AwayLine>();
            AwayReportView.Build(s, r, lines);
            List<AwayLine> grouped = lines.Where(l => l.Section == "outpost").ToList();
            bool header = grouped.Any(l => l.Text.Contains("南岭") && l.Text.Contains("现在立着 0/3"));
            bool entries = grouped.Count(l => l.Text.Contains("被摧毁") || l.Text.Contains("全毁")) >= 3;
            bool homeSep = lines.Any(l => l.Section == "buildings" && l.Text.Contains("被摧毁")) && !lines.Where(l => l.Section == "buildings").Any(l => grouped.Any(gl => gl.Text == l.Text && gl != l && false));
            int stamped = r.Entries.Count(e => e.OutpostId == o.OutpostId);
            Expect(header && entries && homeSep && stamped >= 3,
                $"G 卡片“前哨站的统计和离家报告分组”：离家期间发生在前哨站的事件归到“前哨站”分组（{stamped} 条，组头“{grouped.FirstOrDefault()?.Text}”），家园的建筑被毁仍在“建筑”段（{homeSep}）");
        }

        // ── H 存读档 ─────────────────────────────────────────────────────────────

        private static void Restore()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            OwnOutpostService.ResetSessionState();
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

        private static string Sig(CampaignState s, string ghostId, params int[] machines)
        {
            var sb = new StringBuilder(Json(OwnOutpostService.StateOf(s)));
            BuildingRecord g = HomeGridService.FindBuilding(s, ghostId);
            sb.Append("|ghost=").Append(g != null ? (int)g.ConstructionState + ":" + g.ConstructionDelivered : "null");
            foreach (int id in machines)
            {
                Vector2 p = PosOf(id);
                sb.Append('|').Append(id).Append(':').Append(M(id)?.OutpostId).Append(':').Append(p.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("0.0", CultureInfo.InvariantCulture));
            }
            foreach (DropPodRecord pod in DropPodService.Pods(s))
            {
                sb.Append("|pod").Append(pod.Serial).Append(':').Append(pod.State).Append(':').Append(pod.CargoAmount).Append(':').Append(pod.ConstructionPack);
            }
            return sb.Append("|t=").Append(GameClock.Ticks).ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8113);
            BuildingRecord st = DropStation(s, 4);
            OwnOutpostRecord o = st != null ? MakeOutpost(s, out Vector2 c, cover: true) : null;
            if (o == null)
            {
                return;
            }
            OwnOutpostService.TryRename(s, o.OutpostId, "存档岭", out _);
            int a = SpawnMachine(s, new Vector2(o.X, o.Y) + new Vector2(30f, 0f), MachineRole.OutpostLabor);
            BuildingRecord ghost = Ghost(s, Gen2, new Vector2(o.X, o.Y), 4f, 20f);
            OwnOutpostService.Sync(s);
            MachineRoster.TrySetRole(s, a, MachineRole.OutpostLabor, out _);
            bool launched = ghost != null && OwnOutpostService.TryLaunchPack(s, o.OutpostId, out _);
            Seconds(2f);
            bool flying = DropPodService.Pods(s).Any(p => p.State == DropPodService.StateOutbound && p.ConstructionPack);
            WorldSimulation.SyncAllForSave();
            string domain = Json(OwnOutpostService.StateOf(s));
            string machineOutpost = M(a).OutpostId;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(30f);
            string straight = Sig(CampaignSession.Current, ghost?.BuildingId, a);
            Restore();
            CampaignState l = CampaignSession.Current;
            string after = Json(OwnOutpostService.StateOf(l));
            bool same = domain == after && M(a).OutpostId == machineOutpost && OwnOutpostService.NameOf(l, o.OutpostId) == "存档岭"
                        && DropPodService.Pods(l).Any(p => p.State == DropPodService.StateOutbound && p.ConstructionPack);
            Expect(save.Success && launched && flying && same,
                $"H1 FG08 第 9 节“前哨站分组与名字”存读档：真文件存读档后前哨站域（编号 / 名字 / 锚点 / 统计）逐字段一致（{domain == after}）、机器的常驻前哨站、在飞施工包的标记都还在");
            Seconds(30f);
            string loaded = Sig(l, ghost?.BuildingId, a);
            Expect(straight == loaded, "H2 读档接着跑 = 不存档一路跑：施工包在飞时存档，之后各跑 30 游戏秒（着陆倒进现场、前哨劳动回驻点 / 施工），前哨站域 / 虚影材料 / 机器位置 / 舱逐字段一致" +
                                       (straight == loaded ? string.Empty : $"\n不存档：{straight}\n读档后：{loaded}"));
        }

        // ── I 暂停与倍速 ─────────────────────────────────────────────────────────

        private static string TimingScenario(int seed, float speed, bool pauseFirst, out bool pausedHeld)
        {
            pausedHeld = true;
            CampaignState s = NewWorld(seed);
            int need = BeltScenario(s, out OwnOutpostRecord o, out BuildingRecord ghost, out Vector2 c);
            if (ghost == null)
            {
                return "no-setup";
            }
            Vector2 away = c + (new Vector2(Core(s).X, Core(s).Y) - c).normalized * 40f;
            HomeGridService.RevealArea(s, away, 20f);
            int a = SpawnMachine(s, away, MachineRole.OutpostLabor);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Sig(s, ghost.BuildingId, a);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Sig(s, ghost.BuildingId, a) == p0;
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
            return Sig(s, ghost.BuildingId, a);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = TimingScenario(8114, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != null && !reference.StartsWith("no-", StringComparison.Ordinal),
                "I 暂停中（120 帧）传送带不送料、机器不动、前哨站不对账；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒（传送带送进现场、前哨劳动回驻点 / 施工），前哨站域 / 现场材料 / 机器位置逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        // ── J 种子集 ─────────────────────────────────────────────────────────────

        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(3).ToArray();
            var bad = new List<string>();
            foreach (int seed in seeds)
            {
                CampaignState s = NewWorld(seed);
                int need = BeltScenario(s, out OwnOutpostRecord o, out BuildingRecord ghost, out Vector2 c);
                if (o == null || ghost == null)
                {
                    bad.Add($"{seed}：前哨站或正对虚影的传送带放不下");
                    continue;
                }
                bool full = StepUntil(() => ghost.ConstructionDelivered >= ghost.ConstructionRequired, 90);
                if (!full || OwnOutpostService.DistanceFromCore(s, o) <= 300f)
                {
                    bad.Add($"{seed}：传送带没把材料送进现场 / 前哨站距离不对");
                }
            }
            Expect(bad.Count == 0, $"J B25 种子集 {string.Join("/", seeds)}：按地形找 340 格外的空地建前哨站、铺正对虚影的传送带，前哨站自动归组、材料送进现场" + (bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad)));
        }

        // ── K 界面 ─────────────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool Alt;
            public bool GetKey(KeyCode key) => key == Down || (Alt && (key == KeyCode.LeftAlt || key == KeyCode.RightAlt));
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

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
            CampaignState s = NewWorld(8115);
            BuildingRecord st = DropStation(s, 4);
            OwnOutpostRecord o = st != null ? MakeOutpost(s, out Vector2 c) : null;
            if (o == null)
            {
                return;
            }
            GameObject go = null, rgo = null, mgo = null;
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "OutpostOverviewPanel.uxml", out go);
                OutpostOverviewPanelUIToolkit.InWorldOverrideForTests = true;
                OutpostOverviewPanelUIToolkit panel = go.AddComponent<OutpostOverviewPanelUIToolkit>();
                panel.BindView(root);
                // 快捷键 Alt+U（全部上下文）打开。
                var keys = new Keys { Alt = true };
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.OpenOutposts);
                UiKitInputPump.ProcessLibraryKeys();
                keys.Down = KeyCode.None;
                InputRouter.DebugClearConsumedKeys();
                InputRouter.DebugSetReader(null);
                panel.Refresh(force: true);
                bool opened = OutpostOverviewPanelUIToolkit.IsOpen && panel.PanelVisible && panel.RowsShown == 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OutpostPanelFirstOpen);
                string row = OwnOutpostService.RowText(panel.Row(0));
                bool rowText = row.Contains("前哨站 1") && row.Contains("供电") && row.Contains("信号") && row.Contains("补给") && row.Contains("建筑 3") && panel.SummaryText.Contains("1 个前哨站");
                Expect(opened && rowText, $"K1 FGU-47 总览：{InputDisplay.ForAction(GameActionId.OpenOutposts)} 打开，每个前哨站一行写建筑、供电、防御与覆盖、补给（“{row.Replace("\n", " / ")}”）；汇总“{Short(panel.SummaryText)}”");
                // 改名：输入框 + 按钮（与服务同一入口）。
                panel.NameField.value = "东哨";
                bool renamed = Click(panel.RenameButton) && OwnOutpostService.NameOf(o) == "东哨" && panel.MessageText.Contains("东哨");
                bool resetOn = panel.ResetNameButton.enabledSelf;
                bool packOff = !panel.PackButton.enabledSelf; // 没有缺料的现场
                BuildingRecord ghost = Ghost(s, Gen2, new Vector2(o.X, o.Y), 4f, 20f);
                OwnOutpostService.Sync(s);
                panel.Refresh(force: true);
                bool packOn = panel.PackButton.enabledSelf && panel.DetailText.Contains("施工还缺");
                bool packed = Click(panel.PackButton) && panel.MessageText.Contains("施工包") && DropPodService.Pods(s).Any(p => p.ConstructionPack && p.State == DropPodService.StateOutbound);
                Expect(renamed && resetOn && packOff && packOn && packed,
                    $"K2 详情：改名（{renamed}，“恢复默认名”可点 {resetOn}）；没有缺料的现场时“投送施工包”不可点（{packOff}），放了虚影后可点并写“施工还缺…”，点它发射施工包（“{Short(panel.MessageText)}”）");
                // 派驻机器：打开名册（子页），在名册详情里选常驻前哨站。
                VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out rgo);
                RosterPanelUIToolkit.InWorldOverrideForTests = true;
                RosterPanelUIToolkit roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                roster.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RosterRow.uxml"));
                bool rosterOpen = Click(panel.RosterButton) && RosterPanelUIToolkit.IsOpen;
                int a = SpawnMachine(s, new Vector2(Core(s).X + 4f, Core(s).Y + 4f));
                MachineRoster.TrySetRole(s, a, MachineRole.OutpostLabor, out _);
                roster.ShowDetail(a);
                bool picker = roster.DetailPointVisible && roster.DetailPointIsOutpost && roster.DetailPointField.choices.Contains("东哨");
                RosterPanelUIToolkit.Close();
                Expect(rosterOpen && picker, $"K3 “派驻机器…”打开名册（{rosterOpen}）；前哨劳动机器的详情里有“常驻前哨站”下拉框，列出“东哨”（{picker}）");
                // 战略地图“前哨站”按钮：收起地图、打开总览。
                OutpostOverviewPanelUIToolkit.Close();
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                mgo = new GameObject("fgoutpost-map");
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                StrategicMapUIToolkit map = mgo.AddComponent<StrategicMapUIToolkit>();
                map.BindView(mroot);
                StrategicMapUIToolkit.OpenCentered(o.X, o.Y, 500.0);
                map.Tick(force: true);
                Button mb = mroot.Q<Button>("MapOutposts");
                bool mapBtn = mb != null && mb.text.Contains("前哨站") && Click(mb) && !StrategicMapUIToolkit.IsOpen && OutpostOverviewPanelUIToolkit.IsOpen;
                bool locate = Click(panel.LocateButton) && !OutpostOverviewPanelUIToolkit.IsOpen;
                Expect(mapBtn && locate, $"K4 战略地图“{mb?.text}”按钮收起地图、打开总览（{mapBtn}）；“定位”镜头飞到前哨站并关掉总览（{locate}）");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "OutpostOverviewPanel.uxml", "OpWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("OpRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "K5 布局探针（四种分辨率 + 超长文字 + USS 体检）：前哨站总览 " + (probe.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : probe));
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                OutpostOverviewPanelUIToolkit.Close();
                OutpostOverviewPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Close();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                foreach (GameObject x in new[] { go, rgo, mgo })
                {
                    if (x != null)
                    {
                        Object.DestroyImmediate(x);
                    }
                }
            }
        }

        // ── L 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(8116);
            var centers = new List<Vector2>();
            for (int i = 0; i < 4; i++)
            {
                Vector2? p = FindSpot(s, OutpostDistance + i * 60f, 6, 0, i * 90f);
                if (p.HasValue)
                {
                    centers.Add(p.Value);
                }
            }
            // 每个前哨站约 60 座（含电塔），家园另有约 200 座：模拟一个中后期的家园。
            int placed = 0;
            foreach (Vector2 c in centers)
            {
                for (int k = 0; k < 60; k++)
                {
                    // 每个前哨站先放 4 座实体建筑（只有电塔的建筑群不算前哨站，审查修复），其余是电塔。
                    string type = k < 4 ? Gen2 : "power_pole";
                    GridCell? cell = FreeNearQuiet(s, type, c, k % 20, 30f);
                    if (cell.HasValue)
                    {
                        RegisterQuiet(s, type, cell.Value);
                        placed++;
                    }
                }
            }
            GridCell core = Core(s);
            for (int k = 0; k < 200; k++)
            {
                GridCell? cell = FreeNearQuiet(s, "power_pole", new Vector2(core.X, core.Y), 8 + k % 40, 60f);
                if (cell.HasValue)
                {
                    RegisterQuiet(s, "power_pole", cell.Value);
                }
            }
            HomeGridService.MapFor(s);
            OwnOutpostService.Sync(s);
            int n = s.BuildingRecords.Length;
            var sw = Stopwatch.StartNew();
            const int R = 50;
            for (int i = 0; i < R; i++)
            {
                OwnOutpostService.ResetSessionState(); // 强制重建索引（建筑列表变化时才会发生）
                OwnOutpostService.Sync(s);
            }
            sw.Stop();
            double rebuild = sw.Elapsed.TotalMilliseconds / R;
            sw.Restart();
            for (int i = 0; i < 500; i++)
            {
                OwnOutpostService.Sync(s);
            }
            sw.Stop();
            double steady = sw.Elapsed.TotalMilliseconds / 500;
            sw.Restart();
            for (int i = 0; i < 2000; i++)
            {
                OwnOutpostService.OutpostIdAt(s, new Vector2(core.X + i % 500, core.Y));
            }
            sw.Stop();
            double query = sw.Elapsed.TotalMilliseconds / 2000;
            var views = new List<OutpostView>();
            sw.Restart();
            for (int i = 0; i < 50; i++)
            {
                OwnOutpostService.CollectViews(s, views);
            }
            sw.Stop();
            double view = sw.Elapsed.TotalMilliseconds / 50;
            PerfLines.Add($"{centers.Count} 个前哨站 / {n} 座建筑：重建索引 + 对账 {rebuild:F3} ms / 次（只在建筑列表变化时）；稳态对账 {steady * 1000:F1} µs / 次（每游戏秒一次）；按位置查前哨站 {query * 1000:F2} µs / 次；总览数据 {view:F3} ms / 次（打开时每 0.5 秒）");
            bool within = rebuild < 4.0 && steady < 0.15 && query < 0.01 && view < 1.5;
            bool warn = !within && rebuild < 8.0 && steady < 0.3 && query < 0.02 && view < 3.0;
            if (warn)
            {
                Line($"  ⚠ 性能警告（超线不到 2 倍）：重建 {rebuild:F3} ms（线 4）、稳态 {steady * 1000:F1} µs（线 150）、查询 {query * 1000:F2} µs（线 10）、总览 {view:F3} ms（线 1.5）");
            }
            Expect(centers.Count >= 3 && placed > 100 && (within || warn),
                $"L 性能（单次测量，Editor batchmode / Mono JIT）：{n} 座建筑时重建索引 {rebuild:F3} ms（线 4 ms）、稳态对账 {steady * 1000:F1} µs（线 150 µs）、按位置查 {query * 1000:F2} µs（线 10 µs）、总览数据 {view:F3} ms（线 1.5 ms）");
        }

        private static GridCell? FreeNearQuiet(CampaignState s, string type, Vector2 center, float from, float to)
        {
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float ang = (a * 15f + d * 11f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(center.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(center.y + Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static void RegisterQuiet(CampaignState s, string type, GridCell c)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#perf" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
        }

        // ── M 旧档 ─────────────────────────────────────────────────────────────

        private static void CheckOldSave()
        {
            CampaignState old = CampaignState.CreateNew("fgoutpost-old", "Standard", 8117);
            old.Grid.OwnOutposts = null;
            CampaignFgStateDomains.EnsureAll(old);
            bool empty = old.Grid.OwnOutposts != null && old.Grid.OwnOutposts.Outposts.Length == 0 && old.Grid.OwnOutposts.NextSerial == 1 && !old.Grid.OwnOutposts.Seeded;
            // 旧档里已经有远处的建筑群：第一次对账静默补登（不补发“建立前哨站”通知），之后新出现的才通知。
            CampaignState s = NewWorld(8118);
            Vector2? p = FindSpot(s, OutpostDistance, 6);
            if (p.HasValue)
            {
                PlaceNear(s, Gen2, p.Value, 0f, 12f);
            }
            s.Grid.OwnOutposts = null;
            CampaignFgStateDomains.EnsureAll(s);
            int n0 = NotifyCount(OwnOutpostService.NotifyFounded);
            OwnOutpostService.Sync(s);
            bool silent = OwnOutpostService.All(s).Count == 1 && NotifyCount(OwnOutpostService.NotifyFounded) == n0 && s.Grid.OwnOutposts.Seeded;
            // 坏值：重复编号 / 空编号 / 名字带非法字符 → 去掉 / 清洗。
            s.Grid.OwnOutposts.Outposts = s.Grid.OwnOutposts.Outposts.Concat(new[]
            {
                new OwnOutpostRecord { OutpostId = s.Grid.OwnOutposts.Outposts[0].OutpostId, Serial = 9 },
                new OwnOutpostRecord { OutpostId = string.Empty, Serial = 3 },
            }).ToArray();
            s.Grid.OwnOutposts.Outposts[0].CustomName = "坏<名>";
            OwnOutpostService.EnsureState(s);
            bool cleaned = s.Grid.OwnOutposts.Outposts.Length == 1 && s.Grid.OwnOutposts.Outposts[0].CustomName == "坏名" && s.Grid.OwnOutposts.NextSerial >= 2;
            Expect(empty && silent && cleaned, $"M 旧档：没有前哨站域 = 空域（{empty}）；已有的远处建筑群第一次对账静默补登、不补发通知（{silent}）；重复 / 空编号去掉、名字清洗（{cleaned}）");
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
