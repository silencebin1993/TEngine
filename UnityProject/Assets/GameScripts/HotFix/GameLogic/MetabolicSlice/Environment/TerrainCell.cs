using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Environment
{
    /// <summary>战场格子：TerrainTag 常驻，Residue 有寿命可叠加。不建真实坐标/寻路网格（GDD §1.7 非目标）。</summary>
    public sealed class TerrainCell
    {
        public string CellId { get; }
        public HashSet<string> Tags { get; } = new HashSet<string>();
        public List<ResidueStack> Residues { get; } = new List<ResidueStack>();

        public TerrainCell(string cellId) => CellId = cellId;
    }
}
