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
    /// EMG3-CHS-01 四种现行底盘特性与底盘状态端口（FG19 FGR-EMG-140 / 141 / 211；FGT-EMG-050）的自动验收。全部起真实内核（CombatSite.ApplyContent 同一套表写入）：
    /// A 表 / 枚举 / 阶段 3 与未接通端口；B 碾压拖带（油 / 明火拖成线、停住不拖、碾冰注入动能）；C 拖挂（停留 1 秒带走、同时最多 1 块、到期 / 驶入另一块落下、份量守恒）；
    /// D 离地（悬浮与云端空中单位同一读法：不吃地面层、进烟照常；固件读 chs.airborne 对两者同样生效）；E 锚定与冲撞（DEBT-EMG1ENT01-06 发 evt.push / evt.impact）；
    /// F 冲刺（FG-GAP-176）；G 绝缘闪络跳哨戒桩（DEBT-EMG2REACT02-04）；H 快照 18 往返 / 17 版缺省 / 随车斑块不复制；I 暂停与倍速；J 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgChassisSelfCheck）。
    /// </summary>
    public static class FgEmgChassisSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const byte Sense = 3;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/底盘特性与底盘状态端口")]
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
            Line("\n[底盘特性] EMG3-CHS-01：碾压拖带 / 拖挂 / 离地 / 锚定、chs.* 状态端口、冲刺与冲撞、哨戒桩导体、快照 18（FGT-EMG-050）");
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
                Step(CheckTables);
                Step(CheckProductionEntry);
                Step(CheckTrackDrag);
                Step(CheckWheelTow);
                Step(CheckAirborne);
                Step(CheckAnchorAndRam);
                Step(CheckDash);
                Step(CheckStakeConductor);
                Step(CheckSaveLoad);
                Step(CheckTiming);
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
            Line($"  · [底盘特性] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

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

        private static int Foe(CombatKernel k, double2 p, float hp = 400f, CombatBehavior beh = CombatBehavior.None, int weapon = -1, int profile = -1) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = beh, Flags = Live | CombatUnitFlags.Report, Position = p, Home = p,
                Radius = 0.5f, Speed = 2f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = profile, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
            });

        /// <summary>只监听若干事件的程序（读端按序号 0、1……，角色 sense）+ 可选的 chs.* condition（与第 0 条同序号）。</summary>
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

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : new double2(double.NaN);

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static void Move(CombatKernel k, int id, double2 to) => k.IssueCommand(id, CombatCommandKind.Move, to, 0, 0.2f, 0f, 0f, false);

        /// <summary>某物质全部斑块的份量和（<paramref name="where"/> = 只算满足条件的位置）与块数。</summary>
        private static (float sum, int n) Matter(CombatKernel k, byte matter, Func<double2, bool> where = null)
        {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && (where == null || where(z.Pos)))
                {
                    sum += z.Amount;
                    n++;
                }
            }
            return (sum, n);
        }

        private static int PatchIdAt(CombatKernel k, byte matter, double2 at)
        {
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && math.distance(z.Pos, at) <= z.Radius)
                {
                    return z.EmgId;
                }
            }
            return 0;
        }

        private static float Carried(CombatKernel k, int id) => k.TryGetTraitRun(id, out EmgTraitRun r) && r.TowMatter != 0 ? r.TowAmount : 0f;

        // ── A 表 / 枚举 / 阶段 ─────────────────────────────────────────────────

        private static void CheckTables()
        {
            Line("  · A. 表驱动：内核机制与热更枚举同值；4 条现行生效、3 条预留不生效；阶段 3 已开、未接通端口的 owner 仍走旧轨；文本键齐全");
            bool same = (byte)EmgTraitKind.TrackDrag == EmgTraitConst.TrackDrag && (byte)EmgTraitKind.WheelTow == EmgTraitConst.WheelTow && (byte)EmgTraitKind.HoverFloat == EmgTraitConst.HoverFloat
                        && (byte)EmgTraitKind.FixedAnchor == EmgTraitConst.FixedAnchor && (byte)EmgTraitKind.LeggedStomp == EmgTraitConst.LeggedStomp && (byte)EmgTraitKind.AirDrop == EmgTraitConst.AirDrop
                        && (byte)EmgTraitKind.HullDraw == EmgTraitConst.HullDraw;
            Expect(same, "EmgTraitKind（热更）与 EmgTraitConst（内核）逐个同值");
            Expect(EmgCatalog.ImplementedStage >= 3 && EmgCatalog.EffectiveStage >= 3 && CombatConst.FormatVersion >= 18, // EMG4-HIJ-01：阶段 4 起 ≥ 3
                $"阶段 3：已实现 {EmgCatalog.ImplementedStage}、生效 {EmgCatalog.EffectiveStage}（emg.stage 缺省 ≥ 3）；快照格式 {CombatConst.FormatVersion}");
            EmgCatalog.TryGetTrait("chassis_track", out EmgTraitRow tr);
            var active = new List<string>();
            var reserved = new List<string>();
            foreach (KeyValuePair<string, int> kv in EmgCatalog.Current.TraitByChassis)
            {
                (EmgCatalog.TryGetActiveTrait(kv.Key, out _) ? active : reserved).Add(kv.Key + "→" + EmgCatalog.Current.Traits[kv.Value].Kind);
            }
            Expect(active.Count == 4 && reserved.Count == 3 && EmgCatalog.TryGetActiveTrait("chassis_track", out _) && tr.Kind == EmgTraitKind.TrackDrag
                   && Mathf.Approximately(tr.P0, 1.5f) && Mathf.Approximately(tr.P1, 0.1f),
                $"生效 {string.Join("、", active)}；预留 {string.Join("、", reserved)}（多足 / 飞行 / 船体归 EMG3-CHS-02）；履带参数取 emg.trait.track.*");
            // EMG3-TMP-01：节拍（evt.beat / tmp.*）已由内核执行者接通，fw_clock / fw_resonance 切到声明轨（口径变化，登记 M10-BASELINE 差异表）；
            // EMG3-SQD-01：协同（sqd.relay / evt.ping）已由内核跨机协同执行者接通，struct_relay 切到声明轨（口径变化：原断言“仍走旧轨”换成等价的“已接通”，L5）
            Expect(EmgCatalog.IsOwnerOnDeclTrack("fw_clock") && EmgCatalog.IsOwnerOnDeclTrack("fw_resonance") && EmgCatalog.IsOwnerOnDeclTrack("struct_relay")
                   && !EmgCatalog.IsPendingPort("sqd.relay", EmgRole.Scope) && EmgCatalog.IsOwnerOnDeclTrack("chassis_track") && EmgCatalog.IsOwnerOnDeclTrack("fw_burntrail"),
                "阶段 3：节拍已接通（fw_clock / fw_resonance 在声明轨）；协同已接通（struct_relay 在声明轨，EMG3-SQD-01）；底盘声明与阶段 1 固件照常在声明轨");
            var missing = new List<string>();
            foreach (EmgTraitRow t in EmgCatalog.Current.Traits)
            {
                foreach (string key in new[] { t.NameKey, t.DescKey })
                {
                    if (string.IsNullOrEmpty(key) || GameText.Get(key) == key)
                    {
                        missing.Add(key);
                    }
                }
            }
            foreach (string key in new[] { "emg.trait.option", "emg.trait.tip", "emg.trait.tip_off", "emg.trait.off_stage", "emg.trait.off_reserved", "emg.trait.tow" })
            {
                if (GameText.Get(key) == key)
                {
                    missing.Add(key);
                }
            }
            EmgCatalog.TryGetTrait("chassis_legged", out EmgTraitRow legged);
            string tip = EmgCatalog.TraitTip(tr), off = EmgCatalog.TraitTip(legged);
            Expect(missing.Count == 0 && tip.Contains(GameText.Get(tr.NameKey)) && off.Contains(GameText.Get("emg.trait.off_reserved"))
                   && EmgCatalog.TraitOption("chassis_track", "履带").Contains(GameText.Get(tr.NameKey)) && EmgCatalog.TraitOption("comp_gun", "枪") == "枪",
                $"特性名称 / 说明文本键齐全（缺 {missing.Count}）；蓝图编辑器选项与悬停文字：“{tip}”／预留：“{off}”");
        }

        /// <summary>审查 P0：正式入口 CombatSite.SpawnMachine 用机器记录的机型 ID（erc_001 / 002 / 003，不是底盘内容 ID）也要拿到特性，悬停写特性行。</summary>
        private static void CheckProductionEntry()
        {
            Line("  · A2. 正式入口：CombatSite.SpawnMachine 按机器记录的机型 ID（erc_001 / 002 / 003）查到特性；机器悬停显示特性行");
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            using var site = new CombatSite("chs_entry", c);
            var cases = new (string chassis, byte want)[]
            {
                (GameLogic.Campaign.Regions.HomeValleyLayout.Erc003ChassisId, EmgTraitConst.TrackDrag),
                (GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId, EmgTraitConst.WheelTow),
                (GameLogic.Campaign.Regions.HomeValleyLayout.Erc002ChassisId, EmgTraitConst.WheelTow),
                (ChassisCatalog.ChassisHoverId, EmgTraitConst.HoverFloat),
            };
            var sb = new StringBuilder();
            bool ok = true;
            for (int n = 0; n < cases.Length; n++)
            {
                var rec = new GameLogic.Campaign.MachineRecord { LogicId = 9101 + n, DisplayNumber = 9101 + n, ChassisId = cases[n].chassis, IsAlive = true, Health = 100f, MaxHealth = 100f };
                int unit = site.SpawnMachine(null, rec, new Vector2(n * 10f, 0f), false) != null && site.TryGetMachineUnit(rec.LogicId, out int u) ? u : 0;
                byte got = unit != 0 ? site.Kernel.TraitOf(unit) : EmgTraitConst.None;
                string hover = unit != 0 ? EmgChainView.TraitHoverLine(site, unit) : null;
                ok &= got == cases[n].want && !string.IsNullOrEmpty(hover);
                sb.Append($"{cases[n].chassis} → {got}（悬停“{hover?.Replace("\n", " / ")}”）；");
            }
            Expect(ok, $"SpawnMachine 生成的机器：{sb}");
        }

        // ── B 碾压拖带 ──────────────────────────────────────────────────────

        private static void CheckTrackDrag()
        {
            Line("  · B. 碾压拖带：移动中经过油 / 明火每 1.5 米在身后分出一块同物质小斑块（连成线）；停住不拖；碾过冰注入动能；对照无特性不拖");
            (int line, float src, float total) Run(byte matter, byte trait, bool move)
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(matter, new double2(0, 0), 2f, 3f, 60f);
                int m = Machine(k, new double2(-1, 0), trait);
                StepN(k, 2);
                float before = Matter(k, matter).sum;
                if (move)
                {
                    Move(k, m, new double2(14, 0));
                }
                StepN(k, 210);
                (float sum, int n) outside = Matter(k, matter, p => p.x > 2.3);
                return (outside.n, Matter(k, matter, p => math.length(p) < 0.5).sum, before);
            }
            var oil = Run(EmgMatterConst.Oil, EmgTraitConst.TrackDrag, true);
            var bare = Run(EmgMatterConst.Oil, EmgTraitConst.None, true);
            var still = Run(EmgMatterConst.Oil, EmgTraitConst.TrackDrag, false);
            Expect(oil.line >= 3 && oil.src < 3f - 0.2f && bare.line == 0 && bare.src > 2.95f,
                $"履带驶过油：身后 {oil.line} 块油（应 ≥ 3，点线），源斑块剩 {oil.src:0.##}/3；对照无特性：身后 {bare.line} 块、源 {bare.src:0.##}");
            Expect(still.line == 0 && still.src > 2.95f, $"负向：履带车停在油里不拖（身后 {still.line} 块，源 {still.src:0.##}）");
            var fire = Run(EmgMatterConst.Fire, EmgTraitConst.TrackDrag, true);
            var fireBare = Run(EmgMatterConst.Fire, EmgTraitConst.None, true);
            Expect(fire.line >= 2 && fireBare.line == 0, $"燃烧区被拖成火线：身后明火 {fire.line} 块（对照 {fireBare.line}）");
            float Kin(byte trait)
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Ice, new double2(0, 0), 2f, 3f, 60f);
                StepN(k, 2);
                int ice = PatchIdAt(k, EmgMatterConst.Ice, new double2(0, 0));
                int m = Machine(k, new double2(-3, 0), trait);
                Move(k, m, new double2(4, 0));
                float max = 0f;
                for (int s = 0; s < 150; s++)
                {
                    StepN(k, 1);
                    if (k.TryGetPatchEnergy(ice, out _, out _, out float kin))
                    {
                        max = Mathf.Max(max, kin);
                    }
                    else
                    {
                        return float.PositiveInfinity; // 已碎
                    }
                }
                return max;
            }
            float kTrack = Kin(EmgTraitConst.TrackDrag), kBare = Kin(EmgTraitConst.None);
            Expect(kTrack > 0f && kBare <= 0f, $"碾过冰：履带注入动能 {kTrack:0.##}（∞ = 已碎），无特性 {kBare:0.##}");
        }

        // ── C 拖挂 ──────────────────────────────────────────────────────────

        private static void CheckWheelTow()
        {
            Line("  · C. 拖挂：在油里停留 ≥ 1 秒带走 0.3 份装在车上；车上有一块时不带第二块；12 秒后落下 / 驶入另一块斑块落下；份量守恒");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 3f, 120f);
                int w = Machine(k, new double2(0, 0), EmgTraitConst.WheelTow);
                StepN(k, 50); // 0.83 秒
                float early = Carried(k, w);
                StepN(k, 20); // 1.17 秒
                float got = Carried(k, w), src = Matter(k, EmgMatterConst.Oil).sum;
                Expect(early == 0f && got > 0.85f && got < 0.95f && Mathf.Abs(src + got - 3f) < 0.01f,
                    $"停留 0.83 秒还没带（{early:0.##}）；1.17 秒带走 {got:0.##}（0.3 × 3），地上剩 {src:0.##}，合计守恒");
                StepN(k, 180);
                Expect(Mathf.Abs(Carried(k, w) - got) < 1e-4f && Mathf.Abs(Matter(k, EmgMatterConst.Oil).sum - src) < 0.01f,
                    $"负向：车上已有一块，继续停在油里 3 秒不带第二块（车上 {Carried(k, w):0.##}，地上 {Matter(k, EmgMatterConst.Oil).sum:0.##}）");
                k.TryGetTraitRun(w, out EmgTraitRun run0);
                Move(k, w, new double2(20, 0));
                int steps = 0;
                while (Carried(k, w) > 0f && steps < 60 * 15)
                {
                    StepN(k, 1);
                    steps++;
                }
                float held = 12f - run0.TowT + steps / 60f; // 随车总时长 = 出发前已随车的秒数 + 出发后到落下的秒数
                (float sum, int n) dropped = Matter(k, EmgMatterConst.Oil, p => p.x > 15);
                Expect(Carried(k, w) == 0f && held > 11.9f && held < 12.1f && dropped.n == 1 && Mathf.Abs(dropped.sum - got) < 0.01f && Mathf.Abs(Matter(k, EmgMatterConst.Oil).sum - 3f) < 0.02f,
                    $"随车约 {held:0.#} 秒后落在车所在处：落下 {dropped.n} 块 {dropped.sum:0.##} 份；落下那一刻全场油合计 {Matter(k, EmgMatterConst.Oil).sum:0.##}（守恒）");
            }
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 3f, 120f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(8, 0), 2f, 3f, 120f);
                int w = Machine(k, new double2(0, 0), EmgTraitConst.WheelTow);
                StepN(k, 75);
                float got = Carried(k, w);
                Move(k, w, new double2(8, 0));
                StepN(k, 150);
                Expect(got > 0f && Carried(k, w) == 0f && Matter(k, EmgMatterConst.Oil, p => p.x > 5).n >= 1,
                    $"驶入另一块斑块（水）当场落下：车上 {Carried(k, w):0.##}，水里多了 {Matter(k, EmgMatterConst.Oil, p => p.x > 5).sum:0.##} 份油");
            }
        }

        // ── D 离地 ──────────────────────────────────────────────────────────

        private static void CheckAirborne()
        {
            Line("  · D. 离地：悬浮不受地面层伤害 / 沾染 / evt.enter，可跨燃烧区；进烟照常；云端空中单位同一读法；固件读 chs.airborne 对两者同样生效");
            using (CombatKernel k = NewKernel())
            {
                k.SpawnPatch(EmgMatterConst.Fire, new double2(0, 0), 2f, 3f, 60f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(20, 0), 2f, 3f, 60f);
                k.SpawnPatch(EmgMatterConst.Fire, new double2(40, 0), 2f, 3f, 60f);
                int hover = Machine(k, new double2(0, 0), EmgTraitConst.HoverFloat);
                int ground = Machine(k, new double2(20, 0));
                int air = Foe(k, new double2(40, 0), 400f, CombatBehavior.None, -1, AirProfile(k));
                StepN(k, 120);
                k.TryGetUnitEmg(hover, out _, out EmgUnitState hs);
                k.TryGetUnitEmg(ground, out _, out EmgUnitState gs);
                uint fireBit = 1u << EmgMatterConst.Fire;
                Expect(Hp(k, hover) >= 400f && Hp(k, air) >= 400f && Hp(k, ground) < 400f && (hs.PrevMatterMask & fireBit) == 0u && (gs.PrevMatterMask & fireBit) != 0u
                       && (k.ChsOf(hover) & EmgTraitConst.ChsAirborne) != 0 && (k.ChsOf(air) & EmgTraitConst.ChsAirborne) != 0 && (k.ChsOf(ground) & EmgTraitConst.ChsGround) != 0,
                    $"站在明火里 2 秒：悬浮 {Hp(k, hover):0}、云端空中 {Hp(k, air):0} 不掉血、不记物质；着地对照 {Hp(k, ground):0}（chs：悬浮 {k.ChsOf(hover)}、空中 {k.ChsOf(air)}、着地 {k.ChsOf(ground)}）");
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 0), 3f, 3f, 60f);
                StepN(k, 30);
                k.TryGetUnitEmg(hover, out _, out hs);
                Expect((hs.PrevMatterMask & (1u << EmgMatterConst.Smoke)) != 0u, "负向：悬浮进烟（气体层）照常记物质（失锁等气体层效果照旧）");
                Move(k, hover, new double2(0, 8));
                StepN(k, 10);
                byte chsMoving = k.ChsOf(hover), chsStill = k.ChsOf(ground);
                Expect((chsMoving & EmgTraitConst.ChsMoving) != 0 && (chsStill & EmgTraitConst.ChsMoving) == 0 && ((chsMoving | chsStill) & EmgTraitConst.ChsWater) == 0,
                    $"chs.moving 只在本步有位移时为真（行进中的悬浮 {chsMoving}、停着的着地车 {chsStill}）；chs.water 在水面地形落地前恒 0");
                StepN(k, 140);
                Expect(Pos(k, hover).y > 7f && Hp(k, hover) >= 400f, $"悬浮跨过燃烧区不受伤（到 y = {Pos(k, hover).y:0.#}、血 {Hp(k, hover):0}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int wHover = Listener(k, EmgConst.PortChsAirborne, EmgEventKind.Hurt);
                int hover = Machine(k, new double2(0, 0), EmgTraitConst.HoverFloat, wHover);
                int ground = Machine(k, new double2(10, 0), EmgTraitConst.None, wHover);
                int air = Foe(k, new double2(20, 0), 400f, CombatBehavior.None, wHover, AirProfile(k));
                StepN(k, 2);
                long Wakes(int id)
                {
                    long w0 = k.EmgBusStats.ListenWakes;
                    k.Damage(id, 1f, 0);
                    StepN(k, 2);
                    return k.EmgBusStats.ListenWakes - w0;
                }
                long h = Wakes(hover), g = Wakes(ground), a = Wakes(air);
                Expect(h >= 1 && a >= 1 && g == 0, $"固件读 chs.airborne condition（受伤时）：悬浮唤起 {h}、云端空中 {a}、着地 {g}（读不到底盘 ID，两种离地同一读法）");
            }
            using (CombatKernel k = NewKernel())
            {
                // 审查 P1：带电水洼（E14）放电按位置圈判（EmgUnitsInCircle），离地不积电荷、不挨电弧
                var spots = new[] { new double2(0, 0), new double2(20, 0), new double2(40, 0) };
                foreach (double2 p in spots)
                {
                    k.SpawnPatch(EmgMatterConst.Water, p, 2f, 5f, 30f);
                }
                int hover = Foe(k, spots[0]);
                k.SetUnitTrait(hover, EmgTraitConst.HoverFloat);
                int air = Foe(k, spots[1], 400f, CombatBehavior.None, -1, AirProfile(k));
                int ground = Foe(k, spots[2]);
                StepN(k, 20);
                foreach (double2 p in spots)
                {
                    k.InjectPatchEnergy(p, 0.5f, EmgConst.PortElec, 1f, 3);
                }
                bool Got(int id) => k.AccumOf(id, EmgPhaseConst.Charge) > 0f || k.InPhase(id, EmgPhaseConst.Charge);
                bool gGot = false, hGot = false, aGot = false;
                for (int s = 0; s < 30; s++)
                {
                    StepN(k, 1);
                    gGot |= Got(ground);
                    hGot |= Got(hover);
                    aGot |= Got(air);
                }
                Expect(gGot && !hGot && !aGot && Hp(k, hover) >= 400f && Hp(k, air) >= 400f,
                    $"三车同站带电水洼：着地得电 {gGot}（血 {Hp(k, ground):0}）；悬浮得电 {hGot}、血 {Hp(k, hover):0}；云端空中得电 {aGot}、血 {Hp(k, air):0}");
            }
            using (CombatKernel k = NewKernel())
            {
                // 审查 P1：离地（chs.airborne）不受地面固态斑块阻挡（ADR-EMG-001 固态斑块条）；着地对照绕行 / 被挡在圈外
                k.SpawnPatch(EmgMatterConst.Cured, new double2(0, 0), 1.5f);
                k.SpawnPatch(EmgMatterConst.Cured, new double2(20, 0), 1.5f);
                int hover = Machine(k, new double2(0, -5), EmgTraitConst.HoverFloat);
                int ground = Machine(k, new double2(20, -5));
                StepN(k, 2);
                Move(k, hover, new double2(0, 5));
                Move(k, ground, new double2(20, 5));
                double hMin = 99, gMin = 99;
                for (int s = 0; s < 240; s++)
                {
                    StepN(k, 1);
                    hMin = math.min(hMin, math.distance(Pos(k, hover), new double2(0, 0)));
                    gMin = math.min(gMin, math.distance(Pos(k, ground), new double2(20, 0)));
                }
                Expect(hMin < 0.5 && gMin > 1.5, $"固化胶块：悬浮离块心最近 {hMin:0.##} 米（穿过）、着地对照最近 {gMin:0.##} 米（块半径 1.5，进不去）");
            }
            using (CombatKernel k = NewKernel())
            {
                // 待用户复核的决策 1（FG-GAP-196）：“谁能被打到”仍读云端空中单位，悬浮不因离地免疫只打地面的格斗；云端空中单位照旧打不到
                CombatWeapon mw = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 2.5f, Damage = 5f };
                mw.Reading.Carrier = CombatCarrier.Melee;
                mw.Reading.Area = 2.5f;
                mw.Reading.Cone = 180f;
                mw.Reading.Approach = 2.5f;
                int melee = k.AddWeapon(mw);
                int hover = Machine(k, new double2(0, 0), EmgTraitConst.HoverFloat);
                int brute = Foe(k, new double2(0, 1.2), 400f, CombatBehavior.None, melee);
                int air = Foe(k, new double2(20, 0), 400f, CombatBehavior.None, -1, AirProfile(k));
                int fighter = Machine(k, new double2(20, -1.2), EmgTraitConst.None, melee);
                StepN(k, 1);
                CombatFireResult onHover = k.FireAt(brute, hover, k.Time), onAir = k.FireAt(fighter, air, k.Time);
                Expect(onHover == CombatFireResult.Ok && onAir == CombatFireResult.CannotHitAir && (k.ChsOf(hover) & EmgTraitConst.ChsAirborne) != 0,
                    $"格斗打悬浮 {onHover}（chs.airborne 为真但仍可被打到）；格斗打云端空中 {onAir}");
            }
        }

        /// <summary>云端空中单位：行为参数 Airborne &gt; 0 的敌人（生成时内核写 Clarity.Air，与正式云端空中单位同一入口）。</summary>
        private static int AirProfile(CombatKernel k) => k.AddProfile(new CombatBehaviorProfile { Airborne = 1f });

        // ── E 锚定与冲撞 ──────────────────────────────────────────────────────

        private static (double moved, bool push, bool impact) RamOnce(byte trait)
        {
            using CombatKernel k = NewKernel();
            int listen = Listener(k, 0, EmgEventKind.Push, EmgEventKind.Impact);
            int me = Machine(k, new double2(0, 0), trait, listen, 1000f);
            Machine(k, new double2(0, -2.6), EmgTraitConst.None, -1, 1000f); // 背后 2.6 米挡一台：击退 3 米途中撞上它 → evt.push + evt.impact
            int ram = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 0.5f, Damage = 4f });
            int prof = k.AddProfile(new CombatBehaviorProfile { SenseRange = 20f, Speed = 2f, TurnRate = 360f, ChargeSpeed = 12f, ChargeWarn = 0.8f, ChargeSeconds = 1.5f, ChargeCooldown = 3f, ChargeKnock = 3f });
            Foe(k, new double2(0, 6), 1000f, CombatBehavior.Charger, ram, prof);
            double2 p0 = Pos(k, me);
            bool push = false, impact = false;
            for (int s = 0; s < 360; s++)
            {
                StepN(k, 1);
                push |= k.TryGetLastEmgEvent(EmgEventKind.Push, out EmgEvent e) && e.Ent == me && e.Code == EmgConst.PushCauseKnock;
                impact |= k.TryGetLastEmgEvent(EmgEventKind.Impact, out EmgEvent f) && f.Ent == me;
            }
            return (math.distance(p0, Pos(k, me)), push, impact);
        }

        private static void CheckAnchorAndRam()
        {
            Line("  · E. 冲撞走统一位移入口（DEBT-EMG1ENT01-06：evt.push、撞上发 evt.impact）；锚定不受击退位移");
            var free = RamOnce(EmgTraitConst.None);
            var anchored = RamOnce(EmgTraitConst.FixedAnchor);
            Expect(free.moved > 0.3 && free.push && free.impact, $"铸造冲锋撞上机器：被击退 {free.moved:0.##} 米、evt.push {free.push}、撞上背后的车 evt.impact {free.impact}");
            Expect(anchored.moved < 0.05 && !anchored.push, $"锚定（固定底盘机制）：位移 {anchored.moved:0.###} 米、evt.push {anchored.push}（不受击退）");
        }

        // ── F 冲刺 ──────────────────────────────────────────────────────────

        /// <summary>FG-GAP-176：装冲刺器的车执行攻击命令、目标在射程外冲刺距离内时冲上去。返回 0.5 秒内走了多远、是否发了冲刺原因的 evt.push、撞到挡路单位的 evt.impact。
        /// 也给 FgReadingMatrixSelfCheck C3“装 / 不装行为不同”用。</summary>
        internal static (double moved, bool push, bool impact) DashRun(bool dash, byte trait = EmgTraitConst.None, bool blocker = false)
        {
            using CombatKernel k = NewKernel();
            int listen = Listener(k, 0, EmgEventKind.Push, EmgEventKind.Impact);
            int me = Machine(k, new double2(0, 0), trait, listen, 1000f);
            k.SetUnitDash(me, dash);
            int foe = Foe(k, new double2(0, 8), 1000f);
            if (blocker)
            {
                Foe(k, new double2(0, 3), 1000f);
            }
            StepN(k, 1);
            k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 8), foe, 0.2f, 2f, 1f, false);
            bool push = false, impact = false;
            for (int s = 0; s < 30; s++)
            {
                StepN(k, 1);
                push |= k.TryGetLastEmgEvent(EmgEventKind.Push, out EmgEvent e) && e.Ent == me && e.Code == EmgTraitConst.PushCauseDash;
                impact |= k.TryGetLastEmgEvent(EmgEventKind.Impact, out EmgEvent f) && f.Ent == me;
            }
            return (Pos(k, me).y, push, impact);
        }

        /// <summary>审查 P1：冻结（寒冻相态）的车不冲；<paramref name="freezeStep"/> &lt; 0 = 下攻击命令前就冻住，否则第 N 步（冲刺途中）冻住。返回冻住那一刻的 y、0.5 秒末的 y、冻住是否生效、末了的剩余冲刺时间。</summary>
        private static (double at, double end, bool frozen, float dashT) DashFreeze(int freezeStep)
        {
            using CombatKernel k = NewKernel();
            int me = Machine(k, new double2(0, 0), EmgTraitConst.None, Listener(k, 0, EmgEventKind.Push), 1000f);
            k.SetUnitDash(me, true);
            int foe = Foe(k, new double2(0, 8), 1000f);
            StepN(k, 1);
            bool frozen = false;
            if (freezeStep < 0)
            {
                k.AddAccum(me, EmgPhaseConst.Chill, 10f);
                frozen = k.InPhase(me, EmgPhaseConst.Chill);
            }
            k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 8), foe, 0.2f, 2f, 1f, false);
            double at = Pos(k, me).y;
            for (int s = 0; s < 30; s++)
            {
                if (s == freezeStep)
                {
                    k.AddAccum(me, EmgPhaseConst.Chill, 10f);
                    frozen = k.InPhase(me, EmgPhaseConst.Chill);
                    at = Pos(k, me).y;
                }
                StepN(k, 1);
            }
            frozen &= k.InPhase(me, EmgPhaseConst.Chill);
            return (at, Pos(k, me).y, frozen, k.TryGetTraitRun(me, out EmgTraitRun r) ? r.DashT : -1f);
        }

        internal static bool DashDiffers()
        {
            NewKernel().Dispose();
            var on = DashRun(true);
            var off = DashRun(false);
            return on.moved > off.moved + 2.0;
        }

        private static void CheckDash()
        {
            Line("  · F. 冲刺（FG-GAP-176）：攻击命令下目标在射程外冲刺距离内 → 冲上去；撞上挡路单位发 evt.impact 并停；离地不冲；没装不冲；按声明开（不按部件 ID）");
            var on = DashRun(true);
            var off = DashRun(false);
            var hover = DashRun(true, EmgTraitConst.HoverFloat);
            var hit = DashRun(true, EmgTraitConst.None, true);
            Expect(on.moved > off.moved + 2.0, $"0.5 秒内：装冲刺器走 {on.moved:0.##} 米，没装 {off.moved:0.##} 米（每步位移 < emg.push.d_min，不逐步发 evt.push）");
            Expect(hover.moved < on.moved - 2.0, $"负向：悬浮（chs.ground 为假）不冲（{hover.moved:0.##} 米）");
            Expect(hit.impact && hit.moved < on.moved - 1.0, $"挡路单位：撞上发 evt.impact {hit.impact}，冲刺当场结束（0.5 秒只到 {hit.moved:0.##} 米，自由冲刺 {on.moved:0.##} 米）");
            var pre = DashFreeze(-1);
            var mid = DashFreeze(8);
            Expect(pre.frozen && pre.end - pre.at < 0.05, $"负向：冻住的车不冲（下令前冻住，0.5 秒位移 {pre.end - pre.at:0.###} 米，冻结 {pre.frozen}）");
            Expect(mid.frozen && mid.at > 0.5 && mid.end - mid.at < 0.05 && mid.dashT == 0f,
                $"负向：冲刺途中冻住当场停（冻住时已冲到 {mid.at:0.##} 米，之后位移 {mid.end - mid.at:0.###} 米，剩余冲刺 {mid.dashT:0.##} 秒）");
            Expect(EmgCatalog.OwnerDeclaresWrite("func_dash", "en.kinetic") && !EmgCatalog.OwnerDeclaresWrite("func_marker", "en.kinetic"),
                "冲刺按功能槽部件的声明（写 en.kinetic）开，不按部件 ID（CombatSite.ApplyDash）");
        }

        // ── G 哨戒桩导体 ─────────────────────────────────────────────────────

        private static void CheckStakeConductor()
        {
            Line("  · G. 绝缘闪络（C04）跳哨戒桩（DEBT-EMG2REACT02-04）：哨戒桩比别的导体近时电进哨戒桩、电荷随时间衰减；飞行无人机不算");
            using CombatKernel k = NewKernel();
            int u = Foe(k, new double2(0, 0));
            int other = Foe(k, new double2(3.5, 0));
            int stake = k.AddDroneForTests(new double2(1.5, 0), u, CombatFaction.Hostile, true);
            int flyer = k.AddDroneForTests(new double2(0.5, 0), u, CombatFaction.Hostile, false);
            uint oil = NamedReactionCatalog.BitOf("Oil"), shock = NamedReactionCatalog.BitOf("Shock");
            k.ApplyStatus(u, oil, 10f, 0f, 0f, 0f, 0);
            k.ApplyStatus(u, shock, 3f, 0f, 0f, 0f, 0);
            float q = k.DroneCharge(stake);
            Expect(q > 0.3f && k.DroneCharge(flyer) == 0f && k.AccumOf(other, EmgPhaseConst.Charge) <= 0f && k.AccumOf(u, EmgPhaseConst.Charge) <= 0f,
                $"电跳进 1.5 米处的哨戒桩（{q:0.##}），更远的导体单位 {k.AccumOf(other, EmgPhaseConst.Charge):0.##}，0.5 米处的飞行无人机 {k.DroneCharge(flyer):0.##}");
            StepN(k, 60);
            Expect(k.DroneCharge(stake) < q * 0.6f, $"哨戒桩电荷 1 秒后衰减到 {k.DroneCharge(stake):0.##}");
        }

        // ── H 快照 ──────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · H. 快照 18：拖挂车上有一块时存读档——不复制、哈希一致、续跑一致；读 17 版快照：特性 / 运行态 / 电荷取缺省");
            using CombatKernel a = NewKernel();
            a.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 3f, 120f);
            int w = Machine(a, new double2(0, 0), EmgTraitConst.WheelTow);
            int t = Machine(a, new double2(-5, 4), EmgTraitConst.TrackDrag);
            int d = Machine(a, new double2(5, 5));
            a.SetUnitDash(d, true);
            StepN(a, 75);
            Move(a, w, new double2(10, 0));
            Move(a, t, new double2(10, 4));
            StepN(a, 30);
            byte[] snap = a.Serialize();
            using CombatKernel b = NewKernel();
            CombatLoadResult r = b.Load(snap);
            float oilA = Matter(a, EmgMatterConst.Oil).sum + Carried(a, w), oilB = Matter(b, EmgMatterConst.Oil).sum + Carried(b, w);
            Expect(r == CombatLoadResult.Ok && a.StateHash() == b.StateHash() && b.TraitRunCount == 3 && b.TraitOf(w) == EmgTraitConst.WheelTow && Carried(b, w) > 0f
                   && Matter(b, EmgMatterConst.Oil).n == Matter(a, EmgMatterConst.Oil).n && Mathf.Abs(oilA - oilB) < 1e-4f,
                $"读档 {r}：哈希一致 {a.StateHash() == b.StateHash()}，运行态 {b.TraitRunCount} 行，车上 {Carried(b, w):0.##}，地上块数 {Matter(b, EmgMatterConst.Oil).n}（原 {Matter(a, EmgMatterConst.Oil).n}），油合计 {oilB:0.##}（不复制）");
            StepN(a, 720);
            StepN(b, 720);
            Expect(a.StateHash() == b.StateHash() && Mathf.Abs(Matter(a, EmgMatterConst.Oil).sum - Matter(b, EmgMatterConst.Oil).sum) < 1e-4f && Carried(b, w) == 0f && Carried(a, w) == 0f
                   && Matter(a, EmgMatterConst.Oil).n == Matter(b, EmgMatterConst.Oil).n,
                $"续跑 12 秒：哈希一致 {a.StateHash() == b.StateHash()}、随车斑块到期各自只落一次（块数 {Matter(a, EmgMatterConst.Oil).n}/{Matter(b, EmgMatterConst.Oil).n}，油合计 {Matter(b, EmgMatterConst.Oil).sum:0.##}）");
            byte[] v17 = a.SerializeFormatForTests(17);
            using CombatKernel c = NewKernel();
            CombatLoadResult r17 = c.Load(v17);
            Expect(r17 == CombatLoadResult.Ok && c.TraitRunCount == 0 && c.TraitOf(w) == EmgTraitConst.None && c.MetaRunCount == 0 && c.BeatCount == 0,
                $"读 17 版快照 {r17}：特性 {c.TraitOf(w)}、运行态 {c.TraitRunCount}、元程序 {c.MetaRunCount}、节拍 {c.BeatCount}（缺省全 0）");
            byte[] longSeg = a.SerializeEmgShapeForTests(0, int.MaxValue, 4);
            using CombatKernel e = NewKernel();
            Expect(e.Load(longSeg) == CombatLoadResult.Ok && e.TraitRunCount == a.TraitRunCount, "较晚提交写出的长段 / 不认识的段：按段长跳过，阶段 3 段照常读");
            using CombatKernel empty = NewKernel();
            Machine(empty, new double2(0, 0));
            StepN(empty, 5);
            ulong h0 = empty.StateHash();
            using CombatKernel empty2 = NewKernel();
            empty2.Load(empty.Serialize());
            Expect(empty2.StateHash() == h0 && empty2.TraitRunCount == 0, "没有特性单位：段 22～24 为空，哈希不受影响、往返一致");

            // 审查 P2：特性参数是表数据，读档沿用当前内核的（改过 emg.dash.* 后存读档续跑一致：冲刺速度 20，8 米外的目标只有改过的参数才够得着）
            var fast = new float4(20f, 0.4f, 6f, 0f);
            using CombatKernel pa = NewKernel();
            pa.SetTraitParams(EmgTraitConst.None, fast);
            int pm = Machine(pa, new double2(0, 0), EmgTraitConst.None, -1, 1000f);
            pa.SetUnitDash(pm, true);
            int pf = Foe(pa, new double2(0, 9), 1000f);
            StepN(pa, 1);
            pa.IssueCommand(pm, CombatCommandKind.Attack, new double2(0, 9), pf, 0.2f, 2f, 1f, false);
            byte[] ps = pa.Serialize();
            using CombatKernel pb = NewKernel();
            pb.SetTraitParams(EmgTraitConst.None, fast);
            pb.Load(ps);
            StepN(pa, 30);
            StepN(pb, 30);
            Expect(pa.StateHash() == pb.StateHash() && Pos(pb, pm).y > 4.0, $"改过冲刺参数后存读档续跑：哈希一致 {pa.StateHash() == pb.StateHash()}，0.5 秒到 {Pos(pa, pm).y:0.##} / {Pos(pb, pm).y:0.##} 米");

            // 审查 P2：运行态行按单位 Id 有序（先给高 Id 车建行、再给低 Id 车建行），两车同一步从同一块源斑块分出——存读档续跑哈希一致
            using CombatKernel oa = NewKernel();
            oa.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 4f, 6f, 120f);
            int lo = Machine(oa, new double2(-1, -3.5));
            int hi = Machine(oa, new double2(1, -3.5), EmgTraitConst.TrackDrag);
            oa.SetUnitTrait(lo, EmgTraitConst.TrackDrag);
            StepN(oa, 2);
            Move(oa, lo, new double2(-1, 10));
            Move(oa, hi, new double2(1, 10));
            StepN(oa, 40);
            byte[] os = oa.Serialize();
            using CombatKernel ob = NewKernel();
            ob.Load(os);
            StepN(oa, 120);
            StepN(ob, 120);
            Expect(oa.TraitRunCount == 2 && oa.StateHash() == ob.StateHash() && Matter(oa, EmgMatterConst.Oil).n == Matter(ob, EmgMatterConst.Oil).n && Matter(oa, EmgMatterConst.Oil).n > 3,
                $"乱序建行的两台履带车同步拖带：续跑 2 秒哈希一致 {oa.StateHash() == ob.StateHash()}，油块 {Matter(oa, EmgMatterConst.Oil).n} / {Matter(ob, EmgMatterConst.Oil).n}");
        }

        // ── I 暂停与倍速 ─────────────────────────────────────────────────────

        private static void CheckTiming()
        {
            Line("  · I. 暂停与 0.5x～3x：拖挂计时随模拟时间走（倍速只改每帧步数），暂停不走；同一模拟时间结果逐字一致");
            // pauseAt：第几帧起暂停 100 帧（CombatSite 暂停时不调内核 Step，这里同样不步进）；-1 = 不暂停
            ulong RunAt(int stepsPerFrame, int pauseAt, out float carried, out float towT)
            {
                using CombatKernel k = NewKernel();
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 3f, 120f);
                int w = Machine(k, new double2(0, 0), EmgTraitConst.WheelTow);
                int t = Machine(k, new double2(-3, 3), EmgTraitConst.TrackDrag);
                Move(k, t, new double2(6, 0));
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
                k.TryGetTraitRun(w, out EmgTraitRun run);
                carried = run.TowAmount;
                towT = run.TowT;
                return k.StateHash();
            }
            ulong h1 = RunAt(2, -1, out float c1, out float t1), h05 = RunAt(1, -1, out _, out _), h3 = RunAt(6, -1, out _, out _);
            ulong hp = RunAt(2, 40, out _, out float tp);
            Expect(h1 == h05 && h1 == h3 && h1 == hp && c1 > 0f && t1 > 0f && Mathf.Abs(tp - t1) < 1e-6f,
                $"0.5x / 1x / 3x 同一模拟时间哈希一致（{h05 == h1}/{h3 == h1}）；随车途中暂停 100 帧再续跑，哈希与随车剩余时间都与不暂停相同（{hp == h1}，剩 {tp:0.##}/{t1:0.##} 秒）");
        }

        // ── J 耗时 ──────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 耗时（Editor batchmode，Burst 状态见环境行；真机另测 FG15-SYS-02）：没有特性单位时底盘步早退出；20 台特性车 + 120 个敌人的内核每步");
            double Measure(int traitCars, out int n)
            {
                using CombatKernel k = NewKernel(256);
                for (int i = 0; i < 120; i++)
                {
                    Foe(k, new double2(60 + i % 12 * 2, i / 12 * 2));
                }
                for (int i = 0; i < 20; i++)
                {
                    k.SpawnPatch(EmgMatterConst.Oil, new double2(i * 4, 0), 1.5f, 2f, 600f);
                    int m = Machine(k, new double2(i * 4, 0), i < traitCars ? (i % 2 == 0 ? EmgTraitConst.TrackDrag : EmgTraitConst.WheelTow) : EmgTraitConst.None);
                    Move(k, m, new double2(i * 4, 40));
                }
                StepN(k, 30);
                var sw = Stopwatch.StartNew();
                StepN(k, 600);
                sw.Stop();
                n = k.TraitRunCount;
                return sw.Elapsed.TotalMilliseconds / 600.0;
            }
            double off = Measure(0, out int n0), on = Measure(20, out int n1);
            Line($"    环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，{SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}");
            Line($"    内核每步：无特性 {off:0.###} ms（运行态 {n0} 行）；20 台拖带 / 拖挂 {on:0.###} ms（{n1} 行），差 {on - off:0.###} ms");
            Expect(n0 == 0 && n1 == 20 && on - off < 0.1, $"特性开销 {on - off:0.###} ms / 步（门限 0.1 ms，20 台；没有特性单位时运行态 0 行、StepChassis 一条长度比较）");
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
