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
        internal readonly Dictionary<long, WorldFeature[]> FeatureCache = new Dictionary<long, WorldFeature[]>();

        /// <summary>这个世界的全部生成身份（自检 / 存档卡）。</summary>
        public string Identity => string.Concat(Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "|v", Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "|", Settings?.Id, "|", Core.ToString());

        /// <summary>
        /// FG15-QA-03（生成器 v5 起，FGR-GEN-090 第 2 条“校验失败时对失败项做确定性的局部重生成并写日志”，DEBT-FG8GEN02-09）：
        /// 非 Demo 锚点（两个室内入口、洗礼平台、白潮滩头）逐个做与存档无关的粗粒度连通性判定（<see cref="TerritoryPlanner.EscapesLocally"/>）；
        /// 被地形围住的只对这一个锚点按子种子（种子, 锚点序号, 次数）换下一个候选、整份规划按同样的输入重算（种子的纯函数，与读档先后、玩家修改无关），
        /// 直到都通过或该锚点的 planMaxAttempts 次候选用完（用完 = 回到原位置、写进 <see cref="WorldPlan.EscapeFailed"/> 与错误日志，运行时可达校验写原因、不软锁）。
        /// 不需要换候选时（种子测试集 50 个种子 × 世界设置组合全部如此）结果与 v4 逐格相同，只多几毫秒判定。
        /// </summary>
        private static void ResolveEscapes(int seed, int version, WorldGenVersion row, WorldSettings settings, GridCell core, int chunkSize, ref WorldPlan plan, ref WorldTerrainSource source)
        {
            int maxAttempts = Math.Max(1, row.PlanMaxAttempts);
            var first = new Dictionary<string, int>(StringComparer.Ordinal);
            var failed = new List<string>();
            var log = new List<string>();
            int checkedCount = 0;
            // 每一轮有改动 = 某个锚点的候选序号只增不减（上限 maxAttempts）或它进了 failed，所以最多 锚点数 ×（maxAttempts + 1）轮就收敛；
            // 上限按这个取，循环总是以“最后一份规划全部判过、没有改动”结束（只按 maxAttempts 取时，后面的锚点被前面的换位挤进围圈会漏判）。
            int escapeAnchors = 0;
            foreach (PlannedAnchor a in plan.Anchors)
            {
                escapeAnchors += TerritoryPlanner.NeedsEscapeCheck(a) ? 1 : 0;
            }
            int passCap = (escapeAnchors + 1) * (maxAttempts + 1);
            for (int pass = 0; pass <= passCap; pass++)
            {
                bool changed = false;
                checkedCount = 0;
                foreach (PlannedAnchor a in plan.Anchors)
                {
                    if (!TerritoryPlanner.NeedsEscapeCheck(a) || failed.Contains(a.Id))
                    {
                        continue;
                    }
                    checkedCount++;
                    if (TerritoryPlanner.EscapesLocally(source, a, out int reached))
                    {
                        continue;
                    }
                    int next = a.Attempts + 1;
                    if (a.Fallback || next >= maxAttempts)
                    {
                        failed.Add(a.Id);
                        changed |= first.Remove(a.Id);
                        log.Add($"锚点 {a.Id}：{maxAttempts} 次子种子候选都被地形围住（最后一次入口 ({a.EntryX},{a.EntryY}) 漫水 {reached} 格），回到原位置，由运行时可达校验写原因（FGR-GEN-090）"); // loc-ignore: 开发者自检 / 诊断文本
                        continue;
                    }
                    first[a.Id] = next;
                    changed = true;
                    log.Add($"锚点 {a.Id}：第 {a.Attempts + 1} 次候选原点 ({a.OriginX},{a.OriginY}) 入口 ({a.EntryX},{a.EntryY}) 被地形围住（漫水 {reached} 格碰不到判定框边），按子种子换第 {next + 1} 次候选（粗粒度连通性判定，FGR-GEN-090 局部重生成）"); // loc-ignore: 开发者自检 / 诊断文本
                }
                if (!changed)
                {
                    break;
                }
                plan = WorldPlan.Compute(seed, row, settings, core.X, core.Y, first);
                if (settings == null || !settings.NoStartGuarantee)
                {
                    StartZoneGuarantee.Resolve(plan, row, settings, core, chunkSize);
                }
                source = new WorldTerrainSource(seed, version, settings, WorldGenContent.Surface(WorldGenContent.EarthSurfaceId), core, chunkSize, plan);
            }
            plan.EscapeChecked = checkedCount + failed.Count;
            foreach (KeyValuePair<string, int> kv in first)
            {
                plan.EscapeRegen[kv.Key] = kv.Value;
            }
            plan.EscapeFailed.AddRange(failed);
            plan.Log.AddRange(log);
            foreach (string id in failed)
            {
                TEngine.Log.Warning($"[WorldGen] 种子 {seed} v{version}：锚点 {id} 的全部候选都被地形围住（粗粒度连通性判定），保留原位置"); // loc-ignore: 开发者自检 / 诊断文本
            }
        }

        /// <summary>组装：规划层 → 起始区保证（写保证点）→ 地形来源 →（v5 起）锚点粗粒度连通性判定与换候选。纯函数。</summary>
        public static WorldGenContext Build(int seed, int version, WorldSettings settings, GridCell core, int chunkSize)
        {
            WorldGenVersion row = WorldGenContent.Version(version);
            WorldPlan plan = WorldPlan.Compute(seed, row, settings, core.X, core.Y);
            if (settings == null || !settings.NoStartGuarantee)
            {
                StartZoneGuarantee.Resolve(plan, row, settings, core, chunkSize); // FG14-SBX-02：沙盒“关闭保证”不放资源保证点、不出保证报告
            }
            var source = new WorldTerrainSource(seed, version, settings, WorldGenContent.Surface(WorldGenContent.EarthSurfaceId), core, chunkSize, plan);
            if (version >= TerritoryPlanner.ReachRegenFromVersion && plan.Anchors.Count > 0)
            {
                ResolveEscapes(seed, version, row, settings, core, chunkSize, ref plan, ref source);
            }
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
