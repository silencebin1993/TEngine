using System.Collections.Generic;
using GameLogic.Campaign.Combat;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-DATA-01：Demo 两个区域给敌人生成提供的场地信息（<see cref="IEnemySiteContext"/>）——锚点表来自区域布局，反制来自区域记录。
    /// 不含任何按敌人 ID 的行为分支。没有布局的地点（以后的领地 / 室内）返回一个“没有锚点、干扰机自带干扰场”的通用场地。
    /// </summary>
    public static class RegionEnemySites
    {
        private sealed class Site : IEnemySiteContext
        {
            private readonly Dictionary<string, Vector2> _anchors = new Dictionary<string, Vector2>();
            private readonly System.Func<CampaignState, RegionRecord> _region;

            public Site(string regionId, IEnumerable<(string id, Vector2 pos)> anchors, System.Func<CampaignState, RegionRecord> region, bool anchoredJam)
            {
                RegionId = regionId;
                _region = region;
                AnchoredJamField = anchoredJam;
                if (anchors != null)
                {
                    foreach ((string id, Vector2 pos) a in anchors)
                    {
                        _anchors[a.id] = a.pos;
                    }
                }
            }

            public string RegionId { get; }
            public bool AnchoredJamField { get; }
            public string AdaptationIdFor(CampaignState state) => state == null ? null : _region?.Invoke(state)?.AdaptationId;

            public bool TryAnchor(string anchorId, out Vector2 position)
            {
                position = default;
                return anchorId != null && _anchors.TryGetValue(anchorId, out position);
            }
        }

        /// <summary>FG9-FAC-01：室内场地（地下中继大厅）的场地信息——锚点 = fg.TbInteriorPoint 按当前世界落位（<see cref="RelayHallLayout"/>），干扰机自带干扰场。</summary>
        private sealed class InteriorSite : IEnemySiteContext
        {
            public InteriorSite(string regionId)
            {
                RegionId = regionId;
            }

            public string RegionId { get; }
            public bool AnchoredJamField => false;
            public string AdaptationIdFor(CampaignState state) => null;

            public bool TryAnchor(string anchorId, out Vector2 position) => RelayHallLayout.TryPoint(CampaignSession.Current, anchorId, out position);
        }

        private static readonly Dictionary<string, InteriorSite> Interiors = new Dictionary<string, InteriorSite>();
        private static Site _fractured;
        private static Site _foundry;
        /// <summary>没有布局的地点共用的通用场地（无锚点、无反制、干扰机自带干扰场）：按区域缓存，信号干扰判定每次查询不再新建对象。</summary>
        private static readonly Dictionary<string, Site> Generic = new Dictionary<string, Site>();

        private static IEnumerable<(string, Vector2)> Fractured()
        {
            foreach (FracturedCityLayout.Anchor a in FracturedCityLayout.AllAnchors())
            {
                yield return (a.Id, a.Position);
            }
        }

        private static IEnumerable<(string, Vector2)> Foundry()
        {
            foreach (FoundryOutpostLayout.Anchor a in FoundryOutpostLayout.AllAnchors())
            {
                yield return (a.Id, a.Position);
            }
            // FG9-FAC-02：核心区对空阵地与反制 / 狂暴出生点（fg.TbSitePoint）。
            foreach (FoundryOutpostLayout.Anchor a in FoundryOutpostLayout.SitePoints())
            {
                yield return (a.Id, a.Position);
            }
        }

        /// <summary>按区域取场地信息（O(1)，布局锚点首次用到时建一次表）。</summary>
        public static IEnemySiteContext For(string regionId)
        {
            if (regionId == FracturedCityLayout.RegionId)
            {
                // 破碎都市：信号干扰场由监听节点提供（Demo 规则，FracturedCityRegion.IsPositionJammed），干扰机不另投射。
                return _fractured ??= new Site(regionId, Fractured(), FracturedCityRegion.Find, anchoredJam: true);
            }
            if (regionId == FoundryOutpostLayout.RegionId)
            {
                return _foundry ??= new Site(regionId, Foundry(), FoundryOutpostRegion.Find, anchoredJam: false);
            }
            if (regionId != null && RelayHallLayout.IsInteriorWithPoints(regionId))
            {
                if (!Interiors.TryGetValue(regionId, out InteriorSite interior))
                {
                    interior = new InteriorSite(regionId);
                    Interiors[regionId] = interior;
                }
                return interior;
            }
            string key = regionId ?? string.Empty;
            if (!Generic.TryGetValue(key, out Site generic))
            {
                generic = new Site(regionId, null, null, anchoredJam: false);
                Generic[key] = generic;
            }
            return generic;
        }

        /// <summary>按编成表播种某区域某条件的全部敌人（已存在的实例不动）。返回本次新播种条数。O(编成行数)，只在进场 / 状态变化时调用。</summary>
        public static int SeedEncounters(CampaignState state, string regionId, string condition)
        {
            int n = 0;
            foreach (GameConfig.fg.EnemyEncounter enc in Content.EnemyDefs.Encounters(regionId, condition))
            {
                bool existed = FindRecord(state, enc.Id) != null;
                if (SeedEncounter(state, enc) != null && !existed)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>按编成表播种一个实例（实例 ID = 编成行 ID）。编成行 / 锚点 / 敌人行缺失时可见报错并返回 null。</summary>
        public static RegionEnemyRecord SeedInstance(CampaignState state, string regionId, string instanceId)
        {
            if (!Content.EnemyDefs.TryEncounter(instanceId, out GameConfig.fg.EnemyEncounter enc))
            {
                Content.EnemyDefs.EncounterTypeOf(regionId, instanceId); // 只为可见报错（缺编成行）。
                return null;
            }
            return SeedEncounter(state, enc);
        }

        private static RegionEnemyRecord SeedEncounter(CampaignState state, GameConfig.fg.EnemyEncounter enc)
        {
            if (state == null || enc == null)
            {
                return null;
            }
            RegionEnemyRecord existing = FindRecord(state, enc.Id);
            if (existing != null)
            {
                return existing;
            }
            if (!TrySpawnPosition(enc.RegionId, enc.AnchorId, out Vector2 at))
            {
                Content.EnemyDefs.ReportEncounterProblem(enc.RegionId, enc.Id + " @ " + enc.AnchorId);
                return null;
            }
            return Content.EnemyDefs.SeedFromEncounter(state, enc, at);
        }

        private static RegionEnemyRecord FindRecord(CampaignState state, string instanceId)
        {
            if (state?.RegionEnemies == null)
            {
                return null;
            }
            foreach (RegionEnemyRecord e in state.RegionEnemies)
            {
                if (e != null && e.EnemyInstanceId == instanceId)
                {
                    return e;
                }
            }
            return null;
        }

        /// <summary>按编成锚点取出生位置；锚点不存在时返回 false（调用方可见报错、不生成）。</summary>
        public static bool TrySpawnPosition(string regionId, string anchorId, out Vector2 position) =>
            For(regionId).TryAnchor(anchorId, out position);
    }
}
