using System.Collections.Generic;
using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-04：单障碍绕行启发式路径规划。**明确不是完整寻路**——只处理"直线撞上一个挡路障碍，
    /// 光靠 <c>JobIntegrate</c> 的推挤会原地卡住/绕不出去"这类明显卡点，不是可视图 A*。
    ///
    /// 算法（见 preflight-decisions.md「M4-04」D2）：检查 start→goal 线段是否与任一障碍圆
    /// （半径 + <paramref name="clearance"/> 余量）相交；若相交，取沿线段最近的那个阻挡障碍，
    /// 在其侧面插入一个绕行路点（两侧候选各算一个，选偏移距离更短的一侧），得到两段新线段后
    /// 递归同样检查（每段独立找最近阻挡障碍继续插点）。
    ///
    /// **已知局限**：多个障碍物挤在一起排成复杂迷宫时可能绕不出去——本仓当前障碍物是稀疏随机圆
    /// （<see cref="SimConst.MaxObstacles"/> = 32 个上限），这个局限在当前内容下不构成实际问题，
    /// 留给后续故事按需升级为真正的可视图/A*。
    /// </summary>
    public static class FormationPathPlanner
    {
        /// <summary>路点数上限（含 start/goal）。超过直接返回目前算出的路径，不再继续找，
        /// 防止病态输入（障碍物挤在一起）下无限递归/性能失控。</summary>
        public const int MaxWaypoints = 8;

        /// <summary>
        /// 绕行点相对"贴着障碍边界"额外放大的安全系数。
        ///
        /// 为什么需要 > 1：绕行点只保证它本身离障碍圆心够远，不保证 start→绕行点 这段新线段
        /// 全程都不再贴近障碍圆——起点距离障碍越近，折线中点越容易切回圆内（两点都在圆外
        /// 不代表连线全程在圆外，是简单三角形几何事实）。这里用一个经验安全系数换取
        /// "通常一次绕行点就够"，个别没绕够的情况交给下面的递归检查兜底（同一线段还相交
        /// 就再插一个点），而不是精确求解双切线交点——那是完整寻路的复杂度，超出本 story
        /// 单障碍启发式的范围（已记录为已知局限）。
        /// </summary>
        private const float DetourSafetyFactor = 1.35f;

        public static List<float2> Plan(float2 start, float2 goal, IReadOnlyList<ObstacleSpec> obstacles, float clearance)
        {
            var path = new List<float2>(MaxWaypoints) { start, goal };
            if (obstacles == null || obstacles.Count == 0)
            {
                return path;
            }

            InsertBypass(path, 0, clearance, obstacles);
            return path;
        }

        private static void InsertBypass(List<float2> path, int segmentStart, float clearance, IReadOnlyList<ObstacleSpec> obstacles)
        {
            if (path.Count >= MaxWaypoints)
            {
                return;
            }

            float2 a = path[segmentStart];
            float2 b = path[segmentStart + 1];
            if (!TryFindNearestBlockingObstacle(a, b, clearance, obstacles, out ObstacleSpec blocker))
            {
                return;
            }

            float2 detour = ComputeDetourPoint(a, b, blocker, clearance);
            path.Insert(segmentStart + 1, detour);

            // 先递归处理靠后的一段（detour→b）——它的下标不会被"处理前一段"时可能发生的插入
            // 影响；处理完之后再回头处理前一段（a→detour）。顺序反过来会导致下标错位。
            if (path.Count < MaxWaypoints)
            {
                InsertBypass(path, segmentStart + 1, clearance, obstacles);
            }
            if (path.Count < MaxWaypoints)
            {
                InsertBypass(path, segmentStart, clearance, obstacles);
            }
        }

        /// <summary>沿线段找参数 t 最小（离 a 最近）的那个相交障碍，不是随便一个相交的都行——
        /// D2 明确要求"最近阻挡障碍"。</summary>
        private static bool TryFindNearestBlockingObstacle(float2 a, float2 b, float clearance,
            IReadOnlyList<ObstacleSpec> obstacles, out ObstacleSpec blocker)
        {
            blocker = default;
            bool found = false;
            float bestT = float.MaxValue;
            float2 ab = b - a;
            float abLenSq = math.lengthsq(ab);

            for (int i = 0; i < obstacles.Count; i++)
            {
                ObstacleSpec obstacle = obstacles[i];
                float effRadius = obstacle.Radius + clearance;
                float2 ac = obstacle.Position - a;
                float t = abLenSq > 1e-8f ? math.clamp(math.dot(ac, ab) / abLenSq, 0f, 1f) : 0f;
                float2 closest = a + ab * t;
                float distSq = math.distancesq(closest, obstacle.Position);
                if (distSq >= effRadius * effRadius)
                {
                    continue; // 不相交
                }

                if (t < bestT)
                {
                    bestT = t;
                    blocker = obstacle;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// 在障碍侧面插入绕行点：以障碍圆心到线段（无穷长直线）的垂足为基准，
        /// 往两侧各算一个候选（圆心的有符号垂直偏移 ± 放大后的有效半径），选偏移距离更短的一侧。
        /// </summary>
        private static float2 ComputeDetourPoint(float2 a, float2 b, ObstacleSpec obstacle, float clearance)
        {
            float2 ab = b - a;
            float2 dir = math.normalizesafe(ab, new float2(1f, 0f));
            float2 perp = new float2(-dir.y, dir.x);

            float2 toObstacle = obstacle.Position - a;
            float s = math.dot(toObstacle, dir);
            float2 foot = a + dir * s;
            float c = math.dot(toObstacle, perp); // 圆心相对线段的有符号垂直偏移

            float effRadius = (obstacle.Radius + clearance) * DetourSafetyFactor;
            float offsetNear = c - effRadius;
            float offsetFar = c + effRadius;
            float chosen = math.abs(offsetNear) <= math.abs(offsetFar) ? offsetNear : offsetFar;

            return foot + perp * chosen;
        }
    }
}
