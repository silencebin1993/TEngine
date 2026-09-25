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
