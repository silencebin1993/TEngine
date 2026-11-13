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
        /// <summary>EMG2-MAT-01：物质斑块（Refs = Patches 下标；按覆盖格登记，单块半径 ≤ emg.patch.max_radius 8 米）。</summary>
        public const byte KindPatch = 2;
        /// <summary>每个圆：x, y, 半径, 种类。</summary>
        public NativeList<float4> Entries;
        /// <summary>圆在障碍表 / 残骸表里的下标。</summary>
        public NativeList<int> Refs;
        public NativeParallelMultiHashMap<long, int> Cells;
        /// <summary>FG-GAP-186：残骸专用的格链表（同 <see cref="PatchHead"/>：x, y = 格，z = Entries 下标，w = 下一节点；头表存“节点号 + 1”，0 = 空）。
        /// 每发弹体每步、每个移动单位每步都查残骸，上千发时通用哈希（同格还挂着障碍）是战斗内核最大的新增开销；随哈希重建，不进快照。</summary>
        public NativeArray<int> WreckHead;
        public NativeList<int4> WreckNodes;
        /// <summary>FG-GAP-186：残骸圆（按登记的单精度圆心，外扩 1 毫米）覆盖的 1 米细格位图（散列，只会误报“可能有”、不会漏）。弹体先查它，绝大多数不进残骸检测。</summary>
        public NativeArray<ulong> WreckFine;
        /// <summary>FG-GAP-186：活跃场覆盖格 → 场下标掩码（容量恒 ≤ 64，一格一个 ulong；哈希撞格只会多查、不会漏）。每步弹体结算前重建，弹体结算期间新生成 / 续期的场当场补记。</summary>
        public NativeArray<ulong> FieldMasks;
        /// <summary>FG-GAP-186：弹体 × 斑块专用的格链表（头插，同格遍历顺序与 Cells 相同）：x, y = 格，z = Patches 下标，w = 下一节点。上千发弹体每步都查，不走 Cells 的通用哈希、也不用跳过同格的障碍与残骸。头表存“节点号 + 1”，0 = 空。</summary>
        public NativeArray<int> PatchHead;
        public NativeList<int4> PatchNodes;
        internal const int PatchHeadSlots = 2048;
        private const int WreckHeadSlots = 1024;
        private const int WreckFineWords = 256;
        private const int FieldMaskSlots = 1024;

        public static EmgHash Create() => new EmgHash
        {
            Entries = new NativeList<float4>(8, Allocator.Persistent),
            Refs = new NativeList<int>(8, Allocator.Persistent),
            Cells = new NativeParallelMultiHashMap<long, int>(16, Allocator.Persistent),
            WreckHead = new NativeArray<int>(WreckHeadSlots, Allocator.Persistent),
            WreckNodes = new NativeList<int4>(16, Allocator.Persistent),
            WreckFine = new NativeArray<ulong>(WreckFineWords, Allocator.Persistent),
            FieldMasks = new NativeArray<ulong>(FieldMaskSlots, Allocator.Persistent),
            PatchHead = new NativeArray<int>(PatchHeadSlots, Allocator.Persistent),
            PatchNodes = new NativeList<int4>(64, Allocator.Persistent),
        };

        public void Dispose()
        {
            Entries.Dispose();
            Refs.Dispose();
            Cells.Dispose();
            WreckHead.Dispose();
            WreckNodes.Dispose();
            WreckFine.Dispose();
            FieldMasks.Dispose();
            PatchHead.Dispose();
            PatchNodes.Dispose();
        }

        internal static int CellOf(double v) => (int)math.floor(v / Cell);

        internal static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        internal static int WreckSlot(int cx, int cy) => (int)(((uint)cx * 73856093u ^ (uint)cy * 19349663u) & (WreckHeadSlots - 1u));

        internal void AddWreckCell(int cx, int cy, int entry)
        {
            int slot = WreckSlot(cx, cy);
            WreckNodes.Add(new int4(cx, cy, entry, WreckHead[slot] - 1));
            WreckHead[slot] = WreckNodes.Length;
        }

        internal void ClearWreckCells()
        {
            WreckNodes.Clear();
            for (int i = 0; i < WreckHeadSlots; i++)
            {
                WreckHead[i] = 0;
            }
            for (int i = 0; i < WreckFineWords; i++)
            {
                WreckFine[i] = 0ul;
            }
        }

        private static uint FineBit(int fx, int fy) => ((uint)fx * 73856093u ^ (uint)fy * 19349663u) & (WreckFineWords * 64u - 1u);

        /// <summary>登记一个残骸圆（与 Entries 同一份单精度圆心 / 半径；外扩 1 毫米覆盖 SegmentCircleT 的舍入）。</summary>
        internal void MarkWreckFine(float4 c)
        {
            double r = (double)c.z + 1e-3;
            int x0 = (int)math.floor(c.x - r), x1 = (int)math.floor(c.x + r), y0 = (int)math.floor(c.y - r), y1 = (int)math.floor(c.y + r);
            for (int fy = y0; fy <= y1; fy++)
            {
                for (int fx = x0; fx <= x1; fx++)
                {
                    uint b = FineBit(fx, fy);
                    WreckFine[(int)(b >> 6)] |= 1ul << (int)(b & 63u);
                }
            }
        }

        /// <summary>线段 a→b 的外接框覆盖的 1 米细格里有没有残骸登记（false = 线段必不碰任何残骸圆）。</summary>
        internal bool WreckFineOnSegment(double2 a, double2 b)
        {
            int x0 = (int)math.floor(math.min(a.x, b.x)), x1 = (int)math.floor(math.max(a.x, b.x));
            int y0 = (int)math.floor(math.min(a.y, b.y)), y1 = (int)math.floor(math.max(a.y, b.y));
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 64)
            {
                return true; // 很长的线段不逐格查，交给完整检测
            }
            for (int fy = y0; fy <= y1; fy++)
            {
                for (int fx = x0; fx <= x1; fx++)
                {
                    uint b2 = FineBit(fx, fy);
                    if ((WreckFine[(int)(b2 >> 6)] & (1ul << (int)(b2 & 63u))) != 0ul)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static int PatchSlot(int cx, int cy) => (int)(((uint)cx * 73856093u ^ (uint)cy * 19349663u) & (PatchHeadSlots - 1u));

        internal void ClearPatchCells()
        {
            PatchNodes.Clear();
            for (int i = 0; i < PatchHeadSlots; i++)
            {
                PatchHead[i] = 0;
            }
        }

        internal void AddPatchCell(int cx, int cy, int patch)
        {
            int slot = PatchSlot(cx, cy);
            PatchNodes.Add(new int4(cx, cy, patch, PatchHead[slot] - 1));
            PatchHead[slot] = PatchNodes.Length;
        }

        internal void ClearFieldCells()
        {
            for (int i = 0; i < FieldMaskSlots; i++)
            {
                FieldMasks[i] = 0ul;
            }
        }

        private static int FieldSlot(int cx, int cy) => (int)(((uint)cx * 73856093u ^ (uint)cy * 19349663u) & (FieldMaskSlots - 1u));

        internal void MarkFieldCircle(int field, double2 p, float r)
        {
            int x0 = CellOf(p.x - r), x1 = CellOf(p.x + r), y0 = CellOf(p.y - r), y1 = CellOf(p.y + r);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    int slot = FieldSlot(cx, cy);
                    FieldMasks[slot] |= 1ul << field;
                }
            }
        }

        /// <summary>线段外接框（外扩 <paramref name="pad"/>）覆盖的格里可能碰到的场（下标掩码）。</summary>
        internal ulong FieldMaskOnSegment(double2 a, double2 b, double pad)
        {
            int x0 = CellOf(math.min(a.x, b.x) - pad), x1 = CellOf(math.max(a.x, b.x) + pad), y0 = CellOf(math.min(a.y, b.y) - pad), y1 = CellOf(math.max(a.y, b.y) + pad);
            ulong mask = 0ul;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    mask |= FieldMasks[FieldSlot(cx, cy)];
                }
            }
            return mask;
        }
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

        /// <summary>
        /// EMG1-FUZZ-01（DEBT-EMG1ENT01-08，FG19B 第 6 节“子弹药质量 = 母弹 ÷ 份数”）：沿程序树从本节点往根逐代除以该代的份数（编译期 TrigCount，钳到每次触发份数上限），
        /// 根 = 母武器的弹体档案（重炮按重型弹体）。只读程序表，不加快照字段；O(代数 ≤ 2)。
        /// </summary>
        private static float EmgSubMass(ref CombatData d, in EmgSpawn s)
        {
            bool heavy = s.Weapon >= 0 && s.Weapon < d.Weapons.Length && d.Weapons[s.Weapon].Mode == CombatWeaponMode.Cannon;
            float m = EmgProfileAt(ref d, heavy ? EmgConst.SlotHeavy : EmgConst.SlotProjectile).Mass;
            if (s.Prog < 0 || s.Prog >= d.EmgPrograms.Length)
            {
                return m;
            }
            EmgProgram p = d.EmgPrograms[s.Prog];
            int node = s.Node;
            int cap = EmgMaxChildren(ref d);
            for (int g = 0; g <= EmgConst.MaxGen && node > 0 && node < p.NodeCount && p.FirstNode + node < d.EmgNodes.Length; g++)
            {
                EmgNode n = d.EmgNodes[p.FirstNode + node];
                m /= math.clamp((int)n.TrigCount, 1, cap);
                node = n.Parent;
            }
            return m;
        }

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
                HurtSrc = (byte)EmgCode.SrcZone, HitCode = EmgCode.HitZoneTick, TrigUsed = z.EmgTrigUsed, Damage = math.max(0f, z.Dps), Strike = math.max(0f, z.Dps) * z.TickInterval, Weapon = z.EmgWeapon, OwnerId = z.Owner,
                Faction = (byte)z.Faction, Carrier = (byte)CombatCarrier.Field, ZoneRadius = z.Radius, ZoneLife = (float)(z.Until - z.Born), ZoneBorn = z.Born, ZoneTick = z.TickInterval,
            };
        }

        private static float2 EmgDir(double2 v) => math.lengthsq(v) > 1e-12 ? (float2)math.normalize(v) : float2.zero;

        // ─────────────────────────────── 生产点：入队 + 记待生成请求 ───────────────────────────────

        /// <summary>入队一条带宿主的事件；真的入了队（没被积压保护合并）且宿主节点有挂在这个事件上的触发子节点时，按子节点记待生成请求（未唤起）。</summary>
        internal static void EmgEmitHosted(ref CombatData d, EmgEvent e, in EmgCtx host, float2 dir, ulong gate = ulong.MaxValue)
        {
            int before = d.EmgBus.Length;
            EmgEmit(ref d, e);
            if (e.Prog < 0 || e.Prog >= d.EmgPrograms.Length || d.EmgBus.Length != before + 1)
            {
                return;
            }
            // EMG3-META-01（FGR-EMG-040～042）：有元程序的程序在这里做一次结算的判定——条件 / 随机关掉的、延后的下游指令此刻不执行，复制 / 延后的再执行记进运行态
            ulong allow = (d.EmgPrograms[e.Prog].Flags & EmgProgramFlags.HasMeta) != 0 ? EmgMetaSettle(ref d, d.EmgBus[before], host, dir) & gate : gate; // EMG3-TMP-01：evt.beat 只放行本拍执行的节拍指令
            if (e.Node == 0)
            {
                EmgRootGated(ref d, d.EmgBus[before], host, allow);
            }
            else
            {
                EmgChildWrites(ref d, d.EmgBus[before], host, allow); // EMG2-RECL-01：子节点上的回收修复 / 桥接 / 蔓延（子弹药击毁也拆残骸修母机，FG19A 第 3 节 fw_mix_rustharvest）
            }
            if (d.EmgPrograms[e.Prog].NodeCount < 2)
            {
                return;
            }
            EmgQueueChildren(ref d, d.EmgBus[before], host, dir, allow);
        }

        /// <summary>
        /// EMG1-FW-02（FG19A 第 6 节 N12 / N13 / N16；第 0.3 节口径 3 阶段 1 过渡）：根节点上带 <see cref="EmgInstrFlags.RootKernel"/> 的效果写端，
        /// 在它的生效事件（同一条指令的 trigger 读端）入队时，在事件位置落一块驻留区 + 状态位：排放 = 物质驻留区（冷却液 / 水……），注电 = 短电击区。
        /// 单位级事件（过热 / 冷却完成……）的位置 = 本单位处。只认动词与参数，不认端口名以外的内容 ID（FGR-EMG-013）。O(根节点指令数²)，只在事件入队时。
        /// </summary>
        private static void EmgRootGated(ref CombatData d, in EmgEvent e, in EmgCtx host, ulong allow = ulong.MaxValue, bool any = false)
        {
            EmgProgram p = d.EmgPrograms[e.Prog];
            if (p.NodeCount < 1 || p.FirstNode >= d.EmgNodes.Length)
            {
                return;
            }
            EmgNode n = d.EmgNodes[p.FirstNode];
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            ushort evPort = (ushort)(EmgConst.EventPortBase + (int)e.Kind);
            int emit = d.Config.EmgVerbEmit;
            ushort pingPort = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Ping);
            bool salvaged = false;
            bool clue = p.ClueMask != 0u;
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (clue && x.Role > EmgConst.RoleLastRead && x.Verb != (byte)EmgVerb.Spread && (x.Flags & (EmgInstrFlags.Overflow | EmgInstrFlags.Anchored)) == 0 && EmgMetaAllowed(allow, x.Ordinal) && EmgTrigOn(ref d, n.FirstInstr, end, x.Ordinal, evPort, any))
                {
                    EmgClueSeen(ref d, e.Chain, x.Port); // EMG4-FUSE-02：被这个事件门控的写端在这里结算（含走旧读法过渡的排放），算这条链上出现了它的端口；蔓延不算它液体的排放，由 EmgSpread 扩开时记“液体扩散”
                }
                if (x.Port == pingPort && x.Role > EmgConst.RoleLastRead)
                {
                    // EMG3-SQD-01（FGR-EMG-133）：写 evt.ping 的指令在它的生效事件（同一条指令的 trigger 读端）时广播频道；不看动词 / RootKernel 标记（按端口 + 角色）
                    if ((x.Flags & EmgInstrFlags.Overflow) == 0 && EmgMetaAllowed(allow, x.Ordinal) && EmgTrigOn(ref d, n.FirstInstr, end, x.Ordinal, evPort, any)
                        && !EmgChsBlocked(ref d, host.Owner, n.FirstInstr, end, x.Ordinal))
                    {
                        EmgPingOut(ref d, e, host, x);
                    }
                    continue;
                }
                bool follow = x.Verb == (byte)EmgVerb.Spawn && x.Port == EmgConst.PortProjectile;
                bool recl = x.Verb == (byte)EmgVerb.Salvage || x.Verb == (byte)EmgVerb.Link || x.Verb == (byte)EmgVerb.Spread; // EMG2-RECL-01
                if ((x.Flags & EmgInstrFlags.RootKernel) == 0 || (x.Flags & EmgInstrFlags.Overflow) != 0 || x.Role <= EmgConst.RoleLastRead
                    || (x.Verb != emit && x.Verb != (byte)EmgVerb.Inject && x.Verb != (byte)EmgVerb.Field && !follow && !recl) || !EmgMetaAllowed(allow, x.Ordinal))
                {
                    continue;
                }
                bool gated = any; // EMG3-META-01：循环的周期执行不论下游指令各自的生效事件（FGR-EMG-040 循环）
                for (int j = n.FirstInstr; j < end && !gated; j++)
                {
                    EmgInstr r = d.EmgInstrs[j];
                    gated = r.Ordinal == x.Ordinal && r.Role == EmgConst.RoleTrigger && r.Port == evPort;
                }
                if (!gated || EmgChsBlocked(ref d, host.Owner, n.FirstInstr, end, x.Ordinal)) // EMG3-CHS-01：固件读 chs.* condition（FGR-EMG-141）
                {
                    continue;
                }
                if (follow)
                {
                    EmgQueueFollow(ref d, e, host, x);
                    continue;
                }
                // EMG2-RECL-01：回收修复 / 拆解钳（同一事件只拆一次，多条 salvage 写端合并）、桥接导线、蔓延
                if (x.Verb == (byte)EmgVerb.Salvage)
                {
                    if (!salvaged)
                    {
                        salvaged = true;
                        EmgSalvage(ref d, e, host, n.FirstInstr, end, evPort);
                    }
                    continue;
                }
                if (x.Verb == (byte)EmgVerb.Link)
                {
                    EmgLink(ref d, e, host, x);
                    continue;
                }
                if (x.Verb == (byte)EmgVerb.Spread)
                {
                    EmgSpread(ref d, e, host, x);
                    continue;
                }
                if (x.Verb == (byte)EmgVerb.Field)
                {
                    // EMG2-FLD-01：事件门控的场写端在事件位置展开（单位级事件 = 本单位处；接入冲击 = 接入时以本机为心；被击 = 按宿主载体展开，见 EmgGatedField）
                    if (EmgGatedField(ref d, x, e, host, out double2 fat, out float fr)) // EMG4-CLEAN-01（DEBT-EMG4FUSE02-03）：场没生成（冷却 / 场位满）不挂落点、不执行 B
                    {
                        int fo = e.OtherKind == EmgEntKind.Unit && e.Other != 0 ? d.SlotOf(e.Other) : -1;
                        EmgAnchorAt(ref d, EmgAnchorConst.Field, host, e.Pos, fo, x.Ordinal); // EMG3-FUSE-01
                        EmgFieldAnchors(ref d, host, x, fat, fo, fr); // EMG4-FUSE-02：场内受力 / 被扫过的单位（按场实际中心 / 半径）
                    }
                    continue;
                }
                float a = (x.Flags & EmgInstrFlags.MagA) != 0 ? x.A * x.Amount : x.A;
                float b = (x.Flags & EmgInstrFlags.MagB) != 0 ? x.B * x.Amount : x.B;
                if (!(a > 0f) || !(b > 0f))
                {
                    continue;
                }
                // EMG4-FUSE-02（FG19A 第 4.2 节）：读 evt.fire 的物质排放 = 沿途排放，按长度均分段点排斑块（取代过渡读法 trail）
                if (x.Verb == emit && e.Kind == EmgEventKind.Fire && EmgPatchesOn(ref d) && EmgTrailEmit(ref d, x, e, host, a, b, n.FirstInstr, end))
                {
                    continue;
                }
                // EMG2-MAT-01：斑块层开着时排放写端生成物质斑块（FG19A 第 0.3 节口径 3 的阶段 2 形态），不再落读法驻留区
                if (x.Verb == emit && EmgEmitPatch(ref d, x, e.Pos, a, b, host.Owner, e.Chain, e.Depth))
                {
                    continue;
                }
                int bit = (int)math.round(x.C) - 1;
                uint mask = bit >= 0 && bit < 32 ? 1u << bit : 0u;
                int owner = host.Owner >= 0 && host.Owner < d.Count ? host.Owner : -1;
                if (SpawnZone(ref d, owner, (CombatFaction)host.Faction, e.Pos, a, b, 0f, 0f, 1f, mask, 0f, 0f, 0f, 0f))
                {
                    SetLastZoneLook(ref d, x.Verb == (byte)EmgVerb.Inject ? CombatZoneLook.Pulse : CombatZoneLook.Pool); // 注电 = 外扩冲击环，排放 = 液池
                    NoteReading(ref d, CombatConst.ReadingFeedZone, e.Pos, owner);
                }
                if (x.Verb == (byte)EmgVerb.Inject)
                {
                    EmgClueSeen(ref d, e.Chain, x.Port); // EMG4-FUSE-02：事件门控的注能（含腐蚀度）在链上出现
                }
                if (x.Verb == (byte)EmgVerb.Inject && EmgProgAnchored(ref d, host.Prog))
                {
                    // EMG4-FUSE-02（DEBT-EMG3FUSE01-01“事件门控的注能”）：过热泄放 / 回波 / 谐振这类事件门控的注能——脉冲处的单位与被加热 / 充电的斑块是落点
                    EmgInjectSpots(ref d, host, x.Port, e.Pos, a, x.Ordinal);
                    EmgAnchorAt(ref d, EmgAnchorConst.Inject, host, e.Pos, e.OtherKind == EmgEntKind.Unit && e.Other != 0 ? d.SlotOf(e.Other) : -1, x.Ordinal);
                }
            }
        }

        /// <summary>同一程序序号里有读 <paramref name="evPort"/> 的 trigger 读端（这条指令的生效事件就是本事件）；<paramref name="any"/> = 循环的周期执行不论生效事件。</summary>
        private static bool EmgTrigOn(ref CombatData d, int first, int end, byte ordinal, ushort evPort, bool any)
        {
            for (int j = first; j < end && !any; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                any = r.Ordinal == ordinal && r.Role == EmgConst.RoleTrigger && r.Port == evPort;
            }
            return any;
        }

        /// <summary>
        /// EMG1-FIX-01（FG19A 第 1.2 节第 26 行 / D-14，FG-GAP-153）：根节点写 ent.projectile（spawn）、同一条读的事件入队时——命中了一个单位，就记一条追射：
        /// 到点（a = 延迟秒数，fg.TbPortCarrier）对同一目标补 1 发（幅度 = 份数），伤害 = 宿主这一击的伤害 × param2（0.5；布区落区 = 一个节拍的伤害，不是每秒伤害）。格斗 / 力场只按本次出手的主目标一次（与触发器同口径）。
        /// 追射是这条事件的产物：链深已到 3 不再生成（EmgCanGenerate）。O(根节点指令数)。
        /// </summary>
        private static void EmgQueueFollow(ref CombatData d, in EmgEvent e, in EmgCtx host, in EmgInstr x)
        {
            if (e.OtherKind != EmgEntKind.Unit || e.Other == 0 || (host.PFlags & EmgProjFlags.Heal) != 0)
            {
                return;
            }
            if (host.EntKind == EmgEntKind.Unit && (host.Carrier == (byte)CombatCarrier.Melee || host.Carrier == (byte)CombatCarrier.Aura) && e.Other != host.MainTarget)
            {
                return;
            }
            if (!EmgCanGenerate(ref d, e))
            {
                return;
            }
            if (d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                EmgStat(ref d, 4);
                return;
            }
            EmgClueSeen(ref d, e.Chain, EmgConst.PortProjectile); // EMG4-FUSE-02：链上出现了追射（ent.projectile）
            int retarget = EmgFollowRetarget(ref d, host, e.Other, x.Ordinal); // EMG4-FUSE-02：接线的追射改打 5 米内另一目标、B 刷写给它；没有第二目标 = 重复无效，照常打原目标
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendFollow, Armed = 1, Count = (byte)math.clamp(Round(x.Amount), 1, EmgConst.DefaultMaxChildren), Flags = retarget != 0 ? EmgProjFlags.AnchorFollow : EmgProjFlags.None,
                At = d.Scalars[0].Time + math.max(0f, x.A), Pos = e.Pos, Other = retarget != 0 ? retarget : e.Other, Owner = host.OwnerId, Weapon = host.Weapon,
                Damage = (host.Strike > 0f ? host.Strike : host.Damage) * (x.Param2 > 0f ? x.Param2 : EmgSubRatio(ref d)), Faction = host.Faction, Prog = e.Prog, Node = 0, Chain = e.Chain,
                Depth = (byte)math.min(e.Depth + 1, EmgConst.MaxDepth), SubForm = host.Carrier,
            });
        }

        /// <summary>
        /// 追射到点：目标或发射者已不在 = 作废（不补射）。射弹 / 无人机载体：从发射者（机器）处朝目标补射弹体——弹速 / 半径 / 寿命取武器，只带本节点（根）的弹道修饰，
        /// 不带伤害读法、不再触发程序（每条事件读端计数置满，FGR-EMG-065 同一机制），所以追射弹自己不会再追射；格斗 / 力场 / 布区：立刻对同一目标追加一记。
        /// 返回 false = 弹体池满，留到下一步（不丢）。
        /// </summary>
        private static bool EmgFireFollow(ref CombatData d, ref EmgSpawn s)
        {
            int t = d.SlotOf(s.Other);
            int o = d.SlotOf(s.Owner);
            if (t < 0 || !d.IsAlive(t) || o < 0 || !d.IsAlive(o))
            {
                s.Count = 0;
                return true;
            }
            bool shoots = s.SubForm == (byte)CombatCarrier.Projectile || s.SubForm == (byte)CombatCarrier.Summon;
            if (!shoots)
            {
                EmgCtx saved = d.EmgCtx;
                d.EmgCtx = new EmgCtx { Prog = -1, Owner = o, OwnerId = s.Owner, Faction = s.Faction, Weapon = -1 };
                if ((s.Flags & EmgProjFlags.AnchorFollow) != 0)
                {
                    // EMG4-FUSE-02：格斗 / 力场的追射当场追加一记——落点 = 第二目标处，以母程序为宿主执行 B（Prog −1 的伤害上下文不再进命中注能）
                    EmgAnchorAt(ref d, EmgAnchorConst.Follow, new EmgCtx { Prog = s.Prog, Node = 0, Chain = s.Chain, Depth = s.Depth, Owner = o, OwnerId = s.Owner, Faction = s.Faction, Weapon = s.Weapon,
                        EntKind = EmgEntKind.Unit, Ent = s.Owner, Carrier = s.SubForm }, d.Pos[t], t);
                }
                DamageUnit(ref d, t, s.Damage, o);
                d.EmgCtx = saved;
                s.Count--;
                return true;
            }
            if (s.Weapon < 0 || s.Weapon >= d.Weapons.Length)
            {
                s.Count = 0;
                return true;
            }
            if (d.Projectiles.Length >= d.Config.ProjectileCapacity)
            {
                return false;
            }
            CombatWeapon wp = d.Weapons[s.Weapon];
            float speed = wp.ProjectileSpeed > 0f ? wp.ProjectileSpeed : d.EmgSubForms[EmgConst.SubBullet].Speed;
            float radius = wp.ProjectileRadius > 0f ? wp.ProjectileRadius : d.EmgSubForms[EmgConst.SubBullet].Radius;
            float2 dir = EmgDir(d.Pos[t] - d.Pos[o]);
            if (math.lengthsq(dir) < 1e-12f)
            {
                dir = new float2(0f, 1f);
            }
            double2 start = d.Pos[o] + (double2)(dir * (d.Radius[o] + radius));
            float life = wp.ProjectileLife > 0f ? wp.ProjectileLife : math.max(wp.Range, 1f) * 1.2f / math.max(0.01f, speed); // 与根弹体同一口径（EmgTrySpawnFlight）
            EmgScalars es = d.EmgS[0];
            int id = es.NextProjId++;
            d.EmgS[0] = es;
            var pr = new CombatProjectile
            {
                Id = id, Prog = s.Prog, Chain = s.Chain, Node = 0, Depth = s.Depth, Pos = start, Prev = start, Vel = dir * speed, Owner = s.Owner, Weapon = -1,
                Damage = s.Damage, Radius = radius, Life = life, Faction = (CombatFaction)s.Faction, Flags = EmgProjFlags.Flight | (s.Flags & EmgProjFlags.AnchorFollow), TrigUsed = uint.MaxValue,
            };
            EmgInitFlight(ref d, ref pr, d.Pos[t], s.Other, true);
            d.Projectiles.Add(pr);
            CombatCounters c = d.Counters[0];
            c.ProjectilesSpawned++;
            d.Counters[0] = c;
            s.Count--;
            return true;
        }

        private static void EmgQueueChildren(ref CombatData d, in EmgEvent e, in EmgCtx host, float2 dir, ulong allow = ulong.MaxValue)
        {
            EmgClueSeen(ref d, e.Chain, EmgRuleConst.PortTrig); // EMG4-FUSE-02：触发器放子载荷（grm.trig）；节拍事件放的另记 tmp.beat
            if (e.Kind == EmgEventKind.Beat)
            {
                EmgClueSeen(ref d, e.Chain, EmgRuleConst.PortBeat);
            }
            EmgProgram p = d.EmgPrograms[e.Prog];
            int maxChildren = EmgMaxChildren(ref d);
            int pf = -1, pe = -1;
            for (int j = 1; j < p.NodeCount; j++)
            {
                int ni = p.FirstNode + j;
                if (ni >= d.EmgNodes.Length)
                {
                    break;
                }
                EmgNode c = d.EmgNodes[ni];
                if (c.Parent != e.Node || c.TrigEvent != e.Kind || c.TrigCount == 0 || (c.MetaMask > 0 && !EmgMetaAllowed(allow, (byte)(c.MetaMask - 1))))
                {
                    continue; // EMG3-META-01：放出它的触发器被元程序关掉（条件为假 / 随机没选中 / 延后到点再放）
                }
                // DEBT-EMG3CHS01-02（FGR-EMG-141）：放出它的那条触发器读 chs.* condition、出手者（host.Owner）此刻没有这个状态 → 不放。
                // 单位 / 弹体 / 区域 / 无人机宿主同一判定；有 chs.* condition 的程序编译期必记 MetaMask（EmgProgramCompiler.TempoPass），MetaMask = 0 的程序没有可判的，零开销
                if (c.MetaMask > 0 && (pf >= 0 || EmgNodeSpan(ref d, e.Prog, e.Node, out pf, out pe)) && EmgChsBlocked(ref d, host.Owner, pf, pe, (byte)(c.MetaMask - 1)))
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
                    // EMG4-FUZZ-01（模糊测试器 #321 发现）：命中终结（evt.expire，Code = ExpireHit）在被击单位的位置放子载荷——与命中触发同口径不再撞那个单位，
                    // 否则子载荷一出生就在被击单位体内、当步全部撞回它（子母弹“抛出 4 枚”变成原地再打 4 下，挂在下面的定时永远到不了点）
                    Other = e.Kind == EmgEventKind.Hit || (e.Kind == EmgEventKind.Expire && e.Code == EmgCode.ExpireHit) ? e.Other : 0, Ent = (int)e.Kind,
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
            EmgSplitOut(ref d, e, (int)e.Kind, count, gen);
        }

        /// <summary>
        /// 一次放出子载荷之后（正常唤起与元程序重放 / 循环 <see cref="EmgMetaChildren"/> 同一出口，EMG3-E2E-01 关闭 DEBT-EMG3META01-04）：计一次放出、写分裂链日志、按需发 evt.split。
        /// <paramref name="key"/> = 唤起它的事件码（循环 = evt.timer）。
        /// </summary>
        internal static void EmgSplitOut(ref CombatData d, in EmgEvent e, int key, int count, byte gen)
        {
            EmgStat(ref d, 0);
            // EMG1-CHAIN-01：触发放出子载荷（链深 + 1；Key = 唤起它的事件码，Aux = 子代号）
            EmgChainLog(ref d, EmgChainNode.Trigger, e.Chain, (byte)(e.Depth + 1), key, e.EntKind == EmgEntKind.Unit ? e.Ent : 0, e.Pos, 0, gen);
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
                if (s.Kind == EmgConst.PendPatch)
                {
                    if (!EmgPendPatch(ref d, s))
                    {
                        d.EmgPending[write++] = s; // EMG2-MAT-01：本步生成额度已用完，留到下一步（生成时才计寿命）
                    }
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
                if (s.Kind == EmgConst.PendFollow)
                {
                    // EMG1-FIX-01：追射到点才补（暂停不步进 = 不到点；倍速只改每帧步数，按游戏时间到点）
                    if (now + 1e-9 < s.At)
                    {
                        d.EmgPending[write++] = s;
                        continue;
                    }
                    while (s.Count > 0 && EmgFireFollow(ref d, ref s))
                    {
                    }
                    if (s.Count > 0)
                    {
                        EmgStat(ref d, 2);
                        d.EmgPending[write++] = s;
                    }
                    continue;
                }
                if (s.Kind == EmgConst.PendFrag)
                {
                    // EMG1-FW-01（FGR-EMG-063）：池满顺延的破片，有空位就放出（方向 / 速度 / 射程按排队时记下的）
                    if (!EmgFragmentTry(ref d, s.Pos, s.Dir, s.Owner, (CombatFaction)s.Faction, s.Depth, s.Chain, s.Damage, s.ZoneRadius, s.ZoneLife))
                    {
                        EmgStat(ref d, 2);
                        d.EmgPending[write++] = s;
                    }
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
                    EmgBusStats gs = d.EmgStats[0];
                    gs.GenSeen |= 1 << s.Gen; // EMG2-DISC-01：语法发现“第一次组成第 n 代子弹药”（FGR-EMG-183）
                    d.EmgStats[0] = gs;
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

        /// <summary>
        /// EMG1-FUZZ-01（模糊测试器发现的死指令）：载荷节点下挂着“定时”触发的子节点时，载荷至少飞到定时到点（+ <see cref="TimerLifeMargin"/>）——
        /// 否则连射器根弹体（射程 12 ÷ 弹速 30 × 1.2 = 0.48 秒）与子弹（0.5 秒）装 [延时引信]（0.6 秒）永远先到寿命、定时永不到点。按端口（定时触发参数）判断，O(节点数 ≤ 8)。
        /// </summary>
        internal static float EmgTimerLife(ref CombatData d, int prog, int node, float life)
        {
            if (prog < 0 || prog >= d.EmgPrograms.Length)
            {
                return life;
            }
            EmgProgram p = d.EmgPrograms[prog];
            float timer = 0f;
            for (int k = 0; k < p.NodeCount && p.FirstNode + k < d.EmgNodes.Length; k++)
            {
                EmgNode n = d.EmgNodes[p.FirstNode + k];
                if (n.Parent == node && n.TrigEvent == EmgEventKind.Timer)
                {
                    timer = math.max(timer, n.TrigParam);
                }
            }
            return timer > 0f ? math.max(life, timer + TimerLifeMargin) : life;
        }

        /// <summary>挂定时子节点的载荷寿命比定时多留的游戏秒（6 步）。</summary>
        internal const float TimerLifeMargin = 0.1f;

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
                // EMG1-FW-01（DEBT-EMG1GRM01-05）：子区 / 小脉冲按自己节点的修饰与数值行结算（CombatLogic.ChildMods）
                if (!EmgSpawnSubZone(ref d, ref s, code, form, damage, ownerSlot, faction))
                {
                    return false;
                }
                if (s.Chain > 0)
                {
                    EmgLiveAdd(ref live, s.Chain);
                }
            }
            else
            {
                // EMG1-FW-01（DEBT-EMG1GRM01-05）：子弹药节点上的多份载荷分成真实弹丸（一份的弹丸一起放，弹体池放不下整份留到下一步，扇角不丢）、伤害倍率
                EmgModsOf(ref d, s.Prog, s.Node, out EmgMods cm, 0UL, ownerSlot);
                int pellets = cm.Multi > 1 ? cm.Multi : 1;
                if (d.Projectiles.Length + pellets > d.Config.ProjectileCapacity)
                {
                    return false;
                }
                // EMG3-FW-02（FG19A N42 / N31；FGR-EMG-172“对齐时把本拍各机器的触发器子载荷同时放出 = 齐射”）：相位对齐拍 / 信号脉冲触发的子载荷没有命中目标——
                // 放出的第 k 份朝放出点第 k 近的敌人（齐射“向最近的 2 个敌人各放 1 份”：读 tmp.sync 的节拍触发器；脉冲在脉冲点放、朝那里最近的敌人），没有敌人才径向。其余节拍触发器（时钟脉冲）照旧径向
                int k = s.Ent == (int)EmgEventKind.Hit || s.Ent == (int)EmgEventKind.Ping || (s.Ent == (int)EmgEventKind.Beat && EmgSyncTrigChild(ref d, s)) ? EmgSeekIndex(ref d, s) : -1;
                float2 dir = k >= 0 ? EmgDir(d.Pos[k] - s.Pos) : EmgRadial(ref d, s);
                if (k < 0 && s.Ent == (int)EmgEventKind.Timer && code != EmgConst.SubWave && math.lengthsq(s.Dir) > 1e-12f)
                {
                    // EMG1-FIX-01（FG19A 第 1.1 节第 6 行“出膛 0.6 秒空爆，向前扇形放出 3 枚”）：在飞母载荷的定时空爆沿飞行方向扇形摊开（第 k 份均分扇角），不再径向一圈；格斗余波（出手点放出）仍径向
                    int total = math.max(1, s.Done + s.Count);
                    float off = total > 1 ? EmgConst.TimerFanDeg * ((float)s.Done / (total - 1) - 0.5f) : 0f;
                    dir = Rotate(math.normalize(s.Dir), math.radians(off));
                }
                float mul = cm.DmgMul;
                ulong gatedRows = EmgGatedUnits(ref d, s.Prog, s.Node);
                if (gatedRows != 0UL)
                {
                    // EMG3-E2E-01（DEBT-EMG3META01-03）：条件 / 随机下游的伤害倍率不在放出时乘，命中那一刻按条件重判（EmgSubCondDmg）
                    mul = EmgModsOf(ref d, s.Prog, s.Node, out EmgMods cb, gatedRows, ownerSlot) ? cb.DmgMul : 0f;
                }
                float each = damage * (mul > 0f ? mul : 1f) * (pellets > 1 ? (cm.MultiRatio > 0f ? cm.MultiRatio : 0.4f) : 1f);
                float reach = form.Speed * form.Life;
                float subMass = EmgSubMass(ref d, s); // EMG1-FUZZ-01（DEBT-EMG1ENT01-08）：子弹药质量 = 母载荷 ÷ 份数
                for (int q = 0; q < pellets; q++)
                {
                    float2 pd = pellets > 1 ? EmgPelletDir(ref d, s.Prog, s.Node, s.Pos, dir, reach, q, pellets, cm.MultiFan > 0f ? cm.MultiFan : 30f) : dir;
                    EmgScalars es = d.EmgS[0];
                    int id = es.NextProjId++;
                    d.EmgS[0] = es;
                    var sub = new CombatProjectile
                    {
                        Id = id, Prog = s.Prog, Chain = s.Chain, Node = s.Node, Gen = s.Gen, Depth = s.Depth, Flags = EmgProjFlags.Sub | (s.Flags & EmgProjFlags.Heal),
                        Pos = s.Pos, Prev = s.Pos, Vel = pd * form.Speed, Owner = s.Owner, Weapon = s.Weapon, Damage = each, Radius = form.Radius, Life = EmgTimerLife(ref d, s.Prog, s.Node, form.Life),
                        Faction = faction, IgnoreUnit = s.Other, Mass = subMass,
                    };
                    EmgInitFlight(ref d, ref sub, s.Pos + (double2)(pd * reach), k >= 0 ? d.Id[k] : 0, false); // EMG1-GRM-01：子弹药按自己节点（含继承）的修饰
                    d.Projectiles.Add(sub);
                    // EMG3-TMP-01：子弹药节点读 evt.beat 时在放出点留节拍锚点（子弹药寿命 ≤ 0.5 秒，自己撑不到相位）；EMGR-TRIG-01（FG-GAP-218）：每枚子弹药各留一个（FG19A fw_mix_rustbomb“每枚子弹药”）
                    EmgBeatAnchorSub(ref d, s, pd, each);
                    CombatCounters c = d.Counters[0];
                    c.ProjectilesSpawned++;
                    d.Counters[0] = c;
                    if (s.Chain > 0)
                    {
                        EmgLiveAdd(ref live, s.Chain);
                    }
                }
            }
            s.Done++;
            EmgActNode(ref d, s.Prog, s.Node); // EMG1-FUZZ-01：子节点执行一次（只在模糊测试器开启计数时）
            EmgStat(ref d, 1);
            return true;
        }

        /// <summary>EMG3-FW-02（FGR-EMG-172 齐射，FG19A N42）：这份子载荷由读 tmp.sync condition 的节拍触发器放出（只在相位对齐拍上放）。按端口判，不认固件 ID。O(父节点指令数)，只在节拍子载荷生成时。</summary>
        private static bool EmgSyncTrigChild(ref CombatData d, in EmgSpawn s)
        {
            if (s.Prog < 0 || s.Prog >= d.EmgPrograms.Length || !EmgNodeSpan(ref d, s.Prog, s.Node, out _, out _))
            {
                return false;
            }
            EmgNode c = d.EmgNodes[d.EmgPrograms[s.Prog].FirstNode + s.Node];
            if (c.MetaMask == 0 || c.Parent < 0 || !EmgNodeSpan(ref d, s.Prog, c.Parent, out int first, out int end))
            {
                return false;
            }
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == c.MetaMask - 1 && r.Port == EmgTempoConst.PortSync && r.Role == EmgConst.RoleCondition)
                {
                    return true;
                }
            }
            return false;
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
            if (t >= 0 && d.IsAlive(t) && math.lengthsq(s.Dir) > 1e-12f && EmgPellets(ref d, wp, d.SlotOf(s.Owner), out _, out _) > 1)
            {
                to = s.Pos + (double2)s.Dir * math.distance(d.Pos[t], s.Pos); // EMG1-GRM-01 审查修复：多份载荷排队的弹丸保留各自扇角（Dir = 出手时这一份的方向）
            }
            if (!EmgTrySpawnFlight(ref d, s.Pos, 0f, to, s.Owner, s.Weapon, wp, s.Damage, (CombatFaction)s.Faction, s.Flags, s.Chain, t >= 0 && d.IsAlive(t) ? s.Other : 0, s.Prog))
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
                || EmgReadSlot(ref d, EmgShotProg(ref d, wp.Program), 0, EmgEventKind.Timer) < 0)
            {
                return;
            }
            float secs = EmgReadParam(ref d, EmgShotProg(ref d, wp.Program), 0, EmgEventKind.Timer);
            if (!(secs > 0f) || d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                return;
            }
            EmgCtx c = d.EmgCtx;
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendTimer, Armed = 1, Count = 1, At = d.Scalars[0].Time + secs, Pos = d.Pos[a], Prog = EmgShotProg(ref d, wp.Program), Node = 0, Chain = c.Chain,
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
            if (EmgHasMeta(ref d, c.Prog))
            {
                EmgMetaLaunch(ref d, c.Prog, c.Node, a, -1, EmgZoneHost(ref d, z), z.Pos, float2.zero, 0.0, true); // EMG3-META-01：落区存续期间每 0.5 秒执行循环下游段（与区域节拍无关，FGR-EMG-043）
            }
        }

        /// <summary>带程序的区域到期：evt.expire（Code = 寿命，宿主 = 区域）；子区在到期处执行自己节点的指令（一次结算）。</summary>
        private static void EmgZoneExpire(ref CombatData d, in CombatZone z)
        {
            if (z.EmgSub != 0)
            {
                int zt = -1; // EMG3-META-01：布区的条件对区域内首个单位 / 区域本身求值（FGR-EMG-043）
                uint zm = EmgHasMeta(ref d, z.EmgProgRef - 1) ? EmgZoneSubjOf(ref d, z, ref zt) : EmgNotZone;
                EmgNodeEffects(ref d, z.EmgProgRef - 1, z.EmgNode, z.Pos, d.SlotOf(z.Owner), z.Faction, z.EmgChain, z.EmgDepth,
                    EmgMetaAllowNow(ref d, z.EmgProgRef - 1, z.EmgNode, EmgEventKind.Expire, d.SlotOf(z.Owner), zt, z.EmgChain, z.EmgId, zm));
                EmgSubZoneEcho(ref d, z); // EMG1-FW-01（DEBT-EMG1GRM01-05）：子区收回时的回响
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
                EmgActNode(ref d, prog, 0); // EMG1-FUZZ-01：没有事件监听也是一次修复出手（无条件修饰 / 效果照样执行；只在模糊测试器开启计数时，不改状态）
                return;
            }
            EmgHostBegin(ref d, prog, i, d.Id[i], EmgEntKind.Unit, (byte)EmgCode.SrcOther, d.Pos[i]);
            EmgCtx c = d.EmgCtx;
            c.Damage = amount;
            c.Weapon = d.Weapon[i];
            c.OwnerId = d.Id[i];
            c.Faction = d.Faction[i];
            c.MainTarget = d.Id[target];
            c.PFlags = EmgProjFlags.Heal;
            c.HitCode = EmgCode.HitHeal;
            int w = d.Weapon[i];
            c.Instant = (byte)(w >= 0 && w < d.Weapons.Length && !EmgFlightOn(d.Weapons[w]) ? 1 : 0); // EMG1-FUZZ-01：当场修复 = 载荷当场终结（EmgShotEnd 发 evt.expire）
            d.EmgCtx = c;
            EmgHostFire(ref d, d.Pos[i], target);
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
            int wprog = EmgShotProg(ref d, wp.Program); // EMG3-META-01：本次出手选中的树
            int chain = wprog >= 0 && d.EmgCtx.Prog == wprog && d.EmgCtx.Chain > 0 ? d.EmgCtx.Chain : 0;
            EmgProjFlags flags = EmgProjFlags.Flight | extra;
            // EMG1-GRM-01：多份载荷（霰射：一次出手 N 发真实弹丸，扇面均分、各 a 倍伤害，各自命中、各自触发）。一份时逐字同旧。
            int pellets = EmgPellets(ref d, wp, a, out float ratio, out float fan);
            if (pellets > 1 && chain == 0 && wprog >= 0)
            {
                EmgScalars es0 = d.EmgS[0];
                chain = es0.NextChain++;
                d.EmgS[0] = es0;
                EmgChainLog(ref d, EmgChainNode.Shot, chain, 0, 0, d.Id[a], d.Pos[a]); // EMG1-CHAIN-01
            }
            double2 aim = d.Pos[t] - d.Pos[a];
            double aimLen = math.length(aim);
            float2 aimDir = aimLen > 1e-9 ? (float2)(aim / aimLen) : new float2(0f, 1f);
            for (int q = 0; q < pellets; q++)
            {
                // EMG1-FW-01（DEBT-EMG1GRM01-03）：霰射读 ter.cover 时，扇面里被掩体挡住的那份弹丸往外侧绕开（EmgPelletDir）
                double2 to = pellets > 1 ? d.Pos[a] + (double2)EmgPelletDir(ref d, wprog, 0, d.Pos[a], aimDir, aimLen, q, pellets, fan) * aimLen : d.Pos[t];
                EmgFireOne(ref d, a, t, w, wp, wprog, wp.Damage * ratio, to, flags, ref chain);
            }
        }

        private static void EmgFireOne(ref CombatData d, int a, int t, int w, in CombatWeapon wp, int wprog, float damage, double2 to, EmgProjFlags flags, ref int chain)
        {
            if (EmgTrySpawnFlight(ref d, d.Pos[a], d.Radius[a], to, d.Id[a], w, wp, damage, (CombatFaction)d.Faction[a], flags, chain, d.Id[t], wprog))
            {
                return;
            }
            if (d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                EmgStat(ref d, 4);
                return;
            }
            if (chain == 0 && wprog >= 0)
            {
                EmgScalars es = d.EmgS[0];
                chain = es.NextChain++;
                d.EmgS[0] = es;
                EmgChainLog(ref d, EmgChainNode.Shot, chain, 0, 0, d.Id[a], d.Pos[a]); // EMG1-CHAIN-01
            }
            EmgStat(ref d, 2);
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendRoot, Armed = 1, Count = 1, Pos = d.Pos[a], Dir = EmgDir(to - d.Pos[a]), Other = d.Id[t], Owner = d.Id[a], Weapon = w,
                Damage = damage, Faction = d.Faction[a], Flags = flags, Prog = wprog, Chain = chain,
            });
        }

        /// <summary>无人机开火实体化（武器 SpawnFlight）：从无人机处向目标发一发根弹体（伤害 = 无人机伤害，命中按无人机读法）。</summary>
        internal static void EmgDroneFlight(ref CombatData d, in CombatDrone dr, int target, in CombatWeapon wp)
        {
            EmgStat(ref d, 7);
            int wprog = EmgShotProg(ref d, wp.Program);
            int chain = wprog >= 0 && d.EmgCtx.Prog == wprog && d.EmgCtx.Chain > 0 ? d.EmgCtx.Chain : 0;
            EmgProjFlags flags = EmgProjFlags.Flight | EmgProjFlags.ViaDrone;
            int owner = d.SlotOf(dr.Owner);
            if (EmgTrySpawnFlight(ref d, dr.Pos, 0f, d.Pos[target], dr.Owner, dr.Weapon, wp, dr.Damage, dr.Faction, flags, chain, d.Id[target], wprog) || d.EmgPending.Length >= EmgConst.PendingCapacity)
            {
                return;
            }
            EmgStat(ref d, 2);
            d.EmgPending.Add(new EmgSpawn
            {
                Kind = EmgConst.PendRoot, Armed = 1, Count = 1, Pos = dr.Pos, Dir = EmgDir(d.Pos[target] - dr.Pos), Other = d.Id[target], Owner = owner >= 0 ? d.Id[owner] : 0,
                Weapon = dr.Weapon, Damage = dr.Damage, Faction = (byte)dr.Faction, Flags = flags, Prog = wprog, Chain = chain,
            });
        }

        private static bool EmgTrySpawnFlight(ref CombatData d, double2 from, float fromRadius, double2 to, int ownerId, int weapon, in CombatWeapon wp, float damage,
            CombatFaction faction, EmgProjFlags flags, int chain, int targetId, int prog)
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
            bool opened = chain == 0 && prog >= 0;
            if (opened)
            {
                chain = es.NextChain++;
            }
            d.EmgS[0] = es;
            if (opened)
            {
                EmgChainLog(ref d, EmgChainNode.Shot, chain, 0, 0, ownerId, from); // EMG1-CHAIN-01
            }
            // 寿命显式由热更层写（射程 ÷ 弹速 × 1.2，FG19A 第 2.1.1 节）；不依赖 0 值语义。弹速缺值时弹体不动、寿命到即止——不退回即时命中（缺值由表校验与 FgChannelAdmissionSelfCheck 拦下）。
            float life = wp.ProjectileLife > 0f ? wp.ProjectileLife : wp.Range * 1.2f / math.max(0.01f, wp.ProjectileSpeed);
            life = EmgTimerLife(ref d, prog, 0, life);
            var pr = new CombatProjectile
            {
                Id = id, Prog = prog, Chain = chain, Pos = start, Prev = start, Vel = dir * wp.ProjectileSpeed, Owner = ownerId, Weapon = weapon, Damage = damage,
                Radius = wp.ProjectileRadius, Life = life, Faction = faction, Flags = flags,
                IgnoreUnit = (flags & EmgProjFlags.Heal) != 0 ? ownerId : 0, // EMG1-FW-01：治疗根弹体（维修束）不碰出手者自己（出膛点贴着它的半径）
            };
            pr.HomingTarget = targetId; // EMG1-E2E-01：记下开火目标（没有寻的修饰时只用于“目标所在的障碍圆不挡”，不转向）
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
            if (pr.Prog >= 0 && pr.Node == 0)
            {
                // EMG4-CLEAN-01（FG-GAP-207 / 208、DEBT-EMG3META01-01）：根节点条件 / 随机下游的读法行按命中这一刻的放行位补到读法副本上（与无人机命中 / 当场结算同口径）
                EmgGatedReading(ref d, pr.Prog, EmgCondAllow(ref d, pr.Prog, 0, EmgEventKind.Hit, owner, hit, pr.Chain, pr.Id), ref wp.Reading);
            }
            bool viaDrone = (pr.Flags & EmgProjFlags.ViaDrone) != 0;
            if (viaDrone)
            {
                wp.Damage = pr.Damage;
                wp.Reading.EscortDrones = 0;
            }
            CombatReading r = wp.Reading;
            double now = d.Scalars[0].Time;
            bool wasMarked = d.MarkedUntil[hit] > now;
            float pierce = 0f; // EMG2-REACT-01：熔穿过载旧穿甲退役（FG19B P03）
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
                if (EmgRootWriterMask(ref d, d.EmgCtx.Prog, EmgRuleConst.PortMarked) == 0UL)
                {
                    // EMG4-CLEAN-01（DEBT-EMG4FUSE02-06）：这是功能组件的标记；根节点有写 evt.marked 的固件时，那条固件的标记在命中处按自己的序号落点（EmgRootMarkWrite），组件标记不再连带执行它的 B
                    EmgAnchorAt(ref d, EmgAnchorConst.Marked, d.EmgCtx, d.Pos[hit], hit); // EMG4-FUSE-02
                }
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
        /// 经 fg.TbPortCarrier 映射到读法驻留区——A = 半径、B = 秒数、C = 状态位号 + 1（编译器按物质写好；FG19A 第 0.3 节口径 3）。
        /// EMG2-MAT-01：斑块层开着（emg.stage ≥ 2）时改为生成物质斑块（物质码 = 端口码 − 400，份量 = 声明幅度，链 / 链深取这发子弹药的）。
        /// </summary>
        private static void EmgNodeEffects(ref CombatData d, int prog, int node, double2 pos, int ownerSlot, CombatFaction faction, int chain = 0, byte depth = 0, ulong allow = ulong.MaxValue,
            bool emitOnly = false)
        {
            int verb = d.Config.EmgVerbEmit;
            if (prog < 0 || prog >= d.EmgPrograms.Length)
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
            bool chs = ownerSlot >= 0 && EmgChsAny(ref d, n.FirstInstr, end);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (!EmgMetaAllowed(allow, x.Ordinal) || (chs && EmgChsBlocked(ref d, ownerSlot, n.FirstInstr, end, x.Ordinal)))
                {
                    continue; // EMG3-META-01：元程序关掉 / 推迟的下游指令；DEBT-EMG3CHS01-02：出手者此刻没有同序号 chs.* condition 要的状态（同 EmgRootGated）
                }
                if (!emitOnly && x.Verb == (byte)EmgVerb.Field && x.Role > EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0)
                {
                    // EMG2-FLD-01：子载荷落点 / 到期 / 撞障碍结算处展开场（与命中注能同一步同一处只生成一个）
                    if (EmgFieldFromInstr(ref d, x, pos, EmgIdOf(ref d, ownerSlot), faction, chain, depth) && i < d.EmgAct.Length)
                    {
                        d.EmgAct[i] = d.EmgAct[i] + 1;
                    }
                    continue;
                }
                if (verb <= 0 || x.Role <= EmgConst.RoleLastRead || x.Verb != verb || !(x.A > 0f) || !(x.B > 0f) || (emitOnly && (x.Flags & EmgInstrFlags.Overflow) != 0))
                {
                    continue;
                }
                if (i < d.EmgAct.Length)
                {
                    // EMG2-MAT-01：落点 / 到期结算的排放行真的执行了——按行计激活（模糊测试器零反应判定；带事件触发的序号 EmgActNode 不计，漏记会误判死指令）
                    d.EmgAct[i] = d.EmgAct[i] + 1;
                }
                if (EmgEmitPatch(ref d, x, pos, x.A, x.B, ownerSlot, chain, depth))
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
            d.EmgHash.ClearWreckCells();
            d.EmgHash.ClearPatchCells();
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
            // EMG2-MAT-01（FGR-EMG-081）：斑块 × 单位 / 斑块 × 斑块 / 弹体 × 斑块都查这一张哈希（不做全表两两扫描，顺带偿还 DEBT-FG2FW02-06 的斑块那一半）。
            int blocks = 0;
            uint blockSig = 0u;
            int absorbers = 0;
            for (int k = 0; k < d.Patches.Length; k++)
            {
                CombatZone z = d.Patches[k];
                if (z.Matter != 0)
                {
                    EmgHashAdd(ref d, z.Pos, z.Radius, EmgHash.KindPatch, k);
                    absorbers += IsAbsorber(z) ? 1 : 0;
                    if (PatchBlocks(ref d, z))
                    {
                        blocks++; // EMG2-REACT-01：阻路斑块数（移动查阻路的开关）
                        blockSig = (blockSig ^ (uint)z.EmgId) * 16777619u + 1u; // EMG2-PERSIST-01：集合变了才重推寻路镜像
                    }
                }
            }
            EmgScalars es = d.EmgS[0];
            es.BlockLive = blocks;
            es.BlockSig = blockSig;
            es.AbsorbLive = absorbers;
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
            if (kind == EmgHash.KindWreck)
            {
                d.EmgHash.MarkWreckFine(d.EmgHash.Entries[e]);
            }
            int x0 = EmgHash.CellOf(p.x - r), x1 = EmgHash.CellOf(p.x + r), y0 = EmgHash.CellOf(p.y - r), y1 = EmgHash.CellOf(p.y + r);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    d.EmgHash.Cells.Add(EmgHash.Key(cx, cy), e);
                    if (kind == EmgHash.KindWreck)
                    {
                        d.EmgHash.AddWreckCell(cx, cy, e);
                    }
                    else if (kind == EmgHash.KindPatch)
                    {
                        d.EmgHash.AddPatchCell(cx, cy, index);
                    }
                }
            }
        }

        /// <summary>线段 a→b 与某种圆（障碍 / 有耐久的残骸）的最早交点：返回圆在原表里的下标（没有 −1），<paramref name="tHit"/> ∈ [0, 1]。
        /// <paramref name="skipEnds"/>：线段起点或终点已在圆内的不算（站在残骸上的单位不算躲在它后面）。O(线段外接框覆盖的格 × 格内圆)。</summary>
        private static int EmgHashSegment(ref CombatData d, double2 a, double2 b, byte kind, bool skipEnds, out double tHit, bool skipStart = false,
            bool skipAround = false, double2 around = default)
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
            // FG-GAP-186：线段外接框（外扩 1 毫米，远大于 SegmentCircleT 的舍入误差）——圆的外接框与它不相交时必然不相交，先排除再读残骸表
            double2 lo = math.min(a, b) - 1e-3, hi = math.max(a, b) + 1e-3;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (kind == EmgHash.KindWreck)
                    {
                        // FG-GAP-186：残骸走专用格链（结果只取最早 t、同 t 取小下标，与遍历顺序无关，同通用哈希逐字一致）
                        for (int node = d.EmgHash.WreckHead[EmgHash.WreckSlot(cx, cy)] - 1; node >= 0;)
                        {
                            int4 cell = d.EmgHash.WreckNodes[node];
                            node = cell.w;
                            if (cell.x == cx && cell.y == cy)
                            {
                                EmgHashSegmentTest(ref d, cell.z, a, b, lo, hi, kind, skipEnds, skipStart, skipAround, around, ref tHit, ref best);
                            }
                        }
                        continue;
                    }
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
                    {
                        continue;
                    }
                    do
                    {
                        if ((byte)d.EmgHash.Entries[e].w == kind)
                        {
                            EmgHashSegmentTest(ref d, e, a, b, lo, hi, kind, skipEnds, skipStart, skipAround, around, ref tHit, ref best);
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        private static void EmgHashSegmentTest(ref CombatData d, int e, double2 a, double2 b, double2 lo, double2 hi, byte kind, bool skipEnds, bool skipStart,
            bool skipAround, double2 around, ref double tHit, ref int best)
        {
            float4 c = d.EmgHash.Entries[e];
            if ((double)c.x + c.z < lo.x || (double)c.x - c.z > hi.x || (double)c.y + c.z < lo.y || (double)c.y - c.z > hi.y)
            {
                return; // 外接框不相交：SegmentCircleT 必为 −1、起点也不在圆内（下面各条跳过只会让命中更少），结果不变
            }
            int idx = d.EmgHash.Refs[e];
            if (kind == EmgHash.KindWreck && (idx >= d.Wrecks.Length || !(d.Wrecks.ElementAt(idx).Hp > 0f)))
            {
                return;
            }
            double2 center = new double2(c.x, c.y);
            double r2 = (double)c.z * c.z;
            if ((skipEnds || skipStart) && (math.lengthsq(a - center) <= r2 || (skipEnds && math.lengthsq(b - center) <= r2)))
            {
                return; // EMG1-ENT-01：skipStart = 只跳过起点已在圆内的（弹体从残骸里飞出去不算撞上）
            }
            if (skipAround && math.distance(around, center) <= c.z + 0.1)
            {
                return; // EMG1-E2E-01：开火目标所在的障碍圆不挡（同即时命中的视线 LineOfSight：终点自己所在的障碍不算）
            }
            double t = SegmentCircleT(a, b, center, c.z);
            if (t >= 0 && (t < tHit || (t == tHit && idx < best)))
            {
                tHit = t;
                best = idx;
            }
        }

        /// <summary>
        /// 即时命中的掩体（ADR ③；FGR-EMG-113“残骸背后的单位获得掩护”）：结算前对出手者 → 目标的线段做一次残骸检测（固态斑块随 EMG2 并入同一哈希）；
        /// 地点没有残骸时 O(1) 跳过、与旧行为逐字一致。挡住时这一击被残骸吸收（扣残骸耐久、发 evt.absorb），目标不受伤。
        /// </summary>
        internal static bool EmgInstantCovered(ref CombatData d, int a, int t, in CombatWeapon wp, float kineticCoef)
        {
            // 只对射弹类即时命中（含敌人的 Instant 攻击）；格斗 / 力场 / 布区 / 放机不是一条射击线段。
            if ((d.Wrecks.Length == 0 && d.EmgS[0].PatchLive <= 0) || a < 0 || t < 0 || wp.Reading.Carrier != CombatCarrier.Projectile)
            {
                return false;
            }
            float damage = wp.Damage;
            double tHit = 2.0, solidT = 2.0;
            int w = d.Wrecks.Length > 0 ? EmgHashSegment(ref d, d.Pos[a], d.Pos[t], EmgHash.KindWreck, true, out tHit) : -1;
            // EMG2-PERSIST-01（fieldrule_solid_absorb）：固态斑块与残骸同一口径，取线段上先碰到的那个
            int solid = d.EmgS[0].PatchLive > 0 ? EmgSolidOnShot(ref d, d.Pos[a], d.Pos[t], out solidT) : -1;
            if (solid >= 0 && (w < 0 || solidT < tHit))
            {
                EmgSolidAbsorb(ref d, solid, damage);
                EmgShotEvent(ref d, EmgEventKind.Absorb, d.Patches[solid].EmgId, damage, 0f, 0, d.Pos[a] + (d.Pos[t] - d.Pos[a]) * solidT, EmgEntKind.Patch);
                return true;
            }
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
            // EMG1-ENT-01（FGR-EMG-114）：单次动能（伤害 × 载体系数）够时残骸崩出破片，归出手者、链深 + 1。
            EmgWreckStruck(ref d, w, math.max(0f, damage) * kineticCoef, EmgDir(d.Pos[t] - d.Pos[a]), d.Id[a], FactionOfSlot(ref d, a),
                d.EmgCtx.Prog >= 0 ? d.EmgCtx.Depth : (byte)0, d.EmgCtx.Prog >= 0 ? d.EmgCtx.Chain : 0);
            return true;
        }

        /// <summary>SpawnFlight 弹体 / 子弹药这一步的线段先于单位交点撞上障碍圆：在撞点结束（evt.expire Code = 撞障碍；子弹药在撞点执行自己节点的指令）。</summary>
        private static bool EmgObstacleBefore(ref CombatData d, in CombatProjectile pr, double2 next, double unitT, out double2 at, out int o)
        {
            at = next;
            o = -1;
            if ((pr.Flags & (EmgProjFlags.Sub | EmgProjFlags.Flight)) == 0 || d.Obstacles.Length == 0) // FG-GAP-186：先判本地标志（旧弹体都在这里退出），少一次容器访问
            {
                return false;
            }
            // EMG1-E2E-01（阶段 1 出口：emg.stage = 1 后默认装配 [寻的, 拖尾] 的机器都发飞行弹体）：与即时命中的视线（LineOfSight）同一口径——
            // 起点已在圆内的障碍（站在锚点净空圈 / 屏障边开火）与开火目标所在的障碍（训练靶在自己锚点的净空圈中心）不挡；原先这两种弹体一出膛 / 到靶前就撞圈作废。
            int ts = pr.HomingTarget != 0 ? d.SlotOf(pr.HomingTarget) : -1;
            bool aroundTarget = ts >= 0 && d.IsAlive(ts);
            o = EmgHashSegment(ref d, pr.Prev, next, EmgHash.KindObstacle, false, out double t, (pr.Flags & EmgProjFlags.Sub) == 0, aroundTarget, aroundTarget ? d.Pos[ts] : default);
            if (o < 0 || t > unitT)
            {
                return false;
            }
            at = pr.Prev + (next - pr.Prev) * t;
            EmgStat(ref d, 6);
            return true;
        }

        // ─────────────────────────────── EMG3-FUSE-01 落点接线（ADR-EMG-001 ②；FG19 FGR-EMG-044；FG19A 第 4.1、4.2 节）───────────────────────────────

        /// <summary>程序有落点接线（HasAnchor）——没有的程序 O(1) 返回，金标准与空场景不变。</summary>
        internal static bool EmgProgAnchored(ref CombatData d, int prog) =>
            prog >= 0 && prog < d.EmgPrograms.Length && (d.EmgPrograms[prog].Flags & EmgProgramFlags.HasAnchor) != 0;

        /// <summary>
        /// A 的写端在本次结算产生了一个 <paramref name="kind"/> 种类的落点（跳点 / 反弹点 / 穿透点 / 扩散斑块中心 / 场作用点 / 注入点）：
        /// 在 <paramref name="pos"/>、以 <paramref name="target"/> 为对象内联执行宿主节点挂着的同种类 Anchored 行（B 的效果写端复制，编译期放在程序尾段）一次。
        /// 不经事件总线入队、继承宿主的链、不加链深（FGR-EMG-061 / 064）；每次结算（链 + 节点 + 步）的落点数到 CombatConfig.EmgAnchorMax 就停（计数在不进快照的标量里）；
        /// Anchored 行本身不再挂接线（AnchorBusy，不递归）。B 的条件读端照常（底盘条件 EmgChsBlocked）。O(尾段行数 × 节点行数)，只在带接线的程序上。
        /// <paramref name="src"/> = 产生这个落点的那一行的程序序号：只执行 B = src + 2 的复制行（[A, 接线, B] 的序号相邻），B 自己或第三条同族写端产生的点不算 A 的落点；
        /// −1 = 落点由节点修饰（连锁 / 反弹 / 穿透）产生，不按序号筛。<paramref name="srcMask"/> ≠ 0（读法合并的效果，EmgRootWriterMask）：B 的序号 − 2 落在这些位上的复制行才执行。
        /// </summary>
        internal static void EmgAnchorAt(ref CombatData d, byte kind, in EmgCtx host, double2 pos, int target, int src = -1, ulong srcMask = 0UL)
        {
            if (!EmgProgAnchored(ref d, host.Prog))
            {
                return;
            }
            EmgProgram p = d.EmgPrograms[host.Prog];
            if (host.Node < 0 || host.Node >= p.NodeCount || p.FirstNode + p.NodeCount > d.EmgNodes.Length || d.EmgS[0].AnchorBusy != 0)
            {
                return;
            }
            EmgNode root = d.EmgNodes[p.FirstNode], last = d.EmgNodes[p.FirstNode + p.NodeCount - 1], n = d.EmgNodes[p.FirstNode + host.Node];
            int tail = last.FirstInstr + last.InstrCount, end = math.min(root.FirstInstr + p.InstrTotal, d.EmgInstrs.Length), nEnd = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            EmgScalars es = d.EmgS[0];
            long step = d.Scalars[0].Steps;
            if (es.AnchorChain != host.Chain || es.AnchorNode != host.Node || es.AnchorStep != step)
            {
                es.AnchorChain = host.Chain;
                es.AnchorNode = host.Node;
                es.AnchorStep = step;
                es.AnchorUsed = 0;
            }
            int max = d.Config.EmgAnchorMax > 0 ? d.Config.EmgAnchorMax : 4;
            bool counted = false;
            for (int i = tail; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.AnchorKind != kind || (x.Flags & EmgInstrFlags.Anchored) == 0 || (src >= 0 && x.Ordinal != src + 2)
                    || (srcMask != 0UL && (x.Ordinal < 2 || ((srcMask & EmgOrdBit(x.Ordinal - 2)) == 0UL
                        // 修复轮 2：同一条固件装两次时同端口写端被编译器并成一行（序号取小的那条），线接在第二条后面——A 在本节点只剩读端行、没有自己的写端行，按落点种类认它（种类本身来自 A 的写端口）
                        && EmgOrdinalWrites(ref d, n.FirstInstr, nEnd, x.Ordinal - 2))))
                    || !EmgOrdinalIn(ref d, n.FirstInstr, nEnd, x.Ordinal)
                    || EmgChsBlocked(ref d, host.Owner, n.FirstInstr, nEnd, x.Ordinal))
                {
                    continue;
                }
                if (!counted)
                {
                    if (es.AnchorUsed >= max)
                    {
                        es.AnchorCut++; // 超出 emg.fuse.anchor_max 的落点不执行
                        break;
                    }
                    counted = true;
                    es.AnchorUsed++;
                    es.AnchorFired++;
                    es.AnchorBusy = 1;
                    d.EmgS[0] = es;
                }
                EmgAnchorExec(ref d, x, host, pos, target, tail, end);
                es = d.EmgS[0];
            }
            es.AnchorBusy = 0;
            d.EmgS[0] = es;
        }

        /// <summary>节点 [first, end) 里有这个程序序号的行——B 与 A 在同一节点（继承副本不另接线）。</summary>
        private static bool EmgOrdinalIn(ref CombatData d, int first, int end, byte ordinal)
        {
            for (int j = first; j < end; j++)
            {
                if (d.EmgInstrs[j].Ordinal == ordinal)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>在落点执行一条 Anchored 效果写端：排放 → 斑块（斑块层没开时落驻留区）、注能 → 对象单位 + 落点斑块、场 / 蔓延 / 导线 / 追射按事件门控的同一实现（落点当事件位置）。</summary>
        private static void EmgAnchorExec(ref CombatData d, in EmgInstr x, in EmgCtx host, double2 pos, int target, int first, int last)
        {
            bool unit = target >= 0 && target < d.Count && d.IsAlive(target);
            // EMG4-FUSE-02（DEBT-EMG3FUSE01-01 ③）：动能注入（余震）、数据链标记、处决、破片、伴飞——与子弹药落点同一实现（EmgTargetWrite），对象 = 落点上的单位
            if (x.Port == EmgRuleConst.PortMarked || x.Port == EmgRuleConst.PortIntegrity || x.Port == EmgRuleConst.PortDrone || (x.Verb == (byte)EmgVerb.Inject && x.Port == EmgConst.PortKinetic))
            {
                if (unit)
                {
                    EmgTargetWrite(ref d, target, host, x, first, last);
                }
                return;
            }
            if (x.Port == EmgRuleConst.PortFragment)
            {
                EmgAnchorFragments(ref d, x, host, pos, first, last);
                return;
            }
            if (x.Port == EmgConst.EventPortBase + (int)EmgEventKind.Ping)
            {
                EmgPingOut(ref d, new EmgEvent { Kind = EmgEventKind.Hit, Depth = host.Depth, EntKind = host.EntKind, Ent = host.Ent, Prog = host.Prog, Node = host.Node, Chain = host.Chain, Pos = pos }, host, x);
                return;
            }
            var e = new EmgEvent
            {
                Kind = EmgEventKind.Hit, Depth = host.Depth, EntKind = host.EntKind, Ent = host.Ent, OtherKind = unit ? EmgEntKind.Unit : EmgEntKind.None, Other = unit ? d.Id[target] : 0,
                Prog = host.Prog, Node = host.Node, Chain = host.Chain, Pos = pos,
            };
            if (x.Verb == (byte)EmgVerb.Field)
            {
                EmgGatedField(ref d, x, e, host);
                return;
            }
            if (x.Verb == (byte)EmgVerb.Spread)
            {
                EmgSpread(ref d, e, host, x);
                return;
            }
            if (x.Verb == (byte)EmgVerb.Link)
            {
                EmgLink(ref d, e, host, x);
                return;
            }
            if (x.Verb == (byte)EmgVerb.Spawn && x.Port == EmgConst.PortProjectile)
            {
                EmgQueueFollow(ref d, e, host, x);
                return;
            }
            int ownerId = EmgIdOf(ref d, host.Owner);
            if (x.Verb == (byte)EmgVerb.Inject)
            {
                if (!(x.Amount > 0f))
                {
                    return;
                }
                if (unit)
                {
                    if (x.Port == EmgConst.PortHeat)
                    {
                        EmgAddUnitHeat(ref d, target, x.Amount);
                    }
                    else if (x.Port == EmgConst.PortElec)
                    {
                        if (!EmgElecShed(ref d, target, x.Amount))
                        {
                            EmgAddAcc(ref d, target, EmgPhaseConst.Charge, x.Amount, ownerId);
                        }
                    }
                    else if (x.Port == EmgConst.PortMag)
                    {
                        EmgAddAcc(ref d, target, EmgPhaseConst.Magnetize, x.Amount, ownerId);
                    }
                    else if (x.Port == EmgRuleConst.PortCorrode && EmgPatchesOn(ref d))
                    {
                        EmgClueSeen(ref d, host.Chain, x.Port);
                        EmgAddAcc(ref d, target, EmgPhaseConst.Corrode, x.Amount, ownerId); // 与命中注入腐蚀度同一实现（EmgChildHitWrite）
                    }
                }
                if (x.Port == EmgRuleConst.PortCorrode)
                {
                    return; // 腐蚀度只注单位，不加热 / 充电斑块
                }
                if (x.Port != EmgConst.PortMag && EmgPatchesOn(ref d))
                {
                    EmgInjectPatches(ref d, pos, math.max(0.5f, x.Param2), x.Port, x.Amount, host.Chain, host.Depth, ownerId);
                }
                EmgChainLog(ref d, EmgChainNode.Inject, host.Chain, host.Depth, x.Port, ownerId, pos, unit ? d.Id[target] : 0);
                return;
            }
            if (x.Verb != d.Config.EmgVerbEmit)
            {
                return;
            }
            float a = (x.Flags & EmgInstrFlags.MagA) != 0 ? x.A * x.Amount : x.A;
            float b = (x.Flags & EmgInstrFlags.MagB) != 0 ? x.B * x.Amount : x.B;
            if (!(a > 0f) || !(b > 0f) || EmgEmitPatch(ref d, x, pos, a, b, host.Owner, host.Chain, host.Depth))
            {
                return;
            }
            int bit = (int)math.round(x.C) - 1;
            uint mask = bit >= 0 && bit < 32 ? 1u << bit : 0u;
            int owner = host.Owner >= 0 && host.Owner < d.Count ? host.Owner : -1;
            if (SpawnZone(ref d, owner, (CombatFaction)host.Faction, pos, a, b, 0f, 0f, 1f, mask, 0f, 0f, 0f, 0f))
            {
                SetLastZoneLook(ref d, CombatZoneLook.Pool);
                NoteReading(ref d, CombatConst.ReadingFeedZone, pos, owner);
            }
        }

        /// <summary>
        /// EMG4-FUSE-02（FGR-EMG-044“两父端口在同一条因果链里同现”；FG-GAP-206 ①）：出手时为程序掩码里的每条配方登记这条链（侧位清零；满了挤掉链号最小 = 最早的一条）。
        /// 之后同一条链上的写端执行时调 <see cref="EmgClueSeen"/>，两侧端口都出现才计 1 次（EmgClueCount 按链去重）。O(配方位 × 环长)，只在带掩码的出手时。
        /// </summary>
        internal static void EmgClueArm(ref CombatData d, uint mask, int chain)
        {
            if (mask == 0u || chain <= 0 || !d.EmgClueHalf.IsCreated)
            {
                return;
            }
            for (int i = 0; i < EmgConst.ClueSlots; i++)
            {
                if ((mask & (1u << i)) == 0)
                {
                    continue;
                }
                int b = i * EmgConst.ClueHalfRing, oldest = b;
                bool have = false;
                for (int j = b; j < b + EmgConst.ClueHalfRing && !have; j++)
                {
                    have = d.EmgClueHalf[j].x == chain;
                    if (d.EmgClueHalf[j].x == 0 || (d.EmgClueHalf[oldest].x != 0 && d.EmgClueHalf[j].x < d.EmgClueHalf[oldest].x))
                    {
                        oldest = j; // 空位优先，其次链号最小（最早）的
                    }
                }
                if (!have)
                {
                    d.EmgClueHalf[oldest] = new int2(chain, 0);
                }
            }
        }

        /// <summary>
        /// EMG4-FUSE-02：链 <paramref name="chain"/> 上执行了一条写端口 <paramref name="port"/>（排放 / 注能 / 场 / 追射 / 拆残骸 / 处决 / 触发器 / 节拍）——按线索侧位表给已登记这条链的配方记侧位，
        /// 两侧都出现即计 1 次并清掉登记。没有登记的链（环境 / 规则产物 / 没带掩码的程序）不计。端口不是任何配方的线索端口时 O(1) 返回。
        /// </summary>
        internal static void EmgClueSeen(ref CombatData d, int chain, int port)
        {
            if (chain <= 0 || port <= 0 || port >= EmgConst.ClueSidePorts || !d.EmgClueSides.IsCreated)
            {
                return;
            }
            ulong sides = d.EmgClueSides[port];
            for (int i = 0; sides != 0ul && i < EmgConst.ClueSlots; i++, sides >>= 2)
            {
                int side = (int)(sides & 3ul);
                if (side == 0)
                {
                    continue;
                }
                int b = i * EmgConst.ClueHalfRing;
                for (int j = b; j < b + EmgConst.ClueHalfRing; j++)
                {
                    int2 h = d.EmgClueHalf[j];
                    if (h.x != chain)
                    {
                        continue;
                    }
                    h.y |= side;
                    if (h.y == 3)
                    {
                        d.EmgClueHalf[j] = default;
                        EmgClueCount(ref d, 1u << i, chain);
                    }
                    else
                    {
                        d.EmgClueHalf[j] = h;
                    }
                    break;
                }
            }
        }

        /// <summary>配方 <paramref name="i"/> 有这条链的在途登记就清掉并返回 true。</summary>
        private static bool EmgClueDisarm(ref CombatData d, int i, int chain)
        {
            if (!d.EmgClueHalf.IsCreated)
            {
                return false;
            }
            for (int j = i * EmgConst.ClueHalfRing; j < (i + 1) * EmgConst.ClueHalfRing; j++)
            {
                if (d.EmgClueHalf[j].x == chain)
                {
                    d.EmgClueHalf[j] = default;
                    return true;
                }
            }
            return false;
        }

        /// <summary>EMG3-FUSE-01（FGR-EMG-044 熔合线索）：一次出手的程序覆盖了哪些配方端口对（编译期掩码），各计 1 次并置脏位；热更层按脏位取走（O(配方数)，只在带掩码的程序出手时）。</summary>
        /// 出手（程序掩码，<paramref name="rule"/> = false）与规则成立（规则行的配方掩码，跨机器的同现）走同一个计数：每条配方记住最近由出手计过的 ClueRing 条链号（环）——
        /// 出手：链号在环里不再计，否则进环、计 1；规则成立：链号在环里（这条链的同现已由出手计过）不计，同一条链同一步只计一次（长期存在的斑块合并后沿用一个链号，
        /// 跨步的再次成立按新一次同现计，DEBT-EMG3FUSE01-02）。没有链号（环境反应）不计。状态随快照段 26 存读档。O(配方位 × 环长)，只在带掩码的出手 / 对应配方的规则成立时。
        internal static void EmgClueCount(ref CombatData d, uint mask, int chain, bool rule = false)
        {
            if (mask == 0u || chain <= 0 || !d.EmgClue.IsCreated || d.EmgClue.Length < EmgConst.ClueLen)
            {
                return;
            }
            uint added = 0u;
            for (int i = 0; i < EmgConst.ClueSlots; i++)
            {
                if ((mask & (1u << i)) == 0)
                {
                    continue;
                }
                int ring = EmgConst.ClueSlots * 2 + i * EmgConst.ClueRing;
                bool seen = false;
                for (int j = 0; j < EmgConst.ClueRing && !seen; j++)
                {
                    seen = d.EmgClue[ring + j] == chain;
                }
                if (seen)
                {
                    continue;
                }
                // EMG4-FUSE-02：这条链出手时登记过、两侧还没都出现，规则成立 = 同现成立——按出手那一路计（进链环），之后的出手侧位不再重复计
                bool asRule = rule && !EmgClueDisarm(ref d, i, chain);
                if (asRule)
                {
                    int step = unchecked((int)d.Scalars[0].Steps);
                    if (d.EmgClue[EmgConst.ClueRuleChain + i] == chain && d.EmgClue[EmgConst.ClueRuleStep + i] == step)
                    {
                        continue;
                    }
                    d.EmgClue[EmgConst.ClueRuleChain + i] = chain;
                    d.EmgClue[EmgConst.ClueRuleStep + i] = step;
                    d.EmgClue[i] = d.EmgClue[i] + 1;
                    added |= 1u << i;
                    continue;
                }
                int head = d.EmgClue[EmgConst.ClueSlots + i];
                d.EmgClue[ring + head] = chain;
                d.EmgClue[EmgConst.ClueSlots + i] = (head + 1) % EmgConst.ClueRing;
                d.EmgClue[i] = d.EmgClue[i] + 1;
                added |= 1u << i;
            }
            if (added != 0u)
            {
                EmgScalars es = d.EmgS[0];
                es.ClueDirty |= added;
                d.EmgS[0] = es;
            }
        }
    }
}
