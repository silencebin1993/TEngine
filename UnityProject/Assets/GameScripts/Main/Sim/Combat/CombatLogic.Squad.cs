using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG3-SQD-01 跨机协同常量（FG19 FGR-EMG-132 / 133 / 172；端口码与 fgdata_emg 同）。</summary>
    public static class EmgSquadConst
    {
        public const float DefaultRange = 12f;
        public const int DefaultPerSecond = 4;
        public const int Channels = 4;
        /// <summary>链记录 <see cref="EmgChainRec.Aux"/>（节点种类 Relay）：转发 / 限流未转发 / 信号脉冲（Aux = 2 + 频道）。</summary>
        public const byte AuxForward = 0, AuxCapped = 1, AuxPing = 2;

        /// <summary>白名单（FGR-EMG-132）：只有这 6 种事件的 trigger 读端能被中继转发；evt.fire / evt.overheat 等一律不转发。</summary>
        public static bool Relayable(EmgEventKind k) =>
            k == EmgEventKind.Hit || k == EmgEventKind.Kill || k == EmgEventKind.Marked || k == EmgEventKind.Hurt || k == EmgEventKind.Bounce || k == EmgEventKind.Pierce;

        /// <summary>频道 = 幅度取整夹到 0～3（evt.ping 写端的广播频道 / 读端的收听频道）。</summary>
        public static int ChannelOf(float amount) => math.clamp((int)math.round(amount), 0, Channels - 1);
    }

    /// <summary>EMG3-SQD-01：一台装信号中继的机器（快照段 25）。限流按模拟秒计窗：同一个模拟秒（⌊时间⌋）内最多 emg.relay.per_second 次。</summary>
    public struct EmgRelay
    {
        public int Unit;
        /// <summary>当前计窗的模拟秒（⌊时间⌋）与本窗已转发次数。</summary>
        public long WinSec;
        public int Used;
        /// <summary>本场累计：转发触发次数、限流未转发次数（“多余触发计数可见”，悬停显示）。</summary>
        public long Forwarded;
        public long Dropped;
    }

    /// <summary>
    /// EMG3-SQD-01 跨机协同（FG19 FGR-EMG-130～133、172 中继组、180 跨机链；ADR-EMG-001 ④：转发只遍历装中继的机器）。
    /// 中继转发：友机（机器）产生白名单事件时，中继范围内装中继的机器（<see cref="CombatData.Relays"/>）若根节点有读这个事件的 trigger 读端，就把它当本机事件执行——
    /// 宿主 = 中继机器自己的程序（只有一道信号：每台机器只跑自己装的程序），位置 = 原事件位置，链沿用原事件的因果链、链深 + 1（A、B 互为中继也到链深 3 即停），
    /// 每台中继每个模拟秒最多 emg.relay.per_second 次（多出来的计 Dropped、记链“限流”节点），每次转发执行方积一发的热量。
    /// 信号脉冲：写 evt.ping 的指令在它的生效事件时广播频道 0～3；与发出者同一中继组（某台活着的中继机器 12 米内同时有两者）的友机，根节点读该频道 evt.ping 的 trigger 读端被触发。
    /// 没有装中继的机器时每个入口一条比较返回（<see cref="CombatData.Relays"/> 为空），行为与哈希逐字不变。只认端口码与角色，不认内容 ID（FGR-EMG-013）。
    /// </summary>
    public static partial class CombatLogic
    {
        private static float EmgRelayRange(ref CombatData d) => d.Config.EmgRelayRange > 0f ? d.Config.EmgRelayRange : EmgSquadConst.DefaultRange;

        private static int EmgRelayPerSecond(ref CombatData d) => d.Config.EmgRelayPerSecond > 0 ? d.Config.EmgRelayPerSecond : EmgSquadConst.DefaultPerSecond;

        private static bool EmgIsMachine(ref CombatData d, int i) => i >= 0 && i < d.Count && d.Kind[i] == (byte)CombatUnitKind.Machine;

        /// <summary>程序根节点里读 <paramref name="port"/>、角色 = trigger 的指令序号位（<paramref name="channel"/> ≥ 0 时只要收听这个频道的）。没有返回 0。O(根节点指令数)。</summary>
        private static ulong EmgRootTrigGate(ref CombatData d, int prog, EmgEventKind k, int channel)
        {
            if (prog < 0 || prog >= d.EmgPrograms.Length || (d.EmgPrograms[prog].ListenMask & EmgBit(k)) == 0 || !EmgNodeSpan(ref d, prog, 0, out int first, out int end))
            {
                return 0UL;
            }
            ushort port = (ushort)(EmgConst.EventPortBase + (int)k);
            ulong gate = 0UL;
            for (int i = first; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == port && x.Role == EmgConst.RoleTrigger && (x.Flags & EmgInstrFlags.Overflow) == 0 && (channel < 0 || EmgSquadConst.ChannelOf(x.Amount) == channel))
                {
                    gate |= EmgOrdBit(x.Ordinal);
                }
            }
            return gate;
        }

        /// <summary>
        /// 生产点（<see cref="EmgShotEvent"/> / <see cref="EmgUnitEvent"/> / <see cref="EmgProjEvent"/> / 没有程序的出手命中）：<paramref name="src"/> 槽位的机器产生了事件 <paramref name="k"/>。
        /// 不论 src 自己有没有程序读它。O(中继机器数 × 根节点指令数)；没有中继机器时一条比较返回。
        /// </summary>
        internal static void EmgRelayOut(ref CombatData d, EmgEventKind k, int src, byte depth, int chain, int other, EmgEntKind otherKind, float value, float value2, ushort code, double2 pos)
        {
            if (d.Relays.Length == 0 || !EmgSquadConst.Relayable(k) || !EmgSiteListens(ref d, k) || !EmgIsMachine(ref d, src))
            {
                return;
            }
            float range = EmgRelayRange(ref d);
            int cap = EmgRelayPerSecond(ref d);
            double now = d.Scalars[0].Time;
            long sec = (long)math.floor(now + 1e-9);
            var parent = new EmgEvent { Kind = k, Depth = depth, Chain = chain, EntKind = EmgEntKind.Unit, Ent = d.Id[src], Pos = pos };
            for (int r = 0; r < d.Relays.Length; r++)
            {
                EmgRelay rl = d.Relays[r];
                int b = d.SlotOf(rl.Unit);
                // 中继机器阵亡 / 不在场 / 是事件源自己（自己的事件本来就触发）/ 不同阵营 / 超出范围 / 过热锁定（执行方开不了火）：不转发
                if (b < 0 || b == src || !d.IsAlive(b) || d.Faction[b] != d.Faction[src] || math.distance(d.Pos[b], d.Pos[src]) > range + 1e-6
                    || d.Has(b, CombatUnitFlags.Overheated))
                {
                    continue;
                }
                int prog = EmgUnitProg(ref d, b);
                ulong gate = EmgRootTrigGate(ref d, prog, k, -1);
                if (gate == 0UL)
                {
                    continue; // 这台中继的程序没有读这个事件的 trigger 读端（只 role = trigger 获得 sqd.relay 作用域）
                }
                if (!EmgCanGenerate(ref d, parent))
                {
                    return; // 链深已到 3：不再转发（A、B 互为中继时的兜底；计 DepthCut 并记链尾）
                }
                if (parent.Chain == 0)
                {
                    EmgScalars es = d.EmgS[0];
                    parent.Chain = es.NextChain++; // 事件源没有程序（没开链）：转发开一条新链，链首就是“由中继转发”
                    d.EmgS[0] = es;
                }
                if (rl.WinSec != sec)
                {
                    rl.WinSec = sec;
                    rl.Used = 0;
                }
                EmgBusStats st = d.EmgStats[0];
                if (rl.Used >= cap)
                {
                    rl.Dropped++;
                    d.Relays[r] = rl;
                    st.RelayCapped++;
                    d.EmgStats[0] = st;
                    EmgChainLog(ref d, EmgChainNode.Relay, parent.Chain, depth, (int)k, d.Id[src], pos, rl.Unit, EmgSquadConst.AuxCapped);
                    continue;
                }
                rl.Used++;
                rl.Forwarded++;
                d.Relays[r] = rl;
                st.RelayForwarded++;
                d.EmgStats[0] = st;
                EmgChainLog(ref d, EmgChainNode.Relay, parent.Chain, depth, (int)k, d.Id[src], pos, rl.Unit, EmgSquadConst.AuxForward);
                EmgRelayFire(ref d, b, prog, gate, new EmgEvent
                {
                    Kind = k, Depth = (byte)(depth + 1), EntKind = EmgEntKind.Unit, Ent = rl.Unit, OtherKind = other != 0 ? otherKind : EmgEntKind.None, Other = other,
                    Prog = prog, Node = 0, Chain = parent.Chain, Code = code, Value = value, Value2 = value2, Pos = pos,
                });
            }
        }

        /// <summary>
        /// 执行方 <paramref name="b"/> 按自己的程序根节点执行一条转发来的事件（只放行 <paramref name="gate"/> 里的 trigger 读端那几条指令）：积一发的热量（热量记在执行方），
        /// 宿主上下文换成执行方（效果、子载荷归属执行方），子载荷朝事件位置放。
        /// </summary>
        private static void EmgRelayFire(ref CombatData d, int b, int prog, ulong gate, in EmgEvent e)
        {
            int w = d.Weapon[b];
            if (w >= 0 && w < d.Weapons.Length)
            {
                EmgRelayHeat(ref d, b, d.Weapons[w]);
            }
            EmgCtx host = EmgUnitHost(ref d, b);
            host.Prog = prog;
            host.Chain = e.Chain;
            host.Depth = e.Depth;
            host.HitCode = EmgCode.HitDirect;
            host.MainTarget = e.OtherKind == EmgEntKind.Unit ? e.Other : 0;
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = host;
            EmgEmitHosted(ref d, e, host, EmgDir(e.Pos - d.Pos[b]), gate);
            d.EmgCtx = saved;
        }

        /// <summary>转发 / 脉冲触发的一次执行：执行方积一发的热量（与 <see cref="AddShotHeat"/> 同口径，不扣补给），越过过热阈值同样过热并发 evt.overheat。</summary>
        private static void EmgRelayHeat(ref CombatData d, int b, in CombatWeapon wp)
        {
            float shot = ShotHeat(ref d, b, wp);
            if (!(shot > 0f))
            {
                return;
            }
            float heat = d.Heat[b] + shot;
            d.Heat[b] = heat;
            if (wp.OverheatAt > 0f && heat >= wp.OverheatAt && !d.Has(b, CombatUnitFlags.Overheated))
            {
                d.Set(b, CombatUnitFlags.Overheated, true);
                Cue(ref d, CombatEventKind.Overheat, b, 0, heat, d.Pos[b], 0);
                EmgOnOverheat(ref d, b, heat);
            }
        }

        /// <summary>
        /// 中继组（FGR-EMG-133 / 172）：有一台活着的同阵营中继机器，<paramref name="a"/> 与 <paramref name="b"/> 都在它的中继范围内（中继机器本身也算在自己的组里）。
        /// O(中继机器数)。
        /// </summary>
        internal static bool EmgSameRelayGroup(ref CombatData d, int a, int b)
        {
            if (d.Relays.Length == 0 || a < 0 || b < 0 || a >= d.Count || b >= d.Count || d.Faction[a] != d.Faction[b])
            {
                return false;
            }
            float range = EmgRelayRange(ref d) + 1e-6f;
            for (int r = 0; r < d.Relays.Length; r++)
            {
                int m = d.SlotOf(d.Relays[r].Unit);
                if (m >= 0 && d.IsAlive(m) && d.Faction[m] == d.Faction[a] && math.distance(d.Pos[m], d.Pos[a]) <= range && math.distance(d.Pos[m], d.Pos[b]) <= range)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 写 evt.ping 的指令在生效事件 <paramref name="e"/> 时广播频道（幅度取整 0～3）：同一中继组里根节点收听这个频道的友机（机器）各触发一次，
        /// 事件位置 = 脉冲点（<paramref name="e"/> 的位置），链沿用、链深 + 1。不在任何中继组里 = 没有接收者（只计广播）。
        /// O(单位数 × 中继机器数)，只在脉冲发出时（事件触发、本身受链深限制）。
        /// </summary>
        internal static void EmgPingOut(ref CombatData d, in EmgEvent e, in EmgCtx host, in EmgInstr x)
        {
            int src = host.Owner;
            if (!EmgIsMachine(ref d, src) || !d.IsAlive(src))
            {
                return;
            }
            // EMG3-FW-02（FG19A N30 / N32）：同一条指令的 ent.wreck condition = 事件处留下了残骸（没留不广播、不计广播）；prop.heat sense = 脉冲点取被标记敌人里积热最高的一个
            double2 at = e.Pos;
            if (EmgNodeSpan(ref d, e.Prog, e.Node, out int first, out int end))
            {
                for (int j = first; j < end; j++)
                {
                    EmgInstr r = d.EmgInstrs[j];
                    if (r.Ordinal != x.Ordinal)
                    {
                        continue;
                    }
                    if (r.Role == EmgConst.RoleCondition && r.Port == EmgConst.PortEntWreck && !EmgWreckAtEvent(ref d, e))
                    {
                        return;
                    }
                    if (r.Role == EmgConst.RoleSense && r.Port == EmgConst.PortPropHeat)
                    {
                        at = EmgHottestMarked(ref d, src, e.Kind == EmgEventKind.Marked && e.OtherKind == EmgEntKind.Unit ? d.SlotOf(e.Other) : -1, e.Pos);
                    }
                }
            }
            int channel = EmgSquadConst.ChannelOf(x.Amount);
            EmgBusStats st = d.EmgStats[0];
            st.PingsSent++;
            d.EmgStats[0] = st;
            if (d.Relays.Length == 0 || !EmgSiteListens(ref d, EmgEventKind.Ping) || !EmgCanGenerate(ref d, e))
            {
                return;
            }
            for (int r = 0; r < d.Count; r++)
            {
                if (r == src || !d.IsAlive(r) || !EmgIsMachine(ref d, r) || d.Has(r, CombatUnitFlags.Overheated))
                {
                    continue;
                }
                int prog = EmgUnitProg(ref d, r);
                ulong gate = EmgRootTrigGate(ref d, prog, EmgEventKind.Ping, channel);
                if (gate == 0UL || !EmgSameRelayGroup(ref d, src, r))
                {
                    continue;
                }
                st = d.EmgStats[0];
                st.PingsDelivered++;
                d.EmgStats[0] = st;
                EmgChainLog(ref d, EmgChainNode.Relay, e.Chain, e.Depth, (int)EmgEventKind.Ping, d.Id[src], at, d.Id[r], (byte)(EmgSquadConst.AuxPing + channel));
                EmgRelayFire(ref d, r, prog, gate, new EmgEvent
                {
                    Kind = EmgEventKind.Ping, Depth = (byte)(e.Depth + 1), EntKind = EmgEntKind.Unit, Ent = d.Id[r], OtherKind = EmgEntKind.Unit, Other = d.Id[src],
                    Prog = prog, Node = 0, Chain = e.Chain, Code = (ushort)channel, Value = channel, Pos = at,
                });
            }
        }

        /// <summary>
        /// ent.wreck condition（FG19A N30“击杀并留下残骸”）：击杀事件 = 这次阵亡生成了残骸（evt.kill Value2 = 1，阵亡统一入口 EmgOnDeath 写入；
        /// 同一事件里排在前面的回收修复已把它拆走也算留下过，结果与指令顺序无关）；其他事件 = 事件处 1 米内有残骸。O(残骸数)，只在脉冲发出时。
        /// </summary>
        private static bool EmgWreckAtEvent(ref CombatData d, in EmgEvent e)
        {
            if (e.Kind == EmgEventKind.Kill)
            {
                return e.Value2 > 0.5f;
            }
            for (int j = 0; j < d.Wrecks.Length; j++)
            {
                if (math.distancesq(d.Wrecks[j].Pos, e.Pos) <= 1.0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// prop.heat sense 作脉冲点（FG19A N32）：发出者的敌对单位（不含中立）里此刻被标记、积热最高的一个；同热时优先本次被标记的目标 <paramref name="target"/>，
        /// 再取槽位小的；没有就用事件位置。O(单位数)，只在脉冲发出时。
        /// </summary>
        private static double2 EmgHottestMarked(ref CombatData d, int src, int target, double2 fallback)
        {
            double now = d.Scalars[0].Time;
            int best = target >= 0 && target < d.Count && EmgMarkedFoe(ref d, src, target, now) ? target : -1;
            for (int u = 0; u < d.Count; u++)
            {
                if (EmgMarkedFoe(ref d, src, u, now) && (best < 0 || d.Heat[u] > d.Heat[best]))
                {
                    best = u;
                }
            }
            return best >= 0 ? d.Pos[best] : fallback;
        }

        private static bool EmgMarkedFoe(ref CombatData d, int src, int u, double now) =>
            d.IsAlive(u) && d.Faction[u] != d.Faction[src] && d.Faction[u] != (byte)CombatFaction.Neutral && d.MarkedUntil[u] > now;

        /// <summary>
        /// FGR-EMG-172 中继组部分（DEBT-EMG3TMP01-02）：本拍各机器执行的节拍指令（<paramref name="machine"/>：单位 ID → 序号位）里，和别的机器同在一个中继组的，
        /// 组内合计 ≥ 2 条就对这些机器置 tmp.sync。返回置位的机器集合（调用方查 IsCreated 后 Dispose）。没有中继机器时不分配、返回 default。O(本拍出拍机器数² × 中继机器数)。
        /// </summary>
        internal static NativeHashSet<int> EmgGroupSync(ref CombatData d, NativeHashMap<int, ulong> machine)
        {
            if (d.Relays.Length == 0 || machine.Count < 2)
            {
                return default;
            }
            var synced = new NativeHashSet<int>(4, Allocator.Temp);
            NativeArray<int> ids = machine.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
            {
                int si = d.SlotOf(ids[i]);
                int total = math.countbits(machine[ids[i]]);
                for (int j = 0; j < ids.Length && total < 2; j++)
                {
                    if (j != i && EmgSameRelayGroup(ref d, si, d.SlotOf(ids[j])))
                    {
                        total += math.countbits(machine[ids[j]]);
                    }
                }
                if (total >= 2)
                {
                    synced.Add(ids[i]);
                }
            }
            ids.Dispose();
            return synced;
        }
    }
}
