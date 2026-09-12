using System.Collections.Generic;
using BinGames.Sim;

namespace GameLogic.Control
{
    /// <summary>
    /// 直控动作槽（M2-03）。**这不是一张英雄技能表**——除 <see cref="Move"/> 外，
    /// 每一个动作都必须由个体身上一件真实器官占位，没有器官就没有该动作。
    ///
    /// <see cref="Move"/> 刻意不由器官提供：移动是"还活着就有"的基础能力，
    /// 若让它挂在某件器官上，那件器官一失能单位就彻底动不了，
    /// 玩家会得到一个既不能打也不能跑的空壳——那是 bug 的观感，不是玩法。
    /// 移动器官（鞭毛之类）将来只应该改移动的**参数**，不应该决定移动是否存在。
    /// </summary>
    public enum LoadoutAction : byte
    {
        /// <summary>移动。恒可用，不占器官槽。</summary>
        Move = 0,
        /// <summary>主器官动作（主要攻击/输出手段）。</summary>
        Primary = 1,
        /// <summary>功能器官动作（位移、增益、控制之类）。</summary>
        Utility = 2,
        /// <summary>交互动作（搬运、抓取、与场景物件互动）。</summary>
        Interact = 3,
    }

    /// <summary>一份装配是怎么来的。查询方据此判断"查不到"与"真的没有"。</summary>
    public enum UnitLoadoutOrigin : byte
    {
        /// <summary>该实体没有登记过装配。返回的是 <see cref="UnitLoadout.Empty"/>，不是 null。</summary>
        Unregistered = 0,
        /// <summary>玩家本体：每次查询都从注入源重新投影，不是生成时的快照。</summary>
        PlayerProjection = 1,
        /// <summary>其它友军：生成时按行为原型派生一次，之后只会被失能位改写。</summary>
        ArchetypeDerived = 2,
    }

    /// <summary>装配里的一件器官。<see cref="Disabled"/> 是**热更层状态**，
    /// 不下沉内核（内核不认识"器官"，它只认识 archetype 与弹体参数）。</summary>
    public struct UnitLoadoutOrgan
    {
        /// <summary>器官定义 id（如 <c>org_cilia</c>）。空串/ null 的条目会被装配拒收。</summary>
        public string OrganId;

        /// <summary>该器官占用的动作槽。不允许是 <see cref="LoadoutAction.Move"/>。</summary>
        public LoadoutAction Action;

        /// <summary>失能（损坏/被摘除/被压制）。失能的器官仍留在装配里可见，
        /// 但不再向 <see cref="UnitLoadout.ActionMask"/> 贡献动作。
        /// **本段不定义"什么情况下会失能"**——那属于后续的伤害定位与手术提取。</summary>
        public bool Disabled;

        public UnitLoadoutOrgan(string organId, LoadoutAction action, bool disabled = false)
        {
            OrganId = organId;
            Action = action;
            Disabled = disabled;
        }
    }

    /// <summary>
    /// 一个实体当下的装配，以及由它推导出的可用动作集。
    ///
    /// 实例身份是**稳定的**：玩家本体那一份每次刷新都在原对象上原地重建，
    /// 所以调用方持有引用跨帧是安全的（拿到的永远是最新值），
    /// 不会出现"缓存了一份快照，装配早就变了还在按旧的算"。
    /// </summary>
    public sealed class UnitLoadout
    {
        /// <summary>只含移动位的动作掩码。任何存活单位的 <see cref="ActionMask"/> 都至少是它。</summary>
        public const int MoveActionMask = 1 << (int)LoadoutAction.Move;

        /// <summary>明确的空装配：只有移动，没有任何器官。
        /// 未登记实体返回它而不是 null，查询方因此不需要判空分支
        /// （口径与 <c>Progression/ControlPersistence</c> 的 Reject-to-Safe 一致）。</summary>
        public static readonly UnitLoadout Empty = CreateFrozenEmpty();

        private readonly List<UnitLoadoutOrgan> _organs = new List<UnitLoadoutOrgan>(4);
        /// <summary><see cref="Empty"/> 是全局共享的只读实例，所有写入路径对它一律 no-op。</summary>
        private readonly bool _frozen;

        public SimEntityId EntityId { get; private set; }
        public UnitLoadoutOrigin Origin { get; private set; }

        /// <summary>按 <see cref="LoadoutAction"/> 取位的掩码，只统计**未失能**的器官；移动位恒置。</summary>
        public int ActionMask { get; private set; } = MoveActionMask;

        public IReadOnlyList<UnitLoadoutOrgan> Organs => _organs;
        public int OrganCount => _organs.Count;

        internal UnitLoadout(SimEntityId entityId, UnitLoadoutOrigin origin, bool frozen = false)
        {
            EntityId = entityId;
            Origin = origin;
            _frozen = frozen;
        }

        private static UnitLoadout CreateFrozenEmpty()
        {
            return new UnitLoadout(SimEntityId.None, UnitLoadoutOrigin.Unregistered, frozen: true);
        }

        public bool HasAction(LoadoutAction action) => (ActionMask & (1 << (int)action)) != 0;

        /// <summary>取占据该动作槽的第一件**未失能**器官。移动槽永远返回 false——它不由器官提供。</summary>
        public bool TryGetOrgan(LoadoutAction action, out UnitLoadoutOrgan organ)
        {
            for (int i = 0; i < _organs.Count; i++)
            {
                UnitLoadoutOrgan candidate = _organs[i];
                if (candidate.Action == action && !candidate.Disabled)
                {
                    organ = candidate;
                    return true;
                }
            }

            organ = default;
            return false;
        }

        /// <summary>
        /// 该动作槽上**有没有器官**——失能的也算（M2-03b 新增）。
        ///
        /// 与 <see cref="HasAction"/> 的区别正是本方法存在的理由：<c>HasAction</c> 回答"现在能不能按"，
        /// 这里回答"这具身体长没长这东西"。两者合起来才能把"没长"和"长了但坏了"分开，
        /// 而这两种情况在 UI 上必须给出完全不同的说法。
        /// </summary>
        public bool HasOrganInSlot(LoadoutAction action)
        {
            for (int i = 0; i < _organs.Count; i++)
            {
                if (_organs[i].Action == action)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>该 id 的器官是否在装配里（无论是否失能）。</summary>
        public bool ContainsOrgan(string organId)
        {
            return IndexOfOrgan(organId) >= 0;
        }

        /// <summary>置/清失能位。返回是否真的发生了改变（找不到该器官或值没变都返回 false）。</summary>
        internal bool SetOrganDisabled(string organId, bool disabled)
        {
            if (_frozen)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < _organs.Count; i++)
            {
                UnitLoadoutOrgan organ = _organs[i];
                if (organ.OrganId != organId || organ.Disabled == disabled)
                {
                    continue;
                }

                organ.Disabled = disabled;
                _organs[i] = organ;
                changed = true;
            }

            if (changed)
            {
                RecomputeActionMask();
            }

            return changed;
        }

        /// <summary>
        /// 用新的器官列表整份重建，但**按 <see cref="UnitLoadoutOrgan.OrganId"/> 保留已置的失能位**。
        ///
        /// 为什么要保留：失能是热更层自己记的战损状态，而投影源（玩家的 Carrier 注册表）
        /// 根本不知道"失能"这回事，它每次都交回一份全是健康器官的列表。
        /// 不保留的话，玩家随便装卸一件东西就会把身上所有损坏器官一键治好。
        ///
        /// 已知边界：同一件器官被卸下再装回，失能位会丢——那时它已经是"另一份装配"了，
        /// 沿用旧战损反而更难解释。
        /// </summary>
        internal void Rebuild(List<UnitLoadoutOrgan> source)
        {
            if (_frozen)
            {
                return;
            }

            // 先把旧的失能 id 抄出来（数量是 O(器官数)，与场上单位数无关）。
            _disabledScratch.Clear();
            for (int i = 0; i < _organs.Count; i++)
            {
                if (_organs[i].Disabled && !string.IsNullOrEmpty(_organs[i].OrganId))
                {
                    _disabledScratch.Add(_organs[i].OrganId);
                }
            }

            _organs.Clear();
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    UnitLoadoutOrgan organ = source[i];
                    if (string.IsNullOrEmpty(organ.OrganId) || organ.Action == LoadoutAction.Move)
                    {
                        // 空 id 与"占移动槽的器官"都不合法：前者无法被失能入口寻址，
                        // 后者会让一次损伤把单位钉死在原地。静默丢弃，不抛。
                        continue;
                    }

                    if (!organ.Disabled && _disabledScratch.Contains(organ.OrganId))
                    {
                        organ.Disabled = true;
                    }

                    _organs.Add(organ);
                }
            }

            RecomputeActionMask();
        }

        internal void Reset(SimEntityId entityId, UnitLoadoutOrigin origin)
        {
            if (_frozen)
            {
                return;
            }

            EntityId = entityId;
            Origin = origin;
            _organs.Clear();
            ActionMask = MoveActionMask;
        }

        private int IndexOfOrgan(string organId)
        {
            for (int i = 0; i < _organs.Count; i++)
            {
                if (_organs[i].OrganId == organId)
                {
                    return i;
                }
            }

            return -1;
        }

        private void RecomputeActionMask()
        {
            int mask = MoveActionMask;
            for (int i = 0; i < _organs.Count; i++)
            {
                if (!_organs[i].Disabled)
                {
                    mask |= 1 << (int)_organs[i].Action;
                }
            }

            ActionMask = mask;
        }

        /// <summary>失能 id 暂存。实例级而非静态，避免两份装配同时重建时互相踩。</summary>
        private readonly List<string> _disabledScratch = new List<string>(4);
    }
}
