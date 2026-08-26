using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// organ-gene-rebalance-v3 story-006（Required 4）：EMERGENCE §2 路线 A 矩阵——42 条基因逐条挂
    /// 5 类底盘代表器官（org_emitter/Projectile、org_cilia/Melee、org_enzyme/Field、org_osmotic/Aura、
    /// org_bud/Summon），每对 (gene, organ) 走 <see cref="CarrierCompiler.Compile"/> 真实编译链，断言：
    /// ① 产出恰 1 条 HitEvent；② evt.AttackPattern 与该底盘基线一致（基因禁止改写 Pattern，DESIGN §3/§4，
    /// 只读字段+Pattern，不按 organId 分支）——这两条对全部 210 组合逐一硬断言，不抽样；
    /// ③ <see cref="MetabolicSliceBridge.ApplyEvent"/> 处理该事件不抛异常，同样逐一硬断言；
    /// ④ 相对同底盘空载基线有可观察字段差异（复刻 <see cref="GeneKeepTuneSmokeReport"/> 的反射 Differs 手法）
    /// ——按 Required 4 明文允许的抽样口径，逐基因要求 5 底盘里至少 3 个有差异（个别组合天然无差异是合法的，
    /// 例如 gene_arc 的 BallisticsModule 字段被 org_emitter 自身同类型攻击模块的链尾后写覆盖，属
    /// ComposeEngine 装配顺序既有性质，非本 story 缺陷）。42×5=210 组合覆盖 Required 4"每基因至少 3
    /// 底盘，共 126 最小"的全量版本。另附 3 组前后对照，验证本 story 的核心修复——Homing/Pierce/Bounce/Return/Trail/
    /// SplitOnHit/Linger 这组"延迟落点"字段此前只有 Shape=="Bolt" 能进 <see cref="MetabolicSliceBridge"/>
    /// 的落点结算，Melee/Field/Aura 底盘上全是死字段（org_enzyme 自带的 Linger 从未真正落地成坑）。
    /// 纯 C# 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。
    /// </summary>
    public static class ChassisFallbackSmokeReport
    {
        private static readonly (string OrganId, AttackPattern Expected)[] Chassis =
        {
            ("org_emitter", AttackPattern.Projectile),
            ("org_cilia", AttackPattern.Melee),
            ("org_enzyme", AttackPattern.Pool),
            ("org_osmotic", AttackPattern.Aura),
            ("org_bud", AttackPattern.SummonFollow),
        };

        /// <summary>gene_heatshock 只在 Packet.Heat 已过阈值时才分岔，CarrierCompiler 槽位机制没有
        /// "前置非基因模块"的位置——复刻 <see cref="GeneKeepTuneSmokeReport"/> 同款绕过手法。</summary>
        private static readonly HashSet<string> NeedsHeatPrimer = new HashSet<string> { "gene_heatshock" };

        private static readonly PropertyInfo[] HitEventScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>) && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        public static (bool Pass, string Reason) Run()
        {
            string[] geneIds = GeneCatalog.AllModuleIds.ToArray();
            if (geneIds.Length < 42)
            {
                return (false, $"GeneCatalog.AllModuleIds 应至少 42 条，实际 {geneIds.Length}");
            }

            var engine = new Engine();
            var world = new WorldState();
            int combosPassed = 0;
            int combosTotal = 0;
            // Required 4 明文允许抽样："每基因至少 3 底盘"——个别 (gene, organ) 组合天然不产生可观察
            // 差异是合法的，例如 gene_arc（BallisticsModule(gravity) 落 Speed/Lifetime/Gravity）挂在
            // org_emitter 上：装配链固定 EnergyCore→槽序基因→器官攻击模块（R1～R6 锁定顺序，禁止改），
            // 而 org_emitter 自己的攻击模块同样是 BallisticsModule，链尾后写覆盖槽内基因的同名字段，
            // 与本 story 的底盘 Fallback 无关，是 ComposeEngine 装配顺序的既有性质。Pattern 不变 +
            // ApplyEvent 不抛异常这两条硬约束仍对全部 210 组合逐一断言，不参与抽样豁免。
            var diffCountByGene = new Dictionary<string, int>();
            foreach (string geneId in geneIds)
            {
                diffCountByGene[geneId] = 0;
            }

            foreach ((string organId, AttackPattern expected) in Chassis)
            {
                var reserve = new GeneReserve();
                var baseline = new CarrierInstance("carrier_base_" + organId, organId);
                List<HitEvent> baseEvents = CarrierCompiler.Compile(engine, baseline, reserve, world, seed: 1);
                if (baseEvents.Count != 1)
                {
                    return (false, $"{organId} 空载基线应产出 1 条 HitEvent，实际 {baseEvents.Count}");
                }
                if (baseEvents[0].AttackPattern != expected)
                {
                    return (false, $"{organId} 空载基线 AttackPattern 应为 {expected}，实际 {baseEvents[0].AttackPattern}");
                }

                foreach (string geneId in geneIds)
                {
                    combosTotal++;
                    var moduleFactory = GeneCatalog.GetModule(geneId);
                    if (moduleFactory == null)
                    {
                        return (false, $"{geneId} GetModule 返回 null");
                    }

                    List<HitEvent> geneEvents;
                    List<HitEvent> baseForCompare;
                    if (NeedsHeatPrimer.Contains(geneId))
                    {
                        var primer = new FocusLens(focusMult: 1f, heatPerFocus: 10f);
                        geneEvents = CompileDirect(engine, organId, moduleFactory(), primer, world, seed: 1);
                        baseForCompare = CompileDirect(engine, organId, null, primer, world, seed: 1);
                    }
                    else
                    {
                        var carrier = new CarrierInstance("carrier_" + organId + "_" + geneId, organId);
                        var geneInstance = new GeneInstance("inst_" + organId + "_" + geneId, geneId, GeneLocation.Reserve());
                        reserve.TryAdd(geneInstance);
                        carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                        geneEvents = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                        baseForCompare = baseEvents;
                    }

                    if (geneEvents.Count != 1 || baseForCompare.Count != 1)
                    {
                        return (false, $"{organId}+{geneId} 应产出 1 条 HitEvent，实际 gene={geneEvents.Count} base={baseForCompare.Count}");
                    }

                    HitEvent evt = geneEvents[0];
                    if (evt.AttackPattern != expected)
                    {
                        return (false, $"{organId}+{geneId} 基因不应改写 AttackPattern，期望 {expected} 实际 {evt.AttackPattern}");
                    }
                    if (Differs(evt, baseForCompare[0]))
                    {
                        diffCountByGene[geneId]++;
                    }

                    var sim = new SimBridge();
                    var stats = new StatSheet();
                    var abilities = new AbilitySystem();
                    var bridge = new MetabolicSliceBridge();
                    bridge.Bind(sim, stats, abilities);
                    try
                    {
                        bridge.ApplyEvent(evt);
                    }
                    catch (System.Exception ex)
                    {
                        return (false, $"{organId}+{geneId} ApplyEvent 抛异常：{ex.Message}");
                    }
                    combosPassed++;
                }
            }

            // Required 4："每基因至少 3 底盘"有可观察差异——逐基因核对抽样下限。
            var underSampled = diffCountByGene.Where(kv => kv.Value < 3).Select(kv => $"{kv.Key}={kv.Value}").ToList();
            if (underSampled.Count > 0)
            {
                return (false, $"以下基因未达「至少 3 底盘有可观察差异」下限：{string.Join(", ", underSampled)}");
            }

            // ── 前后对照：证明本 story 修复的核心 bug——Linger 这类"延迟落点"字段此前只有
            // Shape=="Bolt" 能触发落点结算，Melee/Field/Aura 底盘全部死字段 ──

            // ① org_enzyme（Field）自带 LingerModule(4f)，空载基线本身就该落地成坑（PendingImpactCount>0），
            // 不需要额外挂基因——此前恒为 0，是本 story 修的根因 bug。
            {
                var reserve = new GeneReserve();
                var carrier = new CarrierInstance("carrier_enzyme_probe", "org_enzyme");
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 2);
                var sim = new SimBridge();
                var bridge = new MetabolicSliceBridge();
                bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                bridge.ApplyEvent(events[0]);
                if (bridge.PendingImpactCount <= 0)
                {
                    return (false, "①org_enzyme 空载基线自带 Linger，应触发落点结算（PendingImpactCount>0），实际 0——Field 底盘 Fallback 未生效");
                }
            }

            // ② org_cilia（Melee）+ gene_tide（Linger）此前恒不落点，本 story 起应触发。
            {
                var reserve = new GeneReserve();
                var carrier = new CarrierInstance("carrier_cilia_tide_probe", "org_cilia");
                var geneInstance = new GeneInstance("inst_cilia_tide_probe", "gene_tide", GeneLocation.Reserve());
                reserve.TryAdd(geneInstance);
                carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 3);
                var sim = new SimBridge();
                var bridge = new MetabolicSliceBridge();
                bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                bridge.ApplyEvent(events[0]);
                if (bridge.PendingImpactCount <= 0)
                {
                    return (false, "②org_cilia+gene_tide 应触发落点结算（PendingImpactCount>0），实际 0——Melee 底盘 Fallback 未生效");
                }
            }

            // ③ org_osmotic（Aura）+ gene_tide（Linger）同理。
            {
                var reserve = new GeneReserve();
                var carrier = new CarrierInstance("carrier_osmotic_tide_probe", "org_osmotic");
                var geneInstance = new GeneInstance("inst_osmotic_tide_probe", "gene_tide", GeneLocation.Reserve());
                reserve.TryAdd(geneInstance);
                carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 4);
                var sim = new SimBridge();
                var bridge = new MetabolicSliceBridge();
                bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                bridge.ApplyEvent(events[0]);
                if (bridge.PendingImpactCount <= 0)
                {
                    return (false, "③org_osmotic+gene_tide 应触发落点结算（PendingImpactCount>0），实际 0——Aura 底盘 Fallback 未生效");
                }
            }

            // ④ Required 5：org_bud（Summon）+ gene_swarm（InheritPattern）必须结构性满足触发条件
            // （SummonId>0 且 Tags 含 InheritPattern），MetabolicSliceBridge.ApplySwarmInherit 据此让存活
            // 召唤物补一次同结算——该机制本身在更早的 story 已落地，本 story 只需保证底盘链路正确接线，
            // 不重复起专属系统。ApplySummon 里新增的"出生点偏向最近敌人"Homing 逻辑与 ApplySwarmInherit
            // 都依赖 _sim.Running/存活单位快照，遵循仓库既有约定（见其余 SmokeReport）不在纯 C# 直调里
            // 起真实 SimWorld，故只做结构断言，不在此处重复验证需要真跑局的召唤物位置细节。
            {
                var reserve = new GeneReserve();
                var carrier = new CarrierInstance("carrier_bud_swarm_probe", "org_bud");
                var geneInstance = new GeneInstance("inst_bud_swarm_probe", "gene_swarm", GeneLocation.Reserve());
                reserve.TryAdd(geneInstance);
                carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 5);
                HitEvent evt = events[0];
                if (evt.SummonId <= 0 || !evt.Tags.Contains("InheritPattern"))
                {
                    return (false, $"④org_bud+gene_swarm 应同时满足 SummonId>0（实际 {evt.SummonId}）且 Tags 含 InheritPattern（实际 [{string.Join(",", evt.Tags)}]）");
                }
                var sim = new SimBridge();
                var bridge = new MetabolicSliceBridge();
                bridge.Bind(sim, new StatSheet(), new AbilitySystem());
                try
                {
                    bridge.ApplyEvent(evt);
                }
                catch (System.Exception ex)
                {
                    return (false, $"④org_bud+gene_swarm ApplyEvent 抛异常：{ex.Message}");
                }
            }

            int totalDiffs = diffCountByGene.Values.Sum();
            return (true,
                $"{combosPassed}/{combosTotal} (gene,organ) 组合 Pattern 保持不变 + ApplyEvent 不抛异常；" +
                $"{totalDiffs}/{combosTotal} 组合有可观察字段差异，全部 {diffCountByGene.Count} 条基因均 ≥3 底盘达标；" +
                "①org_enzyme 基线/②org_cilia+gene_tide/③org_osmotic+gene_tide 均已触发落点结算（PendingImpactCount>0）；" +
                "④org_bud+gene_swarm 结构性满足 InheritPattern 触发条件。");
        }

        /// <summary>复刻 <see cref="CarrierCompiler.Compile"/> 的链拼装，用于需要在基因前插入非基因前置
        /// 模块（当前仅 gene_heatshock 产热）的场景。空 contracts 与 CarrierCompiler 内部行为一致。</summary>
        private static List<HitEvent> CompileDirect(Engine engine, string organelleId, IModule geneModule, IModule primer, WorldState world, int seed)
        {
            var chain = new List<IModule> { new EnergyCore(10f) };
            if (primer != null) chain.Add(primer);
            if (geneModule != null) chain.Add(geneModule);
            chain.Add(OrganelleCatalog.Get(organelleId).CreateModule());

            var raw = engine.RunAssembly(chain, ticks: 1, seed: seed);
            var rules = engine.NormalizeContracts(new List<IContract>());
            var events = new List<HitEvent>();
            foreach (var evt in raw)
            {
                events.Add(engine.ApplyPipeline(evt, rules, world));
            }
            return events;
        }

        private static bool Differs(HitEvent a, HitEvent b)
        {
            foreach (var prop in HitEventScalarProps)
            {
                var av = prop.GetValue(a);
                var bv = prop.GetValue(b);
                if (!Equals(av, bv)) return true;
            }
            var tagDiff = new HashSet<string>(a.Tags);
            tagDiff.SymmetricExceptWith(b.Tags);
            return tagDiff.Count > 0;
        }
    }
}
