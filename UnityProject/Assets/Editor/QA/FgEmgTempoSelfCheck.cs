using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG3-TMP-01 时间节拍与相位对齐的自动验收（FG19 FGR-EMG-170～172；FGT-EMG-053 同机部分、FGT-EMG-020 evt.beat 生产者）。
    /// 起真实编译器（真实表里的 fw_clock / fw_resonance）、真实预览（EmgProgramTreeText）、真实登记（CombatSite.RegisterProgram）与真实内核（CombatKernel）断言行为：
    /// A 编译与预览（声明轨、周期 / 相位落行、调槽位顺序改相位、需要飞行、MetaMask）；B 生产者（拍序号 = ⌊模拟时间 ÷ 0.5⌋、按拍放子弹药的步号）；C 相位；
    /// D 相位对齐（两条同相位 → tmp.sync、错相位 / 单条指令多发弹体都不触发、每台机器每拍最多一次、读 tmp.sync 的余振只在对齐拍）；E 5 载体（格斗 / 力场锚点、布区、无人机）；
    /// F 暂停与 0.5x～3x（同一模拟时间哈希与出拍步号一致、暂停期间不出拍）；G 存读档（段 24 拍序号接续、段 23 节拍锚点、续跑逐步哈希一致）；H 空场景早退出；K 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgTempoSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgTempoSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgTempoSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger, Cond = EmgConst.RoleCondition, RoleSpawn = 7, RoleInject = 8;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/时间节拍")]
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
            Line("\n[时间节拍] EMG3-TMP-01：全局节拍、周期与相位、相位对齐（FGT-EMG-053 同机部分、FGT-EMG-020 evt.beat 生产者）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程（Editor 下 Mono / Burst，真机 IL2CPP，耗时只作量级参考）");
            GameLanguage lang = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                CarrierReadings.Reload();
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Step(CheckCompile);
                Step(CheckProducer);
                Step(CheckPhase);
                Step(CheckSync);
                Step(CheckResonancePoint);
                Step(CheckSubResonance); // EMG4-CLEAN-01：FG-GAP-209
                Step(CheckCarriers);
                Step(CheckNested);
                Step(CheckTiming);
                Step(CheckSaveLoad);
                Step(CheckEmpty);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"时间节拍自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [时间节拍] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static EmgProgramResult P(FirmwareCarrier car, params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, car, 0);

        private static EmgProgramResult Pc(int cap, params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, cap);

        private static string TreeText(EmgProgramResult r)
        {
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows);
            return string.Join(" | ", rows.Select(x => x.Text));
        }

        // ── A. 编译与预览 ──

        private static void CheckCompile()
        {
            Line("  · A. 编译与预览：fw_clock / fw_resonance 走声明轨；tmp.beat 行幅度 = 周期、param2 = 相位（缺省 = 程序序号 % 周期，调槽位顺序即调相位）；射弹读法需要飞行；预览写周期与相位");
            EmgCompiled c = EmgCatalog.Current;
            Expect(EmgCatalog.IsOwnerOnDeclTrack("fw_clock") && EmgCatalog.IsOwnerOnDeclTrack("fw_resonance") && !EmgCatalog.IsPendingPort("evt.beat", EmgRole.Trigger)
                   && !EmgCatalog.IsPendingPort("tmp.beat", EmgRole.Condition) && !EmgCatalog.IsPendingPort("tmp.sync", EmgRole.Condition) && c.Problems.Count == 0,
                $"真实表：evt.beat / tmp.* 已接通，fw_clock 与 fw_resonance 在声明轨（目录问题 {c.Problems.Count} 条）");
            EmgProgramResult one = P(FirmwareCarrier.Projectile, "fw_clock");
            // 根容量 4（子节点 2）：第 1、2 条修饰填满子节点后，第 4 条时钟脉冲回到根（FGR-EMG-031），根里两条节拍指令序号 0 / 3
            EmgProgramResult two = Pc(4, "fw_clock", "fw_pierce", "fw_tractor", "fw_clock");
            EmgProgramResult nested = P(FirmwareCarrier.Projectile, "fw_clock", "fw_clock");
            Beat(one, 0, 0, out int p0, out int ph0);
            Beat(two, 0, 3, out int p1, out int ph1);
            Beat(nested, 1, 1, out int pn, out int phn);
            bool legacyOff = !one.LegacyOwners.Contains("fw_clock");
            bool flight = (one.Program.Flags & EmgProgramFlags.NeedsFlight) != 0;
            bool mask = one.KernelNodes.Count == 2 && one.KernelNodes[1].MetaMask == 1 && one.KernelNodes[1].TrigEvent == EmgEventKind.Beat;
            Expect(p0 == 2 && ph0 == 0 && p1 == 2 && ph1 == 1 && pn == 2 && phn == 1 && legacyOff && flight && mask,
                $"[时钟脉冲]：周期 {p0}、相位 {ph0}；[时钟脉冲, 贯穿, 牵引, 时钟脉冲] 根里第 4 条相位 {ph1}；[时钟脉冲, 时钟脉冲] 第 2 条进子弹药节点、相位 {phn}；旧 rhythm 读法已退役 {legacyOff}；需要飞行 {flight}；子节点记触发器序号 {mask}");
            string text = TreeText(two);
            Expect(text.Contains(GameText.Format("emg.tree.tag.beat", 2, 0)) && text.Contains(GameText.Format("emg.tree.tag.beat", 2, 1)) && !text.Contains("emg.tree.tag"),
                $"预览两行节拍：“{GameText.Format("emg.tree.tag.beat", 2, 0)}” / 相位 1");
            EmgProgramResult res = P(FirmwareCarrier.Projectile, "fw_resonance");
            string rt = TreeText(res);
            Expect(EmgProgramCompiler.SyncOf(res, 0, 0) && rt.Contains(GameText.Get("emg.tree.tag.sync")) && !res.LegacyOwners.Contains("fw_resonance"),
                $"[谐振] 读 tmp.sync：预览写“{GameText.Get("emg.tree.tag.sync")}”");
            // 审查修复（第 1 轮，FGR-EMG-019 单轨）：谐振的 en.kinetic 由内核按拍余振，正式路径（ProgramReading）里不再折旧回波 echo；回波固件作对照仍有
            int resEcho = RealEcho("fw_resonance"), echoEcho = RealEcho("fw_echo");
            Expect(resEcho == 0 && echoEcho > 0, $"正式读法：[谐振] 每次命中的旧回波 {resEcho} 次（应为 0，只走内核节拍余振）；对照 [回波] {echoEcho} 次");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = TreeText(two);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en.Contains("every 2 beats") && !en.Contains("emg.tree"), "英文预览无缺键（every 2 beats, phase …）");
        }

        /// <summary>按正式路径（电路板 → 接入编译 → CombatSite.ProgramReading）编出机枪读法的回波次数。</summary>
        private static int RealEcho(string fw)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            return program.IsEmpty ? -1 : CombatSite.ProgramReading(p, w.Reading, program).EchoCount;
        }

        private static void Beat(EmgProgramResult r, int node, int ord, out int period, out int phase)
        {
            if (!EmgProgramCompiler.BeatOf(r, node, ord, out period, out phase))
            {
                period = phase = -1;
            }
        }

        // ── 内核夹具 ──

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgSiteSeed = 12345u;
            c.EmgBeatSeconds = 0.5f;
            return c;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static CombatWeapon Instant(int program, float range = 30f, CombatCarrier carrier = CombatCarrier.Projectile, float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f, Program = program,
            Reading = new CombatReading
            {
                Carrier = carrier, Area = carrier == CombatCarrier.Field ? 2f : carrier == CombatCarrier.Aura ? 3f : 0f, FieldSeconds = carrier == CombatCarrier.Field ? 2.2f : 0f,
                FieldDpsRatio = carrier == CombatCarrier.Field ? 1f : 0f,
            },
        };

        private static CombatWeapon Flight(CombatWeapon w, float speed, float life)
        {
            w.SpawnFlight = 1;
            w.ProjectileSpeed = speed;
            w.ProjectileRadius = 0.15f;
            w.ProjectileLife = life;
            return w;
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static long Subs(CombatKernel k) => k.EmgBusStats.SubSpawned;

        private static ushort Ev(EmgEventKind e) => (ushort)(EmgConst.EventPortBase + (int)e);

        private static EmgInstr Row(int ord, ushort port, byte role, float amount = 1f, float p2 = 0f, byte verb = 0) =>
            new EmgInstr { Ordinal = (byte)ord, Port = port, Role = role, Amount = amount, Param2 = p2, Verb = verb };

        /// <summary>节拍指令（与 fw_clock 同形）：evt.beat 触发 + tmp.beat 条件（周期 / 相位）+ 放 1 份子载荷。</summary>
        private static EmgInstr[] ClockI(int ord, int period, int phase) => new[]
        {
            Row(ord, Ev(EmgEventKind.Beat), Trig), Row(ord, EmgTempoConst.PortBeat, Cond, period, phase), Row(ord, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload),
        };

        /// <summary>谐振（与 fw_resonance 同形）：evt.beat 触发 + tmp.sync 条件 + en.kinetic 注入（幅度 0.3、半径 1.5 米）。</summary>
        private static EmgInstr[] ResI(int ord) => new[]
        {
            Row(ord, Ev(EmgEventKind.Beat), Trig), Row(ord, EmgTempoConst.PortSync, Cond), Row(ord, EmgConst.PortKinetic, RoleInject, 0.3f, 1.5f, (byte)EmgVerb.Inject),
        };

        /// <summary>手写程序：根节点 + 每条节拍指令一个子节点（MetaMask = 触发器序号 + 1，与编译器 TempoPass 同口径）。</summary>
        private static int Prog(CombatKernel k, EmgInstr[][] root, params int[] childOf)
        {
            var nodes = new List<EmgNode>();
            var instrs = root.SelectMany(x => x).ToList();
            nodes.Add(new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)instrs.Count });
            foreach (int ord in childOf)
            {
                nodes.Add(new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Beat, TrigCount = 1, FirstInstr = instrs.Count, InstrCount = 0, MetaMask = (byte)(ord + 1) });
            }
            uint mask = (1u << (int)EmgEventKind.Beat) | (1u << (int)EmgEventKind.Fire);
            return k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
        }

        /// <summary>一台机器发一颗慢速长寿命弹（2 米 / 秒、10 秒），返回每次放出子弹药的步号（从开火起计）。</summary>
        private static List<int> FlightRun(Func<CombatKernel, int> build, int steps, out long beats, out CombatKernel kept, bool keep = false)
        {
            var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            k.FireAt(me, foe, k.Time);
            var at = new List<int>();
            long last = 0;
            for (int s = 1; s <= steps; s++)
            {
                StepN(k, 1);
                for (long q = last; q < Subs(k); q++)
                {
                    at.Add(s);
                }
                last = Subs(k);
            }
            beats = k.BeatCount;
            kept = keep ? k : null;
            if (!keep)
            {
                k.Dispose();
            }
            return at;
        }

        // ── B. 生产者（FGT-EMG-020）──

        private static void CheckProducer()
        {
            Line("  · B. evt.beat 生产者：拍序号 = ⌊模拟时间 ÷ 0.5 秒⌋；真实编译的 [时钟脉冲] 在飞弹体每 2 拍（第 2、4、6、8 拍 = 第 60、120、180、240 步）从当前位置放 1 份子弹药");
            EmgProgramResult r = P(FirmwareCarrier.Projectile, "fw_clock");
            List<int> at = FlightRun(k => CombatSite.RegisterProgram(k, r), 252, out long beats, out _);
            Expect(beats == 8 && at.SequenceEqual(new[] { 60, 120, 180, 240 }),
                $"4.2 秒：拍序号 {beats}；子弹药放出步号 [{string.Join(", ", at)}]");
            using var k2 = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k2, r);
            int me = Unit(k2, CombatFaction.Player, new double2(0, 0), k2.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
            Unit(k2, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            StepN(k2, 90);
            Expect(k2.EmgBusStats.BeatsEmitted == 0 && Subs(k2) == 0 && k2.BeatCount == 3,
                $"没有在飞的宿主时不出事件（发出 {k2.EmgBusStats.BeatsEmitted} 条），拍序号照走（{k2.BeatCount}）");
        }

        // ── C. 相位 ──

        private static void CheckPhase()
        {
            Line("  · C. 相位：周期 2 相位 1 在第 1、3、5、7 拍放；周期 3 相位 2 在第 2、5、8 拍放；编译的根里两条时钟脉冲错相位，每拍各放 1 份交替");
            List<int> p1 = FlightRun(k => Prog(k, new[] { ClockI(0, 2, 1) }, 0), 252, out _, out _);
            List<int> p3 = FlightRun(k => Prog(k, new[] { ClockI(0, 3, 2) }, 0), 252, out _, out _);
            Expect(p1.SequenceEqual(new[] { 30, 90, 150, 210 }) && p3.SequenceEqual(new[] { 60, 150, 240 }),
                $"周期 2 相位 1：[{string.Join(", ", p1)}]；周期 3 相位 2：[{string.Join(", ", p3)}]");
            EmgProgramResult two = Pc(4, "fw_clock", "fw_pierce", "fw_tractor", "fw_clock");
            List<int> alt = FlightRun(k => CombatSite.RegisterProgram(k, two), 252, out _, out _);
            Expect(alt.SequenceEqual(new[] { 30, 60, 90, 120, 150, 180, 210, 240 }), $"[时钟脉冲, 贯穿, 牵引, 时钟脉冲]（根里相位 0 / 1）：[{string.Join(", ", alt)}]（每拍 1 份，不重叠）");
        }

        // ── D. 相位对齐（FGR-EMG-172）──

        private static void CheckSync()
        {
            Line("  · D. 相位对齐：同机两条同相位节拍指令同拍执行 → tmp.sync，读它的余振本拍执行（半径 ×2）；错相位、只有一条节拍指令（哪怕 3 发在飞）都不触发；每台机器每拍最多一次");
            long aligned = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1), 1, out long pulses, out float hpLoss);
            long offset = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 1), ResI(2) }, 0, 1), 1, out long pulsesOff, out float lossOff);
            long single = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), ResI(1) }, 0), 3, out long pulsesSingle, out _);
            long many = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1), 3, out long pulsesMany, out _);
            // EMG3-E2E-01（DEBT-EMG3TMP01-01，FG19A 第 132 行）：射弹在“最近一次命中点”余振，飞行中还没命中过不余振（命中点余振与半径 ×2 见 N 段）；对齐计数不变
            Expect(aligned == 4 && pulses == 0 && hpLoss == 0f, $"两条同相位：4.2 秒内对齐 {aligned} 次（第 2、4、6、8 拍）；飞行中没命中过 → 余振 {pulses} 次、弹道旁的敌人掉血 {hpLoss:0.0}（修前在弹体位置余振 4 次）");
            Expect(offset == 0 && pulsesOff == 0 && lossOff == 0f, $"两条错相位：对齐 {offset} 次、余振 {pulsesOff} 次（不读 tmp.sync 的节拍指令照放）");
            Expect(single == 0 && pulsesSingle == 0, $"只有一条节拍指令、3 发同时在飞：对齐 {single} 次（同一条指令按序号去重）");
            Expect(many == 4 && pulsesMany == 0, $"两条同相位、3 发同时在飞：对齐 {many} 次（每台机器每拍最多一次，不是 12 次）、飞行中余振 {pulsesMany} 次");
            // EMG3-FW-02（DEBT-EMG3TMP01-04，FGR-EMG-172“本拍执行”）：被 chs.* 条件 / 元程序条件关掉的节拍指令不计入相位对齐
            EmgInstr[] clock1Air = ClockI(1, 2, 0).Concat(new[] { Row(1, EmgConst.PortChsAirborne, Cond) }).ToArray(); // 第 2 条时钟要求离地，出手者着地
            long chsGated = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), clock1Air, ResI(2) }, 0, 1), 1, out long chsPulses, out _);
            long chsHover = SyncRun(k => Prog(k, new[] { ClockI(0, 2, 0), clock1Air, ResI(2) }, 0, 1), 1, out long hoverPulses, out _, hover: true);
            EmgInstr[] ifHot = { Row(1, (ushort)(EmgConst.PortMetaBase + EmgMetaConst.If), 6), Row(1, EmgConst.PortPropHeat, Cond, 0.5f) }; // [条件·过热] 关掉后面的时钟与谐振（冷武器）
            long metaGated = SyncRun(k =>
            {
                var root = new[] { ClockI(0, 2, 0), ifHot, ClockI(2, 2, 0), ResI(3) };
                var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)root.Sum(x => x.Length) } };
                foreach (int ord in new[] { 0, 2 })
                {
                    nodes.Add(new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Beat, TrigCount = 1, FirstInstr = nodes[0].InstrCount, InstrCount = 0, MetaMask = (byte)(ord + 1) });
                }
                return k.AddProgram(new EmgProgram { ListenMask = (1u << (int)EmgEventKind.Beat) | (1u << (int)EmgEventKind.Fire), Flags = EmgProgramFlags.HasMeta }, nodes,
                    root.SelectMany(x => x).ToList());
            }, 1, out long metaPulses, out _);
            Expect(chsGated == 0 && chsPulses == 0 && chsHover == 4 && hoverPulses == 0 && metaGated == 0 && metaPulses == 0,
                $"被门控的节拍指令不计对齐（DEBT-EMG3TMP01-04）：第 2 条时钟读 chs.airborne、出手者着地 → 对齐 {chsGated} 次、余振 {chsPulses} 次；悬浮离地 → 对齐 {chsHover}、余振 {hoverPulses}；" +
                $"[时钟, 条件·过热, 时钟, 谐振] 冷武器 → 对齐 {metaGated} 次、余振 {metaPulses} 次（修前各 4 次）");
            // 根容量 3（子节点 1）：[谐振, 时钟脉冲, 贯穿, 时钟脉冲] → 根里谐振 0、时钟 1 / 3（相位都是 1），贯穿进子弹药节点
            EmgProgramResult real = Pc(3, "fw_resonance", "fw_clock", "fw_pierce", "fw_clock");
            long realSync = SyncRun(k => CombatSite.RegisterProgram(k, real), 1, out long realPulses, out _);
            Expect(realSync == 4 && realPulses == 0, $"真实编译 [谐振, 时钟脉冲, 贯穿, 时钟脉冲]（根里时钟序号 1 / 3，相位都是 1）：对齐 {realSync} 次、飞行中余振 {realPulses} 次");
            // 审查修复（第 1 轮，FGT-EMG-053 齐射）：读 tmp.sync 的触发器只在对齐拍放出它的子载荷（MetaMask + EmgBeatBlocked 路径），每台机器每拍一次
            List<int> volley = FlightRun(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), SyncTrigI(2) }, 0, 1, 2), 252, out _, out _);
            List<int> volleyOff = FlightRun(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 1), SyncTrigI(2) }, 0, 1, 2), 252, out _, out _);
            long volley3 = SyncSubs(k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), SyncTrigI(2) }, 0, 1, 2), 3);
            bool triple = volley.Count == 12 && volley.GroupBy(s => s).All(g => g.Count() == 3) && volley.Distinct().SequenceEqual(new[] { 60, 120, 180, 240 });
            Expect(triple && volleyOff.Count == 8 && volleyOff.GroupBy(s => s).All(g => g.Count() == 1) && volley3 == 28,
                $"齐射：对齐拍两条时钟 + 读 tmp.sync 的触发器同步各放 1 份（放出 {volley.Count} 份，步号 [{string.Join(", ", volley.Distinct())}]）；错相位时触发器不放（{volleyOff.Count} 份 = 两条时钟各 4）；3 发在飞时 {volley3} 份（= 4 拍 × (3 发 × 2 条时钟 + 每机器 1 次齐射)）");
        }

        /// <summary>evt.beat 触发 + tmp.sync 条件 + 放 1 份子载荷（齐射触发器）。</summary>
        private static EmgInstr[] SyncTrigI(int ord) => new[]
        {
            Row(ord, Ev(EmgEventKind.Beat), Trig), Row(ord, EmgTempoConst.PortSync, Cond), Row(ord, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload),
        };

        /// <summary>一台机器发 <paramref name="shots"/> 颗慢速弹（间隔 2 步）跑 4.2 秒，返回放出的子弹药总数。</summary>
        private static long SyncSubs(Func<CombatKernel, int> build, int shots)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 0.2f, 10f)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            for (int i = 0; i < shots; i++)
            {
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
            }
            StepN(k, 252 - 2 * shots);
            return Subs(k);
        }

        /// <summary>一台机器发 <paramref name="shots"/> 颗慢速弹（间隔 2 步），弹道旁 1 米外放一个敌人（余振半径 1.5 → 对齐 3 米够得着，平时够不着）。返回 tmp.sync 次数。</summary>
        private static long SyncRun(Func<CombatKernel, int> build, int shots, out long pulses, out float hpLoss, bool hover = false)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 0.2f, 10f)), 100f);
            if (hover)
            {
                k.SetUnitTrait(me, EmgTraitConst.HoverFloat); // EMG3-FW-02：悬浮离地（chs.airborne）
            }
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            int side = Unit(k, CombatFaction.Hostile, new double2(2.2, 0.5), -1, 5000f);
            for (int i = 0; i < shots; i++)
            {
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
            }
            StepN(k, 252 - 2 * shots);
            pulses = k.EmgBusStats.BeatPulses;
            hpLoss = k.TryGetUnit(side, out CombatUnitView v) ? 5000f - v.Health : -1f;
            return k.EmgBusStats.BeatSyncs;
        }

        /// <summary>
        /// EMG4-CLEAN-01（FG-GAP-209）：谐振装在子弹药节点上——子弹药命中时在命中点留 2 拍谐振点（按机器 × 节点），放出点不再留带谐振位的节拍锚点（修前在放出点按拍余振）。
        /// 手写程序：根节点 [时钟（每拍放子弹药）]，子节点 [谐振]。
        /// </summary>
        private static void CheckSubResonance()
        {
            Line("  · D3. 子弹药谐振（FG-GAP-209）：子弹药命中点留谐振点（只放行谐振那条），放出点不留锚点");
            using var k = new CombatKernel(Cfg(), 16);
            var instrs = new List<EmgInstr>(ClockI(0, 1, 0));
            int rootN = instrs.Count;
            instrs.AddRange(ResI(1));
            var nodes = new List<EmgNode>
            {
                new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)rootN },
                new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Beat, TrigCount = 1, FirstInstr = rootN, InstrCount = (byte)(instrs.Count - rootN), MetaMask = 1 },
            };
            int prog = k.AddProgram(new EmgProgram { ListenMask = (1u << (int)EmgEventKind.Beat) | (1u << (int)EmgEventKind.Fire) }, nodes, instrs);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
            double2 foePos = new double2(0, 6);
            int foe = Unit(k, CombatFaction.Hostile, foePos, -1, 5000f);
            k.FireAt(me, foe, k.Time);
            bool atHit = false, atRelease = false;
            long subs = 0;
            for (int i = 0; i < 60 * 4; i++)
            {
                StepN(k, 1);
                for (int j = 0; j < k.MetaRunCount; j++)
                {
                    if (!k.TryGetMetaRun(j, out EmgMetaRun an) || an.Mode != EmgMetaConst.RunBeat || an.Node != 1)
                    {
                        continue;
                    }
                    if (math.distance(an.Pos, foePos) < 0.8 && an.Mask == 1UL << 1)
                    {
                        atHit = true;
                    }
                    else if ((an.Mask & (1UL << 1)) != 0UL)
                    {
                        atRelease = true;
                    }
                }
                subs = Math.Max(subs, k.EmgBusStats.SubSpawned);
            }
            Expect(prog >= 0 && subs > 0 && atHit && !atRelease,
                $"子弹药 {subs} 份；命中点（靶位 0.8 米内）留了只放行谐振的谐振点 {atHit}；放出点带谐振位的锚点 {atRelease}（修前 = true，在放出点余振）");
        }

        // ── D2. 谐振命中点（EMG3-E2E-01 关闭 DEBT-EMG3TMP01-01；FG19A 第 132 行“命中点留 2 拍谐振点”）──

        private static void CheckResonancePoint()
        {
            Line("  · D2. 谐振命中点（DEBT-EMG3TMP01-01）：射弹命中点留 2 拍谐振点，对齐拍在命中点余振（半径 ×2）；错相位不余振；第二发命中后刷新到最近一次命中点；无人机每架按自己的命中点；2 拍后移除；存读档接续");
            Func<CombatKernel, int> aligned = k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1);
            Func<CombatKernel, int> offset = k => Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 1), ResI(2) }, 0, 1);
            ResRun(aligned, false, out long pulses, out float lossFoe, out float lossSide, out int anchorMask, out int runsLeft, out long subs, out _, out _);
            ResRun(offset, false, out long pulsesOff, out _, out float lossSideOff, out _, out _, out _, out _, out _);
            Expect(pulses == 1 && Near(lossSide, 3f, 0.01f) && lossFoe >= 12.9f && anchorMask == 1 << 2 && runsLeft == 0 && subs == 0,
                $"同相位：命中（约 0.25 秒）后留谐振点（只放行谐振那条，放行位 {anchorMask}），2 拍内第 2 拍对齐、在命中点余振 {pulses} 次——2.3 米外的敌人掉血 {lossSide:0.0}（3 米半径够到，平时 1.5 米够不到），"
                + $"被打的敌人 {lossFoe:0.0}（直击 10 + 余振 3）；时钟不在谐振点放子弹药（{subs} 份）；2 拍后谐振点移除（剩 {runsLeft}）");
            Expect(pulsesOff == 0 && lossSideOff == 0f, $"负向：错相位（没有对齐拍）谐振点不余振（{pulsesOff} 次、旁边掉血 {lossSideOff:0.0}）");
            ResRun(aligned, true, out long pulses2, out _, out float lossSide1, out _, out _, out _, out float lossSide2, out int anchors2);
            Expect(pulses2 == 1 && lossSide1 == 0f && Near(lossSide2, 3f, 0.01f) && anchors2 == 1,
                $"最近一次命中点：第二发打另一侧后谐振点刷新（{anchors2} 个），对齐拍只在第二个命中点余振——第一处旁边掉血 {lossSide1:0.0}、第二处 {lossSide2:0.0}");
            // 存读档：谐振点（快照段 23 的 RunBeat 记录，放行位一起存）中途存档 → 新内核读档 → 续跑逐步哈希一致
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = aligned(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 10f, 10f)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 3), -1, 5000f);
                int side = Unit(k, CombatFaction.Hostile, new double2(2.3, 2.5), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 36); // 0.6 秒：已命中、谐振点在，还没到对齐拍
                int runs = k.MetaRunCount;
                byte[] snap = k.Serialize();
                using var c = new CombatKernel(Cfg(), 16);
                bool loaded = c.Load(snap) == CombatLoadResult.Ok && c.MetaRunCount == runs && c.StateHash() == k.StateHash() && c.EmgStateHash() == k.EmgStateHash();
                bool every = true;
                for (int i = 0; i < 120; i++)
                {
                    StepN(k, 1);
                    StepN(c, 1);
                    every &= k.StateHash() == c.StateHash();
                }
                float lk = k.TryGetUnit(side, out CombatUnitView vk) ? 5000f - vk.Health : -1f, lc = c.TryGetUnit(side, out CombatUnitView vc) ? 5000f - vc.Health : -1f;
                Expect(runs == 1 && loaded && every && Near(lk, 3f, 0.01f) && lk == lc && k.EmgBusStats.BeatPulses == c.EmgBusStats.BeatPulses,
                    $"存读档：谐振点 {runs} 个中途存档，读档一致 {loaded}、续跑 120 步逐步哈希一致 {every}；读档后照样在命中点余振（旁边掉血 {lk:0.0} / {lc:0.0}）");
            }
            // 无人机：每架按自己最近一次命中点（宿主 = 无人机），无人机原地开火不在无人机此刻位置余振
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = aligned(k);
                CombatWeapon w = Instant(prog, 30f, CombatCarrier.Summon);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 6f, DroneRatio = 1f, DroneCooldown = 10f, DroneLeash = 10f };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                bool found = false;
                EmgMetaRun an = default;
                for (int i = 0; i < 60 * 3 && !found; i++)
                {
                    StepN(k, 1);
                    for (int j = 0; j < k.MetaRunCount && !found; j++)
                    {
                        found = k.TryGetMetaRun(j, out an) && an.Mode == EmgMetaConst.RunBeat && an.HostKind == (byte)EmgEntKind.Drone && an.Mask == 1UL << 2;
                    }
                }
                bool droneId = found && k.TryGetDrone(0, out CombatDrone dr) && dr.Id == an.Host;
                Expect(found && droneId && math.distance(an.Pos, new double2(0, 6)) < 0.01,
                    $"无人机：命中后按这架无人机（Id {an.Host}，与无人机一致 {droneId}）留谐振点，位置 = 命中点（离靶 {math.distance(an.Pos, new double2(0, 6)):0.00} 米）");
            }
            // 审查修复（EMG3-E2E-01 修复轮）：谐振点的对齐单独记，不抢同机在飞弹体本拍的 tmp.sync——[时钟, 时钟, 谐振, 齐射]：一发长寿命弹在飞，
            // 另一发每拍开头打近靶、命中点一直有谐振点；对齐拍照样放齐射（修前逻辑：谐振点先占对齐，齐射 4 拍全丢，8 份）
            long VolleyRes(bool near, out long resPulses)
            {
                using var k = new CombatKernel(Cfg(), 16);
                int prog = Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2), SyncTrigI(3) }, 0, 1, 3);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 4f, 10f)), 100f);
                int far = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
                int close = Unit(k, CombatFaction.Hostile, new double2(1.2, 0), -1, 5000f);
                k.FireAt(me, far, k.Time);
                for (int s = 0; s < 252; s++)
                {
                    if (near && s % 30 == 2)
                    {
                        k.FireAt(me, close, k.Time); // 0.3 秒内命中，不跨拍界
                    }
                    StepN(k, 1);
                }
                resPulses = k.EmgBusStats.BeatPulses;
                return Subs(k);
            }
            long vAlone = VolleyRes(false, out long pAlone), vRes = VolleyRes(true, out long pRes);
            Expect(vAlone == 12 && vRes == 12 && pAlone == 0 && pRes == 4,
                $"同机谐振点 + 在飞弹体齐射：只有在飞弹体时放 {vAlone} 份（4 拍 × 两条时钟 + 齐射）；命中点一直有谐振点时 {vRes} 份（修前逻辑：谐振点先占对齐、齐射全丢，8 份）、谐振点余振 {pRes} 次（对齐拍各 1）");
        }

        private static bool Near(float a, float b, float eps) => Math.Abs(a - b) <= eps;

        /// <summary>
        /// 一台机器对 (0, 3) 打一发快弹（10 米 / 秒，约 0.25 秒命中），2.3 米外放一个敌人；<paramref name="second"/> = 0.6 秒时对 (−3, 0) 再打一发（旁边 2.3 米外另放一个敌人）。跑 2 秒。
        /// </summary>
        private static void ResRun(Func<CombatKernel, int> build, bool second, out long pulses, out float lossFoe, out float lossSide, out int anchorMask, out int runsLeft, out long subs,
            out float lossSide2, out int anchors2)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 10f, 10f)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 3), -1, 5000f);
            int side = Unit(k, CombatFaction.Hostile, new double2(2.3, 2.5), -1, 5000f);
            int foe2 = Unit(k, CombatFaction.Hostile, new double2(-3, 0), -1, 5000f);
            int side2 = Unit(k, CombatFaction.Hostile, new double2(-2.5, 2.3), -1, 5000f);
            k.FireAt(me, foe, k.Time);
            StepN(k, 24); // 0.4 秒：已命中、还没出拍
            anchorMask = k.MetaRunCount == 1 && k.TryGetMetaRun(0, out EmgMetaRun an) ? (int)an.Mask : -1;
            anchors2 = -1;
            if (second)
            {
                StepN(k, 12);
                k.FireAt(me, foe2, k.Time);
                StepN(k, 18); // 0.9 秒：第二发已命中
                anchors2 = k.MetaRunCount;
                StepN(k, 66);
            }
            else
            {
                StepN(k, 96);
            }
            pulses = k.EmgBusStats.BeatPulses;
            lossFoe = k.TryGetUnit(foe, out CombatUnitView vf) ? 5000f - vf.Health : -1f;
            lossSide = k.TryGetUnit(side, out CombatUnitView vs) ? 5000f - vs.Health : -1f;
            lossSide2 = k.TryGetUnit(side2, out CombatUnitView v2) ? 5000f - v2.Health : -1f;
            runsLeft = k.MetaRunCount;
            subs = Subs(k);
        }

        // ── E. 5 载体（FG19A fw_clock 行：格斗 / 力场锚点 2 秒、布区存续期间、无人机每架）──

        private static void CheckCarriers()
        {
            Line("  · E. 5 载体：格斗 / 力场 / 即时射弹在结算点留 2 秒节拍锚点（第 0.25 秒出手 → 第 2、4 拍各放 1 份，2.25 秒后锚点移除）；布区存续期间按拍；每架无人机按拍");
            foreach (CombatCarrier car in new[] { CombatCarrier.Melee, CombatCarrier.Aura, CombatCarrier.Projectile })
            {
                using var k = new CombatKernel(Cfg(), 16);
                int prog = Prog(k, new[] { ClockI(0, 2, 0) }, 0);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, car == CombatCarrier.Projectile ? 30f : 3f, car)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.6), -1, 5000f);
                StepN(k, 15);
                k.FireAt(me, foe, k.Time);
                int anchors = k.MetaRunCount;
                StepN(k, 60 * 3);
                Expect(anchors == 1 && Subs(k) == 2 && k.MetaRunCount == 0, $"{car}：锚点 {anchors} 个，放出 {Subs(k)} 份，3 秒后锚点 {k.MetaRunCount} 个");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                // 格斗连挥 3 下：锚点只有 1 个（刷新到最后一下），带出手伤害；两条同相位 + 谐振在锚点处余振（对齐拍 3 米够到被打的敌人）
                int prog = Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, 3f, CombatCarrier.Melee)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.6), -1, 5000f);
                StepN(k, 15);
                for (int i = 0; i < 3; i++)
                {
                    k.FireAt(me, foe, k.Time);
                    StepN(k, 3);
                }
                k.TryGetMetaRun(0, out EmgMetaRun an);
                int anchors = k.MetaRunCount;
                float hp0 = k.TryGetUnit(foe, out CombatUnitView v0) ? v0.Health : -1f;
                StepN(k, 60 * 3);
                float hp1 = k.TryGetUnit(foe, out CombatUnitView v1) ? v1.Health : -1f;
                Expect(anchors == 1 && an.Damage == 10f && an.Other == foe && k.EmgBusStats.BeatPulses == 2 && hp0 - hp1 >= 5.9f,
                    $"格斗连挥 3 下：锚点 {anchors} 个（伤害 {an.Damage}、目标 {an.Other == foe}）；锚点 2 秒内余振 {k.EmgBusStats.BeatPulses} 次，被打的敌人因余振掉血 {hp0 - hp1:0.0}");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = Prog(k, new[] { ClockI(0, 2, 0) }, 0);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, 30f, CombatCarrier.Field)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f);
                StepN(k, 15);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60 * 4);
                Expect(Subs(k) == 2 && k.MetaRunCount == 0, $"布区（存续 2.2 秒）：放出 {Subs(k)} 份子区（第 2、4 拍），不留锚点");
            }
            CombatConfig cfg = Cfg();
            cfg.DroneReach = 4f;
            using (var k = new CombatKernel(cfg, 16))
            {
                int prog = Prog(k, new[] { ClockI(0, 2, 0) }, 0);
                CombatWeapon w = Instant(prog, 30f);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 2, DroneSeconds = 6f, DroneRatio = 1f, DroneCooldown = 10f, DroneLeash = 10f };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 30), -1, 5000f);
                StepN(k, 15);
                k.FireAt(me, foe, k.Time);
                int drones = k.DroneCount;
                StepN(k, 60 * 3);
                Expect(drones == 2 && Subs(k) == 6, $"无人机 {drones} 架：3 秒内（第 2、4、6 拍）每架各放 1 份，共 {Subs(k)} 份");
            }
        }

        /// <summary>嵌套：真实编译 [时钟脉冲, 时钟脉冲] 第 2 条在子弹药节点（相位 1）；子弹药寿命 0.5 秒撑不到相位，由放出点的 2 秒节拍锚点代为执行。</summary>
        private static void CheckNested()
        {
            Line("  · E2. 嵌套在子弹药里的节拍指令：放出点留 2 秒节拍锚点（每台机器每个节点一个，新放出刷新位置与时限），第 3、5、7 拍再放：4.2 秒共 4 + 3 = 7 份");
            EmgProgramResult r = P(FirmwareCarrier.Projectile, "fw_clock", "fw_clock");
            List<int> at = FlightRun(k => CombatSite.RegisterProgram(k, r), 252, out _, out _);
            int[] want = { 60, 90, 120, 150, 180, 210, 240 };
            Expect(at.SequenceEqual(want), $"放出步号 [{string.Join(", ", at)}]（期望 [{string.Join(", ", want)}]）");
        }

        // ── F. 暂停与 0.5x～3x（B09）──

        private static void CheckTiming()
        {
            Line("  · F. 暂停与 0.5x～3x：倍速只改每帧步数，同一模拟时间拍序号、出拍步号、状态哈希逐字一致；暂停（不调 Step）期间不出拍、拍序号不动");
            ulong RunAt(int perFrame, int pauseAt, out List<int> at, out long pausedBeats)
            {
                using var k = new CombatKernel(Cfg(), 16);
                int prog = Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                at = new List<int>();
                pausedBeats = -1;
                int stepped = 0;
                long last = 0;
                for (int frame = 0; stepped < 240; frame++)
                {
                    if (pauseAt >= 0 && frame >= pauseAt && frame < pauseAt + 100)
                    {
                        long b = k.BeatCount;
                        pausedBeats = pausedBeats < 0 ? b : pausedBeats == b ? b : -2;
                        continue;
                    }
                    for (int s = 0; s < perFrame && stepped < 240; s++)
                    {
                        StepN(k, 1);
                        stepped++;
                        for (long q = last; q < Subs(k); q++)
                        {
                            at.Add(stepped);
                        }
                        last = Subs(k);
                    }
                }
                return k.StateHash() ^ (ulong)k.BeatCount;
            }
            ulong h1 = RunAt(2, -1, out List<int> a1, out _), h05 = RunAt(1, -1, out List<int> a05, out _), h3 = RunAt(6, -1, out List<int> a3, out _);
            ulong hp = RunAt(2, 25, out List<int> ap, out long paused);
            Expect(h1 == h05 && h1 == h3 && a1.SequenceEqual(a05) && a1.SequenceEqual(a3) && a1.Count == 8,
                $"0.5x / 1x / 3x：哈希一致（{h05 == h1}/{h3 == h1}），出拍步号一致 [{string.Join(", ", a1)}]");
            Expect(hp == h1 && ap.SequenceEqual(a1) && paused == 1, $"运行中暂停 100 帧：暂停期间拍序号一直是 {paused}（不出拍），续跑后哈希与出拍步号与不暂停相同（{hp == h1}）");
        }

        // ── G. 存读档 ──

        private static void CheckSaveLoad()
        {
            Line("  · G. 存读档：段 24 拍序号与段 23 节拍锚点中途存档 → 新内核读档 → 续跑，拍序号接续、逐步哈希一致、放出份数相同");
            using var k = new CombatKernel(Cfg(), 16);
            int pf = Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1);
            int pm = Prog(k, new[] { ClockI(0, 2, 1) }, 0);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(pf), 2f, 10f)), 100f);
            int fa = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            int b = Unit(k, CombatFaction.Player, new double2(20, 0), k.AddWeapon(Instant(pm, 3f, CombatCarrier.Melee)), 100f);
            int fb = Unit(k, CombatFaction.Hostile, new double2(20, 1.6), -1, 5000f);
            k.FireAt(a, fa, k.Time);
            StepN(k, 40);
            k.FireAt(b, fb, k.Time);
            StepN(k, 35); // 第 75 步：拍序号 2，锚点还在
            long beatAt = k.BeatCount;
            long subsAt = Subs(k); // 总线计数是调试量、不进快照：比读档后的增量
            int runs = k.MetaRunCount;
            byte[] snap = k.Serialize();
            using var c = new CombatKernel(Cfg(), 16);
            CombatLoadResult lr = c.Load(snap);
            bool same = lr == CombatLoadResult.Ok && c.BeatCount == beatAt && c.MetaRunCount == runs && c.StateHash() == k.StateHash();
            bool every = true;
            for (int i = 0; i < 300; i++)
            {
                StepN(k, 1);
                StepN(c, 1);
                every &= k.StateHash() == c.StateHash() && k.BeatCount == c.BeatCount;
            }
            Expect(same && every && beatAt == 2 && runs == 1 && Subs(k) - subsAt == Subs(c) && Subs(c) > 0 && c.BeatCount == 12
                   && c.EmgBusStats.BeatSyncs > 0,
                $"存档时拍序号 {beatAt}、锚点 {runs} 个（读档 {lr}、读后一致 {same}）；续跑 300 步逐步哈希一致（{every}），拍序号接续到 {c.BeatCount}；读档后子弹药 {Subs(k) - subsAt}/{Subs(c)} 份");
            using var old = new CombatKernel(Cfg(), 16);
            CombatLoadResult r17 = old.Load(k.SerializeFormatForTests(17));
            StepN(old, 1);
            Expect(r17 == CombatLoadResult.Ok && old.BeatCount == (long)Math.Floor((old.Time + 1e-6) / 0.5),
                $"读 17 版快照（没有段 24）：{r17}，下一步直接接到当前拍 {old.BeatCount}（只出这一拍，不补发）");
        }

        // ── H. 空场景早退出 ──

        private static void CheckEmpty()
        {
            Line("  · H. 程序表为空 / 没有程序读 evt.beat：拍序号不动（StateHash 逐字不变）、不出事件");
            using var k = new CombatKernel(Cfg(), 8);
            Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(-1)), 100f);
            ulong h0 = k.StateHash();
            StepN(k, 120);
            using var k2 = new CombatKernel(Cfg(), 8);
            int prog = k2.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Hit }, new[] { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } },
                new[] { Row(0, Ev(EmgEventKind.Hit), Trig) });
            Unit(k2, CombatFaction.Player, new double2(0, 0), k2.AddWeapon(Instant(prog)), 100f);
            StepN(k2, 120);
            Expect(k.BeatCount == 0 && k2.BeatCount == 0 && k2.EmgBusStats.BeatsEmitted == 0 && h0 != 0UL,
                $"空程序表拍序号 {k.BeatCount}；只读 evt.hit 的程序拍序号 {k2.BeatCount}、发出 {k2.EmgBusStats.BeatsEmitted} 条");
        }

        // ── K. 耗时 ──

        private static void CheckPerformance()
        {
            Line("  · K. 耗时（警告口径，不设门禁）：64 台机器各带 [时钟脉冲, 时钟脉冲, 谐振] 的慢速弹满载场景，对照同场景不带节拍");
            double Run(bool tempo, out long beats)
            {
                using var k = new CombatKernel(Cfg(), 512);
                int prog = tempo ? Prog(k, new[] { ClockI(0, 2, 0), ClockI(1, 2, 0), ResI(2) }, 0, 1) : -1;
                var pairs = new List<(int, int)>();
                for (int i = 0; i < 64; i++)
                {
                    int me = Unit(k, CombatFaction.Player, new double2(i * 3, 0), k.AddWeapon(Flight(Instant(prog), 2f, 6f)), 100f);
                    int foe = Unit(k, CombatFaction.Hostile, new double2(i * 3, 20), -1, 100000f);
                    pairs.Add((me, foe));
                }
                var sw = new Stopwatch();
                int steps = 0;
                for (int round = 0; round < 5; round++)
                {
                    foreach ((int me, int foe) in pairs)
                    {
                        k.FireAt(me, foe, k.Time);
                    }
                    for (int s = 0; s < 60; s++)
                    {
                        sw.Start();
                        k.Step(Dt, k.Time + Dt);
                        sw.Stop();
                        steps++;
                    }
                }
                beats = k.EmgBusStats.BeatsEmitted;
                return sw.Elapsed.TotalMilliseconds / steps;
            }
            double base0 = Run(false, out _), ms = Run(true, out long beats);
            Expect(ms < 5.0 && beats > 0, $"节拍满载每步平均 {ms:0.000} 毫秒（发出 evt.beat {beats} 条）；同场景无程序 {base0:0.000} 毫秒；空场景 StepEmgBeat 一次位判断即返回");
        }

        // ── 断言 ──

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
