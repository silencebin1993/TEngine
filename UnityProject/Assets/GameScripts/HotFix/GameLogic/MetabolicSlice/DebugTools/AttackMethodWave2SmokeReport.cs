using System.Collections.Generic;
using System.Linq;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>combat-identity-rework story-006：CATALOG §A2 第二波 12 个攻击方式的独立开火测试
    /// （R1 判据：只装这一件、基因槽全空）。execute_code 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。
    /// 结构照抄 <see cref="AttackMethodWave1SmokeReport"/>，不另起一套判据。</summary>
    public static class AttackMethodWave2SmokeReport
    {
        /// <summary>CATALOG §A2 顺序。</summary>
        private static readonly string[] Wave2Ids =
        {
            "org_needle", "org_acidgland", "org_shotgun", "org_pseudopod", "org_hook", "org_synapsearc",
            "org_spore", "org_phage", "org_drill", "org_wave", "org_trail", "org_pulse",
        };

        public static (bool Pass, string Reason) Run()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            var reserve = new GeneReserve();
            var world = new WorldState();

            var signatures = new Dictionary<string, string>();

            foreach (var id in Wave2Ids)
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
            if (distinct.Count != Wave2Ids.Length)
            {
                var dupGroups = signatures.GroupBy(kv => kv.Value).Where(g => g.Count() > 1)
                    .Select(g => string.Join("=", g.Select(kv => kv.Key)) + " -> " + g.Key);
                return (false, $"Pattern 不可两两区分，重复签名: {string.Join(" | ", dupGroups)}");
            }

            return (true, $"12/12 Solo Fire 通过，Pattern 全部两两可区分：" +
                string.Join("; ", Wave2Ids.Select(id => $"{id}={signatures[id]}")));
        }

        /// <summary>把决定"这是哪种打法"的字段拼成签名串，用于两两去重比较（含 003 之后新增的
        /// Pierce/Chain/Knockback/Trail/Gravity/Speed/Lifetime，比 Wave1 版多几个字段防漏判）。</summary>
        private static string Signature(HitEvent evt) =>
            $"{evt.AttackPattern}|{evt.Shape}|H={evt.Homing:0.##}|Or={evt.Orbit:0.##}|Ct={evt.Count:0.##}" +
            $"|Au={evt.AuraRadius:0.##}|Su={evt.SummonId}|Re={evt.Return}|Sc={evt.Scale:0.##}" +
            $"|Li={evt.Linger:0.##}|Tk={evt.TickRate:0.##}|Sp={evt.SpreadAngle:0.##}" +
            $"|Pi={evt.Pierce:0.##}|Ch={evt.Chain:0.##}|Kb={evt.Knockback:0.##}|Tr={evt.Trail:0.##}" +
            $"|Tags=[{string.Join(",", evt.Tags.OrderBy(t => t))}]";
    }
}
