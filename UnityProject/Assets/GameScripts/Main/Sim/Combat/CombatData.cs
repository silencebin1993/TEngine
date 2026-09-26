using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>编队命令的运行状态（与 Demo RegionSquadCommandSystem.ActiveCommand 逐字段对应，另加工作赶路与接战参数）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatCommand
    {
        public CombatCommandKind Kind;
        public byte HasLast;
        public byte Stuck;
        public byte Pad;
        /// <summary>攻击目标（单位 ID，不是槽位）。</summary>
        public int Target;
        public double2 Pos;
        public float Arrive;
        public float AttackRange;
        public float AttackCooldown;
        public float AtkCd;
        public float ProgTimer;
        public float LastDist;
    }

    /// <summary>一枚飞行中的弹体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatProjectile
    {
        public double2 Pos;
        public double2 Prev;
        public float2 Vel;
        /// <summary>开火者单位 ID（开火者阵亡后弹体照常飞，击杀归属仍记它）。</summary>
        public int Owner;
        public int Weapon;
        public float Damage;
        public float Radius;
        public float Life;
        public CombatFaction Faction;
        public byte Pad0;
        public short Pad1;
    }

    /// <summary>内核的标量状态（NativeArray 长度 1，作业与托管调用共用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatScalars
    {
        public long Steps;
        public double Time;
        public int NextId;
        public int Tombstones;
        public double DirectMinY;
        public double DirectMaxY;
        /// <summary>本步已经写进提示事件的条数（每步清零）。</summary>
        public int CuesThisStep;
        /// <summary>训练靶自动交战：目标单位 ID（0 = 没有）、射程、间隔。</summary>
        public int EngageTarget;
        public float EngageRange;
        public float EngageInterval;
        /// <summary>内核状态修订号（任何状态变化后 +1，渲染缓冲据此判断要不要重填）。</summary>
        public int Revision;
        /// <summary>事件序号计数。</summary>
        public int EventSeq;
    }

    /// <summary>
    /// 战斗内核的全部数据（SoA）。一个结构体装下全部原生容器，Burst 作业（<see cref="CombatStepJob"/>）与托管的即时调用
    /// （<see cref="CombatKernel.FireAt"/> 等）共用同一份数据、同一套 <see cref="CombatLogic"/> 代码。
    /// </summary>
    public struct CombatData
    {
        public CombatConfig Config;

        // ── 单位（按槽位；槽位顺序 = 生成顺序，压实时保持相对顺序）──
        public NativeList<int> Id;
        public NativeList<int> ExtKey;
        public NativeList<byte> Kind;
        public NativeList<byte> Faction;
        public NativeList<byte> Behavior;
        public NativeList<byte> Priority;
        public NativeList<uint> Flags;
        public NativeList<double2> Pos;
        public NativeList<double2> Prev;
        public NativeList<double2> Home;
        public NativeList<float> Radius;
        public NativeList<float> Speed;
        public NativeList<float> Hp;
        public NativeList<float> MaxHp;
        public NativeList<int> Weapon;
        public NativeList<int> BProfile;
        /// <summary>(正面减伤比例, 正面锥角半角余弦, 朝向 x, 朝向 y)。</summary>
        public NativeList<float4> Armor;
        public NativeList<float> BackHit;
        public NativeList<float> Heat;
        public NativeList<double> AimReadyAt;
        public NativeList<double> NextFireAt;
        public NativeList<float> Cycle;
        public NativeList<float> Secondary;
        public NativeList<double> MarkedUntil;
        public NativeList<CombatCommand> Cmd;
        public NativeList<float2> Direct;

        /// <summary>单位 ID → 槽位（-1 = 不存在）。ID 单调递增、永不复用。</summary>
        public NativeList<int> SlotOfId;

        public NativeList<CombatWeapon> Weapons;
        public NativeList<CombatBehaviorProfile> Profiles;
        /// <summary>圆形障碍（x, y, 半径）：视线遮挡与编队局部避障（Demo 锚点净空圈）。</summary>
        public NativeList<float3> Obstacles;
        /// <summary>兴趣点（x, y, 发现半径）与是否已被发现。</summary>
        public NativeList<float3> Pois;
        public NativeList<byte> PoiReached;

        public NativeList<CombatProjectile> Projectiles;

        /// <summary>玩法事件队列（永不丢弃；热更层每步至多取 MaxGameplayEventsPerStep 条，剩下的留到下一步，进存档）。</summary>
        public NativeList<CombatEvent> Gameplay;
        /// <summary>提示事件（每步清空；超出上限的丢弃计数）。</summary>
        public NativeList<CombatEvent> Cues;

        public NativeArray<CombatScalars> Scalars;
        public NativeArray<CombatCounters> Counters;

        public bool IsCreated => Id.IsCreated;

        public static CombatData Create(in CombatConfig config, int capacity)
        {
            capacity = math.max(8, capacity);
            var d = new CombatData
            {
                Config = config,
                Id = new NativeList<int>(capacity, Allocator.Persistent),
                ExtKey = new NativeList<int>(capacity, Allocator.Persistent),
                Kind = new NativeList<byte>(capacity, Allocator.Persistent),
                Faction = new NativeList<byte>(capacity, Allocator.Persistent),
                Behavior = new NativeList<byte>(capacity, Allocator.Persistent),
                Priority = new NativeList<byte>(capacity, Allocator.Persistent),
                Flags = new NativeList<uint>(capacity, Allocator.Persistent),
                Pos = new NativeList<double2>(capacity, Allocator.Persistent),
                Prev = new NativeList<double2>(capacity, Allocator.Persistent),
                Home = new NativeList<double2>(capacity, Allocator.Persistent),
                Radius = new NativeList<float>(capacity, Allocator.Persistent),
                Speed = new NativeList<float>(capacity, Allocator.Persistent),
                Hp = new NativeList<float>(capacity, Allocator.Persistent),
                MaxHp = new NativeList<float>(capacity, Allocator.Persistent),
                Weapon = new NativeList<int>(capacity, Allocator.Persistent),
                BProfile = new NativeList<int>(capacity, Allocator.Persistent),
                Armor = new NativeList<float4>(capacity, Allocator.Persistent),
                BackHit = new NativeList<float>(capacity, Allocator.Persistent),
                Heat = new NativeList<float>(capacity, Allocator.Persistent),
                AimReadyAt = new NativeList<double>(capacity, Allocator.Persistent),
                NextFireAt = new NativeList<double>(capacity, Allocator.Persistent),
                Cycle = new NativeList<float>(capacity, Allocator.Persistent),
                Secondary = new NativeList<float>(capacity, Allocator.Persistent),
                MarkedUntil = new NativeList<double>(capacity, Allocator.Persistent),
                Cmd = new NativeList<CombatCommand>(capacity, Allocator.Persistent),
                Direct = new NativeList<float2>(capacity, Allocator.Persistent),
                SlotOfId = new NativeList<int>(capacity + 1, Allocator.Persistent),
                Weapons = new NativeList<CombatWeapon>(16, Allocator.Persistent),
                Profiles = new NativeList<CombatBehaviorProfile>(16, Allocator.Persistent),
                Obstacles = new NativeList<float3>(16, Allocator.Persistent),
                Pois = new NativeList<float3>(8, Allocator.Persistent),
                PoiReached = new NativeList<byte>(8, Allocator.Persistent),
                Projectiles = new NativeList<CombatProjectile>(math.max(16, config.ProjectileCapacity), Allocator.Persistent),
                Gameplay = new NativeList<CombatEvent>(64, Allocator.Persistent),
                Cues = new NativeList<CombatEvent>(64, Allocator.Persistent),
                Scalars = new NativeArray<CombatScalars>(1, Allocator.Persistent),
                Counters = new NativeArray<CombatCounters>(1, Allocator.Persistent),
            };
            d.SlotOfId.Add(-1); // ID 0 = 没有
            d.Scalars[0] = new CombatScalars
            {
                NextId = 1,
                DirectMinY = -CombatConst.NoClamp,
                DirectMaxY = CombatConst.NoClamp,
            };
            return d;
        }

        public void Dispose()
        {
            if (!IsCreated)
            {
                return;
            }
            Id.Dispose();
            ExtKey.Dispose();
            Kind.Dispose();
            Faction.Dispose();
            Behavior.Dispose();
            Priority.Dispose();
            Flags.Dispose();
            Pos.Dispose();
            Prev.Dispose();
            Home.Dispose();
            Radius.Dispose();
            Speed.Dispose();
            Hp.Dispose();
            MaxHp.Dispose();
            Weapon.Dispose();
            BProfile.Dispose();
            Armor.Dispose();
            BackHit.Dispose();
            Heat.Dispose();
            AimReadyAt.Dispose();
            NextFireAt.Dispose();
            Cycle.Dispose();
            Secondary.Dispose();
            MarkedUntil.Dispose();
            Cmd.Dispose();
            Direct.Dispose();
            SlotOfId.Dispose();
            Weapons.Dispose();
            Profiles.Dispose();
            Obstacles.Dispose();
            Pois.Dispose();
            PoiReached.Dispose();
            Projectiles.Dispose();
            Gameplay.Dispose();
            Cues.Dispose();
            Scalars.Dispose();
            Counters.Dispose();
        }

        public int Count => Id.Length;

        public int SlotOf(int id) => id > 0 && id < SlotOfId.Length ? SlotOfId[id] : -1;

        public bool IsAlive(int slot) => slot >= 0 && (Flags[slot] & (uint)CombatUnitFlags.Alive) != 0;

        public bool Has(int slot, CombatUnitFlags f) => (Flags[slot] & (uint)f) != 0;

        public void Set(int slot, CombatUnitFlags f, bool on)
        {
            uint v = Flags[slot];
            Flags[slot] = on ? (v | (uint)f) : (v & ~(uint)f);
        }

        /// <summary>追加一个单位（全部 SoA 同步增长）。返回槽位。</summary>
        public int Append(in CombatSpawn s, int id)
        {
            int slot = Id.Length;
            Id.Add(id);
            ExtKey.Add(s.ExtKey);
            Kind.Add((byte)s.Kind);
            Faction.Add((byte)s.Faction);
            Behavior.Add((byte)s.Behavior);
            Priority.Add(s.Priority);
            Flags.Add((uint)s.Flags);
            Pos.Add(s.Position);
            Prev.Add(s.Position);
            Home.Add(s.Home);
            Radius.Add(s.Radius);
            Speed.Add(s.Speed);
            Hp.Add(s.Health);
            MaxHp.Add(s.MaxHealth);
            Weapon.Add(s.Weapon);
            BProfile.Add(s.BehaviorProfile);
            float cosHalf = math.cos(math.radians(s.ArmorHalfAngleDeg));
            float2 facing = math.lengthsq(s.ArmorFacing) > 1e-12f ? math.normalize(s.ArmorFacing) : new float2(0f, -1f);
            Armor.Add(new float4(s.ArmorFraction, cosHalf, facing.x, facing.y));
            BackHit.Add(s.BackHitBonus);
            Heat.Add(s.Heat);
            AimReadyAt.Add(s.AimReadyAt);
            NextFireAt.Add(s.NextFireAt);
            Cycle.Add(s.Cycle);
            Secondary.Add(s.Secondary);
            MarkedUntil.Add(0);
            Cmd.Add(default);
            Direct.Add(float2.zero);
            while (SlotOfId.Length <= id)
            {
                SlotOfId.Add(-1);
            }
            SlotOfId[id] = slot;
            return slot;
        }

        /// <summary>把槽位 <paramref name="from"/> 的全部字段搬到 <paramref name="to"/>（压实用，to &lt; from）。</summary>
        public void MoveSlot(int from, int to)
        {
            Id[to] = Id[from];
            ExtKey[to] = ExtKey[from];
            Kind[to] = Kind[from];
            Faction[to] = Faction[from];
            Behavior[to] = Behavior[from];
            Priority[to] = Priority[from];
            Flags[to] = Flags[from];
            Pos[to] = Pos[from];
            Prev[to] = Prev[from];
            Home[to] = Home[from];
            Radius[to] = Radius[from];
            Speed[to] = Speed[from];
            Hp[to] = Hp[from];
            MaxHp[to] = MaxHp[from];
            Weapon[to] = Weapon[from];
            BProfile[to] = BProfile[from];
            Armor[to] = Armor[from];
            BackHit[to] = BackHit[from];
            Heat[to] = Heat[from];
            AimReadyAt[to] = AimReadyAt[from];
            NextFireAt[to] = NextFireAt[from];
            Cycle[to] = Cycle[from];
            Secondary[to] = Secondary[from];
            MarkedUntil[to] = MarkedUntil[from];
            Cmd[to] = Cmd[from];
            Direct[to] = Direct[from];
        }

        public void Truncate(int length)
        {
            Id.ResizeUninitialized(length);
            ExtKey.ResizeUninitialized(length);
            Kind.ResizeUninitialized(length);
            Faction.ResizeUninitialized(length);
            Behavior.ResizeUninitialized(length);
            Priority.ResizeUninitialized(length);
            Flags.ResizeUninitialized(length);
            Pos.ResizeUninitialized(length);
            Prev.ResizeUninitialized(length);
            Home.ResizeUninitialized(length);
            Radius.ResizeUninitialized(length);
            Speed.ResizeUninitialized(length);
            Hp.ResizeUninitialized(length);
            MaxHp.ResizeUninitialized(length);
            Weapon.ResizeUninitialized(length);
            BProfile.ResizeUninitialized(length);
            Armor.ResizeUninitialized(length);
            BackHit.ResizeUninitialized(length);
            Heat.ResizeUninitialized(length);
            AimReadyAt.ResizeUninitialized(length);
            NextFireAt.ResizeUninitialized(length);
            Cycle.ResizeUninitialized(length);
            Secondary.ResizeUninitialized(length);
            MarkedUntil.ResizeUninitialized(length);
            Cmd.ResizeUninitialized(length);
            Direct.ResizeUninitialized(length);
        }

        /// <summary>全部单位与标量清空（读档前）。武器 / 行为表、障碍、兴趣点另行覆盖。</summary>
        public void ClearUnits()
        {
            Truncate(0);
            SlotOfId.Clear();
            SlotOfId.Add(-1);
            Projectiles.Clear();
            Gameplay.Clear();
            Cues.Clear();
        }
    }
}
