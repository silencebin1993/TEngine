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
    /// FG13-FAC-01 白潮舰队单位（FG09 第 8 章；FGR-FAC-001 / 003 / 005 / 008；FG19 FGR-EMG-014 / 160）的自检——起真实系统、断言行为：
    /// A 数据：6 类单位入表（阵营 / 读法 / 职能 / 层级 / 中英文本）、专属预警 ≥ 0.8 秒；3 条中立预制固件过声明准入、纯数值固件被拒；突袭 / 攻城 / 靶子覆盖。
    /// B 编队：完整编队跟随长机目标；打散后速度 / 射速 × 表列倍率、发打散提示。C 灭活光束：预瞄线 ≥ 0.8 秒才放；扫过的格变灭活焦土、家园建造 / 传送带写明原因；来源被毁后 30 秒起逐格恢复。
    /// D 轨道打击：倒计时到点伤害圈内单位；倒计时内摧毁信标 = 取消（卡片负向）；热更层门面排的打击按比例伤建筑（FG13-END-03 复用接口）。
    /// E 屏蔽塔：范围内拒绝接入并写明原因（含米数）、范围外 / 塔被毁后恢复、已接入的机器不被踢出。F 登陆舱：下落中不可伤、不可选中，落地投放 4 台（进敌人记录）。
    /// G 护盾：先吸收、打穿发提示、停止受击后回充；带电荷时 × 电磁倍率且不回充。H 掉落：白潮池只有中立预制固件、可直接使用、不进“所有阵营”池。
    /// I 图鉴：第一次遭遇白潮单位解锁敌人与阵营条目并发钩子（DEBT-FG9UX01-02）；6 条机制图鉴与提示文本中英齐全。
    /// J 快照段 40～42 往返、读回后逐位相同、格式 24 旧档可读。K 暂停与 0.5x～3x：打击倒计时按游戏秒。P 性能（一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgFleetFactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgFleetFactionSelfCheck。
    /// </summary>
    public static class FgFleetFactionSelfCheck
    {
        private const int Slot = 7;
        private const string Bench = "fg13fac01-bench";
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("QA/自检/FG 白潮舰队单位")]
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
            Line("\n[白潮舰队单位] 编队 / 灭活光束与焦土 / 轨道打击 / 屏蔽塔 / 登陆舱 / 护盾、中立预制固件、图鉴、快照与倍速（FG13-FAC-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfleet-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                FleetCombatFeedback.ResetForTests();
                CombatSite.ResetFleetCountersForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();
                TouchedHooks.Clear();
                GuidanceHooks.Touched += OnHookTouched;

                Step(CheckData);
                Step(CheckFormation);
                Step(CheckBeamAndInert);
                Step(CheckOrbitalStrike);
                Step(CheckStrikeFacadeBuilding);
                Step(CheckShieldTower);
                Step(CheckDropPod);
                Step(CheckShield);
                Step(CheckLoot);
                Step(CheckPrefabBehavior);
                Step(CheckKernelSnapshot);
                Step(CheckSpeedMatrix);
                Step(CheckFeedbackAndCodex);
                Step(CheckPerf);
            }
            finally
            {
                GuidanceHooks.Touched -= OnHookTouched;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                SignalUplinkService.CoverageProvider = null;
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
            Line(_fail == 0 ? $"  白潮舰队单位：全部 {_pass} 项通过" : $"  白潮舰队单位：{_fail} 项失败 / {_pass} 项通过");
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
            ("enemy_purge_drone", EnemyDefs.BehaviorFormation, EnemyDefs.TierNormal, "swarm"),
            ("enemy_purifier_walker", EnemyDefs.BehaviorInertBeam, EnemyDefs.TierNormal, "saboteur"),
            ("enemy_orbital_beacon", EnemyDefs.BehaviorOrbitalMark, EnemyDefs.TierNormal, "marker"),
            ("enemy_shield_tower", EnemyDefs.BehaviorNone, EnemyDefs.TierNormal, "countersignal"),
            ("enemy_drop_pod", EnemyDefs.BehaviorAnchorDrop, EnemyDefs.TierNormal, "transport"),
            ("enemy_escort_drone", EnemyDefs.BehaviorFormation, EnemyDefs.TierElite, "escort"),
        };

        private static readonly string[] Prefab = { "fw_pf_quench", "fw_pf_shockguard", "fw_pf_repulse" };

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            var bad = new List<string>();
            foreach ((string id, string behavior, string tier, string role) in Units)
            {
                MechEnemy r = Row(id);
                if (r == null || r.Faction != EnemyDefs.FactionFleet || r.Behavior != behavior || r.Tier != tier || r.Role != role
                    || !EnemyBehaviorReadings.TryGet(r.Behavior, out IEnemyBehaviorReading _) || !EnemyFleetParams.Has(r))
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
            List<string> problems = EnemyDefs.AllProblems();
            Expect(bad.Count == 0 && missingText.Count == 0 && problems.Count == 0,
                $"A1 白潮 6 类单位入表（阵营 / 读法 / 层级 / 职能 / 带 fl* 白潮参数，异常 {bad.Count}：{string.Join(",", bad)}）；中英文本缺 {missingText.Count}：{string.Join(",", missingText.Take(6))}；全表行问题 {problems.Count}");
            var warn = new List<string>();
            foreach (string id in new[] { "enemy_purifier_walker", "enemy_orbital_beacon", "enemy_drop_pod" })
            {
                var p = new CombatBehaviorProfile();
                EnemyFleetParams.Apply(Row(id), ref p, 1f);
                float w = id == "enemy_purifier_walker" ? p.FlBeamWarn : id == "enemy_orbital_beacon" ? p.FlStrikeDelay : p.FlPodWarn;
                if (!(w >= 0.8f))
                {
                    warn.Add(id + " " + w);
                }
            }
            Expect(warn.Count == 0, $"A2 FGR-FAC-008 ①：灭活光束预瞄 / 轨道打击倒计时 / 登陆舱落点预警翻译成内核参数后都 ≥ 0.8 秒（不合格 {warn.Count}：{string.Join("；", warn)}）");

            EmgSource src = EmgCatalog.FromTables();
            Dictionary<string, EmgSource.Port> ports = src.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var rejected = new List<string>();
            foreach (string fw in Prefab)
            {
                bool rowOk = FirmwareKinds.TryGetRow(fw, out GameConfig.fg.FirmwareKind k) && k.Protocol == "neutral" && k.Source == "fleet" && k.Faction == "fleet";
                if (!rowOk || !EmgCatalog.Admit(EmgCatalog.DeclsOf(src, fw), ports, out string why) || !EmgCatalog.Current.OwnerIndex.ContainsKey(fw))
                {
                    rejected.Add(fw);
                }
            }
            bool pureRejected = !EmgCatalog.Admit(new[] { ("evt.hurt", EmgDirs.Read, EmgRole.Trigger), ("grm.mod.power", EmgDirs.Write, EmgRole.Modify) }, ports, out string pureWhy);
            Expect(rejected.Count == 0 && pureRejected,
                $"A3 FGR-EMG-014 / FGT-EMG-002：3 条中立预制固件（protocol neutral、来源 fleet）声明读 1 写 1 且过准入（被拒 {rejected.Count}：{string.Join(",", rejected)}）；对照“受击 → 只改威力”的纯数值固件被拒（{pureRejected}：{pureWhy}）");

            string[] raid = { "fleet.purge_drone", "fleet.purifier_walker", "fleet.escort_drone" };
            bool raidOk = raid.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef d) && d.Faction == EnemyDefs.FactionFleet && d.Points > 0
                                         && Row(d.EnemyTypeId)?.Faction == EnemyDefs.FactionFleet && SiegeCatalog.TryGetUnit(d.EnemyTypeId, out _));
            var uncovered = Units.Where(u => !ConfigSystem.Instance.Tables.TbRangeTarget.DataList.Any(t => t.UnlockEnemies.Split(',').Any(e => e.Trim() == u.id))).Select(u => u.id).ToList();
            Expect(raidOk && uncovered.Count == 0,
                $"A4 DEBT-FG6DEF04-04（舰队部分）：突袭编成 3 行机动单位都展开成白潮敌人、有攻城参数（{raidOk}；发动交 FG13-END-03）；每种白潮单位都解锁靶子（未覆盖 {uncovered.Count}：{string.Join(",", uncovered)}）");
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
        }

        private static double _t;

        private static CombatSite OpenBench(CampaignState s)
        {
            CombatSites.Close(Bench, s, dropRecord: true);
            _t = 0;
            return CombatSites.Open(Bench, s, false, new BenchRules(), out _);
        }

        private static string BenchEnemy(CombatSite site, CampaignState s, string typeId, string id, Vector2 at, string region = Bench)
        {
            MechEnemy row = Row(typeId);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = id, RegionId = region, EnemyTypeId = typeId, Position = at, Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != id).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(region), out CombatSpawn sp))
            {
                Fail("夹具：生成 " + typeId + " 失败");
                return id;
            }
            site.SpawnEnemy(rec, sp);
            return id;
        }

        private static int Player(CombatSite site, Vector2 at, CombatWeapon? wp, float hp = 400f, CombatUnitKind kind = CombatUnitKind.Machine)
        {
            int w = wp.HasValue ? site.WeaponIndex(wp.Value) : -1;
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = kind, Faction = CombatFaction.Player, Behavior = kind == CombatUnitKind.Machine ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (wp.HasValue ? CombatUnitFlags.WeaponEnabled : 0), Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
        }

        private static CombatWeapon Gun(float dmg = 10f, float cd = 0.05f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 40f, Damage = dmg, Cooldown = cd, TargetMode = CombatTargetMode.Nearest,
        };

        private static void RunFor(CombatSite site, float seconds)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 0; i < n; i++)
            {
                _t += Dt;
                site.Step(Dt, _t);
            }
        }

        private static void WorldFor(float seconds) => WorldSimulation.StepMany(Mathf.Max(1, Mathf.RoundToInt(seconds * GameClock.StepHz)));

        private static CombatUnitView View(CombatSite site, int unit) => site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static float Hp(CombatSite site, int unit) => View(site, unit).Health;

        private static int UnitOf(CombatSite site, string enemyId) => site.TryGetEnemyUnit(enemyId, out int u) ? u : 0;

        private static CombatFleet Fl(CombatSite site, int unit) => site.Kernel.TryGetFleet(unit, out CombatFleet r) ? r : default;

        private static float Hit(CombatSite site, int unit, float amount)
        {
            float before = Hp(site, unit);
            site.Kernel.Damage(unit, amount, 0);
            return before - Hp(site, unit);
        }

        private static Vector2 CorePos(CampaignState s) =>
            s.BuildingRecords?.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)?.Position ?? Vector2.zero;

        // ─────────────────────────────── B 编队 ───────────────────────────────

        private static void CheckFormation()
        {
            CampaignState s = NewWorldState(1);
            // B1 完整编队：三台同类无人机挨着；长机（单位号最小）追左边的机器，最右那台本来离右边的机器更近（左边那台在它感知外），完整编队里它跟长机走
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_purge_drone", "d1", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_purge_drone", "d2", new Vector2(1.5f, 0f));
            BenchEnemy(site, s, "enemy_purge_drone", "d3", new Vector2(3f, 0f));
            int d1 = UnitOf(site, "d1"), d2 = UnitOf(site, "d2"), d3 = UnitOf(site, "d3");
            int p1 = Player(site, new Vector2(-9f, 0f), null, 99999f);
            Player(site, new Vector2(11f, 0f), null, 99999f);
            var diagSteps = new List<string>();
            for (int st = 0; st < 4; st++)
            {
                RunFor(site, Dt);
                diagSteps.Add($"[{st}] d1→{View(site, d1).CommandTarget} d3→{View(site, d3).CommandTarget} 编队 {Fl(site, d3).Intact}/{Fl(site, d3).Leader} x3 {View(site, d3).Position.x:F2}");
            }
            RunFor(site, 0.3f - 4 * Dt);
            CombatFleet f3 = Fl(site, d3);
            bool followTarget = View(site, d3).CommandTarget == p1 && View(site, d2).CommandTarget == p1;
            double x30 = 3.0;
            string diagB = $"ids {d1}/{d2}/{d3}、d3 编队 {f3.Intact} 长机 {f3.Leader}、行为 {View(site, d3).Behavior}、d3 x {x30:F2}→{View(site, d3).Position.x:F2}；" + string.Join(" ", diagSteps);
            bool follow = followTarget && f3.Intact == 1 && f3.Leader == Math.Min(d1, Math.Min(d2, d3)) && View(site, d3).Position.x < x30;
            // 打散：干掉两台，剩下的长机发打散提示、效率下降
            int broken0 = FleetCombatFeedback.FormationBrokenCues;
            site.Kernel.Damage(d2, 9999f, 0);
            site.Kernel.Damage(d3, 9999f, 0);
            RunFor(site, 0.1f);
            bool brokenCue = Fl(site, d1).Intact == 0 && FleetCombatFeedback.FormationBrokenCues > broken0;
            CombatSites.Close(Bench, s, dropRecord: true);

            // B2 速度：单独一台（打散）在同样条件下 1.2 秒走的距离 ÷ 完整编队长机走的距离 ≈ 表列 flFormBroken
            double move1 = Advance(s, 3), moveSolo = Advance(s, 1);
            float broken = Row("enemy_purge_drone").FlFormBroken;
            double ratio = move1 > 0.01 ? moveSolo / move1 : 0;
            CombatSites.Close(Bench, s, dropRecord: true);

            // B3 射速：同一个靶子，单独一台 8 秒打出的伤害 ÷ 完整编队平均每台 ≈ flFormBroken
            float solo8 = DamageOver(s, 1), team8 = DamageOver(s, 3) / 3f;
            double fireRatio = team8 > 0f ? solo8 / team8 : 0;
            Expect(follow && brokenCue && Math.Abs(ratio - broken) < 0.12 && Math.Abs(fireRatio - broken) < 0.15,
                $"B1～B3 编队（FG09 第 8 章“完整编队一起转向、集火”）：完整编队里最右那台跟随长机的目标往左飞（{follow}）；打掉两台后剩下那台判定打散并发提示（{brokenCue}）；" +
                $"打散后速度比 {ratio:F2}、射速比 {fireRatio:F2}（表列 {broken}，0.6 秒内长机走 {move1:F2} 米、单机 {moveSolo:F2} 米；8 秒伤害单机 {solo8:F0} / 编队每台 {team8:F0}）；{diagB}");
        }

        /// <summary>n 台无人机（竖排）朝 12 米外的机器飞 0.6 秒，返回第一台走的距离。</summary>
        private static double Advance(CampaignState s, int drones)
        {
            CombatSite site = OpenBench(s);
            for (int i = 0; i < drones; i++)
            {
                BenchEnemy(site, s, "enemy_purge_drone", "a" + i, new Vector2(0f, i * 1.5f));
            }
            int lead = UnitOf(site, "a0");
            Player(site, new Vector2(-12f, 0f), null, 99999f);
            double x0 = View(site, lead).Position.x;
            RunFor(site, 0.6f);
            double moved = x0 - View(site, lead).Position.x;
            CombatSites.Close(Bench, s, dropRecord: true);
            return moved;
        }

        private static float DamageOver(CampaignState s, int drones)
        {
            CombatSite site = OpenBench(s);
            for (int i = 0; i < drones; i++)
            {
                BenchEnemy(site, s, "enemy_purge_drone", "f" + i, new Vector2(i * 1.2f, 0f));
            }
            int dummy = Player(site, new Vector2(1.2f, -5f), null, 99999f);
            RunFor(site, 1f);
            float h0 = Hp(site, dummy);
            RunFor(site, 8f);
            float dealt = h0 - Hp(site, dummy);
            CombatSites.Close(Bench, s, dropRecord: true);
            return dealt;
        }

        // ─────────────────────────────── C 灭活光束与焦土 ───────────────────────────────

        private static void CheckBeamAndInert()
        {
            CampaignState s = NewWorld(42);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            // 步行机放在非整数 y（小数 .7）：光束水平扫过 y = N.7，格心在整数（GridMath.FromWorld 四舍五入）→ 封禁的必须是 N+1 行，不是 floor 的 N 行
            Vector2 core0 = CorePos(s);
            Vector2 at = new Vector2(Mathf.Floor(core0.x) + 60f, Mathf.Floor(core0.y) + 30.7f);
            BenchEnemy(home, s, "enemy_purifier_walker", "w1", at, HomeValleyLayout.RegionId);
            int w = UnitOf(home, "w1");
            int target = Player(home, at + new Vector2(9f, 0f), null, 99999f, CombatUnitKind.Structure);
            int aim0 = FleetCombatFeedback.BeamAimCues;
            float h0 = Hp(home, target);
            WorldFor(0.5f);
            bool aiming = FleetCombatFeedback.BeamAimCues > aim0 && Hp(home, target) >= h0 - 0.01f && home.Kernel.FleetMarkCount == 0;
            WorldFor(2.5f);
            int marks = home.Kernel.FleetMarkCount;
            bool fired = Hp(home, target) < h0 && marks >= 6;
            CombatFleetMark m0 = marks > 0 ? home.Kernel.FleetMarkAt(0) : default;
            var cell = new GridCell(m0.Cell.x, m0.Cell.y);
            bool inert = CombatSite.InertHomeCell(cell.X, cell.Y);
            GridPlacementResult belt = HomeGridService.ValidateBeltCell(s, cell);
            GridPlacementResult bld = HomeGridService.ValidatePlacement(s, "generator_2", cell, 0, checkCost: false);
            bool blocked = belt.Reasons.Any(r => r.Code == GridBlockReason.InertGround) && bld.Reasons.Any(r => r.Code == GridBlockReason.InertGround)
                           && GameText.Get("grid.reason.inert_ground") != "grid.reason.inert_ground";
            var clear = new GridCell(cell.X, cell.Y + 6);
            bool otherFree = !CombatSite.InertHomeCell(clear.X, clear.Y) && !HomeGridService.ValidateBeltCell(s, clear).Reasons.Any(r => r.Code == GridBlockReason.InertGround);
            // 审查 P1：焦土格 = 光束采样点按格网唯一口径换算的格（GridCell.FromWorld），floor 那一行（差半格）没被封、能铺
            int beamRow = GridCell.FromWorld(at).Y;
            bool rowsOk = marks > 0 && Enumerable.Range(0, marks).All(i => home.Kernel.FleetMarkAt(i).Cell.y == beamRow);
            var below = new GridCell(cell.X, beamRow - 1);
            bool belowFree = !CombatSite.InertHomeCell(below.X, below.Y) && !HomeGridService.ValidateBeltCell(s, below).Reasons.Any(r => r.Code == GridBlockReason.InertGround);
            // 地图灰格画在格心（Cell.x, Cell.y），与被封禁的格同一处
            var cmodel = new GameLogic.UI.Kit.WorldMapModel();
            GameLogic.UI.Kit.WorldMapFilters.ResetAll();
            cmodel.Collect(s, new GameLogic.UI.Kit.WorldMapView { CenterX = cell.X, CenterY = cell.Y, HalfWidth = 30f, CanvasWidth = 400f, CanvasHeight = 400f }, 400);
            bool mapCell = cmodel.Circles.Any(c => c.Inert && Math.Abs(c.X - cell.X) < 1e-3 && Math.Abs(c.Y - cell.Y) < 1e-3);
            Expect(aiming && fired && inert && blocked && otherFree && rowsOk && belowFree && mapCell,
                $"C1 灭活光束：先拉预瞄线（0.5 秒时没伤害、没焦土，{aiming}），1.2 秒后放光束（伤到目标、扫出 {marks} 格焦土，{fired}）；焦土格建造 / 铺传送带都写明“灭活焦土”原因（{inert} / {blocked}）；旁边没扫到的格不受影响（{otherFree}）；" +
                $"光束扫过 y={at.y:F1} → 焦土全在格行 {beamRow}（GridCell.FromWorld，{rowsOk}；首格 ({cell.X},{cell.Y})、步行机 y={View(home, w).Position.y:F2}），floor 那一行 {beamRow - 1} 能铺（{belowFree}）；地图灰格画在格心（{mapCell}）");

            // C2 来源被毁 → 30 秒后开始逐格恢复（同一来源隔 2 秒一格）；来源活着时不恢复
            WorldFor(10f);
            bool held = home.Kernel.FleetMarkCount == marks;
            int rec0 = FleetCombatFeedback.RecoverCues;
            home.Kernel.Damage(w, 99999f, 0);
            WorldFor(29f);
            int at29 = home.Kernel.FleetMarkCount;
            WorldFor(2f);
            int at31 = home.Kernel.FleetMarkCount;
            MechEnemy row = Row("enemy_purifier_walker");
            WorldFor(row.FlBeamStagger * marks + 2f);
            int end = home.Kernel.FleetMarkCount;
            Expect(held && at29 == marks && at31 < marks && at31 > 0 && end == 0 && FleetCombatFeedback.RecoverCues > rec0 && !CombatSite.InertHomeCell(cell.X, cell.Y),
                $"C2 焦土恢复（按游戏秒）：步行机活着 10 秒焦土不变（{held}）；击毁后 29 秒还剩 {at29}/{marks} 格、31 秒剩 {at31} 格（开始逐格恢复并提示）、再过 {row.FlBeamStagger * marks + 2f:F0} 秒剩 {end} 格，原格又能建造");
        }

        // ─────────────────────────────── D 轨道打击 ───────────────────────────────

        private static void CheckOrbitalStrike()
        {
            CampaignState s = NewWorldState(2);
            MechEnemy row = Row("enemy_orbital_beacon");
            float dmg = row.FlStrikeDamage * DifficultyService.EnemyDamageMul(s);
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_orbital_beacon", "b1", Vector2.zero);
            int m = Player(site, new Vector2(8f, 0f), null, 400f);
            int far = Player(site, new Vector2(8f, 12f), null, 400f);
            int mark0 = FleetCombatFeedback.StrikeMarkCues, hit0 = FleetCombatFeedback.StrikeCues;
            RunFor(site, 0.2f);
            CombatFleetStrike x = site.Kernel.FleetStrikeCount > 0 ? site.Kernel.FleetStrikeAt(0) : default;
            bool marked = site.Kernel.FleetStrikeCount == 1 && math.distance(x.Center, new double2(8, 0)) < 0.5 && FleetCombatFeedback.StrikeMarkCues > mark0
                          && Math.Abs(x.At - site.Kernel.Time - (row.FlStrikeDelay - 0.2f)) < 0.1;
            string diagD = "记录 " + string.Join(",", Enumerable.Range(0, site.Kernel.FleetCount).Select(i2 => site.Kernel.TryGetFleetAt(i2, out CombatFleet q) ? q.Unit + ":" + q.State : "?"))
                           + " 打击 " + string.Join(",", Enumerable.Range(0, site.Kernel.FleetStrikeCount).Select(i2 => site.Kernel.FleetStrikeAt(i2)).Select(q => q.Id + "/" + q.Beacon + "@" + q.Center.x.ToString("0") + "," + q.Center.y.ToString("0")))
                           + $" 信标 {UnitOf(site, "b1")}、机器 {m}/{far}；" + $"排队 {site.Kernel.FleetStrikeCount} 次、圆心 ({x.Center.x:F1},{x.Center.y:F1})、剩 {x.At - site.Kernel.Time:F2} 秒";
            RunFor(site, row.FlStrikeDelay - 1.2f);
            bool waiting = Hp(site, m) >= 399.9f;
            RunFor(site, 1.5f);
            float lost = 400f - Hp(site, m);
            diagD += $"、落下后圈外机器 {Hp(site, far):F0}、提示 {FleetCombatFeedback.StrikeMarkCues - mark0}/{FleetCombatFeedback.StrikeCues - hit0}";
            bool struck = Math.Abs(lost - dmg) < 1f && Hp(site, far) >= 399.9f && FleetCombatFeedback.StrikeCues > hit0;
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(marked && waiting && struck,
                $"D1 轨道打击：信标挑最近的机器当圆心并标记（{marked}，倒计时 {row.FlStrikeDelay} 秒）；倒计时内不掉血（{waiting}）；到点圈内机器掉 {lost:F0}（表列 × 难度 = {dmg:F0}），圈外的不掉（{struck}）；{diagD}");

            // D2 卡片负向：倒计时内摧毁信标 → 取消，不落下
            site = OpenBench(s);
            BenchEnemy(site, s, "enemy_orbital_beacon", "b2", Vector2.zero);
            int b2 = UnitOf(site, "b2");
            int m2 = Player(site, new Vector2(8f, 0f), null, 400f);
            RunFor(site, 5f);
            int cancel0 = FleetCombatFeedback.StrikeCancelCues, hits1 = FleetCombatFeedback.StrikeCues;
            site.Kernel.Damage(b2, 99999f, 0);
            RunFor(site, row.FlStrikeDelay);
            string diagD2 = $"排队 {site.Kernel.FleetStrikeCount}、取消提示 {FleetCombatFeedback.StrikeCancelCues - cancel0}、落下 {FleetCombatFeedback.StrikeCues - hits1}、机器 {Hp(site, m2):F0}";
            bool cancelled = site.Kernel.FleetStrikeCount == 0 && FleetCombatFeedback.StrikeCancelCues > cancel0 && FleetCombatFeedback.StrikeCues == hits1 && Hp(site, m2) >= 399.9f;
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(cancelled, $"D2 负向：倒计时第 5 秒摧毁信标 → 打击取消、发取消提示、圈内机器不掉血（{cancelled}；{diagD2}）");
        }

        /// <summary>D3：热更层门面（FG13-END-03 复用）排的打击按游戏秒落下、按比例伤家园建筑；取消接口生效。</summary>
        private static void CheckStrikeFacadeBuilding()
        {
            CampaignState s = NewWorld(43);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            BuildingRecord b = s.BuildingRecords.FirstOrDefault(x => x != null && x.BuildingTypeId != HomeValleyLayout.BuildingTypeCore && x.Health > 0f);
            if (b == null)
            {
                Fail("D3 准备：家园里没有可受损的建筑");
                return;
            }
            StandingRuleService.BoundsOf(b, out GridCell min, out GridCell max);
            var c = new Vector2((min.X + max.X + 1) * 0.5f, (min.Y + max.Y + 1) * 0.5f);
            float hp0 = b.Health;
            int hits0 = CombatSite.StrikeBuildingHits;
            int id = home.ScheduleOrbitalStrike(c, 3f, 0f, 1f, 0.5f);
            int id2 = home.ScheduleOrbitalStrike(c + new Vector2(30f, 0f), 3f, 0f, 1f, 0.5f);
            bool cancel = home.CancelOrbitalStrike(id2) && !home.CancelOrbitalStrike(id2);
            WorldFor(1.5f);
            BuildingRecord after = HomeGridService.FindBuilding(s, b.BuildingId);
            float hp1 = after?.Health ?? 0f;
            Expect(id > 0 && cancel && CombatSite.StrikeBuildingHits > hits0 && hp1 < hp0,
                $"D3 可复用打击接口（CombatSite.ScheduleOrbitalStrike / CancelOrbitalStrike，FG13-END-03 的提前标落点直接调）：1 秒后落下伤建筑 {b.BuildingTypeId}（耐久 {hp0:F0} → {hp1:F0}）；取消的那次不落、重复取消返回 false（{cancel}）");

            // D4 审查 P1 负向：非家园地点（靶场，地点局部坐标）在同一坐标落下的打击不伤家园建筑，地图也不画它的打击圈
            CombatSite bench = OpenBench(s);
            int hits1 = CombatSite.StrikeBuildingHits;
            float hp2 = HomeGridService.FindBuilding(s, b.BuildingId)?.Health ?? 0f;
            int id3 = bench.ScheduleOrbitalStrike(c, 3f, 0f, 1f, 0.5f);
            var dmodel = new GameLogic.UI.Kit.WorldMapModel();
            GameLogic.UI.Kit.WorldMapFilters.ResetAll();
            dmodel.Collect(s, new GameLogic.UI.Kit.WorldMapView { CenterX = c.x, CenterY = c.y, HalfWidth = 30f, CanvasWidth = 400f, CanvasHeight = 400f }, 400);
            bool benchUnmapped = !dmodel.Circles.Any(x => x.Impact && Math.Abs(x.X - c.x) < 0.5 && Math.Abs(x.Y - c.y) < 0.5 && Math.Abs(x.Radius - 3f) < 0.01f);
            RunFor(bench, 1.5f);
            float hp3 = HomeGridService.FindBuilding(s, b.BuildingId)?.Health ?? 0f;
            bool benchLanded = bench.Kernel.FleetStrikeCount == 0;
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(id3 > 0 && benchLanded && CombatSite.StrikeBuildingHits == hits1 && Math.Abs(hp3 - hp2) < 0.01f && benchUnmapped,
                $"D4 负向：靶场地点在 ({c.x:F1},{c.y:F1}) 落下打击（{benchLanded}）后家园建筑耐久 {hp2:F0} → {hp3:F0}、建筑受击计数 {hits1} → {CombatSite.StrikeBuildingHits}；地图不画非家园地点的打击圈（{benchUnmapped}）");
        }

        // ─────────────────────────────── E 屏蔽塔 ───────────────────────────────

        private static void CheckShieldTower()
        {
            CampaignState s = NewWorld(44);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            SignalUplinkService.CoverageProvider = _ => true;
            List<MachineRecord> ms = MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId).ToList();
            if (ms.Count < 2 || !home.TryGetMachinePosition(ms[0].LogicId, out Vector2 pa))
            {
                Fail($"E 准备：家园机器不足（{ms.Count}）");
                return;
            }
            int a = ms[0].LogicId, b = ms[1].LogicId;
            UplinkFailure before = SignalUplinkService.Validate(s, a, out _);
            // 已接入的机器不被踢出：先接入 b，再在 b 身边立塔
            UplinkRequestResult req = SignalUplinkService.Request(b, UplinkSource.MachineList);
            for (int n = 0; n < 60 && SignalUplinkService.IsPending; n++)
            {
                Frames(1);
            }
            bool inB = req.Accepted && SignalPresence.CurrentMachineLogicId == b;
            home.TryGetMachinePosition(b, out Vector2 pb);
            float range = Row("enemy_shield_tower").FlBlockRange;
            BenchEnemy(home, s, "enemy_shield_tower", "t1", pa + new Vector2(5f, 0f), HomeValleyLayout.RegionId);
            BenchEnemy(home, s, "enemy_shield_tower", "tb", pb + new Vector2(3f, 0f), HomeValleyLayout.RegionId); // 审查 P2：b 自己也在屏蔽范围里，“不踢出”才有意义
            Frames(30);
            UplinkFailure f = SignalUplinkService.Validate(s, a, out string text);
            bool bShielded = home.AnyUplinkBlockAt(pb, out _);
            bool stillB = bShielded && SignalPresence.CurrentMachineLogicId == b;
            bool shown = text != null && text.Contains(range.ToString("0"));
            // 卡片“屏蔽塔范围的显示”：战略地图 / 小地图共用模型里有这座塔的屏蔽圈（半径 = 表列）
            var model = new GameLogic.UI.Kit.WorldMapModel();
            GameLogic.UI.Kit.WorldMapFilters.ResetAll();
            Vector2 tp = pa + new Vector2(5f, 0f);
            model.Collect(s, new GameLogic.UI.Kit.WorldMapView { CenterX = tp.x, CenterY = tp.y, HalfWidth = 60f, CanvasWidth = 400f, CanvasHeight = 400f }, 400);
            bool mapped = model.Circles.Any(c => c.UplinkBlock && Math.Abs(c.Radius - range) < 0.01f && Math.Abs(c.X - tp.x) < 1.0 && Math.Abs(c.Y - tp.y) < 1.0 && !string.IsNullOrEmpty(c.Label));
            // 塔被毁 → 恢复接入
            home.Kernel.Damage(UnitOf(home, "t1"), 99999f, 0);
            home.Kernel.Damage(UnitOf(home, "tb"), 99999f, 0);
            Frames(5);
            UplinkFailure afterKill = SignalUplinkService.Validate(s, a, out _);
            // 范围外：塔立在 a 的 range + 6 米外
            BenchEnemy(home, s, "enemy_shield_tower", "t2", pa + new Vector2(range + 6f, 0f), HomeValleyLayout.RegionId);
            Frames(5);
            UplinkFailure outside = SignalUplinkService.Validate(s, a, out _);
            Expect(before == UplinkFailure.None && f == UplinkFailure.Shielded && shown && mapped && afterKill != UplinkFailure.Shielded && outside != UplinkFailure.Shielded && inB && stillB,
                $"E1 屏蔽塔（FG09 第 8 章，按表列 {range} 米、不认 ID）：立塔前可接入（{before}）；塔 5 米内拒绝接入（{f}）并写明原因“{text}”；地图上画出屏蔽圈（{mapped}）；塔被毁后恢复（{afterKill}）；塔在 {range + 6f} 米外不拦（{outside}）；" +
                $"已接入的机器不被踢出（先接入 #{b}：{inB}，b 身边 3 米立塔、b 在屏蔽范围内 {bShielded}，信号仍在 b：{stillB}）");
            SignalUplinkService.CoverageProvider = null;
        }

        // ─────────────────────────────── F 登陆舱 ───────────────────────────────

        private static void CheckDropPod()
        {
            CampaignState s = NewWorldState(3);
            CombatSite site = OpenBench(s);
            int warn0 = FleetCombatFeedback.PodWarnCues, land0 = FleetCombatFeedback.PodLandCues, cargo0 = CombatSite.PodCargoSpawned;
            BenchEnemy(site, s, "enemy_drop_pod", "pod1", new Vector2(10f, 10f));
            int pod = UnitOf(site, "pod1");
            RunFor(site, Dt);
            CombatUnitView v0 = View(site, pod);
            bool falling = Fl(site, pod).State == CombatFleetConst.StatePodFalling && (v0.Flags & CombatUnitFlags.Invulnerable) != 0 && (v0.Flags & CombatUnitFlags.Targetable) == 0
                           && FleetCombatFeedback.PodWarnCues > warn0;
            float dmgFalling = Hit(site, pod, 50f);
            MechEnemy row = Row("enemy_drop_pod");
            RunFor(site, row.WarnSeconds - 0.5f);
            bool stillFalling = Fl(site, pod).State == CombatFleetConst.StatePodFalling && CombatSite.PodCargoSpawned == cargo0;
            RunFor(site, 1f);
            EnemyFleetParams.Cargo(row, out MechEnemy cargo, out int count);
            var spawned = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(r => r.EnemyInstanceId.StartsWith("pod1.c", StringComparison.Ordinal)).ToList();
            bool landed = Fl(site, pod).State == CombatFleetConst.StateNormal && FleetCombatFeedback.PodLandCues > land0 && CombatSite.PodCargoSpawned - cargo0 == count && count == 4
                          && spawned.Count == count && spawned.All(r => r.EnemyTypeId == cargo.Id && r.IsAlive && UnitOf(site, r.EnemyInstanceId) > 0
                                                                        && Math.Abs(Vector2.Distance(r.Position, new Vector2(10f, 10f)) - 2.5f) < 0.6f);
            float dmgLanded = Hit(site, pod, 50f);
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(falling && dmgFalling == 0f && stillFalling && landed && dmgLanded > 0f,
                $"F1 登陆舱（FG09 第 8 章“下落时不可被攻击、落地释放一波”）：生成即下落、不可选中、发落点预警（{falling}），下落中挨 50 伤害掉 {dmgFalling}；" +
                $"预警 {row.WarnSeconds} 秒内不投放（{stillFalling}）；落地后投放 {spawned.Count}/{count} 台 {cargo?.Id}（进敌人记录、在落点 2.5 米圈上，{landed}），落地后能被打（掉 {dmgLanded}）");
        }

        // ─────────────────────────────── G 护盾 ───────────────────────────────

        private static void CheckShield()
        {
            CampaignState s = NewWorldState(4);
            MechEnemy row = Row("enemy_escort_drone");
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_escort_drone", "e1", Vector2.zero);
            int e = UnitOf(site, "e1");
            float full = Fl(site, e).Shield;
            float l1 = Hit(site, e, 100f);
            float s1 = Fl(site, e).Shield;
            int br0 = FleetCombatFeedback.ShieldBreakCues;
            float l2 = Hit(site, e, 100f);
            RunFor(site, Dt);
            bool broke = FleetCombatFeedback.ShieldBreakCues > br0 && Fl(site, e).Shield == 0f;
            RunFor(site, row.FlShieldDelay - 0.5f);
            float sDelay = Fl(site, e).Shield;
            RunFor(site, 2.5f);
            float sRegen = Fl(site, e).Shield;
            Expect(Math.Abs(full - row.FlShieldHp) < 0.01f && l1 == 0f && Math.Abs(s1 - (full - 100f)) < 0.01f && Math.Abs(l2 - (200f - full)) < 0.5f && broke && sDelay == 0f
                   && Math.Abs(sRegen - row.FlShieldRegen * 2f) < row.FlShieldRegen * 0.3f,
                $"G1 护卫护盾：满盾 {full}；挨 100 耐久不掉（护盾剩 {s1}）；再挨 100 打穿、耐久掉 {l2:F1} 并发提示（{broke}）；停止受击 {row.FlShieldDelay} 秒内不回充（{sDelay}），之后每秒回 {row.FlShieldRegen}（2 秒后 {sRegen:F1}）");

            BenchEnemy(site, s, "enemy_escort_drone", "e2", new Vector2(20f, 0f));
            int e2 = UnitOf(site, "e2");
            site.Kernel.SetUnitChargeForTests(e2, 0.5f);
            float lc = Hit(site, e2, 40f);
            float sc = Fl(site, e2).Shield;
            for (int i = 0; i < 480; i++)
            {
                site.Kernel.SetUnitChargeForTests(e2, 0.5f);
                RunFor(site, Dt);
            }
            float sNoRegen = Fl(site, e2).Shield;
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(lc == 0f && Math.Abs(sc - (row.FlShieldHp - 40f * row.FlShieldElec)) < 0.5f && sNoRegen <= sc + 0.01f,
                $"G2 FG09“对电磁弱”：带电荷时挨 40 打在护盾上 × {row.FlShieldElec}（护盾剩 {sc:F1}）；一直带电荷 8 秒不回充（{sNoRegen:F1}）");
        }

        // ─────────────────────────────── H 掉落 ───────────────────────────────

        private static void CheckLoot()
        {
            CampaignState s = NewWorldState(5);
            IReadOnlyList<string> pool = LootCatalog.EnemyFirmwareOf(EnemyDefs.FactionFleet);
            IReadOnlyList<string> any = LootCatalog.EnemyFirmwareOf("no-such-faction");
            bool exact = pool.Count == Prefab.Length && Prefab.All(pool.Contains) && !Prefab.Any(any.Contains);
            bool direct = Prefab.All(id => ItemProtocol.OfFirmware(id) == ItemProtocolKind.Neutral && ItemProtocol.DestinationOfFirmware(s, id) == ItemDestination.DirectUse && !FirmwareKinds.IsRaw(s, id));
            // 审查 P2：掉落 → 送回家园（DeliverEnemyItem → GrantChip）的提示写“可直接使用”，不写加密固件的“需要解析”
            string fw0 = Prefab[0];
            int delivered = FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, fw0, "fleet_h1", Vector2.zero, "fleet_h1_salvage");
            string name0 = FirmwareKinds.DisplayName(fw0) ?? fw0;
            string grantText = FieldLootService.LastGrantText ?? "";
            bool deliverText = delivered > 0 && (grantText == GameText.Format("loot.drop.firmware_direct", name0) || grantText == GameText.Format("loot.drop.firmware_direct_pending", name0))
                               && GameText.Get("loot.drop.firmware_direct") != "loot.drop.firmware_direct";
            Expect(exact && direct && deliverText,
                $"H1 掉落（FG09 第 8 章“中立协议预制固件”）：白潮单位的固件池正好是 3 条预制固件 [{string.Join(",", pool)}]、不混进“所有阵营”池（{exact}）；中立协议、不用解析可直接使用（{direct}）；" +
                $"送回家园的提示“{grantText}”（{deliverText}）");
        }

        private static int Acts(CombatSite site)
        {
            int n = 0;
            for (int i = 0; i < 8192; i++)
            {
                n += site.Kernel.InstrActivations(i);
            }
            return n;
        }

        /// <summary>H2：3 条预制固件装在己方机器上真的按声明出手（触发前不执行；受击 → 洒水 / 带电，过热 → 推开力场）。模糊池不收它们（DEBT-FG13FAC01-02），行为在这里逐条断言。</summary>
        private static void CheckPrefabBehavior()
        {
            CampaignState s = NewWorldState(8);
            var rows = new List<string>();
            bool all = true;
            foreach (string fw in Prefab)
            {
                CombatSite site = OpenBench(s);
                site.Kernel.InstrActivationCounting = true;
                BlueprintCircuitPreview p = CombatSite.EnemyPreview(ComponentCatalog.CompGunId, new[] { fw });
                CombatWeapon w = site.WithProgram(p, CombatSite.MachineWeaponFrom(p));
                int u = Player(site, Vector2.zero, w, 99999f);
                RunFor(site, 0.5f);
                int idle = Acts(site), patches0 = site.Kernel.PatchCount, fields0 = site.Kernel.FieldCount;
                float charge0 = site.Kernel.UnitChargeForTests(u);
                int foe = 0;
                if (fw == "fw_pf_repulse")
                {
                    site.Kernel.InjectUnitHeat(u, 9999f);
                }
                else
                {
                    BenchEnemy(site, s, "enemy_purge_drone", "hurt", new Vector2(4f, 0f)); // 真实的敌方伤害（evt.hurt 走 DamageUnit 统一入口）
                    foe = UnitOf(site, "hurt");
                }
                // 写端效果逐步采样（力场只持续 0.8 秒、电荷会衰减，跑完再查会漏掉）
                int maxFields = fields0, shockZones = 0;
                float maxCharge = 0f;
                for (int n = Mathf.RoundToInt(3f / Dt); n > 0; n--)
                {
                    RunFor(site, Dt);
                    maxFields = Math.Max(maxFields, site.Kernel.FieldCount);
                    maxCharge = Math.Max(maxCharge, Math.Max(site.Kernel.UnitChargeForTests(u), foe > 0 ? site.Kernel.UnitChargeForTests(foe) : 0f));
                    // 事件门控的注电 = 本机处的电击区（挂电击状态位 4，与 FgEmgAddonFirmwareSelfCheck 的 fw_ventarc 同一判据）
                    int sz = 0;
                    for (int zi = 0; zi < site.Kernel.ZoneCount; zi++)
                    {
                        if (site.Kernel.TryGetZone(zi, out CombatZone z) && (z.StatusMask & (1u << 4)) != 0u && math.distance(z.Pos, double2.zero) <= 1.5)
                        {
                            sz++;
                        }
                    }
                    shockZones = Math.Max(shockZones, sz);
                }
                int acts = Acts(site);
                // 审查 P2：电栅要真的在本机处放出电击区（en.elec inject 的事件门控写法）、驱离要真的生成推斥力场（fld.push spawn），不能只看指令执行次数
                bool effect = fw == "fw_pf_quench" ? site.Kernel.PatchCount > patches0 : fw == "fw_pf_repulse" ? maxFields > fields0 : shockZones > 0 && charge0 <= 1e-4f;
                bool ok = w.Program >= 0 && idle == 0 && acts > 0 && effect;
                all &= ok;
                rows.Add($"{fw}：触发前 {idle} 次、触发后 {acts} 次，斑块 {patches0}→{site.Kernel.PatchCount}、力场 {fields0}→最多 {maxFields}、本机处电击区最多 {shockZones} 块、电荷 {charge0:F2}→最多 {maxCharge:F2}（{ok}）");
                CombatSites.Close(Bench, s, dropRecord: true);
            }
            Expect(all, $"H2 FGT-EMG-002 行为面：3 条中立预制固件装在机器上，触发前不执行、触发后按声明出手：{string.Join("；", rows)}");
        }

        // ─────────────────────────────── J 快照 ───────────────────────────────

        private static CombatSite BuildMixedBench(CampaignState s)
        {
            CombatSite site = OpenBench(s);
            BenchEnemy(site, s, "enemy_purge_drone", "x_d1", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_purge_drone", "x_d2", new Vector2(1.5f, 0f));
            BenchEnemy(site, s, "enemy_purifier_walker", "x_w", new Vector2(-20f, 0f));
            BenchEnemy(site, s, "enemy_orbital_beacon", "x_b", new Vector2(20f, 20f));
            BenchEnemy(site, s, "enemy_shield_tower", "x_t", new Vector2(-30f, 30f));
            BenchEnemy(site, s, "enemy_drop_pod", "x_p", new Vector2(30f, -30f));
            BenchEnemy(site, s, "enemy_escort_drone", "x_e", new Vector2(5f, -10f));
            Player(site, new Vector2(-11f, 0f), null, 99999f, CombatUnitKind.Structure);
            Player(site, new Vector2(24f, 22f), null, 99999f);
            return site;
        }

        private static void CheckKernelSnapshot()
        {
            CampaignState s = NewWorldState(6);
            CombatSite site = BuildMixedBench(s);
            RunFor(site, 2.5f); // 登陆舱还在下落（3 秒）
            for (int i = 0; i < 300 && site.Kernel.FleetMarkCount == 0; i++)
            {
                RunFor(site, Dt); // 等步行机扫出第一道焦土（预瞄 1.2 秒 + 冷却）
            }
            site.Kernel.Damage(UnitOf(site, "x_e"), 60f, 0);
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
            ulong h1 = b.StateHash();
            bool counts = b.FleetCount == k.FleetCount && b.FleetStrikeCount == k.FleetStrikeCount && b.FleetMarkCount == k.FleetMarkCount
                          && k.FleetCount >= 6 && k.FleetStrikeCount >= 1 && k.FleetMarkCount >= 1;
            string diagJ = $"原 {k.FleetCount}/{k.FleetStrikeCount}/{k.FleetMarkCount} 读回 {b.FleetCount}/{b.FleetStrikeCount}/{b.FleetMarkCount}";
            double t0 = _t;
            for (int i = 0; i < 240; i++)
            {
                t0 += Dt;
                k.Step(Dt, t0);
                b.Step(Dt, t0);
            }
            bool cont = k.StateHash() == b.StateHash() && k.EmgStateHash() == b.EmgStateHash() && b.FleetMarkCount == k.FleetMarkCount;
            byte[] old = k.SerializeFormatForTests(24);
            using var c = new CombatKernel(k.Config, 64);
            CombatSite.ApplyEmgSubForms(c);
            CombatSite.ApplyEntityProfiles(c);
            CombatSite.ApplyMatters(c);
            CombatLoadResult lr24 = c.Load(old);
            Expect(lr == CombatLoadResult.Ok && h0 == h1 && counts && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 25
                   && lr24 == CombatLoadResult.Ok && CombatKernel.PeekFormat(old) == 24 && c.FleetCount == 0 && c.FleetStrikeCount == 0 && c.FleetMarkCount == 0,
                $"J1 内核快照（格式 {CombatConst.FormatVersion}，段 40 白潮单位 {k.FleetCount} 条 / 段 41 打击 {k.FleetStrikeCount} / 段 42 焦土 {k.FleetMarkCount}）往返哈希一致（{h0 == h1}，{lr}）、" +
                $"读回后接着跑 4 秒逐位相同（{cont}）；格式 24 旧档照常读（{lr24}，格式 {CombatKernel.PeekFormat(old)}、白潮 {c.FleetCount}/{c.FleetStrikeCount}/{c.FleetMarkCount}；读回计数一致 {counts}：{diagJ}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── K 暂停与倍速 ───────────────────────────────

        private static void CheckSpeedMatrix()
        {
            var rows = new List<string>();
            bool paused = true, same = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(42);
                CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
                int id = home.ScheduleOrbitalStrike(CorePos(s) + new Vector2(80f, 80f), 3f, 10f, 2f, 0f);
                WorldSimulation.StepMany(1);
                GameClock.SetPaused(true);
                for (int i = 0; i < 60; i++)
                {
                    WorldSimulation.Frame(0.1f);
                }
                paused &= Pending(home, id);
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long start = GameClock.Ticks;
                long mid = start + (long)(GameClock.StepHz * 1.7f), end = start + (long)(GameClock.StepHz * 2.3f);
                for (int i = 0; i < 6000 && GameClock.Ticks < mid; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                bool aliveMid = Pending(home, id);
                for (int i = 0; i < 6000 && GameClock.Ticks < end; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                bool done = !Pending(home, id);
                GameClock.SetSpeed(1f);
                same &= aliveMid && done;
                rows.Add($"{speed}x {(aliveMid ? "1.7 秒未落" : "1.7 秒已落")}/{(done ? "2.3 秒已落" : "2.3 秒未落")}");
            }
            Expect(paused && same, $"K1 暂停 6 秒轨道打击不落（{paused}）；0.5x / 1x / 2x / 3x 都按 2 个游戏秒落下：{string.Join("、", rows)}（焦土恢复同一时钟，见 C2）");
        }

        private static bool Pending(CombatSite site, int id)
        {
            for (int i = 0; i < site.Kernel.FleetStrikeCount; i++)
            {
                if (site.Kernel.FleetStrikeAt(i).Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── I 反馈与图鉴 ───────────────────────────────

        private static void CheckFeedbackAndCodex()
        {
            MechanicCodex.ResetContentForTests();
            MechanicCodex.OnEnemyEncountered("enemy_purge_drone");
            bool unlocked = MechanicCodex.IsUnlocked(MechanicCodex.EnemyEntryId("enemy_purge_drone")) && MechanicCodex.IsUnlocked(MechanicCodex.FactionEntryId(EnemyDefs.FactionFleet));
            string[] hooks = { "fleet.first_encounter", "fleet.first_formation_broken", "fleet.first_inert", "fleet.first_strike_mark", "fleet.first_uplink_blocked",
                "fleet.first_pod", "fleet.first_shield_break" };
            var unseen = hooks.Where(h => !TouchedHooks.Contains(h)).ToList();
            var unknown = hooks.Where(h => !GuidanceHooks.Known.Contains(h)).ToList();
            string[] entries = { "codex.fleet.formation", "codex.fleet.inert", "codex.fleet.strike", "codex.fleet.block", "codex.fleet.pod", "codex.fleet.shield" };
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
            string[] keys = { "fleet.cue.beam_aim", "fleet.cue.beam", "fleet.cue.strike_mark", "fleet.cue.strike_cancel", "fleet.cue.pod_warn", "fleet.cue.shield_break",
                "signal.uplink.reason.shielded", "grid.reason.inert_ground", "ui.map.fleet.block", "ui.map.fleet.strike", "ui.map.fleet.pod", "raid.trait.fleet" };
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                missing.AddRange(keys.Where(k => GameText.Get(k) == k).Select(k => lang + ":" + k));
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(unlocked && unseen.Count == 0 && unknown.Count == 0 && missing.Count == 0,
                $"I1 DEBT-FG9UX01-02：第一次遭遇清洗无人机解锁敌人与白潮阵营条目（{unlocked}）；每条专属行为与首次遭遇都在本次运行里发了引导钩子（未发 {unseen.Count}：{string.Join(",", unseen)}；未登记 {unknown.Count}）；" +
                $"6 条白潮机制图鉴与提示 / 原因 / 地图文本中英齐全（缺 {missing.Count}：{string.Join(",", missing.Take(6))}）");
            Expect(FleetCombatFeedback.BeamAimCues > 0 && FleetCombatFeedback.BeamCues > 0 && FleetCombatFeedback.RecoverCues > 0 && FleetCombatFeedback.StrikeMarkCues > 0
                   && FleetCombatFeedback.StrikeCancelCues > 0 && FleetCombatFeedback.StrikeCues > 0 && FleetCombatFeedback.PodWarnCues > 0 && FleetCombatFeedback.PodLandCues > 0
                   && FleetCombatFeedback.FormationBrokenCues > 0 && FleetCombatFeedback.ShieldBreakCues > 0,
                $"I2 FGR-FAC-008 ①：预瞄 {FleetCombatFeedback.BeamAimCues} / 光束 {FleetCombatFeedback.BeamCues} / 恢复 {FleetCombatFeedback.RecoverCues} / 打击标记 {FleetCombatFeedback.StrikeMarkCues} / 取消 {FleetCombatFeedback.StrikeCancelCues} / " +
                $"落下 {FleetCombatFeedback.StrikeCues} / 舱预警 {FleetCombatFeedback.PodWarnCues} / 落地 {FleetCombatFeedback.PodLandCues} / 打散 {FleetCombatFeedback.FormationBrokenCues} / 破盾 {FleetCombatFeedback.ShieldBreakCues} 都有声音 + 字幕");
        }

        // ─────────────────────────────── P 性能 ───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(7);
            CombatSite site = OpenBench(s);
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
            int records = site.Kernel.FleetCount;
            var sw = Stopwatch.StartNew();
            int steps = 120;
            RunFor(site, steps * Dt);
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            int marks = site.Kernel.FleetMarkCount, strikes = site.Kernel.FleetStrikeCount;
            CombatSites.Close(Bench, s, dropRecord: true);
            PerfGate.Expect(perStep > 0 && records > 50, $"P1 性能（Editor batchmode，Mono JIT，一次测量；真机 IL2CPP + HybridCLR 另测 FG15-SYS-02）：200 个白潮单位（六种各占六分之一，运行记录 {records} 条、" +
                                                        $"焦土 {marks} 格、打击 {strikes} 次排队）+ 40 台机器的内核一步 {perStep:F3} ms",
                new[] { new PerfGate.Metric("白潮单位内核一步(ms)", perStep, 4.0, false) }, Expect, Line);
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
            CampaignState s = CampaignState.CreateNew("fgfleet-" + seed, "Standard", seed);
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
            CampaignState s = CampaignState.CreateNew("fgfleet-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += 0.05f;
                WorldSimulation.Frame(0.05f);
            }
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgFleetFactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalUplinkService.CoverageProvider = null;
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
