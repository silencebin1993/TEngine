using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-PHS-01 相变累积量与单位相态的自动验收（FG19 FGR-EMG-100～103；FGT-EMG-034、FGT-EMG-020 evt.phase 生产者；ADR-EMG-001 ⑥ ⑦ ⑭ #5 #6 #9）。
    /// 起真实内核（CombatKernel，Burst 步进）与真实表（fg.TbPhaseRule、fg.TbChannelRule、fg.TbEntityProfile、fg.TbStatusTag）：
    /// A 表（7 行与 FG19B §3.3 同数、内核拒坏表）；B 冻结（液氮两次喷中冻结、不能开火 / 移动、2.5 秒解冻、免疫窗内反复冰冻无效、窗后能再冻、冻结前减速 = 0.6 × 寒量）；
    /// C 冻结的打断（受动能碎裂 + 破片 + 附带伤害 / 轻弹对照不碎；受热 ≥ 30 热震立即解冻并破甲 / 小热对照不解冻）；D 麻痹（不能开火结果码 Stunned、向 4 米内 ≤ 3 个导体放电、1 秒退出、免疫窗）；
    /// E 熔穿（漏熔融金属、正面装甲失效）；F 破甲（碎屑、质量 × 0.6、装甲失效、阈值退出）；G 磁化（弹体被吸偏 / 对照直飞）；H 燃烧（4 × L 持续伤害、与燃烧标签不叠两份、进水熄灭）；
    /// I 抗性（档案寒抗 0.5 只吃一半、覆盖寒抗 1 冻不住、清掉回档案值、防腐涂层作机器腐蚀抗性下限、电抗日覆盖）；J 上限；K 标签换算（冻结标签 → 寒量 0.3、不再挂易伤位）；
    /// L evt.phase 生产者与对照（无监听不入队）；M 存读档（冻结途中读档续跑哈希一致、读 15 版快照把云端受寒 / 冻结 / 免疫位映射成寒量 / 冻结 / 抗性覆盖）；
    /// N 暂停与 0.5x～3x（按游戏秒）；O 头顶图标（相态在前、累积进度点、合计 ≤ 4）与悬停（抗性满写原因）；P 注入类固件撤过渡字段与旧标签（DEBT-EMG1FW01-03）；Q 400 单位相态步进耗时（只测一次）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgPhaseSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgPhaseSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgPhaseSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const int Chill = EmgPhaseConst.Chill, Heat = EmgPhaseConst.Overheat, Charge = EmgPhaseConst.Charge, Corrode = EmgPhaseConst.Corrode,
            Mag = EmgPhaseConst.Magnetize, Ignite = EmgPhaseConst.Ignite;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/相变累积量与单位相态")]
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
            Line("\n[相变累积量与单位相态] EMG2-PHS-01：7 个累积量、冻结 / 熔穿 / 麻痹 / 破甲 / 磁化 / 燃烧、抗性覆盖、evt.phase、存读档（FGT-EMG-034 / 020）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 同步编译，真机 IL2CPP + Burst，耗时只作量级参考）");
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
                Step(CheckTable);
                Step(CheckFreeze);
                Step(CheckFreezeBreak);
                Step(CheckStun);
                Step(CheckMeltdown);
                Step(CheckBreach);
                Step(CheckMagnet);
                Step(CheckBurning);
                Step(CheckResist);
                Step(CheckCapAndTag);
                Step(CheckPhaseEvent);
                Step(CheckSaveLoad);
                Step(CheckSpeed);
                Step(CheckIconsAndHover);
                Step(CheckInjectLegacy);
                Step(CheckPerf);
            }
            catch (Exception e)
            {
                Fail($"相态自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [相变累积量与单位相态] 断言通过 {_pass}，失败 {_fail}");
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
            k.SetClarityBits(NamedReactionCatalog.BitOf("Acid"), NamedReactionCatalog.BitOf("Wet"), NamedReactionCatalog.BitOf("Oil"), NamedReactionCatalog.BitOf("Fire"),
                NamedReactionCatalog.BitOf("Frozen"), NamedReactionCatalog.BitOf("Shock"));
            k.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            k.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, string material = "material.steel")
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0.4f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });
            if (f == CombatFaction.Hostile && material != null)
            {
                k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy(material, false), 0f);
            }
            return id;
        }

        private static int Foe(CombatKernel k, double2 p, float hp = 400f, int weapon = -1, string material = "material.steel") => Unit(k, CombatFaction.Hostile, p, weapon, hp, material);

        private static CombatWeapon Instant(float damage, float range = 30f, int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Flight(float damage, float speed = 20f, float life = 2f) => new CombatWeapon
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

        private static float Molten(CombatKernel k)
        {
            float sum = 0f;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Molten)
                {
                    sum += z.Amount;
                }
            }
            return sum;
        }

        private static void Move(CombatKernel k, int id, double2 to) => k.IssueCommand(id, CombatCommandKind.Move, to, 0, 0.2f, 0f, 0f, false);

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : default;

        private static EmgUnitState St(CombatKernel k, int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState s) ? s : default;

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

        // ── A 表 ─────────────────────────────────────────────────────────────

        private static void CheckTable()
        {
            EmgPhaseCatalog.Compiled c = EmgPhaseCatalog.Current;
            bool same = c.Ok && c.Rules.Count == EmgPhaseConst.Count;
            for (int i = 0; same && i < c.Rules.Count; i++)
            {
                EmgPhaseRule a = c.Rules[i], b = EmgPhaseConst.Default(i);
                same = a.Enter == b.Enter && a.Exit == b.Exit && a.Decay == b.Decay && a.Cap == b.Cap && a.MinDwell == b.MinDwell && a.Guard == b.Guard && a.Resist == b.Resist
                       && a.ExitMode == b.ExitMode && a.TagGain == b.TagGain && a.ContactGain == b.ContactGain;
            }
            EmgPhaseRule ch = c.Rules.Count > 0 ? c.Rules[0] : default;
            Expect(same && ch.P1 == 0.6f && ch.Decay == 0.25f && ch.MinDwell == 2.5f && ch.Guard == 1.5f,
                $"fg.TbPhaseRule {c.Rules.Count} 行，行序 = 累积量下标，数值与 FG19B §3.3 / 内核缺省一致；phase_chill 与云端受寒同数（减速 {ch.P1}、衰减 {ch.Decay}、冻结 {ch.MinDwell} 秒、免疫 {ch.Guard} 秒）");
            CombatKernel k = NewKernel();
            try
            {
                var bad = new List<EmgPhaseRule>(c.Rules);
                EmgPhaseRule r = bad[3];
                r.Exit = r.Enter + 0.1f;
                bad[3] = r;
                bool rej1 = !k.SetPhaseRules(bad);
                bool rej2 = !k.SetPhaseRules(c.Rules.GetRange(0, 6));
                Expect(rej1 && rej2 && k.PhaseRuleAt(3).Exit == c.Rules[3].Exit, "坏表整表拒绝（阈值退出 ≥ 进入 / 只有 6 行），内核保留原表");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── B 冻结 ───────────────────────────────────────────────────────────

        private static void CheckFreeze()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0), 400f, k.AddWeapon(Instant(5f)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 3), k.AddWeapon(Instant(5f)), 500f);
                k.ChillUnit(foe, 0.6f);
                float acc1 = k.AccumOf(foe, Chill);
                bool notYet = !k.InPhase(foe, Chill) && k.FireAt(foe, me, k.Time) != CombatFireResult.Frozen;
                k.ChillUnit(foe, 0.6f);
                float acc2 = k.AccumOf(foe, Chill);
                bool frozen = k.InPhase(foe, Chill);
                CombatFireResult fr = k.FireAt(foe, me, k.Time);
                int mover = Unit(k, CombatFaction.Player, new double2(-8, 0), -1, 500f);
                k.ChillUnit(mover, 1f);
                double2 p0 = Pos(k, mover);
                Move(k, mover, new double2(-8, 10));
                StepN(k, 60);
                double moved = math.distance(p0, Pos(k, mover));
                Expect(math.abs(acc1 - 0.6f) < 1e-4f && notYet && frozen && math.abs(acc2 - 1f) < 1e-4f && fr == CombatFireResult.Frozen && moved < 1e-6,
                    $"液氮喷中 0.6：寒量 {acc1:0.###}、未冻结；再喷 0.6 → 冻结（寒量封顶 {acc2:0.###}）；冻结中开火结果 = {fr}（结果码保留，来源 = 相态）、下令移动 1 秒位移 {moved:0.####}");
                int thaw = StepsUntil(k, () => !k.InPhase(foe, Chill), 400);
                float guard = St(k, foe).PhsTA.x;
                Expect(thaw > 0 && math.abs((thaw + 60) * Dt - 2.5f) <= 2.5f * Dt && k.AccumOf(foe, Chill) == 0f && math.abs(guard - 1.5f) < 0.05f,
                    $"冻结 {(thaw + 60) * Dt:0.###} 游戏秒后解冻（表 2.5 秒）、寒量归零、进入免疫窗 {guard:0.##} 秒");
                k.ChillUnit(foe, 0.6f);
                k.ChillUnit(foe, 0.6f);
                bool immune = k.AccumOf(foe, Chill) == 0f && !k.InPhase(foe, Chill);
                StepN(k, 100);
                k.ChillUnit(foe, 0.6f);
                k.ChillUnit(foe, 0.6f);
                Expect(immune && k.InPhase(foe, Chill), "负向：解冻后免疫窗内反复冰冻无效（寒量仍 0，不会永久冻结）；1.67 秒后免疫窗过了能再次冻结");
                // 冻结前减速 = 0.6 × 寒量：两台同速单位，一台寒量 0.5，走同样步数
                int a = Unit(k, CombatFaction.Player, new double2(20, 0), -1, 500f), b = Unit(k, CombatFaction.Player, new double2(20, 5), -1, 500f);
                k.ChillUnit(a, 0.5f);
                Move(k, a, new double2(40, 0));
                Move(k, b, new double2(40, 5));
                StepN(k, 6);
                double da = Pos(k, a).x - 20, db = Pos(k, b).x - 20;
                Expect(db > 0 && math.abs(da / db - (1 - 0.6 * k.AccumOf(a, Chill))) < 0.08, $"冻结前减速：寒量 0.5 的单位走 {da:0.###} 米 / 对照 {db:0.###} 米（≈ 1 − 0.6 × 寒量）");
                // 燃烧解冻更快：两台冻结的单位，一台在燃烧相态
                int hot = Foe(k, new double2(-20, 0)), cold = Foe(k, new double2(-20, 5));
                k.ChillUnit(hot, 1f);
                k.ChillUnit(cold, 1f);
                k.AddAccum(hot, Ignite, 1.2f); // 钢热抗 0.1
                int tHot = StepsUntil(k, () => !k.InPhase(hot, Chill), 400);
                int tCold = StepsUntil(k, () => !k.InPhase(cold, Chill), 400);
                Expect(tHot > 0 && tCold > 0 && tHot * 2 < tCold + tHot, $"燃烧中的单位解冻更快：{tHot * Dt:0.##} 秒 / 对照再过 {tCold * Dt:0.##} 秒（燃烧 × 3）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── C 冻结的打断 ─────────────────────────────────────────────────────

        private static void CheckFreezeBreak()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 8), 400f);
                int light = Foe(k, new double2(8, 8), 400f);
                // 从背后打（敌方正面朝 −y，正面装甲 0.4 会把 30 削成 18 < 24）：碎裂判定用装甲之后的实际伤害
                int me = Unit(k, CombatFaction.Player, new double2(0, 16), k.AddWeapon(Flight(30f)), 500f);
                int me2 = Unit(k, CombatFaction.Player, new double2(8, 16), k.AddWeapon(Flight(5f)), 500f);
                k.ChillUnit(foe, 1f);
                k.ChillUnit(light, 1f);
                float hp0 = Hp(k, foe);
                int proj0 = k.ProjectileCount;
                k.FireAt(me, foe, k.Time);
                k.FireAt(me2, light, k.Time);
                int s = StepsUntil(k, () => !k.InPhase(foe, Chill), 60);
                int frags = k.ProjectileCount;
                float lost = hp0 - Hp(k, foe);
                Expect(s > 0 && frags >= 6 && lost >= 30f + 20f - 0.01f && St(k, foe).PhsTA.x > 1f && k.InPhase(light, Chill),
                    $"冻结中被 30 伤害的射弹打中（动能 30 ≥ 24 × 钢脆性 1）→ 碎裂：立即解冻进免疫窗、放出破片（第 {s} 步，弹体 {proj0}→{frags}）、掉血 {lost:0.#}（含附带 20）；5 伤害的轻弹对照仍冻结");
                int a = Foe(k, new double2(30, 0)), b = Foe(k, new double2(30, 5));
                k.ChillUnit(a, 1f);
                k.ChillUnit(b, 1f);
                EmgRuleCatalog.Compiled rc = EmgRuleCatalog.Current;
                int shockKey = rc.Ids.IndexOf("reaction_thermalshock") >= 0 ? rc.Rules[rc.Ids.IndexOf("reaction_thermalshock")].Key : 0; // P02 行的 id
                int shock0 = k.EmgRuleCountOfKey(shockKey);
                k.InjectUnitHeat(a, 40f);
                k.InjectUnitHeat(b, 10f);
                Expect(!k.InPhase(a, Chill) && k.InPhase(a, Corrode) && k.EffectiveArmorOf(a) == 0f && k.InPhase(b, Chill) && k.EmgRuleCountOfKey(shockKey) == shock0 + 1,
                    $"负向：冻结中注热 40 ≥ 30 → 热震立即解冻并进入破甲（装甲 {k.EffectiveArmorOf(a)}），热震计数按 P02 稳定键+{k.EmgRuleCountOfKey(shockKey) - shock0}；注热 10 的对照仍冻结（{k.InPhase(b, Chill)}）；a 冻结 {k.InPhase(a, Chill)} / 破甲 {k.InPhase(a, Corrode)}");
                // P02 第二条触发（FG19B“或被推进明火 / 熔融斑块”）：带热抗 0.6 的冻结目标单发注热到不了 30，站进明火照样热震——洗礼塔泄压阶段的玩家路径
                int c = Foe(k, new double2(-30, 0)), e = Foe(k, new double2(-30, 12));
                k.SetResist(c, 1, 0.6f);
                k.ChillUnit(c, 1f);
                k.ChillUnit(e, 1f);
                int shock1 = k.EmgRuleCountOfKey(shockKey);
                k.InjectUnitHeat(c, 45f); // 过载单发 45 × (1 − 0.6) = 18 < 30：不热震
                bool held = k.InPhase(c, Chill);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(-30, 0), 1.5f, 1f, 3f);
                int sc = StepsUntil(k, () => !k.InPhase(c, Chill), 60);
                Expect(held && sc > 0 && k.InPhase(c, Corrode) && k.InPhase(e, Chill) && k.EmgRuleCountOfKey(shockKey) == shock1 + 1,
                    $"负向：热抗 0.6 的冻结目标吃过载单发 45（有效 18）仍冻结（{held}）；脚下铺明火后第 {sc} 步热震解冻并破甲，旁边不在火里的冻结对照仍冻结（{k.InPhase(e, Chill)}）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── D 麻痹 ───────────────────────────────────────────────────────────

        private static void CheckStun()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0), 400f, k.AddWeapon(Instant(5f)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 9), -1, 500f);
                int n1 = Foe(k, new double2(1.5, 0)), n2 = Foe(k, new double2(-2, 0)), n3 = Foe(k, new double2(0, -3)), n4 = Foe(k, new double2(3.5, 0)), far = Foe(k, new double2(9, 0));
                k.AddAccum(foe, Charge, 1f);
                bool stun = k.InPhase(foe, Charge);
                CombatFireResult fr = k.FireAt(foe, me, k.Time);
                float c1 = k.AccumOf(n1, Charge), c2 = k.AccumOf(n2, Charge), c3 = k.AccumOf(n3, Charge), c4 = k.AccumOf(n4, Charge), cf = k.AccumOf(far, Charge);
                int got = (c1 > 0 ? 1 : 0) + (c2 > 0 ? 1 : 0) + (c3 > 0 ? 1 : 0) + (c4 > 0 ? 1 : 0);
                Expect(stun && fr == CombatFireResult.Stunned && got == 3 && c1 > 0 && c2 > 0 && c3 > 0 && c4 == 0f && cf == 0f && k.AccumOf(foe, Charge) == 0f,
                    $"电荷满 → 麻痹：开火结果 = {fr}；向 4 米内最近的 3 个导体各放电（{c1:0.##}/{c2:0.##}/{c3:0.##}，第 4 近 {c4} 与 9 米外 {cf} 不放），自己电荷清零");
                int s = StepsUntil(k, () => !k.InPhase(foe, Charge), 200);
                k.AddAccum(foe, Charge, 1f);
                Expect(math.abs(s * Dt - 1f) <= 2 * Dt && !k.InPhase(foe, Charge) && k.AccumOf(foe, Charge) == 0f,
                    $"麻痹 {s * Dt:0.##} 秒后退出（表 1 秒）；退出后 2 秒免疫窗内再充电无效（不会永久麻痹）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── E 熔穿 / F 破甲 ──────────────────────────────────────────────────

        private static void CheckMeltdown()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0));
                int ctl = Foe(k, new double2(10, 0));
                float p0 = Molten(k);
                k.InjectUnitHeat(foe, 400f); // 结构积热 = 400 ÷ 300 ≥ 1
                bool melt = k.InPhase(foe, Heat);
                StepN(k, 1);
                float p1 = Molten(k);
                StepN(k, 130);
                float p2 = Molten(k);
                Expect(melt && p1 > p0 && p2 > p1 && k.EffectiveArmorOf(foe) == 0f && math.abs(k.EffectiveArmorOf(ctl) - 0.4f) < 1e-4f,
                    $"注热 400 → 结构积热满 → 熔穿：身下熔融金属份量 {p0:0.#}→{p1:0.#}，2 秒后再漏（{p2:0.#}，同处合并成一块）；正面装甲失效（{k.EffectiveArmorOf(foe)}，对照 {k.EffectiveArmorOf(ctl):0.##}）");
            }
            finally
            {
                k.Dispose();
            }
        }

        private static void CheckBreach()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0));
                float m0 = k.UnitMassOf(foe);
                int p0 = k.PatchCount;
                k.AddAccum(foe, Corrode, 1f);
                bool breach = k.InPhase(foe, Corrode);
                StepN(k, 1);
                float m1 = k.UnitMassOf(foe);
                Expect(breach && k.PatchCount > p0 && math.abs(m1 - m0 * 0.6f) < 1e-3f && k.EffectiveArmorOf(foe) == 0f,
                    $"腐蚀度满 → 破甲：装甲脱落成碎屑斑块（{p0}→{k.PatchCount}）、质量 {m0:0.##}→{m1:0.##}（× 0.6，更容易被推走）、装甲清零（附带）");
                int s = StepsUntil(k, () => !k.InPhase(foe, Corrode), 60 * 15);
                Expect(s * Dt >= 4f && k.AccumOf(foe, Corrode) < 0.3f && math.abs(k.UnitMassOf(foe) - m0) < 1e-3f,
                    $"腐蚀度衰减到 0.3 以下且停留 ≥ 4 秒后退出（{s * Dt:0.#} 秒），质量恢复");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── G 磁化 ───────────────────────────────────────────────────────────

        private static void CheckMagnet()
        {
            double Drift(bool magnetize)
            {
                CombatKernel k = NewKernel();
                try
                {
                    int target = Foe(k, new double2(0, 20), 9999f);
                    int side = Foe(k, new double2(2.5, 8), 9999f);
                    if (magnetize)
                    {
                        k.AddAccum(side, Mag, 1f);
                    }
                    int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 10f, 3f)), 500f);
                    k.FireAt(me, target, k.Time);
                    StepN(k, 60);
                    return k.ProjectileCount > 0 && k.TryGetProjectile(0, out CombatProjectile pr) ? pr.Pos.x : 0;
                }
                finally
                {
                    k.Dispose();
                }
            }
            double withMag = Drift(true), ctl = Drift(false);
            Expect(withMag > 0.2 && math.abs(ctl) < 0.05, $"磁化单位是弱磁源：飞过 4 米内的弹体被吸偏 {withMag:0.###} 米（不分阵营）；对照没有磁化时直飞（偏 {ctl:0.###}）");
        }

        // ── H 燃烧 ───────────────────────────────────────────────────────────

        private static void CheckBurning()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0), 400f);
                int both = Foe(k, new double2(10, 0), 400f);
                int wet = Foe(k, new double2(20, 0), 400f);
                uint fire = NamedReactionCatalog.BitOf("Fire");
                k.AddAccum(foe, Ignite, 1.2f); // 钢热抗 0.1：1.2 × 0.9 = 1.08 ≥ 1
                k.AddAccum(foe, Ignite, 0.3f / 0.9f); // 与下面挂燃烧标签那一次的 +0.3 着火度对齐
                k.AddAccum(both, Ignite, 1.2f);
                k.ApplyStatus(both, fire, 10f, 4f, 0f, 0f, 0); // 燃烧标签（带火载荷）：着火度 +0.3，标签自己 4 / 秒
                float h0 = Hp(k, foe), hb = Hp(k, both);
                StepN(k, 60);
                float lost = h0 - Hp(k, foe), lostBoth = hb - Hp(k, both);
                Expect(k.InPhase(foe, Ignite) && lost >= 4f * 0.9f && lost <= 12f && math.abs(lostBoth - lost) < 0.6f,
                    $"着火度满 → 燃烧：1 秒掉血 {lost:0.##}（4 × L，L 由着火度推出）；同样着火度、同时挂着燃烧标签的单位掉 {lostBoth:0.##}（取大，不叠两份）");
                k.AddAccum(wet, Ignite, 1.2f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(20, 0), 2f, 1f, 10f);
                int s = StepsUntil(k, () => !k.InPhase(wet, Ignite), 60);
                Expect(s > 0 && k.AccumOf(wet, Ignite) == 0f, $"站进水斑块 {s * Dt:0.##} 秒内熄灭（着火度清零）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── I 抗性 ───────────────────────────────────────────────────────────

        private static void CheckResist()
        {
            CombatKernel k = NewKernel();
            try
            {
                int clarity = Foe(k, new double2(0, 0), 400f, -1, "material.clarity");
                k.ChillUnit(clarity, 0.6f);
                float half = k.AccumOf(clarity, Chill);
                int boss = Foe(k, new double2(5, 0));
                k.SetResist(boss, 0, 1f);
                k.ChillUnit(boss, 1f);
                k.ChillUnit(boss, 1f);
                bool sealedOk = !k.InPhase(boss, Chill) && k.AccumOf(boss, Chill) == 0f;
                k.SetResist(boss, 0, 0f);
                k.ChillUnit(boss, 1f);
                Expect(math.abs(half - 0.3f) < 1e-4f && k.EffectiveResist(clarity, 3) >= 0.999f && sealedOk && k.InPhase(boss, Chill),
                    $"抗性按属性列（不按单位 ID）：澄净材质档案寒抗 0.5 → 喷中 0.6 只得 {half:0.###}、腐蚀抗 1；覆盖寒抗 1（洗礼塔封闭）冻不住，覆盖清回 0（泄压）回到档案值能冻");
                int me = Unit(k, CombatFaction.Player, new double2(0, 10), -1, 500f);
                k.SetAcidMarsh(new List<float4>(), 0.02f, 0.5f, 0.6f);
                float floorR = k.EffectiveResist(me, 3);
                k.SetResist(me, 2, 1f);
                k.AddAccum(me, Charge, 2f);
                bool insulated = !k.InPhase(me, Charge);
                k.SetResist(me, 2, 0f);
                k.AddAccum(me, Charge, 2f);
                Expect(math.abs(floorR - 0.6f) < 1e-4f && insulated && k.InPhase(me, Charge),
                    $"防腐涂层作己方机器腐蚀抗性下限（{floorR:0.##}）；绝缘涂层反制锁定日覆盖电抗 1 不会麻痹，次日清回 0 恢复");
                // CombatSite.SetResist 同时维护免疫位（状态位旧路径）
                var site = new CombatSite("phs_selfcheck", CombatSite.ConfigFromTuning());
                try
                {
                    int u = site.Kernel.Spawn(new CombatSpawn
                    {
                        ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Flags = Live, Position = new double2(0, 0), Home = new double2(0, 0), Radius = 0.5f,
                        Speed = 4f, Health = 100f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                    });
                    uint frozenBit = NamedReactionCatalog.BitOf("Frozen");
                    site.SetResist(u, CombatSite.ResistCold, 1f);
                    bool on = site.TryGetClarity(u, out CombatClarityUnit c1) && (c1.ImmuneMask & frozenBit) != 0u && site.EffectiveResist(u, 0) >= 0.999f;
                    site.SetResist(u, CombatSite.ResistCold, 0f);
                    bool off = site.TryGetClarity(u, out CombatClarityUnit c2) && (c2.ImmuneMask & frozenBit) == 0u && site.EffectiveResist(u, 0) < 0.001f;
                    Expect(on && off, "CombatSite.SetResist（原 SetImmuneMask 的调用方改调它）：寒抗 1 同时挂冻结免疫位，清回 0 摘位并回到档案值");
                }
                finally
                {
                    site.Dispose();
                }
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── J 上限 / K 标签换算 ──────────────────────────────────────────────

        private static void CheckCapAndTag()
        {
            CombatKernel k = NewKernel();
            try
            {
                int foe = Foe(k, new double2(0, 0));
                k.AddAccum(foe, Corrode, 5f);
                float a = k.AccumOf(foe, Corrode);
                float more = k.AddAccum(foe, Corrode, 1f);
                k.AddAccum(foe, Heat, 9f);
                Expect(math.abs(a - 1.5f) < 1e-4f && more == 0f && math.abs(k.AccumOf(foe, Heat) - 1.5f) < 1e-4f, $"负向：累积量到上限不再增长（腐蚀度 {a}、再加 {more}；结构积热 {k.AccumOf(foe, Heat)}）");
                int t = Foe(k, new double2(5, 0));
                uint frozen = NamedReactionCatalog.BitOf("Frozen");
                k.ApplyStatus(t, frozen, 4f, 0f, 0f, 0.25f, 0, 10f);
                bool noBit = !k.TryGetStatus(t, out uint tagMask, out _, out _, out _, out _) || (tagMask & frozen) == 0u;
                float h0 = Hp(k, t);
                int me = Unit(k, CombatFaction.Player, new double2(5, 3), k.AddWeapon(Instant(10f)), 500f);
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                float dmg = h0 - Hp(k, t);
                Expect(math.abs(k.AccumOf(t, Chill) - 0.3f) < 0.01f && noBit && dmg <= 10f + 1e-3f,
                    $"冻结标签（带寒载荷）→ 寒量 {k.AccumOf(t, Chill):0.###}（+0.3）、不再挂冻结位（{noBit}）、受击不吃易伤（10 伤害只掉 {dmg:0.##}；FG19 第 14 节 #7 按推荐默认执行）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── L evt.phase ─────────────────────────────────────────────────────

        private static void CheckPhaseEvent()
        {
            long Run(bool listen, out EmgEvent last)
            {
                CombatKernel k = NewKernel();
                try
                {
                    int p = -1;
                    if (listen)
                    {
                        var instrs = new List<EmgInstr> { new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Phase), Role = 2 } };
                        var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
                        p = k.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Phase }, nodes, instrs);
                    }
                    int foe = Foe(k, new double2(0, 0), 400f, k.AddWeapon(Instant(1f, 1f, p)));
                    k.ChillUnit(foe, 1f);
                    StepN(k, 200);
                    k.TryGetLastEmgEvent(EmgEventKind.Phase, out last);
                    return k.EmgEnqueuedOf(EmgEventKind.Phase);
                }
                finally
                {
                    k.Dispose();
                }
            }
            long on = Run(true, out EmgEvent ev);
            long off = Run(false, out _);
            Expect(on == 2 && (ev.Code & 15) == Chill && (ev.Code & EmgPhaseConst.CodeExit) != 0 && off == 0,
                $"evt.phase 生产者（FGT-EMG-020）：读它的单位冻结 → 解冻共入队 {on} 条（末条 Code {ev.Code} = 寒量下标 + 退出位）；对照无监听不入队（{off}）");
        }

        // ── M 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CombatKernel a = NewKernel(), b = NewKernel(), c = NewKernel();
            try
            {
                int foe = Foe(a, new double2(0, 0));
                Foe(a, new double2(3, 0));
                a.ChillUnit(foe, 1f);
                a.AddAccum(foe, Corrode, 0.7f);
                a.SetResist(foe, 2, 0.5f);
                StepN(a, 30);
                byte[] snap = a.Serialize();
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && b.InPhase(foe, Chill) && math.abs(b.AccumOf(foe, Corrode) - a.AccumOf(foe, Corrode)) < 1e-6f
                            && math.abs(St(b, foe).PhsTA.x - St(a, foe).PhsTA.x) < 1e-6f && math.abs(b.EffectiveResist(foe, 2) - a.EffectiveResist(foe, 2)) < 0.01f;
                StepN(a, 200);
                StepN(b, 200);
                Expect(same && a.StateHash() == b.StateHash() && !b.InPhase(foe, Chill) && St(b, foe).PhsTA.x > 0f,
                    $"冻结途中存档 → 读档：相态 / 剩余冻结 / 腐蚀度 / 电抗覆盖一致；各续跑 200 步哈希一致、读档后照常解冻并进入免疫窗（{lr}）");
                // 读 15 版快照：云端 Chill / FrozenT / ThawGuardT 与免疫位映射
                int u2 = Foe(c, new double2(0, 0));
                int u3 = Foe(c, new double2(4, 0));
                c.ChillUnit(u2, 1f);
                StepN(c, 30);
                c.ChillUnit(u3, 0.4f);
                c.SetImmuneMask(u3, NamedReactionCatalog.BitOf("Frozen"));
                float remain = St(c, u2).PhsTA.x;
                byte[] old = c.SerializeFormatForTests(15);
                CombatKernel d = NewKernel();
                try
                {
                    CombatLoadResult lr2 = d.Load(old);
                    bool ok = lr2 == CombatLoadResult.Ok && d.InPhase(u2, Chill) && math.abs(St(d, u2).PhsTA.x - remain) < 1e-5f && math.abs(d.AccumOf(u3, Chill) - 0.4f) < 1e-5f
                              && d.EffectiveResist(u3, 0) >= 0.999f && d.TryGetClarity(u2, out CombatClarityUnit cc) && cc.FrozenT == 0f && cc.Chill == 0f;
                    Expect(ok, $"读 15 版快照（{lr2}）：云端冻结剩余 {remain:0.###} 秒 → 寒量冻结相态同剩余、受寒 0.4 → 寒量 0.4、冻结免疫位 → 寒抗覆盖 1，云端字段清零");
                }
                finally
                {
                    d.Dispose();
                }
                // 审查修复（第 1 轮 P2）：挂着酸蚀免疫位的单位另被直接写电抗覆盖 1（如绝缘涂层反制）→ 存读档、再改免疫位（摘冻结位）后电抗仍是 1，只有刚摘掉的位退回
                int u4 = Foe(c, new double2(8, 0));
                c.SetImmuneMask(u4, NamedReactionCatalog.BitOf("Acid") | NamedReactionCatalog.BitOf("Frozen"));
                c.SetResist(u4, 2, 1f);
                CombatKernel e2 = NewKernel();
                try
                {
                    CombatLoadResult lr3 = e2.Load(c.Serialize());
                    float elecLoaded = e2.EffectiveResist(u4, 2);
                    e2.SetImmuneMask(u4, NamedReactionCatalog.BitOf("Acid"));
                    Expect(lr3 == CombatLoadResult.Ok && elecLoaded >= 0.999f && e2.EffectiveResist(u4, 2) >= 0.999f && e2.EffectiveResist(u4, 0) < 0.999f && e2.EffectiveResist(u4, 3) >= 0.999f,
                        $"运行时电抗覆盖 1 + 酸蚀免疫位：读档后电抗 {elecLoaded:0.##}；摘掉冻结位后寒抗退回 {e2.EffectiveResist(u4, 0):0.##}、电抗仍 {e2.EffectiveResist(u4, 2):0.##}、腐蚀抗 {e2.EffectiveResist(u4, 3):0.##}");
                }
                finally
                {
                    e2.Dispose();
                }
            }
            finally
            {
                a.Dispose();
                b.Dispose();
                c.Dispose();
            }
        }

        // ── N 暂停与倍速 ─────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            float ThawAt(float dt)
            {
                CombatKernel k = NewKernel();
                try
                {
                    int foe = Foe(k, new double2(0, 0));
                    k.ChillUnit(foe, 1f);
                    int s = StepsUntil(k, () => !k.InPhase(foe, Chill), 2000, dt);
                    return s * dt;
                }
                finally
                {
                    k.Dispose();
                }
            }
            float slow = ThawAt(Dt * 0.5f), normal = ThawAt(Dt), fast = ThawAt(Dt * 3f);
            CombatKernel p = NewKernel();
            try
            {
                int foe = Foe(p, new double2(0, 0));
                p.ChillUnit(foe, 1f);
                EmgUnitState before = St(p, foe);
                p.Step(0f, p.Time);
                EmgUnitState after = St(p, foe);
                Expect(math.abs(slow - 2.5f) <= Dt && math.abs(normal - 2.5f) <= 1.5f * Dt && math.abs(fast - 2.5f) <= 3.5f * Dt && after.PhsTA.x == before.PhsTA.x,
                    $"倍速矩阵：每步 0.5x / 1x / 3x 游戏秒，冻结都在 {slow:0.###} / {normal:0.###} / {fast:0.###} 游戏秒解冻（按游戏秒，不按帧）；暂停（0 秒步）剩余冻结不变");
            }
            finally
            {
                p.Dispose();
            }
        }

        // ── O 图标与悬停 ─────────────────────────────────────────────────────

        private static void CheckIconsAndHover()
        {
            CombatKernel k = NewKernel();
            var icons = new NativeList<CombatInstance>(16, Allocator.Persistent);
            var vis = new NativeArray<float2>(CombatRenderer.PhaseVisualBase + EmgPhaseConst.Count, Allocator.Persistent);
            try
            {
                float2[] sv = NamedReactionCatalog.BuildStatusVisuals();
                float2[] pv = EmgPhaseFeedback.BuildVisuals();
                for (int i = 0; i < 32; i++)
                {
                    vis[i] = sv[i];
                }
                for (int i = 0; i < pv.Length; i++)
                {
                    vis[CombatRenderer.PhaseVisualBase + i] = pv[i];
                }
                int foe = Foe(k, new double2(0, 0));
                k.ChillUnit(foe, 1f);
                k.AddAccum(foe, Corrode, 0.5f);
                k.AddAccum(foe, Ignite, 0.8f);
                k.ApplyStatus(foe, NamedReactionCatalog.BitOf("Oil") | NamedReactionCatalog.BitOf("Wet"), 5f, 0f, 0f, 0f, 0);
                k.PrepareStatusIcons(icons, vis, double2.zero, CombatRenderer.MaxIconsPerUnit, 0.5f);
                bool shapes = true;
                for (int i = 0; i < pv.Length && i < 6; i++)
                {
                    for (int j = i + 1; j < 6; j++)
                    {
                        shapes &= pv[i].x >= 0f && pv[i].x != pv[j].x;
                    }
                }
                bool order = icons.Length == 4 && icons[0].B.y == pv[Chill].x && icons[0].B.w == 33f && icons[1].B.y == pv[Corrode].x && icons[1].B.w == 31f
                             && icons[2].B.y == pv[Ignite].x && icons[2].B.w == 32f;
                Expect(shapes && order, $"头顶图标（B05 / B15）：6 个相态形状互不相同；冻结在前（满 3 点）、腐蚀度 50%（1 点）、着火度 80%（2 点），再接标签，合计 {icons.Length} ≤ 4");
                var sb = new StringBuilder();
                k.SetResist(foe, 4, 1f);
                bool lines = EmgPhaseFeedback.AppendLines(k, foe, sb);
                string text = sb.ToString();
                Expect(lines && text.Contains(EmgPhaseFeedback.PhaseName(Chill)) && text.Contains(GameText.Format("emg.phase.meter", EmgPhaseFeedback.AccName(Corrode), "50"))
                       && text.Contains(EmgPhaseFeedback.PhaseName(Mag)), $"悬停读数：相态剩余秒、累积量百分比、抗性满不能进入的原因（{text.Replace('\n', '｜')}）");
                EmgPhaseFeedback.ResetForTests();
                bool hooked = false;
                void OnHook(string id) => hooked |= id == GuidanceHooks.EmgFirstPhase;
                GuidanceHooks.Touched += OnHook;
                EmgPhaseFeedback.OnCue(null, new CombatEvent { Kind = CombatEventKind.PhaseCue, Code = Chill }, Vector2.zero);
                EmgPhaseFeedback.OnCue(null, new CombatEvent { Kind = CombatEventKind.PhaseCue, Code = (byte)(Chill | EmgPhaseConst.CodeExit | EmgPhaseConst.CodeBroken) }, Vector2.zero);
                Expect(EmgPhaseFeedback.EnterCues == 1 && EmgPhaseFeedback.BrokenCues == 1 && hooked,
                    "相变反馈（B07）：进入 / 被打断各出一条声音 + 字幕；进入相态发引导钩子 emg_first_phase");
                GuidanceHooks.Touched -= OnHook;
            }
            finally
            {
                icons.Dispose();
                vis.Dispose();
                k.Dispose();
            }
        }

        // ── P 注入类固件撤过渡字段与旧标签（DEBT-EMG1FW01-03 注入类；FGR-EMG-019 单轨） ──────────────────────

        private static void CheckInjectLegacy()
        {
            // 1) 编译器：同一程序里 en.heat / en.elec 注入写端的旧标签退役（过载 Fire、电容蓄力 Charged），电击 Shock 不在退役表（连锁电弧照挂，归 EMG2-REACT-02）
            // 每条单独编一个程序（三条同装会超出接入口容量、被截的整条旧字段都跳过，比不出逐标签退役）
            string[] fws = { "fw_overload", "fw_capacitor", "fw_arcchain" };
            var single = new CombatReading();
            var dual = new CombatReading();
            bool compiled = EmgProgramCompiler.TransitionFields("en.heat").Length == 0;
            foreach (string fw in fws)
            {
                EmgProgramResult prog = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, FirmwareCarrier.Projectile);
                Func<string, string, bool> skip = EmgProgramCompiler.LegacySkip(prog);
                compiled &= skip != null && !skip(fw, EmgProgramCompiler.TagsField);
                single.StatusMask |= CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { fw }, false, skip).StatusMask;
                dual.StatusMask |= CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { fw }, false).StatusMask;
                compiled &= skip(fw, EmgProgramCompiler.TagField(fw == "fw_arcchain" ? "Shock" : fw == "fw_overload" ? "Fire" : "Charged"));
            }
            CombatReading singleFull = CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { "fw_overload" }, false,
                EmgProgramCompiler.LegacySkip(EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_overload" }, FirmwareCarrier.Projectile)));
            CombatReading dualFull = CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { "fw_overload" }, false);
            uint fire = NamedReactionCatalog.BitOf("Fire"), charged = NamedReactionCatalog.BitOf("Charged"), shock = NamedReactionCatalog.BitOf("Shock");
            bool reading = (single.StatusMask & (fire | charged | shock)) == 0u && (dual.StatusMask & fire) != 0u && (dual.StatusMask & charged) != 0u;
            string[] live = EmgProgramCompiler.LiveTagsOf("fw_overload");
            Expect(compiled && reading && Array.IndexOf(live, "Fire") < 0 && Array.IndexOf(EmgProgramCompiler.LiveTagsOf("fw_arcchain"), "Shock") < 0,
                $"编译器单轨：程序里过载 / 电容蓄力的旧标签 Fire / Charged 不进读法（状态位 {single.StatusMask:X} vs 两轨 {dual.StatusMask:X}），过渡字段 heatburst 退役；" +
                "连锁电弧的旧标签电击也撤了（EMG2-REACT-02，FG19B §12 #3）；固件库 / 修复 / 反应提示显示的标签同口径（LiveTagsOf）");
            // 1b) EMG2-FLD-01：磁聚 fld.magnet 接通 → 整条切声明轨，en.mag 的旧标签 Charged / Magnet 按端口退役（单轨读法不再挂，两轨旧读法仍挂），注入由内核写磁化度
            EmgProgramResult magProg = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_magnet" }, FirmwareCarrier.Projectile);
            Func<string, string, bool> magSkip = EmgProgramCompiler.LegacySkip(magProg);
            uint magnetBit = NamedReactionCatalog.BitOf("Magnet");
            uint magSingle = CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { "fw_magnet" }, false, magSkip).StatusMask;
            uint magDual = CarrierReadings.Build(ComponentCatalog.CompGunId, null, null, new[] { "fw_magnet" }, false).StatusMask;
            Expect(!magProg.IsEmpty && magSkip != null && magSkip("fw_magnet", EmgProgramCompiler.TagField("Magnet")) && magSkip("fw_magnet", EmgProgramCompiler.TagField("Charged"))
                   && (magSingle & (magnetBit | NamedReactionCatalog.BitOf("Charged"))) == 0u && (magDual & magnetBit) != 0u,
                $"磁聚（stageGate 2，fld.magnet 随 EMG2-FLD-01 接通）：程序里旧标签 Charged / Magnet 不进读法（单轨 {magSingle:X} vs 两轨 {magDual:X}）——磁化由 en.mag 注入磁化度与磁场表现");
            // 2) 同一次命中只走一轨：程序注热 + 单轨读法 → 结构积热涨、着火度不涨（没挂 Fire）；负向：两轨读法（旧 Fire 标签还在）会同时写着火度，比对必须比出来
            float IgniteAfterHit(CombatReading r, out float heat)
            {
                CombatKernel k = NewKernel();
                try
                {
                    var instrs = new List<EmgInstr> { new EmgInstr { Port = EmgConst.PortHeat, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = 20f } };
                    var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
                    int p = k.AddProgram(new EmgProgram(), nodes, instrs);
                    CombatWeapon w = Instant(1f, 10f, p);
                    r.Carrier = CombatCarrier.Projectile;
                    w.Reading = r;
                    int foe = Foe(k, new double2(0, 3));
                    int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 500f);
                    k.FireAt(me, foe, k.Time);
                    StepN(k, 1);
                    heat = k.AccumOf(foe, Heat);
                    return k.AccumOf(foe, Ignite);
                }
                finally
                {
                    k.Dispose();
                }
            }
            float ig1 = IgniteAfterHit(singleFull, out float h1), ig2 = IgniteAfterHit(dualFull, out float h2);
            Expect(h1 > 0f && ig1 == 0f && h2 > 0f && ig2 > 0f,
                $"同一写端只走一轨：一发注热命中 → 结构积热 {h1:0.##}、着火度 {ig1:0.##}（单轨不挂 Fire）；负向两轨读法同一发着火度 {ig2:0.##} > 0 被比出来");
            // 2b) 炮塔弹体武器（审查修复第 2 轮）：真实 TurretService.BuildWeapon（Mode = 弹体，沿用 FGR-FW-012 兼容读法）+ 真实 CombatSite.WithProgram 装过载 →
            // 兼容读法里的 Fire / heatburst 也撤（弹体带程序下标，命中经 EmgHitInject 注热）；负向：WithProgram 前的读法两者都在，比得出来
            var board = GameLogic.Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(GameLogic.Campaign.Regions.HomeValleyLayout.Erc003ChassisId,
                ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            bool slotOk = board.TrySetUplink(2).Success; // 过载是核心固件：只能经接入口进程序（与正式炮塔接入同一路径）
            GameLogic.Campaign.Blueprint.BlueprintCircuitPreview tp = GameLogic.Campaign.Blueprint.UplinkCompiler.CompileUplinked(board, new[] { "fw_overload" });
            var prof = new GameLogic.Campaign.Defense.TurretProfileDef { Range = 10f, TurnRate = 180f, ProjectileSpeed = 20f, ProjectileRadius = 0.3f };
            CombatWeapon tw0 = GameLogic.Campaign.Defense.TurretService.BuildWeapon(tp, prof, 1f, 0, false, false, false);
            using (var site = new CombatSite("emgphase-turret", CombatSite.ConfigFromTuning()))
            {
                CombatWeapon tw = site.WithProgram(tp, tw0, out EmgProgramResult tres, out _);
                float ig3 = IgniteAfterHit(tw.Reading, out float h3);
                Expect(slotOk && tw0.Mode == CombatWeaponMode.Projectile && tw.Program >= 0 && tres != null && !tres.IsEmpty
                       && (tw0.Reading.StatusMask & fire) != 0u && tw0.Reading.HeatBurstAt > 0f
                       && (tw.Reading.StatusMask & fire) == 0u && tw.Reading.HeatBurstAt == 0f && tw.Reading.DamageScale == tw0.Reading.DamageScale
                       && h3 > 0f && ig3 == 0f,
                    $"炮塔弹体单轨：装过载后读法 Fire 位 {(tw.Reading.StatusMask & fire) != 0u}（装前 {(tw0.Reading.StatusMask & fire) != 0u}）、heatburst {tw.Reading.HeatBurstAt:0.##}（装前 {tw0.Reading.HeatBurstAt:0.##}）、" +
                    $"程序下标 {tw.Program}；一发命中结构积热 {h3:0.##}、着火度 {ig3:0.##}");
            }
            // 3) 子弹药落点注能：子节点带 en.elec 注入的子弹药打中目标 → 目标电荷累积（落点矩阵见 FgReadingMatrixSelfCheck C2“注入类排在触发器后”）
            CombatKernel kk = NewKernel();
            try
            {
                var instrs = new List<EmgInstr>
                {
                    new EmgInstr { Port = EmgConst.PortElec, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = 0.6f, Param2 = 0.5f },
                };
                var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
                int p = kk.AddProgram(new EmgProgram(), nodes, instrs);
                int foe = Foe(kk, new double2(0, 3));
                int me = Unit(kk, CombatFaction.Player, new double2(0, 0), kk.AddWeapon(Instant(1f, 10f, p)), 500f);
                kk.FireAt(me, foe, kk.Time);
                StepN(kk, 1);
                bool noTag = !kk.TryGetStatus(foe, out uint bits, out _, out _, out _, out _) || (bits & charged) == 0u;
                Expect(math.abs(kk.AccumOf(foe, Charge) - 0.6f * (1f - kk.EffectiveResist(foe, 2))) < 0.4f * Dt * 2f && noTag, // 同一步里已按 0.4 / 秒衰减一步
                    $"en.elec 注入在命中落点写进电荷累积量 {kk.AccumOf(foe, Charge):0.###}，目标没有 Charged 状态位");
            }
            finally
            {
                kk.Dispose();
            }
        }

        // ── Q 耗时 ───────────────────────────────────────────────────────────

        private static void CheckPerf()
        {
            CombatKernel k = NewKernel(512);
            try
            {
                var ids = new List<int>();
                for (int i = 0; i < 400; i++)
                {
                    int id = Foe(k, new double2(i % 20 * 3, i / 20 * 3));
                    ids.Add(id);
                    k.AddAccum(id, i % 6, 0.5f + (i % 3) * 0.3f);
                }
                StepN(k, 5);
                var sw = Stopwatch.StartNew();
                StepN(k, 60);
                sw.Stop();
                double perStep = sw.Elapsed.TotalMilliseconds / 60.0;
                Line($"    · 400 单位全部带累积量 / 相态，整步（含全部内核阶段）平均 {perStep:0.###} 毫秒（Editor batchmode，只测一次；真机 IL2CPP + Burst 更快）");
                Expect(perStep < 1.5 * 2, $"400 单位相态步进整步 {perStep:0.###} 毫秒，低于 FGR-EMG-200 涌现预算 1.5 毫秒的 2 倍失败线（审查修复：原失败线按整帧 8 毫秒过松）");
            }
            finally
            {
                k.Dispose();
            }
        }

        // ── 断言 ─────────────────────────────────────────────────────────────

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

        private static void Line(string s)
        {
            _report?.AppendLine(s);
        }
    }
}
