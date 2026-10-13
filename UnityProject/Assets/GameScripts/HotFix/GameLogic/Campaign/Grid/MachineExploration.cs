using System.Collections.Generic;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>
    /// FG3-LOG-01（FGR-LOG-013“探索靠信号塔覆盖范围扩张，或者由机器走过去”；DEBT-FG0ARCH04-06 的机器探索部分）：
    /// 家园机器走到哪里，周围 grid.explore_machine_radius 格就记为已探索（可以建造）。
    /// FG7-ENV-05（FGR-ENV-050“机器走到的地方被探索”）：按**走过的路**探索——每台机器记下上一次被检查的位置（<see cref="MachineRecord.ExploreLast"/>，进存档），
    /// 这次检查时沿两点之间按 explore.trail_step 格采样补探索，机器再多、两次检查之间走得再远，路上也不留缝（连续空间，不是地格）；
    /// 两点相距超过 explore.trail_gap_max 当作瞬移（出厂、读档、远征归来），不沿路补。
    ///
    /// - 属于模拟：由家园的固定模拟步调用（<see cref="HomeValleyController.SimStep"/>），镜头在不在家园都一样（FGR-BASE-021）。
    /// - 每步只检查 grid.explore_machines_per_step 台机器（轮流：第 游戏步数 mod 机器数 台），与机器数无关；沿路采样最多 trail_gap_max / trail_step 个点（初值 11）。
    ///   每个采样点用 <see cref="HomeGridMap.IsExploredNoLoad"/> 查自己与四周半径一半处是否都已探索（不生成区块），都探索过就什么也不做——
    ///   只有真正走进迷雾时才追加一个圆。
    /// - 复杂度：常见情况（机器走在已探索区里）先命中上次的圆，O(1)；最坏（走进迷雾或换了区域）O(已探索圆数)。
    ///   追加圆（RevealArea）O(已探索圆数)；叠加层只重画遮罩真正变了的区块（<see cref="HomeGridMap.Chunk.ExploredMaskRevision"/>）。
    ///   数百～上千个圆下的实测见 FgBuildFormalSelfCheck 性能段与 FgExplorationSelfCheck P1 段。
    /// - 圆心吸附到 grid.explore_lattice 格距的格点：同一小片区域只会记一个圆，已探索圆的数量随“探索过的面积”增长而不是随时间增长。
    /// - 轮到哪台机器只取决于游戏步数（进存档）和机器句柄的顺序（按名册重建），存读档后继续同样的节奏。
    /// </summary>
    public static class MachineExploration
    {
        /// <summary>本会话因机器走动而追加的已探索圆数（自检读取）。</summary>
        public static int Reveals { get; private set; }

        /// <summary>本会话沿路补探索检查过的采样点数（自检读取）。</summary>
        public static int TrailSamples { get; private set; }

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
                TryRevealAlong(state, m.LogicId, m.Position);
            }
        }

        /// <summary>
        /// FG7-ENV-05：机器 <paramref name="logicId"/> 现在在 <paramref name="position"/>——从它上一次被检查的位置沿直线按 explore.trail_step 格采样补探索，
        /// 再看脚下；记下这次的位置。没有机器记录时只看脚下。返回是否追加了已探索圆。
        /// </summary>
        public static bool TryRevealAlong(CampaignState state, int logicId, Vector2 position)
        {
            if (state == null)
            {
                return false;
            }
            bool any = false;
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null)
            {
                if (rec.ExploreHasLast)
                {
                    Vector2 from = rec.ExploreLast;
                    float d = Vector2.Distance(from, position);
                    float step = Mathf.Max(1f, GridContent.Tuning("explore.trail_step"));
                    float gapMax = Mathf.Max(step, GridContent.Tuning("explore.trail_gap_max"));
                    if (d > step && d <= gapMax)
                    {
                        int n = Mathf.CeilToInt(d / step);
                        for (int i = 1; i < n; i++)
                        {
                            TrailSamples++;
                            any |= TryRevealAround(state, Vector2.Lerp(from, position, i / (float)n));
                        }
                    }
                }
                rec.ExploreLast = position;
                rec.ExploreHasLast = true;
            }
            any |= TryRevealAround(state, position);
            return any;
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
