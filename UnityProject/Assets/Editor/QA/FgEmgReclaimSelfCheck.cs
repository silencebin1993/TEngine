using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG2-RECL-01 回收修复、桥接与蔓延改机制（FG19 FGR-EMG-115、FGR-EMG-230 第 6 / 7 行；FG19A 第 1.2 节第 27 行、第 1.3 节第 35 / 36 行；ADR-EMG-001 ⑦ 第 4 条；
    /// FGT-EMG-037 回收修复拆残骸、FGT-EMG-033 桥接导线传电 / 传热、FGT-EMG-031 蔓延扩散）：起真实内核（CombatSite 同一套表写入、编译器编出的真程序），
    /// 断言拆残骸修复 / 负向矩阵、导线传电传热与液体均分 / 拦索 / 断开 / 到期、液体真扩散、存读档、暂停与 0.5x～3x、旧 Blood 吸血路径清零。
    /// </summary>
    public static class FgEmgReclaimSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const string SiteId = "emg-recl-site";
        private const int Slot = 0;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/回收修复桥接蔓延")]
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
            Line("\n[回收修复 / 桥接 / 蔓延] EMG2-RECL-01：拆残骸修复与负向、导线传电传热 / 液体均分 / 拦索 / 断开、液体真扩散、存读档与倍速、旧吸血清零（FGT-EMG-037 / 033 / 031）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核 Editor 托管调用（真机 IL2CPP + Burst，耗时只作量级参考）");
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
                Step(CheckData);
                Step(CheckReclaim);
                Step(CheckReclaimChild);
                Step(CheckReclaimNegative);
                Step(CheckClaw);
                Step(CheckWreckPoolFull);
                Step(CheckBridge);
                Step(CheckBridgeEdges);
                Step(CheckWireEndpoints);
                Step(CheckSpread);
                Step(CheckSpreadFormal);
                Step(CheckCarriers);
                Step(CheckChildWrites);
                Step(CheckTurret);
                Step(CheckSaveLoadSpeed);
                Step(CheckTexts);
                Step(CheckSitePath);
            }
            catch (Exception e)
            {
                Fail($"回收修复自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [回收修复 / 桥接 / 蔓延] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            CombatSite.ApplyFieldRules(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 400f, float maxHp = 400f, CombatUnitKind kind = CombatUnitKind.Enemy,
            CombatUnitFlags extra = 0, float speed = 0f)
        {
            CombatUnitKind kk = f == CombatFaction.Player && kind == CombatUnitKind.Enemy ? CombatUnitKind.Machine : kind;
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kk, Faction = f, Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | extra, Position = p, Home = p, Radius = 0.5f, Speed = speed, Health = hp, MaxHealth = maxHp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            });
            if (f == CombatFaction.Hostile && kk == CombatUnitKind.Enemy)
            {
                k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
            }
            return id;
        }

        private static CombatWeapon Instant(float damage, int program, CombatCarrier carrier = CombatCarrier.Projectile) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0.1f, Program = program,
            Reading = new CombatReading { Carrier = carrier },
        };

        private static int Prog(CombatKernel k, string fw, FirmwareCarrier carrier = FirmwareCarrier.Projectile)
        {
            EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier);
            return r.IsEmpty ? -1 : CombatSite.RegisterProgram(k, r);
        }

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : float.NaN;

        private static byte Note(CombatKernel k, int id) => k.TryGetUnitEmg(id, out _, out EmgUnitState s) ? s.ReclaimNote : (byte)255;

        private static int AddWreck(CombatKernel k, double2 p, float mass = 10f, byte noSalvage = 0)
        {
            k.TryAddWreck(new EmgWreck { Pos = p, Born = k.Time, Until = k.Time + 90.0, Radius = 0.5f, Hp = 60f, MaxHp = 60f, Mass = mass, Temp = 20f, NoSalvage = noSalvage }, out int id);
            return id;
        }

        private static CombatZone PatchAt(CombatKernel k, double2 p)
        {
            int i = k.PickPatch(p);
            return i >= 0 && k.TryGetPatch(i, out CombatZone z) ? z : default;
        }

        private static bool HoverHas(CombatKernel k, int id, string key)
        {
            var sb = new StringBuilder();
            return EmgPhaseFeedback.AppendLines(k, id, sb) && sb.ToString().Contains(GameText.Get(key));
        }

        private static CombatConfig SiteCfg()
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            return c;
        }

        private static BlueprintCircuitPreview Preview(string comp, params string[] fw)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, comp, null, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fw);
            if (!up.Success || !fw.All(f => p.FirmwareIds.Contains(f)))
            {
                Fail($"测试准备：{comp} 接入 {string.Join("/", fw)} 没全部生效（{up.Message}）");
            }
            return p;
        }

        /// <summary>正式路径的武器：蓝图 → MachineWeaponFrom → WithProgram（与机器 / 炮塔 / 试验场同一条）。</summary>
        private static CombatWeapon Formal(CombatSite site, string comp, params string[] fw)
        {
            BlueprintCircuitPreview p = Preview(comp, fw);
            return site.WithProgram(p, CombatSite.MachineWeaponFrom(p), out _, out _);
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

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            CombatEvent[] buf = k.DrainGameplay(4096, out int n);
            return buf.Take(n).ToList();
        }

        // ── A. 数据与单轨 ─────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：三条固件阶段 2 切声明轨、根节点由内核执行（RootKernel）；Blood 只是标记、全部固件 × 5 载体不再产生吸血；Woven 撤下");
            string[] fws = { "fw_reclaim", "fw_bridge", "fw_spread" };
            var off = fws.Where(f => !EmgCatalog.IsOwnerOnDeclTrack(f)).ToList();
            Expect(off.Count == 0 && !EmgCatalog.IsPendingPort("ent.wreck", EmgRole.Modify) && !EmgCatalog.IsPendingPort("mat.water", EmgRole.Modify),
                $"ent.wreck 与物质 modify 不再是未接通端口；回收修复 / 桥接 / 蔓延全部走声明轨{(off.Count > 0 ? "；未切：" + string.Join("、", off) : string.Empty)}");
            bool Has(string fw, EmgVerb verb)
            {
                EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, FirmwareCarrier.Projectile);
                return r.KernelInstrs.Any(x => x.Verb == (byte)verb && (x.Flags & EmgInstrFlags.RootKernel) != 0);
            }
            Expect(Has("fw_reclaim", EmgVerb.Salvage) && Has("fw_bridge", EmgVerb.Link) && Has("fw_spread", EmgVerb.Spread),
                "编译结果：回收修复 = 根节点 salvage、桥接 = link、蔓延 = spread，三者都带 RootKernel（由内核在 evt.kill / evt.hit 处执行）");
            EmgProgramResult br = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_bridge" }, FirmwareCarrier.Projectile);
            Func<string, string, bool> skip = EmgProgramCompiler.LegacySkip(br);
            EmgProgramResult sr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_spread" }, FirmwareCarrier.Projectile);
            Func<string, string, bool> skip2 = EmgProgramCompiler.LegacySkip(sr);
            Expect(skip != null && skip("fw_bridge", "weave") && skip2 != null && skip2("fw_spread", "scale") && skip2("fw_spread", "linger"),
                "单轨：桥接的旧减速网（weave）、蔓延的一次放大 1.2 倍（scale）与旧驻留区（linger）不再结算（FGR-EMG-019）");
            ushort waterPort = EmgCatalog.Current.Ports[EmgCatalog.Current.PortIndexById["mat.water"]].Code;
            EmgInstr[] media = sr.KernelInstrs.Where(x => x.Port == waterPort && x.Role == (byte)EmgRole.Emit).ToArray();
            EmgInstr coolant = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_coolant" }, FirmwareCarrier.Projectile).KernelInstrs.First(x => x.Role == (byte)EmgRole.Emit);
            EmgInstr[] meleeMedia = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_spread" }, FirmwareCarrier.Melee).KernelInstrs.Where(x => x.Port == waterPort && x.Role == (byte)EmgRole.Emit).ToArray();
            Expect(media.Length == 1 && (media[0].Flags & EmgInstrFlags.RootKernel) != 0 && Mathf.Approximately(media[0].A, 1f) && Mathf.Approximately(media[0].Amount, 0.5f)
                   && (coolant.Flags & EmgInstrFlags.RootKernel) == 0 && meleeMedia.Length == 1 && meleeMedia[0].A == 0f,
                $"蔓延的介质排放（声明同时改写地面物质）由内核排真水 1 米 0.5 份；只排放的旧固件（冷却液）不受影响、照旧迁入；格斗读法不留水（A = {(meleeMedia.Length > 0 ? meleeMedia[0].A : -1f)}）");
            bool blood = CarrierReadings.TryGetTagEffect("Blood", out _, out string eff, out float amt);
            Expect(blood && eff == "none" && amt == 0f, $"Blood 标签效果 = {eff}（{amt}）：只是“可回收残骸”标记，不再吸血");
            string[] prims = { "comp_gun", "comp_claw", "comp_dronebay", "comp_corona", "comp_sprayer" };
            var leak = new List<string>();
            foreach (var row in ConfigSystem.Instance.Tables.TbFirmwareKind.DataList)
            {
                foreach (string p in prims)
                {
                    CombatReading rd = CarrierReadings.Build(p, null, "chassis_wheel", new[] { row.Id }, false);
                    if (rd.Lifesteal > 0f)
                    {
                        leak.Add(row.Id + "@" + p);
                    }
                }
            }
            Expect(leak.Count == 0, $"负向审计：全部 {ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.Count} 条固件 × 5 种主组件，旧吸血读数 Lifesteal 恒 0{(leak.Count > 0 ? "；仍有：" + string.Join("、", leak.Take(6)) : string.Empty)}");
            Expect(FgEmgTextBehaviorSelfCheck.RetiredEngineTags.Contains("Woven") && !StatusTagCatalog.TryGet("Woven", out _), "Woven（连网）记号从标签表撤下，登记为已撤下的旧引擎标记");
        }

        // ── B. 回收修复正式路径 ─────────────────────────────────────────────────

        private static void CheckReclaim()
        {
            Line("  · B. 回收修复：击毁 → 就地拆解本次击杀的残骸（掩体消失）→ 按质量修复本机，≤ 15% 最大耐久；不再按伤害回血");
            using CombatKernel k = NewKernel();
            int pi = Prog(k, "fw_reclaim");
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 200f, 400f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
            k.FireAt(me, foe, k.Time);
            k.Step(Dt, k.Time + Dt);
            List<CombatEvent> ev = Drain(k);
            CombatEvent sal = ev.FirstOrDefault(e => e.Kind == CombatEventKind.Salvaged);
            float hp1 = Hp(k, me);
            Expect(pi >= 0 && k.WreckCount == 0 && k.EmgScalars.Salvages == 1 && sal.Kind == CombatEventKind.Salvaged && hp1 > 200f && hp1 - 200f <= 60.001f
                   && Mathf.Abs(hp1 - 200f - Mathf.Min(sal.Value2 * 5f, 60f)) < 0.01f && Note(k, me) == EmgReclaimConst.NoteRepaired,
                $"击毁后残骸 {k.WreckCount} 块（被拆）、拆解 {k.EmgScalars.Salvages} 次；残骸质量 {sal.Value2:0.##} × 5 → 修复 {hp1 - 200f:0.##}（上限 60 = 400 × 15%），事件维修量 {sal.Value:0.##}");
            int tank = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f, 5000f);
            float before = Hp(k, me);
            k.FireAt(me, tank, k.Time + 1.0);
            k.Step(Dt, k.Time + Dt);
            Expect(Mathf.Abs(Hp(k, me) - before) < 1e-4f, $"取代按伤害回血：打中不击毁的目标 50 伤害，本机血量 {before:0.##} → {Hp(k, me):0.##}（不变）");
        }

        /// <summary>B2. 子节点读 evt.kill（锈蚀回收弹，FG19A 混合固件第 28 行）：子弹药击毁的目标就地拆残骸修复母机；母弹自身打不死目标，不读击杀。</summary>
        private static void CheckReclaimChild()
        {
            Line("  · B2. 锈蚀回收弹：子弹药击毁 → 拆残骸修母机（子节点 salvage 由内核执行，不是静默失效）；同场景只有子母弹没有回收 → 不修复");
            (int salvages, float healed, int wrecks) Run(string fw)
            {
                using CombatKernel k = NewKernel();
                int pi = Prog(k, fw);
                CombatWeapon w = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = pi,
                    SpawnFlight = 1, ProjectileSpeed = 30f, ProjectileRadius = 0.15f, ProjectileLife = 1.2f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 200f, 400f);
                int tank = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f, 5000f);
                // 碎靶都在母弹弹道之外（x ≠ 0），只有母弹到点放出的子弹药够得着
                foreach (int deg in new[] { 0, 45, 135, 180, 225, 315, 60, 120 })
                {
                    double a = math.radians((double)deg);
                    Unit(k, CombatFaction.Hostile, new double2(math.cos(a) * 1.6, 5 + math.sin(a) * 1.6), -1, 1f, 1f);
                }
                k.FireAt(me, tank, k.Time);
                StepN(k, 120);
                return (k.EmgScalars.Salvages, Hp(k, me) - 200f, k.WreckCount);
            }
            (int salvages, float healed, int wrecks) mix = Run("fw_mix_rustharvest");
            (int salvages, float healed, int wrecks) plain = Run("fw_cluster");
            EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_mix_rustharvest" }, FirmwareCarrier.Projectile);
            bool childRow = r.KernelInstrs.Any(x => x.Verb == (byte)EmgVerb.Salvage) && r.Nodes.Count >= 2;
            Expect(childRow && mix.salvages > 0 && mix.healed > 0f && plain.salvages == 0 && plain.healed == 0f && plain.wrecks > 0,
                $"锈蚀回收弹拆残骸 {mix.salvages} 次、母机修复 {mix.healed:0.##}（子节点有 salvage 行 {childRow}）；对照只有子母弹：拆 {plain.salvages} 次、修复 {plain.healed:0.##}、残骸留下 {plain.wrecks} 块");
        }

        // ── C. 负向矩阵 ─────────────────────────────────────────────────────────

        private static void CheckReclaimNegative()
        {
            Line("  · C. 负向：满耐久不拆（残骸留作掩体）/ 每秒最多一次 / 没有残骸写原因 / 同一残骸只拆一次 / 己方残骸不可回收 / 外部血量单位走 HealRequest 不被同步冲掉");
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 400f, 400f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                Expect(k.WreckCount == 1 && Note(k, me) == EmgReclaimConst.NoteFull && HoverHas(k, me, "emg.reclaim.note.full"), $"满耐久：不拆，残骸 {k.WreckCount} 块留作掩体，悬停写“{GameText.Get("emg.reclaim.note.full")}”");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 100f, 400f);
                int f1 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
                int f2 = Unit(k, CombatFaction.Hostile, new double2(2, 5), -1, 10f, 10f);
                int f3 = Unit(k, CombatFaction.Hostile, new double2(-2, 5), -1, 10f, 10f);
                k.FireAt(me, f1, k.Time);
                k.Step(Dt, k.Time + Dt);
                float h1 = Hp(k, me);
                k.FireAt(me, f2, k.Time + 0.2);
                k.Step(Dt, k.Time + Dt);
                bool cd = k.WreckCount == 1 && Note(k, me) == EmgReclaimConst.NoteCooldown && Mathf.Abs(Hp(k, me) - h1) < 1e-4f && HoverHas(k, me, "emg.reclaim.note.cooldown");
                StepN(k, 70);
                k.FireAt(me, f3, k.Time);
                k.Step(Dt, k.Time + Dt);
                Expect(cd && k.EmgScalars.Salvages == 2 && Hp(k, me) > h1 && k.WreckCount == 1,
                    $"每秒最多一次（按模拟时钟）：0.2 秒后第二次击毁不拆（残骸保留、原因码冷却）；1 秒后第三次照常拆（累计 {k.EmgScalars.Salvages} 次）");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 100f, 400f);
                int st = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f, CombatUnitKind.Structure);
                k.FireAt(me, st, k.Time);
                k.Step(Dt, k.Time + Dt);
                Expect(k.WreckCount == 0 && Mathf.Abs(Hp(k, me) - 100f) < 1e-4f && Note(k, me) == EmgReclaimConst.NoteNoWreck
                       && HoverHas(k, me, "emg.reclaim.note.no_wreck"),
                    "击毁建筑（不留残骸）：不修复，悬停写“没有残骸”的原因");
                int a = Unit(k, CombatFaction.Player, new double2(5, 0), -1, 100f, 400f);
                int b = Unit(k, CombatFaction.Player, new double2(6, 0), -1, 100f, 400f);
                int w = AddWreck(k, new double2(5.5, 3));
                float ra = k.SalvageForTests(a, w, true, false);
                float rb = k.SalvageForTests(b, w, true, false);
                Expect(ra > 0f && rb < 0f && k.WreckCount == 0 && Note(k, b) == EmgReclaimConst.NoteNoWreck,
                    $"两台机器拆同一块残骸只算一次：先拆的修 {ra:0.##}，后拆的没拆到（{rb}，原因“没有残骸”）；残骸被拆后掩体随之消失（残骸 {k.WreckCount}）");
                int own = AddWreck(k, new double2(8, 3), 10f, 1);
                float rc = k.SalvageForTests(a, own, true, false);
                StepN(k, 70);
                Expect(rc < 0f && k.WreckCount == 1 && Note(k, a) == EmgReclaimConst.NoteNoSalvage && HoverHas(k, a, "emg.reclaim.note.no_salvage"), "己方机器残骸（NoSalvage，ADR ⑦ T5）不可拆解回收、保留，悬停写原因");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 100f, 400f, CombatUnitKind.Machine, CombatUnitFlags.ExternalHealth);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                CombatEvent hr = Drain(k).FirstOrDefault(e => e.Kind == CombatEventKind.HealRequest);
                bool kernelUntouched = Mathf.Abs(Hp(k, me) - 100f) < 1e-4f;
                // 热更层：MachineRegistry 记录是血量真相，修复后 SyncMachineHealth（SetHealth）写回镜像
                MachineRegistry.ResetForNewCampaign();
                MachineOpResult rr = MachineRegistry.SpawnMachine("chassis_wheel", "bp-recl", "emg-recl", Vector2.zero, 100f, 400f);
                float got = MachineRegistry.ApplyRepair(rr.LogicId, hr.Value);
                MachineRegistry.TryGetRecord(rr.LogicId, out MachineRecord rec);
                k.SetHealth(me, rec.Health, rec.MaxHealth, rec.IsAlive);
                StepN(k, 5);
                k.SetHealth(me, rec.Health, rec.MaxHealth, rec.IsAlive); // 下一次同步：记录里已经是修复后的血量，不会冲掉
                float cap = MachineRegistry.ApplyRepair(rr.LogicId, 10000f);
                MachineRegistry.TryGetRecord(rr.LogicId, out MachineRecord rec2);
                MachineRegistry.MarkDeadByLogicId(rr.LogicId);
                float dead = MachineRegistry.ApplyRepair(rr.LogicId, 50f);
                MachineRegistry.ResetForNewCampaign();
                Expect(hr.Kind == CombatEventKind.HealRequest && hr.Value > 0f && kernelUntouched && Mathf.Abs(got - hr.Value) < 1e-4f && Mathf.Abs(Hp(k, me) - (100f + hr.Value)) < 1e-3f
                       && Mathf.Abs(rec2.Health - 400f) < 1e-3f && cap > 0f && dead == 0f,
                    $"外部血量单位：内核只发 HealRequest（{hr.Value:0.##}）、不自己加血；热更层改记录后同步 → 镜像 {Hp(k, me):0.##}，再同步不被冲掉；修复封顶最大耐久、阵亡不复活");
            }
        }

        // ── D. 桥接导线 ─────────────────────────────────────────────────────────

        private static void CheckBridge()
        {
            Line("  · D. 桥接：结算点（目标脚下水洼）与 5 米内最近的导体拉 3 秒导线——电沿它传到另一片水洼里的单位、热沿它传、同类液体均分；对照：没导线传不过去");
            (int wires, float amtA, float amtB, float chargeB, float unitCharge) Run(bool bridge)
            {
                using CombatKernel k = NewKernel();
                int pi = bridge ? Prog(k, "fw_bridge") : -1;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                int far = Unit(k, CombatFaction.Hostile, new double2(0, 9.6));
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 6), 1f, 1f, 60f);
                k.SpawnPatch(EmgMatterConst.Water, new double2(0, 9.6), 1.5f, 3f, 60f);
                k.Step(Dt, k.Time + Dt);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int w = k.WireCount;
                StepN(k, 16); // 至少一个传导节拍：液体均分
                CombatZone a = PatchAt(k, new double2(0, 6)), b = PatchAt(k, new double2(0, 9.6));
                float before = k.AccumOf(far, EmgPhaseConst.Charge);
                k.InjectPatchEnergy(new double2(0, 6), 0.5f, EmgConst.PortElec, 4f, 1);
                float peak = 0f, cb = 0f;
                for (int s = 0; s < 16; s++)
                {
                    StepN(k, 1);
                    peak = Mathf.Max(peak, k.AccumOf(far, EmgPhaseConst.Charge) - before); // 电荷进麻痹会被结算清掉，取注电后的峰值增量
                    k.TryGetPatchEnergy(PatchAt(k, new double2(0, 9.6)).EmgId, out _, out float c, out _);
                    cb = Mathf.Max(cb, c);
                }
                return (w, a.Amount, b.Amount, cb, peak);
            }
            var on = Run(true);
            var off = Run(false);
            Expect(on.wires == 1 && Mathf.Abs(on.amtA - on.amtB) < 0.01f && Mathf.Abs(on.amtA - 2f) < 0.05f && off.wires == 0 && Mathf.Abs(off.amtA - off.amtB) > 1f,
                $"导线 {on.wires} 条：两片水洼份量 {on.amtA:0.##} / {on.amtB:0.##}（均分，原 1 / 3）；对照没导线 {off.amtA:0.##} / {off.amtB:0.##}");
            Expect(on.chargeB > 0f && off.chargeB <= 0f && on.unitCharge > off.unitCharge + 0.3f, // 对照里单位那一点电荷来自目标进麻痹时的放电电弧（EMG2-PHS-01，与导线无关）
                $"传电：第一片注电，第二片水洼电荷峰值 {on.chargeB:0.##}、站在里面的单位电荷峰值增量 {on.unitCharge:0.##}；对照没导线 {off.chargeB:0.##} / {off.unitCharge:0.##}（FGT-EMG-033 导线部分）");
            // EMG2-REACT-02（DEBT-EMG2RECL01-03）：迁入斑块（冷却液液洼）也是导线端点，同类液体沿导线均分
            (int wires, float amtA, float amtB, float chargeB) Legacy(bool bridge)
            {
                using CombatKernel k = NewKernel();
                int pi = bridge ? Prog(k, "fw_bridge") : -1;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                k.SpawnReadingZoneForTests(CombatFaction.Player, new double2(0, 6), 1f, 60f, 0f, NamedReactionCatalog.BitOf("Wet")); // 迁入：冷却液，1 份
                k.SpawnPatch(EmgMatterConst.Coolant, new double2(0, 9.6), 1.5f, 3f, 60f);
                k.Step(Dt, k.Time + Dt);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int w = k.WireCount;
                StepN(k, 16);
                CombatZone a = default, b = PatchAt(k, new double2(0, 9.6));
                for (int s = 0; s < k.PatchSlots; s++)
                {
                    if (k.TryGetPatch(s, out CombatZone z) && (z.PFlags & EmgMatterConst.FlagLegacy) != 0)
                    {
                        a = z; // PickPatch 只挑普通物质斑块（悬停读数），这里直接按迁入标记找
                    }
                }
                k.InjectPatchEnergy(new double2(0, 6), 0.5f, EmgConst.PortElec, 4f, 1);
                float cb = 0f;
                for (int s = 0; s < 16; s++)
                {
                    StepN(k, 1);
                    k.TryGetPatchEnergy(PatchAt(k, new double2(0, 9.6)).EmgId, out _, out float c, out _);
                    cb = Mathf.Max(cb, c);
                }
                return (w, a.Amount, b.Amount, cb);
            }
            var lOn = Legacy(true);
            var lOff = Legacy(false);
            Expect(lOn.wires == 1 && Mathf.Abs(lOn.amtA - lOn.amtB) < 0.01f && lOn.chargeB > 0f && lOff.wires == 0 && Mathf.Abs(lOff.amtA - lOff.amtB) > 1f && lOff.chargeB <= 0f,
                $"[冷却液, 桥接]：迁入的冷却液液洼当导线端点（导线 {lOn.wires} 条），同类液体均分 {lOn.amtA:0.##} / {lOn.amtB:0.##}，电沿导线传到另一片（{lOn.chargeB:0.##}）；对照没导线 {lOff.amtA:0.##} / {lOff.amtB:0.##}、电荷 {lOff.chargeB:0.##}");
            // 传热：目标不站在斑块里（A = 单位），B = 明火斑块 → 单位结构积热上升
            float Heat(bool bridge)
            {
                using CombatKernel k = NewKernel();
                int pi = bridge ? Prog(k, "fw_bridge") : -1;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                int wk = AddWreck(k, new double2(0, 9), 10f);
                k.Step(Dt, k.Time + Dt);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                // 残骸被烧热（直接写温度：模拟旁边明火把它烧到 600 度）
                int wi = k.WreckIndexOf(wk);
                k.TryGetWreck(wi, out EmgWreck w0);
                w0.Temp = 600f;
                k.SetWreckForTests(wi, w0);
                StepN(k, 32);
                return k.AccumOf(foe, EmgPhaseConst.Overheat);
            }
            float hOn = Heat(true), hOff = Heat(false);
            Expect(hOn > 0f && hOff <= 0f, $"传热：目标与 600 度的残骸之间有导线，目标结构积热 {hOn:0.##}；对照没导线 {hOff:0.##}");
        }

        private static void CheckBridgeEdges()
        {
            Line("  · E. 导线：3 秒到期传导图恢复 / 一端消失立即断开 / 拦索减速 35%（路线不变）/ 5 米外没有导体不拉 / 满 32 条不生效计数");
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_bridge");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                AddWreck(k, new double2(0, 9), 10f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int w0 = k.WireCount;
                StepN(k, (int)(2.7f / Dt));
                int wMid = k.WireCount;
                StepN(k, (int)(0.5f / Dt));
                Expect(w0 == 1 && wMid == 1 && k.WireCount == 0 && k.EmgScalars.WiresExpired == 1, $"到期：0 秒 {w0} 条、2.7 秒 {wMid} 条、3.2 秒 {k.WireCount} 条（移除，传导图恢复）");
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int w1 = k.WireCount;
                k.Kill(foe, me);
                StepN(k, 1);
                Expect(w1 == 1 && k.WireCount == 0 && k.EmgScalars.WiresCut == 1, "一端（目标单位）阵亡：下一步导线立即断开（计数 WiresCut）");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_bridge");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                AddWreck(k, new double2(0, 11.6), 10f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                Expect(k.WireCount == 0, "5 米内没有导体（残骸在 5.1 米外）：不拉导线");
            }
            float Travel(bool wire)
            {
                using CombatKernel k = NewKernel();
                int pi = wire ? Prog(k, "fw_bridge") : -1;
                int me = Unit(k, CombatFaction.Player, new double2(-3, 6), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(-3, 12));
                AddWreck(k, new double2(-3, 16), 10f);
                int walker = Unit(k, CombatFaction.Player, new double2(-9.5, 14), -1, 400f, 400f, CombatUnitKind.Machine, 0, 4f); // 开火时在目标 5 米外：B 端是残骸，它才是“穿过”的第三方（端点不减速，见 E2）
                k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                k.IssueCommand(walker, CombatCommandKind.Move, new double2(-0.5, 14), 0, 0.2f, 0f, 0f, false);
                int steps = 0;
                while (steps < 600 && k.TryGetUnit(walker, out CombatUnitView v) && math.distance(v.Position, new double2(-0.5, 14)) > 0.25)
                {
                    StepN(k, 1);
                    steps++;
                }
                return steps * Dt;
            }
            float open = Travel(false), roped = Travel(true);
            Expect(roped > open * 1.05f && roped < open * 1.6f, $"拦索：横穿导线的单位 {roped:0.00} 秒，对照没导线 {open:0.00} 秒（穿过段减速 35%）");
        }

        // ── F. 蔓延 ─────────────────────────────────────────────────────────────

        private static void CheckSpread()
        {
            Line("  · F. 蔓延：命中处 2 米内的水洼 12 秒内扩到 2 倍半径（份量不变，逐节拍推进）；远处与非液体不扩；不再一次放大 1.2 倍");
            using CombatKernel k = NewKernel();
            int pi = Prog(k, "fw_spread");
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
            k.SpawnPatch(EmgMatterConst.Sludge, new double2(2.5, 6), 1f, 2f, 60f); // 污泥：蔓延现在真排水，油遇水会触发上浮规则，这段只看扩散本身
            k.SpawnPatch(EmgMatterConst.Sludge, new double2(10, 6), 1f, 2f, 60f);
            k.Step(Dt, k.Time + Dt);
            k.FireAt(me, foe, k.Time);
            StepN(k, 2);
            float r0 = PatchAt(k, new double2(2.5, 6)).Radius;
            StepN(k, (int)(6f / Dt));
            float r6 = PatchAt(k, new double2(2.5, 6)).Radius;
            StepN(k, (int)(6.5f / Dt));
            CombatZone near = PatchAt(k, new double2(2.5, 6)), far = PatchAt(k, new double2(10, 6));
            Expect(r0 < 1.2f && r6 > 1.3f && r6 < 1.8f && near.Radius > 1.95f && near.Radius <= 2.001f && Mathf.Abs(near.Amount - 2f) < 1e-3f && Mathf.Abs(far.Radius - 1f) < 1e-3f
                   && k.EmgScalars.SpreadStarts >= 1,
                $"2 米内污泥斑块半径 {r0:0.00} → 6 秒 {r6:0.00} → 12.5 秒 {near.Radius:0.00}（2 倍），份量 {near.Amount:0.##} 不变；10 米外的 {far.Radius:0.00} 不扩");
        }

        /// <summary>F2. 蔓延走正式路径（WithProgram）：只装蔓延开火，真水变大；迁入斑块同样扩；持续开火不超 2 倍；存续不够时提前走完、快干的不再扩、半径不跳变。</summary>
        private static void CheckSpreadFormal()
        {
            Line("  · F2. 蔓延（正式路径）：只装蔓延开火 → 真水 1 米 → 随时间变大；[冷却液, 蔓延] 的迁入液洼也扩；持续开火不超 2 倍；快干的不再扩、半径不跳变");
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, ComponentCatalog.CompGunId, "fw_spread");
                CombatWeapon plain = Formal(site, ComponentCatalog.CompGunId);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                CombatFireResult fired = k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                CombatZone water = Patches(k).FirstOrDefault(z => z.Matter == EmgMatterConst.Water);
                float r0 = water.Radius;
                float rMax = r0;
                bool grew = false;
                for (int s = 0; s < (int)(4.2f / Dt); s++)
                {
                    StepN(k, 1);
                    CombatZone z = Patches(k).FirstOrDefault(p => p.EmgId == water.EmgId);
                    grew |= z.Radius > 1.5f;
                    rMax = Mathf.Max(rMax, z.Radius);
                }
                Expect(fired == CombatFireResult.Ok && water.Matter == EmgMatterConst.Water && (water.PFlags & EmgMatterConst.FlagLegacy) == 0 && Mathf.Abs(water.Amount - 0.5f) < 1e-3f
                       && (water.PFlags & EmgMatterConst.FlagSpread) != 0 && grew && rMax <= 2.001f && w.Reading.ZoneRadius == 0f && w.Reading.BlastRadius == plain.Reading.BlastRadius && k.LegacyPatchCount == 0,
                    $"只装蔓延开火：真水（非迁入）0.5 份，半径 {r0:0.00} → 最大 {rMax:0.00}（≤ 2 倍，存续 6 秒内走完）；旧驻留区半径 {w.Reading.ZoneRadius}、旧放大溅射 {w.Reading.BlastRadius}（与不装相同）");
            }
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, ComponentCatalog.CompGunId, "fw_coolant", "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 3);
                CombatZone first = Patches(k).FirstOrDefault(z => (z.PFlags & EmgMatterConst.FlagLegacy) != 0);
                float r0 = first.Radius;
                StepN(k, 90); // 第二发才碰到第一发留下的液洼（读法驻留在命中结算之后落下）
                k.FireAt(me, foe, k.Time);
                float rMax = r0;
                bool flagged = false;
                for (int s = 0; s < 120; s++)
                {
                    StepN(k, 1);
                    foreach (CombatZone z in Patches(k).Where(p => p.EmgId == first.EmgId))
                    {
                        rMax = Mathf.Max(rMax, z.Radius);
                        flagged |= (z.PFlags & EmgMatterConst.FlagSpread) != 0;
                    }
                }
                Expect(first.Matter != 0 && flagged && rMax > r0 * 1.1f && rMax <= r0 * 2f + 1e-3f,
                    $"[冷却液, 蔓延]：迁入液洼（物质码 {first.Matter}）半径 {r0:0.00} → {rMax:0.00}（FGT-EMG-031：迁入斑块同样扩，不超 2 倍）");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                k.SpawnPatch(EmgMatterConst.Sludge, new double2(2.5, 6), 1f, 2f, 60f);
                k.Step(Dt, k.Time + Dt);
                for (int shot = 0; shot < 20; shot++)
                {
                    k.FireAt(me, foe, k.Time);
                    StepN(k, 60);
                }
                float r = PatchAt(k, new double2(2.5, 6)).Radius;
                Expect(r > 1.9f && r <= 2.001f, $"持续开火 20 秒：斑块半径 {r:0.00}，同一块只扩一次，不超过文本写的 2 倍");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                k.SpawnPatch(EmgMatterConst.Sludge, new double2(2.5, 6), 1f, 2f, 8f); // 存续 8 秒：最后 2 秒干缩
                StepN(k, (int)(4f / Dt));
                k.FireAt(me, foe, k.Time); // 只剩 2 秒可扩：提前走完
                StepN(k, (int)(1.9f / Dt));
                float rEarly = PatchAt(k, new double2(2.5, 6)).Radius;
                float prev = rEarly, jump = 0f;
                for (int s = 0; s < (int)(1.9f / Dt); s++)
                {
                    StepN(k, 1);
                    float cur = PatchAt(k, new double2(2.5, 6)).Radius;
                    jump = Mathf.Max(jump, cur - prev);
                    prev = cur;
                }
                Expect(rEarly > 1.8f && jump < 0.05f && prev < rEarly, $"存续不够 12 秒：剩 2 秒也扩到 {rEarly:0.00}（开始干缩前走完），之后单调缩到 {prev:0.00}，半径不跳变（最大回涨 {jump:0.000}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                k.SpawnPatch(EmgMatterConst.Sludge, new double2(2.5, 6), 1f, 2f, 8f);
                StepN(k, (int)(6.6f / Dt)); // 已经在干缩
                float before = PatchAt(k, new double2(2.5, 6)).Radius;
                k.FireAt(me, foe, k.Time);
                StepN(k, 12);
                CombatZone after = PatchAt(k, new double2(2.5, 6));
                Expect(after.Radius <= before + 1e-4f && (after.PFlags & EmgMatterConst.FlagSpread) == 0, $"已经在干缩的斑块不再扩：{before:0.00} → {after.Radius:0.00}");
            }
        }

        // ── C2. 拆解钳 ──────────────────────────────────────────────────────────

        private static void CheckClaw()
        {
            Line("  · C2. 拆解钳：部件声明经 WithProgram 接进武器；只拆不修；与回收修复同装同一残骸只拆一次；满耐久 / 冷却时拆解钳照拆、回收修复不修；己方残骸不拆；存读档保留");
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon claw = Formal(site, ComponentCatalog.CompClawId);
                CombatWeapon gun = Formal(site, ComponentCatalog.CompGunId);
                Expect(claw.ClawSalvage == 1 && gun.ClawSalvage == 0 && CombatKernel.WeaponKey(claw) != CombatKernel.WeaponKey(WithoutClaw(claw)),
                    "拆解钳主组件经正式路径（WithProgram 出口，同部件场）写上拆残骸；连射器没有；武器去重键区分");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(claw), 100f, 400f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.5), -1, 1f, 1f);
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 1.5), foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 300 && k.EmgScalars.Salvages == 0; s++)
                {
                    StepN(k, 1);
                }
                CombatEvent sal = Drain(k).FirstOrDefault(e => e.Kind == CombatEventKind.Salvaged);
                Expect(k.EmgScalars.Salvages == 1 && k.WreckCount == 0 && Mathf.Abs(Hp(k, me) - 100f) < 1e-4f && sal.Kind == CombatEventKind.Salvaged && sal.Value == 0f && Note(k, me) == 0,
                    $"只装拆解钳击毁：残骸被拆（{k.WreckCount} 块），本机血量不变（{Hp(k, me):0.##}），事件维修量 {sal.Value}，不写回收修复原因");
            }
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon both = Formal(site, ComponentCatalog.CompClawId, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(both), 100f, 400f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.5), -1, 1f, 1f);
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 1.5), foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 300 && k.EmgScalars.Salvages == 0; s++)
                {
                    StepN(k, 1);
                }
                StepN(k, 5);
                Expect(both.ClawSalvage == 1 && both.Program >= 0 && k.EmgScalars.Salvages == 1 && k.WreckCount == 0 && Hp(k, me) > 100f && Note(k, me) == EmgReclaimConst.NoteRepaired,
                    $"拆解钳 + 回收修复：同一残骸只拆一次（累计 {k.EmgScalars.Salvages}），修复 {Hp(k, me) - 100f:0.##}");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                CombatWeapon w = Instant(50f, pi);
                w.ClawSalvage = 1;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 400f, 400f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                Expect(k.WreckCount == 0 && k.EmgScalars.Salvages == 1 && Mathf.Abs(Hp(k, me) - 400f) < 1e-4f && Note(k, me) == 0,
                    "满耐久 + 拆解钳 + 回收修复：拆解钳照拆（残骸没留下、不写“留作掩体”），回收修复不修");
            }
            using (CombatKernel k = NewKernel())
            {
                int pi = Prog(k, "fw_reclaim");
                CombatWeapon w = Instant(50f, pi);
                w.ClawSalvage = 1;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f, 400f);
                int f1 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
                int f2 = Unit(k, CombatFaction.Hostile, new double2(2, 5), -1, 10f, 10f);
                k.FireAt(me, f1, k.Time);
                k.Step(Dt, k.Time + Dt);
                float h1 = Hp(k, me);
                k.FireAt(me, f2, k.Time + 0.2);
                k.Step(Dt, k.Time + Dt);
                Expect(h1 > 100f && Mathf.Abs(Hp(k, me) - h1) < 1e-4f && k.WreckCount == 0 && k.EmgScalars.Salvages == 2, "冷却中：拆解钳照拆（累计 2 块），回收修复这次不修");
                int own = AddWreck(k, new double2(8, 3), 10f, 1);
                float rc = k.SalvageForTests(me, own, false, true);
                Expect(rc < 0f && k.WreckCount == 1, "己方机器残骸（NoSalvage）拆解钳也不拆");
                byte[] snap = k.Serialize();
                using CombatKernel l = NewKernel();
                CombatLoadResult res = l.Load(snap);
                l.TryGetUnit(me, out CombatUnitView view);
                bool found = false;
                for (int i = 0; i < 8 && !found; i++)
                {
                    found = l.TryGetWeapon(i, out CombatWeapon lw) && lw.ClawSalvage == 1;
                }
                Expect(res == CombatLoadResult.Ok && found, $"存读档：武器的拆解钳标记保留（{res}）");
            }
        }

        private static CombatWeapon WithoutClaw(CombatWeapon w)
        {
            w.ClawSalvage = 0;
            return w;
        }

        /// <summary>C3. 残骸池满（卡片负向）：满 256 块时击杀 → 最早一块崩解、计数可见，本次击杀的残骸照常拆、照常修复。</summary>
        private static void CheckWreckPoolFull()
        {
            Line("  · C3. 残骸池满：灌满 256 块再击毁 → 最早一块崩解（计数可见），本次击杀照常拆残骸修复");
            using CombatKernel k = NewKernel();
            int pi = Prog(k, "fw_reclaim");
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pi)), 100f, 400f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
            int oldest = 0;
            for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
            {
                int id = AddWreck(k, new double2(40 + i % 16 * 3, 40 + i / 16 * 3), 1f);
                oldest = oldest == 0 ? id : oldest;
            }
            int full = k.WreckCount;
            int capped0 = k.WreckCounts.Capped;
            k.FireAt(me, foe, k.Time);
            k.Step(Dt, k.Time + Dt);
            Expect(full == EmgConst.DefaultWreckCapacity && k.WreckCounts.Capped == capped0 + 1 && k.WreckIndexOf(oldest) < 0 && k.EmgScalars.Salvages == 1 && Hp(k, me) > 100f
                   && k.WreckCount == full - 1 && Note(k, me) == EmgReclaimConst.NoteRepaired && HoverHas(k, me, "emg.reclaim.note.repaired"),
                $"池满 {full} 块：崩解计数 +{k.WreckCounts.Capped - capped0}、最早一块消失；本次击杀被拆、修复 {Hp(k, me) - 100f:0.##}、剩 {k.WreckCount} 块");
        }

        // ── E2. 导线端点 ────────────────────────────────────────────────────────

        private static void CheckWireEndpoints()
        {
            Line("  · E2. 导线端点：力场桥接的本机是一端但不被自己的导线减速；格斗 / 射弹的另一端不会连回出手者；旁边没有别的导体就不拉");
            float Travel(bool wire)
            {
                using var site = new CombatSite(SiteId, SiteCfg());
                CombatKernel k = site.Kernel;
                CombatWeapon w = wire ? Formal(site, ComponentCatalog.CompCoronaId, "fw_bridge") : Formal(site, ComponentCatalog.CompCoronaId);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w), 400f, 400f, CombatUnitKind.Machine, 0, 4f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), -1, 5000f, 5000f);
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 2), foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 240 && wire && k.WireCount == 0; s++)
                {
                    StepN(k, 1);
                }
                if (wire && (k.WireCount == 0 || k.WireAt(0).KindA != EmgWireConst.EndUnit || k.WireAt(0).A != me))
                {
                    Fail($"力场桥接没拉出以本机为一端的导线（导线 {k.WireCount} 条）");
                }
                k.TryGetUnit(me, out CombatUnitView v0);
                k.IssueCommand(me, CombatCommandKind.Move, v0.Position + new double2(-20, 0), 0, 0.2f, 0f, 0f, false);
                StepN(k, 40);
                k.TryGetUnit(me, out CombatUnitView v1);
                return (float)math.distance(v0.Position, v1.Position);
            }
            float wired = Travel(true), free = Travel(false);
            Expect(free > 1f && Mathf.Abs(wired - free) < free * 0.1f, $"力场桥接：本机带导线走 {wired:0.00} 米，对照没导线 {free:0.00} 米（端点不减速）");
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, ComponentCatalog.CompRamId, "fw_bridge");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.5), -1, 5000f, 5000f);
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 1.5), foe, 0.5f, 6f, 0.5f, false);
                StepN(k, 180);
                Expect(k.EmgScalars.WiresBorn == 0 && k.WireCount == 0, $"格斗桥接：目标 5 米内只有出手者这个导体 → 不拉（不连回自己，导线 {k.WireCount} 条）");
            }
        }

        // ── H. 载体矩阵 ─────────────────────────────────────────────────────────

        private static readonly (string Comp, string Name)[] CarrierComps =
        {
            (ComponentCatalog.CompGunId, "射弹"), (ComponentCatalog.CompRamId, "格斗"), (ComponentCatalog.CompDroneBayId, "无人机"),
            (ComponentCatalog.CompCoronaId, "力场"), (ComponentCatalog.CompSprayerId, "布区"),
        };

        private static double2 FoeAt(string comp) => comp == ComponentCatalog.CompRamId ? new double2(0, 1.5) : new double2(0, 3);

        private static void CheckCarriers()
        {
            Line("  · H. 载体矩阵（正式路径）：回收修复 / 桥接 / 蔓延 × 5 载体各一条行为断言（修复量、导线端点、扩散半径）");
            var report = new List<string>();
            bool ok = true;
            foreach ((string comp, string name) in CarrierComps)
            {
                using var site = new CombatSite(SiteId, SiteCfg());
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, comp, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w), 200f, 400f);
                double2 at = FoeAt(comp);
                int foe = Unit(k, CombatFaction.Hostile, at, -1, 1f, 1f);
                k.IssueCommand(me, CombatCommandKind.Attack, at, foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 600 && k.EmgScalars.Salvages == 0; s++)
                {
                    StepN(k, 1);
                }
                bool pass = k.EmgScalars.Salvages == 1 && k.WreckCount == 0 && Hp(k, me) > 200f && Hp(k, me) <= 260.001f;
                ok &= pass;
                report.Add($"{name}：拆 {k.EmgScalars.Salvages} 次、修复 {Hp(k, me) - 200f:0.##}{(pass ? string.Empty : "（✗）")}");
            }
            Expect(ok, "回收修复 × 载体（击毁 → 残骸消失 → 母机 / 本机血量上升）：" + string.Join("；", report));

            report.Clear();
            ok = true;
            foreach ((string comp, string name) in CarrierComps)
            {
                using var site = new CombatSite(SiteId, SiteCfg());
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, comp, "fw_bridge");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w));
                double2 at = FoeAt(comp);
                int foe = Unit(k, CombatFaction.Hostile, at, -1, 5000f, 5000f);
                int wk = AddWreck(k, at + new double2(3, 0), 10f);
                k.IssueCommand(me, CombatCommandKind.Attack, at, foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 600 && k.WireCount == 0; s++)
                {
                    StepN(k, 1);
                }
                EmgWire wire = k.WireAt(0);
                bool aura = comp == ComponentCatalog.CompCoronaId;
                bool pass = k.WireCount >= 1 && w.Reading.WeaveRadius == 0f
                            && (aura ? wire.KindA == EmgWireConst.EndUnit && wire.A == me && wire.B != me
                                : wire.KindA == EmgWireConst.EndUnit && wire.A == foe && wire.KindB == EmgWireConst.EndWreck && wire.B == wk);
                ok &= pass;
                report.Add($"{name}：A = {wire.KindA}/{wire.A}、B = {wire.KindB}/{wire.B}{(pass ? string.Empty : "（✗）")}");
            }
            Expect(ok, $"桥接 × 载体（力场：本机一端；其余：目标与 5 米内最近导体，不连回出手者；不再拉旧减速网）：" + string.Join("；", report));

            report.Clear();
            ok = true;
            foreach ((string comp, string name) in CarrierComps)
            {
                using var site = new CombatSite(SiteId, SiteCfg());
                CombatKernel k = site.Kernel;
                CombatWeapon w = Formal(site, comp, "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(w));
                double2 at = FoeAt(comp);
                int foe = Unit(k, CombatFaction.Hostile, at, -1, 5000f, 5000f);
                bool aura = comp == ComponentCatalog.CompCoronaId;
                bool noMedium = comp == ComponentCatalog.CompRamId || aura;
                // 污泥与水没有规则：只看扩散。力场以本机为心（4 米），其余在结算点旁
                double2 sludge = aura ? new double2(3.5, 0) : at + new double2(comp == ComponentCatalog.CompDroneBayId ? 2.4 : 1.6, 0);
                k.SpawnPatch(EmgMatterConst.Sludge, sludge, 1f, 2f, 60f);
                k.IssueCommand(me, CombatCommandKind.Attack, at, foe, 0.5f, 6f, 0.5f, false);
                for (int s = 0; s < 600 && k.EmgScalars.SpreadStarts == 0; s++)
                {
                    StepN(k, 1);
                }
                StepN(k, (int)(6f / Dt));
                CombatZone z = Patches(k).FirstOrDefault(p => p.Matter == EmgMatterConst.Sludge);
                bool hasWater = Patches(k).Any(p => p.Matter == EmgMatterConst.Water);
                bool pass = k.EmgScalars.SpreadStarts >= 1 && z.Radius > 1.3f && z.Radius <= 2.001f && hasWater != noMedium;
                ok &= pass;
                report.Add($"{name}：半径 {z.Radius:0.00}、{(hasWater ? "留水" : "不留水")}{(pass ? string.Empty : "（✗）")}");
            }
            Expect(ok, "蔓延 × 载体（FG19A 第 1.3 节读法：格斗 / 力场只扩已有斑块，其余另留一小摊水）：" + string.Join("；", report));
        }

        /// <summary>B3. 挂在触发器后的桥接 / 蔓延（子节点写端）由内核执行：[子母弹, 桥接] 子弹药命中拉导线，[子母弹, 蔓延] 子弹药命中处斑块扩散。</summary>
        private static void CheckChildWrites()
        {
            Line("  · B3. 子节点写端：[子母弹, 桥接 / 蔓延] 子弹药命中由内核执行，不再静默失效");
            (int wires, int spreads, bool child) Run(string fw)
            {
                using CombatKernel k = NewKernel();
                EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_cluster", fw }, FirmwareCarrier.Projectile);
                int pi = CombatSite.RegisterProgram(k, r);
                EmgNode root = r.KernelNodes[0];
                bool rootHas = false, childHas = false;
                for (int i = 0; i < r.KernelInstrs.Count; i++)
                {
                    EmgInstr x = r.KernelInstrs[i];
                    if (x.Verb == (byte)EmgVerb.Link || x.Verb == (byte)EmgVerb.Spread)
                    {
                        bool inRoot = i >= root.FirstInstr && i < root.FirstInstr + root.InstrCount;
                        rootHas |= inRoot;
                        childHas |= !inRoot;
                    }
                }
                var w = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = pi,
                    SpawnFlight = 1, ProjectileSpeed = 30f, ProjectileRadius = 0.15f, ProjectileLife = 1.2f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                };
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w));
                int tank = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f, 5000f);
                foreach (int deg in new[] { 0, 45, 135, 180, 225, 315, 60, 120 })
                {
                    double a = math.radians((double)deg);
                    double2 p = new double2(math.cos(a) * 1.6, 5 + math.sin(a) * 1.6);
                    Unit(k, CombatFaction.Hostile, p, -1, 5000f, 5000f);
                    k.SpawnPatch(EmgMatterConst.Sludge, p, 0.5f, 1f, 60f);
                }
                k.FireAt(me, tank, k.Time);
                StepN(k, 120);
                return (k.EmgScalars.WiresBorn, k.EmgScalars.SpreadStarts, childHas && !rootHas);
            }
            var b = Run("fw_bridge");
            var s = Run("fw_spread");
            Expect(b.child && s.child && b.wires > 0 && s.spreads > 0, $"编译：写端都在子节点（{b.child} / {s.child}）；子弹药命中拉导线 {b.wires} 条、扩散 {s.spreads} 次");
        }

        /// <summary>H2. 炮塔弹体武器（TurretService.BuildWeapon + WithProgram，FG19A 第 1.2 节场景 19）：旧减速网 / 旧放大 / 旧回旋退役，新写端照常生效。</summary>
        private static void CheckTurret()
        {
            Line("  · H2. 炮塔弹体武器单轨：旧减速网 / 旧驻留区与放大 / 旧回旋不再结算，导线 / 扩散 / 拆残骸照常（FGR-EMG-019）");
            var prof = new TurretProfileDef { Range = 12f, TurnRate = 180f, ProjectileSpeed = 20f, ProjectileRadius = 0.3f };
            CombatReading plain = TurretService.BuildWeapon(Preview(ComponentCatalog.CompGunId), prof, 1f, 0, false, false, false).Reading;
            CombatWeapon Built(CombatSite site, out CombatReading before, params string[] fw)
            {
                BlueprintCircuitPreview tp = Preview(ComponentCatalog.CompGunId, fw);
                CombatWeapon t0 = TurretService.BuildWeapon(tp, prof, 1f, 0, false, false, false);
                before = t0.Reading;
                return site.WithProgram(tp, t0, out _, out _);
            }
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon tw = Built(site, out CombatReading before, "fw_bridge");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(tw), 400f, 400f, CombatUnitKind.Turret);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                AddWreck(k, new double2(0, 9), 10f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                Expect(tw.Mode == CombatWeaponMode.Projectile && before.WeaveRadius > 0f && tw.Reading.WeaveRadius == 0f && k.WireCount == 1,
                    $"桥接：旧减速网半径 {before.WeaveRadius} → {tw.Reading.WeaveRadius}，导线 {k.WireCount} 条");
            }
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon tw = Built(site, out CombatReading before, "fw_spread");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(tw), 400f, 400f, CombatUnitKind.Turret);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                CombatZone water = Patches(k).FirstOrDefault(z => z.Matter == EmgMatterConst.Water);
                StepN(k, 120);
                CombatZone later = Patches(k).FirstOrDefault(z => z.EmgId == water.EmgId);
                Expect(before.ZoneRadius > 0f && before.BlastRadius > plain.BlastRadius && tw.Reading.ZoneRadius == 0f && tw.Reading.BlastRadius == plain.BlastRadius
                       && water.Matter == EmgMatterConst.Water && later.Radius > water.Radius,
                    $"蔓延：旧驻留区 {before.ZoneRadius} → {tw.Reading.ZoneRadius}、旧放大溅射 {before.BlastRadius} → {tw.Reading.BlastRadius}；真水 {water.Radius:0.00} → {later.Radius:0.00}");
            }
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                CombatKernel k = site.Kernel;
                CombatWeapon tw = Built(site, out CombatReading before, "fw_reclaim");
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(tw), 100f, 400f, CombatUnitKind.Turret);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 1f, 1f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                Expect(before.EchoCount > plain.EchoCount && tw.Reading.EchoCount == plain.EchoCount && k.EmgScalars.Salvages == 1 && k.WreckCount == 0 && Hp(k, me) > 100f,
                    $"回收修复（FG19A 场景 19）：旧回旋次数 {before.EchoCount} → {tw.Reading.EchoCount}；残骸被拆、炮塔修复 {Hp(k, me) - 100f:0.##}");
            }

            // 排在触发器后（写端在子节点，由 CombatLogic.EmgChildWrites 执行）：旧字段同样退役，只和 [子母弹] 自己的读法一样；子弹药照常执行新机制
            CombatReading clusterOnly;
            using (var site = new CombatSite(SiteId, SiteCfg()))
            {
                clusterOnly = Built(site, out _, "fw_cluster").Reading;
            }
            (CombatReading before, CombatReading now, int wires, int spreads, int salvages, float healed) Child(string fw, float ringHp, double ringDist, int[] degs)
            {
                using var site = new CombatSite(SiteId, SiteCfg());
                CombatKernel k = site.Kernel;
                CombatWeapon tw = Built(site, out CombatReading before, "fw_cluster", fw);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), site.WeaponIndex(tw), 200f, 400f, CombatUnitKind.Turret);
                int tank = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f, 5000f);
                foreach (int deg in degs)
                {
                    double a = math.radians((double)deg);
                    double2 p = new double2(math.cos(a) * ringDist, 6 + math.sin(a) * ringDist);
                    Unit(k, CombatFaction.Hostile, p, -1, ringHp, ringHp);
                    if (ringHp > 1f)
                    {
                        k.SpawnPatch(EmgMatterConst.Sludge, p, 0.5f, 1f, 60f); // 给导线 / 扩散当对象；碎靶站进污泥会被慢慢毒死（没有击杀者），回收修复这条不铺
                    }
                }
                k.FireAt(me, tank, k.Time);
                StepN(k, 120);
                return (before, tw.Reading, k.EmgScalars.WiresBorn, k.EmgScalars.SpreadStarts, k.EmgScalars.Salvages, Hp(k, me) - 200f);
            }
            int[] near = { 0, 45, 135, 180, 225, 315, 60, 120 };
            var cb = Child("fw_bridge", 5000f, 1.6, near);
            Expect(cb.before.WeaveRadius > 0f && cb.now.WeaveRadius == clusterOnly.WeaveRadius && cb.wires > 0,
                $"[子母弹, 桥接]：旧减速网半径 {cb.before.WeaveRadius} → {cb.now.WeaveRadius}（只装子母弹 {clusterOnly.WeaveRadius}）；子弹药拉导线 {cb.wires} 条");
            var cs = Child("fw_spread", 5000f, 1.6, near);
            (float, float, float, float) Linger(CombatReading x) => (x.ZoneRadius, x.ZoneSeconds, x.ZoneDps, x.BlastRadius);
            Expect(Linger(cs.before) != Linger(clusterOnly) && Linger(cs.now) == Linger(clusterOnly) && cs.spreads > 0,
                $"[子母弹, 蔓延]：旧驻留区 / 溅射（半径, 秒, 每秒伤害, 溅射半径）{Linger(cs.before)} → {Linger(cs.now)}（只装子母弹 {Linger(clusterOnly)}）；子弹药扩散 {cs.spreads} 次");
            // 碎靶一圈放到 3.2 米：子母弹自己的旧溅射（炮塔兼容读法，主弹命中时 2.5 米内结算）够不着，只有子弹药够得着，击杀才是子节点读到的；不放在弹道上（240°～300°）
            var cr = Child("fw_reclaim", 1f, 3.2, Enumerable.Range(0, 24).Select(i => i * 15).Where(deg => deg < 240 || deg > 300).ToArray());
            Expect(cr.before.EchoCount > clusterOnly.EchoCount && cr.now.EchoCount == clusterOnly.EchoCount && cr.salvages > 0 && cr.healed > 0f,
                $"[子母弹, 回收修复]：旧回旋次数 {cr.before.EchoCount} → {cr.now.EchoCount}（只装子母弹 {clusterOnly.EchoCount}）；子弹药击毁拆残骸 {cr.salvages} 次、炮塔修复 {cr.healed:0.##}");
        }

        // ── I. 真实 CombatSite 事件链路 ───────────────────────────────────────────

        /// <summary>I. 卡片负向与 B19：真实 CombatSite + 登记过的机器记录——HealRequest → 记录血量 → SyncMachineHealth 不冲掉；Salvaged → 统计；统计面板战斗页；存读档。</summary>
        private static void CheckSitePath()
        {
            Line("  · I. 真实 CombatSite：己方机器修复走 HealRequest 改记录、下一次同步不冲掉；不在本地点的记录不改；维修量进统计（机器 + 炮塔）、统计面板战斗页有这一行、存读档保留");
            string dir = Path.Combine(Path.GetTempPath(), "bingames-emgrecl-selfcheck-" + Guid.NewGuid().ToString("N"));
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Directory.CreateDirectory(dir);
            CampaignSaveService.SaveDirectoryOverrideForTests = dir;
            GameObject sgo = null;
            try
            {
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                CampaignState s = CampaignState.CreateNew("emgrecl", "Standard", 4242);
                CampaignSession.Set(Slot, s);
                PrimitiveInventory.EnsureSeeded(s);
                SignalCoreService.EnsureInitialized(s);
                MachineRegistry.ResetForNewCampaign();
                float repaired;
                using (var site = new CombatSite(SiteId, SiteCfg()))
                {
                    CombatKernel k = site.Kernel;
                    int weapon = site.WeaponIndex(Formal(site, ComponentCatalog.CompGunId, "fw_reclaim"));
                    MachineOpResult rr = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, SiteId, Vector2.zero, 100f, 400f);
                    MachineRegistry.TryGetRecord(rr.LogicId, out MachineRecord rec);
                    site.SpawnMachine(s, rec, Vector2.zero, false);
                    site.TryGetMachineUnit(rec.LogicId, out int unit);
                    k.SetUnitWeapon(unit, weapon);
                    int foe = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 1f, 1f);
                    k.FireAt(unit, foe, k.Time);
                    for (int i = 0; i < 5; i++)
                    {
                        site.Step(Dt, k.Time + Dt);
                    }
                    repaired = rec.Health - 100f;
                    float mirror = Hp(k, unit);
                    site.SyncMachineHealth(rec);
                    float afterSync = Hp(k, unit);
                    Expect(repaired > 0f && repaired <= 60.001f && Mathf.Abs(mirror - rec.Health) < 1e-3f && Mathf.Abs(afterSync - rec.Health) < 1e-3f && k.WreckCount == 0,
                        $"己方机器（外部血量）拆残骸：HealRequest 经 CombatSite 改记录血量 100 → {rec.Health:0.##}，内核镜像 {mirror:0.##}，再同步 {afterSync:0.##}（不被冲掉）");
                    Expect(s.Stats.ReclaimSalvages == 1 && Mathf.Abs((float)s.Stats.ReclaimRepaired - repaired) < 1e-3f,
                        $"B19：Salvaged 事件累加统计：拆残骸 {s.Stats.ReclaimSalvages} 次、累计维修 {s.Stats.ReclaimRepaired:0.##}");
                    // 记录不在本地点（RegionId 对不上）：不改记录
                    MachineOpResult away = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, "elsewhere", new Vector2(10f, 0f), 100f, 400f);
                    MachineRegistry.TryGetRecord(away.LogicId, out MachineRecord awayRec);
                    site.SpawnMachine(s, awayRec, new Vector2(10f, 0f), false);
                    site.TryGetMachineUnit(awayRec.LogicId, out int awayUnit);
                    k.SetUnitWeapon(awayUnit, weapon);
                    int foe2 = Unit(k, CombatFaction.Hostile, new double2(10, 5), -1, 1f, 1f);
                    k.FireAt(awayUnit, foe2, k.Time);
                    for (int i = 0; i < 5; i++)
                    {
                        site.Step(Dt, k.Time + Dt);
                    }
                    Expect(Mathf.Abs(awayRec.Health - 100f) < 1e-4f, $"记录不在本地点（RegionId 对不上）的修复请求不改记录（{awayRec.Health:0.##}）");
                    // 炮塔（血量在内核）：拆残骸直接修复，维修量同样进统计（FG19A 场景 19）
                    int salvages0 = s.Stats.ReclaimSalvages;
                    double total0 = s.Stats.ReclaimRepaired;
                    CombatWeapon tw = Formal(site, ComponentCatalog.CompGunId, "fw_reclaim");
                    int turret = site.SpawnTurretUnit(9001, new Vector2(30f, 0f), 0.5f, 100f, 400f, tw, 0f, true, Vector2.up, 0f);
                    int foe3 = Unit(k, CombatFaction.Hostile, new double2(30, 5), -1, 1f, 1f);
                    k.FireAt(turret, foe3, k.Time);
                    for (int i = 0; i < 5; i++)
                    {
                        site.Step(Dt, k.Time + Dt);
                    }
                    Expect(Hp(k, turret) > 100f && s.Stats.ReclaimSalvages == salvages0 + 1 && s.Stats.ReclaimRepaired > total0,
                        $"炮塔拆残骸：血量 100 → {Hp(k, turret):0.##}，统计累计 {s.Stats.ReclaimSalvages} 次、{s.Stats.ReclaimRepaired:0.##}");
                }
                // 统计面板战斗页（暂停菜单“统计”按钮调的同一个入口）
                VisualElement statsRoot = MountUxml("Assets/GameRes/Raw/UI/UiKit/StatsPanel.uxml", out sgo);
                StatsPanelUIToolkit.InWorldOverrideForTests = true;
                StatsPanelUIToolkit sp = sgo.AddComponent<StatsPanelUIToolkit>();
                sp.BindView(statsRoot);
                StatsPanelUIToolkit.Open();
                Click(sp.TabButton(StatsTab.Combat));
                string rows = string.Join("\n", Enumerable.Range(0, sp.VisibleRowCount).Select(sp.RowText));
                string line = GameText.Format("stats.reclaim.line", s.Stats.ReclaimSalvages, ProductionStats.UiNumber((float)s.Stats.ReclaimRepaired));
                Click(sp.CloseButton);
                Expect(rows.Contains(line) && !GameText.ContainsMarker(rows), $"统计面板战斗页：“{line}”");
                SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
                LoadResult loaded = CampaignSaveService.Load(Slot);
                Expect(saved.Success && loaded.Success && loaded.State.Stats.ReclaimSalvages == s.Stats.ReclaimSalvages && Math.Abs(loaded.State.Stats.ReclaimRepaired - s.Stats.ReclaimRepaired) < 1e-3,
                    $"存读档：拆残骸 {loaded.State?.Stats?.ReclaimSalvages} 次、累计维修 {loaded.State?.Stats?.ReclaimRepaired:0.##} 保留");
            }
            finally
            {
                StatsPanelUIToolkit.InWorldOverrideForTests = false;
                if (sgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(sgo);
                }
                MachineRegistry.ResetForNewCampaign();
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                    // 临时目录删不掉不影响结果
                }
            }
        }

        /// <summary>真 UIDocument + 面板（与 FgReactionFeedbackSelfCheck 同一夹具）。</summary>
        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgEmgReclaimSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Click(Button b)
        {
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        // ── G. 存读档 / 暂停 / 倍速 ─────────────────────────────────────────────

        private static void CheckSaveLoadSpeed()
        {
            Line("  · G. 存读档：导线、扩散中的斑块、回收冷却与悬停原因进快照，读档续跑哈希一致；暂停不走、0.5x～3x 下导线都按 3 秒模拟时间到期");
            using CombatKernel k = NewKernel();
            int pr = Prog(k, "fw_reclaim");
            int pb = Prog(k, "fw_bridge");
            int ps = Prog(k, "fw_spread");
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(50f, pr)), 100f, 400f);
            int me2 = Unit(k, CombatFaction.Player, new double2(3, 0), k.AddWeapon(Instant(1f, pb)));
            int me3 = Unit(k, CombatFaction.Player, new double2(-3, 0), k.AddWeapon(Instant(1f, ps)));
            int f1 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 10f, 10f);
            int f2 = Unit(k, CombatFaction.Hostile, new double2(3, 6));
            int f3 = Unit(k, CombatFaction.Hostile, new double2(-3, 6));
            AddWreck(k, new double2(3, 9), 10f);
            k.SpawnPatch(EmgMatterConst.Water, new double2(-3, 6), 1f, 1f, 60f);
            k.Step(Dt, k.Time + Dt);
            k.FireAt(me, f1, k.Time);
            k.FireAt(me2, f2, k.Time);
            k.FireAt(me3, f3, k.Time);
            StepN(k, 30);
            byte[] snap = k.Serialize();
            using CombatKernel l = NewKernel();
            CombatLoadResult res = l.Load(snap);
            bool same0 = res == CombatLoadResult.Ok && l.WireCount == k.WireCount && l.EmgStateHash() == k.EmgStateHash() && Note(l, me) == EmgReclaimConst.NoteRepaired
                         && Mathf.Abs(PatchAt(l, new double2(-3, 6)).Growth - PatchAt(k, new double2(-3, 6)).Growth) < 1e-6f;
            StepN(k, 120);
            StepN(l, 120);
            Expect(same0 && k.WireCount == 1 && l.EmgStateHash() == k.EmgStateHash() && Mathf.Abs(PatchAt(l, new double2(-3, 6)).Radius - PatchAt(k, new double2(-3, 6)).Radius) < 1e-5f,
                $"读档 {res}：导线 {l.WireCount} 条、悬停原因、扩散速率一致；续跑 2 秒后涌现哈希一致、导线 / 斑块半径一致");
            var lefts = new List<string>();
            bool speedOk = true;
            foreach (float mul in new[] { 0.5f, 1f, 2f, 3f })
            {
                using CombatKernel s = NewKernel();
                int pi = Prog(s, "fw_bridge");
                int a = Unit(s, CombatFaction.Player, new double2(0, 0), s.AddWeapon(Instant(1f, pi)));
                int b = Unit(s, CombatFaction.Hostile, new double2(0, 6));
                AddWreck(s, new double2(0, 9), 10f);
                s.FireAt(a, b, s.Time);
                s.Step(Dt, s.Time + Dt);
                float dt = Dt * mul;
                int n29 = (int)(2.85f / dt);
                StepN(s, n29, dt);
                int at29 = s.WireCount;
                double t29 = s.Time;
                s.Step(0f, s.Time); // 暂停一帧（dt = 0）不走
                bool paused = s.WireCount == at29 && s.Time == t29;
                StepN(s, (int)(0.3f / dt) + 1, dt);
                speedOk &= at29 == 1 && paused && s.WireCount == 0;
                lefts.Add($"{mul}x：2.85 秒 {at29} 条 → 3.15 秒 {s.WireCount} 条");
            }
            Expect(speedOk, "暂停与倍速：" + string.Join("；", lefts));
        }

        private static void CheckTexts()
        {
            Line("  · H. 文本：回收修复 / 桥接 / 蔓延描述按新机制、悬停原因与统计行有文本键（中英）");
            string[] keys = { "emg.reclaim.note.repaired", "emg.reclaim.note.no_wreck", "emg.reclaim.note.full", "emg.reclaim.note.cooldown", "emg.reclaim.note.no_salvage", "stats.reclaim.line",
                "firmware.fw_reclaim.migrated", "firmware.fw_bridge.migrated", "firmware.fw_spread.migrated" };
            var missing = keys.Where(key => GameText.Get(key) == key || string.IsNullOrEmpty(GameText.Get(key))).ToList();
            string desc = ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.First(r => r.Id == "fw_reclaim").DescKey;
            Expect(missing.Count == 0 && GameText.Get(desc).Contains("残骸") && !GameText.Get(desc).Contains("伤害"),
                $"文本键齐全{(missing.Count > 0 ? "；缺：" + string.Join("、", missing) : string.Empty)}；回收修复描述：{GameText.Get(desc)}");
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────

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

        private static void Expect(bool ok, string msg)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + msg);
            }
            else
            {
                Fail(msg);
            }
        }

        private static void Fail(string msg)
        {
            _fail++;
            Line("    ✗ " + msg);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
