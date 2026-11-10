using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-HIJ-02 夺控、槽位封锁统一与镜像复制的自动验收（FG19 FGR-EMG-164 / 165、031；FGT-EMG-061 夺控 / 封锁 / 镜像部分与三类共用事件、FGT-EMG-010 封锁重排用例；
    /// FG10 FGR-EVT-021～022；DEBT-EMG1FW02-03）。起真实内核（CombatKernel，Burst 步进）、真实编译器、真实 CombatSite / 机器登记 / 信号核：
    /// A 表（emg.seize.* 调参进内核配置、通知类型、文本、引导钩子）；B 夺控（己方机器翻成敌对、按教义攻击原同伴、不接受编队命令；拒绝：接入中 / 已被控制 / 敌机）；
    /// C 瘫痪（D-10：被击倒不阵亡、不发 Killed / evt.death、自毁装药不起爆、瘫痪中什么都不做也不被选中；夺控结束恢复耐久；对照：归还后真正阵亡时自毁装药照常起爆一次）；
    /// D 结束（期满归还、调用方解除、来源单位阵亡、反向接入 3 秒夺回、瘫痪时拒绝反向接入、地点卸载按期满归还）；E 存读档（夺控中 + 引导中 + 瘫痪读档续跑哈希一致）；
    /// F 暂停与 0.5x～3x；G 三类共用状态机与事件（劫持 + 夺控同表互不覆盖、劫持上限不算夺控、evt.hijack / evt.seize / evt.release 生产者）；
    /// H 槽位封锁（FGT-EMG-010：被封槽按空槽重编、触发器下游回到上一层、解除恢复同签名；冷却不重置；离开再接入仍封锁；存读档；原因行写触发器说明）；
    /// K 热更层（SignalSeizeService → 通知、悬停、HUD 行；外部血量机器致死伤害 → 瘫痪不走 MarkDead；接入中的机器不被夺控；封锁 / 解除给接入机器注入 evt.seize / evt.release）；
    /// M 镜像复制（1 代程序、容量 C(1)、触发器子载荷按容量截断 / 代际已满、混合固件展开后溢出、复制品不能再被复制、占镜像体刷写带宽）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgSeizeSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgSeizeSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgSeizeSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const string Split = "fw_split", Homing = "fw_homing", Pierce = "fw_pierce", Oil = "fw_oilleak", EtchShell = "fw_mix_etchshell", Scuttle = "fw_scuttle";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/夺控、槽位封锁与镜像复制")]
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
            Line("\n[夺控、槽位封锁与镜像复制] EMG4-HIJ-02：夺控 / 瘫痪 / 反向接入、槽位封锁重编、三类共用事件、镜像复制（FGT-EMG-061 / 010）");
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
                SignalSeizeService.ResetForTests();
                Step(CheckTables);
                Step(CheckSeizeCore);
                Step(CheckDowned);
                Step(CheckEnds);
                Step(CheckSaveLoad);
                Step(CheckSpeed);
                Step(CheckShared);
                Step(CheckSlotBlock);
                Step(CheckHotLayer);
                Step(CheckMirror);
            }
            catch (Exception e)
            {
                Fail($"夺控自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                SignalSeizeService.ResetForTests();
            }
            Line($"  · [夺控、槽位封锁与镜像复制] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(CombatConfig? cfg = null)
        {
            CombatConfig c = cfg ?? CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, 64);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            return k;
        }

        /// <summary>己方机器（外部血量，与正式机器同一组标志）/ 己方普通单位 / 敌机。</summary>
        private static int Machine(CombatKernel k, double2 p, int weapon, float hp = 300f) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
            Flags = Live | CombatUnitFlags.ExternalHealth | CombatUnitFlags.Report, Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp,
            Weapon = weapon, BehaviorProfile = -1, Priority = 1,
        });

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatBehavior behavior = CombatBehavior.None) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
            Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : behavior,
            Flags = Live | CombatUnitFlags.Report, Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
        });

        private static CombatWeapon Instant(float damage, float range = 12f, int program = -1, float cooldown = 0.5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = cooldown, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        /// <summary>测试程序行：根节点读 evt.hijack / evt.seize / evt.release（只为让这三个事件有监听者、能计入队数；不写任何东西）。</summary>
        private static int ListenProgram(CombatKernel k)
        {
            var kinds = new[] { EmgEventKind.Hijack, EmgEventKind.Seize, EmgEventKind.Release };
            var instrs = kinds.Select(e => new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)e), Role = EmgConst.RoleTrigger, Amount = 1f }).ToList();
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            uint mask = kinds.Aggregate(0u, (m, e) => m | 1u << (int)e);
            return k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
        }

        /// <summary>测试固件行：命中 → hij.hack inject（同 FgEmgHijackSelfCheck；正式固件 N39 随 EMG4-FW-01）。</summary>
        private static int HackProgram(CombatKernel k, float amount)
        {
            var instrs = new List<EmgInstr> { new EmgInstr { Port = EmgConst.PortHack, Role = EmgConst.RoleLastRead + 1, Verb = (byte)EmgVerb.Inject, Amount = amount } };
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 } };
            return k.AddProgram(new EmgProgram(), nodes, instrs);
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

        private static List<CombatEvent> Run(CombatKernel k, int steps, float dt = Dt)
        {
            var ev = new List<CombatEvent>();
            for (int s = 0; s < steps; s++)
            {
                k.Step(dt, k.Time + dt);
                ev.AddRange(Drain(k));
            }
            return ev;
        }

        private static CombatUnitView View(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v : default;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static int Count(List<CombatEvent> ev, CombatEventKind kind, int unit) => ev.Count(e => e.Kind == kind && e.Unit == unit);

        private static (float rem, bool down, float reclaim) Seize(CombatKernel k, int id)
        {
            k.TryGetSeize(id, out float rem, out bool down, out float reclaim, out _);
            return (rem, down, reclaim);
        }

        // ── A 表 ─────────────────────────────────────────────────────────────

        private static void CheckTables()
        {
            bool tuned = GridContent.TryGetTuning("emg.seize.seconds", out float sec) && GridContent.TryGetTuning("emg.seize.reclaim_seconds", out float rec)
                         && GridContent.TryGetTuning("emg.seize.recover_ratio", out float ratio) && Mathf.Approximately(sec, 45f) && Mathf.Approximately(rec, 3f) && Mathf.Approximately(ratio, 0.3f);
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            Expect(tuned && Mathf.Approximately(cfg.EmgSeizeRecoverRatio, 0.3f) && Mathf.Approximately(SignalSeizeService.SeizeSeconds, 45f) && Mathf.Approximately(SignalSeizeService.ReclaimSeconds, 3f),
                "fg.TbHomeTuning：emg.seize.seconds 45 / reclaim_seconds 3 / recover_ratio 0.3（FGR-EVT-021 / 022），恢复比例进内核配置");
            string[] types = { "emg_seized", "emg_seize_downed", "emg_seize_ended", "emg_slot_blocked", "emg_slot_restored" };
            string[] missingTypes = types.Where(t => !NotificationCatalog.TryGetType(t, out _)).ToArray();
            Expect(missingTypes.Length == 0, $"通知类型 5 种已登记（fg.TbNotifyType；夺控 / 封锁 = 警告级）：缺 {string.Join("、", missingTypes)}");
            string[] keys =
            {
                "emg.seize.notify.started", "emg.seize.notify.downed", "emg.seize.notify.reclaimed", "emg.seize.notify.recovered", "emg.seize.notify.ended", "emg.seize.hover",
                "emg.seize.hover.doctrine", "emg.seize.hover.downed", "emg.seize.slot.reason", "emg.seize.slot.trigger_note", "emg.seize.notify.slot_blocked", "emg.seize.notify.slot_restored",
                "emg.seize.hud.machine", "emg.seize.hud.slot", "emg.seize.reclaim.started", "emg.seize.reclaim.downed", "emg.mirror.refuse_copy", "emg.seize.source.ember",
            };
            string[] missing = keys.Where(x => !GameText.Has(x)).ToArray();
            Expect(missing.Length == 0, $"文本键齐（中英）：缺 {string.Join("、", missing)}");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstSeize) && GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstSlotBlock), "引导钩子 emg_first_seize / emg_first_slot_block 已登记（内容归 FG15-UX-04）");
        }

        // ── B 夺控 ───────────────────────────────────────────────────────────

        private static void CheckSeizeCore()
        {
            using CombatKernel k = NewKernel();
            int gun = k.AddWeapon(Instant(10f, 12f));
            int me = Machine(k, new double2(0, 0), gun);
            int buddy = Unit(k, CombatFaction.Player, new double2(4, 0), -1, 1000f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(30, 0), -1, 500f, CombatBehavior.HoldFire);
            Drain(k);
            EmgSeizeResult r = k.Seize(me, 45f, 0, (ushort)SignalSeizeService.SourceGhost);
            List<CombatEvent> ev = Run(k, 120);
            CombatEvent seized = ev.FirstOrDefault(e => e.Kind == CombatEventKind.Seized && e.Unit == me);
            CombatUnitView v = View(k, me);
            Expect(r == EmgSeizeResult.Ok && v.Faction == CombatFaction.Hostile && v.Behavior == CombatBehavior.Hijacked && k.SeizeCount == 1 && k.HijackCount == 0
                   && seized.Kind == CombatEventKind.Seized && Mathf.Approximately(seized.Value, 45f) && seized.Code == SignalSeizeService.SourceGhost,
                $"夺控：己方机器翻成敌对阵营、行为 Hijacked（同一套状态机），Seized 事件带 45 秒与来源码；夺控表 1 条、劫持计数 0（{r}）");
            float buddyHp = View(k, buddy).Health;
            Expect(buddyHp < 1000f, $"被夺控的机器按教义攻击射程内原来的同伴（同伴耐久 1000 → {buddyHp:0.#}）");
            k.IssueCommand(me, CombatCommandKind.Move, new double2(-30, 0), 0, 0.2f, 0f, 0f, false);
            StepN(k, 60);
            v = View(k, me);
            Expect(v.Command == CombatCommandKind.None && v.Position.x > -5, $"被夺控的机器不接受编队命令（下达移动到 x = −30 后命令 {v.Command}、位置 x = {v.Position.x:0.#}）");
            // 拒绝：接入中 / 已被控制 / 敌机 / 不存在
            int linked = Machine(k, new double2(-8, 0), gun);
            k.SetFlag(linked, CombatUnitFlags.Possessed, true);
            EmgSeizeResult up = k.Seize(linked, 45f, 0, 1), again = k.Seize(me, 45f, 0, 1), enemy = k.Seize(foe, 45f, 0, 1), none = k.Seize(99999, 45f, 0, 1);
            Expect(up == EmgSeizeResult.Uplinked && again == EmgSeizeResult.Controlled && enemy == EmgSeizeResult.NotMachine && none == EmgSeizeResult.NoUnit && View(k, linked).Faction == CombatFaction.Player,
                $"拒绝：玩家正接入的那台永远不被夺控（{up}）、已被夺控（{again}）、敌机（{enemy}）、不存在（{none}）");
        }

        // ── C 瘫痪（DEBT-EMG1FW02-03 / D-10）──────────────────────────────────

        /// <summary>装着自毁装药（fw_scuttle，读 evt.death 放 4 份子载荷）的己方机器，与 FgEmgAddonFirmwareSelfCheck.ScuttleExternal 同一编法。</summary>
        private static int ScuttleWeapon(CombatKernel k)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { Scuttle });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            w.Program = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(p));
            return k.AddWeapon(w);
        }

        private static void CheckDowned()
        {
            using CombatKernel k = NewKernel();
            int w = ScuttleWeapon(k);
            int me = Machine(k, new double2(0, 0), w);
            int foe = Unit(k, CombatFaction.Player, new double2(4, 0), -1, 5000f); // 打它的人（己方单位，夺控后是敌对）
            StepN(k, 30);
            Drain(k);
            k.Seize(me, 45f, 0, 1);
            StepN(k, 5);
            long sub0 = k.EmgBusStats.SubSpawned, death0 = k.EmgEnqueuedOf(EmgEventKind.Death);
            k.Kill(me, foe); // 内核阵亡统一入口（即死类效果走这里）
            List<CombatEvent> ev = Run(k, 60);
            (float rem, bool down, _) = Seize(k, me);
            CombatUnitView v = View(k, me);
            Expect(Alive(k, me) && down && Count(ev, CombatEventKind.Killed, me) == 0 && Count(ev, CombatEventKind.SeizeDowned, me) == 1 && v.Health <= 0f
                   && (v.Flags & CombatUnitFlags.Targetable) == 0 && k.EmgEnqueuedOf(EmgEventKind.Death) == death0 && k.EmgBusStats.SubSpawned == sub0,
                $"夺控中的机器被击倒 → 瘫痪不阵亡：Killed {Count(ev, CombatEventKind.Killed, me)} 次、SeizeDowned 1 次、evt.death 入队 {k.EmgEnqueuedOf(EmgEventKind.Death) - death0} 次、自毁装药子载荷 {k.EmgBusStats.SubSpawned - sub0} 份、不可选中");
            double2 pos = v.Position;
            float foeHp = View(k, foe).Health;
            k.Kill(me, foe); // 重复的致死结算：幂等
            ev = Run(k, 120);
            Expect(Count(ev, CombatEventKind.SeizeDowned, me) == 0 && math.distance(View(k, me).Position, pos) < 1e-6 && Mathf.Approximately(View(k, foe).Health, foeHp),
                "瘫痪中什么都不做（不移动、不开火），重复的致死结算不再发事件");
            // 归还：恢复 30% 耐久、重新可选中、阵营回到己方；SeizeEnded 的 Code2 标“归还前瘫痪”
            k.ReleaseSeize(me);
            ev = Run(k, 2);
            CombatEvent end = ev.FirstOrDefault(e => e.Kind == CombatEventKind.SeizeEnded && e.Unit == me);
            v = View(k, me);
            Expect(end.Kind == CombatEventKind.SeizeEnded && end.Code == EmgHijackConst.EndReleased && end.Code2 == 1 && Mathf.Approximately(v.Health, 90f) && v.Faction == CombatFaction.Player
                   && v.Behavior == CombatBehavior.Commanded && (v.Flags & CombatUnitFlags.Targetable) != 0 && k.SeizeCount == 0,
                $"夺控结束：瘫痪的机器恢复到 30% 耐久（{v.Health:0.#} / 300）、重新可选中、回到己方与原行为，SeizeEnded 标“归还前瘫痪”");
            // 对照：归还后真正阵亡（热更层判死 → Kill）——阵亡统一入口只走一次，自毁装药照常起爆
            k.SetHealth(me, 0f, 300f, false);
            k.Kill(me, 0);
            ev = Run(k, 60);
            Expect(!Alive(k, me) && k.EmgEnqueuedOf(EmgEventKind.Death) == death0 + 1 && k.EmgBusStats.SubSpawned > sub0,
                $"对照：归还后真正阵亡 → evt.death 一次、自毁装药放出子载荷 {k.EmgBusStats.SubSpawned - sub0} 份（瘫痪没有起爆不是因为固件坏了）");
            // 内核接口 DownSeized（热更层的外部血量致死伤害走这里）：同一套瘫痪；不是夺控中的机器 → false
            int m2 = Machine(k, new double2(-6, 0), k.AddWeapon(Instant(5f)));
            bool notSeized = k.DownSeized(m2, 0);
            k.Seize(m2, 45f, 0, 1);
            bool downed = k.DownSeized(m2, foe);
            Expect(!notSeized && downed && Seize(k, m2).down && Alive(k, m2), "DownSeized：没被夺控的机器不处理（返回 false，照常走阵亡）；夺控中的 → 瘫痪");
        }

        // ── D 结束 ───────────────────────────────────────────────────────────

        private static void CheckEnds()
        {
            using CombatKernel k = NewKernel();
            int gun = k.AddWeapon(Instant(1f, 1f));
            int a = Machine(k, new double2(0, 0), gun), b = Machine(k, new double2(10, 0), gun), c = Machine(k, new double2(20, 0), gun), d2 = Machine(k, new double2(30, 0), gun);
            int seizer = Unit(k, CombatFaction.Hostile, new double2(60, 0), -1, 100f, CombatBehavior.HoldFire);
            k.Seize(a, 45f, 0, 1);
            k.Seize(b, 0f, 0, 2); // 直到解除（根权限压制：阶段结束才恢复）
            k.Seize(c, 45f, seizer, 2);
            k.Seize(d2, 45f, 0, 1);
            // 己方单位正对 d2 执行攻击命令（武器几乎无伤害）：夺回后命令以“目标丢失”结束，不会追着自己人打
            int hunter = Unit(k, CombatFaction.Player, new double2(30, 40), k.AddWeapon(Instant(0.01f, 60f)), 1000f);
            bool hunting = k.IssueCommand(hunter, CombatCommandKind.Attack, new double2(30, 0), d2, 0.5f, 60f, 0.5f, false);
            Drain(k);
            bool reclaim = k.BeginReclaim(d2, 3f);
            bool twice = k.BeginReclaim(d2, 3f);
            double t0 = k.Time;
            var ev = new List<CombatEvent>();
            double reclaimedAt = -1, expiredAt = -1;
            for (int s = 0; s < 60 * 50; s++)
            {
                k.Step(Dt, k.Time + Dt);
                foreach (CombatEvent e in Drain(k))
                {
                    ev.Add(e);
                    if (e.Kind == CombatEventKind.SeizeEnded && e.Unit == d2)
                    {
                        reclaimedAt = k.Time - t0;
                    }
                    if (e.Kind == CombatEventKind.SeizeEnded && e.Unit == a)
                    {
                        expiredAt = k.Time - t0;
                    }
                }
                if (s == 60)
                {
                    k.Kill(seizer, 0); // 来源单位阵亡 → c 归还
                }
            }
            CombatEvent ea = ev.FirstOrDefault(e => e.Kind == CombatEventKind.SeizeEnded && e.Unit == a);
            CombatEvent ec = ev.FirstOrDefault(e => e.Kind == CombatEventKind.SeizeEnded && e.Unit == c);
            CombatEvent ed = ev.FirstOrDefault(e => e.Kind == CombatEventKind.SeizeEnded && e.Unit == d2);
            Expect(reclaim && !twice && ed.Code == EmgHijackConst.EndReclaimed && reclaimedAt > 2.95 && reclaimedAt < 3.1 && View(k, d2).Faction == CombatFaction.Player,
                $"反向接入：引导 3 游戏秒后夺回（{reclaimedAt:0.##} 秒，原因 夺回），引导中再发起被拒");
            Expect(hunting && ev.Count(e => e.Kind == CombatEventKind.CommandEnded && e.Unit == hunter && e.Other == d2 && e.Code == (byte)CombatEndReason.TargetLost) == 1
                   && View(k, hunter).Command == CombatCommandKind.None,
                "夺控结束：己方对这台的攻击命令以“目标丢失”结束一次（CommandEnded），不再追着自己人打");
            Expect(ea.Code == EmgHijackConst.EndExpired && expiredAt > 44.9 && expiredAt < 45.1 && Alive(k, a) && View(k, a).Faction == CombatFaction.Player,
                $"期满：45 游戏秒后归还（{expiredAt:0.##} 秒），不自毁");
            Expect(ec.Code == EmgHijackConst.EndHijackerLost && View(k, c).Faction == CombatFaction.Player, "来源单位阵亡 → 立即归还（原因 来源已不在）");
            Expect(k.TryGetSeize(b, out float remB, out _, out _, out _) && remB < 0f && View(k, b).Faction == CombatFaction.Hostile,
                "seconds = 0：直到调用方解除（50 秒后仍被夺控，剩余读数 −1 = 直到解除）");
            Expect(ev.Count(e => e.Kind == CombatEventKind.Killed && (e.Unit == a || e.Unit == c || e.Unit == d2)) == 0 && ev.Count(e => e.Kind == CombatEventKind.SeizeEnded) == 3,
                "期满 / 来源阵亡 / 夺回都是归还：没有 Killed，SeizeEnded 恰好 3 次");
            // 瘫痪时拒绝反向接入；地点卸载 → 按期满归还（不留在敌方手里）
            k.DownSeized(b, 0);
            bool downReclaim = k.BeginReclaim(b, 3f);
            int released = k.ExpireAllHijacks();
            ev = Run(k, 2);
            Expect(!downReclaim && released == 1 && k.SeizeCount == 0 && View(k, b).Faction == CombatFaction.Player && Alive(k, b) && Count(ev, CombatEventKind.Killed, b) == 0,
                $"瘫痪中拒绝反向接入；地点卸载（ExpireAllHijacks）把夺控按期满归还 {released} 台、不击毁");
        }

        // ── E 存读档 ─────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            using CombatKernel a = NewKernel();
            int gun = a.AddWeapon(Instant(1f, 1f));
            int m1 = Machine(a, new double2(0, 0), gun), m2 = Machine(a, new double2(10, 0), gun), m3 = Machine(a, new double2(20, 0), gun);
            a.Seize(m1, 45f, 0, 1);
            a.Seize(m2, 45f, 0, 3);
            a.Seize(m3, 45f, 0, 2);
            StepN(a, 300);
            a.BeginReclaim(m2, 3f);
            a.DownSeized(m3, 0);
            StepN(a, 60);
            Drain(a);
            byte[] snap = a.Serialize();
            using CombatKernel b = NewKernel();
            CombatLoadResult lr = b.Load(snap);
            bool same = new[] { m1, m2, m3 }.All(id =>
            {
                a.TryGetSeize(id, out float ra, out bool da, out float ca, out ushort sa);
                b.TryGetSeize(id, out float rb, out bool db, out float cb, out ushort sb);
                return Mathf.Approximately(ra, rb) && da == db && Mathf.Approximately(ca, cb) && sa == sb;
            });
            Expect(lr == CombatLoadResult.Ok && CombatKernel.PeekFormat(snap) == 20 && a.StateHash() == b.StateHash() && same && b.SeizeCount == 3 && Seize(b, m3).down && Seize(b, m2).reclaim > 0f,
                $"夺控中途存读档（格式 {CombatKernel.PeekFormat(snap)}，段 27 同版本追加字段）：StateHash 一致，剩余 / 瘫痪 / 反向接入进度 / 来源逐台一致");
            var ea = new List<CombatEvent>();
            var eb = new List<CombatEvent>();
            for (int s = 0; s < 60 * 46; s++)
            {
                a.Step(Dt, a.Time + Dt);
                b.Step(Dt, b.Time + Dt);
                ea.AddRange(Drain(a));
                eb.AddRange(Drain(b));
            }
            string Seq(List<CombatEvent> ev) => string.Join(",", ev.Where(e => e.Kind == CombatEventKind.SeizeEnded).Select(e => $"{e.Unit}:{e.Code}:{e.Code2}"));
            Expect(a.StateHash() == b.StateHash() && Seq(ea) == Seq(eb) && b.SeizeCount == 0 && ea.Count(e => e.Kind == CombatEventKind.SeizeEnded) == 3,
                $"读档续跑：夺回 / 期满 / 瘫痪恢复在同一步发生（{Seq(eb)}），结束后 StateHash 一致");
            using CombatKernel c = NewKernel();
            Machine(c, double2.zero, c.AddWeapon(Instant(1f)));
            byte[] old = c.SerializeFormatForTests(19);
            using CombatKernel d = NewKernel();
            Expect(d.Load(old) == CombatLoadResult.Ok && d.SeizeCount == 0 && d.HijackCount == 0, "读 19 版快照：没有段 27 → 没有劫持也没有夺控");
        }

        // ── F 暂停与倍速 ─────────────────────────────────────────────────────

        private static void CheckSpeed()
        {
            var lived = new List<string>();
            bool ok = true;
            foreach (float mul in new[] { 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel k = NewKernel();
                int gun = k.AddWeapon(Instant(1f, 1f));
                int m = Machine(k, double2.zero, gun), r = Machine(k, new double2(10, 0), gun);
                k.Seize(m, 45f, 0, 1);
                k.Seize(r, 45f, 0, 1);
                k.BeginReclaim(r, 3f);
                double t0 = k.Time;
                (float r0, _, float c0) = Seize(k, m);
                (_, _, float cr0) = Seize(k, r);
                for (int i = 0; i < 30; i++)
                {
                    k.Step(0f, k.Time);
                }
                bool paused = Mathf.Approximately(r0, Seize(k, m).rem) && Mathf.Approximately(cr0, Seize(k, r).reclaim);
                double reclaimAt = -1, endAt = -1;
                for (int s = 0; s < 46 * 60 * 2 && endAt < 0; s++)
                {
                    k.Step(Dt * mul, k.Time + Dt * mul);
                    foreach (CombatEvent e in Drain(k))
                    {
                        if (e.Kind == CombatEventKind.SeizeEnded && e.Unit == r)
                        {
                            reclaimAt = k.Time - t0;
                        }
                        if (e.Kind == CombatEventKind.SeizeEnded && e.Unit == m)
                        {
                            endAt = k.Time - t0;
                        }
                    }
                }
                ok &= paused && reclaimAt > 2.9 && reclaimAt < 3.2 && endAt > 44.9 && endAt < 45.2;
                lived.Add($"{mul}x：夺回 {reclaimAt:0.##}s、期满 {endAt:0.##}s{(paused ? string.Empty : "（暂停时读数变了）")}");
            }
            Expect(ok, $"暂停时剩余与引导进度不动；0.5x～3x 下反向接入 3 游戏秒、期满 45 游戏秒（{string.Join("、", lived)}）");
        }

        // ── G 三类共用状态机与事件 ───────────────────────────────────────────

        private static void CheckShared()
        {
            using CombatKernel k = NewKernel();
            int listen = ListenProgram(k);
            int hackGun = k.AddWeapon(Instant(30f, 12f, HackProgram(k, 0.6f)));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), hackGun, 5000f);
            int foe1 = Unit(k, CombatFaction.Hostile, new double2(0, 5), k.AddWeapon(Instant(1f, 1f, listen)), 50f, CombatBehavior.None);
            int foe2 = Unit(k, CombatFaction.Hostile, new double2(3, 5), k.AddWeapon(Instant(1f, 1f, listen)), 50f, CombatBehavior.None);
            int mach = Machine(k, new double2(-20, 0), k.AddWeapon(Instant(1f, 1f, listen)));
            long h0 = k.EmgEnqueuedOf(EmgEventKind.Hijack), s0 = k.EmgEnqueuedOf(EmgEventKind.Seize), r0 = k.EmgEnqueuedOf(EmgEventKind.Release);
            k.Seize(mach, 45f, 0, 1);
            var ev = new List<CombatEvent>();
            for (int i = 0; i < 900 && !(View(k, foe1).Behavior == CombatBehavior.Hijacked && View(k, foe2).Behavior == CombatBehavior.Hijacked); i++)
            {
                int t = View(k, foe1).Behavior != CombatBehavior.Hijacked ? foe1 : foe2;
                k.FireAt(me, t, k.Time);
                k.Step(Dt, k.Time + Dt);
                ev.AddRange(Drain(k));
            }
            Expect(k.HijackCount == 2 && k.SeizeCount == 1 && ev.Count(e => e.Kind == CombatEventKind.HijackRefused) == 0,
                $"同一张表：夺控 1 台的同时还能劫持满 2 台（劫持上限只数劫持方向）：劫持 {k.HijackCount}、夺控 {k.SeizeCount}");
            Expect(k.EmgEnqueuedOf(EmgEventKind.Hijack) - h0 == 2 && k.EmgEnqueuedOf(EmgEventKind.Seize) - s0 == 1,
                $"共用事件：劫持成立发 evt.hijack（{k.EmgEnqueuedOf(EmgEventKind.Hijack) - h0}）、夺控发 evt.seize（{k.EmgEnqueuedOf(EmgEventKind.Seize) - s0}）");
            k.ReleaseSeize(mach);
            StepN(k, 2);
            Expect(k.HijackCount == 2 && View(k, foe1).Behavior == CombatBehavior.Hijacked && k.EmgEnqueuedOf(EmgEventKind.Release) - r0 == 1,
                "解除夺控不影响劫持（2 台仍被劫持），发 evt.release 一次");
            k.Kill(me, 0); // 劫持者阵亡 → 两台劫持体期满自毁
            StepN(k, 3);
            Expect(k.HijackCount == 0 && k.SeizeCount == 0 && k.EmgEnqueuedOf(EmgEventKind.Release) - r0 == 3 && View(k, mach).Faction == CombatFaction.Player,
                $"劫持结束同样发 evt.release（累计 {k.EmgEnqueuedOf(EmgEventKind.Release) - r0} 次 = 夺控 1 + 劫持 2），不影响已归还的机器");
            // 被劫持的敌机不能再被夺控、被夺控的机器不能被劫持判定（互不覆盖）
            int foe3 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 50f, CombatBehavior.None);
            int me2 = Unit(k, CombatFaction.Player, new double2(0, 0), hackGun, 5000f);
            for (int i = 0; i < 600 && View(k, foe3).Behavior != CombatBehavior.Hijacked; i++)
            {
                k.FireAt(me2, foe3, k.Time);
                k.Step(Dt, k.Time + Dt);
            }
            EmgSeizeResult onHijacked = k.Seize(foe3, 45f, 0, 1);
            int mach2 = Machine(k, new double2(0, 8), k.AddWeapon(Instant(1f, 1f)));
            k.Seize(mach2, 45f, 0, 1);
            Drain(k);
            for (int i = 0; i < 300 && !Seize(k, mach2).down; i++)
            {
                k.FireAt(me2, mach2, k.Time); // 带 hij.hack 的己方武器打被夺控的机器：外部血量 → 伤害请求；这里直接按内核即死入口收尾
                k.Step(Dt, k.Time + Dt);
                if (i == 120)
                {
                    k.Kill(mach2, me2);
                }
            }
            List<CombatEvent> tail = Drain(k);
            Expect(onHijacked == EmgSeizeResult.Controlled && Seize(k, mach2).down && Count(tail, CombatEventKind.Hijacked, mach2) == 0 && k.HijackCount == 1,
                "互不覆盖：被劫持的敌机不能再被夺控（Controlled）；被夺控的机器挨了 hij.hack 再被击倒 → 瘫痪，不会被“劫持”回来");
        }

        // ── H 槽位封锁 ───────────────────────────────────────────────────────

        private static CampaignState NewState(int seed)
        {
            CampaignState s = CampaignState.CreateNew("emgseize-" + seed, "Standard", seed);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation, BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId, ConstructionState = BuildingConstructionState.Operational, PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 1000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            return s;
        }

        private static void Equip(CampaignState s, string firmwareId, int slot)
        {
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            try
            {
                SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
                SignalCoreResult eq = pr.Success ? SignalCoreService.TryEquip(s, pr.CreatedId, slot) : pr;
                if (!eq.Success)
                {
                    Fail($"测试准备：装 {firmwareId} 到 {slot + 1} 号槽失败（{eq.Message}）");
                }
            }
            finally
            {
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            }
        }

        private static EmgProgramResult CompileCore(CampaignState s) =>
            EmgProgramCompiler.Compile(SignalCoreService.ActiveContentIds(s).Where(x => x.Length > 0).ToArray(), new[] { Pierce, Oil }, FirmwareCarrier.Projectile);

        private static string Tree(EmgProgramResult r) =>
            string.Join(" | ", r.Nodes.Select(n => $"{n.Gen}代:" + string.Join("+", n.Entries.Select(e => r.Steps.First(st => st.Ordinal == e.Ordinal).FirmwareId.Replace("fw_", string.Empty)))));

        private static void CheckSlotBlock()
        {
            CampaignState s = NewState(41);
            Equip(s, Split, 0);
            Equip(s, Homing, 1);
            EmgProgramResult before = CompileCore(s);
            int rev = SignalCoreService.Revision;
            var cd = new SignalCoreCooldownRecord { ContentId = Split, ReadyTick = GameClock.Ticks + 600 };
            s.SignalCore.CoreCooldowns = new[] { cd };
            double cd0 = SignalUplinkService.CooldownRemaining(s, Split);
            bool blocked = SignalSeizeService.BlockSlot(s, 0, SignalSeizeService.SourceEmber);
            bool twice = SignalSeizeService.BlockSlot(s, 0, SignalSeizeService.SourceEmber);
            EmgProgramResult during = CompileCore(s);
            Expect(blocked && !twice && SignalCoreService.Revision > rev && SignalCoreService.ActiveContentIds(s)[0].Length == 0 && SignalCoreService.SlotContentId(s, 0) == Split,
                "封锁 1 号槽：版本 +1（接入中的机器重编译），生效列表里该槽为空串，固件仍装在槽里；重复封锁被拒");
            // FGT-EMG-010 封锁重排：[分裂, 寻的 | 贯穿, 漏油] → 根 = 分裂 + 漏油、1 代 = 寻的 + 贯穿；封掉分裂（触发器）→ 下游回到根：根 = 寻的 + 贯穿 + 漏油，没有子节点
            Expect(before.Nodes.Count == 2 && Tree(before) == "0代:split+oilleak | 1代:homing+pierce" && during.Nodes.Count == 1 && Tree(during) == "0代:homing+pierce+oilleak",
                $"FGT-EMG-010 封锁重排：封锁前 {Tree(before)}；被封槽是触发器 → 下游指令回到上一层节点：{Tree(during)}");
            string why = SignalCoreService.SlotStatusLine(s, 0), whyHoming = SignalSeizeService.BlockedReason(s, 1);
            Expect(why.Contains(GameText.Get("emg.seize.slot.trigger_note")) && why.Contains(GameText.Get("emg.seize.source.ember")) && whyHoming.Length == 0,
                $"预览说明：被封槽状态行写来源、按空槽重编、冷却不重置，是触发器时写“下游指令回到上一层节点”：{why}");
            // 离开再接入：封锁记在信号核里，不跟机器走
            s.SignalCore.UplinkMachineLogicId = 0;
            s.SignalCore.UplinkMachineLogicId = 12345;
            Expect(SignalSeizeService.IsBlocked(s, 0) && SignalCoreService.ActiveContentIds(s)[0].Length == 0, "封锁期间离开再接入（换一台）：槽仍被封锁，生效列表仍为空串");
            // 存读档（同一序列化器 JsonUtility）
            CampaignState loaded = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(s));
            CampaignFgStateDomains.EnsureAll(loaded);
            Expect(SignalSeizeService.IsBlocked(loaded, 0) && loaded.SignalCore.SlotBlocks[0].Source == SignalSeizeService.SourceEmber && !SignalSeizeService.IsBlocked(loaded, 1),
                "存读档往返：被封锁的槽、来源随信号核存档域恢复");
            bool restored = SignalSeizeService.UnblockSlot(s, 0);
            EmgProgramResult after = CompileCore(s);
            double cd1 = SignalUplinkService.CooldownRemaining(s, Split);
            Expect(restored && !SignalSeizeService.IsBlocked(s, 0) && after.Signature == before.Signature && Math.Abs(cd1 - cd0) < 1e-6 && s.SignalCore.CoreCooldowns[0].ReadyTick == cd.ReadyTick,
                $"解除立即恢复：程序树与封锁前同签名；冷却不重置（剩余 {cd0:0.##} → {cd1:0.##} 秒，ReadyTick 不变）");
            SignalSeizeService.BlockSlot(s, 0, SignalSeizeService.SourceEmber);
            SignalSeizeService.BlockSlot(s, 1, SignalSeizeService.SourceRoot);
            int n = SignalSeizeService.UnblockAll(s, SignalSeizeService.SourceEmber);
            Expect(n == 1 && !SignalSeizeService.IsBlocked(s, 0) && SignalSeizeService.IsBlocked(s, 1) && !SignalSeizeService.BlockSlot(s, 99, 1),
                "按来源解除（首领被击毁）只解除该来源的槽；越界槽号被拒");
        }

        // ── K 热更层 ─────────────────────────────────────────────────────────

        private static void CheckHotLayer()
        {
            const string siteId = "emgseize-hot";
            CampaignState s = NewState(42);
            int started = CombatSite.SeizeStarted, downs = CombatSite.SeizeDownedCount, ends = CombatSite.SeizeEndedCount;
            bool hooked = false;
            void OnTouch(string id) => hooked |= id == GuidanceHooks.EmgFirstSeize;
            GuidanceHooks.Touched += OnTouch;
            try
            {
                CombatSite site = CombatSites.Open(siteId, s, false, null, out _);
                MachineOpResult r1 = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, siteId, Vector2.zero, 200f, 200f);
                MachineOpResult r2 = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, siteId, new Vector2(30f, 0f), 200f, 200f);
                MachineRegistry.TryGetRecord(r1.LogicId, out MachineRecord rec);
                MachineRegistry.TryGetRecord(r2.LogicId, out MachineRecord rec2);
                site.SpawnMachine(s, rec, Vector2.zero, false);
                site.SpawnMachine(s, rec2, new Vector2(30f, 0f), false);
                site.TryGetMachineUnit(rec.LogicId, out int unit);
                site.TryGetMachineUnit(rec2.LogicId, out int unit2);
                CombatKernel k = site.Kernel;
                int shooter = Unit(k, CombatFaction.Player, new double2(0, 6), k.AddWeapon(Instant(60f, 12f)), 5000f); // 打被夺控机器的己方单位
                s.SignalCore.UplinkMachineLogicId = rec2.LogicId; // 测试捷径：信号在第二台里（接入流程由 FgSignalUplinkSelfCheck 覆盖）
                EmgSeizeResult linked = SignalSeizeService.Seize(s, rec2.LogicId, SignalSeizeService.SourceGhost, 45f);
                EmgSeizeResult ok = SignalSeizeService.Seize(s, rec.LogicId, SignalSeizeService.SourceGhost, 45f);
                for (int i = 0; i < 3; i++)
                {
                    site.Step(Dt, k.Time + Dt);
                }
                string hover = site.SeizeHoverLine(unit);
                string hud = SignalSeizeService.HudLine(s);
                var note = NotificationCenter.History.Concat(NotificationCenter.Toasts).LastOrDefault(n => n.Type?.Id == "emg_seized");
                Expect(linked == EmgSeizeResult.Uplinked && ok == EmgSeizeResult.Ok && CombatSite.SeizeStarted == started + 1 && hooked && note != null && hover != null
                       && hover.Contains(GameText.Get("emg.seize.hover.doctrine")) && hud.Contains(SignalPresence.MachineLabel(rec.LogicId)) && hud.Contains(GameText.Get("emg.seize.source.ghost")),
                    $"热更层：接入中的那台不被夺控（{linked}）；夺控另一台 → 警告级通知 emg_seized、引导钩子、悬停“由敌方信号驱动”、HUD 行写机器与来源：{hud}");
                // 远征 / 室内地点右键攻击与直控瞄准认被夺控的机器（FGR-EVT-022 应对“把它击倒”）：控制器的 FindHostileNear / HostileUnit / TryFindEnemyInAim
                // 都经这几个地点入口；编队攻击命令的出口是 CombatSite.IssueCommand（RegionSquadCommandSystem 同一调用），直控开火的出口是 TryFireAtEnemy
                bool picked = site.TryFindNearestEnemy(Vector2.zero, 2f, out string hostileId) && CombatSite.IsSeizedHostileId(hostileId);
                bool aimed = site.TryFindEnemyInCone(new Vector2(0f, 6f), Vector2.down, 12f, 30f, out string coneId) && coneId == hostileId;
                bool resolved = site.TryGetEnemyUnit(hostileId, out int pickedUnit) && pickedUnit == unit && site.IsEnemyAlive(hostileId) && site.TryGetEnemyPosition(hostileId, out _);
                site.TryFireAtEnemy(rec2.LogicId, coneId, out CombatFireResult direct, out _); // rec2 在 30 米外：认得出目标（射程外 / 已开火），不是“目标不存在”
                bool issued = site.IssueCommand(shooter, CombatCommandKind.Attack, Vector2.zero, pickedUnit, 0.5f, 12f, 0.5f, false);
                Expect(picked && aimed && resolved && issued && direct != CombatFireResult.TargetMissing && direct != CombatFireResult.NoAttacker,
                    $"远征地点右键 / 直控瞄准认被夺控的机器（敌人 ID {hostileId}）：解析回内核单位、存活、有位置；直控开火 {direct}；编队攻击命令成立");
                // 攻击命令把它打到 0：外部血量机器的致死伤害请求 → 瘫痪，不走 MarkDeadByLogicId
                for (int i = 0; i < 600 && !(k.TryGetSeize(unit, out _, out bool dn, out _, out _) && dn); i++)
                {
                    site.Step(Dt, k.Time + Dt);
                }
                site.Step(Dt, k.Time + Dt); // 瘫痪事件在拦截的那一步之后才取走（热更层在取事件时调 DownSeized，事件下一步交付）
                k.TryGetSeize(unit, out _, out bool downed, out _, out _);
                Expect(downed && rec.IsAlive && rec.Health <= 0f && CombatSite.SeizeDownedCount == downs + 1 && Alive(k, unit)
                       && !site.TryGetEnemyUnit(hostileId, out _) && View(k, shooter).Command == CombatCommandKind.None,
                    $"热更层：攻击命令把夺控中的机器打到 0 → 瘫痪（记录仍存活 IsAlive = {rec.IsAlive}、耐久 {rec.Health:0.#}、内核单位存活），不阵亡、不走 MarkDeadByLogicId；瘫痪后不再是攻击目标，攻击命令结束");
                bool reclaimDown = SignalSeizeService.BeginReclaim(rec.LogicId, out string downText);
                SignalSeizeService.Release(rec.LogicId);
                site.Step(Dt, k.Time + Dt);
                Expect(!reclaimDown && downText == GameText.Format("emg.seize.reclaim.downed", SignalPresence.MachineLabel(rec.LogicId)) && CombatSite.SeizeEndedCount == ends + 1
                       && Mathf.Approximately(rec.Health, 60f) && rec.IsAlive && View(k, unit).Faction == CombatFaction.Player,
                    $"瘫痪时反向接入被拒并写原因（{downText}）；解除后记录耐久恢复到 30%（{rec.Health:0.#} / 200）、回到己方");
                // 反向接入：服务入口 → 3 游戏秒后夺回，通知“已夺回”
                SignalSeizeService.Seize(s, rec.LogicId, SignalSeizeService.SourceRoot, 0f);
                bool began = SignalSeizeService.BeginReclaim(rec.LogicId, out string beganText);
                for (int i = 0; i < 200; i++)
                {
                    site.Step(Dt, k.Time + Dt);
                }
                Expect(began && !SignalSeizeService.IsSeized(rec.LogicId) && View(k, unit).Faction == CombatFaction.Player
                       && CombatSite.LastSeizeEndText == GameText.Format("emg.seize.notify.reclaimed", SignalPresence.MachineLabel(rec.LogicId)),
                    $"反向接入（服务入口）：{beganText} → 3 秒后夺回，通知：{CombatSite.LastSeizeEndText}");
                // 统一入口带来源单位（根权限压制的中继）：来源被击毁 → 立即归还；给了不在本地点的来源 → 拒绝
                var relay = new RegionEnemyRecord { EnemyInstanceId = "emgseize-relay", RegionId = siteId, Position = new Vector2(80f, 80f), Health = 100f, MaxHealth = 100f, IsAlive = true };
                s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(relay).ToArray();
                int relayUnit = site.SpawnEnemy(relay, new CombatSpawn
                {
                    Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire, Flags = Live, Radius = 0.5f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                EmgSeizeResult noSource = SignalSeizeService.Seize(s, rec.LogicId, SignalSeizeService.SourceRoot, 0f, "emgseize-nobody");
                EmgSeizeResult bySource = SignalSeizeService.Seize(s, rec.LogicId, SignalSeizeService.SourceRoot, 0f, relay.EnemyInstanceId);
                bool heldWhileRelay = SignalSeizeService.IsSeized(rec.LogicId);
                k.Kill(relayUnit, 0);
                for (int i = 0; i < 3; i++)
                {
                    site.Step(Dt, k.Time + Dt);
                }
                Expect(noSource == EmgSeizeResult.NoUnit && bySource == EmgSeizeResult.Ok && heldWhileRelay && !SignalSeizeService.IsSeized(rec.LogicId) && View(k, unit).Faction == CombatFaction.Player,
                    $"统一入口带来源单位：来源不在本地点被拒（{noSource}）；来源（中继）被击毁 → 立即归还（{CombatSite.LastSeizeEndText}）");
                // 封锁 / 解除给接入的机器注入 evt.seize / evt.release（N41 反夺控“本机被夺控或槽位被封锁时”）
                Equip(s, Split, 0);
                SignalSeizeService.BlockSlot(s, 0, SignalSeizeService.SourceEmber);
                EmgInjectResult noListen = SignalSeizeService.LastSlotInject;
                SignalSeizeService.UnblockSlot(s, 0);
                int listenWeapon = k.AddWeapon(Instant(1f, 1f, ListenProgram(k)));
                k.SetUnitWeapon(unit2, listenWeapon);
                EmgSeizeResult during = SignalSeizeService.Seize(s, rec.LogicId, SignalSeizeService.SourceGhost, 45f); // 夺控与槽位封锁同时生效，互不覆盖
                long s0 = k.EmgEnqueuedOf(EmgEventKind.Seize), r0 = k.EmgEnqueuedOf(EmgEventKind.Release);
                SignalSeizeService.BlockSlot(s, 0, SignalSeizeService.SourceEmber);
                EmgInjectResult seizeInj = SignalSeizeService.LastSlotInject;
                string hudSlot = SignalSeizeService.HudLine(s);
                SignalSeizeService.UnblockSlot(s, 0);
                EmgInjectResult releaseInj = SignalSeizeService.LastSlotInject;
                Expect(noListen == EmgInjectResult.NoListener && seizeInj == EmgInjectResult.Enqueued && releaseInj == EmgInjectResult.Enqueued
                       && k.EmgEnqueuedOf(EmgEventKind.Seize) == s0 + 1 && k.EmgEnqueuedOf(EmgEventKind.Release) == r0 + 1 && hudSlot.Contains(GameText.Format("emg.seize.hud.slot", "1", GameText.Get("emg.seize.source.ember"))),
                    $"槽位封锁与夺控共用事件：封锁 / 解除给接入中的机器注入 evt.seize / evt.release（程序不读时 {noListen}，读时各入队 1 次）；HUD 行写被封锁的槽：{hudSlot}");
                Expect(during == EmgSeizeResult.Ok && SignalSeizeService.IsSeized(rec.LogicId) && k.SeizeCount == 1 && hudSlot.Contains(SignalPresence.MachineLabel(rec.LogicId)),
                    "夺控与槽位封锁同时生效：封锁 / 解除不动夺控记录（仍 1 条），HUD 行同时写被夺控的机器与被封锁的槽（劫持与夺控同表互不覆盖见 G 段）");
                SignalSeizeService.Release(rec.LogicId);
            }
            finally
            {
                GuidanceHooks.Touched -= OnTouch;
                CombatSites.Close(siteId, s, true);
                MachineRegistry.ResetForNewCampaign();
            }
        }

        // ── M 镜像复制 ───────────────────────────────────────────────────────

        private static void CheckMirror()
        {
            BlueprintCircuitPreview P(params string[] fw) => CombatSite.EnemyPreview("comp_gun", fw);
            EmgProgramResult orig = EmgProgramCompiler.Compile(P(Split, Homing, Pierce));
            EmgProgramResult copy = EmgProgramCompiler.CompileCopy(P(Split, Homing, Pierce), false, out string none);
            Expect(none == null && copy != null && copy.IsCopy && copy.Nodes[0].Gen == 1 && copy.Nodes[0].Capacity == 2 && (copy.Program.Flags & EmgProgramFlags.IsCopy) != 0
                   && Tree(orig) == "0代:split | 1代:homing+pierce" && Tree(copy) == "1代:split+pierce | 2代:homing" && copy.Nodes[1].Capacity == 1,
                $"复制品是 1 代程序、根容量 C(1) = 2：原版 {Tree(orig)} → 复制品 {Tree(copy)}（触发器的子载荷是 2 代、容量 1，按容量截断；容量 {copy?.Nodes[0].Capacity}/{(copy?.Nodes.Count > 1 ? copy.Nodes[1].Capacity : -1)}、标志 {copy?.Program.Flags}）");
            EmgProgramResult o2 = EmgProgramCompiler.Compile(P(Split, Split));
            EmgProgramResult c2 = EmgProgramCompiler.CompileCopy(P(Split, Split), false, out _);
            Expect(c2.Notes.Exists(n => n.Key == EmgProgramCompiler.NoteGenFull) && !o2.Notes.Exists(n => n.Key == EmgProgramCompiler.NoteGenFull) && o2.Nodes.Count == 3 && c2.Nodes.Count == 2,
                $"复制触发器：原版 [分裂, 分裂] 有 2 级子载荷（{o2.Nodes.Count} 个节点）；复制品的第二个分裂在 2 代“代际已满”，只剩 1 级（{c2.Nodes.Count} 个节点）");
            EmgProgramResult o3 = EmgProgramCompiler.Compile(P(EtchShell, Homing));
            EmgProgramResult c3 = EmgProgramCompiler.CompileCopy(P(EtchShell, Homing), false, out _);
            int mixSteps = c3.Steps.Count(st => st.FirmwareId == EtchShell && !st.Builtin);
            Expect(mixSteps == 2 && c3.Steps.First(st => st.FirmwareId == Homing).Overflow && !o3.Steps.First(st => st.FirmwareId == Homing).Overflow,
                $"复制混合固件按本地版本：溶蚀弹头展开为 {mixSteps} 条后占满 C(1)，寻的溢出（原版容量 4 不溢出）");
            EmgProgramResult again = EmgProgramCompiler.CompileCopy(P(Split), true, out string refuse);
            Expect(again == null && refuse == EmgProgramCompiler.NoteCopyRefused && GameText.Has(refuse), $"复制品不能再被复制：编译器拒绝并给原因键 {refuse}（{GameText.Get(refuse)}）");
            // 地点：占镜像体自己的刷写带宽（剩余带宽不够从末尾截断），复制品的复制品被拒
            foreach (int bw in new[] { 8, 1 })
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.EmgFlashBandwidth = bw;
                using var site = new CombatSite("emgseize-mirror-" + bw, cfg);
                CombatKernel k = site.Kernel;
                int mirror = Unit(k, CombatFaction.Hostile, new double2(0, 0), k.AddWeapon(Instant(5f)), 300f, CombatBehavior.HoldFire);
                int mirror2 = Unit(k, CombatFaction.Hostile, new double2(3, 0), k.AddWeapon(Instant(5f)), 300f, CombatBehavior.HoldFire);
                int c0 = CombatSite.MirrorCopied;
                MirrorCopyResult res = site.MirrorCopy(mirror, "comp_gun", new[] { Split, Homing, Pierce });
                k.TryGetUnitWeapon(mirror, out CombatWeapon mw);
                int used = k.FlashUsed(mirror);
                if (bw == 8)
                {
                    Expect(res.Ok && res.Held == 3 && res.Dropped == 0 && used == 3 && site.UnitHoldsCopy(mirror) && mw.Program >= 0 && CombatSite.MirrorCopied == c0 + 1,
                        $"镜像复制（带宽 8）：复制品持有 {res.Held} 条、占镜像体自己的刷写带宽 {used} 条，换上带复制品程序的武器");
                    MirrorCopyResult second = site.MirrorCopy(mirror2, "comp_gun", new[] { Split }, mirror);
                    MirrorCopyResult swap = site.MirrorCopy(mirror, "comp_gun", new[] { Homing });
                    Expect(!second.Ok && second.RefuseKey == EmgProgramCompiler.NoteCopyRefused && !site.UnitHoldsCopy(mirror2) && swap.Ok && swap.Held == 1 && k.FlashUsed(mirror) == 1,
                        "复制品的复制品被拒（来源持有复制品）；镜像体换复制新固件时旧复制品先让出带宽（不叠加，占用 3 → 1）");
                }
                else
                {
                    Expect(res.Ok && res.Held == 1 && res.Dropped == 2 && used == 1 && site.UnitHoldsCopy(mirror),
                        $"镜像复制（带宽 1）：复制品从末尾截到放得下（持有 {res.Held}、截掉 {res.Dropped} 条），占用 {used} 条");
                }
            }
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
