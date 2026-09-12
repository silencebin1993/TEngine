using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// AOT 内核 → 热更层的只读视图。
    ///
    /// 只在 <see cref="SimWorld.Step"/> 完成后有效，下一次 Step 即失效。
    /// 热更层只读不写；写入一律走 <see cref="SimCommandBuffer"/>。
    ///
    /// 数组按索引对齐，长度都是 Count。索引只作瞬时地址；跨帧身份必须使用 <see cref="EntityId"/>。
    /// </summary>
    public struct SimSnapshot
    {
        public int Count;

        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<uint> Status;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeArray<int> LogicId;
        [ReadOnly] public NativeArray<int> VisualId;
        [ReadOnly] public NativeArray<SimEntityId> EntityId;
        [ReadOnly] public NativeArray<byte> IntentSource;
        [ReadOnly] public NativeArray<UnitIntent> FinalIntent;
        /// <summary>召唤血统代数（见 <see cref="SpawnRequest.Generation"/>）。
        /// 验收要能看出"第几代"，否则封顶到底生没生效只能靠数数量猜。</summary>
        [ReadOnly] public NativeArray<byte> Generation;

        /// <summary>本帧死亡事件。热更层据此结算掉落、进化能、卡牌 OnKill。</summary>
        [ReadOnly] public NativeArray<DeathEvent> Deaths;
        public int DeathCount;

        /// <summary>本帧命中事件。用于卡牌 OnHit 与打击反馈。</summary>
        [ReadOnly] public NativeArray<HitEvent> Hits;
        public int HitCount;

        /// <summary>本帧可被吞噬的候选单位索引（已按体积门槛筛过）。</summary>
        [ReadOnly] public NativeArray<int> DevourCandidates;
        public int DevourCandidateCount;

        /// <summary>
        /// combat-primitive-overhaul：本帧弹体终结事件（真实落点 + 为什么没的）。
        /// 热更层据此放留坑/命中表现——**不要再自己预测落点**，那是"看到命中了却没伤害"的根因。
        /// </summary>
        [ReadOnly] public NativeArray<ProjectileEndEvent> ProjectileEnds;
        public int ProjectileEndCount;

        /// <summary>
        /// 本帧玩家受到的伤害总量。
        ///
        /// enemy-ranged-and-parry 起**不只是接触伤害**：敌人弹体命中玩家也累加到这里
        /// （见 <see cref="JobDamage.ControlledDamageOut"/>），这样所有打到受控实体的东西
        /// 都统一经过同一条结算路径——过 DamageTaken 减伤、记账、发 PlayerHurtSignal。
        /// </summary>
        public float PlayerDamageTaken;

        public float2 PlayerPosition;
        public float PlayerHealth;
        public float PlayerRadius;

        /// <summary>当前玩家控制实体的稳定身份；不保证它位于固定槽位。</summary>
        public SimEntityId ControlledUnitId;
        /// <summary>构建快照时解析出的瞬时槽位；无有效控制实体时为 InvalidIndex。</summary>
        public int ControlledUnitIndex;

        // 这些查询方法一律标 readonly：快照常常是通过属性（如 SimBridge.Snapshot）取到的，
        // 而对属性返回值调用非 readonly 的结构体方法会报 CS1612，逼调用方先抄一份局部变量。
        // 它们本来就不改状态，标上之后这类绕路整类消失。
        public readonly bool IsAlive(int i) => i >= 0 && i < Count && Alive[i] != 0;

        public readonly bool HasStatus(int i, SimStatus s)
        {
            return i >= 0 && i < Count && (Status[i] & (uint)s) != 0u;
        }

        public readonly SimFaction FactionOf(int i)
        {
            return i >= 0 && i < Count ? (SimFaction)Faction[i] : SimFaction.None;
        }

        public readonly IntentSource IntentSourceOf(int i)
        {
            return i >= 0 && i < Count
                ? (BinGames.Sim.IntentSource)IntentSource[i]
                : BinGames.Sim.IntentSource.AI;
        }

        public readonly bool TryResolve(SimEntityId entityId, out int unitIndex)
        {
            if (entityId.IsValid)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (Alive[i] != 0 && EntityId[i] == entityId)
                    {
                        unitIndex = i;
                        return true;
                    }
                }
            }

            unitIndex = SimConst.InvalidIndex;
            return false;
        }

        /// <summary>O(1) 解析本快照的受控实体，并再次校验稳定 ID，防止默认值或过期槽位串体。</summary>
        public readonly bool TryResolveControlledUnit(out int unitIndex)
        {
            int index = ControlledUnitIndex;
            if (index >= 0 && index < Count && Alive[index] != 0 &&
                ControlledUnitId.IsValid && EntityId[index] == ControlledUnitId)
            {
                unitIndex = index;
                return true;
            }
            unitIndex = SimConst.InvalidIndex;
            return false;
        }

        /// <summary>存活的敌对单位数量。UI 显示"当前敌人规模"用。</summary>
        public readonly int CountHostiles()
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] != 0 && Faction[i] == (byte)SimFaction.Hostile)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
