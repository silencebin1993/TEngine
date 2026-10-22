using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
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
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG10-REG-01 沉淀湖领地模板与锚点（FG09 第 6 章、FG08 FGR-EXP-020、FG17 FGR-GEN-021～024；收口 DEBT-FG8QST01-03）：
    /// A 数据：澄净领地模板（危害区 / 据点 / 缓存 / 终端 / 遗迹 / 支线）、湖心平台锚点、防腐涂层蓝图与样品车任务（接车点 belt_outer）、涂层 / 软门槛 / 介绍的调参、文本与通知类型。
    /// B 世界（3 个种子）：澄净领地在规划层、外圈酸沼带宽约 100 格、领地里有据点 / 缓存 / 终端 / 支线、湖心平台锚点落在领地里；样品车接车点在酸沼带外、朝家园一侧。
    /// C 区域旅程片段（真实世界步进，星球战斗内核 + 正式小队命令入口）：
    ///   没有涂层时机器在酸沼带每秒掉 2%；命令进酸沼先被拒绝并说明蓝图去哪拿（负向），确认窗口内再下一次 = 强行进入，过了窗口重新拒绝；
    ///   拿到蓝图 → 下一个游戏秒写进内核并通知一次 → 机器只掉一成；再下令不拦；悬停 / 检查单写涂层状态；读档后派生量重写。
    /// D 第一次进入澄净领地发一条阵营介绍（每个领地一次、进存档；没有介绍文本的领地不发）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSettlingLakeSelfCheck）；单段：bash tools/unity-validate.sh --segment FgSettlingLakeSelfCheck。
    /// </summary>
    public static class FgSettlingLakeSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string Quest = "main.anticorrosion";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/FG 沉淀湖领地")]
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
            Line("\n[沉淀湖领地] 领地模板与锚点、防腐涂层、酸沼软门槛、样品车接车点、阵营介绍（FG10-REG-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            Func<float> originalSquadTime = RegionSquadCommandSystem.RealTime;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fglake-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                RegionSquadCommandSystem.RealTime = () => _fakeNow;
                GameRoot.BindWorldProviders();
                AcidMarshService.ResetForTests();
                TerritoryIntroService.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();

                Step(CheckData);
                Step(CheckWorld);
                Step(CheckJourney);
                Step(CheckIntro);
            }
            finally
            {
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                SignalUplinkService.RealTimeForTests = null;
                RegionSquadCommandSystem.RealTime = originalSquadTime;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
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
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  沉淀湖领地：全部 {_pass} 项通过" : $"  沉淀湖领地：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            TerritoryTemplate tpl = WorldGenContent.TemplateRows.FirstOrDefault(r => r != null && r.TerritoryId == EnemyDefs.FactionClarity);
            bool template = tpl != null && tpl.HazardZones > 0 && tpl.SideChance > 0f && tpl.RelicFactor > 0f && tpl.CacheChance > 0f && tpl.TerminalChance > 0f
                            && GameText.Has(tpl.IntroKey);
            Quest q = QuestCatalog.Get(Quest);
            KeyMaterial k = QuestCatalog.KeyMaterialOf(AcidMarshService.CoatingBlueprintId);
            bool quest = q != null && q.Anchor == QuestCatalog.AnchorBeltOuter && q.Territory == EnemyDefs.FactionClarity && q.RewardBlueprint == AcidMarshService.CoatingBlueprintId
                         && k != null && k.Kind == "blueprint";
            bool tuning = Mathf.Approximately(AcidMarshService.CoatingResist, 0.9f) && AcidMarshService.GateConfirmSeconds >= 2f && AcidMarshService.PickupMargin >= 4f;
            string[] keys =
            {
                "hazard.acid_marsh.coating_on", "hazard.acid_marsh.coating_off", "hazard.acid_marsh.coating_notify", "hazard.acid_marsh.gate.refuse",
                "hazard.acid_marsh.gate.forced", TerritoryIntroService.IntroKey(EnemyDefs.FactionClarity), "world.anchor.baptism_platform.name",
            };
            string[] missing = keys.Where(x => !GameText.Has(x)).ToArray();
            bool notify = NotificationCatalog.TryGetType(TerritoryIntroService.NotifyType, out _) && NotificationCatalog.TryGetType("acid_coating", out _);
            Expect(template && quest && tuning && missing.Length == 0 && notify,
                $"A1 澄净领地模板（危害区 {tpl?.HazardZones}、支线 {tpl?.SideChance}、遗迹倍率 {tpl?.RelicFactor}、缓存 {tpl?.CacheChance}、终端 {tpl?.TerminalChance}、介绍）；" +
                $"样品车任务接车点 = {q?.Anchor}、奖励 = 防腐涂层蓝图（{k?.Kind}）；涂层抗性 {AcidMarshService.CoatingResist}、确认窗口 {AcidMarshService.GateConfirmSeconds} 秒；" +
                $"文本齐全（缺 {string.Join(",", missing)}）；通知类型 {notify}");
        }

        // ─────────────────────────────── B 世界 ───────────────────────────────

        private static void CheckWorld()
        {
            foreach (int seed in new[] { 1, 42, 20261022 })
            {
                CampaignState s = NewWorldState(seed);
                WorldPlan plan = WorldGenService.PlanFor(s);
                PlannedTerritory t = plan?.Find(EnemyDefs.FactionClarity);
                if (t == null)
                {
                    Fail($"B1 种子 {seed}：规划层里没有澄净领地");
                    continue;
                }
                var feats = new List<WorldFeature>();
                WorldGenQuery.FeaturesIn(s, t.CenterX - t.Radius, t.CenterY - t.Radius, t.CenterX + t.Radius, t.CenterY + t.Radius, feats);
                int Count(WorldFeatureKind kind) => feats.Count(f => f.Kind == kind && f.TerritoryId == t.Id);
                PlannedAnchor platform = plan.Anchors.FirstOrDefault(a => a.Id == "baptism_platform");
                bool platformInside = platform != null && platform.Territory == t
                                      && (long)(platform.OriginX - t.CenterX) * (platform.OriginX - t.CenterX) + (long)(platform.OriginY - t.CenterY) * (platform.OriginY - t.CenterY) <= (long)t.Radius * t.Radius;
                bool zones = plan.HazardZones.Any(z => z.TerritoryId == t.Id);
                long core2 = (long)(t.CenterX - plan.CoreX) * (t.CenterX - plan.CoreX) + (long)(t.CenterY - plan.CoreY) * (t.CenterY - plan.CoreY);
                double coreDist = Math.Sqrt(core2);
                Expect(t.Act == 2 && t.HazardWidth >= 80 && t.HazardWidth <= 120 && coreDist >= 1100 && coreDist <= 1700 && zones
                       && Count(WorldFeatureKind.Outpost) > 0 && Count(WorldFeatureKind.Cache) > 0 && Count(WorldFeatureKind.Terminal) > 0 && Count(WorldFeatureKind.SideSite) > 0 && platformInside,
                    $"B1 种子 {seed}：澄净·沉淀湖（第 {t.Act} 幕，中心离核心 {coreDist:0} 格，半径 {t.Radius}，外圈酸沼带宽 {t.HazardWidth}）——领地里酸沼区 {plan.HazardZones.Count(z => z.TerritoryId == t.Id)} 片、" +
                    $"据点 {Count(WorldFeatureKind.Outpost)}、缓存 {Count(WorldFeatureKind.Cache)}、终端 {Count(WorldFeatureKind.Terminal)}、支线地点 {Count(WorldFeatureKind.SideSite)}、遗迹 {Count(WorldFeatureKind.Relic)}；湖心平台锚点在领地里（{platformInside}）");

                bool resolved = CampaignObjectiveTracker.ResolveAnchorForTests(s, Quest, out int px, out int py);
                var pickup = new Vector2(px, py);
                float fromCenter = Vector2.Distance(pickup, new Vector2(t.CenterX, t.CenterY));
                float pickupToCore = Vector2.Distance(pickup, new Vector2(plan.CoreX, plan.CoreY));
                bool dry = AcidMarshService.ZoneAt(s, pickup) == null;
                Expect(resolved && dry && fromCenter >= t.OuterRadius && fromCenter <= t.OuterRadius + AcidMarshService.PickupMargin * 4f + 24f && pickupToCore < coreDist,
                    $"B2 种子 {seed}：防腐涂层样品车的接车点在酸沼带外（不沾酸 {dry}），离领地中心 {fromCenter:0} 格（带外缘 {t.OuterRadius}），朝家园一侧（离核心 {pickupToCore:0} < {coreDist:0}）——拿涂层之前不用先穿过酸沼");
            }
        }

        // ─────────────────────────────── C 区域旅程片段 ───────────────────────────────

        private static void CheckJourney()
        {
            CampaignState s = NewWorld(42);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act); // 第二幕：澄净领地在地图上显示
            }
            WorldPlan plan = WorldGenService.PlanFor(s);
            PlannedTerritory t = plan.Find(EnemyDefs.FactionClarity);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            var beltPoint = new Vector2(t.CenterX + t.Radius + t.HazardWidth * 0.5f, t.CenterY);
            var center = new Vector2(t.CenterX, t.CenterY);
            var core = new Vector2(plan.CoreX, plan.CoreY);

            // C1 没有涂层：酸沼带上每秒 2%。
            float lost0 = Corrode(home, beltPoint);
            bool noCoat = !AcidMarshService.HasCoating(s) && home.PlayerAcidResist == 0f;
            bool hoverOff = HomeExplorationService.TryDescribeHazard(s, Mathf.FloorToInt(beltPoint.x), Mathf.FloorToInt(beltPoint.y), out _, out string bodyOff)
                            && bodyOff.Contains(GameText.Get("quest.main.anticorrosion.title"));
            Expect(noCoat && lost0 >= 40f && lost0 <= 80f && hoverOff,
                $"C1 没有防腐涂层：酸沼带上的机器 3 个游戏秒掉 {lost0:0.#}（每秒 2% × 1000）；悬停写“没有涂层、蓝图来自第二幕主线「{GameText.Get("quest.main.anticorrosion.title")}」”（{hoverOff}）");

            // C2 负向：没有涂层时命令进酸沼 → 拒绝并说明；确认窗口内再下一次 = 强行进入；过了窗口重新拒绝。已经在酸沼里往外走不拦。
            bool routeIn = AcidMarshService.RouteEntersAcid(s, core, center, out string zone);
            bool fromInside = AcidMarshService.RouteEntersAcid(s, beltPoint, core, out _);
            RegionSquadCommandSystem squad = WorldSimulation.Home?.SquadCommands;
            MachineRecord m = MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId).OrderBy(x => x.LogicId).FirstOrDefault();
            if (squad == null || m == null)
            {
                Fail($"C2 没有家园小队命令入口或开局机器（squad {squad != null}，machine {m != null}）");
                return;
            }
            squad.DebugSelectMany(new[] { m.LogicId });
            int r0 = RegionSquadCommandSystem.AcidGateRefusals;
            int f0 = RegionSquadCommandSystem.AcidGateForced;
            _fakeNow = 100f;
            squad.IssueMoveTo(center, paused: false);
            string refuse = squad.RecentEvents.LastOrDefault() ?? string.Empty;
            bool refused = RegionSquadCommandSystem.AcidGateRefusals == r0 + 1 && RegionSquadCommandSystem.AcidGateForced == f0
                           && refuse.Contains(GameText.Get("quest.main.anticorrosion.title")) && refuse.Contains(AcidMarshService.ZoneName(s, zone));
            _fakeNow = 100f + AcidMarshService.GateConfirmSeconds * 0.5f;
            squad.IssueMoveTo(center, paused: false);
            bool forced = RegionSquadCommandSystem.AcidGateForced == f0 + 1;
            squad.Stop();
            _fakeNow = 200f;
            squad.IssueMoveTo(center, paused: false);
            _fakeNow = 200f + AcidMarshService.GateConfirmSeconds + 1f;
            squad.IssueMoveTo(center, paused: false);
            bool expired = RegionSquadCommandSystem.AcidGateRefusals == r0 + 3 && RegionSquadCommandSystem.AcidGateForced == f0 + 1;
            int r1 = RegionSquadCommandSystem.AcidGateRefusals;
            squad.IssueMoveTo(core + new Vector2(6f, 6f), paused: false);
            bool dryOrderOk = RegionSquadCommandSystem.AcidGateRefusals == r1;
            squad.Stop();
            Expect(routeIn && zone == t.Id + "#belt" && !fromInside && refused && forced && expired && dryOrderOk,
                $"C2 负向（卡片“没有防腐涂层时尝试进入”）：从家园往湖心下令 → 拒绝并说明（“{refuse}”）；{AcidMarshService.GateConfirmSeconds} 秒内再下一次 = 强行进入（{forced}）；" +
                $"过了窗口重新拒绝（{expired}）；不经过酸沼的命令不拦（{dryOrderOk}）；已经在酸沼里往外走不拦（{!fromInside}）");

            // C3 拿到防腐涂层蓝图（主线奖励的同一个事件账本口径）→ 下一个游戏秒写进内核并通知一次 → 只掉一成。
            int notices0 = AcidMarshService.CoatingNotices;
            CampaignEventLedger.TryGrant(s, CampaignObjectiveTracker.BlueprintEventId(AcidMarshService.CoatingBlueprintId), "QuestBlueprint", s.PlaySeconds, Quest);
            AcidMarshService.InvalidateCoating();
            WorldSimulation.StepMany(GameClock.StepHz + 1);
            bool applied = AcidMarshService.HasCoating(s) && Mathf.Approximately(home.PlayerAcidResist, AcidMarshService.CoatingResist) && AcidMarshService.CoatingNotices == notices0 + 1;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool once = AcidMarshService.CoatingNotices == notices0 + 1;
            float lost1 = Corrode(home, beltPoint);
            Expect(applied && once && lost1 >= 3f && lost1 <= 12f,
                $"C3 拿到防腐涂层蓝图：下一个游戏秒写进星球战斗内核（抗性 {home.PlayerAcidResist}），通知一次（{once}）；同样 3 个游戏秒只掉 {lost1:0.#}（原来 {lost0:0.#}，“安全穿过酸沼带”）");

            // C4 有涂层：再下令不拦；悬停与派遣检查单写涂层状态；读档后（派生量）重写。
            int r2 = RegionSquadCommandSystem.AcidGateRefusals;
            _fakeNow = 300f;
            squad.IssueMoveTo(center, paused: false);
            bool notGated = RegionSquadCommandSystem.AcidGateRefusals == r2;
            squad.Stop();
            bool hoverOn = HomeExplorationService.TryDescribeHazard(s, Mathf.FloorToInt(beltPoint.x), Mathf.FloorToInt(beltPoint.y), out _, out string bodyOn)
                           && bodyOn.Contains(AcidMarshService.CoatingLine(s)) && bodyOn.Contains(AcidMarshService.HoverLine());
            ExpeditionChecklistService.HazardHit? hit = ExpeditionChecklistService.DefaultHazardProbe(s, new List<Vector2> { core, center });
            bool checklist = hit.HasValue && hit.Value.Consequence.Contains(AcidMarshService.HoverLine()) && !hit.Value.Consequence.Contains(GameText.Get("hazard.hover.pending"));
            home.SetAcidMarsh(AcidMarshService.ActiveShapes(s), AcidMarshService.CorrodePct, AcidMarshService.WetMult, 0f);
            AcidMarshService.RestoreAfterLoad(s, home);
            bool restored = Mathf.Approximately(home.PlayerAcidResist, AcidMarshService.CoatingResist);
            Expect(notGated && hoverOn && checklist && restored,
                $"C4 有涂层后：再往湖心下令不拦（{notGated}）；悬停写“{AcidMarshService.CoatingLine(s)}”（{hoverOn}）；派遣检查单的酸沼提醒写“生效中”不再写“只作标记”（{checklist}）；读档重写派生量（{restored}）");
        }

        private static float Corrode(CombatSite home, Vector2 at)
        {
            int unit = home.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.6f, Health = 1000f, MaxHealth = 1000f, Weapon = -1,
                BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            float lost = home.Kernel.TryGetUnit(unit, out CombatUnitView v) ? 1000f - v.Health : -1f;
            home.Kernel.Despawn(unit);
            return lost;
        }

        // ─────────────────────────────── D 阵营介绍 ───────────────────────────────

        private static void CheckIntro()
        {
            CampaignState s = NewWorldState(42);
            WorldPlan plan = WorldGenService.PlanFor(s);
            PlannedTerritory lake = plan.Find(EnemyDefs.FactionClarity);
            PlannedTerritory silent = plan.Find(EnemyDefs.FactionSilent);
            int before = NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == TerritoryIntroService.NotifyType);
            bool first = TerritoryIntroService.TryIntroduce(s, new Vector2(lake.CenterX, lake.CenterY));
            bool again = TerritoryIntroService.TryIntroduce(s, new Vector2(lake.CenterX + 5f, lake.CenterY));
            bool outside = TerritoryIntroService.TryIntroduce(s, new Vector2(plan.CoreX, plan.CoreY));
            bool noText = silent != null && !GameText.Has(TerritoryIntroService.IntroKey(silent.Id)) && !TerritoryIntroService.TryIntroduce(s, new Vector2(silent.CenterX, silent.CenterY));
            int after = NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == TerritoryIntroService.NotifyType);
            bool saved = s.Factions.TerritoriesIntroduced.Contains(lake.Id);
            Expect(first && !again && !outside && noText && saved && after > before,
                $"D1 第一次进入澄净领地发一条阵营介绍（{first}），再进不重复（{!again}），领地外不发（{!outside}），没有介绍文本的领地不发（{noText}）；记进存档（{saved}）");
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CampaignState NewWorldState(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            AcidMarshService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fglake-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CampaignState NewWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            AcidMarshService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fglake-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgSettlingLakeSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
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
            _report?.AppendLine(text);
        }
    }
}
