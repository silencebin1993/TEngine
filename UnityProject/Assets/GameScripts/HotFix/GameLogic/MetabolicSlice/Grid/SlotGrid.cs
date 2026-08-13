using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Grid
{
    /// <summary>§9 最小落地：固定 3×3，可空放；玩家画的有向边只允许连四邻。</summary>
    public sealed class SlotGrid
    {
        public const int Width = 3;
        public const int Height = 3;
        public const int SlotCount = Width * Height;

        /// <summary>箭头数上限 = 活性槽数(有 Part 的槽) × 系数（§4.1 第 4 条）。</summary>
        private const int EdgeSoftCapFactor = 2;

        public SlotNode[] Slots { get; } = new SlotNode[SlotCount];
        private readonly List<DirectedEdge> _edges = new List<DirectedEdge>();
        public IReadOnlyList<DirectedEdge> Edges => _edges;

        public SlotGrid(SlotType defaultType)
        {
            for (int i = 0; i < SlotCount; i++) Slots[i] = new SlotNode(i, defaultType);
        }

        public static bool IsAdjacent(int a, int b)
        {
            int ax = a % Width, ay = a / Width;
            int bx = b % Width, by = b / Width;
            int dx = ax - bx, dy = ay - by;
            return (dx == 0 && (dy == 1 || dy == -1)) || (dy == 0 && (dx == 1 || dx == -1));
        }

        private int ActiveSlotCount()
        {
            int count = 0;
            for (int i = 0; i < SlotCount; i++) if (!Slots[i].IsEmpty) count++;
            return count;
        }

        /// <summary>邻接校验 + SoftCap；不合法一律拒绝，不静默截断（Reject-to-Safe，同 ComposeEngine 风格）。</summary>
        public bool TryAddEdge(int from, int to)
        {
            if (from == to) return false;
            if (from < 0 || from >= SlotCount || to < 0 || to >= SlotCount) return false;
            if (!IsAdjacent(from, to)) return false;
            foreach (var e in _edges) if (e.From == from && e.To == to) return false;
            int cap = System.Math.Max(1, ActiveSlotCount()) * EdgeSoftCapFactor;
            if (_edges.Count >= cap) return false;
            _edges.Add(new DirectedEdge(from, to));
            return true;
        }

        public void RemoveEdge(int from, int to) => _edges.RemoveAll(e => e.From == from && e.To == to);
    }
}
