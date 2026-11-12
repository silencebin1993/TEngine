using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG4-TER-01（FG19 FGR-EMG-152、154，FG19B V06 / V07，ADR-EMG-001 ⑯）：熔坑。
    /// - 可熔地面由生成规则标记（ter.ground）：家园按寻路镜像里的世界生成参数逐格取生成地形码（与区块生成同一个纯函数），码在 <see cref="CombatConfig.EmgMeltCodes"/> 里即可熔；
    ///   远征地点由热更层把生成内容里的可熔格写成圆（<see cref="CombatData.MeltGround"/>）。任意种子成立，不依赖固定坐标。
    /// - V06：熔融金属斑块凝固 / 淬火（原地转化，evt.phase）时、V07：酸斑块到期（evt.expire）时，在可熔地面上把份量记进“同一位置”累计点；某种份量到规则阈值 c1 就在累计点处形成熔坑。
    /// - 熔坑：地面单位不可进入（与阻路斑块同一套“推出 + 不可进入 + 局部绕行”），离地不受影响；家园寻路镜像随阻路集合签名增量更新。每地点 ≤ emg.terrain.pit_max，满了不生效并计数（热更层提示）。
    /// 全部是事件驱动（只在斑块转化 / 到期那一刻），没有每步遍历；熔坑数为 0 时移动只多一次长度比较。只认物质码、规则行、地形码，不认任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public static partial class CombatLogic
    {
        private static int PitMaxOf(ref CombatData d) => d.Config.EmgPitMax > 0 ? d.Config.EmgPitMax : EmgPitConst.DefaultMax;
        private static float PitRadiusOf(ref CombatData d) => d.Config.EmgPitRadius > 0f ? d.Config.EmgPitRadius : EmgPitConst.DefaultRadius;
        private static float PitGatherOf(ref CombatData d) => d.Config.EmgPitGather > 0f ? d.Config.EmgPitGather : EmgPitConst.DefaultGather;

        /// <summary>
        /// 这一点是不是可熔地面（远征地点的可熔圆；家园按生成规则逐格）。<see cref="CombatData.MeltGround"/> 里 z &lt; 0 的项是逐格覆盖
        /// （家园玩家改过地形的格，例如拆完的废墟：z = <see cref="EmgPitConst.CellNotMeltable"/> / <see cref="EmgPitConst.CellMeltable"/>），优先于圆与生成采样。
        /// </summary>
        internal static bool EmgMeltableAt(ref CombatData d, double2 p)
        {
            int2 cell = CellOf(p);
            bool inCircle = false;
            for (int i = 0; i < d.MeltGround.Length; i++)
            {
                float3 c = d.MeltGround[i];
                if (c.z < 0f)
                {
                    if ((int)c.x == cell.x && (int)c.y == cell.y)
                    {
                        return c.z == EmgPitConst.CellMeltable;
                    }
                }
                else if (!inCircle && math.distancesq(p, new double2(c.x, c.y)) <= (double)c.z * c.z)
                {
                    inCircle = true;
                }
            }
            if (inCircle)
            {
                return true;
            }
            if (d.Config.EmgMeltCodes == 0ul || !d.Nav.IsCreated || d.Nav.S[0].HasGen == 0)
            {
                return false;
            }
            if (!NavGridOps.InWorld(ref d.Nav, cell.x, cell.y))
            {
                return false;
            }
            NavGridScalars s = d.Nav.S[0];
            WorldGenMath.Sample(in s.Gen, cell.x, cell.y, d.Nav.Rects, s.RectCount, d.Nav.Zones, s.ZoneCount, out byte terrain, out _);
            return terrain < 64 && (d.Config.EmgMeltCodes & (1ul << terrain)) != 0ul;
        }

        /// <summary>
        /// 斑块 <paramref name="z"/>（转化 / 到期前的样子）发生事件 <paramref name="cond"/>（<see cref="EmgRuleConst.CondPitMolten"/> 凝固、<see cref="EmgRuleConst.CondPitAcid"/> 酸到期）：
        /// 按 event_matter 索引里本物质 op = meltpit、条件相符的第一条规则（优先级序）累计并按需形成熔坑。没有这种规则行时一次索引长度判断就返回。
        /// </summary>
        internal static void EmgPitFeed(ref CombatData d, in CombatZone z, byte cond)
        {
            if (!d.EmgRules.IsCreated || z.Matter == 0 || z.Matter > EmgMatterConst.Count || !(z.Amount > 0.01f) || z.Layer != EmgMatterConst.LayerGround)
            {
                return;
            }
            int2 ix = d.EmgRuleIdx[EmgMatterConst.Count + 1 + z.Matter];
            int rule = -1;
            for (int k = ix.x; k < ix.x + ix.y; k++)
            {
                int ri = d.EmgRuleOrder[k];
                if (d.EmgRules[ri].Op == EmgRuleConst.OpMeltPit && d.EmgRules[ri].Cond == cond)
                {
                    rule = ri;
                    break;
                }
            }
            if (rule < 0)
            {
                return;
            }
            if (!EmgMeltableAt(ref d, z.Pos))
            {
                d.PitStats[EmgPitConst.StatOffGround]++;
                return;
            }
            EmgRule r = d.EmgRules[rule];
            float gather = PitGatherOf(ref d);
            for (int p = 0; p < d.Pits.Length; p++)
            {
                if (math.distance(d.Pits[p].Pos, z.Pos) < gather)
                {
                    return; // 这一处已经是熔坑：不再累计
                }
            }
            // 同一位置：取合并半径内最近的累计点（等距取下标小的，确定性）；没有就新开一个，满了替换累计最少的
            int at = -1;
            double best = double.MaxValue;
            for (int a = 0; a < d.PitAcc.Length; a++)
            {
                float4 c = d.PitAcc[a];
                double dist = math.distance(new double2(c.x, c.y), z.Pos);
                if (dist < gather && dist < best)
                {
                    best = dist;
                    at = a;
                }
            }
            if (at < 0)
            {
                var fresh = new float4((float)z.Pos.x, (float)z.Pos.y, 0f, 0f);
                if (d.PitAcc.Length < EmgPitConst.AccCapacity)
                {
                    at = d.PitAcc.Length;
                    d.PitAcc.Add(fresh);
                }
                else
                {
                    at = 0;
                    for (int a = 1; a < d.PitAcc.Length; a++)
                    {
                        if (d.PitAcc[a].z + d.PitAcc[a].w < d.PitAcc[at].z + d.PitAcc[at].w)
                        {
                            at = a;
                        }
                    }
                    d.PitAcc[at] = fresh;
                }
            }
            float4 acc = d.PitAcc[at];
            float before = acc.z + acc.w;
            float add = z.Amount;
            // 累计点位置按份量加权（熔坑落在烧得最多的地方）
            acc.x = (float)((acc.x * before + z.Pos.x * add) / (before + add));
            acc.y = (float)((acc.y * before + z.Pos.y * add) / (before + add));
            bool molten = cond == EmgRuleConst.CondPitMolten;
            if (molten)
            {
                acc.z += add;
            }
            else
            {
                acc.w += add;
            }
            if ((molten ? acc.z : acc.w) < r.C1)
            {
                d.PitAcc[at] = acc;
                return;
            }
            d.PitAcc.RemoveAt(at); // 到阈值：这一处结算掉（形成或上限满都不再重复触发）
            var pos = new double2(acc.x, acc.y);
            if (d.Pits.Length >= PitMaxOf(ref d))
            {
                d.PitStats[EmgPitConst.StatCapped]++; // 卡片负向“熔坑上限满（不生效并提示）”：热更层看到计数涨了提示一次
                d.PitStats[EmgPitConst.StatCapX] = (int)math.round(pos.x * 100.0); // DEBT-EMG4TER01-04：被挡掉的这一处（进快照段 30，读档后通知位置一致）
                d.PitStats[EmgPitConst.StatCapY] = (int)math.round(pos.y * 100.0);
                return;
            }
            float radius = PitRadiusOf(ref d);
            int id = ++d.PitStats[EmgPitConst.StatNextId];
            d.Pits.Add(new EmgPit { Pos = pos, Radius = radius, Born = d.Scalars[0].Time, Id = id, Source = molten ? EmgPitConst.SourceMolten : EmgPitConst.SourceAcid, Chain = z.EmgChain });
            d.PitStats[EmgPitConst.StatBorn]++;
            EmgPushOutCircle(ref d, pos, radius);
            EmgRuleSettle(ref d, r, z.EmgChain, z.EmgDepth, z.Owner, pos, z.EmgId);
            EmgGroundMark(ref d, EmgGroundMarkConst.KindPit, pos, radius, id); // EMG4-TER-02：家园 / 前哨的熔坑同时写进格网（战痕）
        }

        // ── EMG4-TER-02（FG19 FGR-EMG-153，FG19B V03）：地面标记（战痕）。只在长期留存地点发玩法事件（每步 64 条、顺延不丢），写格网在热更层。──

        private static float ScorchIgniteOf(ref CombatData d) => d.Config.EmgScorchIgnite > 0f ? d.Config.EmgScorchIgnite : EmgGroundMarkConst.DefaultScorchIgnite;

        /// <summary>长期留存地点（家园 / 前哨）出现一处战痕：发 <see cref="CombatEventKind.GroundMark"/> 玩法事件并计数。远征 / 室内地点（不由家园格网承载，FG19 第 14 节 #15）不发。</summary>
        internal static void EmgGroundMark(ref CombatData d, byte kind, double2 pos, float radius, int sourceId)
        {
            if (d.Config.EmgPersist == 0)
            {
                return;
            }
            d.PitStats[EmgPitConst.StatMarks]++;
            Gameplay(ref d, CombatEventKind.GroundMark, -1, sourceId, math.max(0.5f, radius), 0f, pos, kind, 0);
        }

        /// <summary>长期留存固态刚生成（<see cref="EmgPersistCap"/> 入口）：玻化层 → 玻化地面、冰 → 冻土战痕。其余物质一次比较返回。</summary>
        internal static void EmgSolidMark(ref CombatData d, int born)
        {
            CombatZone z = d.Patches[born];
            byte kind = z.Matter == EmgMatterConst.Glass ? EmgGroundMarkConst.KindGlazed : z.Matter == EmgMatterConst.Ice ? EmgGroundMarkConst.KindFrozen : (byte)0;
            if (kind != 0)
            {
                EmgGroundMark(ref d, kind, z.Pos, z.R0 > 0f ? z.R0 : z.Radius, z.EmgId);
            }
        }

        /// <summary>
        /// V03：明火斑块 <paramref name="z"/> 到期（evt.expire）时，在长期留存地点把这块火烧过的秒数记进“同一位置”累计点（合并半径同熔坑 emg.terrain.pit_gather）；
        /// 累计到规则阈值 c1（20 秒）→ 结算这一处并发焦土地面标记。不是家园、不是明火、没有这条规则行时一次判断就返回。
        /// </summary>
        internal static void EmgScorchFeed(ref CombatData d, in CombatZone z)
        {
            if (d.Config.EmgPersist == 0 || z.Matter != EmgMatterConst.Fire || !d.EmgRules.IsCreated)
            {
                return;
            }
            int2 ix = d.EmgRuleIdx[EmgMatterConst.Count + 1 + z.Matter];
            int rule = -1;
            for (int k = ix.x; k < ix.x + ix.y; k++)
            {
                int ri = d.EmgRuleOrder[k];
                if (d.EmgRules[ri].Op == EmgRuleConst.OpScorch && d.EmgRules[ri].Cond == EmgRuleConst.CondHomeBurn)
                {
                    rule = ri;
                    break;
                }
            }
            if (rule < 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            float burned = (float)math.max(0.0, math.min(now, z.Until) - z.Born);
            if (!(burned > 0f))
            {
                return;
            }
            float gather = PitGatherOf(ref d);
            int at = -1;
            double best = double.MaxValue;
            for (int a = 0; a < d.ScorchAcc.Length; a++)
            {
                float4 c = d.ScorchAcc[a];
                double dist = math.distance(new double2(c.x, c.y), z.Pos);
                if (dist < gather && dist < best)
                {
                    best = dist;
                    at = a;
                }
            }
            if (at < 0)
            {
                var fresh = new float4((float)z.Pos.x, (float)z.Pos.y, 0f, 0f);
                if (d.ScorchAcc.Length < EmgPitConst.AccCapacity)
                {
                    at = d.ScorchAcc.Length;
                    d.ScorchAcc.Add(fresh);
                }
                else
                {
                    at = 0;
                    for (int a = 1; a < d.ScorchAcc.Length; a++)
                    {
                        if (d.ScorchAcc[a].z < d.ScorchAcc[at].z)
                        {
                            at = a;
                        }
                    }
                    d.ScorchAcc[at] = fresh;
                }
            }
            float4 acc = d.ScorchAcc[at];
            acc.x = (acc.x * acc.z + (float)z.Pos.x * burned) / (acc.z + burned); // 按燃烧秒加权：焦土落在烧得最久的地方
            acc.y = (acc.y * acc.z + (float)z.Pos.y * burned) / (acc.z + burned);
            acc.z += burned;
            acc.w = math.max(acc.w, math.max(z.R0, z.Radius));
            EmgRule r = d.EmgRules[rule];
            if (acc.z < r.C1)
            {
                d.ScorchAcc[at] = acc;
                return;
            }
            d.ScorchAcc.RemoveAt(at); // 到阈值：这一处结算掉，之后同一处再烧 20 秒再记一次（热更层按“同一格反复标记”合并）
            var pos = new double2(acc.x, acc.y);
            d.PitStats[EmgPitConst.StatScorchBorn]++;
            EmgRuleSettle(ref d, r, z.EmgChain, z.EmgDepth, z.Owner, pos, z.EmgId);
            EmgGroundMark(ref d, EmgGroundMarkConst.KindScorch, pos, math.max(1f, acc.w), z.EmgId);
        }

        /// <summary>点 <paramref name="p"/> 在不在焦土圈里（V03“焦土上的可燃斑块点燃阈值 −40 度”）。没有焦土圈时一次长度比较。</summary>
        internal static bool EmgScorchedAt(ref CombatData d, double2 p)
        {
            for (int k = 0; k < d.ScorchGround.Length; k++)
            {
                float3 c = d.ScorchGround[k];
                if (math.distancesq(p, new double2(c.x, c.y)) <= (double)c.z * c.z)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>点 <paramref name="p"/>（单位半径 <paramref name="me"/>）落在哪个熔坑里（−1 = 没有）。O(熔坑数 ≤ 8)。</summary>
        private static int EmgPitAt(ref CombatData d, double2 p, float me)
        {
            for (int k = 0; k < d.Pits.Length; k++)
            {
                if (math.distance(p, d.Pits[k].Pos) < d.Pits[k].Radius + me)
                {
                    return k;
                }
            }
            return -1;
        }

        /// <summary>移动一步不得进入熔坑（与阻路斑块同一口径：目标点落进就沿圆边滑到外面；本来就在坑里的往外走不拦）。调用方已排除离地单位。</summary>
        private static double2 EmgPitClamp(ref CombatData d, int i, double2 from, double2 to)
        {
            float me = i >= 0 && i < d.Count ? d.Radius[i] : 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                int hit = EmgPitAt(ref d, to, me);
                if (hit < 0)
                {
                    return to;
                }
                EmgPit pit = d.Pits[hit];
                double reach = pit.Radius + me;
                if (math.distance(from, pit.Pos) < reach - 1e-3)
                {
                    return to;
                }
                double2 away = to - pit.Pos;
                double len = math.length(away);
                double2 dir = len > 1e-6 ? away / len : math.normalizesafe(from - pit.Pos, new double2(1, 0));
                to = pit.Pos + dir * (reach + 0.01);
            }
            return EmgPitAt(ref d, to, me) >= 0 ? from : to;
        }

        /// <summary>前方探点碰到熔坑（局部避让，与阻路斑块同一套“绕”）。</summary>
        private static bool EmgPitAhead(ref CombatData d, double2 pos, float2 dir, float look, float me, out double2 center)
        {
            center = default;
            for (int k = 1; k <= 3; k++)
            {
                int j = EmgPitAt(ref d, pos + (double2)(dir * (look * k / 3f)), me);
                if (j >= 0)
                {
                    center = d.Pits[j].Pos;
                    return true;
                }
            }
            return false;
        }

        /// <summary>熔坑集合签名（Id 序列；空 = 0）。与阻路斑块签名合起来决定是否把覆盖格重推进寻路镜像。</summary>
        internal static uint EmgPitSig(ref CombatData d)
        {
            uint h = 0u;
            for (int k = 0; k < d.Pits.Length; k++)
            {
                h = (h ^ (uint)d.Pits[k].Id ^ 0x2c1b3c6du) * 16777619u + 1u;
            }
            return h;
        }

        /// <summary>工作单填平一个熔坑（调用方已确认还在）。</summary>
        internal static bool EmgPitFill(ref CombatData d, int id)
        {
            for (int k = 0; k < d.Pits.Length; k++)
            {
                if (d.Pits[k].Id == id)
                {
                    d.Pits.RemoveAt(k);
                    d.PitStats[EmgPitConst.StatFilled]++;
                    return true;
                }
            }
            return false;
        }
    }
}
