using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
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
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-DATA-01 敌人入表（FG09 FGR-FAC-001、FG14 FGR-ARC-005、验收 FGT-FAC-001）的行为自检。起真实系统（真实区域控制器、
    /// 战斗内核、存读档、信号判据、通知中心），行为坏掉会失败：
    ///   A 入表：全部敌人从 fg.TbMechEnemy 加载、编成全部可解析、表无结构问题；生产代码里没有残留的敌人 ID 字典 / 字面值；C# 读法与导表脚本一致。
    ///   B Demo 金标回归：两个区域 + 首领每个实例的内核出生模板与 Demo 常量逐项相等（数值写在本自检里，不读表），播种耐久 / 初始周期一致。
    ///   C 按读法与标签（不按 ID）：注入一个新 ID 的敌人（干扰机副本）→ 照常播种、按读法生成内核单位、按职能展开干扰场、按表掉落；
    ///     改读法 / 层级 / 标签 → 内核行为跟着变。
    ///   D 负向（表里缺字段的可见报错）：缺行 / 读法缺参数 / 读法不认识 / 标签不认识 / 锚点不存在 → 不生成、发“内容表错误”通知、同一问题只通知一次。
    ///   E 干扰机按职能的干扰场（FG-GAP-049）：铸造前哨“干扰支援”真的干扰信号（正式判据 SignalLinkService.IsJammedAt）、有干扰范围圈；击毁即解除；
    ///     破碎都市保留监听节点规则。
    ///   F 难度倍率（DEBT-FG6DEF09-01）：区域敌人耐久 / 武器伤害 × 难度倍率。
    ///   G 存读档：旧档首领类型 boss_node / boss_core 读档改写、伤害照常路由首领状态机；新档往返一致。
    ///   H 暂停与 0.5x～3x：表驱动的巡逻侦察机在四档倍速下同样游戏时长后位置逐位一致，暂停不走。
    ///   I 性能：干扰判据单次查询耗时（Editor batchmode 实测，阈值见断言）。
    /// </summary>
    public static class FgEnemyTableSelfCheck
    {
        private const int Slot = 0;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/FG 敌人入表")]
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
            Line("\n[敌人入表] 敌人入表（FG9-DATA-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgenemy-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();

                Step(CheckTablesAndNoHardcode);
                Step(CheckDemoGolden);
                Step(CheckDispatchByReadingNotId);
                Step(CheckNegativeMatrix);
                Step(CheckJamFieldByRole);
                Step(CheckDifficulty);
                Step(CheckSaveLoad);
                Step(CheckPauseSpeed);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"敌人入表自检抛异常：{e}");
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
                FgContentTables.ResetForTests();
                EnemyDefs.ResetReportsForTests();
                NotificationCenter.ResetForTests();
                GameClock.ResetSession();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                GameSettings.SetLanguage(originalLanguage);
                UiEscapeStack.Clear();
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
            Line($"  · [敌人入表] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 环境 ──────────────────────────────────────────────────────────

        private static CampaignState NewCampaign(int seed, string difficulty = "Standard")
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgenemy-" + seed, difficulty, seed);
            s.DifficultyId = difficulty;
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            return s;
        }

        private static FracturedCityController OpenFracturedCity(CampaignState s)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(Array.Empty<int>(), resume: false);
        }

        private static FoundryOutpostController OpenFoundry(CampaignState s, string adaptation = null)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord r = FoundryOutpostRegion.Find(s);
            r.State = RegionState.Available;
            r.AdaptationId = adaptation;
            return WorldSimulation.LoadFoundryOutpost(Array.Empty<int>(), resume: false);
        }

        private static RegionEnemyRecord Rec(CampaignState s, string id) => s.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == id);

        private static bool TryView(CombatSite site, string id, out CombatUnitView v)
        {
            v = default;
            return site != null && site.TryGetEnemyUnit(id, out int unit) && site.Kernel.TryGetUnit(unit, out v);
        }

        private static TbBuilding RealBuildings => ConfigSystem.Instance.Tables.TbBuilding;

        // ── A 入表与无残留硬编码（FGT-FAC-001）──────────────────────────────

        private static readonly string[] DemoEnemyIds =
            { "enemy_scout", "enemy_jammer", "enemy_strider", "enemy_armorbot", "enemy_repairbot", "enemy_core_boss" };

        private static void CheckTablesAndNoHardcode()
        {
            List<string> problems = EnemyDefs.AllProblems();
            bool demoRows = DemoEnemyIds.All(id => EnemyDefs.TryGet(id, out _)) && EnemyDefs.TryGet("enemy_core_node", out _);
            Expect(demoRows && problems.Count == 0 && EnemyDefs.Rows.Count >= 7,
                $"fg.TbMechEnemy 含 Demo 6 类 + 主核心供能节点共 {EnemyDefs.Rows.Count} 行，全部行结构完整（问题 {problems.Count}：{string.Join("；", problems)}）");

            // 两个区域 + 首领 + 反制增援 + 召唤：每条编成都能解析到敌人表与布局锚点。
            var unresolved = new List<string>();
            foreach (EnemyEncounter enc in FgContentTables.Encounters)
            {
                if (!EnemyDefs.TryGet(enc.EnemyTypeId, out _) || !RegionEnemySites.TrySpawnPosition(enc.RegionId, enc.AnchorId, out _)
                    || (enc.HomeAnchor != EnemyDefs.None && !RegionEnemySites.TrySpawnPosition(enc.RegionId, enc.HomeAnchor, out _)))
                {
                    unresolved.Add(enc.Id);
                }
            }
            Expect(FgContentTables.Encounters.Count >= 14 && unresolved.Count == 0,
                $"fg.TbEnemyEncounter {FgContentTables.Encounters.Count} 行编成全部能解析到敌人表与区域锚点（无法解析：{string.Join(",", unresolved)}）");

            // 生产入口播种后，每条区域敌人记录的类型都在敌人表里。
            CampaignState s = NewCampaign(9101);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.EnsureEnemiesSeeded(s);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.EnsureEnemiesSeeded(s);
            FoundryOutpostCoreBoss.EnsureInitialized(s, FoundryOutpostRegion.Find(s));
            string[] notInTable = s.RegionEnemies.Where(e => !EnemyDefs.TryGet(e.EnemyTypeId, out _)).Select(e => e.EnemyInstanceId).ToArray();
            Expect(s.RegionEnemies.Length >= 10 && notInTable.Length == 0,
                $"正式入口播种（破碎都市 + 铸造前哨 + 首领初始化）{s.RegionEnemies.Length} 个敌人，类型全部来自敌人表（不在表里：{string.Join(",", notInTable)}）");

            // 没有残留的硬编码字典：EnemyCatalog 类型不存在；生产代码里没有敌人表 ID 的字面值（旧档改写表除外）。
            Assembly logic = typeof(EnemyDefs).Assembly;
            bool catalogGone = logic.GetTypes().All(t => t.Name != "EnemyCatalog");
            string root = Path.Combine(Application.dataPath, "GameScripts", "HotFix", "GameLogic");
            var offenders = new List<string>();
            var idPattern = new Regex("\"(" + string.Join("|", EnemyDefs.Rows.Select(r => Regex.Escape(r.Id))) + ")\"");
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "EnemyDefs.cs")
                {
                    continue; // 只含旧档改写表（boss_node → enemy_core_node），自检 A3 对照导表脚本。
                }
                string text = File.ReadAllText(file);
                Match m = idPattern.Match(text);
                if (m.Success)
                {
                    offenders.Add(Path.GetFileName(file) + ":" + m.Value);
                }
            }
            Expect(catalogGone && !File.Exists(Path.Combine(root, "Campaign", "Content", "EnemyCatalog.cs")) && offenders.Count == 0,
                $"没有残留的硬编码敌人字典：EnemyCatalog 已删除（类型不存在、文件不存在），GameLogic 里没有敌人表 ID 字面值（违规：{string.Join("；", offenders)}）");

            // C# 读法 / 旧档改写表与导表脚本一致（两边任一改了另一边没改就失败）。
            string repo = LocateRepo();
            string py = repo == null ? string.Empty : File.ReadAllText(Path.Combine(repo, "tools", "cell_tables", "fgdata_enemy.py"));
            Expect(repo != null, $"找到仓库根（tools/cell_tables）：{repo}");
            Match beh = Regex.Match(py, @"BEHAVIORS = \(([^)]*)\)");
            string[] pyBehaviors = Regex.Matches(beh.Groups[1].Value, "\"([a-z_]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).OrderBy(x => x).ToArray();
            string[] csBehaviors = EnemyBehaviorReadings.All.Select(r => r.Key).OrderBy(x => x).ToArray();
            var reqMismatch = new List<string>();
            foreach (IEnemyBehaviorReading r in EnemyBehaviorReadings.All)
            {
                Match req = Regex.Match(py, "\"" + r.Key + "\": \\(([^)]*)\\)");
                string[] pyReq = Regex.Matches(req.Groups[1].Value, "\"([A-Za-z]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
                if (!req.Success || !pyReq.SequenceEqual(r.RequiredColumns))
                {
                    reqMismatch.Add(r.Key);
                }
            }
            bool legacyOk = EnemyDefs.LegacyTypeIds.All(kv => py.Contains($"\"{kv.Key}\": \"{kv.Value}\""));
            Expect(csBehaviors.SequenceEqual(pyBehaviors) && reqMismatch.Count == 0 && legacyOk,
                $"读法注册与 fgdata_enemy.BEHAVIORS / BEHAVIOR_REQUIRES 一致（C# {string.Join("/", csBehaviors)}；必需参数不一致 {string.Join(",", reqMismatch)}），旧档改写表一致 {legacyOk}");

            // 突袭点数唯一真相：编成表 points = 敌人表 raidPoints；每种可击败的敌人都有对应靶子（FG-GAP-101）。
            var raidBad = ConfigSystem.Instance.Tables.TbRaidUnit.DataList.Where(u => !EnemyDefs.TryGet(u.EnemyTypeId, out MechEnemy e) || e.RaidPoints != u.Points)
                .Select(u => u.Id).ToArray();
            var noTarget = EnemyDefs.Rows.Where(e => !EnemyDefs.IsStructure(e))
                .Where(e => !ConfigSystem.Instance.Tables.TbRangeTarget.DataList.Any(t => (t.UnlockEnemies ?? string.Empty).Split(',').Select(x => x.Trim()).Contains(e.Id)))
                .Select(e => e.Id).ToArray();
            Expect(raidBad.Length == 0 && noTarget.Length == 0,
                $"突袭编成点数全部等于敌人表 raidPoints（不一致 {string.Join(",", raidBad)}）；每种可击败的敌人都对应一个阵营靶子（缺 {string.Join(",", noTarget)}）");

            // 内容目录：显示名 / 图标 / 音效来自表（图鉴、头顶标记、字幕共用）。
            GameSettings.SetLanguage(GameLanguage.En);
            string en = FeedbackNames("enemy_jammer");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string zh = FeedbackNames("enemy_jammer");
            Expect(zh == "静默干扰机" && en == "Silent Jammer" && ContentIconsIcon("enemy_core_node") == "icon_enemy_core_boss",
                $"内容目录由表生成：干扰机显示名 中“{zh}”/ 英“{en}”随语言切换；供能节点头顶图标按表 iconId = {ContentIconsIcon("enemy_core_node")}");
        }

        /// <summary>从当前目录往上找仓库根（影子工程的当前目录在仓库根下的 .unity-validate-clone）。</summary>
        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "fgdata_enemy.py")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static string FeedbackNames(string id) => GameLogic.Campaign.Feedback.FeedbackCues.ContentName(id);

        private static string ContentIconsIcon(string id) => GameLogic.UI.Common.ContentIcons.IconIdFor(id);

        // ── B Demo 金标回归（数值写死在这里，不读表）───────────────────────────

        private readonly struct Golden
        {
            public readonly string Id;
            public readonly CombatBehavior Behavior;
            public readonly CombatUnitKind Kind;
            public readonly CombatUnitFlags Flags;
            public readonly float Radius;
            public readonly float Hp;
            public readonly float Cycle;
            public readonly float[] Weapon; // range, damage, cooldown, aim；null = 没有武器
            public readonly CombatBehaviorProfile? Profile;
            public readonly float Armor;
            public readonly float HalfAngle;
            public readonly Vector2 Facing;
            public readonly Vector2 Home;
            public readonly byte Priority;

            public Golden(string id, CombatBehavior b, CombatUnitKind k, CombatUnitFlags f, float radius, float hp, float cycle, float[] weapon,
                CombatBehaviorProfile? profile, float armor, float halfAngle, Vector2 facing, Vector2 home, byte priority)
            {
                Id = id;
                Behavior = b;
                Kind = k;
                Flags = f;
                Radius = radius;
                Hp = hp;
                Cycle = cycle;
                Weapon = weapon;
                Profile = profile;
                Armor = armor;
                HalfAngle = halfAngle;
                Facing = facing;
                Home = home;
                Priority = priority;
            }
        }

        private const CombatUnitFlags AT = CombatUnitFlags.Alive | CombatUnitFlags.Targetable;

        /// <summary>Demo 常量（原 FracturedCityLayout / FoundryOutpostLayout / CombatDemoContent，DEMO-CONTENT-LOCK.md §4/§5）。</summary>
        private static IEnumerable<Golden> DemoGoldens()
        {
            var scoutProfile = new CombatBehaviorProfile
            {
                Speed = 2.2f, FleeTrigger = 6f, Leash = 5f, PatrolRadius = 2.5f, PatrolFreq = 0.6f, SenseRange = 10f, CycleSeconds = 8f, EffectSeconds = 10f,
            };
            var repairProfile = new CombatBehaviorProfile { Speed = 2.4f, FleeTrigger = 5f, Leash = 6f, EffectRange = 6f, EffectAmount = 15f, CycleSeconds = 3f };
            var jamProfile = new CombatBehaviorProfile { EffectRange = 12f };
            Vector2 down = new Vector2(0f, -1f);
            CombatUnitFlags rl = AT | CombatUnitFlags.Report | CombatUnitFlags.NeedsLos;
            CombatUnitFlags rlw = rl | CombatUnitFlags.WeaponEnabled;
            yield return new Golden("fc_scout_1", CombatBehavior.Scout, CombatUnitKind.Enemy, rl, 0.8f, 70f, 8f, null, scoutProfile, 0f, 90f, down, new Vector2(-10f, -8f), 1);
            yield return new Golden("fc_scout_2", CombatBehavior.Scout, CombatUnitKind.Enemy, rl, 0.8f, 70f, 8f, null, scoutProfile, 0f, 90f, down, new Vector2(10f, -8f), 1);
            yield return new Golden("fc_jammer", CombatBehavior.Jammer, CombatUnitKind.Enemy, rlw, 0.8f, 90f, 8f, new[] { 8f, 6f, 2.5f, 0f }, jamProfile, 0f, 90f, down, new Vector2(4f, 4f), 1);
            yield return new Golden("fo_armorbot_left", CombatBehavior.HoldFire, CombatUnitKind.Enemy, rlw, 0.8f, 160f, 0f, new[] { 9f, 10f, 1.6f, 0f }, null, 0.4f, 75f,
                new Vector2(0.4f, -1f).normalized, new Vector2(-9f, -2f), 1);
            yield return new Golden("fo_armorbot_right", CombatBehavior.HoldFire, CombatUnitKind.Enemy, rlw, 0.8f, 160f, 0f, new[] { 9f, 10f, 1.6f, 0f }, null, 0.4f, 75f,
                new Vector2(-0.4f, -1f).normalized, new Vector2(9f, -2f), 1);
            yield return new Golden("fo_strider", CombatBehavior.Telegraph, CombatUnitKind.Enemy, rlw, 0.8f, 110f, 0f, new[] { 14f, 20f, 2.5f, 1f }, null, 0f, 90f, down,
                new Vector2(0f, 8f), 1);
            yield return new Golden("fo_repairbot", CombatBehavior.Repair, CombatUnitKind.Enemy, AT | CombatUnitFlags.Report, 0.8f, 80f, 0f, null, repairProfile, 0f, 90f, down,
                new Vector2(4f, 2f), 1);
            yield return new Golden("fo_core_node_1", CombatBehavior.None, CombatUnitKind.Structure, AT | CombatUnitFlags.ExternalHealth, 2f, 100f, 0f, null, null, 0f, 90f, down,
                new Vector2(-7f, 38f), 1);
            yield return new Golden("fo_core_node_2", CombatBehavior.None, CombatUnitKind.Structure, AT | CombatUnitFlags.ExternalHealth, 2f, 100f, 0f, null, null, 0f, 90f, down,
                new Vector2(7f, 38f), 1);
            yield return new Golden("fo_main_core", CombatBehavior.HoldFire, CombatUnitKind.Structure,
                AT | CombatUnitFlags.ExternalHealth | CombatUnitFlags.NeedsLos | CombatUnitFlags.SilentFire, 3f, 600f, 0f, new[] { 16f, 18f, 2f, 0f }, null, 0f, 75f, down,
                new Vector2(0f, 45f), 0);
            yield return new Golden("fo_flanker", CombatBehavior.HoldFire, CombatUnitKind.Enemy, rlw, 0.8f, 160f, 0f, new[] { 9f, 10f, 1.6f, 0f }, null, 0.4f, 75f,
                new Vector2(1f, -0.6f).normalized, new Vector2(-14f, 4f), 1);
            yield return new Golden("fo_core_repairbot", CombatBehavior.Repair, CombatUnitKind.Enemy, AT | CombatUnitFlags.Report, 0.8f, 80f, 0f, null, repairProfile, 0f, 90f, down,
                new Vector2(4f, 2f), 1);
            // FG9-DATA-01 有意改变（FG-GAP-049，见 ADR）：“干扰支援”调来的干扰机从 Demo 的驻守开火改为干扰机读法（清标记 + 干扰场 + 驻守开火）。
            yield return new Golden("fo_jammer_support", CombatBehavior.Jammer, CombatUnitKind.Enemy, rlw, 0.8f, 90f, 0f, new[] { 8f, 6f, 2.5f, 0f }, jamProfile, 0f, 90f, down,
                new Vector2(14f, 4f), 1);
        }

        private static void CheckDemoGolden()
        {
            CampaignState s = NewCampaign(9102);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.EnsureEnemiesSeeded(s);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.EnsureEnemiesSeeded(s);
            FoundryOutpostCoreBoss.EnsureInitialized(s, FoundryOutpostRegion.Find(s));
            RegionEnemySites.SeedInstance(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.FlankerSpawnId);
            RegionEnemySites.SeedInstance(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.CoreRepairBotSummonId);
            RegionEnemySites.SeedInstance(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.JammerSupportSpawnId);
            var site = new CombatSite("fg9_golden", CombatSite.ConfigFromTuning());
            var mismatches = new List<string>();
            int checkedCount = 0;
            try
            {
                foreach (Golden g in DemoGoldens())
                {
                    RegionEnemyRecord rec = Rec(s, g.Id);
                    if (rec == null || !CombatDemoContent.TryTemplate(site, s, rec, out CombatSpawn sp))
                    {
                        mismatches.Add(g.Id + "：没有播种 / 模板生成失败");
                        continue;
                    }
                    checkedCount++;
                    var diff = new List<string>();
                    void Cmp(bool ok, string what)
                    {
                        if (!ok)
                        {
                            diff.Add(what);
                        }
                    }
                    Cmp(sp.Behavior == g.Behavior, $"行为 {sp.Behavior}≠{g.Behavior}");
                    Cmp(sp.Kind == g.Kind, $"类型 {sp.Kind}≠{g.Kind}");
                    Cmp(sp.Flags == g.Flags, $"标志 {sp.Flags}≠{g.Flags}");
                    Cmp(Mathf.Approximately(sp.Radius, g.Radius), $"半径 {sp.Radius}");
                    Cmp(Mathf.Approximately(rec.MaxHealth, g.Hp) && Mathf.Approximately(rec.Health, g.Hp), $"耐久 {rec.MaxHealth}");
                    Cmp(Mathf.Approximately(rec.CycleCooldownRemaining, g.Cycle), $"初始周期 {rec.CycleCooldownRemaining}");
                    Cmp(sp.Priority == g.Priority, $"优先级 {sp.Priority}");
                    Cmp(Mathf.Approximately(sp.ArmorFraction, g.Armor) && Mathf.Approximately(sp.ArmorHalfAngleDeg, g.HalfAngle), $"护甲 {sp.ArmorFraction}/{sp.ArmorHalfAngleDeg}");
                    Cmp(math.distance(sp.ArmorFacing, new float2(g.Facing.x, g.Facing.y)) < 1e-4f, $"朝向 {sp.ArmorFacing}");
                    Cmp(math.distance(sp.Home, new double2(g.Home.x, g.Home.y)) < 1e-4, $"牵引中心 {sp.Home}");
                    if (g.Weapon == null)
                    {
                        Cmp(sp.Weapon < 0, "不该有武器");
                    }
                    else
                    {
                        int expectW = site.WeaponIndex(new CombatWeapon
                        {
                            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = g.Weapon[0], Damage = g.Weapon[1], Cooldown = g.Weapon[2], AimSeconds = g.Weapon[3],
                            TargetMode = CombatTargetMode.Nearest,
                        });
                        Cmp(sp.Weapon == expectW, $"武器 {sp.Weapon}≠{expectW}");
                    }
                    if (g.Profile == null)
                    {
                        Cmp(sp.BehaviorProfile < 0, "不该有行为参数");
                    }
                    else
                    {
                        Cmp(sp.BehaviorProfile == site.ProfileIndex(g.Profile.Value), "行为参数与 Demo 不一致");
                    }
                    if (diff.Count > 0)
                    {
                        mismatches.Add(g.Id + "：" + string.Join("，", diff));
                    }
                }
            }
            finally
            {
                site.Dispose();
            }
            Expect(checkedCount == DemoGoldens().Count() && mismatches.Count == 0,
                $"Demo 金标：{checkedCount} 个实例（6 类敌人 + 首领部件 + 反制增援 + 召唤）的内核行为 / 标志 / 武器 / 行为参数 / 护甲 / 朝向 / 牵引中心 / 耐久 / 初始周期与 Demo 常量逐项一致" +
                (mismatches.Count == 0 ? string.Empty : "：" + string.Join("；", mismatches)));

            // 掉落按表：步进炮首次击破掉关键物（keyDrop），所有敌人按表掉废料。
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.StriderSpawnId, 1e6f);
            bool keyDrop = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Count(q => q.ContentId == FoundryOutpostLayout.CannonModuleContentId) == 1;
            GroundItemRecord scrap = s.GroundItems?.FirstOrDefault(g => g.SalvageInstanceId == FoundryOutpostLayout.StriderSpawnId + ":scrap");
            Expect(keyDrop && scrap != null && scrap.Amount == 30,
                $"步进炮击破：按表 keyDrop 掉铸造重炮模块 {(keyDrop ? 1 : 0)} 个、按表 scrapLoot 掉废料 {scrap?.Amount}（Demo 30）");
        }

        // ── C 按读法与标签（不按 ID）──────────────────────────────────────────

        private const string ProbeEnemy = "enemy_fg9_probe";
        private const string ProbeInstance = "fo_fg9_probe";

        private static void CheckDispatchByReadingNotId()
        {
            // C1 一个全新 ID 的敌人（干扰机的副本）只加表行就能出现在铸造前哨、按干扰机读法生成、展开干扰场、按表掉落。
            FgContentTables.OverrideForTests(RealBuildings,
                FgEnemyTableKit.Enemies(null, FgEnemyTableKit.CloneEnemy("enemy_jammer", ProbeEnemy, ("ScrapLoot", 33))),
                null,
                FgEnemyTableKit.Encounters(null, FgEnemyTableKit.CloneEncounter(FoundryOutpostLayout.JammerSupportSpawnId, ProbeInstance,
                    ("EnemyTypeId", ProbeEnemy), ("Condition", EnemyDefs.ConditionBase), ("SortOrder", 999))));
            try
            {
                CampaignState s = NewCampaign(9103);
                FoundryOutpostController fo = OpenFoundry(s);
                CombatSite site = fo.Combat;
                RegionEnemyRecord probe = Rec(s, ProbeInstance);
                bool view = TryView(site, ProbeInstance, out CombatUnitView v);
                Vector2 at = FoundryOutpostLayout.JammerSupportSpawn.Position;
                bool jammedIn = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at + new Vector2(0f, -10f));
                bool jammedOut = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at + new Vector2(0f, -13f));
                Expect(probe != null && probe.EnemyTypeId == ProbeEnemy && Mathf.Approximately(probe.MaxHealth, 90f) && view && v.Behavior == CombatBehavior.Jammer
                       && (v.Flags & CombatUnitFlags.WeaponEnabled) != 0 && jammedIn && !jammedOut,
                    $"新 ID 敌人（{ProbeEnemy}，只加了表行与编成行）：播种耐久 {probe?.MaxHealth}、内核行为 {v.Behavior}、10 米内被干扰 {jammedIn}、13 米外不干扰 {!jammedOut}——生产代码没有它的任何特例");
                FoundryOutpostRegion.TryDamageEnemy(s, ProbeInstance, 1e6f);
                WorldSimulation.StepMany(2);
                GroundItemRecord loot = s.GroundItems?.FirstOrDefault(g => g.SalvageInstanceId == ProbeInstance + ":scrap");
                bool jammedAfter = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at + new Vector2(0f, -10f));
                bool defeated = s.Research?.Range?.DefeatedEnemyTypes?.Contains(ProbeEnemy) ?? false;
                Expect(!Rec(s, ProbeInstance).IsAlive && loot != null && loot.Amount == 33 && !jammedAfter && defeated,
                    $"击毁新敌人：按表掉落废料 {loot?.Amount}（表值 33）、干扰场随之解除 {!jammedAfter}、记入“击败过的敌人类型” {defeated}");
            }
            finally
            {
                FgContentTables.ResetForTests();
                EnemyDefs.ResetReportsForTests();
            }

            // C2 只改表：护甲机读法改成瞄准线两段式、层级改成精英、去掉“耐热”标签 → 内核行为 / 精英标记 / 耐热跟着变。
            FgContentTables.OverrideForTests(RealBuildings, FgEnemyTableKit.Enemies(v =>
            {
                if ((string)v["Id"] == "enemy_armorbot")
                {
                    v["Behavior"] = EnemyDefs.BehaviorTelegraph;
                    v["WeaponAim"] = 0.5f;
                    v["Tier"] = EnemyDefs.TierElite;
                    v["Tags"] = "none";
                }
                return true;
            }));
            try
            {
                CampaignState s = NewCampaign(9104);
                FoundryOutpostController fo = OpenFoundry(s, AdaptationCatalog.HeatResistant);
                bool ok = TryView(fo.Combat, FoundryOutpostLayout.ArmorBotLeftSpawnId, out CombatUnitView v);
                Expect(ok && v.Behavior == CombatBehavior.Telegraph && (v.Flags & CombatUnitFlags.Elite) != 0 && (v.Flags & CombatUnitFlags.HeatResistant) == 0,
                    $"改表：护甲机读法 hold_fire → telegraph、层级 → elite、去掉 adapt_heat：内核行为 {v.Behavior}、精英标记 {(v.Flags & CombatUnitFlags.Elite) != 0}、耐热 {(v.Flags & CombatUnitFlags.HeatResistant) != 0}（反制是耐热）");
            }
            finally
            {
                FgContentTables.ResetForTests();
                EnemyDefs.ResetReportsForTests();
            }

            // C3 对照：真实表 + 反制耐热 → 护甲机（带 adapt_heat 标签）耐热，步进炮（没有该标签）不耐热。
            {
                CampaignState s = NewCampaign(9105);
                FoundryOutpostController fo = OpenFoundry(s, AdaptationCatalog.HeatResistant);
                TryView(fo.Combat, FoundryOutpostLayout.ArmorBotLeftSpawnId, out CombatUnitView armor);
                TryView(fo.Combat, FoundryOutpostLayout.StriderSpawnId, out CombatUnitView strider);
                Expect((armor.Flags & CombatUnitFlags.HeatResistant) != 0 && (strider.Flags & CombatUnitFlags.HeatResistant) == 0 && armor.Behavior == CombatBehavior.HoldFire,
                    "真实表 + 反制“耐热”：带 adapt_heat 标签的护甲机耐热、没有标签的步进炮不耐热（按标签，不按 ID）");
            }
        }

        // ── D 负向矩阵（表里缺字段时的可见报错）──────────────────────────────

        private static int ContentErrors() =>
            NotificationCenter.History.Where(n => n.Type?.Id == EnemyDefs.NotifyContentError).Sum(n => n.Count);

        private static void CheckNegativeMatrix()
        {
            void Case(string label, Func<Dictionary<string, object>, bool> enemyEdit, Dictionary<string, object> extraEnemy, Dictionary<string, object> extraEnc,
                string instance, string expectInText)
            {
                FgContentTables.OverrideForTests(RealBuildings,
                    FgEnemyTableKit.Enemies(enemyEdit, extraEnemy == null ? Array.Empty<Dictionary<string, object>>() : new[] { extraEnemy }),
                    null,
                    FgEnemyTableKit.Encounters(null, extraEnc == null ? Array.Empty<Dictionary<string, object>>() : new[] { extraEnc }));
                try
                {
                    NotificationCenter.ResetForTests();
                    EnemyDefs.ResetReportsForTests();
                    CampaignState s = NewCampaign(9110);
                    bool threw = false;
                    FoundryOutpostController fo = null;
                    try
                    {
                        fo = OpenFoundry(s);
                        WorldSimulation.StepMany(2);
                    }
                    catch (Exception e)
                    {
                        threw = true;
                        Line("    异常：" + e.Message);
                    }
                    RegionEnemyRecord rec = Rec(s, instance);
                    bool noUnit = fo == null || !fo.Combat.TryGetEnemyUnit(instance, out _);
                    int errors = ContentErrors();
                    string text = string.Join(" / ", NotificationCenter.History.Where(n => n.Type?.Id == EnemyDefs.NotifyContentError).Select(n => n.Text));
                    // 其它正常敌人照常生成（坏一行不拖垮整个区域）：至少还有 2 个敌人进了内核。
                    bool othersOk = fo != null && fo.Combat.EnemyIds.Count() >= 2;
                    Expect(!threw && noUnit && errors == 1 && text.Contains(expectInText) && othersOk,
                        $"负向「{label}」：{instance} 不生成内核单位（记录 {(rec == null ? "未播种" : "已播种但不进内核")}），发 {errors} 条“内容表错误”通知“{text}”，其它敌人照常生成，不抛异常");
                }
                finally
                {
                    FgContentTables.ResetForTests();
                    EnemyDefs.ResetReportsForTests();
                }
            }

            Case("编成引用了表里没有的敌人", null, null,
                FgEnemyTableKit.CloneEncounter(FoundryOutpostLayout.JammerSupportSpawnId, "fo_fg9_missing", ("EnemyTypeId", "enemy_fg9_nope"), ("Condition", EnemyDefs.ConditionBase)),
                "fo_fg9_missing", "enemy_fg9_nope");
            Case("驻守开火缺武器射程（缺字段读成 0）", v =>
            {
                if ((string)v["Id"] == "enemy_strider")
                {
                    v["Behavior"] = EnemyDefs.BehaviorHoldFire;
                    v["WeaponRange"] = 0f;
                }
                return true;
            }, null, null, FoundryOutpostLayout.StriderSpawnId, "enemy_strider");
            Case("读法不认识", v =>
            {
                if ((string)v["Id"] == "enemy_repairbot")
                {
                    v["Behavior"] = "teleport";
                }
                return true;
            }, null, null, FoundryOutpostLayout.RepairBotSpawnId, "behavior=teleport");
            Case("标签不认识", v =>
            {
                if ((string)v["Id"] == "enemy_armorbot")
                {
                    v["Tags"] = "adapt_heat,flying";
                }
                return true;
            }, null, null, FoundryOutpostLayout.ArmorBotLeftSpawnId, "tag=flying");
            Case("编成锚点不存在", null, null,
                FgEnemyTableKit.CloneEncounter(FoundryOutpostLayout.JammerSupportSpawnId, "fo_fg9_noanchor", ("AnchorId", "fo_no_such_anchor"), ("Condition", EnemyDefs.ConditionBase)),
                "fo_fg9_noanchor", "fo_no_such_anchor");

            // 修复轮 P2：首领编成行 / 首领敌人行缺失 → 读首领布局数值先发可见“内容表错误”通知，再抛明确异常（不拿默认值顶替）。
            void BossCase(string label, TbMechEnemy enemies, TbEnemyEncounter encounters, string expectInText)
            {
                FgContentTables.OverrideForTests(RealBuildings, enemies, null, encounters);
                try
                {
                    NotificationCenter.ResetForTests();
                    EnemyDefs.ResetReportsForTests();
                    NewCampaign(9112);
                    string thrown = null;
                    try
                    {
                        _ = FoundryOutpostLayout.MainCoreMaxHealth;
                    }
                    catch (InvalidOperationException e)
                    {
                        thrown = e.Message;
                    }
                    string text = string.Join(" / ", NotificationCenter.History.Where(n => n.Type?.Id == EnemyDefs.NotifyContentError).Select(n => n.Text));
                    Expect(thrown != null && ContentErrors() == 1 && text.Contains(expectInText),
                        $"负向「{label}」：读主核心耐久发 {ContentErrors()} 条可见通知“{text}”，并抛明确异常“{thrown}”（不拿 0 / 默认值顶替）");
                }
                finally
                {
                    FgContentTables.ResetForTests();
                    EnemyDefs.ResetReportsForTests();
                    NotificationCenter.ResetForTests();
                }
            }
            BossCase("首领编成行缺失", FgEnemyTableKit.Enemies(),
                FgEnemyTableKit.Encounters(v => (string)v["Id"] != FoundryOutpostLayout.MainCoreId), FoundryOutpostLayout.MainCoreId);
            BossCase("首领敌人行缺失", FgEnemyTableKit.Enemies(v => (string)v["Id"] != "enemy_core_boss"),
                FgEnemyTableKit.Encounters(), "enemy_core_boss");

            // 运行时：旧档里的敌人类型已从表里删掉 → 载入区域时该敌人不进内核、可见报错；同一问题只通知一次。
            FgContentTables.OverrideForTests(RealBuildings, FgEnemyTableKit.Enemies(v => (string)v["Id"] != "enemy_repairbot"));
            try
            {
                NotificationCenter.ResetForTests();
                EnemyDefs.ResetReportsForTests();
                CampaignState s = NewCampaign(9111);
                FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
                s.RegionEnemies = s.RegionEnemies.Append(new RegionEnemyRecord
                {
                    EnemyInstanceId = FoundryOutpostLayout.RepairBotSpawnId, RegionId = FoundryOutpostLayout.RegionId, EnemyTypeId = "enemy_repairbot",
                    Position = FoundryOutpostLayout.RepairBotSpawn.Position, Health = 80f, MaxHealth = 80f, IsAlive = true,
                }).ToArray();
                FoundryOutpostController fo = OpenFoundry(s);
                CombatDemoContent.ReconcileEnemies(fo.Combat, s, FoundryOutpostLayout.RegionId);
                CombatDemoContent.ReconcileEnemies(fo.Combat, s, FoundryOutpostLayout.RegionId);
                Expect(!fo.Combat.TryGetEnemyUnit(FoundryOutpostLayout.RepairBotSpawnId, out _) && ContentErrors() == 1 && EnemyDefs.ReportedCount == 1,
                    $"旧档敌人类型已从表里删除：载入区域不生成替身、可见报错 {ContentErrors()} 条（重复对账不重复通知）");
            }
            finally
            {
                FgContentTables.ResetForTests();
                EnemyDefs.ResetReportsForTests();
                NotificationCenter.ResetForTests();
            }
        }

        // ── E 干扰机按职能的干扰场（FG-GAP-049）──────────────────────────────

        private static void CheckJamFieldByRole()
        {
            CampaignState s = NewCampaign(9120);
            FoundryOutpostController fo = OpenFoundry(s, AdaptationCatalog.JammerSupport);
            fo.SetObserved(true); // 表现对象只在被观察时存在（FG0-ARCH-03）。
            Vector2 at = FoundryOutpostLayout.JammerSupportSpawn.Position;
            Vector2 inside = at + new Vector2(0f, -11f);
            Vector2 outside = at + new Vector2(0f, -12.5f);
            bool jIn = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, inside);
            bool jOut = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, outside);
            Transform ring = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == FoundryOutpostController.JamFieldRingName && t.parent != null && t.parent.name == "Enemy_" + FoundryOutpostLayout.JammerSupportSpawnId);
            bool ringOn = ring != null && ring.gameObject.activeSelf && Mathf.Approximately(ring.localScale.x, 24f);
            Expect(jIn && !jOut && ringOn,
                $"铸造前哨“干扰支援”的干扰机按职能展开干扰场（正式判据 SignalLinkService.IsJammedAt）：11 米内 {jIn}、12.5 米外 {!jOut}；干扰范围圈直径 {ring?.localScale.x}（24）");

            // 内核位置为准：干扰机被挪开后，干扰场跟着走。
            fo.Combat.TryGetEnemyUnit(FoundryOutpostLayout.JammerSupportSpawnId, out int unit);
            fo.Combat.Kernel.SetPosition(unit, new double2(at.x + 30f, at.y));
            bool followOld = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, inside);
            bool followNew = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at + new Vector2(30f, -11f));
            fo.Combat.Kernel.SetPosition(unit, new double2(at.x, at.y));
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.JammerSupportSpawnId, 1e6f);
            WorldSimulation.StepMany(2);
            bool jAfter = SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, inside);
            bool ringOff = ring != null && !ring.gameObject.activeSelf;
            Expect(!followOld && followNew && !jAfter && ringOff,
                $"干扰场以内核里的实时位置为圆心（挪走后原处不干扰 {!followOld}、新位置干扰 {followNew}）；击毁干扰机后干扰解除 {!jAfter}、范围圈隐藏 {ringOff}");

            // 破碎都市保留 Demo 规则：干扰场由监听节点提供（节点被毁即解除，与干扰机死活无关）。
            CampaignState c = NewCampaign(9121);
            OpenFracturedCity(c);
            Vector2 nearNode = FracturedCityLayout.ListeningNode.Position + new Vector2(FracturedCityLayout.JammerRadius - 1f, 0f);
            bool nodeJam = SignalLinkService.IsJammedAt(c, FracturedCityLayout.RegionId, nearNode);
            RegionRecord ruins = FracturedCityRegion.Find(c);
            ruins.DestroyedNodeIds = (ruins.DestroyedNodeIds ?? Array.Empty<string>()).Append(FracturedCityLayout.ListeningNodeId).ToArray();
            bool afterNode = SignalLinkService.IsJammedAt(c, FracturedCityLayout.RegionId, FracturedCityLayout.JammerSpawn.Position);
            Expect(nodeJam && !afterNode && Rec(c, FracturedCityLayout.JammerSpawnId).IsAlive,
                "破碎都市：监听节点干扰场照旧；节点被毁后干扰机还活着也不再干扰（Demo 规则，场地声明“干扰场由设施提供”）");
        }

        // ── F 难度倍率（DEBT-FG6DEF09-01）────────────────────────────────────

        private static void CheckDifficulty()
        {
            CampaignState harsh = NewCampaign(9130, "Harsh");
            FoundryOutpostController fo = OpenFoundry(harsh);
            float hpMul = DifficultyService.EnemyHealthMul(harsh);
            float dmgMul = DifficultyService.EnemyDamageMul(harsh);
            RegionEnemyRecord armor = Rec(harsh, FoundryOutpostLayout.ArmorBotLeftSpawnId);
            TryView(fo.Combat, FoundryOutpostLayout.ArmorBotLeftSpawnId, out CombatUnitView v);
            fo.Combat.Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w);
            string intel = ExpeditionDepartureService.BuildEnemyIntel(harsh, FoundryOutpostLayout.RegionId);
            Expect(hpMul > 1.01f && Mathf.Approximately(armor.MaxHealth, 160f * hpMul) && Mathf.Approximately(w.Damage, 10f * dmgMul)
                   && intel.Contains("HP" + (160f * hpMul).ToString("F0", CultureInfo.InvariantCulture)),
                $"难度“严酷”：区域敌人耐久 ×{hpMul}（护甲机 {armor.MaxHealth}）、武器伤害 ×{dmgMul}（{w.Damage}），出征情报写的是生效耐久：{intel}");

            // 修复轮 P1：区域未播种时的出征估算与敌情行同一口径（表 maxHp × 难度倍率），播种后真实存活耐久与估算一致。
            CampaignState unseeded = NewCampaign(9132, "Harsh");
            FoundryOutpostRegion.EnsureRegionRecordSeeded(unseeded);
            float uMul = DifficultyService.EnemyHealthMul(unseeded);
            float tableSum = 0f;
            int tableCount = 0;
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(FoundryOutpostLayout.RegionId, EnemyDefs.ConditionBase))
            {
                if (EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy encRow))
                {
                    tableSum += encRow.MaxHp;
                    tableCount++;
                }
            }
            ExpeditionForecast.Result est = ExpeditionForecast.Compute(unseeded, Array.Empty<int>(), ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            ExpeditionForecast.EstimateGarrison(unseeded, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost, out int estCount, out float estHealth);
            FoundryOutpostRegion.EnsureEnemiesSeeded(unseeded);
            ExpeditionForecast.Result live = ExpeditionForecast.Compute(unseeded, Array.Empty<int>(), ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            Expect(uMul > 1.01f && est.EnemyEstimated && est.EnemyCount == tableCount && estCount == tableCount
                   && Mathf.Approximately(est.EnemyHealth, tableSum * uMul) && Mathf.Approximately(estHealth, tableSum * uMul)
                   && !live.EnemyEstimated && live.EnemyCount == est.EnemyCount && Mathf.Approximately(live.EnemyHealth, est.EnemyHealth)
                   && ExpeditionForecast.Describe(est).Contains("耐久 " + (tableSum * uMul).ToString("F0", CultureInfo.InvariantCulture)),
                $"难度“严酷”未播种的出征估算 = Σ表值 {tableSum} × {uMul} = {est.EnemyHealth}（{est.EnemyCount} 个，估算 {est.EnemyEstimated}），播种后真实 {live.EnemyHealth}（{live.EnemyCount} 个），面板：{ExpeditionForecast.Describe(est)}");
            CampaignState builder = NewCampaign(9131, "Builder");
            FracturedCityRegion.EnsureRegionRecordSeeded(builder);
            FracturedCityRegion.EnsureEnemiesSeeded(builder);
            float bMul = DifficultyService.EnemyHealthMul(builder);
            Expect(bMul < 0.99f && Mathf.Approximately(Rec(builder, FracturedCityLayout.Scout1SpawnId).MaxHealth, 70f * bMul)
                   && GameText.Get("difficulty.line.enemy").Contains("区域"),
                $"难度“建造者”：侦察机耐久 ×{bMul} = {Rec(builder, FracturedCityLayout.Scout1SpawnId).MaxHealth}；难度说明公开写明区域敌人也受倍率影响（“{GameText.Get("difficulty.line.enemy")}”）");
        }

        // ── G 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewCampaign(9140, "Harsh");
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.Find(s).State = RegionState.Available;
            FoundryOutpostRegion.EnsureEnemiesSeeded(s);
            FoundryOutpostCoreBoss.EnsureInitialized(s, FoundryOutpostRegion.Find(s));
            // 造旧档：首领部件的类型写回 FG9-DATA-01 前的 boss_node / boss_core。
            Rec(s, FoundryOutpostLayout.CoreNode1Id).EnemyTypeId = "boss_node";
            Rec(s, FoundryOutpostLayout.CoreNode2Id).EnemyTypeId = "boss_node";
            Rec(s, FoundryOutpostLayout.MainCoreId).EnemyTypeId = "boss_core";
            Rec(s, FoundryOutpostLayout.StriderSpawnId).Health = 55f;
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            CampaignState l = loaded.State;
            bool upgraded = l != null && Rec(l, FoundryOutpostLayout.CoreNode1Id)?.EnemyTypeId == "enemy_core_node"
                            && Rec(l, FoundryOutpostLayout.MainCoreId)?.EnemyTypeId == "enemy_core_boss";
            bool kept = l != null && Mathf.Approximately(Rec(l, FoundryOutpostLayout.StriderSpawnId).Health, 55f)
                        && Mathf.Approximately(Rec(l, FoundryOutpostLayout.ArmorBotLeftSpawnId).MaxHealth, 160f * DifficultyService.EnemyHealthMul(l));
            bool routed = false;
            if (l != null)
            {
                CampaignSession.Set(Slot, l);
                float before = Rec(l, FoundryOutpostLayout.CoreNode1Id).Health;
                FoundryOutpostRegion.TryDamageEnemy(l, FoundryOutpostLayout.CoreNode1Id, 10f);
                routed = Mathf.Approximately(before - Rec(l, FoundryOutpostLayout.CoreNode1Id).Health, 10f);
            }
            Expect(saved.Outcome == SaveOutcome.Success && loaded.Outcome == LoadOutcome.Success && upgraded && kept && routed,
                $"存读档：旧档首领类型 boss_node / boss_core 读档改写成敌人表 ID {upgraded}；敌人血量 / 难度耐久往返一致 {kept}；护盾阶段打节点照常路由首领状态机 {routed}");
        }

        // ── H 暂停与 0.5x～3x ─────────────────────────────────────────────────

        private static string EnemyHash(CombatSite site)
        {
            var sb = new StringBuilder();
            foreach (string id in site.EnemyIds.OrderBy(x => x, StringComparer.Ordinal))
            {
                site.TryGetEnemyPosition(id, out Vector2 p);
                sb.Append(id).Append(':').Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }

        private static void CheckPauseSpeed()
        {
            var hashes = new List<string>();
            bool moved = false;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewCampaign(9150);
                FracturedCityController city = OpenFracturedCity(s);
                string start = EnemyHash(city.Combat);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + 60 * 20;
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                string end = EnemyHash(city.Combat);
                moved |= end != start;
                hashes.Add(end);
            }
            CampaignState p = NewCampaign(9150);
            FracturedCityController pc = OpenFracturedCity(p);
            string beforePause = EnemyHash(pc.Combat);
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            string paused = EnemyHash(pc.Combat);
            GameClock.SetPaused(false);
            Expect(moved && hashes.Distinct().Count() == 1 && beforePause == paused,
                "表驱动的巡逻侦察机（patrol_mark 读法）：0.5x / 1x / 2x / 3x 跑同样 20 游戏秒后全部敌人位置逐位一致且确实在巡逻；暂停 2 秒真实时间不走");
        }

        // ── I 性能 ─────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewCampaign(9160);
            OpenFoundry(s, AdaptationCatalog.JammerSupport);
            Vector2 at = FoundryOutpostLayout.JammerSupportSpawn.Position;
            const int n = 200000;
            int hits = 0;
            SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                if (SignalLinkService.IsJammedAt(s, FoundryOutpostLayout.RegionId, at + new Vector2(i % 30 - 15, 0f)))
                {
                    hits++;
                }
            }
            sw.Stop();
            double usPer = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            PerfGate.Expect(hits > 0, $"干扰判据（铸造前哨，1 个干扰源 + 区域其它敌人）单次查询 {usPer:F3} 微秒（Editor batchmode，Mono JIT；真机 IL2CPP + HybridCLR 解释执行另测 FG15-SYS-02）",
                new[] { new PerfGate.Metric("干扰判据单次查询(微秒)", usPer, 5.0, false) }, Expect, Line);
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

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
            _report?.AppendLine(text);
        }
    }
}
