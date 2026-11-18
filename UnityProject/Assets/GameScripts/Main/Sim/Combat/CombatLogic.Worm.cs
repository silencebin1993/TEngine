using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>FG12-EVT-02（FG10 FGR-EVT-012）：掘进蠕虫的常量。</summary>
    public static class CombatWormConst
    {
        /// <summary>在地下潜行：打不到（不能被锁定、不受伤害、不咬）、小地图与头顶标记隐藏。</summary>
        public const byte PhaseUnder = 0;
        /// <summary>钻出地面：可以被锁定、受伤、会咬附近的单位。</summary>
        public const byte PhaseSurface = 1;
        /// <summary>时限到了潜回地下离开（不再钻出；热更层在 <see cref="CombatWorm.PhaseT"/> ≥ 潜入时长后移除）。</summary>
        public const byte PhaseLeaving = 2;
        /// <summary>每条蠕虫最多几节 / 路线最多几个顶点（定长表，快照按定长字段写）。</summary>
        public const int MaxSegments = 16;
        public const int MaxRoute = 12;
        /// <summary>快照段 39 每条记录的字段数：标量 19 + 路线 2 × 12 + 节 16。</summary>
        public const int Fields = 19 + MaxRoute * 2 + MaxSegments;
        /// <summary>每步最多记几处大规模爆炸（同一步内相距 <see cref="BlastMerge"/> 格以内的合并成一处，计次累加）。</summary>
        public const int MaxBlasts = 16;
        public const float BlastMerge = 12f;
    }

    /// <summary>
    /// FG12-EVT-02：一条掘进蠕虫（分节链）。每一节是一个普通的敌方单位（耐久、受伤、阵亡、掉落都走既有规则），链只决定它们站在哪、此刻在地上还是地下：
    /// 头部沿闭合路线（首尾相接的折线）按弧长 <see cref="S"/> 前进，第 k 节在弧长 S − k × <see cref="Spacing"/> 处；地下 <see cref="DiveSeconds"/> 秒、地上 <see cref="SurfaceSeconds"/> 秒交替。
    /// 速度 = <see cref="Speed"/> × 各存活节里最慢的减速系数（受寒 / 冻结 = 0 / 导线 / 残骸……与单位同一套 <see cref="CombatLogic.SlowedSpeed"/>）× 被牵引时 <see cref="DragMult"/>。
    /// 内核状态：进快照段 39（格式 24）与状态哈希。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatWorm
    {
        public int Id;
        public byte Phase;
        public byte SegCount;
        public short Pad;
        public float PhaseT;
        public float SurfaceSeconds;
        public float DiveSeconds;
        public float Speed;
        public float Spacing;
        public float DragMult;
        public float RouteLength;
        /// <summary>头部沿路线的弧长（取模路线长）与上一步的值（判断这一步有没有节被外力推开 = 被牵引）。</summary>
        public double S;
        public double PrevS;
        /// <summary>已阵亡 / 已移除的节（第 k 位 = 第 k 节）。</summary>
        public uint DeadMask;
        public float HpSum;
        public float MaxHpSum;
        /// <summary>钻出地面的次数（破土那一次算第 1 次）。</summary>
        public int Surfacings;
        /// <summary>本步实际速度（格 / 秒；悬停与自检读）。</summary>
        public float LastSpeed;
        public FixedList512Bytes<double2> Route;
        public FixedList128Bytes<int> Segs;
    }

    /// <summary>FG12-EVT-02 掘进蠕虫的逐节推进（Main/Sim，与内核一步同在 Burst 作业里）。O(蠕虫数 × 节数 × 路线顶点数)，没有蠕虫时一次长度比较。</summary>
    public static partial class CombatLogic
    {
        /// <summary>闭合路线上弧长 <paramref name="s"/> 处的点（s 取模路线长，负数从尾部倒数）。</summary>
        public static double2 WormPoint(in CombatWorm w, double s)
        {
            int n = w.Route.Length;
            if (n == 0)
            {
                return double2.zero;
            }
            if (n == 1 || w.RouteLength <= 1e-4f)
            {
                return w.Route[0];
            }
            double len = w.RouteLength;
            s %= len;
            if (s < 0)
            {
                s += len;
            }
            for (int i = 0; i < n; i++)
            {
                double2 a = w.Route[i];
                double2 b = w.Route[(i + 1) % n];
                double seg = math.distance(a, b);
                if (s <= seg || i == n - 1)
                {
                    return seg > 1e-9 ? a + (b - a) * math.min(1.0, s / seg) : a;
                }
                s -= seg;
            }
            return w.Route[0];
        }

        public static float WormRouteLength(in FixedList512Bytes<double2> route)
        {
            double len = 0;
            for (int i = 0; i < route.Length; i++)
            {
                len += math.distance(route[i], route[(i + 1) % route.Length]);
            }
            return (float)len;
        }

        internal static void StepWorms(ref CombatData d, float dt)
        {
            for (int wi = 0; wi < d.Worms.Length; wi++)
            {
                CombatWorm w = d.Worms[wi];
                // 1. 存活 / 耐久 / 最慢的那一节 / 有没有节被外力推离上一步的位置（牵引、击退）。
                uint dead = 0;
                float hp = 0f;
                float slow = 1f;
                bool dragged = false;
                for (int k = 0; k < w.SegCount; k++)
                {
                    int slot = d.SlotOf(w.Segs[k]);
                    if (slot < 0 || !d.IsAlive(slot))
                    {
                        dead |= 1u << k;
                        continue;
                    }
                    hp += d.Hp[slot];
                    slow = math.min(slow, IsImmobile(ref d, slot) ? 0f : SlowedSpeed(ref d, slot, 1f));
                    if (math.distancesq(d.Pos[slot], WormPoint(w, w.S - k * (double)w.Spacing)) > 0.0025)
                    {
                        dragged = true;
                    }
                }
                w.DeadMask = dead;
                w.HpSum = hp;
                int alive = w.SegCount - math.countbits(dead);
                if (alive <= 0)
                {
                    w.LastSpeed = 0f;
                    d.Worms[wi] = w;
                    continue;
                }
                // 2. 前进与地上 / 地下交替。
                float v = w.Speed * math.max(0f, slow) * (dragged ? w.DragMult : 1f);
                w.LastSpeed = v;
                w.PrevS = w.S;
                w.S += v * (double)dt;
                if (w.RouteLength > 0f && w.S > w.RouteLength * 64.0)
                {
                    w.S %= w.RouteLength; // 只为数值不无限增长；取模后点的位置不变
                }
                w.PhaseT += dt;
                if (w.Phase == CombatWormConst.PhaseUnder && w.PhaseT >= w.DiveSeconds)
                {
                    w.Phase = CombatWormConst.PhaseSurface;
                    w.PhaseT = 0f;
                    w.Surfacings++;
                }
                else if (w.Phase == CombatWormConst.PhaseSurface && w.PhaseT >= w.SurfaceSeconds)
                {
                    w.Phase = CombatWormConst.PhaseUnder;
                    w.PhaseT = 0f;
                }
                // 3. 各节就位、按阶段切换可否被锁定 / 受伤 / 开火。
                bool up = w.Phase == CombatWormConst.PhaseSurface;
                for (int k = 0; k < w.SegCount; k++)
                {
                    if ((dead & (1u << k)) != 0)
                    {
                        continue;
                    }
                    int slot = d.SlotOf(w.Segs[k]);
                    double2 p = WormPoint(w, w.S - k * (double)w.Spacing);
                    d.Pos[slot] = p;
                    d.Set(slot, CombatUnitFlags.Targetable, up);
                    d.Set(slot, CombatUnitFlags.Invulnerable, !up);
                    d.Set(slot, CombatUnitFlags.Stealthed, !up);
                    d.Set(slot, CombatUnitFlags.WeaponEnabled, up);
                }
                d.Worms[wi] = w;
            }
        }

        /// <summary>
        /// FG12-EVT-02（FGR-EVT-011 末条“大规模爆炸也算”）：半径不小于 <see cref="CombatData.BlastMinRadius"/> 的爆炸在本步记一处（相近的合并、计次累加；x, y, 计次, 最大半径）。
        /// 热更层每个世界步取走（<see cref="CombatKernel.DrainBlasts"/>）按地点加震动值；只是本步的输出缓冲，不进快照。BlastMinRadius = 0（没有登记）时不记。
        /// </summary>
        internal static void NoteBlast(ref CombatData d, double2 pos, float radius)
        {
            if (d.BlastMinRadius <= 0f || !(radius >= d.BlastMinRadius))
            {
                return;
            }
            float m2 = CombatWormConst.BlastMerge * CombatWormConst.BlastMerge;
            for (int i = 0; i < d.WormBlasts.Length; i++)
            {
                float4 b = d.WormBlasts[i];
                if (math.distancesq(new double2(b.x, b.y), pos) <= m2 || d.WormBlasts.Length >= CombatWormConst.MaxBlasts && i == d.WormBlasts.Length - 1)
                {
                    b.z += 1f;
                    b.w = math.max(b.w, radius);
                    d.WormBlasts[i] = b;
                    return;
                }
            }
            d.WormBlasts.Add(new float4((float)pos.x, (float)pos.y, 1f, radius));
        }
    }
}
