using BinGames.Sim;
using GameLogic.Battle;

namespace GameLogic.Control
{
    /// <summary>
    /// AI 控制下的友军用**自己的器官**开火（M2-07）。
    ///
    /// ── 它修的是什么 ──
    /// 试玩反馈 #5：「AI 与直控打法完全不同」。根因不是数值没调好，是同一具身体有两套战斗真相源——
    /// 玩家开它时走器官（真弹体 / 扇形 / 区域），松手之后走 <c>BehaviorArchetype.AttackDamage</c>
    /// 的瞬时扣血。于是"接管"这件事没有可比较的基准：你根本不是在开同一把枪。
    ///
    /// ── 职责切分 ──
    /// 内核负责**什么时候、朝谁开**（只有它有空间哈希、射程、阵营），结论以
    /// <see cref="MinionFireOpportunity"/> 逐帧抛出；本类负责**打出什么**，答案来自那具身体的器官。
    /// 释放动作本身调 <see cref="OrganReleaseRunner.Release"/>，**与玩家直控逐字是同一个函数**。
    /// 弹体最终仍由内核生成，"弹道唯一真相在内核"不变。
    ///
    /// ── 为什么冷却在这里而不在内核 ──
    /// 器官的冷却由 <see cref="UnitVitalsRegistry"/> 推导（形态 → 冷却 / 代谢 / 过载债），
    /// 内核那条 <c>arc.AttackCooldown</c> 与它毫无关系。两边都压一条就是两条互不知情的冷却线，
    /// 表现为"明明该好了却不打"。所以内核每帧抛机会、不起冷却，这里的闸门是唯一那条。
    /// 顺带兑现了一件本来就该成立的事：**过载的身体交给 AI 也一样打不出东西**。
    ///
    /// ── 性能 ──
    /// 每帧开销 = O(本帧开火机会数)，而开火机会只可能来自被标过器官驱动的友军
    /// （数量恒被 MinionCap 卡在个位数），**与敌人数量无关**——热更层红线不破。
    /// 没人开火时是一次 <c>MinionFireCount == 0</c> 判断。
    /// </summary>
    public sealed class MinionOrganCombatDriver
    {
        /// <summary>
        /// AI 用哪个槽开火。**只有主武器槽**，这是一处有意画死的边界：
        /// 功能器官（Utility）要用得对，前提是有"什么时候该用它"的战术判断，
        /// 而那套判断今天不存在。让 AI 一冷却好就无脑放功能器官，不是"打法统一"，
        /// 是给它加了一个玩家不会那样用的新打法——那等于把刚合上的分叉换个方向再劈一次。
        /// </summary>
        public const LoadoutAction AiFireSlot = LoadoutAction.Primary;

        /// <summary>
        /// AI 开火的过载债安全线（相对 <see cref="UnitVitalsRegistry.StrainOverloadThreshold"/>）。
        /// 这一发打完会超过它就先不打。
        ///
        /// ── 为什么必须有这条线 ──
        /// AI 不会"手下留情"：它一冷却好就开，而器官的过载债正是按功率（代谢/冷却）算的，
        /// 于是全速开火的稳态**必然是把自己烧穿**——债涨到 100 进过载、被压制、掉到 60 解除、
        /// 再全速烧上去。那具身体会在"能打"和"打不了"之间无限来回，平均输出还不如老路，
        /// 看上去就是"AI 拿着好器官却站着发呆"。这不是数值没调好，是没有闸门的必然结果。
        ///
        /// ── 为什么这不是给 AI 打折，而是 M2-05 想要的那个差别 ──
        /// 玩家可以把一具身体按到爆表换一轮爆发（代价是随后的压制窗口），AI 不行——
        /// 它守着安全线换持续输出。**同样的器官、同样的数值、同样的弹体**，
        /// 差别只在"敢不敢烧"。这正是"为什么这次值得接管"的一个不靠加数值的答案。
        /// </summary>
        public const float AiStrainCeilingRatio = 0.6f;

        private SimBridge _sim;
        private UnitLoadoutRegistry _loadouts;
        private UnitVitalsRegistry _vitals;
        private StatusSystem _status;

        // ── 诊断（验收与试玩面板读，不参与判定）────────────────────────────
        //
        // 这几个量存在的理由很实际：AI 不开火有五种完全不同的原因（没装配 / 器官坏了 /
        // 没有内核形态 / 冷却 / 过载或代谢不够），而它们在画面上长得一模一样——都是"站着不打"。
        // 没有这几个计数，排查只能靠猜。

        /// <summary>本局累计真的释放了多少次。</summary>
        public int ReleaseCount { get; private set; }

        /// <summary>最近一次释放用的器官 id。</summary>
        public string LastReleasedOrganId { get; private set; }

        /// <summary>最近一次释放的内核动作形态（验收据此断言"AI 打出来的和玩家打出来的是同一种东西"）。</summary>
        public OrganKernelAction LastReleasedKernelAction { get; private set; }

        /// <summary>最近一次**没能**释放的原因；一直是 Allowed 说明没被闸门拦过。</summary>
        public DirectVitalsGate LastBlockedGate { get; private set; } = DirectVitalsGate.Allowed;

        /// <summary>上一帧内核抛了多少条开火机会。</summary>
        public int LastOpportunityCount { get; private set; }

        /// <summary>累计因为"这具身体没有可释放的主武器器官"而落空的机会数。</summary>
        public int NoOrganCount { get; private set; }

        /// <summary>累计因为守 <see cref="AiStrainCeilingRatio"/> 而主动不开的次数。
        /// 它持续上涨说明这具身体的器官对 AI 来说功率偏高，是调数值的信号，不是 bug。</summary>
        public int StrainHoldCount { get; private set; }

        public void Bind(SimBridge sim, UnitLoadoutRegistry loadouts,
            UnitVitalsRegistry vitals, StatusSystem status)
        {
            _sim = sim;
            _loadouts = loadouts;
            // 账本必须是**与玩家直控同一个实例**：分成两本，同一具身体在"被开"和"自己打"
            // 两种状态下就会各攒各的冷却与过载债，那正是本段要消灭的那类分叉。
            _vitals = vitals;
            _status = status;
            ReleaseCount = 0;
            NoOrganCount = 0;
            StrainHoldCount = 0;
            LastOpportunityCount = 0;
            LastReleasedOrganId = null;
            LastReleasedKernelAction = OrganKernelAction.None;
            LastBlockedGate = DirectVitalsGate.Allowed;
        }

        public void Unbind()
        {
            // 先把内核里那批"等我作答"的位放掉，再撒手。顺序反了就收不回来了
            // （口径与 OverloadSuppressionMirror.Unbind 必须先放掉已推下去的压制位一致）。
            //
            // 放掉之后这些身体退回行为原型数值的降级路——它们照常会打，只是打的不再是器官。
            // 这比"留着位、没人驱动、一发打不出来"好得多：那是无声的永久失能。
            _sim?.ClearAllOrganCombat();
            _sim = null;
            _loadouts = null;
            _vitals = null;
            _status = null;
        }

        /// <summary>
        /// 消费上一次 Step 抛出的开火机会。
        ///
        /// 读的是 <c>_sim.Snapshot</c>，机会里的槽位索引与这份快照同源，因此位置/半径可以直接取；
        /// 但仍要核对 <see cref="SimEntityId"/>——槽位是回收复用的，这是本仓既定纪律
        /// （见 <c>SimBridge.TryResolveUnitIndex</c> 的两条使用纪律）。
        /// </summary>
        public void Tick(bool paused)
        {
            if (paused || _sim == null || !_sim.Running || _loadouts == null || _vitals == null)
            {
                LastOpportunityCount = 0;
                return;
            }

            SimSnapshot snapshot = _sim.Snapshot;
            int count = snapshot.MinionFireCount;
            LastOpportunityCount = count;
            if (count <= 0 || !snapshot.MinionFires.IsCreated)
            {
                return;
            }

            for (int f = 0; f < count && f < snapshot.MinionFires.Length; f++)
            {
                MinionFireOpportunity fire = snapshot.MinionFires[f];
                int idx = fire.UnitIndex;

                // 槽位复用核对：验证在先、使用在后。
                if (idx < 0 || idx >= snapshot.Count || !snapshot.IsAlive(idx) ||
                    !snapshot.EntityId.IsCreated || snapshot.EntityId[idx] != fire.EntityId)
                {
                    continue;
                }

                UnitLoadout loadout = _loadouts.Get(fire.EntityId);
                // TryGetOrgan 只返回**未失能**的器官，所以"器官被打坏了"与"压根没长"都到不了下面。
                // 这与玩家按键时的拦截口径一字不差——被打坏主武器的身体，谁开都打不出东西。
                if (!loadout.TryGetOrgan(AiFireSlot, out UnitLoadoutOrgan organ))
                {
                    NoOrganCount++;
                    continue;
                }

                OrganKernelAction act = OrganKernelActionTable.Resolve(organ.OrganId);
                if (!act.IsValid)
                {
                    NoOrganCount++;
                    continue;
                }

                DirectVitalsGate gate = _vitals.Evaluate(fire.EntityId, AiFireSlot, act, checkCooldown: true);
                if (gate != DirectVitalsGate.Allowed)
                {
                    LastBlockedGate = gate;
                    continue;
                }

                // 守安全线（见 AiStrainCeilingRatio）。判的是"打完之后"会不会越线，
                // 不是"现在越没越"——按后者判会正好卡在线上进过载，闸门等于没装。
                float ceiling = UnitVitalsRegistry.StrainOverloadThreshold * AiStrainCeilingRatio;
                if (_vitals.Get(fire.EntityId).Strain + act.StrainCost > ceiling)
                {
                    StrainHoldCount++;
                    continue;
                }

                bool released = OrganReleaseRunner.Release(
                    _sim, _status, idx, snapshot.Position[idx], snapshot.Radius[idx],
                    act, fire.AimDirection, SimFaction.Hostile,
                    // 锁定到具体接点的那一发才算精准射击——语义与 M2-05b 原先透传接点的判据一致，
                    // 没锁定时打的是"混战里最近的那个"，套用接点没有意义。
                    surgicalAim: fire.TargetPart != SimBodyPartSlot.None,
                    targetPart: fire.TargetPart);

                if (!released)
                {
                    NoOrganCount++;
                    continue;
                }

                _vitals.Commit(fire.EntityId, AiFireSlot, act, applyCooldown: true);
                ReleaseCount++;
                LastReleasedOrganId = organ.OrganId;
                LastReleasedKernelAction = act;
            }
        }
    }
}
