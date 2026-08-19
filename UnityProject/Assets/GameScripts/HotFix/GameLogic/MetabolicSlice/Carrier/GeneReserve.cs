using System.Collections.Generic;
using System.Linq;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>基因储备容器（D1），语义同 BagInventory：无限容量。
    /// 内部按 GeneInstanceId 持有全部已知实例（不论当前在囊里还是插在某 Carrier 槽位上）——
    /// 装备时只切换 Location，不物理移除，否则 CarrierGeneService 卸下时找不回实例对象。
    /// Items 对外只暴露"当前在囊里"的子集，语义等价 BagInventory.Items。</summary>
    public sealed class GeneReserve
    {
        private readonly Dictionary<string, GeneInstance> _all = new Dictionary<string, GeneInstance>();

        public IReadOnlyList<GeneInstance> Items => _all.Values.Where(g => g.Location.IsInReserve).ToList();

        public void TryAdd(GeneInstance gene)
        {
            gene.Location = GeneLocation.Reserve();
            _all[gene.GeneInstanceId] = gene;
        }

        public bool Remove(string geneInstanceId) => _all.Remove(geneInstanceId);

        /// <summary>按 id 查找，不论当前在囊里还是已装备（CarrierGeneService 卸下时要靠这个找回实例）。</summary>
        public GeneInstance Find(string geneInstanceId) => _all.TryGetValue(geneInstanceId, out var g) ? g : null;
    }
}
