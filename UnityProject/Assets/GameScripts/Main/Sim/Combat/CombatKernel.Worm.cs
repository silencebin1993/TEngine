using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>FG12-EVT-02 掘进蠕虫的内核接口（热更层只经 Campaign/Combat/CombatSite 调用）与快照段 39。</summary>
    public sealed partial class CombatKernel
    {
        public int WormCount => _d.Worms.Length;

        /// <summary>
        /// 登记一条蠕虫：<paramref name="segUnits"/> 是已经生成好的各节单位（第 0 节是头），沿闭合路线 <paramref name="route"/> 前进；从弧长 <paramref name="s0"/>、钻出地面开始（破土）。
        /// 同 ID 已存在、节数 / 顶点数越界返回 false。
        /// </summary>
        public bool AddWorm(int id, IReadOnlyList<double2> route, IReadOnlyList<int> segUnits, float speed, float spacing, float surfaceSeconds, float diveSeconds,
            float dragMult, double s0)
        {
            if (id <= 0 || route == null || segUnits == null || route.Count < 2 || route.Count > CombatWormConst.MaxRoute
                || segUnits.Count < 1 || segUnits.Count > CombatWormConst.MaxSegments || WormIndex(id) >= 0)
            {
                return false;
            }
            var w = new CombatWorm
            {
                Id = id,
                Phase = CombatWormConst.PhaseSurface,
                SegCount = (byte)segUnits.Count,
                SurfaceSeconds = math.max(0.1f, surfaceSeconds),
                DiveSeconds = math.max(0.1f, diveSeconds),
                Speed = math.max(0f, speed),
                Spacing = math.max(0.1f, spacing),
                DragMult = math.clamp(dragMult, 0f, 1f),
                S = s0,
                PrevS = s0,
                Surfacings = 1,
            };
            for (int i = 0; i < route.Count; i++)
            {
                w.Route.Add(route[i]);
            }
            w.RouteLength = CombatLogic.WormRouteLength(w.Route);
            float maxHp = 0f;
            for (int k = 0; k < segUnits.Count; k++)
            {
                w.Segs.Add(segUnits[k]);
                int slot = _d.SlotOf(segUnits[k]);
                if (slot >= 0)
                {
                    maxHp += _d.MaxHp[slot];
                    _d.Pos[slot] = CombatLogic.WormPoint(w, s0 - k * (double)w.Spacing);
                }
            }
            w.MaxHpSum = maxHp;
            w.HpSum = maxHp;
            _d.Worms.Add(w);
            Touch();
            return true;
        }

        /// <summary>移除一条蠕虫；<paramref name="despawn"/> 时同时移除它还在的各节（潜回地下离开 / 读档对不上）。</summary>
        public bool RemoveWorm(int id, bool despawn)
        {
            int i = WormIndex(id);
            if (i < 0)
            {
                return false;
            }
            CombatWorm w = _d.Worms[i];
            _d.Worms.RemoveAt(i);
            if (despawn)
            {
                for (int k = 0; k < w.SegCount; k++)
                {
                    Despawn(w.Segs[k]);
                }
            }
            Touch();
            return true;
        }

        /// <summary>时限到了：潜回地下、不再钻出（各节打不到；<see cref="CombatWorm.PhaseT"/> 从 0 起算，热更层到潜入时长后移除）。</summary>
        public bool SetWormLeaving(int id)
        {
            int i = WormIndex(id);
            if (i < 0)
            {
                return false;
            }
            CombatWorm w = _d.Worms[i];
            if (w.Phase != CombatWormConst.PhaseLeaving)
            {
                w.Phase = CombatWormConst.PhaseLeaving;
                w.PhaseT = 0f;
                _d.Worms[i] = w;
                // 立即沉下去（本步之前已经在地上的节不再能被锁定）。
                for (int k = 0; k < w.SegCount; k++)
                {
                    int slot = _d.SlotOf(w.Segs[k]);
                    if (slot >= 0)
                    {
                        _d.Set(slot, CombatUnitFlags.Targetable, false);
                        _d.Set(slot, CombatUnitFlags.Invulnerable, true);
                        _d.Set(slot, CombatUnitFlags.Stealthed, true);
                        _d.Set(slot, CombatUnitFlags.WeaponEnabled, false);
                    }
                }
            }
            Touch();
            return true;
        }

        public bool TryGetWorm(int id, out CombatWorm worm)
        {
            int i = WormIndex(id);
            worm = i >= 0 ? _d.Worms[i] : default;
            return i >= 0;
        }

        /// <summary>这条蠕虫各节的单位 ID（按节序；热更层程序集不引用 Unity.Collections，定长表不跨程序集暴露）。</summary>
        public int CopyWormSegs(int id, List<int> into)
        {
            into?.Clear();
            int i = WormIndex(id);
            if (i < 0)
            {
                return 0;
            }
            CombatWorm w = _d.Worms[i];
            for (int k = 0; k < w.SegCount; k++)
            {
                into?.Add(w.Segs[k]);
            }
            return w.SegCount;
        }

        /// <summary>第 <paramref name="index"/> 条（自检 / 读档对账按顺序枚举）。</summary>
        public CombatWorm WormAt(int index) => _d.Worms[index];

        public static double2 WormPointOf(in CombatWorm w, double s) => CombatLogic.WormPoint(w, s);

        private int WormIndex(int id)
        {
            for (int i = 0; i < _d.Worms.Length; i++)
            {
                if (_d.Worms[i].Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>半径不小于多少格的爆炸算大规模（0 = 不记）。派生量：热更层建好 / 读档后写一次。</summary>
        public float BlastMinRadius
        {
            get => _d.BlastMinRadius;
            set => _d.BlastMinRadius = math.max(0f, value);
        }

        /// <summary>取走本步记下的大规模爆炸（x, y, 计次, 最大半径）。O(本步爆炸处数 ≤ 16)。</summary>
        public int DrainBlasts(List<float4> into)
        {
            int n = _d.WormBlasts.Length;
            for (int i = 0; i < n; i++)
            {
                into?.Add(_d.WormBlasts[i]);
            }
            _d.WormBlasts.Clear();
            return n;
        }

        /// <summary>自检：在 <paramref name="pos"/> 记一次半径 <paramref name="radius"/> 的爆炸（与内核各爆炸点同一入口）。</summary>
        public void NoteBlastForTests(double2 pos, float radius) => CombatLogic.NoteBlast(ref _d, pos, radius);

        // ── 快照段 39（格式 24）：每条蠕虫一行，定长字段（路线 / 节按上限补 0，按计数读回）──

        private void WriteWormSegment(BinaryWriter w, EmgSegmentWriter sw)
        {
            sw.Begin(EmgConst.SegWorm, CombatWormConst.Fields, _d.Worms.Length);
            for (int i = 0; i < _d.Worms.Length; i++)
            {
                CombatWorm x = _d.Worms[i];
                if (sw.F()) w.Write(x.Id);
                if (sw.F()) w.Write(x.Phase);
                if (sw.F()) w.Write(x.SegCount);
                if (sw.F()) w.Write(x.PhaseT);
                if (sw.F()) w.Write(x.SurfaceSeconds);
                if (sw.F()) w.Write(x.DiveSeconds);
                if (sw.F()) w.Write(x.Speed);
                if (sw.F()) w.Write(x.Spacing);
                if (sw.F()) w.Write(x.DragMult);
                if (sw.F()) w.Write(x.RouteLength);
                if (sw.F()) w.Write(x.S);
                if (sw.F()) w.Write(x.PrevS);
                if (sw.F()) w.Write(x.DeadMask);
                if (sw.F()) w.Write(x.HpSum);
                if (sw.F()) w.Write(x.MaxHpSum);
                if (sw.F()) w.Write(x.Surfacings);
                if (sw.F()) w.Write(x.LastSpeed);
                if (sw.F()) w.Write((byte)x.Route.Length);
                if (sw.F()) w.Write((byte)0);
                for (int k = 0; k < CombatWormConst.MaxRoute; k++)
                {
                    double2 p = k < x.Route.Length ? x.Route[k] : double2.zero;
                    if (sw.F()) w.Write(p.x);
                    if (sw.F()) w.Write(p.y);
                }
                for (int k = 0; k < CombatWormConst.MaxSegments; k++)
                {
                    if (sw.F()) w.Write(k < x.Segs.Length ? x.Segs[k] : 0);
                }
                sw.EndRecord();
            }
            sw.End();
        }

        private static CombatLoadResult ParseWormSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n > 64)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                var x = new CombatWorm();
                byte routeCount = 0;
                if (sr.F()) x.Id = r.ReadInt32();
                if (sr.F()) x.Phase = r.ReadByte();
                if (sr.F()) x.SegCount = r.ReadByte();
                if (sr.F()) x.PhaseT = r.ReadSingle();
                if (sr.F()) x.SurfaceSeconds = r.ReadSingle();
                if (sr.F()) x.DiveSeconds = r.ReadSingle();
                if (sr.F()) x.Speed = r.ReadSingle();
                if (sr.F()) x.Spacing = r.ReadSingle();
                if (sr.F()) x.DragMult = r.ReadSingle();
                if (sr.F()) x.RouteLength = r.ReadSingle();
                if (sr.F()) x.S = r.ReadDouble();
                if (sr.F()) x.PrevS = r.ReadDouble();
                if (sr.F()) x.DeadMask = r.ReadUInt32();
                if (sr.F()) x.HpSum = r.ReadSingle();
                if (sr.F()) x.MaxHpSum = r.ReadSingle();
                if (sr.F()) x.Surfacings = r.ReadInt32();
                if (sr.F()) x.LastSpeed = r.ReadSingle();
                if (sr.F()) routeCount = r.ReadByte();
                if (sr.F()) r.ReadByte();
                for (int k = 0; k < CombatWormConst.MaxRoute; k++)
                {
                    double px = 0, py = 0;
                    if (sr.F()) px = r.ReadDouble();
                    if (sr.F()) py = r.ReadDouble();
                    if (k < routeCount)
                    {
                        x.Route.Add(new double2(px, py));
                    }
                }
                for (int k = 0; k < CombatWormConst.MaxSegments; k++)
                {
                    int u = 0;
                    if (sr.F()) u = r.ReadInt32();
                    if (k < x.SegCount)
                    {
                        x.Segs.Add(u);
                    }
                }
                if (!sr.RecordOk || x.Id <= 0 || x.Phase > CombatWormConst.PhaseLeaving || x.SegCount < 1 || x.SegCount > CombatWormConst.MaxSegments
                    || routeCount < 2 || routeCount > CombatWormConst.MaxRoute || double.IsNaN(x.S) || double.IsInfinity(x.S)
                    || !math.isfinite(x.Speed) || !math.isfinite(x.Spacing) || !math.isfinite(x.PhaseT))
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Worms.Add(x);
            }
            return CombatLoadResult.Ok;
        }
    }
}
