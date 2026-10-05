using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    public static partial class TurretService
    {
        /// <summary>FG6-DEF-03：一座炮塔此刻的耐久（建成且内核里有单位 = 内核读数，否则 = 建筑记录）。</summary>
        public static float DurabilityOf(CampaignState s, BuildingRecord b)
        {
            if (b == null)
            {
                return 0f;
            }
            TurretRecord r = Find(s, b.BuildingId);
            CombatSite site = HomeSite;
            if (r != null && site != null && site.TryGetTurretState(r.Serial, out TurretUnitState us) && us.Alive)
            {
                return us.Health;
            }
            return BuildingOps.Durability(b);
        }

        /// <summary>
        /// FG6-DEF-03（FGR-DEF-014 维修无人机修炮塔）：给一座建成的炮塔恢复 <paramref name="amount"/> 点耐久，返回实际恢复了多少。
        /// 内核里有它时直接改内核血量并同步建筑记录与“上次推送值”（不抹掉对账间隔里挨的打）；没有时改建筑记录（下一次对账推给内核）。O(1)。
        /// </summary>
        public static float Heal(CampaignState s, BuildingRecord b, float amount)
        {
            if (s == null || b == null || amount <= 0f || !IsBuilt(b))
            {
                return 0f;
            }
            float max = BuildingOps.MaxDurability(b.BuildingTypeId);
            TurretRecord r = Find(s, b.BuildingId);
            CombatSite site = HomeSite;
            if (r != null && site != null && site.TryGetTurretState(r.Serial, out TurretUnitState us) && us.Alive)
            {
                float hp = Mathf.Min(max, us.Health + amount);
                float done = hp - us.Health;
                if (done <= 0f)
                {
                    return 0f;
                }
                site.SetTurretHealth(r.Serial, hp, max);
                b.Health = hp;
                if (Rt.TryGetValue(r.Serial, out Runtime rt))
                {
                    rt.LastPushedHp = hp;
                }
                return done; // 建筑修订号由调用方（RepairDroneService.ApplyRepair）按 10% 档节流 +1，不每步 Touch

            }
            float before = BuildingOps.Durability(b);
            float after = Mathf.Min(max, before + amount);
            b.Health = after;
            return after - before;
        }
    }
}
