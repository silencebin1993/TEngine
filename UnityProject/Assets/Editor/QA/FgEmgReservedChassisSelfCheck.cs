using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG3-CHS-02 预留底盘特性接口（FG19 FGR-EMG-140 / 142，ADR-EMG-001 ⑧；FGT-EMG-050 预留 3 条）的自动验收。全部起真实内核（CombatSite.ApplyContent 同一套表写入）：
    /// A 正式路径不放出（三行仍预留、不进底盘目录、生成时不给特性）；B 测试底盘行（只在本自检数据里把三行改为正式行 = 将来落地的样子）经正式入口拿到特性与实体档案；
    /// C 震地（evt.impact 节拍、碎冰、溅液、扬尘、停住不震）；D 空投（落点不被截停、不可通行区照常、半径 × 0.6、离地、chs.airborne 对悬浮与飞行同样生效）；
    /// E 取水（编译期 DrawHeat、水面上不计热量 / 排放 × 1.5、离水立即失效）；F 存读档（震地节拍连续、DrawHeat 往返、测试底盘行混进正式存档被拒并报错）；
    /// G 暂停与倍速；H FG-GAP-197 机型 ID 实体档案；I 耗时。已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgReservedChassisSelfCheck）。
    /// </summary>
    public static class FgEmgReservedChassisSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const byte Sense = 3;
        private static readonly string[] Reserved = { "chassis_legged", "chassis_air", "chassis_hull" };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/预留底盘特性（多足、飞行、船体）")]
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
            Line("\n[预留底盘特性] EMG3-CHS-02：震地 / 空投 / 取水（测试底盘行验证，FGT-EMG-050 预留 3 条）、测试行混进正式存档被拒");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                EmgPhaseCatalog.Reload();
                EmgFieldCatalog.Reload();
                NamedReactionCatalog.Reload();
                FirmwareKinds.ResetForTests();
                Step(CheckFormalPathClosed);
                Step(CheckTestRows);
                Step(CheckStomp);
                Step(CheckAirDrop);
                Step(CheckHullDraw);
                Step(CheckSaveLoad);
                Step(CheckTiming);
                Step(CheckMachineTypeProfile);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [预留底盘特性] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>测试底盘行：只在本自检的数据里把三条预留特性行改成正式行（将来多足 / 飞行 / 船体落地时就是这样改表），底盘外键放行这三个预留 ID。正式表不动。</summary>
        private static void UseTestRows()
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, bool> exists = src.ChassisExists;
            foreach (EmgSource.Trait t in src.Traits)
            {
                if (t.Reserved == 1)
                {
                    t.Reserved = 0;
                }
            }
            src.ChassisExists = id => exists(id) || Array.IndexOf(Reserved, id) >= 0;
            EmgCatalog.OverrideForTests(src);
        }

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyContent(k);
            return k;
        }

        private static int Machine(CombatKernel k, double2 p, byte trait = EmgTraitConst.None, int weapon = -1, float hp = 400f, float speed = 4f)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live, Position = p, Home = p,
                Radius = 0.5f, Speed = speed, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });
            if (trait != EmgTraitConst.None)
            {
                k.SetUnitTrait(id, trait);
            }
            return id;
        }

        private static int Foe(CombatKernel k, double2 p, float hp = 400f) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | CombatUnitFlags.Report, Position = p, Home = p,
                Radius = 0.5f, Speed = 2f, Health = hp, MaxHealth = hp, Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
            });

        /// <summary>只监听若干事件的程序 + 可选 chs.* condition（与第 0 条同序号）。</summary>
        private static int Listener(CombatKernel k, ushort chsCondition, params EmgEventKind[] kinds)
        {
            var instrs = kinds.Select(x => new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)x), Role = Sense, Ordinal = 0 }).ToList();
            if (chsCondition != 0)
            {
                instrs.Add(new EmgInstr { Port = chsCondition, Role = EmgConst.RoleCondition, Ordinal = 0 });
            }
            uint mask = kinds.Aggregate(0u, (m, x) => m | 1u << (int)x);
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            int prog = k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
            return k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 0, Range = 1f, Program = prog });
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : new double2(double.NaN);

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static float Heat(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Heat : -1f;

        private static void Move(CombatKernel k, int id, double2 to) => k.IssueCommand(id, CombatCommandKind.Move, to, 0, 0.2f, 0f, 0f, false);

        private static List<CombatZone> PatchesOf(CombatKernel k, byte matter)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && (matter == 0 || z.Matter == matter))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        /// <summary>场上的布区落区（读法区域 + 迁入斑块，都在 TryGetZone 里）。</summary>
        private static List<CombatZone> Zones(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        /// <summary>多足车从 <paramref name="from"/> 走向 <paramref name="to"/>：每步记一次是否唤起了 evt.impact 监听（= 一次震地）。</summary>
        private static List<int> StompSteps(CombatKernel k, int unit, int steps)
        {
            var at = new List<int>();
            for (int s = 1; s <= steps; s++)
            {
                long w0 = k.EmgBusStats.ListenWakes;
                StepN(k, 1);
                if (k.EmgBusStats.ListenWakes > w0)
                {
                    at.Add(s);
                }
            }
            return at;
        }

        // ── A 正式路径不放出 ─────────────────────────────────────────────────

        private static void CheckFormalPathClosed()
        {
            Line("  · A. 正式路径：三行仍预留（不生效、不进底盘目录 / 研发树 / 三选一）；正式入口生成时不给特性；文本键与调参键齐全；chs.water 恒 0");
            EmgCatalog.ResetForTests();
            var rows = Reserved.Select(id => (id, ok: EmgCatalog.TryGetTrait(id, out EmgTraitRow r), r)).ToList();
            bool reserved = rows.All(x => x.ok && x.r.Reserved && !EmgCatalog.TryGetActiveTrait(x.id, out _))
                            && rows[0].r.Kind == EmgTraitKind.LeggedStomp && rows[1].r.Kind == EmgTraitKind.AirDrop && rows[2].r.Kind == EmgTraitKind.HullDraw;
            bool notListed = Reserved.All(id => !ChassisCatalog.TryGet(id, out _)) && FgEmergenceFuzzSelfCheck.ChassisIds.Length == 4;
            Expect(reserved && notListed,
                $"fg.TbChassisTrait 三行 reserved = 1、机制 {string.Join("/", rows.Select(x => x.r.Kind))}、均不生效；底盘目录里没有这三个 ID（底盘列表、研发树、三选一都从目录取）；模糊测试器底盘 {FgEmergenceFuzzSelfCheck.ChassisIds.Length} 种（现行 4 种，逐字同旧）");
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            using (var site = new CombatSite("chs_res_formal", c))
            {
                int u = Machine(site.Kernel, new double2(0, 0));
                var got = new List<byte>();
                foreach (string id in Reserved)
                {
                    site.ApplyChassisTrait(u, id);
                    got.Add(site.Kernel.TraitOf(u));
                }
                Expect(got.All(g => g == EmgTraitConst.None) && site.Kernel.ChsOf(u) == EmgTraitConst.ChsGround,
                    $"正式入口 CombatSite.ApplyChassisTrait（生成 / 装配时同一函数）对三个预留底盘写入机制 {string.Join(",", got)}（全 0）；chs = {site.Kernel.ChsOf(u)}（着地，水面位 0）");
            }
            var missing = new List<string>();
            foreach (string key in rows.SelectMany(x => new[] { x.r.NameKey, x.r.DescKey }).Append("combat.load.reason.reserved_trait"))
            {
                if (string.IsNullOrEmpty(key) || GameText.Get(key) == key)
                {
                    missing.Add(key);
                }
            }
            var tuning = new[] { "emg.trait.legged.step", "emg.trait.legged.splash", "emg.trait.legged.dust", "emg.trait.air.radius", "emg.trait.hull.amount" };
            bool tuned = tuning.All(t => GridContent.TryGetTuning(t, out _));
            Expect(missing.Count == 0 && tuned && Mathf.Approximately(rows[0].r.P0, 0.8f) && Mathf.Approximately(rows[0].r.P1, 0.15f) && Mathf.Approximately(rows[0].r.P2, 0.5f)
                   && Mathf.Approximately(rows[1].r.P0, 0.6f) && Mathf.Approximately(rows[2].r.P0, 1.5f),
                $"三条特性名称 / 说明文本键与读档原因键齐全（缺 {missing.Count}：{string.Join(",", missing)}）；参数取 fg.TbHomeTuning（震地 {rows[0].r.P0}/{rows[0].r.P1}/{rows[0].r.P2}、空投 {rows[1].r.P0}、取水 {rows[2].r.P0}）");
        }

        // ── B 测试底盘行 ─────────────────────────────────────────────────────

        private static void CheckTestRows()
        {
            Line("  · B. 测试底盘行（自检数据里把预留行改成正式行 = 将来落地只改这一行）：正式入口拿到机制与实体档案默认值，不改规则表 / 固件声明");
            try
            {
                UseTestRows();
                CombatConfig c = CombatSite.ConfigFromTuning();
                c.NavEnabled = 0;
                using var site = new CombatSite("chs_res_test", c);
                var sb = new StringBuilder();
                bool ok = EmgCatalog.Current.Problems.Count == 0;
                byte[] want = { EmgTraitConst.LeggedStomp, EmgTraitConst.AirDrop, EmgTraitConst.HullDraw };
                for (int n = 0; n < Reserved.Length; n++)
                {
                    int u = Machine(site.Kernel, new double2(n * 10, 0));
                    site.ApplyChassisTrait(u, Reserved[n]);
                    short prof = EmgEntityProfiles.ForChassis(Reserved[n]);
                    byte got = site.Kernel.TraitOf(u);
                    ok &= got == want[n] && prof >= 0 && EmgCatalog.TryGetActiveTrait(Reserved[n], out _);
                    sb.Append($"{Reserved[n]} → 机制 {got}、实体档案 {prof}；");
                }
                Expect(ok, $"{sb}编译问题 {EmgCatalog.Current.Problems.Count}（规则表、固件声明未改）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── C 震地 ──────────────────────────────────────────────────────────

        private static void CheckStomp()
        {
            Line("  · C. 震地：移动中每 0.8 秒（模拟时间）对脚下发 evt.impact；碎冰、溅出液体小斑块、扬起粉尘；停住不震；对照无特性");
            try
            {
                UseTestRows();
                (List<int> at, float dust) Walk(byte trait, bool move)
                {
                    using CombatKernel k = NewKernel();
                    int u = Machine(k, new double2(0, 0), trait, Listener(k, 0, EmgEventKind.Impact));
                    StepN(k, 1);
                    if (move)
                    {
                        Move(k, u, new double2(0, 20));
                    }
                    List<int> at = StompSteps(k, u, 180);
                    return (at, PatchesOf(k, EmgMatterConst.Dust).Sum(z => z.Amount));
                }
                var leg = Walk(EmgTraitConst.LeggedStomp, true);
                var still = Walk(EmgTraitConst.LeggedStomp, false);
                var bare = Walk(EmgTraitConst.None, true);
                bool gaps = leg.at.Count >= 3 && leg.at.Zip(leg.at.Skip(1), (a, b) => b - a).All(g => g == 48);
                Expect(gaps && leg.dust >= 0.49f,
                    $"多足行进 3 秒：震地 {leg.at.Count} 次，在第 {string.Join("/", leg.at)} 步（间隔 48 步 = 0.8 秒）；干地上扬起粉尘、现存合计 {leg.dust:0.##}（每次 0.5，气体层会扩散消散）");
                Expect(still.at.Count == 0 && still.dust == 0f && bare.at.Count == 0 && bare.dust == 0f,
                    $"负向：停着的多足 {still.at.Count} 次 / 粉尘 {still.dust:0.##}；对照无特性行进 {bare.at.Count} 次 / 粉尘 {bare.dust:0.##}");

                bool IceSurvives(byte trait)
                {
                    using CombatKernel k = NewKernel();
                    k.SpawnPatch(EmgMatterConst.Ice, new double2(0, 0), 2f, 3f, 60f);
                    StepN(k, 2);
                    int ice = PatchesOf(k, EmgMatterConst.Ice).Select(z => z.EmgId).FirstOrDefault();
                    int u = Machine(k, new double2(-1.8, 0), trait);
                    Move(k, u, new double2(6, 0));
                    StepN(k, 150);
                    return k.TryGetPatchEnergy(ice, out _, out _, out _);
                }
                bool legIce = IceSurvives(EmgTraitConst.LeggedStomp), bareIce = IceSurvives(EmgTraitConst.None);
                Expect(!legIce && bareIce, $"走过冰面：多足震地后冰还在 {legIce}（应碎）；对照无特性 {bareIce}");

                (int outside, float src, float dust) Splash(byte trait)
                {
                    using CombatKernel k = NewKernel();
                    k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 3f, 60f);
                    StepN(k, 2);
                    int u = Machine(k, new double2(-1.8, 0), trait);
                    Move(k, u, new double2(1.6, 0));
                    StepN(k, 60);
                    List<CombatZone> oils = PatchesOf(k, EmgMatterConst.Oil);
                    return (oils.Count(z => math.distance(z.Pos, double2.zero) > 2.3), oils.Where(z => math.length(z.Pos) < 0.5).Sum(z => z.Amount), PatchesOf(k, EmgMatterConst.Dust).Sum(z => z.Amount));
                }
                var oil = Splash(EmgTraitConst.LeggedStomp);
                var oilBare = Splash(EmgTraitConst.None);
                Expect(oil.outside >= 1 && oil.src < 3f - 0.3f && oil.dust == 0f && oilBare.outside == 0 && oilBare.src > 2.95f,
                    $"站在油里震地：油溅到斑块外 {oil.outside} 块、源斑块剩 {oil.src:0.##}/3、不扬尘（{oil.dust:0.##}）；对照无特性：外面 {oilBare.outside} 块、源 {oilBare.src:0.##}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── D 空投 ──────────────────────────────────────────────────────────

        private static CombatWeapon FieldWeapon(int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Cooldown = 0f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Field, Area = 2f, FieldSeconds = 3f, FieldDpsRatio = 1f },
        };

        private static void CheckAirDrop()
        {
            Line("  · D. 空投：布区落点不被途中障碍截停、落进围死的不可通行区照常生成、半径 × 0.6；物质排放半径 × 0.6；不沾地面层；固件读 chs.airborne 对悬浮与飞行同样生效");
            try
            {
                UseTestRows();
                (double2 at, float r, int n) Drop(byte trait)
                {
                    using CombatKernel k = NewKernel();
                    // 目标被一圈障碍围死（地面到不了的口袋），出手者在圈外
                    var ring = new List<float3>();
                    for (int q = 0; q < 10; q++)
                    {
                        float ang = 2f * Mathf.PI * q / 10f;
                        ring.Add(new float3(3f * Mathf.Cos(ang), 10f + 3f * Mathf.Sin(ang), 1.2f));
                    }
                    k.SetObstacles(ring);
                    int me = Machine(k, new double2(0, 0), trait, k.AddWeapon(FieldWeapon()));
                    int foe = Foe(k, new double2(0, 10), 5000f);
                    StepN(k, 1);
                    k.FireAt(me, foe, k.Time);
                    StepN(k, 1);
                    List<CombatZone> zs = Zones(k);
                    return zs.Count > 0 ? (zs[0].Pos, zs[0].Radius, zs.Count) : (new double2(double.NaN), 0f, 0);
                }
                var air = Drop(EmgTraitConst.AirDrop);
                var bare = Drop(EmgTraitConst.None);
                Expect(air.n == 1 && math.distance(air.at, new double2(0, 10)) < 0.05 && Mathf.Abs(air.r - 1.2f) < 0.01f && bare.n == 1 && bare.at.y < 7.0 && Mathf.Abs(bare.r - 2f) < 0.01f,
                    $"布区打障碍围死的目标：空投落区在 ({air.at.x:0.##}, {air.at.y:0.##})、半径 {air.r:0.##}（= 2 × 0.6，落进地面到不了的口袋照常生成）；对照无特性在 ({bare.at.x:0.##}, {bare.at.y:0.##}) 被障碍截停、半径 {bare.r:0.##}");

                (float r, int n) Emit(byte trait)
                {
                    EmgProgramResult p = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_water" }, FirmwareCarrier.Projectile);
                    using CombatKernel k = NewKernel();
                    int prog = CombatSite.RegisterProgram(k, p);
                    int me = Machine(k, new double2(0, 0), trait, k.AddWeapon(new CombatWeapon
                    {
                        Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Program = prog,
                        Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                    }));
                    int foe = Foe(k, new double2(0, 6), 5000f);
                    StepN(k, 1);
                    k.FireAt(me, foe, k.Time);
                    StepN(k, 3);
                    List<CombatZone> w = PatchesOf(k, EmgMatterConst.Water);
                    return (w.Count > 0 ? w[0].Radius : 0f, w.Count);
                }
                var eAir = Emit(EmgTraitConst.AirDrop);
                var eBare = Emit(EmgTraitConst.None);
                Expect(eAir.n == 1 && eBare.n == 1 && eBare.r > 0f && Mathf.Abs(eAir.r - eBare.r * 0.6f) < 0.01f,
                    $"水箱外排（真实编译的 fw_water）：空投排出的水斑块半径 {eAir.r:0.##} = 对照 {eBare.r:0.##} × 0.6");

                using (CombatKernel k = NewKernel())
                {
                    k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 3f, 60f);
                    k.SpawnPatch(EmgMatterConst.Fire, new double2(20, 0), 2f, 3f, 60f);
                    int air1 = Machine(k, new double2(0, 0), EmgTraitConst.AirDrop);
                    int ground = Machine(k, new double2(20, 0));
                    StepN(k, 120);
                    Expect(Hp(k, air1) >= 400f && Hp(k, ground) < 400f && (k.ChsOf(air1) & EmgTraitConst.ChsAirborne) != 0,
                        $"飞行站在明火里 2 秒：血 {Hp(k, air1):0}（不沾地面层，chs {k.ChsOf(air1)}）；着地对照 {Hp(k, ground):0}");
                }
                using (CombatKernel k = NewKernel())
                {
                    int wl = Listener(k, EmgConst.PortChsAirborne, EmgEventKind.Hurt);
                    int air1 = Machine(k, new double2(0, 0), EmgTraitConst.AirDrop, wl);
                    int hover = Machine(k, new double2(10, 0), EmgTraitConst.HoverFloat, wl);
                    int ground = Machine(k, new double2(20, 0), EmgTraitConst.None, wl);
                    StepN(k, 2);
                    long Wakes(int id)
                    {
                        long w0 = k.EmgBusStats.ListenWakes;
                        k.Damage(id, 1f, 0);
                        StepN(k, 2);
                        return k.EmgBusStats.ListenWakes - w0;
                    }
                    long a = Wakes(air1), h = Wakes(hover), g = Wakes(ground);
                    Expect(a >= 1 && h >= 1 && g == 0, $"FGT-EMG-050：固件读 chs.airborne（受伤时）——飞行唤起 {a}、悬浮 {h}、着地 {g}（同一读法，读不到底盘 ID）");
                }
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── E 取水 ──────────────────────────────────────────────────────────

        private static void CheckHullDraw()
        {
            Line("  · E. 取水：编译期把写 mat.water / mat.coolant 的指令热量记进 DrawHeat；在水面上出手不计这份热量、排水 × 1.5；离水立即失效；对照无特性");
            EmgProgramResult water = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_water" }, FirmwareCarrier.Projectile);
            EmgProgramResult oil = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_oilleak" }, FirmwareCarrier.Projectile);
            EmgProgramResult both = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_water", "fw_oilleak" }, FirmwareCarrier.Projectile);
            Expect(water.HeatDraw > 0f && water.HeatDraw <= water.HeatTotal + 1e-4f && oil.HeatDraw == 0f && oil.HeatTotal > 0f && Mathf.Abs(both.HeatDraw - water.HeatDraw) < 1e-4f,
                $"编译期：水箱外排 DrawHeat {water.HeatDraw:0.##}（总热 {water.HeatTotal:0.##}）；漏油 {oil.HeatDraw:0.##}（总热 {oil.HeatTotal:0.##}，不是水 / 冷却液）；两条同装只记水那条 {both.HeatDraw:0.##}（按写端端口，不按固件 ID）");
            try
            {
                UseTestRows();
                using CombatKernel k = NewKernel();
                int prog = CombatSite.RegisterProgram(k, water);
                const float heatPerShot = 10f, drawHeat = 4f;
                int wpn = k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Program = prog,
                    HeatPerShot = heatPerShot, DrawHeat = drawHeat, OverheatAt = 1000f, RecoverBelow = 1f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                });
                k.SetWaterForTests(new double2(0, 0), 4f);
                int hull = Machine(k, new double2(0, 0), EmgTraitConst.HullDraw, wpn);
                int plain = Machine(k, new double2(2, 0), EmgTraitConst.None, wpn);
                int fHull = Foe(k, new double2(-20, 10), 5000f);
                int fPlain = Foe(k, new double2(20, 10), 5000f);
                StepN(k, 1);
                (float heat, float amount) Shot(int me, int foe)
                {
                    float h0 = Heat(k, me);
                    k.FireAt(me, foe, k.Time);
                    float dh = Heat(k, me) - h0;
                    StepN(k, 3);
                    double2 fp = Pos(k, foe);
                    return (dh, PatchesOf(k, EmgMatterConst.Water).Where(z => math.distance(z.Pos, fp) < 2.0).Sum(z => z.Amount));
                }
                byte chsOn = k.ChsOf(hull);
                var on = Shot(hull, fHull);
                var ctl = Shot(plain, fPlain);
                Expect((chsOn & EmgTraitConst.ChsWater) != 0 && Mathf.Abs(on.heat - (heatPerShot - drawHeat)) < 1e-3f && Mathf.Abs(ctl.heat - heatPerShot) < 1e-3f
                       && ctl.amount > 0f && Mathf.Abs(on.amount - ctl.amount * 1.5f) < 1e-3f,
                    $"在测试水域里（chs {chsOn}，含水面位）：取水单位一发积热 {on.heat:0.##}（= 10 − 4）、排水 {on.amount:0.##}；对照无特性积热 {ctl.heat:0.##}、排水 {ctl.amount:0.##}（取水 = 对照 × 1.5）");
                Move(k, hull, new double2(0, 8));
                StepN(k, 150);
                byte chsOff = k.ChsOf(hull);
                double2 fp2 = Pos(k, fHull);
                float before = PatchesOf(k, EmgMatterConst.Water).Where(z => math.distance(z.Pos, fp2) < 2.0).Sum(z => z.Amount);
                var off = Shot(hull, fHull);
                Expect((chsOff & EmgTraitConst.ChsWater) == 0 && Pos(k, hull).y > 7 && Mathf.Abs(off.heat - heatPerShot) < 1e-3f && Mathf.Abs(off.amount - before - ctl.amount) < 1e-3f,
                    $"负向：驶出水域（y = {Pos(k, hull).y:0.#}，chs {chsOff}）后再打：积热 {off.heat:0.##}（全额）、新排水 {off.amount - before:0.##}（= 对照，加成立即失效）");
                k.SetWaterForTests(double2.zero, 0f);
                Expect(k.ChsOf(plain) == EmgTraitConst.ChsGround, "清掉测试水域后 chs.water 回到 0（正式路径不设测试水域）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── F 存读档 ────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · F. 存读档：震地中途存读档节拍连续、哈希一致；DrawHeat 往返（17 版短段取 0）；测试底盘行混进正式存档 → 内核拒绝（ReservedTrait）、正式读档报错并按记录重建");
            byte[] legSnap = null;
            int legUnit = 0;
            try
            {
                UseTestRows();
                using CombatKernel a = NewKernel();
                legUnit = Machine(a, new double2(0, 0), EmgTraitConst.LeggedStomp, Listener(a, 0, EmgEventKind.Impact));
                StepN(a, 1);
                Move(a, legUnit, new double2(0, 30));
                StepN(a, 30); // 存在两次震地之间（已走 30 / 48 步）
                legSnap = a.Serialize();
                using CombatKernel b = NewKernel();
                CombatLoadResult r = b.Load(legSnap);
                List<int> sa = StompSteps(a, legUnit, 150), sb = StompSteps(b, legUnit, 150);
                Expect(r == CombatLoadResult.Ok && sa.Count >= 3 && sa.SequenceEqual(sb) && sa[0] < 48 && a.StateHash() == b.StateHash(),
                    $"测试数据下读档 {r}：续跑震地在第 {string.Join("/", sb)} 步（不存档 {string.Join("/", sa)}；首次 < 48 = 接着存档前的计时，节拍连续），哈希一致 {a.StateHash() == b.StateHash()}");

                using CombatKernel w = NewKernel();
                int wi = w.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 5f, Damage = 1f, HeatPerShot = 6f, DrawHeat = 2.5f });
                Machine(w, new double2(0, 0), EmgTraitConst.HullDraw, wi);
                StepN(w, 2);
                using CombatKernel w2 = NewKernel();
                using CombatKernel w17 = NewKernel();
                CombatLoadResult r2 = w2.Load(w.Serialize()), r17 = w17.Load(w.SerializeFormatForTests(17));
                w2.TryGetWeapon(wi, out CombatWeapon cw2);
                w17.TryGetWeapon(wi, out CombatWeapon cw17);
                Expect(r2 == CombatLoadResult.Ok && Mathf.Approximately(cw2.DrawHeat, 2.5f) && r17 == CombatLoadResult.Ok && cw17.DrawHeat == 0f && Mathf.Approximately(cw17.HeatPerShot, 6f),
                    $"武器 DrawHeat 往返 {r2}：{cw2.DrawHeat:0.##}（原 2.5）；读 17 版快照 {r17}：DrawHeat {cw17.DrawHeat:0.##}（短段取 0）、HeatPerShot {cw17.HeatPerShot:0.##}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }

            // 正式数据：测试底盘行混进存档
            if (legSnap == null)
            {
                Fail("测试数据快照没生成，跳过负向读档");
                return;
            }
            using (CombatKernel prod = NewKernel())
            {
                CombatLoadResult r = prod.Load(legSnap);
                Expect(r == CombatLoadResult.ReservedTrait && !prod.TryGetUnit(legUnit, out _),
                    $"正式数据的内核读带多足测试行的快照：{r}（拒绝，内核保持空：单位在 {prod.TryGetUnit(legUnit, out _)}）");
            }
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            using (var site = new CombatSite("chs_res_load", c))
            {
                var rec = new CombatSiteRecord { SiteId = "chs_res_load", FormatVersion = CombatConst.FormatVersion, Payload = Convert.ToBase64String(legSnap), Keys = Array.Empty<string>() };
                bool ok = site.TryRestore(null, rec, out string reason);
                Expect(!ok && reason == "combat.load.reason.reserved_trait" && GameText.Get(reason) != reason && !site.Kernel.TryGetUnit(legUnit, out _),
                    $"正式读档入口 CombatSite.TryRestore：{ok}、原因 {reason}（“{GameText.Get(reason)}”，并记错误日志；调用方按记录重建并通知玩家）");
            }
            using (CombatKernel a = NewKernel())
            {
                int t = Machine(a, new double2(0, 0), EmgTraitConst.TrackDrag);
                StepN(a, 3);
                using CombatKernel b = NewKernel();
                Expect(b.Load(a.Serialize()) == CombatLoadResult.Ok && b.TraitOf(t) == EmgTraitConst.TrackDrag, "对照：带正式特性（碾压拖带）的快照照常读入");
            }
        }

        // ── G 暂停与倍速 ────────────────────────────────────────────────────

        private static void CheckTiming()
        {
            Line("  · G. 暂停与 0.5x～3x：震地按模拟时间（倍速只改每帧步数），暂停不走；同一模拟时间震地次数与哈希逐字一致");
            try
            {
                UseTestRows();
                (ulong hash, long wakes) RunAt(int stepsPerFrame, int pauseAt)
                {
                    using CombatKernel k = NewKernel();
                    int u = Machine(k, new double2(0, 0), EmgTraitConst.LeggedStomp, Listener(k, 0, EmgEventKind.Impact));
                    Move(k, u, new double2(0, 40));
                    int stepped = 0;
                    for (int frame = 0; stepped < 300; frame++)
                    {
                        if (pauseAt >= 0 && frame >= pauseAt && frame < pauseAt + 100)
                        {
                            continue;
                        }
                        StepN(k, stepsPerFrame);
                        stepped += stepsPerFrame;
                    }
                    return (k.StateHash(), k.EmgBusStats.ListenWakes);
                }
                var x1 = RunAt(2, -1);
                var x05 = RunAt(1, -1);
                var x3 = RunAt(6, -1);
                var xp = RunAt(2, 40);
                Expect(x1.hash == x05.hash && x1.hash == x3.hash && x1.hash == xp.hash && x1.wakes == 6 && x05.wakes == 6 && x3.wakes == 6 && xp.wakes == 6,
                    $"5 秒模拟时间：0.5x / 1x / 3x / 中途暂停 100 帧 震地 {x05.wakes}/{x1.wakes}/{x3.wakes}/{xp.wakes} 次（应 6）、哈希一致 {x05.hash == x1.hash}/{x3.hash == x1.hash}/{xp.hash == x1.hash}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── H FG-GAP-197 ──────────────────────────────────────────────────────

        private static void CheckMachineTypeProfile()
        {
            Line("  · H. FG-GAP-197：正式入口 SpawnMachine 按机型 ID（erc_001 / 003）也拿到底盘的实体档案默认值（新底盘落地加 fg.TbEntityProfile 默认值即生效）");
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            using var site = new CombatSite("chs_res_profile", c);
            var cases = new (string type, string chassis)[]
            {
                (GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId, ChassisCatalog.ChassisWheelId),
                (GameLogic.Campaign.Regions.HomeValleyLayout.Erc003ChassisId, ChassisCatalog.ChassisTrackId),
            };
            var sb = new StringBuilder();
            bool ok = true;
            for (int n = 0; n < cases.Length; n++)
            {
                var rec = new MachineRecord { LogicId = 9201 + n, DisplayNumber = 9201 + n, ChassisId = cases[n].type, IsAlive = true, Health = 100f, MaxHealth = 100f };
                short want = EmgEntityProfiles.ForChassis(cases[n].chassis);
                int unit = site.SpawnMachine(null, rec, new Vector2(n * 10f, 0f), false) != null && site.TryGetMachineUnit(rec.LogicId, out int u) ? u : 0;
                short got = unit != 0 && site.Kernel.TryGetUnitEmg(unit, out short p, out _) ? p : (short)-9;
                ok &= want >= 0 && got == want && EmgEntityProfiles.ForChassis(cases[n].type) == want;
                sb.Append($"{cases[n].type} → 档案 {got}（{cases[n].chassis} = {want}）；");
            }
            Expect(ok, $"SpawnMachine 生成的机器：{sb}");
        }

        // ── I 耗时 ──────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · I. 耗时（Editor batchmode，只测一次；真机另测 FG15-SYS-02）：没有预留特性单位时零新增遍历；20 台多足行进 + 120 个敌人的内核每步");
            try
            {
                UseTestRows();
                double Measure(int legged)
                {
                    using CombatKernel k = NewKernel(256);
                    for (int i = 0; i < 120; i++)
                    {
                        Foe(k, new double2(60 + i % 12 * 2, i / 12 * 2));
                    }
                    for (int i = 0; i < 20; i++)
                    {
                        int m = Machine(k, new double2(i * 4, 0), i < legged ? EmgTraitConst.LeggedStomp : EmgTraitConst.None);
                        Move(k, m, new double2(i * 4, 40));
                    }
                    StepN(k, 30);
                    var sw = Stopwatch.StartNew();
                    StepN(k, 600);
                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds / 600.0;
                }
                double off = Measure(0), on = Measure(20);
                Line($"    环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，{SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}");
                Line($"    内核每步：无多足 {off:0.###} ms；20 台多足行进 {on:0.###} ms，差 {on - off:0.###} ms");
                Expect(on - off < 0.1, $"震地开销 {on - off:0.###} ms / 步（门限 0.1 ms，20 台；没有多足时 StepChassis 一条长度比较、chs.water 一条比较）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
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
