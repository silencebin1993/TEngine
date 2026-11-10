using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG4-HIJ-01 信号劫持（FG19 FGR-EMG-163；ADR-EMG-001 ⑰；快照段 27）。
    /// - 来源：写 hij.hack 的固件命中（<see cref="EmgHackInject"/>，role = inject）给敌对目标加入侵度（累积量 phs.hack，经 <see cref="EmgAddAcc"/> 统一入口，× (1 − 入侵抗性)）。
    /// - 拦截：<see cref="Kill"/> 在发 Killed / SiegeKilled / TurretKill 之前调 <see cref="EmgTryHijack"/>：入侵度 ≥ phs.hijacked 阈值、敌方非结构非外部血量、击杀者是存活的己方单位、
    ///   入侵抗性 &lt; 1（精英 / 首领默认 1）、本方劫持数 &lt; 上限 → 不阵亡：原地以 hp_ratio 耐久重启为己方、行为改 Hijacked、退出攻城队伍（职能 / 队伍清零），发 Hijacked 玩法事件；
    ///   不成立的（上限满 / 抗性）发 HijackRefused 写原因，照常阵亡。
    /// - 结束：期满或劫持者阵亡（<see cref="StepHijacks"/>）、或被击毁，都回到 <see cref="Kill"/>：先 <see cref="EmgHijackRelease"/> 还原原阵营 / 行为 / 攻城职能，
    ///   发 HijackEnded，再照常走 Killed（击杀者 = 劫持者）、残骸与掉落——掉落与击杀计数只在这时结算一次。
    /// 全部 O(劫持数)（≤ 每名玩家 2 台），空表时每步一次长度比较。规则只看端口 / 单位属性，不认内容 ID（FGR-EMG-013）。
    /// </summary>
    public static partial class CombatLogic
    {
        private static float HijackSecondsOf(ref CombatData d) => d.Config.EmgHijackSeconds > 0f ? d.Config.EmgHijackSeconds : EmgHijackConst.DefaultSeconds;
        private static float HijackHpRatioOf(ref CombatData d) => d.Config.EmgHijackHpRatio > 0f ? math.min(1f, d.Config.EmgHijackHpRatio) : EmgHijackConst.DefaultHpRatio;
        internal static int HijackMaxOf(ref CombatData d) => d.Config.EmgHijackMax > 0 ? d.Config.EmgHijackMax : EmgHijackConst.DefaultMax;

        /// <summary>精英与首领（外部血量的敌方本体、优先级 0 的首领核心）：按单位属性，不按 ID（FGR-EMG-103 / 161）。</summary>
        internal static bool HackRanked(ref CombatData d, int u) =>
            d.Faction[u] == (byte)CombatFaction.Hostile && (d.Has(u, CombatUnitFlags.Elite) || d.Has(u, CombatUnitFlags.ExternalHealth) || d.Priority[u] == 0);

        /// <summary>精英与首领的入侵抗性下限 emg.hijack.elite_resist（缺省 1 = 不可劫持，FG19 第 14 节 #8 推荐默认）。</summary>
        internal static float EliteHackResistOf(ref CombatData d) => d.Config.EmgHijackEliteResist > 0f ? math.saturate(d.Config.EmgHijackEliteResist) : 1f;

        internal static int EmgHijackIndexOf(ref CombatData d, int unitId)
        {
            for (int h = 0; h < d.Hijacks.Length; h++)
            {
                if (d.Hijacks[h].Unit == unitId)
                {
                    return h;
                }
            }
            return -1;
        }

        /// <summary>EMG4-FW-01（FG19A N40）：这把武器的程序带 <see cref="EmgProgramFlags.HijackFlash"/>（编译器按端口判：hij.hijacked condition + grm.inherit 写端，不认内容 ID）。</summary>
        private static bool EmgHijackFlashOf(ref CombatData d, int w)
        {
            int p = w >= 0 && w < d.Weapons.Length ? d.Weapons[w].Program : -1;
            return p >= 0 && p < d.EmgPrograms.Length && (d.EmgPrograms[p].Flags & EmgProgramFlags.HijackFlash) != 0;
        }

        /// <summary>EMG4-FW-01（hij.hijacked condition）：单位此刻处于被信号劫持（劫持方向的记录，夺控不算）。O(劫持数)。</summary>
        internal static bool EmgHijackedNow(ref CombatData d, int i)
        {
            int h = i >= 0 && i < d.Count ? EmgHijackIndexOf(ref d, d.Id[i]) : -1;
            return h >= 0 && !d.Hijacks[h].IsSeize;
        }

        /// <summary>EMG4-FW-01（hij.seized condition）：单位此刻被夺控（夺控方向的记录，含瘫痪）。O(劫持数)。</summary>
        internal static bool EmgSeizedNow(ref CombatData d, int i)
        {
            int h = i >= 0 && i < d.Count ? EmgHijackIndexOf(ref d, d.Id[i]) : -1;
            return h >= 0 && d.Hijacks[h].IsSeize;
        }

        /// <summary>某名玩家（劫持者所在阵营只有己方一种，按全局计）此刻劫持着几台（只数信号劫持方向；夺控记录同表但不占劫持上限，EMG4-HIJ-02）。O(表长)。</summary>
        internal static int EmgHijackCount(ref CombatData d)
        {
            int n = 0;
            for (int h = 0; h < d.Hijacks.Length; h++)
            {
                n += d.Hijacks[h].IsSeize ? 0 : 1;
            }
            return n;
        }

        private static float SeizeRecoverOf(ref CombatData d) => d.Config.EmgSeizeRecoverRatio > 0f ? math.min(1f, d.Config.EmgSeizeRecoverRatio) : EmgHijackConst.DefaultRecoverRatio;

        /// <summary>hij.hack 注入：只对敌对目标（注入方是己方、目标是敌方；反之亦然）加入侵度。返回实际增加量。</summary>
        internal static float EmgHackInject(ref CombatData d, int t, in EmgCtx c, float amount)
        {
            if (c.Owner < 0 || c.Owner >= d.Count || d.Faction[c.Owner] == d.Faction[t] || d.Faction[t] == (byte)CombatFaction.Neutral)
            {
                return 0f;
            }
            if (d.Faction[c.Owner] == (byte)CombatFaction.Player && EmgResist(ref d, t, EmgPhaseConst.ResHack) >= 1f)
            {
                // 精英 / 首领（入侵抗性下限 1，含外部血量首领）：被打时按抗性拒绝并写原因（FG19 第 14 节 #8 推荐默认）；入侵度不会累积，阵亡时不再判。热更层按单位去重通知
                Gameplay(ref d, CombatEventKind.HijackRefused, t, d.Id[c.Owner], EmgHijackCount(ref d), HijackMaxOf(ref d), d.Pos[t], EmgHijackConst.RefuseResist, 0);
                return 0f;
            }
            float got = EmgAddAcc(ref d, t, EmgPhaseConst.Hack, amount, d.Id[c.Owner]);
            EmgChainLog(ref d, EmgChainNode.Inject, c.Chain, c.Depth, EmgConst.PortHack, d.Id[c.Owner], d.Pos[t], d.Id[t]);
            return got;
        }

        /// <summary>阵亡统一入口发击杀事件之前：劫持是否成立（成立时单位已重启为己方，调用方直接返回）。</summary>
        internal static bool EmgTryHijack(ref CombatData d, int t, int killerId)
        {
            if (d.Faction[t] != (byte)CombatFaction.Hostile || !d.EmgPhases.IsCreated || d.EmgPhases.Length <= EmgPhaseConst.Hack)
            {
                return false;
            }
            float acc = AccOf(d.EmgUnit[t], EmgPhaseConst.Hack);
            if (!(acc > 0f) || acc < d.EmgPhases[EmgPhaseConst.Hack].Enter)
            {
                return false;
            }
            int ks = d.SlotOf(killerId);
            if (ks < 0 || ks == t || !d.IsAlive(ks) || d.Faction[ks] != (byte)CombatFaction.Player
                || d.Kind[t] == (byte)CombatUnitKind.Structure || d.Has(t, CombatUnitFlags.ExternalHealth))
            {
                return false; // 不是被己方单位击毁 / 结构 / 外部血量单位：照常阵亡（不写拒绝原因：玩家没有“劫持它”的出手）
            }
            int max = HijackMaxOf(ref d);
            int have = EmgHijackCount(ref d);
            if (EmgResist(ref d, t, EmgPhaseConst.ResHack) >= 1f)
            {
                Gameplay(ref d, CombatEventKind.HijackRefused, t, killerId, have, max, d.Pos[t], EmgHijackConst.RefuseResist, 0);
                return false;
            }
            if (have >= max)
            {
                Gameplay(ref d, CombatEventKind.HijackRefused, t, killerId, have, max, d.Pos[t], EmgHijackConst.RefuseCap, 0);
                return false;
            }
            // 刷写带宽（FGR-EMG-036）：占用 = 原生程序条数；带宽不够时截到放得下（武器换成刷写变体表里“前 k 条”那一把，没有变体表 = 不占带宽）
            int w = d.Weapon[t];
            int4 head = FlashHeadOf(ref d, w);
            int held = 0, hflags = 0;
            int free = FlashBandwidthOf(ref d) - FlashUsedBy(ref d, killerId, -1);
            int mw = d.Weapon[ks];
            int4 mh = FlashHeadOf(ref d, mw);
            if (mh.x >= 0 && mh.z > 0 && free > 0 && EmgHijackFlashOf(ref d, mw))
            {
                // EMG4-FW-01（FG19A N40 劫持中继）：劫持者程序带“hij.hijacked 条件 + grm.inherit”→ 被劫持的敌机改刷劫持者程序（与无人机同一口径：执行母机武器的刷写变体），
                // 用去核心列表（AI 不用核心固件，FGR-SIG-090），持有条数按劫持者剩余带宽截断；原生程序搁置，劫持结束（EmgHijackRelease）还原，阵亡效果按原生程序与原阵营。
                // 离劫持者过远按 FGR-EMG-036 暂停末尾条数（StepHijacks 每 0.25 游戏秒重算）。带宽已满时照旧带原生程序（下面的分支）
                held = math.min(free, mh.z);
                d.Weapon[t] = d.FlashMap[mh.x + mh.y + 1 + held];
                hflags = EmgHijackConst.FlagFlashed;
            }
            else if (head.x >= 0)
            {
                held = math.clamp(free, 0, head.y);
                if (held < head.y && head.x + held < d.FlashMap.Length)
                {
                    d.Weapon[t] = d.FlashMap[head.x + held];
                }
            }
            CombatSiegeUnit sg = d.Siege[t];
            d.Hijacks.Add(new EmgHijack
            {
                Unit = d.Id[t], Hijacker = killerId, Until = d.Scalars[0].Time + HijackSecondsOf(ref d), OrigFaction = d.Faction[t], OrigBehavior = d.Behavior[t],
                SiegeRole = sg.Role, SiegeGroup = sg.Group, Held = held, OrigWeapon = w, Flags = hflags, FlashWeapon = hflags != 0 ? mw : -1,
            });
            // 退出攻城 / 突袭队伍：认领键（队伍键）释放，剩余计数随内核点数减一（SiegeService.CheckGroups 按内核计数对账）
            sg.Role = 0;
            sg.Group = 0;
            sg.Mode = 0;
            d.Siege[t] = sg;
            d.Faction[t] = (byte)CombatFaction.Player;
            d.Behavior[t] = (byte)CombatBehavior.Hijacked;
            d.Cmd[t] = default;
            d.Hp[t] = math.max(0.001f, d.MaxHp[t] * HijackHpRatioOf(ref d));
            EmgUnitState u = d.EmgUnit[t];
            AccPut(ref u, EmgPhaseConst.Hack, 0f); // 入侵度用掉了：重启后从 0 起（自己人不会再被劫持判定）
            u.DeathFlags = 0; // “直接击毁”记下的不留残骸作废：这次没有阵亡
            d.EmgUnit[t] = u;
            d.Cycle[t] = 0f;
            Gameplay(ref d, CombatEventKind.Hijacked, t, killerId, HijackSecondsOf(ref d), d.Hp[t], d.Pos[t], d.Hijacks[d.Hijacks.Length - 1].OrigFaction, 0);
            if (d.EmgListen != 0u && EmgSiteListens(ref d, EmgEventKind.Hijack))
            {
                EmgUnitEvent(ref d, EmgEventKind.Hijack, t, killerId, HijackSecondsOf(ref d), 0f, 0, d.Pos[t]); // evt.hijack 生产者（读者随 EMG4-FW-01 落表；没有监听者时不入队）
            }
            return true;
        }

        /// <summary>被劫持单位将要阵亡（期满 / 劫持者阵亡 / 被击毁）：还原原阵营、行为与攻城职能，发 HijackEnded；返回击杀者（= 劫持者）。不是被劫持单位返回原击杀者。</summary>
        internal static int EmgHijackRelease(ref CombatData d, int t, int killerId)
        {
            int h = EmgHijackIndexOf(ref d, d.Id[t]);
            if (h < 0)
            {
                return killerId;
            }
            EmgHijack r = d.Hijacks[h];
            d.Hijacks.RemoveAt(h);
            d.Faction[t] = r.OrigFaction;
            d.Behavior[t] = r.OrigBehavior;
            if ((r.Flags & EmgHijackConst.FlagFlashed) != 0 && r.OrigWeapon >= 0 && r.OrigWeapon < d.Weapons.Length)
            {
                d.Weapon[t] = r.OrigWeapon; // EMG4-FW-01：改刷体结束时还原原生武器——随后的 evt.death 按原生程序与原阵营结算，不以敌方阵营执行玩家的阵亡触发（例如自毁装药）
            }
            CombatSiegeUnit sg = d.Siege[t];
            sg.Role = r.SiegeRole;
            sg.Group = r.SiegeGroup;
            d.Siege[t] = sg;
            byte reason = r.EndReason != 0 ? r.EndReason : EmgHijackConst.EndDestroyed;
            Gameplay(ref d, CombatEventKind.HijackEnded, t, r.Hijacker, 0f, 0f, d.Pos[t], reason, 0);
            EmgReleaseEvent(ref d, t, r.Hijacker, reason, r.Source);
            return r.Hijacker;
        }

        /// <summary>evt.release 生产者（FG19 第 5 节码 27：劫持、夺控、封锁结束；Code = 结束原因 × 256 + 来源低 8 位）。没有监听者时不入队。</summary>
        private static void EmgReleaseEvent(ref CombatData d, int t, int other, byte reason, ushort source)
        {
            if (d.EmgListen != 0u && EmgSiteListens(ref d, EmgEventKind.Release))
            {
                EmgUnitEvent(ref d, EmgEventKind.Release, t, other, 0f, 0f, (ushort)(reason << 8 | (source & 0xFF)), d.Pos[t]);
            }
        }

        // ── EMG4-HIJ-02 夺控（敌方 → 己方机器，FG19 FGR-EMG-165；FG10 FGR-EVT-021～022；FG11 FGR-END-022 阶段三）──
        // 与信号劫持同一张表（d.Hijacks，Flags 带 FlagSeize）、同一个行为种类（Hijacked：内核自主按教义行动、不读编队命令），只是方向相反：
        // 己方机器翻成敌对阵营，攻击原来的同伴；击倒 = 瘫痪（不阵亡，FlagDowned）；期满 / 反向接入 / 调用方解除 → 归还（不自毁）。

        /// <summary>夺控一台己方单位：存活、己方、非结构、没有被接入（Possessed）、没有被劫持 / 夺控。seconds ≤ 0 = 直到调用方解除。O(表长)。</summary>
        internal static EmgSeizeResult EmgSeize(ref CombatData d, int t, float seconds, int seizerId, ushort source)
        {
            if (t < 0 || t >= d.Count)
            {
                return EmgSeizeResult.NoUnit;
            }
            if (d.IsAlive(t) && (d.Behavior[t] == (byte)CombatBehavior.Hijacked || EmgHijackIndexOf(ref d, d.Id[t]) >= 0))
            {
                return EmgSeizeResult.Controlled; // 已被夺控 / 是被劫持体：三类状态互不覆盖
            }
            if (!d.IsAlive(t) || d.Faction[t] != (byte)CombatFaction.Player || d.Kind[t] == (byte)CombatUnitKind.Structure)
            {
                return EmgSeizeResult.NotMachine;
            }
            if (d.Has(t, CombatUnitFlags.Possessed))
            {
                return EmgSeizeResult.Uplinked;
            }
            CombatSiegeUnit sg = d.Siege[t];
            d.Hijacks.Add(new EmgHijack
            {
                Unit = d.Id[t], Hijacker = seizerId, Until = seconds > 0f ? d.Scalars[0].Time + seconds : 0.0, OrigFaction = d.Faction[t], OrigBehavior = d.Behavior[t],
                SiegeRole = sg.Role, SiegeGroup = sg.Group, OrigWeapon = d.Weapon[t], Flags = EmgHijackConst.FlagSeize, Source = source,
            });
            sg.Role = 0;
            sg.Group = 0;
            sg.Mode = 0;
            d.Siege[t] = sg;
            d.Faction[t] = (byte)CombatFaction.Hostile;
            d.Behavior[t] = (byte)CombatBehavior.Hijacked;
            d.Cmd[t] = default;
            d.Cycle[t] = 0f;
            Gameplay(ref d, CombatEventKind.Seized, t, seizerId, math.max(0f, seconds), d.Hp[t], d.Pos[t], (byte)(source & 0xFF), 0);
            if (d.EmgListen != 0u && EmgSiteListens(ref d, EmgEventKind.Seize))
            {
                EmgUnitEvent(ref d, EmgEventKind.Seize, t, seizerId, math.max(0f, seconds), 0f, source, d.Pos[t]); // evt.seize 生产者（读者 N41 反夺控随 EMG4-FW-01）
            }
            return EmgSeizeResult.Ok;
        }

        /// <summary>阵亡统一入口之前：夺控中的机器被击倒 → 瘫痪（DEBT-EMG1FW02-03：不走 MarkDead / Kill 的后续，不发 Killed / evt.death，自毁装药不起爆）。不是夺控记录返回 false。</summary>
        internal static bool EmgSeizeDown(ref CombatData d, int t, int killerId)
        {
            int h = EmgHijackIndexOf(ref d, d.Id[t]);
            if (h < 0 || !d.Hijacks[h].IsSeize)
            {
                return false;
            }
            EmgHijack r = d.Hijacks[h];
            if (r.IsDowned)
            {
                return true; // 已瘫痪：重复的致死结算什么都不做
            }
            r.Flags |= EmgHijackConst.FlagDowned;
            r.ReleaseAt = 0.0; // 瘫痪中的反向接入作废：机器不能动，等夺控结束恢复
            d.Hijacks[h] = r;
            d.Hp[t] = 0f;
            d.Cmd[t] = default;
            d.Set(t, CombatUnitFlags.Targetable, false);
            EmgUnitState u = d.EmgUnit[t];
            u.DeathFlags = 0; // 不是阵亡：直接击毁记下的“不留残骸”作废
            d.EmgUnit[t] = u;
            Gameplay(ref d, CombatEventKind.SeizeDowned, t, killerId, 0f, 0f, d.Pos[t], (byte)(r.Source & 0xFF), 0);
            return true;
        }

        /// <summary>夺控结束：还原阵营、行为、攻城职能，瘫痪的恢复到 emg.seize.recover_ratio 耐久并重新可选中；发 SeizeEnded 与 evt.release。</summary>
        internal static void EmgSeizeRelease(ref CombatData d, int h, byte reason)
        {
            EmgHijack r = d.Hijacks[h];
            d.Hijacks.RemoveAt(h);
            int t = d.SlotOf(r.Unit);
            if (t < 0 || !d.IsAlive(t))
            {
                return;
            }
            d.Faction[t] = r.OrigFaction;
            d.Behavior[t] = r.OrigBehavior;
            CombatSiegeUnit sg = d.Siege[t];
            sg.Role = r.SiegeRole;
            sg.Group = r.SiegeGroup;
            d.Siege[t] = sg;
            d.Cmd[t] = default;
            d.Cycle[t] = 0f;
            if (r.IsDowned)
            {
                d.Hp[t] = math.max(d.Hp[t], math.max(0.001f, d.MaxHp[t] * SeizeRecoverOf(ref d)));
                d.Set(t, CombatUnitFlags.Targetable, true);
            }
            EmgUnitState u = d.EmgUnit[t];
            AccPut(ref u, EmgPhaseConst.Hack, 0f); // 夺控期间被己方 hij.hack 打进的入侵度作废（自己人不判劫持）
            d.EmgUnit[t] = u;
            Gameplay(ref d, CombatEventKind.SeizeEnded, t, r.Hijacker, 0f, d.Hp[t], d.Pos[t], reason, r.IsDowned ? (byte)1 : (byte)0);
            EmgReleaseEvent(ref d, t, r.Hijacker, reason, r.Source);
            EmgEndAttacksOn(ref d, t);
        }

        /// <summary>
        /// 夺控结束：机器回到己方，己方对它的攻击命令以“目标丢失”结束（否则攻击者会一直追着自己人打出“不是敌方”）；修复出口的命令照旧（回到己方正好能修）。
        /// 事件序列同命令自然结束（TickCommand）。O(单位数)，只在归还时一次。
        /// </summary>
        private static void EmgEndAttacksOn(ref CombatData d, int t)
        {
            int id = d.Id[t];
            for (int i = 0; i < d.Count; i++)
            {
                CombatCommand cmd = d.Cmd[i];
                if (i == t || cmd.Kind != CombatCommandKind.Attack || cmd.Target != id || !d.IsAlive(i) || d.Faction[i] != d.Faction[t])
                {
                    continue;
                }
                int w = d.Weapon[i];
                if (w >= 0 && w < d.Weapons.Length && d.Weapons[w].HasOutput == CombatConst.OutputRepair && d.Weapons[w].Mode != CombatWeaponMode.Cannon)
                {
                    continue;
                }
                d.Cmd[i] = default;
                byte navFail = d.NavFail[i];
                DropRoute(ref d, i);
                d.NavSt[i] = (byte)CombatNavState.None;
                Gameplay(ref d, CombatEventKind.CommandEnded, i, id, navFail, 0f, d.Pos[i], (byte)CombatEndReason.TargetLost, (byte)CombatCommandKind.Attack);
            }
        }

        /// <summary>按单位 ID 解除夺控（反向接入完成 / 调用方解除 / 地点卸载）。不是夺控记录返回 false。</summary>
        internal static bool EmgSeizeReleaseUnit(ref CombatData d, int unitId, byte reason)
        {
            int h = EmgHijackIndexOf(ref d, unitId);
            if (h < 0 || !d.Hijacks[h].IsSeize)
            {
                return false;
            }
            EmgSeizeRelease(ref d, h, reason);
            return true;
        }

        /// <summary>每步（己方单位之后）：期满或劫持者已不在 → 自毁（走 Kill，击杀者 = 劫持者，留残骸）。O(劫持数)，空表直接返回。</summary>
        internal static void StepHijacks(ref CombatData d, float dt)
        {
            if (d.Hijacks.Length == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            bool flashTick = d.FlashHead.Length > 0 && FlashTick(now, dt); // EMG4-FW-01：改刷体的距离衰减与无人机同一节拍
            for (int h = d.Hijacks.Length - 1; h >= 0; h--)
            {
                EmgHijack r = d.Hijacks[h];
                int s = d.SlotOf(r.Unit);
                if (s < 0 || !d.IsAlive(s))
                {
                    d.Hijacks.RemoveAt(h); // 被移除（对账 / 离场）而不是阵亡：没有可结算的，丢掉记录
                    continue;
                }
                int hk = d.SlotOf(r.Hijacker);
                if (r.IsSeize)
                {
                    // 夺控：反向接入引导完成 → 夺回；期满（0 = 直到解除）→ 归还；有单位来源且来源已不在 → 归还。都不自毁
                    byte why = r.ReleaseAt > 0.0 && now >= r.ReleaseAt ? EmgHijackConst.EndReclaimed
                        : r.Until > 0.0 && now >= r.Until ? EmgHijackConst.EndExpired
                        : r.Hijacker != 0 && (hk < 0 || !d.IsAlive(hk)) ? EmgHijackConst.EndHijackerLost : (byte)0;
                    if (why != 0)
                    {
                        EmgSeizeRelease(ref d, h, why);
                    }
                    continue;
                }
                byte reason = hk < 0 || !d.IsAlive(hk) ? EmgHijackConst.EndHijackerLost : now >= r.Until ? EmgHijackConst.EndExpired : (byte)0;
                if (reason == 0)
                {
                    if (flashTick && (r.Flags & EmgHijackConst.FlagFlashed) != 0)
                    {
                        EmgHijackFlashDecay(ref d, h, s, hk);
                    }
                    continue;
                }
                r.EndReason = reason;
                d.Hijacks[h] = r;
                Kill(ref d, s, r.Hijacker);
            }
        }

        /// <summary>EMG4-FW-01（FGR-EMG-036 / 163）：改刷体离劫持者超过 emg.flash.range 后每再远 emg.flash.step 暂停末尾 1 条，执行武器换成去核心列表“前 (持有 − 暂停) 条”那一把。</summary>
        private static void EmgHijackFlashDecay(ref CombatData d, int h, int s, int hk)
        {
            EmgHijack r = d.Hijacks[h];
            int4 mh = FlashHeadOf(ref d, r.FlashWeapon);
            if (mh.x < 0)
            {
                return; // 旧记录没有改刷来源：保持当前武器
            }
            int held = math.min(r.Held, mh.z);
            byte pause = FlashPauseAt(ref d, held, d.Pos[s], d.Pos[hk]);
            if (pause == r.Pause)
            {
                return;
            }
            r.Pause = pause;
            d.Hijacks[h] = r;
            d.Weapon[s] = d.FlashMap[mh.x + mh.y + 1 + held - pause];
        }

        /// <summary>
        /// 劫持体的 AI 教义（FGR-BASE-020 例外第二种）：射程内有敌对目标就按原武器开火；感知范围内有目标就靠近；都没有就跟随劫持者（停在 3 米外）。
        /// 不读编队命令（命令每步清空）；索敌走现有网格查询，并列按下标（FindTarget）。
        /// </summary>
        private static void StepHijackedUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            d.Cmd[i] = default;
            int h = EmgHijackIndexOf(ref d, d.Id[i]);
            if (h >= 0 && d.Hijacks[h].IsDowned)
            {
                return; // EMG4-HIJ-02：瘫痪的被夺控机器什么都不做，等夺控结束恢复
            }
            int bp = d.BProfile[i];
            float speed = bp >= 0 && bp < d.Profiles.Length && d.Profiles[bp].Speed > 0f ? d.Profiles[bp].Speed : d.Speed[i];
            speed = SlowedSpeed(ref d, i, speed);
            int w = d.Weapon[i];
            bool armed = w >= 0 && w < d.Weapons.Length && d.Has(i, CombatUnitFlags.WeaponEnabled);
            float range = armed ? EngageRange(ref d, i, d.Weapons[w]) : 0f;
            float sense = math.max(range, bp >= 0 && bp < d.Profiles.Length ? d.Profiles[bp].SenseRange : 0f);
            if (armed)
            {
                CombatWeapon wp = d.Weapons[w];
                d.Cycle[i] -= dt;
                int t = FindTarget(ref d, ref grid, i, range, d.Has(i, CombatUnitFlags.NeedsLos), wp.TargetMode);
                if (t >= 0)
                {
                    if (d.Cycle[i] <= 0f && HasAmmoFor(ref d, i, wp))
                    {
                        d.Cycle[i] = EffectiveCooldown(wp);
                        UnitAttack(ref d, i, t, wp);
                    }
                    return;
                }
            }
            double2 pos = d.Pos[i];
            int seek = sense > 0f ? FindTarget(ref d, ref grid, i, sense, false, CombatTargetMode.Nearest) : -1;
            if (seek >= 0)
            {
                MoveToward(ref d, i, pos, d.Pos[seek], speed * dt, math.max(0.5, range * 0.9));
                return;
            }
            int hk = h >= 0 ? d.SlotOf(d.Hijacks[h].Hijacker) : -1; // 夺控者为 0（信号幽灵）时没有可跟随的单位：原地待命
            if (hk >= 0)
            {
                MoveToward(ref d, i, pos, d.Pos[hk], speed * dt, EmgHijackConst.FollowStop + d.Radius[i] + d.Radius[hk]);
            }
        }
    }
}
