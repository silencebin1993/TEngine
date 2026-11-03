using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-MAT-02 现有区域迁入斑块（等价迁移）的自动验收（FG19 FGR-EMG-087 / 211；FG19B §1.4 第 11 条、§1.5、§7；ADR-EMG-001 ⑤ ⑩ ⑭ #1 #2 #13；
    /// FGT-EMG-035 迁移等价、FGT-EMG-002 澄净排放行准入、FGT-EMG-041 旧快照 14 / 15 / 16 读入部分）。
    /// 起两套真实内核（CombatKernel，Burst 步进）：斑块层关（= 迁移前的区域路径，基线）与斑块层开（= 迁入斑块），同一剧本逐步跑、比结果：
    /// A 数据（敌方排放声明行 + 准入、structDps、液洼物质列、澄净材质抗性）；B 液洼遇火整片烧（等价 + 物质码 + 燃烧格上报）；C 反应残留燃烧区；
    /// D 喷淋池（酸池打己方机器 / 炮塔 / 建筑等价、冷却液冲淡酸池、明火蒸干液氮池、液氮受寒）；E 油迹整片点燃；F 液洼负向（同一液洼不重复、修好后移除、读档后续更新）；
    /// G 旧快照 14 / 15 / 16（可映射转斑块、不可映射保留到期、续跑与基线一致）与 17 往返；H 酸沼虚拟世界源（不占槽、斑块满时照常、进离场）；
    /// I 设施受击聚合（酸斑块按 structDps 掉耐久、每步 ≤ 64 条顺延不丢、同键合并、明火标燃烧）；J 0.5x～3x 与暂停。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgMigrationSelfCheck）；单段：bash tools/unity-validate.sh FgEmgMigrationSelfCheck。
    /// </summary>
    public static class FgEmgMigrationSelfCheck
    {
        private const float Dt = 1f / 60f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/现有区域迁入斑块")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[区域迁入斑块] EMG2-MAT-02：等价迁移（FGT-EMG-035）/ 澄净排放准入（FGT-EMG-002）/ 旧快照读入（FGT-EMG-041）/ 酸沼虚拟源 / 设施受击聚合");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                Step(CheckData);
                Step(CheckLeak);
                Step(CheckResidue);
                Step(CheckPools);
                Step(CheckSlick);
                Step(CheckLeakNegatives);
                Step(CheckOldSnapshots);
                Step(CheckAcidMarsh);
                Step(CheckStructHits);
                Step(CheckDwell);
                Step(CheckContactSlots);
                Step(CheckSpeed);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"区域迁入斑块自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [区域迁入斑块] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        /// <summary>与 CombatSite 建地点同一套写表（子形态 / 实体档案 / 物质表 / 反应 / 状态效果 / 澄净四位 + 冻结位）。<paramref name="patches"/> = false 是迁移前的区域路径（基线）。</summary>
        private static CombatKernel NewKernel(bool patches, int capacity = 0, int reserve = -1)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgPatchOn = (byte)(patches ? 1 : 0);
            if (capacity > 0)
            {
                c.EmgPatchCapacity = capacity;
            }
            if (reserve >= 0)
            {
                c.EmgPatchReserve = reserve;
            }
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyReactionTables(k); // EMG2-REACT-01：含整片反应残留参数（爆燃撤出旧表后从规则行取）
            return k;
        }

        private static int Dummy(CombatKernel k, double2 at, CombatFaction f = CombatFaction.Hostile, CombatUnitKind kind = CombatUnitKind.Enemy, float hp = 1000f) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = kind,
            Faction = f,
            Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Health = hp,
            MaxHealth = hp,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        });

        private static void Run(CombatKernel k, float seconds, int perFrame = 1)
        {
            int n = Mathf.RoundToInt(seconds * 60f);
            for (int i = 0; i < n; i += perFrame)
            {
                for (int j = 0; j < perFrame && i + j < n; j++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
            }
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static uint Status(CombatKernel k, int id) => k.TryGetStatus(id, out uint m, out double until, out _, out _, out _) && until > k.Time ? m : 0u;

        /// <summary>结果签名：各单位血量（0.01 精度）+ 反应 / 爆燃 / 冲洗 / 油迹点燃 / 酸沼计数。</summary>
        private static string Sig(CombatKernel k, params int[] ids)
        {
            var sb = new StringBuilder();
            foreach (int id in ids)
            {
                sb.Append(Mathf.RoundToInt(Hp(k, id) * 100f)).Append(',');
            }
            CombatCounters c = k.Counters;
            return sb.Append("r").Append(c.ReactionsFired).Append(" d").Append(c.Deflagrations).Append(" w").Append(c.PoolsWashed).Append(" s").Append(c.SlicksIgnited)
                .Append(" a").Append(c.AcidTicks).ToString();
        }

        private static int FirstLegacy(CombatKernel k, byte kind)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && (z.PFlags & EmgMatterConst.FlagLegacy) != 0 && z.Kind == kind)
                {
                    return i;
                }
            }
            return -1;
        }

        // ── A 数据 ───────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：澄净排放声明行与准入（FGT-EMG-002）、structDps、液洼物质列、澄净材质抗性");
            EmgSource src = EmgCatalog.FromTables();
            Dictionary<string, EmgSource.Port> ports = src.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var want = new Dictionary<string, string> { ["enemy_sprayer"] = "mat.acid", ["enemy_ln2_pump"] = "mat.cryo", ["enemy_settling_tank"] = "mat.acid", ["enemy_oil_crawler"] = "mat.oil" };
            var bad = new List<string>();
            foreach (KeyValuePair<string, string> kv in want)
            {
                var rows = src.Decls.Where(d => d.Owner == kv.Key).ToList();
                bool emits = rows.Any(d => d.OwnerKind == "enemy" && d.Port == kv.Value && d.Dir == "write");
                bool admitted = EmgCatalog.Admit(EmgCatalog.DeclsOf(src, kv.Key), ports, out string why);
                if (!emits || !admitted)
                {
                    bad.Add($"{kv.Key}（排放 {emits} / 准入 {admitted}：{why}）");
                }
            }
            // 负向：只读恒真事件、只写物质的敌方行不准入（同一规则，防假阳性）
            bool negative = !EmgCatalog.Admit(new List<(string, EmgDirs, EmgRole)> { ("evt.fire", EmgDirs.Read, EmgRole.Trigger), ("mat.acid", EmgDirs.Write, EmgRole.Emit) }, ports, out _);
            Expect(bad.Count == 0 && negative,
                $"A1 澄净迁入斑块的排放（喷淋池 / 液氮池 / 沉淀巨罐酸池 / 油迹）各有 ownerKind = enemy 的声明行并过准入；只读恒真事件的同形行被拒（{string.Join("；", bad)}）");

            EmgMatterCatalog.Compiled mc = EmgMatterCatalog.Current;
            Expect(mc.Ok && Mathf.Approximately(mc.Rows[EmgMatterConst.Fire].StructDps, GridContent.Tuning("logistics.leak.fire_dps")) && mc.Rows[EmgMatterConst.Acid].StructDps > 0f
                   && mc.Rows[EmgMatterConst.Water].StructDps == 0f,
                $"A2 fg.TbMatter.structDps：明火 = logistics.leak.fire_dps（{mc.Rows[EmgMatterConst.Fire].StructDps}）、酸 {mc.Rows[EmgMatterConst.Acid].StructDps}、水 0");

            var fluids = ConfigSystem.Instance.Tables.TbFluidLeak.DataList.ToDictionary(r => r.Fluid, r => EmgMatterCatalog.CodeOf(r.Matter));
            Expect(fluids.Count >= 5 && fluids[1] == EmgMatterConst.Water && fluids[2] == EmgMatterConst.Oil && fluids[3] == EmgMatterConst.Coolant && fluids[4] == EmgMatterConst.Oil
                   && fluids[5] == EmgMatterConst.Acid,
                "A3 fg.TbFluidLeak.matter 按 FG19B §1.5：水 → mat.water、原油 / 燃油 → mat.oil、冷却液 → mat.coolant、酸液 → mat.acid");

            var prof = ConfigSystem.Instance.Tables.TbEntityProfile.DataList.FirstOrDefault(r => r.Id == "material.clarity");
            var tank = ConfigSystem.Instance.Tables.TbMechEnemy.DataList.Where(r => r.Faction == "clarity").ToList();
            Expect(prof != null && Mathf.Approximately(prof.ResistCorrode, 1f) && Mathf.Approximately(prof.ResistCold, 0.5f) && tank.Count > 0 && tank.All(r => r.Material == "material.clarity"),
                $"A4 澄净材质 material.clarity：腐蚀抗 {prof?.ResistCorrode}、寒抗 {prof?.ResistCold}（ADR ⑭ #1“澄净不怕自己的池”，按推荐默认执行，待用户确认）；{tank.Count} 种澄净单位都用它");
        }

        // ── B 液洼 ───────────────────────────────────────────────────────────

        private static string LeakScenario(CombatKernel k, out bool migrated, out bool fireReported, out int reactions)
        {
            uint oil = Bit("Oil"), fire = Bit("Fire");
            k.UpsertLeakZone(7, new double2(0, 0), 1.5f, oil, EmgMatterConst.Oil);
            int foe = Dummy(k, new double2(0.5, 0));
            int ally = Dummy(k, new double2(-0.5, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int wall = Dummy(k, new double2(0, 0.5), CombatFaction.Player, CombatUnitKind.Structure);
            Run(k, 1.2f);
            int burner = Dummy(k, new double2(0, -0.6));
            k.ApplyStatus(burner, fire, 20f, 4f, 0f, 0f, 0);
            Run(k, 0.6f);
            var drained = new List<int2>();
            k.DrainLeakReactions(drained);
            reactions = drained.Count;
            int at = FirstLegacy(k, CombatConst.ZoneKindLeak);
            migrated = at >= 0 && k.TryGetPatch(at, out CombatZone p) && p.Matter == EmgMatterConst.Fire && p.Phase == CombatConst.LeakPhaseReacted && (p.PFlags & EmgMatterConst.FlagWorld) == 0;
            Run(k, 3f);
            var hits = new List<EmgStructHit>();
            k.DrainStructHits(hits);
            fireReported = hits.Any(h => h.Key == -7 && (h.Flags & EmgMatterConst.HitLeak) != 0 && (h.Flags & EmgMatterConst.HitFire) != 0);
            return Sig(k, foe, ally, wall, burner);
        }

        private static void CheckLeak()
        {
            Line("  · B. 管线液洼：遇火整片烧（迁移前后一致）、物质码、燃烧格上报");
            using CombatKernel a = NewKernel(false);
            using CombatKernel b = NewKernel(true);
            string sa = LeakScenario(a, out bool ma, out bool fa, out int ra);
            string sb = LeakScenario(b, out bool mb, out bool fb, out int rb);
            Expect(sa == sb && ra == 1 && rb == 1, $"B1 液洼遇燃烧的敌人整片反应、烧敌我、结构单位不在内核结算：斑块层关 [{sa}] = 开 [{sb}]（FGT-EMG-035 管线泄漏遇火）");
            Expect(!ma && mb && a.LegacyPatchCount == 0 && b.LeakZoneCount == 1 && a.LeakZoneCount == 1,
                $"B2 斑块层开时液洼是迁入斑块（世界源 → 反应后换成明火、不再是世界源）：{mb}；层关时照旧是区域（{!ma}）");
            Expect(fa && fb, $"B3 燃烧的液洼残留按液洼号报“燃烧格”给热更层（PipeLeakService.Burn 只读这份聚合条目，ADR ⑤）：层关 {fa} / 层开 {fb}");
        }

        // ── C 反应残留 ───────────────────────────────────────────────────────

        private static string ResidueScenario(CombatKernel k, out int legacy)
        {
            uint oil = Bit("Oil"), fire = Bit("Fire");
            int target = Dummy(k, new double2(0, 0));
            int near = Dummy(k, new double2(0.8, 0));
            int ally = Dummy(k, new double2(-0.8, 0), CombatFaction.Player, CombatUnitKind.Machine);
            k.ApplyStatus(target, oil, 10f, 0f, 0f, 0f, 0);
            k.ApplyStatus(target, fire, 10f, 4f, 0f, 0f, 0, 20f);
            legacy = k.LegacyPatchCount;
            Run(k, 4f);
            return Sig(k, target, near, ally);
        }

        private static void CheckResidue()
        {
            Line("  · C. 反应残留燃烧区");
            using CombatKernel a = NewKernel(false);
            using CombatKernel b = NewKernel(true);
            string sa = ResidueScenario(a, out int la);
            string sb = ResidueScenario(b, out int lb);
            Expect(sa == sb && la == 0, $"C1 油 + 燃烧的单位反应留下的残留区：斑块层关 [{sa}] = 开 [{sb}]；层开时残留是迁入斑块（{lb} 块）");
        }

        // ── D 喷淋池 ─────────────────────────────────────────────────────────

        private static string PoolScenario(CombatKernel k, bool cryo, string washer, out int pools, out int legacy)
        {
            uint acid = Bit("Acid"), frozen = Bit("Frozen");
            int sprayer = Dummy(k, new double2(10, 10));
            int machine = Dummy(k, new double2(0.6, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int turret = Dummy(k, new double2(-0.6, 0), CombatFaction.Player, CombatUnitKind.Turret);
            int wall = Dummy(k, new double2(0, 0.6), CombatFaction.Player, CombatUnitKind.Structure);
            int foe = Dummy(k, new double2(0, -0.6));
            k.SpawnPoolForTests(k.TryGetUnit(sprayer, out _) ? sprayer : 0, new double2(0, 0), 2.5f, 8f, cryo ? 0f : 6f, cryo ? frozen : acid, 4f, cryo ? 0.6f : 0f);
            legacy = k.LegacyPatchCount;
            Run(k, 2f);
            if (washer != null)
            {
                k.SpawnFieldZone(CombatFaction.Player, new double2(1.5, 0), 2f, 4f, 0f, Bit(washer), 2f, 0f, 0f, 0f);
            }
            Run(k, 3f);
            pools = k.CountZonesOfKind(CombatConst.ZoneKindPool);
            return Sig(k, machine, turret, wall, foe) + (cryo ? $" frozen{(Status(k, machine) != 0u ? 1 : 0)}" : "");
        }

        private static void CheckPools()
        {
            Line("  · D. 云端喷淋池（酸池打己方机器 / 炮塔 / 建筑、冲洗、液氮池受寒）");
            using (CombatKernel a = NewKernel(false))
            using (CombatKernel b = NewKernel(true))
            {
                string sa = PoolScenario(a, false, null, out int pa, out _);
                string sb = PoolScenario(b, false, null, out int pb, out int lb);
                Expect(sa == sb && lb == 1 && pa == pb,
                    $"D1 酸池（中立，FG19B §7 #1）打己方机器、炮塔与建筑，迁移前后一致（中立分支不跳过炮塔 / 建筑）：层关 [{sa}] = 开 [{sb}]；层开时是迁入斑块（{lb}）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                // EMG2-BOSS-01：池中立——别的阵营单位踩进去同样挨；澄净靠腐蚀抗性 1 不受伤（FG19B §7 #1），液氮池只吃一半寒（寒抗 0.5，§7 #2）。
                int owner = Dummy(k, new double2(10, 10));
                int foe = Dummy(k, new double2(0, -0.6));
                int proof = Dummy(k, new double2(0, 0.6));
                k.SetImmuneMask(proof, Bit("Acid")); // 澄净单位（acid_proof）：免疫位同步成腐蚀抗性 1
                k.SpawnPoolForTests(owner, new double2(0, 0), 2.5f, 8f, 6f, Bit("Acid"), 4f, 0f);
                k.TryGetUnit(foe, out CombatUnitView f0);
                k.TryGetUnit(proof, out CombatUnitView p0);
                Run(k, 2f);
                k.TryGetUnit(foe, out CombatUnitView f1);
                k.TryGetUnit(proof, out CombatUnitView p1);
                Expect(f1.Health < f0.Health && Mathf.Approximately(p1.Health, p0.Health),
                    $"D1b 酸池中立：别的敌方单位踩进去掉血（{f0.Health:0} → {f1.Health:0}），腐蚀抗性 1 的单位不掉（{p0.Health:0} → {p1.Health:0}）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                int owner = Dummy(k, new double2(10, 10));
                int bare = Dummy(k, new double2(0, -0.6));
                int half = Dummy(k, new double2(0, 0.6));
                k.SetResist(half, CombatSite.ResistCold, 0.5f);
                k.SpawnPoolForTests(owner, new double2(0, 0), 2.5f, 8f, 0f, Bit("Frozen"), 4f, 0.2f);
                Run(k, 0.6f);
                k.TryGetUnitEnergy(bare, out float4 ab, out _);
                k.TryGetUnitEnergy(half, out float4 ah, out _);
                Expect(ab.x > 0f && Mathf.Abs(ah.x - ab.x * 0.5f) < ab.x * 0.1f,
                    $"D1c 液氮池中立：敌方单位踩进去也受寒（{ab.x:0.###}），寒抗 0.5 只吃一半（{ah.x:0.###}）");
            }
            using (CombatKernel a = NewKernel(false))
            using (CombatKernel b = NewKernel(true))
            {
                // EMG2-REACT-02（FGR-EMG-087）：冲洗走规则 M06 / M07，专属分支已删；层关（emg.stage < 2 的区域路径）不再被冲洗（预期差异，证据第七节）
                CombatSite.ApplyRules(b);
                PoolScenario(a, false, "Wet", out int pa, out _);
                string sb = PoolScenario(b, false, "Wet", out int pb, out _);
                Expect(pa == 1 && pb == 0 && b.Counters.PoolsWashed == 1,
                    $"D2 冷却液（湿润）盖到酸池上 → 按规则 M07 冲淡、很快消失（池 {pb}，冲洗计数 {b.Counters.PoolsWashed}：[{sb}]）；对照：层关的区域路径不再有专属冲洗（池 {pa}）");
            }
            using (CombatKernel a = NewKernel(false))
            using (CombatKernel b = NewKernel(true))
            {
                string sa = PoolScenario(a, true, null, out _, out _);
                string sb = PoolScenario(b, true, null, out _, out int lb);
                Expect(sa == sb && lb == 1, $"D3 液氮池（冻结位 → mat.cryo）每节拍受寒、冻住站在里面的机器，迁移前后一致：[{sa}] = [{sb}]");
            }
            using (CombatKernel a = NewKernel(false))
            using (CombatKernel b = NewKernel(true))
            {
                CombatSite.ApplyRules(b);
                PoolScenario(a, true, "Fire", out int pa, out _);
                string sb = PoolScenario(b, true, "Fire", out int pb, out _);
                Expect(pa == 1 && pb == 0 && b.Counters.PoolsWashed == 1,
                    $"D4 明火（燃烧）盖到液氮池上 → 按规则 M18 蒸干成冷雾（池 {pb}，计数 {b.Counters.PoolsWashed}：[{sb}]）；对照：层关不再有专属蒸干（池 {pa}）");
            }
            // 冲洗者是物质斑块（阶段 2 排放）：水斑块冲淡迁入的酸池（沾染位 = 湿润）
            using (CombatKernel b = NewKernel(true))
            {
                CombatSite.ApplyRules(b);
                int s = Dummy(b, new double2(10, 10));
                b.SpawnPoolForTests(s, new double2(0, 0), 2.5f, 8f, 6f, Bit("Acid"), 4f, 0f);
                b.SpawnPatch(EmgMatterConst.Water, new double2(1.5, 0), 2f, 1f, 6f);
                Run(b, 1.5f);
                Expect(b.Counters.PoolsWashed == 1 && b.CountZonesOfKind(CombatConst.ZoneKindPool) == 0, "D5 物质斑块（mat.water）也能冲淡迁入的酸池（物质斑块按沾染位算湿润）");
            }
        }

        // ── E 油迹 ───────────────────────────────────────────────────────────

        private static string SlickScenario(CombatKernel k, out bool bothHit)
        {
            int crawler = Dummy(k, new double2(10, 10));
            int foe = Dummy(k, new double2(0.4, 0));
            int ally = Dummy(k, new double2(-0.4, 0), CombatFaction.Player, CombatUnitKind.Machine);
            k.SpawnSlickForTests(crawler, new double2(0, 0), 1.2f, 10f, Bit("Oil"), 10f);
            Run(k, 1f);
            int burner = Dummy(k, new double2(0, 0.5));
            k.ApplyStatus(burner, Bit("Fire"), 20f, 4f, 0f, 0f, 0);
            Run(k, 4f);
            bothHit = Hp(k, foe) < 1000f && Hp(k, ally) < 1000f;
            return Sig(k, foe, ally, burner);
        }

        private static void CheckSlick()
        {
            Line("  · E. 云端油迹");
            using CombatKernel a = NewKernel(false);
            using CombatKernel b = NewKernel(true);
            // EMG2-REACT-02（FG19B §7 #4）：油迹点燃走 E01 / C01（计数改按结算），层开时伤害来自明火接触，数值不再逐位等于旧区域（预期差异，M10-BASELINE 差异表）
            CombatSite.ApplyRules(b);
            string sa = SlickScenario(a, out bool hitA);
            string sb = SlickScenario(b, out bool hitB);
            Expect(hitA && hitB && a.Counters.SlicksIgnited == 1 && b.Counters.SlicksIgnited == 1, $"E1 油迹遇燃烧的单位整片点燃（双方都挨）：层关 [{sa}]；层开走规则 [{sb}]");
        }

        // ── F 液洼负向 ───────────────────────────────────────────────────────

        private static void CheckLeakNegatives()
        {
            Line("  · F. 液洼负向：同一液洼不重复生成、修好后按原规则移除、读档后对账更新同一块");
            using CombatKernel k = NewKernel(true);
            uint wet = Bit("Wet");
            k.UpsertLeakZone(3, new double2(0, 0), 1.2f, wet, EmgMatterConst.Water);
            k.UpsertLeakZone(3, new double2(0.2, 0), 1.6f, wet, EmgMatterConst.Water);
            k.UpsertLeakZone(3, new double2(0.3, 0), 2.0f, wet, EmgMatterConst.Water);
            int at = FirstLegacy(k, CombatConst.ZoneKindLeak);
            bool one = k.LegacyPatchCount == 1 && k.LeakZoneCount == 1 && at >= 0 && k.TryGetPatch(at, out CombatZone p) && Mathf.Abs(p.Radius - 2f) < 1e-4f
                       && p.Matter == EmgMatterConst.Water && (p.PFlags & EmgMatterConst.FlagWorld) != 0;
            Expect(one, "F1 同一液洼反复对账只有一块（就地改位置 / 半径），水液洼 = mat.water 世界源斑块");
            // 冒烟 FG6-LOG-10 回归：迁入斑块不进物质斑块悬停拾取，世界悬停仍归液洼读数（ConstructionSiteView）
            Expect(k.PickPatch(new double2(0.3, 1)) < 0, "F1b 迁入液洼不被物质斑块悬停拾取（液洼悬停沿用原液洼读数）");
            byte[] snap = k.Serialize();
            using CombatKernel r = NewKernel(true);
            bool loaded = r.Load(snap) == CombatLoadResult.Ok && r.LegacyPatchCount == 1;
            r.UpsertLeakZone(3, new double2(0.3, 0), 2.4f, wet, EmgMatterConst.Water);
            bool stillOne = r.LegacyPatchCount == 1 && r.TryGetLeakZone(3, out CombatZone q) && Mathf.Abs(q.Radius - 2.4f) < 1e-4f;
            Expect(loaded && stillOne, "F2 读档后热更层对账更新的是读回来的那一块，不新增第二块");
            int removed = r.RemoveLeakZone(3);
            Run(r, 0.2f);
            Expect(removed == 1 && r.LegacyPatchCount == 0 && r.PatchCount == 0 && !r.TryGetLeakZone(3, out _), "F3 管线修好（热更层移除）后斑块随之消失");
        }

        // ── G 旧快照 ─────────────────────────────────────────────────────────

        private static void Populate(CombatKernel k, out int[] ids)
        {
            uint oil = Bit("Oil"), acid = Bit("Acid");
            int s = Dummy(k, new double2(20, 20));
            k.UpsertLeakZone(5, new double2(0, 0), 1.5f, oil, EmgMatterConst.Oil);
            k.SpawnPoolForTests(s, new double2(6, 0), 2.5f, 8f, 6f, acid, 4f, 0f);
            k.SpawnSlickForTests(s, new double2(-6, 0), 1.2f, 10f, oil, 10f);
            k.SpawnFieldZone(CombatFaction.Player, new double2(0, 6), 2f, 6f, 0f, CombatConst.StatusBitZoneSlow, 0f, 0f, 0.4f, 0f); // 减速场地：映射不了，保留区域
            int a = Dummy(k, new double2(0.4, 0));
            int b = Dummy(k, new double2(6.4, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int c = Dummy(k, new double2(-6.3, 0));
            k.ApplyStatus(c, Bit("Fire"), 20f, 4f, 0f, 0f, 0);
            int d = Dummy(k, new double2(0, 6.2));
            Run(k, 0.5f);
            ids = new[] { a, b, c, d };
        }

        private static void CheckOldSnapshots()
        {
            Line("  · G. 旧快照 14 / 15 / 16 读入（FGT-EMG-041）与 17 往返");
            foreach (int format in new[] { 16, 15, 14 })
            {
                using CombatKernel old = NewKernel(false);
                Populate(old, out int[] ids);
                byte[] snap = old.SerializeFormatForTests(format);
                using CombatKernel baseK = NewKernel(false);
                using CombatKernel migK = NewKernel(true);
                bool ok = baseK.Load(snap) == CombatLoadResult.Ok && migK.Load(snap) == CombatLoadResult.Ok;
                int expect = format >= 15 ? 3 : 1; // 14 版不认识喷淋池 / 油迹（写更老格式时略过）
                bool moved = migK.LastLegacyMigrated == expect && migK.LegacyPatchCount == expect && migK.CountZonesOfKind(CombatConst.ZoneKindField) == 1
                             && baseK.LegacyPatchCount == 0 && baseK.ZoneCount == migK.ZoneCount;
                Run(baseK, 4f);
                Run(migK, 4f);
                string sa = Sig(baseK, ids), sb = Sig(migK, ids);
                Expect(ok && moved && sa == sb,
                    $"G{format} 读 {format} 版快照：液洼 / 喷淋池 / 油迹转成迁入斑块 {migK.LastLegacyMigrated} 块（应 {expect}），减速场地映射不了保留为区域；续跑 4 秒与按区域读回的一致 [{sa}] = [{sb}]");
            }
            using (CombatKernel k = NewKernel(true))
            {
                Populate(k, out int[] ids);
                byte[] snap = k.Serialize();
                using CombatKernel r = NewKernel(true);
                bool ok = r.Load(snap) == CombatLoadResult.Ok && r.StateHash() == k.StateHash() && r.LegacyPatchCount == k.LegacyPatchCount && r.LastLegacyMigrated == 0;
                Run(k, 3f);
                Run(r, 3f);
                Expect(ok && k.StateHash() == r.StateHash() && Sig(k, ids) == Sig(r, ids), "G17 当前格式往返：迁入斑块的区域载荷（种类 / 阵营 / 伤害 / 标签 / 受寒…）逐字读回，续跑哈希一致");
                // 短段：本阶段较早（EMG2-MAT-01）写出的 29 字段斑块记录读回为普通物质斑块
                using CombatKernel m = NewKernel(true);
                m.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 1f);
                Run(m, 0.1f);
                byte[] shortSnap = m.SerializeEmgShapeForTests(EmgConst.SegPatches, 29, 0);
                using CombatKernel ms = NewKernel(true);
                bool shortOk = ms.Load(shortSnap) == CombatLoadResult.Ok && ms.PatchCount == 1 && ms.LegacyPatchCount == 0 && ms.TryGetPatch(0, out CombatZone sp)
                               && sp.Kind == EmgMatterConst.ZoneKindPatch && sp.Faction == CombatFaction.Neutral;
                Expect(shortOk, "G17s 17 版短段（29 字段，迁移前写出）读回：普通物质斑块、恒中立，缺的载荷字段取缺省");
            }
        }

        // ── H 酸沼 ───────────────────────────────────────────────────────────

        private static void CheckAcidMarsh()
        {
            Line("  · H. 酸沼 = 常驻 mat.acid 虚拟世界源（不占槽、不进哈希、斑块满时照常）");
            using CombatKernel k = NewKernel(true, capacity: 24, reserve: 1); // 战斗斑块上限 = 24 − 世界源保留 1 = 23
            for (int i = 0; i < 24; i++)
            {
                k.SpawnPatch(EmgMatterConst.Debris, new double2(100 + i * 10, 100), 1f, 1f, 600f);
            }
            Run(k, 0.2f);
            int full = k.PatchCount;
            k.SetAcidMarsh(new List<float4> { new float4(0, 0, 0, 5) }, 0.02f, 0.5f);
            int machine = Dummy(k, new double2(1, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int outside = Dummy(k, new double2(9, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int listener = Dummy(k, new double2(-1, 0));
            Run(k, 3.05f);
            float lost = 1000f - Hp(k, machine);
            bool mask = (k.MatterMaskAt(new double2(1, 0)) & (1u << EmgMatterConst.Acid)) != 0u && (k.MatterMaskAt(new double2(1, 0), airborne: true) & (1u << EmgMatterConst.Acid)) == 0u
                        && (k.MatterMaskAt(new double2(9, 0)) & (1u << EmgMatterConst.Acid)) == 0u;
            bool prev = k.TryGetUnitEmg(listener, out _, out EmgUnitState st) && (st.PrevMatterMask & (1u << EmgMatterConst.Acid)) != 0u;
            Expect(full == 23 && k.PatchCount == 23 && Mathf.Abs(lost - 60f) < 1f && Hp(k, outside) == 1000f && mask && prev,
                $"H1 斑块满（{full}/23）时酸沼照常：机器 3 秒掉 {lost:0.#}（每秒 2%，StepAcidMarsh 参数）；点在危害区内 = mat.acid（离地不算）、单位记进 PrevMatterMask；不占斑块槽（{k.PatchCount}）");
        }

        // ── I 设施受击聚合 ───────────────────────────────────────────────────

        private static void CheckStructHits()
        {
            Line("  · I. 设施受击聚合（ADR ⑤；DEBT-EMG2MAT01-02）");
            using (CombatKernel k = NewKernel(true))
            {
                k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 1f, 30f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(10, 0), 2f, 1f, 30f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(20, 0), 1f, 1f, 8f);
                var hits = new List<EmgStructHit>();
                Run(k, 2f);
                k.DrainStructHits(hits);
                float acid = hits.Where(h => h.Matter == EmgMatterConst.Acid).Sum(h => h.Damage);
                bool water = hits.All(h => h.Matter != EmgMatterConst.Water);
                bool fire = hits.Any(h => h.Matter == EmgMatterConst.Fire && (h.Flags & EmgMatterConst.HitFire) != 0);
                float acidDps = EmgMatterCatalog.Current.Rows[EmgMatterConst.Acid].StructDps;
                Expect(Mathf.Abs(acid - acidDps * 2f) <= acidDps * 0.5f && water && fire,
                    $"I1 酸斑块 2 秒按 structDps 聚合 {acid:0.##}（≈ {acidDps * 2f}）给范围里的建筑 / 传送带 / 管线；水不伤设施；明火条目标“燃烧”（烧产线）");
            }
            using (CombatKernel k = NewKernel(true))
            {
                // 酸软上限 72：50 块酸 + 50 块明火（都伤设施）
                for (int i = 0; i < 100; i++)
                {
                    k.SpawnPatch(i < 50 ? EmgMatterConst.Acid : EmgMatterConst.Fire, new double2(i * 20, 0), 1f, 1f, 60f);
                }
                Run(k, 2.5f); // 不取走：已发布表满 256 条后，其余留在待发布表（按键合并）
                var keys = new HashSet<int>();
                var hits = new List<EmgStructHit>();
                int pendingWhileFull = k.StructHitsPending;
                k.DrainStructHits(hits);
                int firstDrain = hits.Count;
                foreach (EmgStructHit h in hits)
                {
                    keys.Add(h.Key);
                }
                int maxPerStep = 0;
                for (int s = 0; s < 120; s++)
                {
                    k.Step(Dt, k.Time + Dt);
                    hits.Clear();
                    k.DrainStructHits(hits);
                    maxPerStep = Math.Max(maxPerStep, hits.Count);
                    foreach (EmgStructHit h in hits)
                    {
                        keys.Add(h.Key);
                    }
                }
                Expect(firstDrain == EmgMatterConst.StructHitsOutCap && pendingWhileFull > 0 && maxPerStep <= EmgMatterConst.StructHitsPerStep && keys.Count == 100
                       && k.PatchStats.StructHitsDeferred > 0,
                    $"I2 100 块伤设施的斑块、热更层 2.5 秒不取：已发布表封顶 {firstDrain}、其余 {pendingWhileFull} 条留在待发布表（顺延 {k.PatchStats.StructHitsDeferred} 次）；之后每步最多发布 {maxPerStep} 条（≤ 64），100 块全部上报过（{keys.Count}），不丢");
            }
            using (CombatKernel k = NewKernel(true))
            {
                // 没有消费者的地点（前哨 / 野外）：斑块不断新生 / 死亡、一直不取走 → 两张表都封顶，不随会话时长增长
                int maxPending = 0;
                for (int wave = 0; wave < 12; wave++)
                {
                    for (int i = 0; i < 60; i++)
                    {
                        k.SpawnPatch(i % 2 == 0 ? EmgMatterConst.Acid : EmgMatterConst.Fire, new double2(i * 20, wave * 20), 1f, 1f, 1f);
                    }
                    Run(k, 1.5f);
                    maxPending = Math.Max(maxPending, k.StructHitsPending);
                }
                var hits = new List<EmgStructHit>();
                k.DrainStructHits(hits);
                Expect(maxPending <= EmgMatterConst.StructHitsPendingCap && hits.Count <= EmgMatterConst.StructHitsOutCap && k.PatchStats.StructHitsDropped > 0,
                    $"I3 没有消费者的地点、720 块斑块陆续生灭、一直不取走：待发布表最多 {maxPending} 条（≤ {EmgMatterConst.StructHitsPendingCap}）、已发布表 {hits.Count}（≤ {EmgMatterConst.StructHitsOutCap}），丢最旧的 {k.PatchStats.StructHitsDropped} 条并计数（DEBT-EMG2MAT02-07）");
            }
        }

        // ── L 读法驻留区 ─────────────────────────────────────────────────────

        /// <summary>带驻留读法（命中点落一块区域）的近战武器：冷却 10 秒 = 剧本里只打一发、只落一块。</summary>
        private static CombatWeapon DwellWeapon(uint mask)
        {
            var w = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = 5f, Cooldown = 10f };
            w.Reading = CarrierReadings.Build(ComponentCatalog.CompRamId, null, null, Array.Empty<string>(), false);
            w.Reading.ZoneRadius = 1.4f;
            w.Reading.ZoneSeconds = 5f;
            w.Reading.ZoneDps = 3f;
            w.Reading.ZoneTickScale = 1f;
            w.Reading.ZonePlacement = CombatZonePlacement.HitPoint;
            w.Reading.StatusMask = mask;
            w.Reading.StatusSeconds = 2f;
            w.Reading.StatusDps = 2f;
            return w;
        }

        private static int Shooter(CombatKernel k, double2 at, int weapon) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled, Position = at, Home = at, Radius = 0.4f, Speed = 0f,
            Health = 1e6f, MaxHealth = 1e6f, Weapon = weapon, BehaviorProfile = -1,
        });

        private static string DwellScenario(CombatKernel k, uint mask, out byte matter)
        {
            int me = Shooter(k, new double2(0, -1.5), k.AddWeapon(DwellWeapon(mask)));
            int t = Dummy(k, new double2(0, 0));
            int near = Dummy(k, new double2(0.7, 0.3));
            int ally = Dummy(k, new double2(-0.7, 0.3), CombatFaction.Player, CombatUnitKind.Machine);
            k.FireAt(me, t, k.Time); // 真实武器出手（命中走读法 → 驻留落区）
            Run(k, 0.5f);
            int at = FirstLegacy(k, CombatConst.ZoneKindReading);
            matter = at >= 0 && k.TryGetPatch(at, out CombatZone z) ? z.Matter : (byte)0;
            Run(k, 4f);
            return Sig(k, t, near, ally) + $" z{k.Counters.ZonesSpawned}";
        }

        private static void CheckDwell()
        {
            Line("  · L. 读法驻留区（漏油 / 冷却液 / 液氮 / 酸液 / 粘合剂 / 子母弹污染）真实武器命中落区：迁移前后一致、物质码、读 16 版快照转斑块");
            EmgMatterCatalog.Compiled mc = EmgMatterCatalog.Current;
            byte[] matters = { EmgMatterConst.Oil, EmgMatterConst.Coolant, EmgMatterConst.Cryo, EmgMatterConst.Acid, EmgMatterConst.Polymer, EmgMatterConst.Sludge };
            var bad = new List<string>();
            var seen = new List<string>();
            foreach (byte m in matters)
            {
                int coat = mc.Rows[m].CoatBit;
                uint mask = m == EmgMatterConst.Cryo ? Bit("Frozen") : coat >= 0 && coat < 31 ? 1u << coat : 0u;
                if (mask == 0u)
                {
                    bad.Add($"物质 {m} 没有沾染位");
                    continue;
                }
                using CombatKernel a = NewKernel(false);
                using CombatKernel b = NewKernel(true);
                string sa = DwellScenario(a, mask, out byte ma);
                string sb = DwellScenario(b, mask, out byte mb);
                seen.Add($"{m}→{mb}");
                if (sa != sb || ma != 0 || mb != m || a.LegacyPatchCount != 0 || b.Counters.ZonesSpawned < 1)
                {
                    bad.Add($"物质 {m}：层关 [{sa}] / 层开 [{sb}]，迁入物质 {mb}");
                }
            }
            Expect(bad.Count == 0, $"L1 6 类驻留区由真实武器命中落下：斑块层关 = 开（签名逐项相同），层开时是迁入斑块、物质码按映射表（{string.Join(" ", seen)}）{(bad.Count > 0 ? "；" + string.Join("；", bad) : "")}");

            // 读 16 版快照：在场的驻留区转成迁入斑块，续跑与按区域读回的一致
            uint acid = Bit("Acid");
            using CombatKernel old = NewKernel(false);
            int me0 = Shooter(old, new double2(0, -1.5), old.AddWeapon(DwellWeapon(acid)));
            int tt = Dummy(old, new double2(0, 0));
            int nn = Dummy(old, new double2(0.7, 0.3));
            old.FireAt(me0, tt, old.Time);
            Run(old, 0.5f);
            bool hasZone = old.CountZonesOfKind(CombatConst.ZoneKindReading) == 1;
            byte[] snap = old.SerializeFormatForTests(16);
            using CombatKernel baseK = NewKernel(false);
            using CombatKernel migK = NewKernel(true);
            bool ok = baseK.Load(snap) == CombatLoadResult.Ok && migK.Load(snap) == CombatLoadResult.Ok;
            int at = FirstLegacy(migK, CombatConst.ZoneKindReading);
            bool moved = migK.LastLegacyMigrated == 1 && at >= 0 && migK.TryGetPatch(at, out CombatZone mz) && mz.Matter == EmgMatterConst.Acid && baseK.LegacyPatchCount == 0;
            Run(baseK, 4f);
            Run(migK, 4f);
            string s0 = Sig(baseK, tt, nn), s1 = Sig(migK, tt, nn);
            Expect(hasZone && ok && moved && s0 == s1, $"L2 读 16 版快照：在场的酸液驻留区转成迁入斑块（{migK.LastLegacyMigrated} 块，mat.acid），续跑 4 秒与按区域读回的一致 [{s0}] = [{s1}]");
        }

        /// <summary>接触名额：脚下叠 6 块迁入驻留斑块，再站进一块真 mat.fire 斑块——仍被点燃、吃接触伤害（迁入斑块不占名额）。</summary>
        private static void CheckContactSlots()
        {
            Line("  · M. 迁入斑块不占单位接触名额（ContactPerUnit = 4）");
            using CombatKernel k = NewKernel(true);
            int s = Dummy(k, new double2(30, 30));
            for (int i = 0; i < 6; i++)
            {
                k.SpawnSlickForTests(s, new double2(0.01 * i, 0), 1.5f, 30f, Bit("Oil"), 10f);
            }
            int legacy = k.LegacyPatchCount;
            int unit = Dummy(k, new double2(0, 0), CombatFaction.Player, CombatUnitKind.Machine);
            k.SpawnPatch(EmgMatterConst.Fire, new double2(0.9, 0), 1.2f, 1f, 30f);
            Run(k, 0.5f);
            int fireCoat = EmgMatterCatalog.Current.Rows[EmgMatterConst.Fire].CoatBit;
            bool lit = fireCoat >= 0 && (Status(k, unit) & (1u << fireCoat)) != 0u;
            bool mask = (k.MatterMaskAt(new double2(0, 0)) & (1u << EmgMatterConst.Fire)) != 0u;
            Expect(legacy == 6 && lit && mask,
                $"M1 6 块迁入油迹叠在脚下（{legacy}）+ 一块离得更远的真明火斑块：单位照样挂上燃烧沾染（{lit}），物质掩码含明火（{mask}）；修复前 4 个名额被迁入油迹占满、点不着");
        }

        // ── J 倍速与暂停 ─────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            Line("  · J. 0.5x～3x 与暂停（FGJ-M6 火墙同一套区域节拍）");
            var sigs = new List<string>();
            foreach (int perFrame in new[] { 1, 2, 6 })
            {
                using CombatKernel k = NewKernel(true);
                k.UpsertLeakZone(7, new double2(0, 0), 1.5f, Bit("Oil"), EmgMatterConst.Oil);
                int foe = Dummy(k, new double2(0.5, 0));
                int burner = Dummy(k, new double2(0, -0.6));
                k.ApplyStatus(burner, Bit("Fire"), 20f, 4f, 0f, 0f, 0);
                Run(k, 3f, perFrame);
                ulong h0 = k.StateHash();
                sigs.Add(Sig(k, foe, burner) + "#" + h0);
            }
            Expect(sigs.Distinct().Count() == 1, $"J1 每帧 1 / 2 / 6 步（0.5x～3x 只改每帧步数）结果与哈希一致：{sigs[0]}");
            using (CombatKernel k = NewKernel(true))
            {
                int s = Dummy(k, new double2(10, 10));
                k.SpawnPoolForTests(s, new double2(0, 0), 2.5f, 8f, 6f, Bit("Acid"), 4f, 0f);
                Run(k, 1f);
                ulong h = k.StateHash();
                Expect(h == k.StateHash() && k.LegacyPatchCount == 1, "J2 暂停（不步进）时迁入斑块不变、不到期");
            }
        }

        // ── K 耗时（只测一次，不重跑；用户 2026-09-30）──────────────────────────

        private static void CheckPerformance()
        {
            Line("  · K. 耗时：48 摊液洼 + 120 块喷淋池 / 油迹 + 200 块读法残留 + 300 单位（迁入斑块走区域节拍 + 冲洗 + 整片反应）");
            using CombatKernel k = NewKernel(true);
            uint oil = Bit("Oil"), acid = Bit("Acid"), fire = Bit("Fire");
            int s = Dummy(k, new double2(500, 500));
            for (int i = 0; i < 48; i++)
            {
                k.UpsertLeakZone(i + 1, new double2((i % 8) * 12, (i / 8) * 12), 2f, oil, EmgMatterConst.Oil);
            }
            for (int i = 0; i < 120; i++)
            {
                if (i % 2 == 0)
                {
                    k.SpawnPoolForTests(s, new double2(120 + (i % 12) * 6, (i / 12) * 6), 2.5f, 60f, 6f, acid, 4f, 0f);
                }
                else
                {
                    k.SpawnSlickForTests(s, new double2(120 + (i % 12) * 6, (i / 12) * 6), 1.2f, 60f, oil, 10f);
                }
            }
            for (int i = 0; i < 300; i++)
            {
                int u = Dummy(k, new double2((i % 20) * 9, (i / 20) * 9), i % 3 == 0 ? CombatFaction.Player : CombatFaction.Hostile,
                    i % 3 == 0 ? CombatUnitKind.Machine : CombatUnitKind.Enemy, 100000f);
                if (i < 200)
                {
                    k.ApplyStatus(u, oil, 30f, 0f, 0f, 0f, 0);
                    k.ApplyStatus(u, fire, 30f, 1f, 0f, 0f, 0, 5f); // 反应残留 → 迁入斑块
                }
            }
            Run(k, 0.5f);
            int legacy = k.LegacyPatchCount;
            var sw = Stopwatch.StartNew();
            const int steps = 300;
            for (int i = 0; i < steps; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            double ms = sw.Elapsed.TotalMilliseconds / steps;
            const double budget = 2.0; // 迁移前同规模区域路径的量级（FGR-EMG-200 内核每步预算的一部分）；超线不到 2 倍记警告
            string cpu = $"{SystemInfo.processorType}，{SystemInfo.processorCount} 线程，Editor batchmode（Burst 编译，真机通常更快）";
            if (ms > budget && ms <= budget * 2)
            {
                Line($"    ! 性能警告：每步 {ms:0.###} ms > {budget} ms（未到 2 倍，照常交付；{cpu}）");
            }
            Expect(legacy >= 160 && ms <= budget * 2,
                $"K1 {legacy} 块迁入斑块 + 300 单位：每步平均 {ms:0.###} ms（门槛 {budget} ms，失败线 2 倍；只测一次；{cpu}）");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────

        private static void Step(Action a)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
            Line($"    （{a.Method.Name} {sw.ElapsedMilliseconds} ms）");
        }

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("    ✗ " + what);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
