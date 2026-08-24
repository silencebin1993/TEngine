using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>story-003：把一个激活 Carrier 的槽内基因编译成 RuleVector + Module 链，跑出 HitEvent。
    /// 与旧 PathCompiler+MetabolicSliceRunner.Tick(grid, …) 并行新增，不改旧路径（W12/D5）。
    /// 内建 EnergyCore/Actuator 是链固定头尾，不占玩家插槽（D1/D2）。</summary>
    public static class CarrierCompiler
    {
        public static List<HitEvent> Compile(Engine engine, CarrierInstance carrier, GeneReserve reserve, WorldState world, int seed, string cellId = null)
        {
            if (carrier == null)
            {
                return new List<HitEvent>();
            }

            var contracts = new List<IContract>();
            var moduleGenes = new List<IModule>();

            for (int i = 0; i < carrier.Slots.Count; i++)
            {
                var slot = carrier.Slots[i];
                if (slot.GeneInstanceId == null)
                {
                    continue;
                }

                var gene = reserve.Find(slot.GeneInstanceId);
                if (gene == null)
                {
                    TEngine.Log.Warning($"[CarrierCompiler] 悬空基因实例引用: slot={i} geneInstanceId={slot.GeneInstanceId}");
                    continue;
                }

                var createContract = GeneCatalog.Get(gene.GeneId);
                if (createContract != null)
                {
                    contracts.Add(createContract());
                    continue;
                }

                var createModule = GeneCatalog.GetModule(gene.GeneId);
                if (createModule != null)
                {
                    moduleGenes.Add(createModule());
                    continue;
                }

                TEngine.Log.Warning($"[CarrierCompiler] 基因 {gene.GeneId} 未在 GeneCatalog 命中（004 迁徙前占位，或拼写错误）");
            }

            var rules = engine.NormalizeContracts(contracts);

            // D10：链尾按 Carrier 反查执行器；OrganelleId 为 null / 查不到 / CreateModule 为 null 时
            // 回落 new Actuator()（Reject-to-Safe），保证 003 的 23 条断言里无器官 Carrier 仍出 Bolt/Damage=10。
            // Dictionary.TryGetValue 对 null key 会抛 ArgumentNullException，OrganelleId 必须先判空再查表。
            // R0（carrier-organ-expansion 001 Preflight）：非 Sink 器官（19 个升格 Module 基因，Role
            // 全部是 Relay/Transform/Edge）自身机制不是链的"出口"，若直接顶替 Actuator 当链尾会导致
            // 这些新 Carrier 零 HitEvent。修复：非 Sink 器官先把自己的机制接入链，链尾恒为 Actuator；
            // Sink 器官（org_emitter/org_cilia）行为不变，仍是 CreateModule() 本身当链尾。
            var tailDef = carrier.OrganelleId != null ? OrganelleCatalog.Get(carrier.OrganelleId) : null;

            var chain = new List<IModule> { new EnergyCore(10f) };
            chain.AddRange(moduleGenes);
            if (tailDef != null && tailDef.Role != OrganelleRole.Sink)
            {
                chain.Add(tailDef.CreateModule());
            }
            IModule tail = (tailDef != null && tailDef.Role == OrganelleRole.Sink)
                ? tailDef.CreateModule() : new Actuator();
            chain.Add(tail);

            var raw = engine.RunAssembly(chain, ticks: 1, seed: seed);
            var events = new List<HitEvent>();
            foreach (var evt in raw)
            {
                if (cellId != null) evt.TargetId = cellId;
                events.Add(engine.ApplyPipeline(evt, rules, world));
            }
            return events;
        }
    }
}
