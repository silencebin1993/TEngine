using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>organ-gene-rebalance-v3 story-003：CATALOG-v3 §B1 24 条保留基因逐条挂 org_emitter
    /// （Required 4 指定宿主），断言 HitEvent 相对同条件空载基线有可观察字段差异。
    /// 与 002 的 OrganSlimSmokeReport 同款 CarrierCompiler.Compile 直调路径，不进 Play
    /// （验收优先代码断言，见根 CLAUDE.md）。</summary>
    public static class GeneKeepTuneSmokeReport
    {
        /// <summary>CATALOG-v3 §B1 顺序，24 条。</summary>
        private static readonly string[] KeptGeneIds =
        {
            "gene_taxis", "gene_spindle", "gene_elastic", "gene_tubule", "gene_return", "gene_swell",
            "gene_lyso", "gene_flagella", "gene_echo", "gene_pyro", "gene_tide", "gene_volt",
            "gene_vacuole", "gene_golgi", "gene_heatshock", "gene_synapse", "gene_blood", "gene_swarm",
            "gene_slime", "gene_pull", "gene_split", "gene_mirror", "gene_receptor", "gene_apoptosis",
        };

        /// <summary>gene_heatshock 只在 Packet.Heat 已过阈值（默认 8f）时才分岔（见 HeatShockModule），
        /// CarrierCompiler 槽位机制没有"前置非基因模块"的位置，复用 Gene04SmokeReport 同款 FocusLens
        /// 前置产热、绕开 CarrierInstance 槽位直接拼链隔离该基因自身效果。</summary>
        private static readonly HashSet<string> NeedsHeatPrimer = new HashSet<string> { "gene_heatshock" };

        private static readonly PropertyInfo[] HitEventScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>) && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        public static (bool Pass, string Reason) Run()
        {
            if (GeneCatalog.AllModuleIds.Count() != 24)
            {
                return (false, $"GeneCatalog 应为 24 条 Module 基因，实际 {GeneCatalog.AllModuleIds.Count()}");
            }

            var engine = new Engine();
            var reserve = new GeneReserve();
            var world = new WorldState();

            var baseline = new CarrierInstance("carrier_base", "org_emitter");
            var baseEvents = CarrierCompiler.Compile(engine, baseline, reserve, world, seed: 1);
            if (baseEvents.Count != 1)
            {
                return (false, $"org_emitter 空载基线应产出 1 条 HitEvent，实际 {baseEvents.Count}");
            }

            int passCount = 0;
            foreach (var id in KeptGeneIds)
            {
                var moduleFactory = GeneCatalog.GetModule(id);
                if (moduleFactory == null)
                {
                    return (false, $"{id} GetModule 返回 null");
                }

                List<HitEvent> geneEvents;
                List<HitEvent> baseForCompare;

                if (NeedsHeatPrimer.Contains(id))
                {
                    var primer = new FocusLens(focusMult: 1f, heatPerFocus: 10f);
                    geneEvents = CompileDirect(engine, "org_emitter", moduleFactory(), primer, world, seed: 1);
                    baseForCompare = CompileDirect(engine, "org_emitter", null, primer, world, seed: 1);
                }
                else
                {
                    var carrier = new CarrierInstance("carrier_" + id, "org_emitter");
                    var geneInstance = new GeneInstance("inst_" + id, id, GeneLocation.Reserve());
                    reserve.TryAdd(geneInstance);
                    carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                    geneEvents = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                    baseForCompare = baseEvents;
                }

                if (geneEvents.Count != 1 || baseForCompare.Count != 1)
                {
                    return (false, $"{id} 挂 org_emitter 应产出 1 条 HitEvent，实际 gene={geneEvents.Count} base={baseForCompare.Count}");
                }

                if (!Differs(geneEvents[0], baseForCompare[0]))
                {
                    return (false, $"{id} 挂 org_emitter 时 HitEvent 相对空载基线无可观察差异");
                }
                passCount++;
            }

            return (true, $"{passCount}/{KeptGeneIds.Length} gene_* 挂 org_emitter 时字段均有可观察变化（含 gene_taxis Homing=0.85f 提值）");
        }

        /// <summary>复刻 CarrierCompiler.Compile 的链拼装（EnergyCore 头 + 可选 primer + 可选基因 + 器官链尾 +
        /// ApplyPipeline），用于需要在基因前插入非基因前置模块（当前仅 gene_heatshock 产热）的场景。
        /// 空 contracts（Module 基因不产生 IContract）与 CarrierCompiler 内部行为一致。</summary>
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
