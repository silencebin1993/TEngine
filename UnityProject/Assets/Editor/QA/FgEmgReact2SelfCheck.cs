using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
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
    /// EMG2-REACT-02 其余 11 条具名反应去倍率改写的自动验收（FG19 FGR-EMG-085 / 086 / 087；FG19B 第 2.7、2.8、4、7 节；FGT-EMG-030 16 条全量、FGT-EMG-032 扩到 11 条）。
    /// 全部起真实内核（CombatSite 同一套表写入）：每条“触发 / 只有一种配料不发生”对照；优先级（C04 先于 C03、W01 先于 W02）；
    /// W02 无接触传播、锈蚀破片碎不动冻结目标；P02 主结果是破片；澄净免疫酸时 C05 / M03 的口径；湮灭与处决的结算顺序；
    /// 存读档（段 21、BeamHit、SlowQ、DwellMatter）；暂停与 0.5x～3x（C05 / C07 计时）；其余移交项（光束不碎、电击换算、碎屑吸附、带火穿油、胶减速、烟 / 蒸汽失锁、酸沼吸水 / 通电）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgReact2SelfCheck）。
    /// </summary>
    public static class FgEmgReact2SelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const int Chill = EmgPhaseConst.Chill, Charge = EmgPhaseConst.Charge, Corrode = EmgPhaseConst.Corrode, Mag = EmgPhaseConst.Magnetize;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/其余十一条具名反应")]
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
            Line("\n[其余十一条具名反应] EMG2-REACT-02：电解 / 绝缘 / 热震 / 腐蚀灼烧 / 回路溶断 / 胶结 / 乳化 / 湮灭 / 玻化 / 淤塞 / 连锁锈蚀（FGT-EMG-030 / 032）");
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
                EmgFieldCatalog.Reload();
                NamedReactionCatalog.Reload();
                FirmwareKinds.ResetForTests();
                Step(CheckAll16);
                Step(CheckElectrolysis);
                Step(CheckInsulate);
                Step(CheckCausticBurn);
                Step(CheckMudify);
                Step(CheckThermalShock);
                Step(CheckPatchRows);
                Step(CheckVitrify);
                Step(CheckWreckAcid);
                Step(CheckWreckSludge);
                Step(CheckAnnihilate);
                Step(CheckHandovers);
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
            Line($"  · [其余十一条具名反应] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyContent(k);
            return k;
        }

        /// <summary>克隆一行规则并改一个字段（Luban 生成的表行字段只读，自检造坏行用）。</summary>
        private static GameConfig.fg.ChannelRule With(GameConfig.fg.ChannelRule src, string field, object value)
        {
            var clone = (GameConfig.fg.ChannelRule)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(src, null);
            typeof(GameConfig.fg.ChannelRule).GetField(field).SetValue(clone, value);
            return clone;
        }

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        private static int Key(string id) => NamedReactionCatalog.StableKey(id);

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 400f, CombatUnitKind? kind = null)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind ?? (f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy), Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });
            if (f == CombatFaction.Hostile)
            {
                k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
            }
            return id;
        }

        private static CombatWeapon Instant(float damage, bool beam = false) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f, BeamHit = (byte)(beam ? 1 : 0),
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Flight(float damage, float speed = 15f, float life = 4f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 40f, Damage = damage, Cooldown = 0f,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static float Sum(CombatKernel k, byte matter, double2 at, float within = 3f)
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

        private static int Blocks(CombatKernel k, byte matter)
        {
            int n = 0;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter)
                {
                    n++;
                }
            }
            return n;
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static bool Has(CombatKernel k, int id, string tag) => k.TryGetStatus(id, out uint m, out _, out _, out _, out _) && (m & Bit(tag)) != 0u;

        private static void Coat(CombatKernel k, int id, string tag) => k.ApplyStatus(id, Bit(tag), 10f, 0f, 0f, 0f, 0);

        private static void Elec(CombatKernel k, int id) => k.ApplyStatus(id, Bit("Shock"), 3f, 0f, 0f, 0f, 0);

        private static int Fragments(CombatKernel k)
        {
            int n = 0;
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (p.Flags & EmgProjFlags.Fragment) != 0)
                {
                    n++;
                }
            }
            return n;
        }

        private static int AddWreck(CombatKernel k, double2 at, float hp = 40f, float mass = 6f)
        {
            k.TryAddWreck(new EmgWreck { Pos = at, Radius = 0.6f, Hp = hp, MaxHp = hp, Mass = mass, Born = k.Time, Until = k.Time + 90.0, Profile = -1 }, out int id);
            return id;
        }

        private static float WreckHp(CombatKernel k, int id)
        {
            int i = k.WreckIndexOf(id);
            return i >= 0 && k.TryGetWreck(i, out EmgWreck w) ? w.Hp : 0f;
        }

        // ── 定向：16 条各触发一次（FgEmergenceFuzzSelfCheck 固定用例集的规则可达部分，FGR-EMG-015 规则四）─────

        internal static readonly string[] Named16 =
        {
            "reaction_steam", "reaction_deflagrate", "reaction_conduct", "reaction_shatter", "reaction_sticky", "reaction_electrolysis", "reaction_insulate",
            "reaction_thermalshock", "reaction_causticburn", "reaction_hemolysis", "reaction_syrup", "reaction_dissolve", "reaction_annihilate", "reaction_vitrify",
            "reaction_mudify", "reaction_sepsis",
        };

        /// <summary>同一个内核里铺 16 个互不干扰的场景，真实结算后返回每条的计数。</summary>
        internal static Dictionary<string, int> FireEachNamedOnce()
        {
            using CombatKernel k = NewKernel(128);
            double2 At(int i) => new double2(i * 60.0 - 480.0, 0);
            void Pair(int i, byte a, byte b)
            {
                k.SpawnPatch(a, At(i), 2f, 3f, 30f);
                k.SpawnPatch(b, At(i) + new double2(1, 0), 2f, 3f, 30f);
            }
            Pair(0, EmgMatterConst.Water, EmgMatterConst.Fire);
            Pair(1, EmgMatterConst.Oil, EmgMatterConst.Fire);
            k.SpawnPatch(EmgMatterConst.Water, At(2), 2f, 3f, 30f);
            int shooter = Unit(k, CombatFaction.Player, At(3) + new double2(0, -5), k.AddWeapon(Instant(100f)));
            int frozen = Unit(k, CombatFaction.Hostile, At(3));
            Pair(4, EmgMatterConst.Polymer, EmgMatterConst.Acid);
            k.SpawnPatch(EmgMatterConst.Acid, At(5), 2f, 5f, 30f);
            int insulated = Unit(k, CombatFaction.Hostile, At(6));
            int shocked = Unit(k, CombatFaction.Hostile, At(7));
            int caustic = Unit(k, CombatFaction.Hostile, At(8), hp: 5000f);
            k.SpawnPatch(EmgMatterConst.Acid, At(9), 2f, 20f, 60f);
            Pair(10, EmgMatterConst.Polymer, EmgMatterConst.Water);
            Pair(11, EmgMatterConst.Oil, EmgMatterConst.Acid);
            int crushed = Unit(k, CombatFaction.Hostile, At(12));
            k.SpawnPatch(EmgMatterConst.Dust, At(13), 2f, 3f, 30f);
            int muddy = Unit(k, CombatFaction.Hostile, At(14));
            k.SpawnPatch(EmgMatterConst.Sludge, At(15), 3f, 20f, 60f);
            StepN(k, 2);
            k.InjectPatchEnergy(At(2), 0.5f, EmgConst.PortElec, 1f, 1);
            k.ChillUnit(frozen, 1f);
            k.FireAt(shooter, frozen, k.Time);
            k.InjectPatchEnergy(At(5), 0.5f, EmgConst.PortElec, 1f, 1);
            Coat(k, insulated, "Oil");
            Elec(k, insulated);
            k.ChillUnit(shocked, 1f);
            k.InjectUnitHeat(shocked, 40f);
            Coat(k, caustic, "Acid");
            k.ApplyStatus(caustic, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
            AddWreck(k, At(9), 40f, 6f);
            k.SetHealth(crushed, 40f, 400f, true);
            k.SpawnField(EmgFieldConst.PortDeflect, At(12).x, At(12).y, 1.5f, 5f, shooter, CombatFaction.Player);
            Coat(k, muddy, "Earth");
            Coat(k, muddy, "Wet");
            AddWreck(k, At(15), 40f, 6f);
            bool heated = false;
            for (int s = 0; s < 60 * 12; s++)
            {
                StepN(k, 1);
                for (int i = 0; !heated && i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Dust && z.Conc < 0.6f)
                    {
                        k.InjectPatchEnergy(z.Pos, 3f, EmgConst.PortHeat, 400f, 1);
                        heated = true;
                    }
                }
            }
            return Named16.ToDictionary(id => id, id => k.EmgRuleCountOfKey(Key(id)));
        }

        // ── A FGT-EMG-030 16 条全量 ──────────────────────────────────────────

        private static readonly string[] Eleven =
        {
            "reaction_electrolysis", "reaction_insulate", "reaction_thermalshock", "reaction_causticburn", "reaction_hemolysis", "reaction_syrup", "reaction_dissolve",
            "reaction_annihilate", "reaction_vitrify", "reaction_mudify", "reaction_sepsis",
        };

        private static void CheckAll16()
        {
            Line("  · A. FGT-EMG-030：16 条全部 kind = rule、每条有可结算的规则行、零个 side.mult、主结果不是倍率；旧 CombatReactionRule 表登记了也不再结算；说明与打法提示不写倍率");
            var t = ConfigSystem.Instance.Tables;
            var rows = t.TbChannelRule.DataList;
            var bad = new List<string>();
            foreach (GameConfig.fg.Reaction row in NamedReactionCatalog.Rows.Where(r => r.Id != "reaction_marktag"))
            {
                bool hasRow = rows.Any(r => r.Named == row.Id);
                if (row.Kind != NamedReactionCatalog.KindRule || !hasRow)
                {
                    bad.Add($"{row.Id}（kind {row.Kind}，规则行 {hasRow}）");
                }
            }
            Expect(bad.Count == 0 && NamedReactionCatalog.TagRules.Count == 0, $"{NamedReactionCatalog.Rows.Count} 条反应全部走通道规则、每条有规则行（标记跳转由连锁跳转计数）{(bad.Count > 0 ? "；不符：" + string.Join("、", bad) : string.Empty)}");
            Expect(rows.All(r => r.Side != "side.mult"), "规则表零个 side.mult（FG19B §11）");
            Expect(rows.Where(r => Eleven.Contains(r.Named)).All(r => "STBP".Contains(r.MainKind) && r.Product != "none"), "11 条每行主结果 ∈ S / T / B / P 且有产物");
            // 负向：坏行被编译器拒绝（只有附带伤害、主结果为空；B 类写破甲产物）
            GameConfig.fg.ChannelRule good = rows.First(r => r.Named == "reaction_electrolysis");
            var badTable = rows.ToList();
            badTable[badTable.IndexOf(good)] = With(good, "MainKind", string.Empty);
            Expect(!EmgRuleCatalog.Compile(badTable, 256).Ok, "负向：主结果为空的假规则整表拒绝");
            badTable = rows.ToList();
            badTable[badTable.IndexOf(good)] = With(With(good, "MainKind", "B"), "Product", "bhv.breach");
            Expect(!EmgRuleCatalog.Compile(badTable, 256).Ok, "负向：主结果 B、产物 bhv.breach 的假规则整表拒绝");
            // 旧路径：表里登记了旧配对，标签凑齐也不结算（FireReaction 已删）
            using (CombatKernel k = NewKernel())
            {
                k.SetEmgRules(new List<EmgRule>());
                k.SetReactionRules(new[] { new CombatReactionRule { Pair = Bit("Acid") | Bit("Wet"), Consume = Bit("Wet"), DamageMult = 2f, Key = 1234 } });
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                float hp0 = Hp(k, u);
                k.ApplyStatus(u, Bit("Acid"), 3f, 0f, 0f, 0f, 0, 10f);
                k.ApplyStatus(u, Bit("Wet"), 3f, 0f, 0f, 0f, 0, 10f);
                Expect(k.ReactionCountOf(0) == 0 && Has(k, u, "Wet"), $"旧 CombatReactionRule 不再结算：登记了 ×2 的旧配对，标签凑齐计数 {k.ReactionCountOf(0)}、不消耗配料（掉血 {hp0 - Hp(k, u):0.#}）");
            }
            var noTip = new List<string>();
            var multText = new List<string>();
            var rx = new System.Text.RegularExpressions.Regex(@"[×xX]\s*\d|\d\s*倍|倍率");
            foreach (string id in Eleven)
            {
                NamedReactionCatalog.TryGet(id, out GameConfig.fg.Reaction row);
                string tip = ReactionDiscoveryService.TipOf(row);
                string text = GameText.Get(row.DescKey) + " " + tip;
                if (rx.IsMatch(text))
                {
                    multText.Add($"{id}：{text}");
                }
                if (string.IsNullOrEmpty(GameText.Get(row.DescKey)) || GameText.Get(row.DescKey).Contains("⟦"))
                {
                    noTip.Add(id);
                }
            }
            Expect(noTip.Count == 0 && multText.Count == 0, $"11 条说明 / 打法提示有文本且不写倍率（缺 {string.Join(",", noTip)}；写了倍率：{string.Join("；", multText)}）");
        }

        // ── B 电解 C08 / E16 ────────────────────────────────────────────────

        private static void CheckElectrolysis()
        {
            Line("  · B. 电解：沾腐蚀 + 注电 ≥ 0.2 → 脚下析出碎屑（C08）；酸斑块带电 → 碎屑（E16）；只有一种配料不发生");
            int key = Key("reaction_electrolysis");
            using CombatKernel k = NewKernel();
            int a = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            int onlyAcid = Unit(k, CombatFaction.Hostile, new double2(20, 0));
            int onlyElec = Unit(k, CombatFaction.Hostile, new double2(40, 0));
            Coat(k, a, "Acid");
            Coat(k, onlyAcid, "Acid");
            Elec(k, onlyElec);
            int c0 = k.EmgRuleCountOfKey(key);
            float debris0 = Sum(k, EmgMatterConst.Debris, new double2(0, 0));
            Elec(k, a);
            StepN(k, 2);
            Expect(k.EmgRuleCountOfKey(key) == c0 + 1 && Sum(k, EmgMatterConst.Debris, new double2(0, 0)) > debris0 && !Has(k, a, "Acid")
                   && Sum(k, EmgMatterConst.Debris, new double2(20, 0)) <= 0f && Sum(k, EmgMatterConst.Debris, new double2(40, 0)) <= 0f && Has(k, onlyAcid, "Acid"),
                $"沾腐蚀通电：计数 +{k.EmgRuleCountOfKey(key) - c0}、脚下碎屑 {Sum(k, EmgMatterConst.Debris, new double2(0, 0)):0.#} 份、腐蚀被消耗；只沾腐蚀 / 只通电都不析出");
            using CombatKernel p = NewKernel();
            p.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 5f, 30f);
            p.SpawnPatch(EmgMatterConst.Acid, new double2(30, 0), 2f, 5f, 30f);
            StepN(p, 2);
            int c1 = p.EmgRuleCountOfKey(key);
            p.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
            StepN(p, 40);
            Expect(p.EmgRuleCountOfKey(key) > c1 && Sum(p, EmgMatterConst.Debris, new double2(0, 0)) > 0f && Sum(p, EmgMatterConst.Debris, new double2(30, 0)) <= 0f,
                $"酸斑块带电（E16）：碎屑 {Sum(p, EmgMatterConst.Debris, new double2(0, 0)):0.#} 份；没通电的酸斑块不析出");
        }

        // ── C 绝缘 C04 ──────────────────────────────────────────────────────

        private static void CheckInsulate()
        {
            Line("  · C. 绝缘：沾油污的单位不得电荷，电改跳 4 米内最近的导体（× 0.8）；附近没有导体就消散；不消耗油污；C04 先于 C03");
            int key = Key("reaction_insulate"), conduct = Key("reaction_conduct");
            using CombatKernel k = NewKernel();
            int u = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            int near = Unit(k, CombatFaction.Hostile, new double2(2, 0));
            int farther = Unit(k, CombatFaction.Hostile, new double2(3.5, 0));
            Coat(k, u, "Oil");
            int c0 = k.EmgRuleCountOfKey(key);
            Elec(k, u);
            float qNear = k.AccumOf(near, Charge);
            Expect(k.EmgRuleCountOfKey(key) == c0 + 1 && k.AccumOf(u, Charge) <= 0f && qNear > 0.3f && qNear < 0.5f && k.AccumOf(farther, Charge) <= 0f && Has(k, u, "Oil"),
                $"闪络：本单位电荷 {k.AccumOf(u, Charge):0.##}，最近的导体得 {qNear:0.##}（0.5 × 0.8）、更远的 {k.AccumOf(farther, Charge):0.##}，油污还在");
            int alone = Unit(k, CombatFaction.Hostile, new double2(100, 0));
            Coat(k, alone, "Oil");
            int c1 = k.EmgRuleCountOfKey(key);
            Elec(k, alone);
            Expect(k.EmgRuleCountOfKey(key) == c1 + 1 && k.AccumOf(alone, Charge) <= 0f, $"附近没有导体：这股电消散（本单位电荷 {k.AccumOf(alone, Charge):0.##}）");
            int bare = Unit(k, CombatFaction.Hostile, new double2(200, 0));
            int c2 = k.EmgRuleCountOfKey(key);
            Elec(k, bare);
            Expect(k.EmgRuleCountOfKey(key) == c2 && k.AccumOf(bare, Charge) > 0.3f, $"对照：没沾油污照常得电荷 {k.AccumOf(bare, Charge):0.##}，不报绝缘");
            int both = Unit(k, CombatFaction.Hostile, new double2(300, 0));
            Coat(k, both, "Oil");
            Coat(k, both, "Wet");
            int c3 = k.EmgRuleCountOfKey(key), d3 = k.EmgRuleCountOfKey(conduct);
            Elec(k, both);
            Expect(k.EmgRuleCountOfKey(key) == c3 + 1 && k.EmgRuleCountOfKey(conduct) == d3 && Has(k, both, "Wet"), "优先级：油污 + 浸湿同时在身上通电，只结算绝缘（C04 优先 29 < C03 优先 32），浸湿不消耗");
            // 落点也可以是导电斑块 / 残骸（含己方）
            using CombatKernel g = NewKernel();
            int v = Unit(g, CombatFaction.Hostile, new double2(0, 0));
            g.SpawnPatch(EmgMatterConst.Water, new double2(2.5, 0), 1f, 2f, 30f);
            StepN(g, 2);
            Coat(g, v, "Oil");
            Elec(g, v);
            float wet = 0f;
            for (int i = 0; i < g.PatchSlots; i++)
            {
                if (g.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Water)
                {
                    wet = Mathf.Max(wet, z.Charge);
                }
            }
            Expect(wet > 0.3f, $"附近只有水洼：电跳进导电斑块（电荷 {wet:0.##}）");
            // DEBT-EMG2REACT01-02 裁决：斑块传导给单位的电走 E14 / E15，不触发单位层沾染行（否则同一单位一次结算两次）
            using CombatKernel t = NewKernel();
            int oily = Unit(t, CombatFaction.Hostile, new double2(0, 0));
            int soaked = Unit(t, CombatFaction.Hostile, new double2(30, 0));
            t.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 2f, 5f, 30f);
            t.SpawnPatch(EmgMatterConst.Water, new double2(30, 0), 2f, 5f, 30f);
            StepN(t, 20); // 单位进入斑块（接触桶 0.25 秒）
            Coat(t, oily, "Oil");
            Coat(t, soaked, "Wet");
            int i0 = t.EmgRuleCountOfKey(key);
            t.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
            t.InjectPatchEnergy(new double2(30, 0), 0.5f, EmgConst.PortElec, 1f, 3);
            // 电照常进单位：电荷累积，到阈值进麻痹（进麻痹时电荷清零，所以两个都算）
            bool gotOily = false, gotSoaked = false;
            for (int i = 0; i < 30; i++)
            {
                StepN(t, 1);
                gotOily |= t.AccumOf(oily, Charge) > 0f || t.InPhase(oily, Charge);
                gotSoaked |= t.AccumOf(soaked, Charge) > 0f || t.InPhase(soaked, Charge);
            }
            // reaction_conduct 也是 E14 的计数键，所以浸湿这边看 C03 的消耗（浸湿还在），不看计数
            Expect(t.EmgRuleCountOfKey(key) == i0 && gotOily && gotSoaked && Has(t, oily, "Oil") && Has(t, soaked, "Wet"),
                $"斑块传导不触发沾染行：站在带电水洼里，沾油污的单位得电 {gotOily}（C04 绝缘计数 +{t.EmgRuleCountOfKey(key) - i0}、不闪络）；浸湿的单位得电 {gotSoaked}（C03 不消耗浸湿）");
        }

        // ── D 腐蚀灼烧 C05 ──────────────────────────────────────────────────

        private static void CheckCausticBurn()
        {
            Line("  · D. 腐蚀灼烧：沾腐蚀 + 受热 → 4 秒内沿途滴明火（每 1.5 米一块，静止每秒一块）；只有一种配料不发生；澄净免疫酸位时不结算");
            int key = Key("reaction_causticburn");
            using (CombatKernel k = NewKernel())
            {
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0), hp: 5000f);
                int onlyAcid = Unit(k, CombatFaction.Hostile, new double2(40, 0), hp: 5000f);
                int onlyFire = Unit(k, CombatFaction.Hostile, new double2(80, 0), hp: 5000f);
                Coat(k, u, "Acid");
                Coat(k, onlyAcid, "Acid");
                k.ApplyStatus(onlyFire, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
                int c0 = k.EmgRuleCountOfKey(key);
                k.ApplyStatus(u, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
                StepN(k, 150); // 2.5 秒
                float fire = Sum(k, EmgMatterConst.Fire, new double2(0, 0));
                Expect(k.EmgRuleCountOfKey(key) == c0 + 1 && fire > 0f && Sum(k, EmgMatterConst.Fire, new double2(40, 0)) <= 0f && Sum(k, EmgMatterConst.Fire, new double2(80, 0)) <= 0f && !Has(k, u, "Acid"),
                    $"静止单位 2.5 秒后脚下明火 {fire:0.#} 份、计数 +{k.EmgRuleCountOfKey(key) - c0}、腐蚀被消耗；只沾腐蚀 / 只着火不滴火");
                StepN(k, 60 * 7);
                Expect(Sum(k, EmgMatterConst.Fire, new double2(0, 0)) <= 0f, $"4 秒滴完、明火 3 秒存续：9.5 秒后脚下明火 {Sum(k, EmgMatterConst.Fire, new double2(0, 0)):0.#} 份");
            }
            using (CombatKernel k = NewKernel())
            {
                // 单次注热 ≥ 20 也算（b = en.heat）：过载命中注热 45 起，走真实命中注能；对照无注热的同伤害武器不触发
                int prog = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_overload" }, FirmwareCarrier.Projectile));
                var hot = Instant(1f);
                hot.Program = prog;
                int mh = Unit(k, CombatFaction.Player, new double2(0, -5), k.AddWeapon(hot));
                int mc = Unit(k, CombatFaction.Player, new double2(40, -5), k.AddWeapon(Instant(1f)));
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0), hp: 5000f);
                int low = Unit(k, CombatFaction.Hostile, new double2(40, 0), hp: 5000f);
                Coat(k, u, "Acid");
                Coat(k, low, "Acid");
                int c0 = k.EmgRuleCountOfKey(key);
                k.FireAt(mc, low, k.Time);
                int afterLow = k.EmgRuleCountOfKey(key);
                k.FireAt(mh, u, k.Time);
                Expect(afterLow == c0 && k.EmgRuleCountOfKey(key) == c0 + 1, $"不带热的命中不触发；过载命中（单次注热 ≥ 20）触发（计数 +{k.EmgRuleCountOfKey(key) - c0}）");
            }
            using (CombatKernel k = NewKernel())
            {
                // 移动中按距离滴：比静止块数多
                int m = Unit(k, CombatFaction.Player, new double2(0, 0), hp: 5000f);
                Coat(k, m, "Acid");
                k.ApplyStatus(m, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
                k.IssueCommand(m, CombatCommandKind.Move, new double2(30, 0), 0, 0.5f, 0f, 0f, false);
                StepN(k, 180);
                int moving = Blocks(k, EmgMatterConst.Fire);
                Expect(moving >= 4, $"移动中按距离滴落：3 秒走约 12 米留下 {moving} 块明火（静止 3 秒只有 3 块）");
            }
            using (CombatKernel k = NewKernel())
            {
                int proof = k.AddProfile(new CombatBehaviorProfile { ImmuneMask = Bit("Acid"), AcidResist = 1f });
                int u = k.Spawn(new CombatSpawn { ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live, Position = new double2(0, 0), Home = new double2(0, 0), Radius = 0.5f, Health = 400f, MaxHealth = 400f, Weapon = -1, BehaviorProfile = proof, Priority = 1 });
                int c0 = k.EmgRuleCountOfKey(key);
                Coat(k, u, "Acid");
                k.ApplyStatus(u, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
                Expect(k.EmgRuleCountOfKey(key) == c0 && !Has(k, u, "Acid"), "澄净（免疫酸蚀位、腐蚀抗性 1）不沾酸：腐蚀灼烧不结算（ADR-REACT-001 第 5 节新口径：按抗性结算）");
            }
        }

        // ── E 淤塞 C07 ──────────────────────────────────────────────────────

        private static void CheckMudify()
        {
            Line("  · E. 淤塞（单位层）：扬尘 + 浸湿 → 减速 35%、4 秒内不导电（电弧跳不到）；两种沾染都消耗；只有一种不发生");
            int key = Key("reaction_mudify");
            using CombatKernel k = NewKernel();
            int u = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            int only = Unit(k, CombatFaction.Hostile, new double2(40, 0));
            Coat(k, u, "Earth");
            Coat(k, only, "Earth");
            int c0 = k.EmgRuleCountOfKey(key);
            Coat(k, u, "Wet");
            bool slowed = k.TryGetStatus(u, out uint m, out _, out _, out float slow, out _) && (m & CombatConst.StatusBitZoneSlow) != 0u;
            Expect(k.EmgRuleCountOfKey(key) == c0 + 1 && slowed && Mathf.Abs(slow - 0.35f) < 0.01f && !Has(k, u, "Earth") && !Has(k, u, "Wet") && Has(k, only, "Earth"),
                $"淤塞：计数 +{k.EmgRuleCountOfKey(key) - c0}、减速 {slow:0.##}、扬尘与浸湿都消耗；只沾扬尘不发生");
            // 跳不到：旁边另一个沾浸湿的单位通电（C03 电弧跳 4 米内导体），泥浆单位不得电荷，普通单位得电荷
            int wet = Unit(k, CombatFaction.Hostile, new double2(2, 0));
            int plain = Unit(k, CombatFaction.Hostile, new double2(0, 2));
            Coat(k, wet, "Wet");
            Elec(k, wet);
            float muddy = k.AccumOf(u, Charge), control = k.AccumOf(plain, Charge);
            Expect(muddy <= 0f && control > 0f, $"泥浆期间电弧跳不到它：电弧给泥浆单位 {muddy:0.##}、给旁边的普通单位 {control:0.##}");
            // 传导图本身：泥浆单位站在带电水洼里不得电、不中继（E14 与 BFS 两条路），也不能经导线得电；同一水洼里的普通单位得电
            using (CombatKernel p = NewKernel())
            {
                int mudIn = Unit(p, CombatFaction.Hostile, new double2(0.5, 0));
                int plainIn = Unit(p, CombatFaction.Hostile, new double2(-0.5, 0));
                p.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 3f, 20f, 60f);
                StepN(p, 20); // 先站进水洼（传导图按上一步的接触掩码选单位）
                Coat(p, mudIn, "Earth");
                Coat(p, mudIn, "Wet");
                p.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
                bool m0 = false, p0 = false;
                for (int i = 0; i < 30; i++) // 电荷到阈值进麻痹时清零，所以两个都算
                {
                    StepN(p, 1);
                    m0 |= p.AccumOf(mudIn, Charge) > 0f || p.InPhase(mudIn, Charge);
                    p0 |= p.AccumOf(plainIn, Charge) > 0f || p.InPhase(plainIn, Charge);
                }
                Expect(!m0 && p0, $"泥浆单位站在带电水洼里：传导图不给它电（{m0}），同一水洼的普通单位得电（{p0}）");
            }
            StepN(k, 60 * 5);
            int wet2 = Unit(k, CombatFaction.Hostile, new double2(-2, 0));
            Coat(k, wet2, "Wet");
            float before = k.AccumOf(u, Charge);
            Elec(k, wet2);
            Expect(k.AccumOf(u, Charge) > before, $"4 秒后泥浆结束，电弧又能跳到它（{before:0.##} → {k.AccumOf(u, Charge):0.##}）");
        }

        // ── F 热震 P02 ──────────────────────────────────────────────────────

        private static void CheckThermalShock()
        {
            Line("  · F. 热震：冻结 + 单次注热 ≥ 30 → 主结果崩出装甲破片 4 片，破甲相态只作附带；只有一种配料不发生");
            int key = Key("reaction_thermalshock");
            using CombatKernel k = NewKernel();
            int a = Unit(k, CombatFaction.Hostile, new double2(0, 0));
            k.ChillUnit(a, 1f);
            int c0 = k.EmgRuleCountOfKey(key);
            int f0 = Fragments(k);
            k.InjectUnitHeat(a, 40f);
            int frags = Fragments(k) - f0;
            Expect(frags == 4 && !k.InPhase(a, Chill) && k.InPhase(a, Corrode) && k.EmgRuleCountOfKey(key) == c0 + 1,
                $"冻结中注热 40：破片 {frags} 片（主结果）、立即解冻、进入破甲（附带）、计数 +{k.EmgRuleCountOfKey(key) - c0}");
            int notFrozen = Unit(k, CombatFaction.Hostile, new double2(40, 0));
            int small = Unit(k, CombatFaction.Hostile, new double2(80, 0));
            k.ChillUnit(small, 1f);
            int f1 = Fragments(k), c1 = k.EmgRuleCountOfKey(key);
            k.InjectUnitHeat(notFrozen, 40f);
            k.InjectUnitHeat(small, 10f);
            Expect(Fragments(k) == f1 && k.EmgRuleCountOfKey(key) == c1 && k.InPhase(small, Chill), "对照：没冻结的注热 40、冻结的注热 10 都不崩破片");
        }

        // ── G 斑块行：胶结 / 乳化 / 淤塞 ────────────────────────────────────

        private static void CheckPatchRows()
        {
            Line("  · G. 斑块层：胶结（聚合胶 × 水 / 冷却液，胀大）、乳化（油 × 酸，污泥）、淤塞（水 / 冷却液 × 粉尘，污泥）；只有一种配料不发生");
            (int count, float product) Run(string id, byte a, byte b, byte product, bool hasA, bool hasB)
            {
                using CombatKernel k = NewKernel();
                if (hasA)
                {
                    k.SpawnPatch(a, new double2(0, 0), 2f, 3f, 30f);
                }
                if (hasB)
                {
                    k.SpawnPatch(b, new double2(1, 0), 2f, 3f, 30f);
                }
                int c0 = k.EmgRuleCountOfKey(Key(id));
                StepN(k, 60);
                return (k.EmgRuleCountOfKey(Key(id)) - c0, Sum(k, product, new double2(0, 0), 6f));
            }
            foreach ((string id, byte a, byte b, byte product, string name) in new[]
            {
                ("reaction_syrup", EmgMatterConst.Polymer, EmgMatterConst.Water, EmgMatterConst.Polymer, "胶结（水）"),
                ("reaction_syrup", EmgMatterConst.Polymer, EmgMatterConst.Coolant, EmgMatterConst.Polymer, "胶结（冷却液）"),
                ("reaction_dissolve", EmgMatterConst.Oil, EmgMatterConst.Acid, EmgMatterConst.Sludge, "乳化"),
                ("reaction_mudify", EmgMatterConst.Water, EmgMatterConst.Dust, EmgMatterConst.Sludge, "淤塞（水）"),
                ("reaction_mudify", EmgMatterConst.Coolant, EmgMatterConst.Dust, EmgMatterConst.Sludge, "淤塞（冷却液）"),
            })
            {
                var both = Run(id, a, b, product, true, true);
                var onlyA = Run(id, a, b, product, true, false);
                var onlyB = Run(id, a, b, product, false, true);
                bool productOk = product == EmgMatterConst.Polymer ? both.product > onlyA.product : both.product > 0f;
                Expect(both.count >= 1 && productOk && onlyA.count == 0 && onlyB.count == 0,
                    $"{name}：两种都在结算 {both.count} 次、产物 {both.product:0.#} 份；只有 A {onlyA.count} 次 / 只有 B {onlyB.count} 次");
            }
        }

        // ── H 玻化 E04 ──────────────────────────────────────────────────────

        private static void CheckVitrify()
        {
            Line("  · H. 玻化：粉尘（浓度 < 0.6）× 受热 ≥ 300 → 玻化层；浓尘走尘爆不玻化；不加热不发生");
            int key = Key("reaction_vitrify");
            using CombatKernel k = NewKernel();
            k.SpawnPatch(EmgMatterConst.Dust, new double2(0, 0), 2f, 3f, 30f);
            StepN(k, 2);
            int steps = 0;
            float conc = 1f;
            for (; steps < 900 && conc >= 0.6f; steps++)
            {
                StepN(k, 1);
                conc = 0f;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Dust)
                    {
                        conc = Mathf.Max(conc, z.Conc);
                    }
                }
            }
            int c0 = k.EmgRuleCountOfKey(key);
            float glass0 = Sum(k, EmgMatterConst.Glass, new double2(0, 0), 10f);
            bool quiet = glass0 <= 0f && conc > 0f;
            k.InjectPatchEnergy(new double2(0, 0), 3f, EmgConst.PortHeat, 400f, 1);
            StepN(k, 40);
            Expect(quiet && k.EmgRuleCountOfKey(key) > c0 && Sum(k, EmgMatterConst.Glass, new double2(0, 0), 10f) > 0f,
                $"粉尘摊开到浓度 {conc:0.##}（第 {steps} 步，此前没有玻化层），注热 400 后玻化 {Sum(k, EmgMatterConst.Glass, new double2(0, 0), 10f):0.#} 份");
            using CombatKernel d = NewKernel();
            d.SpawnPatch(EmgMatterConst.Dust, new double2(0, 0), 2f, 3f, 30f);
            StepN(d, 2);
            int c1 = d.EmgRuleCountOfKey(key);
            d.InjectPatchEnergy(new double2(0, 0), 3f, EmgConst.PortHeat, 400f, 1);
            StepN(d, 20);
            Expect(d.EmgRuleCountOfKey(key) == c1 && Sum(d, EmgMatterConst.Glass, new double2(0, 0), 10f) <= 0f, "对照：浓尘（≥ 0.6）受热走尘爆，不玻化");
        }

        // ── I 回路溶断 W01 ──────────────────────────────────────────────────

        private static void CheckWreckAcid()
        {
            Line("  · I. 回路溶断：残骸在酸斑块里 ≥ 2 秒开始溶解，归零变碎屑（0.5 × 质量）；酸外的残骸不溶；有残骸就能打出（不再要“回收”标签）");
            int key = Key("reaction_hemolysis");
            using CombatKernel k = NewKernel();
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 20f, 60f);
            StepN(k, 2);
            int w = AddWreck(k, new double2(0, 0), 40f, 6f);
            int dry = AddWreck(k, new double2(30, 0), 40f, 6f);
            int c0 = k.EmgRuleCountOfKey(key);
            StepN(k, 100);
            bool holding = Mathf.Approximately(WreckHp(k, w), 40f);
            StepN(k, 60);
            float melting = WreckHp(k, w);
            StepN(k, 60 * 5);
            Expect(holding && melting < 40f && melting > 0f && WreckHp(k, w) <= 0f && WreckHp(k, dry) == 40f && k.EmgRuleCountOfKey(key) == c0 + 1 && Sum(k, EmgMatterConst.Debris, new double2(0, 0), 2f) >= 2.5f,
                $"满 2 秒前耐久不变（{holding}），之后溶解（{melting:0.#}）、归零变碎屑 {Sum(k, EmgMatterConst.Debris, new double2(0, 0), 2f):0.#} 份（0.5 × 6）；酸外残骸 {WreckHp(k, dry):0}；计数 +{k.EmgRuleCountOfKey(key) - c0}");
            // W01 先于 W02：同时在酸与污泥里只溶解，不崩塌
            using CombatKernel b = NewKernel();
            b.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 20f, 60f);
            b.SpawnPatch(EmgMatterConst.Sludge, new double2(0, 40), 2f, 20f, 60f);
            StepN(b, 2);
            AddWreck(b, new double2(0, 0), 40f, 6f);
            int sepsis0 = b.EmgRuleCountOfKey(Key("reaction_sepsis"));
            StepN(b, 60 * 4);
            Expect(b.EmgRuleCountOfKey(Key("reaction_sepsis")) == sepsis0 && Fragments(b) == 0, "优先级：酸里的残骸按 W01 溶解，不崩破片");
        }

        // ── J 连锁锈蚀 W02 ──────────────────────────────────────────────────

        private static void CheckWreckSludge()
        {
            Line("  · J. 连锁锈蚀：残骸在污泥里 ≥ 3 秒崩塌成破片 4 片 + 碎屑；≥ 1 秒且单次动能 ≥ 15 提前崩；没有接触传播；污泥外同样的破片不崩；锈蚀破片碎不动冻结目标");
            int key = Key("reaction_sepsis");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Sludge, new double2(0, 0), 3f, 20f, 60f);
                StepN(k, 2);
                int w = AddWreck(k, new double2(0, 0), 40f, 6f);
                StepN(k, 150); // 2.5 秒：还没满
                bool holding = WreckHp(k, w) > 0f && Fragments(k) == 0;
                int late = AddWreck(k, new double2(0.9, 0), 40f, 6f); // 紧贴着、停留才 0.5 秒
                int c0 = k.EmgRuleCountOfKey(key);
                int steps = 0;
                while (WreckHp(k, w) > 0f && steps < 60)
                {
                    StepN(k, 1);
                    steps++;
                }
                int frags = Fragments(k);
                bool noContact = WreckHp(k, late) > 0f;
                Expect(holding && WreckHp(k, w) <= 0f && frags == 4 && k.EmgRuleCountOfKey(key) == c0 + 1 && Sum(k, EmgMatterConst.Debris, new double2(0, 0), 2f) > 1f && noContact,
                    $"满 3 秒崩塌：破片 {frags} 片、碎屑 {Sum(k, EmgMatterConst.Debris, new double2(0, 0), 2f):0.#} 份（0.3 × 6）；紧贴的残骸（停留不足 1 秒）没有被接触传播（耐久 {WreckHp(k, late):0}）");
            }
            (float hp, int count) Shot(bool sludge, int dwellSteps)
            {
                using CombatKernel k = NewKernel();
                if (sludge)
                {
                    k.SpawnPatch(EmgMatterConst.Sludge, new double2(0, 0), 3f, 20f, 60f);
                }
                StepN(k, 2);
                int w = AddWreck(k, new double2(0, 0), 400f, 6f);
                StepN(k, dwellSteps);
                int c0 = k.EmgRuleCountOfKey(key);
                k.SpawnFragmentForTests(new double2(-1.2, 0), new float2(1f, 0f), 0, CombatFaction.Neutral, 6f, 14f, 3f); // 6 × 2.5 = 15
                StepN(k, 20);
                return (WreckHp(k, w), k.EmgRuleCountOfKey(key) - c0);
            }
            var inSludge = Shot(true, 80);
            var tooSoon = Shot(true, 15);
            var outside = Shot(false, 80);
            Expect(inSludge.hp <= 0f && inSludge.count == 1 && tooSoon.hp > 0f && tooSoon.count == 0 && outside.hp > 0f && outside.count == 0,
                $"锈蚀破片（动能 15）：打到污泥里停留 ≥ 1 秒的残骸崩塌（{inSludge.count} 次）；停留不足 1 秒不崩（耐久 {tooSoon.hp:0}）；污泥外不崩（耐久 {outside.hp:0}）");
            using (CombatKernel k = NewKernel())
            {
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                k.ChillUnit(foe, 1f);
                int c0 = k.EmgRuleCountOfKey(Key("reaction_shatter"));
                k.SpawnFragmentForTests(new double2(-1.5, 0), new float2(1f, 0f), 0, CombatFaction.Player, 6f, 14f, 3f);
                StepN(k, 20);
                Expect(k.InPhase(foe, Chill) && k.EmgRuleCountOfKey(Key("reaction_shatter")) == c0, "锈蚀破片动能 15 < 24 × 钢脆性 1：碎不动冻结目标");
            }
        }

        // ── K 湮灭 F01 ──────────────────────────────────────────────────────

        private static void CheckAnnihilate()
        {
            Line("  · K. 湮灭：敌对偏折场里完整度 ≤ 15% → 压溃起爆（不留残骸、附带 40）；完整度高 / 同阵营场 / 固定目标不发生；被处决击毁后不再结算");
            int key = Key("reaction_annihilate");
            using (CombatKernel k = NewKernel())
            {
                int me = Unit(k, CombatFaction.Player, new double2(-50, 0));
                int low = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                int side = Unit(k, CombatFaction.Hostile, new double2(2, 0));
                int high = Unit(k, CombatFaction.Hostile, new double2(30, 0));
                k.SetHealth(low, 50f, 400f, true); // 12.5%
                k.SetHealth(high, 80f, 400f, true); // 20%
                k.SpawnField(EmgFieldConst.PortDeflect, 0, 0, 1.5f, 5f, me, CombatFaction.Player);
                k.SpawnField(EmgFieldConst.PortDeflect, 30, 0, 1.5f, 5f, me, CombatFaction.Player);
                int c0 = k.EmgRuleCountOfKey(key);
                float side0 = Hp(k, side);
                StepN(k, 10);
                Expect(!Alive(k, low) && k.EmgRuleCountOfKey(key) == c0 + 1 && k.WreckCount == 0 && side0 - Hp(k, side) >= 39.9f && Alive(k, high),
                    $"12.5% 的单位被压溃（残骸 {k.WreckCount}）、旁边挨 {side0 - Hp(k, side):0.#}（附带 40）；20% 的单位不发生");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Unit(k, CombatFaction.Hostile, new double2(-50, 0));
                int ally = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                int tower = Unit(k, CombatFaction.Player, new double2(30, 0), kind: CombatUnitKind.Structure);
                int enemyOfTower = Unit(k, CombatFaction.Hostile, new double2(30, 30));
                k.SetHealth(ally, 40f, 400f, true);
                k.SetHealth(tower, 40f, 400f, true);
                k.SpawnField(EmgFieldConst.PortDeflect, 0, 0, 1.5f, 5f, me, CombatFaction.Hostile);
                k.SpawnField(EmgFieldConst.PortDeflect, 30, 0, 1.5f, 5f, enemyOfTower, CombatFaction.Hostile);
                int c0 = k.EmgRuleCountOfKey(key);
                StepN(k, 10);
                Expect(Alive(k, ally) && Alive(k, tower) && k.EmgRuleCountOfKey(key) == c0, "同阵营场不压溃；不可推动的固定目标（建筑）不压溃（待用户复核：防止首领 15% 被秒）");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Unit(k, CombatFaction.Player, new double2(-50, 0));
                int low = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                k.SetHealth(low, 50f, 400f, true);
                k.SpawnField(EmgFieldConst.PortDeflect, 0, 0, 1.5f, 5f, me, CombatFaction.Player);
                int c0 = k.EmgRuleCountOfKey(key);
                k.Kill(low, me); // 处决击毁在先
                StepN(k, 10);
                Expect(!Alive(k, low) && k.EmgRuleCountOfKey(key) == c0, "结算顺序：已被处决 / 别的伤害击毁的单位不再压溃、不再起爆");
            }
        }

        // ── L 其余移交项 ────────────────────────────────────────────────────

        private static void CheckHandovers()
        {
            Line("  · L. 移交项：光束不碎 / 实弹碎；电击 = 一次注电不留标签；磁化吸碎屑；带火穿油立即爆燃；聚合胶减速；烟 / 蒸汽单位失锁；酸沼吸水、通电");
            using (CombatKernel k = NewKernel())
            {
                int beam = k.AddWeapon(Instant(100f, beam: true));
                int gun = k.AddWeapon(Instant(100f));
                int m1 = Unit(k, CombatFaction.Player, new double2(0, 0), beam);
                int m2 = Unit(k, CombatFaction.Player, new double2(40, 0), gun);
                int f1 = Unit(k, CombatFaction.Hostile, new double2(0, 5));
                int f2 = Unit(k, CombatFaction.Hostile, new double2(40, 5));
                k.ChillUnit(f1, 1f);
                k.ChillUnit(f2, 1f);
                k.FireAt(m1, f1, k.Time);
                k.FireAt(m2, f2, k.Time);
                StepN(k, 2);
                Expect(k.InPhase(f1, Chill) && !k.InPhase(f2, Chill), "DEBT-EMG2PHS01-01：光束（载体动能系数 0）打冻结目标不碎；同伤害实弹碎");
            }
            using (CombatKernel k = NewKernel())
            {
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                Elec(k, u);
                float q = k.AccumOf(u, Charge);
                StepN(k, 60);
                Expect(Mathf.Abs(q - 0.5f) < 0.01f && !Has(k, u, "Shock"), $"DEBT-EMG2REACT01-03：电击标签 = 一次注电（phase_charge.tagGain = {q:0.##}），1 秒后身上不留电击标签");
            }
            using (CombatKernel k = NewKernel())
            {
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0));
                k.AddAccum(u, Mag, 1f);
                k.SpawnPatch(EmgMatterConst.Debris, new double2(3, 0), 0.5f, 2f, 30f);
                StepN(k, 2);
                double far = 0;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Debris)
                    {
                        far = math.distance(z.Pos, new double2(0, 0));
                    }
                }
                StepN(k, 60);
                double near = far;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Debris)
                    {
                        near = math.distance(z.Pos, new double2(0, 0));
                    }
                }
                Expect(k.InPhase(u, Mag) && near < far - 0.3, $"DEBT-EMG2PHS01-02：磁化单位吸附 4 米内的碎屑（距离 {far:0.##} → {near:0.##}，p3 = 1 米 / 秒）");
            }
            using (CombatKernel k = NewKernel())
            {
                // 带火弹体穿过油斑块：先穿明火带火，再穿油，整片立即爆燃
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(40, 0));
                k.SpawnPatch(EmgMatterConst.Fire, new double2(3, 0), 1f, 1f, 30f);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(10, 0), 1f, 3f, 30f);
                StepN(k, 2);
                int c0 = k.EmgRuleCountOfKey(Key("reaction_deflagrate"));
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                Expect(k.EmgRuleCountOfKey(Key("reaction_deflagrate")) > c0 && Sum(k, EmgMatterConst.Oil, new double2(10, 0)) <= 0f,
                    "DEBT-EMG2FLD01-01 / FGT-EMG-036：带火弹体穿过油斑块，整片立即爆燃");
            }
            using (CombatKernel k = NewKernel())
            {
                // 聚合胶：穿过的弹体减速；对照无胶
                float Reach(bool glue)
                {
                    using CombatKernel g = NewKernel();
                    int me = Unit(g, CombatFaction.Player, new double2(0, 0), g.AddWeapon(Flight(1f)));
                    int foe = Unit(g, CombatFaction.Hostile, new double2(60, 0));
                    if (glue)
                    {
                        g.SpawnPatch(EmgMatterConst.Polymer, new double2(4, 0), 1.5f, 3f, 30f);
                    }
                    StepN(g, 2);
                    g.FireAt(me, foe, g.Time);
                    StepN(g, 30);
                    float x = 0f;
                    for (int i = 0; i < g.ProjectileCount; i++)
                    {
                        if (g.TryGetProjectile(i, out CombatProjectile p) && p.Owner == me)
                        {
                            x = Mathf.Max(x, (float)p.Pos.x);
                        }
                    }
                    return x;
                }
                float plain = Reach(false), slowed = Reach(true);
                Expect(plain > 0f && slowed > 0f && slowed < plain - 0.2f, $"DEBT-EMG2FLD01-01：穿过聚合胶的弹体减速（0.5 秒飞到 {slowed:0.##}，无胶 {plain:0.##}）");
            }
            // DEBT-EMG1FW01-03 余部：排在触发器之后的处决由子弹药在落点结算，不再写在本机出手上
            int Executed(bool withExecute)
            {
                using CombatKernel g = NewKernel();
                var w = Flight(1f);
                w.Program = CombatSite.RegisterProgram(g, EmgProgramCompiler.Compile(Array.Empty<string>(), withExecute ? new[] { "fw_split", "fw_execute" } : new[] { "fw_split" }, FirmwareCarrier.Projectile));
                int me = Unit(g, CombatFaction.Player, new double2(0, 0), g.AddWeapon(w));
                int front = Unit(g, CombatFaction.Hostile, new double2(0, 6), hp: 1000f);
                var low = new List<int>();
                for (int i = -4; i <= 4; i++)
                {
                    int e = Unit(g, CombatFaction.Hostile, new double2(i * 1.5, 11), hp: 400f);
                    g.SetHealth(e, 40f, 400f, true); // 10% ≤ 处决阈值
                    low.Add(e);
                }
                g.FireAt(me, front, g.Time);
                StepN(g, 120);
                return low.Count(e => !Alive(g, e));
            }
            int withExec = Executed(true), noExec = Executed(false);
            Expect(withExec >= 1 && noExec == 0, $"[分裂, 处决]：子弹药命中 10% 血的目标当场处决 {withExec} 个；对照只分裂 {noExec} 个");
            // 外部血量目标（首领 / 玩家机器）：致死请求不是又一次命中，不得重入处决写端（曾无限递归）；一枚子弹药只发一次请求
            using (CombatKernel k = NewKernel())
            {
                var w = Flight(1f);
                w.Program = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", "fw_execute" }, FirmwareCarrier.Projectile));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w));
                int front = Unit(k, CombatFaction.Hostile, new double2(0, 6), hp: 1000f);
                int boss = k.Spawn(new CombatSpawn
                {
                    ExtKey = 77, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                    Flags = Live | CombatUnitFlags.Report | CombatUnitFlags.ExternalHealth, Position = new double2(0, 11), Home = new double2(0, 11), Radius = 0.5f, Speed = 4f,
                    Health = 400f, MaxHealth = 400f, Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f, ArmorFacing = new float2(0f, -1f),
                });
                k.SetUnitProfile(boss, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
                k.SetHealth(boss, 40f, 400f, true);
                k.FireAt(me, front, k.Time);
                var lethal = new List<float>();
                for (int step = 0; step < 120; step++)
                {
                    StepN(k, 1);
                    CombatEvent[] ev = k.DrainGameplay(4096, out int n);
                    for (int e = 0; e < n; e++)
                    {
                        if (ev[e].Kind == CombatEventKind.DamageRequest && ev[e].Unit == boss && ev[e].Value >= 39.9f)
                        {
                            lethal.Add(ev[e].Value);
                        }
                    }
                }
                Expect(lethal.Count == 1 && Alive(k, boss), $"[分裂, 处决] 打外部血量目标：不递归崩溃，一枚子弹药一发致死请求 {lethal.Count} 次（= 镜像血量），内核不自行判死（{Alive(k, boss)}）");
            }
            // [分裂, 集群协议]：子弹药不开火，evt.fire 门控的伴飞在落地时放出；对照只分裂不放伴飞
            int DronesAfter(params string[] fw)
            {
                using CombatKernel g = NewKernel();
                var w = Flight(1f);
                w.Program = CombatSite.RegisterProgram(g, EmgProgramCompiler.Compile(Array.Empty<string>(), fw, FirmwareCarrier.Projectile));
                int me = Unit(g, CombatFaction.Player, new double2(0, 0), g.AddWeapon(w));
                int front = Unit(g, CombatFaction.Hostile, new double2(0, 6), hp: 5000f);
                for (int i = -4; i <= 4; i++)
                {
                    Unit(g, CombatFaction.Hostile, new double2(i * 1.5, 11), hp: 5000f);
                }
                g.FireAt(me, front, g.Time);
                StepN(g, 120);
                return (int)g.Counters.DronesLaunched;
            }
            int droneOn = DronesAfter("fw_split", "fw_swarm"), droneOff = DronesAfter("fw_split");
            Expect(droneOn >= 1 && droneOff == 0, $"[分裂, 集群协议]：子弹药落地放出伴飞 {droneOn} 架；对照只分裂 {droneOff} 架");
            // [分裂, 余震]：子弹药命中后在落点延迟再击中；[子母弹, 追射]：子弹药命中由发射者补射。对照去掉后一条（FgEmgReclaimSelfCheck B3 已覆盖 [子母弹, 桥接 / 蔓延]）
            float Landed(params string[] fw)
            {
                using CombatKernel g = NewKernel();
                var w = Flight(4f);
                w.Program = CombatSite.RegisterProgram(g, EmgProgramCompiler.Compile(Array.Empty<string>(), fw, FirmwareCarrier.Projectile));
                int me = Unit(g, CombatFaction.Player, new double2(0, 0), g.AddWeapon(w));
                int front = Unit(g, CombatFaction.Hostile, new double2(0, 6), hp: 5000f);
                float sum0 = Hp(g, front);
                var row = new List<int> { front };
                for (int i = -4; i <= 4; i++)
                {
                    row.Add(Unit(g, CombatFaction.Hostile, new double2(i * 1.5, 11), hp: 5000f));
                }
                float total0 = row.Sum(e => Hp(g, e));
                g.FireAt(me, front, g.Time);
                StepN(g, 240);
                _ = sum0;
                return total0 - row.Sum(e => Hp(g, e));
            }
            float echoOn = Landed("fw_split", "fw_echo"), echoOff = Landed("fw_split");
            float followOn = Landed("fw_cluster", "fw_followup"), followOff = Landed("fw_cluster");
            Expect(echoOn > echoOff + 0.01f && followOn > followOff + 0.01f,
                $"子节点落点结算：[分裂, 余震] 总伤害 {echoOn:0.#}（只分裂 {echoOff:0.#}）；[子母弹, 追射] {followOn:0.#}（只子母弹 {followOff:0.#}）");
            (bool smoke, bool steam, bool steamClose, bool open) = (Locked(EmgMatterConst.Smoke, 15), Locked(EmgMatterConst.Steam, 15), Locked(EmgMatterConst.Steam, 4), Locked(0, 15));
            Expect(!smoke && !steam && steamClose && open, $"DEBT-EMG2FLD01-02 / FG-GAP-053：站在烟 / 蒸汽里的单位不被区外锁定（烟 {smoke}、蒸汽 {steam}）；蒸汽里 6 米内仍可锁（{steamClose}）；对照开阔地可锁（{open}）");
            using (CombatKernel k = NewKernel())
            {
                // 酸沼：水斑块被吸收、不把沼泽冲淡；能通电
                k.SetAcidMarsh(new List<float4> { new float4(0, 0, 0, 20) }, 0.02f, 0.5f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(3, 0), 1f, 2f, 60f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(60, 0), 1f, 2f, 60f);
                StepN(k, 2);
                float a0 = Sum(k, EmgMatterConst.Water, new double2(3, 0));
                StepN(k, 120);
                Expect(a0 > 0f && Sum(k, EmgMatterConst.Water, new double2(3, 0)) < a0 * 0.8f && k.AcidShapeCount == 1 && Sum(k, EmgMatterConst.Water, new double2(60, 0)) > 0f,
                    $"DEBT-EMG2MAT02-05（FG19B §7 #12）：沼里的水斑块被吸收（{a0:0.##} → {Sum(k, EmgMatterConst.Water, new double2(3, 0)):0.##}），沼泽不被冲淡；沼外的水不受影响");
                int inMarsh = Unit(k, CombatFaction.Hostile, new double2(2, 2));
                int mate = Unit(k, CombatFaction.Hostile, new double2(5, 2));
                int farIn = Unit(k, CombatFaction.Hostile, new double2(15, 2));
                int c0 = k.EmgRuleCountOfKey(Key("reaction_electrolysis"));
                k.InjectMarshElecForTests(new double2(2, 2), 0.3f);
                float q03 = k.AccumOf(mate, Charge);
                int c03 = k.EmgRuleCountOfKey(Key("reaction_electrolysis"));
                k.InjectMarshElecForTests(new double2(2, 2), 0.5f);
                StepN(k, 2);
                Expect(q03 > 0f && c03 == c0 && k.AccumOf(mate, Charge) > q03 && k.AccumOf(farIn, Charge) <= 0f && k.EmgRuleCountOfKey(Key("reaction_electrolysis")) > c0 && Sum(k, EmgMatterConst.Debris, new double2(2, 2), 4f) > 0f,
                    $"酸沼通电限注入点 8 米：同沼 3 米的单位得电荷 {k.AccumOf(mate, Charge):0.##}、13 米外的 {k.AccumOf(farIn, Charge):0.##}；单次 0.3 不电解、0.5 ≥ 0.4 电解出碎屑（E16，FGR-EMG-015 规则三）");
            }
        }

        /// <summary>单位站在某种物质里、观察者在 <paramref name="dist"/> 米外：观察者能否索敌（FindTarget 路径：驻守自动出手）。</summary>
        private static bool Locked(byte matter, double dist)
        {
            using CombatKernel k = NewKernel();
            int gun = k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 40f, Damage = 1f, Cooldown = 0.2f,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            });
            int me = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.HoldFire, Flags = Live, Position = new double2(0, 0), Home = new double2(0, 0),
                Radius = 0.5f, Speed = 0f, Health = 400f, MaxHealth = 400f, Weapon = gun, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f, ArmorFacing = new float2(0f, 1f),
            });
            int foe = Unit(k, CombatFaction.Hostile, new double2(dist, 0), hp: 5000f);
            if (matter != 0)
            {
                k.SpawnPatch(matter, new double2(dist, 0), 3f, 3f, 30f);
            }
            StepN(k, 30); // 斑块铺上、单位的物质掩码更新之后才算（这之前开的第一枪不算）
            float hp0 = Hp(k, foe);
            StepN(k, 90);
            _ = me;
            return Hp(k, foe) < hp0;
        }

        // ── M 存读档 ────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · M. 存读档：段 21（C05 / C07 计时）、光束标记、弹体减速量、残骸驻留进快照；读档后续跑与不存档连续跑一致");
            using CombatKernel a = NewKernel();
            int m = Unit(a, CombatFaction.Player, new double2(0, 0), a.AddWeapon(Flight(1f, 6f, 6f)), 5000f);
            int w = a.AddWeapon(Instant(5f, beam: true));
            int trail = Unit(a, CombatFaction.Hostile, new double2(30, 0), hp: 5000f);
            int mud = Unit(a, CombatFaction.Hostile, new double2(30, 30), hp: 5000f);
            int foe = Unit(a, CombatFaction.Hostile, new double2(60, 0), hp: 5000f);
            a.SpawnPatch(EmgMatterConst.Polymer, new double2(2, 0), 1.5f, 3f, 30f);
            a.SpawnPatch(EmgMatterConst.Sludge, new double2(-40, 0), 3f, 20f, 60f);
            StepN(a, 2);
            int wreck = AddWreck(a, new double2(-40, 0), 40f, 6f);
            Coat(a, trail, "Acid");
            a.ApplyStatus(trail, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
            Coat(a, mud, "Earth");
            Coat(a, mud, "Wet");
            a.FireAt(m, foe, a.Time);
            StepN(a, 18); // 弹体正穿过聚合胶
            bool slowQ = false;
            for (int i = 0; i < a.ProjectileCount; i++)
            {
                slowQ |= a.TryGetProjectile(i, out CombatProjectile p) && p.Owner == m && p.SlowQ != 0;
            }
            int wi = a.WreckIndexOf(wreck);
            a.TryGetWreck(wi, out EmgWreck w0);
            byte[] snap = a.Serialize();
            ulong h0 = a.StateHash();
            using CombatKernel b = NewKernel();
            CombatLoadResult r = b.Load(snap);
            b.TryGetWreck(b.WreckIndexOf(wreck), out EmgWreck w1);
            b.TryGetWeapon(w, out CombatWeapon wb);
            bool sameDwell = w0.DwellMatter == EmgMatterConst.Sludge && w1.DwellMatter == w0.DwellMatter && Math.Abs(w1.DwellSince - w0.DwellSince) < 1e-9;
            Expect(r == CombatLoadResult.Ok && b.StateHash() == h0 && slowQ && sameDwell && wb.BeamHit == 1,
                $"快照往返 {r}：状态哈希一致，弹体减速量 {slowQ}，残骸驻留 {sameDwell}，光束标记 {wb.BeamHit}");
            StepN(a, 120);
            StepN(b, 120);
            float sa0 = Sum(a, EmgMatterConst.Fire, new double2(30, 0));
            bool mudSlowA = a.TryGetStatus(mud, out uint ma, out _, out _, out _, out _) && (ma & CombatConst.StatusBitZoneSlow) != 0u;
            bool mudSlowB = b.TryGetStatus(mud, out uint mb, out _, out _, out _, out _) && (mb & CombatConst.StatusBitZoneSlow) != 0u;
            Expect(a.StateHash() == b.StateHash() && sa0 > 0f && mudSlowA == mudSlowB && Mathf.Approximately(Sum(a, EmgMatterConst.Fire, new double2(30, 0)), Sum(b, EmgMatterConst.Fire, new double2(30, 0))) && WreckHp(a, wreck) == WreckHp(b, wreck),
                "读档后续跑 2 游戏秒：滴火、泥浆、残骸崩塌与连续跑完全一致（状态哈希相等）");
            // 迁入斑块被规则转化（油迹被点燃）后立刻存读：种类码必须回到物质斑块，否则整份快照读不回（DEBT-EMG2MAT02-03 的审查用例）
            using (CombatKernel c = NewKernel())
            {
                int crawler = Unit(c, CombatFaction.Hostile, new double2(30, 30));
                c.SpawnSlickForTests(crawler, new double2(0, 0), 1.5f, 30f, Bit("Oil"), 10f);
                int before = c.LegacyPatchCount;
                c.SpawnPatch(EmgMatterConst.Fire, new double2(0.9, 0), 1.2f, 1f, 30f);
                StepN(c, 30);
                int after = c.LegacyPatchCount;
                byte[] burn = c.Serialize();
                using CombatKernel c2 = NewKernel();
                CombatLoadResult rc = c2.Load(burn);
                StepN(c, 60);
                StepN(c2, 60);
                Expect(before == 1 && after == 0 && rc == CombatLoadResult.Ok && c2.StateHash() == c.StateHash(),
                    $"迁入油迹被点燃（迁入斑块 {before} → {after}）后存读：{rc}，续跑哈希一致 {c2.StateHash() == c.StateHash()}");
            }
            // 坏值拒绝：段 21 负计时 → 整份拒绝由 ParseMatterSegment 校验（快照自描述段的坏值用例在 FgEmergenceKernelSelfCheck）
        }

        // ── N 暂停与倍速 ────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            Line("  · N. 暂停与 0.5x～3x：C05 滴火、C07 泥浆按游戏秒走，暂停不走");
            var ok = new List<string>();
            foreach (float speed in new[] { 0f, 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                int u = Unit(k, CombatFaction.Hostile, new double2(0, 0), hp: 5000f);
                int v = Unit(k, CombatFaction.Hostile, new double2(30, 0), hp: 5000f);
                Coat(k, u, "Acid");
                k.ApplyStatus(u, Bit("Fire"), 8f, 0f, 0f, 0f, 0);
                Coat(k, v, "Earth");
                Coat(k, v, "Wet");
                StepN(k, 150, Dt * speed); // 真实 2.5 秒
                float fire = Sum(k, EmgMatterConst.Fire, new double2(0, 0));
                bool mudSlow = k.TryGetStatus(v, out uint m, out _, out _, out _, out _) && (m & CombatConst.StatusBitZoneSlow) != 0u;
                float gameSeconds = 2.5f * speed;
                // 静止单位每游戏秒一块明火：0 倍速不滴；滴满 4 游戏秒、明火存续 3 秒，到 7.5 游戏秒已烧完；泥浆 4 游戏秒到期
                bool blockOk = speed == 0f || speed == 3f ? fire <= 0f : fire > 0f;
                bool mudOk = gameSeconds < 3.9f ? mudSlow : !mudSlow;
                if (blockOk && mudOk)
                {
                    ok.Add($"{speed}x:明火{fire:0.#}/{(mudSlow ? "泥浆中" : "已结束")}");
                }
                else
                {
                    Line($"    · {speed}x：明火 {fire:0.#}、泥浆 {mudSlow}");
                }
            }
            Expect(ok.Count == 5, $"真实 2.5 秒后（按游戏秒）：{string.Join(" ", ok)}");
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

        private static void Expect(bool ok, string message)
        {
            if (ok)
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

        private static void Line(string s) => _report.AppendLine(s);
    }
}
