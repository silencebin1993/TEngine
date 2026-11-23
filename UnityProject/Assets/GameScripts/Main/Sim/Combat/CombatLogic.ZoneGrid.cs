using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG15-SYS-02（DEBT-FG2FW02-06 / DEBT-EMGRMAT01-01）：区域节拍的单位网格查询。
    /// 原实现每块区域每个节拍逐个扫全部单位（O(区域 × 单位)）；这里在一次 StepZones 里按需建一张单位格链（与 <see cref="EmgHash"/> 同一 4 米格），
    /// 每块区域只取外接框（区域半径 + 最大单位半径）覆盖格里的单位，按槽位升序走原循环体——跳过的单位都是原实现里会被距离判断跳过的，所以结果逐字一致。
    /// 建表后有单位被挪动（<see cref="CombatData.PosEpoch"/> 变了）：当前区域余下的单位退回逐个扫描，下一块区域重建；建表后新加的单位（槽位 ≥ 建表时单位数）逐个补在候选尾部。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>本步前两次逐单位扫描照旧线性（建表本身 O(单位)，区域少时不划算）。</summary>
        private const int ZoneGridMinScans = 2;
        /// <summary>外接框覆盖格超过这个数（超大区域）就线性扫描。</summary>
        private const int ZoneGridMaxCells = 256;
        /// <summary>一步里最多重建几次（每块区域节拍都在挪单位时不再反复建表）。</summary>
        private const int ZoneGridMaxBuilds = 4;

        private static void ZoneGridBegin(ref CombatData d)
        {
            d.ZoneGridOn = (byte)(d.ZoneGridOff == 0 ? 1 : 0);
            d.ZoneGridN = -1;
            d.ZoneScans = 0;
            d.ZoneGridBuilds = 0;
            d.ZoneGridBusy = 0;
        }

        private static void ZoneGridEnd(ref CombatData d)
        {
            d.ZoneGridOn = 0;
            d.ZoneGridN = -1;
            d.ZoneGridBusy = 0;
        }

        /// <summary>
        /// 一块区域（圆心 <paramref name="c"/>、半径 <paramref name="radius"/>）的候选单位写进 <see cref="EmgHash.UnitCand"/>（升序，含建表后新加的单位）。
        /// false = 本次走逐单位扫描（网格关、区域少、坐标非有限值、区域太大、递归调用）。
        /// </summary>
        private static bool ZoneCandidates(ref CombatData d, double2 c, float radius, int n)
        {
            if (d.ZoneGridOn == 0 || d.ZoneGridBusy != 0 || n == 0 || ++d.ZoneScans <= ZoneGridMinScans)
            {
                return false;
            }
            if (!math.all(math.isfinite(c)) || !math.isfinite(radius))
            {
                return false; // 非有限值的区域：原实现的距离判断全为 false（每个单位都算在内），照旧逐个扫
            }
            if (d.ZoneGridN < 0 || d.ZoneGridEpoch != d.PosEpoch || n < d.ZoneGridN)
            {
                if (d.ZoneGridBuilds >= ZoneGridMaxBuilds || !ZoneGridBuild(ref d, n))
                {
                    d.ZoneGridOn = 0;
                    return false;
                }
            }
            double reach = (double)radius + d.ZoneGridMaxR + 0.01; // 原判断是单精度和 radius + r：外扩 1 厘米盖住舍入
            int x0 = EmgHash.CellOf(c.x - reach), x1 = EmgHash.CellOf(c.x + reach), y0 = EmgHash.CellOf(c.y - reach), y1 = EmgHash.CellOf(c.y + reach);
            if (x1 >= x0 && (long)(x1 - x0 + 1) * (y1 - y0 + 1) > ZoneGridMaxCells)
            {
                return false;
            }
            d.EmgHash.UnitCand.Clear();
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    for (int node = d.EmgHash.UnitHead[EmgHash.UnitSlot(cx, cy)] - 1; node >= 0;)
                    {
                        int4 e = d.EmgHash.UnitNodes[node];
                        node = e.w;
                        if (e.x == cx && e.y == cy)
                        {
                            d.EmgHash.UnitCand.Add(e.z);
                        }
                    }
                }
            }
            d.EmgHash.UnitCand.Sort();
            for (int k = d.ZoneGridN; k < n; k++)
            {
                d.EmgHash.UnitCand.Add(k); // 建表后新加的单位：槽位都比表里的大，补在尾部仍是升序
            }
            d.ZoneGridBusy = 1;
            return true;
        }

        /// <summary>按当前位置把前 <paramref name="n"/> 个单位按圆心所在格登记；有非有限值的坐标 / 半径时放弃（本步不用网格）。O(单位)。</summary>
        private static bool ZoneGridBuild(ref CombatData d, int n)
        {
            d.ZoneGridBuilds++;
            d.EmgHash.ClearUnitCells();
            float maxR = 0f;
            for (int k = 0; k < n; k++)
            {
                double2 p = d.Pos[k];
                float r = d.Radius[k];
                if (!math.all(math.isfinite(p)) || !math.isfinite(r))
                {
                    d.ZoneGridN = -1;
                    return false;
                }
                maxR = math.max(maxR, r);
                d.EmgHash.AddUnitCell(EmgHash.CellOf(p.x), EmgHash.CellOf(p.y), k);
            }
            d.ZoneGridMaxR = maxR;
            d.ZoneGridN = n;
            d.ZoneGridEpoch = d.PosEpoch;
            return true;
        }

        private static int ZoneFirstUnit(ref CombatData d, int n) => d.EmgHash.UnitCand.Length > 0 ? d.EmgHash.UnitCand[0] : n;

        /// <summary>候选里的下一个单位；循环体里有单位被挪动了就退回逐个扫描（从 k + 1 起，与原实现同序）。</summary>
        private static int ZoneNextUnit(ref CombatData d, ref bool grid, ref int ci, int k, int n, int epoch)
        {
            if (d.PosEpoch != epoch)
            {
                grid = false;
                return k + 1;
            }
            ci++;
            return ci < d.EmgHash.UnitCand.Length ? d.EmgHash.UnitCand[ci] : n;
        }
    }
}
