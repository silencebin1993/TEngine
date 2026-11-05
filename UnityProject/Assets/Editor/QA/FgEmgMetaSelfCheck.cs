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
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.Grid;
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
    /// EMG3-META-01 元程序六条指令的自动验收（FG19 FGR-EMG-017、040～043；ADR-EMG-001 ②⑩；FGT-EMG-052、FGT-EMG-014 元程序部分）。
    /// 起真实编译器（EmgProgramCompiler，真实表 + FG19A N01～N08 的测试固件行：正式固件由 EMG3-FW-02 落表）、真实预览（EmgProgramTreeText）、真实登记（CombatSite.RegisterProgram）
    /// 与真实内核（CombatKernel）断言行为：A 编译（下游段 / 执行次数 / 上限原因 / 热量 / 逆序树 / 2 代节点 / 目录与模糊测试器缺口）；B [若目标被标记, 复制, 分裂] 两组子弹药；
    /// C 条件为假下游修饰不生效；D 复制各载体与上限；E 随机（确定性、每次选 1 条）；F 延后（宿主结束在结束点、宿主还在取当时位置、累计超上限不执行、暂停）；
    /// G 循环（飞行沿途、宿主提前结束、≤ 8 次、格斗锚点、布区存续）；H 反转（按本机积热选树）；I 存读档（延后队列与循环计数一致、续跑哈希一致；I3 段 23 短记录）；
    /// L 5 载体读法补全（无人机 / 布区 / 当场结算的延后位置 / 力场多命中）；M 条件读端六类各一正一反；J 空场景早退出与发现；K 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgMetaSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgMetaSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgMetaSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger, Cond = EmgConst.RoleCondition, RoleEmit = 5, RoleModify = 6, RoleSpawn = 7;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        // EMG3-FW-02：FG19A N01～N08 已落表（stageGate 3），自检改用正式固件行（FGT-EMG-052 用正式固件复跑）
        private const string IfMark = "fw_ifmark", Rep = "fw_repeat", Loop = "fw_loop", Rnd = "fw_random";
        private const string Rev = "fw_reverse", Def = "fw_defer", IfHot = "fw_ifhot";
        private const string Split = "fw_split", Pierce = "fw_pierce", Tractor = "fw_tractor", Boomerang = "fw_boomerang";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/元程序")]
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
            Line("\n[元程序] EMG3-META-01：复制 / 循环 / 条件 / 随机 / 反转 / 延后（FGT-EMG-052、FGT-EMG-014 元程序部分）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程（Editor 下 Mono / Burst，真机 IL2CPP，耗时只作量级参考）");
            GameLanguage lang = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                CarrierReadings.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Step(CheckFuzzGap); // 真实表：EMG3-FW-02 落表后 meta.* 缺口为 0
                UseMetaCatalog();
                Step(CheckCompile);
                Step(CheckHeadline);
                Step(CheckConditionMods);
                Step(CheckRepeat);
                Step(CheckRandom);
                Step(CheckDefer);
                Step(CheckLoop);
                Step(CheckReverse);
                Step(CheckSaveLoad);
                Step(CheckTiming);
                Step(CheckShortRecord);
                Step(CheckCarriers);
                Step(CheckCondPorts);
                Step(CheckEmptyAndDiscovery);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"元程序自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [元程序] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 测试目录：真实表 + FG19A N01～N08 的测试固件行（读端照 FG19A 第 6 节；正式落表归 EMG3-FW-02）──

        private static void UseMetaCatalog()
        {
            // EMG3-FW-02：不再加测试行——正式表的 N01～N08 声明与原测试行同形（FG19A 第 6 节读端）
            EmgSource src = EmgCatalog.FromTables();
            string[] tests = { IfMark, Rep, Loop, Rnd, Rev, Def, IfHot };
            EmgCatalog.OverrideForTests(src);
            EmgCompiled c = EmgCatalog.Current;
            string[] need = tests.Concat(new[] { Split, Pierce, Tractor, Boomerang }).ToArray();
            string[] inactive = need.Where(o => !c.OwnerIndex.TryGetValue(o, out int oi) || !c.OwnerActive(oi)).ToArray();
            Expect(c.Problems.Count == 0 && inactive.Length == 0 && c.EffectiveStage == 3,
                $"正式表：7 条元程序正式固件（N01、N02、N04～N08，stageGate 3）零问题{(c.Problems.Count > 0 ? "：" + c.Problems[0] : string.Empty)}；元程序 owner 读 prop.heat 条件也走声明轨（meta.* 已接通，条件由元程序求值）"
                + (inactive.Length > 0 ? "；未生效：" + string.Join("、", inactive) : string.Empty));
        }

        private static EmgProgramResult P(params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, 0);

        private static EmgProgramResult Pc(int cap, FirmwareCarrier car, params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, car, cap);

        private static string Short(string id) => id.StartsWith("fw_selftest_meta_", StringComparison.Ordinal) ? id.Substring(17) : id.StartsWith("fw_", StringComparison.Ordinal) ? id.Substring(3) : id;

        private static string Tree(EmgProgramResult r) => string.Join(" | ", r.Nodes.Select(n =>
            $"{n.Index}{(n.Parent >= 0 ? "<" + n.Parent : string.Empty)}:" + string.Concat(n.Entries.Select(e => " " + (e.Inherited ? "^" : string.Empty) + Short(r.Steps[e.Ordinal].OwnerId)))));

        private static bool HasNote(EmgProgramResult r, string key) => r.Notes.Any(n => n.Key == key);

        // ── A. 编译与预览（FGR-EMG-038、040～042）──

        private static void CheckCompile()
        {
            Line("  · A. 编译：下游段、执行次数、上限原因、热量（FGR-EMG-042）、逆序树、2 代节点、预览文本");
            EmgProgramResult r = P(IfMark, Rep, Split);
            EmgMetaInfo mi = r.Metas.FirstOrDefault(m => m.Kind == EmgMetaConst.If), mr = r.Metas.FirstOrDefault(m => m.Kind == EmgMetaConst.Repeat);
            EmgNode child = r.KernelNodes.Count > 1 ? r.KernelNodes[1] : default;
            Expect(Tree(r) == "0: ifmark repeat split | 1<0:" && r.Metas.Count == 2 && mi.Down != null && mi.Down.SequenceEqual(new[] { 2 }) && mr.Down != null && mr.Down.SequenceEqual(new[] { 2 })
                   && mr.Exec == 2 && (r.Program.Flags & EmgProgramFlags.HasMeta) != 0 && child.MetaMask == 3 && r.Reverse == null,
                $"[若目标被标记, 复制, 分裂]：树 {Tree(r)}；条件 / 复制的下游段都是分裂（序号 2），复制 ×{mr.Exec}；程序带 HasMeta；子节点 MetaMask = 触发器序号 + 1 = {child.MetaMask}");
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows);
            EmgTreeRow repRow = rows.FirstOrDefault(x => x.Meta && x.Ordinal == 1);
            Expect(rows.Count(x => x.Meta) == 2 && repRow.Text != null && repRow.Text.Contains("下游 1 条") && repRow.Text.Contains("×2") && repRow.Text.Contains(EmgProgramTreeText.StepName(r.Steps[2])),
                $"预览：元程序行写明作用的下游段与执行次数（“{repRow.Text}”）");

            EmgProgramResult n3 = P(Rep, Rep, Rep, Split);
            int[] execs = n3.Metas.Select(m => m.Exec).ToArray();
            var rows3 = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(n3, rows3);
            Expect(execs.SequenceEqual(new[] { 2, 4, 4 }) && n3.Metas[2].Valid == false && HasNote(n3, EmgProgramCompiler.NoteMetaExecCap)
                   && rows3.Any(x => x.Kind == EmgTreeRowKind.Reason && x.NoteKey == EmgProgramCompiler.NoteMetaExecCap && x.Text.Contains("4 次")) && RowA(n3, 0, 2) == 0f && RowA(n3, 0, 1) == 1f,
                $"负向：嵌套复制相乘超过 4——执行次数 {string.Join("/", execs)}，第 3 层不生效（内核行 A = 0），预览原因 emg.compile.meta_exec_cap");

            // 热量（FGR-EMG-042）：复制段 ×（次数 − 1）；循环 ×(2 − 1)；随机按平均值；条件 / 延后不加
            // 随机段：下游两条都在根节点（贯穿、牵引都不是触发器；放在分裂后面会被装进分裂的子弹药，不在同一段）
            EmgProgramResult plain = P(Split), rep = P(Rep, Split), loop = P(Loop, Split), cond = P(IfMark, Split), def = P(Def, Split), rnd = P(Rnd, Pierce, Tractor);
            float seg = plain.HeatTotal; // 分裂这一段（指令 + 触发节点 + 子载荷份数）的热量
            float pierceH = P(Pierce).HeatTotal, tractorH = P(Tractor).HeatTotal;
            float rndExpect = (pierceH + tractorH) * 0.5f - (pierceH + tractorH); // 两条按平均值计：比全执行少一半
            Expect(Near(rep.HeatMeta, seg) && Near(loop.HeatMeta, seg) && cond.HeatMeta == 0f && def.HeatMeta == 0f && Near(rnd.HeatMeta, rndExpect)
                   && Near(rep.HeatTotal, rep.HeatInstr + rep.HeatDecl + rep.HeatTrigger + rep.HeatChildren + rep.HeatMeta) && Near(rep.Program.HeatPerShot, rep.HeatTotal),
                $"热量：分裂段 {seg:0.#}；复制 +{rep.HeatMeta:0.#}、循环 +{loop.HeatMeta:0.#}、条件 {cond.HeatMeta}、延后 {def.HeatMeta}、随机（贯穿 / 牵引取平均，树 {Tree(rnd)}）{rnd.HeatMeta:0.##}（期望 {rndExpect:0.##}）；每发热量含元程序加成");

            // 反转：编译期两棵树，逆序树里牵引 ↔ 推斥、回拉 → 前冲；两棵树都计入签名
            EmgProgramResult rv = P(Rev, Split, Pierce), fw = P(Split, Pierce);
            ushort pullCode = PortCode("fld.pull"), pushCode = PortCode("fld.push"), retCode = PortCode("grm.mod.return"), pierceCode = PortCode("grm.mod.pierce");
            EmgProgramResult rvf = P(Rev, Tractor, Boomerang);
            bool flipped = rvf.Reverse != null && rvf.Reverse.KernelInstrs.Any(x => x.Port == pushCode) && !rvf.Reverse.KernelInstrs.Any(x => x.Port == pullCode)
                           && rvf.Reverse.KernelInstrs.Any(x => x.Port == pierceCode) && !rvf.Reverse.KernelInstrs.Any(x => x.Port == retCode)
                           && rvf.KernelInstrs.Any(x => x.Port == pullCode) && rvf.KernelInstrs.Any(x => x.Port == retCode);
            var rowsR = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(rv, rowsR);
            Expect(rv.Reverse != null && Tree(rv) == "0: reverse split | 1<0: pierce" && Tree(rv.Reverse) == "0: reverse pierce split | 1<0: ^pierce" && flipped
                   && rv.Signature != fw.Signature && rowsR.Any(x => x.Kind == EmgTreeRowKind.ReverseTitle),
                $"反转：正序树 {Tree(rv)}，逆序树 {(rv.Reverse != null ? Tree(rv.Reverse) : "无")}（谁被装进子弹药改变）；逆序树里牵引→推斥、回拉→前冲（贯穿）；预览列出逆序树");

            // 2 代节点上的元程序：容量 1、没有下游段 → 不起作用，预览写原因
            EmgProgramResult g2 = P(Split, Split, Rep);
            Expect(Tree(g2) == "0: split | 1<0: split | 2<1: repeat" && HasNote(g2, EmgProgramCompiler.NoteMetaEmpty) && g2.Metas.Count == 1 && g2.Metas[0].Down.Count == 0,
                $"负向：元程序位于 2 代节点（{Tree(g2)}）——没有下游段，预览原因 emg.compile.meta_empty");

            // 一条链累计延后：祖先罩住触发器的延后 + 本节点内的延后 > 3 秒 → 不执行（A = 0），预览写原因
            EmgProgramResult dc = Pc(6, FirmwareCarrier.Projectile, Def, Def, Def, Split, Def, Split);
            EmgMetaInfo deep = dc.Metas.FirstOrDefault(m => m.Node == 1);
            Expect(dc.Metas.Count == 4 && dc.Metas.Where(m => m.Node == 0).All(m => m.Valid) && deep.Node == 1 && !deep.Valid && Near(deep.Delay, 4f)
                   && HasNote(dc, EmgProgramCompiler.NoteMetaDeferCap) && RowA(dc, 1, deep.Ordinal) == 0f,
                $"负向：一条链上累计延后 {deep.Delay:0.#} 秒 > 3 秒（根 3 层罩住分裂 + 子弹药 1 层）——这一段不执行，预览原因 emg.compile.meta_defer_cap");
            Expect(EmgProgramTreeText.ReasonKeys.Contains(EmgProgramCompiler.NoteMetaExecCap) && EmgProgramTreeText.ReasonKeys.All(k => GameText.Has(k) && GameText.Has(EmgProgramTreeText.FixKey(k)))
                   && new[] { "emg.tree.tag.meta", "emg.tree.meta.repeat", "emg.tree.meta.loop", "emg.tree.meta.if", "emg.tree.meta.random", "emg.tree.meta.reverse", "emg.tree.meta.defer", "emg.tree.reverse_title", "emg.tree.heat_meta", "reaction.discovery.meta" }.All(GameText.Has),
                "文本键：三条元程序原因 + 解决办法、预览行与发现文本都有中文（新文本走文本键）");
        }

        private static float RowA(EmgProgramResult r, int node, int ordinal)
        {
            EmgNode n = r.KernelNodes[node];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal == ordinal && EmgMetaConst.KindOf(x.Port) >= 0)
                {
                    return x.A;
                }
            }
            return -1f;
        }

        private static ushort PortCode(string id) => EmgCatalog.Current.PortIdByCode.First(kv => kv.Value == id).Key;

        // ── 内核夹具 ──

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgSiteSeed = 12345u;
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

        private static CombatWeapon Instant(int program, float range = 30f, bool heat = false, CombatCarrier carrier = CombatCarrier.Projectile) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = 10f, Cooldown = 0f, Program = program,
            HeatPerShot = heat ? 10f : 0f, OverheatAt = heat ? 15f : 0f, RecoverBelow = heat ? 4f : 0f, Dissipation = heat ? 1f : 0f,
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

        private static List<CombatZone> Zones(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        /// <summary>排放形成的驻留区（没有程序的区域；布区落区 / 子区带程序，不算）。</summary>
        private static int Pools(CombatKernel k) => Zones(k).Count(z => z.EmgProgRef == 0);

        private static List<CombatProjectile> Projs(CombatKernel k)
        {
            var list = new List<CombatProjectile>();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p))
                {
                    list.Add(p);
                }
            }
            return list;
        }

        private static bool Near(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) <= eps;

        // 手写程序树（与编译器同一形状）：一条指令 = 同一序号的若干行；子节点记放出它的触发器序号（MetaMask）
        private sealed class N
        {
            public int Parent = -1;
            public EmgEventKind On;
            public byte Count = 1;
            public int TrigOrd = -1;
            public readonly List<EmgInstr> Rows = new List<EmgInstr>();

            public N Add(params EmgInstr[] rows)
            {
                Rows.AddRange(rows);
                return this;
            }
        }

        private static ushort Ev(EmgEventKind k) => (ushort)(EmgConst.EventPortBase + (int)k);

        private static EmgInstr Row(int ord, ushort port, byte role, float amount = 1f, float a = 0f, float b = 0f, byte verb = 0, EmgInstrFlags flags = EmgInstrFlags.None) =>
            new EmgInstr { Ordinal = (byte)ord, Port = port, Role = role, Amount = amount, A = a, B = b, Verb = verb, Flags = flags };

        /// <summary>元程序指令：写端 A = 1 生效 / 0 超上限；可带事件触发读端与条件读端。</summary>
        private static EmgInstr[] MetaI(int ord, byte kind, EmgEventKind trig = EmgEventKind.None, ushort cond = 0, float condAmt = 0.5f, float a = 1f)
        {
            var rows = new List<EmgInstr>();
            if (trig != EmgEventKind.None)
            {
                rows.Add(Row(ord, Ev(trig), Trig));
            }
            if (cond != 0)
            {
                rows.Add(Row(ord, cond, Cond, condAmt));
            }
            rows.Add(Row(ord, (ushort)(EmgConst.PortMetaBase + kind), RoleModify, 1f, a));
            return rows.ToArray();
        }

        private static EmgInstr[] TrigI(int ord, EmgEventKind on) => new[] { Row(ord, Ev(on), Trig), Row(ord, 100, RoleSpawn, 1f, 0f, 0f, (byte)EmgVerb.SubPayload) };

        /// <summary>根节点的内核排放（阶段 1 过渡：驻留区，半径 1.5 米、6 秒），在 <paramref name="on"/> 事件处执行。</summary>
        private static EmgInstr[] EmitI(int ord, EmgEventKind on) =>
            new[] { Row(ord, Ev(on), Trig), Row(ord, (ushort)(EmgMatterConst.PortBase + EmgMatterConst.Oil), RoleEmit, 1f, 1.5f, 6f, (byte)EmgCatalog.VerbCode("emit"), EmgInstrFlags.RootKernel) };

        private static EmgInstr[] PierceI(int ord) => new[] { Row(ord, (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Hit), 3), Row(ord, 111, RoleModify, 1f, 1f, 0f, (byte)EmgVerb.PassThrough) };

        private static int Tree(CombatKernel k, params N[] spec)
        {
            var nodes = new List<EmgNode>();
            var instrs = new List<EmgInstr>();
            uint mask = 0u;
            for (int j = 0; j < spec.Length; j++)
            {
                N s = spec[j];
                nodes.Add(new EmgNode
                {
                    Parent = (sbyte)(j == 0 ? -1 : s.Parent), Gen = j == 0 ? (byte)0 : (byte)(nodes[s.Parent].Gen + 1), TrigEvent = j == 0 ? default : s.On, TrigCount = j == 0 ? (byte)0 : s.Count,
                    FirstInstr = instrs.Count, InstrCount = (byte)s.Rows.Count, MetaMask = (byte)(j == 0 ? 0 : s.TrigOrd + 1),
                });
                foreach (EmgInstr x in s.Rows)
                {
                    instrs.Add(x);
                    if (x.Role >= Trig && x.Role <= EmgConst.RoleLastRead && x.Port > EmgConst.EventPortBase && x.Port <= EmgConst.EventPortBase + EmgConst.EventKinds)
                    {
                        mask |= 1u << (x.Port - EmgConst.EventPortBase);
                    }
                }
                mask |= j > 0 ? 1u << (int)s.On : 0u;
            }
            return k.AddProgram(new EmgProgram { ListenMask = mask | (1u << (int)EmgEventKind.Fire), Flags = EmgProgramFlags.HasMeta }, nodes, instrs);
        }

        private static N Root(params EmgInstr[][] steps)
        {
            var n = new N();
            foreach (EmgInstr[] s in steps)
            {
                n.Add(s);
            }
            return n;
        }

        private static N Child(int trigOrd, EmgEventKind on = EmgEventKind.Hit, byte count = 1) => new N { Parent = 0, On = on, Count = count, TrigOrd = trigOrd };

        // ── B. [若目标被标记, 复制, 分裂]（卡片“玩家能看到”）──

        private static void CheckHeadline()
        {
            Line("  · B. 正式编译 + 登记：[若目标被标记, 复制, 分裂] 命中已标记目标放两组子弹药（第二组 0.1 秒后、同位置；FGR-EMG-041 例），未标记时条件为假、下游段（复制 + 分裂）整段跳过（FGR-EMG-040）");
            EmgProgramResult r = P(IfMark, Rep, Split);
            long marked = HeadlineRun(r, true, out long firstGroup, out double2 pos0, out double2 pos1), unmarked = HeadlineRun(r, false, out _, out _, out _);
            Expect(marked == 6 && firstGroup == 3 && unmarked == 0 && math.distance(pos0, pos1) < 0.6,
                $"已标记：先放 {firstGroup} 份、0.1 秒后再放一组，共 {marked} 份（两组在同一命中点，相距 {math.distance(pos0, pos1):0.00} 米）；未标记：{unmarked} 份（条件为假，整段跳过，分裂也不放）");
        }

        private static long HeadlineRun(EmgProgramResult r, bool mark, out long firstGroup, out double2 pos0, out double2 pos1)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k, r);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
            Unit(k, CombatFaction.Hostile, new double2(2, 5), -1, 500f);
            if (mark)
            {
                k.SetMarkedUntil(foe, k.Time + 30.0);
            }
            k.FireAt(me, foe, k.Time);
            StepN(k, 2);
            firstGroup = Subs(k);
            pos0 = Projs(k).Where(p => (p.Flags & EmgProjFlags.Sub) != 0).Select(p => p.Prev).DefaultIfEmpty(new double2(99, 99)).First();
            pos1 = pos0;
            for (int i = 0; i < 12 && Subs(k) == firstGroup; i++)
            {
                StepN(k, 1);
            }
            List<CombatProjectile> late = Projs(k).Where(p => (p.Flags & EmgProjFlags.Sub) != 0).OrderByDescending(p => p.Id).ToList();
            if (late.Count > 0)
            {
                pos1 = late[0].Prev;
            }
            StepN(k, 30);
            return Subs(k);
        }

        // ── C. 条件为假时下游修饰不生效 ──

        private static void CheckConditionMods()
        {
            Line("  · C. 负向：条件为假时下游修饰不生效（[若目标被标记, 贯穿] 飞行弹体：未标记不带穿透，已标记带穿透；FGR-EMG-040“对修饰”列）");
            EmgProgramResult r = P(IfMark, Pierce);
            byte off = PierceOf(r, false), on = PierceOf(r, true);
            Expect((r.Program.Flags & EmgProgramFlags.NeedsFlight) != 0 && off == 0 && on == 1, $"未标记目标：剩余穿透 {off}；已标记目标：剩余穿透 {on}");
        }

        private static byte PierceOf(EmgProgramResult r, bool mark)
        {
            using var k = new CombatKernel(Cfg(), 8);
            int prog = CombatSite.RegisterProgram(k, r);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 20f, 2f)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 10), -1, 500f);
            if (mark)
            {
                k.SetMarkedUntil(foe, k.Time + 30.0);
            }
            k.FireAt(me, foe, k.Time);
            StepN(k, 1);
            CombatProjectile pr = Projs(k).FirstOrDefault(p => (p.Flags & EmgProjFlags.Flight) != 0);
            return pr.Id != 0 ? pr.PierceLeft : (byte)255;
        }

        // ── D. 复制：各载体、上限、效果多结算一次 ──

        private static void CheckRepeat()
        {
            Line("  · D. 复制（FGR-EMG-043 复制列）：射弹命中点 / 格斗与力场出手处 0.1 秒后再结算；嵌套 ×2 相乘 ≤ emg.meta.max_exec；效果多结算一次（FGT-EMG-014：唯一的同键多次结算）");
            foreach (CombatCarrier car in new[] { CombatCarrier.Projectile, CombatCarrier.Melee, CombatCarrier.Aura })
            {
                long one = RepeatRun(car, 0), two = RepeatRun(car, 1), four = RepeatRun(car, 3);
                Expect(one == 1 && two == 2 && four == 4, $"{car}：无复制 {one} 组、一层复制 {two} 组、三层复制（第 3 层内核也按 ≤ 4 截断）{four} 组");
            }
            long capped = RepeatRun(CombatCarrier.Projectile, 2, 2);
            Expect(capped == 2, $"emg.meta.max_exec = 2 时两层复制只执行 {capped} 次（超出部分不执行）");
            using (var k = new CombatKernel(Cfg(), 8))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Repeat, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 2);
                int now = Pools(k);
                StepN(k, 10);
                Expect(now == 1 && Pools(k) == 2, $"[复制, 命中排放]：命中当场排 {now} 摊、0.1 秒后同位置再排 1 摊（共 {Pools(k)}）——同一条排放结算两次只有复制能做到");
            }
        }

        private static long RepeatRun(CombatCarrier car, int layers, int maxExec = 0)
        {
            CombatConfig cfg = Cfg();
            cfg.EmgMetaMaxExec = maxExec;
            using var k = new CombatKernel(cfg, 16);
            var steps = new List<EmgInstr[]>();
            for (int i = 0; i < layers; i++)
            {
                steps.Add(MetaI(i, EmgMetaConst.Repeat, EmgEventKind.Hit));
            }
            steps.Add(TrigI(layers, EmgEventKind.Hit));
            int prog = Tree(k, Root(steps.ToArray()), Child(layers));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, car == CombatCarrier.Projectile ? 30f : 3f, false, car)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.6), -1, 500f);
            k.FireAt(me, foe, k.Time);
            StepN(k, 40);
            return Subs(k);
        }

        // ── E. 随机 ──

        private static void CheckRandom()
        {
            Line("  · E. 随机（FGR-EMG-040）：每次结算只执行下游段里的 1 条；无状态哈希按地点种子，确定性（同种子同结果、换种子两条都会被选中）");
            var picks = new HashSet<int>();
            bool oneEach = true, same = true;
            for (uint seed = 1; seed <= 24; seed++)
            {
                int a = RandomRun(seed, out long subsA), b = RandomRun(seed, out _);
                oneEach &= subsA == 1;
                same &= a == b;
                picks.Add(a);
            }
            Expect(oneEach && same && picks.SetEquals(new[] { 1, 2 }), $"24 个地点种子：每次只放 1 份（{(oneEach ? "全部" : "有不是")}），同种子两次运行选中同一条（{same}），两条都被选到过（{string.Join("/", picks)}）");
        }

        private static int RandomRun(uint seed, out long subs)
        {
            CombatConfig cfg = Cfg();
            cfg.EmgSiteSeed = seed * 2654435761u;
            using var k = new CombatKernel(cfg, 16);
            int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Random, EmgEventKind.Hit), TrigI(1, EmgEventKind.Hit), TrigI(2, EmgEventKind.Hit)), Child(1), Child(2));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
            k.FireAt(me, foe, k.Time);
            StepN(k, 3);
            subs = Subs(k);
            CombatProjectile p = Projs(k).FirstOrDefault(x => (x.Flags & EmgProjFlags.Sub) != 0);
            return p.Id != 0 ? p.Node : -1;
        }

        // ── F. 延后 ──

        private static void CheckDefer()
        {
            Line("  · F. 延后（FGR-EMG-043 延后列）：1 秒后执行；弹体已结束在结束点、还在飞取它当时的位置；累计超上限不执行");
            DeferRun(false, 1f, out long before, out long after, out double distEnd, out double due, out double hitAt);
            DeferRun(true, 1f, out _, out long afterP, out double distFly, out _, out _);
            DeferRun(false, 0f, out _, out long afterBad, out _, out _, out _);
            DeferRun(true, 1f, out _, out long afterEnd, out double distGone, out _, out _, 1.0f); // 贯穿后继续飞、1.0 秒寿命到期（命中后约 0.6 秒）：到点时弹体已结束
            Expect(afterEnd == 1 && distGone > 4.0, $"宿主命中后继续飞、到点前已结束：在它的结束点放出（离命中点 {distGone:0.0} 米，不是命中点）");
            Expect(before == 0 && after == 1 && distEnd < 1.0 && Math.Abs(due - (hitAt + 1.0)) < 0.02,
                $"宿主已结束（命中即止）：0.9 秒时 {before} 份、1.1 秒后 {after} 份，放在结束点（离命中点 {distEnd:0.00} 米）；到点时刻 = 命中 + 1 秒（游戏时间；暂停与倍速见 I2 段）");
            Expect(afterP == 1 && distFly > 5.0, $"宿主还在飞（贯穿）：1 秒后在弹体当时位置放出（离命中点 {distFly:0.0} 米）");
            Expect(afterBad == 0, "负向：累计延后超上限（编译期 A = 0）的下游段不执行（2 秒后仍 0 份）");
        }

        private static void DeferRun(bool pierce, float valid, out long before, out long after, out double dist, out double due, out double hitAt, float life = 4f)
        {
            using var k = new CombatKernel(Cfg(), 16);
            N root = pierce ? Root(PierceI(0), MetaI(1, EmgMetaConst.Defer, EmgEventKind.Hit, 0, 0.5f, valid), TrigI(2, EmgEventKind.Hit))
                : Root(MetaI(1, EmgMetaConst.Defer, EmgEventKind.Hit, 0, 0.5f, valid), TrigI(2, EmgEventKind.Hit));
            int prog = Tree(k, root, Child(2));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 10f, life)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 5000f);
            k.FireAt(me, foe, k.Time);
            hitAt = -1;
            due = -1;
            for (int i = 0; i < 60 && hitAt < 0; i++)
            {
                StepN(k, 1);
                if (k.MetaRunCount > 0 && k.TryGetMetaRun(0, out EmgMetaRun m))
                {
                    hitAt = k.Time;
                    due = m.DueAt;
                }
            }
            StepN(k, 54); // 命中后 0.9 秒
            before = Subs(k);
            double2 hitPoint = new double2(0, 4);
            dist = -1;
            for (int i = 0; i < 12; i++)
            {
                StepN(k, 1);
                if (dist < 0 && Subs(k) > before)
                {
                    CombatProjectile s = Projs(k).Where(p => (p.Flags & EmgProjFlags.Sub) != 0).OrderByDescending(p => p.Id).FirstOrDefault();
                    dist = s.Id != 0 ? math.distance(s.Pos, hitPoint) : -1; // 生成的那一步还没移动：就是放出点
                }
            }
            after = Subs(k);
            StepN(k, 60);
            after = Math.Max(after, Subs(k));
        }

        // ── G. 循环 ──

        private static void CheckLoop()
        {
            Line("  · G. 循环（FGR-EMG-043 循环列）：飞行中每 0.5 秒执行（沿途排放 + 布子弹药）、≤ 8 次；宿主提前结束即停；格斗留出手点锚点；布区存续期间执行");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill), TrigI(2, EmgEventKind.Hit)), Child(2));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 2f, 10f)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 500f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60 * 6);
                List<CombatZone> pools = Zones(k).Where(z => z.EmgProgRef == 0).ToList();
                double spread = pools.Count > 1 ? math.distance(pools.First().Pos, pools.Last().Pos) : 0;
                Expect(pools.Count == 8 && Subs(k) == 8 && spread > 5.0 && k.MetaRunCount == 0,
                    $"[循环, 排放, 分裂] 飞行弹体（寿命 10 秒）：沿途 {pools.Count} 摊、{Subs(k)} 份子弹药（到 emg.meta.loop_max = 8 即止），首末相距 {spread:0.0} 米；运行态已清空");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 1.5f, 10f)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2.6), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60 * 4);
                Expect(Pools(k) == 1 && k.MetaRunCount == 0, $"负向：循环的宿主提前结束（约 0.9 秒命中即止）——只执行 {Pools(k)} 次，运行态随宿主结束清掉");
            }
            CombatConfig cfg = Cfg();
            cfg.EmgMetaAnchor = 1.8f;
            using (var k = new CombatKernel(cfg, 16))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
                int me = Unit(k, CombatFaction.Player, new double2(3, 3), k.AddWeapon(Instant(prog, 3f, false, CombatCarrier.Melee)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(3, 4.6), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60 * 3);
                List<CombatZone> pools = Zones(k).Where(z => z.EmgProgRef == 0).ToList();
                Expect(pools.Count == 3 && pools.All(z => math.distance(z.Pos, new double2(3, 3)) < 0.01),
                    $"格斗：在出手点留“程序锚点”，锚点 1.8 秒内每 0.5 秒执行（{pools.Count} 次，都在出手点）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, 30f, false, CombatCarrier.Field)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 60 * 4);
                List<CombatZone> pools = Zones(k).Where(z => z.EmgProgRef == 0).ToList();
                Expect(pools.Count == 4 && pools.All(z => math.distance(z.Pos, new double2(0, 6)) < 1.0),
                    $"布区：落区存续 2.2 秒期间每 0.5 秒执行（{pools.Count} 次，在区域中心）");
            }
        }

        // ── H. 反转 ──

        private static void CheckReverse()
        {
            Line("  · H. 反转（FGR-EMG-040；FG19A N07）：开火时按本机积热 ≥ 50% 选逆序树，否则正序树（正式编译 + 成对登记）");
            EmgProgramResult r = P(Rev, Split, Pierce);
            using var k = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k, r);
            bool pair = k.TryGetProgram(prog, out EmgProgram fp) && k.TryGetProgram(prog + 1, out EmgProgram rp)
                        && (fp.Flags & EmgProgramFlags.HasReverse) != 0 && (rp.Flags & EmgProgramFlags.IsReverse) != 0 && CombatSite.RegisterProgram(k, r) == prog;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog, 30f, true), 20f, 2f)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 20), -1, 5000f);
            k.FireAt(me, foe, k.Time);
            StepN(k, 1);
            int cold = Projs(k).Select(p => p.Prog).DefaultIfEmpty(-9).Max();
            k.FireAt(me, foe, k.Time);
            StepN(k, 1);
            int hot = Projs(k).Select(p => p.Prog).DefaultIfEmpty(-9).Max();
            Expect(pair && cold == prog && hot == prog + 1, $"成对登记（正序 {prog} / 逆序 {prog + 1}，重复登记去重）；冷态开火用正序树（{cold}），积热过半后用逆序树（{hot}）");
        }

        // ── I. 存读档 ──

        private static void CheckSaveLoad()
        {
            Line("  · I. 存读档（快照段 23）：延后队列与循环计数中途存档 → 新内核读档 → 续跑，与连续运行哈希逐位一致");
            using var k = new CombatKernel(Cfg(), 32);
            int pl = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
            int pd = Tree(k, Root(MetaI(0, EmgMetaConst.Defer, EmgEventKind.Hit), TrigI(1, EmgEventKind.Hit)), Child(1));
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(pl), 2f, 10f)), 100f);
            int fa = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 500f);
            int b = Unit(k, CombatFaction.Player, new double2(20, 0), k.AddWeapon(Instant(pd)), 100f);
            int fb = Unit(k, CombatFaction.Hostile, new double2(20, 4), -1, 500f);
            k.FireAt(a, fa, k.Time);
            k.FireAt(b, fb, k.Time);
            StepN(k, 50); // 循环已执行 1 次、延后还没到点
            int runs = k.MetaRunCount;
            k.TryGetMetaRun(0, out EmgMetaRun r0);
            byte[] snap = k.Serialize();
            using var c = new CombatKernel(Cfg(), 32);
            CombatLoadResult lr = c.Load(snap);
            bool loaded = lr == CombatLoadResult.Ok;
            bool same = loaded && c.MetaRunCount == runs && c.StateHash() == k.StateHash() && c.EmgStateHash() == k.EmgStateHash()
                        && c.TryGetMetaRun(0, out EmgMetaRun c0) && c0.DueAt == r0.DueAt && c0.Loop == r0.Loop && c0.Mask == r0.Mask && c0.Host == r0.Host && c0.Mode == r0.Mode;
            bool every = true;
            for (int i = 0; i < 360; i++)
            {
                StepN(k, 1);
                StepN(c, 1);
                every &= k.StateHash() == c.StateHash();
            }
            Expect(runs >= 2 && same && every && Pools(k) == Pools(c) && Subs(k) == Subs(c) && Subs(k) == 1,
                $"中途 {runs} 条运行态（循环计数 {r0.Loop}、到点 {r0.DueAt:0.00}）存读档一致（读档 {lr}、读后一致 {same}）；续跑 360 步逐步哈希一致（{every}）；排放 {Pools(k)}/{Pools(c)} 摊、延后子弹药 {Subs(k)}/{Subs(c)} 份两边相同");
        }

        // ── I2. 暂停与 0.5x～3x（B09）：循环 / 延后按游戏时间到点；倍速只改每帧步数，暂停时 CombatSite 不调内核 Step ──

        private static void CheckTiming()
        {
            Line("  · I2. 暂停与 0.5x～3x：循环周期与延后到点都按模拟时间（倍速只改每帧步数），暂停不走；同一模拟时间结果逐字一致");
            ulong RunAt(int stepsPerFrame, int pauseAt, out int pools, out long subs, out int runsMid)
            {
                using var k = new CombatKernel(Cfg(), 32);
                int pl = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
                int pd = Tree(k, Root(MetaI(0, EmgMetaConst.Defer, EmgEventKind.Hit), TrigI(1, EmgEventKind.Hit)), Child(1));
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(pl), 2f, 10f)), 100f);
                int fa = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 500f);
                int b = Unit(k, CombatFaction.Player, new double2(20, 0), k.AddWeapon(Instant(pd)), 100f);
                int fb = Unit(k, CombatFaction.Hostile, new double2(20, 4), -1, 500f);
                k.FireAt(a, fa, k.Time);
                k.FireAt(b, fb, k.Time);
                int stepped = 0;
                runsMid = -1;
                for (int frame = 0; stepped < 240; frame++)
                {
                    if (pauseAt >= 0 && frame >= pauseAt && frame < pauseAt + 100)
                    {
                        runsMid = runsMid < 0 ? k.MetaRunCount : runsMid; // 暂停期间运行态原样保留
                        continue;
                    }
                    StepN(k, stepsPerFrame);
                    stepped += stepsPerFrame;
                }
                pools = Pools(k);
                subs = Subs(k);
                return k.StateHash();
            }
            ulong h1 = RunAt(2, -1, out int p1, out long s1, out _), h05 = RunAt(1, -1, out _, out _, out _), h3 = RunAt(6, -1, out _, out _, out _);
            ulong hp = RunAt(2, 20, out int pp, out long sp, out int midRuns);
            Expect(h1 == h05 && h1 == h3 && h1 == hp && p1 >= 6 && s1 == 1 && pp == p1 && sp == s1 && midRuns >= 1,
                $"0.5x / 1x / 3x 同一模拟时间（4 秒）哈希一致（{h05 == h1}/{h3 == h1}）：循环排放 {p1} 摊、延后子弹药 {s1} 份；运行中暂停 100 帧（暂停时运行态 {midRuns} 条原样保留）再续跑，哈希与结果都与不暂停相同（{hp == h1}）");
        }

        // ── J. 空场景早退出、发现、模糊测试器缺口 ──

        private static void CheckEmptyAndDiscovery()
        {
            Line("  · J. 没有元程序的程序逐字不变（无 HasMeta、无运行态、子节点 MetaMask = 0）；第一次用到元程序记语法发现（DEBT-EMG2DISC01-03）");
            EmgProgramResult r = P(Split, Pierce);
            using (var k = new CombatKernel(Cfg(), 8))
            {
                int prog = CombatSite.RegisterProgram(k, r);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                Expect((r.Program.Flags & (EmgProgramFlags.HasMeta | EmgProgramFlags.HasReverse)) == 0 && r.KernelNodes.All(n => n.MetaMask == 0) && r.HeatMeta == 0f && r.Metas.Count == 0
                       && k.MetaRunCount == 0 && k.EmgMetaSeenMask == 0 && Subs(k) == 3,
                    "没有元程序：程序字节与签名口径不变（无 HasMeta / MetaMask / 元程序热量），运行态恒空，每步元程序入口 O(1) 早退出");
            }
            using (var k = new CombatKernel(Cfg(), 8))
            {
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, Ev(EmgEventKind.Marked)), MetaI(1, EmgMetaConst.Repeat, EmgEventKind.Hit), TrigI(2, EmgEventKind.Hit)), Child(2));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 20);
                int mask = k.EmgMetaSeenMask;
                var state = new CampaignState();
                bool first = ReactionDiscoveryService.RecordGrammar(state, ReactionDiscoveryService.MetaEntry(EmgMetaConst.If), "selftest");
                bool again = ReactionDiscoveryService.RecordGrammar(state, ReactionDiscoveryService.MetaEntry(EmgMetaConst.If), "selftest");
                string text = ReactionDiscoveryService.GrammarText(ReactionDiscoveryService.MetaEntry(EmgMetaConst.Repeat));
                Expect((mask & (1 << EmgMetaConst.If)) != 0 && (mask & (1 << EmgMetaConst.Repeat)) == 0 && first && !again && text.Contains("复制") && !text.Contains("meta."),
                    $"条件为假那次只记“条件”（发现位 {mask}），复制没作用不记；语法发现写一次、不重复提示：“{text}”");
            }
        }

        private static void CheckFuzzGap()
        {
            Line("  · A0. 正式表：EMG3-FW-02 落表后 6 个 meta.* 写端都有正常游戏可获得的写者——模糊测试器的 meta.* 缺口清零（EMG3-META-01 时为 6 条，承接 EMG3-FW-02）");
            EmgCatalog.ResetForTests();
            var audit = FgEmergenceFuzzSelfCheck.AuditSources(EmgCatalog.FromTables(), 3, new HashSet<string>());
            var metaGaps = audit.Gaps.Where(g => g.Port.StartsWith("meta.", StringComparison.Ordinal)).ToList();
            Expect(metaGaps.Count == 0 && !audit.Failures.Any(f => f.Contains("meta.")),
                $"meta.* 缺口 {metaGaps.Count} 条（{string.Join("、", metaGaps.Select(g => g.Port + "→" + g.Card))}）、相关失败 {audit.Failures.Count(f => f.Contains("meta."))} 条");
        }

        // ── L. 5 载体读法补全（FGR-EMG-043；“出手点”统一按本次结算的结算点）──

        private static void CheckCarriers()
        {
            Line("  · L. 5 载体读法补全（FGR-EMG-043）：无人机宿主的复制 / 条件 / 随机 / 延后；布区的复制 / 随机 / 延后 / 条件；格斗 / 力场 / 即时射弹的延后在结算点；力场一次打中 3 个单位");
            // 无人机（不飞行的当场开火：宿主 = 无人机）
            long d0 = DroneRun(k => Tree(k, Root(TrigI(0, EmgEventKind.Hit)), Child(0)), false, out _, out _, out _);
            long dRep = DroneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Repeat, EmgEventKind.Hit), TrigI(1, EmgEventKind.Hit)), Child(1)), false, out _, out _, out _);
            Func<CombatKernel, int> ifMark = k => Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, Ev(EmgEventKind.Marked)), TrigI(1, EmgEventKind.Hit)), Child(1));
            long dOn = DroneRun(ifMark, true, out _, out _, out _), dOff = DroneRun(ifMark, false, out _, out _, out _);
            DroneRun(k => Tree(k, Root(EmitI(0, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), false, out int dTwo, out _, out _);
            DroneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Random, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit), EmitI(2, EmgEventKind.Hit))), false, out int dRnd, out _, out _);
            DroneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Defer, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), false, out int dDef, out double toDrone, out double toHit);
            Expect(d0 == 1 && dRep == 2 && dOn == 1 && dOff == 0 && dTwo == 2 && dRnd == 1,
                $"无人机：一次开火 {d0} 组、[复制, 分裂] {dRep} 组；[若目标被标记, 分裂] 已标记 {dOn} / 未标记 {dOff} 份；[随机, 排放, 排放] 只排 {dRnd} 摊（不加随机 {dTwo} 摊）");
            Expect(dDef == 1 && toDrone >= 0 && toDrone < 0.5 && toHit > 2.0,
                $"无人机 [延后, 排放]：1 秒后在无人机当时的位置排放（离无人机 {toDrone:0.00} 米、离命中点 {toHit:0.0} 米），不在命中点");
            // 布区（宿主 = 区域）
            Func<CombatKernel, int> emit1 = k => Tree(k, Root(EmitI(0, EmgEventKind.Hit)));
            int zPlain = ZoneRun(emit1, 2.2f, false, false, out _);
            int zRep = ZoneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Repeat, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), 2.2f, false, false, out _);
            int zTwo = ZoneRun(k => Tree(k, Root(EmitI(0, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), 2.2f, false, false, out _);
            int zRnd = ZoneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Random, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit), EmitI(2, EmgEventKind.Hit))), 2.2f, false, false, out _);
            Expect(zPlain >= 1 && zRep == 2 * zPlain && zTwo == 2 * zPlain && zRnd == zPlain,
                $"布区：每个区域节拍再结算（[排放] {zPlain} 摊 → [复制, 排放] {zRep} 摊）；随机每个节拍选 1 条（[排放, 排放] {zTwo} 摊 → [随机, 排放, 排放] {zRnd} 摊）");
            ZoneRun(emit1, 1.2f, false, true, out List<CombatZone> offPools);
            int zDef = ZoneRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Defer, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), 1.2f, false, true, out List<CombatZone> defPools);
            var center = new double2(0, 6);
            Expect(offPools.Any(z => math.distance(z.Pos, center) > 1.0) && zDef >= 1 && defPools.All(z => math.distance(z.Pos, center) < 0.05),
                $"布区 [延后, 排放]（目标挪到圈边、区域 1.2 秒就结束）：不延后时排在被打的单位处；延后的 {zDef} 摊都在区域中心（区域已结束也在中心）");
            Func<CombatKernel, int> zoneIf = k => Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, Ev(EmgEventKind.Marked)), MetaI(1, EmgMetaConst.Loop), EmitI(2, EmgEventKind.Kill)));
            int zIfOn = ZoneRun(zoneIf, 2.2f, true, false, out _), zIfOff = ZoneRun(zoneIf, 2.2f, false, false, out _);
            Expect(zIfOn == 4 && zIfOff == 0, $"布区落区时条件对区域内首个单位求值：[若目标被标记, 循环, 排放] 圈内单位已标记 {zIfOn} 摊、未标记 {zIfOff} 摊");
            long matAll = ZoneMatRun(false, false, out _, false), matCenter = ZoneMatRun(true, false, out bool ok1), matOwner = ZoneMatRun(false, true, out bool ok2);
            // 出手那一下（宿主 = 出手单位）对出手者自身求值：出手者在油里时那 1 份照放；区域节拍（宿主 = 区域）只看区域中心与圈内单位
            Expect(ok1 && ok2 && matCenter >= 1 && matOwner <= 1 && matCenter + matOwner == matAll,
                $"布区 mat.* 对区域本身求值：不加条件共 {matAll} 份；区域中心在油里（被打的单位不在油里）区域节拍放 {matCenter} 份；只有出手者站在油里时区域节拍 0 份，只剩出手那一下 {matOwner} 份（{matCenter} + {matOwner} = {matAll}）");
            // 当场结算（宿主 = 出手单位）：延后在结算点（命中点），不跟着出手者
            foreach (CombatCarrier car in new[] { CombatCarrier.Projectile, CombatCarrier.Melee, CombatCarrier.Aura })
            {
                using var k = new CombatKernel(Cfg(), 8);
                int prog = Tree(k, Root(MetaI(0, EmgMetaConst.Defer, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit)));
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, car == CombatCarrier.Projectile ? 30f : 3f, false, car)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 1.6), -1, 5000f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 54);
                int before = Pools(k);
                k.SetPosition(me, new double2(-3, 0)); // 出手者挪开：延后不跟着它
                StepN(k, 12);
                List<CombatZone> pools = Zones(k).Where(z => z.EmgProgRef == 0).ToList();
                double dist = pools.Count > 0 ? math.distance(pools[0].Pos, new double2(0, 1.6)) : -1;
                Expect(before == 0 && pools.Count == 1 && dist >= 0 && dist < 0.05, $"{car} [延后, 排放]：0.9 秒时 {before} 摊，1 秒后在命中点排放（离命中点 {dist:0.00} 米；出手者已挪开 3 米）");
            }
            // 力场一次打中 3 个单位：条件 / 随机对主目标；触发器只按主目标重放
            long aRep = AuraRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Repeat, EmgEventKind.Hit), TrigI(1, EmgEventKind.Hit)), Child(1)), -1, out _);
            AuraRun(emit1, -1, out int aEmit);
            AuraRun(k => Tree(k, Root(MetaI(0, EmgMetaConst.Repeat, EmgEventKind.Hit), EmitI(1, EmgEventKind.Hit))), -1, out int aEmitRep);
            Func<CombatKernel, int> aIf = k => Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, Ev(EmgEventKind.Marked)), MetaI(1, EmgMetaConst.Repeat, EmgEventKind.Hit), TrigI(2, EmgEventKind.Hit)), Child(2));
            long aMain = AuraRun(aIf, 0, out _), aSide = AuraRun(aIf, 1, out _);
            Func<CombatKernel, int> aIfEmit = k => Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, Ev(EmgEventKind.Marked)), EmitI(1, EmgEventKind.Hit)));
            AuraRun(aIfEmit, 0, out int eMain);
            AuraRun(aIfEmit, 1, out int eSide);
            Expect(aRep == 2 && aEmit == 3 && aEmitRep == 6,
                $"力场打中 3 个单位：[复制, 分裂] 只放 {aRep} 组（触发只按主目标，副目标那两条命中不重放触发器）；[复制, 排放] 每条命中的排放都再结算一次（{aEmit} → {aEmitRep} 摊）");
            Expect(aMain == 2 && aSide == 0 && eMain == 3 && eSide == 0,
                $"力场条件对主目标求值：[若目标被标记, 复制, 分裂] 主目标已标记 {aMain} 份、只有副目标已标记 {aSide} 份；[若目标被标记, 排放] 主目标已标记 {eMain} 摊、只有副目标已标记 {eSide} 摊");
        }

        private static long DroneRun(Func<CombatKernel, int> build, bool mark, out int pools, out double toDrone, out double toHit)
        {
            CombatConfig cfg = Cfg();
            cfg.DroneReach = 4f;
            using var k = new CombatKernel(cfg, 16);
            int prog = build(k);
            CombatWeapon w = Instant(prog, 30f);
            w.Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 6f, DroneRatio = 1f, DroneCooldown = 10f, DroneLeash = 10f }; // 只开一次火
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            var hit = new double2(0, 7);
            int foe = Unit(k, CombatFaction.Hostile, hit, -1, 5000f);
            if (mark)
            {
                k.SetMarkedUntil(foe, k.Time + 30.0);
            }
            k.FireAt(me, foe, k.Time);
            toDrone = toHit = -1;
            for (int i = 0; i < 60 * 5; i++)
            {
                StepN(k, 1);
                if (toDrone < 0 && Pools(k) > 0 && k.TryGetDrone(0, out CombatDrone dr))
                {
                    CombatZone z = Zones(k).First(x => x.EmgProgRef == 0);
                    toDrone = math.distance(z.Pos, dr.Pos);
                    toHit = math.distance(z.Pos, hit);
                }
            }
            pools = Pools(k);
            return Subs(k);
        }

        /// <summary>布区落在目标处（区域中心 = (0, 6)）；<paramref name="moveOff"/> = 落区后把目标挪到圈边 (1.3, 6)。返回排放驻留区数。</summary>
        private static int ZoneRun(Func<CombatKernel, int> build, float life, bool mark, bool moveOff, out List<CombatZone> pools)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            CombatWeapon w = Instant(prog, 30f, false, CombatCarrier.Field);
            w.Reading.FieldSeconds = life;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f);
            if (mark)
            {
                k.SetMarkedUntil(foe, k.Time + 30.0);
            }
            k.FireAt(me, foe, k.Time);
            if (moveOff)
            {
                k.SetPosition(foe, new double2(1.3, 6));
            }
            StepN(k, 60 * 4);
            pools = Zones(k).Where(z => z.EmgProgRef == 0).ToList();
            return pools.Count;
        }

        /// <summary>真实内容内核（斑块层开）：[若 mat.oil, 分裂] 布区；被打的单位挪到油外。<paramref name="center"/> = 区域中心有油；<paramref name="owner"/> = 出手者站在油里。</summary>
        private static long ZoneMatRun(bool center, bool owner, out bool ok, bool cond = true)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            c.EmgEntity = 1;
            c.EmgSiteSeed = 12345u;
            using var k = new CombatKernel(c, 16);
            CombatSite.ApplyContent(k);
            ushort oil = (ushort)(EmgMatterConst.PortBase + EmgMatterConst.Oil);
            int prog = cond ? Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, oil), TrigI(1, EmgEventKind.Hit)), Child(1)) : Tree(k, Root(TrigI(0, EmgEventKind.Hit)), Child(0));
            CombatWeapon w = Instant(prog, 30f, false, CombatCarrier.Field);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6), -1, 5000f);
            ok = !owner || k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 0), 1.2f, 3f, 60f);
            StepN(k, 20); // 接触按桶刷新 PrevMatterMask
            k.FireAt(me, foe, k.Time);
            k.SetPosition(foe, new double2(1.5, 6));
            ok &= !center || k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 6), 0.6f, 3f, 60f);
            StepN(k, 60 * 3);
            return Subs(k);
        }

        /// <summary>力场（半径 3）一次打中 3 个单位：主目标 (0, 1.6)，副目标两个；<paramref name="markWhich"/>：0 = 标记主目标，1 = 只标记一个副目标，−1 = 不标记。</summary>
        private static long AuraRun(Func<CombatKernel, int> build, int markWhich, out int pools)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = build(k);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, 3f, false, CombatCarrier.Aura)), 100f);
            int main = Unit(k, CombatFaction.Hostile, new double2(0, 1.6), -1, 5000f);
            int side = Unit(k, CombatFaction.Hostile, new double2(1.2, 1.2), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(-1.2, 1.2), -1, 5000f);
            if (markWhich >= 0)
            {
                k.SetMarkedUntil(markWhich == 0 ? main : side, k.Time + 30.0);
            }
            k.FireAt(me, main, k.Time);
            StepN(k, 30);
            pools = Pools(k);
            return Subs(k);
        }

        // ── M. 条件读端六类（卡片“必须同时交付”）：各一正一反 ──

        private static void CheckCondPorts()
        {
            Line("  · M. 条件读端：prop.integrity / prop.heat / phs.*（相态端口、累积量端口）/ mat.* / chs.* 各一正一反；内核不认识的端口为假；弹体宿主的 chs.* 按出手者求值");
            const float Amt = 0.5f;
            long hpLow = CondRun(EmgConst.PortPropIntegrity, Amt, (k, me, foe) => k.SetHealth(foe, 100f, 500f, true)), hpFull = CondRun(EmgConst.PortPropIntegrity, Amt, null);
            Expect(hpLow == 1 && hpFull == 0, $"prop.integrity（低于 50%）：目标剩 20% 放 {hpLow} 份、满血 {hpFull} 份");
            ushort chillAcc = EmgConst.PortPhsBase, chillState = (ushort)(EmgConst.PortPhsBase + 1);
            bool frozen = false;
            long stOn = CondRun(chillState, Amt, (k, me, foe) =>
            {
                k.ChillUnit(foe, 0.6f);
                k.ChillUnit(foe, 0.6f);
                frozen = k.InPhase(foe, 0);
            }, true);
            long stOff = CondRun(chillState, Amt, (k, me, foe) => k.ChillUnit(foe, 0.3f), true);
            long acOn = CondRun(chillAcc, Amt, (k, me, foe) => k.ChillUnit(foe, 0.3f), true), acOff = CondRun(chillAcc, Amt, null, true);
            Expect(frozen && stOn == 1 && stOff == 0 && acOn == 1 && acOff == 0,
                $"phs.*：相态端口（冻结）目标已冻结 {stOn} 份、只有寒量没冻结 {stOff} 份；累积量端口（寒量）有寒量 {acOn} 份、没有 {acOff} 份");
            ushort oil = (ushort)(EmgMatterConst.PortBase + EmgMatterConst.Oil);
            bool spawned = false;
            long inOil = CondRun(oil, Amt, (k, me, foe) =>
            {
                spawned = k.SpawnPatch(EmgMatterConst.Oil, new double2(0, 4), 1f, 3f, 60f);
                StepN(k, 20);
            }, true);
            long dry = CondRun(oil, Amt, (k, me, foe) => StepN(k, 20), true);
            Expect(spawned && inOil == 1 && dry == 0, $"mat.oil：目标站在油里 {inOil} 份、不在 {dry} 份");
            Action<CombatKernel, int, int> hover = (k, me, foe) => k.SetUnitTrait(me, EmgTraitConst.HoverFloat); // 悬浮离地（chs.airborne，不改索敌）
            long airOn = CondRun(EmgConst.PortChsAirborne, Amt, hover, true), airOff = CondRun(EmgConst.PortChsAirborne, Amt, null, true);
            long ground = CondRun(EmgConst.PortChsGround, Amt, null, true), groundAir = CondRun(EmgConst.PortChsGround, Amt, hover, true);
            long airFlight = CondRun(EmgConst.PortChsAirborne, Amt, hover, true, true), groundFlight = CondRun(EmgConst.PortChsAirborne, Amt, null, true, true);
            Expect(airOn == 1 && airOff == 0 && ground == 1 && groundAir == 0 && airFlight == 1 && groundFlight == 0,
                $"chs.*：离地出手者 chs.airborne {airOn} 份、着地 {airOff} 份；chs.ground 着地 {ground} / 离地 {groundAir} 份；飞行弹体宿主按出手者：离地 {airFlight} / 着地 {groundFlight} 份");
            long hot = HotRun(true), cold = HotRun(false);
            Expect(hot >= 3 && cold == 0, $"prop.heat（正式编译 N02 [若本机过热, 分裂]）：积热过半的那发放 {hot} 份、冷武器 {cold} 份");
            long unknown = CondRun(60000, Amt, (k, me, foe) => k.SetMarkedUntil(foe, k.Time + 30.0));
            Expect(unknown == 0, $"负向：内核不认识的条件端口一律为假（{unknown} 份）");
        }

        private static long CondRun(ushort port, float amt, Action<CombatKernel, int, int> setup, bool content = false, bool flight = false)
        {
            CombatConfig cfg = Cfg();
            if (content)
            {
                cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                cfg.EmgEntity = 1;
                cfg.EmgSiteSeed = 12345u;
            }
            using var k = new CombatKernel(cfg, 16);
            if (content)
            {
                CombatSite.ApplyContent(k);
            }
            int prog = Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, port, amt), TrigI(1, EmgEventKind.Hit)), Child(1));
            CombatWeapon w = Instant(prog);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(flight ? Flight(w, 20f, 2f) : w), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 500f);
            setup?.Invoke(k, me, foe);
            k.FireAt(me, foe, k.Time);
            StepN(k, 40);
            return Subs(k);
        }

        private static long HotRun(bool heat)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k, P(IfHot, Split));
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog, 30f, heat)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 5000f);
            for (int s = 0; s < 2; s++)
            {
                k.FireAt(me, foe, k.Time);
                StepN(k, 30);
            }
            return Subs(k);
        }

        // ── I3. 段 23 的 7 字段短记录（本卡之前 CHS-01 骨架写出的形状）──

        private static void CheckShortRecord()
        {
            Line("  · I3. 读档：段 23 只有 7 个字段的短记录（CHS-01 骨架）读进来，追加字段取缺省（模式 = 循环、无宿主），到点即作为“宿主已结束”丢弃，不执行、不报错");
            using var k = new CombatKernel(Cfg(), 16);
            int pl = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill)));
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(pl), 2f, 10f)), 100f);
            int fa = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 500f);
            k.FireAt(a, fa, k.Time);
            StepN(k, 10);
            int runs = k.MetaRunCount;
            using var c = new CombatKernel(Cfg(), 16);
            CombatLoadResult lr = c.Load(k.SerializeEmgShapeForTests(EmgConst.SegMeta, 7, 0));
            bool got = c.TryGetMetaRun(0, out EmgMetaRun m0);
            int pools0 = Pools(c);
            StepN(c, 60);
            Expect(runs == 1 && lr == CombatLoadResult.Ok && got && m0.Mode == EmgMetaConst.RunLoop && m0.Host == 0 && m0.Mask == 0UL && c.MetaRunCount == 0 && Pools(c) == pools0,
                $"短记录读档 {lr}：{(got ? "读到 1 条" : "没读到")}（模式 {m0.Mode}、宿主 {m0.Host}、序号位 {m0.Mask}）；到点丢弃（运行态 {c.MetaRunCount} 条），没有多排放");
        }

        // ── K. 耗时 ──

        private static void CheckPerformance()
        {
            Line("  · K. 耗时（警告口径，不设门禁）：64 台机器同时跑 [循环, 排放, 分裂] 与 [条件, 复制, 延后, 分裂] 的元程序满载场景");
            using var k = new CombatKernel(Cfg(), 512);
            int pl = Tree(k, Root(MetaI(0, EmgMetaConst.Loop), EmitI(1, EmgEventKind.Kill), TrigI(2, EmgEventKind.Hit)), Child(2));
            int pr = Tree(k, Root(MetaI(0, EmgMetaConst.If, EmgEventKind.None, EmgConst.PortPropIntegrity, 2f), MetaI(1, EmgMetaConst.Repeat, EmgEventKind.Hit),
                MetaI(2, EmgMetaConst.Defer, EmgEventKind.Hit), TrigI(3, EmgEventKind.Hit)), Child(3, EmgEventKind.Hit, 2));
            var pairs = new List<(int, int)>();
            for (int i = 0; i < 64; i++)
            {
                bool loop = i % 2 == 0;
                int me = Unit(k, CombatFaction.Player, new double2(i * 3, 0), k.AddWeapon(loop ? Flight(Instant(pl), 2f, 6f) : Instant(pr)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(i * 3, loop ? 20 : 4), -1, 100000f);
                pairs.Add((me, foe));
            }
            var sw = new Stopwatch();
            int steps = 0;
            int peakRuns = 0;
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
                    peakRuns = Math.Max(peakRuns, k.MetaRunCount);
                }
            }
            double ms = sw.Elapsed.TotalMilliseconds / steps;
            Expect(ms < 5.0 && peakRuns > 0, $"元程序满载：每步平均 {ms:0.000} 毫秒（{steps} 步，运行态峰值 {peakRuns} 条）；没有元程序的程序每个入口只判一次标志位（早退出），空场景开销不计入");
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
