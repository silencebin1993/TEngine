using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-REACT-01（FG19 FGR-EMG-021 / 085 / 086 / 093；FG19B §2.7 C01～C08、§2.8 P03、§4.1；ADR-EMG-001 ⑥ ⑪ ⑯）：
    /// 五条定稿反应的单位层（沾染 × 能量 / 沾染）规则行、熔穿过载 P03（相态 × 动能）与固化胶块阻路（动态障碍圆）。
    /// 只认端口码 / 沾染位 / 阈值 / 结算方式（FGR-EMG-013），不认规则名、固件或组件 ID。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>电击沾染新挂上（没有注入量的“标签式”通电）按这么多电荷算（电击标签 → 电荷换算的正式数值归 EMG2-REACT-02，DEBT-EMG2PHS01-04）。</summary>
        private const float CoatTagElec = 0.5f;

        // ─────────────────────────────── 单位层沾染行（C） ───────────────────────────────

        /// <summary>
        /// 单位 <paramref name="u"/> 这一次新挂上沾染位 <paramref name="incoming"/>（ApplyStatus），或这一次命中注入了热 <paramref name="heatIn"/> / 电 <paramref name="elecIn"/>（EmgHitInject）：
        /// 按优先级找第一条成立的 coat_matter 行结算（同一宿主一次只结算一条，FG02:142 沿用）。条件：
        /// a 侧沾染在身上；b = en.heat：本次注热 ≥ c1，或燃烧沾染新挂上 / 已在身上而 a 新挂上（FG19B C01“进入燃烧相”的单位层读法）；
        /// b = en.elec：本次注电 ≥ c1，或电击沾染与 a 新凑齐；b = 另一沾染：两种沾染同时在身上且至少一种是这一次挂上的。
        /// 返回本次还要挂上的位（被消耗的从 <paramref name="incoming"/> 去掉）。O(规则数)。
        /// </summary>
        internal static uint EmgCoatReact(ref CombatData d, int u, uint incoming, float heatIn, float elecIn, int source)
        {
            if (!d.EmgRules.IsCreated || d.EmgRules.Length == 0 || d.EmgS[0].CoatBusy != 0 || u < 0 || u >= d.Count || !d.IsAlive(u))
            {
                return incoming;
            }
            uint have = d.Status[u] & ~CombatConst.StatusBitZoneSlow;
            uint all = have | (incoming & ~CombatConst.StatusBitZoneSlow);
            int best = -1;
            short bestPrio = short.MaxValue;
            float energy = 0f;
            for (int i = 0; i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Pair != EmgRuleConst.PairCoatMatter || r.Op == EmgRuleConst.OpDeferred || r.Priority >= bestPrio || r.A < EmgRuleConst.CoatBase)
                {
                    continue;
                }
                if (r.Op == EmgRuleConst.OpStick && IsStuck(ref d, u))
                {
                    continue; // C06 粘住期间胶膜还在身上：再接触酸不重新结算（不续时、不重复计数 / 字幕），粘住结束清胶膜后才能再凑
                }
                uint aBit = 1u << (r.A - EmgRuleConst.CoatBase);
                if ((all & aBit) == 0u)
                {
                    continue;
                }
                float e = 0f;
                bool ok;
                if (r.B == EmgConst.PortHeat)
                {
                    e = heatIn;
                    ok = heatIn >= r.C1 || (d.FireBit != 0u && (all & d.FireBit) != 0u && (incoming & (d.FireBit | aBit)) != 0u);
                }
                else if (r.B == EmgConst.PortElec)
                {
                    e = elecIn > 0f ? elecIn : CoatTagElec;
                    ok = elecIn >= r.C1 || (d.ShockBit != 0u && (all & d.ShockBit) != 0u && (incoming & (d.ShockBit | aBit)) != 0u);
                }
                else if (r.B >= EmgRuleConst.CoatBase)
                {
                    uint bBit = 1u << (r.B - EmgRuleConst.CoatBase);
                    ok = (all & bBit) != 0u && (incoming & (aBit | bBit)) != 0u;
                }
                else
                {
                    ok = false;
                }
                if (ok)
                {
                    best = i;
                    bestPrio = r.Priority;
                    energy = e;
                }
            }
            if (best < 0)
            {
                return incoming;
            }
            EmgRule rule = d.EmgRules[best];
            uint consumed = 1u << (rule.A - EmgRuleConst.CoatBase);
            EmgScalars es = d.EmgS[0];
            es.CoatBusy = 1;
            d.EmgS[0] = es;
            EmgCtx saved = d.EmgCtx;
            EmgCtx side0 = saved;
            side0.Prog = -1; // 附带伤害不是这发弹体的又一次命中：不重入命中注能（同 PhaseKinetic）
            d.EmgCtx = side0;
            int srcId = EmgIdOf(ref d, source);
            double2 pos = d.Pos[u];
            int chain = saved.Chain > 0 ? saved.Chain : d.EmgUnit[u].LastHitChain;
            byte depth = saved.Chain > 0 ? saved.Depth : (byte)0;
            switch (rule.Op)
            {
                case EmgRuleConst.OpCoatSpawn:
                    EmgRuleSpawn(ref d, rule.Product, pos, rule.P1 > 0f ? rule.P1 : 1.5f, rule.P2, rule.P3, rule.Life, srcId, chain, depth);
                    if (rule.P4 > EmgRuleConst.KeepTemp)
                    {
                        EmgSetAcc(ref d, u, EmgPhaseConst.Ignite, math.max(0f, rule.P4), srcId);
                        if (rule.P4 <= 0f)
                        {
                            EmgUnitState ig = d.EmgUnit[u];
                            AccPut(ref ig, EmgPhaseConst.Ignite, 0f); // EmgSetAcc 只升不降：“着火度清零”直接写 0（含本次燃烧标签刚加的增益）
                            d.EmgUnit[u] = ig;
                            if (InPhase(ref d, u, EmgPhaseConst.Ignite))
                            {
                                ExitPhase(ref d, u, EmgPhaseConst.Ignite, true);
                            }
                            consumed |= d.FireBit; // 着火度清零并退出燃烧：这一次带来的 / 身上的燃烧沾染一并去掉（FG19B C02）
                        }
                    }
                    break;
                case EmgRuleConst.OpCoatArc:
                    EmgCoatArc(ref d, u, rule, energy, consumed, elecIn > 0f);
                    break;
                case EmgRuleConst.OpStick:
                {
                    EmgUnitState st = d.EmgUnit[u];
                    st.StickT = math.max(st.StickT, rule.P1 > 0f ? rule.P1 : 2.5f);
                    d.EmgUnit[u] = st;
                    consumed = 0u; // 胶膜在粘住结束时清除（FG19B C06“结束后胶膜清除”）
                    break;
                }
            }
            EmgUnitRuleSettle(ref d, rule, chain, depth, srcId, u);
            if (rule.Side == EmgRuleConst.SideDamage && rule.SideValue > 0f && d.IsAlive(u) && rule.Op != EmgRuleConst.OpCoatArc)
            {
                DamageFrom(ref d, u, rule.SideValue, source >= 0 && source < d.Count ? source : -1, EmgCode.SrcZone);
            }
            d.EmgCtx = saved;
            es = d.EmgS[0];
            es.CoatBusy = 0;
            d.EmgS[0] = es;
            if (consumed != 0u)
            {
                EmgClearCoat(ref d, u, consumed);
            }
            return incoming & ~consumed;
        }

        /// <summary>C03：本单位电荷注入 × p1；电弧跳向 p3 米内最多 p2 个导电（prop.conductive ≥ 0.3）或带同一沾染（浸湿）的单位（含己方、中立），第 k 跳 × p4^k；每跳附带伤害。
        /// <paramref name="injected"/>：这份电荷已由命中注能（EmgHitInject）加过 1 倍，这里只补 (p1 − 1) 倍，合计 = 注入 × p1。</summary>
        private static void EmgCoatArc(ref CombatData d, int u, in EmgRule r, float energy, uint coatBit, bool injected)
        {
            float base0 = math.max(0f, energy);
            float mul = r.P1 > 0f ? r.P1 : 2f;
            EmgAddAcc(ref d, u, EmgPhaseConst.Charge, base0 * (injected ? math.max(0f, mul - 1f) : mul), EmgIdOf(ref d, u));
            int max = (int)math.max(0f, r.P2);
            float range = r.P3 > 0f ? r.P3 : 4f;
            float fall = r.P4 > 0f ? r.P4 : 0.7f;
            if (max <= 0 || !(base0 > 0f))
            {
                return;
            }
            var picks = new NativeList<float2>(max + 1, Allocator.Temp); // (距离, 槽位)
            for (int v = 0; v < d.Count; v++)
            {
                if (v == u || !d.IsAlive(v) || d.Kind[v] == (byte)CombatUnitKind.Structure || IsAir(ref d, v))
                {
                    continue;
                }
                float dist = (float)math.distance(d.Pos[u], d.Pos[v]);
                if (dist > range || ((d.Status[v] & coatBit) == 0u && EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, v)).Conductive < EmgRuleConst.DefaultConductMinStrength))
                {
                    continue;
                }
                int at = picks.Length;
                for (int j = 0; j < picks.Length; j++)
                {
                    float2 o = picks[j];
                    if (dist < o.x || (dist == o.x && d.Id[v] < d.Id[(int)o.y]))
                    {
                        at = j;
                        break;
                    }
                }
                if (at < max)
                {
                    picks.InsertRangeWithBeginEnd(at, at + 1);
                    picks[at] = new float2(dist, v);
                    if (picks.Length > max)
                    {
                        picks.RemoveAt(picks.Length - 1);
                    }
                }
            }
            float q = base0;
            for (int j = 0; j < picks.Length; j++)
            {
                int v = (int)picks[j].y;
                q *= fall;
                EmgAddAcc(ref d, v, EmgPhaseConst.Charge, q, d.Id[u], enterNow: false);
                Cue(ref d, CombatEventKind.PhaseCue, v, d.Id[u], q, d.Pos[v], 64); // 64 = 电弧连线（表现：从放电者到导体）
                if (r.Side == EmgRuleConst.SideDamage && r.SideValue > 0f)
                {
                    DamageFrom(ref d, v, r.SideValue, -1, EmgCode.SrcZone); // 物质中立：电弧伤到谁都不归属出手者的击杀（友伤通知归 EMG2-DISC-01）
                }
            }
            picks.Dispose();
        }

        /// <summary>单位宿主的规则结算记账：可靠计数 + 因果链“规则”节点 + evt.react（宿主 = 单位，Code = 计数槽）。</summary>
        private static void EmgUnitRuleSettle(ref CombatData d, in EmgRule r, int chain, byte depth, int ownerId, int u)
        {
            double2 pos = d.Pos[u];
            int slot = EmgRuleHit(ref d, r.Key, ownerId, d.Id[u], pos);
            EmgScalars es = d.EmgS[0];
            es.RulesSettled++;
            d.EmgS[0] = es;
            EmgChainLog(ref d, EmgChainNode.Rule, chain, depth, r.Key, ownerId, pos, d.Id[u], r.Product);
            if (slot >= 0)
            {
                EmgEntityEvent(ref d, EmgEventKind.React, EmgEntKind.Unit, d.Id[u], EmgEntKind.None, 0, d.EmgRuleCount[slot], 0f, (ushort)slot, pos);
            }
        }

        /// <summary>去掉单位身上的沾染位（含叠层），重算状态效果与到期（与旧标签反应的“消耗”同一做法）。</summary>
        private static void EmgClearCoat(ref CombatData d, int u, uint bits)
        {
            if ((d.Status[u] & bits) == 0u)
            {
                return;
            }
            d.Status[u] &= ~bits;
            ulong stacks = d.StatusStacks[u];
            uint c = bits;
            while (c != 0u)
            {
                int b = math.tzcnt(c);
                c &= c - 1u;
                stacks &= ~(3UL << (b * 2));
            }
            d.StatusStacks[u] = stacks;
            RecomputeStatusFx(ref d, u);
            RecomputeStatusUntil(ref d, u);
        }

        /// <summary>C06 粘住：剩余秒随游戏时间走（暂停不走、倍速按游戏时间），归零时清掉 a 侧胶膜沾染。每步逐单位调用（O(1)）。</summary>
        internal static void StepStick(ref CombatData d, int i, float dt)
        {
            EmgUnitState st = d.EmgUnit[i];
            if (!(st.StickT > 0f))
            {
                return;
            }
            st.StickT = math.max(0f, st.StickT - dt);
            d.EmgUnit[i] = st;
            if (st.StickT <= 0f)
            {
                for (int r = 0; r < d.EmgRules.Length; r++)
                {
                    EmgRule rule = d.EmgRules[r];
                    if (rule.Pair == EmgRuleConst.PairCoatMatter && rule.Op == EmgRuleConst.OpStick && rule.A >= EmgRuleConst.CoatBase)
                    {
                        EmgClearCoat(ref d, i, 1u << (rule.A - EmgRuleConst.CoatBase));
                    }
                }
            }
        }

        /// <summary>被胶膜固化粘住（能开火、不能移动、不受推动）。</summary>
        internal static bool IsStuck(ref CombatData d, int i) => i >= 0 && i < d.Count && d.EmgUnit.IsCreated && i < d.EmgUnit.Length && d.EmgUnit[i].StickT > 0f;

        // ─────────────────────────────── P03 熔穿过载（相态 × 动能） ───────────────────────────────

        /// <summary>
        /// 同一次命中：注入的热 ≥ c1 × 结构积热换算（0.15 × 300 = 45）且单次动能（伤害 × 载体动能系数）≥ c2（40）→ 命中点溅出熔融金属（p1 份、p2 米、p3 度），
        /// 目标结构积热直接置 p4（1.0）进入熔穿相态。开火者的武器须带“本行在开火时可判定成立”（<see cref="CombatWeapon.Reaction"/> = 熔穿过载，热更层按规则表判定），
        /// 信号带进来的核心固件发动过一次后被压住（ReactionSpent）期间不结算（FG19B §4.1 A2）。
        /// </summary>
        internal static void EmgMeltOverloadHit(ref CombatData d, int t, int attacker, float heatIn, float damage)
        {
            if (!(heatIn > 0f) || attacker < 0 || attacker >= d.Count || !d.IsAlive(t) || !d.EmgRules.IsCreated)
            {
                return;
            }
            int w = d.Weapon[attacker];
            if (w < 0 || w >= d.Weapons.Length || d.Weapons[w].Reaction != CombatReaction.MeltOverload || d.Has(attacker, CombatUnitFlags.ReactionSpent))
            {
                return;
            }
            int ri = -1;
            for (int i = 0; i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Pair == EmgRuleConst.PairPhaseEnergy && r.B == EmgConst.PortKinetic && r.Cond == EmgRuleConst.CondSameHit)
                {
                    ri = i;
                    break;
                }
            }
            if (ri < 0)
            {
                return;
            }
            EmgRule rule = d.EmgRules[ri];
            float kinetic = damage * HitKineticOf(ref d, attacker);
            if (heatIn + 1e-3f < rule.C1 * UnitScaleOf(ref d) || kinetic + 1e-3f < rule.C2) // 容差：0.15f × 300 = 45.0000018，正好 45 的注热算达标
            {
                return;
            }
            int srcId = d.Id[attacker];
            double2 pos = d.Pos[t];
            EmgCtx c = d.EmgCtx;
            if (rule.Product != 0)
            {
                EmgRuleSpawn(ref d, rule.Product, pos, rule.P2 > 0f ? rule.P2 : 1f, rule.P1 > 0f ? rule.P1 : 4f, rule.P3, rule.Life, srcId, c.Chain, c.Depth); // EmgRuleSpawn 内部 +1（与 coatspawn 同一约定）
            }
            EmgSetAcc(ref d, t, EmgPhaseConst.Overheat, rule.P4 > 0f ? rule.P4 : 1f, srcId);
            EmgUnitRuleSettle(ref d, rule, c.Chain, c.Depth, srcId, t);
            if (d.Has(attacker, CombatUnitFlags.ReactionGated))
            {
                d.Set(attacker, CombatUnitFlags.ReactionSpent, true); // FG1-SIG-03：门控反应发动一次即压住，直到热更层按冷却重新下发
            }
        }

        // ─────────────────────────────── 固化胶块阻路（ADR ⑯ 动态障碍圆） ───────────────────────────────

        /// <summary>阻路物质（fg.TbMatter solid = 2：不可进入、可被打碎 / 融化）。</summary>
        internal static bool PatchBlocks(ref CombatData d, in CombatZone z) =>
            z.Matter > 0 && z.Matter <= EmgMatterConst.Count && z.Layer == EmgMatterConst.LayerGround && d.EmgMatters[z.Matter].Solid == EmgMatterConst.SolidBlock;

        /// <summary>
        /// 阻路斑块生成时：圈内的地面单位按 Id 升序推到离圆心最近的边缘点（单位半径外再留 0.05 米；正在圆心的推向 +x），确定性、防软锁（B11）。离地单位不受阻；
        /// 不可推动的（建筑、炮塔、敌方主核心，<see cref="CanDisplace"/>，与其他位移路径同一口径）留在原地——内核位置与热更层的建筑座位置不脱节。
        /// O(单位数)，只在阻路斑块生成那一刻。
        /// </summary>
        internal static void EmgBlockPushOut(ref CombatData d, int patch)
        {
            CombatZone z = d.Patches[patch];
            if (!PatchBlocks(ref d, z))
            {
                return;
            }
            var inside = new NativeList<int2>(8, Allocator.Temp); // (Id, 槽位)
            for (int v = 0; v < d.Count; v++)
            {
                if (!d.IsAlive(v) || IsAir(ref d, v) || !CanDisplace(ref d, v))
                {
                    continue;
                }
                if (math.distance(d.Pos[v], z.Pos) < z.Radius + d.Radius[v])
                {
                    inside.Add(new int2(d.Id[v], v));
                }
            }
            inside.Sort(new IdOrder());
            for (int k = 0; k < inside.Length; k++)
            {
                int v = inside[k].y;
                double2 away = d.Pos[v] - z.Pos;
                double len = math.length(away);
                double2 dir = len > 1e-6 ? away / len : new double2(1, 0);
                d.Pos[v] = z.Pos + dir * (z.Radius + d.Radius[v] + 0.05);
            }
            EmgScalars es = d.EmgS[0];
            es.BlockPushed += inside.Length;
            d.EmgS[0] = es;
            inside.Dispose();
        }

        private struct IdOrder : System.Collections.Generic.IComparer<int2>
        {
            public int Compare(int2 a, int2 b) => a.x.CompareTo(b.x);
        }

        /// <summary>
        /// 移动一步时不得进入阻路斑块：目标点落进（圆 + 单位半径）就沿圆边滑到边缘外（保留切向分量），本来就在圈里的（生成之后被别的东西推进去）往外走不拦。
        /// 只查目标点所在哈希格附近（O(格内条目)）；没有阻路斑块时一个计数判断就返回。离地单位不受阻。
        /// </summary>
        internal static double2 EmgBlockClamp(ref CombatData d, int i, double2 from, double2 to)
        {
            if (!d.EmgUnit.IsCreated || d.Patches.Length == 0 || (i >= 0 && i < d.Count && IsAir(ref d, i)))
            {
                return to;
            }
            EmgHashEnsure(ref d);
            if (d.EmgS[0].BlockLive <= 0)
            {
                return to;
            }
            float me = i >= 0 && i < d.Count ? d.Radius[i] : 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                int hit = EmgBlockAt(ref d, to, me);
                if (hit < 0)
                {
                    return to;
                }
                CombatZone z = d.Patches[hit];
                double reach = z.Radius + me;
                if (math.distance(from, z.Pos) < reach - 1e-3)
                {
                    return to; // 已在圈里：只许往外走（不卡死）
                }
                double2 away = to - z.Pos;
                double len = math.length(away);
                double2 dir = len > 1e-6 ? away / len : math.normalizesafe(from - z.Pos, new double2(1, 0));
                to = z.Pos + dir * (reach + 0.01);
            }
            return EmgBlockAt(ref d, to, me) >= 0 ? from : to;
        }

        /// <summary>点 <paramref name="p"/>（单位半径 <paramref name="me"/>）落在哪块阻路斑块里（−1 = 没有）。</summary>
        private static int EmgBlockAt(ref CombatData d, double2 p, float me)
        {
            int cx = EmgHash.CellOf(p.x), cy = EmgHash.CellOf(p.y);
            for (int yy = cy - 1; yy <= cy + 1; yy++)
            {
                for (int xx = cx - 1; xx <= cx + 1; xx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(xx, yy), out int e, out NativeParallelMultiHashMapIterator<long> it))
                    {
                        continue;
                    }
                    do
                    {
                        if ((byte)d.EmgHash.Entries[e].w != EmgHash.KindPatch)
                        {
                            continue;
                        }
                        int j = d.EmgHash.Refs[e];
                        if (j < 0 || j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone z = d.Patches[j];
                        if (PatchBlocks(ref d, z) && math.distance(p, z.Pos) < z.Radius + me)
                        {
                            return j;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return -1;
        }

        /// <summary>前方 <paramref name="look"/> 米内挡路的阻路斑块（局部避让，与障碍圆同一套“绕”）：沿前进方向在 1 / 2 / 3 个探点查哈希。</summary>
        private static bool EmgBlockAhead(ref CombatData d, double2 pos, float2 dir, float look, float me, out double2 center)
        {
            center = default;
            if (!d.EmgUnit.IsCreated || d.Patches.Length == 0)
            {
                return false;
            }
            EmgHashEnsure(ref d);
            if (d.EmgS[0].BlockLive <= 0)
            {
                return false;
            }
            for (int k = 1; k <= 3; k++)
            {
                int j = EmgBlockAt(ref d, pos + (double2)(dir * (look * k / 3f)), me);
                if (j >= 0)
                {
                    center = d.Patches[j].Pos;
                    return true;
                }
            }
            return false;
        }
    }
}
