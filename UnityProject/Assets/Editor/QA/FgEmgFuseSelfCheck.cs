using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
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
    /// EMG3-FUSE-01 熔合重做的自动验收（FG19 FGR-EMG-044、FGT-EMG-054；FG19A 第 4.1、4.2 节与第 6 节 N43；ADR-EMG-001 ②）。
    /// 起真实表（N43 fw_anchorwire 落表）、真实编译器（EmgProgramCompiler）、真实预览（EmgProgramTreeText）、真实登记（CombatSite.RegisterProgram / FoldProgramReadings）与真实内核（CombatKernel）断言行为：
    /// A 数据（N43 声明轨、grm.anchor 接通、阶段 3 未接通端口清零）；B 编译（一槽两指令计 2 条容量、内置接线、[A, 接线, B] 与熔合芯片程序字节相同、对照组 [A, B] 不接线、
    /// 接线热量 anchor_max × anchor_heat、A / B 按落点丰富度取、负向：首位 / 末位 / 溢出 / 只有修饰 / 没有落点 / 未接通落点 / 两父重复幂等、预览行）；
    /// C 内核（连锁跳点 / 穿透点 / 注能点执行 B 的效果、熔合芯片与独立接线状态哈希逐位相同、对照组不执行、落点上限截断、倍速 0.5x～3x 与暂停、存读档续跑哈希一致）；
    /// D 同链线索（程序掩码：熔合芯片 / 两父分装都计、单父不计；一次出手只计一次；掩码随快照往返）；E 空程序早退出与耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgFuseSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgFuseSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgFuseSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const string Wire = "fw_anchorwire", Arc = "fw_arcchain", Cool = "fw_coolant", Mix = "fw_mix_wetarc", Pierce = "fw_pierce", Overload = "fw_overload";
        private const string Acid = "fw_acid", Split = "fw_split", Boomerang = "fw_boomerang";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/熔合重做与落点接线（EMG3-FUSE-01）")]
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
            Line("\n[熔合重做] EMG3-FUSE-01：一槽两指令、落点接线（N43）与同链线索（FGT-EMG-054）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程（Editor 下 Mono / Burst，真机 IL2CPP，耗时只作量级参考）");
            GameLanguage lang = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                CarrierReadings.Reload();
                EmgCatalog.OverrideForTests(EmgCatalog.FromTables());
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Step(CheckData);
                Step(CheckCompile);
                Step(CheckNegative);
                Step(CheckKernel);
                Step(CheckSpeedAndSave);
                Step(CheckClue);
                Step(CheckEmpty);
            }
            catch (Exception e)
            {
                Fail($"熔合重做自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [熔合重做] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static EmgProgramResult P(params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, 0);

        private static EmgProgramResult Pc(int cap, params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, cap);

        private static bool HasNote(EmgProgramResult r, string key) => r.Notes.Any(n => n.Key == key);

        private static string Key(EmgProgramResult r) => CombatSite.ProgramKey(r.Program, r.KernelNodes, r.KernelInstrs);

        private static int TailRows(EmgProgramResult r) => r.KernelInstrs.Count(x => (x.Flags & EmgInstrFlags.Anchored) != 0);

        private static string Steps(EmgProgramResult r) => string.Join(",", r.Steps.Select(s => (s.Builtin ? "[内置]" : string.Empty) + s.OwnerId + (s.Overflow ? "×" : string.Empty)));

        // ── A. 数据 ──

        private static void CheckData()
        {
            EmgCompiled c = EmgCatalog.Current;
            bool active = c.OwnerIndex.TryGetValue(Wire, out int oi) && c.OwnerActive(oi);
            Expect(active && EmgCatalog.PendingPorts.Length == 0 && !EmgCatalog.IsPendingPort("grm.anchor", EmgRole.Modify) && EmgCatalog.OwnerDeclaresWrite(Wire, "grm.anchor")
                   && FirmwareCatalog.All.ContainsKey(Wire) && c.EffectiveStage >= 3 && FirmwareKinds.DisplayName(Wire) == "接线",
                $"A1 N43 接线（fw_anchorwire）落表：固件目录有它、显示名“{FirmwareKinds.DisplayName(Wire)}”、声明写 grm.anchor、在声明轨；EmgCatalog.PendingPorts 清空（阶段 3 端口全部接通，生效阶段 {c.EffectiveStage}）");
        }

        // ── B. 编译 ──

        private static void CheckCompile()
        {
            EmgProgramResult chip = P(Mix), wired = P(Arc, Wire, Cool), plain = P(Arc, Cool);
            EmgTreeNode rc = chip.Nodes[0], rw = wired.Nodes[0];
            bool shape = chip.Steps.Count == 3 && chip.Steps[0].OwnerId == Arc && chip.Steps[1].Builtin && chip.Steps[2].OwnerId == Cool && rc.Used == 2 && rw.Used == 3;
            EmgAnchorInfo ai = chip.Anchors.FirstOrDefault();
            float amax = CombatSite.Tuning("emg.fuse.anchor_max", 4f), aheat = CombatSite.Tuning("emg.fuse.anchor_heat", 1f);
            bool anchor = chip.Anchors.Count == 1 && ai.Valid && ai.Kind == EmgAnchorConst.Chain && ai.Copies > 0 && TailRows(chip) == ai.Copies
                          && (chip.Program.Flags & EmgProgramFlags.HasAnchor) != 0 && Mathf.Abs(chip.HeatAnchor - amax * aheat) < 1e-4f;
            Expect(shape && anchor,
                $"B1 熔合芯片湿弧 = [电弧, 内置接线, 冷却液]（{Steps(chip)}）：占 1 槽、根节点用 {rc.Used} 条容量（内置接线不占）；对照 [电弧, 接线, 冷却液] 用 {rw.Used} 条；" +
                $"接线落点 = 连锁跳点，复制冷却液效果写端 {ai.Copies} 行到尾段，编译期热量 +{chip.HeatAnchor}（= anchor_max {amax} × anchor_heat {aheat}，不按实际落点数）");
            Expect(Key(chip) == Key(wired) && chip.Signature == wired.Signature && Mathf.Abs(chip.HeatTotal - wired.HeatTotal) < 1e-4f && chip.Program.ClueMask == wired.Program.ClueMask,
                $"B2 FGT-EMG-054 等价：熔合芯片与 [电弧, 接线, 冷却液] 编出的程序逐字节相同（签名 {chip.Signature} / {wired.Signature}），总热量 {chip.HeatTotal} = {wired.HeatTotal}，只差容量");
            Expect(TailRows(plain) == 0 && (plain.Program.Flags & EmgProgramFlags.HasAnchor) == 0 && plain.HeatAnchor == 0f && plain.Anchors.Count == 0 && Key(plain) != Key(chip),
                "B3 对照组 [电弧, 冷却液]：没有 Anchored 行、没有接线热量（冷却液只在节点结算点执行）");
            // A / B 按落点丰富度取（FG19A 第 4.1 节）：腐焰弹头配方父 A = 腐蚀弹头（没有落点）、父 B = 燃迹 → 展开时燃迹在前
            EmgProgramResult caustic = P("fw_mix_causticshell"), glass = P("fw_mix_glassburst"), mirror = P("fw_mix_nullmirror"), flash = P("fw_mix_flashover");
            Expect(caustic.Steps.Count == 3 && caustic.Steps[0].OwnerId == "fw_burntrail" && caustic.Steps[2].OwnerId == "fw_corrode"
                   && glass.Steps[0].OwnerId == "fw_burntrail" && glass.Steps[2].OwnerId == "fw_arc" && mirror.Steps[0].OwnerId == "fw_deflect" && mirror.Steps[2].OwnerId == "fw_execute"
                   && flash.Steps[0].OwnerId == "fw_overload",
                $"B4 A / B 取法（FG19A 第 4.1 节 + 第 4.3 节表）：落点更丰富（单次上限更大）的一方作 A——腐焰弹头 {Steps(caustic)}、玻化弹 {Steps(glass)}（燃迹在前，与配方表相反）、" +
                $"湮灭镜像 {Steps(mirror)}（偏折场 4 > 处决 1，与配方表相反）；一样丰富按配方表父 A（闪燃过载 {Steps(flash)}）；配方 ID 与混合固件 ID 不改");
            // FGR-EMG-171 缺省相位 = 指令在程序里的序号：接线不计（锈蚀子母弹 = [子母弹, 内置接线, 时钟]，时钟仍是第 2 条指令）
            EmgProgramResult rb = P("fw_mix_rustbomb"), rbWired = P("fw_cluster", Wire, "fw_clock"), rbPlain = P("fw_cluster", "fw_clock");
            int clockOrd = rb.Steps.FindIndex(s => s.OwnerId == "fw_clock"), wiredOrd = rbWired.Steps.FindIndex(s => s.OwnerId == "fw_clock"), plainOrd = rbPlain.Steps.FindIndex(s => s.OwnerId == "fw_clock");
            bool beat = EmgProgramCompiler.BeatOf(rb, 1, clockOrd, out int per, out int ph) & EmgProgramCompiler.BeatOf(rbWired, 1, wiredOrd, out int perW, out int phW)
                        & EmgProgramCompiler.BeatOf(rbPlain, 1, plainOrd, out int perP, out int phP);
            Expect(beat && per > 0 && ph == 1 % per && phW == ph && phP == ph && perW == per && perP == per && rb.Signature == rbWired.Signature,
                $"B6 节拍缺省相位不计接线：锈蚀子母弹里时钟相位 {ph}/{per}、[子母弹, 接线, 时钟] {phW}/{perW}、[子母弹, 时钟] {phP}/{perP}（都按第 2 条指令）；芯片与接线写法签名 {rb.Signature} / {rbWired.Signature}");
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(chip, rows);
            string row = rows.FirstOrDefault(x => x.Kind == EmgTreeRowKind.Anchor).Text ?? string.Empty;
            Expect(row.Contains("接线") && row.Contains("连锁跳点") && row.Contains("+4") && row.Contains("每次结算最多 4 个落点"),
                $"B5 预览标出接线与每个落点的执行次数、热量分项：“{row}”");
        }

        private static void CheckNegative()
        {
            EmgProgramResult first = P(Wire, Cool), last = P(Cool, Wire), modOnly = P(Arc, Wire, Pierce), none = P(Acid, Wire, Cool), trig = P(Split, Wire, Cool), pend = P(Boomerang, Wire, Cool);
            bool reasons = HasNote(first, EmgProgramCompiler.NoteAnchorDangling) && HasNote(last, EmgProgramCompiler.NoteAnchorDangling)
                           && HasNote(modOnly, EmgProgramCompiler.NoteAnchorModOnly) && HasNote(none, EmgProgramCompiler.NoteAnchorNone)
                           && (HasNote(trig, EmgProgramCompiler.NoteAnchorNone) || HasNote(trig, EmgProgramCompiler.NoteAnchorSplit)) && HasNote(pend, EmgProgramCompiler.NoteAnchorPending);
            bool inert = new[] { first, last, modOnly, none, trig, pend }.All(r => TailRows(r) == 0 && r.HeatAnchor == 0f && r.Anchors.All(a => !a.Valid));
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(first, rows);
            bool shown = rows.Any(x => x.Kind == EmgTreeRowKind.Anchor && x.Text.Contains("接线未生效")) && rows.Any(x => x.Kind == EmgTreeRowKind.Reason);
            Expect(reasons && inert && shown,
                "B9 负向（首位 / 末位定为无效，预览写原因、不静默）：接线在首位或末位 → “前面或后面没有可接的指令”；后一条只有修饰（穿透）→ 修饰照常作用于节点、不接线；前一条没有落点（酸液落点排放）→ 预混不接线；" +
                "前一条是触发器 → 不接线；折返这类内核未接通的落点 → 写原因；以上都不复制、不计接线热量");
            EmgProgramResult over = Pc(2, Cool, Mix);
            EmgProgramResult dup = P(Mix, Cool);
            Expect(over.Steps.Last().Overflow && HasNote(over, EmgProgramCompiler.NoteOverflow) && HasNote(over, EmgProgramCompiler.NoteAnchorDangling) && TailRows(over) == 0
                   && HasNote(dup, EmgProgramCompiler.NoteDuplicate),
                $"B7 混合固件放进只剩 1 条容量的节点：第二条（冷却液）溢出、预览写原因，内置接线随之无效（{Steps(over)}）；两父也单独出现在同一程序里 → 同端口幂等、重复的标“重复无效”");
            EmgProgramResult mixes = P(Mix, Mix);
            Expect(mixes.Steps.Count(s => s.Builtin) == 2 && mixes.Nodes[0].Used <= mixes.RootCapacity,
                $"B8 两枚熔合芯片：各自展开 2 条 + 内置接线（{Steps(mixes)}）；混合固件不能再熔合（D-09）由 FgFusionSelfCheck 验");
            // B 的效果写端在落点上没有执行者（回波 en.kinetic 注入）：写原因、不复制、不计接线热量（不做静默死指令）；燃迹的沿途段点已接通（不再写“未接通”）
            EmgProgramResult noExec = P(Arc, Wire, "fw_echo"), trail = P("fw_burntrail", Wire, Cool);
            Expect(HasNote(noExec, EmgProgramCompiler.NoteAnchorNoExec) && TailRows(noExec) == 0 && noExec.HeatAnchor == 0f && noExec.Anchors.All(a => !a.Valid)
                   && trail.Anchors.Count == 1 && !trail.Anchors[0].Valid && HasNote(trail, EmgProgramCompiler.NoteAnchorPending) && trail.HeatAnchor == 0f && TailRows(trail) == 0,
                "B10 [电弧, 接线, 回波]：回波的动能注入在落点上没有执行方式 → 写原因“在落点上没有执行方式”、不复制、不计接线热量；[燃迹, 接线, 冷却液]：沿途段点未接通（FG-GAP-205，承接 EMG4-FUSE-02）→ 写原因、不计热");
            // 事件门控的注能做 A（过热泄放读 evt.overheat 触发）：内核事件脉冲分支没有注能落点钩子 → 未接通，不复制、不计热；命中注能（过载，无触发读端）照常生效
            EmgProgramResult gated = P("fw_ventarc", Wire, Cool), hitInj = P(Overload, Wire, Cool);
            Expect(HasNote(gated, EmgProgramCompiler.NoteAnchorPending) && TailRows(gated) == 0 && gated.HeatAnchor == 0f && gated.Anchors.All(a => !a.Valid)
                   && hitInj.Anchors.Count == 1 && hitInj.Anchors[0].Valid && hitInj.HeatAnchor > 0f,
                $"B12 [过热泄放, 接线, 冷却液]：事件门控注能的落点内核未接通（FG-GAP-205，承接 EMG4-FUSE-02）→ 写原因、不复制、不计接线热量（{gated.HeatAnchor}）；对照 [过载, 接线, 冷却液] 生效、计热 {hitInj.HeatAnchor}");
            // 30 条配方芯片逐条：接线无效的都有原因（不静默），清单写进报告，与 FG-GAP-205 / DEBT-EMG3FUSE01-01 对账（EMG4-FUSE-02 接通后改成“全部生效”）
            var inactive = new List<string>();
            bool silent = false;
            foreach (FusionRecipeDef def in FusionCatalog.Recipes)
            {
                EmgProgramResult m = P(def.MixId);
                EmgAnchorInfo ai = m.Anchors.FirstOrDefault();
                if (ai.Valid)
                {
                    continue;
                }
                bool noted = m.Notes.Any(n => n.Key.StartsWith("emg.compile.anchor_", StringComparison.Ordinal));
                bool premix = ai.Note == EmgProgramCompiler.NoteAnchorNone || ai.Note == EmgProgramCompiler.NoteAnchorSplit;
                silent |= !noted && !premix;
                if (!premix)
                {
                    inactive.Add(def.Id.Replace("fusion.", string.Empty) + "（" + (ai.Note ?? "无").Replace("emg.compile.anchor_", string.Empty) + "）");
                }
            }
            Expect(!silent, $"B11 30 条配方芯片：接线无效的都写了原因（预混 / 触发器子节点除外）；暂未生效的 {inactive.Count} 条：{string.Join("、", inactive)}");
        }

        // ── C. 内核 ──

        /// <summary>正式配置（CombatSite.ConfigFromTuning：斑块层、emg.fuse.anchor_max 等按调参）+ 正式物质表与规则表——与战斗地点同一套。</summary>
        private static CombatKernel NewKernel(int anchorMax = 0)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.EmgSiteSeed = 12345u;
            if (anchorMax > 0)
            {
                c.EmgAnchorMax = anchorMax;
            }
            var k = new CombatKernel(c, 32);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static CombatWeapon WeaponOf(CombatKernel k, EmgProgramResult r, out int prog)
        {
            prog = CombatSite.RegisterProgram(k, r);
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = prog,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            };
            w.Reading = CombatSite.FoldProgramReadings(w.Reading, r, false);
            if ((r.Program.Flags & EmgProgramFlags.NeedsFlight) != 0)
            {
                // 程序需要飞行（穿透 / 反弹这类真弹道修饰，ADR ③）：与 CombatSite 同口径实体化为飞行弹体
                w.SpawnFlight = 1;
                w.ProjectileSpeed = 20f;
                w.ProjectileRadius = 0.15f;
                w.ProjectileLife = 3f;
            }
            return w;
        }

        private struct Run1
        {
            public int Fired, Cut, Matter;
            public string Hash;
            public uint Clue;
        }

        /// <summary>一台机器对一列 3 个敌人（相距 4 米，连锁 6 米够得着）开 <paramref name="shots"/> 次火；按 <paramref name="speed"/> 倍的步长推进（0.5x～3x）。</summary>
        private static Run1 Scene(EmgProgramResult r, float speed = 1f, int anchorMax = 0, int shots = 1, double2? second = null)
        {
            using var k = NewKernel(anchorMax);
            CombatWeapon w = WeaponOf(k, r, out int prog);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int t0 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
            Unit(k, CombatFaction.Hostile, second ?? new double2(0, 9), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 13), -1, 5000f);
            float dt = Dt * speed;
            for (int s = 0; s < shots; s++)
            {
                k.FireAt(a, t0, k.Time);
                for (int i = 0; i < 3; i++)
                {
                    k.Step(dt, k.Time + dt);
                }
            }
            for (int i = 0; i < (int)(30 / speed); i++)
            {
                k.Step(dt, k.Time + dt);
            }
            k.TryGetProgram(prog, out EmgProgram p);
            return new Run1 { Fired = k.EmgScalars.AnchorFired, Cut = k.EmgScalars.AnchorCut, Matter = k.ZoneCount + k.PatchCount, Hash = k.StateHash().ToString(), Clue = p.ClueMask };
        }

        private static void CheckKernel()
        {
            Run1 chip = Scene(P(Mix)), wired = Scene(P(Arc, Wire, Cool)), plain = Scene(P(Arc, Cool));
            Expect(chip.Fired == 2 && wired.Fired == 2 && plain.Fired == 0 && chip.Matter >= plain.Matter + 2 && chip.Hash == wired.Hash,
                $"C1 内核：湿弧一次出手连锁跳 2 个目标 → 每个跳点各排一摊冷却液（接线执行 {chip.Fired} 次，物质 / 驻留区 {chip.Matter} 个，对照组 [电弧, 冷却液] {plain.Matter} 个、接线 {plain.Fired} 次）；" +
                $"熔合芯片与独立接线整场状态哈希逐位相同（{chip.Hash == wired.Hash}）");
            Run1 capped = Scene(P(Mix), anchorMax: 1);
            Expect(capped.Fired == 1 && capped.Cut >= 1,
                $"C2 落点上限 emg.fuse.anchor_max = 1：连锁 2 跳只执行 1 个落点（执行 {capped.Fired}、截断 {capped.Cut}），超出的落点不执行");
            Run1 pierce = Scene(P(Pierce, Wire, Cool), second: new double2(0, 6.5));
            Run1 pierceCtl = Scene(P(Pierce, Cool), second: new double2(0, 6.5));
            Run1 inject = Scene(P(Overload, Wire, Cool));
            Expect(pierce.Fired >= 1 && pierceCtl.Fired == 0 && inject.Fired >= 1,
                $"C3 其他落点：穿透点（[穿透, 接线, 冷却液] 执行 {pierce.Fired} 次，对照 {pierceCtl.Fired}）、能量注入点（[过载, 接线, 冷却液] 执行 {inject.Fired} 次）");
            // 只有 A 的写端产生的点才是落点：B 自己（电容注电）或第三条同族写端不触发复制
            Run1 selfB = Scene(P(Overload, Wire, "fw_capacitor")), third = Scene(P(Overload, Wire, Cool, "fw_capacitor"));
            Expect(selfB.Fired == 1 && third.Fired == 1,
                $"C6 落点只认 A：[过载, 接线, 电容] 一次命中执行接线 {selfB.Fired} 次（电容自己的注电不算过载的落点，应为 1）；[过载, 接线, 冷却液, 电容] {third.Fired} 次（第三条注能不占 A 的落点，应为 1）");
        }

        private static void CheckSpeedAndSave()
        {
            EmgProgramResult chip = P(Mix);
            int[] fired = new[] { 0.5f, 1f, 2f, 3f }.Select(s => Scene(chip, s).Fired).ToArray();
            Expect(fired.All(f => f == 2), $"C4 倍速矩阵 0.5x / 1x / 2x / 3x：每次出手的接线落点数相同（{string.Join(" / ", fired)}），与游戏步长无关");
            using var k = NewKernel();
            CombatWeapon w = WeaponOf(k, chip, out int prog);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int t0 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 13), -1, 5000f);
            k.FireAt(a, t0, k.Time);
            k.Step(Dt, k.Time + Dt);
            int pausedFired = k.EmgScalars.AnchorFired;
            string pausedHash = k.StateHash().ToString();
            for (int i = 0; i < 10; i++)
            {
                k.Step(0f, k.Time); // 暂停的真实入口：战斗地点暂停时按 0 步长调用，内核不推进
            }
            bool paused = k.StateHash().ToString() == pausedHash && k.EmgScalars.AnchorFired == pausedFired;
            byte[] snap = k.Serialize();
            using var c = NewKernel();
            CombatLoadResult lr = c.Load(snap);
            bool clue = c.TryGetProgram(prog, out EmgProgram cp) && k.TryGetProgram(prog, out EmgProgram kp) && cp.ClueMask == kp.ClueMask && cp.ClueMask != 0u
                        && (cp.Flags & EmgProgramFlags.HasAnchor) != 0;
            bool same = lr == CombatLoadResult.Ok && c.StateHash() == k.StateHash() && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 19; // EMG4-HIJ-01 升到 20
            bool every = true;
            for (int i = 0; i < 60; i++)
            {
                if (i == 10)
                {
                    k.FireAt(a, t0, k.Time);
                    c.FireAt(a, t0, c.Time);
                }
                k.Step(Dt, k.Time + Dt);
                c.Step(Dt, c.Time + Dt);
                every &= k.StateHash() == c.StateHash();
            }
            Expect(paused && same && clue && every && k.EmgScalars.AnchorFired >= pausedFired,
                $"C5 存读档（快照格式 {CombatConst.FormatVersion}）：带接线尾段与线索掩码的程序表随快照往返（掩码 {cp.ClueMask:X}），读档后续跑 60 步（中途再开火）哈希逐位一致（{every}）；暂停不推进时状态不变（{lr}）");
        }

        // ── D. 同链线索 ──

        private static void CheckClue()
        {
            int idx = -1;
            for (int i = 0; i < FusionCatalog.Recipes.Count; i++)
            {
                if (FusionCatalog.Recipes[i].Id == "fusion.wetarc")
                {
                    idx = i;
                }
            }
            uint bit = idx >= 0 ? 1u << idx : 0u;
            uint chip = P(Mix).Program.ClueMask, split = P(Arc, Cool).Program.ClueMask, single = P(Cool).Program.ClueMask, arcOnly = P(Arc).Program.ClueMask;
            Expect(idx >= 0 && (chip & bit) != 0 && (split & bit) != 0 && (single & bit) == 0 && (arcOnly & bit) == 0,
                $"D1 线索端口对（湿弧 = 电弧 en.elec / 连锁 × 冷却液 mat.coolant，配方书第 {idx + 1} 条）：熔合芯片与两父分装的程序都带这一位，单装冷却液 / 单装电弧不带（按端口，不看固件 ID）");
            // EMG3-E2E-01（FGJ-M5 发现）：规则成立这条路——C03 短路的一端是沾染位 coat:2（浸湿），按“写的物质沾上这一位”展开后，[冷却液] × [电弧] 打出短路计湿弧同现（原来只计到漫流电弧）
            string[] conduct = EmgRuleCatalog.RecipesOfKey(NamedReactionCatalog.StableKey("reaction_conduct"));
            Expect(Array.IndexOf(conduct, "fusion.wetarc") >= 0 && Array.IndexOf(conduct, "fusion.floodarc") >= 0,
                $"D1b 规则成立计同现（短路 = 浸湿 coat:2 × 电 en.elec）：配方 = {string.Join(" / ", conduct)}；冷却液、水都浸湿，湿弧与漫流电弧都计");
            using var k = NewKernel();
            CombatWeapon w = WeaponOf(k, P(Mix), out _);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int t0 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
            for (int s = 0; s < 3; s++)
            {
                k.FireAt(a, t0, k.Time);
                for (int i = 0; i < 4; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
            }
            bool dirty = (k.EmgClueDirty & bit) != 0;
            int got = k.EmgClueTake(idx), again = k.EmgClueTake(idx);
            k.EmgClueClearDirty();
            int settled = k.EmgScalars.RulesSettled;
            Expect(dirty && got == 3 && again == 0 && k.EmgClueDirty == 0u,
                $"D2 可靠计数 + 脏位：3 次出手（每次连锁跳 2 个目标、执行 2 个落点；期间规则成立 {settled} 次，规则成立与出手同一计数、按链去重）计 {got} 次同现——同一条链里只计一次；取走后清零（再取 {again}），脏位清空");
            CheckClueSave(idx, bit);
            {
                // EMG3-E2E-01（FGJ-M5 发现）D2b：跨机器——目标站在一摊冷却液里被浸湿（沾染位 2；本夹具不折旧读法，冷却液洼用 SpawnPatch 直接放），电弧机打它：
                // 单位层 C03 短路成立，计湿弧同现（两父分装在不同机器上时，同现只能由规则成立计）
                using var k2 = NewKernel();
                int arc = Unit(k2, CombatFaction.Player, new double2(1, 0), k2.AddWeapon(WeaponOf(k2, P(Arc), out _)), 100f);
                int tgt = Unit(k2, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
                k2.SpawnPatch(EmgMatterConst.Coolant, new double2(0, 5), 2f, 1f, 30f);
                for (int i = 0; i < 20; i++)
                {
                    k2.Step(Dt, k2.Time + Dt);
                }
                for (int s2 = 0; s2 < 3; s2++)
                {
                    k2.FireAt(arc, tgt, k2.Time);
                    for (int i = 0; i < 20; i++)
                    {
                        k2.Step(Dt, k2.Time + Dt);
                    }
                }
                int rules = k2.EmgScalars.RulesSettled, co = k2.EmgClueTake(idx);
                k2.TryGetUnit(tgt, out CombatUnitView tv);
                Expect(rules > 0 && co > 0, $"D2b 跨机器规则成立计同现：目标站在冷却液洼里浸湿、[电弧] 机打它 3 发，规则成立 {rules} 次、湿弧同现 {co} 次（> 0）；目标血 {tv.Health:0}、斑块 {k2.PatchCount}");
            }
            int fi = -1;
            for (int i = 0; i < FusionCatalog.Recipes.Count; i++)
            {
                if (FusionCatalog.Recipes[i].Id == "fusion.flashover")
                {
                    fi = i;
                }
            }
            uint fbit = fi >= 0 ? 1u << fi : 0u;
            uint capOil = P("fw_capacitor", "fw_oilleak").Program.ClueMask, overOil = P(Overload, "fw_oilleak").Program.ClueMask;
            Expect(fi >= 0 && (capOil & fbit) == 0u && (overOil & fbit) != 0u,
                "D3 线索端口不含语法修饰：[电容, 漏油] 不算闪燃过载（电容与过载都写 grm.mod.power，但没有注热）；[过载, 漏油] 算");
            EmgProgramResult full = P(Mix);
            EmgProgramResult cut = EmgProgramCompiler.Truncate(full, 1, false);
            Expect(cut != null && (full.Program.ClueMask & bit) != 0u && (cut.Program.ClueMask & bit) == 0u,
                $"D4 刷写截断到 1 条（只剩电弧）的变体不再带湿弧线索位（截断前 {full.Program.ClueMask:X}、截断后 {cut?.Program.ClueMask ?? 0u:X}）");
        }

        // ── E. 空场景 ──

        private static void CheckEmpty()
        {
            EmgProgramResult r = P(Arc, Cool);
            Run1 run = Scene(r);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EmgProgramResult chip = null;
            for (int i = 0; i < 200; i++)
            {
                chip = P(Mix);
            }
            sw.Stop();
            Expect(run.Fired == 0 && (r.Program.Flags & EmgProgramFlags.HasAnchor) == 0 && chip != null,
                $"E1 没有接线的程序：程序不带 HasAnchor，落点处 O(1) 返回（执行 0 次）；编译湿弧 200 次 {sw.Elapsed.TotalMilliseconds:F1} ms（装配变化时才编译，Editor 量级参考）");
        }

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

        /// <summary>D5 线索去重状态（快照段 26）存读档：出手后在途链中途存档，读档后①没取走的同现次数与脏位原样恢复；②同一条链后续的规则成立不再计（链环恢复），
        /// 与不存档的对照逐步哈希一致；③段 26 被截成空记录的对照档（链环丢失）读档后哈希与原档不同，证明②的哈希比对测得出链环丢失。</summary>
        private static void CheckClueSave(int idx, uint bit)
        {
            using var k = NewKernel();
            CombatWeapon w = WeaponOf(k, P(Mix), out _);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int t0 = Unit(k, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
            Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
            k.FireAt(a, t0, k.Time);
            k.Step(Dt, k.Time + Dt);
            using var c1 = NewKernel();
            bool load1 = c1.Load(k.Serialize()) == CombatLoadResult.Ok;
            bool dirty1 = (c1.EmgClueDirty & bit) != 0 && c1.EmgClueDirty == k.EmgClueDirty;
            int c1Got = c1.EmgClueTake(idx), kGot = k.EmgClueTake(idx);
            k.EmgClueClearDirty();
            // 热更层已取走之后再存一次：只剩链环，读档后的续跑不应补计
            using var c2 = NewKernel();
            using var c3 = NewKernel();
            bool load2 = c2.Load(k.Serialize()) == CombatLoadResult.Ok;
            bool load3 = c3.Load(k.SerializeEmgShapeForTests(EmgConst.SegClue, 0, 0)) == CombatLoadResult.Ok;
            bool every = c2.StateHash() == k.StateHash();
            bool sensed = c3.StateHash() != k.StateHash(); // 段 26 进 EmgStateHash：链环丢了哈希就对不上
            for (int i = 0; i < 8; i++)
            {
                k.Step(Dt, k.Time + Dt);
                c2.Step(Dt, c2.Time + Dt);
                every &= k.StateHash() == c2.StateHash();
            }
            int kPost = k.EmgClueTake(idx), c2Post = c2.EmgClueTake(idx);
            Expect(load1 && load2 && load3 && dirty1 && kGot == 1 && c1Got == kGot && every && kPost == 0 && c2Post == kPost && sensed,
                $"D5 线索去重状态随快照段 26 往返：出手 1 步后存档，读档后待取走次数 {c1Got}（原档 {kGot}）与脏位一致（{dirty1}）；取走后再存档读档续跑 8 步不补计（原档 {kPost} / 读档 {c2Post}，逐步哈希一致 {every}）；对照：段 26 截空的档读档后哈希与原档不同（{sensed}，证明逐步哈希比对测得出链环丢失）");
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
