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
    /// 数组按索引对齐，长度都是 Count。索引 0 恒为玩家（<see cref="SimConst.PlayerIndex"/>）。
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
        /// （见 <see cref="JobDamage.PlayerDamageOut"/>），这样所有打到玩家身上的东西
        /// 都统一经过同一条结算路径——过 DamageTaken 减伤、记账、发 PlayerHurtSignal。
        /// </summary>
        public float PlayerDamageTaken;

        public float2 PlayerPosition;
        public float PlayerHealth;
        public float PlayerRadius;

        public bool IsAlive(int i) => i >= 0 && i < Count && Alive[i] != 0;

        public bool HasStatus(int i, SimStatus s)
        {
            return i >= 0 && i < Count && (Status[i] & (uint)s) != 0u;
        }

        public SimFaction FactionOf(int i)
        {
            return i >= 0 && i < Count ? (SimFaction)Faction[i] : SimFaction.None;
        }

        /// <summary>存活的敌对单位数量。UI 显示"当前敌人规模"用。</summary>
        public int CountHostiles()
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
