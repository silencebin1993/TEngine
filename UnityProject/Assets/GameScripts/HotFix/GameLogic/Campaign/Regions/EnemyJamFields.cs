using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-DATA-01（FG-GAP-049）：干扰机按职能展开的信号干扰场——任何地点，读法为 jam_field 的活着的敌人，以自身为圆心、
    /// 表里 effectRange 为半径拒绝接入请求、让场内机器断链（与破碎都市监听节点同一判据出口 <see cref="Signal.SignalLinkService.IsJammedAt"/>）。
    /// 例外：场地声明“干扰场由设施提供”（<see cref="IEnemySiteContext.AnchoredJamField"/>，破碎都市的监听节点，Demo 规则）时不另外投射。
    ///
    /// 性能：干扰源列表按“敌人记录数组被替换 / 表版本变化”重建（O(记录数)，只在播种 / 召唤 / 读档时发生）；
    /// 每次查询 O(该地点的干扰源数)（个位数），不随敌人总数增长。位置的真相在战斗内核（地点已载入时读内核插值位置）。
    /// </summary>
    public static class EnemyJamFields
    {
        private static RegionEnemyRecord[] _cachedArray;
        private static int _cachedRevision = int.MinValue;
        private static readonly Dictionary<string, List<(RegionEnemyRecord rec, float radius)>> Sources =
            new Dictionary<string, List<(RegionEnemyRecord, float)>>();

        /// <summary>某地点某坐标是否被干扰机的干扰场覆盖。</summary>
        public static bool IsJammed(CampaignState state, string regionId, Vector2 position)
        {
            List<(RegionEnemyRecord rec, float radius)> list = SourcesOf(state, regionId);
            if (list == null || list.Count == 0 || RegionEnemySites.For(regionId).AnchoredJamField)
            {
                return false;
            }
            CombatSite site = CombatSites.Get(regionId);
            for (int i = 0; i < list.Count; i++)
            {
                (RegionEnemyRecord rec, float radius) = list[i];
                if (!TryCenter(site, rec, out Vector2 at))
                {
                    continue;
                }
                if ((position - at).sqrMagnitude <= radius * radius)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>该地点当前活着的干扰源（实例 ID、圆心、半径），供表现层画干扰范围（O(干扰源数)）。</summary>
        public static void CollectActive(CampaignState state, string regionId, List<(string id, Vector2 at, float radius)> into)
        {
            into.Clear();
            List<(RegionEnemyRecord rec, float radius)> list = SourcesOf(state, regionId);
            if (list == null || RegionEnemySites.For(regionId).AnchoredJamField)
            {
                return;
            }
            CombatSite site = CombatSites.Get(regionId);
            foreach ((RegionEnemyRecord rec, float radius) in list)
            {
                if (TryCenter(site, rec, out Vector2 at))
                {
                    into.Add((rec.EnemyInstanceId, at, radius));
                }
            }
        }

        private static bool TryCenter(CombatSite site, RegionEnemyRecord rec, out Vector2 at)
        {
            at = rec.Position;
            if (!rec.IsAlive)
            {
                return false;
            }
            if (site != null && site.TryGetEnemyPosition(rec.EnemyInstanceId, out Vector2 kernelAt))
            {
                at = kernelAt;
            }
            return true;
        }

        private static List<(RegionEnemyRecord rec, float radius)> SourcesOf(CampaignState state, string regionId)
        {
            if (state?.RegionEnemies == null || regionId == null)
            {
                return null;
            }
            int rev = FgContentTables.Revision;
            if (!ReferenceEquals(state.RegionEnemies, _cachedArray) || rev != _cachedRevision)
            {
                Sources.Clear();
                foreach (RegionEnemyRecord e in state.RegionEnemies)
                {
                    if (e == null || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) || row.Behavior != EnemyDefs.BehaviorJamField
                        || !(row.EffectRange > 0f))
                    {
                        continue;
                    }
                    if (!Sources.TryGetValue(e.RegionId ?? string.Empty, out List<(RegionEnemyRecord, float)> l))
                    {
                        l = new List<(RegionEnemyRecord, float)>();
                        Sources[e.RegionId ?? string.Empty] = l;
                    }
                    l.Add((e, row.EffectRange));
                }
                _cachedArray = state.RegionEnemies;
                _cachedRevision = rev;
            }
            return Sources.TryGetValue(regionId, out List<(RegionEnemyRecord rec, float radius)> found) ? found : null;
        }
    }
}
