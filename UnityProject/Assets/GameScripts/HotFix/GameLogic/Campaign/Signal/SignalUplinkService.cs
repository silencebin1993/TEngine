using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.View;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>FG1-SIG-03（FGR-SIG-030）：发起接入被拒绝的原因（稳定码，测试与界面共用；文本见 signal.uplink.reason.*）。</summary>
    public enum UplinkFailure : byte
    {
        None = 0,
        NoCampaign,
        /// <summary>按接入键时没有选中机器。</summary>
        NoSelection,
        NotFound,
        /// <summary>目标阵亡。</summary>
        Dead,
        /// <summary>目标所在的地点没有在运行（远征已结束 / 还没出发）。FG1-SIG-07 起跨地点的接入就是远距离跳转，不再要求镜头先切过去。</summary>
        OtherSite,
        /// <summary>超出与归还核心连通的信号覆盖（覆盖网络是 FG1-SIG-07；本 Story 留接口 <see cref="SignalUplinkService.CoverageProvider"/>）。</summary>
        OutOfCoverage,
        /// <summary>目标在干扰场里。</summary>
        Jammed,
        /// <summary>静默夜（FG1-SIG-04 预留接口 / FG07 昼夜）。</summary>
        SilentNight,
        /// <summary>目标还在装配站里。</summary>
        InFactory,
        /// <summary>目标在维修台上（维修流程在 FG04；本 Story 留接口）。</summary>
        OnRepairBay,
        /// <summary>目标正在投送途中（投送在 FG08；本 Story 留接口）。</summary>
        InDelivery,
        /// <summary>有面板挡着（确认框、暂停菜单、信号核面板……）。战略暂停本身不算。</summary>
        ModalBlocked,
        /// <summary>上一次接入的过渡还没完成。</summary>
        Busy,
        /// <summary>信号已经在这台机器里。</summary>
        AlreadyUplinked,
        /// <summary>Tab 循环时这里没有别的机器可以切换。</summary>
        NoCandidate,
        /// <summary>镜头正在拉回战略视角（刚按了退出键 / 点了通知飞走）：落地后再接入。否则这次接入会在镜头落地时被当成“玩家退出”取消，
        /// 低帧率下还可能先接进去、落地时又立刻离开（白白取消了目标的移动命令）。</summary>
        ViewLeaving,
        /// <summary>FG1-SIG-07（FGR-SIG-052）：远距离跳转（超过 signal.jump.far_distance_cells 格或跨地点）还在冷却。跳回家园不受限制。</summary>
        JumpCooldown,
        /// <summary>FG1-SIG-07（FGR-SIG-051）：按“跳回上一台机器”时还没有上一台。</summary>
        NoPrevious,
    }

    /// <summary>发起接入的入口（反馈与统计用）。</summary>
    public enum UplinkSource : byte
    {
        /// <summary>选中机器后按“接入 / 退出接入”键（默认 V，可重绑）。</summary>
        Hotkey = 0,
        /// <summary>接入中按“循环切换机器”（默认 Tab，可重绑）。</summary>
        Cycle = 1,
        /// <summary>在机器列表里直接点一台机器。</summary>
        MachineList = 2,
        /// <summary>FG1-SIG-07：按“跳回上一台机器”（默认 J，可重绑）或点 HUD 的“上一台”。</summary>
        JumpPrevious = 3,
    }

    /// <summary>接入过渡被取消的原因。</summary>
    public enum UplinkCancelReason : byte
    {
        None = 0,
        /// <summary>过渡途中目标阵亡（第 5 章第 1 行：取消接入，信号留在原处，给原因）。</summary>
        TargetDead,
        /// <summary>过渡途中目标变得不可接入（进了干扰场、被送回工厂、地点被卸载、镜头飞走……）。</summary>
        TargetUnavailable,
        /// <summary>玩家自己取消（Esc、过渡中又按了退出键把镜头拉回战略）。</summary>
        PlayerCancelled,
    }

    public readonly struct UplinkRequestResult
    {
        public readonly bool Accepted;
        public readonly UplinkFailure Failure;
        public readonly int LogicId;
        public readonly string Text;
        /// <summary>战略暂停中发起：已确认目标，恢复运行后才开始过渡。</summary>
        public readonly bool WaitsForResume;

        public UplinkRequestResult(bool accepted, UplinkFailure failure, int logicId, string text, bool waitsForResume)
        {
            Accepted = accepted;
            Failure = failure;
            LogicId = logicId;
            Text = text;
            WaitsForResume = waitsForResume;
        }
    }

    /// <summary>跨模块事件 <see cref="SignalUplinkService.UplinkChangedEvent"/> 的载荷：信号从哪台机器到了哪台机器。
    /// 形变表现（FG1-VFX-01）、接入 HUD（FG1-HUD-01）订阅它；<see cref="MorphActive"/> = 新机器插进了至少一枚生效的固件（机身该变形）。</summary>
    public struct SignalUplinkChange
    {
        public int PreviousLogicId;
        public int CurrentLogicId;
        public string SiteId;
        public RegionControlChangeReason Reason;
        /// <summary>新机器接入口里生效的固件（离开 / 回到核心时为空）。</summary>
        public string[] InsertedFirmwareIds;
        public bool MorphActive;
    }

    /// <summary>跨模块事件 <see cref="SignalUplinkService.CoreFirmwareFiredEvent"/> 的载荷：一枚核心固件发动、冷却开始（暴露计入在 FG1-SIG-06 订阅）。</summary>
    public struct CoreFirmwareFired
    {
        public int LogicId;
        public string FirmwareId;
        public double ReadyAtGameSeconds;
    }

    /// <summary>
    /// FG1-SIG-03 接入、重编译、离开与防刷（FG01 FGR-SIG-030～034；第 5 章负向矩阵；第 6 章存档）。
    ///
    /// ── 状态真相 ──
    /// 信号在哪台机器里：<see cref="SignalCoreState.UplinkMachineLogicId"/>（存档）。唯一写入口是 <see cref="OnControlChanged"/>——
    /// 区域接管系统（<see cref="RegionControlSystem"/>）的每一次变更（接入完成、Tab 切换、离开、阵亡回弹、失联、地点卸载）都经它同步，
    /// 所以“谁在开这台机器”和“信号在哪里”不会分叉。核心固件冷却：<see cref="SignalCoreState.CoreCooldowns"/>（信号侧，存档）。
    /// 热量、过载、武器冷却：在机体上（战斗内核单位，存档快照）——接入 / 离开只换武器参数的行号，不碰机体状态（FGR-SIG-033）。
    ///
    /// ── 接入过程（FGR-SIG-031）──
    /// <see cref="Request"/> 校验目标（逐条原因，FGR-SIG-030）→ 进入“过渡”：镜头过渡 0.35 秒（fg.TbHomeTuning signal.uplink.transition_seconds，
    /// 真实时间，战略暂停中不走），期间冻结冲突输入 → <see cref="FrameTick"/> 到点提交：区域接管系统占用目标 → 本类把信号位置写进存档状态、
    /// 通知两台机器重编译（离开的回到本地配置，接入的插入信号核固件，<see cref="MachineLoadoutRegistry.ResolveForPilot"/>）→ 发出变更事件（形变）。
    /// 过渡途中目标阵亡 / 不可接入 → 取消，信号留在原处（第 5 章）。战略暂停中发起 → 目标已确认，恢复运行后才开始过渡。
    ///
    /// ── 防刷（FGR-SIG-034）──
    /// 没有任何“插入瞬间触发”的效果：插入只改武器参数的行号；机体的热量、武器冷却、瞄准进度、血量都留在机体上；
    /// 核心固件冷却在信号上、按游戏时间走，离开再接入、换机器都不会重置；冷却中这条固件照样插着（机身形变不闪），只是它的反应不发动。
    ///
    /// 开销：请求 / 提交 / 离开 O(1) 次装配解析（重编译 ≤ 2 ms，与机器总数无关，FG01 第 7 章）；每帧 O(1)；每个模拟步 O(信号核槽位数)。
    /// </summary>
    public static class SignalUplinkService
    {
        public const string UplinkChangedEvent = "SignalUplink.Changed";
        public const string CoreFirmwareFiredEvent = "SignalUplink.CoreFirmwareFired";

        // ── 预留接口（FGR-SIG-030 的前置条件；对应系统在后续 Story 落地，见 DEBT-FG1SIG03-01～03）──

        /// <summary>目标在不在与归还核心连通的信号覆盖里（FG1-SIG-07 覆盖网络接入）。null = 都在覆盖里。</summary>
        public static Func<int, bool> CoverageProvider;
        /// <summary>现在是不是静默夜（FG1-SIG-04 预留接口，FG07 昼夜接入）。null = 不是。</summary>
        public static Func<bool> SilentNightProvider;
        /// <summary>目标是不是在维修台上（FG04 维修流程接入）。null = 不在。</summary>
        public static Func<int, bool> OnRepairBayProvider;
        /// <summary>目标是不是在投送途中（FG08 投送接入）。null = 不在。</summary>
        public static Func<int, bool> InDeliveryProvider;

        /// <summary>任何状态变化 +1（界面据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        // ── 统计（自检用：连按、防刷、重编译次数）──
        public static int AcceptedCount { get; private set; }
        public static int RejectedCount { get; private set; }
        public static int CommitCount { get; private set; }
        public static int LeaveCount { get; private set; }
        public static int CancelCount { get; private set; }
        public static int RecompileNotifyCount { get; private set; }
        public static int CoreFiredCount { get; private set; }
        public static UplinkFailure LastFailure { get; private set; }
        public static UplinkCancelReason LastCancel { get; private set; }
        /// <summary>最近一次有核心固件冷却到期的模拟步（游戏秒；自检核对“各档倍速下到期的游戏时刻一致”）。</summary>
        public static double LastCooldownExpiryGameSeconds { get; private set; } = -1;

        /// <summary>接入中改了信号核（在家园按 P 装卸 / 换位 / 切预设）时，要让接入的机器重新插入——记下上次同步时的信号核版本。</summary>
        private static int _coreRevisionSeen = -1;

        // ── FG1-SIG-07 远距离跳转（FGR-SIG-051、052）──
        /// <summary>这次过渡是远距离跳转（超过门槛或跨地点）：过渡 1.5 秒、提交后开始冷却。</summary>
        private static bool _pendingFar;
        /// <summary>这次跳转的距离（格；跨地点为 +∞），状态行显示用。</summary>
        private static float _pendingDistance;
        /// <summary>“跳回家园”的远距离过渡（信号从远处的机器回到归还核心），与接入过渡互斥。</summary>
        private static bool _pendingHome;
        private static float _pendingHomeRemaining;
        private static bool _pendingHomeWaitsResume;
        /// <summary>发起“跳回家园”时信号所在的机器：到点时信号已不在它里面（按 V 离开 / 阵亡 / 断链已回到核心），只把镜头飞回家园、不开始冷却。</summary>
        private static int _pendingHomeFrom;

        /// <summary>远距离跳转次数、跳回家园次数（自检 / 冒烟读取）。</summary>
        public static int FarJumpCount { get; private set; }
        public static int JumpHomeCount { get; private set; }
        public static bool IsJumpingHome => _pendingHome;
        /// <summary>有 Esc 能取消的过渡：接入 / 远距离跳转，或“跳回家园”的远距离过渡（HUD 据此在取消栈里压一层，<see cref="CancelByPlayer"/> 两种都处理）。</summary>
        public static bool HasCancellableTransition => _pendingTarget != 0 || _pendingHome;
        public static float JumpHomeRemaining => _pendingHome ? _pendingHomeRemaining : 0f;
        public static bool PendingIsFar => _pendingTarget != 0 && _pendingFar;
        public static float PendingDistance => _pendingTarget != 0 ? _pendingDistance : 0f;

        // ── 过渡（运行时，不进存档：过渡没完成时存档，读回来信号在原处）──
        private static int _pendingTarget;
        private static int _pendingOrigin;
        private static string _pendingSite;
        private static float _pendingRemaining;
        private static bool _pendingWaitsResume;
        private static bool _pendingSawDirect;
        private static UplinkSource _pendingSource;
        /// <summary>发起时校验通过的目标表现对象：过渡中每帧复核目标时 O(1) 核对它仍有效，不再每帧线性查本地点机器列表（B18；暂停中挂起的接入可以持续任意长）。</summary>
        private static HomeValleyMachineMarker _pendingMarker;

        public static bool IsPending => _pendingTarget != 0;
        public static int PendingTargetLogicId => _pendingTarget;
        public static int PendingOriginLogicId => _pendingOrigin;
        public static bool PendingWaitsForResume => _pendingTarget != 0 && _pendingWaitsResume;
        public static float PendingRemaining => _pendingTarget != 0 ? _pendingRemaining : 0f;
        /// <summary>这次接入是从哪个入口发起的（接入键 / Tab / 机器列表）。</summary>
        public static UplinkSource PendingSource => _pendingSource;

        // ── 最近一条反馈（HUD 状态行显示 3 秒；声音与字幕另走 FeedbackCues）──
        private static string _feedback = string.Empty;
        private static float _feedbackUntil;

        /// <summary>状态行里上一条反馈显示多久（真实秒，fg.TbUiTuning signal.uplink_feedback_seconds；缺表时 3 秒）。</summary>
        private static float FeedbackSeconds => UiTuningValues.TryGet("signal.uplink_feedback_seconds", out float v) && v > 0f ? v : 3f;
        /// <summary>自检注入真实时间（HUD 反馈的显示时长）；为 null 时读 <see cref="Time.unscaledTime"/>。</summary>
        public static Func<float> RealTimeForTests;
        private static float Now => RealTimeForTests?.Invoke() ?? Time.unscaledTime;
        /// <summary>真实时间（HUD 反馈时长、预警防刷；自检可注入）。</summary>
        public static float RealNow => Now;

        public static string LastFeedbackText => _feedback;

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>接入过渡秒数（FGR-SIG-031 沿用 0.35 秒）。</summary>
        public static float TransitionSeconds
        {
            get
            {
                if (GridContent.TryGetTuning("signal.uplink.transition_seconds", out float v) && v >= 0f)
                {
                    return v;
                }
                if (WarnedTuning.Add("signal.uplink.transition_seconds"))
                {
                    Log.Error("[SignalUplinkService] fg.TbHomeTuning 缺少 signal.uplink.transition_seconds，暂用规格初值 0.35（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
                }
                return 0.35f;
            }
        }

        /// <summary>FG1-SIG-07（FGR-SIG-051）：远距离跳转的距离门槛（格，初值 500）。</summary>
        public static float FarDistanceCells => JumpTuning("signal.jump.far_distance_cells", 500f);
        /// <summary>远距离跳转的过渡（真实秒，初值 1.5；战略暂停中不走）。</summary>
        public static float FarTransitionSeconds => JumpTuning("signal.jump.far_transition_seconds", 1.5f);
        /// <summary>远距离跳转的冷却（游戏秒，初值 10；FGR-SIG-052）。</summary>
        public static float FarCooldownSeconds => JumpTuning("signal.jump.far_cooldown_seconds", 10f);

        private static float JumpTuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v) && v > 0f)
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[SignalUplinkService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>远距离跳转冷却还剩多少游戏秒（0 = 可以跳）。冷却记在信号上并进存档（FG01 第 6 章“远距离跳转冷却”）。</summary>
        public static double JumpCooldownRemaining(CampaignState s)
        {
            long ready = s?.SignalCore?.JumpCooldownReadyTick ?? 0;
            return Math.Max(0, (ready - GameClock.Ticks) / (double)GameClock.StepHz);
        }

        /// <summary>
        /// 信号现在在哪（跳转的起点）：在机器里 = 那台机器所在的地点与实时位置；在归还核心 = 家园所在的星球表面与核心位置（FGR-SIG-002“信号同一时刻只在一处”）。
        /// </summary>
        public static void SignalOrigin(CampaignState s, out string siteId, out Vector2 position)
        {
            int id = CurrentMachine(s);
            if (id != 0 && MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null)
            {
                siteId = rec.RegionId;
                position = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : rec.WorldPosition;
                return;
            }
            siteId = HomeValleyLayout.RegionId;
            position = CorePosition(s);
        }

        /// <summary>归还核心的位置（核心建筑记录的几何中心；没有记录时按开局布局锚点）。</summary>
        public static Vector2 CorePosition(CampaignState s)
        {
            BuildingRecord[] b = s?.BuildingRecords;
            if (b != null)
            {
                for (int i = 0; i < b.Length; i++)
                {
                    if (b[i] != null && b[i].BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                    {
                        return b[i].Position;
                    }
                }
            }
            return HomeValleyLayout.Core.Position;
        }

        /// <summary>
        /// FG1-SIG-07（FGR-SIG-051）：从信号现在的位置跳到 <paramref name="toSite"/> 的 <paramref name="to"/> 算不算远距离：跨地点一律算（距离 +∞），
        /// 同一地点按直线距离与门槛比较。
        /// </summary>
        public static bool IsFarJump(CampaignState s, string toSite, Vector2 to, out float distance)
        {
            SignalOrigin(s, out string fromSite, out Vector2 from);
            if (fromSite != toSite)
            {
                distance = float.PositiveInfinity;
                return true;
            }
            distance = Vector2.Distance(from, to);
            return distance > FarDistanceCells;
        }

        // ─────────────────────────────── 查询 ───────────────────────────────

        /// <summary>信号所在机器的 LogicId；0 = 在归还核心。</summary>
        public static int CurrentMachine(CampaignState s) => s?.SignalCore != null ? s.SignalCore.UplinkMachineLogicId : 0;

        public static bool IsUplinked(CampaignState s, int logicId) => logicId > 0 && CurrentMachine(s) == logicId;

        /// <summary>过渡中、且已经开始走（不在等恢复运行）时，镜头该对准的目标（区域控制器的直控锚点读它）。</summary>
        public static bool TryGetPendingAnchorTarget(string siteId, out int logicId)
        {
            logicId = 0;
            if (_pendingTarget == 0 || _pendingWaitsResume || _pendingSite != siteId)
            {
                return false;
            }
            logicId = _pendingTarget;
            return true;
        }

        /// <summary>某条核心固件还要冷却多少游戏秒（0 = 可以发动）。</summary>
        public static double CooldownRemaining(CampaignState s, string firmwareId)
        {
            SignalCoreCooldownRecord[] cds = s?.SignalCore?.CoreCooldowns;
            if (cds == null || string.IsNullOrEmpty(firmwareId))
            {
                return 0;
            }
            for (int i = 0; i < cds.Length; i++)
            {
                if (cds[i] != null && cds[i].ContentId == firmwareId)
                {
                    return GameClock.SecondsUntil(cds[i].ReadyTick);
                }
            }
            return 0;
        }

        /// <summary>这台机器接入口里**生效**的固件（信号不在这台机器里时为空）。</summary>
        public static string[] EffectiveInserted(CampaignState s, int logicId)
        {
            if (!IsUplinked(s, logicId))
            {
                return Array.Empty<string>();
            }
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, logicId, s.RandomSeed);
            return r.Success && r.Preview?.UplinkFirmwareIds != null ? r.Preview.UplinkFirmwareIds : Array.Empty<string>();
        }

        /// <summary>
        /// 战斗内核翻译武器参数时问：这台机器这条反应现在该不该发动。只有“信号带进来的核心固件正在冷却”时为 true——
        /// 冷却中固件照样插着（形变不闪），只是它的反应暂不发动（FGR-SIG-033：冷却属于信号，换机器不重置）。
        /// 核心固件放不进机器电路（FGR-SIG-012），所以这条反应只可能来自接入口。
        /// </summary>
        public static bool IsReactionSuppressed(CampaignState s, int logicId, string reactionId)
        {
            if (string.IsNullOrEmpty(reactionId) || !IsUplinked(s, logicId))
            {
                return false;
            }
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
            return fw != null && FirmwareKinds.IsCore(fw) && CooldownRemaining(s, fw) > 0;
        }

        /// <summary>
        /// 战斗内核翻译武器参数时问：这条反应是不是“信号带进来的、有冷却的核心固件”发动的（<paramref name="uplinkFirmwareIds"/> = 接入口里生效的固件）。
        /// 是 → 内核单位带 <c>CombatUnitFlags.ReactionGated</c>：反应一发动就当场压住，直到这里按信号上的冷却重新下发参数。
        /// 与 <see cref="OnReactionFired(CampaignState,int,string,bool)"/> 的结算条件一致（核心、插在接入口里、冷却 &gt; 0）。
        /// </summary>
        public static bool IsCoreGatedReaction(string reactionId, string[] uplinkFirmwareIds)
        {
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
            // FG5-RND-04：接入口里的核心混合固件按父固件算（含“过载”的混合固件同样受过载的冷却门控）。
            return fw != null && uplinkFirmwareIds != null && FirmwareKinds.ExpandMixed(uplinkFirmwareIds).Contains(fw)
                   && FirmwareKinds.IsCore(fw) && FirmwareKinds.CoreCooldownSeconds(fw) > 0f;
        }

        // ─────────────────────────────── 发起接入（FGR-SIG-030）───────────────────────────────

        /// <summary>
        /// 目标能不能接入（逐条原因，FGR-SIG-030）。顺序：阵亡 → 静默夜 → 覆盖 → 不在当前地点 → 在工厂 → 维修台 → 投送途中 → 干扰场。
        /// 不判“已经在里面”“有面板挡着”“过渡中”——那是发起时的门槛（<see cref="Request"/>）。
        /// </summary>
        public static UplinkFailure Validate(CampaignState s, int logicId, out string text)
        {
            UplinkFailure f = ValidateCore(s, logicId);
            text = f == UplinkFailure.None ? null : FailureText(f, logicId);
            return f;
        }

        private static UplinkFailure ValidateCore(CampaignState s, int logicId) => ValidateCore(s, logicId, out _);

        private static UplinkFailure ValidateCore(CampaignState s, int logicId, out HomeValleyMachineMarker marker)
        {
            marker = null;
            if (s == null)
            {
                return UplinkFailure.NoCampaign;
            }
            if (logicId <= 0)
            {
                return UplinkFailure.NoSelection;
            }
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return UplinkFailure.NotFound;
            }
            UplinkFailure pre = TargetStateFailure(rec, logicId, null);
            if (pre != UplinkFailure.None)
            {
                return pre;
            }
            // FG1-SIG-07（FGR-SIG-051）：跨地点的接入就是远距离跳转——不再要求镜头先切到目标所在的地点，只要地点在运行、目标在里面。
            RegionControlSystem control = RegionControlSystem.ForRegion(rec.RegionId);
            if (control == null || !TryGetTargetMarker(control, rec.RegionId, logicId, out marker))
            {
                return UplinkFailure.OtherSite;
            }
            Vector3 p = marker.Position3;
            return TargetPlaceFailure(rec, logicId, control, new Vector2(p.x, p.z));
        }

        /// <summary>
        /// FG1-SIG-04（FGR-SIG-040 / FG-GAP-034）：死亡回弹的目标条件——与发起接入<b>同一套</b>目标条件（<see cref="TargetStateFailure"/> + <see cref="TargetPlaceFailure"/>，
        /// 以后给发起接入加条件就是加在这两处，回弹自动跟上），只少“地点正被观察 / 能找到表现对象”这一道发起门槛：
        /// 回弹发生在目标所在地点自己的接管系统里（调用方已持有它的表现对象与位置），不要求玩家正看着那里。
        /// </summary>
        public static UplinkFailure ReboundTargetFailure(RegionControlSystem control, int logicId, Vector2 position)
        {
            if (control == null)
            {
                return UplinkFailure.OtherSite;
            }
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return UplinkFailure.NotFound;
            }
            UplinkFailure pre = TargetStateFailure(rec, logicId, position);
            return pre != UplinkFailure.None ? pre : TargetPlaceFailure(rec, logicId, control, position);
        }

        /// <summary>目标条件前半（与地点是否被观察无关）：阵亡 → 静默夜 → 覆盖。<paramref name="position"/> 为空时取机器的实时位置。</summary>
        private static UplinkFailure TargetStateFailure(MachineRecord rec, int logicId, Vector2? position)
        {
            if (!rec.IsAlive)
            {
                return UplinkFailure.Dead;
            }
            if (SilentNightProvider != null && SilentNightProvider())
            {
                return UplinkFailure.SilentNight;
            }
            // FG1-SIG-04：覆盖判定默认走信号覆盖（归还核心 + 运转且有电的信号塔，SignalCoverageService）；CoverageProvider 仍可整体替换（自检 / FG1-SIG-07）。
            bool covered = CoverageProvider != null
                ? CoverageProvider(logicId)
                : (position.HasValue ? SignalCoverageService.Sample(rec.RegionId, position.Value, logicId) : SignalCoverageService.SampleMachine(logicId)).Covered;
            return covered ? UplinkFailure.None : UplinkFailure.OutOfCoverage;
        }

        /// <summary>目标条件后半（目标在它所在地点里的处境）：在工厂 → 维修台 → 投送途中 → 干扰场。</summary>
        private static UplinkFailure TargetPlaceFailure(MachineRecord rec, int logicId, RegionControlSystem control, Vector2 position)
        {
            if (rec.IsInFactory)
            {
                return UplinkFailure.InFactory;
            }
            // FG4-ECO-06（DEBT-FG1SIG03-01）：机器维修规则把机器送上维修台、正在修理时拒绝接入（注入的判定留给自检）。
            if ((OnRepairBayProvider != null && OnRepairBayProvider(logicId)) || Economy.StandingRuleService.IsOnRepairBay(CampaignSession.Current, logicId))
            {
                return UplinkFailure.OnRepairBay;
            }
            if (InDeliveryProvider != null && InDeliveryProvider(logicId))
            {
                return UplinkFailure.InDelivery;
            }
            return control.IsJammedAt(position) ? UplinkFailure.Jammed : UplinkFailure.None;
        }

        /// <summary>
        /// 目标的表现对象。过渡中复核的正是发起时校验通过的那台：用记下的对象 O(1) 核对它仍有效、仍在同一地点（阵亡、进装配站、
        /// 被派去别的地点另有判定），不再每帧线性查本地点机器列表；其余情况（发起时、Tab 找下一台）走区域接管系统的查找。
        /// </summary>
        private static bool TryGetTargetMarker(RegionControlSystem control, string regionId, int logicId, out HomeValleyMachineMarker marker)
        {
            if (_pendingMarker != null && _pendingTarget == logicId && _pendingSite == regionId
                && _pendingMarker.LogicId == logicId && _pendingMarker.IsValid)
            {
                marker = _pendingMarker;
                return true;
            }
            return control.TryGetMarker(logicId, out marker);
        }

        /// <summary>自检：过渡中复核走的是缓存（没有每帧查机器列表）。</summary>
        public static bool PendingMarkerCached => _pendingTarget != 0 && _pendingMarker != null && _pendingMarker.LogicId == _pendingTarget;

        /// <summary>
        /// 发起接入（选中后按接入键 / 机器列表点一下 / 接入中 Tab）。成功 = 进入过渡（不是立刻接入）；
        /// <paramref name="startCamera"/> 时顺便请镜头过渡到直控（从镜头自己的“需要一个直控目标”钩子进来时传 false，由镜头自己开始过渡）。
        /// </summary>
        public static UplinkRequestResult Request(int logicId, UplinkSource source, bool startCamera = true)
        {
            CampaignState s = CampaignSession.Current;
            if (InputRouter.PanelModalOpen)
            {
                return Reject(UplinkFailure.ModalBlocked, logicId, source);
            }
            if ((_pendingTarget != 0 && !_pendingWaitsResume) || _pendingHome)
            {
                return Reject(UplinkFailure.Busy, _pendingTarget != 0 ? _pendingTarget : logicId, source);
            }
            CameraDirector view = WorldView.Director;
            if (view != null && view.IsBound && view.InTransition && view.TransitionTarget == ViewMode.Strategy)
            {
                // 刚按了退出键（或点通知飞走），镜头正拉回战略：落地时控制器会以“玩家退出”释放接管，这次接入不是被静默取消，
                // 就是在低帧率下先接进去、落地又立刻离开。明确拒绝，落地后再点。
                return Reject(UplinkFailure.ViewLeaving, logicId, source);
            }
            UplinkFailure f = ValidateCore(s, logicId, out HomeValleyMachineMarker marker);
            if (f == UplinkFailure.None && IsUplinked(s, logicId))
            {
                f = UplinkFailure.AlreadyUplinked;
            }
            if (f != UplinkFailure.None)
            {
                return Reject(f, logicId, source);
            }

            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec);
            // FG1-SIG-07（FGR-SIG-051、052）：超过门槛或跨地点 = 远距离跳转：过渡 1.5 秒（世界照常运行）、提交后冷却 10 游戏秒；冷却中拒绝（跳回家园不受限）。
            Vector3 targetPos3 = marker.Position3;
            bool far = IsFarJump(s, rec.RegionId, new Vector2(targetPos3.x, targetPos3.z), out float distance);
            if (far && JumpCooldownRemaining(s) > 0)
            {
                return Reject(UplinkFailure.JumpCooldown, logicId, source);
            }
            bool paused = GameClock.Paused;
            _pendingTarget = logicId;
            _pendingMarker = marker;
            _pendingSite = rec.RegionId;
            _pendingOrigin = CurrentMachine(s);
            _pendingFar = far;
            _pendingDistance = distance;
            _pendingRemaining = far ? FarTransitionSeconds : TransitionSeconds;
            _pendingWaitsResume = paused;
            _pendingSawDirect = false;
            _pendingSource = source;
            AcceptedCount++;
            LastFailure = UplinkFailure.None;
            Revision++;
            string label = SignalPresence.MachineLabel(logicId);
            string text = far && !paused ? PendingFarLine() : GameText.Format(paused ? "signal.uplink.pending_paused" : "signal.uplink.pending", label);
            SetFeedback(text);
            FeedbackCues.Raise(FeedbackCueId.CommandAck, null, FeedbackCues.MachineChassisSfx(logicId));
            if (far && rec.RegionId == HomeValleyLayout.RegionId)
            {
                WorldView.PinPlanet(new Vector2(targetPos3.x, targetPos3.z)); // 星球表面的远处：镜头范围先包含目标，离开接入后不被“已探索区域”钳回去。
            }
            if (startCamera && !paused && WorldView.IsObserved(rec.RegionId))
            {
                StartCamera();
            }
            return new UplinkRequestResult(true, UplinkFailure.None, logicId, text, paused);
        }

        /// <summary>接入中按 Tab：按 LogicId 稳定顺序切到下一台**能接入**的机器（跳过被干扰等不可接入的，不会卡在同一台上）。</summary>
        public static UplinkRequestResult RequestCycle()
        {
            CampaignState s = CampaignSession.Current;
            if ((_pendingTarget != 0 && !_pendingWaitsResume) || _pendingHome)
            {
                return Reject(UplinkFailure.Busy, _pendingTarget, UplinkSource.Cycle);
            }
            int from = _pendingTarget != 0 ? _pendingTarget : CurrentMachine(s);
            string region = MachineRegistry.TryGetRecord(from, out MachineRecord fromRec) && fromRec != null ? fromRec.RegionId : WorldView.ObservedSiteId;
            RegionControlSystem control = RegionControlSystem.ForRegion(region);
            if (control == null)
            {
                return Reject(UplinkFailure.NoCandidate, from, UplinkSource.Cycle);
            }
            int next = control.NextCandidateAfter(from == 0 ? (int?)null : from);
            UplinkFailure firstFailure = UplinkFailure.None;
            int firstFailed = 0;
            var tried = new HashSet<int>();
            while (next != 0 && next != from && tried.Add(next))
            {
                UplinkFailure f = ValidateCore(s, next);
                if (f == UplinkFailure.None)
                {
                    return Request(next, UplinkSource.Cycle);
                }
                if (firstFailure == UplinkFailure.None)
                {
                    firstFailure = f;
                    firstFailed = next;
                }
                next = control.NextCandidateAfter(next);
            }
            return firstFailure != UplinkFailure.None
                ? Reject(firstFailure, firstFailed, UplinkSource.Cycle)
                : Reject(UplinkFailure.NoCandidate, from, UplinkSource.Cycle);
        }

        /// <summary>选中机器后按接入键（区域控制器的“镜头需要直控目标”钩子）。返回 true = 镜头现在可以开始过渡。</summary>
        public static bool RequestFromSelection(int selectedLogicId)
        {
            if (selectedLogicId <= 0)
            {
                Reject(UplinkFailure.NoSelection, 0, UplinkSource.Hotkey);
                return false;
            }
            UplinkRequestResult r = Request(selectedLogicId, UplinkSource.Hotkey, startCamera: false);
            return r.Accepted && !r.WaitsForResume;
        }

        /// <summary>玩家取消还没完成的接入（Esc）。</summary>
        public static bool CancelByPlayer()
        {
            if (_pendingHome)
            {
                ClearPendingHome();
                CancelCount++;
                LastCancel = UplinkCancelReason.PlayerCancelled;
                SetFeedback(GameText.Get("signal.jump.cancelled_home"));
                FeedbackCues.Raise(FeedbackCueId.CommandAck);
                return true;
            }
            if (_pendingTarget == 0)
            {
                return false;
            }
            Cancel(UplinkCancelReason.PlayerCancelled, null);
            return true;
        }

        // ─────────────────────────────── FG1-SIG-07 跳回家园 / 上一台（FGR-SIG-051）───────────────────────────────

        /// <summary>
        /// “跳回家园”（默认 H，可重绑）：信号回到归还核心，镜头飞回家园。信号已在核心 → 只把镜头飞回家园。
        /// 信号在离核心不超过门槛的家园机器里 → 立即离开并飞回；更远（或在远征地点）→ 1.5 秒过渡（世界照常运行），到点离开并飞回，开始远距离跳转冷却。
        /// 跳回家园本身不受冷却限制（最稳妥：任何时候都能回家处理家园的事）。
        /// </summary>
        public static UplinkRequestResult RequestJumpHome()
        {
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                return Reject(UplinkFailure.NoCampaign, 0, UplinkSource.Hotkey);
            }
            if (InputRouter.PanelModalOpen)
            {
                return Reject(UplinkFailure.ModalBlocked, 0, UplinkSource.Hotkey);
            }
            if ((_pendingTarget != 0 && !_pendingWaitsResume) || _pendingHome)
            {
                return Reject(UplinkFailure.Busy, _pendingTarget, UplinkSource.Hotkey);
            }
            if (_pendingTarget != 0)
            {
                Cancel(UplinkCancelReason.PlayerCancelled, null); // 暂停中排着的接入：玩家改主意回家。
            }
            int id = CurrentMachine(s);
            if (id == 0)
            {
                WorldView.FocusHomeCore();
                JumpHomeCount++;
                string at = GameText.Get("signal.jump.home_camera");
                SetFeedback(at);
                FeedbackCues.Raise(FeedbackCueId.CommandAck);
                return new UplinkRequestResult(true, UplinkFailure.None, 0, at, false);
            }
            SignalOrigin(s, out string site, out Vector2 from);
            bool far = site != HomeValleyLayout.RegionId || Vector2.Distance(from, CorePosition(s)) > FarDistanceCells;
            if (!far)
            {
                CommitJumpHome(s, false);
                return new UplinkRequestResult(true, UplinkFailure.None, 0, _feedback, false);
            }
            _pendingHome = true;
            _pendingHomeFrom = id;
            _pendingHomeRemaining = FarTransitionSeconds;
            _pendingHomeWaitsResume = GameClock.Paused;
            AcceptedCount++;
            Revision++;
            string text = GameText.Format("signal.jump.pending_home", Seconds(_pendingHomeRemaining));
            SetFeedback(text);
            FeedbackCues.Raise(FeedbackCueId.CommandAck);
            return new UplinkRequestResult(true, UplinkFailure.None, 0, text, _pendingHomeWaitsResume);
        }

        /// <summary>“跳回上一台机器”（默认 J，可重绑）：跳回最近接入过、不是当前这台的机器（与发起接入同一套条件、远距离时同样过渡与冷却）。</summary>
        public static UplinkRequestResult RequestJumpPrevious()
        {
            CampaignState s = CampaignSession.Current;
            int prev = PreviousMachine(s);
            if (prev == 0)
            {
                return Reject(UplinkFailure.NoPrevious, 0, UplinkSource.JumpPrevious);
            }
            return Request(prev, UplinkSource.JumpPrevious);
        }

        /// <summary>“上一台机器”：最近接入过的机器里第一台不是当前这台的（信号在核心时 = 最后接入的那台）。没有为 0。</summary>
        public static int PreviousMachine(CampaignState s)
        {
            int[] recent = s?.SignalCore?.RecentUplinks;
            int cur = CurrentMachine(s);
            if (recent == null)
            {
                return 0;
            }
            for (int i = 0; i < recent.Length; i++)
            {
                if (recent[i] > 0 && recent[i] != cur)
                {
                    return recent[i];
                }
            }
            return 0;
        }

        private static void CommitJumpHome(CampaignState s, bool far)
        {
            int id = CurrentMachine(s);
            string site = s.SignalCore.UplinkSiteId;
            if (id != 0)
            {
                RegionControlSystem control = RegionControlSystem.ForRegion(site);
                if (control != null && control.PossessedLogicId == id)
                {
                    control.ReleaseToStrategy(RegionControlChangeReason.PlayerRequest);
                }
                if (CurrentMachine(s) == id)
                {
                    SetUplink(s, 0, string.Empty, RegionControlChangeReason.PlayerRequest); // 地点接管系统不在（防御）：信号也要回到核心。
                }
            }
            WorldView.FocusHomeCore();
            JumpHomeCount++;
            if (far)
            {
                StartJumpCooldown(s);
            }
            SetFeedback(GameText.Get("signal.jump.home_done"));
            FeedbackCues.Raise(FeedbackCueId.CommandAck);
        }

        private static void StartJumpCooldown(CampaignState s)
        {
            EnsureState(s);
            s.SignalCore.JumpCooldownReadyTick = GameClock.Ticks + (long)Math.Round(FarCooldownSeconds * GameClock.StepHz);
            FarJumpCount++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstFarJump);
        }

        private static void ClearPendingHome()
        {
            if (_pendingHome)
            {
                Revision++;
            }
            _pendingHome = false;
            _pendingHomeFrom = 0;
            _pendingHomeRemaining = 0f;
            _pendingHomeWaitsResume = false;
        }

        private static string Seconds(float v) => Mathf.Max(0f, v).ToString("0.0", CultureInfo.InvariantCulture);

        private static string PendingFarLine()
        {
            string dist = float.IsInfinity(_pendingDistance)
                ? GameText.Get("signal.jump.other_site")
                : GameText.Format("signal.jump.distance_cells", Mathf.RoundToInt(_pendingDistance).ToString(CultureInfo.InvariantCulture));
            return GameText.Format("signal.jump.pending_far", SignalPresence.MachineLabel(_pendingTarget), dist, Seconds(_pendingRemaining));
        }

        // ─────────────────────────────── 过渡推进与提交（FGR-SIG-031）───────────────────────────────

        /// <summary>每帧（世界模拟推进之后、被观察地点处理输入之前）调用：推进过渡、过渡途中目标失效就取消、到点提交。</summary>
        public static void FrameTick(float realDt)
        {
            SyncCoreEdits(CampaignSession.Current);
            if (_pendingHome)
            {
                TickJumpHome(realDt);
                return;
            }
            if (_pendingTarget == 0)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                ClearPending();
                return;
            }
            if (!MachineRegistry.TryGetRecord(_pendingTarget, out MachineRecord rec) || rec == null || !rec.IsAlive)
            {
                Cancel(UplinkCancelReason.TargetDead, null);
                return;
            }
            UplinkFailure f = ValidateCore(s, _pendingTarget);
            if (f != UplinkFailure.None)
            {
                Cancel(UplinkCancelReason.TargetUnavailable, FailureText(f, _pendingTarget));
                return;
            }
            if (_pendingWaitsResume)
            {
                if (GameClock.Paused)
                {
                    return;
                }
                // 战略暂停中发起的接入：恢复运行后才开始过渡（第 5 章）。
                _pendingWaitsResume = false;
                Revision++;
                SetFeedback(_pendingFar ? PendingFarLine() : GameText.Format("signal.uplink.pending", SignalPresence.MachineLabel(_pendingTarget)));
                if (WorldView.IsObserved(_pendingSite))
                {
                    StartCamera();
                }
                return;
            }
            CameraDirector d = WorldView.Director;
            // FG1-SIG-07：目标在镜头没看着的地点（跨地点的远距离跳转）——过渡期间镜头留在原处（信号还在原来的机器里），提交时再切过去。
            if (d != null && d.IsBound && WorldView.IsObserved(_pendingSite))
            {
                if (d.HeadingDirect)
                {
                    _pendingSawDirect = true;
                }
                else if (_pendingSawDirect)
                {
                    // 过渡中玩家又按了退出键（镜头拉回战略）：取消。
                    Cancel(UplinkCancelReason.PlayerCancelled, null);
                    return;
                }
                else if (d.Mode == ViewMode.Strategy)
                {
                    StartCamera();
                }
            }
            if (GameClock.Paused)
            {
                return; // 过渡在暂停中不走（恢复运行后完成）。
            }
            float before = _pendingRemaining;
            _pendingRemaining -= Mathf.Max(0f, realDt);
            if (_pendingRemaining > 0f)
            {
                if (_pendingFar && Mathf.Ceil(before * 10f) != Mathf.Ceil(_pendingRemaining * 10f))
                {
                    Revision++; // 状态行的倒计时按 0.1 秒刷新。
                }
                return;
            }
            Commit();
        }

        private static void TickJumpHome(float realDt)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null)
            {
                ClearPendingHome();
                return;
            }
            if (_pendingHomeWaitsResume)
            {
                if (GameClock.Paused)
                {
                    return;
                }
                _pendingHomeWaitsResume = false;
                Revision++;
            }
            if (GameClock.Paused)
            {
                return; // 过渡在暂停中不走（恢复运行后完成）。
            }
            float before = _pendingHomeRemaining;
            _pendingHomeRemaining -= Mathf.Max(0f, realDt);
            if (_pendingHomeRemaining > 0f)
            {
                if (Mathf.Ceil(before * 10f) != Mathf.Ceil(_pendingHomeRemaining * 10f))
                {
                    Revision++;
                }
                return;
            }
            // 审查修复：过渡途中信号已经自己离开了发起时那台机器（按 V 离开 / 阵亡 / 断链回到核心）——这次跳回家园不再是远距离跳转：
            // 只把镜头飞回家园（与信号在核心时按 H 同一口径），不开始冷却。
            bool stillFar = CurrentMachine(s) == _pendingHomeFrom;
            ClearPendingHome();
            CommitJumpHome(s, far: stillFar);
        }

        private static void Commit()
        {
            int target = _pendingTarget;
            string site = _pendingSite;
            bool far = _pendingFar;
            HomeValleyMachineMarker marker = _pendingMarker;
            ClearPending();
            // FG1-SIG-07（FGR-SIG-051）：目标在镜头没看着的地点——先把镜头切过去（离开原来的地点会按“主动离开”释放原来那台，
            // 它按最后的命令和 AI 教义继续），再接管目标，最后镜头进直控对准它（不走战略飞行：落在战略视角会被当成“玩家退出”）。
            RegionControlSystem control = RegionControlSystem.ForRegion(site);
            bool switched = false;
            if (!WorldView.IsObserved(site))
            {
                // 审查修复：先在目标地点校验接管能不能成功，通过了再切镜头——失败时镜头留在原处、原来那台不被释放、信号留在原处。
                RegionControlFailure pre = RegionControlFailure.OutOfRange;
                if (control == null || !control.CanCommitUplink(target, out pre))
                {
                    FailCommit(target, pre);
                    return;
                }
                Vector3 p = marker != null && marker.IsValid ? marker.Position3 : Vector3.zero;
                switched = WorldView.ObserveAt(site, new Vector2(p.x, p.z));
                control = RegionControlSystem.ForRegion(site); // 防御：切镜头后按地点重新取一次（地点接管系统是 O(1) 字典查询）。
            }
            RegionControlSwitchResult r = control != null
                ? control.CommitUplink(target)
                : RegionControlSwitchResult.Fail(RegionControlFailure.OutOfRange);
            if (!r.Success && r.Failure != RegionControlFailure.AlreadyControlled)
            {
                FailCommit(target, r.Failure);
                return;
            }
            CommitCount++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink); // 第一次接入完成（引导内容在 FG15-UX-04）。
            CampaignState s = CampaignSession.Current;
            if (far)
            {
                StartJumpCooldown(s); // FGR-SIG-052：远距离跳转完成后冷却（记在信号上，按游戏时间）。
            }
            if (switched)
            {
                StartCamera();
            }
            string status = SteadyStatus(s, target);
            string label = SignalPresence.MachineLabel(target);
            string entered = string.IsNullOrEmpty(status) ? GameText.Format("signal.uplink.entered", label)
                : GameText.Format("signal.uplink.entered", label) + GameText.Get("signal.uplink.status.sep") + status;
            // FG1-SIG-05：从一台过热的机器直接跳到这台——那台交还 AI 时仍在过热，提示一起给出（不被“已接入”盖掉）。
            SetFeedback(_lastHandoffOverheated && !string.IsNullOrEmpty(_lastHandoffText)
                ? entered + GameText.Get("signal.uplink.status.sep") + _lastHandoffText
                : entered);
            FeedbackCues.Raise(FeedbackCueId.Takeover, string.IsNullOrEmpty(status) ? label : label + GameText.Get("signal.uplink.status.sep") + status);
        }

        /// <summary>到点提交接管失败：取消这次接入并说明原因（信号留在原处）。</summary>
        private static void FailCommit(int target, RegionControlFailure failure)
        {
            CancelCount++;
            LastCancel = failure == RegionControlFailure.TargetDead ? UplinkCancelReason.TargetDead : UplinkCancelReason.TargetUnavailable;
            string why = LastCancel == UplinkCancelReason.TargetDead
                ? GameText.Format("signal.uplink.cancel.dead", SignalPresence.MachineLabel(target))
                : GameText.Format("signal.uplink.cancel.gone", SignalPresence.MachineLabel(target), FailureText(FromControlFailure(failure), target));
            SetFeedback(why);
            FeedbackCues.Raise(FeedbackCueId.Denied, why);
        }

        private static void Cancel(UplinkCancelReason reason, string detail)
        {
            int target = _pendingTarget;
            ClearPending();
            CancelCount++;
            LastCancel = reason;
            Revision++;
            string label = SignalPresence.MachineLabel(target);
            string text = reason switch
            {
                UplinkCancelReason.TargetDead => GameText.Format("signal.uplink.cancel.dead", label),
                UplinkCancelReason.TargetUnavailable => GameText.Format("signal.uplink.cancel.gone", label, detail ?? string.Empty),
                _ => GameText.Format("signal.uplink.cancel.player", label),
            };
            SetFeedback(text);
            if (reason == UplinkCancelReason.PlayerCancelled)
            {
                FeedbackCues.Raise(FeedbackCueId.CommandAck);
            }
            else
            {
                FeedbackCues.Raise(FeedbackCueId.Denied, text);
            }
        }

        /// <summary>FG1-SIG-04：断链条件出现（静默夜开始……）时取消正在进行的接入过渡，信号留在原处并说明原因。没有过渡时什么也不做。</summary>
        public static void CancelPendingForLink(string why)
        {
            if (_pendingTarget != 0)
            {
                Cancel(UplinkCancelReason.TargetUnavailable, why);
            }
        }

        /// <summary>FG1-SIG-04：断链 / 预警 / 安全模式的原因写进 HUD 状态行（显示 signal.uplink_feedback_seconds 秒）。</summary>
        public static void PushFeedback(string text) => SetFeedback(text);

        private static void ClearPending()
        {
            if (_pendingTarget != 0)
            {
                Revision++;
            }
            _pendingTarget = 0;
            _pendingMarker = null;
            _pendingOrigin = 0;
            _pendingSite = null;
            _pendingFar = false;
            _pendingDistance = 0f;
            _pendingRemaining = 0f;
            _pendingWaitsResume = false;
            _pendingSawDirect = false;
        }

        private static void StartCamera()
        {
            CameraDirector d = WorldView.Director;
            if (d != null && d.IsBound && d.Mode == ViewMode.Strategy)
            {
                d.RequestDirect();
            }
        }

        private static UplinkRequestResult Reject(UplinkFailure f, int logicId, UplinkSource source)
        {
            RejectedCount++;
            LastFailure = f;
            string text = FailureText(f, logicId);
            SetFeedback(text);
            // 无害的重复操作（已经在里面、过渡中又按、只有一台可切）不出拒绝音；面板挡着时只有点机器列表才提示（键盘按键被面板吃掉是正常的）。
            bool quiet = f == UplinkFailure.AlreadyUplinked || f == UplinkFailure.Busy || f == UplinkFailure.NoCandidate
                         || (f == UplinkFailure.ModalBlocked && source != UplinkSource.MachineList);
            if (!quiet)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied, text);
            }
            return new UplinkRequestResult(false, f, logicId, text, false);
        }

        /// <summary>提交时区域接管系统的拒绝码 → 接入原因码（原因文本统一走 signal.uplink.reason.*）。</summary>
        public static UplinkFailure FromControlFailure(RegionControlFailure f) => f switch
        {
            RegionControlFailure.TargetDead => UplinkFailure.Dead,
            RegionControlFailure.SignalJammed => UplinkFailure.Jammed,
            RegionControlFailure.Ineligible => UplinkFailure.InFactory,
            RegionControlFailure.ModalBlocked => UplinkFailure.ModalBlocked,
            RegionControlFailure.AlreadyControlled => UplinkFailure.AlreadyUplinked,
            RegionControlFailure.TargetNotFound => UplinkFailure.NotFound,
            _ => UplinkFailure.OtherSite,
        };

        public static string FailureText(UplinkFailure f, int logicId)
        {
            string label = logicId > 0 ? SignalPresence.MachineLabel(logicId) : string.Empty;
            switch (f)
            {
                case UplinkFailure.None: return string.Empty;
                case UplinkFailure.NoCampaign: return GameText.Get("signal.uplink.reason.no_campaign");
                case UplinkFailure.NoSelection: return GameText.Format("signal.uplink.reason.no_selection", InputDisplay.ForAction(GameActionId.ToggleCameraView));
                case UplinkFailure.NotFound: return GameText.Get("signal.uplink.reason.not_found");
                case UplinkFailure.Dead: return GameText.Format("signal.uplink.reason.dead", label);
                case UplinkFailure.OtherSite: return GameText.Format("signal.uplink.reason.other_site", label);
                case UplinkFailure.OutOfCoverage: return GameText.Format("signal.uplink.reason.out_of_coverage", label);
                case UplinkFailure.Jammed: return GameText.Format("signal.uplink.reason.jammed", label);
                case UplinkFailure.SilentNight: return GameText.Get("signal.uplink.reason.silent_night");
                case UplinkFailure.InFactory: return GameText.Format("signal.uplink.reason.in_factory", label);
                case UplinkFailure.OnRepairBay: return GameText.Format("signal.uplink.reason.on_repair_bay", label);
                case UplinkFailure.InDelivery: return GameText.Format("signal.uplink.reason.in_delivery", label);
                case UplinkFailure.ModalBlocked: return GameText.Get("signal.uplink.reason.modal");
                case UplinkFailure.Busy: return GameText.Format("signal.uplink.reason.busy", label);
                case UplinkFailure.AlreadyUplinked: return GameText.Format("signal.uplink.reason.already", label);
                case UplinkFailure.NoCandidate: return GameText.Get("signal.uplink.reason.no_candidate");
                case UplinkFailure.ViewLeaving: return GameText.Get("signal.uplink.reason.view_leaving");
                case UplinkFailure.JumpCooldown:
                    return GameText.Format("signal.uplink.reason.jump_cooldown",
                        Math.Ceiling(JumpCooldownRemaining(CampaignSession.Current)).ToString("0", CultureInfo.InvariantCulture),
                        FarDistanceCells.ToString("0", CultureInfo.InvariantCulture));
                case UplinkFailure.NoPrevious: return GameText.Get("signal.uplink.reason.no_previous");
                default: return GameText.Get("signal.uplink.reason.not_found");
            }
        }

        // ─────────────────────────────── 信号位置的唯一写入口 ───────────────────────────────

        /// <summary>
        /// 区域接管系统每一次变更后调用（<see cref="RegionControlSystem"/> 的 PublishChange）：把信号位置同步进存档状态，并通知
        /// 离开的机器回到本地配置、接入的机器插入信号核固件（FGR-SIG-032）。<paramref name="cur"/> = 0 表示信号离开 <paramref name="prev"/> 回到核心。
        /// </summary>
        public static void OnControlChanged(RegionControlSystem control, int prev, int cur, RegionControlChangeReason reason)
        {
            if (reason == RegionControlChangeReason.SignalRestored || control == null)
            {
                return;
            }
            CampaignState s = control.BoundState ?? CampaignSession.Current;
            if (s == null)
            {
                return;
            }
            EnsureState(s);
            int before = s.SignalCore.UplinkMachineLogicId;
            if (cur != 0)
            {
                SetUplink(s, cur, control.RegionId, reason);
                // FG1-SIG-04：阵亡回弹到另一台 = 一次断链（给原因、音效）。
                if (reason == RegionControlChangeReason.DeathRebound && before != 0 && before == prev)
                {
                    SignalLinkService.OnControlBreak(s, control.RegionId, prev, cur, reason);
                }
                return;
            }
            if (before != 0 && (before == prev || s.SignalCore.UplinkSiteId == control.RegionId))
            {
                SetUplink(s, 0, string.Empty, reason);
                // FG1-SIG-04：干扰 / 走出覆盖 / 静默夜 / 阵亡且没有合适的回弹目标 → 弹回归还核心，给原因、音效，机器进入安全模式。
                SignalLinkService.OnControlBreak(s, control.RegionId, before, 0, reason);
                if (reason == RegionControlChangeReason.PlayerRequest)
                {
                    LeaveCount++;
                    if (!_lastHandoffOverheated)
                    {
                        SetFeedback(GameText.Format("signal.uplink.left", SignalPresence.MachineLabel(before)));
                    }
                    FeedbackCues.Raise(FeedbackCueId.UplinkLeave, SignalPresence.MachineLabel(before)); // FG1-HUD-01（FG-GAP-044）：离开的提交音效钩子。
                }
            }
            // 玩家把镜头拉回战略（主动退出）：这个地点里还没完成的接入一并取消，不在玩家看战略地图时突然接进去。
            if (reason == RegionControlChangeReason.PlayerRequest && _pendingTarget != 0 && _pendingSite == control.RegionId && !_pendingWaitsResume)
            {
                Cancel(UplinkCancelReason.PlayerCancelled, null);
            }
        }

        /// <summary>地点卸载（撤离 / 放弃远征 / 回主菜单）：信号若在这里的机器里，回到归还核心；这里的接入过渡取消。</summary>
        public static void OnRegionUnbound(string regionId, CampaignState bound)
        {
            if (regionId == null)
            {
                return;
            }
            if (_pendingTarget != 0 && _pendingSite == regionId)
            {
                ClearPending();
            }
            if (bound?.SignalCore != null && bound.SignalCore.UplinkMachineLogicId != 0 && bound.SignalCore.UplinkSiteId == regionId)
            {
                // FG1-SIG-07 审查修复：信号所在的地点卸载了——从这里发起的“跳回家园”过渡随之作废（信号已经回到核心，不再到点提交、不开始冷却）。
                ClearPendingHome();
                SetUplink(bound, 0, string.Empty, RegionControlChangeReason.RegionUnload);
                SignalLinkService.ClearWatch();
            }
        }

        /// <summary>
        /// FG1-SIG-07 审查修复：整个世界卸载（回主菜单 / 读档 / 回滚，<c>WorldSimulation.UnloadAll</c>）时清掉全部运行时过渡——
        /// 接入 / 远距离跳转过渡与“跳回家园”过渡都不进存档（过渡没完成时存档，读回来信号在原处），不能带进下一局或读档后的对局；
        /// 状态行上一条反馈也属于旧世界，一并清掉。存档数据（冷却、最近接入）不动。
        /// </summary>
        public static void OnWorldUnloaded()
        {
            ClearPending();
            ClearPendingHome();
            _feedback = string.Empty;
            _feedbackUntil = 0f;
            Revision++;
        }

        private static void SetUplink(CampaignState s, int logicId, string siteId, RegionControlChangeReason reason)
        {
            int old = s.SignalCore.UplinkMachineLogicId;
            if (old == logicId)
            {
                s.SignalCore.UplinkSiteId = logicId == 0 ? string.Empty : siteId ?? string.Empty;
                return;
            }
            // FG1-SIG-05（负向：交还 AI 时正处在过载状态）：过热留在机体上（内核 Overheated 位 + 热量），AI 同样要等散热到恢复线以下才开火，
            // 也不会再打熔穿过载（过载随信号离开）。这里只负责告诉玩家——离开的那台还在过热。
            bool oldOverheated = false;
            if (old != 0)
            {
                CombatSites.Get(s.SignalCore.UplinkSiteId)?.TryGetMachineHeat(old, out _, out oldOverheated);
            }
            _lastHandoffOverheated = false;
            // FG1-HUD-01（FGR-SIG-082 机器经历“与信号同行”）：离开的那台把这一段累计进它的记录；进入的那台次数 +1、从现在开始计时。
            // 读档恢复（old == logicId）在上面已经提前返回，不算新的一次；按统一时钟步计，与是否被观察、倍速无关（暂停不走）。
            long nowTick = GameClock.Ticks;
            if (old != 0)
            {
                MachineSignalExperience.CloseSegment(s, old, nowTick);
            }
            if (logicId != 0)
            {
                PushRecent(s, logicId); // FG1-SIG-07：“跳回上一台机器”的记录（最近接入过的机器，进存档）。
                MachineSignalExperience.OpenSegment(s, logicId, nowTick);
            }
            s.SignalCore.UplinkMachineLogicId = logicId;
            s.SignalCore.UplinkSiteId = logicId == 0 ? string.Empty : siteId ?? string.Empty;
            _coreRevisionSeen = SignalCoreService.Revision;
            Revision++;
            // 离开的机器回到本地配置、接入的机器插入信号核固件：只通知这两台重算武器参数（与机器总数无关）。
            if (old != 0)
            {
                RecompileNotifyCount++;
                MachineLoadoutRegistry.NotifyChanged(old);
            }
            string[] inserted = Array.Empty<string>();
            if (logicId != 0)
            {
                RecompileNotifyCount++;
                MachineLoadoutRegistry.NotifyChanged(logicId);
                inserted = EffectiveInserted(s, logicId);
                SignalLinkService.OnUplinked(s, logicId); // FG1-SIG-04：信号回到这台机器 = 它不再处于安全模式。
            }
            else
            {
                SignalLinkService.ClearWatch();
            }
            GameEvent.Send(UplinkChangedEvent, new SignalUplinkChange
            {
                PreviousLogicId = old,
                CurrentLogicId = logicId,
                SiteId = s.SignalCore.UplinkSiteId,
                Reason = reason,
                InsertedFirmwareIds = inserted,
                MorphActive = inserted.Length > 0,
            });
            if (oldOverheated)
            {
                _lastHandoffOverheated = true;
                HandoffOverheatedCount++;
                _lastHandoffText = GameText.Format("signal.uplink.handoff_overheated", SignalPresence.MachineLabel(old),
                    FracturedCityLayout.WeaponHeatRecoverThreshold.ToString("0", CultureInfo.InvariantCulture));
                SetFeedback(_lastHandoffText);
            }
        }

        /// <summary>FG1-SIG-07：最近接入过的机器（去重、最新在前，最多 4 台）。</summary>
        private static void PushRecent(CampaignState s, int logicId)
        {
            int[] old = s.SignalCore.RecentUplinks ?? Array.Empty<int>();
            var next = new List<int>(4) { logicId };
            for (int i = 0; i < old.Length && next.Count < 4; i++)
            {
                if (old[i] > 0 && old[i] != logicId)
                {
                    next.Add(old[i]);
                }
            }
            s.SignalCore.RecentUplinks = next.ToArray();
        }

        /// <summary>FG1-SIG-05：刚离开的那台机器交还 AI 时还在过热（本次变更的反馈已写成“散热后才开火”，不再被“已离开”覆盖）。</summary>
        private static bool _lastHandoffOverheated;
        private static string _lastHandoffText;

        /// <summary>FG1-SIG-05：交还 AI 时机器仍在过热的次数（自检读点）。</summary>
        public static int HandoffOverheatedCount { get; private set; }

        // ─────────────────────────────── 核心固件冷却（FGR-SIG-033）───────────────────────────────

        /// <summary>
        /// 战斗内核报告“这台机器打出了某条反应”（CombatSite 的事件结算）。信号在这台机器里、且这条反应由信号带进来的核心固件发动时，
        /// 这枚固件开始冷却（记在信号上，按游戏时间）；冷却期间这台机器（以及信号接下来去的任何机器）的武器暂不带这条反应。
        /// </summary>
        public static void OnReactionFired(CampaignState s, int logicId, string reactionId) => OnReactionFired(s, logicId, reactionId, false);

        /// <summary>
        /// 同上，由战斗内核的玩法事件 <c>CombatEventKind.ReactionFired</c> 调（CombatSite）。<paramref name="gatedAtFire"/> = 发动那一刻内核认定它是
        /// 门控反应（信号带进来的、有冷却的核心固件）：这时即使事件被顺延处理、信号已经换了机器，冷却照样记上（不给“打完立刻切走”留空子）。
        /// 不是门控时按现在的接入状态核对。同一次冷却里再来的发动（内核已当场压住，正常不会出现）不延长、不重复计数。
        /// </summary>
        public static void OnReactionFired(CampaignState s, int logicId, string reactionId, bool gatedAtFire)
        {
            if (s == null || logicId <= 0)
            {
                return;
            }
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
            if (fw == null || !FirmwareKinds.IsCore(fw))
            {
                return;
            }
            if (!gatedAtFire && (!IsUplinked(s, logicId) || !FirmwareKinds.ExpandMixed(EffectiveInserted(s, logicId)).Contains(fw)))
            {
                return;
            }
            float cd = FirmwareKinds.CoreCooldownSeconds(fw);
            if (cd <= 0f)
            {
                return;
            }
            if (CooldownRemaining(s, fw) > 0)
            {
                NotifyCooldownHolders(s, logicId);
                return;
            }
            StartCoreCooldown(s, fw, cd, logicId);
        }

        /// <summary>
        /// FG6-DEF-01（FGR-DEF-005 接入炮塔，插入核心固件）：信号在炮塔里、炮塔打出了核心固件的具名反应——按信号上的冷却结算（与机器同一份冷却、同一次暴露计数），
        /// 冷却中再来的发动不延长、不重复计数。炮塔不是机器（没有 LogicId），冷却开始后由炮塔服务自己重编译。
        /// </summary>
        public static void OnHostReactionFired(CampaignState s, string reactionId)
        {
            if (s == null)
            {
                return;
            }
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
            if (fw == null || !FirmwareKinds.IsCore(fw))
            {
                return;
            }
            float cd = FirmwareKinds.CoreCooldownSeconds(fw);
            if (cd <= 0f || CooldownRemaining(s, fw) > 0)
            {
                return;
            }
            StartCoreCooldown(s, fw, cd, 0);
        }

        /// <summary>核心固件冷却开始（机器与炮塔共用）：记冷却到期步、计暴露、发反馈与事件。<paramref name="logicId"/> = 发动的机器（炮塔为 0）。</summary>
        private static void StartCoreCooldown(CampaignState s, string fw, float cd, int logicId)
        {
            // 冷却到期存整数步（FG1-E2E-01，DEBT-FG1SIG07-05：游戏秒存浮点读回会差 1 ulp）。
            long readyTick = GameClock.TickAfter(cd);
            double ready = readyTick / (double)GameClock.StepHz;
            EnsureState(s);
            var list = new List<SignalCoreCooldownRecord>(s.SignalCore.CoreCooldowns);
            SignalCoreCooldownRecord rec = list.Find(c => c != null && c.ContentId == fw);
            if (rec == null)
            {
                list.Add(new SignalCoreCooldownRecord { ContentId = fw, ReadyTick = readyTick });
            }
            else
            {
                rec.ReadyTick = readyTick;
            }
            s.SignalCore.CoreCooldowns = list.ToArray();
            CoreFiredCount++;
            // FG1-SIG-06（FGR-SIG-070 / 061）：核心固件发动计暴露（+0.5）；这枚核心固件未破解（信号裸跑）时按裸跑计（+2）。
            // 与冷却同一时刻、同一次冷却只计一次（冷却中的回声在上面已经返回）。
            CampaignExposureLedger.GrantCoreFire(s, fw, FirmwareKinds.IsRaw(s, fw));
            Revision++;
            if (logicId > 0)
            {
                NotifyCooldownHolders(s, logicId);
            }
            SetFeedback(GameText.Format("signal.uplink.core_fired", UplinkCompiler.FirmwareName(fw), cd.ToString("0.#", CultureInfo.InvariantCulture)));
            GameEvent.Send(CoreFirmwareFiredEvent, new CoreFirmwareFired { LogicId = logicId, FirmwareId = fw, ReadyAtGameSeconds = ready });
        }

        /// <summary>冷却开始（或同一次冷却的回声）：发动的那台、以及信号现在所在的那台（事件顺延期间信号可能已换机器）按冷却重新下发武器参数。至多 2 台。</summary>
        private static void NotifyCooldownHolders(CampaignState s, int firedLogicId)
        {
            RecompileNotifyCount++;
            MachineLoadoutRegistry.NotifyChanged(firedLogicId);
            int cur = CurrentMachine(s);
            if (cur != 0 && cur != firedLogicId)
            {
                RecompileNotifyCount++;
                MachineLoadoutRegistry.NotifyChanged(cur);
            }
        }

        /// <summary>
        /// 接入中改了信号核（只能在家园改：装卸、换位、切预设都可以在接入时按 P 做）：接入的机器立刻按新的信号核重新插入、重编译，
        /// 并再发一次变更事件（形变跟着更新）。O(1)：只比较版本号。
        /// </summary>
        private static void SyncCoreEdits(CampaignState s)
        {
            int rev = SignalCoreService.Revision;
            if (rev == _coreRevisionSeen)
            {
                return;
            }
            _coreRevisionSeen = rev;
            int id = CurrentMachine(s);
            if (id == 0)
            {
                return;
            }
            RecompileNotifyCount++;
            Revision++;
            MachineLoadoutRegistry.NotifyChanged(id);
            string[] inserted = EffectiveInserted(s, id);
            GameEvent.Send(UplinkChangedEvent, new SignalUplinkChange
            {
                PreviousLogicId = id,
                CurrentLogicId = id,
                SiteId = s.SignalCore.UplinkSiteId,
                Reason = RegionControlChangeReason.None,
                InsertedFirmwareIds = inserted,
                MorphActive = inserted.Length > 0,
            });
        }

        /// <summary>每个模拟步（整个世界，与观察无关）：到期的冷却清掉，信号所在的机器重新带上这条反应。O(信号核槽位数)。</summary>
        public static void SimStep(CampaignState s)
        {
            SyncCoreEdits(s);
            SignalCoreCooldownRecord[] cds = s?.SignalCore?.CoreCooldowns;
            if (cds == null || cds.Length == 0)
            {
                return;
            }
            long nowTick = GameClock.Ticks;
            double now = GameClock.GameSeconds;
            int keep = 0;
            for (int i = 0; i < cds.Length; i++)
            {
                if (cds[i] != null && cds[i].ReadyTick > nowTick)
                {
                    keep++;
                }
            }
            if (keep == cds.Length)
            {
                return;
            }
            var next = new SignalCoreCooldownRecord[keep];
            int k = 0;
            for (int i = 0; i < cds.Length; i++)
            {
                if (cds[i] != null && cds[i].ReadyTick > nowTick)
                {
                    next[k++] = cds[i];
                }
            }
            s.SignalCore.CoreCooldowns = next;
            LastCooldownExpiryGameSeconds = now;
            Revision++;
            int id = CurrentMachine(s);
            if (id != 0)
            {
                RecompileNotifyCount++;
                MachineLoadoutRegistry.NotifyChanged(id);
            }
        }

        // ─────────────────────────────── 读档（第 5 章“存档时玩家在机器里”）───────────────────────────────

        /// <summary>
        /// 读档后（世界载入、镜头放好之后）调用：存档时信号在某台机器里，就让它回到那台机器（不走过渡，不计一次新的接入统计），镜头进直控。
        /// 固件（信号核）、冷却（信号核状态域）、热量（战斗内核快照）都是存档里的原值，这里只恢复“谁在开”。机器已不可接入时回到归还核心并说明。
        /// 幂等：信号本来就在那台机器里（例如远征归来重进家园）时什么也不做。上一局没完成的过渡在卸载旧世界时已随地点清掉（<see cref="OnRegionUnbound"/>）。
        /// </summary>
        public static bool RestoreAfterLoad()
        {
            CampaignState s = CampaignSession.Current;
            int id = CurrentMachine(s);
            if (id == 0)
            {
                return false;
            }
            // FG1-HUD-01：FG1-HUD-01 之前的存档没有“这一段从哪一步开始”（读成 0）——从读档这一刻开始计，不把整局时长算进去。
            if (s.SignalCore.UplinkSinceTick <= 0 || s.SignalCore.UplinkSinceTick > GameClock.Ticks)
            {
                s.SignalCore.UplinkSinceTick = GameClock.Ticks;
            }
            RegionControlSystem control = null;
            if (MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null && rec.IsAlive)
            {
                control = RegionControlSystem.ForRegion(rec.RegionId);
                if (control != null && !WorldView.IsObserved(rec.RegionId))
                {
                    WorldView.Observe(rec.RegionId);
                }
            }
            if (control != null && control.PossessedLogicId == id)
            {
                return true; // 已经在里面（不是读档，是世界里的重进）：保持原样。
            }
            RegionControlSwitchResult r = control == null
                ? RegionControlSwitchResult.Fail(RegionControlFailure.TargetNotFound)
                : control.CommitUplink(id, restoring: true);
            string label = SignalPresence.MachineLabel(id);
            if (!r.Success)
            {
                SetUplink(s, 0, string.Empty, RegionControlChangeReason.RegionUnload);
                string why = GameText.Format("signal.uplink.restore_failed", label);
                SetFeedback(why);
                FeedbackCues.Raise(FeedbackCueId.Denied, why);
                return false;
            }
            CameraDirector d = WorldView.Director;
            if (d != null && d.IsBound && d.Mode == ViewMode.Strategy)
            {
                d.RequestDirect();
            }
            Revision++;
            SetFeedback(GameText.Format("signal.uplink.restored", label));
            return true;
        }

        // ─────────────────────────────── HUD 状态行 ───────────────────────────────

        /// <summary>
        /// HUD 状态行（FG1-HUD-01 会扩成完整的接入 HUD）：过渡中 → “正在接入…”；接入中的链路预警（宽限倒计时 / 覆盖边缘）→ 预警；刚发生的事（3 秒）→ 那条反馈；
        /// 接入中 → 接入口插了什么 / 没插入的原因 / 冷却；在归还核心 → 空。每帧调用开销 O(1)（调用方按 <see cref="StatusKey"/> 变化才重建）。
        /// </summary>
        public static string StatusLine(CampaignState s)
        {
            if (_pendingHome)
            {
                return GameText.Format("signal.jump.pending_home", Seconds(_pendingHomeRemaining));
            }
            if (_pendingTarget != 0)
            {
                if (_pendingFar && !_pendingWaitsResume)
                {
                    return PendingFarLine();
                }
                return GameText.Format(_pendingWaitsResume ? "signal.uplink.pending_paused" : "signal.uplink.pending", SignalPresence.MachineLabel(_pendingTarget));
            }
            int id = CurrentMachine(s);
            // FG1-SIG-04：链路预警（干扰 / 走出覆盖的宽限逐秒倒计时、接近覆盖边缘）关系到正在接入的这一台会不会马上失联，
            // 优先于普通反馈（例如别的机器退出安全模式的 3 秒提示）和常驻状态。
            string warn = id != 0 ? SignalLinkService.WarningLine : string.Empty;
            if (warn.Length > 0)
            {
                return warn;
            }
            if (_feedback.Length > 0 && Now < _feedbackUntil)
            {
                return _feedback;
            }
            if (id == 0 && Economy.TestRangeService.TryGetUplinkedProjection(out _, out _, out string projection))
            {
                // FG5-RND-03：信号在靶场的仿真投影里（不在真实机器里）——信号位置 HUD 写明，不让玩家以为信号还在核心。
                return GameText.Format("range.signal.in_projection", projection);
            }
            if (id == 0 && Defense.TurretUplink.StatusLine(s) is string turretLine)
            {
                // FG6-DEF-01（FGR-DEF-005）：信号在炮塔里——写明在哪座炮塔、怎么退出。
                return turretLine;
            }
            return id == 0 ? string.Empty : SteadyStatus(s, id);
        }

        /// <summary>状态行的变化键（界面只在变了时重建文本）：过渡、反馈、信号位置、信号核、冷却整秒。</summary>
        public static int StatusKey(CampaignState s)
        {
            int cooldownKey = 0;
            SignalCoreCooldownRecord[] cds = s?.SignalCore?.CoreCooldowns;
            if (cds != null)
            {
                for (int i = 0; i < cds.Length; i++)
                {
                    if (cds[i] != null)
                    {
                        cooldownKey = cooldownKey * 31 + (int)Math.Ceiling(GameClock.SecondsUntil(cds[i].ReadyTick));
                    }
                }
            }
            bool feedbackActive = _feedback.Length > 0 && Now < _feedbackUntil;
            int jumpKey = (int)Math.Ceiling(JumpCooldownRemaining(s));
            return HashCode.Combine(HashCode.Combine(Revision, CurrentMachine(s), cooldownKey, feedbackActive, SignalCoreService.Revision, FirmwareKinds.Revision, (int)GameText.Language),
                SignalLinkService.WarningRevision, jumpKey, Economy.TestRangeService.Revision, Defense.TurretUplink.Revision);
        }

        /// <summary>接入中的常驻状态：插了什么（冷却中的标出剩余秒数）、没插入的逐条原因、没有接入口 / 信号核为空 / 接入口没接通。</summary>
        public static string SteadyStatus(CampaignState s, int logicId)
        {
            if (s == null || logicId == 0)
            {
                return string.Empty;
            }
            string[] core = SignalCoreService.ActiveContentIds(s); // FG4-ECO-11：失效的槽不插入
            UplinkInsertionPlan plan = MachineLoadoutRegistry.PlanForUplink(s, logicId, core);
            if (plan == null)
            {
                return string.Empty;
            }
            if (!plan.HasUplink)
            {
                return GameText.Get("signal.uplink.status.no_uplink");
            }
            if (plan.CoreEmpty)
            {
                return GameText.Format("signal.uplink.status.core_empty", InputDisplay.ForAction(GameActionId.OpenSignalCore));
            }
            string sep = GameText.Get("signal.uplink.status.sep");
            string listSep = GameText.Get("signal.uplink.status.list_sep");
            var sb = new StringBuilder();
            MachineCombatResolution r = MachineLoadoutRegistry.ResolveForPilot(s, logicId, s.RandomSeed);
            if (plan.Inserted.Count > 0 && r.Success && r.Preview != null && !r.Preview.UplinkOnPath)
            {
                sb.Append(GameText.Get("signal.uplink.status.off_path"));
            }
            else if (plan.Inserted.Count > 0)
            {
                var names = new StringBuilder();
                foreach (UplinkFirmwareEntry e in plan.Inserted)
                {
                    if (names.Length > 0)
                    {
                        names.Append(listSep);
                    }
                    string name = UplinkCompiler.FirmwareName(e.FirmwareId);
                    double left = CooldownRemaining(s, e.FirmwareId);
                    names.Append(left > 0 && FirmwareKinds.IsCore(e.FirmwareId)
                        ? GameText.Format("signal.uplink.status.cooling", name, Math.Ceiling(left).ToString("0", CultureInfo.InvariantCulture))
                        : name);
                }
                sb.Append(GameText.Format("signal.uplink.status.inserted", names.ToString()));
            }
            foreach (UplinkFirmwareEntry e in plan.Skipped)
            {
                string name = UplinkCompiler.FirmwareName(e.FirmwareId);
                string line = e.Skip switch
                {
                    UplinkSkipReason.OverQuota => GameText.Format("circuit.uplink.skip.over_quota", name, plan.Quota),
                    UplinkSkipReason.PathLimit => GameText.Format("circuit.uplink.skip.path_limit", name, plan.PathLimit),
                    UplinkSkipReason.Unknown => GameText.Format("circuit.uplink.skip.unknown", e.FirmwareId),
                    _ => GameText.Format("circuit.uplink.skip.no_uplink", name),
                };
                if (sb.Length > 0)
                {
                    sb.Append(sep);
                }
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static void SetFeedback(string text)
        {
            _feedback = text ?? string.Empty;
            _feedbackUntil = Now + FeedbackSeconds;
            Revision++;
        }

        private static void EnsureState(CampaignState s)
        {
            if (s.SignalCore == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            s.SignalCore.UplinkSiteId ??= string.Empty;
            s.SignalCore.CoreCooldowns ??= Array.Empty<SignalCoreCooldownRecord>();
            s.SignalCore.RecentUplinks ??= Array.Empty<int>();
        }

        /// <summary>自检之间复位（不动存档数据）。</summary>
        public static void ResetForTests()
        {
            ClearPending();
            ClearPendingHome();
            FarJumpCount = 0;
            JumpHomeCount = 0;
            CoverageProvider = null;
            SilentNightProvider = null;
            OnRepairBayProvider = null;
            InDeliveryProvider = null;
            RealTimeForTests = null;
            AcceptedCount = 0;
            RejectedCount = 0;
            CommitCount = 0;
            LeaveCount = 0;
            CancelCount = 0;
            RecompileNotifyCount = 0;
            CoreFiredCount = 0;
            HandoffOverheatedCount = 0;
            _lastHandoffOverheated = false;
            _lastHandoffText = null;
            LastFailure = UplinkFailure.None;
            LastCancel = UplinkCancelReason.None;
            LastCooldownExpiryGameSeconds = -1;
            _coreRevisionSeen = -1;
            _feedback = string.Empty;
            _feedbackUntil = 0f;
            Revision++;
        }
    }
}
