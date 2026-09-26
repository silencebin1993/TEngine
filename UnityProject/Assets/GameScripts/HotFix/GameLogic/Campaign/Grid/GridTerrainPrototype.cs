using System;
using System.Collections.Generic;
using GameConfig.fg;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>地形层与污染层的初始分布来源（FGR-LOG-001：来自家园生成器 FGR-GEN-030、031）。
    /// FG0-ARCH-05 的世界生成器实现同一接口替换 <see cref="GridTerrainPrototype"/>。</summary>
    public interface IGridTerrainSource
    {
        /// <summary>来源标识（写进 GridState.TerrainSourceId，读档时知道这份存档的建筑是按哪份地形校验过的）。</summary>
        string SourceId { get; }

        /// <summary>生成身份（来源 + 种子 + 版本 + 世界设置 + 表面 + 核心落点）。身份相同 → 生成结果逐格相同。</summary>
        string SourceKey { get; }

        /// <summary>一个格子的初始地形字节值（fg.TbGridTerrain.code）与污染等级（0～3）。必须只取决于（种子, 格子），与访问顺序无关。</summary>
        void Sample(int x, int y, out byte terrain, out byte pollution);

        /// <summary>整块生成（FG0-ARCH-05：世界生成器在这里走 Burst 内核）。结果必须与逐格 <see cref="Sample"/> 相同。</summary>
        void FillChunk(int chunkX, int chunkY, int size, byte[] terrain, byte[] pollution);

        /// <summary>这个区块在表面上存在（室内表面是有限的；星球受坐标上限约束）。</summary>
        bool ChunkExists(int chunkX, int chunkY, int size);
    }

    /// <summary>
    /// FG0-ARCH-04：FG0-ARCH-05 世界生成器落地前的**原型地形来源**。FG0-ARCH-05 起它是“生成器版本 0”的旧版本路径：
    /// 只给 FG0-ARCH-05 之前的存档（GridState.TerrainSourceId = prototype-v1）重建未修改区块，新战役一律用 WorldTerrainSource
    /// （FGR-GEN-061）。**不要再改它的算法与 grid.terrain.* 调参**——FgWorldGenSelfCheck 的回归哈希守护旧版本结果不变。按（种子, 格子）确定性生成悬崖、水源、矿脉、废墟、油井与污染，
    /// 同一种子任意访问顺序结果相同（整数哈希 + 值噪声，不用 System.Random）。
    /// 起始区保证：核心周围 <c>grid.start_protect_radius</c> 格、开局布局每个建筑 / 建造位占地外 <c>grid.start_protect_margin</c> 圈、
    /// 非建筑锚点（出生点、残骸、靶子、出口）周围 <c>grid.anchor_protect_radius</c> 格强制为可建空地且无污染，
    /// 所以开局布局在任意种子下都合法（FG00 B25）。正式分布（FGR-GEN-030、031、032 起始区四级保证）由 FG3-GEN-01 接手。
    /// </summary>
    public sealed class GridTerrainPrototype : IGridTerrainSource
    {
        public const string Id = "prototype-v1";

        private readonly int _seed;
        private readonly GridCell _core;
        private readonly int _protectRadius;
        private readonly int _anchorRadius;
        private readonly float _scale;
        private readonly float _cliffT;
        private readonly float _waterT;
        private readonly float _oreT;
        private readonly float _pollutionT;
        private readonly byte _cliff;
        private readonly byte _water;
        private readonly byte _oreMetal;
        private readonly byte _oreRare;
        private readonly byte _ruin;
        private readonly byte _oil;
        private readonly List<(GridCell min, GridCell max)> _protectedRects = new List<(GridCell, GridCell)>();
        private readonly List<GridCell> _protectedPoints = new List<GridCell>();

        public string SourceId => Id;

        public string SourceKey { get; }

        public GridTerrainPrototype(int seed, GridCell corePivot)
        {
            _seed = seed;
            _core = corePivot;
            SourceKey = Id + "|" + seed + "|" + corePivot;
            _protectRadius = GridContent.TuningInt("grid.start_protect_radius");
            _anchorRadius = GridContent.TuningInt("grid.anchor_protect_radius");
            int margin = GridContent.TuningInt("grid.start_protect_margin");
            _scale = Mathf.Max(1f, GridContent.Tuning("grid.terrain.feature_scale"));
            _cliffT = GridContent.Tuning("grid.terrain.cliff_threshold");
            _waterT = GridContent.Tuning("grid.terrain.water_threshold");
            _oreT = GridContent.Tuning("grid.terrain.ore_threshold");
            _pollutionT = GridContent.Tuning("grid.terrain.pollution_threshold");
            _cliff = CodeOr("cliff");
            _water = CodeOr("water");
            _oreMetal = CodeOr("ore_metal");
            _oreRare = CodeOr("ore_rare");
            _ruin = CodeOr("ruin");
            _oil = CodeOr("oil");

            foreach (StartLayout row in GridContent.StartLayout)
            {
                var at = new GridCell(corePivot.X + row.OffsetX, corePivot.Y + row.OffsetY);
                if ((row.Kind == "building" || row.Kind == "site") && GridContent.TryGetBuilding(row.TypeId, out BuildingGrid g))
                {
                    GridMath.FootprintBounds(at, g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(row.Rotation), out GridCell min, out GridCell max);
                    _protectedRects.Add((new GridCell(min.X - margin, min.Y - margin), new GridCell(max.X + margin, max.Y + margin)));
                }
                else
                {
                    _protectedPoints.Add(at);
                }
            }
        }

        private static byte CodeOr(string id) => GridContent.TryTerrainCode(id, out byte code) ? code : (byte)0;

        /// <summary>该格是否属于起始区保证（强制可建空地、无污染）。</summary>
        public bool IsProtected(int x, int y)
        {
            if (Math.Max(Math.Abs(x - _core.X), Math.Abs(y - _core.Y)) <= _protectRadius)
            {
                return true;
            }
            for (int i = 0; i < _protectedRects.Count; i++)
            {
                (GridCell min, GridCell max) r = _protectedRects[i];
                if (x >= r.min.X && x <= r.max.X && y >= r.min.Y && y <= r.max.Y)
                {
                    return true;
                }
            }
            for (int i = 0; i < _protectedPoints.Count; i++)
            {
                GridCell p = _protectedPoints[i];
                if (Math.Max(Math.Abs(x - p.X), Math.Abs(y - p.Y)) <= _anchorRadius)
                {
                    return true;
                }
            }
            return false;
        }

        public void Sample(int x, int y, out byte terrain, out byte pollution)
        {
            terrain = 0;
            pollution = 0;
            if (IsProtected(x, y))
            {
                return;
            }
            if (Noise(x, y, 1u, _scale) > _cliffT)
            {
                terrain = _cliff;
            }
            else if (Noise(x, y, 2u, _scale) > _waterT)
            {
                terrain = _water;
            }
            else if (Noise(x, y, 3u, _scale * 0.7f) > _oreT)
            {
                terrain = _oreMetal;
            }
            else if (Noise(x, y, 4u, _scale * 0.6f) > _oreT + (1f - _oreT) * 0.4f)
            {
                terrain = _oreRare;
            }
            else if (Noise(x, y, 5u, _scale * 0.8f) > _oreT)
            {
                terrain = _ruin;
            }
            else if (Noise(x, y, 6u, _scale * 0.5f) > _oreT + (1f - _oreT) * 0.5f)
            {
                terrain = _oil;
            }

            float p = Noise(x, y, 7u, _scale * 1.6f);
            if (p > _pollutionT)
            {
                int level = 1 + (int)((p - _pollutionT) / Mathf.Max(1e-4f, 1f - _pollutionT) * 3f);
                pollution = (byte)Mathf.Clamp(level, 1, 3);
            }
        }

        public void FillChunk(int chunkX, int chunkY, int size, byte[] terrain, byte[] pollution)
        {
            int baseX = chunkX * size;
            int baseY = chunkY * size;
            for (int ly = 0; ly < size; ly++)
            {
                for (int lx = 0; lx < size; lx++)
                {
                    Sample(baseX + lx, baseY + ly, out byte t, out byte p);
                    int i = ly * size + lx;
                    terrain[i] = t;
                    pollution[i] = p;
                }
            }
        }

        public bool ChunkExists(int chunkX, int chunkY, int size) => true;

        // ── 确定性噪声：整数哈希的格点值 + 平滑双线性插值。只依赖（种子, 盐, 格点），与调用顺序无关。──────────

        private float Noise(int x, int y, uint salt, float scale)
        {
            float fx = x / scale;
            float fy = y / scale;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = Smooth(fx - x0);
            float ty = Smooth(fy - y0);
            float a = Lattice(x0, y0, salt);
            float b = Lattice(x0 + 1, y0, salt);
            float c = Lattice(x0, y0 + 1, salt);
            float d = Lattice(x0 + 1, y0 + 1, salt);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private float Lattice(int x, int y, uint salt) => (Hash(_seed, x, y, salt) & 0xFFFFFF) / 16777216f;

        public static uint Hash(int seed, int x, int y, uint salt)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u ^ salt * 0x85EBCA77u;
                h ^= (uint)x * 0xC2B2AE3Du;
                h = ((h << 13) | (h >> 19)) * 0x27D4EB2Fu;
                h ^= (uint)y * 0x165667B1u;
                h = ((h << 17) | (h >> 15)) * 0x9E3779B1u;
                h ^= h >> 15;
                h *= 0x85EBCA77u;
                h ^= h >> 13;
                h *= 0xC2B2AE3Du;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
