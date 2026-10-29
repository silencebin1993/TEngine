using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FLASH-01 刷写带宽与信号离开时的程序处置的自动验收（FG19 FGR-EMG-036 / 037；FG01 FGR-SIG-032 / 033；ADR-EMG-001 ③；FGT-EMG-012 / 013）。
    /// 起真实内核（Burst 步进）、真实编译器与 CombatSite 桥接、真实家园地点断言行为：
    /// A 数据（emg.flash.* 表值进内核配置、文本键）；B FGT-EMG-012 带宽截断（满了再放 = 0 条只有基础攻击、已存在的不受影响、开火事件对照）；
    /// C 距离衰减（飞出范围暂停末尾、飞回恢复、每 0.25 游戏秒重算、暂停不重算、0.5x / 1x / 3x 步长）；D FGT-EMG-013 信号离开（剥离核心、在飞载荷按原程序、不再续命、热量留机体、无核心不动）；
    /// E 存读档（占用量与截断数一致、续跑逐位相同、短段兼容）；F 热更层变体（真实编译：截断程序、去核心、剥离换算、0 条读法 = 没装固件；预览预判；悬停读数）；
    /// W 真实家园地点：无人机舱装配走正式编译挂变体表，主动离开 / 断链 / 静默夜三种原因各一遍（信号广播 → 注入 → 剥离），1x / 0.5x / 3x 与暂停；L 耗时；
    /// T 无人机舱炮塔接入后主动离开 / 静默夜 / 禁用 / 换接各一遍（TurretUplink → 剥离、不再续命）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgFlashSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgFlashSelfCheck。
    /// </summary>
    public static class FgEmgFlashSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const string Bp = "bp_emg_flash_bay";
        private const string MarkTag = "fw_marktag", Split = "fw_split", Pierce = "fw_pierce";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/刷写带宽")]
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
            Line("\n[刷写带宽] EMG1-FLASH-01：持续实体的刷写带宽、距离衰减、信号离开时剥离核心指令（FGT-EMG-012 / 013）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckData);
                Step(CheckBandwidth);
                Step(CheckRange);
                Step(CheckUnlink);
                Step(CheckSaveLoad);
                Step(CheckHotfix);
                Step(CheckPerformance);
                Step(CheckWorld);
                Step(CheckTurret);
                Step(CheckTestRange);
            }
            catch (Exception e)
            {
                Fail($"刷写带宽自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                StatusTagHover.Leave(null);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [刷写带宽] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>一个 n 条指令的程序：根节点读 evt.fire（无人机每次开火都入队，事件的 Prog 说明它此刻执行的是哪一份）+ n − 1 条感知行（端口码 <paramref name="tag"/> 起，区分不同程序）。</summary>
        private static int Prog(CombatKernel k, int n, int tag)
        {
            var instrs = new List<EmgInstr> { new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Fire), Role = EmgConst.RoleTrigger } };
            for (int i = 1; i < n; i++)
            {
                instrs.Add(new EmgInstr { Port = (ushort)(tag + i), Role = 3, Ordinal = (byte)i });
            }
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            return k.AddProgram(new EmgProgram { ListenMask = 1u << (int)EmgEventKind.Fire }, nodes, instrs);
        }

        private static CombatWeapon DroneW(int program, int drones, bool anchored = false, bool flight = false)
        {
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = program,
                Reading = new CombatReading
                {
                    Carrier = CombatCarrier.Summon, Drones = drones, DroneSeconds = 60f, DroneRatio = 0.5f, DroneCooldown = 0.5f, DroneLeash = 8f,
                    DroneAnchored = (byte)(anchored ? 1 : 0),
                },
            };
            if (flight)
            {
                w.SpawnFlight = 1;
                w.ProjectileSpeed = 2f;
                w.ProjectileRadius = 0.15f;
                w.ProjectileLife = 6f;
            }
            return w;
        }

        /// <summary>内核夹具：全长 4 条（第 0 条算核心）的无人机武器 + 手工变体表（全长 P1..P4、去核心 S1..S3、剥离换算 [0,0,1,2,3]）。</summary>
        private sealed class Bay
        {
            public int Self;
            public int[] P = new int[5];
            public int[] S = new int[4];
        }

        private static Bay MakeBay(CombatKernel k, int drones, bool anchored = false, bool flight = false)
        {
            var b = new Bay();
            b.P[0] = -1;
            b.S[0] = -1;
            for (int i = 1; i <= 4; i++)
            {
                b.P[i] = Prog(k, i, 100);
            }
            for (int i = 1; i <= 3; i++)
            {
                b.S[i] = Prog(k, i, 200);
            }
            int[] full = b.P.Select(p => k.AddWeapon(DroneW(p, drones, anchored, flight))).ToArray();
            int[] soft = b.S.Select(p => k.AddWeapon(DroneW(p, drones, anchored, flight))).ToArray();
            b.Self = full[4];
            if (!k.SetFlashVariants(b.Self, full, soft, new[] { 0, 0, 1, 2, 3 }))
            {
                Fail("夹具：变体表登记失败");
            }
            return b;
        }

        private static CombatConfig Cfg(int bandwidth = 8)
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgFlashBandwidth = bandwidth;
            c.EmgFlashRange = 8f;
            c.EmgFlashStep = 4f;
            return c;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, float hp) => Unit(k, f, p, -1, hp);

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static List<CombatDroneFlash> Flash(CombatKernel k)
        {
            var list = new List<CombatDroneFlash>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDroneFlash(i, out CombatDroneFlash f))
                {
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>逐步记下处理过的 evt.fire（按 Seq 变化；同一步多条只记最后一条）。</summary>
        private static List<EmgEvent> WatchFire(CombatKernel k, int steps, float dt = Dt)
        {
            var got = new List<EmgEvent>();
            int last = k.TryGetLastEmgEvent(EmgEventKind.Fire, out EmgEvent e0) ? e0.Seq : int.MinValue;
            for (int s = 0; s < steps; s++)
            {
                StepN(k, 1, dt);
                if (k.TryGetLastEmgEvent(EmgEventKind.Fire, out EmgEvent e) && e.Seq != last)
                {
                    last = e.Seq;
                    got.Add(e);
                }
            }
            return got;
        }

        private static string Ints(IEnumerable<int> xs) => string.Join(",", xs);

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：fg.TbHomeTuning emg.flash.bandwidth / range / step = 8 / 8 / 4，经 CombatSite.ConfigFromTuning 进内核；悬停与原因文本键（中英）");
            bool b = GridContent.TryGetTuning("emg.flash.bandwidth", out float bw);
            bool r = GridContent.TryGetTuning("emg.flash.range", out float range);
            bool s = GridContent.TryGetTuning("emg.flash.step", out float step);
            CombatConfig c = CombatSite.ConfigFromTuning();
            Expect(b && r && s && bw == 8f && range == 8f && step == 4f && c.EmgFlashBandwidth == 8 && c.EmgFlashRange == 8f && c.EmgFlashStep == 4f,
                $"表值 {bw} / {range} / {step}，内核配置 {c.EmgFlashBandwidth} / {c.EmgFlashRange} / {c.EmgFlashStep}");
            string[] keys =
            {
                "emg.flash.hover.title", "emg.flash.hover.held", "emg.flash.hover.full", "emg.flash.hover.range", "emg.flash.hover.stripped",
                EmgProgramTreeText.NoteFlashTruncated, EmgProgramTreeText.NoteFlashRange,
                EmgProgramTreeText.FixKey(EmgProgramTreeText.NoteFlashTruncated), EmgProgramTreeText.FixKey(EmgProgramTreeText.NoteFlashRange),
            };
            string missing = string.Join("、", keys.Where(x => !GameText.Has(x)));
            Expect(missing.Length == 0 && GameText.Format("emg.flash.hover.held", "3", "4") == "刷写 3 / 4 条" && GameText.Get("emg.flash.hover.full") == "（带宽已满）"
                   && GameText.Format("emg.flash.hover.range", "1") == "离母机过远，暂停 1 条",
                $"文本键齐全{(missing.Length > 0 ? "（缺 " + missing + "）" : string.Empty)}；“{GameText.Format("emg.flash.hover.held", "3", "4")}{GameText.Get("emg.flash.hover.full")}”“{GameText.Format("emg.flash.hover.range", "1")}”");
        }

        // ── B. FGT-EMG-012 带宽 ─────────────────────────────────────────────────────────────

        private static void CheckBandwidth()
        {
            Line("  · B. FGT-EMG-012：母机带宽 8、程序 4 条、一次放 3 架 → 4 / 4 / 0（第三架截到 0 = 只有基础攻击）；已存在的不受后放的影响；开火事件对照（带宽 12 时三架都执行程序）");
            var runs = new List<(List<CombatDroneFlash> f, HashSet<int> ents, HashSet<int> progs, long fires, int used, float foeLost)>();
            Bay bay0 = null;
            foreach (int bw in new[] { 8, 12 })
            {
                using var k = new CombatKernel(Cfg(bw), 32);
                Bay bay = MakeBay(k, 3);
                bay0 ??= bay;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), bay.Self, 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), 1e6f);
                k.FireAt(me, foe, k.Time);
                List<CombatDroneFlash> f0 = Flash(k);
                long fire0 = k.EmgEnqueuedOf(EmgEventKind.Fire);
                List<EmgEvent> ev = WatchFire(k, 240);
                k.FireAt(me, foe, k.Time); // 再开火：续命已在场的，不再多放（已满 3 架）
                StepN(k, 1);
                List<CombatDroneFlash> f1 = Flash(k);
                k.TryGetUnit(foe, out CombatUnitView fv);
                runs.Add((f1, new HashSet<int>(ev.Where(e => e.EntKind == EmgEntKind.Drone).Select(e => e.Ent)), new HashSet<int>(ev.Select(e => e.Prog)),
                    k.EmgEnqueuedOf(EmgEventKind.Fire) - fire0, k.FlashUsed(me), fv.MaxHealth - fv.Health));
                if (bw == 8)
                {
                    Expect(f0.Count == 3 && Ints(f0.Select(x => x.Held)) == "4,4,0" && Ints(f0.Select(x => x.Truncated)) == "0,0,4" && k.FlashUsed(me) == 8
                           && f0[2].Program == -1 && f0[0].Program == bay.P[4] && f0[1].Program == bay.P[4] && k.FlashBandwidth == 8,
                        $"放出：持有 {Ints(f0.Select(x => x.Held))}、截掉 {Ints(f0.Select(x => x.Truncated))}，母机占用 {k.FlashUsed(me)} / {k.FlashBandwidth}；第三架执行程序 {f0[2].Program}（−1 = 只有基础攻击）");
                }
            }
            (List<CombatDroneFlash> a, HashSet<int> aEnts, HashSet<int> aProgs, long aFires, int aUsed, float aLost) = runs[0];
            (List<CombatDroneFlash> c, HashSet<int> cEnts, _, long cFires, int cUsed, _) = runs[1];
            Expect(Ints(a.Select(x => x.Held)) == "4,4,0" && aUsed == 8 && aEnts.Count == 2 && !aEnts.Contains(a[2].Id) && aProgs.SetEquals(new[] { bay0.P[4] }) && aLost > 0f,
                $"已存在的不受影响：再开火续命后仍 {Ints(a.Select(x => x.Held))}、占用 {aUsed}；4 秒内发 evt.fire 的只有前两架（{Ints(aEnts)}），程序都是全长那份；第三架照样打出基础攻击（敌人累计掉血 {aLost:0}）");
            Expect(Ints(c.Select(x => x.Held)) == "4,4,4" && cUsed == 12 && cEnts.Count == 3 && cFires > aFires,
                $"对照（带宽 12）：持有 {Ints(c.Select(x => x.Held))}、占用 {cUsed}，三架都发 evt.fire（{cEnts.Count} 架、{cFires} 条 > 带宽 8 时 {aFires} 条）");

            // 没有变体表（没有程序）的无人机：旧行为逐字不变，不占带宽、不弹读数。
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int w = k.AddWeapon(DroneW(-1, 3));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), 1e6f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                Expect(k.DroneCount == 3 && Flash(k).Count == 0 && k.FlashUsed(me) == 0 && k.FlashHeadOf(w).x < 0,
                    $"负向：不带程序的无人机舱放 {k.DroneCount} 架，没有刷写读数、不占带宽（旧行为不变）");
            }
        }

        // ── C. 距离衰减 ─────────────────────────────────────────────────────────────

        private static void CheckRange()
        {
            Line("  · C. 距离衰减：哨戒桩插下后母机走开——超出 8 米每再远 4 米（不足一档按一档）暂停末尾 1 条，回来即恢复；每 0.25 游戏秒重算，暂停（dt = 0）不重算；0.5x / 1x / 3x 步长同样 ≤ 0.25 游戏秒生效");
            foreach (float dt in new[] { Dt / 2f, Dt, Dt * 3f })
            {
                using var k = new CombatKernel(Cfg(), 16);
                Bay bay = MakeBay(k, 1, anchored: true);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), bay.Self, 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), 1e6f);
                k.FireAt(me, foe, k.Time);
                k.TryGetDrone(0, out CombatDrone stake);
                int steps = (int)Math.Ceiling(0.3 / dt); // 一个重算节拍（0.25 游戏秒）之内
                var seen = new List<string>();
                bool ok = true;
                foreach ((double away, int pause) in new[] { (13.0, 2), (21.0, 4), (3.0, 0), (12.5, 2), (8.0, 0) })
                {
                    k.SetPosition(me, stake.Anchor + new double2(0, -away));
                    StepN(k, steps, dt);
                    CombatDroneFlash f = Flash(k)[0];
                    List<EmgEvent> ev = WatchFire(k, (int)Math.Ceiling(1.2 / dt), dt);
                    int expectProg = bay.P[4 - pause];
                    bool progOk = pause == 4 ? ev.Count == 0 : ev.Count > 0 && ev.All(e => e.Prog == expectProg);
                    ok &= f.Pause == pause && f.Effective == 4 - pause && progOk;
                    seen.Add($"{away} 米→暂停 {f.Pause}、开火程序 {(ev.Count > 0 ? ev[ev.Count - 1].Prog.ToString() : "无事件")}");
                }
                // 暂停：dt = 0 的步不重算
                k.SetPosition(me, stake.Anchor + new double2(0, -21));
                for (int i = 0; i < 60; i++)
                {
                    k.Step(0f, k.Time);
                }
                int pausedPause = Flash(k)[0].Pause;
                StepN(k, steps, dt);
                ok &= pausedPause == 0 && Flash(k)[0].Pause == 4;
                Expect(ok, $"步长 {dt * 60f:0.#}/60 秒：{string.Join("；", seen)}；暂停 60 步仍暂停 {pausedPause} 条，恢复后一个节拍内变 {Flash(k)[0].Pause}");
            }
        }

        // ── D. FGT-EMG-013 信号离开 ─────────────────────────────────────────────────────────────

        private static void CheckUnlink()
        {
            Line("  · D. FGT-EMG-013：evt.unlink 注入（主动离开 / 断链 / 静默夜）→ 母机的无人机剥离核心（全长 4 → 去核心 3），在飞弹体按发射时程序走完，剥离过的不再续命打到到期；热量留机体；别的母机不动；无核心程序条数不变但同样不再续命");
            foreach (ushort code in new[] { EmgCode.UnlinkLeave, EmgCode.UnlinkSevered, EmgCode.UnlinkSilentNight })
            {
                using var k = new CombatKernel(Cfg(), 32);
                Bay bay = MakeBay(k, 1, flight: true);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), bay.Self, 500f);
                int other = Unit(k, CombatFaction.Player, new double2(40, 0), bay.Self, 500f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), 1e6f);
                int foe2 = Unit(k, CombatFaction.Hostile, new double2(40, 6), 1e6f);
                k.FireAt(me, foe, k.Time);
                k.FireAt(other, foe2, k.Time);
                CombatProjectile inFlight = default;
                for (int i = 0; i < 600 && inFlight.Id == 0; i++)
                {
                    StepN(k, 1);
                    for (int p = 0; p < k.ProjectileCount; p++)
                    {
                        if (k.TryGetProjectile(p, out CombatProjectile pr) && pr.Prog == bay.P[4])
                        {
                            inFlight = pr;
                        }
                    }
                }
                k.TryGetUnit(me, out CombatUnitView mv0);
                double until0 = Until(k, me);
                EmgInjectResult r = k.InjectEmgUnitEvent(EmgEventKind.Unlink, me, code);
                List<CombatDroneFlash> f = Flash(k);
                CombatDroneFlash mine = f.First(x => x.Owner == me), theirs = f.First(x => x.Owner == other);
                bool flightKept = false;
                for (int p = 0; p < k.ProjectileCount; p++)
                {
                    flightKept |= k.TryGetProjectile(p, out CombatProjectile pr) && pr.Id == inFlight.Id && pr.Prog == bay.P[4];
                }
                k.TryGetUnit(me, out CombatUnitView mv1);
                var newProgs = new HashSet<int>();
                for (int i = 0; i < 180; i++)
                {
                    StepN(k, 1);
                    for (int p = 0; p < k.ProjectileCount; p++)
                    {
                        if (k.TryGetProjectile(p, out CombatProjectile pr) && pr.Id > inFlight.Id && pr.Prog >= 0 && pr.Pos.x < 20.0) // 只看这台母机一侧（另一台在 x = 40）
                        {
                            newProgs.Add(pr.Prog);
                        }
                    }
                }
                k.FireAt(me, foe, k.Time); // 母机再开火：剥离过的不续命
                StepN(k, 1);
                double until1 = Until(k, me);
                Expect(inFlight.Id != 0 && mine.Stripped && mine.Held == 3 && mine.Length == 3 && mine.Program == bay.S[3] && !theirs.Stripped && theirs.Held == 4
                       && flightKept && newProgs.SetEquals(new[] { bay.S[3] }) && Math.Abs(until1 - until0) < 1e-9 && mv0.Heat == mv1.Heat && k.FlashUsed(me) == 3,
                    $"原因码 {code}（注入 {r}）：剥离后持有 {mine.Held} / {mine.Length}、执行去核心程序 {mine.Program}（应 {bay.S[3]}），另一台母机的不动（{theirs.Held}）；"
                    + $"在飞弹体 #{inFlight.Id} 仍按原程序 {bay.P[4]}（{flightKept}）；之后新弹体程序 {Ints(newProgs)}；母机再开火到期时刻不变（{until0:0.##} → {until1:0.##}）；母机热量 {mv0.Heat:0.#} → {mv1.Heat:0.#}");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p4 = Prog(k, 4, 300);
                int p2 = Prog(k, 2, 300);
                int[] full = { k.AddWeapon(DroneW(-1, 1)), k.AddWeapon(DroneW(Prog(k, 1, 300), 1)), k.AddWeapon(DroneW(p2, 1)), k.AddWeapon(DroneW(Prog(k, 3, 300), 1)), k.AddWeapon(DroneW(p4, 1)) };
                k.SetFlashVariants(full[4], full, full, new[] { 0, 1, 2, 3, 4 });
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), full[4], 500f);
                k.FireAt(me, Unit(k, CombatFaction.Hostile, new double2(0, 6), 1e6f), k.Time);
                double u0 = Until(k, me);
                k.InjectEmgUnitEvent(EmgEventKind.Unlink, me, EmgCode.UnlinkLeave);
                CombatDroneFlash f = Flash(k)[0];
                StepN(k, 30);
                k.FireAt(me, Unit(k, CombatFaction.Hostile, new double2(0, 7), 1e6f), k.Time); // 母机再开火：接入时那份也不再续命
                StepN(k, 1);
                double u1 = Until(k, me);
                Expect(f.Held == 4 && f.Length == 4 && f.Program == p4 && Math.Abs(u1 - u0) < 1e-9,
                    $"程序里没有核心指令时信号离开：持有条数不变（{f.Held} / {f.Length}，执行 {f.Program}），但不再被母机开火续命（到期 {u0:0.##} → {u1:0.##}）");
            }
        }

        private static double Until(CombatKernel k, int owner)
        {
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone d) && d.Owner == owner)
                {
                    return d.Until;
                }
            }
            return double.NaN;
        }

        // ── E. 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · E. 存读档：截断 / 暂停 / 剥离中途存档 → 新内核读档 → 每架刷写读数、母机占用量与截断数一致，续跑 4 秒逐位相同；不含刷写字段的短段读到按全长；变体表随快照");
            using var k = new CombatKernel(Cfg(), 32);
            Bay bay = MakeBay(k, 3);
            Bay post = MakeBay(k, 1, anchored: true);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), bay.Self, 500f);
            int sentry = Unit(k, CombatFaction.Player, new double2(20, 0), post.Self, 500f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), 1e6f);
            int foe2 = Unit(k, CombatFaction.Hostile, new double2(20, 5), 1e6f);
            k.FireAt(me, foe, k.Time);
            k.FireAt(sentry, foe2, k.Time);
            StepN(k, 20);
            k.InjectEmgUnitEvent(EmgEventKind.Unlink, me, EmgCode.UnlinkSevered);
            k.SetPosition(sentry, new double2(20, -12));
            StepN(k, 30);
            string Sig(CombatKernel x) => string.Join(" ", Flash(x).Select(f => $"{f.Id}:{f.Held}/{f.Length}-{f.Pause}{(f.Stripped ? "s" : "")}")) + $" | {x.FlashUsed(me)}/{x.FlashUsed(sentry)}";
            string before = Sig(k);
            byte[] snap = k.Serialize();
            using var b = new CombatKernel(k.Config, 32);
            CombatLoadResult lr = b.Load(snap);
            string after = Sig(b);
            bool same = k.StateHash() == b.StateHash();
            for (int i = 0; i < 240; i++)
            {
                double t = k.Time + Dt;
                k.Step(Dt, t);
                b.Step(Dt, t);
            }
            bool cont = k.StateHash() == b.StateHash() && Sig(k) == Sig(b);
            Expect(lr == CombatLoadResult.Ok && before == after && same && cont && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion == 16
                   && b.FlashHeadOf(bay.Self).Equals(k.FlashHeadOf(bay.Self)),
                $"读档 {lr}：{before} → {after}（每架 Id:持有/全长-暂停，s = 已剥离 | 两台母机占用）；状态哈希一致 {same}，续跑 4 秒逐位相同 {cont}；格式仍 {CombatConst.FormatVersion}（阶段 1 内追加）");

            // 短段：模拟本卡之前的提交写出的无人机段（只有 Id）→ 按全长；武器段只有 2 个字段 → 没有变体表，按旧行为
            byte[] shortDrones = k.SerializeEmgShapeForTests(EmgConst.SegDrones, 1, 0);
            using var c = new CombatKernel(k.Config, 32);
            CombatLoadResult lr2 = c.Load(shortDrones);
            List<CombatDroneFlash> fc = Flash(c);
            byte[] shortWeapons = k.SerializeEmgShapeForTests(EmgConst.SegWeapons, 2, 0);
            using var d = new CombatKernel(k.Config, 32);
            CombatLoadResult lr3 = d.Load(shortWeapons);
            Expect(lr2 == CombatLoadResult.Ok && fc.Count == k.DroneCount && fc.All(f => f.Held == f.Length && !f.Stripped) && lr3 == CombatLoadResult.Ok && Flash(d).Count == 0,
                $"短段兼容：无人机段只有 Id 时读到 {fc.Count} 架、全部按全长（{Ints(fc.Select(f => f.Held))}）；武器段没有表头时读档 {lr3}、没有刷写读数（旧行为）");
        }

        // ── F. 热更层 ─────────────────────────────────────────────────────────────

        private static BlueprintCircuitPreview BayPreview(bool uplink)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, new[] { Split, Pierce });
            if (uplink && !board.TrySetUplink(2).Success)
            {
                Fail("夹具：无人机舱蓝图 2 号格标接入口失败");
            }
            return uplink ? UplinkCompiler.CompileUplinked(board, new[] { MarkTag }) : BlueprintCircuitCompiler.CompilePreview(board);
        }

        private static void CheckHotfix()
        {
            Line("  · F. 热更层（emg.stage = 1）：无人机舱 + 接入 [标记跳转（核心）] + 机器 [分裂, 穿透] 经 CombatSite.WithProgram 挂变体表——截断程序只留前 k 条、去核心列表无核心行、剥离换算、0 条读法 = 没装固件；预览预判；悬停读数");
            FgEmgProgramSelfCheck.UseStage1Catalog();
            try
            {
                BlueprintCircuitPreview pv = BayPreview(true);
                using var site = new CombatSite("emgflash-hot", CombatSite.ConfigFromTuning());
                CombatKernel k = site.Kernel;
                CombatWeapon w = site.WithProgram(pv, CombatSite.MachineWeaponFrom(pv), out EmgProgramResult res, out string note);
                int self = site.WeaponIndex(w);
                int4 h = k.FlashHeadOf(self);
                List<EmgProgramStep> full = EmgProgramCompiler.FlashSteps(res, false), soft = EmgProgramCompiler.FlashSteps(res, true);
                string order = string.Join(",", full.Select(s => s.OwnerId + (s.Core ? "*" : "")));
                Expect(h.x >= 0 && h.y == full.Count && h.z == soft.Count && full.Count == 3 && soft.Count == 2 && full[0].Core && full[0].FromUplink && w.Reading.Drones > 0,
                    $"变体表：全长 {h.y}（{order}，* = 核心）、去核心 {h.z}；无人机舱一次放 {w.Reading.Drones} 架（原因 {note ?? "无"}）");
                bool rowsOk = true;
                var desc = new List<string>();
                for (int kk = 0; kk <= full.Count; kk++)
                {
                    EmgProgramResult t = EmgProgramCompiler.Truncate(res, kk, false);
                    int cut = kk == 0 ? -1 : full[kk - 1].Ordinal;
                    int expectRows = res.KernelInstrs.Count(x => x.Ordinal <= cut);
                    k.TryGetWeapon(VariantWeapon(site, self, kk, false), out CombatWeapon v);
                    int rows = v.Program >= 0 && k.TryGetProgram(v.Program, out EmgProgram pg) ? pg.InstrTotal : 0;
                    rowsOk &= rows == expectRows && (kk == 0 ? v.Program == -1 && t == null : t != null && t.KernelInstrs.All(x => x.Ordinal <= cut));
                    desc.Add($"{kk} 条→{rows} 行");
                }
                bool softOk = true;
                for (int kk = 1; kk <= soft.Count; kk++)
                {
                    EmgProgramResult t = EmgProgramCompiler.Truncate(res, kk, true);
                    softOk &= t != null && t.KernelInstrs.All(x => (x.Flags & EmgInstrFlags.Core) == 0) && (t.Program.Flags & EmgProgramFlags.HasCore) == 0;
                }
                Expect(rowsOk && softOk && VariantWeapon(site, self, full.Count, false) == self && StripMap(site, self) == "0,0,1,2",
                    $"截断程序行数：{string.Join("、", desc)}（= 编译结果里序号 ≤ 第 k 条的行）；去核心变体没有核心行、不标含核心；全长那行就是武器自己；剥离换算 {StripMap(site, self)}");

                // 0 条 = 只有基础攻击：读法与没装固件的同一台无人机舱逐字段相同、没有程序
                BlueprintCircuitPreview bare = BlueprintCircuitCompiler.CompilePreview(BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, Array.Empty<string>()));
                k.TryGetWeapon(VariantWeapon(site, self, 0, false), out CombatWeapon v0);
                CombatReading bareR = CombatSite.MachineWeaponFrom(bare).Reading;
                Expect(v0.Program == -1 && v0.Reading.Equals(bareR) && v0.SpawnFlight == 0,
                    $"截到 0 条：程序 {v0.Program}、读法与没装固件的无人机舱相同（{v0.Reading.Equals(bareR)}）、不实体化");

                // 预览预判：2 架 × 3 条按带宽 5 → 第二架只刷 2 条，最后 1 条记“刷写截断”；默认带宽 8 不截
                EmgProgramResult fc = EmgProgramCompiler.Compile(pv);
                EmgProgramCompiler.AddFlashForecast(fc, 2, 5);
                EmgProgramResult fd = CombatSite.WithFlashForecast(pv, EmgProgramCompiler.Compile(pv));
                var rowsF = new List<EmgTreeRow>();
                EmgProgramTreeText.BuildRows(fc, rowsF);
                string reason = EmgProgramTreeText.ReasonText(EmgProgramTreeText.NoteFlashTruncated, EmgProgramTreeText.StepName(full[2]));
                Expect(fc.Notes.Count(n => n.Key == EmgProgramTreeText.NoteFlashTruncated) == 1 && fc.Notes.Any(n => n.Key == EmgProgramTreeText.NoteFlashTruncated && n.Ordinal == full[2].Ordinal)
                       && rowsF.Any(r => r.Text.Contains(GameText.Format(EmgProgramTreeText.NoteFlashTruncated, EmgProgramTreeText.StepName(full[2]))))
                       && fd.Notes.All(n => n.Key != EmgProgramTreeText.NoteFlashTruncated),
                    $"程序树预览预判：2 架 × 3 条、带宽 5 → 截掉“{EmgProgramTreeText.StepName(full[2])}”，预览行有“{reason}”；默认带宽 8 不报");

                // 悬停：真实拾取 → “刷写 3 / 3 条”；带宽 5 下第二架“刷写 2 / 3 条（带宽已满）”+ 截掉的那条原因
                using var site2 = new CombatSite("emgflash-hover", With(CombatSite.ConfigFromTuning(), 5));
                CombatKernel k2 = site2.Kernel;
                CombatWeapon w2 = site2.WithProgram(pv, CombatSite.MachineWeaponFrom(pv), out _, out _);
                int me = Unit(k2, CombatFaction.Player, new double2(0, 0), site2.WeaponIndex(w2), 500f);
                int foe = Unit(k2, CombatFaction.Hostile, new double2(30, 30), 1e6f);
                k2.FireAt(me, foe, k2.Time);
                k2.TryGetDrone(0, out CombatDrone d0);
                k2.TryGetDrone(1, out CombatDrone d1);
                int h0 = StatusTagHover.HoverDroneGroundForTests(site2, d0.Pos);
                string body0 = StatusTagHover.DroneContent(site2, h0)?.Body ?? string.Empty;
                int h1 = StatusTagHover.HoverDroneGroundForTests(site2, d1.Pos);
                TooltipContent c1 = StatusTagHover.DroneContent(site2, h1);
                string body1 = c1?.Body ?? string.Empty;
                int none = StatusTagHover.HoverDroneGroundForTests(site2, new double2(-50, -50));
                Expect(h0 == d0.Id && body0.StartsWith(GameText.Format("emg.flash.hover.held", "3", "3")) && !body0.Contains(GameText.Get("emg.flash.hover.full"))
                       && h1 == d1.Id && body1.StartsWith(GameText.Format("emg.flash.hover.held", "2", "3") + GameText.Get("emg.flash.hover.full")) && body1.Contains(reason)
                       && c1.Title == GameText.Get("emg.flash.hover.title") && none == 0 && StatusTagHover.HoveredDrone == 0,
                    $"悬停读数（光标拾取）：第一架“{body0.Replace('\n', '／')}”；第二架“{body1.Replace('\n', '／')}”；空地不弹");
            }
            finally
            {
                StatusTagHover.Leave(null);
                EmgCatalog.ResetForTests();
            }
            // emg.stage = 0（正式流程缺省）：没有程序就没有变体表，无人机舱武器逐字同旧
            BlueprintCircuitPreview pv0 = BayPreview(false);
            using (var site = new CombatSite("emgflash-stage0", CombatSite.ConfigFromTuning()))
            {
                CombatWeapon w0 = site.WithProgram(pv0, CombatSite.MachineWeaponFrom(pv0), out EmgProgramResult r0, out _);
                Expect(r0.IsEmpty && site.Kernel.FlashHeadOf(site.WeaponIndex(w0)).x < 0 && CombatKernel.WeaponKey(w0) == CombatKernel.WeaponKey(CombatSite.MachineWeaponFrom(pv0)),
                    "负向：emg.stage = 0 时程序为空、不挂变体表，武器键与旧路径逐字相同");
            }
        }

        private static CombatConfig With(CombatConfig c, int bandwidth)
        {
            c.EmgFlashBandwidth = bandwidth;
            return c;
        }

        private static int VariantWeapon(CombatSite site, int self, int k, bool noCore) => site.Kernel.FlashVariantOf(self, k, noCore);

        private static string StripMap(CombatSite site, int self)
        {
            int4 h = site.Kernel.FlashHeadOf(self);
            return Ints(Enumerable.Range(0, h.y + 1).Select(k => site.Kernel.FlashStripOf(self, k)));
        }

        // ── L. 耗时 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · L. 耗时（FGR-EMG-200；LANDING-PLAN 第 12 节 #2 推荐默认：3x 倍速也守 120 帧，每步 ≤ 1.5 毫秒）：32 台母机 × 8 架（256 架带变体表，含哨戒桩），跨刷写节拍的内核一步");
            using var k = new CombatKernel(Cfg(), 128);
            Bay bay = MakeBay(k, 8);
            Bay post = MakeBay(k, 8, anchored: true);
            var pairs = new List<(int, int)>();
            for (int i = 0; i < 32; i++)
            {
                double x = (i % 8) * 25.0, y = (i / 8) * 25.0;
                pairs.Add((Unit(k, CombatFaction.Player, new double2(x, y), i % 2 == 0 ? bay.Self : post.Self, 1e6f), Unit(k, CombatFaction.Hostile, new double2(x, y + 5), 1e6f)));
            }
            foreach ((int me, int foe) in pairs)
            {
                k.FireAt(me, foe, k.Time);
            }
            StepN(k, 30);
            var ms = new List<double>();
            for (int s = 0; s < 32; s++)
            {
                StepN(k, 1);
                ms.Add(k.LastStepMs);
            }
            double worst = ms.Max();
            PerfGate.Expect(k.DroneCount == 256 && Flash(k).Count == 256,
                $"{k.DroneCount} 架带变体表的无人机 / 哨戒桩，32 步（跨 2 个刷写节拍）内核一步最慢 {worst:0.###} 毫秒、平均 {ms.Average():0.###} 毫秒（3x 每步上限 1.5 毫秒）",
                new[] { PerfGate.Le(worst, 1.5, "256 架刷写无人机的内核一步（毫秒）") }, Expect, Line);
        }

        // ── W. 真实家园地点 ─────────────────────────────────────────────────────────────

        private static void CheckWorld()
        {
            Line("  · W. 真实家园地点（emg.stage = 1）：无人机舱机器 [分裂, 穿透] 经装配编译挂变体表；换成接入 [标记跳转] 的那份武器放出无人机；1x / 0.5x / 3x 同样游戏步数哈希一致、暂停不推进；"
                 + "信号广播（CombatSites.OnUplinkChanged）主动离开 / 断链 / 静默夜各一遍 → 注入 evt.unlink（原因码）→ 剥离核心；悬停读数");
            FgEmgProgramSelfCheck.UseStage1Catalog();
            try
            {
                var hashes = new List<ulong>();
                var lines = new List<string>();
                bool ok = true;
                var cases = new[]
                {
                    (1f, RegionControlChangeReason.PlayerRequest, EmgCode.UnlinkLeave),
                    (0.5f, RegionControlChangeReason.SignalLost, EmgCode.UnlinkSevered),
                    (3f, RegionControlChangeReason.SilentNight, EmgCode.UnlinkSilentNight),
                };
                foreach ((float speed, RegionControlChangeReason reason, ushort code) in cases)
                {
                    if (!BuildWorld(out CombatSite site, out int logicId, out int unit, out int localWeapon))
                    {
                        Fail("夹具：家园地点或无人机舱机器没就位");
                        return;
                    }
                    CombatKernel k = site.Kernel;
                    int4 localHead = k.FlashHeadOf(localWeapon);
                    BlueprintCircuitPreview pvUp = BayPreview(true);
                    int upWeapon = site.WeaponIndex(site.WithProgram(pvUp, CombatSite.MachineWeaponFrom(pvUp)));
                    k.SetUnitWeapon(unit, upWeapon);
                    k.TryGetUnit(unit, out CombatUnitView v);
                    int foe = Unit(k, CombatFaction.Hostile, v.Position + new double2(0, 5), 1e6f);
                    k.FireAt(unit, foe, k.Time);
                    GameClock.SetSpeed(speed);
                    long target = GameClock.Ticks + 90;
                    int guard = 0;
                    while (GameClock.Ticks < target && guard++ < 2000)
                    {
                        WorldSimulation.Frame(1f / 60f, target);
                    }
                    GameClock.SetSpeed(1f);
                    hashes.Add(k.StateHash());
                    ulong before = k.StateHash();
                    GameClock.SetPaused(true);
                    for (int i = 0; i < 60; i++)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    ulong paused = k.StateHash();
                    GameClock.SetPaused(false);
                    List<CombatDroneFlash> f0 = Flash(k);
                    k.TryGetDrone(0, out CombatDrone d0);
                    int hovered = StatusTagHover.HoverDroneGroundForTests(site, d0.Pos);
                    string body = StatusTagHover.DroneContent(site, hovered)?.Body ?? string.Empty;
                    long inj0 = CombatSites.EmgUplinkInjected;
                    CombatSites.OnUplinkChanged(new SignalUplinkChange { PreviousLogicId = logicId, CurrentLogicId = 0, SiteId = site.SiteId, Reason = reason });
                    List<CombatDroneFlash> f1 = Flash(k);
                    bool caseOk = localHead.x >= 0 && localHead.y == 2 && f0.Count >= 1 && f0.All(x => x.Held == 3 && !x.Stripped) && before == paused
                                  && CombatSites.UnlinkCode(reason, 0) == code && f1.Count == f0.Count && f1.All(x => x.Stripped && x.Held == 2 && x.Length == 2)
                                  && hovered == d0.Id && body.StartsWith(GameText.Format("emg.flash.hover.held", "3", "3"));
                    ok &= caseOk;
                    lines.Add($"{speed}x/{reason}→码 {CombatSites.UnlinkCode(reason, 0)}：放出 {f0.Count} 架持有 {Ints(f0.Select(x => x.Held))}，广播后 {Ints(f1.Select(x => x.Held))}（剥离 {f1.Count(x => x.Stripped)}），"
                              + $"注入计数 +{CombatSites.EmgUplinkInjected - inj0}，暂停{(before == paused ? "不变" : "变了")}，悬停“{body.Split('\n')[0]}”{(caseOk ? string.Empty : " ✗")}");
                    StatusTagHover.Leave(null);
                }
                Expect(ok && hashes.Distinct().Count() == 1,
                    $"本地装配变体表全长 2；{string.Join("；", lines)}；1x / 0.5x / 3x 同样 90 步哈希 {hashes.Distinct().Count()} 种");
            }
            finally
            {
                StatusTagHover.Leave(null);
                EmgCatalog.ResetForTests();
            }
        }

        // ── T. 炮塔接入（审查 P1）：炮塔没有 evt.unlink，信号离开走 TurretUplink.Leave / 换接 ─────────────────────────────

        private static void CheckTurret()
        {
            Line("  · T. 无人机舱炮塔（固定底盘 + 接入口）：信号接入、亲自开火放出持有核心的无人机 → 主动离开 / 静默夜断链 / 禁用（失去作用）/ 换接到另一座炮塔 各一遍 → 剥离核心、不再续命");
            FgEmgProgramSelfCheck.UseStage1Catalog();
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [MarkTag] = FirmwareKind.Core });
            const string tbp = "bp_emg_flash_tbay";
            try
            {
                foreach (string how in new[] { "主动离开", "静默夜", "禁用", "换接" })
                {
                    CampaignState s = FgTurretSelfCheck.NewWorld(6190);
                    CombatSite site = FgTurretSelfCheck.Site;
                    s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Append(ComponentCatalog.CompDroneBayId).Distinct().ToArray();
                    SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, MarkTag);
                    Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
                    SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
                    SignalCoreResult eq = SignalCoreService.TryEquip(s, print.CreatedId, 0);
                    SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
                    BlueprintCircuitBoard board = FgTurretSelfCheck.Fixed(ComponentCatalog.CompDroneBayId, Split, Pierce);
                    bool port = board.TrySetUplink(2).Success;
                    FgTurretSelfCheck.AddBlueprint(s, tbp, board, "接入无人机舱炮塔");
                    BuildingRecord t1 = FgTurretSelfCheck.PlaceTurret(s, TurretCatalog.HeavyTypeId, tbp);
                    BuildingRecord t2 = how == "换接" ? FgTurretSelfCheck.PlaceTurret(s, TurretCatalog.HeavyTypeId, tbp, from: 9f) : null;
                    TurretRecord r1 = t1 != null ? FgTurretSelfCheck.Rec(s, t1) : null;
                    int unit = 0;
                    if (site == null || !print.Success || !eq.Success || !port || r1 == null || (how == "换接" && t2 == null) || !site.TryGetTurretUnit(r1.Serial, out unit))
                    {
                        Fail($"夹具（{how}）：家园 / 标记跳转装入信号核（{print.Message} / {eq.Message}）/ 接入口 {port} / 炮塔没就位");
                        return;
                    }
                    TurretOpResult up = TurretUplink.Request(s, t1.BuildingId);
                    CombatKernel k = site.Kernel;
                    k.TryGetUnit(unit, out CombatUnitView uv);
                    int foe = Unit(k, CombatFaction.Hostile, uv.Position + new double2(0, 5), 1e6f);
                    CombatFireResult fired = site.TurretFireAt(r1.Serial, foe);
                    WorldSimulation.StepMany(3);
                    List<CombatDroneFlash> f0 = Flash(k).Where(x => x.Owner == unit).ToList();
                    Dictionary<int, double> until0 = DroneUntil(k, unit);
                    switch (how)
                    {
                        case "主动离开":
                            TurretUplink.Leave(s);
                            break;
                        case "静默夜":
                            SignalUplinkService.SilentNightProvider = () => true;
                            WorldSimulation.StepMany(2);
                            SignalUplinkService.SilentNightProvider = null;
                            break;
                        case "禁用":
                            BuildingOps.TrySetEnabled(s, t1.BuildingId, false, out _);
                            WorldSimulation.StepMany(2);
                            break;
                        default:
                            TurretUplink.Request(s, t2.BuildingId);
                            break;
                    }
                    List<CombatDroneFlash> f1 = Flash(k).Where(x => x.Owner == unit).ToList();
                    WorldSimulation.StepMany(GameClock.StepHz * 2);
                    Dictionary<int, double> until1 = DroneUntil(k, unit);
                    bool renewed = until0.Any(kv => until1.TryGetValue(kv.Key, out double u) && Math.Abs(u - kv.Value) > 1e-9);
                    Expect(up.Ok && f0.Count >= 1 && f0.All(x => x.Held == 3 && !x.Stripped) && !TurretUplink.IsUplinkedTo(s, t1.BuildingId)
                           && f1.Count == f0.Count && f1.All(x => x.Stripped && x.Held == 2 && x.Length == 2) && !renewed,
                        $"{how}：接入（{up.Ok}）亲自开火 {fired} 放出 {f0.Count} 架持有 {Ints(f0.Select(x => x.Held))}；信号离开后 {Ints(f1.Select(x => x.Held))}（剥离 {f1.Count(x => x.Stripped)}）；"
                        + $"之后 2 秒到期时刻{(renewed ? "被续命" : "不变")}");
                }
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
                EmgCatalog.ResetForTests();
            }
        }

        // ── R. 靶场投影（复审 P1）：投影的接入 / 离开走 TestRangeService.SetUplinked，不经 evt.unlink（IC-REQ-010 试射与实战同一份程序）─────────────

        private static void CheckTestRange()
        {
            Line("  · R. 靶场投影无人机舱蓝图：信号接入、自动打靶放出持有核心的无人机 → 主动离开 / 换接到另一个投影 / 信号去了机器各一遍 → 剥离核心、不再续命；静默夜投影消失 → 母机不在，无人机随之消失（不留执行核心的无人机）");
            FgEmgProgramSelfCheck.UseStage1Catalog();
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [MarkTag] = FirmwareKind.Core });
            const string rbp = "bp_emg_flash_rbay";
            try
            {
                foreach (string how in new[] { "主动离开", "换接", "信号去了机器", "静默夜" })
                {
                    CampaignState s = FgTestRangeSelfCheck.NewWorld(6290, out BuildingRecord range);
                    if (range == null)
                    {
                        return;
                    }
                    s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Append(ComponentCatalog.CompDroneBayId).Distinct().ToArray();
                    SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, MarkTag);
                    Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
                    SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
                    SignalCoreResult eq = SignalCoreService.TryEquip(s, print.CreatedId, 0);
                    SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
                    BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, new[] { Split, Pierce });
                    bool port = board.TrySetUplink(2).Success;
                    FgTestRangeSelfCheck.AddBlueprint(s, rbp, board);
                    FgTestRangeSelfCheck.SetLayout(s, range.BuildingId, "range.basic", "range.basic", "range.basic", "range.basic");
                    RangeOpResult p1 = TestRangeService.ProjectBlueprint(s, range.BuildingId, rbp);
                    RangeOpResult p2 = TestRangeService.ProjectBlueprint(s, range.BuildingId, rbp); // 第二个投影：换接目标；静默夜时会话不因投影清空而结束
                    CombatSite site = TestRangeService.SiteOf(range.BuildingId);
                    if (!print.Success || !eq.Success || !port || !p1.Success || !p2.Success || site == null)
                    {
                        Fail($"夹具（{how}）：标记跳转装入信号核（{print.Message} / {eq.Message}）/ 接入口 {port} / 投影（{p1.Text} / {p2.Text}）");
                        return;
                    }
                    CombatKernel k = site.Kernel;
                    int unit = TestRangeService.ProjectionsOf(range.BuildingId).First(x => x.Serial == p1.Serial).UnitId;
                    RangeOpResult up = TestRangeService.Uplink(s, range.BuildingId, p1.Serial);
                    bool launched = FgProductionSelfCheck.StepUntil(() => Flash(k).Any(x => x.Owner == unit && x.Held == 3), 20);
                    List<CombatDroneFlash> f0 = Flash(k).Where(x => x.Owner == unit).ToList();
                    Dictionary<int, double> until0 = DroneUntil(k, unit);
                    switch (how)
                    {
                        case "主动离开":
                            TestRangeService.LeaveUplink(s);
                            break;
                        case "换接":
                            TestRangeService.Uplink(s, range.BuildingId, p2.Serial);
                            break;
                        case "信号去了机器":
                            s.SignalCore.UplinkMachineLogicId = 999;
                            WorldSimulation.StepMany(2);
                            s.SignalCore.UplinkMachineLogicId = 0;
                            break;
                        default:
                            SignalUplinkService.SilentNightProvider = () => true;
                            WorldSimulation.StepMany(2);
                            SignalUplinkService.SilentNightProvider = null;
                            break;
                    }
                    var ids = new HashSet<int>(f0.Select(x => x.Id));
                    List<CombatDroneFlash> f1 = Flash(k).Where(x => ids.Contains(x.Id)).ToList();
                    bool left = !TestRangeService.ProjectionsOf(range.BuildingId).Any(x => x.Serial == p1.Serial && x.Uplinked);
                    WorldSimulation.StepMany(GameClock.StepHz * 2);
                    Dictionary<int, double> until1 = DroneUntil(k, unit);
                    bool renewed = until0.Any(kv => until1.TryGetValue(kv.Key, out double u) && Math.Abs(u - kv.Value) > 1e-9);
                    bool after = how == "静默夜"
                        ? f1.Count == 0 && !TestRangeService.ProjectionsOf(range.BuildingId).Any(x => x.Serial == p1.Serial)
                        : f1.Count == f0.Count && f1.All(x => x.Stripped && x.Held == 2 && x.Length == 2) && !renewed;
                    Expect(up.Success && launched && f0.Count >= 1 && f0.All(x => x.Held == 3 && !x.Stripped) && left && after,
                        $"{how}：接入投影（{up.Text}）自动打靶放出 {f0.Count} 架持有 {Ints(f0.Select(x => x.Held))}；信号离开后 {Ints(f1.Select(x => x.Held))}（剥离 {f1.Count(x => x.Stripped)}）；"
                        + $"之后 2 秒到期时刻{(renewed ? "被续命" : "不变")}");
                    TestRangeService.EndTest(s, range.BuildingId);
                }
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                SignalUplinkService.SilentNightProvider = null;
                TestRangeService.ResetForTests();
                EmgCatalog.ResetForTests();
            }
        }

        private static Dictionary<int, double> DroneUntil(CombatKernel k, int owner)
        {
            var map = new Dictionary<int, double>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone d) && d.Owner == owner)
                {
                    map[d.Id] = d.Until;
                }
            }
            return map;
        }

        /// <summary>同一种子的新世界：家园 + 一台装 [分裂, 穿透] 的无人机舱机器（装配登记走正式编译）。返回机器的内核单位与它此刻的武器下标。</summary>
        private static bool BuildWorld(out CombatSite site, out int logicId, out int unit, out int weapon)
        {
            site = null;
            unit = 0;
            weapon = -1;
            logicId = 0;
            SignalUplinkService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            CampaignState s = FgProductionSelfCheck.NewWorld(9714, true, 2000);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompDroneBayId, null, null, new[] { Split, Pierce });
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != Bp)
                .Append(new BlueprintRecord { BlueprintId = Bp, DisplayName = Bp, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, Bp, HomeValleyLayout.RegionId, new Vector2(4f, 4f), 200f, 200f);
            MachineLoadoutRegistry.Register(s, m.LogicId, Bp, 1);
            WorldSimulation.StepMany(2);
            site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (site == null || !m.Success || !site.TryGetMachineUnit(m.LogicId, out unit) || !site.Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            logicId = m.LogicId;
            weapon = v.Weapon;
            return true;
        }

        // ── 断言 ─────────────────────────────────────────────────────────────

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
