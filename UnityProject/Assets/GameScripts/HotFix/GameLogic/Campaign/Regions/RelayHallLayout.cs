using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-FAC-01（FG17 FGR-GEN-024 手工锚点；FG09 第 4 章“首领场地：地下中继大厅”）：室内场地的手工点位（fg.TbInteriorPoint）。
    /// 点位写成室内宽高的比例，落位 = 比例 × 宽高；落在立柱（室内表面按种子生成的悬崖格）上时按固定顺序的环形搜索挪到最近的空地，
    /// 所以任何种子下点位都在可走的地面上（B25，内容不依赖固定坐标）。结果按（种子, 表面）缓存，读档重算、不进存档。
    /// </summary>
    public static class RelayHallLayout
    {
        public const string SurfaceId = "relay_hall";
        public const string KindRelay = "relay";
        public const string KindCore = "core";
        public const string KindPoi = "poi";

        private static readonly Dictionary<string, Vector2> Cache = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private static int _cacheSeed = int.MinValue;
        private static int _cacheRevision = int.MinValue;

        private static readonly HashSet<string> SurfacesWithPoints = new HashSet<string>(StringComparer.Ordinal);
        private static int _surfacesRevision = int.MinValue;

        /// <summary>这个室内表面有没有手工点位（O(1)，按表版本缓存）。</summary>
        public static bool IsInteriorWithPoints(string surfaceId)
        {
            if (surfaceId == null)
            {
                return false;
            }
            int rev = FgContentTables.Revision;
            if (rev != _surfacesRevision)
            {
                _surfacesRevision = rev;
                SurfacesWithPoints.Clear();
                TbInteriorPoint t = ConfigSystem.Instance?.Tables?.TbInteriorPoint;
                if (t != null)
                {
                    foreach (InteriorPoint p in t.DataList)
                    {
                        if (p != null && !string.IsNullOrEmpty(p.SurfaceId))
                        {
                            SurfacesWithPoints.Add(p.SurfaceId);
                        }
                    }
                }
            }
            return SurfacesWithPoints.Contains(surfaceId);
        }

        /// <summary>某个室内表面的全部点位（表顺序）。</summary>
        public static List<InteriorPoint> Points(string surfaceId)
        {
            var list = new List<InteriorPoint>();
            TbInteriorPoint t = ConfigSystem.Instance?.Tables?.TbInteriorPoint;
            if (t == null)
            {
                return list;
            }
            foreach (InteriorPoint p in t.DataList)
            {
                if (p != null && p.SurfaceId == surfaceId)
                {
                    list.Add(p);
                }
            }
            list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            return list;
        }

        public static bool TryGetRow(string pointId, out InteriorPoint row)
        {
            row = null;
            TbInteriorPoint t = ConfigSystem.Instance?.Tables?.TbInteriorPoint;
            return pointId != null && t != null && t.DataMap.TryGetValue(pointId, out row) && row != null;
        }

        /// <summary>点位在当前世界里的落位（格坐标，室内局部坐标）。点位不存在 / 没有存档时返回 false。</summary>
        public static bool TryPoint(CampaignState s, string pointId, out Vector2 position)
        {
            position = default;
            if (!TryGetRow(pointId, out InteriorPoint row))
            {
                return false;
            }
            if (s == null)
            {
                // 没有存档（导表校验 / 离线检查）：只按比例落位，不避立柱（立柱由世界种子决定）。
                return TryRaw(row, out position);
            }
            int seed = s.World?.WorldSeed ?? s.RandomSeed;
            int rev = FgContentTables.Revision;
            if (seed != _cacheSeed || rev != _cacheRevision)
            {
                Cache.Clear();
                _cacheSeed = seed;
                _cacheRevision = rev;
            }
            if (Cache.TryGetValue(pointId, out position))
            {
                return true;
            }
            if (!WorldGenContent.TryGetSurface(row.SurfaceId, out Surface surf) || !WorldGenContent.IsInterior(surf))
            {
                return false;
            }
            int chunk = GridContent.TuningInt("grid.chunk_size");
            int w = Math.Max(1, surf.WidthChunks) * chunk;
            int h = Math.Max(1, surf.HeightChunks) * chunk;
            int cx = Mathf.Clamp(Mathf.RoundToInt(row.Fx * w), 2, w - 3);
            int cy = Mathf.Clamp(Mathf.RoundToInt(row.Fy * h), 3, h - 3);
            HomeGridMap map = WorldGenService.MapFor(s, row.SurfaceId);
            GridCell cell = NearestOpen(map, new GridCell(cx, cy), w, h);
            position = new Vector2(cell.X + 0.5f, cell.Y + 0.5f);
            Cache[pointId] = position;
            return true;
        }

        private static bool TryRaw(InteriorPoint row, out Vector2 position)
        {
            position = default;
            if (!WorldGenContent.TryGetSurface(row.SurfaceId, out Surface surf) || !WorldGenContent.IsInterior(surf))
            {
                return false;
            }
            int chunk = GridContent.TuningInt("grid.chunk_size");
            int w = Math.Max(1, surf.WidthChunks) * chunk;
            int h = Math.Max(1, surf.HeightChunks) * chunk;
            position = new Vector2(Mathf.Clamp(Mathf.RoundToInt(row.Fx * w), 2, w - 3) + 0.5f, Mathf.Clamp(Mathf.RoundToInt(row.Fy * h), 3, h - 3) + 0.5f);
            return true;
        }

        /// <summary>离 <paramref name="from"/> 最近的非悬崖格（2×2 余量，立柱是 2×2 块；按切比雪夫环、固定顺序）。找不到返回原格并写日志。</summary>
        private static GridCell NearestOpen(HomeGridMap map, GridCell from, int w, int h)
        {
            byte cliff = GridContent.TerrainCode("cliff");
            for (int r = 0; r <= 8; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        int x = from.X + dx;
                        int y = from.Y + dy;
                        if (x < 2 || y < 2 || x > w - 3 || y > h - 3)
                        {
                            continue;
                        }
                        if (map == null || (!IsCliff(map, x, y, cliff) && !IsCliff(map, x - 1, y, cliff) && !IsCliff(map, x + 1, y, cliff)
                                            && !IsCliff(map, x, y - 1, cliff) && !IsCliff(map, x, y + 1, cliff)))
                        {
                            return new GridCell(x, y);
                        }
                    }
                }
            }
            Log.Warning($"[RelayHallLayout] 点位 ({from.X},{from.Y}) 附近 8 格内没有空地，按原位放置。");
            return from;
        }

        private static bool IsCliff(HomeGridMap map, int x, int y, byte cliff) => map.GetTerrain(new GridCell(x, y)) == cliff;

        public static void ResetCacheForTests()
        {
            Cache.Clear();
            _cacheSeed = int.MinValue;
        }
    }
}
