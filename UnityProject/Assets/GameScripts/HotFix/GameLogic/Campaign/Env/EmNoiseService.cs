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
    /// FG9-FAC-01（FG09 第 4 章“危害：电磁噪声”，第 13 节 -30%；承接 DEBT-FG7ENV05-01 的静默一份）：静默领地里的电磁噪声区与噪声发射器。
    ///
    /// - 噪声区 = 规划层里属于“危害 = 电磁噪声”的领地的危害区（FG8-GEN-02 按种子放置，B25 不依赖固定坐标）。区内信号覆盖源的有效半径 × hazard.emi_noise.coverage_mult：
    ///   单点采样（<see cref="Signal.SignalCoverageService.Sample"/>）与内核逐机器评估（<see cref="CombatSite.EvaluateCoverage(IReadOnlyList{float3}, List{ValueTuple{int,bool}}, IReadOnlyList{float3}, float)"/>）同一口径。
    /// - 每个噪声区中心有一座噪声发射器：家园（星球表面）战斗内核里的敌方结构单位（实例化渲染，可被编队攻击 / 炮塔 / 直控打），耐久随内核快照进存档；
    ///   被摧毁 → 记进 <see cref="FactionState.NoiseEmittersDestroyed"/>（唯一写入口 <see cref="OnEmitterDestroyed"/>），这一片噪声消失、之后不再生成。
    /// - 进入一片还生效的噪声区（信号所在的机器）时提示一次（同一片只一次）。
    /// 开销：区数个位数；采样 O(生效区数)；发射器对账只在家园内核打开 / 读档时；提示检查每游戏秒一次 O(生效区数)。
    /// </summary>
    public static class EmNoiseService
    {
        public const string HazardId = "emi_noise";
        public const string KeyPrefix = "emi:";

        public static float CoverageMult => Mathf.Clamp(Tuning("hazard.emi_noise.coverage_mult", 0.7f), 0.05f, 1f);

        public static int DestroyedCount { get; private set; }
        public static int Revision { get; private set; } = 1;

        private static WorldPlan _plan;
        private static int _builtRevision = int.MinValue;
        private static readonly List<PlannedHazardZone> ZoneList = new List<PlannedHazardZone>(8);
        private static readonly List<float3> Active = new List<float3>(8);
        private static readonly List<string> ActiveIds = new List<string>(8);

        public static void ResetForTests()
        {
            _plan = null;
            _builtRevision = int.MinValue;
            DestroyedCount = 0;
            Revision++;
        }

        public static string ZoneId(PlannedHazardZone z) => z == null ? string.Empty : z.TerritoryId + "#" + z.Index.ToString();

        public static string EmitterKey(string zoneId) => KeyPrefix + zoneId;

        public static bool IsEmitterKey(string key) => key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>全部噪声区（不管发射器在不在）。</summary>
        public static IReadOnlyList<PlannedHazardZone> Zones(CampaignState s)
        {
            Ensure(s);
            return ZoneList;
        }

        /// <summary>还生效的噪声区（发射器没被摧毁）的圆（x, y, 半径；格）。</summary>
        public static IReadOnlyList<float3> ActiveCircles(CampaignState s)
        {
            Ensure(s);
            return Active;
        }

        public static IReadOnlyList<string> ActiveZoneIds(CampaignState s)
        {
            Ensure(s);
            return ActiveIds;
        }

        public static bool IsDestroyed(CampaignState s, string zoneId)
        {
            string[] arr = s?.Factions?.NoiseEmittersDestroyed;
            return arr != null && Array.IndexOf(arr, zoneId) >= 0;
        }

        /// <summary>某个星球位置的覆盖倍率：在生效的噪声区里 = <see cref="CoverageMult"/>，否则 1。O(生效区数)。</summary>
        public static float CoverageFactorAt(CampaignState s, Vector2 pos)
        {
            if (s == null)
            {
                return 1f;
            }
            Ensure(s);
            for (int i = 0; i < Active.Count; i++)
            {
                float3 c = Active[i];
                float dx = pos.x - c.x;
                float dy = pos.y - c.y;
                if (dx * dx + dy * dy <= c.z * c.z)
                {
                    return CoverageMult;
                }
            }
            return 1f;
        }

        public static string ZoneAt(CampaignState s, Vector2 pos)
        {
            Ensure(s);
            for (int i = 0; i < Active.Count; i++)
            {
                float3 c = Active[i];
                float dx = pos.x - c.x;
                float dy = pos.y - c.y;
                if (dx * dx + dy * dy <= c.z * c.z)
                {
                    return ActiveIds[i];
                }
            }
            return null;
        }

        private static void Ensure(CampaignState s)
        {
            WorldPlan plan = s != null ? WorldGenService.PlanFor(s) : null;
            int rev = Revision * 31 + (s?.Factions?.NoiseEmittersDestroyed?.Length ?? 0);
            if (ReferenceEquals(plan, _plan) && rev == _builtRevision)
            {
                return;
            }
            _plan = plan;
            _builtRevision = rev;
            ZoneList.Clear();
            Active.Clear();
            ActiveIds.Clear();
            if (plan == null)
            {
                return;
            }
            foreach (PlannedHazardZone z in plan.HazardZones)
            {
                Hazard h = HomeExplorationService.HazardForTerritory(z.TerritoryId);
                if (h == null || h.Id != HazardId)
                {
                    continue;
                }
                ZoneList.Add(z);
                string id = ZoneId(z);
                if (!IsDestroyed(s, id))
                {
                    Active.Add(new float3(z.CenterX, z.CenterY, z.Radius));
                    ActiveIds.Add(id);
                }
            }
        }

        // ── 噪声发射器（家园 / 星球战斗内核里的敌方结构单位）──

        /// <summary>家园战斗内核打开 / 读档后对账：没被摧毁的噪声区补上发射器单位，已摧毁的移除。O(噪声区数)。</summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            int spawned = 0;
            float hp = Tuning("hazard.emi_noise.emitter_hp", 120f) * Defense.DifficultyService.EnemyHealthMul(s);
            float radius = Tuning("hazard.emi_noise.emitter_radius", 1.2f);
            foreach (PlannedHazardZone z in Zones(s))
            {
                string id = ZoneId(z);
                string key = EmitterKey(id);
                if (IsDestroyed(s, id))
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
                    Position = new Vector2(z.CenterX, z.CenterY),
                    Health = hp,
                    MaxHealth = hp,
                    IsAlive = true,
                };
                site.SpawnEnemy(rec, new CombatSpawn
                {
                    Kind = CombatUnitKind.Structure,
                    Faction = CombatFaction.Hostile,
                    Behavior = CombatBehavior.None,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Report | CombatUnitFlags.Instanced,
                    Radius = radius,
                    Weapon = -1,
                    BehaviorProfile = -1,
                    Priority = 1,
                    ArmorHalfAngleDeg = 90f,
                    ArmorFacing = new float2(0f, -1f),
                    Home = new double2(z.CenterX, z.CenterY),
                });
                spawned++;
            }
            return spawned;
        }

        /// <summary>发射器被打空（家园战斗规则的具名目标阵亡回调）：这一片噪声消失。幂等。</summary>
        public static bool OnEmitterDestroyed(CampaignState s, string key)
        {
            if (s == null || !IsEmitterKey(key))
            {
                return false;
            }
            string id = key.Substring(KeyPrefix.Length);
            if (IsDestroyed(s, id))
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<string>(s.Factions.NoiseEmittersDestroyed) { id };
            list.Sort(StringComparer.Ordinal);
            s.Factions.NoiseEmittersDestroyed = list.ToArray();
            DestroyedCount++;
            Revision++;
            Signal.SignalCoverageService.Invalidate();
            Vector2 at = Vector2.zero;
            foreach (PlannedHazardZone z in Zones(s))
            {
                if (ZoneId(z) == id)
                {
                    at = new Vector2(z.CenterX, z.CenterY);
                }
            }
            NotificationCenter.Post("emi_noise", GameText.Format("hazard.emi_noise.notify.destroyed",
                $"({Mathf.RoundToInt(at.x)}, {Mathf.RoundToInt(at.y)})"), new Vector3(at.x, 0f, at.y));
            GuidanceHooks.Raise("silent.first_emitter_destroyed");
            Log.Info($"[EmNoiseService] 噪声发射器 {id} 被摧毁，这一片噪声消失。");
            return true;
        }

        // ── 进入噪声区提示（信号所在的机器；每游戏秒一次）──

        public static int Notices { get; private set; }

        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            int id = Signal.SignalUplinkService.CurrentMachine(s);
            if (id == 0 || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || m.RegionId != HomeValleyLayout.RegionId)
            {
                return;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
            string zone = ZoneAt(s, pos);
            if (zone == null || Array.IndexOf(s.Factions.NoiseZonesNoticed, zone) >= 0)
            {
                return;
            }
            var list = new List<string>(s.Factions.NoiseZonesNoticed) { zone };
            list.Sort(StringComparer.Ordinal);
            s.Factions.NoiseZonesNoticed = list.ToArray();
            Notices++;
            NotificationCenter.Post("emi_noise", GameText.Get("hazard.emi_noise.notify.first"), new Vector3(pos.x, 0f, pos.y));
            GuidanceHooks.Raise("silent.first_noise");
        }

        /// <summary>悬停说明里“效果”这一行（电磁噪声已生效：写生效 / 已清除，替代“只作标记”）。</summary>
        public static string HoverLine(CampaignState s, int x, int y)
        {
            Ensure(s);
            foreach (PlannedHazardZone z in ZoneList)
            {
                long dx = x - z.CenterX;
                long dy = y - z.CenterY;
                if (dx * dx + dy * dy <= (long)z.Radius * z.Radius)
                {
                    return IsDestroyed(s, ZoneId(z))
                        ? GameText.Get("hazard.emi_noise.cleared")
                        : GameText.Format("hazard.emi_noise.active", Mathf.RoundToInt((1f - CoverageMult) * 100f).ToString());
                }
            }
            return GameText.Format("hazard.emi_noise.active", Mathf.RoundToInt((1f - CoverageMult) * 100f).ToString());
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
