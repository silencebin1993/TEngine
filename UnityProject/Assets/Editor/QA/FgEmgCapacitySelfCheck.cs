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
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMGR-MAT-01 斑块与区域容量的自动验收（FG19 FGR-EMG-083 / 087、FG19:688、FG19B:221 / §8.1 / §8.4、FG00 B12；FG-GAP-217、DEBT-FG2FW02-06、DEBT-EMG2MAT02-03）。
    /// 起真实内核（CombatKernel，Burst 步进），走生产同一条生成路径（SpawnPatch / SpawnReservedPatch / 读法区域 SpawnZone / 澄净 SpawnPool、SpawnSlick）：
    /// A 敌方 / 中立结构单位挨斑块（明火挂燃烧掉血、熔融掉血、酸挂腐蚀；外部血量结构单位走伤害请求、内核不扣血）；己方结构单位不在内核扣第二遍；结构单位死亡当步不再接触；洗礼塔灌注外缘不碰塔、泵照挨；
    /// B 读法区域 600 块 + 迁入斑块照常生成、ReadingRefused = 0；2048 仍是上限且拒绝计数可见；斑块层满 + 区域满同时发生两边计数可见；
    /// C 20 台油污爬行者喷满后新油迹 / 新喷淋池照常出现、最早的提前到期；迁入油迹与正常油斑块合计到同种软上限时最早的（不论来源）提前到期；斑块层关时满了计数可见；被挤掉的迁入斑块当步不再结算；
    /// D 存读档（续跑一致）；E 每帧 1 / 2 / 6 步（0.5x～3x）一致、暂停不变。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgCapacitySelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgCapacitySelfCheck。
    /// </summary>
    public static class FgEmgCapacitySelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/斑块与区域容量")]
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
            Line("\n[斑块与区域容量] EMGR-MAT-01：敌方结构单位挨斑块（FG-GAP-217）/ 读法区域上限 2048 与迁入交斑块层（DEBT-FG2FW02-06）/ 喷淋池与油迹并入物质斑块上限（DEBT-EMG2MAT02-03）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                Step(CheckStructures);
                Step(CheckZones);
                Step(CheckPools);
                Step(CheckSaveLoad);
                Step(CheckSpeed);
            }
            catch (Exception e)
            {
                Fail($"斑块与区域容量自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [斑块与区域容量] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        /// <summary>与 CombatSite 建地点同一套写表；<paramref name="zoneCap"/> / <paramref name="poolCap"/> 0 = 表值。</summary>
        private static CombatKernel NewKernel(bool patches = true, int capacity = 0, int reserve = -1, int zoneCap = 0, int poolCap = 0)
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
            if (zoneCap > 0)
            {
                c.ZoneCapacity = zoneCap;
            }
            if (poolCap > 0)
            {
                c.PoolZoneCapacity = poolCap;
            }
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyContent(k); // 修复轮 2：原先漏登记通道 / 相态 / 场规则，C 行反应（C01 爆燃）在本段内核里不生效
            return k;
        }

        private static int Unit(CombatKernel k, double2 at, CombatFaction f, CombatUnitKind kind, float hp, CombatUnitFlags extra = 0) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = kind,
            Faction = f,
            Behavior = CombatBehavior.None,
            Flags = Live | extra,
            Position = at,
            Home = at,
            Radius = 0.5f,
            Health = hp,
            MaxHealth = hp,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        });

        private static void StepN(CombatKernel k, int n, int perFrame = 1)
        {
            for (int i = 0; i < n; i += perFrame)
            {
                for (int j = 0; j < perFrame && i + j < n; j++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
            }
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static bool HasBit(CombatKernel k, int id, int bit) =>
            bit >= 0 && bit < 31 && k.TryGetStatus(id, out uint m, out _, out _, out _, out _) && (m & (1u << bit)) != 0u;

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            CombatEvent[] buf = k.DrainGameplay(8192, out int n);
            return buf.Take(n).ToList();
        }

        private static List<CombatZone> Patches(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        private static bool IsLegacy(in CombatZone z) => (z.PFlags & EmgMatterConst.FlagLegacy) != 0;

        private static int CountMatter(CombatKernel k, byte matter) => Patches(k).Count(z => z.Matter == matter && (z.PFlags & EmgMatterConst.FlagWorld) == 0);

        private static bool PatchAt(CombatKernel k, byte matter, double2 at, bool? legacy = null) =>
            Patches(k).Any(z => z.Matter == matter && math.distance(z.Pos, at) < 1e-3 && (legacy == null || IsLegacy(z) == legacy.Value));

        private static int Quota(CombatKernel k, byte matter) => k.MatterRow(matter).Quota;

        // ── A. 结构单位挨斑块（FG-GAP-217）────────────────────────────────────────

        private static void CheckStructures()
        {
            Line("  · A. 敌方 / 中立结构单位挨斑块（FGR-EMG-083 单位含敌人，ADR ⑤）；己方结构单位（家园建筑等）不在内核扣第二遍；外部血量走伤害请求；死亡当步不再接触；洗礼塔灌注不碰塔");
            using (CombatKernel k = NewKernel())
            {
                int player = Unit(k, new double2(-40, 0), CombatFaction.Player, CombatUnitKind.Machine, 500f);
                int aa = Unit(k, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
                int node = Unit(k, new double2(10, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
                int pump = Unit(k, new double2(20, 0), CombatFaction.Neutral, CombatUnitKind.Structure, 400f);
                int home = Unit(k, new double2(30, 0), CombatFaction.Player, CombatUnitKind.Structure, 400f);
                int core = Unit(k, new double2(40, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f, CombatUnitFlags.ExternalHealth);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f, player);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(10, 0), 2f, 1f, 30f, player);
                k.SpawnPatch(EmgMatterConst.Acid, new double2(20, 0), 2f, 1f, 30f, player);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(30, 0), 2f, 1f, 30f, player);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(40, 0), 2f, 1f, 30f, player);
                Drain(k);
                StepN(k, 180);
                List<CombatEvent> ev = Drain(k);
                int fireBit = k.MatterRow(EmgMatterConst.Fire).CoatBit, acidBit = k.MatterRow(EmgMatterConst.Acid).CoatBit, moltenBit = k.MatterRow(EmgMatterConst.Molten).CoatBit;
                float dAa = 400f - Hp(k, aa), dNode = 400f - Hp(k, node), dPump = 400f - Hp(k, pump), dHome = 400f - Hp(k, home), dCore = 400f - Hp(k, core);
                float reqCore = ev.Where(e => e.Kind == CombatEventKind.DamageRequest && e.Unit == core).Sum(e => e.Value);
                Expect(dAa > 0f && HasBit(k, aa, fireBit),
                    $"A1 敌方结构单位（防空炮类，血量在内核）站进明火 3 秒：掉血 {dAa:0.0}、挂燃烧位 {fireBit}（{HasBit(k, aa, fireBit)}）");
                Expect(dNode > 10f && HasBit(k, node, moltenBit),
                    $"A2 敌方结构单位（节点类）站进熔融 3 秒：掉血 {dNode:0.0}（熔融接触 {k.MatterRow(EmgMatterConst.Molten).ContactDps}/秒）、沾染位 {moltenBit}");
                Expect(HasBit(k, pump, acidBit),
                    $"A3 中立结构单位（洗礼塔泵类）站进酸：挂腐蚀位 {acidBit}（{HasBit(k, pump, acidBit)}），掉血 {dPump:0.0}（酸本身无接触伤害，腐蚀按状态效果结算）");
                Expect(Mathf.Approximately(dHome, 0f) && !HasBit(k, home, moltenBit),
                    $"A4 反例：己方结构单位（家园建筑等，耐久在热更层由 PipeLeakService 结算）站进熔融不在内核扣血（{dHome:0.0}）、不挂沾染——不扣两遍");
                Expect(reqCore > 10f && Mathf.Approximately(dCore, 0f),
                    $"A5 外部血量结构单位（首领 / 核心）站进熔融：发出伤害请求合计 {reqCore:0.0}（DamageFrom → 外部血量同一条路径），内核血量不动（{dCore:0.0}）");
            }

            // 死亡当步再接触：血量 5 的敌方结构单位站在熔融 + 明火叠加里，阵亡只报一次、阵亡后不再收伤害请求 / 受伤
            using (CombatKernel k = NewKernel())
            {
                int tiny = Unit(k, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 5f, CombatUnitFlags.Report);
                k.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 1f, 30f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0.2), 2f, 1f, 30f);
                var all = new List<CombatEvent>();
                for (int i = 0; i < 240; i++)
                {
                    StepN(k, 1);
                    all.AddRange(Drain(k));
                }
                int killed = all.Count(e => e.Kind == CombatEventKind.Killed && e.Unit == tiny);
                int deathAt = all.FindIndex(e => e.Kind == CombatEventKind.Killed && e.Unit == tiny);
                int after = deathAt < 0 ? -1 : all.Skip(deathAt + 1).Count(e => e.Unit == tiny && (e.Kind == CombatEventKind.Damaged || e.Kind == CombatEventKind.DamageRequest));
                Expect(!Alive(k, tiny) && killed == 1 && after == 0,
                    $"A6 负向：结构单位在斑块里阵亡（存活 {Alive(k, tiny)}）：阵亡事件 {killed} 次（应 1）、阵亡后再收伤害 {after} 次（应 0）");
            }

            // 洗礼塔：灌注外缘离塔中心 ≥ fill_tower_clear（BaptismTowerBoss.FillSpot），塔站在圈外不被自己的灌注 / 引发明火烧穿；泵在灌注里照挨（预期差异）
            using (CombatKernel k = NewKernel())
            {
                float fillR = BaptismTowerBoss.FillRadius, clear = BaptismTowerBoss.FillTowerClear;
                int tower = Unit(k, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 5000f, CombatUnitFlags.ExternalHealth);
                var spot = new double2(fillR + clear, 0);
                int pump = Unit(k, spot, CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
                int acid = k.SpawnReservedPatch(EmgMatterConst.Acid, spot, fillR, 1f, 10f);
                int fire = k.SpawnReservedPatch(EmgMatterConst.Fire, spot, fillR, 1f, 10f);
                Drain(k);
                StepN(k, 180);
                List<CombatEvent> ev = Drain(k);
                int towerHits = ev.Count(e => e.Kind == CombatEventKind.DamageRequest && e.Unit == tower);
                int acidBit = k.MatterRow(EmgMatterConst.Acid).CoatBit;
                Expect(acid != 0 && fire != 0 && towerHits == 0 && !HasBit(k, tower, acidBit) && 400f - Hp(k, pump) > 0f,
                    $"A7 负向：洗礼塔灌注（半径 {fillR}、中心离塔 {fillR + clear} 米）与引发明火不碰塔（伤害请求 {towerHits} 次、沾酸 {HasBit(k, tower, acidBit)}）；圈内的泵掉血 {400f - Hp(k, pump):0.0}（预期差异）");
            }
        }

        // ── B. 读法区域上限（DEBT-FG2FW02-06）───────────────────────────────────

        private static void CheckZones()
        {
            Line("  · B. 读法区域上限 2048（表 reading.capacity.zones 与常量一致）、迁入斑块不占区域名额交斑块层降级、拒绝计数可见");
            Expect(CombatConst.DefaultZoneCapacity == 2048 && Mathf.RoundToInt(CombatSite.Tuning("reading.capacity.zones", 0f)) == 2048,
                $"B0 区域上限：常量 {CombatConst.DefaultZoneCapacity}、表 {CombatSite.Tuning("reading.capacity.zones", 0f)}（都应 2048）");
            uint acid = Bit("Acid");
            using (CombatKernel k = NewKernel())
            {
                int ok = 0;
                for (int i = 0; i < 600; i++)
                {
                    // 映射不出物质（减速网 / 电击之类）的读法区域照旧落区域
                    ok += k.SpawnReadingZoneForTests(CombatFaction.Player, new double2((i % 30) * 3, (i / 30) * 3), 1f, 60f, 1f, 0u) ? 1 : 0;
                }
                for (int i = 0; i < 200; i++)
                {
                    ok += k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(200 + (i % 20) * 3, (i / 20) * 3), 1f, 60f, 1f, acid) ? 1 : 0;
                }
                int zones = k.ZoneCount - k.LegacyPatchCount, legacy = k.LegacyPatchCount, q = Quota(k, EmgMatterConst.Acid);
                bool newest = PatchAt(k, EmgMatterConst.Acid, new double2(200 + 19 * 3, 9 * 3), true);
                Expect(ok == 800 && k.Counters.ReadingRefused == 0 && zones == 600 && legacy == q && newest && k.PatchStats.PatchSoftExpired == 200 - q,
                    $"B1 铺 600 块读法区域 + 200 块迁入酸驻留：800 次全部生成（{ok}）、ReadingRefused = {k.Counters.ReadingRefused}；区域 {zones} 块；迁入酸 {legacy} 块 = 软上限 {q}，" +
                    $"最新一块在场（{newest}），最早的 {k.PatchStats.PatchSoftExpired} 块提前到期（不静默）");
                StepN(k, 30);
                Expect(k.ZoneCount - k.LegacyPatchCount == 600 && k.LegacyPatchCount == q, $"B1b 续跑 0.5 秒：区域 {k.ZoneCount - k.LegacyPatchCount}、迁入 {k.LegacyPatchCount}（区域节拍照常结算、不崩）");
            }
            using (CombatKernel k = NewKernel())
            {
                int ok = 0;
                for (int i = 0; i < 2048; i++)
                {
                    ok += k.SpawnReadingZoneForTests(CombatFaction.Player, new double2((i % 64) * 2, (i / 64) * 2), 0.8f, 60f, 1f, 0u) ? 1 : 0;
                }
                bool extra = k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(-50, -50), 0.8f, 60f, 1f, 0u);
                bool migrated = k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(-60, -60), 0.8f, 60f, 1f, acid);
                Expect(ok == 2048 && !extra && k.Counters.ReadingRefused == 1 && migrated && k.LegacyPatchCount == 1,
                    $"B2 2048 仍是上限（DEBT-EMGRMAT01-01）：2048 块全部生成（{ok}），第 2049 块被拒（{!extra}）、ReadingRefused = {k.Counters.ReadingRefused}（计数可见）；区域满时迁入斑块照常进斑块层（{migrated}）");
            }
            using (CombatKernel k = NewKernel(capacity: 32, reserve: 16, zoneCap: 4)) // 战斗斑块 = 容量 32 − 世界源保留 16 = 16
            {
                int ok = 0;
                for (int i = 0; i < 30; i++)
                {
                    ok += k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(i * 3, 0), 1f, 60f, 1f, acid) ? 1 : 0;
                }
                EmgScalars s = k.PatchStats;
                Expect(k.LegacyPatchCount == 16 && k.ZoneCount - k.LegacyPatchCount == 4 && ok == 20 && s.LegacyKeptZone == 14 && k.Counters.ReadingRefused == 10,
                    $"B3 负向：斑块层满（16）+ 区域满（4）同时发生：迁入 {k.LegacyPatchCount}、退回区域 {k.ZoneCount - k.LegacyPatchCount}、生成 {ok}/30；两边计数都可见：斑块满落区域 {s.LegacyKeptZone}（应 14）、ReadingRefused {k.Counters.ReadingRefused}（应 10）");
            }
        }

        // ── C. 喷淋池 / 油迹并入物质斑块上限（DEBT-EMG2MAT02-03）──────────────────────

        private static void CheckPools()
        {
            Line("  · C. 喷淋池 / 油迹并入物质斑块同种软上限与总上限（FG19B:221 / §8.1）：满了最早的提前到期（不论来源）；斑块层关时 clarity.pool.cap 满了计数可见");
            uint oil = Bit("Oil"), acid = Bit("Acid");
            using (CombatKernel k = NewKernel())
            {
                var crawlers = new int[20];
                for (int c = 0; c < crawlers.Length; c++)
                {
                    crawlers[c] = Unit(k, new double2(c * 12, 300), CombatFaction.Neutral, CombatUnitKind.Enemy, 100f);
                }
                int ok = 0;
                double2 last = default, first = new double2(0, 0);
                for (int t = 0; t < 15; t++)
                {
                    for (int c = 0; c < crawlers.Length; c++)
                    {
                        last = new double2(c * 12, t * 2.5);
                        ok += k.SpawnSlickForTests(crawlers[c], last, 1.2f, 60f, oil, 10f) ? 1 : 0; // 每台一路留 15 摊油迹（与澄净 SpawnSlick 同一路径）
                    }
                    StepN(k, 6);
                }
                int pools = 0;
                double2 lastPool = default;
                for (int i = 0; i < 100; i++)
                {
                    lastPool = new double2(400 + (i % 10) * 6, (i / 10) * 6);
                    pools += k.SpawnPoolForTests(crawlers[i % 20], lastPool, 2.5f, 60f, 6f, acid, 4f, 0f) ? 1 : 0;
                }
                int qOil = Quota(k, EmgMatterConst.Oil), qAcid = Quota(k, EmgMatterConst.Acid);
                int oilN = CountMatter(k, EmgMatterConst.Oil), acidN = CountMatter(k, EmgMatterConst.Acid);
                Expect(ok == 300 && pools == 100 && oilN == qOil && acidN == qAcid && PatchAt(k, EmgMatterConst.Oil, last, true) && !PatchAt(k, EmgMatterConst.Oil, first)
                       && PatchAt(k, EmgMatterConst.Acid, lastPool, true) && k.PatchStats.PoolRefused == 0 && k.PatchStats.PatchSoftExpired >= (300 - qOil) + (100 - qAcid),
                    $"C1 20 台油污爬行者喷满（300 摊油迹）+ 100 块酸喷淋池：全部生成（{ok} / {pools}），油 {oilN} = 软上限 {qOil}、酸 {acidN} = 软上限 {qAcid}；最新的在场、最早的提前到期（计 {k.PatchStats.PatchSoftExpired}），" +
                    $"不再按 clarity.pool.cap 192 静默丢最新（PoolRefused {k.PatchStats.PoolRefused}）");
            }

            // 迁入油迹与正常油斑块合计到同种软上限：最早的那块不论来源提前到期
            using (CombatKernel k = NewKernel())
            {
                int q = Quota(k, EmgMatterConst.Oil);
                int owner = Unit(k, new double2(-100, -100), CombatFaction.Neutral, CombatUnitKind.Enemy, 100f);
                for (int i = 0; i < q; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2((i % 10) * 10, (i / 10) * 10), 1f, 1f, 60f);
                    if (i % 30 == 29)
                    {
                        StepN(k, 1);
                    }
                }
                StepN(k, 1);
                int before = CountMatter(k, EmgMatterConst.Oil), soft0 = k.PatchStats.PatchSoftExpired;
                bool slick = k.SpawnSlickForTests(owner, new double2(500, 500), 1.2f, 60f, oil, 10f);
                bool firstGone = !PatchAt(k, EmgMatterConst.Oil, new double2(0, 0));
                Expect(before == q && slick && firstGone && CountMatter(k, EmgMatterConst.Oil) == q && PatchAt(k, EmgMatterConst.Oil, new double2(500, 500), true) && k.PatchStats.PatchSoftExpired == soft0 + 1,
                    $"C2a {q} 块正常油斑块 + 1 摊迁入油迹：最早的正常油斑块提前到期（{firstGone}）、油迹在场，合计 {CountMatter(k, EmgMatterConst.Oil)} = 软上限（预期差异：爬行者的油迹可能挤掉玩家排出的同种油斑块）");
            }
            using (CombatKernel k = NewKernel())
            {
                int q = Quota(k, EmgMatterConst.Oil);
                int owner = Unit(k, new double2(-100, -100), CombatFaction.Neutral, CombatUnitKind.Enemy, 100f);
                int victim = Unit(k, new double2(-200, -200), CombatFaction.Player, CombatUnitKind.Machine, 500f);
                k.SpawnSlickForTests(owner, new double2(-200, -200), 1.2f, 60f, oil, 10f); // 最早的那块是迁入油迹，有人正站在里面（区域节拍正在读它）
                StepN(k, 40);
                bool coatedBefore = HasBit(k, victim, k.MatterRow(EmgMatterConst.Oil).CoatBit);
                for (int i = 0; i < q - 1; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2((i % 10) * 10, (i / 10) * 10), 1f, 1f, 60f);
                    if (i % 30 == 29)
                    {
                        StepN(k, 1);
                    }
                }
                StepN(k, 1);
                int before = CountMatter(k, EmgMatterConst.Oil), soft0 = k.PatchStats.PatchSoftExpired;
                bool spawned = k.SpawnPatch(EmgMatterConst.Oil, new double2(300, 300), 1f, 1f, 60f); // 离所有同种 > 6 米：不就近并入
                StepN(k, 1);
                bool slickGone = !PatchAt(k, EmgMatterConst.Oil, new double2(-200, -200));
                int legacyLeft = k.LegacyPatchCount;
                StepN(k, 60);
                Expect(coatedBefore && before == q && spawned && slickGone && legacyLeft == 0 && CountMatter(k, EmgMatterConst.Oil) <= q && k.PatchStats.PatchSoftExpired == soft0 + 1 && Hp(k, victim) > 0f,
                    $"C2b 最早的是迁入油迹（有单位站在里面、区域节拍在读）：新正常油斑块生成（{spawned}），迁入油迹提前到期（{slickGone}，剩迁入 {legacyLeft}），被挤掉当步与之后续跑不崩、不再结算");
            }
            // 迁入斑块在自己的区域节拍里被挤掉：迁入明火（最早、槽位 0）烧到沾油单位 → C01 爆燃在同一节拍里生成明火，同种已满 → 挤掉的正是这块迁入明火、新明火落进同一槽位；
            // 节拍结束写回时不得用旧副本盖掉新明火（审查 P1，StepLegacyPatches 按斑块 Id 核对）
            using (CombatKernel k = NewKernel())
            {
                int q = Quota(k, EmgMatterConst.Fire);
                var at = new double2(-200, -200);
                int victim = Unit(k, new double2(-300, -300), CombatFaction.Hostile, CombatUnitKind.Machine, 500f);
                bool legacyFire = k.SpawnReadingZoneForTests(CombatFaction.Player, at, 1.5f, 60f, 0f, 1u << k.MatterRow(EmgMatterConst.Fire).CoatBit);
                StepN(k, 1);
                for (int i = 0; i < q - 1; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Fire, new double2((i % 10) * 10, (i / 10) * 10), 1f, 1f, 60f);
                    if (i % 30 == 29)
                    {
                        StepN(k, 1);
                    }
                }
                StepN(k, 1);
                int before = CountMatter(k, EmgMatterConst.Fire), legacy0 = k.LegacyPatchCount, soft0 = k.PatchStats.PatchSoftExpired;
                int oilBit = k.MatterRow(EmgMatterConst.Oil).CoatBit;
                k.ApplyStatus(victim, 1u << oilBit, 30f, 0f, 0f, 0f, 0);
                k.SetPosition(victim, at); // 沾油后才走进迁入明火：C01 只能由迁入明火自己的节拍触发
                StepN(k, 60);
                bool newFire = Patches(k).Any(z => z.Matter == EmgMatterConst.Fire && !IsLegacy(z) && math.distance(z.Pos, at) < 2.0);
                Expect(legacyFire && before == q && legacy0 == 1 && !HasBit(k, victim, oilBit) && newFire && k.LegacyPatchCount == 0
                       && CountMatter(k, EmgMatterConst.Fire) == q && k.PatchStats.PatchSoftExpired == soft0 + 1,
                    $"C2c 负向：迁入明火在自己的节拍里点着沾油单位（油已消耗 {!HasBit(k, victim, oilBit)}）、同种已满（{before} = {q}）：爆燃新明火在场（{newFire}），" +
                    $"被挤掉的迁入明火不复活（剩迁入 {k.LegacyPatchCount}），明火 {CountMatter(k, EmgMatterConst.Fire)} = 软上限，提前到期 +{k.PatchStats.PatchSoftExpired - soft0}");
            }
            using (CombatKernel k = NewKernel(patches: false))
            {
                uint oilBit = oil;
                int owner = Unit(k, new double2(-100, -100), CombatFaction.Neutral, CombatUnitKind.Enemy, 100f);
                int cap = Mathf.RoundToInt(CombatSite.Tuning("clarity.pool.cap", 192f)), ok = 0;
                for (int i = 0; i < cap + 8; i++)
                {
                    ok += k.SpawnSlickForTests(owner, new double2((i % 20) * 3, (i / 20) * 3), 1.2f, 60f, oilBit, 10f) ? 1 : 0;
                }
                Expect(ok == cap && k.CountZonesOfKind(CombatConst.ZoneKindSlick) == cap && k.PatchStats.PoolRefused == 8,
                    $"C3 负向：斑块层关时喷淋池 / 油迹仍按 clarity.pool.cap {cap} 落区域：生成 {ok}，满了不生成且计数可见（PoolRefused {k.PatchStats.PoolRefused}，应 8；调试层斑块行同时显示）");
            }
        }

        // ── D. 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · D. 存读档：结构单位接触 + 迁入斑块挤占后的中途快照，读回续跑与原内核逐位一致（新增计数 PoolRefused 不进快照，格式版本不变）");
            uint oil = Bit("Oil");
            using CombatKernel a = NewKernel();
            int s = Unit(a, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
            int owner = Unit(a, new double2(-100, -100), CombatFaction.Neutral, CombatUnitKind.Enemy, 100f);
            a.SpawnPatch(EmgMatterConst.Molten, new double2(0, 0), 2f, 1f, 30f);
            for (int i = 0; i < 120; i++)
            {
                a.SpawnSlickForTests(owner, new double2(50 + (i % 12) * 3, (i / 12) * 3), 1.2f, 60f, oil, 10f);
            }
            StepN(a, 60);
            byte[] snap = a.Serialize();
            using CombatKernel b = NewKernel();
            CombatLoadResult r = b.Load(snap);
            StepN(a, 120);
            StepN(b, 120);
            Expect(r == CombatLoadResult.Ok && a.StateHash() == b.StateHash() && a.EmgStateHash() == b.EmgStateHash() && Mathf.Approximately(Hp(a, s), Hp(b, s)) && Hp(a, s) < 400f
                   && a.LegacyPatchCount == b.LegacyPatchCount,
                $"D1 读档 {r}：续跑 2 秒状态哈希一致（{a.StateHash() == b.StateHash()} / 涌现 {a.EmgStateHash() == b.EmgStateHash()}），结构单位血量 {Hp(a, s):0.0} / {Hp(b, s):0.0}，迁入斑块 {a.LegacyPatchCount} / {b.LegacyPatchCount}");
        }

        // ── E. 倍速与暂停 ─────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            Line("  · E. 0.5x～3x（每帧 1 / 2 / 6 步，只改每帧步数）与暂停：敌方结构单位挨明火的结果一致");
            var sigs = new List<string>();
            foreach (int perFrame in new[] { 1, 2, 6 })
            {
                using CombatKernel k = NewKernel();
                int s = Unit(k, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f);
                StepN(k, 180, perFrame);
                sigs.Add($"{Mathf.RoundToInt(Hp(k, s) * 100f)}:{k.StateHash()}");
            }
            Expect(sigs.Distinct().Count() == 1 && !sigs[0].StartsWith("40000:"), $"E1 每帧 1 / 2 / 6 步结果与哈希一致：{sigs[0]}");
            using (CombatKernel k = NewKernel())
            {
                int s = Unit(k, new double2(0, 0), CombatFaction.Hostile, CombatUnitKind.Structure, 400f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 1f, 30f);
                StepN(k, 60);
                ulong h = k.StateHash();
                float hp = Hp(k, s);
                Expect(h == k.StateHash() && Mathf.Approximately(hp, Hp(k, s)), $"E2 暂停（不步进）时结构单位血量 {hp:0.0} 与状态不变");
            }
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
                Line($"    ✓ {what}");
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line($"    ✗ {what}");
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
