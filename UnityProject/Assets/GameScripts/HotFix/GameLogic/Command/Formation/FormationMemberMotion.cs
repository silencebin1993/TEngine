using System.Collections.Generic;
using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-04：驱动"一个成员沿共享路径走到底"的纯状态机——不依赖 <c>SimBridge</c>/内核，
    /// 输入当前路点索引与成员位置，输出本帧应该追的目标点与是否已经走完整条路径。
    ///
    /// 拆出这一层是关键提醒第 2 条的要求：<see cref="FormationMovementDriver"/> 若直接把这套
    /// 逻辑和 <c>SimBridge</c> 调用糅在一起，自检就必须起真实 <c>SimWorld</c> 才能验证状态机，
    /// 成本和可靠性都不划算。这里只是纯函数，自检可以直接喂假路径/假位置断言。
    /// </summary>
    public static class FormationMemberMotion
    {
        /// <summary>一次推进的结果。</summary>
        public readonly struct StepResult
        {
            /// <summary>推进后的路点索引（可能与传入的相同，也可能前移一格）。</summary>
            public readonly int NextWaypointIndex;

            /// <summary>本帧应该追的目标点（= 路点 + 跟随槽偏移）。</summary>
            public readonly float2 TargetPoint;

            /// <summary>是否已经走完整条路径（到达最后一个路点 + 跟随槽偏移的目标点）。</summary>
            public readonly bool Arrived;

            public StepResult(int nextWaypointIndex, float2 targetPoint, bool arrived)
            {
                NextWaypointIndex = nextWaypointIndex;
                TargetPoint = targetPoint;
                Arrived = arrived;
            }
        }

        /// <summary>
        /// 单次推进：若成员到"当前路点 + <paramref name="slotOffset"/>"的距离已进入到达半径，
        /// 路点索引前移一格；若前移后已经是最后一个路点且同样已到达，视为整体抵达。
        /// 空路径按"已抵达"处理，避免调用方还要单独判空。
        /// </summary>
        public static StepResult Step(IReadOnlyList<float2> path, int currentWaypointIndex, float2 memberPosition,
            float2 slotOffset, float arriveRadius)
        {
            if (path == null || path.Count == 0)
            {
                return new StepResult(currentWaypointIndex, memberPosition, true);
            }

            int index = math.clamp(currentWaypointIndex, 0, path.Count - 1);
            float2 targetPoint = path[index] + slotOffset;
            float arriveSq = arriveRadius * arriveRadius;

            bool reached = math.distancesq(memberPosition, targetPoint) <= arriveSq;
            bool isLast = index >= path.Count - 1;

            if (reached && !isLast)
            {
                index++;
                targetPoint = path[index] + slotOffset;
                isLast = index >= path.Count - 1;
                reached = math.distancesq(memberPosition, targetPoint) <= arriveSq;
            }

            bool arrived = isLast && reached;
            return new StepResult(index, targetPoint, arrived);
        }
    }
}
