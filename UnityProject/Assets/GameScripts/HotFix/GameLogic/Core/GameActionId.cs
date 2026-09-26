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

        /// <summary>E：世界交互（Direct 域 = FG 的“接入”上下文）。</summary>
        Interact = 9,

        /// <summary>Tab：循环切换当前受控/接管目标（Direct 域）。</summary>
        CycleControlTarget = 10,

        /// <summary>战略 / 接入视角切换（FG13“接入 / 退出”，FG0-UX-01 起默认 V；全局键，见 <see cref="InputRouter.ConsumeGlobalAction"/>）。</summary>
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
        /// 排队）。FG0-UX-01 起全部可重绑；默认键为与 FG13 第 5 节合并后的 R/T/G/Z。</summary>
        CommandMove = 32,
        CommandAttack = 33,
        CommandGuard = 34,
        CommandRetreat = 35,
        /// <summary>ER8（DEBT-ER6LOOP01-01）：打开/关闭任务日志与战役地图。FG0-UX-01 起默认 L（FG13 第 5 节）。</summary>
        ToggleMissionLog = 36,

        // ── FG0-UX-01（FG13 第 5 节 / FGR-ARC-012）：其余全部玩家动作。只追加、不重排——
        // 设置 JSON 按整数存动作 ID。默认键、所属上下文、是否已接入玩法都在 fg.TbInputAction，
        // 自检核对“本枚举的每个成员 == 表里的一行”，多一个少一个都失败。
        StrategyPanUp = 40,
        StrategyPanDown = 41,
        ZoomIn = 42,
        ZoomOut = 43,
        FocusHomeCore = 44,
        FollowSelection = 45,
        SpeedHalf = 46,
        SpeedNormal = 47,
        SpeedDouble = 48,
        SpeedTriple = 49,
        PrimaryAction = 50,
        SecondaryAction = 51,
        GroupAssign1 = 52, GroupAssign2 = 53, GroupAssign3 = 54, GroupAssign4 = 55, GroupAssign5 = 56,
        GroupAssign6 = 57, GroupAssign7 = 58, GroupAssign8 = 59, GroupAssign9 = 60,
        OpenBuildMenu = 61,
        Hotbar1 = 62, Hotbar2 = 63, Hotbar3 = 64, Hotbar4 = 65, Hotbar5 = 66,
        Hotbar6 = 67, Hotbar7 = 68, Hotbar8 = 69, Hotbar9 = 70, Hotbar10 = 71,
        Rotate = 72,
        DemolishMode = 73,
        Eyedropper = 74,
        UpgradePlan = 75,
        Copy = 76,
        Paste = 77,
        Undo = 78,
        Redo = 79,
        LayoutLibrary = 80,
        ToggleOverlay = 81,
        JumpHome = 82,
        JumpPreviousMachine = 83,
        OpenMap = 84,
        OpenRoster = 85,
        OpenResearch = 86,
        OpenFirmware = 87,
        OpenCodex = 88,
        OpenIntel = 89,
        ToggleNotificationCenter = 90,
        UiConfirm = 91,
        PinTooltip = 92,
        QuickSave = 93,
        QuickLoad = 94,
        /// <summary>FG0-ARCH-01（FGR-ARC-002）：在家园、远征地点、行进中的突袭之间依次飞跃镜头（战略上下文，默认 Tab）。</summary>
        CycleWorldFocus = 95,
    }
}
