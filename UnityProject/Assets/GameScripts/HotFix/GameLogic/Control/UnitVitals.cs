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

        private sealed class Entry
        {
            public float Metabolism = MetabolismMax;
            public float Strain;
            public bool Overloaded;
            /// <summary>各槽的"就绪时刻"（本地时钟）。下标 = <see cref="LoadoutAction"/>。</summary>
            public readonly float[] ReadyAt = new float[DirectActionSet.SlotCount];
            public float LastSync;
        }

        private readonly Dictionary<SimEntityId, Entry> _entries = new Dictionary<SimEntityId, Entry>(8);
        private readonly List<SimEntityId> _sweepScratch = new List<SimEntityId>(8);

        private float _clock;

        /// <summary>在册条目数。验收/调试用。</summary>
        public int Count => _entries.Count;

        /// <summary>本地单调时钟（秒）。只在非暂停帧前进。</summary>
        public float Clock => _clock;

        /// <summary>
        /// 推进本地时钟。**这是本类唯一的每帧开销，纯 O(1)**：不遍历任何条目。
        /// </summary>
        /// <param name="paused">玩法暂停时不推进——暂停下刷冷却/回代谢是白送的。</param>
        public void Advance(float dt, bool paused = false)
        {
            if (paused || dt <= 0f)
            {
                return;
            }

            _clock += dt;
        }

        /// <summary>跨局清空。实体 id 只在生成它的那个 <c>SimWorld</c> 内有效，跨局一律作废
        /// （同 M2-03a 契约 §11 的理由）。</summary>
        public void Reset()
        {
            _entries.Clear();
            _sweepScratch.Clear();
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
                e.Overloaded = true;
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
                e.Overloaded = true;
            }
            else if (e.Overloaded && e.Strain <= StrainClearThreshold)
            {
                e.Overloaded = false;
            }

            return true;
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
            var made = new Entry { LastSync = _clock };
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
                e.Overloaded = false;
            }
        }
    }
}
