using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG6-DEF-02（FG06 FGR-DEF-010～013）：防御建筑在战斗内核里的门面——热更层玩法代码（<c>Defense.DefenseService</c>）只经这里碰内核（FG14 §5 硬约束 3）。
    /// - 结构单位：屏障 / 闸门 / 护盾发生器 / 陷阱发射器建成后是内核里的己方结构单位（<see cref="CombatUnitKind.Structure"/>、己方阵营、可被选中与命中、不动不开火）：
    ///   敌人的弹体打在它们身上（屏障挡敌方弹体），己方弹体只与敌对阵营碰撞（己方屏障不挡己方炮塔，FGR-DEF-002）；血量在内核，阵亡只报一次（= 建筑被摧毁）。
    ///   外部键 = 防御记录序号（&gt; 0）；随地点快照进存档，读档时按“己方阵营的结构单位”找回（<see cref="RestoreDefenseMap"/>）。
    /// - 护盾：内核护盾表（逐弹体吸收在 Main/Sim），外部键同上。
    /// - 场地：陷阱发射器铺的油膜带 / 冷却液带 / 电磁场 = 不属于任何单位的内核区域（与读法区域同一套结算、同一个上限）。
    /// </summary>
    public sealed partial class CombatSite
    {
        private readonly Dictionary<int, int> _defenseUnit = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _unitDefense = new Dictionary<int, int>();

        /// <summary>防御结构单位阵亡（= 建筑被摧毁）交给防御服务结算。参数：地点、防御序号、事件。O(事件数)。</summary>
        public static Action<CombatSite, int, CombatEvent> DefenseEvent;

        public int DefenseUnitCount => _defenseUnit.Count;

        /// <summary>本地点内核里登记的防御序号（对账用；返回新列表，升序）。</summary>
        public List<int> DefenseSerials()
        {
            var list = new List<int>(_defenseUnit.Count);
            list.AddRange(_defenseUnit.Keys);
            list.Sort();
            return list;
        }

        public bool TryGetDefenseUnit(int serial, out int unitId) => _defenseUnit.TryGetValue(serial, out unitId);

        /// <summary>把一座建成的防御建筑放进内核（已在时返回原单位）。返回单位 ID（失败 0）。</summary>
        public int SpawnDefenseUnit(int serial, Vector2 position, float radius, float health, float maxHealth)
        {
            if (IsDisposed || serial <= 0)
            {
                return 0;
            }
            if (_defenseUnit.TryGetValue(serial, out int existing) && Kernel.Exists(existing))
            {
                return existing;
            }
            int unit = Kernel.Spawn(new CombatSpawn
            {
                ExtKey = serial,
                Kind = CombatUnitKind.Structure,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.ReportDeath,
                Position = new double2(position.x, position.y),
                Home = new double2(position.x, position.y),
                Radius = Mathf.Max(0.3f, radius),
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
            _defenseUnit[serial] = unit;
            _unitDefense[unit] = serial;
            return unit;
        }

        public bool RemoveDefenseUnit(int serial)
        {
            if (!_defenseUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            _defenseUnit.Remove(serial);
            _unitDefense.Remove(unit);
            ClearUnitLabel(unit);
            return !IsDisposed && Kernel.Despawn(unit);
        }

        public bool TryGetDefenseHealth(int serial, out float health, out float maxHealth, out bool alive)
        {
            health = 0f;
            maxHealth = 0f;
            alive = false;
            if (IsDisposed || !_defenseUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            health = v.Health;
            maxHealth = v.MaxHealth;
            alive = v.Alive;
            return true;
        }

        public bool SetDefenseHealth(int serial, float health, float maxHealth) =>
            !IsDisposed && _defenseUnit.TryGetValue(serial, out int unit) && Kernel.SetHealth(unit, Mathf.Max(0.01f, health), Mathf.Max(0.01f, maxHealth), true);

        public bool SetDefensePosition(int serial, Vector2 position) =>
            !IsDisposed && _defenseUnit.TryGetValue(serial, out int unit) && Kernel.SetPosition(unit, new double2(position.x, position.y));

        /// <summary>测试 / 攻城用：对一座防御建筑造成伤害（内核扣血，与敌人弹体同一入口）。</summary>
        public bool DamageDefense(int serial, float amount)
        {
            if (IsDisposed || !_defenseUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            bool ok = Kernel.Damage(unit, amount, 0);
            ProcessEvents();
            return ok;
        }

        // ── 护盾 ──

        public bool SetShield(int serial, Vector2 position, float radius, float hp, float maxHp, bool active, float regenPerSec) =>
            !IsDisposed && Kernel.SetShield(serial, CombatFaction.Player, new double2(position.x, position.y), radius, hp, maxHp, active, regenPerSec);

        public bool RemoveShield(int serial) => !IsDisposed && Kernel.RemoveShield(serial);

        public bool TryGetShield(int serial, out CombatShield shield)
        {
            shield = default;
            return !IsDisposed && Kernel.TryGetShield(serial, out shield);
        }

        public int ShieldCount => IsDisposed ? 0 : Kernel.ShieldCount;

        public List<int> ShieldSerials()
        {
            var list = new List<int>();
            if (IsDisposed)
            {
                return list;
            }
            for (int i = 0; i < Kernel.ShieldCount; i++)
            {
                list.Add(Kernel.ShieldAt(i).ExtKey);
            }
            return list;
        }

        // ── 场地 ──

        /// <summary>陷阱发射器铺一块场地（己方：打敌对阵营）。容量满时返回 false（内核计数“读法被拒”）。</summary>
        public bool SpawnTrapField(Vector2 position, float radius, float seconds, float dps, uint statusMask, float statusDps, float slow, float vuln) =>
            !IsDisposed && Kernel.SpawnFieldZone(CombatFaction.Player, new double2(position.x, position.y), radius, seconds, dps, statusMask, 0f, statusDps, slow, vuln);

        // ── 读档与事件 ──

        /// <summary>读档后按快照里的己方结构单位（外部键 &gt; 0）重建映射；防御服务随后与记录对账（记录是存在性的真相）。</summary>
        private void RestoreDefenseMap(in CombatUnitView v)
        {
            if (v.ExtKey > 0 && !_defenseUnit.ContainsKey(v.ExtKey))
            {
                _defenseUnit[v.ExtKey] = v.Id;
                _unitDefense[v.Id] = v.ExtKey;
            }
        }

        private void ClearDefenseMaps()
        {
            _defenseUnit.Clear();
            _unitDefense.Clear();
        }

        /// <summary>防御结构单位的阵亡事件交给 <see cref="DefenseEvent"/>。返回 true = 已处理。</summary>
        private bool TryHandleDefenseEvent(in CombatEvent e)
        {
            if (e.Kind != CombatEventKind.Killed || !_unitDefense.TryGetValue(e.Unit, out int serial))
            {
                return false;
            }
            DefenseEvent?.Invoke(this, serial, e);
            return true;
        }
    }
}
