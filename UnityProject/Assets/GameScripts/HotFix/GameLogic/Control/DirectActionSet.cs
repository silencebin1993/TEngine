using BinGames.Sim;

namespace GameLogic.Control
{
    /// <summary>
    /// 一个动作槽为什么不能用。**"能不能按"与"为什么不能按"必须分开**——
    /// 只给一个 bool 的话，UI 只能显示"灰的"，玩家读不出是"这具身体没长这东西"
    /// 还是"长了但坏了"还是"现在没有可交互的目标"，三件事的处置方式完全不同。
    /// </summary>
    public enum DirectActionAvailability : byte
    {
        /// <summary>可释放。</summary>
        Available = 0,
        /// <summary>这具身体的装配里就没有器官占这个槽。</summary>
        NoOrgan = 1,
        /// <summary>有器官占槽，但它失能了（损坏 / 被摘除 / 被压制）。</summary>
        OrganDisabled = 2,
        /// <summary>器官在，但它没有可释放的内核动作（纯被动 / 已退役 / 目录里查无此 id）。</summary>
        NoKernelAction = 3,
        /// <summary>交互槽专用：器官在也没坏，但场上没有可交互的目标。</summary>
        NoInteractTarget = 4,
        /// <summary>没有有效受控实体（意识无处可去 / 模拟未运行）。</summary>
        NoControlledUnit = 5,
        /// <summary>移动槽不走释放入口——它是每帧意图，不是一次性动作。</summary>
        NotAReleaseAction = 6,
        /// <summary>玩家本体委托路径专用：器官都在，但被委托的技能槽此刻没就绪（冷却中 / 槽位不存在）。</summary>
        NotReady = 7,

        /// <summary>M2-03c：这个动作槽在这具身体上还在冷却。</summary>
        Cooling = 8,

        /// <summary>M2-03c：这具身体过载债越过阈值，处于过载态，一切释放暂停。</summary>
        Overloaded = 9,

        /// <summary>M2-03c：这具身体的代谢资源不够付这一次释放。</summary>
        NotEnoughMetabolism = 10,
    }

    /// <summary>动作集里的一个槽。</summary>
    public struct DirectActionSlot
    {
        public LoadoutAction Action;

        /// <summary>占这个槽的器官 id。空 = 没有器官占槽。</summary>
        public string OrganId;

        /// <summary>装配里有器官占这个槽——**含失能的**。用来区分"没长"和"长了但坏了"。</summary>
        public bool Bound;

        public DirectActionAvailability Availability;

        /// <summary>该器官释放时落到内核的请求形状。</summary>
        public OrganKernelAction Kernel;

        public bool Available => Availability == DirectActionAvailability.Available;
    }

    /// <summary>
    /// 当前受控实体**编译出来的动作集**（M2-03b）。
    ///
    /// ── 为什么是"编译"而不是"每帧查" ──
    /// 它只在控制权变更时重建一次（见 <see cref="DirectControlActions"/>）。
    /// 每帧对受控实体调 <c>UnitLoadoutRegistry.Get</c> 再逐槽解析器官，在冷启动那一帧
    /// 是 O(单位数 × 查询数)（M2-03a 契约 §10 已经点名提醒过这一点），
    /// 而热更层每帧必须与场上单位数无关（仓规架构红线第 4 条）。
    /// 事件驱动重建把它压成"每次切换一次"。
    ///
    /// ── 它是给查询/显示用的，不是释放时的判据 ──
    /// 释放一律走 <see cref="DirectControlActions.TryRelease"/>，那里会**重新**读一次实时装配。
    /// 理由见该方法注释：只信这份缓存的话，"装配变了但还没切过控制权"的窗口里会按出不存在的动作。
    /// </summary>
    public sealed class DirectActionSet
    {
        /// <summary><see cref="LoadoutAction"/> 的槽位数。</summary>
        public const int SlotCount = 4;

        private readonly DirectActionSlot[] _slots = new DirectActionSlot[SlotCount];

        /// <summary>这份动作集属于哪个实体。<see cref="SimEntityId.None"/> = 空装配（无受控实体）。</summary>
        public SimEntityId EntityId { get; private set; } = SimEntityId.None;

        public UnitLoadoutOrigin Origin { get; private set; } = UnitLoadoutOrigin.Unregistered;

        /// <summary>**可释放**动作的掩码（失能器官不贡献；移动位恒置）。
        /// 注意它与 <c>UnitLoadout.ActionMask</c> 不一定相等：那边只看失能，这边还要求器官真有内核动作。</summary>
        public int ActionMask { get; private set; } = UnitLoadout.MoveActionMask;

        /// <summary>重建次数。验收用：控制权变更时它必须涨。</summary>
        public int BuildVersion { get; private set; }

        /// <summary>装配里一件器官都没有（只有移动）。</summary>
        public bool IsEmptyLoadout
        {
            get
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Bound)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public DirectActionSet()
        {
            ResetSlots();
        }

        public DirectActionSlot GetSlot(LoadoutAction action)
        {
            int i = (int)action;
            return i >= 0 && i < _slots.Length ? _slots[i] : default;
        }

        /// <summary>
        /// 缓存视角下该动作是否可释放。**不是**释放判据，见类注释。
        ///
        /// M2-03c 补充：冷却 / 代谢 / 过载债**刻意不进这份缓存**。它们是逐秒变化的实时量，
        /// 塞进来就意味着这份"只在控制权变更时重建一次"的缓存必须改成每帧重建——
        /// 那正是类注释里写明不能做的事。要显示这三个量，直接读
        /// <c>DirectControlActions.ControlledVitals</c>（O(1) 快照）。
        /// </summary>
        public bool CanRelease(LoadoutAction action)
        {
            return action != LoadoutAction.Move && GetSlot(action).Available;
        }

        public string OrganIdOf(LoadoutAction action) => GetSlot(action).OrganId;

        public DirectActionAvailability AvailabilityOf(LoadoutAction action) => GetSlot(action).Availability;

        /// <summary>
        /// 按一份装配整份重建。<paramref name="loadout"/> 为 null 或空装配时得到"只有移动"的动作集，
        /// 不抛、不留上一具身体的残留——切到一个没有装配的实体上时，动作条必须当场空掉，
        /// 而不是继续显示前一具身体的按钮。
        /// </summary>
        internal void Rebuild(SimEntityId entityId, UnitLoadout loadout, bool interactTargetsAvailable)
        {
            EntityId = entityId;
            Origin = loadout?.Origin ?? UnitLoadoutOrigin.Unregistered;
            ResetSlots();

            int mask = UnitLoadout.MoveActionMask;
            if (loadout != null)
            {
                for (int i = 1; i < _slots.Length; i++)
                {
                    var action = (LoadoutAction)i;
                    DirectActionSlot slot = _slots[i];
                    slot.Bound = loadout.HasOrganInSlot(action);

                    if (!slot.Bound)
                    {
                        slot.Availability = DirectActionAvailability.NoOrgan;
                    }
                    else if (!loadout.TryGetOrgan(action, out UnitLoadoutOrgan organ))
                    {
                        // 槽里有器官但 TryGetOrgan 拿不到 —— 只可能是全部失能。
                        slot.Availability = DirectActionAvailability.OrganDisabled;
                    }
                    else
                    {
                        slot.OrganId = organ.OrganId;
                        slot.Kernel = OrganKernelActionTable.Resolve(organ.OrganId);
                        // 交互目标先判：它说的是"世界里没东西可交互"，比"这件器官没有释放形态"
                        // 更贴近玩家看到的现象，而且必须与 TryRelease 的判定顺序一致——
                        // 两处给出不同原因的话，UI 上写的和实际拒绝的理由会对不上。
                        if (action == LoadoutAction.Interact && !interactTargetsAvailable)
                        {
                            slot.Availability = DirectActionAvailability.NoInteractTarget;
                        }
                        else if (!slot.Kernel.IsValid)
                        {
                            slot.Availability = DirectActionAvailability.NoKernelAction;
                        }
                        else
                        {
                            slot.Availability = DirectActionAvailability.Available;
                            mask |= 1 << i;
                        }
                    }

                    _slots[i] = slot;
                }
            }

            ActionMask = mask;
            BuildVersion++;
        }

        /// <summary>清成"没有受控实体"的状态。与空装配的区别在 <see cref="EntityId"/> 与槽位原因。</summary>
        internal void Clear()
        {
            EntityId = SimEntityId.None;
            Origin = UnitLoadoutOrigin.Unregistered;
            ResetSlots();
            for (int i = 1; i < _slots.Length; i++)
            {
                _slots[i].Availability = DirectActionAvailability.NoControlledUnit;
            }
            ActionMask = UnitLoadout.MoveActionMask;
            BuildVersion++;
        }

        private void ResetSlots()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new DirectActionSlot
                {
                    Action = (LoadoutAction)i,
                    OrganId = null,
                    Bound = false,
                    Kernel = OrganKernelAction.None,
                    // 移动恒可用且不占器官槽（M2-03a 契约 §6）；其余默认"没长这东西"。
                    Availability = i == (int)LoadoutAction.Move
                        ? DirectActionAvailability.Available
                        : DirectActionAvailability.NoOrgan,
                };
            }
        }
    }
}
