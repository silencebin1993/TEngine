using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
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
    /// FG11-FAC-01 超频单位与精英（FG09 第 7 章；FGR-FAC-001 / 003 / 004 / 005 / 008；FG19 FGR-EMG-160 / 164；验收 FGT-FAC-005）的自检——起真实系统、断言行为：
    /// A 数据：5 类单位 + 精英失控超算体入表（读法 / 层级 / 职能 / 装配 / 端口声明 / 中英文本）、专属行为预警 ≥ 0.8 秒；突袭编成 / 反制行 / 据点编成都是本阵营单位；靶子覆盖。
    /// B 内核专属语法（真实步进）：猎犬冲刺—过热—停机（停机不动、受伤 ×1.5）与死亡泄爆（伤到它的友军）；算力光环射速 +50% 不叠加；熔断者 30% 熔断 2 秒爆、熔断中被击毁不爆；
    ///   过热炮台越打越快、满热排气（不开火、受伤 ×1.6）；失控超算体三态循环（预警 ≥ 0.8 秒、强攻 / 护盾）与前倾加速。
    /// C 高温辐射：区内过热点 ×0.75、散热 ×0.8（敌我同一规则，真的更早过热），区外不变；规划层辐射区写进星球内核。
    /// D FGT-FAC-005 反制：限制器 / 流体 / 引信 → 镜像体 / 高热 / 猎犬，每日锁定、日切更新、按写端口判、突袭反制行、据点追加对策单位、情报行。
    /// E 标志组件专属行为（掉落 → 破解 → 蓝图量产见 FgEmgLootHintSelfCheck E2）：蓄热连射枪 / 全开散热鳍冠在己方机器上同一规则；研发树超频节点 comp: 解锁写进已掌握内容。
    /// F 镜像体（FGR-EMG-164）：核心固件发动 → 范围内镜像体复制（悬停可见）、同一枚不重复 / 范围外 / 冷却内不复制、复制品不能再被复制、读档后换复制新固件旧复制品让出带宽。
    /// G 存读档：内核快照段 34 往返哈希一致、读回后逐位相同、格式 20 旧档可读；战役存档保留反制锁定 / 已提示辐射区 / 镜像记录。H 暂停与 0.5x～3x（熔断引信按游戏秒）。
    /// K FG-GAP-225 教义自卫：排放会落在自己路上 / 己方建筑上时不开火并警告一次；负向（背向 / 普通枪照常开火）。I 反馈与图鉴；P 性能（一次测量）。
    /// FG-GAP-223（L 形墙追击）在 FgNavAttackSelfCheck（CheckRaiderChaseAroundWall）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOverclockFactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgOverclockFactionSelfCheck。
    /// </summary>
    public static class FgOverclockFactionSelfCheck
    {
        private const int Slot = 7;
        private const string Bench = "fg11fac01-bench";
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static int _radSeed = 42;

        [MenuItem("BinGames/QA/自检/FG 超频单位与精英")]
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
            Line("\n[超频单位与精英] 五类单位与失控超算体、冲刺停机 / 光环 / 熔断 / 蓄热排气 / 三态、高温辐射、反制、标志组件、镜像体、自卫避开自己的排放（FG11-FAC-01）");
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
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgoverclock-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameRoot.BindWorldProviders();
                NarrativeService.EnsureListening();
                OverclockCombatFeedback.ResetForTests();
                OverclockCounterService.ResetForTests();
                HeatRadiationService.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();
                TouchedHooks.Clear();
                GuidanceHooks.Touched += OnHookTouched;

                Step(CheckData);
                Step(CheckRaidGarrisonAndRange);
                Step(CheckHound);
                Step(CheckAura);
                Step(CheckFuser);
                Step(CheckRapidTurret);
                Step(CheckRunaway);
                Step(CheckRadiationKernel);
                Step(CheckRadiationWorld);
                Step(CheckGarrisonCounter);
                Step(CheckCounter);
                Step(CheckFlagBehaviors);
                Step(CheckResearchUnlock);
                Step(CheckMirror);
                Step(CheckMirrorSaveLoad);
                Step(CheckKernelSnapshot);
                Step(CheckSpeedMatrix);
                Step(CheckSaveLoad);
                Step(CheckSelfDefenseTrail);
                Step(CheckFeedbackAndCodex);
                Step(CheckPerf);
            }
            finally
            {
                GuidanceHooks.Touched -= OnHookTouched;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                SignalUplinkService.RealTimeForTests = null;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    CombatSites.Close(Bench, CampaignSession.Current, dropRecord: true);
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
            Line(_fail == 0 ? $"  超频单位与精英：全部 {_pass} 项通过" : $"  超频单位与精英：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        private static readonly HashSet<string> TouchedHooks = new HashSet<string>(StringComparer.Ordinal);

        private static void OnHookTouched(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                TouchedHooks.Add(id);
            }
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static readonly (string id, string behavior, string tier, string role)[] Units =
        {
            ("enemy_oc_hound", EnemyDefs.BehaviorDash, EnemyDefs.TierNormal, "assault"),
            ("enemy_oc_node", EnemyDefs.BehaviorAura, EnemyDefs.TierNormal, "support"),
            ("enemy_oc_fuser", EnemyDefs.BehaviorMelt, EnemyDefs.TierNormal, "suicide"),
            ("enemy_oc_turret", EnemyDefs.BehaviorRapid, EnemyDefs.TierNormal, "artillery"),
            ("enemy_oc_mirror", EnemyDefs.BehaviorMirror, EnemyDefs.TierNormal, "mimic"),
            ("enemy_oc_runaway", EnemyDefs.BehaviorOcCycle, EnemyDefs.TierElite, "assault"),
        };

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            var bad = new List<string>();
            EmgCompiled c = EmgCatalog.Current;
            foreach ((string id, string behavior, string tier, string role) in Units)
            {
                MechEnemy r = Row(id);
                if (r == null || r.Faction != EnemyDefs.FactionOverclock || r.Behavior != behavior || r.Tier != tier || r.Role != role
                    || !EnemyBehaviorReadings.TryGet(r.Behavior, out IEnemyBehaviorReading _) || !EnemyDefs.HasTag(r, EnemyDefs.TagAssembled)
                    || !EnemySpawnBuilder.TryAssembly(r, out string comp, out _) || !CarrierReadings.TryGetComponent(comp, out _) || !c.OwnerIndex.ContainsKey(id))
                {
                    bad.Add(id);
                }
            }
            var missingText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach ((string id, _, _, string role) in Units)
                {
                    MechEnemy r = Row(id);
                    foreach (string k in new[] { r?.NameKey, r?.DescKey, r?.WeaknessKey, "enemy.role." + role })
                    {
                        if (string.IsNullOrEmpty(k) || GameText.Get(k) == k)
                        {
                            missingText.Add(lang + ":" + k);
                        }
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool flags = new[] { "comp_heat_repeater", "struct_fin_crown" }.All(id => c.OwnerIndex.ContainsKey(id) && MechanicalContentFacade.TryGet(id, out MechanicalContentDef d) && d.Faction == "overclock");
            List<string> problems = EnemyDefs.AllProblems();
            Expect(bad.Count == 0 && missingText.Count == 0 && flags && problems.Count == 0,
                $"A1 超频 5 类单位 + 精英失控超算体入表（阵营 / 读法 / 层级 / 职能 / assembled 装配 / 端口声明，异常 {bad.Count}：{string.Join(",", bad)}）；中英文本缺 {missingText.Count}：{string.Join(",", missingText.Take(6))}；" +
                $"两个标志组件有端口声明与阵营键（{flags}）；全表行问题 {problems.Count}");
            var weak = new List<string>();
            foreach (string id in new[] { "enemy_oc_fuser", "enemy_oc_turret", "enemy_oc_runaway" })
            {
                MechEnemy r = Row(id);
                if (r == null || r.WarnSeconds < 0.8f)
                {
                    weak.Add(id + " " + r?.WarnSeconds);
                }
            }
            Expect(weak.Count == 0, $"A2 FGT-FAC-002 / 009（表层）：熔断 / 排气 / 三态切换预警 ≥ 0.8 秒（不合格 {weak.Count}：{string.Join("；", weak)}；内核实测见 B 段）");
        }

        private static void CheckRaidGarrisonAndRange()
        {
            string[] ids = { "overclock.oc_hound", "overclock.oc_fuser", "overclock.oc_node", "overclock.oc_mirror", "overclock.oc_runaway" };
            bool raid = ids.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.Faction == EnemyDefs.FactionOverclock && d.Points > 0 && d.ElitePoints > d.Points
                                       && Row(d.EnemyTypeId)?.Faction == EnemyDefs.FactionOverclock && SiegeCatalog.TryGetUnit(d.EnemyTypeId, out _));
            bool counters = new[] { ("overclock.limiter", "mirror"), ("overclock.fluid", "hot"), ("overclock.fuse", "hound") }
                .All(x => RaidCatalog.CounterById(x.Item1) is RaidCounter rc && rc.Faction == EnemyDefs.FactionOverclock && rc.Tag == x.Item2
                          && RaidCatalog.Units.Any(u => u.Faction == EnemyDefs.FactionOverclock && u.HasTag(x.Item2)));
            TerritoryControl tc = ConfigSystem.Instance.Tables.TbTerritoryControl.DataList.FirstOrDefault(r => r.Id == EnemyDefs.FactionOverclock);
            bool own = tc != null && tc.GarrisonUnits != "none" && tc.EliteUnits != "none"
                       && tc.GarrisonUnits.Split(',').Concat(tc.EliteUnits.Split(',')).All(u => RaidCatalog.TryGetUnit(u.Trim(), out RaidUnitDef d) && d.Faction == EnemyDefs.FactionOverclock)
                       && tc.EliteUnits.Contains("overclock.oc_runaway");
            var uncovered = Units.Where(u => !ConfigSystem.Instance.Tables.TbRangeTarget.DataList.Any(t => t.UnlockEnemies.Split(',').Any(e => e.Trim() == u.id))).Select(u => u.id).ToList();
            Expect(raid && counters && own && uncovered.Count == 0,
                $"A3 DEBT-FG6DEF04-04：突袭编成表 5 行超频单位都展开成本阵营敌人、有攻城参数（{raid}；突袭行为联验交 FG11-RAID-01）；反制三行标签有单位（{counters}）；" +
                $"DEBT-FG8EXP03-02：超频据点与精英残部编成全是本阵营单位、残部带失控超算体（{own}：{tc?.GarrisonUnits} / {tc?.EliteUnits}）；每种超频敌人都解锁靶子（未覆盖 {uncovered.Count}）");
        }

        // ─────────────────────────────── B 内核专属语法 ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
        }

        private static CombatSite OpenBench(CampaignState s)
        {
            CombatSites.Close(Bench, s, dropRecord: true);
            _t = 0;
            return CombatSites.Open(Bench, s, false, new BenchRules(), out _);
        }

        private static string BenchEnemy(CombatSite site, CampaignState s, string typeId, string id, Vector2 at)
        {
            MechEnemy row = Row(typeId);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = id, RegionId = Bench, EnemyTypeId = typeId, Position = at, Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != id).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(Bench), out CombatSpawn sp))
            {
                Fail("夹具：生成 " + typeId + " 失败");
                return id;
            }
            site.SpawnEnemy(rec, sp);
            return id;
        }

        private static int Player(CombatSite site, Vector2 at, CombatWeapon? wp, float hp = 400f, CombatUnitKind kind = CombatUnitKind.Machine, float speed = 4f, int profile = -1)
        {
            int w = wp.HasValue ? site.WeaponIndex(wp.Value) : -1;
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = kind, Faction = CombatFaction.Player, Behavior = kind == CombatUnitKind.Machine ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (wp.HasValue ? CombatUnitFlags.WeaponEnabled : 0), Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Speed = kind == CombatUnitKind.Machine ? speed : 0f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = profile, ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, 1f),
            });
        }

        private static CombatWeapon Gun(float dmg = 10f, float cd = 0.05f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 40f, Damage = dmg, Cooldown = cd, TargetMode = CombatTargetMode.Nearest,
        };

        private static void MoveTo(CombatSite site, int unit, Vector2 to) =>
            site.Kernel.IssueCommand(unit, CombatCommandKind.Move, new double2(to.x, to.y), 0, 0.5f, 0f, 0f, false);

        private static double _t;

        private static void RunFor(CombatSite site, float seconds)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 0; i < n; i++)
            {
                _t += Dt;
                site.Step(Dt, _t);
            }
        }

        private static CombatUnitView View(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static float Hp(CombatSite site, int unit) => View(site, unit).Health;

        private static int UnitOf(CombatSite site, string enemyId) => site.TryGetEnemyUnit(enemyId, out int u) ? u : 0;

        private static CombatOverclock Oc(CombatSite site, int unit) => site.Kernel.TryGetOverclock(unit, out CombatOverclock r) ? r : default;

        private static double2 PosOf(CombatSite site, int unit) => View(site, unit).Position;

        /// <summary>扣一次 <paramref name="amount"/> 的直接伤害，返回实际掉的血。</summary>
        private static float Hit(CombatSite site, int unit, float amount)
        {
            float before = Hp(site, unit);
            site.Kernel.Damage(unit, amount, 0);
            return before - Hp(site, unit);
        }

        private static void CheckHound()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_hound", "h1", Vector2.zero);
            int hound = UnitOf(site, "h1");
            int prey = Player(site, new Vector2(10f, 0f), null, 9999f, CombatUnitKind.Machine, 5f);
            MoveTo(site, prey, new Vector2(500f, 0f));
            float normal = Hit(site, hound, 4f);
            double2 p0 = PosOf(site, hound);
            RunFor(site, 1f);
            double moved1 = math.distance(PosOf(site, hound), p0);
            int shutCues = OverclockCombatFeedback.ShutdownCues;
            float tShut = -1f;
            for (int i = 0; i < 60 * 9 && tShut < 0f; i++)
            {
                RunFor(site, Dt);
                if (Oc(site, hound).State == CombatOverclockConst.StateShutdown)
                {
                    tShut = (float)_t;
                }
            }
            double2 ps = PosOf(site, hound);
            float vuln = Hit(site, hound, 4f);
            RunFor(site, 1f);
            bool still = math.distance(PosOf(site, hound), ps) < 1e-3 && Oc(site, hound).State == CombatOverclockConst.StateShutdown;
            RunFor(site, 1.7f);
            bool recovered = Oc(site, hound).State == CombatOverclockConst.StateNormal;
            Expect(moved1 >= 3f * 1.5f && tShut > 3f && tShut < 7f && still && recovered && OverclockCombatFeedback.ShutdownCues > shutCues
                   && Mathf.Abs(normal - 4f) < 0.01f && Mathf.Abs(vuln - 6f) < 0.01f,
                $"B1 超频猎犬冲刺：第 1 秒跑 {moved1:0.#} 米（基础速度 3 × 冲刺 1.8）；冲刺满热 {tShut:0.#} 秒强制停机（声音 + 字幕 {OverclockCombatFeedback.ShutdownCues - shutCues} 次），" +
                $"停机 1 秒不动（{still}）、受伤 {normal:0.#} → {vuln:0.#}（×1.5），2.5 秒后恢复（{recovered}）");

            // 死亡泄爆：伤到周围所有单位，包括它的友军（FGR-FAC-003）
            double2 hp = PosOf(site, hound);
            BenchEnemy(site, s, "enemy_oc_node", "h_buddy", new Vector2((float)hp.x + 1.5f, (float)hp.y));
            int buddy = UnitOf(site, "h_buddy");
            int near = Player(site, new Vector2((float)hp.x, (float)hp.y + 1.5f), null, 9999f, CombatUnitKind.Structure);
            int far = Player(site, new Vector2((float)hp.x, (float)hp.y + 12f), null, 9999f, CombatUnitKind.Structure);
            float b0 = Hp(site, buddy), n0 = Hp(site, near), f0 = Hp(site, far);
            int bursts = OverclockCombatFeedback.BurstCues;
            site.Kernel.Kill(hound, 0);
            RunFor(site, 3 * Dt);
            float bh = b0 - Hp(site, buddy), nh = n0 - Hp(site, near), fh = f0 - Hp(site, far);
            Expect(bh > 1f && nh > 1f && fh < 1e-3f && OverclockCombatFeedback.BurstCues > bursts && !site.Kernel.TryGetOverclock(hound, out _),
                $"B2 猎犬死亡泄爆：1.5 米内友军（算力节点）挨 {bh:0.#}、己方建筑挨 {nh:0.#}，12 米外不挨（{fh:0.#}）；泄爆提示 {OverclockCombatFeedback.BurstCues - bursts} 次");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static float AuraDamage(int nodes)
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_turret", "a_turret", Vector2.zero);
            for (int i = 0; i < nodes; i++)
            {
                BenchEnemy(site, s, "enemy_oc_node", "a_node" + i, new Vector2(-3f, i * 2f));
            }
            int target = Player(site, new Vector2(12f, 0f), null, 99999f, CombatUnitKind.Structure);
            float h0 = Hp(site, target);
            RunFor(site, 4f);
            float dmg = h0 - Hp(site, target);
            CombatSites.Close(Bench, s, dropRecord: true);
            return dmg;
        }

        private static void CheckAura()
        {
            float d0 = AuraDamage(0), d1 = AuraDamage(1), d2 = AuraDamage(2);
            Expect(d0 > 0f && d1 >= d0 * 1.25f && Mathf.Abs(d2 - d1) < 1e-3f,
                $"B3 算力光环：过热炮台 4 秒对同一目标的伤害 无节点 {d0:0.#} → 1 个节点（8 米内）{d1:0.#}（射速 +50%）→ 2 个节点 {d2:0.#}（不叠加）");
            CheckAuraHeat();
        }

        /// <summary>新炮台 F（节点 6 米内）对只有它够得着的目标 Y 打 2.5 秒的伤害。node: 0 = 没有节点，1 = 节点正常，2 = 先用 3 台炮台把节点供到满热排气再放 F。</summary>
        private static float FreshTurretDamage(int node, out bool vented, out float nodeHeatEarly, out float vuln, out bool recovered)
        {
            vented = false;
            nodeHeatEarly = 0f;
            vuln = 0f;
            recovered = false;
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            int y = Player(site, new Vector2(0f, -19f), null, 99999f, CombatUnitKind.Structure);
            int n = 0;
            if (node > 0)
            {
                BenchEnemy(site, s, "enemy_oc_node", "ah_node", Vector2.zero);
                n = UnitOf(site, "ah_node");
            }
            if (node == 2)
            {
                Player(site, new Vector2(14f, 0f), null, 99999f, CombatUnitKind.Structure);
                for (int i = 0; i < 3; i++)
                {
                    BenchEnemy(site, s, "enemy_oc_turret", "ah_t" + i, new Vector2(2f, (i - 1) * 2f));
                }
                for (int i = 0; i < 60 * 20 && !vented; i++)
                {
                    RunFor(site, Dt);
                    if (i == 60)
                    {
                        nodeHeatEarly = Oc(site, n).Heat;
                    }
                    vented = Oc(site, n).State == CombatOverclockConst.StateVent;
                }
            }
            BenchEnemy(site, s, "enemy_oc_turret", "ah_f", new Vector2(0f, -6f));
            float h0 = Hp(site, y);
            RunFor(site, 2.5f);
            float dmg = h0 - Hp(site, y);
            if (node == 2)
            {
                bool stillVent = Oc(site, n).State == CombatOverclockConst.StateVent;
                vuln = stillVent ? Hit(site, n, 10f) : 0f;
                RunFor(site, 0.8f);
                recovered = Oc(site, n).State == CombatOverclockConst.StateNormal;
            }
            CombatSites.Close(Bench, s, dropRecord: true);
            return dmg;
        }

        private static void CheckAuraHeat()
        {
            // 负向：节点自己开火、身边没有超频单位 → 不积热（积热只来自“供算力”）
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_node", "ah_solo", Vector2.zero);
            int solo = UnitOf(site, "ah_solo");
            int bait = Player(site, new Vector2(6f, 0f), null, 99999f, CombatUnitKind.Structure);
            float b0 = Hp(site, bait);
            RunFor(site, 5f);
            bool soloFired = Hp(site, bait) < b0 - 1e-3f;
            float soloHeat = Oc(site, solo).Heat;
            CombatSites.Close(Bench, s, dropRecord: true);

            float alone = FreshTurretDamage(0, out _, out _, out _, out _);
            float buffed = FreshTurretDamage(1, out _, out _, out _, out _);
            float during = FreshTurretDamage(2, out bool vented, out float early, out float vuln, out bool recovered);
            Expect(soloFired && soloHeat < 1e-4f && early > 0f && vented && buffed > alone + 1e-3f && Mathf.Abs(during - alone) < 1e-3f
                   && Mathf.Abs(vuln - 15f) < 0.01f && recovered,
                $"B3b 算力节点供算力积热（en.heat 写端，FGR-EMG-014）：自己开火不积热（开火 {soloFired}、热 {soloHeat:0.###}）；3 台炮台吃光环 1 秒后节点热 {early:0.##}，满热排气（{vented}）；" +
                $"新炮台 2.5 秒伤害 无节点 {alone:0.#} / 节点正常 {buffed:0.#} / 节点排气中 {during:0.#}（光环中断 = 无节点）；排气中受伤 10 → {vuln:0.#}（×1.5），之后恢复（{recovered}）");
        }

        private static void CheckFuser()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_fuser", "f1", Vector2.zero);
            BenchEnemy(site, s, "enemy_oc_node", "f_buddy", new Vector2(0f, 3f));
            int fuser = UnitOf(site, "f1");
            int buddy = UnitOf(site, "f_buddy");
            float max = View(site, fuser).MaxHealth;
            int warn = OverclockCombatFeedback.MeltWarnCues;
            Hit(site, fuser, max * 0.6f);
            RunFor(site, 3 * Dt);
            bool notYet = Oc(site, fuser).State == CombatOverclockConst.StateNormal; // 40% 还没到熔断线
            Hit(site, fuser, max * 0.15f);
            RunFor(site, Dt);
            bool melting = Oc(site, fuser).State == CombatOverclockConst.StateMelting;
            float b0 = Hp(site, buddy);
            RunFor(site, 1.8f);
            bool aliveMid = site.Kernel.IsAlive(fuser) && Hp(site, buddy) >= b0 - 1e-3f;
            RunFor(site, 0.4f);
            bool blown = !site.Kernel.IsAlive(fuser);
            float bh = b0 - Hp(site, buddy);
            Expect(notYet && melting && OverclockCombatFeedback.MeltWarnCues > warn && aliveMid && blown && bh > 1f,
                $"B4 熔断者：耐久 40% 不熔断（{notYet}），跌到 25% 开始熔断（{melting}，预警提示 {OverclockCombatFeedback.MeltWarnCues - warn} 次），1.8 秒时还没爆（{aliveMid}），2 秒后自爆（{blown}）、3 米内友军挨 {bh:0.#}");

            // 卡片“一口气打死”：熔断引信期间被击毁 = 熔断中止、不爆
            BenchEnemy(site, s, "enemy_oc_fuser", "f2", new Vector2(20f, 0f));
            BenchEnemy(site, s, "enemy_oc_node", "f_buddy2", new Vector2(20f, 3f));
            int f2 = UnitOf(site, "f2");
            int buddy2 = UnitOf(site, "f_buddy2");
            Hit(site, f2, View(site, f2).MaxHealth * 0.75f);
            RunFor(site, Dt);
            bool melting2 = Oc(site, f2).State == CombatOverclockConst.StateMelting;
            float c0 = Hp(site, buddy2);
            int bursts = OverclockCombatFeedback.BurstCues;
            RunFor(site, 1f);
            site.Kernel.Kill(f2, 0);
            RunFor(site, 2f);
            Expect(melting2 && Hp(site, buddy2) >= c0 - 1e-3f && OverclockCombatFeedback.BurstCues == bursts && !site.Kernel.TryGetOverclock(f2, out _),
                $"B5 负向：熔断中被一口气打死不爆（友军耐久 {c0:0.#} → {Hp(site, buddy2):0.#}，泄爆提示 +{OverclockCombatFeedback.BurstCues - bursts}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckRapidTurret()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_turret", "t1", Vector2.zero);
            int turret = UnitOf(site, "t1");
            int target = Player(site, new Vector2(10f, 0f), null, 99999f, CombatUnitKind.Structure);
            var shots = new List<float>();
            float last = Hp(site, target);
            int vents = OverclockCombatFeedback.VentCues;
            float tVent = -1f;
            for (int i = 0; i < 60 * 15 && tVent < 0f; i++)
            {
                RunFor(site, Dt);
                float h = Hp(site, target);
                if (h < last - 1e-3f)
                {
                    shots.Add((float)_t);
                    last = h;
                }
                if (Oc(site, turret).State == CombatOverclockConst.StateVent)
                {
                    tVent = (float)_t;
                }
            }
            float vuln = Hit(site, turret, 10f);
            float hv = Hp(site, target);
            RunFor(site, 2.8f);
            bool silent = Hp(site, target) >= hv - 1e-3f && Oc(site, turret).State == CombatOverclockConst.StateVent;
            RunFor(site, 0.4f);
            bool back = Oc(site, turret).State == CombatOverclockConst.StateNormal;
            float first = shots.Count >= 3 ? shots[1] - shots[0] : 0f;
            float lastGap = shots.Count >= 3 ? shots[shots.Count - 1] - shots[shots.Count - 2] : 0f;
            Expect(tVent > 0f && shots.Count >= 8 && lastGap < first * 0.75f && silent && back && Mathf.Abs(vuln - 16f) < 0.01f && OverclockCombatFeedback.VentCues > vents,
                $"B6 过热炮台蓄热连射：越打越快（第一个间隔 {first:0.00} 秒 → 最后 {lastGap:0.00} 秒，共 {shots.Count} 发）、满热 {tVent:0.#} 秒排气（提示 {OverclockCombatFeedback.VentCues - vents} 次），" +
                $"排气 3 秒不开火（{silent}）、受伤 ×1.6（10 → {vuln:0.#}），之后恢复（{back}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckRunaway()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_runaway", "r1", Vector2.zero);
            int run = UnitOf(site, "r1");
            int prey = Player(site, new Vector2(12f, 0f), null, 999999f, CombatUnitKind.Machine, 6f);
            MoveTo(site, prey, new Vector2(900f, 0f));
            int warns = OverclockCombatFeedback.ModeWarnCues, modes = OverclockCombatFeedback.ModeCues;
            RunFor(site, 3f);
            float lean = Oc(site, run).Lean;
            float tWarn = -1f, tMode = -1f;
            for (int i = 0; i < 60 * 8 && tMode < 0f; i++)
            {
                RunFor(site, Dt);
                if (tWarn < 0f && OverclockCombatFeedback.ModeWarnCues > warns)
                {
                    tWarn = (float)_t;
                }
                if (Oc(site, run).Mode == CombatOverclockConst.ModeAssault)
                {
                    tMode = (float)_t;
                }
            }
            float leanAfter = Oc(site, run).Lean;
            for (int i = 0; i < 60 * 10 && Oc(site, run).Mode != CombatOverclockConst.ModeShield; i++)
            {
                RunFor(site, Dt);
            }
            CombatOverclock r = Oc(site, run);
            float expect = 10f * 0.5f * (r.State == CombatOverclockConst.StateShutdown ? 1.5f : 1f);
            float shield = Hit(site, run, 10f);
            Expect(lean > 0.5f && tWarn > 0f && tMode > 0f && tMode - tWarn >= 0.8f - 1e-3f && leanAfter < 0.1f && r.Mode == CombatOverclockConst.ModeShield
                   && Mathf.Abs(shield - expect) < 0.01f && OverclockCombatFeedback.ModeCues >= modes + 2,
                $"B7 失控超算体三态：极速态直线跑 3 秒前倾 {lean:0.#} 秒（越跑越快）；{tWarn:0.#} 秒预警、{tMode:0.#} 秒切强攻（提前 {tMode - tWarn:0.0#} 秒 ≥ 0.8）、切换时前倾归零（{leanAfter:0.##}）；" +
                $"再切护盾态受伤减半（10 → {shield:0.#}）；切换提示 {OverclockCombatFeedback.ModeCues - modes} 次");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── C 高温辐射 ───────────────────────────────

        private static void CheckRadiationKernel()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            site.SetHeatRadiation(new List<float4> { new float4(0f, 0f, 0f, 10f) }, HeatRadiationService.CapMult, HeatRadiationService.DissipationMult);
            CombatWeapon hot = Gun(1f, 0.01f);
            hot.HeatPerShot = 10f;
            hot.OverheatAt = 100f;
            hot.Dissipation = 0f;
            int inside = Player(site, new Vector2(2f, 0f), hot, 9999f);
            int outside = Player(site, new Vector2(30f, 0f), hot, 9999f);
            BenchEnemy(site, s, "enemy_oc_mirror", "c_enemy", new Vector2(0f, 4f));
            int enemy = UnitOf(site, "c_enemy");
            int dummyIn = Player(site, new Vector2(2f, 6f), null, 1f, CombatUnitKind.Structure); // 只当靶子的位置参照
            site.Kernel.Despawn(dummyIn);
            BenchEnemy(site, s, "enemy_oc_node", "c_target", new Vector2(15f, 0f));
            int target = UnitOf(site, "c_target");
            int ShotsUntilOverheat(int shooter)
            {
                int n = 0;
                for (int i = 0; i < 20; i++)
                {
                    CombatFireResult fr = site.Kernel.FireAt(shooter, target, _t);
                    if (fr != CombatFireResult.Ok && fr != CombatFireResult.Launched)
                    {
                        break;
                    }
                    n++;
                    _t += 0.02;
                }
                return n;
            }
            int nIn = ShotsUntilOverheat(inside), nOut = ShotsUntilOverheat(outside);
            float capIn = site.Kernel.OverheatAtFor(inside), capOut = site.Kernel.OverheatAtFor(outside);
            float dIn = site.Kernel.DissipationFactorFor(inside), dOut = site.Kernel.DissipationFactorFor(outside), dEnemy = site.Kernel.DissipationFactorFor(enemy);
            Expect(Mathf.Approximately(capIn, 75f) && Mathf.Approximately(capOut, 100f) && Mathf.Approximately(dIn, 0.8f) && Mathf.Approximately(dOut, 1f) && Mathf.Approximately(dEnemy, 0.8f)
                   && nIn < nOut && nOut >= 9,
                $"C1 高温辐射（热量上限 -25%、散热 -20%，敌我同一规则）：区内过热点 {capIn:0.#}（区外 {capOut:0.#}）、散热 ×{dIn:0.##}（区外 ×{dOut:0.##}、区内敌人 ×{dEnemy:0.##}）；" +
                $"同一把枪区内 {nIn} 发过热、区外 {nOut} 发");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckRadiationWorld()
        {
            CampaignState s = null;
            IReadOnlyList<float4> shapes = null;
            foreach (int seed in new[] { 42, 1, 2, 3, 5, 7, 11 })
            {
                s = NewWorld(seed);
                shapes = HeatRadiationService.ActiveShapes(s);
                if (shapes.Count > 0)
                {
                    _radSeed = seed;
                    break;
                }
            }
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionOverclock);
            bool hasZone = shapes != null && shapes.Count > 0 && home != null && home.Kernel.HeatRadiationZoneCount == shapes.Count;
            string zoneId = hasZone ? HeatRadiationService.ZoneAt(s, new Vector2(shapes[0].x + (shapes[0].z + shapes[0].w) * 0.5f, shapes[0].y)) : null;
            string hover = HeatRadiationService.HoverLine();
            Expect(t != null && hasZone && zoneId != null && hover.Contains("25") && hover.Contains("20"),
                $"C2 规划层高温辐射区（种子 {_radSeed}，超频领地 {t?.Id}）写进星球内核（{home?.Kernel.HeatRadiationZoneCount} / {shapes?.Count} 片），与地图悬停同一判定（{zoneId}）；" +
                $"悬停“生效中”文本：{hover}（正式热寂数据城领地里的联验交 FG11-REG-01，DEBT-FG11FAC01-02）");
        }

        // ─────────────────────────────── D 反制（FGT-FAC-005） ───────────────────────────────

        private const string BpLimiter = "bp_fg11fac01_limiter";
        private const string BpFluid = "bp_fg11fac01_fluid";
        private const string BpFuse = "bp_fg11fac01_fuse";
        private const string BpPlain = "bp_fg11fac01_plain";

        private static void MakeBlueprints(CampaignState s)
        {
            void Add(string id, params string[] firmware)
            {
                if (s.BlueprintRecords.Any(b => b.BlueprintId == id))
                {
                    return;
                }
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, firmware);
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) },
                }).ToArray();
            }
            Add(BpLimiter, "fw_homing", "fw_pierce");
            Add(BpFluid, "fw_burntrail");
            Add(BpFuse, "fw_split");
            Add(BpPlain);
        }

        private static int SpawnIn(CampaignState s, string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, HomeValleyLayout.RegionId, at, 400f, 400f, "Player", 1);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            else
            {
                Fail("夹具：登记机器失败 " + r.Message);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static void SetFleet(CampaignState s, string bp, int count)
        {
            foreach (MachineRecord rec in MachineRegistry.AllRecords.ToList())
            {
                if (rec != null && rec.IsAlive)
                {
                    MachineRegistry.MarkDeadByLogicId(rec.LogicId);
                }
            }
            for (int i = 0; i < count; i++)
            {
                SpawnIn(s, bp, new Vector2(i * 3f, 5f));
            }
        }

        private static void CheckGarrisonCounter()
        {
            CampaignState s = NewWorld(1);
            s.Factions.OverclockCounterId = string.Empty;
            s.Factions.OverclockCounterDay = Math.Max(1, s.Clock.Day);
            var types = new List<string>();
            TerritoryControlService.GarrisonEnemyTypes(EnemyDefs.FactionOverclock, 10, types);
            bool own = types.Count == 10 && types.All(id => Row(id)?.Faction == EnemyDefs.FactionOverclock);
            s.Factions.OverclockCounterId = OverclockCounterService.CounterLimiter;
            var lim = new List<string>();
            int applied = OverclockCounterService.Applied;
            TerritoryControlService.GarrisonEnemyTypes(EnemyDefs.FactionOverclock, 10, lim);
            int m0 = types.Count(x => x == "enemy_oc_mirror"), m1 = lim.Count(x => x == "enemy_oc_mirror");
            Expect(own && lim.Count == 10 && m1 > m0 && OverclockCounterService.Applied > applied,
                $"D0 超频据点驻军展开的是本阵营单位（{string.Join(",", types.Distinct())}）；反制锁定“镜像压制”后多了镜像体（{m0} → {m1} 台，总数 {lim.Count} 不变）");
            s.Factions.OverclockCounterId = string.Empty;
        }

        private static void CheckCounter()
        {
            CampaignState s = NewWorld(1);
            MakeBlueprints(s);
            SetFleet(s, BpLimiter, 3);
            OverclockCounterService.Shares(s, out float lim, out float fl0, out float fu0);
            string limDecide = OverclockCounterService.Decide(s);
            string locked = OverclockCounterService.Locked(s);
            SetFleet(s, BpFluid, 3);
            string fluidDecide = OverclockCounterService.Decide(s);
            string stillLocked = OverclockCounterService.Locked(s);
            s.Clock.Day += 1;
            OverclockCounterService.WorldStep(s);
            string nextDay = OverclockCounterService.Locked(s);
            SetFleet(s, BpFuse, 3);
            string fuseDecide = OverclockCounterService.Decide(s);
            SetFleet(s, BpPlain, 3);
            string plain = OverclockCounterService.Decide(s);
            Expect(lim >= 0.99f && fl0 == 0f && fu0 == 0f && limDecide == OverclockCounterService.CounterLimiter && locked == limDecide && fluidDecide == OverclockCounterService.CounterFluid
                   && stillLocked == locked && nextDay == OverclockCounterService.CounterFluid && fuseDecide == OverclockCounterService.CounterFuse && plain == OverclockCounterService.CounterNone,
                $"D1 FGT-FAC-005：限制器构筑（语法族占比 {lim:0.00}）→ 镜像压制（{limDecide}），当天锁定；改成燃迹当天不变（{stillLocked}），日切后锁定为高热蒸发（{nextDay}）；" +
                $"引信构筑 → 猎犬群（{fuseDecide}）；没有固件 → 没有对策（{(plain == string.Empty ? "无" : plain)}）");

            // 按写端口判，不按固件 ID：燃迹的写端口改成 en.kinetic → 引信族
            SetFleet(s, BpFluid, 3);
            EmgSource swapped = EmgCatalog.FromTables();
            foreach (EmgSource.Decl dc in swapped.Decls.Where(dc => dc.Owner == "fw_burntrail" && dc.Dir == "write"))
            {
                dc.Port = "en.kinetic";
            }
            EmgCatalog.OverrideForTests(swapped);
            string portSwapped = OverclockCounterService.Decide(s);
            EmgCatalog.ResetForTests();
            string portBack = OverclockCounterService.Decide(s);
            SetFleet(s, BpPlain, 3);
            Expect(portSwapped == OverclockCounterService.CounterFuse && portBack == OverclockCounterService.CounterFluid,
                $"D2 反制按端口族：燃迹声明改写 en.kinetic → {portSwapped}，还原 → {portBack}");

            RaidCounter rc = OverclockCounterService.RaidCounterFor(OverclockCounterService.Locked(s));
            IntelRecord ir1 = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            bool hiddenAct1 = s.Progress == null || s.Progress.Act >= 2 || (ir1 != null && !ir1.HasOverclockCounter);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            HomeExplorationService.ResetForTests();
            IntelRecord ir = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            string sum = ir != null ? IntelService.Summary(s, ir, GameClock.Ticks) : string.Empty;
            bool intel = ir != null && ir.HasOverclockCounter && ir.OverclockCounter == OverclockCounterService.CounterFluid
                         && sum.Contains(OverclockCounterService.CounterName(OverclockCounterService.CounterFluid));
            var plan = new RaidPlanRecord { PlanId = "fg11fac01", Faction = EnemyDefs.FactionOverclock };
            RaidDirectorService.ChooseCounterForTests(s, plan); // 与突袭排定同一个函数（ChooseCounter）
            Expect(rc != null && rc.Id == OverclockCounterService.CounterFluid && rc.Tag == "hot" && plan.Counter == rc.Id && intel && hiddenAct1,
                $"D3 突袭排定按当天锁定选反制行（{rc?.Id} → 标签 {rc?.Tag}；计划写 {plan.Counter}）；第一幕情报不写超频（{hiddenAct1}），第二幕起“敌方反制预览”写出超频今天的锁定与预测：{sum}");
        }

        // ─────────────────────────────── E 标志组件 / 研发 ───────────────────────────────

        private static void CheckFlagBehaviors()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            site.Kernel.TryGetProfile(site.FlagBehaviorProfile("comp_heat_repeater", null), out CombatBehaviorProfile rp);
            site.Kernel.TryGetProfile(site.FlagBehaviorProfile(ComponentCatalog.CompGunId, "struct_fin_crown"), out CombatBehaviorProfile fp);
            bool rapidParams = rp.OcRampHeat > 0f && rp.OcRampMax > 1f && rp.OcShutdown >= 0.8f && rp.OcVuln > 0f;
            bool finParams = Mathf.Approximately(fp.OcDashSpeed, 1.5f) && Mathf.Approximately(fp.OcDashHeat, 0.2f) && Mathf.Approximately(fp.OcShutdown, 2.5f) && Mathf.Approximately(fp.OcVuln, 0.5f)
                             && fp.LurkSeconds <= 0f;

            // 全开散热鳍冠：移动 ×1.5、满热停机 2.5 秒（不动、受伤 +50%）、停下来热量才会退
            int fin = Player(site, Vector2.zero, null, 9999f, CombatUnitKind.Machine, 4f, site.FlagBehaviorProfile(ComponentCatalog.CompGunId, "struct_fin_crown"));
            MoveTo(site, fin, new Vector2(500f, 0f));
            RunFor(site, 1f);
            double moved = math.distance(PosOf(site, fin), double2.zero);
            float tShut = -1f;
            for (int i = 0; i < 60 * 8 && tShut < 0f; i++)
            {
                RunFor(site, Dt);
                if (Oc(site, fin).State == CombatOverclockConst.StateShutdown)
                {
                    tShut = (float)_t;
                }
            }
            double2 ps = PosOf(site, fin);
            float vuln = Hit(site, fin, 4f);
            RunFor(site, 1f);
            bool still = math.distance(PosOf(site, fin), ps) < 1e-3;
            Expect(rapidParams && finParams && moved >= 4f * 1.4f && tShut > 0f && still && Mathf.Abs(vuln - 6f) < 0.01f,
                $"E1 全开散热鳍冠（FG-GAP-150 超频）：参数来自端口声明幅度（冲刺 ×{fp.OcDashSpeed}、每秒积热 {fp.OcDashHeat}、停机 {fp.OcShutdown} 秒、受伤 +{fp.OcVuln * 100f:0}%，不再误读成潜伏 {fp.LurkSeconds <= 0f}）；" +
                $"己方机器第 1 秒跑 {moved:0.#} 米（基础 4），{tShut:0.#} 秒满热停机、停机不动（{still}）、受伤 4 → {vuln:0.#}");

            // 蓄热连射枪：己方机器同一条蓄热 → 排气规则
            int gunner = Player(site, new Vector2(0f, 20f), Gun(1f, 1f), 9999f, CombatUnitKind.Machine, 0f, site.FlagBehaviorProfile("comp_heat_repeater", null));
            int dummy = HostileDummy(site, new Vector2(8f, 20f), 99999f);
            site.Kernel.IssueCommand(gunner, CombatCommandKind.Attack, new double2(8, 20), dummy, 0.5f, 30f, 1f, false);
            float tVent = -1f;
            for (int i = 0; i < 60 * 14 && tVent < 0f; i++)
            {
                RunFor(site, Dt);
                if (Oc(site, gunner).State == CombatOverclockConst.StateVent)
                {
                    tVent = (float)_t;
                }
            }
            float h0 = Hp(site, dummy);
            RunFor(site, rp.OcShutdown - 0.2f);
            bool held = Hp(site, dummy) >= h0 - 1e-3f;
            Expect(tVent > 0f && held,
                $"E2 蓄热连射枪：己方机器每发积热（{rp.OcRampHeat}/发，射速最高 ×{rp.OcRampMax}），{tVent:0.#} 秒满热排气、排气期间不开火（{held}）");
            CombatSites.Close(Bench, s, dropRecord: true);

            // E2b 审查修复：守点与教义自卫两条自动开火路径（CombatSiege.StepGuard）同样乘超频冷却倍率——越打越快，不是只积热
            (float first, float last, int shots) AutoFire(bool guard)
            {
                CampaignState s2 = NewWorldState(1);
                CombatSite b = OpenBench(s2);
                int g = Player(b, Vector2.zero, Gun(1f, 1f), 9999f, CombatUnitKind.Machine, 0f, b.FlagBehaviorProfile("comp_heat_repeater", null));
                if (guard)
                {
                    b.Kernel.SetMachineGuard(g, double2.zero, 12f);
                }
                else
                {
                    b.Kernel.SetMachineSelfDefense(g, true);
                }
                int e = HostileDummy(b, new Vector2(6f, 0f), 99999f);
                var times = new List<float>();
                float hp = Hp(b, e);
                for (int k = 0; k < 60 * 14 && Oc(b, g).State != CombatOverclockConst.StateVent; k++)
                {
                    RunFor(b, Dt);
                    float h = Hp(b, e);
                    if (h < hp - 1e-3f)
                    {
                        times.Add((float)_t);
                        hp = h;
                    }
                }
                CombatSites.Close(Bench, s2, dropRecord: true);
                return times.Count >= 3 ? (times[1] - times[0], times[times.Count - 1] - times[times.Count - 2], times.Count) : (0f, 0f, times.Count);
            }
            var sd = AutoFire(false);
            var gd = AutoFire(true);
            Expect(sd.shots >= 3 && sd.last < sd.first * 0.9f && gd.shots >= 3 && gd.last < gd.first * 0.9f,
                $"E2b 蓄热连射枪走自动开火路径同样提速：教义自卫 {sd.shots} 发、第一段间隔 {sd.first:0.###} 秒 → 排气前最后一段 {sd.last:0.###} 秒；" +
                $"守点 {gd.shots} 发、{gd.first:0.###} → {gd.last:0.###} 秒（与编队攻击同一口径 CombatLogic.UnitCooldown）");
        }

        private static void CheckResearchUnlock()
        {
            CampaignState s = NewWorld(1);
            bool before = MechanicalContentUnlock.IsUnlocked(s, "comp_heat_repeater") || MechanicalContentUnlock.IsUnlocked(s, "struct_fin_crown");
            BlueprintCircuitBoard b0 = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            bool refused = !b0.TrySetPrimary(s, "comp_heat_repeater").Success;
            ResearchService.CompleteForTests(s, "overclock.component", "overclock.module");
            bool after = MechanicalContentUnlock.IsUnlocked(s, "comp_heat_repeater") && MechanicalContentUnlock.IsUnlocked(s, "struct_fin_crown");
            BlueprintCircuitBoard b1 = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            bool primary = b1.TrySetPrimary(s, "comp_heat_repeater").Success;
            bool structure = b1.TrySetStructure(s, "struct_fin_crown").Success;
            bool done = ResearchService.IsCompleted(s, "overclock.component") && ResearchService.IsCompleted(s, "overclock.module");
            Expect(!before && refused && done && after && primary && structure,
                $"E3 DEBT-FG5RND01-01（超频分支）：研发“超频作战组件 / 结构模块”完成 = 标志组件进已掌握内容（研发前 {before}、写不进蓝图 {refused}；研发后 {after}，蓝图主组件 {primary} / 结构模块 {structure}）");
        }

        // ─────────────────────────────── F 镜像体 ───────────────────────────────

        private static void CheckMirror()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_mirror", "m1", Vector2.zero);
            int mirror = UnitOf(site, "m1");
            RegionEnemyRecord rec = s.RegionEnemies.First(e => e.EnemyInstanceId == "m1");
            int nearP = Player(site, new Vector2(5f, 0f), Gun(), 9999f, CombatUnitKind.Machine, 0f);
            int farP = Player(site, new Vector2(40f, 0f), Gun(), 9999f, CombatUnitKind.Machine, 0f);
            string hoverNone = site.MirrorHoverLine(s, mirror);
            int c0 = CombatSite.MirrorDetectCopies;
            int hooks = TouchedHooks.Contains("overclock.first_mirror") ? 1 : 0;
            string fwA = MechanicalReactionCatalog.TriggerFirmwareOf(MechanicalReactionCatalog.ReactionMarkJumpId);
            string fwB = MechanicalReactionCatalog.TriggerFirmwareOf(MechanicalReactionCatalog.ReactionMeltOverloadId);
            site.OnDeclCoreReaction(s, farP, MechanicalReactionCatalog.ReactionMarkJumpId, 1, new Vector2(40f, 0f));
            bool farNo = CombatSite.MirrorDetectCopies == c0 && string.IsNullOrEmpty(rec.MirrorCopyFirmware);
            site.OnDeclCoreReaction(s, nearP, MechanicalReactionCatalog.ReactionMarkJumpId, 1, new Vector2(5f, 0f));
            bool copied = CombatSite.MirrorDetectCopies == c0 + 1 && rec.MirrorCopyFirmware == fwA && site.UnitHoldsCopy(mirror) && rec.MirrorCopyHeld > 0
                          && site.Kernel.FlashUsed(mirror) == rec.MirrorCopyHeld;
            string hover = site.MirrorHoverLine(s, mirror);
            site.OnDeclCoreReaction(s, nearP, MechanicalReactionCatalog.ReactionMarkJumpId, 1, new Vector2(5f, 0f));
            bool sameNo = CombatSite.MirrorDetectCopies == c0 + 1;
            site.OnDeclCoreReaction(s, nearP, MechanicalReactionCatalog.ReactionMeltOverloadId, 1, new Vector2(5f, 0f));
            bool cooldownNo = CombatSite.MirrorDetectCopies == c0 + 1 && rec.MirrorCopyFirmware == fwA;
            RunFor(site, 4.2f);
            site.OnDeclCoreReaction(s, nearP, MechanicalReactionCatalog.ReactionMeltOverloadId, 1, new Vector2(5f, 0f));
            bool swapped = CombatSite.MirrorDetectCopies == c0 + 2 && rec.MirrorCopyFirmware == fwB && site.Kernel.FlashUsed(mirror) == rec.MirrorCopyHeld;
            string nameA = FirmwareKinds.DisplayName(fwA) ?? fwA;
            Expect(fwA != null && fwB != null && farNo && copied && hoverNone == GameText.Get("overclock.mirror.hover_none") && hover.Contains(nameA) && sameNo && cooldownNo && swapped
                   && TouchedHooks.Contains("overclock.first_mirror"),
                $"F1 FGR-EMG-164：40 米外发动核心固件不复制（{farNo}）；5 米内发动 → 复制“{nameA}”（{copied}，持有 {rec.MirrorCopyHeld} 条、占镜像体自己的刷写带宽），悬停“{hover}”（复制前“{hoverNone}”）；" +
                $"同一枚不重复复制（{sameNo}）、冷却内换固件不复制（{cooldownNo}）、冷却后换复制新固件且旧复制品让出带宽（{swapped}）；引导钩子 {(hooks == 0 ? "首次触发" : "已触发")}");

            // 复制品不能再被复制（来源持有复制品时拒绝，原因可见）
            BenchEnemy(site, s, "enemy_oc_mirror", "m2", new Vector2(3f, 0f));
            int m2 = UnitOf(site, "m2");
            MirrorCopyResult refuse = site.MirrorCopy(m2, ComponentCatalog.CompGunId, new[] { fwA }, mirror, s);
            Expect(!refuse.Ok && refuse.RefuseKey == "emg.mirror.refuse_copy" && GameText.Get(refuse.RefuseKey) != refuse.RefuseKey && !site.UnitHoldsCopy(m2),
                $"F2 负向：复制品不能再被复制（{refuse.RefuseKey}：“{GameText.Get(refuse.RefuseKey ?? string.Empty)}”）；不会产出新的固件实例（只换武器程序）");

            // F2b 审查修复：来源是敌方（镜像体自己用复制品打出核心反应）时别的镜像体不尝试复制、不发拒绝通知
            int r0 = CombatSite.MirrorRefused, d0 = CombatSite.MirrorDetectCopies;
            site.OnDeclCoreReaction(s, mirror, MechanicalReactionCatalog.ReactionMeltOverloadId, 1, Vector2.zero);
            Expect(CombatSite.MirrorRefused == r0 && CombatSite.MirrorDetectCopies == d0 && !site.UnitHoldsCopy(m2),
                $"F2b 负向：敌方单位发动核心反应不触发镜像侦测（拒绝次数 {CombatSite.MirrorRefused - r0}、复制次数 {CombatSite.MirrorDetectCopies - d0}）");

            // F4 卡片负向（审查修复）：复制带触发器的固件，子载荷被截断时原因可见；复制混合固件按展开后的指令计容量——走同一条侦测 / 通知 / 悬停路径（夹具核心固件）
            bool Noted(string text) => NotificationCenter.History.Concat(NotificationCenter.Toasts).Where(n => n.Type?.Id == "overclock_mirror")
                .SelectMany(n => n.Members).Any(mb => mb.Detail != null && mb.Detail.Contains(text));
            RunFor(site, 4.2f);
            site.MirrorDetect(s, nearP, "fw_split", new[] { "fw_split", "fw_split" });
            string hoverGen = site.MirrorHoverLine(s, mirror);
            string genText = GameText.Format("overclock.mirror.gen_full", rec.MirrorCopyGenFull.ToString());
            int genN = rec.MirrorCopyGenFull;
            bool genOk = rec.MirrorCopyFirmware == "fw_split" && genN > 0 && hoverGen.Contains(genText) && Noted(genText);
            RunFor(site, 4.2f);
            site.MirrorDetect(s, nearP, "fw_mix_etchshell", new[] { "fw_mix_etchshell", "fw_homing" });
            string hoverCap = site.MirrorHoverLine(s, mirror);
            string capText = GameText.Format("overclock.mirror.capacity", rec.MirrorCopyOverflow.ToString());
            bool capOk = rec.MirrorCopyFirmware == "fw_mix_etchshell" && rec.MirrorCopyOverflow > 0 && rec.MirrorCopyGenFull == 0 && hoverCap.Contains(capText)
                         && Noted(capText) && !hoverCap.Contains("overclock.mirror.");
            Expect(genOk && capOk,
                $"F4 卡片负向：复制 [分裂, 分裂]（触发器）→ 子载荷代际已满 {genN} 处，悬停“{hoverGen}”、通知写原因（{genOk}）；" +
                $"复制 [溶蚀弹头, 寻的]（混合固件展开 2 条占满 C(1)）→ 溢出 {rec.MirrorCopyOverflow} 条，悬停“{hoverCap}”、通知写原因（{capOk}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckMirrorSaveLoad()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_oc_mirror", "ms", Vector2.zero);
            int mirror = UnitOf(site, "ms");
            int p = Player(site, new Vector2(5f, 0f), Gun(), 9999f, CombatUnitKind.Machine, 0f);
            site.OnDeclCoreReaction(s, p, MechanicalReactionCatalog.ReactionMarkJumpId, 1, new Vector2(5f, 0f));
            RegionEnemyRecord rec = s.RegionEnemies.First(e => e.EnemyInstanceId == "ms");
            int heldA = rec.MirrorCopyHeld;
            RunFor(site, 0.5f);
            CombatSites.WriteTo(s);
            site = CombatSites.Open(Bench, s, true, new BenchRules(), out bool restored);
            bool holds = site.TryGetEnemyUnit("ms", out int m) && m == mirror && site.UnitHoldsCopy(mirror) && site.Kernel.FlashUsed(mirror) == heldA;
            RunFor(site, 4.2f);
            p = Player(site, new Vector2(5f, 0f), Gun(), 9999f, CombatUnitKind.Machine, 0f); // 夹具机器没有机器记录，读档时按“记录不存在”移除：重新放一台
            site.OnDeclCoreReaction(s, p, MechanicalReactionCatalog.ReactionMeltOverloadId, 1, new Vector2(5f, 0f));
            bool swapped = rec.MirrorCopyFirmware == MechanicalReactionCatalog.TriggerFirmwareOf(MechanicalReactionCatalog.ReactionMeltOverloadId)
                           && site.Kernel.FlashUsed(mirror) == rec.MirrorCopyHeld && site.UnitHoldsCopy(mirror);
            Expect(restored && holds && swapped,
                $"F3 DEBT-EMG4HIJ02-02：复制 → 存读档（{restored}）→ 镜像体仍持有复制品（{holds}，{heldA} 条）→ 再复制新固件：占用 = 新复制品条数 {rec.MirrorCopyHeld}（旧复制品让出带宽，复制前武器按敌人行重编：{swapped}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── G 存读档 ───────────────────────────────

        private static CombatSite BuildMixedBench(CampaignState s)
        {
            CombatSite site = OpenBench(s);
            site.SetHeatRadiation(new List<float4> { new float4(0f, 0f, 0f, 30f) }, HeatRadiationService.CapMult, HeatRadiationService.DissipationMult);
            BenchEnemy(site, s, "enemy_oc_hound", "x_hound", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_oc_node", "x_node", new Vector2(-4f, 0f));
            BenchEnemy(site, s, "enemy_oc_fuser", "x_fuser", new Vector2(0f, 8f));
            BenchEnemy(site, s, "enemy_oc_turret", "x_turret", new Vector2(-6f, 4f));
            BenchEnemy(site, s, "enemy_oc_runaway", "x_run", new Vector2(4f, -6f));
            int prey = Player(site, new Vector2(10f, 0f), Gun(2f, 0.5f), 99999f, CombatUnitKind.Machine, 5f);
            MoveTo(site, prey, new Vector2(300f, 0f));
            Player(site, new Vector2(-2f, 12f), null, 99999f, CombatUnitKind.Structure);
            return site;
        }

        private static void CheckKernelSnapshot()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = BuildMixedBench(s);
            RunFor(site, 5.5f);
            int fuser = UnitOf(site, "x_fuser");
            site.Kernel.Damage(fuser, View(site, fuser).MaxHealth * 0.75f, 0);
            RunFor(site, 0.2f);
            CombatKernel k = site.Kernel;
            byte[] snap = k.Serialize();
            ulong h0 = k.StateHash();
            using var b = new CombatKernel(k.Config, 64);
            CombatSite.ApplyEmgSubForms(b);
            CombatSite.ApplyEntityProfiles(b);
            CombatSite.ApplyMatters(b);
            CombatLoadResult lr = b.Load(snap);
            b.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            b.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            b.SetHeatRadiation(new List<float4> { new float4(0f, 0f, 0f, 30f) }, HeatRadiationService.CapMult, HeatRadiationService.DissipationMult);
            ulong h1 = b.StateHash();
            bool sameRecords = b.OverclockCount == k.OverclockCount && k.OverclockCount >= 4;
            bool melting = b.TryGetOverclock(fuser, out CombatOverclock fr) && fr.State == CombatOverclockConst.StateMelting;
            double t0 = _t;
            for (int i = 0; i < 240; i++)
            {
                t0 += Dt;
                k.Step(Dt, t0);
                b.Step(Dt, t0);
            }
            bool cont = k.StateHash() == b.StateHash() && k.EmgStateHash() == b.EmgStateHash();
            byte[] old = k.SerializeFormatForTests(20);
            using var c = new CombatKernel(k.Config, 64);
            CombatSite.ApplyEmgSubForms(c);
            CombatSite.ApplyEntityProfiles(c);
            CombatSite.ApplyMatters(c);
            CombatLoadResult lr20 = c.Load(old);
            Expect(lr == CombatLoadResult.Ok && h0 == h1 && sameRecords && melting && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 21
                   && lr20 == CombatLoadResult.Ok && CombatKernel.PeekFormat(old) == 20 && c.OverclockCount == 0,
                $"G1 内核快照（格式 {CombatConst.FormatVersion}，段 34 超频运行状态 {k.OverclockCount} 条，含熔断中 {melting}）往返哈希一致（{h0 == h1}），读回后接着跑 4 秒逐位相同（{cont}）（{lr}）；" +
                $"格式 20 旧档照常读（{lr20}，没有超频记录 {c.OverclockCount}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        /// <summary>隐含要求：暂停与 0.5x～3x——熔断引信按游戏秒结算：暂停不爆，四档倍速下都在 2 个游戏秒时爆。</summary>
        private static void CheckSpeedMatrix()
        {
            var rows = new List<string>();
            bool paused = true, same = true;
            RaidCatalog.TryGetUnit("overclock.oc_fuser", out RaidUnitDef def);
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(42);
                CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
                PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionOverclock);
                var at = t != null ? new Vector2(t.CenterX, t.CenterY) : new Vector2(400f, 400f); // 远离家园防线：只看引信计时，不让别的火力打死它
                // 走区域敌人的正式生成（记录 + EnemySpawnBuilder），不走攻城单位：没有所属队伍的攻城单位会被攻城剧场收回，测不到引信
                var rec = new RegionEnemyRecord
                {
                    EnemyInstanceId = "h_fuser_" + speed, RegionId = HomeValleyLayout.RegionId, EnemyTypeId = def?.EnemyTypeId, Position = at, IsAlive = true,
                    Health = Row(def?.EnemyTypeId)?.MaxHp ?? 1f, MaxHealth = Row(def?.EnemyTypeId)?.MaxHp ?? 1f,
                };
                s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(rec).ToArray();
                int u = def != null && home != null && EnemySpawnBuilder.TryBuild(home, s, rec, RegionEnemySites.For(HomeValleyLayout.RegionId), out CombatSpawn sp) ? home.SpawnEnemy(rec, sp) : 0;
                if (u == 0)
                {
                    Fail("H 准备：生成熔断者失败");
                    return;
                }
                home.Kernel.Damage(u, View(home, u).MaxHealth * 0.75f, 0);
                WorldSimulation.StepMany(1);
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                paused &= home.Kernel.IsAlive(u) && home.Kernel.TryGetOverclock(u, out CombatOverclock pr) && pr.State == CombatOverclockConst.StateMelting;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long start = GameClock.Ticks;
                long mid = start + (long)(GameClock.StepHz * 1.7f), end = start + (long)(GameClock.StepHz * 2.3f);
                for (int i = 0; i < 6000 && GameClock.Ticks < mid; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                bool aliveMid = home.Kernel.IsAlive(u) && home.Kernel.TryGetOverclock(u, out CombatOverclock mr) && mr.State == CombatOverclockConst.StateMelting;
                int bursts = OverclockCombatFeedback.BurstCues;
                for (int i = 0; i < 6000 && GameClock.Ticks < end; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                bool blown = !home.Kernel.IsAlive(u) && OverclockCombatFeedback.BurstCues > bursts;
                GameClock.SetSpeed(1f);
                same &= aliveMid && blown;
                rows.Add($"{speed}x {(aliveMid ? "1.7 秒未爆" : "1.7 秒已爆或不在熔断")}/{(blown ? "2.3 秒已爆" : "2.3 秒未爆")}（开始 {start} 步、结束 {GameClock.Ticks} 步）");
            }
            Expect(paused && same, $"H1 暂停 6 秒熔断者不爆（{paused}）；0.5x / 1x / 2x / 3x 都按 2 个游戏秒引爆：{string.Join("、", rows)}");
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(_radSeed);
            MakeBlueprints(s);
            SetFleet(s, BpFuse, 3);
            string locked = OverclockCounterService.Locked(s);
            int day = s.Factions.OverclockCounterDay;
            IReadOnlyList<float4> shapes0 = HeatRadiationService.ActiveShapes(s);
            string zone = shapes0.Count > 0 ? HeatRadiationService.ZoneAt(s, new Vector2(shapes0[0].x + (shapes0[0].z + shapes0[0].w) * 0.5f, shapes0[0].y)) : "none";
            s.Factions.RadiationZonesNoticed = new[] { zone };
            var mrec = new RegionEnemyRecord
            {
                EnemyInstanceId = "fg11fac01_mirror_rec", RegionId = "fg11fac01-site", EnemyTypeId = "enemy_oc_mirror", IsAlive = true, Health = 10f, MaxHealth = 10f,
                MirrorCopyFirmware = "fw_marktag", MirrorCopyHeld = 2, MirrorCopyAt = 12.5f,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(mrec).ToArray();
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            HeatRadiationService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!sv.Success || !rr.Success)
            {
                Fail($"G2 准备：存档 {sv.Success} / 读档 {rr.Success}（{rr.Message}）");
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            CampaignState back = rr.State;
            HomeValleyController hv = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(hv.SiteId);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            int locks = OverclockCounterService.LockCount;
            string lockedBack = OverclockCounterService.Locked(back);
            RegionEnemyRecord mb = back.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == mrec.EnemyInstanceId);
            bool kept = back.Factions.OverclockCounterId == locked && back.Factions.OverclockCounterDay == day && lockedBack == locked && OverclockCounterService.LockCount == locks
                        && back.Factions.RadiationZonesNoticed.Contains(zone) && mb != null && mb.MirrorCopyFirmware == "fw_marktag" && mb.MirrorCopyHeld == 2 && Mathf.Approximately(mb.MirrorCopyAt, 12.5f);
            int zones = home != null ? home.Kernel.HeatRadiationZoneCount : -1;
            Expect(locked == OverclockCounterService.CounterFuse && kept && zones == HeatRadiationService.ActiveShapes(back).Count && zones > 0,
                $"G2 战役存读档：今天锁定的对策“{OverclockCounterService.CounterName(locked)}”与锁定日（第 {day} 天）不变、不重锁；已提示过的辐射区、镜像体复制记录保留（{kept}）；" +
                $"星球内核读档后重写 {zones} 片高温辐射区（派生量）");
        }

        // ─────────────────────────────── K FG-GAP-225 教义自卫 ───────────────────────────────

        private static int Shooter(CombatSite site, Vector2 at, params string[] firmware)
        {
            BlueprintCircuitPreview p = CombatSite.EnemyPreview(ComponentCatalog.CompGunId, firmware);
            CombatWeapon w = site.WithProgram(p, CombatSite.MachineWeaponFrom(p));
            int u = Player(site, at, w, 9999f, CombatUnitKind.Machine, 3f);
            site.Kernel.SetMachineSelfDefense(u, true);
            return u;
        }

        /// <summary>不动、不还手的敌方靶子。</summary>
        private static int HostileDummy(CombatSite site, Vector2 at, float hp = 5000f) => site.Kernel.Spawn(new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.6f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = -1, BehaviorProfile = -1,
            ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
        });

        private static void CheckSelfDefenseTrail()
        {
            (bool fired, long warns, byte code) Case(bool trail, Vector2 enemyAt, bool move, bool building)
            {
                CampaignState s = NewWorldState(1);
                CombatSite site = OpenBench(s);
                int u = trail ? Shooter(site, Vector2.zero, "fw_burntrail") : Player(site, Vector2.zero, Gun(10f, 0.5f), 9999f, CombatUnitKind.Machine, 3f);
                site.Kernel.SetMachineSelfDefense(u, true);
                int e = HostileDummy(site, enemyAt);
                if (building)
                {
                    Player(site, enemyAt * 0.5f, null, 9999f, CombatUnitKind.Structure);
                }
                if (move)
                {
                    MoveTo(site, u, new Vector2(30f, 0f));
                }
                float h0 = Hp(site, e);
                long w0 = CombatSite.SelfDefenseHeldWarnings;
                RunFor(site, 2f);
                bool fired = Hp(site, e) < h0 - 1e-3f;
                long w = CombatSite.SelfDefenseHeldWarnings - w0;
                CombatSites.Close(Bench, s, dropRecord: true);
                return (fired, w, CombatSite.LastSelfDefenseHeldCode);
            }
            var ahead = Case(true, new Vector2(6f, 0f), true, false);
            var behind = Case(true, new Vector2(-6f, 0f), true, false);
            var plain = Case(false, new Vector2(6f, 0f), true, false);
            Expect(!ahead.fired && ahead.warns == 1 && ahead.code == 1 && behind.fired && behind.warns == 0 && plain.fired && plain.warns == 0,
                $"K1 FG-GAP-225：带燃迹右键地面走向敌人，教义自卫不把明火铺在自己前进的路上（开火 {ahead.fired}），同一次交战只警告 1 次（{ahead.warns}，原因 {ahead.code}）；" +
                $"负向：敌人在背后照常还手（{behind.fired}，警告 {behind.warns}）、普通枪照常还手（{plain.fired}，警告 {plain.warns}）");
            var house = Case(true, new Vector2(6f, 0f), false, true);
            var open = Case(true, new Vector2(6f, 0f), false, false);
            Expect(!house.fired && house.warns == 1 && house.code == 2 && open.fired && open.warns == 0,
                $"K2 FG-GAP-225：空闲的燃迹机器自卫，明火会落在己方建筑上时不开火并警告（开火 {house.fired}，警告 {house.warns}，原因 {house.code}）；中间没有建筑照常还手（{open.fired}）。玩家右键敌人 = 攻击不受影响（不走教义自卫）");
        }

        // ─────────────────────────────── I 反馈与图鉴 ───────────────────────────────

        private static void CheckFeedbackAndCodex()
        {
            MechanicCodex.ResetContentForTests();
            MechanicCodex.OnEnemyEncountered("enemy_oc_node");
            string[] hooks = { "overclock.first_shutdown", "overclock.first_vent", "overclock.first_melt", "overclock.first_burst", "overclock.first_mode",
                "overclock.first_mirror", "overclock.first_aura" };
            var unseen = hooks.Where(h => !TouchedHooks.Contains(h)).ToList();
            string[] entries = { "codex.overclock.dash", "codex.overclock.rapid", "codex.overclock.burst", "codex.overclock.aura", "codex.overclock.modes", "codex.overclock.mirror", "codex.overclock.radiation" };
            var missing = new List<string>();
            foreach (string id in entries)
            {
                CodexEntry e = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.FirstOrDefault(x => x.Id == id);
                if (e == null)
                {
                    missing.Add(id);
                    continue;
                }
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    if (GameText.Get(e.TitleKey) == e.TitleKey || GameText.Get(e.BodyKey) == e.BodyKey)
                    {
                        missing.Add(lang + ":" + id);
                    }
                }
            }
            var keys = new[] { "overclock.mirror.copied", "overclock.mirror.truncated", "overclock.mirror.hover", "overclock.mirror.hover_none", "overclock.counter.notify",
                "intel.counter.overclock", "emg.avoid.selfdefense.path", "emg.avoid.selfdefense.building", "hazard.heat_radiation.notify.first", "component.struct_fin_crown.name" };
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                missing.AddRange(keys.Where(k => GameText.Get(k) == k).Select(k => lang + ":" + k));
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(unseen.Count == 0 && missing.Count == 0,
                $"I1 B07 / B14：每条专属行为与第一次遭遇算力节点都在本次运行里发了引导钩子（未发 {unseen.Count}：{string.Join(",", unseen)}；高温辐射首次进入提示走信号机器路径，见证据）；" +
                $"7 条超频机制图鉴与镜像 / 反制 / 自卫 / 辐射文本中英齐全（缺 {missing.Count}：{string.Join(",", missing.Take(6))}）");
            Expect(OverclockCombatFeedback.ShutdownCues > 0 && OverclockCombatFeedback.VentCues > 0 && OverclockCombatFeedback.MeltWarnCues > 0 && OverclockCombatFeedback.BurstCues > 0
                   && OverclockCombatFeedback.ModeWarnCues > 0 && OverclockCombatFeedback.ModeCues > 0,
                $"I2 FGR-FAC-008 ①：停机 {OverclockCombatFeedback.ShutdownCues} / 排气 {OverclockCombatFeedback.VentCues} / 熔断预警 {OverclockCombatFeedback.MeltWarnCues} / 泄爆 {OverclockCombatFeedback.BurstCues} / " +
                $"三态预警 {OverclockCombatFeedback.ModeWarnCues} / 切换 {OverclockCombatFeedback.ModeCues} 都有声音 + 字幕");
        }

        // ─────────────────────────────── P 性能 ───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            site.SetHeatRadiation(new List<float4> { new float4(70f, 40f, 0f, 60f) }, HeatRadiationService.CapMult, HeatRadiationService.DissipationMult);
            string[] types = Units.Select(u => u.id).ToArray();
            for (int i = 0; i < 200; i++)
            {
                BenchEnemy(site, s, types[i % types.Length], "p" + i, new Vector2((i % 20) * 7f, (i / 20) * 7f));
            }
            for (int i = 0; i < 40; i++)
            {
                Player(site, new Vector2((i % 10) * 14f + 3.5f, (i / 10) * 16f + 7f), Gun(1f, 0.5f), 1e6f, i % 5 == 0 ? CombatUnitKind.Structure : CombatUnitKind.Machine);
            }
            RunFor(site, 6f);
            int records = site.Kernel.OverclockCount;
            var sw = Stopwatch.StartNew();
            int steps = 120;
            RunFor(site, steps * Dt);
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            CombatSites.Close(Bench, s, dropRecord: true);
            PerfGate.Expect(perStep > 0 && records > 50, $"P1 性能（Editor batchmode，Mono JIT，一次测量；真机 IL2CPP + HybridCLR 另测 FG15-SYS-02）：200 个超频单位（六种各占六分之一，超频记录 {records} 条，" +
                                                        $"高温辐射 1 片）+ 40 台机器的内核一步 {perStep:F3} ms",
                new[] { new PerfGate.Metric("超频单位内核一步(ms)", perStep, 4.0, false) }, Expect, Line);
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
            CampaignState s = CampaignState.CreateNew("fgoverclock-" + seed, "Standard", seed);
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
            HeatRadiationService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgoverclock-" + seed, "Standard", seed);
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
                Debug.Log($"[FgOverclockFactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
