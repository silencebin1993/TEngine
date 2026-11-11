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

        [MenuItem("BinGames/QA/自检/熔合重做与落点接线（EMG3-FUSE-01 / EMG4-FUSE-02）")]
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
            EmgProgramResult first = P(Wire, Cool), last = P(Cool, Wire), modOnly = P(Arc, Wire, Pierce), none = P(Acid, Wire, Cool), trig = P(Split, Wire, Cool);
            bool reasons = HasNote(first, EmgProgramCompiler.NoteAnchorDangling) && HasNote(last, EmgProgramCompiler.NoteAnchorDangling)
                           && HasNote(modOnly, EmgProgramCompiler.NoteAnchorModOnly) && HasNote(none, EmgProgramCompiler.NoteAnchorNone)
                           && (HasNote(trig, EmgProgramCompiler.NoteAnchorNone) || HasNote(trig, EmgProgramCompiler.NoteAnchorSplit));
            bool inert = new[] { first, last, modOnly, none, trig }.All(r => TailRows(r) == 0 && r.HeatAnchor == 0f && r.Anchors.All(a => !a.Valid));
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(first, rows);
            bool shown = rows.Any(x => x.Kind == EmgTreeRowKind.Anchor && x.Text.Contains("接线未生效")) && rows.Any(x => x.Kind == EmgTreeRowKind.Reason);
            Expect(reasons && inert && shown,
                "B9 负向（首位 / 末位定为无效，预览写原因、不静默）：接线在首位或末位 → “前面或后面没有可接的指令”；后一条只有修饰（穿透）→ 修饰照常作用于节点、不接线；前一条没有落点（酸液落点排放）→ 预混不接线；" +
                "前一条是触发器 → 不接线；以上都不复制、不计接线热量（EMG4-FUSE-02 起 15 种落点全部接通，没有“未接通”这一类）");
            EmgProgramResult over = Pc(2, Cool, Mix);
            EmgProgramResult dup = P(Mix, Cool);
            Expect(over.Steps.Last().Overflow && HasNote(over, EmgProgramCompiler.NoteOverflow) && HasNote(over, EmgProgramCompiler.NoteAnchorDangling) && TailRows(over) == 0
                   && HasNote(dup, EmgProgramCompiler.NoteDuplicate),
                $"B7 混合固件放进只剩 1 条容量的节点：第二条（冷却液）溢出、预览写原因，内置接线随之无效（{Steps(over)}）；两父也单独出现在同一程序里 → 同端口幂等、重复的标“重复无效”");
            EmgProgramResult mixes = P(Mix, Mix);
            Expect(mixes.Steps.Count(s => s.Builtin) == 2 && mixes.Nodes[0].Used <= mixes.RootCapacity,
                $"B8 两枚熔合芯片：各自展开 2 条 + 内置接线（{Steps(mixes)}）；混合固件不能再熔合（D-09）由 FgFusionSelfCheck 验");
            // EMG4-FUSE-02：回波的动能注入（余震）、燃迹的沿途段点、事件门控的注能（过热泄放读 evt.overheat）、折返都已接通——生效、复制、计接线热量
            EmgProgramResult echo = P(Arc, Wire, "fw_echo"), trail = P("fw_burntrail", Wire, Cool), gated = P("fw_ventarc", Wire, Cool), hitInj = P(Overload, Wire, Cool), ret = P(Boomerang, Wire, Cool);
            bool Live1(EmgProgramResult r) => r.Anchors.Count == 1 && r.Anchors[0].Valid && TailRows(r) > 0 && r.HeatAnchor > 0f
                                              && !HasNote(r, EmgProgramCompiler.NoteAnchorPending) && !HasNote(r, EmgProgramCompiler.NoteAnchorNoExec);
            Expect(Live1(echo) && Live1(trail) && Live1(ret) && trail.Anchors[0].Kind == EmgAnchorConst.Trail && ret.Anchors[0].Kind == EmgAnchorConst.Return,
                $"B10 [电弧, 接线, 回波]（动能注入在落点执行余震）、[燃迹, 接线, 冷却液]（沿途段点）、[回旋, 接线, 冷却液]（折返点）都生效：复制 B、计接线热量（{echo.HeatAnchor} / {trail.HeatAnchor} / {ret.HeatAnchor}），不再写“未接通 / 没有执行方式”");
            Expect(Live1(gated) && Live1(hitInj) && gated.Anchors[0].Kind == EmgAnchorConst.Inject,
                $"B12 [过热泄放, 接线, 冷却液]：事件门控注能的脉冲分支已有落点钩子（EmgRootGated）→ 生效、计热 {gated.HeatAnchor}（与 [过载, 接线, 冷却液] 的 {hitInj.HeatAnchor} 同口径）");
            EmgProgramResult follow = P("fw_followup", Wire, Cool);
            Expect(Live1(follow) && HasNote(follow, EmgProgramCompiler.NoteAnchorFollowSolo),
                "B13 [追射, 接线, 冷却液]：追射落点生效；预览写明“附近没有第二个目标时补射打原目标、不带后一条（重复无效，FGR-EMG-017）”");
            // 修复第 2 轮（审查 P1）：子节点里的追射接线内核不改打、不带 B（补射记录固定挂根节点）→ 编译期写原因、不复制、不计热，不再“预览生效、实际不执行”
            EmgProgramResult childFollow = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { Split, "fw_followup", Wire, Cool }, FirmwareCarrier.Projectile, 16); // 根容量 16 → 子节点 8，三条都进分裂的子节点
            EmgAnchorInfo cfa = childFollow.Anchors.FirstOrDefault();
            Expect(childFollow.Anchors.Count == 1 && !cfa.Valid && cfa.Note == EmgProgramCompiler.NoteAnchorFollowChild && TailRows(childFollow) == 0 && childFollow.HeatAnchor == 0f
                   && !HasNote(childFollow, EmgProgramCompiler.NoteAnchorFollowSolo),
                $"B13b [分裂, 追射, 接线, 冷却液]（追射在触发器子节点里）：接线不生效、写 anchor_follow_child（{cfa.Note}）、尾段 {TailRows(childFollow)} 行、热量 {childFollow.HeatAnchor}（都应为 0）");
            // 30 条配方芯片逐条：接线全部生效（预混 / 触发器子节点除外），没有一条写“未接通 / 没有执行方式”
            var inactive = new List<string>();
            int valid = 0, premixN = 0;
            foreach (FusionRecipeDef def in FusionCatalog.Recipes)
            {
                EmgProgramResult m = P(def.MixId);
                EmgAnchorInfo ai = m.Anchors.FirstOrDefault();
                bool premix = !ai.Valid && (ai.Note == EmgProgramCompiler.NoteAnchorNone || ai.Note == EmgProgramCompiler.NoteAnchorSplit);
                bool bad = HasNote(m, EmgProgramCompiler.NoteAnchorPending) || HasNote(m, EmgProgramCompiler.NoteAnchorNoExec);
                valid += ai.Valid ? 1 : 0;
                premixN += premix ? 1 : 0;
                if ((!ai.Valid && !premix) || bad)
                {
                    inactive.Add(def.Id.Replace("fusion.", string.Empty) + "（" + (ai.Note ?? "部分写端无执行者").Replace("emg.compile.anchor_", string.Empty) + "）");
                }
            }
            Expect(inactive.Count == 0 && valid + premixN == FusionCatalog.Recipes.Count,
                $"B11 30 条配方芯片的接线全部生效（预混 / 触发器子节点除外）：生效 {valid}、预混或子节点 {premixN}；未生效 {inactive.Count} 条{(inactive.Count > 0 ? "：" + string.Join("、", inactive) : string.Empty)}");
            // FGT-EMG-054 逐条：每条芯片 = [A, 内置接线, B] 与玩家手摆的 [A, 接线, B] 编出的程序逐字节相同、签名相同（只差容量）
            var differ = new List<string>();
            int same = 0, total = 0;
            foreach (FusionRecipeDef def in FusionCatalog.Recipes)
            {
                EmgProgramResult m = P(def.MixId);
                if (m.Steps.Count != 3 || !m.Steps[1].Builtin)
                {
                    differ.Add(def.Id.Replace("fusion.", string.Empty) + "（展开不是 3 条）");
                    total++;
                    continue;
                }
                total++;
                EmgProgramResult hand = P(m.Steps[0].OwnerId, Wire, m.Steps[2].OwnerId);
                if (Key(m) == Key(hand) && m.Signature == hand.Signature && m.Program.ClueMask == hand.Program.ClueMask)
                {
                    same++;
                }
                else
                {
                    int di = 0;
                    while (di < m.KernelInstrs.Count && di < hand.KernelInstrs.Count && JsonUtility.ToJson(m.KernelInstrs[di]) == JsonUtility.ToJson(hand.KernelInstrs[di]))
                    {
                        di++;
                    }
                    string rowM = di < m.KernelInstrs.Count ? JsonUtility.ToJson(m.KernelInstrs[di]) : "-", rowH = di < hand.KernelInstrs.Count ? JsonUtility.ToJson(hand.KernelInstrs[di]) : "-";
                    differ.Add(def.Id.Replace("fusion.", string.Empty) + $"（程序 {Key(m) == Key(hand)} / 签名 {m.Signature == hand.Signature} / 行数 {m.KernelInstrs.Count}:{hand.KernelInstrs.Count} 首个不同行 {di}：{rowM} vs {rowH}）");
                }
            }
            Expect(total == FusionCatalog.Recipes.Count && same == total,
                $"B14 FGT-EMG-054 30 条逐条等价：熔合芯片与 [A, 接线, B] 程序逐字节、签名、线索掩码相同 {same}/{total}{(differ.Count > 0 ? "；不同：" + string.Join("、", differ) : string.Empty)}");
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

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, float speed = 0f) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Speed = speed, Health = hp, MaxHealth = hp,
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
            CheckRecipeAnchors();
            {
                // 负向：追射没有第二个目标 → 打原目标、B 不执行（FGR-EMG-017 重复无效）
                Run2 solo = SceneAt(P("fw_followup", Wire, Cool), new[] { new double2(0, 5) }, new double2(0, 5), 1f);
                Expect(solo.Fired == 0, $"C8 追射接线附近没有第二个目标：补射打原目标、不执行 B（接线执行 {solo.Fired} 次，应 0）");
            }
            {
                // 修复轮（审查 P1）：注能落点只认注入那一刻已有、被这次注能加热的斑块——B 在落点上刚排出的可吸热斑块（漏油）不再被当成 A 的落点、不重复执行、不占名额
                Run2 pre = SceneAt(P(Overload, Wire, "fw_oilleak"), new[] { new double2(0, 5) }, new double2(2, 5), 0.9f, EmgMatterConst.Water, new double2(2, 5));
                Run2 bare = SceneAt(P(Overload, Wire, "fw_oilleak"), new[] { new double2(0, 5) }, new double2(0, 5), 0.9f);
                Expect(pre.Fired == 2 && bare.Fired == 1 && pre.Amt[EmgMatterConst.Oil] > 0f && pre.Amt[EmgMatterConst.Oil] <= bare.Amt[EmgMatterConst.Oil] + 1e-3f,
                    $"C6b 预置 1 摊会吸热的水 + [过载, 接线, 漏油]：接线执行 {pre.Fired} 次（应 2 = 已有斑块 1 + 目标 1，B 排出的油不算落点）；水洼上的油 {pre.Amt[EmgMatterConst.Oil]:0.0} 不超过 1 次排放（无水洼时目标处 {bare.Amt[EmgMatterConst.Oil]:0.0}、接线 {bare.Fired} 次）");
            }
            CheckAnchorKinds();
            CheckTrailHost();
        }

        /// <summary>落点区 <paramref name="at"/> 半径 <paramref name="r"/> 内的明火份量合计。</summary>
        private static float FireNear(CombatKernel k, double2 at, float r)
        {
            float sum = 0f;
            for (int i = 0; i < 4096 && k.TryGetPatch(i, out CombatZone z); i++)
            {
                if (z.Matter == EmgMatterConst.Fire && math.distance(z.Pos, at) <= r)
                {
                    sum += z.Amount;
                }
            }
            return sum;
        }

        /// <summary>
        /// 修复第 3 轮（FGJ-M10 根因核查 fgj-m10-boss-diagnosis.md）：C11 静止 / 格斗开火的燃迹不落在出手者脚下；C12 evt.fire 结算时宿主的阵营 / 载体 / 主目标已填好
        /// （敌方带燃迹按敌方判敌我）；C13 物质不认敌我——行进中段点压在己方单位身上照排（FG19）。
        /// </summary>
        private static void CheckTrailHost()
        {
            float still, stillTarget, melee, meleeTarget;
            using (var k = NewKernel())
            {
                CombatWeapon w = WeaponOf(k, P("fw_burntrail"), out _);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
                int t = Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
                k.FireAt(a, t, k.Time);
                k.Step(Dt, k.Time + Dt);
                (still, stillTarget) = (FireNear(k, new double2(0, 0), 2f), FireNear(k, new double2(0, 9), 1f));
            }
            using (var k = NewKernel())
            {
                CombatWeapon w = WeaponOf(k, P("fw_burntrail"), out _);
                w.Reading.Carrier = CombatCarrier.Melee;
                w.Reading.Area = 3f;
                w.Reading.Cone = 60f;
                w.Range = 3f;
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
                int t = Unit(k, CombatFaction.Hostile, new double2(0, 2.5), -1, 5000f);
                k.FireAt(a, t, k.Time);
                k.Step(Dt, k.Time + Dt);
                (melee, meleeTarget) = (FireNear(k, new double2(0, 0), 1f), FireNear(k, new double2(0, 2.5), 0.5f));
            }
            Expect(still < 1e-4f && stillTarget > 0.3f + 1e-3f && melee < 1e-4f && meleeTarget > 0f,
                $"C11 燃迹不落在出手者脚下：静止开火出手者 2 米内明火 {still:0.00}（应 0）、终结点 {stillTarget:0.00}（> 单段 0.3，n 段合计）；格斗载体出手者 1 米内 {melee:0.00}（应 0）、主目标处 {meleeTarget:0.00}（> 0）");

            // C12：敌方出手者带腐焰弹头（燃迹 → 腐蚀），静止开火 → 落点对象 = 终结点附近与敌方敌对的单位（玩家目标）。
            // 原来 evt.fire 结算时宿主 Faction 还是 0 = 玩家，玩家目标被当成“己方”排除，落点没有对象、腐蚀度注不进去（与只带燃迹的对照状态相同）
            string Hostile(EmgProgramResult r)
            {
                using var k = NewKernel();
                CombatWeapon w = WeaponOf(k, r, out _);
                int a = Unit(k, CombatFaction.Hostile, new double2(0, 0), k.AddWeapon(w), 100f);
                int t = Unit(k, CombatFaction.Player, new double2(0, 9), -1, 5000f);
                k.FireAt(a, t, k.Time);
                for (int i = 0; i < 10; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                return k.TryGetUnitEmg(t, out _, out EmgUnitState ts) ? JsonUtility.ToJson(ts) : "?";
            }
            string chipT = Hostile(P("fw_mix_causticshell")), plainT = Hostile(P("fw_burntrail"));
            Expect(chipT != plainT && chipT != "?",
                $"C12 敌方带燃迹（宿主阵营在 evt.fire 结算前已填好）：腐焰弹头的落点对象是玩家目标，目标涌现状态与只带燃迹的对照{(chipT != plainT ? "不同（注入了腐蚀度）" : "相同 ✗（敌我判反，落点没有对象）")}");

            // C13：物质不认敌我——行进中开火，弹道上站着一台己方机器，段点照排（原 EmgFriendlyOn 跳过，已删）
            float onAlly;
            using (var k = NewKernel())
            {
                CombatWeapon w = WeaponOf(k, P("fw_burntrail"), out _);
                int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f, 4f);
                Unit(k, CombatFaction.Player, new double2(0, 4.6), -1, 100f);
                int t = Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
                k.IssueCommand(a, CombatCommandKind.Move, new double2(-30, 0), 0, 0.2f, 0f, 0f, false);
                for (int i = 0; i < 3; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                k.FireAt(a, t, k.Time);
                k.Step(Dt, k.Time + Dt);
                onAlly = FireNear(k, new double2(0, 4.6), 1f);
            }
            Expect(onAlly > 0f, $"C13 物质不认敌我：行进中开火、弹道上站着己方机器，它身边 1 米内照样排了明火 {onAlly:0.00}（> 0；段距 1.5 米，FG19A 第 2 节第 33 行）");
        }

        // ── C7. EMG4-FUSE-02：15 条“落点接线”配方，B 的效果出现在 A 的落点上（不只是命中点）──

        private struct Run2
        {
            public int Fired;
            public string Region;
            /// <summary>落点区域里各物质（下标 = 物质码）的份量合计。</summary>
            public float[] Amt;
            /// <summary>整场“粉尘 × 热”规则行（E04 玻化 / E05 尘爆，按端口挑：a = mat.dust、b = en.heat）成立次数。</summary>
            public int DustRules;
        }

        /// <summary>
        /// 一台机器（0, 0）对 enemies[0] 开一次火，跑 60 步；返回接线执行次数与“落点区域”（圆心 at、半径 r0）里的物质斑块种类表 + 第二个敌人的涌现状态（JSON）。
        /// pre = 预先放好的斑块（物质, 位置）。对照组 [A, B]（不接线）与熔合芯片比区域签名：B 的效果是否出现在落点上。
        /// </summary>
        private static Run2 SceneAt(EmgProgramResult r, double2[] enemies, double2 at, float r0, byte preMatter = 0, double2 prePos = default, float firstHp = 5000f, bool moving = false)
        {
            using var k = NewKernel();
            k.InstrActivationCounting = true; // 只为按行数规则成立次数（计数不进快照、不进哈希）
            CombatWeapon w = WeaponOf(k, r, out _);
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f, moving ? 4f : 0f);
            var ids = new List<int>();
            for (int i = 0; i < enemies.Length; i++)
            {
                ids.Add(Unit(k, CombatFaction.Hostile, enemies[i], -1, i == 0 ? firstHp : 5000f));
            }
            if (preMatter != 0)
            {
                k.SpawnPatch(preMatter, prePos, 1f, 1f, 30f);
                k.Step(Dt, k.Time + Dt);
            }
            if (moving)
            {
                // 燃迹 / 拖尾读 chs.moving：行进中开火才沿途铺线（FG19A 第 2 节第 33 行），出手者横向走着开火
                k.IssueCommand(a, CombatCommandKind.Move, new double2(-30, 0), 0, 0.2f, 0f, 0f, false);
                for (int i = 0; i < 3; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
            }
            k.FireAt(a, ids[0], k.Time);
            for (int i = 0; i < 60; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            var kinds = new List<string>();
            var amt = new float[EmgMatterConst.Count + 1];
            for (int i = 0; i < 4096 && k.TryGetPatch(i, out CombatZone z); i++)
            {
                if (z.Matter != 0 && math.distance(z.Pos, at) <= r0)
                {
                    kinds.Add(z.Matter + ":" + z.Amount.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    if (z.Matter <= EmgMatterConst.Count)
                    {
                        amt[z.Matter] += z.Amount;
                    }
                }
            }
            int dust = 0;
            EmgRuleCatalog.Compiled cat = EmgRuleCatalog.Current;
            for (int i = 0; i < cat.Rules.Count; i++)
            {
                if (cat.Rules[i].A == EmgMatterConst.PortBase + EmgMatterConst.Dust && cat.Rules[i].B == EmgConst.PortHeat)
                {
                    dust += k.EmgRuleRowActivations(cat.Rules[i].IdKey);
                }
            }
            kinds.Sort(StringComparer.Ordinal);
            string unit = ids.Count > 1 && k.TryGetUnitEmg(ids[1], out _, out EmgUnitState st) ? JsonUtility.ToJson(st) : string.Empty;
            return new Run2 { Fired = k.EmgScalars.AnchorFired, Region = string.Join(",", kinds) + "|" + unit, Amt = amt, DustRules = dust };
        }

        private static void CheckRecipeAnchors()
        {
            var lines = new List<string>();
            int ok = 0;
            void One(string mix, string a, string b, double2[] enemies, double2 at, float r0, int minFired, byte preMatter = 0, double2 prePos = default, float firstHp = 5000f,
                     Func<Run2, Run2, bool> spec = null, Func<Run2, Run2, string> specNote = null, bool moving = false)
            {
                Run2 chip = SceneAt(P(mix), enemies, at, r0, preMatter, prePos, firstHp, moving), ctl = SceneAt(P(a, b), enemies, at, r0, preMatter, prePos, firstHp, moving);
                bool pass = chip.Fired >= minFired && ctl.Fired == 0 && chip.Region != ctl.Region && (spec == null || spec(chip, ctl));
                ok += pass ? 1 : 0;
                string[] cr = chip.Region.Split('|'), tr = ctl.Region.Split('|');
                lines.Add((pass ? "✓" : "✗") + mix.Replace("fw_mix_", string.Empty) + $" 接线 {chip.Fired}（对照 {ctl.Fired}）落点区 [{cr[0]}] / 对照 [{tr[0]}]" + (cr[1] != tr[1] ? " 第二目标状态不同" : string.Empty)
                          + (specNote != null ? "，" + specNote(chip, ctl) : string.Empty));
            }
            double2[] line = { new double2(0, 9) };
            // 沿途段点（燃迹 → B）：路径中段（y ∈ 1～7.5，不含 9 米处的命中点）里出现 B 的物质 / 它与明火的产物
            foreach ((string mix, string b) in new[] { ("fw_mix_napalm", "fw_oilleak"), ("fw_mix_steamjet", "fw_coolant"), ("fw_mix_cryoflame", "fw_nitrogen"), ("fw_mix_causticflame", "fw_acid"),
                         ("fw_mix_causticshell", "fw_corrode") })
            {
                One(mix, "fw_burntrail", b, line, new double2(0, 4.25), 3.25f, 2, moving: true);
            }
            // 玻化弹：曲射的粉尘排放（ter.obstacle 是 sense：按障碍调强弱，不是成立条件，没有障碍也排）落在燃迹段点上，与段上明火立即按“粉尘 × 热”成立
            // （新排的粉尘浓度 1 >= 0.6 → E05 尘爆、吃掉明火；变薄后才走 E04 玻化），所以判据 = 粉尘 × 热的规则成立次数比对照多（对照只在 9 米命中点排一次粉尘）
            One("fw_mix_glassburst", "fw_burntrail", "fw_arc", line, new double2(0, 4.25), 3.25f, 2,
                spec: (c, t) => c.DustRules > t.DustRules || c.Amt[EmgMatterConst.Dust] + c.Amt[EmgMatterConst.Glass] > t.Amt[EmgMatterConst.Dust] + t.Amt[EmgMatterConst.Glass],
                specNote: (c, t) => $"粉尘 × 热成立 {c.DustRules}（对照 {t.DustRules}），段上粉尘 / 玻化 {c.Amt[EmgMatterConst.Dust] + c.Amt[EmgMatterConst.Glass]:0.0}（对照 {t.Amt[EmgMatterConst.Dust] + t.Amt[EmgMatterConst.Glass]:0.0}）", moving: true);
            // 注能的完整落点集（过载 → B）：命中点 2 米外一摊会吃热的水被加热，它的中心也排一摊 B
            double2[] one = { new double2(0, 5) };
            One("fw_mix_flashover", Overload, "fw_oilleak", one, new double2(2, 5), 0.9f, 2, EmgMatterConst.Water, new double2(2, 5),
                spec: (c, t) => c.Fired == 2 && c.Amt[EmgMatterConst.Oil] > t.Amt[EmgMatterConst.Oil], specNote: (c, t) => $"水洼上的油 {c.Amt[EmgMatterConst.Oil]:0.0}（对照 {t.Amt[EmgMatterConst.Oil]:0.0}）");
            One("fw_mix_thermalspike", Overload, "fw_nitrogen", one, new double2(2, 5), 0.9f, 2, EmgMatterConst.Water, new double2(2, 5));
            // 场的完整落点集（牵引 / 冲击波 → B）：场内第二个敌人（1.5 米外）脚下 / 身上出现 B
            double2[] pair = { new double2(0, 5), new double2(1.5, 5.5) };
            // B = 燃迹：行进中开火（静止时对照组的明火堆在主目标处，会把落点上 B 的小火并过去）。修复第 3 轮段距按规格 1.5 米后，弹道段点离第二个敌人更近，落点上 B 的小火会与段点 / 命中点的明火重叠合并
            // （份量相加、中心按份量移动），所以判据改成“第二个敌人 2 米内的明火合计份量比对照多”（合并守恒份量）
            One("fw_mix_cinderpull", "fw_tractor", "fw_burntrail", pair, new double2(1.5, 5.5), 2f, 2, moving: true,
                spec: (c, t) => c.Amt[EmgMatterConst.Fire] > t.Amt[EmgMatterConst.Fire] + 1e-3f, specNote: (c, t) => $"第二目标 2 米内明火 {c.Amt[EmgMatterConst.Fire]:0.00}（对照 {t.Amt[EmgMatterConst.Fire]:0.00}）");
            One("fw_mix_stormwave", "fw_shockwave", Arc, pair, new double2(1.5, 5.5), 0.9f, 2);
            One("fw_mix_steamshock", "fw_shockwave", "fw_burntrail", pair, new double2(1.5, 5.5), 0.9f, 2, moving: true); // B = 燃迹：行进中开火（静止时对照组的明火堆在主目标处，会把落点上 B 的小火并过去）
            // 追射（追射 → B）：补射改打 5 米内的第二个目标，B 出现在第二个目标处
            double2[] two = { new double2(0, 5), new double2(3, 6) }; // 第二目标偏开弹道（补射从出手者飞向它，不会先撞上原目标）
            One("fw_mix_shortloop", "fw_followup", Cool, two, new double2(3, 6), 1.2f, 1);
            One("fw_mix_ioncorrode", "fw_followup", "fw_corrode", two, new double2(3, 6), 1.2f, 1);
            // 拆解（回收修复 → 腐蚀弹头）：击毁目标后在被拆解的残骸处溅酸
            One("fw_mix_meltreclaim", "fw_reclaim", "fw_corrode", one, new double2(0, 5), 1.2f, 1, 0, default, 5f);
            {
                // 修复第 2 轮（FGJ-M10 查出）：燃迹读 chs.moving——静止开火不铺线，n 段合计份量在主目标处堆成一块；行进中开火沿途铺段点（FG19A 第 2 节第 33 行）
                Run2 still = SceneAt(P("fw_burntrail"), line, new double2(0, 4.25), 3.25f), go = SceneAt(P("fw_burntrail"), line, new double2(0, 4.25), 3.25f, moving: true);
                Run2 stillEnd = SceneAt(P("fw_burntrail"), line, new double2(0, 9), 1.5f);
                bool c10 = still.Amt[EmgMatterConst.Fire] < 1e-3f && go.Amt[EmgMatterConst.Fire] > 0f && stillEnd.Amt[EmgMatterConst.Fire] > 0.3f + 1e-3f;
                Expect(c10, $"C10 燃迹静止开火：路径中段明火 {still.Amt[EmgMatterConst.Fire]:0.00}（应 0）、目标处堆成一块 {stillEnd.Amt[EmgMatterConst.Fire]:0.00}（> 单段 0.3）；行进中开火路径中段明火 {go.Amt[EmgMatterConst.Fire]:0.00}（> 0）");
            }
            int expect = lines.Count + 1;
            bool mirror = CheckNullMirror(out string mline);
            ok += mirror ? 1 : 0;
            lines.Add(mline);
            Expect(ok == expect, $"C7 15 条“落点接线”配方的 B 出现在 A 的落点上（熔合芯片 vs 对照 [A, B] 不接线：落点区域状态不同、接线执行 ≥ 落点数）：通过 {ok}/{expect}——{string.Join("；", lines)}");
        }

        // ── C9. 修复轮（审查 P1）：本卡新接的其余落点种类与 B 执行者，各起内核跑一次（[A, 接线, B] vs [A, B]）——接线真的执行、落点上真的出现 B 的效果 ──

        private struct Run3
        {
            public int Fired, Dead, Pings, Drones;
            public float Amt, Lost;
        }

        private sealed class Fx
        {
            public double2[] Enemies = { new double2(0, 5) };
            public float[] Hp, MaxHp;
            public bool Flight, Fire = true;
            /// <summary>按正式流程建读法（CombatSite.ProgramReading 同一做法：CarrierReadings.Build + LegacySkip + FoldProgramReadings，主组件 comp_gun）——第一层的处决 / 伴飞走过渡读法。</summary>
            public bool Legacy;
            /// <summary>武器自带标记秒数（正式流程里来自功能组件 func_marker 的 evt.marked 声明，CombatSite.MarkSeconds）。</summary>
            public float MarkSeconds;
            public int Steps = 60;
            public byte PreMatter;
            public double2 PrePos;
            public float OverheatAt, InjectHeat;
        }

        /// <summary>一台机器（0, 0）按 <paramref name="f"/> 布置开一次火（或只注热到过热），跑 f.Steps 步；返回接线执行次数、全场冷却液份量、敌人失血 / 击毁数、频道广播次数。</summary>
        private static Run3 Play(EmgProgramResult r, Fx f)
        {
            using var k = NewKernel();
            CombatWeapon w = WeaponOf(k, r, out _);
            if (f.Flight && w.SpawnFlight == 0)
            {
                w.SpawnFlight = 1;
                w.ProjectileSpeed = 20f;
                w.ProjectileRadius = 0.15f;
                w.ProjectileLife = 3f;
            }
            if (f.OverheatAt > 0f)
            {
                w.OverheatAt = f.OverheatAt;
            }
            if (f.Legacy)
            {
                List<string> fws = r.Steps.Where(st => !st.Builtin).Select(st => st.OwnerId).Distinct().ToList();
                w.Reading = CombatSite.FoldProgramReadings(CarrierReadings.Build("comp_gun", null, null, fws, false, EmgProgramCompiler.LegacySkip(r)), r, false);
                w.Reading.Carrier = CombatCarrier.Projectile;
            }
            w.MarkSeconds = f.MarkSeconds;
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            var ids = new List<int>();
            var hp0 = new List<float>();
            for (int i = 0; i < f.Enemies.Length; i++)
            {
                float hp = f.Hp != null && i < f.Hp.Length ? f.Hp[i] : 5000f, max = f.MaxHp != null && i < f.MaxHp.Length ? f.MaxHp[i] : hp;
                hp0.Add(hp);
                ids.Add(k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | CombatUnitFlags.Report,
                    Position = f.Enemies[i], Home = f.Enemies[i], Radius = 0.5f, Health = hp, MaxHealth = max, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                    ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
                }));
            }
            if (f.PreMatter != 0)
            {
                k.SpawnPatch(f.PreMatter, f.PrePos, 1f, 1f, 30f);
                k.Step(Dt, k.Time + Dt);
            }
            if (f.InjectHeat > 0f)
            {
                k.InjectUnitHeat(a, f.InjectHeat);
            }
            if (f.Fire)
            {
                k.FireAt(a, ids[0], k.Time);
            }
            for (int i = 0; i < f.Steps; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            var run = new Run3 { Fired = k.EmgScalars.AnchorFired, Pings = (int)k.EmgBusStats.PingsSent, Drones = k.DroneCount };
            for (int i = 0; i < 4096 && k.TryGetPatch(i, out CombatZone z); i++)
            {
                if (z.Matter == EmgMatterConst.Coolant)
                {
                    run.Amt += z.Amount;
                }
            }
            for (int i = 0; i < ids.Count; i++)
            {
                bool alive = k.TryGetUnit(ids[i], out CombatUnitView v) && v.Alive;
                run.Dead += alive ? 0 : 1;
                run.Lost += alive ? hp0[i] - v.Health : hp0[i];
            }
            return run;
        }

        private static void CheckAnchorKinds()
        {
            var lines = new List<string>();
            int ok = 0, n = 0;
            void Case(string name, EmgProgramResult chip, EmgProgramResult ctl, Fx f, Func<Run3, Run3, bool> eff, Func<Run3, string> show)
            {
                n++;
                Run3 c = Play(chip, f), t = Play(ctl, f);
                bool pass = c.Fired >= 1 && t.Fired == 0 && eff(c, t);
                ok += pass ? 1 : 0;
                EmgAnchorInfo ai = chip.Anchors.FirstOrDefault();
                lines.Add((pass ? "✓" : "✗") + $"{name} 接线 {c.Fired}（对照 {t.Fired}）{show(c)} / 对照 {show(t)}" + (pass ? string.Empty : $"〔落点 {ai.Kind} 生效 {ai.Valid} {ai.Note}〕"));
            }
            Func<Run3, Run3, bool> more = (c, t) => c.Amt > t.Amt + 1e-3f;
            Func<Run3, string> amt = r => $"冷却液 {r.Amt:0.00}";
            double2[] line3 = { new double2(0, 5), new double2(0, 9), new double2(0, 13) };
            // A 的落点种类（B = 冷却液：落点上多排出冷却液）
            Case("折返点（回旋）", P(Boomerang, Wire, Cool), P(Boomerang, Cool), new Fx { Flight = true, Steps = 150 }, more, amt);
            Case("绕行每 90°（绕行）", P("fw_orbit", Wire, Cool), P("fw_orbit", Cool), new Fx { Flight = true, Steps = 150 }, more, amt);
            // 第一层固件的 evt.marked 写端内核还没有执行点（FG-GAP-214，承接 EMG4-CLEAN-01）：标记来自武器的标记秒数（正式流程 = 功能组件 func_marker）
            Case("被标记的目标（锁定缓存 + 武器标记 4 秒）", P("fw_lockcache", Wire, Cool), P("fw_lockcache", Cool), new Fx { MarkSeconds = 4f }, more, amt);
            Case("伴飞放出处（蜂群，正式读法）", P("fw_swarm", Wire, Cool), P("fw_swarm", Cool), new Fx { Legacy = true }, more, amt);
            Case("导线两端（桥接，目标 2 米外一摊水）", P("fw_bridge", Wire, Cool), P("fw_bridge", Cool), new Fx { PreMatter = EmgMatterConst.Water, PrePos = new double2(2, 5) }, more, amt);
            Case("被处决的目标（A = 处决，目标血 12%，正式读法）", P("fw_execute", Wire, Cool), P("fw_execute", Cool), new Fx { Hp = new[] { 12f }, MaxHp = new[] { 100f }, Legacy = true }, more, amt);
            Case("事件门控注能脉冲（过热泄放，本机过热、不开火）", P("fw_ventarc", Wire, Cool), P("fw_ventarc", Cool), new Fx { Fire = false, OverheatAt = 50f, InjectHeat = 200f }, more, amt);
            // B 的执行者（A = 电弧连锁跳点）
            Case("B = 动能注入（回波余震）", P(Arc, Wire, "fw_echo"), P(Arc, "fw_echo"), new Fx { Enemies = line3 }, (c, t) => c.Lost > t.Lost + 1e-3f, r => $"敌人失血 {r.Lost:0.0}");
            Case("B = 频道广播 evt.ping（标记广播）", P(Arc, Wire, "fw_markcast"), P(Arc, "fw_markcast"), new Fx { Enemies = line3 }, (c, t) => c.Pings > t.Pings, r => $"广播 {r.Pings}");
            Case("B = 处决 + 破片（跳点目标血 12%）", P(Arc, Wire, "fw_execute"), P(Arc, "fw_execute"),
                new Fx { Enemies = line3, Hp = new[] { 5000f, 12f, 12f }, MaxHp = new[] { 5000f, 100f, 100f } }, (c, t) => c.Dead > t.Dead, r => $"击毁 {r.Dead}");
            Case("B = 伴飞 ent.drone（蜂群）", P(Arc, Wire, "fw_swarm"), P(Arc, "fw_swarm"), new Fx { Enemies = line3 }, (c, t) => c.Drones > t.Drones, r => $"伴飞 {r.Drones}");
            Expect(ok == n, $"C9 其余落点种类与 B 执行者的内核行为（熔合芯片写法 [A, 接线, B] vs 对照 [A, B]：接线执行 ≥ 1、对照 0，且落点上出现 B 的效果）：通过 {ok}/{n}——{string.Join("；", lines)}");
        }

        /// <summary>湮灭镜像：敌人（血 10%）的弹体打中本机 → 偏折场展开 → 下一发被反射回去，带着处决打中敌人 → 直接击毁；对照 [偏折, 处决] 反射弹不带处决，敌人只掉 10 血。</summary>
        private static bool CheckNullMirror(out string line)
        {
            bool Run(EmgProgramResult r, out int fired)
            {
                using var k = NewKernel();
                CombatWeapon w = WeaponOf(k, r, out _);
                int p = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 1000f);
                int ew = k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = -1,
                    ProjectileSpeed = 15f, ProjectileRadius = 0.15f, ProjectileLife = 3f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                });
                int e = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | CombatUnitFlags.Report,
                    Position = new double2(0, 6), Home = new double2(0, 6), Radius = 0.5f, Health = 100f, MaxHealth = 1000f, Weapon = ew, BehaviorProfile = -1, Priority = 1,
                    ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
                });
                for (int i = 0; i < 120; i++)
                {
                    if (i == 0 || i == 30)
                    {
                        k.FireAt(e, p, k.Time);
                    }
                    k.Step(Dt, k.Time + Dt);
                }
                fired = k.EmgScalars.AnchorFired;
                return !k.TryGetUnit(e, out CombatUnitView v) || !v.Alive;
            }
            bool chipDead = Run(P("fw_mix_nullmirror"), out int cf), ctlDead = Run(P("fw_deflect", "fw_execute"), out int tf);
            bool pass = chipDead && !ctlDead && cf >= 1 && tf == 0;
            line = (pass ? "✓" : "✗") + $"nullmirror 被反射的弹体带处决：敌人击毁 {chipDead}（对照 {ctlDead}），接线 {cf}（对照 {tf}）";
            return pass;
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
            Expect(Array.IndexOf(conduct, "fusion.wetarc") >= 0 && Array.IndexOf(conduct, "fusion.floodarc") < 0,
                $"D1b 规则成立计同现（短路 = 浸湿 coat:2 × 电 en.elec）：配方 = {string.Join(" / ", conduct)}；冷却液浸湿计湿弧；漫流电弧的一侧是“液体扩散”（蔓延的 modify 写端），短路不计它（FG19A 第 4.3 节第 30 行）");
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
            CheckClueHalfSave();
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
            uint Bit(string id)
            {
                int ix = FusionCatalog.Recipes.ToList().FindIndex(r => r.Id == id);
                return ix >= 0 ? 1u << ix : 0u;
            }
            uint rustbomb = Bit("fusion.rustbomb"), caustic = Bit("fusion.causticshell");
            uint forkSplit = P("fw_fork", "fw_split").Program.ClueMask, acidTrail = P(Acid, "fw_burntrail").Program.ClueMask;
            uint clusterClock = P("fw_cluster", "fw_clock").Program.ClueMask, trailCorrode = P("fw_burntrail", "fw_corrode").Program.ClueMask;
            uint flood = Bit("fusion.floodarc"), bindSpread = Bit("fusion.bindspread");
            uint coolArc = P(Cool, Arc).Program.ClueMask, acidArc = P(Acid, Arc).Program.ClueMask, oilGlue = P("fw_oilleak", "fw_adhesive").Program.ClueMask, glueGlue = P(Cool, "fw_adhesive").Program.ClueMask;
            uint spreadArc = P("fw_spread", Arc).Program.ClueMask, spreadGlue = P("fw_spread", "fw_adhesive").Program.ClueMask;
            Expect(flood != 0u && bindSpread != 0u && (coolArc & flood) == 0u && (acidArc & flood) == 0u && (oilGlue & bindSpread) == 0u && (glueGlue & bindSpread) == 0u
                   && (spreadArc & flood) != 0u && (spreadGlue & bindSpread) != 0u && (coolArc & bit) != 0u && (spreadArc & bit) == 0u,
                $"D6b “液体扩散”一侧只认蔓延的 modify 写端（FG19A 第 4.3 节第 21 / 30 行，DEBT-EMG3EXIT-01）：[冷却液, 电弧] {coolArc:X}、[酸液, 电弧] {acidArc:X} 不计漫流电弧；[漏油, 粘合剂] {oilGlue:X}、[冷却液, 粘合剂] {glueGlue:X} 不计胶结漫流；" +
                $"对照 [蔓延, 电弧] {spreadArc:X} 计漫流电弧、[蔓延, 粘合剂] {spreadGlue:X} 计胶结漫流；[冷却液, 电弧] 仍计湿弧、[蔓延, 电弧] 不计湿弧");
            {
                // 修复第 2 轮（审查 P2）：“液体扩散”一侧的运行时路径——蔓延真的扩开液体斑块时才记侧位（EmgSpread → EmgClueSeen(PortClueSpread)）：
                // 目标站在冷却液洼里，[蔓延, 电弧] 打它计漫流电弧；对照 [冷却液, 电弧]（只排液体、没有蔓延）不计
                int fl = FusionCatalog.Recipes.ToList().FindIndex(r => r.Id == "fusion.floodarc");
                int Flood(EmgProgramResult r)
                {
                    using var k4 = NewKernel();
                    int a4 = Unit(k4, CombatFaction.Player, new double2(0, 0), k4.AddWeapon(WeaponOf(k4, r, out _)), 100f);
                    int t4 = Unit(k4, CombatFaction.Hostile, new double2(0, 5), -1, 5000f);
                    k4.SpawnPatch(EmgMatterConst.Coolant, new double2(0, 5), 2f, 1f, 30f);
                    k4.Step(Dt, k4.Time + Dt);
                    for (int s4 = 0; s4 < 3; s4++)
                    {
                        k4.FireAt(a4, t4, k4.Time);
                        for (int i = 0; i < 20; i++)
                        {
                            k4.Step(Dt, k4.Time + Dt);
                        }
                    }
                    return k4.EmgClueTake(fl);
                }
                int sa = Flood(P("fw_spread", Arc)), ca = Flood(P(Cool, Arc));
                Expect(fl >= 0 && sa > 0 && ca == 0, $"D6c “液体扩散”运行时：冷却液洼上 [蔓延, 电弧] 计漫流电弧 {sa} 次（> 0）；对照 [冷却液, 电弧] 计 {ca} 次（应 0）");
            }
            Expect(rustbomb != 0u && caustic != 0u && (forkSplit & rustbomb) == 0u && (acidTrail & caustic) == 0u && (clusterClock & rustbomb) != 0u && (trailCorrode & caustic) != 0u,
                $"D6 线索按 FG19A 第 4.3 节逐条端口对（DEBT-EMG3EXIT-01）：[分叉, 分裂]（都只写 grm.trig、没有 tmp.beat）不计锈蚀子母弹（掩码 {forkSplit:X}）；[酸液, 燃迹]（酸液只写 mat.acid、没有 phs.corrode）不计腐焰弹头（{acidTrail:X}）；" +
                "对照 [子母弹, 时钟] 计锈蚀子母弹、[燃迹, 腐蚀弹头] 计腐焰弹头");
            {
                // FG-GAP-206 ①：出手只登记，链上两侧端口真的出现才计——打无敌目标（命中不结算，冷却液与电荷都没出现）不计；正常命中计 1
                int wi = FusionCatalog.Recipes.ToList().FindIndex(r => r.Id == "fusion.wetarc");
                int Shot(bool invul)
                {
                    using var k3 = NewKernel();
                    int a3 = Unit(k3, CombatFaction.Player, new double2(0, 0), k3.AddWeapon(WeaponOf(k3, P(Mix), out _)), 100f);
                    int t3 = k3.Spawn(new CombatSpawn
                    {
                        ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | (invul ? CombatUnitFlags.Invulnerable : 0),
                        Position = new double2(0, 5), Home = new double2(0, 5), Radius = 0.5f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                        ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f),
                    });
                    k3.FireAt(a3, t3, k3.Time);
                    for (int i = 0; i < 6; i++)
                    {
                        k3.Step(Dt, k3.Time + Dt);
                    }
                    return k3.EmgClueTake(wi);
                }
                int miss = Shot(true), hit = Shot(false);
                Expect(wi >= 0 && miss == 0 && hit == 1, $"D7 线索按链上两侧端口实际出现计数（FG-GAP-206 ①）：湿弧打无敌目标（没结算出电荷与冷却液）计 {miss} 次（应 0，原来出手即计 1）；正常命中计 {hit} 次");
            }
            {
                // FG-GAP-206 ③：规则行按自己的两端口匹配配方（不再是计数键下所有行的并集）
                uint wet = Bit("fusion.wetarc"), coatElec = EmgRuleCatalog.ClueMaskOfPair((ushort)(EmgRuleConst.CoatBase + 2), EmgConst.PortElec),
                     oilFire = EmgRuleCatalog.ClueMaskOfPair((ushort)(EmgMatterConst.PortBase + EmgMatterConst.Oil), (ushort)(EmgMatterConst.PortBase + EmgMatterConst.Fire));
                Expect((coatElec & wet) != 0u && (coatElec & Bit("fusion.floodarc")) == 0u && (oilFire & wet) == 0u && (oilFire & Bit("fusion.napalm")) != 0u,
                    $"D8 规则行按行匹配配方：浸湿 × 电（{coatElec:X}）计湿弧、不计漫流电弧（FG-GAP-206 ③ 原例子）；油 × 明火（{oilFire:X}）计燃油喷流、不计湿弧");
            }
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

        /// <summary>D9 线索在途登记（快照段 32）存读档：[燃迹, 冷却液] 用飞行弹（10 米 / 秒打 9 米外），出手时燃迹段点已排明火（mat.fire 一侧出现），弹还在飞、冷却液一侧没出现时存档：
        /// ①快照比“段 32 截空”的对照档长（段 32 真的写了）；②读档后续跑与不存档的原档逐步哈希一致、最终燃油 / 蒸汽配方（燃迹喷流 steamjet）同现次数相同（= 1）；
        /// ③段 32 丢掉“已出现的侧位”的对照档读档后哈希与原档不同、命中后不计（证明哈希与计数都测得出登记丢失）。</summary>
        private static void CheckClueHalfSave()
        {
            int si = FusionCatalog.Recipes.ToList().FindIndex(r => r.Id == "fusion.steamjet");
            EmgProgramResult r = P("fw_burntrail", Cool);
            using var k = NewKernel();
            CombatWeapon w = WeaponOf(k, r, out _);
            w.SpawnFlight = 1;
            w.ProjectileSpeed = 10f;
            w.ProjectileRadius = 0.15f;
            w.ProjectileLife = 3f;
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int t = Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
            k.FireAt(a, t, k.Time);
            for (int i = 0; i < 3; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            byte[] snap = k.Serialize(), cut = k.SerializeEmgShapeForTests(EmgConst.SegClueHalf, 2, 0); // 对照档：段 32 每条记录只留下标与链号，丢掉“已出现的侧位”
            bool seg = snap.Length > cut.Length;
            using var c = NewKernel();
            using var lost = NewKernel();
            bool load = c.Load(snap) == CombatLoadResult.Ok && lost.Load(cut) == CombatLoadResult.Ok;
            bool every = c.StateHash() == k.StateHash(), sensed = lost.StateHash() != k.StateHash();
            for (int i = 0; i < 90; i++)
            {
                k.Step(Dt, k.Time + Dt);
                c.Step(Dt, c.Time + Dt);
                lost.Step(Dt, lost.Time + Dt);
                every &= k.StateHash() == c.StateHash();
            }
            int kGot = k.EmgClueTake(si), cGot = c.EmgClueTake(si), lostGot = lost.EmgClueTake(si);
            int instGot;
            {
                using var ki = NewKernel();
                int ai = Unit(ki, CombatFaction.Player, new double2(0, 0), ki.AddWeapon(WeaponOf(ki, r, out _)), 100f);
                int ti = Unit(ki, CombatFaction.Hostile, new double2(0, 9), -1, 5000f);
                ki.FireAt(ai, ti, ki.Time);
                for (int i = 0; i < 60; i++)
                {
                    ki.Step(Dt, ki.Time + Dt);
                }
                instGot = ki.EmgClueTake(si);
            }
            Expect(si >= 0 && (r.Program.ClueMask & (1u << si)) != 0u && seg && load && every && kGot == 1 && cGot == kGot && sensed && lostGot == 0,
                $"D9 线索在途登记随快照段 32 往返：出手 3 步、弹在飞时存档（快照 {snap.Length} 字节 > 段 32 丢掉侧位的对照档 {cut.Length} 字节：{seg}）；读档续跑 90 步逐步哈希一致 {every}，" +
                $"同现原档 {kGot} / 读档 {cGot}（应都为 1）；段 32 丢掉侧位的档读档后哈希与原档不同 {sensed}、同现 {lostGot}（应 0：燃迹那一侧丢了，命中时只出现冷却液一侧）；即时命中（不存档）同现 {instGot}（读档成功 {load}，掩码 {r.Program.ClueMask:X}）");
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
