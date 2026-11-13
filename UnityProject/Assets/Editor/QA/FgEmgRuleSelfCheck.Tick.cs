using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Emergence;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMGR-RULE-01 节拍与限频（段名 FgEmgRuleTickSelfCheck，并入 <c>CellFrameworkValidate.RunAll</c>；单段：bash tools/unity-validate.sh --segment FgEmgRuleTickSelfCheck）。
    /// 起真实内核与真实表：R1 E16 电解每块每秒最多 1 次（持续注电 4 秒 = 4 次；单次注电 1.0 在 0.5 秒内 1 次）；R2 暂停不触发、0.5x～3x 倍速次数一致；
    /// R3 冷却中途存读档（CondSince 进快照）续跑一致、读回后不提前电解；R4 斑块当拍被电解吃光、同位置新酸斑块不继承冷却；
    /// R5 热交换每拍：熔融 → 冷渣约 10 秒、油挨明火约 0.5 秒点燃，120 块与 480 块热源两种场面逐步一致；R6 热源 0 块、定温源 / 世界源不回温、暂停与倍速下回温按游戏秒。
    /// </summary>
    public static partial class FgEmgRuleSelfCheck
    {
        [MenuItem("BinGames/QA/自检/节拍与限频（EMGR-RULE-01）")]
        public static void RunTickFromMenu()
        {
            var report = new StringBuilder();
            int fail = RunTick(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int RunTick(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[节拍与限频] EMGR-RULE-01：E16 电解每块每秒最多 1 次（FG19B E16、DEBT-EMG2RULE01-06）；热交换不设预算、每拍回温（FG19B §1.4 第 2、3 条，perf-cut-audit P11）");
            try
            {
                LoadCatalogs();
                Step(CheckElecRate);
                Step(CheckElecPauseSpeed);
                Step(CheckElecSaveLoad);
                Step(CheckElecEdges);
                Step(CheckHeatScale);
                Step(CheckHeatEdges);
            }
            catch (Exception e)
            {
                Fail($"节拍与限频自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [节拍与限频] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static int ElecKey => Key("reaction_electrolysis");

        private static void StepDt(CombatKernel k, float dt, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static CombatKernel AcidKernel(float amount = 5f)
        {
            CombatKernel k = NewKernel();
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, amount, 60f, 0, false, 1);
            StepN(k, 2);
            return k;
        }

        /// <summary>按步长 dt 推进 seconds 游戏秒；sustained = 每个传导节拍之后给酸斑块再注电 q（持续通电）。返回期间 E16 次数。</summary>
        private static int RunElec(CombatKernel k, float dt, float seconds, float q, bool sustained)
        {
            int c0 = k.EmgRuleCountOfKey(ElecKey), ticks = k.PatchStats.ConductTicks;
            int steps = (int)Math.Round(seconds / dt);
            for (int s = 0; s < steps; s++)
            {
                k.Step(dt, k.Time + dt);
                if (sustained && k.PatchStats.ConductTicks != ticks)
                {
                    ticks = k.PatchStats.ConductTicks;
                    k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, q, 1);
                }
            }
            return k.EmgRuleCountOfKey(ElecKey) - c0;
        }

        // ── R1 电解限速 ──────────────────────────────────────────────────────

        private static void CheckElecRate()
        {
            Line("  · R1. E16 电解每块每秒最多 1 次：c2 = 同一宿主最短间隔 1 秒（表列，不按规则 ID 写特例），上次结算时刻记 CondSince");
            EmgRuleCatalog.Compiled cat = EmgRuleCatalog.Current;
            List<EmgRule> limited = cat.Rules.Where(r => r.Cond == EmgRuleConst.CondChargeGe && r.C2 > 0f).ToList();
            int chargeGe = cat.Rules.Count(r => r.Cond == EmgRuleConst.CondChargeGe);
            Expect(limited.Count == 1 && limited[0].Key == ElecKey && Mathf.Approximately(limited[0].C2, 1f) && chargeGe >= 3,
                $"表：charge_ge 规则 {chargeGe} 条，c2 > 0 的只有 {limited.Count} 条、是电解（{(limited.Count > 0 && limited[0].Key == ElecKey)}）、c2 = {(limited.Count > 0 ? limited[0].C2 : 0f)}（最短间隔秒）；E14 / E15 导电水洼 c2 = 0 不限、每拍放电不变");
            using (CombatKernel k = AcidKernel())
            {
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                int n = RunElec(k, Dt, 4f, 1f, true);
                float debris = Patches(k).Where(z => z.Matter == EmgMatterConst.Debris).Sum(z => z.Amount);
                Expect(n == 4 && debris > 0f, $"持续注电 4 秒（每个传导节拍注 1.0，共 16 拍）：E16 {n} 次（≤ 4，旧口径每拍 1 次 = 16 次）、析出碎屑 {debris:0.###} 份");
            }
            using (CombatKernel k = AcidKernel())
            {
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                int n = RunElec(k, Dt, 0.5f, 0f, false);
                int later = RunElec(k, Dt, 1.5f, 0f, false);
                Expect(n == 1 && later == 0, $"单次注电 1.0：0.5 秒内电解 {n} 次（旧口径第 2 拍电荷衰减到 0.4 仍够再电解 1 次 = 2 次）；之后 1.5 秒电荷衰减殆尽、不再电解（{later}）");
            }
        }

        // ── R2 暂停与倍速 ────────────────────────────────────────────────────

        private static void CheckElecPauseSpeed()
        {
            Line("  · R2. 暂停（0 秒步）不触发电解；0.5x / 1x / 2x / 3x 倍速（每步 0.5 / 1 / 2 / 3 个 1/60 游戏秒）次数一致");
            using (CombatKernel k = AcidKernel())
            {
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                int c0 = k.EmgRuleCountOfKey(ElecKey);
                ulong h0 = k.StateHash();
                for (int s = 0; s < 600; s++)
                {
                    k.Step(0f, k.Time);
                }
                bool frozen = k.EmgRuleCountOfKey(ElecKey) == c0 && k.StateHash() == h0;
                int resumed = RunElec(k, Dt, 0.5f, 0f, false);
                Expect(frozen && resumed == 1, $"暂停 600 步：电解次数不变、状态哈希不变（{frozen}）；恢复后 0.5 秒内电解 {resumed} 次");
            }
            var sustained = new List<int>();
            var single = new List<int>();
            foreach (float m in new[] { 0.5f, 1f, 2f, 3f })
            {
                float dt = m / 60f;
                using (CombatKernel k = AcidKernel())
                {
                    k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                    sustained.Add(RunElec(k, dt, 4f, 1f, true));
                }
                using (CombatKernel k = AcidKernel())
                {
                    k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                    single.Add(RunElec(k, dt, 0.5f, 0f, false));
                }
            }
            Expect(sustained.All(n => n == 4) && single.All(n => n == 1),
                $"倍速矩阵 0.5x / 1x / 2x / 3x：持续注电 4 游戏秒电解 {string.Join(" / ", sustained)} 次（应都是 4）；单次注电 0.5 游戏秒内 {string.Join(" / ", single)} 次（应都是 1）");
        }

        // ── R3 存读档 ────────────────────────────────────────────────────────

        private static void CheckElecSaveLoad()
        {
            Line("  · R3. 冷却中途存读档：CondSince 进快照，读回后同样等满 1 秒才再电解，续跑哈希一致");
            using CombatKernel a = AcidKernel();
            using CombatKernel b = NewKernel();
            a.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
            int first = RunElec(a, Dt, 0.25f, 1f, true); // 第一个传导节拍电解
            RunElec(a, Dt, 0.4f, 1f, true); // 冷却中途（离上次电解约 0.4 秒，酸斑块仍带电 ≥ 0.4）
            float q = Patches(a).Where(z => z.Matter == EmgMatterConst.Acid).Select(z => z.Charge).DefaultIfEmpty().Max();
            byte[] snap = a.Serialize();
            CombatLoadResult lr = b.Load(snap);
            int ta = a.PatchStats.ConductTicks, tb = b.PatchStats.ConductTicks;
            int coolA = 0, coolB = 0, totalA = 0, totalB = 0;
            int c0a = a.EmgRuleCountOfKey(ElecKey), c0b = b.EmgRuleCountOfKey(ElecKey);
            for (int s = 0; s < 180; s++)
            {
                a.Step(Dt, a.Time + Dt);
                b.Step(Dt, b.Time + Dt);
                if (a.PatchStats.ConductTicks != ta)
                {
                    ta = a.PatchStats.ConductTicks;
                    a.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                }
                if (b.PatchStats.ConductTicks != tb)
                {
                    tb = b.PatchStats.ConductTicks;
                    b.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
                }
                if (s == 20) // 读档后约 0.35 秒：离上次电解约 0.75 秒，还在冷却里（期间至少过 1 个传导节拍）
                {
                    coolA = a.EmgRuleCountOfKey(ElecKey) - c0a;
                    coolB = b.EmgRuleCountOfKey(ElecKey) - c0b;
                }
            }
            totalA = a.EmgRuleCountOfKey(ElecKey) - c0a;
            totalB = b.EmgRuleCountOfKey(ElecKey) - c0b;
            Expect(lr == CombatLoadResult.Ok && first == 1 && q >= 0.4f && coolA == 0 && coolB == 0 && totalA == totalB && totalA == 3 && a.StateHash() == b.StateHash(),
                $"读档 {lr}；存档前已电解 {first} 次、酸斑块电荷 {q:0.##}（够阈值，只靠冷却挡住）；读回后冷却期内两边电解 {coolA} / {coolB} 次；续跑 3 秒两边 {totalA} / {totalB} 次（应 3）、哈希一致");
        }

        // ── R4 边界 ──────────────────────────────────────────────────────────

        private static void CheckElecEdges()
        {
            Line("  · R4. 斑块当拍被电解吃光（份量 × 0.8 ≤ 0.01 即消失）不出错；同一位置新生成的酸斑块不继承冷却（生成时 CondSince 清零）");
            using CombatKernel k = AcidKernel(0.012f);
            bool spawned = CountOf(k, EmgMatterConst.Acid) == 1;
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
            int n1 = RunElec(k, Dt, 0.25f, 0f, false);
            bool gone = CountOf(k, EmgMatterConst.Acid) == 0;
            k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 5f, 60f, 0, false, 1);
            StepN(k, 1);
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 1);
            int n2 = RunElec(k, Dt, 0.25f, 0f, false);
            Expect(spawned && n1 == 1 && gone && n2 == 1 && CountOf(k, EmgMatterConst.Acid) == 1,
                $"0.012 份的酸斑块电解 {n1} 次后被吃光（剩 {CountOf(k, EmgMatterConst.Acid)} 块前为 {gone}）；离上次不到 1 秒在原地新生成的酸斑块通电，下一拍就电解 {n2} 次（不继承旧斑块的冷却）");
        }

        // ── R5 热交换与场面大小无关 ──────────────────────────────────────────

        /// <summary>fillers 块互不相交的冰（-10 度，不回温，每拍都是热源）+ 熔融金属 + 明火 + 相邻的油（三者 Id 最大）；返回熔融凝成冷渣、油被点燃的步数（-1 = 15 秒内没发生）。</summary>
        private static (int slag, int lit, int sources) HeatScene(int fillers)
        {
            using CombatKernel k = BigQuotaKernel();
            SpawnMany(k, fillers, i => (EmgMatterConst.Ice, new double2(200 + i % 24 * 2.5, i / 24 * 2.5), 0.5f, 2f));
            ToNextConduct(k);
            k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 40), 0.5f, 2f, 60f, 0, false, 0);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(40, 0), 2f, 1f, 60f, 0, false, 7);
            k.SpawnPatch(EmgMatterConst.Oil, new double2(42.34, 0), 2f, 5f, 0f, 0, false, 0);
            int slag = -1, lit = -1;
            int sources = 0;
            for (int s = 1; s <= 900 && (slag < 0 || lit < 0); s++)
            {
                StepN(k, 1);
                if (s == 2)
                {
                    sources = Patches(k).Count(z => Mathf.Abs(z.Temp - 20f) > 0.5f);
                }
                if (lit < 0 && CountOf(k, EmgMatterConst.Oil) == 0)
                {
                    lit = s;
                }
                if (slag < 0 && CountOf(k, EmgMatterConst.Molten) == 0 && CountOf(k, EmgMatterConst.Slag) >= 1)
                {
                    slag = s;
                }
            }
            return (slag, lit, sources);
        }

        private static void CheckHeatScale()
        {
            Line("  · R5. 热交换每拍、回落每秒 40 度，与场面大小无关：120 块与 480 块热源（后者 > 电预算 256），熔融 → 冷渣约 10 秒、油挨明火约 0.5 秒点燃，两种场面逐步一致");
            (int slag, int lit, int sources) small = HeatScene(118), big = HeatScene(478);
            Expect(small.sources >= 120 && big.sources >= 480 && small.slag == big.slag && small.lit == big.lit,
                $"热源 {small.sources} 块：冷渣第 {small.slag} 步、点燃第 {small.lit} 步；热源 {big.sources} 块：冷渣第 {big.slag} 步、点燃第 {big.lit} 步（逐步一致；旧口径 256 预算轮转时大场面要等轮到）");
            Expect(small.slag >= 570 && small.slag <= 660, $"熔融金属 1000 度 → 低于 600 度凝成冷渣：{small.slag / 60f:0.##} 秒（约 10 秒 = 400 度 ÷ 40 度 / 秒，按节拍取整 9.5～11 秒）");
            Expect(small.lit > 0 && small.lit <= 45, $"油挨明火：{small.lit / 60f:0.##} 秒点燃（约 0.5 秒，≤ 0.75 秒）");
        }

        // ── R6 热源 0 块、定温源 / 世界源、暂停与倍速 ─────────────────────────

        private static float TempOf(CombatKernel k, byte m, bool world) =>
            Patches(k).Where(z => z.Matter == m && ((z.PFlags & EmgMatterConst.FlagWorld) != 0) == world).Select(z => z.Temp).DefaultIfEmpty(float.NaN).First();

        private static void CheckHeatEdges()
        {
            Line("  · R6. 热源 0 块时热交换不访问；定温源（明火）与世界源（常驻熔融）不回温；回温按游戏秒：暂停不变、0.5x～3x 一致");
            using (CombatKernel k = NewKernel())
            {
                for (int i = 0; i < 4; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Water, new double2(10 * i, 0), 2f, 5f, 0f, 0, false, 0);
                }
                StepN(k, 1);
                int t0 = k.PatchStats.ConductTicks;
                StepN(k, 31);
                Expect(k.PatchStats.ConductTicks - t0 >= 2 && k.PatchStats.ConductLastVisits == 0 && Patches(k).All(z => Mathf.Abs(z.Temp - 20f) < 1e-4f),
                    $"只有常温水洼（热源 0 块）：过 {k.PatchStats.ConductTicks - t0} 个传导节拍，最近一拍访问 {k.PatchStats.ConductLastVisits}（应 0），温度都还是 20 度");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 60f, 0, false, 7);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(30, 0), 2f, 4f, 60f, 0, true, 0);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(60, 0), 2f, 4f, 60f, 0, false, 0);
                StepN(k, 1);
                float f0 = TempOf(k, EmgMatterConst.Fire, false), w0 = TempOf(k, EmgMatterConst.Molten, true), m0 = TempOf(k, EmgMatterConst.Molten, false);
                StepN(k, 180);
                float f1 = TempOf(k, EmgMatterConst.Fire, false), w1 = TempOf(k, EmgMatterConst.Molten, true), m1 = TempOf(k, EmgMatterConst.Molten, false);
                Expect(Mathf.Approximately(f0, f1) && Mathf.Approximately(w0, w1) && m0 - m1 >= 110f && m0 - m1 <= 130f,
                    $"3 秒后：明火（定温源）{f0:0.#} → {f1:0.#} 度、世界源熔融 {w0:0.#} → {w1:0.#} 度（都不变）；对照普通熔融 {m0:0.#} → {m1:0.#} 度（回落约 120 度）");
            }
            var after = new List<float>();
            float paused = float.NaN, before = float.NaN;
            foreach (float m in new[] { 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 4f, 60f, 0, false, 0);
                StepN(k, 1);
                if (m == 1f)
                {
                    before = TempOf(k, EmgMatterConst.Molten, false);
                    for (int s = 0; s < 300; s++)
                    {
                        k.Step(0f, k.Time);
                    }
                    paused = TempOf(k, EmgMatterConst.Molten, false);
                }
                StepDt(k, m / 60f, (int)Math.Round(2f / (m / 60f)));
                after.Add(TempOf(k, EmgMatterConst.Molten, false));
            }
            Expect(Mathf.Approximately(before, paused) && after.All(t => Mathf.Abs(t - after[0]) < 0.01f) && after[0] < 1000f - 70f,
                $"暂停 300 步熔融温度 {before:0.#} → {paused:0.#}（不变）；0.5x / 1x / 2x / 3x 各跑 2 游戏秒后 {string.Join(" / ", after.Select(t => t.ToString("0.#")))} 度（一致，回落约 80 度）");
        }
    }
}
