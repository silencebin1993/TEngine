using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG3-TMP-01 时间节拍常量（FG19 FGR-EMG-170～172；端口码与 fgdata_emg 同）。</summary>
    public static class EmgTempoConst
    {
        public const ushort PortBeat = 1301, PortSync = 1302;
        public const float DefaultBeat = 0.5f;
        /// <summary>evt.beat 的 Code：这台宿主本拍用掉了所属机器的 tmp.sync（每台机器每拍最多一次，FGR-EMG-172）。</summary>
        public const ushort CodeSync = 1;
        /// <summary>声明角色 inject（热更层 EmgRole.Inject = 8；内核行 Role 就是声明角色码）。en.kinetic 的动词随载体解析，这里按端口 + 角色判。</summary>
        public const byte RoleInject = 8;
    }

    /// <summary>
    /// EMG3-TMP-01 全局节拍（FG19 FGR-EMG-170～172；FG19A 第 1 节 fw_clock / fw_resonance 行与 5 载体读法）。
    /// 节拍按内核时间：拍序号 = ⌊模拟时间 ÷ emg.tempo.beat⌋（内核固定 60 Hz 步，倍速只改每帧步数，暂停不调 Step），所以与帧率、倍速、是否被观察无关；
    /// 拍序号存在快照段 24（<see cref="EmgScalars.BeatCount"/>，读档接续）。只有地点里有程序读 evt.beat 时才推进与出拍（没有监听者时 O(1)，StateHash 逐字不变）。
    /// 每拍对“读 evt.beat 的宿主”（在飞弹体 / 带程序的区域 / 无人机 / 格斗·力场·当场结算出手留下的程序锚点）各发一次 evt.beat：
    /// 读 tmp.beat 的指令只在“拍序号 % 周期 = 相位”的拍上执行（周期 = 读端幅度，相位 = param2，编译器把缺省相位写成程序序号 % 周期）；
    /// 同一台机器本拍有 ≥ 2 条不同的节拍指令执行时置 tmp.sync，读 tmp.sync 的指令在本拍执行，每台机器每拍最多一次。只认端口，不认内容 ID（FGR-EMG-013）。
    /// </summary>
    public static partial class CombatLogic
    {
        private const byte BeatProj = 0, BeatZone = 1, BeatDrone = 2, BeatAnchor = 3;

        private struct BeatHost
        {
            public byte Kind;
            public int Index;
            public int Owner;
            public int Prog;
            public int Node;
            /// <summary>读 evt.beat 的指令序号位 / 读 tmp.beat 的 / 其中本拍在相位上的 / 读 tmp.sync 的。</summary>
            public ulong Reads, Beats, OnPhase, Syncs;
            /// <summary>本宿主执行的序号位（谐振点只执行谐振那几条；计机器对齐仍按 <see cref="Reads"/>：谐振点带着载荷的节拍相位）。</summary>
            public ulong Mask;
        }

        private static float EmgBeatSeconds(ref CombatData d) => d.Config.EmgBeatSeconds > 0f ? d.Config.EmgBeatSeconds : EmgTempoConst.DefaultBeat;

        /// <summary>节拍指令的周期（读端幅度取整，≥ 1）与相位（param2 取整后对周期取模）。</summary>
        internal static int EmgBeatPeriod(in EmgInstr r) => math.max(1, (int)math.round(r.Amount));

        internal static int EmgBeatPhase(in EmgInstr r)
        {
            int p = EmgBeatPeriod(r);
            return ((int)math.round(r.Param2) % p + p) % p;
        }

        /// <summary>一个节点里本拍的节拍读端：读 evt.beat / tmp.beat / tmp.sync 的序号位，以及 tmp.beat 在相位上的序号位。溢出行不算。O(节点指令数)。</summary>
        private static void EmgBeatScan(ref CombatData d, int first, int end, long beat, out ulong reads, out ulong beats, out ulong onPhase, out ulong syncs)
        {
            reads = beats = onPhase = syncs = 0UL;
            ushort beatEvt = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Beat);
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Role < EmgConst.RoleTrigger || r.Role > EmgConst.RoleLastRead || (r.Flags & EmgInstrFlags.Overflow) != 0)
                {
                    continue;
                }
                ulong bit = EmgOrdBit(r.Ordinal);
                if (r.Port == beatEvt)
                {
                    reads |= bit;
                }
                else if (r.Port == EmgTempoConst.PortBeat)
                {
                    beats |= bit;
                    if (beat % EmgBeatPeriod(r) == EmgBeatPhase(r))
                    {
                        onPhase |= bit;
                    }
                }
                else if (r.Port == EmgTempoConst.PortSync)
                {
                    syncs |= bit;
                }
            }
        }

        /// <summary>本拍放行的序号位：不在相位上的节拍指令、本拍没有对齐（或机器的对齐已被用掉）时读 tmp.sync 的指令关掉。</summary>
        private static ulong EmgBeatAllow(ulong beats, ulong onPhase, ulong syncs, bool sync) => ~((beats & ~onPhase) | (sync ? 0UL : syncs));

        /// <summary>总线分派 evt.beat 时同一判定（按事件带的拍序号与 Code 重算，无状态）：这条读端的固件本拍不执行。</summary>
        internal static bool EmgBeatBlocked(ref CombatData d, int first, int end, byte ordinal, in EmgEvent e)
        {
            long beat = e.Code2 > 0 ? d.EmgS[0].BeatCount : (long)math.round(e.Value); // 积压保护合并过的事件 Value 是累加值，取当前拍
            EmgBeatScan(ref d, first, end, beat, out _, out ulong beats, out ulong onPhase, out ulong syncs);
            ulong allow = EmgBeatAllow(beats, onPhase, syncs, e.Code == EmgTempoConst.CodeSync);
            return !EmgMetaAllowed(allow, ordinal);
        }

        /// <summary>5c''. 每步：越过拍界时推进拍序号并出拍。只在地点有程序读 evt.beat 时求值（否则 O(1) 返回，拍序号不动）。</summary>
        internal static void StepEmgBeat(ref CombatData d)
        {
            if ((d.EmgListen & EmgBit(EmgEventKind.Beat)) == 0u || d.EmgPrograms.Length == 0)
            {
                return;
            }
            EmgScalars es = d.EmgS[0];
            long beat = (long)math.floor((d.Scalars[0].Time + 1e-6) / EmgBeatSeconds(ref d));
            if (beat <= es.BeatCount)
            {
                return;
            }
            es.BeatCount = beat; // 读 ≤ 17 版快照 / 监听者刚出现：直接接到当前拍（只出这一拍，不补发）
            d.EmgS[0] = es;
            EmgBeatHosts(ref d, beat);
        }

        private static void EmgBeatHosts(ref CombatData d, long beat)
        {
            var hosts = new NativeList<BeatHost>(16, Allocator.Temp);
            double now = d.Scalars[0].Time;
            for (int i = 0; i < d.Projectiles.Length; i++)
            {
                CombatProjectile pr = d.Projectiles[i];
                if (pr.Prog >= 0 && (pr.Flags & EmgProjFlags.Sub) == 0) // 子弹药由放出点的节拍锚点代为执行（EmgBeatAnchorSub），不重复出拍
                {
                    EmgBeatCollect(ref d, ref hosts, BeatProj, i, pr.Owner, pr.Prog, pr.Node, beat, ulong.MaxValue);
                }
            }
            for (int i = 0; i < d.Zones.Length; i++)
            {
                CombatZone z = d.Zones[i];
                if (z.EmgProgRef > 0)
                {
                    EmgBeatCollect(ref d, ref hosts, BeatZone, i, z.Owner, z.EmgProgRef - 1, z.EmgNode, beat, ulong.MaxValue);
                }
            }
            for (int i = 0; i < d.Drones.Length; i++)
            {
                CombatDrone dr = d.Drones[i];
                if (dr.Weapon >= 0 && dr.Weapon < d.Weapons.Length)
                {
                    EmgBeatCollect(ref d, ref hosts, BeatDrone, i, dr.Owner, d.Weapons[FlashExecWeapon(ref d, dr)].Program, 0, beat, ulong.MaxValue);
                }
            }
            for (int i = 0; i < d.MetaRun.Length; i++)
            {
                EmgMetaRun m = d.MetaRun[i];
                if (m.Mode == EmgMetaConst.RunBeat && now <= m.Until + 1e-9)
                {
                    EmgBeatCollect(ref d, ref hosts, BeatAnchor, i, m.Unit, m.Prog, m.Node, beat, m.Mask);
                }
            }
            if (hosts.Length == 0)
            {
                hosts.Dispose();
                return;
            }
            // 每台机器本拍执行的节拍指令（按程序序号去重：同一条指令装在几发在飞弹体上仍是 1 条，卡片负向“只有一条节拍指令时不触发 tmp.sync”）
            var machine = new NativeHashMap<int, ulong>(hosts.Length, Allocator.Temp);
            for (int h = 0; h < hosts.Length; h++)
            {
                BeatHost b = hosts[h];
                ulong exec = EmgBeatExecNow(ref d, b, beat, b.Reads & b.Beats & b.OnPhase);
                if (exec != 0UL)
                {
                    machine[b.Owner] = (machine.TryGetValue(b.Owner, out ulong m) ? m : 0UL) | exec;
                }
            }
            var used = new NativeHashSet<int>(4, Allocator.Temp); // 本拍已用掉 tmp.sync 的机器（只在这一步内有效，不进快照）
            NativeHashSet<int> group = EmgGroupSync(ref d, machine); // EMG3-SQD-01（FGR-EMG-172 中继组部分）：同一中继组里两台机器同拍执行节拍指令也对齐
            for (int h = 0; h < hosts.Length; h++)
            {
                BeatHost b = hosts[h];
                // EMG3-E2E-01（DEBT-EMG3TMP01-01）：谐振点（只放行谐振的锚点）的对齐单独记一份——原来谐振与齐射等由同一宿主一次执行，挪到命中点后不能抢走在飞弹体 / 无人机本拍的对齐
                int key = b.Kind == BeatAnchor && b.Mask != ulong.MaxValue ? ~b.Owner : b.Owner;
                bool sync = machine.TryGetValue(b.Owner, out ulong em) && (math.countbits(em) >= 2 || (group.IsCreated && group.Contains(b.Owner))) && !used.Contains(key);
                ulong allow = EmgBeatAllow(b.Beats, b.OnPhase, b.Syncs, sync) & b.Mask;
                if ((b.Reads & allow) == 0UL)
                {
                    continue; // 这台宿主本拍没有要执行的节拍读端：不入队
                }
                bool took = sync && (b.Syncs & b.Reads & allow) != 0UL;
                if (took)
                {
                    used.Add(key);
                }
                else
                {
                    allow = EmgBeatAllow(b.Beats, b.OnPhase, b.Syncs, false) & b.Mask;
                }
                EmgBeatEmit(ref d, b, beat, allow, took);
            }
            used.Dispose();
            if (group.IsCreated)
            {
                group.Dispose();
            }
            machine.Dispose();
            hosts.Dispose();
        }

        /// <summary><paramref name="mask"/>：本宿主执行的序号位（<see cref="BeatHost.Mask"/>）。节拍锚点 = 记录里的 Mask（普通锚点全放行；谐振点只放行谐振那几条，EMG3-E2E-01），其余宿主全放行。</summary>
        private static void EmgBeatCollect(ref CombatData d, ref NativeList<BeatHost> hosts, byte kind, int index, int owner, int prog, int node, long beat, ulong mask)
        {
            if (!EmgNodeSpan(ref d, prog, node, out int first, out int end))
            {
                return;
            }
            EmgBeatScan(ref d, first, end, beat, out ulong reads, out ulong beats, out ulong onPhase, out ulong syncs);
            if (reads != 0UL)
            {
                hosts.Add(new BeatHost { Kind = kind, Index = index, Owner = owner, Prog = prog, Node = node, Reads = reads, Beats = beats, OnPhase = onPhase, Syncs = syncs, Mask = mask });
            }
        }

        /// <summary>
        /// EMG3-FW-02（DEBT-EMG3TMP01-04，FGR-EMG-172“本拍执行”）：计入 tmp.sync 的节拍指令只算本拍真的执行的——去掉被元程序条件 / 随机关掉、被延后的（与出拍结算同一函数
        /// <see cref="EmgMetaAllowNow"/>，同一组哈希键），以及读 chs.* condition、出手者此刻没有该底盘状态的。没有元程序、没有底盘条件时原样返回。O(节点指令数)，只在出拍时。
        /// </summary>
        private static ulong EmgBeatExecNow(ref CombatData d, in BeatHost b, long beat, ulong exec)
        {
            if (exec == 0UL || !EmgNodeSpan(ref d, b.Prog, b.Node, out int first, out int end))
            {
                return exec;
            }
            EmgCtx host = EmgBeatHostOf(ref d, b, out double2 pos, out _);
            if (EmgHasMeta(ref d, b.Prog))
            {
                var e = new EmgEvent { Kind = EmgEventKind.Beat, EntKind = host.EntKind, Ent = host.Ent, Prog = b.Prog, Node = (byte)b.Node, Chain = host.Chain, Value = beat, Pos = pos };
                int self = host.Owner >= 0 && host.Owner < d.Count ? host.Owner : -1;
                int target = EmgMetaTarget(ref d, e, host);
                uint zoneMat = host.EntKind == EmgEntKind.Zone ? EmgZoneSubj(ref d, host.Ent, ref target, out _) : EmgNotZone;
                exec &= EmgMetaAllowNow(ref d, b.Prog, b.Node, EmgEventKind.Beat, self, target, host.Chain, host.Ent, zoneMat);
            }
            for (ulong m = exec; m != 0UL; m &= m - 1UL)
            {
                byte o = (byte)math.tzcnt(m);
                if (EmgChsBlocked(ref d, host.Owner, first, end, o))
                {
                    exec &= ~EmgOrdBit(o);
                }
            }
            return exec;
        }

        /// <summary>对一台宿主发 evt.beat（Value = 拍序号，Code = 是否用掉了机器的 tmp.sync）：只放行本拍执行的序号位（根节点效果写端、子节点写端、触发放出的子载荷都按它）。
        /// 恒真事件不受“每宿主同一事件码 ≤ 3 次”限制（节拍本身按周期限频，FGR-EMG-065 管的是命中类事件的连发）。</summary>
        private static void EmgBeatEmit(ref CombatData d, in BeatHost b, long beat, ulong allow, bool sync)
        {
            EmgCtx host = EmgBeatHostOf(ref d, b, out double2 pos, out float2 dir);
            var e = new EmgEvent
            {
                Kind = EmgEventKind.Beat, Depth = host.Depth, EntKind = host.EntKind, Ent = host.Ent, Prog = b.Prog, Node = (byte)b.Node, Chain = host.Chain,
                Code = sync ? EmgTempoConst.CodeSync : (ushort)0, Value = beat, Pos = pos,
            };
            EmgBusStats st = d.EmgStats[0];
            st.BeatsEmitted++;
            st.BeatSyncs += sync ? 1 : 0;
            d.EmgStats[0] = st;
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = host;
            EmgEmitHosted(ref d, e, host, dir, allow);
            d.EmgCtx = saved;
            EmgBeatKinetic(ref d, b, host, pos, allow, sync);
        }

        /// <summary>出拍宿主的上下文、位置与朝向（出拍结算与“本拍执行”计数共用）。</summary>
        private static EmgCtx EmgBeatHostOf(ref CombatData d, in BeatHost b, out double2 pos, out float2 dir)
        {
            EmgCtx host;
            switch (b.Kind)
            {
                case BeatProj:
                {
                    CombatProjectile pr = d.Projectiles[b.Index];
                    host = EmgProjHost(ref d, pr);
                    pos = pr.Pos;
                    dir = EmgDir((double2)pr.Vel);
                    break;
                }
                case BeatZone:
                {
                    CombatZone z = d.Zones[b.Index];
                    host = EmgZoneHost(ref d, z);
                    pos = z.Pos;
                    dir = float2.zero;
                    break;
                }
                case BeatDrone:
                {
                    CombatDrone dr = d.Drones[b.Index];
                    host = new EmgCtx
                    {
                        Prog = b.Prog, Ent = dr.Id, EntKind = EmgEntKind.Drone, Owner = d.SlotOf(dr.Owner), Damage = dr.Damage, Weapon = dr.Weapon, OwnerId = dr.Owner,
                        Faction = (byte)dr.Faction, Carrier = (byte)CombatCarrier.Summon, HurtSrc = (byte)EmgCode.SrcProjectile, HitCode = EmgCode.HitDirect,
                    };
                    pos = dr.Pos;
                    float2 v = EmgDir(dr.Pos - dr.Prev);
                    dir = v;
                    break;
                }
                default:
                {
                    EmgMetaRun m = d.MetaRun[b.Index];
                    host = new EmgCtx
                    {
                        Prog = m.Prog, Chain = m.Chain, Ent = m.Host, Owner = d.SlotOf(m.Unit), Node = m.Node, Depth = m.Depth, EntKind = (EmgEntKind)m.HostKind, Damage = m.Damage,
                        Weapon = m.Weapon, OwnerId = m.Unit, Faction = m.Faction, Carrier = m.Carrier, MainTarget = m.Other, ZoneRadius = m.ZoneRadius, ZoneLife = m.ZoneLife,
                        PFlags = (EmgProjFlags)m.PFlags, HitCode = EmgCode.HitDirect,
                    };
                    pos = m.Pos;
                    dir = m.Dir;
                    break;
                }
            }
            return host;
        }

        /// <summary>
        /// en.kinetic × evt.beat（FG19A fw_resonance 行“余振”）：根节点上写 en.kinetic（inject）、且同一条固件读 evt.beat 触发的指令，本拍在宿主位置余振一次——
        /// 半径 = param2 米内的敌对单位各吃 宿主伤害 × 幅度 的动能伤害（宿主 = 锚点 / 谐振点 / 区域；在飞弹体与无人机不在此刻位置余振）；用掉 tmp.sync 的拍半径 × 2。出手者已不在时不结算（没有攻击者归属）。O(节点指令数 + 单位数)，只在出拍时。
        /// </summary>
        private static void EmgBeatKinetic(ref CombatData d, in BeatHost b, in EmgCtx host, double2 pos, ulong allow, bool sync)
        {
            if (b.Kind == BeatProj || b.Kind == BeatDrone)
            {
                return; // EMG3-E2E-01（DEBT-EMG3TMP01-01，FG19A 第 132 行）：射弹 / 无人机在“最近一次命中点”余振（命中时留的谐振点），不在弹体 / 无人机此刻的位置
            }
            if (!EmgNodeSpan(ref d, b.Prog, b.Node, out int first, out int end) || host.Owner < 0 || host.Owner >= d.Count || !d.IsAlive(host.Owner))
            {
                return;
            }
            for (int i = first; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port != EmgConst.PortKinetic || x.Role != EmgTempoConst.RoleInject || (x.Flags & EmgInstrFlags.Overflow) != 0
                    || (EmgOrdBit(x.Ordinal) & b.Reads & allow) == 0UL || !(x.Amount > 0f))
                {
                    continue;
                }
                float radius = math.max(0.5f, x.Param2) * (sync && (EmgOrdBit(x.Ordinal) & b.Syncs) != 0UL ? 2f : 1f);
                float dmg = math.max(0f, host.Damage) * x.Amount;
                if (!(dmg > 0f))
                {
                    continue;
                }
                byte want = host.Faction == (byte)CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
                EmgCtx saved = d.EmgCtx;
                d.EmgCtx = EmgCtx.None; // 余振不做新宿主：不再发 evt.hit 触发子载荷（避免节拍 → 命中 → 子载荷的自激）
                for (int k = 0; k < d.Count; k++)
                {
                    if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || math.distance(d.Pos[k], pos) > radius + d.Radius[k])
                    {
                        continue;
                    }
                    DamageUnit(ref d, k, dmg, host.Owner);
                }
                d.EmgCtx = saved;
                EmgBusStats st = d.EmgStats[0];
                st.BeatPulses++;
                d.EmgStats[0] = st;
                return; // 同一宿主同一拍只余振一次（多条谐振按去重口径只生效一次）
            }
        }

        /// <summary>
        /// 子弹药（子弹 / 余波 / 无人机子弹，寿命 ≤ 0.5 秒）所在节点读 evt.beat：在放出点留 2 秒节拍锚点代它按拍执行（与格斗“挥击点留 2 秒程序锚点”同一读法，FG19A fw_clock 行），
        /// 否则嵌套在子弹药里的节拍指令几乎撑不到自己的相位（零死指令，FGR-EMG-221）。按端口判，不按内容 ID。
        /// </summary>
        /// <remarks>EMG3-FW-01 审查修复（第 1 轮，FG-GAP-201）：小场脉冲（存续 0.3 秒，跨不到拍界）同样留锚点，载体记力场（<paramref name="carrier"/>）；子区域活得过一个周期，自己做宿主，不留。</remarks>
        internal static void EmgBeatAnchorSub(ref CombatData d, in EmgSpawn s, float2 dir, float damage, byte carrier = (byte)CombatCarrier.Projectile)
        {
            if (s.Prog < 0 || (d.EmgListen & EmgBit(EmgEventKind.Beat)) == 0u || EmgReadSlot(ref d, s.Prog, s.Node, EmgEventKind.Beat) < 0)
            {
                return;
            }
            EmgBeatAnchorPut(ref d, new EmgMetaRun
            {
                Unit = s.Owner, Prog = s.Prog, Chain = s.Chain, Node = s.Node, Host = s.Owner, HostKind = (byte)EmgEntKind.Unit, Mode = EmgMetaConst.RunBeat,
                Depth = s.Depth, Mask = ulong.MaxValue, Pos = s.Pos, Dir = dir, Other = s.Other, Weapon = s.Weapon, Damage = math.max(0f, damage), Faction = s.Faction,
                Carrier = carrier, PFlags = (ushort)(s.Flags & EmgProjFlags.Heal), ZoneRadius = math.max(0f, s.ZoneRadius), ZoneLife = math.max(0f, s.ZoneLife),
            });
        }

        /// <summary>格斗 / 力场 / 当场结算的出手读 evt.beat 时，在结算点留“程序锚点”（emg.meta.loop_anchor_seconds，缺省 2 秒，FG19A 第 1 节 5 载体读法），锚点存续期间按全局节拍执行。
        /// 力场 = 脉冲点（出手者处），格斗 / 即时射弹 = 挥击 / 命中点。记在元程序运行态表（快照段 23，Mode = RunBeat），读档接续。</summary>
        internal static void EmgBeatAnchor(ref CombatData d, int prog, int a, int t, in EmgCtx host)
        {
            if (prog < 0 || (d.EmgListen & EmgBit(EmgEventKind.Beat)) == 0u || EmgReadSlot(ref d, prog, 0, EmgEventKind.Beat) < 0)
            {
                return;
            }
            EmgBeatAnchorPut(ref d, new EmgMetaRun
            {
                Unit = d.Id[a], Prog = prog, Chain = host.Chain, Node = 0, Host = d.Id[a], HostKind = (byte)EmgEntKind.Unit, Mode = EmgMetaConst.RunBeat,
                Depth = host.Depth, Mask = ulong.MaxValue, Pos = host.Carrier == (byte)CombatCarrier.Aura ? d.Pos[a] : d.Pos[t], Dir = EmgDir(d.Pos[t] - d.Pos[a]),
                Other = d.Id[t], Weapon = host.Weapon, Damage = math.max(0f, host.Damage), Faction = host.Faction, Carrier = host.Carrier, PFlags = (ushort)host.PFlags,
                ZoneRadius = math.max(0f, host.ZoneRadius), ZoneLife = math.max(0f, host.ZoneLife),
            });
        }

        /// <summary>
        /// 每台机器、每个程序节点只留一个节拍锚点：已有就刷新到这一次的结算点与 2 秒时限（连射不叠锚点，节拍执行次数按机器而不是按出手 / 份数增长；锚点数 ≤ 机器数 × 节点数）。
        /// 谐振点（<see cref="EmgResonanceHit"/>）另按宿主（机器 / 每架无人机）与放行位各留一个，时限 = 2 拍（<paramref name="seconds"/>）。
        /// O(运行态条数)，只在读 evt.beat 的出手 / 子弹药放出 / 谐振命中时。
        /// </summary>
        private static void EmgBeatAnchorPut(ref CombatData d, EmgMetaRun rec, double seconds = 0.0)
        {
            double until = d.Scalars[0].Time + (seconds > 0.0 ? seconds : d.Config.EmgMetaAnchor > 0f ? d.Config.EmgMetaAnchor : EmgMetaConst.DefaultAnchor);
            rec.DueAt = rec.Until = until;
            for (int i = 0; i < d.MetaRun.Length; i++)
            {
                EmgMetaRun m = d.MetaRun[i];
                if (m.Mode == EmgMetaConst.RunBeat && m.Unit == rec.Unit && m.Prog == rec.Prog && m.Node == rec.Node && m.Host == rec.Host && m.HostKind == rec.HostKind && m.Mask == rec.Mask)
                {
                    d.MetaRun[i] = rec;
                    return;
                }
            }
            if (d.MetaRun.Length >= EmgConst.PendingCapacity)
            {
                EmgStat(ref d, 4);
                return;
            }
            d.MetaRun.Add(rec);
        }

        /// <summary>节点里的谐振位：写 en.kinetic（inject）、且同一条指令读 evt.beat（<paramref name="reads"/>）的序号位。O(节点指令数)。</summary>
        private static ulong EmgResonanceBits(ref CombatData d, int first, int end, ulong reads)
        {
            ulong bits = 0UL;
            for (int i = first; i < end && reads != 0UL; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == EmgConst.PortKinetic && x.Role == EmgTempoConst.RoleInject && (x.Flags & EmgInstrFlags.Overflow) == 0 && x.Amount > 0f)
                {
                    bits |= EmgOrdBit(x.Ordinal) & reads;
                }
            }
            return bits;
        }

        /// <summary>
        /// EMG3-E2E-01（DEBT-EMG3TMP01-01；FG19A 第 132 行谐振 5 载体读法）：射弹 / 无人机这一下命中，载荷节点上有谐振时，在命中点留（刷新）一个 2 拍的谐振点——
        /// 一个只放行谐振那几条的节拍锚点（快照段 23 已有的 RunBeat 记录，不加新内核状态）：射弹按机器、无人机按每架各留一个，之后每拍在“最近一次命中点”余振，2 拍后移除。
        /// 格斗 / 即时射弹的锚点本来就在命中点（<see cref="EmgBeatAnchor"/>），力场在圈内、布区在区域里，不经这里。没有监听 evt.beat 时 O(1)。
        /// </summary>
        internal static void EmgResonanceHit(ref CombatData d, int prog, int node, int unitId, int hostId, EmgEntKind hostKind, double2 at, int other, float damage, byte faction, int weapon,
            byte carrier, byte depth, int chain)
        {
            if (prog < 0 || (d.EmgListen & EmgBit(EmgEventKind.Beat)) == 0u || !EmgNodeSpan(ref d, prog, node, out int first, out int end))
            {
                return;
            }
            EmgBeatScan(ref d, first, end, 0, out ulong reads, out _, out _, out _);
            ulong bits = EmgResonanceBits(ref d, first, end, reads);
            if (bits == 0UL)
            {
                return;
            }
            EmgBeatAnchorPut(ref d, new EmgMetaRun
            {
                Unit = unitId, Prog = prog, Chain = chain, Node = (byte)node, Host = hostId, HostKind = (byte)hostKind, Mode = EmgMetaConst.RunBeat, Depth = depth, Mask = bits,
                Pos = at, Other = other, Weapon = weapon, Damage = math.max(0f, damage), Faction = faction, Carrier = carrier,
            }, 2.0 * EmgBeatSeconds(ref d));
        }
    }
}
