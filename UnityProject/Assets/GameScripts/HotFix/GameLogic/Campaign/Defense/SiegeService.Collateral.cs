using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-05：溅射（承接 DEBT-FG3LOG03-04 传送带被突袭打坏、DEBT-FG6DEF03-02 / DEBT-FG3LOG05-12 管线被打坏、DEBT-FG3LOG02-03 施工中被摧毁）。
    /// 敌人打中建筑时，内核在被打的那一侧记命中格（同格累计，进快照）；这里每步取至多 siege.collateral_drain_per_step 格，
    /// 命中格 siege.collateral_radius_cells 内的传送带 / 管线挨“这一发 × siege.collateral_ratio”的伤害（到 0 摧毁、留保留设置的虚影），
    /// 施工中的虚影累计伤害，满 建筑最大耐久 × siege.site_hp_fraction = 施工中被摧毁（已消耗材料按比例掉落、虚影保留）。
    /// 热更层每步 O(取出的格数 × 半径内格数)，与敌人数无关。
    /// </summary>
    public static partial class SiegeService
    {
        private static readonly List<CombatSiegeImpact> ImpactScratch = new List<CombatSiegeImpact>(32);

        /// <summary>本会话溅射结算的命中格数 / 打到的传送带格 / 管线格 / 施工虚影（自检读）。</summary>
        public static long CollateralImpacts { get; private set; }
        public static long CollateralBeltHits { get; private set; }
        public static long CollateralPipeHits { get; private set; }
        public static long CollateralSiteHits { get; private set; }

        private static void DrainCollateral(CampaignState state, CombatSite site)
        {
            if (site.SiegeImpactCount == 0)
            {
                return;
            }
            site.DrainSiegeImpacts(SiegeCatalog.CollateralDrainPerStep, ImpactScratch);
            float ratio = SiegeCatalog.CollateralRatio;
            float radius = SiegeCatalog.CollateralRadius;
            int reach = Mathf.CeilToInt(radius);
            float r2 = radius * radius;
            HomeGridMap map = HomeGridService.BoundMap(state);
            foreach (CombatSiegeImpact im in ImpactScratch)
            {
                CollateralImpacts++;
                float dmg = im.Damage * ratio;
                if (dmg <= 0f)
                {
                    continue;
                }
                int amount = Math.Max(1, Mathf.RoundToInt(dmg));
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        if (dx * dx + dy * dy > r2)
                        {
                            continue;
                        }
                        var cell = new GridCell(im.Cell.x + dx, im.Cell.y + dy);
                        if (BeltNetworkService.HpOf(cell) >= 0)
                        {
                            BeltNetworkService.TryDamage(state, cell, amount, out _);
                            CollateralBeltHits++;
                        }
                        if (PipeNetworkService.HpOf(cell) >= 0)
                        {
                            PipeNetworkService.TryDamage(state, cell, amount, out _);
                            CollateralPipeHits++;
                        }
                        string occ = map?.OccupantAt(cell);
                        if (!string.IsNullOrEmpty(occ))
                        {
                            DamageSite(state, occ, dmg);
                        }
                    }
                }
            }
        }

        /// <summary>施工中的虚影挨溅射：累计到 最大耐久 × siege.site_hp_fraction 即“施工中被摧毁”（还没开工、没运到材料的虚影不受影响）。</summary>
        private static void DamageSite(CampaignState state, string buildingId, float amount)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || !HomeValleyController.IsPlannedGhost(b) || HomeGridService.IsRelocationGhost(b)
                || (b.ConstructionState != BuildingConstructionState.Building && b.ConstructionDelivered <= 0))
            {
                return;
            }
            SiegeState st = StateOf(state);
            float cap = BuildingOps.MaxDurability(b.BuildingTypeId) * SiegeCatalog.SiteHpFraction;
            SiegeSiteDamageRecord rec = null;
            foreach (SiegeSiteDamageRecord r in st.SiteDamage)
            {
                if (r != null && r.BuildingId == buildingId)
                {
                    rec = r;
                    break;
                }
            }
            if (rec == null)
            {
                rec = new SiegeSiteDamageRecord { BuildingId = buildingId };
                var list = new List<SiegeSiteDamageRecord>(st.SiteDamage) { rec };
                st.SiteDamage = list.ToArray();
            }
            rec.Damage += amount;
            CollateralSiteHits++;
            if (rec.Damage < cap)
            {
                return;
            }
            var keep = new List<SiegeSiteDamageRecord>(st.SiteDamage.Length);
            foreach (SiegeSiteDamageRecord r in st.SiteDamage)
            {
                if (r != null && r.BuildingId != buildingId)
                {
                    keep.Add(r);
                }
            }
            st.SiteDamage = keep.ToArray();
            string name = BuildingOps.NameOf(b);
            if (HomeValleyConstruction.OnSiteDestroyed(state, buildingId))
            {
                NotificationCenter.Post("building_destroyed", GameText.Format("siege.notify.collateral_site", name), new Vector3(b.Position.x, 0f, b.Position.y));
            }
        }
    }
}
