namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// story-010 J3：组合弹道瞄准指示器可替换边界。
    ///
    /// 与 <see cref="IComposeProjectileFeedback"/> 并列：那边显示已开火的弹道，
    /// 这边显示"即将开火"的预览——玩家换装配、敌人蓄力时都需要实时预测下一发形状。
    /// 默认实现 <see cref="WhiteboxComposeAimIndicator"/>；未来接精美美术时换注入。
    ///
    /// J4 纪律：指示器**只读** seed，**禁止自增或写回**——写了会让真实开火的随机数漂掉，
    /// 不报错、只表现错。seed 由 <see cref="MetabolicSlice.Combat.MetabolicSliceBridge"/> 持有。
    /// </summary>
    public interface IComposeAimIndicatorFeedback
    {
        /// <summary>显示玩家当前装配的预测指示器（每次装配变更后调用一次）。</summary>
        void ShowPlayerIndicator(int seed);

        /// <summary>隐藏指示器。</summary>
        void Hide();

        /// <summary>每帧推进（如需动画）。</summary>
        void Tick(float dt);
    }
}
