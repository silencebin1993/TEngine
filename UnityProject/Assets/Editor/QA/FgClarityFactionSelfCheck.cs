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
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
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
    /// FG10-FAC-01 化工（澄净）单位与精英（FG09 第 6 章；FGR-FAC-001 / 002 / 004 / 005 / 008；验收 FGT-FAC-005，另覆盖 FGT-FAC-002 / 009 的澄净部分）的自检——起真实系统、断言行为：
    /// A 数据：5 类单位 + 精英沉淀巨罐入表（读法 / 标签 / 层级 / 图鉴中英文本 / 职能文本 / 组件与固件真实 ID）、专属行为预警 ≥ 0.8 秒（表层）、突袭 / 攻城 / 反制行、靶子覆盖、
    ///   澄净据点与精英残部编成都是本阵营单位（DEBT-FG8EXP03-02）。
    /// B 内核专属语法（战斗内核真实步进）：喷淋布区（蓄压实测 ≥ 0.8 秒且原地不动、锥形内命中挂酸蚀、锥形外不挨、落地酸液池、普通开火路径不出手）、
    ///   液氮（先减速再冻结、冻结不能开火、燃烧解冻更快、解冻后免疫）、油污爬行者（油迹、引信实测 ≥ 0.8 秒、自爆、被火击毁的爆燃双方都挨——“反过来利用”、油迹被点燃）、
    ///   枢轴喷灌（蓄压 ≥ 0.8 秒、静止目标从预警到被扫中 ≥ 预警秒数、喷灌中不移动）、分裂（3 滩、爬行、蒸发）与卡片负向“分裂液在酸沼里的叠加”（多滩不叠加、与酸沼相加、叠层封顶）、
    ///   喷淋池被冷却液冲洗、酸沼（每秒 2%、湿润减半、澄净与空中单位不受）、空中单位（格斗打不到、射弹打得到、投放预警 ≥ 0.8 秒、击落阻止投放）、腐蚀蜂群优先打建筑、快照格式 15 往返逐位一致、确定性。
    /// C 世界：规划层酸沼（外圈带 + 领地酸沼）写进星球内核、与地图悬停同一判定、机器在酸沼里真的掉耐久；澄净据点展开本阵营单位（含反制追加）；攻城单位带专属参数（含当天的绝缘涂层）。
    /// D FGT-FAC-005 反制：燃烧 / 引信 / 电磁三种构筑 → 澄净选出液氮 / 蜂群 / 绝缘，每日锁定、日切更新、突袭反制行映射、情报行；同一构筑铸造 / 静默各在自己的技术树里选。
    /// E 反馈与图鉴：每种专属行为都有声音 + 字幕（反馈计数）、引导钩子、图鉴条目与开火失败原因文本。F 性能（一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgClarityFactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgClarityFactionSelfCheck。
    /// </summary>
    public static class FgClarityFactionSelfCheck
    {
        private const int Slot = 7;
        private const string Bench = "fg10fac01-bench";
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/FG 澄净单位与精英")]
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
            Line("\n[澄净单位与精英] 五类单位与沉淀巨罐、专属语法（喷淋 / 液氮 / 自爆与爆燃 / 枢轴 / 分裂 / 飞行与投放）、酸沼、据点与攻城、反制、图鉴（FG10-FAC-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgclarity-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                ClarityCombatFeedback.ResetForTests();
                ClarityCounterService.ResetForTests();
                AcidMarshService.ResetForTests();
                // 图鉴解锁写到临时文件（不碰开发机的图鉴记录）；本次运行实际触发过的引导钩子单独记（“已看过”是跨战役的本机记录，不能当本次事实）。
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();
                TouchedHooks.Clear();
                GuidanceHooks.Touched += OnHookTouched;

                Step(CheckData);
                Step(CheckRaidGarrisonAndRange);
                Step(CheckSpray);
                Step(CheckCryo);
                Step(CheckCrawler);
                Step(CheckDeflagration);
                Step(CheckPivot);
                Step(CheckPivotCoverage);
                Step(CheckSplitInAcid);
                Step(CheckAcidMarshKernel);
                Step(CheckPoolWash);
                Step(CheckAir);
                Step(CheckSwarm);
                Step(CheckSprayEdgeAndSiegeFlag);
                Step(CheckKernelSnapshot);
                Step(CheckDeterminism);
                Step(CheckAcidWorld);
                Step(CheckGarrisonAndSiege);
                Step(CheckSelfDestructSettlement);
                Step(CheckCounter);
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
            Line(_fail == 0 ? $"  澄净单位与精英：全部 {_pass} 项通过" : $"  澄净单位与精英：{_fail} 项失败 / {_pass} 项通过");
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
            ("enemy_sprayer", EnemyDefs.BehaviorSpray, EnemyDefs.TierNormal, "control"),
            ("enemy_oil_crawler", EnemyDefs.BehaviorCrawlBurst, EnemyDefs.TierNormal, "suicide"),
            ("enemy_ln2_pump", EnemyDefs.BehaviorSpray, EnemyDefs.TierNormal, "disabler"),
            ("enemy_deact_drone", EnemyDefs.BehaviorPollute, EnemyDefs.TierNormal, "saboteur"),
            ("enemy_corrosion_swarm", EnemyDefs.BehaviorSwarm, EnemyDefs.TierNormal, "swarm"),
            ("enemy_settling_tank", EnemyDefs.BehaviorPivot, EnemyDefs.TierElite, "control"),
        };

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            var bad = new List<string>();
            foreach ((string id, string behavior, string tier, string role) in Units)
            {
                MechEnemy r = Row(id);
                if (r == null || r.Faction != EnemyDefs.FactionClarity || r.Behavior != behavior || r.Tier != tier || r.Role != role
                    || !EnemyBehaviorReadings.TryGet(r.Behavior, out IEnemyBehaviorReading reading) || reading.Kernel != CombatBehavior.Raider
                    || !EnemyDefs.HasTag(r, EnemyDefs.TagAcidProof))
                {
                    bad.Add(id);
                    continue;
                }
                foreach (string c in r.Components.Split(','))
                {
                    string cid = c.Trim();
                    if (!CarrierReadings.TryGetComponent(cid, out _) && !FirmwareKinds.IsFirmware(cid))
                    {
                        bad.Add(id + ":" + cid);
                    }
                }
            }
            bool air = EnemyDefs.HasTag("enemy_deact_drone", EnemyDefs.TagAirborne);
            bool anti = EnemyDefs.HasTag("enemy_corrosion_swarm", EnemyDefs.TagAntiBuilding);
            bool insul = new[] { "enemy_sprayer", "enemy_ln2_pump", "enemy_settling_tank" }.All(id => EnemyDefs.HasTag(id, EnemyDefs.TagAdaptInsulate));
            MechEnemy tank = Row("enemy_settling_tank");
            bool split = tank != null && Mathf.Approximately(tank.SplitCount, 3f) && tank.SplitHp > 0f && tank.SplitLife > 0f && tank.SplitDps > 0f;
            // 图鉴（FGR-FAC-005 / FG09 第 9 节“每种敌人都有图鉴条目”）：名称 / 职能描述 / 弱点（= 应对）/ 职能名中英都有。
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
            List<string> problems = EnemyDefs.AllProblems();
            Expect(bad.Count == 0 && air && anti && insul && split && missingText.Count == 0 && problems.Count == 0,
                $"A1 澄净 5 类单位 + 精英沉淀巨罐入表（阵营 / 读法 / 层级 / 职能 / acid_proof / 组件与固件真实 ID 异常 {bad.Count}：{string.Join(",", bad)}）；灭活无人机在空中 {air}、" +
                $"腐蚀蜂群打建筑 {anti}、喷洒车 / 液氮泵车 / 巨罐可挂绝缘涂层 {insul}；巨罐分裂成 3 滩（{split}）；图鉴中英文本缺 {missingText.Count}：{string.Join(",", missingText.Take(6))}；全表行问题 {problems.Count}");

            var weak = new List<string>();
            foreach (MechEnemy r in EnemyDefs.Rows.Where(x => x.Faction == EnemyDefs.FactionClarity))
            {
                bool exclusive = r.Behavior == EnemyDefs.BehaviorSpray || r.Behavior == EnemyDefs.BehaviorCrawlBurst || r.Behavior == EnemyDefs.BehaviorPivot
                                 || r.Behavior == EnemyDefs.BehaviorPollute;
                if (exclusive && r.WarnSeconds < 0.8f)
                {
                    weak.Add(r.Id + " warn " + r.WarnSeconds);
                }
                if (r.WeaponDamage >= 20f && r.WarnSeconds < 0.8f)
                {
                    weak.Add(r.Id + " heavy");
                }
            }
            Expect(weak.Count == 0,
                $"A2 FGT-FAC-002 / 009（表层）：澄净全部专属行为（喷嘴蓄压 / 自爆引信 / 枢轴蓄压 / 投放悬停）与大威力攻击的预警 ≥ 0.8 秒（不合格 {weak.Count}：{string.Join("；", weak)}；内核实测见 B 段）");
        }

        private static void CheckRaidGarrisonAndRange()
        {
            string[] ids = { "clarity.sprayer", "clarity.oil_crawler", "clarity.corrosion_swarm", "clarity.ln2_pump", "clarity.deact_drone", "clarity.settling_tank" };
            bool raid = ids.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.Faction == EnemyDefs.FactionClarity && d.Points > 0 && d.ElitePoints > d.Points
                                       && SiegeCatalog.TryGetUnit(d.EnemyTypeId, out _));
            bool counters = new[] { ("clarity.fluid", "cryo"), ("clarity.fuse", "swarm"), ("clarity.em", "insulated") }
                .All(c => RaidCatalog.CounterById(c.Item1) is RaidCounter rc && rc.Faction == EnemyDefs.FactionClarity && rc.Tag == c.Item2
                          && RaidCatalog.Units.Any(u => u.Faction == EnemyDefs.FactionClarity && u.HasTag(c.Item2)));
            TerritoryControl tc = ConfigSystem.Instance.Tables.TbTerritoryControl.DataList.FirstOrDefault(r => r.Id == EnemyDefs.FactionClarity);
            bool own = tc != null && tc.GarrisonUnits != "none" && tc.EliteUnits != "none"
                       && tc.GarrisonUnits.Split(',').Concat(tc.EliteUnits.Split(',')).All(u => RaidCatalog.TryGetUnit(u.Trim(), out RaidUnitDef d) && d.Faction == EnemyDefs.FactionClarity)
                       && tc.EliteUnits.Contains("clarity.settling_tank");
            var uncovered = new List<string>();
            foreach ((string id, _, _, _) in Units)
            {
                if (!ConfigSystem.Instance.Tables.TbRangeTarget.DataList.Any(t => t.UnlockEnemies.Split(',').Any(e => e.Trim() == id)))
                {
                    uncovered.Add(id);
                }
            }
            Expect(raid && counters && own && uncovered.Count == 0,
                $"A3 突袭编成（6 种澄净单位都有攻城参数、精英点数 > 点数：{raid}）；反制三行（流体 / 燃烧 → 液氮、引信 → 蜂群、电磁 → 绝缘）的标签在澄净编成里有单位（{counters}）；" +
                $"DEBT-FG8EXP03-02：澄净据点与精英残部编成全是本阵营单位、残部带沉淀巨罐（{own}：{tc?.GarrisonUnits} / {tc?.EliteUnits}）；每种澄净敌人都解锁靶子（FG-GAP-101，未覆盖 {uncovered.Count}）");
        }

        // ─────────────────────────────── B 内核专属语法 ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
        }

        private static CombatSite OpenBench(CampaignState s)
        {
            CombatSites.Close(Bench, s, dropRecord: true);
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

        private static int Player(CombatSite site, Vector2 at, CombatWeapon wp, float hp = 400f, CombatUnitKind kind = CombatUnitKind.Machine)
        {
            int w = site.WeaponIndex(wp);
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = kind, Faction = CombatFaction.Player, Behavior = kind == CombatUnitKind.Machine ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Speed = kind == CombatUnitKind.Machine ? 4f : 0f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, 1f),
            });
        }

        private static CombatWeapon Gun(float dmg = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 40f, Damage = dmg, Cooldown = 0.05f, TargetMode = CombatTargetMode.Nearest,
        };

        private static CombatWeapon Melee(float dmg = 10f, float reach = 2.5f)
        {
            CombatWeapon w = Gun(dmg);
            w.Reading.Carrier = CombatCarrier.Melee;
            w.Reading.Area = reach;
            w.Reading.Cone = 180f;
            w.Reading.Approach = reach;
            return w;
        }

        private static double _t;

        private static void RunFor(CombatSite site, float seconds)
        {
            int n = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                _t += Dt;
                site.Step(Dt, _t);
            }
        }

        private static CombatUnitView View(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static int UnitOf(CombatSite site, string enemyId) => site.TryGetEnemyUnit(enemyId, out int u) ? u : 0;

        private static CombatClarityUnit Cu(CombatSite site, int unit) => site.TryGetClarity(unit, out CombatClarityUnit c) ? c : default;

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        private static bool HasStatus(CombatSite site, int unit, uint bit) =>
            site.Kernel.TryGetStatus(unit, out uint mask, out double until, out _, out _, out _) && (mask & bit) != 0u && until > _t;

        private static Vector2 Pos(CombatSite site, int unit)
        {
            CombatUnitView v = View(site, unit);
            return new Vector2((float)v.Position.x, (float)v.Position.y);
        }

        private static void CheckSpray()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_sprayer", "b_spray", new Vector2(0f, 0f));
            int sp = UnitOf(site, "b_spray");
            int front = Player(site, new Vector2(6f, 0f), Gun(0.01f));
            int behind = Player(site, new Vector2(-7f, 0f), Gun(0.01f));
            long shots0 = site.Kernel.Counters.ShotsFired;
            double warnStart = -1, sprayAt = -1;
            Vector2 heldAt = Vector2.zero;
            bool held = true;
            for (int i = 0; i < 60 * 8 && sprayAt < 0; i++)
            {
                RunFor(site, Dt);
                CombatClarityUnit c = Cu(site, sp);
                if (c.Spray == 1 && warnStart < 0)
                {
                    warnStart = _t;
                    heldAt = Pos(site, sp);
                }
                else if (c.Spray == 1 && Vector2.Distance(heldAt, Pos(site, sp)) > 1e-4f)
                {
                    held = false;
                }
                if (site.Kernel.Counters.Sprays > 0)
                {
                    sprayAt = _t;
                }
            }
            MechEnemy row = Row("enemy_sprayer");
            float fl = View(site, front).Health;
            float bl = View(site, behind).Health;
            bool acid = HasStatus(site, front, Bit("Acid"));
            int pools = site.CountZonesOfKind(CombatConst.ZoneKindPool);
            bool noGun = site.Kernel.Counters.ShotsFired == shots0;
            Expect(warnStart >= 0 && sprayAt - warnStart >= 0.8 - 1e-3 && held && 400f - fl >= row.WeaponDamage - 0.01f && acid && Mathf.Approximately(bl, 400f) && pools >= 1 && noGun
                   && ClarityCombatFeedback.SprayWarnCues > 0,
                $"B1 喷淋布区（FGR-FAC-008，FGT-FAC-009 实测）：喷嘴蓄压 {sprayAt - warnStart:0.00} 秒（≥ 0.8）且蓄压中原地不动（{held}）→ 锥形喷出：前方机器挨 {400f - fl:0.#}（表 {row.WeaponDamage}）并挂酸蚀（{acid}），" +
                $"身后 7 米的机器不挨（{bl:0}）；地面留下酸液池 {pools} 块；武器列只是喷淋参数，普通开火路径不出手（开火计数不变 {noGun}）；蓄压有声音 + 字幕（{ClarityCombatFeedback.SprayWarnCues}）");

            // 酸液池只伤你的单位：站进池子的机器按节拍掉耐久并挂酸蚀；澄净单位自己不怕酸（acid_proof：主动给它挂酸蚀也挂不上；对照：给身后那台机器挂得上）。
            RunFor(site, 3f);
            float poolHit = 400f - View(site, front).Health;
            site.Kernel.ApplyStatus(sp, Bit("Acid"), 4f, 3f, 0f, 0f, front);
            site.Kernel.ApplyStatus(behind, Bit("Acid"), 4f, 3f, 0f, 0f, sp);
            bool selfClean = !HasStatus(site, sp, Bit("Acid"));
            bool controlTagged = HasStatus(site, behind, Bit("Acid"));
            Expect(poolHit > row.WeaponDamage + 0.5f && selfClean && controlTagged,
                $"B1b 酸液池按节拍腐蚀站在里面的机器（累计 {poolHit:0.#} > 单次喷淋 {row.WeaponDamage}）；主动给喷洒车挂酸蚀也挂不上（{selfClean}），同样挂给机器挂得上（对照 {controlTagged}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckCryo()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_ln2_pump", "b_ln2", new Vector2(0f, 0f));
            // 审查修复的回归：目标在射程外（12 米）原地不动，泵车自己逼近到射程边上停下喷——液氮池要落在目标脚下，静止的目标才会“先减速再冻结”。
            int target = Player(site, new Vector2(12f, 0f), Gun(0.01f), 4000f);
            double firstSpray = -1, frozenAt = -1;
            float chillAfterFirst = 0f;
            for (int i = 0; i < 60 * 20 && frozenAt < 0; i++)
            {
                RunFor(site, Dt);
                if (firstSpray < 0 && site.Kernel.Counters.Sprays > 0)
                {
                    firstSpray = _t;
                    chillAfterFirst = site.ChillOf(target);
                }
                if (site.IsFrozen(target))
                {
                    frozenAt = _t;
                }
            }
            CombatFireResult fr = site.Kernel.FireAt(target, UnitOf(site, "b_ln2"), _t);
            string reason = site.FireReason(fr, 0, null);
            bool frozenTag = HasStatus(site, target, Bit("Frozen"));
            Expect(firstSpray > 0 && chillAfterFirst > 0.2f && chillAfterFirst < 1f && frozenAt > firstSpray && fr == CombatFireResult.Frozen && reason == GameText.Get("combat.fire.frozen")
                   && frozenTag && site.Kernel.Counters.Freezes >= 1 && ClarityCombatFeedback.FreezeCues > 0,
                $"B2 液氮“先减速再冻结”（泵车从 12 米外逼近、目标原地不动）：第一次喷中只受寒 {chillAfterFirst:0.00}（没冻住），站在液氮池里随后冻结（{frozenAt - firstSpray:0.0} 秒后）；冻结中不能开火（{fr}，原因“{reason}”）；挂冰冻标签（{frozenTag}）；提示 {ClarityCombatFeedback.FreezeCues}");
            CombatSites.Close(Bench, s, dropRecord: true);

            // 受寒减速、燃烧解冻更快、解冻后短暂免疫（不会被永久冻住）。
            s = NewWorldState(1);
            site = OpenBench(s);
            _t = 0;
            int cold = Player(site, new Vector2(0f, 0f), Gun(0.01f));
            int warm = Player(site, new Vector2(0f, 6f), Gun(0.01f));
            site.ChillUnit(cold, 0.6f);
            site.IssueCommand(cold, CombatCommandKind.Move, new Vector2(30f, 0f), 0, 0.5f, 0f, 0f, false);
            site.IssueCommand(warm, CombatCommandKind.Move, new Vector2(30f, 6f), 0, 0.5f, 0f, 0f, false);
            RunFor(site, 1f);
            float dCold = Pos(site, cold).x;
            float dWarm = Pos(site, warm).x;
            int a = Player(site, new Vector2(-20f, 0f), Gun(0.01f));
            int b = Player(site, new Vector2(-20f, 6f), Gun(0.01f));
            site.ChillUnit(a, 1f);
            site.ChillUnit(b, 1f);
            site.Kernel.ApplyStatus(b, Bit("Fire"), 10f, 0f, 0f, 0f, 0);
            double ta = -1, tb = -1;
            for (int i = 0; i < 60 * 5 && (ta < 0 || tb < 0); i++)
            {
                RunFor(site, Dt);
                if (ta < 0 && !site.IsFrozen(a))
                {
                    ta = _t;
                }
                if (tb < 0 && !site.IsFrozen(b))
                {
                    tb = _t;
                }
            }
            site.ChillUnit(a, 0.5f);
            float guarded = site.ChillOf(a);
            Expect(dCold > 0.1f && dCold < dWarm * 0.8f && ta > 0 && tb > 0 && tb < ta && guarded < 0.01f,
                $"B2b 受寒越高越慢（1 秒走 {dCold:0.0} 米 vs 没受寒 {dWarm:0.0} 米）；燃烧解冻更快（{tb:0.00} 秒 < {ta:0.00} 秒，应对“燃烧、高热构筑”）；刚解冻的机器加不上受寒（{guarded:0.00}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckCrawler()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_oil_crawler", "b_crawl", new Vector2(0f, 0f));
            int cr = UnitOf(site, "b_crawl");
            int target = Player(site, new Vector2(12f, 0f), Gun(0.01f));
            long kills0 = site.Kernel.Counters.KillsHostile;
            bool oiled = false;
            double fuseStart = -1, boom = -1;
            Vector2 heldAt = Vector2.zero;
            bool held = true;
            for (int i = 0; i < 60 * 10 && boom < 0; i++)
            {
                RunFor(site, Dt);
                oiled |= HasStatus(site, cr, Bit("Oil"));
                CombatClarityUnit c = Cu(site, cr);
                if (c.Fuse == 1 && fuseStart < 0)
                {
                    fuseStart = _t;
                    heldAt = Pos(site, cr);
                }
                else if (c.Fuse == 1 && Vector2.Distance(heldAt, Pos(site, cr)) > 1e-4f)
                {
                    held = false;
                }
                if (site.Kernel.Counters.Bursts > 0)
                {
                    boom = _t;
                }
            }
            MechEnemy row = Row("enemy_oil_crawler");
            int slicks = site.CountZonesOfKind(CombatConst.ZoneKindSlick);
            float lost = 400f - View(site, target).Health;
            bool targetOiled = HasStatus(site, target, Bit("Oil"));
            double oilLeft = site.Kernel.StatusBitSecondsLeft(target, math.tzcnt(Bit("Oil")));
            bool dead = !View(site, cr).Alive;
            RegionEnemyRecord crRec = s.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == "b_crawl");
            bool notKill = site.Kernel.Counters.KillsHostile == kills0 && crRec != null && !crRec.IsAlive;
            Expect(oiled && fuseStart > 0 && boom - fuseStart >= 0.8 - 1e-3 && held && lost >= row.WeaponDamage - 0.01f && targetOiled && oilLeft >= row.StatusSeconds - 0.5
                   && dead && notKill && slicks >= 2 && ClarityCombatFeedback.BurstWarnCues > 0,
                $"B3 油污爬行者：身上一直带油（{oiled}），一路留下油迹（{slicks} 摊）；贴近后引信燃烧 {boom - fuseStart:0.00} 秒（≥ 0.8）且原地不动（{held}）→ 自爆：机器挨 {lost:0.#}（表 {row.WeaponDamage}）并溅上油（{targetOiled}，" +
                $"还剩 {oilLeft:0.0} 秒 ≈ 表 statusSeconds {row.StatusSeconds}），本体阵亡且记录标为阵亡，但不算击杀（{notKill}）；引信有声音 + 字幕（{ClarityCombatFeedback.BurstWarnCues}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckDeflagration()
        {
            // 卡片“油污爬行者可以被玩家反过来利用（与燃迹组合）”：身上着火时被击毁 → 爆燃双方都挨（它的同伴一起烧），伤害 × deflagMult；没着火被击毁只留一摊油迹。
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_oil_crawler", "b_burning", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_sprayer", "b_buddy", new Vector2(2f, 0f));
            BenchEnemy(site, s, "enemy_oil_crawler", "b_cold", new Vector2(0f, 30f));
            int burning = UnitOf(site, "b_burning");
            int buddy = UnitOf(site, "b_buddy");
            int cold = UnitOf(site, "b_cold");
            int shooter = Player(site, new Vector2(-30f, 0f), Gun(0.01f), 9999f);
            float b0 = View(site, buddy).Health;
            site.Kernel.ApplyStatus(burning, Bit("Fire"), 10f, 0f, 0f, 0f, shooter);
            site.Kernel.Damage(burning, 99999f, shooter);
            RunFor(site, 0.1f);
            float hit = b0 - View(site, buddy).Health;
            long deflag = site.Kernel.Counters.Deflagrations;
            MechEnemy row = Row("enemy_oil_crawler");
            int slicks0 = site.CountZonesOfKind(CombatConst.ZoneKindSlick);
            site.Kernel.Damage(cold, 99999f, shooter);
            RunFor(site, 0.1f);
            int slicks1 = site.CountZonesOfKind(CombatConst.ZoneKindSlick);
            Expect(deflag == 1 && hit >= row.WeaponDamage * row.DeflagMult - 0.5f && site.Kernel.Counters.Deflagrations == 1 && slicks1 > slicks0 && ClarityCombatFeedback.DeflagrateCues > 0,
                $"B4 反过来利用：着火的油污爬行者被击毁 → 爆燃（{deflag} 次），身旁的喷洒车挨 {hit:0.#}（≥ 表 {row.WeaponDamage} × {row.DeflagMult}）；没着火的被击毁不爆燃、只留一摊油迹（{slicks0} → {slicks1}）");

            // 油迹遇到燃烧整片爆燃（与管线液洼同一套反应），可以用燃迹提前点掉。
            long ig0 = site.Kernel.Counters.SlicksIgnited;
            CombatZone slick = default;
            for (int z = 0; z < site.Kernel.ZoneCount; z++)
            {
                if (site.Kernel.TryGetZone(z, out CombatZone zn) && zn.Kind == CombatConst.ZoneKindSlick && zn.Phase == CombatConst.LeakPhasePuddle)
                {
                    slick = zn;
                    break;
                }
            }
            site.Kernel.SpawnFieldZone(CombatFaction.Player, slick.Pos, 1.5f, 3f, 0f, Bit("Fire"), 3f, 0f, 0f, 0f);
            RunFor(site, 1.5f);
            Expect(slick.Radius > 0f && site.Kernel.Counters.SlicksIgnited > ig0 && ClarityCombatFeedback.SlickIgniteCues > 0,
                $"B4b 油迹遇到燃烧区域整片爆燃（点燃 {ig0} → {site.Kernel.Counters.SlicksIgnited}；提示 {ClarityCombatFeedback.SlickIgniteCues}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckPivot()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_settling_tank", "b_tank", new Vector2(0f, 0f));
            int tank = UnitOf(site, "b_tank");
            int target = Player(site, new Vector2(4.5f, 0f), Gun(0.01f), 4000f);
            double warnStart = -1, sweepStart = -1, firstHit = -1;
            Vector2 anchor = Vector2.zero;
            bool still = true;
            for (int i = 0; i < 60 * 12 && firstHit < 0; i++)
            {
                RunFor(site, Dt);
                CombatClarityUnit c = Cu(site, tank);
                if (c.Pivot == 1 && warnStart < 0)
                {
                    warnStart = _t;
                    anchor = Pos(site, tank);
                }
                if (c.Pivot == 2 && sweepStart < 0)
                {
                    sweepStart = _t;
                }
                if (warnStart >= 0 && Vector2.Distance(anchor, Pos(site, tank)) > 1e-4f)
                {
                    still = false;
                }
                if (View(site, target).Health < 4000f)
                {
                    firstHit = _t;
                }
            }
            MechEnemy row = Row("enemy_settling_tank");
            double lead = firstHit - sweepStart;
            Expect(warnStart >= 0 && sweepStart - warnStart >= 0.8 - 1e-3 && lead >= row.WarnSeconds - 0.05 && still && site.Kernel.Counters.PivotHits > 0
                   && ClarityCombatFeedback.PivotWarnCues > 0,
                $"B5 枢轴喷灌（FGR-FAC-008）：整圈蓄压 {sweepStart - warnStart:0.00} 秒（≥ 0.8）→ 喷淋臂开始转，静止的机器从臂起转到被扫中还有 {lead:0.00} 秒（≥ 预警 {row.WarnSeconds}，“臂端前方的喷淋预警扇区”）；" +
                $"喷灌中本体不动（{still}）；扫中计数 {site.Kernel.Counters.PivotHits}；蓄压提示 {ClarityCombatFeedback.PivotWarnCues}");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckPivotCoverage()
        {
            // 审查修复的回归：扫臂按“上一节拍到这一节拍扫过的整段弧”结算——臂每节拍转 35°、比臂宽大，只看节拍那一刻会每圈漏一半角度。
            // 在 0° / 15° / 25° / 200° 各放一台静止机器：臂起转后转满一圈（+ 一个节拍）之内每台都被扫中；每台从整圈蓄压开始到被扫中都不少于 0.8 秒。
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_settling_tank", "b_tank", new Vector2(0f, 0f));
            int tank = UnitOf(site, "b_tank");
            float[] angles = { 0f, 15f, 25f, 200f };
            var units = new int[angles.Length];
            var hitAt = new double[angles.Length];
            for (int k = 0; k < angles.Length; k++)
            {
                float a = angles[k] * Mathf.Deg2Rad;
                units[k] = Player(site, new Vector2(Mathf.Cos(a) * 4.5f, Mathf.Sin(a) * 4.5f), Gun(0.01f), 4000f);
                hitAt[k] = -1;
            }
            double warnStart = -1, sweepStart = -1;
            for (int i = 0; i < 60 * 14 && hitAt.Any(h => h < 0); i++)
            {
                RunFor(site, Dt);
                CombatClarityUnit c = Cu(site, tank);
                if (c.Pivot == 1 && warnStart < 0)
                {
                    warnStart = _t;
                }
                if (c.Pivot == 2 && sweepStart < 0)
                {
                    sweepStart = _t;
                }
                for (int k = 0; k < units.Length; k++)
                {
                    if (hitAt[k] < 0 && View(site, units[k]).Health < 4000f)
                    {
                        hitAt[k] = _t;
                    }
                }
            }
            MechEnemy row = Row("enemy_settling_tank");
            double rev = 360.0 / row.TurnRate + 0.5 + 1e-3;
            bool allInFirstTurn = sweepStart > 0 && hitAt.All(h => h > 0 && h - sweepStart <= rev);
            bool warned = warnStart > 0 && hitAt.All(h => h - warnStart >= 0.8 - 1e-3);
            // 复核修复的回归：按臂前沿结算，一圈之内每台恰好被扫中一次（相邻节拍的弧不重叠，伤害不翻倍）。
            long hits = site.Kernel.Counters.PivotHits;
            Expect(allInFirstTurn && warned && hits == angles.Length,
                $"B5b 扫臂整圈覆盖：0° / 15° / 25° / 200° 的静止机器在臂起转后 {rev:0.0} 秒（一圈 + 一个节拍）内都被扫中（{string.Join(" / ", hitAt.Select(h => (h - sweepStart).ToString("0.00")))} 秒），" +
                $"且都在整圈蓄压开始 0.8 秒之后（{warned}）；一圈内扫中 {hits} 次（每台恰好一次 = {angles.Length}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSprayEdgeAndSiegeFlag()
        {
            // 审查修复的回归：喷淋按外沿距离出手——停在“外沿 ≤ 射程、中心 > 射程”的大建筑前也会蓄压、喷中它、在它脚下留池；喷到建筑时置“正在破坏建筑”（FGR-DEF-003）。
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            MechEnemy row = Row("enemy_sprayer");
            BenchEnemy(site, s, "enemy_sprayer", "b_edge", new Vector2(0f, 0f));
            int sp = UnitOf(site, "b_edge");
            int w = site.WeaponIndex(Gun(0.01f));
            float centre = row.WeaponRange + 1.5f; // 中心在射程外 1.5 米
            int building = site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Structure, Faction = CombatFaction.Player, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(centre, 0f), Home = new double2(centre, 0f), Radius = 2f, Health = 4000f, MaxHealth = 4000f, Weapon = w, BehaviorProfile = -1,
                ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
            double warnAt = -1;
            float warnDist = 0f;
            for (int i = 0; i < 60 * 3 && warnAt < 0; i++)
            {
                RunFor(site, Dt);
                if (Cu(site, sp).Spray == 1)
                {
                    warnAt = _t;
                    warnDist = Vector2.Distance(Pos(site, sp), Pos(site, building));
                }
            }
            RunFor(site, 2.5f);
            float lost = 4000f - View(site, building).Health;
            bool flagged = (View(site, sp).Flags & CombatUnitFlags.SiegeAttack) != 0;
            CombatZone pool = default;
            for (int z = 0; z < site.Kernel.ZoneCount; z++)
            {
                if (site.Kernel.TryGetZone(z, out CombatZone zn) && zn.Kind == CombatConst.ZoneKindPool)
                {
                    pool = zn;
                    break;
                }
            }
            float poolToBuilding = pool.Radius > 0f ? (float)math.distance(pool.Pos, new double2(centre, 0f)) : 999f;
            Expect(warnAt >= 0 && warnDist > row.WeaponRange && lost >= row.WeaponDamage - 0.01f && flagged && poolToBuilding <= pool.Radius + 2f,
                $"C0 喷淋按外沿距离出手：建筑中心在 {warnDist:0.0} 米（> 射程 {row.WeaponRange}）、外沿在射程内时就蓄压喷出，建筑挨 {lost:0.#}；喷中建筑时置“正在破坏建筑”（{flagged}）；" +
                $"酸液池落在建筑脚下（池心离建筑中心 {poolToBuilding:0.0} 米）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSelfDestructSettlement()
        {
            // 审查修复的回归：攻城里的油污爬行者自爆 = 自毁，不是被谁击毁——突袭结算不计击毁 / 不留残骸 / 不掉落 / 不解锁靶子（内核照样减员）。
            CampaignState s = NewWorld(1);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            RaidCatalog.TryGetUnit("clarity.oil_crawler", out RaidUnitDef crawler);
            var at = new Vector2(30f, 30f);
            int victim = home.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(at.x + 1.5f, at.y), Home = new double2(at.x + 1.5f, at.y), Radius = 0.6f, Health = 4000f, MaxHealth = 4000f, Weapon = -1,
                BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
            int sd0 = RaidResultService.SelfDestructs;
            long kills0 = home.Kernel.Counters.KillsHostile;
            bool unlockedBefore = Defeated(s, "enemy_oil_crawler");
            int unit = SiegeService.SpawnOne(home, 990301, at, at, crawler, false);
            // 只步进家园战斗内核（与世界步同一入口 CombatSite.Step，事件照常分发给突袭结算）：不经过攻城服务的周期对账，测试用的无主队伍不会被当孤儿清掉。
            double now = GameClock.GameSeconds;
            for (int i = 0; i < 60 * 3; i++)
            {
                now += Dt;
                home.Step(Dt, now);
            }
            bool gone = !home.Kernel.TryGetUnit(unit, out CombatUnitView v) || !v.Alive;
            bool unlockedAfter = Defeated(s, "enemy_oil_crawler");
            Expect(unit > 0 && gone && home.Kernel.Counters.Bursts > 0 && RaidResultService.SelfDestructs > sd0 && home.Kernel.Counters.KillsHostile == kills0
                   && unlockedAfter == unlockedBefore,
                $"C4 攻城中的油污爬行者自爆（{gone}）只算自毁：突袭结算收到 {RaidResultService.SelfDestructs - sd0} 次自毁、不计击杀（击杀计数不变）、不解锁靶子（{unlockedBefore} → {unlockedAfter}）");
            home.Kernel.Despawn(victim);
        }

        /// <summary>这种敌人对应的靶子（unlockEnemies 里有它）有没有解锁（“击败过”的公开口径）。</summary>
        private static bool Defeated(CampaignState s, string enemyTypeId)
        {
            foreach (RangeTargetDef d in TestRangeCatalog.Targets)
            {
                if (d != null && ConfigSystem.Instance.Tables.TbRangeTarget.DataList.Any(t => t.Id == d.Id && t.UnlockEnemies.Split(',').Any(e => e.Trim() == enemyTypeId)))
                {
                    return TestRangeService.IsUnlocked(s, d);
                }
            }
            return false;
        }

        private static void CheckSplitInAcid()
        {
            // 卡片负向“巨罐分裂后的液体在酸沼里的叠加”：三滩分裂液贴着同一台机器、机器站在酸沼里——分裂液之间不叠加（每节拍只算一次），与酸沼各算各的（相加），状态叠层封顶。
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 0f, 30f) }, 0.02f, 0.5f);
            BenchEnemy(site, s, "enemy_settling_tank", "b_tank", new Vector2(0f, 0f));
            int tank = UnitOf(site, "b_tank");
            int victim = Player(site, new Vector2(40f, 40f), Gun(0.01f), 1000f);
            site.Kernel.Damage(tank, 999999f, victim);
            RunFor(site, Dt * 2);
            int puddles = site.CountPuddles();
            bool splitCue = ClarityCombatFeedback.SplitCues > 0 && site.Kernel.Counters.Splits == 1;
            site.Kernel.SetPosition(victim, new double2(0f, 0f));
            RunFor(site, 1f); // 分裂液围上来
            float h0 = View(site, victim).Health;
            RunFor(site, 4f);
            float lost = h0 - View(site, victim).Health;
            MechEnemy row = Row("enemy_settling_tank");
            int stacks = site.Kernel.StatusStacksOf(victim, math.tzcnt(Bit("Acid")));
            int cap = site.Kernel.Config.StatusStackCap > 0 ? site.Kernel.Config.StatusStackCap : 3;
            site.Kernel.TryGetStatus(victim, out _, out _, out float dotDps, out _, out _);
            // 期望 = 4 秒 ×（分裂液 SplitDps，多滩只算一次 + 酸沼 2% × 1000 + 酸蚀持续伤害 = 每层伤害 × 封顶层数）；三滩叠加会多出 2 × SplitDps × 4。
            float expect = 4f * (row.SplitDps + 1000f * 0.02f + dotDps);
            float stacked = expect + 4f * row.SplitDps * 2f;
            float perStack = site.Kernel.StatusFxOf(math.tzcnt(Bit("Acid"))).Amount;
            Expect(puddles == 3 && splitCue && Mathf.Abs(lost - expect) <= 25f && stacks == cap && Mathf.Abs(dotDps - perStack * cap) < 0.01f,
                $"B6 沉淀巨罐被击毁分裂成 {puddles} 滩会爬行的腐蚀液（提示 {splitCue}）；负向：3 滩贴着同一台站在酸沼里的机器，4 秒掉 {lost:0.#}（≈ 分裂液 {row.SplitDps}/秒 + 酸沼 2%/秒 + 酸蚀 {dotDps:0.#}/秒 = {expect:0}，" +
                $"不是三滩叠加的 {stacked:0}）；酸蚀叠到上限 {stacks}/{cap} 层，持续伤害按封顶层数（{perStack} × {cap}）");

            RunFor(site, row.SplitLife + 1f);
            Expect(site.CountPuddles() == 0, $"B6b 分裂液 {row.SplitLife} 秒后蒸发（剩 {site.CountPuddles()} 滩）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckAcidMarshKernel()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 30f, 60f) }, 0.02f, 0.5f);
            int inRing = Player(site, new Vector2(45f, 0f), Gun(0.01f), 1000f);
            int wet = Player(site, new Vector2(-45f, 0f), Gun(0.01f), 1000f);
            int hole = Player(site, new Vector2(0f, 0f), Gun(0.01f), 1000f);
            BenchEnemy(site, s, "enemy_sprayer", "b_native", new Vector2(0f, 45f)); // 离每台机器都超过感知距离，只站在酸沼里
            int native = UnitOf(site, "b_native");
            float n0 = View(site, native).Health;
            site.Kernel.ApplyStatus(wet, Bit("Wet"), 30f, 0f, 0f, 0f, 0);
            RunFor(site, 3f);
            float a = 1000f - View(site, inRing).Health;
            float w = 1000f - View(site, wet).Health;
            float h = 1000f - View(site, hole).Health;
            float nl = n0 - View(site, native).Health;
            Expect(Mathf.Abs(a - 60f) < 1f && Mathf.Abs(w - 30f) < 1f && h < 0.01f && nl < 0.01f && site.Kernel.Counters.AcidTicks >= 3 && site.InAcidMarsh(new Vector2(45f, 0f))
                   && !site.InAcidMarsh(Vector2.zero),
                $"B7 酸沼（DEBT-FG8EXP02-03）：圆环里的机器 3 秒掉 {a:0.#}（每秒 2% × 1000）；湿润（冷却液冲淡）只掉 {w:0.#}；圆环内圈外的机器不掉（{h:0.#}）；澄净单位不怕酸（{nl:0.#}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckPoolWash()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_sprayer", "b_spray", new Vector2(0f, 0f));
            Player(site, new Vector2(6f, 0f), Gun(0.01f), 4000f);
            for (int i = 0; i < 60 * 8 && site.CountZonesOfKind(CombatConst.ZoneKindPool) == 0; i++)
            {
                RunFor(site, Dt);
            }
            CombatZone pool = default;
            for (int z = 0; z < site.Kernel.ZoneCount; z++)
            {
                if (site.Kernel.TryGetZone(z, out CombatZone zn) && zn.Kind == CombatConst.ZoneKindPool)
                {
                    pool = zn;
                    break;
                }
            }
            long w0 = site.Kernel.Counters.PoolsWashed;
            site.Kernel.SpawnFieldZone(CombatFaction.Player, pool.Pos, 2f, 4f, 0f, Bit("Wet"), 2f, 0f, 0f, 0f);
            RunFor(site, 1f);
            Expect(pool.Radius > 0f && site.Kernel.Counters.PoolsWashed > w0 && site.CountZonesOfKind(CombatConst.ZoneKindPool) == 0 && ClarityCombatFeedback.PoolWashedCues > 0,
                $"B8 应对“冷却液冲洗”：带湿润标签的区域盖到酸液池上 → 池子被冲淡、很快消失（冲洗 {w0} → {site.Kernel.Counters.PoolsWashed}，剩 {site.CountZonesOfKind(CombatConst.ZoneKindPool)} 块），有提示");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckAir()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 0f, 50f) }, 0.02f, 0.5f);
            BenchEnemy(site, s, "enemy_deact_drone", "b_drone", new Vector2(0f, 0f));
            int drone = UnitOf(site, "b_drone");
            int melee = Player(site, new Vector2(1.2f, 0f), Melee(30f), 9999f);
            int gun = Player(site, new Vector2(-6f, 0f), Gun(5f), 9999f);
            float d0 = View(site, drone).Health;
            CombatFireResult m = site.Kernel.FireAt(melee, drone, _t);
            string reason = site.FireReason(m, 0, null);
            CombatFireResult g = site.Kernel.FireAt(gun, drone, _t);
            site.ProcessEvents();
            float afterGun = View(site, drone).Health;
            bool airFlag = Cu(site, drone).Air == 1;
            Expect(airFlag && m == CombatFireResult.CannotHitAir && reason == GameText.Get("combat.fire.cannot_hit_air") && g == CombatFireResult.Ok && afterGun < d0,
                $"B9 灭活无人机在空中（{airFlag}）：格斗打不到（{m}，原因“{reason}”），射弹打得到（{g}，{d0:0} → {afterGun:0}）");
            CombatSites.Close(Bench, s, dropRecord: true);

            // B9b：空中单位不受酸沼与地面区域影响——先把无人机的酸蚀抗性清零（只能靠“在空中”挡住），脚下再铺一块会伤人、挂燃烧的地面区域；对照：同一块区域里的地面敌人掉血、挂上燃烧。
            s = NewWorldState(1);
            site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 0f, 50f) }, 0.02f, 0.5f);
            BenchEnemy(site, s, "enemy_deact_drone", "b_air", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_settling_tank", "b_ground", new Vector2(1.5f, 0f));
            int air = UnitOf(site, "b_air");
            int ground = UnitOf(site, "b_ground");
            site.SetAcidResist(air, 0f);
            site.SetAcidResist(ground, 0f);
            site.Kernel.SpawnFieldZone(CombatFaction.Player, new double2(0.7, 0.0), 3f, 5f, 20f, Bit("Fire"), 3f, 4f, 0f, 0f);
            float a0 = View(site, air).Health;
            float g0 = View(site, ground).Health;
            RunFor(site, 3f);
            bool airClean = Mathf.Approximately(View(site, air).Health, a0) && !HasStatus(site, air, Bit("Fire"));
            bool groundHit = View(site, ground).Health < g0 - 20f && HasStatus(site, ground, Bit("Fire"));
            Expect(airClean && groundHit,
                $"B9b 空中单位不受酸沼与地面区域影响（酸蚀抗性清零后站在酸沼 + 燃烧区上空 3 秒：耐久 {a0:0} → {View(site, air).Health:0}、没挂燃烧 {airClean}）；" +
                $"对照：同一块区域里的地面敌人掉血并挂燃烧（{g0:0} → {View(site, ground).Health:0}，{groundHit}）");
            CombatSites.Close(Bench, s, dropRecord: true);

            // 投放：有投放目标（净化塔）时直飞过去、悬停预警 ≥ 0.8 秒后投放并自毁；预警期间被击落 = 投放不发生（应对“对空火力”）。
            s = NewWorldState(1);
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_deact_drone", "b_d1", new Vector2(0f, 0f));
            int d1 = UnitOf(site, "b_d1");
            site.SetPolluteTargets(new List<float3> { new float3(14f, 0f, 3f) });
            double hover = -1, dropped = -1;
            for (int i = 0; i < 60 * 12 && dropped < 0; i++)
            {
                RunFor(site, Dt);
                if (hover < 0 && Cu(site, d1).Pollute == 1)
                {
                    hover = _t;
                }
                if (site.Kernel.Counters.Repollutions > 0)
                {
                    dropped = _t;
                }
            }
            bool selfDestroyed = !View(site, d1).Alive;
            bool relayed = ClarityCombatFeedback.RepollutedEvents >= 1 && ClarityCombatFeedback.DroneWarnCues > 0;
            BenchEnemy(site, s, "enemy_deact_drone", "b_d2", new Vector2(0f, 4f));
            int d2 = UnitOf(site, "b_d2");
            int aa = Player(site, new Vector2(10f, 6f), Gun(999f), 9999f);
            long rp0 = site.Kernel.Counters.Repollutions;
            bool shotDown = false;
            for (int i = 0; i < 60 * 12 && !shotDown; i++)
            {
                RunFor(site, Dt);
                if (Cu(site, d2).Pollute == 1)
                {
                    site.Kernel.FireAt(aa, d2, _t);
                    site.ProcessEvents();
                    shotDown = !View(site, d2).Alive;
                }
            }
            RunFor(site, 2f);
            Expect(hover > 0 && dropped - hover >= 0.8 - 1e-3 && selfDestroyed && relayed && shotDown && site.Kernel.Counters.Repollutions == rp0,
                $"B9c 投放：飞到净化塔附近悬停预警 {dropped - hover:0.00} 秒（≥ 0.8）→ 投放并自毁（{selfDestroyed}），再污染事件转给订阅者（{relayed}，再污染本身归 FG10-RAID-01）；" +
                $"预警期间被击落 → 投放不发生（{shotDown}，投放计数不变）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSwarm()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_corrosion_swarm", "b_swarm", new Vector2(0f, 0f));
            int machine = Player(site, new Vector2(0f, 4f), Gun(0.01f), 4000f);
            int building = Player(site, new Vector2(7f, 0f), Gun(0.01f), 4000f, CombatUnitKind.Structure);
            RunFor(site, 6f);
            float bl = 4000f - View(site, building).Health;
            float ml = 4000f - View(site, machine).Health;
            Expect(bl > 0f && ml < 0.01f,
                $"B10 腐蚀蜂群“专门腐蚀建筑”：机器离得更近（4 米），它仍先去打 7 米外的建筑（建筑掉 {bl:0.#}、机器掉 {ml:0.#}）");
            site.Kernel.Kill(building, 0);
            RunFor(site, 6f);
            Expect(4000f - View(site, machine).Health > 0f, $"B10b 对照：没有建筑时它照常追打机器（机器掉 {4000f - View(site, machine).Health:0.#}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static CombatSite BuildMixedBench(CampaignState s)
        {
            CombatSite site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 0f, 12f) }, 0.02f, 0.5f);
            BenchEnemy(site, s, "enemy_sprayer", "m_spray", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_ln2_pump", "m_ln2", new Vector2(0f, 20f));
            BenchEnemy(site, s, "enemy_oil_crawler", "m_crawl", new Vector2(20f, 0f));
            BenchEnemy(site, s, "enemy_settling_tank", "m_tank", new Vector2(-20f, 0f));
            BenchEnemy(site, s, "enemy_deact_drone", "m_drone", new Vector2(0f, -20f));
            BenchEnemy(site, s, "enemy_corrosion_swarm", "m_swarm", new Vector2(20f, 20f));
            Player(site, new Vector2(6f, 0f), Gun(0.01f), 9999f);
            Player(site, new Vector2(4f, 20f), Gun(0.01f), 9999f);
            Player(site, new Vector2(26f, 0f), Gun(0.01f), 9999f);
            Player(site, new Vector2(-16f, 0f), Gun(0.01f), 9999f);
            Player(site, new Vector2(4f, -20f), Gun(0.01f), 9999f);
            Player(site, new Vector2(26f, 20f), Gun(0.01f), 9999f, CombatUnitKind.Structure);
            return site;
        }

        private static void CheckKernelSnapshot()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = BuildMixedBench(s);
            RunFor(site, 6f);
            site.Kernel.Damage(UnitOf(site, "m_tank"), 999999f, 0);
            RunFor(site, 0.5f);
            CombatKernel k = site.Kernel;
            byte[] snap = k.Serialize();
            ulong h0 = k.StateHash();
            using var b = new CombatKernel(k.Config, 64);
            CombatLoadResult lr = b.Load(snap);
            // 反应规则、状态位效果、酸沼与澄净状态位是派生量（不进快照）：读档后由热更层按表 / 规划层重写——这里照同一份写回。
            b.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            b.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            b.SetAcidMarsh(new List<float4> { new float4(0f, 0f, 0f, 12f) }, 0.02f, 0.5f);
            b.SetClarityBits(Bit("Acid"), Bit("Wet"), Bit("Oil"), Bit("Fire"));
            ulong h1 = b.StateHash();
            double t0 = _t;
            for (int i = 0; i < 240; i++)
            {
                t0 += Dt;
                k.Step(Dt, t0);
                b.Step(Dt, t0);
            }
            bool cont = k.StateHash() == b.StateHash();
            Expect(lr == CombatLoadResult.Ok && h0 == h1 && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 15
                   && k.CountPuddles() == b.CountPuddles() && k.CountZonesOfKind(CombatConst.ZoneKindPool) == b.CountZonesOfKind(CombatConst.ZoneKindPool),
                $"B11 内核快照（格式 {CombatConst.FormatVersion}）：喷淋 / 受寒 / 自爆 / 枢轴 / 飞行 / 分裂液（{k.CountPuddles()} 滩）/ 喷淋池与油迹随快照往返，状态哈希一致（{h0 == h1}），读回后接着跑 4 秒逐位相同（{cont}）（{lr}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckDeterminism()
        {
            // FGR-BASE-021 / FGR-FAC-008 ③：确定性仿真——同一场景跑两遍逐位相同（内核没有观察输入，表现只读仿真状态）。
            ulong Run1()
            {
                CampaignState s = NewWorldState(1);
                CombatSite site = BuildMixedBench(s);
                RunFor(site, 10f);
                ulong h = site.Kernel.StateHash();
                CombatSites.Close(Bench, s, dropRecord: true);
                return h;
            }
            ulong a = Run1();
            ulong b = Run1();
            Expect(a == b, $"B12 确定性：六种澄净单位混战 10 秒跑两遍状态哈希相同（{a:X16} / {b:X16}）");
        }

        // ─────────────────────────────── C 世界 ───────────────────────────────

        private static void CheckAcidWorld()
        {
            CampaignState s = NewWorld(42);
            WorldPlan plan = WorldGenService.PlanFor(s);
            PlannedTerritory t = plan?.Find(EnemyDefs.FactionClarity);
            IReadOnlyList<float4> shapes = AcidMarshService.ActiveShapes(s);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool kernel = home != null && home.AcidShapeCount == shapes.Count && shapes.Count > 0;
            var beltPoint = t != null ? new Vector2(t.CenterX + t.Radius + t.HazardWidth * 0.5f, t.CenterY) : Vector2.zero;
            Hazard hz = t != null ? HomeExplorationService.HazardAt(s, Mathf.FloorToInt(beltPoint.x), Mathf.FloorToInt(beltPoint.y), visibleOnly: false, out _, out bool belt) : null;
            bool agree = hz != null && hz.Id == AcidMarshService.HazardId && home != null && home.InAcidMarsh(beltPoint) && AcidMarshService.ZoneAt(s, beltPoint) == t.Id + "#belt";
            bool homeDry = home != null && !home.InAcidMarsh(Vector2.zero) && AcidMarshService.ZoneAt(s, Vector2.zero) == null;
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act); // 澄净领地第二幕起在地图上显示（悬停只对显示的领地）
            }
            bool described = HomeExplorationService.TryDescribeHazard(s, Mathf.FloorToInt(beltPoint.x), Mathf.FloorToInt(beltPoint.y), out _, out string body);
            string active = AcidMarshService.HoverLine();
            Expect(t != null && kernel && agree && homeDry && active.Contains("2") && described && body.Contains(active) && !body.Contains(GameText.Get("hazard.hover.pending")),
                $"C1 酸沼（规划层，按种子放置）：{shapes.Count} 片（外圈带 + 领地酸沼），星球战斗内核已写入同样 {home?.AcidShapeCount} 片；外圈带上一点：地图悬停判定与内核判定一致（{agree}）；" +
                $"家园核心不在酸沼（{homeDry}）；悬停写“{active}”（不再是“只作标记”）");

            int unit = home.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(beltPoint.x, beltPoint.y), Home = new double2(beltPoint.x, beltPoint.y), Radius = 0.6f, Health = 1000f, MaxHealth = 1000f, Weapon = -1,
                BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            float lost = home.Kernel.TryGetUnit(unit, out CombatUnitView v) ? 1000f - v.Health : -1f;
            Expect(lost >= 40f && lost <= 80f,
                $"C1b 世界里站在酸沼带上的机器 3 个游戏秒掉 {lost:0.#} 耐久（每秒 2% × 1000，按游戏秒结算）");
            home.Kernel.Despawn(unit);
        }

        private static void CheckGarrisonAndSiege()
        {
            CampaignState s = NewWorld(1);
            s.Factions.ClarityCounterId = string.Empty; // 今天没有对策（基线）
            s.Factions.ClarityCounterDay = Math.Max(1, s.Clock.Day);
            var types = new List<string>();
            TerritoryControlService.GarrisonEnemyTypes(EnemyDefs.FactionClarity, 10, types);
            bool own = types.Count == 10 && types.All(id => Row(id)?.Faction == EnemyDefs.FactionClarity);
            Expect(own,
                $"C2 DEBT-FG8EXP03-02：澄净据点驻军迎战展开的是本阵营单位（10 台：{string.Join(",", types.Distinct())}）");

            // 反制锁定为“液氮压制”时：编成末尾换成液氮泵车（总台数不变）。
            s.Factions.ClarityCounterId = EnemyDefs.ConditionCounterCryo;
            s.Factions.ClarityCounterDay = Math.Max(1, s.Clock.Day);
            var cryo = new List<string>();
            int applied0 = TerritoryControlService.ClarityCounterApplied;
            TerritoryControlService.GarrisonEnemyTypes(EnemyDefs.FactionClarity, 10, cryo);
            int pumps0 = types.Count(x => x == "enemy_ln2_pump");
            int pumps1 = cryo.Count(x => x == "enemy_ln2_pump");
            Expect(cryo.Count == 10 && pumps1 > pumps0 && TerritoryControlService.ClarityCounterApplied > applied0,
                $"C2b FGR-FAC-004：反制锁定“液氮压制”后澄净驻军多了液氮泵车（{pumps0} → {pumps1} 台，总数 {cryo.Count} 不变）");

            // 攻城 / 据点展开的澄净单位带专属参数（与区域同一份翻译）；当天锁定“绝缘涂层”时可挂绝缘的单位挡掉电击类标签。
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            RaidCatalog.TryGetUnit("clarity.sprayer", out RaidUnitDef sprayer);
            RaidCatalog.TryGetUnit("clarity.deact_drone", out RaidUnitDef drone);
            RaidCatalog.TryGetUnit("clarity.settling_tank", out RaidUnitDef tank);
            RaidCatalog.TryGetUnit("clarity.oil_crawler", out RaidUnitDef crawler);
            int su = SiegeService.SpawnOne(home, 990201, new Vector2(10f, 10f), Vector2.zero, sprayer, false);
            int du = SiegeService.SpawnOne(home, 990201, new Vector2(12f, 10f), Vector2.zero, drone, false);
            int tu = SiegeService.SpawnOne(home, 990201, new Vector2(14f, 10f), Vector2.zero, tank, false);
            int cu = SiegeService.SpawnOne(home, 990201, new Vector2(16f, 10f), Vector2.zero, crawler, false);
            home.TryGetUnitSilent(su, out _, out CombatBehaviorProfile sp);
            home.TryGetUnitSilent(tu, out _, out CombatBehaviorProfile tp);
            home.TryGetUnitSilent(cu, out _, out CombatBehaviorProfile cp);
            bool airborne = home.TryGetClarity(du, out CombatClarityUnit dc) && dc.Air == 1;
            uint shock = EnemyClarityParams.InsulateMask();
            bool notInsulated = (sp.ImmuneMask & shock) == 0u;
            s.Factions.ClarityCounterId = EnemyDefs.ConditionCounterInsulate;
            int su2 = SiegeService.SpawnOne(home, 990201, new Vector2(18f, 10f), Vector2.zero, sprayer, false);
            home.TryGetUnitSilent(su2, out _, out CombatBehaviorProfile sp2);
            bool insulated = (sp2.ImmuneMask & shock) == shock && shock != 0u;
            Expect(su > 0 && du > 0 && tu > 0 && cu > 0 && sp.SprayRange > 0f && sp.SprayWarn >= 0.8f && tp.PivotRadius > 0f && tp.SplitCount >= 3f && cp.BurstRadius > 0f
                   && cp.DeflagMult > 0f && airborne && notInsulated && insulated && sp.AcidResist >= 1f,
                $"C3 攻城 / 据点展开中的澄净专属语法：喷洒车带喷淋参数（射程 {sp.SprayRange}、蓄压 {sp.SprayWarn}）、巨罐带枢轴与分裂（{tp.PivotRadius} 米、{tp.SplitCount} 滩）、爬行者带自爆与爆燃（{cp.BurstRadius} 米 × {cp.DeflagMult}）、" +
                $"无人机在空中（{airborne}）；绝缘涂层只在当天锁定时挂（平时 {!notInsulated}，锁定后 {insulated}）；不怕酸（抗性 {sp.AcidResist}）");
            // 审查修复的回归（参数表去重键）：同一地点、同一种单位，精英 / 普通的专属伤害不能并成一行；分裂液耐久吃精英耐久倍率。
            RaidCatalog.TryGetUnit("clarity.ln2_pump", out RaidUnitDef pump);
            int pn = SiegeService.SpawnOne(home, 990201, new Vector2(20f, 10f), Vector2.zero, pump, false);
            int pe = SiegeService.SpawnOne(home, 990201, new Vector2(22f, 10f), Vector2.zero, pump, true);
            int te = SiegeService.SpawnOne(home, 990201, new Vector2(24f, 10f), Vector2.zero, tank, true);
            home.TryGetUnitSilent(pn, out _, out CombatBehaviorProfile pnp);
            home.TryGetUnitSilent(pe, out _, out CombatBehaviorProfile pep);
            home.TryGetUnitSilent(te, out _, out CombatBehaviorProfile tep);
            bool eliteSeparate = pep.SprayDamage > pnp.SprayDamage + 1e-3f && Mathf.Approximately(pep.SprayDamage / pnp.SprayDamage, SiegeCatalog.EliteDamageScale);
            bool splitScaled = Mathf.Approximately(tep.SplitHp, tp.SplitHp * SiegeCatalog.EliteHpScale) && tp.SplitHp > 0f;
            Expect(eliteSeparate && splitScaled,
                $"C3b 参数表按完整字节去重：同一种单位的精英与普通各占一行（喷淋伤害 {pnp.SprayDamage} → 精英 {pep.SprayDamage}）；分裂液耐久与本体同吃精英倍率（{tp.SplitHp} → {tep.SplitHp}）");
            // 审查修复的回归（FGR-FAC-004 出发时定编成）：突袭队伍的绝缘涂层取所属计划排定时的反制，不重读当天锁定；没有计划的队伍才按当天锁定。
            RaidDirectorState dir = RaidDirectorService.StateOf(s);
            RaidPlanRecord[] savedPlans = dir.Plans;
            dir.Plans = new[]
            {
                new RaidPlanRecord { PlanId = "fg10fac01-em", Faction = EnemyDefs.FactionClarity, Counter = "clarity.em" },
                new RaidPlanRecord { PlanId = "fg10fac01-none", Faction = EnemyDefs.FactionClarity, Counter = string.Empty },
            };
            s.Factions.ClarityCounterId = string.Empty; // 今天已不锁绝缘：计划仍按排定时的反制
            bool? planEm = SiegeService.InsulateOf(s, new TransitGroupRecord { PlanId = "fg10fac01-em" });
            bool? planNone = SiegeService.InsulateOf(s, new TransitGroupRecord { PlanId = "fg10fac01-none" });
            bool? harass = SiegeService.InsulateOf(s, new TransitGroupRecord());
            int su3 = SiegeService.SpawnOne(home, 990201, new Vector2(26f, 10f), Vector2.zero, sprayer, false, 1f, 1f, planEm);
            home.TryGetUnitSilent(su3, out _, out CombatBehaviorProfile sp3);
            s.Factions.ClarityCounterId = EnemyDefs.ConditionCounterInsulate; // 今天锁绝缘：没选绝缘的计划仍不挂
            int su4 = SiegeService.SpawnOne(home, 990201, new Vector2(28f, 10f), Vector2.zero, sprayer, false, 1f, 1f, planNone);
            home.TryGetUnitSilent(su4, out _, out CombatBehaviorProfile sp4);
            dir.Plans = savedPlans;
            Expect(planEm == true && planNone == false && harass == null && (sp3.ImmuneMask & shock) == shock && (sp4.ImmuneMask & shock) == 0u,
                $"C10 突袭队伍的绝缘取计划的反制（计划=绝缘 → {planEm}，计划=无 → {planNone}，无计划 → {(harass.HasValue ? harass.Value.ToString() : "按当天")}）；" +
                $"当天未锁也挂（{(sp3.ImmuneMask & shock) == shock}），当天锁了但计划没选也不挂（{(sp4.ImmuneMask & shock) != 0u}）");
            foreach (int u in new[] { su, du, tu, cu, su2, pn, pe, te, su3, su4 })
            {
                home.Kernel.Despawn(u);
            }
            s.Factions.ClarityCounterId = string.Empty;
        }

        // ─────────────────────────────── D 反制（FGT-FAC-005） ───────────────────────────────

        private const string BpFire = "bp_fg10fac01_fire";
        private const string BpFuse = "bp_fg10fac01_fuse";
        private const string BpEm = "bp_fg10fac01_em";
        private const string BpPlain = "bp_fg10fac01_plain";

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
            Add(BpFire, "fw_burntrail");
            Add(BpFuse, "fw_homing", "fw_split");
            Add(BpEm, "fw_arcchain", "fw_vortex");
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

        private static void CheckCounter()
        {
            CampaignState s = NewWorld(1);
            MakeBlueprints(s);
            SetFleet(s, BpFire, 3);
            ClarityCounterService.Shares(s, out float fire, out float fuse0, out float em0);
            string fireDecide = ClarityCounterService.Decide(s);
            string locked = ClarityCounterService.Locked(s);
            string foundryFire = FoundryCounterService.Decide(s);
            SetFleet(s, BpFuse, 3);
            string fuseDecide = ClarityCounterService.Decide(s);
            string stillLocked = ClarityCounterService.Locked(s);
            string preview = ClarityCounterService.Preview(s);
            s.Clock.Day += 1;
            ClarityCounterService.WorldStep(s);
            string nextDay = ClarityCounterService.Locked(s);
            SetFleet(s, BpEm, 3);
            string emDecide = ClarityCounterService.Decide(s);
            SetFleet(s, BpPlain, 3);
            string plain = ClarityCounterService.Decide(s);
            string silentPlain = SilentCounterService.Preview(s);
            var clarityIds = new[] { EnemyDefs.ConditionCounterCryo, EnemyDefs.ConditionCounterSwarm, EnemyDefs.ConditionCounterInsulate, ClarityCounterService.CounterNone };
            Expect(fire >= 0.99f && fireDecide == EnemyDefs.ConditionCounterCryo && locked == fireDecide && fuseDecide == EnemyDefs.ConditionCounterSwarm && stillLocked == locked
                   && preview == fuseDecide && nextDay == EnemyDefs.ConditionCounterSwarm && emDecide == EnemyDefs.ConditionCounterInsulate && plain == ClarityCounterService.CounterNone
                   && (foundryFire == ClarityCounterService.CounterNone || !clarityIds.Contains(foundryFire))
                   && (silentPlain == ClarityCounterService.CounterNone || !clarityIds.Contains(silentPlain)),
                $"D1 FGT-FAC-005：燃烧构筑（燃烧占比 {fire:0.00}）→ 澄净选“液氮压制”（{fireDecide}），当天锁定；改成引信构筑当天不变（{stillLocked}），预测“蜂群饱和”（{preview}），日切后锁定为蜂群（{nextDay}）；" +
                $"电磁构筑 → 绝缘涂层（{emDecide}）；没有固件 → 没有对策（{(plain == string.Empty ? "无" : plain)}）；同一燃烧构筑铸造在自己的技术树里选（{(foundryFire == string.Empty ? "无" : foundryFire)}），静默同理（{(silentPlain == string.Empty ? "无" : silentPlain)}）");
            Expect(fireDecide == EnemyDefs.ConditionCounterCryo && fuse0 == 0f && em0 == 0f, "D1b 燃烧构筑里没有引信 / 电磁固件（夹具自证）");

            // 突袭反制映射与情报行（FGR-FAC-004：突袭在出发时按当天的状态生成；情报面板可以看到当前的反制和下一次更新的预测）。
            // 第一幕澄净领地还没显示：情报不写澄净这一行（不剧透）；第二幕起写。
            RaidCounter rc = ClarityCounterService.RaidCounterFor(ClarityCounterService.Locked(s));
            IntelRecord ir1 = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            bool hiddenAct1 = s.Progress == null || s.Progress.Act >= 2 || (ir1 != null && !ir1.HasClarityCounter);
            if (s.Progress != null)
            {
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            IntelRecord ir = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            string sum = ir != null ? IntelService.Summary(s, ir, GameClock.Ticks) : string.Empty;
            bool intel = ir != null && ir.HasClarityCounter && ir.ClarityCounter == EnemyDefs.ConditionCounterSwarm && ir.ClarityCounterNext == ClarityCounterService.CounterNone
                         && sum.Contains(ClarityCounterService.CounterName(EnemyDefs.ConditionCounterSwarm));
            Expect(rc != null && rc.Id == "clarity.fuse" && rc.Tag == "swarm" && intel && hiddenAct1,
                $"D2 突袭按当天锁定选反制行（{rc?.Id} → 标签 {rc?.Tag}）；第一幕情报不写澄净（{hiddenAct1}），第二幕起“敌方反制预览”写出澄净今天的锁定与下一次预测：{sum}");
        }

        // ─────────────────────────────── E 反馈与图鉴 ───────────────────────────────

        private static void CheckFeedbackAndCodex()
        {
            // 第一次遭遇空中敌人：走正式的遭遇入口（与攻城展开 / 交火同一个），按 airborne 标签发钩子。
            MechanicCodex.ResetContentForTests();
            Progression.MechanicCodex.OnEnemyEncountered("enemy_deact_drone");
            string[] hooks = { "clarity.first_spray", "clarity.first_freeze", "clarity.first_burst", "clarity.first_slick_ignite", "clarity.first_pivot",
                "clarity.first_split", "clarity.first_acid", "clarity.first_pool_washed", "clarity.first_drone", "clarity.first_air" };
            var unseen = hooks.Where(h => !TouchedHooks.Contains(h)).ToList(); // 本次运行真的触发过（不看跨战役的“已看过”记录）
            string[] entries = { "codex.clarity.spray", "codex.clarity.pivot", "codex.clarity.crawler", "codex.clarity.chill", "codex.clarity.air", "codex.clarity.acid" };
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
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            var badLinks = new List<string>();
            foreach ((string id, _, _, _) in Units)
            {
                string links = Row(id)?.CodexLinks ?? string.Empty;
                foreach (string l in links.Split(','))
                {
                    string x = l.Trim();
                    if (x.StartsWith("codex.") && ConfigSystem.Instance.Tables.TbCodexEntry.DataList.All(c => c.Id != x))
                    {
                        badLinks.Add(id + ":" + x);
                    }
                }
            }
            Expect(unseen.Count == 0 && missing.Count == 0 && badLinks.Count == 0,
                $"E1 B07 / B14：每条专属行为与第一次遭遇空中敌人都在本次运行里发了引导钩子（未发 {unseen.Count}：{string.Join(",", unseen)}）；6 条澄净机制图鉴条目中英齐全（缺 {missing.Count}）；敌人图鉴互链可达（坏链 {badLinks.Count}）");
            string hover = AcidMarshService.HoverLine();
            string frozen = GameText.Get("combat.fire.frozen");
            string air = GameText.Get("combat.fire.cannot_hit_air");
            Expect(hover.Length > 0 && frozen != "combat.fire.frozen" && air != "combat.fire.cannot_hit_air"
                   && ClarityCombatFeedback.SprayWarnCues > 0 && ClarityCombatFeedback.PivotWarnCues > 0 && ClarityCombatFeedback.BurstWarnCues > 0 && ClarityCombatFeedback.DroneWarnCues > 0
                   && ClarityCombatFeedback.AcidCues > 0,
                $"E2 FGR-FAC-008 ①：四种预警（喷嘴蓄压 {ClarityCombatFeedback.SprayWarnCues} / 枢轴蓄压 {ClarityCombatFeedback.PivotWarnCues} / 自爆引信 {ClarityCombatFeedback.BurstWarnCues} / 投放悬停 {ClarityCombatFeedback.DroneWarnCues}）" +
                $"都有声音 + 字幕；酸沼腐蚀提示 {ClarityCombatFeedback.AcidCues}；开火失败原因“{frozen}”“{air}”");
        }

        // ─────────────────────────────── F 性能 ───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            site.SetAcidMarsh(new List<float4> { new float4(70f, 40f, 0f, 60f) }, 0.02f, 0.5f);
            string[] types = { "enemy_sprayer", "enemy_oil_crawler", "enemy_ln2_pump", "enemy_deact_drone", "enemy_corrosion_swarm", "enemy_settling_tank" };
            for (int i = 0; i < 200; i++)
            {
                BenchEnemy(site, s, types[i % types.Length], "p" + i, new Vector2((i % 20) * 7f, (i / 20) * 7f));
            }
            // 40 台机器（含 8 座建筑）穿插进敌人网格：每个敌人 16 米内都有目标，喷淋 / 枢轴 / 自爆 / 油迹 / 喷淋池 / 分裂液都真的在跑（审查修复：此前机器离敌人 27 米以上，计时的是空转）。
            for (int i = 0; i < 40; i++)
            {
                Player(site, new Vector2((i % 10) * 14f + 3.5f, (i / 10) * 16f + 7f), Gun(1f), 1e6f, i % 5 == 0 ? CombatUnitKind.Structure : CombatUnitKind.Machine);
            }
            RunFor(site, 9f); // 喷淋池与油迹到稳态
            long sprays = site.Kernel.Counters.Sprays;
            long pivots = site.Kernel.Counters.PivotHits;
            long bursts = site.Kernel.Counters.Bursts;
            int warm = site.CountZonesOfKind(CombatConst.ZoneKindPool) + site.CountZonesOfKind(CombatConst.ZoneKindSlick);
            Expect(sprays > 0 && pivots > 0 && bursts > 0 && warm >= 20,
                $"F0 性能场景的负载是真的（计时前：喷淋 {sprays} 次、扫中 {pivots} 次、自爆 {bursts} 次、喷淋池 + 油迹 {warm} 块）");
            var sw = Stopwatch.StartNew();
            int steps = 120;
            RunFor(site, steps * Dt);
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            int pools = site.CountZonesOfKind(CombatConst.ZoneKindPool) + site.CountZonesOfKind(CombatConst.ZoneKindSlick);
            CombatSites.Close(Bench, s, dropRecord: true);
            PerfGate.Expect(perStep > 0, $"F1 性能（Editor batchmode，Mono JIT，一次测量；真机 IL2CPP + HybridCLR 另测 FG15-SYS-02）：200 个澄净单位（六种各占六分之一，" +
                                         $"场上喷淋池 + 油迹 {pools} 块，酸沼 1 片）+ 40 台机器的内核一步 {perStep:F3} ms",
                new[] { new PerfGate.Metric("澄净单位内核一步(ms)", perStep, 4.0, false) }, Expect, Line);
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
            CampaignState s = CampaignState.CreateNew("fgclarity-" + seed, "Standard", seed);
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
            CampaignState s = CampaignState.CreateNew("fgclarity-" + seed, "Standard", seed);
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
                Debug.Log($"[FgClarityFactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
