using System;
using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using Unity.Mathematics;

namespace GameLogic.Control
{
    /// <summary>
    /// 直控动作集的编译与释放（M2-03b）。
    ///
    /// 一句话职责：**"我现在按下的这个键，在我现在这具身体上意味着什么"**，
    /// 以及"它到底能不能打出来"。里程碑 M2-03 的立论是"没有额外英雄技能表"——
    /// 能按出来的东西必须从受控实体的实际装配（M2-03a 的 <see cref="UnitLoadoutRegistry"/>）读出来。
    ///
    /// ── 三条设计纪律 ──
    /// 1. <b>动作集事件驱动重建</b>：只订阅 M1-06 那条单一出口 <see cref="ControlledUnitChangedSignal"/>，
    ///    收到才重建。每帧查注册表在冷启动那帧是 O(单位数 × 查询数)（M2-03a 契约 §10 已点名），
    ///    而热更层每帧必须与场上单位数无关。
    /// 2. <b>失能在释放入口拦截</b>，不是只在 UI 上灰掉。只灰不拦的话按键照样生效，
    ///    等于"显示禁用、实际可用"——玩家会把它当成随机失效，那是最坏的一种 bug。
    /// 3. <b>玩家本体与友军走两条不同的释放路径，且这是刻意的</b>：见 <see cref="TryRelease"/>。
    /// </summary>
    public sealed class DirectControlActions
    {
        private SimBridge _sim;
        private UnitLoadoutRegistry _loadouts;
        private AbilitySystem _abilities;
        private StatusSystem _status;
        private SignalScope _scope;

        private readonly DirectActionSet _set = new DirectActionSet();

        /// <summary>
        /// M2-03c：代谢 / 过载债 / 按槽冷却的账本。**按实体归属**，跟着控制权走。
        /// 它属于本类而不是一个全局单例，理由见 <see cref="UnitVitalsRegistry"/> 类注释。
        /// </summary>
        private readonly UnitVitalsRegistry _vitals = new UnitVitalsRegistry();

        /// <summary>
        /// M2-04b：把过载态镜像到内核，让**交给 AI 的身体也吃过载惩罚**。
        ///
        /// 它挂在本类而不是 <c>CellStageFlow</c> 上，理由只有一个：
        /// 账本 <see cref="_vitals"/> 就在这里，而"谁有资格把过载态写进内核"必须只有一个答案。
        /// 放到外面就要多一条 Bind/Unbind 生命周期，而它和账本的生死必须严格一致
        /// ——账本 Reset 了、镜像还留着位，就是把单位永久钉死。
        /// </summary>
        private readonly OverloadSuppressionMirror _overloadMirror = new OverloadSuppressionMirror();

        /// <summary>
        /// M2-07：这具身体交给 AI 之后，用**同一套器官**开火的那条路。
        ///
        /// 放在这里而不是另起一个系统，是因为它需要的四样东西（内核桥、装配表、体征账本、状态系统）
        /// 恰好全在本类手上，而其中 <see cref="_vitals"/> **必须是同一个实例**——
        /// 冷却与过载债分成两本，就等于"被开"和"自己打"各攒各的，本段要合的分叉会原地长回来。
        /// </summary>
        private readonly MinionOrganCombatDriver _minionCombat = new MinionOrganCombatDriver();

        /// <summary>
        /// 玩家本体上 <see cref="LoadoutAction.Primary"/> / <see cref="LoadoutAction.Utility"/>
        /// 委托到的技能槽下标。
        ///
        /// **刻意落在 1..N 区间**：<c>CellPlayerController.PollAbilityInput</c> 里槽 1..N 本来就
        /// Ready 即自动施放，所以这两个键对玩家本体是**幂等委托**——按下去时槽多半正在冷却，
        /// 什么都不会多发生。这正是"玩家本体行为零改动"这条硬约束的落点：
        /// 直控键不给玩家本体引入第二条伤害来源。
        /// 槽 0 是冲刺（Space），不在这里，否则右键会变成闪避。
        /// </summary>
        private const int PlayerPrimaryAbilitySlot = 1;
        private const int PlayerUtilityAbilitySlot = 2;

        public DirectActionSet ActionSet => _set;

        /// <summary>
        /// 场上有没有可交互目标。
        ///
        /// **今天恒为 false，且没有任何生产代码会写它。** GDD 里的"交互"指拾取野生器官 / 临时移植，
        /// 那些对象在仓库里根本不存在。造一个假的交互物出来凑验收，等真正的拾取系统进来时
        /// 必然要连带拆掉，而在此之前它会让"E 键为什么没反应"变成一个查不清的问题。
        /// 所以这里只留一个接缝：槽位、键位、可用性判定都在，目标由后续里程碑写入。
        /// </summary>
        public bool InteractTargetsAvailable { get; set; }

        /// <summary>动作集重建次数。验收用。</summary>
        public int RebuildCount { get; private set; }

        /// <summary>成功释放次数。验收用。</summary>
        public int ReleaseCount { get; private set; }

        /// <summary>最近一次释放尝试的动作。</summary>
        public LoadoutAction LastAttemptedAction { get; private set; } = LoadoutAction.Move;

        /// <summary>最近一次**成功**释放所用的器官 id。null 表示还没有成功过。</summary>
        public string LastReleasedOrganId { get; private set; }

        /// <summary>最近一次释放尝试的结果。<see cref="DirectActionAvailability.Available"/> = 成功。</summary>
        public DirectActionAvailability LastReleaseResult { get; private set; } =
            DirectActionAvailability.NoControlledUnit;

        /// <summary>最近一次成功释放落到内核的请求形状。验收/调试读它确认"打出来的东西和器官对得上"。</summary>
        public OrganKernelAction LastReleasedKernelAction { get; private set; }

        /// <summary>M4-R00-02 队列①-1：最近一次内核路释放的发射上下文（诊断/回归锚点，见
        /// `EmissionContext` 类注释——尚未成为唯一入口）。玩家本体委托路走 <see cref="AbilitySystem"/>，
        /// 不产生它，保持默认值。</summary>
        public EmissionContext LastEmissionContext { get; private set; }

        /// <summary>内核路释放次数计数，喂给 <see cref="OrganKernelActionTable.ResolveCompiled"/> 当
        /// seed——只需要在同一局内单调递增、对同一次释放前后一致（预览/判定用同一个数），
        /// 不需要额外的随机源。</summary>
        private int _kernelReleaseSeed;

        /// <summary>三个量的账本（M2-03c）。UI 只读快照走 <see cref="ControlledVitals"/>。</summary>
        public UnitVitalsRegistry Vitals => _vitals;

        /// <summary>过载态到内核的镜像（M2-04b）。验收读它确认压制真的推下去了。</summary>
        public OverloadSuppressionMirror OverloadMirror => _overloadMirror;

        /// <summary>
        /// **当前受控实体**的代谢 / 过载债 / 冷却快照（M2-03c）。O(1)：一次受控视图解析 + 一次字典查。
        /// 没有受控实体时 <c>Valid = false</c>，UI 据此整块隐藏。
        /// </summary>
        public UnitVitalsView ControlledVitals
        {
            get
            {
                if (_sim == null || !_sim.Running ||
                    !_sim.TryGetControlledPresentation(out SimControlledUnitView view))
                {
                    return default;
                }

                return _vitals.Get(view.EntityId);
            }
        }

        /// <summary>
        /// 每帧推进三个量的本地时钟（M2-03c）。**与场上单位数无关**：代谢回复 / 冷却推进
        /// 全部在读写那一刻惰性补齐（见 <see cref="UnitVitalsRegistry"/> 类注释）。
        /// 唯一的例外是过载态巡守表（M2-04b），长度 ≤ <see cref="UnitVitalsRegistry.MaxOverloadWatch"/>，
        /// 没人过载时是空循环——它必须逐帧补齐，因为过载态被镜像进了内核，
        /// 没有读者也得按时解除，否则那具身体被无声地永久钉死。
        ///
        /// 暂停下不推进：暂停刷冷却是白送的，口径与 <c>_hub</c> 被暂停冻住一致。
        /// </summary>
        public void Tick(float dt, bool paused)
        {
            _vitals.Advance(dt, paused);
            // M2-07：AI 控制下的友军用自己的器官开火。必须在 Advance **之后**——
            // 冷却是惰性补齐的，先推时钟再判闸门，否则每一发都会拿上一帧的时钟去问"好了没"，
            // 稳定地慢半拍。开销 O(本帧开火机会数)，与敌人数无关。
            _minionCombat.Tick(paused);
        }

        /// <summary>M2-07：AI 侧器官开火的诊断入口（试玩面板与验收读）。</summary>
        public MinionOrganCombatDriver MinionCombat => _minionCombat;

        /// <summary>
        /// 绑定。<paramref name="abilities"/> / <paramref name="status"/> 可为 null：
        /// 前者缺席时玩家本体的委托释放直接判失败（不抛），后者缺席时范围状态退回
        /// <c>SimBridge.ApplyStatusArea</c>——Edit 模式回归起不了整套 <c>ModuleHub</c>，
        /// 不可注入就等于这条路径永远只能靠进 Play 手点验（同 M2-03a 的投影源注入理由）。
        /// </summary>
        public void Bind(SimBridge sim, UnitLoadoutRegistry loadouts,
            AbilitySystem abilities = null, StatusSystem status = null)
        {
            _sim = sim;
            _loadouts = loadouts;
            _abilities = abilities;
            _status = status;
            InteractTargetsAvailable = false;
            // 跨局必须清：条目里的键是上一局那个 SimWorld 发的实体 id。
            // 镜像先解绑（它会把上一副内核里压下去的过载位收回来），再清账本、再重新绑。
            // 顺序反了就会拿新账本的空表去给旧内核收尾，旧世界里那些位就此悬空。
            _overloadMirror.Unbind();
            _vitals.Reset();
            _overloadMirror.Bind(sim, _vitals);
            _minionCombat.Bind(sim, loadouts, _vitals, status);
            RebuildCount = 0;
            ReleaseCount = 0;
            LastReleasedOrganId = null;
            LastReleasedKernelAction = OrganKernelAction.None;
            LastReleaseResult = DirectActionAvailability.NoControlledUnit;
            LastEmissionContext = default;
            _kernelReleaseSeed = 0;

            _scope?.Dispose();
            // M1-06 把"控制权变了"收敛成了唯一一条出口。动作集只订阅它，不自己去猜
            // "受控实体是不是换了"——第二个判断源头必然和这条出口漂移。
            _scope = new SignalScope()
                .On<ControlledUnitChangedSignal>(OnControlledUnitChanged);

            Rebuild();
        }

        public void Unbind()
        {
            _scope?.Dispose();
            _scope = null;
            _sim = null;
            _loadouts = null;
            _abilities = null;
            _status = null;
            _set.Clear();
            // 镜像先于账本收尾：它要用账本里那份"谁还被压着"去放掉内核的位。
            _overloadMirror.Unbind();
            _minionCombat.Unbind();
            _vitals.Reset();
        }

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            Rebuild();
        }

        /// <summary>
        /// 重建动作集。除了控制权变更，装配延迟登记落地（<c>ResolvePending</c>）之后也该调一次——
        /// 接管发生在登记落地之前时，那一刻查到的还是空装配。
        /// </summary>
        public void Rebuild()
        {
            RebuildCount++;

            if (_sim == null || !_sim.Running ||
                !_sim.TryGetControlledPresentation(out SimControlledUnitView view))
            {
                // 意识无处可去时动作集必须当场空掉，而不是留着上一具身体的按钮。
                _set.Clear();
                return;
            }

            UnitLoadout loadout = _loadouts != null ? _loadouts.Get(view.EntityId) : UnitLoadout.Empty;
            _set.Rebuild(view.EntityId, loadout, InteractTargetsAvailable);
        }

        /// <summary>当前受控的是不是玩家本体。见 <see cref="SimBridge.ControllingPlayerBody"/>。</summary>
        public bool ControllingPlayerBody => _sim != null && _sim.ControllingPlayerBody;

        /// <summary>
        /// 释放一个动作。**这是唯一的释放入口**，失能拦截发生在这里。
        ///
        /// ── 为什么不信 <see cref="ActionSet"/> 那份缓存 ──
        /// 缓存只在控制权变更时重建，而装配随时会变（玩家选卡装上新器官、器官被打坏失能）。
        /// 只查缓存的话，"装配变了但还没切过控制权"的那段窗口里会按出一个已经不存在的动作。
        /// 所以这里**重新**读一次实时装配：代价是 O(器官数)，且只发生在按键那一刻，不是每帧。
        ///
        /// ── 玩家本体与友军为什么是两条路 ──
        /// 玩家本体的输出手段早就有一整套（<c>AbilitySystem</c> 槽位 + <c>MetabolicSliceRunner</c>
        /// 的 Carrier 自动开火），它们和手感、体力账本、卡牌触发全绑在一起。在这里另起一条
        /// 释放路径等于给玩家本体加了第二个伤害来源——回归风险最高的地方，任务书明令零改动。
        /// 所以玩家本体只做**委托**。
        /// 而非玩家友军身上根本没有 <c>AbilitySystem</c> 实例（它是玩家全局单例），
        /// 委托无从谈起，只能按那件器官的参数一次性向内核下请求（同 M2-02 的 <c>IssueCommand</c> 模式）。
        /// 统一这两条路是一次独立的大重构，本段碰它必然超时返工。
        /// </summary>
        /// <param name="aim">瞄准方向（世界 XZ）。零向量时退化为 +X。</param>
        /// <returns>是否真的释放了。失败原因见 <see cref="LastReleaseResult"/>。</returns>
        public bool TryRelease(LoadoutAction action, float2 aim)
        {
            LastAttemptedAction = action;

            if (action == LoadoutAction.Move)
            {
                // 移动是每帧意图，不是一次性动作（M2-03a 契约 §6：它不由器官提供）。
                return Reject(DirectActionAvailability.NotAReleaseAction);
            }

            if (_sim == null || !_sim.Running ||
                !_sim.TryGetControlledPresentation(out SimControlledUnitView view))
            {
                return Reject(DirectActionAvailability.NoControlledUnit);
            }

            UnitLoadout loadout = _loadouts != null ? _loadouts.Get(view.EntityId) : UnitLoadout.Empty;

            // 失能拦截点。TryGetOrgan 只返回**未失能**的器官，所以"器官在但坏了"
            // 与"压根没长"在这里被区分开，而两者都到不了下面任何一条释放路径。
            if (!loadout.TryGetOrgan(action, out UnitLoadoutOrgan organ))
            {
                return Reject(loadout.HasOrganInSlot(action)
                    ? DirectActionAvailability.OrganDisabled
                    : DirectActionAvailability.NoOrgan);
            }

            if (action == LoadoutAction.Interact && !InteractTargetsAvailable)
            {
                // 器官在、也没坏，就是没东西可交互。明确判不可用，不假装做了什么。
                return Reject(DirectActionAvailability.NoInteractTarget);
            }

            bool onPlayerBody = view.Faction == SimFaction.Player &&
                                loadout.Origin == UnitLoadoutOrigin.PlayerProjection;

            // 形态与代价同一个来源：这件器官自己的目录条目，伤害数值已换成真实编译结果
            // （M4-R00-02 队列①-1，见 OrganKernelActionTable.ResolveCompiled 注释）。
            // 两条释放路都在这里解析一次，于是"能不能按"与"按了要付多少"永远说的是同一件器官。
            // geneIds 直接取自装配（M4-R00-02 队列②号项）：TemplateDerived 友军（萌生腔/回巢改造）
            // 现在真的带基因；ArchetypeDerived 固定队友结构上没有基因概念，organ.GeneIds 恒为 null，
            // 兜底传空列表。
            OrganKernelAction act = OrganKernelActionTable.ResolveCompiled(
                organ.OrganId, organ.GeneIds ?? Array.Empty<string>(), _kernelReleaseSeed);

            // ── M2-03c：三道闸门，与失能同样**在释放入口**拦 ──
            // 只在 UI 上把按钮灰掉、不在入口拦的话，按键照样生效，那是"显示禁用、实际可用"。
            //
            // 冷却只管内核路：玩家本体走的是委托，它的冷却归既有 AbilitySystem 的技能槽管，
            // 在这里再叠一层就是第二层冷却——同一个键两条互不知情的冷却线，
            // 玩家读不出自己到底在等谁。
            DirectVitalsGate gate = _vitals.Evaluate(view.EntityId, action, act, checkCooldown: !onPlayerBody);
            if (gate != DirectVitalsGate.Allowed)
            {
                return Reject(ToAvailability(gate));
            }

            bool emitterBlocked = false;
            bool released = onPlayerBody
                ? ReleaseOnPlayerBody(action)
                : ReleaseOnKernel(view, act, aim, out emitterBlocked);

            if (!released)
            {
                // 三条路的失败含义不同：委托路失败=被委托的技能槽没就绪；内核路失败分两种——
                // 器官没有可释放形态，或发射点被障碍挡死（CP-REQ-003 第③级，队列③-10）。
                // 混成一个原因会让排查从"看一眼"变成"猜"。
                return Reject(onPlayerBody
                    ? DirectActionAvailability.NotReady
                    : emitterBlocked
                        ? DirectActionAvailability.EmitterBlocked
                        : DirectActionAvailability.NoKernelAction);
            }

            // 扣账在释放**之后**：判到一半就扣，会出现"代谢付了但什么都没打出来"
            // （委托路的技能槽没就绪、器官没有内核形态都会走到上面那个 return）。
            _vitals.Commit(view.EntityId, action, act, applyCooldown: !onPlayerBody);

            ReleaseCount++;
            LastReleasedOrganId = organ.OrganId;
            LastReleaseResult = DirectActionAvailability.Available;
            return true;
        }

        private static DirectActionAvailability ToAvailability(DirectVitalsGate gate)
        {
            switch (gate)
            {
                case DirectVitalsGate.Cooling: return DirectActionAvailability.Cooling;
                case DirectVitalsGate.Overloaded: return DirectActionAvailability.Overloaded;
                case DirectVitalsGate.NotEnoughMetabolism: return DirectActionAvailability.NotEnoughMetabolism;
                default: return DirectActionAvailability.Available;
            }
        }

        /// <summary>
        /// 玩家本体：委托给既有 <see cref="AbilitySystem"/>，不新建释放路径。
        /// 落到槽 1..N（本来就 Ready 即自动施放）的理由见 <see cref="PlayerPrimaryAbilitySlot"/> 注释。
        /// </summary>
        private bool ReleaseOnPlayerBody(LoadoutAction action)
        {
            if (_abilities == null)
            {
                return false;
            }

            int slot = action == LoadoutAction.Utility ? PlayerUtilityAbilitySlot : PlayerPrimaryAbilitySlot;
            if (slot >= _abilities.SlotCount)
            {
                return false;
            }

            LastReleasedKernelAction = OrganKernelAction.None;
            return _abilities.TryCastAuto(slot);
        }

        /// <summary>
        /// 非玩家友军：按那件器官自己的参数一次性向内核下请求。
        ///
        /// M2-07 起真正的释放动作在 <see cref="OrganReleaseRunner"/>，**这具身体交给 AI 之后
        /// 走的是同一个函数**——那正是"同一具身体，谁开都打出同样的东西"的兑现点。
        /// 本方法只剩下"把受控视图拆成参数 + 记一次诊断"。
        /// </summary>
        private bool ReleaseOnKernel(in SimControlledUnitView view, in OrganKernelAction act, float2 aim, out bool emitterBlocked)
        {
            _kernelReleaseSeed++;

            float2 dir = math.normalizesafe(aim, new float2(1f, 0f));
            // M4-R00-02 队列③-10（CP-REQ-003 第③级）：诊断用途的发射点解析，与 Release 内部对
            // Projectile 形态做的是同一个纯函数、同一份输入（含"不叠加 projectileRadius"这条口径，
            // 见 OrganReleaseRunner.Release 的注释）——EmissionContext 记的就是这次释放实际会用到
            // 的发射点，不是另算一份可能对不上的近似值。
            CombatBallistics.TryResolveEmitterPosition(
                view.Position, view.Radius, dir, 0f, _sim.Obstacles, _sim.ArenaHalfExtent, out float2 emitterPos);

            // M4-R00-02 队列①-1：记录本次释放的发射上下文（诊断/回归锚点，见 EmissionContext 类注释）。
            LastEmissionContext = new EmissionContext(
                view.EntityId, view.Faction, ControllerKind.DirectFriendly, act.OrganId,
                view.Position, view.Radius, view.BodyForward, aim, emitterPos, SimBodyPartSlot.None, _kernelReleaseSeed);

            // surgicalAim: true 是直控特有的——手术窗口（M2-05）本来就是"人手瞄准接点"的产物。
            // AI 那条路按内核给的锁定接点来，不共用这个开关。
            bool released = OrganReleaseRunner.Release(
                _sim, _status, view.UnitIndex, view.Position, view.Radius,
                act, aim, SimFaction.Hostile, surgicalAim: true, out emitterBlocked);

            if (released)
            {
                LastReleasedKernelAction = act;
            }

            return released;
        }

        private bool Reject(DirectActionAvailability reason)
        {
            LastReleaseResult = reason;
            return false;
        }
    }
}
