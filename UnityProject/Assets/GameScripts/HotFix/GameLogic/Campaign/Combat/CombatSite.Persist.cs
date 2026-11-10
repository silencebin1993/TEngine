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

        /// <summary>EMG4-TER-01：格中心 (x, y) 落在熔坑里（家园建造校验；O(熔坑数 ≤ 8)，没有熔坑时一次比较）。</summary>
        public bool PitCovers(int x, int y) => !IsDisposed && Kernel.PitCount > 0 && Kernel.PitCovers(x, y);

        /// <summary>EMG4-TER-01：本地点熔坑数 / 第 i 个熔坑（悬停、自检）。</summary>
        public int PitCount => IsDisposed ? 0 : Kernel.PitCount;
        public EmgPit PitAt(int i) => IsDisposed ? default : Kernel.PitAt(i);

        public (int Wrecks, int Solids) PersistTally() => Kernel.PersistTally();

        /// <summary>累计崩解为碎屑的件数（长期留存固态 + 残骸满额）。</summary>
        public int PersistCappedTotal => Kernel.PersistCounts.Capped + Kernel.WreckCounts.Capped;
    }
}
