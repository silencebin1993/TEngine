using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using BinGames.Sim.Nav;

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
        /// <summary>FG0-ARCH-06：下一条寻路请求的序号（单位只认领序号与自己在等的那条一致的结果）。</summary>
        public int NextNavSerial;
        /// <summary>FG0-ARCH-06：路线池里已作废的路点数（过半时整理）。</summary>
        public int RouteGarbage;
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
        /// <summary>FG2-FW-02：状态标签（固件读法挂上的）——标签位掩码、统一到期时间、持续伤害（每秒）、减速比例、易伤比例、最后挂上的单位 ID（持续伤害归属）。
        /// 到期后整组清掉；同类效果取大、到期取晚（不叠层，叠层与反应归 FG2-FW-03）。</summary>
        public NativeList<uint> Status;
        public NativeList<double> StatusUntil;
        public NativeList<float> StatusDps;
        public NativeList<float> StatusSlow;
        public NativeList<float> StatusVuln;
        public NativeList<int> StatusSource;
        /// <summary>FG2-FW-03：每个状态位的叠层数（2 位一格，位 i 的层数 = (StatusStacks &gt;&gt; 2i) &amp; 3；上限 <see cref="CombatConfig.StatusStackCap"/>，最多 3）。</summary>
        public NativeList<ulong> StatusStacks;
        /// <summary>FG2-FW-04（DEBT-FG2FW03-02，格式 5）：每个状态位自己的到期时间（每单位 <see cref="CombatConst.StatusBitStride"/> 格，槽位 i 的位 b 在 i × 32 + b）。
        /// <see cref="StatusUntil"/> 仍是这些时间里最晚的一个（“这个单位还有没有标签”的快速判断）；先挂的标签先到期，到期只清那一位并按剩下的位重算效果。</summary>
        public NativeList<double> StatusBitUntil;
        /// <summary>FG2-FW-04 修复（格式 6）：区域减速位（<see cref="CombatConst.StatusBitZoneSlow"/>）自己的减速值。<see cref="StatusSlow"/> 是所有来源取大后的结果；
        /// 减速标签逐位到期后按剩下的位重算时，区域减速从这里取起点，不再沿用混合最大值（否则已到期的减速标签的数值会残留到区域减速位到期）。</summary>
        public NativeList<float> StatusZoneSlow;
        public NativeList<CombatCommand> Cmd;
        public NativeList<float2> Direct;

        // ── FG0-ARCH-06 寻路（每单位；只在 Config.NavEnabled 的地点使用）──
        public NativeList<byte> NavSt;
        public NativeList<int> NavSerial;
        public NativeList<byte> NavFail;
        public NativeList<int> RouteOff;
        public NativeList<int> RouteLen;
        public NativeList<int> RouteIdx;
        /// <summary>路线终点的精确位置（目标格可走时 = 命令目标点；目标被占时 = 就近可达格的格心）。</summary>
        public NativeList<double2> RouteEnd;
        /// <summary>路线路点池（格坐标）。</summary>
        public NativeList<int2> RoutePts;
        /// <summary>本步新发出、还没交给寻路内核的请求（热更层每步整体转交）。</summary>
        public NativeList<CombatNavRequest> NavOut;
        /// <summary>通行格网镜像（归寻路内核所有，内核只读 / 按需生成纯地形区块；没有寻路的地点是一张空格网）。不随本结构释放。</summary>
        public NavGrid Nav;

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
        /// <summary>FG2-FW-02：读法生成的区域、待结算回波、无人机（都随快照进存档）。</summary>
        public NativeList<CombatZone> Zones;
        public NativeList<CombatEcho> Echoes;
        /// <summary>FG2-E2E-01（FG-GAP-043）：引信弹迹（表现数据，按游戏时间到期；不进快照与哈希）。</summary>
        public NativeList<CombatShotTrace> Traces;
        public NativeList<CombatDrone> Drones;

        /// <summary>FG2-FW-03：具名标签反应规则（热更层按 fg.TbReaction 在建地点时写入，按 priority 排好序；不进存档——规则是内容，不是状态）。</summary>
        public NativeList<CombatReactionRule> Reactions;
        /// <summary>FG2-FW-03：每个状态位的效果（反应附加 / 残留区域挂标签时用；按 fg.TbStatusTag 的 effect / amount 写入，32 格）。</summary>
        public NativeArray<CombatStatusFx> StatusFx;
        /// <summary>FG2-FW-03：每条反应（规则下标）累计触发次数与反应额外伤害（随快照进存档；伤害归因与统计面板由 FG2-FW-04 读）。</summary>
        public NativeArray<int> ReactionCount;
        public NativeArray<float> ReactionDamage;
        /// <summary>FG2-FW-03：计数槽 i 属于哪条反应（<see cref="CombatReactionRule.Key"/>；0 = 无键 / 空槽）。登记规则与读档时按键把计数挪到新下标。</summary>
        public NativeArray<int> ReactionKey;

        // ── FG2-FW-04 反馈与伤害归因的“进给”（不进快照、不进状态哈希：只给热更层按步读增量；读档 / 重新登记规则后热更层重取基线）──
        /// <summary>每条反应最后一次触发的位置、出手者单位 ID、目标单位 ID（弹字定位、反应日志的触发者与目标）。</summary>
        public NativeArray<double2> ReactionLastPos;
        public NativeArray<int> ReactionLastSource;
        public NativeArray<int> ReactionLastTarget;
        /// <summary>每条反应打在敌对阵营单位身上的额外伤害（伤害归因：玩家这一边的反应伤害；克制类的返还不计）。</summary>
        public NativeArray<double> ReactionHostileDamage;
        /// <summary>[0] = 敌对阵营单位累计受到的伤害（精确值，<see cref="CombatCounters.DamageToHostile"/> 是逐次四舍五入的整数）。</summary>
        public NativeArray<double> DamageDealtHostile;
        /// <summary>读法生成的区域 / 回波 / 无人机（下标见 <see cref="CombatConst.ReadingFeedZone"/> 等）：累计次数、最后的位置与出手者（读法弹字与音效）。</summary>
        public NativeArray<long> ReadingFeedCount;
        public NativeArray<double2> ReadingFeedPos;
        public NativeArray<int> ReadingFeedOwner;

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
                Status = new NativeList<uint>(capacity, Allocator.Persistent),
                StatusUntil = new NativeList<double>(capacity, Allocator.Persistent),
                StatusDps = new NativeList<float>(capacity, Allocator.Persistent),
                StatusSlow = new NativeList<float>(capacity, Allocator.Persistent),
                StatusVuln = new NativeList<float>(capacity, Allocator.Persistent),
                StatusSource = new NativeList<int>(capacity, Allocator.Persistent),
                StatusStacks = new NativeList<ulong>(capacity, Allocator.Persistent),
                StatusBitUntil = new NativeList<double>(capacity * CombatConst.StatusBitStride, Allocator.Persistent),
                StatusZoneSlow = new NativeList<float>(capacity, Allocator.Persistent),
                Cmd = new NativeList<CombatCommand>(capacity, Allocator.Persistent),
                Direct = new NativeList<float2>(capacity, Allocator.Persistent),
                NavSt = new NativeList<byte>(capacity, Allocator.Persistent),
                NavSerial = new NativeList<int>(capacity, Allocator.Persistent),
                NavFail = new NativeList<byte>(capacity, Allocator.Persistent),
                RouteOff = new NativeList<int>(capacity, Allocator.Persistent),
                RouteLen = new NativeList<int>(capacity, Allocator.Persistent),
                RouteIdx = new NativeList<int>(capacity, Allocator.Persistent),
                RouteEnd = new NativeList<double2>(capacity, Allocator.Persistent),
                RoutePts = new NativeList<int2>(64, Allocator.Persistent),
                NavOut = new NativeList<CombatNavRequest>(8, Allocator.Persistent),
                SlotOfId = new NativeList<int>(capacity + 1, Allocator.Persistent),
                Weapons = new NativeList<CombatWeapon>(16, Allocator.Persistent),
                Profiles = new NativeList<CombatBehaviorProfile>(16, Allocator.Persistent),
                Obstacles = new NativeList<float3>(16, Allocator.Persistent),
                Pois = new NativeList<float3>(8, Allocator.Persistent),
                PoiReached = new NativeList<byte>(8, Allocator.Persistent),
                Projectiles = new NativeList<CombatProjectile>(math.max(16, config.ProjectileCapacity), Allocator.Persistent),
                Zones = new NativeList<CombatZone>(16, Allocator.Persistent),
                Echoes = new NativeList<CombatEcho>(16, Allocator.Persistent),
                Traces = new NativeList<CombatShotTrace>(16, Allocator.Persistent),
                Drones = new NativeList<CombatDrone>(16, Allocator.Persistent),
                Reactions = new NativeList<CombatReactionRule>(CombatConst.MaxReactions, Allocator.Persistent),
                StatusFx = new NativeArray<CombatStatusFx>(32, Allocator.Persistent),
                ReactionCount = new NativeArray<int>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionDamage = new NativeArray<float>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionKey = new NativeArray<int>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionLastPos = new NativeArray<double2>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionLastSource = new NativeArray<int>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionLastTarget = new NativeArray<int>(CombatConst.MaxReactions, Allocator.Persistent),
                ReactionHostileDamage = new NativeArray<double>(CombatConst.MaxReactions, Allocator.Persistent),
                DamageDealtHostile = new NativeArray<double>(1, Allocator.Persistent),
                ReadingFeedCount = new NativeArray<long>(CombatConst.ReadingFeedKinds, Allocator.Persistent),
                ReadingFeedPos = new NativeArray<double2>(CombatConst.ReadingFeedKinds, Allocator.Persistent),
                ReadingFeedOwner = new NativeArray<int>(CombatConst.ReadingFeedKinds, Allocator.Persistent),
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
            Status.Dispose();
            StatusUntil.Dispose();
            StatusDps.Dispose();
            StatusSlow.Dispose();
            StatusVuln.Dispose();
            StatusSource.Dispose();
            StatusStacks.Dispose();
            StatusBitUntil.Dispose();
            StatusZoneSlow.Dispose();
            Cmd.Dispose();
            Direct.Dispose();
            NavSt.Dispose();
            NavSerial.Dispose();
            NavFail.Dispose();
            RouteOff.Dispose();
            RouteLen.Dispose();
            RouteIdx.Dispose();
            RouteEnd.Dispose();
            RoutePts.Dispose();
            NavOut.Dispose();
            SlotOfId.Dispose();
            Weapons.Dispose();
            Profiles.Dispose();
            Obstacles.Dispose();
            Pois.Dispose();
            PoiReached.Dispose();
            Projectiles.Dispose();
            Zones.Dispose();
            Echoes.Dispose();
            Traces.Dispose();
            Drones.Dispose();
            Reactions.Dispose();
            StatusFx.Dispose();
            ReactionCount.Dispose();
            ReactionDamage.Dispose();
            ReactionKey.Dispose();
            ReactionLastPos.Dispose();
            ReactionLastSource.Dispose();
            ReactionLastTarget.Dispose();
            ReactionHostileDamage.Dispose();
            DamageDealtHostile.Dispose();
            ReadingFeedCount.Dispose();
            ReadingFeedPos.Dispose();
            ReadingFeedOwner.Dispose();
            Gameplay.Dispose();
            Cues.Dispose();
            Scalars.Dispose();
            Counters.Dispose();
        }

        public int Count => Id.Length;

        public int ZoneCap => Config.ZoneCapacity > 0 ? Config.ZoneCapacity : CombatConst.DefaultZoneCapacity;
        public int EchoCap => Config.EchoCapacity > 0 ? Config.EchoCapacity : CombatConst.DefaultEchoCapacity;
        public int DroneCap => Config.DroneCapacity > 0 ? Config.DroneCapacity : CombatConst.DefaultDroneCapacity;

        /// <summary>FG2-FW-02：这个单位此刻身上的状态标签（已到期的算没有）。</summary>
        public bool StatusActive(int slot, double now) => Status[slot] != 0u && StatusUntil[slot] > now;

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
            Status.Add(0u);
            StatusUntil.Add(0);
            StatusDps.Add(0f);
            StatusSlow.Add(0f);
            StatusVuln.Add(0f);
            StatusSource.Add(0);
            StatusStacks.Add(0UL);
            for (int b = 0; b < CombatConst.StatusBitStride; b++)
            {
                StatusBitUntil.Add(0);
            }
            StatusZoneSlow.Add(0f);
            Cmd.Add(default);
            Direct.Add(float2.zero);
            NavSt.Add(0);
            NavSerial.Add(0);
            NavFail.Add(0);
            RouteOff.Add(0);
            RouteLen.Add(0);
            RouteIdx.Add(0);
            RouteEnd.Add(s.Position);
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
            Status[to] = Status[from];
            StatusUntil[to] = StatusUntil[from];
            StatusDps[to] = StatusDps[from];
            StatusSlow[to] = StatusSlow[from];
            StatusVuln[to] = StatusVuln[from];
            StatusSource[to] = StatusSource[from];
            StatusStacks[to] = StatusStacks[from];
            for (int b = 0; b < CombatConst.StatusBitStride; b++)
            {
                StatusBitUntil[to * CombatConst.StatusBitStride + b] = StatusBitUntil[from * CombatConst.StatusBitStride + b];
            }
            StatusZoneSlow[to] = StatusZoneSlow[from];
            Cmd[to] = Cmd[from];
            Direct[to] = Direct[from];
            NavSt[to] = NavSt[from];
            NavSerial[to] = NavSerial[from];
            NavFail[to] = NavFail[from];
            RouteOff[to] = RouteOff[from];
            RouteLen[to] = RouteLen[from];
            RouteIdx[to] = RouteIdx[from];
            RouteEnd[to] = RouteEnd[from];
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
            Status.ResizeUninitialized(length);
            StatusUntil.ResizeUninitialized(length);
            StatusDps.ResizeUninitialized(length);
            StatusSlow.ResizeUninitialized(length);
            StatusVuln.ResizeUninitialized(length);
            StatusSource.ResizeUninitialized(length);
            StatusStacks.ResizeUninitialized(length);
            StatusBitUntil.ResizeUninitialized(length * CombatConst.StatusBitStride);
            StatusZoneSlow.ResizeUninitialized(length);
            Cmd.ResizeUninitialized(length);
            Direct.ResizeUninitialized(length);
            NavSt.ResizeUninitialized(length);
            NavSerial.ResizeUninitialized(length);
            NavFail.ResizeUninitialized(length);
            RouteOff.ResizeUninitialized(length);
            RouteLen.ResizeUninitialized(length);
            RouteIdx.ResizeUninitialized(length);
            RouteEnd.ResizeUninitialized(length);
        }

        /// <summary>全部单位与标量清空（读档前）。武器 / 行为表、障碍、兴趣点另行覆盖。</summary>
        public void ClearUnits()
        {
            Truncate(0);
            SlotOfId.Clear();
            SlotOfId.Add(-1);
            Projectiles.Clear();
            Zones.Clear();
            Echoes.Clear();
            Traces.Clear();
            Drones.Clear();
            Gameplay.Clear();
            Cues.Clear();
            RoutePts.Clear();
            NavOut.Clear();
        }
    }
}
