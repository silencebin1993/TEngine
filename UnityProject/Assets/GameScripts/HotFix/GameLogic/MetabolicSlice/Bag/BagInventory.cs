using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Bag
{
    public enum AddResult { Added, NeedDecision }

    /// <summary>储备囊（§3）：容量 SoftCap，满则必须抉择（丢囊中一件/丢新件/合成腾位），不允许静默超容。</summary>
    public sealed class BagInventory
    {
        public int Cap { get; set; }
        public List<PartInstance> Items { get; } = new List<PartInstance>();

        public BagInventory(int cap) => Cap = cap;

        public AddResult TryAdd(PartInstance part)
        {
            if (Items.Count >= Cap) return AddResult.NeedDecision;
            part.Location = PartLocation.Bag();
            Items.Add(part);
            return AddResult.Added;
        }

        public bool Remove(string partId) => Items.RemoveAll(p => p.PartId == partId) > 0;
    }
}
