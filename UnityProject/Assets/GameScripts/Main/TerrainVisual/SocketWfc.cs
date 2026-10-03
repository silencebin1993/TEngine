using System;
using System.Collections.Generic;

namespace BinGames.TerrainVisual
{
    public sealed class WfcResult
    {
        public bool Success;
        public int[] Cells;
        public int Attempts, Observations;
        public string Error;
    }

    // Weighted minimum-entropy observation + AC-style neighbour propagation.
    // A bounded local patch has sealed outside sockets: generation order of chunks cannot affect it.
    public static class SocketWfc
    {
        private static readonly int[] Dx = { 0, 1, 0, -1 }, Dz = { 1, 0, -1, 0 };
        public static WfcResult Solve(List<ModuleVariant> variants, int width, int height, uint seed,
            ulong[] allowed = null, int maxAttempts = 12)
        {
            var result = new WfcResult();
            int m = variants.Count;
            if (m == 0 || m > 63 || width < 1 || height < 1 || (allowed != null && allowed.Length != width * height))
            { result.Error = "模块数量必须为 1～63，尺寸与环境约束必须有效。"; return result; }
            var compatible = new ulong[m, 4];
            for (int a = 0; a < m; a++) for (int d = 0; d < 4; d++) for (int b = 0; b < m; b++)
                if (variants[a].Port(d) == variants[b].Port((d + 2) % 4)) compatible[a, d] |= 1UL << b;
            ulong all = (1UL << m) - 1;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                result.Attempts = attempt + 1;
                uint random = seed ^ (0x9e3779b9u * (uint)(attempt + 1));
                var domains = new ulong[width * height];
                var queue = new Queue<int>();
                for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    ulong mask = allowed == null ? all : all & allowed[i];
                    for (int v = 0; v < m; v++)
                    {
                        ModuleVariant tile = variants[v];
                        if ((z == height - 1 && tile.N != 0) || (x == width - 1 && tile.E != 0)
                            || (z == 0 && tile.S != 0) || (x == 0 && tile.W != 0)) mask &= ~(1UL << v);
                    }
                    domains[i] = mask; queue.Enqueue(i);
                }
                if (!Propagate(domains, compatible, m, width, height, queue)) continue;
                bool contradiction = false;
                for (int iteration = 0; iteration < width * height; iteration++)
                {
                    int cell = -1; double best = double.PositiveInfinity;
                    for (int i = 0; i < domains.Length; i++)
                    {
                        ulong domain = domains[i];
                        if ((domain & (domain - 1)) == 0) continue;
                        double sum = 0, logarithms = 0;
                        for (int v = 0; v < m; v++) if ((domain & (1UL << v)) != 0)
                        { double w = variants[v].Weight; sum += w; logarithms += w * Math.Log(w); }
                        double entropy = Math.Log(sum) - logarithms / sum + Next(ref random) * .0000001;
                        if (entropy < best) { cell = i; best = entropy; }
                    }
                    if (cell < 0) break;
                    float total = 0;
                    for (int v = 0; v < m; v++) if ((domains[cell] & (1UL << v)) != 0) total += variants[v].Weight;
                    double chosen = Next(ref random) * total;
                    int pick = -1;
                    for (int v = 0; v < m; v++) if ((domains[cell] & (1UL << v)) != 0)
                    { pick = v; chosen -= variants[v].Weight; if (chosen <= 0) break; }
                    domains[cell] = 1UL << pick; result.Observations++;
                    queue.Enqueue(cell);
                    if (!Propagate(domains, compatible, m, width, height, queue)) { contradiction = true; break; }
                }
                if (contradiction) continue;
                result.Cells = new int[domains.Length];
                for (int i = 0; i < domains.Length; i++)
                    for (int v = 0; v < m; v++) if ((domains[i] & (1UL << v)) != 0) { result.Cells[i] = v; break; }
                result.Success = true; return result;
            }
            result.Error = "连接规则存在矛盾；已达到有限重试次数。请检查接口和边界，不会强行放置不兼容模块。";
            return result;
        }
        private static bool Propagate(ulong[] domains, ulong[,] compatible, int count, int width, int height, Queue<int> queue)
        {
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue(); ulong source = domains[cell];
                if (source == 0) return false;
                int x = cell % width, z = cell / width;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Dx[d], nz = z + Dz[d];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= height) continue;
                    ulong support = 0;
                    for (int v = 0; v < count; v++) if ((source & (1UL << v)) != 0) support |= compatible[v, d];
                    int neighbour = nz * width + nx;
                    ulong narrowed = domains[neighbour] & support;
                    if (narrowed == domains[neighbour]) continue;
                    if (narrowed == 0) return false;
                    domains[neighbour] = narrowed; queue.Enqueue(neighbour);
                }
            }
            return true;
        }
        private static double Next(ref uint state)
        {
            if (state == 0) state = 0x6d2b79f5;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xffffff) / 16777216.0;
        }
        public static bool CheckConnections(WfcResult result, List<ModuleVariant> variants, int width, int height)
        {
            if (!result.Success || result.Cells == null || result.Cells.Length != width * height) return false;
            for (int z = 0; z < height; z++) for (int x = 0; x < width; x++)
            {
                ModuleVariant a = variants[result.Cells[z * width + x]];
                if ((z == 0 && a.S != 0) || (z == height - 1 && a.N != 0)
                    || (x == 0 && a.W != 0) || (x == width - 1 && a.E != 0)) return false;
                if (x + 1 < width && a.E != variants[result.Cells[z * width + x + 1]].W) return false;
                if (z + 1 < height && a.N != variants[result.Cells[(z + 1) * width + x]].S) return false;
            }
            return true;
        }
    }
}
