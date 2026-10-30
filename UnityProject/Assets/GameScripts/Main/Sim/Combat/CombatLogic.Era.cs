using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-FW-01（FG19 FGR-EMG-233，第 11 节第 1 行第 3 层，2026-10-26 用户批准）：爆炸反应装甲的块数机制。全部在内核固定步里算，每处 O(1)（补块是散热之后的逐单位循环，只碰缺块的单位）。
    /// - 判定只认“带反伤读法”（Thorns 或 ThornsFlat &gt; 0），不判 func_spikes ID（FGR-EMG-013）。
    /// - 触发口径 = <see cref="ReflectMelee"/> 的三个调用点（即时命中；弹体、重炮、区域、斑块、场、撞击、无人机、回波、破片、反伤本身都不进来）——
    ///   evt.hurt 进总线是按需、异步的，来源码不区分“即时命中”，所以起爆挂在即时命中的同步处理点上（与反伤同一个入口），不从总线回读。
    /// - <see cref="CombatConfig.EmgEraBlocks"/> = 0（emg.stage = 0）时这里一律不进：反伤不限次数、不放破片，旧行为逐字不变。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>每台总块数（表校验 2～16 的偶数；内核再钳一次防坏值）。</summary>
        internal static int EmgEraN(ref CombatData d) => math.clamp(d.Config.EmgEraBlocks & ~1, 2, 16);

        internal static float EmgEraPeriod(ref CombatData d) => d.Config.EmgEraRefill > 0f ? d.Config.EmgEraRefill : 3f;

        /// <summary>块数机制开着、且这个单位的武器带反伤读法。</summary>
        internal static bool EmgEraArmed(ref CombatData d, int i)
        {
            if (d.Config.EmgEraBlocks == 0 || i < 0 || i >= d.Count)
            {
                return false;
            }
            int w = d.Weapon[i];
            return w >= 0 && w < d.Weapons.Length && (d.Weapons[w].Reading.Thorns > 0f || d.Weapons[w].Reading.ThornsFlat > 0f);
        }

        private static void EmgEraDirtyAdd(ref CombatData d, int slot)
        {
            int id = d.Id[slot];
            for (int k = d.EmgEraDirtyPublished; k < d.EmgEraDirty.Length; k++)
            {
                if (d.EmgEraDirty[k] == id)
                {
                    return;
                }
            }
            d.EmgEraDirty.Add(id);
        }

        /// <summary>
        /// 起爆（宿主 <paramref name="t"/> 被攻击者 <paramref name="a"/> 即时命中，触及与敌对已由 <see cref="ReflectMelee"/> 判过）：块为 0 → 什么都不做（不反伤、不放破片）；
        /// 否则选块 → 扣 1 块 → 按原公式反伤 <paramref name="reflect"/> → 放破片（链深已到 3 只计 DepthCut；弹体池满由 <see cref="EmgSpawnFragment"/> 顺延到待生成表，扣块与反伤当步完成）。
        /// 同一步多名攻击者按调用先后（= 命中先后，确定性）逐个扣块，块用完的不起爆。
        /// </summary>
        internal static void EmgEraDetonate(ref CombatData d, int a, int t, float reflect)
        {
            int n = EmgEraN(ref d);
            int full = (1 << n) - 1;
            EmgUnitState u = d.EmgUnit[t];
            int miss = u.EraMiss & full;
            if (miss == full)
            {
                return;
            }
            int b = EmgEraPick(ref d, t, a, n, miss);
            u.EraMiss = (ushort)(miss | (1 << b));
            d.EmgUnit[t] = u;
            EmgEraDirtyAdd(ref d, t);

            // 链：攻击者这一下在程序出手里就挂在它的链上（链深沿用），否则为起爆新开一条链（破片命中、因果链“左裙第 2 块起爆”都要有链可挂）。
            bool inCtx = d.EmgCtx.Prog >= 0 && d.EmgCtx.Owner == a;
            int chain = inCtx ? d.EmgCtx.Chain : 0;
            byte depth = inCtx ? d.EmgCtx.Depth : (byte)0;
            if (chain <= 0)
            {
                EmgScalars es = d.EmgS[0];
                chain = es.NextChain++;
                d.EmgS[0] = es;
            }
            EmgChainLog(ref d, EmgChainNode.Era, chain, depth, (int)EmgEventKind.Hurt, d.Id[t], d.Pos[t], d.Id[a], (byte)(b + 1));
            NoteReading(ref d, CombatConst.ReadingFeedEra, d.Pos[t], t);
            if (reflect > 0f)
            {
                DamageUnit(ref d, a, reflect, t); // 反伤公式与数值不动；反伤不经 ReflectMelee，不会再起爆 / 再反伤
                // LANDING-PLAN 第 12 节 #7 按推荐默认执行（2026-10-28），待用户确认：反应装甲反伤发 evt.hit（宿主 = 装甲宿主，Other = 攻击者）。
                // 只进总线（按需），不回灌 evt.hurt 起爆入口——起爆只认 ReflectMelee 的即时命中调用点，所以不会递归。
                if (EmgSiteListens(ref d, EmgEventKind.Hit))
                {
                    EmgUnitEvent(ref d, EmgEventKind.Hit, t, d.Id[a], reflect, 0f, EmgCode.HitDirect, d.Pos[a]);
                }
            }

            EmgEntityParams ep = d.EmgEntParams[0];
            if (ep.EraFragCount <= 0 || !(ep.EraFragSpeed > 0f) || !(ep.EraFragRange > 0f))
            {
                return;
            }
            if (depth >= EmgConst.MaxDepth)
            {
                EmgBusStats st = d.EmgStats[0];
                st.DepthCut++;
                st.LastDepthCutChain = chain;
                d.EmgStats[0] = st;
                EmgChainLog(ref d, EmgChainNode.DepthCut, chain, depth, d.Id[t], d.Id[t], d.Pos[t]);
                return;
            }
            double2 to = d.Pos[a] - d.Pos[t];
            float2 fwd = math.lengthsq(to) > 1e-12 ? (float2)math.normalize(to) : EmgEraFacing(ref d, t);
            double2 from = d.Pos[t] + (double2)(fwd * (d.Radius[t] + 0.15f));
            int cnt = ep.EraFragCount;
            for (int q = 0; q < cnt; q++)
            {
                float spread = cnt > 1 ? math.radians(-30f + 60f * q / (cnt - 1)) : 0f; // FG19B 第 2.9 节：攻击者方向为中心 ±30° 扇面均布
                float cs = math.cos(spread), sn = math.sin(spread);
                float2 v = new float2(fwd.x * cs - fwd.y * sn, fwd.x * sn + fwd.y * cs);
                EmgSpawnFragment(ref d, from, v, d.Id[t], (CombatFaction)d.Faction[t], (byte)(depth + 1), chain, ep.EraFragDamage, ep.EraFragSpeed, ep.EraFragRange);
            }
        }

        private static float2 EmgEraFacing(ref CombatData d, int t)
        {
            float2 f = d.Armor[t].zw; // 单位朝向（CombatKernel.SetFacing 写入；装甲朝向同一个量）
            return math.lengthsq(f) > 1e-12f ? math.normalize(f) : new float2(0f, 1f);
        }

        /// <summary>
        /// 选块（只影响表现与因果链）：攻击者在朝向左侧取左裙、右侧取右裙，正前 / 正后 ±15° 内取完整块多的一侧（相同取左）；
        /// 前半侧取该侧最前一块完整块，后半侧取最后一块；该侧没有完整块时按同一规则取另一侧。
        /// </summary>
        internal static int EmgEraPick(ref CombatData d, int t, int a, int n, int miss)
        {
            int half = n >> 1;
            float2 f = EmgEraFacing(ref d, t);
            double2 v = d.Pos[a] - d.Pos[t];
            float2 r = math.lengthsq(v) > 1e-12 ? (float2)math.normalize(v) : f;
            float cross = f.x * r.y - f.y * r.x; // > 0：攻击者在左侧（俯视逆时针）
            bool front = math.dot(f, r) >= 0f;
            int sideMask = (1 << half) - 1;
            int leftIntact = half - math.countbits(miss & sideMask);
            int rightIntact = half - math.countbits((miss >> half) & sideMask);
            bool left = math.abs(cross) <= 0.258819f ? leftIntact >= rightIntact : cross > 0f; // sin 15°
            int b = EmgEraSideBlock(miss, left ? 0 : half, half, front);
            return b >= 0 ? b : EmgEraSideBlock(miss, left ? half : 0, half, front);
        }

        private static int EmgEraSideBlock(int miss, int start, int half, bool front)
        {
            for (int k = 0; k < half; k++)
            {
                int bit = start + (front ? k : half - 1 - k);
                if (((miss >> bit) & 1) == 0)
                {
                    return bit;
                }
            }
            return -1;
        }

        /// <summary>
        /// 补块（散热之后、只对缺块的存活单位）：每 emg.era.refill_seconds 补回 1 块，再次被击不重置计时，满块时计时归零；
        /// 补位顺序：完整块少的一侧优先（相同取左），该侧从前往后第一个缺位。武器已不带反伤读法（拆掉装甲）时清成满块，下次装上按满块。
        /// </summary>
        internal static void EmgEraRefill(ref CombatData d, int i, float dt)
        {
            EmgUnitState u = d.EmgUnit[i];
            int n = EmgEraN(ref d);
            int miss = u.EraMiss & ((1 << n) - 1);
            if (miss == 0 || !EmgEraArmed(ref d, i))
            {
                u.EraMiss = 0;
                u.EraRefillT = 0f;
                d.EmgUnit[i] = u;
                EmgEraDirtyAdd(ref d, i);
                return;
            }
            float period = EmgEraPeriod(ref d);
            u.EraRefillT += dt;
            bool changed = false;
            int half = n >> 1;
            int sideMask = (1 << half) - 1;
            while (miss != 0 && u.EraRefillT >= period - 1e-4f)
            {
                u.EraRefillT = math.max(0f, u.EraRefillT - period);
                int leftIntact = half - math.countbits(miss & sideMask);
                int rightIntact = half - math.countbits((miss >> half) & sideMask);
                bool left = leftIntact <= rightIntact;
                int b = EmgEraFirstMissing(miss, left ? 0 : half, half);
                if (b < 0)
                {
                    b = EmgEraFirstMissing(miss, left ? half : 0, half);
                }
                miss &= ~(1 << b);
                changed = true;
            }
            if (miss == 0)
            {
                u.EraRefillT = 0f;
            }
            u.EraMiss = (ushort)miss;
            d.EmgUnit[i] = u;
            if (changed)
            {
                EmgEraDirtyAdd(ref d, i);
            }
        }

        private static int EmgEraFirstMissing(int miss, int start, int half)
        {
            for (int k = 0; k < half; k++)
            {
                if (((miss >> (start + k)) & 1) != 0)
                {
                    return start + k;
                }
            }
            return -1;
        }
    }
}
