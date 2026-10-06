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
using GameLogic.Campaign.Logistics;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Regions;
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
    /// FG4-ECO-09 离家报告（生产部分）与告警补全（FG04 FGR-ECO-060 / 080；FG13 FGU-30；FGT-ECO-007 / 009）自检。全部起真实世界（家园、电网、生产、真实出发 / 撤离事务）跑：
    /// A 数据（调参、文本中英、图鉴与钩子、通知类型与离家报告分段、快捷键 Alt+H、设置默认开）；
    /// B FGT-ECO-007（真实出发 → 离家期间生产 / 缺料 / 停电 / 建筑被毁 / 突袭到达 / 规则动作 / 家园机器重伤 / 远征队阵亡 → 真实撤离；报告每一项与独立读数逐项一致）与全灭放弃；
    /// C 负向：离家期间什么都没发生的空报告；出发被拦不开报告；
    /// D 最近 3 份、条目上限；E 自动打开（设置开 / 关、面板与暂停菜单两个开关）；F 面板（真 UXML：每一条都能点击定位 / 打开面板、定位失败写原因、空状态、Alt+H、布局探针）；
    /// G FGT-ECO-009 告警六级都有真实数据源（核心受损 / 突袭到达、缺电、仓满、持续缺料 / 输出堵塞、机器重伤、工作受阻），排序、定位、通知只发一次、恢复后消失；
    /// H 真文件存读档（远征途中存、读档接着记；旧存档补空域；旧档读档时正在远征从此刻开一份）；I 暂停与 0.5x～3x；J 观察一致（FGR-BASE-021）；K 性能与“不每帧遍历”。
    /// </summary>
    public static class FgAwayReportSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 9500;
        private static double _fakeNow = 1000;

        [MenuItem("BinGames/QA/自检/FG 离家报告与告警补全")]
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
            Line("\n[离家报告与告警补全] FGT-ECO-007 真实远征一次报告与实际一致、空报告、最近 3 份、自动打开与设置、面板点击定位、FGT-ECO-009 告警六级、存读档、倍速、观察一致、性能（FG4-ECO-09）");
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
            NotificationCenter.LocateDelegate originalLocate = NotificationCenter.LocateHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgaway-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                NotificationCatalog.Reload();
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
                     "离家报告记账 / 告警汇总 / 报告组装在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网 / 传送带 / 管线 / 战斗内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckExpeditionReport);
                Step(CheckWipeReport);
                Step(CheckEmptyAndBlocked);
                Step(CheckRecentAndCaps);
                Step(CheckAutoOpenAndSetting);
                Step(CheckAlarmsSixLevels);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance);
                HomeValleyWorkOrders.ResetSessionState();
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                AwayReportPanelUIToolkit.Close();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
            }
            finally
            {
                AwayReportPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                NotificationCenter.LocateHandler = originalLocate;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
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
                WorldSimulation.UnloadAll();
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
            Line($"  · [离家报告与告警补全] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            StandingRuleService.ResetForTests();
            MachineRoster.ResetForTests();
            _seq = 9500;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            // 测试只看自己设的规则：删掉新战役默认开启的“远征卸货”。
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
            return s;
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（真实放置 / 施工由 FG3 / FG4-ECO-05 覆盖），位置按种子地形找空地（B25）。</summary>
        private static BuildingRecord Place(CampaignState s, string typeId)
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, typeId, 8f, 26f);
            if (!at.HasValue)
            {
                return null;
            }
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = at.Value.X,
                GridY = at.Value.Y,
                Position = GridMath.FootprintCenter(at.Value, bg.FootprintW, bg.FootprintH, 0),
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

        private static int Spawn(string chassis, string blueprint, Vector2 offset, float hp)
        {
            CampaignState s = CampaignSession.Current;
            Vector2 core = HomeValleyLayout.Core.Position;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, core + offset, hp, hp);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        /// <summary>出发的前置（与 FG4-ECO-07 名册自检 C3 相同）：再造三台能出征的机器、信号塔与发电机修好（带宽）、破碎都市可出征。返回出征名单（3 台）。</summary>
        private static int[] PrepareDeparture(CampaignState s)
        {
            Spawn(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f), 120f);
            Spawn(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f), 100f);
            Spawn(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(8f, -4f), 100f);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
                {
                    b.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：信号塔修好（出发的带宽要够；修复流程由 FG4-ECO-05 覆盖）
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
        }

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) => b == null ? null : FgProductionSelfCheck.P(s, b);

        private static ItemDef Def(string id) => ItemCatalog.Find(id);

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static List<NotificationEntry> Notes(string type)
        {
            var list = new List<NotificationEntry>();
            NotificationCenter.Query(null, type, list);
            return list;
        }

        private static int NoteCount(string type) => Notes(type).Sum(e => e.Count);

        /// <summary>按当前倍速推进到第 <paramref name="target"/> 个世界步（真实帧路径 WorldSimulation.Frame，带步数上限：不同倍速在完全相同的步上停下），每帧取走精炼炉的产出。</summary>
        private static void RunTo(long target, BuildingRecord drain, CampaignState s)
        {
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 2000)
            {
                WorldSimulation.Frame(1f / 60f, target);
                if (drain != null && P(s, drain) != null)
                {
                    P(s, drain).Rec.Out = Array.Empty<ItemStackRecord>();
                }
                frames++;
            }
        }

        private static long Ticks(float seconds) => GameClock.TicksFor(seconds);

        // ── 远征场景（B / H / I / J 共用）────────────────────────────────────────────

        private sealed class Away
        {
            public CampaignState S;
            public BuildingRecord Furnace;
            public BuildingRecord Bench;
            public BuildingRecord Gen2;
            public BuildingRecord Gen1;
            public int[] Roster = Array.Empty<int>();
            public int HomeMachine;
            public int Killed;
            public float WoundedHealth;
            public long DepartTick;
            public long DestroyTick;
            public long RestoreTick;
            public long KillTick;
            public long RaidTick;
            public long FurnaceDone0;
            public int BrownAtDestroy;
            public StandingRuleRecord War;
            public TransitGroupRecord Raid;
            public string Failure;
        }

        /// <summary>精炼炉（满料）→ 真实出发 → 出发后在家园放一座缺稀土矿的电子组装台。observeHome = 出发后镜头切回家园（玩家看着家园）。</summary>
        private static Away LayAndDepart(int seed, bool observeHome)
        {
            var a = new Away();
            CampaignState s = NewWorld(seed);
            a.S = s;
            a.Furnace = Place(s, "refinery_furnace");
            if (a.Furnace == null)
            {
                a.Failure = "放不下精炼炉";
                return a;
            }
            ProductionService.TrySetRecipe(s, a.Furnace.BuildingId, "alloy_scrap", out _);
            P(s, a.Furnace).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
            a.War = StandingRuleService.TryCreate(s, StandingRuleService.KindWar, out StandingRuleRecord war, out _) ? war : null;
            a.Roster = PrepareDeparture(s);
            a.HomeMachine = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !a.Roster.Contains(m.LogicId))
                .Select(m => m.LogicId).OrderBy(id => id).FirstOrDefault();
            a.Gen1 = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            a.Gen2 = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(a.Roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                a.Failure = $"真实出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）";
                return a;
            }
            s = CampaignSession.Current;
            a.S = s;
            if (observeHome)
            {
                WorldView.Observe(HomeValleyLayout.RegionId);
            }
            a.DepartTick = GameClock.Ticks;
            a.FurnaceDone0 = P(s, a.Furnace).Rec.Completed;
            a.Bench = Place(s, "electronics_bench");
            if (a.Bench != null)
            {
                ProductionService.TrySetRecipe(s, a.Bench.BuildingId, "electronic", out _);
                P(s, a.Bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } }; // 有合金、缺稀土矿
            }
            return a;
        }

        /// <summary>离家期间的事（每件都在固定的世界步上发生，不同倍速 / 观察结果可逐字段比较）：生产 30 秒 → 两台发电机被摧毁（停电）→ 20 秒 → 修好 → 20 秒 →
        /// 突袭部队到达家园（战时预案规则动作）、家园一台机器重伤、远征队一台阵亡 → 10 秒。</summary>
        private static void AwayEvents(Away a, bool withCombat = true)
        {
            CampaignState s = a.S;
            long t = a.DepartTick;
            RunTo(t += Ticks(30f), a.Furnace, s);
            a.DestroyTick = GameClock.Ticks;
            // 摧毁来源（FG6 突袭的伤害）在家园的模拟步里发生：按家园地点执行（通知与报告条目记的地点 = 事件真正发生的地点，与镜头在哪无关）。
            WorldSimulation.RunAsSite(HomeValleyLayout.RegionId, () =>
            {
                if (a.Gen2 != null)
                {
                    HomeValleyPowerGrid.ApplyBuildingDestroyed(s, a.Gen2.BuildingId);
                }
                if (a.Gen1 != null)
                {
                    HomeValleyPowerGrid.ApplyBuildingDestroyed(s, a.Gen1.BuildingId);
                }
            });
            a.BrownAtDestroy = HomeValleyPowerGrid.GetSummary(s).BrownoutBuildingIds?.Length ?? 0;
            RunTo(t += Ticks(20f), a.Furnace, s);
            a.RestoreTick = GameClock.Ticks;
            foreach (BuildingRecord g in new[] { a.Gen1, a.Gen2 })
            {
                if (g != null)
                {
                    g.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：修好（维修流程由 FG4-ECO-05 覆盖）
                    g.Health = BuildingOps.MaxDurability(g.BuildingTypeId);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            RunTo(t += Ticks(20f), a.Furnace, s);
            if (withCombat)
            {
                a.Raid = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out _);
                if (a.Raid != null)
                {
                    a.Raid.PosX = a.Raid.TargetX; // 测试捷径：行进由 FG0-ARCH-01 验证；放到目标点，下一步按正式路径“到达”
                    a.Raid.PosY = a.Raid.TargetY;
                }
                a.RaidTick = GameClock.Ticks;
                MachineRecord home = Rec(a.HomeMachine);
                if (home != null)
                {
                    MachineRegistry.ApplyDamage(home.LogicId, home.Health - home.MaxHealth * 0.25f);
                    a.WoundedHealth = home.Health;
                }
                a.Killed = a.Roster.Length > 1 ? a.Roster[a.Roster.Length - 1] : 0;
                a.KillTick = GameClock.Ticks;
                if (a.Killed > 0)
                {
                    MachineRegistry.ApplyDamage(a.Killed, 1e6f);
                }
            }
            RunTo(t += Ticks(10f), a.Furnace, s);
        }

        /// <summary>报告域逐字段快照（JsonUtility，与存档同一序列化）。</summary>
        private static string Snap(CampaignState s) => JsonUtility.ToJson(s.Stats.AwayReports);

        private static List<AwayLine> Lines(CampaignState s, AwayReportRecord r)
        {
            var lines = new List<AwayLine>();
            AwayReportView.Build(s, r, lines);
            return lines;
        }

        private static string Text(List<AwayLine> lines) => string.Join(" / ", lines.Select(l => l.Text));

        private static long BucketStarve(ProductionService.Producer p, string itemId)
        {
            long sum = 0;
            foreach (ProducerStatBucket b in p?.Rec?.Stats ?? Array.Empty<ProducerStatBucket>())
            {
                if (b?.Starve == null || b.Index < 0)
                {
                    continue;
                }
                sum += b.Starve.Where(x => x != null && x.ItemId == itemId).Sum(x => (long)x.Amount);
            }
            return sum;
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] tuning =
            {
                "away.reports_keep", "away.bottleneck_top", "away.items_max", "away.entries_max", "away.rules_max", "away.outages_max", "away.machines_max",
                "alarm.jam_seconds", "alarm.machine_wounded_fraction",
            };
            string[] missing = tuning.Where(id => !GridContent.TryGetTuning(id, out _)).ToArray();
            bool spec = AwayReportService.ReportsKeep == 3 && AwayReportService.BottleneckTop == 3;
            string[] keys =
            {
                "away.panel.title", "away.panel.none", "away.panel.empty_report", "away.panel.auto_open", "away.summary", "away.summary.open", "away.section.production",
                "away.section.bottleneck", "away.section.power", "away.section.weather", "away.section.machines", "away.section.raid", "away.section.event",
                "away.section.research", "away.section.rules", "away.prod.row", "away.bn.row", "away.power.outage", "away.machine.died", "away.machine.hurt_bad",
                "away.weather.none", "away.event.none", "alarm.level.core", "alarm.level.power", "alarm.level.storage", "alarm.level.factory", "alarm.level.machine",
                "alarm.level.work", "alarm.factory.starved", "alarm.factory.blocked", "alarm.machine.wounded", "alarm.core.damaged", "alarm.core.raid", "alarm.power.brownout",
                "alarm.storage.items", "pause.away_report", "pause.away_auto", "codex.economy.away.title", "codex.economy.away.body", "input.action.open_away_report.name",
                "notify.type.input_starved.single", "notify.type.output_blocked.single", "notify.type.machine_wounded.single", "notify.type.world_event.single",
                "notify.type.away_report.single",
            };
            var badText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    if (!GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k)))
                    {
                        badText.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CodexEntry codex = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.economy.away");
            string[] hooks = { GuidanceHooks.AwayReportFirstOpen, GuidanceHooks.AwayFirstAutoOpen, GuidanceHooks.AlarmFirstFactoryJam, GuidanceHooks.AlarmFirstMachineWounded };
            bool codexOk = codex != null && hooks.All(h => codex.Hooks.Contains(h)) && hooks.All(h => GuidanceHooks.Known.Contains(h));
            bool action = InputActionCatalog.TryGet(GameActionId.OpenAwayReport, out InputActionDef act) && act.DefaultChord.Key == KeyCode.H
                          && act.DefaultChord.Mods == InputModifier.Alt && act.Status == InputActionStatus.Wired;
            var expectTier = new Dictionary<string, NotifyLevel>
            {
                { "input_starved", NotifyLevel.Warning }, { "output_blocked", NotifyLevel.Warning }, { "machine_wounded", NotifyLevel.Warning },
                { "world_event", NotifyLevel.Warning }, { "away_report", NotifyLevel.Info },
            };
            bool tiers = expectTier.All(kv => NotificationCatalog.TryGetType(kv.Key, out NotifyTypeDef d) && d.Tier == kv.Value && d.KeepInHistory);
            // 通知类型 → 离家报告分段（新增告警里“突袭、静默夜、天气、事件、研究完成、解析完成”都有类型并进报告；机器伤亡由登记表直接记，不重复映射）。
            var expectSection = new Dictionary<string, string>
            {
                { "raid_arrival", "raid" }, { "silent_night", "weather" }, { "weather", "weather" }, { "world_event", "event" }, { "research_done", "research" },
                { "analysis_complete", "research" }, { "building_damaged", "buildings" }, { "building_destroyed", "buildings" }, { "power_fuel_out", "power" },
                { "output_blocked", "bottleneck" }, { "eco_deficit", "production" }, { "machine_destroyed", "none" }, { "machine_wounded", "none" }, { "input_starved", "none" },
            };
            string[] badSection = expectSection.Where(kv => !NotificationCatalog.TryGetType(kv.Key, out NotifyTypeDef d) || d.AwaySection != kv.Value)
                .Select(kv => kv.Key).ToArray();
            // 源数据（tools/cell_tables/fgdata_away.py）与运行时表一致：图鉴条目逐字段。
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string dump) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string cx = dump.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("CX\tcodex.economy.away\t", StringComparison.Ordinal)) ?? string.Empty;
            string runtime = codex == null ? string.Empty : string.Join("\t", "CX", codex.Id, codex.Tab, codex.TitleKey, codex.BodyKey, codex.HintKey, codex.Links, codex.Hooks,
                codex.SortOrder.ToString(CultureInfo.InvariantCulture));
            bool settingDefault = new GameSettingsData().AwayReportAutoOpen;
            Expect(missing.Length == 0 && spec && badText.Count == 0 && codexOk && action && tiers && badSection.Length == 0 && code == 0 && cx == runtime && settingDefault,
                $"A 数据：调参齐全（缺 {string.Join(",", missing)}）、最近 {AwayReportService.ReportsKeep} 份、前 {AwayReportService.BottleneckTop} 个瓶颈（规格值）；文本中英齐全（缺 {string.Join(",", badText)}）；" +
                $"图鉴“离家报告与家园告警”带四个钩子、源数据与运行时逐字段一致（{cx == runtime}）；快捷键 OpenAwayReport 默认 Alt+H 已接入；新通知类型等级正确；" +
                $"通知类型 → 离家报告分段对照正确（不符 {string.Join(",", badSection)}）；“远征回来时自动打开”默认开");
        }

        // ── B FGT-ECO-007 ─────────────────────────────────────────────────────────────

        private static void CheckExpeditionReport()
        {
            Away a = LayAndDepart(9101, observeHome: false);
            if (a.Failure != null || a.Bench == null)
            {
                Fail("B 场景搭不起来：" + (a.Failure ?? "放不下电子组装台"));
                return;
            }
            CampaignState s = a.S;
            AwayReportRecord open = AwayReportService.Current(s);
            bool opened = open != null && open.StartTick == a.DepartTick && open.Members.SequenceEqual(a.Roster) && open.RegionId == FracturedCityLayout.RegionId
                          && open.EndTick < 0;
            Expect(opened, $"B0 真实出发事务成功 → 开一份离家报告（地点 {open?.RegionId}，出发步 {open?.StartTick} = {a.DepartTick}，名单 {string.Join(",", open?.Members ?? Array.Empty<int>())}）");
            // 物流：出发后在家园铺一条带分流器的传送带（起点推 30 件，两路各有一个收货口），离家期间的送达 / 推上 / 分出进报告。
            int srcId = 0;
            var sinks = new List<int>();
            GridCell sp = default;
            GridCell? area = FgProductionSelfCheck.FindArea(s, 6, 7);
            bool beltLaid = false;
            if (area.HasValue)
            {
                GridCell bo = FgProductionSelfCheck.At(area.Value, 0, 3);
                beltLaid = FgProductionSelfCheck.Belts(s, bo, BeltDir.East, 3);
                sp = FgProductionSelfCheck.At(bo, 3, 0);
                beltLaid &= BeltNetworkService.TryPlaceNode(s, sp, BeltDir.East, 2, BeltNodeKind.Splitter).Ok;
                if (beltLaid && BeltNetworkService.Kernel.TryGetNodeInfo(sp.X, sp.Y, out BeltNodeInfo ni))
                {
                    foreach ((int x, int y) in new[] { (ni.OutLX, ni.OutLY), (ni.OutRX, ni.OutRY) })
                    {
                        var c = new GridCell(x, y);
                        beltLaid &= FgProductionSelfCheck.Belts(s, c, BeltDir.East, 2);
                        sinks.Add(FgProductionSelfCheck.TestSink(s, FgProductionSelfCheck.At(c, 2, 0)));
                    }
                }
                srcId = FgProductionSelfCheck.TestSource(s, bo, "alloy", 30);
            }
            int destroyedNotes0 = NoteCount("building_destroyed");
            AwayEvents(a);
            CampaignState now = CampaignSession.Current;
            // 进行中的报告能随时看（显示时现算瓶颈与差额，不改存档）。
            string openSnap = Snap(now);
            List<AwayLine> openLines = Lines(now, AwayReportService.Current(now));
            bool openView = openLines.Count > 3 && Snap(now) == openSnap && AwayReportView.Summary(now, AwayReportService.Current(now)).Contains("还在进行");
            long furnaceDone = P(now, a.Furnace).Rec.Completed - a.FurnaceDone0;
            long benchStarve = BucketStarve(P(now, a.Bench), "rare_earth_ore");
            // 持续赤字警告的定位（DEBT-FG4ECO08-02）：最近窗口里缺这种物品最久的建筑。
            BuildingRecord mostStarved = ProductionStats.MostStarvedFor("rare_earth_ore");
            BuildingRecord noneStarved = ProductionStats.MostStarvedFor(ItemCatalog.ScrapId);
            BeltKernel bk = BeltNetworkService.Kernel;
            long srcTotal = bk != null && bk.TryGetPortInfo(srcId, out BeltPortInfo si) ? si.Total : -1;
            long sinkTotal = sinks.Sum(id => bk != null && bk.TryGetPortInfo(id, out BeltPortInfo ki) ? ki.Total : 0);
            long splitTotal = bk != null && bk.TryGetNodeInfo(sp.X, sp.Y, out BeltNodeInfo spi) ? spi.SentL + spi.SentR : -1;
            long scansBeforeEnd = AwayReportService.CloseScans;
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            now = CampaignSession.Current;
            List<AwayReportRecord> recent = AwayReportService.Recent(now);
            AwayReportRecord r = recent.FirstOrDefault();
            if (!ret.Success || r == null)
            {
                Fail($"B 真实撤离失败（{ret.FailureReason}）或没有生成报告");
                return;
            }
            Expect(openView && r.Outcome == AwayReportService.OutcomeEvacuated && r.EndTick >= a.KillTick && !AwayReportService.IsOpen(now) && r.Serial == open.Serial
                   && AwayReportService.CloseScans > scansBeforeEnd && NoteCount("away_report") >= 1,
                $"B1 真实撤离事务 → 报告结算进“最近报告”（结果：撤离回家；进行中也能查看且查看不改存档 {openView}）；发一条“离家报告”通知");
            // 生产：报告的合金产出 = 精炼炉离家期间完成的份数（独立读数），没有被消耗。
            long alloyP = r.Produced.Where(x => x.ItemId == "alloy").Sum(x => x.Amount);
            long alloyC = r.Consumed.Where(x => x.ItemId == "alloy").Sum(x => x.Amount);
            Expect(furnaceDone > 0 && alloyP == furnaceDone && alloyC == 0,
                $"B2 生产：报告里合金产出 {alloyP} = 精炼炉离家期间完成 {furnaceDone} 份，消耗 {alloyC}（与统计同一记账口径，逐件一致）");
            AwayLine flowLine = Lines(now, r).FirstOrDefault(l => l.Section == "logistics" && l.Text.StartsWith("传送带：送进", StringComparison.Ordinal));
            AwayLine splitLine = Lines(now, r).FirstOrDefault(l => l.Section == "logistics" && l.Text.Contains("分流器共分出"));
            Expect(beltLaid && sinkTotal > 0 && r.BeltIn == sinkTotal && r.BeltOut == srcTotal && r.Split == splitTotal && splitTotal > 0
                   && flowLine != null && splitLine != null && flowLine.Action == AwayLineAction.StatsTab,
                $"B2b 物流（DEBT-FG3LOG03-08 / 04-04）：报告里传送带送进建筑 {r.BeltIn} 件 = 两个收货口累计 {sinkTotal}，推上 {r.BeltOut} 件 = 起点累计 {srcTotal}，" +
                $"分流器分出 {r.Split} = 内核左右口累计 {splitTotal}；“{flowLine?.Text}”“{splitLine?.Text}”");
            Expect(mostStarved == a.Bench && noneStarved == null,
                $"B2c 持续赤字警告的定位（DEBT-FG4ECO08-02）：缺稀土矿最久的建筑 = 电子组装台（{mostStarved?.BuildingId}）；没有建筑缺废料时不给位置（{noneStarved == null}）");
            // 瓶颈：缺稀土矿的电子组装台（出发后才建，缺料全在离家期间）——步数 = 它自己的 10 分钟缺料桶合计。
            AwayStarveRecord st = r.Starve.FirstOrDefault(x => x.ItemId == "rare_earth_ore" && x.BuildingId == a.Bench.BuildingId);
            List<AwayLine> lines = Lines(now, r);
            AwayLine bnLine = lines.FirstOrDefault(l => l.Section == "bottleneck" && l.IsEntry && l.Arg == a.Bench.BuildingId);
            Expect(st != null && benchStarve > 0 && st.Ticks == benchStarve && bnLine != null && bnLine.Text.Contains(ItemCatalog.NameOf("rare_earth_ore"))
                   && bnLine.Action == AwayLineAction.Building && bnLine.HasPos && Mathf.Approximately(bnLine.Pos.x, a.Bench.Position.x) && Mathf.Approximately(bnLine.Pos.z, a.Bench.Position.y),
                $"B3 瓶颈：缺稀土矿 {st?.Ticks} 步 = 电子组装台自己的缺料桶 {benchStarve} 步；“{bnLine?.Text}”点击定位到那座建筑并打开面板");
            // 停电：一段，开始 / 结束步 = 摧毁 / 修好的那一步，最多停机座数 = 摧毁时的缺电建筑数；缺电秒数 ≈ 停电时长。
            AwayOutageRecord o = r.Outages.FirstOrDefault();
            long outageSec = (a.RestoreTick - a.DestroyTick) / GameClock.StepHz;
            AwayLine outLine = lines.FirstOrDefault(l => l.Section == "power" && l.Text.StartsWith("停电", StringComparison.Ordinal));
            long awaySec = (r.EndTick - r.StartTick) / GameClock.StepHz;
            Expect(a.BrownAtDestroy > 0 && r.Outages.Length == 1 && o.StartTick == a.DestroyTick && o.EndTick == a.RestoreTick && o.Peak == a.BrownAtDestroy
                   && Math.Abs(r.ShortSeconds - outageSec) <= 1 && Math.Abs(r.PowerSeconds - awaySec) <= 1 && outLine != null && outLine.Action == AwayLineAction.Building && outLine.HasPos,
                $"B4 停电：{r.Outages.Length} 段，第 {o?.StartTick} 步起到第 {o?.EndTick} 步（摧毁 {a.DestroyTick} / 修好 {a.RestoreTick}），最多 {o?.Peak} 座停机（摧毁时 {a.BrownAtDestroy} 座）；" +
                $"缺电 {r.ShortSeconds} 秒 ≈ {outageSec} 秒；发电记了 {r.PowerSeconds} 秒 ≈ 离家 {awaySec} 秒；“{outLine?.Text}”可定位");
            // 机器变化：家园机器重伤、远征队阵亡；远征队 3 台回来 2 台。
            AwayMachineRecord dead = r.Machines.FirstOrDefault(m => m.LogicId == a.Killed);
            AwayMachineRecord hurt = r.Machines.FirstOrDefault(m => m.LogicId == a.HomeMachine);
            AwayLine team = lines.FirstOrDefault(l => l.Section == "machines" && l.Action == AwayLineAction.Roster);
            AwayLine deadLine = lines.FirstOrDefault(l => l.Section == "machines" && l.LogicId == a.Killed);
            AwayLine hurtLine = lines.FirstOrDefault(l => l.Section == "machines" && l.LogicId == a.HomeMachine);
            Expect(dead != null && dead.Died && dead.DiedTick == a.KillTick && dead.Expedition && hurt != null && !hurt.Died && !hurt.Expedition
                   && Mathf.Approximately(hurt.MinHealth, a.WoundedHealth) && team != null && team.Text.Contains("3 台") && team.Text.Contains("回来 2 台") && team.Text.Contains("损失 1 台")
                   && deadLine != null && deadLine.Text.Contains("阵亡") && deadLine.HasPos && hurtLine != null && hurtLine.Text.Contains("重伤") && hurtLine.Action == AwayLineAction.Machine,
                $"B5 机器变化：“{team?.Text}”；“{deadLine?.Text}”（阵亡步 {dead?.DiedTick} = {a.KillTick}）；“{hurtLine?.Text}”（最低耐久 {F(hurt?.MinHealth ?? -1)} = {F(a.WoundedHealth)}）");
            // 事件：建筑被摧毁（两台发电机）、突袭到达（带位置），常驻规则“战时预案”的动作。
            AwayEntryRecord[] destroyed = r.Entries.Where(e => e.TypeId == "building_destroyed").ToArray();
            AwayEntryRecord raid = r.Entries.FirstOrDefault(e => e.TypeId == "raid_arrival");
            int destroyedNotes = NoteCount("building_destroyed") - destroyedNotes0;
            bool raidPos = raid != null && raid.HasLocation && a.Raid != null && Mathf.Abs(raid.X - (float)a.Raid.TargetX) < 0.5f && Mathf.Abs(raid.Z - (float)a.Raid.TargetY) < 0.5f;
            bool rule = a.War != null && r.RulesTotal >= 1 && r.Rules.Any(e => e.Key == "rules.log.war_on" && e.Rule == a.War.Serial);
            // FG6-DEF-08 起突袭段先列这一波的结算（标题 / 过程 / 损失 / 贡献，点开看完整结算），通知转来的“突袭到达”条目在后面：按位置找那一条。
            AwayLine raidLine = lines.FirstOrDefault(l => l.Section == "raid" && l.IsEntry && l.Action == AwayLineAction.Locate && a.Raid != null
                                                         && Mathf.Abs(l.Pos.x - (float)a.Raid.TargetX) < 0.5f && Mathf.Abs(l.Pos.z - (float)a.Raid.TargetY) < 0.5f);
            bool settlement = lines.Any(l => l.Section == "raid" && l.Action == AwayLineAction.RaidResult);
            AwayLine ruleLine = lines.FirstOrDefault(l => l.Section == "rules" && l.IsEntry && l.Text.Contains("R" + a.War?.Serial));
            Expect(destroyed.Length == destroyedNotes && destroyed.Length >= 1 && destroyed.All(e => e.Section == "buildings" && e.HasLocation) && raidPos && rule
                   && raidLine != null && raidLine.Action == AwayLineAction.Locate && ruleLine != null && ruleLine.Action == AwayLineAction.Rules && settlement,
                $"B6 事件：建筑被摧毁 {destroyed.Length} 条 = 离家期间的“建筑被摧毁”通知 {destroyedNotes} 条（带位置）；突袭到达带位置（{raidPos}）“{raidLine?.Text}”，突袭段先列这一波的结算（{settlement}）；" +
                $"常驻规则“战时预案”的动作进报告（{rule}）“{ruleLine?.Text}”");
            // 每一条都能点击：条目都有动作；标题与空状态不是条目；全部文字没有未解析的文本键。
            bool everyEntry = lines.All(l => l.IsEntry || l.Cls == "ar-row-title" || l.Cls == "ar-row-dim");
            bool locatable = lines.Where(l => l.IsEntry).All(l => l.HasPos || l.Action != AwayLineAction.Locate);
            string all = AwayReportView.Summary(now, r) + Text(lines);
            Expect(everyEntry && locatable && !GameText.ContainsMarker(all) && all.Contains("撤离回家") && lines.Any(l => l.Section == "weather" && l.Cls == "ar-row-dim"),
                $"B7 报告每一条都能点击（定位或打开对应面板）；没有记录的分段写空状态（天气：“{lines.FirstOrDefault(l => l.Section == "weather" && l.Cls == "ar-row-dim")?.Text}”）；概要“{AwayReportView.Summary(now, r)}”");
            CheckPanel(now, r, a);
            // B5b（修复轮，审查 P1）：已结算的报告是历史记录——之后活着回来的一台再阵亡、受伤的家园机器被修好，旧报告的队伍行与伤势行都不变。
            CampaignState later = CampaignSession.Current;
            AwayReportRecord settled = AwayReportService.Recent(later).FirstOrDefault(x => x.Serial == r.Serial);
            int survivor = a.Roster.FirstOrDefault(id => id != a.Killed && MachineRegistry.TryGetRecord(id, out MachineRecord sr) && sr.IsAlive);
            string teamBefore = team?.Text;
            string hurtBefore = hurtLine?.Text;
            if (survivor > 0)
            {
                MachineRegistry.ApplyDamage(survivor, 1e6f);
            }
            if (MachineRegistry.TryGetRecord(a.HomeMachine, out MachineRecord homeM) && homeM.IsAlive)
            {
                homeM.Health = homeM.MaxHealth; // 测试捷径：修好（维修由 FG4-ECO-06 / 07 覆盖）
            }
            WorldSimulation.StepMany(1);
            List<AwayLine> afterLines = Lines(later, settled);
            AwayLine teamAfter = afterLines.FirstOrDefault(l => l.Section == "machines" && l.Action == AwayLineAction.Roster);
            AwayLine hurtAfter = afterLines.FirstOrDefault(l => l.Section == "machines" && l.LogicId == a.HomeMachine);
            bool survivorDead = survivor > 0 && MachineRegistry.TryGetRecord(survivor, out MachineRecord sd) && !sd.IsAlive;
            Expect(settled != null && settled.TeamSettled && settled.MembersLost == 1 && survivorDead && teamAfter?.Text == teamBefore && hurtAfter?.Text == hurtBefore
                   && AwayReportService.MembersLost(settled) == 1,
                $"B5b 历史报告不被之后的事改写：结算时固化“损失 {settled?.MembersLost} 台”；之后回来的第 {survivor} 号再阵亡（{survivorDead}）、家园那台被修好，" +
                $"旧报告仍是“{teamAfter?.Text}”“{hurtAfter?.Text}”（结算时：“{teamBefore}”“{hurtBefore}”）");
        }

        /// <summary>F 面板（真 UXML）：Alt+H 打开最新一份；每一条都能点；定位成功收起面板并打开对应面板；定位失败不收起、写明原因；布局探针。</summary>
        private static void CheckPanel(CampaignState s, AwayReportRecord r, Away a)
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out GameObject go);
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            AwayReportPanelUIToolkit.Clock = () => _fakeNow;
            var located = new List<(string Region, Vector3 Pos)>();
            bool locateOk = true;
            NotificationCenter.LocateHandler = (string region, Vector3 pos, out string failureKey) =>
            {
                failureKey = locateOk ? null : "ui.notify.no_camera";
                located.Add((region, pos));
                return locateOk;
            };
            try
            {
                AwayReportPanelUIToolkit panel = go.AddComponent<AwayReportPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                var keys = new Keys();
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                keys.Held.Add(KeyCode.LeftAlt);
                keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.OpenAwayReport);
                UiKitInputPump.ProcessLibraryKeys();
                keys.Down = KeyCode.None;
                keys.Held.Clear();
                InputRouter.DebugClearConsumedKeys();
                panel.Refresh();
                bool opened = AwayReportPanelUIToolkit.IsOpen && panel.PanelVisible && panel.ShownSerial == r.Serial && panel.VisibleRowCount > 5
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.AwayReportFirstOpen);
                int entries = 0, enabledMismatch = 0;
                for (int i = 0; i < panel.VisibleRowCount; i++)
                {
                    AwayLine l = panel.Line(i);
                    Button b = panel.RowButton(i);
                    entries += l.IsEntry ? 1 : 0;
                    enabledMismatch += b.enabledSelf != l.IsEntry ? 1 : 0;
                }
                string summary = panel.SummaryText;
                Expect(opened && entries > 5 && enabledMismatch == 0 && summary.Contains("撤离回家") && panel.RowText(0).Length > 0,
                    $"F1 Alt+H 打开离家报告：显示最新一份（第 {panel.ShownSerial} 份，{panel.VisibleRowCount} 行，其中 {entries} 条可点击；标题 / 空状态不可点）；概要“{summary}”");
                // 点生产行：收起面板 → 统计面板生产页选中这种物品。
                int prodRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.Line(i).Action == AwayLineAction.StatsItem && panel.Line(i).Arg == "alloy");
                bool clickedProd = panel.Line(prodRow)?.Arg == "alloy" && Click(panel.RowButton(prodRow)) && !AwayReportPanelUIToolkit.IsOpen && panel.LastClicked?.Arg == "alloy";
                // 点瓶颈行：镜头定位到电子组装台、打开建筑面板。
                AwayReportPanelUIToolkit.Open();
                panel.Refresh();
                int bnRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.Line(i).Section == "bottleneck" && panel.Line(i).IsEntry);
                located.Clear();
                bool clickedBn = Click(panel.RowButton(bnRow)) && !AwayReportPanelUIToolkit.IsOpen && located.Count == 1 && located[0].Region == HomeValleyLayout.RegionId
                                 && Mathf.Approximately(located[0].Pos.x, a.Bench.Position.x) && Mathf.Approximately(located[0].Pos.z, a.Bench.Position.y);
                // 点阵亡行：定位到阵亡的地点，打开名册里这台机器的详情。
                AwayReportPanelUIToolkit.Open();
                panel.Refresh();
                int deadRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.Line(i).LogicId == a.Killed && panel.Line(i).Action == AwayLineAction.Machine);
                located.Clear();
                bool clickedDead = panel.Line(deadRow)?.LogicId == a.Killed && Click(panel.RowButton(deadRow)) && located.Count == 1 && located[0].Region == FracturedCityLayout.RegionId;
                RosterPanelUIToolkit.Close();
                // 定位失败（不在那个地点 / 没有镜头）：面板不收，写明原因（不静默）。
                AwayReportPanelUIToolkit.Open();
                panel.Refresh();
                locateOk = false;
                int raidRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.Line(i).Section == "raid" && panel.Line(i).Action == AwayLineAction.Locate);
                bool failShown = panel.Line(raidRow)?.Action == AwayLineAction.Locate && !panel.Click(raidRow) && AwayReportPanelUIToolkit.IsOpen
                                 && panel.MessageText.Contains("无法定位") && !GameText.ContainsMarker(panel.MessageText);
                locateOk = true;
                Expect(clickedProd && clickedBn && clickedDead && failShown,
                    $"F2 点击定位：生产行 → 统计面板该物品（{clickedProd}）；瓶颈行 → 镜头飞到电子组装台并打开建筑面板（{clickedBn}）；阵亡行 → 飞到远征地点阵亡处并打开名册详情（{clickedDead}）；" +
                    $"定位失败时面板不收起、写明“{panel.MessageText}”（{failShown}）");
                AwayReportPanelUIToolkit.Close();
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "AwayReportPanel.uxml", "AwayReportWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("AwayReportRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "F3 离家报告面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                AwayReportPanelUIToolkit.Close();
                RosterPanelUIToolkit.Close();
                ProductionPanelUIToolkit.Close();
                StatsPanelUIToolkit.Close();
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>B8 全灭放弃：远征队全部阵亡 → 放弃远征 → 报告结果“全灭”，远征队全部记为损失。</summary>
        private static void CheckWipeReport()
        {
            Away a = LayAndDepart(9102, observeHome: false);
            if (a.Failure != null)
            {
                Fail("B8 场景搭不起来：" + a.Failure);
                return;
            }
            foreach (int id in a.Roster)
            {
                MachineRegistry.ApplyDamage(id, 1e6f);
            }
            bool wiped = FgProductionSelfCheck.StepUntil(() => WorldSimulation.FracturedCity != null && WorldSimulation.FracturedCity.IsWiped, 10);
            ExpeditionReturnService.ReturnResult abandon = ExpeditionReturnService.TryConfirmAbandon();
            CampaignState s = CampaignSession.Current;
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            List<AwayLine> lines = Lines(s, r);
            AwayLine team = lines.FirstOrDefault(l => l.Section == "machines" && l.Action == AwayLineAction.Roster);
            Expect(wiped && abandon.Success && r != null && r.Outcome == AwayReportService.OutcomeWiped && r.Machines.Count(m => m.Died && m.Expedition) == a.Roster.Length
                   && team != null && team.Text.Contains("损失 " + a.Roster.Length + " 台") && AwayReportView.Summary(s, r).Contains("全灭"),
                $"B8 全灭放弃：报告结果“全灭”（{r?.Outcome}），远征队 {a.Roster.Length} 台全部记为阵亡；“{team?.Text}”");
        }

        // ── C 空报告与出发被拦 ─────────────────────────────────────────────────────

        private static void CheckEmptyAndBlocked()
        {
            CampaignState s = NewWorld(9111);
            int[] roster = PrepareDeparture(s);
            // 负向：地点还锁着 → 出发被拦 → 不开报告。
            RegionRecord ruins = FracturedCityRegion.Find(s);
            RegionState was = ruins?.State ?? RegionState.Available;
            if (ruins != null)
            {
                ruins.State = RegionState.Locked;
            }
            ExpeditionDepartureService.DepartureResult blocked = ExpeditionDepartureService.TryDepart(roster, true);
            bool noReport = blocked.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && !AwayReportService.IsOpen(s) && AwayReportService.Recent(s).Count == 0;
            if (ruins != null)
            {
                ruins.State = was;
            }
            Expect(noReport, $"C1 负向：出发被拦（{string.Join("，", blocked.Reasons ?? Array.Empty<string>())}）→ 不开离家报告");
            // 家园没有产线、没有事：出发后马上撤离。
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            WorldSimulation.StepMany(GameClock.StepHz / 2);
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            CampaignState now = CampaignSession.Current;
            AwayReportRecord r = AwayReportService.Recent(now).FirstOrDefault();
            var lines = new List<AwayLine>();
            bool empty = r != null && AwayReportView.Build(now, r, lines);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out GameObject go);
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                AwayReportPanelUIToolkit panel = go.AddComponent<AwayReportPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                AwayReportPanelUIToolkit.Open();
                panel.Refresh();
                bool shown = panel.ShowingEmptyReport && panel.VisibleRowCount == 1 && panel.RowText(0).Contains("一切照旧") && !panel.RowButton(0).enabledSelf
                             && panel.SummaryText.Contains("撤离回家");
                Expect(dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Success && ret.Success && empty && lines.Count == 1 && shown,
                    $"C2 负向（卡片“离家期间什么都没发生时的空报告”）：报告照常生成，只写一行“{lines.FirstOrDefault()?.Text}”，概要照常；面板显示同一行（不可点）");
                AwayReportPanelUIToolkit.Close();
            }
            finally
            {
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
            // C2b（修复轮，审查 P1）：空报告结算后，名单里的一台之后阵亡 → 这份报告仍是“一切照旧”（不会展开成整份、也不会多出“损失 1 台”）。
            int victim = r?.Members.FirstOrDefault(id => MachineRegistry.TryGetRecord(id, out MachineRecord vm) && vm.IsAlive) ?? 0;
            if (victim > 0)
            {
                MachineRegistry.ApplyDamage(victim, 1e6f);
            }
            var laterLines = new List<AwayLine>();
            bool stillEmpty = r != null && AwayReportView.Build(CampaignSession.Current, r, laterLines) && laterLines.Count == 1 && laterLines[0].Text.Contains("一切照旧");
            Expect(victim > 0 && r.TeamSettled && r.MembersLost == 0 && stillEmpty,
                $"C2b 负向（历史报告）：空报告结算后名单里的第 {victim} 号阵亡，这份报告仍只写“{laterLines.FirstOrDefault()?.Text}”（固化损失 {r?.MembersLost} 台）");
            // 从来没有报告：面板写明去处。
            CampaignState blank = NewWorld(9112);
            VisualElement root2 = FgProductionSelfCheck.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out GameObject go2);
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                AwayReportPanelUIToolkit panel = go2.AddComponent<AwayReportPanelUIToolkit>();
                panel.BindView(root2);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                AwayReportPanelUIToolkit.Open();
                panel.Refresh();
                Expect(blank != null && panel.EmptyText.Contains("还没有离家报告") && panel.VisibleRowCount == 0 && !panel.PickField.enabledSelf,
                    $"C3 空状态：还没有任何离家报告时写“{panel.EmptyText}”");
                AwayReportPanelUIToolkit.Close();
            }
            finally
            {
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go2);
            }
        }

        // ── D 最近 3 份与条目上限 ──────────────────────────────────────────────────

        private static void CheckRecentAndCaps()
        {
            CampaignState s = NewWorld(9121);
            var serials = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                AwayReportRecord r = AwayReportService.Begin(s, FracturedCityLayout.RegionId, new[] { 1 });
                AwayReportService.End(s, AwayReportService.OutcomeEvacuated, notify: false);
                serials.Add(r.Serial);
            }
            List<AwayReportRecord> recent = AwayReportService.Recent(s);
            bool keep3 = recent.Count == 3 && recent.Select(x => x.Serial).SequenceEqual(serials.Skip(1).Reverse()) && s.Stats.AwayReports.NextSerial == serials.Last() + 1;
            // 条目上限：超过 away.entries_max 的只计数，报告末尾写“另有 N 条”。
            int max = AwayReportService.EntriesMax;
            AwayReportService.Begin(s, FracturedCityLayout.RegionId, Array.Empty<int>());
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            for (int i = 0; i < max + 7; i++)
            {
                NotificationCenter.Post("building_damaged", "building.core.name", new Vector3(core.Position.x, 0f, core.Position.y));
            }
            AwayReportRecord cur = AwayReportService.Current(s);
            List<AwayLine> lines = Lines(s, cur);
            bool capped = cur.Entries.Length == max && cur.EntriesDropped == 7 && lines.Any(l => l.Text.Contains("另有 7 条"));
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
            Expect(keep3 && capped,
                $"D 最近 {AwayReportService.ReportsKeep} 份：连续 4 次远征后保留第 {string.Join("、", recent.Select(x => x.Serial))} 份（最新在前，最旧的丢掉）；" +
                $"条目超过上限 {max} 条时只计数、报告写“另有 7 条”（{capped}）");
        }

        // ── E 自动打开与设置 ──────────────────────────────────────────────────────

        private static void CheckAutoOpenAndSetting()
        {
            CampaignState s = NewWorld(9131);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out GameObject go);
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                AwayReportPanelUIToolkit panel = go.AddComponent<AwayReportPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                GameSettings.SetAwayReportAutoOpen(true);
                AwayReportService.Begin(s, FracturedCityLayout.RegionId, Array.Empty<int>());
                AwayReportRecord r1 = AwayReportService.End(s, AwayReportService.OutcomeEvacuated);
                bool pending = AwayReportPanelUIToolkit.AutoOpenPending;
                bool autoOpened = panel.TryAutoOpenNow() && AwayReportPanelUIToolkit.IsOpen && panel.ShownSerial == r1.Serial && panel.AutoOpenCount == 1
                                  && GameSettings.HasSeenGuidanceHook(GuidanceHooks.AwayFirstAutoOpen);
                // 面板里的开关：关掉 → 设置变了；再结算一份只发通知、不打开。
                panel.AutoOpenToggle.value = false;
                bool offSaved = !GameSettings.AwayReportAutoOpen;
                AwayReportPanelUIToolkit.Close();
                int notes0 = NoteCount("away_report");
                AwayReportService.Begin(s, FracturedCityLayout.RegionId, Array.Empty<int>());
                AwayReportRecord r2 = AwayReportService.End(s, AwayReportService.OutcomeEvacuated);
                bool notOpened = !AwayReportPanelUIToolkit.AutoOpenPending && !panel.TryAutoOpenNow() && !AwayReportPanelUIToolkit.IsOpen && NoteCount("away_report") == notes0 + 1;
                // 暂停菜单里的同一个开关（真 UXML）。
                VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
                bool pauseToggle;
                bool pauseButton;
                try
                {
                    PauseMenuUIToolkit pm = pgo.AddComponent<PauseMenuUIToolkit>();
                    pm.BindView(proot);
                    pm.SyncReactionToggles();
                    bool showsOff = pm.AwayAutoOpenToggle != null && !pm.AwayAutoOpenToggle.value && pm.AwayAutoOpenToggle.label.Contains("离家报告");
                    pm.AwayAutoOpenToggle.value = true;
                    pauseToggle = showsOff && GameSettings.AwayReportAutoOpen;
                    pauseButton = pm.AwayReportButton != null && pm.AwayReportButton.text.Contains("离家报告") && Click(pm.AwayReportButton) && AwayReportPanelUIToolkit.IsOpen;
                    AwayReportPanelUIToolkit.Close();
                }
                finally
                {
                    PauseMenuUIToolkit.Close();
                    Object.DestroyImmediate(pgo);
                }
                // E2（修复轮，审查 P2）：关掉自动打开后的那条“离家报告”通知写明去处（暂停菜单按钮 + 当前快捷键），点它（弹出条 / 通知中心“打开”）直接打开离家报告。
                NotificationEntry note = Notes("away_report").OrderByDescending(e => e.Id).FirstOrDefault();
                string noteText = note?.Latest?.DetailText ?? string.Empty;
                string keyText = InputDisplay.ForAction(GameActionId.OpenAwayReport);
                bool noteWhere = noteText.Contains("暂停菜单") && noteText.Contains(keyText) && !GameText.ContainsMarker(noteText) && note != null && !note.HasAnyLocation
                                 && NotificationCenter.HasOpenHandler(note);
                VisualElement hroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "NotificationHud.uxml", out GameObject hgo);
                bool noteOpens;
                try
                {
                    var hud = hgo.AddComponent<NotificationHudUIToolkit>();
                    hud.BindView(hroot);
                    AwayReportPanelUIToolkit.Close();
                    hud.OnToastClicked(note);
                    noteOpens = AwayReportPanelUIToolkit.IsOpen && !NotificationHudUIToolkit.CenterOpen;
                    AwayReportPanelUIToolkit.Close();
                    hud.SetCenterOpen(false);
                }
                finally
                {
                    Object.DestroyImmediate(hgo);
                }
                Expect(noteWhere && noteOpens,
                    $"E2 “离家报告”通知写明去处：“{noteText}”（快捷键 {keyText}）；点这条通知直接打开离家报告（{noteOpens}），不再只是打开通知中心");
                Expect(pending && autoOpened && offSaved && notOpened && pauseToggle && pauseButton && r2 != null,
                    $"E 自动打开：设置开时报告结算 → 待打开（{pending}）→ 面板（在家园、没有别的面板挡着时）打开这一份（{autoOpened}）；面板开关关掉存进设置（{offSaved}）；面板里关掉开关后再结算只发“离家报告”通知、不打开（{notOpened}）；" +
                    $"暂停菜单里同一个开关显示当前值、改了生效（{pauseToggle}），暂停菜单“离家报告”按钮打开面板（{pauseButton}）");
            }
            finally
            {
                GameSettings.SetAwayReportAutoOpen(true);
                AwayReportPanelUIToolkit.Close();
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── G FGT-ECO-009 告警六级 ────────────────────────────────────────────────

        private static bool Has(List<HomeValleyAlarms.AlertRecord> list, HomeValleyAlarms.Severity sev) => list.Any(x => x.Severity == sev);

        private static void CheckAlarmsSixLevels()
        {
            CampaignState s = NewWorld(9141);
            List<HomeValleyAlarms.AlertRecord> base0 = HomeValleyAlarms.Collect(s);
            bool clean = !Has(base0, HomeValleyAlarms.Severity.CoreThreatened) && !Has(base0, HomeValleyAlarms.Severity.PowerCritical)
                         && !Has(base0, HomeValleyAlarms.Severity.FactoryJammed) && !Has(base0, HomeValleyAlarms.Severity.MachineWounded);
            // 工厂堵塞：缺稀土矿的电子组装台；输出没人取的精炼炉（输出堵塞）。不到 alarm.jam_seconds 不告警。
            BuildingRecord bench = Place(s, "electronics_bench");
            BuildingRecord furnace = Place(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, bench.BuildingId, "electronic", out _);
            ProductionService.TrySetRecipe(s, furnace.BuildingId, "alloy_scrap", out _);
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } };
            P(s, furnace).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
            int starved0 = NoteCount("input_starved");
            int blocked0 = NoteCount("output_blocked");
            float jam = HomeValleyAlarms.JamSeconds;
            FgProductionSelfCheck.Seconds(jam - 5f);
            bool early = !Has(HomeValleyAlarms.Collect(s), HomeValleyAlarms.Severity.FactoryJammed) && NoteCount("input_starved") == starved0;
            FgProductionSelfCheck.Seconds(10f);
            List<HomeValleyAlarms.AlertRecord> jams = HomeValleyAlarms.Collect(s).Where(x => x.Severity == HomeValleyAlarms.Severity.FactoryJammed).ToList();
            HomeValleyAlarms.AlertRecord benchAlert = jams.FirstOrDefault(x => x.BuildingId == bench.BuildingId);
            bool starvedAlert = benchAlert.Key != null && benchAlert.Message.Contains("缺") && benchAlert.Message.Contains(ItemCatalog.NameOf("rare_earth_ore")) && benchAlert.HasLocation
                                && NoteCount("input_starved") == starved0 + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.AlarmFirstFactoryJam);
            // 精炼炉的输出堵塞要等输出缓存满：再走一段（满了以后计时）。
            bool blockedAlert = FgProductionSelfCheck.StepUntil(() => HomeValleyAlarms.Collect(s).Any(x => x.Severity == HomeValleyAlarms.Severity.FactoryJammed && x.BuildingId == furnace.BuildingId), 400)
                                && NoteCount("output_blocked") == blocked0 + 1;
            FgProductionSelfCheck.Seconds(30f);
            bool once = NoteCount("input_starved") == starved0 + 1 && NoteCount("output_blocked") == blocked0 + 1;
            // 恢复：给组装台稀土矿 → 不再缺料 → 告警消失；再断料 → 新的一段，到时再告一次。
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 50 }, new ItemStackRecord { ItemId = "rare_earth_ore", Amount = 50 } };
            FgProductionSelfCheck.Seconds(3f);
            bool recovered = !HomeValleyAlarms.Collect(s).Any(x => x.BuildingId == bench.BuildingId);
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } };
            FgProductionSelfCheck.Seconds(jam + 15f);
            bool again = HomeValleyAlarms.Collect(s).Any(x => x.BuildingId == bench.BuildingId) && NoteCount("input_starved") == starved0 + 2;
            Expect(clean && early && starvedAlert && blockedAlert && once && recovered && again,
                $"G1 工厂堵塞（新增“持续缺料 / 输出持续堵塞”）：不到 {F(jam)} 秒不告警（{early}）；超过后组装台“{benchAlert.Message}”带定位、发一次警告与钩子（{starvedAlert}）；" +
                $"精炼炉输出没人取 → 输出持续堵塞（{blockedAlert}）；同一段只告一次（{once}）；恢复后消失（{recovered}），再卡住到时再告一次（{again}）");
            P(s, furnace).Rec.Out = Array.Empty<ItemStackRecord>();
            P(s, furnace).Rec.In = Array.Empty<ItemStackRecord>();
            P(s, bench).Rec.In = Array.Empty<ItemStackRecord>();
            // G1b（修复轮，审查 P2，B08 聚合）：再放两座同样缺稀土矿的组装台 → “持续缺料”只占一条（N 座、最久那座），点它定位到缺得最久的组装台。
            BuildingRecord bench2 = Place(s, "electronics_bench");
            BuildingRecord bench3 = Place(s, "electronics_bench");
            if (bench2 != null && bench3 != null)
            {
                ProductionService.TrySetRecipe(s, bench2.BuildingId, "electronic", out _);
                ProductionService.TrySetRecipe(s, bench3.BuildingId, "electronic", out _);
                P(s, bench2).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } };
                P(s, bench3).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } };
            }
            FgProductionSelfCheck.Seconds(jam + 5f);
            List<HomeValleyAlarms.AlertRecord> starvedRows = HomeValleyAlarms.Collect(s)
                .Where(x => x.Severity == HomeValleyAlarms.Severity.FactoryJammed && (x.Key == "jam:~starved" || x.Message.Contains("持续缺"))).ToList();
            HomeValleyAlarms.AlertRecord agg = starvedRows.FirstOrDefault();
            bool aggregated = bench2 != null && bench3 != null && starvedRows.Count == 1 && agg.Key == "jam:~starved" && agg.Message.Contains("座建筑持续缺料") && agg.Message.Contains("最久")
                              && agg.BuildingId == bench.BuildingId && agg.HasLocation && Mathf.Approximately(agg.Position.x, bench.Position.x)
                              && P(s, bench2).Rec.JamAlerted && P(s, bench3).Rec.JamAlerted;
            Expect(aggregated,
                $"G1b 工厂级聚合：3 座组装台同时持续缺料，告警栏只占 {starvedRows.Count} 条“{agg.Message}”，定位到缺得最久的那座（{agg.BuildingId} = {bench.BuildingId}）；每座仍各自计时、各自告过警");

            // 机器重伤：跌破阈值那一下发一次；再挨打不重复；修好消失；阵亡移出（另有紧急通知）。
            MachineRecord m = MachineRegistry.AllRecords.First(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId);
            int wounded0 = NoteCount("machine_wounded");
            float limit = m.MaxHealth * HomeValleyAlarms.WoundedFraction;
            MachineRegistry.ApplyDamage(m.LogicId, m.Health - limit * 0.9f);
            HomeValleyAlarms.AlertRecord mAlert = HomeValleyAlarms.Collect(s).FirstOrDefault(x => x.Severity == HomeValleyAlarms.Severity.MachineWounded && x.MachineLogicId == m.LogicId);
            MachineRegistry.ApplyDamage(m.LogicId, 1f);
            bool woundedOnce = mAlert.Key != null && mAlert.HasLocation && NoteCount("machine_wounded") == wounded0 + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.AlarmFirstMachineWounded);
            m.Health = m.MaxHealth; // 测试捷径：修好（维修台 / 送修由 FG4-ECO-06 / 07 覆盖）
            bool healed = !HomeValleyAlarms.Collect(s).Any(x => x.MachineLogicId == m.LogicId && x.Severity == HomeValleyAlarms.Severity.MachineWounded);
            MachineRegistry.ApplyDamage(m.LogicId, m.Health - limit * 0.5f);
            bool againWounded = NoteCount("machine_wounded") == wounded0 + 2 && HomeValleyAlarms.Collect(s).Any(x => x.MachineLogicId == m.LogicId);
            int destroyed0 = NoteCount("machine_destroyed");
            MachineRegistry.ApplyDamage(m.LogicId, 1e6f);
            bool deadGone = !HomeValleyAlarms.Collect(s).Any(x => x.MachineLogicId == m.LogicId) && NoteCount("machine_destroyed") == destroyed0 + 1;
            Expect(woundedOnce && healed && againWounded && deadGone,
                $"G2 机器重伤（耐久 ≤ {HomeValleyAlarms.WoundedFraction:P0}）：“{mAlert.Message}”带定位，跌破那一下发一次警告、再挨打不重复（{woundedOnce}）；修好后消失（{healed}）；再次重伤再告（{againWounded}）；阵亡后移出、另发紧急通知（{deadGone}）");

            // 核心受威胁（核心受损、突袭到达）、电力、仓满、工作受阻——六级同时存在时按威胁排序，每条都能定位。
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            BuildingOps.ApplyDamage(s, core.BuildingId, 30f);
            FgProductionSelfCheck.Seconds(1f);
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } };
            FgProductionSelfCheck.Seconds(jam + 5f);
            // 突袭到达放在缺料计时之后：到达的部队停留 1 游戏小时后按时间上限撤退（FG6-DEF-04 / FGR-DEF-032），
            // 要在停留期内收集告警。
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out string raidFail);
            if (raid != null)
            {
                raid.PosX = raid.TargetX;
                raid.PosY = raid.TargetY;
            }
            FgProductionSelfCheck.Seconds(1f);
            BuildingRecord gen2 = s.BuildingRecords.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
            BuildingRecord gen1 = s.BuildingRecords.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (gen2 != null)
            {
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, gen2.BuildingId);
            }
            if (gen1 != null)
            {
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, gen1.BuildingId);
            }
            HomeInventory.Add(s, "alloy", 1000000, clampToSpace: true);
            MachineRecord w = MachineRegistry.AllRecords.First(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId);
            MachineRegistry.ApplyDamage(w.LogicId, w.Health - w.MaxHealth * 0.2f);
            // 工作受阻：正式来源是派工途中路径卡住（HomeValleyWorkOrders 写 path-blocked，ER3-WRK-02 自检覆盖）；这里登记一张这样的等待单。
            var blockedOrder = new WorkOrderRecord
            {
                WorkOrderId = "fgaway-blocked", Kind = WorkOrderKind.Haul, State = WorkOrderState.Waiting, FailureReason = HomeValleyWorkOrders.PathBlockedReason,
                AssignedMachineLogicId = w.LogicId,
            };
            s.WorkOrders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Append(blockedOrder).ToArray();
            List<HomeValleyAlarms.AlertRecord> all = HomeValleyAlarms.Collect(s);
            var sevs = Enum.GetValues(typeof(HomeValleyAlarms.Severity)).Cast<HomeValleyAlarms.Severity>().ToArray();
            bool allSix = sevs.All(v => Has(all, v));
            bool sorted = all.Select(x => (int)x.Severity).SequenceEqual(all.Select(x => (int)x.Severity).OrderBy(x => x));
            bool coreBoth = all.Any(x => x.Severity == HomeValleyAlarms.Severity.CoreThreatened && x.BuildingId == core.BuildingId)
                            && all.Any(x => x.Severity == HomeValleyAlarms.Severity.CoreThreatened && x.Key.StartsWith("raid:", StringComparison.Ordinal));
            var located = new List<Vector3>();
            NotificationCenter.LocateHandler = (string region, Vector3 pos, out string failureKey) =>
            {
                failureKey = null;
                located.Add(pos);
                return true;
            };
            bool everyLocates = all.Where(x => x.Severity != HomeValleyAlarms.Severity.WorkBlocked || x.HasLocation).All(x => x.HasLocation && HomeValleyAlarms.Locate(x, out _));
            bool rowsText = all.All(x => x.RowText.StartsWith("[", StringComparison.Ordinal) && !GameText.ContainsMarker(x.RowText));
            // G5（修复轮，审查 P2）：告警栏只有 5 行时每个等级至少露出一条，其余写“另有 N 条”；不会被同一等级挤掉。
            var synth = new List<HomeValleyAlarms.AlertRecord>();
            for (int i = 0; i < 6; i++)
            {
                synth.Add(new HomeValleyAlarms.AlertRecord("jam:t" + i, HomeValleyAlarms.Severity.FactoryJammed, "t" + i, 0, "b" + i, Vector3.zero));
            }
            synth.Add(new HomeValleyAlarms.AlertRecord("machine:t", HomeValleyAlarms.Severity.MachineWounded, "m", 1, null, Vector3.zero));
            synth.Add(new HomeValleyAlarms.AlertRecord("work:t", HomeValleyAlarms.Severity.WorkBlocked, "w", 0));
            List<HomeValleyAlarms.AlertRecord> shown = HomeValleyAlarms.PickForDisplay(synth, 5, out int hiddenRows);
            bool picked = shown.Count == 5 && hiddenRows == 3 && Has(shown, HomeValleyAlarms.Severity.MachineWounded) && Has(shown, HomeValleyAlarms.Severity.WorkBlocked)
                          && shown.Select(x => (int)x.Severity).SequenceEqual(shown.Select(x => (int)x.Severity).OrderBy(x => x));
            List<HomeValleyAlarms.AlertRecord> realShown = HomeValleyAlarms.PickForDisplay(all, HomeValleyAlarms.DisplayRows, out int realHidden);
            bool realEveryLevel = HomeValleyAlarms.DisplayRows >= sevs.Length && sevs.All(v => Has(realShown, v)) && realHidden == Math.Max(0, all.Count - HomeValleyAlarms.DisplayRows);
            string moreText = GameText.Format("alarm.list.more", hiddenRows);
            Expect(picked && realEveryLevel && moreText.Contains("3") && !GameText.ContainsMarker(moreText),
                $"G5 告警栏 5 行：6 条工厂 + 1 条机器 + 1 条工作时显示 {string.Join("、", shown.Select(x => x.Key))}，另有 {hiddenRows} 条写“{moreText}”；本场景真实 {all.Count} 条在工单面板 {HomeValleyAlarms.DisplayRows} 行里每个等级都露出（{realEveryLevel}）");
            // G6（修复轮，审查 P2，B06）：点告警定位失败要写原因——没有位置、没有镜头各给一个文本键，文字与离家报告同一套。
            var noPos = new HomeValleyAlarms.AlertRecord("work:nopos", HomeValleyAlarms.Severity.WorkBlocked, "w", 0);
            bool noPosFail = !HomeValleyAlarms.Locate(noPos, out string noPosKey) && noPosKey == "ui.notify.no_location";
            NotificationCenter.LocateDelegate keepLocate = NotificationCenter.LocateHandler;
            NotificationCenter.LocateHandler = null;
            bool noCamFail = !HomeValleyAlarms.Locate(all.First(x => x.HasLocation), out string noCamKey) && noCamKey == "ui.notify.no_camera";
            NotificationCenter.LocateHandler = keepLocate;
            string failText = GameText.Format("away.panel.locate_failed", GameText.Get(noCamKey ?? string.Empty));
            string uxml = File.ReadAllText("Assets/GameRes/Raw/UI/WorkOrder/WorkOrderPanel.uxml");
            string uss = File.ReadAllText("Assets/GameRes/Raw/UI/WorkOrder/WorkOrderUI.uss");
            bool statusRow = uxml.Contains("name=\"AlertStatus\"") && uss.Contains(".wop-alert-status--hidden") && uss.Contains("display: none");
            Expect(noPosFail && noCamFail && statusRow && failText.Contains("无法定位") && !GameText.ContainsMarker(failText),
                $"G6 点告警定位失败不静默：没有位置 → {noPosKey}，没有镜头 → {noCamKey}，工单面板告警栏下方写“{failText}”（AlertStatus 行与隐藏类已在 UXML / USS：{statusRow}）");
            Expect(allSix && sorted && coreBoth && everyLocates && rowsText && raid != null,
                $"G3 FGT-ECO-009 六级都有真实数据源：{string.Join("；", sevs.Select(v => HomeValleyAlarms.LevelName(v) + " " + all.Count(x => x.Severity == v) + " 条"))}；按威胁排序（{sorted}）；" +
                $"核心受损与突袭到达都算“核心受威胁”（{coreBoth}{(raid == null ? "，派不出突袭：" + raidFail : string.Empty)}）；每条都能定位（{everyLocates}，{located.Count} 次）；首行“{all.FirstOrDefault().RowText}”");
            // 读档后：已告过警的卡住计时进存档，不再重复告警（H 段另测真文件；这里核对计时字段在记录上）。
            ProductionService.Producer pb = P(s, bench);
            Expect(pb.Rec.JamKind == HomeValleyAlarms.JamStarved && pb.Rec.JamAlerted && pb.Rec.JamSinceTick > 0,
                $"G4 卡住计时记在建筑记录上（种类 {pb.Rec.JamKind}、起始步 {pb.Rec.JamSinceTick}、已告警 {pb.Rec.JamAlerted}），随存档保存");
        }

        // ── H 存读档 ─────────────────────────────────────────────────────────────

        /// <summary>报告里只看家园的部分（远征地点里战斗的随机细节不在比较范围）：产出 / 消耗、缺料、停电、电力累计、条目、规则；外加生产建筑的卡住计时。</summary>
        private static string HomeSnap(CampaignState s)
        {
            AwayReportRecord r = AwayReportService.Current(s) ?? AwayReportService.Recent(s).FirstOrDefault();
            if (r == null)
            {
                return "（没有报告）";
            }
            var sb = new StringBuilder();
            sb.Append(r.Serial).Append('|').Append(r.StartTick).Append('|');
            foreach (ItemAmountRecord x in r.Produced.OrderBy(x => x.ItemId, StringComparer.Ordinal))
            {
                sb.Append('+').Append(x.ItemId).Append(':').Append(x.Amount);
            }
            foreach (ItemAmountRecord x in r.Consumed.OrderBy(x => x.ItemId, StringComparer.Ordinal))
            {
                sb.Append('-').Append(x.ItemId).Append(':').Append(x.Amount);
            }
            foreach (AwayStarveRecord x in AwayReportService.CollectStarve(s, r, clear: false))
            {
                sb.Append('~').Append(x.ItemId).Append('@').Append(x.BuildingId).Append(':').Append(x.Ticks);
            }
            foreach (AwayOutageRecord o in r.Outages)
            {
                sb.Append("|o").Append(o.StartTick).Append('-').Append(o.EndTick).Append('x').Append(o.Peak);
            }
            sb.Append("|p").Append(r.PowerSeconds).Append('/').Append(r.ShortSeconds).Append('/').Append(r.SupplySum.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (AwayEntryRecord e in r.Entries.Where(e => e.RegionId != FracturedCityLayout.RegionId))
            {
                sb.Append("|e").Append(e.TypeId).Append('@').Append(e.Tick);
            }
            sb.Append("|r").Append(r.RulesTotal);
            foreach (ProducerRecord p in s.Economy.Producers.OrderBy(p => p.BuildingId, StringComparer.Ordinal))
            {
                sb.Append("|j").Append(p.BuildingId).Append(':').Append(p.JamKind).Append('/').Append(p.JamSinceTick).Append('/').Append(p.JamAlerted);
            }
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            Away a = LayAndDepart(9151, observeHome: false);
            if (a.Failure != null)
            {
                Fail("H 场景搭不起来：" + a.Failure);
                return;
            }
            CampaignState s = a.S;
            RunTo(a.DepartTick + Ticks(30f), a.Furnace, s);
            WorldSimulation.RunAsSite(HomeValleyLayout.RegionId, () =>
            {
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, a.Gen2.BuildingId);
                HomeValleyPowerGrid.ApplyBuildingDestroyed(s, a.Gen1.BuildingId);
            });
            RunTo(a.DepartTick + Ticks(45f + HomeValleyAlarms.JamSeconds), a.Furnace, s); // 停电进行中、组装台已告过“持续缺料”
            string before = HomeSnap(s);
            string beforeFull = Snap(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long mark = GameClock.Ticks;
            RunTo(mark + Ticks(30f), a.Furnace, s);
            string continuous = HomeSnap(s);

            string RunFromSave(out string loaded, out string loadedFull, out bool stillOpen)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                loadedFull = null;
                stillOpen = false;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                // 与 GameRoot.ResumeCampaign 同一顺序：家园先载入，存档时在外的远征随后载入（中间没有世界步）。
                WorldSimulation.LoadHome(resume: true);
                int[] there = rr.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
                WorldSimulation.LoadFracturedCity(there, resume: true);
                loaded = HomeSnap(rr.State);
                loadedFull = Snap(rr.State);
                BuildingRecord furnace = HomeGridService.FindBuilding(rr.State, a.Furnace.BuildingId);
                RunTo(mark + Ticks(30f), furnace, rr.State);
                stillOpen = AwayReportService.IsOpen(rr.State);
                return HomeSnap(rr.State);
            }

            string first = RunFromSave(out string loaded1, out string loadedFull1, out bool open1);
            string second = RunFromSave(out _, out _, out _);
            Expect(save.Success && loaded1 == before && loadedFull1 == beforeFull && open1,
                "H1 真文件存读档（远征途中）：进行中的报告（产出 / 消耗、各建筑的离家缺料、停电进行中的一段、电力累计、条目、规则）与卡住计时写进存档，读档后逐字段一致、报告仍在进行"
                + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "H2 读档后接着跑 30 游戏秒，与不存档一直跑逐位一致（停电接着计、已告警的卡住不重复告警）；同一存档读两次结果一致"
                + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            // 旧存档：没有离家报告域 → 补空域；读档时正在远征却没有报告 → 从此刻开一份（不伪造之前的记录）。
            var old = new CampaignState();
            old.Stats = new StatsState { AwayReports = null };
            CampaignFgStateDomains.EnsureAll(old);
            CampaignState cur = CampaignSession.Current;
            // 修复轮（审查 P2）：远征地点里先有一台阵亡（RegionId 不变）——补开的报告名单不能把它算进去。
            MachineRecord fallen = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId);
            if (fallen != null)
            {
                MachineRegistry.ApplyDamage(fallen.LogicId, 1e6f);
            }
            cur.Stats.AwayReports = new AwayReportState();
            WorldSimulation.StepMany(1);
            AwayReportRecord fresh = AwayReportService.Current(cur);
            int[] inRuins = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId).Select(m => m.LogicId).OrderBy(x => x).ToArray();
            int deadInRuins = MachineRegistry.AllRecords.Count(m => m != null && !m.IsAlive && m.RegionId == FracturedCityLayout.RegionId);
            Expect(old.Stats.AwayReports != null && !old.Stats.AwayReports.HasOpen && old.Stats.AwayReports.Reports.Length == 0 && old.Stats.AwayReports.NextSerial == 1
                   && fresh != null && fresh.Members.OrderBy(x => x).SequenceEqual(inRuins) && deadInRuins >= 1 && !fresh.Members.Contains(fallen?.LogicId ?? -1) && fresh.Produced.Length == 0 && fresh.StartTick >= GameClock.Ticks - 1,
                $"H3 旧存档：没有离家报告域时补空域；读档时正在远征却没有报告 → 从此刻开一份（名单 = 远征地点里活着的 {inRuins.Length} 台机器，不含以前死在那里的 {deadInRuins} 台；没有伪造之前的产量）");
        }

        // ── I / J 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunAwayScenario(int seed, bool observeHome, float speed, bool pauseFirst, out bool pausedHeld)
        {
            pausedHeld = true;
            Away a = LayAndDepart(seed, observeHome);
            if (a.Failure != null)
            {
                return "场景失败：" + a.Failure;
            }
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snap(a.S);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snap(a.S) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            AwayEvents(a, withCombat: false);
            GameClock.SetSpeed(1f);
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            return ret.Success ? Snap(CampaignSession.Current) : "撤离失败：" + ret.FailureReason;
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunAwayScenario(9161, false, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && !reference.StartsWith("场景失败", StringComparison.Ordinal) && !reference.StartsWith("撤离失败", StringComparison.Ordinal) && reference.Contains("\"Outages\""),
                "I 暂停中（120 帧）离家报告不动；0.5x / 1x / 2x / 3x 跑同一段离家（生产、缺料、停电、修复）后撤离，报告逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string atExpedition = RunAwayScenario(9171, false, 1f, false, out _);
            string atHome = RunAwayScenario(9171, true, 1f, false, out _);
            Expect(atExpedition == atHome && !atHome.StartsWith("场景失败", StringComparison.Ordinal),
                "J 离家期间镜头在远征地点（家园没人看）与一直看着家园，撤离后的离家报告逐字段一致（FGR-BASE-021；B24 整个世界同时运行）"
                + (atExpedition == atHome ? string.Empty : $"\n看远征：{atExpedition}\n看家园：{atHome}"));
        }

        // ── K 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9181, scrap: 100);
            var producers = new List<BuildingRecord>();
            for (int i = 0; i < 45; i++)
            {
                BuildingRecord f = Place(s, i % 3 == 0 ? "electronics_bench" : "refinery_furnace");
                if (f == null)
                {
                    break;
                }
                if (f.BuildingTypeId == "refinery_furnace")
                {
                    ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
                    P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
                }
                else
                {
                    ProductionService.TrySetRecipe(s, f.BuildingId, "electronic", out _); // 缺料：每步记离家缺料与卡住计时
                }
                producers.Add(f);
            }
            for (int g = 0; g < 40 && HomeValleyPowerGrid.GetSummary(s).TotalDemand > HomeValleyPowerGrid.GetSummary(s).TotalSupply; g++)
            {
                if (Place(s, HomeValleyLayout.BuildingTypeGenerator2) == null)
                {
                    break;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            int[] roster = PrepareDeparture(s);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                Fail($"K 真实出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            s = CampaignSession.Current;
            FgProductionSelfCheck.Seconds(10f); // 预热（首次调用的 JIT 不算）
            long calls0 = AwayReportService.HookCalls;
            double ms0 = AwayReportService.HookMs;
            long scans0 = AwayReportService.CloseScans;
            int rebuilds0 = HomeValleyAlarms.WoundedRebuilds;
            ProductionService.ResetStepStats();
            for (float t = 0; t < 170f; t += 0.5f)
            {
                FgProductionSelfCheck.Seconds(0.5f);
                foreach (BuildingRecord b in producers)
                {
                    if (b.BuildingTypeId == "refinery_furnace")
                    {
                        P(s, b).Rec.Out = Array.Empty<ItemStackRecord>();
                    }
                }
            }
            long calls = AwayReportService.HookCalls - calls0;
            double perHookUs = (AwayReportService.HookMs - ms0) * 1000.0 / Math.Max(1, calls);
            double step = ProductionService.MaxStepMs;
            bool noScan = AwayReportService.CloseScans == scans0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
            {
                HomeValleyAlarms.Collect(s);
            }
            double collectMs = sw.Elapsed.TotalMilliseconds / 50.0;
            bool noRescan = HomeValleyAlarms.WoundedRebuilds - rebuilds0 <= 1;
            int jammed = HomeValleyAlarms.JammedCount;
            var lines = new List<AwayLine>();
            sw.Restart();
            for (int i = 0; i < 20; i++)
            {
                AwayReportView.Build(s, AwayReportService.Current(s), lines);
            }
            double viewMs = sw.Elapsed.TotalMilliseconds / 20.0;
            sw.Restart();
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
            double endMs = sw.Elapsed.TotalMilliseconds;
            Expect(producers.Count >= 30 && calls > 1000 && noScan && noRescan && jammed >= 10,
                $"K1 场景：{producers.Count} 座生产建筑（1/3 缺料）、报告进行中跑 3 游戏分钟，记账钩子 {calls} 次；期间没有逐建筑扫描（扫描只在结算 / 打开报告时，{noScan}）；" +
                $"告警汇总 50 次不重扫机器（{noRescan}），{jammed} 座在“工厂堵塞”");
            string perf = $"记账钩子每次 {perHookUs:F2} µs；生产步（含缺料、卡住计时、离家记账）最大 {step:F3} ms；告警汇总一次 {collectMs:F3} ms；组装报告一次 {viewMs:F3} ms；结算一次 {endMs:F3} ms";
            PerfGate.Expect(true,
                $"K2 性能（Editor batchmode；真机 HybridCLR 另测 FG15-SYS-02）：{perf}（阈值：钩子 5 µs、生产步 2 ms、告警汇总 0.5 ms、组装 2 ms、结算 5 ms）",
                new[]
                {
                    PerfGate.Le(perHookUs, 5.0, "钩子每次 µs"), PerfGate.Le(step, 2.0, "生产步最大 ms"), PerfGate.Le(collectMs, 0.5, "告警汇总 ms"),
                    PerfGate.Le(viewMs, 2.0, "组装报告 ms"), PerfGate.Le(endMs, 5.0, "结算 ms"),
                }, Expect, Line);
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public bool GetKey(KeyCode key) => key == Down || Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        /// <summary>与真实点击同一个回调（Clickable.Invoke）；按钮为空 / 没有 Clickable / 被禁用返回 false。</summary>
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
                InputRouter.SetModalUi(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                HomeValleyPowerGrid.OverrideNodesForTests(null);
                PowerEnvironment.ResetForTests();
                NotificationCenter.LocateHandler = null;
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
