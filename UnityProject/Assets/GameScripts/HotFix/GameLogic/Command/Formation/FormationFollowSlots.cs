using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-04：跟随槽——"阵位调整"的全部实现。只是一个纯公式，不做队形形状（三角形/楔形等）
    /// 选择，那是更细的表现层课题，本 story 不做（见 preflight-decisions.md D3/D8）。
    ///
    /// 返回值是**相对路径轴的局部偏移**：x 分量沿路径前进方向（本公式恒为 0，不做纵向错位），
    /// y 分量垂直于路径方向（左右分布）。调用方（<see cref="FormationMovementDriver"/>）按当前
    /// 路径段方向把它旋转到世界坐标。
    /// </summary>
    public static class FormationFollowSlots
    {
        /// <summary>默认槽位间距。与 <c>SquadCommandSystem.DefaultArriveRadius</c>（1.2f）取相近
        /// 但不同的一个新常量，不复用它——见 D3。</summary>
        public const float DefaultSpacing = 1.5f;

        /// <summary>
        /// 成员按索引左右交替分布：偶数索引在一侧，奇数索引在另一侧，偏移量以
        /// <paramref name="spacing"/> 为步长线性增长（0, +1, -1, +2, -2, ...）。
        ///
        /// <paramref name="memberCount"/> 当前实现不参与计算——纯按索引奇偶 + 序号排布已经
        /// 满足"阵位调整"的需求，不需要按总数居中。保留这个参数是为了签名与未来可能的密度/
        /// 居中调整对齐，不破坏调用方约定。
        /// </summary>
        public static float2 ComputeOffset(int memberIndex, int memberCount, float spacing = DefaultSpacing)
        {
            _ = memberCount;
            if (memberIndex <= 0)
            {
                return float2.zero;
            }

            int rank = (memberIndex + 1) / 2; // 1,1,2,2,3,3...
            float side = memberIndex % 2 == 1 ? 1f : -1f; // 奇数一侧，偶数另一侧
            return new float2(0f, side * rank * spacing);
        }
    }
}
