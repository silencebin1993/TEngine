using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG6-LOG-10（FG03 FGR-LOG-046）：管线泄漏液洼在战斗内核里的门面——热更层玩法代码（<c>Logistics.PipeLeakService</c>）只经这里碰内核（FG14 §5 硬约束 3）。
    /// 液洼 = 中立的内核区域（种类 <see cref="CombatConst.ZoneKindLeak"/>，外部编号 = 液洼记录编号）：站进去的己方机器与敌人都按节拍挂上流体标签（油污 / 浸湿 / 腐蚀），
    /// 遇上能和它起反应、且会留下残留区域的标签时整片反应成残留区域（燃油遇火 = 燃烧区）。逐单位、逐区域的判定都在 Main/Sim（Burst）。
    /// 区域随地点快照进存档（格式 12），读档后液洼服务按阶段与记录对账。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>登记或更新一块液洼（还没反应的那块）；已反应的同编号区域不动，返回 false。</summary>
        public bool UpsertLeak(int leakId, Vector2 position, float radius, uint statusMask) =>
            !IsDisposed && Kernel.UpsertLeakZone(leakId, new double2(position.x, position.y), radius, statusMask);

        /// <summary>移除一块液洼（含已反应的残留区域）。</summary>
        public int RemoveLeak(int leakId) => IsDisposed ? 0 : Kernel.RemoveLeakZone(leakId);

        /// <summary>编号为 <paramref name="leakId"/> 的液洼区域此刻的样子（还没反应或已反应、未到期）。</summary>
        public bool TryGetLeak(int leakId, out CombatZone zone)
        {
            if (IsDisposed)
            {
                zone = default;
                return false;
            }
            return Kernel.TryGetLeakZone(leakId, out zone);
        }

        public int LeakZoneCount => IsDisposed ? 0 : Kernel.LeakZoneCount;

        /// <summary>内核里全部液洼区域的编号（含已反应的；读档对账找孤儿用）。O(区域数)。</summary>
        public void LeakIds(List<int> into)
        {
            into.Clear();
            int n = IsDisposed ? 0 : Kernel.ZoneCount;
            for (int i = 0; i < n; i++)
            {
                if (Kernel.TryGetZone(i, out CombatZone z) && z.Kind == CombatConst.ZoneKindLeak && !into.Contains(-z.Owner))
                {
                    into.Add(-z.Owner);
                }
            }
        }

        /// <summary>取走本步液洼整片反应的记录（x = 液洼编号，y = 反应规则下标）。</summary>
        public int DrainLeakReactions(List<int2> into) => IsDisposed ? 0 : Kernel.DrainLeakReactions(into);
    }
}
