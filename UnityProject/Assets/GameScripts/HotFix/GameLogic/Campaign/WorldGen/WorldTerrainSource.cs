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

        public WorldTerrainSource(int worldSeed, int version, WorldPreset preset, Surface surface, GridCell core, int chunkSize, WorldPlan plan)
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
            };

            if (IsInterior)
            {
                Rects = Array.Empty<WorldGenRect>();
                Zones = Array.Empty<WorldGenZone>();
            }
            else
            {
                Rects = StartProtection(v, preset.StartZone == "relaxed", core);
                Zones = plan?.Zones ?? Array.Empty<WorldGenZone>();
            }
            SourceKey = string.Concat(Id, "|", surface.Id, "|", worldSeed.ToString(), "|v", version.ToString(), "|", preset.Id, "|", core.ToString());
        }

        /// <summary>浮点表值 → 16.16 定点（round(x × 65536)，同一个 float 永远得到同一个整数）。</summary>
        public static int Q(float x) => (int)Math.Round(x * 65536.0);

        private static byte Code(string terrainId) => GridContent.TerrainCode(terrainId);

        /// <summary>起始区保护矩形（沿用 FG0-ARCH-04 原型规则）：核心周围 protectRadius 格（切比雪夫）、开局布局建筑与建造位占地外
        /// protectMargin 圈、非建筑锚点周围 anchorProtectRadius 格强制为可建空地，所以开局布局在任意种子下都合法（FG00 B25）。
        /// “宽松”起始区把三个半径放大 1.5 倍（FGR-GEN-070）。
        /// 注意：开局布局与这些建筑的占地目前不随版本走（C 类生成输入，冻结待版本化，DEBT-FG0ARCH05-09）；
        /// <see cref="WorldGenInputs"/> 把这里算出的矩形纳入生成输入清单，改布局 / 占地而不升版本时自检失败。</summary>
        public static WorldGenRect[] StartProtection(WorldGenVersion v, bool relaxed, GridCell core)
        {
            int Scale(int r) => relaxed ? r * 3 / 2 : r;
            int radius = Scale(v.ProtectRadius);
            int margin = Scale(v.ProtectMargin);
            int anchor = Scale(v.AnchorProtectRadius);
            var rects = new List<WorldGenRect> { new WorldGenRect(core.X - radius, core.Y - radius, core.X + radius, core.Y + radius) };
            foreach (StartLayout row in GridContent.StartLayout)
            {
                var at = new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY);
                if ((row.Kind == "building" || row.Kind == "site") && GridContent.TryGetBuilding(row.TypeId, out BuildingGrid g))
                {
                    GridMath.FootprintBounds(at, g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(row.Rotation), out GridCell min, out GridCell max);
                    rects.Add(new WorldGenRect(min.X - margin, min.Y - margin, max.X + margin, max.Y + margin));
                }
                else
                {
                    rects.Add(new WorldGenRect(at.X - anchor, at.Y - anchor, at.X + anchor, at.Y + anchor));
                }
            }
            return rects.ToArray();
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
