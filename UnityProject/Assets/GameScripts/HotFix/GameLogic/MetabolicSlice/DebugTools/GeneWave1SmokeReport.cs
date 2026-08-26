using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>organ-gene-rebalance-v3 story-004：CATALOG-v3 §B2 新增 18 条中本 story 落地的 12 条
    /// （仅 Composite 现有 Module，其余 6 条待 005+ 新 Module 落地后接线）逐条挂 org_emitter，断言 HitEvent
    /// 相对同条件空载基线有可观察字段差异。与 003 的 GeneKeepTuneSmokeReport 同款 CarrierCompiler.Compile
    /// 直调路径，不进 Play（验收优先代码断言，见根 CLAUDE.md）。</summary>
    public static class GeneWave1SmokeReport
    {
        /// <summary>story-004 Required 顺序，12 条。</summary>
        private static readonly string[] NewGeneIds =
        {
            "gene_fan", "gene_vortex", "gene_oilfilm", "gene_sugarfilm", "gene_acidfilm", "gene_frostfilm",
            "gene_harmonic", "gene_membrane", "gene_bloomlate", "gene_arc", "gene_magnet", "gene_scatterseed",
        };

        /// <summary>默认挂 org_emitter（同 003 GeneKeepTuneSmokeReport）。org_emitter 自身攻击链是
        /// CompositeModule(BallisticsModule(speed:1.3f), Actuator)，链尾在基因模块之后执行，会无条件
        /// 把 Speed/Lifetime/Gravity 覆盖回 1.3/0/0——gene_arc 正好改这三个字段，挂 org_emitter 测不出
        /// 差异（不是 bug，是 R1 编译链顺序"槽序基因 Module 链 → 激活器官攻击模块"的必然结果）。
        /// 换挂 org_cilia（SpreadModule+Melee Actuator，不碰 Speed/Lifetime/Gravity）隔离 gene_arc 自身效果。</summary>
        private static readonly Dictionary<string, string> HostOverride = new Dictionary<string, string>
        {
            ["gene_arc"] = "org_cilia",
        };

        private static readonly PropertyInfo[] HitEventScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>) && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        public static (bool Pass, string Reason) Run()
        {
            if (GeneCatalog.AllModuleIds.Count() != 36)
            {
                return (false, $"GeneCatalog 应为 36 条 Module 基因（24 保留 + 12 新增），实际 {GeneCatalog.AllModuleIds.Count()}");
            }

            var engine = new Engine();
            var reserve = new GeneReserve();
            var world = new WorldState();
            var baselineCache = new Dictionary<string, HitEvent>();

            int passCount = 0;
            foreach (var id in NewGeneIds)
            {
                var moduleFactory = GeneCatalog.GetModule(id);
                if (moduleFactory == null)
                {
                    return (false, $"{id} GetModule 返回 null");
                }

                string hostId = HostOverride.TryGetValue(id, out var host) ? host : "org_emitter";
                if (!baselineCache.TryGetValue(hostId, out var baseEvent))
                {
                    var baseline = new CarrierInstance("carrier_base_" + hostId, hostId);
                    var baseEvents = CarrierCompiler.Compile(engine, baseline, reserve, world, seed: 1);
                    if (baseEvents.Count != 1)
                    {
                        return (false, $"{hostId} 空载基线应产出 1 条 HitEvent，实际 {baseEvents.Count}");
                    }
                    baseEvent = baseEvents[0];
                    baselineCache[hostId] = baseEvent;
                }

                var carrier = new CarrierInstance("carrier_" + id, hostId);
                var geneInstance = new GeneInstance("inst_" + id, id, GeneLocation.Reserve());
                reserve.TryAdd(geneInstance);
                carrier.Slots[0].GeneInstanceId = geneInstance.GeneInstanceId;
                var geneEvents = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);

                if (geneEvents.Count != 1)
                {
                    return (false, $"{id} 挂 {hostId} 应产出 1 条 HitEvent，实际 {geneEvents.Count}");
                }

                if (!Differs(geneEvents[0], baseEvent))
                {
                    return (false, $"{id} 挂 {hostId} 时 HitEvent 相对空载基线无可观察差异");
                }
                passCount++;
            }

            return (true, $"{passCount}/{NewGeneIds.Length} 新增 gene_* 挂器官时字段均有可观察变化（gene_arc 换挂 org_cilia，理由见 HostOverride 注释）");
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
