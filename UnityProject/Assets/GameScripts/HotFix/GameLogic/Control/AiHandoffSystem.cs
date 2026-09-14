using System;
using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Command;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Control
{
    /// <summary>交还瞬间给单位安排的延续方式（M2-04a）。</summary>
    public enum HandoffContinuation : byte
    {
        /// <summary>没有延续（单位已死 / 不是从玩家手里放开的 / 非 AI 归属）。</summary>
        None = 0,
        /// <summary>接管前就带着 RTS 命令，命令在内核里原样复活，本系统不干预。</summary>
        ResumeCommand = 1,
        /// <summary>
        /// 原地守住。2026-09-14 起这是**唯一**会被产出的延续方式：松手就停在松手的地方。
        /// </summary>
        HoldGround = 2,

        /// <summary>
        /// 已退役（2026-09-14）：沿退出瞬间的速度方向再走一段。理由见 <see cref="AiHandoffSystem.ArmBuffer"/>。
        /// 枚举值保留不复用，避免与历史存档/日志里的数字对不上。
        /// </summary>
        Advance = 3,

        /// <summary>已退役（2026-09-14）：背离最近威胁再走一段。同 <see cref="Advance"/>。</summary>
        Disengage = 4,
    }

    /// <summary>调试显示用的单位 AI 快照（M2-04a 实施第 5 条）。只读，随查随算。</summary>
    public struct UnitAiDebugInfo
    {
        public bool Valid;
        public SimEntityId Unit;
        /// <summary>单位级**行为原型**。注意它不是 GDD §8.3 的编队教义，见契约 §6。</summary>
        public BehaviorKind Behavior;
        public IntentSource IntentSource;
        public UnitCommandKind Command;
        public SimEntityId CommandTarget;
        public float2 CommandTargetPosition;
        public bool HasCommandTargetPosition;
        public float2 Position;
        public bool InHandoffBuffer;
        public float BufferRemaining;
        public HandoffContinuation Continuation;
        /// <summary>所属编组槽位的位掩码（bit1..bit9 对应编组 1..9）。</summary>
        public int GroupMask;
    }

    /// <summary>
    /// 接管交还的可靠性（M2-04a：里程碑 M2-04 实施第 1、2、4、5 条）。
    ///
    /// GDD §7.3 定的产品语义是「玩家可随时退出，身体立即恢复 AI；离开后单位仍能可靠完成明确命令」。
    /// 拆开是三件独立的事，本类只做后两件里热更层能做的部分：
    ///
    /// 1. <b>命令与编队的延续本来就成立</b>，本系统不重造它，只记录 + 供断言：
    ///    编队归属存在 <see cref="SquadCommandSystem"/> 的热更层字典里（键是稳定 <see cref="SimEntityId"/>），
    ///    内核根本不认识"编队"，接管与退出都碰不到它；
    ///    RTS 命令在直控期间原样留在内核的 <c>_unitCommands</c> 里，只是 <c>JobCommandIntent</c>
    ///    不处理 <c>IntentSource != Commanded</c> 的槽位所以被"冻结"，交还时
    ///    <c>SwitchControlledUnitInternal</c> 把 <c>IntentSource</c> 恢复成 <c>Commanded</c>，命令自动复活。
    /// 2. <b>没有命令的单位才是真缺口</b>：交还后直接落回 <c>JobAIIntent</c>，按行为原型从零决策，
    ///    不记得"刚才在打谁 / 正朝哪走"。本系统在交还那一刻给它一条**短时守备命令**当缓冲，
    ///    见 <see cref="HandoffBufferSeconds"/>。
    /// 3. <b>安全位置</b>：全仓没有寻路，"不可达"的真实形态是位置落在场地外 / 障碍体内 / 变成 NaN，
    ///    见 <see cref="EnforceControlledSafePosition"/>。
    ///
    /// <b>每帧代价与场上单位数无关</b>（仓规架构红线第 4 条）：
    /// - 缓冲计时是"记录时刻 + 惰性判断"，稳态下每条记录只做一次 float 比较，记录数被
    ///   <see cref="MaxPendingHandoffs"/> 卡在 8 以内；
    /// - 安全位置检查只对**当前受控的那一个**单位做，代价 O(障碍数)，障碍被
    ///   <see cref="SimConst.MaxObstacles"/> 卡在 32 个以内，是地图静态属性，与敌人规模无关。
    ///
    /// <b>不是 GameModule</b>：与 <see cref="SquadCommandSystem"/> / <c>CameraDirector</c> /
    /// <see cref="DirectControlActions"/> 同理——控制权在暂停下也可能变（死亡回弹、读档恢复），
    /// 而 <c>_hub</c> 会被暂停早退整个冻住。
    ///
    /// 本段**不碰过载债**（实施第 3 条「AI 禁止高风险过载」拆到 M2-04b），
    /// 也**不改内核**：`Main/Sim/` 一行未动。
    /// </summary>
    public sealed class AiHandoffSystem
    {
        // ── 常量与理由 ──────────────────────────────────────

        /// <summary>
        /// 接管缓冲窗口（秒）。玩家松手之后这段时间里单位执行的是"延续指令"，不做 AI 决策。
        ///
        /// 取 1.5s 的理由：它必须比<b>一次控制权切换冷却</b>（<c>SimBridge.DefaultControlSwitchCooldown</c>
        /// = 0.75s，这里取它的 2 倍）长，否则"切过去看一眼再切回来"的来回操作会让缓冲每次都在半路被打断，
        /// 缓冲等于不存在；
        /// 又必须短到玩家不会把它误读成"这个单位不听 AI 了"——超过两秒的静默期在俯视战斗里
        /// 看起来就是卡住。
        ///
        /// 2026-09-14 之后这段窗口里执行的**一律是原地守备**（见 <see cref="ArmBuffer"/>），
        /// 所以它现在的作用只剩"缓冲到期前不交还给自由 AI"，不再是"走完一段延续指令"。
        /// </summary>
        public const float HandoffBufferSeconds = 1.5f;

        /// <summary>
        /// 判定"退出那一刻是不是在交战"的威胁感知半径。
        /// 与内核撤退命令的 <c>SimWorld.RetreatThreatRange</c> 同量级，
        /// 不复用那一个是因为它是**内核撤退行为**的参数，改它会顺带改掉所有撤退命令的手感。
        ///
        /// 2026-09-14 起它只用来填 <see cref="HandoffRecord.Threat"/> 供诊断，不再参与落点判定。
        /// </summary>
        public const float EngageThreatRange = 12f;

        /// <summary>缓冲守备命令的到达半径。比单位半径宽，避免在守备点上反复微调。</summary>
        public const float BufferArriveRadius = 1.5f;

        /// <summary>同时在缓冲期里的记录上限。超出时最旧的一条立刻收尾，不留悬挂的守备命令。</summary>
        private const int MaxPendingHandoffs = 8;

        /// <summary>位置有效性的容差。<c>JobIntegrate</c> 把贴边单位精确放在边界上，
        /// 不留这点余量会把"正常贴墙"误判成"卡在墙里"，于是每帧都在抢救。</summary>
        private const float SafeEpsilon = 0.01f;

        /// <summary>推出障碍的迭代次数。障碍互相重叠时一次推出可能落进另一个。</summary>
        private const int SafePushPasses = 4;

        private const int SafeFallbackDirections = 8;
        private const int SafeFallbackRings = 6;

        // ── 状态 ────────────────────────────────────────────

        /// <summary>被钉成永久原地守备的单位上限。见 <see cref="RememberParkedHold"/>。</summary>
        private const int MaxParkedHolds = 16;

        /// <summary>一条"我们钉的"原地守备。记命令本体是为了与玩家后来下的命令区分开。</summary>
        private struct ParkedHold
        {
            public SimEntityId Unit;
            public UnitCommand Command;
        }

        private readonly List<ParkedHold> _parkedHolds = new List<ParkedHold>(MaxParkedHolds);

        private struct HandoffRecord
        {
            public SimEntityId Unit;
            public float ExpireAt;
            public HandoffContinuation Continuation;
            public UnitCommand IssuedCommand;
            public bool GuardIssued;
            public int GroupMask;
            public SimEntityId Threat;
        }

        private SimBridge _sim;
        private SquadCommandSystem _squad;
        private BehaviorArchetype[] _archetypes = Array.Empty<BehaviorArchetype>();
        private SignalScope _scope;

        private readonly List<HandoffRecord> _records = new List<HandoffRecord>(MaxPendingHandoffs);
        private readonly SimEntityId[] _oneTarget = new SimEntityId[1];
        private float _clock;

        /// <summary>本局发生过多少次"交还给 AI"（含直接复活命令的那种）。验收用。</summary>
        public int HandoffCount { get; private set; }

        /// <summary>本局有多少次交还真的下了缓冲守备命令。验收用。</summary>
        public int BufferedHandoffCount { get; private set; }

        /// <summary>缓冲窗口正常走完并把单位交还 AI 的次数。验收用。</summary>
        public int BufferExpiredCount { get; private set; }

        /// <summary>安全位置兜底真的搬动过单位的次数。验收用。</summary>
        public int SafePositionRescueCount { get; private set; }

        /// <summary>最后一次交还的延续判定。验收与调试用。</summary>
        public HandoffContinuation LastContinuation { get; private set; }

        /// <summary>最后一次交还的单位。</summary>
        public SimEntityId LastHandoffUnit { get; private set; }

        /// <summary>当前处于接管缓冲期的单位数。</summary>
        public int PendingHandoffCount => _records.Count;

        /// <summary>调试叠加层的显示开关（实施第 5 条）。默认关。</summary>
        public bool DebugOverlayEnabled { get; set; }

        // ── 生命周期 ────────────────────────────────────────

        /// <summary>
        /// 绑定。<paramref name="squad"/> 可为 null（不显示编队归属，其余照常）；
        /// <paramref name="archetypes"/> 可为 null（行为原型显示落到 <see cref="BehaviorKind.Stationary"/>
        /// 的默认档）——两者都做成可选正是为了让这条路径能在 Edit 模式写真断言。
        /// </summary>
        public void Bind(SimBridge sim, SquadCommandSystem squad, BehaviorArchetype[] archetypes)
        {
            _sim = sim;
            _squad = squad;
            _archetypes = archetypes ?? Array.Empty<BehaviorArchetype>();
            _records.Clear();
            _clock = 0f;
            HandoffCount = 0;
            BufferedHandoffCount = 0;
            BufferExpiredCount = 0;
            HoldGroundHandoffCount = 0;
            _parkedHolds.Clear();
            SafePositionRescueCount = 0;
            LastContinuation = HandoffContinuation.None;
            LastHandoffUnit = SimEntityId.None;

            _scope?.Dispose();
            // M1-06 已经把"控制权变了"收敛成唯一一条出口。本系统只订阅它，
            // 不自己去猜受控实体换没换——第二个判断源头必然和那条出口漂移。
            _scope = new SignalScope()
                .On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
        }

        public void Unbind()
        {
            _scope?.Dispose();
            _scope = null;
            // 记录里的键是上一局那个 SimWorld 发的实体 id，跨局一律作废。
            _records.Clear();
            _sim = null;
            _squad = null;
            _archetypes = Array.Empty<BehaviorArchetype>();
        }

        /// <summary>
        /// 每帧驱动。稳态代价：一次 float 加法 + 每条缓冲记录一次 float 比较 + 受控单位一次位置校验。
        /// 全部与场上单位数无关。
        /// </summary>
        public void Tick(float dt, bool paused)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            if (!paused)
            {
                _clock += math.max(0f, dt);
                // 暂停时单位不动，位置也就不会变坏，没必要在冻结帧里反复校验。
                EnforceControlledSafePosition();
            }

            ExpireBuffers();
        }

        // ── 交还 ────────────────────────────────────────────

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            // 新受控单位若还在缓冲期：不要在这里清它的守备命令——此刻它的 IntentSource 已是 Player，
            // ClearCommand 会被内核拒绝（那条拒绝正是"绝不夺走玩家直控实体"的保护）。
            // 记录留着，倒计时在 ExpireBuffers 里被冻住，等它下次被放开时重新武装。
            HandlePreviousUnit(signal.PreviousUnitId);
        }

        private void HandlePreviousUnit(SimEntityId previous)
        {
            if (!previous.IsValid || _sim == null || !_sim.Running || _sim.World == null)
            {
                return;
            }

            if (!_sim.World.TryGetUnitControlState(previous, out SimUnitControlState state) || !state.IsAlive)
            {
                // 死亡回弹：旧躯体已经没了，没有"交还"这回事。
                DropRecord(previous, clearCommand: false);
                return;
            }

            HandoffCount++;
            LastHandoffUnit = previous;

            int recordIndex = FindRecord(previous);
            bool ourGuardStillOn = recordIndex >= 0 && _records[recordIndex].GuardIssued &&
                                   CommandMatches(previous, _records[recordIndex].IssuedCommand);

            // 2026-09-13：区分「玩家的编队命令」和「上一次交还我们自己钉下的原地守备」。
            // 两者在内核里长得一模一样（都是 Commanded + Guard），但含义相反：
            // 前者是玩家的明确意图，必须原样复活；后者只是上一次放手的落点，
            // 这一次玩家已经把它开到别处了，再"延续"回去等于把单位拉回旧守备点。
            // 不在接管那一刻清掉它，是因为此刻它的 IntentSource 已是 Player，
            // ClearCommand 会被内核拒绝（那条拒绝正是"绝不夺走玩家直控实体"的保护）。
            bool parkedByUs = TryGetParkedHold(previous, out UnitCommand parkedHold) &&
                              CommandMatches(previous, parkedHold);
            if (parkedByUs)
            {
                DropParkedHold(previous);
            }

            // 接管前带着真命令 → 内核已经把 IntentSource 恢复成 Commanded，命令原样复活。
            // 这一条**本来就成立**，本系统只登记不干预：在这里再下一条缓冲命令会把玩家的编队命令覆盖掉，
            // 那正好是 GDD §7.3「离开后单位仍能可靠完成明确命令」的反面。
            if (state.IntentSource == IntentSource.Commanded && !ourGuardStillOn && !parkedByUs)
            {
                DropRecord(previous, clearCommand: false);
                LastContinuation = HandoffContinuation.ResumeCommand;
                return;
            }

            // Scripted 单位是战役编排定死的行为，不归 AI 管，也就没有"交还给 AI"这件事。
            if (state.IntentSource == IntentSource.Scripted)
            {
                LastContinuation = HandoffContinuation.None;
                return;
            }

            ArmBuffer(previous, state, recordIndex);
        }

        /// <summary>
        /// 给一个"交还后会落回裸 AI"的单位安排缓冲。
        ///
        /// 缓冲的形态是一条**守备命令**而不是新造一套 AI 记忆：守备的语义天然就是
        /// "待在这别乱跑、不主动追远处目标"，正好等于任务要求的"保守、不主动开新战线"；
        /// 它走的是 M2-02 已经验收过的 <c>JobCommandIntent</c> 路径，热更层只在下令那一刻付一次代价。
        ///
        /// 守备是持久命令（内核不会让它自己完成），所以**到期必须由本系统撤销**，见 <see cref="ExpireBuffers"/>。
        /// 这是刻意的：缓冲的结束时机是产品决策，不该藏在内核的完成判据里。
        /// </summary>
        private void ArmBuffer(SimEntityId unit, in SimUnitControlState state, int recordIndex)
        {
            float2 pos = state.Position;
            float radius = 0.5f;
            float2 velocity = float2.zero;
            if (_sim.TryResolveUnitIndex(unit, out int index) && index < _sim.Snapshot.Count)
            {
                velocity = _sim.Snapshot.Velocity[index];
                radius = _sim.Snapshot.Radius[index];
            }

            // ── 2026-09-14 产品决策反转：**松手就停在松手的地方，一条分支，没有例外** ──
            //
            // 在此之前这里按退出瞬间的速度分三档：无威胁且在移动 → 沿原朝向再走
            // 沿原朝向再走 6 米；有威胁且正在背离 → 继续拉开同样距离；
            // 其余原地守住。立论是 M2-04 的"退出后延续合理意图，而不是站桩"。
            //
            // 玩家连着两轮报同一件事（#6「放下的身体应原地不动」、09-14「上一个角色老是会位移一段」），
            // 根因是这个立论有一处站不住：**那段"意图"根本不是这具身体的意图**。
            // 直控期间它是被提线操着的，速度来自玩家最后一次按键，不是任何正在进行的行军。
            // 拿残速当"未竟的意图"续上 6 米，等于凭一个从不存在的计划推翻玩家选定的落点——
            // 玩家把它开到这儿松手，这儿就是他要它待的地方。
            //
            // 09-13 已经为同一个理由把"缓冲到期回自由 AI"翻成了原地守备
            // （<see cref="HoldGroundAfterHandoff"/>），这两条分支是那次漏下的最后一块。
            //
            // 撤退档一并去掉：留着它，同一个抱怨在交战时照样能复现，而且它与
            // "交战中退出 → 守在原地继续打"这条**已经上线且没人反对**的规则自相矛盾——
            // 既然站在敌人旁边不许跑，那背对敌人时也没有理由替玩家多跑 6 米。
            //
            // 保留威胁探测：它只用来填 <see cref="HandoffRecord.Threat"/> 供诊断，不再参与判定。
            // 守备只产出移动意图、**不接管战斗**，所以"原地不动"从来不等于"不还手"。
            SimEntityId threat = TryFindNearestThreat(pos, out SimEntityId hostile, out _)
                ? hostile
                : SimEntityId.None;
            HandoffContinuation continuation = HandoffContinuation.HoldGround;
            float2 anchor = pos;

            if (TryFindSafePosition(anchor, radius, out float2 safeAnchor))
            {
                anchor = safeAnchor;
            }

            var command = new UnitCommand
            {
                Kind = UnitCommandKind.Guard,
                TargetPosition = anchor,
                TargetEntity = SimEntityId.None,
                ArriveRadius = BufferArriveRadius,
            };

            _oneTarget[0] = unit;
            bool issued = _sim.IssueCommand(_oneTarget, command) > 0;

            var record = new HandoffRecord
            {
                Unit = unit,
                ExpireAt = _clock + HandoffBufferSeconds,
                Continuation = continuation,
                IssuedCommand = command,
                GuardIssued = issued,
                GroupMask = CaptureGroupMask(unit),
                Threat = threat,
            };

            if (recordIndex >= 0)
            {
                _records[recordIndex] = record;
            }
            else
            {
                if (_records.Count >= MaxPendingHandoffs)
                {
                    // 最旧的一条立刻收尾。悬挂的守备命令比"缓冲少一条"危险得多——
                    // 守备不会自己完成，漏掉它等于把那个单位永久钉在地上。
                    FinishRecord(0);
                }
                _records.Add(record);
            }

            if (issued)
            {
                BufferedHandoffCount++;
            }
            LastContinuation = continuation;
        }

        /// <summary>
        /// 到期收尾。稳态下每条记录只做一次 <c>_clock</c> 比较，解析实体这类 O(单位数) 的活
        /// 每条记录**一生只付一次**。
        /// </summary>
        private void ExpireBuffers()
        {
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                HandoffRecord record = _records[i];

                // 玩家又把它接管回去了：缓冲不该在玩家手里跑完，冻住倒计时等下次放开时重新武装。
                if (_sim.ControlledUnitId == record.Unit)
                {
                    record.ExpireAt = _clock + HandoffBufferSeconds;
                    _records[i] = record;
                    continue;
                }

                if (_clock < record.ExpireAt)
                {
                    continue;
                }

                FinishRecord(i);
            }
        }

        /// <summary>
        /// 2026-09-13 试玩反馈（产品决策，可推翻）：**缓冲结束后不再放回自由 AI，改为永久原地守备**。
        ///
        /// 玩家的原话是「切换角色或者战术视角，先暂时用简单的 AI 逻辑（原地不动但是持续攻击）」。
        /// 原先缓冲一过就 <c>ClearCommand</c> → 落回 <c>JobAIIntent</c>，单位按行为原型自己去追人，
        /// 于是「我刚放下的那具身体跑哪去了」成了试玩里最常见的失控感来源。
        ///
        /// 守备命令**不接管战斗**——<c>SimWorld.ResolveMinionCombat</c> 对 Commanded 单位照常结算攻击
        /// （M2-02 已把那里的判据从「是 AI」改成「不是玩家直控」），所以"原地不动"不等于"不还手"。
        /// 副作用是它保持 <c>IntentSource.Commanded</c>，因此**继续留在 RTS 选择集里、可被重新下令**，
        /// 这正好也是玩家报的另一条问题（放下的身体在战术视角选不中）想要的结果。
        ///
        /// 保留 1.5s 缓冲的延续语义不变（走完 Advance/Disengage 那一段再停），只改"之后去哪"。
        /// </summary>
        public const bool HoldGroundAfterHandoff = true;

        /// <summary>
        /// 结束一条缓冲。<see cref="HoldGroundAfterHandoff"/> 为真时把它换成一条**原地**守备，
        /// 否则撤掉命令、交还自由 AI（原 M2-04a 行为）。
        ///
        /// **只动自己下的那一条**：缓冲期间玩家完全可能给它下了新的编队命令，
        /// 把那条一起覆盖就是"RTS 命令莫名其妙被吞"，而且只在接管刚结束的那 1.5 秒里复现。
        /// </summary>
        private void FinishRecord(int index)
        {
            HandoffRecord record = _records[index];
            _records.RemoveAt(index);

            if (!record.GuardIssued || _sim == null || !_sim.Running)
            {
                return;
            }

            if (!CommandMatches(record.Unit, record.IssuedCommand))
            {
                return;
            }

            if (!HoldGroundAfterHandoff)
            {
                if (_sim.ClearCommand(record.Unit))
                {
                    BufferExpiredCount++;
                }
                return;
            }

            // 锚点取**此刻**的位置而不是缓冲开始时那个：Advance/Disengage 的延续正是要它往前走一段，
            // 用旧锚点会让它走完之后再倒回去，看起来像被拉了一把。
            if (_sim.World == null ||
                !_sim.World.TryGetUnitControlState(record.Unit, out SimUnitControlState state) || !state.IsAlive)
            {
                return;
            }

            var hold = new UnitCommand
            {
                Kind = UnitCommandKind.Guard,
                TargetPosition = state.Position,
                TargetEntity = SimEntityId.None,
                ArriveRadius = BufferArriveRadius,
            };
            _oneTarget[0] = record.Unit;
            if (_sim.IssueCommand(_oneTarget, hold) > 0)
            {
                BufferExpiredCount++;
                HoldGroundHandoffCount++;
                RememberParkedHold(record.Unit, hold);
            }
        }

        /// <summary>缓冲结束后被钉成永久原地守备的次数。验收用。</summary>
        public int HoldGroundHandoffCount { get; private set; }

        /// <summary>当前被钉成原地守备的单位数。验收用。</summary>
        public int ParkedHoldCount => _parkedHolds.Count;

        /// <summary>
        /// 记住"这条 Guard 是我们钉的"。上限与 <see cref="MaxPendingHandoffs"/> 同量级：
        /// 同时被放下的身体不可能多——溢出时丢最早的一条，代价只是那一具下次交还被当成
        /// 「延续玩家命令」（行为仍是原地守备，不会乱跑），是安全方向的失效。
        /// </summary>
        private void RememberParkedHold(SimEntityId unit, in UnitCommand hold)
        {
            for (int i = 0; i < _parkedHolds.Count; i++)
            {
                if (_parkedHolds[i].Unit == unit)
                {
                    _parkedHolds[i] = new ParkedHold { Unit = unit, Command = hold };
                    return;
                }
            }
            if (_parkedHolds.Count >= MaxParkedHolds)
            {
                _parkedHolds.RemoveAt(0);
            }
            _parkedHolds.Add(new ParkedHold { Unit = unit, Command = hold });
        }

        private bool TryGetParkedHold(SimEntityId unit, out UnitCommand hold)
        {
            for (int i = 0; i < _parkedHolds.Count; i++)
            {
                if (_parkedHolds[i].Unit == unit)
                {
                    hold = _parkedHolds[i].Command;
                    return true;
                }
            }
            hold = default;
            return false;
        }

        private void DropParkedHold(SimEntityId unit)
        {
            for (int i = 0; i < _parkedHolds.Count; i++)
            {
                if (_parkedHolds[i].Unit == unit)
                {
                    _parkedHolds.RemoveAt(i);
                    return;
                }
            }
        }

        private bool CommandMatches(SimEntityId unit, in UnitCommand expected)
        {
            return _sim != null && _sim.TryGetCommand(unit, out UnitCommand actual) &&
                   actual.Kind == expected.Kind &&
                   actual.TargetEntity == expected.TargetEntity &&
                   math.all(actual.TargetPosition == expected.TargetPosition) &&
                   math.abs(actual.ArriveRadius - expected.ArriveRadius) < 1e-4f;
        }

        private int FindRecord(SimEntityId unit)
        {
            for (int i = 0; i < _records.Count; i++)
            {
                if (_records[i].Unit == unit) { return i; }
            }
            return -1;
        }

        private void DropRecord(SimEntityId unit, bool clearCommand)
        {
            int index = FindRecord(unit);
            if (index < 0)
            {
                return;
            }

            if (clearCommand)
            {
                FinishRecord(index);
            }
            else
            {
                _records.RemoveAt(index);
            }
        }

        /// <summary>
        /// 交还那一刻最近的敌对单位。逐单位筛选发生在内核（AOT）里，且只在交还那一刻跑一次，
        /// 与 <c>SquadCommandSystem.TryPickHostile</c> 同一约定。
        ///
        /// 已知边界：<c>QueryUnitsInRect</c> 的返回条目被 <see cref="SimConst.MaxSelectionSize"/> 截断，
        /// 极密集的虫群里可能漏掉真正最近的那一个。这是"交战中退出"的启发式判据，
        /// 漏判的后果只是把 Disengage 判成 HoldGround，不会产生错误命令。
        /// </summary>
        private bool TryFindNearestThreat(float2 pos, out SimEntityId hostile, out float2 hostilePos)
        {
            hostile = SimEntityId.None;
            hostilePos = float2.zero;

            var half = new float2(EngageThreatRange, EngageThreatRange);
            SimUnitPick[] picks = _sim.QueryUnitsInRect(pos - half, pos + half, commandableOnly: false);
            float bestSq = EngageThreatRange * EngageThreatRange;
            for (int i = 0; i < picks.Length; i++)
            {
                if (picks[i].Faction != SimFaction.Hostile)
                {
                    continue;
                }
                float d = math.distancesq(picks[i].Position, pos);
                if (d < bestSq)
                {
                    bestSq = d;
                    hostile = picks[i].EntityId;
                    hostilePos = picks[i].Position;
                }
            }

            return hostile.IsValid;
        }

        private int CaptureGroupMask(SimEntityId unit)
        {
            if (_squad == null)
            {
                return 0;
            }

            int mask = 0;
            for (int slot = 1; slot <= 9; slot++)
            {
                IReadOnlyList<SimEntityId> members = _squad.GroupMembers(slot);
                for (int i = 0; i < members.Count; i++)
                {
                    if (members[i] == unit)
                    {
                        mask |= 1 << slot;
                        break;
                    }
                }
            }
            return mask;
        }

        // ── 安全位置（实施第 4 条）────────────────────────────

        /// <summary>
        /// 把受控单位从无效位置拉回最近的有效位置。
        ///
        /// <b>为什么守在"接管期间每帧"而不是"交还那一刻"：</b>
        /// 玩家只能把**自己正在直控的那一个**单位精确开进坏位置；被命令 / 被 AI 驱动的单位每帧由
        /// <c>JobIntegrate</c> 推出障碍、夹进场地，玩家碰不到它们的落点。守住受控单位这一个槽位，
        /// "交还时单位处在有效位置"就是**构造上成立**的，而不是靠交还那一刻补救。
        /// 顺带还修掉了同源的另一半问题：玩家自己被卡住、动不了。
        ///
        /// 这也是本段**内核零改动**的前提：受控单位有现成的内核入口
        /// <c>SimBridge.SetControlledPosition</c>；换成"交还后再搬运"就必须给内核新开一个
        /// "按实体 id 挪动任意单位"的入口。
        ///
        /// 代价 O(障碍数 ≤ 32)，且绝大多数帧在第一条判据就返回——不触碰"每帧不得 O(单位数)"红线。
        /// <b>本段不建任何寻路</b>（M2-04 非目标，且 Built-in RP 无 NavMesh）。
        /// </summary>
        private void EnforceControlledSafePosition()
        {
            if (!_sim.TryGetControlledPresentation(out SimControlledUnitView view))
            {
                return;
            }

            if (IsPositionValid(view.Position, view.Radius))
            {
                return;
            }

            if (TryFindSafePosition(view.Position, view.Radius, out float2 safe) &&
                _sim.SetControlledPosition(safe))
            {
                SafePositionRescueCount++;
            }
        }

        /// <summary>位置是否有效：非 NaN/Inf、在场地内、不在任何障碍体内部。</summary>
        public bool IsPositionValid(float2 position, float radius)
        {
            if (!math.all(math.isfinite(position)))
            {
                return false;
            }

            float half = _sim != null ? _sim.ArenaHalfExtent : 0f;
            if (math.abs(position.x) > half + SafeEpsilon || math.abs(position.y) > half + SafeEpsilon)
            {
                return false;
            }

            ObstacleSpec[] obstacles = _sim != null ? _sim.Obstacles : null;
            if (obstacles == null)
            {
                return true;
            }

            for (int i = 0; i < obstacles.Length; i++)
            {
                float minDist = obstacles[i].Radius + radius;
                if (math.distancesq(position, obstacles[i].Position) < (minDist - SafeEpsilon) * (minDist - SafeEpsilon))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 求离 <paramref name="from"/> 最近的有效位置。
        /// 先按"夹进场地 + 逐个推出障碍"迭代几轮（与 <c>JobIntegrate</c> 同一套几何，
        /// 只是多跑几遍以处理互相重叠的障碍）；仍然不行才退到一圈一圈向外采样。
        /// 采样兜底只在抢救路径上跑，不在每帧路径上。
        /// </summary>
        public bool TryFindSafePosition(float2 from, float radius, out float2 safe)
        {
            float half = _sim != null ? _sim.ArenaHalfExtent : 0f;
            // NaN 进来时一切几何都没有意义，直接从场地中心重新起算。
            float2 start = math.all(math.isfinite(from)) ? from : float2.zero;

            float2 candidate = ClampToArena(start, half);
            for (int pass = 0; pass < SafePushPasses; pass++)
            {
                candidate = PushOutOfObstacles(candidate, radius);
                candidate = ClampToArena(candidate, half);
                if (IsPositionValid(candidate, radius))
                {
                    safe = candidate;
                    return true;
                }
            }

            float step = math.max(0.5f, radius * 2f);
            for (int ring = 1; ring <= SafeFallbackRings; ring++)
            {
                float distance = step * ring;
                for (int d = 0; d < SafeFallbackDirections; d++)
                {
                    float angle = d * (math.PI * 2f / SafeFallbackDirections);
                    float2 probe = ClampToArena(
                        start + new float2(math.cos(angle), math.sin(angle)) * distance, half);
                    if (IsPositionValid(probe, radius))
                    {
                        safe = probe;
                        return true;
                    }
                }
            }

            safe = start;
            return false;
        }

        private static float2 ClampToArena(float2 position, float half)
        {
            return new float2(math.clamp(position.x, -half, half), math.clamp(position.y, -half, half));
        }

        private float2 PushOutOfObstacles(float2 position, float radius)
        {
            ObstacleSpec[] obstacles = _sim != null ? _sim.Obstacles : null;
            if (obstacles == null)
            {
                return position;
            }

            for (int i = 0; i < obstacles.Length; i++)
            {
                float2 diff = position - obstacles[i].Position;
                float minDist = obstacles[i].Radius + radius;
                float distSq = math.lengthsq(diff);
                if (distSq >= minDist * minDist)
                {
                    continue;
                }
                float dist = math.sqrt(distSq);
                float2 normal = dist > 1e-4f ? diff / dist : new float2(1f, 0f);
                position = obstacles[i].Position + normal * (minDist + SafeEpsilon);
            }

            return position;
        }

        // ── 调试显示（实施第 5 条）───────────────────────────

        /// <summary>
        /// 单位当前的行为原型 / 意图来源 / 命令 / 目标 / 是否在接管缓冲期。
        ///
        /// <b>这里显示的是"单位级行为原型"，不是 GDD §8.3 的六种编队教义</b>（先锋 / 猎手 / 护送 /
        /// 潜行 / 回收 / 坚守）——那是编队级概念，属 M3 的 <c>PhenotypeTemplate</c> 层，
        /// 代码里今天根本不存在（全仓 "Doctrine" 零命中）。详见契约 §6。
        /// </summary>
        public UnitAiDebugInfo Describe(SimEntityId unit)
        {
            var info = new UnitAiDebugInfo { Unit = unit };
            if (_sim == null || !_sim.Running || !unit.IsValid ||
                !_sim.TryResolveUnitIndex(unit, out int index))
            {
                return info;
            }

            SimSnapshot snap = _sim.Snapshot;
            if (index >= snap.Count)
            {
                return info;
            }

            info.Valid = true;
            info.Position = snap.Position[index];
            info.IntentSource = (IntentSource)snap.IntentSource[index];

            int archetypeId = snap.ArchetypeId[index];
            info.Behavior = archetypeId >= 0 && archetypeId < _archetypes.Length
                ? _archetypes[archetypeId].Kind
                : BehaviorArchetype.Default.Kind;

            if (_sim.TryGetCommand(unit, out UnitCommand command))
            {
                info.Command = command.Kind;
                info.CommandTarget = command.TargetEntity;
                info.CommandTargetPosition = command.TargetPosition;
                info.HasCommandTargetPosition = command.Kind != UnitCommandKind.Attack;
            }

            int recordIndex = FindRecord(unit);
            if (recordIndex >= 0)
            {
                HandoffRecord record = _records[recordIndex];
                info.InHandoffBuffer = true;
                info.BufferRemaining = math.max(0f, record.ExpireAt - _clock);
                info.Continuation = record.Continuation;
                info.GroupMask = record.GroupMask;
                if (!info.CommandTarget.IsValid && record.Threat.IsValid)
                {
                    info.CommandTarget = record.Threat;
                }
            }
            else
            {
                info.GroupMask = CaptureGroupMask(unit);
            }

            return info;
        }

        /// <summary>缓冲期里第 <paramref name="i"/> 条记录的单位。调试叠加层按序遍历用。</summary>
        public SimEntityId PendingHandoffUnit(int i)
        {
            return i >= 0 && i < _records.Count ? _records[i].Unit : SimEntityId.None;
        }

        /// <summary>某个单位是否处在接管缓冲期。</summary>
        public bool IsInHandoffBuffer(SimEntityId unit) => FindRecord(unit) >= 0;

        /// <summary>某个单位的缓冲剩余秒数；不在缓冲期时为 0。</summary>
        public float BufferRemaining(SimEntityId unit)
        {
            int index = FindRecord(unit);
            return index >= 0 ? math.max(0f, _records[index].ExpireAt - _clock) : 0f;
        }

        /// <summary>验收入口：把本地时钟快进一段并立刻做一次到期收尾，不必真的跑满 1.5 秒的帧。</summary>
        public void DebugAdvanceClock(float seconds)
        {
            _clock += math.max(0f, seconds);
            ExpireBuffers();
        }
    }
}
