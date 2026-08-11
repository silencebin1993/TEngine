namespace GameLogic.MetabolicSlice.Grid
{
    /// <summary>玩家画的有向代谢管（§4）。本窗仅节点级，不做管类边模块（§9 范围不需要）。</summary>
    public readonly struct DirectedEdge
    {
        public readonly int From;
        public readonly int To;

        public DirectedEdge(int from, int to)
        {
            From = from;
            To = to;
        }
    }
}
