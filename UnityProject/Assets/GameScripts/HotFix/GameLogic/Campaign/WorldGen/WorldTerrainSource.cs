using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-002、010、050；FGR-ARC-013、014）：世界生成器作为格网的地形来源。
    /// 生成身份 =（世界种子, 生成器版本, 世界设置, 表面, 核心落点）；计算全部在 AOT 内核（<see cref="WorldGenKernel"/>，Burst），
    /// 热更层只组装参数、调度任务、接入结果。同一身份下，工作线程、主线程 Burst、托管三条路径逐字节一致。
    /// </summary>
    public sealed class WorldTerrainSource : IGridTerrainSource
    {
        /// <summary>写进 GridState.TerrainSourceId：地形由世界生成器按 WorldGenState.GeneratorVersion 生成。</summary>
        public const string Id = "worldgen";

        private const uint SurfaceSalt = 0x53524643u; // "SRFC"

        public string SourceId => Id;
        public string SourceKey { get; }
        public string SurfaceId { get; }
        public bool IsInterior { get; }
        public int WidthChunks { get; }
        public int HeightChunks { get; }
        public int CoordLimit { get; }
        public int WorldSeed { get; }
        public int Version { get; }
        public string PresetId { get; }

        public readonly WorldGenParams Params;
        public readonly WorldGenRect[] Rects;
        public readonly WorldGenZone[] Zones;

        /// <summary>v1 写法（整套预设）；等价于按预设解析的 <see cref="WorldSettings"/>。</summary>
        public WorldTerrainSource(int worldSeed, int version, WorldPreset preset, Surface surface, GridCell core, int chunkSize, WorldPlan plan)
            : this(worldSeed, version, preset != null ? WorldSettings.FromPreset(preset, version) : null, surface, core, chunkSize, plan)
        {
        }

        public WorldTerrainSource(int worldSeed, int version, WorldSettings preset, Surface surface, GridCell core, int chunkSize, WorldPlan plan)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }
            WorldGenVersion v = WorldGenContent.Version(version);
            WorldSeed = worldSeed;
            Version = version;
            PresetId = preset.Id;
            SurfaceId = surface.Id;
            IsInterior = WorldGenContent.IsInterior(surface);
            WidthChunks = IsInterior ? surface.WidthChunks : 0;
            HeightChunks = IsInterior ? surface.HeightChunks : 0;
            CoordLimit = GridContent.TuningInt("world.coord_limit");

            int scaleQ = Q(v.FeatureScale);
            int oreBase = Q(v.OreThreshold);
            int abundanceQ = Q(preset.ResourceAbundance);
            int oreT = WorldGenMath.One - (int)(((long)(WorldGenMath.One - oreBase) * abundanceQ) >> 16);
            oreT = Math.Max(WorldGenMath.One / 2, Math.Min(WorldGenMath.One - 1, oreT));
            Params = new WorldGenParams
            {
                Version = version,
                Kind = IsInterior ? WorldSurfaceKind.Interior : WorldSurfaceKind.Planet,
                SurfaceSeed = WorldGenMath.Hash(unchecked((uint)worldSeed), surface.Salt, 0, SurfaceSalt),
                ChunkSize = chunkSize,
                CoreX = core.X,
                CoreY = core.Y,
                InteriorWidth = WidthChunks * chunkSize,
                InteriorHeight = HeightChunks * chunkSize,
                ScaleCliffQ = scaleQ,
                ScaleOreQ = scaleQ * 7 / 10,
                ScaleRareQ = scaleQ * 6 / 10,
                ScaleRuinQ = scaleQ * 8 / 10,
                ScaleOilQ = scaleQ * 5 / 10,
                ScalePollutionQ = (int)(((long)scaleQ * Q(v.PollutionScale)) >> 16),
                CliffT = Q(v.CliffThreshold),
                WaterT = Q(v.WaterThreshold),
                OreT = oreT,
                RareBiasQ = Q(v.RareBias),
                OilBiasQ = Q(v.OilBias),
                PollutionT = Q(v.PollutionThreshold),
                InteriorRuinT = Q(v.InteriorRuinThreshold),
                PollutionDistStart = v.PollutionDistanceStart,
                PollutionDistPerLevel = v.PollutionDistancePerLevel,
                PollutionIntensityQ = Q(preset.PollutionIntensity),
                ResourcePerStep = v.ResourceDistancePerStep,
                ResourceStepQ = Q(v.ResourceDistanceStep),
                ResourceMaxSteps = v.ResourceDistanceMaxSteps,
                TerritoryBonus = v.TerritoryPollutionBonus,
                HazardPollution = v.HazardPollution,
                PillarSpacing = v.InteriorPillarSpacing,
                CodeBuildable = Code("buildable"),
                CodeCliff = Code("cliff"),
                CodeWater = Code("water"),
                CodeOil = Code("oil"),
                CodeOreMetal = Code("ore_metal"),
                CodeOreRare = Code("ore_rare"),
                CodeRuin = Code("ruin"),
                // FG3-GEN-01（FGR-GEN-031 第 1 级）：v1 版本行为 0，不启用。
                FlatRadius = IsInterior ? 0 : ScaledRadius(v.StartFlatRadius, preset),
                FlatPollutionCap = v.StartFlatPollution,
            };

            if (IsInterior)
            {
                Rects = Array.Empty<WorldGenRect>();
                Zones = Array.Empty<WorldGenZone>();
            }
            else
            {
                Rects = StartProtection(v, preset.LegacyRelaxed, core);
                Zones = plan?.Zones ?? Array.Empty<WorldGenZone>();
            }
            SourceKey = string.Concat(Id, "|", surface.Id, "|", worldSeed.ToString(), "|v", version.ToString(), "|", preset.Id, "|", core.ToString());
        }

        /// <summary>浮点表值 → 16.16 定点（round(x × 65536)，同一个 float 永远得到同一个整数）。</summary>
        public static int Q(float x) => (int)Math.Round(x * 65536.0);

        /// <summary>起始区半径按世界设置“起始区”缩放（定点，四舍五入；宽松 1.5 → 12 格变 18 格）。</summary>
        public static int ScaledRadius(int radius, WorldSettings settings) =>
            radius <= 0 ? 0 : (int)(((long)radius * Q(settings?.StartScale ?? 1f) + 32768) >> 16);

        private static byte Code(string terrainId) => GridContent.TerrainCode(terrainId);

        /// <summary>起始区保护矩形：核心周围 protectRadius 格（切比雪夫）、开局布局建筑与建造位占地外 protectMargin 圈、非建筑锚点周围
        /// anchorProtectRadius 格强制为可建空地、无污染，所以开局布局在任意种子下都合法（FG00 B25；FGR-GEN-031 第 2 级“开局预置建筑的平地”）。
        /// FG3-GEN-01 起读版本行 startClearSet 引用的**字面快照**（fg.TbWorldStartClear，相对核心），不再按当前开局布局现算（关闭 DEBT-FG0ARCH05-09）：
        /// 改开局布局不会改变任何已发布版本的世界。c1 快照与 FG0-ARCH-05 起按布局现算的矩形逐个相同（生成输入清单基准守护）。
        /// v1 的“宽松”起始区用快照的 relaxed 变体（三个半径 ×1.5）；v2 两种起始区都用 standard，宽松改为放大第 1、3 级半径（<see cref="ScaledRadius"/>）。</summary>
        public static WorldGenRect[] StartProtection(WorldGenVersion v, bool relaxed, GridCell core)
        {
            List<WorldStartClear> rows = WorldGenContent.StartClearFor(v, relaxed ? "relaxed" : "standard");
            if (rows.Count == 0)
            {
                throw new KeyNotFoundException($"起始区平地快照 fg.TbWorldStartClear 缺少生成器 v{v.Version} 引用的 {v.StartClearSet}.{(relaxed ? "relaxed" : "standard")}");
            }
            var rects = new WorldGenRect[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                WorldStartClear r = rows[i];
                rects[i] = new WorldGenRect(core.X + r.MinX, core.Y + r.MinY, core.X + r.MaxX, core.Y + r.MaxY);
            }
            return rects;
        }

        public bool IsProtected(int x, int y)
        {
            foreach (WorldGenRect r in Rects)
            {
                if (x >= r.MinX && x <= r.MaxX && y >= r.MinY && y <= r.MaxY)
                {
                    return true;
                }
            }
            return false;
        }

        public void Sample(int x, int y, out byte terrain, out byte pollution) =>
            WorldGenKernel.SampleCell(in Params, x, y, Rects, Zones, out terrain, out pollution);

        public void FillChunk(int chunkX, int chunkY, int size, byte[] terrain, byte[] pollution) =>
            WorldGenKernel.GenerateNow(in Params, chunkX, chunkY, Rects, Zones, terrain, pollution, burst: true);

        /// <summary>托管路径（自检证明 Burst 与托管逐字节一致）。</summary>
        public void FillChunkManaged(int chunkX, int chunkY, byte[] terrain, byte[] pollution) =>
            WorldGenKernel.GenerateNow(in Params, chunkX, chunkY, Rects, Zones, terrain, pollution, burst: false);

        public bool ChunkExists(int chunkX, int chunkY, int size)
        {
            if (IsInterior)
            {
                return chunkX >= 0 && chunkY >= 0 && chunkX < WidthChunks && chunkY < HeightChunks;
            }
            long minX = (long)chunkX * size;
            long minY = (long)chunkY * size;
            long maxX = minX + size - 1;
            long maxY = minY + size - 1;
            // 区块与 [-limit, limit]² 有交集才存在（FGR-GEN-051 坐标上限）。
            return maxX >= -CoordLimit && minX <= CoordLimit && maxY >= -CoordLimit && minY <= CoordLimit;
        }

        /// <summary>在工作线程上调度一个区块的生成。</summary>
        public WorldGenJob Schedule(int chunkX, int chunkY, int tag) => WorldGenKernel.Schedule(in Params, chunkX, chunkY, tag, Rects, Zones);
    }
}
