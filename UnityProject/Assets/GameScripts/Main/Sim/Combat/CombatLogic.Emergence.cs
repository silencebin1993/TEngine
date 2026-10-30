using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-BUS-01 战斗事件总线（FG19 FGR-EMG-060～068；ADR-EMG-001 ④）：生产点只入队、不内联执行；一步里“5d 涌现总线”按 Seq 先进先出每步处理
    /// emg.bus.per_step（512）条，处理中新产生的追加到队尾、预算有剩本步继续，超出顺延到下一步（队列进快照，不丢）。
    /// 按需：地点级监听掩码（<see cref="CombatData.EmgListen"/>）一条比较判空，再按宿主节点的读端行过滤——没有程序读的事件不入队，近炸不求值。
    /// 分派：事件自带 (Prog, Node) → 该节点读这个事件端口的指令行，按槽位序最多唤起 8 条（多的计 FanoutCut）；链深已到 3 的事件不再生成后代（计 DepthCut）。
    /// 唤起触发读端之后的子载荷生成归 EMG1-TRIG-01、条件 / 感知读端之后的效果归 EMG1-FW-01 / FW-02（都经 <see cref="EmgTryEmitChild"/> 一类入口，受同一套链深约束）。
    /// 术语只用“战斗事件总线”（内核侧一律 Emg 前缀），与 FG10“事件导演”无关（FGR-EMG-068）。
    /// </summary>
    public static partial class CombatLogic
    {
        // ─────────────────────────────── 掩码与宿主 ───────────────────────────────

        private static uint EmgBit(EmgEventKind k) => 1u << (int)k;

        internal static bool EmgSiteListens(ref CombatData d, EmgEventKind k) => (d.EmgListen & EmgBit(k)) != 0;

        private static int EmgUnitProg(ref CombatData d, int slot)
        {
            int w = d.Weapon[slot];
            return w >= 0 && w < d.Weapons.Length ? d.Weapons[w].Program : -1;
        }

        /// <summary>宿主 (程序, 节点) 读事件 <paramref name="k"/> 的第一条读端行，在本节点“事件读端行”里的序号（每宿主 2 比特计数的位置）；没有这样的行返回 −1（= 没有监听，不入队）。
        /// O(本节点指令行数，≤ 128，通常个位数)。</summary>
        internal static int EmgReadSlot(ref CombatData d, int prog, int node, EmgEventKind k)
        {
            if (prog < 0 || prog >= d.EmgPrograms.Length || (d.EmgPrograms[prog].ListenMask & EmgBit(k)) == 0)
            {
                return -1;
            }
            EmgProgram p = d.EmgPrograms[prog];
            int ni = p.FirstNode + node;
            if (node < 0 || node >= p.NodeCount || ni >= d.EmgNodes.Length)
            {
                return -1;
            }
            EmgNode n = d.EmgNodes[ni];
            ushort port = (ushort)(EmgConst.EventPortBase + (int)k);
            int slot = 0;
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Role < EmgConst.RoleTrigger || x.Role > EmgConst.RoleLastRead || x.Port <= EmgConst.EventPortBase || x.Port > EmgConst.EventPortBase + EmgConst.EventKinds)
                {
                    continue;
                }
                if (x.Port == port)
                {
                    return slot;
                }
                slot++;
            }
            return -1;
        }

        /// <summary>读端参数（定时秒 / 近炸半径）：本节点读 <paramref name="k"/> 的读端行 Param2 取大；都是 0 时取该事件触发的子节点的 TrigParam（编译器把触发写端的参数落在那里）。</summary>
        private static float EmgReadParam(ref CombatData d, int prog, int node, EmgEventKind k)
        {
            EmgProgram p = d.EmgPrograms[prog];
            EmgNode n = d.EmgNodes[p.FirstNode + node];
            ushort port = (ushort)(EmgConst.EventPortBase + (int)k);
            float best = 0f;
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == port && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead)
                {
                    best = math.max(best, x.Param2);
                }
            }
            if (best > 0f)
            {
                return best;
            }
            for (int j = 0; j < p.NodeCount && p.FirstNode + j < d.EmgNodes.Length; j++)
            {
                EmgNode c = d.EmgNodes[p.FirstNode + j];
                if (c.Parent == node && c.TrigEvent == k)
                {
                    best = math.max(best, c.TrigParam);
                }
            }
            return best;
        }

        // ─────────────────────────────── 入队 ───────────────────────────────

        /// <summary>入队一条事件：总线发 Seq；Chain = 0 时开一条新因果链。积压到 BusCapacity − 32 起先合并（<see cref="EmgConst.MergeHeadroom"/>），计数不丢。</summary>
        internal static void EmgEmit(ref CombatData d, EmgEvent e)
        {
            EmgScalars es = d.EmgS[0];
            EmgBusStats st = d.EmgStats[0];
            int pending = d.EmgBus.Length - es.BusHead;
            if (pending >= EmgConst.BusCapacity - EmgConst.MergeHeadroom && EmgTryMerge(ref d, es, e))
            {
                st.Merged++;
                d.EmgStats[0] = st;
                d.EmgPortCount[(int)e.Kind] = d.EmgPortCount[(int)e.Kind] + 1; // 合并的计数不丢（EMG1-CHAIN-01 端口计数同口径）
                return;
            }
            if (pending >= EmgConst.BusCapacity)
            {
                st.Lost++;
                d.EmgStats[0] = st;
                return;
            }
            bool opened = e.Chain == 0;
            if (opened)
            {
                e.Chain = es.NextChain++;
            }
            e.Seq = es.EmgSeq;
            es.EmgSeq = unchecked(es.EmgSeq + 1);
            d.EmgS[0] = es;
            d.EmgBus.Add(e);
            st.Enqueued++;
            st.MaxPending = math.max(st.MaxPending, pending + 1);
            d.EmgStats[0] = st;
            d.EmgKindEnq[(int)e.Kind] = d.EmgKindEnq[(int)e.Kind] + 1;
            d.EmgPortCount[(int)e.Kind] = d.EmgPortCount[(int)e.Kind] + 1; // EMG1-CHAIN-01：按端口的可靠计数（进快照）
            if (opened)
            {
                EmgChainLog(ref d, EmgChainNode.Inject, e.Chain, e.Depth, (int)e.Kind, e.EntKind == EmgEntKind.Unit ? e.Ent : 0, e.Pos); // 不在任何出手里的事件开新链
            }
        }

        /// <summary>积压保护（ADR ④）：先找同一步、同类同源（宿主程序 / 节点 / 实体 / Code 相同）的一条，找不到再找队尾 MergeScan 条里同类的一条；Value 累加、Code2 记合并数。
        /// 两轮都只看队尾 MergeScan 条：O(1) / 条，且只是队列内容的函数（连续跑与存读档后续跑合并到同一条）。</summary>
        private static bool EmgTryMerge(ref CombatData d, in EmgScalars es, in EmgEvent e)
        {
            int head = es.BusHead;
            int last = d.EmgBus.Length - 1;
            int add = 1 + e.Code2;
            int found = -1;
            for (int i = last, n = 0; i >= head && n < EmgConst.MergeScan; i--, n++)
            {
                EmgEvent x = d.EmgBus[i];
                if (unchecked(x.Seq - es.StepFirstSeq) < 0)
                {
                    break;
                }
                if (x.Kind == e.Kind && x.EntKind == e.EntKind && x.Ent == e.Ent && x.Prog == e.Prog && x.Node == e.Node && x.Code == e.Code && x.Code2 + add <= ushort.MaxValue)
                {
                    found = i;
                    break;
                }
            }
            for (int i = last, n = 0; found < 0 && i >= head && n < EmgConst.MergeScan; i--, n++)
            {
                EmgEvent x = d.EmgBus[i];
                if (x.Kind == e.Kind && x.Code2 + add <= ushort.MaxValue)
                {
                    found = i;
                }
            }
            if (found < 0)
            {
                return false;
            }
            EmgEvent t = d.EmgBus[found];
            t.Value += e.Value;
            t.Code2 = (ushort)(t.Code2 + add);
            d.EmgBus[found] = t;
            return true;
        }

        /// <summary>单位级事件（阵亡 / 被击 / 过热 / 冷却完成……）与“单位做宿主”的事件：宿主 = 单位武器的程序根节点。在一次出手 / 载荷结算里发生的继承它的链与链深。</summary>
        internal static void EmgUnitEvent(ref CombatData d, EmgEventKind k, int slot, int other, float value, float value2, ushort code, double2 pos,
            EmgEntKind otherKind = EmgEntKind.Unit)
        {
            if (!EmgSiteListens(ref d, k) || slot < 0 || slot >= d.Count)
            {
                return;
            }
            int prog = EmgUnitProg(ref d, slot);
            if (EmgReadSlot(ref d, prog, 0, k) < 0)
            {
                return;
            }
            bool inCtx = d.EmgCtx.Prog >= 0;
            // EMG1-TRIG-01：单位级事件的触发子载荷在本单位处放出；被击朝向攻击者（FGR-EMG-034 表）。
            int os = k == EmgEventKind.Hurt && other != 0 ? d.SlotOf(other) : -1;
            float2 dir = os >= 0 ? EmgDir(d.Pos[os] - d.Pos[slot]) : float2.zero;
            EmgEmitHosted(ref d, new EmgEvent
            {
                Kind = k, Depth = inCtx ? d.EmgCtx.Depth : (byte)0, EntKind = EmgEntKind.Unit, Ent = d.Id[slot],
                OtherKind = other != 0 ? otherKind : EmgEntKind.None, Other = other, Prog = prog, Node = 0,
                Chain = inCtx ? d.EmgCtx.Chain : 0, Code = code, Value = value, Value2 = value2, Pos = pos,
            }, EmgUnitHost(ref d, slot), dir);
        }

        /// <summary>当前宿主（出手单位 / 弹体命中结算 / 无人机）做宿主的事件；与弹体宿主同一条“每宿主同一事件码 ≤ 3 次”（计数在上下文里，出手结束即丢，弹体宿主拷回弹体）。</summary>
        internal static void EmgShotEvent(ref CombatData d, EmgEventKind k, int other, float value, float value2, ushort code, double2 pos,
            EmgEntKind otherKind = EmgEntKind.Unit)
        {
            EmgCtx c = d.EmgCtx;
            if (c.Prog < 0 || !EmgSiteListens(ref d, k))
            {
                return;
            }
            int slot = EmgReadSlot(ref d, c.Prog, c.Node, k);
            if (slot < 0 || !EmgHostCap(ref d, ref c.TrigUsed, slot))
            {
                return;
            }
            d.EmgCtx.TrigUsed = c.TrigUsed;
            // EMG1-TRIG-01：触发子载荷的方向 = 出手者 → 结算点（命中触发另按“5 米内其他敌对单位”取向）。
            float2 dir = c.Owner >= 0 && c.Owner < d.Count ? EmgDir(pos - d.Pos[c.Owner]) : float2.zero;
            EmgEmitHosted(ref d, new EmgEvent
            {
                Kind = k, Depth = c.Depth, EntKind = c.EntKind, Ent = c.Ent, OtherKind = other != 0 ? otherKind : EmgEntKind.None, Other = other,
                Prog = c.Prog, Node = c.Node, Chain = c.Chain, Code = code, Value = value, Value2 = value2, Pos = pos,
            }, c, dir);
        }

        /// <summary>每宿主同一事件码 ≤ emg.trig.max_per_host 次（FGR-EMG-065）：<paramref name="used"/> 每条事件读端 2 比特；到上限计 PerHostCut、返回 false。
        /// 读端序号 &lt; HostCounterSlots 由登记 / 读档校验保证（ProgramShapeValid）。</summary>
        private static bool EmgHostCap(ref CombatData d, ref uint used, int slot)
        {
            int cap = d.Config.EmgMaxPerHost > 0 ? math.min(d.Config.EmgMaxPerHost, EmgConst.DefaultMaxPerHost) : EmgConst.DefaultMaxPerHost;
            int shift = slot * 2;
            if (((used >> shift) & 3u) >= (uint)cap)
            {
                EmgBusStats st = d.EmgStats[0];
                st.PerHostCut++;
                d.EmgStats[0] = st;
                return false;
            }
            used += 1u << shift;
            return true;
        }

        /// <summary>弹体做宿主的事件（穿透 / 反弹 / 终结 / 近炸 / 定时 / 吸收；命中与击杀在命中结算的上下文里经 <see cref="EmgOnDamage"/> / <see cref="EmgOnKill"/> 发）：
        /// 每个载荷节点同一事件码 ≤ emg.trig.max_per_host 次（TrigUsed 每条事件读端 2 比特）。穿透与“撞单位 / 障碍反弹”的发生处随 EMG1-GRM-01 的真实穿透 / 反弹接入，调的就是这里。</summary>
        internal static void EmgProjEvent(ref CombatData d, ref CombatProjectile pr, EmgEventKind k, int other, EmgEntKind otherKind, float value, float value2, ushort code, double2 pos)
        {
            if (pr.Prog < 0 || !EmgSiteListens(ref d, k))
            {
                return;
            }
            int slot = EmgReadSlot(ref d, pr.Prog, pr.Node, k);
            if (slot < 0 || !EmgHostCap(ref d, ref pr.TrigUsed, slot))
            {
                return;
            }
            EmgEmitHosted(ref d, new EmgEvent
            {
                Kind = k, Depth = pr.Depth, EntKind = EmgEntKind.Projectile, Ent = pr.Id, OtherKind = otherKind, Other = other,
                Prog = pr.Prog, Node = pr.Node, Chain = pr.Chain, Code = code, Value = value, Value2 = value2, Pos = pos,
            }, EmgProjHost(ref d, pr), EmgDir((double2)pr.Vel));
        }

        /// <summary>
        /// 事件触发的生成（子载荷、事件驱动的规则产物、破片、写事件端口的固件发出的新事件）一律经这里：链深已到 3 的事件不再生成后代，计 DepthCut 并记下这条链
        /// （因果链末端的“链深已达上限”由 EMG1-CHAIN-01 读它显示）。原地转化 / 热交换 / 传导 / 合并不走这里（不加链深，FGR-EMG-061）。
        /// </summary>
        internal static bool EmgCanGenerate(ref CombatData d, in EmgEvent parent)
        {
            if (parent.Depth < EmgConst.MaxDepth)
            {
                return true;
            }
            EmgBusStats st = d.EmgStats[0];
            st.DepthCut++;
            st.LastDepthCutChain = parent.Chain;
            d.EmgStats[0] = st;
            EmgChainLog(ref d, EmgChainNode.DepthCut, parent.Chain, parent.Depth, (int)parent.Kind, parent.EntKind == EmgEntKind.Unit ? parent.Ent : 0, parent.Pos); // EMG1-CHAIN-01：链尾“链深已达上限”
            return false;
        }

        /// <summary>由 <paramref name="parent"/> 引起的一条新事件：链深 + 1、继承因果链；链深已到 3 时不入队（返回 false）。</summary>
        internal static bool EmgTryEmitChild(ref CombatData d, in EmgEvent parent, EmgEvent child)
        {
            if (!EmgCanGenerate(ref d, parent))
            {
                return false;
            }
            child.Depth = (byte)(parent.Depth + 1);
            child.Chain = parent.Chain;
            EmgEmit(ref d, child);
            return true;
        }

        // ─────────────────────────────── 出手 / 载荷上下文 ───────────────────────────────

        /// <summary>
        /// 一次出手（任何开火方式真的开了火）：带程序的武器开一条新因果链并成为本次出手的宿主（0 代根节点、链深 0），发 evt.fire；
        /// 即时类载体（格斗 / 力场 / 布区 / 即时射弹）的近炸按“出手瞬间目标在读端半径内”求值一次。
        /// 没有程序的武器只记伤害来源（evt.hurt 的来源码按载体算，不看攻击者有没有程序），不开链、不发事件（与旧行为逐字相同）。
        /// 必须与 <see cref="EmgShotEnd"/> 成对（UnitAttack / FireAt 的外层负责）。
        /// </summary>
        internal static void EmgShotBegin(ref CombatData d, int a, int t, in CombatWeapon wp)
        {
            CombatCarrier carrier = wp.Reading.Carrier;
            byte src = wp.Mode == CombatWeaponMode.Projectile || carrier == CombatCarrier.Projectile ? (byte)EmgCode.SrcProjectile
                : carrier == CombatCarrier.Melee ? (byte)EmgCode.SrcMelee
                : carrier == CombatCarrier.Field ? (byte)EmgCode.SrcZone
                : carrier == CombatCarrier.Aura ? (byte)EmgCode.SrcField : (byte)EmgCode.SrcOther;
            int prog = wp.Program;
            EmgHostBegin(ref d, prog, a, d.Id[a], EmgEntKind.Unit, src, d.Pos[a], t);
            EmgFillShot(ref d, a, t, wp); // EMG1-TRIG-01：母载荷参数（触发子载荷用）
            if (prog < 0 || d.EmgListen == 0u)
            {
                return;
            }
            EmgQueueShotTimer(ref d, a, wp); // EMG1-TRIG-01：格斗 / 力场的定时从出手起算（DEBT-EMG1BUS01-03）
            if (wp.Mode != CombatWeaponMode.Projectile && EmgSiteListens(ref d, EmgEventKind.Prox) && EmgReadSlot(ref d, prog, 0, EmgEventKind.Prox) >= 0)
            {
                EmgCountProx(ref d);
                float radius = EmgReadParam(ref d, prog, 0, EmgEventKind.Prox);
                float dist = (float)math.distance(d.Pos[a], d.Pos[t]);
                if (radius > 0f && dist <= radius)
                {
                    EmgShotEvent(ref d, EmgEventKind.Prox, d.Id[t], dist, 0f, 0, d.Pos[t]);
                }
            }
        }

        internal static void EmgShotEnd(ref CombatData d) => d.EmgCtx = EmgCtx.None;

        /// <summary>
        /// 不经过 UnitAttack / FireAt 的派系专属攻击（齐射空爆、地雷、冲撞 / 攻城冲撞、喷淋、横扫、自爆、爆燃、分裂液、酸沼）走这里扣血：
        /// 先记“来源 + 出手者”再进统一入口，evt.hurt 的来源码按载体给（FGR-EMG-060 第 11 行）；结算完恢复原上下文。<paramref name="attacker"/> 可为 −1（酸沼、己方挨的爆燃）。
        /// </summary>
        internal static bool DamageFrom(ref CombatData d, int t, float damage, int attacker, ushort src)
        {
            if (d.EmgListen == 0u)
            {
                d.EmgMergeCont = (byte)(src == EmgCode.SrcZone || src == EmgCode.SrcPatch ? 1 : 0);
                bool hit = DamageUnit(ref d, t, damage, attacker);
                d.EmgMergeCont = 0;
                return hit;
            }
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = EmgCtx.Source(attacker, EmgEntKind.Unit, (byte)src);
            bool cont = src == EmgCode.SrcZone || src == EmgCode.SrcPatch; // EMG1-ENT-01：区域 / 斑块类持续伤害对外部血量单位按步合并
            d.EmgMergeCont = (byte)(cont ? 1 : 0);
            bool landed = DamageUnit(ref d, t, damage, attacker);
            d.EmgMergeCont = 0;
            d.EmgCtx = saved;
            return landed;
        }

        /// <summary>
        /// 一个宿主开始结算：先记伤害来源与出手者（没有程序也记，evt.hurt 的来源码用）；带程序时开一条新因果链、成为宿主（0 代根节点、链深 0），按需发 evt.fire
        /// （Value / Value2 = 从 <paramref name="from"/> 指向目标的方向）。
        /// </summary>
        private static void EmgHostBegin(ref CombatData d, int prog, int owner, int ent, EmgEntKind kind, byte src, double2 from, int t)
        {
            d.EmgCtx = EmgCtx.Source(owner, kind, src);
            if (prog < 0)
            {
                return;
            }
            EmgScalars es = d.EmgS[0];
            int chain = es.NextChain++;
            d.EmgS[0] = es;
            EmgChainLog(ref d, EmgChainNode.Shot, chain, 0, 0, EmgIdOf(ref d, owner), from); // EMG1-CHAIN-01：一次出手开一条链
            d.EmgCtx = new EmgCtx { Prog = prog, Chain = chain, Ent = ent, Owner = owner, Node = 0, Depth = 0, EntKind = kind, HurtSrc = src, HitCode = EmgCode.HitDirect };
            if (d.EmgListen == 0u)
            {
                return;
            }
            double2 to = d.Pos[t] - from;
            float2 dir = math.lengthsq(to) > 1e-12 ? (float2)math.normalize(to) : new float2(0f, 1f);
            EmgShotEvent(ref d, EmgEventKind.Fire, d.Id[t], dir.x, dir.y, 0, from);
        }

        // ── EMG1-FLASH-01（FG19 FGR-EMG-036 / 037；ADR ③）：持续实体的刷写带宽、距离衰减与信号离开时剥离核心指令 ──
        // 变体表（CombatData.FlashHead / FlashMap）由热更层在武器结算时登记：无人机开火查“前 k 条”那一行的武器，程序与读法一起截，
        // 子载荷、触发、evt.expire 都随那一行的程序走（在飞载荷持有发射时的程序，FGR-EMG-037）。全部 O(无人机数)，只在内核。

        internal static int4 FlashHeadOf(ref CombatData d, int weapon) => weapon >= 0 && weapon < d.FlashHead.Length ? d.FlashHead[weapon] : new int4(-1, 0, 0, 0);

        internal static int FlashBandwidthOf(ref CombatData d) => d.Config.EmgFlashBandwidth > 0 ? d.Config.EmgFlashBandwidth : EmgConst.DefaultFlashBandwidth;

        /// <summary>这架持有几条（读档短段的 255 = 全长；武器没有变体表 = 0，不占带宽）。</summary>
        internal static int FlashHeld(ref CombatData d, in CombatDrone dr)
        {
            int4 h = FlashHeadOf(ref d, dr.Weapon);
            return h.x < 0 ? 0 : math.min(dr.Active, dr.Strip != 0 ? h.z : h.y);
        }

        /// <summary>母机 <paramref name="ownerId"/> 的刷写占用：存活（未到期）实体持有条数之和，跳过下标 <paramref name="skip"/>。</summary>
        internal static int FlashUsedBy(ref CombatData d, int ownerId, int skip)
        {
            double now = d.Scalars[0].Time;
            int used = 0;
            for (int q = 0; q < d.Drones.Length; q++)
            {
                CombatDrone dr = d.Drones[q];
                if (q != skip && dr.Owner == ownerId && dr.Until > now)
                {
                    used += FlashHeld(ref d, dr);
                }
            }
            return used;
        }

        /// <summary>这架此刻开火用的武器：没有变体表 = 放出它的武器（旧行为逐字不变）；有 = 变体表里“前 (持有 − 暂停) 条”那一行。</summary>
        internal static int FlashExecWeapon(ref CombatData d, in CombatDrone dr)
        {
            int4 h = FlashHeadOf(ref d, dr.Weapon);
            if (h.x < 0)
            {
                return dr.Weapon;
            }
            int len = dr.Strip != 0 ? h.z : h.y;
            int k = math.clamp(math.min(dr.Active, len) - dr.Pause, 0, len);
            int at = h.x + (dr.Strip != 0 ? h.y + 1 : 0) + k;
            return at < d.FlashMap.Length ? d.FlashMap[at] : dr.Weapon;
        }

        /// <summary>新放出的一架该刷几条：min(程序全长, 带宽 − 已占用)，可到 0（= 只有基础攻击）。已存在的实体不受影响（FGR-EMG-036）。</summary>
        internal static byte FlashGrant(ref CombatData d, int weapon, int used)
        {
            int4 h = FlashHeadOf(ref d, weapon);
            return h.x < 0 ? (byte)0 : (byte)math.clamp(FlashBandwidthOf(ref d) - used, 0, h.y);
        }

        /// <summary>本步是否跨过一个 0.25 游戏秒的刷写节拍（按模拟时间对齐：暂停 dt = 0 不跨，倍速按游戏秒同样每 0.25 秒一次；不存状态，读档后照样对齐）。</summary>
        internal static bool FlashTick(double now, float dt) =>
            dt > 0f && math.floor(now / EmgConst.FlashInterval) != math.floor((now - dt) / EmgConst.FlashInterval);

        /// <summary>距离衰减：离母机超过 emg.flash.range 后，每再远 emg.flash.step（不足一档按一档）暂停末尾 1 条，最多暂停到 0 条；回到范围内为 0。</summary>
        internal static byte FlashPauseOf(ref CombatData d, in CombatDrone dr, int ownerSlot)
        {
            int held = FlashHeld(ref d, dr);
            if (held <= 0)
            {
                return 0;
            }
            float range = d.Config.EmgFlashRange > 0f ? d.Config.EmgFlashRange : EmgConst.DefaultFlashRange;
            float step = d.Config.EmgFlashStep > 0f ? d.Config.EmgFlashStep : EmgConst.DefaultFlashStep;
            double over = math.distance(dr.Pos, d.Pos[ownerSlot]) - range;
            return over <= 0.0 ? (byte)0 : (byte)math.min(held, (int)math.ceil(over / step));
        }

        /// <summary>信号离开母机（evt.unlink 注入 / 炮塔离开）：它已放出、持有刷写程序的无人机 / 哨戒桩剥离核心指令（按剥离换算改持有条数），常规指令留到实体到期。
        /// 程序里没有核心指令的持有条数不变（剥离换算是恒等），但同样标记、不再被母机开火续命——母机已回到本地程序，接入时那份不能永远续下去。返回处理了几架。O(无人机数)。</summary>
        internal static int EmgFlashStrip(ref CombatData d, int ownerId)
        {
            int n = 0;
            for (int q = 0; q < d.Drones.Length; q++)
            {
                CombatDrone dr = d.Drones[q];
                int4 h = FlashHeadOf(ref d, dr.Weapon);
                if (dr.Owner != ownerId || dr.Strip != 0 || h.x < 0)
                {
                    continue;
                }
                int held = math.min(dr.Active, h.y);
                dr.Active = (byte)d.FlashMap[h.x + h.y + 1 + h.z + 1 + held];
                dr.Strip = 1;
                dr.Pause = (byte)math.min((int)dr.Pause, dr.Active);
                d.Drones[q] = dr;
                n++;
            }
            return n;
        }

        /// <summary>一架无人机（含哨戒桩）对 <paramref name="t"/> 开火一次：宿主 = 无人机（稳定 Id），程序 = 它所属武器的程序根节点；每次开火发 evt.fire（FGR-EMG-060 第 1 行“每架每次开火”），
        /// 随后的伤害发 evt.hit / evt.kill、被击者 evt.hurt 的来源 = 弹体（无人机是射击）。与 <see cref="EmgShotEnd"/> 成对。</summary>
        internal static void EmgDroneBegin(ref CombatData d, in CombatDrone dr, int owner, int t, int prog)
        {
            EmgHostBegin(ref d, prog, owner, dr.Id, EmgEntKind.Drone, (byte)EmgCode.SrcProjectile, dr.Pos, t);
            EmgCtx c = d.EmgCtx; // EMG1-TRIG-01：母载荷参数（无人机子弹）
            c.Damage = dr.Damage;
            c.Weapon = dr.Weapon;
            c.OwnerId = dr.Owner;
            c.Faction = (byte)dr.Faction;
            c.Carrier = (byte)CombatCarrier.Summon;
            c.MainTarget = d.Id[t];
            d.EmgCtx = c;
        }

        /// <summary>无人机 / 哨戒桩到期或母机没了、从列表移除：evt.expire（Code = 寿命；宿主 = 无人机，程序 = 它此刻持有的那份，EMG1-FLASH-01）。</summary>
        internal static void EmgDroneExpire(ref CombatData d, in CombatDrone dr)
        {
            if (d.EmgListen == 0u || dr.Weapon < 0 || dr.Weapon >= d.Weapons.Length)
            {
                return;
            }
            int prog = d.Weapons[FlashExecWeapon(ref d, dr)].Program;
            if (prog < 0)
            {
                return;
            }
            d.EmgCtx = new EmgCtx { Prog = prog, Ent = dr.Id, Owner = -1, EntKind = EmgEntKind.Drone };
            EmgShotEvent(ref d, EmgEventKind.Expire, 0, 0f, 0f, EmgCode.ExpireLife, dr.Pos);
            d.EmgCtx = EmgCtx.None;
        }

        /// <summary>一发弹体命中结算期间以它为宿主（DamageUnit / Kill 据此发 evt.hit、evt.hurt 的来源与链、evt.kill 的宿主）；没有程序也记来源。
        /// 每宿主计数从弹体拷进上下文，<see cref="EmgProjCtxEnd"/> 拷回。</summary>
        private static void EmgProjCtxBegin(ref CombatData d, in CombatProjectile pr) => d.EmgCtx = EmgProjHost(ref d, pr); // EMG1-TRIG-01：连同母载荷参数

        private static void EmgProjCtxEnd(ref CombatData d, ref CombatProjectile pr)
        {
            pr.TrigUsed = d.EmgCtx.TrigUsed;
            d.EmgCtx = EmgCtx.None;
        }

        private static void EmgCountProx(ref CombatData d)
        {
            EmgBusStats st = d.EmgStats[0];
            st.ProxEvals++;
            d.EmgStats[0] = st;
        }

        // ─────────────────────────────── 生产点（扣血 / 阵亡 / 积热 / 标记）───────────────────────────────

        /// <summary>DamageUnit 里（伤害已乘完易伤 / 攻城倍率）：本宿主（出手 / 弹体 / 无人机）造成的 evt.hit，受击单位的 evt.hurt。在扣血之前调用（目标此刻还活着），
        /// 所以同一次伤害的 evt.hit 一定排在 Kill 里的 evt.kill 之前。</summary>
        internal static void EmgOnDamage(ref CombatData d, int t, float damage, int attacker, int attackerId)
        {
            if (d.EmgListen == 0u)
            {
                return;
            }
            EmgCtx c = d.EmgCtx;
            // 这次伤害出自当前载体：出手者对得上（弹体 / 区域的出手者可能已不在，记为 −1，也算）；反伤、别人的伤害不算。
            bool ours = c.EntKind != EmgEntKind.None && attacker == c.Owner;
            if (ours && c.Prog >= 0)
            {
                c.Hits++;
                d.EmgCtx = c;
                EmgShotEvent(ref d, EmgEventKind.Hit, d.Id[t], damage, c.Hits, c.HitCode, d.Pos[t]);
                c = d.EmgCtx;
            }
            if (EmgSiteListens(ref d, EmgEventKind.Hurt))
            {
                float dir = 0f;
                if (attacker >= 0 && attacker < d.Count)
                {
                    double2 v = d.Pos[t] - d.Pos[attacker];
                    dir = math.lengthsq(v) > 1e-12 ? (float)math.atan2(v.y, v.x) : 0f;
                }
                EmgUnitEvent(ref d, EmgEventKind.Hurt, t, attackerId, damage, dir, ours ? c.HurtSrc : EmgCode.SrcOther, d.Pos[t]);
            }
        }

        /// <summary>Kill 里：阵亡者自己的 evt.death（Value = 阵亡时积热）；击杀者的 evt.kill（Value = 溢出伤害；宿主 = 造成这次致死伤害的载荷，不在载荷结算里时 = 击杀者武器程序的根节点）。
        /// Code = 阵亡者材质（实体档案材质随 EMG1-ENT-01 接上，现为 0）。</summary>
        internal static void EmgOnKill(ref CombatData d, int t, int killerId)
        {
            if (d.EmgListen == 0u)
            {
                return;
            }
            float overflow = d.EmgCtx.Overflow;
            d.EmgCtx.Overflow = 0f;
            EmgUnitEvent(ref d, EmgEventKind.Death, t, killerId, d.Heat[t], 0f, 0, d.Pos[t]);
            if (!EmgSiteListens(ref d, EmgEventKind.Kill) || killerId == 0)
            {
                return;
            }
            int ks = d.SlotOf(killerId);
            ushort mat = EmgUnitMaterial(ref d, t); // EMG1-ENT-01：evt.kill Code = 阵亡者材质（实体档案）
            if (d.EmgCtx.Prog >= 0 && ks >= 0 && ks == d.EmgCtx.Owner)
            {
                EmgShotEvent(ref d, EmgEventKind.Kill, d.Id[t], overflow, 0f, mat, d.Pos[t]);
            }
            else if (ks >= 0 && ks != t)
            {
                EmgUnitEvent(ref d, EmgEventKind.Kill, ks, d.Id[t], overflow, 0f, mat, d.Pos[t]);
            }
        }

        /// <summary>积热首次越过过热阈值：evt.overheat（Value = 积热）；地点有程序读 evt.cooled 时记下“过热过”，回落时 <see cref="EmgOnCool"/> 发 evt.cooled。</summary>
        internal static void EmgOnOverheat(ref CombatData d, int a, float heat)
        {
            if (d.EmgListen == 0u)
            {
                return;
            }
            EmgUnitEvent(ref d, EmgEventKind.Overheat, a, 0, heat, 0f, 0, d.Pos[a]);
            if (EmgSiteListens(ref d, EmgEventKind.Cooled))
            {
                EmgUnitState u = d.EmgUnit[a];
                u.HeatLatch = 1;
                d.EmgUnit[a] = u;
            }
        }

        /// <summary>散热之后：过热过、且积热回落到阈值 × emg.heat.cool_ratio 以下 → evt.cooled 一次。
        /// 过热之后换成了不会过热的武器（阈值 ≤ 0）或没有武器（<paramref name="overheatAt"/> 传 0）：“过热过”不再成立，静默清掉记号、不发 evt.cooled（新程序没见过这次过热）。</summary>
        internal static void EmgOnCool(ref CombatData d, int i, float heat, float overheatAt)
        {
            EmgUnitState u = d.EmgUnit[i];
            if (u.HeatLatch == 0)
            {
                return;
            }
            bool canHeat = overheatAt > 0f;
            float ratio = d.Config.EmgCoolRatio > 0f ? d.Config.EmgCoolRatio : EmgConst.DefaultCoolRatio;
            if (canHeat && heat >= overheatAt * ratio)
            {
                return;
            }
            u.HeatLatch = 0;
            d.EmgUnit[i] = u;
            if (canHeat)
            {
                EmgUnitEvent(ref d, EmgEventKind.Cooled, i, 0, heat, 0f, 0, d.Pos[i]);
            }
        }

        // ─────────────────────────────── 弹体每步（定时 / 近炸）───────────────────────────────

        /// <summary>带程序的在飞弹体：累计飞行秒数，越过定时读端的秒数时发 evt.timer；读 evt.prox 的弹体求最近敌对单位距离，进入半径时发 evt.prox（离开后可再进，受每宿主上限）。
        /// 没有程序读这两个事件码时不求值（FGR-EMG-062）。O(带程序的弹体 × 半径内格子)，只在内核。</summary>
        private static void EmgProjTick(ref CombatData d, ref CombatGrid grid, ref CombatProjectile pr, float dt)
        {
            float prevAge = pr.Age;
            pr.Age += dt;
            if (d.EmgListen == 0u)
            {
                return;
            }
            if (EmgSiteListens(ref d, EmgEventKind.Timer) && EmgReadSlot(ref d, pr.Prog, pr.Node, EmgEventKind.Timer) >= 0)
            {
                float secs = EmgReadParam(ref d, pr.Prog, pr.Node, EmgEventKind.Timer);
                if (secs > 0f && prevAge < secs && pr.Age >= secs)
                {
                    EmgProjEvent(ref d, ref pr, EmgEventKind.Timer, 0, EmgEntKind.None, secs, 0f, 0, pr.Pos);
                }
            }
            if (!EmgSiteListens(ref d, EmgEventKind.Prox) || EmgReadSlot(ref d, pr.Prog, pr.Node, EmgEventKind.Prox) < 0)
            {
                return;
            }
            float radius = EmgReadParam(ref d, pr.Prog, pr.Node, EmgEventKind.Prox);
            if (!(radius > 0f))
            {
                return;
            }
            EmgCountProx(ref d);
            byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            int cx0 = grid.CellOf(pr.Pos.x - radius), cx1 = grid.CellOf(pr.Pos.x + radius);
            int cy0 = grid.CellOf(pr.Pos.y - radius), cy1 = grid.CellOf(pr.Pos.y + radius);
            int nearest = -1;
            double best = double.MaxValue;
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
                        if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                        {
                            continue;
                        }
                        double dist = math.distance(pr.Pos, d.Pos[k]);
                        if (dist < best || (dist == best && k < nearest))
                        {
                            best = dist;
                            nearest = k;
                        }
                    }
                }
            }
            bool inside = nearest >= 0 && best <= radius;
            bool wasIn = (pr.Flags & EmgProjFlags.ProxIn) != 0;
            if (inside && !wasIn)
            {
                pr.Flags |= EmgProjFlags.ProxIn;
                EmgProjEvent(ref d, ref pr, EmgEventKind.Prox, d.Id[nearest], EmgEntKind.Unit, (float)best, 0f, 0, pr.Pos);
            }
            else if (!inside && wasIn)
            {
                pr.Flags &= ~EmgProjFlags.ProxIn;
            }
        }

        // ─────────────────────────────── 消费（一步里的 5d）───────────────────────────────

        /// <summary>按 Seq 先进先出处理本步预算内的事件；处理中新入队的追加在队尾，预算有剩本步继续。头指针过半时整体前移。</summary>
        internal static void StepEmgBus(ref CombatData d)
        {
            EmgScalars es = d.EmgS[0];
            int head = es.BusHead;
            int budget = d.Config.EmgBusPerStep > 0 ? d.Config.EmgBusPerStep : EmgConst.DefaultBusPerStep;
            int done = 0;
            while (done < budget && head < d.EmgBus.Length)
            {
                EmgEvent e = d.EmgBus[head++];
                es = d.EmgS[0];
                es.BusHead = head;
                d.EmgS[0] = es;
                EmgBusStats st = d.EmgStats[0];
                if (st.HasLast != 0 && unchecked(e.Seq - st.LastSeq) <= 0)
                {
                    st.OrderErrors++;
                }
                st.LastSeq = e.Seq;
                st.HasLast = 1;
                d.EmgStats[0] = st;
                EmgDispatch(ref d, e);
                done++;
            }
            es = d.EmgS[0];
            if (head >= d.EmgBus.Length)
            {
                d.EmgBus.Clear();
                head = 0;
            }
            else if (head >= 1024 && head * 2 >= d.EmgBus.Length)
            {
                d.EmgBus.RemoveRange(0, head);
                head = 0;
            }
            es.BusHead = head;
            d.EmgS[0] = es;
            EmgBusStats s2 = d.EmgStats[0];
            s2.Processed += done;
            s2.LastStepProcessed = done;
            if (d.EmgBus.Length > head)
            {
                s2.StepsDeferred++;
            }
            d.EmgStats[0] = s2;
        }

        /// <summary>
        /// 分派一条事件：宿主节点里读这个事件端口的指令行（按槽位序）最多唤起 emg.bus.max_fanout 条，超出的计 FanoutCut。
        /// 触发读端：链深已到 3 时不生成（DepthCut），否则唤起——子载荷生成在 EMG1-TRIG-01 接到这里（按子节点 Parent = 本节点且 TrigEvent = 本事件码）。
        /// 条件 / 感知读端：唤起，效果写端的执行在 EMG1-FW-01 / FW-02 接到这里（会生成东西的同样先过 <see cref="EmgCanGenerate"/>）。
        /// 阶段 1 没有规则表（EMG2-RULE-01）：没有宿主程序的事件（Prog = −1）只记录、不唤起。
        /// </summary>
        private static void EmgDispatch(ref CombatData d, in EmgEvent e)
        {
            int kind = (int)e.Kind;
            if (kind > 0 && kind < d.EmgLastByKind.Length)
            {
                d.EmgLastByKind[kind] = e;
            }
            if (e.Prog < 0 || e.Prog >= d.EmgPrograms.Length)
            {
                return;
            }
            EmgProgram p = d.EmgPrograms[e.Prog];
            int ni = p.FirstNode + e.Node;
            if (e.Node >= p.NodeCount || ni >= d.EmgNodes.Length)
            {
                return;
            }
            EmgNode n = d.EmgNodes[ni];
            ushort port = (ushort)(EmgConst.EventPortBase + kind);
            int maxFan = d.Config.EmgMaxFanout > 0 ? d.Config.EmgMaxFanout : EmgConst.DefaultMaxFanout;
            int woken = 0, cut = 0;
            bool trigWoken = false;
            EmgBusStats st = d.EmgStats[0];
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port != port || x.Role < EmgConst.RoleTrigger || x.Role > EmgConst.RoleLastRead)
                {
                    continue;
                }
                if (woken >= maxFan)
                {
                    cut++;
                    continue;
                }
                woken++;
                if (x.Role == EmgConst.RoleTrigger)
                {
                    d.EmgStats[0] = st;
                    if (!EmgCanGenerate(ref d, e))
                    {
                        st = d.EmgStats[0];
                        continue;
                    }
                    st = d.EmgStats[0];
                    st.TriggerWakes++;
                    trigWoken = true;
                }
                else
                {
                    st.ListenWakes++;
                }
            }
            if (cut > 0)
            {
                st.FanoutCut += cut;
                st.LastFanoutCutChain = e.Chain;
            }
            d.EmgStats[0] = st;
            // EMG1-TRIG-01：由这条事件记下的待生成子载荷——触发读端被唤起（链深未到 3）则唤起并发 evt.split，否则作废。
            // 子节点与触发指令按程序顺序一一对应；一个节点里读同一事件的指令 ≤ 容量 4，到不了扇出 8，所以“唤起了一条触发读端”即唤起这条事件的全部子节点。
            if (d.EmgS[0].ArmCursor < d.EmgPending.Length)
            {
                EmgResolvePending(ref d, e, trigWoken);
            }
        }
    }
}
