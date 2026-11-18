using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG11-FAC-01（FG09 第 7 章、FGR-FAC-008 超频专属语法）：冲刺—过热—停机、前倾加速、蓄热连射、过热泄爆（熔断 / 死亡时）、算力节点光环、失控超算体三种超频状态循环；
    /// 另含高温辐射区（热量上限 × / 散热 ×，DEBT-FG7ENV05-01 / FG8EXP02-03）。规则只看行为参数（表列），不认任何单位 ID（IC-REQ-012、FGR-EMG-160）；
    /// 敌我同一规则（FGR-FAC-003）：玩家机器装“全开散热鳍冠 / 蓄热连射枪”得到同一组参数、走同一套结算（FG-GAP-150）。
    /// 开销：运行状态是稀疏表（只有带超频参数的单位），空表时每步一次长度比较；光环 O(节点数 × 表长)；泄爆 O(N)，只在泄爆那一步。
    /// </summary>
    public static partial class CombatLogic
    {
        // ─────────────────────────────── 查询（O(表长)，空表 O(1)） ───────────────────────────────

        internal static int OcIndexOf(ref CombatData d, int unitId)
        {
            for (int k = 0; k < d.Overclocks.Length; k++)
            {
                if (d.Overclocks[k].Unit == unitId)
                {
                    return k;
                }
            }
            return -1;
        }

        private static bool OcProfile(ref CombatData d, int i, out CombatBehaviorProfile p)
        {
            int bp = d.BProfile[i];
            if (bp < 0 || bp >= d.Profiles.Length)
            {
                p = default;
                return false;
            }
            p = d.Profiles[bp];
            return true;
        }

        /// <summary>
        /// FG11-RAID-01（FG09 第 7 章“突袭特点：熔断者冲墙自爆”）：攻城中带熔断语法（行为参数 OcMeltHp &gt; 0）的单位顶到它正在打的己方结构（屏障 / 炮塔 / 建筑）→
        /// 进入熔断，引信与耐久触发同一个（≥ 0.8 秒，表里 2 秒，预警 Cue 照发）。只看行为参数不认单位 ID（FGR-EMG-013）；已在熔断 / 待泄爆的不重复进入。O(超频表长)，只在开火到点那一步。
        /// </summary>
        internal static bool OcMeltOnContact(ref CombatData d, int i)
        {
            if (d.Overclocks.Length == 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p) || p.OcMeltHp <= 0f)
            {
                return false;
            }
            int k = OcIndexOf(ref d, d.Id[i]);
            if (k < 0)
            {
                return false;
            }
            CombatOverclock r = d.Overclocks[k];
            if (r.State == CombatOverclockConst.StateMelting || r.State == CombatOverclockConst.StatePendingBurst)
            {
                return false;
            }
            r.State = CombatOverclockConst.StateMelting;
            r.Until = d.Scalars[0].Time + math.max(0.8f, p.OcMeltFuse);
            d.Overclocks[k] = r;
            Cue(ref d, CombatEventKind.OverclockCue, i, 0, p.OcBurstRadius, d.Pos[i], CombatOverclockConst.CueMeltWarn);
            return true;
        }

        /// <summary>停机 / 排气中：不移动、不开火（停机计时照常走，冻结不叠加额外停机，FG09 第 10 节）。</summary>
        internal static bool OcHolds(ref CombatData d, int i)
        {
            if (d.Overclocks.Length == 0)
            {
                return false;
            }
            int k = OcIndexOf(ref d, d.Id[i]);
            if (k < 0)
            {
                return false;
            }
            byte st = d.Overclocks[k].State;
            return st == CombatOverclockConst.StateShutdown || st == CombatOverclockConst.StateVent;
        }

        /// <summary>移动速度倍率：停机 / 排气 = 0；冲刺 × OcDashSpeed；前倾加速 × (1 + 直线秒数 × 加速率，封顶)；极速态 × OcModeSpeed。</summary>
        internal static float OcSpeedFactor(ref CombatData d, int i)
        {
            if (d.Overclocks.Length == 0)
            {
                return 1f;
            }
            int k = OcIndexOf(ref d, d.Id[i]);
            if (k < 0 || !OcProfile(ref d, i, out CombatBehaviorProfile p))
            {
                return 1f;
            }
            CombatOverclock r = d.Overclocks[k];
            if (r.State == CombatOverclockConst.StateShutdown || r.State == CombatOverclockConst.StateVent)
            {
                return 0f;
            }
            float f = p.OcDashSpeed > 0f ? p.OcDashSpeed : 1f;
            bool speedMode = p.OcModeCycle <= 0f || r.Mode == CombatOverclockConst.ModeSpeed;
            if (p.OcLeanRate > 0f && speedMode)
            {
                f *= math.min(math.max(1f, p.OcLeanMax), 1f + r.Lean * p.OcLeanRate);
            }
            if (p.OcModeCycle > 0f && r.Mode == CombatOverclockConst.ModeSpeed && p.OcModeSpeed > 0f)
            {
                f *= p.OcModeSpeed;
            }
            return f;
        }

        /// <summary>冷却倍率：蓄热连射（热越高射得越快）、强攻态、光环内（附近有算力节点：射速 +OcAuraRate，多个不叠加）。</summary>
        internal static float OcCooldownFactor(ref CombatData d, int i)
        {
            if (d.Overclocks.Length == 0)
            {
                return 1f;
            }
            float rate = 1f;
            int k = OcIndexOf(ref d, d.Id[i]);
            if (k >= 0 && OcProfile(ref d, i, out CombatBehaviorProfile p))
            {
                CombatOverclock r = d.Overclocks[k];
                if (p.OcRampHeat > 0f && p.OcRampMax > 1f)
                {
                    rate *= 1f + (p.OcRampMax - 1f) * math.saturate(r.Heat);
                }
                if (p.OcModeCycle > 0f && r.Mode == CombatOverclockConst.ModeAssault && p.OcModeRate > 0f)
                {
                    rate *= p.OcModeRate;
                }
            }
            if (k >= 0)
            {
                rate *= 1f + OcAuraBonus(ref d, i, out _); // FG09：光环只加“附近的超频单位”（有超频运行记录的），不加别的阵营 / 普通单位
            }
            return 1f / math.max(0.05f, rate);
        }

        /// <summary>单位 i 所在的光环加成（同阵营、在某个光环源半径内、不是光环源自己；取最大，不叠加）。src = 提供加成的光环源记录下标（没有 = -1）。
        /// 光环源排气 / 停机期间光环中断（读自身 prop.heat：满热就停供算力）。</summary>
        internal static float OcAuraBonus(ref CombatData d, int i, out int src)
        {
            float best = 0f;
            src = -1;
            double2 pos = d.Pos[i];
            for (int k = 0; k < d.Overclocks.Length; k++)
            {
                CombatOverclock r = d.Overclocks[k];
                if (r.Unit == d.Id[i] || r.State != CombatOverclockConst.StateNormal && r.State != CombatOverclockConst.StateMelting)
                {
                    continue;
                }
                int s = d.SlotOf(r.Unit);
                if (s < 0 || !d.IsAlive(s) || d.Faction[s] != d.Faction[i] || !OcProfile(ref d, s, out CombatBehaviorProfile p) || p.OcAuraRange <= 0f || p.OcAuraRate <= best)
                {
                    continue;
                }
                if (math.distancesq(d.Pos[s], pos) <= (double)p.OcAuraRange * p.OcAuraRange)
                {
                    best = p.OcAuraRate;
                    src = k;
                }
            }
            return best;
        }

        /// <summary>受伤倍率：停机 / 排气期间 × (1 + OcVuln)；护盾态 × (1 − OcModeShield)。</summary>
        internal static float OcDamageFactor(ref CombatData d, int t)
        {
            if (d.Overclocks.Length == 0)
            {
                return 1f;
            }
            int k = OcIndexOf(ref d, d.Id[t]);
            if (k < 0 || !OcProfile(ref d, t, out CombatBehaviorProfile p))
            {
                return 1f;
            }
            CombatOverclock r = d.Overclocks[k];
            float f = 1f;
            if ((r.State == CombatOverclockConst.StateShutdown || r.State == CombatOverclockConst.StateVent) && p.OcVuln > 0f)
            {
                f *= 1f + p.OcVuln;
            }
            if (p.OcModeCycle > 0f && r.Mode == CombatOverclockConst.ModeShield)
            {
                f *= math.saturate(1f - p.OcModeShield);
            }
            return f;
        }

        // ─────────────────────────────── 登记 / 出手 / 阵亡 ───────────────────────────────

        /// <summary>生成 / 换参数行后登记运行状态（没有超频参数 → 删掉旧记录）。</summary>
        internal static void OcRegister(ref CombatData d, int slot)
        {
            int k = OcIndexOf(ref d, d.Id[slot]);
            bool want = OcProfile(ref d, slot, out CombatBehaviorProfile p) && p.HasOverclock;
            if (!want)
            {
                if (k >= 0)
                {
                    d.Overclocks.RemoveAt(k);
                }
                return;
            }
            if (k >= 0)
            {
                return;
            }
            d.Overclocks.Add(new CombatOverclock
            {
                Unit = d.Id[slot],
                Profile = d.BProfile[slot],
                ModeAt = p.OcModeCycle > 0f ? d.Scalars[0].Time + p.OcModeCycle : 0.0,
            });
        }

        /// <summary>真的开了一发之后：蓄热连射积热；这一发吃到光环时，光环源也按自身 OcRampHeat 积热（算力节点写 en.heat，FGR-EMG-014）。满热进入排气（弱点窗口）。</summary>
        internal static void OcOnFired(ref CombatData d, int i)
        {
            if (d.Overclocks.Length == 0)
            {
                return;
            }
            int k = OcIndexOf(ref d, d.Id[i]);
            if (k < 0)
            {
                return;
            }
            OcRampAdd(ref d, k, i, false);
            OcAuraBonus(ref d, i, out int src);
            if (src >= 0)
            {
                OcRampAdd(ref d, src, d.SlotOf(d.Overclocks[src].Unit), true);
            }
        }

        /// <summary>记录 k（单位槽 slot）按自身 OcRampHeat 积一发热；没有蓄热参数 / 不在正常态 = 不变。
        /// 光环源（OcAuraRange &gt; 0）的 OcRampHeat 是“供算力积热”：只在 asAuraSource 时积，自己开火不积。</summary>
        private static void OcRampAdd(ref CombatData d, int k, int slot, bool asAuraSource)
        {
            if (slot < 0 || !OcProfile(ref d, slot, out CombatBehaviorProfile p) || p.OcRampHeat <= 0f || (p.OcAuraRange > 0f) != asAuraSource)
            {
                return;
            }
            CombatOverclock r = d.Overclocks[k];
            if (r.State != CombatOverclockConst.StateNormal)
            {
                return;
            }
            r.Heat = math.min(1f, r.Heat + p.OcRampHeat);
            r.Until = d.Scalars[0].Time; // 正常态下 Until = 最近一次积热时刻（停火够久热量才退，FG09“停火一会儿热量会退”）
            if (r.Heat >= 1f)
            {
                r.State = CombatOverclockConst.StateVent;
                r.Until = d.Scalars[0].Time + math.max(0.8f, p.OcShutdown);
                Cue(ref d, CombatEventKind.OverclockCue, slot, 0, math.max(0.8f, p.OcShutdown), d.Pos[slot], CombatOverclockConst.CueVent);
            }
            d.Overclocks[k] = r;
        }

        /// <summary>阵亡统一入口：熔断中被击毁 = 熔断中止（“一口气打死”）；带死亡泄爆的留一条待结算（下一个超频阶段泄爆，不在伤害结算里递归）。</summary>
        internal static void OcOnKill(ref CombatData d, int t)
        {
            if (d.Overclocks.Length == 0)
            {
                return;
            }
            int k = OcIndexOf(ref d, d.Id[t]);
            if (k < 0)
            {
                return;
            }
            CombatOverclock r = d.Overclocks[k];
            if (r.State != CombatOverclockConst.StateMelting && OcProfile(ref d, t, out CombatBehaviorProfile p) && p.OcDeathBurst > 0f && p.OcBurstRadius > 0f)
            {
                r.State = CombatOverclockConst.StatePendingBurst;
                r.At = d.Pos[t];
                r.Profile = d.BProfile[t];
                d.Overclocks[k] = r;
            }
            // 审查修复：阵亡单位的记录不在这里删——StepOverclock 倒序遍历中泄爆连锁击杀时，删前面的记录会让后面一条同一步被处理两次；
            // 下一次 StepOverclock 遇到“单位已不在 / 已阵亡”统一删（读写只看存活单位，多留一步不影响结算）
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        private static void StepOverclock(ref CombatData d, float dt)
        {
            if (d.Overclocks.Length == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            for (int k = d.Overclocks.Length - 1; k >= 0; k--)
            {
                if (k >= d.Overclocks.Length)
                {
                    continue; // 泄爆连锁删掉了后面的记录
                }
                CombatOverclock r = d.Overclocks[k];
                if (r.State == CombatOverclockConst.StatePendingBurst)
                {
                    d.Overclocks.RemoveAt(k);
                    if (r.Profile >= 0 && r.Profile < d.Profiles.Length)
                    {
                        OcBurst(ref d, -1, r.At, d.Profiles[r.Profile]);
                    }
                    continue;
                }
                int i = d.SlotOf(r.Unit);
                if (i < 0 || !d.IsAlive(i) || !OcProfile(ref d, i, out CombatBehaviorProfile p))
                {
                    d.Overclocks.RemoveAt(k);
                    continue;
                }
                double2 pos = d.Pos[i];
                switch (r.State)
                {
                    case CombatOverclockConst.StateShutdown:
                    case CombatOverclockConst.StateVent:
                        if (now >= r.Until)
                        {
                            r.State = CombatOverclockConst.StateNormal;
                            r.Heat = 0f;
                            r.Lean = 0f;
                            Cue(ref d, CombatEventKind.OverclockCue, i, 0, 0f, pos, CombatOverclockConst.CueRecover);
                        }
                        break;
                    case CombatOverclockConst.StateMelting:
                        if (now >= r.Until)
                        {
                            d.Overclocks.RemoveAt(k);
                            OcBurst(ref d, i, pos, p);
                            SelfDestruct(ref d, i); // 熔断泄爆是自毁：不计击杀、不掉落（与澄净自爆同一口径）
                            continue;
                        }
                        break;
                    default:
                        if (p.OcMeltHp > 0f && d.MaxHp[i] > 0f && d.Hp[i] <= d.MaxHp[i] * p.OcMeltHp)
                        {
                            float fuse = math.max(0.8f, p.OcMeltFuse);
                            r.State = CombatOverclockConst.StateMelting;
                            r.Until = now + fuse;
                            Cue(ref d, CombatEventKind.OverclockCue, i, 0, p.OcBurstRadius, pos, CombatOverclockConst.CueMeltWarn);
                            break;
                        }
                        double2 step = pos - d.Prev[i];
                        double moved = math.length(step);
                        bool moving = moved > 1e-4;
                        if (p.OcDashHeat > 0f)
                        {
                            r.Heat = moving ? math.min(1f, r.Heat + p.OcDashHeat * dt) : math.max(0f, r.Heat - p.OcCool * dt);
                            if (r.Heat >= 1f)
                            {
                                r.State = CombatOverclockConst.StateShutdown;
                                r.Until = now + math.max(0.8f, p.OcShutdown);
                                r.Lean = 0f;
                                Cue(ref d, CombatEventKind.OverclockCue, i, 0, math.max(0.8f, p.OcShutdown), pos, CombatOverclockConst.CueShutdown);
                            }
                        }
                        else if (p.OcRampHeat > 0f && p.OcCool > 0f && now - r.Until >= CombatOverclockConst.RampIdleSeconds)
                        {
                            r.Heat = math.max(0f, r.Heat - p.OcCool * dt); // 只在停火满 RampIdleSeconds 后散热：持续射击时热量只升不退
                        }
                        if (p.OcLeanRate > 0f)
                        {
                            float2 dir = moving ? (float2)(step / moved) : float2.zero;
                            float cosTurn = math.cos(math.radians(math.clamp(p.OcLeanTurn > 0f ? p.OcLeanTurn : 20f, 1f, 179f)));
                            bool straight = moving && math.lengthsq(r.Dir) > 0.5f && math.dot(dir, r.Dir) >= cosTurn;
                            r.Lean = straight ? r.Lean + dt : 0f; // 转向掉速（按固定曲线结算，不做物理）
                            if (moving)
                            {
                                r.Dir = dir;
                            }
                        }
                        break;
                }
                if (p.OcModeCycle > 0f && r.State != CombatOverclockConst.StateMelting)
                {
                    byte next = (byte)((r.Mode + 1) % 3);
                    float warn = math.max(0.8f, p.OcModeWarn);
                    if (r.Warned == 0 && now >= r.ModeAt - warn)
                    {
                        r.Warned = 1;
                        OcModeCue(ref d, i, CombatOverclockConst.CueModeWarn, warn, pos, next);
                    }
                    if (now >= r.ModeAt)
                    {
                        r.Mode = next;
                        r.Warned = 0;
                        r.ModeAt = now + math.max(warn + 0.1f, p.OcModeCycle);
                        r.Lean = 0f;
                        OcModeCue(ref d, i, CombatOverclockConst.CueMode, 0f, pos, next);
                    }
                }
                d.Overclocks[k] = r;
            }
        }

        private static void OcModeCue(ref CombatData d, int i, byte code, float v, double2 pos, byte mode)
        {
            int before = d.Cues.Length;
            Cue(ref d, CombatEventKind.OverclockCue, i, 0, v, pos, code);
            if (d.Cues.Length > before)
            {
                CombatEvent e = d.Cues[d.Cues.Length - 1];
                e.Code2 = mode;
                d.Cues[d.Cues.Length - 1] = e;
            }
        }

        /// <summary>过热泄爆：半径内所有可被攻击的单位（敌我、建筑、它的友军）都受伤（FGR-FAC-003）。<paramref name="self"/> = 泄爆者槽位（死亡泄爆时 −1）。O(N)。</summary>
        private static void OcBurst(ref CombatData d, int self, double2 pos, in CombatBehaviorProfile p)
        {
            float radius = math.max(0.5f, p.OcBurstRadius);
            NoteBlast(ref d, pos, radius); // FG12-EVT-02：大规模爆炸记震动值
            int n = d.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == self || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable))
                {
                    continue;
                }
                if (math.distance(d.Pos[k], pos) > radius + d.Radius[k])
                {
                    continue;
                }
                DamageFrom(ref d, k, p.OcBurstDamage, self, EmgCode.SrcZone);
                if (p.OcBurstHeat > 0f && d.IsAlive(k))
                {
                    EmgAddUnitHeat(ref d, k, p.OcBurstHeat); // FG11-REACT-01：泄爆是热爆（声明 en.heat inject）：半径内单位积热
                }
            }
            if (p.OcBurstHeat > 0f && EmgPatchesOn(ref d))
            {
                EmgInjectPatches(ref d, pos, radius, EmgConst.PortHeat, p.OcBurstHeat, 0, 0, self >= 0 ? d.Id[self] : 0); // 斑块升温（点燃油、玻化粉尘、蒸发水……由规则表结算）
            }
            Cue(ref d, CombatEventKind.OverclockCue, self, 0, radius, pos, CombatOverclockConst.CueBurst);
        }

        // ─────────────────────────────── 高温辐射（DEBT-FG7ENV05-01 / FG8EXP02-03） ───────────────────────────────

        private static bool InRadiation(ref CombatData d, int i)
        {
            if (d.RadZones.Length == 0)
            {
                return false;
            }
            double2 pos = d.Pos[i];
            for (int k = 0; k < d.RadZones.Length; k++)
            {
                float4 c = d.RadZones[k];
                double dx = pos.x - c.x;
                double dy = pos.y - c.y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= (double)c.w * c.w && d2 >= (double)c.z * c.z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>高温辐射区内的散热倍率（不在区里 = 1）。O(区数，个位数)。</summary>
        internal static float RadDissFactor(ref CombatData d, int i) => d.RadDissMult > 0f && InRadiation(ref d, i) ? d.RadDissMult : 1f;

        /// <summary>武器过热点（热量上限）：高温辐射区内 × RadCapMult。所有“积热到过热点”的判定走这里（敌我同一规则）。</summary>
        internal static float OverheatAtOf(ref CombatData d, int i, in CombatWeapon wp) =>
            d.RadCapMult > 0f && i >= 0 && i < d.Count && InRadiation(ref d, i) && !InCooling(ref d, i) ? wp.OverheatAt * d.RadCapMult : wp.OverheatAt;

        /// <summary>FG11-REG-01：单位在某座运转中的冷却塔圈里（热量上限恢复）。O(塔数，个位数)；只在辐射区内才查。</summary>
        private static bool InCooling(ref CombatData d, int i)
        {
            double2 pos = d.Pos[i];
            for (int k = 0; k < d.CoolZones.Length; k++)
            {
                float3 c = d.CoolZones[k];
                double dx = pos.x - c.x;
                double dy = pos.y - c.y;
                if (dx * dx + dy * dy <= (double)c.z * c.z)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
