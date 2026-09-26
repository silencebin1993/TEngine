using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
            _d = CombatData.Create(c, capacity);
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
            };
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
            return true;
        }

        public bool ClearCommand(int id)
        {
            int i = _d.SlotOf(id);
            if (i < 0)
            {
                return false;
            }
            bool had = _d.Cmd[i].Kind != CombatCommandKind.None;
            _d.Cmd[i] = default;
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

        /// <summary>一次开火尝试（编队攻击以外的正式入口：直控点击）。<paramref name="now"/> = 当前游戏秒。</summary>
        public CombatFireResult FireAt(int attackerId, int targetId, double now)
        {
            CombatScalars s = _d.Scalars[0];
            s.Time = now;
            s.Revision++;
            _d.Scalars[0] = s;
            return CombatLogic.FireAt(ref _d, _d.SlotOf(attackerId), _d.SlotOf(targetId));
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
                CombatCommand c = _d.Cmd[i];
                Mix(ref h, (int)c.Kind);
                Mix(ref h, c.Target);
                Mix(ref h, c.Pos.x);
                Mix(ref h, c.Pos.y);
                Mix(ref h, c.AtkCd);
                Mix(ref h, c.ProgTimer);
                Mix(ref h, c.LastDist);
                Mix(ref h, c.Stuck);
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
            Mix(ref h, _d.Gameplay.Length);
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
        public byte[] Serialize()
        {
            using var ms = new MemoryStream(256 + _d.Count * 220 + _d.Projectiles.Length * 64);
            using var w = new BinaryWriter(ms);
            CombatScalars s = _d.Scalars[0];
            w.Write(CombatConst.Magic);
            w.Write(CombatConst.FormatVersion);
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
            WriteCounters(w, _d.Counters[0]);

            w.Write(_d.Weapons.Length);
            for (int i = 0; i < _d.Weapons.Length; i++)
            {
                WriteWeapon(w, _d.Weapons[i]);
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
            if (BitConverter.ToInt32(data, 4) != CombatConst.FormatVersion)
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
            CombatLoadResult result;
            try
            {
                result = Parse(data, bodyLen, ref staging);
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
            // 成功：替换内核数据（障碍来自布局，由热更层随后重设，这里沿用当前的）。
            for (int i = 0; i < _d.Obstacles.Length; i++)
            {
                staging.Obstacles.Add(_d.Obstacles[i]);
            }
            _d.Dispose();
            _d = staging;
            return CombatLoadResult.Ok;
        }

        private CombatLoadResult Parse(byte[] data, int bodyLen, ref CombatData staging)
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
            staging.Counters[0] = ReadCounters(r);

            int wn = r.ReadInt32();
            if (wn < 0 || wn > 1 << 16)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < wn; i++)
            {
                staging.Weapons.Add(ReadWeapon(r));
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
            if (ms.Position != bodyLen)
            {
                return CombatLoadResult.Truncated;
            }
            s.Revision = _d.Scalars[0].Revision + 1;
            staging.Scalars[0] = s;
            return CombatLoadResult.Ok;
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

        private static void WriteCounters(BinaryWriter w, in CombatCounters c)
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
        }

        private static CombatCounters ReadCounters(BinaryReader r) => new CombatCounters
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

        private static void WriteWeapon(BinaryWriter w, in CombatWeapon x)
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
        }

        private static CombatWeapon ReadWeapon(BinaryReader r) => new CombatWeapon
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
        }
    }
}
