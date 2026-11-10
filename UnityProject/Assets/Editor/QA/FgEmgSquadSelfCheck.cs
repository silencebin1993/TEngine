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
    /// EMG3-SQD-01 跨机协同的自动验收（FG19 FGR-EMG-130～133、172 中继组、180 跨机链；FGT-EMG-051、FGT-EMG-053 中继组相位对齐部分、FGT-EMG-020 evt.ping 生产者）。
    /// 起真实表 / 编译器 / 预览（EmgCatalog、EmgProgramCompiler、EmgProgramTreeText）、真实地点（CombatSite.ApplyRelay、悬停）与真实内核（CombatKernel）断言行为：
    /// A 表与预览（端口接通、struct_relay 声明轨、固件自写 sqd.relay 被表校验拒绝、预览“可被中继转发”只标白名单 trigger）；B 中继转发（触发、热量记执行方、因果链“由中继转发：A 车命中”）；
    /// C 负向（白名单外、范围外、中继阵亡、没有中继）；D 限流（每秒 4 次、多余计数、链“限流”节点、下一秒恢复）；E 重叠与互为中继（同一事件每条指令一次、链深 3 兜底）；
    /// F 信号脉冲（频道、中继组、没有中继不送达）；G 中继组相位对齐（两台单节拍机器同组齐射、组外不齐射）；H 跨机留存（A 铺的斑块 B 点燃，链记两台）；
    /// I 地点与悬停（ApplyRelay 按声明登记、悬停计数、引导钩子名）；J 暂停与 0.5x～3x；K 存读档（段 25 计窗与计数接续、逐步哈希一致、读 17 版）；L 空场景早退出；M 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgSquadSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgSquadSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgSquadSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger, Cond = EmgConst.RoleCondition, RoleEmit = 5, RoleSpawn = 7;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/跨机协同")]
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
            Line("\n[跨机协同] EMG3-SQD-01：中继转发、信号脉冲、中继组相位对齐、跨机留存（FGT-EMG-051、FGT-EMG-053 中继组部分、FGT-EMG-020 evt.ping 生产者）");
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
                Step(CheckCatalog);
                Step(CheckForward);
                Step(CheckNegatives);
                Step(CheckThrottle);
                Step(CheckOverlap);
                Step(CheckPing);
                Step(CheckGroupSync);
                Step(CheckPersistence);
                Step(CheckSite);
                Step(CheckTiming);
                Step(CheckSaveLoad);
                Step(CheckEmpty);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"跨机协同自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [跨机协同] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 表、编译与预览 ──

        private static string TreeText(EmgProgramResult r)
        {
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows);
            return string.Join(" | ", rows.Select(x => x.Text));
        }

        private static EmgProgramResult Board(string structure, string fw)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, structure, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            return EmgProgramCompiler.Compile(p);
        }

        private static void CheckCatalog()
        {
            Line("  · A. 表与预览：sqd.relay / evt.ping 已接通、struct_relay 在声明轨；固件声明里自写 sqd.relay 被表校验拒绝；预览只在白名单 trigger 读端标“可被中继转发”");
            EmgCompiled c = EmgCatalog.Current;
            bool live = !EmgCatalog.IsPendingPort("sqd.relay", EmgRole.Scope) && !EmgCatalog.IsPendingPort("evt.ping", EmgRole.Trigger) && !EmgCatalog.IsPendingPort("evt.ping", EmgRole.Emit)
                        && EmgCatalog.IsOwnerOnDeclTrack(ComponentCatalog.StructRelayId)
                        && EmgCatalog.PartReads("sqd.relay", EmgRole.Scope, new[] { ComponentCatalog.StructRelayId })
                        && !EmgCatalog.PartReads("sqd.relay", EmgRole.Scope, new[] { "struct_armor" });
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            Expect(live && c.Problems.Count == 0 && Mathf.Approximately(cfg.EmgRelayRange, 12f) && cfg.EmgRelayPerSecond == 4,
                $"真实表：端口接通 {live}、struct_relay 声明 sqd.relay scope、struct_armor 没有；目录问题 {c.Problems.Count} 条；调参 emg.relay.range = {cfg.EmgRelayRange}、per_second = {cfg.EmgRelayPerSecond}");
            // 负向：固件自写 sqd.relay（scope / trigger 两种写法）被表校验拒绝，声明不进内核数组
            EmgSource src = EmgCatalog.FromTables();
            int realDecls = EmgCatalog.Compile(src).Decls.Length;
            EmgSource.Decl tpl = src.Decls.First(d => d.Owner == "fw_followup" && d.Seq == 1);
            EmgSource.Decl bad = FgEmergenceRuleAuditSelfCheck.CopyDecl(tpl, "fw_followup", 99);
            bad.Port = "sqd.relay";
            bad.Role = "scope";
            bad.Dir = "read";
            src.Decls.Add(bad);
            EmgCompiled cb = EmgCatalog.Compile(src);
            string why = cb.Problems.FirstOrDefault(p => p.Contains("fw_followup") && p.Contains("sqd.relay"));
            Expect(why != null && cb.Decls.Length < realDecls && !cb.OwnerIndex.ContainsKey("fw_followup"),
                $"固件 fw_followup 加一条 read sqd.relay scope：表校验拒绝——{why}；整条固件的声明不进内核数组（{cb.Decls.Length} < {realDecls}）");
            EmgSource src2 = EmgCatalog.FromTables();
            EmgSource.Decl relayDecl = src2.Decls.First(d => d.Owner == ComponentCatalog.StructRelayId);
            relayDecl.Role = "trigger";
            EmgCompiled c2 = EmgCatalog.Compile(src2);
            Expect(c2.Problems.Any(p => p.Contains(ComponentCatalog.StructRelayId) && p.Contains("sqd.relay")),
                $"对照：struct_relay 把 sqd.relay 写成 trigger 同样被拒（只认 scope）：{c2.Problems.FirstOrDefault(p => p.Contains(ComponentCatalog.StructRelayId))}");
            // 预览：装中继的车上，[追射]（evt.hit trigger）标“可被中继转发”；[蜂群]（evt.fire trigger）不标；不装中继不标
            EmgProgramResult relayHit = Board(ComponentCatalog.StructRelayId, "fw_followup");
            EmgProgramResult plainHit = Board(null, "fw_followup");
            EmgProgramResult relayFire = Board(ComponentCatalog.StructRelayId, "fw_swarm");
            string tag = GameText.Get("emg.tree.tag.relay");
            string t1 = TreeText(relayHit), t0 = TreeText(plainHit), tf = TreeText(relayFire);
            Expect(relayHit.Relay && !plainHit.Relay && t1.Contains(tag) && !t0.Contains(tag) && !tf.Contains(tag) && !t1.Contains("emg.tree"),
                $"预览：装中继 [追射] 标“{tag}”{t1.Contains(tag)}；不装中继 {t0.Contains(tag)}；装中继 [蜂群]（evt.fire）{tf.Contains(tag)}");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = TreeText(relayHit) + GameText.Get("emg.chain.node.relay") + GameText.Get("emg.relay.hover") + GameText.Get("emg.chain.node.ping") + GameText.Get("emg.chain.node.relay_capped");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en.Contains("relayable") && !en.Contains("emg."), "英文文本无缺键（relayable / Relayed / Ping / throttled）");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstRelay) && GuidanceHooks.EmgFirstRelay == "emg_first_relay", "引导钩子 emg_first_relay 已登记（内容归 FG15-UX-04）");
        }

        // ── 内核夹具 ──

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgSiteSeed = 12345u;
            c.EmgBeatSeconds = 0.5f;
            c.EmgRelayRange = 12f;
            c.EmgRelayPerSecond = 4;
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

        private static CombatWeapon Instant(int program, float heat = 0f, float overheatAt = 0f, float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f, Program = program,
            HeatPerShot = heat, OverheatAt = overheatAt, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
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

        private static uint Bit(EmgEventKind e) => 1u << (int)e;

        /// <summary>“事件 → 放 1 份子载荷”的程序：根节点每条指令 = 读 <paramref name="evs"/>[i] 的 trigger（幅度 = 频道）+ 放子载荷，每条一个子节点。</summary>
        internal static int TrigProg(CombatKernel k, float channel, params EmgEventKind[] evs)
        {
            var instrs = new List<EmgInstr>();
            var nodes = new List<EmgNode> { default };
            uint mask = Bit(EmgEventKind.Fire);
            for (int i = 0; i < evs.Length; i++)
            {
                instrs.Add(Row(i, Ev(evs[i]), Trig, channel));
                instrs.Add(Row(i, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload));
                mask |= Bit(evs[i]);
            }
            nodes[0] = new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)instrs.Count };
            for (int i = 0; i < evs.Length; i++)
            {
                nodes.Add(new EmgNode { Parent = 0, Gen = 1, TrigEvent = evs[i], TrigCount = 1, FirstInstr = instrs.Count, InstrCount = 0, MetaMask = (byte)(i + 1) });
            }
            return k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
        }

        /// <summary>“命中时广播 <paramref name="channel"/> 号频道脉冲”的程序（与 N30～N32 同形：事件 trigger + evt.ping emit）。</summary>
        internal static int PingProg(CombatKernel k, int channel) =>
            k.AddProgram(new EmgProgram { ListenMask = Bit(EmgEventKind.Hit) | Bit(EmgEventKind.Fire) }, new[] { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 2 } },
                new[] { Row(0, Ev(EmgEventKind.Hit), Trig), Row(0, Ev(EmgEventKind.Ping), RoleEmit, channel) });

        private static float HeatOf(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Heat : -1f;

        private static bool FindRelayRec(CombatKernel k, byte aux, out EmgChainRec rec)
        {
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out rec) && rec.Kind == EmgChainNode.Relay && rec.Aux == aux)
                {
                    return true;
                }
            }
            rec = default;
            return false;
        }

        /// <summary>A（没有程序的机枪）在 (0,0)、中继机器 B 在 <paramref name="bAt"/>（程序：读 <paramref name="bReads"/> 的 trigger → 放 1 份子载荷，每发积热 5），敌人在 (0,6)。</summary>
        private static CombatKernel Pair(double2 bAt, bool relay, out int a, out int b, out int foe, int aProg = -1, params EmgEventKind[] bReads)
        {
            var k = new CombatKernel(Cfg(), 32);
            int pb = TrigProg(k, 0f, bReads.Length == 0 ? new[] { EmgEventKind.Hit } : bReads);
            a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(aProg)), 100f);
            b = Unit(k, CombatFaction.Player, bAt, k.AddWeapon(Instant(pb, 5f, 100f)), 100f);
            foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
            if (relay)
            {
                k.SetUnitRelay(b, true);
            }
            return k;
        }

        // ── B. 中继转发（FGT-EMG-051）──

        private static void CheckForward()
        {
            Line("  · B. 中继转发：没有程序的 A 车命中 → 5 米外装中继的 B 车“命中时放子载荷”执行一次（宿主 = B，积 B 一发的热量）；因果链记“由中继转发：A 车命中（B 车执行）”");
            using CombatKernel k = Pair(new double2(5, 0), true, out int a, out int b, out int foe);
            k.FireAt(a, foe, k.Time);
            StepN(k, 1);
            k.TryGetRelay(b, out EmgRelay rl);
            bool rec = FindRelayRec(k, EmgSquadConst.AuxForward, out EmgChainRec r);
            Expect(k.EmgBusStats.RelayForwarded == 1 && Subs(k) == 1 && rl.Forwarded == 1 && rl.Dropped == 0 && Mathf.Approximately(HeatOf(k, b), 5f) && HeatOf(k, a) == 0f,
                $"转发 {k.EmgBusStats.RelayForwarded} 次、B 放出子载荷 {Subs(k)} 份；B 积热 {HeatOf(k, b)}（执行方一发 5）、A 积热 {HeatOf(k, a)}");
            Expect(rec && r.SrcUnit == a && r.Other == b && r.Key == (int)EmgEventKind.Hit && r.Chain > 0,
                $"因果链节点：种类 Relay、源 = A {r.SrcUnit == a}、执行方 = B {r.Other == b}、事件 = 命中 {r.Key == (int)EmgEventKind.Hit}、A 没有程序时转发开新链 {r.Chain}");
            string text = EmgChainView.RelayNodeText(null, null, r);
            string expect = GameText.Format("emg.chain.node.relay", EmgChainView.PartyName(null, null, a), EmgProgramTreeText.EventName(EmgEventKind.Hit), EmgChainView.PartyName(null, null, b));
            Expect(text == expect && text.StartsWith("由中继转发：", StringComparison.Ordinal) && text.Contains(EmgProgramTreeText.EventName(EmgEventKind.Hit)),
                $"链文本：“{text}”（FGR-BASE-020：由中继转发：A 车命中）");
            // 白名单里的被击 / 击杀：敌人打 A → B“被击时”执行；A 击毁 5 血的敌人 → B“击杀时”执行（各 1 次，不读命中的 B 不因命中执行）
            using (CombatKernel kh = Pair(new double2(5, 0), true, out int ah, out int bh, out int fh, -1, EmgEventKind.Hurt))
            {
                kh.Damage(ah, 5f, fh);
                StepN(kh, 1);
                using CombatKernel kk = Pair(new double2(5, 0), true, out int ak, out int bk, out _, -1, EmgEventKind.Kill);
                int weak = Unit(kk, CombatFaction.Hostile, new double2(0, 3), -1, 5f);
                kk.FireAt(ak, weak, kk.Time);
                StepN(kk, 1);
                Expect(kh.EmgBusStats.RelayForwarded == 1 && Subs(kh) == 1 && kk.EmgBusStats.RelayForwarded == 1 && Subs(kk) == 1,
                    $"白名单：A 被击 → B 被击时执行 {kh.EmgBusStats.RelayForwarded} 次、放 {Subs(kh)} 份；A 击杀 → B 击杀时执行 {kk.EmgBusStats.RelayForwarded} 次、放 {Subs(kk)} 份（命中不转给只读击杀的 B）");
            }
            // 只有一道信号：B 只跑自己装的程序——A 有自己的程序（命中时放子载荷）时，A 的那份由 A 放、B 的那份由 B 放，各 1 份，程序不被搬运
            using var k2 = new CombatKernel(Cfg(), 32);
            int pa = TrigProg(k2, 0f, EmgEventKind.Hit);
            int pb = TrigProg(k2, 0f, EmgEventKind.Kill);
            int a2 = Unit(k2, CombatFaction.Player, new double2(0, 0), k2.AddWeapon(Instant(pa)), 100f);
            int b2 = Unit(k2, CombatFaction.Player, new double2(5, 0), k2.AddWeapon(Instant(pb)), 100f);
            int f2 = Unit(k2, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
            k2.SetUnitRelay(b2, true);
            k2.FireAt(a2, f2, k2.Time);
            StepN(k2, 1);
            Expect(Subs(k2) == 1 && k2.EmgBusStats.RelayForwarded == 0,
                $"只转发事件不转发程序：A 带“命中时放子载荷”、B 只读击杀 → A 命中只放 A 自己的 1 份（{Subs(k2)}），B 不执行 A 的程序（转发 {k2.EmgBusStats.RelayForwarded}）");
        }

        // ── C. 负向 ──

        private static void CheckNegatives()
        {
            Line("  · C. 负向：白名单外（evt.fire / evt.overheat）不转发；范围外（13 米）不转发、11.9 米转发；中继阵亡立即停止；没装中继不转发");
            // A 自己有读 evt.fire 的程序（开火事件真的产生），A 每发积热 200 → 过热（evt.overheat 真的产生）；B 读 evt.fire / evt.overheat 的 trigger
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int pa = TrigProg(k, 0f, EmgEventKind.Fire, EmgEventKind.Overheat);
                int pb = TrigProg(k, 0f, EmgEventKind.Fire, EmgEventKind.Overheat);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pa, 200f, 100f)), 100f);
                int b = Unit(k, CombatFaction.Player, new double2(5, 0), k.AddWeapon(Instant(pb)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
                k.SetUnitRelay(b, true);
                k.FireAt(a, foe, k.Time);
                StepN(k, 2);
                bool overheated = k.EmgEnqueuedOf(EmgEventKind.Overheat) > 0 && k.EmgEnqueuedOf(EmgEventKind.Fire) > 0;
                Expect(overheated && k.EmgBusStats.RelayForwarded == 0 && Subs(k) == 2,
                    $"A 开火 + 过热（事件真的产生 {overheated}）：B 的 evt.fire / evt.overheat 读端不被转发（转发 {k.EmgBusStats.RelayForwarded}），只有 A 自己的开火 / 过热子载荷 {Subs(k)} 份");
            }
            long Forwarded(double2 at, bool relay, bool killB)
            {
                using CombatKernel k = Pair(at, relay, out int a, out int b, out int foe);
                if (killB)
                {
                    k.Kill(b, 0);
                }
                k.FireAt(a, foe, k.Time);
                StepN(k, 1);
                return k.EmgBusStats.RelayForwarded + Subs(k);
            }
            long far = Forwarded(new double2(13, 0), true, false), near = Forwarded(new double2(11.9, 0), true, false);
            long dead = Forwarded(new double2(5, 0), true, true), none = Forwarded(new double2(5, 0), false, false);
            Expect(far == 0 && near == 2 && dead == 0 && none == 0,
                $"13 米 {far}、11.9 米 {near}（转发 1 + 子载荷 1）；中继 B 阵亡后 {dead}；没装中继 {none}");
            // 运行中阵亡：转发过一次后 B 被击毁，下一次命中立即不再转发
            using (CombatKernel k = Pair(new double2(5, 0), true, out int a, out int b, out int foe))
            {
                k.FireAt(a, foe, k.Time);
                StepN(k, 1);
                long before = k.EmgBusStats.RelayForwarded;
                k.Kill(b, 0);
                k.FireAt(a, foe, k.Time);
                StepN(k, 1);
                Expect(before == 1 && k.EmgBusStats.RelayForwarded == 1, $"转发 {before} 次后 B 阵亡：下一次命中转发数仍是 {k.EmgBusStats.RelayForwarded}（立即停止）");
            }
        }

        // ── D. 限流 ──

        private static void CheckThrottle()
        {
            Line("  · D. 限流：同一模拟秒里 A 命中 6 次 → B 只执行 4 次，多余 2 次计数可见并在因果链记“中继限流”；下一秒恢复");
            using CombatKernel k = Pair(new double2(5, 0), true, out int a, out int b, out int foe);
            for (int i = 0; i < 6; i++)
            {
                k.FireAt(a, foe, k.Time);
                StepN(k, 2);
            }
            k.TryGetRelay(b, out EmgRelay r1);
            bool capRec = FindRelayRec(k, EmgSquadConst.AuxCapped, out EmgChainRec cr);
            string capText = EmgChainView.RelayNodeText(null, null, cr);
            Expect(r1.Forwarded == 4 && r1.Dropped == 2 && k.EmgBusStats.RelayCapped == 2 && Mathf.Approximately(HeatOf(k, b), 20f),
                $"0.2 秒内命中 6 次：转发 {r1.Forwarded}、限流未转发 {r1.Dropped}（总线计数 {k.EmgBusStats.RelayCapped}）；B 积热 {HeatOf(k, b)}（只按执行的 4 次）");
            Expect(capRec && cr.Other == b && capText.Contains("4") && capText.StartsWith("中继限流", StringComparison.Ordinal), $"限流原因文本：“{capText}”");
            StepN(k, 60);
            k.FireAt(a, foe, k.Time);
            StepN(k, 1);
            k.TryGetRelay(b, out EmgRelay r2);
            Expect(r2.Forwarded == 5 && r2.Dropped == 2, $"下一个模拟秒：再命中转发 {r2.Forwarded - r1.Forwarded} 次（恢复）");
        }

        // ── E. 重叠与互为中继 ──

        private static void CheckOverlap()
        {
            Line("  · E. 两台中继范围重叠：一次命中每台中继各执行自己的指令一次（不重复）；A、B 互为中继：一次命中不形成无限转发（链深 3 与限流兜底）");
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int pb = TrigProg(k, 0f, EmgEventKind.Hit);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(-1)), 100f);
                int b = Unit(k, CombatFaction.Player, new double2(4, 0), k.AddWeapon(Instant(pb)), 100f);
                int c = Unit(k, CombatFaction.Player, new double2(-4, 0), k.AddWeapon(Instant(pb)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
                k.SetUnitRelay(b, true);
                k.SetUnitRelay(c, true);
                k.FireAt(a, foe, k.Time);
                k.TryGetRelay(b, out EmgRelay rb);
                k.TryGetRelay(c, out EmgRelay rc);
                int pending = k.EmgBusPending; // 转发在命中当场入队（A 没有程序：队里只有 B、C 各一条）
                StepN(k, 1);
                Expect(rb.Forwarded == 1 && rc.Forwarded == 1 && pending == 2 && Subs(k) == 2 && k.EmgBusStats.RelayForwarded == 2,
                    $"B、C 范围重叠：一次命中 B 执行 {rb.Forwarded} 次、C 执行 {rc.Forwarded} 次、入队 {pending} 条、共放 {Subs(k)} 份（每条指令一次）");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int p = TrigProg(k, 0f, EmgEventKind.Hit);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(p)), 100f);
                int b = Unit(k, CombatFaction.Player, new double2(3, 0), k.AddWeapon(Instant(p)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
                Unit(k, CombatFaction.Hostile, new double2(1.5, 6.5), -1, 100000f); // 5 米内另一个敌人：命中触发的子载荷朝它飞、真的再命中（链才会往下长）
                k.SetUnitRelay(a, true);
                k.SetUnitRelay(b, true);
                k.FireAt(a, foe, k.Time);
                StepN(k, 300);
                long fwd = k.EmgBusStats.RelayForwarded;
                int maxDepth = 0;
                for (int i = 0; i < k.EmgChainRecordCount; i++)
                {
                    if (k.TryGetEmgChainRecent(i, out EmgChainRec r))
                    {
                        maxDepth = Math.Max(maxDepth, r.Depth);
                    }
                }
                Expect(fwd >= 1 && fwd <= 8 && k.EmgBusPending == 0 && maxDepth <= EmgConst.MaxDepth && k.EmgBusStats.DepthCut > 0,
                    $"A、B 互为中继、都带“命中时放子载荷”：A 命中一次后 5 秒内转发 {fwd} 次（有限）、总线清空 {k.EmgBusPending == 0}、链深最大 {maxDepth}、链深截断 {k.EmgBusStats.DepthCut} 次");
            }
        }

        // ── F. 信号脉冲（FGR-EMG-133，FGT-EMG-020 evt.ping 生产者）──

        private static void CheckPing()
        {
            Line("  · F. 信号脉冲：A 命中时广播 1 号频道；同一中继组里收听 1 号频道的 R 被触发（放 1 份子载荷、链记“信号脉冲”）；频道不符 / 没有中继 / 组外不送达；中间一台中继把 20 米外的 R 连进组");
            (long sent, long got, long subs, bool rec) Run(double2 rAt, int listen, bool aRelay, double2? mid)
            {
                using var k = new CombatKernel(Cfg(), 32);
                int pa = PingProg(k, 1);
                int pr = TrigProg(k, listen, EmgEventKind.Ping);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pa)), 100f);
                int r = Unit(k, CombatFaction.Player, rAt, k.AddWeapon(Instant(pr)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
                if (aRelay)
                {
                    k.SetUnitRelay(a, true);
                }
                if (mid.HasValue)
                {
                    int m = Unit(k, CombatFaction.Player, mid.Value, k.AddWeapon(Instant(-1)), 100f);
                    k.SetUnitRelay(m, true);
                }
                k.FireAt(a, foe, k.Time);
                StepN(k, 1);
                bool found = FindRelayRec(k, (byte)(EmgSquadConst.AuxPing + 1), out EmgChainRec cr) && cr.SrcUnit == a && cr.Other == r;
                return (k.EmgBusStats.PingsSent, k.EmgBusStats.PingsDelivered, Subs(k), found);
            }
            var ok = Run(new double2(6, 0), 1, true, null);
            var wrong = Run(new double2(6, 0), 2, true, null);
            var none = Run(new double2(6, 0), 1, false, null);
            var far = Run(new double2(20, 0), 1, true, null);
            var bridged = Run(new double2(20, 0), 1, false, new double2(10, 0));
            Expect(ok.sent == 1 && ok.got == 1 && ok.subs == 1 && ok.rec, $"同组收听 1 号频道：广播 {ok.sent}、送达 {ok.got}、R 放出 {ok.subs} 份、链节点 {ok.rec}");
            Expect(wrong.sent == 1 && wrong.got == 0 && wrong.subs == 0 && none.sent == 1 && none.got == 0 && far.got == 0,
                $"R 收听 2 号频道：送达 {wrong.got}；没有中继：广播 {none.sent}、送达 {none.got}；R 在 20 米外（A 是中继）：送达 {far.got}");
            Expect(bridged.got == 1 && bridged.subs == 1, $"中间 10 米处一台中继把 A 与 20 米外的 R 连成一组：送达 {bridged.got}、R 放出 {bridged.subs} 份");
            // 子弹药节点上的脉冲写端（程序 [命中时放子载荷 → 子载荷命中时广播 1 号频道]，与 [追射, 击杀回传] 这类排在触发器后的写法同形）：根节点没有脉冲，送达只可能来自子弹药
            using (var k = new CombatKernel(Cfg(), 32))
            {
                var instrs = new List<EmgInstr>
                {
                    Row(0, Ev(EmgEventKind.Hit), Trig), Row(0, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload),
                    Row(1, Ev(EmgEventKind.Hit), Trig), Row(1, Ev(EmgEventKind.Ping), RoleEmit, 1f),
                };
                var nodes = new List<EmgNode>
                {
                    new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 2 },
                    new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Hit, TrigCount = 1, FirstInstr = 2, InstrCount = 2, MetaMask = 1 },
                };
                int pa = k.AddProgram(new EmgProgram { ListenMask = Bit(EmgEventKind.Hit) | Bit(EmgEventKind.Fire) }, nodes, instrs);
                int pr = TrigProg(k, 1f, EmgEventKind.Ping);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pa)), 100f);
                Unit(k, CombatFaction.Player, new double2(6, 0), k.AddWeapon(Instant(pr)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
                Unit(k, CombatFaction.Hostile, new double2(1.5, 6.5), -1, 100000f);
                k.SetUnitRelay(a, true);
                k.FireAt(a, foe, k.Time);
                long rootSent = k.EmgBusStats.PingsSent;
                StepN(k, 120);
                Expect(rootSent == 0 && k.EmgBusStats.PingsSent >= 1 && k.EmgBusStats.PingsDelivered >= 1,
                    $"子弹药节点上的脉冲：A 命中当场广播 {rootSent} 次（根节点没有脉冲），子弹药命中后广播 {k.EmgBusStats.PingsSent} 次、送达 {k.EmgBusStats.PingsDelivered} 次");
            }
            string text = EmgChainView.RelayNodeText(null, null, new EmgChainRec { Kind = EmgChainNode.Relay, Aux = (byte)(EmgSquadConst.AuxPing + 1) });
            Expect(text.StartsWith("信号脉冲", StringComparison.Ordinal) && text.Contains("1"), $"链文本：“{text}”");
        }

        // ── G. 中继组相位对齐（FGT-EMG-053 中继组部分，DEBT-EMG3TMP01-02）──

        private static EmgInstr[] ClockI(int ord) => new[]
        {
            Row(ord, Ev(EmgEventKind.Beat), Trig), Row(ord, EmgTempoConst.PortBeat, Cond, 2f, 0f), Row(ord, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload),
        };

        private static EmgInstr[] SyncTrigI(int ord) => new[]
        {
            Row(ord, Ev(EmgEventKind.Beat), Trig), Row(ord, EmgTempoConst.PortSync, Cond), Row(ord, 100, RoleSpawn, 1f, 0f, (byte)EmgVerb.SubPayload),
        };

        /// <summary>[时钟脉冲, 齐射触发器]：一台机器只有一条节拍指令，单机永远不对齐。</summary>
        private static int BeatProg(CombatKernel k)
        {
            var instrs = ClockI(0).Concat(SyncTrigI(1)).ToList();
            var nodes = new List<EmgNode>
            {
                new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)instrs.Count },
                new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Beat, TrigCount = 1, FirstInstr = instrs.Count, InstrCount = 0, MetaMask = 1 },
                new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Beat, TrigCount = 1, FirstInstr = instrs.Count, InstrCount = 0, MetaMask = 2 },
            };
            return k.AddProgram(new EmgProgram { ListenMask = Bit(EmgEventKind.Beat) | Bit(EmgEventKind.Fire) }, nodes, instrs);
        }

        private static long GroupRun(double2 yAt, bool relay, out long syncs)
        {
            using var k = new CombatKernel(Cfg(), 32);
            int prog = BeatProg(k);
            int x = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
            int y = Unit(k, CombatFaction.Player, yAt, k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
            int fx = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 5000f);
            int fy = Unit(k, CombatFaction.Hostile, yAt + new double2(0, 25), -1, 5000f);
            if (relay)
            {
                k.SetUnitRelay(x, true);
            }
            k.FireAt(x, fx, k.Time);
            k.FireAt(y, fy, k.Time);
            StepN(k, 252);
            syncs = k.EmgBusStats.BeatSyncs;
            return Subs(k);
        }

        private static void CheckGroupSync()
        {
            Line("  · G. 中继组相位对齐：两台各只有一条节拍指令的机器（单机不对齐），同在一台中继的范围内 → 同拍置 tmp.sync，各自的齐射触发器放出；组外 / 没有中继不对齐");
            long grouped = GroupRun(new double2(6, 0), true, out long gs);
            long alone = GroupRun(new double2(6, 0), false, out long ns);
            long apart = GroupRun(new double2(30, 0), true, out long fs);
            Expect(grouped == 16 && gs == 8 && alone == 8 && ns == 0 && apart == 8 && fs == 0,
                $"同组：放出 {grouped} 份（= 2 台 × 4 拍 × (时钟 1 + 齐射 1)）、对齐 {gs} 次；没有中继：{alone} 份、对齐 {ns} 次；Y 在 30 米外（组外）：{apart} 份、对齐 {fs} 次");
        }

        // ── H. 跨机留存（FGR-EMG-130 / 131 / 180）──

        private static void CheckPersistence()
        {
            Line("  · H. 跨机：A 铺的东西留在世界里，A 离场（阵亡）后 B 的载荷照样读到；不靠中继（世界状态任何机器都能读），因果链里同时有 A、B 两次出手");
            using var k = new CombatKernel(Cfg(), 32);
            int pb = TrigProg(k, 0f, EmgEventKind.Hit);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pb)), 100f);
            int b = Unit(k, CombatFaction.Player, new double2(3, 0), k.AddWeapon(Instant(pb)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 100000f);
            k.FireAt(a, foe, k.Time);
            StepN(k, 1);
            long afterA = Subs(k);
            k.Kill(a, 0);
            k.FireAt(b, foe, k.Time);
            StepN(k, 1);
            var shooters = new HashSet<int>();
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Kind == EmgChainNode.Shot)
                {
                    shooters.Add(r.SrcUnit);
                }
            }
            Expect(afterA == 1 && Subs(k) >= 2 && shooters.Contains(a) && shooters.Contains(b) && k.EmgBusStats.RelayForwarded == 0,
                $"A 放出 {afterA} 份后阵亡，A 的子载荷继续按发射时程序飞（总 {Subs(k)} 份）；链记录里出手者含 A {shooters.Contains(a)}、B {shooters.Contains(b)}（没有中继、转发 {k.EmgBusStats.RelayForwarded}）");
            Line("    · 斑块 / 残骸 / 相态的跨机读取与按链平分伤害由 FgEmgMatterSelfCheck、FgEmgDiscoverySelfCheck（FGR-EMG-182，A 铺 B 点归属两台）覆盖，本段只核对转发与链");
        }

        // ── I. 地点与悬停 ──

        private static void CheckSite()
        {
            Line("  · I. 地点：CombatSite.ApplyRelay 按结构模块的 sqd.relay 声明登记（不认模块 ID），换成别的结构模块即撤销；中继机器悬停写范围、已转发 / 限流未转发次数");
            using var site = new CombatSite("selfcheck_squad", CombatSite.ConfigFromTuning());
            int u = site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live,
                Position = new double2(1, 1), Home = new double2(1, 1), Radius = 0.5f, Health = 100f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            site.ApplyRelay(u, ComponentCatalog.StructRelayId);
            bool on = site.Kernel.TryGetRelay(u, out _);
            string hover = EmgChainView.RelayHoverLine(site, u);
            site.ApplyRelay(u, "struct_armor");
            bool off = !site.Kernel.TryGetRelay(u, out _) && EmgChainView.RelayHoverLine(site, u) == null;
            Expect(on && off && hover != null && hover.Contains("12") && hover.Contains("0 次") && hover.Contains("4"),
                $"装 struct_relay 登记 {on}、换装甲撤销 {off}；悬停：“{hover}”");
        }

        // ── J. 暂停与倍速 ──

        private static void CheckTiming()
        {
            Line("  · J. 暂停与 0.5x～3x：同一模拟时间里 A 每 10 步命中一次（限流按模拟秒计窗），每帧 1 / 2 / 6 步与中途暂停 100 帧，哈希与转发 / 限流计数一致");
            (ulong h, long fwd, long drop) RunAt(int perFrame, int pauseAt)
            {
                using CombatKernel k = Pair(new double2(5, 0), true, out int a, out int b, out int foe);
                int stepped = 0, frame = 0;
                while (stepped < 240)
                {
                    frame++;
                    if (pauseAt > 0 && frame >= pauseAt && frame < pauseAt + 100)
                    {
                        continue; // 暂停：不调 Step
                    }
                    for (int s = 0; s < perFrame && stepped < 240; s++)
                    {
                        if (stepped % 10 == 0)
                        {
                            k.FireAt(a, foe, k.Time);
                        }
                        StepN(k, 1);
                        stepped++;
                    }
                }
                k.TryGetRelay(b, out EmgRelay r);
                return (k.StateHash(), r.Forwarded, r.Dropped);
            }
            var x1 = RunAt(2, -1);
            var x05 = RunAt(1, -1);
            var x3 = RunAt(6, -1);
            var xp = RunAt(2, 20);
            Expect(x1 == x05 && x1 == x3 && x1 == xp && x1.fwd == 16 && x1.drop == 8,
                $"0.5x / 1x / 3x / 暂停：哈希与计数一致（{x05 == x1}/{x3 == x1}/{xp == x1}）；4 秒 24 次命中 → 转发 {x1.fwd}（每秒 4）、限流 {x1.drop}；不观察时内核照同一步序推进，结果相同");
        }

        // ── K. 存读档 ──

        private static void CheckSaveLoad()
        {
            Line("  · K. 存读档：本秒已转发 3 次时存档 → 新内核读档 → 两边各再命中 2 次：都只再转发 1 次、限流 1 次；逐步哈希一致；读 17 版快照（没有段 25）= 没有中继");
            using CombatKernel k = Pair(new double2(5, 0), true, out int a, out int b, out int foe);
            for (int i = 0; i < 3; i++)
            {
                k.FireAt(a, foe, k.Time);
                StepN(k, 2);
            }
            byte[] snap = k.Serialize();
            using var c = new CombatKernel(Cfg(), 32);
            CombatLoadResult lr = c.Load(snap);
            bool same = lr == CombatLoadResult.Ok && c.StateHash() == k.StateHash() && c.RelayCount == 1;
            bool every = true;
            for (int i = 0; i < 2; i++)
            {
                k.FireAt(a, foe, k.Time);
                c.FireAt(a, foe, c.Time);
                StepN(k, 2);
                StepN(c, 2);
                every &= k.StateHash() == c.StateHash();
            }
            for (int i = 0; i < 120; i++)
            {
                StepN(k, 1);
                StepN(c, 1);
                every &= k.StateHash() == c.StateHash();
            }
            k.TryGetRelay(b, out EmgRelay rk);
            c.TryGetRelay(b, out EmgRelay rc);
            Expect(same && every && rk.Forwarded == 4 && rk.Dropped == 1 && rc.Forwarded == rk.Forwarded && rc.Dropped == rk.Dropped && rc.WinSec == rk.WinSec,
                $"读档 {lr}、读后一致 {same}；续跑逐步哈希一致 {every}；原内核 转发 {rk.Forwarded} / 限流 {rk.Dropped}，读档内核 {rc.Forwarded} / {rc.Dropped}（计窗接续）");
            using var old = new CombatKernel(Cfg(), 32);
            CombatLoadResult r17 = old.Load(k.SerializeFormatForTests(17));
            Expect(r17 == CombatLoadResult.Ok && old.RelayCount == 0, $"读 17 版快照：{r17}，中继 {old.RelayCount} 台（读档后由热更层按装配重新登记）");
        }

        // ── L. 空场景早退出 ──

        private static void CheckEmpty()
        {
            Line("  · L. 没有中继机器：转发入口一条比较返回，哈希与从未登记过中继的内核逐字相同（登记后再撤销也相同）");
            ulong Run(bool toggle, out long fwd)
            {
                using CombatKernel k = Pair(new double2(5, 0), false, out int a, out int b, out int foe);
                if (toggle)
                {
                    k.SetUnitRelay(b, true);
                    k.SetUnitRelay(b, false);
                }
                for (int i = 0; i < 6; i++)
                {
                    k.FireAt(a, foe, k.Time);
                    StepN(k, 10);
                }
                fwd = k.EmgBusStats.RelayForwarded + k.EmgBusStats.PingsDelivered;
                return k.StateHash();
            }
            ulong h0 = Run(false, out long f0), h1 = Run(true, out long f1);
            Expect(h0 == h1 && f0 == 0 && f1 == 0, $"从未登记 / 登记后撤销：哈希一致 {h0 == h1}，转发 {f0}/{f1}");
        }

        // ── M. 耗时 ──

        private static void CheckPerformance()
        {
            Line("  · M. 耗时（警告口径，不设门禁）：120 台机器（其中 8 台中继、都带“命中时放子载荷”）轮番开火，对照同场景没有中继");
            double Run(bool relays, out long fwd)
            {
                using var k = new CombatKernel(Cfg(), 512);
                int prog = TrigProg(k, 0f, EmgEventKind.Hit);
                var pairs = new List<(int, int)>();
                for (int i = 0; i < 120; i++)
                {
                    int me = Unit(k, CombatFaction.Player, new double2(i * 2, 0), k.AddWeapon(Instant(prog)), 100f);
                    int foe = Unit(k, CombatFaction.Hostile, new double2(i * 2, 8), -1, 1e7f);
                    pairs.Add((me, foe));
                    if (relays && i % 15 == 7)
                    {
                        k.SetUnitRelay(me, true);
                    }
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
                fwd = k.EmgBusStats.RelayForwarded;
                return sw.Elapsed.TotalMilliseconds / steps;
            }
            double base0 = Run(false, out _), ms = Run(true, out long fwd);
            Expect(ms < 5.0 && fwd > 0, $"8 台中继满载每步平均 {ms:0.000} 毫秒（转发 {fwd} 次，开火在步外）；同场景没有中继 {base0:0.000} 毫秒");
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
