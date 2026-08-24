using System.Collections.Generic;
using System.Linq;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>combat-identity-rework story-003：CATALOG §A1 第一波 12 个攻击方式的独立开火测试
    /// （R1 判据：只装这一件、基因槽全空）。execute_code 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。</summary>
    public static class AttackMethodWave1SmokeReport
    {
        /// <summary>CATALOG §A1 顺序，含刺突（反伤特例，仍走同一 Solo Fire 断言：均产出 HitEvent）。</summary>
        private static readonly string[] Wave1Ids =
        {
            "org_emitter", "org_cilia", "org_spine", "org_lensbeam", "org_enzyme", "org_osmotic",
            "org_orbitcilia", "org_bud", "org_mycelium", "org_taxis", "org_boomer", "org_calcium",
        };

        /// <summary>Required 4：003 之前就已存在于 OrganelleCatalog 的旧修饰器官，本波必须 AttackMethod=false。</summary>
        private static readonly string[] LegacyModifierIds =
        {
            "org_vacuole", "org_golgi", "org_merge", "org_lens", "org_scatter", "org_swell",
            "org_flagella", "org_lyso", "org_perox", "org_aqua", "org_ion", "org_radiator",
            "org_breaker", "org_synapse", "org_slime", "org_receptor", "org_valve", "org_filter",
        };

        public static (bool Pass, string Reason) Run()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var reserve = new GeneReserve();
            var world = new WorldState();

            var signatures = new Dictionary<string, string>();

            foreach (var id in Wave1Ids)
            {
                var def = OrganelleCatalog.Get(id);
                if (def == null)
                {
                    return (false, $"{id} 未在 OrganelleCatalog 注册");
                }
                if (!def.AttackMethod)
                {
                    return (false, $"{id} AttackMethod 应为 true，实际 false");
                }

                var carrier = new CarrierInstance("carrier_" + id, id);
                List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                if (events.Count == 0)
                {
                    return (false, $"{id} Solo Fire 未产出 HitEvent（空基因、只装该器官）");
                }

                HitEvent evt = events[0];
                if (evt.Damage <= 0f)
                {
                    return (false, $"{id} evt.Damage 应 >0（EnergyCore 未生效或链路断点），实际 {evt.Damage}");
                }

                signatures[id] = Signature(evt);
            }

            var distinct = signatures.Values.Distinct().ToList();
            if (distinct.Count != Wave1Ids.Length)
            {
                var dupGroups = signatures.GroupBy(kv => kv.Value).Where(g => g.Count() > 1)
                    .Select(g => string.Join("=", g.Select(kv => kv.Key)) + " -> " + g.Key);
                return (false, $"Pattern 不可两两区分，重复签名: {string.Join(" | ", dupGroups)}");
            }

            foreach (var id in LegacyModifierIds)
            {
                var def = OrganelleCatalog.Get(id);
                if (def == null)
                {
                    return (false, $"{id} 未在 OrganelleCatalog 注册（旧修饰不应被删除，只应 AttackMethod=false）");
                }
                if (def.AttackMethod)
                {
                    return (false, $"{id} 是旧修饰，AttackMethod 应为 false，实际 true");
                }
            }

            return (true, $"12/12 Solo Fire 通过，Pattern 全部两两可区分：" +
                string.Join("; ", Wave1Ids.Select(id => $"{id}={signatures[id]}")) +
                $"；{LegacyModifierIds.Length} 个旧修饰 AttackMethod 均为 false");
        }

        /// <summary>把决定"这是哪种打法"的字段拼成签名串，用于两两去重比较。</summary>
        private static string Signature(HitEvent evt) =>
            $"{evt.AttackPattern}|{evt.Shape}|H={evt.Homing:0.##}|Or={evt.Orbit:0.##}|Ct={evt.Count:0.##}" +
            $"|Au={evt.AuraRadius:0.##}|Su={evt.SummonId}|Re={evt.Return}|Sc={evt.Scale:0.##}" +
            $"|Li={evt.Linger:0.##}|Tk={evt.TickRate:0.##}|Sp={evt.SpreadAngle:0.##}" +
            $"|Tags=[{string.Join(",", evt.Tags.OrderBy(t => t))}]";
    }
}
