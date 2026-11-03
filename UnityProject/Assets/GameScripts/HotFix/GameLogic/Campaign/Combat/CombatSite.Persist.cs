using System.Collections.Generic;
using BinGames.Sim.Combat;

namespace GameLogic.Campaign.Combat
{
    /// <summary>EMG2-PERSIST-01：长期留存物的门面（热更玩法代码只经 Campaign/Combat/ 碰内核，FG14 §5 硬约束 3）。全部只在下单、悬停、定时检查时调用，不按帧。</summary>
    public sealed partial class CombatSite
    {
        public bool PersistSite => !IsDisposed && Kernel.PersistSite;

        public bool TryGetPersist(byte kind, int id, out EmgPersistItem item) => Kernel.TryGetPersist(kind, id, out item);

        public void CollectPersist(List<EmgPersistItem> into) => Kernel.CollectPersist(into);

        public bool ClearPersist(byte kind, int id) => Kernel.ClearPersist(kind, id);

        public uint PersistSignature() => Kernel.PersistSignature();

        public (int Wrecks, int Solids) PersistTally() => Kernel.PersistTally();

        /// <summary>累计崩解为碎屑的件数（长期留存固态 + 残骸满额）。</summary>
        public int PersistCappedTotal => Kernel.PersistCounts.Capped + Kernel.WreckCounts.Capped;
    }
}
