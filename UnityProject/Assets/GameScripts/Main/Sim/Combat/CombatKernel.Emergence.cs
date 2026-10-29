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
            _d.EmgListen |= program.ListenMask; // EMG1-BUS-01（FGR-EMG-062）：地点级监听掩码在程序表变化时并上，不按帧
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

        // ─────────────────────────────── EMG1-FLASH-01 刷写带宽（FGR-EMG-036 / 037；ADR ③）───────────────────────────────

        /// <summary>
        /// 给一把会放无人机 / 哨戒桩的武器挂刷写变体表（热更层桥接在武器结算时登记）：<paramref name="full"/>[k] = 只刷前 k 条（全长列表）时无人机开火用的武器下标，
        /// <paramref name="soft"/>[k] = 剥离核心后只剩前 k 条（去核心列表）时的武器，<paramref name="strip"/>[k] = 全长列表上持有 k 条的无人机剥离核心后在去核心列表上持有几条。
        /// full[L0] 应当是武器自己。变体武器的程序与读法都已按截断重算（热更层），内核只按条数查表，不认内容 ID（FGR-EMG-013）。
        /// 同一把武器已登记过（武器按完整字节去重，同一武器 = 同一程序 = 同一变体表）时不改、返回 true。参数不合法返回 false、表不变。O(L)。
        /// </summary>
        public bool SetFlashVariants(int weapon, int[] full, int[] soft, int[] strip)
        {
            if (weapon < 0 || weapon >= _d.Weapons.Length || full == null || soft == null || strip == null
                || full.Length < 1 || full.Length > EmgConst.MaxFlashLen + 1 || soft.Length < 1 || soft.Length > full.Length || strip.Length != full.Length)
            {
                return false;
            }
            if (weapon < _d.FlashHead.Length && _d.FlashHead[weapon].x >= 0)
            {
                return true;
            }
            int l0 = full.Length - 1, l1 = soft.Length - 1;
            for (int k = 0; k <= l0; k++)
            {
                if (full[k] < 0 || full[k] >= _d.Weapons.Length || strip[k] < 0 || strip[k] > l1)
                {
                    return false;
                }
            }
            for (int k = 0; k <= l1; k++)
            {
                if (soft[k] < 0 || soft[k] >= _d.Weapons.Length)
                {
                    return false;
                }
            }
            while (_d.FlashHead.Length <= weapon)
            {
                _d.FlashHead.Add(new Unity.Mathematics.int4(-1, 0, 0, 0));
            }
            _d.FlashHead[weapon] = new Unity.Mathematics.int4(_d.FlashMap.Length, l0, l1, 0);
            foreach (int x in full) _d.FlashMap.Add(x);
            foreach (int x in soft) _d.FlashMap.Add(x);
            foreach (int x in strip) _d.FlashMap.Add(x);
            Touch();
            return true;
        }

        /// <summary>这把武器的刷写变体表表头（x &lt; 0 = 没有；y = 全长 L0，z = 去核心长度 L1）。</summary>
        public Unity.Mathematics.int4 FlashHeadOf(int weapon) => CombatLogic.FlashHeadOf(ref _d, weapon);

        /// <summary>变体表的一格：<paramref name="k"/> 条（<paramref name="noCore"/> = 去核心列表）时的执行武器；没有表或越界返回 −1。</summary>
        public int FlashVariantOf(int weapon, int k, bool noCore)
        {
            Unity.Mathematics.int4 h = CombatLogic.FlashHeadOf(ref _d, weapon);
            int len = noCore ? h.z : h.y;
            return h.x < 0 || k < 0 || k > len ? -1 : _d.FlashMap[h.x + (noCore ? h.y + 1 : 0) + k];
        }

        /// <summary>剥离换算：全长列表上持有 <paramref name="k"/> 条的实体剥离核心后持有几条；没有表或越界返回 −1。</summary>
        public int FlashStripOf(int weapon, int k)
        {
            Unity.Mathematics.int4 h = CombatLogic.FlashHeadOf(ref _d, weapon);
            return h.x < 0 || k < 0 || k > h.y ? -1 : _d.FlashMap[h.x + h.y + 1 + h.z + 1 + k];
        }

        /// <summary>每台母机的刷写带宽（条，配置值；≤ 0 取初值 8）。</summary>
        public int FlashBandwidth => CombatLogic.FlashBandwidthOf(ref _d);

        /// <summary>这台母机（单位 ID）此刻的刷写占用 = 它存活的无人机 / 哨戒桩各自持有的指令条数之和（每步由存活实体派生，不另存计数）。O(无人机数)。
        /// FG11-FAC-01 的镜像复制品、EMG4 的被劫持敌机按同一口径汇总到各自母机。</summary>
        public int FlashUsed(int ownerId) => CombatLogic.FlashUsedBy(ref _d, ownerId, -1);

        /// <summary>信号离开母机 <paramref name="ownerId"/>（单位 ID）：它已放出的无人机 / 哨戒桩剥离核心指令、不再续命（FGR-EMG-037）。返回处理了几架。O(无人机数)。
        /// 机器经 evt.unlink 注入调用；炮塔接入离开（TurretUplink）没有 evt.unlink，直接调这里。</summary>
        public int FlashStrip(int ownerId)
        {
            int n = CombatLogic.EmgFlashStrip(ref _d, ownerId);
            if (n > 0)
            {
                Touch();
            }
            return n;
        }

        /// <summary>稳定 Id → 无人机下标（没有 −1）。O(无人机数)，只在悬停刷新 / 自检时调用。</summary>
        public int DroneIndexOf(int droneId)
        {
            for (int q = 0; q < _d.Drones.Length; q++)
            {
                if (_d.Drones[q].Id == droneId)
                {
                    return q;
                }
            }
            return -1;
        }

        /// <summary>一架无人机此刻的刷写读数（悬停 / 自检）。没有这架或它的武器没有变体表时返回 false。</summary>
        public bool TryGetDroneFlash(int index, out CombatDroneFlash f)
        {
            f = default;
            if (index < 0 || index >= _d.Drones.Length)
            {
                return false;
            }
            CombatDrone dr = _d.Drones[index];
            Unity.Mathematics.int4 h = CombatLogic.FlashHeadOf(ref _d, dr.Weapon);
            if (h.x < 0)
            {
                return false;
            }
            int len = dr.Strip != 0 ? h.z : h.y;
            int held = Math.Min(dr.Active, len);
            int exec = CombatLogic.FlashExecWeapon(ref _d, dr);
            f = new CombatDroneFlash
            {
                Id = dr.Id, Owner = dr.Owner, Weapon = dr.Weapon, ExecWeapon = exec, Program = _d.Weapons[exec].Program,
                Held = held, Length = len, FullLength = h.y, Pause = Math.Min(dr.Pause, held), Stripped = dr.Strip != 0, Anchored = dr.Anchored != 0,
            };
            return true;
        }

        /// <summary>光标拾取：离 <paramref name="at"/> 最近、在 <paramref name="radius"/> 米内、持有刷写程序（武器有变体表）的无人机 / 哨戒桩下标（没有 −1）。
        /// 没有程序的无人机不挡住旁边有程序的那架。O(无人机数)，悬停每帧一次。</summary>
        public int PickDrone(Unity.Mathematics.double2 at, float radius)
        {
            int best = -1;
            double bestD = radius;
            for (int q = 0; q < _d.Drones.Length; q++)
            {
                if (CombatLogic.FlashHeadOf(ref _d, _d.Drones[q].Weapon).x < 0)
                {
                    continue;
                }
                double dist = Unity.Mathematics.math.distance(_d.Drones[q].Pos, at);
                if (dist <= bestD)
                {
                    bestD = dist;
                    best = q;
                }
            }
            return best;
        }

        /// <summary>读档：武器表头与变体表一起核对后落进 staging（表头指向的整段都在表内、变体武器在武器表内、剥离换算不超过去核心长度）。</summary>
        private static bool LoadFlash(ref CombatData staging, EmgLoadBuffer buf)
        {
            int[] map = buf?.FlashMap ?? Array.Empty<int>();
            Unity.Mathematics.int4[] heads = buf?.FlashHead;
            int weapons = staging.Weapons.Length;
            for (int i = 0; heads != null && i < heads.Length && i < weapons; i++)
            {
                Unity.Mathematics.int4 h = heads[i];
                if (h.x < 0)
                {
                    staging.FlashHead.Add(new Unity.Mathematics.int4(-1, 0, 0, 0));
                    continue;
                }
                int l0 = h.y, l1 = h.z;
                if (l1 > l0 || l0 > EmgConst.MaxFlashLen || (long)h.x + (l0 + 1) * 2 + l1 + 1 > map.Length)
                {
                    return false;
                }
                for (int k = 0; k <= l0 + l1 + 1; k++)
                {
                    if (map[h.x + k] < 0 || map[h.x + k] >= weapons)
                    {
                        return false;
                    }
                }
                for (int k = 0; k <= l0; k++)
                {
                    int sk = map[h.x + l0 + 1 + l1 + 1 + k];
                    if (sk < 0 || sk > l1)
                    {
                        return false;
                    }
                }
                staging.FlashHead.Add(h);
            }
            foreach (int x in map)
            {
                staging.FlashMap.Add(x);
            }
            return true;
        }

        /// <summary>EMG1-TRIG-01：写一行子形态参数（下标 = fg.TbSubPayload 的 code 1～5；数值必须有限且非负，否则拒绝）。</summary>
        public bool SetEmgSubForm(int code, in EmgSubForm f)
        {
            bool ok = code >= 1 && code <= 5 && Finite(f.Speed) && Finite(f.Life) && Finite(f.LifeRatio) && Finite(f.Radius) && Finite(f.RadiusRatio)
                      && f.Speed >= 0f && f.Life >= 0f && f.LifeRatio >= 0f && f.Radius >= 0f && f.RadiusRatio >= 0f;
            if (ok)
            {
                _d.EmgSubForms[code] = f;
            }
            return ok;
        }

        public EmgSubForm EmgSubFormOf(int code) => code >= 1 && code <= 5 ? _d.EmgSubForms[code] : default;

        /// <summary>EMG1-TRIG-01：待生成表（子载荷 / 定时 / 顺延的根弹体）。</summary>
        public int EmgPendingCount => _d.EmgPending.Length;

        public bool TryGetEmgPending(int index, out EmgSpawn s)
        {
            bool ok = index >= 0 && index < _d.EmgPending.Length;
            s = ok ? _d.EmgPending[index] : default;
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
            // 节点恰好铺满指令列表（上面已核），按节点走一遍就是全部指令。
            for (int j = 0; j < nodes.Count; j++)
            {
                int eventReads = 0;
                for (int i = nodes[j].FirstInstr; i < nodes[j].FirstInstr + nodes[j].InstrCount; i++)
                {
                    EmgInstr x = instrs[i];
                    if (!Finite(x.Amount) || !Finite(x.Param2) || !Finite(x.A) || !Finite(x.B) || !Finite(x.C))
                    {
                        return false;
                    }
                    // EMG1-BUS-01（FGR-EMG-065）：每宿主计数每条事件读端 2 比特、共 16 条；超过的读端行会让“同一事件 ≤ 3 次”失效，所以不收。
                    if (x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead && x.Port > EmgConst.EventPortBase && x.Port <= EmgConst.EventPortBase + EmgConst.EventKinds
                        && ++eventReads > EmgConst.HostCounterSlots)
                    {
                        return false;
                    }
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

        /// <summary>EMG1-BUS-01：地点级监听掩码按读进来的程序表重算（O(程序数)；读档后调用）。</summary>
        private void RecomputeEmgListen()
        {
            uint m = 0u;
            for (int i = 0; i < _d.EmgPrograms.Length; i++)
            {
                m |= _d.EmgPrograms[i].ListenMask;
            }
            _d.EmgListen = m;
        }

        /// <summary>地点级监听掩码（位 k = 事件码 k 可能有程序读；0 = 本地点没有程序）。</summary>
        public uint EmgListenMask => _d.EmgListen;

        /// <summary>战斗事件总线的调试计数（调试层：处理 / 合并 / 链深截断 / 扇出截断 / 每宿主截断 / 近炸求值……；不进快照）。</summary>
        public EmgBusStats EmgBusStats => _d.EmgStats[0];

        /// <summary>某种事件累计入队条数（调试层；不进快照）。</summary>
        public long EmgEnqueuedOf(EmgEventKind kind) => (int)kind > 0 && (int)kind < _d.EmgKindEnq.Length ? _d.EmgKindEnq[(int)kind] : 0;

        /// <summary>某种事件最后处理的一条（调试层 / 自检看字段；没处理过返回 false）。</summary>
        public bool TryGetLastEmgEvent(EmgEventKind kind, out EmgEvent e)
        {
            bool ok = (int)kind > 0 && (int)kind < _d.EmgLastByKind.Length && _d.EmgLastByKind[(int)kind].Kind == kind;
            e = ok ? _d.EmgLastByKind[(int)kind] : default;
            return ok;
        }

        /// <summary>
        /// EMG1-BUS-01（FGR-EMG-066）：热更层在两步之间注入一条单位事件——接入 / 断开 / 拾取 / 夺控 / 归还（O(本单位程序根节点的指令行数)）。
        /// 宿主 = 单位武器的程序根节点；按需：程序不读这个事件码时不入队（返回 <see cref="EmgInjectResult.NoListener"/>）。每次注入开一条新因果链（ADR ⑨）。
        /// 与内核生产点走同一个入队口（同一套 Seq、积压保护），在下一步的 5d 段按 Seq 处理。
        /// </summary>
        public EmgInjectResult InjectEmgUnitEvent(EmgEventKind kind, int unitId, ushort code, int other = 0)
        {
            if (kind != EmgEventKind.Uplink && kind != EmgEventKind.Unlink && kind != EmgEventKind.Pickup && kind != EmgEventKind.Seize && kind != EmgEventKind.Release)
            {
                return EmgInjectResult.NotInjectable;
            }
            int slot = _d.SlotOf(unitId);
            if (slot < 0)
            {
                return EmgInjectResult.NoUnit;
            }
            if (kind == EmgEventKind.Unlink)
            {
                FlashStrip(unitId); // EMG1-FLASH-01（FGR-EMG-037）：信号离开即剥离，不看程序有没有监听 evt.unlink
            }
            long before = _d.EmgStats[0].Enqueued + _d.EmgStats[0].Merged;
            long lost = _d.EmgStats[0].Lost;
            EmgCtx saved = _d.EmgCtx;
            _d.EmgCtx = EmgCtx.None; // 注入不在任何出手里：新链、链深 0
            CombatLogic.EmgUnitEvent(ref _d, kind, slot, other, 0f, 0f, code, _d.Pos[slot]);
            _d.EmgCtx = saved;
            if (_d.EmgStats[0].Lost > lost)
            {
                return EmgInjectResult.Full;
            }
            if (_d.EmgStats[0].Enqueued + _d.EmgStats[0].Merged == before)
            {
                return EmgInjectResult.NoListener;
            }
            Touch();
            return EmgInjectResult.Enqueued;
        }

        /// <summary>测试 / 调试：以未处理队列里第 <paramref name="i"/> 条为父事件，按“事件触发的生成”入队一条子事件（链深 + 1、继承链；父事件链深已到 3 时不入队并计 DepthCut）。
        /// 子载荷 / 规则产物的真实生成在 EMG1-TRIG-01 / EMG2-RULE-01 走同一个内核入口。</summary>
        public bool EmgEmitChildForTests(int i, EmgEventKind kind)
        {
            if (!TryGetEmgEvent(i, out EmgEvent parent) || kind < EmgEventKind.Fire || (byte)kind > EmgConst.EventKinds)
            {
                return false;
            }
            bool ok = CombatLogic.EmgTryEmitChild(ref _d, parent, new EmgEvent { Kind = kind, EntKind = parent.EntKind, Ent = parent.Ent, Prog = parent.Prog, Node = parent.Node, Pos = parent.Pos });
            Touch();
            return ok;
        }

        /// <summary>测试 / 调试：对在飞的第 <paramref name="slot"/> 发弹体走一次弹体宿主事件的生产（与内核生产点同一段代码：按需过滤、每宿主上限、入队）。
        /// 穿透 / 撞单位与障碍的反弹在 EMG1-GRM-01 的真实弹道里调同一个生产点；这里让自检在那之前就能验证它的入队口径。</summary>
        public bool EmgProjectileEventForTests(int slot, EmgEventKind kind, int otherUnit, float value, float value2, ushort code)
        {
            if (slot < 0 || slot >= _d.Projectiles.Length || kind < EmgEventKind.Fire || (byte)kind > EmgConst.EventKinds)
            {
                return false;
            }
            CombatProjectile pr = _d.Projectiles[slot];
            long before = _d.EmgStats[0].Enqueued + _d.EmgStats[0].Merged;
            CombatLogic.EmgProjEvent(ref _d, ref pr, kind, otherUnit, otherUnit != 0 ? EmgEntKind.Unit : EmgEntKind.None, value, value2, code, pr.Pos);
            _d.Projectiles[slot] = pr;
            Touch();
            return _d.EmgStats[0].Enqueued + _d.EmgStats[0].Merged > before;
        }

        /// <summary>
        /// 原始注入一条事件（KRN-01 的底层入口，自检与读档对拍用；热更层的接入 / 断开 / 拾取 / 夺控 / 归还走 <see cref="InjectEmgUnitEvent"/>）。内核发 Seq（总线自己的计数器）；
        /// Chain = 0 时分配一条新的因果链（ADR ⑨“热更注入分配一个”）。事件码不在 1～29、链深 &gt; 3、程序下标越界、节点不在该程序里、链为负或没发过、数值不是有限数、
        /// 队列已满（16384）时拒绝（返回 false，队列不变）。校验与读档共用 <see cref="EventValid"/>。不经积压合并；入队的事件由下一步 5d 段（CombatLogic.StepEmgBus）处理。
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
            // EMG1-BUS-01：调试计数与内核生产点同一口径（入队数 / 峰值 / 每种入队数）。
            EmgBusStats st = _d.EmgStats[0];
            st.Enqueued++;
            st.MaxPending = Math.Max(st.MaxPending, _d.EmgBusPending);
            _d.EmgStats[0] = st;
            _d.EmgKindEnq[(int)x.Kind] = _d.EmgKindEnq[(int)x.Kind] + 1;
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

        /// <summary>EMG1-TRIG-01：障碍 / 残骸变了——EmgHash 下次查询时重建（脏标记在 EmgS 里，内核一步的作业副本与主线程共用）。</summary>
        private void EmgHashDirty()
        {
            EmgScalars es = _d.EmgS[0];
            es.HashDirty = 1;
            _d.EmgS[0] = es;
        }

        /// <summary>登记一个残骸（发稳定 Id）。满了（<see cref="EmgConst.DefaultWreckCapacity"/>）、数值不合理（与读档同一份 <see cref="WreckValid"/>）或链没发过返回 false——
        /// 满了的“最早的崩解为碎屑”归 EMG1-ENT-01。</summary>
        public bool TryAddWreck(in EmgWreck wreck, out int id)
        {
            EmgHashDirty(); // EMG1-TRIG-01：残骸圆收在 EmgHash 里（即时命中掩体检测）
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
                if (_d.EProfile[i] != -1 || u.PrevMatterMask != 0u || u.LastHitBy != 0 || u.LastHitChain != 0 || u.HeatLatch != 0)
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
            w.Write(14 + (extraBytes > 0 ? 1 : 0)); // EMG1-FLASH-01：+ 段 14（刷写变体表）
            EmgScalars es = _d.EmgS[0];
            sw.Begin(EmgConst.SegScalars, 8, 1);
            if (sw.F()) w.Write(es.NextProjId);
            if (sw.F()) w.Write(es.NextDroneId);
            if (sw.F()) w.Write(es.NextWreckId);
            if (sw.F()) w.Write(es.NextPatchId);
            if (sw.F()) w.Write(es.NextFieldId);
            if (sw.F()) w.Write(es.NextChain);
            if (sw.F()) w.Write(es.EmgSeq);
            if (sw.F()) w.Write(es.NextZoneId); // EMG1-TRIG-01：尾部追加，短段读到取 1
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

            sw.Begin(EmgConst.SegWeapons, WeaponFields + 3, _d.Weapons.Length);
            for (int i = 0; i < _d.Weapons.Length; i++)
            {
                WriteWeaponEmg(w, _d.Weapons[i], sw);
                // EMG1-FLASH-01：刷写变体表表头（尾部追加；不进 WriteWeaponEmg——那份字段清单也是武器去重键，表头是登记后才挂上的派生关系）
                Unity.Mathematics.int4 fh = i < _d.FlashHead.Length ? _d.FlashHead[i] : new Unity.Mathematics.int4(-1, 0, 0, 0);
                if (sw.F()) w.Write(fh.x);
                if (sw.F()) w.Write((byte)fh.y);
                if (sw.F()) w.Write((byte)fh.z);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegUnits, 5, _d.Count);
            for (int i = 0; i < _d.Count; i++)
            {
                EmgUnitState u = _d.EmgUnit[i];
                if (sw.F()) w.Write(_d.EProfile[i]);
                if (sw.F()) w.Write(u.PrevMatterMask);
                if (sw.F()) w.Write(u.LastHitBy);
                if (sw.F()) w.Write(u.LastHitChain);
                if (sw.F()) w.Write(u.HeatLatch); // EMG1-BUS-01：尾部追加（同一格式 16 内只追加，短段读到取 0）
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegProjectiles, 20, _d.Projectiles.Length); // EMG1-GRM-01：16 → 20 个字段
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
                if (sw.F()) w.Write(p.IgnoreUnit); // EMG1-TRIG-01：尾部追加
                if (sw.F()) w.Write(p.ModQ); // EMG1-GRM-01：尾部追加 4 个（短段读到取缺省 = 没有修饰）
                if (sw.F()) w.Write((byte)p.Mods);
                if (sw.F()) w.Write(p.BounceObs);
                if (sw.F()) w.Write(p.Intercepts);
                sw.EndRecord();
            }
            sw.End();

            sw.Begin(EmgConst.SegDrones, 4, _d.Drones.Length);
            for (int i = 0; i < _d.Drones.Length; i++)
            {
                CombatDrone dr = _d.Drones[i];
                if (sw.F()) w.Write(dr.Id);
                if (sw.F()) w.Write(dr.Active); // EMG1-FLASH-01：尾部追加 3 个（短段读到 Active = 255 按全长、Strip / Pause = 0）
                if (sw.F()) w.Write(dr.Strip);
                if (sw.F()) w.Write(dr.Pause);
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

            WriteTriggerSegments(w, sw);

            sw.Begin(EmgConst.SegFlash, 1, _d.FlashMap.Length);
            for (int i = 0; i < _d.FlashMap.Length; i++)
            {
                if (sw.F()) w.Write(_d.FlashMap[i]);
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

        /// <summary>EMG1-TRIG-01：待生成表（段 11）、区域宿主字段（段 12，按区域表逐条）、回波宿主字段（段 13，按回波表逐条）。</summary>
        private void WriteTriggerSegments(BinaryWriter w, EmgSegmentWriter sw)
        {
            sw.Begin(EmgConst.SegPending, 25, _d.EmgPending.Length);
            for (int i = 0; i < _d.EmgPending.Length; i++)
            {
                EmgSpawn s = _d.EmgPending[i];
                if (sw.F()) w.Write(s.At);
                if (sw.F()) w.Write(s.Pos.x);
                if (sw.F()) w.Write(s.Pos.y);
                if (sw.F()) w.Write(s.Dir.x);
                if (sw.F()) w.Write(s.Dir.y);
                if (sw.F()) w.Write(s.Damage);
                if (sw.F()) w.Write(s.ZoneRadius);
                if (sw.F()) w.Write(s.ZoneLife);
                if (sw.F()) w.Write(s.Prog);
                if (sw.F()) w.Write(s.Chain);
                if (sw.F()) w.Write(s.Owner);
                if (sw.F()) w.Write(s.Weapon);
                if (sw.F()) w.Write(s.Other);
                if (sw.F()) w.Write(s.Ent);
                if (sw.F()) w.Write(s.Seq);
                if (sw.F()) w.Write(s.Node);
                if (sw.F()) w.Write(s.Depth);
                if (sw.F()) w.Write(s.Count);
                if (sw.F()) w.Write(s.Kind);
                if (sw.F()) w.Write(s.Faction);
                if (sw.F()) w.Write((byte)s.Flags);
                if (sw.F()) w.Write((byte)s.EntKind);
                if (sw.F()) w.Write(s.Armed);
                if (sw.F()) w.Write(s.SubForm);
                if (sw.F()) w.Write((short)(s.Gen | (s.Done << 4)));
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegZones, 9, _d.Zones.Length);
            for (int i = 0; i < _d.Zones.Length; i++)
            {
                CombatZone z = _d.Zones[i];
                if (sw.F()) w.Write(z.EmgProgRef);
                if (sw.F()) w.Write(z.EmgId);
                if (sw.F()) w.Write(z.EmgChain);
                if (sw.F()) w.Write(z.EmgTrigUsed);
                if (sw.F()) w.Write(z.EmgNode);
                if (sw.F()) w.Write(z.EmgDepth);
                if (sw.F()) w.Write(z.EmgGen);
                if (sw.F()) w.Write(z.EmgSub);
                if (sw.F()) w.Write(z.EmgWeapon);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegEchoes, 4, _d.Echoes.Length);
            for (int i = 0; i < _d.Echoes.Length; i++)
            {
                CombatEcho e = _d.Echoes[i];
                if (sw.F()) w.Write(e.EmgProgRef);
                if (sw.F()) w.Write(e.EmgChain);
                if (sw.F()) w.Write(e.EmgNode);
                if (sw.F()) w.Write(e.EmgDepth);
                sw.EndRecord();
            }
            sw.End();
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
            /// <summary>EMG1-FLASH-01：无人机刷写字段（Active / Strip / Pause）、武器表头、变体表。</summary>
            public byte[] DroneFlash;
            public Unity.Mathematics.int4[] FlashHead;
            public int[] FlashMap;
            /// <summary>EMG1-TRIG-01：区域 / 回波的宿主字段（读法表在 Emg 段之后读，FinishEmgLoad 再落）。</summary>
            public CombatZone[] ZoneHosts;
            public CombatEcho[] EchoHosts;
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
                    if (sr.F()) es.NextZoneId = r.ReadInt32();
                    if (!sr.RecordOk || es.NextProjId < 1 || es.NextDroneId < 1 || es.NextWreckId < 1 || es.NextPatchId < 1 || es.NextFieldId < 1 || es.NextChain < 1
                        || es.NextZoneId < 1)
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
                        var fh = new Unity.Mathematics.int4(-1, 0, 0, 0);
                        if (sr.F()) fh.x = r.ReadInt32();
                        if (sr.F()) fh.y = r.ReadByte();
                        if (sr.F()) fh.z = r.ReadByte();
                        buf.FlashHead ??= new Unity.Mathematics.int4[n];
                        buf.FlashHead[i] = fh; // 范围在 FinishEmgLoad 按变体表核（段 14 可能在后面）
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
                        if (sr.F()) u.HeatLatch = r.ReadByte();
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
                        if (sr.F()) p.IgnoreUnit = r.ReadInt32();
                        if (sr.F()) p.ModQ = r.ReadUInt16();
                        if (sr.F()) p.Mods = (EmgProjMods)r.ReadByte();
                        if (sr.F()) p.BounceObs = r.ReadByte();
                        if (sr.F()) p.Intercepts = r.ReadByte();
                        if (!sr.RecordOk || p.IgnoreUnit < 0 || ((p.Mods & EmgProjMods.Orbit) == 0 && p.SteerLeft > 360f))
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
                    buf.DroneFlash = new byte[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        if (sr.F()) buf.DroneIds[i] = r.ReadInt32();
                        buf.DroneFlash[i * 3] = sr.F() ? r.ReadByte() : (byte)255;
                        if (sr.F()) buf.DroneFlash[i * 3 + 1] = r.ReadByte();
                        if (sr.F()) buf.DroneFlash[i * 3 + 2] = r.ReadByte();
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
                case EmgConst.SegPending:
                    return ParsePending(r, sr, ref staging);
                case EmgConst.SegFlash:
                    if (n > 1 << 20)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    buf.FlashMap = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        buf.FlashMap[i] = sr.F() ? r.ReadInt32() : -1;
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegZones:
                    if (n > 1 << 20)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    buf.ZoneHosts = new CombatZone[n];
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var z = new CombatZone();
                        if (sr.F()) z.EmgProgRef = r.ReadInt32();
                        if (sr.F()) z.EmgId = r.ReadInt32();
                        if (sr.F()) z.EmgChain = r.ReadInt32();
                        if (sr.F()) z.EmgTrigUsed = r.ReadUInt32();
                        if (sr.F()) z.EmgNode = r.ReadByte();
                        if (sr.F()) z.EmgDepth = r.ReadByte();
                        if (sr.F()) z.EmgGen = r.ReadByte();
                        if (sr.F()) z.EmgSub = r.ReadByte();
                        if (sr.F()) z.EmgWeapon = r.ReadInt32();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        buf.ZoneHosts[i] = z;
                    }
                    return CombatLoadResult.Ok;
                case EmgConst.SegEchoes:
                    if (n > 1 << 20)
                    {
                        return CombatLoadResult.InvalidValue;
                    }
                    buf.EchoHosts = new CombatEcho[n];
                    for (int i = 0; i < n; i++)
                    {
                        sr.Record(i);
                        var e = new CombatEcho();
                        if (sr.F()) e.EmgProgRef = r.ReadInt32();
                        if (sr.F()) e.EmgChain = r.ReadInt32();
                        if (sr.F()) e.EmgNode = r.ReadByte();
                        if (sr.F()) e.EmgDepth = r.ReadByte();
                        if (!sr.RecordOk)
                        {
                            return CombatLoadResult.InvalidValue;
                        }
                        buf.EchoHosts[i] = e;
                    }
                    return CombatLoadResult.Ok;
                default:
                    return CombatLoadResult.Ok; // 不认识的段（较晚提交新增的）：整段跳过
            }
        }

        private static CombatLoadResult ParsePending(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n > EmgConst.PendingCapacity)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                var s = new EmgSpawn { Prog = -1, Weapon = -1 };
                short genDone = 0;
                if (sr.F()) s.At = r.ReadDouble();
                if (sr.F()) s.Pos.x = r.ReadDouble();
                if (sr.F()) s.Pos.y = r.ReadDouble();
                if (sr.F()) s.Dir.x = r.ReadSingle();
                if (sr.F()) s.Dir.y = r.ReadSingle();
                if (sr.F()) s.Damage = r.ReadSingle();
                if (sr.F()) s.ZoneRadius = r.ReadSingle();
                if (sr.F()) s.ZoneLife = r.ReadSingle();
                if (sr.F()) s.Prog = r.ReadInt32();
                if (sr.F()) s.Chain = r.ReadInt32();
                if (sr.F()) s.Owner = r.ReadInt32();
                if (sr.F()) s.Weapon = r.ReadInt32();
                if (sr.F()) s.Other = r.ReadInt32();
                if (sr.F()) s.Ent = r.ReadInt32();
                if (sr.F()) s.Seq = r.ReadInt32();
                if (sr.F()) s.Node = r.ReadByte();
                if (sr.F()) s.Depth = r.ReadByte();
                if (sr.F()) s.Count = r.ReadByte();
                if (sr.F()) s.Kind = r.ReadByte();
                if (sr.F()) s.Faction = r.ReadByte();
                if (sr.F()) s.Flags = (EmgProjFlags)r.ReadByte();
                if (sr.F()) s.EntKind = (EmgEntKind)r.ReadByte();
                if (sr.F()) s.Armed = r.ReadByte();
                if (sr.F()) s.SubForm = r.ReadByte();
                if (sr.F()) genDone = r.ReadInt16();
                s.Gen = (byte)(genDone & 0xF);
                s.Done = (short)(genDone >> 4);
                if (!sr.RecordOk)
                {
                    return CombatLoadResult.InvalidValue;
                }
                staging.EmgPending.Add(s);
            }
            return CombatLoadResult.Ok;
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
            if (!LoadFlash(ref staging, buf))
            {
                return CombatLoadResult.InvalidValue;
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
                dr.Active = buf?.DroneFlash != null ? buf.DroneFlash[i * 3] : (byte)255;
                dr.Strip = buf?.DroneFlash != null ? buf.DroneFlash[i * 3 + 1] : (byte)0;
                dr.Pause = buf?.DroneFlash != null ? buf.DroneFlash[i * 3 + 2] : (byte)0;
                if (dr.Id < 0 || (dr.Id > 0 && !ids.Add(dr.Id)) || dr.Strip > 1)
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
            CombatLoadResult tr = FinishTriggerLoad(ref staging, buf, ref es, ref maxChain);
            if (tr != CombatLoadResult.Ok)
            {
                return tr;
            }
            es.NextChain = Math.Max(es.NextChain, maxChain + 1);
            es.BusHead = 0;
            staging.EmgS[0] = es;
            return CombatLoadResult.Ok;
        }

        /// <summary>EMG1-TRIG-01 读档收尾：落区域 / 回波的宿主字段（条数必须与读法表一致），校验程序下标 / 节点 / 链深 / Id 唯一，待生成请求逐条校验；区域发号器接着发。</summary>
        private static CombatLoadResult FinishTriggerLoad(ref CombatData staging, EmgLoadBuffer buf, ref EmgScalars es, ref int maxChain)
        {
            int programs = staging.EmgPrograms.Length;
            if (buf?.ZoneHosts != null && buf.ZoneHosts.Length != staging.Zones.Length || buf?.EchoHosts != null && buf.EchoHosts.Length != staging.Echoes.Length)
            {
                return CombatLoadResult.InvalidValue;
            }
            var ids = new HashSet<int>();
            int maxZone = 0;
            for (int i = 0; buf?.ZoneHosts != null && i < staging.Zones.Length; i++)
            {
                CombatZone h = buf.ZoneHosts[i];
                CombatZone z = staging.Zones[i];
                if (h.EmgProgRef < 0 || h.EmgProgRef > programs || h.EmgChain < 0 || h.EmgDepth > EmgConst.MaxDepth || h.EmgGen > EmgConst.MaxGen || h.EmgSub > 1
                    || h.EmgProgRef > 0 && (h.EmgWeapon < -1 || h.EmgWeapon >= staging.Weapons.Length || h.EmgId < 1 || !ids.Add(h.EmgId) || h.EmgNode >= staging.EmgPrograms[h.EmgProgRef - 1].NodeCount))
                {
                    return CombatLoadResult.InvalidValue;
                }
                z.EmgProgRef = h.EmgProgRef;
                z.EmgId = h.EmgId;
                z.EmgChain = h.EmgChain;
                z.EmgTrigUsed = h.EmgTrigUsed;
                z.EmgNode = h.EmgNode;
                z.EmgDepth = h.EmgDepth;
                z.EmgGen = h.EmgGen;
                z.EmgSub = h.EmgSub;
                z.EmgWeapon = h.EmgWeapon;
                staging.Zones[i] = z;
                maxZone = Math.Max(maxZone, h.EmgId);
                maxChain = Math.Max(maxChain, h.EmgChain);
            }
            es.NextZoneId = Math.Max(es.NextZoneId, maxZone + 1);
            for (int i = 0; buf?.EchoHosts != null && i < staging.Echoes.Length; i++)
            {
                CombatEcho h = buf.EchoHosts[i];
                CombatEcho e = staging.Echoes[i];
                if (h.EmgProgRef < 0 || h.EmgProgRef > programs || h.EmgChain < 0 || h.EmgDepth > EmgConst.MaxDepth
                    || h.EmgProgRef > 0 && h.EmgNode >= staging.EmgPrograms[h.EmgProgRef - 1].NodeCount)
                {
                    return CombatLoadResult.InvalidValue;
                }
                e.EmgProgRef = h.EmgProgRef;
                e.EmgChain = h.EmgChain;
                e.EmgNode = h.EmgNode;
                e.EmgDepth = h.EmgDepth;
                staging.Echoes[i] = e;
                maxChain = Math.Max(maxChain, h.EmgChain);
            }
            for (int i = 0; i < staging.EmgPending.Length; i++)
            {
                EmgSpawn s = staging.EmgPending[i];
                if (!SpawnValid(s, ref staging))
                {
                    return CombatLoadResult.InvalidValue;
                }
                maxChain = Math.Max(maxChain, s.Chain);
            }
            es.ArmCursor = 0;
            es.HashDirty = 1;
            return CombatLoadResult.Ok;
        }

        /// <summary>待生成请求：种类 0～2、程序下标在表内且节点在程序节点数内（根弹体为 −1 也可）、链深 ≤ 3、代 ≤ 2、子形态 0～5、武器在表内、数值有限。</summary>
        private static bool SpawnValid(in EmgSpawn s, ref CombatData d) =>
            s.Kind <= EmgConst.PendRoot && s.Prog >= -1 && s.Prog < d.EmgPrograms.Length && (s.Prog < 0 || s.Node < d.EmgPrograms[s.Prog].NodeCount)
            && (s.Prog >= 0 || s.Kind == EmgConst.PendRoot) && s.Depth <= EmgConst.MaxDepth && s.Gen <= EmgConst.MaxGen && s.SubForm <= 5 && s.Armed <= 1
            && s.Weapon >= -1 && s.Weapon < d.Weapons.Length && s.Chain >= 0 && s.Done >= 0 && (byte)s.EntKind <= (byte)EmgEntKind.Field
            && !double.IsNaN(s.At) && !double.IsInfinity(s.At) && IsFinite(s.Pos) && Finite(s.Dir.x) && Finite(s.Dir.y) && Finite(s.Damage) && Finite(s.ZoneRadius) && Finite(s.ZoneLife);

        // ── 写入口与读档共用的校验：写得进去的值存档后一定读得回来（审查 P1：三个写入口曾比读档宽）──

        /// <summary>事件：码 1～29、链深 ≤ 3、实体种类合法、程序下标 −1 或在表内、有程序时节点在该程序的节点数内（BUS-01 按 FirstNode + Node 查节点表）、链非负、数值有限。</summary>
        private static bool EventValid(in EmgEvent e, ref CombatData d) =>
            e.Kind >= EmgEventKind.Fire && (byte)e.Kind <= EmgConst.EventKinds && e.Depth <= EmgConst.MaxDepth
            && (byte)e.EntKind <= (byte)EmgEntKind.Field && (byte)e.OtherKind <= (byte)EmgEntKind.Field
            && e.Prog >= -1 && e.Prog < d.EmgPrograms.Length && (e.Prog < 0 || e.Node < d.EmgPrograms[e.Prog].NodeCount)
            && e.Chain >= 0 && Finite(e.Value) && Finite(e.Value2) && IsFinite(e.Pos);

        private static bool WeaponEmgValid(in CombatWeapon w, int programs) => w.ProgramRef >= 0 && w.ProgramRef <= programs && w.SpawnFlight <= 1;

        private static bool UnitEmgValid(short profile, in EmgUnitState u) => profile >= -1 && u.LastHitBy >= 0 && u.LastHitChain >= 0 && u.HeatLatch <= 1;

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
