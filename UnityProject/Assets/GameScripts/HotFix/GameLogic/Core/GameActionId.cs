namespace GameLogic.Core
{
    /// <summary>
    /// ER2-INPUT-01：统一输入域表的逻辑动作 ID。ERD 要求的物理键（WASD/方向键、左右键、E、
    /// Space、Tab、M、1～9、Ctrl+1～9、Esc）在这里各对应一个可重绑的逻辑动作；玩法代码一律读
    /// <see cref="InputBindingSet"/> 解析出的物理键，不再写字面量 <c>KeyCode.XXX</c>。
    ///
    /// 域（<see cref="InputScope"/>）与"这个键归谁"是正交的两件事：本枚举只回答"这个动作默认绑
    /// 在哪个物理键上"，由哪个域消费仍由调用方在 <see cref="InputRouter.ConsumeAction"/> 时指定。
    /// </summary>
    public enum GameActionId : byte
    {
        MoveForward = 0,
        MoveBack = 1,
        MoveLeft = 2,
        MoveRight = 3,

        /// <summary>Direct 直控技能槽 0——冲刺。默认键从 Space 挪到 LeftShift，见
        /// <see cref="InputBindingSet.GetHardcodedDefault"/> 注释：运行时靠 Direct/Strategy 域互斥，
        /// 冲刺（Direct）与暂停（Strategy，<see cref="TogglePause"/>）共用 Space 并不会真的抢键，
        /// 但会让重绑面板的"全局唯一键"冲突提示自相矛盾，因此默认键分开。</summary>
        DirectSkillSlot0 = 4,
        DirectSkillSlot1 = 5,
        DirectSkillSlot2 = 6,
        DirectSkillSlot3 = 7,
        DirectSkillSlot4 = 8,

        /// <summary>E：世界交互（Direct 域）。归还谷地的 E 交互正式实现属于 ER5-INT-01/UI-04，
        /// 本 Story 只保证这个键统一经过 <see cref="InputRouter"/> 且可重绑。</summary>
        Interact = 9,

        /// <summary>Tab：循环切换当前受控/接管目标（Direct 域）。</summary>
        CycleControlTarget = 10,

        /// <summary>M：战略/直控视角切换（全局键，见 <see cref="InputRouter.ConsumeGlobalAction"/>）。</summary>
        ToggleCameraView = 11,

        /// <summary>Space：暂停/恢复（全局键）。AC-UI-005：暂停恢复此前速度。</summary>
        TogglePause = 12,

        /// <summary>Esc：关闭当前面板/取消（全局键，模态期间也放行）。</summary>
        Cancel = 13,

        Group1 = 20, Group2 = 21, Group3 = 22, Group4 = 23, Group5 = 24,
        Group6 = 25, Group7 = 26, Group8 = 27, Group9 = 28,

        /// <summary>战略平移左右键（方向键左右，独立于 WASD 的 A/D，供手柄式单手操作）。</summary>
        StrategyPanLeft = 30,
        StrategyPanRight = 31,

        /// <summary>ER5-CMD-01：战略命令快捷键（Strategy 域，配合 GameLogic.Campaign.Regions 下的
        /// RegionSquadCommandSystem）。Move/Attack 是"武装待命，下一次左键点击世界确认目标"；
        /// Guard/Retreat 目标固定（分别为"当前位置"/"区域安全点"），按下即立即下达（或战略暂停下
        /// 排队）。这四个是战术固定键，不纳入 <see cref="InputBindingSet.RebindableActions"/>
        /// （同 WASD 的范围裁剪理由）。</summary>
        CommandMove = 32,
        CommandAttack = 33,
        CommandGuard = 34,
        CommandRetreat = 35,
    }
}
