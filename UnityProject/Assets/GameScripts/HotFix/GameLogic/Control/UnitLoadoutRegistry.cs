using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;

namespace GameLogic.Control
{
    /// <summary>
    /// 装配按实体归属（M2-03a）。回答一个在此之前根本无从回答的问题：
    /// **"我现在接管的这个单位，身上装着什么？"**
    ///
    /// 在这之前，装配（<c>CarrierRegistry</c> / <c>BagInventory</c> / <c>SlotGrid</c>）是全局单例，
    /// 唯一持有者是 <c>UI/Battle/MetabolicSlicePanel</c>，它自陈"本面板持有的 Grid/Bag 就是玩家状态本身"。
    /// 那套东西只描述得了"玩家"，描述不了"某一个实体"。本注册表把装配挂到实体上。
    ///
    /// ── 键为什么是 <see cref="SimEntityId"/> ──
    /// 不用槽位索引：<c>SimWorld.TryResolveUnit</c> 的注释已写明索引不得跨帧缓存，槽位会被复用。
    /// 不用 <c>LogicId</c>：它是热更层自己发的号，只保证同一局内可比，且死亡回收后没有失效语义。
    /// <see cref="SimEntityId"/> 在槽位复用时会重新分配，所以 M2-02 在 <c>ReleaseSlot</c> 遇到的
    /// "新单位继承上一个占用者的数据"那类 bug，在这里**结构上不成立**——新单位拿到的是新 id，
    /// 查不到旧条目。
    ///
    /// ── 但仍然必须清理 ──
    /// 结构上不串体，不等于条目会自己消失。死者的条目会一直躺着。因此：
    /// 1. 查询路径上顺手验活，解析不到就当场删（见 <see cref="Get"/>）；
    /// 2. 登记路径上按容量阈值触发一次全表清扫（见 <see cref="SweepThreshold"/>）。
    /// **刻意没有逐帧清扫**：热更层每帧不得出现与场上单位数相关的循环（仓规架构红线第 4 条）。
    ///
    /// ── 规模 ──
    /// 只登记友方可指挥单位，与敌人规模无关。当前是三名（玩家本体 + 两名友军）。
    /// </summary>
    public sealed class UnitLoadoutRegistry
    {
        /// <summary>条目数达到该阈值时，下一次登记顺手做一次全表清扫。
        /// 取值只需远大于正常同时在场的可指挥单位数即可——它是"长跑不涨"的兜底，不是常规路径。</summary>
        public const int SweepThreshold = 64;

        /// <summary>挂起项最多重试多少次解析。生成后若干帧内实体必然落地；
        /// 超出说明这个单位压根没生出来（或出生即死），继续留着只会让
        /// <see cref="ResolvePending"/> 每帧白扫一遍快照——那正是本类要避免的逐帧 O(N)。</summary>
        public const int MaxResolveAttempts = 120;

        private sealed class Entry
        {
            public UnitLoadout Loadout;

            /// <summary>上一次解析到的槽位。**这不是"缓存索引"那个坑**：
            /// 每次使用前都先拿快照核对该槽位上的实体 id 还是不是同一个，
            /// 对不上就回落到内核重新解析。验证在先、使用在后，因此不可能指错单位，
            /// 又能把常见情况压到 O(1)（内核的 id→index 是线性扫描）。</summary>
            public int CachedUnitIndex;
        }

        private struct PendingSpawn
        {
            public int LogicId;
            public int ArchetypeId;
            /// <summary>M3-05：非 null 时走 <see cref="UnitLoadoutOrigin.TemplateDerived"/> 路径，
            /// <see cref="ArchetypeId"/> 字段本次不使用。两条延迟登记路径共用同一张挂起表/同一套
            /// 解析节流（<see cref="MaxResolveAttempts"/>），不再新开一张平行表。</summary>
            public List<UnitLoadoutOrgan> ExplicitOrgans;
            public int Attempts;
        }

        private readonly Dictionary<SimEntityId, Entry> _entries = new Dictionary<SimEntityId, Entry>(8);
        private readonly List<PendingSpawn> _pending = new List<PendingSpawn>(4);
        private readonly List<UnitLoadoutOrgan> _collectScratch = new List<UnitLoadoutOrgan>(8);
        private readonly List<SimEntityId> _sweepScratch = new List<SimEntityId>(8);

        private SimBridge _sim;

        /// <summary>玩家本体装配的取值方式。见 <see cref="IPlayerLoadoutSource"/> 里"为什么可注入"。</summary>
        public IPlayerLoadoutSource PlayerSource { get; private set; }

        /// <summary>已登记条目数（含尚未被验活剔除的死者）。</summary>
        public int Count => _entries.Count;

        /// <summary>尚未解析出实体 id 的挂起登记数。</summary>
        public int PendingCount => _pending.Count;

        public void Bind(SimBridge sim, IPlayerLoadoutSource playerSource)
        {
            _sim = sim;
            PlayerSource = playerSource;
            _entries.Clear();
            _pending.Clear();
        }

        public void Unbind()
        {
            _sim = null;
            PlayerSource = null;
            _entries.Clear();
            _pending.Clear();
        }

        /// <summary>替换玩家装配投影源。生产路径不需要它；给 Edit 模式回归与将来的多人本体留口子。</summary>
        public void SetPlayerSource(IPlayerLoadoutSource playerSource)
        {
            PlayerSource = playerSource;
        }

        // ── 登记 ────────────────────────────────────────────

        /// <summary>把某实体登记为"玩家本体"：它的装配每次查询都从 <see cref="PlayerSource"/> 重新投影。</summary>
        public UnitLoadout RegisterPlayerBody(SimEntityId entityId)
        {
            return Register(entityId, UnitLoadoutOrigin.PlayerProjection, 0);
        }

        /// <summary>按行为原型派生一份装配并登记。原型未登记时得到只有移动的空装配。</summary>
        public UnitLoadout RegisterArchetype(SimEntityId entityId, int archetypeId)
        {
            return Register(entityId, UnitLoadoutOrigin.ArchetypeDerived, archetypeId);
        }

        /// <summary>
        /// 延迟登记：<c>SimBridge.Spawn</c> 只是入队，实体 id 要等下一次 Step 才存在，
        /// 生成点当场拿不到键。这里先按 <c>LogicId</c> 记账，由
        /// <see cref="ResolvePending"/> 在实体落地后补登记。
        /// </summary>
        public void RegisterArchetypePending(int logicId, int archetypeId)
        {
            if (logicId == 0)
            {
                return;
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].LogicId == logicId)
                {
                    return;
                }
            }

            _pending.Add(new PendingSpawn { LogicId = logicId, ArchetypeId = archetypeId, ExplicitOrgans = null, Attempts = 0 });
        }

        /// <summary>M3-05：萌生腔新生个体的延迟登记——装配是显式的一份器官列表（来自表型模板版本），
        /// 不是按 archetypeId 查表派生。</summary>
        public void RegisterExplicitPending(int logicId, IReadOnlyList<UnitLoadoutOrgan> organs)
        {
            if (logicId == 0)
            {
                return;
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].LogicId == logicId)
                {
                    return;
                }
            }

            _pending.Add(new PendingSpawn { LogicId = logicId, ArchetypeId = 0, ExplicitOrgans = new List<UnitLoadoutOrgan>(organs), Attempts = 0 });
        }

        /// <summary>把一份显式器官列表登记为某实体的装配（M3-05：萌生腔新生个体）。</summary>
        public UnitLoadout RegisterExplicit(SimEntityId entityId, IReadOnlyList<UnitLoadoutOrgan> organs)
        {
            return Register(entityId, UnitLoadoutOrigin.TemplateDerived, 0, organs);
        }

        /// <summary>
        /// 把已落地的挂起项补登记，返回本次解析成功的条数。
        ///
        /// **挂起表为空时立即返回 0，一行都不扫**——这就是它可以被放在每帧路径上的原因：
        /// 稳态下（生成完成后）它的代价是一次 <c>Count == 0</c> 判断，与场上单位数无关。
        /// 只有生成后的那一两帧才会出现 O(单位数 × 挂起数) 的扫描，且有
        /// <see cref="MaxResolveAttempts"/> 兜底，绝不会长期挂着白扫。
        /// </summary>
        public int ResolvePending(in SimSnapshot snapshot)
        {
            if (_pending.Count == 0)
            {
                return 0;
            }

            int resolved = 0;
            for (int p = _pending.Count - 1; p >= 0; p--)
            {
                PendingSpawn pending = _pending[p];
                SimEntityId found = SimEntityId.None;

                if (snapshot.LogicId.IsCreated && snapshot.EntityId.IsCreated)
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (snapshot.Alive[i] != 0 && snapshot.LogicId[i] == pending.LogicId)
                        {
                            found = snapshot.EntityId[i];
                            break;
                        }
                    }
                }

                if (found.IsValid)
                {
                    if (pending.ExplicitOrgans != null)
                    {
                        RegisterExplicit(found, pending.ExplicitOrgans);
                    }
                    else
                    {
                        RegisterArchetype(found, pending.ArchetypeId);
                    }
                    _pending.RemoveAt(p);
                    resolved++;
                    continue;
                }

                pending.Attempts++;
                if (pending.Attempts >= MaxResolveAttempts)
                {
                    _pending.RemoveAt(p);
                }
                else
                {
                    _pending[p] = pending;
                }
            }

            return resolved;
        }

        public bool Unregister(SimEntityId entityId)
        {
            return _entries.Remove(entityId);
        }

        public void Clear()
        {
            _entries.Clear();
            _pending.Clear();
        }

        // ── 查询 ────────────────────────────────────────────

        /// <summary>该实体当前是否有登记条目（不触发验活剔除）。</summary>
        public bool IsRegistered(SimEntityId entityId)
        {
            return entityId.IsValid && _entries.ContainsKey(entityId);
        }

        /// <summary>
        /// 取该实体当下的装配。**永不返回 null，也永不抛**：
        /// 未登记 / 无效 id / 实体已死一律返回 <see cref="UnitLoadout.Empty"/>（只有移动）。
        ///
        /// 玩家本体走实时投影而不是生成时快照：玩家在战斗中随时会选卡装上新器官，
        /// 快照会让"装配"与"实际身上有什么"当场脱节，而 M2-03 的全部立论就是二者必须一致。
        /// </summary>
        public UnitLoadout Get(SimEntityId entityId)
        {
            if (!entityId.IsValid || !_entries.TryGetValue(entityId, out Entry entry))
            {
                return UnitLoadout.Empty;
            }

            if (!IsEntityLive(entityId, entry))
            {
                _entries.Remove(entityId);
                return UnitLoadout.Empty;
            }

            if (entry.Loadout.Origin == UnitLoadoutOrigin.PlayerProjection)
            {
                RefreshPlayerProjection(entry);
            }

            return entry.Loadout;
        }

        /// <summary>M3-07：装临时移植器官（<see cref="GameLogic.MetabolicSlice.WildOrgan.WildOrganRegistry"/>
        /// 的消费入口）。未登记实体 no-op 返回 false——Reject-to-Safe，调用方不应该给一个查不到装配的
        /// 实体装临时器官。冲突判断（动作槽是否已被占用）由调用方在此之前用 <see cref="Get"/> +
        /// <see cref="UnitLoadout.HasOrganInSlot"/> 做完，本方法只负责机制上的装配写入。</summary>
        public bool SetTemporaryOrgan(SimEntityId entityId, UnitLoadoutOrgan organ)
        {
            if (!entityId.IsValid || !_entries.TryGetValue(entityId, out Entry entry))
            {
                return false;
            }
            return entry.Loadout.SetTemporaryOrgan(organ);
        }

        /// <summary>M3-07：卸下临时移植器官（显式卸下或身体死亡清理）。</summary>
        public bool ClearTemporaryOrgan(SimEntityId entityId)
        {
            if (!entityId.IsValid || !_entries.TryGetValue(entityId, out Entry entry))
            {
                return false;
            }
            return entry.Loadout.ClearTemporaryOrgan();
        }

        /// <summary>置/清某件器官的失能位。返回是否真的发生了改变。
        /// 本段只提供这一个入口 —— "什么情况下器官会失能"由后续里程碑（伤害定位 / 手术提取）定义。</summary>
        public bool SetOrganDisabled(SimEntityId entityId, string organId, bool disabled)
        {
            if (string.IsNullOrEmpty(organId))
            {
                return false;
            }

            UnitLoadout loadout = Get(entityId);
            if (loadout == UnitLoadout.Empty)
            {
                return false;
            }

            return loadout.SetOrganDisabled(organId, disabled);
        }

        /// <summary>剔除全部已死实体的条目，返回剔除条数。由容量阈值触发，也可手动调用。
        /// 代价是 O(登记条目数)，**不是** O(场上单位数)。</summary>
        public int SweepDead()
        {
            if (_entries.Count == 0)
            {
                return 0;
            }

            _sweepScratch.Clear();
            foreach (KeyValuePair<SimEntityId, Entry> pair in _entries)
            {
                if (!IsEntityLive(pair.Key, pair.Value))
                {
                    _sweepScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _sweepScratch.Count; i++)
            {
                _entries.Remove(_sweepScratch[i]);
            }

            int removed = _sweepScratch.Count;
            _sweepScratch.Clear();
            return removed;
        }

        // ── 内部 ────────────────────────────────────────────

        private UnitLoadout Register(SimEntityId entityId, UnitLoadoutOrigin origin, int archetypeId, IReadOnlyList<UnitLoadoutOrgan> explicitOrgans = null)
        {
            if (!entityId.IsValid)
            {
                return UnitLoadout.Empty;
            }

            if (_entries.Count >= SweepThreshold)
            {
                SweepDead();
            }

            if (!_entries.TryGetValue(entityId, out Entry entry))
            {
                entry = new Entry
                {
                    Loadout = new UnitLoadout(entityId, origin),
                    CachedUnitIndex = SimConst.InvalidIndex,
                };
                _entries[entityId] = entry;
            }

            entry.Loadout.Reset(entityId, origin);

            if (origin == UnitLoadoutOrigin.ArchetypeDerived)
            {
                _collectScratch.Clear();
                ArchetypeLoadoutTable.Collect(archetypeId, _collectScratch);
                entry.Loadout.Rebuild(_collectScratch);
            }
            else if (origin == UnitLoadoutOrigin.TemplateDerived)
            {
                _collectScratch.Clear();
                if (explicitOrgans != null)
                {
                    _collectScratch.AddRange(explicitOrgans);
                }
                entry.Loadout.Rebuild(_collectScratch);
            }
            else
            {
                RefreshPlayerProjection(entry);
            }

            PushOrganCombatFlag(entityId, entry.Loadout);
            return entry.Loadout;
        }

        /// <summary>
        /// M2-07：把"这具身体的战斗由器官驱动"推给内核。
        ///
        /// 判据只有一条——**主武器槽上有一件解析得出内核动作的器官**。没有的话内核照旧用
        /// 行为原型数值结算，这是有意保留的降级：召唤物、自爆虫这类身上根本没装配的单位
        /// 若被迫走器官路就会彻底哑火，那比原问题更严重。
        ///
        /// 注意它只在**装配变更**时推，不逐帧同步：器官被打坏是逐帧会变的量，
        /// 但那一档不需要动这个位——内核照常抛开火机会，
        /// <c>MinionOrganCombatDriver</c> 在取器官那一步就会拿不到（<c>TryGetOrgan</c> 只返回未失能的），
        /// 于是不开火。与玩家按键时被拦下的口径一字不差，多推一个位反而是第二个会漂移的真相源。
        /// </summary>
        private void PushOrganCombatFlag(SimEntityId entityId, UnitLoadout loadout)
        {
            if (_sim == null || !entityId.IsValid)
            {
                return;
            }

            bool driven = loadout.HasOrganInSlot(MinionOrganCombatDriver.AiFireSlot) &&
                          loadout.TryGetOrgan(MinionOrganCombatDriver.AiFireSlot, out UnitLoadoutOrgan organ) &&
                          OrganKernelActionTable.Resolve(organ.OrganId).IsValid;

            _sim.SetOrganCombat(entityId, driven);
        }

        private void RefreshPlayerProjection(Entry entry)
        {
            _collectScratch.Clear();
            PlayerSource?.CollectOrgans(_collectScratch);
            entry.Loadout.Rebuild(_collectScratch);
        }

        /// <summary>
        /// 实体是否还在。未绑定 <see cref="SimBridge"/> 时无从验证，按"还在"处理
        /// （纯数据用法，例如单测只想看映射结果）。
        ///
        /// 快路：拿上次的槽位直接查快照，核对那一格的实体 id 仍是同一个。命中即 O(1)。
        /// 慢路：内核 <c>TryFindUnit</c> 是线性扫描（O(单位数)，发生在 AOT 内），
        /// 只在槽位真的变了或首次查询时走一次，之后重新缓存索引。
        /// </summary>
        private bool IsEntityLive(SimEntityId entityId, Entry entry)
        {
            if (_sim == null)
            {
                return true;
            }

            SimSnapshot snapshot = _sim.Snapshot;
            int cached = entry.CachedUnitIndex;
            if (cached >= 0 && cached < snapshot.Count &&
                snapshot.EntityId.IsCreated && snapshot.Alive.IsCreated &&
                snapshot.EntityId[cached] == entityId)
            {
                return snapshot.Alive[cached] != 0;
            }

            if (_sim.TryResolveUnitIndex(entityId, out int resolved))
            {
                entry.CachedUnitIndex = resolved;
                return true;
            }

            entry.CachedUnitIndex = SimConst.InvalidIndex;
            return false;
        }
    }
}
