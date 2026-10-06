using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    public static partial class DefenseService
    {
        /// <summary>
        /// FG6-DEF-03（FGR-DEF-014 维修无人机修屏障 / 护盾 / 陷阱）：给一座建成的防御建筑恢复 <paramref name="amount"/> 点耐久，返回实际恢复了多少。
        /// 内核里有它（建成且活着）时直接改内核血量并同步建筑记录与“上次推送值”——不走“建筑记录被别处改过 = 推给内核”的对账分支，
        /// 那样会把对账间隔里挨的打抹掉（维修与挨打同时发生时两边都算数）。内核里没有时改建筑记录（下一次对账推给内核）。O(1)。
        /// </summary>
        public static float Heal(CampaignState s, BuildingRecord b, float amount)
        {
            if (s == null || b == null || amount <= 0f || !IsBuilt(b))
            {
                return 0f;
            }
            float max = BuildingOps.MaxDurability(b.BuildingTypeId);
            DefenseRecord r = Find(s, b.BuildingId);
            CombatSite site = HomeSite;
            if (r != null && site != null && site.TryGetDefenseHealth(r.Serial, out float cur, out float kernelMax, out bool alive) && alive)
            {
                float top = Mathf.Max(max, kernelMax);
                float hp = Mathf.Min(top, cur + amount);
                float done = hp - cur;
                if (done <= 0f)
                {
                    return 0f;
                }
                site.SetDefenseHealth(r.Serial, hp, top);
                b.Health = hp;
                RuntimeOf(r).LastPushedHp = hp;
                return done; // 建筑修订号由调用方（RepairDroneService.ApplyRepair）按 10% 档节流 +1，不每步 Touch
            }
            float before = BuildingOps.Durability(b);
            float after = Mathf.Min(max, before + amount);
            b.Health = after;
            return after - before;
        }

        /// <summary>
        /// FG6-DEF-08 复修（FGT-DEF-009）：建筑记录的耐久被别处直接改过（机器维修完工回满、<see cref="BuildingOps.ApplyDamage"/>）之后立即推给内核并同步“上次推送值”，
        /// 与 <see cref="TurretService.CommitRecord"/> 同一口径。内核上限与记录类型不一致（原地升级刚完工）时不推，留给下一次对账先按比例换算。O(1)。
        /// </summary>
        public static bool CommitRecord(CampaignState s, BuildingRecord b)
        {
            if (s == null || b == null || !IsBuilt(b))
            {
                return false;
            }
            DefenseRecord r = Find(s, b.BuildingId);
            CombatSite site = HomeSite;
            if (r == null || site == null || site.IsDisposed || !site.TryGetDefenseHealth(r.Serial, out _, out float kernelMax, out bool alive) || !alive)
            {
                return false;
            }
            float max = BuildingOps.MaxDurability(b.BuildingTypeId);
            if (Mathf.Abs(kernelMax - max) > 0.01f)
            {
                return false;
            }
            float hp = Mathf.Clamp(b.Health, 1f, max);
            site.SetDefenseHealth(r.Serial, hp, max);
            b.Health = hp;
            RuntimeOf(r).LastPushedHp = hp;
            return true;
        }
    }
}
