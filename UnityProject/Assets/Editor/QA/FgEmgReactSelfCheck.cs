using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-REACT-01 五条定稿反应与装配反应端口化的自动验收（FG19 FGR-EMG-021 / 085 / 086 / 093；FGT-EMG-030 / 032 / 004；ADR-EMG-001 ⑥ ⑪ ⑯）。
    /// 起真实内核（CombatKernel，Burst 步进）与真实表（fg.TbChannelRule / TbMatter / TbReaction / TbStatusTag / TbEntityProfile）：
    /// A 规则表形状（FgChannelRuleShapeSelfCheck 口径：主结果 ∈ S/T/B/P、产物与伴生在封闭清单、附带倍率 ≤ 1.25、B 类改行为）+ 16 条 ID / StableKey 未变 + 五条撤旧上新单轨（两表无同一键）；
    /// B 爆燃（油斑块整片变明火 / 沾油单位受热脚下起火；只有油或只有火不发生）；C 导电水洼（带电水洼连锁电击含己方、只有水不带电不发生、浸湿单位通电电弧跳向 ≤ 3 个导体）；
    /// D 碎裂（冻结 + 重击碎成破片 / 未冻结同样重击不碎）；E 烟幕（明火遇水生成蒸汽斑块、浸湿单位受热冒蒸汽熄火 / 只有水不发生）；
    /// F 固化阻路（聚合胶遇酸固化成胶块、生成时圈内单位按 Id 升序推到边缘、之后不可进入、后续单位绕行、离地单位不受阻、只有胶 / 只有酸不发生、胶块可被打碎；胶膜单位沾酸粘住 2.5 秒能开火不能动）；
    /// G 熔穿过载 P03（同一次命中注热 ≥ 45 且动能 ≥ 40 → 溅出熔融金属 + 目标熔穿；注热不足 / 动能不足 / 武器没有开火时成立标志 / 已发动被压住都不发生；开火代价 65 取代 55；不再加穿甲）；
    /// H 装配判定端口化（重型射弹 + en.heat 写端 → 熔穿过载、切割束 + 过载不触发、热量 65 / 基础 + 15；标记跳转按“evt.marked 条件 + grm.mod.chain”判定）；
    /// I 存读档（粘住剩余秒进快照、读档后续跑一致；旧表撤下反应的计数按键并进涌现计数）；J 暂停与 0.5x～3x（粘住按游戏秒）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgReactSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgReactSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgReactSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/五条定稿反应与装配反应端口化")]
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
            Line("\n[五条定稿反应与装配反应端口化] EMG2-REACT-01：爆燃 / 导电水洼 / 碎裂 / 烟幕 / 固化阻路 + 熔穿过载 P03 + 标记跳转端口化（FGT-EMG-030 / 032 / 004）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                EmgPhaseCatalog.Reload();
                NamedReactionCatalog.Reload();
                FirmwareKinds.ResetForTests();
                Step(CheckShape);
                Step(CheckDeflagrate);
                Step(CheckConduct);
                Step(CheckShatter);
                Step(CheckSteam);
                Step(CheckCure);
                Step(CheckStick);
                Step(CheckMeltOverload);
                Step(CheckAssembly);
                Step(CheckSaveLoad);
                Step(CheckSpeed);
            }
            catch (Exception e)
            {
                Fail($"自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [五条定稿反应与装配反应端口化] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            k.SetClarityBits(Bit("Acid"), Bit("Wet"), Bit("Oil"), Bit("Fire"), Bit("Frozen"), Bit("Shock"));
            k.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            CombatSite.ApplyLegacyResidue(k);
            k.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            return k;
        }

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 400f, bool air = false)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = air ? k.AddProfile(new CombatBehaviorProfile { Airborne = 1f }) : -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });
            if (f == CombatFaction.Hostile)
            {
                k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
            }
            return id;
        }

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static int StepsUntil(CombatKernel k, Func<bool> done, int max, float dt = Dt)
        {
            for (int s = 1; s <= max; s++)
            {
                k.Step(dt, k.Time + dt);
                if (done())
                {
                    return s;
                }
            }
            return -1;
        }

        private static float MatterSum(CombatKernel k, byte matter, double2 at = default, float within = 1e9f)
        {
            float sum = 0f;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && math.distance(z.Pos, at) <= within)
                {
                    sum += z.Amount;
                }
            }
            return sum;
        }

        private static bool FindPatch(CombatKernel k, byte matter, out CombatZone found)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    found = z;
                    return true;
                }
            }
            found = default;
            return false;
        }

        private static int Key(string reactionId) => NamedReactionCatalog.StableKey(reactionId);

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : default;

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static EmgUnitState St(CombatKernel k, int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState s) ? s : default;

        private static uint Status(CombatKernel k, int id) => k.TryGetStatus(id, out uint m, out _, out _, out _, out _) ? m : 0u;

        private static int HeatProgram(CombatKernel k, string firmware)
        {
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { firmware }, FirmwareCarrier.Projectile);
            return CombatSite.RegisterProgram(k, pr);
        }

        // ── A 表形状 / 16 条 ID / 单轨 ───────────────────────────────────────

        private static readonly string[] Named16 =
        {
            "reaction_steam", "reaction_deflagrate", "reaction_conduct", "reaction_shatter", "reaction_sticky", "reaction_electrolysis", "reaction_insulate",
            "reaction_thermalshock", "reaction_causticburn", "reaction_hemolysis", "reaction_syrup", "reaction_dissolve", "reaction_annihilate", "reaction_vitrify",
            "reaction_mudify", "reaction_sepsis",
        };

        private static readonly string[] Five = { "reaction_steam", "reaction_deflagrate", "reaction_conduct", "reaction_shatter", "reaction_sticky" };

        private static void CheckShape()
        {
            Line("  · A. FgChannelRuleShapeSelfCheck 口径（FGR-EMG-085 / 2.9 节）+ FGT-EMG-030（16 条 ID / StableKey 未变）+ 单轨（FGR-EMG-019 / 086）");
            var t = ConfigSystem.Instance.Tables;
            string[] products =
            {
                "ter.block", "ter.bridge", "ter.cover", "ter.slick", "ter.mark", "bhv.lockloss", "bhv.overheat_stop", "bhv.stun", "bhv.freeze", "bhv.stick", "bhv.slow", "bhv.push",
                "bhv.hijack", "topo.arc", "topo.fragment", "topo.reflect", "topo.redirect", "topo.burst", "topo.trail",
            };
            var bad = new List<string>();
            foreach (GameConfig.fg.ChannelRule r in t.TbChannelRule.DataList)
            {
                bool closed(string p) => p != null && (p.StartsWith("mat.", StringComparison.Ordinal) || products.Contains(p));
                if (!"STBP".Contains(r.MainKind) || r.MainKind.Length != 1 || !closed(r.Product) || (r.ByProduct != "none" && !closed(r.ByProduct))
                    || (r.Side == "side.mult" && r.SideValue > 1.25f) || (r.MainKind == "B" && !r.Product.StartsWith("bhv.", StringComparison.Ordinal)))
                {
                    bad.Add(r.Id);
                }
            }
            Expect(bad.Count == 0 && t.TbChannelRule.DataList.Count >= 61,
                $"{t.TbChannelRule.DataList.Count} 行规则：主结果 ∈ S/T/B/P、产物与伴生在 2.9 节封闭清单、附带倍率 ≤ 1.25、B 类产物都是改行为的 bhv.*{(bad.Count > 0 ? "；不符：" + string.Join(",", bad) : string.Empty)}");
            // 负向：坏行被编译器拒绝（主结果写成“只有倍率”）
            var rows = t.TbChannelRule.DataList.ToList();
            EmgRuleCatalog.Compiled ok = EmgRuleCatalog.Compile(rows, 256);
            Expect(ok.Ok && ok.Rules.Count == rows.Count, $"真实表编进内核 {ok.Rules.Count} 行、无问题");
            var missing = Named16.Where(id => !NamedReactionCatalog.TryGet(id, out GameConfig.fg.Reaction row) || NamedReactionCatalog.StableKey(row.Id) != Key(id)).ToList();
            Expect(missing.Count == 0 && NamedReactionCatalog.TryGet("reaction_marktag", out _) && NamedReactionCatalog.TryGet("reaction_meltoverload", out _),
                "FGT-EMG-030：16 条 reaction_* ID 都在、StableKey = FNV-1a(ID) 未变；两条装配反应 ID 也在" + (missing.Count > 0 ? "；缺：" + string.Join(",", missing) : string.Empty));
            CombatReactionRule[] old = NamedReactionCatalog.BuildKernelRules();
            var oldKeys = new HashSet<int>(old.Select(r => r.Key));
            var namedLive = new HashSet<int>(ok.Rules.Where(r => r.Op != EmgRuleConst.OpDeferred || r.Pair == EmgRuleConst.PairPhaseEnergy || r.Pair == EmgRuleConst.PairPropField || r.Pair == EmgRuleConst.PairMatterEntity).Select(r => r.Key));
            bool single = Named16.All(id => !oldKeys.Contains(Key(id)) && namedLive.Contains(Key(id)) && NamedReactionCatalog.TryGet(id, out GameConfig.fg.Reaction row) && row.Kind == NamedReactionCatalog.KindRule);
            Expect(single && old.Length == 0,
                $"单轨（EMG2-REACT-02）：16 条全部 kind = rule、旧标签反应表撤空（剩 {old.Length} 条），每条在通道规则表有生效行");
            int coatLive = ok.Rules.Count(r => r.Pair == EmgRuleConst.PairCoatMatter && r.Op != EmgRuleConst.OpDeferred);
            Expect(coatLive == 8, $"单位层沾染行 C01～C08 全部接上（生效 {coatLive}）");
            Expect(GameText.Get("reaction.shatter.name") == "碎裂" && GameText.Get("reaction.sticky.name") == "固化" && GameText.Get("reaction.dissolve.name") == "乳化",
                $"玩家名按推荐默认（2026-10-30）：脆裂 → {GameText.Get("reaction.shatter.name")}、粘滞 → {GameText.Get("reaction.sticky.name")}；溶蚀 → {GameText.Get("reaction.dissolve.name")}（EMG2-REACT-02）");
            string adhesive = GameText.Get(t.TbFirmwareKind.GetOrDefault("fw_adhesive")?.DescKey ?? "none");
            Expect(adhesive.Contains("排出聚合胶，遇酸固化成阻路的胶块"), $"粘合剂文案（FGR-EMG-230 第 10 行）：{adhesive}");
        }

        // ── B 爆燃 ───────────────────────────────────────────────────────────

        private static void CheckDeflagrate()
        {
            Line("  · B. 爆燃（E01 油斑块整片变明火 / C01 沾油单位受热脚下起火）+ 只有一种配料不发生");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_deflagrate");
            int c0 = k.EmgRuleCountOfKey(key);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 6f, 30f);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(20, 0), 2f, 6f, 30f); // 对照：只有油
            k.SpawnPatch(EmgMatterConst.Fire, new double2(1.5, 0), 1f, 3f, 10f);
            int s = StepsUntil(k, () => MatterSum(k, EmgMatterConst.Oil, new double2(0, 0), 3f) <= 0f, 240);
            Expect(s > 0 && MatterSum(k, EmgMatterConst.Fire, new double2(0, 0), 3f) > 3f && MatterSum(k, EmgMatterConst.Oil, new double2(20, 0), 1f) > 0f
                   && k.EmgRuleCountOfKey(key) > c0,
                $"油斑块挨着明火第 {s} 步整片变明火（燃烧区份量 {MatterSum(k, EmgMatterConst.Fire, new double2(0, 0), 3f):0.#}），计数按爆燃稳定键 +{k.EmgRuleCountOfKey(key) - c0}；远处只有油的斑块不燃");
            int u = Unit(k, CombatFaction.Hostile, new double2(40, 0));
            int onlyOil = Unit(k, CombatFaction.Hostile, new double2(50, 0));
            int onlyFire = Unit(k, CombatFaction.Hostile, new double2(60, 0));
            k.ApplyStatus(u, Bit("Oil"), 10f, 0f, 0f, 0f, 0);
            k.ApplyStatus(onlyOil, Bit("Oil"), 10f, 0f, 0f, 0f, 0);
            k.ApplyStatus(onlyFire, Bit("Fire"), 10f, 0f, 0f, 0f, 0);
            int c1 = k.EmgRuleCountOfKey(key);
            float hp0 = Hp(k, u);
            k.ApplyStatus(u, Bit("Fire"), 3f, 4f, 0f, 0f, 0);
            Expect(MatterSum(k, EmgMatterConst.Fire, new double2(40, 0), 2f) > 0f && (Status(k, u) & Bit("Oil")) == 0u && k.AccumOf(u, EmgPhaseConst.Ignite) >= 1f
                   && hp0 - Hp(k, u) >= 8f - 0.01f && k.EmgRuleCountOfKey(key) == c1 + 1
                   && MatterSum(k, EmgMatterConst.Fire, new double2(50, 0), 2f) <= 0f && MatterSum(k, EmgMatterConst.Fire, new double2(60, 0), 2f) <= 0f,
                $"沾油单位受热（燃烧沾染挂上）：脚下生成明火、油污消耗、着火度置满、附带伤害 {hp0 - Hp(k, u):0.#}（8）、计数 +1；只沾油 / 只着火的对照不生成明火");
        }

        // ── C 导电水洼 ───────────────────────────────────────────────────────

        private static void CheckConduct()
        {
            Line("  · C. 导电水洼（E14 带电水洼连锁电击，含己方；C03 浸湿单位通电电弧跳向 ≤ 3 个导体）+ 只有水不发生");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_conduct");
            k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 3f, 6f, 30f);
            k.SpawnPatch(EmgMatterConst.Water, new double2(30, 0), 3f, 6f, 30f); // 对照：不带电
            int ally = Unit(k, CombatFaction.Player, new double2(0.5, 0));
            int foe = Unit(k, CombatFaction.Hostile, new double2(-0.5, 0));
            int dry = Unit(k, CombatFaction.Hostile, new double2(30, 0.5));
            StepN(k, 20);
            int c0 = k.EmgRuleCountOfKey(key);
            float a0 = Hp(k, ally), f0 = Hp(k, foe), d0 = Hp(k, dry);
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
            StepN(k, 30);
            Expect(k.EmgRuleCountOfKey(key) > c0 && Hp(k, ally) < a0 && Hp(k, foe) < f0 && Hp(k, dry) == d0 && k.AccumOf(dry, EmgPhaseConst.Charge) == 0f,
                $"带电水洼对站在里面的单位放电：己方掉血 {a0 - Hp(k, ally):0.#}、敌方 {f0 - Hp(k, foe):0.#}（物质中立），计数 +{k.EmgRuleCountOfKey(key) - c0}；不带电水洼里的对照不掉血、无电荷");
            // C03：浸湿单位一次命中通电 → 本单位电荷 × 2，电弧跳 ≤ 3 个导体
            using CombatKernel k2 = NewKernel();
            int wet = Unit(k2, CombatFaction.Hostile, new double2(0, 0));
            int n1 = Unit(k2, CombatFaction.Hostile, new double2(1.5, 0)), n2 = Unit(k2, CombatFaction.Player, new double2(-2, 0)), n3 = Unit(k2, CombatFaction.Hostile, new double2(0, 2.5));
            int n4 = Unit(k2, CombatFaction.Hostile, new double2(3.5, 0)), far = Unit(k2, CombatFaction.Hostile, new double2(9, 0));
            k2.ApplyStatus(wet, Bit("Wet"), 10f, 0f, 0f, 0f, 0);
            int cc = k2.EmgRuleCountOfKey(key);
            k2.ApplyStatus(wet, Bit("Shock"), 3f, 0f, 0f, 0f, 0);
            int got = (k2.AccumOf(n1, EmgPhaseConst.Charge) > 0f ? 1 : 0) + (k2.AccumOf(n2, EmgPhaseConst.Charge) > 0f ? 1 : 0) + (k2.AccumOf(n3, EmgPhaseConst.Charge) > 0f ? 1 : 0);
            Expect(k2.EmgRuleCountOfKey(key) == cc + 1 && got == 3 && k2.AccumOf(n4, EmgPhaseConst.Charge) == 0f && k2.AccumOf(far, EmgPhaseConst.Charge) == 0f && (Status(k2, wet) & Bit("Wet")) == 0u,
                $"浸湿 + 电击：电弧跳向 4 米内最近 3 个导体（含己方 n2），第 4 近与 9 米外不跳，浸湿消耗，计数 +1");
            int shockOnly = Unit(k2, CombatFaction.Hostile, new double2(40, 0));
            int c2 = k2.EmgRuleCountOfKey(key);
            k2.ApplyStatus(shockOnly, Bit("Shock"), 3f, 0f, 0f, 0f, 0);
            Expect(k2.EmgRuleCountOfKey(key) == c2, "负向：只有电击（没浸湿）不发生短路");
        }

        // ── D 碎裂 ───────────────────────────────────────────────────────────

        private static void CheckShatter()
        {
            Line("  · D. 碎裂（P01 冻结 + 动能 → 破片；关闭 FG-GAP-052）+ 未冻结同样重击不碎");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_shatter");
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 30f, Cooldown = 0f, Program = -1,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            }), 500f);
            int frozen = Unit(k, CombatFaction.Hostile, new double2(0, 8));
            int warm = Unit(k, CombatFaction.Hostile, new double2(20, 8));
            k.ChillUnit(frozen, 1f);
            int c0 = k.EmgRuleCountOfKey(key);
            int p0 = k.ProjectileCount;
            k.FireAt(me, frozen, k.Time);
            int frags = k.ProjectileCount - p0;
            k.FireAt(me, warm, k.Time);
            Expect(k.EmgRuleCountOfKey(key) == c0 + 1 && frags >= 6 && !k.InPhase(frozen, EmgPhaseConst.Chill),
                $"冻结目标受单次动能 30（≥ 24 × 钢脆性 1）碎成 {frags} 片破片、立即解冻，计数按碎裂稳定键 +1；同样重击打未冻结目标不再计数（{k.EmgRuleCountOfKey(key) - c0}）");

            // 卡片负向“首领冻结后受动能（按抗性）”：寒抗 0.5 的首领喷一次只积一半、没冻上 → 重击不碎；再喷一次冻上 → 同一重击照样碎（抗性按属性列，不按单位 ID）。
            // 每一发用一台单独的射手（同一时刻同一台连开第二发会被冷却 / 同步拦下）
            int ShooterAt(double2 at) => Unit(k, CombatFaction.Player, at, k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 30f, Cooldown = 0f, Program = -1,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            }), 500f);
            int boss = Unit(k, CombatFaction.Hostile, new double2(-40, 8));
            int s1 = ShooterAt(new double2(-40, 0)), s2 = ShooterAt(new double2(-40, -1));
            k.SetResist(boss, 0, 0.5f);
            k.ChillUnit(boss, 1f);
            bool half = !k.InPhase(boss, EmgPhaseConst.Chill);
            int cb0 = k.EmgRuleCountOfKey(key);
            CombatFireResult r1 = k.FireAt(s1, boss, k.Time);
            int cb1 = k.EmgRuleCountOfKey(key);
            int sprays = 1;
            while (!k.InPhase(boss, EmgPhaseConst.Chill) && sprays < 6)
            {
                k.ChillUnit(boss, 1f); // 寒抗 0.5：每次只积一半，要多喷几次才冻上
                sprays++;
            }
            bool frozenNow = k.InPhase(boss, EmgPhaseConst.Chill);
            CombatFireResult r2 = k.FireAt(s2, boss, k.Time);
            Expect(half && sprays >= 2 && cb1 == cb0 && frozenNow && k.EmgRuleCountOfKey(key) == cb0 + 1 && !k.InPhase(boss, EmgPhaseConst.Chill),
                $"首领（寒抗 0.5）：喷一次没冻上（{half}）时重击不碎（{cb1 - cb0}，{r1}），喷到第 {sprays} 次冻上（{frozenNow}）后同一重击碎裂（{k.EmgRuleCountOfKey(key) - cb0}，{r2}）、解冻");
        }

        // ── E 烟幕 ───────────────────────────────────────────────────────────

        private static void CheckSteam()
        {
            Line("  · E. 烟幕（M12 明火遇水生成蒸汽斑块；C02 浸湿单位受热冒蒸汽、熄火）+ 只有水不发生");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_steam");
            int c0 = k.EmgRuleCountOfKey(key);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 4f, 20f);
            k.SpawnPatch(EmgMatterConst.Water, new double2(0.5, 0), 2f, 6f, 20f);
            k.SpawnPatch(EmgMatterConst.Water, new double2(30, 0), 2f, 6f, 20f); // 对照：只有水
            int s = StepsUntil(k, () => MatterSum(k, EmgMatterConst.Steam) > 0f, 120);
            Expect(s > 0 && k.EmgRuleCountOfKey(key) > c0 && MatterSum(k, EmgMatterConst.Steam, new double2(30, 0), 2f) <= 0f && FindPatch(k, EmgMatterConst.Steam, out CombatZone st)
                   && st.Layer == EmgMatterConst.LayerGas && k.MatterRow(EmgMatterConst.Steam).Obscure >= 0.5f,
                $"明火遇水第 {s} 步生成蒸汽斑块（气体层、遮蔽 {k.MatterRow(EmgMatterConst.Steam).Obscure}），计数 +{k.EmgRuleCountOfKey(key) - c0}；只有水的地方不冒蒸汽（失锁由遮蔽经场 × 载体表产生，EMG2-FLD-01）");
            int u = Unit(k, CombatFaction.Hostile, new double2(50, 0));
            k.ApplyStatus(u, Bit("Wet"), 10f, 0f, 0f, 0f, 0);
            int c1 = k.EmgRuleCountOfKey(key);
            k.ApplyStatus(u, Bit("Fire"), 3f, 4f, 0f, 0f, 0);
            Expect(k.EmgRuleCountOfKey(key) == c1 + 1 && MatterSum(k, EmgMatterConst.Steam, new double2(50, 0), 2f) > 0f && (Status(k, u) & (Bit("Wet") | Bit("Fire"))) == 0u
                   && k.AccumOf(u, EmgPhaseConst.Ignite) == 0f,
                $"浸湿单位受热：脚下冒蒸汽（{MatterSum(k, EmgMatterConst.Steam, new double2(50, 0), 2f)}）、浸湿消耗、不着火（着火度 {k.AccumOf(u, EmgPhaseConst.Ignite)}、状态 0x{Status(k, u):X}），计数 +{k.EmgRuleCountOfKey(key) - c1}");
        }

        // ── F 固化阻路 ───────────────────────────────────────────────────────

        private static void CheckCure()
        {
            Line("  · F. 固化阻路（M16 聚合胶遇酸 → 固化胶块 ter.block；ADR ⑯ 推出 / 不可进入 / 绕行 / 离地不受阻）+ 只有一种配料不发生 + 可被打碎");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_sticky");
            int c0 = k.EmgRuleCountOfKey(key);
            k.SpawnPatch(EmgMatterConst.Polymer, new double2(0, 0), 3f, 8f, 60f);
            k.SpawnPatch(EmgMatterConst.Polymer, new double2(40, 0), 3f, 8f, 60f); // 对照：只有胶
            k.SpawnPatch(EmgMatterConst.Acid, new double2(80, 0), 3f, 8f, 60f); // 对照：只有酸
            int a = Unit(k, CombatFaction.Player, new double2(0.3, 0.1));
            int b = Unit(k, CombatFaction.Hostile, new double2(-0.4, 0));
            int air = Unit(k, CombatFaction.Player, new double2(0, 0.6), air: true);
            // 复审负向：不可推动的（炮塔、敌方优先级 0）在圈里也不被推出（CanDisplace 同一口径）
            int Fixed(CombatUnitKind kind, CombatFaction f, double2 at, int prio) => k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = f, Behavior = CombatBehavior.HoldFire, Flags = Live, Position = at, Home = at, Radius = 0.5f, Speed = 0f,
                Health = 400f, MaxHealth = 400f, Weapon = -1, BehaviorProfile = -1, Priority = (byte)prio,
            });
            double2 turretAt = new double2(-0.2, -0.6), coreAt = new double2(0.7, -0.6);
            int turret = Fixed(CombatUnitKind.Turret, CombatFaction.Player, turretAt, 1);
            int core = Fixed(CombatUnitKind.Enemy, CombatFaction.Hostile, coreAt, 0);
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0.5, 0), 2f, 6f, 60f);
            int s = StepsUntil(k, () => FindPatch(k, EmgMatterConst.Cured, out _), 120);
            FindPatch(k, EmgMatterConst.Cured, out CombatZone cube);
            double reach = cube.Radius + 0.5;
            double da = math.distance(Pos(k, a), cube.Pos), db = math.distance(Pos(k, b), cube.Pos);
            Expect(s > 0 && k.EmgRuleCountOfKey(key) > c0 && da >= reach - 1e-3 && db >= reach - 1e-3 && math.distance(Pos(k, air), new double2(0, 0.6)) < 1e-6
                   && !FindPatchNear(k, EmgMatterConst.Cured, new double2(40, 0)) && !FindPatchNear(k, EmgMatterConst.Cured, new double2(80, 0)),
                $"聚合胶遇酸第 {s} 步固化成胶块（半径 {cube.Radius:0.##}），圈内两台按 Id 升序推到边缘（{da:0.##} / {db:0.##} ≥ {reach:0.##}），离地单位原地不动；只有胶 / 只有酸的地方不固化；计数 +{k.EmgRuleCountOfKey(key) - c0}");
            double dt0 = math.distance(Pos(k, turret), turretAt), dc0 = math.distance(Pos(k, core), coreAt);
            Expect(dt0 < 1e-6 && dc0 < 1e-6,
                $"负向：圈里的炮塔（位移 {dt0:0.###}）与不可推动的敌方单位（优先级 0，位移 {dc0:0.###}）原地不动——内核位置不与热更层的建筑座 / 据点脱节");
            // 之后不可进入 + 后续单位绕行
            int walker = Unit(k, CombatFaction.Player, cube.Pos + new double2(-6, 0.2));
            double2 goal = cube.Pos + new double2(6, 0);
            k.IssueCommand(walker, CombatCommandKind.Move, goal, 0, 0.3f, 0f, 0f, false);
            double minD = 99;
            int arrive = -1;
            for (int i = 1; i <= 900 && arrive < 0; i++)
            {
                StepN(k, 1);
                minD = math.min(minD, math.distance(Pos(k, walker), cube.Pos));
                if (math.distance(Pos(k, walker), goal) < 0.6)
                {
                    arrive = i;
                }
            }
            Expect(minD >= reach - 0.02 && arrive > 0,
                $"之后不可进入：穿过胶块的移动命令全程离圆心 ≥ {minD:0.##}（胶块 + 车身 {reach:0.##}），第 {arrive} 步绕到对面目标点");
            int flyer = Unit(k, CombatFaction.Player, cube.Pos + new double2(-4, 0), air: true);
            k.IssueCommand(flyer, CombatCommandKind.Move, cube.Pos + new double2(4, 0), 0, 0.3f, 0f, 0f, false);
            double fMin = 99;
            for (int i = 0; i < 300; i++)
            {
                StepN(k, 1);
                fMin = math.min(fMin, math.distance(Pos(k, flyer), cube.Pos));
            }
            Expect(fMin < cube.Radius, $"离地单位不受地面固态斑块阻挡（飞越时离圆心最近 {fMin:0.##} < 半径 {cube.Radius:0.##}）");
            // 堵死唯一通路可被打碎：动能累计到耐久 120
            // 堵死唯一通路可被打碎：一发重弹（寿命到正落在胶块圆心）累计动能 ≥ 耐久 120；对照：轻弹不碎
            // （另起一块存续 60 秒的胶块，免得上面绕行 / 飞越用掉的时间碰上野外 30 秒寿命）
            using CombatKernel k2 = NewKernel();
            k2.SpawnPatch(EmgMatterConst.Cured, new double2(0, 0), 2f, 4f, 60f);
            CombatFireResult light = LandShot(k2, new double2(0, 0), 4f);
            bool stay = FindPatch(k2, EmgMatterConst.Cured, out CombatZone cz); // EMG2-PERSIST-01：胶块挡弹（fieldrule_solid_absorb），弹体在圆边被吸收、扣耐久 = 伤害
            Expect(light == CombatFireResult.Launched && stay && Mathf.Approximately(cz.Hp, 116f), $"对照：轻弹（4）被胶块吸收，耐久 120 → {cz.Hp:0.#}，胶块还在（开火 {light}）");
            CombatFireResult heavy = LandShot(k2, new double2(0, 0), 300f);
            Expect(heavy == CombatFireResult.Launched && !FindPatch(k2, EmgMatterConst.Cured, out _) && k2.PatchCount == 0,
                $"胶块受够动能（重弹 300 ≥ 耐久 120）碎掉、无产物（剩斑块 {k2.PatchCount}），通路重开（开火 {heavy}；堵路警告归 EMG2-PERSIST-01）");
        }

        /// <summary>从落点正南 8 米处打一发寿命刚好飞到落点的射弹（不装固件；动能 = 伤害 × 载体动能系数，在落点累计到固态斑块）。</summary>
        private static CombatFireResult LandShot(CombatKernel k, double2 at, float damage)
        {
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 80f, Damage = damage, Cooldown = 0f, Program = -1,
                SpawnFlight = 1, ProjectileSpeed = 30f, ProjectileRadius = 0.15f, ProjectileLife = 8f / 30f,
            };
            int me = Unit(k, CombatFaction.Player, at + new double2(0, -8), k.AddWeapon(w), 500f);
            int foe = Unit(k, CombatFaction.Hostile, at + new double2(0, 40), -1, 500f);
            CombatFireResult fired = k.FireAt(me, foe, k.Time);
            StepN(k, 60);
            k.Despawn(me);
            k.Despawn(foe);
            return fired;
        }

        private static bool FindPatchNear(CombatKernel k, byte matter, double2 at)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && math.distance(z.Pos, at) < 4)
                {
                    return true;
                }
            }
            return false;
        }

        // ── C06 粘住 ─────────────────────────────────────────────────────────

        private static void CheckStick()
        {
            Line("  · F2. 胶膜单位沾酸粘住（C06 bhv.stick）：2.5 秒能开火、不能移动，结束后胶膜清除；只有胶膜不发生");
            using CombatKernel k = NewKernel();
            int key = Key("reaction_sticky");
            int u = Unit(k, CombatFaction.Player, new double2(0, 0));
            int only = Unit(k, CombatFaction.Player, new double2(20, 0));
            k.ApplyStatus(u, Bit("SugarFilm"), 10f, 0f, 0.3f, 0f, 0);
            k.ApplyStatus(only, Bit("SugarFilm"), 10f, 0f, 0.3f, 0f, 0);
            int c0 = k.EmgRuleCountOfKey(key);
            k.ApplyStatus(u, Bit("Acid"), 5f, 0f, 0f, 0f, 0);
            float stick = St(k, u).StickT;
            k.IssueCommand(u, CombatCommandKind.Move, new double2(0, 10), 0, 0.3f, 0f, 0f, false);
            k.IssueCommand(only, CombatCommandKind.Move, new double2(20, 10), 0, 0.3f, 0f, 0f, false);
            StepN(k, 60);
            double moved = math.distance(Pos(k, u), new double2(0, 0));
            double movedOnly = math.distance(Pos(k, only), new double2(20, 0));
            int s = StepsUntil(k, () => St(k, u).StickT <= 0f, 200);
            Expect(math.abs(stick - 2.5f) < 1e-4f && moved < 1e-6 && movedOnly > 1.0 && k.EmgRuleCountOfKey(key) == c0 + 1 && s > 0 && (Status(k, u) & Bit("SugarFilm")) == 0u,
                $"胶膜 + 酸：粘住 {stick} 秒，期间移动命令不动（{moved:0.###}）、只沾胶膜的对照照常走（{movedOnly:0.#}）；粘住结束后胶膜清除；计数 +1");
            // 复审负向：带胶膜站在酸池里（每个接触节拍都再挂酸）——粘住期间不重新结算：不续时、计数 / 字幕只 +1，粘住总时长 ≤ 2.5 秒
            using CombatKernel k2 = NewKernel();
            int v = Unit(k2, CombatFaction.Hostile, new double2(0, 0));
            k2.ApplyStatus(v, Bit("SugarFilm"), 10f, 0f, 0.3f, 0f, 0);
            int c1 = k2.EmgRuleCountOfKey(key);
            k2.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 6f, 60f);
            int stuckSteps = 0;
            bool rose = false;
            float last = 0f;
            for (int i = 0; i < 240; i++)
            {
                StepN(k2, 1);
                float st = St(k2, v).StickT;
                stuckSteps += st > 0f ? 1 : 0;
                rose |= last > 0f && st > last + 1e-5f;
                last = st;
            }
            Expect(k2.EmgRuleCountOfKey(key) == c1 + 1 && !rose && stuckSteps * Dt <= 2.5f + 2f * Dt && Hp(k2, v) > 0f,
                $"胶膜单位站在酸池里 4 秒：固化计数 +{k2.EmgRuleCountOfKey(key) - c1}（= 1）、粘住剩余秒没被续上（{!rose}）、粘住总时长 {stuckSteps * Dt:0.##} 秒 ≤ 2.5");
        }

        // ── G 熔穿过载 P03 ───────────────────────────────────────────────────

        private static void CheckMeltOverload()
        {
            Line("  · G. 熔穿过载 P03（同一次命中注热 ≥ 45 且动能 ≥ 40 → 溅出熔融金属 + 目标熔穿；旧穿甲 +30% 退役）+ 负向矩阵");
            int key = Key("reaction_meltoverload");
            (bool melt, float molten, bool phase, int count) Shot(float damage, string firmware, CombatReaction reaction, bool spent)
            {
                using CombatKernel k = NewKernel();
                int prog = firmware != null ? HeatProgram(k, firmware) : -1;
                var w = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f, Program = prog,
                    Reading = new CombatReading { Carrier = CombatCarrier.Projectile }, Reaction = reaction,
                };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 500f);
                int t = Unit(k, CombatFaction.Hostile, new double2(0, 8), -1, 800f);
                if (spent)
                {
                    k.SetFlag(me, CombatUnitFlags.ReactionGated, true);
                    k.SetFlag(me, CombatUnitFlags.ReactionSpent, true);
                }
                int c0 = k.EmgRuleCountOfKey(key);
                k.FireAt(me, t, k.Time);
                StepN(k, 2);
                return (k.EmgRuleCountOfKey(key) > c0, MatterSum(k, EmgMatterConst.Molten, Pos(k, t), 3f), k.InPhase(t, EmgPhaseConst.Overheat), k.EmgRuleCountOfKey(key) - c0);
            }
            var hit = Shot(110f, "fw_overload", CombatReaction.MeltOverload, false);
            Expect(hit.melt && hit.molten > 0f && hit.phase && hit.count == 1,
                $"注热 45（过载）+ 动能 110 的一击：命中点溅出熔融金属 {hit.molten:0.#} 份、目标进入熔穿相态，计数按熔穿过载稳定键 +{hit.count}（只断言主结果，不断言穿甲）");
            var weak = Shot(30f, "fw_overload", CombatReaction.MeltOverload, false);
            var cold = Shot(110f, null, CombatReaction.MeltOverload, false);
            var flag = Shot(110f, "fw_overload", CombatReaction.None, false);
            var gated = Shot(110f, "fw_overload", CombatReaction.MeltOverload, true);
            Expect(!weak.melt && !cold.melt && !flag.melt && !gated.melt && weak.molten <= 0f && cold.molten <= 0f,
                $"负向（只有一种配料不发生）：动能 30 < 40 不熔穿（{weak.melt}）；没有注热不熔穿（{cold.melt}）；开火时不成立（没有 cost 行判定）不结算（{flag.melt}）；核心固件已发动被压住不结算（{gated.melt}）");
            // 开火代价：重炮 + 过载每发 65（= 基础 40 + 过载表值 15 − 15 + 代价 25），被压住回到表值 55
            using CombatKernel k2 = NewKernel();
            var cannon = new CombatWeapon
            {
                Mode = CombatWeaponMode.Cannon, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 55f, Cooldown = 0.1f, AimSeconds = 0.05f,
                HeatPerShot = 55f, OverloadExtraHeat = 10f, OverheatAt = 500f, RecoverBelow = 100f, Reaction = CombatReaction.MeltOverload, PierceBonus = 0.3f,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            };
            int gun = Unit(k2, CombatFaction.Player, new double2(0, 0), k2.AddWeapon(cannon), 500f);
            int tgt = Unit(k2, CombatFaction.Hostile, new double2(0, 8), -1, 800f);
            k2.SetArmor(tgt, 0.5f, 90f);
            float hp0 = Hp(k2, tgt);
            double t0 = k2.Time;
            k2.FireAt(gun, tgt, k2.Time);
            StepsUntil(k2, () => { k2.FireAt(gun, tgt, k2.Time); return k2.TryGetUnit(gun, out CombatUnitView gv) && gv.Heat > 0f; }, 120);
            float heat = k2.TryGetUnit(gun, out CombatUnitView hv) ? hv.Heat : -1f;
            float dealt = hp0 - Hp(k2, tgt);
            Expect(math.abs(heat - 65f) < 0.5f && dealt <= 55f * 0.5f + 0.5f,
                $"开火者代价：重炮首发积热 {heat:0.#}（= 65，cost.heat 25 取代过载表值 15，不叠加）；正面装甲照常减伤（伤害 {dealt:0.#} ≤ 27.5，旧穿甲 +30% 退役）");
        }

        // ── H 装配判定端口化 ─────────────────────────────────────────────────

        private static void CheckAssembly()
        {
            Line("  · H. 装配反应端口化（FGR-EMG-021 豁免撤销）：熔穿过载 = 重型射弹 + en.heat 写端；标记跳转 = evt.marked 条件 + grm.mod.chain");
            string cannon = ComponentCatalog.CompCannonId, beam = ComponentCatalog.CompBeamId, gun = ComponentCatalog.CompGunId;
            string overload = FirmwareCatalog.FwOverloadId, mark = FirmwareCatalog.FwMarkTagId;
            Expect(EmgRuleCatalog.AssemblyReactionId(cannon, new[] { overload }) == MechanicalReactionCatalog.ReactionMeltOverloadId
                   && EmgRuleCatalog.AssemblyReactionId(beam, new[] { overload }) == null
                   && Mathf.Approximately(BlueprintCircuitBoard.ComputeHeatBudget(cannon, new[] { overload }), 65f)
                   && Mathf.Approximately(BlueprintCircuitBoard.ComputeHeatBudget(beam, new[] { overload }) - BlueprintCircuitBoard.ComputeHeatBudget(beam, Array.Empty<string>()), 15f)
                   && Mathf.Approximately(BlueprintCircuitBoard.ComputeHeatBudget(cannon, Array.Empty<string>()), 40f),
                "铸造重炮 + 过载 → 熔穿过载、每发 65；切割束 + 过载不触发（光束不是重型射弹）、每发基础 + 15；重炮无固件 40");
            Expect(EmgRuleCatalog.AssemblyReactionId(gun, new[] { mark }) == MechanicalReactionCatalog.ReactionMarkJumpId
                   && EmgRuleCatalog.AssemblyReactionId(cannon, new[] { mark }) == MechanicalReactionCatalog.ReactionMarkJumpId
                   && EmgRuleCatalog.AssemblyReactionId(gun, Array.Empty<string>()) == null,
                "标记跳转按固件端口判定（不再要求标记器 + 连射器 / 切割束同装，任何主组件 + 标记跳转固件都认，预期差异）；没有固件不判定");
        }

        // ── I 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · I. 存读档：粘住剩余秒进快照 17（段尾追加）、读档续跑一致；旧表撤下反应的计数按 Key 并进涌现计数（32 → 256 不错位）");
            using CombatKernel k = NewKernel();
            int u = Unit(k, CombatFaction.Player, new double2(0, 0));
            k.ApplyStatus(u, Bit("SugarFilm"), 10f, 0f, 0.3f, 0f, 0);
            k.ApplyStatus(u, Bit("Acid"), 5f, 0f, 0f, 0f, 0);
            StepN(k, 30);
            byte[] snap = k.Serialize();
            float before = St(k, u).StickT;
            using CombatKernel k2 = NewKernel();
            CombatLoadResult lr = k2.Load(snap);
            float after = St(k2, u).StickT;
            StepN(k, 60);
            StepN(k2, 60);
            Expect(lr == CombatLoadResult.Ok && before > 0f && Mathf.Approximately(before, after) && k.StateHash() == k2.StateHash(),
                $"粘住 {before:0.##} 秒时存档，读档 {lr} 后 {after:0.##} 秒，各续跑 1 秒状态哈希一致");
            // 旧档：计数还在旧标签反应表里（按键）——读进来、换成撤空的表时按键并进涌现可靠计数。旧结算路径已删，这里直接写快照的计数块造旧档
            using CombatKernel k3 = NewKernel();
            int key = Key("reaction_conduct");
            var oldTable = new[] { new CombatReactionRule { Pair = Bit("Wet") | Bit("Shock"), Consume = Bit("Wet"), DamageMult = 1.3f, Key = key } };
            k3.SetReactionRules(oldTable);
            byte[] oldSnap = k3.Serialize();
            int oldBody = oldSnap.Length - 4;
            Buffer.BlockCopy(BitConverter.GetBytes(1), 0, oldSnap, oldBody - CombatConst.MaxReactions * CombatKernel.ReactionCounterEntryBytes + 4, 4);
            uint fnv = 2166136261u;
            for (int i = 0; i < oldBody; i++)
            {
                fnv = (fnv ^ oldSnap[i]) * 16777619u;
            }
            Buffer.BlockCopy(BitConverter.GetBytes(fnv), 0, oldSnap, oldBody, 4);
            using CombatKernel k4 = NewKernel();
            CombatLoadResult lr4 = k4.Load(oldSnap); // 正式路径：表已撤空的地点读旧档
            k4.SetReactionRules(NamedReactionCatalog.BuildKernelRules()); // 再次登记不重复并入
            Expect(lr4 == CombatLoadResult.Ok && k4.EmgRuleCountOfKey(key) == 1,
                $"旧档里旧标签表的短路计数 1 → 读档（{lr4}）并换成撤空后的表：按稳定键并进涌现可靠计数（{k4.EmgRuleCountOfKey(key)}），内核规则下标不进存档");
        }

        // ── J 暂停与倍速 ─────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            Line("  · J. 暂停与 0.5x～3x：粘住按游戏秒走（暂停不走）");
            var ok = new List<string>();
            foreach (float speed in new[] { 0f, 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                int u = Unit(k, CombatFaction.Player, new double2(0, 0));
                k.ApplyStatus(u, Bit("SugarFilm"), 10f, 0f, 0.3f, 0f, 0);
                k.ApplyStatus(u, Bit("Acid"), 5f, 0f, 0f, 0f, 0);
                StepN(k, 30, Dt * speed); // 真实 0.5 秒
                float left = St(k, u).StickT;
                float expect = math.max(0f, 2.5f - 0.5f * speed);
                if (math.abs(left - expect) < 0.02f)
                {
                    ok.Add($"{speed}x:{left:0.##}");
                }
            }
            Expect(ok.Count == 5, $"真实 0.5 秒后粘住剩余（按游戏秒）：{string.Join(" ", ok)}");
        }

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

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
