using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-MAT-01（FG19 FGR-EMG-080～083 / 211；ADR-EMG-001 ⑤ ⑩）：物质斑块层的热更层接口（表写入、注入、只读查询、拾取）与快照格式 17 的物质段。
    /// 热更层只在载入 / 表重载时写表（O(17)），悬停时按点拾取一块（O(斑块数)，玩家操作时），调试计数 O(1)；不按帧遍历斑块（FGR-EMG-201）。
    /// </summary>
    public sealed partial class CombatKernel
    {
        /// <summary>写物质数值表（下标 = 物质码 1～17，fg.TbMatter）。行数不是 17、存续 ≤ 0、层 / 沾染位 / 纹样越界时整表拒绝（返回 false，保留原表）。</summary>
        public bool SetMatters(IReadOnlyList<EmgMatter> byCode)
        {
            if (byCode == null || byCode.Count != EmgMatterConst.Count + 1)
            {
                return false;
            }
            for (int m = 1; m <= EmgMatterConst.Count; m++)
            {
                EmgMatter r = byCode[m];
                if (!(r.Life > 0f) || r.Layer > 1 || r.CoatBit < -1 || r.CoatBit > 29 || r.Pattern > 16 || r.Quota < 0 || !(r.MergeOverlap >= 0f && r.MergeOverlap <= 1f)
                    || r.Emit > EmgMatterConst.Count || (r.Solid != 0 && !(r.Hp > 0f)))
                {
                    return false;
                }
            }
            for (int m = 0; m <= EmgMatterConst.Count; m++)
            {
                _d.EmgMatters[m] = m == 0 ? default : byCode[m];
            }
            Touch();
            return true;
        }

        public EmgMatter MatterRow(int code) => code >= 1 && code <= EmgMatterConst.Count ? _d.EmgMatters[code] : default;

        /// <summary>DEBT-EMG1ENT01-07：实体档案表每行的稳定键（与 <see cref="SetEntityProfiles"/> 同序同长；不同长拒绝）。读档时按它把旧档里的档案下标挪到当前表。</summary>
        public bool SetEntityProfileKeys(IReadOnlyList<int> keys)
        {
            if (keys == null || keys.Count != _d.EmgProfiles.Length)
            {
                return false;
            }
            _d.EmgProfileKeys.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                _d.EmgProfileKeys.Add(keys[i]);
            }
            return true;
        }

        /// <summary>
        /// 热更层 / 自检注入一块斑块（世界源用 <paramref name="world"/>：占世界源保留额、不衰减不合并、不被挤掉）。走与内核排放同一条生成路径（合并、软 / 总上限、每步生成额度顺延）。
        /// <paramref name="ownerUnit"/> = 排放者单位 ID（0 = 无）；<paramref name="chain"/> = 所属因果链（0 = 无）。斑块层没开（emg.stage &lt; 2 / 物质表没写）返回 false。
        /// </summary>
        public bool SpawnPatch(byte matter, double2 pos, float radius, float amount = 1f, float seconds = 0f, int ownerUnit = 0, bool world = false, int chain = 0)
        {
            bool ok = CombatLogic.EmgRequestPatch(ref _d, matter, pos, radius, amount, seconds, ownerUnit, math.max(0, chain), 0, world ? EmgMatterConst.FlagWorld : (byte)0);
            Touch();
            return ok;
        }

        /// <summary>活着的斑块数。</summary>
        public int PatchCount => _d.EmgS[0].PatchLive;

        /// <summary>斑块表长度（含墓碑；遍历用 <see cref="TryGetPatch"/>，Matter = 0 的跳过）。</summary>
        public int PatchSlots => _d.Patches.Length;

        public bool TryGetPatch(int index, out CombatZone patch)
        {
            bool ok = index >= 0 && index < _d.Patches.Length && _d.Patches[index].Matter != 0;
            patch = ok ? _d.Patches[index] : default;
            return ok;
        }

        public int PatchIndexOf(int id)
        {
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                if (_d.Patches[i].Matter != 0 && _d.Patches[i].EmgId == id)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>悬停拾取：<paramref name="at"/> 所在、离中心最近的活斑块（气体层与地面层都算；没有 −1）。O(斑块数)，只在玩家悬停时。</summary>
        public int PickPatch(double2 at)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < _d.Patches.Length; i++)
            {
                CombatZone z = _d.Patches[i];
                // EMG2-MAT-02：迁入斑块（液洼 / 喷淋池 / 读法驻留）沿用原区域的悬停（液洼 = ConstructionSiteView 的液洼读数），不在这里抢走世界悬停。
                if (z.Matter == 0 || (z.PFlags & EmgMatterConst.FlagLegacy) != 0)
                {
                    continue;
                }
                double dist = math.distance(z.Pos, at);
                if (dist <= z.Radius && (dist < bd || (dist == bd && z.EmgId < _d.Patches[best].EmgId)))
                {
                    bd = dist;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>斑块可靠计数（调试层：生成 / 合并 / 到期 / 软上限并入 · 到期 / 总上限并入 · 到期 / 丢弃 / 顺延 / 残骸崩解成碎屑）。</summary>
        public EmgScalars PatchStats => _d.EmgS[0];

        /// <summary>活跃场数（EMG2-FLD-01 生成；本卡恒为 0）。</summary>
        public int FieldCount => _d.Fields.Length;

        /// <summary>自检：点上所在的物质掩码（位 = 物质码；<paramref name="airborne"/> = 离地实体不算地面层）。</summary>
        public uint MatterMaskAt(double2 p, bool airborne = false)
        {
            CombatLogic.EmgHashEnsurePublic(ref _d);
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            uint mask = CombatLogic.PatchesAt(ref _d, p, airborne, ref hits, EmgMatterConst.ContactPerUnit);
            hits.Dispose();
            return mask;
        }

        // ─────────────────────────────── 快照格式 17：物质段 ───────────────────────────────

        private const int PatchFields = 40; // EMG2-MAT-02：段尾追加迁入斑块的区域载荷 11 个字段（同版本 17 只追加，短段取缺省）
        private const int FieldFields = 12;
        private const int UnitStage2Fields = 22;

        private static void WriteUnitStage2(BinaryWriter w, in EmgUnitState u, EmgSegmentWriter sw)
        {
            if (sw.F()) w.Write(u.Temp);
            if (sw.F()) w.Write(u.AccA.x);
            if (sw.F()) w.Write(u.AccA.y);
            if (sw.F()) w.Write(u.AccA.z);
            if (sw.F()) w.Write(u.AccA.w);
            if (sw.F()) w.Write(u.AccB.x);
            if (sw.F()) w.Write(u.AccB.y);
            if (sw.F()) w.Write(u.AccB.z);
            if (sw.F()) w.Write(u.PhaseMask);
            if (sw.F()) w.Write(u.PhsTA.x);
            if (sw.F()) w.Write(u.PhsTA.y);
            if (sw.F()) w.Write(u.PhsTA.z);
            if (sw.F()) w.Write(u.PhsTA.w);
            if (sw.F()) w.Write(u.PhsTB.x);
            if (sw.F()) w.Write(u.PhsTB.y);
            if (sw.F()) w.Write(u.PhsTB.z);
            if (sw.F()) w.Write(u.Res0);
            if (sw.F()) w.Write(u.Res1);
            if (sw.F()) w.Write(u.Res2);
            if (sw.F()) w.Write(u.Res3);
            if (sw.F()) w.Write(u.Res4);
            if (sw.F()) w.Write(u.Res5);
        }

        private static void ReadUnitStage2(BinaryReader r, EmgSegmentReader sr, ref EmgUnitState u)
        {
            if (sr.F()) u.Temp = r.ReadSingle();
            if (sr.F()) u.AccA.x = r.ReadSingle();
            if (sr.F()) u.AccA.y = r.ReadSingle();
            if (sr.F()) u.AccA.z = r.ReadSingle();
            if (sr.F()) u.AccA.w = r.ReadSingle();
            if (sr.F()) u.AccB.x = r.ReadSingle();
            if (sr.F()) u.AccB.y = r.ReadSingle();
            if (sr.F()) u.AccB.z = r.ReadSingle();
            if (sr.F()) u.PhaseMask = r.ReadUInt16();
            if (sr.F()) u.PhsTA.x = r.ReadSingle();
            if (sr.F()) u.PhsTA.y = r.ReadSingle();
            if (sr.F()) u.PhsTA.z = r.ReadSingle();
            if (sr.F()) u.PhsTA.w = r.ReadSingle();
            if (sr.F()) u.PhsTB.x = r.ReadSingle();
            if (sr.F()) u.PhsTB.y = r.ReadSingle();
            if (sr.F()) u.PhsTB.z = r.ReadSingle();
            if (sr.F()) u.Res0 = r.ReadByte();
            if (sr.F()) u.Res1 = r.ReadByte();
            if (sr.F()) u.Res2 = r.ReadByte();
            if (sr.F()) u.Res3 = r.ReadByte();
            if (sr.F()) u.Res4 = r.ReadByte();
            if (sr.F()) u.Res5 = r.ReadByte();
        }

        private static void ReadPatchCounts(BinaryReader r, EmgSegmentReader sr, ref EmgScalars es)
        {
            if (sr.F()) es.PatchesBorn = r.ReadInt32();
            if (sr.F()) es.PatchesMerged = r.ReadInt32();
            if (sr.F()) es.PatchesExpired = r.ReadInt32();
            if (sr.F()) es.PatchSoftMerged = r.ReadInt32();
            if (sr.F()) es.PatchSoftExpired = r.ReadInt32();
            if (sr.F()) es.PatchCapMerged = r.ReadInt32();
            if (sr.F()) es.PatchCapExpired = r.ReadInt32();
            if (sr.F()) es.PatchDropped = r.ReadInt32();
            if (sr.F()) es.PatchDeferred = r.ReadInt32();
            if (sr.F()) es.WrecksToDebris = r.ReadInt32();
        }

        /// <summary>段 17 斑块（只写活斑块，按表内顺序）、段 18 活跃场、段 19 实体档案稳定键。</summary>
        private void WriteMatterSegments(BinaryWriter w, EmgSegmentWriter sw)
        {
            sw.Begin(EmgConst.SegPatches, PatchFields, _d.EmgS[0].PatchLive);
            // 按斑块 Id 升序写（规范顺序）：连续跑里新斑块复用墓碑槽、读档后列表是压实的，按槽位写会让同一状态写出不同字节、
            // 涌现哈希随槽位布局变（FGT-EMG-040 读档续跑哈希必须一致）。读回即按 Id 有序、无墓碑。
            var order = new long[_d.EmgS[0].PatchLive];
            int n = 0;
            for (int i = 0; i < _d.Patches.Length && n < order.Length; i++)
            {
                if (_d.Patches[i].Matter != 0)
                {
                    order[n++] = ((long)_d.Patches[i].EmgId << 32) | (uint)i;
                }
            }
            System.Array.Sort(order, 0, n);
            for (int oi = 0; oi < n; oi++)
            {
                CombatZone z = _d.Patches[(int)(order[oi] & 0xFFFFFFFFL)];
                if (sw.F()) w.Write(z.Pos.x);
                if (sw.F()) w.Write(z.Pos.y);
                if (sw.F()) w.Write(z.Radius);
                if (sw.F()) w.Write(z.Born);
                if (sw.F()) w.Write(z.Until);
                if (sw.F()) w.Write(z.NextTick);
                if (sw.F()) w.Write(z.TickInterval);
                if (sw.F()) w.Write(z.Owner);
                if (sw.F()) w.Write(z.Matter);
                if (sw.F()) w.Write(z.Layer);
                if (sw.F()) w.Write(z.PFlags);
                if (sw.F()) w.Write(z.Phase);
                if (sw.F()) w.Write(z.Amount);
                if (sw.F()) w.Write(z.Temp);
                if (sw.F()) w.Write(z.Charge);
                if (sw.F()) w.Write(z.Hp);
                if (sw.F()) w.Write(z.Kinetic);
                if (sw.F()) w.Write(z.Conc);
                if (sw.F()) w.Write(z.R0);
                if (sw.F()) w.Write(z.BurnSeconds);
                if (sw.F()) w.Write(z.MoltenAccum);
                if (sw.F()) w.Write(z.CondSince);
                if (sw.F()) w.Write(z.Owners.x);
                if (sw.F()) w.Write(z.Owners.y);
                if (sw.F()) w.Write(z.Owners.z);
                if (sw.F()) w.Write(z.Owners.w);
                if (sw.F()) w.Write(z.EmgId);
                if (sw.F()) w.Write(z.EmgChain);
                if (sw.F()) w.Write(z.EmgDepth);
                // EMG2-MAT-02：迁入斑块的区域载荷（非迁入斑块写缺省值）
                if (sw.F()) w.Write(z.Kind);
                if (sw.F()) w.Write((byte)z.Faction);
                if (sw.F()) w.Write((byte)z.Look);
                if (sw.F()) w.Write(z.Dps);
                if (sw.F()) w.Write(z.StatusMask);
                if (sw.F()) w.Write(z.StatusSeconds);
                if (sw.F()) w.Write(z.StatusDps);
                if (sw.F()) w.Write(z.StatusSlow);
                if (sw.F()) w.Write(z.StatusVuln);
                if (sw.F()) w.Write(z.Chill);
                if (sw.F()) w.Write(z.Growth);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegFields, FieldFields, _d.Fields.Length);
            for (int i = 0; i < _d.Fields.Length; i++)
            {
                EmgField f = _d.Fields[i];
                if (sw.F()) w.Write(f.Pos.x);
                if (sw.F()) w.Write(f.Pos.y);
                if (sw.F()) w.Write(f.Born);
                if (sw.F()) w.Write(f.Until);
                if (sw.F()) w.Write(f.Radius);
                if (sw.F()) w.Write(f.Strength);
                if (sw.F()) w.Write(f.Id);
                if (sw.F()) w.Write(f.Owner);
                if (sw.F()) w.Write(f.Chain);
                if (sw.F()) w.Write(f.Port);
                if (sw.F()) w.Write(f.Faction);
                if (sw.F()) w.Write(f.Depth);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegProfileKeys, 1, _d.EmgProfileKeys.Length);
            for (int i = 0; i < _d.EmgProfileKeys.Length; i++)
            {
                if (sw.F()) w.Write(_d.EmgProfileKeys[i]);
                sw.EndRecord();
            }
            sw.End();
        }

        private static CombatLoadResult ParseMatterSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            switch (sr.Tag)
            {
                case EmgConst.SegPatches:
                {
                    if (n > EmgMatterConst.DefaultCapacity)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    var ids = new HashSet<int>();
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        // 短段（本阶段较早写出的记录）缺的尾字段取生成时的缺省，不判坏值。
                        var z = new CombatZone { Faction = CombatFaction.Neutral, Kind = EmgMatterConst.ZoneKindPatch, Conc = 1f, Amount = 1f, TickInterval = EmgMatterConst.DefaultTick };
                        if (sr.F()) z.Pos.x = r.ReadDouble();
                        if (sr.F()) z.Pos.y = r.ReadDouble();
                        if (sr.F()) z.Radius = r.ReadSingle();
                        if (sr.F()) z.Born = r.ReadDouble();
                        if (sr.F()) z.Until = r.ReadDouble();
                        if (sr.F()) z.NextTick = r.ReadDouble();
                        if (sr.F()) z.TickInterval = r.ReadSingle();
                        if (sr.F()) z.Owner = r.ReadInt32();
                        if (sr.F()) z.Matter = r.ReadByte();
                        if (sr.F()) z.Layer = r.ReadByte();
                        if (sr.F()) z.PFlags = r.ReadByte();
                        if (sr.F()) z.Phase = r.ReadByte();
                        if (sr.F()) z.Amount = r.ReadSingle();
                        if (sr.F()) z.Temp = r.ReadSingle();
                        if (sr.F()) z.Charge = r.ReadSingle();
                        if (sr.F()) z.Hp = r.ReadSingle();
                        if (sr.F()) z.Kinetic = r.ReadSingle();
                        if (sr.F()) z.Conc = r.ReadSingle();
                        if (sr.F()) z.R0 = r.ReadSingle();
                        if (sr.F()) z.BurnSeconds = r.ReadSingle();
                        if (sr.F()) z.MoltenAccum = r.ReadSingle();
                        if (sr.F()) z.CondSince = r.ReadDouble();
                        if (sr.F()) z.Owners.x = r.ReadInt32();
                        if (sr.F()) z.Owners.y = r.ReadInt32();
                        if (sr.F()) z.Owners.z = r.ReadInt32();
                        if (sr.F()) z.Owners.w = r.ReadInt32();
                        if (sr.F()) z.EmgId = r.ReadInt32();
                        if (sr.F()) z.EmgChain = r.ReadInt32();
                        if (sr.F()) z.EmgDepth = r.ReadByte();
                        if (sr.F()) z.Kind = r.ReadByte();
                        if (sr.F()) z.Faction = (CombatFaction)r.ReadByte();
                        if (sr.F()) z.Look = (CombatZoneLook)r.ReadByte();
                        if (sr.F()) z.Dps = r.ReadSingle();
                        if (sr.F()) z.StatusMask = r.ReadUInt32();
                        if (sr.F()) z.StatusSeconds = r.ReadSingle();
                        if (sr.F()) z.StatusDps = r.ReadSingle();
                        if (sr.F()) z.StatusSlow = r.ReadSingle();
                        if (sr.F()) z.StatusVuln = r.ReadSingle();
                        if (sr.F()) z.Chill = r.ReadSingle();
                        if (sr.F()) z.Growth = r.ReadSingle();
                        // EMG2-MAT-02：迁入斑块的载荷校验（种类只能是读法 / 液洼 / 喷淋池 / 油迹；液洼的 Owner 必须是液洼号的相反数）；非迁入斑块恒为斑块种类、中立
                        bool legacy = (z.PFlags & EmgMatterConst.FlagLegacy) != 0;
                        if (legacy ? z.Kind > CombatConst.ZoneKindSlick || z.Kind == CombatConst.ZoneKindField || (z.Kind == CombatConst.ZoneKindLeak && z.Owner >= 0)
                                     || (byte)z.Faction > (byte)CombatFaction.Neutral || (byte)z.Look > (byte)CombatZoneLook.Residue || z.Phase > CombatConst.LeakPhaseReacted
                                     || float.IsNaN(z.Dps) || float.IsNaN(z.StatusDps) || !(z.Chill >= 0f) || float.IsNaN(z.Growth)
                                   : z.Kind != EmgMatterConst.ZoneKindPatch || z.Faction != CombatFaction.Neutral)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        if (z.R0 <= 0f)
                        {
                            z.R0 = z.Radius;
                        }
                        if (!sr.RecordOk || z.Matter < 1 || z.Matter > EmgMatterConst.Count || z.Layer > 1 || !(z.Radius > 0f) || !math.all(math.isfinite(z.Pos))
                            || !(z.TickInterval > 0f) || z.EmgId < 0 || (z.EmgId > 0 && !ids.Add(z.EmgId)) || z.EmgChain < 0 || z.EmgDepth > EmgConst.MaxDepth || !(z.Amount > 0f))
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.Patches.Add(z);
                    }
                    return CombatLoadResult.Ok;
                }
                case EmgConst.SegFields:
                    if (n > EmgMatterConst.FieldCapacity)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var f = new EmgField();
                        if (sr.F()) f.Pos.x = r.ReadDouble();
                        if (sr.F()) f.Pos.y = r.ReadDouble();
                        if (sr.F()) f.Born = r.ReadDouble();
                        if (sr.F()) f.Until = r.ReadDouble();
                        if (sr.F()) f.Radius = r.ReadSingle();
                        if (sr.F()) f.Strength = r.ReadSingle();
                        if (sr.F()) f.Id = r.ReadInt32();
                        if (sr.F()) f.Owner = r.ReadInt32();
                        if (sr.F()) f.Chain = r.ReadInt32();
                        if (sr.F()) f.Port = r.ReadUInt16();
                        if (sr.F()) f.Faction = r.ReadByte();
                        if (sr.F()) f.Depth = r.ReadByte();
                        if (!sr.RecordOk || f.Id < 1 || !(f.Radius >= 0f) || f.Chain < 0)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.Fields.Add(f);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegProfileKeys:
                    if (n > 4096)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    staging.EmgProfileKeys.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        int key = 0;
                        if (sr.F()) key = r.ReadInt32();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgProfileKeys.Add(key);
                    }
                    return CombatLoadResult.Ok;
            }
            return CombatLoadResult.Ok;
        }

        /// <summary>
        /// 读档收尾（在换上当前表之前）：活斑块计数与斑块 / 场发号器；把旧档里按“写档时档案表”存的单位与残骸档案下标，按稳定键挪到当前表（DEBT-EMG1ENT01-07；
        /// 键在当前表里找不到的落回种类默认 −1）。旧档没有段 19（≤ 16 版或热更层没写键）时下标原样（与改动前一致）。最后换上当前表的键。
        /// </summary>
        private static void RemapProfiles(ref CombatData staging, NativeList<int> current)
        {
            EmgScalars es = staging.EmgS[0];
            es.PatchLive = staging.Patches.Length;
            for (int i = 0; i < staging.Patches.Length; i++)
            {
                es.NextPatchId = math.max(es.NextPatchId, staging.Patches[i].EmgId + 1);
            }
            // 短段（没写到 Id 的较早记录）按读入顺序补发 Id，接在已用最大号之后。
            for (int i = 0; i < staging.Patches.Length; i++)
            {
                if (staging.Patches[i].EmgId == 0)
                {
                    CombatZone z = staging.Patches[i];
                    z.EmgId = es.NextPatchId++;
                    staging.Patches[i] = z;
                }
            }
            for (int i = 0; i < staging.Fields.Length; i++)
            {
                es.NextFieldId = math.max(es.NextFieldId, staging.Fields[i].Id + 1);
            }
            es.HashDirty = 1;
            staging.EmgS[0] = es;
            int saved = staging.EmgProfileKeys.Length;
            if (saved > 0 && current.Length > 0)
            {
                var at = new Dictionary<int, short>(current.Length);
                for (int i = 0; i < current.Length; i++)
                {
                    at[current[i]] = (short)i;
                }
                bool same = saved == current.Length;
                for (int i = 0; same && i < saved; i++)
                {
                    same = staging.EmgProfileKeys[i] == current[i];
                }
                if (!same)
                {
                    for (int u = 0; u < staging.Count; u++)
                    {
                        short p = staging.EProfile[u];
                        if (p >= 0)
                        {
                            staging.EProfile[u] = p < saved && at.TryGetValue(staging.EmgProfileKeys[p], out short np) ? np : (short)-1;
                        }
                    }
                    for (int k = 0; k < staging.Wrecks.Length; k++)
                    {
                        EmgWreck w = staging.Wrecks[k];
                        short p = w.Profile;
                        if (p >= 0)
                        {
                            w.Profile = p < saved && at.TryGetValue(staging.EmgProfileKeys[p], out short np) ? np : (short)-1;
                            staging.Wrecks[k] = w;
                        }
                    }
                }
            }
            // 读进来的内核还没写键（热更层没写 / 自检的裸内核）：下标原样，键也沿用存档里的，否则再存一次就把段 19 丢了（重新序列化不再逐字节一致、涌现哈希变）。
            if (current.Length == 0)
            {
                return;
            }
            staging.EmgProfileKeys.Clear();
            for (int i = 0; i < current.Length; i++)
            {
                staging.EmgProfileKeys.Add(current[i]);
            }
        }
    }
}
