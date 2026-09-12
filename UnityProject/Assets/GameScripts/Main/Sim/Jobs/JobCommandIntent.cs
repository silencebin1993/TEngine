using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 把持久的 <see cref="UnitCommand"/> 编译成本帧的 <see cref="UnitIntent"/>（M2-02）。
    ///
    /// 为什么必须每帧重算：<c>SimWorld.PrepareUnitIntents</c> 每帧都把全体意图重置成 Idle，
    /// 所以"下令时写一次意图"下一帧就没了。命令是状态，意图是该状态在这一帧的投影。
    ///
    /// 只处理 <see cref="IntentSource.Commanded"/> 槽位，与只处理 AI 槽位的
    /// <see cref="JobAIIntent"/> 互不相交——两者都写 Intents，靠槽位归属而非执行顺序保证不打架。
    ///
    /// 命令**只产出移动意图**，不接管战斗：攻击结算走既有的距离判定路径，
    /// 这样"命令单位不会打架"这类问题从一开始就不存在。
    /// </summary>
    [BurstCompile]
    public struct JobCommandIntent : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<SimEntityId> EntityId;
        [ReadOnly] public NativeArray<uint> Status;
        /// <summary>撤退时找最近敌人用；其余命令不读它。</summary>
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;
        public float InvCellSize;

        /// <summary>命令完成时就地清空并把单位交还 AI，所以这两个是可写的。
        /// 每个 i 只写自己的槽位，符合 IJobParallelFor 的安全约定。</summary>
        public NativeArray<UnitCommand> Commands;
        public NativeArray<byte> IntentSource;

        [NativeDisableParallelForRestriction]
        public NativeArray<UnitIntent> Intents;

        public int Count;
        /// <summary>撤退时的威胁感知半径。超出这个距离的敌人不影响撤离方向。</summary>
        public float RetreatThreatRange;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0 ||
                IntentSource[i] != (byte)BinGames.Sim.IntentSource.Commanded)
            {
                return;
            }

            UnitCommand cmd = Commands[i];
            if (cmd.Kind == UnitCommandKind.None)
            {
                ReleaseToAi(i);
                return;
            }

            // 麻痹：命令保留（解除后继续执行），但这一帧动不了。
            if ((Status[i] & (uint)SimStatus.Stunned) != 0u)
            {
                Intents[i] = UnitIntent.Idle(EntityId[i], BinGames.Sim.IntentSource.Commanded);
                return;
            }

            float2 pos = Position[i];
            float arrive = math.max(0.1f, cmd.ArriveRadius);
            float2 moveDir = float2.zero;

            switch (cmd.Kind)
            {
                case UnitCommandKind.Move:
                {
                    float2 delta = cmd.TargetPosition - pos;
                    if (math.lengthsq(delta) <= arrive * arrive)
                    {
                        // 到达即完成。交还 AI 而不是站在原地——站桩是 M2-04 明确要消灭的行为。
                        ReleaseToAi(i);
                        return;
                    }
                    moveDir = math.normalizesafe(delta);
                    break;
                }

                case UnitCommandKind.Attack:
                {
                    if (!TryResolveTarget(cmd.TargetEntity, out int targetIndex))
                    {
                        // 目标没了（打死了或消失了）：命令完成，交还 AI 自行找新目标。
                        ReleaseToAi(i);
                        return;
                    }
                    float2 delta = Position[targetIndex] - pos;
                    // 贴到攻击距离就停下推进，剩下的交给既有的接触/远程结算。
                    moveDir = math.lengthsq(delta) <= arrive * arrive
                        ? float2.zero
                        : math.normalizesafe(delta);
                    break;
                }

                case UnitCommandKind.Guard:
                {
                    // 守备是持久命令，**不会自己完成**——它就是"待在这别乱跑"。
                    float2 delta = cmd.TargetPosition - pos;
                    moveDir = math.lengthsq(delta) <= arrive * arrive
                        ? float2.zero
                        : math.normalizesafe(delta);
                    break;
                }

                case UnitCommandKind.Retreat:
                {
                    float2 delta = cmd.TargetPosition - pos;
                    if (math.lengthsq(delta) <= arrive * arrive)
                    {
                        ReleaseToAi(i);
                        return;
                    }

                    float2 toTarget = math.normalizesafe(delta);
                    // 撤退不是单纯跑向目标点：身后有敌人时要一边远离一边撤，
                    // 否则"撤退"会笔直穿过追兵，看起来像自杀。
                    if (TryFindNearestThreat(i, pos, out float2 threatPos))
                    {
                        float2 away = math.normalizesafe(pos - threatPos, toTarget);
                        moveDir = math.normalizesafe(toTarget + away, toTarget);
                    }
                    else
                    {
                        moveDir = toTarget;
                    }
                    break;
                }
            }

            Intents[i] = new UnitIntent
            {
                EntityId = EntityId[i],
                Source = BinGames.Sim.IntentSource.Commanded,
                MoveDir = moveDir,
                SpeedMul = 1f,
                RadiusOverride = -1f,
                AddStatus = SimStatus.None,
                RemoveStatus = SimStatus.None,
            };
        }

        private void ReleaseToAi(int i)
        {
            Commands[i] = UnitCommand.None;
            IntentSource[i] = (byte)BinGames.Sim.IntentSource.AI;
            // 本帧就写成 AI 的 Idle：JobAIIntent 已经跑过（只认当时还是 AI 的槽位），
            // 这一帧没人再给它写意图，留着 Commanded 的旧意图会让它多冲一帧。
            Intents[i] = UnitIntent.Idle(EntityId[i], BinGames.Sim.IntentSource.AI);
        }

        private bool TryResolveTarget(SimEntityId targetId, out int targetIndex)
        {
            if (targetId.IsValid)
            {
                for (int k = 0; k < Count; k++)
                {
                    if (Alive[k] != 0 && EntityId[k] == targetId)
                    {
                        targetIndex = k;
                        return true;
                    }
                }
            }

            targetIndex = SimConst.InvalidIndex;
            return false;
        }

        /// <summary>找最近的敌对单位。走空间哈希环形粗筛，不做全场线性扫。</summary>
        private bool TryFindNearestThreat(int self, float2 pos, out float2 threatPos)
        {
            threatPos = float2.zero;
            if (RetreatThreatRange <= 0f)
            {
                return false;
            }

            byte selfFaction = Faction[self];
            int ring = SpatialHash.RingFor(RetreatThreatRange, InvCellSize);
            int2 center = SpatialHash.ToCell(pos, InvCellSize);
            float bestSq = RetreatThreatRange * RetreatThreatRange;
            bool found = false;

            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int key = SpatialHash.Hash(new int2(center.x + dx, center.y + dy));
                    if (!Hash.TryGetFirstValue(key, out int other, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (other == self || other >= Count || Alive[other] == 0 ||
                            !IsHostileTo(selfFaction, Faction[other]))
                        {
                            continue;
                        }
                        float d = math.distancesq(pos, Position[other]);
                        if (d < bestSq)
                        {
                            bestSq = d;
                            threatPos = Position[other];
                            found = true;
                        }
                    }
                    while (Hash.TryGetNextValue(out other, ref it));
                }
            }

            return found;
        }

        private static bool IsHostileTo(byte selfFaction, byte otherFaction)
        {
            bool selfFriendly = selfFaction == (byte)SimFaction.Player ||
                                selfFaction == (byte)SimFaction.PlayerMinion;
            bool otherFriendly = otherFaction == (byte)SimFaction.Player ||
                                 otherFaction == (byte)SimFaction.PlayerMinion;
            bool otherNeutral = otherFaction == (byte)SimFaction.Neutral ||
                                otherFaction == (byte)SimFaction.Pickup ||
                                otherFaction == (byte)SimFaction.None;
            return selfFriendly && !otherFriendly && !otherNeutral;
        }
    }
}
