using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>FG6-DEF-01：一座炮塔在内核里的实时状态（面板 / 状态行 / 自检读；只读快照）。</summary>
    public struct TurretUnitState
    {
        public int UnitId;
        public bool Alive;
        public Vector2 Position;
        public float Health;
        public float MaxHealth;
        public float Heat;
        public bool Overheated;
        public bool WeaponEnabled;
        public bool Possessed;
        public float Ammo;
        public Vector2 Facing;
        public int Weapon;
        public float Armor;
    }

    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-001～005）：炮塔在战斗内核里的门面——热更层玩法代码（<c>Defense.TurretService</c>）只经这里碰内核（FG14 §5 硬约束 3）。
    /// 炮塔 = 内核的 <see cref="CombatUnitKind.Turret"/> 单位：己方、驻守开火（<see cref="CombatBehavior.HoldFire"/>，内核按武器的目标模式 / 转速 / 补给选目标开火，
    /// 弹道唯一真相在内核）、血量在内核（阵亡只报一次 <see cref="CombatUnitFlags.ReportDeath"/>）、击毁记一次（<see cref="CombatUnitFlags.CountKills"/>）。
    /// 外部键 = 炮塔序号（<c>TurretRecord.Serial</c>，&gt; 0）；随地点快照进存档，读档时按外部键找回（<see cref="RestoreTurretMap"/>）。
    /// 原型炮塔（<see cref="CombatBench"/>，外部键 -1）不登记在这里。
    /// </summary>
    public sealed partial class CombatSite
    {
        private readonly Dictionary<int, int> _turretUnit = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _unitTurret = new Dictionary<int, int>();

        /// <summary>FG6-DEF-01：炮塔相关的内核玩法事件（炮塔阵亡 = 建筑被摧毁；击毁记数；接入炮塔的具名反应发动）交给炮塔服务结算。
        /// 参数：地点、炮塔序号、事件。O(事件数)，与炮塔数无关。</summary>
        public static Action<CombatSite, int, CombatEvent> TurretEvent;

        public int TurretUnitCount => _turretUnit.Count;

        /// <summary>本地点内核里登记的炮塔序号（对账用；返回新列表，调用方随意改）。</summary>
        public List<int> TurretSerials()
        {
            var list = new List<int>(_turretUnit.Count);
            list.AddRange(_turretUnit.Keys);
            list.Sort();
            return list;
        }

        public bool TryGetTurretUnit(int serial, out int unitId) => _turretUnit.TryGetValue(serial, out unitId);

        public bool TryGetTurretOfUnit(int unitId, out int serial) => _unitTurret.TryGetValue(unitId, out serial);

        /// <summary>
        /// 把一座炮塔放进内核（建成 / 读档对账补齐）。已经在内核里时返回原单位。<paramref name="armorFraction"/> = 炮塔座等级的全方位减伤。
        /// <paramref name="ammo"/> = 补给存量（发）。返回单位 ID（失败 0）。
        /// </summary>
        public int SpawnTurretUnit(int serial, Vector2 position, float radius, float health, float maxHealth, in CombatWeapon weapon,
            float armorFraction, bool weaponEnabled, Vector2 facing, float ammo)
        {
            if (IsDisposed || serial <= 0)
            {
                return 0;
            }
            if (_turretUnit.TryGetValue(serial, out int existing) && Kernel.Exists(existing))
            {
                return existing;
            }
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced
                                    | CombatUnitFlags.CountKills | CombatUnitFlags.ReportDeath;
            if (weaponEnabled)
            {
                flags |= CombatUnitFlags.WeaponEnabled;
            }
            int unit = Kernel.Spawn(new CombatSpawn
            {
                ExtKey = serial,
                Kind = CombatUnitKind.Turret,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.HoldFire,
                Flags = flags,
                Position = new double2(position.x, position.y),
                Home = new double2(position.x, position.y),
                Radius = Mathf.Max(0.3f, radius),
                Speed = 0f,
                Health = Mathf.Max(0.01f, health),
                MaxHealth = Mathf.Max(0.01f, maxHealth),
                Weapon = WeaponIndex(weapon),
                BehaviorProfile = -1,
                Priority = 1,
                ArmorFraction = Mathf.Clamp(armorFraction, 0f, 0.9f),
                ArmorHalfAngleDeg = 180f,
                ArmorFacing = facing.sqrMagnitude > 1e-6f ? new float2(facing.x, facing.y) : new float2(0f, 1f),
                Ammo = Mathf.Max(0f, ammo),
            });
            if (unit <= 0)
            {
                return 0;
            }
            _turretUnit[serial] = unit;
            _unitTurret[unit] = serial;
            MarkPlaceholderVisuals();
            return unit;
        }

        /// <summary>把炮塔从内核里拿掉（拆除 / 被摧毁结算后 / 记录不存在）。</summary>
        public bool RemoveTurretUnit(int serial)
        {
            if (!_turretUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            _turretUnit.Remove(serial);
            _unitTurret.Remove(unit);
            ClearUnitLabel(unit);
            return !IsDisposed && Kernel.Despawn(unit);
        }

        public bool TryGetTurretState(int serial, out TurretUnitState st)
        {
            st = default;
            if (IsDisposed || !_turretUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            st = new TurretUnitState
            {
                UnitId = unit,
                Alive = v.Alive,
                Position = new Vector2((float)v.Position.x, (float)v.Position.y),
                Health = v.Health,
                MaxHealth = v.MaxHealth,
                Heat = v.Heat,
                Overheated = (v.Flags & CombatUnitFlags.Overheated) != 0,
                WeaponEnabled = (v.Flags & CombatUnitFlags.WeaponEnabled) != 0,
                Possessed = (v.Flags & CombatUnitFlags.Possessed) != 0,
                Ammo = v.Ammo,
                Facing = new Vector2(v.Facing.x, v.Facing.y),
                Weapon = v.Weapon,
                Armor = Kernel.ArmorOf(unit),
            };
            return true;
        }

        public bool TryGetTurretWeapon(int serial, out CombatWeapon weapon)
        {
            weapon = default;
            return !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.TryGetUnit(unit, out CombatUnitView v)
                   && Kernel.TryGetWeapon(v.Weapon, out weapon);
        }

        /// <summary>换武器参数（换蓝图 / 改目标模式 / 等级变化 / 接入与离开）。同一份参数共用武器表的一行（<see cref="WeaponIndex"/>）。</summary>
        public bool SetTurretWeapon(int serial, in CombatWeapon weapon)
        {
            return !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetUnitWeapon(unit, WeaponIndex(weapon));
        }

        public bool SetTurretFlag(int serial, CombatUnitFlags flag, bool on) =>
            !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetFlag(unit, flag, on);

        public bool SetTurretHealth(int serial, float health, float maxHealth) =>
            !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetHealth(unit, Mathf.Max(0.01f, health), Mathf.Max(0.01f, maxHealth), true);

        public bool SetTurretAmmo(int serial, float ammo) =>
            !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetAmmo(unit, Mathf.Max(0f, ammo));

        public bool SetTurretArmor(int serial, float fraction) =>
            !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetArmor(unit, Mathf.Clamp(fraction, 0f, 0.9f), 180f);

        public bool SetTurretPosition(int serial, Vector2 position) =>
            !IsDisposed && _turretUnit.TryGetValue(serial, out int unit) && Kernel.SetPosition(unit, new double2(position.x, position.y));

        /// <summary>FG6-DEF-01（FGR-DEF-005）：被接入的炮塔向 <paramref name="targetUnit"/> 开一发（与直控同一个开火入口 <c>CombatKernel.FireAt</c>：
        /// 门槛、补给、积热、读法、弹体都在内核）。之后立即排空事件。</summary>
        public CombatFireResult TurretFireAt(int serial, int targetUnit)
        {
            if (IsDisposed || !_turretUnit.TryGetValue(serial, out int unit))
            {
                return CombatFireResult.NoAttacker;
            }
            CombatFireResult r = Kernel.FireAt(unit, targetUnit, Kernel.Time);
            ProcessEvents();
            return r;
        }

        /// <summary>FG6-DEF-01（FGR-DEF-005 亲自瞄准）：从 <paramref name="origin"/> 朝 <paramref name="aimDir"/> 的瞄准锥里、射程内第一个存活的敌方单位（内核扫描，O(单位)，只在玩家点击时）。0 = 没有。</summary>
        public int FindHostileInAim(Vector2 origin, Vector2 aimDir, float range, float halfAngleDeg) =>
            IsDisposed ? 0 : Kernel.FindFirstInCone(new double2(origin.x, origin.y), new float2(aimDir.x, aimDir.y), range, halfAngleDeg, CombatFaction.Hostile);

        /// <summary>炮塔此刻射程内有没有它会打的目标（与模拟步同一套选目标规则；状态行“开火中 / 待机”按需调用，O(单位)）。</summary>
        public bool TurretHasTarget(int serial) => TurretTargetUnit(serial) != 0;

        /// <summary>炮塔按自己的目标模式此刻会选的目标单位（0 = 没有）。自检用它与暴力扫描对照；状态行用它判断“开火中”。</summary>
        public int TurretTargetUnit(int serial)
        {
            if (IsDisposed || !_turretUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v) || !v.Alive
                || !Kernel.TryGetWeapon(v.Weapon, out CombatWeapon w))
            {
                return 0;
            }
            return Kernel.QueryTarget(unit, w.Range, (v.Flags & CombatUnitFlags.NeedsLos) != 0, w.TargetMode);
        }

        /// <summary>内核里单位的威胁（与“最高威胁”模式同一个数；面板 / 自检读）。</summary>
        public float ThreatOfUnit(int unitId) => IsDisposed ? 0f : Kernel.ThreatOfUnit(unitId);

        /// <summary>读档后按快照里的炮塔单位（种类 = 炮塔、外部键 &gt; 0）重建映射；炮塔服务随后与记录对账（记录是存在性的真相）。</summary>
        private void RestoreTurretMap(in CombatUnitView v)
        {
            if (v.ExtKey > 0 && !_turretUnit.ContainsKey(v.ExtKey))
            {
                _turretUnit[v.ExtKey] = v.Id;
                _unitTurret[v.Id] = v.ExtKey;
            }
        }

        private void ClearTurretMaps()
        {
            _turretUnit.Clear();
            _unitTurret.Clear();
        }

        /// <summary>内核事件里属于炮塔的那几类（阵亡、击毁、具名反应发动）：交给 <see cref="TurretEvent"/>。返回 true = 已处理（不再走机器 / 敌人的分支）。
        /// FG6-DEF-01 审查修复（P2，B07）：炮塔打出的装配反应（熔穿过载 / 标记跳转）先交炮塔服务结算核心固件冷却，再照常走通用分支——
        /// 反应日志、伤害归因的次数、首次触发的慢放 / 图鉴 / 弹字（ReactionFeedback.OnAssemblyReaction；攻击者名字已登记为“炮塔·名字”）。</summary>
        private bool TryHandleTurretEvent(in CombatEvent e)
        {
            switch (e.Kind)
            {
                case CombatEventKind.Killed:
                case CombatEventKind.TurretKill:
                    if (_unitTurret.TryGetValue(e.Unit, out int serial))
                    {
                        TurretEvent?.Invoke(this, serial, e);
                        return true;
                    }
                    return false;
                case CombatEventKind.ReactionFired:
                    if (_unitTurret.TryGetValue(e.Unit, out int reactingSerial))
                    {
                        TurretEvent?.Invoke(this, reactingSerial, e);
                    }
                    return false; // 通用分支：机器的信号冷却只认机器单位（炮塔不重复结算），装配反应反馈照常
                default:
                    return false;
            }
        }
    }
}
