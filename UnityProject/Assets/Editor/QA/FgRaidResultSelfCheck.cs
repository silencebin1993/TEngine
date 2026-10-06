using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.HomeValleyFailure;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-08 突袭结算、残骸与离家报告的自动验收（FG06 FGR-DEF-050～053；第 4 节“突袭历史”；验收 FGT-DEF-009；卡片负向“核心被摧毁（失败界面和自动存档选项）”）。
    /// 全部起真实系统：真实家园（世界模拟、战斗内核攻城、炮塔真实开火击毁）、真实机器搬运与送货工单、真实解析台 / 回收站、真实离家报告、真 UXML 面板、真文件存读档。
    /// A 数据；B 结算（展开开单、击毁 / 精英 / 种类 / 贡献、全歼与撤退两种结束、损失四类与负向“攻城之外不记”）；C 残骸（并堆、机器搬回仓库守恒、送解析台得技术数据、
    /// 送回收站、留在仓库、不自动搬运与改回补单、确定性掉落）；D 离家报告突袭段（时间线含远征中的选择、损失、贡献最大）；E 突袭历史面板（真 UXML、通知打开、残骸去向下拉、
    /// 只有导演历史的一项、布局探针）；F 失败（预警自动存档、攻城真实打空核心 → 摧毁、存档被拒、失败页死因 / 安全档 / 读档按钮、读回安全档）；
    /// G FGT-DEF-009 突袭中途两次存读档：不存档 / 存档后接着跑 / 读档后接着跑三条路逐位一致；G2 炮塔与屏障维修完工那一步存档、存档后修满，三条路一致；
    /// H 暂停与 0.5x～3x；H2 攻城中来预警（预警存档推迟）倍速一致、H3 靶场测试中来预警不打断测试；I 观察一致；J 种子集；P 性能。
    /// 复修另加：B6 真实开火的伤害读数 = 内核累计差额；B7 各反应的伤害贡献（与反应归因突袭场次逐项一致、详情有“反应贡献”行）；C6 / C7 残骸去向降级与回收站容量口径；
    /// D2 离家报告面板点突袭行 → 突袭历史；E4 防御总览“突袭历史”按钮真点击。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgRaidResultSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgRaidResultSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 5;
        private const string L = "turret_light";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string FailureUxml = "Assets/GameRes/Raw/UI/HomeValleyFailure/HomeValleyFailure.uxml";
        private const string Strider = "foundry.strider";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq;
        private static int _planSeq;
        private static double _fakeNow;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 突袭结算与残骸")]
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
            Line("\n[突袭结算与残骸] 结算（伤害 / 击毁 / 反应 / 损失 / 贡献 / 战利品）、残骸搬运与去向、离家报告突袭段、突袭历史面板、核心被摧毁与自动存档、存读档、倍速、观察一致（FG6-DEF-08）");
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
            Func<double> originalClock = RaidResultPanelUIToolkit.Clock;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgraidresult-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                SiegeCatalog.Reload();
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
                RaidResultPanelUIToolkit.Clock = () => _fakeNow;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；击毁事件在 AOT 内核（Main/Sim）发出，结算 / 残骸 / 去向记账在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckSettlementDestroyed);
                Step(CheckDamageAndReactions);
                Step(CheckSettlementWithdrawn);
                Step(CheckLosses);
                Step(CheckWreckHaulAndBench);
                Step(CheckWreckRecyclerAndOff);
                Step(CheckWreckFallback);
                Step(CheckDrops);
                Step(CheckAwayReport);
                Step(CheckPanel);
                Step(CheckCoreLostAndAutosave);
                Step(CheckAutosaveDeferred);
                Step(CheckSaveLoad);
                Step(CheckRepairSaveLoad);
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
                Fail($"突袭结算自检抛异常：{e}");
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
                RaidResultPanelUIToolkit.InWorldOverrideForTests = false;
                RaidResultPanelUIToolkit.Clock = originalClock;
                RaidResultPanelUIToolkit.Close();
                RaidResultService.ResetSessionState();
                SiegeService.ResetSessionState();
                DefenseService.ResetSessionState();
                TurretService.ResetSessionState();
                RepairDroneService.ResetSessionState();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                HomeRaidAlertService.ResetSession();
                StandingRuleService.ResetForTests();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                NotificationCenter.LocateHandler = originalLocate;
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
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                foreach (object o in InputRouter.ModalOwnerList.ToArray())
                {
                    InputRouter.PopModal(o);
                }
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
            Line($"  · [突袭结算与残骸] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>与主菜单“新建”同一入口（v2 世界），家园有电、防御研发已完成。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RaidDirectorService.ResetSessionState();
            RaidResultService.ResetSessionState();
            StandingRuleService.ResetForTests();
            WorldSimulation.UnloadAll();
            HomeRaidAlertService.ResetSession();
            GameClock.ResetSession();
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            UiEscapeStack.Clear();
            _seq = 8800;
            _planSeq = 0;
            CampaignState s = CampaignState.CreateNew("fgraidresult-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 3000;
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse || b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench))
                {
                    b.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：仓库与解析台修好（残骸要搬进仓库、送进解析台；修复流程由 FG4-ECO-05 覆盖）
                }
            }
            F.PowerUp(s);
            // 与 FgTurretSelfCheck 同一套前置：第二台发电机（几座炮塔不缺电）、原件库存与信号核（炮塔默认蓝图的固件来源）。
            GridCell? gen = F.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (gen.HasValue)
            {
                F.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "rr_gen", gen.Value);
            }
            GameLogic.Campaign.Primitive.PrimitiveInventory.EnsureSeeded(s);
            GameLogic.Campaign.Signal.SignalCoreService.EnsureInitialized(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            // 测试只看自己设的规则：删掉新战役默认开启的规则（远征卸货等），免得干扰机器分配。
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
            return s;
        }

        private static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        private static Vector2 OutsidePoint(CampaignState s, float dist, int startDir = 0)
        {
            Vector2 c = CoreCenter(s);
            for (int k = 0; k < 16; k++)
            {
                float ang = ((startDir + k) % 16) * 22.5f * Mathf.Deg2Rad;
                var p = new Vector2(c.x + Mathf.Cos(ang) * dist, c.y + Mathf.Sin(ang) * dist);
                GridCell g = SiegeService.NearestPassable(NavService.CellOf(p.x, p.y), 6);
                if (NavService.PassableNow(g.X, g.Y, NavConst.ClassHostile))
                {
                    return new Vector2(g.X, g.Y);
                }
            }
            return c + new Vector2(dist, 0f);
        }

        private static BuildingRecord Register(CampaignState s, string type, GridCell c)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#rr" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Rotation = 0,
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
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
            return r;
        }

        /// <summary>测试捷径：在核心附近登记一座建成的 <paramref name="type"/>（真实放置 / 施工由 FG3 / FG6-DEF-01 的自检覆盖）。</summary>
        private static BuildingRecord Place(CampaignState s, string type, float from = 5f, float to = 26f, bool needPower = false)
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
                    if (needPower && b.PowerState != BuildingPowerState.Powered)
                    {
                        s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                        HomeGridService.MapFor(s);
                        HomeValleyPowerGrid.Recompute(s);
                        TurretService.Sync(s);
                        continue;
                    }
                    return b;
                }
            }
            Fail($"测试准备：家园里找不到能放 {type} 的空地");
            return null;
        }

        /// <summary>测试捷径：在 <paramref name="near"/> 附近（螺旋由近到远，至多 <paramref name="radius"/> 格）登记一座接得上电的建成炮塔——放在来袭方向上，炮塔射程盖得到攻城的敌人。</summary>
        private static BuildingRecord PlaceNear(CampaignState s, string type, Vector2 near, int radius = 12)
        {
            var c0 = new GridCell(Mathf.RoundToInt(near.x), Mathf.RoundToInt(near.y));
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
                        var c = new GridCell(c0.X + dx, c0.Y + dy);
                        if (!HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            continue;
                        }
                        BuildingRecord b = Register(s, type, c);
                        if (b.PowerState != BuildingPowerState.Powered)
                        {
                            s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                            HomeGridService.MapFor(s);
                            HomeValleyPowerGrid.Recompute(s);
                            TurretService.Sync(s);
                            continue;
                        }
                        return b;
                    }
                }
            }
            return Place(s, type, needPower: true);
        }

        private static int TurretUnit(CampaignState s, BuildingRecord t)
        {
            TurretRecord r = t != null ? TurretService.Find(s, t.BuildingId) : null;
            return r != null && Site != null && Site.TryGetTurretUnit(r.Serial, out int u) ? u : 0;
        }

        private static int HomeMachine(CampaignState s, out int unit)
        {
            unit = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId).OrderBy(x => x.LogicId))
            {
                if (Site != null && Site.TryGetMachineUnit(m.LogicId, out unit))
                {
                    return m.LogicId;
                }
            }
            return 0;
        }

        /// <summary>
        /// 测试捷径：一支已经到达、带计划（波次 / 预警时刻）的突袭队伍（正式的导演 → 出发 → 行进 → 到达由 FgRaidDirectorSelfCheck / FgSiegeSelfCheck F 段覆盖）；
        /// 下一步攻城服务按编成展开、结算服务开单——之后全走正式流程。
        /// </summary>
        private static TransitGroupRecord Raid(CampaignState s, Vector2 at, int units, int elites = 0, string faction = "foundry", string unitId = Strider)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 o = at + (at - CoreCenter(s)).normalized * 60f;
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, faction, units, o.x, o.y, core.X, core.Y);
            g.Faction = faction;
            g.UnitIds = new[] { unitId };
            g.UnitCounts = new[] { units };
            g.EliteCounts = new[] { elites };
            g.TargetKind = RaidDirectorService.TargetHome;
            g.PosX = at.x;
            g.PosY = at.y;
            g.RouteX = new[] { (int)Mathf.Round(at.x) };
            g.RouteY = new[] { (int)Mathf.Round(at.y) };
            g.RouteIndex = 1;
            g.RouteState = WorldTransitSystem.RouteFollowing;
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks;
            _planSeq++;
            var plan = new RaidPlanRecord
            {
                PlanId = "plan-rr" + _planSeq.ToString(CultureInfo.InvariantCulture), Serial = 900 + _planSeq, Wave = 40 + _planSeq, State = RaidDirectorService.StateDeparted,
                GroupId = g.GroupId, Faction = faction, Level = 2, Trigger = RaidCatalog.TriggerStory, TargetKind = RaidDirectorService.TargetHome,
                WarnTick = Math.Max(0, GameClock.Ticks - GameClock.StepHz * 30L), ArrivedTick = g.ArrivedAtTick,
            };
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            d.Plans = d.Plans.Append(plan).ToArray();
            g.PlanId = plan.PlanId;
            WorldSimulation.StepMany(1);
            return g;
        }

        private static int Key(TransitGroupRecord g) => SiegeService.KeyOf(g);

        private static List<int> Raiders(TransitGroupRecord g)
        {
            var ids = new List<int>();
            Site?.SiegeRaiderIds(Key(g), ids);
            return ids;
        }

        private static bool IsElite(int unit) => Site != null && Site.TryGetSiegeRaider(unit, out _, out _, out bool elite, out _, out _) && elite;

        /// <summary>经内核的阵亡入口（与弹体打死同一条 CombatLogic.Kill）打死这些攻城单位，击杀者 = <paramref name="killer"/>（0 = 来源不明）；之后走一步处理事件。</summary>
        private static int Kill(IEnumerable<int> ids, int killer)
        {
            int n = 0;
            foreach (int id in ids.ToList())
            {
                if (Site.Kernel.Kill(id, killer))
                {
                    n++;
                }
            }
            WorldSimulation.StepMany(1);
            return n;
        }

        private static void Seconds(float sec) => F.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => F.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string Short(string s) => s == null ? string.Empty : s.Length > 220 ? s.Substring(0, 220) + "…" : s;

        /// <summary>两段签名第一处不同附近的片段（失败信息只写差异处，不写整段）。</summary>
        private static string Diff(string a, string b)
        {
            a ??= string.Empty;
            b ??= string.Empty;
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 60);
            string Cut(string x) => from >= x.Length ? string.Empty : x.Substring(from, Math.Min(260, x.Length - from));
            return $"第 {i} 个字符起不同（长度 {a.Length} / {b.Length}，字符 {(i < a.Length ? ((int)a[i]).ToString() : "-")} / {(i < b.Length ? ((int)b[i]).ToString() : "-")}）：\n  甲 …{Cut(a)}\n  乙 …{Cut(b)}";
        }

        private static string WreckRes => ItemCatalog.ResourceTypeOf(AnalysisCatalog.WreckId);

        private static List<GroundItemRecord> WreckPiles(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null && g.ResourceType == WreckRes).ToList();

        private static long Loot(RaidResultRecord r, string id) => r?.Loot?.Where(a => a != null && a.ItemId == id).Sum(a => a.Amount) ?? 0;

        private static WorkOrderRecord HaulFor(CampaignState s, string groundItemId) =>
            (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).FirstOrDefault(o => o != null && o.Kind == WorkOrderKind.Haul && o.TargetId == groundItemId && HomeValleyWorkOrders.IsActive(o));

        private static List<WorkOrderRecord> WreckDeliveries(CampaignState s) =>
            (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.Kind == WorkOrderKind.Deliver && o.IssuerId == RaidResultService.DeliverIssuer).ToList();

        private static string Signature(CampaignState s)
        {
            CombatSite site = Site;
            int field = RaidResultService.WrecksOnField(s);
            int hauls = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o != null && o.Kind == WorkOrderKind.Haul && HomeValleyWorkOrders.IsActive(o));
            return RaidResultService.Snapshot(s) + "|S" + SiegeService.Snapshot(s) + "|W" + field + "|I" + HomeInventory.Stock(s, AnalysisCatalog.WreckId) + "|O" + hauls
                   + "|H" + (site != null ? site.Kernel.StateHash().ToString("X16") : "-") + "|B" + DurabilitySig(s);
        }

        /// <summary>复修（FGT-DEF-009）：家园每座建筑的记录耐久（逐位）、最近挨打的步、施工状态——存档不改写记录、读档后对账与不存档一致，都要在签名里看得到。</summary>
        private static string DurabilitySig(CampaignState s)
        {
            var sb = new StringBuilder();
            // 按建筑 ID 排序再比：存档会把建筑记录数组按 ID 规范化排序（既有行为，与耐久无关），这里比的是每座建筑的值。
            foreach (BuildingRecord b in (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(x => x != null).OrderBy(x => x.BuildingId, StringComparer.Ordinal))
            {
                if (b.RegionId == HomeValleyLayout.RegionId)
                {
                    sb.Append(b.BuildingId).Append(':').Append(b.Health.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(b.LastHitTick)
                      .Append(':').Append((int)b.ConstructionState).Append(';');
                }
            }
            return sb.ToString();
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RaidResultService.ResetSessionState();
            HomeValleyWorkOrders.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                fail = rr.Message;
                return null;
            }
            fail = null;
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return rr.State;
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static readonly string[] TuningKeys =
        {
            "raid.result.wreck_per_unit", "raid.result.wreck_per_elite", "raid.result.wreck_merge_cells", "raid.result.wreck_pile_max", "raid.result.piles_max",
            "raid.result.drop_module_chance", "raid.result.drop_firmware_chance", "raid.result.drop_elite_mult", "raid.result.timeline_max", "raid.result.losses_max",
            "raid.result.contrib_max", "raid.result.contrib_top", "raid.result.route_seconds", "raid.result.route_batch", "raid.result.autosave_on_warning",
            "raid.result.panel_refresh_seconds", "raid.result.perf_ms",
        };

        private static readonly string[] TextKeys =
        {
            "raid.result.notify", "raid.result.summary", "raid.result.panel.title", "raid.result.panel.none", "raid.result.panel.wreck_tip", "raid.result.panel.wreck.bench",
            "raid.result.panel.wreck.recycler", "raid.result.panel.wreck.store", "raid.result.panel.wreck.off", "raid.result.tl.choice_stay", "raid.result.loss.summary",
            "raid.result.contrib.row", "raid.result.wreck.status", "raid.result.defense", "raid.result.trace.wreck", "failure.title", "failure.cause_raid", "failure.lastsave", "failure.load",
            "failure.menu", "save.blocked_core_lost", "pause.raid_history", "defov.history", "codex.raid.result.body", "raid.end.destroyed", "away.raid.best",
        };

        private static bool HasCjk(string t) => t.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static void CheckData()
        {
            bool tuning = TuningKeys.All(k => GridContent.TryGetTuning(k, out _)) && RaidResultService.WreckPerUnit == 1 && RaidResultService.WreckPerElite == 2
                          && RaidResultService.WreckPileMax == 5 && Mathf.Approximately(RaidResultService.DropModuleChance, 0.03f) && Mathf.Approximately(RaidResultService.DropFirmwareChance, 0.015f)
                          && Mathf.Approximately(RaidResultService.RouteSeconds, 10f) && RaidResultService.ContribTop == 3 && RaidResultService.AutosaveOnWarning;
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in TextKeys)
                {
                    string t = GameText.Get(k);
                    if (string.IsNullOrEmpty(t) || GameText.ContainsMarker(t) || (lang == GameLanguage.En && HasCjk(t)) || (lang == GameLanguage.ZhCn && !HasCjk(t)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = new[] { GuidanceHooks.RaidFirstResult, GuidanceHooks.RaidResultFirstOpen, GuidanceHooks.RaidFirstWreck, GuidanceHooks.RaidFirstCoreLost }.All(h => GuidanceHooks.Known.Contains(h));
            bool codex = MechanicCodex.Find("codex.raid.result") != null;
            bool notify = NotificationCatalog.TryGetType("raid_result", out NotifyTypeDef nt) && nt.AwaySection == "raid";
            bool enums = (int)SaveReason.RaidWarning == 8 && (int)SaveOutcome.CampaignLost == 6 && (int)CombatEventKind.SiegeKilled == 20;
            Expect(tuning && missing.Count == 0 && hooks && codex && notify && enums,
                $"A1 数据：{TuningKeys.Length} 项调参入 fg.TbHomeTuning（每台 1 份残骸、精英 2 份、每堆 ≤ 5、掉落 3% / 1.5%、每 10 游戏秒检查去向、贡献排行前 3、预警自动存档开）、" +
                $"{TextKeys.Length} 个文本键中英齐全（缺 {string.Join(",", missing.Take(6))}）、4 个引导钩子登记、图鉴条目（{codex}）、通知“突袭结算”记进离家报告突袭段（{notify}）、" +
                $"存档原因“突袭预警”/ 写盘结果“战役已失败”/ 内核玩法事件“攻城单位被击毁”只在末尾追加（{enums}）");
        }

        // ── B 结算 ────────────────────────────────────────────────────────────

        private static void CheckSettlementDestroyed()
        {
            CampaignState s = NewWorld(8101);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            int logic = HomeMachine(s, out int mUnit);
            Vector2 at = OutsidePoint(s, 34f);
            TransitGroupRecord g = Raid(s, at, 8, elites: 2);
            Seconds(0.5f);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            RaidPlanRecord plan = RaidDirectorService.FindPlan(s, g.PlanId);
            CombatCounters c0 = Site.Kernel.Counters;
            bool begun = r != null && r.EndTick < 0 && r.Unfolded == 8 && r.Wave == plan?.Wave && r.PlanId == plan?.PlanId && r.Outcome == RaidResultService.OutcomeOpen
                         && r.Timeline.Any(t => t.Kind == "warn") && r.Timeline.Any(t => t.Kind == "arrive") && r.Timeline.Any(t => t.Kind == "unfold")
                         && r.BaseDealt <= c0.DamageToHostile && r.BaseTaken <= c0.DamageToPlayer && RaidResultService.PrimaryOpen(s) == r;
            Expect(begun && tUnit > 0 && mUnit > 0,
                $"B1 FGR-DEF-050：突袭部队展开攻城时开一份结算（第 {r?.Wave} 波、展开 {r?.Unfolded} 台、过程记下预警 / 到达 / 展开、伤害读数从展开那一刻算），炮塔单位 {tUnit}、家园机器 {logic}");

            List<int> ids = Raiders(g);
            int elitesHit = 0;
            var byTurret = ids.Take(3).ToList();
            var byMachine = ids.Skip(3).Take(2).ToList();
            var byNone = ids.Skip(5).Take(1).ToList();
            elitesHit += byTurret.Concat(byMachine).Concat(byNone).Count(IsElite);
            int defeatedBefore = s.Research?.Range?.DefeatedEnemyTypes?.Length ?? 0;
            Kill(byTurret, tUnit);
            Kill(byMachine, mUnit);
            Kill(byNone, 0);
            RaidContribRecord ct = r?.Contrib.FirstOrDefault(x => x.Kind == RaidResultService.KindTurret);
            RaidContribRecord cm = r?.Contrib.FirstOrDefault(x => x.Kind == RaidResultService.KindMachine);
            long wreckExpect = 6L * RaidResultService.WreckPerUnit + elitesHit * (RaidResultService.WreckPerElite - RaidResultService.WreckPerUnit);
            List<GroundItemRecord> piles = WreckPiles(s);
            bool pilesOk = piles.Count > 0 && piles.Sum(p => p.Amount) == wreckExpect && piles.All(p => p.Amount <= RaidResultService.WreckPileMax) && piles.All(p => HaulFor(s, p.GroundItemId) != null)
                           && r != null && r.Piles.Length == piles.Count && piles.Count < 6;
            bool attributed = r != null && r.Killed == 6 && r.EliteKilled == elitesHit && ct != null && ct.Kills == 3 && ct.Id == turret.BuildingId && cm != null && cm.Kills == 2
                              && cm.Id == logic.ToString(CultureInfo.InvariantCulture) && r.OtherKills == 1 && r.KilledKinds.Any(k => k.ItemId == Strider && k.Amount == 6)
                              && Loot(r, AnalysisCatalog.WreckId) == wreckExpect && TurretService.Find(s, turret.BuildingId).KillCount == 3;
            bool rangeUnlocked = s.Research?.Range?.DefeatedEnemyTypes != null && s.Research.Range.DefeatedEnemyTypes.Contains("enemy_strider");
            Expect(attributed && pilesOk && rangeUnlocked,
                $"B2 击毁按内核事件记账：6 台（精英 {elitesHit}）、炮塔 {ct?.Kills} / 机器 {cm?.Kills} / 来源不明 {r?.OtherKills}、种类 {Strider} ×6、与炮塔击毁数一致；残骸 {wreckExpect} 份并成 {piles.Count} 堆" +
                $"（每堆 ≤ {RaidResultService.WreckPileMax}，每堆一张搬运单 {pilesOk}）；“击败过的敌人种类”记下 enemy_strider（FG-GAP-101 靶场解锁，{defeatedBefore} → {s.Research?.Range?.DefeatedEnemyTypes?.Length}）");

            int notes = NotifyCount("raid_result");
            Kill(Raiders(g), tUnit);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            Seconds(1f);
            RaidContribRecord top = RaidResultService.Ranked(r).FirstOrDefault();
            string last = NotificationCenter.History.LastOrDefault(n => n.Type?.Id == "raid_result")?.Text ?? string.Empty;
            RaidHistoryRecord h = RaidDirectorService.History(s).LastOrDefault(x => x.PlanId == g.PlanId);
            CombatCounters cEnd = Site.KernelCounters; // 全灭之后没有敌人，累计不再变：结算的差额 = 结束时累计 − 展开时基线（复修：原来只断言 ≥ 0，恒真）
            bool ended = r.EndTick >= 0 && r.Outcome == RaidResultService.OutcomeDestroyed && r.Exited == 0 && r.Killed == 8
                         && r.Dealt == Math.Max(0, cEnd.DamageToHostile - r.BaseDealt) && r.Taken == Math.Max(0, cEnd.DamageToPlayer - r.BaseTaken)
                         && r.Timeline.Last().Kind == "end_destroyed" && NotifyCount("raid_result") == notes + 1 && last.Contains(RaidResultService.WaveLabel(r))
                         && top != null && top.Kind == RaidResultService.KindTurret && top.Kills == 5 && RaidResultService.OpenFor(s, g.GroupId) == null
                         && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstResult);
            bool history = h != null && h.EndReason == RaidDirectorService.EndDestroyed && RaidResultService.HistoryEntries(s).Any(e => e.Result == r);
            var lines = new List<AwayLine>();
            RaidResultService.BuildDetail(s, new RaidHistoryEntry(r, null, r.UnfoldTick), lines);
            string text = string.Join("\n", lines.Select(l => l.Text));
            bool detail = text.Contains(GameText.Get("raid.result.sec.timeline")) && text.Contains(GameText.Get("raid.result.sec.contrib"))
                          && text.Contains(GameText.Format("raid.result.defense", r.Overloads, r.Lays, ProductionStats.UiNumber((float)r.Repaired), r.Kits))
                          && text.Contains(top?.Name ?? "?") && text.Contains(GameText.Get("raid.result.sec.loot")) && text.Contains(ItemCatalog.NameOf(AnalysisCatalog.WreckId))
                          && !GameText.ContainsMarker(text) && lines.Any(l => l.Action == AwayLineAction.Locate && l.HasPos);
            Expect(ended && history && detail,
                $"B3 结束条件“敌人全灭”：最后一台被击毁 → 结算（全部击毁、击毁 8、伤害差额 {r.Dealt} / {r.Taken}）、发通知“{last}”；贡献最大 = {top?.Name}（{top?.Kills} 台）；" +
                $"导演历史结束原因 {h?.EndReason}、突袭历史里有这一份；结算的行写明过程 / 贡献 / 战利品、可定位（{detail}）");
        }

        /// <summary>
        /// B6 / B7（复修 P1）：真实开火下的伤害读数与各反应的伤害贡献。三座炮塔真实开火打一支 4 台的突袭；攻城期间在一台攻城单位身上凑齐一条具名反应的两个配料
        /// （内核的同一条挂状态入口，出手者 = 炮塔），反应经内核计数 → 反馈进给 → 反应归因的突袭场次；结束时结算复制这一场的占比。
        /// </summary>
        private static void CheckDamageAndReactions()
        {
            CampaignState s = RaidScenario(8115, true, 1.5f);
            RaidResultRecord r = RaidResultService.All(s).FirstOrDefault(x => x.EndTick < 0);
            CombatKernel k = Site.Kernel;
            int ri = -1;
            for (int i = 0; i < k.ReactionRuleCount; i++)
            {
                CombatReactionRule rule = k.ReactionRule(i);
                if (rule.Pair != 0u && (rule.Grant != 0u || rule.ResidueBit != 0u) && NamedReactionCatalog.IdOfRule(i) != null)
                {
                    ri = i;
                    break;
                }
            }
            string rid = ri >= 0 ? NamedReactionCatalog.IdOfRule(ri) : null;
            var ids = new List<int>();
            Site.SiegeRaiderIds(0, ids);
            TurretRecord tr = TurretService.All(s).FirstOrDefault();
            BuildingRecord tb = tr != null ? HomeGridService.FindBuilding(s, tr.BuildingId) : null;
            int tUnit = TurretUnit(s, tb);
            int fired0 = ri >= 0 ? k.ReactionCountOf(ri) : 0;
            if (ri >= 0 && ids.Count > 0)
            {
                uint pair = k.ReactionRule(ri).Pair;
                uint a = pair & (~pair + 1u);
                k.ApplyStatus(ids[0], a, 6f, 0f, 0f, 0f, tUnit);
                k.ApplyStatus(ids[0], pair & ~a, 6f, 0f, 0f, 0f, tUnit);
            }
            int fired = ri >= 0 ? k.ReactionCountOf(ri) - fired0 : 0;
            WorldSimulation.StepMany(1); // 反馈进给：内核计数 → 反应归因（突袭场次）
            ReactionSessionRecord live = ReactionAttribution.Sessions(s).LastOrDefault(x => x != null && x.Kind == ReactionAttribution.KindRaid && x.EndTick < 0);
            bool ended = r != null && StepUntil(() => r.EndTick >= 0, 300);
            CombatCounters c = Site.KernelCounters;
            ReactionSessionRecord sess = r != null ? ReactionAttribution.Sessions(s).FirstOrDefault(x => x != null && x.SessionId == r.ReactionSessionId) : null;
            bool damage = ended && r.Dealt > 0 && r.Dealt == c.DamageToHostile - r.BaseDealt && r.Taken == Math.Max(0, c.DamageToPlayer - r.BaseTaken);
            Expect(damage,
                $"B6 FGR-DEF-050 伤害（真实开火）：结算“造成 {r?.Dealt} / 承受 {r?.Taken}”= 结束时内核累计（{c.DamageToHostile} / {c.DamageToPlayer}）− 展开时基线（{r?.BaseDealt} / {r?.BaseTaken}），造成 > 0；" +
                $"结束方式 {r?.Outcome}");

            ReactionShareRecord mine = r?.Reactions?.FirstOrDefault(x => x != null && x.ReactionId == rid);
            bool sameAsSession = sess != null && r.Reactions.Length == (sess.Reactions?.Length ?? 0) && Math.Abs(r.ReactionTotal - sess.TotalDamage) < 1e-9
                                 && sess.Reactions.All(x => r.Reactions.Any(y => y.ReactionId == x.ReactionId && y.Count == x.Count && Math.Abs(y.Damage - x.Damage) < 1e-9));
            var lines = new List<AwayLine>();
            if (r != null)
            {
                RaidResultService.BuildDetail(s, new RaidHistoryEntry(r, null, r.UnfoldTick), lines);
            }
            List<AwayLine> rl = lines.Where(l => l.Section == "reaction").ToList();
            string name = rid != null ? ReactionAttribution.DisplayName(s, rid) : "?";
            double rDamage = r?.Reactions?.Where(x => x != null && (x.Count > 0 || x.Damage > 0)).Sum(x => Math.Max(0, x.Damage)) ?? 0;
            double rTotal = r?.ReactionTotal ?? 0;
            int allPct = rTotal > 0 ? Mathf.RoundToInt((float)(100.0 * Math.Min(1.0, rDamage / rTotal))) : 0;
            bool row = rl.Count == 3 && rl[0].Text == GameText.Get("raid.result.sec.reaction") && rl[1].Text.Contains(name)
                       && rl[2].Text == GameText.Format("raid.result.reaction_total", allPct) && !GameText.ContainsMarker(string.Join("\n", rl.Select(l => l.Text)));
            Expect(ri >= 0 && fired >= 1 && live != null && mine != null && mine.Count >= fired && sameAsSession && row,
                $"B7 FGR-DEF-050 各反应的伤害贡献：攻城中凑齐“{name}”的两个配料（内核触发 {fired} 次，出手者炮塔单位 {tUnit}）→ 突袭场次记下；结束时结算复制这一场" +
                $"（{rid} ×{mine?.Count}、伤害 {mine?.Damage:0.0} / 敌方全部 {r?.ReactionTotal:0.0}，与场次逐项一致 {sameAsSession}）；详情“反应贡献”段 {rl.Count} 行（{string.Join(" / ", rl.Select(l => l.Text))}）");
        }

        private static void CheckSettlementWithdrawn()
        {
            CampaignState s = NewWorld(8102);
            Vector2 at = OutsidePoint(s, 40f, 4);
            TransitGroupRecord g = Raid(s, at, 5);
            Seconds(0.5f);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            Kill(Raiders(g).Take(4), 0);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            bool ordered = r != null && r.Timeline.Any(t => t.Kind == "retreat_losses") && r.RetreatReason == SiegeService.ReasonLosses && r.EndTick < 0;
            bool done = StepUntil(() => r.EndTick >= 0, (int)SiegeCatalog.RetreatMaxSeconds + 40);
            Expect(ordered && done && r.Outcome == RaidResultService.OutcomeWithdrawn && r.Exited == 1 && r.Killed == 4 && r.Timeline.Last().Kind == "end_withdrawn"
                   && RaidResultService.OutcomeText(r).Contains(GameText.Get("raid.result.reason.losses")),
                $"B4 结束条件“撤退”：损失超过 70% → 撤退令记进过程（{ordered}）→ 残部撤离家园后结算“{RaidResultService.OutcomeText(r)}”（击毁 {r?.Killed}、撤走 {r?.Exited}）");
        }

        private static void CheckLosses()
        {
            CampaignState s = NewWorld(8103);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            int logic = HomeMachine(s, out _);
            // 负向：没有攻城时被毁的东西不进任何结算。
            MachineOpResult spare = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, CoreCenter(s) + new Vector2(3f, 3f), 50f, 50f);
            MachineRegistry.ApplyDamage(spare.LogicId, 9999f);
            bool quiet = RaidResultService.All(s).Count == 0;
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 36f, 8), 4);
            Seconds(0.6f);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            SiegeState st = SiegeService.StateOf(s);
            SiegeStructureRecord victim = st.Structures.FirstOrDefault(x => x != null && HomeGridService.FindBuilding(s, x.BuildingId)?.BuildingTypeId != HomeValleyLayout.BuildingTypeCore);
            string victimName = victim != null ? FeedbackCues.BuildingLabel(victim.BuildingId) : string.Empty;
            if (victim != null)
            {
                Site.DamageSiegeStructure(victim.Serial, 1e7f);
            }
            WorldSimulation.StepMany(1);
            int raider = Raiders(g).FirstOrDefault();
            Site.Kernel.Kill(tUnit, raider);
            WorldSimulation.StepMany(1);
            MachineRegistry.ApplyDamage(logic, 99999f);
            WorldSimulation.StepMany(1);
            bool counted = r != null && r.LostBuildings == (victim != null ? 1 : 0) && r.LostTurrets == 1 && r.LostMachines == 1 && RaidResultService.LossTotal(r) == r.Losses.Length
                           && r.Losses.Any(l => l.Kind == RaidResultService.KindTurret && l.Id == turret.BuildingId)
                           && r.Losses.Any(l => l.Kind == RaidResultService.KindMachine && l.Id == logic.ToString(CultureInfo.InvariantCulture) && l.Name.Length > 0)
                           && (victim == null || r.Losses.Any(l => l.Kind == RaidResultService.KindBuilding && l.Id == victim.BuildingId && l.Name == victimName))
                           && r.Timeline.Count(t => t.Kind == "first_loss") == 1;
            Expect(quiet && victim != null && counted,
                $"B5 损失：攻城中被拆的建筑“{victimName}”、被打掉的炮塔、阵亡的机器各记一处（合计 {RaidResultService.LossTotal(r)}，明细带当时的名字与位置，第一处损失进过程）；" +
                $"负向：没有攻城时阵亡的机器不进任何结算（{quiet}）");
        }

        // ── C 残骸 ────────────────────────────────────────────────────────────

        private static void CheckWreckHaulAndBench()
        {
            CampaignState s = NewWorld(8104);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteStore);
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 30f, 2), 6);
            Seconds(0.5f);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            Kill(Raiders(g), tUnit);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            long total = Loot(r, AnalysisCatalog.WreckId);
            int stock0 = HomeInventory.Stock(s, AnalysisCatalog.WreckId);
            bool hauled = StepUntil(() => RaidResultService.WrecksOnField(s) == 0 && !s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && HomeValleyWorkOrders.IsActive(o)), 600);
            int stock1 = HomeInventory.Stock(s, AnalysisCatalog.WreckId);
            bool noDelivery = WreckDeliveries(s).Count == 0;
            Expect(total == 6 && hauled && stock1 - stock0 == total && noDelivery && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstWreck),
                $"C1 FGR-DEF-051 残骸 → 机器搬回仓库：{total} 份残骸落地后由机器按搬运单搬回（{hauled}），仓库 {stock0} → {stock1}（守恒）；去向“留在仓库”时不派送货单（{noDelivery}）");

            BuildingRecord bench = HomeValleyAnalysis.FindBench(s);
            if (bench == null)
            {
                bench = Place(s, HomeValleyLayout.BuildingTypeAnalysisBench, 6f, 30f);
            }
            if (bench != null)
            {
                bench.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：解析台修好（修复流程由 FG4-ECO-05 覆盖）
                HomeValleyPowerGrid.Recompute(s);
            }
            AnalysisBenchState a = s.Research.Analysis;
            long processed0 = a?.WrecksProcessed ?? 0;
            long tech0 = a?.TechFromWrecks ?? 0;
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteBench);
            WorldSimulation.StepMany(2);
            List<WorkOrderRecord> orders = WreckDeliveries(s);
            string trace = orders.Count > 0 ? StandingRuleService.DescribeOrder(s, orders[0]) : string.Empty;
            bool ordered = bench != null && orders.Count > 0 && orders.All(o => o.TargetId == bench.BuildingId && o.ReservedItemId == AnalysisCatalog.WreckId && o.ReservedItemAmount <= RaidResultService.RouteBatch)
                           && orders.Sum(o => o.ReservedItemAmount) == stock1 && HomeInventory.Stock(s, AnalysisCatalog.WreckId) == 0
                           && trace.StartsWith(GameText.Format("raid.result.trace.wreck", string.Empty), StringComparison.Ordinal) && trace.Length > GameText.Format("raid.result.trace.wreck", string.Empty).Length;
            bool delivered = StepUntil(() => (a?.WreckBuffer ?? 0) + (a?.WrecksProcessed ?? 0) - processed0 >= stock1, 600);
            bool tech = StepUntil(() => (a?.TechFromWrecks ?? 0) > tech0, 120);
            Expect(ordered && delivered && tech,
                $"C2 去向“先送解析台”：仓库里的 {stock1} 份开成送货单（每单 ≤ {RaidResultService.RouteBatch}，开单即从仓库预留，工单写“{trace}”）→ 机器送进解析台残骸缓存（{delivered}）→ 空闲时逐件处理得技术数据（{tech0} → {a?.TechFromWrecks}）");
        }

        private static void CheckWreckRecyclerAndOff()
        {
            CampaignState s = NewWorld(8105);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            BuildingRecord recycler = Place(s, "recycler", 8f, 34f);
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteOff);
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 30f, 10), 3);
            Seconds(0.5f);
            Kill(Raiders(g), tUnit);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            List<GroundItemRecord> piles = WreckPiles(s);
            bool off = piles.Count > 0 && piles.All(p => HaulFor(s, p.GroundItemId) == null);
            Seconds(30f);
            bool stillThere = RaidResultService.WrecksOnField(s) == 3 && WreckDeliveries(s).Count == 0;
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteRecycler);
            bool backOn = piles.All(p => HaulFor(s, p.GroundItemId) != null);
            Expect(off && stillThere && backOn,
                $"C3 去向“不自动搬运”：残骸留在战场、不派搬运单（{off}），30 游戏秒后还在（{stillThere}）；改回别的去向立即给还在战场上的 {piles.Count} 堆补上搬运单（{backOn}）");
            bool delivered = recycler != null && StepUntil(() =>
            {
                ProductionService.TryGet(s, recycler.BuildingId, out ProductionService.Producer p);
                return p != null && (ProductionService.Count(p.Rec.In, AnalysisCatalog.WreckId) > 0 || p.Rec.PendingItem == AnalysisCatalog.WreckId || p.Rec.ItemsRecycled > 0);
            }, 600);
            bool targeted = WreckDeliveries(s).Count > 0 && WreckDeliveries(s).All(o => o.TargetId == recycler?.BuildingId);
            RaidResultState st = RaidResultService.StateOf(s);
            Expect(delivered && targeted && st.WrecksToRecycler >= 1,
                $"C4 去向“先送回收站”：搬回仓库的残骸开成送往回收站的送货单（{targeted}），机器送进回收站输入、开始分解成废料（{delivered}，累计送去 {st.WrecksToRecycler} 份）");
        }

        /// <summary>
        /// C6（复修 P2）：残骸去向的降级与回收站容量口径。去向“先送解析台”但解析台残骸缓存已满 → 改送回收站；回收站输入里已有别的货时按输入总数算剩余空间
        /// （各种固体共用一个容量，与传送带进料同口径），不多开送货单；两者都用不了 → 残骸留在仓库、不开单。
        /// </summary>
        private static void CheckWreckFallback()
        {
            CampaignState s = NewWorld(8116);
            BuildingRecord recycler = Place(s, "recycler", 8f, 34f);
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            AnalysisBenchState a = s.Research.Analysis;
            if (a != null)
            {
                a.WreckBuffer = AnalysisCatalog.WreckBufferCap; // 测试捷径：解析台残骸缓存装满（正式装填由 C2 覆盖）
            }
            ItemCatalog.TryGet(AnalysisCatalog.WreckId, out ItemDef wreckDef);
            HomeInventory.Add(s, wreckDef, 12);
            ProductionService.Producer p = null;
            bool hasRec = recycler != null && ProductionService.TryGet(s, recycler.BuildingId, out p) && p != null;
            int cap = hasRec ? ProductionService.InCapacity(p, wreckDef) : 0;
            int filler = Math.Max(0, cap - 3);
            if (hasRec && filler > 0)
            {
                ProductionService.Add(ref p.Rec.In, AnalysisCatalog.EncryptedFirmwareId, filler); // 回收站输入里已有别的固体（占掉容量，只剩 3）
            }
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteBench);
            RaidResultState st = RaidResultService.StateOf(s);
            int benchRoom = HomeValleyAnalysis.WreckRoom(s);
            int made = RaidResultService.RouteWrecks(s, st);
            List<WorkOrderRecord> orders = WreckDeliveries(s);
            int reserved = orders.Sum(o => o.ReservedItemAmount);
            bool fellBack = hasRec && benchRoom == 0 && made > 0 && orders.All(o => o.TargetId == recycler.BuildingId) && reserved == cap - ProductionService.Total(p.Rec.In)
                            && reserved == 3 && HomeInventory.Stock(s, AnalysisCatalog.WreckId) == 12 - reserved;
            Expect(fellBack,
                $"C6 去向“先送解析台”、解析台残骸缓存满（剩余 {benchRoom}）→ 改送回收站：回收站输入容量 {cap}、已有别的货 {filler}，只开 {reserved} 份（按输入总数算剩余空间，" +
                $"复修前按同种物品数会开 {Math.Min(12, cap)} 份、挤爆输入缓存），仓库剩 {HomeInventory.Stock(s, AnalysisCatalog.WreckId)}");

            if (recycler != null)
            {
                recycler.ConstructionState = BuildingConstructionState.Disabled; // 回收站停用（玩家禁用）
                HomeValleyPowerGrid.Recompute(s);
            }
            int stock0 = HomeInventory.Stock(s, AnalysisCatalog.WreckId);
            int made2 = RaidResultService.RouteWrecks(s, st);
            Expect(made2 == 0 && HomeInventory.Stock(s, AnalysisCatalog.WreckId) == stock0 && stock0 > 0 && WreckDeliveries(s).Count == orders.Count,
                $"C7 负向：解析台满、回收站停用 → 不开新的送货单，{stock0} 份残骸留在仓库（等有空间再送）");
        }

        private static void CheckDrops()
        {
            CampaignState s = NewWorld(8106);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            RaidResultService.SetWreckRouting(s, RaidResultService.RouteOff);
            int modules0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
            int firmware0 = HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId);
            var results = new List<RaidResultRecord>();
            for (int i = 0; i < 4; i++)
            {
                TransitGroupRecord g = Raid(s, OutsidePoint(s, 34f, i * 4), 40);
                Seconds(0.5f);
                results.Add(RaidResultService.OpenFor(s, g.GroupId));
                Kill(Raiders(g), tUnit);
                Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            }
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int expectM = 0, expectF = 0, kills = 0;
            foreach (RaidResultRecord r in results.Where(x => x != null))
            {
                for (int k = 1; k <= r.Killed; k++)
                {
                    kills++;
                    if (WorldGenMath.Hash(seed, r.Serial, k, 0x52524D31u) / 4294967296.0 < RaidResultService.DropModuleChance)
                    {
                        expectM++;
                    }
                    if (WorldGenMath.Hash(seed, r.Serial, k, 0x52524632u) / 4294967296.0 < RaidResultService.DropFirmwareChance)
                    {
                        expectF++;
                    }
                }
            }
            long gotM = results.Sum(r => Loot(r, AnalysisCatalog.UnparsedModuleId));
            long gotF = results.Sum(r => Loot(r, AnalysisCatalog.EncryptedFirmwareId));
            int physM = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) - modules0
                        + WreckPilesOf(s, ItemCatalog.ResourceTypeOf(AnalysisCatalog.UnparsedModuleId));
            int physF = HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId) - firmware0
                        + WreckPilesOf(s, ItemCatalog.ResourceTypeOf(AnalysisCatalog.EncryptedFirmwareId));
            bool rate = kills == 160 && (expectM + expectF) <= kills * 0.1;
            Expect(rate && gotM == expectM && gotF == expectF && physM == gotM && physF == gotF && RaidResultService.StateOf(s).TotalDrops == gotM + gotF,
                $"C5 少量掉落（FGR-DEF-051“少数情况”）：{kills} 次击毁掉了未解析模块 {gotM} 件、加密固件 {gotF} 件，与按（世界种子, 结算序号, 第几次击毁）重算的期望 {expectM} / {expectF} 逐件一致（确定性）；" +
                $"实物进了仓库（放不下的落地）{physM} / {physF}，走解析台的唯一发放入口");
        }

        private static int WreckPilesOf(CampaignState s, string res) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null && g.ResourceType == res).Sum(g => g.Amount);

        // ── D 离家报告（FGR-DEF-052）──────────────────────────────────────────

        private static void CheckAwayReport()
        {
            CampaignState s = NewWorld(8107);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            long reportStart = GameClock.Ticks;
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 34f, 6), 4);
            RaidPlanRecord plan = RaidDirectorService.FindPlan(s, g.PlanId);
            // 远征中对这一波做的选择（正式由远征 HUD 的“留在远征队”写入，FgHomeRaidAlertSelfCheck 覆盖点击路径）：读的是同一份存档记录。
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            d.AwayDecisions = d.AwayDecisions.Append(new RaidAwayDecisionRecord { Wave = plan.Wave, Choice = HomeRaidAlertService.ChoiceStay, Tick = GameClock.Ticks, SiteId = FracturedCityLayout.RegionId }).ToArray();
            Seconds(0.5f);
            RaidResultRecord r = RaidResultService.OpenFor(s, g.GroupId);
            Kill(Raiders(g).Take(3), tUnit);
            int raider = Raiders(g).FirstOrDefault();
            MachineRegistry.ApplyDamage(HomeMachine(s, out _), 99999f);
            Kill(new[] { raider }, tUnit);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            // 离家报告的容器（开 / 结算由 FG4-ECO-09 的出发 / 撤离事务负责，FgAwayReportSelfCheck 覆盖）：这里给一份覆盖这段时间的已结算报告，只验突袭段的组装。
            var done = new AwayReportRecord { Serial = 77, RegionId = FracturedCityLayout.RegionId, StartTick = reportStart, EndTick = GameClock.Ticks, Outcome = AwayReportService.OutcomeEvacuated, TeamSettled = true };
            var lines = new List<AwayLine>();
            bool empty = AwayReportView.Build(s, done, lines);
            List<AwayLine> raid = lines.Where(l => l.Section == "raid").ToList();
            string text = string.Join("\n", raid.Select(l => l.Text));
            AwayLine head = raid.FirstOrDefault(l => l.Action == AwayLineAction.RaidResult);
            bool ok = !empty && r != null && r.EndTick >= 0 && head != null && head.Arg == r.Serial.ToString(CultureInfo.InvariantCulture)
                      && text.Contains(GameText.Get("raid.result.tl.choice_stay")) && text.Contains(GameText.Get("raid.result.tl.unfold").Split('（')[0])
                      && text.Contains(GameText.Get("raid.result.tl.end_destroyed")) && text.Contains(FeedbackCues.BuildingLabel(turret.BuildingId))
                      && raid.Any(l => l.Text == GameText.Format("raid.result.loss.summary", 0, 0, 0, 1, 0))
                      && r.Timeline.Any(t => t.Kind == "choice_stay") && !GameText.ContainsMarker(text);
            Expect(ok,
                $"D1 FGR-DEF-052 离家报告“突袭”段：这一波一行标题（点开看完整结算）、过程时间线（预警 → 到达 → 展开 → 你选择“留在远征队” → 最后一台被击毁）、损失（机器 1 台）、" +
                $"贡献最大的炮塔“{FeedbackCues.BuildingLabel(turret.BuildingId)}”（共 {raid.Count} 行）" + (ok ? string.Empty : "\n" + Short(text)));

            // D2（复修 P2）：真 UXML 的离家报告面板里点这一波的标题行 → 突袭历史面板打开并选中这一份结算。
            if (s.Stats?.AwayReports != null)
            {
                s.Stats.AwayReports.Reports = (s.Stats.AwayReports.Reports ?? Array.Empty<AwayReportRecord>()).Append(done).ToArray();
            }
            VisualElement arRoot = F.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out GameObject arGo);
            VisualElement rrRoot = F.MountUxml(UiKitFolder + "RaidResultPanel.uxml", out GameObject rrGo);
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            RaidResultPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                AwayReportPanelUIToolkit ap = arGo.AddComponent<AwayReportPanelUIToolkit>();
                ap.BindView(arRoot);
                ap.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                RaidResultPanelUIToolkit rp = rrGo.AddComponent<RaidResultPanelUIToolkit>();
                rp.BindView(rrRoot);
                rp.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                AwayReportPanelUIToolkit.OpenReport(done.Serial);
                ap.Refresh();
                int idx = Enumerable.Range(0, ap.VisibleRowCount).FirstOrDefault(i => ap.Line(i)?.Action == AwayLineAction.RaidResult && ap.Line(i)?.Arg == r?.Serial.ToString(CultureInfo.InvariantCulture));
                bool found = ap.Line(idx)?.Action == AwayLineAction.RaidResult;
                bool clicked = found && ap.Click(idx);
                rp.Refresh();
                Expect(found && clicked && !AwayReportPanelUIToolkit.IsOpen && RaidResultPanelUIToolkit.IsOpen && rp.ShownKey == "R" + r?.Serial,
                    $"D2 离家报告面板（真 UXML）里点这一波的标题行（第 {idx} 行）→ 离家报告收起、突袭历史面板打开并选中这一份（{rp.ShownKey}）");
            }
            finally
            {
                AwayReportPanelUIToolkit.Close();
                RaidResultPanelUIToolkit.Close();
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                RaidResultPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(arGo);
                Object.DestroyImmediate(rrGo);
            }
        }

        // ── E 突袭历史面板 ─────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(8108);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 34f, 12), 3);
            Seconds(0.5f);
            Kill(Raiders(g), tUnit);
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            RaidResultRecord r = RaidResultService.LatestEnded(s);
            // 导演历史里一条没有在家园展开攻城的突袭（测试捷径：例如中途被拦截全灭 / 取消的计划，正式进历史的路径由 FgRaidDirectorSelfCheck 覆盖）。
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            d.History = d.History.Append(new RaidHistoryRecord { PlanId = "plan-rr-cancel", Wave = 77, Faction = "silent", Level = 1, EndTick = GameClock.Ticks + 1, EndReason = RaidDirectorService.EndCancelled }).ToArray();
            VisualElement root = F.MountUxml(UiKitFolder + "RaidResultPanel.uxml", out GameObject go);
            GameObject defGo = null;
            RaidResultPanelUIToolkit.InWorldOverrideForTests = true;
            var located = new List<Vector3>();
            NotificationCenter.LocateHandler = (string region, Vector3 pos, out string failureKey) =>
            {
                failureKey = null;
                located.Add(pos);
                return true;
            };
            try
            {
                RaidResultPanelUIToolkit panel = go.AddComponent<RaidResultPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type?.Id == "raid_result");
                bool opened = note != null && NotificationCenter.HasOpenHandler(note) && NotificationCenter.TryOpen(note);
                panel.Refresh();
                string rows = string.Join("\n", Enumerable.Range(0, panel.VisibleRowCount).Select(panel.RowText));
                bool shown = opened && RaidResultPanelUIToolkit.IsOpen && panel.PanelVisible && r != null && panel.ShownKey == "R" + r.Serial && panel.ChoiceCount == 2
                             && panel.SummaryText.Contains(RaidResultService.OutcomeText(r)) && rows.Contains(GameText.Get("raid.result.sec.wreck"))
                             && rows.Contains(GameText.Get("raid.result.sec.contrib")) && !GameText.ContainsMarker(panel.SummaryText + rows)
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidResultFirstOpen);
                int locIdx = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.Line(i)?.Action == AwayLineAction.Locate);
                bool clicked = panel.Line(locIdx)?.Action == AwayLineAction.Locate && panel.Click(locIdx) && located.Count == 1 && !RaidResultPanelUIToolkit.IsOpen;
                Expect(shown && clicked,
                    $"E1 点“突袭结算”通知 → 突袭历史面板选中这一份（“{panel.SummaryText}”，{panel.VisibleRowCount} 行，共 {panel.ChoiceCount} 项）；点可定位的行镜头飞过去、面板收起（{clicked}）");

                RaidResultPanelUIToolkit.Open();
                int hIndex = RaidResultService.HistoryEntries(s).FindIndex(e => e.History != null && e.History.PlanId == "plan-rr-cancel");
                PickForTests(panel.PickField, hIndex);
                panel.Refresh();
                bool historyOnly = panel.ShownKey == "Hplan-rr-cancel" && panel.VisibleRowCount == 1
                                   && panel.RowText(0) == GameText.Format("raid.result.no_siege", GameText.Get("raid.end.cancelled"));
                bool wreck = panel.PickWreckForTests(RaidResultService.RouteStore) && RaidResultService.StateOf(s).WreckRouting == RaidResultService.RouteStore
                             && panel.MessageText == GameText.Format("raid.result.panel.wreck_set", RaidResultService.RouteText(RaidResultService.RouteStore));
                Expect(historyOnly && wreck,
                    $"E2 只有导演历史的一项写“{(panel.VisibleRowCount > 0 ? panel.RowText(0) : string.Empty)}”；“残骸去向”下拉选中即生效（{wreck}：{panel.MessageText}）");
                RaidResultPanelUIToolkit.Close();
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "RaidResultPanel.uxml", "RaidResultWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("RaidResultRoot")?.RemoveFromClassList("uk-hidden");
                });
                string pauseUxml = File.ReadAllText(UiKitFolder + "PauseMenu.uxml");
                string defUxml = File.ReadAllText(UiKitFolder + "DefenseOverviewPanel.uxml");
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL") && pauseUxml.Contains("PauseRaidHistory") && defUxml.Contains("DefOvHistory"),
                    "E3 突袭历史面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]) + "；入口：暂停菜单“突袭历史”、防御总览“突袭历史”按钮");

                // E4（复修 P2）：防御总览（真 UXML）的“突袭历史”按钮真点一次 → 突袭历史面板打开、默认选中最新一份。
                VisualElement defRoot = F.MountUxml(UiKitFolder + "DefenseOverviewPanel.uxml", out defGo);
                DefenseOverviewPanelUIToolkit.InWorldOverrideForTests = true;
                DefenseOverviewPanelUIToolkit dp = defGo.AddComponent<DefenseOverviewPanelUIToolkit>();
                dp.BindView(defRoot);
                bool pressed = ClickButton(dp.HistoryButton);
                panel.Refresh();
                string newest = RaidResultService.HistoryEntries(s).FirstOrDefault().Key;
                Expect(pressed && RaidResultPanelUIToolkit.IsOpen && panel.PanelVisible && !string.IsNullOrEmpty(newest) && panel.ShownKey == newest,
                    $"E4 防御总览“{dp.HistoryButton?.text}”按钮（真点击）→ 突袭历史面板打开、选中最新一项（{panel.ShownKey}）");
            }
            finally
            {
                RaidResultPanelUIToolkit.Close();
                RaidResultPanelUIToolkit.InWorldOverrideForTests = false;
                DefenseOverviewPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                if (defGo != null)
                {
                    Object.DestroyImmediate(defGo);
                }
            }
        }

        private static bool PickForTests(DropdownField field, int index) => RulesPanelUIToolkit.PickForTests(field, index);

        /// <summary>真点击一个按钮（走 Clickable 的点击处理，与玩家点击同一条回调；与 FgAwayReportSelfCheck 同一写法）。</summary>
        private static bool ClickButton(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        // ── F 失败（FGR-DEF-053）与预警自动存档 ─────────────────────────────────

        private static void CheckCoreLostAndAutosave()
        {
            CampaignState s = NewWorld(8109);
            // 预警自动存档：正式路径——剧情突袭排定 → 到预警时刻（导演 Warn）请求帧末自动存档一次（同一波只存一次）。
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if (GameClock.Ticks < grace)
            {
                GameClock.SkipForTests(s, grace - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
            bool scheduled = p != null && StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            if (scheduled && p.WarnTick - 2 > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, p.WarnTick - 2 - GameClock.Ticks);
            }
            bool warned = scheduled && StepUntil(() => p.State >= RaidDirectorService.StateWarned, 30);
            int pending = RaidResultService.PendingAutosaveWave;
            bool saved = RaidResultService.FlushAutosave(s);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            string writtenAt = meta.WrittenAtUtc;
            RaidResultService.OnRaidWarned(s, p); // 同一波再发一次预警（读档 / 合并后重发）：不再存
            bool once = RaidResultService.PendingAutosaveWave == 0;
            Expect(warned && pending == p.Wave && saved && meta.State == CampaignSlotState.Ready && s.LastSaveReason == SaveReason.RaidWarning
                   && RaidResultService.StateOf(s).AutosavedWaves.Contains(p.Wave) && once,
                $"F1 突袭预警发出时请求自动存档（第 {pending} 波），帧末写盘（槽位 {Slot}，原因 {s.LastSaveReason}）；同一波不再存（{once}）——失败页有一个突袭前的安全档");

            // 攻城真实打空核心（远征 / 无人观察与否不影响，FgHomeRaidAlertSelfCheck N3 / N4）：核心只剩 40 耐久，一支突击部队在旁边展开。
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            core.Health = 40f;
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 14f, 3), 6);
            bool lost = StepUntil(() => HomeValleySoftlockGuard.IsCoreDestroyed(s), 240);
            RaidResultRecord r = RaidResultService.All(s).FirstOrDefault(x => x.GroupId == g.GroupId);
            RaidResultState st = RaidResultService.StateOf(s);
            bool settled = lost && st.CoreLost && r != null && r.Outcome == RaidResultService.OutcomeCoreLost && r.EndTick >= 0 && r.Timeline.Any(t => t.Kind == "core_lost")
                           && st.CoreLostResult == r.Serial && ReactionAttribution.Current(s, HomeValleyLayout.RegionId, false) == null
                           && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstCoreLost);
            Expect(settled,
                $"F2 FGR-DEF-053：攻城把归还核心打到耐久下限 = 被摧毁（第 {st.CoreLostTick} 步，内核读数经攻城对账判定），进行中的结算按“归还核心被摧毁”结束、过程写明，反应归因场次结束");

            int blocked0 = CampaignAutoSaveService.BlockedSaves;
            SaveResult auto = CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete);
            SaveResult manual = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            RaidResultService.OnRaidWarned(s, new RaidPlanRecord { Wave = 999, TargetKind = RaidDirectorService.TargetHome });
            bool noPending = RaidResultService.PendingAutosaveWave == 0;
            bool unchanged = CampaignSaveService.GetSlotMetadata(Slot).WrittenAtUtc == writtenAt;
            Expect(auto.Outcome == SaveOutcome.CampaignLost && manual.Outcome == SaveOutcome.CampaignLost && CampaignAutoSaveService.BlockedSaves == blocked0 + 2 && noPending && unchanged,
                $"F3 负向：核心被摧毁之后自动存档与“保存并返回主菜单”都被拒（{auto.Outcome} / {manual.Outcome}：“{auto.Message}”），新的预警也不再请求存档；槽位里仍是被摧毁之前的那一份（{unchanged}）");

            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FailureUxml).CloneTree();
            var go = new GameObject("__FgRaidResultFailure") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                HomeValleyFailureUIToolkit page = go.AddComponent<HomeValleyFailureUIToolkit>();
                page.BindView(tree);
                page.Apply(true);
                string faction = WorldTransitSystem.OriginName(r?.Faction);
                bool ui = page.Shown && page.CauseText.Contains(faction) && page.CauseText.Contains(RaidResultService.WaveLabel(r)) && page.LastSaveText.Contains("槽位")
                          && page.LoadButton != null && page.LoadButton.enabledSelf && page.LoadButton.text == GameText.Get("failure.load")
                          && page.MenuButton.text == GameText.Get("failure.menu") && !GameText.ContainsMarker(page.CauseText + page.LastSaveText);
                Expect(ui, $"F4 失败页（沿用 ER7-FAIL-01）：死因“{page.CauseText}”、最近安全自动档“{page.LastSaveText}”，“读取最近自动存档”可点、“返回主菜单”在旁边");
                page.Apply(false);
            }
            finally
            {
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }

            // 读档（失败页按钮走 GameRoot.ReloadActiveSlot → 同一个恢复编排；Play 下由冒烟真实点击覆盖）：读回的是预警时的安全档——核心完好、这一波还在、不会再存一次。
            CampaignState l = RestoreSlot(out string fail);
            RaidPlanRecord lp = l != null ? RaidDirectorService.FindPlan(l, p.PlanId) : null;
            bool restored = l != null && !HomeValleySoftlockGuard.IsCoreDestroyed(l) && !RaidResultService.IsCoreLost(l) && lp != null && lp.State >= RaidDirectorService.StateWarned
                            && RaidResultService.StateOf(l).AutosavedWaves.Contains(p.Wave);
            Expect(restored, $"F5 读取最近自动存档：回到这一波预警时（核心完好、计划 {lp?.PlanId} 状态 {lp?.State}、已存过这一波）{(fail != null ? "；失败：" + fail : string.Empty)}");
        }

        // ── G / H / I / J 存读档、倍速、观察一致、种子集 ─────────────────────────────

        /// <summary>一场真实的攻城：两座炮塔、一支 6 台的突袭在旁边展开，炮塔真实开火击毁（残骸、搬运、贡献全走正式流程）；跑 <paramref name="seconds"/> 游戏秒。</summary>
        private static CampaignState RaidScenario(int seed, bool observe, float seconds, int routing = RaidResultService.RouteBench)
        {
            CampaignState s = NewWorld(seed, observe);
            RaidResultService.SetWreckRouting(s, routing);
            Vector2 at = OutsidePoint(s, 34f, seed % 16);
            Vector2 c = CoreCenter(s);
            Vector2 dir = (at - c).normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            PlaceNear(s, L, c + dir * 14f);
            PlaceNear(s, L, c + dir * 12f + side * 6f);
            PlaceNear(s, L, c + dir * 12f - side * 6f);
            Raid(s, at, 4, elites: 1);
            Seconds(seconds);
            return s;
        }

        private static string TurretDiag(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (TurretRecord t in TurretService.All(s))
            {
                TurretService.TryGetReadout(s, t.BuildingId, out TurretReadout ro);
                BuildingRecord b = HomeGridService.FindBuilding(s, t.BuildingId);
                sb.Append($"[{t.BuildingId} 有效 {ro.Valid} 单位 {ro.HasUnit} 电 {b?.PowerState} 击毁 {t.KillCount} 状态 {ro.Status.Reason}]");
            }
            var ids = new List<int>();
            Site?.SiegeRaiderIds(0, ids);
            foreach (int id in ids.Take(4))
            {
                if (Site.TryGetSiegeRaider(id, out Vector2 pos, out _, out _, out float hp, out float max))
                {
                    sb.Append($"(敌 {hp:F0}/{max:F0} 距核心 {Vector2.Distance(pos, CoreCenter(s)):F0})");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// G FGT-DEF-009（复修）：三条路逐位对照——①不存档一路跑（基线）；②存档后不读档接着跑（存档对模拟没有副作用）；③读档后接着跑。
        /// 两个时刻（攻城中 6 秒、读档后又跑 15 秒的击毁后 / 残骸搬运中），签名含结算、残骸、搬运单、攻城域、内核状态哈希与每座建筑的记录耐久 / 最近挨打的步。
        /// </summary>
        private static void CheckSaveLoad()
        {
            // ① 不存档基线：同一场攻城一路跑到 6 / 21 / 36 游戏秒。
            CampaignState b = RaidScenario(8111, true, 6f);
            string base6 = Signature(b);
            Seconds(15f);
            string base21 = Signature(b);
            Seconds(15f);
            string base36 = Signature(b);
            string[] baseAt = { base6, base21 };
            string[] baseAfter = { base21, base36 };

            CampaignState s = RaidScenario(8111, true, 6f);
            var lines = new List<string>();
            bool all = true;
            for (int moment = 0; moment < 2; moment++)
            {
                string before = Signature(s);
                SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                string afterSave = Signature(s);
                Seconds(15f);
                string continuous = Signature(s);
                CampaignState l = RestoreSlot(out string fail);
                string loaded = l != null ? Signature(l) : "读档失败：" + fail;
                if (l != null)
                {
                    Seconds(15f);
                }
                string after = l != null ? Signature(l) : string.Empty;
                bool ok = save.Success && before == baseAt[moment] && afterSave == before && loaded == before && continuous == baseAfter[moment] && after == baseAfter[moment];
                all &= ok;
                lines.Add($"第 {moment + 1} 次（{(moment == 0 ? "攻城中" : "击毁后、残骸搬运中")}）{(ok ? "三条路一致" : $"（存档 {save.Outcome}）\n基线 / 存前：{Diff(baseAt[moment], before)}\n存前 / 存后：{Diff(before, afterSave)}" + $"\n存前 / 读后：{Diff(before, loaded)}\n基线 / 存档后续跑：{Diff(baseAfter[moment], continuous)}\n基线 / 读档后续跑：{Diff(baseAfter[moment], after)}")}");
                s = l ?? s;
            }
            RaidResultRecord any = RaidResultService.All(s).FirstOrDefault();
            bool real = any != null && any.Killed > 0 && any.Contrib.Any(c => c.Kind == RaidResultService.KindTurret) && !HomeValleySoftlockGuard.IsCoreDestroyed(s);
            Expect(all && real,
                $"G FGT-DEF-009 突袭中途任意时刻存读：两个时刻真文件存读档，存档那一刻签名不变（存档对模拟无副作用）、读回与存前逐位一致；之后 15 游戏秒，" +
                $"“存档后接着跑”“读档后接着跑”都与“不存档一路跑”的基线逐位一致（结算 / 残骸 / 搬运单 / 攻城 / 内核哈希 / 建筑记录耐久，炮塔真实击毁 {any?.Killed} 台）：" +
                string.Join("；", lines) + (all && real ? string.Empty : "\n诊断：" + TurretDiag(s)));
        }

        /// <summary>修复测试的场景：攻城中一座炮塔、一段屏障被打掉一半（经 BuildingOps.ApplyDamage 正式入口），玩家下了维修单（机器带维修件上门）。</summary>
        private static CampaignState RepairScenario(out string turretId, out string barrierId)
        {
            CampaignState s = RaidScenario(8117, true, 1f);
            TurretRecord tr = TurretService.All(s).FirstOrDefault();
            turretId = tr?.BuildingId;
            BuildingRecord barrier = Place(s, DefenseCatalog.BarrierT1, 6f, 24f);
            barrierId = barrier?.BuildingId;
            WorldSimulation.StepMany(2);
            HomeInventory.Add(s, ItemCatalog.TryGet(BuildingOps.RepairKitId, out ItemDef kit) ? kit : null, 40);
            foreach (string id in new[] { turretId, barrierId })
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, id);
                if (b != null)
                {
                    BuildingOps.ApplyDamage(s, id, BuildingOps.MaxDurability(b.BuildingTypeId) * 0.5f);
                    BuildingOps.TryOrderRepair(s, id, out _);
                }
            }
            return s;
        }

        /// <summary>逐步推进到有一张维修单完工（机器维修完工 = 记录回满，立即推给内核）——存档点就在完工的这一步之后、下一次对账之前。</summary>
        private static int StepToRepairDone(CampaignState s, string turretId, string barrierId)
        {
            int steps = 0;
            for (; steps < GameClock.StepHz * 400; steps++)
            {
                if (HomeValleyWorkOrders.FindActiveRepair(s, turretId) == null || HomeValleyWorkOrders.FindActiveRepair(s, barrierId) == null)
                {
                    break;
                }
                WorldSimulation.StepMany(1);
            }
            return steps;
        }

        /// <summary>存档点之后、下一次对账之前对炮塔和屏障的记录直接改写（回满）——不经即时推送的写法（例如旧路径 / 以后新加的写入点），专测读档后“上次推送值”补回。</summary>
        private static void RawRepairAfterSave(CampaignState s, string turretId, string barrierId)
        {
            foreach (string id in new[] { turretId, barrierId })
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, id);
                if (b != null)
                {
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
        }

        /// <summary>
        /// G2 FGT-DEF-009（复修 P1：炮塔与防御建筑的同一条读档分叉）：攻城中炮塔与屏障挨打、机器维修完工的那一步存档；存档后、下一次对账前再把炮塔和屏障的记录修满。
        /// 三条路（不存档 / 存档后接着跑 / 读档后接着跑）10 游戏秒后逐位一致——读档后炮塔与屏障的“上次推送值”按记录补回，修满的记录照样推给内核。
        /// </summary>
        private static void CheckRepairSaveLoad()
        {
            CampaignState b = RepairScenario(out string tId, out string wId);
            int steps = StepToRepairDone(b, tId, wId);
            string baseAt = Signature(b);
            RawRepairAfterSave(b, tId, wId);
            Seconds(10f);
            string baseAfter = Signature(b);
            float baseTurret = TurretService.DurabilityOf(b, HomeGridService.FindBuilding(b, tId));
            float baseBarrier = DefenseService.DurabilityOf(b, HomeGridService.FindBuilding(b, wId));

            CampaignState s = RepairScenario(out _, out _);
            int steps2 = StepToRepairDone(s, tId, wId);
            string before = Signature(s);
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string afterSave = Signature(s);
            RawRepairAfterSave(s, tId, wId);
            Seconds(10f);
            string continuous = Signature(s);
            CampaignState l = RestoreSlot(out string fail);
            string loaded = l != null ? Signature(l) : "读档失败：" + fail;
            if (l != null)
            {
                RawRepairAfterSave(l, tId, wId);
                Seconds(10f);
            }
            string after = l != null ? Signature(l) : string.Empty;
            bool same = save.Success && steps == steps2 && before == baseAt && afterSave == before && loaded == before && continuous == baseAfter && after == baseAfter;
            bool real = !string.IsNullOrEmpty(tId) && !string.IsNullOrEmpty(wId) && steps < GameClock.StepHz * 400 && baseTurret > 0f && baseBarrier > 0f;
            Expect(same && real,
                $"G2 FGT-DEF-009 炮塔与屏障：攻城中挨打（经 ApplyDamage 从内核读数扣、立即推回）→ 机器维修完工那一步（第 {steps} 步）存档 → 存档后、下一次对账前把两者记录修满；" +
                $"10 游戏秒后三条路逐位一致（炮塔 {baseTurret:0.#}、屏障 {baseBarrier:0.#}）" +
                (same ? string.Empty : $"\n（存档 {save.Outcome}，步数 {steps}/{steps2}）\n基线 / 存前：{Diff(baseAt, before)}\n存前 / 存后：{Diff(before, afterSave)}\n存前 / 读后：{Diff(before, loaded)}" +
                                       $"\n基线 / 存档后续跑：{Diff(baseAfter, continuous)}\n基线 / 读档后续跑：{Diff(baseAfter, after)}"));
        }

        /// <summary>
        /// H2（复修 P1：预警自动存档 × 倍速 / 靶场）：攻城中途来了一次突袭预警——自动存档推迟到攻城结束（帧末照常调 FrameUpdate），0.5x / 3x 与没有预警的 1x 逐位一致；
        /// 攻城结束后的第一个帧末写盘；负向：靶场测试进行中来预警，测试不被“已存档”结束，存档等测试结束后才写。
        /// </summary>
        private static string RunWarnedSiege(float speed, bool warn, out bool deferredDuring, out bool savedAfter, out int deferrals)
        {
            CampaignState s = RaidScenario(8118, true, 1f);
            int saves0 = RaidResultService.AutosaveCount;
            int defer0 = RaidResultService.AutosaveDeferrals;
            if (warn)
            {
                RaidResultService.OnRaidWarned(s, new RaidPlanRecord { Wave = 950, TargetKind = RaidDirectorService.TargetHome });
            }
            deferredDuring = true;
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                bool sieging = SiegeService.StateOf(s)?.TheaterActive ?? false;
                RaidResultService.FrameUpdate(); // 与 GameRoot.Update 帧末同一调用
                if (sieging && RaidResultService.AutosaveCount != saves0)
                {
                    deferredDuring = false;
                }
                frames++;
            }
            GameClock.SetSpeed(1f);
            string sig = Signature(s);
            deferrals = RaidResultService.AutosaveDeferrals - defer0;
            // 攻城结束后的第一个帧末写盘。
            for (int i = 0; i < 60 * 600 && (SiegeService.StateOf(s)?.TheaterActive ?? false); i++)
            {
                WorldSimulation.Frame(1f / 60f);
                RaidResultService.FrameUpdate();
            }
            RaidResultService.FrameUpdate();
            savedAfter = !warn || (RaidResultService.AutosaveCount == saves0 + 1 && RaidResultService.PendingAutosaveWave == 0 && s.LastSaveReason == SaveReason.RaidWarning
                                   && RaidResultService.StateOf(s).AutosavedWaves.Contains(950));
            return sig;
        }

        private static void CheckAutosaveDeferred()
        {
            string plain = RunWarnedSiege(1f, false, out _, out _, out _);
            string slow = RunWarnedSiege(0.5f, true, out bool d1, out bool s1, out int n1);
            string fast = RunWarnedSiege(3f, true, out bool d2, out bool s2, out int n2);
            Expect(d1 && d2 && n1 > 0 && n2 > 0 && s1 && s2 && slow == plain && fast == plain,
                $"H2 攻城中途来预警：自动存档推迟（攻城中一次也没写，推迟 {n1} / {n2} 帧），攻城结束后的第一个帧末写盘（{s1} / {s2}，原因 RaidWarning）；" +
                $"0.5x / 3x 带预警与 1x 不带预警跑同样 30 游戏秒逐位一致" + (slow == plain && fast == plain ? string.Empty : $"\n0.5x：{Diff(plain, slow)}\n3x：{Diff(plain, fast)}"));

            // 负向：靶场测试进行中来预警。
            CampaignState w = NewWorld(8119);
            BuildingRecord turret = Place(w, L, needPower: true);
            BuildingRecord range = PlaceRange(w);
            string bp = turret != null ? TurretService.Find(w, turret.BuildingId)?.BlueprintId : null;
            RangeOpResult pr = range != null ? TestRangeService.ProjectBlueprint(w, range.BuildingId, bp) : default;
            bool running = range != null && pr.Success && TestRangeService.IsRunning(range.BuildingId);
            int hist0 = w.Research?.Range?.History?.Length ?? 0;
            int saves0 = RaidResultService.AutosaveCount;
            RaidResultService.OnRaidWarned(w, new RaidPlanRecord { Wave = 960, TargetKind = RaidDirectorService.TargetHome });
            for (int i = 0; i < 30; i++)
            {
                WorldSimulation.Frame(1f / 60f);
                RaidResultService.FrameUpdate();
            }
            bool kept = running && TestRangeService.IsRunning(range.BuildingId) && (w.Research?.Range?.History?.Length ?? 0) == hist0
                        && RaidResultService.PendingAutosaveWave == 960 && RaidResultService.AutosaveCount == saves0 && RaidResultService.AutosaveDeferReason(w) == "test_range";
            if (range != null)
            {
                TestRangeService.EndTest(w, range.BuildingId);
            }
            RaidResultService.FrameUpdate();
            bool savedLater = RaidResultService.AutosaveCount == saves0 + 1 && RaidResultService.PendingAutosaveWave == 0 && w.LastSaveReason == SaveReason.RaidWarning;
            Expect(kept && savedLater,
                $"H3 负向：靶场测试进行中（投影 {pr.Success}）来预警——测试照常进行、不被“已存档”结束（{kept}），玩家结束测试后的帧末才写这次自动存档（{savedLater}）");
        }

        /// <summary>测试捷径：在接得上电网的空地上登记一座建成的靶场（与 FgTestRangeSelfCheck 同一写法，由近到远按种子地形找，B25）。</summary>
        private static BuildingRecord PlaceRange(CampaignState s)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (float d = 6f; d <= 40f; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, TestRangeCatalog.TypeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = F.Built(s, TestRangeCatalog.TypeId, "rr_range", c);
                    b.Health = BuildingOps.MaxDurability(TestRangeCatalog.TypeId);
                    HomeValleyPowerGrid.Recompute(s);
                    if (HomeValleyPowerGrid.IsConnected(s, b.BuildingId) && b.PowerState == BuildingPowerState.Powered)
                    {
                        return b;
                    }
                    s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                    HomeGridService.MapFor(s);
                    HomeValleyPowerGrid.Recompute(s);
                }
            }
            Fail("测试准备：家园里找不到接得上电网、能放靶场的空地");
            return null;
        }

        private static string RunTiming(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = RaidScenario(seed, observe, 1f);
            pausedHeld = true;
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Signature(s);
                for (int i = 0; i < 90; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Signature(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 40;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Signature(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunTiming(8112, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{Short(snap)}\n参照：{Short(reference)}";
                }
                same &= snap == reference;
            }
            Expect(paused && same, "H 暂停中（90 帧）结算、残骸、搬运单都不动；0.5x / 1x / 2x / 3x 跑同样的 40 游戏秒攻城（炮塔击毁、残骸落地、机器搬运、去向检查）逐位一致" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunTiming(8113, true, 1f, false, out _);
            string unseen = RunTiming(8113, false, 1f, false, out _);
            Expect(seen == unseen, "I 同一场攻城在观察与不观察家园时跑 40 游戏秒，结算与残骸处理逐位一致（FGR-BASE-021：远征时家园照常结算）"
                                   + (seen == unseen ? string.Empty : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
        }

        private static void CheckSeedSet()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 7101, 7202 })
            {
                CampaignState s = NewWorld(seed);
                BuildingRecord turret = Place(s, L, needPower: true);
                TransitGroupRecord g = Raid(s, OutsidePoint(s, 30f, seed % 16), 4);
                Seconds(0.5f);
                Kill(Raiders(g), TurretUnit(s, turret));
                Seconds(SiegeCatalog.SyncSeconds + 0.3f);
                RaidResultRecord r = RaidResultService.LatestEnded(s);
                bool ok = r != null && r.Killed == 4 && WreckPiles(s).Count > 0 && RaidResultService.Ranked(r).Count == 1;
                all &= ok;
                lines.Add($"种子 {seed}：结算 {r?.Outcome}、击毁 {r?.Killed}、残骸 {WreckPiles(s).Count} 堆");
            }
            Expect(all, "J B25 种子测试集：两个种子的家园地形 / 布局不同，结算、残骸落地、贡献都成立（不依赖固定坐标）：" + string.Join("；", lines));
        }

        // ── P 性能 ────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(8114);
            BuildingRecord turret = Place(s, L, needPower: true);
            int tUnit = TurretUnit(s, turret);
            TransitGroupRecord g = Raid(s, OutsidePoint(s, 40f, 1), 150, elites: 10);
            Seconds(0.5f);
            long events0 = RaidResultService.KillEvents;
            double ms0 = RaidResultService.KillMs;
            Kill(Raiders(g), tUnit);
            Seconds(2f); // 玩法事件每步按上限排空、跨步保留：等全部处理完
            long events = RaidResultService.KillEvents - events0;
            double avg = events > 0 ? (RaidResultService.KillMs - ms0) / events : 0;
            Seconds(SiegeCatalog.SyncSeconds + 0.3f);
            RaidResultState st = RaidResultService.StateOf(s);
            for (int i = 0; i < 40; i++)
            {
                HomeInventory.Add(s, ItemCatalog.TryGet(AnalysisCatalog.WreckId, out ItemDef wd) ? wd : null, 1);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int made = RaidResultService.RouteWrecks(s, st);
            double routeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double limit = RaidResultService.PerfMs;
            PerfLines.Add($"同一步 {events} 次击毁事件（150 台，精英 10）：热更层平均每次 {avg:F4} ms（上限 {limit} ms，最长 {RaidResultService.MaxKillMs:F3} ms）；一次残骸去向检查开出 {made} 张送货单 {routeMs:F3} ms");
            Expect(events == 150 && made > 0, $"P1 同一步 150 次击毁事件全部进结算（{events}），残骸去向检查开出送货单（{made}）");
            PerfGate.Expect(true, $"P2 击毁事件的热更层开销：平均 {avg:F4} ms / 次（上限 {limit} ms，Editor Mono JIT；真机 FG15-SYS-02）",
                new[] { PerfGate.Le(avg, limit, "击毁事件平均 ms") }, (ok, msg) => Expect(ok, msg), Line);
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
