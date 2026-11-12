using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
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
    /// EMG4-HIJ-01 敌方同程序与信号劫持的自动验收（FG19 FGR-EMG-160～163、211；FGT-EMG-061 劫持部分、FGT-EMG-002 阶段 4 敌方行准入、FGT-EMG-040；ADR-EMG-001 ⑦ ⑩ ⑰）。
    /// 起真实内核（CombatKernel，Burst 步进）、真实表与真实 CombatSite：
    /// A 表与阶段（emg.stage 缺省 4、快照格式 20、hij.hack 端口码、emg.hijack.* 调参进内核配置、引导钩子）；
    /// B 敌方同程序（全部 assembled 敌人行逐行编出程序、LegacyOwners 为空、地点登记程序下标；emg.stage = 0 时为空程序；开 / 关在内核里结算结果不同；敌方声明行过准入；悬停 / 图鉴显示敌方程序）；
    /// C 劫持（测试固件行：hij.hack 注入；阈值以下照常阵亡；满阈值阵亡 → 不发 Killed、不留残骸、30% 耐久己方、行为 Hijacked）；D 劫持体 AI（攻击最近敌人、无目标跟随劫持者、不接受命令）；
    /// E 结束（期满自毁 / 劫持者阵亡连带自毁 / 被敌方击毁：HijackEnded 后 Killed 只一次、击杀者 = 劫持者、留残骸、击杀计数只加一次）；F 负向（上限满提示、精英 / 首领抗性拒绝写原因、阈值以下）；
    /// G 攻城队伍对账（队伍计数减一、期满时 SiegeKilled 带原队伍一次）；H 刷写带宽（占劫持者带宽、不够时截断）；I 存读档（劫持中途读档续跑哈希一致、剩余时间一致、读 19 版快照无劫持）；
    /// J 暂停与 0.5x～3x（按游戏秒到期）；K 热更层（Hijacked / Refused 事件 → 计数、引导钩子、悬停“由教义触发”）；L 入侵度来源接上（DEBT-EMG2PHS01-05）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgHijackSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgHijackSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgHijackSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/信号劫持与敌方同程序")]
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
            Line("\n[信号劫持与敌方同程序] EMG4-HIJ-01：敌方组装武器同一编译器、hij.hack 入侵度、阵亡拦截重启为己方、期满自毁、上限 / 抗性、带宽、存读档（FGT-EMG-061 / 002 / 040）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；内核单线程 IJob + Burst（Editor 同步编译）");
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
                Step(CheckTables);
                Step(CheckEnemyPrograms);
                Step(CheckHijackCore);
                Step(CheckDoctrine);
                Step(CheckEnds);
                Step(CheckNegatives);
                Step(CheckSiege);
                Step(CheckBandwidth);
                Step(CheckSaveLoad);
                Step(CheckSpeed);
                Step(CheckHotLayer);
                Step(CheckRelayExcluded);
                Step(CheckBurningOnOil);
            }
            catch (Exception e)
            {
                Fail($"劫持自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [信号劫持与敌方同程序] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(CombatConfig? cfg = null, int cap = 64)
        {
            CombatConfig c = cfg ?? CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatUnitFlags extra = 0, CombatBehavior behavior = CombatBehavior.None)
        {
            return k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : behavior,
                Flags = Live | CombatUnitFlags.Report | extra, Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            });
        }

        private static CombatWeapon Instant(float damage, float range = 12f, int program = -1, float cooldown = 0.5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = cooldown, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        /// <summary>测试固件行（自检注入，不进 Luban 表；正式固件 N39 重启注入由 EMG4-FW-01 落表）：命中 → hij.hack inject amount。</summary>
        private static int HackProgram(CombatKernel k, float amount)
        {
            var instrs = new List<EmgInstr> { new EmgInstr { Port = EmgConst.PortHack, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = amount } };
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
            return k.AddProgram(new EmgProgram(), nodes, instrs);
        }

        /// <summary>己方黑客机（武器 = 测试固件行 + 伤害）、一台敌机。</summary>
        private static (int me, int foe) Duel(CombatKernel k, float foeHp, float damage, float hack, CombatUnitFlags foeFlags = 0, float range = 12f)
        {
            int w = k.AddWeapon(Instant(damage, range, HackProgram(k, hack)));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 2000f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), k.AddWeapon(Instant(5f, 6f)), foeHp, foeFlags, CombatBehavior.HoldFire);
            return (me, foe);
        }

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            var list = new List<CombatEvent>();
            for (int guard = 0; guard < 100; guard++)
            {
                CombatEvent[] buf = k.DrainGameplay(4096, out int n);
                if (n == 0)
                {
                    break;
                }
                for (int i = 0; i < n; i++)
                {
                    list.Add(buf[i]);
                }
            }
            return list;
        }

        /// <summary>一步一步打，直到条件成立或超过秒数；每步取走玩法事件累到 <paramref name="events"/>。</summary>
        private static bool RunUntil(CombatKernel k, Func<bool> done, float seconds, List<CombatEvent> events, float dt = Dt, Action before = null)
        {
            int n = Mathf.CeilToInt(seconds / Math.Max(dt, 1e-4f));
            for (int s = 0; s < n; s++)
            {
                before?.Invoke(); // 己方机器（Commanded）只按显式出手开火：自检每步替它下一次 FireAt（冷却由内核管）
                k.Step(dt, k.Time + dt);
                events?.AddRange(Drain(k));
                if (done())
                {
                    return true;
                }
            }
            return false;
        }

        private static CombatUnitView View(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v : default;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static int Count(List<CombatEvent> ev, CombatEventKind kind, int unit) => ev.Count(e => e.Kind == kind && e.Unit == unit);

        /// <summary>把 foe 打成被劫持（hack 0.6 一发、两发满阈值，第二发打死）。返回事件。</summary>
        private static List<CombatEvent> HijackIt(CombatKernel k, int me, int foe)
        {
            var ev = new List<CombatEvent>();
            RunUntil(k, () => View(k, foe).Behavior == CombatBehavior.Hijacked || !Alive(k, foe), 6f, ev, Dt, () => k.FireAt(me, foe, k.Time));
            return ev;
        }

        // ── A 表与阶段 ─────────────────────────────────────────────────────

        private static void CheckTables()
        {
            EmgCompiled c = EmgCatalog.Current;
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            bool port = c.PortIdByCode.TryGetValue(EmgConst.PortHack, out string pid) && pid == "hij.hack";
            Expect(c.EffectiveStage == 4 && EmgCatalog.ImplementedStage == 4 && CombatConst.FormatVersion == 20 && port,
                $"emg.stage 缺省 4（生效 {c.EffectiveStage}、已实现 {EmgCatalog.ImplementedStage}）；快照格式 {CombatConst.FormatVersion}；内核端口码 {EmgConst.PortHack} = 表 {pid}");
            Expect(Mathf.Approximately(cfg.EmgHijackSeconds, 30f) && Mathf.Approximately(cfg.EmgHijackHpRatio, 0.3f) && cfg.EmgHijackMax == 2 && Mathf.Approximately(cfg.EmgHijackEliteResist, 1f),
                $"fg.TbHomeTuning emg.hijack.*进内核配置：时长 {cfg.EmgHijackSeconds}s、耐久 {cfg.EmgHijackHpRatio}、上限 {cfg.EmgHijackMax}、精英 / 首领入侵抗性 {cfg.EmgHijackEliteResist}");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstHijack) && GuidanceHooks.EmgFirstHijack == "emg_first_hijack",
                "引导钩子 emg_first_hijack 已登记（引导内容归 FG15-UX-04）");
            string[] keys = { "emg.hijack.notify.started", "emg.hijack.notify.full", "emg.hijack.notify.resist", "emg.hijack.hover", "emg.hijack.hover.doctrine", "emg.enemy.program.title" };
            Expect(keys.All(GameText.Has), "劫持通知 / 悬停 / 敌方程序文本键齐全");
            // CodexTaxonomy 是热更层 internal：按反射取 StatusDescription（与图鉴同一份文字）
            Type tax = typeof(GameLogic.Progression.MechanicCodex).Assembly.GetType("GameLogic.Progression.CodexTaxonomy");
            string codex = tax?.GetMethod("StatusDescription", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(null, new object[] { BinGames.Sim.SimStatus.Infected }) as string;
            Expect(codex != null && codex.Contains("信号劫持") && codex.Contains("重启") && codex.Contains("友方"),
                $"EMG1-TXT-01 图鉴“信号劫持”说明与机制一致（入侵度满时被击毁 → 部分耐久重启为友方短暂作战；内部状态位 Infected 不改名、不复用）：{codex}");
        }

        // ── B 敌方同程序（DEBT-EMG3FW01-01；FGT-EMG-002 阶段 4）─────────────

        private static void CheckEnemyPrograms()
        {
            var rows = EnemyDefs.Rows.Where(r => EnemyDefs.HasTag(r, EnemyDefs.TagAssembled)).ToList();
            var bad = new List<string>();
            var legacy = new List<string>();
            var noFw = new List<string>();
            int compiled = 0;
            using (var site = new CombatSite("emghij-enemy", CombatSite.ConfigFromTuning()))
            {
                foreach (MechEnemy row in rows)
                {
                    if (!EnemySpawnBuilder.TryAssembly(row, out string comp, out List<string> fw))
                    {
                        bad.Add(row.Id + "（没有作战组件）");
                        continue;
                    }
                    EmgProgramResult pr = EmgProgramCompiler.Compile(CombatSite.EnemyPreview(comp, fw));
                    CombatWeapon w = site.EnemyWeapon(null, row, out EmgProgramResult sp);
                    if (fw.Count == 0)
                    {
                        noFw.Add(row.Id); // 只有作战组件、没有固件（如维修束装填机）：没有可编的指令，武器就是基础读法
                        if (!pr.IsEmpty || w.Program >= 0)
                        {
                            bad.Add(row.Id + "（无固件却有程序）");
                        }
                        continue;
                    }
                    if (pr.IsEmpty || w.Program < 0 || sp == null || sp.IsEmpty || !site.Kernel.TryGetProgram(w.Program, out _))
                    {
                        bad.Add($"{row.Id}（{string.Join("+", fw)}）");
                    }
                    else
                    {
                        compiled++;
                    }
                    if (pr.LegacyOwners.Count > 0)
                    {
                        legacy.Add($"{row.Id}：{string.Join("、", pr.LegacyOwners)}");
                    }
                }
            }
            Expect(rows.Count > 0 && bad.Count == 0 && legacy.Count == 0 && compiled + noFw.Count == rows.Count && compiled > 0,
                $"全部 {rows.Count} 个 assembled 敌人行逐行：装了固件的 {compiled} 个与玩家同一编译器编出程序并登记进地点程序表、LegacyOwners 为空（旧 readFields 不再结算）；没有固件的 {noFw.Count} 个（{string.Join("、", noFw)}）只有基础读法"
                + (bad.Count > 0 ? "；未编出：" + string.Join("、", bad) : string.Empty) + (legacy.Count > 0 ? "；回落旧轨：" + string.Join("；", legacy) : string.Empty));

            // EMG4-CLEAN-01：原“emg.stage = 0 回滚对照（编不出程序）”改负向：填 0 不再生效
            EmgSource zero = EmgCatalog.FromTables();
            Func<string, (bool, float)> baseTuning = zero.Tuning;
            zero.Tuning = id => id == EmgCatalog.StageKey ? (true, 0f) : baseTuning(id);
            EmgCatalog.OverrideForTests(zero);
            bool offEmpty;
            try
            {
                offEmpty = EmgCatalog.EffectiveStage == 4 && rows.All(r => !EnemySpawnBuilder.TryAssembly(r, out string comp, out List<string> fw) || fw.Count == 0 || !EmgProgramCompiler.Compile(CombatSite.EnemyPreview(comp, fw)).IsEmpty);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Expect(offEmpty, "负向（EMG4-CLEAN-01）：emg.stage 填 0 不再生效——装了固件的 assembled 敌人照常编出程序（旧读法回滚分支已删）");

            // 开 / 关在内核里结算的结果不同：同一敌人行，程序轨武器 vs 旧读法武器，打同一台机器 6 秒，比内核可观察结果
            int differ = 0;
            var same = new List<string>();
            foreach (MechEnemy row in rows.Where(r => r.WeaponRange > 0f))
            {
                string a = Outcome(row, true), b = Outcome(row, false);
                if (a != b)
                {
                    differ++;
                }
                else
                {
                    same.Add(row.Id);
                }
            }
            Expect(differ > 0, $"开 / 关行为对照：{differ} 个组装敌人的程序轨武器与旧读法武器打同一目标结果不同（内核真实结算）"
                + (same.Count > 0 ? $"；结果相同的 {same.Count} 个（所装固件在该场景无可观察差异，例如需要残骸 / 区域到期才触发）：{string.Join("、", same)}" : string.Empty));

            // 敌方声明行（ownerKind = enemy）过准入：编译目录里每个敌方 owner 至少读 1 写 1、表零问题
            EmgSource src = EmgCatalog.FromTables();
            var enemyOwners = src.Decls.Where(d => d.OwnerKind == "enemy").GroupBy(d => d.Owner).ToList();
            var refused = new List<string>();
            foreach (var g in enemyOwners)
            {
                if (!EmgCatalog.Admit(EmgCatalog.DeclsOf(src, g.Key), src.Ports.ToDictionary(p => p.Id), out string why))
                {
                    refused.Add($"{g.Key}：{why}");
                }
            }
            Expect(enemyOwners.Count > 0 && refused.Count == 0 && EmgCatalog.Current.Problems.Count == 0,
                $"敌方单位声明行 {enemyOwners.Count} 个 owner 全部过准入（FGR-EMG-014 末条）{(refused.Count > 0 ? "；被拒：" + string.Join("；", refused) : string.Empty)}");

            // 悬停 / 图鉴显示敌方程序（读写端口）
            MechEnemy shown = rows.FirstOrDefault(r => EnemySpawnBuilder.TryAssembly(r, out _, out List<string> f) && f.Count > 0);
            string text = CombatSite.EnemyProgramText(shown);
            string firstFw = shown != null && EnemySpawnBuilder.TryAssembly(shown, out _, out List<string> ff) ? ff[0] : null;
            Expect(text != null && text.StartsWith(GameText.Get("emg.enemy.program.title"), StringComparison.Ordinal) && firstFw != null
                   && text.Contains(EmgProgramTreeText.FirmwareName(firstFw)) && text.Contains(EmgProgramTreeText.PortsLine(firstFw)),
                $"击杀悬停 / 图鉴的敌方程序（{shown?.Id}）：{text?.Replace('\n', '｜')}");
            MechEnemy plain = EnemyDefs.Rows.FirstOrDefault(r => !EnemyDefs.HasTag(r, EnemyDefs.TagAssembled) && EmgProgramTreeText.PortsLine(r.Id) == null);
            Expect(plain == null || CombatSite.EnemyProgramText(plain) == null, $"没有固件、没有声明的敌人（{plain?.Id}）不显示敌方程序行（不编造）");
        }

        /// <summary>一个敌人行的武器（程序轨 / 旧读法）打一台机器 6 秒后的内核可观察结果。</summary>
        private static string Outcome(MechEnemy row, bool program)
        {
            using var site = new CombatSite("emghij-out-" + row.Id, CombatSite.ConfigFromTuning());
            CombatKernel k = site.Kernel;
            CombatWeapon w = program ? site.EnemyWeapon(null, row) : EnemySpawnBuilder.WeaponOf(null, row);
            w.Cooldown = Math.Max(0.3f, w.Cooldown);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 0), site.WeaponIndex(w), 500f, 0, CombatBehavior.HoldFire);
            float dist = Math.Max(1f, Math.Min(row.WeaponRange * 0.6f, 6f));
            int me = Unit(k, CombatFaction.Player, new double2(0, dist), -1, 5000f);
            StepN(k, 360);
            CombatUnitView v = View(k, me);
            string acc = string.Join(",", Enumerable.Range(0, EmgPhaseConst.Count).Select(i => k.AccumOf(me, i).ToString("0.###")));
            return $"{v.Health:0.#}|{v.Position.x:0.##},{v.Position.y:0.##}|{acc}|{k.EmgScalars.PatchesBorn}|{k.WreckCount}|{k.ProjectileCount}";
        }

        // ── C 劫持本身 ─────────────────────────────────────────────────────

        private static void CheckHijackCore()
        {
            // 阈值以下：入侵度 0.6 时被打死 → 照常阵亡（Killed、残骸），没有劫持
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 20f, 30f, 0.6f);
                var ev = new List<CombatEvent>();
                RunUntil(k, () => !Alive(k, foe), 4f, ev, Dt, () => k.FireAt(me, foe, k.Time));
                Expect(!Alive(k, foe) && Count(ev, CombatEventKind.Killed, foe) == 1 && Count(ev, CombatEventKind.Hijacked, foe) == 0 && k.HijackCount == 0 && k.WreckCount == 1,
                    $"负向：入侵度 0.6（阈值 1）时阵亡 → 照常击毁（Killed {Count(ev, CombatEventKind.Killed, foe)} 次、残骸 {k.WreckCount}、劫持 {k.HijackCount}）");
            }
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
                long kills0 = k.Counters.KillsHostile;
                List<CombatEvent> ev = HijackIt(k, me, foe);
                CombatUnitView v = View(k, foe);
                bool hj = k.TryGetHijack(View(k, foe).Id, out int by, out float left, out _);
                CombatEvent h = ev.FirstOrDefault(e => e.Kind == CombatEventKind.Hijacked);
                Expect(Alive(k, foe) && v.Faction == CombatFaction.Player && v.Behavior == CombatBehavior.Hijacked && Mathf.Abs(v.Health - 0.3f * v.MaxHealth) < 0.6f
                       && hj && by == me && left > 29f && h.Unit == foe && h.Other == me && Mathf.Approximately(h.Value, 30f) && h.Code == (byte)CombatFaction.Hostile,
                    $"入侵度满（两发 0.6）时阵亡 → 不阵亡、原地以 30% 耐久（{v.Health:0.#} / {v.MaxHealth:0}）重启为己方，行为 {v.Behavior}，劫持者 = 黑客机、剩余 {left:0.#}s；Hijacked 事件（持续 {h.Value}s，原阵营 {h.Code}）");
                Expect(Count(ev, CombatEventKind.Killed, foe) == 0 && k.WreckCount == 0 && k.Counters.KillsHostile == kills0 && k.AccumOf(foe, EmgPhaseConst.Hack) == 0f,
                    $"劫持瞬间不发 Killed（{Count(ev, CombatEventKind.Killed, foe)}）、不留残骸（{k.WreckCount}）、击杀计数不变（{kills0} → {k.Counters.KillsHostile}）、入侵度清零");
            }
        }

        // ── D 劫持体 AI 教义 ───────────────────────────────────────────────

        private static void CheckDoctrine()
        {
            using CombatKernel k = NewKernel();
            (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
            HijackIt(k, me, foe);
            // 黑客机不再开火（武器关掉），只看劫持体：在它射程内放一台新敌机
            k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
            int other = Unit(k, CombatFaction.Hostile, View(k, foe).Position + new double2(3, 0), -1, 400f, 0, CombatBehavior.None);
            StepN(k, 180);
            float otherHp = View(k, other).Health;
            Expect(otherHp < 400f, $"劫持体无命令时按教义攻击射程内最近的敌对单位（新敌机耐久 400 → {otherHp:0.#}）");
            k.Kill(other, 0);
            Drain(k);
            // 无目标：跟随劫持者
            k.SetPosition(me, new double2(20, 0));
            double before = math.distance(View(k, foe).Position, View(k, me).Position);
            StepN(k, 360);
            double after = math.distance(View(k, foe).Position, View(k, me).Position);
            Expect(before > 15 && after < 5.5, $"没有目标时跟随劫持者：距离 {before:0.#} → {after:0.#} 米（停在 3 米 + 半径外）");
            // 不接受编队命令
            k.IssueCommand(View(k, foe).Id, CombatCommandKind.Move, new double2(-30, 0), 0, 0.2f, 0f, 0f, false);
            StepN(k, 60);
            CombatUnitView v = View(k, foe);
            Expect(v.Command == CombatCommandKind.None && v.Position.x > -5, $"被劫持单位不接受编队命令（下达移动到 x = −30 后命令 {v.Command}、位置 x = {v.Position.x:0.#}）");
        }

        // ── E 结束 ───────────────────────────────────────────────────────

        private static void CheckEnds()
        {
            // 期满自毁：30 秒后 HijackEnded(期满) → Killed（击杀者 = 劫持者）一次、留残骸、击杀计数加一次
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
                HijackIt(k, me, foe);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                long kills0 = k.Counters.KillsHostile;
                double t0 = k.Time;
                var ev = new List<CombatEvent>();
                bool ended = RunUntil(k, () => !Alive(k, foe), 31f, ev);
                double lived = k.Time - t0;
                int endIdx = ev.FindIndex(e => e.Kind == CombatEventKind.HijackEnded && e.Unit == foe);
                int killIdx = ev.FindIndex(e => e.Kind == CombatEventKind.Killed && e.Unit == foe);
                Expect(ended && lived > 29.5 && lived < 30.5 && endIdx >= 0 && ev[endIdx].Code == EmgHijackConst.EndExpired && killIdx > endIdx
                       && Count(ev, CombatEventKind.Killed, foe) == 1 && ev[killIdx].Other == me && k.WreckCount == 1 && k.Counters.KillsHostile == kills0 + 1 && k.HijackCount == 0,
                    $"期满自毁：劫持后 {lived:0.##} 游戏秒 → HijackEnded（期满）后 Killed 恰好 1 次（击杀者 = 劫持者 {ev.ElementAtOrDefault(killIdx).Other == me}），留残骸 {k.WreckCount}，击杀计数只加一次");
            }
            // 劫持者阵亡：被劫持单位立即自毁
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
                HijackIt(k, me, foe);
                k.Kill(me, 0);
                var ev = new List<CombatEvent>();
                bool gone = RunUntil(k, () => !Alive(k, foe), 0.1f, ev);
                CombatEvent end = ev.FirstOrDefault(e => e.Kind == CombatEventKind.HijackEnded && e.Unit == foe);
                Expect(gone && end.Code == EmgHijackConst.EndHijackerLost && Count(ev, CombatEventKind.Killed, foe) == 1 && ev.First(e => e.Kind == CombatEventKind.Killed && e.Unit == foe).Other == me,
                    "劫持者阵亡 → 下一步被劫持单位立即自毁（HijackEnded 原因 = 劫持者阵亡，Killed 一次、击杀者 = 劫持者）");
            }
            // 被敌方击毁：击杀者仍记劫持者，只结算一次
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
                HijackIt(k, me, foe);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                int shooter = Unit(k, CombatFaction.Hostile, View(k, foe).Position + new double2(0, 3), k.AddWeapon(Instant(40f, 8f)), 9000f, CombatUnitFlags.Invulnerable, CombatBehavior.HoldFire);
                var ev = new List<CombatEvent>();
                bool gone = RunUntil(k, () => !Alive(k, foe), 8f, ev);
                CombatEvent end = ev.FirstOrDefault(e => e.Kind == CombatEventKind.HijackEnded && e.Unit == foe);
                CombatEvent kill = ev.FirstOrDefault(e => e.Kind == CombatEventKind.Killed && e.Unit == foe);
                Expect(gone && end.Code == EmgHijackConst.EndDestroyed && Count(ev, CombatEventKind.Killed, foe) == 1 && kill.Other == me && kill.Other != shooter,
                    "劫持中被敌方击毁 → HijackEnded（被击毁）后 Killed 一次，击杀者记劫持者（掉落与击杀只结算一次）");
            }
        }

        // ── F 负向 ───────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            // 上限满：第三台不生效并提示，照常阵亡
            using (CombatKernel k = NewKernel())
            {
                int w = k.AddWeapon(Instant(30f, 30f, HackProgram(k, 0.6f), 0.3f));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 5000f);
                var foes = new[] { 4, -4, 8 }.Select(x => Unit(k, CombatFaction.Hostile, new double2(x, 6), -1, 50f, 0, CombatBehavior.None)).ToArray();
                var ev = new List<CombatEvent>();
                RunUntil(k, () => foes.All(f => View(k, f).Behavior == CombatBehavior.Hijacked || !Alive(k, f)), 20f, ev, Dt, () =>
                {
                    int t = foes.FirstOrDefault(f => Alive(k, f) && View(k, f).Behavior != CombatBehavior.Hijacked);
                    if (t != 0)
                    {
                        k.FireAt(me, t, k.Time);
                    }
                });
                int hijacked = foes.Count(f => View(k, f).Behavior == CombatBehavior.Hijacked && Alive(k, f));
                CombatEvent refuse = ev.FirstOrDefault(e => e.Kind == CombatEventKind.HijackRefused);
                int dead = foes.First(f => !Alive(k, f) || View(k, f).Behavior != CombatBehavior.Hijacked);
                Expect(hijacked == 2 && k.HijackCount == 2 && refuse.Code == EmgHijackConst.RefuseCap && Mathf.Approximately(refuse.Value, 2f) && Mathf.Approximately(refuse.Value2, 2f)
                       && !Alive(k, dead) && Count(ev, CombatEventKind.Killed, dead) == 1,
                    $"劫持上限满（同时 2 台）：第三台入侵度满阵亡时不劫持、发 HijackRefused（上限，{refuse.Value}/{refuse.Value2}）、照常 Killed");
            }
            // 精英：入侵抗性 1，命中不累积；首领（外部血量）同样
            using (CombatKernel k = NewKernel())
            {
                (int me, int elite) = Duel(k, 400f, 1f, 0.6f, CombatUnitFlags.Elite);
                int boss = Unit(k, CombatFaction.Hostile, new double2(0, -4), -1, 400f, CombatUnitFlags.ExternalHealth, CombatBehavior.None);
                bool flip = false;
                var hits = new List<CombatEvent>();
                RunUntil(k, () => false, 3f, hits, Dt, () => k.FireAt(me, (flip = !flip) ? elite : boss, k.Time));
                Expect(k.AccumOf(elite, EmgPhaseConst.Hack) == 0f && k.AccumOf(boss, EmgPhaseConst.Hack) == 0f,
                    $"精英与首领入侵抗性默认 1：hij.hack 命中不累积（精英 {k.AccumOf(elite, EmgPhaseConst.Hack)}、首领 {k.AccumOf(boss, EmgPhaseConst.Hack)}）");
                bool Refused(int u) => hits.Any(e => e.Kind == CombatEventKind.HijackRefused && e.Unit == u && e.Code == EmgHijackConst.RefuseResist && e.Other == me);
                Expect(Refused(elite) && Refused(boss),
                    $"真实路径：hij.hack 命中精英 / 外部血量首领时当场按入侵抗性拒绝并写原因（HijackRefused 抗性：精英 {Refused(elite)}、首领 {Refused(boss)}）");
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                Drain(k);
                k.Kill(elite, me);
                k.Kill(boss, me);
                List<CombatEvent> dead = Drain(k);
                Expect(!Alive(k, elite) && !Alive(k, boss) && Count(dead, CombatEventKind.Killed, elite) == 1 && Count(dead, CombatEventKind.Killed, boss) == 1
                       && !dead.Any(e => e.Kind == CombatEventKind.Hijacked) && k.HijackCount == 0,
                    "被 hij.hack 打过的精英 / 首领被己方击毁：照常阵亡、Killed 各一次、不劫持");
            }
            // 首领 / 精英被劫持按抗性拒绝并写原因：入侵度已满后抗性变 1（抗性覆盖），阵亡时发 HijackRefused（抗性）、照常阵亡
            using (CombatKernel k = NewKernel())
            {
                (int me, int foe) = Duel(k, 400f, 1f, 0.6f);
                k.AddAccum(foe, EmgPhaseConst.Hack, 1f); // 入侵度满（同一步里抗性随后变成 1：例如精英标记 / 洗礼塔类抗性切换）
                k.SetResist(foe, EmgPhaseConst.ResHack, 1f);
                Drain(k);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                k.Kill(foe, me);
                List<CombatEvent> ev = Drain(k);
                CombatEvent refuse = ev.FirstOrDefault(e => e.Kind == CombatEventKind.HijackRefused);
                Expect(!Alive(k, foe) && refuse.Code == EmgHijackConst.RefuseResist && Count(ev, CombatEventKind.Killed, foe) == 1 && k.HijackCount == 0,
                    "入侵度满但入侵抗性 1（精英 / 首领口径）→ HijackRefused（抗性）写原因、照常阵亡");
            }
            // 被劫持单位不能被再劫持 / 中立目标不累积
            using (CombatKernel k = NewKernel())
            {
                int w = k.AddWeapon(Instant(1f, 12f, HackProgram(k, 0.6f)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 2000f);
                int neutral = k.Spawn(new CombatSpawn { ExtKey = -1, Kind = CombatUnitKind.Structure, Faction = CombatFaction.Neutral, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                    Position = new double2(0, 3), Home = new double2(0, 3), Radius = 0.5f, Health = 100f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1, Priority = 1 });
                k.FireAt(me, neutral, k.Time);
                StepN(k, 2);
                Expect(k.AccumOf(neutral, EmgPhaseConst.Hack) == 0f, "中立目标（训练靶）不吃 hij.hack（只对敌对目标）");
            }
        }

        // ── G 攻城 / 突袭队伍对账 ─────────────────────────────────────────

        private static void CheckSiege()
        {
            using CombatKernel k = NewKernel();
            (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
            int mate = Unit(k, CombatFaction.Hostile, new double2(30, 30), -1, 300f, 0, CombatBehavior.None);
            var sg = new CombatSiegeUnit { Role = 1, Group = 77 };
            k.SetSiegeUnit(foe, sg);
            k.SetSiegeUnit(mate, sg);
            int before = k.CountSiegeGroup(77);
            List<CombatEvent> ev = HijackIt(k, me, foe);
            int during = k.CountSiegeGroup(77);
            Expect(before == 2 && during == 1 && ev.Count(e => e.Kind == CombatEventKind.SiegeKilled) == 0,
                $"攻城队伍对账：劫持瞬间从队伍摘除（队伍 77 内核计数 {before} → {during}，认领键释放、剩余数减一），不发 SiegeKilled");
            k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
            var ev2 = new List<CombatEvent>();
            RunUntil(k, () => !Alive(k, foe), 31f, ev2);
            CombatEvent sk = ev2.FirstOrDefault(e => e.Kind == CombatEventKind.SiegeKilled && e.Unit == foe);
            Expect(ev2.Count(e => e.Kind == CombatEventKind.SiegeKilled) == 1 && Mathf.RoundToInt(sk.Value) == 77 && sk.Other == me && k.CountSiegeGroup(77) == 1,
                $"期满自毁时按原队伍发 SiegeKilled 恰好 1 次（队伍 {sk.Value:0}、击杀者 = 劫持者）：突袭的掉落 / 残骸 / 贡献只在这时结算；队伍计数不再变化（{k.CountSiegeGroup(77)}）");
        }

        // ── H 刷写带宽 ───────────────────────────────────────────────────

        private static void CheckBandwidth()
        {
            MechEnemy row = EnemyDefs.Rows.Where(r => EnemyDefs.HasTag(r, EnemyDefs.TagAssembled) && r.WeaponRange > 0f && !EnemyDefs.HasTag(r, EnemyDefs.TagStructure))
                .FirstOrDefault(r => EnemySpawnBuilder.TryAssembly(r, out string c, out List<string> f) && EmgProgramCompiler.FlashSteps(EmgProgramCompiler.Compile(CombatSite.EnemyPreview(c, f)), false).Count >= 1);
            if (row == null)
            {
                Fail("找不到带程序的组装敌人行做带宽用例");
                return;
            }
            foreach (int bw in new[] { 8, 1 })
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.EmgFlashBandwidth = bw;
                using var site = new CombatSite("emghij-bw-" + bw, cfg);
                CombatKernel k = site.Kernel;
                CombatWeapon ew = site.EnemyWeapon(null, row, out EmgProgramResult prog);
                int full = EmgProgramCompiler.FlashSteps(prog, false).Count;
                int native = site.WeaponIndex(ew);
                int w = k.AddWeapon(Instant(30f, 12f, HackProgram(k, 0.6f)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 2000f);
                int a = Unit(k, CombatFaction.Hostile, new double2(-2, 4), native, 50f, 0, CombatBehavior.None);
                int b = Unit(k, CombatFaction.Hostile, new double2(2, 4), native, 50f, 0, CombatBehavior.None);
                RunUntil(k, () => View(k, a).Behavior == CombatBehavior.Hijacked || !Alive(k, a), 6f, null, Dt, () => k.FireAt(me, a, k.Time));
                RunUntil(k, () => View(k, b).Behavior == CombatBehavior.Hijacked || !Alive(k, b), 6f, null, Dt, () => k.FireAt(me, b, k.Time));
                k.TryGetHijack(a, out _, out _, out int heldA);
                k.TryGetHijack(b, out _, out _, out int heldB);
                int wantB = Math.Max(0, Math.Min(full, bw - heldA));
                k.TryGetUnitWeapon(b, out CombatWeapon wb);
                bool swapped = heldB < full ? View(k, b).Weapon != native && wb.Program != ew.Program : View(k, b).Weapon == native;
                Expect(View(k, a).Behavior == CombatBehavior.Hijacked && View(k, b).Behavior == CombatBehavior.Hijacked && heldA == Math.Min(full, bw) && heldB == wantB
                       && k.FlashUsed(me) == heldA + heldB && swapped,
                    $"刷写带宽 {bw}：先后劫持两台 {row.Id}（原生程序 {full} 条）各占 {heldA} / {heldB} 条，劫持者占用 {k.FlashUsed(me)}；"
                    + (heldB < full ? $"第二台带宽不够 → 截到 {heldB} 条（武器换成“前 {heldB} 条”变体，程序 {wb.Program}）" : "放得下时武器不变"));
            }
        }

        // ── I 存读档 ─────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            using CombatKernel a = NewKernel();
            (int me, int foe) = Duel(a, 50f, 30f, 0.6f);
            HijackIt(a, me, foe);
            a.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
            StepN(a, 300); // 劫持 5 秒后存档
            Drain(a);
            byte[] snap = a.Serialize();
            using CombatKernel b = NewKernel();
            CombatLoadResult lr = b.Load(snap);
            a.TryGetHijack(View(a, foe).Id, out int ha, out float ra, out int qa);
            b.TryGetHijack(View(b, foe).Id, out int hb, out float rb, out int qb);
            Expect(lr == CombatLoadResult.Ok && CombatKernel.PeekFormat(snap) == 20 && a.StateHash() == b.StateHash() && ha == hb && Mathf.Approximately(ra, rb) && qa == qb && b.HijackCount == 1
                   && View(b, foe).Faction == CombatFaction.Player && View(b, foe).Behavior == CombatBehavior.Hijacked,
                $"劫持中途存读档（格式 {CombatKernel.PeekFormat(snap)}）：StateHash 一致、劫持者 / 剩余 {ra:0.##}s→{rb:0.##}s / 占用条数一致，阵营与行为随单位段恢复");
            var ea = new List<CombatEvent>();
            var eb = new List<CombatEvent>();
            int stepsA = 0, stepsB = 0;
            for (int s = 0; s < 2000 && (Alive(a, foe) || Alive(b, foe)); s++)
            {
                if (Alive(a, foe)) { a.Step(Dt, a.Time + Dt); ea.AddRange(Drain(a)); stepsA++; }
                if (Alive(b, foe)) { b.Step(Dt, b.Time + Dt); eb.AddRange(Drain(b)); stepsB++; }
            }
            Expect(stepsA == stepsB && a.StateHash() == b.StateHash() && Count(ea, CombatEventKind.Killed, foe) == 1 && Count(eb, CombatEventKind.Killed, foe) == 1,
                $"读档续跑：两边在同一步（{stepsA} / {stepsB}）期满自毁、Killed 各一次、结束后 StateHash 一致（不复制、不丢失）");
            // 读 19 版快照：没有段 27 = 没有劫持
            using CombatKernel c = NewKernel();
            (int me2, int foe2) = Duel(c, 400f, 1f, 0.3f);
            StepN(c, 30);
            byte[] old = c.SerializeFormatForTests(19);
            using CombatKernel d2 = NewKernel();
            Expect(CombatKernel.PeekFormat(old) == 19 && d2.Load(old) == CombatLoadResult.Ok && d2.HijackCount == 0, "读 19 版快照：段 27 不存在 → 没有劫持（缺省“未劫持”）");
        }

        // ── J 暂停与倍速 ─────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            var lived = new List<string>();
            bool ok = true;
            foreach (float mul in new[] { 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                (int me, int foe) = Duel(k, 50f, 30f, 0.6f);
                HijackIt(k, me, foe);
                k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                double t0 = k.Time;
                k.TryGetHijack(View(k, foe).Id, out _, out float r0, out _);
                for (int i = 0; i < 30; i++)
                {
                    k.Step(0f, k.Time); // 暂停：0 步长
                }
                k.TryGetHijack(View(k, foe).Id, out _, out float rp, out _);
                bool paused = Alive(k, foe) && Mathf.Approximately(r0, rp);
                RunUntil(k, () => !Alive(k, foe), 32f, null, Dt * mul);
                double t = k.Time - t0;
                ok &= paused && t > 29.4 && t < 30.6;
                lived.Add($"{mul}x：{t:0.##}s{(paused ? string.Empty : "（暂停时剩余变了）")}");
            }
            Expect(ok, $"暂停时剩余时间不动；0.5x～3x 倍速下都按 30 游戏秒到期（{string.Join("、", lived)}）");
        }

        // ── K 热更层 ─────────────────────────────────────────────────────

        private static void CheckHotLayer()
        {
            int s0 = CombatSite.HijackStarted, e0 = CombatSite.HijackEndedCount, c0 = CombatSite.HijackRefusedCap, r0 = CombatSite.HijackRefusedResist;
            bool hooked = false;
            void OnTouch(string id) => hooked |= id == GuidanceHooks.EmgFirstHijack;
            GuidanceHooks.Touched += OnTouch;
            try
            {
                using var site = new CombatSite("emghij-hot", CombatSite.ConfigFromTuning());
                CombatKernel k = site.Kernel;
                int w = k.AddWeapon(Instant(30f, 12f, HackProgram(k, 0.6f)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 2000f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 50f, 0, CombatBehavior.None);
                for (int i = 0; i < 360 && View(k, foe).Behavior != CombatBehavior.Hijacked; i++)
                {
                    k.FireAt(me, foe, k.Time);
                    site.Step(Dt, k.Time + Dt);
                }
                string hover = site.HijackHoverLine(foe);
                var note = GameLogic.Notifications.NotificationCenter.History.Concat(GameLogic.Notifications.NotificationCenter.Toasts).LastOrDefault(n => n.Type?.Id == "emg_hijacked");
                Expect(note != null, $"热更层：劫持成功发信息级通知 emg_hijacked（fg.TbNotifyType 已登记，同类聚合、可定位）：{note?.Text}");
                Expect(CombatSite.HijackStarted == s0 + 1 && hooked && hover != null && hover.Contains(GameText.Get("emg.hijack.hover.doctrine")),
                    $"热更层：Hijacked 事件 → 劫持计数 +1、引导钩子 emg_first_hijack、悬停写“由教义触发”：{hover?.Replace('\n', '｜')}");
                k.Kill(me, 0);
                for (int i = 0; i < 5; i++)
                {
                    site.Step(Dt, k.Time + Dt);
                }
                Expect(CombatSite.HijackEndedCount == e0 + 1 && !Alive(k, foe) && site.HijackHoverLine(foe) == null, "热更层：劫持者阵亡 → HijackEnded 计数 +1，劫持体自毁，悬停行消失");
            }
            finally
            {
                GuidanceHooks.Touched -= OnTouch;
            }
            Expect(CombatSite.HijackRefusedCap == c0 && CombatSite.HijackRefusedResist == r0, "热更层：没有被拒的劫持时不发拒绝通知（对照）");
            string unknown = GameText.Get("emg.hijack.unit_unknown");
            string LastNote(string type) =>
                GameLogic.Notifications.NotificationCenter.History.Concat(GameLogic.Notifications.NotificationCenter.Toasts).LastOrDefault(n => n.Type?.Id == type)?.Text;
            // 精英被 hij.hack 反复命中：热更层每台只发一次“入侵抗性”通知，文本写原因
            using (var site = new CombatSite("emghij-hot-resist", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                int w = k.AddWeapon(Instant(1f, 12f, HackProgram(k, 0.6f), 0.2f));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 2000f);
                int elite = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f, CombatUnitFlags.Elite, CombatBehavior.None);
                for (int i = 0; i < 120; i++)
                {
                    k.FireAt(me, elite, k.Time);
                    site.Step(Dt, k.Time + Dt);
                }
                string note = LastNote("emg_hijack_resist");
                string want = GameText.Format("emg.hijack.notify.resist", unknown);
                Expect(CombatSite.HijackRefusedResist == r0 + 1 && note == want,
                    $"热更层：hij.hack 反复命中精英 → emg_hijack_resist 通知恰好一次（{CombatSite.HijackRefusedResist - r0}），写原因：{note}");
            }
            // 上限满：第三台被拒 → emg_hijack_full 通知写台数
            using (var site = new CombatSite("emghij-hot-full", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                int w = k.AddWeapon(Instant(30f, 30f, HackProgram(k, 0.6f), 0.3f));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, 5000f);
                var foes = new[] { 4, -4, 8 }.Select(x => Unit(k, CombatFaction.Hostile, new double2(x, 6), -1, 50f, 0, CombatBehavior.None)).ToArray();
                for (int i = 0; i < 1200 && !foes.All(f => View(k, f).Behavior == CombatBehavior.Hijacked || !Alive(k, f)); i++)
                {
                    int t = foes.FirstOrDefault(f => Alive(k, f) && View(k, f).Behavior != CombatBehavior.Hijacked);
                    if (t != 0)
                    {
                        k.FireAt(me, t, k.Time);
                    }
                    site.Step(Dt, k.Time + Dt);
                }
                string note = LastNote("emg_hijack_full");
                string want = GameText.Format("emg.hijack.notify.full", unknown, "2", "2");
                Expect(CombatSite.HijackRefusedCap == c0 + 1 && k.HijackCount == 2 && note == want,
                    $"热更层：劫持上限满（2 台）时第三台被拒 → emg_hijack_full 通知写台数：{note}");
                // 被劫持单位在己方机器旁（信号覆盖内）也不能被接入：接入只按机器逻辑 ID 走（SetPossessed），劫持体不是机器、没有逻辑 ID
                int hj = foes.First(f => View(k, f).Behavior == CombatBehavior.Hijacked);
                Expect(!site.TryGetMachineOfUnit(hj, out _) && (View(k, hj).Flags & CombatUnitFlags.Possessed) == 0,
                    "热更层：被劫持单位没有机器逻辑 ID，接入入口（SetPossessed）够不到它，Possessed 位保持 0");
            }
        }

        /// <summary>被劫持单位在己方中继组里：收不到同组机器的信号脉冲（接收者只认机器，FGR-EMG-133）。对照：同位置的己方机器收得到。</summary>
        private static void CheckRelayExcluded()
        {
            (long sent, long got) Run(bool hijackedListener)
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.EmgVerbEmit = EmgCatalog.VerbCode("emit");
                cfg.EmgRelayRange = 12f;
                cfg.EmgRelayPerSecond = 4;
                using CombatKernel k = NewKernel(cfg);
                int listenProg = FgEmgSquadSelfCheck.TrigProg(k, 1, EmgEventKind.Ping);
                int pinger = Unit(k, CombatFaction.Player, new double2(6, 0), k.AddWeapon(Instant(1f, 30f, FgEmgSquadSelfCheck.PingProg(k, 1), 0f)), 2000f);
                k.SetUnitRelay(pinger, true);
                k.SetFlag(pinger, CombatUnitFlags.WeaponEnabled, false);
                int target = Unit(k, CombatFaction.Hostile, new double2(6, 10), -1, 100000f, 0, CombatBehavior.HoldFire);
                int listener;
                if (hijackedListener)
                {
                    int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(30f, 12f, HackProgram(k, 0.6f))), 2000f);
                    listener = Unit(k, CombatFaction.Hostile, new double2(0, 4), k.AddWeapon(Instant(1f, 1f, listenProg)), 50f, 0, CombatBehavior.HoldFire);
                    HijackIt(k, me, listener);
                    k.SetFlag(me, CombatUnitFlags.WeaponEnabled, false);
                    if (View(k, listener).Behavior != CombatBehavior.Hijacked)
                    {
                        return (-1, -1);
                    }
                }
                else
                {
                    listener = Unit(k, CombatFaction.Player, new double2(0, 4), k.AddWeapon(Instant(1f, 1f, listenProg)), 2000f);
                }
                long s0 = k.EmgBusStats.PingsSent, g0 = k.EmgBusStats.PingsDelivered;
                k.SetFlag(pinger, CombatUnitFlags.WeaponEnabled, true);
                k.FireAt(pinger, target, k.Time);
                StepN(k, 1);
                return (k.EmgBusStats.PingsSent - s0, k.EmgBusStats.PingsDelivered - g0);
            }
            var hij = Run(true);
            var ctl = Run(false);
            Expect(hij.sent >= 1 && hij.got == 0 && ctl.sent >= 1 && ctl.got >= 1,
                $"被劫持单位处在己方中继覆盖内也不能被接入：同组脉冲送达被劫持体 {hij.got} 次（广播 {hij.sent}）；对照己方机器送达 {ctl.got} 次（广播 {ctl.sent}）");
        }

        // ── L DEBT-EMG2PHS01-05：入侵度来源已由 hij.hack 接上（C 段）；燃烧单位走过油区引燃的对照 ─────

        private static void CheckBurningOnOil()
        {
            int Fires(CombatKernel k)
            {
                int n = 0;
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Fire)
                    {
                        n++;
                    }
                }
                return n;
            }
            int Run(bool burning)
            {
                using CombatKernel k = NewKernel();
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 0), -1, 5000f, 0, CombatBehavior.None);
                if (burning)
                {
                    k.AddAccum(foe, EmgPhaseConst.Ignite, 1.5f);
                }
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 1f, 20f);
                for (int s = 0; s < 480 && Fires(k) == 0; s++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                return Fires(k);
            }
            int on = Run(true), off = Run(false);
            Expect(on > 0 && off == 0, $"DEBT-EMG2PHS01-05 对照：燃烧中的敌人站在油区里 8 秒内引燃（明火斑块 {on} 块）；不燃烧的对照不引燃（{off}）。入侵度来源 = hij.hack 写端（C 段），放热数值归 FG15-BAL-01");
        }

        // ── 断言 ─────────────────────────────────────────────────────────

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

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("  ✗ " + what);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
