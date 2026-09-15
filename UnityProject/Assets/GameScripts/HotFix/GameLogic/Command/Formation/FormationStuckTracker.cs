using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-04：卡死检测与重规划边界的纯判定逻辑（D5），从 <see cref="FormationMovementDriver"/> 里
    /// 拆出来的理由与 <see cref="FormationMemberMotion"/> 一致（关键提醒第 2 条）——重规划本身
    /// 要调 <c>SimBridge</c>/<see cref="Formation.TryComputeAnchor"/> 算新起点，没法脱离内核；
    /// 但"要不要重规划/要不要判失败"这个决策本身是纯状态转移，拆出来才能被自检直接喂数字断言，
    /// 不必真的驱动一个会动的单位。
    /// </summary>
    public static class FormationStuckTracker
    {
        public enum Outcome
        {
            /// <summary>没卡住，或还没卡够时长，继续正常推进。</summary>
            Ok,

            /// <summary>刚满足"连续卡住"条件，且重规划次数还没超上限：调用方应该重新规划一次路径。</summary>
            Replan,

            /// <summary>重规划次数已超过上限仍卡住：调用方应该 <see cref="Formation.FailActiveCommand"/>。</summary>
            Fail,
        }

        /// <summary>
        /// 单次推进判定。<paramref name="stuckTimer"/>/<paramref name="replanCount"/> 是调用方持有的
        /// 状态，按引用推进——这样调用方可以把它们存在自己的运行时记录里，不需要这个类型自己维护
        /// 一份跨帧字典。
        /// </summary>
        public static Outcome Evaluate(ref float stuckTimer, ref int replanCount, float movedDistance, float dt,
            float stuckDistanceThreshold, float stuckTimeThreshold, int maxReplans)
        {
            if (movedDistance >= stuckDistanceThreshold)
            {
                stuckTimer = 0f;
                return Outcome.Ok;
            }

            stuckTimer += math.max(0f, dt);
            if (stuckTimer < stuckTimeThreshold)
            {
                return Outcome.Ok;
            }

            // 判定为"卡住"了这一次：不管走 Replan 还是 Fail 分支，计时器都要清零重新计——
            // 否则下一帧又立刻因为"计时器仍 >= 阈值"再触发一次。
            stuckTimer = 0f;
            replanCount++;
            return replanCount > maxReplans ? Outcome.Fail : Outcome.Replan;
        }
    }
}
