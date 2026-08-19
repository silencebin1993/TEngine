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

            for (int i = 0; i < carrier.Slots.Length; i++)
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

                // 004 迁徙后：在此插入 Module 分支基因目录查表 + moduleGenes.Add(...)，签名不变。
                TEngine.Log.Warning($"[CarrierCompiler] 基因 {gene.GeneId} 未在 GeneCatalog 命中（004 迁徙前占位，或拼写错误）");
            }

            var rules = engine.NormalizeContracts(contracts);

            var chain = new List<IModule> { new EnergyCore(10f) };
            chain.AddRange(moduleGenes);
            chain.Add(new Actuator());

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
