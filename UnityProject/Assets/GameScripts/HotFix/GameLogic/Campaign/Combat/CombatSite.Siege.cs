using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032）：攻城在战斗内核里的门面——热更层玩法代码（<c>Defense.SiegeService</c>）只经这里碰内核（FG14 §5 硬约束 3）。
    /// - 攻城剧场：矩形、职能表（目标类别位 + 偏好代价）、集结点（撤退的终点）。流场、按职能选目标、破墙、撤退都在内核（Main/Sim，Burst）。
    /// - 建筑结构单位：炮塔 / 防御建筑之外的建筑在攻城期间是内核里的己方结构单位（可被选中与命中、不动不开火、血量在内核、阵亡只报一次 = 建筑被摧毁；
    ///   不画圆片——建筑有自己的表现对象）。外部键 = −(<see cref="SiegeStructKeyBase"/> + 序号)，与防御（&gt; 0）、无人机（−100 万区间）、原型（−1）、攻城单位（−300 万区间）都不重叠。
    /// - 攻城单位：突袭队伍展开的敌方单位（职能、精英、所属队伍）。外部键 = −(<see cref="SiegeRaiderKeyBase"/> + 队伍键 × <see cref="SiegeKindStride"/> + 种类序号)（FG6-DEF-08 起编码敌人种类，序号 1～63，0 = 未知），阵亡即移除（匿名单位，不逐个报告）。
    ///   边界：种类序号 ≥ <see cref="SiegeKindStride"/> 记“未知”（生成表时 fgdata_raidresult.validate 校验编成表行数 &lt; 64）；击毁事件经 float 的 Value2 传外部键的相反数，
    ///   小于 2^24 才精确——队伍键须小于约 21.5 万（队伍键按派遣顺序递增，正常一局远达不到；超出时种类解码失败只记“未知”，不影响击毁数与残骸）。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>建筑结构单位外部键的基数：ExtKey = −(基数 + 序号)。</summary>
        public const int SiegeStructKeyBase = 2000000;
        /// <summary>攻城单位外部键的基数：ExtKey = −(基数 + 队伍键)。</summary>
        public const int SiegeRaiderKeyBase = 3000000;

        private readonly Dictionary<int, int> _siegeStructUnit = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _unitSiegeStruct = new Dictionary<int, int>();

        /// <summary>建筑结构单位阵亡（= 建筑被摧毁）交给攻城服务结算。参数：地点、序号、事件。</summary>
        public static Action<CombatSite, int, CombatEvent> SiegeStructEvent;

        /// <summary>攻城单位走到集结点离场（<see cref="CombatEventKind.SiegeExited"/>）。参数：地点、队伍键、事件。</summary>
        public static Action<CombatSite, int, CombatEvent> SiegeExitEvent;

        /// <summary>FG6-DEF-08：攻城 / 拦截单位被击毁（<see cref="CombatEventKind.SiegeKilled"/>）交给突袭结算服务。参数：地点、事件。</summary>
        public static Action<CombatSite, CombatEvent> SiegeKillEvent;

        /// <summary>攻城单位外部键里编码的敌人种类：ExtKey = −(<see cref="SiegeRaiderKeyBase"/> + 队伍键 × <see cref="SiegeKindStride"/> + 种类序号)。</summary>
        public const int SiegeKindStride = 64;

        /// <summary>从攻城单位的外部键（相反数）解出敌人种类序号（1 起；0 = 旧档 / 未编码 / 对不上队伍键）。</summary>
        public static int SiegeKindOf(int negExtKey, int groupKey)
        {
            int k = negExtKey - SiegeRaiderKeyBase;
            if (k <= 0 || groupKey <= 0 || k / SiegeKindStride != groupKey)
            {
                return 0;
            }
            return k % SiegeKindStride;
        }

        /// <summary>头顶职能图标（4 项：突击 / 破坏 / 攻城 / 撤退中；x = 形状序号，y = 打包颜色）。攻城服务按 fg.TbSiegeRole 写一次。</summary>
        public static float2[] SiegeRoleVisuals;

        public int SiegeStructUnitCount => _siegeStructUnit.Count;

        // ── 剧场 ──

        public bool SetSiegeTheater(bool enabled, int2 min, int2 max, int breachBase, int breachPerBand, float bandHp, float retargetSeconds, float exitRadius) =>
            !IsDisposed && Kernel.SetSiegeConfig(new CombatSiegeConfig
            {
                Enabled = (byte)(enabled ? 1 : 0),
                Min = min,
                Max = max,
                BreachBase = breachBase,
                BreachPerBand = breachPerBand,
                BandHp = bandHp,
                RetargetSeconds = retargetSeconds,
                ExitRadius = exitRadius,
            });

        public CombatSiegeConfig SiegeTheater => IsDisposed ? default : Kernel.SiegeConfig;

        public void SetSiegeRoles(IReadOnlyList<int> masks, IReadOnlyList<int> bias)
        {
            if (!IsDisposed)
            {
                Kernel.SetSiegeRoles(masks, bias);
            }
        }

        public void SetSiegeExits(IReadOnlyList<int2> exits)
        {
            if (!IsDisposed)
            {
                Kernel.SetSiegeExits(exits);
            }
        }

        // ── 建筑结构单位 ──

        public bool TryGetSiegeStructUnit(int serial, out int unitId) => _siegeStructUnit.TryGetValue(serial, out unitId);

        public List<int> SiegeStructSerials()
        {
            var list = new List<int>(_siegeStructUnit.Keys);
            list.Sort();
            return list;
        }

        /// <summary>把一座建筑放进内核当攻城目标（已在时返回原单位）。<paramref name="healthFloor"/> = 归还核心（不在内核里阵亡）。</summary>
        public int SpawnSiegeStructure(int serial, Vector2 position, float radius, float health, float maxHealth, byte category, int2 footMin, int2 footMax, bool healthFloor)
        {
            if (IsDisposed || serial <= 0)
            {
                return 0;
            }
            if (_siegeStructUnit.TryGetValue(serial, out int existing) && Kernel.Exists(existing))
            {
                return existing;
            }
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.ReportDeath;
            if (healthFloor)
            {
                flags |= CombatUnitFlags.HealthFloor;
            }
            int unit = Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -(SiegeStructKeyBase + serial),
                Kind = CombatUnitKind.Structure,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.None,
                Flags = flags,
                Position = new double2(position.x, position.y),
                Home = new double2(position.x, position.y),
                Radius = Mathf.Max(0.3f, radius),
                Speed = 0f,
                Health = Mathf.Max(0.001f, health),
                MaxHealth = Mathf.Max(0.01f, maxHealth),
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
                Siege = new CombatSiegeUnit { Cat = category, FootMin = footMin, FootMax = footMax },
            });
            if (unit <= 0)
            {
                return 0;
            }
            _siegeStructUnit[serial] = unit;
            _unitSiegeStruct[unit] = serial;
            return unit;
        }

        public bool RemoveSiegeStructure(int serial)
        {
            if (!_siegeStructUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            _siegeStructUnit.Remove(serial);
            _unitSiegeStruct.Remove(unit);
            ClearUnitLabel(unit);
            return !IsDisposed && Kernel.Despawn(unit);
        }

        public bool TryGetSiegeStructHealth(int serial, out float health, out float maxHealth, out bool alive)
        {
            health = 0f;
            maxHealth = 0f;
            alive = false;
            if (IsDisposed || !_siegeStructUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            health = v.Health;
            maxHealth = v.MaxHealth;
            alive = v.Alive;
            return true;
        }

        public bool SetSiegeStructHealth(int serial, float health, float maxHealth) =>
            !IsDisposed && _siegeStructUnit.TryGetValue(serial, out int unit) && Kernel.SetHealth(unit, Mathf.Max(0.001f, health), Mathf.Max(0.01f, maxHealth), true);

        /// <summary>建筑结构单位的位置与占地（搬迁完工 / 升级换了占地）。</summary>
        public bool SetSiegeStructPlace(int serial, Vector2 position, byte category, int2 footMin, int2 footMax)
        {
            if (IsDisposed || !_siegeStructUnit.TryGetValue(serial, out int unit) || !Kernel.TryGetSiegeUnit(unit, out CombatSiegeUnit su))
            {
                return false;
            }
            Kernel.SetPosition(unit, new double2(position.x, position.y));
            su.Cat = category;
            su.FootMin = footMin;
            su.FootMax = footMax;
            return Kernel.SetSiegeUnit(unit, su);
        }

        /// <summary>测试 / 溅射用：对建筑结构单位造成伤害（内核扣血，与敌人弹体同一入口）。</summary>
        public bool DamageSiegeStructure(int serial, float amount)
        {
            if (IsDisposed || !_siegeStructUnit.TryGetValue(serial, out int unit))
            {
                return false;
            }
            bool ok = Kernel.Damage(unit, amount, 0);
            ProcessEvents();
            return ok;
        }

        /// <summary>给任意己方单位（炮塔 / 防御结构单位）标攻城目标类别与占地（0 = 清掉）。</summary>
        public bool SetUnitSiegeTarget(int unitId, byte category, int2 footMin, int2 footMax)
        {
            if (IsDisposed || !Kernel.TryGetSiegeUnit(unitId, out CombatSiegeUnit su))
            {
                return false;
            }
            if (su.Cat == category && su.FootMin.Equals(footMin) && su.FootMax.Equals(footMax))
            {
                return true;
            }
            su.Cat = category;
            su.FootMin = footMin;
            su.FootMax = footMax;
            return Kernel.SetSiegeUnit(unitId, su);
        }

        // ── 攻城单位 ──

        /// <summary>生成一台攻城单位（敌方、突袭者行为、实例化画出来、阵亡即移除）。返回单位 ID。</summary>
        public int SpawnSiegeRaider(int groupKey, Vector2 at, Vector2 home, float radius, float speed, float health, float armorFraction,
            int weapon, int profile, CombatSiegeRole role, bool elite, float structMult, int kind = 0)
        {
            if (IsDisposed || groupKey <= 0)
            {
                return 0;
            }
            // FG6-DEF-08：外部键里带上敌人种类（RaidCatalog.Units 下标 + 1，0 = 不知道），被击毁时热更层据此记“击败过的敌人种类”（靶场解锁）与结算编成。
            int kindCode = kind > 0 && kind < SiegeKindStride && groupKey < (int.MaxValue - SiegeRaiderKeyBase) / SiegeKindStride - 1 ? kind : 0;
            MarkPlaceholderVisuals();
            CombatUnitFlags flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.Instanced
                                    | CombatUnitFlags.RemoveOnDeath;
            if (elite)
            {
                flags |= CombatUnitFlags.Elite;
            }
            return Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -(SiegeRaiderKeyBase + groupKey * SiegeKindStride + kindCode),
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.Raider,
                Flags = flags,
                Position = new double2(at.x, at.y),
                Home = new double2(home.x, home.y),
                Radius = Mathf.Max(0.2f, radius),
                Speed = Mathf.Max(0.1f, speed),
                Health = Mathf.Max(1f, health),
                MaxHealth = Mathf.Max(1f, health),
                Weapon = weapon,
                BehaviorProfile = profile,
                Priority = 1,
                ArmorFraction = Mathf.Clamp01(armorFraction),
                ArmorHalfAngleDeg = armorFraction > 0f ? 75f : 90f,
                ArmorFacing = new float2((float)(home.x - at.x), (float)(home.y - at.y)),
                Siege = new CombatSiegeUnit { Role = (byte)role, Group = groupKey, StructMult = Mathf.Max(0f, structMult) },
            });
        }

        /// <summary>拦截交战用：把刚生成的攻城单位改成“就地交战”（不走攻城流场）。</summary>
        public bool SetSiegeUnitMode(int unitId, CombatSiegeMode mode)
        {
            if (IsDisposed || !Kernel.TryGetSiegeUnit(unitId, out CombatSiegeUnit su))
            {
                return false;
            }
            su.Mode = (byte)mode;
            return Kernel.SetSiegeUnit(unitId, su);
        }

        /// <summary>
        /// DEBT-FG4ECO07-02（FGR-DEF-043“守在指定点、按教义交战”）：给驻防机器设驻防点与守点半径（0 = 不守点）。守点交战在内核里（空闲时：射程内原地打、
        /// 射程外在半径内靠近、没敌人回驻防点；有命令时听命令）。机器不在本地点返回 false。
        /// </summary>
        public bool SetMachineGuard(int logicId, Vector2 post, float radius) =>
            !IsDisposed && _machineUnit.TryGetValue(logicId, out int unit) && Kernel.SetMachineGuard(unit, new double2(post.x, post.y), radius);
        /// <summary>离 <paramref name="at"/> 最近的己方机器（半径内）。0 = 没有。</summary>
        public int FindNearestMachine(Vector2 at, float radius) =>
            IsDisposed ? 0 : Kernel.FindNearestKind(new double2(at.x, at.y), radius, CombatFaction.Player, CombatUnitKind.Machine);

        /// <summary>离 <paramref name="at"/> 最近的敌方单位（半径内）。0 = 没有。</summary>
        public int FindNearestHostile(Vector2 at, float radius) =>
            IsDisposed ? 0 : Kernel.FindNearest(new double2(at.x, at.y), radius, CombatFaction.Hostile);

        public int CountSiegeGroup(int groupKey, int[] perRole = null) => IsDisposed ? 0 : Kernel.CountSiegeGroup(groupKey, perRole);

        /// <summary>FG6-DEF-06：一支队伍还活着的攻城单位的重心（观战镜头跟随）。返回台数。</summary>
        public int SiegeGroupCentroid(int groupKey, out Vector2 centroid)
        {
            centroid = Vector2.zero;
            if (IsDisposed)
            {
                return 0;
            }
            int n = Kernel.SiegeGroupCentroid(groupKey, out double x, out double y);
            centroid = new Vector2((float)x, (float)y);
            return n;
        }

        public int SetSiegeGroupRetreat(int groupKey) => IsDisposed ? 0 : Kernel.SetSiegeGroupMode(groupKey, CombatSiegeMode.Retreat);

        public int DespawnSiegeGroup(int groupKey) => IsDisposed ? 0 : Kernel.DespawnSiegeGroup(groupKey);

        /// <summary>内核里还活着的攻城 / 拦截单位所属的队伍键。</summary>
        public int CollectSiegeGroups(List<int> into) => IsDisposed ? 0 : Kernel.CollectSiegeGroups(into);

        public int SiegeUnitCount => IsDisposed ? 0 : Kernel.SiegeUnitCount;

        // ── 溅射、破墙、查询 ──

        public int DrainSiegeImpacts(int max, List<CombatSiegeImpact> into) => IsDisposed ? 0 : Kernel.DrainSiegeImpacts(max, into);

        public int SiegeImpactCount => IsDisposed ? 0 : Kernel.SiegeImpactCount;

        public int SiegeBreachCount => IsDisposed ? 0 : Kernel.SiegeBreachCount;

        public int SiegeBreachAt(int index) => IsDisposed ? 0 : Kernel.SiegeBreachAt(index);

        /// <summary>结构单位 ID → 建筑结构单位序号（不是 = 0）。</summary>
        public int SiegeStructSerialOf(int unitId) => _unitSiegeStruct.TryGetValue(unitId, out int s) ? s : 0;

        /// <summary>结构单位 ID → 防御记录序号（不是 = 0）。</summary>
        public int DefenseSerialOf(int unitId) => _unitDefense.TryGetValue(unitId, out int s) ? s : 0;

        /// <summary>结构单位 ID → 炮塔序号（不是 = 0）。</summary>
        public int TurretSerialOf(int unitId) => _unitTurret.TryGetValue(unitId, out int s) ? s : 0;

        public CombatSiegeStats SiegeStats => IsDisposed ? default : Kernel.SiegeStats;

        /// <summary>FG6-DEF-08：内核累计读数（敌对 / 己方受到的伤害等，进内核快照）——突袭结算按“展开时基准、结束时差额”算造成 / 承受的伤害（玩法代码只经门面读，FG14 §5 硬约束 3）。</summary>
        public CombatCounters KernelCounters => IsDisposed ? default : Kernel.Counters;

        public int4 SiegeRect => IsDisposed ? int4.zero : Kernel.SiegeRect;

        public bool SiegeFieldValid(int field) => !IsDisposed && Kernel.SiegeFieldValid(field);

        public int SiegeDistAt(int field, int2 cell) => IsDisposed ? CombatSiegeConst.Inf : Kernel.SiegeDistAt(field, cell);

        public int TraceSiegePath(int field, int2 from, int maxPoints, List<int2> into) => IsDisposed ? 0 : Kernel.TraceSiegePath(field, from, maxPoints, into);

        public int SiegeBreachAhead(int field, int2 from, int lookahead) => IsDisposed ? 0 : Kernel.SiegeBreachAhead(field, from, lookahead);

        public int VerifySiegeFields(out int fields)
        {
            fields = 0;
            return IsDisposed ? 0 : Kernel.VerifySiegeFields(out fields);
        }

        public void MaintainSiegeNow()
        {
            if (!IsDisposed)
            {
                Kernel.MaintainSiegeNow();
            }
        }

        public double LastSiegeMs => IsDisposed ? 0 : Kernel.LastSiegeMs;
        public double MaxSiegeMs => IsDisposed ? 0 : Kernel.MaxSiegeMs;
        public double MaxSiegeChangeMs => IsDisposed ? 0 : Kernel.MaxSiegeChangeMs;
        public double MaxSiegeResetMs => IsDisposed ? 0 : Kernel.MaxSiegeResetMs;

        /// <summary>攻城单位的读数（悬停 / 叠加层）：位置、职能、撤退中、精英、破墙目标。</summary>
        public bool TryGetSiegeRaider(int unitId, out Vector2 position, out CombatSiegeUnit siege, out bool elite, out float health, out float maxHealth)
        {
            position = default;
            siege = default;
            elite = false;
            health = 0f;
            maxHealth = 0f;
            if (IsDisposed || !Kernel.TryGetUnit(unitId, out CombatUnitView v) || v.Siege.Role == 0 || !v.Alive)
            {
                return false;
            }
            position = new Vector2((float)v.Position.x, (float)v.Position.y);
            siege = v.Siege;
            elite = (v.Flags & CombatUnitFlags.Elite) != 0;
            health = v.Health;
            maxHealth = v.MaxHealth;
            return true;
        }

        /// <summary>攻城单位的单位 ID（按槽位顺序；叠加层 / 自检按需取，O(单位数) 只在调用时）。</summary>
        public void SiegeRaiderIds(int groupKey, List<int> into)
        {
            into.Clear();
            if (IsDisposed)
            {
                return;
            }
            for (int slot = 0; slot < Kernel.SlotCount; slot++)
            {
                CombatUnitView v = Kernel.ViewAt(slot);
                if (v.Alive && v.Siege.Role != 0 && (groupKey <= 0 || v.Siege.Group == groupKey))
                {
                    into.Add(v.Id);
                }
            }
        }

        // ── 读档与事件 ──

        /// <summary>读档后按快照里外部键落在建筑结构单位区间的己方结构单位重建映射。返回 true = 是建筑结构单位。</summary>
        private bool RestoreSiegeMap(in CombatUnitView v)
        {
            if (v.ExtKey > -SiegeStructKeyBase || v.ExtKey <= -SiegeRaiderKeyBase)
            {
                return false;
            }
            int serial = -v.ExtKey - SiegeStructKeyBase;
            if (serial > 0 && !_siegeStructUnit.ContainsKey(serial))
            {
                _siegeStructUnit[serial] = v.Id;
                _unitSiegeStruct[v.Id] = serial;
            }
            return true;
        }

        private void ClearSiegeMaps()
        {
            _siegeStructUnit.Clear();
            _unitSiegeStruct.Clear();
        }

        /// <summary>建筑结构单位的阵亡、攻城单位离场交给攻城服务。返回 true = 已处理。</summary>
        private bool TryHandleSiegeEvent(in CombatEvent e)
        {
            if (e.Kind == CombatEventKind.SiegeExited)
            {
                SiegeExitEvent?.Invoke(this, e.Other, e);
                return true;
            }
            if (e.Kind == CombatEventKind.SiegeKilled)
            {
                SiegeKillEvent?.Invoke(this, e);
                return true;
            }
            if (e.Kind == CombatEventKind.Killed && _unitSiegeStruct.TryGetValue(e.Unit, out int serial))
            {
                SiegeStructEvent?.Invoke(this, serial, e);
                return true;
            }
            return false;
        }
    }
}
