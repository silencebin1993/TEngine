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
        /// <summary>站进熔融金属对沾染行算一次足够的注热（只用来过 inject_ge 阈值，不改单位积热）。</summary>
        private const float MoltenContactHeat = 1000f;

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
                    e = elecIn;
                    ok = elecIn >= r.C1; // EMG2-REACT-02（FG19B §3.1 位 4）：电击不再是持续标签，电击标签 = 一次 en.elec 注入（PhaseTagGain 按 phase_charge.tagGain 换算），这里只看注入量
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
            // EMG2-DISC-01：单位身上的反应没有“斑块归属”。本步伤害行（不进快照）归这条链上的单位 + 这次的来源（ADR ⑨）；
            // 产物斑块和顺延记录进快照与 StateHash，环读档后是空的，所以它们只继承键、归属由 EmgOwnersOfNew 取来源（否则存读档后分叉）。
            int key0 = d.EmgRuleKeyNow;
            int4 owners0 = d.EmgRuleOwnersNow;
            int4 coatOwners = EmgChainAttr(ref d, chain, out _);
            EmgOwnersAdd(ref coatOwners, srcId);
            d.EmgRuleKeyNow = rule.Key;
            d.EmgRuleOwnersNow = int4.zero;
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
                    EmgCoatArc(ref d, u, rule, energy, consumed, elecIn > 0f, coatOwners);
                    break;
                case EmgRuleConst.OpStick:
                {
                    EmgUnitState st = d.EmgUnit[u];
                    st.StickT = math.max(st.StickT, rule.P1 > 0f ? rule.P1 : 2.5f);
                    d.EmgUnit[u] = st;
                    consumed = 0u; // 胶膜在粘住结束时清除（FG19B C06“结束后胶膜清除”）
                    break;
                }
                case EmgRuleConst.OpCoatFlash:
                    EmgCoatFlash(ref d, u, rule, energy, chain, depth);
                    consumed = 0u; // C04 不消耗油污
                    break;
                case EmgRuleConst.OpCoatTrail:
                {
                    int fx = EmgCoatFxOf(ref d, d.Id[u], true);
                    EmgCoatFx x = d.CoatFx[fx];
                    x.TrailT = math.max(x.TrailT, rule.P1 > 0f ? rule.P1 : 4f);
                    x.TrailMoved = 0f;
                    x.TrailIdle = 0f;
                    x.TrailLast = pos;
                    x.Chain = chain;
                    d.CoatFx[fx] = x;
                    break;
                }
                case EmgRuleConst.OpCoatSlow:
                {
                    int fx = EmgCoatFxOf(ref d, d.Id[u], true);
                    EmgCoatFx x = d.CoatFx[fx];
                    x.MudT = math.max(x.MudT, rule.P2 > 0f ? rule.P2 : 4f);
                    d.CoatFx[fx] = x;
                    ApplyStatus(ref d, u, CombatConst.StatusBitZoneSlow, x.MudT, 0f, rule.P1, 0f, source); // 减速走区域减速位（敌我同一条移动口径，到期自清）
                    consumed |= 1u << (rule.B - EmgRuleConst.CoatBase); // C07 同时消耗扬尘、浸湿
                    break;
                }
            }
            int settled = EmgUnitRuleSettle(ref d, rule, chain, depth, srcId, u); // 这里 d.EmgCtx 是 side0（Prog = −1），载荷宿主的 evt.react 在恢复上下文后补发
            if (rule.Side == EmgRuleConst.SideDamage && rule.SideValue > 0f && d.IsAlive(u) && rule.Op != EmgRuleConst.OpCoatArc)
            {
                EmgAttrBegin(ref d, rule.Key, coatOwners, EmgFriendlyCode.Reaction, chain);
                DamageFrom(ref d, u, rule.SideValue, source >= 0 && source < d.Count ? source : -1, EmgCode.SrcZone);
                d.EmgAttr.On = 0;
            }
            d.EmgRuleKeyNow = key0;
            d.EmgRuleOwnersNow = owners0;
            d.EmgCtx = saved;
            es = d.EmgS[0];
            es.CoatBusy = 0;
            d.EmgS[0] = es;
            EmgReactHosted(ref d, settled, pos, d.Id[u]);
            if (consumed != 0u)
            {
                EmgClearCoat(ref d, u, consumed);
            }
            return incoming & ~consumed;
        }

        /// <summary>
        /// C04 这一次注电是否被闪络顶掉（沾油污且注入量 ≥ 阈值）：调用方不给本单位加电荷（FG19B C04“本单位不得电荷”），改由沾染行把电跳走。O(规则数)，只在注电时。
        /// 优先级高于 C03（29 &lt; 32），所以只要这一行成立就不会再走短路。
        /// </summary>
        internal static bool EmgElecShed(ref CombatData d, int u, float elec)
        {
            if (!d.EmgRules.IsCreated || !(elec > 0f) || !d.IsAlive(u))
            {
                return false;
            }
            uint have = d.Status[u];
            for (int i = 0; i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Op == EmgRuleConst.OpCoatFlash && r.A >= EmgRuleConst.CoatBase && (have & (1u << (r.A - EmgRuleConst.CoatBase))) != 0u && elec >= r.C1)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// C04 闪络：这股电改跳 p3 米内最近的另一个导体（单位 / 导电斑块 / 残骸，含己方），量 × p1；附近没有导体就消散。
        /// EMG3-CHS-01（DEBT-EMG2REACT02-04，格式 18）：哨戒桩（定点无人机）也是导体，算跳点（电荷记在 <see cref="CombatDrone.Charge"/>，每秒衰减）。O(单位 + 斑块 + 残骸 + 无人机)，只在闪络那一刻。
        /// </summary>
        private static void EmgCoatFlash(ref CombatData d, int u, in EmgRule r, float energy, int chain, byte depth)
        {
            float q = math.max(0f, energy) * (r.P1 > 0f ? r.P1 : 0.8f);
            double reach = r.P3 > 0f ? r.P3 : 4f;
            if (!(q > 0f))
            {
                return;
            }
            double2 pos = d.Pos[u];
            double best = reach + 1e-6;
            int bestUnit = -1, bestPatch = -1, bestWreck = -1, bestDrone = -1;
            for (int v = 0; v < d.Count; v++)
            {
                if (v == u || !d.IsAlive(v) || d.Kind[v] == (byte)CombatUnitKind.Structure || IsAir(ref d, v) || !EmgUnitConducts(ref d, v))
                {
                    continue;
                }
                double dist = math.distance(pos, d.Pos[v]);
                if (dist < best || (dist == best && bestUnit >= 0 && d.Id[v] < d.Id[bestUnit]))
                {
                    best = dist;
                    bestUnit = v;
                }
            }
            for (int j = 0; j < d.Patches.Length; j++)
            {
                CombatZone z = d.Patches[j];
                if (!PatchAlive(z) || MatterOf(ref d, z.Matter).Conductive < EmgRuleConst.DefaultConductMinStrength)
                {
                    continue;
                }
                double dist = math.max(0.0, math.distance(pos, z.Pos) - z.Radius);
                if (dist < best || (dist == best && bestPatch >= 0 && z.EmgId < d.Patches[bestPatch].EmgId)) // 同距按 EmgId：墓碑复用让读档后的槽位布局与连续跑不同（同 EmgGroundPatchAt）
                {
                    best = dist;
                    bestUnit = -1;
                    bestPatch = j;
                }
            }
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck k = d.Wrecks[w];
                if (!(k.Hp > 0f) || EmgWreckProps(ref d, k).Conductive < EmgRuleConst.DefaultConductMinStrength)
                {
                    continue;
                }
                double dist = math.max(0.0, math.distance(pos, k.Pos) - k.Radius);
                if (dist < best)
                {
                    best = dist;
                    bestUnit = -1;
                    bestPatch = -1;
                    bestWreck = w;
                }
            }
            for (int k = 0; k < d.Drones.Length; k++)
            {
                CombatDrone dr = d.Drones[k];
                if (dr.Anchored == 0)
                {
                    continue; // 只有哨戒桩（定点、落地）是导体；飞行无人机在空中，不接地面电弧
                }
                double dist = math.distance(pos, dr.Pos);
                if (dist < best)
                {
                    best = dist;
                    bestUnit = -1;
                    bestPatch = -1;
                    bestWreck = -1;
                    bestDrone = k;
                }
            }
            if (bestDrone >= 0)
            {
                CombatDrone dr = d.Drones[bestDrone];
                dr.Charge += q;
                d.Drones[bestDrone] = dr;
                Cue(ref d, CombatEventKind.PhaseCue, u, d.Id[u], q, dr.Pos, 64); // 64 = 电弧连线（落点 = 哨戒桩）
            }
            else if (bestUnit >= 0)
            {
                EmgAddAcc(ref d, bestUnit, EmgPhaseConst.Charge, q, d.Id[u], enterNow: false);
                Cue(ref d, CombatEventKind.PhaseCue, bestUnit, d.Id[u], q, d.Pos[bestUnit], 64); // 64 = 电弧连线
            }
            else if (bestPatch >= 0)
            {
                CombatZone z = d.Patches[bestPatch];
                z.Charge += q;
                z.EmgChain = z.EmgChain != 0 ? z.EmgChain : chain;
                z.EmgDepth = (byte)math.max((int)z.EmgDepth, (int)depth);
                d.Patches[bestPatch] = z;
            }
            else if (bestWreck >= 0)
            {
                EmgWreck k = d.Wrecks[bestWreck];
                k.Charge += q;
                d.Wrecks[bestWreck] = k;
            }
        }

        /// <summary>单位算不算导体（prop.conductive ≥ 0.3；C07 泥浆附着期间视为 0，不进传导图）。</summary>
        internal static bool EmgUnitConducts(ref CombatData d, int v) =>
            !EmgMudded(ref d, v) && EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, v)).Conductive >= EmgRuleConst.DefaultConductMinStrength;

        /// <summary>C03：本单位电荷注入 × p1；电弧跳向 p3 米内最多 p2 个导电（prop.conductive ≥ 0.3）或带同一沾染（浸湿）的单位（含己方、中立），第 k 跳 × p4^k；每跳附带伤害。
        /// <paramref name="injected"/>：这份电荷已由命中注能（EmgHitInject）加过 1 倍，这里只补 (p1 − 1) 倍，合计 = 注入 × p1。</summary>
        private static void EmgCoatArc(ref CombatData d, int u, in EmgRule r, float energy, uint coatBit, bool injected, in int4 owners)
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
                if (dist > range || EmgMudded(ref d, v) || ((d.Status[v] & coatBit) == 0u && !EmgUnitConducts(ref d, v))) // EMG2-REACT-02（C07）：泥浆附着的单位电弧跳不到
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
                    EmgAttrBegin(ref d, r.Key, owners, EmgFriendlyCode.Energy, d.EmgCtx.Chain); // EMG2-DISC-01（FGR-EMG-182 / 184）
                    DamageFrom(ref d, v, r.SideValue, -1, EmgCode.SrcZone); // 物质中立：电弧伤到谁都不归属出手者的击杀
                    d.EmgAttr.On = 0;
                }
            }
            picks.Dispose();
        }

        /// <summary>单位宿主的规则结算记账：可靠计数 + 因果链“规则”节点 + evt.react（宿主 = 单位，Code = 计数槽）。</summary>
        private static int EmgUnitRuleSettle(ref CombatData d, in EmgRule r, int chain, byte depth, int ownerId, int u)
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
                EmgReactHosted(ref d, slot, pos, d.Id[u]);
            }
            return slot;
        }

        /// <summary>
        /// EMG3-FW-01（FG19A 第 1 节第 22 行反应增幅 / D-12：“本载荷引发的每次通道交互，在交互点再放出 1 份子载荷”）：规则在某个载荷的结算里成立时
        /// （宿主 = 正在结算的弹体 / 出手，d.EmgCtx.Prog ≥ 0），另按这个载荷宿主发一次 evt.react，读 evt.react 的触发器在交互点放出子载荷。
        /// 按需：没有程序读 evt.react 时 EmgShotEvent 首行即返回；不转发（中继只转发 Relayable 事件）。斑块 / 单位宿主的 evt.react 照旧另发（图鉴计数口径不变）。
        /// </summary>
        private static void EmgReactHosted(ref CombatData d, int slot, double2 pos, int unitId)
        {
            if (slot < 0 || d.EmgCtx.Prog < 0 || d.EmgCtx.EntKind == EmgEntKind.None)
            {
                return;
            }
            EmgShotEvent(ref d, EmgEventKind.React, unitId, d.EmgRuleCount[slot], 0f, (ushort)slot, pos, unitId != 0 ? EmgEntKind.Unit : EmgEntKind.None);
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

        /// <summary>单位 Id 在沾染行计时表里的下标；没有且 <paramref name="add"/> = 新增一行。</summary>
        private static int EmgCoatFxOf(ref CombatData d, int unitId, bool add)
        {
            for (int i = 0; i < d.CoatFx.Length; i++)
            {
                if (d.CoatFx[i].Unit == unitId)
                {
                    return i;
                }
            }
            if (!add)
            {
                return -1;
            }
            d.CoatFx.Add(new EmgCoatFx { Unit = unitId });
            return d.CoatFx.Length - 1;
        }

        /// <summary>C07 泥浆附着中（prop.conductive 视为 0）。O(计时表)，表空时一条比较。</summary>
        private static bool EmgMudded(ref CombatData d, int v)
        {
            if (d.CoatFx.Length == 0)
            {
                return false;
            }
            int i = EmgCoatFxOf(ref d, d.Id[v], false);
            return i >= 0 && d.CoatFx[i].MudT > 0f;
        }

        /// <summary>
        /// C05 燃烧的酸液滴落 / C07 泥浆计时（每步，随游戏时间走，暂停不走、倍速按游戏时间）。每走 p2 米在身后留一块明火，静止时每秒一块（FG19B C05）。单位没了或计时走完就删行。O(计时表)。
        /// </summary>
        internal static void StepCoatFx(ref CombatData d, float dt)
        {
            for (int i = d.CoatFx.Length - 1; i >= 0; i--)
            {
                EmgCoatFx x = d.CoatFx[i];
                int u = d.SlotOf(x.Unit);
                if (u < 0 || !d.IsAlive(u))
                {
                    d.CoatFx.RemoveAt(i);
                    continue;
                }
                x.MudT = math.max(0f, x.MudT - dt);
                if (x.TrailT > 0f)
                {
                    x.TrailT = math.max(0f, x.TrailT - dt);
                    double2 pos = d.Pos[u];
                    x.TrailMoved += (float)math.distance(pos, x.TrailLast);
                    x.TrailIdle += dt;
                    double2 behind = x.TrailLast;
                    x.TrailLast = pos;
                    int ri = -1;
                    for (int r = 0; r < d.EmgRules.Length && ri < 0; r++)
                    {
                        if (d.EmgRules[r].Op == EmgRuleConst.OpCoatTrail)
                        {
                            ri = r;
                        }
                    }
                    if (ri >= 0)
                    {
                        EmgRule rule = d.EmgRules[ri];
                        if (x.TrailMoved >= (rule.P2 > 0f ? rule.P2 : 1.5f) || x.TrailIdle >= 1f)
                        {
                            x.TrailMoved = 0f;
                            x.TrailIdle = 0f;
                            EmgRuleSpawn(ref d, rule.Product != 0 ? rule.Product : EmgMatterConst.Fire, behind, rule.P3 > 0f ? rule.P3 : 1f, rule.P4 > 0f ? rule.P4 : 3f, EmgRuleConst.KeepTemp - 1f, rule.Life, x.Unit, x.Chain, 0);
                        }
                    }
                }
                if (x.MudT <= 0f && x.TrailT <= 0f)
                {
                    d.CoatFx.RemoveAt(i);
                }
                else
                {
                    d.CoatFx[i] = x;
                }
            }
        }

        // ─────────────────────────────── 残骸 × 物质（W01 / W02） ───────────────────────────────

        /// <summary>matter_entity 行：按 a 侧物质端口找，不认规则 ID。没有 = −1。O(规则数)。</summary>
        private static int EmgEntityRule(ref CombatData d, byte matter)
        {
            for (int i = 0; d.EmgRules.IsCreated && i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Pair == EmgRuleConst.PairMatterEntity && EmgMatterConst.MatterOfPort(r.A) == matter)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// EMG3-FW-01（散热鳍声明 prop.heat modify，FG19A 第 2.2 节）：装散热件的单位站在 condition 所列物质的斑块里时散热件那份散热的倍率，否则 1。
        /// 没配置或没有斑块时直接返回（空场景零开销）；O(掩码位数 × 本格登记数)，只对挂散热件的单位。
        /// </summary>
        internal static float EmgFinWetFactor(ref CombatData d, int i)
        {
            uint mask = d.Config.EmgFinWetMatters;
            if (mask == 0u || !(d.Config.EmgFinWetMul > 0f) || d.Patches.Length == 0)
            {
                return 1f;
            }
            double2 p = d.Pos[i];
            while (mask != 0u)
            {
                int m = math.tzcnt(mask);
                mask &= mask - 1u;
                if (EmgGroundPatchAt(ref d, p, (byte)m) >= 0)
                {
                    return d.Config.EmgFinWetMul;
                }
            }
            return 1f;
        }

        /// <summary>点 <paramref name="p"/> 所在的地面层 <paramref name="matter"/> 斑块（圆心在斑块内才算，−1 = 没有）。Id 最小的优先（确定性）。O(本格登记数)。</summary>
        private static int EmgGroundPatchAt(ref CombatData d, double2 p, byte matter)
        {
            int found = -1;
            if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(p.x), EmgHash.CellOf(p.y)), out int e, out NativeParallelMultiHashMapIterator<long> it))
            {
                return -1;
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
                if (PatchAlive(z) && z.Matter == matter && math.distancesq(p, z.Pos) <= (double)z.Radius * z.Radius && (found < 0 || z.EmgId < d.Patches[found].EmgId))
                {
                    found = j;
                }
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
            return found;
        }

        /// <summary>
        /// 每步逐残骸（StepWrecks，只在有斑块、有 W 行时）：W01 圆心在酸斑块里 ≥ c1 秒 → 完整度每秒 −p1，归零变金属碎屑（份量 p2 × 质量，≤ life），每秒耗酸 p4；
        /// W02 圆心在污泥斑块里 ≥ c1 秒 → 锈蚀崩塌（破片 + 伴生碎屑）。优先级：同时在酸与污泥里按 W01（85 &lt; 86）。驻留记在 DwellMatter / DwellSince（进快照）。
        /// </summary>
        internal static void StepWreckDwell(ref CombatData d, int w, float dt, double now, int acidRule, int sludgeRule)
        {
            EmgWreck k = d.Wrecks[w];
            if (!(k.Hp > 0f))
            {
                return;
            }
            int acid = acidRule >= 0 ? EmgGroundPatchAt(ref d, k.Pos, EmgMatterConst.Acid) : -1;
            int sludge = acid < 0 && sludgeRule >= 0 ? EmgGroundPatchAt(ref d, k.Pos, EmgMatterConst.Sludge) : -1;
            byte matter = acid >= 0 ? EmgMatterConst.Acid : sludge >= 0 ? EmgMatterConst.Sludge : (byte)0;
            if (k.DwellMatter != matter)
            {
                k.DwellMatter = matter;
                k.DwellSince = now;
                d.Wrecks[w] = k;
            }
            if (matter == 0)
            {
                return;
            }
            double held = now - k.DwellSince;
            if (acid >= 0)
            {
                EmgRule r = d.EmgRules[acidRule];
                if (held + 1e-6 < r.C1)
                {
                    return;
                }
                k.Hp -= k.MaxHp * r.P1 * dt;
                d.Wrecks[w] = k;
                CombatZone z = d.Patches[acid];
                if (!PatchWorld(z) && r.P4 > 0f)
                {
                    EmgResizePatch(ref d, acid, z.Amount * (1f - r.P4 * dt));
                }
                if (!(k.Hp > 0f))
                {
                    float amount = math.min(r.Life > 0f ? r.Life : 20f, r.P2 * k.Mass);
                    EmgRuleSpawn(ref d, r.Product, k.Pos, math.max(k.Radius, 0.5f), math.max(0.1f, amount), EmgRuleConst.KeepTemp - 1f, 0f, 0, k.Chain, 0);
                    EmgRuleSettle(ref d, r, k.Chain, 0, 0, k.Pos, k.Id);
                }
                return;
            }
            EmgRule rs = d.EmgRules[sludgeRule];
            if (held + 1e-6 >= rs.C1)
            {
                EmgWreckCollapse(ref d, w, rs, 0, CombatFaction.Neutral, 0, k.Chain);
            }
        }

        /// <summary>W02 锈蚀崩塌：残骸崩解为 p1 片锈蚀破片（放射），伴生碎屑（by 比例 × 质量，≤ life）。破片的连锁只靠动能（链深截止），没有接触传播（FG19B W02）。</summary>
        private static void EmgWreckCollapse(ref CombatData d, int w, in EmgRule r, int ownerId, CombatFaction faction, byte depth, int chain)
        {
            EmgWreck k = d.Wrecks[w];
            k.Hp = 0f;
            d.Wrecks[w] = k;
            int n = math.max(1, (int)math.round(r.P1));
            if (depth < EmgConst.MaxDepth)
            {
                float off = (uint)math.hash(new int2(k.Id, n)) % 360u * (math.PI / 180f);
                for (int f = 0; f < n; f++)
                {
                    float a = off + f * (2f * math.PI / n);
                    float2 v = new float2(math.cos(a), math.sin(a));
                    EmgSpawnFragment(ref d, k.Pos + (double2)(v * (k.Radius + 0.15f)), v, ownerId, faction, (byte)(depth + 1), chain, r.P2, r.P3, r.P4);
                }
            }
            if (r.By > 0)
            {
                EmgRuleSpawn(ref d, r.By, k.Pos, math.max(k.Radius, 0.5f), math.max(0.1f, math.min(r.Life > 0f ? r.Life : 12f, r.ByAmount * k.Mass)), EmgRuleConst.KeepTemp - 1f, 0f, ownerId, chain, depth);
            }
            EmgRuleSettle(ref d, r, chain, depth, ownerId, k.Pos, k.Id);
            MarkHashDirty(ref d);
        }

        // ─────────────────────────────── 湮灭（F01 属性 × 场） ───────────────────────────────

        /// <summary>prop_field 行里 b = 偏折场的那一行（F01）；没有 = −1。</summary>
        internal static int EmgAnnihilateRule(ref CombatData d)
        {
            for (int i = 0; d.EmgRules.IsCreated && i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Pair == EmgRuleConst.PairPropField && r.B == EmgFieldConst.PortDeflect)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// F01：单位处于与它敌对的偏折场内且完整度 ≤ c1 → 压溃起爆：被击毁、不留残骸，原地起爆（半径 p1、动能 p3，附带每单位 side）。已被处决 / 别的伤害击毁的单位不再结算（调用方先查存活）。
        /// 不可推动的固定目标（建筑、炮塔、敌方主核心，<see cref="CanDisplace"/>）不被压溃（待用户复核的决策）。返回 true = 结算了。
        /// </summary>
        internal static bool EmgAnnihilate(ref CombatData d, int u, in EmgRule r, in EmgField f)
        {
            if (!CanDisplace(ref d, u) || d.Has(u, CombatUnitFlags.Invulnerable) || !(d.MaxHp[u] > 0f) || d.Hp[u] / d.MaxHp[u] > r.C1)
            {
                return false;
            }
            double2 pos = d.Pos[u];
            int unitId = d.Id[u];
            EmgChainLog(ref d, EmgChainNode.Rule, f.Chain, f.Depth, r.Key, f.Owner, pos, unitId, 0);
            EmgRuleSettle(ref d, r, f.Chain, f.Depth, f.Owner, pos, unitId);
            int killer = d.SlotOf(f.Owner);
            EmgDestroyDirect(ref d, u, killer);
            float rad = r.P1 > 0f ? r.P1 : 3f;
            EmgUnitsInCircle(ref d, pos, rad, r.SideValue, f.Owner, false, 0f, r.Key, new int4(f.Owner, 0, 0, 0), f.Chain, false); // 场的湮灭爆发不是地面层斑块
            EmgBurstPatches(ref d, pos, rad, r.P4, r.P3);
            return true;
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
                if (!d.IsAlive(v) || ChsAirborne(ref d, v) || !CanDisplace(ref d, v))
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
            if (!d.EmgUnit.IsCreated || d.Patches.Length == 0 || (i >= 0 && i < d.Count && ChsAirborne(ref d, i))) // EMG3-CHS-01：离地（chs.airborne）不受地面固态斑块阻挡（ADR-EMG-001 固态斑块条）
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
