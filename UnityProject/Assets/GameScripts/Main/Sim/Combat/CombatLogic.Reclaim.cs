using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-RECL-01（FG19 FGR-EMG-115、FGR-EMG-230 第 6 / 7 行；FG19A 第 1.2 节第 27 行、第 1.3 节第 35 / 36 行；ADR-EMG-001 ⑦ 第 4 条）：
    /// - 回收修复 / 拆解钳（<see cref="EmgVerb.Salvage"/>，读 evt.kill、写 ent.wreck）：击毁目标后就地拆解它的残骸（残骸消失 = 掩体消失），按残骸质量换算维修量修复本机
    ///   （≤ 本机最大耐久 emg.reclaim.max_ratio、每 emg.reclaim.interval 秒最多一次、满耐久不拆）；外部血量单位（己方机器）发 HealRequest 由热更层改记录血量。
    ///   声明 param2 &gt; 0 的写端才修复（回收修复）；param2 = 0 只拆解（拆解钳，额外废料由热更层掉落的拆解钳加成给，FG8-LOOT-01）。
    /// - 桥接（<see cref="EmgVerb.Link"/>，读 evt.hit、写 prop.conductive）：结算点与 reach 米内最近的另一个导体之间拉一条导线（<see cref="EmgWire"/>），电、热沿它传，同类液体沿它均分，兼作拦索。
    /// - 蔓延（<see cref="EmgVerb.Spread"/>，读 evt.hit、写地面液体 modify）：结算点附近同种液体斑块在 param2 秒内扩到 amount 倍半径（份量不变），由斑块节拍推进（<see cref="TickPatch"/>）。
    /// 只认动词、端口码与参数，不认任何内容 ID（FGR-EMG-013）。逐单位 / 逐斑块的扫描都在内核（O(N) 只在 Main/Sim）。
    /// </summary>
    public static partial class CombatLogic
    {
        private static float ReclaimMaxRatioOf(ref CombatData d) => d.Config.EmgReclaimMaxRatio > 0f ? d.Config.EmgReclaimMaxRatio : EmgReclaimConst.DefaultMaxRatio;
        private static float ReclaimHpPerMassOf(ref CombatData d) => d.Config.EmgReclaimHpPerMass > 0f ? d.Config.EmgReclaimHpPerMass : EmgReclaimConst.DefaultHpPerMass;
        private static float ReclaimIntervalOf(ref CombatData d) => d.Config.EmgReclaimInterval > 0f ? d.Config.EmgReclaimInterval : EmgReclaimConst.DefaultInterval;

        /// <summary>0.1 秒刻度（与 FieldCdQ 同口径：取模 65535 再 +1，0 = 没有记录）。</summary>
        private static ushort TimeQ(double now) => (ushort)((long)math.floor(now * 10.0) % 65535 + 1);

        private static float SinceQ(ushort q, double now)
        {
            if (q == 0)
            {
                return float.MaxValue;
            }
            long cur = (long)math.floor(now * 10.0) % 65535 + 1;
            long diff = cur - q;
            if (diff < 0)
            {
                diff += 65535;
            }
            return diff * 0.1f;
        }

        // ─────────────────────────────── 回收修复 / 拆解钳 ───────────────────────────────

        /// <summary>
        /// evt.kill 门控的 ent.wreck 写端（EmgRootGated 调；同一事件同一节点的全部 salvage 写端合并处理一次：任一条 param2 &gt; 0 = 修复，任一条 param2 = 0 = 拆解钳）。
        /// 本次击杀的残骸 = 本步刚在阵亡者位置生成的那块（阵亡统一入口 EmgOnDeath 先生成残骸再发 evt.kill）。
        /// </summary>
        internal static void EmgSalvage(ref CombatData d, in EmgEvent e, in EmgCtx host, int firstInstr, int endInstr, ushort evPort)
        {
            if (!EmgEntOn(ref d))
            {
                return;
            }
            bool repair = false, claw = false;
            for (int i = firstInstr; i < endInstr; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Verb != (byte)EmgVerb.Salvage || (x.Flags & EmgInstrFlags.Overflow) != 0 || x.Role <= EmgConst.RoleLastRead
                    || !EmgGatedBy(ref d, firstInstr, endInstr, x.Ordinal, evPort))
                {
                    continue;
                }
                if (x.Param2 > 0f)
                {
                    repair = true;
                }
                else
                {
                    claw = true;
                }
            }
            int owner = host.Owner;
            if ((!repair && !claw) || owner < 0 || owner >= d.Count || !d.IsAlive(owner))
            {
                return;
            }
            EmgSalvageCore(ref d, owner, EmgKillWreck(ref d, e.Pos), repair, claw || EmgOwnerClaw(ref d, owner), e.Other);
        }

        private static bool EmgOwnerClaw(ref CombatData d, int slot)
        {
            int w = d.Weapon[slot];
            return w >= 0 && w < d.Weapons.Length && d.Weapons[w].ClawSalvage != 0;
        }

        /// <summary>
        /// 拆解钳（部件声明 evt.kill → ent.wreck，热更层写进武器 ClawSalvage，不进程序）：阵亡统一入口在 evt.kill 之后调用——击杀者带拆解钳、本次击杀的残骸还在就拆走（不修复）。
        /// 同装回收修复时程序写端先合并处理（<see cref="EmgSalvage"/>），残骸已被拆，这里找不到：同一残骸只拆一次。
        /// </summary>
        internal static void EmgPartSalvage(ref CombatData d, int t, int killerId)
        {
            int ks = killerId != 0 && EmgEntOn(ref d) ? d.SlotOf(killerId) : -1;
            if (ks < 0 || ks == t || !d.IsAlive(ks) || !EmgOwnerClaw(ref d, ks))
            {
                return;
            }
            int w = EmgKillWreck(ref d, d.Pos[t]);
            if (w >= 0)
            {
                EmgSalvageCore(ref d, ks, w, false, true, d.Id[t]);
            }
        }

        /// <summary>
        /// 子节点载荷（子弹药 / 子区）的事件：节点里被这个事件门控的回收修复 / 桥接 / 蔓延写端由内核执行（修复的是母机 host.Owner；FG19A 第 3 节混合固件、[触发, 桥接 / 蔓延]）。
        /// 同一事件同一节点的 salvage 写端合并处理一次。O(节点指令数²)，只在事件入队时。
        /// </summary>
        internal static void EmgChildWrites(ref CombatData d, in EmgEvent e, in EmgCtx host, ulong allow = ulong.MaxValue, bool any = false)
        {
            EmgProgram p = d.EmgPrograms[e.Prog];
            if (e.Node < 0 || e.Node >= p.NodeCount || p.FirstNode + e.Node >= d.EmgNodes.Length)
            {
                return;
            }
            EmgNode n = d.EmgNodes[p.FirstNode + e.Node];
            int first = n.FirstInstr, end = math.min(first + n.InstrCount, d.EmgInstrs.Length);
            ushort evPort = (ushort)(EmgConst.EventPortBase + (int)e.Kind);
            bool salvaged = false;
            ushort pingPort = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Ping);
            for (int i = first; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == pingPort && x.Role > EmgConst.RoleLastRead)
                {
                    // EMG3-SQD-01（FGR-EMG-133）：子弹药节点上的 evt.ping 写端（排在触发器后面的 [击杀回传] 等）在子弹药的生效事件时广播，脉冲点 = 事件位置
                    if ((x.Flags & EmgInstrFlags.Overflow) == 0 && EmgMetaAllowed(allow, x.Ordinal) && (any || EmgGatedBy(ref d, first, end, x.Ordinal, evPort)))
                    {
                        EmgPingOut(ref d, e, host, x);
                    }
                    continue;
                }
                bool follow = x.Verb == (byte)EmgVerb.Spawn && x.Port == EmgConst.PortProjectile; // EMG2-REACT-02（DEBT-EMG1FW01-03）：[子母弹, 追射] 子弹药命中后补射
                if ((x.Verb != (byte)EmgVerb.Salvage && x.Verb != (byte)EmgVerb.Link && x.Verb != (byte)EmgVerb.Spread && !follow)
                    || (x.Flags & EmgInstrFlags.Overflow) != 0 || x.Role <= EmgConst.RoleLastRead || !EmgMetaAllowed(allow, x.Ordinal) || !(any || EmgGatedBy(ref d, first, end, x.Ordinal, evPort)))
                {
                    continue;
                }
                if (follow)
                {
                    EmgQueueFollow(ref d, e, host, x);
                    continue;
                }
                if (x.Verb == (byte)EmgVerb.Link)
                {
                    EmgLink(ref d, e, host, x);
                }
                else if (x.Verb == (byte)EmgVerb.Spread)
                {
                    EmgSpread(ref d, e, host, x);
                }
                else if (!salvaged)
                {
                    salvaged = true;
                    EmgSalvage(ref d, e, host, first, end, evPort);
                }
            }
        }

        private static bool EmgGatedBy(ref CombatData d, int first, int end, byte ordinal, ushort evPort)
        {
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ordinal && r.Role == EmgConst.RoleTrigger && r.Port == evPort)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>本步刚生成、位置与阵亡者重合的残骸下标（从表尾往前找，遇到更早生成的就停）；没有返回 −1。</summary>
        private static int EmgKillWreck(ref CombatData d, double2 pos)
        {
            double now = d.Scalars[0].Time;
            for (int j = d.Wrecks.Length - 1; j >= 0; j--)
            {
                EmgWreck w = d.Wrecks[j];
                if (w.Born < now)
                {
                    break;
                }
                if (math.distancesq(w.Pos, pos) < 1e-8)
                {
                    return j;
                }
            }
            return -1;
        }

        /// <summary>
        /// 拆解一块残骸（<paramref name="w"/> = 残骸下标，−1 = 没有）。修复与拆解钳共用这一条规则：没有残骸 / 不可回收 → 不拆；
        /// 只有修复时，满耐久或冷却中不拆（残骸保留作掩体）；拆解钳照拆（不修复）。返回实际修复量（−1 = 没拆）。悬停原因写进拆解者的 ReclaimNote。
        /// </summary>
        internal static float EmgSalvageCore(ref CombatData d, int owner, int w, bool repair, bool claw, int victimId)
        {
            double now = d.Scalars[0].Time;
            EmgUnitState u = d.EmgUnit[owner];
            byte note;
            bool take;
            float amount = 0f;
            if (w < 0 || w >= d.Wrecks.Length)
            {
                note = EmgReclaimConst.NoteNoWreck;
                take = false;
            }
            else if (d.Wrecks[w].NoSalvage != 0)
            {
                note = EmgReclaimConst.NoteNoSalvage;
                take = false;
            }
            else if (repair && d.Hp[owner] >= d.MaxHp[owner])
            {
                note = EmgReclaimConst.NoteFull;
                take = claw;
            }
            else if (repair && SinceQ(u.ReclaimCdQ, now) < ReclaimIntervalOf(ref d) - 1e-4f)
            {
                note = EmgReclaimConst.NoteCooldown;
                take = claw;
            }
            else
            {
                note = EmgReclaimConst.NoteRepaired;
                take = true;
                if (repair)
                {
                    float mass = d.Wrecks[w].Mass;
                    amount = math.min(math.max(0f, mass) * ReclaimHpPerMassOf(ref d), math.min(ReclaimMaxRatioOf(ref d) * d.MaxHp[owner], math.max(0f, d.MaxHp[owner] - d.Hp[owner])));
                    u.ReclaimCdQ = TimeQ(now);
                }
            }
            if (repair)
            {
                u.ReclaimNote = take && amount <= 0f ? (byte)0 : note; // 悬停原因只对回收修复有意义（拆解钳不修复）；满耐久 / 冷却时被同装的拆解钳拆走：残骸没留下，不再写“留作掩体”
            }
            d.EmgUnit[owner] = u;
            if (!take)
            {
                return -1f;
            }
            EmgWreck wk = d.Wrecks[w];
            d.Wrecks.RemoveAt(w); // 保持生成先后顺序（满容量时下标 0 最早崩解）
            EmgScalars es = d.EmgS[0];
            es.Salvages++;
            es.HashDirty = 1;
            d.EmgS[0] = es;
            bool healed = amount > 0f;
            if (healed)
            {
                EmgReclaimHeal(ref d, owner, amount);
            }
            Gameplay(ref d, CombatEventKind.Salvaged, owner, victimId, amount, wk.Mass, wk.Pos, healed ? (byte)1 : (byte)0, 0);
            return amount;
        }

        /// <summary>
        /// 回收修复的维修量落到本机：外部血量单位（己方机器 / 首领）发 HealRequest（与 DamageRequest 对称，热更层改记录血量后 SyncMachineHealth 回写，内核镜像先不动，
        /// 下一次同步自然一致，不被冲掉）；普通单位直接加血。不走 <see cref="Heal"/>：那条会发 evt.hit（Code = 修复），回收修复不是一次命中，不再触发程序。
        /// </summary>
        private static void EmgReclaimHeal(ref CombatData d, int owner, float amount)
        {
            if (d.Has(owner, CombatUnitFlags.ExternalHealth))
            {
                Gameplay(ref d, CombatEventKind.HealRequest, owner, d.Id[owner], amount, d.Hp[owner], d.Pos[owner], 0, 0);
                return;
            }
            float hp = math.min(d.MaxHp[owner], d.Hp[owner] + amount);
            float healed = hp - d.Hp[owner];
            d.Hp[owner] = hp;
            if (d.Has(owner, CombatUnitFlags.Report))
            {
                Gameplay(ref d, CombatEventKind.Healed, owner, d.Id[owner], healed, hp, d.Pos[owner], 0, 0);
            }
        }

        // ─────────────────────────────── 桥接导线 ───────────────────────────────

        /// <summary>
        /// evt.hit 门控的 prop.conductive 写端：A 端 = 结算点的导体（命中单位脚下的导电地面斑块，没有就是命中的单位；力场载体 = 本机，本机成为电网一端）；B 端 = A 处 reach 米内最近的另一个导体（导电 ≥ 0.3 的单位 / 残骸 / 斑块，
        /// 已经和 A 接触的斑块 / 残骸不算）。同一对端点已有导线时续期。格斗 / 力场只按本次出手的主目标一次。满 <see cref="EmgWireConst.Capacity"/> 不生效并计数。
        /// a = 减速比例（fg.TbPortCarrier），amount = 秒数，param2 = 搜索半径（声明；力场取圈半径）。
        /// </summary>
        internal static void EmgLink(ref CombatData d, in EmgEvent e, in EmgCtx host, in EmgInstr x)
        {
            if (!EmgPatchesOn(ref d) || (host.PFlags & EmgProjFlags.Heal) != 0)
            {
                return;
            }
            bool aura = host.EntKind == EmgEntKind.Unit && host.Carrier == (byte)CombatCarrier.Aura;
            if (host.EntKind == EmgEntKind.Unit && (host.Carrier == (byte)CombatCarrier.Melee || aura) && e.Other != host.MainTarget)
            {
                return;
            }
            int a = aura ? host.Owner : e.OtherKind == EmgEntKind.Unit ? d.SlotOf(e.Other) : -1;
            if (a < 0 || a >= d.Count || !d.IsAlive(a))
            {
                return;
            }
            float reach = aura && host.ZoneRadius > 0f ? host.ZoneRadius : x.Param2 > 0f ? x.Param2 : EmgWireConst.DefaultReach;
            double2 pa = d.Pos[a];
            // A 端：结算点所在的导电地面斑块（站在水洼里 = 结算点的导体是这片水洼，同类液体才能沿导线均分）；不在导电斑块里 = 命中的单位本身
            byte kindA = EmgWireConst.EndUnit;
            int aid = d.Id[a], aidx = a;
            double2 ca = pa;
            float ra = 0f;
            if (!aura && !IsAir(ref d, a))
            {
                for (int j = 0; j < d.Patches.Length; j++)
                {
                    CombatZone z = d.Patches[j];
                    if (PatchAlive(z) && z.Layer == 0 && MatterOf(ref d, z.Matter).Conductive >= EmgRuleConst.DefaultConductMinStrength
                        && math.distance(z.Pos, pa) <= z.Radius && (kindA != EmgWireConst.EndPatch || z.EmgId < aid))
                    {
                        (kindA, aid, aidx, ca, ra) = (EmgWireConst.EndPatch, z.EmgId, j, z.Pos, z.Radius);
                    }
                }
            }
            // B 端：A 处 reach 米内最近的另一个导体（导电 ≥ 0.3 的单位 / 残骸 / 斑块）；已经和 A 接触的不算（本来就连着）
            byte kind = 0;
            int bid = 0, bidx = -1;
            double best = double.MaxValue;
            for (int u = 0; u < d.Count; u++)
            {
                if (u == a || u == host.Owner || !d.IsAlive(u) || IsAir(ref d, u) || EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, u)).Conductive < EmgRuleConst.DefaultConductMinStrength)
                {
                    continue;
                }
                double dist = math.distance(d.Pos[u], ca) - d.Radius[u] - ra;
                if (kindA == EmgWireConst.EndPatch && math.distance(d.Pos[u], ca) <= ra)
                {
                    continue;
                }
                dist = math.max(0.0, dist);
                if (dist <= reach && (dist < best || (dist == best && kind == EmgWireConst.EndUnit && d.Id[u] < bid)))
                {
                    (best, kind, bid, bidx) = (dist, EmgWireConst.EndUnit, d.Id[u], u);
                }
            }
            for (int j = 0; j < d.Wrecks.Length; j++)
            {
                EmgWreck wk = d.Wrecks[j];
                if (!(wk.Hp > 0f) || EmgProfileAt(ref d, wk.Profile).Conductive < EmgRuleConst.DefaultConductMinStrength)
                {
                    continue;
                }
                double dist = math.distance(wk.Pos, ca) - wk.Radius - ra;
                if (dist > 0.0 && dist <= reach && dist < best)
                {
                    (best, kind, bid, bidx) = (dist, EmgWireConst.EndWreck, wk.Id, j);
                }
            }
            for (int j = 0; j < d.Patches.Length; j++)
            {
                CombatZone z = d.Patches[j];
                if (!PatchAlive(z) || (kindA == EmgWireConst.EndPatch && j == aidx) || MatterOf(ref d, z.Matter).Conductive < EmgRuleConst.DefaultConductMinStrength)
                {
                    continue;
                }
                double dist = math.distance(z.Pos, ca) - z.Radius - ra;
                if (dist > 0.0 && dist <= reach && dist < best)
                {
                    (best, kind, bid, bidx) = (dist, EmgWireConst.EndPatch, z.EmgId, j);
                }
            }
            if (kind == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            float secs = x.Amount > 0f ? x.Amount : EmgWireConst.DefaultSeconds;
            EmgScalars es = d.EmgS[0];
            for (int i = 0; i < d.Wires.Length; i++)
            {
                EmgWire o = d.Wires[i];
                bool same = (o.KindA == kindA && o.A == aid && o.KindB == kind && o.B == bid) || (o.KindB == kindA && o.B == aid && o.KindA == kind && o.A == bid);
                if (same)
                {
                    o.Until = math.max(o.Until, now + secs);
                    d.Wires[i] = o;
                    return;
                }
            }
            if (d.Wires.Length >= EmgWireConst.Capacity)
            {
                es.WiresCapped++;
                d.EmgS[0] = es;
                return;
            }
            int owner = host.Owner >= 0 && host.Owner < d.Count ? d.Id[host.Owner] : host.OwnerId;
            d.Wires.Add(new EmgWire
            {
                Born = now, Until = now + secs, Id = es.NextWireId++, Owner = owner, Chain = e.Chain, A = aid, B = bid, KindA = kindA, KindB = kind,
                Slow = math.saturate(x.A > 0f ? x.A : EmgWireConst.DefaultSlow), Faction = host.Faction, Depth = (byte)math.min(e.Depth + 1, EmgConst.MaxDepth), IdxA = aidx, IdxB = bidx,
            });
            es.WiresBorn++;
            d.EmgS[0] = es;
        }

        /// <summary>导线一端此刻的下标（单位槽位 / 残骸下标 / 斑块槽位；用缓存、对不上再查）。−1 = 这一端已经消失。</summary>
        internal static int WireEndIndex(ref CombatData d, byte kind, int id, int cached)
        {
            switch (kind)
            {
                case EmgWireConst.EndUnit:
                {
                    int s = d.SlotOf(id);
                    return s >= 0 && d.IsAlive(s) ? s : -1;
                }
                case EmgWireConst.EndWreck:
                    if (cached >= 0 && cached < d.Wrecks.Length && d.Wrecks[cached].Id == id)
                    {
                        return d.Wrecks[cached].Hp > 0f ? cached : -1;
                    }
                    for (int j = 0; j < d.Wrecks.Length; j++)
                    {
                        if (d.Wrecks[j].Id == id)
                        {
                            return d.Wrecks[j].Hp > 0f ? j : -1;
                        }
                    }
                    return -1;
                case EmgWireConst.EndPatch:
                    if (cached >= 0 && cached < d.Patches.Length && PatchAlive(d.Patches[cached]) && d.Patches[cached].EmgId == id)
                    {
                        return cached;
                    }
                    for (int j = 0; j < d.Patches.Length; j++)
                    {
                        if (PatchAlive(d.Patches[j]) && d.Patches[j].EmgId == id)
                        {
                            return j;
                        }
                    }
                    return -1;
            }
            return -1;
        }

        internal static double2 WireEndPos(ref CombatData d, byte kind, int idx) =>
            kind == EmgWireConst.EndUnit ? d.Pos[idx] : kind == EmgWireConst.EndWreck ? d.Wrecks[idx].Pos : d.Patches[idx].Pos;

        /// <summary>每步（斑块 / 传导之前）：到期的导线移除（传导图恢复），任一端消失的立即断开；刷新两端下标缓存。O(导线数)，没有导线时不调。</summary>
        internal static void StepWires(ref CombatData d)
        {
            double now = d.Scalars[0].Time;
            EmgScalars es = d.EmgS[0];
            for (int i = d.Wires.Length - 1; i >= 0; i--)
            {
                EmgWire w = d.Wires[i];
                if (now >= w.Until)
                {
                    d.Wires.RemoveAt(i);
                    es.WiresExpired++;
                    continue;
                }
                int ia = WireEndIndex(ref d, w.KindA, w.A, w.IdxA), ib = WireEndIndex(ref d, w.KindB, w.B, w.IdxB);
                if (ia < 0 || ib < 0)
                {
                    d.Wires.RemoveAt(i);
                    es.WiresCut++;
                    continue;
                }
                if (ia != w.IdxA || ib != w.IdxB)
                {
                    w.IdxA = ia;
                    w.IdxB = ib;
                    d.Wires[i] = w;
                }
            }
            d.EmgS[0] = es;
        }

        /// <summary>拦索：不是这条导线端点的地面单位，中心到线段的距离 ≤ 半径 + emg.wire.rope_half_width 时速度 × (1 − Slow)（多条取最慢）。没有导线时 O(1)。</summary>
        internal static float EmgWireSlow(ref CombatData d, int i)
        {
            if (d.Wires.Length == 0 || IsAir(ref d, i))
            {
                return 1f;
            }
            float f = 1f;
            double2 p = d.Pos[i];
            double reach = d.Radius[i] + (d.Config.EmgWireRopeHalfWidth > 0f ? d.Config.EmgWireRopeHalfWidth : EmgWireConst.RopeHalfWidth);
            int self = d.Id[i];
            for (int k = 0; k < d.Wires.Length; k++)
            {
                EmgWire w = d.Wires[k];
                if ((w.KindA == EmgWireConst.EndUnit && w.A == self) || (w.KindB == EmgWireConst.EndUnit && w.B == self))
                {
                    continue; // 端点单位不算“穿过”（力场桥接的本机不被自己的导线减速）
                }
                int ia = WireEndIndex(ref d, w.KindA, w.A, w.IdxA), ib = WireEndIndex(ref d, w.KindB, w.B, w.IdxB);
                if (ia < 0 || ib < 0)
                {
                    continue;
                }
                double2 a = WireEndPos(ref d, w.KindA, ia), b = WireEndPos(ref d, w.KindB, ib), ab = b - a;
                double len2 = math.lengthsq(ab);
                double t = len2 > 1e-12 ? math.saturate(math.dot(p - a, ab) / len2) : 0.0;
                if (math.distancesq(p, a + ab * t) <= reach * reach)
                {
                    f = math.min(f, 1f - w.Slow);
                }
            }
            return f;
        }

        /// <summary>
        /// 电沿导线传（EmgElecBfs 每访问一个节点调一次）：节点是某条导线的一端时，把电按普通一跳传到另一端——斑块 / 残骸 × 0.7 × 接收方导电，单位分得 unit_share × 电荷 × (1 − 电抗)。
        /// 接收方导电不足 0.3 时按 0.3 计（导线本身就是导体，接在不导电的单位上也能把电送到它身上）。
        /// </summary>
        private static void EmgWireElec(ref CombatData d, ref NativeList<int4> queue, ref NativeList<float> qv, int4 nd, float q, float qmin, float bigR, float share)
        {
            byte kind = nd.x == 0 ? EmgWireConst.EndPatch : nd.x == 1 ? EmgWireConst.EndWreck : EmgWireConst.EndUnit;
            float fall = FalloffOf(ref d);
            for (int k = 0; k < d.Wires.Length; k++)
            {
                EmgWire w = d.Wires[k];
                byte ok;
                int oid, oidx;
                if (w.KindA == kind && w.A == nd.w)
                {
                    (ok, oid, oidx) = (w.KindB, w.B, w.IdxB);
                }
                else if (w.KindB == kind && w.B == nd.w)
                {
                    (ok, oid, oidx) = (w.KindA, w.A, w.IdxA);
                }
                else
                {
                    continue;
                }
                int j = WireEndIndex(ref d, ok, oid, oidx);
                if (j < 0)
                {
                    continue;
                }
                if (ok == EmgWireConst.EndPatch)
                {
                    EmgElecToPatch(ref d, ref queue, ref qv, j, q * fall * math.max(EmgRuleConst.DefaultConductMinStrength, MatterOf(ref d, d.Patches[j].Matter).Conductive), nd.z + 1, qmin, bigR);
                }
                else if (ok == EmgWireConst.EndWreck)
                {
                    EmgWreck wk = d.Wrecks[j];
                    float recv = q * fall * math.max(EmgRuleConst.DefaultConductMinStrength, EmgProfileAt(ref d, wk.Profile).Conductive);
                    if (recv >= qmin && recv > wk.Charge + 1e-4f)
                    {
                        wk.Charge = recv;
                        d.Wrecks[j] = wk;
                        queue.Add(new int4(1, j, nd.z + 1, wk.Id));
                        qv.Add(recv);
                    }
                }
                else if (!EmgMudded(ref d, j)) // C07：泥浆附着不进传导图，导线也送不到它身上
                {
                    EmgUnitState st = d.EmgUnit[j];
                    float recv = math.min(2f, q * share * (1f - EmgResist(ref d, j, 2)));
                    if (recv >= qmin && recv > st.AccA.z + 1e-4f && EmgAddAcc(ref d, j, EmgPhaseConst.Charge, recv - st.AccA.z, 0, raw: true, enterNow: false) > 0f)
                    {
                        queue.Add(new int4(2, j, nd.z + 1, d.Id[j]));
                        qv.Add(recv);
                    }
                }
            }
        }

        /// <summary>导线两端里带电（≥ q_min）的单位作电 BFS 起点（单位不站在导电斑块里时原本不是起点）。</summary>
        private static void EmgWireSeeds(ref CombatData d, ref NativeList<int4> queue, float qmin)
        {
            for (int k = 0; k < d.Wires.Length; k++)
            {
                EmgWire w = d.Wires[k];
                for (int side = 0; side < 2; side++)
                {
                    byte kind = side == 0 ? w.KindA : w.KindB;
                    if (kind != EmgWireConst.EndUnit)
                    {
                        continue;
                    }
                    int u = WireEndIndex(ref d, kind, side == 0 ? w.A : w.B, -1);
                    if (u < 0 || d.EmgUnit[u].AccA.z < qmin || EmgMudded(ref d, u))
                    {
                        continue;
                    }
                    bool dup = false;
                    for (int s = 0; s < queue.Length && !dup; s++)
                    {
                        dup = queue[s].x == 2 && queue[s].y == u;
                    }
                    if (!dup)
                    {
                        queue.Add(new int4(2, u, 0, d.Id[u]));
                    }
                }
            }
        }

        /// <summary>
        /// 传导节拍里的导线（热与液体）：两端都有温度（斑块 / 残骸）时按满接触交换 ΔT = 交换系数 × (T_b − T_a) × 对方份额（定温源不变）；一端是单位、另一端比常温高 emg.wire.heat_delta 度以上时，
        /// 单位按“站在热斑块里”同一口径吃热；两端是同种地面液体斑块时份量均分。返回访问数。
        /// </summary>
        private static int EmgWireTick(ref CombatData d)
        {
            float amb = AmbientOf(ref d), ex = ExchangeOf(ref d), gain = UnitGainOf(ref d);
            float heatDelta = d.Config.EmgWireHeatDelta > 0f ? d.Config.EmgWireHeatDelta : EmgWireConst.HeatDelta;
            int visits = 0;
            for (int k = 0; k < d.Wires.Length; k++)
            {
                EmgWire w = d.Wires[k];
                int ia = WireEndIndex(ref d, w.KindA, w.A, w.IdxA), ib = WireEndIndex(ref d, w.KindB, w.B, w.IdxB);
                if (ia < 0 || ib < 0)
                {
                    continue;
                }
                visits++;
                bool ua = w.KindA == EmgWireConst.EndUnit, ub = w.KindB == EmgWireConst.EndUnit;
                if (ua && ub)
                {
                    continue;
                }
                if (ua || ub)
                {
                    float hot = ua ? WireEndTemp(ref d, w.KindB, ib) : WireEndTemp(ref d, w.KindA, ia);
                    if (hot > amb + heatDelta)
                    {
                        EmgAddUnitHeat(ref d, ua ? ia : ib, gain * (hot - amb));
                    }
                    continue;
                }
                float ta = WireEndTemp(ref d, w.KindA, ia), tb = WireEndTemp(ref d, w.KindB, ib);
                bool fa = WireEndFixed(ref d, w.KindA, ia), fb = WireEndFixed(ref d, w.KindB, ib);
                float da = fa ? 0f : ex * (tb - ta) * (fb ? 1f : 0.5f), db = fb ? 0f : ex * (ta - tb) * (fa ? 1f : 0.5f);
                if (da != 0f)
                {
                    SetWireEndTemp(ref d, w.KindA, ia, ta + da);
                }
                if (db != 0f)
                {
                    SetWireEndTemp(ref d, w.KindB, ib, tb + db);
                }
                if (w.KindA == EmgWireConst.EndPatch && w.KindB == EmgWireConst.EndPatch)
                {
                    CombatZone za = d.Patches[ia], zb = d.Patches[ib];
                    EmgMatter m = MatterOf(ref d, za.Matter);
                    if (za.Matter == zb.Matter && m.Solid == 0 && za.Layer == 0 && zb.Layer == 0 && !PatchWorld(za) && !PatchWorld(zb) && math.abs(za.Amount - zb.Amount) > 1e-3f)
                    {
                        float mean = 0.5f * (za.Amount + zb.Amount);
                        EmgResizePatch(ref d, ia, mean);
                        EmgResizePatch(ref d, ib, mean);
                    }
                }
            }
            return visits;
        }

        private static float WireEndTemp(ref CombatData d, byte kind, int idx) =>
            kind == EmgWireConst.EndWreck ? d.Wrecks[idx].Temp : kind == EmgWireConst.EndPatch ? d.Patches[idx].Temp : AmbientOf(ref d);

        private static bool WireEndFixed(ref CombatData d, byte kind, int idx) =>
            kind == EmgWireConst.EndPatch && (MatterOf(ref d, d.Patches[idx].Matter).FixedTemp != 0 || PatchWorld(d.Patches[idx]));

        private static void SetWireEndTemp(ref CombatData d, byte kind, int idx, float t)
        {
            if (kind == EmgWireConst.EndWreck)
            {
                EmgWreck wk = d.Wrecks[idx];
                wk.Temp = t;
                d.Wrecks[idx] = wk;
            }
            else if (kind == EmgWireConst.EndPatch)
            {
                CombatZone z = d.Patches[idx];
                z.Temp = t;
                d.Patches[idx] = z;
            }
        }

        // ─────────────────────────────── 蔓延 ───────────────────────────────

        /// <summary>
        /// evt.hit 门控的地面液体 modify 写端（每个液体端口一条，只扩自己那种物质）：结算点（力场 = 本机处）a 米内（a = 0 时布区取落区半径）的同种地面液体斑块，
        /// 在 param2 秒内扩到当前半径 × amount（份量不变，不超过斑块半径上限）：同一块只扩一次（<see cref="EmgMatterConst.FlagSpread"/>）；自带增长的迁入斑块（读法 ZoneGrowth）不动；
        /// 存续不够 param2 秒的扩得快些（在开始干缩前走完）；已经在干缩的物质斑块不再扩。迁入斑块（漏油、冷却液等驻留区）同样扩，由 <see cref="StepLegacyPatches"/> 推进。格斗 / 力场只按本次出手的主目标一次。
        /// </summary>
        internal static void EmgSpread(ref CombatData d, in EmgEvent e, in EmgCtx host, in EmgInstr x)
        {
            byte matter = EmgMatterConst.MatterOfPort(x.Port);
            if (matter == 0 || !EmgPatchesOn(ref d) || (host.PFlags & EmgProjFlags.Heal) != 0)
            {
                return;
            }
            bool aura = host.EntKind == EmgEntKind.Unit && host.Carrier == (byte)CombatCarrier.Aura;
            if (host.EntKind == EmgEntKind.Unit && (host.Carrier == (byte)CombatCarrier.Melee || aura) && e.Other != host.MainTarget)
            {
                return;
            }
            double2 c = aura && host.Owner >= 0 && host.Owner < d.Count ? d.Pos[host.Owner] : e.Pos;
            float reach = x.A > 0f ? x.A : host.ZoneRadius > 0f ? host.ZoneRadius : 2f;
            float mult = x.Amount > 1f ? x.Amount : 2f;
            float secs = x.Param2 > 0f ? x.Param2 : 12f;
            float maxR = PatchMaxRadiusOf(ref d);
            double now = d.Scalars[0].Time;
            EmgScalars es = d.EmgS[0];
            for (int j = 0; j < d.Patches.Length; j++)
            {
                CombatZone z = d.Patches[j];
                if (z.Matter != matter || PatchWorld(z) || z.Layer != 0 || z.Growth != 0f || (z.PFlags & EmgMatterConst.FlagSpread) != 0 || math.distance(z.Pos, c) - z.Radius > reach)
                {
                    continue;
                }
                double usable = IsLegacy(z) ? z.Until - now : z.Until - 0.25 * (z.Until - z.Born) - now; // 物质斑块存续最后 25% 在干缩（TickPatch）
                float target = math.min(maxR, z.Radius * mult);
                if (usable <= 0.0 || target <= z.Radius + 1e-3f)
                {
                    continue;
                }
                z.R0 = target;
                z.Growth = (target - z.Radius) / (float)math.min(secs, usable);
                z.PFlags |= EmgMatterConst.FlagSpread;
                d.Patches[j] = z;
                es.SpreadStarts++;
                es.HashDirty = 1;
            }
            d.EmgS[0] = es;
        }
    }
}
