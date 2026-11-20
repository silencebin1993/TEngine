// PL-RP-02 修复（PL-VEH-02 遗留）→ production/design/presentation-track/handoff/PL-RP-02.md；SEAMS S-13
using BinGames.Presentation;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// 表现泳道经桥接层取战斗内核的唯一入口（FG14 §5 硬约束 3：热更层碰内核只许在 Campaign/Combat/ 内）。
    /// PL 新文件，不改 CombatSite*；表现层拿到内核只读（VehicleRigSystem 只读单位位姿 / 朝向）。
    /// </summary>
    public static class PresentationCombatBridge
    {
        public static void RegisterVehicleRig(CombatSite site, int key, object owner, int unitId, VehicleArchetype archetype, Transform a, Transform b)
            => VehicleRigSystem.Register(key, owner, site?.Kernel, unitId, archetype, a, b);
    }
}
