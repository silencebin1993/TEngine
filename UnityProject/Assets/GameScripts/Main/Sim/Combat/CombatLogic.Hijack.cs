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

        /// <summary>某名玩家（劫持者所在阵营只有己方一种，按全局计）此刻劫持着几台。</summary>
        internal static int EmgHijackCount(ref CombatData d) => d.Hijacks.Length;

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
                Gameplay(ref d, CombatEventKind.HijackRefused, t, d.Id[c.Owner], d.Hijacks.Length, HijackMaxOf(ref d), d.Pos[t], EmgHijackConst.RefuseResist, 0);
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
            if (EmgResist(ref d, t, EmgPhaseConst.ResHack) >= 1f)
            {
                Gameplay(ref d, CombatEventKind.HijackRefused, t, killerId, d.Hijacks.Length, max, d.Pos[t], EmgHijackConst.RefuseResist, 0);
                return false;
            }
            if (d.Hijacks.Length >= max)
            {
                Gameplay(ref d, CombatEventKind.HijackRefused, t, killerId, d.Hijacks.Length, max, d.Pos[t], EmgHijackConst.RefuseCap, 0);
                return false;
            }
            // 刷写带宽（FGR-EMG-036）：占用 = 原生程序条数；带宽不够时截到放得下（武器换成刷写变体表里“前 k 条”那一把，没有变体表 = 不占带宽）
            int w = d.Weapon[t];
            int4 head = FlashHeadOf(ref d, w);
            int held = 0;
            if (head.x >= 0)
            {
                held = math.clamp(FlashBandwidthOf(ref d) - FlashUsedBy(ref d, killerId, -1), 0, head.y);
                if (held < head.y && head.x + held < d.FlashMap.Length)
                {
                    d.Weapon[t] = d.FlashMap[head.x + held];
                }
            }
            CombatSiegeUnit sg = d.Siege[t];
            d.Hijacks.Add(new EmgHijack
            {
                Unit = d.Id[t], Hijacker = killerId, Until = d.Scalars[0].Time + HijackSecondsOf(ref d), OrigFaction = d.Faction[t], OrigBehavior = d.Behavior[t],
                SiegeRole = sg.Role, SiegeGroup = sg.Group, Held = held, OrigWeapon = w,
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
            CombatSiegeUnit sg = d.Siege[t];
            sg.Role = r.SiegeRole;
            sg.Group = r.SiegeGroup;
            d.Siege[t] = sg;
            byte reason = r.EndReason != 0 ? r.EndReason : EmgHijackConst.EndDestroyed;
            Gameplay(ref d, CombatEventKind.HijackEnded, t, r.Hijacker, 0f, 0f, d.Pos[t], reason, 0);
            return r.Hijacker;
        }

        /// <summary>每步（己方单位之后）：期满或劫持者已不在 → 自毁（走 Kill，击杀者 = 劫持者，留残骸）。O(劫持数)，空表直接返回。</summary>
        internal static void StepHijacks(ref CombatData d)
        {
            if (d.Hijacks.Length == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
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
                byte reason = hk < 0 || !d.IsAlive(hk) ? EmgHijackConst.EndHijackerLost : now >= r.Until ? EmgHijackConst.EndExpired : (byte)0;
                if (reason == 0)
                {
                    continue;
                }
                r.EndReason = reason;
                d.Hijacks[h] = r;
                Kill(ref d, s, r.Hijacker);
            }
        }

        /// <summary>
        /// 劫持体的 AI 教义（FGR-BASE-020 例外第二种）：射程内有敌对目标就按原武器开火；感知范围内有目标就靠近；都没有就跟随劫持者（停在 3 米外）。
        /// 不读编队命令（命令每步清空）；索敌走现有网格查询，并列按下标（FindTarget）。
        /// </summary>
        private static void StepHijackedUnit(ref CombatData d, ref CombatGrid grid, int i, float dt)
        {
            d.Cmd[i] = default;
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
            int h = EmgHijackIndexOf(ref d, d.Id[i]);
            int hk = h >= 0 ? d.SlotOf(d.Hijacks[h].Hijacker) : -1;
            if (hk >= 0)
            {
                MoveToward(ref d, i, pos, d.Pos[hk], speed * dt, EmgHijackConst.FollowStop + d.Radius[i] + d.Radius[hk]);
            }
        }
    }
}
