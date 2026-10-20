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
using GameLogic.Campaign.Feedback;
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
    /// FG9-FAC-02 铸造扩充与主核心阶段三（FG09 第 5 章；FGR-FAC-002 / 004 / 006 / 008；FG17 FGR-GEN-024；验收 FGT-FAC-003、005，另覆盖 002 / 009 的铸造部分）的自检——起真实系统、断言行为：
    /// A 数据：4 类新单位 + 精英攻城重甲入表（读法 / 标签 / 层级 / 图鉴中英文本 / 组件与固件真实 ID）、编成与新增点位能落位、Demo 布局 + 新增点位零违规、
    ///   专属行为预警 ≥ 0.8 秒（表层）、突袭 / 攻城 / 驻军 / 精英残部编成、“按组件与固件装配武器”（DEBT-FG9DATA01-02）。
    /// B 内核专属语法（战斗内核真实步进）：对空高炮（只打无人机与飞行中的弹体、不打地面、装填完成灯预警实测 ≥ 0.8 秒、近战可拆）、空爆炮台（集群受伤、散开的不受伤、无人机被击落）、
    ///   布雷车与子母雷（铺雷、不能被单体锁定、引信预警实测 ≥ 0.8 秒、范围伤害 / 挪动提前引爆、上限、失效）、装填机（附近火炮出手更快）、
    ///   步进炮架炮驻锄（架设 → 瞄准线 → 开火；架设中被击退 / 冻结回到未架设——FG09 第 10 节负向）与近距盲区、攻城重甲（正面几乎免伤、背部 +50%、受限转向、蓄力实测 ≥ 0.8 秒的冲锋与击退）、
    ///   熔渣热区散热 -30%、快照格式 14 往返逐位一致。
    /// C 铸造主核心（从铸造前哨入场、越过核心区大门开战）：Demo 阶段原样（护盾 / 阶段一 / 过渡 / 阶段二）+ 阶段三·对空形态（只打空中目标、核心暴露 ×1.5、15 秒）→ 狂暴（开火间隔减半、精英增援）
    ///   → 击败奖励（过载 / 电容蓄力 / 三选一 / 领地平定 / 熔炉心，只发一次）；阶段唯一写入口；FGT-FAC-003 首领战中存读档一致；卡片负向“阶段三窗口期内全灭”→ 复位 + 上次止步阶段 + 首领战前自动存档；
    ///   场地边界（FGR-FAC-006：机器全部离开核心区 20 秒复位、恢复本阶段耐久）；暂停与 0.5x～3x、观察与否结果一致；HUD 文本。
    /// D 熔渣热区（规划层危害区、星球内核写入、悬停说明）。E FGT-FAC-005 反制：不同构筑 → 铸造 / 静默各自选出对应对策、每日锁定、出发带入、前哨追加编成、情报行。
    /// F 突袭 / 攻城单位带布雷 / 装填参数、精英标记与背部弱点。G 对空阵地：3 个种子落在星球锚点平整范围内、投送舱防空覆盖、被拆光后覆盖消失。H 性能（一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgFoundryFactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgFoundryFactionSelfCheck。
    /// </summary>
    public static class FgFoundryFactionSelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string Bench = "fg9fac02-bench";
        private const string Fo = FoundryOutpostLayout.RegionId;
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static readonly int[] Seeds = { 1, 42, 20261017 };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/FG 铸造扩充与主核心阶段三")]
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
            Line("\n[铸造扩充与主核心阶段三] 新单位与专属语法、主核心阶段三与狂暴、场地边界、熔渣热区、反制与突袭、对空阵地（FG9-FAC-02）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfoundry-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                FoundryOutpostCoreBoss.ResetCountersForTests();
                FoundryCombatFeedback.ResetForTests();
                FoundryCounterService.ResetForTests();
                SlagHeatService.ResetForTests();

                Step(CheckData);
                Step(CheckWarningsAndAssembly);
                Step(CheckRaidAndGarrisonRows);
                Step(CheckAntiAir);
                Step(CheckAirburst);
                Step(CheckMines);
                Step(CheckLoader);
                Step(CheckStriderDeployAndBlindSpot);
                Step(CheckSiegeHeavy);
                Step(CheckSlagKernel);
                Step(CheckKernelSnapshot);
                Step(CheckBossPhases);
                Step(CheckBossSaveLoad);
                Step(CheckBossWipeInAntiAir);
                Step(CheckArenaBoundary);
                Step(CheckBossSpeedMatrixAndObservation);
                Step(CheckSlagWorld);
                Step(CheckCounter);
                Step(CheckSiegeUnits);
                Step(CheckBatteryAnchorAndDropPods);
                Step(CheckPerf);
            }
            finally
            {
                HudClose();
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
            Line(_fail == 0 ? $"  铸造扩充与主核心阶段三：全部 {_pass} 项通过" : $"  铸造扩充与主核心阶段三：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static readonly (string id, string behavior, string tier, string tag)[] NewUnits =
        {
            ("enemy_airburst", EnemyDefs.BehaviorSalvo, EnemyDefs.TierNormal, EnemyDefs.TagAirburst),
            ("enemy_minelayer", EnemyDefs.BehaviorMinelay, EnemyDefs.TierNormal, EnemyDefs.TagAssembled),
            ("enemy_aa_gun", EnemyDefs.BehaviorSalvo, EnemyDefs.TierNormal, EnemyDefs.TagAntiAir),
            ("enemy_loader", EnemyDefs.BehaviorReload, EnemyDefs.TierNormal, EnemyDefs.TagAssembled),
            ("enemy_siege_heavy", EnemyDefs.BehaviorCharge, EnemyDefs.TierElite, EnemyDefs.TagAssembled),
        };

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            var bad = new List<string>();
            foreach ((string id, string behavior, string tier, string tag) in NewUnits)
            {
                MechEnemy r = Row(id);
                if (r == null || r.Faction != EnemyDefs.FactionFoundry || r.Behavior != behavior || r.Tier != tier || !EnemyDefs.HasTag(r, tag)
                    || !EnemyBehaviorReadings.TryGet(r.Behavior, out _))
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
            // 图鉴（FGR-FAC-005 / FG09 第 9 节“每种敌人都有图鉴条目”）：名称 / 职能描述 / 弱点（= 应对）中英都有。
            var missingText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach ((string id, _, _, _) in NewUnits)
                {
                    MechEnemy r = Row(id);
                    foreach (string k in new[] { r?.NameKey, r?.DescKey, r?.WeaknessKey })
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
            // 编成：对空阵地 / 反制追加 / 狂暴精英都能落到点位上（fg.TbSitePoint，不是世界生成输入）。
            var unresolved = new List<string>();
            int rows = 0;
            foreach (string cond in new[] { EnemyDefs.ConditionBattery, EnemyDefs.ConditionCounterAntiAir, EnemyDefs.ConditionCounterMinefield, EnemyDefs.ConditionBossWave })
            {
                foreach (EnemyEncounter enc in EnemyDefs.Encounters(Fo, cond))
                {
                    rows++;
                    if (!RegionEnemySites.TrySpawnPosition(Fo, enc.AnchorId, out _))
                    {
                        unresolved.Add(enc.Id);
                    }
                }
            }
            List<string> layout = FoundryOutpostLayout.Validate();
            int battery = FoundryOutpostLayout.BatteryPoints().Count;
            Expect(bad.Count == 0 && missingText.Count == 0 && problems.Count == 0 && unresolved.Count == 0 && rows == 6 && layout.Count == 0 && battery == 2,
                $"A1 铸造 4 类新单位 + 精英攻城重甲入表（读法 / 标签 / 层级 / 组件与固件真实 ID 异常 {bad.Count}：{string.Join(",", bad)}）；图鉴中英文本缺 {missingText.Count}；" +
                $"全表行问题 {problems.Count}；对空阵地 / 反制 / 狂暴编成 {rows} 行全部落位（未落位 {unresolved.Count}）；Demo 布局 + 新增点位净空 / 相机边界零违规（{layout.Count}）；对空炮位 {battery} 个");
        }

        private static void CheckWarningsAndAssembly()
        {
            var weak = new List<string>();
            foreach (MechEnemy r in EnemyDefs.Rows.Where(r => r.Faction == EnemyDefs.FactionFoundry))
            {
                bool exclusive = r.Behavior == EnemyDefs.BehaviorSalvo || r.Behavior == EnemyDefs.BehaviorMinelay || r.Behavior == EnemyDefs.BehaviorCharge || r.SalvoCount > 0f;
                if (exclusive && r.WarnSeconds < 0.8f)
                {
                    weak.Add(r.Id + " warn " + r.WarnSeconds);
                }
                if (r.DeploySeconds > 0f && r.DeploySeconds < 0.8f)
                {
                    weak.Add(r.Id + " deploy " + r.DeploySeconds);
                }
                if (r.WeaponDamage >= 20f && r.WeaponAim < 0.8f && r.WarnSeconds < 0.8f)
                {
                    weak.Add(r.Id + " heavy");
                }
            }
            MechEnemy strider = Row("enemy_strider");
            Expect(weak.Count == 0 && strider != null && strider.DeploySeconds >= 0.8f && strider.MinRange > 0f,
                $"A2 FGT-FAC-002 / 009（表层）：铸造全部专属行为（齐射装填完成灯 / 布雷引信 / 冲锋蓄力 / 架炮驻锄）与大威力攻击的预警 ≥ 0.8 秒（不合格 {weak.Count}：{string.Join("；", weak)}）；" +
                $"步进炮带架炮驻锄 {strider?.DeploySeconds} 秒、近距盲区 {strider?.MinRange} 米（内核实测见 B 段）");

            // DEBT-FG9DATA01-02（铸造部分）：带 assembled 的敌人按“组件与固件”列装配武器——撞角是格斗载体、炮管是射弹载体；Demo 敌人仍是内置武器。
            CampaignState s = NewWorldState(1);
            CombatWeapon heavy = EnemySpawnBuilder.WeaponOf(s, Row("enemy_siege_heavy"));
            CombatWeapon burst = EnemySpawnBuilder.WeaponOf(s, Row("enemy_airburst"));
            CombatWeapon demo = EnemySpawnBuilder.WeaponOf(s, strider);
            bool ok = heavy.Reading.Carrier == CombatCarrier.Melee && heavy.Reading.Area > 0f && burst.Reading.Carrier == CombatCarrier.Projectile
                      && demo.Reading.Carrier == CombatCarrier.Projectile && !demo.Reading.HasFirmwareReading && Mathf.Approximately(heavy.Damage, Row("enemy_siege_heavy").WeaponDamage);
            Expect(ok, $"A3 按组件装配（FGR-FAC-003 同一套规则）：攻城重甲的撞角 = 格斗载体（触及 {heavy.Reading.Area} 米），空爆炮台的炮管 = 射弹载体；伤害仍取敌人表 × 难度（{heavy.Damage}）；Demo 步进炮仍是内置武器");
        }

        private static void CheckRaidAndGarrisonRows()
        {
            string[] want = { "foundry.minelayer", "foundry.loader", "foundry.siege_heavy" };
            bool raid = want.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.Faction == "foundry" && d.Points > 0
                                          && SiegeCatalog.TryGetUnit(d.EnemyTypeId, out _));
            TerritoryControl tc = ConfigSystem.Instance.Tables.TbTerritoryControl.DataList.FirstOrDefault(r => r.Id == "foundry");
            bool garrison = tc != null && tc.GarrisonUnits.Contains("foundry.minelayer") && tc.GarrisonUnits.Contains("foundry.loader")
                            && tc.EliteUnits.Contains("foundry.siege_heavy");
            bool fixedOut = RaidCatalog.Units.All(d => d.EnemyTypeId != "enemy_aa_gun" && d.EnemyTypeId != "enemy_airburst");
            Expect(raid && garrison && fixedOut,
                $"A4 突袭编成（FG09 第 5 章“攻城为主”）：布雷车 / 装填机 / 攻城重甲进铸造突袭且有攻城参数（{raid}）；领地驻军迎战带布雷车与装填机、精英残部带攻城重甲（{garrison}）；固定炮台不进突袭（{fixedOut}）");
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

        private static int Player(CombatSite site, Vector2 at, CombatWeapon wp, float hp = 400f)
        {
            int w = site.WeaponIndex(wp);
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
        }

        private static CombatWeapon Gun(float dmg = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 40f, Damage = dmg, Cooldown = 0.05f, TargetMode = CombatTargetMode.Nearest,
        };

        private static CombatWeapon SlowBolt(float dmg = 5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 40f, Damage = dmg, Cooldown = 0.05f, ProjectileSpeed = 4f, ProjectileRadius = 0.2f,
            ProjectileLife = 12f, TargetMode = CombatTargetMode.Nearest,
        };

        private static CombatWeapon Drones()
        {
            CombatWeapon w = Gun(4f);
            w.Reading.Carrier = CombatCarrier.Summon;
            w.Reading.Drones = 2;
            w.Reading.DroneSeconds = 30f;
            w.Reading.DroneRatio = 0.5f;
            w.Reading.DroneCooldown = 0.5f;
            w.Reading.DroneLeash = 8f;
            return w;
        }

        private static CombatWeapon Area(CombatCarrier carrier, float dmg = 10f, float area = 6f)
        {
            CombatWeapon w = Gun(dmg);
            w.Reading.Carrier = carrier;
            w.Reading.Area = area;
            w.Reading.Cone = 180f;
            w.Reading.Approach = area;
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

        private static CombatFoundryUnit Fu(CombatSite site, int unit) => site.TryGetFoundry(unit, out CombatFoundryUnit f) ? f : default;

        private static void CheckAntiAir()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_aa_gun", "b_aa", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_decoy_relay", "b_dummy", new Vector2(14f, 0f));
            int aa = UnitOf(site, "b_aa");
            int ground = Player(site, new Vector2(4f, 0f), Gun(0.01f));
            int drone = Player(site, new Vector2(11f, 2f), Drones());
            RunFor(site, 1f);
            float g0 = View(site, ground).Health;
            bool idle = Fu(site, aa).Salvo == 0 && site.Kernel.Counters.AirIntercepts == 0;
            CombatFireResult launch = site.Kernel.FireAt(drone, UnitOf(site, "b_dummy"), _t);
            int dronesUp = site.Kernel.DroneCount;
            double t0 = _t;
            double firstIntercept = -1;
            for (int i = 0; i < 60 * 6 && firstIntercept < 0; i++)
            {
                RunFor(site, Dt);
                if (site.Kernel.Counters.AirIntercepts > 0)
                {
                    firstIntercept = _t;
                }
            }
            double warn = firstIntercept - t0;
            RunFor(site, 3f);
            bool cleared = site.Kernel.DroneCount == 0;
            bool groundSafe = Mathf.Approximately(View(site, ground).Health, g0);
            Expect(idle && launch == CombatFireResult.Ok && dronesUp > 0 && firstIntercept > 0 && warn >= 0.8 && cleared && groundSafe && FoundryCombatFeedback.SalvoWarnCues > 0,
                $"B1 对空高炮（FGR-FAC-008“齐射弹幕（对空）”，FGT-FAC-009 实测）：没有空中目标时不开火（{idle}）；无人机升空（{dronesUp} 架）→ 装填完成灯预警 {warn:0.00} 秒（≥ 0.8）→ 一轮齐射击落，" +
                $"3 秒后天上不剩无人机（{cleared}）；它从不打地面单位（贴在 4 米处的机器耐久不变 {groundSafe}）；预警有声音 + 字幕（{FoundryCombatFeedback.SalvoWarnCues}）");

            // 飞行中的弹体同样被击落（“会攻击你的无人机和曲射弹”）；应对“地面近战”：格斗贴身能拆掉它。
            RunFor(site, 6f); // 上一轮齐射后的装填（5 秒）走完
            long ic0 = site.Kernel.Counters.AirIntercepts;
            int bolt = Player(site, new Vector2(-16f, 4f), SlowBolt()); // 慢速弹体从对空高炮头顶飞过（约 7 秒航程）
            for (int k = 0; k < 6; k++)
            {
                site.Kernel.FireAt(bolt, UnitOf(site, "b_dummy"), _t);
                RunFor(site, 0.3f);
            }
            RunFor(site, 4f);
            bool shotDown = site.Kernel.Counters.AirIntercepts > ic0;
            float a0 = View(site, aa).Health;
            int melee = Player(site, new Vector2(1.6f, 0f), Area(CombatCarrier.Melee, 30f, 2.5f));
            CombatFireResult hit = site.Kernel.FireAt(melee, aa, _t);
            site.ProcessEvents();
            Expect(shotDown && hit == CombatFireResult.Ok && View(site, aa).Health < a0,
                $"B1b 飞行中的己方弹体同样被拦截（拦截 {ic0} → {site.Kernel.Counters.AirIntercepts}）；应对“地面近战”：贴身格斗打得动它（{a0:0} → {View(site, aa).Health:0}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckAirburst()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_airburst", "b_burst", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_decoy_relay", "b_dummy", new Vector2(10.6f, 1.6f));
            int a = Player(site, new Vector2(10f, 0f), Gun(0.01f));
            int b = Player(site, new Vector2(10.9f, 0.6f), Gun(0.01f));
            int c = Player(site, new Vector2(10.3f, -0.8f), Drones());
            int far = Player(site, new Vector2(-6f, 12f), Gun(0.01f));
            site.Kernel.FireAt(c, UnitOf(site, "b_dummy"), _t);
            int dronesUp = site.Kernel.DroneCount;
            float a0 = View(site, a).Health, b0 = View(site, b).Health, c0 = View(site, c).Health, f0 = View(site, far).Health;
            double firstHit = -1;
            for (int i = 0; i < 60 * 5 && firstHit < 0; i++)
            {
                RunFor(site, Dt);
                if (View(site, a).Health < a0 || View(site, b).Health < b0 || View(site, c).Health < c0)
                {
                    firstHit = _t;
                }
            }
            RunFor(site, 2f);
            float dmg = Row("enemy_airburst").WeaponDamage;
            bool cluster = a0 - View(site, a).Health >= dmg - 0.01f && b0 - View(site, b).Health >= dmg - 0.01f && c0 - View(site, c).Health >= dmg - 0.01f;
            bool spread = Mathf.Approximately(View(site, far).Health, f0);
            bool dronesDown = site.Kernel.Counters.AirIntercepts > 0;
            Expect(firstHit >= 1.0 - 1e-3 && cluster && spread && dronesUp > 0 && dronesDown,
                $"B2 空爆炮台（FG09 第 5 章“对无人机和密集阵型伤害很高”）：装填完成灯预警 {firstHit:0.00} 秒后一轮空爆，挤在一起的 3 台每台都吃到 ≥ {dmg}（{cluster}），" +
                $"散开 12 米外的那台不受伤（{spread}，应对“分散阵型”）；集群旁的无人机被击落（{dronesDown}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static int FirstMine(CombatSite site) => site.MineCount > 0 ? site.MineAt(0) : 0;

        private static void CheckMines()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_minelayer", "b_layer", new Vector2(0f, 0f));
            int layer = UnitOf(site, "b_layer");
            int p = Player(site, new Vector2(30f, 0f), Gun(0.01f));
            RunFor(site, 4.5f);
            int mine = FirstMine(site);
            CombatFireResult lockOn = site.Kernel.FireAt(p, mine, _t);
            string reason = site.FireReason(lockOn, 0, null);
            bool pickHidden = site.Kernel.TryGetUnit(mine, out CombatUnitView mv) && mv.Flags.HasFlag(CombatUnitFlags.AreaOnly);
            Expect(mine != 0 && site.Kernel.Counters.MinesLaid >= 1 && lockOn == CombatFireResult.AreaOnly && reason == GameText.Get("combat.fire.area_only") && pickHidden,
                $"B3 布雷车（FGR-FAC 铸造“铺设子母雷区，雷区有标记”）：巡逻中铺下第一颗雷（已铺 {site.Kernel.Counters.MinesLaid}）；雷不能被单体锁定（{lockOn}，原因“{reason}”）");

            // 踩雷：引信预警 ≥ 0.8 秒后爆炸（伤害 = 表 effectAmount × 难度）。
            Vector2 mp = new Vector2((float)mv.Position.x, (float)mv.Position.y);
            site.Kernel.SetPosition(p, new double2(mp.x + 0.5f, mp.y));
            float h0 = View(site, p).Health;
            double stepOn = _t;
            double blast = -1;
            bool armed = false;
            for (int i = 0; i < 60 * 3 && blast < 0; i++)
            {
                RunFor(site, Dt);
                armed |= site.Kernel.TryGetFoundry(mine, out CombatFoundryUnit mf) && mf.MineState == 1;
                if (View(site, p).Health < h0)
                {
                    blast = _t;
                }
            }
            float mineDmg = Row("enemy_minelayer").EffectAmount * DifficultyService.EnemyDamageMul(s);
            Expect(armed && blast - stepOn >= 0.8 && Mathf.Abs(h0 - View(site, p).Health - mineDmg) < 0.01f && site.Kernel.Counters.MinesDetonated >= 1 && FoundryCombatFeedback.MineArmedCues > 0,
                $"B3b 踩雷：引信触发（{armed}），预警 {blast - stepOn:0.00} 秒（≥ 0.8，有声音 + 字幕）后爆炸，机器吃 {h0 - View(site, p).Health:0}（= {mineDmg}）");

            // 负向 / 应对：范围伤害提前引爆（附近没有己方单位，没人受伤）；被挪动（牵引）同样提前引爆。
            site.Kernel.SetPosition(p, new double2(60f, 0f));
            int guard = 0;
            while (site.MineCount == 0 && guard++ < 60 * 10)
            {
                RunFor(site, Dt);
            }
            int m2 = FirstMine(site);
            site.Kernel.TryGetUnit(m2, out CombatUnitView m2v);
            long det0 = site.Kernel.Counters.MinesDetonated;
            int aura = Player(site, new Vector2((float)m2v.Position.x + 4.5f, (float)m2v.Position.y), Area(CombatCarrier.Aura, 5f, 6f));
            float auraHp = View(site, aura).Health;
            site.Kernel.FireAt(aura, layer, _t);
            RunFor(site, Dt * 3);
            bool early = site.Kernel.Counters.MinesDetonated == det0 + 1 && !site.Kernel.Exists(m2) || !View(site, m2).Alive;
            bool auraSafe = Mathf.Approximately(View(site, aura).Health, auraHp);
            site.Kernel.SetPosition(aura, new double2(80f, 0f));
            guard = 0;
            while (site.MineCount == 0 && guard++ < 60 * 10)
            {
                RunFor(site, Dt);
            }
            int m3 = FirstMine(site);
            long det1 = site.Kernel.Counters.MinesDetonated;
            site.Kernel.TryGetUnit(m3, out CombatUnitView m3v);
            site.Kernel.SetPosition(m3, m3v.Position + new double2(1.5, 0), resetPrev: false);
            RunFor(site, Dt * 2);
            bool moved = site.Kernel.Counters.MinesDetonated == det1 + 1;
            Expect(early && auraSafe && moved,
                $"B3c 应对“用冲击波等范围伤害或牵引提前引爆”：力场范围伤害扫到雷 → 当场提前引爆（{early}），4.5 米外的力场机器不受伤（{auraSafe}）；雷被挪动（牵引）→ 提前引爆（{moved}）");

            // 上限与失效：自己最多 maxPlaced 颗，雷没人踩 90 秒后自行失效、布雷车会补铺（铺雷总数超过同时存在数）。
            RunFor(site, 100f);
            int alive = site.MineCount;
            long laid = site.Kernel.Counters.MinesLaid;
            int cap = Mathf.RoundToInt(Row("enemy_minelayer").MaxPlaced);
            Expect(alive <= cap && laid > alive + 2,
                $"B3d 布雷上限 {cap} 颗（100 秒后同时存在 {alive} 颗）；没人踩的雷 {EnemyFoundryParams.MineLifeSeconds} 秒后失效、布雷车补铺（累计铺 {laid} 颗）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static int CountHits(CampaignState s, bool withLoader)
        {
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 0f));
            if (withLoader)
            {
                BenchEnemy(site, s, "enemy_loader", "b_loader", new Vector2(-2f, -1.5f));
            }
            int p = Player(site, new Vector2(10f, 0f), Gun(0.01f), 99999f);
            float h0 = View(site, p).Health;
            RunFor(site, 24f);
            float lost = h0 - View(site, p).Health;
            CombatSites.Close(Bench, s, dropRecord: true);
            return Mathf.RoundToInt(lost / (Row("enemy_strider").WeaponDamage * DifficultyService.EnemyDamageMul(s)));
        }

        private static void CheckLoader()
        {
            CampaignState s = NewWorldState(1);
            int alone = CountHits(s, false);
            int boosted = CountHits(s, true);
            Expect(boosted > alone && alone > 0,
                $"B4 装填机（“让附近的火炮装填更快”）：同一台步进炮 24 秒里命中 {alone} 次 → 旁边有装填机时 {boosted} 次（应对“优先击杀”）");
        }

        private static double FirstHitAfter(CombatSite site, int victim, float maxSeconds, Action<double> during = null)
        {
            float h0 = View(site, victim).Health;
            double start = _t;
            for (int i = 0; i < Mathf.RoundToInt(maxSeconds / Dt); i++)
            {
                RunFor(site, Dt);
                during?.Invoke(_t - start);
                if (View(site, victim).Health < h0)
                {
                    return _t - start;
                }
            }
            return -1;
        }

        private static void CheckStriderDeployAndBlindSpot()
        {
            CampaignState s = NewWorldState(1);
            MechEnemy row = Row("enemy_strider");
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 0f));
            int st = UnitOf(site, "b_strider");
            int p = Player(site, new Vector2(10f, 0f), Gun(0.01f), 9999f);
            double first = FirstHitAfter(site, p, 8f);
            bool deployed = Fu(site, st).Deploy == 2;
            Expect(first >= row.DeploySeconds + row.WeaponAim - 0.05 && deployed && FoundryCombatFeedback.DeployCues > 0,
                $"B5 架炮驻锄（FGR-FAC-008，FGT-FAC-009 实测）：机器进射程后先放下驻锄 {row.DeploySeconds} 秒、再亮瞄准线 {row.WeaponAim} 秒才开火——第一次掉血在 {first:0.00} 秒（已架设 {deployed}）");

            // 负向（FG09 第 10 节）：架设到一半被击退 → 回到未架设、重新计时；冻结中不架设。
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 0f));
            st = UnitOf(site, "b_strider");
            p = Player(site, new Vector2(10f, 0f), Gun(0.01f), 9999f);
            bool reset = false;
            bool knocked = false;
            double knockedFirst = FirstHitAfter(site, p, 10f, el =>
            {
                if (!knocked && el >= 0.5)
                {
                    knocked = true;
                    site.Kernel.TryGetUnit(st, out CombatUnitView sv);
                    site.Kernel.SetPosition(st, sv.Position + new double2(0, 0.8), resetPrev: false); // 被击退 0.8 米
                }
                else if (knocked && !reset && el < 0.6)
                {
                    reset = Fu(site, st).Deploy == 0 || Fu(site, st).DeployT >= row.DeploySeconds - 0.1f;
                }
            });
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 0f));
            st = UnitOf(site, "b_strider");
            p = Player(site, new Vector2(10f, 0f), Gun(0.01f), 9999f);
            site.Kernel.ApplyStatus(st, 1u, 3f, 0f, 0.95f, 0f, 0);
            double frozenFirst = FirstHitAfter(site, p, 10f);
            Expect(knocked && reset && knockedFirst >= first + 0.4 && frozenFirst >= 3f + row.DeploySeconds + row.WeaponAim - 0.1,
                $"B5b 负向：架设到 0.5 秒时被击退 → 回到未架设、重新计时（{reset}），第一次开火推迟到 {knockedFirst:0.00} 秒（> {first:0.00}）；冻结 3 秒（减速 95%）期间不架设，第一次开火 {frozenFirst:0.00} 秒");

            // 高抛曲射的近距盲区：贴身的机器打不到（应对“贴身进入盲区”）。
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 0f));
            int near = Player(site, new Vector2(2.2f, 0f), Gun(0.01f), 9999f);
            double blind = FirstHitAfter(site, near, 8f);
            site.Kernel.SetPosition(near, new double2(9f, 0f));
            double outside = FirstHitAfter(site, near, 8f);
            Expect(blind < 0 && outside > 0,
                $"B5c 高抛曲射近距盲区 {row.MinRange} 米：贴在 2.2 米处 8 秒没挨打（{blind < 0}）；退到 9 米立刻进入射界、{outside:0.00} 秒后挨打");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSiegeHeavy()
        {
            CampaignState s = NewWorldState(1);
            MechEnemy row = Row("enemy_siege_heavy");
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_siege_heavy", "b_heavy", new Vector2(0f, 0f));
            int hv = UnitOf(site, "b_heavy");
            CombatUnitView v0 = View(site, hv);
            float2 face = math.normalize(v0.Facing);
            Vector2 front = new Vector2(face.x, face.y) * 30f;
            int shooter = Player(site, front, Gun(100f), 99999f);
            float h0 = View(site, hv).Health;
            site.Kernel.FireAt(shooter, hv, _t);
            site.ProcessEvents();
            float frontDmg = h0 - View(site, hv).Health;
            site.Kernel.SetPosition(shooter, new double2(-front.x, -front.y));
            float h1 = View(site, hv).Health;
            site.Kernel.FireAt(shooter, hv, _t);
            site.ProcessEvents();
            float rearDmg = h1 - View(site, hv).Health;
            bool elite = View(site, hv).Flags.HasFlag(CombatUnitFlags.Elite);
            Expect(Mathf.Abs(frontDmg - 100f * (1f - row.FrontalDamageReduction)) < 0.1f && Mathf.Abs(rearDmg - 100f * (1f + row.RearBonus)) < 0.1f && elite,
                $"B6 攻城重甲（精英，FGR-FAC-008“正面推进”）：正面几乎免伤（100 → {frontDmg:0.#}），背部散热口 +{row.RearBonus:P0}（100 → {rearDmg:0.#}）；带精英标记（{elite}）");

            // 冲锋：目标在正前方 → 蓄力（预警 ≥ 0.8 秒）→ 冲锋撞击（武器伤害）+ 击退；目标在背后时要慢慢转过来（转向受限）。
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_siege_heavy", "b_heavy", new Vector2(0f, 0f));
            hv = UnitOf(site, "b_heavy");
            face = math.normalize(View(site, hv).Facing);
            int victim = Player(site, new Vector2(face.x, face.y) * 11f, Gun(0.01f), 9999f);
            double windup = -1;
            double hit = FirstHitAfter(site, victim, 6f, el =>
            {
                if (windup < 0 && Fu(site, hv).Charge == 1)
                {
                    windup = el;
                }
            });
            CombatUnitView vv = View(site, victim);
            double pushed = math.dot((float2)(vv.Position - new double2(face.x * 11f, face.y * 11f)), face);
            Expect(windup >= 0 && hit - windup >= 0.8 && site.Kernel.Counters.Charges >= 1 && pushed >= 1.0 && FoundryCombatFeedback.ChargeCues > 0,
                $"B6b 冲锋：蓄力在 {windup:0.00} 秒开始，{hit - windup:0.00} 秒后才撞到（≥ 0.8，有声音 + 字幕）；被撞的机器沿冲锋方向被击退 {pushed:0.0} 米");

            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_siege_heavy", "b_heavy", new Vector2(0f, 0f));
            hv = UnitOf(site, "b_heavy");
            face = math.normalize(View(site, hv).Facing);
            Player(site, new Vector2(-face.x, -face.y) * 11f, Gun(0.01f), 9999f);
            double behindWindup = -1;
            for (int i = 0; i < 60 * 8 && behindWindup < 0; i++)
            {
                RunFor(site, Dt);
                if (Fu(site, hv).Charge == 1)
                {
                    behindWindup = _t;
                }
            }
            double minTurn = (180.0 - 15.0) / row.TurnRate;
            Expect(behindWindup >= minTurn - 0.1,
                $"B6c 转向半径大（{row.TurnRate}°/秒）：目标在正后方时要 {behindWindup:0.00} 秒才转过来开始蓄力（≥ {minTurn:0.0} 秒）——绕侧后有足够的窗口");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSlagKernel()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            CombatWeapon hot = Gun(1f);
            hot.Dissipation = 10f;
            hot.OverheatAt = 1000f;
            int inside = Player(site, new Vector2(0f, 0f), hot);
            int outside = Player(site, new Vector2(40f, 0f), hot);
            site.SetSlagZones(new List<float3> { new float3(0f, 0f, 8f) }, 0.7f);
            site.Kernel.SetWeaponState(inside, 50f, false, 0, 0);
            site.Kernel.SetWeaponState(outside, 50f, false, 0, 0);
            RunFor(site, 2f);
            float hi = View(site, inside).Heat;
            float ho = View(site, outside).Heat;
            Expect(Mathf.Abs(hi - (50f - 20f * 0.7f)) < 0.2f && Mathf.Abs(ho - 30f) < 0.2f && site.Kernel.SlagZoneCount == 1,
                $"B7 熔渣热区“区内散热 -30%”（内核逐单位结算）：2 秒后区内机器热量 {hi:0.0}（散热 7/秒）、区外 {ho:0.0}（10/秒）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckKernelSnapshot()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_minelayer", "b_layer", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_aa_gun", "b_aa", new Vector2(-20f, 0f));
            BenchEnemy(site, s, "enemy_siege_heavy", "b_heavy", new Vector2(20f, 0f));
            BenchEnemy(site, s, "enemy_strider", "b_strider", new Vector2(0f, 20f));
            BenchEnemy(site, s, "enemy_decoy_relay", "b_dummy", new Vector2(-14f, 4f));
            int dr = Player(site, new Vector2(-12f, 2f), Drones(), 9999f);
            Player(site, new Vector2(29f, 0f), Gun(0.01f), 9999f);
            Player(site, new Vector2(8f, 20f), Gun(0.01f), 9999f);
            RunFor(site, 3f);
            site.Kernel.FireAt(dr, UnitOf(site, "b_dummy"), _t);
            RunFor(site, 1.75f);
            CombatKernel k = site.Kernel;
            byte[] snap = k.Serialize();
            ulong h0 = k.StateHash();
            using var b = new CombatKernel(k.Config, 64);
            CombatLoadResult lr = b.Load(snap);
            ulong h1 = b.StateHash();
            double t0 = _t;
            for (int i = 0; i < 240; i++)
            {
                t0 += Dt;
                k.Step(Dt, t0);
                b.Step(Dt, t0);
            }
            bool cont = k.StateHash() == b.StateHash();
            k.TryGetFoundry(UnitOf(site, "b_strider"), out CombatFoundryUnit fa);
            b.TryGetFoundry(UnitOf(site, "b_strider"), out CombatFoundryUnit fb);
            Expect(lr == CombatLoadResult.Ok && h0 == h1 && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 14
                   && k.CountMines() == b.CountMines() && fa.Deploy == fb.Deploy,
                $"B8 内核快照（格式 {CombatConst.FormatVersion}）：齐射 / 架设 / 冲锋 / 子母雷（{k.CountMines()} 颗）的状态与参数随快照往返，状态哈希一致（{h0 == h1}），读回后接着跑 4 秒逐位相同（{cont}）（{lr}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── C 铸造主核心 ───────────────────────────────

        private const string BpGun = "bp_fg9fac02_gun";
        private const string BpDrone = "bp_fg9fac02_drone";
        private const string BpRam = "bp_fg9fac02_ram";

        private static void MakeBlueprints(CampaignState s)
        {
            void Add(string id, string primary)
            {
                if (s.BlueprintRecords.Any(b => b.BlueprintId == id))
                {
                    return;
                }
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = id,
                    DisplayName = id,
                    ActiveVersion = 1,
                    Versions = new[]
                    {
                        new BlueprintVersionRecord { Version = 1, ChassisId = HomeValleyLayout.Erc003ChassisId, PrimaryId = primary, OrderedFirmwareIds = Array.Empty<string>() },
                    },
                }).ToArray();
            }
            Add(BpGun, ComponentCatalog.CompGunId);
            Add(BpDrone, "comp_dronebay");
            Add(BpRam, "comp_ram");
            if (s.BlueprintRecords.All(b => b.BlueprintId != BpCannonUplink))
            {
                // 核心区大门三灯之一：有一台“接入时能打出熔穿过载”的机器（重炮 + 接通的接入口），与 FgCombatKernelSelfCheck 同一夹具。
                BlueprintCircuitBoard up = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
                up.TrySetUplink(2);
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = BpCannonUplink, DisplayName = BpCannonUplink, ActiveVersion = 1, Versions = new[] { up.ToVersion(1, 0f) },
                }).ToArray();
            }
        }

        private const string BpCannonUplink = "bp_fg9fac02_cannon_uplink";

        private static int SpawnIn(CampaignState s, string region, string bp, Vector2 at, float hp = 4000f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
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

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static float Hp(int logicId) => MachineRegistry.TryGetRecord(logicId, out MachineRecord r) ? r.Health : -1f;

        private static RegionEnemyRecord Enemy(CampaignState s, string id) => s.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == id);

        /// <summary>安全停靠点：核心区里（越过大门线），离主核心 / 精英 / 守卫都远。</summary>
        private static readonly Vector2 Park = new Vector2(-20f, 24.5f);

        /// <summary>铸造前哨入场 → 核心区大门解锁 → 机器越过大门线 → 首领战开始（护盾阶段）。</summary>
        private static FoundryOutpostController EnterBoss(CampaignState s, out int machine)
        {
            MakeBlueprints(s);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FoundryOutpostRegion.Find(s);
            region.State = RegionState.Available;
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).Distinct().ToArray();
            CampaignEventLedger.TryGrant(s, BlueprintEditorService.ReactionChargeEventId(MechanicalReactionCatalog.ReactionMeltOverloadId), "BlueprintReactionCharge", 0f);
            SpawnIn(s, HomeValleyLayout.RegionId, BpCannonUplink, new Vector2(3f, 3f));
            machine = SpawnIn(s, Fo, BpGun, new Vector2(0f, -18f));
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(new[] { machine }, resume: false);
            WorldSimulation.StepMany(2);
            Place(fo.Combat, machine, Park);
            WorldSimulation.StepMany(3);
            return fo;
        }

        /// <summary>护盾 → 阶段一 → 过渡 → 阶段二（Demo 原样）。</summary>
        private static void DriveToPhase2(CampaignState s)
        {
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode1Id, 99999f);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode2Id, 99999f);
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, core.Health - core.MaxHealth * 0.5f);
            WorldSimulation.StepMany(GameClock.StepHz * 6);
        }

        private static void CheckBossPhases()
        {
            CampaignState s = NewWorld(42);
            FoundryOutpostController fo = EnterBoss(s, out int m);
            CombatSite site = fo.Combat;
            RegionRecord region = FoundryOutpostRegion.Find(s);
            bool shielded = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Shielded;
            bool battery = EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBattery).All(enc => Enemy(s, enc.Id) != null && site.TryGetEnemyUnit(enc.Id, out _));
            List<string> dup = FoundryOutpostRegion.SelfCheckNoDuplicates(s);
            Expect(shielded && battery && dup.Count == 0,
                $"C1 从铸造前哨入场、越过核心区大门：首领战开始（护盾阶段 {shielded}）；核心区对空阵地（2 门对空高炮 + 装填机）就位并进战斗内核（{battery}）；区域实例零重复（{string.Join("；", dup)}）");
            bool hudBound = HudOpen();
            string hudTitle = Hud(s, "PhaseLabel");
            Expect(hudBound && _hud.RefreshNow(s, region) && (hudTitle ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.Shielded)),
                $"C1b 首领 HUD（真实 UXML {CoreBossHudUxml} + 运行时同一个绑定入口）：16 个节点全部绑上（{hudBound}），开战即可见，标题“{hudTitle}”");

            // Demo 阶段原样 + 阶段唯一写入口：阶段二不能直接跳到摧毁（非法转换被拒）。
            DriveToPhase2(s);
            bool phase2 = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Phase2;
            bool illegal = !FoundryOutpostCoreBoss.TryTransition(s, region, CoreBossState.Destroyed) && FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Phase2;
            string hudPhase2 = GameText.Format("boss.foundry.hud.next_phase", Mathf.RoundToInt(FoundryOutpostCoreBoss.Phase3HealthFraction * 100f).ToString(),
                Mathf.RoundToInt(FoundryOutpostCoreBoss.AntiAirSeconds).ToString());
            string hudStage2 = Hud(s, "StageLabel");
            Expect(phase2 && illegal && hudPhase2.Contains("30") && hudPhase2.Contains("15") && hudStage2 == hudPhase2,
                $"C2 FGT-FAC-003 唯一写入口：护盾 → 阶段一 → 过渡 → 阶段二（Demo 原样，{phase2}）；阶段二 → 已摧毁被拒（{illegal}）；阶段二 HUD 常显阶段三情报（HUD 阶段行“{hudStage2}”）");

            // 阶段二的伤害最多打到 30% → 阶段三·对空形态（剧情时刻：所有炮口转向天空）。
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            int notes0 = NotifyCount("foundry_boss");
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 99999f);
            double antiAirAt = GameClock.GameSeconds;
            bool antiAir = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.AntiAir;
            bool floor = Mathf.Abs(core.Health - core.MaxHealth * FoundryOutpostCoreBoss.Phase3HealthFraction) < 0.01f;
            WorldSimulation.StepMany(2);
            site.TryGetEnemyUnit(FoundryOutpostLayout.MainCoreId, out int coreUnit);
            bool airMode = Fu(site, coreUnit).AirMode == 1 && !View(site, coreUnit).Flags.HasFlag(CombatUnitFlags.WeaponEnabled);
            bool told = NotifyCount("foundry_boss") > notes0 && FeedbackCues.CountOf(FeedbackCueId.FoundryBossAntiAir) > 0;
            Expect(antiAir && floor && airMode && told && FoundryOutpostCoreBoss.DisplayPhaseText(region) == GameText.Get("boss.foundry.phase.antiair"),
                $"C3 阶段二耐久停在门槛 {core.Health:0}（{floor}）→ 阶段三·对空形态（{antiAir}）：内核对空模式、对地武器关闭（{airMode}）；剧情时刻通知 + 声音 + 字幕（{told}）；阶段名“{FoundryOutpostCoreBoss.DisplayPhaseText(region)}”");

            // 对空形态：不打地面（机器站进主核心射程也不挨打）；打无人机；核心暴露 × 1.5。
            Place(site, m, FoundryOutpostLayout.MainCore.Position + new Vector2(0f, -8f));
            float g0 = Hp(m);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            float g1 = Hp(m);
            float c0 = core.Health;
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 10f);
            bool mult = Mathf.Abs((c0 - core.Health) - 10f * FoundryOutpostCoreBoss.ExposedDamageMult) < 1e-3f;
            Place(site, m, Park);
            Expect(Mathf.Approximately(g0, g1) && mult,
                $"C4 对空形态：机器站在主核心 8 米处 3 秒不挨打（{g0:0} → {g1:0}）；核心暴露，受伤 × {FoundryOutpostCoreBoss.ExposedDamageMult}（10 → {c0 - core.Health:0.##}）");

            // 修复轮（审查 P1）：对空形态在核心背后拦截无人机，主核心朝向不变（固定结构，侧后加成按出生朝向判定）。
            Vector2 facing0 = FoundryOutpostLayout.MainCoreFacing.normalized;
            Vector2 behind = FoundryOutpostLayout.MainCore.Position - facing0 * 7f;
            Vector2 front = FoundryOutpostLayout.MainCore.Position + facing0 * 7f;
            long intercepts0 = site.Kernel.Counters.AirIntercepts;
            int launcher = Player(site, behind + new Vector2(1.5f, 0f), Drones());
            CombatFireResult launch = site.Kernel.FireAt(launcher, coreUnit, site.Kernel.Time);
            WorldSimulation.StepMany(1);
            int dronesUp = site.Kernel.DroneCount;
            int maxSalvo = 0;
            float minSalvoT = 999f;
            for (int k = 0; k < GameClock.StepHz * 3; k++)
            {
                WorldSimulation.StepMany(1);
                CombatFoundryUnit cf = Fu(site, coreUnit);
                maxSalvo = Math.Max(maxSalvo, cf.Salvo);
                minSalvoT = Math.Min(minSalvoT, cf.SalvoT);
            }
            site.SetUnitHealth(launcher, 0f, 400f, false);
            bool interceptedBehind = site.Kernel.Counters.AirIntercepts > intercepts0;
            float2 facingAfter = View(site, coreUnit).Facing;
            bool facingKept = math.lengthsq(facingAfter) > 1e-6f && math.dot(math.normalize(facingAfter), new float2(facing0.x, facing0.y)) > 0.999f;
            Expect(interceptedBehind && facingKept,
                $"C4b 对空形态在核心背后拦截无人机（发射 {launch}、升空 {dronesUp} 架、3 秒后 {site.Kernel.DroneCount} 架，核心齐射状态最高 {maxSalvo}、最短计时 {minSalvoT:0.00}；拦截 {intercepts0} → {site.Kernel.Counters.AirIntercepts}，{interceptedBehind}），主核心内核朝向不变（{facingAfter} vs 出生朝向 {facing0}，{facingKept}）");

            // HUD：对空形态写出“受伤 × 1.5”与剩余秒数；机器退出核心区 → 场地边界倒计时（20 秒起倒数），回到核心区倒计时消失。
            string hudAir = Hud(s, "StageLabel");
            string hudAirTitle = Hud(s, "PhaseLabel");
            float leftAir = region.CoreAntiAirEndAtPlaySeconds - s.PlaySeconds;
            Place(site, m, new Vector2(0f, -10f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            string hudArena = Hud(s, "ArenaLabel");
            string expectArena = GameText.Format("boss.foundry.hud.arena_leave", Mathf.Max(0f, FoundryOutpostCoreBoss.ArenaLeaveSeconds - region.CoreArenaOutsideSeconds).ToString("F0"));
            Place(site, m, Park);
            WorldSimulation.StepMany(2);
            string hudArenaBack = Hud(s, "ArenaLabel");
            Expect((hudAirTitle ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.AntiAir)) && (hudAir ?? "").Contains(FoundryOutpostCoreBoss.ExposedDamageMult.ToString("0.##"))
                   && (hudAir ?? "").Contains(Mathf.Max(0f, leftAir).ToString("F0")) && !string.IsNullOrEmpty(hudArena) && hudArena == expectArena && (hudArena ?? "").Contains("18")
                   && string.IsNullOrEmpty(hudArenaBack),
                $"C4c 首领 HUD·对空形态：标题“{hudAirTitle}”、阶段行“{hudAir}”（剩 {leftAir:0} 秒）；离开核心区 2 秒 → 边界倒计时“{hudArena}”；回到核心区倒计时清空（“{hudArenaBack}”）");

            // 15 秒后恢复并狂暴：对地开火间隔减半、精英攻城重甲压上来（一次）。
            double enterAt = GameClock.GameSeconds;
            while (FoundryOutpostCoreBoss.GetState(region) == CoreBossState.AntiAir && GameClock.GameSeconds - enterAt < 30)
            {
                WorldSimulation.StepMany(1);
            }
            double lasted = GameClock.GameSeconds - antiAirAt; // 从进入对空形态那一刻算
            bool berserk = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Berserk;
            WorldSimulation.StepMany(2);
            bool heavy = EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBossWave).All(enc => site.TryGetEnemyUnit(enc.Id, out _));
            site.TryGetUnitWeapon(coreUnit, out CombatWeapon bw);
            bool faster = Mathf.Abs(bw.Cooldown - Row("enemy_core_boss").WeaponCooldown * FoundryOutpostCoreBoss.BerserkCooldownMult) < 1e-3f
                          && View(site, coreUnit).Flags.HasFlag(CombatUnitFlags.WeaponEnabled) && Fu(site, coreUnit).AirMode == 0;
            Expect(berserk && Math.Abs(lasted - FoundryOutpostCoreBoss.AntiAirSeconds) < 0.2 && heavy && faster,
                $"C5 对空形态持续 {lasted:0.0} 秒（{FoundryOutpostCoreBoss.AntiAirSeconds}）→ 恢复并狂暴（{berserk}）：精英攻城重甲进场（{heavy}），对地开火间隔 {bw.Cooldown:0.##} 秒（× {FoundryOutpostCoreBoss.BerserkCooldownMult}，{faster}）");

            // 修复轮（审查 P1）：狂暴沿用阶段二“侧后 +20%”——内核路径（自动交战 / 弹体 / 读法命中）与直控路径同一套方向判定。
            float kBack = site.Kernel.BackMultiplierOf(coreUnit, new double2(behind.x, behind.y));
            float kFront = site.Kernel.BackMultiplierOf(coreUnit, new double2(front.x, front.y));
            float dBack = FoundryOutpostCoreBoss.ComputeDamageMultiplier(s, core, behind);
            float dFront = FoundryOutpostCoreBoss.ComputeDamageMultiplier(s, core, front);
            Expect(berserk && Mathf.Abs(kBack - dBack) < 1e-4f && Mathf.Abs(kFront - dFront) < 1e-4f && kBack > 1.01f && Mathf.Approximately(kFront, 1f),
                $"C5b 狂暴的侧后加成：背后 内核 × {kBack:0.##} / 直控 × {dBack:0.##}，正面 内核 × {kFront:0.##} / 直控 × {dFront:0.##}（两条路径一致，背后才有加成）");
            string hudBerserk = Hud(s, "StageLabel");
            EnemyEncounter heavyEnc = EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBossWave).FirstOrDefault();
            RegionEnemyRecord heavyRec = heavyEnc != null ? Enemy(s, heavyEnc.Id) : null;
            string hudElite = Hud(s, "EliteLabel");
            bool eliteShown = HudEliteShown();
            Expect((hudBerserk ?? "").Contains(FoundryOutpostCoreBoss.BerserkCooldownMult.ToString("0.##")) && (Hud(s, "PhaseLabel") ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.Berserk))
                   && heavyRec != null && eliteShown && (hudElite ?? "").Contains(EnemyDefs.NameOf(heavyRec.EnemyTypeId)),
                $"C5c 首领 HUD·狂暴：阶段行“{hudBerserk}”；精英血条显示（{eliteShown}）“{hudElite}”");

            // 击败 → 奖励（只发一次）：核心固件“过载”“电容蓄力” + 三选一数据包；领地平定 → 主线“熄灭熔炉”→ 熔炉心。
            int packets0 = LootPickService.Count(s);
            int grants0 = FoundryOutpostCoreBoss.RewardGrants;
            region.CoreLastReachedState = CoreBossState.Phase2.ToString(); // 模拟之前失败过一次（“上次止步于阶段二”）
            string hudLastBefore = Hud(s, "LastPhaseLabel");
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 99999f);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool destroyed = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Destroyed;
            bool overload = s.PrimitiveChips.Any(p => p.CardDefId == "fw_overload" && p.Origin == PrimitiveInventory.OriginBoss);
            bool capacitor = s.PrimitiveChips.Any(p => p.CardDefId == "fw_capacitor" && p.Origin == PrimitiveInventory.OriginBoss);
            bool packet = LootPickService.Count(s) == packets0 + 1 && LootPickService.Packets(s).Last().Source == LootPickService.SourceBoss;
            bool pacified = TerritoryControlService.Find(s, "foundry")?.LeaderDefeated == true;
            KeyMaterial heart = QuestCatalog.KeyMaterialOf("furnace_heart");
            bool keyMat = heart != null && CampaignObjectiveTracker.HasKeyMaterial(s, heart);
            FoundryOutpostCoreBoss.ApplyDamage(s, core, 100f);
            string hudLastAfter = Hud(s, "LastPhaseLabel");
            Expect((hudLastBefore ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.Phase2)) && string.IsNullOrEmpty(region.CoreLastReachedState) && hudLastAfter == string.Empty
                   && (Hud(s, "PhaseLabel") ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.Destroyed)),
                $"C6b 首领 HUD·“上次止步于”：失败过时显示“{hudLastBefore}”，击败后记录清空、HUD 不再显示（“{hudLastAfter}”）");
            HudClose();
            bool once = FoundryOutpostCoreBoss.RewardGrants == grants0 + 1 && s.PrimitiveChips.Count(p => p.CardDefId == "fw_capacitor" && p.Origin == PrimitiveInventory.OriginBoss) == 1;
            Expect(destroyed && overload && capacitor && packet && pacified && keyMat && once,
                $"C6 击败铸造主核心（FGR-FAC-006 奖励）：核心固件“过载” {overload}、“电容蓄力” {capacitor}，三选一数据包 {packet}；铸造领地平定 {pacified} → 主线关键材料“熔炉心” {keyMat}；奖励只发一次 {once}");
        }

        // ── 首领 HUD（修复轮，审查 P1）：用真实 UXML 克隆出的节点树 + 运行时同一个绑定 / 刷新入口，逐阶段读标签内容 ──
        private const string CoreBossHudUxml = "Assets/GameRes/Raw/UI/CoreBoss/CoreBossHud.uxml";
        private static GameObject _hudGo;
        private static GameLogic.UI.CoreBoss.CoreBossHudUIToolkit _hud;
        private static UnityEngine.UIElements.VisualElement _hudRoot;

        private static bool HudOpen()
        {
            HudClose();
            var tree = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(CoreBossHudUxml);
            if (tree == null)
            {
                return false;
            }
            _hudRoot = tree.CloneTree();
            _hudGo = new GameObject("[SelfCheck_CoreBossHud]") { hideFlags = HideFlags.HideAndDontSave };
            _hud = _hudGo.AddComponent<GameLogic.UI.CoreBoss.CoreBossHudUIToolkit>();
            return _hud.Bind(_hudRoot);
        }

        private static void HudClose()
        {
            if (_hudGo != null)
            {
                UnityEngine.Object.DestroyImmediate(_hudGo);
            }
            _hudGo = null;
            _hud = null;
            _hudRoot = null;
        }

        /// <summary>立即刷新一次并返回节点的文字（节点不存在 = null）。</summary>
        private static string Hud(CampaignState s, string label)
        {
            if (_hud == null)
            {
                return null;
            }
            _hud.RefreshNow(s, FoundryOutpostRegion.Find(s));
            return UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(_hudRoot, label)?.text;
        }

        private static bool HudEliteShown() =>
            _hudRoot != null && UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.VisualElement>(_hudRoot, "EliteRow")?.style.display.value == UnityEngine.UIElements.DisplayStyle.Flex;

        private static string BossFingerprint(CampaignState s)
        {
            RegionRecord r = FoundryOutpostRegion.Find(s);
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            CombatSite site = CombatSites.Get(Fo);
            string air = "-";
            string heavy = "-";
            if (site != null && site.TryGetEnemyUnit(FoundryOutpostLayout.MainCoreId, out int u))
            {
                CombatFoundryUnit f = Fu(site, u);
                air = f.AirMode + ":" + f.Salvo + ":" + f.SalvoT.ToString("R");
            }
            EnemyEncounter wave = EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBossWave).FirstOrDefault();
            if (site != null && wave != null && site.TryGetEnemyUnit(wave.Id, out int hu) && site.Kernel.TryGetUnit(hu, out CombatUnitView hv))
            {
                heavy = hv.Position.x.ToString("R") + "," + hv.Position.y.ToString("R") + ":" + hv.Health.ToString("R");
            }
            return $"{r?.CoreState}|{core?.Health.ToString("R")}|{r?.CoreAntiAirEndAtPlaySeconds.ToString("R")}|{r?.CorePhaseStartHealth.ToString("R")}|{r?.CoreBerserkWaveSummoned}|{air}|{heavy}|{GameClock.Ticks}";
        }

        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            int[] there = r.State.MachineRecords.Where(m => m.RegionId == Fo && m.IsAlive).Select(m => m.LogicId).ToArray();
            WorldSimulation.LoadFoundryOutpost(there, resume: true);
            WorldView.Observe(Fo);
            return r.State;
        }

        private static void CheckBossSaveLoad()
        {
            CampaignState s = NewWorld(1);
            FoundryOutpostController fo = EnterBoss(s, out int m);
            DriveToPhase2(s);
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 99999f);
            WorldSimulation.StepMany(GameClock.StepHz * 4);
            WorldView.Observe(Fo);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string saved = BossFingerprint(s);
            WorldSimulation.StepMany(GameClock.StepHz * 14);
            string continued = BossFingerprint(s);
            CampaignState back = Reload(Slot);
            string loaded = BossFingerprint(back);
            WorldSimulation.StepMany(GameClock.StepHz * 14);
            string after = BossFingerprint(back);
            Expect(sv.Success && back != null && loaded == saved && after == continued && FoundryOutpostCoreBoss.GetState(FoundryOutpostRegion.Find(back)) == CoreBossState.Berserk,
                $"C7 FGT-FAC-003 首领战中存档（对空形态第 4 秒）：读档后阶段、主核心耐久、对空计时、本阶段起始耐久、对空模式与齐射状态完全一致（{loaded == saved}），" +
                $"接着跑 14 秒（跨过狂暴与精英进场）与不存档逐位相同（{after == continued}）：{after}");
        }

        private static void CheckBossWipeInAntiAir()
        {
            CampaignState s = NewWorld(42);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            FoundryOutpostController fo = EnterBoss(s, out int m);
            // 越线那一步已经写了“首领战前”自动存档（BossEngageEnter）：先留一份副本，后面读它重试。
            string preFight = Path.Combine(_dir, "pre-boss.json");
            bool autoSaved = File.Exists(slotFile);
            if (autoSaved)
            {
                File.Copy(slotFile, preFight, true);
            }
            RegionRecord region = FoundryOutpostRegion.Find(s);
            DriveToPhase2(s);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.MainCoreId, 99999f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool inWindow = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.AntiAir;
            // 卡片负向：阶段三窗口期内全灭。
            if (fo.Combat.TryGetMachineUnit(m, out int u))
            {
                fo.Combat.SetUnitHealth(u, 0f, 4000f, false);
            }
            MachineRegistry.MarkDeadByLogicId(m);
            WorldSimulation.StepMany(3);
            bool wiped = fo.IsWiped;
            int notes0 = NotifyCount("foundry_boss");
            var abandon = ExpeditionReturnService.TryConfirmAbandon();
            WorldSimulation.StepMany(2);
            bool reset = !FoundryOutpostCoreBoss.IsInitialized(region) && region.CoreLastReachedState == CoreBossState.AntiAir.ToString()
                         && EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBossWave).All(enc => Enemy(s, enc.Id) == null)
                         && Enemy(s, FoundryOutpostLayout.MainCoreId) is RegionEnemyRecord c && Mathf.Approximately(c.Health, c.MaxHealth);
            string retry = LatestDetail("foundry_boss");
            bool hudWipe = HudOpen();
            bool hudVisible = _hud != null && _hud.RefreshNow(s, region);
            string hudLast = Hud(s, "LastPhaseLabel");
            HudClose();
            Expect(hudWipe && hudVisible && (hudLast ?? "").Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.AntiAir)),
                $"C8b 首领 HUD：全灭复位后首领未开战也显示，写出“{hudLast}”（{hudVisible}）");
            bool retryNote = NotifyCount("foundry_boss") > notes0 && retry.Contains(FoundryOutpostCoreBoss.PhaseName(CoreBossState.AntiAir));
            bool preState = false;
            if (autoSaved)
            {
                File.Copy(preFight, slotFile, true);
                CampaignState again = Reload(Slot);
                preState = again != null && !FoundryOutpostCoreBoss.IsInitialized(FoundryOutpostRegion.Find(again));
            }
            Expect(inWindow && wiped && abandon.Success && reset && retryNote && autoSaved && preState,
                $"C8 负向“阶段三窗口期内全灭”（FGT-FAC-004 同一语义）：全灭被侦测（{wiped}），放弃远征（{abandon.Success}）→ 首领复位到开战前（满血、狂暴精英撤走），记下“上次止步于{FoundryOutpostCoreBoss.PhaseName(CoreBossState.AntiAir)}”（{reset}）；" +
                $"通知“{retry}”（{retryNote}）；读越线前的“首领战前”自动存档 → 首领未开战（{preState}）");
        }

        private static void CheckArenaBoundary()
        {
            CampaignState s = NewWorld(1);
            FoundryOutpostController fo = EnterBoss(s, out int m);
            CombatSite site = fo.Combat;
            RegionRecord region = FoundryOutpostRegion.Find(s);
            // 外围的维修机会跑去修首领部件（Demo 规则：修血量比例最低的友军）——先拆掉它，只看场地边界本身。
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.RepairBotSpawnId, 99999f);
            WorldSimulation.StepMany(2);
            RegionEnemyRecord n1 = Enemy(s, FoundryOutpostLayout.CoreNode1Id);
            FoundryOutpostRegion.TryDamageEnemy(s, n1.EnemyInstanceId, 40f);
            float hurt = n1.Health;
            Place(site, m, new Vector2(0f, -10f)); // 退回外围（离开核心区）
            WorldSimulation.StepMany(GameClock.StepHz * 19);
            float at19 = n1.Health;
            float out19 = region.CoreArenaOutsideSeconds;
            int resets19 = region.CoreArenaResets;
            bool notYet = Mathf.Approximately(at19, hurt) && out19 > 18f && resets19 == 0;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool restored = Mathf.Approximately(n1.Health, n1.MaxHealth) && region.CoreArenaResets == 1 && FeedbackCues.CountOf(FeedbackCueId.FoundryBossArenaReset) > 0;
            Expect(notYet && restored,
                $"C9 场地边界（FGR-FAC-006，初值 20 秒）：护盾阶段打伤供能节点（{hurt:0}）后机器全部退出核心区——19 秒时还没复位（{notYet}：耐久 {at19:0}、离场 {out19:0.0} 秒、复位 {resets19} 次），" +
                $"满 20 秒复位、节点耐久回满（{restored}），有通知 + 声音 + 字幕");

            // 阶段一：主核心掉血后离场 20 秒 → 回到本阶段开始时的耐久；回到核心区计时清零。
            Place(site, m, Park);
            WorldSimulation.StepMany(2);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode1Id, 99999f);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode2Id, 99999f);
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            float start = core.Health;
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 60f);
            Place(site, m, new Vector2(0f, -10f));
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Place(site, m, Park);
            WorldSimulation.StepMany(2);
            bool cleared = region.CoreArenaOutsideSeconds == 0f && core.Health < start;
            Place(site, m, new Vector2(0f, -10f));
            WorldSimulation.StepMany(GameClock.StepHz * 21);
            bool coreBack = Mathf.Approximately(core.Health, start) && region.CoreArenaResets == 2;
            string hud = GameText.Format("boss.foundry.hud.arena_leave", "5");
            Expect(cleared && coreBack && hud.Contains("5"),
                $"C9b 阶段一：离场 10 秒又回来计时清零（{cleared}）；再离场满 20 秒 → 主核心回到本阶段开始时的 {start:0}（{coreBack}）；HUD 倒计时“{hud}”");
        }

        private static string RunBoss(float speed, bool pauseHalf, bool observe)
        {
            CampaignState s = NewWorld(42);
            EnterBoss(s, out _);
            DriveToPhase2(s);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.MainCoreId, 99999f);
            WorldView.Observe(observe ? Fo : HomeValleyLayout.RegionId);
            long target = GameClock.Ticks + GameClock.StepHz * 20;
            GameClock.SetSpeed(speed);
            bool pausedOk = true;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 40000)
            {
                if (pauseHalf && frames == 200)
                {
                    GameClock.SetPaused(true);
                    string p0 = BossFingerprint(s);
                    for (int k = 0; k < 30; k++)
                    {
                        WorldSimulation.Frame(1f / 60f, target);
                    }
                    pausedOk = BossFingerprint(s) == p0;
                    GameClock.SetPaused(false);
                }
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return (pausedOk ? string.Empty : "PAUSE-MOVED ") + BossFingerprint(s);
        }

        private static void CheckBossSpeedMatrixAndObservation()
        {
            string r05 = RunBoss(0.5f, false, true);
            string r1 = RunBoss(1f, true, true);
            string r2 = RunBoss(2f, false, true);
            string r3 = RunBoss(3f, false, true);
            string unobserved = RunBoss(1f, false, false);
            Expect(r05 == r1 && r1 == r2 && r2 == r3 && !r1.Contains("PAUSE") && unobserved == r1 && r1.Contains(CoreBossState.Berserk.ToString()),
                $"C10 B09 / FGR-BASE-021：主核心对空形态 → 狂暴（20 游戏秒，含对空齐射与精英进场）在 0.5x / 1x（中途暂停 30 帧不走）/ 2x / 3x 下逐位相同，镜头不看铸造前哨时结果也一样（{unobserved == r1}）：{r1}");
        }

        // ─────────────────────────────── D 熔渣热区（世界） ───────────────────────────────

        private static void CheckSlagWorld()
        {
            CampaignState s = NewWorld(42);
            IReadOnlyList<PlannedHazardZone> zones = SlagHeatService.Zones(s);
            PlannedHazardZone z = zones.FirstOrDefault();
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool inZone = z != null && Mathf.Approximately(SlagHeatService.DissipationFactorAt(s, new Vector2(z.CenterX, z.CenterY)), 0.7f);
            bool outZone = Mathf.Approximately(SlagHeatService.DissipationFactorAt(s, Vector2.zero), 1f);
            bool kernel = home != null && home.Kernel.SlagZoneCount == zones.Count && zones.Count > 0;
            string hover = SlagHeatService.HoverLine();
            Expect(zones.Count > 0 && zones.All(h => h.TerritoryId == "foundry") && inZone && outZone && kernel && hover.Contains("30"),
                $"D1 熔渣热区（规划层危害区，按种子放置）：{zones.Count} 片、全部在铸造领地；区内散热倍率 0.7、区外 1；星球战斗内核已写入同样 {home?.Kernel.SlagZoneCount} 片；悬停“{hover}”");
        }

        // ─────────────────────────────── E 反制（FGT-FAC-005） ───────────────────────────────

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
                SpawnIn(s, HomeValleyLayout.RegionId, bp, new Vector2(i * 3f, 5f));
            }
        }

        private static void CheckCounter()
        {
            CampaignState s = NewWorld(1);
            MakeBlueprints(s);
            SetFleet(s, BpDrone, 3);
            string droneDecide = FoundryCounterService.Decide(s);
            string locked = FoundryCounterService.Locked(s);
            SetFleet(s, BpRam, 3);
            string meleeDecide = FoundryCounterService.Decide(s);
            string stillLocked = FoundryCounterService.Locked(s);
            string preview = FoundryCounterService.Preview(s);
            s.Clock.Day += 1;
            FoundryCounterService.WorldStep(s);
            string nextDay = FoundryCounterService.Locked(s);
            SetFleet(s, BpGun, 3);
            string plain = FoundryCounterService.Decide(s);
            string four = EnemyAdaptationService.ComputeAdaptation(s);
            // 同样的“格斗冲脸”构筑，静默在自己的技术树里选自己的对策（不会选出铸造的雷区）。
            string silent = SilentCounterService.Preview(s);
            Expect(droneDecide == EnemyDefs.ConditionCounterAntiAir && locked == droneDecide && meleeDecide == EnemyDefs.ConditionCounterMinefield
                   && stillLocked == locked && preview == meleeDecide && nextDay == EnemyDefs.ConditionCounterMinefield
                   && plain == (four == AdaptationCatalog.None ? FoundryCounterService.CounterNone : four) && silent != EnemyDefs.ConditionCounterMinefield,
                $"E1 FGT-FAC-005：无人机构筑 → 铸造选“对空加强”（{droneDecide}），当天锁定；改成格斗构筑当天不变（{stillLocked}），预测“雷区”（{preview}），日切后锁定为雷区（{nextDay}）；" +
                $"常规构筑回到 Demo 四态（{(plain == string.Empty ? "无" : plain)}）；静默按自己的技术树选（{(silent == string.Empty ? "无" : silent)}）");

            // 出发带入当天的锁定 → 铸造前哨按它追加编成（雷区 = 布雷车），开场通知写明原因；情报行写出今天的锁定与下一次预测。
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FoundryOutpostRegion.Find(s);
            string adapt = EnemyAdaptationService.LockAdaptation(s, region);
            string intel = FoundryCounterService.IntelLine(FoundryCounterService.Locked(s), FoundryCounterService.Preview(s));
            region.State = RegionState.Available;
            int notes0 = NotifyCount("foundry_boss");
            FoundryOutpostController fo = WorldSimulation.LoadFoundryOutpost(Array.Empty<int>(), resume: false);
            WorldSimulation.StepMany(2);
            bool seeded = EnemyDefs.Encounters(Fo, EnemyDefs.ConditionCounterMinefield).All(enc => Enemy(s, enc.Id) != null && fo.Combat.TryGetEnemyUnit(enc.Id, out _))
                          && EnemyDefs.Encounters(Fo, EnemyDefs.ConditionCounterAntiAir).All(enc => Enemy(s, enc.Id) == null);
            bool told = NotifyCount("foundry_boss") > notes0 && LatestDetail("foundry_boss").Contains(FoundryCounterService.CounterName(EnemyDefs.ConditionCounterMinefield));
            Expect(region.FoundryCounterId == EnemyDefs.ConditionCounterMinefield && adapt == AdaptationCatalog.None && seeded && told
                   && intel.Contains(FoundryCounterService.CounterName(EnemyDefs.ConditionCounterMinefield)),
                $"E2 出发时带入当天的锁定（{region.FoundryCounterId}，Demo 四态部分 {adapt}）；铸造前哨按它多布一台布雷车（{seeded}），开场通知写明原因（{told}）；情报“{intel}”");

            // 修复轮（审查 P1）：远征情报里铸造前哨的反制 = 出发时真正锁定的值（不按实时构筑重算四态），只写一行。
            fo.Exit(evacuateSuccess: false);
            WorldSimulation.StepMany(2);
            s.Clock.Day += 1;
            SetFleet(s, BpDrone, 3);
            s.SignalExposure = 80f;
            FoundryCounterService.WorldStep(s);
            string four2 = EnemyAdaptationService.ComputeAdaptation(s);
            IntelRecord ir = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            string sum1 = ir != null ? IntelService.Summary(s, ir, GameClock.Ticks) : string.Empty;
            SetFleet(s, BpGun, 3); // 同一天下午换构筑：情报与出发仍是当天的锁定
            IntelRecord ir2 = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            string sum2 = ir2 != null ? IntelService.Summary(s, ir2, GameClock.Ticks) : string.Empty;
            string panel = FoundryCounterService.PeekLocked(s);
            string adapt2 = EnemyAdaptationService.LockAdaptation(s, region);
            string foSite = CombatSites.SiteName(Fo);
            int Occur(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length - text.Replace(foSite, string.Empty).Length) / foSite.Length;
            int foIdx = ir != null ? Array.IndexOf(ir.Regions, Fo) : -1;
            bool sameValue = ir != null && ir2 != null && foIdx >= 0 && ir.FoundryCounter == EnemyDefs.ConditionCounterAntiAir && ir2.FoundryCounter == ir.FoundryCounter
                             && ir.Adaptations[foIdx] == adapt2 && ir2.Adaptations[foIdx] == adapt2 && region.FoundryCounterId == ir.FoundryCounter && panel == ir.FoundryCounter;
            bool oneLine = Occur(sum1) == 1 && Occur(sum2) == 1 && sum1.Contains(FoundryCounterService.CounterName(EnemyDefs.ConditionCounterAntiAir))
                           && (four2 == AdaptationCatalog.None || !sum1.Contains(AdaptationCatalog.Describe(four2).DisplayName));
            Expect(sameValue && oneLine,
                $"E3 FGR-FAC-004 情报与出发同一个值：无人机构筑当天锁定“对空加强”，情报记录 {ir?.FoundryCounter} / 四态部分 {(ir != null && foIdx >= 0 ? ir.Adaptations[foIdx] : "?")}，下午换构筑后仍是 {ir2?.FoundryCounter}，" +
                $"出发面板 {panel}、出发锁定 {region.FoundryCounterId}（{sameValue}）；铸造前哨只写一行、不出现实时四态“{(four2 == AdaptationCatalog.None ? "无" : four2)}”（{oneLine}）：{sum1}");
        }

        // ─────────────────────────────── F 攻城单位 ───────────────────────────────

        private static void CheckSiegeUnits()
        {
            CampaignState s = NewWorld(1);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            RaidCatalog.TryGetUnit("foundry.minelayer", out RaidUnitDef layer);
            RaidCatalog.TryGetUnit("foundry.loader", out RaidUnitDef loader);
            RaidCatalog.TryGetUnit("foundry.siege_heavy", out RaidUnitDef heavy);
            int lu = SiegeService.SpawnOne(home, 990101, new Vector2(10f, 10f), Vector2.zero, layer, false);
            int ou = SiegeService.SpawnOne(home, 990101, new Vector2(12f, 10f), Vector2.zero, loader, false);
            int hu = SiegeService.SpawnOne(home, 990101, new Vector2(14f, 10f), Vector2.zero, heavy, false);
            home.TryGetUnitSilent(lu, out _, out CombatBehaviorProfile lp);
            home.TryGetUnitSilent(ou, out _, out CombatBehaviorProfile op);
            bool elite = home.Kernel.TryGetUnit(hu, out CombatUnitView hv) && hv.Flags.HasFlag(CombatUnitFlags.Elite);
            Expect(lu > 0 && ou > 0 && hu > 0 && lp.MineMax > 0f && lp.MineDamage > 0f && op.ReloadRange > 0f && elite,
                $"F1 攻城中的铸造专属语法：突袭布雷车带布雷参数（上限 {lp.MineMax}、伤害 {lp.MineDamage}），装填机带装填光环（{op.ReloadRange} 米），攻城重甲本身是精英（{elite}）");
            home.Kernel.Despawn(lu);
            home.Kernel.Despawn(ou);
            home.Kernel.Despawn(hu);
        }

        // ─────────────────────────────── G 对空阵地（手工锚点 + 防空覆盖） ───────────────────────────────

        private static void CheckBatteryAnchorAndDropPods()
        {
            var outside = new List<string>();
            foreach (int seed in Seeds)
            {
                CampaignState w = NewWorldState(seed);
                WorldPlan plan = WorldGenService.PlanFor(w);
                PlannedAnchor a = plan?.Anchors.FirstOrDefault(x => x.Id == Fo);
                if (a == null)
                {
                    outside.Add(seed + ":无锚点");
                    continue;
                }
                foreach (FoundryOutpostLayout.Anchor g in FoundryOutpostLayout.BatteryPoints())
                {
                    TerritoryPlanner.ToWorld(a, g.Position.x, g.Position.y, out int gx, out int gy);
                    float d = Vector2.Distance(new Vector2(gx, gy), new Vector2(a.OriginX, a.OriginY));
                    if (d + g.ClearanceRadius > a.FootprintRadius)
                    {
                        outside.Add(seed + ":" + g.Id);
                    }
                }
            }
            Expect(outside.Count == 0,
                $"G1 FGR-GEN-024 手工锚点：核心区与对空阵地随铸造前哨的星球锚点按种子落位，3 个种子（{string.Join(",", Seeds)}）的对空炮位全部在锚点平整范围里（越界 {outside.Count}：{string.Join(",", outside)}）");

            CampaignState s = NewWorld(42);
            OutpostRecord battery = DropPodService.FoundryBattery(s);
            bool covers = battery != null && !battery.Destroyed && DropPodService.AntiAirAt(s, battery.CellX, battery.CellY)?.OutpostId == DropPodService.BatteryOutpostId;
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.EnsureEnemiesSeeded(s);
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(Fo, EnemyDefs.ConditionBattery))
            {
                RegionEnemyRecord rec = Enemy(s, enc.Id);
                if (rec != null && EnemyDefs.HasTag(enc.EnemyTypeId, EnemyDefs.TagAntiAir))
                {
                    rec.IsAlive = false;
                }
            }
            OutpostRecord gone = DropPodService.FoundryBattery(s);
            bool lifted = gone != null && gone.Destroyed && DropPodService.AntiAirAt(s, battery?.CellX ?? 0, battery?.CellY ?? 0)?.OutpostId != DropPodService.BatteryOutpostId;
            Expect(covers && lifted,
                $"G2 FGR-EXP-014“对空高炮会阻止投送舱在其覆盖范围内着陆”：对空阵地（{battery?.CellX},{battery?.CellY}）的覆盖拒绝落点（{covers}）；对空高炮全部被拆掉后这片覆盖消失（{lifted}）");
        }

        // ─────────────────────────────── H 性能 ───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            string[] types = { "enemy_aa_gun", "enemy_airburst", "enemy_minelayer", "enemy_loader", "enemy_siege_heavy" };
            for (int i = 0; i < 200; i++)
            {
                BenchEnemy(site, s, types[i % types.Length], "p" + i, new Vector2((i % 20) * 7f, (i / 20) * 7f));
            }
            for (int i = 0; i < 40; i++)
            {
                Player(site, new Vector2((i % 10) * 14f, 90f + (i / 10) * 4f), Gun(1f), 1e6f);
            }
            RunFor(site, 2f);
            var sw = Stopwatch.StartNew();
            int steps = 120;
            RunFor(site, steps * Dt);
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            int mines = site.MineCount;
            CombatSites.Close(Bench, s, dropRecord: true);

            CampaignState w = NewWorld(42);
            var sw2 = Stopwatch.StartNew();
            int n = 20000;
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += SlagHeatService.DissipationFactorAt(w, new Vector2(i % 3000, i / 7));
            }
            sw2.Stop();
            double us = sw2.Elapsed.TotalMilliseconds * 1000.0 / n;
            PerfGate.Expect(acc > 0f, $"H1 性能（Editor batchmode，Mono JIT，一次测量；真机 IL2CPP + HybridCLR 另测 FG15-SYS-02）：200 个铸造单位（对空 / 空爆 / 布雷 / 装填 / 攻城重甲各五分之一，" +
                                     $"场上 {mines} 颗雷）+ 40 台机器的内核一步 {perStep:F3} ms；熔渣倍率单点 {us:F3} 微秒",
                new[] { new PerfGate.Metric("铸造单位内核一步(ms)", perStep, 4.0, false), new PerfGate.Metric("熔渣倍率单点(微秒)", us, 2.0, false) }, Expect, Line);
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
            CampaignState s = CampaignState.CreateNew("fgfoundry-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
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
            SlagHeatService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgfoundry-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static string LatestDetail(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e.Type != null && e.Type.Id == typeId)?.Latest?.DetailText ?? string.Empty;

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgFoundryFactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
