using System.IO;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG11-ATTR-01（FGR-EMG-182 / 181，ADR-EMG-001 ⑨ 更正）：交互伤害归因的持久数据——每单位来源（段 36：状态位来源 + 相态来源）、斑块沾染来源（段 37）、
    /// 持久链归属（段 38）。格式 23 起写，都只在非空时写（没有归因数据时快照与哈希逐字不变）；进 Emg 段哈希。旧档（≤ 22 或没有这三段）读到取空 = 来源不明，不报错。
    /// 只影响统计显示与悬停，不参与任何结算。
    /// </summary>
    public sealed partial class CombatKernel
    {
        private const int UnitSrcFields = 1 + CombatConst.StatusBitStride + EmgPhaseConst.Count;
        private const int CoatAttrFields = 5 + EmgOwners.Cap;
        private const int ChainOwnFields = 2 + EmgOwners.Cap;

        private bool UnitSrcAny(int i)
        {
            int b0 = i * CombatConst.StatusBitStride, p0 = i * EmgPhaseConst.Count;
            for (int b = 0; b < CombatConst.StatusBitStride; b++)
            {
                if (_d.StatusBitSource[b0 + b] != 0)
                {
                    return true;
                }
            }
            for (int k = 0; k < EmgPhaseConst.Count; k++)
            {
                if (_d.EmgPhaseSrc[p0 + k] != 0)
                {
                    return true;
                }
            }
            return false;
        }

        private int UnitSrcRows()
        {
            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                n += UnitSrcAny(i) ? 1 : 0;
            }
            return n;
        }

        private int ChainOwnRows()
        {
            int n = 0;
            for (int i = 0; i < _d.EmgChainOwns.Length; i++)
            {
                n += _d.EmgChainOwns[i].Chain != 0 ? 1 : 0;
            }
            return n;
        }

        /// <summary>这一份快照要写几个归因段（WriteEmg 段数表头用；与 <see cref="WriteAttrSegments"/> 同一判定）。</summary>
        /// <remarks>段 38 持久链归属随写环同步登记，与环同样不进 StateHash（FGR-EMG-180：多写环记录不改哈希；同段 33 计数表的做法），只进快照。</remarks>
        private int AttrSegmentCount(int format, bool hash, out int unitRows, out int chainRows)
        {
            unitRows = format >= 23 ? UnitSrcRows() : 0;
            chainRows = format >= 23 && !hash ? ChainOwnRows() : 0;
            return format < 23 ? 0 : (unitRows > 0 ? 1 : 0) + (_d.CoatAttr.Length > 0 ? 1 : 0) + (chainRows > 0 ? 1 : 0);
        }

        private void WriteOwners(BinaryWriter w, EmgSegmentWriter sw, in EmgOwners o)
        {
            for (int q = 0; q < EmgOwners.Cap; q++)
            {
                if (sw.F()) w.Write(o[q]);
            }
        }

        private void WriteAttrSegments(BinaryWriter w, EmgSegmentWriter sw, int unitRows, int chainRows)
        {
            if (unitRows > 0)
            {
                sw.Begin(EmgConst.SegUnitSrc, UnitSrcFields, unitRows);
                for (int i = 0; i < _d.Count; i++)
                {
                    if (!UnitSrcAny(i))
                    {
                        continue;
                    }
                    if (sw.F()) w.Write(_d.Id[i]);
                    for (int b = 0; b < CombatConst.StatusBitStride; b++)
                    {
                        if (sw.F()) w.Write(_d.StatusBitSource[i * CombatConst.StatusBitStride + b]);
                    }
                    for (int k = 0; k < EmgPhaseConst.Count; k++)
                    {
                        if (sw.F()) w.Write(_d.EmgPhaseSrc[i * EmgPhaseConst.Count + k]);
                    }
                    sw.EndRecord();
                }
                sw.End();
            }
            if (_d.CoatAttr.Length > 0)
            {
                sw.Begin(EmgConst.SegCoatAttr, CoatAttrFields, _d.CoatAttr.Length);
                for (int i = 0; i < _d.CoatAttr.Length; i++)
                {
                    EmgCoatAttr a = _d.CoatAttr[i];
                    if (sw.F()) w.Write(a.Unit);
                    if (sw.F()) w.Write(a.Bit);
                    if (sw.F()) w.Write(a.Key);
                    if (sw.F()) w.Write(a.Matter);
                    if (sw.F()) w.Write(a.Chain);
                    WriteOwners(w, sw, a.Owners);
                    sw.EndRecord();
                }
                sw.End();
            }
            if (chainRows > 0)
            {
                sw.Begin(EmgConst.SegChainOwn, ChainOwnFields, chainRows);
                for (int i = 0; i < _d.EmgChainOwns.Length; i++)
                {
                    EmgChainOwn c = _d.EmgChainOwns[i];
                    if (c.Chain == 0)
                    {
                        continue;
                    }
                    if (sw.F()) w.Write(c.Chain);
                    if (sw.F()) w.Write(c.Key);
                    WriteOwners(w, sw, c.Owners);
                    sw.EndRecord();
                }
                sw.End();
            }
        }

        private static EmgOwners ReadOwners(BinaryReader r, EmgSegmentReader sr)
        {
            EmgOwners o = default;
            for (int q = 0; q < EmgOwners.Cap; q++)
            {
                if (sr.F()) o[q] = r.ReadInt32();
            }
            return o;
        }

        private static bool OwnersValid(in EmgOwners o)
        {
            for (int q = 0; q < EmgOwners.Cap; q++)
            {
                if (o[q] < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>段 36～38 读入（单位段之后读，单位按 Id 找槽位；坏值 / 找不到的单位 / 同一格两行整份拒绝）。</summary>
        private static CombatLoadResult ParseAttrSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            switch (sr.Tag)
            {
                case EmgConst.SegUnitSrc:
                    if (n > staging.Count)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        int id = 0;
                        if (sr.F()) id = r.ReadInt32();
                        int slot = staging.SlotOf(id);
                        bool ok = slot >= 0;
                        for (int b = 0; b < CombatConst.StatusBitStride; b++)
                        {
                            int v = 0;
                            if (sr.F()) v = r.ReadInt32();
                            ok &= v >= 0;
                            if (slot >= 0)
                            {
                                staging.StatusBitSource[slot * CombatConst.StatusBitStride + b] = v;
                            }
                        }
                        for (int k = 0; k < EmgPhaseConst.Count; k++)
                        {
                            int v = 0;
                            if (sr.F()) v = r.ReadInt32();
                            ok &= v >= 0;
                            if (slot >= 0)
                            {
                                staging.EmgPhaseSrc[slot * EmgPhaseConst.Count + k] = v;
                            }
                        }
                        if (!sr.RecordOk || !ok)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegCoatAttr:
                    if (n > staging.Count * CombatConst.StatusBitStride)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var a = new EmgCoatAttr();
                        if (sr.F()) a.Unit = r.ReadInt32();
                        if (sr.F()) a.Bit = r.ReadByte();
                        if (sr.F()) a.Key = r.ReadInt32();
                        if (sr.F()) a.Matter = r.ReadInt32();
                        if (sr.F()) a.Chain = r.ReadInt32();
                        a.Owners = ReadOwners(r, sr);
                        if (!sr.RecordOk || a.Unit <= 0 || a.Bit >= CombatConst.StatusBitStride || a.Chain < 0 || !OwnersValid(a.Owners))
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.CoatAttr.Add(a);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegChainOwn:
                    if (n > EmgConst.ChainOwnSlots)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var c = new EmgChainOwn();
                        if (sr.F()) c.Chain = r.ReadInt32();
                        if (sr.F()) c.Key = r.ReadInt32();
                        c.Owners = ReadOwners(r, sr);
                        int at = c.Chain & (EmgConst.ChainOwnSlots - 1);
                        if (!sr.RecordOk || c.Chain <= 0 || !OwnersValid(c.Owners) || staging.EmgChainOwns[at].Chain != 0)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgChainOwns[at] = c;
                    }
                    return CombatLoadResult.Ok;
                default:
                    return CombatLoadResult.Ok;
            }
        }

        // ─────────────────────────────── 查询（悬停 / 自检；O(1) 或 O(沾染来源表)）───────────────────────────────

        /// <summary>FG11-ATTR-01 ① / ④：单位第 <paramref name="phase"/> 个相态（含累积阶段）的来源单位 Id；0 = 来源不明（环境 / 斑块）。单位不在返回 false。</summary>
        public bool TryGetPhaseSource(int unitId, int phase, out int sourceId)
        {
            int i = _d.SlotOf(unitId);
            sourceId = i >= 0 && phase >= 0 && phase < EmgPhaseConst.Count ? _d.EmgPhaseSrc[i * EmgPhaseConst.Count + phase] : 0;
            return i >= 0;
        }

        /// <summary>FG11-ATTR-01 ③：单位身上某个状态位最后由谁挂上（0 = 没记录 / 不在身上）。</summary>
        public int StatusBitSourceOf(int unitId, int bit)
        {
            int i = _d.SlotOf(unitId);
            return i >= 0 && bit >= 0 && bit < CombatConst.StatusBitStride && (_d.Status[i] & (1u << bit)) != 0u ? _d.StatusBitSource[i * CombatConst.StatusBitStride + bit] : 0;
        }

        /// <summary>FG11-ATTR-01 ②：沾染来源表行数与第 i 行（自检）。</summary>
        public int CoatAttrCount => _d.CoatAttr.Length;

        public EmgCoatAttr CoatAttrAt(int i) => _d.CoatAttr[i];

        /// <summary>FG11-ATTR-01 ④：持久链归属表里这条链的行（没有返回 false；自检）。</summary>
        public bool TryGetChainOwn(int chain, out EmgChainOwn row)
        {
            int at = CombatLogic.EmgChainOwnOf(ref _d, chain);
            row = at >= 0 ? _d.EmgChainOwns[at] : default;
            return at >= 0;
        }

        /// <summary>
        /// FG11-ATTR-01（自检“只改统计、不改血量胜负”）：把全部归因数据抹掉（沾染来源表、状态位来源、相态来源、持久链归属、斑块 / 残骸 / 顺延记录的归属），
        /// 结算用的字段一个不动。同一场景抹与不抹逐步比血量、存活、位置、状态位与相态，必须完全一致。
        /// </summary>
        public void EmgScrambleAttrForTests()
        {
            _d.CoatAttr.Clear();
            for (int i = 0; i < _d.StatusBitSource.Length; i++)
            {
                _d.StatusBitSource[i] = 0;
            }
            for (int i = 0; i < _d.EmgPhaseSrc.Length; i++)
            {
                _d.EmgPhaseSrc[i] = 0;
            }
            for (int i = 0; i < _d.EmgChainOwns.Length; i++)
            {
                _d.EmgChainOwns[i] = default;
            }
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                z.Owners = default;
                _d.Patches[i] = z;
            }
            for (int i = 0; i < _d.Wrecks.Length; i++)
            {
                EmgWreck w = _d.Wrecks[i];
                w.Owners = default;
                _d.Wrecks[i] = w;
            }
            for (int i = 0; i < _d.EmgPending.Length; i++)
            {
                EmgSpawn s = _d.EmgPending[i];
                s.Owners = default;
                _d.EmgPending[i] = s;
            }
        }

        /// <summary>FG11-ATTR-01（自检）：模拟读档后因果链环为空（只清环，不动持久数据），验证环外仍能取到归属。</summary>
        public void EmgClearRingForTests()
        {
            _d.EmgRingHead[0] = 0;
            _d.EmgRingHead[1] = 0;
        }
    }
}
