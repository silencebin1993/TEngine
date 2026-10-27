using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG10-FAC-01（FG09 FGR-FAC-008 澄净专属语法）：战斗内核澄净部分的地点级包装——酸沼形状（派生量，规划层写）、投放目标（净化塔，派生量）、
    /// 单位的澄净状态（喷淋 / 枢轴 / 自爆 / 受寒 / 飞行，随快照）与只读查询。逐单位的结算全部在内核（AOT），这里只转发，O(1) 或 O(区域 / 单位) 的只读查询给自检与表现用。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>酸沼（x, y, 内半径, 外半径；内半径 0 = 实心圆）、每游戏秒扣最大耐久的比例、湿润时的倍率（冷却液冲淡）、
        /// 玩家机器的防腐涂层抗性（FG10-REG-01；0 = 没有涂层）。派生量，不进快照。</summary>
        public void SetAcidMarsh(IReadOnlyList<float4> shapes, float pctPerSecond, float wetMult, float playerResist = 0f)
        {
            if (!IsDisposed)
            {
                Kernel.SetAcidMarsh(shapes, pctPerSecond, wetMult, playerResist);
            }
        }

        /// <summary>FG10-REG-01：内核里当前生效的玩家机器防腐涂层抗性。</summary>
        public float PlayerAcidResist => IsDisposed ? 0f : Kernel.PlayerAcidResist;

        /// <summary>FG10-BOSS-01 洗礼塔：中立场地（预警脉冲圈 / 灌注液池 / 主动反应区，对双方生效）。容量满时返回 false。</summary>
        public bool SpawnNeutralField(Vector2 at, float radius, float seconds, float dps, uint statusMask, float statusSeconds, float statusDps, CombatZoneLook look) =>
            !IsDisposed && Kernel.SpawnFieldZone(CombatFaction.Neutral, new double2(at.x, at.y), radius, seconds, dps, statusMask, statusSeconds, statusDps, 0f, 0f, look);

        public int AcidShapeCount => IsDisposed ? 0 : Kernel.AcidShapeCount;

        public bool InAcidMarsh(Vector2 point) => !IsDisposed && Kernel.InAcidMarsh(new double2(point.x, point.y));

        /// <summary>灭活无人机的投放目标（x, y, 半径；净化塔）。派生量，不进快照。</summary>
        public void SetPolluteTargets(IReadOnlyList<float3> targets)
        {
            if (!IsDisposed)
            {
                Kernel.SetPolluteTargets(targets);
            }
        }

        public int PolluteTargetCount => IsDisposed ? 0 : Kernel.PolluteTargetCount;

        public bool TryGetClarity(int unitId, out CombatClarityUnit clarity)
        {
            clarity = default;
            return !IsDisposed && Kernel.TryGetClarity(unitId, out clarity);
        }

        /// <summary>改单位的酸蚀抗性（0～1；防腐涂层等）。</summary>
        public bool SetAcidResist(int unitId, float resist) => !IsDisposed && Kernel.SetAcidResist(unitId, Mathf.Clamp01(resist));

        /// <summary>FG10-BOSS-01：改单位的免疫状态位（洗礼塔封闭时冻不住、泄压时能冻）。</summary>
        public bool SetImmuneMask(int unitId, uint mask) => !IsDisposed && Kernel.SetImmuneMask(unitId, mask);

        /// <summary>给单位加受寒（与液氮命中同一个入口；自检夹具 / 将来玩家侧的液氮来源）。</summary>
        public bool ChillUnit(int unitId, float amount) => !IsDisposed && Kernel.ChillUnit(unitId, amount);

        /// <summary>某一种区域此刻有几块（<see cref="CombatConst.ZoneKindPool"/> 喷淋池 / <see cref="CombatConst.ZoneKindSlick"/> 油迹）。</summary>
        public int CountZonesOfKind(byte kind) => IsDisposed ? 0 : Kernel.CountZonesOfKind(kind);

        /// <summary>存活的分裂液数。</summary>
        public int CountPuddles() => IsDisposed ? 0 : Kernel.CountPuddles();

        /// <summary>单位冻结中（液氮受寒满）：不能移动、不能开火。</summary>
        public bool IsFrozen(int unitId) => TryGetClarity(unitId, out CombatClarityUnit c) && c.FrozenT > 0f;

        /// <summary>单位当前的受寒（0～1）。</summary>
        public float ChillOf(int unitId) => TryGetClarity(unitId, out CombatClarityUnit c) ? c.Chill : 0f;
    }
}
