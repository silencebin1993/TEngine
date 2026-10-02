namespace GameLogic.Campaign.Feedback
{
    /// <summary>ER8-CONTENT-01 / AC-AUD-001：玩家反馈时刻的稳定 id。每个 id 在
    /// <see cref="FeedbackCueCatalog"/> 里有且只有一行定义（音效 + 字幕条 + 语气），玩法代码只传 id，
    /// 不直接碰 <c>GameModule.Audio</c>，也不自己拼提示文字的类别标签。
    ///
    /// 只追加、不重排：数值会被 <see cref="FeedbackCues"/> 当数组下标用于节流计时。</summary>
    public enum FeedbackCueId
    {
        None = 0,

        // ── AC-AUD-001 点名的十一类（每类都必须同时有声音与非声音反馈） ──
        Takeover,
        Denied,
        PowerLost,
        PowerRestored,
        SignalLost,
        SignalRestored,
        StorageFull,
        ProductionComplete,
        Failure,
        ExpeditionWiped,
        CoreDestroyed,
        AnalysisComplete,
        ReactionMarkJump,
        ReactionMeltOverload,
        BossPhase,
        BossLockoutWarning,
        BossLockoutActive,
        BossDestroyed,
        BeaconLaunch,
        Victory,

        // ── 战斗与远征 ──
        WeaponFire,
        CannonCharge,
        CannonFire,
        EnemyHit,
        ArmorHit,
        EnemyDestroyed,
        EnemyAttack,
        MachineDamaged,
        MachineDestroyed,
        WeaponOverheat,
        Pickup,
        ExpeditionDepart,
        Evacuate,

        // ── 家园与通用 ──
        BuildComplete,
        SaveComplete,
        CommandAck,
        UiClick,

        // ── 战役目标（DEBT-ER6LOOP01-01） ──
        ObjectiveActivated,
        ObjectiveComplete,
        // ── 家园软锁救援（ER8-NEG-01：此前紧急救援机出现时没有任何提示） ──
        EmergencyRescue,
        // ── 存档（FG0-SAVE-01：读档时内容迁移的通知——已移除内容转成废料等） ──
        SaveContentMigrated,
        // ── 信号链路（FG1-SIG-04：接近信号覆盖边缘、静默夜将至的预警；断链本身用 SignalLost） ──
        SignalLinkWarning,
        // ── 接入（FG1-HUD-01 / FG-GAP-044：接入提交用 Takeover，信号离开机器用 UplinkLeave；形变随之出声，同类按最小间隔限流） ──
        UplinkLeave,
        // ── 具名标签反应（FG2-FW-03：开放命名的反应触发时报出机械名；聚合 / 每秒上限 / 慢放归 FG2-FW-04） ──
        TagReaction,
        // ── 读法生成（FG2-FW-04 承接 DEBT-FG2FW02-02：区域展开 / 回波 / 无人机出动的音效，每步聚合、同类限流） ──
        ReadingZone,
        ReadingEcho,
        ReadingDrone,
        // ── FG2-VFX-02：尖刺外装反伤（被近身攻击时把伤害反弹给攻击者） ──
        ReadingThorns,
        // ── FG4-ECO-10（FGR-ECO-070）：家园机器少于 2 台时归还核心免费打印一台搬运机（沿用 ER3-SOFTLOCK-01 的“核心应急”通知类型） ──
        EmergencyPrint,

        Max,
    }

    /// <summary>字幕条显示策略。Always＝事件通知，任何设置下都显示（这是 AC-AUD-001 的“非声音反馈”，
    /// 不能被字幕开关关掉）；SubtitlesOnly＝只在“字幕”开启时显示的声音字幕（战斗音等高频声音）；
    /// None＝只发声，没有文字（例如命令确认音，画面上已有选择框/路径线等价反馈）。</summary>
    public enum FeedbackCaptionMode
    {
        None = 0,
        Always = 1,
        SubtitlesOnly = 2,
    }

    /// <summary>字幕条语气：只决定边框颜色这一“第二通道”，类别本身由文字标签表达，
    /// 不靠颜色区分（AC-ACC-002）。</summary>
    public enum FeedbackTone
    {
        Info = 0,
        Good = 1,
        Warning = 2,
        Danger = 3,
    }
}
