using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Digestion
{
    public enum InsertResult { Inserted, ChamberFull }

    /// <summary>消化泡（§C1 捕食消化）：容量 Cap，满则拒绝插入，不静默超容（同 BagInventory.TryAdd 纪律）。</summary>
    public sealed class DigestionChamber
    {
        private const float ToxicityFailThreshold = 1f;

        private readonly List<Reagent> _items = new List<Reagent>();

        public int Cap { get; set; }
        public IReadOnlyList<Reagent> Items => _items;

        public DigestionChamber(int cap) => Cap = cap;

        public InsertResult Insert(Reagent reagent)
        {
            if (_items.Count >= Cap) return InsertResult.ChamberFull;
            _items.Add(reagent);
            return InsertResult.Inserted;
        }

        /// <summary>每个在泡 Reagent 累进 Progress；达到 MaxTicks 按 Toxicity 结算并移出（不做部分退还，见 Expel）。</summary>
        public List<DigestionEvent> Tick(int dt)
        {
            var events = new List<DigestionEvent>();
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var reagent = _items[i];
                reagent.Progress += dt;
                if (reagent.Progress < reagent.MaxTicks) continue;

                _items.RemoveAt(i);
                events.Add(reagent.Toxicity >= ToxicityFailThreshold
                    ? new DigestionEvent(DigestionEventKind.Failed, reagent.ReagentId, null)
                    : new DigestionEvent(DigestionEventKind.Completed, reagent.ReagentId, reagent.PotentialCrafts[0]));
            }
            return events;
        }

        public bool Expel(string reagentId) => _items.RemoveAll(r => r.ReagentId == reagentId) > 0;
    }
}
