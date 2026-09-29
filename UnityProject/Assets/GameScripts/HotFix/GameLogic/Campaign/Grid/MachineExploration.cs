using System.Collections.Generic;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>
    /// FG3-LOG-01（FGR-LOG-013“探索靠信号塔覆盖范围扩张，或者由机器走过去”；DEBT-FG0ARCH04-06 的机器探索部分）：
    /// 家园机器走到哪里，周围 grid.explore_machine_radius 格就记为已探索（可以建造）。
    ///
    /// - 属于模拟：由家园的固定模拟步调用（<see cref="HomeValleyController.SimStep"/>），镜头在不在家园都一样（FGR-BASE-021）。
    /// - 每步只检查 grid.explore_machines_per_step 台机器（轮流：第 游戏步数 mod 机器数 台），与机器数无关；
    ///   轮到的机器用 <see cref="HomeGridMap.IsExploredNoLoad"/> 查自己脚下与四周半径一半处是否都已探索（不生成区块），
    ///   都探索过就什么也不做——只有真正走进迷雾时才追加一个圆。
    /// - 复杂度：常见情况（机器走在已探索区里）先命中上次的圆，O(1)；最坏（走进迷雾或换了区域）O(已探索圆数)。
    ///   追加圆（RevealArea）O(已探索圆数)；叠加层只重画遮罩真正变了的区块（<see cref="HomeGridMap.Chunk.ExploredMaskRevision"/>）。
    ///   数百～上千个圆下的实测见 FgBuildFormalSelfCheck 性能段。
    /// - 圆心吸附到 grid.explore_lattice 格距的格点：同一小片区域只会记一个圆，已探索圆的数量随“探索过的面积”增长而不是随时间增长。
    /// - 轮到哪台机器只取决于游戏步数（进存档）和机器句柄的顺序（按名册重建），存读档后继续同样的节奏。
    /// </summary>
    public static class MachineExploration
    {
        /// <summary>本会话因机器走动而追加的已探索圆数（自检读取）。</summary>
        public static int Reveals { get; private set; }

        public static void Step(CampaignState state, IReadOnlyList<HomeValleyMachineMarker> markers, long tick)
        {
            if (state == null || markers == null || markers.Count == 0)
            {
                return;
            }
            int per = Mathf.Max(1, GridContent.TuningInt("grid.explore_machines_per_step"));
            int count = markers.Count;
            for (int k = 0; k < per && k < count; k++)
            {
                int index = (int)(((tick * per + k) % count + count) % count);
                HomeValleyMachineMarker m = markers[index];
                if (m == null || !m.IsValid)
                {
                    continue;
                }
                TryRevealAround(state, m.Position);
            }
        }

        /// <summary>机器在 <paramref name="position"/>：脚下或四周半径一半处还有没探索的格子时，以吸附后的格点为圆心追加一个已探索圆。</summary>
        public static bool TryRevealAround(CampaignState state, Vector2 position)
        {
            if (state == null)
            {
                return false;
            }
            int radius = GridContent.TuningInt("grid.explore_machine_radius");
            int lattice = Mathf.Max(1, GridContent.TuningInt("grid.explore_lattice"));
            if (radius <= 0)
            {
                return false;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            GridCell at = GridCell.FromWorld(position);
            int half = Mathf.Max(1, radius / 2);
            if (map.IsExploredNoLoad(at) && map.IsExploredNoLoad(new GridCell(at.X + half, at.Y)) && map.IsExploredNoLoad(new GridCell(at.X - half, at.Y))
                && map.IsExploredNoLoad(new GridCell(at.X, at.Y + half)) && map.IsExploredNoLoad(new GridCell(at.X, at.Y - half)))
            {
                return false;
            }
            int cx = Mathf.RoundToInt(at.X / (float)lattice) * lattice;
            int cy = Mathf.RoundToInt(at.Y / (float)lattice) * lattice;
            if (!HomeGridService.RevealArea(state, new Vector2(cx, cy), radius))
            {
                return false;
            }
            Reveals++;
            return true;
        }
    }
}
