using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-003：Catalog 逐 id 最小链冒烟（接线证明，非组合穷尽）。
    /// 覆盖当前 CreateModule!=null 的器官 + 全部基因；7 个 CreateModule=null 的 stub 器官留给 004 接线后再测。
    /// 再跑：execute_code 里调 GameLogic.MetabolicSlice.DebugTools.IdSmokeReport.Run()。
    /// </summary>
    public static class IdSmokeReport
    {
        /// <summary>HitEvent 的一等标量字段（排除集合类）。<see cref="DiffEvents"/> 用它做逐字段比对。</summary>
        private static readonly PropertyInfo[] HitEventScalarProps = typeof(HitEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(Dictionary<string, object>)
                     && p.PropertyType != typeof(HashSet<string>))
            .ToArray();

        private static readonly PropertyInfo[] RuleVectorScalarProps = typeof(RuleVector)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(RuleVector.StackCount) && p.PropertyType != typeof(Dictionary<string, object>))
            .ToArray();

        public static List<(string Id, bool Pass, string Reason)> Run()
        {
            var results = new List<(string, bool, string)>();

            foreach (var def in OrganelleCatalog.All.Values.Where(d => d.CreateModule != null).OrderBy(d => d.Id))
                results.Add(RunOrganelle(def));

            foreach (var id in GeneCatalog.AllIds.OrderBy(x => x))
                results.Add(RunGene(id));

            return results;
        }

        private static (string, bool, string) RunOrganelle(OrganelleDef def)
        {
            if (def.Id == "org_radiator") return RunRadiator();
            if (def.Id == "org_breaker") return RunBreaker();

            var engine = new Engine();
            IModule[] testChain;
            IModule[] baseChain;

            switch (def.Role)
            {
                case OrganelleRole.Source:
                    testChain = new[] { def.CreateModule(), new Actuator() };
                    baseChain = new IModule[] { new Actuator() };
                    break;
                case OrganelleRole.Sink:
                    testChain = new[] { new EnergyCore(), def.CreateModule() };
                    baseChain = new IModule[] { new EnergyCore() };
                    break;
                default:
                    testChain = new[] { new EnergyCore(), def.CreateModule(), new Actuator() };
                    baseChain = new IModule[] { new EnergyCore(), new Actuator() };
                    break;
            }

            var testEvents = engine.RunAssembly(testChain);
            var baseEvents = engine.RunAssembly(baseChain);
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return (def.Id, differs, reason);
        }

        /// <summary>org_radiator（HeatSink）：Actuator 不读 packet.Heat，HitEvent 层面测不出差异，改读 FinalPacket.Heat；
        /// 借 FocusLens 的加热副作用把 Heat 垫高到非 0，否则"降到 0"和"本来是 0"分不出。</summary>
        private static (string, bool, string) RunRadiator()
        {
            var engine = new Engine();
            var testHeat = engine.NormalizeAssembly(new IModule[] { new EnergyCore(), new FocusLens(), new HeatSink() }).FinalPacket.Heat;
            var baseHeat = engine.NormalizeAssembly(new IModule[] { new EnergyCore(), new FocusLens() }).FinalPacket.Heat;
            bool differs = Math.Abs(testHeat - baseHeat) > 1e-4f;
            string reason = differs ? $"Heat {baseHeat:0.##}->{testHeat:0.##}" : $"Heat 未变化（{testHeat:0.##}），HeatSink 未生效";
            return ("org_radiator", differs, reason);
        }

        /// <summary>org_breaker（Fuse）：默认 EnergyCore()=10 顶不到 AmpCap(8)*50=400 闸值，是合法空转不是失败；
        /// 本条冒烟改用 EnergyCore(500f) 顶过闸值，只用于本条断言，不代表器官正式数值。</summary>
        private static (string, bool, string) RunBreaker()
        {
            var engine = new Engine();
            var testEvents = engine.RunAssembly(new IModule[] { new EnergyCore(500f), new Fuse(), new Actuator() });
            var baseEvents = engine.RunAssembly(new IModule[] { new EnergyCore(500f), new Actuator() });
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return ("org_breaker", differs, reason);
        }

        private static (bool Differs, string Reason) DiffEvents(IReadOnlyList<HitEvent> testEvents, IReadOnlyList<HitEvent> baseEvents)
        {
            if (testEvents.Count != baseEvents.Count)
                return (true, $"事件数 {baseEvents.Count}->{testEvents.Count}");
            if (testEvents.Count == 0)
                return (false, "两条链均无事件（缺执行器），无法判定");

            var t = testEvents[0];
            var b = baseEvents[0];

            if (Math.Abs(t.Damage - b.Damage) > 1e-4f) return (true, $"Damage {b.Damage:0.##}->{t.Damage:0.##}");
            if (Math.Abs(t.Heal - b.Heal) > 1e-4f) return (true, $"Heal {b.Heal:0.##}->{t.Heal:0.##}");
            if (t.Shape != b.Shape) return (true, $"Shape {b.Shape}->{t.Shape}");

            var tagDiff = new HashSet<string>(t.Tags);
            tagDiff.SymmetricExceptWith(b.Tags);
            if (tagDiff.Count > 0) return (true, $"Tags 差异 [{string.Join(",", tagDiff)}]");

            // enemy-mechanics-parity 修：这里原本**只**看 Payload["Count"/"GrowScale"/...]。
            // 那是 story-002 之前的存放位置——Count/Scale/Spin/ExplodeOnHit 等早已升成
            // HitEvent 的一等字段，Payload 里不再有它们，于是 org_scatter 这种"只改 Count"的
            // 模块被判成"无可观察差异"，这个报告因此一直是 45/46。
            // 改成先逐个比一等字段（与仓内其它报告同一套反射手法），Payload 只作兜底。
            foreach (var prop in HitEventScalarProps)
            {
                object tv2 = prop.GetValue(t);
                object bv2 = prop.GetValue(b);
                if (!Equals(tv2, bv2))
                {
                    return (true, $"{prop.Name} {bv2}->{tv2}");
                }
            }

            foreach (var key in new[] { "Count", "GrowScale", "OrbitSpeed", "ExplodeOnHit" })
            {
                bool tHas = t.Payload.TryGetValue(key, out var tv);
                bool bHas = b.Payload.TryGetValue(key, out var bv);
                if (tHas != bHas || (tHas && !Equals(tv, bv)))
                    return (true, $"Payload[{key}] 差异");
            }

            return (false, "HitEvent 无可观察差异");
        }

        private static (string, bool, string) RunGene(string id)
        {
            var engine = new Engine();
            var contract = GeneCatalog.Get(id)();
            var testVector = engine.NormalizeContracts(new[] { contract });
            var baseVector = engine.NormalizeContracts(Array.Empty<IContract>());

            foreach (var prop in RuleVectorScalarProps)
            {
                var tv = prop.GetValue(testVector);
                var bv = prop.GetValue(baseVector);
                if (!Equals(tv, bv))
                    return (id, true, $"{prop.Name} {bv}->{tv}");
            }
            return (id, false, "RuleVector 标量无差异");
        }
    }
}
