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

        /// <summary>本帧玩家受到的接触伤害总量。</summary>
        public float PlayerContactDamage;

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
