using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Graph
{
    /// <summary>
    /// 把方格 + 有向箭头编译成若干条线性装配链（§4.3 选项 2：对每个 source→sink 可达路径预抽成线性
    /// 列表再 RunAssembly）。多源多汇 = 多条 source→sink 简单路径；不做真实分叉/汇合的并行拓扑——
    /// Engine 是单 Packet 顺序流水线（见 ComposeEngine/README.md「Module 装配链」一节），Splitter/Combiner
    /// 也只是同一条链内的标量近似，不是真的把 Packet 拆成两个对象。
    /// </summary>
    public static class PathCompiler
    {
        public sealed class CompiledPath
        {
            public IReadOnlyList<int> SlotPath { get; }
            public IReadOnlyList<IModule> Modules { get; }

            public CompiledPath(IReadOnlyList<int> slotPath, IReadOnlyList<IModule> modules)
            {
                SlotPath = slotPath;
                Modules = modules;
            }
        }

        public static List<CompiledPath> Compile(SlotGrid grid)
        {
            var result = new List<CompiledPath>();
            var adjacency = BuildAdjacency(grid);

            for (int slotId = 0; slotId < SlotGrid.SlotCount; slotId++)
            {
                if (!IsSource(grid, slotId)) continue;
                var visited = new HashSet<int> { slotId };
                var stack = new List<int> { slotId };
                Walk(grid, adjacency, slotId, visited, stack, result);
            }
            return result;
        }

        private static Dictionary<int, List<int>> BuildAdjacency(SlotGrid grid)
        {
            var map = new Dictionary<int, List<int>>();
            foreach (var edge in grid.Edges)
            {
                if (!map.TryGetValue(edge.From, out var list))
                {
                    list = new List<int>();
                    map[edge.From] = list;
                }
                list.Add(edge.To);
            }
            return map;
        }

        private static bool IsSource(SlotGrid grid, int slotId)
        {
            var part = grid.Slots[slotId].Part;
            var def = part == null ? null : CardCatalog.Get(part.CardDefId);
            return def != null && def.IsSource;
        }

        private static bool IsSink(SlotGrid grid, int slotId)
        {
            var part = grid.Slots[slotId].Part;
            var def = part == null ? null : CardCatalog.Get(part.CardDefId);
            return def != null && def.IsSink;
        }

        /// <summary>DFS 枚举简单路径；visited 防环——箭头成环时不会死循环，只是环外的边不会被重复计入同一条路径。</summary>
        private static void Walk(SlotGrid grid, Dictionary<int, List<int>> adjacency, int current,
            HashSet<int> visited, List<int> stack, List<CompiledPath> result)
        {
            if (stack.Count > 1 && IsSink(grid, current))
            {
                result.Add(BuildPath(grid, stack));
            }

            if (!adjacency.TryGetValue(current, out var neighbors)) return;
            foreach (var next in neighbors)
            {
                if (visited.Contains(next)) continue;
                visited.Add(next);
                stack.Add(next);
                Walk(grid, adjacency, next, visited, stack, result);
                stack.RemoveAt(stack.Count - 1);
                visited.Remove(next);
            }
        }

        private static CompiledPath BuildPath(SlotGrid grid, List<int> slotPath)
        {
            var modules = new List<IModule>(slotPath.Count * 2);
            foreach (var slotId in slotPath)
            {
                var node = grid.Slots[slotId];
                modules.Add(new SlotPassiveModule(node.SlotType));
                if (!node.IsEmpty)
                {
                    var def = CardCatalog.Get(node.Part.CardDefId);
                    if (def?.CreateModule != null) modules.Add(def.CreateModule());
                }
            }
            return new CompiledPath(new List<int>(slotPath), modules);
        }
    }
}
