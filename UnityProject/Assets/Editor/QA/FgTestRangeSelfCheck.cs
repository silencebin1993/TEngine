using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
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
using GameLogic.UI.CircuitBoard;
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
    /// FG5-RND-03 靶场与仿真投影的自动验收（FG05 FGR-RND-030～033；FG13 FGU-24；FGT-RND-004；负向“测试中存档”“投影同时存在上限”“投影中静默夜开始”）。
    /// 全部起真实系统：真实家园（世界模拟、电网）、真实靶场建筑、靶场自己的 Main/Sim 战斗内核（弹体 / 反应 / 读法与真实机器同一套）、真文件存读档、真 UXML 面板。
    /// A 数据；B 建筑与默认布置；C FGT-RND-004（不消耗资源、只在靶场内核里、内核真打、重炮走弹体）；D 不出靶场；E 测试中存档（测试结束、不进存档、读档空闲）；
    /// F 接入（接入态编译、核心固件冷却只在投影里模拟、信号在机器里被拒 / 信号移走、静默夜断链投影消失）；G 上限与负向；H 靶子（解锁、旧档补记、五种原型的行为）；
    /// I 读数（反应 / 标签覆盖 / 热量 / 能耗、对比、记录上限）与反应日志的名字（FG-GAP-061）；J 预设；K 暂停与 0.5x～3x；L 观察 / 不观察一致；
    /// M 快捷入口（蓝图编辑器 / 固件库）；N 面板（真 UXML、布局探针）；O 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgTestRangeSelfCheck）。
    /// </summary>
    public static class FgTestRangeSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 6;
        private const string BpGun = "bp_range_gun";
        private const string BpCannon = "bp_range_cannon";
        private const string BpWet = "bp_range_wet";
        private const string BpShock = "bp_range_shock";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 靶场与仿真投影")]
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
            Line("\n[靶场与仿真投影] 投影不消耗资源、不出靶场、不进存档、可以被接入；靶子按击败的敌人解锁；读数与对比（FG5-RND-03）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgrange-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                AnalysisCatalog.Reload();
                TestRangeCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                TestRangeService.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；靶场的投影 / 靶子 / 弹体在 AOT 战斗内核（Burst），会话与读数在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行；真机另测 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckBuildingAndDefaults);
                Step(CheckFreeAndKernel);
                Step(CheckLateStart);
                Step(CheckStaysInRange);
                Step(CheckDisplacersStayInRange);
                Step(CheckSaveEndsTest);
                Step(CheckUplink);
                Step(CheckSilentNight);
                Step(CheckCapAndNegative);
                Step(CheckTargets);
                Step(CheckReadingsAndLog);
                Step(CheckReactionKillLabel);
                Step(CheckLayoutPrune);
                Step(CheckPresets);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckShortcuts);
                Step(CheckPanel);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"靶场自检抛异常：{e}");
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
                TestRangeService.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
                FirmwareKinds.ResetForTests();
                SignalCoreService.ResetForTests();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
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
                TestRangePanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [靶场与仿真投影] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>一个有电的家园 + 一座运转中的靶场（按种子地形找空地，不写死坐标，B25）+ 四张测试蓝图。</summary>
        private static CampaignState NewWorld(int seed, out BuildingRecord range, bool observe = true)
        {
            TestRangeService.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            SignalUplinkService.SilentNightProvider = null;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 900);
            FgProductionSelfCheck.PowerUp(s);
            range = PlaceRange(s, "range");
            AddBlueprint(s, BpGun, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            cannon.TrySetUplink(2);
            AddBlueprint(s, BpCannon, cannon);
            AddBlueprint(s, BpWet, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_coolant" }));
            AddBlueprint(s, BpShock, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_arcchain" }));
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：在接得上电网的空地上登记一座建成、满耐久的靶场（由近到远按种子地形找，B25；接不上的位置撤掉再找）。施工流程由 FG3-LOG-02 覆盖。</summary>
        private static BuildingRecord PlaceRange(CampaignState s, string key)
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
                    BuildingRecord b = FgProductionSelfCheck.Built(s, TestRangeCatalog.TypeId, key, c);
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
            Fail("测试准备：家园里找不到接得上电网、能放 8×8 靶场的空地");
            return null;
        }

        /// <summary>与 Python repr(float) 对齐（表里的 float 按单精度往返写法）。</summary>
        private static string Fl(float v)
        {
            string t = v.ToString("R", CultureInfo.InvariantCulture);
            return t.Contains(".") || t.Contains("E") ? t : t + ".0";
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static void SetLayout(CampaignState s, string rangeId, params string[] slots)
        {
            TestRangeService.ClearSlots(s, rangeId);
            for (int i = 0; i < slots.Length; i++)
            {
                RangeOpResult r = TestRangeService.SetSlot(s, rangeId, i, slots[i]);
                if (!r.Success)
                {
                    Fail($"测试准备：靶位 {i + 1} 放 {slots[i]} 失败：{r.Text}");
                }
            }
        }

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static string Json(object o) => o == null ? "null" : JsonUtility.ToJson(o);

        /// <summary>资源快照：废料、技术数据、研究点、全部物资、机器、芯片、蓝图（FGT-RND-004“不消耗资源”逐字段比）。</summary>
        private static string Resources(CampaignState s) =>
            $"scrap={s.Scrap} tech={s.TechData} rp={s.Research.Points} items={Json(s.Economy.Items)}|{string.Join(",", (s.Economy.Items ?? Array.Empty<ItemStackRecord>()).Select(Json))}|{string.Join(",", (s.Economy.Vault ?? Array.Empty<ItemStackRecord>()).Select(Json))} machines={s.MachineRecords?.Length ?? 0}|{MachineRegistry.AllRecords.Count} " +
            $"chips={s.PrimitiveChips?.Length ?? 0} bp={string.Join(",", (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Select(b => b.BlueprintId + ":" + b.ActiveVersion + ":" + (b.Versions?.Length ?? 0)))}";

        /// <summary>靶场世界矩形（含边）。</summary>
        private static void Bounds(BuildingRecord b, out Vector2 min, out Vector2 max)
        {
            BuildingGridOf(out int w, out int h);
            GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), w, h, GridMath.NormalizeRotation(b.Rotation), out GridCell lo, out GridCell hi);
            min = new Vector2(lo.X - 0.5f, lo.Y - 0.5f);
            max = new Vector2(hi.X + 0.5f, hi.Y + 0.5f);
        }

        private static void BuildingGridOf(out int w, out int h)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(TestRangeCatalog.TypeId);
            w = g.FootprintW;
            h = g.FootprintH;
        }

        private static int AliveOf(CombatKernel k, CombatFaction f) => k.CountAlive(f);

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = FgProductionSelfCheck.RunPython(FgProductionSelfCheck.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("RT\t")).ToArray();
            string[] rt = t.TbRangeTarget.DataList.Select(r => string.Join("\t", "RT", r.Id, r.NameKey, r.DescKey, r.Archetype, r.UnlockEnemies, Fl(r.Hp),
                Fl(r.Radius), r.Count, Fl(r.Armor), Fl(r.ArmorHalfAngle), Fl(r.Speed),
                Fl(r.PatrolRadius), r.SortOrder)).ToArray();
            Expect(code == 0 && src.Length == rt.Length && src.SequenceEqual(rt) && rt.Length >= 5,
                $"A1 fg.TbRangeTarget 与源数据 fgdata_range.py 逐字段一致（{src.Length} 行）" + (code == 0 ? (src.SequenceEqual(rt) ? string.Empty : $"\n源：{string.Join(" / ", src)}\n表：{string.Join(" / ", rt)}") : "：" + output));

            var archetypes = TestRangeCatalog.Targets.Select(d => d.Archetype).Distinct().ToList();
            bool five = Enum.GetValues(typeof(RangeTargetArchetype)).Cast<RangeTargetArchetype>().All(archetypes.Contains);
            bool building = t.TbBuilding.DataMap.ContainsKey(TestRangeCatalog.TypeId) && GridContent.TryGetBuilding(TestRangeCatalog.TypeId, out GameConfig.fg.BuildingGrid g)
                            && g.FootprintW == 8 && g.FootprintH == 8 && g.Placeable == 1 && g.Category == "research"
                            && BuildingOps.CodexIdOf(TestRangeCatalog.TypeId) == "codex.research.range";
            Expect(TestRangeCatalog.Problems.Count == 0 && five && building && TestRangeCatalog.ProjectionCap == 6 && TestRangeCatalog.SlotCount == 8
                   && TestRangeCatalog.Targets.Count(d => d.AlwaysUnlocked) >= 1,
                $"A2 五种靶子原型（标准 / 重甲 / 高速 / 集群 / 护盾）、至少一种一开始就有（B11）；靶场建筑 8×8、可放置、建造菜单“研发”、“?”指向图鉴“靶场与仿真投影”；同时投影上限 6（FG05 第 7 节）、靶位 8"
                + (TestRangeCatalog.Problems.Count > 0 ? "；问题：" + string.Join("；", TestRangeCatalog.Problems) : string.Empty));

            string fgRange = File.ReadAllText(Path.Combine(FgProductionSelfCheck.LocateRepo(), "tools", "cell_tables", "fgdata_range.py"));
            // 文本条目 = ("键", "中文", "English")：第二项不是键 / ID 写法（排除靶子表行、钩子、通知类型、调参、图鉴行）。
            var keys = new System.Text.RegularExpressions.Regex("\\(\"([a-z0-9_.]+)\",\\s*\"([^\"]*)\"")
                .Matches(fgRange).Cast<System.Text.RegularExpressions.Match>()
                .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m.Groups[2].Value, "^[a-z0-9_.,]*$"))
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    if (!GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            // 代码里用到的 range.* 文本键也都在表里（扫描本 Story 的代码文件）。
            string root = Application.dataPath;
            var codeKeys = new HashSet<string>();
            foreach (string f in new[]
                     {
                         "GameScripts/HotFix/GameLogic/Campaign/Economy/TestRangeService.cs", "GameScripts/HotFix/GameLogic/Campaign/Combat/TestRangeService.Sim.cs",
                         "GameScripts/HotFix/GameLogic/UI/Kit/TestRangePanelUIToolkit.cs",
                     })
            {
                foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex("\"((?:range|reaction\\.log|bs\\.reason\\.range)\\.[a-z0-9_.]+)\"").Matches(File.ReadAllText(Path.Combine(root, f))))
                {
                    codeKeys.Add(m.Groups[1].Value);
                }
            }
            var codeMissing = codeKeys.Where(k => !GameText.Has(k)).ToList();
            bool hooks = new[]
            {
                GuidanceHooks.RangeFirstBuilt, GuidanceHooks.RangeFirstOpen, GuidanceHooks.RangeFirstTest, GuidanceHooks.RangeFirstUplink,
                GuidanceHooks.RangeFirstCompare, GuidanceHooks.RangeFirstSend, GuidanceHooks.RangeFirstTargetUnlocked,
            }.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType("range_ended", out _) && NotificationCatalog.TryGetType("range_target_unlocked", out _);
            Expect(keys.Count >= 60 && missing.Count == 0 && codeKeys.Count >= 40 && codeMissing.Count == 0 && hooks && notify,
                $"A3 靶场文本 {keys.Count} 个键中英齐全（扫到代码里用到 {codeKeys.Count} 个，全部在表里）；7 个引导钩子在 GuidanceHooks.Known；两种通知类型登记"
                + (missing.Count + codeMissing.Count > 0 ? "；缺：" + string.Join("，", missing.Concat(codeMissing).Take(12)) : string.Empty));
        }

        // ── B 建筑与默认布置 ─────────────────────────────────────────────────────

        private static void CheckBuildingAndDefaults()
        {
            CampaignState s = NewWorld(9301, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            bool usable = TestRangeService.IsUsable(s, range, out string why);
            string[] layout = TestRangeService.Layout(s, range.BuildingId);
            bool defaults = layout.Length == 8 && layout.Take(4).All(x => x == "range.basic") && layout.Skip(4).All(string.IsNullOrEmpty);
            bool readOnly = s.Research.Range.Layouts.Length == 0;
            BuildingStatus st = BuildingStatusService.Evaluate(s, range);
            bool hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstBuilt) && MechanicCodex.IsUnlocked("codex.research.range");
            Expect(usable && defaults && readOnly && st.Kind == BuildingStatusKind.Idle && st.Reason.Contains("投影") && hook,
                $"B1 建成有电的靶场可以测试（{usable} {why}）；默认布置前排 4 个标准靶、后排空（读布置不写存档：{readOnly}）；建筑状态“空闲”并写明怎么开始（“{st.Reason}”）；第一次建成发引导钩子、图鉴解锁（{hook}）");

            range.ConstructionState = BuildingConstructionState.Disabled;
            bool disabledRejected = !TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun).Success;
            range.ConstructionState = BuildingConstructionState.Operational;
            range.PowerState = BuildingPowerState.Unpowered;
            RangeOpResult unpowered = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            HomeValleyPowerGrid.Recompute(s);
            Expect(disabledRejected && !unpowered.Success && unpowered.Failure == RangeFailure.NotWorking && unpowered.Text.Contains("靶场现在不能用") && TestRangeService.ActiveSessions == 0,
                $"B2 靶场禁用 / 缺电时投影被拒并写明原因（“{unpowered.Text}”），不开测试");
        }

        // ── C FGT-RND-004：不消耗资源、只在靶场内核里、内核真打 ─────────────────────────

        private static void CheckFreeAndKernel()
        {
            CampaignState s = NewWorld(9302, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            string before = Resources(s);
            int sitesBefore = CombatSites.Count;
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            long homeShots0 = home.Kernel.Counters.ShotsFired;
            double homeDamage0 = home.Kernel.DamageDealtToHostile;
            RangeOpResult a = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            RangeOpResult b = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpCannon);
            CombatSite site = TestRangeService.SiteOf(range.BuildingId);
            Seconds(20f);
            string after = Resources(s);
            CombatKernel k = site?.Kernel;
            RangeResultRecord live = TestRangeService.LiveReadings(range.BuildingId);
            bool inKernel = k != null && k.CountAlive(CombatFaction.Player) == 2 && AliveOf(k, CombatFaction.Hostile) >= 1;
            bool isolated = CombatSites.Count == sitesBefore && !CombatSites.All.Contains(site) && CombatSites.Get(site?.SiteId) == null
                            && home.Kernel.Counters.ShotsFired == homeShots0 && Math.Abs(home.Kernel.DamageDealtToHostile - homeDamage0) < 1e-6;
            bool fought = live != null && live.Damage > 0f && live.Shots > 0 && k.Counters.ShotsFired > 0;
            // 与真实机器同一条翻译：家园里造一台同一张蓝图的真机器，它在家园内核里的武器参数与投影在靶场内核里的逐字节相同（不另起一套武器 / 弹道）。
            MachineOpResult real = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpCannon, HomeValleyLayout.RegionId, HomeValleyLayout.Core.Position + new Vector2(3f, 3f), 200f, 200f, "Player", 1);
            MachineLoadoutRegistry.Register(s, real.LogicId, BpCannon, 1);
            WorldSimulation.StepMany(3);
            bool sameWeapon = false;
            string weaponNote = "没找到";
            if (real.Success && home.TryGetMachineWeapon(real.LogicId, out MachineWeaponInfo realInfo) && home.Kernel.TryGetWeapon(realInfo.WeaponIndex, out CombatWeapon rw))
            {
                RangeProjectionView cannonView = TestRangeService.ProjectionsOf(range.BuildingId).First(v => v.Label == BpCannon);
                k.TryGetUnit(cannonView.UnitId, out CombatUnitView pv);
                if (k.TryGetWeapon(pv.Weapon, out CombatWeapon pw))
                {
                    sameWeapon = CombatKernel.WeaponKey(rw) == CombatKernel.WeaponKey(pw);
                    weaponNote = $"{rw.Mode} 伤害 {rw.Damage:F0} 射程 {rw.Range:F0}";
                }
            }
            Expect(a.Success && b.Success && before == after,
                $"C1 FGT-RND-004 投影两张蓝图（连射器、重炮）跑 20 游戏秒：废料 / 技术数据 / 研究点 / 物资 / 机器 / 芯片 / 蓝图逐字段不变（不消耗任何资源、不需要造出机器）"
                + (before == after ? string.Empty : $"\n前：{before}\n后：{after}"));
            Expect(inKernel && isolated && fought && sameWeapon,
                $"C2 投影只在靶场自己的战斗内核里（{k?.CountAlive(CombatFaction.Player)} 个己方投影、{(k != null ? AliveOf(k, CombatFaction.Hostile) : 0)} 个投影靶；不登记进战斗地点表 {CombatSites.Count == sitesBefore}、不进机器名册；"
                + $"家园内核开火 / 伤害不变 {home.Kernel.Counters.ShotsFired == homeShots0}）；内核真打：伤害 {live?.Damage:F1}、发数 {live?.Shots}；重炮投影的武器参数与家园里同一张蓝图的真机器逐字节相同（{weaponNote}，{sameWeapon}）——开火、命中、反应都走 Main/Sim 同一个战斗内核，不另起模拟");
        }

        /// <summary>冒烟抓到的回归：游戏已经过了很久（远超最长测试时长）时开始测试，时长从开始算，不会一开始就“到时”结束。</summary>
        private static void CheckLateStart()
        {
            CampaignState s = NewWorld(9316, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            s.Clock.Ticks = GameClock.Ticks + (long)GameClock.StepHz * 400;
            GameClock.Bind(s);
            double t0 = GameClock.GameSeconds;
            RangeOpResult p = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            Seconds(5f);
            RangeResultRecord live = TestRangeService.LiveReadings(range.BuildingId);
            bool ok = p.Success && t0 > 390 && TestRangeService.IsRunning(range.BuildingId) && live != null && live.Seconds > 4.5f && live.Seconds < 5.5f
                      && Mathf.Abs(TestRangeService.ElapsedSeconds(range.BuildingId) - live.Seconds) < 0.01f;
            TestRangeService.EndTest(s, range.BuildingId);
            RangeResultRecord r = s.Research.Range.History.LastOrDefault();
            Expect(ok && r != null && r.EndReason == (int)RangeEndReason.Player && r.Seconds < 6f,
                $"C3 游戏时间已过 {t0:F0} 秒时开始测试：5 游戏秒后测试还在进行、时长 {live?.Seconds:F2} 秒（从开始算，不会一开始就到达最长时长 {TestRangeCatalog.MaxTestSeconds:F0} 秒）；手动结束的记录 {r?.Seconds:F2} 秒");
        }

        // ── D 不出靶场 ─────────────────────────────────────────────────────────

        private static void CheckStaysInRange()
        {
            CampaignState s = NewWorld(9303, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            // 高速靶需要先击败静默侦察机；集群靶需要静默干扰机（直接记一次击败，解锁路径本身在 H 段）。
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.ScoutId);
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.JammerId);
            SetLayout(s, range.BuildingId, "range.fast", "range.swarm", "range.fast", "range.basic", "range.fast", "range.swarm", "range.fast", "range.basic");
            for (int i = 0; i < 6; i++)
            {
                TestRangeService.ProjectBlueprint(s, range.BuildingId, i % 2 == 0 ? BpGun : BpCannon);
            }
            CombatSite site = TestRangeService.SiteOf(range.BuildingId);
            Bounds(range, out Vector2 min, out Vector2 max);
            int samples = 0;
            int outside = 0;
            string worst = string.Empty;
            float fastTravel = 0f;
            Vector2? fastStart = null;
            for (int t = 0; t < 120 && site != null && !site.IsDisposed; t++)
            {
                Seconds(0.5f);
                CombatKernel k = site.Kernel;
                for (int i = 0; i < k.SlotCount; i++)
                {
                    CombatUnitView v = k.ViewAt(i);
                    if ((v.Flags & CombatUnitFlags.Alive) == 0)
                    {
                        continue;
                    }
                    samples++;
                    var p = new Vector2((float)v.Position.x, (float)v.Position.y);
                    if (p.x < min.x - 1e-3f || p.y < min.y - 1e-3f || p.x > max.x + 1e-3f || p.y > max.y + 1e-3f)
                    {
                        outside++;
                        worst = $"{v.Kind}/{v.Faction} ({p.x:F2},{p.y:F2})";
                    }
                    if (v.Behavior == CombatBehavior.Scout)
                    {
                        fastStart ??= p;
                        fastTravel = Mathf.Max(fastTravel, Vector2.Distance(fastStart.Value, p));
                    }
                }
            }
            Expect(samples > 2000 && outside == 0 && fastTravel > 0.5f,
                $"D 6 个投影 × 8 个靶位（高速 / 集群混放）跑 60 游戏秒、每 0.5 秒采样全部存活单位 {samples} 次：没有一个离开靶场 [{min.x:F1},{min.y:F1}]～[{max.x:F1},{max.y:F1}]"
                + (outside > 0 ? $"（越界 {outside} 次，例 {worst}）" : string.Empty) + $"；高速靶确实在动（最大位移 {fastTravel:F2} 米）");
        }

        private const string BpShovel = "bp_range_shovel";
        private const string BpPulser = "bp_range_pulser";
        private const string BpDrone = "bp_range_drone";
        private const string BpTractor = "bp_range_tractor";

        /// <summary>D2：会把靶子推走 / 拉走 / 放无人机的投影（推铲 + 壁撞、脉冲器 + 偏折、无人机舱、牵引固件）跑满 60 秒，单位、无人机、弹体都不出靶场；
        /// 对照组同一场景关掉内核场地边界，确实会越界（证明边界是内核钳住的，不是摆位碰巧）。</summary>
        private static void CheckDisplacersStayInRange()
        {
            int RunOnce(bool arena, out int samples, out float pushed, out string worst, out int drones, out int shots)
            {
                CampaignState s = NewWorld(9313, out BuildingRecord range);
                samples = 0;
                pushed = 0f;
                worst = string.Empty;
                drones = 0;
                shots = 0;
                if (range == null)
                {
                    return -1;
                }
                AddBlueprint(s, BpShovel, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompShovelId, null, null, new[] { "fw_wallslam" }));
                AddBlueprint(s, BpPulser, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompPulserId, null, null, new[] { "fw_deflect" }));
                AddBlueprint(s, BpDrone, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, Array.Empty<string>()));
                AddBlueprint(s, BpTractor, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_tractor" }));
                SetLayout(s, range.BuildingId, "range.basic", "range.basic", "range.basic", "range.basic", "range.basic", "range.basic", "range.basic", "range.basic");
                foreach (string bp in new[] { BpShovel, BpPulser, BpDrone, BpTractor, BpShovel, BpCannon })
                {
                    RangeOpResult r = TestRangeService.ProjectBlueprint(s, range.BuildingId, bp);
                    if (!r.Success)
                    {
                        Fail($"D2 测试准备：投影 {bp} 失败：{r.Text}");
                    }
                }
                CombatSite site = TestRangeService.SiteOf(range.BuildingId);
                if (site == null)
                {
                    return -1;
                }
                if (!arena)
                {
                    site.Kernel.ClearArena();
                }
                Bounds(range, out Vector2 min, out Vector2 max);
                bool Out(double x, double y) => x < min.x - 1e-3 || y < min.y - 1e-3 || x > max.x + 1e-3 || y > max.y + 1e-3;
                var first = new Dictionary<int, Vector2>();
                int outside = 0;
                for (int t = 0; t < 240 && !site.IsDisposed; t++)
                {
                    Seconds(0.25f);
                    CombatKernel k = site.Kernel;
                    for (int i = 0; i < k.SlotCount; i++)
                    {
                        CombatUnitView v = k.ViewAt(i);
                        if (!v.Alive)
                        {
                            continue;
                        }
                        samples++;
                        var p = new Vector2((float)v.Position.x, (float)v.Position.y);
                        if (v.Faction == CombatFaction.Hostile)
                        {
                            if (!first.TryGetValue(v.Id, out Vector2 p0))
                            {
                                first[v.Id] = p;
                            }
                            else
                            {
                                pushed = Mathf.Max(pushed, Vector2.Distance(p0, p));
                            }
                        }
                        if (Out(p.x, p.y))
                        {
                            outside++;
                            worst = $"{v.Kind}/{v.Faction} ({p.x:F2},{p.y:F2})";
                        }
                    }
                    for (int i = 0; i < k.DroneCount; i++)
                    {
                        if (k.TryGetDrone(i, out CombatDrone dr) && dr.Until > k.Time)
                        {
                            drones++;
                            if (Out(dr.Pos.x, dr.Pos.y))
                            {
                                outside++;
                                worst = $"无人机 ({dr.Pos.x:F2},{dr.Pos.y:F2})";
                            }
                        }
                    }
                    for (int i = 0; i < k.ProjectileCount; i++)
                    {
                        if (k.TryGetProjectile(i, out CombatProjectile pr))
                        {
                            shots++;
                            if (Out(pr.Pos.x, pr.Pos.y))
                            {
                                outside++;
                                worst = $"弹体 ({pr.Pos.x:F2},{pr.Pos.y:F2})";
                            }
                        }
                    }
                }
                TestRangeService.EndTest(s, range.BuildingId);
                return outside;
            }

            int outsideOn = RunOnce(true, out int samplesOn, out float pushedOn, out string worstOn, out int dronesOn, out int shotsOn);
            int outsideOff = RunOnce(false, out _, out float pushedOff, out string worstOff, out _, out _);
            Expect(outsideOn == 0 && samplesOn > 2000 && pushedOn > 0.5f && dronesOn > 0,
                $"D2 FGR-RND-031 / FGT-RND-004：推铲 + 壁撞 ×2、脉冲器 + 偏折、无人机舱、牵引固件、重炮 6 个投影 × 8 个标准靶跑 60 游戏秒，每 0.25 秒采样单位 {samplesOn} 次、无人机 {dronesOn} 次、弹体 {shotsOn} 次（机器武器是即时命中 / 重炮，靶场里一般没有直线弹体；有的话出界即作废）："
                + $"全部在靶场内（越界 {outsideOn} 次{(outsideOn > 0 ? "，例 " + worstOn : string.Empty)}）；靶子确实被推动（最大位移 {pushedOn:F2} 米）、无人机确实放出来了");
            Expect(outsideOff > 0 && pushedOff > pushedOn,
                $"D2 对照：同一场景关掉内核场地边界后越界 {outsideOff} 次（例 {worstOff}，靶子最大位移 {pushedOff:F2} 米）——不出靶场是内核边界钳住的，不是摆位碰巧");
        }

        // ── E 测试中存档 ───────────────────────────────────────────────────────

        private static void CheckSaveEndsTest()
        {
            CampaignState s = NewWorld(9304, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            SetLayout(s, range.BuildingId, "range.basic", "range.basic", "", "", "range.basic");
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            Seconds(8f);
            int hist0 = s.Research.Range.History.Length;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            RangeResultRecord saved = s.Research.Range.History.LastOrDefault();
            bool ended = save.Success && TestRangeService.ActiveSessions == 0 && TestRangeService.ProjectionCount == 0 && s.Research.Range.History.Length == hist0 + 1
                         && saved != null && saved.EndReason == (int)RangeEndReason.Saved && saved.Seconds > 7f && saved.Damage > 0f;
            string file = File.ReadAllText(CampaignSaveService.SlotPath(Slot));
            bool notSaved = !file.Contains(TestRangeService.SitePrefix) && file.Contains("DefeatedEnemyTypes") && file.Contains("HeatCurve");
            bool notified = NotificationCenter.History.Any(e => e.Type.Id == "range_ended" && e.Text.Contains("存档"));
            Expect(ended && notSaved && notified,
                $"E1 测试中存档：测试直接结束、投影消失（进行中 {TestRangeService.ActiveSessions}），读数记进测试记录（结束原因“存档”、{saved?.Seconds:F1} 秒、伤害 {saved?.Damage:F1}）；"
                + $"存档文件里没有靶场的仿真地点（扫 “{TestRangeService.SitePrefix}” {!file.Contains(TestRangeService.SitePrefix)}；对照：靶场域在 {file.Contains("DefeatedEnemyTypes")}）；发“靶场测试结束”通知（{notified}）");

            string layoutBefore = string.Join(",", TestRangeService.Layout(s, range.BuildingId));
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("E2 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            Seconds(3f);
            CampaignState l = rr.State;
            BuildingRecord lr = TestRangeService.FindRange(l, range.BuildingId);
            bool idle = TestRangeService.ActiveSessions == 0 && lr != null && BuildingStatusService.Evaluate(l, lr).Kind == BuildingStatusKind.Idle;
            bool kept = l.Research.Range.History.Length == hist0 + 1 && Json(l.Research.Range.History.Last()) == Json(saved)
                        && string.Join(",", TestRangeService.Layout(l, range.BuildingId)) == layoutBefore;
            RangeOpResult again = TestRangeService.ProjectBlueprint(l, range.BuildingId, BpGun);
            Seconds(8f);
            Expect(idle && kept && again.Success && TestRangeService.LiveReadings(range.BuildingId)?.Damage > 0f,
                $"E2 读档后靶场空闲（没有投影）、测试记录与靶子布置都还在（逐字段一致 {kept}），可以马上再投影开始新测试（{again.Text}）");
            TestRangeService.EndTest(l, range.BuildingId);
        }

        // ── F 接入 ─────────────────────────────────────────────────────────────

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
                CampaignState s = NewWorld(9305, out BuildingRecord range);
                if (range == null)
                {
                    return;
                }
                EquipOverload(s);
                SetLayout(s, range.BuildingId, "range.basic", "range.basic", "range.basic", "range.basic");
                RangeOpResult p = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpCannon);
                CombatSite site = TestRangeService.SiteOf(range.BuildingId);
                int unit = TestRangeService.ProjectionsOf(range.BuildingId)[0].UnitId;
                site.Kernel.TryGetUnit(unit, out CombatUnitView v0);
                int weaponLocal = v0.Weapon;
                bool gatedLocal = (v0.Flags & CombatUnitFlags.ReactionGated) != 0;
                int coreCd0 = s.SignalCore.CoreCooldowns?.Length ?? 0;
                RangeOpResult up = TestRangeService.Uplink(s, range.BuildingId, p.Serial);
                site.Kernel.TryGetUnit(unit, out CombatUnitView v1);
                bool uplinked = up.Success && TestRangeService.TryGetUplinkedProjection(out string ub, out int us, out _) && ub == range.BuildingId && us == p.Serial
                                && v1.Weapon != weaponLocal && (v1.Flags & CombatUnitFlags.ReactionGated) != 0 && !gatedLocal
                                && SignalUplinkService.StatusLine(s).Contains("投影") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstUplink);
                Expect(uplinked,
                    $"F1 接入投影：信号进入投影（{up.Text}），按接入态重新编译（武器参数 {weaponLocal}→{v1.Weapon}，带“核心固件门控”{(v1.Flags & CombatUnitFlags.ReactionGated) != 0}；本地配置不带 {!gatedLocal}）；"
                    + $"信号位置 HUD 写“{SignalUplinkService.StatusLine(s)}”");

                int melt0 = 0;
                RangeResultRecord r0 = TestRangeService.LiveReadings(range.BuildingId);
                melt0 = r0.Reactions.Where(c => c.Id == MechanicalReactionCatalog.ReactionMeltOverloadId).Sum(c => c.Value);
                bool fired = FgProductionSelfCheck.StepUntil(() =>
                    (TestRangeService.LiveReadings(range.BuildingId)?.Reactions.Where(c => c.Id == MechanicalReactionCatalog.ReactionMeltOverloadId).Sum(c => c.Value) ?? 0) > melt0, 20);
                site.Kernel.TryGetUnit(unit, out CombatUnitView v2);
                bool cooling = TestRangeService.ProjectionsOf(range.BuildingId)[0].CoreCooling && (v2.Flags & CombatUnitFlags.ReactionGated) == 0;
                int coreCd1 = s.SignalCore.CoreCooldowns?.Length ?? 0;
                float cd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);
                Seconds(cd + 1f);
                site.Kernel.TryGetUnit(unit, out CombatUnitView v3);
                bool back = !TestRangeService.ProjectionsOf(range.BuildingId)[0].CoreCooling && (v3.Flags & CombatUnitFlags.ReactionGated) != 0;
                Expect(fired && cooling && back && coreCd0 == 0 && coreCd1 == 0,
                    $"F2 接入的重炮投影打出熔穿过载（核心固件发动）→ 投影里按信号规则冷却 {cd:F0} 秒（冷却中不再发动 {cooling}；结束后恢复 {back}）；真实信号核的冷却不动（{coreCd0}→{coreCd1} 条，零风险）");

                RangeOpResult leave = TestRangeService.LeaveUplink(s);
                site.Kernel.TryGetUnit(unit, out CombatUnitView v4);
                bool left = leave.Success && !TestRangeService.TryGetUplinkedProjection(out _, out _, out _) && v4.Weapon == weaponLocal && (v4.Flags & CombatUnitFlags.ReactionGated) == 0;
                RangeOpResult notUp = TestRangeService.LeaveUplink(s);
                Expect(left && !notUp.Success && notUp.Failure == RangeFailure.NotUplinked,
                    $"F3 退出接入：投影回到本地配置（武器参数回到 {weaponLocal}），继续打靶；没在投影里时再退出被拒（“{notUp.Text}”）");

                // 信号在真实机器里：接入投影被拒；接入投影后信号去了别的机器 → 投影回到本地配置。
                s.SignalCore.UplinkMachineLogicId = 999;
                RangeOpResult inMachine = TestRangeService.Uplink(s, range.BuildingId, p.Serial);
                s.SignalCore.UplinkMachineLogicId = 0;
                TestRangeService.Uplink(s, range.BuildingId, p.Serial);
                s.SignalCore.UplinkMachineLogicId = 999;
                WorldSimulation.StepMany(2);
                bool moved = !TestRangeService.TryGetUplinkedProjection(out _, out _, out _) && TestRangeService.LastFeedback.Contains("别的机器");
                s.SignalCore.UplinkMachineLogicId = 0;
                Expect(!inMachine.Success && inMachine.Failure == RangeFailure.SignalInMachine && inMachine.Text.Contains("先退出接入") && moved,
                    $"F4 信号在机器里时接入投影被拒并写明办法（“{inMachine.Text}”）；接入投影后信号去了别的机器 → 投影自动回到本地配置（信号只在一处）");

                // 第二个投影接入：信号从第一个移过来。
                RangeOpResult p2 = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
                TestRangeService.Uplink(s, range.BuildingId, p.Serial);
                RangeOpResult up2 = TestRangeService.Uplink(s, range.BuildingId, p2.Serial);
                List<RangeProjectionView> views = TestRangeService.ProjectionsOf(range.BuildingId);
                Expect(up2.Success && views.Count(x => x.Uplinked) == 1 && views.First(x => x.Uplinked).Serial == p2.Serial,
                    "F5 接入另一个投影：信号从前一个投影移过来，同一时刻只有一个投影被接入");
                TestRangeService.EndTest(s, range.BuildingId);
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        private static void CheckSilentNight()
        {
            CampaignState s = NewWorld(9306, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            RangeOpResult a = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            RangeOpResult b = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpCannon);
            TestRangeService.Uplink(s, range.BuildingId, a.Serial);
            Seconds(2f);
            // 静默夜开始的正式入口（FG07 调 SignalLinkService.OnSilentNightStarted）：信号在投影里 → 断链、弹回核心、投影消失。
            bool broke = SignalLinkService.OnSilentNightStarted();
            List<RangeProjectionView> views = TestRangeService.ProjectionsOf(range.BuildingId);
            bool gone = broke && views.Count == 1 && views[0].Serial == b.Serial && !TestRangeService.TryGetUplinkedProjection(out _, out _, out _)
                        && TestRangeService.LastFeedback.Contains("静默夜") && TestRangeService.ActiveSessions == 1;
            // 静默夜期间：接入被拒；判定入口（SilentNightProvider）开着时再接入的投影在下一步同样断链。
            SignalUplinkService.SilentNightProvider = () => true;
            RangeOpResult rejected = TestRangeService.Uplink(s, range.BuildingId, b.Serial);
            SignalUplinkService.SilentNightProvider = null;
            TestRangeService.Uplink(s, range.BuildingId, b.Serial);
            SignalUplinkService.SilentNightProvider = () => true;
            WorldSimulation.StepMany(2);
            SignalUplinkService.SilentNightProvider = null;
            RangeResultRecord last = s.Research.Range.History.LastOrDefault();
            bool ended = TestRangeService.ActiveSessions == 0 && last != null && last.EndReason == (int)RangeEndReason.SilentNight && last.Uplinked;
            Expect(gone && !rejected.Success && rejected.Failure == RangeFailure.SilentNight && ended,
                $"G 负向“投影中静默夜开始”：信号在投影里时静默夜开始 → 断链、弹回核心、那个投影消失（另一个照常，{gone}）；静默夜期间接入被拒（“{rejected.Text}”）；"
                + $"最后一个投影因此消失 → 测试结束，记录写“{(last != null ? TestRangeService.EndReasonText(last.EndReason) : "无")}”");
        }

        // ── G 上限与负向 ─────────────────────────────────────────────────────────

        private static void CheckCapAndNegative()
        {
            CampaignState s = NewWorld(9307, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            BuildingRecord range2 = PlaceRange(s, "range2");
            FgProductionSelfCheck.Resync(s);
            WorldSimulation.StepMany(2);
            int ok = 0;
            for (int i = 0; i < 4; i++)
            {
                ok += TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun).Success ? 1 : 0;
            }
            for (int i = 0; i < 2 && range2 != null; i++)
            {
                ok += TestRangeService.ProjectBlueprint(s, range2.BuildingId, BpGun).Success ? 1 : 0;
            }
            RangeOpResult seventh = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            Expect(range2 != null && ok == 6 && !seventh.Success && seventh.Failure == RangeFailure.ProjectionCap && seventh.Text.Contains("6") && TestRangeService.ProjectionCount == 6
                   && TestRangeService.ActiveSessions == 2,
                $"G1 负向“投影同时存在上限”：两座靶场合计投影 6 个后第 7 个被拒（“{seventh.Text}”），已有的不受影响");
            int serial = TestRangeService.ProjectionsOf(range.BuildingId)[0].Serial;
            RangeOpResult rm = TestRangeService.RemoveProjection(s, range.BuildingId, serial);
            RangeOpResult again = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpCannon);
            RangeOpResult layoutRunning = TestRangeService.SetSlot(s, range.BuildingId, 5, "range.basic");
            RangeOpResult missing = TestRangeService.ProjectBlueprint(s, range.BuildingId, "bp_not_exist");
            RangeOpResult none = TestRangeService.ProjectBlueprint(s, range.BuildingId, null);
            Expect(rm.Success && again.Success && !layoutRunning.Success && layoutRunning.Failure == RangeFailure.RunningLayout && !missing.Success
                   && missing.Failure == RangeFailure.BlueprintMissing && !none.Success && none.Failure == RangeFailure.NoBlueprint,
                $"G2 移除一个后又能投影；测试进行中改靶子布置被拒（“{layoutRunning.Text}”）；不存在的蓝图 / 没选蓝图被拒（“{missing.Text}” / “{none.Text}”）");

            // 靶场停用 / 被毁 / 拆除：进行中的测试结束，读数记下。
            int h0 = s.Research.Range.History.Length;
            range.ConstructionState = BuildingConstructionState.Disabled;
            WorldSimulation.StepMany(2);
            bool lost1 = !TestRangeService.IsRunning(range.BuildingId) && s.Research.Range.History.Last().EndReason == (int)RangeEndReason.RangeLost;
            range.ConstructionState = BuildingConstructionState.Operational;
            s.BuildingRecords = s.BuildingRecords.Where(b => b.BuildingId != range2.BuildingId).ToArray();
            HomeGridService.Invalidate();
            WorldSimulation.StepMany(2);
            bool lost2 = !TestRangeService.IsRunning(range2.BuildingId) && s.Research.Range.History.Length == h0 + 2 && TestRangeService.ProjectionCount == 0;
            // 最后一个投影移除 → 测试结束。
            RangeOpResult solo = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            TestRangeService.RemoveProjection(s, range.BuildingId, solo.Serial);
            bool emptied = !TestRangeService.IsRunning(range.BuildingId) && s.Research.Range.History.Last().EndReason == (int)RangeEndReason.Emptied;
            RangeOpResult endIdle = TestRangeService.EndTest(s, range.BuildingId);
            Expect(lost1 && lost2 && emptied && !endIdle.Success && endIdle.Failure == RangeFailure.NotRunning,
                $"G3 靶场被禁用 / 被拆除时测试结束并记下读数（{lost1} / {lost2}）；最后一个投影移除 → 测试结束（{emptied}）；没在测试时“结束测试”被拒（“{endIdle.Text}”）");

            // 时间到：到最长时长自动结束。
            RangeOpResult longRun = TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            Seconds(TestRangeCatalog.MaxTestSeconds + 1f);
            RangeResultRecord tr = s.Research.Range.History.Last();
            Expect(longRun.Success && !TestRangeService.IsRunning(range.BuildingId) && tr.EndReason == (int)RangeEndReason.TimeUp
                   && Mathf.Abs(tr.Seconds - TestRangeCatalog.MaxTestSeconds) < 0.1f && tr.HeatCurve.Length <= TestRangeCatalog.CurvePoints
                   && s.Research.Range.History.Length <= TestRangeCatalog.HistoryKeep,
                $"G4 一次测试最长 {TestRangeCatalog.MaxTestSeconds:F0} 游戏秒，到点自动结束（记录 {tr.Seconds:F1} 秒，热量曲线抽样到 {tr.HeatCurve.Length} 点）；测试记录不超过 {TestRangeCatalog.HistoryKeep} 条");
        }

        // ── H 靶子 ─────────────────────────────────────────────────────────────

        private static void CheckTargets()
        {
            CampaignState s = NewWorld(9308, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            RangeTargetDef heavy = TestRangeCatalog.Targets.First(d => d.Archetype == RangeTargetArchetype.Heavy);
            RangeTargetDef shield = TestRangeCatalog.Targets.First(d => d.Archetype == RangeTargetArchetype.Shield);
            RangeTargetDef swarm = TestRangeCatalog.Targets.First(d => d.Archetype == RangeTargetArchetype.Swarm);
            RangeOpResult locked = TestRangeService.SetSlot(s, range.BuildingId, 0, heavy.Id);
            bool lockedOk = !locked.Success && locked.Failure == RangeFailure.TargetLocked && locked.Text.Contains("击败") && !TestRangeService.IsUnlocked(s, heavy);
            int n0 = NotificationCenter.History.Count(e => e.Type.Id == "range_target_unlocked");
            // 正式入口：区域敌人阵亡结算（铸造前哨的护甲机被击毁）。
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            var bot = new RegionEnemyRecord
            {
                EnemyInstanceId = "range_test_armorbot", RegionId = FoundryOutpostLayout.RegionId, EnemyTypeId = EnemyCatalog.ArmorBotId,
                Position = Vector2.zero, Health = 0f, MaxHealth = 160f, IsAlive = false,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(bot).ToArray();
            FoundryOutpostRegion.OnEnemyKilled(s, bot);
            bool unlocked = TestRangeService.IsUnlocked(s, heavy) && !TestRangeService.IsUnlocked(s, shield)
                            && NotificationCenter.History.Count(e => e.Type.Id == "range_target_unlocked") == n0 + 1
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstTargetUnlocked) && TestRangeService.IsNewTarget(s, heavy);
            TestRangeService.MarkTargetsSeen(s);
            Expect(lockedOk && unlocked && !TestRangeService.IsNewTarget(s, heavy),
                $"H1 FGR-RND-030：没击败过对应敌人时放重甲靶被拒并写明怎么解锁（“{locked.Text}”）；铸造前哨的护甲机被击毁（区域阵亡结算的正式入口）→ 解锁“{heavy.Name}”、发通知与引导钩子、"
                + "面板标“新”直到看过一次；护盾靶仍锁着");

            // 旧档补记：区域记录里已经击毁的敌人（含主核心 boss_core → enemy_core_boss）读档时补成“击败过”。
            CampaignState old = FgProductionSelfCheck.NewWorld(9318, true, 300);
            old.RegionEnemies = (old.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Append(new RegionEnemyRecord { EnemyInstanceId = "x1", EnemyTypeId = FoundryOutpostLayout.BossCoreTypeId, IsAlive = false })
                .Append(new RegionEnemyRecord { EnemyInstanceId = "x2", EnemyTypeId = EnemyCatalog.ScoutId, IsAlive = true }).ToArray();
            old.Research.Range = null;
            TestRangeService.EnsureState(old);
            bool backfill = old.Research.Range.DefeatedEnemyTypes.SequenceEqual(new[] { EnemyCatalog.CoreBossId })
                            && TestRangeService.IsUnlocked(old, shield) && !TestRangeService.IsUnlocked(old, TestRangeCatalog.Targets.First(d => d.Archetype == RangeTargetArchetype.Fast));
            Expect(backfill, $"H2 旧档迁移：区域记录里已击毁的主核心补记为击败过 → 护盾靶解锁；还活着的侦察机不算（{string.Join(",", old.Research.Range.DefeatedEnemyTypes)}）");

            // 五种原型的行为（同一台连射器投影，各打 25 游戏秒，比每发伤害）。
            CampaignState w = NewWorld(9328, out range);
            foreach (string e in new[] { EnemyCatalog.ArmorBotId, EnemyCatalog.ScoutId, EnemyCatalog.JammerId, EnemyCatalog.RepairBotId })
            {
                TestRangeService.NoteEnemyDefeated(w, e);
            }
            float PerShot(string target)
            {
                SetLayout(w, range.BuildingId, target);
                TestRangeService.ProjectBlueprint(w, range.BuildingId, BpGun);
                Seconds(25f);
                RangeResultRecord r = TestRangeService.LiveReadings(range.BuildingId);
                TestRangeService.EndTest(w, range.BuildingId);
                return r != null && r.Shots > 0 ? r.Damage / r.Shots : 0f;
            }
            float basic = PerShot("range.basic");
            float hv = PerShot(heavy.Id);
            float sh = PerShot(shield.Id);
            SetLayout(w, range.BuildingId, swarm.Id);
            TestRangeService.ProjectBlueprint(w, range.BuildingId, BpGun);
            int swarmUnits = TestRangeService.SiteOf(range.BuildingId).Kernel.CountAlive(CombatFaction.Hostile);
            TestRangeService.EndTest(w, range.BuildingId);
            Expect(basic > 0f && hv < basic * 0.75f && sh < basic * 0.75f && swarmUnits == swarm.Count,
                $"H3 靶子原型：同一台连射器每发伤害 标准 {basic:F1} / 重甲正面 {hv:F1} / 护盾 {sh:F1}（重甲与护盾都明显减伤）；集群靶一个靶位放 {swarmUnits} 个（表 {swarm.Count}）");
        }

        // ── I 读数、对比与反应日志 ───────────────────────────────────────────────

        private static void CheckReadingsAndLog()
        {
            CampaignState s = NewWorld(9309, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            NamedReactionCatalog.OpenBatch(s, "act1");
            SetLayout(s, range.BuildingId, "range.basic", "range.basic", "range.basic", "range.basic");
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpWet);
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpShock);
            int log0 = ReactionLog.All.Count;
            Seconds(30f);
            RangeResultRecord live = TestRangeService.LiveReadings(range.BuildingId);
            List<RangeProjectionView> views = TestRangeService.ProjectionsOf(range.BuildingId);
            float expectEnergy = 0f;
            foreach (string bp in new[] { BpWet, BpShock })
            {
                BlueprintVersionRecord v = TestRangeService.LatestVersion(BlueprintEditorService.Find(s, bp));
                BlueprintCircuitPreview p = BlueprintCircuitCompiler.CompilePreview(BlueprintCircuitBoard.FromVersion(v), s.RandomSeed);
                expectEnergy += Math.Max(0, p.PowerCost) * views.First(x => x.Label == bp).Shots;
            }
            bool readings = live.Dps > 0f && live.Reactions.Length > 0 && live.Coverage.Length > 0 && live.Coverage.All(c => c.Value > 0 && c.Value <= 1000)
                            && live.HeatCurve.Length >= 50 && Mathf.Abs(live.Energy - expectEnergy) < 0.01f && live.Seconds > 29f;
            Expect(readings,
                $"I1 FGR-RND-032 读数：冷却液 + 电弧链两台投影打 30 秒——每秒伤害 {live.Dps:F1}、反应 {TestRangeService.ReactionName(live.Reactions.FirstOrDefault()?.Id)} ×{live.Reactions.FirstOrDefault()?.Value}、"
                + $"标签覆盖 {string.Join("，", live.Coverage.Select(c => TestRangeService.TagName(c.Id) + " " + c.Value / 10f + "%"))}、热量曲线 {live.HeatCurve.Length} 点、能耗 {live.Energy:F1}（= 每发耗电 × 发数 {expectEnergy:F1}）");

            var mine = ReactionLog.All.Skip(log0).Where(e => TestRangeService.IsRangeSite(e.SiteId)).ToList();
            string line = mine.Count > 0 ? ReactionLog.Describe(s, mine[0]) : string.Empty;
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool dummyLabel = home.TryGetEnemyUnit(HomeValleyCombatTargets.LowThreatTargetId, out int dummy) && home.TryGetUnitLabel(dummy, out string dk, out _, out _)
                              && dk == HomeValleyCombatTargets.TrainingTargetLabelKey
                              && ReactionLog.PartyLabel(new ReactionLogParty { Known = true, Kind = CombatUnitKind.Enemy, LabelKey = dk }) == "训练靶";
            bool siteName = CombatSites.SiteName(TestRangeService.SitePrefix + range.BuildingId) == "靶场";
            bool noStats = (s.Stats?.ReactionSessions ?? Array.Empty<ReactionSessionRecord>()).All(r => !TestRangeService.IsRangeSite(r.SiteId));
            string generic = GameText.Get("reaction.log.enemy");
            string unknown = GameText.Get("reaction.log.unknown");
            List<string> bad = mine.Select(e => ReactionLog.Describe(s, e)).Where(d => d.Contains(generic) || d.Contains(unknown)).ToList();
            Expect(mine.Count > 0 && bad.Count == 0 && line.Contains("投影靶·") && line.Contains("投影·") && dummyLabel && siteName && noStats,
                $"I2 FG-GAP-061：靶场里的反应进反应日志，全部 {mine.Count} 条都没有“{generic}”“{unknown}”"
                + (bad.Count > 0 ? $"（{bad.Count} 条有，例“{bad[0]}”）" : string.Empty)
                + $"，目标写“投影靶·…”、触发者写“投影·蓝图名”（“{line}”）；家园训练靶写“训练靶”（{dummyLabel}）；反应图鉴首次触发的地点写“靶场”；仿真不进伤害归因统计（{noStats}）");

            // I2b 文本键：投影靶的名字参数存文本键，切到英文后同一条旧日志跟着换（不会中英混排）。
            ReactionLogEntry sample = mine.FirstOrDefault(e => e.Target.LabelKey == "reaction.log.range_target");
            string zhTarget = sample != null ? ReactionLog.PartyLabel(sample.Target) : string.Empty;
            GameSettings.SetLanguage(GameLanguage.En);
            string enTarget = sample != null ? ReactionLog.PartyLabel(sample.Target) : string.Empty;
            string enExpect = sample != null ? GameText.Format("reaction.log.range_target", GameText.Get(sample.Target.LabelArg)) : "?";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hasCjk = enTarget.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            Expect(sample != null && sample.Target.LabelArgIsKey && enTarget == enExpect && !hasCjk && zhTarget != enTarget,
                $"I2b 投影靶日志名参数存文本键（{sample?.Target.LabelArg}）：中文“{zhTarget}”，切英文后同一条日志“{enTarget}”（没有中文残留）");

            TestRangeService.EndTest(s, range.BuildingId);
            SetLayout(s, range.BuildingId, "range.basic");
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpGun);
            Seconds(10f);
            TestRangeService.EndTest(s, range.BuildingId);
            bool pair = TestRangeService.ComparePair(s, out RangeResultRecord a, out RangeResultRecord b);
            string cmp = pair ? TestRangePanelUIToolkit.CompareLines(a, b) : string.Empty;
            RangeOpResult pick = TestRangeService.SetCompare(s, false, a.Serial);
            TestRangeService.ComparePair(s, out RangeResultRecord a2, out RangeResultRecord b2);
            Expect(pair && b.Serial > a.Serial && cmp.Contains("每秒伤害") && cmp.Contains(TestRangeService.Num(a.Dps)) && cmp.Contains(TestRangeService.Num(b.Dps))
                   && pick.Success && b2.Serial == a.Serial && a2.Serial != a.Serial && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstCompare),
                $"I3 两次测试并排对比：默认最近两条，逐项写“A ｜ B（差值）”；可以手选（选了同一条时另一栏自动换开）\n{cmp}");
        }

        /// <summary>I4 FG-GAP-061：反应打出致命一击的那一步，日志目标仍写“投影靶·…”（名字不在阵亡事件里清掉）。
        /// 靶子血压到很低，让反应频繁收尾；旁听同一地点的阵亡事件，按（单位 ID, 刻）对上日志条目。</summary>
        private static void CheckReactionKillLabel()
        {
            CampaignState s = NewWorld(9319, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            NamedReactionCatalog.OpenBatch(s, "act1");
            SetLayout(s, range.BuildingId, "range.basic", "range.basic", "range.basic", "range.basic");
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpWet);
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpShock);
            CombatSite site = TestRangeService.SiteOf(range.BuildingId);
            if (site == null)
            {
                Fail("I4 测试准备：靶场没有开始测试");
                return;
            }
            var kills = new HashSet<(int, long)>();
            var killedUnits = new HashSet<int>();
            Action<CombatSite, CombatEvent> inner = site.EventObserver;
            site.EventObserver = (cs, e) =>
            {
                inner?.Invoke(cs, e);
                if (e.Kind == CombatEventKind.Killed)
                {
                    kills.Add((e.Unit, GameClock.Ticks));
                    killedUnits.Add(e.Unit);
                }
            };
            int log0 = ReactionLog.All.Count;
            for (int t = 0; t < 240 && !site.IsDisposed; t++)
            {
                CombatKernel k = site.Kernel;
                for (int i = 0; i < k.SlotCount; i++)
                {
                    CombatUnitView v = k.ViewAt(i);
                    if (v.Alive && v.Faction == CombatFaction.Hostile && v.Health > 14f)
                    {
                        k.SetHealth(v.Id, 14f, v.MaxHealth, true); // 普通一发打不死、再挂一次反应就死
                    }
                }
                Seconds(0.25f);
            }
            var mine = ReactionLog.All.Skip(log0).Where(e => TestRangeService.IsRangeSite(e.SiteId)).ToList();
            var finishing = mine.Where(e => e.Target.UnitId > 0 && kills.Contains((e.Target.UnitId, e.Tick))).ToList();
            string generic = GameText.Get("reaction.log.enemy");
            string unknown = GameText.Get("reaction.log.unknown");
            var bad = finishing.Select(e => ReactionLog.Describe(s, e)).Where(d => d.Contains(generic) || d.Contains(unknown) || !d.Contains("投影靶·")).ToList();
            bool labelKept = killedUnits.Count > 0 && killedUnits.All(u => site.TryGetUnitLabel(u, out string key, out _, out _) && key == "reaction.log.range_target");
            Expect(finishing.Count > 0 && bad.Count == 0 && labelKept,
                $"I4 反应收尾击杀：60 游戏秒里 {killedUnits.Count} 次击毁，其中 {finishing.Count} 条反应日志就是打死目标的那一步，目标全部写“投影靶·…”"
                + (bad.Count > 0 ? $"（{bad.Count} 条不是，例“{bad[0]}”）" : string.Empty)
                + (finishing.Count > 0 ? $"，例“{ReactionLog.Describe(s, finishing[0])}”" : string.Empty)
                + $"；阵亡靶子的名字留到测试结束（{labelKept}）");
            TestRangeService.EndTest(s, range.BuildingId);
        }

        /// <summary>J0 存档域：已经不存在的靶场的布置在读档补全时清掉，还在的靶场（含搬迁 / 升级虚影同 ID）保留。</summary>
        private static void CheckLayoutPrune()
        {
            CampaignState s = NewWorld(9321, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            SetLayout(s, range.BuildingId, "range.basic", "range.basic");
            TestRangeState rs = s.Research.Range;
            rs.Layouts = rs.Layouts.Append(new RangeLayoutRecord { BuildingId = "range_gone", Slots = new[] { "range.basic" } }).ToArray();
            TestRangeService.EnsureState(s);
            bool kept = rs.Layouts.Any(l => l.BuildingId == range.BuildingId && l.Slots.Count(x => x == "range.basic") == 2);
            bool pruned = rs.Layouts.All(l => l.BuildingId != "range_gone");
            Expect(kept && pruned,
                $"J0 读档补全：拆掉的靶场的布置被清掉（{pruned}），还在的靶场布置原样保留（{kept}）；共 {rs.Layouts.Length} 条");
        }

        // ── J 预设 ─────────────────────────────────────────────────────────────

        private static void CheckPresets()
        {
            CampaignState s = NewWorld(9310, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.ArmorBotId);
            SetLayout(s, range.BuildingId, "range.heavy", "", "range.basic");
            RangeOpResult save = TestRangeService.SavePreset(s, range.BuildingId, "重甲测试");
            SetLayout(s, range.BuildingId, "range.basic");
            RangePresetRecord p = TestRangeService.Presets(s).Last();
            RangeOpResult apply = TestRangeService.ApplyPreset(s, range.BuildingId, p.PresetId);
            string[] l = TestRangeService.Layout(s, range.BuildingId);
            bool applied = save.Success && apply.Success && l[0] == "range.heavy" && l[1] == string.Empty && l[2] == "range.basic" && p.Name == "重甲测试";
            // 预设里有锁着的靶子（例如别的存档里存的）：应用时留空位，不偷偷换成别的。
            p.Slots[3] = "range.shield";
            TestRangeService.ApplyPreset(s, range.BuildingId, p.PresetId);
            bool lockedSkipped = TestRangeService.Layout(s, range.BuildingId)[3] == string.Empty;
            int cap = TestRangeCatalog.PresetCap;
            for (int i = TestRangeService.Presets(s).Count; i < cap; i++)
            {
                TestRangeService.SavePreset(s, range.BuildingId, null);
            }
            RangeOpResult full = TestRangeService.SavePreset(s, range.BuildingId, null);
            RangeOpResult del = TestRangeService.DeletePreset(s, p.PresetId);
            RangeOpResult delMissing = TestRangeService.DeletePreset(s, p.PresetId);
            bool autoName = TestRangeService.Presets(s).Any(x => x.Name.StartsWith("预设 ", StringComparison.Ordinal));
            Expect(applied && lockedSkipped && !full.Success && full.Failure == RangeFailure.PresetFull && del.Success && !delMissing.Success && autoName,
                $"J 靶子布置预设：保存（命名 / 留空自动编号）、应用、删除；锁着的靶子应用时留空位；最多 {cap} 个（第 {cap + 1} 个被拒：“{full.Text}”）");
        }

        // ── K 暂停与倍速、L 观察一致 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, out BuildingRecord range, observe);
            pausedHeld = true;
            if (range == null)
            {
                return "no-range";
            }
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.ScoutId);
            SetLayout(s, range.BuildingId, "range.basic", "range.fast", "range.basic", "range.basic");
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpWet);
            TestRangeService.ProjectBlueprint(s, range.BuildingId, BpCannon);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Json(TestRangeService.LiveReadings(range.BuildingId)) + TestRangeService.SiteOf(range.BuildingId).Kernel.StateHash();
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Json(TestRangeService.LiveReadings(range.BuildingId)) + TestRangeService.SiteOf(range.BuildingId).Kernel.StateHash() == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 300)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            RangeResultRecord r = TestRangeService.LiveReadings(range.BuildingId);
            ulong hash = TestRangeService.SiteOf(range.BuildingId).Kernel.StateHash();
            TestRangeService.EndTest(s, range.BuildingId);
            return Json(r) + "|" + hash.ToString("X16");
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9311, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("\"Damage\":") && !reference.Contains("\"Damage\":0.0,"),
                "K 暂停中（120 帧）投影与读数都不动；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒，读数与靶场内核状态哈希逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9312, true, 1f, false, out _);
            string unseen = RunScenario(9312, false, 1f, false, out _);
            Expect(seen == unseen && !seen.StartsWith("no-range", StringComparison.Ordinal),
                "L 同一次测试在观察与不观察家园时跑 30 游戏秒，读数与靶场内核状态哈希逐字段一致（FGR-BASE-021：远征时靶场照常跑，画面只在观察时画）"
                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── M 快捷入口 ─────────────────────────────────────────────────────────

        private static void CheckShortcuts()
        {
            CampaignState s = NewWorld(9313, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            RangeOpResult bp = TestRangeService.SendBlueprint(s, BpGun);
            string regular = "fw_coolant";
            RangeOpResult fw = TestRangeService.SendFirmware(s, regular);
            RangeOpResult unknown = TestRangeService.SendFirmware(s, "fw_not_exist");
            string raw = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.IsRaw(s, id));
            RangeOpResult rawR = raw != null ? TestRangeService.SendFirmware(s, raw) : default;
            List<RangeProjectionView> views = TestRangeService.ProjectionsOf(range.BuildingId);
            bool rigHasFw = TestRangeService.SiteOf(range.BuildingId).TryGetUnitLabel(views.Last().UnitId, out _, out string rigLabel, out string[] rigFw) && rigFw.Contains(regular);
            Expect(bp.Success && bp.BuildingId == range.BuildingId && fw.Success && views.Count == 2 && rigHasFw && rigLabel.Contains("固件试验台")
                   && !unknown.Success && unknown.Failure == RangeFailure.FirmwareUnknown && raw != null && !rawR.Success && rawR.Failure == RangeFailure.FirmwareRaw
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstSend),
                $"M1 FGR-RND-033：蓝图编辑器“送到靶场测试”投影已保存的那一版（{bp.Text}）；固件详情送来的是带这枚固件的“固件试验台”（{rigLabel}）；不存在的 / 未破解的固件被拒（“{rawR.Text}”）");

            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                RangeOpResult core = TestRangeService.SendFirmware(s, FirmwareCatalog.FwOverloadId);
                RangeProjectionView rig = TestRangeService.ProjectionsOf(range.BuildingId).Last();
                RangeOpResult rigUp = TestRangeService.Uplink(s, range.BuildingId, rig.Serial);
                Expect(core.Success && rig.CoreRig && !rigUp.Success && rigUp.Failure == RangeFailure.RigUplinked,
                    $"M2 核心固件放不进机器电路：送来的试验台按接入态编译（模拟信号核只有这一枚），不需要再接入（“{rigUp.Text}”）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
            TestRangeService.EndTest(s, range.BuildingId);

            // 没有能用的靶场：写明原因。
            range.ConstructionState = BuildingConstructionState.Disabled;
            RangeOpResult noRange = TestRangeService.SendBlueprint(s, BpGun);
            range.ConstructionState = BuildingConstructionState.Operational;
            s.BuildingRecords = s.BuildingRecords.Where(b => b.BuildingId != range.BuildingId).ToArray();
            HomeGridService.Invalidate();
            RangeOpResult none = TestRangeService.SendBlueprint(s, BpGun);
            Expect(!noRange.Success && noRange.Failure == RangeFailure.NotWorking && !none.Success && none.Failure == RangeFailure.NoRange && none.Text.Contains("建造菜单"),
                $"M3 送到靶场时靶场不能用 / 家园里没有靶场：拒绝并写明原因与办法（“{noRange.Text}” / “{none.Text}”）");
        }

        // ── N 面板 ─────────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9314, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.ArmorBotId);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "TestRangePanel.uxml", out GameObject go);
            TestRangePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                TestRangePanelUIToolkit panel = go.AddComponent<TestRangePanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplatesForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "TestRangeProjRow.uxml"),
                    AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "TestRangeSlotRow.uxml"));
                TestRangePanelUIToolkit.Open(range.BuildingId);
                panel.Refresh(force: true);
                string openNote = $"可见 {panel.PanelVisible}、标题“{panel.TitleText}”、靶位 {panel.SlotRowCount}、靶位 1 “{panel.SlotValue(0)}”、空状态“{panel.ProjectionsEmptyText}”、"
                                  + $"未解锁“{panel.LockedText}”、新 {panel.SlotChoices(0).Any(c => c.Contains("（新）"))}、蓝图 {panel.BlueprintChoices.Count}、钩子 {GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstOpen)}";
                bool opened = panel.PanelVisible && panel.TitleText.Contains("靶场") && panel.SlotRowCount == 8 && panel.SlotValue(0) == "标准靶"
                              && panel.ProjectionsEmptyText.Contains("投影") && panel.LockedText.Contains("击败") && panel.SlotChoices(0).Any(c => c.Contains("（新）"))
                              && panel.BlueprintChoices.Contains(BpGun) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RangeFirstOpen) && panel.FooterText.Length > 10
                              && !GameText.ContainsMarker(panel.FooterText);
                // 靶位下拉选中即生效：放一个重甲靶。
                int heavyIndex = panel.SlotChoices(4).ToList().FindIndex(c => c.Contains("重甲"));
                TestRangePanelUIToolkit.PickForTests(panel.SlotField(4), heavyIndex);
                bool slotSet = TestRangeService.Layout(s, range.BuildingId)[4] == "range.heavy";
                // 选蓝图 → 投影：测试开始，投影行出现，靶位下拉锁定。
                int gun = panel.BlueprintChoices.ToList().IndexOf(BpGun);
                TestRangePanelUIToolkit.PickForTests(panel.BlueprintField, gun);
                panel.ClickProject();
                Seconds(6f);
                panel.Refresh(force: true);
                bool running = panel.ProjectionRowCount == 1 && panel.ProjectionRowText(0).Contains(BpGun) && !panel.SlotEnabled(0)
                               && panel.ReadingsText.Contains("每秒伤害") && panel.RunStateText.Contains("测试中") && panel.MessageText.Contains("已投影");
                // 测试中改靶位：被拒并写明原因（面板消息行）。
                panel.ChooseSlot(1, 0);
                bool runningRejected = panel.MessageText.Contains("测试进行中");
                // 接入 / 退出接入按钮。
                panel.ClickUplink(0);
                panel.Refresh(force: true);
                bool upRow = panel.ProjectionRowText(0).Contains("接入态") && panel.ProjectionUplinkButton(0).text == "退出接入";
                panel.ClickUplink(0);
                panel.ClickEnd();
                panel.Refresh(force: true);
                bool ended = panel.ProjectionRowCount == 0 && panel.RunStateText.Contains("没有在测试") && panel.ReadingsText.Contains("每秒伤害") && panel.CompareText.Contains("不足两条");
                // 再测一次 → 对比栏出现两条。
                panel.ClickProject();
                Seconds(4f);
                panel.ClickEnd();
                panel.Refresh(force: true);
                bool compare = panel.CompareChoices.Count == 2 && panel.CompareText.Contains("每秒伤害") && panel.CompareText.Contains("｜");
                // 预设：保存 → 下拉里有 → 应用。
                panel.SetPresetName("面板预设");
                panel.ClickSavePreset();
                panel.Refresh(force: true);
                bool preset = panel.PresetChoices.Contains("面板预设");
                Expect(opened && slotSet && running && runningRejected && upRow && ended && compare && preset,
                    $"N1 面板（真 UXML）：打开时 8 个靶位（默认标准靶）、蓝图下拉、空状态与未解锁说明、新解锁标“新”（{opened}{(opened ? string.Empty : "：" + openNote)}）；靶位下拉选中即生效（{slotSet}）；"
                    + $"选蓝图按“投影”→ 投影行与实时读数（{running}）；测试中改靶位被拒（{runningRejected}）；接入 / 退出接入（{upRow}）；结束测试（{ended}）；两条后对比（{compare}）；预设（{preset}）");
                TestRangePanelUIToolkit.Close();

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "TestRangePanel.uxml", "TestRangeWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("TestRangeRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "N2 靶场面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));

                // 建筑面板“靶场…”按钮只在靶场上出现。
                VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
                try
                {
                    ProductionPanelUIToolkit.InWorldOverrideForTests = true;
                    ProductionPanelUIToolkit prod = pgo.AddComponent<ProductionPanelUIToolkit>();
                    prod.BindView(proot);
                    ProductionPanelUIToolkit.Open(range.BuildingId);
                    prod.Refresh();
                    bool onRange = !prod.RangeButton.ClassListContains("bn-hidden") && prod.RangeButton.text == "靶场…";
                    ProductionPanelUIToolkit.Open(s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse).BuildingId);
                    prod.Refresh();
                    bool hiddenElsewhere = prod.RangeButton.ClassListContains("bn-hidden");
                    ProductionPanelUIToolkit.Open(range.BuildingId);
                    prod.Refresh();
                    prod.OpenRange();
                    Expect(onRange && hiddenElsewhere && TestRangePanelUIToolkit.BuildingId == range.BuildingId,
                        $"N3 靶场的建筑面板有“靶场…”按钮（别的建筑没有），点它打开这座靶场的面板（{onRange} / {hiddenElsewhere}）");
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
                TestRangePanelUIToolkit.Close();
                TestRangePanelUIToolkit.InWorldOverrideForTests = false;
                InputRouter.Reset();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
        }

        // ── O 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9315, out BuildingRecord range);
            if (range == null)
            {
                return;
            }
            TestRangeService.NoteEnemyDefeated(s, EnemyCatalog.JammerId);
            string sw = "range.swarm";
            SetLayout(s, range.BuildingId, sw, sw, sw, sw, sw, sw, sw, sw);
            for (int i = 0; i < 6; i++)
            {
                TestRangeService.ProjectBlueprint(s, range.BuildingId, i % 2 == 0 ? BpWet : BpCannon);
            }
            Seconds(3f);
            int units = TestRangeService.SiteOf(range.BuildingId).Kernel.CountAlive(CombatFaction.Hostile) + TestRangeService.ProjectionCount;
            var watch = new Stopwatch();
            int steps = GameClock.StepHz * 20;
            long t0 = 0;
            for (int i = 0; i < steps; i++)
            {
                watch.Restart();
                TestRangeService.WorldStep(s, GameClock.StepSeconds);
                watch.Stop();
                t0 += watch.ElapsedTicks;
                GameClock.CommitStep(s);
            }
            double perStep = t0 * 1000.0 / Stopwatch.Frequency / steps;
            PerfLines.Add($"满载靶场（6 个投影 + 8 个集群靶位 = {units} 个单位，含标签反应）每个世界步 {perStep:F3} ms（Editor batchmode；内核 Burst，会话与读数在热更层）");
            PerfGate.Expect(units >= 40,
                $"O 性能：满载靶场（{units} 个单位）每个世界步 {perStep:F3} ms（阈值 0.5 ms；逐单位在 AOT 内核里，热更层 O(投影数 ≤ 6 + 到期复位)）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perStep, 0.5, "靶场每步 ms") }, Expect, Line);
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
