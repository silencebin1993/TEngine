using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-061 / FGR-ARC-013，修复轮）：某个生成器版本的**全部生成输入清单**。
    ///
    /// FGR-GEN-061 要求“任何会改变生成结果的改动都必须升版本”。生成结果除了算法本身，还取决于以下数据（ADR-ARC-013 第 4 节）：
    /// - A 类，随版本走：版本行整行（含规划层参数）、版本行引用的领地集合（含行序）、版本行引用的世界设置集合；
    /// - B 类，永久冻结：已发布的表面行（kind / salt / 室内宽高）、区块边长 grid.chunk_size、生成器用到的地形码；
    /// - C 类，冻结待版本化：开局布局与其建筑占地算出的起始区保护矩形（DEBT-FG0ARCH05-09，FG3-GEN-01 改为版本自带）。
    /// 这里把每一部分规范化成一段文本（浮点一律按生成时实际使用的 16.16 定点值写出），<see cref="Digest"/> 求 64 位摘要。
    /// FgWorldGenSelfCheck 为已发布版本保存各部分的摘要基准：不升版本就改了其中任何一项，自检失败并打印新旧文本。
    /// 只在自检 / 工具里调用，不在每帧路径上。
    /// </summary>
    public static class WorldGenInputs
    {
        /// <summary>生成器用到的地形（码写进区块与区块差异，B 类冻结）。</summary>
        public static readonly string[] TerrainIds = { "buildable", "cliff", "water", "oil", "ore_metal", "ore_rare", "ruin" };

        /// <summary>版本 <paramref name="version"/> 的生成输入清单：（部分名, 规范文本）。部分名以 "v{版本}." 开头的随版本走，
        /// 其余（surface.* / grid.chunk_size / terrain.codes）是全局冻结项。</summary>
        public static List<KeyValuePair<string, string>> Manifest(int version)
        {
            WorldGenVersion v = WorldGenContent.Version(version);
            var parts = new List<KeyValuePair<string, string>>();
            string pre = "v" + version.ToString(CultureInfo.InvariantCulture) + ".";

            parts.Add(Part(pre + "version", Join(v.Version, Q(v.FeatureScale), Q(v.CliffThreshold), Q(v.WaterThreshold), Q(v.OreThreshold), Q(v.RareBias),
                Q(v.OilBias), Q(v.PollutionThreshold), Q(v.PollutionScale), v.PollutionDistanceStart, v.PollutionDistancePerLevel, v.ResourceDistancePerStep,
                Q(v.ResourceDistanceStep), v.ResourceDistanceMaxSteps, v.TerritoryPollutionBonus, v.HazardPollution, v.ProtectRadius, v.ProtectMargin,
                v.AnchorProtectRadius, v.InteriorPillarSpacing, Q(v.InteriorRuinThreshold), v.HomeZoneRadius, v.TerritoryMinAngle, v.PlanMaxAttempts,
                v.TerritorySet, v.PresetSet)));

            var sb = new StringBuilder();
            List<Territory> territories = WorldGenContent.TerritoriesFor(v);
            for (int i = 0; i < territories.Count; i++)
            {
                Territory t = territories[i];
                sb.Append(i).Append(':').Append(Join(t.Id, t.Faction, t.MinDistance, t.MaxDistance, t.Radius, t.HazardKind, t.HazardWidth)).Append(';');
            }
            parts.Add(Part(pre + "territories[" + v.TerritorySet + "]", sb.ToString()));

            sb.Clear();
            foreach (WorldPreset p in WorldGenContent.PresetsFor(version))
            {
                sb.Append(Join(p.Id, Q(p.ResourceAbundance), Q(p.PollutionIntensity), Q(p.TerritoryDistanceScale), p.StartZone)).Append(';');
            }
            parts.Add(Part(pre + "presets[" + v.PresetSet + "]", sb.ToString()));

            parts.Add(Part(pre + "start_protection.standard", Rects(WorldTerrainSource.StartProtection(v, false, new GridCell(0, 0)))));
            parts.Add(Part(pre + "start_protection.relaxed", Rects(WorldTerrainSource.StartProtection(v, true, new GridCell(0, 0)))));

            parts.Add(Part("grid.chunk_size", GridContent.TuningInt("grid.chunk_size").ToString(CultureInfo.InvariantCulture)));
            sb.Clear();
            foreach (string id in TerrainIds)
            {
                sb.Append(id).Append('=').Append(GridContent.TerrainCode(id)).Append(';');
            }
            parts.Add(Part("terrain.codes", sb.ToString()));
            foreach (Surface s in WorldGenContent.Surfaces)
            {
                parts.Add(Part("surface." + s.Id, Join(s.Kind, s.Salt, s.WidthChunks, s.HeightChunks)));
            }
            return parts;
        }

        /// <summary>FNV-1a 64 位摘要（UTF-8），与平台无关。</summary>
        public static ulong Digest(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text ?? string.Empty))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }

        private static KeyValuePair<string, string> Part(string name, string text) => new KeyValuePair<string, string>(name, text);

        private static int Q(float x) => WorldTerrainSource.Q(x);

        private static string Rects(WorldGenRect[] rects)
        {
            var sb = new StringBuilder();
            foreach (WorldGenRect r in rects)
            {
                sb.Append(Join(r.MinX, r.MinY, r.MaxX, r.MaxY)).Append(';');
            }
            return sb.ToString();
        }

        private static string Join(params object[] values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(values[i] is System.IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : values[i]?.ToString());
            }
            return sb.ToString();
        }
    }
}
