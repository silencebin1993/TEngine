using System;
using System.Collections.Generic;
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
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
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
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG11-REACT-01 超频反应开放（FG19 FGR-EMG-085 / 086 / 190 / 192、FG19B §4.1 / §4.2、FG02 FGR-FW-043；FGT-EMG-030 / 032 扩展 / 062）：
    /// M 酸沼电解限速（FG-GAP-228，E16“每块每秒最多 1 次”）：生产函数持续注电 4 秒 = 4 次、门槛以下 0 次、两块沼各自计；20 发 / 秒的电武器打沼 1 秒 1 次；暂停 0 次、0.5x～3x 都是 4 次；
    ///   快照段 35 往返（两种读档顺序、哈希一致、冷却接续）、格式 21 旧档 = 没有登记；改写酸沼时同一组形状保留冷却、换形状不继承。
    /// S 有源审计（超频内容下四条各侧来源；构造的无源端口必须被报出）。
    /// O 超频内容下四条各一组“只有一种配料不发生”：电解（含斑块限速在超频热源下仍成立）、玻化、回路溶断、连锁锈蚀——真实地点 + 真实超频敌人（EnemySpawnBuilder）+ 真实规则表。
    /// C 图鉴条目写主结果与两侧端口类别；超频敌人的程序（读写端口）在图鉴文本与击杀悬停一致（FGR-EMG-162）；首次发现只通知一次；同一步大量触发弹字聚合。
    /// D DEBT-EMG2REACT02-01：熔穿过载的核心冷却按接入口装配里写 en.heat 注入的那条记（信号上行门控 / 宿主结算）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgOverclockReactionSelfCheck）；单段：bash tools/unity-validate.sh --segment FgOverclockReactionSelfCheck。
    /// </summary>
    public static class FgOverclockReactionSelfCheck
    {
        private const int Slot = 7;
        private const string Bench = "fg11react01-bench";
        private const float Dt = 1f / 60f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/FG 超频反应开放")]
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
            Line("\n[超频反应开放] 酸沼电解限速、超频内容下电解 / 玻化 / 回路溶断 / 连锁锈蚀有源可触发、图鉴与悬停、首次发现、弹字聚合、核心冷却按装配（FG11-REACT-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgocreact-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                EmgPhaseCatalog.Reload();
                EmgFieldCatalog.Reload();
                NamedReactionCatalog.Reload();
                FirmwareKinds.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.ResetContentForTests();

                Step(CheckMarshDirect);
                Step(CheckMarshWeapon);
                Step(CheckMarshPauseSpeed);
                Step(CheckMarshSaveLoad);
                Step(CheckMarshReshape);
                Step(CheckSourceAudit);
                Step(CheckOcElectrolysis);
                Step(CheckOcVitrify);
                Step(CheckOcWreckRules);
                Step(CheckCodexAndHover);
                Step(CheckPopupAggregate);
                Step(CheckCoreCooldownAssembly);
            }
            finally
            {
                EmgCatalog.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
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
            Line(_fail == 0 ? $"  超频反应开放：全部 {_pass} 项通过" : $"  超频反应开放：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── 夹具 ───────────────────────────────

        private static int Key(string id) => NamedReactionCatalog.StableKey(id);

        private static readonly float4 MarshA = new float4(0f, 0f, 0f, 10f);
        private static readonly float4 MarshB = new float4(100f, 0f, 0f, 10f);

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyContent(k);
            return k;
        }

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static int Elec(CombatKernel k) => k.EmgRuleCountOfKey(Key("reaction_electrolysis"));

        /// <summary>持续往沼里注电 <paramref name="steps"/> 步（每步注一次 <paramref name="amount"/>，步长 <paramref name="dt"/>），返回电解次数增量。</summary>
        private static int Pour(CombatKernel k, double2 at, float amount, int steps, float dt = Dt)
        {
            int c0 = Elec(k);
            for (int i = 0; i < steps; i++)
            {
                k.InjectMarshElecForTests(at, amount);
                k.Step(dt, k.Time + dt);
            }
            return Elec(k) - c0;
        }

        /// <summary>命中注电 <paramref name="amount"/>（半径 <paramref name="radius"/>）的瞬发武器程序。</summary>
        private static int ElecProgram(CombatKernel k, float amount, float radius)
        {
            var instrs = new List<EmgInstr>
            {
                new EmgInstr { Port = EmgConst.PortElec, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = amount, Param2 = radius },
            };
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
            return k.AddProgram(new EmgProgram(), nodes, instrs);
        }

        private static CombatWeapon ElecGun(int program, float range = 40f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = 1f, Cooldown = 0.05f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatUnitKind kind)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = f, Behavior = kind == CombatUnitKind.Machine ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = (weapon >= 0 ? Live : CombatUnitFlags.Alive | CombatUnitFlags.Targetable) | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0),
                Position = p, Home = p, Radius = 0.5f, Speed = kind == CombatUnitKind.Machine ? 4f : 0f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
                ArmorHalfAngleDeg = 90f, ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });
            return id;
        }

        /// <summary>20 发 / 秒（每 3 步一发）打 <paramref name="seconds"/> 秒，返回电解增量。</summary>
        private static int Burst(CombatKernel k, int shooter, int target, float seconds)
        {
            int c0 = Elec(k);
            int n = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                if (i % 3 == 0)
                {
                    k.FireAt(shooter, target, k.Time);
                }
                k.Step(Dt, k.Time + Dt);
            }
            return Elec(k) - c0;
        }

        // ─────────────────────────────── M 酸沼电解限速 ───────────────────────────────

        private static void CheckMarshDirect()
        {
            Line("  · M1. FG-GAP-228：酸沼（虚拟世界源）按 E16“每块每秒最多 1 次”电解；冷却中照样导电，只是不析出");
            using CombatKernel k = NewKernel();
            k.SetAcidMarsh(new[] { MarshA, MarshB }, 0.02f, 1f);
            StepN(k, 2);
            int foe = Unit(k, CombatFaction.Hostile, new double2(3, 0), -1, 400f, CombatUnitKind.Enemy);
            k.SetUnitProfile(foe, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
            k.InjectMarshElecForTests(new double2(0, 0), 0.3f); // 门槛以下：只导电，不电解、不登记冷却
            float charge = k.AccumOf(foe, EmgPhaseConst.Charge);
            int four = Pour(k, new double2(0, 0), 1f, 240);
            bool timer = k.TryGetMarshElecAt(MarshA, out double at) && at > 0;
            Expect(four == 4 && timer && charge > 0f && k.PatchCount > 0,
                $"持续往沼里注电 4 秒（每步一次、共 240 次）：电解 {four} 次（应 4，FG19B:358 E16）；上次电解时刻登记 {timer}（{at:0.###} 秒）；沼里 8 米内的敌人得电荷 {charge:0.###}（导电不受限速）；碎屑斑块 {k.PatchCount} 块");
            int below = Pour(k, new double2(0, 0), 0.3f, 120);
            int outside = Pour(k, new double2(50, 0), 1f, 120);
            Expect(below == 0 && outside == 0, $"负向：注电 0.3（< E16 门槛 0.4）2 秒电解 {below} 次；沼外注电 {outside} 次（都应 0）");
            using CombatKernel two = NewKernel();
            two.SetAcidMarsh(new[] { MarshA, MarshB }, 0.02f, 1f);
            StepN(two, 2);
            int c0 = Elec(two);
            for (int i = 0; i < 60; i++)
            {
                two.InjectMarshElecForTests(new double2(0, 0), 1f);
                two.InjectMarshElecForTests(new double2(100, 0), 1f);
                two.InjectMarshElecForTests(new double2(5, 0), 1f);
                StepN(two, 1);
            }
            Expect(Elec(two) - c0 == 2 && two.MarshElecCount == 2,
                $"两块沼同一秒各注电 60 次（A 块两个注入点）：电解 {Elec(two) - c0} 次（应 2：每块各 1、同一块的两个注入点共用冷却），登记 {two.MarshElecCount} 块");
        }

        private static void CheckMarshWeapon()
        {
            Line("  · M2. 正式路径：20 发 / 秒的电武器（命中注电 1.0）打沼里的目标——1 秒内最多 1 次（FG-GAP-228 卡片断言）");
            using CombatKernel k = NewKernel();
            k.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
            int gun = k.AddWeapon(ElecGun(ElecProgram(k, 1f, 0.5f)));
            int me = Unit(k, CombatFaction.Player, new double2(0, -15), gun, 9999f, CombatUnitKind.Machine);
            int dummy = Unit(k, CombatFaction.Hostile, new double2(0, 0), -1, 99999f, CombatUnitKind.Structure);
            StepN(k, 2);
            int one = Burst(k, me, dummy, 1f);
            int more = Burst(k, me, dummy, 1.5f);
            Expect(one == 1 && more == 2, $"20 发 / 秒打沼里的目标：第 1 秒电解 {one} 次（应 1）、之后 1.5 秒 {more} 次（应 2）");
            using CombatKernel dry = NewKernel();
            int gun2 = dry.AddWeapon(ElecGun(ElecProgram(dry, 1f, 0.5f)));
            int me2 = Unit(dry, CombatFaction.Player, new double2(0, -15), gun2, 9999f, CombatUnitKind.Machine);
            int dummy2 = Unit(dry, CombatFaction.Hostile, new double2(0, 0), -1, 99999f, CombatUnitKind.Structure);
            StepN(dry, 2);
            int none = Burst(dry, me2, dummy2, 2f);
            Expect(none == 0, $"对照：没有酸沼时同一把电武器打 2 秒电解 {none} 次（只有电、没有酸不发生）");
        }

        private static void CheckMarshPauseSpeed()
        {
            Line("  · M3. 暂停与 0.5x～3x：冷却按游戏秒");
            using (CombatKernel k = NewKernel())
            {
                k.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
                StepN(k, 2);
                int first = Pour(k, new double2(0, 0), 1f, 1);
                ulong h0 = k.StateHash();
                int paused = Pour(k, new double2(0, 0), 1f, 600, 0f);
                Expect(first == 1 && paused == 0 && k.StateHash() == h0, $"暂停（0 秒步）注电 600 次：电解 {paused} 次、状态哈希不变（{k.StateHash() == h0}）");
            }
            var got = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                k.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
                StepN(k, 2);
                float dt = Dt * speed;
                int n = Mathf.RoundToInt(4f / dt);
                int c = Pour(k, new double2(0, 0), 1f, n, dt);
                got.Add($"{speed}x {c}");
                ok &= c == 4;
            }
            Expect(ok, $"持续注电 4 游戏秒：{string.Join(" / ", got)} 次（都应 4）");
        }

        private static void CheckMarshSaveLoad()
        {
            Line("  · M4. 新内核状态进快照（段 35、FormatVersion 22）：冷却中途存档，读回哈希一致、冷却接续；旧档没有登记");
            using CombatKernel a = NewKernel();
            a.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
            StepN(a, 2);
            int first = Pour(a, new double2(0, 0), 1f, 1);
            StepN(a, 29);
            byte[] snap = a.Serialize();
            ulong h = a.StateHash();
            a.TryGetMarshElecAt(MarshA, out double atA);
            using CombatKernel b = NewKernel();
            b.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f); // 正式顺序之一：热更层先写酸沼再读档
            CombatLoadResult lb = b.Load(snap);
            using CombatKernel c = NewKernel();
            CombatLoadResult lc = c.Load(snap);
            c.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f); // 另一种顺序：读档后再写同一组形状（冷却保留）
            bool restored = b.TryGetMarshElecAt(MarshA, out double atB) && c.TryGetMarshElecAt(MarshA, out double atC) && atB == atA && atC == atA;
            Expect(first == 1 && lb == CombatLoadResult.Ok && lc == CombatLoadResult.Ok && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 22
                   && b.StateHash() == h && restored,
                $"冷却中途（距上次电解 0.5 秒）存档：格式 {CombatKernel.PeekFormat(snap)}、两种读档顺序都 Ok（{lb} / {lc}）、哈希一致 {b.StateHash() == h}、上次电解时刻 {atA:0.###} 读回一致 {restored}");
            int ca = Pour(a, new double2(0, 0), 1f, 20), cb = Pour(b, new double2(0, 0), 1f, 20), cc = Pour(c, new double2(0, 0), 1f, 20);
            int na = Pour(a, new double2(0, 0), 1f, 20), nb = Pour(b, new double2(0, 0), 1f, 20);
            Expect(ca == 0 && cb == 0 && cc == 0 && na == 1 && nb == 1 && a.StateHash() == b.StateHash(),
                $"读回后冷却接续：剩余冷却内注电 原 / 读回 / 读回后再写形状 = {ca} / {cb} / {cc} 次（都应 0），冷却过后 {na} / {nb} 次（都应 1），两边哈希一致 {a.StateHash() == b.StateHash()}");
            byte[] old = a.SerializeFormatForTests(21);
            using CombatKernel d = NewKernel();
            d.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
            CombatLoadResult ld = d.Load(old);
            int fresh = Pour(d, new double2(0, 0), 1f, 1);
            Expect(ld == CombatLoadResult.Ok && CombatKernel.PeekFormat(old) == 21 && d.MarshElecCount == 1 && fresh == 1,
                $"格式 21 旧档读入 {ld}：没有登记，读回后第一次注电立即电解（{fresh} 次）");
        }

        private static void CheckMarshReshape()
        {
            Line("  · M5. 热更层改写酸沼：同一组形状保留冷却，换了形状不继承（只登记现存的形状）");
            using CombatKernel k = NewKernel();
            k.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
            StepN(k, 2);
            Pour(k, new double2(0, 0), 1f, 1);
            k.SetAcidMarsh(new[] { MarshA }, 0.02f, 1f);
            int same = Pour(k, new double2(0, 0), 1f, 10);
            var moved = new float4(0f, 0f, 0f, 12f);
            k.SetAcidMarsh(new[] { moved }, 0.02f, 1f);
            int kept = k.MarshElecCount;
            int fresh = Pour(k, new double2(0, 0), 1f, 1);
            Expect(same == 0 && kept == 0 && fresh == 1 && k.MarshElecCount == 1,
                $"重写同一组形状后冷却内注电 {same} 次（应 0）；换成新形状后旧登记清掉（剩 {kept}）、新沼立即可电解（{fresh} 次）");
        }

        // ─────────────────────────────── S 有源审计 ───────────────────────────────

        private static readonly (string Rule, string Named, string A, string B)[] Four =
        {
            ("E16", "reaction_electrolysis", "mat.acid", "en.elec"),
            ("E04", "reaction_vitrify", "mat.dust", "en.heat"),
            ("W01", "reaction_hemolysis", "mat.acid", "ent.wreck"),
            ("W02", "reaction_sepsis", "mat.sludge", "ent.wreck"),
        };

        private static bool IsOverclockOwner(EmgSource.Decl d) =>
            d.OwnerKind == "enemy" ? EnemyDefs.TryGet(d.Owner, out MechEnemy e) && e.Faction == EnemyDefs.FactionOverclock
            : MechanicalContentFacade.TryGet(d.Owner, out MechanicalContentDef m) && m.Faction == EnemyDefs.FactionOverclock;

        /// <summary>端口在正常游戏里的写者（生效、可获得的声明）；实体端口由内核阵亡统一入口产生（FGR-EMG-113），物质端口另认世界常驻源。</summary>
        private static List<EmgSource.Decl> WritersOf(EmgSource src, string port, int stage) =>
            src.Decls.Where(d => d != null && d.Dir == "write" && d.Port == port && d.StageGate <= stage && (d.OwnerKind != "firmware" || FirmwareKinds.IsObtainable(d.Owner))).ToList();

        private static bool Sourced(EmgSource src, string port, int stage) =>
            port == "ent.wreck" || WritersOf(src, port, stage).Count > 0 || FgEmergenceFuzzSelfCheck.WorldSources.ContainsKey(port);

        private static void CheckSourceAudit()
        {
            Line("  · S. 有源审计（FGR-EMG-015 规则三 / FGR-EMG-192）：四条在正常游戏里两侧都有源；超频内容新带来的端口源逐条列出；构造的无源端口必须被报出");
            EmgSource src = EmgCatalog.FromTables();
            int stage = EmgCatalog.Current.EffectiveStage;
            var rows = ConfigSystem.Instance.Tables.TbChannelRule.DataList;
            var unsourced = new List<string>();
            var report = new List<string>();
            foreach ((string rule, string named, string a, string b) in Four)
            {
                ChannelRule row = rows.FirstOrDefault(r => r.Named == named && r.A == a && r.B == b);
                if (row == null)
                {
                    unsourced.Add($"{rule} 规则行缺失");
                    continue;
                }
                foreach (string p in new[] { a, b })
                {
                    if (!Sourced(src, p, stage))
                    {
                        unsourced.Add($"{rule}:{p}");
                    }
                }
                var oc = WritersOf(src, a, stage).Concat(WritersOf(src, b, stage)).Where(IsOverclockOwner).Select(d => d.Owner + "→" + d.Port).Distinct().ToList();
                report.Add($"{rule} {named}：超频源 {(oc.Count > 0 ? string.Join("、", oc) : b == "ent.wreck" ? "超频单位阵亡留残骸（O 段实测）" : "无（FG19B §4.1 来源举例未列超频，靠玩家固件 / 世界源）")}");
            }
            Expect(unsourced.Count == 0, $"四条两侧都有正常游戏来源（{(unsourced.Count == 0 ? "无缺口" : "无源：" + string.Join("、", unsourced))}）：" + string.Join("；", report));
            bool heatFromOc = WritersOf(src, "en.heat", stage).Any(IsOverclockOwner);
            Expect(heatFromOc, "超频阵营带来新热源（en.heat 写端口的超频拥有者），玻化的热侧在超频领地有本阵营来源");
            Expect(!Sourced(src, "mat.__fg11react01_none", stage), "负向：构造的无源物质端口被报出（审计本身能失败）");
        }

        // ─────────────────────────────── O 超频内容下四条 ───────────────────────────────

        private sealed class BenchRules : CombatSiteRules
        {
        }

        private static double _t;

        private static CampaignState NewWorldState(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            NotificationCenter.ResetForTests();
            ReactionDiscoveryService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgocreact-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(3, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CombatSite OpenBench(CampaignState s)
        {
            CombatSites.Close(Bench, s, dropRecord: true);
            _t = 0;
            return CombatSites.Open(Bench, s, false, new BenchRules(), out _);
        }

        private static int OcEnemy(CombatSite site, CampaignState s, string typeId, string id, Vector2 at)
        {
            if (!EnemyDefs.TryGet(typeId, out MechEnemy row))
            {
                Fail("夹具：没有敌人行 " + typeId);
                return 0;
            }
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = id, RegionId = Bench, EnemyTypeId = typeId, Position = at, Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != id).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(Bench), out CombatSpawn sp))
            {
                Fail("夹具：生成 " + typeId + " 失败");
                return 0;
            }
            site.SpawnEnemy(rec, sp);
            return site.TryGetEnemyUnit(id, out int u) ? u : 0;
        }

        private static int Player(CombatSite site, Vector2 at, CombatWeapon? wp, float hp, CombatUnitKind kind)
        {
            int w = wp.HasValue ? site.WeaponIndex(wp.Value) : -1;
            return site.Kernel.Spawn(new CombatSpawn
            {
                Kind = kind, Faction = CombatFaction.Player, Behavior = kind == CombatUnitKind.Machine ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (wp.HasValue ? CombatUnitFlags.WeaponEnabled : 0), Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
                Radius = 0.6f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = w, BehaviorProfile = -1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
        }

        private static void RunFor(CombatSite site, float seconds, Action perStep = null)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 0; i < n; i++)
            {
                perStep?.Invoke();
                _t += Dt;
                site.Step(Dt, _t);
            }
        }

        private static int Count(CombatSite site, string id) => site.Kernel.EmgRuleCountOfKey(Key(id));

        private static int Patches(CombatSite site, byte matter)
        {
            int n = 0;
            for (int i = 0; i < site.Kernel.PatchSlots; i++)
            {
                if (site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    n++;
                }
            }
            return n;
        }

        private static float MaxTemp(CombatSite site, byte matter)
        {
            float t = float.NegativeInfinity;
            for (int i = 0; i < site.Kernel.PatchSlots; i++)
            {
                if (site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    t = Mathf.Max(t, z.Temp);
                }
            }
            return t;
        }

        private static int Hostile(CombatSite site, Vector2 at, CombatUnitKind kind = CombatUnitKind.Structure) =>
            site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Report,
                Position = new double2(at.x, at.y), Home = new double2(at.x, at.y), Radius = 0.5f, Health = 99999f, MaxHealth = 99999f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
            });

        /// <summary>命中注能（端口 <paramref name="port"/>、幅度 <paramref name="amount"/>、半径 <paramref name="radius"/>）的玩家武器，登记进地点的程序表。</summary>
        private static CombatWeapon InjectGun(CombatSite site, ushort port, float amount, float radius)
        {
            int prog = site.Kernel.AddProgram(new EmgProgram(), new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } },
                new List<EmgInstr> { new EmgInstr { Port = port, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = amount, Param2 = radius } });
            CombatWeapon gun = ElecGun(prog);
            gun.Damage = 0.01f;
            return gun;
        }

        private static void CheckOcElectrolysis()
        {
            Line("  · O1. 超频内容下电解（E16）：超频领地里（过热炮台在场开火）玩家电武器 20 发 / 秒把电带进酸斑块——4 秒最多 4 次（斑块限速在超频内容下仍成立，FGT-EMG-032 扩展）；只有一种配料不发生");
            int Scenario(bool acid, bool elec, out bool turretFired)
            {
                CampaignState s = NewWorldState(11);
                CombatSite site = OpenBench(s);
                if (acid)
                {
                    site.Kernel.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 1.5f, 5f, 60f);
                }
                int decoy = Player(site, new Vector2(0f, 0f), null, 99999f, CombatUnitKind.Structure); // 超频炮台的靶子站在酸斑块中心
                OcEnemy(site, s, "enemy_oc_turret", "r1_turret", new Vector2(3f, 9f));
                int me = Player(site, new Vector2(0f, -20f), InjectGun(site, EmgConst.PortElec, elec ? 1f : 0f, 3.5f), 99999f, CombatUnitKind.Machine);
                int target = Hostile(site, new Vector2(2.5f, 0f)); // 电武器的靶子在酸斑块外 2.5 米：只靠注电半径把电带进斑块，不沾酸（排除沾染层 C08）
                RunFor(site, 0.5f);
                float hp0 = site.Kernel.TryGetUnit(decoy, out CombatUnitView v0) ? v0.Health : 0f;
                int c0 = Count(site, "reaction_electrolysis");
                int i = 0;
                RunFor(site, 4f, () =>
                {
                    if (i++ % 3 == 0)
                    {
                        site.Kernel.FireAt(me, target, site.Kernel.Time);
                    }
                });
                turretFired = site.Kernel.TryGetUnit(decoy, out CombatUnitView v1) && v1.Health < hp0;
                int got = Count(site, "reaction_electrolysis") - c0;
                CombatSites.Close(Bench, s, dropRecord: true);
                return got;
            }
            int both = Scenario(true, true, out bool fired);
            int onlyAcid = Scenario(true, false, out _);
            int onlyElec = Scenario(false, true, out _);
            Expect(both >= 3 && both <= 4 && fired, $"酸斑块 + 20 发 / 秒注电（超频过热炮台同时在打酸里的靶子：{fired}）：4 秒电解 {both} 次（应 3～4，每块每秒最多 1 次）");
            Expect(onlyAcid == 0 && onlyElec == 0, $"对照：只有酸、没有电 {onlyAcid} 次；只有电、没有酸 {onlyElec} 次（都应 0）");
        }

        private static void CheckOcVitrify()
        {
            Line("  · O2. 超频内容下玻化（E04 粉尘 × en.heat）：玩家命中注热落在超频敌人脚下的粉尘上 → 玻璃；只有一种配料不发生；首次发现只通知一次");
            int Scenario(bool dust, bool heat, out string diag, out CampaignState state)
            {
                CampaignState s = NewWorldState(12);
                state = s;
                CombatSite site = OpenBench(s);
                if (dust)
                {
                    site.Kernel.SpawnPatch(EmgMatterConst.Dust, new double2(0, 0), 2f, 0.4f, 60f);
                }
                int turret = OcEnemy(site, s, "enemy_oc_turret", "v_turret", new Vector2(0f, 0f));
                int me = Player(site, new Vector2(0f, -12f), InjectGun(site, EmgConst.PortHeat, heat ? 60f : 0f, 3f), 99999f, CombatUnitKind.Machine);
                float DustConc()
                {
                    float c = 0f;
                    for (int i = 0; i < site.Kernel.PatchSlots; i++)
                    {
                        if (site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Dust)
                        {
                            c = Mathf.Max(c, z.Conc);
                        }
                    }
                    return c;
                }
                // 扬尘先沉降（浓度 ≥ 0.6 时遇热是尘爆，E04 只认稀薄的尘，FG19B:215）；没有粉尘时同样等这么久
                for (int w = 0; w < 60 * 20 && (!dust || DustConc() >= 0.6f); w++)
                {
                    RunFor(site, Dt);
                }
                float conc = DustConc();
                int c0 = Count(site, "reaction_vitrify");
                float maxT = float.NegativeInfinity;
                int n = 0;
                RunFor(site, 4f, () =>
                {
                    if (n++ % 30 == 0)
                    {
                        site.Kernel.FireAt(me, turret, site.Kernel.Time);
                    }
                    maxT = Mathf.Max(maxT, MaxTemp(site, EmgMatterConst.Dust));
                });
                int got = Count(site, "reaction_vitrify") - c0;
                diag = $"粉尘浓度 {conc:0.##}、最高温 {maxT:0.#} 度、玻璃 {Patches(site, EmgMatterConst.Glass)} 块";
                CombatSites.Close(Bench, s, dropRecord: true);
                return got;
            }
            int posted0 = ReactionDiscoveryService.Posted;
            int both = Scenario(true, true, out string d1, out CampaignState s1);
            int posted1 = ReactionDiscoveryService.Posted;
            int firsts = s1.ReactionFirstTriggers?.Count(x => x.ReactionId == "reaction_vitrify") ?? 0;
            int onlyDust = Scenario(true, false, out string d2, out _);
            int onlyHeat = Scenario(false, true, out _, out _);
            Expect(both > 0, $"粉尘里的超频过热炮台挨玩家注热（每 0.5 秒一发、每发 60 热量单位）：4 秒玻化 {both} 次（{d1}）");
            Expect(onlyDust == 0 && onlyHeat == 0, $"对照：只有粉尘、没有热 {onlyDust} 次（{d2}）；只有热、没有粉尘 {onlyHeat} 次");
            Expect(firsts == 1 && posted1 > posted0, $"首次发现：本存档玻化首次记录 {firsts} 条（多次触发只记 1 条）、“新发现”通知 +{posted1 - posted0}");
            CheckOcBurstHeat();
        }

        /// <summary>超频阵营自己的热源：过热泄爆（猎犬死亡 / 熔断者到点）按声明 en.heat inject 往半径内单位与斑块注热。</summary>
        private static void CheckOcBurstHeat()
        {
            Line("  · O2b. 超频阵营自带热源（FGR-EMG-192）：超频猎犬死亡泄爆按声明写 en.heat——半径内斑块升温（稀薄粉尘玻化）、单位积热；没有粉尘不玻化");
            int Scenario(bool dust, out float buddyHeat, out float burstHeat, out bool snapOk)
            {
                CampaignState s = NewWorldState(17);
                CombatSite site = OpenBench(s);
                if (dust)
                {
                    site.Kernel.SpawnPatch(EmgMatterConst.Dust, new double2(0, 0), 2f, 0.4f, 60f);
                }
                int hound = OcEnemy(site, s, "enemy_oc_hound", "b_hound", new Vector2(0f, 0f));
                int buddy = Hostile(site, new Vector2(1.5f, 0f), CombatUnitKind.Enemy);
                site.Kernel.SetUnitProfile(buddy, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
                int killer = Player(site, new Vector2(0f, -40f), null, 99999f, CombatUnitKind.Machine);
                site.Kernel.TryGetProfileOf(hound, out CombatBehaviorProfile prof);
                burstHeat = prof.OcBurstHeat;
                byte[] snap = site.Kernel.Serialize();
                using (CombatKernel copy = NewKernel())
                {
                    snapOk = copy.Load(snap) == CombatLoadResult.Ok && copy.TryGetProfileOf(hound, out CombatBehaviorProfile p2) && p2.OcBurstHeat == prof.OcBurstHeat;
                }
                for (int w = 0; w < 60 * 20 && dust && MaxConc(site) >= 0.6f; w++)
                {
                    RunFor(site, Dt); // 猎犬会冲向玩家：先把它按在原地（冲刺目标 40 米外，沉降期间它也许跑开，下面按住位置再击毁）
                    site.Kernel.SetPosition(hound, new double2(0, 0));
                }
                int c0 = Count(site, "reaction_vitrify");
                float h0 = site.Kernel.AccumOf(buddy, EmgPhaseConst.Overheat);
                site.Kernel.SetPosition(hound, new double2(0, 0));
                site.Kernel.Kill(hound, killer);
                RunFor(site, 0.5f);
                buddyHeat = site.Kernel.AccumOf(buddy, EmgPhaseConst.Overheat) - h0;
                RunFor(site, 1.5f);
                int got = Count(site, "reaction_vitrify") - c0;
                CombatSites.Close(Bench, s, dropRecord: true);
                return got;
            }
            int withDust = Scenario(true, out float heatA, out float q, out bool snapOk);
            int noDust = Scenario(false, out _, out _, out _);
            Expect(q >= 60f - 1e-3f && snapOk, $"猎犬的泄爆注热取自它的 en.heat inject 声明：{q:0.#} 热量单位；进快照（格式 22 行为参数行尾部）读回一致 {snapOk}");
            Expect(withDust > 0 && heatA > 0f && noDust == 0,
                $"粉尘上击毁超频猎犬：泄爆把粉尘烧成玻璃 {withDust} 次、1.5 米内的友军积热 +{heatA:0.###}（热爆波及友军）；对照：没有粉尘时 {noDust} 次");
        }

        private static float MaxConc(CombatSite site)
        {
            float c = 0f;
            for (int i = 0; i < site.Kernel.PatchSlots; i++)
            {
                if (site.Kernel.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Dust)
                {
                    c = Mathf.Max(c, z.Conc);
                }
            }
            return c;
        }

        private static void CheckOcWreckRules()
        {
            Line("  · O3. 超频内容下回路溶断（W01 酸 × 残骸）与连锁锈蚀（W02 污泥 × 残骸）：超频单位阵亡留下的残骸即可（FG19B W01 / W02）；只有一种配料不发生；第二次触发不再通知");
            int Scenario(string reaction, byte matter, bool liquid, bool wreck, out int wrecks)
            {
                CampaignState s = NewWorldState(13);
                CombatSite site = OpenBench(s);
                if (liquid)
                {
                    site.Kernel.SpawnPatch(matter, new double2(0, 0), 3f, 20f, 60f);
                }
                int killer = Player(site, new Vector2(0f, -30f), null, 99999f, CombatUnitKind.Machine);
                int fuser = OcEnemy(site, s, "enemy_oc_fuser", "w_fuser", new Vector2(0f, 0f));
                int node = OcEnemy(site, s, "enemy_oc_node", "w_node", new Vector2(1f, 1f));
                int w0 = site.Kernel.WreckCount;
                if (wreck)
                {
                    site.Kernel.Kill(fuser, killer);
                    site.Kernel.Kill(node, killer);
                }
                int c0 = Count(site, reaction);
                RunFor(site, 0.2f);
                wrecks = site.Kernel.WreckCount - w0;
                RunFor(site, 6f);
                int got = Count(site, reaction) - c0;
                CombatSites.Close(Bench, s, dropRecord: true);
                return got;
            }
            foreach ((string id, byte matter, string name) in new[] { ("reaction_hemolysis", EmgMatterConst.Acid, "酸"), ("reaction_sepsis", EmgMatterConst.Sludge, "污泥") })
            {
                int posted0 = ReactionDiscoveryService.Posted;
                int both = Scenario(id, matter, true, true, out int wrecks);
                int onlyLiquid = Scenario(id, matter, true, false, out int noWreck);
                int onlyWreck = Scenario(id, matter, false, true, out _);
                Expect(wrecks >= 1 && both >= 1, $"{name}斑块里击毁超频熔断者 + 算力节点（留下残骸 {wrecks} 具）：6 秒内 {NamedReactionCatalog.NameOf(id)} {both} 次");
                Expect(onlyLiquid == 0 && noWreck == 0 && onlyWreck == 0, $"对照：{name}里的超频单位活着（没有残骸 {noWreck}）{onlyLiquid} 次；残骸在{name}外 {onlyWreck} 次（都应 0）");
                _ = posted0;
            }
            // 首次发现只通知一次：同一存档里先后两次溶断
            CampaignState st = NewWorldState(14);
            CombatSite bench = OpenBench(st);
            bench.Kernel.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 3f, 20f, 120f);
            int k1 = Player(bench, new Vector2(0f, -30f), null, 99999f, CombatUnitKind.Machine);
            bench.Kernel.Kill(OcEnemy(bench, st, "enemy_oc_fuser", "d_fuser1", new Vector2(0f, 0f)), k1);
            RunFor(bench, 6f);
            int afterFirst = ReactionDiscoveryService.Posted, c1 = Count(bench, "reaction_hemolysis");
            bench.Kernel.Kill(OcEnemy(bench, st, "enemy_oc_fuser", "d_fuser2", new Vector2(1f, 0f)), k1);
            RunFor(bench, 6f);
            int c2 = Count(bench, "reaction_hemolysis");
            int recs = st.ReactionFirstTriggers?.Count(x => x.ReactionId == "reaction_hemolysis") ?? 0;
            Expect(c1 >= 1 && c2 > c1 && ReactionDiscoveryService.Posted == afterFirst && recs == 1,
                $"同一存档两次回路溶断（计数 {c1} → {c2}）：首次记录 {recs} 条，第二次“新发现”通知 +{ReactionDiscoveryService.Posted - afterFirst}（应 0）");
            CombatSites.Close(Bench, st, dropRecord: true);
        }

        // ─────────────────────────────── C 图鉴 / 悬停 / 弹字 ───────────────────────────────

        private static void CheckCodexAndHover()
        {
            Line("  · C1. 图鉴：四条写主结果与两侧端口（端口名 + 通道类别）；C2. FGR-EMG-162：超频敌人的程序在图鉴文本与击杀悬停一致");
            CampaignState s = NewWorldState(15);
            var bad = new List<string>();
            string resultHead = GameText.Get("codex.combo.result").Split('{')[0];
            foreach ((string rule, string named, string a, string b) in Four)
            {
                ReactionFeedback.RecordFirst(s, named, Bench);
                string body = MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.ReactionEntryId(named)), s) ?? string.Empty;
                string ca = MechanicCodex.ChannelName(a), cb = MechanicCodex.ChannelName(b);
                if (!body.Contains(resultHead) || !body.Contains(ca) || !body.Contains(cb) || GameText.ContainsMarker(body))
                {
                    bad.Add($"{named}（主结果 {body.Contains(resultHead)}、{ca} {body.Contains(ca)}、{cb} {body.Contains(cb)}）");
                }
            }
            string sample = MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.ReactionEntryId("reaction_hemolysis")), s) ?? string.Empty;
            string sampleLine = sample.Split('\n').FirstOrDefault(l => l.Contains(GameText.Get("codex.combo.ports").Split('{')[0])) ?? "?";
            Expect(bad.Count == 0, $"四条图鉴正文都有主结果与两侧端口类别（{(bad.Count == 0 ? "例：" + sampleLine.Trim() : "缺：" + string.Join("、", bad))}）");

            var missing = new List<string>();
            string heat = GameText.Get("emg.port.en.heat.name");
            foreach (string id in new[] { "enemy_oc_hound", "enemy_oc_node", "enemy_oc_fuser", "enemy_oc_turret", "enemy_oc_mirror", "enemy_oc_runaway" })
            {
                EnemyDefs.TryGet(id, out MechEnemy row);
                string text = CombatSite.EnemyProgramText(row);
                bool writesHeat = EmgCatalog.OwnerDeclaresWrite(id, "en.heat");
                if (text == null || GameText.ContainsMarker(text) || (writesHeat && !text.Contains(heat)))
                {
                    missing.Add(id);
                }
            }
            CombatSite site = OpenBench(s);
            int u = OcEnemy(site, s, "enemy_oc_hound", "c_hound", new Vector2(0f, 0f));
            EnemyDefs.TryGet("enemy_oc_hound", out MechEnemy hound);
            string hover = site.EnemyProgramLine(s, u);
            Expect(missing.Count == 0 && hover != null && hover == CombatSite.EnemyProgramText(hound) && hover.Contains(heat),
                $"6 种超频敌人的程序文本有读写端口、写热的都列出“{heat}”（缺 {missing.Count}：{string.Join(",", missing)}）；击杀悬停与图鉴同一份文本（{hover?.Replace("\n", " / ")}）");
            CombatSites.Close(Bench, s, dropRecord: true);
        }

        private static void CheckPopupAggregate()
        {
            Line("  · C3. 负向“同一步大量触发”：弹字聚合（FGR-FW-043 沿用），不刷屏");
            ReactionPopups.ResetForTests();
            string name = NamedReactionCatalog.NameOf("reaction_hemolysis");
            for (int i = 0; i < 12; i++)
            {
                ReactionPopups.Push("reaction_hemolysis", name, 1, new Vector3(i * 0.1f, 0f, 0f), false, false, 10f);
            }
            ReactionPopups.Popup p = ReactionPopups.Active.FirstOrDefault(x => x.Key == "reaction_hemolysis");
            Expect(ReactionPopups.Active.Count == 1 && p != null && p.Count == 12 && ReactionPopups.MergedCount == 11 && p.Text.Contains("12"),
                $"同一步 12 次回路溶断：弹字 {ReactionPopups.Active.Count} 条“{p?.Text}”（合并 {ReactionPopups.MergedCount} 次）");
            ReactionPopups.ResetForTests();
        }

        // ─────────────────────────────── D 核心冷却按装配 ───────────────────────────────

        private static void CheckCoreCooldownAssembly()
        {
            Line("  · D. DEBT-EMG2REACT02-01：熔穿过载的核心冷却记在接入口装配里写 en.heat 注入的那条固件上（不再固定算过载）");
            string melt = MechanicalReactionCatalog.ReactionMeltOverloadId;
            string overload = FirmwareCatalog.FwOverloadId;
            string other = FirmwareCatalog.All.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault(id => id != overload && FirmwareKinds.IsCore(id) && FirmwareKinds.CoreCooldownSeconds(id) > 0f
                && EmgCatalog.PartWriteAmount("en.heat", EmgRole.Inject, new[] { id }) <= 0f && EmgCatalog.Current.OwnerIndex.ContainsKey(id));
            if (other == null)
            {
                Fail("夹具：没有可用的另一条带冷却的核心固件");
                return;
            }
            bool before = SignalUplinkService.IsCoreGatedReaction(melt, new[] { other });
            EmgSource src = EmgCatalog.FromTables();
            EmgSource.Decl anyOfOther = src.Decls.First(d => d != null && d.Owner == other);
            src.Decls.Add(new EmgSource.Decl
            {
                Id = "fg11react01_test_heat", Owner = other, OwnerKind = "firmware", Dir = "write", Port = "en.heat", Role = "inject", Amount = 45f, StageGate = anyOfOther.StageGate,
                Seq = src.Decls.Max(d => d?.Seq ?? 0) + 1, Carriers = anyOfOther.Carriers, TextKey = anyOfOther.TextKey, Legacy = "none",
            });
            EmgCatalog.OverrideForTests(src);
            try
            {
                bool compiled = EmgCatalog.Current.Problems.Count == 0 && EmgCatalog.PartWriteAmount("en.heat", EmgRole.Inject, new[] { other }) > 0f;
                string viaOther = MechanicalReactionCatalog.TriggerFirmwareOf(melt, new[] { other });
                string viaNone = MechanicalReactionCatalog.TriggerFirmwareOf(melt, null);
                bool gated = SignalUplinkService.IsCoreGatedReaction(melt, new[] { other });
                bool gatedOv = SignalUplinkService.IsCoreGatedReaction(melt, new[] { overload });
                CampaignState s = NewWorldState(16);
                SignalUplinkService.OnHostReactionFired(s, melt, new[] { other });
                double cdOther = SignalUplinkService.CooldownRemaining(s, other), cdOv = SignalUplinkService.CooldownRemaining(s, overload);
                Expect(compiled && viaOther == other && viaNone == overload && !before && gated && gatedOv && cdOther > 0 && cdOv == 0,
                    $"测试表让核心固件 {other} 也写 en.heat 注入（编译 {compiled}）：冷却归属 {viaOther}（没给装配 = {viaNone}）；接入口只有它时门控 {before} → {gated}、只有过载时 {gatedOv}；" +
                    $"宿主结算后 {other} 冷却 {cdOther} 秒、过载 {cdOv} 秒（冷却记在装配里写热的那条）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Expect(!SignalUplinkService.IsCoreGatedReaction(melt, new[] { other }) && SignalUplinkService.IsCoreGatedReaction(melt, new[] { overload }),
                "还原真实表：正式内容里写 en.heat 注入的核心固件只有过载，现有装配结果不变（只有带过载的接入口才门控）");
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgOverclockReactionSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
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
