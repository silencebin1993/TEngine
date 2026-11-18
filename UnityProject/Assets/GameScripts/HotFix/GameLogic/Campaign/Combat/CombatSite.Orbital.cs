using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>FG12-EVT-04 轨道坠落的门面（热更玩法代码只经 Campaign/Combat/ 碰内核）。只在撞击那一刻调用一次。</summary>
    public sealed partial class CombatSite
    {
        /// <summary>圈内机器与敌人各受一次撞击伤害（内核 O(单位数)，一次性）；返回受击单位数。</summary>
        public int StrikeCircle(Vector2 at, float radius, float damage) =>
            IsDisposed ? 0 : Kernel.StrikeCircle(new double2(at.x, at.y), radius, damage);

        /// <summary>
        /// 落点圆心在己方活跃护盾罩内时，各护盾按顺序（内核护盾表顺序，确定）用剩余护盾值吸收撞击能量，总共最多吸收 <paramref name="maxAbsorb"/>。
        /// 返回实际吸收量。O(护盾数 ≤ CombatConst.MaxShields)，一次性。
        /// </summary>
        public float AbsorbImpact(Vector2 at, float maxAbsorb)
        {
            if (IsDisposed || !(maxAbsorb > 0f))
            {
                return 0f;
            }
            float left = maxAbsorb;
            var p = new double2(at.x, at.y);
            for (int i = 0; i < Kernel.ShieldCount && left > 0f; i++)
            {
                CombatShield sh = Kernel.ShieldAt(i);
                if (sh.Faction != CombatFaction.Player || sh.Active == 0 || !(sh.Hp > 0f) || math.distancesq(sh.Pos, p) > (double)sh.Radius * sh.Radius)
                {
                    continue;
                }
                left -= Kernel.AbsorbShield(sh.ExtKey, left);
            }
            return maxAbsorb - Mathf.Max(0f, left);
        }

        /// <summary>坠落残骸（FGR-EMG-113 实体）：返回残骸稳定 Id（0 = 失败）。</summary>
        public int SpawnWreckAt(Vector2 at, float radius, float maxHp, float mass, float temp) =>
            IsDisposed ? 0 : Kernel.SpawnWreckAt(new double2(at.x, at.y), radius, maxHp, mass, temp);
    }
}
