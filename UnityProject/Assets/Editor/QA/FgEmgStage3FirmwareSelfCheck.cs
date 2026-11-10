using System;
using System.Collections.Generic;
using System.IO;
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
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static GameLogic.EditorTools.FgReadingMatrixSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG3-FW-02（FG19A 第 6 节 N01～N08、N30～N32、N42“元程序、协同与齐射新固件落表”；FGT-EMG-002 / 003 / 006，FGT-EMG-051 / 052 用正式固件复跑，FGJ-EMG-3 的
    /// [若目标被标记, 复制, 分裂]）。起真实系统：真实 Luban 表、真实声明编译与准入（EmgCatalog）、真实程序编译（EmgProgramCompiler）与蓝图 → 内核（读法矩阵行为指纹）、
    /// 真实内核（中继 / 信号脉冲 / 节拍 / 元程序）、真实掉落 / 三选一 / 数据复原入口、真实存档往返。
    /// A 数据：12 条追加在 N09～N16 之后、名表 44 条与 EMG1 的 8 条不动；stageGate = 3；准入；文本键（名称 / 描述 / 获取途径 / 5 载体读法）与占位图标；三选一追加行排在 EMG1 行之后；预览端口行；
    /// B 零死对（FGT-EMG-003）：元程序 × 5 载体（蓝图 → 内核行为指纹，[元程序, 分裂] ≠ [分裂]）；末位 / 2 代节点没有下游段 → 预览原因；反转 5 载体都出逆序树、逆序树相位 = 逆序序号 % 周期（EMG3-TMP-01 遗留）；循环“越热周期越短”；
    /// C 获取（FGT-EMG-006）：emg.stage = 2 时 12 条都拿不到；= 3 时全部进三选一 / 数据复原、杂兵残骸来源的进掉落；
    /// D 协同（FGT-EMG-051 正式固件）：击杀回传 → 中继应答（残骸处脉冲、在脉冲点放出、朝最近敌人）；没留残骸不广播；没有中继只计广播；预览写原因；子节点收不到；标记广播脉冲点 = 最热的被标记目标；
    /// E 齐射（N42）：相位对齐拍上向最近的 2 个敌人各放 1 份、错相位不放；FGJ-EMG-3 [若目标被标记, 复制, 分裂] 已标记 6 份、未标记 0 份；
    /// F 负向 / 存读档 / 倍速：嵌套复制超上限原因；旧存档读入新固件不凭空进库存；齐射中途存读档逐步哈希一致；0.5x / 1x / 3x 结果一致。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgStage3FirmwareSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgStage3FirmwareSelfCheck。
    /// G 零死对（FGT-EMG-003，修复轮）：条件门·标记、随机分配、击杀回传、中继应答、标记广播、齐射同步 × 5 载体，正式蓝图编译 + 真实内核，先造条件（目标标记 / 低血目标 + 中继友机 /
    /// 发标记广播的友机 / 本机武器带标记 / 两条同相位时钟），各自的行为证据成立、首位 / 末位都与对照不同；击杀回传与同装回收修复的指令顺序无关。
    /// </summary>
    public static class FgEmgStage3FirmwareSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail, _pass;
        private const int Slot = 7;
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const byte Trig = EmgConst.RoleTrigger, RoleSpawn = 7;

        /// <summary>FG19A 第 6 节暂定 ID（定稿 = 暂定，FG19 第 2.8 节）与写端口。</summary>
        internal static readonly (string Id, string Write)[] Spec =
        {
            ("fw_ifmark", "meta.if"), ("fw_ifhot", "meta.if"), ("fw_ifair", "meta.if"), ("fw_repeat", "meta.repeat"), ("fw_loop", "meta.loop"), ("fw_random", "meta.random"),
            ("fw_reverse", "meta.reverse"), ("fw_defer", "meta.defer"), ("fw_killping", "evt.ping"), ("fw_pingfire", "grm.trig"), ("fw_markcast", "evt.ping"), ("fw_volley", "grm.trig"),
            ("fw_anchorwire", "grm.anchor"), // EMG3-FUSE-01：N43 接线进 FGT-EMG-002 / 003 / 006（行为验收在 FgEmgFuseSelfCheck）
        };

        [MenuItem("BinGames/QA/自检/元程序、协同与齐射新固件（EMG3-FW-02）")]
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
            _fail = _pass = 0;
            Line("\n[阶段 3 新固件] FG19A 第 6 节 N01～N08、N30～N32、N42 落表（EMG3-FW-02；FGT-EMG-002 / 003 / 006 / 051 / 052，FGJ-EMG-3）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）");
            string dir = Path.Combine(Path.GetTempPath(), "bingames-emg3fw02");
            GameLanguage lang = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                CarrierReadings.Reload();
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                Step(CheckData);
                Step(CheckZeroDead);
                Step(CheckSources);
                Step(CheckSquad);
                Step(CheckVolleyAndJourney);
                Step(CheckNegatives);
                Step(CheckConditionalCarriers);
            }
            finally
            {
                EmgCatalog.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CampaignSession.Clear();
                GameSettings.SetLanguage(lang);
            }
            Line($"  · [阶段 3 新固件] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        private static EmgProgramResult P(params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, 0);

        private static EmgProgramResult Pc(int cap, params string[] local) => EmgProgramCompiler.Compile(Array.Empty<string>(), local, FirmwareCarrier.Projectile, cap);

        private static string TreeText(EmgProgramResult r)
        {
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows);
            return string.Join(" | ", rows.Select(x => x.Text));
        }

        private static EmgProgramResult Board(string structure, params string[] fws) => BoardL(structure, Array.Empty<string>(), fws);

        /// <summary>正常装配路径：本机固件位 <paramref name="local"/>（2 个槽）+ 接入口 <paramref name="fws"/>（配额 2）。</summary>
        private static EmgProgramResult BoardL(string structure, string[] local, params string[] fws)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, structure, local);
            board.TrySetUplink(2);
            return EmgProgramCompiler.Compile(UplinkCompiler.CompileUplinked(board, fws));
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 阶段 3 新固件追加在 N09～N16 之后（第 53 行起，条数见下行）、名表 44 条与 EMG1 的 8 条不动；stageGate 3、准入、写端口 5 载体、文本键与占位图标齐；三选一追加行排在 EMG1 行之后");
            var rows = FirmwareKinds.Rows.Where(r => r != null && !FirmwareKinds.IsMixedRow(r)).Select(r => r.Id).ToList();
            var addon = FirmwareKinds.AddonRows.Select(r => r.Id).ToList();
            // EMG4-FW-01：阶段 4 的 3 条（N39～N41）接在阶段 3 之后（FgEmgStage4FirmwareSelfCheck A 段核），这里只核阶段 3 这一段不动
            Expect(addon.Count >= 8 + Spec.Length && addon.Skip(8).Take(Spec.Length).SequenceEqual(Spec.Select(s => s.Id)) && rows.Skip(52).Take(Spec.Length).SequenceEqual(addon.Skip(8).Take(Spec.Length)) && FirmwareKinds.BaseRows.Count == 44
                   && FirmwareCatalog.BaseCount == FirmwareCatalog.ExpectedCount && addon.All(FirmwareCatalog.IsAddon) && Spec.All(s => FirmwareCatalog.All.ContainsKey(s.Id)),
                $"新固件 {Spec.Length} 条按 FG19A 顺序追加在表尾第 53～{52 + Spec.Length} 行（{string.Join("、", addon.Skip(8))}）；名表 44 条、EMG1 的 8 条（{string.Join("、", addon.Take(8))}）不动；都进固件目录");
            EmgSource src = EmgCatalog.FromTables();
            var ports = src.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var bad = new List<string>();
            foreach ((string id, string write) in Spec)
            {
                var decls = src.Decls.Where(d => d.Owner == id).ToList();
                if (decls.Count == 0 || decls.Any(d => d.StageGate != 3) || !decls.Any(d => d.Dir == "write" && d.Port == write))
                {
                    bad.Add($"{id} 声明 stageGate {string.Join("/", decls.Select(d => d.StageGate).Distinct())} / 写端 {string.Join("/", decls.Where(d => d.Dir == "write").Select(d => d.Port))}");
                }
                if (!EmgCatalog.Admit(EmgCatalog.DeclsOf(src, id), ports, out string why))
                {
                    bad.Add($"{id} 准入被拒：{why}");
                }
                if (!EmgCatalog.IsOwnerOnDeclTrack(id))
                {
                    bad.Add($"{id} 阶段 3 未走声明轨");
                }
                foreach (EmgSource.Decl w in decls.Where(d => d.Dir == "write" && ports.TryGetValue(d.Port, out EmgSource.Port pp) && pp.CarrierImpl == 1))
                {
                    int carriers = src.PortCarriers.Where(pc => pc.Port == w.Port).Select(pc => pc.Carrier).Distinct().Count();
                    if (carriers != 5)
                    {
                        bad.Add($"{id} 写端 {w.Port} 只有 {carriers} 种载体行");
                    }
                }
                FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
                string[] keys = { row.NameKey, row.DescKey, row.AcquireKey, row.ReadProjectileKey, row.ReadMeleeKey, row.ReadSummonKey, row.ReadAuraKey, row.ReadFieldKey };
                foreach (string k in keys)
                {
                    string zh = GameText.Get(k, GameLanguage.ZhCn), en = GameText.Get(k, GameLanguage.En);
                    if (string.IsNullOrEmpty(zh) || zh == k || string.IsNullOrEmpty(en) || en == k || zh.Contains("meta.") || zh.Contains("evt."))
                    {
                        bad.Add($"{id} 文本键 {k} 缺中 / 英或露出内部端口名");
                    }
                }
                string icon = Path.Combine(Application.dataPath, "GameRes", "Raw", "UI", "Icons", row.Icon + ".png");
                if (row.Icon != "icon_" + id || !File.Exists(icon) || !File.Exists(icon + ".meta"))
                {
                    bad.Add($"{id} 占位图标 {row.Icon} 缺失");
                }
                if (row.Protocol != "enemy" || (row.Source != "salvage" && row.Source != "elite") || row.ReadFields != "none" || row.LegacyId != "none" || row.Kind != "regular")
                {
                    bad.Add($"{id} 协议 / 来源 / 读法字段 / 种类不是“敌方加密 · 杂兵残骸或精英 · 只走声明 · 常规”");
                }
            }
            Expect(bad.Count == 0,
                $"12 条 stageGate = 3（FG19A“阶段”列）、全部准入且在声明轨、写端口（meta.* / evt.ping / grm.trig）按 carrierImpl 有 5 载体行、名称 / 描述 / 获取途径 / 5 载体读法中英齐（不露内部端口名）、占位图标与 .meta（B22 占位）{Detail(bad)}");
            Line("    · 例（射弹）：" + string.Join("；", new[] { "fw_ifmark", "fw_repeat", "fw_killping", "fw_volley" }.Select(id => FirmwareKinds.Reading(id, FirmwareCarrier.Projectile))));
            // 详情页 / 三选一卡面的读写端口行由声明生成（EmgProgramTreeText.PortsLine，与 44 条同一函数）
            string detail = CarrierReadings.DetailLines("fw_killping") ?? string.Empty;
            Expect(detail.Contains(GameText.Get("emg.port.evt.kill.name")) && detail.Contains(GameText.Get("emg.port.ent.wreck.name")) && detail.Contains(GameText.Get("emg.port.evt.ping.name")),
                $"固件详情页列出读 / 写端口（击杀 · 残骸 → 信号脉冲）：{detail.Replace('\n', '｜')}");
            // 三选一追加行：EMG1 的 8 条在前（sortOrder 不变），阶段 3 的 12 条接在后面
            var picks = LootPickCatalog.Rows.Where(r => FirmwareCatalog.IsAddon(r.Target)).ToList();
            var e1 = picks.Where(r => addon.Take(8).Contains(r.Target)).ToList();
            var e3 = picks.Where(r => Spec.Any(s => s.Id == r.Target)).ToList();
            Expect(e1.Count > 0 && e3.Count == Spec.Length && e3.All(r => r.SortOrder >= 1000) && e1.Max(r => r.SortOrder) < e3.Min(r => r.SortOrder) && e1.Min(r => r.SortOrder) == 1000,
                $"fg.TbLootPick：EMG1 追加行 {e1.Count} 行 sortOrder {e1.Min(r => r.SortOrder)}～{e1.Max(r => r.SortOrder)}（不变），阶段 3 的 {Spec.Length} 行 {(e3.Count > 0 ? e3.Min(r => r.SortOrder) + "～" + e3.Max(r => r.SortOrder) : "无")} 接在后面（权重 1，临时值）");
        }

        // ── B. 零死对（FGT-EMG-003）──

        private static void CheckZeroDead()
        {
            Line("  · B. 零死对：元程序 × 5 载体真实蓝图 → 内核行为指纹（[元程序, 分裂] ≠ [分裂]）；末位 / 2 代节点没有下游段 → 预览原因；反转 5 载体出逆序树、逆序树相位；循环越热越快");
            var dead = new List<string>();
            var noReverse = new List<string>();
            int combos = 0;
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                string baseline = DeclDigest(comp, new[] { "fw_split" }, out _, out _);
                foreach (string fw in new[] { "fw_ifhot", "fw_ifair", "fw_repeat", "fw_loop", "fw_defer" })
                {
                    combos++;
                    if (DeclDigest(comp, new[] { fw, "fw_split" }, out EmgProgramResult pr, out _) == baseline)
                    {
                        dead.Add($"{fw} × {carrier}（树 {pr.Nodes.Count} 节点）");
                    }
                }
                DeclDigest(comp, new[] { "fw_reverse", "fw_split" }, out EmgProgramResult rv, out _);
                if (rv.Reverse == null)
                {
                    noReverse.Add(carrier.ToString());
                }
            }
            Expect(dead.Count == 0 && noReverse.Count == 0,
                $"{combos} 组（5 条元程序 × 5 载体，接入口装 [元程序, 分裂]）行为指纹都与只装 [分裂] 不同（过热 / 离地条件在默认场景为假 → 分裂不放；复制 / 循环 / 延后改变次数或时刻）；反转 5 载体都编出逆序树" +
                $"（条件门·标记在矩阵场景里目标已标记、条件为真时与 [分裂] 相同是正确语义；它与反转、随机的开 / 关差异由 FgEmgMetaSelfCheck B / L / H / E 段用正式固件按场景验）{Detail(dead.Concat(noReverse.Select(c => "反转无逆序树 × " + c)).ToList())}");
            // 末位：没有下游段，预览写原因（不静默无效）
            var noReason = Spec.Take(8).Select(s => s.Id).Where(fw => !P("fw_split", fw).Notes.Any(n => n.Key == EmgProgramCompiler.NoteMetaEmpty) && !P("fw_pierce", fw).Notes.Any(n => n.Key == EmgProgramCompiler.NoteMetaEmpty)).ToList();
            EmgProgramResult g2 = P("fw_split", "fw_split", "fw_repeat");
            string g2Text = TreeText(g2);
            Expect(noReason.Count == 0 && g2.Notes.Any(n => n.Key == EmgProgramCompiler.NoteMetaEmpty) && g2Text.Contains(GameText.Get(EmgProgramCompiler.NoteMetaEmpty).Split('：')[0]),
                $"负向：8 条元程序放在程序末位 / 2 代节点（[分裂, 分裂, 双发指令]）没有下游段 → 预览原因 emg.compile.meta_empty（“{GameText.Get(EmgProgramCompiler.NoteMetaEmpty)}”）{Detail(noReason)}");
            // 逆序树相位（EMG3-TMP-01 遗留：BuildReverse 末尾补的 TempoPass）：逆序树里时钟的相位 = 逆序序号 % 周期
            EmgProgramResult rt = Pc(4, "fw_reverse", "fw_clock", "fw_clock", "fw_pierce");
            var fwdClock = rt.KernelInstrs.Where(x => x.Port == EmgTempoConst.PortBeat).Select(x => ((int)x.Ordinal, (int)Math.Round(x.Param2), (int)Math.Round(x.Amount))).ToList();
            var revClock = rt.Reverse?.KernelInstrs.Where(x => x.Port == EmgTempoConst.PortBeat).Select(x => ((int)x.Ordinal, (int)Math.Round(x.Param2), (int)Math.Round(x.Amount))).ToList() ?? new List<(int, int, int)>();
            bool phaseOk = revClock.Count == 2 && revClock.All(c => c.Item2 == c.Item1 % c.Item3) && fwdClock.All(c => c.Item2 == c.Item1 % c.Item3)
                           && !revClock.Select(c => c.Item1).OrderBy(x => x).SequenceEqual(fwdClock.Select(c => c.Item1).OrderBy(x => x));
            Expect(phaseOk, $"逆序树相位 = 逆序序号 % 周期（[逆序编译, 时钟, 时钟, 贯穿]，树 {Tree(rt)} / 逆序 {(rt.Reverse != null ? Tree(rt.Reverse) : "无")}）：正序（序号, 相位, 周期）[{string.Join(" ", fwdClock)}]、逆序 [{string.Join(" ", revClock)}]（序号变了、相位跟着重算）");
            // 循环（N05）：本机越热周期越短（0.5 × (1 − 0.5 × 积热比例)）
            long cold = LoopRun(false), hot = LoopRun(true);
            Expect(cold >= 3 && hot > cold, $"[循环写入, 分裂] 飞行 2 秒：冷武器执行 {cold} 次、积热过半的武器 {hot} 次（越热周期越短）");
        }

        private static long LoopRun(bool heat)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k, P("fw_loop", "fw_split"));
            CombatWeapon w = Flight(Instant(prog), 2f, 2.2f);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 40), -1, 5000f);
            if (heat)
            {
                k.SetWeaponState(me, 14f, false, 0, 0); // 积热 14 / 15
            }
            k.FireAt(me, foe, k.Time);
            StepN(k, 150);
            return k.EmgBusStats.SubSpawned / 3; // 分裂一次 3 份
        }

        // ── C. 获取（FGT-EMG-006）──

        private static void CheckSources()
        {
            Line("  · C. FGT-EMG-006：只在 emg.stage ≥ 3 时开放（杂兵残骸掉落 / 精英三选一 / 数据复原 / 可装机）；名表固件与 EMG1 新固件不受影响");
            CampaignState s = CampaignState.CreateNew("emg3fw02-c", "Standard", 1);
            var pool = new List<LootPickDef>();
            try
            {
                foreach (int stage in new[] { 2, 3 })
                {
                    UseStage(stage);
                    var leaked = new List<string>();
                    int open = 0;
                    foreach ((string id, _) in Spec)
                    {
                        string fac = FirmwareKinds.FactionOf(id);
                        LootPickCatalog.PoolFor(fac, pool);
                        FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
                        bool inDrop = LootCatalog.EnemyFirmwareOf(fac).Contains(id);
                        bool inPick = pool.Any(p => p.Target == id);
                        bool inRestore = FirmwareRestoreService.Candidates(s).Contains(id);
                        bool obtainable = FirmwareKinds.IsObtainable(id) && FirmwareKinds.HasMachineImplementation(id);
                        bool want = stage >= 3;
                        bool wantDrop = want && row.Source == "salvage";
                        if (inPick && inRestore && obtainable && inDrop == wantDrop)
                        {
                            open++;
                        }
                        if (inDrop != wantDrop || inPick != want || inRestore != want || obtainable != want)
                        {
                            leaked.Add($"{id}（掉落 {inDrop} 三选一 {inPick} 复原 {inRestore} 可装 {obtainable}，应 {want}）");
                        }
                    }
                    int e1Open = FirmwareKinds.AddonRows.Take(8).Count(r => FirmwareKinds.IsObtainable(r.Id));
                    Expect(leaked.Count == 0 && open == (stage >= 3 ? Spec.Length : 0) && e1Open == 8,
                        $"emg.stage = {stage}：阶段 3 新固件开放 {open} / {Spec.Length} 条（杂兵残骸来源的进掉落，精英来源的只进三选一）；EMG1 的 8 条照常开放 {e1Open}{Detail(leaked)}");
                }
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Expect(EmgCatalog.EffectiveStage >= 3 && Spec.All(x => FirmwareKinds.IsObtainable(x.Id)),
                $"正式表 emg.stage = {EmgCatalog.EffectiveStage}：12 条在正常游戏里拿得到（可装出 FGJ-EMG-3 的 [条件门·标记, 双发指令, 分裂]）");
        }

        // ── D. 协同（FGT-EMG-051 正式固件）──

        private static void CheckSquad()
        {
            Line("  · D. 跨机协同（正式固件 N30～N32）：击杀回传 → 中继应答；没留残骸不广播；没有中继只计广播（预览写原因）；子节点收不到；标记广播脉冲点 = 最热的被标记目标");
            // D1 击杀留残骸 → 0 号频道 → 中继组里装中继应答的 B 在脉冲点放 1 份、朝那里最近的敌人
            using (CombatKernel k = KillPing(true, false, out int a, out int b, out int weak, out int near, out float nearLoss))
            {
                bool rec = FindPing(k, 0, out EmgChainRec r);
                Expect(k.WreckCount >= 1 && k.EmgBusStats.PingsSent == 1 && k.EmgBusStats.PingsDelivered == 1 && k.EmgBusStats.SubSpawned == 1 && rec && r.SrcUnit == a && r.Other == b
                       && math.distance(r.Pos, new double2(0, 3)) < 0.05 && nearLoss > 0f,
                    $"击杀回传：残骸 {k.WreckCount} 块、广播 {k.EmgBusStats.PingsSent} 次、送达 {k.EmgBusStats.PingsDelivered} 次；B（中继应答）放 {k.EmgBusStats.SubSpawned} 份；链“信号脉冲：A 广播 0 号频道 → B” 脉冲点 ({r.Pos.x:0.0}, {r.Pos.y:0.0}) = 残骸处；" +
                    $"脉冲点旁最近的敌人掉血 {nearLoss:0.0}");
            }
            // D2 没留残骸（结构被击毁不留残骸）→ 不广播
            using (CombatKernel k = KillPing(true, true, out _, out _, out _, out _, out _))
            {
                Expect(k.WreckCount == 0 && k.EmgBusStats.PingsSent == 0 && k.EmgBusStats.PingsDelivered == 0 && k.EmgBusStats.SubSpawned == 0,
                    $"负向：击毁结构（不留残骸）→ 残骸 {k.WreckCount}、广播 {k.EmgBusStats.PingsSent}、送达 {k.EmgBusStats.PingsDelivered}、子载荷 {k.EmgBusStats.SubSpawned}（FG19A N30“没留残骸不广播”）");
            }
            // D3 没有中继：只计广播、无人接收、不报错；预览写原因
            using (CombatKernel k = KillPing(false, false, out _, out _, out _, out _, out _))
            {
                string noRelay = EmgProgramTreeText.PingNoRelayText();
                string plain = TreeText(Board(null, "fw_killping")), relayed = TreeText(Board(ComponentCatalog.StructRelayId, "fw_killping"));
                string send = GameText.Format("emg.tree.tag.ping_send", 0);
                Expect(k.EmgBusStats.PingsSent == 1 && k.EmgBusStats.PingsDelivered == 0 && k.EmgBusStats.SubSpawned == 0 && plain.Contains(send) && plain.Contains(noRelay) && !relayed.Contains(noRelay) && relayed.Contains(send),
                    $"负向：没有中继——广播 {k.EmgBusStats.PingsSent} 次、送达 {k.EmgBusStats.PingsDelivered} 次（不报错）；预览（未装中继）“{plain}”；装中继后不再写这条原因");
            }
            // D4 收听装在子节点：脉冲只触发接收方第一层，预览写原因
            EmgProgramResult child = P("fw_split", "fw_pingfire");
            string childText = TreeText(child);
            bool inChild = child.Nodes.Any(n => n.Index > 0 && n.Entries.Any(e => !e.Inherited && child.Steps[e.Ordinal].OwnerId == "fw_pingfire"));
            Expect(inChild && childText.Contains(GameText.Get("emg.tree.tag.ping_root_only")),
                $"负向：[分裂, 中继应答] 收听装进子弹药节点 → 预览写“{GameText.Get("emg.tree.tag.ping_root_only")}”：{childText}");
            // D5 标记广播：脉冲点 = 被标记敌人里积热最高的一个（对照：只有刚标记的目标时就在它身上）
            double2 hotAt = MarkCast(true, false, out long sentHot, out long delHot, out long subHot), coldAt = MarkCast(false, out long sentCold);
            double2 tieAt = MarkCast(false, out long sentTie, tieOther: true);
            Expect(sentHot == 1 && sentCold == 1 && sentTie == 1 && delHot == 1 && subHot == 1 && math.distance(hotAt, new double2(4, 4)) < 0.05 && math.distance(coldAt, new double2(0, 3)) < 0.05
                   && math.distance(tieAt, new double2(0, 3)) < 0.05,
                $"标记广播（0 号频道）→ 只用正式固件装出的接收方 [中继应答]：送达 {delHot} 次、放出 {subHot} 份（“一台标记、几台开火”）；另有积热 30 的已标记敌人时脉冲点 ({hotAt.x:0.0}, {hotAt.y:0.0}) = 它；" +
                $"只有刚被标记的目标时 ({coldAt.x:0.0}, {coldAt.y:0.0}) = 目标；另一个已标记敌人积热相同（0）时优先本次标记的目标 ({tieAt.x:0.0}, {tieAt.y:0.0})");
        }

        /// <summary>A（[击杀回传]）在 (0,0) 打 5 血的敌人（或结构）(0,3)；B（[中继应答]）在 (5,0)；<paramref name="relay"/> = B 装中继；另一个敌人在 (1.5,4.5)（脉冲点最近的敌人）。</summary>
        private static CombatKernel KillPing(bool relay, bool structure, out int a, out int b, out int weak, out int near, out float nearLoss)
        {
            CombatConfig cfg = Cfg();
            var k = new CombatKernel(cfg, 32);
            int pa = CombatSite.RegisterProgram(k, P("fw_killping"));
            int pb = CombatSite.RegisterProgram(k, P("fw_pingfire"));
            a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pa)), 100f);
            b = Unit(k, CombatFaction.Player, new double2(5, 0), k.AddWeapon(Instant(pb)), 100f);
            weak = Unit(k, CombatFaction.Hostile, new double2(0, 3), -1, 5f, structure ? CombatUnitKind.Structure : CombatUnitKind.Enemy);
            near = Unit(k, CombatFaction.Hostile, new double2(1.5, 4.5), -1, 5000f);
            if (relay)
            {
                k.SetUnitRelay(b, true);
            }
            k.FireAt(a, weak, k.Time);
            StepN(k, 90);
            nearLoss = k.TryGetUnit(near, out CombatUnitView v) ? 5000f - v.Health : -1f;
            return k;
        }

        /// <summary>
        /// A（[标记广播]，出手标记目标）打 (0,3) 的目标；<paramref name="hotOther"/> = (4,4) 另有一个已标记、积热 30 的敌人；<paramref name="tieOther"/> = 那个敌人已标记、积热与目标相同（0）。
        /// 接收方 R 装正式固件 [中继应答]（收听 0 号频道）、装中继。返回脉冲点；<paramref name="delivered"/> / <paramref name="subs"/> = 送达次数 / 放出的份数。
        /// </summary>
        private static double2 MarkCast(bool hotOther, out long sent, bool tieOther = false) => MarkCast(hotOther, tieOther, out sent, out _, out _);

        private static double2 MarkCast(bool hotOther, bool tieOther, out long sent, out long delivered, out long subs)
        {
            using var k = new CombatKernel(Cfg(), 32);
            int pa = CombatSite.RegisterProgram(k, P("fw_markcast"));
            int pr = CombatSite.RegisterProgram(k, P("fw_pingfire"));
            CombatWeapon w = Instant(pa);
            w.MarkSeconds = 6f;
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w), 100f);
            int r = Unit(k, CombatFaction.Player, new double2(5, 0), k.AddWeapon(Instant(pr)), 100f);
            k.SetUnitRelay(r, true);
            int t = Unit(k, CombatFaction.Hostile, new double2(0, 3), -1, 5000f);
            int h = Unit(k, CombatFaction.Hostile, new double2(4, 4), -1, 5000f);
            if (hotOther || tieOther)
            {
                k.SetMarkedUntil(h, 1e9);
                if (hotOther)
                {
                    k.SetWeaponState(h, 30f, false, 0, 0);
                }
            }
            k.FireAt(a, t, k.Time);
            StepN(k, 3);
            sent = k.EmgBusStats.PingsSent;
            delivered = k.EmgBusStats.PingsDelivered;
            subs = k.EmgBusStats.SubSpawned;
            return FindPing(k, 0, out EmgChainRec rec) ? rec.Pos : new double2(-99, -99);
        }

        private static bool FindPing(CombatKernel k, int channel, out EmgChainRec rec)
        {
            for (int i = 0; i < k.EmgChainRecordCount; i++)
            {
                if (k.TryGetEmgChainRecent(i, out rec) && rec.Kind == EmgChainNode.Relay && rec.Aux == EmgSquadConst.AuxPing + channel)
                {
                    return true;
                }
            }
            rec = default;
            return false;
        }

        // ── E. 齐射与 FGJ-EMG-3 ──

        private static void CheckVolleyAndJourney()
        {
            Line("  · E. 齐射同步（N42）：相位对齐拍上向最近的 2 个敌人各放 1 份；错相位不放；FGJ-EMG-3 的 [若目标被标记, 复制, 分裂]");
            EmgProgramResult aligned = PickVolley(true), off = PickVolley(false);
            VolleyObs on = VolleyRun(aligned, Dt), no = VolleyRun(off, Dt);
            Expect(aligned != null && off != null && on.Syncs > 0 && on.AtSecond > 0 && on.AtFirst > 0 && no.Syncs == 0 && no.AtSecond == 0,
                $"真实编译 {(aligned != null ? Tree(aligned) : "无")}：对齐 {on.Syncs} 拍，朝第 2 近的敌人放出 {on.AtSecond} 份、朝最近的 {on.AtFirst} 份；" +
                $"不对齐（整台机器只有 1 条节拍指令）{(off != null ? Tree(off) : "无")}：对齐 {no.Syncs} 拍、朝第 2 近的敌人 {no.AtSecond} 份；{on.Probe}");
            // FGJ-EMG-3：[条件门·标记, 双发指令, 分裂] 命中已标记目标放两组子弹药（6 份），未标记 0 份
            // 正常装配路径：接入口 [条件门·标记, 双发指令] + 本机固件位 [分裂]（机器只有 2 个固件槽，第 3 条走接入口）
            EmgProgramResult j = BoardL(null, new[] { "fw_split" }, "fw_ifmark", "fw_repeat");
            long marked = JourneyRun(j, true), plain = JourneyRun(j, false);
            Expect(j.Notes.Count == 0 && j.Metas.Count == 2 && marked == 6 && plain == 0,
                $"FGJ-EMG-3 [条件门·标记, 双发指令, 分裂]（正式固件，蓝图 + 接入口装配，树 {Tree(j)}）：命中已标记目标放 {marked} 份（两组）、未标记 {plain} 份；预览：{TreeText(j)}");
        }

        /// <summary>按真实编译结果挑排列：两条时钟脉冲都在根里（对齐 = 同相位 / 错相位 = 不同相位），齐射同步在根里读 tmp.sync。</summary>
        private static EmgProgramResult PickVolley(bool aligned)
        {
            string[][] tries = aligned
                ? new[] { new[] { "fw_clock", "fw_pierce", "fw_clock", "fw_volley" }, new[] { "fw_volley", "fw_clock", "fw_pierce", "fw_clock" }, new[] { "fw_clock", "fw_pierce", "fw_clock", "fw_tractor", "fw_volley" } }
                : new[] { new[] { "fw_clock", "fw_pierce", "fw_volley" } }; // 不对齐对照：同一台机器只有 1 条节拍指令（FGR-EMG-172 需要 ≥ 2 条）
            foreach (string[] t in tries)
            {
                foreach (int cap in new[] { 3, 4, 5 })
                {
                    EmgProgramResult r = Pc(cap, t);
                    int vo = Array.IndexOf(t, "fw_volley");
                    var phases = new List<int>();
                    for (int o = 0; o < t.Length; o++)
                    {
                        if (EmgProgramCompiler.BeatOf(r, 0, o, out int period, out int phase))
                        {
                            phases.Add(phase + 100 * period);
                        }
                    }
                    bool shape = aligned ? phases.Count == 2 && phases[0] == phases[1] : phases.Count == 1 && r.KernelInstrs.Count(x => x.Port == EmgTempoConst.PortBeat) == 1;
                    if (shape && EmgProgramCompiler.SyncOf(r, 0, vo))
                    {
                        return r;
                    }
                }
            }
            return null;
        }

        private struct VolleyObs
        {
            public long Syncs, AtFirst, AtSecond, Subs;
            public string Probe;
            public ulong Hash;
        }

        /// <summary>慢速长寿命弹（0.2 米 / 秒）载着程序飞向远处敌人；两侧各一个敌人（最近 (2,1)、第 2 近 (−2.6,1.2)）。数出朝它们放出的子弹药份数。</summary>
        private static VolleyObs VolleyRun(EmgProgramResult r, float dt, int saveAtStep = -1)
        {
            var o = new VolleyObs();
            if (r == null)
            {
                return o;
            }
            var k = new CombatKernel(Cfg(), 32);
            try
            {
                int prog = CombatSite.RegisterProgram(k, r);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(Instant(prog), 0.2f, 10f)), 100f);
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 25), -1, 50000f);
                int first = Unit(k, CombatFaction.Hostile, new double2(2, 1), -1, 50000f);
                int second = Unit(k, CombatFaction.Hostile, new double2(-2.6, 1.2), -1, 50000f);
                k.FireAt(me, foe, k.Time);
                var seen = new HashSet<int>();
                int steps = (int)math.round(4.2f / dt);
                for (int s = 0; s < steps; s++)
                {
                    if (s == saveAtStep)
                    {
                        byte[] snap = k.Serialize();
                        var c = new CombatKernel(Cfg(), 32);
                        if (c.Load(snap) != CombatLoadResult.Ok)
                        {
                            c.Dispose();
                            o.Hash = 1;
                            return o;
                        }
                        k.Dispose();
                        k = c;
                    }
                    k.Step(dt, k.Time + dt);
                    for (int i = 0; i < k.ProjectileCount; i++)
                    {
                        if (!k.TryGetProjectile(i, out CombatProjectile p) || (p.Flags & EmgProjFlags.Sub) == 0 || !seen.Add(p.Id))
                        {
                            continue;
                        }
                        float2 v = math.normalizesafe(p.Vel);
                        if (o.Probe == null && k.TryGetUnit(first, out CombatUnitView f0) && k.TryGetUnit(second, out CombatUnitView s0))
                        {
                            o.Probe = $"首枚子弹药 @({p.Pos.x:0.00},{p.Pos.y:0.00}) 速度 ({p.Vel.x:0.0},{p.Vel.y:0.0}) 对最近 {math.dot(v, math.normalizesafe((float2)(f0.Position - p.Pos))):0.000} 对第 2 近 {math.dot(v, math.normalizesafe((float2)(s0.Position - p.Pos))):0.000}";
                        }
                        if (k.TryGetUnit(first, out CombatUnitView f) && math.dot(v, math.normalizesafe((float2)(f.Position - p.Pos))) > 0.999f)
                        {
                            o.AtFirst++;
                        }
                        if (k.TryGetUnit(second, out CombatUnitView q) && math.dot(v, math.normalizesafe((float2)(q.Position - p.Pos))) > 0.999f)
                        {
                            o.AtSecond++;
                        }
                    }
                }
                o.Syncs = k.EmgBusStats.BeatSyncs;
                o.Subs = k.EmgBusStats.SubSpawned;
                o.Hash = k.StateHash();
            }
            finally
            {
                k.Dispose();
            }
            return o;
        }

        private static long JourneyRun(EmgProgramResult r, bool marked)
        {
            using var k = new CombatKernel(Cfg(), 16);
            int prog = CombatSite.RegisterProgram(k, r);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(prog)), 100f);
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 4), -1, 5000f);
            if (marked)
            {
                k.SetMarkedUntil(foe, 1e9);
            }
            k.FireAt(me, foe, k.Time);
            StepN(k, 30);
            return k.EmgBusStats.SubSpawned;
        }

        // ── F. 负向 / 存读档 / 倍速 ──

        private static void CheckNegatives()
        {
            Line("  · F. 负向：嵌套复制超上限的原因；旧存档读入新固件不凭空进库存；齐射中途存读档、0.5x / 1x / 3x 结果一致");
            EmgProgramResult n3 = P("fw_repeat", "fw_repeat", "fw_repeat", "fw_split");
            string n3Text = TreeText(n3);
            Expect(n3.Metas.Select(m => m.Exec).SequenceEqual(new[] { 2, 4, 4 }) && n3.Notes.Any(n => n.Key == EmgProgramCompiler.NoteMetaExecCap) && n3Text.Contains("4 次"),
                $"负向：三层双发指令相乘超过 4 次 → 第 3 层不执行，预览原因 emg.compile.meta_exec_cap：{n3Text}");
            // 存档往返：新建战役（已解锁内容 + 芯片）存档 → 读档，12 条新固件不在库存 / 已解锁里（读档升级路径不按固件表批量发放）
            try
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                CampaignState s = CampaignState.CreateNew("emg3fw02-f", "Standard", 2);
                CampaignSession.Set(Slot, s);
                PrimitiveInventory.EnsureSeeded(s);
                SignalCoreService.EnsureInitialized(s);
                CampaignFgStateDomains.EnsureAll(s);
                int chips = s.PrimitiveChips.Length;
                SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
                LoadResult loaded = CampaignSaveService.Load(Slot);
                string[] ids = Spec.Select(x => x.Id).ToArray();
                bool clean = loaded.Success && loaded.State.PrimitiveChips.Length == chips
                             && !loaded.State.PrimitiveChips.Any(c => ids.Contains(c.CardDefId)) && !ids.Any(id => MechanicalContentUnlock.IsUnlocked(loaded.State, id));
                Expect(saved.Success && clean, $"存档 → 读档（emg.stage = 3）：芯片 {chips} 枚原样、12 条新固件没有凭空进库存或解锁");
            }
            finally
            {
                CampaignSession.Clear();
            }
            EmgProgramResult aligned = PickVolley(true);
            VolleyObs whole = VolleyRun(aligned, Dt), resumed = VolleyRun(aligned, Dt, 100);
            Expect(whole.Hash != 0 && whole.Hash == resumed.Hash && whole.AtSecond == resumed.AtSecond && whole.AtFirst == resumed.AtFirst && whole.AtSecond > 0,
                $"齐射：第 100 步存档 → 新内核读档续跑，终局状态哈希 {resumed.Hash:X16} / 连续运行 {whole.Hash:X16}，朝第 2 近 / 最近敌人的子弹药 {resumed.AtSecond}/{whole.AtSecond}、{resumed.AtFirst}/{whole.AtFirst}（总线统计不进快照，读档后从 0 计）");
            var speed = new[] { 1f / 120f, 1f / 60f, 1f / 20f }.Select(dt => VolleyRun(aligned, dt)).Select(x => $"{x.Syncs}/{x.AtSecond}").ToList();
            Expect(speed.Distinct().Count() == 1 && speed[0] != "0/0",
                $"同样 4.2 游戏秒，步长 1/120、1/60、1/20（= 0.5x / 1x / 3x）对齐拍 / 朝第 2 近敌人的份数：{string.Join(" · ", speed)}（暂停 = 不步进）");
        }

        // ── G. 条件型新固件 × 5 载体 × 首位 / 末位（FGT-EMG-003，修复轮）──

        private static void CheckConditionalCarriers()
        {
            Line("  · G. 零死对（条件型）：条件门·标记、随机分配、击杀回传、中继应答、标记广播、齐射同步 × 5 载体，正式蓝图 + 真实内核；先造条件，首位 / 末位都与对照不同");
            const float secs = 4f;
            string f = AddonFiller;
            var dead = new List<string>();
            var lines = new List<string>();
            Func<CombatWeapon, CombatWeapon> marker = w =>
            {
                w.MarkSeconds = 6f; // 条件提供者：本机武器带标记（标记桅杆等部件提供），对照同样带
                return w;
            };
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                // N01 条件门·标记（末位没有下游段 → B 段预览原因）：目标没被标记时后面的分裂不放，被标记时放
                Stage3Obs offCtl = RunStage3(comp, new[] { "fw_split" }, null, false, secs), offGate = RunStage3(comp, new[] { "fw_ifmark", "fw_split" }, null, false, secs);
                Stage3Obs onGate = RunStage3(comp, new[] { "fw_ifmark", "fw_split" }, null, true, secs);
                Judge(dead, carrier, "fw_ifmark（首位）", offCtl.Subs > 0 && offGate.Subs < offCtl.Subs && onGate.Subs > offGate.Subs && offGate.Digest != offCtl.Digest);
                lines.Add($"{carrier}/条件门·标记：未标记 [分裂] {offCtl.Subs} 份 · [门, 分裂] {offGate.Subs} 份；已标记 [门, 分裂] {onGate.Subs} 份");
                // N06 随机分配（末位 → B 段预览原因）：[随机, 填充, 分裂] 每次只执行一条
                Stage3Obs rCtl = RunStage3(comp, new[] { f }, new[] { "fw_split" }, true, secs), rnd = RunStage3(comp, new[] { "fw_random", f }, new[] { "fw_split" }, true, secs);
                Judge(dead, carrier, "fw_random（首位）", rnd.Digest != rCtl.Digest && rnd.Subs < rCtl.Subs);
                lines.Add($"{carrier}/随机分配：[填充, 分裂] {rCtl.Subs} 份 · [随机, 填充, 分裂] {rnd.Subs} 份");
                // N30 击杀回传：场上敌人都只剩 1 血（击杀留残骸；无人机不一定打主目标）+ 中继友机装 [中继应答]
                Action<CombatKernel, int, int[]> killSetup = (k, me, ids) =>
                {
                    for (int i = 1; i < ids.Length; i++)
                    {
                        k.SetHealth(ids[i], 1f, 5000f, true);
                    }
                    Receiver(k, new double2(-1.5, -1.5));
                };
                Squad(dead, lines, carrier, "击杀回传", comp, "fw_killping", null, null, killSetup,
                    (o, c) => o.Kills > 0 && o.PingsSent > 0 && o.PingsDelivered > 0 && o.Subs > c.Subs && c.PingsSent == 0);
                // N31 中继应答：本机装中继；友机装 [标记广播]，每秒标记一次主目标
                Action<CombatKernel, int, int[]> pingSetup = (k, me, ids) =>
                {
                    int main = ids[1];
                    k.SetUnitRelay(me, true);
                    CombatWeapon sw = Instant(CombatSite.RegisterProgram(k, P("fw_markcast")));
                    sw.MarkSeconds = 6f;
                    sw.Cooldown = 1f;
                    int s = Unit(k, CombatFaction.Player, new double2(-1.5, -1.5), k.AddWeapon(sw), 100f);
                    k.IssueCommand(s, CombatCommandKind.Attack, new double2(3.0, 0.3), main, 0.5f, 30f, 0.5f, false);
                };
                Squad(dead, lines, carrier, "中继应答", comp, "fw_pingfire", null, null, pingSetup,
                    (o, c) => o.PingsDelivered > 0 && o.Digest != c.Digest && (o.Subs > c.Subs || o.Zones > c.Zones));
                // N32 标记广播：本机武器带标记（标记桅杆）+ 中继友机装 [中继应答]。无人机子弹不产生 evt.marked（FG-GAP-204）→ 无人机舱整条不编译、预览写“本载体不适用”、读法如实写明
                if (carrier == FirmwareCarrier.Summon)
                {
                    EmgProgramResult na = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_markcast" }, carrier);
                    string reading = FirmwareKinds.Reading("fw_markcast", carrier) ?? string.Empty;
                    Judge(dead, carrier, "fw_markcast（本载体不适用：应整条不编译、预览写原因、读法写明）",
                        na.Notes.Any(n => n.Key == EmgProgramCompiler.NoteCarrierNa) && na.Steps.All(st => st.Decls.Count == 0) && reading.Contains("不适用"));
                    lines.Add($"{carrier}/标记广播：本载体不适用（FG-GAP-204），预览原因 {EmgProgramCompiler.NoteCarrierNa}，读法“{reading}”");
                }
                else
                {
                    Squad(dead, lines, carrier, "标记广播", comp, "fw_markcast", marker, null, (k, me, ids) => Receiver(k, new double2(-1.5, -1.5)),
                        (o, c) => o.Marks > 0 && o.PingsSent > 0 && o.PingsDelivered > 0 && o.Subs > c.Subs && c.PingsSent == 0);
                }
                // N42 齐射同步：正常装法（机器 2 个固件槽 + 接入口 2 条）一台机器里放不下“两条同相位时钟都在根里 + 齐射在根里”，相位对齐走 FGR-EMG-172 的中继组部分——
                // 本机 [时钟脉冲] 作条件提供者占接入口第 1 位（序号 0，相位 0），中继友机的慢速长飞弹体载着 [时钟脉冲]（序号 0，相位 0），同一拍各执行 1 条 = 组内对齐
                Action<CombatKernel, int, int[]> syncSetup = (k, me, ids) =>
                {
                    // 射弹载体：弹体 0.1 秒就命中，几乎跨不到拍界——条件提供者改成慢速长飞弹（与 E 段同一造法），本机宿主才在拍上执行时钟
                    if (carrier == FirmwareCarrier.Projectile && k.TryGetUnitWeapon(me, out CombatWeapon mw) && mw.SpawnFlight != 0)
                    {
                        mw.ProjectileSpeed = 1f;
                        mw.ProjectileLife = 10f;
                        k.SetUnitWeapon(me, k.AddWeapon(mw));
                    }
                    int pal = Unit(k, CombatFaction.Player, new double2(-1.5, -1.5), k.AddWeapon(Flight(Instant(CombatSite.RegisterProgram(k, P("fw_clock"))), 0.2f, 10f)), 100f);
                    k.SetUnitRelay(pal, true);
                    k.FireAt(pal, ids[ArenaBackIndex], k.Time);
                };
                Squad(dead, lines, carrier, "齐射同步", comp, "fw_volley", null, "fw_clock", syncSetup,
                    (o, c) => o.Syncs > 0 && o.Subs > c.Subs && o.Digest != c.Digest, prepared: true);
            }
            foreach (string l in lines)
            {
                Line("      " + l);
            }
            Expect(dead.Count == 0, $"6 条条件型新固件 × 5 载体（元程序首位；协同 / 齐射首位 + 末位）各自的行为证据成立、与对照指纹不同" + (dead.Count == 0 ? string.Empty : "：" + string.Join("；", dead)));
            // N30 的 ent.wreck 条件与同装回收修复的指令顺序无关（击杀生成了残骸就算，不看此刻是否已被拆走）
            long wa = ReclaimOrder(true, out int left), wb = ReclaimOrder(false, out _);
            Expect(wa == 1 && wb == 1 && left == 0, $"[回收修复, 击杀回传] 与 [击杀回传, 回收修复]（本机受伤、残骸被同一事件里排在前面的回收修复拆走，剩 {left} 块）都广播：{wa} / {wb} 次");
        }

        private static void Judge(List<string> dead, FirmwareCarrier carrier, string what, bool ok)
        {
            if (!ok)
            {
                dead.Add($"{carrier}/{what}");
            }
        }

        /// <summary>
        /// 首位 [固件, 填充] / 末位 [填充, 固件] 各与对照 [填充] 比（同一条件场景）。<paramref name="provider"/> 非空 = 条件提供者固件占接入口第 1 位（不参与首位 / 末位比较），
        /// 被测固件在本机固件位的填充之前 / 之后（与 FgEmgAddonFirmwareSelfCheck B 段同一摆法）。
        /// </summary>
        private static void Squad(List<string> dead, List<string> lines, FirmwareCarrier carrier, string name, string comp, string fw, Func<CombatWeapon, CombatWeapon> edit, string provider,
            Action<CombatKernel, int, int[]> setup, Func<Stage3Obs, Stage3Obs, bool> evidence, bool prepared = false)
        {
            string fl = AddonFiller;
            Stage3Obs c = provider == null ? RunStage3(comp, new[] { fl }, null, prepared, 4f, edit, setup) : RunStage3(comp, new[] { provider, fl }, null, prepared, 4f, edit, setup);
            Stage3Obs a = provider == null ? RunStage3(comp, new[] { fw, fl }, null, prepared, 4f, edit, setup) : RunStage3(comp, new[] { provider, fw }, new[] { fl }, prepared, 4f, edit, setup);
            Stage3Obs b = provider == null ? RunStage3(comp, new[] { fl, fw }, null, prepared, 4f, edit, setup) : RunStage3(comp, new[] { provider, fl }, new[] { fw }, prepared, 4f, edit, setup);
            Judge(dead, carrier, fw + "（首位）", evidence(a, c));
            Judge(dead, carrier, fw + "（末位）", evidence(b, c));
            lines.Add($"{carrier}/{name}：对照 广播 {c.PingsSent} 送达 {c.PingsDelivered} 对齐 {c.Syncs} 份 {c.Subs} 区 {c.Zones} · 首位 对齐 {a.Syncs} 击杀 {a.Kills} 标记 {a.Marks} 广播 {a.PingsSent} 送达 {a.PingsDelivered} 份 {a.Subs} 区 {a.Zones}" +
                      $" · 末位 击杀 {b.Kills} 标记 {b.Marks} 广播 {b.PingsSent} 送达 {b.PingsDelivered} 份 {b.Subs} 区 {b.Zones}");
        }

        /// <summary>中继友机：装中继、武器跑正式固件 [中继应答]（收听 0 号频道）。</summary>
        private static int Receiver(CombatKernel k, double2 at)
        {
            int r = Unit(k, CombatFaction.Player, at, k.AddWeapon(Instant(CombatSite.RegisterProgram(k, P("fw_pingfire")))), 100f);
            k.SetUnitRelay(r, true);
            return r;
        }

        /// <summary>矩阵场地里攻击者身后那个敌人（FgReadingMatrixSelfCheck ArenaBack）的下标：友机时钟弹朝它慢飞，远离主战场。</summary>
        private const int ArenaBackIndex = 6;

        /// <summary>A（受伤）装 [回收修复, 击杀回传] 或反序，击杀 5 血敌人（留残骸）；中继友机装 [中继应答]。返回广播次数。</summary>
        private static long ReclaimOrder(bool reclaimFirst, out int wrecksLeft)
        {
            using var k = new CombatKernel(Cfg(), 32);
            int pa = CombatSite.RegisterProgram(k, reclaimFirst ? P("fw_reclaim", "fw_killping") : P("fw_killping", "fw_reclaim"));
            int a = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(pa)), 100f);
            k.SetHealth(a, 40f, 100f, true);
            Receiver(k, new double2(5, 0));
            int weak = Unit(k, CombatFaction.Hostile, new double2(0, 3), -1, 5f);
            k.FireAt(a, weak, k.Time);
            StepN(k, 30);
            wrecksLeft = k.WreckCount;
            return k.EmgBusStats.PingsSent;
        }

        // ── 夹具 ──

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgSiteSeed = 12345u;
            c.EmgBeatSeconds = 0.5f;
            c.EmgRelayRange = 12f;
            c.EmgRelayPerSecond = 4;
            c.EmgEntity = 1; // 残骸（ent.wreck）
            return c;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatUnitKind kind = CombatUnitKind.Enemy) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : kind, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static CombatWeapon Instant(int program) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 0f, Program = program,
            HeatPerShot = 0f, OverheatAt = 15f, RecoverBelow = 4f, Dissipation = 0f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
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

        private static string Tree(EmgProgramResult r) => string.Join(" | ", r.Nodes.Select(n =>
            $"{n.Index}{(n.Parent >= 0 ? "<" + n.Parent : string.Empty)}:" + string.Concat(n.Entries.Select(e => " " + (e.Inherited ? "^" : string.Empty) + r.Steps[e.Ordinal].OwnerId.Replace("fw_", string.Empty)))));

        private static string Detail(List<string> bad) => bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad.Take(8));

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

        private static void Line(string s) => _report.AppendLine(s);
    }
}
