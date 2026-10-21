using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.WorldSim;
using GameLogic.Campaign.WorldGen;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-ACT1-01（承接 DEBT-FG0ARCH01-01 / DEBT-FG8EXP03-06；ADR-NAR-002 第 2 节）：远征地点里的领地驻军据点。唯一写入口（据点记录 kind = site 的登记、
    /// 进场播种、全灭结算的触发）。
    ///
    /// - 登记：新战役（或旧档第一次载入）按 <see cref="SiteGenLayout"/> 的驻军点位，把每处驻军登记成星球据点（<see cref="WorldOutpostSystem.SpawnSiteGarrison"/>，
    ///   星球格 = 锚点周围的真实位置）——计入领地控制度、首领被击败前会被增援重建，战略地图上与领地据点一样显示。幂等（据点 ID = sg.地点.序号）。
    /// - 进场：远征队进入地点时，每处没被摧毁的驻军按据点当前驻军人数（至多 fg.TbSiteGen.unitsMax）播种成地点敌人记录（实例 ID = 据点 ID.d摧毁次数.序号；
    ///   编成与星球迎战同一张 fg.TbTerritoryControl.garrisonUnits），之后随地点存档；被重建的据点下次进场换一组新实例（旧的阵亡记录留作墓碑）。
    /// - 全灭：一处驻军的敌人记录全部阵亡 = 据点被摧毁 → <see cref="TerritoryControlService.RouteSiteGarrison"/>（控制度、通知、掉落在地点里）。
    /// 开销：进场 / 阵亡时 O(驻军数 × 区域敌人记录数)，不在每步里。
    /// </summary>
    public static class SiteGarrisonService
    {
        public const string Prefix = "sg.";

        private static readonly List<string> TypeScratch = new List<string>(16);

        /// <summary>自检读点。</summary>
        public static int SeededUnits { get; private set; }
        public static int RoutedGarrisons { get; private set; }

        public static string OutpostIdOf(string siteId, int index) => Prefix + siteId + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool IsGarrisonInstance(string enemyInstanceId) =>
            enemyInstanceId != null && enemyInstanceId.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>sg.地点.序号 → 地点 ID。</summary>
        public static string SiteOfOutpost(string outpostId)
        {
            if (outpostId == null || !outpostId.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null;
            }
            int dot = outpostId.LastIndexOf('.');
            return dot > Prefix.Length ? outpostId.Substring(Prefix.Length, dot - Prefix.Length) : null;
        }

        /// <summary>敌人实例 ID（sg.地点.序号.d摧毁次数.第几台）→ 据点 ID。</summary>
        public static string OutpostOfInstance(string instanceId)
        {
            if (!IsGarrisonInstance(instanceId))
            {
                return null;
            }
            int d = instanceId.IndexOf(".d", Prefix.Length, StringComparison.Ordinal);
            return d > 0 ? instanceId.Substring(0, d) : null;
        }

        private static string InstancePrefix(OutpostRecord o) => o.OutpostId + ".d" + o.DestroyCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";

        /// <summary>这个地点此刻载入着（玩家的机器在里面）。</summary>
        public static bool SiteBusy(string siteId) => !string.IsNullOrEmpty(siteId) && WorldSimulation.IsSiteLoaded(siteId);

        public static void ResetSessionState()
        {
            SeededUnits = 0;
            RoutedGarrisons = 0;
        }

        // ─────────────────────────────── 登记 ───────────────────────────────

        /// <summary>两个远征地点的驻军据点都登记成星球据点（幂等；v1 / v2 世界没有锚点，什么也不做）。返回本次新登记几处。</summary>
        public static int SeedAll(CampaignState s)
        {
            if (s?.World == null || s.World.SiteGarrisonsSeeded || !WorldGenService.UsesWorldGen(s) || WorldGenService.PlanFor(s) == null)
            {
                return 0;
            }
            int n = 0;
            bool any = false;
            foreach (string site in SiteGenLayout.SiteIds)
            {
                SiteGenContent c = SiteGenLayout.For(s, site);
                if (c == null)
                {
                    continue;
                }
                any = true;
                foreach (SiteGarrisonPoint g in c.Garrisons)
                {
                    if (WorldOutpostSystem.Find(s, g.OutpostId) != null)
                    {
                        continue;
                    }
                    OutpostRecord spawned = WorldOutpostSystem.SpawnSiteGarrison(s, g.OutpostId, c.TerritoryId, g.Tier, g.CellX, g.CellY, c.Row.UnitsPerTier);
                    if (spawned != null)
                    {
                        n++;
                        // 修复轮（第 1 轮审查 P2）：旧档第一次载入时补登记——领地首领已被击败（平定中 / 已收复）的，补登记的驻军同样不再增援（与 Pacify 同一口径）。
                        if (TerritoryControlService.Find(s, c.TerritoryId)?.LeaderDefeated == true)
                        {
                            WorldOutpostSystem.StopReinforcement(s, spawned);
                        }
                    }
                }
            }
            // 旧世界（没有锚点 / 表）也记一次“已处理”，不每次载入都重算；有锚点的世界登记完才记。
            s.World.SiteGarrisonsSeeded = true;
            if (any)
            {
                Log.Info($"[SiteGarrisonService] 种子 {s.World.WorldSeed}：两个远征地点登记领地驻军据点 {n} 处。");
            }
            return n;
        }

        // ─────────────────────────────── 进场播种 ───────────────────────────────

        /// <summary>
        /// 远征队进入地点（新派遣 / 读档续玩）：每处没被摧毁、本轮还没播种过的驻军按当前驻军人数播种成地点敌人记录。返回本次新播种几台。
        /// 读档续玩时这一轮的记录已经在存档里（无论死活），不会重复播种。
        /// </summary>
        public static int SeedOnEnter(CampaignState s, string siteId)
        {
            SiteGenContent c = SiteGenLayout.For(s, siteId);
            if (c == null)
            {
                return 0;
            }
            SeedAll(s);
            int added = 0;
            float ring = Mathf.Max(1f, Tuning("site.garrison.ring_cells", 2.5f));
            foreach (SiteGarrisonPoint g in c.Garrisons)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                if (o == null || o.Destroyed || o.Kind != WorldOutpostSystem.KindSite)
                {
                    continue;
                }
                string prefix = InstancePrefix(o);
                if (HasAnyWithPrefix(s, prefix))
                {
                    continue;
                }
                int count = Mathf.Clamp(o.Garrison, 1, Math.Max(1, c.Row.UnitsMax));
                TerritoryControlService.GarrisonEnemyTypes(o.TerritoryId, count, TypeScratch);
                var list = new List<RegionEnemyRecord>(s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>());
                for (int k = 0; k < TypeScratch.Count; k++)
                {
                    string type = TypeScratch[k];
                    if (!EnemyDefs.TryGetValid(siteId, prefix + k, type, out GameConfig.fg.MechEnemy row))
                    {
                        continue;
                    }
                    float hp = EnemyDefs.SeedHealth(s, row);
                    list.Add(new RegionEnemyRecord
                    {
                        EnemyInstanceId = prefix + k.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        RegionId = siteId,
                        EnemyTypeId = row.Id,
                        Position = SlotPosition(c, g.Local, k, ring),
                        Health = hp,
                        MaxHealth = hp,
                        IsAlive = true,
                        CycleCooldownRemaining = row.CycleSeconds * (0.25f + 0.15f * (k % 4)), // 错开周期，不同步开火 / 标记
                        SecondaryTimer = 0f,
                    });
                    added++;
                }
                s.RegionEnemies = list.ToArray();
            }
            TypeScratch.Clear();
            SeededUnits += added;
            return added;
        }

        /// <summary>第 k 台的位置：据点点位为中心按圈排开（第 0 台在中心，之后每圈 6 台），避开地形障碍。</summary>
        private static Vector2 SlotPosition(SiteGenContent c, Vector2 center, int k, float ring)
        {
            if (k == 0)
            {
                return center;
            }
            int layer = 1 + (k - 1) / 6;
            int idx = (k - 1) % 6;
            for (int turn = 0; turn < 6; turn++)
            {
                float a = (idx + turn) * Mathf.PI / 3f + layer * 0.5f;
                var p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring * layer;
                if (!SiteGenLayout.InObstacle(c, p, 0.6f) && c.InWindow(p, 1f))
                {
                    return p;
                }
            }
            return center;
        }

        private static bool HasAnyWithPrefix(CampaignState s, string prefix)
        {
            foreach (RegionEnemyRecord e in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e != null && e.EnemyInstanceId != null && e.EnemyInstanceId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 阵亡 → 摧毁 ───────────────────────────────

        /// <summary>地点里一台敌人阵亡（两个地点的阵亡结算调用）：它是驻军、且这一轮的驻军全部阵亡 → 据点被摧毁。返回是否摧毁了据点。</summary>
        public static bool OnEnemyKilled(CampaignState s, RegionEnemyRecord e)
        {
            if (s == null || e == null || !IsGarrisonInstance(e.EnemyInstanceId))
            {
                return false;
            }
            OutpostRecord o = WorldOutpostSystem.Find(s, OutpostOfInstance(e.EnemyInstanceId));
            if (o == null || o.Destroyed || o.Kind != WorldOutpostSystem.KindSite)
            {
                return false;
            }
            string prefix = InstancePrefix(o);
            if (!e.EnemyInstanceId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false; // 上一轮（重建前）的残余记录：与当前据点无关
            }
            int alive = 0;
            foreach (RegionEnemyRecord r in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (r != null && r.IsAlive && r.EnemyInstanceId != null && r.EnemyInstanceId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    alive++;
                }
            }
            if (alive > 0)
            {
                // 剩下的驻军人数写回据点（战略地图标签、下次增援从这里接着长；不改结算口径）。
                o.Garrison = Math.Max(1, Math.Min(o.Garrison, alive));
                return false;
            }
            SiteGenContent c = SiteGenLayout.For(s, e.RegionId);
            Vector2 at = c?.GarrisonByOutpost(o.OutpostId)?.Local ?? e.Position;
            if (!TerritoryControlService.RouteSiteGarrison(s, o, e.RegionId, at))
            {
                return false;
            }
            RoutedGarrisons++;
            return true;
        }

        /// <summary>地点里还站着的驻军据点数（出征情报 / 入口提示）。</summary>
        public static int StandingCount(CampaignState s, string siteId)
        {
            SiteGenContent c = SiteGenLayout.For(s, siteId);
            if (c == null)
            {
                return 0;
            }
            int n = 0;
            foreach (SiteGarrisonPoint g in c.Garrisons)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                if (o != null && !o.Destroyed)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 修复轮（第 1 轮审查 P2“出征情报不反映地点驻军”）：出征情报里的一行“领地驻军 N 处（等级 a～b，约 M 台：类型…）”。
        /// 还站着的驻军据点按当前驻军人数算（与进场播种同一口径：1～unitsMax 台，类型按领地编成）；没有生成内容 / 全部摧毁返回空串。O(驻军据点数 × 人数)，只在开面板时调用。
        /// </summary>
        public static string IntelLine(CampaignState s, string siteId)
        {
            SiteGenContent c = SiteGenLayout.For(s, siteId);
            if (c == null)
            {
                return string.Empty;
            }
            int standing = 0;
            int units = 0;
            int tierMin = int.MaxValue;
            int tierMax = 0;
            var types = new List<string>(4);
            foreach (SiteGarrisonPoint g in c.Garrisons)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                if (o == null || o.Destroyed)
                {
                    continue;
                }
                standing++;
                int count = Mathf.Clamp(o.Garrison, 1, Math.Max(1, c.Row.UnitsMax));
                units += count;
                tierMin = Math.Min(tierMin, o.Tier);
                tierMax = Math.Max(tierMax, o.Tier);
                TerritoryControlService.GarrisonEnemyTypes(o.TerritoryId, count, TypeScratch);
                foreach (string t in TypeScratch)
                {
                    if (!types.Contains(t))
                    {
                        types.Add(t);
                    }
                }
            }
            TypeScratch.Clear();
            if (standing == 0)
            {
                return string.Empty;
            }
            var names = new List<string>(types.Count);
            foreach (string t in types)
            {
                names.Add(EnemyDefs.TryGet(t, out GameConfig.fg.MechEnemy row) ? Localization.GameText.Get(row.NameKey) : t);
            }
            string tiers = tierMin == tierMax ? tierMin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : tierMin.ToString(System.Globalization.CultureInfo.InvariantCulture) + "～" + tierMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Localization.GameText.Format("expedition.intel.garrison", standing, tiers, units, string.Join(Localization.GameText.Get("expedition.intel.sep"), names));
        }

        private static float Tuning(string id, float fallback) => Grid.GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
