using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-005（combat-visualization）：验证装配链路未断 + Shape 表现层二次映射（R4）落地——
    /// org_emitter Carrier 装不同 Module 基因（org_flagella/org_lyso/org_scatter）后，判定层
    /// <see cref="HitEvent.Shape"/> 仍恒为 Bolt（数值规则不变），但真实战斗路径
    /// （<see cref="CarrierCompiler.Compile"/> → <see cref="MetabolicSliceBridge.ApplyEvent"/> →
    /// <see cref="ComposeCastSignal"/>）呈现的表现 Shape 随装配变化，正式战斗可达 ≥4 种可区分弹道。
    /// 纯 C#，execute_code 直调，不进 Play（验收优先代码断言，见根 CLAUDE.md）。
    /// </summary>
    public static class OrganDifferentiationSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);

            var reserve = new GeneReserve();
            CarrierInstance bare = MakeCarrier(reserve, "carrier_bare", "org_emitter");
            CarrierInstance wave = MakeCarrier(reserve, "carrier_wave", "org_emitter", "org_flagella");
            CarrierInstance spore = MakeCarrier(reserve, "carrier_spore", "org_emitter", "org_lyso");
            CarrierInstance arc = MakeCarrier(reserve, "carrier_arc", "org_emitter", "org_scatter");
            CarrierInstance melee = MakeCarrier(reserve, "carrier_melee", "org_cilia");

            var world = new WorldState();
            var sim = new SimBridge();
            var stats = new StatSheet();
            var bridge = new MetabolicSliceBridge();
            bridge.Bind(sim, stats);

            var distinctShapes = new HashSet<string>();
            string captured = null;
            void Handler(ComposeCastSignal s) => captured = s.Shape;
            Signals.Subscribe<ComposeCastSignal>(Handler);

            try
            {
                var cases = new (string Label, CarrierInstance Carrier, string ExpectedRawShape, string ExpectedPresentShape)[]
                {
                    ("空槽", bare, "Bolt", "Bolt"),
                    ("org_flagella", wave, "Bolt", "Wave"),
                    ("org_lyso", spore, "Bolt", "Spore"),
                    ("org_scatter", arc, "Bolt", "Arc"),
                    ("org_cilia", melee, "Melee", "Melee"),
                };

                foreach (var (label, carrier, expectedRaw, expectedPresent) in cases)
                {
                    List<HitEvent> events = CarrierCompiler.Compile(engine, carrier, reserve, world, seed: 1);
                    if (events.Count == 0)
                    {
                        return (false, $"{label} CarrierCompiler 未产出 HitEvent（装配链路断点）");
                    }
                    HitEvent raw = events[0];
                    if (raw.Shape != expectedRaw)
                    {
                        return (false, $"{label} 判定层 evt.Shape 应恒为 {expectedRaw}（Shape 不改判定），实际 {raw.Shape}");
                    }
                    if (raw.Damage <= 0f)
                    {
                        return (false, $"{label} evt.Damage 应 >0（EnergyCore(10) 未生效，装配链路可能断点）");
                    }

                    captured = null;
                    if (!bridge.ApplyEvent(raw))
                    {
                        return (false, $"{label} ApplyEvent 返回 false，未产生可观察效果");
                    }
                    if (captured != expectedPresent)
                    {
                        return (false, $"{label} 表现层 ComposeCastSignal.Shape 期望 {expectedPresent}，实际 {captured}");
                    }
                    distinctShapes.Add(captured);
                }
            }
            finally
            {
                Signals.Unsubscribe<ComposeCastSignal>(Handler);
            }

            if (distinctShapes.Count < 4)
            {
                return (false, $"可区分弹道种数={distinctShapes.Count}（<4）：{string.Join(",", distinctShapes)}");
            }

            return (true, $"装配链路未断+Shape 随装配变化，正式战斗可达 {distinctShapes.Count} 种可区分弹道："
                + $"{string.Join(",", distinctShapes)}；判定层 evt.Shape 全程恒为 Bolt/Melee（数值规则未变）");
        }

        private static CarrierInstance MakeCarrier(GeneReserve reserve, string carrierId, string organelleId, string moduleGeneId = null)
        {
            var carrier = new CarrierInstance(carrierId, organelleId);
            if (moduleGeneId != null)
            {
                string instanceId = carrierId + "_gene0";
                reserve.TryAdd(new GeneInstance(instanceId, moduleGeneId, GeneLocation.Reserve()));
                carrier.Slots[0].GeneInstanceId = instanceId;
            }
            return carrier;
        }
    }
}
