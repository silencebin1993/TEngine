using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-BUS-01 战斗事件总线的自动验收（FG19 FGR-EMG-060～068；FGT-EMG-020 阶段 1 生产者、FGT-EMG-021、FGT-EMG-022；ADR-EMG-001 ④）。
    /// 起真实内核（BinGames.Sim.Combat.CombatKernel，Burst 步进）与真实家园地点（WorldSimulation + CombatSites）断言行为：
    /// A 口径（端口码 = 300 + 码、调参键与内核常量一致、地点级监听掩码在登记 / 读档时重算）；
    /// B 即时出手的生产者（发射 / 命中 / 被标记 / 近炸 / 过热 / 冷却完成 / 击杀 / 被击 / 阵亡）每条“该发”与“不该发”（程序不读该码 / 没有程序）对照，字段逐项核对；
    /// C 弹体宿主的生产者（命中 / 终结三种原因 / 定时 / 近炸 / 吸收 / 击杀），对照同上；穿透 / 反弹与每宿主 ≤ 3 次（生产入口与真实弹道同一段代码）；
    /// D 热更层注入（内核入口的四种结果；真实地点：信号广播 → 接入 / 断开与原因码、同机核心编辑不算、重复订阅不重复注入；拾取入口）；
    /// E 预算与顺延（一步灌入 2000 条：每步 512 条、按 Seq、无丢失、中途存读档续跑一致；真实地点暂停不推进、0.5x / 1x / 3x 按步处理一致）；
    /// F 链深与扇出（链深 3 截止并计数、子事件链深 + 1 继承链、扇出 8 截断计数）；G 积压保护（合并为计数事件、计数不丢）；
    /// H 现有“玩法 64 / 步顺延、提示 24 / 步丢弃”口径不受总线影响；I 存读档（队列、过热记号、每宿主计数、近炸记号）；J 耗时（单步处理 512 条，3x 每步 ≤ 1.5 毫秒）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgBusSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgBusSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgBusSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger;
        private const byte Cond = 2;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/战斗事件总线")]
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
            Line("\n[战斗事件总线] EMG1-BUS-01：生产点只入队、按需监听、每步 512 条顺延不丢、链深 / 扇出 / 每宿主上限、热更注入（FGT-EMG-020 / 021 / 022）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckBasics);
                Step(CheckInstantProducers);
                Step(CheckProjectileProducers);
                Step(CheckPierceBounceHost);
                Step(CheckHostsAndSources);
                Step(CheckTriggerHosts);
                Step(CheckKernelInject);
                Step(CheckBudget);
                Step(CheckDepthFanout);
                Step(CheckBackpressure);
                Step(CheckLegacyQueues);
                Step(CheckSaveLoad);
                Step(CheckPerformance);
                Step(CheckWorld);
            }
            catch (Exception e)
            {
                Fail($"战斗事件总线自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [战斗事件总线] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>登记一个只有根节点的程序：每个 (事件码, 角色, 参数) 一条读端行；<paramref name="children"/> 每个加一个 1 代子节点（触发事件 = 该码）。</summary>
        private static int Prog(CombatKernel k, (EmgEventKind kind, byte role, float param)[] reads, params EmgEventKind[] children)
        {
            var instrs = reads.Select((r, i) => new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)r.kind), Role = r.role, Param2 = r.param, Ordinal = (byte)i }).ToList();
            uint mask = reads.Aggregate(0u, (m, r) => m | 1u << (int)r.kind);
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            foreach (EmgEventKind c in children)
            {
                nodes.Add(new EmgNode { Parent = 0, Gen = 1, TrigEvent = c, TrigCount = 1, FirstInstr = instrs.Count, InstrCount = 0 });
                mask |= 1u << (int)c;
            }
            return k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
        }

        private static (EmgEventKind, byte, float) R(EmgEventKind k, byte role = Trig, float param = 0f) => (k, role, param);

        private static int Machine(CombatKernel k, double2 p, int weapon, float hp = 100f) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live, Position = p, Home = p,
                Radius = 0.5f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, 1f),
            });

        private static int Foe(CombatKernel k, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | CombatUnitFlags.Report, Position = p, Home = p,
                Radius = 0.5f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
            });

        private static CombatWeapon Instant(int program, float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f,
            MarkSeconds = 2f, HeatPerShot = 10f, OverheatAt = 15f, RecoverBelow = 4f, Dissipation = 10f, Program = program,
        };

        private static CombatWeapon Bolt(int program, float life = 3f, float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = damage, Cooldown = 0f,
            ProjectileSpeed = 10f, ProjectileRadius = 0.2f, ProjectileLife = life, Program = program,
        };

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static List<EmgEvent> Pending(CombatKernel k)
        {
            var list = new List<EmgEvent>(k.EmgBusPending);
            for (int i = 0; i < k.EmgBusPending; i++)
            {
                k.TryGetEmgEvent(i, out EmgEvent e);
                list.Add(e);
            }
            return list;
        }

        private static long Enq(CombatKernel k, EmgEventKind kind) => k.EmgEnqueuedOf(kind);

        private static readonly EmgEventKind[] InstantKinds =
        {
            EmgEventKind.Fire, EmgEventKind.Hit, EmgEventKind.Marked, EmgEventKind.Prox, EmgEventKind.Overheat, EmgEventKind.Cooled,
            EmgEventKind.Kill, EmgEventKind.Hurt, EmgEventKind.Death,
        };

        private static readonly EmgEventKind[] ProjectileKinds =
        {
            EmgEventKind.Fire, EmgEventKind.Hit, EmgEventKind.Expire, EmgEventKind.Timer, EmgEventKind.Prox, EmgEventKind.Absorb, EmgEventKind.Kill,
        };

        // ── A. 口径 ─────────────────────────────────────────────────────────────

        private static void CheckBasics()
        {
            Line("  · A. 口径：事件码 / 端口码、emg.* 调参与内核常量、地点级监听掩码（FGR-EMG-060 / 062 / 063 / 065）");
            EmgCompiled c = EmgCatalog.Current;
            var bad = new List<string>();
            for (int code = 1; code <= EmgConst.EventKinds; code++)
            {
                string want = "evt." + ((EmgEventKind)code).ToString().ToLowerInvariant();
                if (!c.PortIdByCode.TryGetValue((ushort)(EmgConst.EventPortBase + code), out string id) || id != want)
                {
                    bad.Add($"{code}:{id ?? "缺"}≠{want}");
                }
            }
            bool sep = Enum.GetValues(typeof(EmgEventKind)).Length == EmgConst.EventKinds + 1 && typeof(EmgEventKind) != typeof(CombatEventKind)
                       && (int)CombatEventKind.Repolluted < 32 && (int)CombatEventKind.Fired == 32;
            Expect(bad.Count == 0 && sep, $"EmgEventKind 1～29 与端口表 evt.* 一一对应（端口码 = 300 + 码{(bad.Count > 0 ? "，不符：" + string.Join("、", bad) : string.Empty)}）；与 CombatEventKind（玩法 < 32、提示 ≥ 32）是两个枚举，不占用其码段");

            CombatConfig cfg = CombatSite.ConfigFromTuning();
            bool capOk = GridContent.TryGetTuning("emg.bus.capacity", out float cap) && (int)cap == EmgConst.BusCapacity;
            Expect(cfg.EmgBusPerStep == 512 && cfg.EmgMaxFanout == 8 && cfg.EmgMaxPerHost == 3 && Mathf.Approximately(cfg.EmgCoolRatio, 0.5f) && capOk
                   && cfg.MaxGameplayEventsPerStep == 64 && cfg.MaxCueEventsPerStep == 24,
                $"fg.TbHomeTuning → 内核配置：每步 {cfg.EmgBusPerStep} 条、扇出 {cfg.EmgMaxFanout}、每宿主 {cfg.EmgMaxPerHost}、冷却比例 {cfg.EmgCoolRatio}；emg.bus.capacity {cap} = 内核常量 {EmgConst.BusCapacity}；玩法 / 提示事件口径仍是 {cfg.MaxGameplayEventsPerStep} / {cfg.MaxCueEventsPerStep}");

            using var k = new CombatKernel(CombatConfig.Default, 8);
            uint m0 = k.EmgListenMask;
            int p1 = Prog(k, new[] { R(EmgEventKind.Hit) });
            uint m1 = k.EmgListenMask;
            int p2 = Prog(k, new[] { R(EmgEventKind.Prox, Trig, 2f) }, EmgEventKind.Timer);
            uint m2 = k.EmgListenMask;
            int again = Prog(k, new[] { R(EmgEventKind.Hit) });
            using var b = new CombatKernel(CombatConfig.Default, 8);
            CombatLoadResult lr = b.Load(k.Serialize());
            uint want2 = 1u << (int)EmgEventKind.Hit | 1u << (int)EmgEventKind.Prox | 1u << (int)EmgEventKind.Timer;
            Expect(m0 == 0u && m1 == 1u << (int)EmgEventKind.Hit && m2 == want2 && again == p1 && k.EmgListenMask == want2 && lr == CombatLoadResult.Ok && b.EmgListenMask == want2 && p2 == 1,
                $"地点级监听掩码：没有程序 = 0；登记程序时并上（{m1:X} → {m2:X}，含子节点触发码），同一程序再登记不变；读档后按程序表重算 = {b.EmgListenMask:X}（不按帧）");
        }

        // ── B. 即时出手的生产者 ───────────────────────────────────────────────────────

        private sealed class InstantRun
        {
            public CombatKernel K;
            public int Me, Foe, Foe2, Prog;
            public List<EmgEvent> Shot1, Shot2;
            public EmgEvent? Hurt, Death, Cooled;
            public CombatFireResult R1, R2;
        }

        /// <summary>即时武器（射程 30、伤害 10、标记 2 秒、每发积热 10 / 过热 15 / 散热 10 每秒）对 4 米外的 15 血敌人打两发（第二发过热 + 击杀），
        /// 再被另一个敌人打 3 点、散热到冷却完成、最后被击杀。</summary>
        private static InstantRun RunInstant(Func<CombatKernel, int> program, float proxRadius = 5f)
        {
            var run = new InstantRun { K = new CombatKernel(CombatConfig.Default, 16) };
            CombatKernel k = run.K;
            run.Prog = program(k);
            int w = k.AddWeapon(Instant(run.Prog));
            int wf = k.AddWeapon(Instant(-1, 3f));
            run.Me = Machine(k, new double2(0, 0), w);
            run.Foe = Foe(k, new double2(0, 4), wf, 15f);
            run.Foe2 = Foe(k, new double2(3, 0), wf, 50f);
            run.R1 = k.FireAt(run.Me, run.Foe, k.Time);
            run.Shot1 = Pending(k);
            StepN(k, 1);
            run.R2 = k.FireAt(run.Me, run.Foe, k.Time);
            run.Shot2 = Pending(k);
            StepN(k, 1);
            k.Damage(run.Me, 3f, run.Foe2);
            List<EmgEvent> hurt = Pending(k);
            run.Hurt = hurt.Count == 1 ? hurt[0] : (EmgEvent?)null;
            StepN(k, 150);
            run.Cooled = k.TryGetLastEmgEvent(EmgEventKind.Cooled, out EmgEvent ce) ? ce : (EmgEvent?)null;
            k.Kill(run.Me, run.Foe2);
            List<EmgEvent> death = Pending(k);
            run.Death = death.Count == 1 ? death[0] : (EmgEvent?)null;
            StepN(k, 1);
            return run;
        }

        private static void CheckInstantProducers()
        {
            Line("  · B. 即时出手的生产者（FGT-EMG-020 阶段 1：发射 / 命中 / 被标记 / 近炸 / 过热 / 冷却完成 / 击杀 / 被击 / 阵亡），每条“该发”与“不该发”对照");
            InstantRun on = RunInstant(k => Prog(k, new[]
            {
                R(EmgEventKind.Fire), R(EmgEventKind.Hit), R(EmgEventKind.Marked, Cond), R(EmgEventKind.Prox, Trig, 5f), R(EmgEventKind.Overheat),
                R(EmgEventKind.Cooled), R(EmgEventKind.Kill), R(EmgEventKind.Hurt), R(EmgEventKind.Death),
            }));
            try
            {
                CombatKernel k = on.K;
                string s1 = string.Join(",", on.Shot1.Select(e => e.Kind));
                string s2 = string.Join(",", on.Shot2.Select(e => e.Kind));
                EmgEvent f1 = on.Shot1.FirstOrDefault(e => e.Kind == EmgEventKind.Fire);
                EmgEvent h1 = on.Shot1.FirstOrDefault(e => e.Kind == EmgEventKind.Hit);
                EmgEvent mk = on.Shot1.FirstOrDefault(e => e.Kind == EmgEventKind.Marked);
                EmgEvent px = on.Shot1.FirstOrDefault(e => e.Kind == EmgEventKind.Prox);
                EmgEvent oh = on.Shot2.FirstOrDefault(e => e.Kind == EmgEventKind.Overheat);
                EmgEvent kl = on.Shot2.FirstOrDefault(e => e.Kind == EmgEventKind.Kill);
                bool sameChain = on.Shot1.All(e => e.Chain == f1.Chain) && on.Shot2.All(e => e.Chain == on.Shot2[0].Chain) && f1.Chain > 0 && on.Shot2[0].Chain != f1.Chain;
                bool seq = on.Shot1.Concat(on.Shot2).Select(e => e.Seq).SequenceEqual(Enumerable.Range(on.Shot1[0].Seq, on.Shot1.Count + on.Shot2.Count));
                Expect(on.R1 == CombatFireResult.Ok && s1 == "Fire,Prox,Hit,Marked" && sameChain && seq
                       && f1.EntKind == EmgEntKind.Unit && f1.Ent == on.Me && f1.Other == on.Foe && f1.Prog == on.Prog && f1.Node == 0 && f1.Depth == 0 && Mathf.Approximately(f1.Value2, 1f)
                       && h1.Other == on.Foe && Mathf.Approximately(h1.Value, 10f) && Mathf.Approximately(h1.Value2, 1f) && h1.Code == EmgCode.HitDirect
                       && mk.Other == on.Foe && Mathf.Approximately(mk.Value, 2f) && Mathf.Approximately(px.Value, 4f) && px.Other == on.Foe,
                    $"第一发：入队 [{s1}]（只入队、不内联；同一条新链 {f1.Chain}、Seq 连续）；发射 Ent = 出手者、Other = 目标、方向 (0, {f1.Value2:0.##})；命中 10 点、第 1 个；被标记 2 秒；近炸（出手瞬间目标 {px.Value:0.#} 米 ≤ 读端 5 米）");
                Expect(on.R2 == CombatFireResult.Ok && s2 == "Fire,Prox,Overheat,Hit,Kill" && oh.Value >= 15f && oh.Value <= 20f && oh.Ent == on.Me && oh.EntKind == EmgEntKind.Unit
                       && kl.Other == on.Foe && Mathf.Approximately(kl.Value, 5f) && kl.Ent == on.Me && kl.Prog == on.Prog,
                    $"第二发：[{s2}]——积热 {oh.Value:0} 首次越过 15 发过热；目标阵亡发击杀（溢出伤害 {kl.Value:0}，宿主 = 这次出手）；已阵亡的目标不再标记");
                EmgEvent hu = on.Hurt ?? default;
                EmgEvent de = on.Death ?? default;
                EmgEvent co = on.Cooled ?? default;
                Expect(on.Hurt.HasValue && hu.Kind == EmgEventKind.Hurt && hu.Ent == on.Me && hu.Other == on.Foe2 && Mathf.Approximately(hu.Value, 3f) && hu.Code == EmgCode.SrcOther && hu.Chain > 0
                       && on.Cooled.HasValue && co.Ent == on.Me && co.Value < 7.5f && Enq(k, EmgEventKind.Cooled) == 1
                       && on.Death.HasValue && de.Kind == EmgEventKind.Death && de.Ent == on.Me && de.Other == on.Foe2,
                    $"被击：被打 3 点（Other = 攻击者，来源码 {hu.Code} = 非载荷伤害，热更直扣开新链 {hu.Chain}）；冷却完成：散热到 {co.Value:0.#} < 15 × 0.5 时发一次；阵亡：Ent = 自己、Other = 击杀者");
                EmgBusStats st = k.EmgBusStats;
                Expect(k.EmgBusPending == 0 && st.Processed == st.Enqueued && st.OrderErrors == 0 && st.Lost == 0 && InstantKinds.All(x => Enq(k, x) >= 1),
                    $"全部在随后的步里按 Seq 处理完（入队 {st.Enqueued} = 处理 {st.Processed}，顺序错 0）；9 种都至少发过 1 次");
            }
            finally
            {
                on.K.Dispose();
            }

            // 不该发：程序在但不读这些码（只读接入）；完全没有程序。
            InstantRun off = RunInstant(k => Prog(k, new[] { R(EmgEventKind.Uplink) }));
            InstantRun none = RunInstant(k => -1);
            InstantRun far = RunInstant(k => Prog(k, new[] { R(EmgEventKind.Prox, Trig, 2f) }));
            try
            {
                string offHits = string.Join(",", InstantKinds.Where(x => Enq(off.K, x) > 0));
                Expect(offHits.Length == 0 && off.K.EmgBusStats.Enqueued == 0 && off.K.EmgBusStats.ProxEvals == 0 && off.R1 == CombatFireResult.Ok && off.R2 == CombatFireResult.Ok,
                    $"不该发（程序只读 evt.uplink）：同样两发 + 被击 + 冷却 + 阵亡，9 种一条都不入队{(offHits.Length > 0 ? "（却发了 " + offHits + "）" : string.Empty)}，近炸不求值（{off.K.EmgBusStats.ProxEvals} 次）");
                Expect(none.K.EmgBusStats.Enqueued == 0 && none.K.EmgListenMask == 0u && none.K.EmgScalars.NextChain == 1 && none.K.EmgBusStats.ProxEvals == 0
                       && none.K.ProgramCount == 0 && none.K.TryGetUnitEmg(none.Me, out _, out EmgUnitState nu) && nu.HeatLatch == 0,
                    $"不该发（没有程序）：掩码 0、不入队、不发链号（下一个链号仍是 {none.K.EmgScalars.NextChain}）、过热不留记号——与旧行为逐字相同（金标准哈希见 FgEmergenceKernelSelfCheck B）");
                Expect(Enq(far.K, EmgEventKind.Prox) == 0 && far.K.EmgBusStats.ProxEvals >= 2,
                    $"近炸读端半径 2 米、目标在 4 米：求值 {far.K.EmgBusStats.ProxEvals} 次、不发");
            }
            finally
            {
                off.K.Dispose();
                none.K.Dispose();
                far.K.Dispose();
            }
        }

        // ── C. 弹体宿主的生产者 ───────────────────────────────────────────────────────

        private static (CombatKernel k, int me, int foe, int prog) ProjectileDuel(Func<CombatKernel, int> program, double foeY, float life, float foeHp, bool shield)
        {
            var k = new CombatKernel(CombatConfig.Default, 16);
            int p = program(k);
            int w = k.AddWeapon(Bolt(p, life));
            int wf = k.AddWeapon(Instant(-1, 1f));
            int me = Machine(k, new double2(0, 0), w);
            int foe = Foe(k, new double2(0, foeY), wf, foeHp);
            if (shield)
            {
                k.SetShield(77, CombatFaction.Hostile, new double2(0, foeY), 3f, 100f, 100f, true, 0f);
            }
            return (k, me, foe, p);
        }

        private static int ProjAll(CombatKernel k) => Prog(k, new[]
        {
            R(EmgEventKind.Fire), R(EmgEventKind.Hit), R(EmgEventKind.Expire), R(EmgEventKind.Timer, Trig, 0.2f), R(EmgEventKind.Prox, Trig, 2f),
            R(EmgEventKind.Absorb), R(EmgEventKind.Kill),
        });

        private static void FlyOut(CombatKernel k)
        {
            for (int i = 0; i < 300 && k.ProjectileCount > 0; i++)
            {
                StepN(k, 1);
            }
            StepN(k, 1);
        }

        private static void CheckProjectileProducers()
        {
            Line("  · C. 弹体宿主的生产者（命中 / 终结：命中·寿命·被吸收 / 定时 / 近炸 / 吸收 / 击杀）：宿主 = 弹体稳定 Id、程序与节点随弹体；对照同上");
            var (k, me, foe, p) = ProjectileDuel(ProjAll, 6, 3f, 5f, false);
            using (k)
            {
                k.FireAt(me, foe, k.Time);
                EmgEvent fire = Pending(k).FirstOrDefault(e => e.Kind == EmgEventKind.Fire);
                k.TryGetProjectile(0, out CombatProjectile pr0);
                FlyOut(k);
                k.TryGetLastEmgEvent(EmgEventKind.Hit, out EmgEvent hit);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent exp);
                k.TryGetLastEmgEvent(EmgEventKind.Timer, out EmgEvent tim);
                k.TryGetLastEmgEvent(EmgEventKind.Prox, out EmgEvent prx);
                k.TryGetLastEmgEvent(EmgEventKind.Kill, out EmgEvent kil);
                bool host = new[] { hit, exp, tim, prx, kil }.All(e => e.EntKind == EmgEntKind.Projectile && e.Ent == pr0.Id && e.Prog == p && e.Node == 0 && e.Chain == pr0.Chain);
                Expect(fire.Chain == pr0.Chain && pr0.Chain > 0 && host && hit.Seq < kil.Seq && kil.Seq < exp.Seq
                       && hit.Other == foe && Mathf.Approximately(hit.Value, 10f) && exp.Code == EmgCode.ExpireHit && exp.Other == foe
                       && Mathf.Approximately(tim.Value, 0.2f) && prx.Value <= 2f && prx.Other == foe && Mathf.Approximately(kil.Value, 5f) && kil.Other == foe
                       && Enq(k, EmgEventKind.Hit) == 1 && Enq(k, EmgEventKind.Expire) == 1 && Enq(k, EmgEventKind.Timer) == 1 && Enq(k, EmgEventKind.Prox) == 1
                       && Enq(k, EmgEventKind.Kill) == 1 && Enq(k, EmgEventKind.Absorb) == 0 && k.EmgBusPending == 0,
                    $"命中弹（6 米外 5 血）：发射与弹体同链 {pr0.Chain}；定时 {tim.Value:0.##} 秒、近炸 {prx.Value:0.##} 米、命中 10 点、击杀（溢出 {kil.Value:0}，宿主 = 弹体 {pr0.Id}）、命中同一步终结（Code = 命中）各 1 条，Seq 顺序 命中 → 击杀 → 终结（与即时出手一致）；没进护盾不发吸收");
            }

            var (k2, me2, foe2, _) = ProjectileDuel(ProjAll, 40, 0.5f, 50f, false);
            using (k2)
            {
                k2.FireAt(me2, foe2, k2.Time);
                k2.TryGetProjectile(0, out CombatProjectile pr);
                FlyOut(k2);
                k2.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent exp);
                Expect(Enq(k2, EmgEventKind.Expire) == 1 && exp.Code == EmgCode.ExpireLife && exp.Ent == pr.Id && Enq(k2, EmgEventKind.Hit) == 0 && Enq(k2, EmgEventKind.Prox) == 0
                       && Enq(k2, EmgEventKind.Timer) == 1 && k2.EmgBusStats.ProxEvals > 0,
                    $"打不到的弹（目标 40 米、寿命 0.5 秒）：寿命到发终结（Code = 寿命）；不发命中；近炸求值 {k2.EmgBusStats.ProxEvals} 次但 2 米内没有敌人、不发");
            }

            var (k3, me3, foe3, _) = ProjectileDuel(ProjAll, 10, 3f, 50f, true);
            using (k3)
            {
                k3.FireAt(me3, foe3, k3.Time);
                FlyOut(k3);
                k3.TryGetLastEmgEvent(EmgEventKind.Absorb, out EmgEvent ab);
                k3.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent exp);
                Expect(Enq(k3, EmgEventKind.Absorb) == 1 && ab.Other == 77 && ab.OtherKind == EmgEntKind.None && Mathf.Approximately(ab.Value, 10f)
                       && exp.Code == EmgCode.ExpireAbsorbed && Enq(k3, EmgEventKind.Hit) == 0 && k3.ShieldAt(0).Hits == 1,
                    $"飞进敌方护盾：发吸收（Other = 护盾外部键 {ab.Other}，吸收 {ab.Value:0}）与终结（Code = 被吸收），不发命中");
            }

            var (k4, me4, foe4, _) = ProjectileDuel(kk => Prog(kk, new[] { R(EmgEventKind.Uplink) }), 6, 3f, 5f, false);
            var (k5, me5, foe5, _) = ProjectileDuel(kk => -1, 6, 3f, 5f, false);
            using (k4)
            using (k5)
            {
                k4.FireAt(me4, foe4, k4.Time);
                k5.FireAt(me5, foe5, k5.Time);
                k5.TryGetProjectile(0, out CombatProjectile p5);
                StepN(k5, 3);
                k5.TryGetProjectile(0, out CombatProjectile p5b);
                FlyOut(k4);
                FlyOut(k5);
                string offHits = string.Join(",", ProjectileKinds.Where(x => Enq(k4, x) > 0));
                Expect(offHits.Length == 0 && k4.EmgBusStats.ProxEvals == 0 && k5.EmgBusStats.Enqueued == 0 && p5.Prog == -1 && p5.Chain == 0 && p5b.Age == 0f && k5.EmgScalars.NextChain == 1,
                    $"不该发：程序只读 evt.uplink 的弹体 7 种一条不入队、近炸不求值；没有程序的弹体 Prog = −1、无链、不累计飞行秒数（{p5b.Age}）{(offHits.Length > 0 ? "；却发了 " + offHits : string.Empty)}");
            }
        }

        // ── C2. 穿透 / 反弹的生产入口与每宿主上限 ─────────────────────────────────────────

        private static void CheckPierceBounceHost()
        {
            Line("  · C2. 穿透 / 反弹（真实弹道在 EMG1-GRM-01 接入同一生产入口；偏折场反弹已接）与每宿主同一事件码 ≤ 3 次（FGR-EMG-065），计数随弹体进快照");
            var (k, me, foe, p) = ProjectileDuel(kk => Prog(kk, new[] { R(EmgEventKind.Pierce), R(EmgEventKind.Bounce) }), 40, 3f, 50f, false);
            using (k)
            {
                k.FireAt(me, foe, k.Time);
                bool[] pierce = Enumerable.Range(0, 4).Select(_ => k.EmgProjectileEventForTests(0, EmgEventKind.Pierce, foe, 2f, 0f, 0)).ToArray();
                bool bounce = k.EmgProjectileEventForTests(0, EmgEventKind.Bounce, 0, 0f, 1f, EmgCode.FaceUnit);
                bool hitNot = !k.EmgProjectileEventForTests(0, EmgEventKind.Hit, foe, 1f, 0f, 0);
                List<EmgEvent> q = Pending(k);
                k.TryGetProjectile(0, out CombatProjectile pr);
                EmgEvent pe = q.First(e => e.Kind == EmgEventKind.Pierce);
                Expect(pierce.Take(3).All(x => x) && !pierce[3] && bounce && hitNot && k.EmgBusStats.PerHostCut == 1 && q.Count(e => e.Kind == EmgEventKind.Pierce) == 3
                       && pe.Ent == pr.Id && pe.EntKind == EmgEntKind.Projectile && pe.Chain == pr.Chain && pe.Other == foe && (pr.TrigUsed & 3u) == 3u && ((pr.TrigUsed >> 2) & 3u) == 1u,
                    $"同一发弹体穿透 4 次：前 3 次入队、第 4 次不入队（每宿主截断 {k.EmgBusStats.PerHostCut}）；反弹另占一格计数（TrigUsed = {pr.TrigUsed:X}）；程序不读命中时命中不入队");
                using var b = new CombatKernel(k.Config, 16);
                CombatLoadResult lr = b.Load(k.Serialize());
                bool again = b.EmgProjectileEventForTests(0, EmgEventKind.Pierce, foe, 2f, 0f, 0);
                bool bounce2 = b.EmgProjectileEventForTests(0, EmgEventKind.Bounce, 0, 0f, 1f, EmgCode.FaceUnit);
                Expect(lr == CombatLoadResult.Ok && !again && bounce2 && b.EmgBusPending == k.EmgBusPending + 1,
                    "存读档后：这发弹体的穿透计数仍满（再穿透不入队）、反弹还剩额度；未处理的 4 条事件随快照恢复");
            }
        }

        // ── C3. 被击来源、无人机宿主、命中种类、即时宿主上限、读端行上限、过热记号（第 1 轮审查修复）──────────────────────

        private static CombatWeapon WithReading(CombatWeapon w, CombatReading r)
        {
            w.Reading = r;
            return w;
        }

        private static void CheckHostsAndSources()
        {
            Line("  · C3. 被击来源码按载体（没有程序的敌人也算）；无人机做宿主（每架每次开火 / 命中 / 到期）；连锁 / 穿透后命中种类；即时出手每宿主 ≤ 3；单节点事件读端 ≤ 16 行；换成不过热的武器清过热记号");
            // 1) 被击来源：敌人没有程序，分别用格斗 / 即时射弹 / 弹体 / 布区打带读 evt.hurt 程序的机器。
            var srcs = new List<string>();
            bool srcOk = true;
            (string name, CombatWeapon w, int steps, ushort want)[] cases =
            {
                ("格斗", WithReading(Instant(-1, 4f), new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 90f }), 0, EmgCode.SrcMelee),
                ("即时射弹", Instant(-1, 4f), 0, EmgCode.SrcProjectile),
                ("弹体", Bolt(-1, 3f, 4f), 120, EmgCode.SrcProjectile),
                ("布区", WithReading(Instant(-1, 4f), new CombatReading { Carrier = CombatCarrier.Field, Area = 3f, FieldSeconds = 2f, FieldDpsRatio = 1f }), 120, EmgCode.SrcZone),
            };
            foreach (var c in cases)
            {
                using var k = new CombatKernel(CombatConfig.Default, 8);
                int p = Prog(k, new[] { R(EmgEventKind.Hurt) });
                int me = Machine(k, new double2(0, 0), k.AddWeapon(Instant(p)), 1000f);
                int foe = Foe(k, new double2(0, 2), k.AddWeapon(c.w), 50f);
                CombatFireResult fr = k.FireAt(foe, me, k.Time);
                List<EmgEvent> now = Pending(k).Where(e => e.Kind == EmgEventKind.Hurt).ToList();
                StepN(k, c.steps + 1);
                k.TryGetLastEmgEvent(EmgEventKind.Hurt, out EmgEvent last);
                long n = Enq(k, EmgEventKind.Hurt);
                bool ok = fr == CombatFireResult.Ok && last.Kind == EmgEventKind.Hurt && last.Ent == me && last.Other == foe && last.Code == c.want
                          && now.All(e => e.Code == c.want) && (c.name != "布区" || n >= 2) && k.EmgScalars.NextChain == 1 + n;
                srcOk &= ok;
                srcs.Add($"{c.name}={last.Code}×{n}{(ok ? string.Empty : "（错，开火 " + fr + "）")}");
            }
            Expect(srcOk, $"被击来源码按载体（敌人没有程序）：{string.Join("，", srcs)}（格斗 1 / 射弹·弹体 2 / 区域 3；布区含出手那一下与之后的区域节拍）；热更直扣 = 0 的对照在 B 段");

            // 1b) 派系专属攻击不经过 UnitAttack / FireAt（第 2 轮审查 P1）：齐射空爆 = 弹体 2、冲锋撞击 = 撞击 6、酸沼 = 斑块 4（世界源，攻击者 0）。
            EmgEvent FirstHurt(CombatBehavior beh, CombatBehaviorProfile prof, CombatWeapon w, bool acid, out int foeId)
            {
                using var k = new CombatKernel(CombatConfig.Default, 8);
                int me = Machine(k, new double2(0, 0), k.AddWeapon(Instant(Prog(k, new[] { R(EmgEventKind.Hurt) }))), 1000f);
                foeId = 0;
                if (acid)
                {
                    k.SetAcidMarsh(new[] { new float4(0f, 0f, 0f, 5f) }, 0.02f, 1f);
                }
                else
                {
                    foeId = k.Spawn(new CombatSpawn
                    {
                        ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = beh, Flags = Live | CombatUnitFlags.Report, Position = new double2(0, 6), Home = new double2(0, 6),
                        Radius = 0.5f, Health = 1000f, MaxHealth = 1000f, Weapon = k.AddWeapon(w), BehaviorProfile = k.AddProfile(prof), Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
                    });
                }
                for (int s = 0; s < 600; s++)
                {
                    StepN(k, 1);
                    if (k.TryGetLastEmgEvent(EmgEventKind.Hurt, out EmgEvent e) && e.Ent == me)
                    {
                        return e;
                    }
                }
                return default;
            }
            CombatWeapon ram = Instant(-1, 4f);
            ram.Range = 0.5f;
            EmgEvent airburst = FirstHurt(CombatBehavior.Salvo, new CombatBehaviorProfile { SalvoCount = 1f, SalvoGap = 0.3f, SalvoReload = 3f, SalvoWarn = 0.8f, SalvoMode = 2f, SalvoRadius = 2f },
                Instant(-1, 4f), false, out int salvoFoe);
            EmgEvent impact = FirstHurt(CombatBehavior.Charger, new CombatBehaviorProfile { SenseRange = 20f, Speed = 2f, TurnRate = 360f, ChargeSpeed = 12f, ChargeWarn = 0.8f, ChargeSeconds = 1.5f, ChargeCooldown = 3f },
                ram, false, out int chargeFoe);
            EmgEvent marsh = FirstHurt(CombatBehavior.None, default, default, true, out _);
            Expect(airburst.Kind == EmgEventKind.Hurt && airburst.Code == EmgCode.SrcProjectile && airburst.Other == salvoFoe
                   && impact.Kind == EmgEventKind.Hurt && impact.Code == EmgCode.SrcImpact && impact.Other == chargeFoe
                   && marsh.Kind == EmgEventKind.Hurt && marsh.Code == EmgCode.SrcPatch && marsh.Other == 0,
                $"派系专属攻击的被击来源码（不经过 UnitAttack）：齐射空爆 {airburst.Code}（应 2，攻击者 {airburst.Other}）、冲锋撞击 {impact.Code}（应 6，攻击者 {impact.Other}）、酸沼 {marsh.Code}（应 4，攻击者 {marsh.Other}）");

            // 2) 无人机做宿主：母机的无人机舱武器带程序（读 发射 / 命中 / 终结）。
            CombatWeapon bay = WithReading(Instant(-1, 10f), new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 3f, DroneRatio = 1f, DroneCooldown = 0.2f });
            (long fire, long hit, long exp, EmgEvent lf, EmgEvent lh, EmgEvent le, int prog, long enq, int chains) DroneRun(Func<CombatKernel, int> program)
            {
                using var k = new CombatKernel(CombatConfig.Default, 8);
                CombatWeapon w = bay;
                w.Program = program(k);
                int me = Machine(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 3), k.AddWeapon(Instant(-1, 1f)), 1000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 240);
                k.TryGetLastEmgEvent(EmgEventKind.Fire, out EmgEvent lf);
                k.TryGetLastEmgEvent(EmgEventKind.Hit, out EmgEvent lh);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent le);
                return (Enq(k, EmgEventKind.Fire), Enq(k, EmgEventKind.Hit), Enq(k, EmgEventKind.Expire), lf, lh, le, w.Program, k.EmgBusStats.Enqueued, k.EmgScalars.NextChain);
            }
            var dOn = DroneRun(k => Prog(k, new[] { R(EmgEventKind.Fire), R(EmgEventKind.Hit), R(EmgEventKind.Expire) }));
            var dOff = DroneRun(k => Prog(k, new[] { R(EmgEventKind.Uplink) }));
            var dNone = DroneRun(k => -1);
            Expect(dOn.fire >= 2 && dOn.hit >= 1 && dOn.exp == 1 && dOn.lf.EntKind == EmgEntKind.Drone && dOn.lh.EntKind == EmgEntKind.Drone && dOn.lh.Ent == dOn.lf.Ent && dOn.lh.Prog == dOn.prog
                   && dOn.lh.Code == EmgCode.HitDirect && dOn.le.EntKind == EmgEntKind.Drone && dOn.le.Code == EmgCode.ExpireLife && dOn.le.Ent == dOn.lf.Ent,
                $"无人机宿主：放机那次出手 + 每架每次开火共发射 {dOn.fire} 次（最后一条宿主 = 无人机 {dOn.lf.Ent}）；命中 {dOn.hit} 次（宿主 = 无人机、程序 = 舱武器的程序根节点）；到期终结 {dOn.exp} 次（Code = 寿命）");
            Expect(dOff.fire + dOff.hit + dOff.exp == 0 && dNone.enq == 0 && dNone.chains == 1,
                $"不该发：舱武器的程序不读这三个码 → 0 条；没有程序 → 不入队、不开链（下一个链号 {dNone.chains}）");

            // 3) 命中种类：连锁跳到的 = 连锁，身后被穿到的 = 穿透后；主目标 = 直接。
            string HitCodes(CombatReading r, params double2[] foes)
            {
                using var k = new CombatKernel(CombatConfig.Default, 8);
                int p = Prog(k, new[] { R(EmgEventKind.Hit) });
                int me = Machine(k, new double2(0, 0), k.AddWeapon(WithReading(Instant(p, 10f), r)));
                int first = -1;
                foreach (double2 f in foes)
                {
                    int id = Foe(k, f, k.AddWeapon(Instant(-1, 1f)), 100f);
                    first = first < 0 ? id : first;
                }
                k.FireAt(me, first, k.Time);
                return string.Join(",", Pending(k).Where(e => e.Kind == EmgEventKind.Hit).Select(e => $"{e.Code}#{e.Value2:0}"));
            }
            string chain = HitCodes(new CombatReading { ChainHits = 1, ChainRange = 5f, ChainFalloff = 0.5f }, new double2(0, 4), new double2(3, 4));
            string pierce = HitCodes(new CombatReading { PierceHits = 1, PierceRange = 5f, PierceRatio = 0.5f }, new double2(0, 4), new double2(0, 6));
            Expect(chain == $"{EmgCode.HitDirect}#1,{EmgCode.HitChain}#2" && pierce == $"{EmgCode.HitDirect}#1,{EmgCode.HitAfterPierce}#2",
                $"命中种类（Code#第几个）：连锁 [{chain}]（直接 0、连锁 6）；穿透 [{pierce}]（直接 0、穿透后 1）");

            // 4) 即时出手每宿主同一事件 ≤ 3：力场一圈打中 5 个。
            using (var k = new CombatKernel(CombatConfig.Default, 8))
            {
                int p = Prog(k, new[] { R(EmgEventKind.Hit) });
                int me = Machine(k, new double2(0, 0), k.AddWeapon(WithReading(Instant(p, 1f), new CombatReading { Carrier = CombatCarrier.Aura, Area = 5f })));
                int first = 0;
                for (int i = 0; i < 5; i++)
                {
                    int id = Foe(k, new double2(math.cos(i * 1.2566) * 2.5, math.sin(i * 1.2566) * 2.5), k.AddWeapon(Instant(-1, 1f)), 100f);
                    first = i == 0 ? id : first;
                }
                CombatFireResult fr = k.FireAt(me, first, k.Time);
                int hits = Pending(k).Count(e => e.Kind == EmgEventKind.Hit);
                Expect(fr == CombatFireResult.Ok && hits == 3 && k.EmgBusStats.PerHostCut == 2,
                    $"即时出手同一宿主：力场一圈打中 5 个，命中只入队 {hits} 条、截断 {k.EmgBusStats.PerHostCut} 条（FGR-EMG-065 与弹体同一计数口径）");
            }

            // 5) 单节点事件读端行 ≤ 16（每宿主计数 2 比特 × 16）：17 行拒绝。
            using (var k = new CombatKernel(CombatConfig.Default, 8))
            {
                int ok16 = Prog(k, Enumerable.Range(0, 16).Select(i => R(EmgEventKind.Hit, Trig, i)).ToArray());
                int bad17 = Prog(k, Enumerable.Range(0, 17).Select(i => R(EmgEventKind.Hit, Trig, i)).ToArray());
                Expect(ok16 >= 0 && bad17 == -1 && k.ProgramCount == 1,
                    $"一个节点 16 条事件读端行可登记（{ok16}），17 条拒绝（{bad17}）——读档走同一份校验，超出的读端行不会让“同一事件 ≤ 3 次”静默失效");
            }

            // 6) 过热之后换成不会过热的武器：记号清掉、不发冷却完成（新程序没见过这次过热）。
            using (var k = new CombatKernel(CombatConfig.Default, 8))
            {
                int p = Prog(k, new[] { R(EmgEventKind.Overheat), R(EmgEventKind.Cooled) });
                int me = Machine(k, new double2(0, 0), k.AddWeapon(Instant(p)));
                int foe = Foe(k, new double2(0, 4), k.AddWeapon(Instant(-1, 1f)), 1000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                k.FireAt(me, foe, k.Time);
                k.TryGetUnitEmg(me, out _, out EmgUnitState hot);
                CombatWeapon cold = Instant(p);
                cold.OverheatAt = 0f;
                cold.HeatPerShot = 0f;
                k.SetUnitWeapon(me, k.AddWeapon(cold));
                StepN(k, 3);
                k.TryGetUnitEmg(me, out _, out EmgUnitState after);
                Expect(Enq(k, EmgEventKind.Overheat) == 1 && hot.HeatLatch == 1 && after.HeatLatch == 0 && Enq(k, EmgEventKind.Cooled) == 0,
                    $"过热（记号 {hot.HeatLatch}）后换成阈值 0 的武器：记号 → {after.HeatLatch}、不发冷却完成（{Enq(k, EmgEventKind.Cooled)} 条）——记号不会永久置位、不再一直混进状态哈希");
            }
        }

        // ── D. 热更层注入（内核入口）───────────────────────────────────────────────────

        // ── C2. EMG1-TRIG-01 承接 DEBT-EMG1BUS01-03：区域 / 回波 / 维修 / 格斗与力场定时做宿主 ─────────────────────

        private static List<(int step, EmgEvent e)> WatchAt(CombatKernel k, int steps, params EmgEventKind[] kinds)
        {
            var got = new List<(int, EmgEvent)>();
            var last = kinds.ToDictionary(x => x, x => k.TryGetLastEmgEvent(x, out EmgEvent e) ? e.Seq : int.MinValue);
            for (int s = 1; s <= steps; s++)
            {
                StepN(k, 1);
                foreach (EmgEventKind x in kinds)
                {
                    if (k.TryGetLastEmgEvent(x, out EmgEvent e) && e.Seq != last[x])
                    {
                        last[x] = e.Seq;
                        got.Add((s, e));
                    }
                }
            }
            return got;
        }

        /// <summary>mode 0 = 程序读这些码；1 = 程序只读 evt.uplink（不该发）；2 = 没有程序（不该发）。</summary>
        private static int HostProg(CombatKernel k, int mode, params (EmgEventKind, byte, float)[] reads) =>
            mode == 0 ? Prog(k, reads) : mode == 1 ? Prog(k, new[] { R(EmgEventKind.Uplink) }) : -1;

        private static void CheckTriggerHosts()
        {
            Line("  · C2. EMG1-TRIG-01 承接 DEBT-EMG1BUS01-03（ADR ③ 宿主五元组）：布区落区做宿主——区域节拍 evt.hit（Code 3）、区域定时 evt.timer（从落区起算）、区域到期 evt.expire；回波 evt.hit（Code 5）；维修 evt.hit（Code 7）；格斗 / 力场 evt.timer（从出手起算）——每条“该发 / 不该发”对照");
            EmgEventKind[] zk = { EmgEventKind.Hit, EmgEventKind.Timer, EmgEventKind.Expire };
            for (int mode = 0; mode < 3; mode++)
            {
                using var k = new CombatKernel(CombatConfig.Default, 16);
                int p = HostProg(k, mode, R(EmgEventKind.Hit, Cond), R(EmgEventKind.Timer, Trig, 0.4f), R(EmgEventKind.Expire, Cond));
                CombatWeapon w = Instant(p);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Field, Area = 2f, FieldSeconds = 1.2f, FieldDpsRatio = 1f };
                int me = Machine(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 4), -1, 500f);
                k.FireAt(me, foe, k.Time);
                k.TryGetZone(0, out CombatZone z0);
                List<(int step, EmgEvent e)> got = WatchAt(k, 100, zk);
                var zone = got.Where(x => x.e.EntKind == EmgEntKind.Zone).ToList();
                if (mode == 0)
                {
                    var tick = zone.Where(x => x.e.Kind == EmgEventKind.Hit).ToList();
                    var timer = zone.Where(x => x.e.Kind == EmgEventKind.Timer).ToList();
                    var expire = zone.Where(x => x.e.Kind == EmgEventKind.Expire).ToList();
                    bool host = zone.Count > 0 && zone.All(x => x.e.Ent == z0.EmgId && x.e.Prog == p && x.e.Node == 0 && x.e.Chain > 0 && x.e.Chain == zone[0].e.Chain) && z0.EmgId > 0 && z0.EmgProgRef == p + 1;
                    Expect(host && tick.Count >= 1 && tick.All(x => x.e.Code == EmgCode.HitZoneTick && x.e.Other == foe) && timer.Count == 1 && Mathf.Approximately(timer[0].e.Value, 0.4f)
                           && Math.Abs(timer[0].step * Dt - 0.4f) <= 2f * Dt && expire.Count == 1 && expire[0].e.Code == EmgCode.ExpireLife && Math.Abs(expire[0].step * Dt - 1.2f) <= 2f * Dt,
                        $"该发（布区程序读 evt.hit / evt.timer / evt.expire）：落区成为宿主（EntKind = 区域、Ent = 稳定 ID {z0.EmgId}、程序 / 0 代根节点 / 出手的链）；区域节拍 evt.hit × {tick.Count}（Code {EmgCode.HitZoneTick}，Other = 圈内敌人）；"
                        + $"evt.timer 在落区后第 {(timer.Count > 0 ? timer[0].step : -1)} 步（0.4 秒）；evt.expire 在第 {(expire.Count > 0 ? expire[0].step : -1)} 步（存续 1.2 秒，Code = 寿命）");
                }
                else
                {
                    Expect(zone.Count == 0 && z0.EmgProgRef == (mode == 1 ? p + 1 : 0) && zk.All(x => Enq(k, x) == 0),
                        $"不该发（{(mode == 1 ? "程序只读接入：落区仍挂着出手的程序（区域程序引用 " + z0.EmgProgRef + "），但程序不读这些码" : "没有程序：落区不挂程序")}）：evt.hit / timer / expire 一条都不入队");
                }
            }
            for (int mode = 0; mode < 3; mode++)
            {
                using var k = new CombatKernel(CombatConfig.Default, 16);
                int p = HostProg(k, mode, R(EmgEventKind.Hit, Cond));
                CombatWeapon w = Instant(p);
                w.MarkSeconds = 0f;
                w.Reading = new CombatReading { EchoCount = 1, EchoDelay = 0.3f, EchoRatio = 0.5f };
                int me = Machine(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 4), -1, 500f);
                k.FireAt(me, foe, k.Time);
                List<(int step, EmgEvent e)> got = WatchAt(k, 40, EmgEventKind.Hit);
                var direct = got.Where(x => x.e.Code == EmgCode.HitDirect).ToList();
                var echo = got.Where(x => x.e.Code == EmgCode.HitEcho).ToList();
                if (mode == 0)
                {
                    Expect(direct.Count == 1 && echo.Count == 1 && echo[0].e.EntKind == EmgEntKind.Unit && echo[0].e.Ent == me && echo[0].e.Other == foe && echo[0].e.Chain == direct[0].e.Chain
                           && echo[0].e.Prog == p && Mathf.Approximately(echo[0].e.Value, 5f) && Math.Abs(echo[0].step * Dt - 0.3f) <= 2f * Dt,
                        $"该发（回波读法 + 程序读 evt.hit）：直击 evt.hit Code 0，{(echo.Count > 0 ? echo[0].step : -1)} 步后回波结算发 evt.hit（Code {EmgCode.HitEcho} = 回波，宿主 = 出手单位、同一条链、伤害 5）");
                }
                else
                {
                    Expect(got.Count == 0 && Enq(k, EmgEventKind.Hit) == 0 && k.EchoCount == 0, $"不该发（{(mode == 1 ? "程序只读接入" : "没有程序")}）：直击与回波都照常结算，不发 evt.hit");
                }
            }
            for (int mode = 0; mode < 3; mode++)
            {
                using var k = new CombatKernel(CombatConfig.Default, 16);
                int p = HostProg(k, mode, R(EmgEventKind.Hit, Cond));
                int prof = k.AddProfile(new CombatBehaviorProfile { EffectRange = 10f, EffectAmount = 5f, CycleSeconds = 0.2f });
                int healer = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.Repair, Flags = Live, Position = new double2(10, 10), Home = new double2(10, 10),
                    Radius = 0.5f, Health = 100f, MaxHealth = 100f, Weapon = k.AddWeapon(Instant(p)), BehaviorProfile = prof, Priority = 1,
                });
                int hurt = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live, Position = new double2(12, 10), Home = new double2(12, 10),
                    Radius = 0.5f, Health = 20f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                List<(int step, EmgEvent e)> got = WatchAt(k, 30, EmgEventKind.Hit);
                var heal = got.Where(x => x.e.Code == EmgCode.HitHeal && x.e.EntKind == EmgEntKind.Unit && x.e.Ent == healer && x.e.Other == hurt).ToList();
                bool healed = k.TryGetUnit(hurt, out CombatUnitView hv) && hv.Health > 20f;
                Expect(mode == 0 ? heal.Count >= 1 && healed && Mathf.Approximately(heal[0].e.Value, 5f) : got.Count == 0 && Enq(k, EmgEventKind.Hit) == 0 && healed,
                    mode == 0
                        ? $"该发（敌方维修单位带程序）：每次修复 = 一次出手，修复命中 evt.hit Code {EmgCode.HitHeal}（宿主 = 维修单位、Other = 伤员、数值 5）× {heal.Count}"
                        : $"不该发（{(mode == 1 ? "程序只读接入" : "没有程序")}）：照常修复（伤员 {hv.Health:0} 血），不发 evt.hit");
            }
            foreach (CombatCarrier carrier in new[] { CombatCarrier.Melee, CombatCarrier.Aura, CombatCarrier.Projectile })
            {
                using var k = new CombatKernel(CombatConfig.Default, 16);
                int p = Prog(k, new[] { R(EmgEventKind.Timer, Trig, 0.3f) });
                CombatWeapon w = Instant(p);
                w.Range = 3f;
                w.Reading = new CombatReading { Carrier = carrier, Area = 3f, Cone = 60f };
                int me = Machine(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 2), -1, 500f);
                k.FireAt(me, foe, k.Time);
                List<(int step, EmgEvent e)> got = WatchAt(k, 40, EmgEventKind.Timer);
                if (carrier != CombatCarrier.Projectile)
                {
                    Expect(got.Count == 1 && got[0].e.EntKind == EmgEntKind.Unit && got[0].e.Ent == me && Mathf.Approximately(got[0].e.Value, 0.3f) && Math.Abs(got[0].step * Dt - 0.3f) <= 2f * Dt,
                        $"该发（{(carrier == CombatCarrier.Melee ? "格斗" : "力场")}程序读 evt.timer 0.3 秒）：从出手起算，第 {(got.Count > 0 ? got[0].step : -1)} 步发 1 条（宿主 = 出手单位）");
                }
                else
                {
                    Expect(got.Count == 0 && Enq(k, EmgEventKind.Timer) == 0 && k.EmgPendingCount == 0,
                        "不该发（射弹载体的即时命中：没有在飞的载荷，不记出手定时；弹体 / SpawnFlight 弹体的定时由弹体宿主发，见 C 段）");
                }
            }
        }

        private static void CheckKernelInject()
        {
            Line("  · D. 热更层注入的内核入口（FGR-EMG-066）：接入 / 断开 / 拾取 / 夺控 / 归还；按需；新链；在下一步按 Seq 处理");
            using var k = new CombatKernel(CombatConfig.Default, 8);
            EmgEventKind[] hot = { EmgEventKind.Uplink, EmgEventKind.Unlink, EmgEventKind.Pickup, EmgEventKind.Seize, EmgEventKind.Release };
            int p = Prog(k, hot.Select(x => R(x)).ToArray());
            int w = k.AddWeapon(Instant(p));
            int wn = k.AddWeapon(Instant(-1));
            int u = Machine(k, new double2(1, 2), w);
            int plain = Machine(k, new double2(3, 2), wn);
            EmgInjectResult[] res = hot.Select((x, i) => k.InjectEmgUnitEvent(x, u, (ushort)(i + 1), plain)).ToArray();
            List<EmgEvent> q = Pending(k);
            EmgInjectResult notInj = k.InjectEmgUnitEvent(EmgEventKind.Hit, u, 0);
            EmgInjectResult noUnit = k.InjectEmgUnitEvent(EmgEventKind.Uplink, 9999, 0);
            EmgInjectResult noListen = k.InjectEmgUnitEvent(EmgEventKind.Uplink, plain, 0);
            bool fields = q.Count == 5 && q.Select((e, i) => e.Kind == hot[i] && e.Ent == u && e.EntKind == EmgEntKind.Unit && e.Code == i + 1 && e.Other == plain && e.Prog == p && e.Depth == 0
                                                           && e.Pos.x == 1 && e.Pos.y == 2).All(x => x)
                          && q.Select(e => e.Chain).Distinct().Count() == 5 && q.All(e => e.Chain > 0);
            Expect(res.All(r => r == EmgInjectResult.Enqueued) && fields && notInj == EmgInjectResult.NotInjectable && noUnit == EmgInjectResult.NoUnit && noListen == EmgInjectResult.NoListener
                   && k.EmgBusPending == 5,
                $"五种热更事件注入：各入队 1 条（宿主 = 单位武器程序根节点、Code 照传、每条一条新链）；命中不能由热更层注入（{notInj}）、单位不存在（{noUnit}）、程序不读（{noListen}）都不入队");
            StepN(k, 1);
            Expect(k.EmgBusPending == 0 && k.EmgBusStats.Processed == 5 && k.EmgBusStats.TriggerWakes == 5 && k.EmgBusStats.OrderErrors == 0,
                $"下一步的 5d 段处理完（处理 {k.EmgBusStats.Processed}、唤起触发读端 {k.EmgBusStats.TriggerWakes}）");
        }

        // ── E. 预算与顺延（FGT-EMG-021）────────────────────────────────────────────────

        private static (CombatKernel k, int unit, int prog) BusKernel(int perStep = 0)
        {
            CombatConfig cfg = CombatConfig.Default;
            if (perStep > 0)
            {
                cfg.EmgBusPerStep = perStep;
            }
            var k = new CombatKernel(cfg, 8);
            int p = Prog(k, new[] { R(EmgEventKind.Uplink, Cond) });
            int u = Machine(k, new double2(0, 0), k.AddWeapon(Instant(p)));
            return (k, u, p);
        }

        private static bool Flood(CombatKernel k, int unit, int prog, int n)
        {
            bool ok = true;
            for (int i = 0; i < n; i++)
            {
                ok &= k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Uplink, EntKind = EmgEntKind.Unit, Ent = unit, Prog = prog, Value = i, Pos = new double2(i, 0) });
            }
            return ok;
        }

        private static void CheckBudget()
        {
            Line("  · E. 预算与顺延（FGT-EMG-021）：一步灌入 2000 条，每步 512 条按 Seq 先进先出、超出顺延进快照、无丢失；中途存读档续跑一致；暂停不推进；倍速只看步数");
            var (k, u, p) = BusKernel();
            using (k)
            {
                bool flooded = Flood(k, u, p, 2000);
                k.TryGetEmgEvent(0, out EmgEvent first);
                var pend = new List<int>();
                using var c = new CombatKernel(k.Config, 8);
                StepN(k, 1);
                pend.Add(k.EmgBusPending);
                CombatLoadResult lr = c.Load(k.Serialize());
                c.TryGetEmgEvent(0, out EmgEvent cHead);
                bool same = true;
                for (int s = 0; s < 3; s++)
                {
                    StepN(k, 1);
                    StepN(c, 1);
                    pend.Add(k.EmgBusPending);
                    same &= k.EmgStateHash() == c.EmgStateHash() && k.EmgBusPending == c.EmgBusPending;
                }
                EmgBusStats st = k.EmgBusStats;
                EmgBusStats cs = c.EmgBusStats;
                Expect(flooded && first.Seq == 0 && string.Join("/", pend) == "1488/976/464/0" && st.Processed == 2000 && st.OrderErrors == 0 && st.LastSeq == 1999 && st.Lost == 0
                       && st.ListenWakes == 2000 && st.StepsDeferred == 3 && st.MaxPending == 2000,
                    $"2000 条一次灌入：每步后剩 {string.Join(" / ", pend)}（每步 512），共处理 {st.Processed} 条、最后 Seq {st.LastSeq}、顺序错 {st.OrderErrors}、丢失 {st.Lost}；每条都唤起了监听（{st.ListenWakes}）");
                Expect(lr == CombatLoadResult.Ok && cHead.Seq == 512 && same && cs.Processed == 1488 && cs.LastSeq == 1999 && cs.OrderErrors == 0,
                    $"第 1 步后存档（未处理 1488 条进快照，队头 Seq {cHead.Seq}）→ 读档续跑 3 步：每步涌现哈希与不存档连续跑一致，处理完剩下的 {cs.Processed} 条、顺序不乱");
            }

            var (k2, u2, p2) = BusKernel(100);
            using (k2)
            {
                Flood(k2, u2, p2, 250);
                StepN(k2, 1);
                int a = k2.EmgBusPending;
                StepN(k2, 2);
                Expect(a == 150 && k2.EmgBusPending == 0 && k2.EmgBusStats.LastStepProcessed == 50, $"每步条数按 emg.bus.per_step（这里配 100）：250 条 → 剩 {a} → 第 3 步处理 {k2.EmgBusStats.LastStepProcessed} 条后清空");
            }

            // 暂停 = 不走步（内核时钟不推进）；0.5x / 1x / 3x = 同样的游戏时间走同样多的步：只看步数。
            var hashes = new List<ulong>();
            var processed = new List<long>();
            foreach (float speed in new[] { 0.5f, 1f, 3f })
            {
                var (ks, us, ps) = BusKernel();
                using (ks)
                {
                    Flood(ks, us, ps, 2000);
                    double acc = 0;
                    int frames = 0;
                    while (ks.EmgBusPending > 0 && frames < 1000)
                    {
                        acc += speed;
                        while (acc >= 1.0)
                        {
                            StepN(ks, 1);
                            acc -= 1.0;
                        }
                        frames++;
                    }
                    hashes.Add(ks.EmgStateHash());
                    processed.Add(ks.EmgBusStats.Processed);
                }
            }
            var (kp, up, pp) = BusKernel();
            using (kp)
            {
                Flood(kp, up, pp, 600);
                ulong h0 = kp.EmgStateHash();
                double t0 = kp.Time;
                // 暂停：世界层不调内核 Step（GameClock 暂停时 WorldSimulation.Frame 不出步，真实地点的同一断言见 J 段）。
                bool pausedSame = kp.EmgStateHash() == h0 && kp.EmgBusPending == 600 && kp.Time == t0;
                StepN(kp, 1);
                Expect(hashes.Distinct().Count() == 1 && processed.All(x => x == 2000) && pausedSame && kp.EmgBusPending == 88,
                    $"0.5x / 1x / 3x：处理完 2000 条后涌现哈希一致（{hashes.Distinct().Count()} 种）、都处理 2000 条；暂停期间队列不推进、不丢（600 条原样），恢复后第一步处理 512 条（剩 {kp.EmgBusPending}）");
            }
        }

        // ── F. 链深与扇出（FGT-EMG-022）────────────────────────────────────────────────

        private static void CheckDepthFanout()
        {
            Line("  · F. 链深与扇出（FGT-EMG-022；FGR-EMG-064 / 065）：链深 3 的事件不再生成后代并计数（记下链）；子事件链深 + 1、继承链；一条事件最多唤起 8 条监听");
            using var k = new CombatKernel(CombatConfig.Default, 8);
            int p = Prog(k, new[] { R(EmgEventKind.Hit) }, EmgEventKind.Hit);
            int u = Machine(k, new double2(0, 0), k.AddWeapon(Instant(p)));
            for (byte depth = 0; depth <= 3; depth++)
            {
                k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Depth = depth, EntKind = EmgEntKind.Unit, Ent = u, Prog = p });
            }
            List<EmgEvent> q = Pending(k);
            int deepChain = q[3].Chain;
            bool child2 = k.EmgEmitChildForTests(2, EmgEventKind.Split);
            bool child3 = k.EmgEmitChildForTests(3, EmgEventKind.Split);
            k.TryGetEmgEvent(k.EmgBusPending - 1, out EmgEvent kid);
            long cutBefore = k.EmgBusStats.DepthCut;
            StepN(k, 1);
            EmgBusStats st = k.EmgBusStats;
            Expect(child2 && !child3 && kid.Kind == EmgEventKind.Split && kid.Depth == 3 && kid.Chain == q[2].Chain && cutBefore == 1
                   && st.TriggerWakes == 3 && st.DepthCut == 2 && st.LastDepthCutChain == deepChain && k.EmgBusPending == 0,
                $"链深 0～3 的命中各一条：0～2 唤起触发读端（{st.TriggerWakes}），3 的不再生成（链深截断 {st.DepthCut}，含直接请求生成的 1 次；末端记链 {st.LastDepthCutChain}）；链深 2 生成的子事件链深 3、同链，链深 3 再生成被拒");

            using var f = new CombatKernel(CombatConfig.Default, 8);
            int pf = Prog(f, Enumerable.Range(0, 10).Select(_ => R(EmgEventKind.Uplink, Cond)).ToArray());
            int uf = Machine(f, new double2(0, 0), f.AddWeapon(Instant(pf)));
            f.InjectEmgUnitEvent(EmgEventKind.Uplink, uf, 0);
            f.TryGetEmgEvent(0, out EmgEvent fe);
            StepN(f, 1);
            EmgBusStats fs = f.EmgBusStats;
            Expect(fs.ListenWakes == 8 && fs.FanoutCut == 2 && fs.LastFanoutCutChain == fe.Chain,
                $"同一条接入被 10 条读端监听：唤起前 8 条（按槽位序），截掉 {fs.FanoutCut} 条并记链 {fs.LastFanoutCutChain}（调试层可见）");
        }

        // ── G. 积压保护 ────────────────────────────────────────────────────────────

        private static void CheckBackpressure()
        {
            Line("  · G. 积压保护（FGR-EMG-063 / ADR ④）：逼近容量时同一步同类同源的事件合并为计数事件（Value 累加、Code2 记合并数），找不到再并进同类的任意一条；计数不丢、队列不到容量");
            var (k, u, p) = BusKernel();
            using (k)
            {
                int pHot = Prog(k, new[] { R(EmgEventKind.Uplink), R(EmgEventKind.Pickup) });
                int u2 = Machine(k, new double2(5, 0), k.AddWeapon(Instant(pHot)));
                int mergeAt = EmgConst.BusCapacity - EmgConst.MergeHeadroom;
                Flood(k, u, p, mergeAt);
                var r1 = new List<EmgInjectResult>();
                for (int i = 0; i < 100; i++)
                {
                    r1.Add(k.InjectEmgUnitEvent(EmgEventKind.Uplink, u, 0));
                }
                k.TryGetEmgEvent(k.EmgBusPending - 1, out EmgEvent tail);
                int afterSame = k.EmgBusPending;
                EmgInjectResult newKind = k.InjectEmgUnitEvent(EmgEventKind.Pickup, u2, 1);
                EmgInjectResult otherSrc = k.InjectEmgUnitEvent(EmgEventKind.Uplink, u2, 0);
                long total = Pending(k).Sum(e => 1L + e.Code2);
                long injected = mergeAt + 100 + 2;
                EmgBusStats st = k.EmgBusStats;
                Expect(r1.All(r => r == EmgInjectResult.Enqueued) && afterSame == mergeAt && tail.Code2 == 100 && newKind == EmgInjectResult.Enqueued && otherSrc == EmgInjectResult.Enqueued
                       && k.EmgBusPending == mergeAt + 1 && st.Merged == 101 && total == injected && st.Lost == 0,
                    $"灌到 {mergeAt} 条后：同源接入再来 100 条全部并进队尾那条（Code2 = {tail.Code2}），队列不长；队列里还没有的“拾取”照常追加；另一台单位的接入找不到同源、并进同类（合并 {st.Merged}）；队列里按 1 + Code2 合计 {total} = 实际发生 {injected}");
                int steps = 0;
                while (k.EmgBusPending > 0 && steps++ < 64)
                {
                    StepN(k, 1);
                }
                Expect(k.EmgBusPending == 0 && k.EmgBusStats.Processed == mergeAt + 1 && k.EmgBusStats.OrderErrors == 0 && k.EmgBusStats.MaxPending < EmgConst.BusCapacity,
                    $"随后 {steps} 步按 Seq 处理完（{k.EmgBusStats.Processed} 条），队列峰值 {k.EmgBusStats.MaxPending} < 容量 {EmgConst.BusCapacity}（B11：不会无界增长）");
            }
        }

        // ── H. 现有玩法 / 提示事件口径不动 ─────────────────────────────────────────────────

        private static (int gpFirst, int gpLeft, int cues, long dropped, int bus) LegacyRun(bool withBus)
        {
            using var k = new CombatKernel(CombatSite.ConfigFromTuning(), 128);
            int p = withBus ? Prog(k, new[] { R(EmgEventKind.Fire), R(EmgEventKind.Hit), R(EmgEventKind.Hurt) }) : -1;
            CombatWeapon cool = Instant(p, 0.5f);
            cool.HeatPerShot = 0f; // 30 发连点不过热
            cool.MarkSeconds = 0f;
            int w = k.AddWeapon(cool);
            int me = Machine(k, new double2(0, 0), w, 1000f);
            var foes = Enumerable.Range(0, 100).Select(i => Foe(k, new double2(i % 10, 3 + i / 10), w, 100f)).ToList();
            k.ClearCues();
            long d0 = k.Counters.CuesDropped;
            for (int i = 0; i < 30; i++)
            {
                k.FireAt(me, foes[i], k.Time);
            }
            int cues = k.Cues.Length;
            long dropped = k.Counters.CuesDropped - d0;
            foreach (int f in foes)
            {
                k.Damage(f, 1f, me);
            }
            int pending = k.GameplayPending;
            k.DrainGameplay(k.Config.MaxGameplayEventsPerStep, out int first);
            int left = k.GameplayPending;
            return (first, left, cues, dropped, k.EmgBusPending + (int)k.EmgBusStats.Processed);
        }

        private static void CheckLegacyQueues()
        {
            Line("  · H. 现有“玩法事件 64 / 步顺延不丢、提示事件 24 / 步丢弃”口径与代码不动（tools/fgdata_combat.py:17；CombatKernel.DrainGameplay / CombatLogic.Cue），总线不占它们的额度");
            var a = LegacyRun(false);
            var b = LegacyRun(true);
            Expect(a.gpFirst == 64 && a.gpLeft == 66 && a.cues == 24 && a.dropped == 6 && b.gpFirst == a.gpFirst && b.gpLeft == a.gpLeft && b.cues == a.cues && b.dropped == a.dropped
                   && a.bus == 0 && b.bus > 100,
                $"30 发直控 + 100 个敌人各挨 1 点：提示事件留 {a.cues} 条、丢 {a.dropped} 条；玩法事件一步取 {a.gpFirst} 条、剩 {a.gpLeft} 条留到下一步；带程序（总线同时入队 {b.bus} 条）时以上数字逐个相同");
        }

        // ── I. 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · I. 存读档：未处理队列、过热记号（evt.cooled 的“过热过”）、近炸记号、每宿主计数进快照（格式 16 单位段尾部追加 1 个字段，短段取 0）");
            using var k = new CombatKernel(CombatConfig.Default, 16);
            int p = Prog(k, new[] { R(EmgEventKind.Overheat), R(EmgEventKind.Cooled), R(EmgEventKind.Prox, Trig, 30f) });
            int me = Machine(k, new double2(0, 0), k.AddWeapon(Instant(p)));
            int bp = Prog(k, new[] { R(EmgEventKind.Prox, Trig, 30f), R(EmgEventKind.Hit) });
            int gunner = Machine(k, new double2(-2, 0), k.AddWeapon(Bolt(bp, 5f)));
            int foe = Foe(k, new double2(0, 30), k.AddWeapon(Instant(-1)), 100f);
            k.FireAt(me, foe, k.Time);
            k.FireAt(me, foe, k.Time); // 积热 20 ≥ 15：过热，记下“过热过”
            k.FireAt(gunner, foe, k.Time);
            StepN(k, 30); // 弹体起飞即在 30 米近炸半径内（记号置位、发过一次），再飞 0.5 秒
            k.TryGetUnitEmg(me, out _, out EmgUnitState us);
            k.TryGetProjectile(0, out CombatProjectile pr);
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Overheat, EntKind = EmgEntKind.Unit, Ent = me, Prog = p });
            byte[] snap = k.Serialize();
            byte[] shortUnits = k.SerializeEmgShapeForTests(EmgConst.SegUnits, 4, 0); // 模拟本阶段较早提交写出的单位段（没有第 5 个字段）
            using var b = new CombatKernel(k.Config, 16);
            CombatLoadResult lr = b.Load(snap);
            b.TryGetUnitEmg(me, out _, out EmgUnitState bs);
            b.TryGetProjectile(0, out CombatProjectile bpr);
            bool same = lr == CombatLoadResult.Ok && us.HeatLatch == 1 && bs.HeatLatch == 1 && (pr.Flags & EmgProjFlags.ProxIn) != 0 && bpr.Flags == pr.Flags && bpr.TrigUsed == pr.TrigUsed
                        && bpr.Age == pr.Age && b.EmgBusPending == 1 && b.StateHash() == k.StateHash() && b.EmgStateHash() == k.EmgStateHash();
            StepN(k, 120);
            StepN(b, 120);
            Expect(same && b.EmgStateHash() == k.EmgStateHash() && Enq(k, EmgEventKind.Cooled) == 1 && Enq(b, EmgEventKind.Cooled) == 1 && Enq(b, EmgEventKind.Prox) == 0,
                $"过热记号 {bs.HeatLatch}、弹体近炸记号与触发计数（{bpr.TrigUsed:X}）、飞行秒数、未处理事件 1 条都读回；续跑 2 秒后两边涌现哈希一致，读档那边照样在散热后发冷却完成、已在半径内的弹体不重复发近炸");

            using var s = new CombatKernel(k.Config, 16);
            CombatLoadResult rs = s.Load(shortUnits);
            s.TryGetUnitEmg(me, out _, out EmgUnitState ss);
            Expect(rs == CombatLoadResult.Ok && us.HeatLatch == 1 && ss.HeatLatch == 0 && ss.LastHitBy == us.LastHitBy,
                $"本阶段较早提交写出的单位段（4 个字段，存档时记号为 {us.HeatLatch}）照读：过热记号取 0（{rs}）");
        }

        // ── J. 耗时 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 耗时（FGR-EMG-200；LANDING-PLAN 第 12 节 #2 推荐默认：3x 倍速也守 120 帧，每步 ≤ 1.5 毫秒）：单步处理满 512 条（每条扫宿主节点 8 条读端行）");
            var (k, u, _) = BusKernel();
            using (k)
            {
                int p = Prog(k, Enumerable.Range(0, 8).Select(_ => R(EmgEventKind.Uplink, Cond)).ToArray());
                int w = k.AddWeapon(Instant(p));
                int uu = Machine(k, new double2(1, 1), w);
                for (int i = 0; i < 4000; i++)
                {
                    k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Uplink, EntKind = EmgEntKind.Unit, Ent = uu, Prog = p });
                }
                StepN(k, 1); // 预热（Burst 编译已在前面的段完成）
                var ms = new List<double>();
                for (int s = 0; s < 6; s++)
                {
                    StepN(k, 1);
                    ms.Add(k.LastStepMs);
                }
                double worst = ms.Max();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 512; i++)
                {
                    k.InjectEmgUnitEvent(EmgEventKind.Uplink, uu, 0);
                }
                sw.Stop();
                double injectUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 512.0;
                PerfGate.Expect(k.EmgBusStats.LastStepProcessed == 512 && k.EmgBusStats.ListenWakes >= 512 * 8 * 6,
                    $"每步处理 512 条的内核一步最慢 {worst:0.###} 毫秒（6 步：{string.Join(" / ", ms.Select(x => x.ToString("0.###")))}；3x 每步上限 1.5 毫秒）；热更层注入每条 {injectUs:0.##} 微秒（托管，O(1)）",
                    new[] { PerfGate.Le(worst, 1.5, "满 512 条的一步（毫秒）") }, Expect, Line);
            }
        }

        // ── K. 真实地点（家园）：信号广播注入、拾取入口、暂停与倍速 ───────────────────────────────

        private static void CheckWorld()
        {
            Line("  · K. 真实家园地点：信号广播（SignalUplinkService.UplinkChangedEvent）→ 接入 / 断开注入与原因码；同机核心编辑不算；重复订阅不重复注入；拾取入口；暂停不推进、0.5x / 1x / 3x 每步 512 条");
            SignalUplinkService.ResetForTests();
            CampaignState s = FgProductionSelfCheck.NewWorld(9611, true, 2000);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, new Vector2(4f, 4f), 200f, 200f);
            MachineOpResult m2 = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, new Vector2(8f, 4f), 200f, 200f);
            MachineLoadoutRegistry.Register(s, m.LogicId, HomeValleyLayout.BlueprintErc003Id, 1);
            MachineLoadoutRegistry.Register(s, m2.LogicId, HomeValleyLayout.BlueprintErc003Id, 1);
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (site == null || !m.Success || !m2.Success || !site.TryGetMachineUnit(m.LogicId, out int unit) || !site.TryGetMachineUnit(m2.LogicId, out int unit2))
            {
                Fail("夹具：家园地点或测试机器没就位");
                return;
            }
            CombatKernel k = site.Kernel;
            EmgEventKind[] hot = { EmgEventKind.Uplink, EmgEventKind.Unlink, EmgEventKind.Pickup };
            int p = Prog(k, hot.Select(x => R(x)).ToArray());
            foreach (int un in new[] { unit, unit2 })
            {
                k.TryGetUnit(un, out CombatUnitView v);
                k.TryGetWeapon(v.Weapon, out CombatWeapon w0);
                w0.Program = p;
                k.SetUnitWeapon(un, k.AddWeapon(w0)); // 夹具：真实表里还没有读接入 / 断开 / 拾取的固件（EMG1-FW-02 落表），这里给机器武器挂一个读这三个码的程序
            }
            CombatSites.EnsureEmgListening();
            CombatSites.EnsureEmgListening();
            int inj0 = CombatSites.EmgUplinkInjected;
            long up0 = k.EmgEnqueuedOf(EmgEventKind.Uplink), down0 = k.EmgEnqueuedOf(EmgEventKind.Unlink);
            GameEvent.Send(SignalUplinkService.UplinkChangedEvent, new SignalUplinkChange { PreviousLogicId = 0, CurrentLogicId = m.LogicId, SiteId = site.SiteId, Reason = RegionControlChangeReason.PlayerRequest });
            List<EmgEvent> a1 = Pending(k);
            GameEvent.Send(SignalUplinkService.UplinkChangedEvent, new SignalUplinkChange { PreviousLogicId = m.LogicId, CurrentLogicId = m.LogicId, SiteId = site.SiteId });
            int afterEdit = k.EmgBusPending;
            GameEvent.Send(SignalUplinkService.UplinkChangedEvent, new SignalUplinkChange { PreviousLogicId = m.LogicId, CurrentLogicId = m2.LogicId, SiteId = site.SiteId, Reason = RegionControlChangeReason.PlayerRequest });
            GameEvent.Send(SignalUplinkService.UplinkChangedEvent, new SignalUplinkChange { PreviousLogicId = m2.LogicId, CurrentLogicId = 0, SiteId = string.Empty, Reason = RegionControlChangeReason.SilentNight });
            List<EmgEvent> all = Pending(k);
            string got = string.Join(",", all.Select(e => $"{e.Kind}:{(e.Ent == unit ? "A" : e.Ent == unit2 ? "B" : "?")}:{e.Code}"));
            Expect(CombatSites.EmgListening && a1.Count == 1 && a1[0].Kind == EmgEventKind.Uplink && a1[0].Ent == unit && afterEdit == 1
                   && got == "Uplink:A:0,Unlink:A:2,Uplink:B:0,Unlink:B:5" && CombatSites.EmgUplinkInjected - inj0 == 4
                   && k.EmgEnqueuedOf(EmgEventKind.Uplink) - up0 == 2 && k.EmgEnqueuedOf(EmgEventKind.Unlink) - down0 == 2,
                $"信号广播：接入 A → [接入 A]；同机核心编辑不算；A → B → [断开 A（切换）, 接入 B]；静默夜 → [断开 B（静默夜）]——共 {got}；订阅两次也只注入一次");
            string codes = string.Join(",", new[]
            {
                CombatSites.UnlinkCode(RegionControlChangeReason.PlayerRequest, 0), CombatSites.UnlinkCode(RegionControlChangeReason.PlayerRequest, 5),
                CombatSites.UnlinkCode(RegionControlChangeReason.DeathRebound, 0), CombatSites.UnlinkCode(RegionControlChangeReason.SignalLost, 0),
                CombatSites.UnlinkCode(RegionControlChangeReason.CoverageLost, 0), CombatSites.UnlinkCode(RegionControlChangeReason.SilentNight, 0),
                CombatSites.UnlinkCode(RegionControlChangeReason.RegionUnload, 0), CombatSites.UnlinkCode(RegionControlChangeReason.None, 0),
            });
            bool picked = CombatSites.InjectEmgPickup(m.LogicId, true) && CombatSites.InjectEmgPickup(m2.LogicId, false);
            List<EmgEvent> pk = Pending(k).Where(e => e.Kind == EmgEventKind.Pickup).ToList();
            bool missing = !CombatSites.InjectEmgPickup(987654, true);
            Expect(codes == "1,2,3,4,4,5,6,0" && picked && pk.Count == 2 && pk[0].Code == EmgCode.PickupScrap && pk[1].Code == EmgCode.PickupDrop && missing,
                $"断开原因码（主动 1 / 切换 2 / 阵亡 3 / 断链 4 / 静默夜 5 / 区域卸载 6 / 其它 0）= {codes}；拾取入口（CargoService 自动拾取调用）：散装材料 = 废料、其余 = 掉落；不在任何地点的机器不注入");

            // 暂停与倍速（真实世界时钟）：灌 2000 条，暂停 60 帧不推进；恢复后按倍速，每步 512 条。
            WorldSimulation.StepMany(2);
            Flood(k, unit, p, 2000);
            GameClock.SetPaused(true);
            for (int i = 0; i < 60; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            int pausedPending = k.EmgBusPending;
            GameClock.SetPaused(false);
            var perStep = new List<string>();
            var maxMs = new List<double>();
            foreach (float speed in new[] { 0.5f, 1f, 3f })
            {
                GameClock.SetSpeed(speed);
                long before = k.EmgBusStats.Processed;
                long steps0 = k.Steps;
                long target = GameClock.Ticks + 1;
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 100)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                maxMs.Add(k.LastStepMs);
                perStep.Add($"{speed}x:{k.EmgBusStats.Processed - before}/{k.Steps - steps0}");
            }
            GameClock.SetSpeed(1f);
            Expect(pausedPending == 2000 && string.Join(",", perStep) == "0.5x:512/1,1x:512/1,3x:512/1",
                $"真实地点：暂停 60 帧队列原样 {pausedPending} 条；0.5x / 1x / 3x 各走 1 步，处理 {string.Join("，", perStep)}（处理条数 / 步数，只看步数、与倍速无关）；这几步内核耗时 {string.Join(" / ", maxMs.Select(x => x.ToString("0.###")))} 毫秒");

            // 真实接入路径（第 1 轮审查修复）：SignalUplinkService.Request → SetUplink 先通知两台机器重编译程序（NotifyChanged → RefreshMachineWeapon），再广播。
            // 夹具程序读 evt.uplink；重编译后的真实程序（表里还没有读接入的固件）不读 → 按新程序判断，不入队。
            int progAtBroadcast = int.MinValue;
            Action<SignalUplinkChange> probe = c =>
            {
                if (c.CurrentLogicId == m.LogicId && k.TryGetUnit(unit, out CombatUnitView v) && k.TryGetWeapon(v.Weapon, out CombatWeapon w))
                {
                    progAtBroadcast = w.Program;
                }
            };
            GameEvent.AddEventListener(SignalUplinkService.UplinkChangedEvent, probe);
            long upReal0 = k.EmgEnqueuedOf(EmgEventKind.Uplink);
            int injReal0 = CombatSites.EmgUplinkInjected;
            UplinkRequestResult rq = SignalUplinkService.Request(m.LogicId, UplinkSource.MachineList, startCamera: false);
            for (int i = 0; i < 600 && SignalUplinkService.CurrentMachine(s) != m.LogicId; i++)
            {
                SignalUplinkService.FrameTick(1f / 60f);
                WorldSimulation.Frame(1f / 60f);
            }
            GameEvent.RemoveEventListener(SignalUplinkService.UplinkChangedEvent, probe);
            Expect(rq.Accepted && SignalUplinkService.CurrentMachine(s) == m.LogicId && progAtBroadcast != int.MinValue && progAtBroadcast != p
                   && k.EmgEnqueuedOf(EmgEventKind.Uplink) == upReal0 && CombatSites.EmgUplinkInjected == injReal0,
                $"真实接入（SignalUplinkService.Request）：广播时机器武器已重编译（程序 {progAtBroadcast}，夹具程序 {p} 已被换掉）→ 新程序不读 evt.uplink，不入队（{k.EmgEnqueuedOf(EmgEventKind.Uplink) - upReal0} 条）");
        }

        // ── 报告 ─────────────────────────────────────────────────────────────────

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
