using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-014“无人机是可见的实体，可能被击落”）：维修无人机在战斗内核里的门面——热更层玩法代码（<c>Defense.RepairDroneService</c>）只经这里碰内核。
    /// 出动中的维修无人机 = 内核里的己方结构单位（<see cref="CombatUnitKind.Structure"/>、己方阵营、可被选中与命中、不开火、不推挤机器、实例化画出来）：
    /// 敌人的弹体能打中它，血量在内核，阵亡只报一次（= 被击落）。位置由热更层每步写（直线飞行，数量 = 站数 × 编制，很少）。
    /// 外部键 = −(<see cref="DroneKeyBase"/> + 无人机序号)，与防御建筑（&gt; 0）、原型单位（−1）、敌方结构单位（字符串键下标 ≥ 0）都不重叠；
    /// 随地点快照进存档，读档时按外部键找回（<see cref="RestoreDroneMap"/>）。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>无人机外部键的基数：ExtKey = −(基数 + 序号)。</summary>
        public const int DroneKeyBase = 1000000;

        private readonly Dictionary<int, int> _droneUnit = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _unitDrone = new Dictionary<int, int>();

        /// <summary>维修无人机阵亡（= 被击落）交给维修无人机服务结算。参数：地点、无人机序号、事件。O(事件数)。</summary>
        public static Action<CombatSite, int, CombatEvent> DroneEvent;

        public int DroneUnitCount => _droneUnit.Count;

        /// <summary>本地点内核里登记的无人机序号（对账用；返回新列表，升序）。</summary>
        public List<int> DroneSerials()
        {
            var list = new List<int>(_droneUnit.Count);
            list.AddRange(_droneUnit.Keys);
            list.Sort();
            return list;
        }

        public bool TryGetDroneUnit(int serial, out int unitId) => _droneUnit.TryGetValue(serial, out unitId);

        /// <summary>放一架出动的无人机进内核（已在时返回原单位）。返回单位 ID（失败 0）。</summary>
        public int SpawnDroneUnit(int serial, Vector2 position, float radius, float health, float maxHealth)
        {
            if (IsDisposed || serial <= 0)
            {
                return 0;
            }
            if (_droneUnit.TryGetValue(serial, out int existing) && Kernel.Exists(existing))
            {
                return existing;
            }
            int unit = Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -(DroneKeyBase + serial),
                Kind = CombatUnitKind.Structure,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.ReportDeath | CombatUnitFlags.Instanced,
                Position = new double2(position.x, position.y),
                Home = new double2(position.x, position.y),
                Radius = Mathf.Max(0.1f, radius),
                Speed = 0f,
                Health = Mathf.Max(0.01f, health),
                MaxHealth = Mathf.Max(0.01f, maxHealth),
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            });
            if (unit <= 0)
            {
                return 0;
            }
            _droneUnit[serial] = unit;
            _unitDrone[unit] = serial;
            return unit;
        }

        public bool RemoveDroneUnit(int serial)
        {
            if (!_droneUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            _droneUnit.Remove(serial);
            _unitDrone.Remove(unit);
            ClearUnitLabel(unit);
            return !IsDisposed && Kernel.Despawn(unit);
        }

        public bool TryGetDroneState(int serial, out Vector2 position, out float health, out float maxHealth, out bool alive)
        {
            position = default;
            health = 0f;
            maxHealth = 0f;
            alive = false;
            if (IsDisposed || !_droneUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            position = new Vector2((float)v.Position.x, (float)v.Position.y);
            health = v.Health;
            maxHealth = v.MaxHealth;
            alive = v.Alive;
            return true;
        }

        public bool SetDronePosition(int serial, Vector2 position) =>
            !IsDisposed && _droneUnit.TryGetValue(serial, out int unit) && Kernel.SetPosition(unit, new double2(position.x, position.y));

        /// <summary>测试 / 攻城用：对一架无人机造成伤害（内核扣血，与敌人弹体同一入口）。</summary>
        public bool DamageDrone(int serial, float amount)
        {
            if (IsDisposed || !_droneUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            bool ok = Kernel.Damage(unit, amount, 0);
            ProcessEvents();
            return ok;
        }

        // ── 读档与事件 ──

        /// <summary>读档后按快照里外部键落在无人机区间的己方结构单位重建映射。返回 true = 是无人机（调用方不再当防御建筑处理）。</summary>
        private bool RestoreDroneMap(in CombatUnitView v)
        {
            if (v.ExtKey > -DroneKeyBase)
            {
                return false;
            }
            int serial = -v.ExtKey - DroneKeyBase;
            if (serial > 0 && !_droneUnit.ContainsKey(serial))
            {
                _droneUnit[serial] = v.Id;
                _unitDrone[v.Id] = serial;
            }
            return true;
        }

        private void ClearDroneMaps()
        {
            _droneUnit.Clear();
            _unitDrone.Clear();
        }

        /// <summary>无人机的阵亡事件交给 <see cref="DroneEvent"/>。返回 true = 已处理。</summary>
        private bool TryHandleDroneEvent(in CombatEvent e)
        {
            if (e.Kind != CombatEventKind.Killed || !_unitDrone.TryGetValue(e.Unit, out int serial))
            {
                return false;
            }
            DroneEvent?.Invoke(this, serial, e);
            return true;
        }
    }
}
