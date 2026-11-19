using System.IO;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG13-FAC-01 白潮专属语法的内核接口（热更层只经 Campaign/Combat/CombatSite 调用）与快照段 40～42（格式 25；只在非空时写，没有白潮时快照与哈希逐字不变）。
    /// 轨道打击排程 / 取消是可复用接口：FG13-END-03 清洗波次“落点提前 30 秒标出”直接调 <see cref="ScheduleOrbitalStrike"/>（beacon = 0 时只能由 <see cref="CancelOrbitalStrike"/> 取消）。
    /// </summary>
    public sealed partial class CombatKernel
    {
        public int FleetCount => _d.Fleets.Length;

        public bool TryGetFleet(int unitId, out CombatFleet state)
        {
            int k = CombatLogic.FlIndexOf(ref _d, unitId);
            state = k >= 0 ? _d.Fleets[k] : default;
            return k >= 0;
        }

        /// <summary>自检用：直接给单位挂电荷（en.elec 累积量），测护盾的电磁倍率与“带电荷不回充”。</summary>
        public bool SetUnitChargeForTests(int id, float charge)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || i >= _d.EmgUnit.Length)
            {
                return false;
            }
            var u = _d.EmgUnit[i];
            u.AccA.z = Unity.Mathematics.math.max(0f, charge);
            _d.EmgUnit[i] = u;
            return true;
        }

        /// <summary>自检用：单位身上的电荷（en.elec 累积量）；正处在放电相态（累积到阈值进相态时电荷清零，FG19B §3.3）时返回 1。</summary>
        public float UnitChargeForTests(int id)
        {
            int i = _d.SlotOf(id);
            if (i < 0 || i >= _d.EmgUnit.Length)
            {
                return 0f;
            }
            EmgUnitState s = _d.EmgUnit[i];
            return (s.PhaseMask & (1 << EmgPhaseConst.Charge)) != 0 ? 1f : s.AccA.z;
        }

        /// <summary>第 i 条白潮运行状态（地图 / 自检遍历用）。</summary>
        public bool TryGetFleetAt(int i, out CombatFleet state)
        {
            bool ok = i >= 0 && i < _d.Fleets.Length;
            state = ok ? _d.Fleets[i] : default;
            return ok;
        }

        /// <summary>FG13-END-05（FG19 FGR-EMG-161）：单位的端口闸门（护盾节点）——族别、剩余与总值。没有闸门 / 不存在返回 false。</summary>
        public bool TryGetGate(int unitId, out byte family, out float gate, out float max)
        {
            family = CombatFleetConst.GateNone;
            gate = max = 0f;
            int k = CombatLogic.FlIndexOf(ref _d, unitId);
            int i = _d.SlotOf(unitId);
            if (k < 0 || i < 0 || !CombatLogic.FlGateOf(ref _d, i, out family, out max))
            {
                return false;
            }
            gate = _d.Fleets[k].Gate;
            return true;
        }

        public int FleetStrikeCount => _d.FleetStrikes.Length;

        public CombatFleetStrike FleetStrikeAt(int i) => i >= 0 && i < _d.FleetStrikes.Length ? _d.FleetStrikes[i] : default;

        public int FleetMarkCount => _d.FleetMarks.Length;

        public CombatFleetMark FleetMarkAt(int i) => i >= 0 && i < _d.FleetMarks.Length ? _d.FleetMarks[i] : default;

        /// <summary>排一次轨道打击：<paramref name="delay"/> 秒后圆心 <paramref name="center"/>、半径 <paramref name="radius"/> 内单位各受 <paramref name="damage"/>，
        /// 发 FleetEvent（建筑按 <paramref name="building"/> 由热更层结算）。<paramref name="beaconUnitId"/> &gt; 0 时该单位在倒计时内阵亡 = 取消。返回打击 Id（0 = 排不下 / 参数不合法）。</summary>
        public int ScheduleOrbitalStrike(double2 center, float radius, float damage, float delay, float building, int beaconUnitId = 0)
        {
            if (!IsFinite(center) || !(radius > 0f) || !(delay >= 0f) || float.IsInfinity(delay) || !(damage >= 0f))
            {
                return 0;
            }
            return CombatLogic.FlScheduleStrike(ref _d, center, radius, damage, delay, building, beaconUnitId);
        }

        /// <summary>FG13-END-04：直接写一格灭活焦土（白潮滩头着陆时登陆信标周围的焦土）：来源单位活着就不恢复，来源不在了按 <paramref name="recover"/> + 序次 × <paramref name="stagger"/> 恢复。
        /// 与灭活光束同一张表（快照段 42，格式不变）。来源不存在 / 参数不合法返回 false。</summary>
        public bool AddInertMark(int2 cell, int sourceUnitId, float recover, float stagger)
        {
            if (_d.SlotOf(sourceUnitId) < 0 || !(recover >= 0f) || !(stagger >= 0f) || float.IsInfinity(recover) || float.IsInfinity(stagger))
            {
                return false;
            }
            CombatLogic.FlAddMark(ref _d, cell, sourceUnitId, recover, stagger);
            return true;
        }

        /// <summary>取消一次还没落下的轨道打击（发取消提示）。没有这次打击返回 false。</summary>
        public bool CancelOrbitalStrike(int strikeId)
        {
            for (int s = 0; s < _d.FleetStrikes.Length; s++)
            {
                if (_d.FleetStrikes[s].Id == strikeId)
                {
                    CombatFleetStrike x = _d.FleetStrikes[s];
                    _d.FleetStrikes.RemoveAt(s);
                    CombatLogic.Cue(ref _d, CombatEventKind.FleetCue, -1, x.Id, 0f, x.Center, CombatFleetConst.CueStrikeCancel);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 某点是否在屏蔽塔（行为参数 FlBlockRange &gt; 0、活着的单位）的拒绝接入范围里；是则给出最近那座塔的单位号与范围。O(白潮表长)，只在发起接入 / 画范围时调用。
        /// </summary>
        public bool AnyUplinkBlockAt(double2 p, out int unitId, out float range)
        {
            unitId = 0;
            range = 0f;
            double best = double.MaxValue;
            for (int k = 0; k < _d.Fleets.Length; k++)
            {
                int i = _d.SlotOf(_d.Fleets[k].Unit);
                if (i < 0 || !_d.IsAlive(i))
                {
                    continue;
                }
                int bp = _d.BProfile[i];
                if (bp < 0 || bp >= _d.Profiles.Length || !(_d.Profiles[bp].FlBlockRange > 0f))
                {
                    continue;
                }
                float r = _d.Profiles[bp].FlBlockRange;
                double dd = math.distancesq(_d.Pos[i], p);
                if (dd <= (double)r * r && dd < best)
                {
                    best = dd;
                    unitId = _d.Id[i];
                    range = r;
                }
            }
            return unitId != 0;
        }

        /// <summary>屏蔽塔清单（画范围用）：第 n 座活着的塔的单位号 / 位置 / 范围；没有返回 false。O(白潮表长)。</summary>
        public bool UplinkBlockerAt(int n, out int unitId, out double2 pos, out float range)
        {
            int seen = 0;
            for (int k = 0; k < _d.Fleets.Length; k++)
            {
                int i = _d.SlotOf(_d.Fleets[k].Unit);
                int bp = i >= 0 && _d.IsAlive(i) ? _d.BProfile[i] : -1;
                if (bp < 0 || bp >= _d.Profiles.Length || !(_d.Profiles[bp].FlBlockRange > 0f))
                {
                    continue;
                }
                if (seen++ == n)
                {
                    unitId = _d.Id[i];
                    pos = _d.Pos[i];
                    range = _d.Profiles[bp].FlBlockRange;
                    return true;
                }
            }
            unitId = 0;
            pos = default;
            range = 0f;
            return false;
        }

        // ── 快照段 40～42（格式 25）──

        private void WriteFleetSegments(BinaryWriter w, EmgSegmentWriter sw, int format)
        {
            if (_d.Fleets.Length > 0)
            {
                sw.Begin(EmgConst.SegFleet, format >= 26 ? CombatFleetConst.Fields : CombatFleetConst.FieldsV25, _d.Fleets.Length);
                for (int i = 0; i < _d.Fleets.Length; i++)
                {
                    CombatFleet x = _d.Fleets[i];
                    if (sw.F()) w.Write(x.Unit);
                    if (sw.F()) w.Write(x.State);
                    if (sw.F()) w.Write(x.Intact);
                    if (sw.F()) w.Write(x.Pad0);
                    if (sw.F()) w.Write(x.Pad1);
                    if (sw.F()) w.Write(x.Leader);
                    if (sw.F()) w.Write(x.AimTarget);
                    if (sw.F()) w.Write(x.Until);
                    if (sw.F()) w.Write(x.Shield);
                    if (sw.F()) w.Write(x.ShieldAt);
                    if (sw.F()) w.Write(x.Gate);
                    if (sw.F()) w.Write(x.GateBy);
                    sw.EndRecord();
                }
                sw.End();
            }
            if (_d.FleetStrikes.Length > 0)
            {
                sw.Begin(EmgConst.SegFleetStrike, CombatFleetConst.StrikeFields, _d.FleetStrikes.Length);
                for (int i = 0; i < _d.FleetStrikes.Length; i++)
                {
                    CombatFleetStrike x = _d.FleetStrikes[i];
                    if (sw.F()) w.Write(x.Id);
                    if (sw.F()) w.Write(x.Beacon);
                    if (sw.F()) w.Write(x.Center.x);
                    if (sw.F()) w.Write(x.Center.y);
                    if (sw.F()) w.Write(x.Radius);
                    if (sw.F()) w.Write(x.Damage);
                    if (sw.F()) w.Write(x.Building);
                    if (sw.F()) w.Write(x.At);
                    if (sw.F()) w.Write((byte)0);
                    sw.EndRecord();
                }
                sw.End();
            }
            if (_d.FleetMarks.Length > 0)
            {
                sw.Begin(EmgConst.SegFleetMark, CombatFleetConst.MarkFields, _d.FleetMarks.Length);
                for (int i = 0; i < _d.FleetMarks.Length; i++)
                {
                    CombatFleetMark x = _d.FleetMarks[i];
                    if (sw.F()) w.Write(x.Cell.x);
                    if (sw.F()) w.Write(x.Cell.y);
                    if (sw.F()) w.Write(x.Source);
                    if (sw.F()) w.Write(x.Recover);
                    if (sw.F()) w.Write(x.Stagger);
                    if (sw.F()) w.Write(x.RecoverAt);
                    sw.EndRecord();
                }
                sw.End();
            }
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static CombatLoadResult ParseFleetSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n > 4096)
            {
                return CombatLoadResult.InvalidValue;
            }
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                var x = new CombatFleet();
                if (sr.F()) x.Unit = r.ReadInt32();
                if (sr.F()) x.State = r.ReadByte();
                if (sr.F()) x.Intact = r.ReadByte();
                if (sr.F()) x.Pad0 = r.ReadByte();
                if (sr.F()) x.Pad1 = r.ReadByte();
                if (sr.F()) x.Leader = r.ReadInt32();
                if (sr.F()) x.AimTarget = r.ReadInt32();
                if (sr.F()) x.Until = r.ReadDouble();
                if (sr.F()) x.Shield = r.ReadSingle();
                if (sr.F()) x.ShieldAt = r.ReadDouble();
                if (sr.F()) x.Gate = r.ReadSingle();
                if (sr.F()) x.GateBy = r.ReadInt32();
                if (!sr.RecordOk || !Finite(x.Gate) || x.Gate < 0f || x.GateBy < 0 || x.Unit <= 0 || !seen.Add(x.Unit) || x.State > CombatFleetConst.StatePodFalling || x.Intact > 1 || x.Leader < 0 || x.AimTarget < 0
                    || !Finite(x.Until) || !Finite(x.ShieldAt) || !Finite(x.Shield) || x.Shield < 0f)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.Fleets.Add(x);
            }
            return CombatLoadResult.Ok;
        }

        private static CombatLoadResult ParseFleetStrikeSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n > CombatFleetConst.StrikeMax)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                var x = new CombatFleetStrike();
                if (sr.F()) x.Id = r.ReadInt32();
                if (sr.F()) x.Beacon = r.ReadInt32();
                if (sr.F()) x.Center.x = r.ReadDouble();
                if (sr.F()) x.Center.y = r.ReadDouble();
                if (sr.F()) x.Radius = r.ReadSingle();
                if (sr.F()) x.Damage = r.ReadSingle();
                if (sr.F()) x.Building = r.ReadSingle();
                if (sr.F()) x.At = r.ReadDouble();
                if (sr.F()) r.ReadByte();
                if (!sr.RecordOk || x.Id <= 0 || x.Beacon < 0 || !Finite(x.Center.x) || !Finite(x.Center.y) || !(x.Radius > 0f) || !Finite(x.Radius)
                    || !(x.Damage >= 0f) || !Finite(x.Damage) || !(x.Building >= 0f && x.Building <= 1f) || !Finite(x.At))
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.FleetStrikes.Add(x);
            }
            return CombatLoadResult.Ok;
        }

        private static CombatLoadResult ParseFleetMarkSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n > CombatFleetConst.MarkMax)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                var x = new CombatFleetMark();
                if (sr.F()) x.Cell.x = r.ReadInt32();
                if (sr.F()) x.Cell.y = r.ReadInt32();
                if (sr.F()) x.Source = r.ReadInt32();
                if (sr.F()) x.Recover = r.ReadSingle();
                if (sr.F()) x.Stagger = r.ReadSingle();
                if (sr.F()) x.RecoverAt = r.ReadDouble();
                if (!sr.RecordOk || x.Source < 0 || !(x.Recover >= 0f) || !Finite(x.Recover) || !(x.Stagger >= 0f) || !Finite(x.Stagger) || !Finite(x.RecoverAt) || x.RecoverAt < 0.0)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.FleetMarks.Add(x);
            }
            return CombatLoadResult.Ok;
        }
    }
}
