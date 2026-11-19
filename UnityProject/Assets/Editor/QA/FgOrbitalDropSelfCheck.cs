using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-04 轨道坠落的自动验收（FG10 FGR-EVT-030 / 031，FGT-EVT-005；FG19 FGR-EMG-111 / 113 坠落残骸；承接 DEBT-FG12EVT01-02 的坠落部分）。
    /// 全部起真实世界（家园星球战斗内核 + 事件导演 + 真实 orbital_drop 处理器），走 <c>WorldSimulation.StepMany / Frame</c>；落点由处理器按种子选，
    /// 需要把落点放在被测建筑 / 护盾上的段用导演的正式入口 <c>EventDirectorService.Request(at)</c>（与推迟后补上同一条路径：落点合格才沿用）。
    /// D 数据；A1 FGT-EVT-005 / B25 多种子 × 多序号落点离核心 ≥ 12 格、可走、确定；A2 负向“请求落在核心旁”被改到合格处、不可走格不合格；
    /// A3 第 2 幕门槛与“事件频率：关闭”；B1 正常一次：60 秒预警（通知 / 地图 / 小地图 / 主视图）→ 撞击（圈内建筑重创、圈外不受、圈内单位受击、核心不受）
    /// → 坠落残骸（内核实体、长期留存、挡弹可被点选）→ 奖励（材料落地、探针按种子）；B2 负向“圈内有关键建筑”：照常受损、被毁留虚影、自动重建规则下修复单；
    /// C1 护盾吸收一部分；T1 暂停与 0.5x～3x；T2 观察一致；S1 预警中存读档；P1 性能。已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOrbitalDropSelfCheck）。
    /// </summary>
    public static class FgOrbitalDropSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string KeyType = HomeValleyLayout.BuildingTypeGenerator2; // 关键建筑：发电机
        private const string OtherType = "power_pole";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq = 700;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly List<string> Hooks = new List<string>();

        [MenuItem("QA/自检/FG 轨道坠落")]
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
            Line("\n[轨道坠落] 落点远离核心（多种子）、60 秒预警（通知 / 地图 / 小地图 / 主视图）、撞击（建筑 / 单位 / 护盾）、坠落残骸、奖励、负向、倍速 / 观察 / 存读档（FG12-EVT-04）");
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
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgorbital-selfcheck-" + Guid.NewGuid().ToString("N"));
            Action<string> onHook = id => Hooks.Add(id);
            GuidanceHooks.Touched += onHook;
            GameObject root = null;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                ResearchCatalog.Reload();
                GameClock.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "处理器 / 建筑结算在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），单位伤害 / 护盾 / 残骸在 Main/Sim（Editor 下 Burst 未必开）；真机另测（FG15-SYS-02）");
                root = new GameObject("OrbitalDropSelfCheckRoot");
                GameObject viewRoot = root;

                Step(CheckData);
                Step(CheckLandingSeeds);
                Step(CheckLandingNegative);
                Step(CheckGates);
                Step(() => CheckNormalImpact(viewRoot.transform));
                Step(CheckProbe);
                Step(CheckKeyBuildingRebuild);
                Step(CheckShield);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoadWarned);
                Step(CheckSaveLoadAfterImpact);
                Step(CheckImpactHomeUnloaded);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"轨道坠落自检抛异常：{e}");
            }
            finally
            {
                GuidanceHooks.Touched -= onHook;
                OrbitalDropViews.Clear();
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                EventDirectorService.ResetForTests();
                OrbitalDropService.ResetSessionState();
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                ResearchService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
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
            Line($"  · [轨道坠落] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        private static long Sec(double s) => GameClock.TicksFor(s);
        private static void StepSec(double s) => WorldSimulation.StepMany((int)Math.Max(1, Sec(s)));

        private static bool StepUntil(Func<bool> done, double maxSeconds)
        {
            long end = GameClock.Ticks + Sec(maxSeconds);
            while (GameClock.Ticks < end)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(1);
            }
            return done();
        }

        private static CombatSite Site => WorldSimulation.Home?.Combat;

        /// <summary>真实家园（星球战斗内核）+ 事件导演 + 真实 orbital_drop 处理器；战役在第 <paramref name="act"/> 幕。</summary>
        private static CampaignState World(int seed, bool observe, int act = 2)
        {
            EventDirectorService.ResetForTests();
            OrbitalDropService.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            Hooks.Clear();
            _seq = 700; // 建筑 ID 只取决于本段的登记顺序（快照比较逐字段）
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 3000);
            FgProductionSelfCheck.PowerUp(s);
            SignalCoreService.EnsureInitialized(s);
            EventDirectorService.Install();
            EventDirectorService.Register(new OrbitalDropService.Handler());
            s.Progress.Act = act;
            WorldSimulation.StepMany(2);
            return s;
        }

        private static EventDirectorInstanceRecord Inst(CampaignState s) =>
            EventDirectorService.Active(s).FirstOrDefault(r => r != null && r.EventId == OrbitalDropService.EventId);

        private static EventDirectorHistoryRecord LastHistory(CampaignState s) =>
            EventDirectorService.History(s).LastOrDefault(h => h.EventId == OrbitalDropService.EventId);

        /// <summary>请求一次（<paramref name="at"/> = 指定落点，null = 处理器按种子选）并跑到预警中。</summary>
        private static EventDirectorInstanceRecord ToWarned(CampaignState s, Vector2? at)
        {
            if (!EventDirectorService.Request(s, OrbitalDropService.EventId, at: at))
            {
                return null;
            }
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateWarned, 15);
            return Inst(s);
        }

        private static BuildingRecord Register(CampaignState s, string type, GridCell c, float hpFrac = 1f)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#od" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, 0),
                Health = BuildingOps.MaxDurability(type) * hpFrac,
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
            return r;
        }

        /// <summary>离 <paramref name="near"/> 由近到远找第一个能放 <paramref name="type"/> 的格（中心距 ∈ [from, to]）。</summary>
        private static GridCell? FreeNear(CampaignState s, string type, Vector2 near, float from, float to)
        {
            for (float d = from; d <= to; d += 0.5f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float ang = a * 15f * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.FloorToInt(near.x + Mathf.Cos(ang) * d), Mathf.FloorToInt(near.y + Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        /// <summary>离核心 [from, to] 格、可落、四周 <paramref name="clear"/> 格内能放下关键建筑的点（测试选址，按格扫，确定）。</summary>
        private static Vector2? LandingSpot(CampaignState s, float from, float to, Func<Vector2, bool> extra = null)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            for (float d = from; d <= to; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = (a * 10f + 5f) * Mathf.Deg2Rad;
                    var p = new Vector2(Mathf.Floor(core.x + Mathf.Cos(ang) * d) + 0.5f, Mathf.Floor(core.y + Mathf.Sin(ang) * d) + 0.5f);
                    if (OrbitalDropService.ValidLanding(s, p) && FreeNear(s, KeyType, p, 0f, 1f).HasValue && (extra == null || extra(p)))
                    {
                        return p;
                    }
                }
            }
            return null;
        }

        private static int SpawnEnemy(CombatSite site, Vector2 at, float hp) =>
            site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive,
                Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.6f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });

        private static int SpawnMachine(Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 400f, 400f, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static float EnemyHp(int id) => Site != null && Site.Kernel.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;
        private static float MachineHp(int logicId) => MachineRegistry.TryGetRecord(logicId, out MachineRecord m) ? m.Health : -1f;

        private static bool TryWreck(int id, out EmgWreck w)
        {
            w = default;
            CombatSite site = Site;
            for (int i = 0; site != null && i < site.Kernel.WreckCount; i++)
            {
                if (site.Kernel.TryGetWreck(i, out w) && w.Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<GroundItemRecord> Loot(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourceOrbital + ":", StringComparison.Ordinal)).ToList();

        private static List<string> EventTexts() =>
            NotificationCenter.History.Where(e => e.Type?.Id == "world_event").SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty).ToList();

        private static int Heard(CampaignState s) => IntelService.StateOf(s)?.FragmentsHeard?.Length ?? 0;

        private static WorldMapModel Map(CampaignState s, Vector2 center, double half, float canvas)
        {
            var view = new WorldMapView { CenterX = center.x, CenterY = center.y, HalfWidth = half, CanvasWidth = canvas, CanvasHeight = canvas };
            var m = new WorldMapModel();
            m.Collect(s, view, 48);
            return m;
        }

        private static bool MapShows(WorldMapModel m, EventDirectorInstanceRecord r) =>
            m.Circles.Any(c => c.Impact && Math.Abs(c.X - r.X) < 1e-3 && Math.Abs(c.Y - r.Y) < 1e-3 && Mathf.Approximately(c.Radius, OrbitalDropService.Radius) && !string.IsNullOrEmpty(c.Label))
            && m.Items.Any(i => i.Kind == WorldMapItemKind.RaidWarning && i.Id == "orbital:" + r.Serial);

        // ── D 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorDef d = EventDirectorService.Def(OrbitalDropService.EventId);
            Expect(d != null && d.Threat && d.Major && d.ActMin == 2 && d.Trigger == EventDirectorService.TriggerRandom && Math.Abs(d.WarningSeconds - 60) < 1e-6
                   && d.Reward == "fixed" && Math.Abs(d.IntervalMinDays - 3) < 1e-6 && Math.Abs(d.IntervalMaxDays - 6) < 1e-6,
                "D1 fg.TbWorldEvent orbital_drop：重大威胁、第 2 幕起导演随机（3～6 日）、预警 60 秒、奖励由处理器给（FGR-EVT-030 / 031、DEBT-FG12EVT01-02）");
            string[] tun = { "radius", "min_core_distance", "max_core_distance", "place_attempts", "building_damage_frac", "unit_damage", "impact_energy", "shield_absorb_max",
                "wreck_radius", "wreck_hp", "wreck_mass", "wreck_temp", "probe_chance" };
            string[] missingT = tun.Where(k => !GridContent.TryGetTuning("event.orbital_drop." + k, out _)).ToArray();
            string[] keys = { "orbital_drop.notify.warn", "orbital_drop.notify.impact", "orbital_drop.shield", "orbital_drop.notify.probe", "ui.map.orbital_drop",
                "event_director.name.orbital_drop", "codex.event_director.orbital_drop.body" };
            string[] missingK = keys.Where(k => !GameText.Has(k)).ToArray();
            var rows = new List<LootDropDef>();
            LootCatalog.RowsFor(LootCatalog.SourceOrbital, string.Empty, rows);
            Expect(missingT.Length == 0 && missingK.Length == 0 && Mathf.Approximately(OrbitalDropService.Radius, 3f) && Mathf.Approximately(OrbitalDropService.MinCoreDistance, 12f)
                   && rows.Count >= 3 && rows.Any(r => r.Item.Id == "rare_earth_ore") && GuidanceHooks.Known.Contains("event_director.first.orbital_drop") && GameRootRegisters(),
                $"D2 调参齐（缺 {string.Join("、", missingT)}；半径 {OrbitalDropService.Radius} 格、离核心 ≥ {OrbitalDropService.MinCoreDistance} 格 = FG10 第 15 节初值）、文本键齐（缺 {string.Join("、", missingK)}）、" +
                $"fg.TbLootDrop 来源 orbital {rows.Count} 行（含稀有材料）、第一次说明钩子、GameRoot 登记了处理器");
        }

        private static bool GameRootRegisters()
        {
            string src = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Stage/GameRoot.cs");
            return src.Contains("Register(new Campaign.Env.OrbitalDropService.Handler())");
        }

        // ── A 落点（FGT-EVT-005、B25） ───────────────────────────────────────────

        private static void CheckLandingSeeds()
        {
            int[] seeds = { 4101, 4202, 4303, 4404, 4505, 4606 };
            int total = 0, bad = 0, nondet = 0, failed = 0;
            float minD = float.MaxValue;
            var distinct = new HashSet<string>();
            var cores = new HashSet<string>();
            foreach (int seed in seeds)
            {
                CampaignState s = World(seed, false);
                Vector2 core = SignalUplinkService.CorePosition(s);
                cores.Add($"{core.x:F1},{core.y:F1}");
                for (int serial = 1; serial <= 40; serial++)
                {
                    total++;
                    if (!OrbitalDropService.PickLanding(s, serial, out Vector2 p))
                    {
                        failed++;
                        continue;
                    }
                    float d = Vector2.Distance(p, core);
                    minD = Mathf.Min(minD, d);
                    if (d < 12f || !WorldGenQuery.IsWalkableGenerated(s, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)))
                    {
                        bad++;
                    }
                    if (!OrbitalDropService.PickLanding(s, serial, out Vector2 q) || q != p)
                    {
                        nondet++;
                    }
                    distinct.Add($"{seed}:{p.x:F2},{p.y:F2}");
                }
            }
            Expect(bad == 0 && nondet == 0 && failed == 0 && distinct.Count == total && minD >= 12f,
                $"A1 FGT-EVT-005 / B25：{seeds.Length} 个种子 × 40 个实例序号共 {total} 次选落点——全部选到（失败 {failed}）、离该世界归还核心（核心建筑实际位置，{cores.Count} 种）全部 ≥ 12 格（最近 {minD:F1} 格，违规 {bad}）、" +
                $"圆心格可走、同种子同序号结果一致（不一致 {nondet}）、互不相同（{distinct.Count}）");

            // 真实导演路径：随机请求 → 处理器选点 → 实例记录的落点同样合格。
            CampaignState w = World(4707, false);
            EventDirectorInstanceRecord r = ToWarned(w, null);
            Vector2 c = SignalUplinkService.CorePosition(w);
            Expect(r != null && Vector2.Distance(new Vector2(r.X, r.Y), c) >= 12f && r.RegionId == HomeValleyLayout.RegionId,
                $"A1b 导演正式路径（请求 → Prepare）：实例落点 ({r?.X:F1}, {r?.Y:F1}) 离核心 {(r != null ? Vector2.Distance(new Vector2(r.X, r.Y), c) : 0f):F1} 格（≥ 12），落在星球表面");
        }

        private static void CheckLandingNegative()
        {
            CampaignState s = World(4808, false);
            Vector2 core = SignalUplinkService.CorePosition(s);
            // 负向：请求直接落在核心旁（2 格）→ 不合格，处理器改到合格处（“落点永远不会在归还核心附近”）。
            EventDirectorInstanceRecord r = ToWarned(s, core + new Vector2(2f, 0f));
            float d = r != null ? Vector2.Distance(new Vector2(r.X, r.Y), core) : 0f;
            Expect(r != null && d >= 12f && !OrbitalDropService.ValidLanding(s, core) && !OrbitalDropService.ValidLanding(s, core + new Vector2(11.5f, 0f)),
                $"A2 负向：请求落在核心旁 2 格 → 改到 {d:F1} 格外；核心处 / 11.5 格处都判不合格");
            // 负向：悬崖 / 水面不能当落点（按种子地形扫一圈找一格不可走的）。
            Vector2? blocked = null;
            for (int rr = 12; rr <= 60 && blocked == null; rr++)
            {
                for (int a = 0; a < 72; a++)
                {
                    var p = new Vector2(Mathf.Floor(core.x + Mathf.Cos(a * 5f * Mathf.Deg2Rad) * rr) + 0.5f, Mathf.Floor(core.y + Mathf.Sin(a * 5f * Mathf.Deg2Rad) * rr) + 0.5f);
                    if (!WorldGenQuery.IsWalkableGenerated(s, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)))
                    {
                        blocked = p;
                        break;
                    }
                }
            }
            if (blocked.HasValue)
            {
                Expect(!OrbitalDropService.ValidLanding(s, blocked.Value), $"A2b 负向：悬崖 / 水面格 ({blocked.Value.x:F1}, {blocked.Value.y:F1}) 不合格");
            }
            else
            {
                Line("  - A2b 这个种子核心周围 60 格内没有悬崖 / 水面，跳过（A1 已在 6 个种子上断言圆心格可走）");
            }
        }

        private static void CheckGates()
        {
            CampaignState s = World(4909, false, act: 1);
            EventDirectorService.Request(s, OrbitalDropService.EventId);
            StepSec(25);
            bool heldAct1 = Inst(s) == null && OrbitalDropService.Impacts == 0;
            s.Progress.Act = 2;
            bool came = StepUntil(() => Inst(s) != null, 20);
            Expect(heldAct1 && came, $"A3 FGR-EVT-030“第二幕起”：第 1 幕里请求也不来（{heldAct1}），进入第 2 幕后才开始预警（{came}）");

            CampaignState o = World(4910, false);
            EventDirectorService.SetFrequency(o, EventDirectorService.FrequencyOff);
            bool rejected = !EventDirectorService.Request(o, OrbitalDropService.EventId);
            StepSec(20);
            Expect(rejected && Inst(o) == null, "A3b 负向“事件频率：关闭”：轨道坠落不是剧情固定事件，请求被拒、不发生");
        }

        // ── B 正常一次 ─────────────────────────────────────────────────────────

        private static void CheckNormalImpact(Transform viewRoot)
        {
            CampaignState s = World(5101, true);
            CombatSite site = Site;
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            float coreHp0 = core.Health;
            Vector2? spot = LandingSpot(s, 16f, 30f, p => FreeNear(s, OtherType, p, 7f, 9f).HasValue);
            if (!spot.HasValue)
            {
                Fail("B1 测试准备：找不到合适的落点");
                return;
            }
            Vector2 at = spot.Value;
            FgProductionSelfCheck.ParkMachinesAwayFrom(site, at, 20f);
            BuildingRecord inside = Register(s, KeyType, FreeNear(s, KeyType, at, 0f, 1f).Value);
            BuildingRecord outside = Register(s, OtherType, FreeNear(s, OtherType, at, 7f, 9f).Value);
            float inMax = BuildingOps.MaxDurability(KeyType);
            float outHp0 = outside.Health;
            int enemyIn = SpawnEnemy(site, at + new Vector2(1.2f, 0f), 2000f);
            int enemyOut = SpawnEnemy(site, at + new Vector2(9f, 0f), 2000f);
            int mIn = SpawnMachine(at + new Vector2(0f, 1.5f));
            int mOut = SpawnMachine(at + new Vector2(0f, 10f));
            WorldSimulation.StepMany(3);
            float mIn0 = MachineHp(mIn), mOut0 = MachineHp(mOut);
            int wrecks0 = site.Kernel.WreckCount;
            int texts0 = EventTexts().Count;

            EventDirectorInstanceRecord r = ToWarned(s, at);
            if (r == null)
            {
                Fail("B1 没能开始预警");
                return;
            }
            long warnSpan = r.StartTick - r.WarnTick;
            string warnText = EventTexts().Skip(texts0).FirstOrDefault(t => t.Contains(Mathf.RoundToInt(r.X).ToString(CultureInfo.InvariantCulture)) && t.Contains("撞击")) ?? string.Empty;
            Expect(warnSpan == Sec(60) && Mathf.Approximately(r.X, at.x) && Mathf.Approximately(r.Y, at.y) && warnText.Length > 0 && Hooks.Contains("event_director.first.orbital_drop"),
                $"B1a 60 秒预警（{warnSpan} 步 = {Sec(60)}）：落点圈在请求处、通知写落点 / 离核心 / 倒计时（“{warnText}”）、第一次说明钩子");

            // 地图与小地图（同一模型）：落点圈 + 告警图标；主视图：火光轨迹与落点圈。
            bool strategic = MapShows(Map(s, at, 120, 900f), r);
            bool minimap = MapShows(Map(s, at + new Vector2(10f, -6f), Math.Max(16.0, GridContent.Tuning("minimap.radius_cells")), 172f), r);
            OrbitalDropViews.FrameUpdate(s, viewRoot);
            GameObject disk = OrbitalDropViews.DiskAt(0), ball0 = OrbitalDropViews.BallAt(0);
            float h0 = ball0 != null ? ball0.transform.position.y : 0f;
            StepSec(30);
            OrbitalDropViews.FrameUpdate(s, viewRoot);
            float h1 = ball0 != null ? ball0.transform.position.y : 0f;
            bool diskOk = disk != null && disk.activeSelf && Vector2.Distance(new Vector2(disk.transform.position.x, disk.transform.position.z), at) < 0.01f
                          && disk.transform.localScale.x > OrbitalDropService.Radius * 1.8f && disk.GetComponent<Collider>() == null;
            Expect(strategic && minimap && OrbitalDropViews.VisibleCount == 1 && diskOk && h1 < h0 && h0 > 40f,
                $"B1b 卡片“落点圈在地图和小地图上可见”：战略地图（{strategic}）与小地图（{minimap}）都有橙色落点圈 + 告警图标；主视图落点圆盘在落点、半径 3 格、不挡点击（{diskOk}），火球从高空落下（{h0:F0} → {h1:F0} 米）");
            bool calmBefore = inside.Health >= inMax - 0.01f && Mathf.Approximately(EnemyHp(enemyIn), 2000f) && site.Kernel.WreckCount == wrecks0;
            Expect(calmBefore, "B1c 预警期间什么都不发生（建筑满耐久、单位不受伤、没有残骸）：玩家有 60 秒撤离");

            int texts1 = EventTexts().Count;
            var sw = Stopwatch.StartNew();
            bool hit = StepUntil(() => OrbitalDropService.Impacts > 0, 35);
            double impactMs = sw.Elapsed.TotalMilliseconds;
            WorldSimulation.StepMany(4); // 外部血量机器的伤害请求下一步落到记录
            OrbitalDropViews.FrameUpdate(s, viewRoot);
            float frac = GridContent.TryGetTuning("event.orbital_drop.building_damage_frac", out float bf) ? bf : 0.8f;
            float unitDmg = GridContent.TryGetTuning("event.orbital_drop.unit_damage", out float ud) ? ud : 300f;
            float inLoss = inMax - inside.Health;
            Expect(hit && Math.Abs(inLoss - frac * inMax) < 1f && Mathf.Approximately(outside.Health, outHp0) && Mathf.Approximately(core.Health, coreHp0)
                   && OrbitalDropService.LastBuildingsHit >= 1 && Mathf.Approximately(OrbitalDropService.LastDamageMul, 1f),
                $"B1d 撞击（第 60 秒）：圈内建筑受重创（{inLoss:F0} = 最大耐久 {inMax:F0} × {frac}）、圈外建筑不受（{outside.Health:F0}）、归还核心不受（{core.Health:F0}）");
            Expect(Math.Abs(2000f - EnemyHp(enemyIn) - unitDmg) < 1f && Mathf.Approximately(EnemyHp(enemyOut), 2000f) && Math.Abs(mIn0 - MachineHp(mIn) - unitDmg) < 1f
                   && Mathf.Approximately(MachineHp(mOut), mOut0) && OrbitalDropService.LastUnitsHit >= 2,
                $"B1e 圈内单位被波及：敌人 2000 → {EnemyHp(enemyIn):F0}、己方机器 {mIn0:F0} → {MachineHp(mIn):F0}（外部血量走伤害请求）；圈外的敌人 / 机器不受（{EnemyHp(enemyOut):F0} / {MachineHp(mOut):F0}）");
            bool wreck = TryWreck(OrbitalDropService.LastWreckId, out EmgWreck w);
            Expect(wreck && site.Kernel.WreckCount == wrecks0 + 1 && math.distance(w.Pos, new double2(at.x, at.y)) < 1e-3 && Mathf.Approximately(w.Radius, 2f) && w.Hp > 0f
                   && w.Mass > 0f && w.NoSalvage == 0 && double.IsPositiveInfinity(w.Until) == false && w.Until > site.Kernel.Time + 3600 && site.Kernel.PickWreck(new double2(at.x, at.y), 0.2f) >= 0,
                $"B1f 坠落残骸（ent.wreck，FGR-EMG-113）：内核实体在落点（半径 {w.Radius} 米、耐久 {w.Hp:F0}、质量 {w.Mass}、温度 {w.Temp:F0}）、家园长期留存、可拆解（NoSalvage 0）、空间哈希可点选 / 挡弹");
            EventDirectorHistoryRecord h = LastHistory(s);
            List<GroundItemRecord> loot = Loot(s);
            bool probe = OrbitalDropService.RollsProbe(s, r.Serial);
            string impactText = EventTexts().Skip(texts0).FirstOrDefault(t => t.Contains("座建筑受创")) ?? string.Empty;
            Expect(h != null && h.Outcome == EventDirectorService.OutcomeSuccess && loot.Count >= 3 && loot.All(g => Vector2.Distance(g.Position, at) < 4f)
                   && loot.Any(g => g.ResourceType == "rare_earth_ore") && (probe ? h.RewardFragment.Length > 0 : h.RewardFragment.Length == 0) && OrbitalDropService.Rewards == 1
                   && impactText.Length > 0 && OrbitalDropViews.VisibleCount == 0,
                $"B1g 奖励（FGR-EVT-031）：事件历史 success、坠落残骸处落 {loot.Count} 堆材料（含稀有材料，派机器回收）、舰队探针按种子 {(probe ? "有 → 加密片段 " + h?.RewardFragment : "无")}；" +
                $"撞击通知（“{impactText}”）；撞击后主视图落点圈收起；撞击那一步 {impactMs:F1} ms（含 35 秒内的世界步）");
            PerfLines.Add($"撞击所在那段 StepUntil（含撞击一步）{impactMs:F1} ms");
            // B1h（修复轮 1，审查 P2）：瞬发事件只发撞击结果一条通知，导演不再发“开始（时限 …）”与成功的“结束”。
            List<string> impactTexts = EventTexts().Skip(texts1).ToList();
            int impactNotes = impactTexts.Count(t => t.Contains("座建筑受创"));
            bool directorNotes = impactTexts.Any(t => t.Contains("：开始（时限") || t.Contains("结束："));
            Expect(hit && impactNotes == 1 && !directorNotes,
                $"B1h 撞击那一步只有一条撞击通知（{impactNotes} 条），没有导演的开始 / 结束通知（{directorNotes}）：[{string.Join(" | ", impactTexts)}]");

        }

        /// <summary>FGR-EVT-031“有时是舰队探针”：按种子挑一个第一次坠落就有探针的世界，跑正式路径，加密片段真的收进资料库；探针比例按种子统计接近 probe_chance。</summary>
        private static void CheckProbe()
        {
            float chance = GridContent.TryGetTuning("event.orbital_drop.probe_chance", out float pc) ? pc : 0.35f;
            CampaignState s = null;
            int seed = 0;
            int with = 0, total = 0;
            for (int k = 0; k < 12; k++)
            {
                CampaignState w = World(5701 + k, false);
                for (int serial = 1; serial <= 40; serial++)
                {
                    total++;
                    with += OrbitalDropService.RollsProbe(w, serial) ? 1 : 0;
                }
                if (OrbitalDropService.RollsProbe(w, EventDirectorService.StateOf(w).NextSerial))
                {
                    s = w;
                    seed = 5701 + k;
                    break;
                }
            }
            if (s == null)
            {
                Fail("B3 测试准备：12 个种子里没有第一次坠落就有探针的世界");
                return;
            }
            int heard0 = Heard(s);
            int texts0 = EventTexts().Count;
            ToWarned(s, null);
            bool hit = StepUntil(() => OrbitalDropService.Impacts > 0, 75);
            EventDirectorHistoryRecord h = LastHistory(s);
            bool told = EventTexts().Skip(texts0).Any(t => t.Contains("舰队探针"));
            double ratio = with / (double)Math.Max(1, total);
            Expect(hit && h != null && h.RewardFragment.Length > 0 && Heard(s) == heard0 + 1 && OrbitalDropService.Probes == 1 && told && Math.Abs(ratio - chance) < 0.15,
                $"B3 FGR-EVT-031“有时是舰队探针（剧情伏笔）”：种子 {seed} 第一次坠落有探针 → 加密片段 {h?.RewardFragment} 收进资料库（截获 {heard0} → {Heard(s)}）、通知写明；" +
                $"按种子统计 {total} 个实例里 {ratio:P0} 有探针（probe_chance {chance}）");
        }

        private static void CheckKeyBuildingRebuild()
        {
            CampaignState s = World(5202, false);
            ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
            if (!StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord rule, out string m))
            {
                Fail("B2 测试准备：建不了自动重建规则：" + m);
                return;
            }
            StandingRuleService.TryAddTarget(s, rule.Serial, KeyType, out _);
            Vector2? spot = LandingSpot(s, 16f, 30f);
            if (!spot.HasValue)
            {
                Fail("B2 测试准备：找不到合适的落点");
                return;
            }
            Vector2 at = spot.Value;
            BuildingRecord key = Register(s, KeyType, FreeNear(s, KeyType, at, 0f, 1f).Value, hpFrac: 0.5f); // 已经受过损的关键建筑
            EventDirectorInstanceRecord r = ToWarned(s, at);
            bool hit = StepUntil(() => OrbitalDropService.Impacts > 0, 70);
            bool ruined = key.ConstructionState == BuildingConstructionState.Damaged;
            bool ordered = StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, key.BuildingId) != null || key.ConstructionState == BuildingConstructionState.Operational, 60);
            Expect(r != null && hit && ruined && ordered && LastHistory(s)?.Outcome == EventDirectorService.OutcomeSuccess,
                $"B2 负向“落点圈内有关键建筑”（FG10 第 11 节：按规则受损，自动重建规则照常生效）：半血的发电机不被豁免、被砸毁留虚影（{ruined}）、自动重建规则给它下修复单（{ordered}）、事件照常结束");
        }

        // ── C 护盾 ─────────────────────────────────────────────────────────────

        private static void CheckShield()
        {
            CampaignState s = World(5303, false);
            CombatSite site = Site;
            GridCell? g = FgProductionSelfCheck.FindFree(s, DefenseCatalog.ShieldTypeId, 14f, 26f);
            if (!g.HasValue)
            {
                Fail("C1 测试准备：放不下护盾发生器");
                return;
            }
            BuildingRecord gen = Register(s, DefenseCatalog.ShieldTypeId, g.Value);
            StepSec(25); // 充能 → 展开 → 回满
            DefenseRecord rec = DefenseService.Find(s, gen.BuildingId);
            bool up = rec != null && site.TryGetShield(rec.Serial, out CombatShield sh0) && sh0.Active == 1 && sh0.Hp > 0f;
            CombatShield before = default;
            if (rec != null)
            {
                site.TryGetShield(rec.Serial, out before);
            }
            Vector2? spot = LandingSpot(s, 12f, 40f, p => Vector2.Distance(p, gen.Position) >= 5f && Vector2.Distance(p, gen.Position) <= before.Radius - 1f);
            if (!up || !spot.HasValue)
            {
                Fail($"C1 测试准备：护盾没展开（{up}，护盾值 {before.Hp:F0}）或护盾罩里找不到落点（{spot.HasValue}）");
                return;
            }
            Vector2 at = spot.Value;
            BuildingRecord b = Register(s, KeyType, FreeNear(s, KeyType, at, 0f, 1f).Value);
            float max = BuildingOps.MaxDurability(KeyType);
            ToWarned(s, at);
            int overloads0 = rec.ShieldOverloads;
            bool hit = StepUntil(() => OrbitalDropService.Impacts > 0, 70);
            site.TryGetShield(rec.Serial, out CombatShield after);
            float energy = GridContent.TryGetTuning("event.orbital_drop.impact_energy", out float e) ? e : 1000f;
            float cap = GridContent.TryGetTuning("event.orbital_drop.shield_absorb_max", out float c) ? c : 0.75f;
            float frac = GridContent.TryGetTuning("event.orbital_drop.building_damage_frac", out float f) ? f : 0.8f;
            // 撞击那一步之前护盾可能还在回充：吸收量以内核累计吸收量的增量为准。
            float took = (float)(after.Absorbed - before.Absorbed);
            float mul = 1f - took / energy;
            float loss = max - b.Health;
            Expect(hit && took > 0f && took <= energy * cap + 0.01f && Mathf.Abs(took - OrbitalDropService.LastAbsorbed) < 0.5f && Mathf.Abs(mul - OrbitalDropService.LastDamageMul) < 1e-3f
                   && mul < 1f && Math.Abs(loss - frac * max * mul) < 1f && after.Hits > before.Hits,
                $"C1 FGR-EVT-030“护盾发生器可以吸收一部分伤害”：落点在护盾罩里 → 护盾用剩余护盾值吸收 {took:F0}（≤ 总能量 {energy} × 上限 {cap}），护盾值 {before.Hp:F0} → {after.Hp:F0}；" +
                $"圈内建筑只受 {loss:F0}（= {max:F0} × {frac} × {mul:F2}，没有护盾时是 {frac * max:F0}）");

            // C1b（修复轮 1，审查 P1）：撞击打空护盾时与弹体打空同一口径——内核收起护盾，热更层状态机转过载（计数 + 过载通知），过载期间不回充、不吸收。
            bool emptied = took < energy * cap - 0.01f; // 护盾值不够吸满上限 = 被打空（默认容量 400 < 1000 × 0.75）
            bool collapsed = after.Active == 0 && after.Hp <= 0f;
            StepSec(1);
            bool overloaded = rec.ShieldState == "overloaded" && rec.ShieldOverloads == overloads0 + 1;
            StepSec(10); // 过载 20 秒内
            site.TryGetShield(rec.Serial, out CombatShield later);
            bool idle = later.Active == 0 && later.Hp <= 0f && later.Hits == after.Hits && rec.ShieldState == "overloaded";
            Expect(hit && emptied && collapsed && overloaded && idle,
                $"C1b FG06 护盾状态机（耗尽 = 过载）：撞击打空护盾（{emptied}）→ 内核收起（Active={after.Active}，护盾值 {after.Hp:F0}）→ 下一次同步进入过载（状态 {rec.ShieldState}，过载计数 {overloads0} → {rec.ShieldOverloads}）" +
                $"→ 过载 10 秒后仍收起、不回充（护盾值 {later.Hp:F0}）、不吸收（命中 {after.Hits} → {later.Hits}）");
        }

        // ── T 暂停 / 倍速 / 观察 ──────────────────────────────────────────────

        private static string Snap(CampaignState s)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.DirectorEvents));
            sb.Append("|heard=").Append(Heard(s));
            foreach (GroundItemRecord g in Loot(s).OrderBy(x => x.SalvageInstanceId, StringComparer.Ordinal))
            {
                sb.Append($"|{g.SalvageInstanceId}:{g.ResourceType}:{g.Amount}:{g.Position.x:R},{g.Position.y:R}");
            }
            CombatSite site = Site;
            for (int i = 0; site != null && i < site.Kernel.WreckCount; i++)
            {
                if (site.Kernel.TryGetWreck(i, out EmgWreck w) && w.SrcExtKey == 0 && w.Chain == 0 && Mathf.Approximately(w.Radius, 2f))
                {
                    sb.Append($"|wreck:{w.Pos.x:R},{w.Pos.y:R}:{w.Hp:R}");
                }
            }
            foreach (BuildingRecord b in (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(x => x != null && x.BuildingId.Contains("#od")).OrderBy(x => x.BuildingId, StringComparer.Ordinal))
            {
                sb.Append($"|{b.BuildingId}:{b.Health:R}:{b.ConstructionState}");
            }
            return sb.ToString();
        }

        private static void FrameTo(long target, ref int frames)
        {
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 900)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
        }

        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = World(seed, observe);
            frames = 0;
            Vector2? spot = LandingSpot(s, 16f, 30f);
            if (spot.HasValue)
            {
                Register(s, KeyType, FreeNear(s, KeyType, spot.Value, 0f, 1f).Value);
            }
            EventDirectorService.Request(s, OrbitalDropService.EventId, at: spot);
            GameClock.SetPaused(true);
            string p0 = Snap(s);
            long t0 = GameClock.Ticks;
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            pausedHeld = Snap(s) == p0 && GameClock.Ticks == t0;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(speed);
            FrameTo(t0 + Sec(80), ref frames); // 请求 → 60 秒预警 → 撞击 → 奖励
            GameClock.SetSpeed(1f);
            return Snap(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(5401, false, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && same)
                {
                    Line($"  · T1 {speed}x 与 0.5x 的首个差异：{Diff(reference, snap)}");
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(80)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("\"Outcome\":\"success\"") && reference.Contains("|wreck:"),
                $"T1 暂停与 0.5x～3x：暂停中 120 帧预警不动；{string.Join(" / ", shown)} 跑同样的请求 → 60 秒预警 → 撞击 → 残骸 / 奖励，导演、建筑、残骸、掉落逐字段一致，" +
                $"所需真实帧与倍速成反比（暂停 {paused} / 一致 {same} / 成反比 {proportional}）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(5402, true, 1f, out _, out _);
            string unseen = RunScenario(5402, false, 1f, out _, out _);
            if (seen != unseen)
            {
                Line("  · T2 首个差异：" + Diff(seen, unseen));
            }
            Expect(seen == unseen && seen.Contains("\"Outcome\":\"success\""), "T2 家园观察与不观察时，同一段（请求、预警、撞击、残骸、掉落）结果逐字段一致（FGR-BASE-021）");
        }

        private static string Diff(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 60);
            return $"…{a.Substring(from, Math.Min(140, a.Length - from))} ⇔ …{b.Substring(from, Math.Min(140, b.Length - from))}";
        }

        // ── S 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveLoadWarned()
        {
            CampaignState s = World(5501, true);
            Vector2? spot = LandingSpot(s, 16f, 30f);
            if (spot.HasValue)
            {
                Register(s, KeyType, FreeNear(s, KeyType, spot.Value, 0f, 1f).Value);
            }
            EventDirectorInstanceRecord r0 = ToWarned(s, spot);
            StepSec(30);
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            int slot = CampaignSession.ActiveSlotIndex;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            StepSec(45);
            string continuous = Snap(s);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            if (!rr.Success)
            {
                Fail("S1 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            CampaignState l = rr.State;
            string loaded = Snap(l);
            EventDirectorInstanceRecord lr = Inst(l);
            bool mapAfter = lr != null && MapShows(Map(l, new Vector2(lr.X, lr.Y), 120, 900f), lr);
            StepSec(45);
            string after = Snap(l);
            int diffAt = 0;
            while (diffAt < after.Length && diffAt < continuous.Length && after[diffAt] == continuous[diffAt])
            {
                diffAt++;
            }
            Expect(r0 != null && save.Success && loaded == before && lr != null && Mathf.Approximately(lr.X, r0.X) && lr.StartTick == r0.StartTick && mapAfter && after == continuous
                   && continuous.Contains("|wreck:"),
                $"S1 负向“事件进行中存档”——预警中（还剩 30 秒）：真文件存档 → 读档后落点、倒计时与地图落点圈一致（读回 {loaded == before}，圈 {mapAfter}），" +
                $"继续跑到撞击后与不存档逐字段一致（{after == continuous}" + (after == continuous ? string.Empty : $"，首个差异 …{continuous.Substring(Math.Max(0, diffAt - 40), Math.Min(100, continuous.Length - Math.Max(0, diffAt - 40)))}") + "）");
        }

        /// <summary>S2（FG12-EVT-04 修复轮 1 审查 P2，承接 FG12-E2E-01）：撞击完成后真文件存档再读档，坠落残骸（位置 / 耐久 / 质量）仍在内核里。</summary>
        private static void CheckSaveLoadAfterImpact()
        {
            CampaignState s = World(5502, true);
            Vector2? spot = LandingSpot(s, 16f, 30f);
            if (!spot.HasValue)
            {
                Fail("S2 测试准备：找不到合适的落点");
                return;
            }
            Vector2 at = spot.Value;
            FgProductionSelfCheck.ParkMachinesAwayFrom(Site, at, 20f);
            if (ToWarned(s, at) == null || !StepUntil(() => OrbitalDropService.Impacts > 0, 70))
            {
                Fail("S2 没能走到撞击");
                return;
            }
            WorldSimulation.StepMany(4);
            bool had = TryWreck(OrbitalDropService.LastWreckId, out EmgWreck w0);
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            int slot = CampaignSession.ActiveSlotIndex;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            if (!rr.Success)
            {
                Fail("S2 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            EmgWreck w1 = default;
            bool found = false;
            CombatSite site = Site;
            for (int i = 0; site != null && i < site.Kernel.WreckCount && !found; i++)
            {
                found = site.Kernel.TryGetWreck(i, out w1) && math.distance(w1.Pos, new double2(at.x, at.y)) < 1e-3 && Mathf.Approximately(w1.Radius, w0.Radius);
            }
            string loaded = Snap(rr.State);
            bool same = found && Mathf.Approximately(w1.Hp, w0.Hp) && Mathf.Approximately(w1.Mass, w0.Mass) && w1.NoSalvage == w0.NoSalvage && site.Kernel.PickWreck(new double2(at.x, at.y), 0.2f) >= 0;
            Expect(had && save.Success && same && loaded == before && Loot(rr.State).Count >= 3,
                $"S2 撞击后存档 → 读档：坠落残骸仍在落点（{found}；耐久 {w0.Hp:F0} → {w1.Hp:F0}、质量 {w0.Mass} → {w1.Mass}、可拆解 {w1.NoSalvage == 0}、空间哈希可点选），" +
                $"导演历史、建筑、残骸处材料逐字段一致（{loaded == before}，材料 {Loot(rr.State).Count} 堆）");
        }

        /// <summary>S3（FG12-EVT-04 修复轮 1 审查 P2，承接 FG12-E2E-01）：预警中家园被卸载（没有星球战斗内核）时到点撞击——不崩溃，圈内建筑照常受创、
        /// 残骸处材料照常掉落、事件以 success 结束；没有内核就不生成内核残骸、不波及单位。</summary>
        private static void CheckImpactHomeUnloaded()
        {
            CampaignState s = World(5503, false);
            Vector2? spot = LandingSpot(s, 16f, 30f);
            if (!spot.HasValue)
            {
                Fail("S3 测试准备：找不到合适的落点");
                return;
            }
            Vector2 at = spot.Value;
            BuildingRecord inside = Register(s, KeyType, FreeNear(s, KeyType, at, 0f, 1f).Value);
            float inMax = BuildingOps.MaxDurability(KeyType);
            if (ToWarned(s, at) == null)
            {
                Fail("S3 没能开始预警");
                return;
            }
            WorldSimulation.UnloadAll(); // 家园卸载、战役状态与时钟留着：世界步只跑状态层（导演、震动、天气……）
            bool unloaded = WorldSimulation.Home == null && ReferenceEquals(CampaignSession.Current, s);
            Exception thrown = null;
            bool hit = false;
            try
            {
                hit = StepUntil(() => OrbitalDropService.Impacts > 0, 70);
                WorldSimulation.StepMany(4);
            }
            catch (Exception e)
            {
                thrown = e;
            }
            float frac = GridContent.TryGetTuning("event.orbital_drop.building_damage_frac", out float bf) ? bf : 0.8f;
            BuildingRecord b = HomeGridService.FindBuilding(s, inside.BuildingId);
            float loss = b == null ? inMax : inMax - b.Health;
            EventDirectorHistoryRecord h = LastHistory(s);
            List<GroundItemRecord> loot = Loot(s);
            Expect(unloaded && thrown == null && hit && Math.Abs(loss - frac * inMax) < 1f && OrbitalDropService.LastBuildingsHit >= 1
                   && OrbitalDropService.LastWreckId == 0 && OrbitalDropService.LastUnitsHit == 0
                   && h != null && h.Outcome == EventDirectorService.OutcomeSuccess && loot.Count >= 3 && loot.All(g => Vector2.Distance(g.Position, at) < 4f),
                $"S3 家园未载入时撞击（卸载 {unloaded}）：不抛异常（{thrown?.GetType().Name ?? "无"}）、圈内建筑照常受创（{loss:F0} = {inMax:F0} × {frac}）、" +
                $"不生成内核残骸（{OrbitalDropService.LastWreckId}）、不波及单位（{OrbitalDropService.LastUnitsHit}）、历史 {h?.Outcome}、残骸处材料 {loot.Count} 堆" +
                (thrown == null ? string.Empty : "；异常：" + thrown.Message));
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(5601, false);
            ToWarned(s, null);
            var into = new List<EventDirectorInstanceRecord>();
            const int n = 100000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                OrbitalDropService.Warned(s, into);
            }
            double warnedUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                OrbitalDropService.PickLanding(s, 1000 + i, out _);
            }
            double pickUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000;
            sw.Restart();
            int nb = s.BuildingRecords.Length;
            for (int i = 0; i < 1000; i++)
            {
                OrbitalDropService.HitBuildings(s, new Vector2(1e5f, 1e5f), 3f, 0.8f); // 远处：只量扫描，不改建筑
            }
            double scanUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000;
            PerfLines.Add($"OrbitalDropService.Warned（每帧地图 / 主视图）{warnedUs:0.###} µs；选落点 PickLanding（只在 Prepare）{pickUs:0.#} µs；撞击建筑扫描 HitBuildings（只在撞击那一刻，{nb} 座建筑）{scanUs:0.#} µs");
            Expect(warnedUs < 5.0 && pickUs < 2000.0, $"P1 每帧开销与单位 / 建筑数无关（{warnedUs:0.###} µs，线 5 µs）；选落点 {pickUs:0.#} µs、撞击扫描 {scanUs:0.#} µs 都只发生一次");
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
            finally
            {
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("  ✗ " + what);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
