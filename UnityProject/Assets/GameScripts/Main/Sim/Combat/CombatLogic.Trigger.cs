using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-TRIG-01（ADR-EMG-001 ③ / ⑤）：EmgHash——障碍圆与残骸圆的派生空间哈希（格 4 米，一个圆登记进它外接框覆盖的每一格）。
    /// 不进快照：障碍（<see cref="CombatKernel.SetObstacles"/>）、残骸（新增 / 读档 / 清战斗）变化时置脏，下次查询时 O(障碍 + 残骸) 重建。
    /// 阶段 2 的斑块（EMG2-MAT-01）并入同一哈希。只服务 SpawnFlight 弹体 / 子弹药的线段检测与即时命中的掩体检测（炮塔 / 突袭者现有弹体不查，回归面不扩）。
    /// </summary>
    public struct EmgHash
    {
        public const float Cell = 4f;
        public const byte KindObstacle = 0;
        public const byte KindWreck = 1;
        /// <summary>每个圆：x, y, 半径, 种类。</summary>
        public NativeList<float4> Entries;
        /// <summary>圆在障碍表 / 残骸表里的下标。</summary>
        public NativeList<int> Refs;
        public NativeParallelMultiHashMap<long, int> Cells;

        public static EmgHash Create() => new EmgHash
        {
            Entries = new NativeList<float4>(8, Allocator.Persistent),
            Refs = new NativeList<int>(8, Allocator.Persistent),
            Cells = new NativeParallelMultiHashMap<long, int>(16, Allocator.Persistent),
        };

        public void Dispose()
        {
            Entries.Dispose();
            Refs.Dispose();
            Cells.Dispose();
        }

        internal static int CellOf(double v) => (int)math.floor(v / Cell);

        internal static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
    }

    /// <summary>
    /// EMG1-TRIG-01 触发器、子载荷与根载荷实体化（FG19 FGR-EMG-031 运行时、034、035、064；FG19A 第 2.1.1 节；ADR-EMG-001 ③、第 7 节第 2 / 6 条）。
    /// - 生产点：带程序的宿主发出一条事件时（<see cref="EmgEmitHosted"/>），若宿主节点有“挂在这个事件上的触发子节点”，按子节点记一条待生成请求（母载荷参数随请求保存）；
    ///   总线处理到这条事件、触发读端被唤起且链深未到 3 时唤起请求并发 evt.split（<see cref="EmgResolvePending"/>）；链深截断 / 事件被合并的请求作废。
    /// - 生成（<see cref="StepEmgPending"/>，5d 总线之后）：先进先出；子弹 / 余波 / 无人机子弹进弹体池（伤害 = 母载荷 × emg.sub.dmg_ratio，带代号与链，Flags.Sub 跳过根读法），
    ///   子区进区域表（带程序与节点）；弹体池满 / 区域满 / 同一条链同时存活已达 emg.tree.max_payloads 时留到下一步（顺延不丢，生成时才开始计寿命）。
    /// - 根载荷实体化：武器 SpawnFlight（编译器判“需要飞行”、组件允许实体化，热更层置位）时，即时命中 / 重炮在原来当场结算的那一刻改为生成一发弹体
    ///   （<see cref="EmgFireFlight"/>，不改 Mode，重炮瞄准阶段与熔穿过载不变），命中时按原结算那一下结算（<see cref="EmgFlightHit"/>）；开火结果报“已发射”。
    /// - 子弹药只执行自己节点的指令：阶段 1 的排放指令（mat.* 写端，fg.TbPortCarrier 映射到读法驻留区 + 状态位）在子弹药每次结算（命中 / 寿命到 / 撞障碍）处落一块区域。
    /// 全部 O(N) 逻辑只在内核；没有程序的地点每处一条比较判空，行为与 StateHash 逐字不变。
    /// </summary>
    public static partial class CombatLogic
    {
        private static float EmgSubRatio(ref CombatData d) => d.Config.EmgSubDmgRatio > 0f ? d.Config.EmgSubDmgRatio : EmgConst.DefaultSubDmgRatio;

        private static int EmgMaxChildren(ref CombatData d) => d.Config.EmgMaxChildren > 0 ? math.min(d.Config.EmgMaxChildren, 64) : EmgConst.DefaultMaxChildren;

        private static int EmgMaxPayloads(ref CombatData d) => d.Config.EmgMaxPayloads > 0 ? d.Config.EmgMaxPayloads : EmgConst.DefaultMaxPayloads;

        /// <summary>子形态码缺省 = 载体 + 1（射弹 1 / 格斗 2 / 无人机 3 / 力场 4 / 布区 5，与 fg.TbSubPayload 行序一致）。</summary>
        private static byte EmgSubFormOf(byte carrier) => (byte)(math.min((int)carrier, 4) + 1);

        private static void EmgStat(ref CombatData d, int which)
        {
            EmgBusStats st = d.EmgStats[0];
            switch (which)
            {
                case 0: st.Splits++; break;
                case 1: st.SubSpawned++; break;
                case 2: st.SpawnDeferred++; break;
                case 3: st.SpawnDropped++; break;
                case 4: st.SpawnLost++; break;
                case 5: st.CoverAbsorbed++; break;
                case 6: st.ObstacleHits++; break;
                case 7: st.FlightShots++; break;
            }
            d.EmgStats[0] = st;
        }

        // ─────────────────────────────── 宿主的母载荷参数 ───────────────────────────────

        /// <summary>出手宿主（即时 / 重炮 / 格斗 / 力场 / 布区）的母载荷参数（EmgShotBegin 之后调）。</summary>
        private static void EmgFillShot(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            EmgCtx c = d.EmgCtx;
            c.Damage = math.max(0f, wp.Damage);
            c.Weapon = d.Weapon[a];
            c.OwnerId = d.Id[a];
            c.Faction = d.Faction[a];
            c.Carrier = (byte)wp.Reading.Carrier;
            c.MainTarget = t >= 0 && t < d.Count ? d.Id[t] : 0;
            c.ZoneRadius = wp.Reading.Area;
            c.ZoneLife = wp.Reading.FieldSeconds;
            d.EmgCtx = c;
        }

        private static EmgCtx EmgProjHost(ref CombatData d, in CombatProjectile pr)
        {
            byte carrier = pr.Weapon >= 0 && pr.Weapon < d.Weapons.Length ? (byte)d.Weapons[pr.Weapon].Reading.Carrier : (byte)CombatCarrier.Projectile;
            return new EmgCtx
            {
                Prog = pr.Prog, Chain = pr.Chain, Ent = pr.Id, Owner = d.SlotOf(pr.Owner), Node = pr.Node, Depth = pr.Depth, EntKind = EmgEntKind.Projectile,
                HurtSrc = (byte)EmgCode.SrcProjectile, HitCode = EmgCode.HitDirect, TrigUsed = pr.TrigUsed, Damage = pr.Damage, Weapon = pr.Weapon, OwnerId = pr.Owner,
                Faction = (byte)pr.Faction, Carrier = carrier, PFlags = pr.Flags, Flight = (byte)((pr.Flags & EmgProjFlags.Flight) != 0 ? 1 : 0),
            };
        }

        private static EmgCtx EmgUnitHost(ref CombatData d, int slot)
        {
            int w = d.Weapon[slot];
            bool hasW = w >= 0 && w < d.Weapons.Length;
            CombatWeapon wp = hasW ? d.Weapons[w] : default;
            return new EmgCtx
            {
                Prog = hasW ? wp.Program : -1, Owner = slot, Ent = d.Id[slot], EntKind = EmgEntKind.Unit, Damage = math.max(0f, wp.Damage), Weapon = hasW ? w : -1,
                OwnerId = d.Id[slot], Faction = d.Faction[slot], Carrier = (byte)wp.Reading.Carrier, ZoneRadius = wp.Reading.Area, ZoneLife = wp.Reading.FieldSeconds,
            };
        }

        private static EmgCtx EmgZoneHost(ref CombatData d, in CombatZone z)
        {
            return new EmgCtx
            {
                Prog = z.EmgProgRef - 1, Chain = z.EmgChain, Ent = z.EmgId, Owner = d.SlotOf(z.Owner), Node = z.EmgNode, Depth = z.EmgDepth, EntKind = EmgEntKind.Zone,
                HurtSrc = (byte)EmgCode.SrcZone, HitCode = EmgCode.HitZoneTick, TrigUsed = z.EmgTrigUsed, Damage = math.max(0f, z.Dps), Weapon = z.EmgWeapon, OwnerId = z.Owner,
                Faction = (byte)z.Faction, Carrier = (byte)CombatCarrier.Field, ZoneRadius = z.Radius, ZoneLife = (float)(z.Until - z.Born),
            };
        }

        private static float2 EmgDir(double2 v) => math.lengthsq(v) > 1e-12 ? (float2)math.normalize(v) : float2.zero;

        // ─────────────────────────────── 生产点：入队 + 记待生成请求 ───────────────────────────────

        /// <summary>入队一条带宿主的事件；真的入了队（没被积压保护合并）且宿主节点有挂在这个事件上的触发子节点时，按子节点记待生成请求（未唤起）。</summary>
        internal static void EmgEmitHosted(ref CombatData d, EmgEvent e, in EmgCtx host, float2 dir)
        {
            int before = d.EmgBus.Length;
            EmgEmit(ref d, e);
            if (e.Prog < 0 || e.Prog >= d.EmgPrograms.Length || d.EmgPrograms[e.Prog].NodeCount < 2 || d.EmgBus.Length != before + 1)
            {
                return;
            }
            EmgQueueChildren(ref d, d.EmgBus[before], host, dir);
        }

        private static void EmgQueueChildren(ref CombatData d, in EmgEvent e, in EmgCtx host, float2 dir)
        {
            EmgProgram p = d.EmgPrograms[e.Prog];
            int maxChildren = EmgMaxChildren(ref d);
            for (int j = 1; j < p.NodeCount; j++)
            {
                int ni = p.FirstNode + j;
                if (ni >= d.EmgNodes.Length)
                {
                    break;
                }
                EmgNode c = d.EmgNodes[ni];
                if (c.Parent != e.Node || c.TrigEvent != e.Kind || c.TrigCount == 0)
                {
                    continue;
                }
                // 格斗 / 力场：一次出手对扇形 / 圈内每个单位各算一次命中，触发只按主目标一次（FGR-EMG-034 表第 1 行、FGR-EMG-035）。
                if (e.Kind == EmgEventKind.Hit && host.EntKind == EmgEntKind.Unit
                    && (host.Carrier == (byte)CombatCarrier.Melee || host.Carrier == (byte)CombatCarrier.Aura) && e.Other != host.MainTarget)
                {
                    continue;
                }
                if (d.EmgPending.Length >= EmgConst.PendingCapacity)
                {
                    EmgStat(ref d, 4);
                    continue;
                }
                d.EmgPending.Add(new EmgSpawn
                {
                    Kind = EmgConst.PendSub, Seq = e.Seq, Node = (byte)j, Gen = c.Gen, Depth = (byte)math.min(e.Depth + 1, EmgConst.MaxDepth),
                    Count = (byte)math.min((int)c.TrigCount, maxChildren), SubForm = c.SubForm != 0 ? c.SubForm : EmgSubFormOf(host.Carrier),
                    Prog = e.Prog, Chain = e.Chain, Owner = host.OwnerId, Weapon = host.Weapon, Faction = host.Faction, Damage = host.Damage,
                    ZoneRadius = host.ZoneRadius, ZoneLife = host.ZoneLife, Flags = host.PFlags & EmgProjFlags.Heal, Pos = e.Pos, Dir = dir,
                    Other = e.Kind == EmgEventKind.Hit ? e.Other : 0, Ent = (int)e.Kind,
                });
            }
        }

        // ─────────────────────────────── 唤起（总线分派处）───────────────────────────────

        /// <summary>
        /// 总线处理完一条事件：<paramref name="arm"/> = 触发读端被唤起且链深未到 3——把由它记下的待生成请求置为已唤起（同时计一次放出、按需发 evt.split）；
        /// 否则（链深截断）作废。待生成表里“未决”的请求都排在游标之后、按 Seq 递增，游标摊还 O(1)。
        /// </summary>
        internal static void EmgResolvePending(ref CombatData d, in EmgEvent e, bool arm)
        {
            int n = d.EmgPending.Length;
            int i = math.clamp(d.EmgS[0].ArmCursor, 0, n);
            int armed = 0, count = 0;
            byte gen = 0;
            for (; i < n; i++)
            {
                EmgSpawn s = d.EmgPending[i];
                if (s.Armed != 0 || s.Count == 0 || s.Kind != EmgConst.PendSub)
                {
                    continue;
                }
                int diff = unchecked(s.Seq - e.Seq);
                if (diff > 0)
                {
                    break;
                }
                if (diff == 0 && arm)
                {
                    s.Armed = 1;
                    armed++;
                    count += s.Count;
                    gen = s.Gen;
                }
                else
                {
                    s.Count = 0; // 链深截断，或更早的事件已处理过（不该出现）：作废
                    EmgStat(ref d, 3);
                }
                d.EmgPending[i] = s;
            }
            EmgScalars es = d.EmgS[0];
            es.ArmCursor = i;
            d.EmgS[0] = es;
            if (armed == 0)
            {
                return;
            }
            EmgStat(ref d, 0);
            // evt.split：宿主 = 母载荷（Node = 父节点），Value = 份数，Code = 子代号；链深 + 1。只在宿主节点读它时入队（FGR-EMG-062）。
            if (EmgSiteListens(ref d, EmgEventKind.Split) && EmgReadSlot(ref d, e.Prog, e.Node, EmgEventKind.Split) >= 0)
            {
                EmgTryEmitChild(ref d, e, new EmgEvent
                {
                    Kind = EmgEventKind.Split, EntKind = e.EntKind, Ent = e.Ent, Prog = e.Prog, Node = e.Node, Code = gen, Value = count, Pos = e.Pos,
                });
            }
        }

        // ─────────────────────────────── 生成（5d 之后）───────────────────────────────

        /// <summary>
        /// 每步处理待生成表（先进先出）：已唤起的子载荷按份生成（弹体池满 / 区域满 / 同链同时存活已满 → 留到下一步）；即时宿主的定时到点发 evt.timer；
        /// 池满顺延的 SpawnFlight 根弹体有空位就发出。未唤起而它那条事件已经处理过的请求作废。O(待生成数 + 首次生成时的弹体数 + 区域数)。
        /// </summary>
        internal static void StepEmgPending(ref CombatData d)
        {
            int n = d.EmgPending.Length;
            if (n == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            int head = d.EmgS[0].BusHead;
            bool busEmpty = head >= d.EmgBus.Length;
            int oldest = busEmpty ? 0 : d.EmgBus[head].Seq;
            var live = new NativeParallelHashMap<int, int>(16, Allocator.Temp);
            bool liveBuilt = false;
            int write = 0;
            for (int i = 0; i < n; i++)
            {
                EmgSpawn s = d.EmgPending[i];
                if (s.Count == 0)
                {
                    continue;
                }
                if (s.Kind == EmgConst.PendSub && s.Armed == 0)
                {
                    if (busEmpty || unchecked(s.Seq - oldest) < 0)
                    {
                        EmgStat(ref d, 3); // 它那条事件已处理过却没被唤起（被积压保护合并等）：作废
                        continue;
                    }
                    d.EmgPending[write++] = s;
                    continue;
                }
                if (s.Kind == EmgConst.PendTimer)
                {
                    if (now + 1e-9 < s.At)
                    {
                        d.EmgPending[write++] = s;
                        continue;
                    }
                    EmgFireTimer(ref d, s);
                    continue;
                }
                if (!liveBuilt)
                {
                    EmgBuildLive(ref d, ref live);
                    liveBuilt = true;
                }
                if (s.Kind == EmgConst.PendRoot)
                {
                    if (!EmgSpawnRoot(ref d, s, ref live))
                    {
                        EmgStat(ref d, 2);
                        d.EmgPending[write++] = s;
                    }
                    continue;
                }
                while (s.Count > 0 && EmgSpawnSub(ref d, ref s, ref live))
                {
                    s.Count--;
                }
                if (s.Count > 0)
                {
                    EmgStat(ref d, 2);
                    d.EmgPending[write++] = s;
                }
            }
            // 定时触发在本循环里新记的请求（下标 ≥ n）搬到压缩后的尾部。
            int end = d.EmgPending.Length;
            for (int i = n; i < end; i++)
            {
                d.EmgPending[write++] = d.EmgPending[i];
            }
            d.EmgPending.ResizeUninitialized(write);
            int cursor = 0;
            while (cursor < write && (d.EmgPending[cursor].Armed != 0 || d.EmgPending[cursor].Kind != EmgConst.PendSub))
            {
                cursor++;
            }
            EmgScalars cs = d.EmgS[0];
            cs.ArmCursor = cursor;
            d.EmgS[0] = cs;
            live.Dispose();
        }

        /// <summary>同一条链此刻存活的载荷数（带程序的弹体 + 带程序的区域）：派生量，每步第一次要生成时现算，不进快照（ADR ③）。</summary>
        private static void EmgBuildLive(ref CombatData d, ref NativeParallelHashMap<int, int> live)
        {
            for (int p = 0; p < d.Projectiles.Length; p++)
            {
                CombatProjectile pr = d.Projectiles[p];
                if (pr.Prog >= 0 && pr.Chain > 0)
                {
                    EmgLiveAdd(ref live, pr.Chain);
                }
            }
            for (int z = 0; z < d.Zones.Length; z++)
            {
                CombatZone zn = d.Zones[z];
                if (zn.EmgProgRef > 0 && zn.EmgChain > 0)
                {
                    EmgLiveAdd(ref live, zn.EmgChain);
                }
            }
        }

        private static void EmgLiveAdd(ref NativeParallelHashMap<int, int> live, int chain)
        {
            live.TryGetValue(chain, out int v);
            live.Remove(chain);
            live.TryAdd(chain, v + 1);
        }

        private static int EmgLiveOf(ref NativeParallelHashMap<int, int> live, int chain) => live.TryGetValue(chain, out int v) ? v : 0;

        /// <summary>生成一份子载荷；弹体池 / 区域满或同链同时存活已满返回 false（留到下一步）。</summary>
        private static bool EmgSpawnSub(ref CombatData d, ref EmgSpawn s, ref NativeParallelHashMap<int, int> live)
        {
            if (s.Chain > 0 && EmgLiveOf(ref live, s.Chain) >= EmgMaxPayloads(ref d))
            {
                return false;
            }
            int code = s.SubForm >= 1 && s.SubForm <= 5 ? s.SubForm : EmgConst.SubBullet;
            EmgSubForm form = d.EmgSubForms[code];
            float damage = s.Damage * EmgSubRatio(ref d);
            int ownerSlot = d.SlotOf(s.Owner);
            CombatFaction faction = (CombatFaction)s.Faction;
            if (code == EmgConst.SubPulse || code == EmgConst.SubZone)
            {
                if (d.Zones.Length >= d.ZoneCap && CountZones(ref d, CombatConst.ZoneKindReading) >= d.ZoneCap)
                {
                    return false;
                }
                CombatReading r = s.Weapon >= 0 && s.Weapon < d.Weapons.Length ? d.Weapons[s.Weapon].Reading : default;
                float radius = form.RadiusRatio > 0f ? s.ZoneRadius * form.RadiusRatio : form.Radius;
                float seconds = form.LifeRatio > 0f ? s.ZoneLife * form.LifeRatio : form.Life;
                if (!(radius > 0f) || !(seconds > 0f))
                {
                    s.Count = 0; // 母落区参数缺失：生成不了，整条请求作废（不会因为等待而好转）
                    EmgStat(ref d, 3);
                    return false;
                }
                float dps, tickScale;
                if (code == EmgConst.SubPulse)
                {
                    // 小场脉冲：存续内恰好结算一次，一次的伤害 = 母载荷伤害 × 比例。
                    float interval = seconds * 0.6f;
                    tickScale = ZoneTickOf(ref d) / interval;
                    dps = damage / interval;
                }
                else
                {
                    tickScale = 1f;
                    dps = damage; // 母落区的每秒伤害 × 比例
                }
                if (!SpawnZone(ref d, ownerSlot, faction, s.Pos, radius, seconds, dps, 0f, tickScale, r.StatusMask, r.StatusSeconds, r.StatusDps, r.StatusSlow, r.StatusVuln))
                {
                    return false;
                }
                SetLastZoneLook(ref d, code == EmgConst.SubPulse ? CombatZoneLook.Pulse : CombatZoneLook.Pool);
                int last = d.Zones.Length - 1;
                CombatZone z = d.Zones[last];
                EmgScalars es = d.EmgS[0];
                z.EmgProgRef = s.Prog + 1;
                z.EmgId = es.NextZoneId++;
                d.EmgS[0] = es;
                z.EmgChain = s.Chain;
                z.EmgNode = s.Node;
                z.EmgDepth = s.Depth;
                z.EmgGen = s.Gen;
                z.EmgSub = 1;
                z.EmgWeapon = s.Weapon;
                d.Zones[last] = z;
            }
            else
            {
                if (d.Projectiles.Length >= d.Config.ProjectileCapacity)
                {
                    return false;
                }
                int k = s.Ent == (int)EmgEventKind.Hit ? EmgSeekIndex(ref d, s) : -1;
                float2 dir = k >= 0 ? EmgDir(d.Pos[k] - s.Pos) : EmgRadial(ref d, s);
                EmgScalars es = d.EmgS[0];
                int id = es.NextProjId++;
                d.EmgS[0] = es;
                var sub = new CombatProjectile
                {
                    Id = id, Prog = s.Prog, Chain = s.Chain, Node = s.Node, Gen = s.Gen, Depth = s.Depth, Flags = EmgProjFlags.Sub | (s.Flags & EmgProjFlags.Heal),
                    Pos = s.Pos, Prev = s.Pos, Vel = dir * form.Speed, Owner = s.Owner, Weapon = s.Weapon, Damage = damage, Radius = form.Radius, Life = form.Life,
                    Faction = faction, IgnoreUnit = s.Other,
                };
                EmgInitFlight(ref d, ref sub, s.Pos + (double2)(dir * form.Speed * form.Life), k >= 0 ? d.Id[k] : 0, false); // EMG1-GRM-01：子弹药按自己节点（含继承）的修饰
                d.Projectiles.Add(sub);
                CombatCounters c = d.Counters[0];
                c.ProjectilesSpawned++;
                d.Counters[0] = c;
            }
            if (s.Chain > 0)
            {
                EmgLiveAdd(ref live, s.Chain);
            }
            s.Done++;
            EmgStat(ref d, 1);
            return true;
        }

        /// <summary>命中触发的第 k 份子弹药（k = 已生成份数）指向“5 米内第 k 近的其他敌对单位”（治疗弹找己方）；不够就返回 −1（径向）。O(单位数 × (k + 1))，k ≤ 份数上限。</summary>
        private static int EmgSeekIndex(ref CombatData d, in EmgSpawn s)
        {
            int rank = math.max(0, (int)s.Done);
            bool heal = (s.Flags & EmgProjFlags.Heal) != 0;
            byte want = heal ? s.Faction : s.Faction == (byte)CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            double r2 = EmgConst.ChildSeekRadius * EmgConst.ChildSeekRadius;
            double lastD = -1;
            int lastK = -1;
            int pick = -1;
            for (int step = 0; step <= rank; step++)
            {
                pick = -1;
                double best = double.MaxValue;
                for (int u = 0; u < d.Count; u++)
                {
                    if (d.Faction[u] != want || !d.IsAlive(u) || !d.Has(u, CombatUnitFlags.Targetable) || d.Id[u] == s.Other)
                    {
                        continue;
                    }
                    double dist = math.lengthsq(d.Pos[u] - s.Pos);
                    if (dist > r2)
                    {
                        continue;
                    }
                    bool after = dist > lastD || (dist == lastD && u > lastK);
                    if (after && (dist < best || (dist == best && u < pick)))
                    {
                        best = dist;
                        pick = u;
                    }
                }
                if (pick < 0)
                {
                    return -1;
                }
                lastD = best;
                lastK = pick;
            }
            return pick;
        }

        /// <summary>径向：从母载荷方向起按份数均分一圈（母载荷没有方向时起始角取链与序号的无状态哈希，FGR-EMG-213）。</summary>
        private static float2 EmgRadial(ref CombatData d, in EmgSpawn s)
        {
            float baseAngle = math.lengthsq(s.Dir) > 1e-12f
                ? math.atan2(s.Dir.y, s.Dir.x)
                : EmgRng.Unit((uint)s.Chain, (uint)s.Seq, 0x5B1Fu, 0u) * (2f * math.PI);
            int total = math.max(1, s.Done + s.Count);
            float a = baseAngle + 2f * math.PI * s.Done / total;
            return new float2(math.cos(a), math.sin(a));
        }

        private static bool EmgSpawnRoot(ref CombatData d, in EmgSpawn s, ref NativeParallelHashMap<int, int> live)
        {
            if (s.Weapon < 0 || s.Weapon >= d.Weapons.Length)
            {
                return true; // 武器表已换（重编译）：这一发作废，不卡住队列
            }
            CombatWeapon wp = d.Weapons[s.Weapon];
            int t = d.SlotOf(s.Other);
            double2 to = t >= 0 && d.IsAlive(t) ? d.Pos[t] : s.Pos + (double2)s.Dir;
            if (t >= 0 && d.IsAlive(t) && math.lengthsq(s.Dir) > 1e-12f && EmgPellets(ref d, wp, out _, out _) > 1)
            {
                to = s.Pos + (double2)s.Dir * math.distance(d.Pos[t], s.Pos); // EMG1-GRM-01 审查修复：多份载荷排队的弹丸保留各自扇角（Dir = 出手时这一份的方向）
            }
            if (!EmgTrySpawnFlight(ref d, s.Pos, 0f, to, s.Owner, s.Weapon, wp, s.Damage, (CombatFaction)s.Faction, s.Flags, s.Chain, t >= 0 && d.IsAlive(t) ? s.Other : 0))
            {
                return false;
            }
            if (s.Chain > 0)
            {
                EmgLiveAdd(ref live, s.Chain);
            }
            return true;
        }

        /// <summary>即时宿主（格斗 / 力场）的定时到点：以当时记下的宿主发 evt.timer（触发的子载荷在出手点放，FGR-EMG-034 表第 2 行）。</summary>
        private static void EmgFireTimer(ref CombatData d, in EmgSpawn s)
        {
            if (s.Prog < 0 || s.Prog >= d.EmgPrograms.Length)
            {
                return;
            }
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = new EmgCtx
            {
                Prog = s.Prog, Chain = s.Chain, Ent = s.Ent, Owner = d.SlotOf(s.Owner), Node = s.Node, Depth = s.Depth, EntKind = s.EntKind,
                Damage = s.Damage, Weapon = s.Weapon, OwnerId = s.Owner, Faction = s.Faction, Carrier = s.SubForm, MainTarget = s.Other,
                ZoneRadius = s.ZoneRadius, ZoneLife = s.ZoneLife,
            };
            float secs = EmgReadParam(ref d, s.Prog, s.Node, EmgEventKind.Timer);
            EmgShotEvent(ref d, EmgEventKind.Timer, 0, secs, 0f, 0, s.Pos);
            d.EmgCtx = saved;
        }

        /// <summary>出手开始时：带程序的格斗 / 力场宿主读 evt.timer → 记一条定时（从出手起算；“在出手点放”，宿主载体记在 SubForm 位）。</summary>
        private static void EmgQueueShotTimer(ref CombatData d, int a, in CombatWeapon wp)
        {
            CombatCarrier carrier = wp.Reading.Carrier;
            if ((carrier != CombatCarrier.Melee && carrier != CombatCarrier.Aura) || !EmgSiteListens(ref d, EmgEventKind.Timer)
                || EmgReadSlot(ref d, wp.Program, 0, EmgEventKind.Timer) < 0)
            {
                return;
            }
            float secs = EmgReadParam(ref d, wp.Program, 0, EmgEventKind.Timer);
            if (!(secs > 0f) || d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                return;
            }
            EmgCtx c = d.EmgCtx;
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendTimer, Armed = 1, Count = 1, At = d.Scalars[0].Time + secs, Pos = d.Pos[a], Prog = wp.Program, Node = 0, Chain = c.Chain,
                Depth = c.Depth, EntKind = EmgEntKind.Unit, Ent = d.Id[a], Owner = d.Id[a], Weapon = c.Weapon, Damage = c.Damage, Faction = c.Faction,
                Other = c.MainTarget, ZoneRadius = c.ZoneRadius, ZoneLife = c.ZoneLife, SubForm = (byte)carrier,
            });
        }

        // ─────────────────────────────── 区域 / 回波 / 修复做宿主（DEBT-EMG1BUS01-03）───────────────────────────────

        /// <summary>布区刚落下的区域（区域表最后一行）：出手宿主带程序时，区域成为根载荷宿主——程序 / 0 代根节点 / 本次出手的链与链深，发稳定 ID。</summary>
        private static void EmgAdoptZone(ref CombatData d, int a)
        {
            EmgCtx c = d.EmgCtx;
            int last = d.Zones.Length - 1;
            if (c.Prog < 0 || a != c.Owner || last < 0)
            {
                return;
            }
            CombatZone z = d.Zones[last];
            EmgScalars es = d.EmgS[0];
            z.EmgProgRef = c.Prog + 1;
            z.EmgId = es.NextZoneId++;
            d.EmgS[0] = es;
            z.EmgChain = c.Chain;
            z.EmgNode = c.Node;
            z.EmgDepth = c.Depth;
            z.EmgGen = 0;
            z.EmgSub = 0;
            z.EmgWeapon = c.Weapon;
            d.Zones[last] = z;
        }

        /// <summary>带程序的区域到期：evt.expire（Code = 寿命，宿主 = 区域）；子区在到期处执行自己节点的指令（一次结算）。</summary>
        private static void EmgZoneExpire(ref CombatData d, in CombatZone z)
        {
            if (z.EmgSub != 0)
            {
                EmgNodeEffects(ref d, z.EmgProgRef - 1, z.EmgNode, z.Pos, d.SlotOf(z.Owner), z.Faction);
            }
            if (d.EmgListen == 0u)
            {
                return;
            }
            d.EmgCtx = EmgZoneHost(ref d, z);
            EmgShotEvent(ref d, EmgEventKind.Expire, 0, 0f, 0f, EmgCode.ExpireLife, z.Pos);
            d.EmgCtx = EmgCtx.None;
        }

        /// <summary>带程序的区域：落区后存在满读端声明的秒数时发一次 evt.timer（从落区起算，FG19 FGR-EMG-034 表第 2 行）。按区域生成时刻判，读档 / 倍速一致。</summary>
        private static void EmgZoneTimer(ref CombatData d, ref CombatZone z, double now, float dt)
        {
            int prog = z.EmgProgRef - 1;
            if (!EmgSiteListens(ref d, EmgEventKind.Timer) || EmgReadSlot(ref d, prog, z.EmgNode, EmgEventKind.Timer) < 0)
            {
                return;
            }
            float secs = EmgReadParam(ref d, prog, z.EmgNode, EmgEventKind.Timer);
            double age = now - z.Born;
            if (!(secs > 0f) || age < secs || age - dt >= secs)
            {
                return;
            }
            d.EmgCtx = EmgZoneHost(ref d, z);
            EmgShotEvent(ref d, EmgEventKind.Timer, 0, secs, 0f, 0, z.Pos);
            z.EmgTrigUsed = d.EmgCtx.TrigUsed;
            d.EmgCtx = EmgCtx.None;
        }

        /// <summary>回波（读法“追射 / 残影 / 节拍 / 回旋”排的延迟再结算）：排它的那次出手带程序时，回波记下出手宿主的程序 / 节点 / 链 / 链深。</summary>
        private static void EmgAdoptEcho(ref CombatData d, int a)
        {
            EmgCtx c = d.EmgCtx;
            int last = d.Echoes.Length - 1;
            if (c.Prog < 0 || a != c.Owner || last < 0)
            {
                return;
            }
            CombatEcho e = d.Echoes[last];
            e.EmgProgRef = c.Prog + 1;
            e.EmgChain = c.Chain;
            e.EmgNode = c.Node;
            e.EmgDepth = c.Depth;
            d.Echoes[last] = e;
        }

        /// <summary>回波结算：带程序的回波以出手宿主（单位 Id）为宿主，evt.hit Code = 回波（每次回波各自计每宿主上限）；被击来源仍是“其他”（BUS-01 口径）。没有程序只清上下文。</summary>
        private static void EmgEchoBegin(ref CombatData d, in CombatEcho echo, int owner)
        {
            if (echo.EmgProgRef <= 0 || d.EmgListen == 0u)
            {
                d.EmgCtx = EmgCtx.None;
                return;
            }
            int w = owner >= 0 ? d.Weapon[owner] : -1;
            d.EmgCtx = new EmgCtx
            {
                Prog = echo.EmgProgRef - 1, Chain = echo.EmgChain, Ent = echo.Owner, Owner = owner, Node = echo.EmgNode, Depth = echo.EmgDepth, EntKind = EmgEntKind.Unit,
                HurtSrc = (byte)EmgCode.SrcOther, HitCode = EmgCode.HitEcho, Damage = echo.Damage, Weapon = w, OwnerId = echo.Owner,
                Faction = owner >= 0 ? d.Faction[owner] : (byte)0, Carrier = w >= 0 && w < d.Weapons.Length ? (byte)d.Weapons[w].Reading.Carrier : (byte)0,
                // EntKind = Unit 的回波宿主不是“格斗 / 力场的同一次出手”，主目标取回波目标，不会被主目标规则挡掉。
                MainTarget = echo.Target,
            };
        }

        /// <summary>载荷修复友军（治疗子弹药、带程序的维修单位）：evt.hit（Code = 修复，宿主 = 当前载荷）。自我治疗（吸血）不算命中。</summary>
        private static void EmgOnHeal(ref CombatData d, int t, float amount, int healer)
        {
            if (d.EmgListen == 0u)
            {
                return;
            }
            EmgCtx c = d.EmgCtx;
            if (c.Prog < 0 || c.EntKind == EmgEntKind.None || healer != c.Owner || t == healer)
            {
                return;
            }
            c.Hits++;
            ushort code = c.HitCode;
            c.HitCode = EmgCode.HitHeal;
            d.EmgCtx = c;
            EmgShotEvent(ref d, EmgEventKind.Hit, d.Id[t], amount, c.Hits, EmgCode.HitHeal, d.Pos[t]);
            EmgCtx after = d.EmgCtx;
            after.HitCode = code;
            d.EmgCtx = after;
        }

        /// <summary>带程序的维修单位每次修复 = 一次出手（evt.fire；修复命中 evt.hit Code = 修复；修复放出的子载荷是治疗子弹药）。与 EmgShotEnd 成对。</summary>
        internal static void EmgRepairBegin(ref CombatData d, int i, int target, float amount)
        {
            int prog = EmgUnitProg(ref d, i);
            if (prog < 0 || d.EmgListen == 0u)
            {
                return;
            }
            EmgHostBegin(ref d, prog, i, d.Id[i], EmgEntKind.Unit, (byte)EmgCode.SrcOther, d.Pos[i], target);
            EmgCtx c = d.EmgCtx;
            c.Damage = amount;
            c.Weapon = d.Weapon[i];
            c.OwnerId = d.Id[i];
            c.Faction = d.Faction[i];
            c.MainTarget = d.Id[target];
            c.PFlags = EmgProjFlags.Heal;
            c.HitCode = EmgCode.HitHeal;
            d.EmgCtx = c;
        }

        // ─────────────────────────────── 根载荷实体化（SpawnFlight）───────────────────────────────

        /// <summary>武器按 SpawnFlight 开火：热更层置位（程序需要飞行且组件允许实体化）；弹体武器本来就飞、无人机舱的母机出手是放机（无人机的开火另行实体化），都不算。</summary>
        internal static bool EmgFlightOn(in CombatWeapon wp) =>
            wp.SpawnFlight != 0 && wp.Mode != CombatWeaponMode.Projectile && wp.Reading.Carrier != CombatCarrier.Summon;

        /// <summary>
        /// SpawnFlight 武器在原“当场结算”的那一刻生成一发根弹体（0 代根节点、本次出手的链；弹速 / 半径 / 寿命取武器上由热更层按 FG19A 第 2.1.1 节写好的参数）。
        /// 弹体池满：这一发同样留在待生成表里顺延（生成时才开始计寿命），不丢、不退回即时命中。
        /// </summary>
        internal static void EmgFireFlight(ref CombatData d, int a, int t, int w, in CombatWeapon wp, EmgProjFlags extra)
        {
            EmgStat(ref d, 7);
            int chain = d.EmgCtx.Prog == wp.Program && d.EmgCtx.Chain > 0 ? d.EmgCtx.Chain : 0;
            EmgProjFlags flags = EmgProjFlags.Flight | extra;
            // EMG1-GRM-01：多份载荷（霰射：一次出手 N 发真实弹丸，扇面均分、各 a 倍伤害，各自命中、各自触发）。一份时逐字同旧。
            int pellets = EmgPellets(ref d, wp, out float ratio, out float fan);
            if (pellets > 1 && chain == 0 && wp.Program >= 0)
            {
                EmgScalars es0 = d.EmgS[0];
                chain = es0.NextChain++;
                d.EmgS[0] = es0;
            }
            double2 aim = d.Pos[t] - d.Pos[a];
            for (int q = 0; q < pellets; q++)
            {
                double2 to = pellets > 1 ? d.Pos[a] + (double2)Rotate((float2)aim, math.radians(fan * ((float)q / (pellets - 1) - 0.5f))) : d.Pos[t];
                EmgFireOne(ref d, a, t, w, wp, wp.Damage * ratio, to, flags, ref chain);
            }
        }

        private static void EmgFireOne(ref CombatData d, int a, int t, int w, in CombatWeapon wp, float damage, double2 to, EmgProjFlags flags, ref int chain)
        {
            if (EmgTrySpawnFlight(ref d, d.Pos[a], d.Radius[a], to, d.Id[a], w, wp, damage, (CombatFaction)d.Faction[a], flags, chain, d.Id[t]))
            {
                return;
            }
            if (d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                EmgStat(ref d, 4);
                return;
            }
            if (chain == 0 && wp.Program >= 0)
            {
                EmgScalars es = d.EmgS[0];
                chain = es.NextChain++;
                d.EmgS[0] = es;
            }
            EmgStat(ref d, 2);
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendRoot, Armed = 1, Count = 1, Pos = d.Pos[a], Dir = EmgDir(to - d.Pos[a]), Other = d.Id[t], Owner = d.Id[a], Weapon = w,
                Damage = damage, Faction = d.Faction[a], Flags = flags, Prog = wp.Program, Chain = chain,
            });
        }

        /// <summary>无人机开火实体化（武器 SpawnFlight）：从无人机处向目标发一发根弹体（伤害 = 无人机伤害，命中按无人机读法）。</summary>
        internal static void EmgDroneFlight(ref CombatData d, in CombatDrone dr, int target, in CombatWeapon wp)
        {
            EmgStat(ref d, 7);
            int chain = d.EmgCtx.Prog == wp.Program && d.EmgCtx.Chain > 0 ? d.EmgCtx.Chain : 0;
            EmgProjFlags flags = EmgProjFlags.Flight | EmgProjFlags.ViaDrone;
            int owner = d.SlotOf(dr.Owner);
            if (EmgTrySpawnFlight(ref d, dr.Pos, 0f, d.Pos[target], dr.Owner, dr.Weapon, wp, dr.Damage, dr.Faction, flags, chain, d.Id[target]) || d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                return;
            }
            EmgStat(ref d, 2);
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendRoot, Armed = 1, Count = 1, Pos = dr.Pos, Dir = EmgDir(d.Pos[target] - dr.Pos), Other = d.Id[target], Owner = owner >= 0 ? d.Id[owner] : 0,
                Weapon = dr.Weapon, Damage = dr.Damage, Faction = (byte)dr.Faction, Flags = flags, Prog = wp.Program, Chain = chain,
            });
        }

        private static bool EmgTrySpawnFlight(ref CombatData d, double2 from, float fromRadius, double2 to, int ownerId, int weapon, in CombatWeapon wp, float damage,
            CombatFaction faction, EmgProjFlags flags, int chain, int targetId)
        {
            if (d.Projectiles.Length >= d.Config.ProjectileCapacity)
            {
                return false;
            }
            float2 dir = EmgDir(to - from);
            if (math.lengthsq(dir) < 1e-12f)
            {
                dir = new float2(0f, 1f);
            }
            double2 start = from + (double2)(dir * (fromRadius + wp.ProjectileRadius));
            EmgScalars es = d.EmgS[0];
            int id = es.NextProjId++;
            if (chain == 0 && wp.Program >= 0)
            {
                chain = es.NextChain++;
            }
            d.EmgS[0] = es;
            // 寿命显式由热更层写（射程 ÷ 弹速 × 1.2，FG19A 第 2.1.1 节）；不依赖 0 值语义。弹速缺值时弹体不动、寿命到即止——不退回即时命中（缺值由表校验与 FgChannelAdmissionSelfCheck 拦下）。
            float life = wp.ProjectileLife > 0f ? wp.ProjectileLife : wp.Range * 1.2f / math.max(0.01f, wp.ProjectileSpeed);
            var pr = new CombatProjectile
            {
                Id = id, Prog = wp.Program, Chain = chain, Pos = start, Prev = start, Vel = dir * wp.ProjectileSpeed, Owner = ownerId, Weapon = weapon, Damage = damage,
                Radius = wp.ProjectileRadius, Life = life, Faction = faction, Flags = flags,
            };
            EmgInitFlight(ref d, ref pr, to, targetId, true); // EMG1-GRM-01：根节点的弹道修饰写进弹体（没有修饰时不改任何字段）
            d.Projectiles.Add(pr);
            CombatCounters c = d.Counters[0];
            c.ProjectilesSpawned++;
            d.Counters[0] = c;
            return true;
        }

        /// <summary>
        /// SpawnFlight 根弹体命中：按原“当场结算”那一下结算——即时武器：伤害（护甲 / 侧后按飞行方向）、逐目标读法、命中标记、标记跳转、锚定读法；
        /// 重炮：熔穿过载这一发加穿甲 + 重炮读法；无人机弹体：无人机读法（以无人机为锚）。开火者已阵亡：与炮塔弹体同一口径（逐目标 + 锚定）。
        /// </summary>
        private static void EmgFlightHit(ref CombatData d, in CombatProjectile pr, int owner, int hit, double2 hitPos, double2 fromSide)
        {
            if (pr.Weapon < 0 || pr.Weapon >= d.Weapons.Length)
            {
                DamageUnit(ref d, hit, pr.Damage, owner);
                return;
            }
            CombatWeapon wp = d.Weapons[pr.Weapon];
            bool viaDrone = (pr.Flags & EmgProjFlags.ViaDrone) != 0;
            if (viaDrone)
            {
                wp.Damage = pr.Damage;
                wp.Reading.EscortDrones = 0;
            }
            CombatReading r = wp.Reading;
            double now = d.Scalars[0].Time;
            bool wasMarked = d.MarkedUntil[hit] > now;
            float pierce = (pr.Flags & EmgProjFlags.Overload) != 0 && !d.Has(hit, CombatUnitFlags.HeatResistant) ? wp.PierceBonus : 0f;
            float dealt = StrikeDamage(ref d, owner, hit, pr.Damage, r, fromSide, !viaDrone, pierce);
            if (!DamageUnit(ref d, hit, dealt, owner))
            {
                return;
            }
            if (owner < 0)
            {
                ProjectileReadingHit(ref d, -1, hit, hitPos, dealt, wp, fromSide);
                return;
            }
            if (viaDrone)
            {
                PerTarget(ref d, owner, hit, dealt, r, fromSide);
                Anchored(ref d, owner, hit, hitPos, dealt, pr.Damage, wp, wasMarked, fromSide, true);
                return;
            }
            if (wp.Mode == CombatWeaponMode.Cannon)
            {
                CannonReadingHit(ref d, owner, hit, hitPos, dealt, wp, wasMarked);
                return;
            }
            PerTarget(ref d, owner, hit, dealt, r, d.Pos[owner]);
            if (wp.MarkSeconds > 0f && d.IsAlive(hit))
            {
                d.MarkedUntil[hit] = now + wp.MarkSeconds;
                EmgShotEvent(ref d, EmgEventKind.Marked, d.Id[hit], wp.MarkSeconds, 0f, 0, d.Pos[hit]);
            }
            if (wp.Reaction == CombatReaction.MarkJump && wasMarked && !d.Has(owner, CombatUnitFlags.ReactionSpent))
            {
                MarkJump(ref d, owner, hit, hitPos, dealt, wp);
            }
            Anchored(ref d, owner, hit, hitPos, dealt, wp.Damage, wp, wasMarked, d.Pos[owner], false);
        }

        /// <summary>
        /// SpawnFlight 弹体命中致死：击毁改由命中回报（ADR ③）——击杀者正对这个目标执行编队攻击时，补发“攻击结果 = 击毁”并以“目标已击毁”结束命令
        /// （与即时命中当场击毁时的事件序列一致，热更层“击毁目标 …，交还 AI”文案不变）。O(1)。
        /// </summary>
        private static void EmgFlightKillReport(ref CombatData d, int ks, int t)
        {
            CombatCommand cmd = d.Cmd[ks];
            if (cmd.Kind != CombatCommandKind.Attack || cmd.Target != d.Id[t])
            {
                return;
            }
            Gameplay(ref d, CombatEventKind.AttackOutcome, ks, d.Id[t], 0f, 0f, d.Pos[t], (byte)CombatFireResult.Ok, 1);
            d.Cmd[ks] = default;
            byte navFail = d.NavFail[ks];
            DropRoute(ref d, ks);
            d.NavSt[ks] = (byte)CombatNavState.None;
            Gameplay(ref d, CombatEventKind.CommandEnded, ks, cmd.Target, navFail, 0f, d.Pos[ks], (byte)CombatEndReason.TargetDestroyed, (byte)CombatCommandKind.Attack);
        }

        // ─────────────────────────────── 子弹药只执行自己节点的指令 ───────────────────────────────

        /// <summary>
        /// 子载荷（1 / 2 代）一次结算（命中 / 寿命到 / 撞障碍 / 子区到期）时执行自己节点的效果写端。阶段 1：排放（mat.* 写端，动词 = 热更层给的 emit 码）
        /// 经 fg.TbPortCarrier 映射到读法驻留区——A = 半径、B = 秒数、C = 状态位号 + 1（编译器按物质写好；FG19A 第 0.3 节口径 3）。阶段 2 改为斑块（EMG2-MAT-01）。
        /// </summary>
        private static void EmgNodeEffects(ref CombatData d, int prog, int node, double2 pos, int ownerSlot, CombatFaction faction)
        {
            int verb = d.Config.EmgVerbEmit;
            if (verb <= 0 || prog < 0 || prog >= d.EmgPrograms.Length)
            {
                return;
            }
            EmgProgram p = d.EmgPrograms[prog];
            if (node < 0 || node >= p.NodeCount || p.FirstNode + node >= d.EmgNodes.Length)
            {
                return;
            }
            EmgNode n = d.EmgNodes[p.FirstNode + node];
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Role <= EmgConst.RoleLastRead || x.Verb != verb || !(x.A > 0f) || !(x.B > 0f))
                {
                    continue;
                }
                int bit = (int)math.round(x.C) - 1;
                uint mask = bit >= 0 && bit < 32 ? 1u << bit : 0u;
                if (SpawnZone(ref d, ownerSlot, faction, pos, x.A, x.B, 0f, 0f, 1f, mask, 0f, 0f, 0f, 0f))
                {
                    SetLastZoneLook(ref d, CombatZoneLook.Pool);
                    NoteReading(ref d, CombatConst.ReadingFeedZone, pos, ownerSlot);
                }
            }
        }

        // ─────────────────────────────── EmgHash：障碍 / 残骸线段检测 ───────────────────────────────

        private static void EmgHashEnsure(ref CombatData d)
        {
            if (d.EmgS[0].HashDirty == 0)
            {
                return;
            }
            d.EmgHash.Entries.Clear();
            d.EmgHash.Refs.Clear();
            d.EmgHash.Cells.Clear();
            for (int k = 0; k < d.Obstacles.Length; k++)
            {
                float3 o = d.Obstacles[k];
                EmgHashAdd(ref d, new double2(o.x, o.y), o.z, EmgHash.KindObstacle, k);
            }
            for (int k = 0; k < d.Wrecks.Length; k++)
            {
                EmgWreck w = d.Wrecks[k];
                EmgHashAdd(ref d, w.Pos, w.Radius, EmgHash.KindWreck, k);
            }
            EmgScalars es = d.EmgS[0];
            es.HashDirty = 0;
            d.EmgS[0] = es;
        }

        private static void EmgHashAdd(ref CombatData d, double2 p, float r, byte kind, int index)
        {
            if (!(r > 0f))
            {
                return;
            }
            int e = d.EmgHash.Entries.Length;
            d.EmgHash.Entries.Add(new float4((float)p.x, (float)p.y, r, kind));
            d.EmgHash.Refs.Add(index);
            int x0 = EmgHash.CellOf(p.x - r), x1 = EmgHash.CellOf(p.x + r), y0 = EmgHash.CellOf(p.y - r), y1 = EmgHash.CellOf(p.y + r);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    d.EmgHash.Cells.Add(EmgHash.Key(cx, cy), e);
                }
            }
        }

        /// <summary>线段 a→b 与某种圆（障碍 / 有耐久的残骸）的最早交点：返回圆在原表里的下标（没有 −1），<paramref name="tHit"/> ∈ [0, 1]。
        /// <paramref name="skipEnds"/>：线段起点或终点已在圆内的不算（站在残骸上的单位不算躲在它后面）。O(线段外接框覆盖的格 × 格内圆)。</summary>
        private static int EmgHashSegment(ref CombatData d, double2 a, double2 b, byte kind, bool skipEnds, out double tHit)
        {
            tHit = 2.0;
            EmgHashEnsure(ref d);
            if (d.EmgHash.Entries.Length == 0)
            {
                return -1;
            }
            int x0 = EmgHash.CellOf(math.min(a.x, b.x)), x1 = EmgHash.CellOf(math.max(a.x, b.x));
            int y0 = EmgHash.CellOf(math.min(a.y, b.y)), y1 = EmgHash.CellOf(math.max(a.y, b.y));
            int best = -1;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
                    {
                        continue;
                    }
                    do
                    {
                        float4 c = d.EmgHash.Entries[e];
                        if ((byte)c.w != kind)
                        {
                            continue;
                        }
                        int idx = d.EmgHash.Refs[e];
                        if (kind == EmgHash.KindWreck && (idx >= d.Wrecks.Length || !(d.Wrecks[idx].Hp > 0f)))
                        {
                            continue;
                        }
                        double2 center = new double2(c.x, c.y);
                        double r2 = (double)c.z * c.z;
                        if (skipEnds && (math.lengthsq(a - center) <= r2 || math.lengthsq(b - center) <= r2))
                        {
                            continue;
                        }
                        double t = SegmentCircleT(a, b, center, c.z);
                        if (t >= 0 && (t < tHit || (t == tHit && idx < best)))
                        {
                            tHit = t;
                            best = idx;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        /// <summary>
        /// 即时命中的掩体（ADR ③；FGR-EMG-113“残骸背后的单位获得掩护”）：结算前对出手者 → 目标的线段做一次残骸检测（固态斑块随 EMG2 并入同一哈希）；
        /// 地点没有残骸时 O(1) 跳过、与旧行为逐字一致。挡住时这一击被残骸吸收（扣残骸耐久、发 evt.absorb），目标不受伤。
        /// </summary>
        internal static bool EmgInstantCovered(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            // 只对射弹类即时命中（含敌人的 Instant 攻击）；格斗 / 力场 / 布区 / 放机不是一条射击线段。
            if (d.Wrecks.Length == 0 || a < 0 || t < 0 || wp.Reading.Carrier != CombatCarrier.Projectile)
            {
                return false;
            }
            float damage = wp.Damage;
            int w = EmgHashSegment(ref d, d.Pos[a], d.Pos[t], EmgHash.KindWreck, true, out double tHit);
            if (w < 0)
            {
                return false;
            }
            EmgWreck k = d.Wrecks[w];
            k.Hp = math.max(0f, k.Hp - math.max(0f, damage));
            d.Wrecks[w] = k;
            EmgStat(ref d, 5);
            double2 at = d.Pos[a] + (d.Pos[t] - d.Pos[a]) * tHit;
            EmgShotEvent(ref d, EmgEventKind.Absorb, k.Id, damage, 0f, 0, at, EmgEntKind.Wreck);
            return true;
        }

        /// <summary>SpawnFlight 弹体 / 子弹药这一步的线段先于单位交点撞上障碍圆：在撞点结束（evt.expire Code = 撞障碍；子弹药在撞点执行自己节点的指令）。</summary>
        private static bool EmgObstacleBefore(ref CombatData d, in CombatProjectile pr, double2 next, double unitT, out double2 at, out int o)
        {
            at = next;
            o = -1;
            if (d.Obstacles.Length == 0 || (pr.Flags & (EmgProjFlags.Sub | EmgProjFlags.Flight)) == 0)
            {
                return false;
            }
            o = EmgHashSegment(ref d, pr.Prev, next, EmgHash.KindObstacle, false, out double t);
            if (o < 0 || t > unitT)
            {
                return false;
            }
            at = pr.Prev + (next - pr.Prev) * t;
            EmgStat(ref d, 6);
            return true;
        }
    }
}
