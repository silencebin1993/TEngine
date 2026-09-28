using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-CTL-01：任意接管正式化——归还谷地与破碎都市共用同一个
    /// <see cref="TrySwitchControlledUnit"/> 入口（Tab 候选/候选条点击/M 键首次接管三条路径全部收敛于此），
    /// 与 <see cref="RegionSquadCommandSystem"/> 同一设计范式（Context 委托 + 共享引擎，避免两个
    /// Controller 各写一套判定）。
    ///
    /// ── 与细胞阶段 <c>SimBridge.RequestControlSwitch</c>/<c>ControlRequestResult</c> 的关系 ──
    /// 那一套走 SimEntityId + SimWorld 内核（<c>BinGames.Sim</c>），本类走 <c>MachineRegistry</c> 的
    /// 战役级 LogicId，两边数据模型完全独立（ER5-REGION-01/ER5-CMD-01 已反复确认区域系统不接 SimBridge）。
    /// 本类的九失败码/Suspended 宽限/死亡回弹设计**参照**细胞阶段那一套已验证过的状态机（9个失败码、
    /// ControlAvailability 三态、死亡回弹队列），是同一模式的第二次独立实现，不是同一套代码的直接复用
    /// ——两边永远不会共享一个 SimEntityId 或 LogicId 号段，硬复用只会造成类型不匹配。
    ///
    /// ── 状态归属（DIGEST 明确点名，不要另起一套）──
    /// 真正"谁被接管"的状态仍然是 Controller 自己的 <c>_possessed</c> 字段（经
    /// <c>IsMachineDirectControlled</c> 被 <see cref="RegionSquadCommandSystem"/> 读取，ER5-CMD-01
    /// 编队命令排除受控机的判定口）——本类只是校验+委托写入（<see cref="RegionControlContext.SetPossessed"/>），
    /// 自己不持有第二份"当前受控是谁"的权威拷贝。</summary>
    public enum RegionControlFailure : byte
    {
        None = 0,
        /// <summary>目标 LogicId 从未分配过 / Tab 循环时本区域没有任何候选。</summary>
        TargetNotFound,
        /// <summary>目标存在，但归属另一个区域（如仍留在归还谷地、当前在破碎都市查询）。</summary>
        TargetNotFriendly,
        /// <summary>目标已阵亡。</summary>
        TargetDead,
        /// <summary>目标记录属于本区域且存活，但当前没有可视化对象（尚未同步/暂不在场）。</summary>
        OutOfRange,
        /// <summary>目标位置处于干扰场内，新接管请求被拒绝（ER5-REGION-01 既有机制的正式化）。</summary>
        SignalJammed,
        /// <summary>镜头正在过渡中，拒绝新的接管请求。</summary>
        TransitionInProgress,
        /// <summary>模态 UI 打开中，世界输入让位。</summary>
        ModalBlocked,
        /// <summary>目标已经是当前受控机器。</summary>
        AlreadyControlled,
        /// <summary>其它合法但不可用（如仍在装配站队列内，尚未驶出工厂）。</summary>
        Ineligible,
    }

    /// <summary>这次控制变更是怎么来的——同 <c>GameLogic.Core.ControlChangeReason</c>（细胞阶段）
    /// 同一设计意图，区域系统的独立版本（LogicId 而非 SimEntityId）。</summary>
    public enum RegionControlChangeReason : byte
    {
        None = 0,
        /// <summary>玩家主动请求（Tab / 候选条点击 / M 键首次接管）。</summary>
        PlayerRequest,
        /// <summary>受控机阵亡，按距离再 LogicId 自动回弹到下一台，或无候选转 None。</summary>
        DeathRebound,
        /// <summary>失联（干扰场）宽限期耗尽，控制权收回。</summary>
        SignalLost,
        /// <summary>失联宽限期内信号恢复，控制权继续保留在同一台机器上。</summary>
        SignalRestored,
        /// <summary>区域卸载（Exit）导致的强制释放。</summary>
        RegionUnload,
        /// <summary>FG1-SIG-04：走出与归还核心连通的信号覆盖，宽限期耗尽，信号弹回归还核心。</summary>
        CoverageLost,
        /// <summary>FG1-SIG-04：静默夜开始（FG07；本 Story 只留接口），信号立即弹回归还核心。</summary>
        SilentNight,
    }

    /// <summary>当前控制可用性三态，同细胞阶段 <c>ControlAvailability</c> 的区域版本
    /// （区域系统没有"主动放下意识"的 Released 概念，故只有三态）。</summary>
    public enum RegionControlAvailability : byte
    {
        /// <summary>无受控目标，处于战略视角。</summary>
        None = 0,
        /// <summary>信号暂时中断（干扰场内），记录仍在，宽限期内等待恢复。</summary>
        Suspended = 1,
        /// <summary>正常受控。</summary>
        Controlled = 2,
    }

    public readonly struct RegionControlSwitchResult
    {
        public readonly bool Success;
        public readonly RegionControlFailure Failure;
        public readonly int LogicId;

        private RegionControlSwitchResult(bool success, RegionControlFailure failure, int logicId)
        {
            Success = success;
            Failure = failure;
            LogicId = logicId;
        }

        public static RegionControlSwitchResult Ok(int logicId) =>
            new RegionControlSwitchResult(true, RegionControlFailure.None, logicId);
        public static RegionControlSwitchResult Fail(RegionControlFailure failure) =>
            new RegionControlSwitchResult(false, failure, 0);

        /// <summary>九类失败码对应的玩家可读文案；成功时返回 null（调用方不应该在成功路径上显示它）。</summary>
        public string PlayerText => Success ? null : RegionControlSystem.TextFor(Failure);
    }

    /// <summary>M1-04 <c>ControlledUnitChangedSignal</c> 的区域版本——LogicId 而非 SimEntityId，
    /// 字段语义完全对应："成功只发一次事件"同一条纪律，失败请求不发布。</summary>
    public struct RegionControlledUnitChangedSignal
    {
        public string RegionId;
        /// <summary>0 = 之前没有受控目标。</summary>
        public int PreviousLogicId;
        /// <summary>0 = 变更后处于战略视角（无受控目标）。</summary>
        public int CurrentLogicId;
        public RegionControlChangeReason Reason;
        /// <summary>CurrentLogicId 为 0 时，表现层的战略回退锚点。</summary>
        public Vector2 FallbackAnchor;
    }

    /// <summary>宿主区域 Controller 提供的最小上下文——用委托而不是接口，避免要求
    /// <see cref="HomeValleyController"/>/<see cref="FracturedCityController"/> 改继承结构
    /// （同 <see cref="RegionSquadCommandContext"/> 先例）。</summary>
    public sealed class RegionControlContext
    {
        public string RegionId;
        /// <summary>本区域当前全部可视化机器标记（含已受控的那一台）。</summary>
        public List<HomeValleyMachineMarker> Markers;
        public System.Func<HomeValleyMachineMarker> GetPossessed;
        /// <summary>真正落 Controller 自己的 <c>_possessed</c> 字段——本类不持有第二份权威状态。</summary>
        public System.Action<HomeValleyMachineMarker> SetPossessed;
        public System.Func<bool> IsCameraTransitioning;
        /// <summary>可空：本区域没有干扰机制（归还谷地）时传 null，SignalJammed/Suspended 永不触发。</summary>
        public System.Func<Vector2, bool> IsPositionJammed;
        public RegionSquadCommandSystem SquadCommands;
        /// <summary>接管成功提交后的收尾（取消工作单占用、MachineRegistry 统计登记等），logicId 参数。</summary>
        public System.Action<int> OnPossessCommitted;
        /// <summary>释放（切换出去/回战略/死亡/失联）时的收尾，logicId 参数为被释放的旧目标。</summary>
        public System.Action<int> OnReleased;
        /// <summary>信号事件的回退锚点来源（战略镜头当前注视点/安全点）。</summary>
        public System.Func<Vector2> FallbackAnchor;
        public float JamGraceSeconds = 2f;
    }

    public sealed class RegionControlSystem
    {
        private RegionControlContext _ctx;
        private float _jamGraceRemaining;
        /// <summary>FG1-SIG-04：当前宽限的原因（干扰场 / 走出覆盖）；None = 不在宽限里。</summary>
        private Signal.SignalLinkBreakReason _suspendReason;
        /// <summary>FG1-SIG-03：绑定时的战役（卸载时只改这一份的信号位置——读档流程先卸载旧世界再换会话，不会误改新档）。</summary>
        private CampaignState _boundState;

        /// <summary>FG1-SIG-03：已绑定的区域接管系统（区域 ID → 系统）。信号接入服务按目标机器所在区域找到它（O(1)），
        /// 不经过任何具体的区域控制器类型。</summary>
        private static readonly Dictionary<string, RegionControlSystem> ByRegion = new Dictionary<string, RegionControlSystem>();

        public static RegionControlSystem ForRegion(string regionId) =>
            regionId != null && ByRegion.TryGetValue(regionId, out RegionControlSystem c) && c._ctx != null ? c : null;

        public string RegionId => _ctx?.RegionId;
        public CampaignState BoundState => _boundState;

        /// <summary>当前受控（信号接入）的机器；没有为 0。</summary>
        public int PossessedLogicId => _ctx?.GetPossessed?.Invoke()?.LogicId ?? 0;

        /// <summary>AC-CTL-008 验收用：本局累计成功切换次数（含死亡回弹）。</summary>
        public int SwitchCount { get; private set; }
        public RegionControlAvailability Availability { get; private set; } = RegionControlAvailability.None;

        public void Bind(RegionControlContext ctx)
        {
            _ctx = ctx;
            _boundState = CampaignSession.Current;
            SwitchCount = 0;
            Availability = RegionControlAvailability.None;
            _jamGraceRemaining = 0f;
            _suspendReason = Signal.SignalLinkBreakReason.None;
            if (ctx?.RegionId != null)
            {
                ByRegion[ctx.RegionId] = this;
            }
        }

        public void Unbind()
        {
            string regionId = _ctx?.RegionId;
            CampaignState bound = _boundState;
            _ctx = null;
            _boundState = null;
            if (regionId != null && ByRegion.TryGetValue(regionId, out RegionControlSystem c) && c == this)
            {
                ByRegion.Remove(regionId);
            }
            // FG1-SIG-03：地点卸载（撤离 / 放弃远征 / 回主菜单）时，信号若在这里的机器里，回到归还核心。
            Signal.SignalUplinkService.OnRegionUnbound(regionId, bound);
        }

        /// <summary>本区域里某台机器的句柄（在场才有）。</summary>
        public bool TryGetMarker(int logicId, out HomeValleyMachineMarker marker)
        {
            marker = _ctx != null ? FindMarker(logicId) : null;
            return marker != null;
        }

        /// <summary>FG1-SIG-03：这个位置在不在干扰场里（本区域没有干扰机制时恒为 false）。</summary>
        public bool IsJammedAt(Vector2 position) => _ctx?.IsPositionJammed != null && _ctx.IsPositionJammed(position);

        /// <summary>FG1-SIG-03：Tab 循环的下一台候选（按 LogicId 稳定顺序，与 <see cref="TrySwitchControlledUnit"/>(null) 同一规则）；没有候选为 0。</summary>
        public int NextCandidateAfter(int? afterLogicId) => _ctx == null ? 0 : NextCandidate(afterLogicId)?.LogicId ?? 0;

        /// <summary>
        /// FG1-SIG-03（FGR-SIG-031）：信号接入过渡结束时提交接管。与 <see cref="TrySwitchControlledUnit"/> 同一套目标校验与提交
        /// （存活、本区域、在场、不在工厂、干扰场），区别只是不拦“镜头过渡中 / 面板打开”——那两条是**发起**接入时的门槛，
        /// 由 <c>SignalUplinkService.Request</c> 在发起时判定；过渡本身就是镜头在动，提交时再拦会让每次接入都失败。
        /// 不出接管音（接入服务自己给“已接入”反馈，避免一件事两条字幕）。
        /// <paramref name="restoring"/>：读档恢复“存档时信号在这台机器里”——不是一次新的接入，不计接管次数与机器经历（读档后状态逐字段一致）。
        /// </summary>
        public RegionControlSwitchResult CommitUplink(int logicId, bool restoring = false)
        {
            if (_ctx == null)
            {
                return RegionControlSwitchResult.Fail(RegionControlFailure.Ineligible);
            }
            if (!TryResolveExplicitTarget(logicId, out HomeValleyMachineMarker target, out RegionControlFailure failure))
            {
                return RegionControlSwitchResult.Fail(failure);
            }
            return CommitSwitch(target, restoring);
        }

        /// <summary>FG1-SIG-03：读档恢复接入的提交过程中为 true——宿主的“接管完成”回调据此不补记接管统计 / 机器经历（那是存档里已有的）。</summary>
        public static bool IsRestoringUplink { get; private set; }

        /// <summary>唯一的接管请求入口。<paramref name="explicitLogicId"/> 为 null 时走 Tab 循环
        /// （按 LogicId 稳定顺序挑下一个合法候选）；给出具体值时供候选条按钮/程序化调用。
        /// 拒绝（Success=false）时不改变当前受控目标，不发布事件。
        ///
        /// ER8-CONTENT-01 AC-AUD-001：成功＝“接管”音与字幕，拒绝＝“拒绝”音与原因字幕。
        /// “已在操控/镜头切换中/面板打开”三种是无害的重复按键，不出拒绝音。</summary>
        public RegionControlSwitchResult TrySwitchControlledUnit(int? explicitLogicId)
        {
            RegionControlSwitchResult result = TrySwitchControlledUnitCore(explicitLogicId);
            if (result.Success)
            {
                FeedbackCues.Raise(FeedbackCueId.Takeover, FeedbackCues.MachineLabel(result.LogicId));
            }
            else if (result.Failure != RegionControlFailure.AlreadyControlled
                     && result.Failure != RegionControlFailure.TransitionInProgress
                     && result.Failure != RegionControlFailure.ModalBlocked)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied, result.PlayerText);
            }
            return result;
        }

        private RegionControlSwitchResult TrySwitchControlledUnitCore(int? explicitLogicId)
        {
            if (_ctx == null)
            {
                return RegionControlSwitchResult.Fail(RegionControlFailure.Ineligible);
            }
            if (InputRouter.ModalUiOpen)
            {
                return RegionControlSwitchResult.Fail(RegionControlFailure.ModalBlocked);
            }
            if (_ctx.IsCameraTransitioning != null && _ctx.IsCameraTransitioning())
            {
                return RegionControlSwitchResult.Fail(RegionControlFailure.TransitionInProgress);
            }

            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            HomeValleyMachineMarker target;

            if (explicitLogicId.HasValue)
            {
                if (!TryResolveExplicitTarget(explicitLogicId.Value, out target, out RegionControlFailure failure))
                {
                    return RegionControlSwitchResult.Fail(failure);
                }
            }
            else
            {
                target = NextCandidate(current?.LogicId);
                if (target == null)
                {
                    return RegionControlSwitchResult.Fail(RegionControlFailure.TargetNotFound);
                }
                if (current != null && current.LogicId == target.LogicId)
                {
                    return RegionControlSwitchResult.Fail(RegionControlFailure.AlreadyControlled);
                }
                Vector3 p = target.Position3;
                if (IsJammedAt(new Vector2(p.x, p.z)))
                {
                    return RegionControlSwitchResult.Fail(RegionControlFailure.SignalJammed);
                }
            }
            return CommitSwitch(target);
        }

        /// <summary>显式目标的校验（接管请求与接入提交共用）：存在、存活、本区域、在场、不在工厂、不是当前受控、不在干扰场。</summary>
        private bool TryResolveExplicitTarget(int logicId, out HomeValleyMachineMarker target, out RegionControlFailure failure)
        {
            target = null;
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
            {
                failure = RegionControlFailure.TargetNotFound;
                return false;
            }
            if (!rec.IsAlive)
            {
                failure = RegionControlFailure.TargetDead;
                return false;
            }
            if (rec.RegionId != _ctx.RegionId)
            {
                failure = RegionControlFailure.TargetNotFriendly;
                return false;
            }
            target = FindMarker(logicId);
            if (target == null)
            {
                failure = RegionControlFailure.OutOfRange;
                return false;
            }
            if (rec.IsInFactory)
            {
                failure = RegionControlFailure.Ineligible;
                return false;
            }
            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            if (current != null && current.LogicId == target.LogicId)
            {
                failure = RegionControlFailure.AlreadyControlled;
                return false;
            }
            Vector3 p = target.Position3;
            if (IsJammedAt(new Vector2(p.x, p.z)))
            {
                failure = RegionControlFailure.SignalJammed;
                return false;
            }
            failure = RegionControlFailure.None;
            return true;
        }

        /// <summary>提交一次接管：释放旧的、占用新的、统计、发布变更（接入服务据此移动信号、重编译两台机器）。</summary>
        private RegionControlSwitchResult CommitSwitch(HomeValleyMachineMarker target, bool restoring = false)
        {
            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            Vector3 targetPos = target.Position3;
            var targetPos2 = new Vector2(targetPos.x, targetPos.z);
            int previousLogicId = current != null ? current.LogicId : 0;
            ReleaseInternal(current);

            target.CancelCommandMove();
            _ctx.SetPossessed?.Invoke(target);
            IsRestoringUplink = restoring;
            try
            {
                _ctx.OnPossessCommitted?.Invoke(target.LogicId);
            }
            finally
            {
                IsRestoringUplink = false;
            }
            _jamGraceRemaining = _ctx.JamGraceSeconds;
            Availability = RegionControlAvailability.Controlled;
            if (!restoring)
            {
                SwitchCount++;
                // ER7-CREDITS-01：战役级"接管次数"统计——唯一写入口，见 CampaignState.TotalControlTakeovers
                // 类注释（与本类自己的 SwitchCount 是两件独立的事，不要合并）。
                CampaignState state = CampaignSession.Current;
                if (state != null)
                {
                    state.TotalControlTakeovers++;
                }
            }

            PublishChange(previousLogicId, target.LogicId, RegionControlChangeReason.PlayerRequest, targetPos2);
            return RegionControlSwitchResult.Ok(target.LogicId);
        }

        /// <summary>镜头已经/即将落回战略视角时调用（M 键手动切换或用户其它主动退出路径）——
        /// 不是死亡/失联触发的那两条，专用于"玩家主动放下"这一种 Reason。没有受控目标时是安全 no-op。</summary>
        public void ReleaseToStrategy(RegionControlChangeReason reason = RegionControlChangeReason.PlayerRequest)
        {
            if (_ctx == null)
            {
                return;
            }
            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            if (current == null)
            {
                return;
            }
            int previousLogicId = current.LogicId;
            Vector3 p = current.Position3;
            ReleaseInternal(current);
            _ctx.SetPossessed?.Invoke(null);
            Availability = RegionControlAvailability.None;
            PublishChange(previousLogicId, 0, reason, ResolveFallbackAnchor(new Vector2(p.x, p.z)));
        }

        /// <summary>每帧驱动：受控机死亡侦测（AC-CTL-003 回弹）+ 断链判定（FG1-SIG-04 / FGR-SIG-040）：静默夜立即断；干扰场或走出信号覆盖进入宽限
        /// （AC-CTL-004 Suspended，宽限内回来就恢复），宽限耗尽断链——信号弹回归还核心，机器进入安全模式（原因、音效、安全模式由 <c>SignalLinkService</c> 给出）。
        /// 由 Controller.Update 在暂停早退之后调用，<paramref name="dt"/> 是按倍速缩放的游戏时间（暂停为 0）——宽限按游戏秒走，与倍速一致。
        /// 开销 O(覆盖源数)：只看被接入的那一台。</summary>
        public void Tick(float dt)
        {
            if (_ctx == null)
            {
                return;
            }
            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            if (current == null)
            {
                Availability = RegionControlAvailability.None;
                _suspendReason = Signal.SignalLinkBreakReason.None;
                return;
            }

            if (!MachineRegistry.TryGetRecord(current.LogicId, out MachineRecord rec) || !rec.IsAlive)
            {
                HandleDeathRebound(current);
                return;
            }

            Vector3 p = current.Position3;
            var pos2 = new Vector2(p.x, p.z);

            // 静默夜：不给宽限，立即弹回（FG07 负责静默夜前的预警，见 SignalLinkService.AnnounceSilentNight）。
            if (Signal.SignalLinkService.IsSilentNight)
            {
                ForceBreak(RegionControlChangeReason.SilentNight);
                return;
            }

            bool jammed = IsJammedAt(pos2);
            Signal.SignalCoverageSample coverage = Signal.SignalCoverageService.Sample(_ctx.RegionId, pos2);
            Signal.SignalLinkBreakReason cause = jammed ? Signal.SignalLinkBreakReason.Jammed
                : !coverage.Covered ? Signal.SignalLinkBreakReason.OutOfCoverage
                : Signal.SignalLinkBreakReason.None;

            if (cause == Signal.SignalLinkBreakReason.None)
            {
                bool wasSuspended = Availability == RegionControlAvailability.Suspended;
                Signal.SignalLinkBreakReason was = _suspendReason;
                _jamGraceRemaining = _ctx.JamGraceSeconds;
                _suspendReason = Signal.SignalLinkBreakReason.None;
                Availability = RegionControlAvailability.Controlled;
                Signal.SignalLinkService.Watch(current.LogicId, pos2, coverage, Signal.SignalLinkBreakReason.None, 0f);
                if (wasSuspended)
                {
                    Signal.SignalLinkService.OnGraceRecovered(current.LogicId, pos2, was);
                    PublishChange(current.LogicId, current.LogicId, RegionControlChangeReason.SignalRestored, pos2);
                }
                return;
            }

            float grace = cause == Signal.SignalLinkBreakReason.Jammed ? _ctx.JamGraceSeconds : Signal.SignalLinkService.GraceSeconds;
            if (Availability != RegionControlAvailability.Suspended || _suspendReason != cause)
            {
                // 进入宽限的第一帧（或原因换了）：先预警（宽限期内回来即可恢复），宽限耗尽时再断链。
                _jamGraceRemaining = Availability == RegionControlAvailability.Suspended ? Mathf.Min(_jamGraceRemaining, grace) : grace;
                Signal.SignalLinkService.OnGraceStarted(current.LogicId, pos2, cause, grace);
            }
            _suspendReason = cause;
            Availability = RegionControlAvailability.Suspended;
            _jamGraceRemaining -= dt;
            Signal.SignalLinkService.Watch(current.LogicId, pos2, coverage, cause, _jamGraceRemaining);
            if (_jamGraceRemaining > 0f)
            {
                return;
            }

            Log.Info($"[RegionControlSystem] 机器 {current.LogicId} 宽限期耗尽（{cause}），信号弹回归还核心（{_ctx.RegionId}）。");
            ForceBreak(cause == Signal.SignalLinkBreakReason.Jammed ? RegionControlChangeReason.SignalLost : RegionControlChangeReason.CoverageLost);
        }

        /// <summary>
        /// FG1-SIG-04：强制断链——释放受控机（沿用主动退出的释放规则：Move/Attack 取消，Guard/Retreat 继续，恢复 AI 教义），信号弹回归还核心。
        /// <paramref name="reason"/> 只能是 <see cref="RegionControlChangeReason.SignalLost"/>（干扰）/ <see cref="RegionControlChangeReason.CoverageLost"/> /
        /// <see cref="RegionControlChangeReason.SilentNight"/>。没有受控机时返回 false。
        /// </summary>
        public bool ForceBreak(RegionControlChangeReason reason)
        {
            if (_ctx == null)
            {
                return false;
            }
            HomeValleyMachineMarker current = _ctx.GetPossessed?.Invoke();
            if (current == null)
            {
                return false;
            }
            Vector3 p = current.Position3;
            var pos2 = new Vector2(p.x, p.z);
            ReleaseInternal(current);
            _ctx.SetPossessed?.Invoke(null);
            Availability = RegionControlAvailability.None;
            _suspendReason = Signal.SignalLinkBreakReason.None;
            _jamGraceRemaining = _ctx.JamGraceSeconds;
            PublishChange(current.LogicId, 0, reason, ResolveFallbackAnchor(pos2));
            return true;
        }

        /// <summary>AC-CTL-003：受控机阵亡时按距离（近者优先）再 LogicId（近似并列时升序）确定性
        /// 选出下一个回弹目标；无候选转 None（由调用方 CameraDirector 的既有"锚点失效自动回退战略视角"
        /// 兜底，这里只负责把 <c>_possessed</c> 正确置空/置新）。</summary>
        private void HandleDeathRebound(HomeValleyMachineMarker deadMarker)
        {
            Vector3 deadPos = deadMarker.Position3;
            var deadPos2 = new Vector2(deadPos.x, deadPos.z);

            HomeValleyMachineMarker best = null;
            float bestDist = float.MaxValue;
            if (_ctx.Markers != null)
            {
                foreach (HomeValleyMachineMarker m in _ctx.Markers)
                {
                    if (m == null || m == deadMarker)
                    {
                        continue;
                    }
                    // FG1-SIG-04（FGR-SIG-040 / FG-GAP-034）：只回弹到“合适”的目标——与发起接入同一套目标条件
                    // （存活、不在静默夜、在与核心连通的覆盖里、不在工厂 / 维修台 / 投送途中、没被干扰；只少“地点正被观察”）；一台都没有就回到归还核心。
                    Vector3 mp = m.Position3;
                    var mp2 = new Vector2(mp.x, mp.z);
                    if (Signal.SignalUplinkService.ReboundTargetFailure(this, m.LogicId, mp2) != Signal.UplinkFailure.None)
                    {
                        continue;
                    }
                    float dist = Vector3.Distance(deadPos, m.Position3);
                    if (best == null || dist < bestDist - 0.0001f ||
                        (dist <= bestDist + 0.0001f && m.LogicId < best.LogicId))
                    {
                        bestDist = dist;
                        best = m;
                    }
                }
            }

            int previousLogicId = deadMarker.LogicId;
            ReleaseInternal(deadMarker);
            _ctx.SetPossessed?.Invoke(null);
            _suspendReason = Signal.SignalLinkBreakReason.None;

            if (best != null)
            {
                best.CancelCommandMove();
                _ctx.SetPossessed?.Invoke(best);
                _ctx.OnPossessCommitted?.Invoke(best.LogicId);
                _jamGraceRemaining = _ctx.JamGraceSeconds;
                Availability = RegionControlAvailability.Controlled;
            }
            else
            {
                Availability = RegionControlAvailability.None;
            }

            Log.Info(best != null
                ? $"[RegionControlSystem] 机器 {previousLogicId} 阵亡，控制权回弹到机器 {best.LogicId}（{_ctx.RegionId}）。"
                : $"[RegionControlSystem] 机器 {previousLogicId} 阵亡，本区域无可回弹候选，控制权转回战略视角（{_ctx.RegionId}）。");

            PublishChange(previousLogicId, best?.LogicId ?? 0, RegionControlChangeReason.DeathRebound,
                ResolveFallbackAnchor(deadPos2));
        }

        private void ReleaseInternal(HomeValleyMachineMarker current)
        {
            if (current == null)
            {
                return;
            }
            // AC-CTL-007："退出后只恢复合法 Guard/Retreat，不恢复死亡或跨区目标"——Move/Attack 是
            // 一次性命令，玩家亲自驾驶期间目标可能早已失效/位置已变，释放时直接取消而不是让它带着
            // 接管前的旧目标突然复活；Guard/Retreat 原地不动，留在 SquadCommands._active 里，
            // 解除 IsDirectControlled 后下一帧自动继续（TickActiveCommands 的既有冻结/解冻机制）。
            if (_ctx.SquadCommands != null &&
                _ctx.SquadCommands.TryGetActiveCommandKind(current.LogicId, out RegionCommandKind kind) &&
                (kind == RegionCommandKind.Move || kind == RegionCommandKind.Attack))
            {
                _ctx.SquadCommands.CancelCommandFor(current.LogicId);
            }
            _ctx.OnReleased?.Invoke(current.LogicId);
        }

        /// <summary>Tab 循环：按 LogicId 稳定顺序找下一个合法候选（同旧 <c>CycleControlTarget</c> 排序
        /// 手感——不按距离，避免两台机器之间来回跳）；只有一台合法机器时返回它自己，行为等价于原地不动。</summary>
        private HomeValleyMachineMarker NextCandidate(int? afterLogicId)
        {
            if (_ctx.Markers == null)
            {
                return null;
            }
            HomeValleyMachineMarker first = null;
            HomeValleyMachineMarker next = null;
            foreach (HomeValleyMachineMarker m in _ctx.Markers)
            {
                if (m == null || !MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec) || !rec.IsAlive || rec.IsInFactory)
                {
                    continue;
                }
                if (first == null || m.LogicId < first.LogicId)
                {
                    first = m;
                }
                if (afterLogicId.HasValue && m.LogicId > afterLogicId.Value && (next == null || m.LogicId < next.LogicId))
                {
                    next = m;
                }
            }
            return next ?? first;
        }

        private HomeValleyMachineMarker FindMarker(int logicId)
        {
            if (_ctx.Markers == null)
            {
                return null;
            }
            foreach (HomeValleyMachineMarker m in _ctx.Markers)
            {
                if (m != null && m.LogicId == logicId)
                {
                    return m;
                }
            }
            return null;
        }

        /// <summary>候选条 UI 用：本区域当前全部合法候选（存活/不在厂内），按 LogicId 升序。</summary>
        public List<int> GetCandidateLogicIds()
        {
            var result = new List<int>(8);
            if (_ctx?.Markers == null)
            {
                return result;
            }
            foreach (HomeValleyMachineMarker m in _ctx.Markers)
            {
                if (m != null && MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec) && rec.IsAlive && !rec.IsInFactory)
                {
                    result.Add(m.LogicId);
                }
            }
            result.Sort();
            return result;
        }

        private Vector2 ResolveFallbackAnchor(Vector2 fallback)
        {
            return _ctx.FallbackAnchor != null ? _ctx.FallbackAnchor() : fallback;
        }

        private void PublishChange(int previousLogicId, int currentLogicId, RegionControlChangeReason reason, Vector2 fallbackAnchor)
        {
            // FG1-SIG-03：信号位置的唯一真相在信号核状态域（存档）里——每一次接管变更（接入、切换、离开、阵亡回弹、失联）都经这里同步过去，
            // 并通知两台机器重编译（离开的回到本地配置，接入的插入信号核固件）。
            Signal.SignalUplinkService.OnControlChanged(this, previousLogicId, currentLogicId, reason);
            Signals.Publish(new RegionControlledUnitChangedSignal
            {
                RegionId = _ctx.RegionId,
                PreviousLogicId = previousLogicId,
                CurrentLogicId = currentLogicId,
                Reason = reason,
                FallbackAnchor = fallbackAnchor,
            });
        }

        public static string TextFor(RegionControlFailure failure)
        {
            switch (failure)
            {
                case RegionControlFailure.TargetNotFound: return "没有可接管的目标。";
                case RegionControlFailure.TargetNotFriendly: return "目标不属于本区域。";
                case RegionControlFailure.TargetDead: return "目标已阵亡。";
                case RegionControlFailure.OutOfRange: return "目标当前不在场。";
                case RegionControlFailure.SignalJammed: return "信号被干扰，接管失败。";
                case RegionControlFailure.TransitionInProgress: return "镜头切换中，请稍候。";
                case RegionControlFailure.ModalBlocked: return "请先关闭当前面板。";
                case RegionControlFailure.AlreadyControlled: return "已经在操控这台机器。";
                case RegionControlFailure.Ineligible: return "目标暂不可接管。";
                default: return "接管失败。";
            }
        }
    }
}
