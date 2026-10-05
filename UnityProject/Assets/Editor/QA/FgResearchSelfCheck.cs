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
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
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
using GameLogic.UI.Common;
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
    /// FG5-RND-01 研究点、仿真实验室与研发树的自动验收（FG05 FGR-RND-001 / 002 / 010～013；FG13 FGU-22；FGT-RND-001 / 002；负向“研究中技术数据耗尽”“需要关键材料的节点”）。
    /// 全部起真实系统：真实家园（世界模拟、电网内核、生产建筑）、真实研究服务、真文件存读档、真 UXML 研发树面板。研发树在本段里显式开放（真门槛，不受全量自检的旧段规则影响）。
    /// A 数据（新表与源数据逐字段、11 个分支 / 约 70 个节点、门槛回写到各条目、实验室三级、文本中英、钩子、通知、图鉴）；
    /// B 实验室产出（每分钟技术数据 2 → 研究点 1、多座相加、T2 / T3 转换效率、状态）；C 研究推进与取消（FGT-RND-001：投入、完成、移出保留进度、重新排队接着做）；
    /// D 研发树门槛（FGT-RND-002：未研究被拒并写节点名、研究后可建、建筑升级门槛、前置、关键材料、阵营分支、内容未开放、队列已满、排序）；
    /// E 负向（技术数据耗尽、缺电、禁用、没有实验室、拆除退回、被摧毁停住）；F 效果（工作速度、仓库 / 储能容量、实验室效率、规划助手）；
    /// G 真文件存读档与旧档迁移；H 暂停与 0.5x～3x；I 观察 / 不观察一致；J 面板（真 UXML：快捷键、搜索、筛选、缩放、平移、悬停预览、加入 / 移出、拖动排序、布局探针）；
    /// K 通知 / 离家报告 / 图鉴 / 钩子 / 建造菜单“新”；L 性能。已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgResearchSelfCheck）。
    /// </summary>
    public static class FgResearchSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string Lab = ResearchService.TypeId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 研究点、实验室与研发树")]
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
            Line("\n[研究点、实验室与研发树] 仿真实验室、研究队列、约 70 个节点、研究门槛、研发树面板（FG5-RND-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgresearch-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                     "研究服务与面板在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckLabOutput);
                Step(CheckProgressAndCancel);
                Step(CheckGates);
                Step(CheckKeyMaterialAndBranches);
                Step(CheckNegative);
                Step(CheckEffects);
                Step(CheckSaveLoad);
                Step(CheckMigration);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanel);
                Step(CheckFeedback);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"研发树自检抛异常：{e}");
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
                ResearchGate.ResetForTests();
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
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                ResearchTreePanelUIToolkit.InWorldOverrideForTests = false;
                ResearchTreePanelUIToolkit.Close();
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
            Line($"  · [研究点、实验室与研发树] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 900)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            ResearchGate.TreeAvailableOverrideForTests = () => true; // 本段测的是开放后的真门槛
            _labSeq = 0; // 实验室的建筑 ID 每个世界从头编号：同一场景在不同倍速 / 观察下逐字段可比
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            // 再接两座发电机 2：开局的用电建筑 + 三座实验室（T3 耗电 30）都分得到电（测的是实验室，不是电网分配；电网本身由 FG3-LOG-06 / FG4-ECO-04 覆盖）。
            for (int i = 0; i < 2; i++)
            {
                PlaceConnected(s, HomeValleyLayout.BuildingTypeGenerator2, "rnd_gen" + i);
            }
            HomeValleyPowerGrid.Recompute(s);
            return s;
        }

        private static int _labSeq;

        /// <summary>测试捷径：在接得上电网的空地上登记一座建成的建筑（由近到远按种子地形找，B25；接不上的位置撤掉再找）。</summary>
        private static BuildingRecord PlaceConnected(CampaignState s, string typeId, string key)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (float d = 4f; d <= 30f; d += 1f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (!HomeGridService.ValidatePlacement(s, typeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        continue;
                    }
                    BuildingRecord b = FgProductionSelfCheck.Built(s, typeId, key, c);
                    if (HomeValleyPowerGrid.IsConnected(s, b.BuildingId))
                    {
                        return b;
                    }
                    s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                    HomeGridService.MapFor(s);
                    HomeValleyPowerGrid.Recompute(s);
                }
            }
            return null;
        }

        private static BuildingRecord AddLab(CampaignState s, int tier = 1)
        {
            BuildingRecord b = PlaceConnected(s, Lab, "lab" + (_labSeq++).ToString(CultureInfo.InvariantCulture));
            if (b == null)
            {
                Fail("测试准备：核心附近没有接得上电网、能放仿真实验室的空地");
                return null;
            }
            b.Tier = tier;
            HomeValleyPowerGrid.Recompute(s);
            return b;
        }

        /// <summary>测试捷径：确保有一座运转中的仓库（开局布局里的仓库可能要先修；施工 / 维修由 FG3-LOG-02 / FG4-ECO-05 覆盖）。</summary>
        private static BuildingRecord EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && b.ConstructionState == BuildingConstructionState.Operational
                                                                      && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                return wh;
            }
            BuildingRecord any = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (any != null)
            {
                any.ConstructionState = BuildingConstructionState.Operational;
                any.Health = BuildingOps.MaxDurability(any.BuildingTypeId);
                HomeValleyPowerGrid.Recompute(s);
                return any;
            }
            GridCell? c = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
            return c.HasValue ? FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "rnd_wh", c.Value) : null;
        }

        private static void Give(CampaignState s, string itemId, int n) => HomeInventory.Add(s, itemId, n, clampToSpace: false);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static long Milli(CampaignState s) => s.Research.PointsProduced * 1000L + s.Research.PointsMilli;

        private static ResearchNodeDef N(string id) => ResearchCatalog.Find(id);

        private static List<BuildingRecord> Generators(CampaignState s) =>
            s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                                         && HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId) && b.ConstructionState == BuildingConstructionState.Operational).ToList();

        private static string Snapshot(CampaignState s)
        {
            ResearchState r = s.Research;
            var sb = new StringBuilder();
            sb.Append("pts=").Append(r.Points).Append('+').Append(r.PointsMilli)
                .Append(" tech=").Append(s.TechData).Append(" used=").Append(r.TechConsumed).Append(" made=").Append(r.PointsProduced).Append(" inv=").Append(r.PointsInvested)
                .Append(" done=").Append(string.Join(",", r.CompletedNodes))
                .Append(" q=").Append(string.Join(",", r.Queue))
                .Append(" prog=").Append(string.Join(",", r.Progress.Select(p => p.NodeId + ":" + p.Invested)))
                .Append(" labs=").Append(string.Join(",", r.Labs.Select(l => l.BuildingId + ":" + l.ProgressTicks + (l.Loaded ? "L" : "-"))))
                .Append(" new=").Append(string.Join(",", r.NewEntries))
                .Append(" stall=").Append(r.StallNotified)
                .Append(" ver=").Append(r.DomainVersion);
            return sb.ToString();
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] rbSrc = lines.Where(l => l.StartsWith("RB\t", StringComparison.Ordinal)).ToArray();
            string[] rnSrc = lines.Where(l => l.StartsWith("RN\t", StringComparison.Ordinal)).ToArray();
            string[] rb = ConfigSystem.Instance.Tables.TbResearchBranch.DataList
                .Select(r => string.Join("\t", "RB", r.Id, r.NameKey, r.Kind, r.Faction, r.Color, r.Glyph, r.SortOrder.ToString(CultureInfo.InvariantCulture))).ToArray();
            string[] rn = ConfigSystem.Instance.Tables.TbResearchNode.DataList
                .Select(r => string.Join("\t", "RN", r.Id, r.Branch, r.NameKey, r.DescKey, r.Kind, r.Cost.ToString(CultureInfo.InvariantCulture), r.Prereqs, r.KeyItems, r.Unlocks,
                    r.EffectKind, r.EffectTarget, Py(r.EffectValue), r.Col.ToString(CultureInfo.InvariantCulture), r.Row.ToString(CultureInfo.InvariantCulture), r.OpensIn)).ToArray();
            Expect(code == 0 && rbSrc.Length == 11 && rbSrc.SequenceEqual(rb) && rnSrc.Length >= 60 && rnSrc.SequenceEqual(rn),
                $"A1 新表 fg.TbResearchBranch（{rb.Length} 行）/ fg.TbResearchNode（{rn.Length} 行）与源数据 fgdata_research 逐字段一致" + (code == 0 ? string.Empty : "：" + FgProductionSelfCheck.Tail(output)));

            IReadOnlyList<ResearchBranchDef> branches = ResearchCatalog.Branches;
            int general = branches.Count(b => !b.IsFaction), faction = branches.Count(b => b.IsFaction);
            int nodes = ResearchCatalog.Nodes.Count, ready = ResearchCatalog.CountReady();
            var kinds = new HashSet<string>(ResearchCatalog.Nodes.Select(n => n.Kind));
            bool factions = new[] { "silent", "foundry", "clarity", "overclock" }.All(f => branches.Any(b => b.IsFaction && b.Faction == f));
            Expect(ResearchCatalog.Problems.Count == 0 && general == 7 && faction == 4 && factions && nodes >= 60 && nodes <= 80
                   && kinds.SetEquals(new[] { "building", "upgrade", "efficiency", "capacity", "slot", "blueprint" })
                   && ResearchCatalog.Nodes.All(n => n.Branch.Nodes.Contains(n)),
                $"A2 研发树：通用分支 {general} 个（工业、物流、能源、防御、信号、环境、远征）+ 阵营分支 {faction} 个（静默 / 铸造 / 澄净 / 超频）；{nodes} 个节点（约 70），其中内容已就绪 {ready} 个；" +
                $"六类节点（蓝图 / 建筑 / 升级 / 效率 / 容量 / 槽位）都有；载入校验无问题（{string.Join("；", ResearchCatalog.Problems)}）");

            // 门槛回写：DEBT 点名的条目都要研究，节点名写在锁定说明里。
            (string entry, string node)[] gates =
            {
                ("belt_t2", "logistics.belt_t2"), ("belt_t3", "logistics.belt_t3"), ("splitter", "logistics.splitter"), ("merger", "logistics.splitter"),
                ("underground_t1", "logistics.underground"), ("underground_t2", "logistics.belt_t2"), ("underground_t3", "logistics.belt_t3"),
                ("pipe_t2", "logistics.pipe_t2"), ("pipe_underground_t1", "logistics.pipe_underground"), ("pipe_underground_t2", "logistics.pipe_t2"),
                ("power_pole_t2", "energy.pole_t2"), ("fuel_generator", "energy.fuel_generator"), ("solar_array", "energy.solar"), ("energy_storage", "energy.storage"),
                ("electronics_bench", "industry.electronics"), ("component_workshop", "industry.components"), ("refinery_tower", "industry.refinery_tower"),
                ("override_array", "signal.override_t1"),
            };
            var bad = new List<string>();
            foreach ((string entry, string node) in gates)
            {
                if (!BuildCatalog.TryGet(entry, out BuildEntry e) || e.UnlockRule != "research:" + node || !GameText.Get(e.UnlockHintKey).Contains(N(node)?.Name ?? "?"))
                {
                    bad.Add(entry);
                }
            }
            (string type, int tier, string node)[] tierGates =
            {
                ("warehouse", 2, "logistics.warehouse_t2"), ("warehouse", 3, "logistics.warehouse_t3"), ("signal_tower", 2, "signal.tower_t2"),
                ("override_array", 2, "signal.override_t2"), ("override_array", 3, "signal.override_t3"), (Lab, 2, "industry.lab_t2"), (Lab, 3, "industry.lab_t3"),
            };
            foreach ((string type, int tier, string node) in tierGates)
            {
                if (BuildingOps.TierRow(type, tier)?.UnlockRule != "research:" + node)
                {
                    bad.Add(type + ".t" + tier);
                }
            }
            bool starters = new[] { "belt_t1", "pipe_t1", "pump", "tank", "valve", "power_pole", "recycler", "extraction_drill", "refinery_furnace", "parts_workshop", Lab }
                .All(id => BuildCatalog.TryGet(id, out BuildEntry e) && e.UnlockRule == "always");
            Expect(bad.Count == 0 && starters,
                $"A3 研究门槛回写到 {gates.Length} 个建造菜单条目与 {tierGates.Length} 个建筑等级（承接 DEBT-FG3LOG03-07 / 04-01 / 05-01 / 06-01、DEBT-FG4ECO04-01 / 05-01 / 11-01），锁定说明写节点名；" +
                $"开局必需（T1 传送带 / 管线、泵、储罐、阀门、电塔、回收站、提取钻、精炼炉、零件工坊、仿真实验室）仍开局可造（不符：{string.Join(",", bad)}）");

            BuildingGrid g = GridContent.Building(Lab);
            BuildingTier t1 = BuildingOps.TierRow(Lab, 1), t2 = BuildingOps.TierRow(Lab, 2), t3 = BuildingOps.TierRow(Lab, 3);
            bool lab = g != null && g.Placeable == 1 && g.Category == "research" && g.FootprintW == 3 && g.FootprintH == 3 && t1 != null && t2 != null && t3 != null
                       && Mathf.Approximately(t1.Value, 1f) && Mathf.Approximately(t2.Value, 1.25f) && Mathf.Approximately(t3.Value, 1.5f)
                       && HomeValleyLayout.PowerProfile.TryGetValue(Lab, out (float PowerDemand, int PowerPriority) pp) && pp.PowerDemand > 0f
                       && Math.Abs(ResearchService.TechPerMinute - 2.0) < 1e-6 && Math.Abs(ResearchService.PointsPerMinute - 1.0) < 1e-6 && ResearchService.QueueMax == 5
                       && Math.Abs(ResearchService.EfficiencyCap - 0.3) < 1e-6;
            Expect(lab, "A4 仿真实验室：建造菜单“研发”页、3×3、用电；三级转换效率 1 / 1.25 / 1.5；初值 每分钟技术数据 2 → 研究点 1、队列 5 项、效率上限 +30%（FGR-RND-001 / 011 / 013）");

            string[] textKeys =
            {
                "building.sim_lab.name", "building.sim_lab.desc", "bs.reason.working_lab", "bs.reason.lab_no_tech", "bs.reason.lab_no_power", "research.panel.title",
                "research.panel.search", "research.panel.footer", "research.panel.queue_empty", "research.status.no_lab", "research.status.paused_no_tech", "research.state.available",
                "research.state.locked", "research.state.later", "research.reason.queue_full", "research.reason.prereq", "research.reason.key_missing", "research.reason.closed",
                "research.detail.unlock_building", "research.detail.effect_speed", "research.build.new", "codex.research.tree.body", "research.branch.closed",
                "research.node.logistics.splitter", "research.node.silent.turret.desc", "research.hint.energy.storage", "codex.tab.research",
            };
            bool zh = textKeys.All(k => GameText.Has(k) && !GameText.ContainsMarker(GameText.Get(k)));
            bool nodeTexts = ResearchCatalog.Nodes.All(n => GameText.Has(n.NameKey) && GameText.Has(n.DescKey) && (n.Unlocks.Length == 0 || GameText.Has("research.hint." + n.Id)));
            GameSettings.SetLanguage(GameLanguage.En);
            bool en = textKeys.All(k => !GameText.ContainsMarker(GameText.Get(k)) && GameText.Get(k).Length > 0 && !ContainsCjk(GameText.Get(k)))
                      && ResearchCatalog.Nodes.All(n => !ContainsCjk(n.Name) && !ContainsCjk(n.Desc));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = new[] { GuidanceHooks.ResearchTreeFirstOpen, GuidanceHooks.ResearchLabFirstBuilt, GuidanceHooks.ResearchFirstQueued, GuidanceHooks.ResearchFirstCompleted,
                GuidanceHooks.ResearchFirstStalled }.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType("research_done", out NotifyTypeDef done) && done.Tier == NotifyLevel.Info && done.AwaySection == "research"
                          && NotificationCatalog.TryGetType("research_stalled", out NotifyTypeDef stall) && stall.Tier == NotifyLevel.Warning && stall.AwaySection == "research";
            bool key = InputActionCatalog.TryGet(GameActionId.OpenResearch, out InputActionDef act) && act.Status == InputActionStatus.Wired
                       && GameSettings.KeyBindings.GetKey(GameActionId.OpenResearch) == KeyCode.K;
            Expect(zh && en && nodeTexts && hooks && notify && key,
                $"A5 新文本中英都有（{textKeys.Length} 个界面键 + 每个节点的名字 / 说明 / 锁定说明）；5 个引导钩子；“研究完成”（信息）/“研究暂停”（警告）通知都进离家报告“研究”段；研发树键 K 已接通（{zh}/{en}/{nodeTexts}/{hooks}/{notify}/{key}）");

            // A6（复审 P1，B06 / B11）：缺技术数据时只指向现在真有的来源（解析台）；残骸 / 遗迹终端 / 数据核心 / 黑匣子在 FG5-RND-02 / 06、FG8 才有，图鉴写“后续版本开放”。
            string noTech = GameText.Format("bs.reason.lab_no_tech", 0), paused = GameText.Get("research.status.paused_no_tech"), codex = GameText.Get("codex.research.lab.body");
            Expect(noTech.Contains("解析台") && !noTech.Contains("残骸") && paused.Contains("解析台") && !paused.Contains("残骸") && codex.Contains("后续版本开放"),
                $"A6 缺技术数据的原因只写现有来源：“{noTech}”；图鉴把残骸 / 遗迹终端 / 数据核心 / 黑匣子写成后续版本开放");
            // 记录（不是断言）：新档技术数据供给与“研发树开放前开局可造的内容”的研究成本之差——DEBT-FG5RND01-08，FG5-E2E-01 用真实新档旅程断言供给够付。
            int supply = HomeValleyAnalysis.YieldTable.Values.Sum(y => y.TechDataYield);
            int legacyCost = ResearchCatalog.Nodes.Where(n => n.IsReady && n.Unlocks.Length > 0 && n.KeyItems.Count == 0 && n.Unlocks.All(u => !u.Contains(Lab))).Sum(n => n.Cost);
            Line($"  · 记录 DEBT-FG5RND01-08：新档技术数据目前只来自解析台首次解析 Demo 带回的模块，合计 {supply} 点（≈ {supply / 2} 研究点，T1 实验室）；"
                 + $"研发树开放前开局可造、现在要研究的内容（不含关键材料节点）合计 {legacyCost} 研究点（≈ {legacyCost * 2} 技术数据）。持续来源在 FG5-RND-02（残骸）/ FG5-RND-06（黑匣子），FG5-E2E-01 断言供给够付");
        }

        /// <summary>与 Python repr(float) 同一写法（0.1 → “0.1”，0 → “0.0”）。</summary>
        private static string Py(float v)
        {
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("E") ? s : s + ".0";
        }

        private static bool ContainsCjk(string s) => s != null && s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── B 实验室产出（FGT-RND-001）────────────────────────────────────────────

        private static void CheckLabOutput()
        {
            CampaignState s = NewWorld(5101);
            BuildingRecord a = AddLab(s);
            if (a == null)
            {
                return;
            }
            Give(s, ItemCatalog.TechDataId, 100);
            int tech0 = s.TechData;
            Seconds(2f);
            long m0 = Milli(s);
            long used0 = s.Research.TechConsumed;
            Seconds(120f);
            long made = Milli(s) - m0;
            long used = s.Research.TechConsumed - used0;
            BuildingStatus st = BuildingStatusService.Evaluate(s, a);
            Expect(a.PowerState == BuildingPowerState.Powered && Math.Abs(made - 2000) <= 500 && used >= 4 && used <= 5 && st.Kind == BuildingStatusKind.Working && st.ReasonCode == "lab.working"
                   && st.Reason.Contains("2 → 1") && Math.Abs(ResearchService.RatePerMinute(s) - 1.0) < 1e-6 && s.TechData == tech0 - (int)s.Research.TechConsumed,
                $"B1 一座 T1 仿真实验室跑 120 游戏秒：消耗技术数据 {used}（每分钟 2）、产出研究点 {made / 1000.0:0.###}（每分钟 1）；状态“{st.Reason}”；每分钟 +{ResearchService.RatePerMinute(s)}");

            BuildingRecord b = AddLab(s, tier: 2);
            BuildingRecord c = AddLab(s, tier: 3);
            Seconds(2f);
            long m1 = Milli(s);
            Seconds(120f);
            long made3 = Milli(s) - m1;
            double rate = ResearchService.RatePerMinute(s);
            Expect(b != null && c != null && ResearchService.PerCycleMilli(s, a) == 500 && ResearchService.PerCycleMilli(s, b) == 625 && ResearchService.PerCycleMilli(s, c) == 750
                   && Math.Abs(rate - 3.75) < 1e-6 && Math.Abs(made3 - 7500) <= 1875 && ResearchService.BuiltLabCount(s) == 3,
                $"B2 三座实验室（T1 / T2 / T3）产出相加：每周期 500 / 625 / 750 千分点，合计每分钟 {rate}；120 游戏秒实际产出 {made3 / 1000.0:0.###} 研究点");
            string summary = ResearchService.SummaryLine(s);
            Expect(summary.Contains("实验室 3 座") && summary.Contains("工作中 3") && !GameText.ContainsMarker(summary), $"B3 研发树顶部状态行“{summary}”");
        }

        // ── C 研究推进与取消（FGT-RND-001）────────────────────────────────────────

        private static void CheckProgressAndCancel()
        {
            CampaignState s = NewWorld(5201);
            for (int i = 0; i < 4; i++)
            {
                AddLab(s);
            }
            Give(s, ItemCatalog.TechDataId, 300);
            bool q1 = ResearchService.TryEnqueue(s, "logistics.splitter", out string r1);
            bool q2 = ResearchService.TryEnqueue(s, "logistics.underground", out string r2);
            bool done1 = StepUntil(() => ResearchService.IsCompleted(s, "logistics.splitter"), 200);
            bool head = ResearchService.Head(s)?.Id == "logistics.underground";
            Expect(q1 && q2 && done1 && head && BuildCatalog.IsUnlocked(s, "research:logistics.splitter") && s.Research.Queue.SequenceEqual(new[] { "logistics.underground" })
                   && s.Research.NewEntries.Contains("splitter") && s.Research.NewEntries.Contains("merger"),
                $"C1 队列按顺序投入：4 座实验室产出的研究点先投“分流与过滤”（5 点）直到完成（{done1}）→ 解锁分流器 / 合流器、建造菜单标“新”；队列头换成“地下传送带”（{r1}{r2}）");

            bool partial = StepUntil(() => ResearchService.Invested(s, "logistics.underground") >= 3, 120);
            int kept = ResearchService.Invested(s, "logistics.underground");
            bool removed = ResearchService.TryDequeue(s, "logistics.underground", out _);
            int pts0 = s.Research.Points;
            Seconds(40f);
            int afterIdle = ResearchService.Invested(s, "logistics.underground");
            int banked = s.Research.Points;
            Expect(partial && removed && kept > 0 && kept < 10 && afterIdle == kept && banked > pts0 && ResearchService.StateOf(s, N("logistics.underground")) == ResearchNodeState.Available
                   && ResearchService.StateText(s, N("logistics.underground")).Contains(kept + " / 10"),
                $"C2 取消（移出队列）：已投入的 {kept} 点留在节点上（之后 40 游戏秒不变：{afterIdle}），研究点攒着（{pts0} → {banked}）；节点写“已投入 {kept} / 10”");
            bool re = ResearchService.TryEnqueue(s, "logistics.underground", out _);
            int resumed = ResearchService.Invested(s, "logistics.underground");
            bool done2 = StepUntil(() => ResearchService.IsCompleted(s, "logistics.underground"), 120);
            Expect(re && (resumed >= Math.Min(10, kept + banked) || ResearchService.IsCompleted(s, "logistics.underground")) && done2,
                $"C3 重新排队：从保留的进度接着做，攒着的研究点立刻投进去（{kept} → {resumed}），随后完成（{done2}）");
        }

        // ── D 研发树门槛（FGT-RND-002）────────────────────────────────────────────

        private static void CheckGates()
        {
            CampaignState s = NewWorld(5301);
            AddLab(s);
            GridCell? at = FgProductionSelfCheck.FindFree(s, "electronics_bench", 6f, 30f);
            GridOpResult refused = at.HasValue ? PlanHistory.Place(s, "electronics_bench", at.Value, 0) : default;
            string why = refused.Reason?.Describe() ?? string.Empty;
            BuildCatalog.TryGet("electronics_bench", out BuildEntry entry);
            string gate = ResearchService.GateStatusLine(s, entry?.UnlockRule);
            Expect(at.HasValue && !refused.Success && why.Contains("工业 · 电子组装台") && !BuildCatalog.IsUnlocked(s, entry) && gate.Contains("可研究"),
                $"D1 没研究就放电子组装台：被拒，原因“{why}”；建造菜单悬停写树上的状态“{gate}”");
            string planNote0 = ProductionPlanner.ResearchNote(s, "electronics_bench");
            string planNoteOpen = ProductionPlanner.ResearchNote(s, "extraction_drill");
            Give(s, ItemCatalog.ResearchPointsId, 6);
            bool q = ResearchService.TryEnqueue(s, "industry.electronics", out _);
            bool done = ResearchService.IsCompleted(s, "industry.electronics");
            GridOpResult placed = at.HasValue ? PlanHistory.Place(s, "electronics_bench", at.Value, 0) : default;
            Expect(q && done && placed.Success && BuildCatalog.IsUnlocked(s, entry) && s.Research.Points == 0 && s.Research.NewEntries.Contains("electronics_bench"),
                "D2 研究完成（攒着的 6 点立刻投进去）→ 同一处可以放电子组装台的虚影；条目标“新”");
            string planNote1 = ProductionPlanner.ResearchNote(s, "electronics_bench");
            Expect(planNote0.Contains("需要先研究") && planNote0.Contains("工业 · 电子组装台") && planNote0.Contains("可研究") && planNoteOpen.Length == 0 && planNote1.Length == 0,
                $"D1b 产线规划助手：方案里还没研究的建筑在规划行后写“{planNote0.Trim()}”（开局可造的提取钻不写）；研究完成后后缀消失");

            BuildingRecord wh = EnsureWarehouse(s);
            GridOpResult up0 = wh != null ? HomeGridService.TryUpgrade(s, wh.BuildingId) : default;
            string upWhy = up0.Reason?.Describe() ?? string.Empty;
            Give(s, ItemCatalog.ResearchPointsId, 8);
            ResearchService.TryEnqueue(s, "logistics.warehouse_t2", out _);
            GridOpResult up1 = wh != null ? HomeGridService.TryUpgrade(s, wh.BuildingId) : default;
            Expect(wh != null && !up0.Success && upWhy.Contains("物流 · 仓库 T2") && up1.Success,
                $"D3 建筑升级门槛：没研究“物流 · 仓库 T2”时原地升级被拒（“{upWhy}”），研究后可以升级（{up1.Outcome}）");

            // 前置：没完成也没排在前面 → 拒绝；先排前置再排它 → 允许；不能挪到前置前面。
            bool pre0 = ResearchService.TryEnqueue(s, "industry.lab_t3", out string preWhy);
            bool pre1 = ResearchService.TryEnqueue(s, "industry.lab_t2", out _) && ResearchService.TryEnqueue(s, "industry.lab_t3", out _);
            bool move = ResearchService.TryMove(s, "industry.lab_t3", 0, out string moveWhy);
            Expect(!pre0 && preWhy.Contains("工业 · 实验室 T2") && pre1 && !move && moveWhy.Contains("工业 · 实验室 T2")
                   && s.Research.Queue.SequenceEqual(new[] { "industry.lab_t2", "industry.lab_t3" })
                   && ResearchService.StateOf(s, N("industry.lab_t3")) == ResearchNodeState.Queued,
                $"D4 前置：“实验室 T3”单独加入被拒（“{preWhy}”）；先排“实验室 T2”再排它可以；把它挪到前置前面被拒（“{moveWhy}”）");
            // 移出前置：以它为前置的后续节点一起移出（进度保留）。
            ResearchService.TryDequeue(s, "industry.lab_t2", out _);
            Expect(s.Research.Queue.Length == 0, "D5 移出前置时，排在后面以它为前置的节点一起移出（不会永远等不到前置）");

            // 队列已满。
            string[] five = { "logistics.splitter", "logistics.pipe_underground", "energy.pole_t2", "energy.fuel_generator", "signal.tower_t2" };
            bool all5 = five.All(id => ResearchService.TryEnqueue(s, id, out _));
            bool sixth = ResearchService.TryEnqueue(s, "industry.refinery_tower", out string fullWhy);
            bool unknown = ResearchService.TryEnqueue(s, "no.such.node", out string unknownWhy);
            bool dup = ResearchService.TryEnqueue(s, "logistics.splitter", out string dupWhy);
            bool doneAgain = ResearchService.TryEnqueue(s, "industry.electronics", out string doneWhy);
            Expect(all5 && !sixth && fullWhy.Contains("最多 5 项") && !unknown && unknownWhy.Length > 0 && !dup && dupWhy.Contains("已经在队列") && !doneAgain && doneWhy.Contains("已经研究完成"),
                $"D6 队列最多 5 项：第 6 项被拒（“{fullWhy}”）；重复加入（“{dupWhy}”）、已完成（“{doneWhy}”）、不存在的节点（“{unknownWhy}”）都被拒");
            bool reorder = ResearchService.TryMove(s, "signal.tower_t2", 0, out _);
            Expect(reorder && s.Research.Queue[0] == "signal.tower_t2" && s.Research.Queue.Length == 5, "D7 调整顺序：没有前置约束的节点可以挪到第一位");
        }

        private static void CheckKeyMaterialAndBranches()
        {
            CampaignState s = NewWorld(5401);
            for (int i = 0; i < 4; i++)
            {
                AddLab(s);
            }
            Give(s, ItemCatalog.TechDataId, 400);
            int stalled0 = NotifyCount("research_stalled");
            bool q = ResearchService.TryEnqueue(s, "signal.override_t1", out _) && ResearchService.TryEnqueue(s, "logistics.splitter", out _);
            ResearchNodeDef ov = N("signal.override_t1");
            ResearchService.Step(s, 3, GameClock.StepHz);
            ResearchNodeState wait = ResearchService.StateOf(s, ov);
            string reason = ResearchText.BlockReason(s, ov) ?? string.Empty;
            bool skipped = StepUntil(() => ResearchService.IsCompleted(s, "logistics.splitter"), 200);
            int ovInvested = ResearchService.Invested(s, "signal.override_t1");
            ResearchStatus status = ResearchService.StatusOf(s);
            Seconds(1f);
            int stalled1 = NotifyCount("research_stalled");
            string note = LastNotify("research_stalled");
            Seconds(10f);
            int stalled2 = NotifyCount("research_stalled");
            Expect(q && wait == ResearchNodeState.WaitingKey && reason.Contains("监听阵列核") && reason.Contains("不消耗") && skipped && ovInvested == 0
                   && status == ResearchStatus.WaitingKey && stalled1 == stalled0 + 1 && stalled2 == stalled1 && note.Contains("监听阵列核"),
                $"D8 需要关键材料的节点（超控阵列 T1）：核心保管库没有监听阵列核时排着但不投入（“{reason}”），研究点给后面的“分流与过滤”（完成 {skipped}）；" +
                $"之后整体状态“等关键材料”，发一次“研究暂停”通知（“{note}”），不重复发");
            Give(s, "listening_array_core", 1);
            bool done = StepUntil(() => ResearchService.IsCompleted(s, "signal.override_t1"), 400);
            Expect(done && HomeInventory.Stock(s, "listening_array_core") == 1 && BuildCatalog.IsUnlocked(s, "research:signal.override_t1"),
                "D9 监听阵列核放进核心保管库后接着研究、完成；关键材料不被研究消耗（仍是 1 件，建造超控阵列时才投入）");

            // 阵营分支：没破解该阵营的技术 → 分支未开放；破解后开放，但内容在后续版本（不能加入，写明）。
            ResearchBranchDef silent = ResearchCatalog.Branches.First(b => b.Faction == "silent");
            bool closed = !ResearchService.BranchOpen(s, silent) && !ResearchService.TryEnqueue(s, "silent.component", out string closedWhy) && closedWhy.Contains("静默");
            string closedState = ResearchService.StateText(s, N("silent.component"));
            string fw = FirmwareKinds.Rows.Select(r => r.Id).FirstOrDefault(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.FactionOf(id) == "silent");
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(fw).ToArray();
            bool open = fw != null && ResearchService.BranchOpen(s, silent);
            bool later = !ResearchService.TryEnqueue(s, "silent.component", out string laterWhy) && laterWhy.Contains("后续版本")
                         && ResearchService.StateOf(s, N("silent.component")) == ResearchNodeState.Later;
            bool foundryClosed = !ResearchService.BranchOpen(s, ResearchCatalog.Branches.First(b => b.Faction == "foundry"));
            bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ResearchEntryId("silent.turret")) && !MechanicCodex.IsUnlocked(MechanicCodex.ResearchEntryId("foundry.turret"));
            Expect(closed && closedState.Contains("分支未开放") && open && later && foundryClosed && codex,
                $"D10 阵营分支：没破解静默的技术时“{closedState}”、加入被拒；破解第一件静默固件（{fw}）后静默分支开放（图鉴解锁该分支节点），铸造分支仍关；" +
                $"节点内容在后续版本（“{laterWhy}”）");
            bool defLater = !ResearchService.TryEnqueue(s, "defense.barrier", out string defWhy) && defWhy.Contains("后续版本"); // FG6-DEF-01 炮塔座已开放，改用屏障（FG6-DEF-02）
            Expect(defLater, $"D11 内容还没做的节点（防御 · 屏障与闸门）显示在树上，写明后续版本开放，不能加入（“{defWhy}”）");
        }

        // ── E 负向 ───────────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            // E1 研究中技术数据耗尽：实验室停工写原因，研究暂停、进度保留；补技术数据后继续。
            CampaignState s = NewWorld(5501);
            BuildingRecord lab = AddLab(s, tier: 3);
            Give(s, ItemCatalog.TechDataId, 2);
            ResearchService.TryEnqueue(s, "logistics.splitter", out _);
            ResearchService.TryEnqueue(s, "energy.pole_t2", out _);
            ResearchService.TryEnqueue(s, "energy.fuel_generator", out _);
            int stall0 = NotifyCount("research_stalled");
            bool ran = StepUntil(() => s.TechData == 0 && ResearchService.LabStateOf(s, lab) == LabState.NoTech, 300);
            Seconds(5f);
            int inv = ResearchService.Invested(s, "logistics.splitter");
            BuildingStatus st = BuildingStatusService.Evaluate(s, lab);
            string status = ResearchService.StatusText(s);
            Seconds(30f);
            bool held = ResearchService.Invested(s, "logistics.splitter") == inv;
            int stall1 = NotifyCount("research_stalled");
            Expect(ran && st.Kind == BuildingStatusKind.NoMaterial && st.Reason.Contains("技术数据不足") && st.Reason.Contains("解析") && status.Contains("研究暂停")
                   && status.Contains("进度保留") && held && inv >= 1 && stall1 == stall0 + 1 && s.Research.StallNotified == "no_tech",
                $"E1 研究中技术数据耗尽：实验室“{st.Reason}”；研究“{status}”；进度 {inv} 点保留（30 游戏秒不变）；“研究暂停”通知一次");
            Give(s, ItemCatalog.TechDataId, 40);
            bool resumed = StepUntil(() => ResearchService.Invested(s, "logistics.splitter") > inv, 120);
            Give(s, ItemCatalog.TechDataId, 40); // 后面的缺电 / 禁用要实验室本来能工作
            Expect(resumed && ResearchService.StatusOf(s) == ResearchStatus.Running && s.Research.StallNotified.Length == 0,
                "E2 补上技术数据：实验室恢复、研究接着推进，暂停标记清掉（下次再停会再通知）");

            // E3 缺电 / 禁用。
            List<BuildingRecord> gens = Generators(s);
            foreach (BuildingRecord g in gens)
            {
                g.ConstructionState = BuildingConstructionState.Disabled;
            }
            BuildingOps.TrySetEnabled(s, lab.BuildingId, true, out _);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            BuildingStatus np = BuildingStatusService.Evaluate(s, lab);
            int invNp = ResearchService.Invested(s, "logistics.splitter");
            Seconds(20f);
            bool npHeld = ResearchService.Invested(s, "logistics.splitter") == invNp || ResearchService.IsCompleted(s, "logistics.splitter");
            bool npOk = ResearchService.LabStateOf(s, lab) == LabState.NoPower && np.Kind == BuildingStatusKind.NoPower && np.Reason.Contains("电")
                        && ResearchService.StatusOf(s) == ResearchStatus.NoPower;
            foreach (BuildingRecord g in gens)
            {
                g.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            BuildingOps.TrySetEnabled(s, lab.BuildingId, false, out _);
            Seconds(1f);
            BuildingStatus dis = BuildingStatusService.Evaluate(s, lab);
            bool disOk = ResearchService.LabStateOf(s, lab) == LabState.Disabled && dis.Kind == BuildingStatusKind.Disabled && ResearchService.StatusOf(s) == ResearchStatus.Disabled;
            BuildingOps.TrySetEnabled(s, lab.BuildingId, true, out _);
            HomeValleyPowerGrid.Recompute(s);
            Expect(npOk && npHeld && disOk, $"E3 缺电：实验室“{np.Reason}”、研究暂停、进度不动；禁用：“{dis.Reason}”、研究写“实验室都被禁用了”");

            // E4 没有实验室：研究点攒着不投入，写明要建实验室。
            CampaignState n = NewWorld(5502);
            Give(n, ItemCatalog.ResearchPointsId, 20);
            ResearchService.TryEnqueue(n, "logistics.splitter", out _);
            Seconds(5f);
            string noLab = ResearchService.StatusText(n);
            bool noInvest = ResearchService.Invested(n, "logistics.splitter") == 0 && n.Research.Points == 20 && ResearchService.StatusOf(n) == ResearchStatus.NoLab;
            AddLab(n);
            Seconds(1f);
            Expect(noInvest && noLab.Contains("还没有仿真实验室") && ResearchService.IsCompleted(n, "logistics.splitter"),
                $"E4 没有实验室：研究不推进、研究点攒着（“{noLab}”）；建好实验室后攒着的点投进去、完成");

            // E5 拆除 / 被摧毁：本周期取的技术数据退回；被摧毁时停住、修好接着做。
            CampaignState d = NewWorld(5503);
            BuildingRecord l1 = AddLab(d);
            Give(d, ItemCatalog.TechDataId, 10);
            Seconds(3f);
            LabRecord rec = ResearchService.FindLab(d, l1.BuildingId);
            bool loaded = rec != null && rec.Loaded && rec.ProgressTicks > 0;
            int tech0 = d.TechData;
            l1.ConstructionState = BuildingConstructionState.Damaged;
            Seconds(5f);
            bool frozen = ResearchService.FindLab(d, l1.BuildingId)?.Loaded == true && d.TechData == tech0 && ResearchService.LabStateOf(d, l1) == LabState.Destroyed;
            l1.ConstructionState = BuildingConstructionState.Operational;
            ResearchService.OnDemolished(d, l1);
            d.BuildingRecords = d.BuildingRecords.Where(b => b.BuildingId != l1.BuildingId).ToArray();
            Seconds(1f);
            bool refunded = d.TechData == tech0 + 1 && ResearchService.FindLab(d, l1.BuildingId) == null;
            BuildingRecord l2 = AddLab(d);
            Seconds(3f);
            int tech1 = d.TechData;
            d.BuildingRecords = d.BuildingRecords.Where(b => b.BuildingId != l2.BuildingId).ToArray(); // 不经拆除流程消失（如读到坏档）：对账时同样退回
            Seconds(1f);
            Expect(loaded && frozen && refunded && d.TechData == tech1 + 1,
                $"E5 实验室被摧毁：本周期停住、已取的技术数据留着；拆除：退回本周期已取的 1 件技术数据（{tech0} → {tech0 + 1}）；记录消失时对账同样退回");
        }

        // ── F 效果（效率 / 容量）──────────────────────────────────────────────────

        private static void CheckEffects()
        {
            long d0 = DrillCycle(5601, false, out double plan0, out _);
            long d1 = DrillCycle(5601, true, out double plan1, out CampaignState s);
            bool drill = d0 > 0 && d1 > 0;
            double total = ResearchService.EffectTotal(s, ResearchCatalog.EffectSpeed, "gathering");
            Expect(drill && d1 == (long)Math.Round(d0 / 1.3) && Math.Abs(total - 0.3) < 1e-6 && Math.Abs(ResearchService.SpeedFactor(s, "refinery_furnace") - 1.0) < 1e-9
                   && plan0 > 0 && Math.Abs(plan1 / plan0 - 1.3) < 1e-6,
                $"F1 效率：采集优化 I～III 完成后提取钻每个周期 {d0} → {d1} 步（+30%，到上限）；加工建筑不受影响；产线规划助手按加成后的速率算（{plan0:0.##} → {plan1:0.##} /分钟）");

            EnsureWarehouse(s);
            ItemDef alloy = ItemCatalog.Find("alloy");
            int cap0 = HomeInventory.Capacity(s, alloy);
            ResearchService.CompleteForTests(s, "logistics.storage_cap_1");
            int cap1 = HomeInventory.Capacity(s, alloy);
            GridCell? es = FgProductionSelfCheck.FindFree(s, "energy_storage", 6f, 30f);
            BuildingRecord store = es.HasValue ? FgProductionSelfCheck.Built(s, "energy_storage", "rnd_store", es.Value) : null;
            HomeValleyPowerGrid.Recompute(s);
            HomeValleyPowerGrid.TryGetStorage(s, store?.BuildingId, out _, out double sc0);
            ResearchService.CompleteForTests(s, "energy.storage_cap_1");
            HomeValleyPowerGrid.Recompute(s);
            HomeValleyPowerGrid.TryGetStorage(s, store?.BuildingId, out _, out double sc1);
            Expect(cap0 > 0 && cap1 == (int)Math.Round(cap0 * 1.15) && store != null && sc0 > 0 && Math.Abs(sc1 / sc0 - 1.2) < 1e-6,
                $"F2 容量：货架优化 I 后仓库容量 {cap0} → {cap1}（+15%）；电芯扩容 I 后储能站容量 {sc0:0.#} → {sc1:0.#} 分钟（+20%）");

            BuildingRecord lab = AddLab(s);
            int m0 = ResearchService.PerCycleMilli(s, lab);
            ResearchService.CompleteForTests(s, "industry.eff_lab_1");
            int m1 = ResearchService.PerCycleMilli(s, lab);
            string preview = ResearchText.Detail(s, N("industry.eff_gather_2"));
            Expect(m0 == 500 && m1 == 550 && preview.Contains("提取钻") && preview.Contains("+10%") && preview.Contains("上限 +30%"),
                $"F3 实验室效率：仿真校准 I 后每周期 {m0} → {m1} 千分点；效率节点的预览写作用到哪些建筑、累计与上限（“{preview.Replace("\n", " / ")}”）");
        }

        /// <summary>同一种子的家园里建一座提取钻（研究可选地先完成），返回它第一个周期的长度（步）与规划助手给的每分钟产量。</summary>
        private static long DrillCycle(int seed, bool researched, out double planPerMinute, out CampaignState s)
        {
            s = NewWorld(seed);
            if (researched)
            {
                ResearchService.CompleteForTests(s, "industry.eff_gather_1", "industry.eff_gather_2", "industry.eff_gather_3");
            }
            ProductionPlanner.Extractor(ItemCatalog.Find("metal_ore"), out planPerMinute, out _, out _);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 2, 2, 8f, 24f);
            if (!o.HasValue)
            {
                return 0;
            }
            FgProductionSelfCheck.SetFootprint(s, "extraction_drill", o.Value, "ore_metal");
            BuildingRecord drill = FgProductionSelfCheck.Built(s, "extraction_drill", "rnd_drill", o.Value);
            Seconds(1f);
            ProductionService.Producer p = FgProductionSelfCheck.P(s, drill);
            return p != null && p.Rec.Running ? p.Rec.Duration : 0;
        }

        // ── G 真文件存读档与旧档迁移 ─────────────────────────────────────────────

        private static void LayScenario(CampaignState s)
        {
            AddLab(s);
            AddLab(s, tier: 2);
            Give(s, ItemCatalog.TechDataId, 60);
            ResearchService.TryEnqueue(s, "logistics.splitter", out _);
            ResearchService.TryEnqueue(s, "logistics.underground", out _);
            ResearchService.TryEnqueue(s, "energy.pole_t2", out _);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(5701);
            LayScenario(s);
            Seconds(75f); // 分流与过滤完成、地下传送带投入一半、实验室周期进行到一半
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(90f);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded, out int notesAfterLoad)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                ResearchService.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                notesAfterLoad = -1;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                int n0 = NotifyCount("research_done") + NotifyCount("research_stalled");
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                notesAfterLoad = NotifyCount("research_done") + NotifyCount("research_stalled") - n0;
                Seconds(90f);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1, out int notes1);
            string second = RunFromSave(out _, out _);
            Expect(save.Success && loaded1 == before && before.Contains("logistics.splitter") && before.Contains("L") && before.Contains("new=") && notes1 == 0,
                "G1 真文件存读档：研究点（含零头）、已完成节点、队列、每个节点的进度、每座实验室的周期进度与已取的技术数据、“新”标记、暂停标记写进存档，读档后逐字段一致；读档不补发通知"
                + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first && continuous.Contains("done=logistics.splitter") && !before.Contains("done=logistics.splitter"),
                "G2 读档后接着跑 90 游戏秒（实验室周期接着走、“分流与过滤”完成、地下传送带接着投入），与不存档一直跑逐位一致；同一存档读两次结果一致"
                + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        private static void CheckMigration()
        {
            CampaignState s = NewWorld(5702);
            s.Research.DomainVersion = 1; // 研发树开放前的旧档
            s.Research.CompletedNodes = Array.Empty<string>();
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            ResearchService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            ResearchState r = rr.State?.Research;
            bool migrated = rr.Success && r != null && r.DomainVersion == ResearchState.CurrentVersion
                            && new[] { "logistics.splitter", "logistics.belt_t3", "energy.storage", "industry.electronics", "signal.override_t1", "logistics.warehouse_t2" }.All(id => r.CompletedNodes.Contains(id))
                            && !r.CompletedNodes.Contains("industry.eff_gather_1") && !r.CompletedNodes.Contains("logistics.storage_cap_1") && !r.CompletedNodes.Contains("defense.turret_t2")
                            && !r.CompletedNodes.Contains("industry.lab_t2") && !r.CompletedNodes.Contains("industry.lab_t3");
            CampaignState fresh = CampaignState.CreateNew("fgrnd-fresh", "Standard", 5703);
            Expect(save.Success && migrated && fresh.Research.DomainVersion == ResearchState.CurrentVersion && fresh.Research.CompletedNodes.Length == 0,
                $"G3 旧档迁移（研究域 1 → 2）：研发树开放前就能建造的内容对应的节点记为已研究（{r?.CompletedNodes.Length} 个，旧档里在用的东西不会突然造不了），效率 / 容量、未开放内容与实验室 T2 / T3（仿真实验室是研发树带来的新建筑）不送；新战役从零开始");
        }

        // ── H / I 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe);
            pausedHeld = true;
            LayScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 150;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 900)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(5801, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("logistics.splitter"),
                "H 暂停中（120 帧）实验室与研究不动；0.5x / 1x / 2x / 3x 跑同样的 150 游戏秒（实验室周期、研究点、队列投入与完成）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(5802, true, 1f, false, out _);
            string unseen = RunScenario(5802, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("logistics.splitter"), "I 同一组实验室与研究队列在观察与不观察家园时跑 150 游戏秒逐字段一致（FGR-BASE-021）"
                                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── J 面板（真 UXML）──────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
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

        private static void SendPointer<T>(VisualElement target, Vector2 pos, EventType type, int button) where T : PointerEventBase<T>, new()
        {
            var ev = new Event { type = type, mousePosition = pos, button = button, clickCount = 1 };
            using (T e = PointerEventBase<T>.GetPooled(ev))
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static void PressKey(Keys keys, GameActionId action)
        {
            keys.Down = GameSettings.KeyBindings.GetKey(action);
            UiKitInputPump.ProcessLibraryKeys();
            keys.Down = KeyCode.None;
            InputRouter.DebugClearConsumedKeys();
        }

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(5901);
            AddLab(s);
            Give(s, ItemCatalog.TechDataId, 50);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "ResearchTreePanel.uxml", out GameObject go);
            ResearchTreePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ResearchTreePanelUIToolkit panel = go.AddComponent<ResearchTreePanelUIToolkit>();
                panel.BindView(root);
                panel.SetTemplatesForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ResearchNode.uxml"),
                    AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ResearchQueueRow.uxml"));
                var keys = new Keys();
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                PressKey(keys, GameActionId.OpenResearch);
                panel.Refresh();
                int nodes = ResearchCatalog.Nodes.Count;
                bool opened = ResearchTreePanelUIToolkit.IsOpen && panel.PanelVisible && InputRouter.IsModalOwner(panel) && panel.NodeViewCount == nodes && panel.VisibleNodeCount == nodes
                              && panel.LinkCount >= 0 && panel.FilterButtonCount == ResearchCatalog.Branches.Count + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.ResearchTreeFirstOpen)
                              && panel.QueueEmptyText.Contains("最多 5 项") && panel.StatusText.Contains("研究队列为空") && panel.FooterText.Contains("K")
                              && !GameText.ContainsMarker(panel.SummaryText + panel.StatusText + panel.FooterText + panel.QueueTitleText);
                Expect(opened, $"J1 按 K 打开研发树（模态）：{panel.NodeViewCount} 个节点、{panel.FilterButtonCount} 个筛选按钮；状态“{panel.StatusText}”；队列空状态“{panel.QueueEmptyText}”；页脚“{panel.FooterText}”");

                // 搜索：命中描金边、其余变暗；没有命中写明。
                panel.SetSearch("分流");
                bool hit = panel.MatchCount >= 1 && panel.FirstMatchId == "logistics.splitter" && panel.NodeHasClass("logistics.splitter", "rt-node-match")
                           && panel.NodeHasClass("industry.lab_t2", "rt-node-dim") && panel.SearchCountText.Contains("找到");
                panel.SetSearch("没有这种东西zz");
                bool none = panel.MatchCount == 0 && panel.SearchCountText.Contains("没有匹配");
                panel.SetSearch("电子组装台"); // 按解锁内容也能搜到
                bool byUnlock = panel.FirstMatchId == "industry.electronics";
                panel.SetSearch(string.Empty);
                Expect(hit && none && byUnlock, $"J2 搜索：“分流”命中 {panel.MatchCount} 个以外变暗；按解锁内容“电子组装台”找到节点；没有命中时写“没有匹配”");

                // 分支筛选：只显示一个分支；阵营分支未开放时写明。
                int logi = Enumerable.Range(0, panel.FilterButtonCount).First(i => panel.FilterId(i) == "logistics");
                bool filtered = Click(panel.FilterButton(logi)) && panel.Filter == "logistics" && panel.NodeVisible("logistics.splitter") && !panel.NodeVisible("industry.lab_t2")
                                && panel.VisibleNodeCount == ResearchCatalog.Branches.First(b => b.Id == "logistics").Nodes.Count;
                int silentIdx = Enumerable.Range(0, panel.FilterButtonCount).First(i => panel.FilterId(i) == "silent");
                string silentLane = panel.LaneText(ResearchCatalog.Branches.First(b => b.Id == "silent").Index);
                bool closedLane = panel.FilterButton(silentIdx).ClassListContains("rt-filter-closed") && silentLane.Contains("破解");
                Click(panel.FilterButton(0));
                Expect(filtered && closedLane && panel.Filter == null && panel.VisibleNodeCount == nodes,
                    $"J3 按分支筛选：点“物流”只显示物流的节点；阵营分支未开放时按钮与车道写“{silentLane}”；点“全部分支”恢复");

                // 缩放（滚轮 / 按钮，夹在上下限）与平移（拖空白处）。
                VisualElement viewport = root.Q<VisualElement>("ResearchViewport");
                float z0 = panel.Zoom;
                using (WheelEvent we = WheelEvent.GetPooled(new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, -3f), mousePosition = new Vector2(300f, 200f) }))
                {
                    we.target = viewport;
                    viewport.SendEvent(we);
                }
                float z1 = panel.Zoom;
                for (int i = 0; i < 30; i++)
                {
                    Click(root.Q<Button>("ResearchZoomIn"));
                }
                float zMax = panel.Zoom;
                for (int i = 0; i < 40; i++)
                {
                    Click(root.Q<Button>("ResearchZoomOut"));
                }
                float zMin = panel.Zoom;
                Click(root.Q<Button>("ResearchZoomReset"));
                Vector2 pan0 = panel.PanOffset;
                SendPointer<PointerDownEvent>(viewport, new Vector2(400f, 300f), EventType.MouseDown, 0);
                SendPointer<PointerMoveEvent>(viewport, new Vector2(330f, 260f), EventType.MouseDrag, 0);
                SendPointer<PointerUpEvent>(viewport, new Vector2(330f, 260f), EventType.MouseUp, 0);
                Vector2 pan1 = panel.PanOffset;
                Expect(z1 > z0 && Mathf.Approximately(zMax, 1.6f) && Mathf.Approximately(zMin, 0.5f) && Mathf.Approximately(panel.Zoom, 1f) && (pan1 - pan0).magnitude > 10f
                       && panel.ZoomText == "100%",
                    $"J4 缩放：滚轮放大 {z0} → {z1}；按钮放大到上限 {zMax}、缩小到下限 {zMin}；“复位”回 100%；拖空白处平移 {pan0} → {pan1}");

                // 悬停预览：会解锁什么、建筑占地、占位图标。
                panel.HoverNode("industry.electronics");
                bool preview = panel.DetailNodeId == "industry.electronics" && panel.DetailTitle.Contains("电子组装台") && panel.DetailBody.Contains("占地 3×3")
                               && panel.FootprintShown && panel.FootprintText == "3×3" && panel.DetailNote.Contains("占位图标") && panel.DetailBody.Contains("可研究");
                panel.HoverNode("signal.override_t1");
                bool keyPreview = panel.DetailBody.Contains("监听阵列核") && panel.DetailBody.Contains("不消耗") && !panel.FootprintShown == false;
                panel.HoverNode(null);
                Expect(preview && panel.DetailBody.Length > 0, $"J5 悬停节点：详情写它会解锁什么（“{panel.DetailBody.Split('\n').FirstOrDefault(l => l.Contains("占地"))}”）、建筑占地框 {panel.FootprintText}、占位图标标明；关键材料节点写明不消耗（{keyPreview}）");

                // 左键加入、右键移出（进度保留）、拖动排序与不合法排序。
                bool add1 = Click(panel.NodeButton("logistics.splitter")) && ResearchService.QueueIndex(s, "logistics.splitter") == 0;
                panel.ClickNode("energy.pole_t2");
                panel.ClickNode("industry.lab_t2");
                panel.ClickNode("industry.lab_t3");
                panel.ClickNode("defense.barrier"); // FG6-DEF-01 炮塔座已开放，“后续版本”改用屏障（FG6-DEF-02）
                string refusedMsg = panel.MessageText;
                panel.Refresh();
                bool rows = panel.QueueRowVisibleCount == 4 && panel.QueueRowText(0).Contains("物流 · 分流与过滤") && panel.QueueTitleText.Contains("4 / 5");
                panel.BeginQueueDrag(3);
                bool badDrop = !panel.DropQueueAt(1) && panel.MessageText.Contains("不能排在前置");
                panel.BeginQueueDrag(1);
                bool goodDrop = panel.DropQueueAt(0) && panel.QueueRowNode(0) == "energy.pole_t2";
                bool upBtn = Click(panel.QueueRowButton(1, 0)) && panel.QueueRowNode(0) == "logistics.splitter";
                SendPointer<PointerDownEvent>(panel.NodeButton("energy.pole_t2"), Vector2.zero, EventType.MouseDown, 1);
                bool rightRemoved = ResearchService.QueueIndex(s, "energy.pole_t2") < 0 && panel.MessageText.Contains("保留");
                Expect(add1 && refusedMsg.Contains("后续版本") && rows && badDrop && goodDrop && upBtn && rightRemoved,
                    $"J6 左键加入队列（不能加的写原因“{refusedMsg}”）；队列行“{panel.QueueRowText(0)}”；拖动排序：挪到前置前面被拒、合法的生效；上移按钮；右键移出写明进度保留");

                // 研究推进后节点状态与进度条刷新；状态行。
                Give(s, ItemCatalog.ResearchPointsId, 5);
                Seconds(2f);
                panel.Refresh();
                bool live = panel.NodeStateText("logistics.splitter").Contains("已完成") && panel.NodeHasClass("logistics.splitter", "rt-node-done")
                            && panel.NodeFill("logistics.splitter") > 99f && panel.StatusText.Length > 0 && !panel.StatusText.Contains("分流与过滤");
                Expect(live, $"J7 研究推进后节点刷新：“{panel.NodeStateText("logistics.splitter")}”、进度条满；状态行“{panel.StatusText}”");

                // Esc / 快捷键关闭。
                PressKey(keys, GameActionId.OpenResearch);
                bool closedByKey = !ResearchTreePanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(panel);
                ResearchTreePanelUIToolkit.Open();
                bool escClosed = UiEscapeStack.CloseTop() && !ResearchTreePanelUIToolkit.IsOpen;
                Expect(closedByKey && escClosed, "J8 再按 K 关闭；Esc 关闭（先关最上层）");

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "ResearchTreePanel.uxml", "ResearchWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("ResearchRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "J9 研发树面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                ResearchTreePanelUIToolkit.Close();
                ResearchTreePanelUIToolkit.InWorldOverrideForTests = false;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(go);
            }
        }

        // ── K 通知 / 离家报告 / 图鉴 / 钩子 / 建造菜单“新” ─────────────────────────

        private static void CheckFeedback()
        {
            CampaignState s = NewWorld(6001);
            BuildingRecord lab = AddLab(s);
            Seconds(1f);
            AwayReportRecord rep = AwayReportService.Begin(s, "fractured_city", new[] { 1 });
            int done0 = NotifyCount("research_done");
            Give(s, ItemCatalog.ResearchPointsId, 5);
            ResearchService.TryEnqueue(s, "logistics.splitter", out _);
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type?.Id == "research_done");
            bool notified = NotifyCount("research_done") == done0 + 1 && note != null && note.Text.Contains("分流与过滤") && note.Text.Contains("分流器")
                            && note.Members.Count > 0 && note.Members[note.Members.Count - 1].HasLocation;
            bool away = rep != null && rep.Entries.Any(e => e.Section == "research" && e.TypeId == "research_done" && e.Detail.Contains("分流与过滤"));
            AwayReportService.End(s, 1, notify: false);
            Expect(notified && away && GameSettings.HasSeenGuidanceHook(GuidanceHooks.ResearchFirstCompleted) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.ResearchFirstQueued)
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.ResearchLabFirstBuilt),
                $"K1 研究完成：通知“{note?.Text}”（带实验室位置，点击定位 / 打开研发树）；离家期间完成的研究记进离家报告“研究”段（承接 FG-GAP-099 的研究部分）；首次排队 / 完成 / 建成实验室的钩子都发了");

            int researchEntries = MechanicCodex.Entries.Count(e => e.Tab == MechanicCodex.TabResearch);
            MechanicCodexEntry ce = MechanicCodex.Find(MechanicCodex.ResearchEntryId("logistics.splitter"));
            string body = ce != null ? MechanicCodex.Body(ce, s) : string.Empty;
            bool codex = researchEntries == ResearchCatalog.Nodes.Count && ce != null && ce.AlwaysOpen && body.Contains("已完成") && body.Contains("分流器")
                         && MechanicCodex.Find("codex.research.lab") != null && MechanicCodex.Find("codex.research.tree") != null && MechanicCodex.IsUnlocked("codex.research.lab");
            Expect(codex, $"K2 图鉴：“研究”页签 {researchEntries} 条（每个节点一条，正文与研发树详情同一份）；系统说明“仿真实验室与研究点”（建成实验室时解锁）与“研发树”");

            // 建造菜单“新”标记：显示过的页离开时记为看过。
            bool isNew = ResearchService.IsNew(s, "splitter") && ResearchService.AnyNewInCategory(s, "logistics");
            int removed = ResearchService.MarkSeen(s, new HashSet<string> { "splitter" });
            bool stillMerger = ResearchService.IsNew(s, "merger") && !ResearchService.IsNew(s, "splitter");
            ResearchService.MarkSeen(s, new HashSet<string> { "merger" });
            Expect(isNew && removed == 1 && stillMerger && !ResearchService.AnyNewInCategory(s, "logistics"),
                "K3 建造菜单“新”：研究解锁的条目与所在分类标“新”，看过一次（离开显示它的那一页）就去掉，只去掉看过的那条（真实建造栏在冒烟里走）");
            ResearchService.ResetForTests();
        }

        // ── L 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6101);
            int labs = 0;
            for (int i = 0; i < 40; i++)
            {
                if (AddLab(s) != null)
                {
                    labs++;
                }
            }
            Give(s, ItemCatalog.TechDataId, 5000);
            foreach (string id in new[] { "logistics.splitter", "logistics.underground", "logistics.belt_t2", "logistics.belt_t3", "energy.fuel_generator" })
            {
                ResearchService.TryEnqueue(s, id, out _);
            }
            Seconds(1f);
            ResearchService.ResetStepStats();
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < 400; r++)
            {
                ResearchService.Step(s, 3, GameClock.StepHz);
            }
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / 400.0;
            sw.Restart();
            int sink = 0;
            for (int r = 0; r < 200; r++)
            {
                foreach (ResearchNodeDef n in ResearchCatalog.Nodes)
                {
                    sink += (int)ResearchService.StateOf(s, n);
                }
            }
            sw.Stop();
            double perTree = sw.Elapsed.TotalMilliseconds / 200.0;
            Expect(labs >= 20 && sink >= 0, $"L 场景：{labs} 座实验室 + 5 项队列");
            PerfLines.Add($"研究推进（{labs} 座实验室 + 5 项队列）每个生产步 {perStep:F3} ms（每 3 个世界步一次 = 20 Hz）；整棵树 {ResearchCatalog.Nodes.Count} 个节点的状态计算每次 {perTree:F3} ms（只在面板刷新时）");
            PerfGate.Expect(perStep <= 0.5 && perTree <= 2.0,
                $"L 性能：研究推进每步 {perStep:F3} ms（阈值 0.5 ms，O(实验室数 + 队列长度)）；整棵树状态 {perTree:F3} ms（阈值 2 ms，只在研究版本变化 / 每 0.5 秒刷新）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perStep, 0.5, "研究推进 ms"), PerfGate.Le(perTree, 2.0, "整棵树状态 ms") }, Expect, Line);

            // L2：研究完成之后，生产步里每座生产建筑都会查效率加成（ProductionService 的周期计算 → ScaleTicks / SpeedFactor → EffectTotal）。
            // 查询必须零分配（复审 P2：原来每次拼一个 "kind|target" 串）。计量同 FgDataPipelineSelfCheck：Unity 的 Mono 不实现
            // GC.GetAllocatedBytesForCurrentThread，用托管堆已用字节；100 万次查询若每次分配一个最小对象（≥16 B）堆增量至少 16 MB，容差 2 MB。
            CampaignState e = NewWorld(6102);
            ResearchService.CompleteForTests(e, "industry.eff_gather_1", "industry.eff_gather_2", "industry.eff_process_1", "industry.eff_manufacture_1", "logistics.storage_cap_1");
            string[] types = { "extraction_drill", "refinery_furnace", "electronics_bench", "component_workshop" };
            long warm = 0;
            for (int i = 0; i < 1000; i++)
            {
                warm += ResearchService.ScaleTicks(e, types[i & 3], 600);
            }
            const int calls = 1000000;
            GC.Collect();
            long heap0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            sw.Restart();
            long acc = 0;
            for (int i = 0; i < calls; i++)
            {
                acc += ResearchService.ScaleTicks(e, types[i & 3], 600);
            }
            sw.Stop();
            long heapGrow = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - heap0;
            double nsPerCall = sw.Elapsed.TotalMilliseconds * 1e6 / calls;
            double gather = ResearchService.SpeedFactor(e, "extraction_drill");
            Expect(heapGrow < 2L * 1024 * 1024 && warm > 0 && acc > 0 && Math.Abs(gather - 1.2) < 1e-6,
                $"L2 已完成效率节点时，生产建筑查效率加成零分配：{calls} 次 ScaleTicks 托管堆增长 {heapGrow / 1024} KB（容差 2048 KB）；提取钻倍率 {gather:0.##}");
            PerfLines.Add($"已完成 5 个效率 / 容量节点时，生产建筑查效率加成（ScaleTicks → SpeedFactor → EffectTotal）每次 {nsPerCall:F0} ns，零分配（800 座生产建筑每步约 {nsPerCall * 800 / 1e6:F3} ms）");
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                PowerEnvironment.ResetForTests();
                ResearchGate.ResetForTests();
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
