using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FIX-01 现存文案与实现不符修复（FG-GAP-152～156；FG19 FGT-EMG-005 / 011、FGR-EMG-015 规则二、FG02 FGR-FW-011；FG19A 第 1.1 / 1.2 / 1.4 节对应 5 行与 D-01～D-04、D-14）。
    /// 起真实编译器（EmgProgramCompiler，emg.stage 取 1）与真实内核（CombatKernel，Burst 步进）逐条断言“描述—行为”一致，每条都有“触发”与“不触发”对照：
    /// A 文本与声明 / 表值同源（描述里的数 = 声明 = 读法），旧说法不再出现；B 分裂；C 延时引信（飞行中空爆、向前扇形、提前命中不空爆、暂停与倍速按游戏时间）；
    /// C 段另对格斗 / 力场 / 布区 / 无人机逐载体核对“0.6 秒放 3 份”；D 壁撞（撞障碍才冲击 + evt.impact，开阔地不触发，撞敌人不反弹）；
    /// E 追射（真补一发、只补一次、目标 / 发射者不在不补、格斗 / 力场立刻追加主目标、布区追加 = 这一拍 × 50%、无人机从母机补射、待生成存读档）；
    /// F 磁聚（过渡文案 = 旧读法弱牵引 + 标签，端口行标“后续版本生效”，没有旧读法的新固件标“当前不生效”）；
    /// G 图鉴 / 详情 / 三选一同步、旧存档蓝图预览只对改了机制的 3 条写变化说明（行为没变的不写）；H emg.stage = 0 不受影响。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgTextBehaviorSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgTextBehaviorSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgTextBehaviorSelfCheck
    {
        /// <summary>旧组合引擎模块（Demo 冻结 DLL）仍会贴、但已从 fg.TbStatusTag 撤下的标记（FG19A 第 1.6 节迁移表“FeedbackHit 废弃”，FG-GAP-153）。</summary>
        public static readonly HashSet<string> RetiredEngineTags = new HashSet<string>(StringComparer.Ordinal) { "FeedbackHit" };

        /// <summary>读法说明按 FG19A 新读法写的（固件, 载体）——与 tools/cell_tables/fgdata_firmware.py READ_TEXT_FG19A 同一份名单（A 段核对两边一致）。</summary>
        public static readonly HashSet<(string, FirmwareCarrier)> Fg19aReadings = new HashSet<(string, FirmwareCarrier)>
        {
            (FwDelay, FirmwareCarrier.Projectile), (FwDelay, FirmwareCarrier.Melee), (FwDelay, FirmwareCarrier.Summon), (FwDelay, FirmwareCarrier.Aura), (FwDelay, FirmwareCarrier.Field),
            (FwFollow, FirmwareCarrier.Projectile), (FwFollow, FirmwareCarrier.Melee), (FwFollow, FirmwareCarrier.Summon), (FwFollow, FirmwareCarrier.Aura), (FwFollow, FirmwareCarrier.Field),
            (FwWall, FirmwareCarrier.Projectile), (FwWall, FirmwareCarrier.Summon),
        };

        private const string FwSplit = "fw_split", FwDelay = "fw_delayfuse", FwWall = "fw_wallslam", FwFollow = "fw_followup", FwMagnet = "fw_magnet";
        private const float Dt = 1f / 60f;
        private const int SessionSlot = 0;
        private const int UplinkSlot = 2;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static string _diag = string.Empty;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/现存文案与实现一致")]
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
            Line("\n[现存文案与实现一致] EMG1-FIX-01：延时引信、追射、分裂、壁撞、磁聚的“描述—行为”触发 / 不触发对照（FGT-EMG-005 / 011，FG-GAP-152～156）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                UseStage(1);
                Step(CheckTexts);
                Step(CheckSplit);
                Step(CheckDelayFuse);
                Step(CheckWallSlam);
                Step(CheckFollowUp);
                Step(CheckMagnet);
                Step(CheckDisplaySync);
                Step(CheckStageZero);
            }
            catch (Exception e)
            {
                Fail($"现存文案自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [现存文案与实现一致] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            return c;
        }

        private static EmgProgramResult Compile(string fw, FirmwareCarrier carrier = FirmwareCarrier.Projectile) =>
            EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier);

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Me(CombatKernel k, double2 p, int weapon) => Unit(k, CombatFaction.Player, p, weapon, 100f);

        private static int Foe(CombatKernel k, double2 p, float hp) => Unit(k, CombatFaction.Hostile, p, -1, hp);

        private static CombatWeapon Instant(int program, float damage = 10f, float range = 30f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f, Program = program,
        };

        /// <summary>真实编译 → 登记 → 经热更层同一入口置 SpawnFlight（需要飞行的程序在连射器上实体化，弹速 / 半径 / 寿命取表）。</summary>
        private static CombatWeapon Gun(CombatKernel k, EmgProgramResult r)
        {
            CombatWeapon w = Instant(CombatSite.RegisterProgram(k, r));
            CombatSite.ApplySpawnFlight(ref w, r, ComponentCatalog.CompGunId);
            return w;
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static List<CombatProjectile> Projectiles(CombatKernel k, EmgProjFlags flag)
        {
            var list = new List<CombatProjectile>();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (flag == EmgProjFlags.None || (p.Flags & flag) != 0))
                {
                    list.Add(p);
                }
            }
            return list;
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : new double2(double.NaN, double.NaN);

        private static bool Near(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) <= eps;

        private static string Desc(string fw) => FirmwareCatalog.TryGet(fw, out MechanicalContentDef d) ? d.Description : string.Empty;

        private static string Read(string fw, FirmwareCarrier c) => FirmwareKinds.Reading(fw, c) ?? string.Empty;

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static readonly string[] ZhDigits = { "零", "一", "两", "三", "四", "五" };

        private static IEnumerable<EmgInstr> RootInstrs(EmgProgramResult r)
        {
            EmgNode n = r.KernelNodes[0];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                yield return r.KernelInstrs[i];
            }
        }

        private static ushort PortCode(string id)
        {
            foreach (KeyValuePair<ushort, string> kv in EmgCatalog.Current.PortIdByCode)
            {
                if (kv.Value == id)
                {
                    return kv.Key;
                }
            }
            return 0;
        }

        // ── A. 文本与声明 / 表值同源 ─────────────────────────────────────────────

        private static void CheckTexts()
        {
            Line("  · A. 描述里的数 = 通道声明（真实编译）= 读法说明；旧说法（两枚 / 先飞一段 / 墙体和地形 / 立刻补一发 / 弹道被拉向一点）不再出现");
            EmgProgramResult split = Compile(FwSplit);
            int splitN = split.KernelNodes.Count > 1 ? split.KernelNodes[1].TrigCount : 0;
            Expect(splitN == 3 && FirmwareKinds.TryGetRow(FwSplit, out GameConfig.fg.FirmwareKind sr) && sr.ReadFields.Contains("split:3")
                   && Desc(FwSplit).Contains(ZhDigits[splitN] + "枚") && !Desc(FwSplit).Contains("两枚") && Read(FwSplit, FirmwareCarrier.Projectile).Contains($"{splitN} 枚")
                   && Desc(FwSplit).Contains("一半") && Read(FwSplit, FirmwareCarrier.Projectile).Contains("50%"),
                $"分裂（D-01，FG-GAP-154）：声明触发 {splitN} 份 = 表值 split:3 = 描述“{ZhDigits[Math.Min(splitN, 5)]}枚、一半” = 读法“{Read(FwSplit, FirmwareCarrier.Projectile)}”");

            EmgProgramResult delay = Compile(FwDelay);
            EmgInstr timer = RootInstrs(delay).FirstOrDefault(x => x.Port == EmgConst.EventPortBase + (int)EmgEventKind.Timer);
            int delayN = delay.KernelNodes.Count > 1 ? delay.KernelNodes[1].TrigCount : 0;
            string dd = Desc(FwDelay), dr = Read(FwDelay, FirmwareCarrier.Projectile);
            Expect(Near(timer.Param2, 0.6f) && timer.Role == EmgConst.RoleTrigger && delayN == 3 && dd.Contains($"{F(timer.Param2)} 秒") && dd.Contains(ZhDigits[delayN] + "枚")
                   && dd.Contains("扇形") && dd.Contains("不空爆") && !dd.Contains("先飞一段") && dr.Contains($"{F(timer.Param2)} 秒") && dr.Contains($"{delayN} 枚") && !dr.Contains("回波"),
                $"延时引信（D-02，FG-GAP-152）：声明 evt.timer {F(timer.Param2)} 秒触发 {delayN} 份；描述“{dd}”；读法“{dr}”");

            EmgProgramResult wall = Compile(FwWall);
            EmgInstr bounce = RootInstrs(wall).FirstOrDefault(x => x.Port == PortCode("grm.mod.bounce"));
            EmgInstr kin = RootInstrs(wall).FirstOrDefault(x => x.Port == EmgConst.PortKinetic);
            bool obsCond = RootInstrs(wall).Any(x => x.Port == EmgConst.PortObstacle && x.Ordinal == kin.Ordinal && x.Role > EmgConst.RoleTrigger);
            string wd = Desc(FwWall), wr = Read(FwWall, FirmwareCarrier.Projectile);
            Expect(Near(bounce.Amount, 2f) && Near(bounce.Param2, 2f) && Near(kin.Amount, 0.5f) && Near(kin.Param2, 1.5f) && obsCond
                   && wd.Contains($"最多 {F(bounce.Amount)} 次") && wd.Contains($"{F(kin.Param2)} 米") && wd.Contains("一半") && wd.Contains("开阔地") && !wd.Contains("墙体和地形")
                   && wr.Contains($"{F(kin.Param2)} 米") && wr.Contains("50%") && FirmwareKinds.TagsOf(FwWall).Length == 0 && !wr.Contains("撞壁"),
                $"壁撞（D-03，FG-GAP-155）：声明 反弹 ×{F(bounce.Amount)}（面 = 障碍，param2 {F(bounce.Param2)}）+ 动能 {F(kin.Amount)} 倍 / {F(kin.Param2)} 米（同条读 ter.obstacle 条件）；不再贴撞壁；描述“{wd}”");

            EmgProgramResult follow = Compile(FwFollow);
            EmgInstr spawn = RootInstrs(follow).FirstOrDefault(x => x.Port == EmgConst.PortProjectile && x.Role > EmgConst.RoleLastRead);
            EmgProgramResult followMelee = Compile(FwFollow, FirmwareCarrier.Melee);
            EmgInstr spawnMelee = RootInstrs(followMelee).FirstOrDefault(x => x.Port == EmgConst.PortProjectile && x.Role > EmgConst.RoleLastRead);
            Func<string, string, bool> skip = EmgProgramCompiler.LegacySkip(follow);
            string fd = Desc(FwFollow), fr = Read(FwFollow, FirmwareCarrier.Projectile);
            Expect(spawn.Verb == (byte)EmgVerb.Spawn && EmgCatalog.VerbCode("spawn") == (int)EmgVerb.Spawn && (spawn.Flags & EmgInstrFlags.RootKernel) != 0
                   && Near(spawn.A, 0.1f) && Near(spawnMelee.A, 0f) && Near(spawn.Amount, 1f) && Near(spawn.Param2, 0.5f)
                   && skip != null && skip(FwFollow, "feedback") && !skip(FwFollow, EmgProgramCompiler.TagsField)
                   && fd.Contains($"{F(spawn.A)} 秒") && fd.Contains($"{F(spawn.Amount)} 枚") && fd.Contains("一半") && fd.Contains("不会再次追射") && !fd.Contains("立刻补一发")
                   && fr.Contains($"{F(spawn.A)} 秒") && fr.Contains("50%") && Read(FwFollow, FirmwareCarrier.Melee).Contains("立刻")
                   && !StatusTagCatalog.TryGet("FeedbackHit", out _),
                $"追射（D-14，FG-GAP-153）：根节点 ent.projectile 由内核执行（RootKernel，射弹延迟 {F(spawn.A)} 秒 / 格斗 {F(spawnMelee.A)} 秒，{F(spawn.Amount)} 发 × {F(spawn.Param2)} 倍），"
                + "旧回波字段 feedback 不再结算（单轨），追击标记已从 fg.TbStatusTag 撤下；描述“" + fd + "”");

            string md = Desc(FwMagnet);
            FirmwareKinds.TryGetRow(FwMagnet, out GameConfig.fg.FirmwareKind mr);
            Expect(!EmgCatalog.IsOwnerOnDeclTrack(FwMagnet) && mr != null && mr.ReadFields.Contains("pull:")
                   && FirmwareKinds.TagsOf(FwMagnet).Contains("Charged") && FirmwareKinds.TagsOf(FwMagnet).Contains("Magnet")
                   && md.Contains("吸近") && md.Contains("充能") && md.Contains("磁化") && !md.Contains("弹道被拉向一点") && Read(FwMagnet, FirmwareCarrier.Projectile).Contains("拉近"),
                $"磁聚（D-04，FG-GAP-156 描述部分）：阶段 1 仍走旧读法（{mr?.ReadFields} + 充能 / 磁化），过渡描述“{md}”与之一致；磁场机制随 EMG2-FLD-01");

            // 读法名单两边一致：Python READ_TEXT_FG19A 写过的（固件, 载体）都不含旧字段短语；没写的仍由字段短语生成（FgReadingMatrixSelfCheck F 段）。
            var leak = new List<string>();
            foreach ((string fw, FirmwareCarrier c) in Fg19aReadings)
            {
                string t = Read(fw, c);
                if (string.IsNullOrEmpty(t) || t.Contains("回波") || t.Contains("撞到目标后弹向下一个") || t.Contains("撞壁"))
                {
                    leak.Add($"{fw}.{c}：{t}");
                }
            }
            Expect(leak.Count == 0 && Read(FwWall, FirmwareCarrier.Melee).Contains("弹开"),
                $"{Fg19aReadings.Count} 句按 FG19A 新读法写的读法说明都没有旧机制说法；壁撞的格斗 / 力场 / 布区仍是击退读法（{Read(FwWall, FirmwareCarrier.Melee)}）{(leak.Count > 0 ? "；不符：" + string.Join("、", leak) : string.Empty)}");
        }

        // ── B. 分裂 ─────────────────────────────────────────────────────────────

        private static void CheckSplit()
        {
            Line("  · B. 分裂：命中后裂出 3 枚次级弹（各 50%）打向 5 米内其他目标；对照：同一武器不装程序不裂");
            EmgProgramResult r = Compile(FwSplit);
            foreach (bool with in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 32);
                CombatWeapon w = with ? Gun(k, r) : Instant(-1);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 6), 200f);
                Foe(k, new double2(2, 7), 200f);
                Foe(k, new double2(-2, 7), 200f);
                Foe(k, new double2(0, 9), 200f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 3);
                List<CombatProjectile> subs = Projectiles(k, EmgProjFlags.Sub);
                if (with)
                {
                    Expect(subs.Count == 3 && subs.All(p => Near(p.Damage, 5f)) && subs.All(p => p.IgnoreUnit == foe),
                        $"触发：命中后放出 {subs.Count} 枚次级弹（伤害 {string.Join("/", subs.Select(p => F(p.Damage)))}，都不打被命中的目标）——与描述“三枚、各一半”一致");
                }
                else
                {
                    Expect(subs.Count == 0 && Near(Hp(k, foe), 190f), $"不触发：不装分裂的同一武器命中后不放次级弹（{subs.Count} 枚）");
                }
            }
        }

        // ── C. 延时引信 ─────────────────────────────────────────────────────────

        /// <summary>延时引信打 <paramref name="dist"/> 米外的目标；<paramref name="perFrame"/> = 每帧步数（3 = 3x 倍速），<paramref name="pauseFrames"/> = 0.3 秒时暂停（不步进）的帧数。返回空爆时的游戏时间（没空爆 −1）。</summary>
        private static double RunDelay(EmgProgramResult r, double dist, int perFrame, int pauseFrames, out List<CombatProjectile> subs, out float2 rootDir, out bool rootFlying, out ulong hash)
        {
            using var k = new CombatKernel(Cfg(), 32);
            int me = Me(k, new double2(0, 0), k.AddWeapon(Gun(k, r)));
            int foe = Foe(k, new double2(0, dist), 300f);
            k.FireAt(me, foe, k.Time);
            subs = new List<CombatProjectile>();
            rootDir = new float2(0f, 1f);
            rootFlying = false;
            double at = -1;
            int frames = 0;
            while (k.Time < 2.0 && frames < 2000)
            {
                frames++;
                if (pauseFrames > 0 && k.Time >= 0.3)
                {
                    pauseFrames--; // 暂停：这一帧不步进（游戏时间不走）
                    continue;
                }
                StepN(k, perFrame);
                List<CombatProjectile> s = Projectiles(k, EmgProjFlags.Sub);
                if (at < 0 && s.Count > 0)
                {
                    at = k.Time;
                    subs = s;
                    List<CombatProjectile> root = Projectiles(k, EmgProjFlags.Flight).Where(p => (p.Flags & EmgProjFlags.Sub) == 0).ToList();
                    rootFlying = root.Count == 1;
                    if (root.Count == 1)
                    {
                        rootDir = math.normalize(root[0].Vel);
                    }
                }
            }
            hash = k.StateHash();
            return at;
        }

        private static void CheckDelayFuse()
        {
            Line("  · C. 延时引信：出膛 0.6 秒空爆、3 枚子弹药向前扇形（±15°）、母弹继续飞；对照：0.6 秒内先命中不空爆；暂停 / 3x 倍速按游戏时间到点（FGT-EMG-011 定时）");
            EmgProgramResult r = Compile(FwDelay);
            double at = RunDelay(r, 40, 1, 0, out List<CombatProjectile> subs, out float2 dir, out bool flying, out ulong h1);
            float maxOff = subs.Count > 0 ? subs.Max(p => math.degrees(math.acos(math.clamp(math.dot(math.normalize(p.Vel), dir), -1f, 1f)))) : 999f;
            bool spread = subs.Select(p => math.round(math.atan2(p.Vel.y, p.Vel.x) * 100f)).Distinct().Count() == subs.Count;
            Expect(at > 0.59 && at < 0.6 + 2 * Dt && subs.Count == 3 && flying && maxOff <= EmgConst.TimerFanDeg * 0.5f + 0.5f && spread && subs.All(p => Near(p.Damage, 5f)),
                $"触发：目标 40 米外，空爆在游戏时间 {at:0.000} 秒（声明 0.6 秒）；{subs.Count} 枚子弹药向前扇形（与母弹方向最大夹角 {maxOff:0.#}°，扇角 {EmgConst.TimerFanDeg}°），各 {F(subs.FirstOrDefault().Damage)}；母弹仍在飞={flying}");
            double near = RunDelay(r, 6, 1, 0, out List<CombatProjectile> subs2, out _, out _, out _);
            Expect(near < 0 && subs2.Count == 0, $"不触发：目标 6 米（约 0.2 秒命中，母弹命中即结束）——到 2 秒都不空爆（{subs2.Count} 枚），与描述“0.6 秒内先命中则不空爆”一致");
            double fast = RunDelay(r, 40, 3, 0, out List<CombatProjectile> s3, out _, out _, out ulong h3);
            double paused = RunDelay(r, 40, 1, 90, out List<CombatProjectile> s4, out _, out _, out ulong h4);
            Expect(Math.Abs(fast - at) <= 3 * Dt && Math.Abs(paused - at) <= Dt && s3.Count == 3 && s4.Count == 3 && h4 == h1,
                $"3x 倍速（每帧 3 步）空爆在游戏时间 {fast:0.000} 秒、中途暂停 90 帧后 {paused:0.000} 秒——都按游戏时间 0.6 秒到点；暂停版终局状态哈希与连续运行相同（{h4 == h1}）");
            // 审查修复（第 1 轮）：其余 4 个载体的 FG19A 新读法（“出手 / 脉冲 / 落区 / 无人机子弹出膛 0.6 秒后放出 3 份”）逐载体对照
            foreach ((FirmwareCarrier fc, CombatReading rd, double dist, string say) in CarrierCases())
            {
                (double on, long onSubs) = RunCarrierTimer(fc, rd, true, dist);
                (double _, long offSubs) = RunCarrierTimer(fc, rd, false, dist);
                Expect(on > 0.6 - 2 * Dt && on < 0.6 + 3 * Dt && onSubs == 3 && offSubs == 0,
                    $"{say}：出手 / 出膛后 {on:0.000} 秒放出 {onSubs} 份子载荷（读法“{Read(FwDelay, fc)}”）；不装延时引信的同一载体放 {offSubs} 份{(on > 0 ? string.Empty : "；" + _diag)}");
            }
        }

        /// <summary>格斗 / 力场 / 布区 / 无人机四个载体的夹具（射弹见上）。dist = 目标距离。</summary>
        private static IEnumerable<(FirmwareCarrier, CombatReading, double, string)> CarrierCases()
        {
            yield return (FirmwareCarrier.Melee, new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 90f }, 2.0, "格斗");
            yield return (FirmwareCarrier.Aura, new CombatReading { Carrier = CombatCarrier.Aura, Area = 3f }, 2.0, "力场");
            yield return (FirmwareCarrier.Field, new CombatReading { Carrier = CombatCarrier.Field, Area = 2f, FieldSeconds = 2f, FieldDpsRatio = 1f }, 6.0, "布区");
            yield return (FirmwareCarrier.Summon, DroneReading(), 7.0, "无人机"); // 无人机只打拴绳（8 米）内的目标
        }

        private static CombatReading DroneReading() =>
            new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 6f, DroneRatio = 1f, DroneCooldown = 2f, DroneLeash = 8f };

        /// <summary>某载体的武器：带 <paramref name="fw"/> 的真实编译程序（或不带）；无人机的程序需要飞行时实体化（慢弹速 0.3 米 / 秒：无人机贴近目标开火，0.6 秒内也不会先命中；半径 0.2、寿命系数 3）。</summary>
        private static CombatWeapon CarrierGun(CombatKernel k, string fw, FirmwareCarrier fc, CombatReading rd, bool with)
        {
            EmgProgramResult r = with ? Compile(fw, fc) : null;
            bool close = rd.Carrier == CombatCarrier.Melee || rd.Carrier == CombatCarrier.Aura;
            CombatWeapon w = Instant(r != null ? CombatSite.RegisterProgram(k, r) : -1, 10f, close ? 3f : 30f);
            w.Reading = rd;
            if (r != null && rd.Carrier == CombatCarrier.Summon && (r.Program.Flags & EmgProgramFlags.NeedsFlight) != 0)
            {
                CombatSite.ApplySpawnFlight(ref w, 0.3f, 0.2f, 3f);
            }
            return w;
        }

        /// <summary>延时引信在某载体上：返回（从出手 / 无人机子弹出膛到第一次放出子载荷的游戏时间，−1 = 没放；那一刻放出的份数）。</summary>
        private static (double, long) RunCarrierTimer(FirmwareCarrier fc, CombatReading rd, bool with, double dist)
        {
            using var k = new CombatKernel(Cfg(), 32);
            int me = Me(k, new double2(0, 0), k.AddWeapon(CarrierGun(k, FwDelay, fc, rd, with)));
            int foe = Foe(k, new double2(0, dist), 1000f);
            double launch = rd.Carrier == CombatCarrier.Summon ? -1 : k.Time;
            k.FireAt(me, foe, k.Time);
            for (int i = 0; i < 240; i++)
            {
                StepN(k, 1);
                if (launch < 0 && Projectiles(k, EmgProjFlags.ViaDrone).Count > 0)
                {
                    launch = k.Time - Dt; // 这一步里出膛
                }
                if (k.EmgBusStats.SubSpawned > 0)
                {
                    return (launch >= 0 ? k.Time - launch : -1, k.EmgBusStats.SubSpawned);
                }
            }
            if (!with)
            {
                return (-1, k.EmgBusStats.SubSpawned);
            }
            EmgProgramResult pr = Compile(FwDelay, fc);
            _diag = ($"诊断 {fc}：程序空={pr?.IsEmpty}，需要飞行={(pr != null && (pr.Program.Flags & EmgProgramFlags.NeedsFlight) != 0)}，无人机 {k.DroneCount}，出膛 {launch:0.###}，"
                 + $"弹体累计 {k.Counters.ProjectilesSpawned}，实体化 {k.EmgBusStats.FlightShots}，子载荷 {k.EmgBusStats.SubSpawned}，目标血 {Hp(k, foe):0.#}");
            return (-1, k.EmgBusStats.SubSpawned);
        }

        // ── D. 壁撞 ─────────────────────────────────────────────────────────────

        private static void CheckWallSlam()
        {
            Line("  · D. 壁撞：撞障碍反弹 + 撞击点 1.5 米冲击（50%）+ evt.impact；对照：开阔地不冲击、撞到敌人不反弹");
            EmgProgramResult r = Compile(FwWall);
            foreach (bool obstacle in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 32);
                // 在原程序根节点末尾加一条读 evt.impact 的条件行（保持原序号），用来观察撞击事件；不改原指令
                var nodes = new List<EmgNode>(r.KernelNodes);
                var instrs = new List<EmgInstr>(r.KernelInstrs);
                EmgNode root = nodes[0];
                instrs.Insert(root.FirstInstr + root.InstrCount, new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Impact), Role = 2, Ordinal = 200 });
                root.InstrCount++;
                nodes[0] = root;
                for (int i = 1; i < nodes.Count; i++)
                {
                    EmgNode n = nodes[i];
                    n.FirstInstr++;
                    nodes[i] = n;
                }
                EmgProgram prog = r.Program;
                prog.ListenMask |= 1u << (int)EmgEventKind.Impact;
                int p = k.AddProgram(prog, nodes, instrs);
                CombatWeapon w = Instant(p);
                CombatSite.ApplySpawnFlight(ref w, r, ComponentCatalog.CompGunId);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int foe = Foe(k, new double2(0, 8), 100f);
                int by = Foe(k, new double2(1.2, 3.0), 100f);
                if (obstacle)
                {
                    k.SetObstacles(new List<float3> { new float3(0f, 4f, 1f) });
                }
                long bounces0 = 0;
                k.FireAt(me, foe, k.Time);
                bool impact = false;
                EmgEvent ie = default;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    if (!impact && k.TryGetLastEmgEvent(EmgEventKind.Impact, out ie))
                    {
                        impact = true;
                    }
                }
                if (obstacle)
                {
                    Expect(impact && ie.Code == EmgCode.FaceObstacle && Near(ie.Value, 5f) && Near(Hp(k, by), 95f) && Near(Hp(k, foe), 100f) && k.EmgBusStats.ObstacleHits >= 1,
                        $"触发：弹体撞上障碍（0, 4，半径 1）反弹，撞击点 ({ie.Pos.x:0.##}, {ie.Pos.y:0.##}) 1.5 米内的敌人 {Hp(k, by):0} 血（吃 {F(ie.Value)} = 50%），evt.impact 已发；障碍后的目标未被打到");
                }
                else
                {
                    Expect(!impact && Near(Hp(k, by), 100f) && Near(Hp(k, foe), 90f) && k.ProjectileCount == bounces0,
                        $"不触发：开阔地直接命中目标（{Hp(k, foe):0} 血），没有冲击（旁边敌人 {Hp(k, by):0} 血、无 evt.impact），撞到敌人不反弹（弹体已结束，剩 {k.ProjectileCount} 发）");
                }
            }
        }

        // ── E. 追射 ─────────────────────────────────────────────────────────────

        private static void CheckFollowUp()
        {
            Line("  · E. 追射：命中后 0.1 秒从发射者补射 1 枚（50%）、只补一次；对照：目标被打死 / 发射者阵亡不补、不装不补；格斗立刻追加一记；待生成追射存读档一致");
            EmgProgramResult r = Compile(FwFollow);
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Gun(k, r)));
                int foe = Foe(k, new double2(0, 6), 100f);
                long spawned0 = k.Counters.ProjectilesSpawned;
                k.FireAt(me, foe, k.Time);
                StepN(k, 3); // 0.05 秒：还没到 0.1 秒
                bool notYet = k.Counters.ProjectilesSpawned == spawned0 && k.EmgPendingCount == 1 && Near(Hp(k, foe), 90f);
                StepN(k, 4); // 0.117 秒：已补射
                List<CombatProjectile> follow = Projectiles(k, EmgProjFlags.Flight);
                bool fromShooter = follow.Count == 1 && math.distance(follow[0].Pos, new double2(0, 0)) < 3.0 && Near(follow[0].Damage, 5f) && follow[0].Weapon < 0;
                StepN(k, 180);
                Expect(notYet && fromShooter && Near(Hp(k, foe), 85f) && k.Counters.ProjectilesSpawned - spawned0 == 1 && k.EmgPendingCount == 0,
                    $"触发：命中扣 10；0.05 秒时还没补（待生成 1 条）；0.1 秒后从发射者处补射 1 枚（伤害 {F(follow.FirstOrDefault().Damage)}）命中，目标 {Hp(k, foe):0} 血；3 秒内总共只补 1 发（追射弹不再追射）");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Gun(k, r)));
                int weak = Foe(k, new double2(0, 6), 10f);
                int me2 = Me(k, new double2(10, 0), k.AddWeapon(Gun(k, r)));
                int foe2 = Foe(k, new double2(10, 6), 100f);
                long spawned0 = k.Counters.ProjectilesSpawned;
                k.FireAt(me, weak, k.Time);
                k.FireAt(me2, foe2, k.Time);
                k.Kill(me2, 0);
                StepN(k, 60);
                Expect(Hp(k, weak) <= 0f && Near(Hp(k, foe2), 90f) && k.Counters.ProjectilesSpawned == spawned0 && k.EmgPendingCount == 0,
                    $"不触发：目标被第一发打死 → 不补射；发射者在 0.1 秒内阵亡 → 不补射（另一目标停在 {Hp(k, foe2):0} 血），待生成表清空");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(-1)));
                int foe = Foe(k, new double2(0, 6), 100f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                Expect(Near(Hp(k, foe), 90f) && k.ProjectileCount == 0, "对照：不装追射的同一武器只打一下（90 血），不补射");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                EmgProgramResult rm = Compile(FwFollow, FirmwareCarrier.Melee);
                CombatWeapon melee = Instant(CombatSite.RegisterProgram(k, rm), 10f, 3f);
                melee.Reading = new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 90f };
                int me = Me(k, new double2(0, 0), k.AddWeapon(melee));
                int foe = Foe(k, new double2(0, 2), 100f);
                int side = Foe(k, new double2(0.8, 2), 100f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                Expect(Near(Hp(k, foe), 85f) && Near(Hp(k, side), 90f) && k.ProjectileCount == 0,
                    $"格斗：命中主目标后立刻追加一记（{Hp(k, foe):0} 血），扇形里的另一个敌人只吃挥击（{Hp(k, side):0} 血）——与格斗读法“命中主目标后立刻对它追加一记”一致");
            }
            // 审查修复（第 1 轮）：力场 / 布区 / 无人机三个载体的追射读法逐条对照
            foreach (bool with in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 32);
                int me = Me(k, new double2(0, 0), k.AddWeapon(CarrierGun(k, FwFollow, FirmwareCarrier.Aura, new CombatReading { Carrier = CombatCarrier.Aura, Area = 3f }, with)));
                int foe = Foe(k, new double2(0, 2), 100f);
                int side = Foe(k, new double2(0.8, 2), 100f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                Expect(Near(Hp(k, foe), with ? 85f : 90f) && Near(Hp(k, side), 90f) && k.ProjectileCount == 0,
                    with ? $"力场：脉冲命中主目标后立刻追加一记（{Hp(k, foe):0} 血 = 10 + 50%），圈里另一个敌人只吃脉冲（{Hp(k, side):0} 血）——与读法“{Read(FwFollow, FirmwareCarrier.Aura)}”一致"
                         : $"力场对照：不装追射只吃脉冲（{Hp(k, foe):0} / {Hp(k, side):0} 血）");
            }
            foreach (bool with in new[] { true, false })
            {
                // 主目标 10 血被落区那一下打死（它的追射作废）；旁边的敌人只吃落区节拍 = 每拍 Dps × 节拍 = 10 × 0.5 = 5，追射 = 这一拍 × 50% = 2.5（不是每秒伤害 × 50%）
                using var k = new CombatKernel(Cfg(), 32);
                int me = Me(k, new double2(0, 0), k.AddWeapon(CarrierGun(k, FwFollow, FirmwareCarrier.Field,
                    new CombatReading { Carrier = CombatCarrier.Field, Area = 2f, FieldSeconds = 2f, FieldDpsRatio = 1f }, with)));
                int main = Foe(k, new double2(0, 6), 10f);
                int by = Foe(k, new double2(1.2, 6), 200f);
                k.FireAt(me, main, k.Time);
                var drops = new List<float>();
                float last = Hp(k, by);
                for (int i = 0; i < 150; i++)
                {
                    StepN(k, 1);
                    float now = Hp(k, by);
                    if (last - now > 1e-3f)
                    {
                        drops.Add(last - now);
                    }
                    last = now;
                }
                const float tick = 5f;
                bool allKnown = drops.Count > 0 && drops.All(x => Near(x, tick) || Near(x, tick * 0.5f) || Near(x, tick * 1.5f));
                bool anyFollow = drops.Any(x => Near(x, tick * 0.5f) || Near(x, tick * 1.5f));
                Expect(Hp(k, main) <= 0f && allKnown && anyFollow == with,
                    $"布区{(with ? string.Empty : "对照（不装追射）")}：旁边敌人每帧掉血 {string.Join("/", drops.Select(F))}——节拍 {F(tick)}，"
                    + (with ? $"落区命中后立刻追加 {F(tick * 0.5f)}（这一拍 × 50%），与读法“{Read(FwFollow, FirmwareCarrier.Field)}”一致" : "没有追加"));
            }
            foreach (bool with in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 32);
                int me = Me(k, new double2(0, 0), k.AddWeapon(CarrierGun(k, FwFollow, FirmwareCarrier.Summon, DroneReading(), with)));
                int foe = Foe(k, new double2(0, 6), 1000f);
                k.FireAt(me, foe, k.Time);
                CombatProjectile first = default;
                bool seen = false;
                double hitAt = -1, firedAt = -1;
                float hp0 = Hp(k, foe);
                for (int i = 0; i < 150 && !seen; i++)
                {
                    StepN(k, 1);
                    if (hitAt < 0 && Hp(k, foe) < hp0)
                    {
                        hitAt = k.Time;
                    }
                    List<CombatProjectile> f = Projectiles(k, EmgProjFlags.Flight).Where(p => (p.Flags & EmgProjFlags.ViaDrone) == 0 && p.Weapon < 0).ToList();
                    if (f.Count > 0)
                    {
                        (first, seen, firedAt) = (f[0], true, k.Time);
                    }
                }
                double fromMother = seen ? math.distance(first.Pos, new double2(0, 0)) : -1;
                Expect(with ? seen && fromMother < 2.0 && Near(first.Damage, 5f) && hitAt > 0 && Math.Abs(firedAt - hitAt - 0.1) <= 2 * Dt : !seen && hitAt > 0,
                    with ? $"无人机：无人机子弹命中（{hitAt:0.000} 秒）后 {firedAt - hitAt:0.000} 秒从母机处（距母机 {fromMother:0.##} 米）补射 1 枚追射弹（伤害 {F(first.Damage)} = 50%）——与读法“{Read(FwFollow, FirmwareCarrier.Summon)}”一致"
                         : $"无人机对照：不装追射，无人机照常命中（{hitAt:0.000} 秒）但没有追射弹");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Gun(k, r)));
                int foe = Foe(k, new double2(0, 6), 100f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int pending = k.EmgPendingCount;
                byte[] snap = k.Serialize();
                using var b = new CombatKernel(Cfg(), 32);
                bool loaded = b.Load(snap) == CombatLoadResult.Ok && b.StateHash() == k.StateHash() && b.EmgPendingCount == pending;
                bool every = true;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    StepN(b, 1);
                    every &= k.StateHash() == b.StateHash();
                }
                Expect(pending == 1 && loaded && every && Near(Hp(b, foe), 85f) && Near(Hp(k, foe), 85f),
                    $"存读档：追射待生成（1 条）中途存档 → 新内核读档逐项相同，续跑 90 步每步状态哈希一致，两边都只补 1 发（{Hp(b, foe):0} 血）");
            }
        }

        // ── F. 磁聚（过渡文案）─────────────────────────────────────────────────

        private static void CheckMagnet()
        {
            Line("  · F. 磁聚（阶段 1 过渡）：真实装配编译的连射器 + 磁聚——命中点附近的敌人被吸近一小段、目标挂充能与磁化；对照：远处的敌人不动");
            CampaignState s = NewState(9301);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { FwMagnet });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            using var k = new CombatKernel(Cfg(), 32);
            int me = Me(k, new double2(0, 0), k.AddWeapon(w));
            int foe = Foe(k, new double2(0, 6), 300f);
            int nearFoe = Foe(k, new double2(3, 6), 300f);
            int farFoe = Foe(k, new double2(0, 13), 300f);
            CombatFireResult fr = k.FireAt(me, foe, k.Time);
            StepN(k, 2);
            double moved = 3.0 - math.distance(Pos(k, nearFoe), new double2(0, 6));
            double farMoved = math.distance(Pos(k, farFoe), new double2(0, 13));
            k.TryGetStatus(foe, out uint mask, out _, out _, out _, out _);
            uint want = NamedReactionCatalog.BitOf("Charged") | NamedReactionCatalog.BitOf("Magnet");
            Expect(s != null && moved > 0.3 && moved < 1.5 && farMoved < 1e-6 && want != 0u && (mask & want) == want,
                $"开火 {fr}：命中点 3 米处的敌人被吸近 {moved:0.##} 米，7 米外的敌人不动（{farMoved:0.###}）；目标挂上充能 + 磁化（状态位 {mask & want:X}）——与过渡描述一致");
            Expect(EmgProgramTreeText.PortsLine(FwMagnet).Contains(GameText.Get("emg.ports.line_pending").Split('：')[0])
                   && !EmgProgramTreeText.PortsLine(FwSplit).Contains(GameText.Get("emg.ports.line_pending").Split('：')[0]),
                $"端口行：磁聚的磁场端口本版本不生效，详情 / 三选一写“{EmgProgramTreeText.PortsLine(FwMagnet)}”；分裂照常“{EmgProgramTreeText.PortsLine(FwSplit)}”");
            string inactive = GameText.Get("emg.ports.line_inactive").Split('：')[0];
            Expect(EmgProgramTreeText.PortsLine("fw_enterfuse")?.StartsWith(inactive, StringComparison.Ordinal) == true && !EmgProgramTreeText.PortsLine(FwMagnet).StartsWith(inactive, StringComparison.Ordinal),
                $"端口行：没有旧读法、门槛未到的新固件写“当前不生效”（{EmgProgramTreeText.PortsLine("fw_enterfuse")}），不写“按原读法结算”");
        }

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("emgfix-" + seed, "Standard", seed);
            CampaignSession.Set(SessionSlot, s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            return s;
        }

        // ── G. 图鉴 / 详情 / 三选一同步、旧存档预览说明 ───────────────────────────

        private static void CheckDisplaySync()
        {
            Line("  · G. 图鉴、固件详情页、三选一卡面与描述 / 声明同源；旧存档蓝图里的固件重编译后预览写明“已按新机制运行”");
            CampaignState s = NewState(9302);
            var bad = new List<string>();
            foreach (string fw in new[] { FwSplit, FwDelay, FwWall, FwFollow, FwMagnet })
            {
                string detail = FirmwareLibrary.BuildDetail(s, fw);
                MechanicCodexEntry entry = MechanicCodex.Find(MechanicCodex.FirmwareEntryId(fw));
                string codex = entry != null ? MechanicCodex.Body(entry, s) : null;
                var cand = new LootCandidateRecord { Kind = LootPickCatalog.KindFirmware, TargetId = fw, Tier = "rare" };
                string name = LootPickService.CandidateName(cand);
                string cmp = LootPickService.CompareText(s, cand);
                string ports = EmgProgramTreeText.PortsLine(fw);
                if (!detail.Contains(Desc(fw)) || codex != detail || !name.Contains(FirmwareKinds.DisplayName(fw)) || ports == null || !cmp.Contains(ports)
                    || cmp.Contains("两枚") || detail.Contains("先飞一段") || detail.Contains("撞到墙体和地形") || detail.Contains("立刻补一发") || detail.Contains("弹道被拉向一点"))
                {
                    bad.Add(fw);
                }
            }
            Expect(bad.Count == 0, $"5 条固件：详情页含当前描述、图鉴正文 = 详情页（同一函数）、三选一卡面名称与端口行同源，旧说法都不再出现{(bad.Count > 0 ? "；不符：" + string.Join("、", bad) : string.Empty)}");

            var rows = new List<EmgTreeRow>();
            var migrated = new List<string>();
            foreach (string fw in new[] { FwDelay, FwWall, FwFollow })
            {
                EmgProgramTreeText.BuildRows(Compile(fw), rows);
                string change = GameText.Get(EmgProgramTreeText.MigratedKey(fw));
                if (rows.Count(x => x.Kind == EmgTreeRowKind.Migrated && x.Text.Contains(change)) == 1)
                {
                    migrated.Add(fw);
                }
            }
            // 审查修复（第 1 轮）：行为没变的不写——分裂只改描述、广角只改数值（旧字段折算）、冷却液保留过渡字段、磁聚未进程序、新固件没有旧读法
            var wrongly = new List<string>();
            foreach (string fw in new[] { FwSplit, "fw_wide", "fw_coolant", FwMagnet, "fw_scuttle" })
            {
                EmgProgramTreeText.BuildRows(Compile(fw), rows);
                if (rows.Any(x => x.Kind == EmgTreeRowKind.Migrated))
                {
                    wrongly.Add(fw);
                }
            }
            bool oldOnDecl = !Compile(FwSplit).IsEmpty && !Compile("fw_wide").IsEmpty && !Compile("fw_coolant").IsEmpty;
            string cd = GameText.Get(EmgProgramTreeText.MigratedKey(FwDelay)), cf = GameText.Get(EmgProgramTreeText.MigratedKey(FwFollow)), cw = GameText.Get(EmgProgramTreeText.MigratedKey(FwWall));
            bool numbers = cd.Contains("0.6 秒") && cd.Contains("3 枚") && cf.Contains("0.1 秒") && cf.Contains("50%") && cw.Contains("1.5 米") && cw.Contains("50%");
            Expect(migrated.Count == 3 && wrongly.Count == 0 && oldOnDecl && numbers,
                $"程序树预览：真正改了机制的 3 条（延时引信 / 壁撞 / 追射）重编译后各一行“已按新机制运行”并写明新旧差别（数字与声明同源）；"
                + $"已进程序但行为没变的分裂 / 广角 / 冷却液、未进程序的磁聚、没有旧读法的新固件都不写{(wrongly.Count > 0 ? "；误写：" + string.Join("、", wrongly) : string.Empty)}");
        }

        // ── H. emg.stage = 0 不受影响 ──────────────────────────────────────────

        private static void CheckStageZero()
        {
            Line("  · H. emg.stage = 0（回滚开关）：五条固件都不进程序、照旧走原读法，预览没有“已按新机制运行”");
            UseStage(0);
            try
            {
                bool allEmpty = new[] { FwSplit, FwDelay, FwWall, FwFollow, FwMagnet }.All(fw => Compile(fw).IsEmpty && !EmgCatalog.IsOwnerOnDeclTrack(fw));
                var rows = new List<EmgTreeRow>();
                EmgProgramTreeText.BuildRows(Compile(FwFollow), rows);
                Expect(allEmpty && rows.All(x => x.Kind != EmgTreeRowKind.Migrated), "阶段 0：程序为空、都不在声明轨（旧读法照旧），预览无新机制说明");
            }
            finally
            {
                UseStage(1);
            }
        }

        // ── 报告 ─────────────────────────────────────────────────────────────

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
