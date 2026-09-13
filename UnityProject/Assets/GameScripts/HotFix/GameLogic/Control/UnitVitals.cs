using System;
using System.Collections.Generic;
using BinGames.Sim;
using UnityEngine;

namespace GameLogic.Control
{
    /// <summary>一次释放被三个量里的哪一个挡下了。<see cref="Allowed"/> = 没被挡。</summary>
    public enum DirectVitalsGate : byte
    {
        /// <summary>放行。</summary>
        Allowed = 0,
        /// <summary>这个动作槽还在冷却。</summary>
        Cooling = 1,
        /// <summary>过载债越过阈值，这具身体处于过载态。</summary>
        Overloaded = 2,
        /// <summary>代谢资源不够付这一次释放的代价。</summary>
        NotEnoughMetabolism = 3,
    }

    /// <summary>
    /// 一具身体当下的代谢 / 过载债 / 冷却（M2-03c）。只读快照，给 UI 与验收读。
    ///
    /// **是快照不是引用**：读的那一刻已经把时间推进补齐了（见 <see cref="UnitVitalsRegistry"/> 的惰性结算），
    /// 所以调用方拿到的是"此刻"的值；但它不会自己跟着时间变，需要最新值就再取一次。
    /// </summary>
    public struct UnitVitalsView
    {
        /// <summary>false = 没有有效实体（没有受控单位 / 无效 id）。UI 据此决定是否上屏。</summary>
        public bool Valid;

        public SimEntityId EntityId;

        public float Metabolism;
        public float MetabolismMax;

        public float Strain;
        /// <summary>越过它进入过载态。</summary>
        public float StrainThreshold;

        /// <summary>过载中：释放入口会一律拒绝，直到过载债衰减回 <see cref="StrainThreshold"/> 的清除比例以下。</summary>
        public bool Overloaded;

        /// <summary>各动作槽的剩余冷却秒数（下标 = <see cref="LoadoutAction"/>）。0 = 就绪。</summary>
        public float MoveCooldown;
        public float PrimaryCooldown;
        public float UtilityCooldown;
        public float InteractCooldown;

        public float MetabolismRatio =>
            MetabolismMax > 0f ? Mathf.Clamp01(Metabolism / MetabolismMax) : 0f;

        public float StrainRatio =>
            StrainThreshold > 0f ? Mathf.Clamp01(Strain / StrainThreshold) : 0f;

        public float CooldownOf(LoadoutAction action)
        {
            switch (action)
            {
                case LoadoutAction.Primary: return PrimaryCooldown;
                case LoadoutAction.Utility: return UtilityCooldown;
                case LoadoutAction.Interact: return InteractCooldown;
                default: return MoveCooldown;
            }
        }
    }

    /// <summary>
    /// 代谢 / 过载债 / 按槽冷却的账本（M2-03c）。
    ///
    /// ── 为什么按 <see cref="SimEntityId"/> 归属，而不是一套全局单例 ──
    /// 这正是 M2-03a 在纠正的那个错误。装配已经挂到实体上了（"这具身体长着什么"），
    /// 如果代价与冷却还是全局的，就会出现"换一具身体接着按同一条冷却"
    /// ——玩家看到的现象是"我换了个人，技能却还是灰的"，而且它从根上否定了
    /// M2-03 的立论（能按出什么、按得多勤，都该由这具身体决定）。
    /// 键选 <see cref="SimEntityId"/> 的理由与 M2-03a 契约 §2 完全相同：槽位索引会被复用，
    /// LogicId 没有失效语义，只有稳定实体 id 在槽位复用时会重新分配。
    ///
    /// ── 为什么是惰性结算，而不是每帧遍历所有登记单位 ──
    /// 逐帧给每个登记实体回代谢、衰减过载债、推进冷却，是一个 O(登记单位数) 的每帧循环，
    /// 直接撞上仓规架构红线第 4 条（热更层每帧必须与场上单位数无关）。
    /// 这里改成：每帧只把一个**本地单调时钟**推进一格（<see cref="Advance"/>，纯 O(1)），
    /// 每条记录自己记住"上次结算到哪一刻"；任何一次读/写之前先按时间差把它补齐
    /// （<see cref="Sync"/>）。读写本身只发生在"HUD 看当前受控实体"和"按下了某个键"这两处，
    /// 都是 O(1)。结果与逐帧结算等价，因为回复/衰减都是线性的。
    ///
    /// ── 时钟为什么不用 <see cref="Time"/> ──
    /// 暂停下冷却不该走、代谢不该回、过载债不该衰减，否则"暂停刷冷却"是白送的。
    /// 时钟由 <c>CellStageFlow.Update</c> 带着暂停标志喂进来（见 <see cref="Advance"/>），
    /// 与 <c>_hub</c> 被暂停冻住的口径一致。Edit 模式回归也因此能直接把时间快进，
    /// 不必真的等真实秒数过去。
    ///
    /// ── ⚠️ 为什么叫 Strain（过载债）而不是 Heat（热债）：**不要改回去** ──
    /// 仓里已经有两个语义完全不同的 <c>Heat</c>，都在 ComposeEngine，都不是这个东西：
    ///   1. <c>ComposeEngine.Core.Packet.Heat</c> —— 装配链路上的**过路**热负荷。
    ///      每次组合 <c>new Packet()</c>，一趟链走完就丢，**不跨释放**；过热阈值是 <b>8</b>，
    ///      过热后果是清零 + 转一圈瞬时脉冲（<c>HeatShockModule</c>）。
    ///   2. <c>ComposeEngine.Core.SubstanceVector.Heat</c> —— 九维物质代数里的**温度**维，
    ///      **有正负**（Fire +2 / Ice −1.5 / Frozen −2，负 = 冷），是元素属性，与过载无关。
    /// 本类这个量是**跨释放累积、按身体归属、阈值 100 带滞回**的第三种东西——
    /// 与 1 尺度差 12.5 倍、生命周期完全不同，与 2 连量纲都不是一回事。
    ///
    /// 当前不会真的打架：写 <c>Packet.Heat</c> 的四个器官（<c>org_lens</c> / <c>org_merge</c> /
    /// <c>org_radiator</c> / <c>org_insulate</c>）在 <c>OrganelleCatalog</c> 里**全部 isRetired**，
    /// 现役的 <c>gene_heatshock</c> 只读不写，所以那条链路目前恒为 0（见 <c>EmergenceSmoke.cs</c> 的同名说明）。
    /// 但**"现在不冲突"正是现在改名最便宜的理由**：等哪天复活 <c>org_lens</c>（"这件器官产热"），
    /// 两个 Heat 会在同一段代码里以 8 和 100 两种尺度共存，那时候再分是纯返工。
    /// 冻结总案 §5.2 的器官表把 6 个器官写成"读 Heat"，看文档很容易以为这套已经在跑——
    /// 名字分开之后，那份文档说的是哪一个就不再需要猜。
    ///
    /// ── M2-04b：为什么"进/出过载态"要对外播报（<see cref="OverloadChanged"/>）──
    /// M2-03c 的过载债只挡得住**玩家直控**那一条释放入口（<see cref="Evaluate"/>）。
    /// 同一具身体交给 AI 之后走的是内核 <c>SimWorld.ResolveMinionCombat</c>，那里只认原型的
    /// <c>AttackCooldown</c>，根本不知道有这本账——于是"把身体打到过载 → 退出直控换一具接着打"
    /// 就能**完全规避过载惩罚**，"过载债按身体归属"这条立论只兑现了一半。
    /// 修法是把过载态镜像成内核的 <c>SimStatus.Overloaded</c> 位（见 <c>OverloadSuppressionMirror</c>）。
    /// 本类因此要在"过载态真正发生变化的那一刻"播报一次——**只在变化时，不是每帧**。
    ///
    /// ── 惰性结算与"没人读就永远不解除"的冲突，以及巡守表 ──
    /// 惰性结算的前提是"没人读 = 结果无所谓"。镜像到内核之后这条前提破了：
    /// 一具交给 AI 的身体没有任何读者，<see cref="Sync"/> 永远不跑，
    /// 过载位就会**永久**挂在它身上——单位被无声地钉死，这是本段最危险的失败模式。
    /// 所以额外维护一张**只装当前处于过载态的实体**的巡守表 <c>_overloadWatch</c>，
    /// 在 <see cref="Advance"/> 里逐条补齐。它的长度与场上单位数**无关**：
    /// 过载债只可能由玩家的释放（<see cref="Commit"/>）产生，玩家一次只操一具身体，
    /// 稳态长度是个位数，硬上限 <see cref="MaxOverloadWatch"/>。
    /// 清除判据仍然只有 <see cref="Sync"/> 里那一套（衰减 + 滞回）——
    /// **不给内核第二套过期逻辑**，两套过期必然漂移。
    /// </summary>
    public sealed class UnitVitalsRegistry
    {
        /// <summary>代谢池上限。**产品决策，可推翻**：本段不引入"每具身体代谢上限不同"的内容维度，
        /// 那需要一张表和一套成长口径，属于后续里程碑。</summary>
        public const float MetabolismMax = 100f;

        /// <summary>代谢每秒回复量。</summary>
        public const float MetabolismRegenPerSecond = 20f;

        /// <summary>过载债阈值：越过即过载。</summary>
        public const float StrainOverloadThreshold = 100f;

        /// <summary>过载债每秒衰减量。</summary>
        public const float StrainDecayPerSecond = 25f;

        /// <summary>
        /// 过载解除比例（相对 <see cref="StrainOverloadThreshold"/>）。
        ///
        /// **刻意做成滞回而不是同一个阈值**：同阈值的话，刚跌到 99.9 就解除，下一次释放立刻加回去，
        /// 玩家会看到"能按 / 不能按"在一两帧之间反复横跳——那是最难向玩家解释的一种手感。
        /// 滞回把"过载"变成一段要实打实等过去的窗口。
        /// </summary>
        public const float StrainClearRatio = 0.6f;

        /// <summary>过载解除的绝对过载债值。</summary>
        public const float StrainClearThreshold = StrainOverloadThreshold * StrainClearRatio;

        /// <summary>条目数超过它才做一次全表清扫。同 M2-03a 契约 §2 的口径：刻意不逐帧清扫。</summary>
        public const int SweepThreshold = 64;

        /// <summary>多久没被碰过的条目算陈旧（本地时钟秒）。满代谢、零过载债、冷却走完之后，
        /// 一条记录与"从没存在过"在行为上完全等价，删掉它不改变任何结果。</summary>
        public const float StaleSeconds = 30f;

        /// <summary>
        /// 同时处于过载态的身体数上限（M2-04b 巡守表长度上限）。
        ///
        /// 它同时是本类每帧开销的上界。取 16 已经远超实际：过载债只由玩家的释放产生
        /// （<see cref="Commit"/>），玩家一次只操一具身体，而一具身体从 100 衰减到清除线 60
        /// 只要 1.6 秒。溢出时按"最早进入过载的那一条先解除"处理（见 <see cref="SetOverloaded"/>）——
        /// **宁可让一具身体提前脱离过载，也绝不能让它的内核压制位悬空**，
        /// 后者等于把那个单位无声地永久钉死。
        /// </summary>
        public const int MaxOverloadWatch = 16;

        private sealed class Entry
        {
            /// <summary>自己的键。<see cref="Sync"/> 里要播报过载态变化，得知道是谁。</summary>
            public SimEntityId Id;
            public float Metabolism = MetabolismMax;
            public float Strain;
            public bool Overloaded;
            /// <summary>各槽的"就绪时刻"（本地时钟）。下标 = <see cref="LoadoutAction"/>。</summary>
            public readonly float[] ReadyAt = new float[DirectActionSet.SlotCount];
            public float LastSync;
        }

        private readonly Dictionary<SimEntityId, Entry> _entries = new Dictionary<SimEntityId, Entry>(8);
        private readonly List<SimEntityId> _sweepScratch = new List<SimEntityId>(8);

        /// <summary>当前处于过载态的实体。长度与场上单位数无关，见类注释。</summary>
        private readonly List<SimEntityId> _overloadWatch = new List<SimEntityId>(MaxOverloadWatch);

        private float _clock;

        /// <summary>在册条目数。验收/调试用。</summary>
        public int Count => _entries.Count;

        /// <summary>当前处于过载态的身体数。验收/调试用。</summary>
        public int OverloadedCount => _overloadWatch.Count;

        /// <summary>本地单调时钟（秒）。只在非暂停帧前进。</summary>
        public float Clock => _clock;

        /// <summary>
        /// 某具身体**进入或离开**过载态时播报一次（M2-04b）。
        /// 参数二 true = 刚进入过载，false = 刚解除。
        ///
        /// **只在变化那一刻发一次**，不是每帧状态广播——订阅方（<c>OverloadSuppressionMirror</c>）
        /// 要把它写进内核，而"写内核"是需要解析实体槽位的 O(单位数) 操作，
        /// 每帧做就撞上热更层架构红线。
        /// </summary>
        public event Action<SimEntityId, bool> OverloadChanged;

        /// <summary>
        /// 推进本地时钟并补齐巡守表。
        ///
        /// **每帧开销与场上单位数无关**：一次 float 加法 + 巡守表逐条补齐，
        /// 后者长度 ≤ <see cref="MaxOverloadWatch"/>，而且稳态下是 0（没人过载时整段是空循环）。
        /// 仍然**不**遍历 <c>_entries</c>——代谢回复、冷却推进照旧惰性结算。
        /// 只有过载态例外，理由见类注释（它被镜像到了内核，没人读也必须按时解除）。
        /// </summary>
        /// <param name="paused">玩法暂停时不推进——暂停下刷冷却/回代谢是白送的。</param>
        public void Advance(float dt, bool paused = false)
        {
            if (paused || dt <= 0f)
            {
                return;
            }

            _clock += dt;

            // 倒序遍历：Sync 可能在里面把当前这条从表里摘掉。
            for (int i = _overloadWatch.Count - 1; i >= 0; i--)
            {
                if (i >= _overloadWatch.Count)
                {
                    continue;
                }

                if (_entries.TryGetValue(_overloadWatch[i], out Entry e))
                {
                    Sync(e);
                }
                else
                {
                    // 条目没了却还在巡守表里：不可能发生（SweepStale 只扫非过载条目），
                    // 但真发生时必须把压制位放掉，否则那个单位永久打不出东西。
                    SimEntityId orphan = _overloadWatch[i];
                    _overloadWatch.RemoveAt(i);
                    OverloadChanged?.Invoke(orphan, false);
                }
            }
        }

        /// <summary>跨局清空。实体 id 只在生成它的那个 <c>SimWorld</c> 内有效，跨局一律作废
        /// （同 M2-03a 契约 §11 的理由）。
        ///
        /// **刻意不为巡守表里的条目播报"解除"**：Reset 只发生在换局（<c>Bind</c>）与拆台
        /// （<c>Unbind</c>），那一刻旧世界里的实体 id 已经没有意义，播报出去也解析不到槽位。
        /// 内核那一侧由镜像自己在 <c>Unbind</c> 时收尾。</summary>
        public void Reset()
        {
            _entries.Clear();
            _sweepScratch.Clear();
            _overloadWatch.Clear();
            _clock = 0f;
        }

        /// <summary>取某具身体的三个量。无效 id 返回 <c>Valid = false</c> 的空快照，不抛。</summary>
        public UnitVitalsView Get(SimEntityId id)
        {
            if (!id.IsValid)
            {
                return default;
            }

            Entry e = Resolve(id);
            Sync(e);

            return new UnitVitalsView
            {
                Valid = true,
                EntityId = id,
                Metabolism = e.Metabolism,
                MetabolismMax = MetabolismMax,
                Strain = e.Strain,
                StrainThreshold = StrainOverloadThreshold,
                Overloaded = e.Overloaded,
                MoveCooldown = RemainingCooldown(e, (int)LoadoutAction.Move),
                PrimaryCooldown = RemainingCooldown(e, (int)LoadoutAction.Primary),
                UtilityCooldown = RemainingCooldown(e, (int)LoadoutAction.Utility),
                InteractCooldown = RemainingCooldown(e, (int)LoadoutAction.Interact),
            };
        }

        /// <summary>该实体是否已有记录。<b>不</b>建记录——给验收用，避免断言本身改变被测状态。</summary>
        public bool IsTracked(SimEntityId id) => id.IsValid && _entries.ContainsKey(id);

        /// <summary>
        /// 这一次释放能不能过三道闸门。**只判不扣**——扣账在 <see cref="Commit"/>，
        /// 因为释放本身还可能被下游拒绝（玩家本体的技能槽没就绪 / 器官没有内核形态），
        /// 判到一半就扣会出现"扣了代谢却什么都没打出来"。
        /// </summary>
        /// <param name="checkCooldown">
        /// 玩家本体走的是委托路径，冷却由既有 <c>AbilitySystem</c> 的技能槽管；
        /// 在这里再叠一层就是**第二层冷却**，会让同一个键出现两条互不知情的冷却线。
        /// </param>
        public DirectVitalsGate Evaluate(SimEntityId id, LoadoutAction action,
            in OrganKernelAction act, bool checkCooldown)
        {
            if (!id.IsValid || !act.IsValid)
            {
                // 没有可释放形态的器官谈不上代价：它在上游就会被判 NoKernelAction，
                // 在这里额外拦一道只会把拒绝原因说成别的东西。
                return DirectVitalsGate.Allowed;
            }

            Entry e = Resolve(id);
            Sync(e);

            int slot = (int)action;
            if (checkCooldown && slot >= 0 && slot < e.ReadyAt.Length && e.ReadyAt[slot] > _clock)
            {
                return DirectVitalsGate.Cooling;
            }

            if (e.Overloaded)
            {
                return DirectVitalsGate.Overloaded;
            }

            if (e.Metabolism < act.MetabolicCost)
            {
                return DirectVitalsGate.NotEnoughMetabolism;
            }

            return DirectVitalsGate.Allowed;
        }

        /// <summary>
        /// 扣账：付代谢、累过载债、起冷却。**只在释放真的发生之后调用。**
        /// </summary>
        public void Commit(SimEntityId id, LoadoutAction action, in OrganKernelAction act, bool applyCooldown)
        {
            if (!id.IsValid || !act.IsValid)
            {
                return;
            }

            Entry e = Resolve(id);
            Sync(e);

            e.Metabolism = Mathf.Max(0f, e.Metabolism - act.MetabolicCost);
            e.Strain += act.StrainCost;
            if (e.Strain >= StrainOverloadThreshold)
            {
                SetOverloaded(e, true);
            }

            int slot = (int)action;
            if (applyCooldown && slot >= 0 && slot < e.ReadyAt.Length && act.Cooldown > 0f)
            {
                e.ReadyAt[slot] = _clock + act.Cooldown;
            }
        }

        /// <summary>
        /// 直接写代谢余量。**本段刻意只提供入口、不定义"什么情况下代谢会被额外抽走"**——
        /// 那属于后续的受伤 / 手术 / 环境代谢（同 M2-03a 对失能位的处理）。
        /// 当前唯一的生产写入路径是 <see cref="Commit"/>；本入口供验收与后续里程碑调用。
        /// </summary>
        public bool SetMetabolism(SimEntityId id, float value)
        {
            if (!id.IsValid)
            {
                return false;
            }

            Entry e = Resolve(id);
            Sync(e);
            e.Metabolism = Mathf.Clamp(value, 0f, MetabolismMax);
            return true;
        }

        /// <summary>叠加过载债（可为负）。越过阈值同样会进过载态，口径与 <see cref="Commit"/> 完全一致。</summary>
        public bool AddStrain(SimEntityId id, float amount)
        {
            if (!id.IsValid)
            {
                return false;
            }

            Entry e = Resolve(id);
            Sync(e);
            e.Strain = Mathf.Max(0f, e.Strain + amount);
            if (e.Strain >= StrainOverloadThreshold)
            {
                SetOverloaded(e, true);
            }
            else if (e.Overloaded && e.Strain <= StrainClearThreshold)
            {
                SetOverloaded(e, false);
            }

            return true;
        }

        /// <summary>
        /// 过载态的**唯一**写入口（M2-04b）。只有在真的发生变化时才动巡守表、才播报。
        ///
        /// 数值口径一行未改：谁能把它置真（越过 <see cref="StrainOverloadThreshold"/>）、
        /// 谁能把它置假（跌到 <see cref="StrainClearThreshold"/> 以下）全部照旧在调用方判定，
        /// 这里只负责"记账 + 播报"。
        /// </summary>
        private void SetOverloaded(Entry e, bool value)
        {
            if (e.Overloaded == value)
            {
                return;
            }

            e.Overloaded = value;

            if (value)
            {
                if (_overloadWatch.Count >= MaxOverloadWatch)
                {
                    // 溢出兜底：先把最早进入过载的那条放掉，腾出位置。
                    // 让它提前脱离过载，好过让它的内核压制位永远悬着。
                    SimEntityId oldest = _overloadWatch[0];
                    if (_entries.TryGetValue(oldest, out Entry victim))
                    {
                        victim.Strain = Mathf.Min(victim.Strain, StrainClearThreshold);
                        SetOverloaded(victim, false);
                    }
                    else
                    {
                        _overloadWatch.RemoveAt(0);
                        OverloadChanged?.Invoke(oldest, false);
                    }
                }

                _overloadWatch.Add(e.Id);
            }
            else
            {
                _overloadWatch.Remove(e.Id);
            }

            OverloadChanged?.Invoke(e.Id, value);
        }

        private float RemainingCooldown(Entry e, int slot)
        {
            if (slot < 0 || slot >= e.ReadyAt.Length)
            {
                return 0f;
            }

            return Mathf.Max(0f, e.ReadyAt[slot] - _clock);
        }

        private Entry Resolve(SimEntityId id)
        {
            if (_entries.TryGetValue(id, out Entry existing))
            {
                return existing;
            }

            if (_entries.Count >= SweepThreshold)
            {
                SweepStale();
            }

            // 新身体一律满代谢、零过载债、无冷却："刚接管一具没打过的身体"就该是这个状态。
            var made = new Entry { Id = id, LastSync = _clock };
            _entries[id] = made;
            return made;
        }

        /// <summary>
        /// 把"回到初始态且很久没被碰过"的条目删掉。**不做存活验证**：
        /// 那需要回内核解析槽位（<c>SimWorld.TryFindUnit</c> 是线性扫描，见 M2-03a 契约 §10），
        /// 而这里根本不需要知道谁死了——一条满代谢/零过载债/冷却走完的记录，
        /// 与"从来没有过这条记录"在行为上完全等价，删错了也只是下次重建一条一模一样的。
        /// </summary>
        private void SweepStale()
        {
            _sweepScratch.Clear();
            foreach (KeyValuePair<SimEntityId, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (_clock - e.LastSync < StaleSeconds)
                {
                    continue;
                }

                // 先补齐再判断：没补齐的话，一条其实早就回满的记录会被当成"还欠着账"留下来。
                Sync(e);
                if (e.Metabolism >= MetabolismMax && e.Strain <= 0f && !e.Overloaded)
                {
                    _sweepScratch.Add(kv.Key);
                }
            }

            for (int i = 0; i < _sweepScratch.Count; i++)
            {
                _entries.Remove(_sweepScratch[i]);
            }
            _sweepScratch.Clear();
        }

        /// <summary>把一条记录从 <c>LastSync</c> 推进到当下。回复与衰减都是线性的，
        /// 所以一次算清和逐帧累加等价（这正是惰性结算成立的前提）。</summary>
        private void Sync(Entry e)
        {
            float dt = _clock - e.LastSync;
            if (dt <= 0f)
            {
                e.LastSync = _clock;
                return;
            }

            e.LastSync = _clock;

            if (e.Metabolism < MetabolismMax)
            {
                e.Metabolism = Mathf.Min(MetabolismMax, e.Metabolism + MetabolismRegenPerSecond * dt);
            }

            if (e.Strain > 0f)
            {
                e.Strain = Mathf.Max(0f, e.Strain - StrainDecayPerSecond * dt);
            }

            if (e.Overloaded && e.Strain <= StrainClearThreshold)
            {
                SetOverloaded(e, false);
            }
        }
    }
}
