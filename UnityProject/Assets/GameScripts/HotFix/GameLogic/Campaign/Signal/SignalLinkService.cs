using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>FG1-SIG-04（FGR-SIG-040）：信号被强制弹回的四种原因（稳定码，存档 / 测试 / 界面共用；数值只追加不重排）。</summary>
    public enum SignalLinkBreakReason : byte
    {
        None = 0,
        /// <summary>走出与归还核心连通的信号覆盖。</summary>
        OutOfCoverage = 1,
        /// <summary>进入干扰场（宽限期耗尽）。</summary>
        Jammed = 2,
        /// <summary>静默夜开始（FG07；本 Story 只留接口）。</summary>
        SilentNight = 3,
        /// <summary>被接入的机器阵亡（沿用死亡回弹；没有合适的回弹目标就回到归还核心）。</summary>
        MachineDestroyed = 4,
    }

    /// <summary>跨模块事件 <see cref="SignalLinkService.LinkBrokenEvent"/> 的载荷：一次断链。</summary>
    public struct SignalLinkBreak
    {
        /// <summary>失去信号的机器。</summary>
        public int LogicId;
        public SignalLinkBreakReason Reason;
        /// <summary>阵亡时回弹到的机器；0 = 回到归还核心。</summary>
        public int ReboundLogicId;
        public string SiteId;
        /// <summary>失去信号的机器进入了安全模式（阵亡时为 false）。</summary>
        public bool EnteredSafeMode;
    }

    /// <summary>跨模块事件 <see cref="SignalLinkService.SafeModeChangedEvent"/> 的载荷：一台机器进入 / 退出安全模式。</summary>
    public struct SignalSafeModeChange
    {
        public int LogicId;
        public bool InSafeMode;
        public SignalLinkBreakReason Reason;
    }

    /// <summary>
    /// FG1-SIG-04 断链与安全模式（FG01 FGR-SIG-040、041；验收 FGT-SIG-006）。
    ///
    /// ── 断链（FGR-SIG-040）──
    /// 判定在区域接管系统（<see cref="RegionControlSystem.Tick"/>，被接入的机器所在的地点每帧）：
    /// 干扰场 / 走出覆盖 → 宽限（signal.link.grace_seconds，游戏秒；期间预警、回来就恢复，边缘反复进出不会断）→ 耗尽即断链；
    /// 静默夜（<see cref="SignalUplinkService.SilentNightProvider"/> 判定入口，或 FG07 调 <see cref="OnSilentNightStarted"/>）→ 立即断链；
    /// 阵亡 → 死亡回弹（只回弹到“能接入”的机器：没被干扰、在覆盖里、不在静默夜……），没有合适目标就回到归还核心。
    /// 信号位置仍由 <see cref="SignalUplinkService.OnControlChanged"/> 唯一写入；本类在它之后给出原因（HUD + 音效 + 字幕）并处理安全模式。
    ///
    /// ── 安全模式（FGR-SIG-041）──
    /// 因断链（干扰 / 覆盖 / 静默夜）失去信号的机器进入安全模式：只运行本地常规固件（结算回到 AI 配置，接入口为空——信号不在这台里时
    /// <see cref="MachineLoadoutRegistry.ResolveForPilot"/> 本来就是 AI 配置，这里不另造一套），继续执行最后一条命令或 AI 教义（沿用 FC-REQ-051 的释放规则）。
    /// 记录进存档（<see cref="SignalCoreState.SafeModes"/>）；头顶图标与机器列表标记读它。
    /// 退出：信号再次接入这台机器；或断链的条件消失并持续 signal.safe_mode.exit_seconds（游戏秒，防止在边缘反复进出时图标闪烁）；或机器阵亡 / 消失。
    /// 退出判定在世界模拟步里按游戏时间每 signal.safe_mode.check_seconds 做一次，与是否被观察无关（FGR-BASE-021），开销 O(安全模式机器数)。
    ///
    /// ── 预警（卡片“地图上覆盖边缘的预警”）──
    /// 被接入的机器离覆盖边缘不到 signal.coverage.edge_warn_cells 格时：HUD 状态行预警 + 一次预警音 + 地图上画出罩着它的覆盖圈（<c>SignalLinkView</c>）；
    /// 退回 3 格以上才解除（回差，边缘反复进出不闪）。每帧 O(覆盖源数)，只采样被接入的那一台。
    /// </summary>
    public static class SignalLinkService
    {
        public const string LinkBrokenEvent = "SignalLink.Broken";
        public const string SafeModeChangedEvent = "SignalLink.SafeModeChanged";
        /// <summary>FG07 静默夜预警（预留）：载荷 float = 还有几秒开始。</summary>
        public const string SilentNightWarningEvent = "SignalLink.SilentNightWarning";

        public const string SafeModeIconId = GameLogic.UI.Common.ContentIcons.StateSafeMode;
        /// <summary>边缘预警的回差（格）：进入预警在 edge_warn_cells 以内，解除要退回到 edge_warn_cells + 本值以外。</summary>
        public static float EdgeWarnHysteresisCells => Tuning("signal.coverage.edge_warn_hysteresis_cells", 3f);

        // ── 统计（自检用）──
        public static int BreakCount { get; private set; }
        public static SignalLinkBreakReason LastBreakReason { get; private set; }
        public static int LastBreakLogicId { get; private set; }
        public static int LastReboundLogicId { get; private set; }
        public static double LastBreakGameSeconds { get; private set; } = -1;
        public static int SafeModeEnterCount { get; private set; }
        public static int SafeModeExitCount { get; private set; }
        public static int WarningCueCount { get; private set; }
        public static int SafeModeChecks { get; private set; }
        /// <summary>安全模式任何变化 +1（界面：头顶图标、机器列表据此刷新）。</summary>
        public static int SafeModeRevision { get; private set; } = 1;
        /// <summary>预警状态任何变化 +1（HUD 状态行、地图预警圈据此刷新）。</summary>
        public static int WarningRevision { get; private set; } = 1;

        // ── 被接入的机器的链路预警（运行时，不进存档：宽限几秒，读档后从头计）──
        private static int _watchLogicId;
        private static bool _edgeWarning;
        private static SignalLinkBreakReason _suspendReason;
        private static float _graceLeft;
        private static SignalCoverageSample _watchSample;
        // 每种提示各记一份“上次为哪台机器、在什么真实时刻响过”（种类交替出现时互不冲掉）。
        private static readonly float[] LastCueAt = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        private static readonly int[] LastCueLogicId = new int[4];
        // 防刷用的提示种类（同一台、同一种，在 warn_repeat_seconds 内只响一次）。
        private const int CueRecovered = 0;
        private const int CueCoverageGrace = 1;
        private const int CueJamGrace = 2;
        private const int CueEdge = 3;
        private static string _warningLine = string.Empty;
        private static int _warningLineKey;

        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        public static float GraceSeconds => Tuning("signal.link.grace_seconds", 2f);
        public static float SafeModeExitSeconds => Tuning("signal.safe_mode.exit_seconds", 2f);
        public static float SafeModeCheckSeconds => Tuning("signal.safe_mode.check_seconds", 0.5f);
        public static float WarnRepeatSeconds => Tuning("signal.link.warn_repeat_seconds", 3f);

        /// <summary>现在是不是静默夜（判定入口 <see cref="SignalUplinkService.SilentNightProvider"/>；FG07 昼夜接入前恒为否）。</summary>
        public static bool IsSilentNight => SignalUplinkService.SilentNightProvider != null && SignalUplinkService.SilentNightProvider();

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static bool IsInSafeMode(CampaignState s, int logicId) => Find(s, logicId) != null;

        public static SignalLinkBreakReason SafeModeReason(CampaignState s, int logicId)
        {
            SignalSafeModeRecord r = Find(s, logicId);
            return r != null ? (SignalLinkBreakReason)r.Reason : SignalLinkBreakReason.None;
        }

        public static int SafeModeCount(CampaignState s) => s?.SignalCore?.SafeModes?.Length ?? 0;

        /// <summary>被接入的机器现在是否在边缘预警 / 宽限中（地图预警圈读它）。</summary>
        public static bool EdgeWarningActive => _watchLogicId != 0 && (_edgeWarning || _suspendReason == SignalLinkBreakReason.OutOfCoverage);
        public static bool InGrace => _watchLogicId != 0 && _suspendReason != SignalLinkBreakReason.None;
        public static SignalLinkBreakReason GraceReason => _watchLogicId != 0 ? _suspendReason : SignalLinkBreakReason.None;
        public static SignalCoverageSample WatchedSample => _watchSample;
        /// <summary>FG1-HUD-01：宽限还剩多少秒（接入 HUD 的链路行“N 秒内恢复”）；不在宽限中为 0。</summary>
        public static float GraceLeft => InGrace ? _graceLeft : 0f;
        public static int WatchedLogicId => _watchLogicId;

        /// <summary>HUD 状态行的链路预警（空 = 没有预警）。</summary>
        public static string WarningLine => _warningLine;

        /// <summary>断链原因文本（HUD / 字幕 / 机器列表说明共用）。</summary>
        public static string ReasonName(SignalLinkBreakReason r) => r switch
        {
            SignalLinkBreakReason.OutOfCoverage => GameText.Get("signal.link.reason_name.out_of_coverage"),
            SignalLinkBreakReason.Jammed => GameText.Get("signal.link.reason_name.jammed"),
            SignalLinkBreakReason.SilentNight => GameText.Get("signal.link.reason_name.silent_night"),
            SignalLinkBreakReason.MachineDestroyed => GameText.Get("signal.link.reason_name.destroyed"),
            _ => string.Empty,
        };

        /// <summary>机器列表按钮上的安全模式说明（进入原因 + 行为 + 恢复条件）。</summary>
        public static string SafeModeTooltip(CampaignState s, int logicId)
        {
            SignalSafeModeRecord r = Find(s, logicId);
            if (r == null)
            {
                return string.Empty;
            }
            return GameText.Format("signal.safe_mode.tooltip", SignalPresence.MachineLabel(logicId), ReasonName((SignalLinkBreakReason)r.Reason));
        }

        // ─────────────────────────────── 链路监视（被接入的那一台，每帧）───────────────────────────────

        /// <summary>
        /// 区域接管系统每帧把被接入的机器的链路状况报上来：覆盖采样、是否在干扰场、是否在宽限中（哪种原因、还剩几秒）。
        /// 这里只负责预警（HUD 行、预警音、地图预警圈的数据）；断不断链由接管系统按宽限决定。
        /// </summary>
        public static void Watch(int logicId, Vector2 position, SignalCoverageSample sample, SignalLinkBreakReason suspendReason, float graceLeft)
        {
            bool changed = logicId != _watchLogicId || suspendReason != _suspendReason;
            if (logicId != _watchLogicId)
            {
                _edgeWarning = false;
            }
            _watchLogicId = logicId;
            _watchSample = sample;
            _suspendReason = suspendReason;
            _graceLeft = Mathf.Max(0f, graceLeft);

            bool wasEdge = _edgeWarning;
            if (sample.Bounded && sample.Covered)
            {
                float warn = SignalCoverageService.EdgeWarnCells;
                if (!_edgeWarning && sample.MarginCells < warn)
                {
                    _edgeWarning = true;
                }
                else if (_edgeWarning && sample.MarginCells > warn + EdgeWarnHysteresisCells)
                {
                    _edgeWarning = false;
                }
            }
            else
            {
                _edgeWarning = false;
            }
            if (_edgeWarning && !wasEdge)
            {
                RaiseWarningCue(logicId, position,
                    GameText.Format("signal.link.warn.edge", SignalPresence.MachineLabel(logicId), CellsText(sample.MarginCells)));
            }
            if (changed || wasEdge != _edgeWarning)
            {
                WarningRevision++;
            }
            RebuildWarningLine();
        }

        /// <summary>接管系统进入宽限（干扰 / 走出覆盖）的第一帧：预警音 + 字幕（同一台同一种原因在 warn_repeat_seconds 内只响一次，边缘反复进出不刷屏）。</summary>
        public static void OnGraceStarted(int logicId, Vector2 position, SignalLinkBreakReason reason, float graceSeconds)
        {
            string key = reason == SignalLinkBreakReason.Jammed ? "signal.link.warn.jammed" : "signal.link.warn.out_of_coverage";
            string text = GameText.Format(key, SignalPresence.MachineLabel(logicId), graceSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            int kind = reason == SignalLinkBreakReason.Jammed ? CueJamGrace : CueCoverageGrace;
            if (ShouldCue(logicId, kind))
            {
                MarkCue(logicId, kind);
                WarningCueCount++;
                FeedbackCues.RaiseAt(FeedbackCueId.SignalLost, position, text);
            }
            // HUD 状态行不写这条固定文本：宽限期间状态行由 WarningLine 显示逐秒倒计时（它优先于普通反馈，见 SignalUplinkService.StatusLine）。
        }

        /// <summary>宽限期内回到覆盖 / 离开干扰场：信号保住（恢复音同样防刷）。</summary>
        public static void OnGraceRecovered(int logicId, Vector2 position, SignalLinkBreakReason reason)
        {
            string text = GameText.Format("signal.link.recovered", SignalPresence.MachineLabel(logicId));
            if (ShouldCue(logicId, CueRecovered))
            {
                MarkCue(logicId, CueRecovered);
                FeedbackCues.RaiseAt(FeedbackCueId.SignalRestored, position, text);
            }
            SignalUplinkService.PushFeedback(text);
        }

        /// <summary>信号不在任何机器里（离开 / 断链 / 地点卸载）：清掉链路监视。</summary>
        public static void ClearWatch()
        {
            if (_watchLogicId == 0 && _warningLine.Length == 0)
            {
                return;
            }
            _watchLogicId = 0;
            _edgeWarning = false;
            _suspendReason = SignalLinkBreakReason.None;
            _graceLeft = 0f;
            _watchSample = default;
            _warningLine = string.Empty;
            // 清掉行的同时清掉它的变化键：离开后原地重新接入同一台（同一格数）时预警行要重新生成，不能被旧键挡掉。
            _warningLineKey = 0;
            WarningRevision++;
        }

        private static void RebuildWarningLine()
        {
            string line = string.Empty;
            int key;
            if (_watchLogicId == 0)
            {
                key = 0;
            }
            else if (_suspendReason == SignalLinkBreakReason.Jammed || _suspendReason == SignalLinkBreakReason.OutOfCoverage)
            {
                int secs = Mathf.CeilToInt(_graceLeft);
                key = HashCode.Combine(1, _watchLogicId, (int)_suspendReason, secs, (int)GameText.Language);
                if (key == _warningLineKey)
                {
                    return;
                }
                string k = _suspendReason == SignalLinkBreakReason.Jammed ? "signal.link.warn.jammed" : "signal.link.warn.out_of_coverage";
                line = GameText.Format(k, SignalPresence.MachineLabel(_watchLogicId), secs.ToString(CultureInfo.InvariantCulture));
            }
            else if (_edgeWarning)
            {
                int cells = Mathf.Max(0, Mathf.FloorToInt(_watchSample.MarginCells));
                key = HashCode.Combine(2, _watchLogicId, cells, (int)GameText.Language);
                if (key == _warningLineKey)
                {
                    return;
                }
                line = GameText.Format("signal.link.warn.edge", SignalPresence.MachineLabel(_watchLogicId), cells.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                key = 0;
            }
            if (key == _warningLineKey)
            {
                return;
            }
            _warningLineKey = key;
            _warningLine = line;
            WarningRevision++;
        }

        // ─────────────────────────────── 断链（FGR-SIG-040）───────────────────────────────

        /// <summary>
        /// 信号位置刚被 <see cref="SignalUplinkService.OnControlChanged"/> 改写之后调用：如果这次变更是一次断链（干扰 / 覆盖 / 静默夜 / 阵亡），
        /// 给出原因（HUD 状态行 + 音效 + 带位置的字幕）、让失去信号的机器进入安全模式、发出断链事件。
        /// </summary>
        public static void OnControlBreak(CampaignState s, string siteId, int prev, int cur, RegionControlChangeReason reason)
        {
            SignalLinkBreakReason r = FromControlReason(reason);
            if (r == SignalLinkBreakReason.None || s == null || prev <= 0)
            {
                return;
            }
            ClearWatch();
            BreakCount++;
            LastBreakReason = r;
            LastBreakLogicId = prev;
            LastReboundLogicId = r == SignalLinkBreakReason.MachineDestroyed ? cur : 0;
            LastBreakGameSeconds = GameClock.GameSeconds;
            string label = SignalPresence.MachineLabel(prev);
            bool safe = false;
            string text;
            switch (r)
            {
                case SignalLinkBreakReason.MachineDestroyed:
                    text = cur != 0
                        ? GameText.Format("signal.link.break.destroyed_rebound", label, SignalPresence.MachineLabel(cur))
                        : GameText.Format("signal.link.break.destroyed_core", label);
                    break;
                case SignalLinkBreakReason.Jammed:
                    text = GameText.Format("signal.link.break.jammed", label);
                    safe = true;
                    break;
                case SignalLinkBreakReason.OutOfCoverage:
                    text = GameText.Format("signal.link.break.out_of_coverage", label);
                    safe = true;
                    break;
                default:
                    text = GameText.Format("signal.link.break.silent_night", label);
                    safe = true;
                    break;
            }
            if (safe)
            {
                EnterSafeMode(s, prev, r);
            }
            SignalUplinkService.PushFeedback(text);
            Vector2? at = MachineRegistry.TryGetLivePosition(prev, out Vector2 p) ? p
                : MachineRegistry.TryGetRecord(prev, out MachineRecord rec) && rec != null ? rec.WorldPosition : (Vector2?)null;
            if (at.HasValue)
            {
                FeedbackCues.RaiseAt(FeedbackCueId.SignalLost, at.Value, text);
            }
            else
            {
                FeedbackCues.Raise(FeedbackCueId.SignalLost, text);
            }
            Log.Info($"[SignalLinkService] 断链：{r}，机器 {prev}{(cur != 0 ? "，回弹到 " + cur : "，信号回到归还核心")}（{siteId}）。");
            GameEvent.Send(LinkBrokenEvent, new SignalLinkBreak
            {
                LogicId = prev,
                Reason = r,
                ReboundLogicId = r == SignalLinkBreakReason.MachineDestroyed ? cur : 0,
                SiteId = siteId ?? string.Empty,
                EnteredSafeMode = safe,
            });
        }

        public static SignalLinkBreakReason FromControlReason(RegionControlChangeReason reason) => reason switch
        {
            RegionControlChangeReason.SignalLost => SignalLinkBreakReason.Jammed,
            RegionControlChangeReason.CoverageLost => SignalLinkBreakReason.OutOfCoverage,
            RegionControlChangeReason.SilentNight => SignalLinkBreakReason.SilentNight,
            RegionControlChangeReason.DeathRebound => SignalLinkBreakReason.MachineDestroyed,
            _ => SignalLinkBreakReason.None,
        };

        // ─────────────────────────────── 静默夜（预留接口，FG07 接入）───────────────────────────────

        /// <summary>FG07 静默夜预警（开始前 N 秒）：HUD 与字幕提示“届时信号会被弹回归还核心”。信号在核心时也提示（玩家可能正准备接入）。</summary>
        public static void AnnounceSilentNight(float secondsUntilStart)
        {
            string text = GameText.Format("signal.link.warn.silent_night", Mathf.CeilToInt(Mathf.Max(0f, secondsUntilStart)).ToString(CultureInfo.InvariantCulture));
            SignalUplinkService.PushFeedback(text);
            WarningCueCount++;
            FeedbackCues.Raise(FeedbackCueId.SignalLinkWarning, text);
            GameEvent.Send(SilentNightWarningEvent, secondsUntilStart);
        }

        /// <summary>
        /// FG07 静默夜开始：信号在机器里就立即弹回归还核心（机器进入安全模式），正在进行的接入过渡取消。
        /// 之后的判定走 <see cref="SignalUplinkService.SilentNightProvider"/>（FG07 负责让它在静默夜期间返回 true）。返回是否真的断了链。
        /// </summary>
        public static bool OnSilentNightStarted()
        {
            SignalUplinkService.CancelPendingForLink(GameText.Get("signal.uplink.reason.silent_night"));
            CampaignState s = CampaignSession.Current;
            int id = SignalUplinkService.CurrentMachine(s);
            if (id == 0)
            {
                return false;
            }
            RegionControlSystem control = RegionControlSystem.ForRegion(s.SignalCore.UplinkSiteId);
            return control != null && control.ForceBreak(RegionControlChangeReason.SilentNight);
        }

        // ─────────────────────────────── 安全模式（FGR-SIG-041）───────────────────────────────

        private static void EnterSafeMode(CampaignState s, int logicId, SignalLinkBreakReason reason)
        {
            EnsureState(s);
            SignalSafeModeRecord r = Find(s, logicId);
            if (r == null)
            {
                var list = new List<SignalSafeModeRecord>(s.SignalCore.SafeModes)
                {
                    new SignalSafeModeRecord { LogicId = logicId },
                };
                list.Sort((a, b) => a.LogicId.CompareTo(b.LogicId)); // 按编号排序：存档与指纹不依赖进入顺序。
                s.SignalCore.SafeModes = list.ToArray();
                r = Find(s, logicId);
            }
            r.Reason = (int)reason;
            r.SinceTick = GameClock.Ticks; // 整数步（DEBT-FG1SIG07-05）
            r.ClearSinceTick = -1;
            SafeModeEnterCount++;
            SafeModeRevision++;
            GameLogic.Core.GuidanceHooks.Raise(GameLogic.Core.GuidanceHooks.SafeModeFirstEnter); // FG1-HUD-01：第一次进入安全模式（图鉴“安全模式”随之解锁）。
            GameEvent.Send(SafeModeChangedEvent, new SignalSafeModeChange { LogicId = logicId, InSafeMode = true, Reason = reason });
        }

        /// <summary>信号接入了这台机器：它不再处于安全模式（<see cref="SignalUplinkService"/> 的 SetUplink 调）。</summary>
        public static void OnUplinked(CampaignState s, int logicId)
        {
            if (Find(s, logicId) != null)
            {
                Remove(s, logicId, feedback: false);
            }
        }

        private static void Remove(CampaignState s, int logicId, bool feedback)
        {
            SignalSafeModeRecord[] arr = s.SignalCore.SafeModes;
            int keep = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null && arr[i].LogicId != logicId)
                {
                    keep++;
                }
            }
            var next = new SignalSafeModeRecord[keep];
            int k = 0;
            SignalLinkBreakReason reason = SignalLinkBreakReason.None;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] == null)
                {
                    continue;
                }
                if (arr[i].LogicId == logicId)
                {
                    reason = (SignalLinkBreakReason)arr[i].Reason;
                    continue;
                }
                next[k++] = arr[i];
            }
            s.SignalCore.SafeModes = next;
            SafeModeExitCount++;
            SafeModeRevision++;
            if (feedback)
            {
                string text = GameText.Format("signal.safe_mode.exit", SignalPresence.MachineLabel(logicId));
                SignalUplinkService.PushFeedback(text);
                if (MachineRegistry.TryGetLivePosition(logicId, out Vector2 p))
                {
                    FeedbackCues.RaiseAt(FeedbackCueId.SignalRestored, p, text);
                }
                else
                {
                    FeedbackCues.Raise(FeedbackCueId.SignalRestored, text);
                }
            }
            GameEvent.Send(SafeModeChangedEvent, new SignalSafeModeChange { LogicId = logicId, InSafeMode = false, Reason = reason });
        }

        /// <summary>
        /// 每个模拟步（整个世界，与观察无关）：每 signal.safe_mode.check_seconds 游戏秒检查一次安全模式里的机器——
        /// 阵亡 / 消失 → 移除；断链条件（静默夜、干扰场、覆盖外，任何一条）都已消失并持续 signal.safe_mode.exit_seconds → 退出安全模式。
        /// 开销 O(安全模式机器数)，只在检查的那一步。
        /// </summary>
        public static void SimStep(CampaignState s)
        {
            SignalSafeModeRecord[] arr = s?.SignalCore?.SafeModes;
            if (arr == null || arr.Length == 0)
            {
                return;
            }
            long every = Math.Max(1L, (long)Math.Round(SafeModeCheckSeconds * GameClock.StepHz));
            if (GameClock.Ticks % every != 0)
            {
                return;
            }
            SafeModeChecks++;
            long now = GameClock.Ticks;
            long exitAfter = GameClock.TicksFor(SafeModeExitSeconds);
            bool night = IsSilentNight;
            int uplinked = SignalUplinkService.CurrentMachine(s);
            List<int> toRemove = null;
            List<int> toExit = null;
            for (int i = 0; i < arr.Length; i++)
            {
                SignalSafeModeRecord r = arr[i];
                if (r == null)
                {
                    continue;
                }
                if (!MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec) || rec == null || !rec.IsAlive || r.LogicId == uplinked)
                {
                    (toRemove ??= new List<int>(2)).Add(r.LogicId);
                    continue;
                }
                if (night || CauseAt(s, rec) != SignalLinkBreakReason.None)
                {
                    r.ClearSinceTick = -1;
                    continue;
                }
                if (r.ClearSinceTick < 0)
                {
                    r.ClearSinceTick = now;
                    continue;
                }
                if (now - r.ClearSinceTick >= exitAfter)
                {
                    (toExit ??= new List<int>(2)).Add(r.LogicId);
                }
            }
            if (toRemove != null)
            {
                foreach (int id in toRemove)
                {
                    Remove(s, id, feedback: false);
                }
            }
            if (toExit != null)
            {
                foreach (int id in toExit)
                {
                    Remove(s, id, feedback: true);
                }
            }
        }

        /// <summary>这台机器此刻有没有断链条件（干扰场 / 覆盖外；静默夜由调用方判）。位置取实时位置（任何已载入地点），与是否被观察无关。</summary>
        public static SignalLinkBreakReason CauseAt(CampaignState s, MachineRecord rec)
        {
            Vector2 pos = MachineRegistry.TryGetLivePosition(rec.LogicId, out Vector2 live) ? live : rec.WorldPosition;
            if (IsJammedAt(s, rec.RegionId, pos))
            {
                return SignalLinkBreakReason.Jammed;
            }
            if (!SignalCoverageService.Sample(rec.RegionId, pos, rec.LogicId).Covered)
            {
                return SignalLinkBreakReason.OutOfCoverage;
            }
            return SignalLinkBreakReason.None;
        }

        /// <summary>干扰场判定（与破碎都市接管上下文同一个函数；其余地点没有干扰机制）。不依赖地点控制器，未被观察时同样成立。</summary>
        public static bool IsJammedAt(CampaignState s, string regionId, Vector2 position) =>
            regionId == FracturedCityLayout.RegionId && s != null && FracturedCityRegion.IsPositionJammed(s, position);

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static SignalSafeModeRecord Find(CampaignState s, int logicId)
        {
            SignalSafeModeRecord[] arr = s?.SignalCore?.SafeModes;
            if (arr == null || logicId <= 0)
            {
                return null;
            }
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null && arr[i].LogicId == logicId)
                {
                    return arr[i];
                }
            }
            return null;
        }

        private static bool ShouldCue(int logicId, int kind)
        {
            float now = SignalUplinkService.RealNow;
            return !(logicId == LastCueLogicId[kind] && now - LastCueAt[kind] < WarnRepeatSeconds);
        }

        private static void MarkCue(int logicId, int kind)
        {
            LastCueAt[kind] = SignalUplinkService.RealNow;
            LastCueLogicId[kind] = logicId;
        }

        private static void RaiseWarningCue(int logicId, Vector2 position, string text)
        {
            if (!ShouldCue(logicId, CueEdge))
            {
                return;
            }
            MarkCue(logicId, CueEdge);
            WarningCueCount++;
            FeedbackCues.RaiseAt(FeedbackCueId.SignalLinkWarning, position, text);
        }

        private static string CellsText(float margin) => Mathf.Max(0, Mathf.FloorToInt(margin)).ToString(CultureInfo.InvariantCulture);

        private static void EnsureState(CampaignState s)
        {
            if (s.SignalCore == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            s.SignalCore.SafeModes ??= Array.Empty<SignalSafeModeRecord>();
        }

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v) && v > 0f)
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[SignalLinkService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>自检之间复位（不动存档数据）。</summary>
        public static void ResetForTests()
        {
            BreakCount = 0;
            LastBreakReason = SignalLinkBreakReason.None;
            LastBreakLogicId = 0;
            LastReboundLogicId = 0;
            LastBreakGameSeconds = -1;
            SafeModeEnterCount = 0;
            SafeModeExitCount = 0;
            WarningCueCount = 0;
            SafeModeChecks = 0;
            _watchLogicId = 0;
            _edgeWarning = false;
            _suspendReason = SignalLinkBreakReason.None;
            _graceLeft = 0f;
            _watchSample = default;
            for (int i = 0; i < LastCueAt.Length; i++)
            {
                LastCueAt[i] = float.NegativeInfinity;
                LastCueLogicId[i] = 0;
            }
            _warningLine = string.Empty;
            _warningLineKey = 0;
            SafeModeRevision++;
            WarningRevision++;
        }
    }
}
