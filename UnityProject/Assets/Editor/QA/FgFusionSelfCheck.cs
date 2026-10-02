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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG5-RND-04 熔合、配方书与线索的自动验收（FG05 FGR-RND-040～045；FG02 FGR-FW-050；FG13 FGU-21；FGT-RND-005～007；
    /// 负向“模拟熔合时选了混合固件”“正式熔合途中合成台被毁：事务回滚，父固件退回，不复制不丢失”）。
    /// 全部起真实系统：真实家园（世界模拟、电网、仓库、固件库、固件刻录台与传送带）、真实合成台队列、真文件存读档、真 UXML 面板、
    /// 真实远征战斗（破碎都市，两台机器带燃迹 / 漏油打出爆燃）生成线索。
    /// A 数据（两张新表与源数据逐字段、30 条混合固件的规则：父固件 / 负载 / 核心 / 类别 / 读法字段按合并规则、标签并集、文本中英、调参初值、钩子、图鉴、快捷键）；
    /// B FGT-RND-005 模拟熔合（不消耗固件、无反应、有配方；负向：混合固件 / 未破解 / 同一条 / 不在固件库 / 技术数据不够 / 合成台不工作）；
    /// C 正式熔合原子事务（先模拟才能熔合、预留 → 完成：消耗两枚父固件产出混合固件、记入配方书与解锁；取消全额退回；队列上限；材料不够整体不动；预留的芯片不能被别处拿走）；
    /// D 合成台被毁 / 被拆：在办的熔合回滚，两枚父固件回到固件库、芯片基板（仓库放不下落地）与技术数据退回，守恒（不复制、不丢失）；完成时芯片不在 → 回滚不凭空产出；
    /// E FGT-RND-006 发现后量产（刻录台可刻、每枚芯片基板 3、不再消耗父固件；未发现前拒绝；信号核不刻混合固件）；
    /// F FGR-FW-050 混合固件读法（读法 = 父固件按上限合并、装配反应按父固件识别、积热、负载；核心混合固件装不进机器电路）；
    /// G FGR-RND-044 线索（部分 / 完整阈值、同一场只分析一次、没有实验室先存着、第一次线索的引导钩子、上限）；H FGT-RND-007 真实战斗打出燃烧 + 油污 → 线索；
    /// I 配方书（各类别还剩几个未发现 = 配方表条数 − 已发现，随表联动；筛选；新旧排序）；J 真文件存读档（熔合中途存档 → 读档接着做 / 读档后被毁照样守恒）；
    /// K 暂停与 0.5x～3x；L 观察 / 不观察一致；M 面板（真 UXML：模拟、正式（确认框）、队列取消、配方书、线索选固件、布局探针）；N 建筑面板入口与状态；O 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgFusionSelfCheck）。
    /// </summary>
    public static class FgFusionSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string PanelUxml = "Assets/GameRes/Raw/UI/UiKit/FusionPanel.uxml";
        private const string QueueRowUxml = "Assets/GameRes/Raw/UI/UiKit/FusionQueueRow.uxml";
        private const string ClueRowUxml = "Assets/GameRes/Raw/UI/UiKit/FusionClueRow.uxml";
        private const string ProdUxml = "Assets/GameRes/Raw/UI/UiKit/ProductionPanel.uxml";
        private const int Slot = 5;
        private const string Burn = "fw_burntrail";
        private const string Oil = "fw_oilleak";
        private const string Coolant = "fw_coolant";
        private const string Nitrogen = "fw_nitrogen";
        private const string Napalm = "fw_mix_napalm";
        private const string NapalmRecipe = "fusion.napalm";
        private const string Substrate = "chip_substrate";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 熔合、配方书与线索")]
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
            Line("\n[熔合与配方书] 模拟熔合、正式熔合（原子事务 / 被毁回滚）、发现后量产、混合固件读法、线索、配方书（FG5-RND-04）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfusion-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                FirmwareKinds.ResetForTests();
                FusionCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                FusionService.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；熔合队列 / 线索 / 面板在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），战斗与传送带在 AOT 内核；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSimulate);
                Step(CheckFormal);
                Step(CheckRollback);
                Step(CheckMassBurn);
                Step(CheckMixedReadings);
                Step(CheckClues);
                Step(CheckBattleClue);
                Step(CheckRecipeBook);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanel);
                Step(CheckBuildingPanel);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"熔合自检抛异常：{e}");
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
                FusionPanelUIToolkit.InWorldOverrideForTests = false;
                FusionPanelUIToolkit.Close();
                FusionService.ResetSessionState();
                HomeValleyAnalysis.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
            Line($"  · [熔合与配方书] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static string _synthId;

        /// <summary>新家园：发电与仓库运转、一座电路合成台（按种子地形找空地，不写死坐标，B25）、可选一座仿真实验室；
        /// 燃迹 / 漏油 / 冷却液 / 液氮已破解，固件库里各两枚；仓库有芯片基板与技术数据。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, bool lab = true, int tech = 200, int substrate = 20)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            HomeValleyAnalysis.ResetSessionState();
            FusionService.ResetSessionState();
            CampaignState s = F.NewWorld(seed, observe, 600);
            F.PowerUp(s);
            EnsureWarehouse(s);
            GridCell? at = F.FindFree(s, FusionCatalog.TypeId, 6f, 30f);
            BuildingRecord synth = at.HasValue ? F.Built(s, FusionCatalog.TypeId, "synth" + seed, at.Value) : null;
            _synthId = synth?.BuildingId;
            if (lab)
            {
                GridCell? lc = F.FindFree(s, ResearchCatalog.LabTypeId, 6f, 30f);
                if (lc.HasValue)
                {
                    F.Built(s, ResearchCatalog.LabTypeId, "lab" + seed, lc.Value);
                }
            }
            Unlock(s, Burn, Oil, Coolant, Nitrogen);
            foreach (string fw in new[] { Burn, Oil, Coolant, Nitrogen })
            {
                PrimitiveInventory.GrantCrafted(s, fw);
                PrimitiveInventory.GrantCrafted(s, fw);
            }
            s.TechData = tech;
            HomeInventory.Add(s, Substrate, substrate);
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = F.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "fu_wh", c.Value);
                }
            }
            foreach (BuildingRecord w in s.BuildingRecords.Where(b => b != null && BuildingOps.IsWarehouse(b)))
            {
                BeltPortService.TrySetFilter(s, w.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        private static void Unlock(CampaignState s, params string[] ids)
        {
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(ids).Distinct().ToArray();
        }

        private static BuildingRecord Synth(CampaignState s) => FusionService.FindSynth(s, _synthId);

        private static int Chips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static int FreeChips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw && FusionService.IsFree(c));

        private static int Stock(CampaignState s, string item) => HomeInventory.Stock(s, ItemCatalog.Find(item));

        private static int Ground(CampaignState s, string item) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == ItemCatalog.Find(item).ResourceType).Sum(g => g.Amount);

        /// <summary>守恒量：父固件芯片总数、芯片基板与技术数据的家园总量——物资分布（仓库 / 端口 / 传送带 / 地面 / 机器货舱 / 生产缓存 / 熔合在办项，
        /// 与玩家悬停物品图标看到的同一个总数）。</summary>
        private static (int Burn, int Oil, int Sub, int Tech) Totals(CampaignState s)
        {
            ItemDistribution.Invalidate();
            int sub = (int)ItemDistribution.Get(s, ItemCatalog.Find(Substrate)).Total;
            ItemDistribution.Invalidate();
            int tech = (int)ItemDistribution.Get(s, ItemCatalog.Find(ItemCatalog.TechDataId)).Total;
            return (Chips(s, Burn), Chips(s, Oil), sub, tech);
        }

        private static void Seconds(float sec) => F.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => F.StepUntil(done, maxGameSeconds);

        /// <summary>模拟 → 正式熔合燃迹 + 漏油（玩家正常流程：先模拟确认有配方）。</summary>
        private static FusionOpResult SimThenFuse(CampaignState s)
        {
            FusionService.Simulate(s, _synthId, Burn, Oil);
            return FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
        }

        private static string Snapshot(CampaignState s)
        {
            FusionState f = FusionService.StateOf(s);
            var sb = new StringBuilder();
            sb.Append("tech=").Append(s.TechData).Append(" sub=").Append(Stock(s, Substrate)).Append('+').Append(Ground(s, Substrate))
                .Append(" disc=").Append(string.Join(",", f.Discovered))
                .Append(" jobs=").Append(string.Join(";", f.Jobs.Select(j => $"{j.JobId}|{j.BuildingId == _synthId}|{j.RecipeId}|{j.State}|{j.Progress.ToString("F3", CultureInfo.InvariantCulture)}|{j.Reason}|{j.Tech}|{j.Substrate}|{(string.IsNullOrEmpty(j.OutputPartId) ? 0 : 1)}")))
                .Append(" clues=").Append(string.Join(";", f.Clues.Select(c => $"{c.Serial}|{c.RecipeId}|{c.Full}|{c.KnownParent}|{c.Count}|{c.SessionKind}")))
                .Append(" sim=").Append(string.Join(";", f.SimLog.Select(x => x.PairKey + ">" + x.RecipeId)))
                .Append(" pend=").Append(f.Pending.Length).Append(" serial=").Append(f.NextSerial)
                .Append(" stat=").Append(f.Simulations).Append('|').Append(f.SimulationMisses).Append('|').Append(f.Fused).Append('|').Append(f.RolledBack).Append('|').Append(f.TechSpent)
                .Append(" chips=").Append(string.Join(",", (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                    .Where(c => c != null && FirmwareKinds.IsFirmware(c.CardDefId)).Select(c => c.CardDefId + ":" + c.State + ":" + (string.IsNullOrEmpty(c.ReservedByTransactionId) ? "-" : c.ReservedByTransactionId))
                    .OrderBy(x => x, StringComparer.Ordinal)))
                .Append(" unlocked=").Append(MechanicalContentUnlock.IsUnlocked(s, Napalm));
            return sb.ToString();
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = F.RunPython(F.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("FR\t") || l.StartsWith("FM\t")).ToArray();
            var rt = new List<string>();
            rt.AddRange(t.TbFusionRecipe.DataList.Select(r => string.Join("\t", "FR", r.Id, r.MixId, r.ParentA, r.ParentB, r.Family, r.Reaction, r.NameKey, r.SortOrder)));
            // 合并上限是浮点：按两位小数比（源数据 repr 与运行时单精度的十进制写法不同）。
            rt.AddRange(t.TbFusionMerge.DataList.Select(r => string.Join("\t", "FM", r.Field, r.Cap.ToString("0.00", CultureInfo.InvariantCulture))));
            src = src.Select(l => l.StartsWith("FM\t") ? string.Join("\t", "FM", l.Split('\t')[1],
                double.Parse(l.Split('\t')[2], CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture)) : l).ToArray();
            Expect(code == 0 && src.Length == rt.Count && src.SequenceEqual(rt) && t.TbFusionRecipe.DataList.Count >= 25 && t.TbFusionRecipe.DataList.Count <= 35,
                $"A1 fg.TbFusionRecipe（{t.TbFusionRecipe.DataList.Count} 条配方，FGR-RND-043 约 30 条）/ fg.TbFusionMerge（{t.TbFusionMerge.DataList.Count} 个读法字段的合并上限）与源数据 fgdata_fusion.py 逐字段一致" +
                (code == 0 ? (src.SequenceEqual(rt) ? string.Empty : $"\n源：{string.Join(" / ", src.Take(4))}\n表：{string.Join(" / ", rt.Take(4))}") : "：" + output));

            var bad = new List<string>();
            foreach (FusionRecipeDef r in FusionCatalog.Recipes)
            {
                bool parents = FirmwareKinds.TryGetParents(r.MixId, out string a, out string b) && r.Matches(a, b) && !FirmwareKinds.IsMixed(a) && !FirmwareKinds.IsMixed(b);
                bool core = FirmwareKinds.IsCore(r.MixId) == (FirmwareKinds.IsCore(r.ParentA) || FirmwareKinds.IsCore(r.ParentB));
                int la = FirmwareCatalog.TryGet(r.ParentA, out MechanicalContentDef da) ? da.Load : 0;
                int lb = FirmwareCatalog.TryGet(r.ParentB, out MechanicalContentDef db) ? db.Load : 0;
                bool load = FirmwareCatalog.TryGet(r.MixId, out MechanicalContentDef dm) && dm.Load == Math.Max(la, lb) + FusionCatalog.LoadBonus;
                List<(string Field, float Magnitude)> want = FusionCatalog.MergeFields(r.ParentA, r.ParentB);
                IReadOnlyList<(string Field, float Magnitude)> have = CarrierReadings.FieldsOf(r.MixId);
                bool fields = want.Count == have.Count && want.Zip(have, (x, y) => x.Field == y.Field && Math.Abs(x.Magnitude - y.Magnitude) < 1e-3f).All(v => v)
                              && have.All(f => !FusionCatalog.TryGetCap(f.Field, out float cap) || f.Magnitude <= cap + 1e-4f);
                var tags = new HashSet<string>(FirmwareKinds.TagsOf(r.ParentA).Concat(FirmwareKinds.TagsOf(r.ParentB)));
                bool tagOk = tags.SetEquals(FirmwareKinds.TagsOf(r.MixId));
                var cats = new[] { FirmwareKinds.CategoryOf(r.ParentA), FirmwareKinds.CategoryOf(r.ParentB) };
                bool catOk = cats.Contains(FirmwareKinds.CategoryOf(r.MixId)) && cats.Contains(FirmwareKinds.Category2Of(r.MixId))
                             && FusionService.CategoryKey(FirmwareKinds.CategoryOf(r.MixId)) == r.Family;
                bool name = GameText.TryGet(r.NameKey, GameLanguage.ZhCn, out string zh) && GameText.TryGet(r.NameKey, GameLanguage.En, out string en)
                            && !string.IsNullOrWhiteSpace(zh) && !string.IsNullOrWhiteSpace(en) && FirmwareKinds.DisplayName(r.MixId) == zh
                            && !zh.Contains(FirmwareKinds.DisplayName(r.ParentA) + FirmwareKinds.DisplayName(r.ParentB));
                bool reaction = NamedReactionCatalog.TryGet(r.Reaction, out GameConfig.fg.Reaction rx)
                                && ((HasTag(r.ParentA, rx.TagA) && HasTag(r.ParentB, rx.TagB)) || (HasTag(r.ParentA, rx.TagB) && HasTag(r.ParentB, rx.TagA)));
                bool acquire = FirmwareKinds.SourceOf(r.MixId) == "fusion" && !FirmwareKinds.IsEnemyProtocol(r.MixId) && FirmwareRestoreService.IsRestorable(r.MixId) == false
                               && CarrierReadings.AllCarriers.All(c => !string.IsNullOrEmpty(FirmwareKinds.Reading(r.MixId, c)));
                if (!(parents && core && load && fields && tagOk && catOk && name && reaction && acquire))
                {
                    bad.Add($"{r.Id}（父 {parents} 核心 {core} 负载 {load} 读法 {fields} 标签 {tagOk} 类别 {catOk} 名 {name} 反应 {reaction} 来源 {acquire}）");
                }
            }
            int coreMixes = FusionCatalog.Recipes.Count(r => FirmwareKinds.IsCore(r.MixId));
            Expect(bad.Count == 0 && FusionCatalog.Problems.Count == 0 && coreMixes >= 1 && FusionCatalog.Families.All(f => FusionCatalog.CountInFamily(f) > 0),
                $"A2 FGR-FW-050 混合固件（{FusionCatalog.Recipes.Count} 条）逐条：两条父固件都是正式固件；任一父固件是核心 ⇔ 它是核心（{coreMixes} 条核心混合固件）；负载 = 较大值 + {FusionCatalog.LoadBonus}；" +
                "读法字段 = 父固件按 fg.TbFusionMerge 加法叠加、受上限约束；标签 = 并集；两个类别取自父固件；名字来自配方表文本键（不拼名字）；配方的反应标签分在两条父固件上；来源“熔合”、不走数据复原；4 个类别都有配方" +
                (bad.Count > 0 ? "；问题：" + string.Join("；", bad) : string.Empty) + (FusionCatalog.Problems.Count > 0 ? "；表问题：" + string.Join("；", FusionCatalog.Problems) : string.Empty));

            Expect(FusionCatalog.SimTech == 5 && FusionCatalog.FormalTech == 30 && FusionCatalog.FormalSubstrate == 2 && FusionCatalog.BurnSubstrate == 3 && FusionCatalog.LoadBonus == 1
                   && GridContent.TryGetBuilding(FusionCatalog.TypeId, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 3 && g.FootprintH == 3 && g.Category == "manufacturing" && g.Placeable == 1,
                $"A3 FG05 第 10 节初值：模拟 {FusionCatalog.SimTech} 技术数据、正式 {FusionCatalog.FormalTech} 技术数据 + 芯片基板 ×{FusionCatalog.FormalSubstrate}、量产芯片基板 ×{FusionCatalog.BurnSubstrate}；" +
                "电路合成台 3×3、建造菜单“制造”页签（FG04 建筑表）");

            string[] keys =
            {
                "building.circuit_synth.name", "building.circuit_synth.desc", "fusion.panel.title", "fusion.panel.book_title", "fusion.panel.simulate", "fusion.panel.formal",
                "fusion.panel.result_hit", "fusion.panel.result_miss", "fusion.panel.remaining", "fusion.panel.remaining_done", "fusion.panel.clues_empty", "fusion.panel.book_empty",
                "fusion.reason.mixed", "fusion.reason.raw", "fusion.reason.same", "fusion.reason.tech", "fusion.reason.substrate", "fusion.reason.simulate_first", "fusion.reason.no_chip",
                "fusion.reason.queue_full", "fusion.reason.not_held", "fusion.feedback.rolled_back", "fusion.clue.partial", "fusion.clue.full", "fusion.confirm.body", "fusion.confirm.body2",
                "codex.research.fusion.title", "codex.research.fusion.body", "codex.research.recipe_book.title", "codex.research.recipe_book.body", "firmware.acquire.fusion",
                "firmware.mixed.parents", "firmware.mixed.categories", "prod.panel.burn_line_mixed", "signal.reason.print_mixed", "bs.reason.fusion_idle", "bs.reason.fusion_running",
                "input.action.open_recipe_book.name",
            };
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            string[] hooks = { GuidanceHooks.FusionFirstBuilt, GuidanceHooks.FusionFirstOpen, GuidanceHooks.FusionFirstSimulate, GuidanceHooks.FusionFirstDiscovery, GuidanceHooks.FusionFirstClue, GuidanceHooks.FusionFirstRecipeBook };
            bool hookOk = hooks.All(h => GuidanceHooks.Known.Contains(h))
                          && t.TbCodexEntry.DataList.Any(e => e.Id == "codex.research.recipe_book" && e.Hooks.Contains(GuidanceHooks.FusionFirstClue))
                          && t.TbCodexEntry.DataList.Any(e => e.Id == "codex.research.fusion");
            bool key = InputActionCatalog.TryGet(GameActionId.OpenRecipeBook, out InputActionDef def) && def.Status == InputActionStatus.Wired
                       && InputActionCatalog.DefaultChord(GameActionId.OpenRecipeBook).Key == KeyCode.F && (InputActionCatalog.DefaultChord(GameActionId.OpenRecipeBook).Mods & InputModifier.Alt) != 0;
            bool notify = new[] { "fusion_discovered", "fusion_clue", "fusion_done", "fusion_rolled_back" }
                .All(id => t.TbNotifyType.DataList.Any(n => n.Id == id && n.AwaySection == "research")); // B19：进离家报告“研究与解析”段
            bool icons = FusionCatalog.Recipes.All(r => File.Exists(Path.Combine(Application.dataPath, "GameRes/Raw/UI/Icons/icon_" + r.MixId + ".png")));
            Expect(missing.Count == 0 && hookOk && key && notify && icons,
                "A4 新文本中英两套（面板 / 原因 / 反馈 / 线索 / 图鉴 / 刻录台 / 信号核拒绝 / 建筑状态）；引导钩子 6 个登记（含“第一次拿到线索”，图鉴“配方书与线索”以它解锁）；" +
                "配方书快捷键 Alt+F（可重绑，动作登记表）；通知类型 4 种（都记进离家报告“研究与解析”段）；30 条混合固件都有占位图标（芯片外形 + 区分字 + 类别角标）" + (missing.Count > 0 ? "；缺文本：" + string.Join("、", missing) : string.Empty));
        }

        private static bool HasTag(string fw, string tag)
        {
            foreach (string t in FirmwareKinds.TagsOf(fw))
            {
                if (t == tag || (StatusTagCatalog.TryGet(t, out GameConfig.fg.StatusTag row) && row.AliasOf == tag))
                {
                    return true;
                }
            }
            return false;
        }

        // ── B 模拟熔合（FGT-RND-005 前半）──────────────────────────────────────────

        private static void CheckSimulate()
        {
            CampaignState s = NewWorld(9601);
            string before = string.Join(",", s.PrimitiveChips.Select(c => c.PartId + c.State + c.ReservedByTransactionId));
            int tech0 = s.TechData;
            FusionOpResult miss = FusionService.Simulate(s, _synthId, Coolant, Nitrogen);
            bool missOk = miss.Success && miss.RecipeId == null && miss.Text.Contains("无反应") && s.TechData == tech0 - 5
                          && string.Join(",", s.PrimitiveChips.Select(c => c.PartId + c.State + c.ReservedByTransactionId)) == before
                          && FusionService.SimResultOf(s, Nitrogen, Coolant)?.RecipeId == string.Empty;
            FusionOpResult hit = FusionService.Simulate(s, _synthId, Oil, Burn);
            bool hitOk = hit.Success && hit.RecipeId == NapalmRecipe && s.TechData == tech0 - 10 && Chips(s, Burn) == 2 && Chips(s, Oil) == 2
                         && string.Join(",", s.PrimitiveChips.Select(c => c.PartId + c.State + c.ReservedByTransactionId)) == before
                         && FusionService.IsPairKnown(s, Burn, Oil) && !FusionService.IsDiscovered(s, NapalmRecipe)
                         && FusionService.ClueOf(FusionService.StateOf(s), NapalmRecipe)?.SessionKind == FusionService.KindSim
                         && FusionService.PreviewText(FusionCatalog.Recipes.First(r => r.Id == NapalmRecipe)).Contains(FirmwareKinds.DisplayName(Napalm));
            Expect(missOk && hitOk,
                $"B1 FGT-RND-005 模拟熔合不消耗固件：冷却液 + 液氮 → “{miss.Text}”（技术数据 -5，固件库逐枚不变，记进模拟记录）；漏油 + 燃迹（不分先后）→ “{hit.Text}”，" +
                $"预览“{FusionService.PreviewText(FusionCatalog.Recipes.First(r => r.Id == NapalmRecipe)).Split('\n')[0]}”；固件仍是燃迹 2 / 漏油 2、没有预留；这一对记为“已确认有配方”，配方书未发现（第一次正式熔合成功才算）");

            // 负向矩阵：混合固件 / 未破解 / 同一条 / 不在固件库 / 技术数据不够 / 合成台不工作——都拒绝且不扣任何东西
            PrimitiveInventory.GrantCrafted(s, Napalm); // 测试捷径：让固件库里有一枚混合固件（正式来源见 C / E 段）
            string raw = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal).First(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.IsRaw(s, id));
            PrimitiveInventory.TryGrantEncryptedFirmware(s, "fgfusion-raw", raw, out _);
            int tech1 = s.TechData;
            FusionOpResult mixed = FusionService.Simulate(s, _synthId, Napalm, Coolant);
            FusionOpResult rawR = FusionService.Simulate(s, _synthId, raw, Coolant);
            FusionOpResult same = FusionService.Simulate(s, _synthId, Coolant, Coolant);
            FusionOpResult notHeld = FusionService.Simulate(s, _synthId, Coolant, "fw_pierce"); // 中立协议（不用破解），固件库里没有
            FusionOpResult none = FusionService.Simulate(s, _synthId, null, Coolant);
            s.TechData = 4;
            FusionOpResult poor = FusionService.Simulate(s, _synthId, Coolant, Nitrogen);
            s.TechData = tech1;
            BuildingOps.TrySetEnabled(s, _synthId, false, out _);
            HomeValleyPowerGrid.Recompute(s);
            FusionOpResult off = FusionService.Simulate(s, _synthId, Coolant, Nitrogen);
            BuildingOps.TrySetEnabled(s, _synthId, true, out _);
            HomeValleyPowerGrid.Recompute(s);
            bool unchanged = s.TechData == tech1 && Chips(s, Napalm) == 1 && FusionService.StateOf(s).Simulations == 2;
            Expect(!mixed.Success && mixed.Failure == FusionFailure.Mixed && mixed.Text.Contains("混合固件不能再次熔合")
                   && !rawR.Success && rawR.Failure == FusionFailure.Raw && !same.Success && same.Failure == FusionFailure.Same
                   && !notHeld.Success && notHeld.Failure == FusionFailure.NotHeld && !none.Success && none.Failure == FusionFailure.PickTwo
                   && !poor.Success && poor.Failure == FusionFailure.Tech && poor.Text.Contains("要 5") && !off.Success && off.Failure == FusionFailure.NotWorking && unchanged,
                $"B2 负向（FG05 第 5 节“选了混合固件”等）：混合固件“{mixed.Text}”；未破解“{rawR.Text}”；同一条“{same.Text}”；不在固件库“{notHeld.Text}”；没选全“{none.Text}”；" +
                $"技术数据不够“{poor.Text}”；合成台禁用“{off.Text}”——全部拒绝，技术数据与固件一样不扣");
        }

        // ── C 正式熔合（原子事务）─────────────────────────────────────────────────

        private static void CheckFormal()
        {
            CampaignState s = NewWorld(9602, lab: false); // 不放仿真实验室：它会持续消耗技术数据，守恒核对只看熔合
            int sub0 = Stock(s, Substrate);
            int subTotal0 = Totals(s).Sub;
            int tech0 = s.TechData;
            FusionOpResult unknown = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            bool refused = !unknown.Success && unknown.Failure == FusionFailure.NoRecipe && unknown.Text.Contains("先模拟熔合") && Stock(s, Substrate) == sub0 && s.TechData == tech0
                           && FreeChips(s, Burn) == 2;
            FusionService.Simulate(s, _synthId, Burn, Oil);
            int tech1 = s.TechData;
            FusionOpResult q = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            FusionJobRecord job = FusionService.FindJob(s, q.JobId);
            bool queued = q.Success && job != null && job.State == FusionJobState.Queued && FreeChips(s, Burn) == 1 && FreeChips(s, Oil) == 1 && Chips(s, Burn) == 2
                          && Stock(s, Substrate) <= sub0 - 2 && s.TechData == tech1 - 30 && job.Substrate == 2 && job.Tech == 30 && Totals(s).Sub == subTotal0
                          && s.PrimitiveChips.Count(c => c.ReservedByTransactionId == q.JobId) == 2;
            // 预留中的芯片不能被别处拿走（信号核 / 电路草稿 / 分解都拒绝）
            string reservedPart = job?.PartA;
            CircuitOpResult toCore = PrimitiveInventory.TryMoveToSignalCore(s, reservedPart);
            List<PrimitiveChipRecord> dis = PrimitiveInventory.RemoveForDisassembly(s, new[] { reservedPart });
            bool guarded = !toCore.Success && dis.Count == 0 && PrimitiveInventory.Find(s, reservedPart) != null;
            Seconds(10f);
            bool running = job.State == FusionJobState.Running && job.Progress > 5f && job.Progress < 15f;
            bool done = StepUntil(() => job.State == FusionJobState.Done, 40);
            PrimitiveChipRecord output = PrimitiveInventory.Find(s, job.OutputPartId);
            bool result = done && Chips(s, Burn) == 1 && Chips(s, Oil) == 1 && Chips(s, Napalm) == 1 && output != null && output.CardDefId == Napalm
                          && FusionService.IsDiscovered(s, NapalmRecipe) && MechanicalContentUnlock.IsUnlocked(s, Napalm) && FusionService.StateOf(s).Fused == 1
                          && Totals(s).Sub == subTotal0 - 2 && job.Substrate == 0 && job.Tech == 0 && output.Origin == PrimitiveInventory.OriginCraft
                          && FusionService.ClueOf(FusionService.StateOf(s), NapalmRecipe) != null && FusionService.ClueText(s, FusionService.ClueOf(FusionService.StateOf(s), NapalmRecipe)).Contains("已发现");
            Expect(refused && queued && guarded && running && done && result,
                $"C1 FGT-RND-005 正式熔合原子事务：没模拟过的一对被拒（“{unknown.Text}”，什么都不扣）；模拟后入队——两枚父固件被本任务预留、芯片基板 -2、技术数据 -30 记在任务上；" +
                $"预留中的芯片放不进信号核（“{toCore.Message}”）也不能分解；{FusionCatalog.FormalSeconds:0} 秒后完成：父固件各少一枚、产出“{FirmwareKinds.DisplayName(Napalm)}”进固件库（来源“电路合成台”）、" +
                $"配方记入配方书并写入解锁（运行 {running}，完成 {done}）" +
                (refused && queued && guarded && running && done && result ? string.Empty : $"\n诊断：拒 {refused} 入队 {queued} 守 {guarded} 结果 {result}；{Snapshot(s)}"));

            // 取消：排队中 / 熔合中都全额退回
            FusionService.Simulate(s, _synthId, Coolant, Nitrogen); // 无反应（确认这一对不能熔合）
            Unlock(s, "fw_acid");
            PrimitiveInventory.GrantCrafted(s, "fw_acid");
            PrimitiveInventory.GrantCrafted(s, Burn);
            PrimitiveInventory.GrantCrafted(s, Oil);
            FusionService.Simulate(s, _synthId, Burn, "fw_acid");
            var t0 = Totals(s);
            int acid0 = Chips(s, "fw_acid");
            FusionOpResult a = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            FusionOpResult b = FusionService.EnqueueFormal(s, _synthId, Burn, "fw_acid");
            Seconds(8f);
            FusionJobRecord ja = FusionService.FindJob(s, a.JobId);
            FusionJobRecord jb = FusionService.FindJob(s, b.JobId);
            bool headOnly = ja.State == FusionJobState.Running && jb.State == FusionJobState.Queued && jb.Progress == 0f;
            int techMid = s.TechData;
            FusionOpResult ca = FusionService.Cancel(s, a.JobId);
            FusionOpResult cb = FusionService.Cancel(s, b.JobId);
            FusionOpResult again = FusionService.Cancel(s, a.JobId);
            var t1 = Totals(s);
            bool refunded = ca.Success && cb.Success && !again.Success && again.Failure == FusionFailure.NotCancellable && t1 == t0 && s.TechData == techMid + 60
                            && Chips(s, "fw_acid") == acid0 && FreeChips(s, Burn) == Chips(s, Burn) && ja.State == FusionJobState.Cancelled && jb.State == FusionJobState.Cancelled
                            && s.PrimitiveChips.All(c => c.ReservedByTransactionId != a.JobId && c.ReservedByTransactionId != b.JobId) && ca.Text.Contains("全部退回");
            Expect(headOnly && refunded,
                $"C2 B03 取消：同一座合成台一次只做队首（第二项排队、进度 0）；熔合中 / 排队中取消“{ca.Text}”——两枚芯片回到固件库可用、芯片基板与技术数据全额退回（守恒 {t0} = {t1}）；已结束的不能再取消（“{again.Text}”）");

            // 队列上限与材料不够：整体不动
            int qmax = FusionCatalog.QueueMax;
            for (int i = 0; i < qmax + 1; i++)
            {
                PrimitiveInventory.GrantCrafted(s, Burn);
                PrimitiveInventory.GrantCrafted(s, Oil);
            }
            s.TechData = 10000;
            HomeInventory.Add(s, Substrate, 20);
            var ok = new List<FusionOpResult>();
            for (int i = 0; i < qmax; i++)
            {
                ok.Add(FusionService.EnqueueFormal(s, _synthId, Burn, Oil));
            }
            var tFull = Totals(s);
            FusionOpResult full = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            bool fullOk = ok.All(r => r.Success) && !full.Success && full.Failure == FusionFailure.QueueFull && Totals(s) == tFull && full.Text.Contains(qmax.ToString());
            foreach (FusionOpResult r in ok)
            {
                FusionService.Cancel(s, r.JobId);
            }
            HomeInventory.RemoveUpTo(s, ItemCatalog.Find(Substrate), Stock(s, Substrate) - 1);
            var tPoor = Totals(s);
            FusionOpResult pre = FusionService.PreflightFormal(s, _synthId, Burn, Oil); // 面板弹确认框之前的同一套判定
            FusionOpResult poor = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            bool poorOk = !poor.Success && poor.Failure == FusionFailure.Substrate && Totals(s) == tPoor && s.PrimitiveChips.All(c => string.IsNullOrEmpty(c.ReservedByTransactionId))
                          && !pre.Success && pre.Failure == FusionFailure.Substrate && pre.Text == poor.Text;
            Expect(fullOk && poorOk,
                $"C3 队列上限 {qmax}（第 {qmax + 1} 项“{full.Text}”）与芯片基板不够（“{poor.Text}”）：拒绝时芯片不预留、材料一件不动（全有或全无）");
        }

        // ── D 被毁 / 被拆回滚（卡片补充：必须自动化）──────────────────────────────────

        private static void CheckRollback()
        {
            CampaignState s = NewWorld(9603, lab: false);
            FusionService.Simulate(s, _synthId, Burn, Oil);
            PrimitiveInventory.GrantCrafted(s, Burn);
            PrimitiveInventory.GrantCrafted(s, Oil);
            var t0 = Totals(s);
            int napalm0 = Chips(s, Napalm);
            FusionOpResult a = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            FusionOpResult b = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            Seconds(FusionCatalog.FormalSeconds * 0.5f);
            var tMid = Totals(s);
            int notes0 = NotificationCenter.History.Count(n => n.Type.Id == "fusion_rolled_back");
            bool killed = BuildingOps.ApplyDamage(s, _synthId, BuildingOps.MaxDurability(FusionCatalog.TypeId) + 1f);
            WorldSimulation.StepMany(2);
            FusionJobRecord ja = FusionService.FindJob(s, a.JobId);
            FusionJobRecord jb = FusionService.FindJob(s, b.JobId);
            var t1 = Totals(s);
            bool rolled = killed && ja.State == FusionJobState.RolledBack && jb.State == FusionJobState.RolledBack && ja.Reason == "destroyed"
                          && t1 == t0 && tMid == t0 && Chips(s, Napalm) == napalm0 && FreeChips(s, Burn) == Chips(s, Burn) && FreeChips(s, Oil) == Chips(s, Oil)
                          && !FusionService.IsDiscovered(s, NapalmRecipe) && FusionService.StateOf(s).RolledBack == 2
                          && NotificationCenter.History.Count(n => n.Type.Id == "fusion_rolled_back") > notes0 && FusionService.LastFeedback.Contains("被毁");
            // 被毁后：不再接活；进度不再推进；重建后恢复
            FusionOpResult after = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            Seconds(5f);
            bool frozen = !after.Success && after.Failure == FusionFailure.NotWorking && Totals(s) == t0;
            Synth(s).ConstructionState = BuildingConstructionState.Operational;
            Synth(s).Health = BuildingOps.MaxDurability(FusionCatalog.TypeId);
            HomeValleyPowerGrid.Recompute(s);
            FusionOpResult rebuilt = FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            Expect(rolled && frozen && rebuilt.Success,
                $"D1 FG05 负向“正式熔合途中合成台被毁（突袭打断）”：两项在办熔合（一项熔合中、一项排队）走正式摧毁入口后全部回滚——父固件回到固件库可用、芯片基板与技术数据退回，" +
                $"守恒（父固件 / 基板 / 技术数据 {t0} → 熔合中 {tMid} → 回滚后 {t1}），没有凭空产出混合固件、配方不算发现；发出“熔合中止”通知与字幕“{FusionService.LastFeedback}”；" +
                $"被毁后拒绝接活（“{after.Text}”）、重建后恢复（回滚 {rolled} / 冻结 {frozen} / 重建 {rebuilt.Success}）" +
                (rolled && frozen && rebuilt.Success ? string.Empty : $"\n诊断：a={a.Success}/{a.Text} b={b.Success}/{b.Text} 毁={killed} 状态={Synth(s)?.ConstructionState}；{Snapshot(s)}"));

            // 被拆除（建筑记录不在了）+ 仓库放不下：芯片基板落在合成台旁（由机器搬回），总量守恒
            Seconds(3f);
            Vector2 pos = Synth(s).Position;
            ItemDef subDef = ItemCatalog.Find(Substrate);
            int cap = HomeInventory.Capacity(s, subDef);
            int fill = Math.Max(0, cap - Stock(s, Substrate));
            ItemDef filler = subDef;
            HomeInventory.Add(s, filler, fill);
            var t2 = Totals(s);
            s.BuildingRecords = s.BuildingRecords.Where(x => x.BuildingId != _synthId).ToArray(); // 测试捷径：拆除完成（拆除工单由 FG3-LOG-02 覆盖）
            HomeGridService.Invalidate();
            WorldSimulation.StepMany(2);
            FusionJobRecord jr = FusionService.FindJob(s, rebuilt.JobId);
            var t3 = Totals(s);
            bool groundOk = jr.State == FusionJobState.RolledBack && jr.Reason == "removed" && t3 == t2 && Ground(s, Substrate) >= 2
                            && (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.ResourceType == subDef.ResourceType && Vector2.Distance(g.Position, pos) < 6f);
            Expect(groundOk,
                $"D2 合成台被拆除时同样回滚；仓库已满（芯片基板 {Stock(s, Substrate)}/{cap}）放不下的芯片基板落在合成台旁（地面 {Ground(s, Substrate)} 件，由机器搬回）——总量守恒 {t2} = {t3}" +
                (groundOk ? string.Empty : $"\n诊断：任务 {jr?.State}/{jr?.Reason}；{Snapshot(s)}"));

            // 完成时芯片不在：回滚、不凭空产出（与 Demo 合成台“两件材料中一个消失”同一条）
            CampaignState s2 = NewWorld(9604, lab: false);
            FusionOpResult c = SimThenFuse(s2);
            FusionJobRecord jc = FusionService.FindJob(s2, c.JobId);
            PrimitiveChipRecord chip = PrimitiveInventory.Find(s2, jc.PartA);
            chip.ReservedByTransactionId = null; // 模拟预留被外部抹掉（旧档 / 调试改档）
            PrimitiveInventory.ConsumeReservedMaterial(s2, chip.PartId, null);
            s2.PrimitiveChips = s2.PrimitiveChips.Where(x => x.PartId != chip.PartId).ToArray();
            var t4 = Totals(s2);
            StepUntil(() => !FusionService.IsActive(jc), 40);
            Expect(jc.State == FusionJobState.RolledBack && jc.Reason == "chip_missing" && Chips(s2, Napalm) == 0 && !FusionService.IsDiscovered(s2, NapalmRecipe)
                   && FreeChips(s2, Oil) == Chips(s2, Oil) && Totals(s2) == t4,
                "D3 到时发现父固件芯片已不在：这一项回滚（另一枚回到固件库、材料退回），不凭空产出混合固件、配方不算发现" +
                $"（{jc.State}/{jc.Reason}；{Snapshot(s2)}；守恒 {t4} → {Totals(s2)}）");
        }

        // ── E 发现后量产（FGT-RND-006）──────────────────────────────────────────────

        private static BuildingRecord LayBurner(CampaignState s, out GridCell feedCell)
        {
            feedCell = default;
            GridCell? o = F.FindArea(s, 12, 12, 7f, 22f);
            if (o == null)
            {
                return null;
            }
            BuildingRecord b = F.Built(s, "firmware_burner", "fu_burner", F.At(o.Value, 5, 5));
            F.Resync(s);
            BeltPortService.Binding bind = BeltPortService.Find(b.BuildingId, "firmware_burner.in0");
            if (bind == null)
            {
                return null;
            }
            Vector2Int v = GridMath.DirVector(bind.Face);
            var dir = (BeltDir)(((int)bind.Face + 2) & 3);
            for (int i = 0; i < 3; i++)
            {
                BeltNetworkService.TryPlace(s, new GridCell(bind.BeltCell.X + v.x * i, bind.BeltCell.Y + v.y * i), dir, 2);
            }
            feedCell = new GridCell(bind.BeltCell.X + v.x * 2, bind.BeltCell.Y + v.y * 2);
            F.Resync(s);
            return b;
        }

        private static void CheckMassBurn()
        {
            CampaignState s = NewWorld(9605);
            BuildingRecord burner = LayBurner(s, out GridCell feed);
            bool lockedBefore = burner != null && !ProductionService.IsBurnable(s, Napalm) && !ProductionService.TrySetBurnTarget(s, burner.BuildingId, Napalm, out string lm)
                                && !SignalCoreService.PrintableFirmware(s, includeMixed: true).Contains(Napalm);
            FusionOpResult f = SimThenFuse(s);
            StepUntil(() => FusionService.FindJob(s, f.JobId).State == FusionJobState.Done, 40);
            int burn0 = Chips(s, Burn);
            int oil0 = Chips(s, Oil);
            int napalm0 = Chips(s, Napalm);
            bool listed = SignalCoreService.PrintableFirmware(s, includeMixed: true).Contains(Napalm) && !SignalCoreService.PrintableFirmware(s).Contains(Napalm);
            bool set = ProductionService.TrySetBurnTarget(s, burner.BuildingId, Napalm, out string sm);
            int src = F.TestSource(s, feed, Substrate, 7);
            ProductionService.Producer p = F.P(s, burner);
            bool made = StepUntil(() => Chips(s, Napalm) >= napalm0 + 2, 120);
            Seconds(30f);
            int consumed = BeltNetworkService.Kernel.TryGetPortInfo(src, out BeltPortInfo pi) ? (int)pi.Total : 0;
            int napalmMade = Chips(s, Napalm) - napalm0;
            int left = ProductionService.Count(p.Rec.In, Substrate);
            bool cost = napalmMade == 2 && consumed == 7 && left == 1 && Chips(s, Burn) == burn0 && Chips(s, Oil) == oil0
                        && p.State != ProdState.Working && ProductionService.ReasonText(s, p).Length > 0;
            // 信号核不刻混合固件（不能绕开刻录台成本）
            SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, Napalm);
            Expect(lockedBefore && listed && set && made && cost && !print.Success && print.Message.Contains("固件刻录台"),
                $"E FGT-RND-006 发现后量产：未发现前刻录台拒绝这条目标、目标清单里没有；第一次正式熔合成功后目标清单出现“{FirmwareKinds.DisplayName(Napalm)}”（“{sm}”）；" +
                $"送 7 块芯片基板 → 刻出 {napalmMade} 枚（每枚 3 块，余 {left} 块在缓存等料：“{ProductionService.ReasonText(s, p)}”），父固件一枚都不消耗；信号核刻印被拒（“{print.Message}”）");
        }

        // ── F 混合固件读法（FGR-FW-050）────────────────────────────────────────────

        private static void CheckMixedReadings()
        {
            CampaignState s = NewWorld(9606);
            Unlock(s, FusionCatalog.Recipes.Select(r => r.MixId).ToArray());
            Unlock(s, FirmwareCatalog.BaseIds.ToArray());
            // 读法：内核读法参数 = 合并后的字段（上限）+ 标签并集；与两条父固件分别装的差别只在被上限截掉的部分
            CombatReading mix = CarrierReadings.Build(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, new[] { Napalm }, false);
            CombatReading both = CarrierReadings.Build(ComponentCatalog.CompGunId, HomeValleyLayout.Erc003ChassisId, new[] { Burn, Oil }, false);
            bool sameReading = mix.StatusMask == both.StatusMask && mix.StatusMask != 0 && Math.Abs(mix.StatusDps - both.StatusDps) < 1e-4f;
            FusionRecipeDef capped = FusionCatalog.Recipes.FirstOrDefault(r =>
                CarrierReadings.FieldsOf(r.ParentA).Any(a => CarrierReadings.FieldsOf(r.ParentB).Any(b => b.Field == a.Field && FusionCatalog.TryGetCap(a.Field, out float c) && a.Magnitude + b.Magnitude > c)));
            bool capOk = capped == null || CarrierReadings.FieldsOf(capped.MixId).All(x => !FusionCatalog.TryGetCap(x.Field, out float c) || x.Magnitude <= c + 1e-4f);
            // 装配：常规混合固件装进机器电路、负载 = 较大值 + 1、积热 = 两者之和、编译出旧引擎等价模块
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult setMix = board.TrySetFirmware(s, 0, Napalm);
            BlueprintCircuitPreview prev = BlueprintCircuitCompiler.CompilePreview(board);
            bool fitted = setMix.Success && prev.FirmwareIds.Contains(Napalm) && board.Validate().IsValid
                          && Math.Abs(BlueprintCircuitBoard.ComputeFirmwareHeat(ComponentCatalog.CompGunId, new[] { Napalm }) - (FirmwareKinds.HeatOf(Burn) + FirmwareKinds.HeatOf(Oil))) < 1e-4f
                          && Math.Abs(FirmwareKinds.HeatOf(Napalm) - (FirmwareKinds.HeatOf(Burn) + FirmwareKinds.HeatOf(Oil))) < 1e-4f;
            // 核心混合固件（父固件含过载）：装不进机器电路，只能进信号核；接入时按父固件识别熔穿过载（重炮 + 过载）
            FusionRecipeDef coreMix = FusionCatalog.Recipes.First(r => FirmwareKinds.IsCore(r.MixId) && (r.ParentA == FirmwareCatalog.FwOverloadId || r.ParentB == FirmwareCatalog.FwOverloadId));
            FirmwareCatalog.TryGet(coreMix.MixId, out MechanicalContentDef cdef);
            bool coreRules = !FirmwareKinds.CanInstall(s, coreMix.MixId, FirmwareHost.MachineCircuit, out _) && FirmwareKinds.CanInstall(s, coreMix.MixId, FirmwareHost.SignalCore, out _)
                             && cdef != null && cdef.AiPermission == MechanicalContentAiPermission.PlayerOnly;
            string melt = BlueprintCircuitCompiler.DetectReactionId(ComponentCatalog.CompCannonId, null, new[] { coreMix.MixId });
            bool gated = SignalUplinkService.IsCoreGatedReaction(melt, new[] { coreMix.MixId });
            float heatMelt = BlueprintCircuitBoard.ComputeHeatBudget(ComponentCatalog.CompCannonId, new[] { coreMix.MixId });
            float heatParents = BlueprintCircuitBoard.ComputeHeatBudget(ComponentCatalog.CompCannonId, new[] { coreMix.ParentA, coreMix.ParentB });
            Expect(sameReading && capOk && fitted && coreRules && melt == MechanicalReactionCatalog.ReactionMeltOverloadId && gated && Math.Abs(heatMelt - heatParents) < 1e-4f,
                $"F FGR-FW-050 同时具有两者的读法：“{FirmwareKinds.DisplayName(Napalm)}”的内核状态标签 / 持续伤害与同时装燃迹 + 漏油一致；读法字段相加受上限（{capped?.Id ?? "无"} 被截）；" +
                $"常规混合固件经正式入口装进机器电路、编译生效，积热 = 两者之和；核心混合固件“{cdef?.DisplayName}”装不进机器电路、只能进信号核、AI 不用；" +
                $"装配反应按父固件识别（重炮 + 它 → {melt}，受过载的冷却门控 {gated}），热量预算与分别装两条父固件相同（{heatMelt:0.#}）");
        }

        // ── G 线索（FGR-RND-044）────────────────────────────────────────────────────

        private static void Raid(CampaignState s, string key, string reaction, int count)
        {
            ReactionAttribution.BeginRaid(s, key);
            ReactionAttribution.AddReaction(s, HomeValleyLayout.RegionId, reaction, count, count * 4.0);
            ReactionAttribution.EndRaid(s);
        }

        private static void CheckClues()
        {
            CampaignState s = NewWorld(9607);
            FusionState f = FusionService.StateOf(s);
            int touch0 = GuidanceHooks.TouchCount;
            Raid(s, "fu-r1", "reaction_deflagrate", FusionCatalog.CluePartialMin - 1);
            bool below = f.Clues.Length == 0;
            Raid(s, "fu-r2", "reaction_deflagrate", FusionCatalog.CluePartialMin);
            FusionClueRecord partial = FusionService.ClueOf(f, NapalmRecipe);
            string partialText = FusionService.ClueText(s, partial);
            bool partialOk = partial != null && !partial.Full && partial.KnownParent == Burn && partialText.Contains("燃烧") && partialText.Contains("油污")
                             && partialText.Contains(FirmwareKinds.DisplayName(Burn)) && !partialText.Contains(FirmwareKinds.DisplayName(Oil)) && partialText.Contains("突袭")
                             && f.FirstClueSeen && GameSettings.HasSeenGuidanceHook(GuidanceHooks.FusionFirstClue) && MechanicCodex.IsUnlocked("codex.research.recipe_book")
                             && NotificationCenter.History.Any(n => n.Type.Id == "fusion_clue");
            Raid(s, "fu-r3", "reaction_deflagrate", FusionCatalog.ClueFullMin);
            FusionClueRecord full = FusionService.ClueOf(f, NapalmRecipe);
            string fullText = FusionService.ClueText(s, full);
            bool upgraded = full != null && full.Full && full.Count == FusionCatalog.ClueFullMin && f.Clues.Count(c => c.RecipeId == NapalmRecipe) == 1
                            && fullText.Contains($"同时出现 {FusionCatalog.ClueFullMin} 次") && fullText.Contains(FirmwareKinds.DisplayName(Burn) + " + " + FirmwareKinds.DisplayName(Oil));
            Raid(s, "fu-r4", "reaction_deflagrate", FusionCatalog.ClueFullMin);
            FusionClueRecord second = f.Clues.FirstOrDefault(c => c.RecipeId == "fusion.flashover");
            Expect(below && partialOk && upgraded && second != null && second.Full,
                $"G1 线索：一场突袭里爆燃（燃烧 + 油污）{FusionCatalog.CluePartialMin - 1} 次不给线索；{FusionCatalog.CluePartialMin} 次 → 部分线索“{partialText}”（只知道一条父固件）；" +
                $"{FusionCatalog.ClueFullMin} 次 → 升级成完整线索“{fullText}”（同一条配方只留一条）；再来一场 → 指向同一反应的下一条配方（闪燃过载）；第一次拿到线索发引导钩子（图鉴“配方书与线索”解锁）、发“新的熔合线索”通知");

            // 同一场只分析一次；结束时一次性生成（不在战斗中实时统计）
            int n0 = f.Clues.Length;
            ReactionSessionRecord last = s.Stats.ReactionSessions.Last();
            FusionService.OnSessionClosed(s, last);
            bool once = f.Clues.Length == n0 && f.AnalyzedSessions.Contains(last.SessionId);
            // 没有仿真实验室：战斗记录先存着，建好实验室后在世界步里分析
            CampaignState s2 = NewWorld(9608, lab: false);
            FusionState f2 = FusionService.StateOf(s2);
            Raid(s2, "fu-p1", "reaction_conduct", FusionCatalog.ClueFullMin);
            bool pending = f2.Clues.Length == 0 && f2.Pending.Length == 1;
            GridCell? lc = F.FindFree(s2, ResearchCatalog.LabTypeId, 6f, 30f);
            if (lc.HasValue)
            {
                F.Built(s2, ResearchCatalog.LabTypeId, "lab-late", lc.Value);
            }
            WorldSimulation.StepMany(2);
            bool analysed = f2.Pending.Length == 0 && f2.Clues.Length == 1 && f2.Clues[0].Full && FusionCatalog.TryGet(f2.Clues[0].RecipeId, out FusionRecipeDef cr) && cr.Reaction == "reaction_conduct";
            // 上限：最多 fusion.clue_per_session 条
            CampaignState s3 = NewWorld(9609);
            ReactionAttribution.BeginRaid(s3, "fu-many");
            foreach (string rid in new[] { "reaction_deflagrate", "reaction_conduct", "reaction_steam", "reaction_thermalshock", "reaction_dissolve" })
            {
                ReactionAttribution.AddReaction(s3, HomeValleyLayout.RegionId, rid, FusionCatalog.ClueFullMin + 1, 1);
            }
            ReactionAttribution.EndRaid(s3);
            bool capped = FusionService.StateOf(s3).Clues.Length == FusionCatalog.CluePerSession;
            Expect(once && pending && analysed && capped,
                $"G2 同一场只分析一次；没有仿真实验室时这场战斗记录先存着（面板写“有 1 场战斗记录等仿真实验室分析”），建好实验室后在世界步里分析出线索；一场最多 {FusionCatalog.CluePerSession} 条线索");
        }

        // ── H 真实战斗（FGT-RND-007）────────────────────────────────────────────────

        private static void CheckBattleClue()
        {
            // 破碎都市：两台机器分别带燃迹（燃烧）与漏油（油污）编队攻击侦察机，战斗内核真实打出爆燃；撤离（关场次）→ 仿真实验室分析 → 线索。
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            HomeValleyPowerGrid.ResetForTests();
            ResearchService.ResetForTests();
            FusionService.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgfusion-battle", "Standard", 9610);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            Unlock(s, FirmwareCatalog.BaseIds.ToArray());
            AddBlueprint(s, "bp_fgfusion_burn", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { Burn }));
            AddBlueprint(s, "bp_fgfusion_oil", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { Oil }));
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            GridCell? lc = F.FindFree(s, ResearchCatalog.LabTypeId, 6f, 30f);
            if (lc.HasValue)
            {
                F.Built(s, ResearchCatalog.LabTypeId, "lab-battle", lc.Value);
            }
            int a = SpawnMachine(FracturedCityLayout.RegionId, "bp_fgfusion_burn", FracturedCityLayout.Scout2Spawn.Position + new Vector2(-3f, -3f));
            int b = SpawnMachine(FracturedCityLayout.RegionId, "bp_fgfusion_oil", FracturedCityLayout.Scout2Spawn.Position + new Vector2(2f, -3f));
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FracturedCityRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = 1;
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { a, b }, resume: false);
            WorldView.Observe(home.SiteId);
            city.SquadCommands.DebugSelectMany(new[] { a, b });
            city.SquadCommands.IssueAttack(FracturedCityLayout.Scout2SpawnId, paused: false);
            city.SquadCommands.ClearSelection();
            int ri = RuleIndex("reaction_deflagrate");
            CombatSite site = CombatSites.Get(FracturedCityLayout.RegionId);
            int c0 = site?.Kernel.ReactionCountOf(ri) ?? 0;
            for (int i = 0; i < 90 && (site?.Kernel.ReactionCountOf(ri) ?? 0) - c0 < FusionCatalog.ClueFullMin; i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz / 2);
            }
            int kernelCount = (site?.Kernel.ReactionCountOf(ri) ?? 0) - c0;
            ReactionSessionRecord sess = ReactionAttribution.Current(s, FracturedCityLayout.RegionId, create: false);
            int sessCount = sess?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_deflagrate")?.Count ?? 0;
            ReactionAttribution.CloseExpedition(s, FracturedCityLayout.RegionId); // 撤离（ExpeditionReturnService 同一入口）
            FusionClueRecord clue = FusionService.ClueOf(FusionService.StateOf(s), NapalmRecipe);
            string text = FusionService.ClueText(s, clue);
            bool fullExpected = sessCount >= FusionCatalog.ClueFullMin;
            Expect(kernelCount >= FusionCatalog.CluePartialMin && sessCount == kernelCount && clue != null && clue.Count == sessCount && clue.Full == fullExpected
                   && clue.SessionKind == ReactionAttribution.KindExpedition && text.Contains("燃烧") && text.Contains("油污") && text.Contains($"{sessCount} 次")
                   && text.Contains(FirmwareKinds.DisplayName(Burn)) && text.Contains("远征"),
                $"H FGT-RND-007：破碎都市里燃迹机 + 漏油机编队攻击侦察机，战斗内核真实打出爆燃 {kernelCount} 次（场次记 {sessCount} 次）；撤离关场次后仿真实验室分析，生成线索“{text}”" +
                $"（{(clue?.Full ?? false ? "完整" : "部分")}线索）");
        }

        private static int RuleIndex(string reactionId)
        {
            for (int i = 0; i < NamedReactionCatalog.TagRules.Count; i++)
            {
                if (NamedReactionCatalog.TagRules[i].Id == reactionId)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int SpawnMachine(string region, string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, 200f, 200f, "Player", 1);
            if (!r.Success)
            {
                Fail($"登记测试机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        // ── I 配方书（FGR-RND-045）──────────────────────────────────────────────────

        private static void CheckRecipeBook()
        {
            CampaignState s = NewWorld(9611);
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            bool linked = FusionCatalog.Families.All(fam => FusionService.Remaining(s, fam) == t.TbFusionRecipe.DataList.Count(r => r.Family == fam))
                          && FusionService.Remaining(s, null) == t.TbFusionRecipe.DataList.Count;
            int fluid0 = FusionService.Remaining(s, "fluid");
            FusionOpResult f = SimThenFuse(s);
            StepUntil(() => FusionService.FindJob(s, f.JobId).State == FusionJobState.Done, 40);
            int fluid1 = FusionService.Remaining(s, "fluid");
            string text = FusionService.RemainingText(s, "fluid");
            List<FusionRecipeDef> book = FusionService.DiscoveredRecipes(s, "fluid");
            bool filtered = book.Count == 1 && book[0].Id == NapalmRecipe && FusionService.DiscoveredRecipes(s, "em").Count == 0
                            && FusionService.RecipeLine(book[0]).Contains(FirmwareKinds.DisplayName(Burn));
            // 线索新旧排序
            Raid(s, "fu-b1", "reaction_conduct", FusionCatalog.ClueFullMin);
            Raid(s, "fu-b2", "reaction_thermalshock", FusionCatalog.ClueFullMin);
            List<FusionClueRecord> newest = FusionService.Clues(s, null, newestFirst: true);
            List<FusionClueRecord> oldest = FusionService.Clues(s, null, newestFirst: false);
            bool sorted = newest.Count >= 2 && newest.First().Serial > newest.Last().Serial && oldest.First().Serial < oldest.Last().Serial
                          && FusionService.Clues(s, "em", true).All(c => FusionCatalog.Recipes.First(r => r.Id == c.RecipeId).Family == "em");
            bool isNew = newest.All(c => FusionService.IsNewClue(s, c));
            FusionService.MarkCluesSeen(s);
            bool seen = newest.All(c => !FusionService.IsNewClue(s, c));
            Expect(linked && fluid1 == fluid0 - 1 && text == $"流体类熔合还有 {fluid1} 个未发现" && filtered && sorted && isNew && seen,
                $"I FGR-RND-045 配方书：每个类别“还剩几个未发现”= 配方表这一类的条数 − 已发现（与表 fg.TbFusionRecipe 逐类对账，不写死数字）；发现燃油喷流后“{text}”；" +
                "按类别筛选已发现的配方；线索可按新旧排序、按类别筛选；新线索打开配方书前标“新”、看过后不再标");
        }

        // ── J 存读档 ────────────────────────────────────────────────────────────

        private static void LayScenario(CampaignState s)
        {
            FusionService.Simulate(s, _synthId, Coolant, Nitrogen);
            FusionService.Simulate(s, _synthId, Burn, Oil);
            PrimitiveInventory.GrantCrafted(s, Burn);
            PrimitiveInventory.GrantCrafted(s, Oil);
            FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            Raid(s, "fu-s1", "reaction_conduct", FusionCatalog.CluePartialMin);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9612);
            LayScenario(s);
            Seconds(FusionCatalog.FormalSeconds * 1.4f);
            string before = Snapshot(s);
            var totals = Totals(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(40f);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded, out bool destroyedOk)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                ResearchService.ResetForTests();
                FusionService.ResetSessionState();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                destroyedOk = false;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                Seconds(40f);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1, out _);
            string second = RunFromSave(out _, out _);
            // 读档后合成台被毁：读回来的在办熔合照样回滚、守恒
            CampaignState r = CampaignSession.Current;
            RunFromSave(out _, out _);
            r = CampaignSession.Current;
            var tl = Totals(r);
            BuildingOps.ApplyDamage(r, _synthId, BuildingOps.MaxDurability(FusionCatalog.TypeId) + 1f);
            WorldSimulation.StepMany(2);
            bool conserved = Totals(r) == tl && FusionService.ActiveCount(r, null) == 0;
            Expect(save.Success && loaded1 == before && before.Contains("|Running|") && before.Contains("|Done|") && before.Contains("clues=") && before.Contains("sim="),
                "J1 真文件存读档：配方书、线索、模拟记录、正式熔合队列（进度 / 预留的芯片 / 记在任务上的材料）、统计、序号写进存档，读档后逐字段一致" + (loaded1 == before ? string.Empty : $"\n存前：{before}\n读后：{loaded1}"));
            Expect(first == continuous && second == continuous && conserved,
                "J2 读档后接着跑 40 游戏秒与不存档连续跑逐字段一致（两次读档同一结果）；读档后合成台被毁，读回来的在办熔合同样回滚、总量守恒" + (first == continuous ? string.Empty : $"\n连续：{continuous}\n读档：{first}")
                + (second == continuous ? string.Empty : $"\n第二次：{second}") + $"（守恒 {conserved}，{tl} / {totals}）");
        }

        // ── K / L 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe);
            pausedHeld = true;
            LayScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
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
            long target = GameClock.Ticks + GameClock.StepHz * 70;
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
                string snap = RunScenario(9701, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("|Done|"),
                "K 暂停中（120 帧）熔合进度不动；0.5x / 1x / 2x / 3x 跑同样的 70 游戏秒（两项正式熔合、线索）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9702, true, 1f, false, out _);
            string unseen = RunScenario(9702, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("|Done|"), "L 同一组熔合在观察与不观察家园时跑 70 游戏秒逐字段一致（FGR-BASE-021：远征时家园照常熔合）"
                                                            + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── M 面板（真 UXML）──────────────────────────────────────────────────────

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

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9613);
            VisualElement root = F.MountUxml(PanelUxml, out GameObject go);
            FusionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<FusionPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplatesForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(QueueRowUxml), AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ClueRowUxml));
                FusionPanelUIToolkit.Open(_synthId);
                bool open = FusionPanelUIToolkit.IsOpen && panel.PanelVisible && panel.TitleText.Contains("电路合成台") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.FusionFirstOpen)
                            && panel.QueueEmptyText.Length > 0 && panel.CluesEmptyText.Length > 0 && panel.BookText == GameText.Get("fusion.panel.book_empty")
                            && panel.RemainingText.Contains("流体类熔合还有") && panel.ParentChoices.Count == 4 && panel.FooterText.Contains("Alt");
                Expect(open, $"M1 电路合成台面板（真 UXML）：标题“{panel.TitleText}”；固件下拉 {panel.ParentChoices.Count} 条；队列 / 线索 / 配方书空状态都有提示；“{panel.RemainingText.Split('\n')[0]}”；页脚写配方书快捷键");

                // 选了混合固件模拟：拒绝并写明原因（负向，经面板）
                PrimitiveInventory.GrantCrafted(s, Napalm);
                panel.Refresh(force: true);
                panel.SelectParentById(true, Napalm);
                panel.SelectParentById(false, Coolant);
                int tech0 = s.TechData;
                Click(panel.SimulateButton);
                string mixedMsg = panel.MessageText;
                bool mixedRefused = mixedMsg.Contains("混合固件不能再次熔合") && s.TechData == tech0;
                // 模拟 → 有配方 → 正式熔合（确认框）→ 队列行 → 取消
                panel.SelectParentById(true, Burn);
                panel.SelectParentById(false, Oil);
                bool formalLocked = !panel.FormalButton.enabledSelf;
                Click(panel.SimulateButton);
                bool hit = panel.ResultText.Contains("有配方") && panel.ResultText.Contains(FirmwareKinds.DisplayName(Napalm)) && panel.ResultText.Contains("芯片基板") && s.TechData == tech0 - 5;
                Click(panel.FormalButton);
                bool asked = UiConfirmDialog.Current != null && UiConfirmDialog.Current.Lines.Any(l => l.Contains(FirmwareKinds.DisplayName(Napalm)));
                UiConfirmDialog.Confirm();
                panel.Refresh(force: true);
                bool queued = panel.QueueRowCount == 1 && panel.QueueRowText(0).Contains(FirmwareKinds.DisplayName(Napalm)) && FusionService.ActiveCount(s, _synthId) == 1;
                int subTotal = Totals(s).Sub;
                int stock = Stock(s, Substrate);
                Click(panel.QueueCancelButton(0));
                panel.Refresh(force: true);
                bool cancelled = FusionService.ActiveCount(s, _synthId) == 0 && Totals(s).Sub == subTotal && Stock(s, Substrate) >= stock && panel.MessageText.Contains("全部退回");
                Expect(mixedRefused && formalLocked && hit && asked && queued && cancelled,
                    $"M2 面板操作：选混合固件点模拟被拒（“{mixedMsg}”）；未模拟时“正式熔合”不可点；模拟后结果写配方、预览与成本；正式熔合先弹确认框（B04），确认后队列出现一行；" +
                    "行内“取消”全部退回");

                // 配方书 / 线索 / 按线索选固件
                Raid(s, "fu-panel", "reaction_conduct", FusionCatalog.ClueFullMin);
                FusionPanelUIToolkit.Close();
                FusionPanelUIToolkit.ToggleBook();
                PrimitiveInventory.GrantCrafted(s, "fw_arcchain");
                Unlock(s, "fw_arcchain");
                panel.Refresh(force: true);
                FusionClueRecord clue = FusionService.StateOf(s).Clues.OrderByDescending(c => c.Serial).First(); // 最新的一条 = 这场突袭给的
                bool book = FusionPanelUIToolkit.IsOpen && panel.ClueRowCount == FusionService.StateOf(s).Clues.Length && panel.ClueRowText(0).StartsWith("（新）")
                            && panel.ClueRowText(0).Contains("同时出现") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.FusionFirstRecipeBook);
                FusionCatalog.TryGet(clue.RecipeId, out FusionRecipeDef cr);
                Click(panel.ClueUseButton(0));
                bool picked = panel.SelectedA == cr.ParentA && panel.SelectedB == cr.ParentB;
                panel.SelectFamily(1 + Array.IndexOf(FusionCatalog.Families, "em"));
                panel.Refresh(force: true);
                bool filtered = panel.RemainingText.StartsWith("电磁类熔合") && !panel.RemainingText.Contains("流体");
                Expect(book && picked && filtered && panel.MessageText.Contains(FirmwareKinds.DisplayName(cr.ParentA)),
                    $"M3 配方书快捷键打开（引导钩子）：新线索行“{panel.ClueRowText(0)}”；“按这条线索选固件”把两条父固件选好；类别筛选后只写电磁类（“{panel.RemainingText}”）");

                string probe = UiToolkitLayoutProbe.Probe(PanelUxml, "FusionRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("FusionRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "M4 面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                FusionPanelUIToolkit.Close();
                FusionPanelUIToolkit.InWorldOverrideForTests = false;
                UiConfirmDialog.DiscardAll();
                Object.DestroyImmediate(go);
            }
        }

        // ── N 建筑面板入口与状态 ───────────────────────────────────────────────────

        private static void CheckBuildingPanel()
        {
            CampaignState s = NewWorld(9614);
            BuildingStatus idle = BuildingStatusService.Evaluate(s, Synth(s));
            FusionOpResult f = SimThenFuse(s);
            Seconds(3f);
            BuildingStatus work = BuildingStatusService.Evaluate(s, Synth(s));
            VisualElement root = F.MountUxml(ProdUxml, out GameObject go);
            try
            {
                var panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(_synthId);
                bool shown = panel.FusionButton != null && !panel.FusionButton.ClassListContains("bn-hidden") && panel.FusionButton.text == GameText.Get("fusion.panel.open")
                             && panel.RangeButton.ClassListContains("bn-hidden");
                Expect(idle.Kind == BuildingStatusKind.Idle && idle.Reason == GameText.Get("bs.reason.fusion_idle") && f.Success
                       && work.Kind == BuildingStatusKind.Working && work.Reason.Contains(FirmwareKinds.DisplayName(Napalm)) && shown,
                    $"N 建筑状态（B05）：空闲“{idle.Reason}”、熔合中“{work.Reason}”；点电路合成台打开的建筑面板有“{panel.FusionButton?.text}”入口（其它建筑不显示）");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }
        }

        // ── O 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9615, tech: 100000, substrate: 200);
            FusionService.Simulate(s, _synthId, Burn, Oil);
            int qmax = FusionCatalog.QueueMax;
            for (int i = 0; i < qmax; i++)
            {
                PrimitiveInventory.GrantCrafted(s, Burn);
                PrimitiveInventory.GrantCrafted(s, Oil);
                FusionService.EnqueueFormal(s, _synthId, Burn, Oil);
            }
            for (int i = 0; i < 200; i++)
            {
                FusionService.Tick(s, 0f);
            }
            var sw = Stopwatch.StartNew();
            const int steps = 20000;
            for (int i = 0; i < steps; i++)
            {
                FusionService.Tick(s, 0f); // dt = 0：只量每步判定开销（队列满 + 合成台查找），不推进
            }
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            // 线索分析：一场 18 种反应（全部具名反应）
            var rec = new ReactionSessionRecord
            {
                SessionId = "perf", Kind = ReactionAttribution.KindRaid, SiteId = HomeValleyLayout.RegionId, EndTick = 1,
                Reactions = NamedReactionCatalog.TagRules.Select(r => new ReactionShareRecord { ReactionId = r.Id, Count = 40 }).ToArray(),
            };
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                rec.SessionId = "perf" + i;
                FusionService.OnSessionClosed(s, rec);
            }
            sw2.Stop();
            double perSession = sw2.Elapsed.TotalMilliseconds / 100.0;
            PerfLines.Add($"熔合每个世界步（队列 {qmax} 项）{perStep * 1000.0:F2} µs；一场战斗的线索分析 {perSession:F3} ms（只在场次结束时一次）");
            PerfGate.Expect(true,
                $"O 性能：熔合每个世界步 {perStep:F4} ms（阈值 0.02 ms，O(进行中的任务)，有上限）；线索分析 {perSession:F3} ms / 场（阈值 2 ms，场次结束时一次性）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perStep, 0.02, "熔合每步 ms"), PerfGate.Le(perSession, 2.0, "线索分析 ms") }, Expect, Line);
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
