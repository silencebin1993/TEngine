using System;
using System.Collections.Generic;
using System.IO;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-KRN-01（FG19 FGR-EMG-045、061、202、211、213；ADR-EMG-001 ②④⑦⑨⑩）：内核门面的涌现部分——程序表登记（按完整字节去重、只追加）、
    /// 热更注入事件、残骸登记、每单位涌现段、状态哈希与快照格式 16 的 Emg 段。
    /// 内核只认整数码与值类型表，不认内容 ID（FGR-EMG-013）。执行（编译器 / 总线消费 / 触发器 / 动词 / 残骸生成）由后续 EMG1 卡接上。
    /// </summary>
    public sealed partial class CombatKernel
    {
        /// <summary>程序完整字节键 → 程序下标（派生量：不进快照，读档后按程序表懒重建）。</summary>
        private Dictionary<string, int> _programIndex;

        // ─────────────────────────────── 程序表 ───────────────────────────────

        public int ProgramCount => _d.EmgPrograms.Length;
        public int EmgNodeCount => _d.EmgNodes.Length;
        public int EmgInstrCount => _d.EmgInstrs.Length;
        public EmgScalars EmgScalars => _d.EmgS[0];

        /// <summary>
        /// 登记一个程序（ADR ②）：<paramref name="nodes"/> 的 FirstInstr 是相对 <paramref name="instrs"/> 的下标，<paramref name="program"/> 的 FirstNode / NodeCount / InstrTotal 由本方法填。
        /// 按完整字节去重（<see cref="ProgramKey"/>，与 <see cref="WeaponKey"/> 同一做法）：同样的程序返回已有下标；不同的追加到表尾——表只追加、不改写、不压实，
        /// 在飞载荷持有的旧下标一直有效（FGR-EMG-037）。形状不合法或表满（<see cref="EmgConst.MaxPrograms"/>）返回 −1，表不变。
        /// </summary>
        public int AddProgram(in EmgProgram program, IReadOnlyList<EmgNode> nodes, IReadOnlyList<EmgInstr> instrs)
        {
            if (!ProgramShapeValid(program, nodes, instrs))
            {
                return -1;
            }
            EnsureProgramIndex();
            string key = ProgramKey(program, nodes, instrs);
            if (_programIndex.TryGetValue(key, out int at))
            {
                return at;
            }
            if (_d.EmgPrograms.Length >= EmgConst.MaxPrograms)
            {
                return -1;
            }
            EmgProgram p = program;
            p.FirstNode = _d.EmgNodes.Length;
            p.NodeCount = (byte)nodes.Count;
            p.InstrTotal = (byte)instrs.Count;
            int instrBase = _d.EmgInstrs.Length;
            for (int j = 0; j < nodes.Count; j++)
            {
                EmgNode n = nodes[j];
                n.FirstInstr += instrBase;
                _d.EmgNodes.Add(n);
            }
            for (int j = 0; j < instrs.Count; j++)
            {
                _d.EmgInstrs.Add(instrs[j]);
            }
            int index = _d.EmgPrograms.Length;
            _d.EmgPrograms.Add(p);
            _programIndex[key] = index;
            Touch();
            return index;
        }

        public bool TryGetProgram(int index, out EmgProgram p)
        {
            bool ok = index >= 0 && index < _d.EmgPrograms.Length;
            p = ok ? _d.EmgPrograms[index] : default;
            return ok;
        }

        public bool TryGetEmgNode(int index, out EmgNode n)
        {
            bool ok = index >= 0 && index < _d.EmgNodes.Length;
            n = ok ? _d.EmgNodes[index] : default;
            return ok;
        }

        public bool TryGetEmgInstr(int index, out EmgInstr x)
        {
            bool ok = index >= 0 && index < _d.EmgInstrs.Length;
            x = ok ? _d.EmgInstrs[index] : default;
            return ok;
        }

        /// <summary>程序的完整字节键（FirstNode 归零、节点的 FirstInstr 取相对值，所以与它落在表里哪一行无关）。</summary>
        public static string ProgramKey(in EmgProgram program, IReadOnlyList<EmgNode> nodes, IReadOnlyList<EmgInstr> instrs)
        {
            using var ms = new MemoryStream(64 + nodes.Count * 16 + instrs.Count * 28);
            using var bw = new BinaryWriter(ms);
            EmgProgram p = program;
            p.FirstNode = 0;
            p.NodeCount = (byte)nodes.Count;
            p.InstrTotal = (byte)instrs.Count;
            WriteProgramRec(bw, p, null);
            for (int j = 0; j < nodes.Count; j++)
            {
                WriteNodeRec(bw, nodes[j], null);
            }
            for (int j = 0; j < instrs.Count; j++)
            {
                WriteInstrRec(bw, instrs[j], null);
            }
            bw.Flush();
            return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
        }

        /// <summary>程序形状：1～16 个节点、≤ EmgConst.MaxInstr（128）条指令；根节点 Parent = −1、Gen = 0；其余节点的父节点在它之前且 Gen = 父 Gen + 1 ≤ 2；
        /// 触发事件码 ≤ 29；节点按序拥有连续的指令段、恰好铺满指令列表（继承在编译期做完，每个节点自带完整指令，FGR-EMG-033）；数值有限。</summary>
        public static bool ProgramShapeValid(in EmgProgram program, IReadOnlyList<EmgNode> nodes, IReadOnlyList<EmgInstr> instrs)
        {
            if (nodes == null || instrs == null || nodes.Count < 1 || nodes.Count > EmgConst.MaxNodes || instrs.Count > EmgConst.MaxInstr
                || !Finite(program.HeatPerShot) || program.HeatPerShot < 0f)
            {
                return false;
            }
            int next = 0;
            for (int j = 0; j < nodes.Count; j++)
            {
                EmgNode n = nodes[j];
                bool shape = j == 0 ? n.Parent == -1 && n.Gen == 0 : n.Parent >= 0 && n.Parent < j && n.Gen == nodes[n.Parent].Gen + 1;
                if (!shape || n.Gen > EmgConst.MaxGen || (byte)n.TrigEvent > EmgConst.EventKinds || !Finite(n.TrigParam) || n.FirstInstr != next)
                {
                    return false;
                }
                next += n.InstrCount;
            }
            if (next != instrs.Count)
            {
                return false;
            }
            for (int j = 0; j < instrs.Count; j++)
            {
                EmgInstr x = instrs[j];
                if (!Finite(x.Amount) || !Finite(x.Param2) || !Finite(x.A) || !Finite(x.B) || !Finite(x.C))
                {
                    return false;
                }
            }
            return true;
        }

        private void EnsureProgramIndex()
        {
            if (_programIndex != null)
            {
                return;
            }
            _programIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _d.EmgPrograms.Length; i++)
            {
                ProgramLists(ref _d, i, out EmgProgram p, out List<EmgNode> nodes, out List<EmgInstr> instrs);
                _programIndex[ProgramKey(p, nodes, instrs)] = i; // 同键取后者不会发生：登记时已去重；读档来的表也逐行校验过
            }
        }

        /// <summary>把表里第 <paramref name="index"/> 个程序还原成“相对下标”的节点 / 指令列表（去重键与读档校验用）。</summary>
        private static void ProgramLists(ref CombatData d, int index, out EmgProgram p, out List<EmgNode> nodes, out List<EmgInstr> instrs)
        {
            p = d.EmgPrograms[index];
            nodes = new List<EmgNode>(p.NodeCount);
            instrs = new List<EmgInstr>(p.InstrTotal);
            // 节点按序铺满本程序的指令段（ProgramShapeValid），所以根节点的 FirstInstr 就是本程序指令段的起点。
            int baseInstr = p.NodeCount > 0 && p.FirstNode >= 0 && p.FirstNode < d.EmgNodes.Length ? d.EmgNodes[p.FirstNode].FirstInstr : 0;
            for (int j = 0; j < p.NodeCount && p.FirstNode + j < d.EmgNodes.Length; j++)
            {
                EmgNode n = d.EmgNodes[p.FirstNode + j];
                n.FirstInstr -= baseInstr;
                nodes.Add(n);
            }
            for (int k = 0; k < p.InstrTotal && baseInstr >= 0 && baseInstr + k < d.EmgInstrs.Length; k++)
            {
                instrs.Add(d.EmgInstrs[baseInstr + k]);
            }
        }

        /// <summary>武器表的程序下标要么 −1，要么指向已登记的程序，SpawnFlight 只能是 0 / 1；违反是调用方的编程错误（不悄悄改成“没有程序”）。
        /// 与读档用同一份校验（<see cref="WeaponEmgValid"/>）：写得进去的值存档后一定读得回来。</summary>
        private void RequireProgram(in CombatWeapon w)
        {
            if (!WeaponEmgValid(w, _d.EmgPrograms.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(w), $"武器的程序下标 {w.Program}（程序表 {_d.EmgPrograms.Length} 行）或 SpawnFlight {w.SpawnFlight} 不合法");
            }
        }

        /// <summary>调用方给的因果链只能是 0（没有）或已经发过的号：给一个没发过的号，读档会把发号器推到它之后，“连续跑”和“存读档后续跑”发出的链就分叉了（FGT-EMG-042）。</summary>
        private bool ChainIssued(int chain) => chain < _d.EmgS[0].NextChain;

        // ─────────────────────────────── 总线、残骸、单位段 ───────────────────────────────

        /// <summary>未处理的涌现事件条数。</summary>
        public int EmgBusPending => _d.EmgBusPending;

        /// <summary>第 <paramref name="i"/> 条未处理的事件（按 Seq 先后）。</summary>
        public bool TryGetEmgEvent(int i, out EmgEvent e)
        {
            int at = _d.EmgS[0].BusHead + i;
            bool ok = i >= 0 && at < _d.EmgBus.Length;
            e = ok ? _d.EmgBus[at] : default;
            return ok;
        }

        /// <summary>
        /// 热更层注入一条事件（ADR ④：evt.uplink / unlink / pickup / seize / release 在两步之间注入，O(1)）。内核发 Seq（总线自己的计数器）；
        /// Chain = 0 时分配一条新的因果链（ADR ⑨“热更注入分配一个”）。事件码不在 1～29、链深 &gt; 3、程序下标越界、节点不在该程序里、链为负或没发过、数值不是有限数、
        /// 队列已满（16384）时拒绝（返回 false，队列不变）。校验与读档共用 <see cref="EventValid"/>。队列的消费归 EMG1-BUS-01。
        /// </summary>
        public bool InjectEmgEvent(in EmgEvent e)
        {
            if (!EventValid(e, ref _d) || !ChainIssued(e.Chain) || _d.EmgBusPending >= EmgConst.BusCapacity)
            {
                return false;
            }
            EmgScalars es = _d.EmgS[0];
            EmgEvent x = e;
            x.Seq = es.EmgSeq;
            es.EmgSeq = unchecked(es.EmgSeq + 1);
            if (x.Chain == 0)
            {
                x.Chain = es.NextChain++;
            }
            _d.EmgS[0] = es;
            _d.EmgBus.Add(x);
            Touch();
            return true;
        }

        public int WreckCount => _d.Wrecks.Length;

        public bool TryGetWreck(int index, out EmgWreck w)
        {
            bool ok = index >= 0 && index < _d.Wrecks.Length;
            w = ok ? _d.Wrecks[index] : default;
            return ok;
        }

        /// <summary>登记一个残骸（发稳定 Id）。满了（<see cref="EmgConst.DefaultWreckCapacity"/>）、数值不合理（与读档同一份 <see cref="WreckValid"/>）或链没发过返回 false——
        /// 满了的“最早的崩解为碎屑”归 EMG1-ENT-01。</summary>
        public bool TryAddWreck(in EmgWreck wreck, out int id)
        {
            id = 0;
            EmgWreck w = wreck;
            w.Id = 1; // 只为过校验；真正的号下面发
            if (_d.Wrecks.Length >= EmgConst.DefaultWreckCapacity || !WreckValid(w) || !ChainIssued(w.Chain))
            {
                return false;
            }
            EmgScalars es = _d.EmgS[0];
            w.Id = id = es.NextWreckId++;
            _d.EmgS[0] = es;
            _d.Wrecks.Add(w);
            Touch();
            return true;
        }

        /// <summary>写单位的实体档案下标（−1 = 种类默认）与涌现状态基本段。不存在的单位、档案 &lt; −1、LastHitBy / LastHitChain 为负（与读档同一份 <see cref="UnitEmgValid"/>）、链没发过时拒绝。</summary>
        public bool SetUnitEmg(int unitId, short profile, in EmgUnitState state)
        {
            int i = _d.SlotOf(unitId);
            if (i < 0 || !UnitEmgValid(profile, state) || !ChainIssued(state.LastHitChain))
            {
                return false;
            }
            _d.EProfile[i] = profile;
            _d.EmgUnit[i] = state;
            Touch();
            return true;
        }

        public bool TryGetUnitEmg(int unitId, out short profile, out EmgUnitState state)
        {
            int i = _d.SlotOf(unitId);
            profile = i >= 0 ? _d.EProfile[i] : (short)-1;
            state = i >= 0 ? _d.EmgUnit[i] : default;
            return i >= 0;
        }

        // ─────────────────────────────── 哈希 ───────────────────────────────

        /// <summary>涌现状态的完整哈希（FNV-1a 64，覆盖格式 16 Emg 段的全部字节：发号器、三张表、武器程序、单位段、弹体 Id 与五元组、无人机 Id、未处理事件、残骸）。</summary>
        public ulong EmgStateHash()
        {
            using var ms = new MemoryStream(256 + _d.Projectiles.Length * 48);
            using var w = new BinaryWriter(ms);
            WriteEmg(w, 0, int.MaxValue, 0);
            w.Flush();
            ulong h = 14695981039346656037UL;
            byte[] buf = ms.GetBuffer();
            for (int i = 0; i < ms.Length; i++)
            {
                h ^= buf[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>有涌现状态（程序表 / 未处理事件 / 残骸 / 任一单位的涌现段不是缺省）时才把 <see cref="EmgStateHash"/> 混进 <see cref="StateHash"/>：
        /// 程序表为空的现有旅程哈希与格式 15 逐字相同（卡片“程序表为空时 StateHash 逐字不变”）；弹体 / 无人机的发号只是簿记，在这之前不影响任何结算。</summary>
        private void MixEmergence(ref ulong h)
        {
            if (EmgActive())
            {
                Mix(ref h, (long)EmgStateHash());
            }
        }

        private bool EmgActive()
        {
            if (_d.EmgPrograms.Length > 0 || _d.EmgBusPending > 0 || _d.Wrecks.Length > 0)
            {
                return true;
            }
            for (int i = 0; i < _d.Count; i++)
            {
                EmgUnitState u = _d.EmgUnit[i];
                if (_d.EProfile[i] != -1 || u.PrevMatterMask != 0u || u.LastHitBy != 0 || u.LastHitChain != 0)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 快照（格式 16 的 Emg 段）───────────────────────────────

        /// <summary>测试用：按格式 16 写快照，但段标记为 <paramref name="shortTag"/> 的 Emg 段每条记录只写前 <paramref name="fieldCap"/> 个字段（模拟本阶段较早提交写出的短段）；
        /// <paramref name="extraBytes"/> &gt; 0 时每段每条记录尾部多写这么多字节、字段数 +1，并多写一个不认识的段（模拟较晚提交写出的长段 / 新段）。</summary>
        public byte[] SerializeEmgShapeForTests(int shortTag, int fieldCap, int extraBytes) =>
            SerializeFormat(CombatConst.FormatVersion, shortTag, Math.Max(0, fieldCap), Math.Max(0, extraBytes));

        private const int UnknownSegmentTagForTests = 9999;

        private void WriteEmg(BinaryWriter w, int shortTag, int fieldCap, int extraBytes)
        {
            var sw = new EmgSegmentWriter(w, shortTag, fieldCap, extraBytes);
            w.Write(10 + (extraBytes > 0 ? 1 : 0));
            EmgScalars es = _d.EmgS[0];
            sw.Begin(EmgConst.SegScalars, 7, 1);
            if (sw.F()) w.Write(es.NextProjId);
            if (sw.F()) w.Write(es.NextDroneId);
            if (sw.F()) w.Write(es.NextWreckId);
            if (sw.F()) w.Write(es.NextPatchId);
            if (sw.F()) w.Write(es.NextFieldId);
            if (sw.F()) w.Write(es.NextChain);
            if (sw.F()) w.Write(es.EmgSeq);
            sw.EndRecord();
            sw.End();

            sw.Begin(EmgConst.SegPrograms, ProgramFields, _d.EmgPrograms.Length);
            for (int i = 0; i < _d.EmgPrograms.Length; i++)
            {
                WriteProgramRec(w, _d.EmgPrograms[i], sw);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegNodes, NodeFields, _d.EmgNodes.Length);
            for (int i = 0; i < _d.EmgNodes.Length; i++)
            {
                WriteNodeRec(w, _d.EmgNodes[i], sw);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegInstrs, InstrFields, _d.EmgInstrs.Length);
            for (int i = 0; i < _d.EmgInstrs.Length; i++)
            {
                WriteInstrRec(w, _d.EmgInstrs[i], sw);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegWeapons, WeaponFields, _d.Weapons.Length);
            for (int i = 0; i < _d.Weapons.Length; i++)
            {
                WriteWeaponEmg(w, _d.Weapons[i], sw);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegUnits, 4, _d.Count);
            for (int i = 0; i < _d.Count; i++)
            {
                EmgUnitState u = _d.EmgUnit[i];
                if (sw.F()) w.Write(_d.EProfile[i]);
                if (sw.F()) w.Write(u.PrevMatterMask);
                if (sw.F()) w.Write(u.LastHitBy);
                if (sw.F()) w.Write(u.LastHitChain);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegProjectiles, 15, _d.Projectiles.Length);
            for (int i = 0; i < _d.Projectiles.Length; i++)
            {
                CombatProjectile p = _d.Projectiles[i];
                if (sw.F()) w.Write(p.Id);
                if (sw.F()) w.Write(p.Prog);
                if (sw.F()) w.Write(p.Chain);
                if (sw.F()) w.Write(p.Coat);
                if (sw.F()) w.Write(p.PrevMatterMask);
                if (sw.F()) w.Write(p.TrigUsed);
                if (sw.F()) w.Write(p.HomingTarget);
                if (sw.F()) w.Write(p.Mass);
                if (sw.F()) w.Write(p.Age);
                if (sw.F()) w.Write(p.Node);
                if (sw.F()) w.Write(p.Gen);
                if (sw.F()) w.Write(p.PierceLeft);
                if (sw.F()) w.Write(p.BounceLeft);
                if (sw.F()) w.Write(p.Depth);
                if (sw.F()) w.Write((byte)p.Flags);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegDrones, 1, _d.Drones.Length);
            for (int i = 0; i < _d.Drones.Length; i++)
            {
                if (sw.F()) w.Write(_d.Drones[i].Id);
                sw.EndRecord();
            }
            sw.End();

            int head = es.BusHead;
            sw.Begin(EmgConst.SegBus, 16, _d.EmgBus.Length - head);
            for (int i = head; i < _d.EmgBus.Length; i++)
            {
                EmgEvent e = _d.EmgBus[i];
                if (sw.F()) w.Write((byte)e.Kind);
                if (sw.F()) w.Write(e.Depth);
                if (sw.F()) w.Write((byte)e.EntKind);
                if (sw.F()) w.Write((byte)e.OtherKind);
                if (sw.F()) w.Write(e.Code);
                if (sw.F()) w.Write(e.Code2);
                if (sw.F()) w.Write(e.Ent);
                if (sw.F()) w.Write(e.Other);
                if (sw.F()) w.Write(e.Prog);
                if (sw.F()) w.Write(e.Chain);
                if (sw.F()) w.Write(e.Seq);
                if (sw.F()) w.Write(e.Node);
                if (sw.F()) w.Write(e.Value);
                if (sw.F()) w.Write(e.Value2);
                if (sw.F()) w.Write(e.Pos.x);
                if (sw.F()) w.Write(e.Pos.y);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegWrecks, 20, _d.Wrecks.Length);
            for (int i = 0; i < _d.Wrecks.Length; i++)
            {
                EmgWreck k = _d.Wrecks[i];
                if (sw.F()) w.Write(k.Pos.x);
                if (sw.F()) w.Write(k.Pos.y);
                if (sw.F()) w.Write(k.Born);
                if (sw.F()) w.Write(k.Until);
                if (sw.F()) w.Write(k.DwellSince);
                if (sw.F()) w.Write(k.Radius);
                if (sw.F()) w.Write(k.Hp);
                if (sw.F()) w.Write(k.MaxHp);
                if (sw.F()) w.Write(k.Mass);
                if (sw.F()) w.Write(k.Temp);
                if (sw.F()) w.Write(k.Charge);
                if (sw.F()) w.Write(k.Id);
                if (sw.F()) w.Write(k.Chain);
                if (sw.F()) w.Write(k.SrcExtKey);
                if (sw.F()) w.Write(k.Coat);
                if (sw.F()) w.Write(k.PrevMatterMask);
                if (sw.F()) w.Write(k.Profile);
                if (sw.F()) w.Write(k.SrcKind);
                if (sw.F()) w.Write(k.DwellMatter);
                if (sw.F()) w.Write(k.NoSalvage);
                sw.EndRecord();
            }
            sw.End();

            if (extraBytes > 0)
            {
                sw.Begin(UnknownSegmentTagForTests, 1, 1);
                if (sw.F()) w.Write(0x5A5A5A5A);
                sw.EndRecord();
                sw.End();
            }
        }

        private const int ProgramFields = 6;
        private const int NodeFields = 10;
        private const int InstrFields = 11;
        private const int WeaponFields = 2;

        private static bool Put(EmgSegmentWriter sw) => sw == null || sw.F();

        private static void WriteProgramRec(BinaryWriter w, in EmgProgram p, EmgSegmentWriter sw)
        {
            if (Put(sw)) w.Write(p.FirstNode);
            if (Put(sw)) w.Write(p.NodeCount);
            if (Put(sw)) w.Write(p.InstrTotal);
            if (Put(sw)) w.Write((ushort)p.Flags);
            if (Put(sw)) w.Write(p.ListenMask);
            if (Put(sw)) w.Write(p.HeatPerShot);
        }

        private static void WriteNodeRec(BinaryWriter w, in EmgNode n, EmgSegmentWriter sw)
        {
            if (Put(sw)) w.Write(n.Parent);
            if (Put(sw)) w.Write(n.Gen);
            if (Put(sw)) w.Write((byte)n.TrigEvent);
            if (Put(sw)) w.Write(n.TrigCount);
            if (Put(sw)) w.Write(n.TrigParam);
            if (Put(sw)) w.Write(n.FirstInstr);
            if (Put(sw)) w.Write(n.SubForm);
            if (Put(sw)) w.Write(n.InstrCount);
            if (Put(sw)) w.Write(n.InheritMask);
            if (Put(sw)) w.Write(n.MetaMask);
        }

        private static void WriteInstrRec(BinaryWriter w, in EmgInstr x, EmgSegmentWriter sw)
        {
            if (Put(sw)) w.Write(x.Port);
            if (Put(sw)) w.Write(x.Role);
            if (Put(sw)) w.Write(x.AnchorKind);
            if (Put(sw)) w.Write(x.Verb);
            if (Put(sw)) w.Write(x.Ordinal);
            if (Put(sw)) w.Write((ushort)x.Flags);
            if (Put(sw)) w.Write(x.Amount);
            if (Put(sw)) w.Write(x.Param2);
            if (Put(sw)) w.Write(x.A);
            if (Put(sw)) w.Write(x.B);
            if (Put(sw)) w.Write(x.C);
        }

        /// <summary>武器的涌现字段（快照段与 <see cref="WeaponKey"/> 共用同一份字段清单，新增字段两边同时生效，去重不会把不同程序的武器并成一行）。</summary>
        private static void WriteWeaponEmg(BinaryWriter w, in CombatWeapon x, EmgSegmentWriter sw)
        {
            if (Put(sw)) w.Write(x.Program);
            if (Put(sw)) w.Write(x.SpawnFlight);
        }

        /// <summary>读档中转：Emg 段在读法表（无人机）之前，无人机 Id 等读法表读完再落。</summary>
        private sealed class EmgLoadBuffer
        {
            public int[] DroneIds;
            public bool HadSeq;
        }

        private static CombatLoadResult ParseEmg(BinaryReader r, ref CombatData staging, int bodyLen, out EmgLoadBuffer buf)
        {
            buf = new EmgLoadBuffer();
            int nseg = r.ReadInt32();
            if (nseg < 0 || nseg > EmgConst.MaxSegments)
            {
                return CombatLoadResult.InvalidValue;
            }
            var seen = new HashSet<int>();
            var sr = new EmgSegmentReader(r);
            for (int k = 0; k < nseg; k++)
            {
                CombatLoadResult hr = sr.Next(bodyLen);
                if (hr != CombatLoadResult.Ok)
                {
                    return hr;
                }
                if (!seen.Add(sr.Tag))
                {
                    return CombatLoadResult.InvalidValue; // 同一段出现两次
                }
                CombatLoadResult res = ParseSegment(r, sr, ref staging, buf);
                if (res != CombatLoadResult.Ok)
                {
                    return res;
                }
                sr.Skip(); // 读完已知字段后跳到段尾（长段的未知尾部 / 不认识的段整段跳过）
            }
            return CombatLoadResult.Ok;
        }

        private static CombatLoadResult ParseSegment(BinaryReader r, EmgSegmentReader sr, ref CombatData staging, EmgLoadBuffer buf)
        {
            int n = sr.Count;
            switch (sr.Tag)
            {
                case EmgConst.SegScalars:
                {
                    if (n != 1)
                    {
                        return n == 0 ? CombatLoadResult.Ok : CombatLoadResult.InvalidValue;
                    }
                    sr.Record(0);
                    EmgScalars es = EmgScalars.Initial;
                    if (sr.F()) es.NextProjId = r.ReadInt32();
                    if (sr.F()) es.NextDroneId = r.ReadInt32();
                    if (sr.F()) es.NextWreckId = r.ReadInt32();
                    if (sr.F()) es.NextPatchId = r.ReadInt32();
                    if (sr.F()) es.NextFieldId = r.ReadInt32();
                    if (sr.F()) es.NextChain = r.ReadInt32();
                    buf.HadSeq = sr.F();
                    if (buf.HadSeq) es.EmgSeq = r.ReadInt32();
                    if (!sr.RecordOk || es.NextProjId < 1 || es.NextDroneId < 1 || es.NextWreckId < 1 || es.NextPatchId < 1 || es.NextFieldId < 1 || es.NextChain < 1)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    es.BusHead = 0;
                    staging.EmgS[0] = es;
                    return CombatLoadResult.Ok;
                }
                case EmgConst.SegPrograms:
                    if (n > EmgConst.MaxPrograms)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var p = new EmgProgram();
                        if (sr.F()) p.FirstNode = r.ReadInt32();
                        if (sr.F()) p.NodeCount = r.ReadByte();
                        if (sr.F()) p.InstrTotal = r.ReadByte();
                        if (sr.F()) p.Flags = (EmgProgramFlags)r.ReadUInt16();
                        if (sr.F()) p.ListenMask = r.ReadUInt32();
                        if (sr.F()) p.HeatPerShot = r.ReadSingle();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgPrograms.Add(p);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegNodes:
                    if (n > EmgConst.MaxPrograms * EmgConst.MaxNodes)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var nd = new EmgNode { Parent = -1 };
                        if (sr.F()) nd.Parent = r.ReadSByte();
                        if (sr.F()) nd.Gen = r.ReadByte();
                        if (sr.F()) nd.TrigEvent = (EmgEventKind)r.ReadByte();
                        if (sr.F()) nd.TrigCount = r.ReadByte();
                        if (sr.F()) nd.TrigParam = r.ReadSingle();
                        if (sr.F()) nd.FirstInstr = r.ReadInt32();
                        if (sr.F()) nd.SubForm = r.ReadByte();
                        if (sr.F()) nd.InstrCount = r.ReadByte();
                        if (sr.F()) nd.InheritMask = r.ReadByte();
                        if (sr.F()) nd.MetaMask = r.ReadByte();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgNodes.Add(nd);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegInstrs:
                    if (n > EmgConst.MaxPrograms * EmgConst.MaxInstr)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var x = new EmgInstr();
                        if (sr.F()) x.Port = r.ReadUInt16();
                        if (sr.F()) x.Role = r.ReadByte();
                        if (sr.F()) x.AnchorKind = r.ReadByte();
                        if (sr.F()) x.Verb = r.ReadByte();
                        if (sr.F()) x.Ordinal = r.ReadByte();
                        if (sr.F()) x.Flags = (EmgInstrFlags)r.ReadUInt16();
                        if (sr.F()) x.Amount = r.ReadSingle();
                        if (sr.F()) x.Param2 = r.ReadSingle();
                        if (sr.F()) x.A = r.ReadSingle();
                        if (sr.F()) x.B = r.ReadSingle();
                        if (sr.F()) x.C = r.ReadSingle();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgInstrs.Add(x);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegWeapons:
                    if (n != staging.Weapons.Length)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        CombatWeapon cw = staging.Weapons[i];
                        int prog = cw.Program;
                        if (sr.F()) prog = r.ReadInt32();
                        if (sr.F()) cw.SpawnFlight = r.ReadByte();
                        if (!sr.RecordOk || prog < -1) // 先判再赋：Program 的 setter 会把任何负数折成“没有程序”，坏值得在这里拒绝
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        cw.Program = prog;
                        staging.Weapons[i] = cw; // 程序下标是否在表内、SpawnFlight 是否 0 / 1 在 FinishEmgLoad 用 WeaponEmgValid 判（程序表此时可能还没读）
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegUnits:
                    if (n != staging.Count)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        short prof = -1;
                        EmgUnitState u = default;
                        if (sr.F()) prof = r.ReadInt16();
                        if (sr.F()) u.PrevMatterMask = r.ReadUInt32();
                        if (sr.F()) u.LastHitBy = r.ReadInt32();
                        if (sr.F()) u.LastHitChain = r.ReadInt32();
                        if (!sr.RecordOk || !UnitEmgValid(prof, u))
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EProfile[i] = prof;
                        staging.EmgUnit[i] = u;
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegProjectiles:
                    if (n != staging.Projectiles.Length)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        CombatProjectile p = staging.Projectiles[i];
                        if (sr.F()) p.Id = r.ReadInt32();
                        if (sr.F()) p.Prog = r.ReadInt32();
                        if (sr.F()) p.Chain = r.ReadInt32();
                        if (sr.F()) p.Coat = r.ReadUInt32();
                        if (sr.F()) p.PrevMatterMask = r.ReadUInt32();
                        if (sr.F()) p.TrigUsed = r.ReadUInt32();
                        if (sr.F()) p.HomingTarget = r.ReadInt32();
                        if (sr.F()) p.Mass = r.ReadSingle();
                        if (sr.F()) p.Age = r.ReadSingle();
                        if (sr.F()) p.Node = r.ReadByte();
                        if (sr.F()) p.Gen = r.ReadByte();
                        if (sr.F()) p.PierceLeft = r.ReadByte();
                        if (sr.F()) p.BounceLeft = r.ReadByte();
                        if (sr.F()) p.Depth = r.ReadByte();
                        if (sr.F()) p.Flags = (EmgProjFlags)r.ReadByte();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.Projectiles[i] = p;
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegDrones:
                    if (n > 1 << 20)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    buf.DroneIds = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        if (sr.F()) buf.DroneIds[i] = r.ReadInt32();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegBus:
                    if (n > EmgConst.BusCapacity)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var e = new EmgEvent { Prog = -1 };
                        if (sr.F()) e.Kind = (EmgEventKind)r.ReadByte();
                        if (sr.F()) e.Depth = r.ReadByte();
                        if (sr.F()) e.EntKind = (EmgEntKind)r.ReadByte();
                        if (sr.F()) e.OtherKind = (EmgEntKind)r.ReadByte();
                        if (sr.F()) e.Code = r.ReadUInt16();
                        if (sr.F()) e.Code2 = r.ReadUInt16();
                        if (sr.F()) e.Ent = r.ReadInt32();
                        if (sr.F()) e.Other = r.ReadInt32();
                        if (sr.F()) e.Prog = r.ReadInt32();
                        if (sr.F()) e.Chain = r.ReadInt32();
                        if (sr.F()) e.Seq = r.ReadInt32();
                        if (sr.F()) e.Node = r.ReadByte();
                        if (sr.F()) e.Value = r.ReadSingle();
                        if (sr.F()) e.Value2 = r.ReadSingle();
                        if (sr.F()) e.Pos.x = r.ReadDouble();
                        if (sr.F()) e.Pos.y = r.ReadDouble();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.EmgBus.Add(e);
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegWrecks:
                    if (n > EmgConst.DefaultWreckCapacity)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var k = new EmgWreck(); // 结构默认 Profile 即 −1（种类默认）
                        if (sr.F()) k.Pos.x = r.ReadDouble();
                        if (sr.F()) k.Pos.y = r.ReadDouble();
                        if (sr.F()) k.Born = r.ReadDouble();
                        if (sr.F()) k.Until = r.ReadDouble();
                        if (sr.F()) k.DwellSince = r.ReadDouble();
                        if (sr.F()) k.Radius = r.ReadSingle();
                        if (sr.F()) k.Hp = r.ReadSingle();
                        if (sr.F()) k.MaxHp = r.ReadSingle();
                        if (sr.F()) k.Mass = r.ReadSingle();
                        if (sr.F()) k.Temp = r.ReadSingle();
                        if (sr.F()) k.Charge = r.ReadSingle();
                        if (sr.F()) k.Id = r.ReadInt32();
                        if (sr.F()) k.Chain = r.ReadInt32();
                        if (sr.F()) k.SrcExtKey = r.ReadInt32();
                        if (sr.F()) k.Coat = r.ReadUInt32();
                        if (sr.F()) k.PrevMatterMask = r.ReadUInt32();
                        if (sr.F()) k.Profile = r.ReadInt16();
                        if (sr.F()) k.SrcKind = r.ReadByte();
                        if (sr.F()) k.DwellMatter = r.ReadByte();
                        if (sr.F()) k.NoSalvage = r.ReadByte();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        staging.Wrecks.Add(k);
                    }
                    return CombatLoadResult.Ok;
                default:
                    return CombatLoadResult.Ok; // 不认识的段（较晚提交新增的）：整段跳过
            }
        }

        /// <summary>
        /// 读档收尾（任何格式都走）：落无人机 Id；校验交叉引用（武器 / 弹体 / 事件的程序下标、节点、代、链深、Id 唯一）；
        /// 缺省补发——没有 Id 的弹体 / 无人机按读入顺序补发，发号器取“已用最大号 + 1”与原值的较大者（ADR ⑩：旧弹体 Prog = −1、0 代）。
        /// </summary>
        private static CombatLoadResult FinishEmgLoad(ref CombatData staging, EmgLoadBuffer buf)
        {
            int programs = staging.EmgPrograms.Length;
            for (int i = 0; i < programs; i++)
            {
                EmgProgram p = staging.EmgPrograms[i];
                if (p.FirstNode < 0 || p.NodeCount < 1 || p.FirstNode + p.NodeCount > staging.EmgNodes.Length)
                {
                    return CombatLoadResult.InvalidValue;
                }
                ProgramLists(ref staging, i, out EmgProgram rp, out List<EmgNode> nodes, out List<EmgInstr> instrs);
                if (!ProgramShapeValid(rp, nodes, instrs) || rp.InstrTotal != instrs.Count)
                {
                    return CombatLoadResult.InvalidValue;
                }
            }
            for (int i = 0; i < staging.Weapons.Length; i++)
            {
                if (!WeaponEmgValid(staging.Weapons[i], programs))
                {
                    return CombatLoadResult.InvalidValue;
                }
            }
            EmgScalars es = staging.EmgS[0];
            int maxChain = 0;
            var ids = new HashSet<int>();
            int maxId = 0;
            for (int i = 0; i < staging.Projectiles.Length; i++)
            {
                CombatProjectile p = staging.Projectiles[i];
                bool progOk = p.Prog == -1 || (p.Prog >= 0 && p.Prog < programs && p.Node < staging.EmgPrograms[p.Prog].NodeCount);
                if (!progOk || p.Id < 0 || (p.Id > 0 && !ids.Add(p.Id)) || p.Gen > EmgConst.MaxGen || p.Depth > EmgConst.MaxDepth || p.Chain < 0
                    || !Finite(p.Mass) || !Finite(p.Age) || p.HomingTarget < 0)
                {
                    return CombatLoadResult.InvalidValue;
                }
                maxId = Math.Max(maxId, p.Id);
                maxChain = Math.Max(maxChain, p.Chain);
            }
            es.NextProjId = Math.Max(es.NextProjId, maxId + 1);
            for (int i = 0; i < staging.Projectiles.Length; i++)
            {
                CombatProjectile p = staging.Projectiles[i];
                if (p.Id == 0)
                {
                    p.Id = es.NextProjId++;
                    staging.Projectiles[i] = p;
                }
            }

            if (buf?.DroneIds != null && buf.DroneIds.Length != staging.Drones.Length)
            {
                return CombatLoadResult.InvalidValue;
            }
            ids.Clear();
            maxId = 0;
            for (int i = 0; i < staging.Drones.Length; i++)
            {
                CombatDrone dr = staging.Drones[i];
                dr.Id = buf?.DroneIds != null ? buf.DroneIds[i] : 0;
                if (dr.Id < 0 || (dr.Id > 0 && !ids.Add(dr.Id)))
                {
                    return CombatLoadResult.InvalidValue;
                }
                maxId = Math.Max(maxId, dr.Id);
                staging.Drones[i] = dr;
            }
            es.NextDroneId = Math.Max(es.NextDroneId, maxId + 1);
            for (int i = 0; i < staging.Drones.Length; i++)
            {
                CombatDrone dr = staging.Drones[i];
                if (dr.Id == 0)
                {
                    dr.Id = es.NextDroneId++;
                    staging.Drones[i] = dr;
                }
            }

            for (int i = 0; i < staging.EmgBus.Length; i++)
            {
                EmgEvent e = staging.EmgBus[i];
                if (!EventValid(e, ref staging))
                {
                    return CombatLoadResult.InvalidValue;
                }
                maxChain = Math.Max(maxChain, e.Chain);
            }
            if (staging.EmgBus.Length > 0 && (buf == null || !buf.HadSeq))
            {
                es.EmgSeq = unchecked(staging.EmgBus[staging.EmgBus.Length - 1].Seq + 1);
            }

            ids.Clear();
            maxId = 0;
            for (int i = 0; i < staging.Wrecks.Length; i++)
            {
                EmgWreck k = staging.Wrecks[i];
                if (!WreckValid(k) || !ids.Add(k.Id))
                {
                    return CombatLoadResult.InvalidValue;
                }
                maxId = Math.Max(maxId, k.Id);
                maxChain = Math.Max(maxChain, k.Chain);
            }
            es.NextWreckId = Math.Max(es.NextWreckId, maxId + 1);
            for (int i = 0; i < staging.Count; i++)
            {
                maxChain = Math.Max(maxChain, staging.EmgUnit[i].LastHitChain);
            }
            es.NextChain = Math.Max(es.NextChain, maxChain + 1);
            es.BusHead = 0;
            staging.EmgS[0] = es;
            return CombatLoadResult.Ok;
        }

        // ── 写入口与读档共用的校验：写得进去的值存档后一定读得回来（审查 P1：三个写入口曾比读档宽）──

        /// <summary>事件：码 1～29、链深 ≤ 3、实体种类合法、程序下标 −1 或在表内、有程序时节点在该程序的节点数内（BUS-01 按 FirstNode + Node 查节点表）、链非负、数值有限。</summary>
        private static bool EventValid(in EmgEvent e, ref CombatData d) =>
            e.Kind >= EmgEventKind.Fire && (byte)e.Kind <= EmgConst.EventKinds && e.Depth <= EmgConst.MaxDepth
            && (byte)e.EntKind <= (byte)EmgEntKind.Field && (byte)e.OtherKind <= (byte)EmgEntKind.Field
            && e.Prog >= -1 && e.Prog < d.EmgPrograms.Length && (e.Prog < 0 || e.Node < d.EmgPrograms[e.Prog].NodeCount)
            && e.Chain >= 0 && Finite(e.Value) && Finite(e.Value2) && IsFinite(e.Pos);

        private static bool WeaponEmgValid(in CombatWeapon w, int programs) => w.ProgramRef >= 0 && w.ProgramRef <= programs && w.SpawnFlight <= 1;

        private static bool UnitEmgValid(short profile, in EmgUnitState u) => profile >= -1 && u.LastHitBy >= 0 && u.LastHitChain >= 0;

        private static bool WreckValid(in EmgWreck k) =>
            k.Id > 0 && IsFinite(k.Pos) && !double.IsNaN(k.Born) && !double.IsNaN(k.Until) && !double.IsNaN(k.DwellSince)
            && Finite(k.Radius) && k.Radius >= 0f && Finite(k.MaxHp) && k.MaxHp >= 0f && Finite(k.Hp) && k.Hp >= 0f && k.Hp <= k.MaxHp
            && Finite(k.Mass) && k.Mass >= 0f && Finite(k.Temp) && Finite(k.Charge) && k.Chain >= 0 && k.Profile >= -1 && k.NoSalvage <= 1;

    }

    /// <summary>
    /// 快照 Emg 段的自描述写入器（ADR-EMG-001 ⑩；EMG2-MAT-01、EMG3-CHS-01、EMG4-HIJ-01 复用）。一段 = 段标记（int）+ 段长度（int，其后的字节数）+ 字段数（ushort）+ 记录数（int）+ 定长记录。
    /// 同一 FormatVersion 跨阶段内多次提交：新增字段只追加到记录尾部，已提交字段的类型、顺序、语义不再改（要改就升格式）。
    /// 每条记录逐字段 <c>if (sw.F()) w.Write(...)</c>，最后 <see cref="EndRecord"/>；记录必须定长（只写定长基本类型）。
    /// </summary>
    public sealed class EmgSegmentWriter
    {
        private readonly BinaryWriter _w;
        private readonly int _shortTag;
        private readonly int _cap;
        private readonly int _extra;
        private long _lenAt;
        private int _fields;
        private int _k;

        /// <param name="shortTag">只对这一段生效 <paramref name="fieldCap"/>（0 = 不截）。</param>
        /// <param name="fieldCap">该段每条记录最多写几个字段（测试模拟较早提交的短段；正常 = int.MaxValue）。</param>
        /// <param name="extraBytes">每段每条记录尾部多写几个字节、字段数 +1（测试模拟较晚提交的长段；正常 = 0）。</param>
        public EmgSegmentWriter(BinaryWriter w, int shortTag = 0, int fieldCap = int.MaxValue, int extraBytes = 0)
        {
            _w = w;
            _shortTag = shortTag;
            _cap = Math.Max(0, fieldCap);
            _extra = Math.Max(0, extraBytes);
        }

        public void Begin(int tag, int fields, int count)
        {
            _fields = tag == _shortTag ? Math.Min(fields, _cap) : fields;
            _w.Write(tag);
            _lenAt = _w.BaseStream.Position;
            _w.Write(0);
            _w.Write((ushort)(_fields + (_extra > 0 ? 1 : 0)));
            _w.Write(count);
            _k = 0;
        }

        /// <summary>这条记录的下一个字段要不要写。</summary>
        public bool F() => _k++ < _fields;

        public void EndRecord()
        {
            for (int i = 0; i < _extra; i++)
            {
                _w.Write((byte)0xA5);
            }
            _k = 0;
        }

        public void End()
        {
            long end = _w.BaseStream.Position;
            _w.BaseStream.Position = _lenAt;
            _w.Write(checked((int)(end - _lenAt - 4)));
            _w.BaseStream.Position = end;
        }
    }

    /// <summary>
    /// 快照 Emg 段的自描述读取器（与 <see cref="EmgSegmentWriter"/> 成对）：短段（字段数少）缺的字段取缺省；长段（字段数多）按记录长度跳过未知尾部；不认识的段按段长度整段跳过。
    /// 用法：<c>Next</c> → 逐条 <c>Record(i)</c> → <c>x = sr.F() ? r.ReadXxx() : 缺省</c> → 查 <c>RecordOk</c> → <c>Skip()</c>。
    /// </summary>
    public sealed class EmgSegmentReader
    {
        private readonly BinaryReader _r;
        private long _payload;
        private long _end;
        private long _recEnd;
        private int _stride;
        private int _k;

        public int Tag { get; private set; }
        public int Fields { get; private set; }
        public int Count { get; private set; }

        public EmgSegmentReader(BinaryReader r)
        {
            _r = r;
        }

        /// <summary>读下一段的段头。<paramref name="limit"/> = 快照正文的结尾位置（段越过它 = 截断）。</summary>
        public CombatLoadResult Next(long limit)
        {
            Tag = _r.ReadInt32();
            int len = _r.ReadInt32();
            long start = _r.BaseStream.Position;
            if (len < 6)
            {
                return CombatLoadResult.InvalidValue;
            }
            if (start + len > limit)
            {
                return CombatLoadResult.Truncated;
            }
            Fields = _r.ReadUInt16();
            Count = _r.ReadInt32();
            _payload = _r.BaseStream.Position;
            _end = start + len;
            long body = _end - _payload;
            if (Count < 0 || (Count == 0 && body != 0) || (Count > 0 && body % Count != 0))
            {
                return CombatLoadResult.InvalidValue;
            }
            _stride = Count == 0 ? 0 : (int)(body / Count);
            return CombatLoadResult.Ok;
        }

        public void Record(int i)
        {
            _r.BaseStream.Position = _payload + (long)i * _stride;
            _recEnd = _r.BaseStream.Position + _stride;
            _k = 0;
        }

        /// <summary>这条记录里有没有下一个字段（没有 = 短段，取缺省）。</summary>
        public bool F() => _k++ < Fields;

        /// <summary>已知字段没有读出记录边界（字段数虚报时整份拒绝）。</summary>
        public bool RecordOk => _r.BaseStream.Position <= _recEnd;

        public void Skip() => _r.BaseStream.Position = _end;
    }
}
