using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMGR-FIELD-01 场的进场 / 离场事件（FG19 FGR-EMG-060 第 14、15 行与 :459“进入的斑块 / 场中心”；FGR-EMG-062 按需；FG19A N14 入场引信；ADR-EMG-001 ④；DEBT-EMG1FW02-02 场这一半）。
    /// 起真实内核（CombatSite 同一套表写入）：A 入场引信（真实编译）弹体穿过 6 种场各分裂一次（己方场照样分裂；敌方偏折场先分裂再反射）；B 单位进出 6 种场各一次 enter / leave、走进走出；
    /// C 无人机机身（游走 / 哨戒桩）进出场与进出斑块（游走只碰气体层，哨戒桩插地碰地面层）；D 物质位与场位互不覆盖（单位 / 弹体）；E 负向（场刚生成 / 刚消失、弹体在场内被销毁、没人读不入队）；
    /// F 存读档（跨场边界读档不重复 enter、旧档无人机段缺字段第一拍只发一次、暂停与 0.5x～3x）；G 残骸不发、快照占位不错位。
    /// 段名 FgEmgFieldEdgeSelfCheck（并入 CellFrameworkValidate.RunAll；单段：bash tools/unity-validate.sh --segment FgEmgFieldEdgeSelfCheck）。
    /// </summary>
    public static partial class FgEmgFieldSelfCheck
    {
        private const byte EdgeCond = 2;
        private static readonly ushort[] EdgePorts = { Pull, Push, Vortex, Magnet, Deflect, Corona };

        public static int RunEdges(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[场的进场 / 离场] EMGR-FIELD-01：6 种场 × 弹体 / 单位 / 无人机机身的 evt.enter / evt.leave、入场引信、物质位与场位、存读档与倍速、残骸结论（FGR-EMG-060 第 14、15 行）");
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
                Step(CheckEdgeFuse);
                Step(CheckEdgeUnits);
                Step(CheckEdgeDrones);
                Step(CheckEdgeBits);
                Step(CheckEdgeNegative);
                Step(CheckEdgeSaveLoad);
                Step(CheckEdgeWreck);
            }
            catch (Exception e)
            {
                Fail($"场进出自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [场的进场 / 离场] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>根节点读 evt.enter / evt.leave 的最小程序（只监听，不放子载荷）。</summary>
        private static int EdgeListen(CombatKernel k)
        {
            var instrs = new List<EmgInstr>
            {
                new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Enter), Role = EdgeCond, Ordinal = 0 },
                new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Leave), Role = EdgeCond, Ordinal = 1 },
            };
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            return k.AddProgram(new EmgProgram { ListenMask = (1u << (int)EmgEventKind.Enter) | (1u << (int)EmgEventKind.Leave) }, nodes, instrs);
        }

        private static CombatWeapon EdgeShot(int program, float speed = 20f, float life = 3f)
        {
            CombatWeapon w = Flight(1f, speed, life, program);
            w.Cooldown = 30f; // 只打 FireAt 的那一发
            return w;
        }

        private static int Walker(CombatKernel k, double2 p, int weapon)
        {
            return k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = Live, Position = p, Home = p, Radius = 0.5f, Speed = 4f, Health = 400f, MaxHealth = 400f, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            });
        }

        /// <summary>静默阵营的真实偏折场单位（FG9-FAC-01：DeflectArmed + 阶段 = 展开，走 DeflectActive、不在场表里；与首领状态机同一入口 SetDeflectPhase）。半径 3 米，收起后 30 秒再展开。</summary>
        private static int Prism(CombatKernel k, double2 p, int weapon = -1, float up = 60f)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live, Position = p, Home = p, Radius = 0.5f, Speed = 0f,
                Health = 50000f, MaxHealth = 50000f, Weapon = weapon, Priority = 1,
                BehaviorProfile = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f, DeflectGap = 30f }),
            });
            k.SetFlag(id, CombatUnitFlags.DeflectArmed, true);
            k.SetDeflectPhase(id, 2, up);
            return id;
        }

        /// <summary>逐步记下本步新入队的 enter / leave（同一步多条时只能取到最后处理的一条，Raw 记原始条数）。</summary>
        private sealed class EdgeLog
        {
            public readonly List<EmgEvent> Enter = new List<EmgEvent>();
            public readonly List<EmgEvent> Leave = new List<EmgEvent>();
            public long EnterRaw, LeaveRaw;

            public int EnterOf(EmgEntKind kind, int ent = 0) => Enter.Count(e => e.EntKind == kind && (ent == 0 || e.Ent == ent));
            public int LeaveOf(EmgEntKind kind, int ent = 0) => Leave.Count(e => e.EntKind == kind && (ent == 0 || e.Ent == ent));
        }

        private static void EdgeRun(CombatKernel k, int steps, EdgeLog log, float dt = Dt)
        {
            for (int s = 0; s < steps; s++)
            {
                long e0 = k.EmgEnqueuedOf(EmgEventKind.Enter), l0 = k.EmgEnqueuedOf(EmgEventKind.Leave);
                k.Step(dt, k.Time + dt);
                long e1 = k.EmgEnqueuedOf(EmgEventKind.Enter), l1 = k.EmgEnqueuedOf(EmgEventKind.Leave);
                if (e1 > e0 && k.TryGetLastEmgEvent(EmgEventKind.Enter, out EmgEvent ee))
                {
                    log.Enter.Add(ee);
                }
                if (l1 > l0 && k.TryGetLastEmgEvent(EmgEventKind.Leave, out EmgEvent le))
                {
                    log.Leave.Add(le);
                }
                log.EnterRaw += e1 - e0;
                log.LeaveRaw += l1 - l0;
            }
        }

        private static uint UnitMask(CombatKernel k, int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState st) ? st.PrevMatterMask : 0xFFFFFFFFu;

        private static uint DroneMask(CombatKernel k, int index) => k.TryGetDrone(index, out CombatDrone dr) ? dr.PrevMatterMask : 0xFFFFFFFFu;

        private static string PortName(ushort p) => p switch
        {
            Pull => "牵引", Push => "推斥", Vortex => "涡旋", Magnet => "磁", Deflect => "偏折", Corona => "电晕", _ => p.ToString(),
        };

        // ── A. 入场引信穿过 6 种场 ─────────────────────────────────────────────

        private static void CheckEdgeFuse()
        {
            Line("  · A. FG19A N14 入场引信（真实编译）：弹体穿过 6 种场各分裂一次（Code = 场端口、Other = 场 Id、位置 = 场中心）；己方场也分裂；敌方偏折场先分裂再反射；对照：路上没有场不分裂");
            EmgProgramResult fuse = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_enterfuse" }, FirmwareCarrier.Projectile);
            (long sub, int enter, EmgEvent first, int fieldId, bool reflected) Fuse(ushort port, bool field, CombatFaction ff)
            {
                using CombatKernel k = NewKernel();
                int prog = CombatSite.RegisterProgram(k, fuse);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(EdgeShot(prog)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 20), hp: 50000f);
                int fid = 0;
                if (field)
                {
                    k.SpawnField(port, 0, 10, 2f, 5f, ff == CombatFaction.Player ? me : foe, ff);
                    fid = k.FieldAt(0).Id;
                }
                long before = k.EmgBusStats.SubSpawned;
                k.FireAt(me, foe, k.Time);
                var log = new EdgeLog();
                EdgeRun(k, 40, log);
                bool refl = false;
                for (int i = 0; i < k.ProjectileCount; i++)
                {
                    refl |= k.TryGetProjectile(i, out CombatProjectile p) && p.Faction == CombatFaction.Hostile;
                }
                List<EmgEvent> pe = log.Enter.Where(e => e.EntKind == EmgEntKind.Projectile).ToList();
                return (k.EmgBusStats.SubSpawned - before, pe.Count, pe.FirstOrDefault(), fid, refl);
            }
            var bad = new List<string>();
            var lines = new List<string>();
            foreach (ushort port in EdgePorts)
            {
                var r = Fuse(port, true, CombatFaction.Player);
                bool ok = r.sub == 1 && r.enter == 1 && r.first.Code == port && r.first.OtherKind == EmgEntKind.Field && r.first.Other == r.fieldId
                          && math.distance(r.first.Pos, new double2(0, 10)) < 1e-6;
                lines.Add($"{PortName(port)} 分裂 {r.sub}");
                if (!ok)
                {
                    bad.Add($"{PortName(port)}（分裂 {r.sub}、enter {r.enter}、Code {r.first.Code}、Other {r.first.Other}/{r.fieldId}、位置 {r.first.Pos.x:0.##},{r.first.Pos.y:0.##}）");
                }
            }
            Expect(!fuse.IsEmpty && bad.Count == 0, $"入场引信穿过己方 6 种场：{string.Join("、", lines)}；evt.enter 各 1 条，Code = 场端口、Other = 场 Id、位置 = 场中心{(bad.Count > 0 ? "；不符：" + string.Join("；", bad) : string.Empty)}");
            var hostile = Fuse(Deflect, true, CombatFaction.Hostile);
            var none = Fuse(Corona, false, CombatFaction.Player);
            Expect(hostile.sub == 1 && hostile.enter == 1 && hostile.reflected && none.sub == 0 && none.enter == 0,
                $"敌方偏折场：先分裂 {hostile.sub} 次（enter {hostile.enter}）再被反射（反射后弹体归敌方 {hostile.reflected}）；对照路上没有场：分裂 {none.sub}、enter {none.enter}");
            // 修复轮 1（审查 P1）：玩家实际遇到的敌方偏折场是静默阵营单位技能（DeflectActive，不在场表里，FG19 第 886 行）
            using (CombatKernel k = NewKernel())
            {
                int prog = CombatSite.RegisterProgram(k, fuse);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(EdgeShot(prog)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 20), hp: 50000f);
                int prism = Prism(k, new double2(0, 10));
                long before = k.EmgBusStats.SubSpawned;
                k.FireAt(me, foe, k.Time);
                var log = new EdgeLog();
                EdgeRun(k, 40, log);
                bool refl = false;
                for (int i = 0; i < k.ProjectileCount; i++)
                {
                    refl |= k.TryGetProjectile(i, out CombatProjectile p) && p.Faction == CombatFaction.Hostile;
                }
                long sub = k.EmgBusStats.SubSpawned - before;
                List<EmgEvent> pe = log.Enter.Where(e => e.EntKind == EmgEntKind.Projectile).ToList();
                EmgEvent first = pe.FirstOrDefault();
                Expect(sub == 1 && pe.Count == 1 && refl && first.Code == Deflect && first.OtherKind == EmgEntKind.Unit && first.Other == prism && math.distance(first.Pos, new double2(0, 10)) < 1e-6,
                    $"静默阵营的真实偏折场（展开阶段）：先分裂 {sub} 次（enter {pe.Count} 条，Code {first.Code}、Other = 场源单位 {first.Other}/{prism}、种类 {first.OtherKind}、位置 = 场心）再被反射（{refl}）");
            }
        }

        // ── B. 单位进出 6 种场 ─────────────────────────────────────────────────

        private static void CheckEdgeUnits()
        {
            Line("  · B. FGR-EMG-060 第 14、15 行：单位（宿主 = 本机武器程序）进出 6 种场各一次 evt.enter / evt.leave；走进 / 走出场各一次；场里没有斑块时不发物质码的进出");
            var bad = new List<string>();
            foreach (ushort port in EdgePorts)
            {
                using CombatKernel k = NewKernel();
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, listen)), kind: CombatUnitKind.Turret);
                k.SpawnField(port, 0, 0, 3f, 1f, me, CombatFaction.Player);
                int fid = k.FieldAt(0).Id;
                var log = new EdgeLog();
                EdgeRun(k, 30, log);
                uint inside = UnitMask(k, me);
                EdgeRun(k, 60, log);
                EmgEvent en = log.Enter.FirstOrDefault(), lv = log.Leave.FirstOrDefault();
                bool ok = log.EnterOf(EmgEntKind.Unit, me) == 1 && log.LeaveOf(EmgEntKind.Unit, me) == 1 && log.EnterRaw == 1 && log.LeaveRaw == 1
                          && en.Code == port && en.Other == fid && en.OtherKind == EmgEntKind.Field && lv.Code == port
                          && (inside & EmgFieldConst.FieldBits) == EmgFieldConst.FieldBit(port) && (inside & EmgFieldConst.MatterBits) == 0u
                          && (UnitMask(k, me) & EmgFieldConst.FieldBits) == 0u;
                if (!ok)
                {
                    bad.Add($"{PortName(port)}（enter {log.EnterRaw} Code {en.Code} Other {en.Other}/{fid}，leave {log.LeaveRaw} Code {lv.Code}，场内掩码 {inside:X}，到期后 {UnitMask(k, me):X}）");
                }
            }
            Expect(bad.Count == 0, $"站在 6 种场里的炮塔：每种 enter 1 条（Code = 端口、Other = 场 Id）、场到期 leave 1 条；场内掩码只有对应场位、物质位为 0，到期后场位清掉{(bad.Count > 0 ? "；不符：" + string.Join("；", bad) : string.Empty)}");

            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Walker(k, new double2(0, 0), k.AddWeapon(Instant(1f, listen)));
                k.SpawnField(Corona, 7, 0, 2f, 30f, me, CombatFaction.Player);
                k.IssueCommand(me, CombatCommandKind.Move, new double2(14, 0), 0, 0.3f, 0f, 1f, false);
                var log = new EdgeLog();
                int enterStep = -1, leaveStep = -1;
                for (int s = 0; s < 300; s++)
                {
                    int e0 = log.Enter.Count, l0 = log.Leave.Count;
                    EdgeRun(k, 1, log);
                    enterStep = enterStep < 0 && log.Enter.Count > e0 ? s : enterStep;
                    leaveStep = leaveStep < 0 && log.Leave.Count > l0 ? s : leaveStep;
                }
                double2 end = Pos(k, me);
                Expect(log.EnterRaw == 1 && log.LeaveRaw == 1 && enterStep >= 0 && leaveStep > enterStep && end.x > 12.0 && log.Enter[0].Code == Corona && log.Leave[0].Code == Corona,
                    $"机器右键移动穿过电晕场：走进那一步 enter 1 条（第 {enterStep} 步）、走出 leave 1 条（第 {leaveStep} 步）、终点 x = {end.x:0.0}；都只发一次");
            }

            // 修复轮 1（审查 P1）：静默阵营的偏折场（不在场表里）——单位进出照发、场源自己不算；首领状态机拨到收起（步间）当场补 leave
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, listen)), kind: CombatUnitKind.Turret);
                int prism = Prism(k, new double2(2, 0), k.AddWeapon(Instant(1f, listen)));
                var log = new EdgeLog();
                EdgeRun(k, 30, log);
                uint inside = UnitMask(k, me), self = UnitMask(k, prism);
                long l0 = k.EmgEnqueuedOf(EmgEventKind.Leave);
                k.SetDeflectPhase(prism, 0, 30f);
                long atFold = k.EmgEnqueuedOf(EmgEventKind.Leave) - l0; // 拨相位时当场入队，下一步总线处理
                EdgeRun(k, 30, log);
                bool folded = atFold == 1 && k.TryGetLastEmgEvent(EmgEventKind.Leave, out EmgEvent fl) && fl.Ent == me && fl.Code == Deflect;
                EmgEvent en = log.Enter.FirstOrDefault();
                Expect(log.EnterRaw == 1 && en.Ent == me && en.Code == Deflect && en.OtherKind == EmgEntKind.Unit && en.Other == prism && math.distance(en.Pos, new double2(2, 0)) < 1e-6
                       && (inside & EmgFieldConst.FieldBits) == EmgFieldConst.FieldBit(Deflect) && (self & EmgFieldConst.FieldBits) == 0u && folded && log.LeaveRaw == 0
                       && (UnitMask(k, me) & EmgFieldConst.FieldBits) == 0u,
                    $"静默偏折场罩住炮塔：enter {log.EnterRaw} 条（Code {en.Code}、Other = 场源 {en.Other}/{prism}、位置 = 场心），场源自己不进（掩码 {self:X}）；首领状态机拨到收起当场 leave（{folded}），之后不再发（{log.LeaveRaw}），场位清零");
            }
            // 同上，步内到时收起（场表为空、StepFields 不再运行）：炮塔与哨戒桩同一步各 leave 1 条
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int w = k.AddWeapon(Instant(1f, listen));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), w, kind: CombatUnitKind.Turret);
                int post = k.AddDroneForTests(new double2(1, 2), me, CombatFaction.Player, true, 30f, w);
                Prism(k, new double2(2, 0), up: 0.5f);
                var log = new EdgeLog();
                EdgeRun(k, 15, log);
                uint dm = DroneMask(k, post);
                EdgeRun(k, 45, log);
                Expect(log.EnterRaw == 2 && log.LeaveRaw == 2 && dm == EmgFieldConst.FieldBit(Deflect) && (UnitMask(k, me) & EmgFieldConst.FieldBits) == 0u && DroneMask(k, post) == 0u,
                    $"静默偏折场展开 0.5 秒后自己收起：炮塔 + 哨戒桩 enter {log.EnterRaw} 条、收起那一步 leave {log.LeaveRaw} 条（各 1）；哨戒桩场内掩码 {dm:X}，收起后都清零");
            }
        }

        // ── C. 无人机机身 ───────────────────────────────────────────────────────

        private static void CheckEdgeDrones()
        {
            Line("  · C. 无人机机身按单位算（FGR-EMG-060 第 14、15 行）：游走无人机 / 哨戒桩进出场各一次（宿主 = 无人机）；游走无人机进出气体层斑块、不碰地面层；哨戒桩插地碰地面层");
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int w = k.AddWeapon(Instant(1f, listen));
                int owner = Unit(k, CombatFaction.Player, new double2(0, 0), kind: CombatUnitKind.Turret);
                int roam = k.AddDroneForTests(new double2(1.5, 0), owner, CombatFaction.Player, false, 30f, w);
                int post = k.AddDroneForTests(new double2(12, 0), owner, CombatFaction.Player, true, 30f, w);
                k.TryGetDrone(roam, out CombatDrone rd);
                k.TryGetDrone(post, out CombatDrone pd);
                k.SpawnField(Corona, 0, 0, 5f, 1f, owner, CombatFaction.Player);
                var log = new EdgeLog();
                EdgeRun(k, 30, log); // 两个场错开 0.5 秒：同一步的多条事件夹具只取得到最后一条
                k.SpawnField(Pull, 12, 0, 3f, 1f, owner, CombatFaction.Player);
                EdgeRun(k, 20, log);
                uint rm = DroneMask(k, roam), pm = DroneMask(k, post);
                EdgeRun(k, 60, log);
                EmgEvent re = log.Enter.FirstOrDefault(e => e.Ent == rd.Id), pe = log.Enter.FirstOrDefault(e => e.Ent == pd.Id);
                Expect(log.EnterOf(EmgEntKind.Drone, rd.Id) == 1 && log.EnterOf(EmgEntKind.Drone, pd.Id) == 1 && log.LeaveOf(EmgEntKind.Drone, rd.Id) == 1 && log.LeaveOf(EmgEntKind.Drone, pd.Id) == 1
                       && re.Code == Corona && pe.Code == Pull && re.OtherKind == EmgEntKind.Field && log.EnterOf(EmgEntKind.Unit) == 0
                       && rm == EmgFieldConst.FieldBit(Corona) && pm == EmgFieldConst.FieldBit(Pull) && (DroneMask(k, roam) | DroneMask(k, post)) == 0u,
                    $"游走无人机在电晕场里、哨戒桩在牵引场里：各 enter 1 条（Code {re.Code} / {pe.Code}，宿主种类 = 无人机）、场到期各 leave 1 条；场内掩码 {rm:X} / {pm:X}，到期清零；母机没有程序不发");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int w = k.AddWeapon(Instant(1f, listen));
                int owner = Unit(k, CombatFaction.Player, new double2(0, 0), kind: CombatUnitKind.Turret);
                int roam = k.AddDroneForTests(new double2(1.5, 0), owner, CombatFaction.Player, false, 30f, w);
                int post = k.AddDroneForTests(new double2(12, 0), owner, CombatFaction.Player, true, 30f, w);
                k.TryGetDrone(roam, out CombatDrone rd);
                k.TryGetDrone(post, out CombatDrone pd);
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 0), 4f, 1f, 1.5f);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 4f, 1f, 1.5f); // 地面层：游走无人机不碰
                var log = new EdgeLog();
                EdgeRun(k, 45, log); // 两处斑块错开 0.75 秒（同一步多条事件夹具只取得到最后一条）
                k.SpawnPatch(EmgMatterConst.Oil, new double2(12, 0), 2f, 1f, 1.5f);
                EdgeRun(k, 15, log);
                uint rm = DroneMask(k, roam), pm = DroneMask(k, post);
                EdgeRun(k, 300, log);
                List<EmgEvent> rEnter = log.Enter.Where(e => e.EntKind == EmgEntKind.Drone && e.Ent == rd.Id).ToList();
                List<EmgEvent> pEnter = log.Enter.Where(e => e.EntKind == EmgEntKind.Drone && e.Ent == pd.Id).ToList();
                Expect(rEnter.Count == 1 && rEnter[0].Code == EmgMatterConst.Smoke && rEnter[0].OtherKind == EmgEntKind.Patch && pEnter.Count == 1 && pEnter[0].Code == EmgMatterConst.Oil
                       && rm == 1u << EmgMatterConst.Smoke && pm == 1u << EmgMatterConst.Oil
                       && log.LeaveOf(EmgEntKind.Drone, rd.Id) == 1 && log.LeaveOf(EmgEntKind.Drone, pd.Id) == 1 && (DroneMask(k, roam) | DroneMask(k, post)) == 0u,
                    $"斑块：游走无人机只进烟（Code {rEnter.FirstOrDefault().Code}，脚下的油不算，掩码 {rm:X}）、哨戒桩插地进油（Code {pEnter.FirstOrDefault().Code}，掩码 {pm:X}）；斑块消失各 leave 1 条、掩码清零");
            }
            // 修复轮 1（审查 P1）：酸沼是地面层虚拟世界源（不算进 PatchLive）——只有酸沼时哨戒桩照样进；别处斑块生成又消失，不重复进出
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int w = k.AddWeapon(Instant(1f, listen));
                int owner = Unit(k, CombatFaction.Player, new double2(-30, 0), kind: CombatUnitKind.Turret);
                int post = k.AddDroneForTests(new double2(12, 0), owner, CombatFaction.Player, true, 30f, w);
                k.TryGetDrone(post, out CombatDrone pd);
                k.SetAcidMarsh(new[] { new float4(12f, 0f, 0f, 3f) }, 0.02f, 1f);
                var log = new EdgeLog();
                EdgeRun(k, 15, log);
                uint pm = DroneMask(k, post);
                long e0 = log.EnterRaw;
                k.SpawnPatch(EmgMatterConst.Smoke, new double2(-10, 0), 2f, 1f, 1f);
                EdgeRun(k, 180, log);
                Expect(e0 == 1 && log.EnterRaw == 1 && log.Enter.Count == 1 && log.Enter[0].Ent == pd.Id && log.Enter[0].Code == EmgMatterConst.Acid && log.LeaveRaw == 0
                       && pm == 1u << EmgMatterConst.Acid && DroneMask(k, post) == 1u << EmgMatterConst.Acid,
                    $"只有酸沼、没有斑块：插在酸沼里的哨戒桩 enter {e0} 条（Code {log.Enter.FirstOrDefault().Code}，掩码 {pm:X}）；别处烟斑生成又消失后 enter 共 {log.EnterRaw}、leave {log.LeaveRaw}，掩码仍是酸 {DroneMask(k, post):X}");
            }
        }

        // ── D. 物质位与场位互不覆盖 ─────────────────────────────────────────────

        private static void CheckEdgeBits()
        {
            Line("  · D. 同一单位 / 弹体同时在斑块和场里：物质位与场位互不覆盖（进斑块不清场位、出场不清物质位，反之亦然）");
            (EdgeLog log, uint both, uint after) Stand(float patchSec, float fieldSec)
            {
                using CombatKernel k = NewKernel();
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, listen)), kind: CombatUnitKind.Turret);
                k.SpawnPatch(EmgMatterConst.Acid, new double2(0, 0), 2f, 1f, patchSec);
                k.SpawnField(Corona, 0, 0, 3f, fieldSec, me, CombatFaction.Player);
                var log = new EdgeLog();
                EdgeRun(k, 30, log);
                uint both = UnitMask(k, me);
                EdgeRun(k, 90, log);
                return (log, both, UnitMask(k, me));
            }
            uint acid = 1u << EmgMatterConst.Acid, corona = EmgFieldConst.FieldBit(Corona);
            var fieldFirst = Stand(10f, 1f);
            var patchFirst = Stand(1f, 10f);
            Expect(fieldFirst.both == (acid | corona) && fieldFirst.after == acid && fieldFirst.log.EnterRaw == 2 && fieldFirst.log.LeaveRaw == 1 && fieldFirst.log.Leave[0].Code == Corona,
                $"场先到期：两者都在时掩码 {fieldFirst.both:X}（酸位 + 电晕位）；出场只发电晕 leave（Code {fieldFirst.log.Leave.FirstOrDefault().Code}），物质位保留 {fieldFirst.after:X}");
            Expect(patchFirst.both == (acid | corona) && patchFirst.after == corona && patchFirst.log.EnterRaw == 2 && patchFirst.log.LeaveRaw == 1 && patchFirst.log.Leave[0].Code == EmgMatterConst.Acid,
                $"斑块先到期：出斑块只发酸 leave（Code {patchFirst.log.Leave.FirstOrDefault().Code}），场位保留 {patchFirst.after:X}（最后一块斑块消失的清掩码只清物质位）");

            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(EdgeShot(listen, 10f, 3f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), hp: 50000f);
                k.SpawnField(Corona, 0, 10, 5f, 10f, me, CombatFaction.Player);
                k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 10), 1.5f, 1f, 30f);
                k.FireAt(me, foe, k.Time);
                var log = new EdgeLog();
                uint mid = 0u;
                for (int s = 0; s < 150; s++)
                {
                    EdgeRun(k, 1, log);
                    if (s == 75 && k.TryGetProjectile(0, out CombatProjectile p))
                    {
                        mid = p.PrevMatterMask; // 约 y = 12.6：已出油、仍在电晕场里
                    }
                }
                ushort[] enters = log.Enter.Where(e => e.EntKind == EmgEntKind.Projectile).Select(e => e.Code).ToArray();
                ushort[] leaves = log.Leave.Where(e => e.EntKind == EmgEntKind.Projectile).Select(e => e.Code).ToArray();
                Expect(enters.SequenceEqual(new ushort[] { Corona, EmgMatterConst.Oil }) && leaves.SequenceEqual(new ushort[] { EmgMatterConst.Oil, Corona }) && mid == corona,
                    $"弹体先进电晕场再穿过场中的油：enter 顺序 [{string.Join(",", enters)}]、leave 顺序 [{string.Join(",", leaves)}]；出油后仍在场里时掩码 {mid:X}（只剩电晕位，出斑块没清场位）");
            }
        }

        // ── E. 负向 ─────────────────────────────────────────────────────────────

        private static void CheckEdgeNegative()
        {
            Line("  · E. 负向：场在弹体位置刚生成 / 刚消失；弹体在场内被销毁不补 leave；地点没人读 evt.enter / leave 时不入队（掩码照记）");
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(EdgeShot(listen, 3f, 10f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 40), hp: 50000f);
                k.FireAt(me, foe, k.Time);
                var log = new EdgeLog();
                EdgeRun(k, 90, log); // 弹体飞到 y ≈ 4.5（离开火者够远，开火者不在新场里）
                k.TryGetProjectile(0, out CombatProjectile p0);
                k.SpawnField(Pull, p0.Pos.x, p0.Pos.y, 2f, 0.3f, me, CombatFaction.Player);
                EdgeRun(k, 1, log);
                long enterAtSpawn = log.EnterRaw;
                EdgeRun(k, 40, log);
                k.TryGetProjectile(0, out CombatProjectile p1);
                Expect(enterAtSpawn == 1 && log.EnterRaw == 1 && log.LeaveRaw == 1 && log.Leave[0].Code == Pull && log.Leave[0].EntKind == EmgEntKind.Projectile && k.FieldCount == 0
                       && (p1.PrevMatterMask & EmgFieldConst.FieldBits) == 0u && k.ProjectileCount >= 1,
                    $"场在飞行弹体位置生成：下一步 enter {enterAtSpawn} 条；0.3 秒后场在弹体还在里面时消失：leave {log.LeaveRaw} 条（Code {log.Leave.FirstOrDefault().Code}），场位清零，不重复发");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(EdgeShot(listen, 20f, 3f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 10), hp: 50000f);
                k.SpawnField(Corona, 0, 10, 4f, 10f, me, CombatFaction.Player);
                k.FireAt(me, foe, k.Time);
                var log = new EdgeLog();
                EdgeRun(k, 90, log);
                Expect(log.EnterOf(EmgEntKind.Projectile) == 1 && log.LeaveOf(EmgEntKind.Projectile) == 0 && k.ProjectileCount == 0,
                    $"弹体进场后在场内命中、被销毁：enter {log.EnterOf(EmgEntKind.Projectile)} 条、leave {log.LeaveOf(EmgEntKind.Projectile)} 条（不给已销毁实体补 leave），弹体池 {k.ProjectileCount}");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), kind: CombatUnitKind.Turret);
                k.SpawnField(Vortex, 0, 0, 3f, 10f, me, CombatFaction.Player);
                StepN(k, 30);
                Expect(k.EmgEnqueuedOf(EmgEventKind.Enter) == 0 && (UnitMask(k, me) & EmgFieldConst.FieldBits) == EmgFieldConst.FieldBit(Vortex),
                    $"地点没有任何程序读 evt.enter / leave：不入队（{k.EmgEnqueuedOf(EmgEventKind.Enter)} 条，FGR-EMG-062），场位照记（{UnitMask(k, me):X}，以后有人读时不会误发）");
            }
            // 修复轮 1（审查 P2）：单位在敌对偏折场里被压溃（F01 湮灭）：不给已阵亡单位补 leave
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(-50, 0));
                int low = Unit(k, CombatFaction.Hostile, new double2(0, 0), k.AddWeapon(Instant(1f, listen)));
                k.SetHealth(low, 50f, 400f, true); // 12.5%
                k.SpawnField(Deflect, 0, 0, 1.5f, 1f, me, CombatFaction.Player);
                var log = new EdgeLog();
                EdgeRun(k, 90, log);
                Expect(!k.IsAlive(low) && log.LeaveOf(EmgEntKind.Unit, low) == 0 && log.LeaveRaw == 0 && k.FieldCount == 0,
                    $"低完整度敌人在玩家偏折场里被压溃（存活 {k.IsAlive(low)}）：场到期后 leave {log.LeaveRaw} 条（不给已阵亡单位补）");
            }
        }

        // ── F. 存读档 / 暂停 / 倍速 ───────────────────────────────────────────

        private static void CheckEdgeSaveLoad()
        {
            Line("  · F. 存读档：在场内存档读回不重复发 enter、离场照发一次；弹体跨场边界存读档；旧档（无人机段缺新字段，缺省 0）读后第一拍在场内的无人机只发一次 enter；暂停不发；0.5x～3x 次数一致");
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            using (CombatKernel c = NewKernel())
            {
                int listen = EdgeListen(a);
                int w = a.AddWeapon(Instant(1f, listen));
                int me = Unit(a, CombatFaction.Player, new double2(0, 0), w, kind: CombatUnitKind.Turret);
                int post = a.AddDroneForTests(new double2(1, 0), me, CombatFaction.Player, true, 30f, w);
                a.SpawnField(Corona, 0, 0, 3f, 2f, me, CombatFaction.Player);
                var la = new EdgeLog();
                EdgeRun(a, 30, la);
                long aEnter = la.EnterRaw;
                byte[] snap = a.Serialize();
                byte[] old = a.SerializeEmgShapeForTests(EmgConst.SegDrones, 5, 0);
                CombatLoadResult rb = b.Load(snap), rc = c.Load(old);
                uint bm = DroneMask(b, post), cm = DroneMask(c, post);
                var la2 = new EdgeLog();
                var lb = new EdgeLog();
                var lc = new EdgeLog();
                EdgeRun(a, 120, la2);
                EdgeRun(b, 120, lb);
                EdgeRun(c, 120, lc);
                Expect(rb == CombatLoadResult.Ok && aEnter == 2 && bm == EmgFieldConst.FieldBit(Corona) && lb.EnterRaw == 0 && lb.LeaveRaw == 2 && la2.LeaveRaw == 2 && a.StateHash() == b.StateHash(),
                    $"炮塔与哨戒桩在电晕场里（enter {aEnter} 条）时存档 → 读档：无人机场位读回 {bm:X}；续跑不重复发 enter（{lb.EnterRaw}）、场到期 leave {lb.LeaveRaw} 条与不读档的一致（{la2.LeaveRaw}）；状态哈希一致");
                Expect(rc == CombatLoadResult.Ok && cm == 0u && lc.EnterRaw == 1 && lc.Enter.Count == 1 && lc.Enter[0].EntKind == EmgEntKind.Drone && lc.LeaveRaw == 2,
                    $"旧档（无人机段只有 5 个字段，场位缺省 0）：读档后第一拍在场内的哨戒桩只发 1 次 enter（{lc.EnterRaw} 条，宿主 {lc.Enter.FirstOrDefault().EntKind}），单位不重发（单位段带掩码）；到期 leave {lc.LeaveRaw} 条（{rc}）");
            }
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                int listen = EdgeListen(a);
                int me = Unit(a, CombatFaction.Player, new double2(0, 0), a.AddWeapon(EdgeShot(listen, 5f, 6f)));
                int foe = Unit(a, CombatFaction.Hostile, new double2(0, 25), hp: 50000f);
                a.SpawnField(Corona, 0, 5, 2f, 10f, me, CombatFaction.Player);
                a.FireAt(me, foe, a.Time);
                var la = new EdgeLog();
                EdgeRun(a, 48, la); // 约 y = 4：刚进场
                CombatLoadResult rb = b.Load(a.Serialize());
                var la2 = new EdgeLog();
                var lb = new EdgeLog();
                EdgeRun(a, 120, la2);
                EdgeRun(b, 120, lb);
                Expect(rb == CombatLoadResult.Ok && la.EnterRaw == 1 && la2.EnterRaw == 0 && lb.EnterRaw == 0 && la2.LeaveRaw == 1 && lb.LeaveRaw == 1 && a.StateHash() == b.StateHash(),
                    $"弹体跨场边界（进场后、出场前）存读档：读档后不重复 enter（{lb.EnterRaw}）、出场 leave {lb.LeaveRaw} 条与连续跑一致（{la2.LeaveRaw}）；状态哈希一致");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = EdgeListen(k);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, listen)), kind: CombatUnitKind.Turret);
                k.SpawnField(Corona, 0, 0, 3f, 1f, me, CombatFaction.Player);
                var log = new EdgeLog();
                EdgeRun(k, 10, log);
                long e0 = k.EmgEnqueuedOf(EmgEventKind.Enter), l0 = k.EmgEnqueuedOf(EmgEventKind.Leave);
                for (int s = 0; s < 120; s++)
                {
                    k.Step(0f, k.Time);
                }
                bool paused = k.EmgEnqueuedOf(EmgEventKind.Enter) == e0 && k.EmgEnqueuedOf(EmgEventKind.Leave) == l0 && (UnitMask(k, me) & EmgFieldConst.FieldBits) != 0u;
                EdgeRun(k, 80, log);
                Expect(paused && log.EnterRaw == 1 && log.LeaveRaw == 1, $"暂停 120 帧：不发、不清场位（{paused}）；恢复后场到期 leave 1 条（enter {log.EnterRaw} / leave {log.LeaveRaw}）");
            }
            var counts = new List<string>();
            bool same = true;
            foreach (float dt in new[] { 1f / 120f, 1f / 60f, 1f / 20f })
            {
                using CombatKernel k = NewKernel();
                int listen = EdgeListen(k);
                int me = Walker(k, new double2(0, 0), k.AddWeapon(Instant(1f, listen)));
                k.SpawnField(Corona, 7, 0, 2f, 30f, me, CombatFaction.Player);
                k.IssueCommand(me, CombatCommandKind.Move, new double2(14, 0), 0, 0.3f, 0f, 1f, false);
                var log = new EdgeLog();
                EdgeRun(k, (int)math.round(5f / dt), log, dt);
                same &= log.EnterRaw == 1 && log.LeaveRaw == 1;
                counts.Add($"{log.EnterRaw}/{log.LeaveRaw}");
            }
            Expect(same, $"0.5x / 1x / 3x（步长 1/120、1/60、1/20）机器走过电晕场：enter / leave 次数 {string.Join("、", counts)}，都是各 1 次");
        }

        // ── G. 残骸 ─────────────────────────────────────────────────────────────

        private static void CheckEdgeWreck()
        {
            Line("  · G. 残骸结论（ADR-EMG-001 ④ 改写）：没有程序读残骸的进出事件，残骸不发 enter / leave；结构里的物质掩码已删，快照段 10 保留占位，读回后其余字段不错位");
            using CombatKernel a = NewKernel();
            using CombatKernel b = NewKernel();
            int listen = EdgeListen(a);
            Unit(a, CombatFaction.Player, new double2(-30, -30), a.AddWeapon(Instant(1f, listen)), kind: CombatUnitKind.Turret);
            a.TryAddWreck(new EmgWreck { Pos = new double2(0, 0), Born = a.Time, Until = a.Time + 90.0, Radius = 0.5f, Hp = 60f, MaxHp = 60f, Mass = 10f, Temp = 20f, Coat = 5u, SrcKind = 3, Profile = 2 }, out int wid);
            a.SpawnField(Corona, 0, 0, 3f, 1f, 0, CombatFaction.Player);
            a.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 2f, 1f, 1f);
            var log = new EdgeLog();
            EdgeRun(a, 120, log);
            bool noWreck = log.EnterOf(EmgEntKind.Wreck) == 0 && log.LeaveOf(EmgEntKind.Wreck) == 0 && log.EnterRaw == 0 && log.LeaveRaw == 0;
            bool noField = typeof(EmgWreck).GetField("PrevMatterMask") == null;
            CombatLoadResult rb = b.Load(a.Serialize());
            EmgWreck wa = Wreck(a, wid), wb = Wreck(b, wid);
            Expect(noWreck && noField && rb == CombatLoadResult.Ok && wb.Id == wid && wb.Profile == wa.Profile && wb.SrcKind == wa.SrcKind && wb.Coat == wa.Coat && wb.Mass == wa.Mass && a.StateHash() == b.StateHash(),
                $"残骸在场和油里 2 秒：enter / leave 0 条（{noWreck}）；EmgWreck 没有 PrevMatterMask 字段（{noField}）；读档 {rb}，占位之后的档案 {wb.Profile} / 来源种类 {wb.SrcKind} / 沾染 {wb.Coat} 与原值一致，哈希一致");
        }
    }
}
