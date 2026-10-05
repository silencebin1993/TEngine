using System;
using System.Collections.Generic;

namespace BinGames.Sim.Combat
{
    /// <summary>一段火力覆盖不到的连续区段（沿路线或沿外围圈；格坐标 = 世界坐标取整）。</summary>
    public struct CoverageRun
    {
        public int StartX;
        public int StartY;
        public int EndX;
        public int EndY;
        public int MidX;
        public int MidY;
        /// <summary>区段长度（格；沿路线 = 走过的格数，沿外围 = 弧长取整）。</summary>
        public int Cells;
        /// <summary>沿路线时：区段起点是路线上第几格（离出发地由远到近排序用）；沿外围时：起始采样序号。</summary>
        public int StartIndex;
    }

    /// <summary>
    /// FG6-DEF-06（FG06 FGR-DEF-070 防御面板“覆盖热力图：显示炮塔火力覆盖；薄弱点：沿预测的突袭路线标出火力覆盖不到的区段”）：
    /// 防御总览的覆盖栅格化与薄弱区段查找。逐格 / 逐炮塔的循环放在 AOT（Main/Sim，分层红线：热更层不做 O(格数 × 炮塔数)）。
    /// 纯函数：只读输入数组、写调用方给的输出，不碰战斗内核状态；同一输入结果逐位一致（不随观察 / 倍速变化）。
    /// 只在防御总览打开、或防线 / 突袭计划变了时由热更层调用一次（不是每帧）。
    /// </summary>
    public static class CombatCoverage
    {
        /// <summary>
        /// 把 <paramref name="n"/> 座炮塔（圆心 xs / ys、射程 ranges，单位 = 格 = 米）的射程圆叠加到 <paramref name="w"/>×<paramref name="h"/> 的栅格
        /// （左下角格 = (<paramref name="ox"/>, <paramref name="oy"/>)；格 (x, y) 的代表点是世界坐标 (x, y)）。<paramref name="counts"/> 长度 ≥ w × h，
        /// 每格 = 覆盖它的炮塔数（封顶 255）。返回至少被一座炮塔覆盖的格数。O(Σ 每座炮塔射程外接方形与栅格的交)。
        /// </summary>
        public static int Rasterize(int ox, int oy, int w, int h, float[] xs, float[] ys, float[] ranges, int n, byte[] counts) =>
            Rasterize(ox, oy, w, h, xs, ys, ranges, n, counts, out _);

        /// <summary>同上，另数出被两座及以上炮塔覆盖的格数 <paramref name="covered2"/>（防御总览“两座以上覆盖 %”；逐格统计留在 AOT）。</summary>
        public static int Rasterize(int ox, int oy, int w, int h, float[] xs, float[] ys, float[] ranges, int n, byte[] counts, out int covered2)
        {
            covered2 = 0;
            if (w <= 0 || h <= 0 || counts == null || counts.Length < w * h)
            {
                return 0;
            }
            Array.Clear(counts, 0, w * h);
            for (int t = 0; t < n; t++)
            {
                float r = ranges[t];
                if (!(r > 0f))
                {
                    continue;
                }
                double cx = xs[t], cy = ys[t];
                double r2 = (double)r * r;
                int x0 = Math.Max(0, (int)Math.Floor(cx - r) - ox);
                int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + r) - ox);
                int y0 = Math.Max(0, (int)Math.Floor(cy - r) - oy);
                int y1 = Math.Min(h - 1, (int)Math.Ceiling(cy + r) - oy);
                for (int gy = y0; gy <= y1; gy++)
                {
                    double dy = gy + oy - cy;
                    double dy2 = dy * dy;
                    if (dy2 > r2)
                    {
                        continue;
                    }
                    int row = gy * w;
                    for (int gx = x0; gx <= x1; gx++)
                    {
                        double dx = gx + ox - cx;
                        if (dx * dx + dy2 <= r2 && counts[row + gx] < 255)
                        {
                            counts[row + gx]++;
                        }
                    }
                }
            }
            int covered = 0;
            int two = 0;
            for (int i = 0; i < w * h; i++)
            {
                byte c = counts[i];
                if (c > 0)
                {
                    covered++;
                    if (c >= 2)
                    {
                        two++;
                    }
                }
            }
            covered2 = two;
            return covered;
        }

        /// <summary>
        /// 覆盖数 → 颜色（防御总览热力图的底色，逐格在 AOT）：<paramref name="palette"/>[k] = 覆盖数 k 的颜色，覆盖数超过调色板长度的用最后一种。
        /// <paramref name="into"/> 长度 ≥ <paramref name="len"/>。O(格数)。
        /// </summary>
        public static void PaintCounts(byte[] counts, int len, UnityEngine.Color32[] palette, UnityEngine.Color32[] into)
        {
            if (counts == null || palette == null || palette.Length == 0 || into == null)
            {
                return;
            }
            int n = Math.Min(len, Math.Min(counts.Length, into.Length));
            int last = palette.Length - 1;
            for (int i = 0; i < n; i++)
            {
                int c = counts[i];
                into[i] = palette[c < last ? c : last];
            }
        }

        /// <summary>某格的覆盖数（栅格外 = -1）。</summary>
        public static int At(byte[] counts, int ox, int oy, int w, int h, int x, int y)
        {
            int gx = x - ox, gy = y - oy;
            if (counts == null || gx < 0 || gy < 0 || gx >= w || gy >= h)
            {
                return -1;
            }
            return counts[gy * w + gx];
        }

        /// <summary>
        /// 沿一条折线路线（格坐标的拐点，按行进顺序）逐格走（相邻拐点之间按 Bresenham 补齐），找栅格内连续 ≥ <paramref name="minRun"/> 格覆盖数为 0 的区段；
        /// 走出栅格会把区段截断（栅格外不算家园）。结果按行进顺序追加到 <paramref name="into"/>，返回追加的条数。<paramref name="walked"/> = 栅格内走过的格数（证据 / 自检）。
        /// O(路线长度)。
        /// </summary>
        public static int UncoveredRuns(byte[] counts, int ox, int oy, int w, int h, int[] routeX, int[] routeY, int routeLen, int minRun,
            List<CoverageRun> into, out int walked)
        {
            walked = 0;
            if (counts == null || routeX == null || routeY == null || routeLen <= 0 || into == null)
            {
                return 0;
            }
            int inside = 0;
            int before = into.Count;
            int index = 0;
            bool open = false;
            var run = new CoverageRun();
            int px = routeX[0], py = routeY[0];
            Visit(px, py);
            for (int k = 1; k < routeLen; k++)
            {
                int tx = routeX[k], ty = routeY[k];
                int dx = Math.Abs(tx - px), dy = -Math.Abs(ty - py);
                int sx = px < tx ? 1 : -1, sy = py < ty ? 1 : -1;
                int err = dx + dy;
                int x = px, y = py;
                int guard = 0;
                while ((x != tx || y != ty) && guard++ < 1 << 20)
                {
                    int e2 = 2 * err;
                    if (e2 >= dy)
                    {
                        err += dy;
                        x += sx;
                    }
                    if (e2 <= dx)
                    {
                        err += dx;
                        y += sy;
                    }
                    Visit(x, y);
                }
                px = tx;
                py = ty;
            }
            Close();
            walked = inside;
            return into.Count - before;

            void Visit(int x, int y)
            {
                int c = At(counts, ox, oy, w, h, x, y);
                if (c >= 0)
                {
                    inside++;
                }
                if (c == 0)
                {
                    if (!open)
                    {
                        open = true;
                        run = new CoverageRun { StartX = x, StartY = y, StartIndex = index };
                    }
                    run.EndX = x;
                    run.EndY = y;
                    run.Cells++;
                }
                else
                {
                    Close();
                }
                index++;
            }

            void Close()
            {
                if (!open)
                {
                    return;
                }
                open = false;
                if (run.Cells >= minRun)
                {
                    run.MidX = (run.StartX + run.EndX) / 2;
                    run.MidY = (run.StartY + run.EndY) / 2;
                    into.Add(run);
                }
            }
        }

        /// <summary>
        /// 没有预测路线时的外围检查：以 (cx, cy) 为圆心、<paramref name="radius"/> 为半径的圆上取 <paramref name="samples"/> 个点（从正东逆时针），
        /// 找连续没有火力覆盖（覆盖数 0；栅格外也算没覆盖）、弧长 ≥ <paramref name="minRunCells"/> 的弧段（首尾相接的弧段合并）。返回追加的条数。O(采样数)。
        /// </summary>
        public static int RingRuns(byte[] counts, int ox, int oy, int w, int h, float cx, float cy, float radius, int samples, float minRunCells,
            List<CoverageRun> into)
        {
            if (counts == null || into == null || samples < 4 || !(radius > 0f))
            {
                return 0;
            }
            var bare = new bool[samples];
            var px = new int[samples];
            var py = new int[samples];
            bool anyCovered = false;
            for (int i = 0; i < samples; i++)
            {
                double a = 2.0 * Math.PI * i / samples;
                px[i] = (int)Math.Round(cx + Math.Cos(a) * radius);
                py[i] = (int)Math.Round(cy + Math.Sin(a) * radius);
                bare[i] = At(counts, ox, oy, w, h, px[i], py[i]) <= 0;
                anyCovered |= !bare[i];
            }
            double step = 2.0 * Math.PI * radius / samples;
            int before = into.Count;
            if (!anyCovered)
            {
                into.Add(new CoverageRun
                {
                    StartX = px[0], StartY = py[0], EndX = px[samples - 1], EndY = py[samples - 1], MidX = px[samples / 2], MidY = py[samples / 2],
                    Cells = (int)Math.Round(step * samples), StartIndex = 0,
                });
                return 1;
            }
            // 从一个有覆盖的采样点之后开始绕一圈，弧段不会被起点切成两半。
            int start = 0;
            while (bare[start])
            {
                start++;
            }
            int len = 0;
            int first = -1;
            for (int k = 1; k <= samples; k++)
            {
                int i = (start + k) % samples;
                if (bare[i])
                {
                    if (len == 0)
                    {
                        first = i;
                    }
                    len++;
                    continue;
                }
                if (len > 0)
                {
                    Emit(first, len);
                    len = 0;
                }
            }
            return into.Count - before;

            void Emit(int from, int count)
            {
                int cells = (int)Math.Round(step * count);
                if (cells < minRunCells)
                {
                    return;
                }
                int last = (from + count - 1) % samples;
                int mid = (from + count / 2) % samples;
                into.Add(new CoverageRun
                {
                    StartX = px[from], StartY = py[from], EndX = px[last], EndY = py[last], MidX = px[mid], MidY = py[mid], Cells = cells, StartIndex = from,
                });
            }
        }
    }
}
