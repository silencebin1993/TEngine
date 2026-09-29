using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>起始区保证一项的结果（FGR-GEN-031）。</summary>
    public sealed class StartGuaranteeItem
    {
        public int Index;
        public string Item;
        public string Terrain;
        public string NameKey;
        public int Radius;
        public int MinCells;
        public int MinSquare;
        /// <summary>自然生成就满足（没有放保证点）。</summary>
        public bool NaturallySatisfied;
        /// <summary>最终是否满足。</summary>
        public bool Satisfied;
        /// <summary>放了保证点（局部重生成）。</summary>
        public bool Stamped;
        /// <summary>保证点用了第几次派生子种子试探（0 起）；-1 = 候选兜底。</summary>
        public int StampAttempt = -2;
        public int StampX;
        public int StampY;
        public int StampRadius;
        /// <summary>满足条件的那片连通区的格数。</summary>
        public int BestCells;
        /// <summary>这片资源的代表位置（整块左下角）：地图上的“起始区资源点”图标。</summary>
        public int SiteX;
        public int SiteY;
        public int TotalCells;
    }

    /// <summary>起始区四级保证的校验报告（FGR-GEN-031、090）。规划层的一部分：读档时按种子重算，不进存档。</summary>
    public sealed class StartGuaranteeReport
    {
        public int FlatRadius;
        public int FlatPollutionCap;
        public int NoOutpostRadius;
        public int CliffsInFlat;
        public int MaxPollutionInFlat;
        public int SampledCells;
        public readonly List<StartGuaranteeItem> Items = new List<StartGuaranteeItem>();
        public readonly List<string> Log = new List<string>();
        public readonly List<string> Failures = new List<string>();
        /// <summary>自然生成 + 局部重生成的两次校验（Burst）加起来的耗时（毫秒，墙钟）。</summary>
        public double AnalyzeMs;
        public int AnalyzePasses;

        public bool FlatOk => CliffsInFlat == 0 && MaxPollutionInFlat <= FlatPollutionCap;

        public bool AllSatisfied
        {
            get
            {
                if (!FlatOk)
                {
                    return false;
                }
                foreach (StartGuaranteeItem i in Items)
                {
                    if (!i.Satisfied)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public int StampCount
        {
            get
            {
                int n = 0;
                foreach (StartGuaranteeItem i in Items)
                {
                    n += i.Stamped ? 1 : 0;
                }
                return n;
            }
        }

        public StartGuaranteeItem Find(string item)
        {
            foreach (StartGuaranteeItem i in Items)
            {
                if (i.Item == item)
                {
                    return i;
                }
            }
            return null;
        }

        public string Fingerprint()
        {
            var sb = new StringBuilder("G:");
            foreach (StartGuaranteeItem i in Items)
            {
                sb.Append(i.Item).Append(i.Stamped ? "@" + i.StampX + "," + i.StampY + "a" + i.StampAttempt : "n").Append(';');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-031 起始区保证，FGR-GEN-090 生成校验与局部重生成）。
    ///
    /// 四级保证（以归还核心为中心）：
    /// 1. 半径 startFlatRadius（12）内没有悬崖、污染 ≤ startFlatPollution（1）：生成内核按规则保证（<see cref="WorldGenParams.FlatRadius"/>），这里校验；
    /// 2. 半径 24 内：开局预置建筑的平地（起始区平地快照，版本行 startClearSet）+ 水源 / 金属矿脉 / 废墟群；
    /// 3. 半径 64 内：没有敌方建筑（<see cref="WorldFeatures"/> 的侦察巢避开）+ 稀土矿脉；
    /// 4. 半径 128 内：油井。
    /// 资源项按 fg.TbWorldStartGuarantee（版本行 guaranteeSet）逐项校验：先看自然生成；某项不满足时，**只对这一项**用派生子种子
    /// （种子, 项序号, 次数）确定性地放一个保证点（半径 stampRadius 的圆盘，避开开局布局的平地与已放的保证点），再整体复核；
    /// 不重生成整个世界，其它项、其它区块都不变。试探用尽后按候选兜底（仍确定），实在放不下写失败日志（需要调表），不会卡死。
    /// 逐格的采样与连通区统计都在 AOT 的 Burst 任务里（<see cref="WorldGenKernel.AnalyzeStart"/>）。
    /// </summary>
    public static class StartZoneGuarantee
    {
        private const uint StampSalt = 0x53544D50u; // "STMP"

        /// <summary>对规划层求起始区保证：写入保证点（<see cref="WorldPlan.SetStamps"/>）与报告（<see cref="WorldPlan.StartReport"/>）。
        /// 版本行没有保证集合（v1）时什么也不做。</summary>
        public static void Resolve(WorldPlan plan, WorldGenVersion v, WorldSettings settings, GridCell core, int chunkSize)
        {
            List<WorldStartGuarantee> rows = WorldGenContent.GuaranteesFor(v);
            if (plan == null || rows.Count == 0)
            {
                return;
            }
            var sw = Stopwatch.StartNew();
            var report = new StartGuaranteeReport
            {
                FlatRadius = WorldTerrainSource.ScaledRadius(v.StartFlatRadius, settings),
                FlatPollutionCap = v.StartFlatPollution,
                NoOutpostRadius = WorldTerrainSource.ScaledRadius(v.StartNoOutpostRadius, settings),
            };
            // 自然生成：领地 + 河流 + 矿带，没有保证点。
            plan.SetStamps(null);
            var source = new WorldTerrainSource(plan.Seed, v.Version, settings, WorldGenContent.Surface(WorldGenContent.EarthSurfaceId), core, chunkSize, plan);
            var queries = new WorldStartQuery[rows.Count];
            int maxRadius = 1;
            for (int i = 0; i < rows.Count; i++)
            {
                WorldStartGuarantee g = rows[i];
                queries[i] = new WorldStartQuery { Code = GridContent.TerrainCode(g.Terrain), Radius = g.Radius, MinCells = g.MinCells, MinSquare = g.MinSquare };
                maxRadius = Math.Max(maxRadius, g.Radius);
                report.Items.Add(new StartGuaranteeItem
                {
                    Index = i, Item = g.Item, Terrain = g.Terrain, NameKey = g.NameKey, Radius = g.Radius, MinCells = g.MinCells, MinSquare = g.MinSquare,
                });
            }
            var results = new WorldStartResult[rows.Count];
            var stats = new int[3];
            WorldGenKernel.AnalyzeStart(in source.Params, source.Rects, plan.Zones, maxRadius, report.FlatRadius, queries, results, stats);
            report.AnalyzePasses++;
            for (int i = 0; i < rows.Count; i++)
            {
                report.Items[i].NaturallySatisfied = results[i].Satisfied == 1;
            }

            // 局部重生成：只对不满足的项放保证点，复核后仍不满足的（被后放的保证点压掉等）再放一轮，至多 3 轮。
            uint seed = unchecked((uint)plan.Seed);
            var stamps = new List<WorldGenZone>();
            int attemptsMax = Math.Max(1, v.StartStampAttempts);
            for (int round = 0; round < 3; round++)
            {
                bool placed = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    StartGuaranteeItem item = report.Items[i];
                    if (results[i].Satisfied == 1 || item.Stamped)
                    {
                        continue;
                    }
                    WorldStartGuarantee g = rows[i];
                    if (TryPlaceStamp(seed, i, g, core, source.Rects, stamps, attemptsMax, out int sx, out int sy, out int attempt))
                    {
                        stamps.Add(WorldPlan.Stamp(sx, sy, g.StampRadius, GridContent.TerrainCode(g.Terrain)));
                        item.Stamped = true;
                        item.StampX = sx;
                        item.StampY = sy;
                        item.StampRadius = g.StampRadius;
                        item.StampAttempt = attempt;
                        placed = true;
                        report.Log.Add(attempt >= 0
                            ? $"{g.Item}：自然生成不满足（{g.Radius} 格内最大连通区 {results[i].BestCells} 格、要 ≥{g.MinCells} 格且含 {g.MinSquare}×{g.MinSquare} 整块），按子种子（种子, 项 {i}, 次 {attempt}）局部重生成：保证点 ({sx},{sy}) 半径 {g.StampRadius}"
                            : $"{g.Item}：自然生成不满足，{attemptsMax} 次子种子试探都与开局布局 / 其它保证点冲突，按候选兜底放在 ({sx},{sy})");
                    }
                    else
                    {
                        report.Failures.Add($"{g.Item}：{g.Radius} 格内找不到能放半径 {g.StampRadius} 保证点的位置（开局布局占满？需要调表）");
                    }
                }
                if (!placed)
                {
                    break;
                }
                plan.SetStamps(stamps);
                WorldGenKernel.AnalyzeStart(in source.Params, source.Rects, plan.Zones, maxRadius, report.FlatRadius, queries, results, stats);
                report.AnalyzePasses++;
                bool all = true;
                for (int i = 0; i < rows.Count; i++)
                {
                    all &= results[i].Satisfied == 1;
                }
                if (all)
                {
                    break;
                }
            }
            plan.SetStamps(stamps);
            for (int i = 0; i < rows.Count; i++)
            {
                StartGuaranteeItem item = report.Items[i];
                item.Satisfied = results[i].Satisfied == 1;
                item.BestCells = results[i].BestCells;
                item.SiteX = results[i].SquareX;
                item.SiteY = results[i].SquareY;
                item.TotalCells = results[i].TotalCells;
                if (!item.Satisfied)
                {
                    report.Failures.Add($"{item.Item}：局部重生成后仍不满足（最大连通区 {item.BestCells} 格）");
                }
            }
            report.CliffsInFlat = stats[0];
            report.MaxPollutionInFlat = stats[1];
            report.SampledCells = stats[2];
            if (!report.FlatOk)
            {
                report.Failures.Add($"第 1 级：{report.FlatRadius} 格内有 {report.CliffsInFlat} 格悬崖、最高污染 {report.MaxPollutionInFlat}（上限 {report.FlatPollutionCap}）");
            }
            sw.Stop();
            report.AnalyzeMs = sw.Elapsed.TotalMilliseconds;
            plan.StartReport = report;
        }

        /// <summary>
        /// 为第 <paramref name="index"/> 项找保证点圆心：先按子种子（种子, 项, 次数）在“圆盘整个落在保证半径内”的范围里试探 <paramref name="attempts"/> 次；
        /// 都不行时逐个列出全部合法候选、按子种子取一个（候选兜底，<paramref name="attempt"/> = -1）。合法 = 圆盘外扩 1 格不碰起始区平地矩形、
        /// 与已放的保证点至少隔 2 格。全整数运算，结果只取决于（种子, 版本, 设置, 核心）。
        /// 热更层只算试探点（O(attempts)）；检查与候选兜底（O(半径²)）在 AOT Burst 里做（<see cref="WorldGenKernel.PlaceStamp"/>）。
        /// </summary>
        private static bool TryPlaceStamp(uint seed, int index, WorldStartGuarantee g, GridCell core, WorldGenRect[] rects, List<WorldGenZone> placed,
            int attempts, out int x, out int y, out int attempt)
        {
            int r = Math.Max(1, g.StampRadius);
            int hi = g.Radius - r - 1;
            x = core.X;
            y = core.Y;
            attempt = -2;
            if (hi < 0)
            {
                return false;
            }
            int n = Math.Max(0, attempts);
            var tryX = new int[n];
            var tryY = new int[n];
            for (int k = 0; k < n; k++)
            {
                uint h = WorldGenMath.Hash(seed, index, k, StampSalt);
                int angle = (int)(h % 360u);
                int dist = (int)((h >> 9) % (uint)(hi + 1));
                tryX[k] = core.X + (int)(((long)dist * WorldPlan.Cos(angle) + 32768) >> 16);
                tryY[k] = core.Y + (int)(((long)dist * WorldPlan.Cos(angle - 90) + 32768) >> 16);
            }
            uint pickHash = WorldGenMath.Hash(seed, index, attempts, StampSalt);
            attempt = WorldGenKernel.PlaceStamp(core.X, core.Y, r, hi, rects, placed.ToArray(), tryX, tryY, n, pickHash, out x, out y, out _);
            return attempt >= -1;
        }
    }
}
