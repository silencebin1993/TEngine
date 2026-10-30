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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.SilentMind;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-FAC-01 静默扩充与寂听主脑（FG09 第 4 章；FGR-FAC-002 / 004 / 006 / 008；验收 FGT-FAC-002～004、006、009）的自检——起真实系统、断言行为：
    /// A 数据：4 类新单位 + 精英 + 首领入表（读法 / 标签 / 职能 / 图鉴文本）、FGT-FAC-002 大威力攻击与专属行为预警 ≥ 0.8 秒（表层）、大厅点位在 3 个种子下都落在空地、突袭 / 反制 / 领地 / 情报行。
    /// B 内核专属语法（战斗内核真实步进）：静默潜伏（静止进入、远程锁不中、范围伤害命中即现形并照常扣血、侦测揭示）、伏击（现形预警 → 追击 → 瞄准线出手，预警实测 ≥ 0.8 秒，
    ///   信号中继优先）、偏折场（预警实测 ≥ 0.8 秒；展开时即时射弹打回开火者、弹体掉头、格斗不受影响、间隙照常命中）、断链束（照射满 2 秒断链、遮挡视线清零）、快照往返逐位一致。
    /// C 寂听主脑（从正式入口进地下中继大厅）：开战与入口情报预警、阶段唯一写入口（非法转换被拒）、诱饵 / 真节点、中继跳转与跳转被打断（负向）、监听站情报指出真节点、
    ///   阶段二偏折场与反弹、阶段三静默预警 → 20 秒全场断链（接入被拒、全部机器安全模式、门控核心固件不生效——“机器全依赖核心固件”负向）→ 核心暴露 → 击败奖励
    ///   （集群协议 / 标记跳转 / 三选一 / 领地平定 / 主线监听阵列核 / 剧情节点 / 报复突袭规则），FGT-FAC-003 中途存读档一致，FGT-FAC-004 全灭复位与首领战前自动存档重试，
    ///   全员撤出复位，暂停与 0.5x～3x、观察与否结果一致。
    /// D 电磁噪声：噪声区覆盖 -30%（单点采样与内核逐机器评估同一口径）、噪声发射器可摧毁且进存档、悬停说明。
    /// E 监听节点暴露翻倍、静默反制每日锁定、大厅按反制追加编成、攻城单位带偏折场。F 伏笔地点（天空扫描墙）读取进资料库。G 性能（一次测量）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSilentFactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgSilentFactionSelfCheck。
    /// </summary>
    public static class FgSilentFactionSelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string Hall = "relay_hall";
        private const string Bench = "fg9fac-bench";
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static readonly int[] Seeds = { 1, 42, 20261017 };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/FG 静默扩充与寂听主脑")]
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
            Line("\n[静默扩充与寂听主脑] 新单位与专属语法、寂听主脑三阶段、电磁噪声、反制与突袭、伏笔地点（FG9-FAC-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsilent-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                SilentMindBoss.ResetForTests();
                SilentCombatFeedback.ResetForTests();
                SilentCounterService.ResetForTests();
                EmNoiseService.ResetForTests();

                Step(CheckData);
                Step(CheckWarnings);
                Step(CheckHallPoints);
                Step(CheckStealthAndAmbush);
                Step(CheckDeflect);
                Step(CheckSever);
                Step(CheckKernelSnapshot);
                Step(CheckBossEngageAndPhaseOne);
                Step(CheckBossJumps);
                Step(CheckBossPhaseTwoThreeAndRewards);
                Step(CheckBossSaveLoad);
                Step(CheckBossWipeRetryAndExit);
                Step(CheckBossSpeedMatrixAndObservation);
                Step(CheckNoise);
                Step(CheckListenAndCounter);
                Step(CheckRaidLurkReveal);
                Step(CheckForeshadow);
                Step(CheckPerf);
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                SignalLinkService.SiteSilencedProvider = null;
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
                // 还原本机设置（本段触发的引导钩子不留在设置里，别的段不受影响）。
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
            Line(_fail == 0 ? $"  静默扩充与寂听主脑：全部 {_pass} 项通过" : $"  静默扩充与寂听主脑：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static MechEnemy Row(string id) => EnemyDefs.TryGet(id, out MechEnemy r) ? r : null;

        private static void CheckData()
        {
            string[] ids = { "enemy_listen_node", "enemy_prism", "enemy_echo_hunter", "enemy_decoy_relay", "enemy_listener", "enemy_mind_core", "enemy_mind_relay" };
            var problems = new List<string>();
            foreach (string id in ids)
            {
                MechEnemy r = Row(id);
                if (r == null)
                {
                    problems.Add(id + " 缺行");
                    continue;
                }
                if (r.Faction != EnemyDefs.FactionSilent || EnemyDefs.ProblemsFor(r).Count > 0)
                {
                    problems.Add(id + " 阵营 / 结构问题：" + string.Join(",", EnemyDefs.ProblemsFor(r)));
                }
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    string desc = GameText.Get(r.DescKey);
                    string weak = GameText.Get(r.WeaknessKey);
                    if (string.IsNullOrEmpty(desc) || desc == r.DescKey || string.IsNullOrEmpty(weak) || weak == r.WeaknessKey || desc == weak)
                    {
                        problems.Add($"{id} 图鉴文本（{lang}）缺：desc“{desc}” weakness“{weak}”");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            bool readings = EnemyDefs.TryGet("enemy_prism", out MechEnemy prism) && prism.Behavior == EnemyDefs.BehaviorDeflect
                            && Row("enemy_echo_hunter")?.Behavior == EnemyDefs.BehaviorStalk && Row("enemy_listener")?.Behavior == EnemyDefs.BehaviorSever
                            && EnemyDefs.IsElite(Row("enemy_listener")) && EnemyDefs.IsBoss(Row("enemy_mind_core")) && EnemyDefs.HasTag(Row("enemy_listen_node"), EnemyDefs.TagListenAmp)
                            && EnemyDefs.HasTag(Row("enemy_decoy_relay"), EnemyDefs.TagDecoy) && EnemyBehaviorReadings.TryGet(EnemyDefs.BehaviorDeflect, out _)
                            && EnemyBehaviorReadings.TryGet(EnemyDefs.BehaviorStalk, out _) && EnemyBehaviorReadings.TryGet(EnemyDefs.BehaviorSever, out _);
            // 组件与固件列（DEBT-FG9DATA01-02）：新敌人写真实 ID。
            bool components = ids.Where(i => Row(i) != null && Row(i).Components != EnemyDefs.None)
                .All(i => Row(i).Components.Split(',').All(c => FirmwareKinds.IsFirmware(c) || ConfigSystem.Instance.Tables.TbCombatComponent.GetOrDefault(c) != null));
            Expect(problems.Count == 0 && readings && components,
                $"A1 静默扩充入表：监听节点 / 磁暴棱柱 / 回声猎手 / 诱饵中继 + 精英寂听者 + 首领寂听主脑与中继节点 7 行，读法（偏折 / 伏击 / 断链）与标签（监听翻倍 / 诱饵）按表，" +
                $"每种单位的图鉴文本（描述 + 弱点即应对）中英齐全，组件与固件列都是真实 ID（{components}）；问题：{string.Join("；", problems)}");

            Tables t = ConfigSystem.Instance.Tables;
            bool raid = t.TbRaidUnit.DataList.Any(r => r.Faction == "silent" && r.EnemyTypeId == "enemy_echo_hunter")
                        && t.TbRaidUnit.DataList.Any(r => r.Faction == "silent" && r.EnemyTypeId == "enemy_prism" && r.Tags.Contains("deflect"))
                        && t.TbRaidCounter.DataList.Any(c => c.Faction == "silent" && c.Category == "fuse" && c.Tag == "deflect")
                        && t.TbRaidCounter.DataList.Any(c => c.Faction == "silent" && c.Category == "em" && c.Tag == "jam")
                        && t.TbSiegeUnit.DataList.Any(u => u.EnemyTypeId == "enemy_echo_hunter") && t.TbSiegeUnit.DataList.Any(u => u.EnemyTypeId == "enemy_prism")
                        && t.TbRaidStory.DataList.Any(r => r.Rule == RaidCatalog.StoryRuleSilentMind && r.Faction == "silent");
            bool territory = t.TbTerritoryControl.GetOrDefault("silent")?.LeaderRule == RaidCatalog.StoryRuleSilentMind;
            bool intel = IntelCatalog.TryGetBoss(IntelCatalog.BossIdSilentMind, out IntelBossDef boss) && boss.Rule == IntelCatalog.BossRuleSilentMind;
            bool hazard = t.TbHazard.DataList.Any(h => h.Id == EmNoiseService.HazardId && h.TerritoryId == "silent");
            Expect(raid && territory && intel && hazard,
                $"A2 突袭编成（回声猎手刺杀、磁暴棱柱偏折）/ 阵营内反制（引信→磁暴棱柱、电磁→干扰机）/ 攻城参数 / 静默报复剧情突袭 {raid}；领地首领规则 silent_mind {territory}；" +
                $"监听站首领弱点情报行 {intel}；静默危害 = 电磁噪声 {hazard}");
        }

        /// <summary>A3 FGT-FAC-002 / 009（表层）：大威力攻击（单发 ≥ 20）有 ≥ 0.8 秒瞄准线；专属行为（偏折 / 伏击 / 潜伏现形 / 断链束 / 中继跳转 / 静默预警）预警 ≥ 0.8 秒。</summary>
        private static void CheckWarnings()
        {
            var bad = new List<string>();
            foreach (MechEnemy r in EnemyDefs.Rows)
            {
                if (r.WeaponDamage >= 20f && Math.Max(r.WeaponAim, r.WarnSeconds) < 0.8f)
                {
                    bad.Add($"{r.Id} 单发 {r.WeaponDamage} 预警 {Math.Max(r.WeaponAim, r.WarnSeconds)}");
                }
                if ((r.Behavior == EnemyDefs.BehaviorDeflect || r.Behavior == EnemyDefs.BehaviorStalk || r.LurkSeconds > 0f) && r.WarnSeconds < 0.8f)
                {
                    bad.Add($"{r.Id} 专属行为预警 {r.WarnSeconds}");
                }
                if (r.Behavior == EnemyDefs.BehaviorSever && r.EffectSeconds < 0.8f)
                {
                    bad.Add($"{r.Id} 断链束照射 {r.EffectSeconds}");
                }
            }
            bool boss = SilentMindBoss.JumpWarnSeconds >= 0.8f && SilentMindBoss.SilenceWarnSeconds >= 0.8f && Math.Abs(SilentMindBoss.SilenceSeconds - 20f) < 1e-4f;
            bool core = (Row("enemy_mind_core")?.WarnSeconds ?? 0f) >= 1.5f - 1e-4f;
            Expect(bad.Count == 0 && boss && core,
                $"A3 FGT-FAC-002（表层）：全部敌人的大威力攻击与专属行为预警 ≥ 0.8 秒；中继跳转 {SilentMindBoss.JumpWarnSeconds} 秒、全频静默预警 {SilentMindBoss.SilenceWarnSeconds} 秒、" +
                $"静默 {SilentMindBoss.SilenceSeconds} 秒、全场偏折场预警 {Row("enemy_mind_core")?.WarnSeconds} 秒（规格 1.5）；违规：{string.Join("；", bad)}（实测见 B / C 段）");
        }

        /// <summary>A4 B25 / FGR-GEN-024：大厅的手工点位在 3 个种子下都落在可走的空地上、互不重叠（立柱按种子生成）。</summary>
        private static void CheckHallPoints()
        {
            var problems = new List<string>();
            byte cliff = GridContent.TerrainCode("cliff");
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorldState(seed);
                RelayHallLayout.ResetCacheForTests();
                HomeGridMap map = WorldGenService.MapFor(s, Hall);
                var seen = new HashSet<Vector2>();
                foreach (InteriorPoint p in RelayHallLayout.Points(Hall))
                {
                    if (!RelayHallLayout.TryPoint(s, p.Id, out Vector2 at))
                    {
                        problems.Add($"{seed}:{p.Id} 落不了位");
                        continue;
                    }
                    if (map.GetTerrain(new GridCell(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y))) == cliff)
                    {
                        problems.Add($"{seed}:{p.Id} 落在立柱上 {at}");
                    }
                    if (!seen.Add(at))
                    {
                        problems.Add($"{seed}:{p.Id} 与别的点位重叠 {at}");
                    }
                }
            }
            int relays = RelayHallLayout.Points(Hall).Count(p => p.Kind == RelayHallLayout.KindRelay);
            Expect(problems.Count == 0 && relays == 6 && SilentMindBoss.Nodes.Count == 6 && SilentMindBoss.CoreInstanceId != null,
                $"A4 B25 地下中继大厅的手工点位（{RelayHallLayout.Points(Hall).Count} 个，含 6 个中继节点、主脑、伏笔墙）在种子 {string.Join("/", Seeds)} 下都落在空地且不重叠；问题：{string.Join("；", problems)}");
        }

        // ─────────────────────────────── B 内核专属语法 ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
            public int Killed;
            public override void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy) => Killed++;
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

        private static int Player(CombatSite site, Vector2 at, CombatWeapon wp, CombatUnitFlags extra = CombatUnitFlags.None, float hp = 400f)
        {
            int w = site.WeaponIndex(wp);
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | extra, Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
        }

        private static CombatWeapon Gun(float dmg = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 30f, Damage = dmg, Cooldown = 0.05f, TargetMode = CombatTargetMode.Nearest,
        };

        private static CombatWeapon Bolt(float dmg = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 30f, Damage = dmg, Cooldown = 0.05f, ProjectileSpeed = 30f, ProjectileRadius = 0.2f,
            TargetMode = CombatTargetMode.Nearest,
        };

        private static CombatWeapon Area(CombatCarrier carrier, float dmg = 10f)
        {
            CombatWeapon w = Gun(dmg);
            w.Reading.Carrier = carrier;
            w.Reading.Area = 6f;
            w.Reading.Cone = 180f;
            w.Reading.Approach = 6f;
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

        private static void CheckStealthAndAmbush()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_echo_hunter", "b_hunter", new Vector2(0f, 0f));
            int hunter = UnitOf(site, "b_hunter");
            int p = Player(site, new Vector2(40f, 0f), Gun());
            RunFor(site, 1f);
            bool notYet = !View(site, hunter).Flags.HasFlag(CombatUnitFlags.Stealthed);
            RunFor(site, 2.5f);
            CombatUnitView v = View(site, hunter);
            bool stealthed = v.Flags.HasFlag(CombatUnitFlags.Stealthed);
            CombatFireResult remote = site.Kernel.FireAt(p, hunter, _t);
            bool pickHidden = !site.TryFindNearestEnemy(new Vector2(0f, 0f), 5f, out _);
            Expect(notYet && stealthed && remote == CombatFireResult.Hidden && pickHidden && GameText.Get("combat.fire.hidden") != "combat.fire.hidden",
                $"B1 静默潜伏：静止 1 秒时还没潜伏（{notYet}），满 3 秒进入潜伏（{stealthed}）；远程锁定 → {remote}（原因“{site.FireReason(remote, 0, "b_hunter")}”），点选也选不中（{pickHidden}）");

            // 负向（FG09 第 10 节）：潜伏中被范围伤害命中 → 立即现形，并照常结算伤害。
            float hp0 = View(site, hunter).Health;
            site.Kernel.SetPosition(p, new double2(3f, 0f));
            int aura = Player(site, new Vector2(2f, 1f), Area(CombatCarrier.Aura, 15f));
            // 先拿另一台没武器的诱饵挡住伏击目标判定不影响：直接对潜伏单位附近的另一个目标开力场（力场按圈命中全部敌人）。
            BenchEnemy(site, s, "enemy_decoy_relay", "b_decoy", new Vector2(2.5f, 1.5f));
            CombatFireResult auraHit = site.Kernel.FireAt(aura, UnitOf(site, "b_decoy"), _t);
            site.ProcessEvents();
            CombatUnitView after = View(site, hunter);
            Expect(auraHit == CombatFireResult.Ok && !after.Flags.HasFlag(CombatUnitFlags.Stealthed) && after.Health < hp0 && SilentCombatFeedback.RevealCues > 0,
                $"B2 负向：潜伏的回声猎手被力场（范围伤害）命中 → 立即现形（{!after.Flags.HasFlag(CombatUnitFlags.Stealthed)}），伤害照常结算（{hp0:0} → {after.Health:0}）；现形有声音 + 字幕（{SilentCombatFeedback.RevealCues} 次）");

            // 伏击：重新潜伏后，机器走进感知范围 → 现形（展桅预警）→ 追上去 → 瞄准线 → 出手；从机器进入范围到第一次掉血 ≥ 预警下限。
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_echo_hunter", "b_hunter", new Vector2(0f, 0f));
            hunter = UnitOf(site, "b_hunter");
            int victim = Player(site, new Vector2(40f, 0f), Gun(0.01f));
            int relay = Player(site, new Vector2(-40f, 0f), Gun(0.01f), CombatUnitFlags.SignalRelay);
            RunFor(site, 3.5f);
            bool lurking = View(site, hunter).Flags.HasFlag(CombatUnitFlags.Stealthed);
            site.Kernel.SetPosition(victim, new double2(6f, 0f));
            site.Kernel.SetPosition(relay, new double2(-8f, 0f)); // 更远，但装着信号中继
            double enter = _t;
            double firstHit = -1;
            float hv0 = View(site, relay).Health;
            float hn0 = View(site, victim).Health;
            for (int i = 0; i < 600 && firstHit < 0; i++)
            {
                RunFor(site, Dt);
                if (View(site, relay).Health < hv0 || View(site, victim).Health < hn0)
                {
                    firstHit = _t;
                }
            }
            bool hitRelay = View(site, relay).Health < hv0 && View(site, victim).Health >= hn0;
            double warn = firstHit - enter;
            float need = Row("enemy_echo_hunter").WarnSeconds;
            Expect(lurking && firstHit > 0 && warn >= 0.8 && warn >= need && hitRelay,
                $"B3 伏击（FGT-FAC-009 实测）：潜伏中的回声猎手在机器进入感知范围后先现形预警（展桅 {need} 秒）再追击、瞄准线 {Row("enemy_echo_hunter").WeaponAim} 秒后出手——" +
                $"从进入范围到第一次掉血 {warn:0.00} 秒（≥ 0.8）；目标优先装有信号中继的机器（更远的中继机挨打 {hitRelay}）");

            // 侦测：装信号中继的机器在侦测距离内 → 潜伏单位不能潜伏。
            site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_echo_hunter", "b_hunter", new Vector2(0f, 0f));
            hunter = UnitOf(site, "b_hunter");
            Player(site, new Vector2(7f, 7f), Gun(0.01f), CombatUnitFlags.SignalRelay, 9999f);
            RunFor(site, 1f);
            bool chased = !View(site, hunter).Flags.HasFlag(CombatUnitFlags.Stealthed);
            CombatSite site2 = OpenBench(s);
            _t = 0;
            BenchEnemy(site2, s, "enemy_listener", "b_listener", new Vector2(0f, 0f));
            int listener = UnitOf(site2, "b_listener");
            Player(site2, new Vector2(30f, 0f), Gun(0.01f));
            RunFor(site2, 4.5f);
            bool lurked = View(site2, listener).Flags.HasFlag(CombatUnitFlags.Stealthed);
            int marker = Player(site2, new Vector2(5f, 0f), new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 30f, Damage = 0.01f, Cooldown = 9f, MarkSeconds = 5f });
            RunFor(site2, Dt * 2);
            bool revealedByMarker = !View(site2, listener).Flags.HasFlag(CombatUnitFlags.Stealthed);
            Expect(chased && lurked && revealedByMarker,
                $"B4 侦测揭示（FGR-FAC-008“监听站、标记器、信号中继覆盖可揭示”）：中继机在旁 → 猎手不能潜伏（{chased}）；寂听者静止 4 秒潜伏（{lurked}），带标记器的机器走近 8 米内 → 现形（{revealedByMarker}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckDeflect()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_prism", "b_prism", new Vector2(0f, 0f));
            int prism = UnitOf(site, "b_prism");
            int gun = Player(site, new Vector2(20f, 0f), Gun(10f));
            int bolt = Player(site, new Vector2(-20f, 0f), Bolt(10f));
            int melee = Player(site, new Vector2(1.5f, 0f), Area(CombatCarrier.Melee, 10f));
            // 间隙 → 预警 → 展开：实测预警时长。
            double warnAt = -1, upAt = -1;
            for (int i = 0; i < 60 * 12 && upAt < 0; i++)
            {
                RunFor(site, Dt);
                site.TryGetUnitSilent(prism, out CombatSilentUnit sl, out _);
                if (sl.Deflect == 1 && warnAt < 0)
                {
                    warnAt = _t;
                }
                if (sl.Deflect == 2)
                {
                    upAt = _t;
                }
            }
            double warn = upAt - warnAt;
            // 展开中：即时射弹被打回开火者（目标不掉血），弹体掉头；格斗照常打到。
            float prismHp = View(site, prism).Health;
            float gunHp = View(site, gun).Health;
            CombatFireResult r1 = site.Kernel.FireAt(gun, prism, _t);
            site.ProcessEvents();
            float prismAfterGun = View(site, prism).Health;
            float gunAfter = View(site, gun).Health;
            float boltHp = View(site, bolt).Health;
            site.Kernel.FireAt(bolt, prism, _t);
            RunFor(site, 1.6f);
            float boltAfter = View(site, bolt).Health;
            bool stillUp = site.TryGetUnitSilent(prism, out CombatSilentUnit up, out _) && up.Deflect == 2;
            float prismBeforeMelee = View(site, prism).Health;
            CombatFireResult r3 = site.Kernel.FireAt(melee, prism, _t);
            site.ProcessEvents();
            float prismAfterMelee = View(site, prism).Health;
            long reflected = site.ShotsReflected;
            Expect(warnAt > 0 && warn >= 0.8 - 1e-6 && r1 == CombatFireResult.Ok && Math.Abs(prismAfterGun - prismHp) < 1e-3f && gunAfter < gunHp
                   && boltAfter < boltHp && reflected >= 2 && stillUp && r3 == CombatFireResult.Ok && prismAfterMelee < prismBeforeMelee && SilentCombatFeedback.DeflectWarnCues > 0,
                $"B5 偏折场（FGT-FAC-009 实测）：预警 {warn:0.00} 秒（≥ 0.8，表 {Row("enemy_prism").WarnSeconds}）后展开；展开时即时射弹打回开火者（棱柱 {prismHp:0}→{prismAfterGun:0} 不掉，开火者 {gunHp:0}→{gunAfter:0}），" +
                $"弹体掉头打中发射者（{boltHp:0}→{boltAfter:0}），反弹 {reflected} 次；格斗不受偏折（{prismBeforeMelee:0}→{prismAfterMelee:0}）；预警有声音 + 字幕 {SilentCombatFeedback.DeflectWarnCues} 次");

            // EMG1-BUS-01：偏折场真实反弹路径发 evt.bounce（面 = 场、Other = 场源），宿主 = 本次出手 / 弹体并带它的程序；反弹回来的弹体去掉程序。上面没有程序的开火一条不入队（对照）。
            long bounceNoProg = site.Kernel.EmgEnqueuedOf(EmgEventKind.Bounce);
            int bp = site.Kernel.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Bounce },
                new[] { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = 1 } },
                new[] { new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Bounce), Role = EmgConst.RoleTrigger } });
            CombatWeapon progGun = Gun(10f);
            progGun.Program = bp;
            CombatWeapon progBolt = Bolt(10f);
            progBolt.Program = bp;
            int gunP = Player(site, new Vector2(20f, 3f), progGun);
            int boltP = Player(site, new Vector2(-20f, 3f), progBolt);
            CombatFireResult rp = site.Kernel.FireAt(gunP, prism, _t);
            // 只入队、不内联：开火这一刻事件还在队里（TryGetLastEmgEvent 是“最后处理的一条”，要等下一步）。
            bool instBounce = site.Kernel.EmgEnqueuedOf(EmgEventKind.Bounce) == bounceNoProg + 1
                              && Enumerable.Range(0, site.Kernel.EmgBusPending).Any(j => site.Kernel.TryGetEmgEvent(j, out EmgEvent ib) && ib.Kind == EmgEventKind.Bounce
                                  && ib.EntKind == EmgEntKind.Unit && ib.Ent == gunP && ib.Code == EmgCode.FaceField && ib.Prog == bp && ib.Other == prism);
            site.Kernel.FireAt(boltP, prism, _t);
            bool projBounce = false, stripped = false;
            for (int i = 0; i < 120; i++)
            {
                RunFor(site, Dt);
                if (site.Kernel.EmgEnqueuedOf(EmgEventKind.Bounce) >= bounceNoProg + 2 && site.Kernel.TryGetLastEmgEvent(EmgEventKind.Bounce, out EmgEvent pbv))
                {
                    projBounce = pbv.EntKind == EmgEntKind.Projectile && pbv.Code == EmgCode.FaceField && pbv.Prog == bp && pbv.Other == prism;
                    stripped = Enumerable.Range(0, site.Kernel.ProjectileCount).All(j => !site.Kernel.TryGetProjectile(j, out CombatProjectile cp) || cp.Prog < 0);
                    break;
                }
            }
            Expect(bounceNoProg == 0 && instBounce && projBounce && stripped,
                $"EMG1-BUS-01 偏折场真实反弹发 evt.bounce：没有程序的开火不入队（{bounceNoProg}）；带读 evt.bounce 程序的即时射弹 {(instBounce ? "发" : "没发")}（开火结果 {rp}，入队 {site.Kernel.EmgEnqueuedOf(EmgEventKind.Bounce)}；面 = 场、Other = 棱柱）、弹体 {(projBounce ? "发" : "没发")}（宿主 = 弹体）；反弹回来的弹体{(stripped ? "已去掉" : "仍带")}己方程序");

            // 间隙里即时射弹照常命中。
            for (int i = 0; i < 60 * 8; i++)
            {
                RunFor(site, Dt);
                site.TryGetUnitSilent(prism, out CombatSilentUnit g, out _);
                if (g.Deflect == 0)
                {
                    break;
                }
            }
            float gap0 = View(site, prism).Health;
            site.Kernel.FireAt(gun, prism, _t);
            site.ProcessEvents();
            Expect(View(site, prism).Health < gap0, $"B6 偏折场间隙：射弹照常命中（{gap0:0} → {View(site, prism).Health:0}）——抓间隙是应对（图鉴弱点写明）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckSever()
        {
            CampaignState s = NewWorld(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_listener", "b_listener", new Vector2(0f, 0f));
            int a = SpawnIn(s, Bench, new Vector2(10f, 0f));
            MachineRecord rec = Rec(a);
            HomeValleyMachineMarker marker = site.SpawnMachine(s, rec, rec.WorldPosition, autoEngage: false);
            site.SetPossessed(a, true);
            int sever0 = SilentCombatFeedback.SeverEvents;
            double t0 = _t, at = -1;
            for (int i = 0; i < 60 * 4 && at < 0; i++)
            {
                RunFor(site, Dt);
                if (SilentCombatFeedback.SeverEvents > sever0)
                {
                    at = _t;
                }
            }
            double beam = at - t0;
            // 负向：照射中途遮挡视线 → 计时清零，不断链。
            site.SetPossessed(a, false);
            RunFor(site, 7f); // 冷却
            site.SetPossessed(a, true);
            RunFor(site, 1.2f);
            site.SetObstacles(new List<float3> { new float3(5f, 0f, 2f) });
            int sever1 = SilentCombatFeedback.SeverEvents;
            RunFor(site, 2f);
            bool blocked = SilentCombatFeedback.SeverEvents == sever1;
            site.TryGetUnitSilent(UnitOf(site, "b_listener"), out CombatSilentUnit sl, out _);
            Expect(marker != null && at > 0 && beam >= Row("enemy_listener").EffectSeconds - 1e-3 && beam < Row("enemy_listener").EffectSeconds + 0.1 && blocked && sl.SeverT == 0f,
                $"B7 断链束（FGT-FAC-009 实测）：持续照射被接入的机器 {beam:0.00} 秒后断链（表 {Row("enemy_listener").EffectSeconds} 秒；光束全程可见即预警线）；负向：中途被立柱遮挡视线 → 计时清零、不断链（{blocked}）");
            CombatSites.Close(Bench, s, dropRecord: true);
            WorldSimulation.UnloadAll();
        }

        private static void CheckKernelSnapshot()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            BenchEnemy(site, s, "enemy_prism", "b_prism", new Vector2(0f, 0f));
            BenchEnemy(site, s, "enemy_echo_hunter", "b_hunter", new Vector2(20f, 0f));
            BenchEnemy(site, s, "enemy_listener", "b_listener", new Vector2(-20f, 0f));
            Player(site, new Vector2(0f, 40f), Gun(1f));
            RunFor(site, 7.3f);
            CombatKernel k = site.Kernel;
            byte[] snap = k.Serialize();
            ulong h0 = k.StateHash();
            CombatConfig cfg = k.Config;
            using var b = new CombatKernel(cfg, 64);
            CombatLoadResult lr = b.Load(snap);
            ulong h1 = b.StateHash();
            double t0 = _t;
            for (int i = 0; i < 180; i++)
            {
                t0 += Dt;
                k.Step(Dt, t0);
                b.Step(Dt, t0);
            }
            bool cont = k.StateHash() == b.StateHash();
            int hunter = UnitOf(site, "b_hunter");
            b.TryGetProfileOf(hunter, out CombatBehaviorProfile hp);
            k.TryGetSilent(hunter, out CombatSilentUnit ka);
            b.TryGetSilent(hunter, out CombatSilentUnit kb);
            Expect(lr == CombatLoadResult.Ok && h0 == h1 && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && hp.LurkSeconds > 0f && ka.Still == kb.Still,
                $"B8 内核快照（格式 {CombatConst.FormatVersion}）：潜伏 / 偏折场 / 断链束的状态与参数随快照往返，状态哈希一致（{h0 == h1}），读回后接着跑 3 秒与不存档逐位相同（{cont}）（{lr}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── C 寂听主脑 ───────────────────────────────

        private static InteriorSiteController EnterHall(CampaignState s, int count, out int[] machines, out PlannedAnchor gate)
        {
            gate = InteriorSiteService.EntranceFor(s, Hall);
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            HomeGridService.RevealArea(s, e, 30f);
            var ids = new List<int>();
            for (int i = 0; i < count; i++)
            {
                ids.Add(Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, e + new Vector2(i - count / 2f, 1f)));
            }
            machines = ids.ToArray();
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            if (!InteriorSiteService.TryEnter(s, gate.Id, out string why))
            {
                Fail("夹具：进入地下中继大厅被拒：" + why);
            }
            return WorldSimulation.Interior(Hall);
        }

        /// <summary>把大厅里的机器挪到入口内侧不挨打的地方（保持在入口附近，敌人碰不到），方便只测首领。</summary>
        private static void ParkMachines(InteriorSiteController site, int[] ids)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                if (site.Combat.TryGetMachineUnit(ids[i], out int u))
                {
                    site.Combat.SetUnitPosition(u, site.ExitPoint + new Vector2(i - ids.Length / 2f, 1.5f));
                }
            }
        }

        private static void HitNode(InteriorSiteController site, int index, float dmg)
        {
            site.Combat.TryDamageEnemy(SilentMindBoss.Nodes[index].Id, dmg);
        }

        private static void CheckBossEngageAndPhaseOne()
        {
            CampaignState s = NewWorld(42);
            PlannedAnchor gate0 = InteriorSiteService.EntranceFor(s, Hall);
            string previewBefore = InteriorSiteService.BossPreview(s, Hall);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out PlannedAnchor gate);
            string savedCopy = Path.Combine(_dir, "boss-engage-enter.json");
            if (File.Exists(slotFile))
            {
                File.Copy(slotFile, savedCopy, true);
            }
            SilentBossRecord r = SilentMindBoss.Record(s);
            bool seeded = SilentMindBoss.Nodes.All(n => SilentMindBoss.FindPart(s, n.Id)?.IsAlive == true) && SilentMindBoss.Core(s)?.IsAlive == true
                          && s.RegionEnemies.Count(e => e.RegionId == Hall) >= 13;
            int core = UnitOf(site.Combat, SilentMindBoss.CoreInstanceId);
            bool coreHidden = !site.Combat.UnitHasFlag(core, CombatUnitFlags.Targetable) && site.Combat.UnitHasFlag(core, CombatUnitFlags.Invulnerable);
            WorldView.Observe(Hall);
            bool hud = SilentMindHudUIToolkit.Compose(s, out string title, out _, out float mark, out _, out string lines);
            MechEnemy elite = Row("enemy_listener");
            bool eliteLine = lines.Contains(GameText.Get(elite.NameKey)) || site.Combat.UnitHasFlag(UnitOf(site.Combat, "rh_listener"), CombatUnitFlags.Stealthed);
            Expect(previewBefore.Contains(GameText.Get("boss.silent.entrance.unknown")) && File.Exists(savedCopy) && SilentMindBoss.PhaseOf(s) == SilentMindPhase.Hiding
                   && r.Attempt == 1 && r.RealNode >= 0 && seeded && coreHidden && hud && title.Contains(SilentMindBoss.PhaseName(SilentMindPhase.Hiding))
                   && lines.Contains(GameText.Get("boss.silent.hud.nodes_hint")) && Math.Abs(mark - SilentMindBoss.Phase2HpFraction) < 1e-4f && eliteLine,
                $"C1 开战（FGR-FAC-006 / FGR-GEN-042）：入口说明先提示“{previewBefore.Trim()}”；进门前写首领战前自动存档；进门即开战——阶段一·藏身（第 {r.Attempt} 次挑战，真节点 {r.RealNode + 1}），" +
                $"6 个中继节点 + 主脑 + 守卫按表播种（{seeded}），主脑本体藏起来不可选中（{coreHidden}）；首领 HUD“{title}”，血条在 {mark:P0} 处标出阶段分界，提示“{lines.Split('\n')[0]}”，精英寂听者写名字与耐久（{eliteLine}）");

            // 阶段唯一写入口：非法转换被拒。
            int rejected = SilentMindBoss.RejectedTransitions;
            bool illegal = !SilentMindBoss.TryTransition(s, SilentMindPhase.Defeated) && SilentMindBoss.RejectedTransitions == rejected + 1 && SilentMindBoss.PhaseOf(s) == SilentMindPhase.Hiding;
            // 诱饵节点：掉血、可以拆掉，不推进阶段；真节点被打中 → 阶段二。
            int decoy = Enumerable.Range(0, 6).First(i => i != r.RealNode && i != r.JumpTarget);
            float hp0 = SilentMindBoss.Node(s, decoy).Health;
            HitNode(site, decoy, 30f);
            bool decoyHurt = SilentMindBoss.Node(s, decoy).Health < hp0 && SilentMindBoss.PhaseOf(s) == SilentMindPhase.Hiding;
            HitNode(site, decoy, 500f);
            bool decoyDead = !SilentMindBoss.Node(s, decoy).IsAlive;
            // 标记器：被标记的节点显示波形（真节点不规则）。
            int real = r.RealNode;
            int realUnit = UnitOf(site.Combat, SilentMindBoss.Nodes[real].Id);
            site.Combat.Kernel.SetMarkedUntil(realUnit, site.Combat.Kernel.Time + 10.0);
            SilentMindHudUIToolkit.Compose(s, out _, out _, out _, out _, out string marked);
            bool wave = marked.Contains(GameText.Format("boss.silent.hud.wave_real", SilentMindBoss.NodeLabel(real)));
            // 监听站情报（遭遇过之后才破译）：直接指出真节点。
            IntelService.EnsureState(s);
            bool needs = IntelCatalog.TryGet(IntelCatalog.KindBoss, out IntelKindDef bossKind) && IntelService.Needed(s, bossKind, GameClock.Ticks, out _);
            InjectBossIntel(s);
            SilentMindHudUIToolkit.Compose(s, out _, out _, out _, out _, out string known);
            bool intel = known.Contains(GameText.Format("boss.silent.hud.nodes_intel", SilentMindBoss.NodeLabel(real))) && InteriorSiteService.BossPreview(s, Hall).Contains(GameText.Get("boss.silent.entrance.preview"));
            int transitions = SilentMindBoss.TransitionCount;
            HitNode(site, real, 1f);
            bool phase2 = SilentMindBoss.PhaseOf(s) == SilentMindPhase.Magnetic && SilentMindBoss.TransitionCount == transitions + 1;
            WorldSimulation.StepMany(2);
            bool coreShown = site.Combat.UnitHasFlag(core, CombatUnitFlags.Targetable) && !site.Combat.UnitHasFlag(core, CombatUnitFlags.Invulnerable)
                             && site.Combat.UnitHasFlag(core, CombatUnitFlags.DeflectArmed);
            Expect(illegal && decoyHurt && decoyDead && wave && needs && intel && phase2 && coreShown,
                $"C2 阶段一（FGT-FAC-003 唯一写入口）：非法转换 藏身→已击败 被拒（{illegal}）；打诱饵节点只掉血 / 拆掉（{decoyHurt}/{decoyDead}），标记真节点看到不规则波形（{wave}）；" +
                $"遭遇过后监听站要破译首领弱点（{needs}），有效情报直接指出真节点、入口说明写出三个阶段（{intel}）；打中真节点 → 阶段二·磁暴（{phase2}），本体现身、可伤、偏折场上电（{coreShown}）");
            SignalCoverageService.OverrideForTests = null;
        }

        private static void InjectBossIntel(CampaignState s)
        {
            IntelService.EnsureState(s);
            IntelState st = IntelService.StateOf(s);
            var list = new List<IntelRecord>(st.Records ?? Array.Empty<IntelRecord>())
            {
                new IntelRecord
                {
                    Serial = 9000, Kind = IntelCatalog.KindBoss, Subject = IntelCatalog.BossIdSilentMind, Source = "post", ProducedTick = GameClock.Ticks,
                    ExpiresTick = GameClock.Ticks + GameClock.TicksFor(3600.0),
                },
            };
            st.Records = list.ToArray();
        }

        private static void CheckBossJumps()
        {
            CampaignState s = NewWorld(42);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            ParkMachines(site, m);
            SilentBossRecord r = SilentMindBoss.Record(s);
            int start = r.RealNode;
            long lit = -1, done = -1;
            int target = -1;
            for (int i = 0; i < GameClock.StepHz * 20 && done < 0; i++)
            {
                WorldSimulation.StepMany(1);
                if (r.JumpTarget >= 0 && lit < 0)
                {
                    lit = GameClock.Ticks;
                    target = r.JumpTarget;
                }
                if (lit >= 0 && r.JumpTarget < 0)
                {
                    done = GameClock.Ticks;
                }
            }
            double warn = (done - lit) / (double)GameClock.StepHz;
            bool moved = r.RealNode == target && target != start && r.Jumps == 1;
            Expect(lit > 0 && done > 0 && warn >= 0.8 && moved,
                $"C3 中继跳转（FGR-FAC-008，FGT-FAC-009 实测）：约 {SilentMindBoss.JumpSeconds} 秒后目标节点 {target + 1} 提前亮起 {warn:0.00} 秒（≥ 0.8），然后主脑转移过去（{moved}，从节点 {start + 1}）");

            // 负向（FG09 第 10 节）：跳转途中目标节点被摧毁 → 跳转取消，主脑留在出发节点并现形。
            int from = r.RealNode;
            for (int i = 0; i < GameClock.StepHz * 20 && r.JumpTarget < 0; i++)
            {
                WorldSimulation.StepMany(1);
            }
            int t2 = r.JumpTarget;
            int cancelled0 = SilentMindBoss.JumpsCancelled;
            HitNode(site, t2, 9999f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            WorldView.Observe(Hall);
            SilentMindHudUIToolkit.Compose(s, out _, out _, out _, out _, out string lines);
            Expect(t2 >= 0 && SilentMindBoss.JumpsCancelled == cancelled0 + 1 && r.RealNode == from && SilentMindBoss.IsRevealed(s)
                   && lines.Contains(GameText.Format("boss.silent.hud.revealed", SilentMindBoss.NodeLabel(from), string.Empty).Split('（')[0]),
                $"C4 负向：中继跳转的目标节点 {t2 + 1} 在亮起途中被摧毁 → 跳转取消（{SilentMindBoss.JumpsCancelled - cancelled0} 次），主脑留在出发节点 {from + 1} 并现形（{SilentMindBoss.IsRevealed(s)}）；HUD“{lines.Split('\n').LastOrDefault()}”");
            SignalCoverageService.OverrideForTests = null;
        }

        private static void CheckBossPhaseTwoThreeAndRewards()
        {
            CampaignState s = NewWorld(42);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            ParkMachines(site, m);
            SilentBossRecord r = SilentMindBoss.Record(s);
            HitNode(site, r.RealNode, 1f);
            WorldSimulation.StepMany(2);
            int core = UnitOf(site.Combat, SilentMindBoss.CoreInstanceId);
            RegionEnemyRecord coreRec = SilentMindBoss.Core(s);
            // 全场偏折场：实测预警 1.5 秒；展开时机器的射弹被反弹回自己（主脑不掉血）。
            long warnAt = -1, upAt = -1;
            for (int i = 0; i < GameClock.StepHz * 12 && upAt < 0; i++)
            {
                WorldSimulation.StepMany(1);
                site.Combat.TryGetUnitSilent(core, out CombatSilentUnit sl, out _);
                if (sl.Deflect == 1 && warnAt < 0)
                {
                    warnAt = GameClock.Ticks;
                }
                if (sl.Deflect == 2)
                {
                    upAt = GameClock.Ticks;
                }
            }
            double warn = (upAt - warnAt) / (double)GameClock.StepHz;
            string warnDiag = $"{warnAt}→{upAt} @{GameClock.StepHz}Hz";
            MachineRecord shooter = Rec(m[0]);
            float shooterHp = shooter.Health;
            float coreHp = coreRec.Health;
            site.Combat.TryGetMachineUnit(m[0], out int su);
            int coreUnit = core;
            long refl0 = site.Combat.Kernel.Counters.ShotsReflected;
            CombatFireResult fr = site.Combat.Kernel.FireAt(su, coreUnit, site.Combat.Kernel.Time);
            site.Combat.ProcessEvents();
            // EMG1-E2E-01：emg.stage = 1 起装了需要飞行的固件（寻的等）的机器武器发飞行弹体（LANDING #8），弹体飞进偏折场才掉头——等它飞回来（至多 3 秒、场仍展开）。
            bool fired = fr == CombatFireResult.Ok || fr == CombatFireResult.Launched; // 飞行弹体开火报“已发射”
            for (int i = 0; i < GameClock.StepHz * 3 && fired && Rec(m[0]).Health >= shooterHp; i++)
            {
                WorldSimulation.StepMany(1);
            }
            bool reflected = fired && site.Combat.Kernel.Counters.ShotsReflected > refl0 && Math.Abs(coreRec.Health - coreHp) < 1e-3f && Rec(m[0]).Health < shooterHp;
            Expect(warnAt > 0 && warn >= 1.5 - 1e-6 && reflected,
                $"C5 阶段二·磁暴（FGT-FAC-009 实测）：全场偏折场预警 {warn:0.00} 秒（规格 1.5；{warnDiag}）后展开；展开时机器的射弹被反弹（主脑 {coreHp:0}→{coreRec.Health:0} 不掉，开火的机器 {shooterHp:0}→{Rec(m[0]).Health:0}）" + (reflected ? string.Empty : $"（诊断：开火 {fr}、反弹 {site.Combat.Kernel.Counters.ShotsReflected - refl0}、弹体发射 {site.Combat.Kernel.Counters.ProjectilesSpawned}、命中 {site.Combat.Kernel.Counters.ProjectilesHit}、到期 {site.Combat.Kernel.Counters.ProjectilesExpired}、偏折场 {site.Combat.Kernel.ActiveDeflectCount}）"));

            // 打到阈值 → 全频静默预警（情报提前警告：字幕 + 通知 + HUD 倒计时）→ 静默 20 秒。
            int warnNotes0 = NotifyCount("silent_boss");
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, coreRec.MaxHealth * (1f - SilentMindBoss.Phase2HpFraction) + 1f);
            WorldSimulation.StepMany(1);
            // 绕过偏折场的伤害请求——若伤害被偏折场挡住会停在阶段二：这里直接结算伤害请求（内核扣血入口），只测阶段门槛。
            bool warned = SilentMindBoss.PhaseOf(s) == SilentMindPhase.SilenceWarn && NotifyCount("silent_boss") > warnNotes0;
            WorldView.Observe(Hall);
            SilentMindHudUIToolkit.Compose(s, out _, out _, out _, out _, out string warnLine);
            // 信号接入大厅里的一台机器（负向场景：机器全依赖核心固件——信号带进来的核心固件在静默时不生效）。
            ParkMachines(site, m);
            // 镜头放在家园（远距离接入不要求镜头先切过去，FG1-SIG-07）；不起镜头过渡——batchmode 里没有真实的直控镜头。
            WorldView.Observe(HomeValleyLayout.RegionId);
            UplinkRequestResult up = SignalUplinkService.Request(m[1], UplinkSource.MachineList, startCamera: false);
            for (int i = 0; i < 80 && SignalUplinkService.IsPending; i++)
            {
                _fakeNow += 0.05f;
                InputRouter.DebugClearConsumedKeys();
                WorldSimulation.Frame(0.05f);
            }
            bool uplinked = SignalUplinkService.CurrentMachine(s) == m[1];
            string upWhy = up.Accepted ? (uplinked ? "ok" : "pending:" + SignalUplinkService.LastFeedbackText) : up.Failure + ":" + up.Text;
            long silenceEnd = 0;
            for (int i = 0; i < GameClock.StepHz * 7 && SilentMindBoss.PhaseOf(s) != SilentMindPhase.Silence; i++)
            {
                WorldSimulation.StepMany(1);
            }
            silenceEnd = r.PhaseEndTick;
            bool silence = SilentMindBoss.PhaseOf(s) == SilentMindPhase.Silence && silenceEnd - GameClock.Ticks >= GameClock.TicksFor(19.9);
            bool broke = SignalUplinkService.CurrentMachine(s) != m[1];
            bool safe = m.All(id => SignalLinkService.IsInSafeMode(s, id) && SignalLinkService.SafeModeReason(s, id) == SignalLinkBreakReason.BossSilence);
            UplinkFailure refused = SignalUplinkService.Validate(s, m[0], out string refuseText);
            bool gatedOff = m.All(id => site.Combat.TryGetMachineUnit(id, out int u) && !site.Combat.UnitHasFlag(u, CombatUnitFlags.ReactionGated));
            bool wave = EnemyDefs.Encounters(Hall, EnemyDefs.ConditionBossWave).All(enc => SilentMindBoss.FindPart(s, enc.Id)?.IsAlive == true);
            float hpSilence = coreRec.Health;
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, 50f);
            bool invuln = Math.Abs(coreRec.Health - hpSilence) < 1e-3f;
            Expect(warned && warnLine.Contains(GameText.Format("boss.silent.hud.silence_warn", string.Empty).Substring(0, 4)) && uplinked && silence && broke && safe
                   && refused == UplinkFailure.Jammed && gatedOff && wave && invuln,
                $"C6 阶段三·全频静默（FGT-FAC-006；负向“机器全依赖核心固件”）：耐久到 {SilentMindBoss.Phase2HpFraction:P0} → 情报预警（通知 + 字幕 + HUD“{warnLine.Split('\n')[0]}”）{SilentMindBoss.SilenceWarnSeconds} 秒；" +
                $"静默开始：信号被强制弹出大厅（接入过 {uplinked}（{upWhy}）→ 断开 {broke}），大厅全部机器进入安全模式（原因“全频静默”，{safe}），再接入被拒“{refuseText}”，" +
                $"门控的核心固件一律不生效（只跑本地常规固件，{gatedOff}）；召来伏击波（{wave}），主脑这 20 秒不可伤（{invuln}）");

            // 撑过 20 秒 → 核心暴露（伤害 × 1.5）；静默解除后安全模式按规则退出、可以再接入。
            for (int i = 0; i < GameClock.StepHz * 22 && SilentMindBoss.PhaseOf(s) == SilentMindPhase.Silence; i++)
            {
                ParkMachines(site, m);
                WorldSimulation.StepMany(1);
            }
            double lasted = (GameClock.Ticks - (silenceEnd - GameClock.TicksFor(SilentMindBoss.SilenceSeconds))) / (double)GameClock.StepHz;
            bool exposed = SilentMindBoss.PhaseOf(s) == SilentMindPhase.Exposed;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            UplinkFailure after = SignalUplinkService.Validate(s, m[0], out _);
            float e0 = coreRec.Health;
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, 10f);
            bool mult = Math.Abs((e0 - coreRec.Health) - 10f * SilentMindBoss.ExposedDamageMult) < 1e-3f;
            Expect(exposed && Math.Abs(lasted - SilentMindBoss.SilenceSeconds) < 0.1 && after != UplinkFailure.Jammed && mult,
                $"C7 撑过全频静默（{lasted:0.00} 秒）→ 核心暴露（{exposed}），受伤 × {SilentMindBoss.ExposedDamageMult}（{mult}）；静默结束后可以再接入（{after}）");

            // 击败 → 奖励：集群协议 + 标记跳转（此前没拿到）+ 三选一数据包；领地平定 → 主线“监听阵列核”；剧情节点；报复突袭规则；情报不再破译这个首领。
            int packets0 = LootPickService.Count(s);
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, 99999f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool defeated = SilentMindBoss.IsDefeated(s);
            bool swarm = s.PrimitiveChips.Any(p => p.CardDefId == "fw_swarm" && p.Origin == PrimitiveInventory.OriginBoss);
            bool mark = s.PrimitiveChips.Any(p => p.CardDefId == "fw_marktag" && p.Origin == PrimitiveInventory.OriginBoss);
            bool packet = LootPickService.Count(s) == packets0 + 1 && LootPickService.Packets(s).Last().Source == LootPickService.SourceBoss;
            bool pacified = TerritoryControlService.Find(s, "silent")?.LeaderDefeated == true;
            KeyMaterial keyCore = QuestCatalog.KeyMaterialOf("listening_array_core");
            bool keyMat = keyCore != null && CampaignObjectiveTracker.HasKeyMaterial(s, keyCore);
            bool story = RaidDirectorService.StoryRuleMet(s, RaidCatalog.StoryRuleSilentMind);
            bool narrative = NarrativeService.Has(s, NarrativeCatalog.All.FirstOrDefault(en => en.Trigger == NarrativeCatalog.TriggerLeader + "silent")?.Id ?? "-");
            bool intelDone = IntelCatalog.TryGetBoss(IntelCatalog.BossIdSilentMind, out IntelBossDef bdef) && IntelService.BossDefeated(s, bdef);
            int grants = SilentMindBoss.RewardGrants;
            SilentMindBoss.ApplyDamage(s, coreRec, 100f);
            bool once = SilentMindBoss.RewardGrants == grants && s.PrimitiveChips.Count(p => p.CardDefId == "fw_swarm" && p.Origin == PrimitiveInventory.OriginBoss) == 1;
            Expect(defeated && swarm && mark && packet && pacified && keyMat && story && narrative && intelDone && once,
                $"C8 击败寂听主脑（FGR-FAC-006 奖励）：核心固件“集群协议” {swarm}，此前没有的“标记跳转” {mark}，三选一数据包 {packet}；静默领地平定 {pacified} → 主线关键材料“监听阵列核” {keyMat}；" +
                $"剧情节点 leader:silent {narrative}；静默报复突袭规则成立 {story}；监听站不再破译它 {intelDone}；奖励只发一次 {once}");
            SignalCoverageService.OverrideForTests = null;
        }

        private static string BossFingerprint(CampaignState s)
        {
            SilentBossRecord r = SilentMindBoss.Record(s);
            RegionEnemyRecord core = SilentMindBoss.Core(s);
            CombatSite site = CombatSites.Get(Hall);
            string deflect = "-";
            if (site != null && site.TryGetEnemyUnit(SilentMindBoss.CoreInstanceId, out int u) && site.TryGetUnitSilent(u, out CombatSilentUnit sl, out _))
            {
                deflect = sl.Deflect + ":" + sl.DeflectT.ToString("R");
            }
            string nodes = string.Join(",", Enumerable.Range(0, SilentMindBoss.Nodes.Count).Select(i => (SilentMindBoss.Node(s, i)?.Health ?? -1f).ToString("R")));
            return $"{r.Phase}|{r.RealNode}|{r.Jumps}|{r.JumpTarget}|{r.NextJumpTick}|{r.PhaseEndTick}|{core?.Health.ToString("R")}|{deflect}|{nodes}|{GameClock.Ticks}";
        }

        private static void CheckBossSaveLoad()
        {
            CampaignState s = NewWorld(1);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            ParkMachines(site, m);
            SilentBossRecord r = SilentMindBoss.Record(s);
            HitNode(site, (r.RealNode + 1) % 6, 20f);
            HitNode(site, r.RealNode, 1f);
            WorldSimulation.StepMany(GameClock.StepHz * 7);
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, 100f);
            WorldView.Observe(Hall);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            string saved = BossFingerprint(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string continued = BossFingerprint(s);
            CampaignState back = Reload(Slot);
            int resumed = InteriorSiteService.ResumeAll(back);
            string loaded = BossFingerprint(back);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string after = BossFingerprint(back);
            Expect(sv.Success && resumed == 1 && loaded == saved && after == continued && SilentMindBoss.PhaseOf(back) == SilentMindPhase.Magnetic,
                $"C9 FGT-FAC-003 首领战中存档：读档后阶段、真节点、节点耐久、主脑耐久、偏折场阶段与计时完全一致（{loaded == saved}），接着跑 3 秒与不存档逐位相同（{after == continued}）：{after}");
            SignalCoverageService.OverrideForTests = null;
        }

        private static void CheckBossWipeRetryAndExit()
        {
            CampaignState s = NewWorld(42);
            string slotFile = CampaignSaveService.SlotPath(Slot);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            string preFight = Path.Combine(_dir, "pre-fight.json");
            File.Copy(slotFile, preFight, true);
            SilentBossRecord r = SilentMindBoss.Record(s);
            HitNode(site, r.RealNode, 1f);
            WorldSimulation.StepMany(2);
            site.Combat.TryDamageEnemy(SilentMindBoss.CoreInstanceId, SilentMindBoss.Core(s).MaxHealth * 0.5f);
            for (int i = 0; i < GameClock.StepHz * 7 && SilentMindBoss.PhaseOf(s) != SilentMindPhase.Silence; i++)
            {
                WorldSimulation.StepMany(1);
            }
            bool inSilence = SilentMindBoss.PhaseOf(s) == SilentMindPhase.Silence;
            int notes0 = NotifyCount("silent_boss");
            foreach (int id in m)
            {
                if (site.Combat.TryGetMachineUnit(id, out int u))
                {
                    site.Combat.SetUnitHealth(u, 0f, 100f, false);
                }
                MachineRegistry.MarkDeadByLogicId(id);
            }
            WorldSimulation.StepMany(3);
            bool unloaded = WorldSimulation.Interior(Hall) == null;
            bool reset = SilentMindBoss.PhaseOf(s) == SilentMindPhase.Dormant && r.LastReachedPhase == (int)SilentMindPhase.Silence
                         && SilentMindBoss.Core(s).Health >= SilentMindBoss.Core(s).MaxHealth - 1e-3f && SilentMindBoss.Nodes.All(n => SilentMindBoss.FindPart(s, n.Id).IsAlive)
                         && EnemyDefs.Encounters(Hall, EnemyDefs.ConditionBossWave).All(enc => SilentMindBoss.FindPart(s, enc.Id) == null);
            string retryText = LatestDetail("silent_boss");
            bool retryNote = NotifyCount("silent_boss") > notes0 && retryText.Contains(SilentMindBoss.PhaseName(SilentMindPhase.Silence));
            bool preview = InteriorSiteService.BossPreview(s, Hall).Contains(SilentMindBoss.PhaseName(SilentMindPhase.Silence));
            // 首领战前自动存档：读它 = 回到入口外面、首领在休眠（重试）。
            File.Copy(preFight, CampaignSaveService.SlotPath(Slot), true);
            CampaignState retry = Reload(Slot);
            bool preState = retry != null && SilentMindBoss.PhaseOf(retry) == SilentMindPhase.Dormant
                            && m.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord rr) && rr.IsAlive && rr.RegionId == HomeValleyLayout.RegionId);
            Expect(inSilence && unloaded && reset && retryNote && preview && preState,
                $"C10 FGT-FAC-004 首领战全灭：大厅卸载（{unloaded}），首领复位到开战前（满血、伏击波撤走，{reset}）并记下“上次止步于{SilentMindBoss.PhaseName((SilentMindPhase)r.LastReachedPhase)}”；" +
                $"通知“{retryText}”（{retryNote}），入口说明同样写出（{preview}）；读首领战前的自动存档 → 机器在入口外、首领休眠（{preState}）");

            // 重新进入：第 2 次挑战，满血重开；全员撤出（不是全灭）同样复位。
            CampaignState s2 = retry;
            InteriorSiteController again = EnterHall(s2, 2, out int[] m2, out _);
            SilentBossRecord r2 = SilentMindBoss.Record(s2);
            bool second = SilentMindBoss.PhaseOf(s2) == SilentMindPhase.Hiding && r2.Attempt >= 1;
            HitNode(again, r2.RealNode, 1f);
            WorldSimulation.StepMany(2);
            foreach (int id in m2)
            {
                if (again.Combat.TryGetMachineUnit(id, out int u))
                {
                    again.Combat.SetUnitPosition(u, again.ExitPoint + new Vector2(0.3f * id % 2, 1f));
                }
            }
            WorldSimulation.StepMany(2);
            bool exited = InteriorSiteService.TryExit(s2, Hall, out string why);
            WorldSimulation.StepMany(3);
            bool exitReset = SilentMindBoss.PhaseOf(s2) == SilentMindPhase.Dormant && r2.LastReachedPhase == (int)SilentMindPhase.Magnetic;
            Expect(second && exited && exitReset,
                $"C11 再次进入重新开战（{second}）；打到阶段二后全员撤出（{exited}{(exited ? string.Empty : "：" + why)}）→ 首领同样复位，记下止步于阶段二（{exitReset}）——不跨出击磨血");
            SignalCoverageService.OverrideForTests = null;
        }

        private static string RunBoss(float speed, bool pauseHalf, bool observeHall)
        {
            CampaignState s = NewWorld(42);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            ParkMachines(site, m);
            WorldView.Observe(observeHall ? Hall : HomeValleyLayout.RegionId);
            long hitAt = GameClock.Ticks + GameClock.StepHz * 17;
            long target = GameClock.Ticks + GameClock.StepHz * 26;
            GameClock.SetSpeed(speed);
            bool hit = false;
            bool pausedOk = true;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 40000)
            {
                if (!hit && GameClock.Ticks >= hitAt)
                {
                    HitNode(site, SilentMindBoss.Record(s).RealNode, 1f);
                    hit = true;
                }
                if (pauseHalf && frames == 300)
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
                WorldSimulation.Frame(1f / 60f, hit ? target : hitAt);
                frames++;
            }
            GameClock.SetSpeed(1f);
            SignalCoverageService.OverrideForTests = null;
            return (pausedOk ? string.Empty : "PAUSE-MOVED ") + BossFingerprint(s);
        }

        private static void CheckBossSpeedMatrixAndObservation()
        {
            string r05 = RunBoss(0.5f, false, true);
            string r1 = RunBoss(1f, true, true);
            string r2 = RunBoss(2f, false, true);
            string r3 = RunBoss(3f, false, true);
            string unobserved = RunBoss(1f, false, false);
            Expect(r05 == r1 && r1 == r2 && r2 == r3 && !r1.Contains("PAUSE") && unobserved == r1,
                $"C12 B09 / FGR-BASE-021：寂听主脑 26 游戏秒（中继跳转、打中真节点、偏折场循环）在 0.5x / 1x（中途暂停 30 帧不走）/ 2x / 3x 下逐位相同，镜头不看大厅时结果也一样（{unobserved == r1}）：{r1}");
        }

        // ─────────────────────────────── D 电磁噪声 ───────────────────────────────

        private static void CheckNoise()
        {
            CampaignState s = NewWorld(42);
            IReadOnlyList<PlannedHazardZone> zones = EmNoiseService.Zones(s);
            PlannedHazardZone z = zones.FirstOrDefault();
            if (z == null)
            {
                Fail("D 夹具：种子 42 没有电磁噪声区");
                return;
            }
            var inside = new Vector2(z.CenterX, z.CenterY);
            float fIn = EmNoiseService.CoverageFactorAt(s, inside);
            float fOut = EmNoiseService.CoverageFactorAt(s, inside + new Vector2(z.Radius + 50f, 0f));
            // 内核逐机器评估：覆盖源半径 100，机器离源 80（0.8 倍）——噪声区里（× 0.7 = 70）在覆盖外，噪声区外在覆盖里。
            CombatSite bench = OpenBench(s);
            int inUnit = Player(bench, inside, Gun(1f));
            int outUnit = Player(bench, inside + new Vector2(z.Radius + 200f, 0f), Gun(1f));
            var sources = new List<float3> { new float3(inside.x - 80f, inside.y, 100f), new float3(inside.x + z.Radius + 200f - 80f, inside.y, 100f) };
            var changes = new List<int2>();
            bench.Kernel.EvaluateCoverage(sources, changes, EmNoiseService.ActiveCircles(s), EmNoiseService.CoverageMult);
            bool kernel = bench.Kernel.IsOutOfCoverage(inUnit) && !bench.Kernel.IsOutOfCoverage(outUnit);
            CombatSites.Close(Bench, s, dropRecord: true);
            Expect(zones.Count >= 1 && Math.Abs(fIn - 0.7f) < 1e-4f && Math.Abs(fOut - 1f) < 1e-4f && kernel,
                $"D1 电磁噪声（FG09 第 13 节 -30%）：种子 42 静默领地 {zones.Count} 片噪声区；区内覆盖倍率 {fIn}、区外 {fOut}；内核逐机器评估同一口径（离源 0.8 倍半径：噪声区里在覆盖外、区外在覆盖里，{kernel}）");

            // 噪声发射器：家园 / 星球内核里的敌方结构，摧毁 → 这一片噪声消失，进存档。
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            string key = EmNoiseService.EmitterKey(EmNoiseService.ZoneId(z));
            bool spawned = home != null && home.TryGetEnemyUnit(key, out int eu) && home.UnitExists(eu);
            string hoverBefore = EmNoiseService.HoverLine(s, z.CenterX, z.CenterY);
            int activeBefore = EmNoiseService.ActiveCircles(s).Count;
            home.TryDamageEnemy(key, 99999f);
            WorldSimulation.StepMany(2);
            bool destroyed = EmNoiseService.IsDestroyed(s, EmNoiseService.ZoneId(z)) && EmNoiseService.ActiveCircles(s).Count == activeBefore - 1
                             && Math.Abs(EmNoiseService.CoverageFactorAt(s, inside) - 1f) < 1e-4f;
            string hoverAfter = EmNoiseService.HoverLine(s, z.CenterX, z.CenterY);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            CombatSite home2 = CombatSites.Get(HomeValleyLayout.RegionId);
            bool persisted = sv.Success && EmNoiseService.IsDestroyed(back, EmNoiseService.ZoneId(z)) && !(home2 != null && home2.TryGetEnemyUnit(key, out int gone) && home2.IsUnitAlive(gone))
                             && EmNoiseService.ActiveCircles(back).Count == activeBefore - 1;
            Expect(spawned && destroyed && persisted && hoverBefore.Contains("30") && hoverAfter == GameText.Get("hazard.emi_noise.cleared"),
                $"D2 噪声发射器：每片噪声区中心有一座（星球内核里的敌方结构，{spawned}）；摧毁 → 这一片噪声消失（{destroyed}），悬停说明“{hoverBefore}”→“{hoverAfter}”；存读档后仍是已摧毁、不再生成（{persisted}）");
        }

        // ─────────────────────────────── E 监听节点 / 反制 / 攻城 ───────────────────────────────

        private static void CheckListenAndCounter()
        {
            CampaignState s = NewWorld(42);
            InteriorSiteController site = EnterHall(s, 2, out int[] m, out _);
            Vector2 node = SilentMindBoss.PartPosition(s, "rh_listen_node");
            float range = Row("enemy_listen_node").EffectRange;
            site.Combat.TryGetMachineUnit(m[0], out int u0);
            site.Combat.SetUnitPosition(u0, node + new Vector2(range * 0.5f, 0f));
            WorldSimulation.StepMany(1);
            float ampIn = SilentCounterService.ListenAmpAt(s, m[0]);
            float ampOut = SilentCounterService.ListenAmpAt(s, m[1]);
            float e0 = s.SignalExposure;
            CampaignExposureLedger.GrantCoreFire(s, "fw_marktag", false);
            SilentCounterService.OnCoreFired(s, "fw_marktag", false, m[1]);
            float dOut = s.SignalExposure - e0;
            float e1 = s.SignalExposure;
            CampaignExposureLedger.GrantCoreFire(s, "fw_marktag", false);
            SilentCounterService.OnCoreFired(s, "fw_marktag", false, m[0]);
            float dIn = s.SignalExposure - e1;
            Expect(Math.Abs(ampIn - 2f) < 1e-4f && Math.Abs(ampOut - 1f) < 1e-4f && dOut > 0f && Math.Abs(dIn - 2f * dOut) < 1e-4f,
                $"E1 监听节点：范围（{range} 米）里的机器发动核心固件，暴露翻倍（范围外 +{dOut:0.##}，范围里 +{dIn:0.##}）");

            // 反制：前一天核心固件发动 ≥ 门槛 → 次日锁定“监听加强”；当天之内不变；开战时大厅按它追加监听节点与寂听者。
            CampaignFgStateDomains.EnsureAll(s);
            FactionState f = s.Factions;
            int day = s.Clock.Day;
            f.CoreFireDay = day;
            f.CoreFiresToday = 8;
            f.SilentCounterDay = day;
            s.Clock.Day = day + 1;
            SilentCounterService.WorldStep(s);
            string locked = SilentCounterService.Locked(s);
            f.CoreFiresYesterday = 0;
            string sameDay = SilentCounterService.Locked(s);
            string preview = SilentCounterService.Preview(s);
            // 玩家可见路径：监听站产出的“敌方反制预览”情报（情报面板同一行文本）+ 大厅入口面板 / 悬停说明。
            IntelRecord counterIntel = IntelService.ProduceForTests(s, IntelCatalog.KindCounter);
            string intelText = counterIntel == null ? string.Empty : IntelService.Summary(s, counterIntel, GameClock.Ticks);
            string entrance = InteriorSiteService.BossPreview(s, Hall);
            bool intelShows = counterIntel != null && counterIntel.HasSilentCounter && counterIntel.SilentCounter == locked && counterIntel.SilentCounterNext == preview
                              && intelText.Contains(InteriorSiteService.SurfaceName(Hall)) && intelText.Contains(SilentCounterService.CounterName(locked))
                              && intelText.Contains(SilentCounterService.CounterName(preview))
                              && entrance.Contains(GameText.Format("boss.silent.entrance.counter", SilentCounterService.CounterName(locked)));
            s.Clock.Day = day;
            // E3 夹具：让“今天”锁定的对策是监听加强（前一天 8 次核心固件），然后重新进大厅开战。
            f.CoreFireDay = day - 1;
            f.CoreFiresToday = 8;
            f.SilentCounterDay = 0;
            Expect(locked == EnemyDefs.ConditionCounterListen && sameDay == locked && preview != null && preview != locked && intelShows,
                $"E2 FGR-FAC-004 静默反制每日锁定：前一天核心固件发动 8 次 → 次日锁定“{SilentCounterService.CounterName(locked)}”，当天之内不再变（{sameDay == locked}）；" +
                $"情报面板的反制预览写出当前反制与下一次日切的预测（{intelShows}：“{intelText}”），大厅入口面板写出今天的反制");

            SilentMindBoss.ResetToPreFight(s, false);
            foreach (int id in m)
            {
                if (site.Combat.TryGetMachineUnit(id, out int u))
                {
                    site.Combat.SetUnitPosition(u, site.ExitPoint + new Vector2(0.5f, 1f));
                }
            }
            InteriorSiteService.TryExit(s, Hall, out _);
            WorldSimulation.StepMany(3);
            InteriorSiteController again = EnterHall(s, 1, out _, out _);
            string counterNow = SilentMindBoss.Record(s).CounterId;
            bool counterSeeded = counterNow == EnemyDefs.ConditionCounterListen
                                 && EnemyDefs.Encounters(Hall, EnemyDefs.ConditionCounterListen).All(enc => SilentMindBoss.FindPart(s, enc.Id)?.IsAlive == true);
            string counterNotice = GameText.Format("boss.silent.notify.counter", SilentCounterService.CounterName(EnemyDefs.ConditionCounterListen));
            bool told = NotificationCenter.History.Any(n => n.Members.Any(mm => mm.DetailText.Contains(counterNotice)));
            Expect(again != null && counterSeeded && told,
                $"E3 开战时按当天锁定的反制追加编成：监听加强 → 大厅多出监听节点与寂听者（{counterSeeded}，锁定“{counterNow}”）；开战通知写明加派的原因（{told}）");

            // 负向（复审 P2）：复位时反制追加的编成一并撤走——换一天锁定别的反制再开战，上一次的反制单位不会留在大厅里累积。
            SilentMindBoss.ResetToPreFight(s, false);
            bool counterCleared = EnemyDefs.Encounters(Hall, EnemyDefs.ConditionCounterListen).All(enc => SilentMindBoss.FindPart(s, enc.Id) == null);
            Expect(counterCleared, $"E3b 首领复位后反制追加的编成全部撤走（下次开战按那天锁定的反制重新播种，不会跨次累积）：{counterCleared}");

            // 攻城：磁暴棱柱进突袭时同样带偏折场（DEBT-FG6DEF05-04 的静默部分）、回声猎手带潜伏参数。
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            RaidUnitDef prismDef = RaidCatalog.Units.FirstOrDefault(d => d.EnemyTypeId == "enemy_prism");
            RaidUnitDef hunterDef = RaidCatalog.Units.FirstOrDefault(d => d.EnemyTypeId == "enemy_echo_hunter");
            int pu = SiegeService.SpawnOne(home, 990001, new Vector2(10f, 10f), Vector2.zero, prismDef, false);
            int hu = SiegeService.SpawnOne(home, 990001, new Vector2(12f, 10f), Vector2.zero, hunterDef, false);
            home.TryGetUnitSilent(pu, out _, out CombatBehaviorProfile pp);
            home.TryGetUnitSilent(hu, out _, out CombatBehaviorProfile hp);
            // 干扰机：攻城中自带干扰场（家园里场内接入被拒，FG09 第 4 章“干扰机压制信号塔一带”）；击毁即解除。
            RaidUnitDef jamDef = RaidCatalog.Units.FirstOrDefault(d => d.EnemyTypeId == "enemy_jammer");
            var jamAt = new Vector2(40f, 40f);
            bool before = SignalLinkService.IsJammedAt(s, HomeValleyLayout.RegionId, jamAt + new Vector2(2f, 0f));
            int ju = SiegeService.SpawnOne(home, 990002, jamAt, Vector2.zero, jamDef, false);
            float jr = Row("enemy_jammer").EffectRange;
            bool inField = SignalLinkService.IsJammedAt(s, HomeValleyLayout.RegionId, jamAt + new Vector2(jr * 0.5f, 0f));
            bool outField = SignalLinkService.IsJammedAt(s, HomeValleyLayout.RegionId, jamAt + new Vector2(jr * 1.5f, 0f));
            home.SetUnitHealth(ju, 0f, 100f, false);
            bool cleared = !SignalLinkService.IsJammedAt(s, HomeValleyLayout.RegionId, jamAt + new Vector2(jr * 0.5f, 0f));
            Expect(prismDef != null && pu > 0 && home.UnitHasFlag(pu, CombatUnitFlags.DeflectArmed) && pp.DeflectRadius > 0f && hp.LurkSeconds > 0f
                   && jamDef != null && !before && inField && !outField && cleared,
                $"E4 攻城中的静默单位带专属语法（DEBT-FG6DEF05-04 静默部分）：磁暴棱柱偏折场（半径 {pp.DeflectRadius}，已上电）、回声猎手潜伏参数（{hp.LurkSeconds} 秒）、" +
                $"干扰机自带干扰场（半径 {jr}：场内接入被拒 {inField}、场外不受影响 {!outField}、击毁即解除 {cleared}）——与区域敌人同一读法、同一套规则");
            SignalCoverageService.OverrideForTests = null;
        }

        /// <summary>
        /// E4b（复审 P1，FGR-FAC-008“移动或开火时展开现形”、① ≥ 0.8 秒预警、FGT-FAC-009）：突袭 / 行进拦截里的回声猎手（攻城单位、突袭者行为）
        /// 原地静止进入潜伏后，玩家机器走进射程——先现形（展桅预警），预警满了才第一次出手；潜伏状态下一发都不打。
        /// </summary>
        private static void CheckRaidLurkReveal()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            RaidUnitDef hunterDef = RaidCatalog.Units.FirstOrDefault(d => d.EnemyTypeId == "enemy_echo_hunter");
            var at = new Vector2(0f, 0f);
            int hunter = hunterDef == null ? 0 : SiegeService.SpawnOne(site, 990003, at, at, hunterDef, false);
            site.TryGetUnitSilent(hunter, out _, out CombatBehaviorProfile hp);
            RunFor(site, hp.LurkSeconds + 0.5f);
            bool lurking = View(site, hunter).Flags.HasFlag(CombatUnitFlags.Stealthed);
            int victim = Player(site, at + new Vector2(2.5f, 0f), Gun(0.01f));
            float h0 = View(site, victim).Health;
            double enter = _t;
            double revealAt = -1;
            double firstHit = -1;
            bool firedFromStealth = false;
            for (int i = 0; i < 600 && firstHit < 0; i++)
            {
                RunFor(site, Dt);
                bool stealthed = View(site, hunter).Flags.HasFlag(CombatUnitFlags.Stealthed);
                if (revealAt < 0 && !stealthed)
                {
                    revealAt = _t;
                }
                if (View(site, victim).Health < h0)
                {
                    firstHit = _t;
                    firedFromStealth = stealthed || revealAt < 0;
                }
            }
            double warn = firstHit - enter;
            float need = Row("enemy_echo_hunter").WarnSeconds;
            Expect(hunter > 0 && hp.LurkSeconds > 0f && lurking && revealAt > 0 && firstHit > 0 && !firedFromStealth && revealAt <= firstHit && warn >= 0.8 && warn >= need,
                $"E4b 负向（复审 P1）：突袭 / 拦截交战里的回声猎手（突袭者行为）静止 {hp.LurkSeconds} 秒潜伏（{lurking}）后，机器走进射程 → 先现形（{revealAt - enter:0.00} 秒）、" +
                $"展桅 {need} 秒后才第一次掉血（{warn:0.00} 秒 ≥ 0.8）；潜伏中开火 {firedFromStealth}");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        // ─────────────────────────────── F 伏笔地点 ───────────────────────────────

        private static void CheckForeshadow()
        {
            CampaignState s = NewWorld(1);
            InteriorSiteController site = EnterHall(s, 1, out int[] m, out _);
            InteriorPoint wall = RelayHallLayout.Points(Hall).First(p => p.Kind == RelayHallLayout.KindPoi);
            RelayHallLayout.TryPoint(s, wall.Id, out Vector2 at);
            int count0 = NarrativeService.Count(s);
            site.Combat.TryGetMachineUnit(m[0], out int u);
            site.Combat.SetUnitPosition(u, at + new Vector2(1f, 0f));
            WorldSimulation.StepMany(3);
            bool readPoi = Array.IndexOf(s.Factions.PoisRead, wall.Id) >= 0;
            bool read = readPoi && NarrativeService.Has(s, "poi." + wall.Id) && NarrativeService.Count(s) == count0 + 1;
            WorldSimulation.StepMany(3);
            bool once = NarrativeService.Count(s) == count0 + 1;
            TerritoryTemplate tpl = WorldGenService.PlanFor(s).TemplateFor("silent");
            bool lore = tpl != null && (ConfigSystem.Instance.Tables.TbTerritoryTemplate.DataList.First(t => t.TerritoryId == "silent").TerminalLoreKeys ?? string.Empty).Contains("terminal.lore.silent.d");
            Expect(read && once && lore,
                $"F1 伏笔（FG09 第 4 章 / FGR-GEN-024 手工锚点）：机器走近大厅的天空扫描墙 → 读取一次（{readPoi}）、收进资料库（{read}），不重复（{once}）；静默领地的旧终端日志里有“天上的眼睛”（{lore}）");
            SignalCoverageService.OverrideForTests = null;
        }

        // ─────────────────────────────── G 性能 ───────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(1);
            CombatSite site = OpenBench(s);
            _t = 0;
            for (int i = 0; i < 200; i++)
            {
                string type = (i % 3) == 0 ? "enemy_prism" : (i % 3) == 1 ? "enemy_echo_hunter" : "enemy_listener";
                BenchEnemy(site, s, type, "p" + i, new Vector2((i % 20) * 6f, (i / 20) * 6f));
            }
            for (int i = 0; i < 40; i++)
            {
                Player(site, new Vector2((i % 10) * 12f, 80f + (i / 10) * 4f), Gun(1f), CombatUnitFlags.None, 1e6f);
            }
            RunFor(site, 1f);
            var sw = Stopwatch.StartNew();
            int steps = 120;
            RunFor(site, steps * Dt);
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            CombatSites.Close(Bench, s, dropRecord: true);

            CampaignState w = NewWorld(42);
            var sw2 = Stopwatch.StartNew();
            int n = 20000;
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += EmNoiseService.CoverageFactorAt(w, new Vector2(i % 3000, i / 7));
            }
            sw2.Stop();
            double us = sw2.Elapsed.TotalMilliseconds * 1000.0 / n;
            PerfGate.Expect(acc > 0f, $"G1 性能（Editor batchmode，Mono JIT，一次测量；真机 IL2CPP + HybridCLR 另测 FG15-SYS-02）：200 个静默单位（潜伏 / 偏折场 / 断链束各三分之一）+ 40 台机器的内核一步 {perStep:F3} ms；" +
                                     $"电磁噪声覆盖倍率单点 {us:F3} 微秒",
                new[] { new PerfGate.Metric("静默单位内核一步(ms)", perStep, 4.0, false), new PerfGate.Metric("噪声倍率单点(微秒)", us, 2.0, false) }, Expect, Line);
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
            CampaignState s = CampaignState.CreateNew("fgsilent-" + seed, "Standard", seed);
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
            EmNoiseService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgsilent-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
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
            WorldView.Observe(HomeValleyLayout.RegionId);
            return r.State;
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static int SpawnIn(CampaignState s, string region, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, region, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static string LatestDetail(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e.Type != null && e.Type.Id == typeId)?.Latest?.DetailText ?? string.Empty;

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgSilentFactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
