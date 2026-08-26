using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>organ-gene-rebalance-v3 story-002：CATALOG-v3 §A 13 器官 Solo Fire 断言（只装这一件、
    /// 基因槽全空，均产出非空 HitEvent）+ §C 12 退役器官作链尾恒 0 HitEvent（CarrierCompiler 遇
    /// AttackMethod=false 链尾直接短路，见其源码注释）。execute_code 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。</summary>
    public static class OrganSlimSmokeReport
    {
        /// <summary>CATALOG-v3 §A 顺序，13 条。</summary>
        private static readonly string[] KeptIds =
        {
            "org_emitter", "org_cilia", "org_spine", "org_phago", "org_lensbeam", "org_enzyme",
            "org_osmotic", "org_orbitcilia", "org_wave", "org_pseudopod", "org_drill", "org_bud",
            "org_mycelium",
        };

        /// <summary>CATALOG-v3 §C 顺序，12 条（旧 org_wave/胞质浪几何身份并入新 org_wave 同 id，不单列）。</summary>
        private static readonly string[] RetiredIds =
        {
            "org_taxis", "org_boomer", "org_needle", "org_shotgun", "org_hook", "org_synapsearc",
            "org_trail", "org_calcium", "org_pulse", "org_acidgland", "org_spore", "org_phage",
        };

        public static (bool Pass, string Reason) Run()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var reserve = new GeneReserve();
            var world = new WorldState();

            foreach (var id in KeptIds)
            {
                var def = OrganelleCatalog.Get(id);
                if (def == null)
                {
                    return (false, $"{id} 未在 OrganelleCatalog 注册");
                }
                if (!def.AttackMethod || def.IsRetired)
                {
                    return (false, $"{id} 应为 AttackMethod=true/IsRetired=false，实际 AttackMethod={def.AttackMethod} IsRetired={def.IsRetired}");
                }

                var carrier = new CarrierInstance("carrier_" + id, id);
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                if (events.Count == 0)
                {
                    return (false, $"{id} Solo Fire 未产出 HitEvent（空基因、只装该器官）");
                }
            }

            foreach (var id in RetiredIds)
            {
                var def = OrganelleCatalog.Get(id);
                if (def == null)
                {
                    return (false, $"{id} 未在 OrganelleCatalog 注册（退役不应删条目）");
                }
                if (def.AttackMethod || !def.IsRetired)
                {
                    return (false, $"{id} 应为 AttackMethod=false/IsRetired=true，实际 AttackMethod={def.AttackMethod} IsRetired={def.IsRetired}");
                }

                var carrier = new CarrierInstance("carrier_" + id, id);
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                if (events.Count != 0)
                {
                    return (false, $"{id} 退役后作链尾应 0 HitEvent，实际 {events.Count}");
                }
            }

            return (true, $"{KeptIds.Length}/{KeptIds.Length} 器官 Solo Fire 非空 HitEvent 通过；" +
                $"{RetiredIds.Length}/{RetiredIds.Length} 退役器官作链尾均 0 HitEvent");
        }
    }
}
