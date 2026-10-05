using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using BinGames.Sim.Nav;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032；FGT-DEF-005）：攻城的常量。内核只认“职能下标 / 目标类别位 / 代价”，不认识“核心”“电塔”之类的玩法概念：
    /// 热更层按 fg.TbSiegeTarget / fg.TbSiegeCategory 把建筑翻译成类别位、把职能偏好翻译成代价后交给内核。
    /// </summary>
    public static class CombatSiegeConst
    {
        /// <summary>流场的职能下标：0 突击、1 破坏、2 攻城、3 撤退（撤退不是编成职能，只是一张回集结点的流场）。</summary>
        public const int RoleCount = 4;
        public const int RoleRetreat = 3;
        /// <summary>每种职能两张流场：开路（屏障 / 建筑挡路）与破墙（建筑格可穿过，按耐久加代价）。</summary>
        public const int FieldCount = RoleCount * 2;
        /// <summary>目标类别位（<see cref="CombatSiegeUnit.Cat"/>）：核心 / 发电 / 信号 / 防御 / 监听站 / 其它建筑。</summary>
        public const int CategoryCount = 6;
        public const byte CatCore = 1 << 0;
        public const byte CatPower = 1 << 1;
        public const byte CatSignal = 1 << 2;
        public const byte CatDefense = 1 << 3;
        public const byte CatListening = 1 << 4;
        public const byte CatOther = 1 << 5;
        public const int Inf = int.MaxValue / 4;
        /// <summary>带 <see cref="CombatUnitFlags.HealthFloor"/> 的单位耐久的下限（与 BuildingOps.CoreFloorHealth 同一口径）。</summary>
        public const float HealthFloor = 0.001f;
        /// <summary>剧场矩形格数上限（流场内存与全量计算耗时的硬上限；热更层按 siege.theater_max_cells 截取，内核再兜底）。</summary>
        public const int MaxRectCells = 512 * 512;
        /// <summary>还没结算的溅射命中格上限（满了丢弃并计数）。</summary>
        public const int ImpactCap = 4096;
    }

    /// <summary>突袭单位的职能（<see cref="CombatSiegeUnit.Role"/>）。0 = 不是攻城单位（Demo 敌人、原型突袭者照旧）。</summary>
    public enum CombatSiegeRole : byte
    {
        None = 0,
        Assault = 1,
        Sabotage = 2,
        Siege = 3,
    }

    /// <summary>突袭单位的攻城阶段（<see cref="CombatSiegeUnit.Mode"/>）。</summary>
    public enum CombatSiegeMode : byte
    {
        Attack = 0,
        /// <summary>FGR-DEF-032：撤退——沿撤退流场回集结点，到了就离开家园（<see cref="CombatEventKind.SiegeExited"/>）。</summary>
        Retreat = 1,
        /// <summary>DEBT-FG6DEF04-03：行进途中被拦截、就地交战（不走攻城流场；照原型突袭者的规则打感知范围内的己方单位，没有目标就原地待命）。</summary>
        Skirmish = 2,
    }

    /// <summary>
    /// FG6-DEF-05：一个单位的攻城属性（每单位一份，随快照进存档，格式 11）。突袭单位用 Role / Mode / Group / StructMult / HealCd / Breach；
    /// 结构单位（建筑、炮塔、屏障）用 Cat（目标类别位）与 FootMin / FootMax（占地格，含两端）。全 0 = 不参与攻城。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatSiegeUnit
    {
        public byte Role;
        public byte Mode;
        public byte Cat;
        /// <summary>1 = 当前目标要站定打（职能目标 / 破墙目标）；0 = 边走边打（自卫）。</summary>
        public byte Hold;
        /// <summary>所属行进队伍的键（热更层 TransitGroupRecord.NavKey）。</summary>
        public int Group;
        public int2 FootMin;
        public int2 FootMax;
        /// <summary>打建筑（带类别位的单位）时的伤害倍率（0 = 1）。</summary>
        public float StructMult;
        /// <summary>随队维修的计时（游戏秒；行为参数 EffectAmount &gt; 0 的单位用）。</summary>
        public float HealCd;
        /// <summary>正在破墙的结构单位 ID（0 = 没有）：叠加层画“正在拆哪段墙”、热更层发破墙通知。</summary>
        public int Breach;
        /// <summary>
        /// FG6-DEF-05（DEBT-FG4ECO07-02 驻防守点交战）：己方机器的驻防点与守点半径（0 = 不守点）。空闲（没有命令）时朝驻防点半径内最近的敌人交战：
        /// 射程内原地打，射程外在半径内靠近；半径内没有敌人就回驻防点。热更层按驻防岗与工单状态写入。
        /// </summary>
        public double2 GuardPost;
        public float GuardRadius;
    }

    /// <summary>FG6-DEF-05：攻城剧场配置（热更层在突袭展开 / 读档后写入；不进快照）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatSiegeConfig
    {
        public byte Enabled;
        public byte Pad0;
        public short Pad1;
        /// <summary>流场矩形（格，含两端）。</summary>
        public int2 Min;
        public int2 Max;
        /// <summary>破墙流场：穿过一格建筑的基础代价（10 = 1 米）与每档耐久加的代价；耐久每 BandHp 一档。</summary>
        public int BreachBase;
        public int BreachPerBand;
        public float BandHp;
        /// <summary>重新选目标的间隔（游戏秒）与撤退的离场半径（米）。</summary>
        public float RetargetSeconds;
        public float ExitRadius;
    }

    /// <summary>FG6-DEF-05：一格还没结算的溅射命中（敌人打中建筑的那一侧的格子，累计伤害）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatSiegeImpact
    {
        public int2 Cell;
        public float Damage;
    }

    /// <summary>FG6-DEF-05：攻城剧场的计数（性能证据 / 自检）。</summary>
    [Serializable]
    public struct CombatSiegeStats
    {
        public long Maintains;
        public long Resets;
        public long FullComputes;
        public long Incrementals;
        public long ChangedCells;
        public long InvalidatedCells;
        public int LastChanged;
        public int LastInvalidated;
        public int ValidMask;
        public int NeedMask;
        public long ImpactsDropped;
        public long Exits;
        /// <summary>驻防机器守点开火的次数（DEBT-FG4ECO07-02）；守点逻辑跑过的单位步数、其中有目标的步数、最近一次开火结果（诊断）。</summary>
        public long GuardShots;
        public long GuardTicks;
        public long GuardEngaged;
        public int GuardLastResult;
    }

    /// <summary>一个结构单位上一次写进格子缓存的样子（变化检测用）。</summary>
    public struct CombatSiegeStructCache
    {
        public int4 Foot;
        public int Pen;
        public byte Cat;
        public int Stamp;
    }

    /// <summary>一格的攻城相关状态（变化检测与增量更新时的“旧图”）。</summary>
    public struct CombatSiegeCell
    {
        /// <summary>通行：高 4 位 = 代价倍率，最低位 = 敌方类别被挡。</summary>
        public byte Nav;
        public byte Cat;
        public byte Adj;
        public byte Exit;
        public int Occ;
        public int Pen;

        public bool SameAs(in CombatSiegeCell o) => Nav == o.Nav && Cat == o.Cat && Adj == o.Adj && Exit == o.Exit && Occ == o.Occ && Pen == o.Pen;
    }

    /// <summary>
    /// FG6-DEF-05：攻城剧场的全部原生容器（挂在 <see cref="CombatData.SiegeState"/> 上，Burst 作业直接读写）。
    /// 格子缓存与 8 张流场按剧场矩形分配；流场是格网 + 结构单位 + 集结点的纯函数（不进快照，读档后重算）。
    /// </summary>
    public struct CombatSiegeData : IDisposable
    {
        public NativeArray<CombatSiegeConfig> Cfg;
        /// <summary>职能 r 的目标类别位（4 项）与偏好代价（4 × 6；<see cref="CombatSiegeConst.Inf"/> = 不当目标）。</summary>
        public NativeArray<int> RoleMask;
        public NativeArray<int> Bias;
        /// <summary>撤退的集结点（格）。</summary>
        public NativeList<int2> Exits;
        /// <summary>当前缓存对应的矩形：x, y = 最小格，z = 宽，w = 高（z = 0 = 没有）。</summary>
        public NativeArray<int4> Rect;
        public NativeList<CombatSiegeCell> Cells;
        public NativeList<int> Dist;
        public NativeArray<byte> FieldValid;
        public NativeHashMap<int, CombatSiegeStructCache> Structs;
        public NativeList<int2> ExitsApplied;
        public NativeHashMap<int, CombatSiegeCell> OldCells;
        public NativeList<byte> Mark;
        public NativeList<int> Changed;
        public NativeList<int> AdjDirty;
        public NativeList<CombatSiegeImpact> Impacts;
        public NativeHashMap<long, int> ImpactIndex;
        public NativeList<int> Breaches;
        public NativeArray<CombatSiegeStats> Stats;
        public NativeArray<int> Stamp;

        public static CombatSiegeData Create()
        {
            var s = new CombatSiegeData
            {
                Cfg = new NativeArray<CombatSiegeConfig>(1, Allocator.Persistent),
                RoleMask = new NativeArray<int>(CombatSiegeConst.RoleCount, Allocator.Persistent),
                Bias = new NativeArray<int>(CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount, Allocator.Persistent),
                Exits = new NativeList<int2>(4, Allocator.Persistent),
                Rect = new NativeArray<int4>(1, Allocator.Persistent),
                Cells = new NativeList<CombatSiegeCell>(16, Allocator.Persistent),
                Dist = new NativeList<int>(16, Allocator.Persistent),
                FieldValid = new NativeArray<byte>(CombatSiegeConst.FieldCount, Allocator.Persistent),
                Structs = new NativeHashMap<int, CombatSiegeStructCache>(64, Allocator.Persistent),
                ExitsApplied = new NativeList<int2>(4, Allocator.Persistent),
                OldCells = new NativeHashMap<int, CombatSiegeCell>(64, Allocator.Persistent),
                Mark = new NativeList<byte>(16, Allocator.Persistent),
                Changed = new NativeList<int>(64, Allocator.Persistent),
                AdjDirty = new NativeList<int>(64, Allocator.Persistent),
                Impacts = new NativeList<CombatSiegeImpact>(16, Allocator.Persistent),
                ImpactIndex = new NativeHashMap<long, int>(16, Allocator.Persistent),
                Breaches = new NativeList<int>(8, Allocator.Persistent),
                Stats = new NativeArray<CombatSiegeStats>(1, Allocator.Persistent),
                Stamp = new NativeArray<int>(1, Allocator.Persistent),
            };
            for (int i = 0; i < s.Bias.Length; i++)
            {
                s.Bias[i] = CombatSiegeConst.Inf;
            }
            return s;
        }

        public bool IsCreated => Cfg.IsCreated;

        public void Dispose()
        {
            if (!Cfg.IsCreated)
            {
                return;
            }
            Cfg.Dispose();
            RoleMask.Dispose();
            Bias.Dispose();
            Exits.Dispose();
            Rect.Dispose();
            Cells.Dispose();
            Dist.Dispose();
            FieldValid.Dispose();
            Structs.Dispose();
            ExitsApplied.Dispose();
            OldCells.Dispose();
            Mark.Dispose();
            Changed.Dispose();
            AdjDirty.Dispose();
            Impacts.Dispose();
            ImpactIndex.Dispose();
            Breaches.Dispose();
            Stats.Dispose();
            Stamp.Dispose();
        }

        public void ClearImpacts()
        {
            Impacts.Clear();
            ImpactIndex.Clear();
        }

        /// <summary>把剧场配置、职能表、集结点从另一份复制过来（读档换内核数据时沿用热更层写的配置；缓存与流场重建）。</summary>
        public void CopyConfigFrom(ref CombatSiegeData o)
        {
            Cfg[0] = o.Cfg[0];
            RoleMask.CopyFrom(o.RoleMask);
            Bias.CopyFrom(o.Bias);
            Exits.Clear();
            for (int i = 0; i < o.Exits.Length; i++)
            {
                Exits.Add(o.Exits[i]);
            }
        }
    }

    /// <summary>剧场维护（Burst，主线程 Run）：每个固定步在内核一步之前跑一次——格子缓存与流场按变化增量更新，需要的流场补算。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatSiegeMaintainJob : IJob
    {
        public CombatData D;

        public void Execute()
        {
            CombatSiegeLogic.Maintain(ref D);
        }
    }

    /// <summary>自检：每张有效流场按当前输入全量重算一遍，与增量维护的结果逐格比对（Burst，Run）。Out[0] = 不一致的格数，Out[1] = 比对的流场数。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatSiegeVerifyJob : IJob
    {
        public CombatData D;
        public NativeArray<int> Out;

        public void Execute()
        {
            CombatSiegeLogic.VerifyAll(ref D, Out);
        }
    }

    /// <summary>FG6-DEF-05：头顶职能图标（Burst）。追加在状态标签图标之后；B = (边长, 形状序号, 打包颜色, 30)；精英的图标大一圈。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatSiegeIconJob : IJob
    {
        public CombatData D;
        public NativeList<CombatInstance> Icons;
        [ReadOnly] public NativeArray<float2> Visuals;
        public double2 Origin;
        public float Size;

        public void Execute()
        {
            for (int i = 0; i < D.Count; i++)
            {
                if (!D.IsAlive(i))
                {
                    continue;
                }
                CombatSiegeUnit su = D.Siege[i];
                if (su.Role == 0)
                {
                    continue;
                }
                int r = su.Mode == (byte)CombatSiegeMode.Retreat ? CombatSiegeConst.RoleRetreat : math.clamp(su.Role - 1, 0, 2);
                if (r >= Visuals.Length)
                {
                    continue;
                }
                float2 v = Visuals[r];
                if (v.x < 0f)
                {
                    continue;
                }
                float size = D.Has(i, CombatUnitFlags.Elite) ? Size * 1.3f : Size;
                double2 p = D.Pos[i] - Origin;
                double2 q = D.Prev[i] - Origin;
                // 状态标签图标那一排在“半径 + 0.9 边长”处；职能图标再往上一格，互不遮挡。
                float lift = D.Radius[i] + Size * 2.05f;
                Icons.Add(new CombatInstance
                {
                    A = new float4((float)p.x, (float)p.y + lift, (float)q.x, (float)q.y + lift),
                    B = new float4(size, v.x, v.y, 30f),
                });
            }
        }
    }

    /// <summary>
    /// FG6-DEF-05：攻城的全部规则（Burst 作业与托管查询共用）。
    ///
    /// <b>流场</b>（FGR-DEF-031）：剧场矩形里每种职能两张“到目标的代价”场——
    /// 开路场：敌方类别被挡的格（屏障、闸门、建筑、悬崖、水）不能走；起点 = 目标建筑外一圈可走格，起点值 = 职能对这类目标的偏好代价。
    /// 破墙场：带攻城类别的结构单位所在格可以穿过，进入一格加 BreachBase + 耐久档 × BreachPerBand；起点 = 目标建筑的占地格。
    /// 8 向、斜走不切角，代价与层级寻路同一套（两格代价倍率之和 × 5 / × 7）。距离唯一（与处理顺序无关）→ 确定性；增量更新与全量重算逐格相同（自检对照）。
    /// <b>增量更新</b>：每步比对格网镜像、结构单位（存在 / 占地 / 类别 / 耐久档）与集结点 → 变化的格；代价变大 / 变不可走的格及依赖它的格（旧图上的最短路树后代）作废，
    /// 再从作废区域边界与变化格周围重新松弛（Dijkstra）。
    /// </summary>
    public static class CombatSiegeLogic
    {
        // ─────────────────────────────── 几何 ───────────────────────────────

        public static bool Active(ref CombatData d) => d.SiegeState.IsCreated && d.SiegeState.Rect[0].z > 0;

        /// <summary>这个单位按攻城规则行动（有职能、不是拦截交战、剧场开着）。</summary>
        public static bool UsesSiege(ref CombatData d, int i) => d.Siege[i].Role != 0 && d.Siege[i].Mode != (byte)CombatSiegeMode.Skirmish && Active(ref d);

        public static int IndexOf(ref CombatData d, int2 c)
        {
            int4 r = d.SiegeState.Rect[0];
            int lx = c.x - r.x;
            int ly = c.y - r.y;
            if (r.z <= 0 || lx < 0 || ly < 0 || lx >= r.z || ly >= r.w)
            {
                return -1;
            }
            return ly * r.z + lx;
        }

        public static int2 CellAtIndex(ref CombatData d, int idx)
        {
            int4 r = d.SiegeState.Rect[0];
            return new int2(r.x + idx % r.z, r.y + idx / r.z);
        }

        // ─────────────────────────────── 每格的性质（按字段 f） ───────────────────────────────

        private static bool Pass(int f, in CombatSiegeCell c) => (c.Nav & 1) == 0 || ((f & 1) == 1 && c.Occ != 0);

        private static int Mult(in CombatSiegeCell c) => math.max(1, c.Nav >> 4);

        private static int Seed(ref CombatSiegeData s, int f, in CombatSiegeCell c)
        {
            int r = f >> 1;
            if (r == CombatSiegeConst.RoleRetreat)
            {
                return c.Exit != 0 && Pass(f, c) ? 0 : CombatSiegeConst.Inf;
            }
            int mask = s.RoleMask[r];
            int bits;
            if ((f & 1) == 0)
            {
                if ((c.Nav & 1) != 0)
                {
                    return CombatSiegeConst.Inf;
                }
                bits = c.Adj & mask;
            }
            else
            {
                bits = c.Cat & mask;
            }
            int best = CombatSiegeConst.Inf;
            while (bits != 0)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1;
                best = math.min(best, s.Bias[r * CombatSiegeConst.CategoryCount + b]);
            }
            return best;
        }

        private static int Enter(ref CombatSiegeData s, int f, in CombatSiegeCell c) =>
            (f & 1) == 1 && c.Occ != 0 && Seed(ref s, f, c) >= CombatSiegeConst.Inf ? c.Pen : 0;

        private static int StepCost(in CombatSiegeCell a, in CombatSiegeCell b, bool diag) => (Mult(a) + Mult(b)) * (diag ? 7 : 5);

        /// <summary>u → v（v = u + 方向 k）这条边在当前图上是否可走，代价多少（不可走 = -1）。</summary>
        private static int EdgeCost(ref CombatSiegeData s, int f, int w, int h, int ux, int uy, int k)
        {
            int2 dd = NavSearch.Dir8(k);
            int vx = ux + dd.x;
            int vy = uy + dd.y;
            if (vx < 0 || vy < 0 || vx >= w || vy >= h)
            {
                return -1;
            }
            CombatSiegeCell cu = s.Cells[uy * w + ux];
            CombatSiegeCell cv = s.Cells[vy * w + vx];
            if (!Pass(f, cu) || !Pass(f, cv))
            {
                return -1;
            }
            bool diag = k >= 4;
            if (diag && (!Pass(f, s.Cells[uy * w + vx]) || !Pass(f, s.Cells[vy * w + ux])))
            {
                return -1;
            }
            return StepCost(cu, cv, diag) + Enter(ref s, f, cv);
        }

        // ─────────────────────────────── 维护 ───────────────────────────────

        /// <summary>每步一次（内核一步之前）：没开剧场 → 释放缓存；矩形变了 → 全部重建；否则按变化增量更新；最后补算本步需要的流场。</summary>
        public static void Maintain(ref CombatData d)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            CombatSiegeConfig cfg = s.Cfg[0];
            CombatSiegeStats st = s.Stats[0];
            st.Maintains++;
            int w = cfg.Max.x - cfg.Min.x + 1;
            int h = cfg.Max.y - cfg.Min.y + 1;
            if (cfg.Enabled == 0 || w <= 0 || h <= 0 || (long)w * h > CombatSiegeConst.MaxRectCells)
            {
                if (s.Rect[0].z > 0)
                {
                    Release(ref s);
                }
                st.ValidMask = 0;
                st.NeedMask = 0;
                s.Stats[0] = st;
                s.Breaches.Clear();
                return;
            }
            int4 rect = s.Rect[0];
            if (rect.x != cfg.Min.x || rect.y != cfg.Min.y || rect.z != w || rect.w != h)
            {
                Rebuild(ref d, cfg, w, h);
                st = s.Stats[0];
                st.Maintains++;
                st.Resets++;
            }
            else
            {
                s.Stats[0] = st;
                Collect(ref d, w, h);
                st = s.Stats[0];
                int changed = s.Changed.Length;
                st.LastChanged = changed;
                st.ChangedCells += changed;
                st.LastInvalidated = 0;
                if (changed > 0)
                {
                    s.Stats[0] = st;
                    for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
                    {
                        if (s.FieldValid[f] != 0)
                        {
                            Incremental(ref d, f, w, h);
                        }
                    }
                    st = s.Stats[0];
                }
                ClearMarks(ref s);
            }
            s.Stats[0] = st;
            EnsureNeeded(ref d, w, h);
            RebuildBreaches(ref d);
        }

        private static void Release(ref CombatSiegeData s)
        {
            s.Rect[0] = int4.zero;
            s.Cells.Clear();
            s.Dist.Clear();
            s.Mark.Clear();
            s.Structs.Clear();
            s.ExitsApplied.Clear();
            s.OldCells.Clear();
            s.Changed.Clear();
            s.AdjDirty.Clear();
            for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
            {
                s.FieldValid[f] = 0;
            }
        }

        /// <summary>矩形变了（第一次开剧场 / 扩大）：格子缓存按当前格网、结构单位、集结点全部重建，流场全部作废（本步按需要全量重算）。</summary>
        private static void Rebuild(ref CombatData d, in CombatSiegeConfig cfg, int w, int h)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int n = w * h;
            s.Rect[0] = new int4(cfg.Min.x, cfg.Min.y, w, h);
            s.Cells.ResizeUninitialized(n);
            s.Mark.ResizeUninitialized(n);
            s.Dist.ResizeUninitialized(n * CombatSiegeConst.FieldCount);
            for (int i = 0; i < n; i++)
            {
                s.Cells[i] = default;
                s.Mark[i] = 0;
            }
            for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
            {
                s.FieldValid[f] = 0;
            }
            s.Structs.Clear();
            s.ExitsApplied.Clear();
            s.OldCells.Clear();
            s.Changed.Clear();
            s.AdjDirty.Clear();
            // 通行
            ScanNav(ref d, w, h, false);
            // 结构单位
            int stamp = ++s.Stamp[0];
            for (int i = 0; i < d.Count; i++)
            {
                if (!IsSiegeStructure(ref d, i))
                {
                    continue;
                }
                CombatSiegeStructCache c = CacheOf(ref d, i, stamp);
                WriteFoot(ref d, w, h, d.Id[i], c, false);
                s.Structs[d.Id[i]] = c;
            }
            // 集结点
            for (int e = 0; e < s.Exits.Length; e++)
            {
                int idx = IndexOf(ref d, s.Exits[e]);
                if (idx >= 0)
                {
                    CombatSiegeCell c = s.Cells[idx];
                    c.Exit = 1;
                    s.Cells[idx] = c;
                }
                s.ExitsApplied.Add(s.Exits[e]);
            }
            // 相邻类别
            for (int i = 0; i < n; i++)
            {
                CombatSiegeCell c = s.Cells[i];
                c.Adj = AdjOf(ref s, w, h, i % w, i / w);
                s.Cells[i] = c;
            }
            s.AdjDirty.Clear();
        }

        private static bool IsSiegeStructure(ref CombatData d, int i) =>
            d.IsAlive(i) && d.Has(i, CombatUnitFlags.Targetable) && d.Faction[i] == (byte)CombatFaction.Player && d.Siege[i].Cat != 0;

        private static CombatSiegeStructCache CacheOf(ref CombatData d, int i, int stamp)
        {
            CombatSiegeConfig cfg = d.SiegeState.Cfg[0];
            CombatSiegeUnit su = d.Siege[i];
            int band = cfg.BandHp > 0f ? (int)math.ceil(math.max(0f, d.Hp[i]) / cfg.BandHp) : 0;
            return new CombatSiegeStructCache
            {
                Foot = new int4(math.min(su.FootMin, su.FootMax), math.max(su.FootMin, su.FootMax)),
                Pen = math.max(0, cfg.BreachBase) + band * math.max(0, cfg.BreachPerBand),
                Cat = su.Cat,
                Stamp = stamp,
            };
        }

        private static byte AdjOf(ref CombatSiegeData s, int w, int h, int x, int y)
        {
            int adj = 0;
            for (int k = 0; k < 8; k++)
            {
                int2 dd = NavSearch.Dir8(k);
                int nx = x + dd.x;
                int ny = y + dd.y;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                {
                    continue;
                }
                adj |= s.Cells[ny * w + nx].Cat;
            }
            return (byte)adj;
        }

        private static void Record(ref CombatSiegeData s, int idx)
        {
            if (s.Mark[idx] != 0)
            {
                return;
            }
            s.Mark[idx] = 1;
            s.OldCells.TryAdd(idx, s.Cells[idx]);
        }

        private static void ClearMarks(ref CombatSiegeData s)
        {
            if (s.OldCells.Count == 0)
            {
                s.Changed.Clear();
                return;
            }
            NativeArray<int> keys = s.OldCells.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                s.Mark[keys[i]] = 0;
            }
            keys.Dispose();
            s.OldCells.Clear();
            s.Changed.Clear();
        }

        /// <summary>格网镜像 → 格子缓存的通行字节（按区块直接取，不逐格查哈希）。<paramref name="record"/> = 变了的先记旧值。</summary>
        private static void ScanNav(ref CombatData d, int w, int h, bool record)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            int size = d.Nav.IsCreated ? d.Nav.ChunkSize : 0;
            if (size <= 0)
            {
                for (int i = 0; i < w * h; i++)
                {
                    byte b = (byte)((1 << 4) | 1);
                    if (s.Cells[i].Nav != b)
                    {
                        if (record)
                        {
                            Record(ref s, i);
                        }
                        CombatSiegeCell c = s.Cells[i];
                        c.Nav = b;
                        s.Cells[i] = c;
                    }
                }
                return;
            }
            int cx0 = NavGridOps.FloorDiv(r.x, size);
            int cy0 = NavGridOps.FloorDiv(r.y, size);
            int cx1 = NavGridOps.FloorDiv(r.x + w - 1, size);
            int cy1 = NavGridOps.FloorDiv(r.y + h - 1, size);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int slot = NavGridOps.EnsureSlot(ref d.Nav, cx, cy);
                    int bx = cx * size;
                    int by = cy * size;
                    int x0 = math.max(bx, r.x);
                    int y0 = math.max(by, r.y);
                    int x1 = math.min(bx + size - 1, r.x + w - 1);
                    int y1 = math.min(by + size - 1, r.y + h - 1);
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            byte raw;
                            if (slot < 0 || !NavGridOps.InWorld(ref d.Nav, x, y))
                            {
                                raw = NavConst.Solid;
                            }
                            else
                            {
                                raw = d.Nav.Cell[slot * d.Nav.Cells + (y - by) * size + (x - bx)];
                            }
                            byte b = (byte)((raw & 0xF0) | ((raw >> NavConst.ClassHostile) & 1));
                            int idx = (y - r.y) * w + (x - r.x);
                            CombatSiegeCell c = s.Cells[idx];
                            if (c.Nav != b)
                            {
                                if (record)
                                {
                                    Record(ref s, idx);
                                }
                                c.Nav = b;
                                s.Cells[idx] = c;
                            }
                        }
                    }
                }
            }
        }

        private static void WriteFoot(ref CombatData d, int w, int h, int id, in CombatSiegeStructCache c, bool record)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            int x0 = math.max(c.Foot.x, r.x);
            int y0 = math.max(c.Foot.y, r.y);
            int x1 = math.min(c.Foot.z, r.x + w - 1);
            int y1 = math.min(c.Foot.w, r.y + h - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int idx = (y - r.y) * w + (x - r.x);
                    CombatSiegeCell cell = s.Cells[idx];
                    if (cell.Occ != 0 && cell.Occ != id)
                    {
                        continue; // 两个结构单位占同一格（不应发生）：先到的占着
                    }
                    if (cell.Occ == id && cell.Cat == c.Cat && cell.Pen == c.Pen)
                    {
                        continue;
                    }
                    if (record)
                    {
                        Record(ref s, idx);
                    }
                    bool catChanged = cell.Cat != c.Cat;
                    cell.Occ = id;
                    cell.Cat = c.Cat;
                    cell.Pen = c.Pen;
                    s.Cells[idx] = cell;
                    if (catChanged && record)
                    {
                        s.AdjDirty.Add(idx);
                    }
                }
            }
        }

        private static void ClearFoot(ref CombatData d, int w, int h, int id, in CombatSiegeStructCache c)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            int x0 = math.max(c.Foot.x, r.x);
            int y0 = math.max(c.Foot.y, r.y);
            int x1 = math.min(c.Foot.z, r.x + w - 1);
            int y1 = math.min(c.Foot.w, r.y + h - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int idx = (y - r.y) * w + (x - r.x);
                    CombatSiegeCell cell = s.Cells[idx];
                    if (cell.Occ != id)
                    {
                        continue;
                    }
                    Record(ref s, idx);
                    bool catChanged = cell.Cat != 0;
                    cell.Occ = 0;
                    cell.Cat = 0;
                    cell.Pen = 0;
                    s.Cells[idx] = cell;
                    if (catChanged)
                    {
                        s.AdjDirty.Add(idx);
                    }
                }
            }
        }

        /// <summary>本步的变化：通行、结构单位（新增 / 消失 / 换占地 / 换类别 / 耐久掉档）、集结点 → 格子缓存（先记旧值）→ 变化格清单。</summary>
        private static void Collect(ref CombatData d, int w, int h)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            s.Changed.Clear();
            s.AdjDirty.Clear();
            ScanNav(ref d, w, h, true);
            int stamp = ++s.Stamp[0];
            // 先给仍在的结构单位打上本步的戳、清掉消失的（阵亡 / 移除 / 不再带类别），再写新增 / 变化的——
            // 同一格旧单位消失、新单位出现（原地换墙 / 重建）时必须先清后写，否则新单位被“先到的占着”挡掉、随后又被清空，那一格就成了拆不掉的墙。
            for (int i = 0; i < d.Count; i++)
            {
                if (IsSiegeStructure(ref d, i) && s.Structs.TryGetValue(d.Id[i], out CombatSiegeStructCache keep))
                {
                    keep.Stamp = stamp;
                    s.Structs[d.Id[i]] = keep;
                }
            }
            if (s.Structs.Count > 0)
            {
                NativeArray<int> keys = s.Structs.GetKeyArray(Allocator.Temp);
                keys.Sort(); // 确定性：按 ID 顺序清（各自的占地互不重叠，顺序本不影响结果）
                for (int k = 0; k < keys.Length; k++)
                {
                    CombatSiegeStructCache c = s.Structs[keys[k]];
                    if (c.Stamp != stamp)
                    {
                        ClearFoot(ref d, w, h, keys[k], c);
                        s.Structs.Remove(keys[k]);
                    }
                }
                keys.Dispose();
            }
            for (int i = 0; i < d.Count; i++)
            {
                if (!IsSiegeStructure(ref d, i))
                {
                    continue;
                }
                int id = d.Id[i];
                CombatSiegeStructCache c = CacheOf(ref d, i, stamp);
                if (s.Structs.TryGetValue(id, out CombatSiegeStructCache old))
                {
                    if (!old.Foot.Equals(c.Foot) || old.Cat != c.Cat || old.Pen != c.Pen)
                    {
                        if (!old.Foot.Equals(c.Foot))
                        {
                            ClearFoot(ref d, w, h, id, old);
                        }
                        WriteFoot(ref d, w, h, id, c, true);
                    }
                    s.Structs[id] = c;
                }
                else
                {
                    WriteFoot(ref d, w, h, id, c, true);
                    s.Structs.TryAdd(id, c);
                }
            }
            // 集结点
            bool exitsSame = s.Exits.Length == s.ExitsApplied.Length;
            for (int e = 0; exitsSame && e < s.Exits.Length; e++)
            {
                exitsSame = s.Exits[e].Equals(s.ExitsApplied[e]);
            }
            if (!exitsSame)
            {
                for (int e = 0; e < s.ExitsApplied.Length; e++)
                {
                    SetExit(ref d, s.ExitsApplied[e], 0);
                }
                for (int e = 0; e < s.Exits.Length; e++)
                {
                    SetExit(ref d, s.Exits[e], 1);
                }
                s.ExitsApplied.Clear();
                for (int e = 0; e < s.Exits.Length; e++)
                {
                    s.ExitsApplied.Add(s.Exits[e]);
                }
            }
            // 相邻类别：类别变了的格，周围 8 格重算。
            for (int q = 0; q < s.AdjDirty.Length; q++)
            {
                int idx = s.AdjDirty[q];
                int x = idx % w;
                int y = idx / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 dd = NavSearch.Dir8(k);
                    int nx = x + dd.x;
                    int ny = y + dd.y;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    {
                        continue;
                    }
                    int n = ny * w + nx;
                    byte adj = AdjOf(ref s, w, h, nx, ny);
                    if (adj != s.Cells[n].Adj)
                    {
                        Record(ref s, n);
                        CombatSiegeCell c = s.Cells[n];
                        c.Adj = adj;
                        s.Cells[n] = c;
                    }
                }
            }
            s.AdjDirty.Clear();
            // 变化格 = 记过旧值、而且现在确实不一样的格。
            if (s.OldCells.Count > 0)
            {
                NativeArray<int> keys = s.OldCells.GetKeyArray(Allocator.Temp);
                for (int k = 0; k < keys.Length; k++)
                {
                    if (!s.OldCells[keys[k]].SameAs(s.Cells[keys[k]]))
                    {
                        s.Changed.Add(keys[k]);
                    }
                }
                keys.Dispose();
            }
        }

        private static void SetExit(ref CombatData d, int2 cell, byte on)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int idx = IndexOf(ref d, cell);
            if (idx < 0 || s.Cells[idx].Exit == on)
            {
                return;
            }
            Record(ref s, idx);
            CombatSiegeCell c = s.Cells[idx];
            c.Exit = on;
            s.Cells[idx] = c;
        }

        private static CombatSiegeCell OldOf(ref CombatSiegeData s, int idx) =>
            s.Mark[idx] != 0 && s.OldCells.TryGetValue(idx, out CombatSiegeCell o) ? o : s.Cells[idx];

        /// <summary>u → v 在“旧图”（本步变化之前）上的代价（不可走 = -1）。</summary>
        private static int OldEdgeCost(ref CombatSiegeData s, int f, int w, int h, int ux, int uy, int k)
        {
            int2 dd = NavSearch.Dir8(k);
            int vx = ux + dd.x;
            int vy = uy + dd.y;
            if (vx < 0 || vy < 0 || vx >= w || vy >= h)
            {
                return -1;
            }
            CombatSiegeCell cu = OldOf(ref s, uy * w + ux);
            CombatSiegeCell cv = OldOf(ref s, vy * w + vx);
            if (!Pass(f, cu) || !Pass(f, cv))
            {
                return -1;
            }
            bool diag = k >= 4;
            if (diag && (!Pass(f, OldOf(ref s, uy * w + vx)) || !Pass(f, OldOf(ref s, vy * w + ux))))
            {
                return -1;
            }
            return StepCost(cu, cv, diag) + Enter(ref s, f, cv);
        }

        // ─────────────────────────────── 流场：全量 / 增量 ───────────────────────────────

        private static void HeapPush(NativeList<long> heap, int dist, int idx)
        {
            long it = ((long)dist << 32) | (uint)idx;
            heap.Add(it);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                long pv = heap[p];
                if (pv <= it)
                {
                    break;
                }
                heap[i] = pv;
                i = p;
            }
            heap[i] = it;
        }

        private static long HeapPop(NativeList<long> heap)
        {
            long top = heap[0];
            long last = heap[heap.Length - 1];
            heap.RemoveAt(heap.Length - 1);
            int n = heap.Length;
            if (n == 0)
            {
                return top;
            }
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= n)
                {
                    break;
                }
                int r = l + 1;
                int m = r < n && heap[r] < heap[l] ? r : l;
                if (heap[m] >= last)
                {
                    break;
                }
                heap[i] = heap[m];
                i = m;
            }
            heap[i] = last;
            return top;
        }

        /// <summary>从堆里取格往外松弛（u → c 的边，代价 = 步长 + 进入 c 的代价）。</summary>
        private static void Relax(ref CombatSiegeData s, int f, int w, int h, NativeList<long> heap, NativeArray<int> dist, int baseOff)
        {
            while (heap.Length > 0)
            {
                long it = HeapPop(heap);
                int dd = (int)(it >> 32);
                int c = (int)(uint)it;
                if (dd != dist[baseOff + c])
                {
                    continue;
                }
                int cx = c % w;
                int cy = c / w;
                CombatSiegeCell cc = s.Cells[c];
                int enterC = Enter(ref s, f, cc);
                for (int k = 0; k < 8; k++)
                {
                    int2 d8 = NavSearch.Dir8(k);
                    int ux = cx - d8.x;
                    int uy = cy - d8.y;
                    if (ux < 0 || uy < 0 || ux >= w || uy >= h)
                    {
                        continue;
                    }
                    int u = uy * w + ux;
                    CombatSiegeCell cu = s.Cells[u];
                    if (!Pass(f, cu))
                    {
                        continue;
                    }
                    bool diag = k >= 4;
                    if (diag && (!Pass(f, s.Cells[uy * w + cx]) || !Pass(f, s.Cells[cy * w + ux])))
                    {
                        continue;
                    }
                    int nd = dd + StepCost(cu, cc, diag) + enterC;
                    if (nd < dist[baseOff + u])
                    {
                        dist[baseOff + u] = nd;
                        HeapPush(heap, nd, u);
                    }
                }
            }
        }

        /// <summary>全量计算第 <paramref name="f"/> 张流场（写进 <paramref name="dist"/> 的 [baseOff, baseOff + w·h)）。</summary>
        private static void FullInto(ref CombatSiegeData s, int f, int w, int h, NativeArray<int> dist, int baseOff)
        {
            int n = w * h;
            var heap = new NativeList<long>(1024, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                CombatSiegeCell c = s.Cells[i];
                int seed = Pass(f, c) ? Seed(ref s, f, c) : CombatSiegeConst.Inf;
                dist[baseOff + i] = seed;
                if (seed < CombatSiegeConst.Inf)
                {
                    HeapPush(heap, seed, i);
                }
            }
            Relax(ref s, f, w, h, heap, dist, baseOff);
            heap.Dispose();
        }

        private static void Full(ref CombatData d, int f, int w, int h)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            FullInto(ref s, f, w, h, s.Dist.AsArray(), f * w * h);
            s.FieldValid[f] = 1;
            CombatSiegeStats st = s.Stats[0];
            st.FullComputes++;
            s.Stats[0] = st;
        }

        /// <summary>增量更新第 <paramref name="f"/> 张流场（本步的变化格在 <see cref="CombatSiegeData.Changed"/>，旧值在 <see cref="CombatSiegeData.OldCells"/>）。</summary>
        private static void Incremental(ref CombatData d, int f, int w, int h)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            NativeArray<int> dist = s.Dist.AsArray();
            int off = f * w * h;
            int inf = CombatSiegeConst.Inf;
            var invalid = new NativeHashSet<int>(64, Allocator.Temp);
            var queue = new NativeList<int>(64, Allocator.Temp);
            // A：哪些变化对这张流场是“变差”（不可走、代价变大、进入代价变大、起点没了 / 变大）→ 作废的根。
            for (int q = 0; q < s.Changed.Length; q++)
            {
                int c = s.Changed[q];
                CombatSiegeCell co = OldOf(ref s, c);
                CombatSiegeCell cn = s.Cells[c];
                bool passO = Pass(f, co);
                bool passN = Pass(f, cn);
                int seedO = passO ? Seed(ref s, f, co) : inf;
                int seedN = passN ? Seed(ref s, f, cn) : inf;
                bool worse = (passO && !passN) || Mult(cn) > Mult(co) || Enter(ref s, f, cn) > Enter(ref s, f, co) || seedN > seedO;
                if (worse && dist[off + c] < inf && invalid.Add(c))
                {
                    queue.Add(c);
                }
                if (passO && !passN)
                {
                    // 这一格变得不可走：以它为拐角的斜边（两个正交邻格之间）也断了——旧最短路走过那条斜边的格一并作废。
                    int cx = c % w;
                    int cy = c / w;
                    for (int k = 0; k < 4; k++)
                    {
                        int2 a = NavSearch.Dir8(k);
                        int2 b = NavSearch.Dir8((k + 1) & 3);
                        int ax = cx + a.x, ay = cy + a.y, bx = cx + b.x, by = cy + b.y;
                        if (ax < 0 || ay < 0 || ax >= w || ay >= h || bx < 0 || by < 0 || bx >= w || by >= h)
                        {
                            continue;
                        }
                        int ai = ay * w + ax;
                        int bi = by * w + bx;
                        int kab = DirIndex(bx - ax, by - ay);
                        int kba = DirIndex(ax - bx, ay - by);
                        if (dist[off + ai] < inf && kab >= 0)
                        {
                            int ec = OldEdgeCost(ref s, f, w, h, ax, ay, kab);
                            if (ec >= 0 && dist[off + bi] < inf && dist[off + ai] == dist[off + bi] + ec && invalid.Add(ai))
                            {
                                queue.Add(ai);
                            }
                        }
                        if (dist[off + bi] < inf && kba >= 0)
                        {
                            int ec = OldEdgeCost(ref s, f, w, h, bx, by, kba);
                            if (ec >= 0 && dist[off + ai] < inf && dist[off + bi] == dist[off + ai] + ec && invalid.Add(bi))
                            {
                                queue.Add(bi);
                            }
                        }
                    }
                }
            }
            // B：旧图上的最短路树后代（u 的最短路经过 x：dist[u] = dist[x] + 旧代价(u → x)）全部作废。先找全再改值。
            int head = 0;
            while (head < queue.Length)
            {
                int x = queue[head++];
                int xx = x % w;
                int xy = x / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 d8 = NavSearch.Dir8(k);
                    int ux = xx - d8.x;
                    int uy = xy - d8.y;
                    if (ux < 0 || uy < 0 || ux >= w || uy >= h)
                    {
                        continue;
                    }
                    int u = uy * w + ux;
                    if (dist[off + u] >= inf || invalid.Contains(u))
                    {
                        continue;
                    }
                    int ec = OldEdgeCost(ref s, f, w, h, ux, uy, k);
                    if (ec >= 0 && dist[off + u] == dist[off + x] + ec && invalid.Add(u))
                    {
                        queue.Add(u);
                    }
                }
            }
            for (int q = 0; q < queue.Length; q++)
            {
                dist[off + queue[q]] = inf;
            }
            // C / D：作废的格、变化格及其周围 8 格按新图取一次暂定值，进堆；再统一松弛。
            var heap = new NativeList<long>(math.max(64, queue.Length * 2), Allocator.Temp);
            var cand = new NativeHashSet<int>(math.max(64, queue.Length + s.Changed.Length * 9), Allocator.Temp);
            for (int q = 0; q < queue.Length; q++)
            {
                cand.Add(queue[q]);
            }
            for (int q = 0; q < s.Changed.Length; q++)
            {
                int c = s.Changed[q];
                cand.Add(c);
                int cx = c % w;
                int cy = c / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 d8 = NavSearch.Dir8(k);
                    int nx = cx + d8.x;
                    int ny = cy + d8.y;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                    {
                        cand.Add(ny * w + nx);
                    }
                }
            }
            NativeArray<int> cands = cand.ToNativeArray(Allocator.Temp);
            cands.Sort(); // 与集合内部顺序无关（距离本来就唯一，排序只是让堆的推入顺序固定）
            for (int q = 0; q < cands.Length; q++)
            {
                int u = cands[q];
                CombatSiegeCell cu = s.Cells[u];
                if (!Pass(f, cu))
                {
                    dist[off + u] = inf;
                    continue;
                }
                int t = Seed(ref s, f, cu);
                int ux = u % w;
                int uy = u / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 d8 = NavSearch.Dir8(k);
                    int vx = ux + d8.x;
                    int vy = uy + d8.y;
                    if (vx < 0 || vy < 0 || vx >= w || vy >= h)
                    {
                        continue;
                    }
                    int v = vy * w + vx;
                    int dv = dist[off + v];
                    if (dv >= inf)
                    {
                        continue;
                    }
                    int ec = EdgeCost(ref s, f, w, h, ux, uy, k);
                    if (ec >= 0 && dv + ec < t)
                    {
                        t = dv + ec;
                    }
                }
                if (t < dist[off + u])
                {
                    dist[off + u] = t;
                    HeapPush(heap, t, u);
                }
            }
            Relax(ref s, f, w, h, heap, dist, off);
            CombatSiegeStats st = s.Stats[0];
            st.Incrementals++;
            st.InvalidatedCells += queue.Length;
            st.LastInvalidated += queue.Length;
            s.Stats[0] = st;
            cands.Dispose();
            cand.Dispose();
            heap.Dispose();
            queue.Dispose();
            invalid.Dispose();
        }

        private static int DirIndex(int dx, int dy)
        {
            for (int k = 0; k < 8; k++)
            {
                int2 d8 = NavSearch.Dir8(k);
                if (d8.x == dx && d8.y == dy)
                {
                    return k;
                }
            }
            return -1;
        }

        /// <summary>
        /// 本步要用的流场：每个攻城单位的职能（撤退中 = 撤退）要它的开路场；站在开路场到不了的格（被完全堵死）的单位还要它的破墙场。
        /// 没有就全量算（需要的集合是状态的纯函数 → 读档后同一步算出同样的场）。
        /// </summary>
        private static void EnsureNeeded(ref CombatData d, int w, int h)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int need = 0;
            for (int i = 0; i < d.Count; i++)
            {
                if (!d.IsAlive(i) || d.Behavior[i] != (byte)CombatBehavior.Raider)
                {
                    continue;
                }
                CombatSiegeUnit su = d.Siege[i];
                if (su.Role == 0 || su.Mode == (byte)CombatSiegeMode.Skirmish)
                {
                    continue;
                }
                need |= 1 << (FieldRole(su) * 2);
            }
            for (int f = 0; f < CombatSiegeConst.FieldCount; f += 2)
            {
                if ((need & (1 << f)) != 0 && s.FieldValid[f] == 0)
                {
                    Full(ref d, f, w, h);
                }
            }
            int n = w * h;
            for (int i = 0; i < d.Count; i++)
            {
                if (!d.IsAlive(i) || d.Behavior[i] != (byte)CombatBehavior.Raider)
                {
                    continue;
                }
                CombatSiegeUnit su = d.Siege[i];
                if (su.Role == 0 || su.Mode == (byte)CombatSiegeMode.Skirmish)
                {
                    continue;
                }
                int fo = FieldRole(su) * 2;
                int idx = IndexOf(ref d, CombatLogic.CellOf(d.Pos[i]));
                if (idx >= 0 && s.Dist[fo * n + idx] >= CombatSiegeConst.Inf)
                {
                    need |= 1 << (fo + 1);
                }
            }
            for (int f = 1; f < CombatSiegeConst.FieldCount; f += 2)
            {
                if ((need & (1 << f)) != 0 && s.FieldValid[f] == 0)
                {
                    Full(ref d, f, w, h);
                }
            }
            CombatSiegeStats st = s.Stats[0];
            st.NeedMask = need;
            int valid = 0;
            for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
            {
                valid |= s.FieldValid[f] != 0 ? 1 << f : 0;
            }
            st.ValidMask = valid;
            s.Stats[0] = st;
        }

        public static int FieldRole(in CombatSiegeUnit su) =>
            su.Mode == (byte)CombatSiegeMode.Retreat ? CombatSiegeConst.RoleRetreat : math.clamp(su.Role - 1, 0, 2);

        private static void RebuildBreaches(ref CombatData d)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            s.Breaches.Clear();
            for (int i = 0; i < d.Count; i++)
            {
                if (!d.IsAlive(i))
                {
                    continue;
                }
                int b = d.Siege[i].Breach;
                if (b <= 0)
                {
                    continue;
                }
                bool dup = false;
                for (int k = 0; k < s.Breaches.Length; k++)
                {
                    if (s.Breaches[k] == b)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup && d.SlotOf(b) >= 0 && d.IsAlive(d.SlotOf(b)))
                {
                    s.Breaches.Add(b);
                }
            }
        }

        /// <summary>自检：每张有效流场全量重算后与当前值逐格比对。</summary>
        public static void VerifyAll(ref CombatData d, NativeArray<int> outv)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            outv[0] = 0;
            outv[1] = 0;
            if (r.z <= 0)
            {
                return;
            }
            int n = r.z * r.w;
            var tmp = new NativeArray<int>(n, Allocator.Temp);
            for (int f = 0; f < CombatSiegeConst.FieldCount; f++)
            {
                if (s.FieldValid[f] == 0)
                {
                    continue;
                }
                FullInto(ref s, f, r.z, r.w, tmp, 0);
                int off = f * n;
                for (int i = 0; i < n; i++)
                {
                    if (tmp[i] != s.Dist[off + i])
                    {
                        outv[0] = outv[0] + 1;
                    }
                }
                outv[1] = outv[1] + 1;
            }
            tmp.Dispose();
            // 格子缓存：每个结构单位的占地格都写着它（占用、类别），没有格子写着已不在的结构单位——增量维护与全量重建结果一致（全量重算流场读的也是这份缓存，上面的比对看不出它坏）。
            int bad = 0;
            for (int i = 0; i < d.Count; i++)
            {
                if (!IsSiegeStructure(ref d, i))
                {
                    continue;
                }
                CombatSiegeStructCache c = CacheOf(ref d, i, 0);
                int x0 = math.max(c.Foot.x, r.x);
                int y0 = math.max(c.Foot.y, r.y);
                int x1 = math.min(c.Foot.z, r.x + r.z - 1);
                int y1 = math.min(c.Foot.w, r.y + r.w - 1);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        CombatSiegeCell cell = s.Cells[(y - r.y) * r.z + (x - r.x)];
                        if (cell.Occ != d.Id[i] || cell.Cat != c.Cat)
                        {
                            bad++;
                        }
                    }
                }
            }
            for (int k = 0; k < n; k++)
            {
                int occ = s.Cells[k].Occ;
                if (occ == 0)
                {
                    continue;
                }
                int slot = d.SlotOf(occ);
                if (slot < 0 || !IsSiegeStructure(ref d, slot))
                {
                    bad++;
                }
            }
            outv[0] = outv[0] + bad;
        }

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static int DistAt(ref CombatData d, int f, int idx)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            if (idx < 0 || f < 0 || f >= CombatSiegeConst.FieldCount || s.FieldValid[f] == 0)
            {
                return CombatSiegeConst.Inf;
            }
            return s.Dist[f * r.z * r.w + idx];
        }

        /// <summary>从格 <paramref name="idx"/> 沿流场下坡走一步：返回下一格（代价最小；并列取方向序号小的），到了起点 / 走不动返回 -1。</summary>
        public static int NextStep(ref CombatData d, int f, int idx)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int4 r = s.Rect[0];
            int w = r.z;
            int h = r.w;
            int off = f * w * h;
            int cur = s.Dist[off + idx];
            if (cur >= CombatSiegeConst.Inf)
            {
                return -1;
            }
            CombatSiegeCell cc = s.Cells[idx];
            if (Seed(ref s, f, cc) == cur && Pass(f, cc))
            {
                return -1; // 已在起点（目标边上 / 集结点）
            }
            int ux = idx % w;
            int uy = idx / w;
            int best = -1;
            int bestV = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int ec = EdgeCost(ref s, f, w, h, ux, uy, k);
                if (ec < 0)
                {
                    continue;
                }
                int2 d8 = NavSearch.Dir8(k);
                int v = (uy + d8.y) * w + ux + d8.x;
                int dv = s.Dist[off + v];
                if (dv >= CombatSiegeConst.Inf)
                {
                    continue;
                }
                int total = dv + ec;
                if (total < bestV)
                {
                    bestV = total;
                    best = v;
                }
            }
            return best;
        }

        /// <summary>从 <paramref name="idx"/> 沿破墙场往前看至多 <paramref name="lookahead"/> 格，第一格挡路的结构单位（不是这张场的目标）——要拆的那段墙。没有 = 0。</summary>
        public static int BreachAhead(ref CombatData d, int f, int idx, int lookahead)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int cur = idx;
            for (int k = 0; k < lookahead && cur >= 0; k++)
            {
                int nx = NextStep(ref d, f, cur);
                if (nx < 0)
                {
                    return 0;
                }
                CombatSiegeCell c = s.Cells[nx];
                if (c.Occ != 0 && (c.Nav & 1) != 0 && Seed(ref s, f, c) >= CombatSiegeConst.Inf)
                {
                    return c.Occ;
                }
                cur = nx;
            }
            return 0;
        }

        // ─────────────────────────────── 突袭单位的一步 ───────────────────────────────

        /// <summary>
        /// 攻城单位的一步（FGR-DEF-030 / 031 / 032）：
        /// 1. 随队维修（行为参数带治疗量的单位）：冷却到点就修射程内血量比例最低的友军。
        /// 2. 每 RetargetSeconds 重选目标：射程内、而且“就是流场要带它去的地方”的职能目标（见 <see cref="RoleTarget"/>）；被完全堵死时，破墙场前方第一段挡路的建筑（够得着才打）——
        ///    攻城时朝目标、撤退时朝集结点都一样（撤退中被重新围死也会拆出去）；都没有时射程内的己方机器 / 炮塔 / 无人机（边走边打）。
        /// 3. 冷却到点开火（打建筑乘对建筑倍率，在 DamageUnit 里）。
        /// 4. 移动：正在打职能目标 / 破墙目标时原地不动；否则沿开路场（到不了就沿破墙场）下坡；撤退中走到集结点就离场；在剧场外的照旧沿层级寻路朝目标点走。
        /// </summary>
        public static void StepSiegeRaider(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            int w = d.Weapon[i];
            int bp = d.BProfile[i];
            if (w < 0 || bp < 0)
            {
                return;
            }
            ref CombatSiegeData s = ref d.SiegeState;
            CombatSiegeConfig cfg = s.Cfg[0];
            CombatWeapon wp = d.Weapons[w];
            CombatBehaviorProfile p = d.Profiles[bp];
            float speed = CombatLogic.SlowedSpeed(ref d, i, p.Speed);
            CombatSiegeUnit su = d.Siege[i];
            bool retreat = su.Mode == (byte)CombatSiegeMode.Retreat;
            int role = FieldRole(su);
            int fo = role * 2;
            int fb = fo + 1;
            double2 pos = d.Pos[i];
            int idx = IndexOf(ref d, CombatLogic.CellOf(pos));
            int n = s.Rect[0].z * s.Rect[0].w;

            // 撤退：走到集结点（撤退场的值在离场半径内）或已在剧场外 → 离开家园。
            if (retreat && (idx < 0 || (s.FieldValid[fo] != 0 && s.Dist[fo * n + idx] <= (int)(cfg.ExitRadius * 10f))))
            {
                Exit(ref d, i);
                return;
            }

            // 1. 随队维修
            if (!retreat && p.EffectAmount > 0f && p.EffectRange > 0f)
            {
                su.HealCd -= dt;
                if (su.HealCd <= 0f)
                {
                    int ally = LowestAlly(ref d, ref grid, i, p.EffectRange);
                    if (ally >= 0)
                    {
                        CombatLogic.Heal(ref d, ally, p.EffectAmount, i);
                        su.HealCd = math.max(0.1f, p.CycleSeconds);
                    }
                    else
                    {
                        su.HealCd = 0f;
                    }
                }
            }

            // 2. 选目标
            bool useBreach = idx >= 0 && s.FieldValid[fo] != 0 && s.Dist[fo * n + idx] >= CombatSiegeConst.Inf && s.FieldValid[fb] != 0
                             && s.Dist[fb * n + idx] < CombatSiegeConst.Inf;
            float range = CombatLogic.EngageRange(ref d, i, wp);
            CombatCommand cmd = d.Cmd[i];
            int t = d.SlotOf(cmd.Target);
            float retarget = d.Secondary[i] - dt;
            bool holds = su.Hold != 0;
            if (t < 0 || !d.IsAlive(t) || !d.Has(t, CombatUnitFlags.Targetable) || retarget <= 0f)
            {
                t = -1;
                holds = false;
                su.Breach = 0;
                if (!retreat)
                {
                    t = RoleTarget(ref d, ref grid, i, role, range, idx, fo, fb);
                    holds = t >= 0;
                }
                // 被完全堵死：沿破墙场拆前方第一段挡路的墙。复审修复（P1，FGR-DEF-032 / B11）：撤退中也拆——撤退开路场到不了集结点（敌人进墙后玩家补上了缺口）时，
                // 沿撤退破墙场把挡路的那段拆开再走，不站在墙里既不离场也不拆墙。
                if (t < 0 && useBreach)
                {
                    int wall = BreachAhead(ref d, fb, idx, (int)math.ceil(range) + 4);
                    su.Breach = wall;
                    int ws = d.SlotOf(wall);
                    if (ws >= 0 && d.IsAlive(ws) && EdgeDistance(ref d, i, ws) <= range)
                    {
                        t = ws;
                        holds = true;
                    }
                }
                if (t < 0)
                {
                    t = SelfDefense(ref d, ref grid, i, range);
                }
                cmd.Target = t >= 0 ? d.Id[t] : 0;
                su.Hold = (byte)(holds ? 1 : 0);
                retarget = cfg.RetargetSeconds > 0f ? cfg.RetargetSeconds : 0.5f;
            }
            d.Secondary[i] = retarget;
            d.Cmd[i] = cmd;
            d.Siege[i] = su;

            // 3. 开火
            float cycle = d.Cycle[i] - dt;
            d.Cycle[i] = cycle;
            bool inRange = t >= 0 && EdgeDistance(ref d, i, t) <= range;
            if (inRange && cycle <= 0f)
            {
                d.Cycle[i] = CombatLogic.EffectiveCooldown(wp);
                CombatLogic.UnitAttack(ref d, i, t, wp);
            }

            // 4. 移动
            if (holds && inRange)
            {
                CombatLogic.Separate(ref d, ref grid, i, speed, dt);
                return;
            }
            int f = -1;
            if (idx >= 0 && s.FieldValid[fo] != 0 && s.Dist[fo * n + idx] < CombatSiegeConst.Inf)
            {
                f = fo;
            }
            else if (useBreach)
            {
                f = fb;
            }
            if (f >= 0)
            {
                int next = NextStep(ref d, f, idx);
                if (next >= 0)
                {
                    CombatSiegeCell nc = s.Cells[next];
                    bool wall = (nc.Nav & 1) != 0;
                    if (!wall)
                    {
                        int2 cell = CellAtIndex(ref d, next);
                        double2 target = new double2(cell.x, cell.y);
                        double2 to = target - pos;
                        double len = math.length(to);
                        if (len > 1e-6)
                        {
                            d.Pos[i] = CombatLogic.MoveCollide(ref d, i, pos, pos + to / len * math.min(speed * dt, len));
                        }
                    }
                }
            }
            else if (idx < 0)
            {
                CombatLogic.RaiderRouteMove(ref d, i, speed, dt);
            }
            CombatLogic.Separate(ref d, ref grid, i, speed, dt);
        }

        /// <summary>
        /// FG6-DEF-05（DEBT-FG4ECO07-02，FGR-DEF-043“守在指定点、按教义交战”）：空闲的驻防机器——驻防点 GuardRadius 内最近的敌人（并列取槽位小者）：
        /// 射程内按武器节奏开火（与编队攻击同一套 FireAt：瞄准、热量、装甲）；射程外朝它靠近，但不走出守点半径；半径内没有敌人 → 回到驻防点。
        /// 每单位 O(半径内的格)，确定性（只看步内状态）。
        /// </summary>
        public static void StepGuard(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            CombatSiegeUnit su = d.Siege[i];
            int w = d.Weapon[i];
            double2 pos = d.Pos[i];
            double2 post = su.GuardPost;
            float radius = su.GuardRadius;
            float speed = CombatLogic.SlowedSpeed(ref d, i, d.Speed[i]);
            int best = -1;
            double bestDist = double.MaxValue;
            if (w >= 0 && w < d.Weapons.Length)
            {
                double reach = radius + grid.MaxRadius;
                int cx0 = grid.CellOf(post.x - reach);
                int cx1 = grid.CellOf(post.x + reach);
                int cy0 = grid.CellOf(post.y - reach);
                int cy1 = grid.CellOf(post.y + reach);
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        if (!grid.TryGetCell(cx, cy, out int start, out int count))
                        {
                            continue;
                        }
                        for (int e = start; e < start + count; e++)
                        {
                            int k = grid.Slots[e];
                            if (d.Faction[k] != (byte)CombatFaction.Hostile || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable)
                                || math.distance(post, d.Pos[k]) > radius)
                            {
                                continue;
                            }
                            double dist = EdgeDistance(ref d, i, k);
                            if (dist < bestDist || (dist == bestDist && k < best))
                            {
                                bestDist = dist;
                                best = k;
                            }
                        }
                    }
                }
            }
            su.HealCd -= dt;
            CombatSiegeStats gs = d.SiegeState.IsCreated ? d.SiegeState.Stats[0] : default;
            gs.GuardTicks++;
            if (best >= 0)
            {
                gs.GuardEngaged++;
                CombatWeapon wp = d.Weapons[w];
                float range = CombatLogic.EngageRange(ref d, i, wp);
                if (bestDist <= range)
                {
                    if (su.HealCd <= 0f)
                    {
                        CombatFireResult r = CombatLogic.FireAt(ref d, i, best);
                        gs.GuardLastResult = (int)r;
                        if (r == CombatFireResult.Ok || r == CombatFireResult.StillAiming)
                        {
                            su.HealCd = math.max(0.05f, CombatLogic.EffectiveCooldown(wp));
                            gs.GuardShots++;
                        }
                        else
                        {
                            su.HealCd = 0.2f; // 打不出去（过热 / 瞄准中断…）：稍后再试，不每步刷
                        }
                    }
                }
                else
                {
                    double2 to = d.Pos[best] - pos;
                    double len = math.length(to);
                    if (len > 1e-6)
                    {
                        double2 next = pos + to / len * math.min(speed * dt, len);
                        if (math.distance(next, post) <= radius)
                        {
                            d.Pos[i] = CombatLogic.MoveCollide(ref d, i, pos, next);
                        }
                    }
                }
            }
            else
            {
                double2 to = post - pos;
                double len = math.length(to);
                if (len > 1.0)
                {
                    d.Pos[i] = CombatLogic.MoveCollide(ref d, i, pos, pos + to / len * math.min(speed * dt, len));
                }
            }
            su.HealCd = math.max(su.HealCd, -1f);
            d.Siege[i] = su;
            if (d.SiegeState.IsCreated)
            {
                d.SiegeState.Stats[0] = gs;
            }
        }
        /// <summary>撤退的单位走到集结点：发不丢的事件（热更层把幸存者并回行进队伍），当场移除（不算阵亡、不计击杀）。</summary>
        private static void Exit(ref CombatData d, int i)
        {
            CombatLogic.Gameplay(ref d, CombatEventKind.SiegeExited, i, d.Siege[i].Group, 0f, 0f, d.Pos[i], 0, 0);
            d.Set(i, CombatUnitFlags.Alive, false);
            d.Set(i, CombatUnitFlags.Targetable, false);
            d.Cmd[i] = default;
            if (d.Has(i, CombatUnitFlags.RemoveOnDeath))
            {
                CombatScalars sc = d.Scalars[0];
                sc.Tombstones++;
                d.Scalars[0] = sc;
            }
            CombatSiegeStats st = d.SiegeState.Stats[0];
            st.Exits++;
            d.SiegeState.Stats[0] = st;
        }

        /// <summary>从单位 <paramref name="i"/> 到目标 <paramref name="t"/> 外沿的距离（结构单位按半径算，建筑大了也能从边上打到）。</summary>
        public static double EdgeDistance(ref CombatData d, int i, int t) => math.max(0.0, math.distance(d.Pos[i], d.Pos[t]) - d.Radius[t]);

        /// <summary>
        /// 射程内的职能目标：偏好代价 + 外沿距离 × 10 最小；并列取槽位小者。
        /// 复审修复（P1，FGR-DEF-030“突击直扑核心”）：偏好代价决定“停不停下来打”，不只是射程内排序——只接受“流场此刻要带它去的地方”：
        /// 偏好 + 从当前格到目标外一圈的代价下界（8 向步长 10 / 14 的距离，流场代价不会比它小）≤ 当前格的流场值（开路场；被堵死时破墙场）。
        /// 流场值 = 各目标“偏好 + 实际路程”的最小值，所以流场正带它去的那个目标一定满足（不会漏掉主目标）；不满足的一定不是最省的目标——
        /// 突击型路过仓库（偏好 120 米）不再站定拆光，监听站（20 米）顺路照样先拆。不在剧场里 / 两张场都到不了时不设门槛（只打得到射程内的）。
        /// </summary>
        private static int RoleTarget(ref CombatData d, ref CombatGrid grid, int i, int role, float range, int idx, int fo, int fb)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            int mask = role < CombatSiegeConst.RoleRetreat ? s.RoleMask[role] : 0;
            if (mask == 0 || range <= 0f)
            {
                return -1;
            }
            int gate = CombatSiegeConst.Inf;
            int2 here = default;
            if (idx >= 0)
            {
                int n = s.Rect[0].z * s.Rect[0].w;
                gate = s.FieldValid[fo] != 0 ? s.Dist[fo * n + idx] : CombatSiegeConst.Inf;
                if (gate >= CombatSiegeConst.Inf && s.FieldValid[fb] != 0)
                {
                    gate = s.Dist[fb * n + idx];
                }
                here = CellAtIndex(ref d, idx);
            }
            double2 pos = d.Pos[i];
            double reach = range + grid.MaxRadius + 2.0;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            int best = -1;
            long bestScore = long.MaxValue;
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                        {
                            continue;
                        }
                        int cat = d.Siege[k].Cat & mask;
                        if (cat == 0)
                        {
                            continue;
                        }
                        double ed = EdgeDistance(ref d, i, k);
                        if (ed > range)
                        {
                            continue;
                        }
                        int bias = s.Bias[role * CombatSiegeConst.CategoryCount + math.tzcnt(cat)];
                        if (gate < CombatSiegeConst.Inf && (long)bias + RingLowerBound(here, d.Siege[k].FootMin, d.Siege[k].FootMax) > gate)
                        {
                            continue; // 不是流场要带它去的地方（偏好太高、要绕路才去）：不停下来打
                        }
                        long score = (long)bias + (long)math.round(ed * 10.0);
                        if (score < bestScore || (score == bestScore && k < best))
                        {
                            bestScore = score;
                            best = k;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 从格 <paramref name="c"/> 走到占地 [<paramref name="a"/>, <paramref name="b"/>] 外一圈（开路场的起点都在这一圈里；破墙场的起点是占地本身，更远）的流场代价下界：
        /// 8 向距离，正交 10、斜向 14（流场每步代价 = 两格倍率之和 × 5 / × 7，倍率 ≥ 1，再加非负的进入代价，所以不会更小）。
        /// </summary>
        public static int RingLowerBound(int2 c, int2 a, int2 b)
        {
            int2 lo = math.min(a, b) - 1;
            int2 hi = math.max(a, b) + 1;
            int dx = math.max(0, math.max(lo.x - c.x, c.x - hi.x));
            int dy = math.max(0, math.max(lo.y - c.y, c.y - hi.y));
            return 10 * math.max(dx, dy) + 4 * math.min(dx, dy);
        }

        /// <summary>自卫：射程内最近的己方机器 / 炮塔 / 出动的无人机（边走边打，不停下）。</summary>
        private static int SelfDefense(ref CombatData d, ref CombatGrid grid, int i, float range)
        {
            if (range <= 0f)
            {
                return -1;
            }
            double2 pos = d.Pos[i];
            double reach = range + grid.MaxRadius + 2.0;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            int best = -1;
            double bestD = double.MaxValue;
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (d.Faction[k] != (byte)CombatFaction.Player || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                        {
                            continue;
                        }
                        byte kind = d.Kind[k];
                        bool mobile = kind == (byte)CombatUnitKind.Machine || kind == (byte)CombatUnitKind.Turret
                                      || (kind == (byte)CombatUnitKind.Structure && d.Speed[k] > 0f);
                        if (!mobile)
                        {
                            continue;
                        }
                        double ed = EdgeDistance(ref d, i, k);
                        if (ed > range)
                        {
                            continue;
                        }
                        if (ed < bestD || (ed == bestD && k < best))
                        {
                            bestD = ed;
                            best = k;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>随队维修：射程内血量比例最低的受伤友军（同阵营、存活；并列取槽位小者）。</summary>
        private static int LowestAlly(ref CombatData d, ref CombatGrid grid, int i, float range)
        {
            double2 pos = d.Pos[i];
            double reach = range + 2.0;
            int cx0 = grid.CellOf(pos.x - reach);
            int cx1 = grid.CellOf(pos.x + reach);
            int cy0 = grid.CellOf(pos.y - reach);
            int cy1 = grid.CellOf(pos.y + reach);
            byte fac = d.Faction[i];
            int best = -1;
            float bestPct = 1f;
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    if (!grid.TryGetCell(cx, cy, out int start, out int count))
                    {
                        continue;
                    }
                    for (int e = start; e < start + count; e++)
                    {
                        int k = grid.Slots[e];
                        if (d.Faction[k] != fac || !d.IsAlive(k) || d.MaxHp[k] <= 0f)
                        {
                            continue;
                        }
                        float pct = d.Hp[k] / d.MaxHp[k];
                        if (pct >= 1f - 1e-4f || math.distance(pos, d.Pos[k]) > range)
                        {
                            continue;
                        }
                        if (pct < bestPct || (pct == bestPct && k < best))
                        {
                            bestPct = pct;
                            best = k;
                        }
                    }
                }
            }
            return best;
        }

        // ─────────────────────────────── 溅射 ───────────────────────────────

        /// <summary>
        /// 敌方单位打中带攻城类别的结构单位：在目标占地上离攻击者最近的那一格记一次命中（同格累计）。热更层每步取至多 N 格结算到附近的传送带 / 管线 / 施工虚影。
        /// 满了丢弃并计数（不抛异常）。O(1)。
        /// </summary>
        public static void RecordImpact(ref CombatData d, int t, int attacker, float damage)
        {
            ref CombatSiegeData s = ref d.SiegeState;
            if (!s.IsCreated || damage <= 0f)
            {
                return;
            }
            CombatSiegeUnit su = d.Siege[t];
            double2 from = attacker >= 0 && attacker < d.Count ? d.Pos[attacker] : d.Pos[t];
            int2 lo = math.min(su.FootMin, su.FootMax);
            int2 hi = math.max(su.FootMin, su.FootMax);
            int2 cell = new int2((int)math.clamp(math.round(from.x), lo.x, hi.x), (int)math.clamp(math.round(from.y), lo.y, hi.y));
            long key = ((long)cell.x << 32) ^ (uint)cell.y;
            if (s.ImpactIndex.TryGetValue(key, out int at))
            {
                CombatSiegeImpact im = s.Impacts[at];
                im.Damage += damage;
                s.Impacts[at] = im;
                return;
            }
            if (s.Impacts.Length >= CombatSiegeConst.ImpactCap)
            {
                CombatSiegeStats st = s.Stats[0];
                st.ImpactsDropped++;
                s.Stats[0] = st;
                return;
            }
            s.ImpactIndex.TryAdd(key, s.Impacts.Length);
            s.Impacts.Add(new CombatSiegeImpact { Cell = cell, Damage = damage });
        }
    }
}
