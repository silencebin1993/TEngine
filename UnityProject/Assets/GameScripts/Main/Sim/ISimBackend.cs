namespace BinGames.Sim
{
    /// <summary>
    /// 模拟后端抽象。
    ///
    /// 当前唯一实现是 <see cref="SimWorld"/>（Burst + Jobs + SoA NativeArray）。
    ///
    /// 存在这层抽象的原因见 DesignDocs/Game_Framework_Design.md §2.2：
    /// 本工程是 Built-in RP，官方 Entities Graphics 不可用，所以现在不用 ECS。
    /// 若将来迁移到 URP，可新增 EntitiesSimBackend 实现本接口替换后端，
    /// 上层玩法代码零改动。这也是"为多阶段适配"的一部分。
    /// </summary>
    public interface ISimBackend
    {
        bool IsCreated { get; }

        /// <summary>当前由玩家控制的稳定实体身份；无控制实体时为 <see cref="SimEntityId.None"/>。</summary>
        SimEntityId ControlledUnitId { get; }

        void Initialize(SimConfig cfg);

        /// <summary>加载行为原型表。可在运行期重载（换阶段时）。</summary>
        void SetArchetypes(BehaviorArchetype[] archetypes);

        /// <summary>加载静态障碍布局（story-009）。数量超过 <see cref="SimConst.MaxObstacles"/> 时截断。</summary>
        void SetObstacles(ObstacleSpec[] obstacles);

        /// <summary>推进一帧。命令缓冲会在应用后被清空。</summary>
        void Step(float dt, ref SimCommandBuffer cmds);

        /// <summary>取本帧只读快照。只在 Step 之后有效。</summary>
        SimSnapshot GetSnapshot();

        /// <summary>查询实体的控制状态；死亡实体在槽位复用前仍可查询，但 IsAlive 为 false。</summary>
        bool TryGetUnitControlState(SimEntityId entityId, out SimUnitControlState state);

        /// <summary>
        /// 查询当前受控实体信号范围内的其它存活友军。逐单位筛选在 AOT 内核完成，
        /// 返回值按距离、稳定实体 ID 排序，不向热更层暴露 NativeArray。
        /// </summary>
        SimControlCandidate[] GetControlCandidates(float maxDistance);

        /// <summary>把玩家控制权切到一个存活友军。失败不改变当前状态。</summary>
        ControlSwitchResult TrySwitchControlledUnit(SimEntityId entityId);

        void Dispose();
    }
}
