using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-009：对照验收汇总（人诉求「器官/基因效果看不到区别」的固化探针）。
    /// 独立自洽：直接用 <see cref="ComposeEngine.Builtin.Modules"/> 手写链，
    /// 不依赖 <see cref="IdSmokeReport"/> / <see cref="LookDevFixtures"/> 私有成员，允许手法重复。
    /// Carrier Shape/指示器一对（Play-only，需 CarrierRegistry/WhiteboxComposeAimIndicator 真实对象）
    /// 不适合塞进纯 C# 静态方法，按 007 手法在证据 execute_code 脚本里手写，见证据 md。
    /// </summary>
    public static class DiffAcceptanceReport
    {
        public static List<(string Pair, bool Pass, string Reason)> RunPure()
        {
            var results = new List<(string, bool, string)>
            {
                RunCountPair(),
                RunTagPair(),
                RunScalePair(),
                RunExplodePair(),
            };
            return results;
        }

        // 基因：空槽 vs org_scatter（Count）
        private static (string, bool, string) RunCountPair()
        {
            var engine = new Engine();
            var baseEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Actuator() });
            var testEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Scatterer(), new Actuator() });
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return ("空槽 vs org_scatter（Count）", differs, reason);
        }

        // 基因：无元素 vs org_perox（Tag 染色）
        private static (string, bool, string) RunTagPair()
        {
            var engine = new Engine();
            var baseEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Actuator() });
            var testEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new TagAttach("Fire"), new Actuator() });
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return ("无元素 vs org_perox（Tag 染色）", differs, reason);
        }

        // 基因：org_swell（Scale）
        private static (string, bool, string) RunScalePair()
        {
            var engine = new Engine();
            var baseEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Actuator() });
            var testEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Grow(3f), new Actuator() });
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return ("空槽 vs org_swell（Scale）", differs, reason);
        }

        // 基因：org_lyso（Explode）
        private static (string, bool, string) RunExplodePair()
        {
            var engine = new Engine();
            var baseEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new Actuator() });
            var testEvents = engine.RunAssembly(new IModule[] { new EnergyCore(), new ExplodeOnHit(), new Actuator() });
            var (differs, reason) = DiffEvents(testEvents, baseEvents);
            return ("空槽 vs org_lyso（Explode）", differs, reason);
        }

        /// <summary>手法参照 <see cref="IdSmokeReport"/>.DiffEvents（独立实现，不复用其私有方法），
        /// 但修正一处实读发现的判定缺口：<see cref="HitEvent"/> 的 Count/Scale/Spin/Orbit/ExplodeOnHit
        /// 是直接字段（见 HitEvent.cs），不在 Payload 字典里——原判定只查 Payload["Count"/...] 永远查不到，
        /// 导致 org_scatter（Count）等对照假性 Pass=False（已用 IdSmokeReport.Run() 实测复现，
        /// 001 决策表"org_scatter 预期 Pass"的判断依据即此判定缺口，未察觉其误判方向）。
        /// 本报告直接比较 HitEvent 字段本身，不做 Payload 旁路查找。</summary>
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
            if (Math.Abs(t.Count - b.Count) > 1e-4f) return (true, $"Count {b.Count:0.##}->{t.Count:0.##}");
            if (Math.Abs(t.Scale - b.Scale) > 1e-4f) return (true, $"Scale {b.Scale:0.##}->{t.Scale:0.##}");
            if (Math.Abs(t.Spin - b.Spin) > 1e-4f) return (true, $"Spin {b.Spin:0.##}->{t.Spin:0.##}");
            if (Math.Abs(t.Orbit - b.Orbit) > 1e-4f) return (true, $"Orbit {b.Orbit:0.##}->{t.Orbit:0.##}");
            if (t.ExplodeOnHit != b.ExplodeOnHit) return (true, $"ExplodeOnHit {b.ExplodeOnHit}->{t.ExplodeOnHit}");

            var tagDiff = new HashSet<string>(t.Tags);
            tagDiff.SymmetricExceptWith(b.Tags);
            if (tagDiff.Count > 0) return (true, $"Tags 差异 [{string.Join(",", tagDiff)}]");

            foreach (var kv in t.Payload)
            {
                bool bHas = b.Payload.TryGetValue(kv.Key, out var bv);
                if (!bHas || !Equals(kv.Value, bv))
                    return (true, $"Payload[{kv.Key}] 差异");
            }
            foreach (var key in b.Payload.Keys)
            {
                if (!t.Payload.ContainsKey(key))
                    return (true, $"Payload[{key}] 差异");
            }

            return (false, "HitEvent 无可观察差异");
        }
    }
}
