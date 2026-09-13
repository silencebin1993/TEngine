using BinGames.Sim;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Core;
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

        /// <summary>弹体出膛点相对释放者体表再外推一点，避免刚生成就撞到自己。</summary>
        private const float MuzzleClearance = 0.2f;

        /// <summary>区域类动作钉在瞄准方向上多远（按区域半径成比例，不写死世界距离）。</summary>
        private const float ZoneThrowDistanceMul = 1.5f;

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

        /// <summary>三个量的账本（M2-03c）。UI 只读快照走 <see cref="ControlledVitals"/>。</summary>
        public UnitVitalsRegistry Vitals => _vitals;

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
        /// 每帧推进三个量的本地时钟（M2-03c）。**纯 O(1)**：不遍历任何实体，
        /// 代谢回复 / 过载债衰减 / 冷却推进全部在读写那一刻惰性补齐
        /// （见 <see cref="UnitVitalsRegistry"/> 类注释）。
        ///
        /// 暂停下不推进：暂停刷冷却是白送的，口径与 <c>_hub</c> 被暂停冻住一致。
        /// </summary>
        public void Tick(float dt, bool paused)
        {
            _vitals.Advance(dt, paused);
        }

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
            _vitals.Reset();
            RebuildCount = 0;
            ReleaseCount = 0;
            LastReleasedOrganId = null;
            LastReleasedKernelAction = OrganKernelAction.None;
            LastReleaseResult = DirectActionAvailability.NoControlledUnit;

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

            // 形态与代价同一个来源：这件器官自己的目录条目。两条释放路都在这里解析一次，
            // 于是"能不能按"与"按了要付多少"永远说的是同一件器官。
            OrganKernelAction act = OrganKernelActionTable.Resolve(organ.OrganId);

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

            bool released = onPlayerBody
                ? ReleaseOnPlayerBody(action)
                : ReleaseOnKernel(view, act, aim);

            if (!released)
            {
                // 两条路的失败含义不同：委托路失败=被委托的技能槽没就绪；
                // 内核路失败=这件器官没有可释放形态。混成一个原因会让排查从"看一眼"变成"猜"。
                return Reject(onPlayerBody
                    ? DirectActionAvailability.NotReady
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
        /// 四条分支全部走 <see cref="SimBridge"/> 上的既有入口，本段**没有给内核加任何能力**；
        /// 每次释放只发一次调用，逐单位的事全归 AOT 作业，热更层这里一个循环都没有。
        /// </summary>
        private bool ReleaseOnKernel(in SimControlledUnitView view, in OrganKernelAction act, float2 aim)
        {
            if (!act.IsValid)
            {
                // 这件器官没有可释放形态。**不许退回一发通用弹**——那会让所有单位打出同一种东西，
                // "动作与实体一致"当场失效，而且"这一发是哪来的"再也追不到源头。
                return false;
            }

            float2 origin = view.Position;
            float2 dir = math.normalizesafe(aim, new float2(1f, 0f));
            int sourceLogicId = ResolveLogicId(view.UnitIndex);

            switch (act.Kind)
            {
                case OrganKernelActionKind.Projectile:
                    _sim.FireProjectile(
                        origin + dir * (view.Radius + MuzzleClearance),
                        dir, act.Speed, act.Damage, act.Radius, act.Lifetime, act.Pierce,
                        SimFaction.Hostile, act.ApplyStatus, sourceLogicId);
                    break;

                case OrganKernelActionKind.Cone:
                    _sim.DamageCone(origin, act.Radius, dir, act.HalfAngleDeg, act.Damage,
                        SimFaction.Hostile, act.ApplyStatus, sourceLogicId: sourceLogicId);
                    break;

                case OrganKernelActionKind.Zone:
                    _sim.SpawnZone(
                        act.FollowSelf ? origin : origin + dir * (act.Radius * ZoneThrowDistanceMul),
                        act.Radius, act.Seconds, act.Damage, act.Interval,
                        SimFaction.Hostile, applyStatus: act.ApplyStatus, sourceLogicId: sourceLogicId,
                        followUnitIndex: act.FollowSelf ? view.UnitIndex : SimConst.InvalidIndex);
                    break;

                case OrganKernelActionKind.Status:
                    if (_status != null)
                    {
                        _status.ApplyTimedArea(origin, act.Radius, act.ApplyStatus, act.Seconds);
                    }
                    else
                    {
                        _sim.ApplyStatusArea(origin, act.Radius, act.ApplyStatus);
                    }
                    break;

                default:
                    return false;
            }

            LastReleasedKernelAction = act;
            return true;
        }

        /// <summary>取释放者的 LogicId，供内核把伤害归属回来源。解析不到时用 0（= 无归属）。</summary>
        private int ResolveLogicId(int unitIndex)
        {
            SimSnapshot snapshot = _sim.Snapshot;
            return unitIndex >= 0 && unitIndex < snapshot.Count && snapshot.LogicId.IsCreated
                ? snapshot.LogicId[unitIndex]
                : 0;
        }

        private bool Reject(DirectActionAvailability reason)
        {
            LastReleaseResult = reason;
            return false;
        }
    }
}
