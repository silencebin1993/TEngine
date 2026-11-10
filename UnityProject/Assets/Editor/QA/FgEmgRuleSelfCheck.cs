using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// EMG2-RULE-01 通道规则引擎与能量传导的自动验收（FG19 FGR-EMG-084 / 085 / 088 / 090～093；ADR-EMG-001 ⑥ ⑬ ⑯；FGT-EMG-033 / 039 / 020 evt.react 生产者）。
    /// 卡片建议名“FgEmgMatterSelfCheck 规则段”，单独成段以便单跑。起真实内核（CombatKernel，Burst 步进）与真实表（fg.TbChannelRule、fg.TbMatter、fg.TbHomeTuning emg.*）：
    /// A 表与编译（61 行、只认码、按优先级索引、超容量整表拒绝、内核拒坏表）；B 热（点燃相邻油、5 块油整片点燃计数与因果链深度 ≤ 3、对照不点）；
    /// C 电（3 块相连水洼传到第 3 块里的单位、敌我都电、断开中间一块传不到、低于阈值停）；D 同一宿主两条规则同时满足只结算优先级小的、其余下一节拍；
    /// E 熔融冷却成冷渣、沸腾成蒸汽；F 持续加热让敌我武器过热停火、冷却液里过热喷蒸汽（V04，无总线监听也触发）；G 动能：无固件的普通射弹落在冰上把冰打碎（FG-GAP-052 前一半）、对照轻弹不碎；G2 连锁优先跳站在水洼里的单位、对照跳最近；
    /// H AI 避险（FGT-EMG-039）：守点教义绕开明火、关掉教义直穿、玩家显式移动不改写只警告一次、对照不穿过不警告；I evt.react 生产者（Code = 计数槽、宿主斑块）与对照；
    /// J 存读档（传导途中读档续跑哈希一致、传导相位按步序号）；K 传导那一步的尖峰耗时（只测一次）。
    /// 修复第 1 轮补：B 重叠 0.3 ≤ 0.75 秒点燃；C2 单节拍 ≤ 6 跳与跨节拍收敛、访问预算顺延、M14 淤塞阻断；C3 站在导电斑块里的单位是节点（斑块 ↔ 单位）；
    /// L 磁化注入与单位间传播（× 0.7 × 磁性、q_min、跳数上限、不接触不传）；H 接入机器不绕、带电水洼、链号 0 的危险物、迁入斑块按阵营、攻击移动按射程截断；
    /// K2 热交换与电传导各有预算、超出按节拍轮转。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgRuleSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgRuleSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgRuleSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/通道规则与能量传导")]
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
            Line("\n[通道规则与能量传导] EMG2-RULE-01：规则表 / 热 / 电 / 动能 / 同时满足 / AI 避险 / evt.react / 存读档 / 传导尖峰（FGT-EMG-033 / 039 / 020）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下同步编译，真机 IL2CPP + Burst，耗时只作量级参考）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                Step(CheckTable);
                Step(CheckHeat);
                Step(CheckElectric);
                Step(CheckElecBounds);
                Step(CheckElecUnits);
                Step(CheckMagnet);
                Step(CheckTwoRules);
                Step(CheckCoolBoil);
                Step(CheckOverheat);
                Step(CheckKinetic);
                Step(CheckChainWet);
                Step(CheckAvoid);
                Step(CheckReactEvent);
                Step(CheckSaveLoad);
                Step(CheckSpike);
                Step(CheckBudgetSplit);
            }
            catch (Exception e)
            {
                Fail($"规则自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [通道规则与能量传导] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(int cap = 64, int visits = 0, int hops = 0)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            if (visits > 0)
            {
                c.EmgConductVisits = visits; // 小场景：把访问预算调小，用少量斑块复现“超出预算”
            }
            if (hops > 0)
            {
                c.EmgConductHops = hops;
            }
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static List<CombatZone> Patches(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        private static int CountOf(CombatKernel k, byte m) => Patches(k).Count(z => z.Matter == m);

        private static CombatZone At(CombatKernel k, double2 p) => Patches(k).Where(z => math.distance(z.Pos, p) < 0.3).DefaultIfEmpty().First();

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static int Key(string id) => NamedReactionCatalog.StableKey(id);

        private static float Charge(CombatKernel k, double2 p) => At(k, p).Charge;

        private static float4 AccA(CombatKernel k, int id) => k.TryGetUnitEnergy(id, out float4 a, out _) ? a : default;

        private static float MagOf(CombatKernel k, int id) => k.TryGetUnitEnergy(id, out _, out float3 b) ? b.x : -1f;

        /// <summary>步进到下一个传导节拍刚结束（≤ 16 步）；读数是节拍末（规则求值、本节拍衰减之后）。</summary>
        private static void ToNextConduct(CombatKernel k)
        {
            int t0 = k.PatchStats.ConductTicks;
            for (int s = 0; s < 16 && k.PatchStats.ConductTicks == t0; s++)
            {
                StepN(k, 1);
            }
        }

        private static bool HasChain(CombatKernel k, EmgChainNode kind, Func<EmgChainRec, bool> pred)
        {
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == kind && pred(r))
                {
                    return true;
                }
            }
            return false;
        }

        // ── A 表与编译 ───────────────────────────────────────────────────────

        private static void CheckTable()
        {
            Line("  · A. 规则表：61 行编进内核、只认码、按优先级索引；超 256 行整表拒绝；内核拒坏表");
            IReadOnlyList<GameConfig.fg.ChannelRule> rows = ConfigSystem.Instance.Tables.TbChannelRule.DataList;
            EmgRuleCatalog.Compiled c = EmgRuleCatalog.Current;
            int live = c.Rules.Count(r => r.Op != EmgRuleConst.OpDeferred);
            Expect(rows.Count == 61 && c.Ok && c.Rules.Count == 61 && live == 52,
                $"fg.TbChannelRule {rows.Count} 行（FG19B 第 2 节 61 行），编译 {c.Rules.Count} 条、问题 {c.Problems.Count}；内核结算 {live} 条（M 18 + E 20 + V 4 阶段 2 行，C01～C08 八条单位沾染行，EMG4-TER-01 起 + V06 / V07 熔坑 = 52；P / F / W 由各自的相态、场、残骸步按端口找行；仍 deferred 的 V03 焦土随 EMG4-TER-02、V08 冻结桥随水面 FG18-SEA-01）");
            Expect(c.Rules.All(r => (r.A >= 300 && r.A < 1200) || (r.A >= EmgRuleConst.CoatBase && r.A < EmgRuleConst.CoatBase + 32))
                   && c.Rules.All(r => (r.B >= 300 && r.B < 1200) || (r.B >= EmgRuleConst.CoatBase && r.B < EmgRuleConst.CoatBase + 32)),
                "内核规则两侧只有端口码（300～1199）或沾染位码（2000 + 位号），没有任何内容 ID / 规则名（FGR-EMG-013 / 084）");
            Expect(c.Rules.All(r => r.Main >= EmgRuleConst.MainS && r.Main <= EmgRuleConst.MainP) && rows.All(r => r.Side != "side.mult" || r.SideValue <= 1.25f),
                "主结果全部 ∈ S / T / B / P，附带倍率 ≤ 1.25（FGR-EMG-085）");
            // 超容量：同一批行重复到 257 行 → 编译整表拒绝；内核 SetEmgRules 同样拒绝 257 条
            var many = new List<GameConfig.fg.ChannelRule>();
            while (many.Count < 257)
            {
                many.AddRange(rows);
            }
            many = many.Take(257).ToList();
            EmgRuleCatalog.Compiled over = EmgRuleCatalog.Compile(many, 256);
            using (CombatKernel k = NewKernel())
            {
                var big = Enumerable.Repeat(c.Rules[0], 257).ToList();
                bool kernelRejects = !k.SetEmgRules(big) && k.EmgRuleCount == 61;
                var bad = new List<EmgRule> { c.Rules[0] };
                EmgRule br = bad[0];
                br.Op = 99;
                bad[0] = br;
                Expect(!over.Ok && over.Rules.Count == 0 && over.Problems[0].Contains("257") && kernelRejects && !k.SetEmgRules(bad) && k.EmgRuleCount == 61,
                    $"257 行整表拒绝：编译问题“{over.Problems.FirstOrDefault()}”；内核 SetEmgRules(257 条) 与坏结算方式都拒绝、保留原 61 条");
                // 索引：水（物质码 2）在 a 侧的规则按优先级升序
                var water = new List<int>();
                for (int i = 0; i < k.EmgRuleCount; i++)
                {
                    EmgRule r = k.EmgRuleAt(i);
                    if (r.Op != EmgRuleConst.OpDeferred && r.A == 402)
                    {
                        water.Add(r.Priority);
                    }
                }
                Expect(water.Count >= 4 && water.Contains(22) && water.Contains(30) && water.Contains(40), $"a 侧是水的规则（E07 / E09 / E14 / M04 / M14…）优先级 {string.Join(",", water)}");
            }
        }

        // ── B 热 ─────────────────────────────────────────────────────────────

        private static void CheckHeat()
        {
            Line("  · B. 热：明火点燃相邻油斑块（热交换 → E01）、5 块相连油整片点燃（计数 5、因果链都在点火那条链上、链深 ≤ 3）；对照：没有明火不点");
            int key = Key("reaction_deflagrate");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, 0, false, 7);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, 0, false, 0);
                int before = k.EmgRuleCountOfKey(key);
                StepN(k, 240);
                bool lit = CountOf(k, EmgMatterConst.Oil) == 0 && CountOf(k, EmgMatterConst.Fire) >= 2;
                Expect(lit && k.EmgRuleCountOfKey(key) - before == 1 && HasChain(k, EmgChainNode.Rule, r => r.Key == key && r.Chain == 7),
                    $"相邻油被热交换加热到 180 度整片变明火（油 {CountOf(k, EmgMatterConst.Oil)}、明火 {CountOf(k, EmgMatterConst.Fire)}），爆燃计数 +{k.EmgRuleCountOfKey(key) - before}，因果链“规则”记在点火的链 7 上");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 40f, 0, false, 9);
                for (int i = 1; i <= 5; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2(3 * i, 0), 2f, 5f, 0f, 0, false, 0);
                }
                StepN(k, 1);
                int before = k.EmgRuleCountOfKey(key);
                StepN(k, 1200);
                int lit = k.EmgRuleCountOfKey(key) - before;
                var recs = new List<EmgChainRec>();
                for (int i = 0; i < k.EmgChainRecordCount; i++)
                {
                    if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Rule && r.Key == key)
                    {
                        recs.Add(r);
                    }
                }
                Expect(lit == 5 && CountOf(k, EmgMatterConst.Oil) == 0 && recs.Count == 5 && recs.All(r => r.Chain == 9 && r.Depth <= EmgConst.MaxDepth),
                    $"5 块相连油全部被点燃（爆燃 +{lit}），因果链 5 条“规则”都在链 9、链深 {string.Join(",", recs.Select(r => r.Depth))} ≤ 3（原地转化 / 热交换不加链深，FGT-EMG-033）");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, 0, false, 0);
                StepN(k, 240);
                Expect(CountOf(k, EmgMatterConst.Oil) == 1 && k.EmgRuleCountOfKey(key) == 0, "对照：没有明火的油斑块 4 秒后还是油，计数 0");
            }
            using (CombatKernel k = NewKernel())
            {
                // FGT-EMG-033：重叠比 0.3（两个半径 2 的圆，圆心距 2.34 米：(2/π)(acos x − x√(1−x²)) = 0.30，x = 2.34 ÷ 4）→ 每节拍升温约 94 度，两个节拍越过 180 度
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, 0, false, 8);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(2.34, 0), 2f, 5f, 0f, 0, false, 0);
                int steps = -1;
                for (int s = 1; s <= 90 && steps < 0; s++)
                {
                    StepN(k, 1);
                    steps = CountOf(k, EmgMatterConst.Oil) == 0 ? s : -1;
                }
                Expect(steps > 0 && steps <= 45, $"油与明火重叠 0.3：第 {steps} 步（{steps / 60f:0.##} 秒）油被点燃，≤ 0.75 秒（45 步，含传导节拍与斑块节拍的相位，FGT-EMG-033）");
            }
        }

        // ── C 电 ─────────────────────────────────────────────────────────────

        private static (float hpFoe, float hpAlly, float acc, float c3, float c2) RunWater(bool middle, float charge)
        {
            using CombatKernel k = NewKernel();
            k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 2f, 5f, 0f, 0, false, 3);
            if (middle)
            {
                k.SpawnPatch(EmgMatterConst.Water, new double2(3, 0), 2f, 5f, 0f, 0, false, 3);
            }
            k.SpawnPatch(EmgMatterConst.Water, new double2(6, 0), 2f, 5f, 0f, 0, false, 3);
            int foe = Unit(k, CombatFaction.Hostile, new double2(6, 0.5), -1, 200f);
            int ally = Unit(k, CombatFaction.Player, new double2(6, -0.5), -1, 200f);
            StepN(k, 20); // 单位进入斑块（接触桶 0.25 秒）
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, charge, 3);
            float c2 = 0f, c3 = 0f;
            for (int s = 0; s < 16; s++)
            {
                StepN(k, 1);
                c2 = math.max(c2, At(k, new double2(3, 0)).Charge);
                c3 = math.max(c3, At(k, new double2(6, 0)).Charge);
            }
            k.TryGetUnitEnergy(foe, out float4 a, out _);
            return (Hp(k, foe), Hp(k, ally), a.z, c3, c2);
        }

        private static void CheckElectric()
        {
            Line("  · C. 电：3 块相连水洼传到第 3 块里的单位（敌我都电，E14 导电水洼）；断开中间一块传不到；低于阈值停止传导");
            var on = RunWater(true, 1f);
            // 读数是传导节拍末（电荷规则求值之后、本节拍衰减之后）：传导当时第 3 块 = 1.0 × (0.7 × 0.9)² ≈ 0.397 ≥ E14 阈值 0.2，衰减后约一半
            Expect(on.c3 > 0.15f && on.c2 > on.c3 && on.hpFoe < 200f && on.hpAlly < 200f && on.acc > 0f,
                $"第 1 块注电 1.0 → 节拍末第 2 块 {on.c2:0.###}、第 3 块 {on.c3:0.###}（每跳 × 0.7 × 导电 0.9，已过本节拍衰减），第 3 块里的敌人 {on.hpFoe:0.#} 血、己方 {on.hpAlly:0.#} 血都被电到（物质中立），敌人电荷累积 {on.acc:0.###}");
            var cut = RunWater(false, 1f);
            Expect(cut.c3 == 0f && Mathf.Approximately(cut.hpFoe, 200f) && Mathf.Approximately(cut.hpAlly, 200f) && cut.acc == 0f,
                $"断开中间一块：第 3 块电荷 {cut.c3:0.###}，里面的单位不掉血（{cut.hpFoe:0.#} / {cut.hpAlly:0.#}）、电荷 {cut.acc:0.###}");
            var weak = RunWater(true, 0.06f);
            Expect(weak.c2 == 0f && weak.c3 == 0f && Mathf.Approximately(weak.hpFoe, 200f),
                $"低于阈值：注 0.06 → 下一块接收 0.06 × 0.7 × 0.9 < q_min 0.05，传导停止（第 2 块 {weak.c2:0.###}、第 3 块 {weak.c3:0.###}）");
        }

        // ── C2 传导边界 ──────────────────────────────────────────────────────

        private static void CheckElecBounds()
        {
            Line("  · C2. 电传导边界（FGR-EMG-091 / FG19B §1.4 第 5 条）：单节拍 ≤ 6 跳、每跳 × 0.7 × 导电；跨节拍余电随衰减收敛、第 8 块起不带电也不伤人；访问预算用完时剩余能量留在节点上、下一节拍接着传；第 2 块被粉尘淤塞成污泥（M14）后传不到第 3 块（FGT-EMG-033）");
            using (CombatKernel k = NewKernel())
            {
                for (int i = 0; i < 10; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Water, new double2(3 * i, 0), 2f, 5f, 0f, 0, false, 3);
                }
                int near = Unit(k, CombatFaction.Hostile, new double2(15, 0), -1, 200f); // 第 6 块（5 跳）
                int far = Unit(k, CombatFaction.Hostile, new double2(21, 0), -1, 200f); // 第 8 块（7 跳）
                StepN(k, 20);
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 4f, 3);
                ToNextConduct(k);
                float[] c = Enumerable.Range(0, 10).Select(i => Charge(k, new double2(3 * i, 0))).ToArray();
                bool ratio = Enumerable.Range(0, 6).All(i => c[i] > 0f && Mathf.Abs(c[i + 1] / c[i] - 0.63f) < 0.02f);
                Expect(c.Take(7).All(x => x > 0f) && c.Skip(7).All(x => x == 0f) && ratio,
                    $"注电 4.0 后第一个节拍（读数已过本节拍 × 0.5 衰减）：{string.Join(" / ", c.Select(x => x.ToString("0.###")))}——第 1～7 块（0～6 跳）带电、逐跳 × 0.63（0.7 × 导电 0.9），第 8 块起为 0（单节拍 ≤ 6 跳；按 q_min 本可再传一跳 0.157）");
                float beyond = 0f;
                for (int t = 0; t < 10; t++)
                {
                    k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 4f, 3); // 源头每个节拍都被电武器命中（持续通电）：中间各块节拍开始时都留有余电、都是起点
                    int t0 = k.PatchStats.ConductTicks;
                    for (int s = 0; s < 16 && k.PatchStats.ConductTicks == t0; s++)
                    {
                        StepN(k, 1);
                        for (int i = 7; i < 10; i++)
                        {
                            beyond = math.max(beyond, Charge(k, new double2(3 * i, 0)));
                        }
                    }
                }
                Expect(beyond <= 0.5f * 0.7f + 1e-3f && Mathf.Approximately(Hp(k, far), 200f) /* EMG2-PHS-01：第 7 块里的单位麻痹放电 0.5 给第 8 块里的单位，它再把电带进第 8 块（FG19B §3.4“放电进导电水洼”），网格本身不越过 6 跳 */ && AccA(k, far).z <= 0.5f + 1e-3f && Hp(k, near) < 200f, // EMG2-PHS-01：第 7 块里的单位电荷满麻痹时向 4 米内导体放电 0.5（第 8 块的单位只可能从这里得电，不挨电）
                    $"之后 10 个节拍源头每拍都注电 4.0：第 8～10 块每步读数最大 {beyond:0.###}（≤ 0.35：只来自第 7 块单位的麻痹放电；起点按节拍开始时的余电出发、不重读本节拍刚抬高的值；越界的那一跳 ≈ 0.079 低于放电阈值 0.2、节拍末衰减后归零），第 8 块里的单位 {Hp(k, far):0.#} 血、电荷 {AccA(k, far).z:0.###}（≤ 邻居麻痹放电 0.5）；对照第 6 块里的单位被电到 {Hp(k, near):0.#} 血");
            }
            using (CombatKernel k = NewKernel(visits: 4))
            {
                for (int i = 0; i < 8; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Water, new double2(3 * i, 0), 2f, 5f, 0f, 0, false, 3);
                }
                StepN(k, 1);
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 4f, 3);
                int carried0 = k.PatchStats.ConductCarried;
                ToNextConduct(k);
                float c4 = Charge(k, new double2(12, 0)), c5First = Charge(k, new double2(15, 0));
                bool carried = k.PatchStats.ConductCarried > carried0 && k.PatchStats.ConductLastVisits <= 4;
                float c5 = 0f;
                int ticks = 0;
                for (int t = 0; t < 6 && c5 == 0f; t++)
                {
                    k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 4f, 3); // 源头持续通电
                    ToNextConduct(k);
                    ticks++;
                    c5 = Charge(k, new double2(15, 0));
                }
                Expect(carried && c4 > 0f && c5First == 0f && c5 > 0f,
                    $"访问预算 4（小场景）：第一个节拍访问 {4} 个节点即停、顺延 +{k.PatchStats.ConductCarried - carried0}，第 5 块已带电 {c4:0.###}、第 6 块 {c5First:0.###}；之后第 {ticks} 个节拍从留在节点上的电荷接着传到第 6 块 {c5:0.###}（起点按节拍轮转，不固定截断）");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 2f, 5f, 0f, 0, false, 3);
                k.SpawnPatch(EmgMatterConst.Water, new double2(3, 0), 2f, 5f, 0f, 0, false, 3);
                k.SpawnPatch(EmgMatterConst.Water, new double2(6, 0), 2f, 5f, 0f, 0, false, 3);
                k.SpawnPatch(EmgMatterConst.Dust, new double2(3, 2.3), 1.5f, 3f, 0f, 0, false, 3); // 只压在第 2 块上（重叠比约 0.3 ≥ M14 的 0.2），不碰第 1、3 块
                StepN(k, 60);
                bool silted = CountOf(k, EmgMatterConst.Water) == 2 && At(k, new double2(3, 0)).Matter == EmgMatterConst.Sludge;
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
                float c1 = 0f, c3 = 0f;
                for (int s = 0; s < 32; s++)
                {
                    StepN(k, 1);
                    c1 = math.max(c1, Charge(k, new double2(0, 0)));
                    c3 = math.max(c3, Charge(k, new double2(6, 0)));
                }
                Expect(silted && c1 > 0f && c3 == 0f,
                    $"第 2 块被粉尘淤塞成污泥（M14，污泥导电 0.2 < 0.3）后：第 1 块注电 {c1:0.###}，第 3 块 {c3:0.###}（传不过去）；对照见 C 段连通时第 3 块带电");
            }
        }

        // ── C3 站在导电斑块里的单位是节点 ─────────────────────────────────────

        private static void CheckElecUnits()
        {
            Line("  · C3. 站在导电斑块里的单位是传导节点（FG19B §1.4 第 5 条）：从斑块分得 unit_share 0.6 × 电荷（碎屑没有放电规则也一样）；带电单位走进干水洼让水洼带电（单位 → 斑块）；对照：斑块外的单位、没有单位时都不带电");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Debris, new double2(0, 0), 2f, 5f, 0f, 0, false, 3);
                int inU = Unit(k, CombatFaction.Hostile, new double2(0, 0.5), -1, 200f);
                int outU = Unit(k, CombatFaction.Hostile, new double2(5, 0), -1, 200f);
                StepN(k, 20);
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
                ToNextConduct(k);
                float a = AccA(k, inU).z, b = AccA(k, outU).z;
                Expect(a > 0.5f && a < 0.61f && b == 0f, // EMG2-PHS-01：电荷衰减改由 fg.TbPhaseRule（0.4 / 秒，每步）结算，不再在节拍末 × 0.5
                    $"金属碎屑（导电 0.5）带电 1.0：站在里面的单位分得 0.6 × 1.0、按 phase_charge 每秒 0.4 衰减后 {a:0.###}（≈ 0.6）；碎屑外的单位 {b:0.###}");
            }
            foreach (bool carrier in new[] { true, false })
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Debris, new double2(0, 0), 1f, 5f, 0f, 0, false, 3);
                k.SpawnPatch(EmgMatterConst.Water, new double2(6, 0), 1.5f, 5f, 0f, 0, false, 3); // 与碎屑不相交
                int u = carrier ? Unit(k, CombatFaction.Player, new double2(0, 0), -1, 500f) : 0;
                StepN(k, 20);
                k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1.5f, 3); // EMG2-PHS-01：单位分得 0.9 < 麻痹阈值 1（满了会放电清零）
                ToNextConduct(k);
                float q = carrier ? AccA(k, u).z : 0f;
                if (carrier)
                {
                    k.SetPosition(u, new double2(6, 0));
                }
                float wet = 0f;
                for (int s = 0; s < 45; s++)
                {
                    StepN(k, 1);
                    wet = math.max(wet, Charge(k, new double2(6, 0)));
                }
                if (carrier)
                {
                    Expect(q > 0.5f && wet > 0f, $"单位在带电碎屑里得电 {q:0.###} 后走进干水洼：水洼带电 {wet:0.###}（单位 → 斑块一跳 × 0.7 × 导电 0.9；两块斑块不相交）");
                }
                else
                {
                    Expect(wet == 0f, "对照：没有带电单位走进去，水洼一直不带电");
                }
            }
        }

        // ── L 磁化 ───────────────────────────────────────────────────────────

        private static void CheckMagnet()
        {
            Line("  · L. 磁化（FGR-EMG-090 / 091）：en.mag 命中注入目标磁化度（上限 2）；传导节拍沿磁性接触在单位之间每跳 × 0.7 × 接收方磁性（钢 0.9）传播、低于 q_min 停止、不超过跳数上限；不接触不传");
            (int prog, float hit, float[] m, float[] peak) Run(int hops, double spacing, int extraTicks = 0)
            {
                using CombatKernel k = NewKernel(hops: hops);
                var instrs = new List<EmgInstr> { new EmgInstr { Port = EmgConst.PortMag, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Ordinal = 0, Amount = 2f } };
                var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
                int p = k.AddProgram(new EmgProgram(), nodes, instrs);
                int me = Unit(k, CombatFaction.Player, new double2(0, 4), k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 8f, Damage = 1f, Program = p }), 500f);
                var ids = Enumerable.Range(0, 9).Select(i => Unit(k, CombatFaction.Hostile, new double2(spacing * i, 10), -1, 500f)).ToList();
                StepN(k, 2);
                k.FireAt(me, ids[0], k.Time);
                float hit = MagOf(k, ids[0]);
                ToNextConduct(k);
                float[] m = ids.Select(id => MagOf(k, id)).ToArray();
                var peak = new float[ids.Count];
                for (int t = 0; t < extraTicks; t++)
                {
                    k.FireAt(me, ids[0], k.Time); // 持续命中：中间各台节拍开始时都留有余量、都是起点
                    int t0 = k.PatchStats.ConductTicks;
                    for (int s = 0; s < 16 && k.PatchStats.ConductTicks == t0; s++)
                    {
                        StepN(k, 1);
                        for (int i = 0; i < ids.Count; i++)
                        {
                            peak[i] = math.max(peak[i], MagOf(k, ids[i]));
                        }
                    }
                }
                return (p, hit, m, peak);
            }
            var chain = Run(0, 1.1);
            bool ratio = Enumerable.Range(0, 5).All(i => chain.m[i] > 0f && Mathf.Abs(chain.m[i + 1] / chain.m[i] - 0.63f) < 0.02f);
            Expect(chain.prog >= 0 && chain.hit > 1.45f /* EMG2-PHS-01：磁化度上限 fg.TbPhaseRule cap 1.5 */ && ratio && chain.m[6] > 0f && chain.m[7] == 0f && chain.m[8] == 0f,
                $"命中注入磁化 {chain.hit:0.###}（上限 1.5）；节拍末沿 9 台相距 1.1 米的钢制敌人：{string.Join(" / ", chain.m.Select(x => x.ToString("0.###")))}——逐跳 × 0.63，第 8 台起低于 q_min 停止");
            var cut = Run(3, 1.1);
            Expect(cut.m.Take(4).All(x => x > 0f) && cut.m.Skip(4).All(x => x == 0f),
                $"跳数上限调到 3：{string.Join(" / ", cut.m.Select(x => x.ToString("0.###")))}——第 5 台按 q_min 本可得 0.157，被跳数上限截住");
            var held = Run(3, 1.1, 10);
            Expect(held.peak.Take(4).All(x => x > 0f) && held.peak[4] < 0.24f && Enumerable.Range(4, 3).All(i => held.peak[i] <= held.peak[i - 1] * 0.64f) && held.peak[7] == 0f && held.peak[8] == 0f, // EMG2-PHS-01：磁化度按 0.15 / 秒衰减（不再节拍末 × 0.5），带磁的单位跨节拍再作起点，链每节拍最多外延一跳、逐跳 × 0.63、低于 q_min 停
                $"跳数上限 3、之后 10 个节拍每拍都命中第 1 台：各台每步读数最大 {string.Join(" / ", held.peak.Select(x => x.ToString("0.###")))}——起点按入队时的余量出发；第 5 台只有跨节拍越界一跳（≤ 1.5 × 0.63⁴ ≈ 0.236；EMG2-PHS-01 起不在节拍末 × 0.5），第 6、7 台只靠跨节拍逐跳外延（仍 × 0.63），第 8 台起低于 q_min 始终为 0");
            var apart = Run(0, 1.5);
            Expect(apart.m[0] > 0f && apart.m.Skip(1).All(x => x == 0f), $"对照：相距 1.5 米（超过半径和 + 0.2）不接触，只有被命中的那台有磁化 {apart.m[0]:0.###}");
        }

        // ── D 同一宿主两条规则 ─────────────────────────────────────────────────

        private static void CheckTwoRules()
        {
            Line("  · D. 同一宿主两条规则同时满足：只结算优先级最小的一条（E07 沸腾 22 先于 M14 淤塞 57），其余留到下一节拍");
            using CombatKernel k = NewKernel();
            k.SpawnPatch(EmgMatterConst.Water, new double2(0, 0), 2f, 5f, 0f, 0, false, 4);
            // 粉尘放在注热点 0.3 米范围外（圆心距 2.5、两圆重叠比约 0.26 ≥ M14 的 0.2）：只加热水，否则热粉尘会把沸腾后的水重新烘到 100 度、E07 一直抢先
            k.SpawnPatch(EmgMatterConst.Dust, new double2(2.5, 0), 2f, 3f, 0f, 0, false, 4);
            StepN(k, 1);
            k.InjectPatchEnergy(new double2(0, 0), 0.3f, EmgConst.PortHeat, 65f, 4); // ΔT = 65 × 10 ÷ 5 = 130 → 150 度
            int deferred0 = k.PatchStats.RulesDeferred;
            int boil = 0;
            bool waterStill = false;
            for (int s = 0; s < 16 && boil == 0; s++)
            {
                StepN(k, 1);
                boil = CountOf(k, EmgMatterConst.Steam);
                waterStill = CountOf(k, EmgMatterConst.Water) == 1 && CountOf(k, EmgMatterConst.Sludge) == 0;
            }
            int deferred = k.PatchStats.RulesDeferred - deferred0;
            StepN(k, 20);
            Expect(boil >= 1 && waterStill && deferred >= 1 && CountOf(k, EmgMatterConst.Sludge) == 1 && CountOf(k, EmgMatterConst.Water) == 0,
                $"第一个节拍只沸腾（蒸汽 {boil}、水还在、顺延 {deferred} 条），下一节拍才淤塞成污泥（污泥 {CountOf(k, EmgMatterConst.Sludge)}）"
                + (CountOf(k, EmgMatterConst.Sludge) == 1 ? string.Empty : "；斑块：" + string.Join("、", Patches(k).Select(z => $"物质 {z.Matter} 温度 {z.Temp:0} 份量 {z.Amount:0.##} 半径 {z.Radius:0.##} @({z.Pos.x:0.#},{z.Pos.y:0.#})"))));
        }

        // ── E 冷却 / 沸腾 ────────────────────────────────────────────────────

        private static void CheckCoolBoil()
        {
            Line("  · E. 熔融金属回温到 600 度以下凝成冷渣（E11）；冷却液被加热到 130 度沸腾成蒸汽（E08）");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 4f, 30f, 0, false, 0);
                StepN(k, 540);
                bool still = CountOf(k, EmgMatterConst.Molten) == 1;
                StepN(k, 180);
                Expect(still && CountOf(k, EmgMatterConst.Slag) == 1 && CountOf(k, EmgMatterConst.Molten) == 0,
                    $"熔融金属 9 秒时仍是熔融（每秒回落 40 度），12 秒时凝成冷渣（冷渣 {CountOf(k, EmgMatterConst.Slag)}）");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Coolant, new double2(0, 0), 2f, 5f, 0f, 0, false, 0);
                StepN(k, 1);
                k.InjectPatchEnergy(new double2(0, 0), 0.3f, EmgConst.PortHeat, 70f, 0); // → 150 度
                StepN(k, 20);
                Expect(CountOf(k, EmgMatterConst.Steam) >= 1 && CountOf(k, EmgMatterConst.Coolant) == 1, $"冷却液沸腾：蒸汽 {CountOf(k, EmgMatterConst.Steam)} 块，冷却液剩一部分");
            }
        }

        // ── F 过热 ───────────────────────────────────────────────────────────

        private static CombatWeapon HeatGun(float heatPerShot = 0f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = 1f, Cooldown = 0f, Program = -1,
            HeatPerShot = heatPerShot, OverheatAt = 60f, RecoverBelow = 10f, Dissipation = 0f,
        };

        private static bool Overheated(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Overheated) != 0;

        private static void CheckOverheat()
        {
            Line("  · F. 持续加热：站在明火里的己方机器与敌人武器都过热停火（对照：不在明火里不过热）；冷却液里过热喷蒸汽（V04，地点没有任何程序监听也触发）");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 30f, 30f, 0, false, 0);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0.3), k.AddWeapon(HeatGun()), 2000f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, -0.3), k.AddWeapon(HeatGun()), 2000f);
                int far = Unit(k, CombatFaction.Player, new double2(10, 0), k.AddWeapon(HeatGun()), 2000f);
                StepN(k, 150);
                Expect(Overheated(k, me) && Overheated(k, foe) && !Overheated(k, far),
                    $"2.5 秒后：明火里的己方机器过热 {Overheated(k, me)}、敌人过热 {Overheated(k, foe)}（敌我都算）；明火外的机器 {Overheated(k, far)}");
                Expect(k.FireAt(me, foe, k.Time) == CombatFireResult.Overheated, "过热的机器开火结果 = 过热（停火）");
            }
            int key = Key("reaction_steamvent");
            foreach (bool inCoolant in new[] { true, false })
            {
                using CombatKernel k = NewKernel();
                if (inCoolant)
                {
                    k.SpawnPatch(EmgMatterConst.Coolant, new double2(0, 0), 2f, 20f, 0f, 0, false, 0);
                }
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(HeatGun(70f)), 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), -1, 500f);
                StepN(k, 20);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int steam = CountOf(k, EmgMatterConst.Steam);
                if (inCoolant)
                {
                    Expect(steam == 1 && k.EmgRuleCountOfKey(key) == 1 && Overheated(k, me), $"冷却液里开火过热：脚下喷出蒸汽 {steam} 块、冷却蒸腾计数 {k.EmgRuleCountOfKey(key)}");
                }
                else
                {
                    Expect(steam == 0 && k.EmgRuleCountOfKey(key) == 0 && Overheated(k, me), "对照：不在冷却液里过热不喷蒸汽");
                }
            }
        }

        // ── G 动能 ───────────────────────────────────────────────────────────

        private static void CheckKinetic()
        {
            Line("  · G. 动能（FG-GAP-052 前一半）：没装任何固件的普通射弹落在冰上，伤害 × 载体动能系数累计到耐久 → 冰碎（E18：冰碴破片 + 化出水）；对照：轻弹不碎");
            foreach (float dmg in new[] { 120f, 4f })
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Ice, new double2(0, 10), 2f, 5f, 0f, 0, false, 0);
                var w = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = dmg, Cooldown = 0f, Program = -1,
                    SpawnFlight = 1, ProjectileSpeed = 30f, ProjectileRadius = 0.15f, ProjectileLife = 10f / 30f,
                };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 40), -1, 500f);
                StepN(k, 2);
                long deposits0 = k.PatchStats.KineticDeposits;
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                if (dmg > 50f)
                {
                    Expect(CountOf(k, EmgMatterConst.Ice) == 0 && CountOf(k, EmgMatterConst.Water) == 1 && k.PatchStats.KineticDeposits > deposits0,
                        $"重弹（{dmg}）寿命到落在冰上：动能落地 {k.PatchStats.KineticDeposits - deposits0} 次，冰碎（冰 {CountOf(k, EmgMatterConst.Ice)}、水 {CountOf(k, EmgMatterConst.Water)}）——不需要任何固件挂“动能”");
                }
                else
                {
                    Expect(CountOf(k, EmgMatterConst.Ice) == 1 && k.PatchStats.KineticDeposits > deposits0, $"对照：轻弹（{dmg}）落在冰上动能不到耐久，冰还在");
                }
            }
        }

        // ── G2 连锁优先导电斑块 ──────────────────────────────────────────────

        /// <summary>FGR-EMG-091 末条（兑现电弧“沿湿面优先”）：没有任何浸湿状态位，只是站在水洼（导电 0.9）里的单位也算导电，连锁第一跳优先跳它；对照：没有水洼时跳最近的。</summary>
        private static void CheckChainWet()
        {
            Line("  · G2. 连锁优先跳站在导电斑块里的单位（FGR-EMG-091，电弧“沿湿面优先”）；对照：同一站位没有水洼时跳最近的");
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_arcchain" }, FirmwareCarrier.Melee);
            float range = 0f;
            (float near, float wet) Run(bool puddle)
            {
                using CombatKernel k = NewKernel();
                var w = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = 10f, Cooldown = 0f };
                w.Reading = CombatSite.FoldProgramReadings(new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 90f }, pr, false);
                w.Program = CombatSite.RegisterProgram(k, pr);
                range = w.Reading.ChainRange > 0f ? w.Reading.ChainRange : 4f;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
                int t = Unit(k, CombatFaction.Hostile, new double2(0, 1.5), -1, 200f);
                // 两个候选都在第一跳够得着、在格斗扇形外；彼此相距 1.7 × 跳距（第二跳够不着另一个）
                int near = Unit(k, CombatFaction.Hostile, new double2(-0.8 * range, 1.5), -1, 200f);
                double2 wetPos = new double2(0.9 * range, 1.5);
                int wet = Unit(k, CombatFaction.Hostile, wetPos, -1, 200f);
                if (puddle)
                {
                    k.SpawnPatch(EmgMatterConst.Water, wetPos, 1f, 5f, 0f, 0, false, 0);
                }
                StepN(k, 20); // 进斑块（接触桶 0.25 秒）
                k.FireAt(me, t, k.Time);
                StepN(k, 1);
                return (200f - Hp(k, near), 200f - Hp(k, wet));
            }
            var on = Run(true);
            var off = Run(false);
            Expect(on.wet > 0f && Mathf.Approximately(on.near, 0f) && off.near > 0f && Mathf.Approximately(off.wet, 0f),
                $"跳距 {range:0.#} 米：站在水洼里的较远单位被跳到（掉 {on.wet:0.#}），较近的不跳（{on.near:0.#}）；对照没有水洼：跳最近的（掉 {off.near:0.#}），较远的不跳（{off.wet:0.#}）");
        }

        // ── H AI 避险 ────────────────────────────────────────────────────────

        private static void FireHazard(CombatKernel k) => k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 6), 1.5f, 40f, 60f, 0, false, 5);

        /// <summary>危险物都放在 (0, 6) 半径 1.5：守点机器从 (0, 0) 追击 (0, 12) 的敌人。<paramref name="charge"/> = 每传导节拍给 (0, 6) 补电（带电水洼）；<paramref name="possessed"/> = 接入直控、直推向北。</summary>
        private static (double minEdge, int pushes, bool doctrineChain) RunGuard(bool avoid, Action<CombatKernel> hazard = null, bool possessed = false, bool charge = false)
        {
            using CombatKernel k = NewKernel();
            (hazard ?? FireHazard)(k);
            var w = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 2f, Damage = 1f, Cooldown = 1f, Program = -1 };
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 12), -1, 5000f);
            k.SetMachineGuard(me, new double2(0, 0), 20f);
            k.SetHazardAvoid(me, avoid);
            if (possessed)
            {
                k.SetFlag(me, CombatUnitFlags.Possessed, true);
                k.SetDirectClamp(-100, 100);
                k.SetDirectInput(me, new float2(0f, 1f));
            }
            double minEdge = double.MaxValue;
            for (int s = 0; s < 300; s++)
            {
                if (charge && s % 15 == 0)
                {
                    k.InjectPatchEnergy(new double2(0, 6), 0.5f, EmgConst.PortElec, 1f, 0);
                }
                StepN(k, 1);
                k.TryGetUnit(me, out CombatUnitView v);
                minEdge = math.min(minEdge, math.distance(v.Position, new double2(0, 6)) - 1.5);
            }
            return (minEdge, k.PatchStats.AvoidPushes, HasChain(k, EmgChainNode.Doctrine, r => r.Aux == 1 && r.SrcUnit == me));
        }

        private static void CheckAvoid()
        {
            Line("  · H. AI 避险（FGT-EMG-039）：守点教义自选的追击走位绕开明火、因果链记“由教义触发：避开危险物质”；关掉教义直穿；玩家显式移动不改写只警告一次；对照不穿过不警告");
            var on = RunGuard(true);
            var off = RunGuard(false);
            Expect(on.minEdge > 0 && on.pushes > 0 && on.doctrineChain, $"教义开：追击路线离明火边缘最近 {on.minEdge:0.##} 米（没踩进去），排斥 {on.pushes} 步，因果链有“由教义触发：避开危险物质”");
            Expect(off.minEdge < 0 && off.pushes == 0, $"对照（教义关）：直线追击踩进明火（最近 {off.minEdge:0.##} 米），没有排斥");
            Expect(GameText.Get("emg.avoid.chain") == "由教义触发：避开危险物质", "界面文本 emg.avoid.chain = “由教义触发：避开危险物质”");
            // EMG2-BOSS-01 卡片负向：AI 机器按教义避开首领的灌注斑块（酸片），和普通斑块同一条路
            var boss = RunGuard(true, kk => kk.SpawnReservedPatch(EmgMatterConst.Acid, new double2(0, 6), 1.5f, 5f, 60f));
            Expect(boss.minEdge > 0 && boss.pushes > 0, $"教义开：追击路线绕开首领灌注的酸片（离边缘最近 {boss.minEdge:0.##} 米，排斥 {boss.pushes} 步）");
            // EMG2-BOSS-01 卡片负向：防腐涂层是己方机器的腐蚀抗性下限，站进酸片按抗性结算（不是免疫，也不是不变）
            float Corrosion(float resist)
            {
                using CombatKernel k = NewKernel();
                k.SetAcidMarsh(Array.Empty<float4>(), 0f, 1f, resist);
                int u = Unit(k, CombatFaction.Player, new double2(0, 0), -1, 5000f);
                k.SpawnReservedPatch(EmgMatterConst.Acid, new double2(0, 0), 3f, 5f, 10f);
                StepN(k, 120);
                return k.TryGetUnitEnergy(u, out float4 a, out _) ? a.w : 0f;
            }
            float bare = Corrosion(0f), coated = Corrosion(0.25f);
            Expect(bare > 0f && coated > 0f && coated < bare, $"己方机器站进灌注酸片：没涂层腐蚀度 {bare:0.###}，防腐涂层（抗性 0.25）{coated:0.###}");
            foreach (bool cross in new[] { true, false })
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 6), 1.5f, 40f, 60f, 0, false, 5);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), -1, 5000f);
                StepN(k, 1);
                k.DrainGameplay(256, out _);
                k.IssueCommand(me, CombatCommandKind.Move, cross ? new double2(0, 12) : new double2(8, 0), 0, 0.5f, 0f, 0f, false);
                double minEdge = double.MaxValue;
                int warns = 0;
                for (int s = 0; s < 240; s++)
                {
                    StepN(k, 1);
                    CombatEvent[] ev = k.DrainGameplay(256, out int n);
                    for (int e = 0; e < n; e++)
                    {
                        warns += ev[e].Kind == CombatEventKind.HazardPath ? 1 : 0;
                    }
                    k.TryGetUnit(me, out CombatUnitView v);
                    minEdge = math.min(minEdge, math.distance(v.Position, new double2(0, 6)) - 1.5);
                }
                if (cross)
                {
                    Expect(warns == 1 && minEdge < 0 && k.PatchStats.AvoidPushes == 0, $"玩家显式移动穿过明火：警告 {warns} 次（只一次），路线不改写（踩进明火 {minEdge:0.##} 米），没有排斥");
                }
                else
                {
                    Expect(warns == 0, "对照：路线不穿过危险物质不警告");
                }
            }
            // FGT-EMG-039 的其余对照：接入的机器不绕；带电水洼同样绕开；链号为 0 的危险物也写教义来源；迁入斑块按阵营判有害
            var pos = RunGuard(true, possessed: true);
            Expect(pos.minEdge < 0 && pos.pushes == 0, $"接入（直控）的机器：教义开着也不受约束，直推穿过明火（最近 {pos.minEdge:0.##} 米），排斥 {pos.pushes}");
            void Puddle(CombatKernel k) => k.SpawnPatch(EmgMatterConst.Water, new double2(0, 6), 1.5f, 40f, 60f, 0, false, 5);
            var wetOn = RunGuard(true, Puddle, charge: true);
            var wetOff = RunGuard(false, Puddle, charge: true);
            Expect(wetOn.minEdge > 0 && wetOn.pushes > 0 && wetOff.minEdge < 0 && wetOff.pushes == 0,
                $"己方带电水洼（电荷 ≥ 0.2）：教义开绕开（最近 {wetOn.minEdge:0.##} 米、排斥 {wetOn.pushes} 步），教义关踩进去（{wetOff.minEdge:0.##} 米）");
            var noChain = RunGuard(true, k => k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 6), 1.5f, 40f, 60f, 0, false, 0));
            Expect(noChain.pushes > 0 && noChain.doctrineChain, $"没有链号的明火（敌方 / 世界源 / 迁入区域常见）：排斥 {noChain.pushes} 步，因果链照样有“由教义触发：避开危险物质”（为教义动作新开一条链）");
            uint fire = NamedReactionCatalog.BitOf("Fire");
            int legacyMade = 0;
            Action<CombatKernel> Legacy(CombatFaction f) => k =>
            {
                k.SpawnReadingZoneForTests(f, new double2(0, 6), 1.5f, 30f, 10f, fire);
                legacyMade += k.LegacyPatchCount;
            };
            var own = RunGuard(true, Legacy(CombatFaction.Player));
            var foe = RunGuard(true, Legacy(CombatFaction.Hostile));
            Expect(legacyMade == 2 && own.minEdge < 0 && own.pushes == 0 && foe.minEdge > 0 && foe.pushes > 0 && foe.doctrineChain,
                $"迁入斑块（读法驻留区的燃迹，{legacyMade} 块）按区域载荷只伤对面：己方燃迹不绕（最近 {own.minEdge:0.##} 米、排斥 {own.pushes}），敌方燃迹绕开（{foe.minEdge:0.##} 米、排斥 {foe.pushes}）并写教义来源");
            int warnOwn = RunWarn(Legacy(CombatFaction.Player), false, new double2(0, 12));
            int warnFoe = RunWarn(Legacy(CombatFaction.Hostile), false, new double2(0, 12));
            Expect(warnOwn == 0 && warnFoe == 1, $"玩家移动穿过己方燃迹不警告（{warnOwn}），穿过敌方燃迹警告一次（{warnFoe}）");
            int atkFar = RunWarn(FireHazard, true, new double2(0, 12), 2f);
            int atkNear = RunWarn(FireHazard, true, new double2(0, 9), 6f);
            Expect(atkFar == 1 && atkNear == 0,
                $"攻击移动：目标在明火后 12 米、射程 2 → 要穿过明火，警告 {atkFar} 次；目标在 9 米、射程 6 → 走到 3 米就停、不穿过，不警告（{atkNear}）");
        }

        /// <summary>玩家显式下令后 1 秒内收到的“路线穿过危险物质”警告数（移动到 <paramref name="to"/>，或攻击站在 <paramref name="to"/> 的敌人）。</summary>
        private static int RunWarn(Action<CombatKernel> hazard, bool attack, double2 to, float range = 0f)
        {
            using CombatKernel k = NewKernel();
            hazard(k);
            var w = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = math.max(1f, range), Damage = 1f, Cooldown = 1f, Program = -1 };
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 5000f);
            int foe = attack ? Unit(k, CombatFaction.Hostile, to, -1, 5000f) : 0;
            StepN(k, 1);
            k.DrainGameplay(256, out _);
            if (attack)
            {
                k.IssueCommand(me, CombatCommandKind.Attack, to, foe, 0.5f, range, 1f, false);
            }
            else
            {
                k.IssueCommand(me, CombatCommandKind.Move, to, 0, 0.5f, 0f, 0f, false);
            }
            int warns = 0;
            for (int s = 0; s < 60; s++)
            {
                StepN(k, 1);
                CombatEvent[] ev = k.DrainGameplay(256, out int n);
                for (int e = 0; e < n; e++)
                {
                    warns += ev[e].Kind == CombatEventKind.HazardPath ? 1 : 0;
                }
            }
            return warns;
        }

        // ── I evt.react ──────────────────────────────────────────────────────

        private static void CheckReactEvent()
        {
            Line("  · I. evt.react 生产者（FGT-EMG-020）：规则结算入队一条（宿主斑块、Code = 计数槽下标、Value = 累计次数）；对照：没有规则成立不发");
            foreach (bool fire in new[] { true, false })
            {
                using CombatKernel k = NewKernel();
                var instrs = new List<EmgInstr> { new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.React), Role = 2, Ordinal = 0 } };
                var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
                int p = k.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.React }, nodes, instrs);
                Unit(k, CombatFaction.Player, new double2(20, 20), k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 3f, Damage = 1f, Program = p }), 500f);
                if (fire)
                {
                    k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, 0, false, 7);
                }
                k.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, 0, false, 0);
                StepN(k, 240);
                bool got = k.TryGetLastEmgEvent(EmgEventKind.React, out EmgEvent e);
                if (fire)
                {
                    Expect(got && e.EntKind == EmgEntKind.Patch && k.EmgRuleKeyAt(e.Code) == Key("reaction_deflagrate") && Mathf.Approximately(e.Value, 1f),
                        $"爆燃结算后 evt.react：宿主 {e.EntKind}、Code {e.Code} → 稳定键 = reaction_deflagrate、累计 {e.Value}");
                }
                else
                {
                    Expect(!got, "对照：没有明火、没有规则成立，不发 evt.react");
                }
            }
        }

        // ── J 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · J. 存读档：传导途中（油在升温、水洼带电）存档读回续跑与一直跑哈希一致；传导相位按步序号（读档不改相位）；暂停（不步进）不变");
            using CombatKernel a = NewKernel();
            using CombatKernel b = NewKernel();
            a.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, 0, false, 7);
            a.SpawnPatch(EmgMatterConst.Oil, new double2(3, 0), 2f, 5f, 0f, 0, false, 0);
            a.SpawnPatch(EmgMatterConst.Water, new double2(0, 8), 2f, 5f, 0f, 0, false, 3);
            a.SpawnPatch(EmgMatterConst.Water, new double2(3, 8), 2f, 5f, 0f, 0, false, 3);
            Unit(a, CombatFaction.Hostile, new double2(3, 8), -1, 300f);
            StepN(a, 23);
            a.InjectPatchEnergy(new double2(0, 8), 0.5f, EmgConst.PortElec, 1f, 3);
            StepN(a, 7); // 第 30 步：已过一个传导节拍、还在下一个节拍之前
            ulong paused = a.StateHash();
            byte[] snap = a.Serialize();
            ulong paused2 = a.StateHash();
            CombatLoadResult lr = b.Load(snap);
            int ticksA0 = a.PatchStats.ConductTicks;
            StepN(a, 180);
            StepN(b, 180);
            Expect(lr == CombatLoadResult.Ok && paused == paused2 && a.StateHash() == b.StateHash() && a.PatchStats.ConductTicks - ticksA0 == b.PatchStats.ConductTicks,
                $"读档 {lr}；存档不改状态；续跑 180 步后两边哈希一致，传导节拍 {a.PatchStats.ConductTicks - ticksA0} / {b.PatchStats.ConductTicks}（相位按步序号，读档后不错拍）");
        }

        // ── K 传导尖峰 ───────────────────────────────────────────────────────

        private static void CheckSpike()
        {
            Line("  · K. 传导那一步的尖峰耗时（ADR ⑬ 允许 +0.2 ms；只测一次，超线不到 2 倍记警告）");
            using CombatKernel k = NewKernel(512);
            // 满载形态：200 敌人 + 一片 16 × 8 的水 / 油斑块网（128 块，水洼带电、油挨着明火在升温）
            for (int i = 0; i < 200; i++)
            {
                Unit(k, CombatFaction.Hostile, new double2(i % 20 * 2.5, i / 20 * 2.5), -1, 1000f);
            }
            int n = 0;
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    if (k.SpawnPatch(x % 3 == 0 ? EmgMatterConst.Oil : EmgMatterConst.Water, new double2(x * 3.2, y * 3.2), 2f, 5f, 60f, 0, false, 11))
                    {
                        n++;
                    }
                    if (n % 30 == 0)
                    {
                        StepN(k, 1);
                    }
                }
            }
            k.SpawnPatch(EmgMatterConst.Fire, new double2(-2, 0), 2f, 10f, 60f, 0, false, 11);
            StepN(k, 20);
            var sw = new Stopwatch();
            double conductSum = 0, otherSum = 0, conductMax = 0;
            int conductN = 0, otherN = 0;
            for (int s = 0; s < 300; s++)
            {
                if (s % 15 == 0)
                {
                    k.InjectPatchEnergy(new double2(3.2, 3.2), 0.5f, EmgConst.PortElec, 1f, 11);
                }
                int before = k.PatchStats.ConductTicks;
                sw.Restart();
                StepN(k, 1);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (k.PatchStats.ConductTicks != before)
                {
                    conductSum += ms;
                    conductN++;
                    conductMax = Math.Max(conductMax, ms);
                }
                else
                {
                    otherSum += ms;
                    otherN++;
                }
            }
            double spike = conductN > 0 && otherN > 0 ? conductSum / conductN - otherSum / otherN : 0;
            Line($"    传导步 {conductN} 次平均 {conductSum / Math.Max(1, conductN):0.###} ms（最大 {conductMax:0.###}），其余步平均 {otherSum / Math.Max(1, otherN):0.###} ms，尖峰增量 {spike:0.###} ms；最近一拍访问 {k.PatchStats.ConductLastVisits}，顺延 {k.PatchStats.ConductCarried}");
            if (spike > 0.2 && spike <= 0.4)
            {
                Line($"    ⚠ 性能警告：传导尖峰增量 {spike:0.###} ms 超 0.2 ms 但不到 2 倍（Editor batchmode，非真机）");
            }
            Expect(conductN >= 19 && spike <= 0.4, $"传导步尖峰增量 {spike:0.###} ms ≤ 2 × 0.2 ms（失败线）；斑块 {n} 块 + 200 单位");
        }

        // ── K2 访问预算不互相挤占 ────────────────────────────────────────────

        private static void CheckBudgetSplit()
        {
            Line("  · K2. 访问预算不互相挤占（FGR-EMG-091 / ADR ⑬）：非常温斑块多于预算时热交换按节拍轮转、电传导另有自己的预算——电照样传到，Id 最大的明火照样点燃相邻的油（小场景：预算调到 16、30 块冰作热源）");
            using CombatKernel k = NewKernel(visits: 16);
            for (int i = 0; i < 30; i++)
            {
                k.SpawnPatch(EmgMatterConst.Ice, new double2(100 + i % 6 * 2.0, i / 6 * 2.0), 0.5f, 2f, 0f, 0, false, 0); // 远处互不相交的冰：-10 度，不回温，每节拍都是热源
            }
            StepN(k, 1);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(40, 0), 2f, 1f, 60f, 0, false, 7); // Id 排在所有冰之后的热源
            k.SpawnPatch(EmgMatterConst.Oil, new double2(42.34, 0), 2f, 5f, 0f, 0, false, 0);
            for (int i = 0; i < 4; i++)
            {
                k.SpawnPatch(EmgMatterConst.Water, new double2(3 * i, 0), 2f, 5f, 0f, 0, false, 3);
            }
            StepN(k, 1);
            int ice = CountOf(k, EmgMatterConst.Ice);
            k.InjectPatchEnergy(new double2(0, 0), 0.5f, EmgConst.PortElec, 1f, 3);
            int carried0 = k.PatchStats.ConductCarried;
            ToNextConduct(k);
            float c3 = Charge(k, new double2(9, 0));
            bool carried = k.PatchStats.ConductCarried > carried0;
            int lit = -1;
            for (int s = 1; s <= 180 && lit < 0; s++)
            {
                StepN(k, 1);
                lit = CountOf(k, EmgMatterConst.Oil) == 0 ? s : -1;
            }
            Expect(ice >= 30 && carried && c3 > 0f, $"冰 {ice} 块 + 明火 + 油都在升温（热源 > 预算 16，热交换顺延 {k.PatchStats.ConductCarried - carried0} 次）：第 4 块水洼照样带电 {c3:0.###}（电 BFS 不再拿热交换剩下的预算）");
            Expect(lit > 0, $"Id 最大的明火在第 {lit} 步点燃相邻的油（热交换按节拍轮转起点，排在 16 名之后的热源也轮得到）");
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

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
