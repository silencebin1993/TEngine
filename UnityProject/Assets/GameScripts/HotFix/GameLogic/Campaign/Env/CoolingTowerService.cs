using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG11-REG-01（FG09 第 7 章“场地中有冷却塔遗址，站在运转中的冷却塔附近可以恢复热量上限”；承接 DEBT-FG11FAC01-02）：热寂数据城的冷却塔遗址。
    ///
    /// - 位置只由规划层决定（B25 不依赖固定坐标）：超频领地里每片高温辐射区的中心一座，外圈高温辐射带中线上朝归还核心方向 ± cooling.tower.belt_spread_deg 各一座
    ///   （cooling.tower.belt_count 座；让玩家穿过辐射带时有落脚点）。与噪声发射器同一做法：不改地形、不进生成输入，旧 v3 世界同样有。
    /// - 运转中的塔 = 星球战斗内核里的中立结构单位（实例化渲染；被流弹、范围伤害、燃迹真明火打空就毁——FG-EMG 真明火不认敌我），耐久随内核快照进存档；
    ///   被摧毁 → 记进 <see cref="FactionState.CoolingTowersDestroyed"/>（唯一写入口 <see cref="OnTowerDestroyed"/>），之后不再生成、不再恢复热量上限、地图上的范围圈消失。
    /// - 效果：运转中的塔 cooling.tower.radius 格内，高温辐射的热量上限倍率不生效（散热 -20% 照常）——星球战斗内核逐单位结算（<see cref="CombatSite.SetCoolingZones"/>，派生量不进快照，
    ///   内核打开 / 读档 / 塔被摧毁时重写；敌我同一规则）。接入 HUD 的热量上限读内核同一口径（<see cref="HudNote"/>）。
    /// 开销：塔数个位数；内核只在辐射区内的积热判定时 O(塔数)；对账只在内核打开 / 读档 / 塔被摧毁时。
    /// </summary>
    public static class CoolingTowerService
    {
        public const string KeyPrefix = "cool:";

        public static float Radius => Mathf.Max(1f, Tuning("cooling.tower.radius", 14f));

        public static int DestroyedCount { get; private set; }
        public static int Revision { get; private set; } = 1;

        public struct Tower
        {
            public string Id;
            public string TerritoryId;
            public int X;
            public int Y;
        }

        private static WorldPlan _plan;
        private static int _builtRevision = int.MinValue;
        private static readonly List<Tower> All = new List<Tower>(8);
        private static readonly List<float3> Active = new List<float3>(8);

        public static void ResetForTests()
        {
            _plan = null;
            _builtRevision = int.MinValue;
            DestroyedCount = 0;
            Revision++;
        }

        public static string KeyOf(string towerId) => KeyPrefix + towerId;

        public static bool IsTowerKey(string key) => key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>全部冷却塔遗址（不管毁没毁）。</summary>
        public static IReadOnlyList<Tower> Towers(CampaignState s)
        {
            Ensure(s);
            return All;
        }

        /// <summary>运转中的塔圈（x, y, 半径；格）。</summary>
        public static IReadOnlyList<float3> ActiveCircles(CampaignState s)
        {
            Ensure(s);
            return Active;
        }

        public static bool IsDestroyed(CampaignState s, string towerId)
        {
            string[] arr = s?.Factions?.CoolingTowersDestroyed;
            return arr != null && Array.IndexOf(arr, towerId) >= 0;
        }

        /// <summary>某个位置在不在运转中的冷却塔圈里。O(塔数)。</summary>
        public static bool InActiveTower(CampaignState s, Vector2 pos)
        {
            Ensure(s);
            for (int i = 0; i < Active.Count; i++)
            {
                float3 c = Active[i];
                float dx = pos.x - c.x;
                float dy = pos.y - c.y;
                if (dx * dx + dy * dy <= c.z * c.z)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsRadiationTerritory(PlannedTerritory t)
        {
            if (t == null || !t.IsFaction)
            {
                return false;
            }
            Hazard belt = HomeExplorationService.HazardForBelt(t.HazardKind);
            Hazard zone = HomeExplorationService.HazardForTerritory(t.Id);
            return belt?.Id == HeatRadiationService.HazardId || zone?.Id == HeatRadiationService.HazardId;
        }

        private static void Ensure(CampaignState s)
        {
            WorldPlan plan = s != null ? WorldGenService.PlanFor(s) : null;
            int rev = Revision * 31 + (s?.Factions?.CoolingTowersDestroyed?.Length ?? 0);
            if (ReferenceEquals(plan, _plan) && rev == _builtRevision)
            {
                return;
            }
            _plan = plan;
            _builtRevision = rev;
            All.Clear();
            Active.Clear();
            if (plan == null)
            {
                return;
            }
            int beltCount = Mathf.Max(0, Mathf.RoundToInt(Tuning("cooling.tower.belt_count", 2f)));
            int spread = Mathf.RoundToInt(Tuning("cooling.tower.belt_spread_deg", 25f));
            foreach (PlannedTerritory t in plan.Territories)
            {
                if (!IsRadiationTerritory(t))
                {
                    continue;
                }
                if (plan.HasTemplates && plan.TemplateFor(t.Id) != null)
                {
                    foreach (PlannedHazardZone z in plan.HazardZones)
                    {
                        if (z != null && z.TerritoryId == t.Id)
                        {
                            All.Add(new Tower { Id = t.Id + "#" + z.Index.ToString(), TerritoryId = t.Id, X = z.CenterX, Y = z.CenterY });
                        }
                    }
                }
                if (t.HazardWidth > 0)
                {
                    int toCore = WorldPlan.RadialAngle(plan.CoreX - t.CenterX, plan.CoreY - t.CenterY);
                    long mid = t.Radius + t.HazardWidth / 2;
                    for (int k = 0; k < beltCount; k++)
                    {
                        // 朝核心方向左右交替展开：k = 0 → -spread，1 → +spread，2 → -2·spread……（只由规划层决定）。
                        int side = k % 2 == 0 ? -1 : 1;
                        int angle = toCore + side * spread * (k / 2 + 1);
                        int x = t.CenterX + (int)((mid * WorldPlan.Cos(angle) + 32768) >> 16);
                        int y = t.CenterY + (int)((mid * WorldPlan.Cos(angle - 90) + 32768) >> 16);
                        All.Add(new Tower { Id = t.Id + "#belt" + k.ToString(), TerritoryId = t.Id, X = x, Y = y });
                    }
                }
            }
            float r = Radius;
            foreach (Tower tw in All)
            {
                if (!IsDestroyed(s, tw.Id))
                {
                    Active.Add(new float3(tw.X, tw.Y, r));
                }
            }
        }

        // ── 塔本体（星球战斗内核里的中立结构单位）──

        /// <summary>家园（星球表面）战斗内核打开 / 读档后对账：运转中的塔补上单位、已摧毁的移除，并写入塔圈（派生量）。O(塔数)。返回补了几座。</summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            int spawned = 0;
            float hp = Tuning("cooling.tower.hp", 400f);
            float unitRadius = Tuning("cooling.tower.unit_radius", 2.5f);
            foreach (Tower tw in Towers(s))
            {
                string key = KeyOf(tw.Id);
                if (IsDestroyed(s, tw.Id))
                {
                    site.RemoveEnemy(key);
                    continue;
                }
                if (site.TryGetEnemyUnit(key, out int existing) && site.UnitExists(existing))
                {
                    continue;
                }
                var rec = new RegionEnemyRecord
                {
                    EnemyInstanceId = key,
                    RegionId = HomeValleyLayout.RegionId,
                    EnemyTypeId = key,
                    Position = new Vector2(tw.X, tw.Y),
                    Health = hp,
                    MaxHealth = hp,
                    IsAlive = true,
                };
                site.SpawnEnemy(rec, new CombatSpawn
                {
                    Kind = CombatUnitKind.Structure,
                    Faction = CombatFaction.Neutral,
                    Behavior = CombatBehavior.None,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Report | CombatUnitFlags.Instanced,
                    Radius = unitRadius,
                    Weapon = -1,
                    BehaviorProfile = -1,
                    Priority = 0,
                    ArmorHalfAngleDeg = 90f,
                    ArmorFacing = new float2(0f, -1f),
                    Home = new double2(tw.X, tw.Y),
                });
                spawned++;
            }
            site.SetCoolingZones(ActiveCircles(s));
            return spawned;
        }

        /// <summary>塔被打空（家园战斗规则的具名目标阵亡回调）：这座塔不再恢复热量上限、范围圈消失。幂等。</summary>
        public static bool OnTowerDestroyed(CampaignState s, string key)
        {
            if (s == null || !IsTowerKey(key))
            {
                return false;
            }
            string id = key.Substring(KeyPrefix.Length);
            if (IsDestroyed(s, id))
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<string>(s.Factions.CoolingTowersDestroyed) { id };
            list.Sort(StringComparer.Ordinal);
            s.Factions.CoolingTowersDestroyed = list.ToArray();
            DestroyedCount++;
            Revision++;
            CombatSites.Get(HomeValleyLayout.RegionId)?.SetCoolingZones(ActiveCircles(s));
            Vector2 at = Vector2.zero;
            foreach (Tower tw in Towers(s))
            {
                if (tw.Id == id)
                {
                    at = new Vector2(tw.X, tw.Y);
                }
            }
            NotificationCenter.Post("heat_radiation", GameText.Format("cooling.notify.destroyed",
                $"({Mathf.RoundToInt(at.x)}, {Mathf.RoundToInt(at.y)})"), new Vector3(at.x, 0f, at.y));
            Log.Info($"[CoolingTowerService] 冷却塔遗址 {id} 被摧毁，这一带不再恢复热量上限。");
            return true;
        }

        /// <summary>接入 HUD 热量一栏的说明（空串 = 不在辐射区）：辐射区里写“热量上限 -25%”，在运转中的冷却塔圈里写“已恢复”。</summary>
        public static string HudNote(CampaignState s, Vector2 pos)
        {
            if (s == null || HeatRadiationService.ZoneAt(s, pos) == null)
            {
                return string.Empty;
            }
            return InActiveTower(s, pos)
                ? GameText.Get("cooling.hud.restored")
                : GameText.Format("cooling.hud.radiation", Mathf.RoundToInt((1f - HeatRadiationService.CapMult) * 100f).ToString());
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
