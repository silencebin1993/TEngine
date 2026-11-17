using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

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

        /// <summary>EMG-R 出口补缺口（FG-GAP-224）：格区间 [min, max] 内有会烧蚀设施的物质斑块（structDps &gt; 0）或燃烧液洼——自动重建等火灭用。O(斑块数 + 区域数)，只在派单 / 定时重试 / 派工评估时调用。</summary>
        public bool StructHazardIn(int minX, int minY, int maxX, int maxY) => !IsDisposed && Kernel.StructHazardIn(minX, minY, maxX, maxY);

        /// <summary>EMG-R 出口补缺口第 5 轮（DEBT-EMGREXIT-06）：站在会烧蚀设施的斑块 / 燃烧液洼里时撤到圈外哪一点（内核算）；不在里面 = false。只在自动重建单因起火挂起时调用。</summary>
        public bool StructHazardEscape(Vector2 at, out Vector2 safe)
        {
            safe = at;
            if (IsDisposed || !Kernel.StructHazardEscape(new double2(at.x, at.y), out double2 s))
            {
                return false;
            }
            safe = new Vector2((float)s.x, (float)s.y);
            return true;
        }

        public (int Wrecks, int Solids) PersistTally() => Kernel.PersistTally();

        /// <summary>累计崩解为碎屑的件数（长期留存固态 + 残骸满额）。</summary>
        public int PersistCappedTotal => Kernel.PersistCounts.Capped + Kernel.WreckCounts.Capped;
    }
}
