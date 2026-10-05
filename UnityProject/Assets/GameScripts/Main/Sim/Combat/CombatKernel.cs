using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using BinGames.Sim.Nav;

namespace BinGames.Sim.Combat
{
    /// <summary>快照读回的结果（热更层映射成文本键；内核里没有玩家可见文本，B16）。</summary>
    public enum CombatLoadResult : byte
    {
        Ok = 0,
        Empty = 1,
        BadMagic = 2,
        /// <summary>不认识的格式版本：内核不读。热更层通知玩家、按机器与敌人记录重建这个地点的内核；
        /// 下一次存档用重建后内核的快照覆盖该地点，原快照不保留（覆盖前的整份存档由存档服务的 .bak 轮换保留一代）。</summary>
        UnknownFormat = 3,
        BadChecksum = 4,
        Truncated = 5,
        InvalidValue = 6,
    }

    /// <summary>
    /// FG0-ARCH-03 战斗内核（FG14 FGR-ARC-003）：一个地点（表面）一份。逐单位、逐弹体的 O(N) 逻辑全部在这里（AOT + Burst），
    /// 热更层只下发命令、读取汇总与事件（每步事件数有上限，与单位数无关）。
    ///
    /// - 固定步：<see cref="Step"/> 由世界模拟按统一时钟调用（60 Hz），单线程 Burst 作业，规范顺序，结果与线程 / 帧率 / 倍速 / 是否被观察无关。
    /// - 即时调用（直控点击开火、编队命令下达、查询）是托管调用同一份 <see cref="CombatLogic"/>，只在两步之间发生。
    /// - 快照：<see cref="Serialize"/> / <see cref="Load"/>，二进制 + 校验和；状态哈希 <see cref="StateHash"/> 用于确定性与存读档逐位比对。
    /// 原生容器成对释放：<see cref="Dispose"/>。
    /// </summary>
    public sealed class CombatKernel : IDisposable
    {
        private CombatData _d;
        private CombatEvent[] _drainBuffer = new CombatEvent[64];
        /// <summary>没有绑定寻路镜像时用的空格网（Burst 作业要求原生容器都已创建）。</summary>
        private NavGrid _dummyNav;
        private bool _navBound;

        public CombatKernel(in CombatConfig config, int capacity = 64)
        {
            CombatConfig c = config;
            if (c.GridCell < CombatConst.MinGridCell)
            {
                c.GridCell = CombatConfig.Default.GridCell;
            }
            if (c.MaxGameplayEventsPerStep <= 0)
            {
                c.MaxGameplayEventsPerStep = CombatConfig.Default.MaxGameplayEventsPerStep;
            }
            if (c.MaxCueEventsPerStep < 0)
            {
                c.MaxCueEventsPerStep = CombatConfig.Default.MaxCueEventsPerStep;
            }
            if (c.ProjectileCapacity <= 0)
            {
                c.ProjectileCapacity = CombatConfig.Default.ProjectileCapacity;
            }
            if (c.ProgressInterval <= 0f)
            {
                c.ProgressInterval = 1f;
            }
            if (c.MaxStuckStrikes <= 0)
            {
                c.MaxStuckStrikes = 3;
            }
            c.StatusStackCap = Math.Max(0, Math.Min(CombatConst.MaxStatusStacks, c.StatusStackCap));
            _d = CombatData.Create(c, capacity);
            _dummyNav = NavGrid.Create(8, null, false, default, null, null, 0, 1);
            _d.Nav = _dummyNav;
            LiveKernels++;
        }

        /// <summary>当前存活的内核实例数（自检用：区域切换后不泄漏）。</summary>
        public static int LiveKernels { get; private set; }

        public bool IsDisposed => !_d.IsCreated;
        public CombatConfig Config => _d.Config;
        public long Steps => _d.Scalars[0].Steps;
        public double Time => _d.Scalars[0].Time;
        public int Revision => _d.Scalars[0].Revision;
        public int SlotCount => _d.Count;
        public int ProjectileCount => _d.Projectiles.Length;
        /// <summary>FG2-FW-02：读法生成的区域 / 待结算回波 / 无人机的当前数量。</summary>
        public int ZoneCount => _d.Zones.Length;
        public int EchoCount => _d.Echoes.Length;

        /// <summary>FG2-E2E-01（FG-GAP-043）：当前留在画面上的引信弹迹条数与第 <paramref name="index"/> 条（自检核对）。</summary>
        public int TraceCount => _d.Traces.Length;

        public bool TryGetTrace(int index, out CombatShotTrace trace)
        {
            if (index < 0 || index >= _d.Traces.Length)
            {
                trace = default;
                return false;
            }
            trace = _d.Traces[index];
            return true;
        }
        public int DroneCount => _d.Drones.Length;
        public int GameplayPending => _d.Gameplay.Length;
        public int WeaponCount => _d.Weapons.Length;
        public int ProfileCount => _d.Profiles.Length;
        public CombatCounters Counters => _d.Counters[0];
        public double LastStepMs { get; private set; }

        internal ref CombatData Data => ref _d;

        public void Dispose()
        {
            if (_d.IsCreated)
            {
                _d.Dispose();
                LiveKernels--;
            }
            if (_dummyNav.IsCreated)
            {
                _dummyNav.Dispose();
            }
            DisposeCoverage();
            _navBound = false;
        }

        // ─────────────────────────────── 寻路（FG0-ARCH-06）───────────────────────────────

        /// <summary>是否绑定了寻路镜像（家园）。</summary>
        public bool NavBound => _navBound;

        /// <summary>绑定寻路内核的通行格网镜像（归寻路内核所有；解绑或寻路内核释放前必须先解绑）。</summary>
        public void BindNav(NavGrid mirror)
        {
            _d.Nav = mirror;
            _navBound = mirror.IsCreated;
        }

        public void UnbindNav()
        {
            _d.Nav = _dummyNav;
            _navBound = false;
        }

        /// <summary>待交给寻路内核的请求数（本步新发出）。</summary>
        public int NavPendingCount => _d.NavOut.Length;

        private void ResetNav(int i, bool keepRoute = false)
        {
            if (!keepRoute)
            {
                CombatScalars s = _d.Scalars[0];
                s.RouteGarbage += _d.RouteLen[i];
                _d.Scalars[0] = s;
                _d.RouteLen[i] = 0;
                _d.RouteIdx[i] = 0;
            }
            _d.NavSt[i] = (byte)CombatNavState.None;
            _d.NavFail[i] = 0;
        }

        /// <summary>
        /// 寻路内核交回一条结果：单位还在、还在等这条（序号一致）才采纳；否则丢弃（命令已换、单位已死、结果过期）。
        /// 路线终点：终点格就是目标所在格时用目标点本身（工作赶路对齐到精确位置），否则用就近可达格的格心。
        /// </summary>
        public bool ApplyRoute(int unitId, int serial, NavStatus status, NavFailReason reason, NativeArray<int2> pts, int from, int count, int2 end)
        {
            int i = _d.SlotOf(unitId);
            if (i < 0 || !_d.IsAlive(i) || _d.NavSt[i] != (byte)CombatNavState.Awaiting || _d.NavSerial[i] != serial)
            {
                return false;
            }
            if (status == NavStatus.Ok || status == NavStatus.Partial)
            {
                CombatScalars s = _d.Scalars[0];
                s.RouteGarbage += _d.RouteLen[i];
                s.Revision++;
                _d.Scalars[0] = s;
                _d.RouteOff[i] = _d.RoutePts.Length;
                for (int k = 0; k < count; k++)
                {
                    _d.RoutePts.Add(pts[from + k]);
                }
                _d.RouteLen[i] = count;
                _d.RouteIdx[i] = 0;
                double2 target = _d.Behavior[i] == (byte)CombatBehavior.Raider ? _d.Home[i] : _d.Cmd[i].Pos;
                int2 tc = CombatLogic.CellOf(target);
                _d.RouteEnd[i] = tc.x == end.x && tc.y == end.y ? target : new double2(end.x, end.y);
                _d.NavSt[i] = (byte)CombatNavState.Following;
                _d.NavFail[i] = (byte)(status == NavStatus.Partial ? reason : NavFailReason.None);
            }
            else
            {
                _d.NavSt[i] = (byte)CombatNavState.Failed;
                _d.NavFail[i] = (byte)reason;
            }
            return true;
        }

        /// <summary>寻路快照读不了时：所有“等路线”的单位改为重新要路线（下一次推进时发新请求，旧序号作废）。返回单位数。</summary>
        public int ReissueAwaiting()
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.NavSt[i] == (byte)CombatNavState.Awaiting)
                {
                    _d.NavSt[i] = (byte)CombatNavState.NeedRoute;
                    n++;
                }
            }
            return n;
        }

        /// <summary>地形变化后检查全部“沿路线走”的单位：剩余路线被挡的改为重新要路线。返回失效数。</summary>
        public int InvalidateBlockedRoutes()
        {
            if (!_navBound)
            {
                return 0;
            }
            var result = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new CombatNavInvalidateJob { D = _d, Out = result }.Run();
                return result[0];
            }
            finally
            {
                result.Dispose();
            }
        }

        public bool TryGetNavState(int id, out CombatNavState state, out NavFailReason fail)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                state = CombatNavState.None;
                fail = NavFailReason.None;
                return false;
            }
            state = (CombatNavState)_d.NavSt[i];
            fail = (NavFailReason)_d.NavFail[i];
            return true;
        }

        /// <summary>沿路线剩余的长度（米）；没有在沿路线走时返回 -1。</summary>
        public double RemainingRoute(int id)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || _d.NavSt[i] != (byte)CombatNavState.Following)
            {
                return -1;
            }
            return CombatLogic.RemainingRouteLength(ref _d, i);
        }

        /// <summary>复制单位剩余的路线（从当前要走向的路点起，末项 = 精确终点）。没有路线返回 0。</summary>
        public int CopyRoute(int id, List<double2> into)
        {
            into.Clear();
            int i = _d.SlotOf(id);
            if (i < 0 || _d.NavSt[i] != (byte)CombatNavState.Following)
            {
                return 0;
            }
            int len = _d.RouteLen[i];
            for (int k = _d.RouteIdx[i]; k < len - 1; k++)
            {
                int2 c = _d.RoutePts[_d.RouteOff[i] + k];
                into.Add(new double2(c.x, c.y));
            }
            into.Add(_d.RouteEnd[i]);
            return into.Count;
        }

        // ─────────────────────────────── 表 ───────────────────────────────

        public int AddWeapon(in CombatWeapon w)
        {
            _d.Weapons.Add(w);
            return _d.Weapons.Length - 1;
        }

        public void SetWeaponTable(int index, in CombatWeapon w)
        {
            if (index >= 0 && index < _d.Weapons.Length)
            {
                _d.Weapons[index] = w;
            }
        }

        public bool TryGetWeapon(int index, out CombatWeapon w)
        {
            if (index >= 0 && index < _d.Weapons.Length)
            {
                w = _d.Weapons[index];
                return true;
            }
            w = default;
            return false;
        }

        public int AddProfile(in CombatBehaviorProfile p)
        {
            _d.Profiles.Add(p);
            return _d.Profiles.Length - 1;
        }

        public void SetObstacles(IReadOnlyList<float3> obstacles)
        {
            _d.Obstacles.Clear();
            if (obstacles == null)
            {
                return;
            }
            for (int i = 0; i < obstacles.Count; i++)
            {
                _d.Obstacles.Add(obstacles[i]);
            }
        }

        /// <summary>设置兴趣点（x, y, 发现半径）。已发现标记保留长度相同的前缀（读档后重设不会重复发现）。</summary>
        public void SetPois(IReadOnlyList<float3> pois, IReadOnlyList<bool> alreadyReached = null)
        {
            _d.Pois.Clear();
            _d.PoiReached.Clear();
            if (pois == null)
            {
                return;
            }
            for (int i = 0; i < pois.Count; i++)
            {
                _d.Pois.Add(pois[i]);
                _d.PoiReached.Add((byte)(alreadyReached != null && i < alreadyReached.Count && alreadyReached[i] ? 1 : 0));
            }
        }

        public bool IsPoiReached(int index) => index >= 0 && index < _d.PoiReached.Length && _d.PoiReached[index] != 0;

        public void SetEngage(int targetId, float range, float interval)
        {
            CombatScalars s = _d.Scalars[0];
            s.EngageTarget = targetId;
            s.EngageRange = range;
            s.EngageInterval = interval;
            _d.Scalars[0] = s;
        }

        public void SetDirectClamp(double minY, double maxY)
        {
            CombatScalars s = _d.Scalars[0];
            s.DirectMinY = minY;
            s.DirectMaxY = maxY;
            _d.Scalars[0] = s;
        }

        /// <summary>FG5-RND-03：场地边界（世界 XZ 轴对齐矩形）。开着时每步末尾：单位与无人机按自身半径钳进矩形（击退、牵引、追击、
        /// 巡逻都出不去），飞出矩形的弹体作废。O(单位 + 弹体 + 无人机)，只在 AOT 内核里。运行时设置，不进存档。</summary>
        public void SetArena(double2 min, double2 max)
        {
            CombatScalars s = _d.Scalars[0];
            s.ArenaOn = 1;
            s.ArenaMin = math.min(min, max);
            s.ArenaMax = math.max(min, max);
            _d.Scalars[0] = s;
        }

        public void ClearArena()
        {
            CombatScalars s = _d.Scalars[0];
            s.ArenaOn = 0;
            _d.Scalars[0] = s;
        }

        public bool TryGetArena(out double2 min, out double2 max)
        {
            CombatScalars s = _d.Scalars[0];
            min = s.ArenaMin;
            max = s.ArenaMax;
            return s.ArenaOn != 0;
        }

        // ─────────────────────────────── 单位 ───────────────────────────────

        public int Spawn(in CombatSpawn spawn)
        {
            CombatScalars s = _d.Scalars[0];
            int id = s.NextId++;
            s.Revision++;
            _d.Scalars[0] = s;
            _d.Append(spawn, id);
            return id;
        }

        /// <summary>移除一个单位（保持其余单位的相对顺序）。它发出的弹体照常飞。</summary>
        public bool Despawn(int id)
        {
            int slot = _d.SlotOf(id);
            if (slot < 0)
            {
                return false;
            }
            _d.Id[slot] = 0;
            _d.Flags[slot] = 0;
            _d.SlotOfId[id] = -1;
            CombatLogic.Compact(ref _d);
            return true;
        }

        /// <summary>一次移除全部匿名单位（外部键 -1：突袭者、炮塔原型），一次压实。返回移除个数。</summary>
        public int DespawnAnonymous()
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.ExtKey[i] == -1 && _d.Id[i] > 0)
                {
                    _d.SlotOfId[_d.Id[i]] = -1;
                    _d.Id[i] = 0;
                    _d.Flags[i] = 0;
                    n++;
                }
            }
            if (n > 0)
            {
                CombatLogic.Compact(ref _d);
            }
            return n;
        }

        public bool Exists(int id) => _d.SlotOf(id) >= 0;

        public bool IsAlive(int id)
        {
            int slot = _d.SlotOf(id);
            return slot >= 0 && _d.IsAlive(slot);
        }

        public bool TryGetUnit(int id, out CombatUnitView v)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                v = default;
                return false;
            }
            v = View(i);
            return true;
        }

        public CombatUnitView ViewAt(int slot) => View(slot);

        private CombatUnitView View(int i)
        {
            CombatCommand cmd = _d.Cmd[i];
            return new CombatUnitView
            {
                Id = _d.Id[i],
                ExtKey = _d.ExtKey[i],
                Kind = (CombatUnitKind)_d.Kind[i],
                Faction = (CombatFaction)_d.Faction[i],
                Behavior = (CombatBehavior)_d.Behavior[i],
                Flags = (CombatUnitFlags)_d.Flags[i],
                Position = _d.Pos[i],
                PrevPosition = _d.Prev[i],
                Health = _d.Hp[i],
                MaxHealth = _d.MaxHp[i],
                Heat = _d.Heat[i],
                AimReadyAt = _d.AimReadyAt[i],
                NextFireAt = _d.NextFireAt[i],
                Cycle = _d.Cycle[i],
                Secondary = _d.Secondary[i],
                Command = cmd.Kind,
                CommandTarget = cmd.Target,
                CommandPos = cmd.Pos,
                MarkedUntil = _d.MarkedUntil[i],
                Weapon = _d.Weapon[i],
                Ammo = _d.Ammo[i],
                Facing = _d.Armor[i].zw,
            };
        }

        // ─────────────────────────────── FG6-DEF-01 炮塔 ───────────────────────────────

        /// <summary>FG6-DEF-01（FGR-DEF-004）：设置单位的补给存量（按发；热更层从管线消费者的缓存装填）。返回 false = 单位不存在或值不合法。</summary>
        public bool SetAmmo(int id, float ammo)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !(ammo >= 0f) || float.IsInfinity(ammo))
            {
                return false;
            }
            _d.Ammo[i] = ammo;
            Touch();
            return true;
        }

        /// <summary>FG6-DEF-01：单位当前补给存量（不存在 = 0）。</summary>
        public float AmmoOf(int id)
        {
            int i = _d.SlotOf(id);
            return i < 0 ? 0f : _d.Ammo[i];
        }

        /// <summary>FG6-DEF-01（FGR-DEF-003“最高威胁”）：单位的威胁（与选目标同一个数，面板 / 自检读）。不存在 = 0。</summary>
        public float ThreatOfUnit(int id)
        {
            int i = _d.SlotOf(id);
            return i < 0 ? 0f : CombatLogic.ThreatOf(ref _d, i);
        }

        /// <summary>FG6-DEF-01：设置单位的装甲（减伤比例、覆盖半角；炮塔座等级的“更结实”= 半角 180° 的全方位减伤）。朝向不变。</summary>
        public bool SetArmor(int id, float fraction, float halfAngleDeg)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !(fraction >= 0f) || fraction >= 1f || !(halfAngleDeg >= 0f))
            {
                return false;
            }
            float4 a = _d.Armor[i];
            _d.Armor[i] = new float4(fraction, halfAngleDeg >= 180f ? -2f : math.cos(math.radians(halfAngleDeg)), a.z, a.w);
            Touch();
            return true;
        }

        /// <summary>FG6-DEF-01：单位当前的装甲减伤比例（不存在 = 0）。</summary>
        public float ArmorOf(int id)
        {
            int i = _d.SlotOf(id);
            return i < 0 ? 0f : _d.Armor[i].x;
        }

        /// <summary>FG6-DEF-01：设置单位朝向（炮塔建成时的初始炮口朝向；装甲朝向同一个量）。</summary>
        public bool SetFacing(int id, float2 facing)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || math.lengthsq(facing) < 1e-12f)
            {
                return false;
            }
            float2 f = math.normalize(facing);
            float4 a = _d.Armor[i];
            _d.Armor[i] = new float4(a.x, a.y, f.x, f.y);
            Touch();
            return true;
        }

        public bool TryGetPosition(int id, out double2 pos)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                pos = default;
                return false;
            }
            pos = _d.Pos[i];
            return true;
        }

        /// <summary>直接放置（传送、出生、读档修正）。<paramref name="resetPrev"/> 时插值起点也跳过去（不画“滑过去”）。</summary>
        public bool SetPosition(int id, double2 pos, bool resetPrev = true)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Pos[i] = pos;
            if (resetPrev)
            {
                _d.Prev[i] = pos;
            }
            if (_d.NavSt[i] == (byte)CombatNavState.Following || _d.NavSt[i] == (byte)CombatNavState.Awaiting || _d.RouteLen[i] > 0)
            {
                // 传送后原路线不再从脚下开始：下一次推进时从新位置重新要路线。
                ResetNav(i);
                _d.NavSt[i] = (byte)CombatNavState.NeedRoute;
            }
            Touch();
            return true;
        }

        /// <summary>血量在热更层的单位结算后回写镜像（也用于首领重置、训练靶再生）。</summary>
        public bool SetHealth(int id, float health, float maxHealth, bool alive)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Hp[i] = health;
            _d.MaxHp[i] = maxHealth;
            bool wasAlive = _d.IsAlive(i);
            _d.Set(i, CombatUnitFlags.Alive, alive);
            if (alive && !wasAlive)
            {
                _d.Set(i, CombatUnitFlags.Targetable, true);
            }
            if (!alive)
            {
                _d.Set(i, CombatUnitFlags.Targetable, false);
                _d.Cmd[i] = default;
            }
            Touch();
            return true;
        }

        public bool SetFlag(int id, CombatUnitFlags flag, bool on)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Set(i, flag, on);
            return true;
        }

        public bool SetUnitWeapon(int id, int weapon)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Weapon[i] = weapon;
            return true;
        }

        public bool SetBackHitBonus(int id, float bonus)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.BackHit[i] = bonus;
            return true;
        }

        public bool SetDirectInput(int id, float2 dir)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Direct[i] = dir;
            return true;
        }

        public bool SetMarkedUntil(int id, double until)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.MarkedUntil[i] = until;
            return true;
        }

        /// <summary>写回重炮 / 热量状态（机器跨地点移交时从记录导入）。</summary>
        public bool SetWeaponState(int id, float heat, bool overheated, double aimReadyAt, double nextFireAt)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Heat[i] = heat;
            _d.Set(i, CombatUnitFlags.Overheated, overheated);
            _d.AimReadyAt[i] = aimReadyAt;
            _d.NextFireAt[i] = nextFireAt;
            return true;
        }

        public bool SetCycle(int id, float cycle, float secondary)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            _d.Cycle[i] = cycle;
            _d.Secondary[i] = secondary;
            return true;
        }

        public bool Kill(int id, int killerId)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !_d.IsAlive(i))
            {
                return false;
            }
            CombatLogic.Kill(ref _d, i, killerId);
            Touch();
            return true;
        }

        /// <summary>直接扣血（热更层的正式伤害入口：例如地点已载入时其它系统对具名敌人造成的伤害）。事件照常产生。</summary>
        public bool Damage(int id, float amount, int attackerId)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !_d.IsAlive(i))
            {
                return false;
            }
            CombatLogic.DamageUnit(ref _d, i, amount, _d.SlotOf(attackerId));
            Touch();
            return true;
        }

        public bool Heal(int id, float amount, int healerId)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !_d.IsAlive(i))
            {
                return false;
            }
            CombatLogic.Heal(ref _d, i, amount, _d.SlotOf(healerId));
            return true;
        }

        // ─────────────────────────────── 命令 ───────────────────────────────

        /// <summary>下达命令。Guard 传 NaN 位置表示“守在当前位置”。<paramref name="pending"/>：暂停中下达（UI 显示“已排队”，第一次执行时清掉）。</summary>
        public bool IssueCommand(int id, CombatCommandKind kind, double2 pos, int targetId, float arrive, float attackRange, float attackCooldown, bool pending)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || !_d.IsAlive(i))
            {
                return false;
            }
            if (kind == CombatCommandKind.Guard && (double.IsNaN(pos.x) || double.IsNaN(pos.y)))
            {
                pos = _d.Pos[i];
            }
            // 移动中改目的地：新请求仍按固定步采纳，等待时沿旧路线继续走。
            bool keepRoute = _d.Config.NavEnabled != 0 && _d.RouteLen[i] > 0
                && IsNavMove(_d.Cmd[i].Kind) && IsNavMove(kind);
            _d.Cmd[i] = new CombatCommand
            {
                Kind = kind,
                Target = targetId,
                Pos = pos,
                Arrive = arrive,
                AttackRange = attackRange,
                AttackCooldown = attackCooldown,
                AtkCd = 0f,
                ProgTimer = _d.Config.ProgressInterval,
                HasLast = 0,
                Stuck = 0,
            };
            _d.Set(i, CombatUnitFlags.PendingCommand, pending);
            ResetNav(i, keepRoute);
            return true;
        }

        private static bool IsNavMove(CombatCommandKind kind) => kind == CombatCommandKind.Move
            || kind == CombatCommandKind.WorkMove || kind == CombatCommandKind.Retreat || kind == CombatCommandKind.Guard;

        public bool ClearCommand(int id)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            bool had = _d.Cmd[i].Kind != CombatCommandKind.None;
            _d.Cmd[i] = default;
            if (_d.Behavior[i] != (byte)CombatBehavior.Raider)
            {
                ResetNav(i);
            }
            _d.Set(i, CombatUnitFlags.PendingCommand, false);
            return had;
        }

        public bool TryGetCommand(int id, out CombatCommand cmd)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || _d.Cmd[i].Kind == CombatCommandKind.None)
            {
                cmd = default;
                return false;
            }
            cmd = _d.Cmd[i];
            return true;
        }

        // ─────────────────────────────── 即时开火与查询 ───────────────────────────────

        /// <summary>一次开火尝试（编队攻击以外的正式入口：直控点击；蓄力读法要等蓄满，见 <see cref="CombatLogic.FireDirect"/>）。<paramref name="now"/> = 当前游戏秒。</summary>
        public CombatFireResult FireAt(int attackerId, int targetId, double now)
        {
            CombatScalars s = _d.Scalars[0];
            s.Time = now;
            s.Revision++;
            _d.Scalars[0] = s;
            return CombatLogic.FireDirect(ref _d, _d.SlotOf(attackerId), _d.SlotOf(targetId));
        }

        /// <summary>瞄准锥内按槽位顺序第一个存活的指定阵营单位（Demo TryFindEnemyInAim：返回第一个满足条件的，不是最近的）。</summary>
        public int FindFirstInCone(double2 origin, float2 aimDir, float range, float halfAngleDeg, CombatFaction faction)
        {
            if (math.lengthsq(aimDir) < 1e-6f)
            {
                return 0;
            }
            float2 dir = math.normalize(aimDir);
            float cosHalf = math.cos(math.radians(halfAngleDeg));
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i) || _d.Faction[i] != (byte)faction || !_d.Has(i, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double2 to = _d.Pos[i] - origin;
                double dist = math.length(to);
                if (dist > range || dist < 0.01)
                {
                    continue;
                }
                if (math.dot(dir, (float2)(to / dist)) >= cosHalf)
                {
                    return _d.Id[i];
                }
            }
            return 0;
        }

        /// <summary>离某点最近的存活指定阵营单位（半径内；并列取槽位靠后者，同 Demo 点选）。0 = 没有。</summary>
        public int FindNearest(double2 point, float radius, CombatFaction faction)
        {
            int best = -1;
            double bestDist = radius;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i) || _d.Faction[i] != (byte)faction || !_d.Has(i, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                double dist = math.distance(point, _d.Pos[i]);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best >= 0 ? _d.Id[best] : 0;
        }

        public int CountAlive(CombatFaction faction)
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.IsAlive(i) && _d.Faction[i] == (byte)faction)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>FG5-RND-03（FGR-RND-032“各标签的覆盖率”）：指定阵营的存活、可选中单位里，身上挂着每个状态位的各有几个（<paramref name="perBit"/> 长度 ≥ 32，先清零）。
        /// 返回参与统计的单位数。逐单位扫描只在内核（AOT）里做，热更层按采样间隔调用一次。</summary>
        public int CountStatusBits(CombatFaction faction, int[] perBit)
        {
            if (perBit == null || perBit.Length < 32)
            {
                return 0;
            }
            System.Array.Clear(perBit, 0, 32);
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i) || _d.Faction[i] != (byte)faction || !_d.Has(i, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                n++;
                uint m = _d.Status[i];
                while (m != 0u)
                {
                    int b = math.tzcnt(m);
                    m &= m - 1u;
                    perBit[b]++;
                }
            }
            return n;
        }

        public int CountAlive(CombatFaction faction, CombatUnitKind kind)
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.IsAlive(i) && _d.Faction[i] == (byte)faction && _d.Kind[i] == (byte)kind)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>是否有存活的指定阵营、种类的单位越过了 y 线（铸造前哨“越线触发首领战”）。</summary>
        public bool AnyAliveBeyondY(CombatFaction faction, CombatUnitKind kind, double y)
        {
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.IsAlive(i) && _d.Faction[i] == (byte)faction && _d.Kind[i] == (byte)kind && _d.Pos[i].y > y)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>指定阵营、种类的存活单位的位置重心（没有返回 false）。</summary>
        public bool TryCentroid(CombatFaction faction, CombatUnitKind kind, out double2 centroid)
        {
            double2 sum = double2.zero;
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                if (_d.IsAlive(i) && _d.Faction[i] == (byte)faction && _d.Kind[i] == (byte)kind)
                {
                    sum += _d.Pos[i];
                    n++;
                }
            }
            centroid = n > 0 ? sum / n : double2.zero;
            return n > 0;
        }

        public bool LineOfSight(double2 from, double2 to) => CombatLogic.LineOfSight(ref _d, from, to);

        /// <summary>按模拟步里同一套规则（空间网格 + 比较规则）为单位 <paramref name="id"/> 选目标（自检用来与暴力扫描对照）。返回目标单位 ID，0 = 没有。</summary>
        public int QueryTarget(int id, float range, bool needsLos, CombatTargetMode mode)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return 0;
            }
            var grid = new CombatGrid(_d.Config.GridCell);
            grid.Build(ref _d);
            int t = CombatLogic.FindTarget(ref _d, ref grid, i, range, needsLos, mode);
            grid.Dispose();
            return t >= 0 ? _d.Id[t] : 0;
        }

        // ─────────────────────────────── 步 ───────────────────────────────

        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

        /// <summary>一个固定步。<paramref name="time"/> = 这一步开始时的游戏秒（统一时钟）。</summary>
        public void Step(float dt, double time)
        {
            if (!_d.IsCreated || dt <= 0f)
            {
                return;
            }
            long t0 = Watch.ElapsedTicks;
            Watch.Start();
            var job = new CombatStepJob { D = _d, Dt = dt, Time = time };
            job.Run();
            Watch.Stop();
            LastStepMs = (Watch.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // ─────────────────────────────── 信号覆盖（FG1-SIG-07）───────────────────────────────

        private NativeList<float3> _coverageSources;
        private NativeList<int2> _coverageChanges;

        /// <summary>
        /// FG1-SIG-07（FGR-SIG-053）：按热更层给出的“与归还核心连通的覆盖圆”（x, y = 圆心格坐标，z = 半径格数）评估每台己方机器在不在覆盖里，
        /// 写 <see cref="CombatUnitFlags.OutOfCoverage"/>；状态变了的机器追加到 <paramref name="changes"/>（x = 单位 ID，y = 1 回到覆盖 / 0 走出覆盖）。
        /// 逐单位 O(机器数 × 覆盖源数) 在 Burst 作业里做（CLAUDE.md 架构 4：逐单位 O(N) 只在 AOT）；世界模拟按游戏时间定期调用，与是否被观察无关。
        /// 已阵亡的单位不评估（保持原标志）。返回变化条数。
        /// </summary>
        public int EvaluateCoverage(IReadOnlyList<float3> sources, List<int2> changes)
        {
            changes?.Clear();
            if (!_d.IsCreated)
            {
                return 0;
            }
            if (!_coverageSources.IsCreated)
            {
                _coverageSources = new NativeList<float3>(16, Allocator.Persistent);
                _coverageChanges = new NativeList<int2>(16, Allocator.Persistent);
            }
            _coverageSources.Clear();
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    _coverageSources.Add(sources[i]);
                }
            }
            _coverageChanges.Clear();
            var job = new CombatCoverageJob { D = _d, Sources = _coverageSources, Changes = _coverageChanges };
            job.Run();
            int n = _coverageChanges.Length;
            if (changes != null)
            {
                for (int i = 0; i < n; i++)
                {
                    changes.Add(_coverageChanges[i]);
                }
            }
            return n;
        }

        /// <summary>FG1-SIG-07：这台单位是否被标为在信号覆盖之外（最近一次 <see cref="EvaluateCoverage"/> 的结果）。</summary>
        public bool IsOutOfCoverage(int id)
        {
            int slot = _d.SlotOf(id);
            return slot >= 0 && _d.Has(slot, CombatUnitFlags.OutOfCoverage);
        }

        private void DisposeCoverage()
        {
            if (_coverageSources.IsCreated)
            {
                _coverageSources.Dispose();
            }
            if (_coverageChanges.IsCreated)
            {
                _coverageChanges.Dispose();
            }
        }

        // ─────────────────────────────── 事件 ───────────────────────────────

        /// <summary>取走至多 <paramref name="max"/> 条玩法事件（按发生顺序）。剩下的留在队列里（下一步再取；进存档）。
        /// 返回的数组是内部缓冲，只在下一次调用前有效。</summary>
        public CombatEvent[] DrainGameplay(int max, out int count)
        {
            int n = math.min(math.max(0, max), _d.Gameplay.Length);
            if (_drainBuffer.Length < n)
            {
                _drainBuffer = new CombatEvent[math.max(n, _drainBuffer.Length * 2)];
            }
            for (int i = 0; i < n; i++)
            {
                _drainBuffer[i] = _d.Gameplay[i];
            }
            if (n > 0)
            {
                _d.Gameplay.RemoveRange(0, n);
            }
            if (_d.Gameplay.Length > 0)
            {
                CombatCounters c = _d.Counters[0];
                c.GameplayEventsDeferred += _d.Gameplay.Length;
                _d.Counters[0] = c;
            }
            count = n;
            return _drainBuffer;
        }

        /// <summary>本批提示事件（只读视图，<see cref="ClearCues"/> 前有效）。</summary>
        public NativeArray<CombatEvent> Cues => _d.Cues.AsArray();

        public void ClearCues()
        {
            _d.Cues.Clear();
            CombatScalars s = _d.Scalars[0];
            s.CuesThisStep = 0;
            _d.Scalars[0] = s;
        }

        private void Touch()
        {
            CombatScalars s = _d.Scalars[0];
            s.Revision++;
            _d.Scalars[0] = s;
        }

        // ─────────────────────────────── FG2-FW-02 读法查询 ───────────────────────────────

        public bool TryGetZone(int index, out CombatZone zone)
        {
            if (index < 0 || index >= _d.Zones.Length)
            {
                zone = default;
                return false;
            }
            zone = _d.Zones[index];
            return true;
        }

        public bool TryGetDrone(int index, out CombatDrone drone)
        {
            if (index < 0 || index >= _d.Drones.Length)
            {
                drone = default;
                return false;
            }
            drone = _d.Drones[index];
            return true;
        }

        /// <summary>FG5-RND-03：只读取一发在飞弹体（自检核对场地边界）。</summary>
        public bool TryGetProjectile(int index, out CombatProjectile projectile)
        {
            if (index < 0 || index >= _d.Projectiles.Length)
            {
                projectile = default;
                return false;
            }
            projectile = _d.Projectiles[index];
            return true;
        }

        public bool TryGetEcho(int index, out CombatEcho echo)
        {
            if (index < 0 || index >= _d.Echoes.Length)
            {
                echo = default;
                return false;
            }
            echo = _d.Echoes[index];
            return true;
        }

        /// <summary>单位此刻身上的状态标签（已到期的返回 0）与各效果。</summary>
        public bool TryGetStatus(int id, out uint mask, out double until, out float dps, out float slow, out float vuln)
        {
            int slot = _d.SlotOf(id);
            mask = 0u;
            until = 0;
            dps = slow = vuln = 0f;
            if (slot < 0)
            {
                return false;
            }
            if (_d.StatusActive(slot, _d.Scalars[0].Time))
            {
                mask = _d.Status[slot];
                until = _d.StatusUntil[slot];
                dps = _d.StatusDps[slot];
                slow = _d.StatusSlow[slot];
                vuln = _d.StatusVuln[slot];
            }
            return true;
        }

        /// <summary>测试 / 读档修复用：直接给单位挂状态（与读法挂的是同一条规则）。</summary>
        public bool ApplyStatus(int id, uint mask, float seconds, float dps, float slow, float vuln, int sourceId)
        {
            int slot = _d.SlotOf(id);
            if (slot < 0)
            {
                return false;
            }
            CombatLogic.ApplyStatus(ref _d, slot, mask, seconds, dps, slow, vuln, _d.SlotOf(sourceId));
            return true;
        }

        // ─────────────────────────────── FG2-FW-03 反应与叠层 ───────────────────────────────

        /// <summary>登记本地点的具名标签反应规则（热更层按 fg.TbReaction 的 priority 排好序；最多 <see cref="CombatConst.MaxReactions"/> 条，多出的丢弃并返回 false）。
        /// 规则是内容不是状态：不进快照，读档后沿用当前登记的。</summary>
        public bool SetReactionRules(CombatReactionRule[] rules)
        {
            // 已有的触发次数 / 伤害按反应的稳定键挪到新下标（表里 priority 顺序改了、表重载了，计数仍记在同一条反应上）。
            int n = CombatConst.MaxReactions;
            var keys = new int[n];
            var counts = new int[n];
            var dmgs = new float[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = _d.ReactionKey[i];
                counts[i] = _d.ReactionCount[i];
                dmgs[i] = _d.ReactionDamage[i];
            }
            _d.Reactions.Clear();
            if (rules != null)
            {
                for (int i = 0; i < rules.Length && i < CombatConst.MaxReactions; i++)
                {
                    _d.Reactions.Add(rules[i]);
                }
            }
            RemapReactionCounters(ref _d, keys, counts, dmgs, n);
            // FG2-FW-04：规则下标变了，按下标记的进给（最后位置 / 出手者 / 目标 / 敌方身上的反应伤害）清零，热更层按新的纪元重取基线。
            for (int i = 0; i < n; i++)
            {
                _d.ReactionLastPos[i] = double2.zero;
                _d.ReactionLastSource[i] = 0;
                _d.ReactionLastTarget[i] = 0;
                _d.ReactionHostileDamage[i] = 0;
            }
            FeedEpoch++;
            return rules == null || rules.Length <= CombatConst.MaxReactions;
        }

        // ─────────────────────────────── FG2-FW-04 反馈与伤害归因进给 ───────────────────────────────

        /// <summary>进给纪元：读档成功、重新登记反应规则时 +1。进给数组不进快照，热更层看到纪元变了就把当前值当新基线（不把读档前后的差当成新触发）。</summary>
        public int FeedEpoch { get; private set; }

        /// <summary>第 <paramref name="index"/> 条反应最后一次触发的位置 / 出手者单位 ID / 目标单位 ID。</summary>
        public double2 ReactionLastPosOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionLastPos[index] : double2.zero;

        public int ReactionLastSourceOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionLastSource[index] : 0;

        public int ReactionLastTargetOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionLastTarget[index] : 0;

        /// <summary>第 <paramref name="index"/> 条反应打在敌对阵营身上的累计额外伤害（伤害归因的分子；不进快照）。</summary>
        public double ReactionHostileDamageOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionHostileDamage[index] : 0;

        /// <summary>敌对阵营累计受到的伤害（精确值；伤害归因的分母；不进快照）。</summary>
        public double DamageDealtToHostile => _d.DamageDealtHostile[0];

        /// <summary>读法进给（<see cref="CombatConst.ReadingFeedZone"/> / Echo / Drone）：累计次数、最后的位置与出手者单位 ID。</summary>
        public long ReadingFeedCountOf(int kind) => kind >= 0 && kind < CombatConst.ReadingFeedKinds ? _d.ReadingFeedCount[kind] : 0;

        public double2 ReadingFeedPosOf(int kind) => kind >= 0 && kind < CombatConst.ReadingFeedKinds ? _d.ReadingFeedPos[kind] : double2.zero;

        public int ReadingFeedOwnerOf(int kind) => kind >= 0 && kind < CombatConst.ReadingFeedKinds ? _d.ReadingFeedOwner[kind] : 0;

        /// <summary>FG2-FW-04（DEBT-FG2FW03-02）：单位身上某个状态位还剩多少游戏秒（没挂 / 已到期 = 0）。悬停读数逐标签显示。</summary>
        public double StatusBitSecondsLeft(int id, int bit)
        {
            int slot = _d.SlotOf(id);
            double now = _d.Scalars[0].Time;
            if (slot < 0 || bit < 0 || bit > 31 || !_d.StatusActive(slot, now) || (_d.Status[slot] & (1u << bit)) == 0u)
            {
                return 0;
            }
            return math.max(0, _d.StatusBitUntil[slot * CombatConst.StatusBitStride + bit] - now);
        }

        /// <summary>快照里每条反应计数占的字节数（稳定键 int + 次数 int + 伤害 float；自检改坏值时按它算偏移）。</summary>
        public const int ReactionCounterEntryBytes = 12;

        /// <summary>快照反应计数块条数的合理上限（超出视为坏值）。</summary>
        private const int MaxSnapshotReactionEntries = 4096;

        /// <summary>把一组（键, 次数, 伤害）按 <paramref name="d"/> 当前登记的规则放进计数槽：有键的按键找，无键（0）的按同一下标；
        /// 没有登记规则时原样放（规则稍后登记时再按键挪）。当前规则里没有的反应，计数丢弃。</summary>
        private static void RemapReactionCounters(ref CombatData d, int[] keys, int[] counts, float[] dmgs, int n)
        {
            int max = CombatConst.MaxReactions;
            for (int i = 0; i < max; i++)
            {
                d.ReactionKey[i] = 0;
                d.ReactionCount[i] = 0;
                d.ReactionDamage[i] = 0f;
            }
            int rules = d.Reactions.Length;
            if (rules == 0)
            {
                for (int j = 0; j < n && j < max; j++)
                {
                    d.ReactionKey[j] = keys[j];
                    d.ReactionCount[j] = counts[j];
                    d.ReactionDamage[j] = dmgs[j];
                }
                return;
            }
            for (int i = 0; i < rules && i < max; i++)
            {
                int key = d.Reactions[i].Key;
                d.ReactionKey[i] = key;
                int src = -1;
                if (key != 0)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (keys[j] == key)
                        {
                            src = j;
                            break;
                        }
                    }
                }
                else if (i < n && keys[i] == 0)
                {
                    src = i;
                }
                if (src >= 0)
                {
                    d.ReactionCount[i] = counts[src];
                    d.ReactionDamage[i] = dmgs[src];
                }
            }
        }

        /// <summary>登记每个状态位的效果（32 格，fg.TbStatusTag 的 effect / amount）。</summary>
        public void SetStatusFx(CombatStatusFx[] fx)
        {
            for (int b = 0; b < 32; b++)
            {
                _d.StatusFx[b] = fx != null && b < fx.Length ? fx[b] : default;
            }
        }

        public int ReactionRuleCount => _d.Reactions.Length;

        public CombatReactionRule ReactionRule(int index) => index >= 0 && index < _d.Reactions.Length ? _d.Reactions[index] : default;

        public CombatStatusFx StatusFxOf(int bit) => bit >= 0 && bit < 32 ? _d.StatusFx[bit] : default;

        /// <summary>第 <paramref name="index"/> 条反应累计触发次数 / 反应额外伤害（进存档）。</summary>
        public int ReactionCountOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionCount[index] : 0;

        public float ReactionDamageOf(int index) => index >= 0 && index < CombatConst.MaxReactions ? _d.ReactionDamage[index] : 0f;

        /// <summary>单位身上某个状态位此刻的叠层数（没有 / 已到期 = 0）。</summary>
        public int StatusStacksOf(int id, int bit)
        {
            int slot = _d.SlotOf(id);
            if (slot < 0 || bit < 0 || bit > 30 || !_d.StatusActive(slot, _d.Scalars[0].Time) || (_d.Status[slot] & (1u << bit)) == 0u)
            {
                return 0;
            }
            return (int)((_d.StatusStacks[slot] >> (bit * 2)) & 3UL);
        }

        /// <summary>离 <paramref name="pos"/> 最近、在 <paramref name="radius"/>（另加单位半径）内的活单位 ID（悬停读数用）；<paramref name="withStatus"/> 时只找身上有标签的。没有返回 0。O(单位数)，在 AOT 内核里。</summary>
        public int PickUnit(double2 pos, float radius, bool withStatus)
        {
            double now = _d.Scalars[0].Time;
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _d.Count; i++)
            {
                if (!_d.IsAlive(i))
                {
                    continue;
                }
                if (withStatus && (!_d.StatusActive(i, now) || (_d.Status[i] & ~CombatConst.StatusBitZoneSlow) == 0u))
                {
                    continue;
                }
                double dist = math.distance(_d.Pos[i], pos);
                if (dist > radius + _d.Radius[i] || dist >= bestDist)
                {
                    continue;
                }
                bestDist = dist;
                best = _d.Id[i];
            }
            return best;
        }

        /// <summary>FGR-FW-031 头顶状态标签图标的实例化缓冲（Burst）：每个带标签的活单位按位序排出前 <paramref name="maxPerUnit"/> 个图标，
        /// 摆在单位上方一排。<paramref name="visuals"/> 32 格：x = 形状序号（&lt;0 = 这一位不画），y = 打包颜色 0xRRGGBB。</summary>
        public void PrepareStatusIcons(NativeList<CombatInstance> icons, NativeArray<float2> visuals, double2 origin, int maxPerUnit, float iconSize)
        {
            var job = new CombatStatusIconJob { D = _d, Icons = icons, Visuals = visuals, Origin = origin, MaxPerUnit = math.max(1, maxPerUnit), Size = iconSize };
            job.Run();
        }

        /// <summary>这台单位当前挂着几架无人机。</summary>
        public int DronesOf(int ownerId)
        {
            int n = 0;
            for (int q = 0; q < _d.Drones.Length; q++)
            {
                if (_d.Drones[q].Owner == ownerId)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>FG2-FW-02：区域与无人机的实例化渲染缓冲（Burst）：区域 = 半径圆片（外圈是剩余时间），无人机 = 小圆片。</summary>
        public void PrepareEffects(NativeList<CombatInstance> effects, double2 origin)
        {
            var none = new NativeArray<float2>(0, Allocator.TempJob);
            try
            {
                PrepareEffects(effects, origin, none);
            }
            finally
            {
                none.Dispose();
            }
        }

        /// <summary>FG2-VFX-02（DEBT-FG2FW02-02）：区域与无人机的实例（Burst）。<paramref name="statusVisuals"/> = 每个状态位的（图标形状, 打包颜色），
        /// 区域颜色取它挂的第一个有外观的状态位的颜色（与头顶图标同源，fg.TbStatusTag）；空数组 = 一律用阵营色。</summary>
        public void PrepareEffects(NativeList<CombatInstance> effects, double2 origin, NativeArray<float2> statusVisuals)
        {
            var job = new CombatEffectsRenderJob { D = _d, Effects = effects, Origin = origin, Visuals = statusVisuals };
            job.Run();
        }

        // ─────────────────────────────── 渲染缓冲 ───────────────────────────────

        /// <summary>重填实例化渲染缓冲（Burst）：带 <see cref="CombatUnitFlags.Instanced"/> 的存活单位与全部弹体，坐标相对 <paramref name="origin"/>。</summary>
        public void PrepareRender(NativeList<CombatInstance> units, NativeList<CombatInstance> projectiles, double2 origin)
        {
            var job = new CombatRenderJob { D = _d, Units = units, Projectiles = projectiles, Origin = origin };
            job.Run();
        }

        // ─────────────────────────────── 哈希 ───────────────────────────────

        /// <summary>状态哈希（FNV-1a 64）：步数、每个单位的全部模拟状态、弹体、兴趣点、玩法事件队列。不含渲染插值起点与提示事件。</summary>
        public ulong StateHash()
        {
            ulong h = 14695981039346656037UL;
            CombatScalars s = _d.Scalars[0];
            Mix(ref h, s.Steps);
            Mix(ref h, s.NextId);
            for (int i = 0; i < _d.Count; i++)
            {
                Mix(ref h, _d.Id[i]);
                Mix(ref h, _d.ExtKey[i]);
                Mix(ref h, _d.Flags[i] & ~(uint)CombatUnitFlags.PendingCommand);
                Mix(ref h, _d.Behavior[i]);
                Mix(ref h, _d.Pos[i].x);
                Mix(ref h, _d.Pos[i].y);
                Mix(ref h, _d.Hp[i]);
                Mix(ref h, _d.MaxHp[i]);
                Mix(ref h, _d.Weapon[i]);
                Mix(ref h, _d.Heat[i]);
                Mix(ref h, _d.AimReadyAt[i]);
                Mix(ref h, _d.NextFireAt[i]);
                Mix(ref h, _d.Cycle[i]);
                Mix(ref h, _d.Secondary[i]);
                Mix(ref h, _d.MarkedUntil[i]);
                Mix(ref h, _d.BackHit[i]);
                Mix(ref h, _d.Status[i]);
                Mix(ref h, _d.StatusUntil[i]);
                Mix(ref h, _d.StatusDps[i]);
                Mix(ref h, _d.StatusSlow[i]);
                Mix(ref h, _d.StatusVuln[i]);
                Mix(ref h, (long)_d.StatusStacks[i]);
                // FG2-FW-04（格式 5）：已挂各位自己的到期时间（没挂的位不参与，残留的旧值不影响哈希）。
                uint hb = _d.Status[i];
                while (hb != 0u)
                {
                    int b = math.tzcnt(hb);
                    hb &= hb - 1u;
                    Mix(ref h, _d.StatusBitUntil[i * CombatConst.StatusBitStride + b]);
                }
                Mix(ref h, _d.StatusZoneSlow[i]); // FG2-FW-04 修复（格式 6）
                Mix(ref h, _d.Ammo[i]); // FG6-DEF-01（格式 9）
                Mix(ref h, _d.Armor[i].z); // FG6-DEF-01：炮塔朝向（转速转出来的，属于模拟状态）
                Mix(ref h, _d.Armor[i].w);
                CombatCommand c = _d.Cmd[i];
                Mix(ref h, (int)c.Kind);
                Mix(ref h, c.Target);
                Mix(ref h, c.Pos.x);
                Mix(ref h, c.Pos.y);
                Mix(ref h, c.AtkCd);
                Mix(ref h, c.ProgTimer);
                Mix(ref h, c.LastDist);
                Mix(ref h, c.Stuck);
                Mix(ref h, _d.NavSt[i]);
                Mix(ref h, _d.NavSerial[i]);
                Mix(ref h, _d.NavFail[i]);
                Mix(ref h, _d.RouteIdx[i]);
                Mix(ref h, _d.RouteLen[i]);
                Mix(ref h, _d.RouteEnd[i].x);
                Mix(ref h, _d.RouteEnd[i].y);
                for (int k = 0; k < _d.RouteLen[i]; k++)
                {
                    int2 rp = _d.RoutePts[_d.RouteOff[i] + k];
                    Mix(ref h, rp.x);
                    Mix(ref h, rp.y);
                }
            }
            for (int p = 0; p < _d.Projectiles.Length; p++)
            {
                CombatProjectile pr = _d.Projectiles[p];
                Mix(ref h, pr.Pos.x);
                Mix(ref h, pr.Pos.y);
                Mix(ref h, pr.Vel.x);
                Mix(ref h, pr.Vel.y);
                Mix(ref h, pr.Owner);
                Mix(ref h, pr.Damage);
                Mix(ref h, pr.Life);
            }
            for (int q = 0; q < _d.PoiReached.Length; q++)
            {
                Mix(ref h, _d.PoiReached[q]);
            }
            for (int z = 0; z < _d.Zones.Length; z++)
            {
                CombatZone zn = _d.Zones[z];
                Mix(ref h, zn.Pos.x);
                Mix(ref h, zn.Pos.y);
                Mix(ref h, zn.Radius);
                Mix(ref h, zn.Until);
                Mix(ref h, zn.NextTick);
                Mix(ref h, zn.Owner);
            }
            for (int e = 0; e < _d.Echoes.Length; e++)
            {
                CombatEcho ec = _d.Echoes[e];
                Mix(ref h, ec.At);
                Mix(ref h, ec.Target);
                Mix(ref h, ec.Damage);
            }
            for (int q = 0; q < _d.Drones.Length; q++)
            {
                CombatDrone dr = _d.Drones[q];
                Mix(ref h, dr.Pos.x);
                Mix(ref h, dr.Pos.y);
                Mix(ref h, dr.Until);
                Mix(ref h, dr.NextHit);
                Mix(ref h, dr.Owner);
                Mix(ref h, (int)dr.Anchored); // FG2-VFX-02（格式 7）：定点无人机的锚点
                Mix(ref h, dr.Anchor.x);
                Mix(ref h, dr.Anchor.y);
            }
            for (int i = 0; i < CombatConst.MaxReactions; i++)
            {
                Mix(ref h, _d.ReactionCount[i]);
                Mix(ref h, _d.ReactionDamage[i]);
            }
            Mix(ref h, _d.Gameplay.Length);
            Mix(ref h, s.NextNavSerial);
            Mix(ref h, _d.NavOut.Length);
            for (int q = 0; q < _d.NavOut.Length; q++)
            {
                CombatNavRequest nr = _d.NavOut[q];
                Mix(ref h, nr.UnitId);
                Mix(ref h, nr.Serial);
                Mix(ref h, nr.Goal.x);
                Mix(ref h, nr.Goal.y);
            }
            return h;
        }

        private static void Mix(ref ulong h, long v)
        {
            for (int b = 0; b < 8; b++)
            {
                h ^= (byte)(v >> (b * 8));
                h *= 1099511628211UL;
            }
        }

        private static void Mix(ref ulong h, int v) => Mix(ref h, (long)v);
        private static void Mix(ref ulong h, uint v) => Mix(ref h, (long)v);
        private static void Mix(ref ulong h, byte v) => Mix(ref h, (long)v);
        private static void Mix(ref ulong h, double v) => Mix(ref h, BitConverter.DoubleToInt64Bits(v));
        private static void Mix(ref ulong h, float v) => Mix(ref h, (long)BitConverter.SingleToInt32Bits(v));

        // ─────────────────────────────── 快照 ───────────────────────────────

        /// <summary>整份内核状态的二进制快照（格式版本 <see cref="CombatConst.FormatVersion"/>，末尾 FNV-1a 32 校验和）。</summary>
        public byte[] Serialize() => SerializeFormat(CombatConst.FormatVersion);

        /// <summary>测试用：按较老的格式写快照（2 = FG2-FW-02 之前：没有读法、状态、区域 / 回波 / 无人机），用来验证旧档能读。</summary>
        public byte[] SerializeFormatForTests(int format) => SerializeFormat(Math.Max(2, Math.Min(format, CombatConst.FormatVersion)));

        private byte[] SerializeFormat(int format)
        {
            using var ms = new MemoryStream(256 + _d.Count * 220 + _d.Projectiles.Length * 64);
            using var w = new BinaryWriter(ms);
            CombatScalars s = _d.Scalars[0];
            w.Write(CombatConst.Magic);
            w.Write(format);
            w.Write(s.Steps);
            w.Write(s.Time);
            w.Write(s.NextId);
            // 直控的钳制线、直控输入、“正在被接入”都是玩家本帧的实时操作（只在被观察时存在），不进快照：
            // 读档后没有任何机器处于接入状态（与 Demo 读档回战略视角一致），也保证“观察不改变存档结果”。
            w.Write(-CombatConst.NoClamp);
            w.Write(CombatConst.NoClamp);
            w.Write(s.EngageTarget);
            w.Write(s.EngageRange);
            w.Write(s.EngageInterval);
            w.Write(s.EventSeq);
            WriteCounters(w, _d.Counters[0], format);

            w.Write(_d.Weapons.Length);
            for (int i = 0; i < _d.Weapons.Length; i++)
            {
                WriteWeapon(w, _d.Weapons[i], format);
            }
            w.Write(_d.Profiles.Length);
            for (int i = 0; i < _d.Profiles.Length; i++)
            {
                WriteProfile(w, _d.Profiles[i]);
            }
            w.Write(_d.Pois.Length);
            for (int i = 0; i < _d.Pois.Length; i++)
            {
                float3 p = _d.Pois[i];
                w.Write(p.x);
                w.Write(p.y);
                w.Write(p.z);
                w.Write(_d.PoiReached[i]);
            }

            w.Write(_d.Count);
            for (int i = 0; i < _d.Count; i++)
            {
                w.Write(_d.Id[i]);
                w.Write(_d.ExtKey[i]);
                w.Write(_d.Kind[i]);
                w.Write(_d.Faction[i]);
                w.Write(_d.Behavior[i]);
                w.Write(_d.Priority[i]);
                w.Write(_d.Flags[i] & ~(uint)CombatUnitFlags.Possessed);
                w.Write(_d.Pos[i].x);
                w.Write(_d.Pos[i].y);
                w.Write(_d.Home[i].x);
                w.Write(_d.Home[i].y);
                w.Write(_d.Radius[i]);
                w.Write(_d.Speed[i]);
                w.Write(_d.Hp[i]);
                w.Write(_d.MaxHp[i]);
                w.Write(_d.Weapon[i]);
                w.Write(_d.BProfile[i]);
                float4 a = _d.Armor[i];
                w.Write(a.x);
                w.Write(a.y);
                w.Write(a.z);
                w.Write(a.w);
                w.Write(_d.BackHit[i]);
                w.Write(_d.Heat[i]);
                w.Write(_d.AimReadyAt[i]);
                w.Write(_d.NextFireAt[i]);
                w.Write(_d.Cycle[i]);
                w.Write(_d.Secondary[i]);
                w.Write(_d.MarkedUntil[i]);
                CombatCommand c = _d.Cmd[i];
                w.Write((byte)c.Kind);
                w.Write(c.HasLast);
                w.Write(c.Stuck);
                w.Write(c.Target);
                w.Write(c.Pos.x);
                w.Write(c.Pos.y);
                w.Write(c.Arrive);
                w.Write(c.AttackRange);
                w.Write(c.AttackCooldown);
                w.Write(c.AtkCd);
                w.Write(c.ProgTimer);
                w.Write(c.LastDist);
                w.Write(0f);
                w.Write(0f);
                // FG0-ARCH-06（格式 2）：寻路状态与剩余路线。
                w.Write(_d.NavSt[i]);
                w.Write(_d.NavSerial[i]);
                w.Write(_d.NavFail[i]);
                w.Write(_d.RouteIdx[i]);
                w.Write(_d.RouteEnd[i].x);
                w.Write(_d.RouteEnd[i].y);
                w.Write(_d.RouteLen[i]);
                for (int k = 0; k < _d.RouteLen[i]; k++)
                {
                    int2 rp = _d.RoutePts[_d.RouteOff[i] + k];
                    w.Write(rp.x);
                    w.Write(rp.y);
                }
                // FG2-FW-02（格式 3）：状态标签。
                if (format >= 3)
                {
                    w.Write(_d.Status[i]);
                    w.Write(_d.StatusUntil[i]);
                    w.Write(_d.StatusDps[i]);
                    w.Write(_d.StatusSlow[i]);
                    w.Write(_d.StatusVuln[i]);
                    w.Write(_d.StatusSource[i]);
                }
                // FG2-FW-03（格式 4）：状态标签叠层。
                if (format >= 4)
                {
                    w.Write(_d.StatusStacks[i]);
                }
                // FG2-FW-04（格式 5）：每个已挂状态位自己的到期时间（按位序，条数 = 掩码里的位数）。
                if (format >= 5)
                {
                    uint wb = _d.Status[i];
                    while (wb != 0u)
                    {
                        int b = math.tzcnt(wb);
                        wb &= wb - 1u;
                        w.Write(_d.StatusBitUntil[i * CombatConst.StatusBitStride + b]);
                    }
                }
                // FG2-FW-04 修复（格式 6）：区域减速位自己的减速值。
                if (format >= 6)
                {
                    w.Write(_d.StatusZoneSlow[i]);
                }
                // FG6-DEF-01（格式 9）：补给存量。
                if (format >= 9)
                {
                    w.Write(_d.Ammo[i]);
                }
            }

            w.Write(_d.Projectiles.Length);
            for (int p = 0; p < _d.Projectiles.Length; p++)
            {
                CombatProjectile pr = _d.Projectiles[p];
                w.Write(pr.Pos.x);
                w.Write(pr.Pos.y);
                w.Write(pr.Vel.x);
                w.Write(pr.Vel.y);
                w.Write(pr.Owner);
                w.Write(pr.Weapon);
                w.Write(pr.Damage);
                w.Write(pr.Radius);
                w.Write(pr.Life);
                w.Write((byte)pr.Faction);
            }

            w.Write(_d.Gameplay.Length);
            for (int e = 0; e < _d.Gameplay.Length; e++)
            {
                CombatEvent ev = _d.Gameplay[e];
                w.Write((byte)ev.Kind);
                w.Write(ev.Seq);
                w.Write(ev.Code);
                w.Write(ev.Code2);
                w.Write(ev.Unit);
                w.Write(ev.Other);
                w.Write(ev.Value);
                w.Write(ev.Value2);
                w.Write(ev.Pos.x);
                w.Write(ev.Pos.y);
            }
            // FG0-ARCH-06（格式 2）：请求序号计数与本步还没交给寻路内核的请求。
            w.Write(s.NextNavSerial);
            w.Write(_d.NavOut.Length);
            for (int q = 0; q < _d.NavOut.Length; q++)
            {
                CombatNavRequest nr = _d.NavOut[q];
                w.Write(nr.UnitId);
                w.Write(nr.Serial);
                w.Write(nr.Start.x);
                w.Write(nr.Start.y);
                w.Write(nr.Goal.x);
                w.Write(nr.Goal.y);
                w.Write(nr.Class);
                w.Write(nr.Flags);
            }
            // FG2-FW-02（格式 3）：区域、回波、无人机。
            if (format >= 3)
            {
                WriteReadings(w, format);
            }
            // FG2-FW-03（格式 4）：每条反应的稳定键、触发次数与反应额外伤害（条数 + 每条 ReactionCounterEntryBytes 字节；读档按键对应，不按下标）。
            if (format >= 4)
            {
                w.Write(CombatConst.MaxReactions);
                for (int i = 0; i < CombatConst.MaxReactions; i++)
                {
                    w.Write(_d.ReactionKey[i]);
                    w.Write(_d.ReactionCount[i]);
                    w.Write(_d.ReactionDamage[i]);
                }
            }
            w.Flush();
            byte[] body = ms.ToArray();
            uint sum = Fnv32(body, body.Length);
            var result = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = (byte)sum;
            result[body.Length + 1] = (byte)(sum >> 8);
            result[body.Length + 2] = (byte)(sum >> 16);
            result[body.Length + 3] = (byte)(sum >> 24);
            return result;
        }

        private void WriteReadings(BinaryWriter w, int format)
        {
            w.Write(_d.Zones.Length);
            for (int z = 0; z < _d.Zones.Length; z++)
            {
                CombatZone zn = _d.Zones[z];
                w.Write(zn.Pos.x);
                w.Write(zn.Pos.y);
                w.Write(zn.Radius);
                w.Write(zn.Growth);
                w.Write(zn.Born);
                w.Write(zn.Until);
                w.Write(zn.NextTick);
                w.Write(zn.TickInterval);
                w.Write(zn.Dps);
                w.Write(zn.StatusMask);
                w.Write(zn.StatusSeconds);
                w.Write(zn.StatusDps);
                w.Write(zn.StatusSlow);
                w.Write(zn.StatusVuln);
                w.Write(zn.Owner);
                w.Write((byte)zn.Faction);
                if (format >= 7)
                {
                    w.Write((byte)zn.Look);
                }
            }
            w.Write(_d.Echoes.Length);
            for (int e = 0; e < _d.Echoes.Length; e++)
            {
                CombatEcho ec = _d.Echoes[e];
                w.Write(ec.At);
                w.Write(ec.Target);
                w.Write(ec.Owner);
                w.Write(ec.Damage);
                w.Write(ec.StatusMask);
                w.Write(ec.StatusSeconds);
                w.Write(ec.StatusDps);
                w.Write(ec.StatusSlow);
                w.Write(ec.StatusVuln);
            }
            w.Write(_d.Drones.Length);
            for (int q = 0; q < _d.Drones.Length; q++)
            {
                CombatDrone dr = _d.Drones[q];
                w.Write(dr.Pos.x);
                w.Write(dr.Pos.y);
                w.Write(dr.Until);
                w.Write(dr.NextHit);
                w.Write(dr.Owner);
                w.Write(dr.Weapon);
                w.Write(dr.Damage);
                w.Write(dr.Leash);
                w.Write(dr.Cooldown);
                w.Write((byte)dr.Faction);
                if (format >= 7)
                {
                    w.Write(dr.Anchored);
                    w.Write(dr.Anchor.x);
                    w.Write(dr.Anchor.y);
                }
            }
        }

        /// <summary>读格式版本（不校验其余部分）：-1 = 不是内核快照。</summary>
        public static int PeekFormat(byte[] data)
        {
            if (data == null || data.Length < 8 || BitConverter.ToUInt32(data, 0) != CombatConst.Magic)
            {
                return -1;
            }
            return BitConverter.ToInt32(data, 4);
        }

        /// <summary>从快照恢复。失败时内核保持原样不变（先完整解析到临时结构，全部通过才替换）。</summary>
        public CombatLoadResult Load(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return CombatLoadResult.Empty;
            }
            if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != CombatConst.Magic)
            {
                return CombatLoadResult.BadMagic;
            }
            int format = BitConverter.ToInt32(data, 4);
            if (format < CombatConst.MinReadableFormat || format > CombatConst.FormatVersion)
            {
                return CombatLoadResult.UnknownFormat;
            }
            int bodyLen = data.Length - 4;
            uint expect = (uint)(data[bodyLen] | (data[bodyLen + 1] << 8) | (data[bodyLen + 2] << 16) | (data[bodyLen + 3] << 24));
            if (Fnv32(data, bodyLen) != expect)
            {
                return CombatLoadResult.BadChecksum;
            }
            var staging = CombatData.Create(_d.Config, 16);
            // FG2-FW-03：反应规则与状态位效果是内容（热更层建地点时按表写入），读档沿用当前的；先放进 staging，反应计数按当前规则的键对应。
            for (int i = 0; i < _d.Reactions.Length; i++)
            {
                staging.Reactions.Add(_d.Reactions[i]);
            }
            staging.StatusFx.CopyFrom(_d.StatusFx);
            CombatLoadResult result;
            try
            {
                result = Parse(data, bodyLen, format, ref staging);
            }
            catch (EndOfStreamException)
            {
                result = CombatLoadResult.Truncated;
            }
            catch (Exception)
            {
                result = CombatLoadResult.InvalidValue;
            }
            if (result != CombatLoadResult.Ok)
            {
                staging.Dispose();
                return result;
            }
            if (format < 4)
            {
                // 格式 3 及更早没有反应计数：计数为 0，计数槽的键对齐当前登记的规则（之后触发的次数按键进存档）。
                RemapReactionCounters(ref staging, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(), 0);
            }
            // 成功：替换内核数据（障碍来自布局，由热更层随后重设，这里沿用当前的；寻路镜像属于寻路内核，沿用当前绑定）。
            staging.Nav = _d.Nav;
            for (int i = 0; i < _d.Obstacles.Length; i++)
            {
                staging.Obstacles.Add(_d.Obstacles[i]);
            }
            _d.Dispose();
            _d = staging;
            FeedEpoch++;
            return CombatLoadResult.Ok;
        }

        private CombatLoadResult Parse(byte[] data, int bodyLen, int format, ref CombatData staging)
        {
            using var ms = new MemoryStream(data, 0, bodyLen, false);
            using var r = new BinaryReader(ms);
            r.ReadUInt32();
            r.ReadInt32();
            CombatScalars s = staging.Scalars[0];
            s.Steps = r.ReadInt64();
            s.Time = r.ReadDouble();
            s.NextId = r.ReadInt32();
            s.DirectMinY = r.ReadDouble();
            s.DirectMaxY = r.ReadDouble();
            s.EngageTarget = r.ReadInt32();
            s.EngageRange = r.ReadSingle();
            s.EngageInterval = r.ReadSingle();
            s.EventSeq = r.ReadInt32();
            if (s.NextId < 1 || s.Steps < 0 || double.IsNaN(s.Time))
            {
                return CombatLoadResult.InvalidValue;
            }
            staging.Counters[0] = ReadCounters(r, format);

            int wn = r.ReadInt32();
            if (wn < 0 || wn > 1 << 16)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < wn; i++)
            {
                CombatWeapon rw = ReadWeapon(r, format);
                // FG2-VFX-02（格式 7）：布区落点 / 定点 / 反伤的取值不合理整份拒绝。
                CombatReading rr7 = rw.Reading;
                if ((byte)rr7.FieldPlacement > (byte)CombatZonePlacement.Attacker || rr7.DroneAnchored > 1
                    || !(rr7.Thorns >= 0f) || !(rr7.ThornsFlat >= 0f) || !(rr7.ThornsReach >= 0f)
                    || float.IsInfinity(rr7.Thorns) || float.IsInfinity(rr7.ThornsFlat) || float.IsInfinity(rr7.ThornsReach))
                {
                    return CombatLoadResult.InvalidValue;
                }
                // FG6-DEF-01（格式 9）：目标模式只认已定义的五种；转速 / 每发补给不能是负数或不是数。
                if ((byte)rw.TargetMode > (byte)CombatTargetMode.SiegeFirst || !(rw.TurnRate >= 0f) || float.IsInfinity(rw.TurnRate)
                    || !(rw.AmmoPerShot >= 0f) || float.IsInfinity(rw.AmmoPerShot))
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Weapons.Add(rw);
            }
            int pn = r.ReadInt32();
            if (pn < 0 || pn > 1 << 16)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < pn; i++)
            {
                staging.Profiles.Add(ReadProfile(r));
            }
            int qn = r.ReadInt32();
            if (qn < 0 || qn > 1 << 16)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < qn; i++)
            {
                staging.Pois.Add(new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                staging.PoiReached.Add(r.ReadByte());
            }

            int un = r.ReadInt32();
            if (un < 0 || un > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            var bitUntil = new System.Collections.Generic.List<double>(CombatConst.StatusBitStride);
            for (int i = 0; i < un; i++)
            {
                var sp = new CombatSpawn();
                int id = r.ReadInt32();
                sp.ExtKey = r.ReadInt32();
                sp.Kind = (CombatUnitKind)r.ReadByte();
                sp.Faction = (CombatFaction)r.ReadByte();
                sp.Behavior = (CombatBehavior)r.ReadByte();
                sp.Priority = r.ReadByte();
                sp.Flags = (CombatUnitFlags)r.ReadUInt32();
                sp.Position = new double2(r.ReadDouble(), r.ReadDouble());
                sp.Home = new double2(r.ReadDouble(), r.ReadDouble());
                sp.Radius = r.ReadSingle();
                sp.Speed = r.ReadSingle();
                sp.Health = r.ReadSingle();
                sp.MaxHealth = r.ReadSingle();
                sp.Weapon = r.ReadInt32();
                sp.BehaviorProfile = r.ReadInt32();
                float4 armor = new float4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                sp.BackHitBonus = r.ReadSingle();
                sp.Heat = r.ReadSingle();
                sp.AimReadyAt = r.ReadDouble();
                sp.NextFireAt = r.ReadDouble();
                sp.Cycle = r.ReadSingle();
                sp.Secondary = r.ReadSingle();
                double marked = r.ReadDouble();
                var cmd = new CombatCommand
                {
                    Kind = (CombatCommandKind)r.ReadByte(),
                    HasLast = r.ReadByte(),
                    Stuck = r.ReadByte(),
                    Target = r.ReadInt32(),
                    Pos = new double2(r.ReadDouble(), r.ReadDouble()),
                    Arrive = r.ReadSingle(),
                    AttackRange = r.ReadSingle(),
                    AttackCooldown = r.ReadSingle(),
                    AtkCd = r.ReadSingle(),
                    ProgTimer = r.ReadSingle(),
                    LastDist = r.ReadSingle(),
                };
                float2 dir = new float2(r.ReadSingle(), r.ReadSingle());
                byte navSt = 0;
                int navSerial = 0;
                byte navFail = 0;
                int routeIdx = 0;
                double2 routeEnd = sp.Position;
                int routeOff = staging.RoutePts.Length;
                int routeLen = 0;
                if (format >= 2)
                {
                    navSt = r.ReadByte();
                    navSerial = r.ReadInt32();
                    navFail = r.ReadByte();
                    routeIdx = r.ReadInt32();
                    routeEnd = new double2(r.ReadDouble(), r.ReadDouble());
                    routeLen = r.ReadInt32();
                    if (navSt > (byte)CombatNavState.Failed || routeLen < 0 || routeLen > 1 << 20 || routeIdx < 0 || routeIdx > routeLen || !IsFinite(routeEnd))
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int k = 0; k < routeLen; k++)
                    {
                        staging.RoutePts.Add(new int2(r.ReadInt32(), r.ReadInt32()));
                    }
                }
                uint status = 0u;
                double statusUntil = 0;
                float statusDps = 0f, statusSlow = 0f, statusVuln = 0f;
                int statusSource = 0;
                if (format >= 3)
                {
                    status = r.ReadUInt32();
                    statusUntil = r.ReadDouble();
                    statusDps = r.ReadSingle();
                    statusSlow = r.ReadSingle();
                    statusVuln = r.ReadSingle();
                    statusSource = r.ReadInt32();
                    if (double.IsNaN(statusUntil) || float.IsNaN(statusDps) || statusDps < 0f || float.IsNaN(statusSlow) || statusSlow < 0f || statusSlow > 1f
                        || float.IsNaN(statusVuln) || statusVuln < 0f)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                }
                // FG2-FW-03（格式 4）：叠层；更老的快照按已有标签各 1 层。
                ulong stacks = format >= 4 ? r.ReadUInt64() : OneStackPerBit(status);
                if ((stacks & ~StackMaskOf(status)) != 0UL)
                {
                    return CombatLoadResult.InvalidValue;
                }
                // FG2-FW-04（格式 5）：各位自己的到期；更老的快照各位 = 整组到期。任何一位晚于整组到期（整组 = 最晚的一位）或不是数都拒绝。
                bitUntil.Clear();
                uint rb = status;
                while (rb != 0u)
                {
                    int b = math.tzcnt(rb);
                    rb &= rb - 1u;
                    double bu = format >= 5 ? r.ReadDouble() : statusUntil;
                    if (double.IsNaN(bu) || double.IsInfinity(bu) || bu > statusUntil)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    bitUntil.Add(bu);
                }
                // FG2-FW-04 修复（格式 6）：区域减速位自己的减速值；更老的快照按原来的算法取 StatusSlow（没有区域减速位 = 0）。
                bool hasZoneSlow = (status & CombatConst.StatusBitZoneSlow) != 0u;
                float zoneSlow = format >= 6 ? r.ReadSingle() : hasZoneSlow ? statusSlow : 0f;
                if (float.IsNaN(zoneSlow) || zoneSlow < 0f || zoneSlow > 1f || (!hasZoneSlow && zoneSlow != 0f))
                {
                    return CombatLoadResult.InvalidValue;
                }
                // FG6-DEF-01（格式 9）：补给存量；更老的快照没有 = 0。
                float ammo = format >= 9 ? r.ReadSingle() : 0f;
                if (!(ammo >= 0f) || float.IsInfinity(ammo))
                {
                    return CombatLoadResult.InvalidValue;
                }
                sp.Ammo = ammo;
                if (id <= 0 || id >= s.NextId || !IsFinite(sp.Position) || !IsFinite(sp.Home) || float.IsNaN(sp.Health)
                    || sp.Weapon >= wn || sp.BehaviorProfile >= pn || staging.SlotOf(id) >= 0)
                {
                    return CombatLoadResult.InvalidValue;
                }
                int slot = staging.Append(sp, id);
                staging.Armor[slot] = armor;
                staging.MarkedUntil[slot] = marked;
                staging.Cmd[slot] = cmd;
                staging.Direct[slot] = dir;
                staging.NavSt[slot] = navSt;
                staging.NavSerial[slot] = navSerial;
                staging.NavFail[slot] = navFail;
                staging.RouteOff[slot] = routeOff;
                staging.RouteLen[slot] = routeLen;
                staging.RouteIdx[slot] = routeIdx;
                staging.RouteEnd[slot] = routeEnd;
                staging.Status[slot] = status;
                staging.StatusUntil[slot] = statusUntil;
                staging.StatusDps[slot] = statusDps;
                staging.StatusSlow[slot] = statusSlow;
                staging.StatusVuln[slot] = statusVuln;
                staging.StatusSource[slot] = statusSource;
                staging.StatusStacks[slot] = stacks;
                staging.StatusZoneSlow[slot] = zoneSlow;
                uint sb = status;
                int bi = 0;
                while (sb != 0u)
                {
                    int b = math.tzcnt(sb);
                    sb &= sb - 1u;
                    staging.StatusBitUntil[slot * CombatConst.StatusBitStride + b] = bitUntil[bi++];
                }
            }

            int prn = r.ReadInt32();
            if (prn < 0 || prn > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int p = 0; p < prn; p++)
            {
                var pr = new CombatProjectile
                {
                    Pos = new double2(r.ReadDouble(), r.ReadDouble()),
                    Vel = new float2(r.ReadSingle(), r.ReadSingle()),
                    Owner = r.ReadInt32(),
                    Weapon = r.ReadInt32(),
                    Damage = r.ReadSingle(),
                    Radius = r.ReadSingle(),
                    Life = r.ReadSingle(),
                    Faction = (CombatFaction)r.ReadByte(),
                };
                pr.Prev = pr.Pos;
                if (!IsFinite(pr.Pos))
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Projectiles.Add(pr);
            }

            int en = r.ReadInt32();
            if (en < 0 || en > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int e = 0; e < en; e++)
            {
                staging.Gameplay.Add(new CombatEvent
                {
                    Kind = (CombatEventKind)r.ReadByte(),
                    Seq = r.ReadInt32(),
                    Code = r.ReadByte(),
                    Code2 = r.ReadByte(),
                    Unit = r.ReadInt32(),
                    Other = r.ReadInt32(),
                    Value = r.ReadSingle(),
                    Value2 = r.ReadSingle(),
                    Pos = new double2(r.ReadDouble(), r.ReadDouble()),
                });
            }
            if (format >= 2)
            {
                s.NextNavSerial = r.ReadInt32();
                int nn = r.ReadInt32();
                if (nn < 0 || nn > 1 << 20)
                {
                    return CombatLoadResult.InvalidValue;
                }
                for (int q = 0; q < nn; q++)
                {
                    staging.NavOut.Add(new CombatNavRequest
                    {
                        UnitId = r.ReadInt32(),
                        Serial = r.ReadInt32(),
                        Start = new int2(r.ReadInt32(), r.ReadInt32()),
                        Goal = new int2(r.ReadInt32(), r.ReadInt32()),
                        Class = r.ReadByte(),
                        Flags = r.ReadByte(),
                    });
                }
            }
            if (format >= 3)
            {
                CombatLoadResult rr = ParseReadings(r, ref staging, wn, format);
                if (rr != CombatLoadResult.Ok)
                {
                    return rr;
                }
            }
            if (format >= 4)
            {
                // 条数不必等于当前的 MaxReactions（以后调大 / 调小都能读）：全部读进来，按键放进当前登记的规则，多出的丢弃。
                int rn = r.ReadInt32();
                if (rn < 0 || rn > MaxSnapshotReactionEntries)
                {
                    return CombatLoadResult.InvalidValue;
                }
                var keys = new int[rn];
                var counts = new int[rn];
                var dmgs = new float[rn];
                for (int i = 0; i < rn; i++)
                {
                    int key = r.ReadInt32();
                    int count = r.ReadInt32();
                    float dmg = r.ReadSingle();
                    if (count < 0 || float.IsNaN(dmg) || float.IsInfinity(dmg))
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    keys[i] = key;
                    counts[i] = count;
                    dmgs[i] = dmg;
                }
                RemapReactionCounters(ref staging, keys, counts, dmgs, rn);
            }
            if (ms.Position != bodyLen)
            {
                return CombatLoadResult.Truncated;
            }
            s.Revision = _d.Scalars[0].Revision + 1;
            staging.Scalars[0] = s;
            return CombatLoadResult.Ok;
        }

        /// <summary>FG2-FW-02（格式 3）：区域、回波、无人机三张表。任何数值不合理整份拒绝（内核保持原样）。</summary>
        private static CombatLoadResult ParseReadings(BinaryReader r, ref CombatData staging, int weaponCount, int format)
        {
            int zn = r.ReadInt32();
            if (zn < 0 || zn > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int z = 0; z < zn; z++)
            {
                var zone = new CombatZone
                {
                    Pos = new double2(r.ReadDouble(), r.ReadDouble()),
                    Radius = r.ReadSingle(),
                    Growth = r.ReadSingle(),
                    Born = r.ReadDouble(),
                    Until = r.ReadDouble(),
                    NextTick = r.ReadDouble(),
                    TickInterval = r.ReadSingle(),
                    Dps = r.ReadSingle(),
                    StatusMask = r.ReadUInt32(),
                    StatusSeconds = r.ReadSingle(),
                    StatusDps = r.ReadSingle(),
                    StatusSlow = r.ReadSingle(),
                    StatusVuln = r.ReadSingle(),
                    Owner = r.ReadInt32(),
                    Faction = (CombatFaction)r.ReadByte(),
                };
                // FG2-VFX-02（格式 7）：区域外观（旧格式 = 液池）。
                zone.Look = format >= 7 ? (CombatZoneLook)r.ReadByte() : CombatZoneLook.Pool;
                if ((byte)zone.Look > (byte)CombatZoneLook.Residue)
                {
                    return CombatLoadResult.InvalidValue;
                }
                if (!IsFinite(zone.Pos) || !(zone.Radius >= 0f) || float.IsInfinity(zone.Radius) || !(zone.TickInterval > 0f) || float.IsNaN(zone.Dps)
                    || double.IsNaN(zone.Until) || double.IsNaN(zone.NextTick) || double.IsNaN(zone.Born) || zone.Born > zone.Until || (byte)zone.Faction > (byte)CombatFaction.Neutral)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Zones.Add(zone);
            }
            int en = r.ReadInt32();
            if (en < 0 || en > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int e = 0; e < en; e++)
            {
                var echo = new CombatEcho
                {
                    At = r.ReadDouble(),
                    Target = r.ReadInt32(),
                    Owner = r.ReadInt32(),
                    Damage = r.ReadSingle(),
                    StatusMask = r.ReadUInt32(),
                    StatusSeconds = r.ReadSingle(),
                    StatusDps = r.ReadSingle(),
                    StatusSlow = r.ReadSingle(),
                    StatusVuln = r.ReadSingle(),
                };
                if (double.IsNaN(echo.At) || float.IsNaN(echo.Damage) || echo.Damage < 0f)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Echoes.Add(echo);
            }
            int dn = r.ReadInt32();
            if (dn < 0 || dn > 1 << 20)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int q = 0; q < dn; q++)
            {
                var drone = new CombatDrone
                {
                    Pos = new double2(r.ReadDouble(), r.ReadDouble()),
                    Until = r.ReadDouble(),
                    NextHit = r.ReadDouble(),
                    Owner = r.ReadInt32(),
                    Weapon = r.ReadInt32(),
                    Damage = r.ReadSingle(),
                    Leash = r.ReadSingle(),
                    Cooldown = r.ReadSingle(),
                    Faction = (CombatFaction)r.ReadByte(),
                };
                drone.Prev = drone.Pos;
                // FG2-VFX-02（格式 7）：定点无人机（哨戒桩）的锚点（旧格式 = 伴飞、无锚点）。
                if (format >= 7)
                {
                    drone.Anchored = r.ReadByte();
                    drone.Anchor = new double2(r.ReadDouble(), r.ReadDouble());
                    if (drone.Anchored > 1 || !IsFinite(drone.Anchor))
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                }
                if (!IsFinite(drone.Pos) || double.IsNaN(drone.Until) || drone.Weapon >= weaponCount || float.IsNaN(drone.Damage) || drone.Damage < 0f
                    || !(drone.Cooldown > 0f) || (byte)drone.Faction > (byte)CombatFaction.Neutral)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Drones.Add(drone);
            }
            return CombatLoadResult.Ok;
        }

        /// <summary>每个已挂标签 1 层（读格式 1～3 的旧快照）。</summary>
        private static ulong OneStackPerBit(uint status)
        {
            ulong st = 0UL;
            for (int b = 0; b < 31; b++)
            {
                if ((status & (1u << b)) != 0u)
                {
                    st |= 1UL << (b * 2);
                }
            }
            return st;
        }

        /// <summary>这组标签允许出现叠层的比特位（每个已挂标签两位）。</summary>
        private static ulong StackMaskOf(uint status)
        {
            ulong m = 0UL;
            for (int b = 0; b < 31; b++)
            {
                if ((status & (1u << b)) != 0u)
                {
                    m |= 3UL << (b * 2);
                }
            }
            return m;
        }

        private static bool IsFinite(double2 v) => !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsInfinity(v.x) || double.IsInfinity(v.y));

        private static uint Fnv32(byte[] data, int length)
        {
            uint h = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            return h;
        }

        private static void WriteCounters(BinaryWriter w, in CombatCounters c, int format)
        {
            w.Write(c.Steps);
            w.Write(c.ShotsFired);
            w.Write(c.ProjectilesSpawned);
            w.Write(c.ProjectilesHit);
            w.Write(c.ProjectilesExpired);
            w.Write(c.ProjectilesRefused);
            w.Write(c.KillsPlayer);
            w.Write(c.KillsHostile);
            w.Write(c.DamageToPlayer);
            w.Write(c.DamageToHostile);
            w.Write(c.CuesDropped);
            w.Write(c.GameplayEventsDeferred);
            w.Write(c.Compactions);
            if (format >= 3)
            {
                w.Write(c.ZonesSpawned);
                w.Write(c.EchoesQueued);
                w.Write(c.DronesLaunched);
                w.Write(c.ReadingRefused);
            }
            if (format >= 4)
            {
                w.Write(c.ReactionsFired);
            }
        }

        private static CombatCounters ReadCounters(BinaryReader r, int format)
        {
            CombatCounters c = ReadCountersV1(r);
            if (format >= 3)
            {
                c.ZonesSpawned = r.ReadInt64();
                c.EchoesQueued = r.ReadInt64();
                c.DronesLaunched = r.ReadInt64();
                c.ReadingRefused = r.ReadInt64();
            }
            if (format >= 4)
            {
                c.ReactionsFired = r.ReadInt64();
            }
            return c;
        }

        private static CombatCounters ReadCountersV1(BinaryReader r) => new CombatCounters
        {
            Steps = r.ReadInt64(),
            ShotsFired = r.ReadInt64(),
            ProjectilesSpawned = r.ReadInt64(),
            ProjectilesHit = r.ReadInt64(),
            ProjectilesExpired = r.ReadInt64(),
            ProjectilesRefused = r.ReadInt64(),
            KillsPlayer = r.ReadInt64(),
            KillsHostile = r.ReadInt64(),
            DamageToPlayer = r.ReadInt64(),
            DamageToHostile = r.ReadInt64(),
            CuesDropped = r.ReadInt64(),
            GameplayEventsDeferred = r.ReadInt64(),
            Compactions = r.ReadInt64(),
        };

        private static void WriteWeapon(BinaryWriter w, in CombatWeapon x, int format = CombatConst.FormatVersion)
        {
            w.Write((byte)x.Mode);
            w.Write((byte)x.Reaction);
            w.Write(x.HasOutput);
            w.Write((byte)x.TargetMode);
            w.Write(x.Range);
            w.Write(x.Damage);
            w.Write(x.Cooldown);
            w.Write(x.AimSeconds);
            w.Write(x.ProjectileSpeed);
            w.Write(x.ProjectileRadius);
            w.Write(x.ProjectileLife);
            w.Write(x.HeatPerShot);
            w.Write(x.OverloadExtraHeat);
            w.Write(x.OverheatAt);
            w.Write(x.RecoverBelow);
            w.Write(x.Dissipation);
            w.Write(x.HeatSinkBonus);
            w.Write(x.PierceBonus);
            w.Write(x.MarkSeconds);
            w.Write(x.JumpRange);
            w.Write(x.JumpFalloff);
            w.Write(x.JumpMax);
            if (format >= 3)
            {
                WriteReading(w, x.Reading);
            }
            // FG2-VFX-02（格式 7）：布区落点、无人机定点、反伤。
            if (format >= 7)
            {
                w.Write((byte)x.Reading.FieldPlacement);
                w.Write(x.Reading.DroneAnchored);
                w.Write(x.Reading.Thorns);
                w.Write(x.Reading.ThornsFlat);
                w.Write(x.Reading.ThornsReach);
            }
            // FG2-E2E-01（格式 8，FG-GAP-043）：引信弹迹标记。
            if (format >= 8)
            {
                w.Write(x.FuseTrace);
            }
            // FG6-DEF-01（格式 9）：炮塔转速、每发补给。
            if (format >= 9)
            {
                w.Write(x.TurnRate);
                w.Write(x.AmmoPerShot);
            }
        }

        /// <summary>FG2-FW-02：武器的读法参数（格式 3；逐字段，顺序即格式）。</summary>
        private static void WriteReading(BinaryWriter w, in CombatReading x)
        {
            w.Write((byte)x.Carrier);
            w.Write((byte)x.ZonePlacement);
            w.Write(x.Approach);
            w.Write(x.Cone);
            w.Write(x.Area);
            w.Write(x.FieldSeconds);
            w.Write(x.FieldDpsRatio);
            w.Write(x.Drones);
            w.Write(x.DroneSeconds);
            w.Write(x.DroneRatio);
            w.Write(x.DroneCooldown);
            w.Write(x.DroneLeash);
            w.Write(x.DamageScale);
            w.Write(x.CooldownScale);
            w.Write(x.ExtraHits);
            w.Write(x.ExtraRatio);
            w.Write(x.ExtraRadius);
            w.Write(x.PierceHits);
            w.Write(x.PierceRange);
            w.Write(x.PierceRatio);
            w.Write(x.ChainHits);
            w.Write(x.ChainRange);
            w.Write(x.ChainFalloff);
            w.Write(x.BlastRadius);
            w.Write(x.BlastRatio);
            w.Write(x.SweepRadius);
            w.Write(x.SweepRatio);
            w.Write(x.EchoCount);
            w.Write(x.EchoDelay);
            w.Write(x.EchoRatio);
            w.Write(x.PullStrength);
            w.Write(x.PullRadius);
            w.Write(x.PullToAttacker);
            w.Write(x.Knockback);
            w.Write(x.Lunge);
            w.Write(x.ArmorPierce);
            w.Write(x.ExecuteBelow);
            w.Write(x.Lifesteal);
            w.Write(x.StatusAmp);
            w.Write(x.StatusMask);
            w.Write(x.StatusSeconds);
            w.Write(x.StatusDps);
            w.Write(x.StatusSlow);
            w.Write(x.StatusVuln);
            w.Write(x.ZoneRadius);
            w.Write(x.ZoneSeconds);
            w.Write(x.ZoneDps);
            w.Write(x.ZoneGrowth);
            w.Write(x.ZoneTickScale);
            w.Write(x.WeaveRadius);
            w.Write(x.WeaveSeconds);
            w.Write(x.WeaveSlow);
            w.Write(x.WeaveDpsRatio);
            w.Write(x.EscortDrones);
            w.Write(x.HeatBurstAt);
            w.Write(x.HeatBurstRadius);
            w.Write(x.HeatBurstRatio);
            w.Write(x.JumpRange);
            w.Write(x.JumpFalloff);
            w.Write(x.JumpMax);
        }

        private static CombatReading ReadReading(BinaryReader r) => new CombatReading
        {
            Carrier = (CombatCarrier)r.ReadByte(),
            ZonePlacement = (CombatZonePlacement)r.ReadByte(),
            Approach = r.ReadSingle(),
            Cone = r.ReadSingle(),
            Area = r.ReadSingle(),
            FieldSeconds = r.ReadSingle(),
            FieldDpsRatio = r.ReadSingle(),
            Drones = r.ReadInt32(),
            DroneSeconds = r.ReadSingle(),
            DroneRatio = r.ReadSingle(),
            DroneCooldown = r.ReadSingle(),
            DroneLeash = r.ReadSingle(),
            DamageScale = r.ReadSingle(),
            CooldownScale = r.ReadSingle(),
            ExtraHits = r.ReadInt32(),
            ExtraRatio = r.ReadSingle(),
            ExtraRadius = r.ReadSingle(),
            PierceHits = r.ReadInt32(),
            PierceRange = r.ReadSingle(),
            PierceRatio = r.ReadSingle(),
            ChainHits = r.ReadInt32(),
            ChainRange = r.ReadSingle(),
            ChainFalloff = r.ReadSingle(),
            BlastRadius = r.ReadSingle(),
            BlastRatio = r.ReadSingle(),
            SweepRadius = r.ReadSingle(),
            SweepRatio = r.ReadSingle(),
            EchoCount = r.ReadInt32(),
            EchoDelay = r.ReadSingle(),
            EchoRatio = r.ReadSingle(),
            PullStrength = r.ReadSingle(),
            PullRadius = r.ReadSingle(),
            PullToAttacker = r.ReadByte(),
            Knockback = r.ReadSingle(),
            Lunge = r.ReadSingle(),
            ArmorPierce = r.ReadSingle(),
            ExecuteBelow = r.ReadSingle(),
            Lifesteal = r.ReadSingle(),
            StatusAmp = r.ReadSingle(),
            StatusMask = r.ReadUInt32(),
            StatusSeconds = r.ReadSingle(),
            StatusDps = r.ReadSingle(),
            StatusSlow = r.ReadSingle(),
            StatusVuln = r.ReadSingle(),
            ZoneRadius = r.ReadSingle(),
            ZoneSeconds = r.ReadSingle(),
            ZoneDps = r.ReadSingle(),
            ZoneGrowth = r.ReadSingle(),
            ZoneTickScale = r.ReadSingle(),
            WeaveRadius = r.ReadSingle(),
            WeaveSeconds = r.ReadSingle(),
            WeaveSlow = r.ReadSingle(),
            WeaveDpsRatio = r.ReadSingle(),
            EscortDrones = r.ReadInt32(),
            HeatBurstAt = r.ReadSingle(),
            HeatBurstRadius = r.ReadSingle(),
            HeatBurstRatio = r.ReadSingle(),
            JumpRange = r.ReadSingle(),
            JumpFalloff = r.ReadSingle(),
            JumpMax = r.ReadInt32(),
        };

        /// <summary>FG2-FW-02：武器参数的完整字节键（武器表去重用：新增字段自动算进去，不会因为漏写某个字段把两套不同的读法并成一行）。</summary>
        public static string WeaponKey(in CombatWeapon w)
        {
            using var ms = new MemoryStream(256);
            using var bw = new BinaryWriter(ms);
            WriteWeapon(bw, w);
            bw.Flush();
            return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
        }

        private static CombatWeapon ReadWeapon(BinaryReader r, int format)
        {
            CombatWeapon w = ReadWeaponV1(r);
            if (format >= 3)
            {
                w.Reading = ReadReading(r);
            }
            if (format >= 7)
            {
                w.Reading.FieldPlacement = (CombatZonePlacement)r.ReadByte();
                w.Reading.DroneAnchored = r.ReadByte();
                w.Reading.Thorns = r.ReadSingle();
                w.Reading.ThornsFlat = r.ReadSingle();
                w.Reading.ThornsReach = r.ReadSingle();
            }
            if (format >= 8)
            {
                w.FuseTrace = r.ReadByte();
            }
            if (format >= 9)
            {
                w.TurnRate = r.ReadSingle();
                w.AmmoPerShot = r.ReadSingle();
            }
            return w;
        }

        private static CombatWeapon ReadWeaponV1(BinaryReader r) => new CombatWeapon
        {
            Mode = (CombatWeaponMode)r.ReadByte(),
            Reaction = (CombatReaction)r.ReadByte(),
            HasOutput = r.ReadByte(),
            TargetMode = (CombatTargetMode)r.ReadByte(),
            Range = r.ReadSingle(),
            Damage = r.ReadSingle(),
            Cooldown = r.ReadSingle(),
            AimSeconds = r.ReadSingle(),
            ProjectileSpeed = r.ReadSingle(),
            ProjectileRadius = r.ReadSingle(),
            ProjectileLife = r.ReadSingle(),
            HeatPerShot = r.ReadSingle(),
            OverloadExtraHeat = r.ReadSingle(),
            OverheatAt = r.ReadSingle(),
            RecoverBelow = r.ReadSingle(),
            Dissipation = r.ReadSingle(),
            HeatSinkBonus = r.ReadSingle(),
            PierceBonus = r.ReadSingle(),
            MarkSeconds = r.ReadSingle(),
            JumpRange = r.ReadSingle(),
            JumpFalloff = r.ReadSingle(),
            JumpMax = r.ReadInt32(),
        };

        private static void WriteProfile(BinaryWriter w, in CombatBehaviorProfile p)
        {
            w.Write(p.Speed);
            w.Write(p.FleeTrigger);
            w.Write(p.Leash);
            w.Write(p.PatrolRadius);
            w.Write(p.PatrolFreq);
            w.Write(p.SenseRange);
            w.Write(p.CycleSeconds);
            w.Write(p.EffectSeconds);
            w.Write(p.EffectAmount);
            w.Write(p.EffectRange);
        }

        private static CombatBehaviorProfile ReadProfile(BinaryReader r) => new CombatBehaviorProfile
        {
            Speed = r.ReadSingle(),
            FleeTrigger = r.ReadSingle(),
            Leash = r.ReadSingle(),
            PatrolRadius = r.ReadSingle(),
            PatrolFreq = r.ReadSingle(),
            SenseRange = r.ReadSingle(),
            CycleSeconds = r.ReadSingle(),
            EffectSeconds = r.ReadSingle(),
            EffectAmount = r.ReadSingle(),
            EffectRange = r.ReadSingle(),
        };
    }

    /// <summary>渲染缓冲（Burst）：实例化单位与弹体。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CombatRenderJob : IJob
    {
        public CombatData D;
        public NativeList<CombatInstance> Units;
        public NativeList<CombatInstance> Projectiles;
        public double2 Origin;

        public void Execute()
        {
            Units.Clear();
            for (int i = 0; i < D.Count; i++)
            {
                if (!D.IsAlive(i) || !D.Has(i, CombatUnitFlags.Instanced))
                {
                    continue;
                }
                double2 p = D.Pos[i] - Origin;
                double2 q = D.Prev[i] - Origin;
                float hp = D.MaxHp[i] > 0f ? math.saturate(D.Hp[i] / D.MaxHp[i]) : 1f;
                Units.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y, (float)q.x, (float)q.y),
                    B = new float4(D.Radius[i], hp, D.Faction[i], D.Kind[i]),
                });
            }
            Projectiles.Clear();
            for (int k = 0; k < D.Projectiles.Length; k++)
            {
                CombatProjectile pr = D.Projectiles[k];
                double2 p = pr.Pos - Origin;
                double2 q = pr.Prev - Origin;
                Projectiles.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y, (float)q.x, (float)q.y),
                    B = new float4(pr.Radius, 1f, (float)pr.Faction, 9f),
                });
            }
            // FG2-E2E-01（FG-GAP-043）：引信弹迹 = 弹体这一路里 B.w = 10 的实例：A = (命中点, 炮口)，着色器按两端画一条不插值的亮线；
            // B.y = 剩余比例（按游戏时间淡出）。只闪光的（两端重合）不画线，由区域那一路的炮口闪光画。
            double now = D.Scalars[0].Time;
            float life = math.max(1e-3f, D.Config.FuseTraceSeconds);
            for (int k = 0; k < D.Traces.Length; k++)
            {
                CombatShotTrace tr = D.Traces[k];
                if (tr.Line == 0 || math.lengthsq(tr.To - tr.From) < 1e-6)
                {
                    continue;
                }
                double2 to = tr.To - Origin;
                double2 from = tr.From - Origin;
                float left = math.saturate((float)(1.0 - (now - tr.Born) / life));
                Projectiles.Add(new CombatInstance
                {
                    A = new float4((float)to.x, (float)to.y, (float)from.x, (float)from.y),
                    B = new float4(0.08f, left, tr.Faction, CombatConst.TraceInstanceKind),
                });
            }
        }
    }

    /// <summary>FG2-FW-02：区域与无人机的渲染缓冲（Burst）。B = (半径, 剩余比例, 阵营, 种类 20 = 区域 / 21 = 无人机)。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CombatEffectsRenderJob : IJob
    {
        public CombatData D;
        public NativeList<CombatInstance> Effects;
        public double2 Origin;
        [ReadOnly] public NativeArray<float2> Visuals;

        private static int FactionColor(CombatFaction f) =>
            f == CombatFaction.Player ? CombatConst.EffectFriendColor : f == CombatFaction.Hostile ? CombatConst.EffectFoeColor : CombatConst.EffectNeutralColor;

        public void Execute()
        {
            Effects.Clear();
            double now = D.Scalars[0].Time;
            for (int z = 0; z < D.Zones.Length; z++)
            {
                CombatZone zn = D.Zones[z];
                double2 p = zn.Pos - Origin;
                float left = (float)math.saturate((zn.Until - now) / math.max(0.001, zn.Until - zn.Born));
                // 颜色：区域挂的第一个有外观的状态位（燃烧 = 火色、腐蚀 = 酸绿……与头顶图标同源）；没有就用阵营色。
                int color = FactionColor(zn.Faction);
                uint bits = zn.StatusMask;
                while (bits != 0u)
                {
                    int b = math.tzcnt(bits);
                    bits &= bits - 1u;
                    if (b < Visuals.Length && Visuals[b].x >= 0f)
                    {
                        color = (int)Visuals[b].y;
                        break;
                    }
                }
                Effects.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y, (float)p.x, (float)p.y),
                    B = new float4(zn.Radius, left, color, CombatConst.EffectKindZone + (float)zn.Look),
                });
            }
            for (int q = 0; q < D.Drones.Length; q++)
            {
                CombatDrone dr = D.Drones[q];
                double2 p = dr.Pos - Origin;
                double2 pv = dr.Prev - Origin;
                bool post = dr.Anchored != 0;
                Effects.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y, (float)pv.x, (float)pv.y),
                    B = new float4(post ? 0.45f : 0.3f, (float)math.saturate((dr.Until - now) / 8.0), FactionColor(dr.Faction), post ? CombatConst.EffectKindPost : CombatConst.EffectKindDrone),
                });
            }
            // FG2-E2E-01（FG-GAP-043）：炮口装定闪光——弹迹起点朝目标前推一点（落在炮口，不被机身盖住），按游戏时间淡出。
            float life = math.max(1e-3f, D.Config.FuseTraceSeconds);
            for (int k = 0; k < D.Traces.Length; k++)
            {
                CombatShotTrace tr = D.Traces[k];
                double2 dir = tr.To - tr.From;
                double len = math.length(dir);
                double2 muzzle = len > 1e-3 ? tr.From + dir / len * math.min(0.9, len * 0.5) : tr.From;
                double2 p = muzzle - Origin;
                float left = math.saturate((float)(1.0 - (now - tr.Born) / life));
                Effects.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y, (float)p.x, (float)p.y),
                    B = new float4(0.45f, left, CombatConst.MuzzleFlashColor, CombatConst.MuzzleFlashKind),
                });
            }
        }
    }

    /// <summary>FG2-FW-03（FGR-FW-031）：头顶状态标签图标（Burst）。B = (边长, 形状序号, 打包颜色, 30 + 叠层)；A = 图标中心（当前 / 上一步，已按单位头顶偏移）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CombatStatusIconJob : IJob
    {
        public CombatData D;
        public NativeList<CombatInstance> Icons;
        [ReadOnly] public NativeArray<float2> Visuals;
        public double2 Origin;
        public int MaxPerUnit;
        public float Size;

        public void Execute()
        {
            Icons.Clear();
            double now = D.Scalars[0].Time;
            for (int i = 0; i < D.Count; i++)
            {
                if (!D.IsAlive(i) || !D.StatusActive(i, now))
                {
                    continue;
                }
                uint bits = D.Status[i] & ~CombatConst.StatusBitZoneSlow;
                int shown = 0;
                int total = math.countbits(bits);
                int row = math.min(total, MaxPerUnit);
                double2 p = D.Pos[i] - Origin;
                double2 q = D.Prev[i] - Origin;
                float lift = D.Radius[i] + Size * 0.9f;
                float left = -(row - 1) * Size * 0.55f;
                while (bits != 0u && shown < MaxPerUnit)
                {
                    int b = math.tzcnt(bits);
                    bits &= bits - 1u;
                    float2 v = Visuals[b];
                    if (v.x < 0f)
                    {
                        continue;
                    }
                    int stacks = (int)((D.StatusStacks[i] >> (b * 2)) & 3UL);
                    float dx = left + shown * Size * 1.1f;
                    Icons.Add(new CombatInstance
                    {
                        A = new float4((float)p.x + dx, (float)p.y + lift, (float)q.x + dx, (float)q.y + lift),
                        B = new float4(Size, v.x, v.y, 30f + math.max(1, stacks)),
                    });
                    shown++;
                }
            }
        }
    }

    /// <summary>FG1-SIG-07：己方机器在不在信号覆盖里（Burst，Run）。只写 <see cref="CombatUnitFlags.OutOfCoverage"/>，状态变了才记一条。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CombatCoverageJob : IJob
    {
        public CombatData D;
        [ReadOnly] public NativeList<float3> Sources;
        public NativeList<int2> Changes;

        public void Execute()
        {
            int n = Sources.Length;
            for (int i = 0; i < D.Count; i++)
            {
                if (!D.IsAlive(i) || D.Faction[i] != (byte)CombatFaction.Player || D.Kind[i] != (byte)CombatUnitKind.Machine)
                {
                    continue;
                }
                double2 p = D.Pos[i];
                bool covered = false;
                for (int k = 0; k < n; k++)
                {
                    float3 s = Sources[k];
                    double dx = p.x - s.x;
                    double dy = p.y - s.y;
                    if (dx * dx + dy * dy <= (double)s.z * s.z)
                    {
                        covered = true;
                        break;
                    }
                }
                bool wasOut = (D.Flags[i] & (uint)CombatUnitFlags.OutOfCoverage) != 0;
                if (covered == wasOut)
                {
                    D.Flags[i] = covered ? D.Flags[i] & ~(uint)CombatUnitFlags.OutOfCoverage : D.Flags[i] | (uint)CombatUnitFlags.OutOfCoverage;
                    Changes.Add(new int2(D.Id[i], covered ? 1 : 0));
                }
            }
        }
    }

    /// <summary>FG0-ARCH-06：地形变化后检查全部路线（Burst，Run）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatNavInvalidateJob : IJob
    {
        public CombatData D;
        public NativeArray<int> Out;

        public void Execute()
        {
            Out[0] = CombatLogic.InvalidateBlockedRoutes(ref D);
        }
    }
}
