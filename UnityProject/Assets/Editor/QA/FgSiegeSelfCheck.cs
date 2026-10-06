using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
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
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-05 攻城行为与寻路的自动验收（FG06 FGR-DEF-030～032；FGT-DEF-005；FG06 第 5 章负向“玩家用屏障把核心完全围死”“屏障在突袭中被拆或新建”“突袭期间存档”）。
    /// 全部起真实系统：真实家园（世界模拟、寻路镜像、战斗内核攻城剧场与流场、建筑 / 防御 / 炮塔结构单位）、真实突袭导演（剧情突袭 → 出发 → 沿地形行进 → 到达展开）、真文件存读档。
    /// A 数据（四张新表与源数据逐字段、初值、文本中英、钩子、图鉴、通知）；B 到达展开（编成 / 职能 / 精英 / 剧场 / 建筑进内核 / 反应归因场次）；
    /// C 按职能选目标（突击打核心且核心不在内核里阵亡、破坏打电塔与发电、攻城打炮塔与屏障、监听站是次要目标）；
    /// D FGT-DEF-005 流场：增量 = 全量、完全堵死时攻击最短路线上最薄弱的屏障、屏障被拆 / 新建后流场更新、玩家看得到在拆哪段墙；
    /// E 撤退（损失超过 70% / 时间上限 → 回集结点 → 并回行进队伍原路回据点；全歼 → 计划记 destroyed）；F 正式旅程（剧情突袭到达展开）；
    /// G 存读档、H 暂停与倍速、I 观察一致、J 种子集；K 溅射 / 管线与电塔耐久 / 维修无人机 / 传送带最近挨打；L 拦截 / 驻防交战；M 职能图标 / 叠加层 / 放置预览；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSiegeSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static partial class FgSiegeSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 5;
        private const string B1 = "barrier_t1";
        private const string B3 = "barrier_t3";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 攻城行为与寻路")]
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
            Line("\n[攻城行为与寻路] 到达展开、按职能选目标、流场（增量 = 全量）、完全堵死时攻击最薄弱的屏障、撤退、溅射与管线 / 电塔耐久、拦截与驻防（FG6-DEF-05）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsiege-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；流场 / 按职能选目标 / 破墙 / 撤退在 AOT 内核（Burst），展开 / 对账 / 撤退条件 / 溅射结算在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckUnfold);
                Step(CheckRoleTargets);
                Step(CheckListeningPost);
                Step(CheckFieldIncremental);
                Step(CheckWeakestWall);
                Step(CheckBreachJourney);
                Step(CheckRetreatLosses);
                Step(CheckRetreatTime);
                Step(CheckAnnihilation);
                Step(CheckFormalJourney);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSeedSet);
                RunExtras();
                RunReview();
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"攻城自检抛异常：{e}");
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
                SiegeService.ResetSessionState();
                DefenseService.ResetSessionState();
                TurretService.ResetSessionState();
                RepairDroneService.ResetSessionState();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
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
            Line($"  · [攻城行为与寻路] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>与主菜单“新建”同一入口（v2 世界：规划层、家园区侦察巢），家园有电、防御研发已完成。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            _seq = 700;
            CampaignState s = CampaignState.CreateNew("fgsiege-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 3000;
            F.PowerUp(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：直接登记一座建成、满耐久的建筑（真实放置 / 施工由 FG3 / FG6-DEF-02 的自检覆盖）；<paramref name="state"/> 给出虚影状态时登记成施工现场。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c, int rotation = 0, bool sync = true,
            BuildingConstructionState state = BuildingConstructionState.Operational)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            bool built = state == BuildingConstructionState.Operational || state == BuildingConstructionState.Disabled;
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#sg" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, rotation),
                Health = built ? BuildingOps.MaxDurability(type) : 0f,
                ConstructionState = state,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            if (sync)
            {
                HomeValleyPowerGrid.Recompute(s);
                DefenseService.Sync(s);
                TurretService.Sync(s);
            }
            return r;
        }

        private static bool CanPlace(CampaignState s, string type, GridCell c) =>
            HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok;

        internal static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        /// <summary>全部己方建筑的外接矩形。</summary>
        private static void BaseBounds(CampaignState s, out GridCell min, out GridCell max)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            var cells = new List<GridCell>(32);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b == null)
                {
                    continue;
                }
                cells.Clear();
                HomeGridService.FootprintOf(b, cells);
                foreach (GridCell c in cells)
                {
                    x0 = Math.Min(x0, c.X);
                    y0 = Math.Min(y0, c.Y);
                    x1 = Math.Max(x1, c.X);
                    y1 = Math.Max(y1, c.Y);
                }
            }
            min = new GridCell(x0, y0);
            max = new GridCell(x1, y1);
        }

        /// <summary>离核心 <paramref name="dist"/> 格、敌方可走、而且能走到核心外围的一点（按 8 个方向依次试；B25 不写死坐标）。</summary>
        internal static Vector2 OutsidePoint(CampaignState s, float dist, int startDir = 0)
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

        /// <summary>
        /// 测试捷径：一支已经到达 <paramref name="at"/> 的突袭队伍（编成按参数；正式派出 → 沿地形行进 → 到达由 F 段的剧情突袭覆盖）。
        /// 下一步攻城服务按编成展开。
        /// </summary>
        internal static TransitGroupRecord Arrive(CampaignState s, Vector2 at, string faction, string[] ids, int[] counts, int[] elites = null, Vector2? origin = null)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 o = origin ?? (at + (at - CoreCenter(s)).normalized * 60f);
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, faction, counts.Sum(), o.x, o.y, core.X, core.Y);
            g.Faction = faction;
            g.UnitIds = ids;
            g.UnitCounts = counts;
            g.EliteCounts = elites ?? new int[ids.Length];
            g.TargetKind = RaidDirectorService.TargetHome;
            g.PosX = at.x;
            g.PosY = at.y;
            g.RouteX = new[] { (int)Mathf.Round(at.x) };
            g.RouteY = new[] { (int)Mathf.Round(at.y) };
            g.RouteIndex = 1;
            g.RouteState = WorldTransitSystem.RouteFollowing;
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks;
            WorldSimulation.StepMany(1);
            return g;
        }

        private static int Key(TransitGroupRecord g) => SiegeService.KeyOf(g);

        private static int Alive(TransitGroupRecord g) => Site?.CountSiegeGroup(Key(g)) ?? 0;

        private static float Durability(CampaignState s, BuildingRecord b) => RepairDroneService.DurabilityOf(s, b);

        private static BuildingRecord CoreOf(CampaignState s) => s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);

        private static void Seconds(float sec) => F.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => F.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static string Short(string s) => s == null ? string.Empty : s.Length > 160 ? s.Substring(0, 160) + "…" : s;

        /// <summary>用屏障把整个家园围成一圈（外接矩形外 <paramref name="margin"/> 格）；返回圈上放了墙的格（挡路已经被地形 / 建筑挡住的格不放）。</summary>
        private static List<BuildingRecord> Ring(CampaignState s, int margin, string type, out GridCell min, out GridCell max, out int gaps)
        {
            BaseBounds(s, out GridCell a, out GridCell b);
            // 测试捷径：先把这一圈揭开迷雾（迷雾里不能建造；真实玩家要先探索，FG3 / FG0-ARCH-05 覆盖）。
            HomeGridService.RevealArea(s, new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f), Mathf.Max(b.X - a.X, b.Y - a.Y) * 0.75f + margin + 16f);
            var walls = new List<BuildingRecord>();
            var cells = new List<GridCell>();
            gaps = int.MaxValue;
            min = max = default;
            // 从 margin 往外找第一圈“敌方能走的格都能放墙”的矩形（家园外有传送带 / 资源点 / 不可建地表时换一圈；B25 不写死坐标）。
            for (int m = margin; m <= margin + 12 && gaps > 0; m++)
            {
                var lo = new GridCell(a.X - m, a.Y - m);
                var hi = new GridCell(b.X + m, b.Y + m);
                var ring = new List<GridCell>();
                int miss = 0;
                var gapInfo = new StringBuilder();
                for (int x = lo.X; x <= hi.X; x++)
                {
                    for (int y = lo.Y; y <= hi.Y; y++)
                    {
                        if (x != lo.X && x != hi.X && y != lo.Y && y != hi.Y)
                        {
                            continue;
                        }
                        var c = new GridCell(x, y);
                        if (!NavService.PassableNow(x, y, NavConst.ClassHostile))
                        {
                            continue;
                        }
                        if (!CanPlace(s, type, c) && PurifyIfOnlyPollution(s, type, c))
                        {
                            ring.Add(c);
                            continue;
                        }
                        if (!CanPlace(s, type, c))
                        {
                            miss++;
                            if (miss <= 3)
                            {
                                GridPlacementResult pr = HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false);
                                gapInfo.Append($" ({x},{y}):{string.Join("/", pr.Reasons.Select(r => r.Code.ToString()))}");
                            }
                            continue;
                        }
                        ring.Add(c);
                    }
                }
                if (miss < gaps)
                {
                    gaps = miss;
                    RingGapInfo = gapInfo.ToString();
                    min = lo;
                    max = hi;
                    cells = ring;
                }
            }
            foreach (GridCell c in cells)
            {
                walls.Add(Register(s, type, c, sync: false));
            }
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            NavService.SyncGridChanges();
            // 校验真的围死：从圈外几个点向核心外一圈求敌方路线（寻路镜像上的 Burst 距离场）；还走得通就沿路线找出它穿过圈的那一格补上
            // （扫描时寻路镜像还没推进到的格、开局残骸等），补不上的计入缺口。
            if (gaps == 0 && HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax))
            {
                var goals = new List<int2>();
                for (int x = cmin.X - 1; x <= cmax.X + 1; x++)
                {
                    goals.Add(new int2(x, cmin.Y - 1));
                    goals.Add(new int2(x, cmax.Y + 1));
                }
                for (int y = cmin.Y; y <= cmax.Y; y++)
                {
                    goals.Add(new int2(cmin.X - 1, y));
                    goals.Add(new int2(cmax.X + 1, y));
                }
                var lo = new int2(min.X - 3, min.Y - 3);
                var hi = new int2(max.X + 3, max.Y + 3);
                var entries = new List<int2>();
                foreach (int2 e in new[] { lo, hi, new int2(lo.x, hi.y), new int2(hi.x, lo.y), new int2((lo.x + hi.x) / 2, lo.y), new int2((lo.x + hi.x) / 2, hi.y), new int2(lo.x, (lo.y + hi.y) / 2), new int2(hi.x, (lo.y + hi.y) / 2) })
                {
                    if (NavService.PassableNow(e.x, e.y, NavConst.ClassHostile))
                    {
                        entries.Add(e);
                    }
                }
                var pts = new List<int2>();
                var off = new int[entries.Count];
                var cnt = new int[entries.Count];
                var cost = new int[entries.Count];
                for (int round = 0; round < 24 && entries.Count > 0; round++)
                {
                    NavService.FlowRoutes(NavConst.ClassHostile, lo, hi, goals, null, entries, pts, off, cnt, cost);
                    int hole = -1;
                    for (int e = 0; e < entries.Count && hole < 0; e++)
                    {
                        for (int p = 0; cost[e] >= 0 && p < cnt[e]; p++)
                        {
                            int2 c = pts[off[e] + p];
                            if (c.x >= min.X && c.x <= max.X && c.y >= min.Y && c.y <= max.Y && (c.x == min.X || c.x == max.X || c.y == min.Y || c.y == max.Y))
                            {
                                hole = off[e] + p;
                                break;
                            }
                        }
                    }
                    if (hole < 0)
                    {
                        break;
                    }
                    var hc = new GridCell(pts[hole].x, pts[hole].y);
                    if (!CanPlace(s, type, hc))
                    {
                        gaps++;
                        GridPlacementResult pr = HomeGridService.ValidatePlacement(s, type, hc, 0, asPlayerPlacement: false, checkCost: false);
                        RingGapInfo += $" 漏洞({hc.X},{hc.Y}):{string.Join("/", pr.Reasons.Select(r => r.Code.ToString()))}";
                        break;
                    }
                    walls.Add(Register(s, type, hc));
                    NavService.SyncGridChanges();
                    RingPatched++;
                }
            }
            return walls;
        }

        /// <summary>自检读：围墙校验时补上的漏洞格数（累计）。</summary>
        private static int RingPatched;

        /// <summary>离 <paramref name="at"/> 最近、不在圈角上的那段墙（圈角那一格拆了也进不去：两侧都是墙、斜走不切角）。</summary>
        private static BuildingRecord NearestWall(List<BuildingRecord> ring, GridCell min, GridCell max, Vector2 at) =>
            ring.Where(w => s_live(w) && !((w.GridX == min.X || w.GridX == max.X) && (w.GridY == min.Y || w.GridY == max.Y)))
                .OrderBy(w => Vector2.SqrMagnitude(w.Position - at)).ThenBy(w => w.GridX).ThenBy(w => w.GridY).First();

        private static bool s_live(BuildingRecord w) => CampaignSession.Current?.BuildingRecords != null && CampaignSession.Current.BuildingRecords.Contains(w);

        private static string RingGapInfo = string.Empty;

        /// <summary>测试捷径：这一格只因为污染放不下墙时，把污染清掉（正式的净化在 FG07）。返回清过之后能不能放。</summary>
        private static bool PurifyIfOnlyPollution(CampaignState s, string type, GridCell c)
        {
            GridPlacementResult pr = HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false);
            if (pr.Ok || pr.Reasons.Count == 0 || pr.Reasons.Any(r => r.Code != GridBlockReason.Pollution))
            {
                return false;
            }
            HomeGridService.MapFor(s).SetPollution(c, 0);
            return CanPlace(s, type, c);
        }

        /// <summary>沿 <paramref name="from"/> → <paramref name="to"/> 连线（比例 <paramref name="f0"/>～<paramref name="f1"/>，两侧偏 0～4 格）找第一个能放 <paramref name="type"/> 的格并登记。</summary>
        internal static BuildingRecord PlaceNear(CampaignState s, string type, Vector2 from, Vector2 to, float f0, float f1)
        {
            Vector2 dir = (to - from).normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            for (float f = f0; f <= f1 + 1e-4f; f += 0.05f)
            {
                foreach (int o in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4 })
                {
                    Vector2 p = Vector2.Lerp(from, to, f) + side * o;
                    var c = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    if (NavService.PassableNow(c.X, c.Y, NavConst.ClassHostile) && CanPlace(s, type, c))
                    {
                        BuildingRecord b = Register(s, type, c);
                        NavService.SyncGridChanges();
                        return b;
                    }
                }
            }
            return null;
        }

        private static GridCell CellOfB(BuildingRecord b) => new GridCell(b.GridX, b.GridY);

        private static int UnitOfDefense(CampaignState s, BuildingRecord b)
        {
            DefenseRecord r = b != null ? DefenseService.Find(s, b.BuildingId) : null;
            return r != null && Site != null && Site.TryGetDefenseUnit(r.Serial, out int u) ? u : 0;
        }

        private static string Fields()
        {
            int bad = Site.VerifySiegeFields(out int n);
            return $"{n} 张流场 {bad} 格不一致";
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static string Cell(object v) => v is float f ? Norm(f.ToString("R", CultureInfo.InvariantCulture)) : v?.ToString() ?? string.Empty;

        private static string Norm(string pyCell) =>
            double.TryParse(pyCell, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && pyCell.Contains(".") ? d.ToString("0.###", CultureInfo.InvariantCulture) : pyCell;

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = F.RunPython(F.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var py = new HashSet<string>(output.Replace("\r", string.Empty).Split('\n')
                .Where(l => l.StartsWith("SGR\t") || l.StartsWith("SGT\t") || l.StartsWith("SGC\t") || l.StartsWith("SGU\t") || l.StartsWith("CX\tcodex.raid.siege"))
                .Select(l => string.Join("\t", l.Split('\t').Select(Norm))));
            var rt = new HashSet<string>();
            foreach (GameConfig.fg.SiegeRole r in t.TbSiegeRole.DataList)
            {
                rt.Add(string.Join("\t", "SGR", r.Role, r.NameKey, r.DescKey, r.IconShape, r.IconColor, r.SortOrder));
            }
            foreach (GameConfig.fg.SiegeTarget r in t.TbSiegeTarget.DataList)
            {
                rt.Add(string.Join("\t", "SGT", r.Id, r.Role, r.Category, Cell(r.BiasMeters)));
            }
            foreach (GameConfig.fg.SiegeCategory r in t.TbSiegeCategory.DataList)
            {
                rt.Add(string.Join("\t", "SGC", r.TypeId, r.Category));
            }
            foreach (GameConfig.fg.SiegeUnit r in t.TbSiegeUnit.DataList)
            {
                rt.Add(string.Join("\t", "SGU", r.EnemyTypeId, Cell(r.Speed), Cell(r.Radius), Cell(r.Range), Cell(r.Damage), Cell(r.Cooldown), Cell(r.ProjectileSpeed),
                    Cell(r.ProjectileRadius), Cell(r.StructureMult), Cell(r.HealAmount), Cell(r.HealRange), Cell(r.HealCooldown)));
            }
            GameConfig.fg.CodexEntry cx = t.TbCodexEntry.GetOrDefault("codex.raid.siege");
            if (cx != null)
            {
                rt.Add(string.Join("\t", "CX", cx.Id, cx.Tab, cx.TitleKey, cx.BodyKey, cx.HintKey, cx.Links, cx.Hooks, cx.SortOrder));
            }
            var missing = py.Where(l => !rt.Contains(l)).ToList();
            var extra = rt.Where(l => !py.Contains(l)).ToList();
            Expect(code == 0 && py.Count > 20 && missing.Count == 0 && extra.Count == 0 && SiegeCatalog.Problems.Count == 0,
                $"A1 fg.TbSiegeRole / TbSiegeTarget / TbSiegeCategory / TbSiegeUnit 与图鉴“攻城行为”与源数据 fgdata_siege.py 逐字段一致（{py.Count} 行）；运行时校验无问题"
                + (missing.Count > 0 ? "；缺：" + Short(string.Join(" | ", missing)) : string.Empty) + (extra.Count > 0 ? "；多：" + Short(string.Join(" | ", extra)) : string.Empty)
                + (SiegeCatalog.Problems.Count > 0 ? "；问题：" + string.Join("；", SiegeCatalog.Problems) : string.Empty));

            byte core = SiegeCatalog.CategoryOf("core");
            bool roles = SiegeCatalog.CategoryOf("power_pole") == CombatSiegeConst.CatSignal && SiegeCatalog.CategoryOf("signal_tower") == CombatSiegeConst.CatSignal
                         && SiegeCatalog.CategoryOf("override_array") == CombatSiegeConst.CatSignal && SiegeCatalog.CategoryOf("generator") == CombatSiegeConst.CatPower
                         && SiegeCatalog.CategoryOf("turret_light") == CombatSiegeConst.CatDefense && SiegeCatalog.CategoryOf(B1) == CombatSiegeConst.CatDefense
                         && SiegeCatalog.CategoryOf("listening_post") == CombatSiegeConst.CatListening && core == CombatSiegeConst.CatCore
                         && SiegeCatalog.CategoryOf("recycler") == CombatSiegeConst.CatOther;
            IReadOnlyList<int> masks = SiegeCatalog.RoleMasks;
            IReadOnlyList<int> bias = SiegeCatalog.RoleBias;
            int Bias(int r, byte cat) => bias[r * CombatSiegeConst.CategoryCount + math.tzcnt((int)cat)];
            bool primary = Bias(0, CombatSiegeConst.CatCore) == 0 && Bias(1, CombatSiegeConst.CatPower) == 0 && Bias(1, CombatSiegeConst.CatSignal) == 0 && Bias(2, CombatSiegeConst.CatDefense) == 0
                           && Bias(0, CombatSiegeConst.CatListening) > 0 && Bias(1, CombatSiegeConst.CatListening) > 0 && Bias(2, CombatSiegeConst.CatListening) > 0
                           && (masks[0] & CombatSiegeConst.CatDefense) == 0 && (masks[2] & CombatSiegeConst.CatPower) == 0;
            Expect(roles && primary && Math.Abs(SiegeCatalog.LossRetreatRatio - 0.7f) < 1e-4f && SiegeCatalog.PerfUnits >= 200 && Math.Abs(SiegeCatalog.PerfFieldUpdateMs - 4f) < 1e-4f
                   && Math.Abs(RaidCatalog.TimeLimitHours - 1f) < 1e-4f,
                "A2 FGR-DEF-030：突击 → 核心、破坏 → 发电 + 信号（信号塔 / 超控阵列 / 电塔）、攻城 → 防御（炮塔 / 屏障）为主目标；监听站是三种职能的次要目标（偏好 > 0，FGR-RND-052 / FG-GAP-103）；" +
                "FG06 第 10 节撤退损失 70%、时间上限 1 游戏小时；第 7 节 200 敌人、流场更新 ≤ 4 ms");

            string[] keys = { "siege.role.assault.desc", "siege.role.sabotage.desc", "siege.role.siege.desc", "siege.notify.unfold", "siege.notify.breach", "siege.notify.retreat_losses",
                              "siege.notify.retreat_time", "siege.notify.regrouped", "siege.notify.destroyed", "siege.notify.intercepted", "overlay.label.siege_legend", "codex.raid.siege.body",
                              "pipe.destroyed.notify", "defense.preview.breach_target" };
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = keys.All(k => GameText.Has(k) && !GameText.Get(k).Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool zh = keys.All(k => GameText.Has(k) && GameText.Get(k).Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
            bool hooks = new[] { GuidanceHooks.SiegeFirstUnfold, GuidanceHooks.SiegeFirstBreach, GuidanceHooks.SiegeFirstRetreat, GuidanceHooks.SiegeFirstDestroyed }.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = new[] { "raid_breach", "raid_siege", "raid_destroyed" }.All(id => t.TbNotifyType.GetOrDefault(id) != null && t.TbNotifyType.GetOrDefault(id).AwaySection == "raid");
            SiegeService.ResetSessionState(); // 新建 / 读档时的同一入口：按表生成职能图标
            float2[] vis = CombatSite.SiegeRoleVisuals;
            bool icons = vis != null && vis.Length == 4 && vis.All(v => v.x >= 14f && v.x <= 17f);
            Expect(zh && en && hooks && notify && icons,
                "A3 文本中英齐全（英文界面没有中文）；4 个引导钩子登记进 GuidanceHooks.Known；三种通知类型（破墙 / 展开 / 全歼）进离家报告突袭段；职能图标形状 14～17（形状为主，B15）"
                + $"（中文 {zh} / 英文 {en} / 钩子 {hooks} / 通知 {notify} / 图标 {icons}：{(vis == null ? "null" : string.Join(",", vis.Select(v => v.x.ToString(CultureInfo.InvariantCulture))))}）");
        }

        // ── B 到达展开 ──────────────────────────────────────────────────────────

        private static void CheckUnfold()
        {
            CampaignState s = NewWorld(7001);
            CombatSite site = Site;
            Vector2 at = OutsidePoint(s, 45f);
            int sessionsBefore = ReactionAttribution.Sessions(s).Count;
            int notes = NotifyCount("raid_siege");
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider", "foundry.armorbot", "foundry.repairbot" }, new[] { 6, 3, 2 }, new[] { 2, 1, 0 });
            var ids = new List<int>();
            site.SiegeRaiderIds(Key(g), ids);
            int assault = 0, siege = 0, elite = 0, healers = 0;
            foreach (int id in ids)
            {
                site.TryGetSiegeRaider(id, out Vector2 pos, out CombatSiegeUnit su, out bool el, out float hp, out float max);
                assault += su.Role == (byte)CombatSiegeRole.Assault ? 1 : 0;
                siege += su.Role == (byte)CombatSiegeRole.Siege ? 1 : 0;
                elite += el ? 1 : 0;
                healers += site.Kernel.TryGetUnit(id, out CombatUnitView v) && site.Kernel.TryGetWeapon(v.Weapon, out _) ? 0 : 0;
            }
            SiegeState st = SiegeService.StateOf(s);
            ReactionSessionRecord raid = ReactionAttribution.Current(s, HomeValleyLayout.RegionId, false);
            Expect(g.Engaged && g.UnfoldedCount == 11 && ids.Count == 11 && assault == 6 && siege == 5 && elite == 3 && st.TheaterActive && NotifyCount("raid_siege") > notes
                   && raid != null && raid.Kind == ReactionAttribution.KindRaid && ReactionAttribution.Sessions(s).Count > sessionsBefore
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SiegeFirstUnfold),
                $"B1 到达的队伍按编成展开成 {ids.Count} 台战斗单位（突击 {assault} / 攻城 {siege}、精英 {elite}，编成 6+3+2、精英 2+1），队伍置 Engaged；攻城剧场开启 [{st.MinX},{st.MinY}]–[{st.MaxX},{st.MaxY}]；" +
                $"反应归因开了突袭场次（FG-GAP-056）；“突袭部队展开”通知：{Short(LastNotify("raid_siege"))}");

            // 建筑进内核：核心（不会在内核里阵亡）、发电、其它建筑都是结构单位，类别按表；炮塔 / 防御结构单位标了类别。
            BuildingRecord core = CoreOf(s);
            SiegeStructureRecord coreRec = SiegeService.FindRecord(s, core.BuildingId);
            bool coreIn = coreRec != null && site.TryGetSiegeStructUnit(coreRec.Serial, out int coreUnit) && site.Kernel.TryGetSiegeUnit(coreUnit, out CombatSiegeUnit csu)
                          && csu.Cat == CombatSiegeConst.CatCore && site.Kernel.TryGetUnit(coreUnit, out CombatUnitView cv) && (cv.Flags & CombatUnitFlags.HealthFloor) != 0;
            BuildingRecord gen = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2);
            SiegeStructureRecord genRec = gen != null ? SiegeService.FindRecord(s, gen.BuildingId) : null;
            bool genIn = genRec != null && site.TryGetSiegeStructUnit(genRec.Serial, out int gu) && site.Kernel.TryGetSiegeUnit(gu, out CombatSiegeUnit gsu) && gsu.Cat == CombatSiegeConst.CatPower;
            GridCell? wc = F.FindFree(s, B1, 10f, 20f);
            BuildingRecord wall = wc.HasValue ? Register(s, B1, wc.Value) : null;
            Seconds(SiegeCatalog.SyncSeconds + 0.1f);
            int wu = UnitOfDefense(s, wall);
            bool wallTagged = wu > 0 && site.Kernel.TryGetSiegeUnit(wu, out CombatSiegeUnit wsu) && wsu.Cat == CombatSiegeConst.CatDefense && wsu.FootMin.x == wall.GridX;
            Expect(coreIn && genIn && wallTagged && st.Structures.Length >= 3,
                $"B2 剧场里的建筑进战斗内核当攻城目标（{st.Structures.Length} 座）：核心类别 = 核心、带耐久下限（被打空的后果在 FG6-DEF-08）（{coreIn}）；发电机 2 类别 = 发电（{genIn}）；" +
                $"新建的屏障（防御结构单位）标上类别 = 防御与占地（{wallTagged}）");
            int unbreakable = UnbreakableBuildingCells(s, out int blockedCells, out string sample);
            Expect(unbreakable == 0 && blockedCells > 0,
                $"B3 复审修复（FGR-DEF-031 不允许完美迷宫）：开局布局的剧场里挡敌方寻路的建筑格 {blockedCells} 格，全部是内核里能拆的结构单位（拆不掉的 {unbreakable} 格{sample}）");
        }

        /// <summary>
        /// 剧场里“有建筑占着、挡敌方寻路、却不是内核里能拆的结构单位”的格数（完美迷宫的根）；<paramref name="blocked"/> = 有建筑占着、挡敌方寻路的格数。
        /// 先维护一次剧场（读最新的格子缓存）。只在对账之后调用（新建的建筑要等下一次对账才进内核）。
        /// </summary>
        private static int UnbreakableBuildingCells(CampaignState s, out int blocked, out string sample)
        {
            blocked = 0;
            sample = string.Empty;
            CombatSite site = Site;
            if (site == null)
            {
                return -1;
            }
            site.MaintainSiegeNow();
            int4 r = site.SiegeRect;
            HomeGridMap map = HomeGridService.MapFor(s);
            int bad = 0;
            for (int y = r.y; y < r.y + r.w; y++)
            {
                for (int x = r.x; x < r.x + r.z; x++)
                {
                    var c = new GridCell(x, y);
                    string occ = map.OccupantAt(c);
                    if (string.IsNullOrEmpty(occ) || !site.Kernel.SiegeCellInfo(new int2(x, y), out int nav, out int unit, out _, out _) || (nav & 1) == 0)
                    {
                        continue;
                    }
                    blocked++;
                    if (unit != 0)
                    {
                        continue;
                    }
                    bad++;
                    if (bad <= 3)
                    {
                        BuildingRecord b = HomeGridService.FindBuilding(s, occ);
                        sample += $"；({x},{y}) {b?.BuildingTypeId} {b?.ConstructionState}";
                    }
                }
            }
            return bad;
        }

        // ── C 按职能选目标 ──────────────────────────────────────────────────────

        private static void CheckRoleTargets()
        {
            // C1 突击：直扑核心，打核心；核心不在内核里阵亡（耐久下限）。
            CampaignState s = NewWorld(7002);
            BuildingRecord core = CoreOf(s);
            Vector2 at = OutsidePoint(s, 30f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 8 });
            float c0 = Durability(s, core);
            float d0 = Vector2.Distance(at, CoreCenter(s));
            bool hit = StepUntil(() => Durability(s, core) < c0 - 20f, 90);
            var ids = new List<int>();
            Site.SiegeRaiderIds(Key(g), ids);
            float meanDist = ids.Count == 0 ? 0f : ids.Average(id => Site.TryGetSiegeRaider(id, out Vector2 p, out _, out _, out _, out _) ? Vector2.Distance(p, CoreCenter(s)) : 0f);
            core.Health = 1f; // 把核心打到底：下一次对账推进内核
            Seconds(0.6f);
            StepUntil(() => HomeValleySoftlockGuard.IsCoreDestroyed(s), 8); // FG6-DEF-08：打到下限的那次对账判为被摧毁（判定当步核心单位仍在内核、停在下限）
            SiegeStructureRecord cr = SiegeService.FindRecord(s, core.BuildingId);
            // FG6-DEF-08 起：核心在内核里仍不阵亡（耐久下限，单位还活着），攻城对账读到下限 = 归还核心被摧毁（战役失败，RaidResultService.CheckCore）。
            bool floor = cr != null && Site.TryGetSiegeStructHealth(cr.Serial, out float chp, out _, out bool calive) && calive && chp <= 1f
                         && core.ConstructionState == BuildingConstructionState.Destroyed && RaidResultService.IsCoreLost(s);
            Expect(hit && meanDist < d0 && floor,
                $"C1 突击型直扑归还核心：{d0:F0} 米外展开、平均走近到 {meanDist:F0} 米，核心耐久 {c0:F0} → {Durability(s, core):F1}；核心打到底在内核里不阵亡（耐久下限），攻城对账判为被摧毁、战役失败（FG6-DEF-08）（{floor}）");

            // C2 破坏：先打信号设施（电塔）与发电，不先打核心。
            s = NewWorld(7003);
            core = CoreOf(s);
            at = OutsidePoint(s, 34f);
            BuildingRecord pole = PlaceNear(s, "power_pole", at, CoreCenter(s), 0.3f, 0.7f);
            float coreBefore = Durability(s, core);
            g = Arrive(s, at, "silent", new[] { "silent.jammer" }, new[] { 6 });
            bool poleDown = pole != null && StepUntil(() => pole.ConstructionState == BuildingConstructionState.Damaged, 90);
            float coreAtPole = Durability(s, core);
            bool ghost = pole != null && HomeGridService.FindBuilding(s, pole.BuildingId) != null;
            Expect(poleDown && coreAtPole >= coreBefore - 0.01f && ghost && SiegeService.StateOf(s).TotalDestroyedBuildings >= 1,
                $"C2 破坏型先打信号设施：路上的电塔被打掉（留下虚影可重建；FG-GAP-089 电塔有耐久、击毁走 ApplyBuildingDestroyed）（{poleDown}），此前核心没挨打（{coreBefore:F0} → {coreAtPole:F0}）"
                + (poleDown ? string.Empty : Diag(s, g, pole)));

            // C3 攻城：先打屏障（炮塔见 C5，用真实炮塔服务的炮塔单位）。
            s = NewWorld(7004);
            core = CoreOf(s);
            at = OutsidePoint(s, 34f);
            BuildingRecord wall = PlaceNear(s, B1, at, CoreCenter(s), 0.3f, 0.7f);
            coreBefore = Durability(s, core);
            g = Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 4 });
            bool wallDown = wall != null && StepUntil(() => wall.ConstructionState == BuildingConstructionState.Damaged, 120);
            Expect(wallDown && Durability(s, core) >= coreBefore - 0.01f,
                $"C3 攻城型先打屏障（炮塔见 C5）：路边的屏障先被拆掉（{wallDown}），此前核心没挨打（{coreBefore:F0} → {Durability(s, core):F0}）"
                + (wallDown ? string.Empty : Diag(s, g, wall)));
        }

        /// <summary>失败时的诊断：队伍在内核里的单位数 / 职能 / 位置 / 目标、目标建筑耐久与结构单位。</summary>
        private static string Diag(CampaignState s, TransitGroupRecord g, BuildingRecord target)
        {
            CombatSite site = Site;
            var sb = new StringBuilder();
            SiegeState sst = SiegeService.StateOf(s);
            sb.Append($"\n    诊断：队伍 {g?.GroupId} 状态 {g?.State} 展开 {g?.UnfoldedCount} 内核存活 {(g != null ? Alive(g) : -1)} 撤退 {g?.SiegeRetreat}；剧场 {sst?.TheaterActive} [{sst?.MinX},{sst?.MinY}]–[{sst?.MaxX},{sst?.MaxY}] 问题 {SiegeService.LastProblem}；");
            if (target == null)
            {
                sb.Append("目标建筑没放上（CanPlace 失败）；");
            }
            else
            {
                SiegeStructureRecord r = SiegeService.FindRecord(s, target.BuildingId);
                int du = UnitOfDefense(s, target);
                sb.Append($"目标 {target.BuildingTypeId} @({target.GridX},{target.GridY}) 状态 {target.ConstructionState} 耐久 {Durability(s, target):F0} 结构记录 {(r != null ? r.Serial : -1)} 防御单位 {du} 类别 {SiegeCatalog.CategoryOf(target.BuildingTypeId)}；");
            }
            if (g != null && site != null)
            {
                var ids = new List<int>();
                site.SiegeRaiderIds(Key(g), ids);
                foreach (int id in ids.Take(4))
                {
                    if (site.TryGetSiegeRaider(id, out Vector2 p, out CombatSiegeUnit su, out _, out float hp, out _) && site.Kernel.TryGetUnit(id, out CombatUnitView v))
                    {
                        sb.Append($" [#{id} 职能 {su.Role} 模式 {su.Mode} 位置 ({p.x:F1},{p.y:F1}) 目标 {v.CommandTarget} 破墙 {su.Breach} hp {hp:F0}]");
                    }
                }
                int4 rect = site.SiegeRect;
                sb.Append($" 剧场 ({rect.x},{rect.y}) {rect.z}×{rect.w}");
                for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
                {
                    if (site.SiegeFieldValid(f))
                    {
                        sb.Append($" 场{f}");
                    }
                }
            }
            return sb.ToString();
        }

        private static void CheckListeningPost()
        {
            CampaignState s = NewWorld(7005);
            BuildingRecord core = CoreOf(s);
            Vector2 at = OutsidePoint(s, 40f);
            Vector2 toCore = (CoreCenter(s) - at).normalized;
            Vector2 side = new Vector2(-toCore.y, toCore.x);
            BuildingRecord post = null;
            for (float f = 0.3f; f <= 0.7f && post == null; f += 0.05f)
            {
                for (int o = 4; o <= 8 && post == null; o++)
                {
                    Vector2 p = Vector2.Lerp(at, CoreCenter(s), f) + side * o;
                    var c = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    if (CanPlace(s, "listening_post", c))
                    {
                        post = Register(s, "listening_post", c);
                    }
                }
            }
            NavService.SyncGridChanges();
            float coreBefore = Durability(s, core);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 6 });
            bool down = post != null && StepUntil(() => post.ConstructionState == BuildingConstructionState.Damaged, 90);
            float coreAtDown = Durability(s, core);
            Expect(down && coreAtDown >= coreBefore - 30f,
                $"C4 监听站是突袭的次要目标（FGR-RND-052 / FG-GAP-103）：突击型路过时先拆路边的监听站（{down}），核心此时 {coreBefore:F0} → {coreAtDown:F0}；被毁照常留虚影");
        }

        // ── D FGT-DEF-005 流场 ─────────────────────────────────────────────────

        private static void CheckFieldIncremental()
        {
            CampaignState s = NewWorld(7006);
            CombatSite site = Site;
            Vector2 at = OutsidePoint(s, 40f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 3 });
            site.MaintainSiegeNow();
            int bad0 = site.VerifySiegeFields(out int n0);
            int2 gc = new int2((int)at.x, (int)at.y);
            int d0 = site.SiegeDistAt(0, gc);
            // 随机放 / 拆屏障 30 次（按种子确定），每次之后流场增量更新必须与全量重算逐格相同。
            var rng = new System.Random(7006);
            var walls = new List<BuildingRecord>();
            int bad = 0;
            int checks = 0;
            double maxMs = 0;
            BaseBounds(s, out GridCell bmin, out GridCell bmax);
            for (int i = 0; i < 30; i++)
            {
                if (walls.Count > 0 && rng.NextDouble() < 0.35)
                {
                    BuildingRecord w = walls[rng.Next(walls.Count)];
                    walls.Remove(w);
                    if (UnitOfDefense(s, w) > 0)
                    {
                        site.DamageDefense(DefenseService.Find(s, w.BuildingId).Serial, 100000f);
                    }
                }
                else
                {
                    for (int tries = 0; tries < 40; tries++)
                    {
                        var c = new GridCell(rng.Next(bmin.X - 6, bmax.X + 7), rng.Next(bmin.Y - 6, bmax.Y + 7));
                        if (CanPlace(s, B1, c) && NavService.PassableNow(c.X, c.Y, NavConst.ClassHostile))
                        {
                            walls.Add(Register(s, B1, c));
                            break;
                        }
                    }
                }
                WorldSimulation.StepMany(2);
                maxMs = Math.Max(maxMs, site.MaxSiegeChangeMs);
                bad += site.VerifySiegeFields(out int nf);
                checks += nf;
            }
            CombatSiegeStats st = site.SiegeStats;
            Expect(bad0 == 0 && n0 >= 1 && d0 < CombatSiegeConst.Inf && bad == 0 && checks >= 30 && st.Incrementals > 0,
                $"D1 FGR-DEF-031 流场：展开后 {n0} 张有效流场与全量重算一致；随机放 / 拆屏障 30 次（每次之后增量更新 {st.Incrementals} 次、作废 {st.InvalidatedCells} 格），" +
                $"每次都与全量重算逐格相同（比对 {checks} 张次，不一致 {bad} 格）；集结点到核心 {d0 / 10f:F0} 米；单次增量更新最大 {maxMs:F2} ms（阈值 4 ms）");
            PerfLines.Add($"流场增量更新（随机放 / 拆屏障）单次最大 {maxMs:F3} ms（阈值 {SiegeCatalog.PerfFieldUpdateMs} ms）");
            PerfGate.Expect(true, $"D1b 流场增量更新单次最大 {maxMs:F3} ms（FG06 第 7 节 ≤ 4 ms；剧场 {site.SiegeRect.z}×{site.SiegeRect.w} 格）",
                new[] { PerfGate.Le(maxMs, SiegeCatalog.PerfFieldUpdateMs, "流场增量更新 ms") }, Expect, Line);
        }

        private static void CheckWeakestWall()
        {
            CampaignState s = NewWorld(7007);
            CombatSite site = Site;
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            Vector2 c = CoreCenter(s);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 14f);
            // 离集结点最近的那段墙换成 T1（最薄弱）。
            BuildingRecord nearest = NearestWall(ring, rmin, rmax, at);
            BuildingRecord weak = ReplaceWall(s, nearest, B1);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 4 });
            site.MaintainSiegeNow();
            int2 gc = new int2((int)at.x, (int)at.y);
            bool enclosed = site.SiegeDistAt(0, gc) >= CombatSiegeConst.Inf && site.SiegeFieldValid(1) && site.SiegeDistAt(1, gc) < CombatSiegeConst.Inf;
            int target = site.SiegeBreachAhead(1, gc, 400);
            int weakUnit = UnitOfDefense(s, weak);
            Expect(gaps == 0 && enclosed && target == weakUnit && weakUnit > 0,
                $"D2 FG06 第 5 章负向“用屏障把核心完全围死”：{ring.Count} 段 T3 屏障围成一圈（缺口 {gaps}）后开路流场到不了（{enclosed}）；破墙流场沿最短路线找到的第一段墙 = 换成 T1 的那段（最薄弱）（目标 {target} / T1 {weakUnit}）" + (gaps > 0 ? "；缺口原因" + RingGapInfo : string.Empty));

            // 最薄弱的墙离得太远（绕路比破一段 T3 还贵）：改拆近处的 T3——“最短路线上”最薄弱的那段，不是全图最薄弱。
            BuildingRecord far = ring.Where(w => w != nearest && s_live(w) && !((w.GridX == rmin.X || w.GridX == rmax.X) && (w.GridY == rmin.Y || w.GridY == rmax.Y))).OrderByDescending(w => Vector2.SqrMagnitude(w.Position - at)).First();
            BuildingRecord weakFar = ReplaceWall(s, far, B1);
            ReplaceWall(s, weak, B3);
            WorldSimulation.StepMany(2);
            int target2 = site.SiegeBreachAhead(1, gc, 400);
            string target2Id = SiegeService.BuildingIdOfUnit(s, site, target2);
            BuildingRecord t2b = target2Id != null ? HomeGridService.FindBuilding(s, target2Id) : null;
            bool nearT3 = t2b != null && t2b.BuildingTypeId == B3 && Vector2.Distance(t2b.Position, at) < Vector2.Distance(weakFar.Position, at) - 10f;
            Expect(nearT3 && Fields().EndsWith(" 0 格不一致"),
                $"D3 最薄弱的那段在家园另一侧（绕路比破一段 T3 更贵）：改拆近处的 T3（{t2b?.BuildingTypeId}，离集结点 {(t2b != null ? Vector2.Distance(t2b.Position, at) : -1):F0} 米 vs T1 {Vector2.Distance(weakFar.Position, at):F0} 米）；换墙后 {Fields()}");
        }

        /// <summary>把一段墙换成另一个等级（拆掉旧的、原地登记新的）。</summary>
        private static BuildingRecord ReplaceWall(CampaignState s, BuildingRecord w, string type)
        {
            GridCell c = CellOfB(w);
            s.BuildingRecords = s.BuildingRecords.Where(x => x != w).ToArray();
            HomeGridService.MapFor(s);
            DefenseService.Sync(s);
            BuildingRecord n = Register(s, type, c);
            NavService.SyncGridChanges();
            return n;
        }

        private static void CheckBreachJourney()
        {
            CampaignState s = NewWorld(7008);
            CombatSite site = Site;
            BuildingRecord core = CoreOf(s);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out int gaps);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f);
            BuildingRecord nearest = NearestWall(ring, rmin, rmax, at);
            BuildingRecord weak = ReplaceWall(s, nearest, B1);
            int breachNotes = NotifyCount("raid_breach");
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 10 });
            bool breaching = StepUntil(() => Enumerable.Range(0, site.SiegeBreachCount).Any(i => site.SiegeBreachAt(i) == UnitOfDefense(s, weak)), 60);
            bool notified = StepUntil(() => NotifyCount("raid_breach") > breachNotes, 20) && LastNotify("raid_breach").Contains(BuildingOps.NameOf(weak));
            bool down = StepUntil(() => weak.ConstructionState == BuildingConstructionState.Damaged, 120);
            Seconds(1f);
            int2 gc = new int2((int)at.x, (int)at.y);
            bool opened = site.SiegeDistAt(0, gc) < CombatSiegeConst.Inf;
            string fieldsAfterBreach = Fields();
            float coreBefore = Durability(s, core);
            bool inside = StepUntil(() => Durability(s, core) < coreBefore - 10f, 120);
            Expect(breaching && notified && down && opened && inside && fieldsAfterBreach.EndsWith(" 0 格不一致") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SiegeFirstBreach),
                $"D4 FGT-DEF-005 完全堵死：敌人拆最短路线上最薄弱的那段（T1）屏障（内核破墙目标 {breaching}），玩家看得到在拆哪段墙（破墙通知“{Short(LastNotify("raid_breach"))}”）；" +
                $"墙被拆 → 寻路镜像让开 → 流场增量更新后开路可达（{opened}，{fieldsAfterBreach}）→ 敌人从缺口进来打核心（{inside}）");

            // 负向“屏障在突袭中新建”：在缺口上重新放一段墙 → 流场随之更新（重新围死），敌人再去拆。
            GridCell gapCell = CellOfB(weak);
            s.BuildingRecords = s.BuildingRecords.Where(x => x != weak).ToArray();
            HomeGridService.MapFor(s);
            BuildingRecord refill = CanPlace(s, B3, gapCell) ? Register(s, B3, gapCell) : null;
            WorldSimulation.StepMany(3);
            bool reclosed = refill != null && site.SiegeDistAt(0, gc) >= CombatSiegeConst.Inf && Fields().EndsWith(" 0 格不一致");
            Expect(reclosed, $"D5 负向“屏障在突袭中新建”：缺口上补一段 T3 → 流场增量更新、开路重新到不了（{Fields()}），敌人改沿破墙流场继续拆");
        }

        // ── E 撤退 ─────────────────────────────────────────────────────────────

        private static void CheckRetreatLosses()
        {
            CampaignState s = NewWorld(7009);
            CombatSite site = Site;
            Vector2 at = OutsidePoint(s, 40f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 10 });
            Seconds(2f);
            var ids = new List<int>();
            site.SiegeRaiderIds(Key(g), ids);
            for (int i = 0; i < 7; i++)
            {
                site.Kernel.Kill(ids[i], 0); // 测试捷径：七成被击毁（击毁本身由炮塔 / 机器打出来，见炮塔自检）
            }
            Seconds(1f);
            bool notRetreating = !g.SiegeRetreat;
            site.Kernel.Kill(ids[7], 0);
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool ordered = g.SiegeRetreat && g.SiegeRetreatReason == SiegeService.ReasonLosses && LastNotify("raid_withdrawn").Contains("80%");
            bool retreatMode = ids.Skip(8).All(id => site.TryGetSiegeRaider(id, out _, out CombatSiegeUnit su, out _, out _, out _) && su.Mode == (byte)CombatSiegeMode.Retreat);
            bool exited = StepUntil(() => g.ExitedCount + Alive(g) == 2 && Alive(g) == 0, 120);
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool regrouped = g.State == TransitGroupState.Retreating && g.UnitCount == g.ExitedCount && g.UnitCount == 2;
            Expect(notRetreating && ordered && retreatMode && exited && regrouped && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SiegeFirstRetreat),
                $"E1 FGR-DEF-032 损失超过 70% 撤退：损失 70% 时还在攻城（{notRetreating}），80% 时全队改走撤退流场（{ordered}/{retreatMode}，通知“{Short(LastNotify("raid_withdrawn"))}”）；" +
                $"走回集结点的离场（{g.ExitedCount} 台），全部离场后并回行进队伍、沿原路回据点（状态 {g.State}，{g.UnitCount} 台）");
        }

        private static void CheckRetreatTime()
        {
            CampaignState s = NewWorld(7010);
            Vector2 at = OutsidePoint(s, 40f);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out _); // 围死、只有 T3：一个小时里拆不完
            at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f);
            TransitGroupRecord g = Arrive(s, at, "silent", new[] { "silent.scout" }, new[] { 3 });
            long limit = WorldTransitSystem.TimeLimitTicks;
            bool before = StepUntil(() => GameClock.Ticks - g.ArrivedAtTick >= limit - GameClock.StepHz, (int)(limit / GameClock.StepHz) + 5) && !g.SiegeRetreat;
            bool ordered = StepUntil(() => g.SiegeRetreat, 5) && g.SiegeRetreatReason == SiegeService.ReasonTime;
            bool gone = StepUntil(() => WorldTransitSystem.Find(s, g.GroupId)?.State == TransitGroupState.Retreating, 120) && g.UnitCount == g.ExitedCount && g.ExitedCount > 0;
            Expect(before && ordered && gone,
                $"E2 FGR-DEF-032 时间上限：到达后 1 个游戏小时（{limit / (double)GameClock.StepHz:F0} 秒模拟时间）内一直攻城（{before}），到点撤退（{g.SiegeRetreatReason}），走回集结点后并回行进队伍原路返回（{g.State}）");
        }

        private static void CheckAnnihilation()
        {
            CampaignState s = NewWorld(7011);
            CombatSite site = Site;
            Vector2 at = OutsidePoint(s, 40f);
            TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 5 });
            var plan = new RaidPlanRecord { PlanId = "plan-sgtest", Serial = 991, State = RaidDirectorService.StateDeparted, GroupId = g.GroupId, Faction = "foundry", Level = 1, ArrivedTick = g.ArrivedAtTick };
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            d.Plans = d.Plans.Append(plan).ToArray();
            g.PlanId = plan.PlanId;
            Seconds(1f);
            var ids = new List<int>();
            site.SiegeRaiderIds(Key(g), ids);
            foreach (int id in ids)
            {
                site.Kernel.Kill(id, 0);
            }
            int destroyedNotes = NotifyCount("raid_destroyed");
            Seconds(SiegeCatalog.SyncSeconds + 0.2f);
            bool removed = WorldTransitSystem.Find(s, g.GroupId) == null && NotifyCount("raid_destroyed") > destroyedNotes;
            Seconds(2f);
            RaidHistoryRecord h = RaidDirectorService.History(s).LastOrDefault(x => x.PlanId == plan.PlanId);
            bool theaterOff = !SiegeService.StateOf(s).TheaterActive && site.SiegeStructUnitCount == 0 && ReactionAttribution.Current(s, HomeValleyLayout.RegionId, false) == null;
            Expect(removed && h != null && h.EndReason == RaidDirectorService.EndDestroyed && theaterOff && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SiegeFirstDestroyed),
                $"E3 全歼：队伍移除、发“突袭部队被全歼”（{removed}）；计划进历史结束原因 = {h?.EndReason}（突袭历史 FG6-DEF-08 读它）；没有队伍攻城了 → 剧场关、建筑结构单位移出内核、反应归因场次结束（{theaterOff}）");
        }

        // ── F 正式旅程（剧情突袭 → 出发 → 沿地形行进 → 到达展开）───────────────────────

        private static CampaignState FormalArrival(int seed, out TransitGroupRecord g, bool observe = true)
        {
            CampaignState s = NewWorld(seed, observe);
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if (GameClock.Ticks < grace)
            {
                GameClock.SkipForTests(s, grace - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
            g = null;
            if (p == null || !StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120))
            {
                return s;
            }
            long warn = p.WarnTick - 2;
            if (warn > GameClock.Ticks && WorldTransitSystem.Groups(s).All(x => x == null))
            {
                GameClock.SkipForTests(s, warn - GameClock.Ticks);
            }
            if (!StepUntil(() => p.State >= RaidDirectorService.StateDeparted, 900))
            {
                return s;
            }
            TransitGroupRecord found = WorldTransitSystem.Find(s, p.GroupId);
            if (found == null || !StepUntil(() => found.Engaged, 1800))
            {
                return s;
            }
            g = found;
            return s;
        }

        private static void CheckFormalJourney()
        {
            CampaignState s = FormalArrival(6151, out TransitGroupRecord g);
            CombatSite site = Site;
            int alive = g != null ? Alive(g) : 0;
            int units = g?.UnitCounts.Sum() ?? -1;
            Expect(g != null && g.Engaged && alive == units && alive > 0 && !string.IsNullOrEmpty(g.PlanId) && SiegeService.StateOf(s).TheaterActive,
                $"F1 正式流程：剧情突袭 → 排定 → 预警 → 出发 → 沿地形行进 → 到达家园 → 按编成展开成 {alive} 台战斗单位（编成 {units} 台）；行进队伍不再在外围停留到时间上限（DEBT-FG6DEF04-02 / DEBT-FG0ARCH03-01）");
        }

        // ── G / H / I / J 存读档、倍速、观察、种子集 ───────────────────────────────────

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

        /// <summary>一场进行中的攻城：围死（一段 T1）、混编（突击 / 攻城 / 维修）；跑 <paramref name="seconds"/> 游戏秒。</summary>
        private static CampaignState SiegeScenario(int seed, bool observe, float seconds)
        {
            CampaignState s = NewWorld(seed, observe);
            List<BuildingRecord> ring = Ring(s, 4, B3, out GridCell rmin, out GridCell rmax, out _);
            Vector2 at = OutsidePoint(s, Math.Max(rmax.X - rmin.X, rmax.Y - rmin.Y) * 0.5f + 12f);
            BuildingRecord nearest = NearestWall(ring, rmin, rmax, at);
            ReplaceWall(s, nearest, B1);
            Arrive(s, at, "foundry", new[] { "foundry.strider", "foundry.armorbot", "foundry.repairbot" }, new[] { 8, 4, 2 }, new[] { 1, 1, 0 });
            Seconds(seconds);
            return s;
        }

        private static string Signature(CampaignState s)
        {
            CombatSite site = Site;
            return SiegeService.Snapshot(s) + "|H" + (site != null ? site.Kernel.StateHash().ToString("X16") : "-");
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = SiegeScenario(7012, true, 12f);
            string before = Signature(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(20f);
            string continuous = Signature(s);
            CampaignState l = RestoreSlot(out string fail);
            string loaded = l != null ? Signature(l) : "读档失败：" + fail;
            if (l != null)
            {
                Seconds(20f);
            }
            string after = l != null ? Signature(l) : string.Empty;
            int bad = Site != null ? Site.VerifySiegeFields(out _) : -1;
            Expect(save.Success && loaded == before && after == continuous && bad == 0,
                "G1 FG06 第 5 章“突袭期间存档”：真文件存读档后敌人位置 / 耐久 / 职能 / 撤退、剧场、建筑结构单位、内核状态哈希逐位一致；流场读档后按同一输入重算、与增量维护一致；读档后接着跑 20 游戏秒与不存档连续跑逐位一致"
                + (loaded == before ? string.Empty : $"\n存前：{Short(before)}\n读后：{Short(loaded)}") + (after == continuous ? string.Empty : $"\n连续：{Short(continuous)}\n读档：{Short(after)}"));
        }

        private static string RunTiming(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = SiegeScenario(seed, observe, 2f);
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
                string snap = RunTiming(7013, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{Short(snap)}\n参照：{Short(reference)}";
                }
                same &= snap == reference;
            }
            Expect(paused && same, "H 暂停中（90 帧）攻城单位、流场、建筑耐久都不动；0.5x / 1x / 2x / 3x 跑同样的 40 游戏秒攻城（破墙、进攻、随队维修）逐位一致" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunTiming(7014, true, 1f, false, out _);
            string unseen = RunTiming(7014, false, 1f, false, out _);
            Expect(seen == unseen, "I 同一场攻城在观察与不观察家园时跑 40 游戏秒逐位一致（FGR-BASE-021：远征时家园照常被攻城）"
                                   + (seen == unseen ? string.Empty : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
        }

        private static void CheckSeedSet()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 7101, 7202 })
            {
                CampaignState s = NewWorld(seed);
                BuildingRecord core = CoreOf(s);
                Vector2 at = OutsidePoint(s, 38f, seed % 16);
                TransitGroupRecord g = Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 6 });
                float c0 = Durability(s, core);
                bool hit = StepUntil(() => Durability(s, core) < c0 - 10f, 90);
                int bad = Site.VerifySiegeFields(out int n);
                all &= g.Engaged && hit && bad == 0 && n > 0;
                lines.Add($"种子 {seed}：展开 {g.UnfoldedCount}、打到核心 {hit}、流场 {n} 张一致 {bad == 0}");
            }
            Expect(all, "J B25 种子测试集：两个种子的家园地形 / 布局不同，攻城展开、沿流场打到核心、增量流场一致都成立（不依赖固定坐标）：" + string.Join("；", lines));
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
