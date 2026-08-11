using System.Collections.Generic;
using ChemEngine;
using ChemEngine.Core;
using GameLogic.MetabolicSlice.Graph;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// 每 Tick 驱动（§7）：编译路径 → 逐路径 RunAssembly → 全路径复用同一份 RuleVector 走 ApplyPipeline
    /// （契约只需 NormalizeContracts 一次，避免每条路径重复计算）。不接战斗表现/伤害应用——
    /// §9 范围不做完整局内战斗 UI，调用方自己消费返回的 HitEvent。
    /// </summary>
    public sealed class MetabolicSliceRunner
    {
        private readonly Engine _engine;

        public MetabolicSliceRunner(Engine engine) => _engine = engine;

        public List<HitEvent> Tick(SlotGrid grid, IReadOnlyList<IContract> globalGeneContracts, WorldState world, int seed)
        {
            var rules = _engine.NormalizeContracts(globalGeneContracts);
            var events = new List<HitEvent>();

            var paths = PathCompiler.Compile(grid);
            foreach (var path in paths)
            {
                var raw = _engine.RunAssembly(path.Modules, ticks: 1, seed: seed);
                foreach (var evt in raw)
                {
                    events.Add(_engine.ApplyPipeline(evt, rules, world));
                }
            }
            return events;
        }
    }
}
