using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-PERSIST-01（FG19 FGR-EMG-150、154；ADR-EMG-001 ⑦ ⑩ T1）：长期留存与固态掩体。
    /// - 长期留存（<see cref="CombatConfig.EmgPersist"/> = 1，只有家园内核）：残骸与固态斑块不按寿命到期，直到被摧毁、融化、回收或被玩家工作单清除；
    ///   固态斑块 ≤ emg.persist.patch_max、各物质 ≤ 软上限（FG19B 第 8 节），超了最早的崩解为碎屑斑块（可见、计数）。
    /// - fieldrule_solid_absorb（FG19B 第 5 节，DEBT-EMG2FLD01-01）：固化胶块挡所有穿过的弹体；冷渣只挡射向其中、或其后 1.5 米内单位的弹体（低矮掩体）；吸收扣斑块耐久 = 伤害。
    /// - fieldrule_glass_payload / ice_payload 的抛射落地（FGT-EMG-036 第 2 条）：落在玻化层 / 冰面再弹一段 = 原射程 × 0.4 / 0.25。
    /// - 残骸点燃（FGR-EMG-113 作用 3）：可燃材质的残骸碰到燃点以上的地面斑块就着，脚下起明火，烧到耐久归零或被浇灭（<see cref="EmgWreckBurnTick"/>）。
    /// 只认物质码、规则行与参数，不认任何内容 ID（FGR-EMG-013）。查询走 EmgHash，逐单位扫描只在弹体真撞上冷渣的那一步（Main/Sim）。
    /// </summary>
    public static partial class CombatLogic
    {
        private const int DefaultPersistPatchMax = 128;

        /// <summary>这块斑块在本地点是否长期留存（固态 + 长期留存地点）。无状态判断：不进快照、不用标志位，读档后口径不变。</summary>
        internal static bool PatchKept(ref CombatData d, in CombatZone z) =>
            d.Config.EmgPersist != 0 && z.Matter != 0 && MatterOf(ref d, z.Matter).Solid != 0;

        private static int PersistPatchMaxOf(ref CombatData d) => d.Config.EmgPersistPatchMax > 0 ? d.Config.EmgPersistPatchMax : DefaultPersistPatchMax;

        /// <summary>第 <paramref name="born"/> 块长期留存固态刚生成：超物质软上限或总额时，最早的（不含它自己）崩解为碎屑。O(斑块数)，只在固态斑块生成那一刻。</summary>
        internal static void EmgPersistCap(ref CombatData d, int born)
        {
            byte matter = d.Patches[born].Matter;
            int quota = MatterOf(ref d, matter).Quota, max = PersistPatchMaxOf(ref d);
            for (int guard = 0; guard < 2; guard++)
            {
                int same = 0, total = 0, oldestSame = -1, oldestAny = -1;
                for (int i = 0; i < d.Patches.Length; i++)
                {
                    CombatZone z = d.Patches[i];
                    if (!PatchKept(ref d, z) || PatchWorld(z))
                    {
                        continue;
                    }
                    total++;
                    same += z.Matter == matter ? 1 : 0;
                    if (i == born)
                    {
                        continue;
                    }
                    if (z.Matter == matter && (oldestSame < 0 || Older(z, d.Patches[oldestSame])))
                    {
                        oldestSame = i;
                    }
                    if (oldestAny < 0 || Older(z, d.Patches[oldestAny]))
                    {
                        oldestAny = i;
                    }
                }
                int victim = quota > 0 && same > quota ? oldestSame : total > max ? oldestAny : -1;
                if (victim < 0)
                {
                    return;
                }
                EmgPersistCollapse(ref d, victim);
            }
        }

        /// <summary>EMG2-BOSS-01：撤掉全部首领灌注斑块（FlagReserved）。</summary>
        internal static int EmgClearReserved(ref CombatData d)
        {
            int n = 0;
            for (int i = 0; i < d.Patches.Length; i++)
            {
                if (PatchAlive(d.Patches[i]) && (d.Patches[i].PFlags & EmgMatterConst.FlagReserved) != 0)
                {
                    KillPatch(ref d, i);
                    n++;
                }
            }
            return n;
        }

        /// <summary>长期留存固态崩解为金属碎屑斑块（转化，不消失；FGR-EMG-150）。斑块层放不下时碎屑顺延 / 计丢弃，不静默。</summary>
        private static void EmgPersistCollapse(ref CombatData d, int i)
        {
            CombatZone z = d.Patches[i];
            KillPatch(ref d, i);
            EmgScalars es = d.EmgS[0];
            es.PersistCapped++;
            d.EmgS[0] = es;
            EmgRequestPatch(ref d, EmgMatterConst.Debris, z.Pos, math.max(z.Radius, 0.5f), 1f, 0f, 0, z.EmgChain, 0, 0, EmgMatterConst.PhaseFromWreck);
        }

        /// <summary>工作单清除一件长期留存物（调用方已确认目标还在）。残骸：整表前移保持“最早在前”；斑块：记墓碑。</summary>
        internal static void EmgPersistClear(ref CombatData d, byte kind, int id)
        {
            if (kind == EmgPersistItem.KindWreck)
            {
                int write = 0;
                for (int i = 0; i < d.Wrecks.Length; i++)
                {
                    if (d.Wrecks[i].Id != id)
                    {
                        d.Wrecks[write++] = d.Wrecks[i];
                    }
                }
                d.Wrecks.ResizeUninitialized(write);
            }
            else
            {
                for (int i = 0; i < d.Patches.Length; i++)
                {
                    if (d.Patches[i].EmgId == id && PatchAlive(d.Patches[i]))
                    {
                        KillPatch(ref d, i);
                        break;
                    }
                }
            }
            EmgScalars es = d.EmgS[0];
            es.PersistCleared++;
            es.HashDirty = 1;
            d.EmgS[0] = es;
        }

        // ─────────────────────────────── 固态挡弹 ───────────────────────────────

        private static bool IsAbsorber(in CombatZone z) => z.Matter == EmgMatterConst.Cured || z.Matter == EmgMatterConst.Slag;

        /// <summary>即时命中线段 a→b（b = 目标）上最先碰到的挡弹固态斑块（−1 = 没有）。出手者站在斑块里的不算；冷渣只在目标在其中或其后 Param 米内时才挡。</summary>
        internal static int EmgSolidOnShot(ref CombatData d, double2 a, double2 b, out double tHit)
        {
            tHit = 2.0;
            EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.SolidAbsorb);
            if (!FieldLive(rule) || !EmgPatchesOn(ref d))
            {
                return -1;
            }
            EmgHashEnsure(ref d);
            if (d.EmgS[0].AbsorbLive <= 0)
            {
                return -1;
            }
            int x0 = EmgHash.CellOf(math.min(a.x, b.x)), x1 = EmgHash.CellOf(math.max(a.x, b.x));
            int y0 = EmgHash.CellOf(math.min(a.y, b.y)), y1 = EmgHash.CellOf(math.max(a.y, b.y));
            int best = -1;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
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
                        if (j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone z = d.Patches[j];
                        if (!IsAbsorber(z) || !(z.Hp > 0f) || math.distance(a, z.Pos) <= z.Radius)
                        {
                            continue;
                        }
                        if (z.Matter == EmgMatterConst.Slag && math.distance(b, z.Pos) > z.Radius + rule.Param)
                        {
                            continue;
                        }
                        double t = SegmentCircleT(a, b, z.Pos, z.Radius);
                        if (t >= 0 && (t < tHit || (t == tHit && z.EmgId < d.Patches[best].EmgId)))
                        {
                            tHit = t;
                            best = j;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        /// <summary>飞行弹体本步线段上、先于单位交点（<paramref name="unitT"/>）的挡弹固态斑块。冷渣要求敌对地面单位在其中或其后 Param 米内（弹体没有“目标”，按真实站位判，O(单位数)，只在弹体穿冷渣那一步）。</summary>
        internal static int EmgSolidOnProjectile(ref CombatData d, in CombatProjectile pr, double2 next, double unitT, out double tHit)
        {
            tHit = 2.0;
            EmgFieldRule rule = FieldRule(ref d, EmgFieldConst.SolidAbsorb);
            if (!FieldLive(rule) || !EmgPatchesOn(ref d) || (pr.Flags & EmgProjFlags.Lob) != 0)
            {
                return -1;
            }
            EmgHashEnsure(ref d);
            if (d.EmgS[0].AbsorbLive <= 0)
            {
                return -1;
            }
            double2 a = pr.Prev;
            int x0 = EmgHash.CellOf(math.min(a.x, next.x)), x1 = EmgHash.CellOf(math.max(a.x, next.x));
            int y0 = EmgHash.CellOf(math.min(a.y, next.y)), y1 = EmgHash.CellOf(math.max(a.y, next.y));
            int best = -1;
            double2 dir = next - a;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
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
                        if (j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone z = d.Patches[j];
                        if (!IsAbsorber(z) || !(z.Hp > 0f) || math.distance(a, z.Pos) <= z.Radius)
                        {
                            continue; // 从斑块里飞出去的不算（破片从碎掉的胶块里出生）
                        }
                        double t = SegmentCircleT(a, next, z.Pos, z.Radius);
                        if (t < 0 || t > unitT || (best >= 0 && t >= tHit))
                        {
                            continue;
                        }
                        if (z.Matter == EmgMatterConst.Slag && !SlagCovers(ref d, z, dir, pr.Faction, rule.Param))
                        {
                            continue;
                        }
                        tHit = t;
                        best = j;
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        /// <summary>冷渣掩护：弹体的敌对地面单位站在冷渣里，或站在冷渣后方 <paramref name="behind"/> 米内。</summary>
        private static bool SlagCovers(ref CombatData d, in CombatZone z, double2 dir, CombatFaction shooter, float behind)
        {
            double reach = z.Radius + behind;
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || d.Faction[u] == (byte)shooter || IsAir(ref d, u))
                {
                    continue;
                }
                double2 rel = d.Pos[u] - z.Pos;
                if (math.lengthsq(rel) <= reach * reach && (math.lengthsq(rel) <= (double)z.Radius * z.Radius || math.dot(rel, dir) > 0))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 固态斑块吸收一发：扣耐久 = 伤害。胶块归零时下一节拍碎掉，无产物（FG19B §1.4 第 7 条，TickPatch 统一处理）；
        /// 冷渣吸收同时累计动能，打碎走 E20 出碎屑（与抛射落地同一条规则）：耐久先到 0 时把动能补满、留一丝耐久等下一节拍的规则求值。
        /// </summary>
        internal static void EmgSolidAbsorb(ref CombatData d, int j, float damage)
        {
            CombatZone z = d.Patches[j];
            z.Hp = math.max(0f, z.Hp - math.max(0f, damage));
            if (z.Matter == EmgMatterConst.Slag && EmgRulesOn(ref d))
            {
                z.Kinetic += math.max(0f, damage);
                if (!(z.Hp > 0f))
                {
                    z.Kinetic = math.max(z.Kinetic, MatterOf(ref d, z.Matter).Hp);
                    z.Hp = 0.01f;
                }
            }
            d.Patches[j] = z;
            EmgScalars es = d.EmgS[0];
            es.SolidAbsorbs++;
            d.EmgS[0] = es;
        }

        /// <summary>飞行弹体撞上挡弹固态斑块：有障碍面反弹次数就按斑块圆法线反弹（返回 true，继续飞），否则被吸收（evt.absorb + evt.expire Code = 被吸收）。与残骸同一口径。</summary>
        internal static bool EmgSolidHit(ref CombatData d, ref CombatProjectile pr, int j, double2 next, double t)
        {
            CombatZone z = d.Patches[j];
            double2 at = pr.Prev + (next - pr.Prev) * t;
            float dmg = math.max(0f, pr.Damage);
            EmgSolidAbsorb(ref d, j, dmg);
            if (EmgBounceFace(ref d, ref pr, at, z.Pos, EmgCode.FaceMatter))
            {
                return true;
            }
            if ((pr.Flags & EmgProjFlags.Sub) != 0)
            {
                EmgNodeEffects(ref d, pr.Prog, pr.Node, at, d.SlotOf(pr.Owner), pr.Faction, pr.Chain, pr.Depth); // 子弹药在撞点执行自己节点的指令（与撞残骸同一口径）
            }
            EmgProjEvent(ref d, ref pr, EmgEventKind.Absorb, z.EmgId, EmgEntKind.Patch, dmg, 0f, 0, at);
            EmgProjEvent(ref d, ref pr, EmgEventKind.Expire, z.EmgId, EmgEntKind.Patch, 0f, 0f, EmgCode.ExpireAbsorbed, at);
            CombatCounters c = d.Counters[0];
            c.ProjectilesExpired++;
            d.Counters[0] = c;
            return false;
        }

        // ─────────────────────────────── 残骸点燃 ───────────────────────────────

        private static float WreckIgniteOf(ref CombatData d) => d.Config.EmgWreckIgniteTemp > 0f ? d.Config.EmgWreckIgniteTemp : EmgConst.DefaultWreckIgniteTemp;

        private static float WreckFlamOf(ref CombatData d, in EmgWreck k) => EmgProfileAt(ref d, k.Profile >= 0 ? k.Profile : EmgConst.SlotWreck).Flammable;

        private static bool WreckCanBurn(ref CombatData d, float flam) => flam >= (d.Config.EmgWreckFlammableMin > 0f ? d.Config.EmgWreckFlammableMin : EmgConst.DefaultWreckFlammableMin);

        internal static bool EmgWreckBurning(ref CombatData d, in EmgWreck k) =>
            k.Hp > 0f && k.Temp >= WreckIgniteOf(ref d) && WreckCanBurn(ref d, WreckFlamOf(ref d, k));

        /// <summary>
        /// 传导节拍逐残骸（O(残骸数)，钢 / 合金残骸不查哈希）。可燃材质的残骸圆心在燃点以上的地面斑块里（明火、熔融金属、热冷渣）就升到燃点；燃点以上 = 在烧（状态只有 Temp，进快照，也认导线传来的热）：
        /// 每秒掉 MaxHp × burn_rate × 可燃；脚下没有明火就起一块（半径 2 倍，盖到贴着的残骸和油，相邻的照常被引燃；计 WreckFires）。
        /// 圆心在冷却类斑块（水、冷却液……）里 = 浇灭，回到常温。烧到耐久归零由 StepWrecks 移除（被摧毁，无产物）。
        /// </summary>
        internal static void EmgWreckBurnTick(ref CombatData d)
        {
            if (d.Wrecks.Length == 0)
            {
                return;
            }
            bool patches = d.EmgS[0].PatchLive > 0;
            if (patches)
            {
                EmgHashEnsure(ref d);
            }
            float ignite = WreckIgniteOf(ref d), amb = AmbientOf(ref d), tick = ConductTickOf(ref d);
            float rate = d.Config.EmgWreckBurnRate > 0f ? d.Config.EmgWreckBurnRate : EmgConst.DefaultWreckBurnRate;
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck k = d.Wrecks[w];
                float flam = WreckFlamOf(ref d, k);
                if (!(k.Hp > 0f) || !WreckCanBurn(ref d, flam))
                {
                    continue;
                }
                bool hot = false, fire = false, wet = false;
                if (patches)
                {
                    EmgWreckContact(ref d, k.Pos, ignite, out hot, out fire, out wet);
                }
                if (wet)
                {
                    if (k.Temp > amb)
                    {
                        k.Temp = amb;
                        d.Wrecks[w] = k;
                    }
                    continue;
                }
                if (hot && k.Temp < ignite)
                {
                    k.Temp = ignite;
                    d.Wrecks[w] = k;
                }
                if (k.Temp < ignite)
                {
                    continue;
                }
                k.Hp = math.max(0f, k.Hp - k.MaxHp * rate * flam * tick);
                d.Wrecks[w] = k;
                if (!fire)
                {
                    EmgRuleSpawn(ref d, EmgMatterConst.Fire, k.Pos, k.Radius * 2f, math.max(1f, flam * k.Mass), EmgRuleConst.KeepTemp - 1f, 0f, 0, k.Chain, 0);
                    EmgScalars es = d.EmgS[0];
                    es.WreckFires++;
                    d.EmgS[0] = es;
                }
            }
        }

        private static void EmgWreckContact(ref CombatData d, double2 p, float ignite, out bool hot, out bool fire, out bool wet)
        {
            hot = fire = wet = false;
            if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(p.x), EmgHash.CellOf(p.y)), out int e, out NativeParallelMultiHashMapIterator<long> it))
            {
                return;
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
                if (!PatchAlive(z) || z.Layer != EmgMatterConst.LayerGround || math.distancesq(p, z.Pos) > (double)z.Radius * z.Radius)
                {
                    continue;
                }
                hot |= z.Temp >= ignite;
                fire |= z.Matter == EmgMatterConst.Fire;
                wet |= MatterOf(ref d, z.Matter).Cooling > 0f;
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
        }

        // ─────────────────────────────── 抛射落地再弹 ───────────────────────────────

        /// <summary>
        /// 抛射弹落在玻化层 / 冰面（FG19B 第 5 节）：每种每发 1 次（FieldOnce），再飞一段 = 已飞秒数 × 规则幅度（速度不变 = 原射程 × 0.4 / 0.25）。返回 true = 弹体继续飞。
        /// 落地动能照常落到斑块上（玻化层很薄，可能被砸碎）；落点结算（子弹药、evt.expire）留到最后一段。
        /// </summary>
        internal static bool EmgLobRebound(ref CombatData d, ref CombatProjectile pr, double2 at, float dt)
        {
            EmgHashEnsure(ref d);
            if (!EmgPatchesOn(ref d)
                || !d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(at.x), EmgHash.CellOf(at.y)), out int e, out NativeParallelMultiHashMapIterator<long> it))
            {
                return false;
            }
            int best = -1;
            do
            {
                if ((byte)d.EmgHash.Entries[e].w != EmgHash.KindPatch)
                {
                    continue;
                }
                int j = d.EmgHash.Refs[e];
                if (j >= d.Patches.Length)
                {
                    continue;
                }
                CombatZone z = d.Patches[j];
                if ((z.Matter != EmgMatterConst.Glass && z.Matter != EmgMatterConst.Ice) || math.distance(z.Pos, at) > z.Radius)
                {
                    continue;
                }
                byte bit = z.Matter == EmgMatterConst.Glass ? EmgFieldConst.OnceGlass : EmgFieldConst.OnceIce;
                if ((pr.FieldOnce & bit) == 0 && (best < 0 || z.EmgId < d.Patches[best].EmgId))
                {
                    best = j;
                }
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
            if (best < 0)
            {
                return false;
            }
            CombatZone hit = d.Patches[best];
            bool glass = hit.Matter == EmgMatterConst.Glass;
            EmgFieldRule rule = FieldRule(ref d, glass ? EmgFieldConst.GlassPayload : EmgFieldConst.IcePayload);
            float life = (pr.Age + dt) * rule.Amount;
            if (!FieldLive(rule) || !(life > 0f))
            {
                return false;
            }
            pr.FieldOnce |= glass ? EmgFieldConst.OnceGlass : EmgFieldConst.OnceIce;
            pr.Life = life;
            if (pr.Damage > 0f)
            {
                EmgKineticAt(ref d, at, pr.Damage * EmgProjKinetic(ref d, pr));
            }
            double2 rel = at - hit.Pos;
            float2 nrm = math.lengthsq(rel) > 1e-12 ? (float2)math.normalize(rel) : math.normalizesafe(-pr.Vel);
            EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, hit.EmgId, EmgEntKind.Patch, nrm.x, nrm.y, EmgCode.FaceMatter, at);
            EmgScalars es = d.EmgS[0];
            es.ProjPatchBounce++;
            d.EmgS[0] = es;
            return true;
        }
    }
}
