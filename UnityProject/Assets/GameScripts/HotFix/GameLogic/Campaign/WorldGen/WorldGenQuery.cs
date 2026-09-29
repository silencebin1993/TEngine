using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG3-GEN-01：一个世界（星球表面）的生成上下文——（种子, 生成器版本, 世界设置, 核心）解析一次后的全部结果：
    /// 规划层（领地、危害带、白潮滩头预留区、河流、矿带、起始区保证）、地形来源（内核参数 + 形状）、点位缓存。
    /// 规划层不进存档，读档时按种子重算（FGR-GEN-020）。由 <see cref="WorldGenService.ContextFor"/> 按战役缓存。
    /// </summary>
    public sealed class WorldGenContext
    {
        public int Seed { get; internal set; }
        public int Version { get; internal set; }
        public WorldGenVersion Row { get; internal set; }
        public WorldSettings Settings { get; internal set; }
        public GridCell Core { get; internal set; }
        public WorldPlan Plan { get; internal set; }
        public WorldTerrainSource Source { get; internal set; }
        internal readonly Dictionary<long, (WorldFeature relic, WorldFeature nest)> FeatureCache = new Dictionary<long, (WorldFeature, WorldFeature)>();

        /// <summary>这个世界的全部生成身份（自检 / 存档卡）。</summary>
        public string Identity => string.Concat(Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "|v", Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "|", Settings?.Id, "|", Core.ToString());

        /// <summary>组装：规划层 → 起始区保证（写保证点）→ 地形来源。纯函数。</summary>
        public static WorldGenContext Build(int seed, int version, WorldSettings settings, GridCell core, int chunkSize)
        {
            WorldGenVersion row = WorldGenContent.Version(version);
            WorldPlan plan = WorldPlan.Compute(seed, row, settings, core.X, core.Y);
            StartZoneGuarantee.Resolve(plan, row, settings, core, chunkSize);
            var source = new WorldTerrainSource(seed, version, settings, WorldGenContent.Surface(WorldGenContent.EarthSurfaceId), core, chunkSize, plan);
            return new WorldGenContext
            {
                Seed = seed,
                Version = version,
                Row = row,
                Settings = settings,
                Core = core,
                Plan = plan,
                Source = source,
            };
        }
    }

    /// <summary>
    /// FG3-GEN-01：世界生成器的对外查询接口（供 FG3-LOG-01 建造、FG6 突袭、FG8 远征 / 领地、地图界面等下游 Story 对接）。
    /// 全部是只读查询；“生成结果”是（种子, 版本, 设置, 核心）的纯函数，“当前地形”另外叠加了玩家的修改（区块差异）。
    ///
    /// | 查询 | 含义 |
    /// |---|---|
    /// | <see cref="Seed"/> / <see cref="Version"/> / <see cref="Settings"/> | 世界身份 |
    /// | <see cref="Plan"/> | 区域规划层：领地（方向、中心、半径、危害带）、白潮滩头预留区、河流、矿带 |
    /// | <see cref="StartReport"/> | 起始区四级保证的校验结果（每项是否自然满足、是否局部重生成、资源点位置） |
    /// | <see cref="TerritoryAt"/> / <see cref="HazardAt"/> | 某格所在的领地 / 危害带 |
    /// | <see cref="GeneratedTerrainAt"/> | 某格“生成出来的”地形与污染（不含玩家修改；任何区块、不必已加载） |
    /// | <see cref="CurrentTerrainAt"/> | 某格当前的地形与污染（含玩家修改，经家园格网） |
    /// | <see cref="IsWalkableGenerated"/> | 地面单位能不能走（悬崖 / 水面不能） |
    /// | <see cref="FeaturesIn"/> / <see cref="HomeZoneNests"/> | 遗迹点 / 侦察巢 |
    /// | <see cref="ReliefHeight"/> | 地貌起伏高度（只是表现：可走的地面恒为 0） |
    /// </summary>
    public static class WorldGenQuery
    {
        public static WorldGenContext Context(CampaignState state) => WorldGenService.ContextFor(state);

        public static int Seed(CampaignState state) => state?.World?.WorldSeed ?? 0;
        public static int Version(CampaignState state) => state?.World?.GeneratorVersion ?? 0;
        public static WorldSettings Settings(CampaignState state) => WorldGenService.SettingsFor(state);
        public static WorldPlan Plan(CampaignState state) => WorldGenService.PlanFor(state);
        public static StartGuaranteeReport StartReport(CampaignState state) => WorldGenService.PlanFor(state)?.StartReport;

        public static PlannedTerritory TerritoryAt(CampaignState state, int x, int y) => Plan(state)?.TerritoryAt(x, y);
        public static PlannedTerritory HazardAt(CampaignState state, int x, int y) => Plan(state)?.HazardAt(x, y);

        /// <summary>生成器在这一格给出的地形与污染（纯函数，不含玩家修改；原型地形的旧存档返回 false）。</summary>
        public static bool GeneratedTerrainAt(CampaignState state, int x, int y, out byte terrain, out byte pollution)
        {
            terrain = 0;
            pollution = 0;
            WorldGenContext ctx = Context(state);
            if (ctx == null)
            {
                return false;
            }
            ctx.Source.Sample(x, y, out terrain, out pollution);
            return true;
        }

        /// <summary>这一格当前的地形与污染（含玩家修改）。区块没加载时同步生成（约 0.1 毫秒 / 块），调用方避免在每帧大范围调用。</summary>
        public static void CurrentTerrainAt(CampaignState state, int x, int y, out byte terrain, out byte pollution)
        {
            HomeGridMap map = HomeGridService.MapFor(state);
            var cell = new GridCell(x, y);
            terrain = map.GetTerrain(cell);
            pollution = map.GetPollution(cell);
        }

        public static bool IsWalkableGenerated(CampaignState state, int x, int y)
        {
            if (!GeneratedTerrainAt(state, x, y, out byte t, out _))
            {
                return false;
            }
            WorldGenContext ctx = Context(state);
            return t != ctx.Source.Params.CodeCliff && t != ctx.Source.Params.CodeWater;
        }

        public static void FeaturesIn(CampaignState state, int minX, int minY, int maxX, int maxY, List<WorldFeature> into) =>
            WorldFeatures.Query(Context(state), minX, minY, maxX, maxY, into);

        public static List<WorldFeature> HomeZoneNests(CampaignState state) => WorldFeatures.HomeZoneNests(Context(state));

        /// <summary>地貌起伏高度（米，只是表现）：可走的格 = 0；悬崖 / 水面按起伏规则的格心高度。玩法（寻路、放置、拾取）一律按 0 高度平面。</summary>
        public static float ReliefHeight(CampaignState state, int x, int y)
        {
            WorldGenContext ctx = Context(state);
            if (ctx == null)
            {
                return 0f;
            }
            CurrentTerrainAt(state, x, y, out byte t, out _);
            ReliefParams q = Regions.WorldTerrainOverlay.ReliefParamsFor(ctx.Source.Params.SurfaceSeed, GridContent.TuningInt("grid.chunk_size"), 0, 0);
            if (t == q.CodeCliff)
            {
                return JobBuildRelief.CliffHeight(in q, 2 * x, 2 * y) + q.CliffHeight * 0.25f;
            }
            return t == q.CodeWater ? -q.WaterDepth : 0f;
        }
    }
}
