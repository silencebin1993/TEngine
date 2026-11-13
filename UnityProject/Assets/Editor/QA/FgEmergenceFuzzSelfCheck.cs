using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FUZZ-01 组合模糊测试器雏形与涌现满载场景（FG19 FGR-EMG-200～203、220、221、223 雏形；ADR-EMG-001 ⑫ ⑬；FGT-EMG-070 / 071 阶段 1、FGT-EMG-042）。
    /// 一份实现两个入口（ADR ⑫）：<see cref="Run"/> 挂 CellFrameworkValidate.RunAll（段名 FgEmergenceFuzzSelfCheck，固定种子 64 例）；
    /// <see cref="RunBatch"/>(seed, count, seconds) 长批次（影子工程 -executeMethod <see cref="RunBatchFromCommandLine"/>，不进 RunAll），报告写本机证据目录。
    /// 起真实系统、无画面：真实 Luban 表 → 真实蓝图编译（BlueprintCircuitCompiler / UplinkCompiler）→ 真实程序编译器 → 真实 CombatSite.WithProgram（含刷写变体）→ 真实内核，
    /// 用例中途走正式地点快照（CombatSite.Snapshot / TryRestore）。内核“按指令行的激活计数”只在本自检打开（CombatKernel.InstrActivationCounting），不进快照、不进状态哈希。
    /// A 用例与覆盖；B 有源（规则三）与缺口清单；C 固定 64 例（崩溃 / 卡死 / 死指令 / 确定性 / 存读档 / 积压 / 数值溢出；平庸、容量只报告）；
    /// D 负向（构造的死指令、无源端口、NaN、卡死、积压都必须被报出）；E 涌现满载性能场景（多地点，阶段 1 只判 FGR-EMG-200 绝对预算并存为“上一次”）。
    /// 单段：bash tools/unity-validate.sh FgEmergenceFuzzSelfCheck。
    /// </summary>
    public static partial class FgEmergenceFuzzSelfCheck
    {
        private const float Dt = 1f / 60f;
        public const int FixedSeed = 20261030;
        /// <summary>EMG4-FUZZ-01（FGR-EMG-223 阶段 4 常驻）：固定用例集 500 例、≤ 10 分钟（<see cref="FixedBudgetSeconds"/>）。</summary>
        public const int FixedCount = 500;
        public const double FixedBudgetSeconds = 600.0;
        public const float CaseSeconds = 8f;
        private const int UplinkSlot = 2;
        private const int SessionSlot = 7;
        /// <summary>卡死判定（FGR-EMG-221）：单步墙钟 &gt; 100 毫秒；事件队列连续 2 秒（120 步）只增不减。</summary>
        public const double StallStepMs = 100.0;
        public const int StallRiseSteps = 120;
        private const float DeathAtSeconds = 6.5f;
        private const float CooledSeconds = 14f;
        /// <summary>读“冷却完成”的用例时长：5 秒停火 + 后撤 14 米（约 4 秒）+ 无人机到期 + 积热从 2 倍阈值散到一半（散热 10 / 秒）。
        /// EMG4-FUZZ-01（#313 / #410 实测）：自己铺的火 / 熔渣把本机点燃，燃烧期间积热钉在上限到第 12～17 秒，之后按 10 / 秒散到一半还要 15 秒——时长 40 秒。</summary>
        private const float CoolSeconds = 40f;
        /// <summary>读“冷却完成”的用例：第 CoolHeatAtSeconds 秒起补积热到过热，第 CoolCeaseSeconds 秒停火。</summary>
        private const float CoolHeatAtSeconds = 3f;
        private const float CoolCeaseSeconds = 5f;

        private static StringBuilder _report;
        private static int _fail;

        private static readonly string[] SiteTemplates = { "home", "fractured_city", "foundry_outpost" };
        private static readonly string[] BaseChassisIds = { ChassisCatalog.ChassisWheelId, ChassisCatalog.ChassisTrackId, ChassisCatalog.ChassisHoverId, ChassisCatalog.ChassisFixedId };

        /// <summary>EMG3-CHS-02（FGR-EMG-142 / 220）：现行 4 种（顺序不变，用例与旧版逐字相同）+ 任何新落地、特性行已生效且在底盘目录里的底盘（将来多足 / 飞行 / 船体加一行表就自动纳入）。</summary>
        internal static string[] ChassisIds => BaseChassisIds.Concat(EmgCatalog.Current.TraitByChassis.Keys
            .Where(id => !BaseChassisIds.Contains(id) && EmgCatalog.TryGetActiveTrait(id, out _) && ChassisCatalog.TryGet(id, out _)).OrderBy(id => id, StringComparer.Ordinal)).ToArray();
        private static readonly string[] FunctionIds = { ComponentCatalog.FuncDashId, ComponentCatalog.FuncMarkerId, ComponentCatalog.FuncSpikesId };

        /// <summary>FG19A N09～N16 中阶段 1 生效的 6 条（EMG1-FW-02 转来，DEBT-EMG1FW02-01）与 N14 / N15（阶段 2，报缺口）。</summary>
        private static readonly string[] AddonStage1 = { "fw_scuttle", "fw_bouncefuse", "fw_piercefuse", "fw_ventarc", "fw_coolback", "fw_water" };
        /// <summary>EMG3-FW-02：FG19A 第 6 节阶段 3 落表的 12 条（N01～N08、N30～N32、N42）。</summary>
        internal static readonly string[] Stage3Firmware =
        {
            "fw_ifmark", "fw_ifhot", "fw_ifair", "fw_repeat", "fw_loop", "fw_random", "fw_reverse", "fw_defer", "fw_killping", "fw_pingfire", "fw_markcast", "fw_volley",
            "fw_anchorwire", // EMG3-FUSE-01：N43 接线（grm.anchor）进阶段 3 全量（FGT-EMG-003）
        };
        private static readonly string[] AddonStage2 = { "fw_enterfuse", "fw_uplinkpulse" };

        // ═════════════════════════════════ 入口 ═════════════════════════════════

        [MenuItem("BinGames/QA/自检/组合模糊测试器（EMG1-FUZZ-01）")]
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
            Line($"\n[组合模糊测试器] 常驻固定 {FixedCount} 例 + 有源缺口 + 负向 + 新颖度空间 + 涌现满载（EMG4-FUZZ-01；FGT-EMG-070 常驻、FGT-EMG-071 全满载、FGT-EMG-042）");
            Env env = Env.Enter();
            try
            {
                UseStage(EmgCatalog.ImplementedStage);
                Pools pools = Pools.Build();
                WarmUp(pools);
                Step(() => CheckCoverage(pools));
                List<string> observed = null;
                List<CaseResult> fixedResults = null;
                Step(() => observed = CheckFixedCases(pools, out fixedResults));
                Step(() => CheckNoveltySpace(pools, fixedResults));
                Step(() => CheckSources(observed ?? new List<string>()));
                Step(() => CheckNegatives(pools));
                Step(() => CheckResidentNegatives(pools));
                Step(() => CheckSubMass(pools));
                Step(CheckFullLoad);
                Step(CheckFullLoadMax);
            }
            finally
            {
                env.Exit();
            }
            return _fail;
        }

        /// <summary>长批次（ADR ⑫ 第 2 入口）：随机 <paramref name="count"/> 例，每例 <paramref name="seconds"/> 游戏秒；报告（死指令清单、帧耗前 20、新颖度前 20、事件分布）写本机证据目录。返回失败例数。</summary>
        public static int RunBatch(int seed, int count, float seconds)
        {
            _report = new StringBuilder();
            _fail = 0;
            Env env = Env.Enter();
            try
            {
                UseStage(EmgCatalog.ImplementedStage);
                Pools pools = Pools.Build();
                WarmUp(pools);
                var rnd = new System.Random(seed);
                var cases = new List<FuzzCase>(count);
                for (int i = 0; i < count; i++)
                {
                    cases.Add(RandomCase(rnd, i, pools, seconds));
                }
                var results = cases.Select(c => Evaluate(c, full: true)).ToList();
                string path = WriteReport($"emg-fuzz-batch-{seed}-{count}.md", $"长批次 seed={seed} count={count} seconds={seconds}", results);
                int failed = results.Count(r => r.Failed);
                Debug.Log($"[组合模糊测试器] 长批次 {count} 例：失败 {failed} 例；报告 {path}");
                return failed;
            }
            finally
            {
                env.Exit();
            }
        }

        /// <summary>影子工程入口：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmergenceFuzzSelfCheck.RunBatchFromCommandLine；
        /// 参数取环境变量 BINGAMES_FUZZ_SEED / BINGAMES_FUZZ_COUNT / BINGAMES_FUZZ_SECONDS（缺省 1 / 200 / 8）。</summary>
        public static void RunBatchFromCommandLine()
        {
            int seed = EnvInt("BINGAMES_FUZZ_SEED", 1);
            int count = Math.Max(1, EnvInt("BINGAMES_FUZZ_COUNT", 200));
            float seconds = Mathf.Clamp(EnvInt("BINGAMES_FUZZ_SECONDS", 8), 1, 120);
            int failed = -1;
            try
            {
                failed = RunBatch(seed, count, seconds);
                Debug.Log(failed == 0 ? "全部通过" : $"失败 {failed} 项");
            }
            catch (Exception e)
            {
                Debug.LogError("[组合模糊测试器] 长批次抛异常：" + e);
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(failed == 0 ? 0 : 1);
            }
        }

        private static int EnvInt(string key, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        /// <summary>预热：同一进程里第一次推进内核会同步编译 Burst 作业（实测单步上万毫秒），不算进任何用例的单步耗时（卡死判定只看稳态）。</summary>
        private static void WarmUp(Pools pools)
        {
            var c = new FuzzCase { Index = -1, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                Faction = pools.Factions[0], Enemies = 3, Uplinked = true, Uplink = new[] { "fw_split" } };
            using Arena a = Build(c, true, pools);
            for (int i = 0; i < 30; i++)
            {
                a.StepOnce(i);
            }
            a.Site.Snapshot();
        }

        /// <summary>环境：真实表、中文、测试战役会话；退出时还原（不影响别的段）。</summary>
        private sealed class Env
        {
            private CampaignState _session;
            private int _slot;
            private GameLanguage _lang;
            private int _workers;

            public static Env Enter()
            {
                var e = new Env { _session = CampaignSession.Current, _slot = CampaignSession.ActiveSlotIndex, _lang = GameSettings.Language, _workers = JobsUtility.JobWorkerCount };
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                CarrierReadings.Reload();
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSession.Set(SessionSlot, CampaignState.CreateNew("emg1fuzz", "Standard", 1));
                return e;
            }

            public void Exit()
            {
                EmgCatalog.ResetForTests();
                JobsUtility.JobWorkerCount = _workers;
                GameSettings.SetLanguage(_lang);
                if (_session != null)
                {
                    CampaignSession.Set(_slot, _session);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
        }

        // ═════════════════════════════════ 用例 ═════════════════════════════════

        /// <summary>可抽取的内容池（只取正常游戏此刻可获得、机器电路有实现的；核心固件只能插接入口且没有机器实现，不进池）。</summary>
        internal sealed class Pools
        {
            public readonly List<string> Firmware = new List<string>();
            public readonly List<string> Comps = new List<string>();
            public readonly List<string> Factions = new List<string>();
            public readonly Dictionary<string, List<GameConfig.fg.MechEnemy>> EnemiesByFaction = new Dictionary<string, List<GameConfig.fg.MechEnemy>>(StringComparer.Ordinal);
            public EmgSource Src;
            public string PierceProvider;

            public static Pools Build()
            {
                var p = new Pools { Src = EmgCatalog.FromTables() };
                foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
                {
                    if (row == null || !FirmwareKinds.IsObtainable(row.Id))
                    {
                        continue;
                    }
                    bool mixed = FirmwareKinds.IsMixedRow(row);
                    if (mixed || FirmwareKinds.HasMachineImplementation(row.Id))
                    {
                        p.Firmware.Add(row.Id);
                    }
                }
                // EMG4-LOOT-01：阵营标志组件（faction ≠ none，同载体的阵营变体）不进模糊池，固定集与满载基线保持可比；它们的交战覆盖见 DEBT-EMG4LOOT01-02
                p.Comps.AddRange(CarrierReadings.Components.Where(c => c.Slot == "main" && c.Faction == "none").Select(c => c.Id));
                foreach (GameConfig.fg.MechEnemy e in EnemyDefs.Rows)
                {
                    if (e == null || EnemyDefs.IsBoss(e) || EnemyDefs.IsStructure(e) || !(e.MaxHp > 0f) || string.IsNullOrEmpty(e.Faction))
                    {
                        continue;
                    }
                    if (!p.EnemiesByFaction.TryGetValue(e.Faction, out List<GameConfig.fg.MechEnemy> list))
                    {
                        p.EnemiesByFaction[e.Faction] = list = new List<GameConfig.fg.MechEnemy>();
                        p.Factions.Add(e.Faction);
                    }
                    list.Add(e);
                }
                // 条件提供者（FG19A 第 1.8 节“固定用例先造出条件”）：按端口找写 grm.mod.pierce 的可获得固件（不按内容 ID）
                p.PierceProvider = p.Firmware.FirstOrDefault(id => !FirmwareKinds.IsMixed(id) && p.Writes(id, "grm.mod.pierce"));
                return p;
            }

            private IEnumerable<EmgSource.Decl> DeclsOf(string fw)
            {
                IEnumerable<string> owners = FirmwareKinds.IsMixed(fw) ? FirmwareKinds.ExpandMixed(new[] { fw }) : new[] { fw };
                var set = new HashSet<string>(owners, StringComparer.Ordinal);
                int stage = EmgCatalog.EffectiveStage;
                return Src.Decls.Where(d => d != null && set.Contains(d.Owner) && d.StageGate <= stage);
            }

            public bool Reads(string fw, string port) => DeclsOf(fw).Any(d => d.Dir == "read" && d.Port == port);

            public bool Writes(string fw, string port) => DeclsOf(fw).Any(d => d.Dir == "write" && d.Port == port);

            /// <summary>池里固件此刻生效的声明读到的事件端口（按端口码排序）。</summary>
            public List<string> EventPortsRead()
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string fw in Firmware)
                {
                    foreach (EmgSource.Decl d in DeclsOf(fw))
                    {
                        if (d.Dir == "read" && d.Port.StartsWith("evt.", StringComparison.Ordinal))
                        {
                            set.Add(d.Port);
                        }
                    }
                }
                return set.OrderBy(x => Src.Ports.FirstOrDefault(pt => pt.Id == x)?.Code ?? 0).ToList();
            }
        }

        internal sealed class FuzzCase
        {
            public int Index;
            public string Chassis, Comp, Func, Site, Faction, Tag;
            public FirmwareCarrier Carrier;
            public string[] Uplink = Array.Empty<string>();
            public string[] Local = Array.Empty<string>();
            public bool Uplinked;
            public int Enemies;
            public int EnemySeed;
            public float Seconds = CaseSeconds;
            /// <summary>负向：场地不放任何条件源（偏折单位、障碍、残骸、热目标）。</summary>
            public bool NoWorldSources;
            /// <summary>EMG4-FUZZ-01（FGR-EMG-220 阶段 3 起“2～3 台己方机器协同”）：同装配的僚机台数（0～2）与是否装信号中继结构模块（struct_relay）。</summary>
            public int Mates;
            public bool Relay;
            /// <summary>EMG4-FUZZ-01（DEBT-EMG2REACT02-02）：规则行定向用例的规则 ID（fg.TbChannelRule.id；null = 不是）。</summary>
            public string RuleId;

            public string[] Program => Uplink.Concat(Local).ToArray();

            public string Text => $"#{Index} {Comp}({Carrier})/{Chassis}{(Func != null ? "+" + Func : string.Empty)} " +
                                  $"[{string.Join(",", Uplink)}{(Uplinked ? "|接入" : "|无接入")}|{string.Join(",", Local)}] {Site} {Faction}×{Enemies}{(Mates > 0 ? $" 协同×{Mates + 1}{(Relay ? "+中继" : string.Empty)}" : string.Empty)}{(Tag != null ? " " + Tag : string.Empty)}";
        }

        private static FirmwareCarrier CarrierOf(string comp) =>
            CarrierReadings.TryGetComponent(comp, out GameConfig.fg.CombatComponent row) ? row.Carrier switch
            {
                "melee" => FirmwareCarrier.Melee,
                "summon" => FirmwareCarrier.Summon,
                "aura" => FirmwareCarrier.Aura,
                "field" => FirmwareCarrier.Field,
                _ => FirmwareCarrier.Projectile,
            } : FirmwareCarrier.Projectile;

        /// <summary>
        /// 固定用例集：先放 N09～N13、N16 的定向用例（声明适用的每个载体至少 1 例、首位 / 末位交替，FGR-EMG-220 + 卡片），再用种子随机补到 <paramref name="count"/> 例；
        /// 随机部分主组件轮转 13 个、底盘轮转 4 种（不能装的跳过）、地点模板 3 种、阵营轮转，保证每个维度都被覆盖。
        /// </summary>
        internal static List<FuzzCase> FixedCases(Pools pools, int seed, int count)
        {
            var rnd = new System.Random(seed);
            var list = new List<FuzzCase>(count);
            int k = 0;
            foreach (string fw in AddonStage1)
            {
                foreach ((FirmwareCarrier carrier, string comp) in FgReadingMatrixSelfCheck.AddonCarriers)
                {
                    if (EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier).Steps.All(s => s.Decls.Count == 0))
                    {
                        continue; // 声明 carriers 不含本载体：C 段另断言“不编译 + 本载体不适用”
                    }
                    bool first = k++ % 2 == 0;
                    string filler = FgReadingMatrixSelfCheck.AddonFiller;
                    var c = new FuzzCase
                    {
                        Index = list.Count, Comp = comp, Carrier = carrier, Chassis = ChassisCatalog.ChassisWheelId, Site = SiteTemplates[list.Count % 3],
                        Faction = pools.Factions[list.Count % pools.Factions.Count], Enemies = 3 + list.Count % 10, EnemySeed = rnd.Next(), Uplinked = true,
                        Tag = $"定向 {fw} {(first ? "首位" : "末位")}",
                    };
                    c.Uplink = first ? new[] { fw, filler } : new[] { filler, fw };
                    EnsureConditions(c, pools);
                    list.Add(c);
                }
            }
            // 端口覆盖：每个被可获得固件读的事件端口，至少 1 例带它的一个读者（让“有源”有实测依据、死指令判定覆盖每个事件生产者）
            foreach (string port in pools.EventPortsRead())
            {
                if (list.Any(x => x.Program.Any(id => pools.Reads(id, port))))
                {
                    continue;
                }
                string fw = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && pools.Reads(id, port)).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
                if (fw == null)
                {
                    continue;
                }
                (FirmwareCarrier carrier, string comp) = FgReadingMatrixSelfCheck.AddonCarriers
                    .FirstOrDefault(t => EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, t.Carrier).Steps.Any(st => st.Decls.Count > 0));
                var c = new FuzzCase
                {
                    Index = list.Count, Comp = comp ?? ComponentCatalog.CompGunId, Carrier = comp != null ? carrier : FirmwareCarrier.Projectile, Chassis = ChassisIds[list.Count % 3],
                    Site = SiteTemplates[list.Count % 3], Faction = pools.Factions[list.Count % pools.Factions.Count], Enemies = 3 + list.Count % 10, EnemySeed = rnd.Next(),
                    Uplinked = true, Uplink = new[] { fw, FgReadingMatrixSelfCheck.AddonFiller }, Tag = $"端口覆盖 {port} ← {fw}",
                };
                EnsureConditions(c, pools);
                list.Add(c);
            }
            AddSquadCases(list, pools, rnd); // EMG4-FUZZ-01：2～3 台己方机器协同（含中继）
            AddRuleCases(list, pools, rnd); // EMG4-FUZZ-01（DEBT-EMG2REACT02-02）：规则表每行一例定向场景
            while (list.Count < count)
            {
                list.Add(RandomCase(rnd, list.Count, pools, CaseSeconds, list.Count));
            }
            return list;
        }

        /// <summary>随机用例（FGR-EMG-220）：程序 0～4 条（全部可获得固件含混合固件，随机顺序、随机有无接入）、13 个主作战组件之一 + 可选功能组件、4 种底盘、地点模板、按阵营 3～12 个敌人。
        /// <paramref name="rotate"/> ≥ 0 时主组件 / 底盘 / 地点 / 阵营按它轮转（固定集的覆盖保证），否则全随机（长批次）。</summary>
        internal static FuzzCase RandomCase(System.Random rnd, int index, Pools pools, float seconds, int rotate = -1)
        {
            var c = new FuzzCase { Index = index, Seconds = seconds };
            for (int tries = 0; ; tries++)
            {
                c.Comp = rotate >= 0 ? pools.Comps[(rotate + tries) % pools.Comps.Count] : pools.Comps[rnd.Next(pools.Comps.Count)];
                c.Chassis = rotate >= 0 ? ChassisIds[(rotate / pools.Comps.Count + rotate + tries) % ChassisIds.Length] : ChassisIds[rnd.Next(ChassisIds.Length)];
                if (CarrierReadings.CanMount(c.Chassis, c.Comp, out _, out _) || tries > 64)
                {
                    break;
                }
            }
            c.Carrier = CarrierOf(c.Comp);
            if (rnd.Next(3) == 0)
            {
                string f = FunctionIds[rnd.Next(FunctionIds.Length)];
                c.Func = CarrierReadings.CanMount(c.Chassis, f, out _, out _) ? f : null;
            }
            c.Uplinked = rnd.Next(2) == 0;
            int n = rnd.Next(0, 5);
            if (!c.Uplinked)
            {
                n = Math.Min(n, 2); // 没有接入：只有机器自己的 2 个固件槽
            }
            var fws = new List<string>();
            while (fws.Count < n)
            {
                string id = pools.Firmware[rnd.Next(pools.Firmware.Count)];
                if (!fws.Contains(id))
                {
                    fws.Add(id);
                }
            }
            c.Uplink = c.Uplinked ? fws.Take(2).ToArray() : Array.Empty<string>();
            c.Local = c.Uplinked ? fws.Skip(2).ToArray() : fws.ToArray();
            c.Site = rotate >= 0 ? SiteTemplates[rotate % SiteTemplates.Length] : SiteTemplates[rnd.Next(SiteTemplates.Length)];
            c.Faction = rotate >= 0 ? pools.Factions[rotate % pools.Factions.Count] : pools.Factions[rnd.Next(pools.Factions.Count)];
            c.Enemies = rnd.Next(3, 13);
            c.EnemySeed = rnd.Next();
            EnsureConditions(c, pools);
            return c;
        }

        /// <summary>
        /// 先造条件（FG19A 第 1.8 节）：程序里有读 evt.pierce 的指令、却没有写 grm.mod.pierce 的指令时，在它前面补一条穿透写者（按端口查，不按内容 ID）。
        /// 其余条件由场地提供：偏折单位（evt.bounce）、障碍 / 残骸（ter.*）、开火的敌人（evt.hurt）、读过热 / 冷却完成时开局积热、读阵亡时 6.5 秒判死。
        /// </summary>
        private static void EnsureConditions(FuzzCase c, Pools pools)
        {
            if (!c.Uplinked && c.Program.Any(id => pools.Reads(id, "evt.uplink")))
            {
                // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件：读“接入”的程序插上信号核（接入口不装固件，本机槽不变）——Build 按正式入口注入 evt.uplink；没插信号核时永远没有接入事件
                c.Uplinked = true;
                c.Tag = (c.Tag != null ? c.Tag + " " : string.Empty) + "补接入";
            }
            EnsureBeats(c, pools);
            if (c.Program.Any(id => pools.Reads(id, "evt.cooled")))
            {
                // 开局贴着过热阈值：过热 → 散热回落到阈值 × emg.heat.cool_ratio（与 FgEmgAddonFirmwareSelfCheck 同一造法）；EMG4-FUZZ-01：一直开火的格斗 / 力场 / 无人机积热回不到一半，
                // Build 里 4 秒后停火（玩家撤下过热的机器），停火后无人机到期、积热从上限（2 倍阈值）散到一半要 15 秒左右，时长跟着加
                c.Seconds = Math.Max(c.Seconds, CoolSeconds);
            }
            if (c.Program.Any(id => pools.Reads(id, "evt.bounce")))
            {
                c.Seconds = Math.Max(c.Seconds, CooledSeconds); // 偏折场每 4 秒才展开 1 秒：读反弹的程序跑长一些，多几次反弹机会
            }
            if (c.Carrier == FirmwareCarrier.Summon && c.Program.Any(id => pools.Reads(id, "evt.expire")))
            {
                // 同时读“击杀”的停火推迟到碎靶改令结束后 1 秒（Build 里的 CeaseStep），时长跟着加
                float late = c.Program.Any(id => pools.Reads(id, "evt.kill")) ? BaitAtSeconds + 3f - CeaseAtSeconds : 0f;
                c.Seconds = Math.Max(c.Seconds, SummonExpireSeconds + late);
            }
            EnsureHackWriter(c, pools);
            string[] prog = c.Program;
            int reader = Array.FindIndex(prog, id => pools.Reads(id, "evt.pierce"));
            if (reader < 0 || pools.PierceProvider == null || prog.Any(id => pools.Writes(id, "grm.mod.pierce")))
            {
                return;
            }
            var list = prog.ToList();
            list.Remove(prog[reader]);
            list.Insert(0, prog[reader]);
            list.Insert(0, pools.PierceProvider);
            if (!c.Uplinked)
            {
                c.Uplinked = true; // 补的提供者占接入口：AI 驾驶只有 2 个本机槽
            }
            list = list.Take(4).ToList();
            c.Uplink = list.Take(2).ToArray();
            c.Local = list.Skip(2).ToArray();
            c.Tag = (c.Tag != null ? c.Tag + " " : string.Empty) + "补穿透写者";
        }

        /// <summary>
        /// EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件：读 tmp.sync（相位对齐，FGR-EMG-172）的程序要本机同一拍有 2 条以上节拍指令执行——程序里节拍指令（读 evt.beat 且读 tmp.beat）不到 2 条时，
        /// 换成 [读者, 节拍, 填充, 节拍]：两条节拍在第 1 / 3 位（周期 2 时相位都是 1，同拍执行），读者在第一层——触发器后面紧跟的那条落进它的子节点，填充垫在中间，两条节拍与读者都留在第一层（重炮这类短命弹体只在第一层宿主上出拍）。按端口查，不按内容 ID。
        /// </summary>
        private static void EnsureBeats(FuzzCase c, Pools pools)
        {
            string[] prog = c.Program;
            int reader = Array.FindIndex(prog, id => pools.Reads(id, "tmp.sync"));
            string clock = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && pools.Reads(id, "evt.beat") && pools.Reads(id, "tmp.beat")).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            if (reader < 0 || clock == null || prog.Count(id => pools.Reads(id, "tmp.beat")) >= 2)
            {
                return;
            }
            string filler = prog.FirstOrDefault(id => id != prog[reader] && !FirmwareKinds.IsMixed(id) && !pools.Reads(id, "tmp.beat") && !pools.Writes(id, "grm.trig")) ?? FgReadingMatrixSelfCheck.AddonFiller;
            c.Uplinked = true;
            c.Uplink = new[] { prog[reader], clock };
            c.Local = new[] { filler, clock };
            c.Tag = (c.Tag != null ? c.Tag + " " : string.Empty) + "补节拍";
        }

        /// <summary>
        /// EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件：读 hij.hijacked 条件（FG19A N40 劫持中继）的程序要有被它自己劫持的单位——劫持只在“入侵度满的杂兵被本机击毁”时成立，
        /// 程序里没有写 hij.hack 的指令时在读者前面补一条入侵写者（按端口查，同穿透写者的补法）；场地另放一个耐久约 2.5 发的杂兵并令本机先打它（Build）。
        /// </summary>
        private static void EnsureHackWriter(FuzzCase c, Pools pools)
        {
            string[] prog = c.Program;
            int reader = Array.FindIndex(prog, id => pools.Reads(id, "hij.hijacked"));
            string writer = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && pools.Writes(id, "hij.hack")).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            if (reader < 0 || writer == null || prog.Any(id => pools.Writes(id, "hij.hack")))
            {
                return;
            }
            var list = prog.ToList();
            list.Remove(prog[reader]);
            list.Insert(0, prog[reader]);
            list.Insert(0, writer);
            c.Uplinked = true;
            list = list.Take(4).ToList();
            c.Uplink = list.Take(2).ToArray();
            c.Local = list.Skip(2).ToArray();
            c.Tag = (c.Tag != null ? c.Tag + " " : string.Empty) + "补入侵写者";
        }

        private static ushort PortCode(string id) => EmgCatalog.Current.PortIdByCode.FirstOrDefault(kv => kv.Value == id).Key;

        private static bool ProgramWritesPort(EmgProgramResult r, ushort port) => r != null && r.KernelInstrs.Any(x => x.Port == port && x.Role > EmgConst.RoleLastRead);

        private static bool ProgramReadsPort(EmgProgramResult r, ushort port) => r != null && r.KernelInstrs.Any(x => x.Port == port && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead);

        // ═════════════════════════════════ 场地 ═════════════════════════════════

        private sealed class Arena : IDisposable
        {
            public CombatSite Site;
            public CombatKernel K => Site.Kernel;
            public int Me;
            public int Target;
            public CombatWeapon W;
            public EmgProgramResult Program;
            public bool Preheat;
            public bool Airborne;
            public int KillStep = -1;
            /// <summary>偏折单位（−1 = 没有）：偏折场罩住主目标（不罩本机），每 4 游戏秒展开 1 秒（与首领状态机同一入口 SetDeflectPhase），
            /// 展开时打主目标被反弹（evt.bounce 世界源）、收起时正常命中（evt.hit）。</summary>
            public int Prism = -1;
            public double2 MainAt;
            public int CeaseStep = -1;
            /// <summary>读“击杀”的用例：第 BaitStep 步改令打碎靶，2 游戏秒后改回主目标（−1 = 不改令）。</summary>
            public int BaitStep = -1;
            public int Bait = -1;
            public double2 BaitAt;
            /// <summary>EMG4-FUZZ-01：读“目标被劫持”的用例开局先打的杂兵（−1 = 没有）。</summary>
            public int HijackBait = -1;
            /// <summary>EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）：读“夺控”的协同用例在第 SeizeStep 步夺控僚机 SeizeUnit（同程序）2 游戏秒，夺控者 = 主目标（−1 = 不夺控；内核正式入口 CombatKernel.Seize）。</summary>
            public int SeizeStep = -1;
            public int SeizeUnit = -1;
            /// <summary>改令打碎靶后保持的步数：默认 2 秒；重炮这类慢出手按冷却 + 瞄准 + 飞行算，窗口里至少打到碎靶一发（DEBT-EMG4FUZZ01-02）。</summary>
            public int BaitHold = 2 * SwitchSteps;
            /// <summary>补积热的步区间 [PreheatFrom, PreheatTo)：读过热的从开局起 3 秒；读冷却完成的在第 3～5 秒（先正常打一阵，再过热、停火散热）。</summary>
            public int PreheatFrom;
            /// <summary>补碎靶的耐久（与开局那个同一档：当场结算的载体 = 母载荷一击 + 余量，其余 0.1）。</summary>
            public float BaitHp = 0.1f;
            /// <summary>读冷却完成、能走的机器：停火那一步同时后撤（离开自己铺的火 / 熔渣，站在热区里散热回不到一半）。</summary>
            public bool CoolRetreat;
            public int PreheatTo = PreheatSteps;
            public float Engage;
            public double T;
            public readonly List<int> Hostiles = new List<int>();
            public List<float3> Obstacles = new List<float3>();
            /// <summary>EMG4-FUZZ-01：规则行定向场景（null = 没有）与僚机（协同用例）。</summary>
            public RuleScene Rule;
            public List<int> Mates = new List<int>();

            public void Dispose() => Site?.Dispose();

            /// <summary>
            /// 子节点读“击杀”要子载荷打出致死一击，而母载荷（溅射 / 力场）通常先把附近的碎靶打掉，所以碎靶跟着子载荷放（FGR-EMG-220 先造条件）：
            /// 飞行子弹药放在前方、抛射子弹药放在落点；子区域与母区域同点、节拍同相、按表序结算，放在区域中心的碎靶耐久按阶梯铺开，总有一档的致死一击落在子区域的节拍上。
            /// 只看内核状态（子载荷进快照）与步号，不留计数，存读档 / 分批推进都走同样的序列。
            /// </summary>
            private void BaitChildPayload(int step)
            {
                for (int p = 0; p < K.ProjectileCount; p++)
                {
                    if (K.TryGetProjectile(p, out CombatProjectile pr) && pr.Prog >= 0 && pr.Node > 0 && pr.Owner == Me)
                    {
                        float ahead = (pr.Flags & EmgProjFlags.Lob) != 0 ? pr.Life : Dt * 2f;
                        K.Spawn(Hostile(pr.Pos + (double2)(pr.Vel * ahead), 0.1f, 0.4f, -1));
                        return;
                    }
                }
                for (int z = 0; z < K.ZoneCount; z++)
                {
                    if (K.TryGetZone(z, out CombatZone child) && child.EmgProgRef > 0 && child.EmgNode > 0 && child.Owner == Me)
                    {
                        K.Spawn(Hostile(child.Pos, 0.5f * (1 + step / ChildBaitSteps % 60), 0.4f, -1));
                        return;
                    }
                }
            }

            public void StepOnce(int i)
            {
                if (i == CeaseStep)
                {
                    K.ClearCommand(Me);
                    if (CoolRetreat)
                    {
                        K.IssueCommand(Me, CombatCommandKind.Move, new double2(-12.0, -8.0), 0, 0.5f, 0f, 0f, false);
                    }
                }
                if (i == BaitStep)
                {
                    // 碎靶已被打主目标的溅射 / 子载荷提前打掉（重炮实测：开局那发的余波就打死 0.1 耐久的碎靶，改令打一个死目标、整个窗口不开火）：改令这一刻在原处补一个
                    Bait = Bait >= 0 && K.TryGetUnit(Bait, out CombatUnitView bv) && (bv.Flags & CombatUnitFlags.Alive) != 0 ? Bait : K.Spawn(Hostile(BaitAt, BaitHp, 0.4f, -1));
                    K.IssueCommand(Me, CombatCommandKind.Attack, BaitAt, Bait, 0.5f, Engage, 0.5f, false);
                }
                else if (BaitStep >= 0 && i == BaitStep + BaitHold)
                {
                    K.IssueCommand(Me, CombatCommandKind.Attack, MainAt, Target, 0.5f, Engage, 0.5f, false);
                }
                if (BaitStep >= 0 && i % ChildBaitSteps == 0)
                {
                    BaitChildPayload(i);
                }
                if (i == KillStep)
                {
                    K.Kill(Me, 0); // 己方机器血量在热更层（外部血量），判死走 Kernel.Kill(unit, 0)（ADR-EMG-001 ⑦，与 CombatSite.OnMachineDied 同一入口）
                }
                if (i == SeizeStep && SeizeUnit >= 0)
                {
                    K.Seize(SeizeUnit, 2f, Target, 0);
                }
                if (Preheat && i >= PreheatFrom && i < PreheatTo && W.OverheatAt > 0f && K.TryGetUnit(Me, out CombatUnitView mv) && (mv.Flags & CombatUnitFlags.Overheated) == 0 && mv.Heat < W.OverheatAt - 0.5f * Mathf.Max(0.02f, W.HeatPerShot))
                {
                    // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）：开局贴阈值的积热在接近目标的路上被散热吃掉（实测 coolback 6 例全程没过热）——窗口里没过热、且积热低到下一发过不了阈值时补回阈值下沿，下一发即过热。
                    // 只在“下一发过不了阈值”时补（不是每步补）：SetWeaponState 会打断重炮的瞄准，每步补的话重炮整个窗口一发都打不出（#353 实测）。只看内核状态与步号
                    K.SetWeaponState(Me, W.OverheatAt - 0.01f, false, mv.AimReadyAt, mv.NextFireAt);
                }
                if (Prism >= 0 && i > 0 && i % SwitchSteps == 0)
                {
                    // 每 4 秒展开 1 秒，另在第 7k+5、7k+6 秒多展开（再多一档 8k+1 实测让重炮每发都被弹、9 例命中类指令全死，已撤；重炮真开火在瞄准 1 秒之后，#197 每发落在 4k+2 秒，7k+6 接住第 6 秒那发）：反弹有机会、重炮这类慢出手也有大半时间能正常命中；
                    // EMG4-FUZZ-01：重炮出手周期正好 4 秒（冷却 3 + 瞄准 1），只按 4 秒轮换时每发都落在收起的 3 秒里、永远不反弹（fw_bouncefuse 实测 0 次），加一档错开周期的窗口
                    long sec = i / SwitchSteps;
                    K.SetDeflectPhase(Prism, (byte)(sec % 4 == 0 || sec % 7 >= 5 ? 2 : 0), PhaseHold);
                }
                if (Rule != null)
                {
                    RuleStep(this, i);
                }
                Site.Step(Dt, T);
                T += Dt;
            }
        }

        /// <summary>偏折场相位检查间隔（1 游戏秒）。</summary>
        private const int SwitchSteps = 60;

        /// <summary>偏折相位保持秒数（远大于用例时长：相位只由 <see cref="SwitchSteps"/> 切换，不自己走）。</summary>
        private const float PhaseHold = 1e6f;

        /// <summary>读“定时”的程序：主目标放在弹体飞这么多秒才到的距离（阶段 1 定时最长的延时引信 0.6 秒，留余量）。</summary>
        private const double TimerReachSeconds = 1.0;

        /// <summary>读“节拍”的飞行程序：主目标放在弹体飞这么多秒才到的距离（拍长 0.5 秒）。</summary>
        private const double BeatReachSeconds = 0.6;

        /// <summary>召唤载体读“终结”的用例：开打这么多秒后停火；用例时长 = 停火 + 最长召唤存续（哨戒桩 14 秒）+ 1 秒。</summary>
        private const float CeaseAtSeconds = 4f;
        private const float BaitAtSeconds = 4f;
        private const int ChildBaitSteps = 6;
        private const float SummonExpireSeconds = CeaseAtSeconds + 15f;
        /// <summary>读过热 / 冷却完成的用例：头这么多步没过热就把积热补回阈值下沿（Arena.StepOnce）。</summary>
        private const int PreheatSteps = 180;
        /// <summary>读“夺控”的协同用例：开打这么多秒后夺控一台僚机。</summary>
        private const float SeizeAtSeconds = 3f;

        private static CombatConfig Config()
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            return cfg;
        }

        /// <param name="pin">EMG4-FUZZ-01 按行为判死（去掉一条指令重跑）：场景布置（主目标距离、先造条件的诱饵 / 预热 / 判死 / 停火）按原用例的程序与武器定，
        /// 只换程序本身，否则“去掉这条”会连带改场景、行为差异不是这条指令造成的。null = 按本用例自己的程序布置。</param>
        private static Arena Build(FuzzCase c, bool counting, Pools pools, Arena pin = null)
        {
            var a = new Arena { Site = new CombatSite("fuzz_" + c.Site, ConfigFor(c)) };
            CombatKernel k = a.K;
            k.InstrActivationCounting = counting;
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(c.Chassis, c.Comp, c.Func, c.Relay ? ComponentCatalog.StructRelayId : null, c.Local);
            BlueprintCircuitPreview p;
            if (c.Uplinked)
            {
                board.TrySetUplink(UplinkSlot);
                p = UplinkCompiler.CompileUplinked(board, c.Uplink);
            }
            else
            {
                p = BlueprintCircuitCompiler.CompilePreview(board);
            }
            bool fixedChassis = CarrierReadings.IsFixedChassis(c.Chassis);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            w = a.Site.WithProgram(p, w, out EmgProgramResult program, out _);
            if (pin != null)
            {
                // 去掉重跑（按行为判死）：每发积热 / 取水热按原用例固定——固件表热量是成本不是效果，否则凡带热量的固件去掉后积热必变、永远判不成死指令（审查 P1）
                w.HeatPerShot = pin.W.HeatPerShot;
                w.DrawHeat = pin.W.DrawHeat;
            }
            a.Program = program ?? new EmgProgramResult();
            a.W = w;
            EmgProgramResult sp = pin != null ? pin.Program : a.Program; // 场景布置看的程序 / 武器（见 pin）
            CombatWeapon sw = pin != null ? pin.W : w;
            int wi = a.Site.WeaponIndex(w);
            a.Me = k.Spawn(new CombatSpawn
            {
                ExtKey = 0, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.ExternalHealth,
                Position = double2.zero, Home = double2.zero, Radius = 0.6f, Speed = fixedChassis ? 0f : 4f, Health = 2000f, MaxHealth = 2000f, Weapon = wi, BehaviorProfile = -1,
            });
            // EMG4-FW-01（用例集随新固件入池换了一批后实测：悬浮机 [条件门·离地, 时钟脉冲…] 子节点全程不激活）：本机按正式路径带上底盘特性（CombatSite 机器生成同一入口），
            // 否则 chs.* 条件 / 离地读法在模糊测试里一律按“没有特性”求值，悬浮的“离地”永远不成立
            a.Site.ApplyChassisTrait(a.Me, c.Chassis);
            a.Airborne = (k.ChsOf(a.Me) & EmgTraitConst.ChsAirborne) != 0; // 底盘特性决定，全程不变（FG-GAP-215 判恒不成立的门用；判死时这台场地已释放）
            // 主目标：正面装甲朝着本机（与 FgReadingMatrixSelfCheck 条件场景同一摆法）。
            // 先造条件（FGR-EMG-220）：读“定时”的飞行程序把目标放到定时到点之后才够得着的距离
            //（延时引信“提前命中不空爆”，目标太近时定时永远不到点）；攻击命令的接战距离随之放宽，玩家正常也会远距离下令。
            // 固定底盘不会走：主目标放进贴身载体的触及（推铲 / 脉冲器接近距离 2.5 + 目标半径 = 3.0 米，3.0 米外的目标它永远够不着）
            a.MainAt = fixedChassis ? new double2(2.4, 0.3) : new double2(3.0, 0.3);
            a.Engage = 6f;
            // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）：读“节拍”的飞行程序，宿主是在飞的弹体（FG19A 第 1 节射弹读法“需要飞行”）——3 米外的目标飞 0.1 秒，出手周期与 0.5 秒拍长互质不了时每发都落在两拍之间（实测重炮 / 维修束全程 0 拍），
            // 目标放到飞 0.3 秒的距离（不超出射程）；维修束的受损友军同理放远
            bool beatFlight = sw.ProjectileSpeed > 0f && ProgramReads(sp, EmgEventKind.Beat) && !ProgramReads(sp, EmgEventKind.Timer);
            double beatFar = beatFlight ? Math.Min(sw.ProjectileSpeed * BeatReachSeconds + 1.5, Math.Max(3.0, sw.Range - 0.8)) : 0;
            if ((sw.SpawnFlight == 1 || sw.Mode == CombatWeaponMode.Projectile) && sw.ProjectileSpeed > 0f)
            {
                double far = ProgramReads(sp, EmgEventKind.Timer) ? sw.ProjectileSpeed * TimerReachSeconds + 1.5 : beatFar;
                if (far > 3.0)
                {
                    a.MainAt = new double2(0.3, -far); // 正下方：随机敌人只在 ±60° 扇面里，弹道上没有别的敌人先挡住（否则 0.6 秒前就命中）
                    // EMG4-FUZZ-01：接战距离不超过武器射程——原来 far + 1 = 24.5 米 > 重炮射程 20，机器以为已在接战距离内、原地不动也打不着（#353 实测第一发之后再不开火）
                    a.Engage = sw.Mode == CombatWeaponMode.Cannon && sw.Range > 1f ? Mathf.Min((float)far + 1f, sw.Range - 0.5f) : (float)far + 1f; // 只收重炮：机枪的有效射程不按 Range 算，收了反而定时打不到（#28 实测）
                }
            }
            CombatSpawn main = Hostile(a.MainAt, 5000f, 0.5f, -1);
            main.ArmorFraction = 0.5f;
            main.ArmorHalfAngleDeg = 70f;
            main.ArmorFacing = new float2(-1f, 0f);
            a.Target = k.Spawn(main);
            a.Hostiles.Add(a.Target);
            // 按阵营抽的敌人（真实表的耐久 / 半径 / 武器），种子决定站位
            ushort timerPort = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Timer);
            bool childTimer = sp.KernelNodes.Count > 1 && sp.KernelInstrs.Skip(sp.KernelNodes[0].FirstInstr + sp.KernelNodes[0].InstrCount).Any(x => x.Port == timerPort && x.Role == EmgConst.RoleTrigger);
            var er = new System.Random(c.EnemySeed);
            List<GameConfig.fg.MechEnemy> rows = pools.EnemiesByFaction.TryGetValue(c.Faction ?? string.Empty, out List<GameConfig.fg.MechEnemy> l) ? l : pools.EnemiesByFaction.Values.First();
            for (int i = 1; i < c.Enemies; i++)
            {
                GameConfig.fg.MechEnemy row = rows[er.Next(rows.Count)];
                double ang = math.radians(-60.0 + er.NextDouble() * 120.0);
                double dist = 2.5 + er.NextDouble() * 6.5;
                if (childTimer && math.distance(new double2(math.cos(ang), math.sin(ang)) * dist, a.MainAt) < Mathf.Clamp(row.Radius, 0.3f, 1.5f) + 1.5)
                {
                    // EMG4-FUZZ-01（#321 实测）先造条件：子节点读“定时”的程序，子载荷在主目标处放出（命中终结），贴着主目标的随机敌人（半径到 1.5 米）一出生就被撞上、定时永远到不了点——
                    // 这样的随机敌人沿原方向挪远（随机数序列不变）。按端口查
                    dist += Mathf.Clamp(row.Radius, 0.3f, 1.5f) + 3.0;
                }
                int weapon = row.WeaponDamage > 0f && row.WeaponRange > 0f
                    ? a.Site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = row.WeaponRange, Damage = row.WeaponDamage, Cooldown = Mathf.Max(0.2f, row.WeaponCooldown) })
                    : -1;
                CombatSpawn s = Hostile(new double2(math.cos(ang) * dist, math.sin(ang) * dist), row.MaxHp, Mathf.Clamp(row.Radius, 0.3f, 1.5f), weapon);
                a.Hostiles.Add(k.Spawn(s));
            }
            // 维修类载体的友方目标（受损的己方结构；耐久上限足够大，整例都修不满）
            double2 allyAt = beatFlight && w.HasOutput == CombatConst.OutputRepair ? math.normalize(new double2(-1.5, 1.0)) * beatFar : new double2(-1.5, 1.0);
            int ally = k.Spawn(new CombatSpawn
            {
                ExtKey = 0, Kind = CombatUnitKind.Structure, Faction = CombatFaction.Player, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = allyAt, Home = allyAt, Radius = 0.6f, Health = 200f, MaxHealth = 100000f, Weapon = -1, BehaviorProfile = -1,
            });
            // 第二个受损友军在第一个身后同一方向：修复弹体穿过第一个才有“穿透”（维修束 + 穿透类固件）
            k.Spawn(new CombatSpawn
            {
                ExtKey = 0, Kind = CombatUnitKind.Structure, Faction = CombatFaction.Player, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(-3.0, 2.0), Home = new double2(-3.0, 2.0), Radius = 0.6f, Health = 200f, MaxHealth = 100000f, Weapon = -1, BehaviorProfile = -1,
            });
            // 子节点读“定时”（子弹药飞 0.6 秒空爆，FG19A 第 1.1 节第 6 行“先命中则不空爆”）：径向放出的子弹药 0.6 秒内撞上偏折单位 / 侧翼一圈就永远不到点（实测 #321 fw_delayfuse 格斗子节点 0 次、4 发被偏折场弹掉），
            // 这类程序不放世界源与侧翼一圈（与用例字段 NoWorldSources 同一开关）
            if (!c.NoWorldSources && !childTimer)
            {
                // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02，#197 实测）先造条件：读“反弹”的射弹载体——重炮弹体半径大，主目标露在 0.8 米场外的那半边先被弹体碰到、弹体中心进不了场，全程 0 次反弹；
                // 场半径放到 1.6 米（罩住主目标全身，场边离本机仍 &gt; 1 米；格斗 / 无人机贴近目标出手，不放大）。按端口 + 载体判
                AddWorldSources(a, c.Site, sw.Reading.Carrier == CombatCarrier.Projectile && ProgramReads(sp, EmgEventKind.Bounce) ? 1.6f : 0.8f, markMain: !ProgramWrites(sp, EmgEventKind.Marked));
            }
            // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件，按载体 / 端口（场景按原用例的程序定，去掉重跑时不变）：
            // ① 格斗：基础扇形（推铲半角 60°）外、加宽后（寻的 +24° × 幅度、侧偏、体积）够得着的侧翼敌人——主目标方向偏 70°、2.2 米
            // ② 写穿透的格斗：主目标身后 1.8 米、侧偏 1 米（PierceBehind 横向 ≤ 半径 + 0.6）的敌人，避开主目标后面那块残骸
            // ③ 写穿透的力场 / 布区：本机与主目标之间一块小残骸（掩体），不穿透时脉冲打不到 / 布区落在残骸前（EmgPulseBlocked / EmgDropPoint）
            // ④ 写破甲的程序：随机敌人带正面装甲（50%），破甲才有东西可破
            // ⑤ 读“目标被劫持”（N40 劫持中继）：耐久约 2.5 发的杂兵，本机开局先打它——入侵度两发满阈值（FG19A N39），击毁那一下被劫持（CombatLogic.EmgTryHijack）
            double2 mainDir = math.normalizesafe(a.MainAt, new double2(1.0, 0.0));
            if (sw.Reading.Carrier == CombatCarrier.Melee && !childTimer)
            {
                // 一侧每 15° 一个（70°～160°）：加宽量（寻的 +24° × 0.85 ≈ 20°）大于间隔，不论基础扇形已被别的固件加宽到多少，总有一个只在加宽后才够得着
                for (int deg = 70; deg <= 160; deg += 15)
                {
                    double ang = -math.radians(deg);
                    a.Hostiles.Add(k.Spawn(Hostile(new double2(mainDir.x * math.cos(ang) - mainDir.y * math.sin(ang), mainDir.x * math.sin(ang) + mainDir.y * math.cos(ang)) * 2.2, 5000f, 0.4f, -1)));
                }
            }
            if (ProgramWritesPort(sp, PortCode("grm.mod.pierce")))
            {
                if (sw.Reading.Carrier == CombatCarrier.Melee)
                {
                    a.Hostiles.Add(k.Spawn(Hostile(a.MainAt + mainDir * 1.8 + new double2(-mainDir.y, mainDir.x) * 1.0, 5000f, 0.5f, -1)));
                }
                else if (sw.Reading.Carrier == CombatCarrier.Aura || c.Carrier == FirmwareCarrier.Field)
                {
                    k.TryAddWreck(new EmgWreck { Pos = a.MainAt * 0.55, Radius = 0.3f, Hp = 1e6f, MaxHp = 1e6f, Until = 1e9 }, out _);
                }
            }
            if (ProgramWritesPort(sp, PortCode("grm.mod.armor")))
            {
                foreach (int id in a.Hostiles.Skip(1).ToList())
                {
                    k.SetArmor(id, 0.5f, 90f); // 朝向沿用 Hostile 的 (-1, 0)（朝本机一侧）
                }
            }
            if (ProgramWritesPort(sp, PortCode("prop.conductive")))
            {
                // 审查 P1 修复轮（按行为判死）先造条件：桥接导线（evt.hit 门控的 prop.conductive 写端，CombatLogic.EmgLink）的 B 端要 A 处 5 米内另有一个导体——
                // 随机敌人 / 世界残骸都不导电，全程拉不出导线。本机与主目标连线中点侧偏 1.2 米放一小片水（导电斑块，不罩本机也不罩主目标）。按端口
                k.SpawnPatch(EmgMatterConst.Water, a.MainAt * 0.5 + new double2(-mainDir.y, mainDir.x) * 1.2, 0.5f, 1f, 0f, 0, true);
            }
            // 修复轮 1 第 2 棒先造条件（#353）：写反弹修饰（grm.mod.bounce）的程序——命中后弹向命中点 6 米内另一个敌人（CombatLogic.Grammar EmgChainPick），
            // 随机敌人可能都在 6 米外；主目标侧旁 2.5 米放一个敌人（耐久高，不挡本机与主目标的连线）。按端口
            if (sp.KernelInstrs.Any(x => x.Role > EmgConst.RoleLastRead && EmgCatalog.Current.PortIdByCode.TryGetValue(x.Port, out string bid) && bid == "grm.mod.bounce"))
            {
                double2 side = new double2(-mainDir.y, mainDir.x) * 2.5;
                a.Hostiles.Add(k.Spawn(Hostile(a.MainAt + side, 5000f, 0.5f, -1)));
            }
            bool repairCarrier = w.HasOutput == CombatConst.OutputRepair && w.Mode != CombatWeaponMode.Cannon;
            if (ProgramReadsPort(sp, EmgConst.PortHijacked) && !repairCarrier)
            {
                // 格斗一次挥击可多段命中（实测 #295 环刃两击打死 2.5 发耐久的碎靶），击杀那一击的入侵度在阵亡判定之后才记（CombatLogic.Kill → EmgTryHijack 只看已有累积）：格斗按 4.5 发
                float baitShots = sw.Reading.Carrier == CombatCarrier.Melee ? 4.5f : 2.5f;
                // 上限耐久放高：劫持成立后按上限 × 劫持耐久比（0.3）重启为己方，旁边的敌人当步就会打它——原先上限 = 当前耐久（约 8 点），
                // 重启后被身旁持枪的敌人一枪打死、劫持当步解除（#295 实测：CombatLogic.Kill ← StepHostileUnit），改刷来不及留下任何行为
                CombatSpawn bait = Hostile(a.MainAt + new double2(-1.0, 0.6), Mathf.Max(1f, sw.Damage) * baitShots + 0.05f, 0.4f, -1);
                bait.MaxHealth = 2000f;
                a.HijackBait = k.Spawn(bait);
                a.Hostiles.Add(a.HijackBait);
            }
            if (ProgramReads(sp, EmgEventKind.Bounce))
            {
                // 读“反弹”的程序：被偏折场弹回的方向（本机身后）放一个敌人，反弹放出的子载荷才有东西可打（FGR-EMG-220 先造条件）
                a.Hostiles.Add(k.Spawn(Hostile(new double2(-2.6, -0.4), 5000f, 1.2f, -1))); // 半径大一些：无人机子弹（4.8 米射程）从场边往回飞也够得着
            }
            if (c.Carrier == FirmwareCarrier.Field && sp.Steps.Any(st => st.Kind == EmgStepKind.Trigger) && ProgramReads(sp, EmgEventKind.Hit))
            {
                // EMG2-MAT-01：力场的子载荷是小脉冲（触及 1.2 米，在脉冲结束处放出）——子节点读“命中”的程序在本机贴身处放一个敌人，子脉冲才有东西可打（FGR-EMG-220 先造条件）
                a.Hostiles.Add(k.Spawn(Hostile(new double2(0.8, -0.6), 5000f, 0.5f, -1)));
            }
            if (ProgramReads(sp, EmgEventKind.Kill))
            {
                // EMG2-RECL-01：读“击杀”的程序（回收修复及其混合固件）——主目标 5000 耐久、8 秒打不死（FGR-EMG-220 先造条件）：放一个碎靶，开打 4 秒后玩家改令打它 2 秒；子节点读者另见 Arena.BaitChildPayload。
                // 格斗 / 力场当场结算、一圈全打，子载荷生成在被令攻击的目标身上、同一步撞上它：碎靶到改令那一步才生成在本机一侧（固定底盘也够得着），耐久 = 母载荷一击 + 余量，母打不死、子收尾
                bool instantHost = sw.Reading.Carrier == CombatCarrier.Melee || sw.Reading.Carrier == CombatCarrier.Aura;
                a.BaitAt = instantHost ? new double2(-1.5, -0.5) : a.MainAt + new double2(-1.0, 0.0);
                a.BaitHp = instantHost ? Mathf.Max(0.1f, sw.Damage) + 0.05f : 0.1f;
                a.Bait = instantHost ? -1 : k.Spawn(Hostile(a.BaitAt, a.BaitHp, 0.4f, -1));
                a.BaitStep = Mathf.RoundToInt(BaitAtSeconds / Dt);
                // 慢出手（重炮冷却 + 瞄准 + 22 m/s 飞行）2 秒窗口里一发都打不到碎靶（实测 fw_reclaim 两例全程 0 击杀）：窗口按出手节奏放宽，上限 6 秒
                float cadence = Mathf.Max(0f, sw.Cooldown) + Mathf.Max(0f, sw.AimSeconds) + (sw.ProjectileSpeed > 0f ? (float)math.length(a.BaitAt) / sw.ProjectileSpeed : 0f) + 0.5f;
                a.BaitHold = Mathf.Clamp(Mathf.CeilToInt(cadence / Dt), 2 * SwitchSteps, 6 * SwitchSteps);
                if (ProgramWritesPort(sp, PortCode("ent.wreck")))
                {
                    // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件：击杀即拆的回收（evt.kill → ent.wreck）要击杀点旁边有残骸——碎靶不留残骸，场上那块世界源残骸可能已被同程序的腐蚀 / 熔断拆掉；
                    // 碎靶旁边放一块耐久很高的残骸（按端口，回收只拆 1 块）
                    k.TryAddWreck(new EmgWreck { Pos = a.BaitAt + new double2(0.0, -0.9), Radius = 0.4f, Hp = 1e6f, MaxHp = 1e6f, Until = 1e9 }, out _);
                }
            }
            // 条件按端口：读过热 / 冷却完成 → 开局积热贴着过热阈值；读阵亡 → 6.5 秒判死（FG19A 第 1.8 节）
            a.Preheat = ProgramReads(sp, EmgEventKind.Overheat) || ProgramReads(sp, EmgEventKind.Cooled);
            if (ProgramReads(sp, EmgEventKind.Cooled))
            {
                // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）读冷却完成：开局就过热时头 4 秒全在过热里、停火后程序别的指令一次都没出手（实测 #15 / #313）——先正常打 3 秒，第 3～5 秒补积热到阈值下沿（下一发过热），然后停火散热
                a.PreheatFrom = fixedChassis ? 0 : Mathf.RoundToInt(CoolHeatAtSeconds / Dt); // 定点底盘追不上被击退的目标、往往只有开局那一发（#141 实测），从开局补
                a.PreheatTo = Mathf.RoundToInt(CoolCeaseSeconds / Dt);
                // 无人机舱停火后积热原地钉在 2 倍阈值 20 秒（#266 实测），能走的母机后撤到 14 米外；EMG4-FUZZ-01（#313 / #410）：自己铺的火 / 熔渣在脚下持续注热，能走的一律后撤（玩家把过热的机器撤离火场）
                a.CoolRetreat = !fixedChassis;
            }
            else if (a.Preheat && w.OverheatAt > 0f)
            {
                k.SetWeaponState(a.Me, w.OverheatAt - 0.01f, false, 0, 0);
            }
            if (c.Uplinked && ProgramReads(sp, EmgEventKind.Uplink))
            {
                // EMG2-FLD-01：读“接入”的程序（N15 接入冲击）——正式路径在信号接入时由热更层注入 evt.uplink（CombatSites.OnUplinkChanged），用例开局按同一入口注入一次（FGR-EMG-220 先造条件）
                k.InjectEmgUnitEvent(EmgEventKind.Uplink, a.Me, 0);
            }
            if (ProgramReads(sp, EmgEventKind.Hurt))
            {
                // EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）先造条件：读“受击”的程序要有敌人打本机——随机敌人多是原地不动、射程够不着本机（实测静默派 9 个敌人全程 0 次受击）：
                // 本机身后侧放一个 5 米射程、每秒 1 点伤害的敌人（离本机比离受损友军近，按“离自己最近”挑目标先打本机）
                int shooter = a.Site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 5f, Damage = 1f, Cooldown = 1f });
                k.Spawn(Hostile(new double2(-1.2, -2.0), 5000f, 0.5f, shooter));
            }
            if (ProgramReads(sp, EmgEventKind.Death))
            {
                a.KillStep = Mathf.RoundToInt(Mathf.Min(DeathAtSeconds, c.Seconds - 1f) / Dt);
            }
            // 修复出口（维修束）只对己方出手：玩家对受损友军下“攻击”命令 = 修它（FGR-BASE-020 玩家显式命令；与 RepairFire 同一入口）
            bool repair = repairCarrier;
            int firstTarget = repair ? ally : a.HijackBait >= 0 ? a.HijackBait : a.Target;
            k.IssueCommand(a.Me, CombatCommandKind.Attack, repair ? allyAt : a.HijackBait >= 0 ? a.MainAt + new double2(-1.0, 0.6) : a.MainAt, firstTarget, 0.5f, a.Engage, 0.5f, false);
            SpawnMates(a, c, wi, fixedChassis, repair ? ally : a.Target, repair ? allyAt : a.MainAt); // EMG4-FUZZ-01：协同用例的僚机（同装配、同命令）
            if (ProgramReads(sp, EmgEventKind.Cooled) && a.CeaseStep < 0)
            {
                // 停火散热（玩家撤下过热的机器）：排在碎靶窗口之后。EMG4-FUZZ-01（#353 实测）：重炮出手周期 4 秒（1、5、9 秒），第 5 秒那发（瞄准 1 秒、第 6 秒出膛）正好撞上停火、一直过不了阈值——重炮宽限瞄准时长 + 0.25 秒（按武器模式，不按内容 ID）
                a.CeaseStep = a.BaitStep >= 0 ? a.BaitStep + a.BaitHold + SwitchSteps : Mathf.RoundToInt((CoolCeaseSeconds + (sw.Mode == CombatWeaponMode.Cannon ? sw.AimSeconds + 0.25f : 0f)) / Dt);
            }
            if (a.Mates.Count > 0 && ProgramReads(sp, EmgEventKind.Seize))
            {
                // 先造条件（按端口）：读“夺控”的协同程序——开打 3 秒后主目标夺控一台僚机 2 秒（内核正式入口；正式调用方随 FG11-BOSS-01 / FG12-EVT-03 落地，DEBT-EMG4FW01-01）
                a.SeizeUnit = a.Mates[0];
                a.SeizeStep = Mathf.RoundToInt(SeizeAtSeconds / Dt);
            }
            if (c.RuleId != null)
            {
                a.Rule = SetupRuleScene(a, c.RuleId); // EMG4-FUZZ-01：规则行定向场景（在远离本机交战区的地方铺两侧条件）
            }
            // 召唤载体读“终结”：无人机 / 桩每次母机开火都续命，只在停火后到期——先打 CeaseAtSeconds 秒再撤销命令（玩家停火），让它们到期（FGR-EMG-220 先造条件）
            if (c.Carrier == FirmwareCarrier.Summon && ProgramReads(sp, EmgEventKind.Expire))
            {
                a.CeaseStep = Mathf.RoundToInt(CeaseAtSeconds / Dt);
                if (a.BaitStep >= 0)
                {
                    // 同时读“击杀”（改令打碎靶 → 2 秒后改回主目标）：停火排在改回之后，否则改回主目标那一下又开了火、桩一直续命不到期（EMG3-FUSE-01 接力 #62）
                    a.CeaseStep = a.BaitStep + a.BaitHold + SwitchSteps;
                }
            }
            return a;
        }

        private static bool ProgramWrites(EmgProgramResult r, EmgEventKind e)
        {
            ushort port = (ushort)(EmgConst.EventPortBase + (int)e);
            return r != null && r.KernelInstrs.Any(x => x.Port == port && x.Role > EmgConst.RoleLastRead);
        }

        private static bool ProgramReads(EmgProgramResult r, EmgEventKind e)
        {
            ushort port = (ushort)(EmgConst.EventPortBase + (int)e);
            return r != null && r.KernelInstrs.Any(x => x.Port == port && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead);
        }

        private static CombatSpawn Hostile(double2 at, float hp, float radius, int weapon) => new CombatSpawn
        {
            ExtKey = 0, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (weapon >= 0 ? CombatUnitFlags.WeaponEnabled : CombatUnitFlags.None),
            Position = at, Home = at, Radius = radius, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(-1f, 0f),
        };

        /// <summary>地点模板的障碍（地形，不进内核快照：读档后由热更层按地点重新下发，与正式流程一样）。</summary>
        private static List<float3> ObstaclesOf(string site)
        {
            var obstacles = new List<float3> { new float3(3.0f, -1.3f, 0.35f), new float3(1.6f, 1.5f, 0.3f) };
            if (site == "fractured_city")
            {
                obstacles.Add(new float3(-2.0f, -1.6f, 0.5f));
                obstacles.Add(new float3(5.5f, 2.2f, 0.6f));
                obstacles.Add(new float3(6.0f, -4.2f, 0.4f));
            }
            return obstacles;
        }

        /// <summary>场地条件源（地点模板决定障碍 / 残骸的多少与摆法）：偏折单位（evt.bounce 世界源）、障碍、残骸、热信号目标、标记与浸湿（与 FgReadingMatrixSelfCheck 条件场景同一造法）。</summary>
        /// <param name="markMain">false = 主目标不预先标记（写 evt.marked 的程序：预先标到 1e9 会把它的标记写端整条盖掉，#197 fw_lockcache；读“被标记”的另两个敌人照常预标）。</param>
        private static void AddWorldSources(Arena a, string site, float deflectRadius = 0.8f, bool markMain = true)
        {
            CombatKernel k = a.K;
            // 偏折单位：场（半径 0.8 米）罩住主目标与热信号目标（离本机最近的两个敌人，无人机按“离自己最近”挑目标），不罩本机，也不罩贴近目标出手的无人机（停在触及 0.96 米 + 目标半径处）——飞行弹体从场外飞进场边才反弹，反弹点在场外，反弹放出的子载荷往回飞不会一出生就被同一个场再弹掉；
            // 展开 / 收起按 SwitchSteps 轮换（Arena.StepOnce）
            CombatSpawn prism = Hostile(new double2(2.7, -0.3), 5000f, 0.5f, -1);
            prism.BehaviorProfile = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = deflectRadius, DeflectReflect = 0.5f, DeflectWarn = 0.5f, DeflectUp = PhaseHold, DeflectGap = PhaseHold });
            a.Prism = k.Spawn(prism);
            k.SetFlag(a.Prism, CombatUnitFlags.DeflectArmed, true);
            k.SetDeflectPhase(a.Prism, 2, PhaseHold);
            a.Obstacles = ObstaclesOf(site);
            k.SetObstacles(a.Obstacles);
            k.TryAddWreck(new EmgWreck { Pos = new double2(4.6, 0.45), Radius = 0.45f, Hp = 1000f, MaxHp = 1000f, Until = 1e9 }, out _);
            // EMG2-MAT-01：阶段 2 斑块层开时，本机脚下一块常驻碎屑（世界源，残骸崩解的产物；碎屑沾染位 28 没有状态效果、不参与反应）——
            // 本机与刚出膛的载荷都在斑块里，读“进场 / 离场”的程序有条件可触发（FGR-EMG-220 先造条件）；斑块层关时注入被拒、场景与原来一致。
            k.SpawnPatch(EmgMatterConst.Debris, double2.zero, 1.5f, 1f, 0f, 0, true);
            if (site == "foundry_outpost")
            {
                k.TryAddWreck(new EmgWreck { Pos = new double2(2.0, -2.2), Radius = 0.5f, Hp = 800f, MaxHp = 800f, Until = 1e9 }, out _);
                k.TryAddWreck(new EmgWreck { Pos = new double2(6.0, 1.4), Radius = 0.6f, Hp = 800f, MaxHp = 800f, Until = 1e9 }, out _);
            }
            int dummy = a.Site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 0 });
            CombatSpawn hot = Hostile(new double2(2.4, -0.9), 5000f, 0.5f, dummy);
            k.SetWeaponState(k.Spawn(hot), 50f, false, 0, 0);
            if (CarrierReadings.TryGetTagEffect("Wet", out uint wet, out _, out _))
            {
                foreach (int id in a.Hostiles.Take(3))
                {
                    if (markMain || id != a.Target)
                    {
                        k.SetMarkedUntil(id, 1e9);
                    }
                    k.ApplyStatus(id, wet, 1e7f, 0f, 0f, 0f, 0);
                }
            }
        }

        // ═════════════════════════════════ 判定 ═════════════════════════════════

        internal sealed class CaseResult
        {
            public FuzzCase Case;
            public readonly Dictionary<int, int> RuleHits = new Dictionary<int, int>();
            /// <summary>EMG4-FUZZ-01：按规则行（fg.TbChannelRule.id）的成立次数。</summary>
            public readonly Dictionary<string, int> RowHits = new Dictionary<string, int>(StringComparer.Ordinal);
            /// <summary>EMG4-FUZZ-01（DEBT-EMG1FUZZ01-02）：内核激活过、但按行为判死的指令（去掉后行为指纹逐项相同）；也计入 <see cref="Dead"/>。</summary>
            public readonly List<string> BehaviorDead = new List<string>();
            public int BehaviorChecked;
            public string Crash;
            public string Stall;
            public string Determinism;
            public string SaveLoad;
            public string Variant;
            public string Backlog;
            public readonly List<string> Dead = new List<string>();
            /// <summary>条件型零反应（FG-GAP-179 口径）：定时在本载体射程内必先命中、读法写明条件的节点及其子节点，单列、不算死指令。</summary>
            public readonly List<string> CondZero = new List<string>();
            /// <summary>出现过、但记为条件型零反应的固件（新颖度报告的“出现例数”照样计入）。</summary>
            public readonly HashSet<string> CondFw = new HashSet<string>();
            public readonly List<string> Overflow = new List<string>();
            public readonly List<string> Capacity = new List<string>();
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
            public int Novelty;
            public bool Plain;
            public int Steps;
            public int Instructions;
            public int OverflowSteps;
            public int LegacySteps;
            public int MergedSteps;
            public double P99Ms;
            public double MaxMs;
            public int MaxPending;
            public long SubSpawned, SpawnLost;
            public long DepthCut;
            public ulong Hash;
            /// <summary>定位用：出手 / 实体化 / 弹体数与武器寿命、主目标距离（只进报告）。</summary>
            public string Diag;
            public readonly long[] Events = new long[EmgConst.EventKinds + 1];
            /// <summary>（固件, 载体, 位置）→ 是否激活过；长批次汇总死指令清单用。</summary>
            public readonly List<(string Fw, FirmwareCarrier Carrier, int Ordinal, bool Active)> Ordinals = new List<(string, FirmwareCarrier, int, bool)>();

            public bool Failed => Crash != null || Stall != null || Determinism != null || SaveLoad != null || Variant != null || Backlog != null || Dead.Count > 0 || Overflow.Count > 0;

            public string Problems => string.Join("；", new[] { Crash, Stall, Determinism, SaveLoad, Variant, Backlog }.Where(s => s != null)
                .Concat(Dead.Select(d => "死指令 " + d)).Concat(Overflow.Take(2).Select(o => "溢出 " + o)));
        }

        /// <summary>一例：主跑（计数开、逐步观察）+ 确定性重跑（计数关、不观察）+ 中途存读档续跑；<paramref name="full"/> 时抽样再跑一次“作业线程 1 + 观察读取 + 空推进帧”的变体。</summary>
        /// <param name="behavior">EMG4-FUZZ-01（DEBT-EMG1FUZZ01-02）：按行为判死指令（内核激活过的每条指令去掉后重跑，行为指纹逐项相同 = 死指令）。缺省 = <paramref name="full"/>。</param>
        internal static CaseResult Evaluate(FuzzCase c, bool full, Pools pools = null, bool variant = false, bool? behavior = null)
        {
            pools ??= Pools.Build();
            bool judge = behavior ?? full;
            var r = new CaseResult { Case = c };
            int steps = Mathf.RoundToInt(c.Seconds / Dt);
            r.Steps = steps;
            try
            {
                using (Arena a = Build(c, true, pools))
                {
                    MainRun(a, r, steps);
                }
                if (r.Crash != null || (!full && !judge))
                {
                    return r;
                }
                ulong print = 0;
                Arena pin;
                using (Arena b = Build(c, false, pools))
                {
                    for (int i = 0; i < steps; i++)
                    {
                        if (ForkInject == 1 && i == steps / 2)
                        {
                            Fork(b.K);
                        }
                        b.StepOnce(i);
                        if (judge && i % PrintEvery == PrintEvery - 1)
                        {
                            print = MixPrint(print, b);
                        }
                    }
                    print = MixPrint(print, b);
                    pin = new Arena { Program = b.Program, W = b.W };
                    ulong h = Hash(b.K);
                    if (full && h != r.Hash)
                    {
                        r.Determinism = $"同种子重跑（计数关、不观察）状态哈希 {h:X16} ≠ 主跑 {r.Hash:X16}";
                    }
                }
                if (judge)
                {
                    JudgeBehavior(c, pools, steps, r, print, pin);
                }
                if (!full)
                {
                    return r;
                }
                r.SaveLoad = SaveLoadRun(c, pools, steps, r.Hash);
                if (variant)
                {
                    r.Variant = VariantRun(c, pools, steps, r.Hash);
                }
            }
            catch (Exception e)
            {
                r.Crash ??= "异常：" + e.GetType().Name + "：" + e.Message;
            }
            return r;
        }

        /// <summary>卡死判定（FGR-EMG-221）：单步墙钟 &gt; 100 ms、内核步数不前进、事件队列连续 2 游戏秒只增不减。主跑与 D 段负向用例共用这一段（负向可注入步耗）。</summary>
        internal static string StallOf(int i, double wallMs, long stepsBefore, long stepsAfter, int pending, ref int prevPending, ref int rise)
        {
            rise = pending > prevPending ? rise + 1 : 0;
            prevPending = pending;
            if (rise >= StallRiseSteps)
            {
                return $"事件队列连续 {rise} 步（2 秒）只增不减（{pending} 条）";
            }
            if (stepsAfter <= stepsBefore)
            {
                return $"第 {i} 步内核步数不前进（{stepsBefore} → {stepsAfter}）";
            }
            return wallMs > StallStepMs ? $"第 {i} 步单步 {wallMs:F1} ms > {StallStepMs} ms" : null;
        }

        private static ulong Hash(CombatKernel k) => k.StateHash() ^ (k.EmgStateHash() * 0x9E3779B97F4A7C15UL);

        /// <summary>EMG4-CLEAN-01（DEBT-EMG4FUZZ01-05）：可控分叉注入口，只给 G 段负向用——1 = 确定性重跑在中点多放一个远处的场、2 = 存读档读回后多放一个，检测器必须判失败。平时 0。</summary>
        internal static int ForkInject;

        private static void Fork(CombatKernel k) => k.SpawnField(EmgFieldConst.PortCorona, 900, 900, 1f, 600f, 0, CombatFaction.Hostile);

        private static void MainRun(Arena a, CaseResult r, int steps)
        {
            CombatKernel k = a.K;
            var ms = new List<double>(steps);
            var sw = new Stopwatch();
            int rise = 0;
            int prevPending = 0;
            float dmgCap = Mathf.Max(1f, a.W.Damage) * CombatSite.Tuning("emg.tree.max_payloads", 16f);
            float heatCap = 2f * Mathf.Max(100f, a.W.OverheatAt) + Mathf.Max(0f, a.W.HeatPerShot);
            int perChainCap = Mathf.RoundToInt(CombatSite.Tuning("emg.tree.max_payloads", 16f));
            var perChain = new Dictionary<int, int>();
            for (int i = 0; i < steps && r.Crash == null && r.Stall == null; i++)
            {
                long before = k.Steps;
                sw.Restart();
                a.StepOnce(i);
                sw.Stop();
                double wall = sw.Elapsed.TotalMilliseconds;
                ms.Add(a.Site.LastKernelMs);
                r.MaxMs = Math.Max(r.MaxMs, wall);
                int pending = k.EmgBusPending;
                r.MaxPending = Math.Max(r.MaxPending, pending);
                r.Stall = StallOf(i, wall, before, k.Steps, pending, ref prevPending, ref rise);
                Scan(a, r, i, dmgCap, heatCap, perChainCap, perChain);
                if (i % PrintEvery == 0 && k.HijackCount > 0)
                {
                    r.Keys.Add("hijack"); // EMG4-FUZZ-01：劫持族的交互键（FG19A 7.2 维度贡献表）
                }
            }
            ms.Sort();
            r.P99Ms = ms.Count > 0 ? ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.99))] : 0;
            EmgBusStats st = k.EmgBusStats;
            r.MaxPending = Math.Max(r.MaxPending, st.MaxPending);
            if (r.MaxPending > EmgConst.BusCapacity / 2)
            {
                r.Backlog = $"事件队列峰值 {r.MaxPending} 条 > 容量 {EmgConst.BusCapacity} 的 50%";
            }
            r.SubSpawned = st.SubSpawned;
            r.SpawnLost = st.SpawnLost;
            r.DepthCut = st.DepthCut;
            r.Hash = Hash(k);
            for (int e = 1; e <= EmgConst.EventKinds; e++)
            {
                r.Events[e] = k.EmgEnqueuedOf((EmgEventKind)e);
                if (r.Events[e] > 0)
                {
                    r.Keys.Add("evt:" + (EmgEventKind)e);
                }
            }
            if (st.SubSpawned > 0) r.Keys.Add("sub");
            if (st.WreckAbsorbs > 0) r.Keys.Add("wreck.absorb");
            if (st.WreckBounces > 0) r.Keys.Add("wreck.bounce");
            if (st.FragSpawned > 0) r.Keys.Add("fragment");
            if (st.Impacts > 0) r.Keys.Add("impact");
            if (st.Pushes > 0) r.Keys.Add("push");
            // 容量（只报告，FGR-EMG-221）
            CombatCounters cc = k.Counters;
            if (cc.ProjectilesRefused > 0) r.Capacity.Add($"弹体池满拒绝 {cc.ProjectilesRefused}");
            if (st.SpawnDeferred > 0) r.Capacity.Add($"子载荷顺延 {st.SpawnDeferred}");
            if (st.DepthCut > 0) r.Capacity.Add($"链深截断 {st.DepthCut}");
            if (st.FanoutCut > 0) r.Capacity.Add($"扇出截断 {st.FanoutCut}");
            if (st.PerHostCut > 0) r.Capacity.Add($"每宿主截断 {st.PerHostCut}");
            if (st.FragLost > 0) r.Capacity.Add($"破片丢失 {st.FragLost}");
            if (st.Lost > 0) r.Capacity.Add($"总线满未入队 {st.Lost}");
            if (st.Merged > 0) r.Capacity.Add($"积压合并 {st.Merged}");
            if (st.SpawnLost > 0) r.Capacity.Add($"运行态 / 待生成满拒绝（stat 4）{st.SpawnLost}");
            JudgeOrdinals(a, r);
            r.Diag = $"出手 {cc.ShotsFired}、实体化 {st.FlightShots}、弹体 {cc.ProjectilesSpawned}、命中 {cc.ProjectilesHit}、到期 {cc.ProjectilesExpired}、反弹 {cc.ShotsReflected}；" +
                     $"武器寿命 {a.W.ProjectileLife:0.##} 秒、弹速 {a.W.ProjectileSpeed:0.#}、主目标距离 {math.length(a.MainAt):0.#} 米";
            bool interaction = st.SubSpawned > 0 || k.ZoneCount > 0 || st.WreckAbsorbs + st.WreckBounces + st.FragSpawned + st.Impacts + st.Pushes > 0
                               || r.Events.Where((v, e) => e != (int)EmgEventKind.Fire && e != (int)EmgEventKind.Hit && v > 0).Any() || cc.ZonesSpawned > 0;
            r.Plain = !interaction;
            foreach (int key in EmgRuleCatalog.Current.Rules.Select(x => x.Key).Distinct())
            {
                int fired = a.K.EmgRuleCountOfKey(key); // EMG2-REACT-02：规则可达分布（FGR-EMG-015 规则四、FGR-EMG-221）
                if (fired > 0)
                {
                    r.RuleHits[key] = fired;
                }
            }
            CollectRows(a, r, st);
        }

        /// <summary>每步扫描：NaN / 无穷（单位、弹体、无人机、区域）→ 崩溃；数值溢出（耐久越界、积热、单发伤害、同链子载荷数）→ 失败。</summary>
        private static void Scan(Arena a, CaseResult r, int step, float dmgCap, float heatCap, int perChainCap, Dictionary<int, int> perChain)
        {
            CombatKernel k = a.K;
            for (int s = 0; s < k.SlotCount; s++)
            {
                CombatUnitView v = k.ViewAt(s);
                if (!Finite(v.Position) || !float.IsFinite(v.Health) || !float.IsFinite(v.Heat))
                {
                    r.Crash = $"第 {step} 步单位 {v.Id} 位置 / 耐久 / 积热出现 NaN 或无穷";
                    return;
                }
                if (v.Health > v.MaxHealth + 0.01f || v.Health < -0.01f)
                {
                    AddOnce(r.Overflow, $"单位 {v.Id} 耐久 {v.Health:F1} 越界（0～{v.MaxHealth:F0}）");
                }
                if (v.Heat > heatCap)
                {
                    AddOnce(r.Overflow, $"单位 {v.Id} 积热 {v.Heat:F1} > 上限 {heatCap:F0}");
                }
            }
            perChain.Clear();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (!k.TryGetProjectile(i, out CombatProjectile p))
                {
                    continue;
                }
                if (!Finite(p.Pos) || !float.IsFinite(p.Vel.x) || !float.IsFinite(p.Vel.y) || !float.IsFinite(p.Damage))
                {
                    r.Crash = $"第 {step} 步弹体 {p.Id} 位置 / 速度 / 伤害出现 NaN 或无穷";
                    return;
                }
                if (p.Owner == a.Me && p.Damage > dmgCap)
                {
                    AddOnce(r.Overflow, $"弹体单发伤害 {p.Damage:F1} > 上限 {dmgCap:F0}（武器伤害 × emg.tree.max_payloads）");
                }
                if ((p.Flags & EmgProjFlags.Sub) != 0 && p.Chain > 0)
                {
                    perChain.TryGetValue(p.Chain, out int n);
                    perChain[p.Chain] = ++n;
                    if (n > perChainCap)
                    {
                        AddOnce(r.Overflow, $"同一条链同时存活的子载荷 {n} > emg.tree.max_payloads {perChainCap}");
                    }
                }
            }
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone d) && !Finite(d.Pos))
                {
                    r.Crash = $"第 {step} 步无人机 {i} 位置出现 NaN 或无穷";
                    return;
                }
            }
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z) && (!Finite(z.Pos) || !float.IsFinite(z.Radius) || !float.IsFinite(z.Dps)))
                {
                    r.Crash = $"第 {step} 步区域 {i} 位置 / 半径 / 伤害出现 NaN 或无穷";
                    return;
                }
            }
        }

        private static bool Finite(double2 v) => double.IsFinite(v.x) && double.IsFinite(v.y);

        private static void AddOnce(List<string> list, string s)
        {
            if (list.Count < 4)
            {
                list.Add(s);
            }
        }

        /// <summary>
        /// 死指令（FGR-EMG-221“零反应”）：按程序序号汇总内核激活计数（本地点全部程序：主程序与刷写变体；合并行按来源记给每个序号）。
        /// 容量溢出（不执行）、本载体不适用（没有声明行）、同节点同键幂等合并（没有独立行）的位置不算死指令，分别计数。
        /// </summary>
        private static void JudgeOrdinals(Arena a, CaseResult r)
        {
            CombatKernel k = a.K;
            EmgProgramResult prog = a.Program;
            var act = new Dictionary<int, long>();
            var withRows = new HashSet<int>();
            for (int j = 0; j < prog.KernelInstrs.Count; j++)
            {
                withRows.Add(prog.KernelInstrs[j].Ordinal);
                if (prog.MergeSources.TryGetValue(j, out List<EmgMergeSource> src))
                {
                    foreach (EmgMergeSource m in src)
                    {
                        withRows.Add(m.Ordinal);
                    }
                }
            }
            int mainBase = -1;
            if (a.W.Program >= 0 && k.TryGetProgram(a.W.Program, out EmgProgram mp) && mp.NodeCount > 0 && k.TryGetEmgNode(mp.FirstNode, out EmgNode mroot))
            {
                mainBase = mroot.FirstInstr;
            }
            for (int pi = 0; pi < k.ProgramCount; pi++)
            {
                if (!k.TryGetProgram(pi, out EmgProgram pr) || pr.NodeCount == 0 || !k.TryGetEmgNode(pr.FirstNode, out EmgNode root))
                {
                    continue;
                }
                for (int j = 0; j < pr.InstrTotal; j++)
                {
                    int idx = root.FirstInstr + j;
                    if (!k.TryGetEmgInstr(idx, out EmgInstr x))
                    {
                        continue;
                    }
                    long n = k.InstrActivations(idx);
                    if (n <= 0)
                    {
                        continue;
                    }
                    r.Keys.Add($"port:{x.Port}");
                    if (pi == a.W.Program && mainBase >= 0 && prog.MergeSources.TryGetValue(idx - mainBase, out List<EmgMergeSource> src))
                    {
                        foreach (EmgMergeSource m in src)
                        {
                            act[m.Ordinal] = act.TryGetValue(m.Ordinal, out long v0) ? v0 + n : n;
                        }
                    }
                    act[x.Ordinal] = act.TryGetValue(x.Ordinal, out long v) ? v + n : n;
                }
            }
            var squadOrds = new HashSet<int>();
            HashSet<int> condNodes = TimerCondNodes(a, r.Case, squadOrds);
            foreach (EmgProgramStep s in prog.Steps)
            {
                r.Instructions++;
                if (s.Overflow)
                {
                    r.OverflowSteps++;
                    continue;
                }
                if (s.Decls.Count == 0)
                {
                    continue;
                }
                if (!withRows.Contains(s.Ordinal))
                {
                    r.MergedSteps++;
                    continue;
                }
                bool on = act.TryGetValue(s.Ordinal, out long c) && c > 0;
                if (!on && (squadOrds.Contains(s.Ordinal) || OnlyOnNodes(prog, s.Ordinal, condNodes)))
                {
                    r.CondFw.Add(s.FirmwareId);
                    r.CondZero.Add($"{s.FirmwareId}（第 {s.Ordinal + 1} 位，{r.Case.Carrier}；{RowsOf(prog, s.Ordinal)}）");
                    continue;
                }
                r.Ordinals.Add((s.FirmwareId, r.Case.Carrier, s.Ordinal, on));
                if (!on)
                {
                    r.Dead.Add($"{s.FirmwareId}（第 {s.Ordinal + 1} 位，{r.Case.Carrier}；{RowsOf(prog, s.Ordinal)}）");
                }
            }
            r.LegacySteps = prog.LegacyOwners.Count;
        }

        /// <summary>
        /// EMG3-FW-02（同 FG-GAP-179 口径）：收听 / 广播 evt.ping 的指令（要有同一中继组里的另一台机器，固定集一例只有一台机器、没有中继）与读 tmp.sync 的指令
        /// （要本机同一拍有 2 条以上节拍指令）在单机用例里本来就成立不了；该固件本载体读法写明“中继组” / “对齐”时，只豁免这条指令自身（<paramref name="ords"/>）
        /// 与它的触发器放出的子节点（及其子孙，<paramref name="set"/>）——不豁免同节点里的其他指令（修复轮：原来整节点豁免，放在根节点时整个程序的死指令都被盖住）。按端口判，不按内容 ID。
        /// </summary>
        private static void SquadCondNodes(Arena a, FuzzCase c, HashSet<int> set, HashSet<int> ords)
        {
            ushort ping = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Ping);
            bool squad = c.Mates > 0 && c.Relay; // EMG4-FUZZ-01：协同用例有同一中继组的僚机，收听 / 广播 evt.ping 能送达，不豁免
            string carrier = c.Carrier.ToString().ToLowerInvariant();
            List<EmgNode> nodes = a.Program.KernelNodes;
            for (int n = 0; n < nodes.Count; n++)
            {
                for (int j = nodes[n].FirstInstr; j < nodes[n].FirstInstr + nodes[n].InstrCount && j < a.Program.KernelInstrs.Count; j++)
                {
                    EmgInstr x = a.Program.KernelInstrs[j];
                    bool listen = !squad && x.Port == ping && (x.Role == EmgConst.RoleTrigger || x.Role > EmgConst.RoleLastRead); // 收听或广播：单机、没有中继的用例里送达不了
                    bool sync = x.Port == EmgTempoConst.PortSync && x.Role == EmgConst.RoleCondition;
                    string fw = listen || sync ? a.Program.Steps.FirstOrDefault(st => st.Ordinal == x.Ordinal)?.FirmwareId : null;
                    string reading = fw != null ? GameText.Get($"firmware.{fw}.read.{carrier}") + GameText.Get($"firmware.{fw}.desc") : string.Empty;
                    if ((listen && reading.Contains("中继组")) || (sync && reading.Contains("对齐")))
                    {
                        ords.Add(x.Ordinal);
                        for (int ch = 0; ch < a.Program.Nodes.Count && ch < nodes.Count; ch++)
                        {
                            if (a.Program.Nodes[ch].TriggerOrdinal == x.Ordinal)
                            {
                                set.Add(ch); // 这条触发器放出的子节点：脉冲 / 对齐拍不来就不放，其上指令同样成立不了
                            }
                        }
                    }
                }
            }
        }

        private static HashSet<int> Grow(List<EmgNode> nodes, HashSet<int> set)
        {
            for (bool grew = set.Count > 0; grew;)
            {
                grew = false;
                for (int n = 0; n < nodes.Count; n++)
                {
                    if (!set.Contains(n) && nodes[n].Parent >= 0 && set.Contains(nodes[n].Parent))
                    {
                        grew |= set.Add(n);
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// FG-GAP-179 口径（EMG1-E2E-01 定，待用户复核）在固定集上的判定：读“定时”的节点，若载体最远打击距离 ÷ 实体化弹速 &lt; 定时秒数（哨戒桩 / 无人机按牵引绳，维修束按射程），
        /// 子弹在射程内必先命中、定时只在打空时才到点；该固件本载体读法写明“先命中则不空爆”时，这个节点及其子节点返回，其上全程不激活的指令记条件型零反应。
        /// 射程够的远射载体不走这里（<see cref="Build"/> 把目标放远造条件）；读法没写明条件的照旧算死指令。按端口 + 载体参数判，不按内容 ID。
        /// </summary>
        private static HashSet<int> TimerCondNodes(Arena a, FuzzCase c, HashSet<int> squadOrds)
        {
            var set = new HashSet<int>();
            CombatWeapon w = a.W;
            bool summon = c.Carrier == FirmwareCarrier.Summon;
            SquadCondNodes(a, c, set, squadOrds);
            if ((!summon && w.HasOutput != CombatConst.OutputRepair) || !(w.ProjectileSpeed > 0f) || a.Program.KernelNodes.Count == 0)
            {
                return Grow(a.Program.KernelNodes, set);
            }
            float reach = summon ? (w.Reading.DroneLeash > 0f ? w.Reading.DroneLeash : 10f) + 1f : w.Range + 1.2f;
            float flight = reach / w.ProjectileSpeed;
            ushort port = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Timer);
            string carrier = c.Carrier.ToString().ToLowerInvariant();
            List<EmgNode> nodes = a.Program.KernelNodes;
            for (int n = 0; n < nodes.Count; n++)
            {
                for (int j = nodes[n].FirstInstr; j < nodes[n].FirstInstr + nodes[n].InstrCount && j < a.Program.KernelInstrs.Count; j++)
                {
                    EmgInstr x = a.Program.KernelInstrs[j];
                    if (x.Port != port || x.Role < EmgConst.RoleTrigger || x.Role > EmgConst.RoleLastRead || !(x.Param2 > flight))
                    {
                        continue;
                    }
                    string fw = a.Program.Steps.FirstOrDefault(st => st.Ordinal == x.Ordinal)?.FirmwareId;
                    if (fw != null && GameText.Get($"firmware.{fw}.read.{carrier}").Contains("先命中则不空爆"))
                    {
                        set.Add(n);
                    }
                }
            }
            for (bool grew = set.Count > 0; grew;)
            {
                grew = false;
                for (int n = 0; n < nodes.Count; n++)
                {
                    if (!set.Contains(n) && nodes[n].Parent >= 0 && set.Contains(nodes[n].Parent))
                    {
                        set.Add(n);
                        grew = true;
                    }
                }
            }
            return set;
        }

        /// <summary>这个指令位置的所有行都落在给定节点上（至少 1 行）。</summary>
        private static bool OnlyOnNodes(EmgProgramResult prog, int ordinal, HashSet<int> nodes)
        {
            if (nodes.Count == 0)
            {
                return false;
            }
            int rows = 0;
            for (int n = 0; n < prog.KernelNodes.Count; n++)
            {
                EmgNode node = prog.KernelNodes[n];
                for (int j = node.FirstInstr; j < node.FirstInstr + node.InstrCount && j < prog.KernelInstrs.Count; j++)
                {
                    if (prog.KernelInstrs[j].Ordinal != ordinal)
                    {
                        continue;
                    }
                    if (!nodes.Contains(n))
                    {
                        return false;
                    }
                    rows++;
                }
            }
            return rows > 0;
        }

        /// <summary>死指令的行（报告给策划定位用）：节点 / 端口 / 角色，例如“n1 evt.timer 触发”。</summary>
        private static string RowsOf(EmgProgramResult prog, int ordinal)
        {
            var parts = new List<string>();
            for (int n = 0; n < prog.KernelNodes.Count; n++)
            {
                EmgNode node = prog.KernelNodes[n];
                for (int j = node.FirstInstr; j < node.FirstInstr + node.InstrCount && j < prog.KernelInstrs.Count; j++)
                {
                    EmgInstr x = prog.KernelInstrs[j];
                    if (x.Ordinal == ordinal)
                    {
                        string port = EmgCatalog.Current.PortIdByCode.TryGetValue(x.Port, out string id) ? id : x.Port.ToString(CultureInfo.InvariantCulture);
                        parts.Add($"n{n} {port} 角色{x.Role}");
                    }
                }
            }
            return parts.Count == 0 ? "无行" : string.Join("，", parts);
        }

        /// <summary>存读档（FGR-EMG-221）：同一用例跑到一半取正式地点快照（CombatSite.Snapshot），在新地点的内核层读回（CombatKernel.Load，内核层快照往返），两边续跑到底比终局状态哈希。
        /// 地点还原路径（CombatSite.TryRestore 的机器登记表对账）不在这里测，由 FgReadingMatrixSelfCheck.AddonSaveLoad 与存档自检承接。</summary>

        private static string SaveLoadRun(FuzzCase c, Pools pools, int steps, ulong expected)
        {
            int half = steps / 2;
            using Arena a = Build(c, false, pools);
            for (int i = 0; i < half; i++)
            {
                a.StepOnce(i);
            }
            CombatSiteRecord rec = a.Site.Snapshot();
            var b = new Arena { Site = new CombatSite("fuzz_" + c.Site, ConfigFor(c)), Me = a.Me, Target = a.Target, Prism = a.Prism, HijackBait = a.HijackBait, KillStep = a.KillStep, CeaseStep = a.CeaseStep, T = a.T, BaitStep = a.BaitStep, Bait = a.Bait, BaitAt = a.BaitAt, MainAt = a.MainAt, Engage = a.Engage, W = a.W, Rule = a.Rule, Mates = a.Mates, Preheat = a.Preheat, PreheatFrom = a.PreheatFrom, PreheatTo = a.PreheatTo, SeizeStep = a.SeizeStep, SeizeUnit = a.SeizeUnit, BaitHold = a.BaitHold, BaitHp = a.BaitHp, CoolRetreat = a.CoolRetreat };
            try
            {
                // 内核层读回地点快照（与 FgReadingMatrixSelfCheck.AddonSaveLoad 同一做法）：CombatSite.TryRestore 会按机器登记表对账，
                // 本场地的机器不在战役登记表里（外部键 0）会被当孤儿移除——那是战役对账，不是快照往返要测的东西。新地点的构造已按正式流程登记反应 / 子形态 / 实体档案。
                if (a.Rule?.Melt != null)
                {
                    b.K.SetMeltGround(a.Rule.Melt); // 可熔地面是派生量（热更层按生成规则写），读档沿用当前内核的：读档前先按地点下发（与正式载入同序）
                }
                CombatLoadResult lr = b.K.Load(Convert.FromBase64String(rec.Payload));
                if (lr != CombatLoadResult.Ok)
                {
                    return "中途读档失败：" + lr;
                }
                if (a.Obstacles.Count > 0)
                {
                    b.K.SetObstacles(a.Obstacles); // 地形不进快照：读档后按地点重新下发（与 WorldSimulation 载入地点同一做法）
                }
                if (b.K.StateHash() != a.K.StateHash())
                {
                    return "读档后状态哈希与存档时不同";
                }
                if (ForkInject == 2)
                {
                    Fork(b.K);
                }
                for (int i = half; i < steps; i++)
                {
                    b.StepOnce(i);
                }
                ulong h = Hash(b.K);
                return h == expected ? null : $"中途存读档续跑终局哈希 {h:X16} ≠ 连续跑 {expected:X16}（{SaveLoadDiverge(c, pools, steps)}）";
            }
            finally
            {
                b.Dispose();
            }
        }

        /// <summary>FGT-EMG-042 抽样变体：每帧读全部单位 / 弹体视图（观察）与暂停帧（不推进）、按帧分批推进，终局哈希必须与主跑相同。
        /// “不同线程数”维度当前不适用：内核每步用 job.Run() 在调用线程同步执行（CombatKernel 不 Schedule），工作线程数对结果没有影响；这里仍把 JobWorkerCount 改为 1 只为在引入并行作业后自动生效（阶段 4 再判）。
        /// 内核是固定 60 Hz 步，倍速只改变每帧推进几步（0.5x 隔帧 1 步、3x 每帧至多 2 步），所以“按帧分批推进”与连续推进的步序相同。</summary>
        private static string VariantRun(FuzzCase c, Pools pools, int steps, ulong expected)
        {
            int workers = JobsUtility.JobWorkerCount;
            try
            {
                JobsUtility.JobWorkerCount = 1;
                using Arena a = Build(c, false, pools);
                int[] pattern = { 0, 1, 0, 2, 2, 1, 0, 1 }; // 一帧推进几步：暂停帧 0、1x 1、3x 最坏 2
                int i = 0;
                for (int f = 0; i < steps; f++)
                {
                    int n = pattern[f % pattern.Length];
                    for (int j = 0; j < n && i < steps; j++, i++)
                    {
                        a.StepOnce(i);
                    }
                    for (int s = 0; s < a.K.SlotCount; s++)
                    {
                        _ = a.K.ViewAt(s);
                    }
                    for (int s = 0; s < a.K.ProjectileCount; s++)
                    {
                        a.K.TryGetProjectile(s, out _);
                    }
                }
                ulong h = Hash(a.K);
                return h == expected ? null : $"观察 + 暂停帧 + 分批推进变体终局哈希 {h:X16} ≠ 主跑 {expected:X16}";
            }
            finally
            {
                JobsUtility.JobWorkerCount = workers;
            }
        }

        // ═════════════════════════════════ A 覆盖 ═════════════════════════════════

        private static void CheckCoverage(Pools pools)
        {
            Line("  · A. 用例生成：可获得固件池、13 个主作战组件、4 种底盘、地点模板、阵营；N09～N16 的状态");
            int addonInPool = AddonStage1.Count(pools.Firmware.Contains);
            // EMG3-FW-02：阶段 3 生效后 12 条新固件都在抽取池里（可获得且有机器实现），随机例会抽到它们（新颖度见报告“阶段 3 新固件”节）
            var s3Missing = EmgCatalog.EffectiveStage >= 3 ? Stage3Firmware.Where(f => !pools.Firmware.Contains(f)).ToList() : new List<string>();
            Expect(s3Missing.Count == 0, $"阶段 3 新固件 {Stage3Firmware.Length - s3Missing.Count} / {Stage3Firmware.Length} 条在抽取池里（emg.stage = {EmgCatalog.EffectiveStage}）{Detail(s3Missing)}");
            Expect(pools.Comps.Count == 13 && pools.Factions.Count >= 2 && addonInPool == AddonStage1.Length && pools.Firmware.Contains("fw_enterfuse") && pools.Firmware.Contains("fw_uplinkpulse")
                   && pools.Firmware.Count >= 44 && pools.PierceProvider != null,
                $"抽取池：固件 {pools.Firmware.Count} 条（可获得且有机器实现，含混合固件 {pools.Firmware.Count(FirmwareKinds.IsMixed)} 条；阶段 1 生效的 N09～N13、N16 共 {addonInPool} 条在池里；阶段 2 端口已接通的 N14 在、fld.push 随 EMG2-FLD-01 接通后 N15 也在），" +
                $"主作战组件 {pools.Comps.Count} 个，阵营 {pools.Factions.Count} 个（{string.Join("、", pools.Factions)}），穿透条件提供者按端口查到 {pools.PierceProvider}");
            // FG-GAP-177：反弹 / 穿透引信在声明 carriers 不含的载体上——不编译 + 预览写“本载体不适用” + 读法如实写明
            var na = new List<string>();
            var bad = new List<string>();
            foreach (string fw in new[] { "fw_bouncefuse", "fw_piercefuse" })
            {
                foreach ((FirmwareCarrier carrier, string _) in FgReadingMatrixSelfCheck.AddonCarriers)
                {
                    EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier);
                    if (r.Steps.Any(s => s.Decls.Count > 0))
                    {
                        continue;
                    }
                    string reading = FirmwareKinds.Reading(fw, carrier) ?? string.Empty;
                    bool ok = r.Notes.Any(n => n.Key == EmgProgramCompiler.NoteCarrierNa) && r.KernelInstrs.Count == 0 && GameText.Has(EmgProgramCompiler.NoteCarrierNa)
                              && (reading.Contains("不适用") || reading.Contains("Not applicable"));
                    (ok ? na : bad).Add($"{carrier}/{fw}");
                }
            }
            Expect(na.Count == 5 && bad.Count == 0,
                $"反弹 / 穿透引信在声明不适用的 {na.Count} 个载体上（{string.Join("、", na)}）不编译、预览写“本载体不适用”、读法如实写明{Detail(bad)}");
        }

        // ═════════════════════════════════ C 固定 64 例 ═════════════════════════════════

        private static List<string> CheckFixedCases(Pools pools, out List<CaseResult> fixedResults)
        {
            Line($"  · C. 固定用例集 {FixedCount} 例（种子 {FixedSeed}，每例 {CaseSeconds} 游戏秒、1 倍速 {Mathf.RoundToInt(CaseSeconds / Dt)} 步；真实编译器 + CombatSite + 内核，无画面）");
            var sw = Stopwatch.StartNew();
            List<FuzzCase> cases = FixedCases(pools, FixedSeed, FixedCount);
            var results = new List<CaseResult>(cases.Count);
            foreach (FuzzCase c in cases)
            {
                results.Add(Evaluate(c, full: true, pools, variant: c.Index % 8 == 3));
            }
            sw.Stop();
            fixedResults = results;
            // 覆盖
            var directed = cases.Where(c => c.Tag != null && c.Tag.StartsWith("定向", StringComparison.Ordinal)).ToList();
            var dirBad = new List<string>();
            foreach (string fw in AddonStage1)
            {
                var mine = directed.Where(c => c.Program.Contains(fw)).ToList();
                var carriers = FgReadingMatrixSelfCheck.AddonCarriers.Where(t => EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, t.Carrier).Steps.Any(s => s.Decls.Count > 0))
                    .Select(t => t.Carrier).ToList();
                bool allCarriers = carriers.All(car => mine.Any(c => c.Carrier == car));
                bool firstLast = mine.Any(c => c.Tag.Contains("首位")) && mine.Any(c => c.Tag.Contains("末位"));
                if (!allCarriers || !firstLast)
                {
                    dirBad.Add($"{fw}（载体 {mine.Select(c => c.Carrier).Distinct().Count()}/{carriers.Count}，首末 {firstLast}）");
                }
            }
            Expect(cases.Count == FixedCount && dirBad.Count == 0,
                $"固定集 {cases.Count} 例：N09～N13、N16 定向 {directed.Count} 例——6 条各在声明适用的每个载体上至少 1 例、首位 / 末位各至少 1 例（关闭 DEBT-EMG1FW02-01）{Detail(dirBad)}");
            Expect(pools.Comps.All(cp => cases.Any(c => c.Comp == cp)) && ChassisIds.All(ch => cases.Any(c => c.Chassis == ch)) && SiteTemplates.All(s => cases.Any(c => c.Site == s))
                   && cases.Select(c => c.Faction).Distinct().Count() >= Math.Min(3, pools.Factions.Count) && cases.Any(c => c.Uplinked) && cases.Any(c => !c.Uplinked)
                   && Enumerable.Range(0, 5).All(n => cases.Any(c => c.Program.Length == n)) && cases.Any(c => c.Func != null) && cases.Min(c => c.Enemies) == 3 && cases.Max(c => c.Enemies) == 12
                   && cases.Any(c => c.Program.Any(FirmwareKinds.IsMixed)),
                $"维度覆盖：13 个主作战组件、4 种底盘、3 个地点模板、{cases.Select(c => c.Faction).Distinct().Count()} 个阵营、有 / 无接入、程序长度 0～4、带功能组件、敌人 3～12、含混合固件");
            var crash = results.Where(r => r.Crash != null).ToList();
            var stall = results.Where(r => r.Stall != null).ToList();
            var dead = results.Where(r => r.Dead.Count > 0).ToList();
            var det = results.Where(r => r.Determinism != null).ToList();
            var sl = results.Where(r => r.SaveLoad != null).ToList();
            var vr = results.Where(r => r.Variant != null).ToList();
            var bl = results.Where(r => r.Backlog != null).ToList();
            var of = results.Where(r => r.Overflow.Count > 0).ToList();
            Expect(crash.Count == 0, $"零崩溃：{results.Count} 例无异常、单位 / 弹体 / 无人机 / 区域没有 NaN 或无穷{Detail(crash.Select(r => r.Case.Text + "：" + r.Crash))}");
            Expect(stall.Count == 0, $"零卡死：单步墙钟最大 {results.Max(r => r.MaxMs):F2} ms（≤ {StallStepMs}）、事件队列没有连续 2 秒只增不减、时间都在前进{Detail(stall.Select(r => r.Case.Text + "：" + r.Stall))}");
            int ords = results.Sum(r => r.Ordinals.Count);
            Expect(dead.Count == 0 && ords > 0,
                $"零死指令：{ords} 个指令位置全程激活次数 > 0（内核按指令行计数，合并行记给每个来源），其中 {results.Sum(r => r.BehaviorChecked)} 个另按行为判（去掉后重跑行为指纹必须不同，DEBT-EMG1FUZZ01-02）、按行为判死 {results.Sum(r => r.BehaviorDead.Count)}；容量溢出 {results.Sum(r => r.OverflowSteps)}、幂等合并 {results.Sum(r => r.MergedSteps)}、" +
                $"旧轨（stageGate &gt; 1）{results.Sum(r => r.LegacySteps)} 条不计；条件型零反应（FG-GAP-179 口径：定时在本载体射程内必先命中、读法写明“先命中则不空爆”）{results.Sum(r => r.CondZero.Count)} 条单列"
                + $"{(results.Any(r => r.CondZero.Count > 0) ? "（" + string.Join("；", results.Where(r => r.CondZero.Count > 0).Select(r => $"#{r.Case.Index} {string.Join("，", r.CondZero)}")) + "）" : string.Empty)}"
                + $"{Detail(dead.Select(r => r.Case.Text + "：" + string.Join("，", r.Dead)))}");
            Expect(det.Count == 0, $"确定性：{results.Count} 例同种子重跑（计数关、不观察）终局状态哈希都与主跑（计数开、逐步观察）相同——激活计数不改状态{Detail(det.Select(r => r.Case.Text + "：" + r.Determinism))}");
            Expect(sl.Count == 0, $"存读档：{results.Count} 例在第 {Mathf.RoundToInt(CaseSeconds / Dt) / 2} 步取地点快照、内核层读回（快照往返），续跑终局哈希与连续跑相同{Detail(sl.Select(r => r.Case.Text + "：" + r.SaveLoad))}");
            int variants = results.Count(r => r.Case.Index % 8 == 3);
            Expect(vr.Count == 0 && variants >= 8,
                $"FGT-EMG-042 抽样 {variants} 例：作业工作线程 1 + 每帧观察读取（全部单位 / 弹体视图）+ 暂停帧 / 每帧 2 步（3x 最坏）分批推进，终局哈希与主跑相同{Detail(vr.Select(r => r.Case.Text + "：" + r.Variant))}");
            Expect(bl.Count == 0, $"积压：事件队列峰值最大 {results.Max(r => r.MaxPending)} 条 ≤ 容量 {EmgConst.BusCapacity} 的 50%{Detail(bl.Select(r => r.Case.Text + "：" + r.Backlog))}");
            Expect(of.Count == 0, $"数值溢出：耐久不越界、积热 ≤ 2 × 过热阈值 + 单发积热、本机弹体单发伤害 ≤ 武器伤害 × emg.tree.max_payloads、同链子载荷 ≤ emg.tree.max_payloads" +
                                  $"{Detail(of.Select(r => r.Case.Text + "：" + string.Join("，", r.Overflow)))}");
            // 只报告的项
            int plain = results.Count(r => r.Plain);
            var capHits = results.Where(r => r.Capacity.Count > 0).ToList();
            Line($"    · 平庸（只报告）：{plain} 例除基础伤害外没有任何交互；容量（只报告）：{capHits.Count} 例命中上限或截断——" +
                 string.Join("；", capHits.Take(6).Select(r => $"#{r.Case.Index} {string.Join("、", r.Capacity)}")));
            // EMGR-TRIG-01（DEBT-EMGRTRIG01-01）：节拍锚点按出手保留后盯运行态满拒绝（stat 4，与元程序循环 / 延后、待生成队列同一计数），只报告
            Line($"    · stat 4（运行态 / 待生成队列满拒绝，只报告）：{results.Count} 例合计 {results.Sum(r => r.SpawnLost)}");
            // EMG2-REACT-02（FGT-EMG-070、FGR-EMG-015 规则四）：16 条具名反应在固定用例集里每条至少触发一次；随机例打不到的由定向场景补（配料要两侧凑齐，随机装配不保证）
            var randomKeys = new HashSet<int>(results.SelectMany(x => x.RuleHits.Keys));
            Dictionary<string, int> directed16 = FgEmgReact2SelfCheck.FireEachNamedOnce();
            var missing16 = directed16.Where(kv => kv.Value < 1 && !randomKeys.Contains(NamedReactionCatalog.StableKey(kv.Key))).Select(kv => kv.Key).ToList();
            int random16 = directed16.Keys.Count(id => randomKeys.Contains(NamedReactionCatalog.StableKey(id)));
            Expect(directed16.Count == 16 && missing16.Count == 0,
                $"规则可达（规则四）：16 条具名反应每条至少触发过一次（随机例打到 {random16} 条、定向场景补全；定向 {string.Join(" ", directed16.Select(kv => kv.Key.Replace("reaction_", string.Empty) + ":" + kv.Value))}）；" +
                $"规则分布：随机例共触发 {randomKeys.Count} / {EmgRuleCatalog.Current.Rules.Select(x => x.Key).Distinct().Count()} 个稳定键{Detail(missing16)}");
            CheckResident(cases, results, sw.Elapsed.TotalSeconds);
            Line($"    · 本段 {results.Count} 例共 {results.Sum(r => r.Steps)} 步 × 3 遍（主跑 / 确定性 / 存读档）+ {variants} 例变体 + 按行为判死重跑 {results.Sum(r => r.BehaviorChecked)} 遍，墙钟 {sw.Elapsed.TotalSeconds:F1} 秒；子载荷生成 {results.Sum(r => r.SubSpawned)} 份，" +
                 $"链深截断 {results.Sum(r => r.DepthCut)} 次；单例内核步 p99 最大 {results.Max(r => r.P99Ms):F3} ms");
            string path = WriteReport("_emg-fuzz-fixed.md", $"固定集 seed={FixedSeed} count={FixedCount}", results);
            Line($"    · 报告（FGR-EMG-222：新颖度前 20、死指令清单、帧耗前 20、事件分布）：{path ?? "（没找到证据目录，未写）"}");
            // DEBT-EMG1KRN01-01（阶段 1 出口实测单地点程序行数）：固定集全部装配（远多于一局里同一地点会换的装配）登记进同一个地点内核的程序表，看离上限多远。
            using (var one = new CombatKernel(CombatConfig.Default, 8))
            {
                int rejected = 0;
                foreach (CaseResult r in results)
                {
                    EmgProgramResult pr = EmgProgramCompiler.Compile(r.Case.Uplinked ? r.Case.Uplink : Array.Empty<string>(), r.Case.Local, r.Case.Carrier);
                    rejected += !pr.IsEmpty && CombatSite.RegisterProgram(one, pr) < 0 ? 1 : 0;
                }
                Expect(rejected == 0 && one.ProgramCount * 4 <= EmgConst.MaxPrograms,
                    $"单地点程序表实测（DEBT-EMG1KRN01-01）：固定集 {results.Count} 套装配登记进同一地点 = {one.ProgramCount} 行（完整字节去重；上限 {EmgConst.MaxPrograms}，不到四分之一，阶段 1 不需要压实），被拒 {rejected}");
            }
            var observed = new List<string>();
            for (int e = 1; e <= EmgConst.EventKinds; e++)
            {
                if (results.Any(r => r.Events[e] > 0))
                {
                    observed.Add($"evt.{((EmgEventKind)e).ToString().ToLowerInvariant()}");
                }
            }
            return observed;
        }

        // ═════════════════════════════════ B 有源与缺口 ═════════════════════════════════

        /// <summary>
        /// FG-GAP-183（EMG1-E2E-01 修复轮，FG19 第 12 节规则三出口口径）：阶段 1 已交付生产者的事件端口——总线按需入队（FGR-EMG-062），没有读者的程序时固定集观察不到，
        /// 但生产点在、行为由所列自检断言。出口模式里它们算“有写者引用”，读者落表前记缺口、不判失败。
        /// </summary>
        internal static readonly Dictionary<string, string> EventProducers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["evt.split"] = "内核触发器放出子载荷（CombatLogic.Trigger；FgEmgTriggerSelfCheck / FgEmgBusSelfCheck）",
            ["evt.enter"] = "单位 / 载荷进入物质斑块（CombatLogic.Matter EmgMatterEdges / EmgProjMatter；FgEmgMatterSelfCheck G / I）",
            ["evt.leave"] = "单位 / 载荷离开物质斑块（CombatLogic.Matter；FgEmgMatterSelfCheck G）",
            ["evt.kill"] = "内核伤害归属阵亡（FgEmgBusSelfCheck）",
            ["evt.impact"] = "内核撞击 / 壁撞（CombatLogic.Entity；FgEmgEntitySelfCheck）",
            ["evt.push"] = "内核击退 / 牵引位移（FgEmgEntitySelfCheck）",
            ["evt.react"] = "可靠计数结算（CombatLogic.Chain；FgEmgChainSelfCheck B2）",
            ["evt.absorb"] = "护盾 / 残骸 / 弹体吸收（CombatLogic / Entity / Grammar；FgEmgEntitySelfCheck）",
            ["evt.uplink"] = "信号接入时热更层注入（CombatSites.OnUplinkChanged；FgEmgAddonFirmwareSelfCheck 接入冲击）",
            ["evt.unlink"] = "信号离开时热更层注入（CombatSites.OnUplinkChanged；FgEmgFlashSelfCheck）",
            ["evt.pickup"] = "机器拾取时热更层注入（CargoService → CombatSites.InjectEmgPickup；FgEmgBusSelfCheck）",
            ["evt.phase"] = "单位进 / 出相态时内核发（CombatLogic.Phase EnterPhase / ExitPhase；EMG2-PHS-01，FgEmgPhaseSelfCheck）", // EMG3-FW-01：阶段 2 出口核对暴露（原被 evt.react 未接通挡在阶段 1 出口）
            // EMG4-E2E-02（FG-GAP-210 阶段 4 出口裁决：维持“已有生产者、读者未落表”缺口口径，与 evt.split 等同；FG19A 43 条里没有读它们的固件，补读者 = 扩内容，不在 FG-EMG）
            ["evt.hijack"] = "劫持成立时内核发（CombatLogic.Hijack；EMG4-HIJ-01；FgEmgSeizeSelfCheck“共用事件：劫持成立发 evt.hijack”）",
            ["evt.release"] = "劫持 / 夺控 / 封锁结束时内核与 SignalSeizeService 发（CombatLogic.Hijack、SignalSeizeService.InjectHost；EMG4-HIJ-02；FgEmgSeizeSelfCheck“劫持结束同样发 evt.release”）",
        };

        /// <summary>
        /// EMG3-FW-01（FG19 规则三“世界常驻源”之一：内核规则产物）：已生效（stage ≤ 当前阶段）的 fg.TbChannelRule 行的主产物 / 伴生产物物质端口——
        /// 规则结算时内核直接生成这些斑块（蒸汽、烟、熔渣、冰、玻璃、碎屑；ter.block = 固化胶块 mat.cured，EmgRuleCatalog.ProductMatter），不需要声明去写。
        /// </summary>
        /// <summary>EMG3-FW-01：相态端口（phs.*）——阶段 ≥ 2 由内核相变层按 fg.TbPhaseRule 的累积量 / 相态行维护（EMG2-PHS-01），属于世界常驻源。</summary>
        private static bool KernelPhase(string port, int stage)
        {
            if (stage < 2 || !port.StartsWith("phs.", StringComparison.Ordinal))
            {
                return false;
            }
            try
            {
                foreach (GameConfig.fg.PhaseRule r in ConfigSystem.Instance.Tables.TbPhaseRule.DataList)
                {
                    if (r != null && (r.Acc == port || r.Phase == port))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 表不可用：不算
            }
            return false;
        }

        private static bool RuleProduct(string port, int stage)
        {
            try
            {
                foreach (GameConfig.fg.ChannelRule r in ConfigSystem.Instance.Tables.TbChannelRule.DataList)
                {
                    if (r != null && r.Stage <= stage && (r.Product == port || r.ByProduct == port || (port == "mat.cured" && r.Product == "ter.block")))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 表不可用：不算规则产物（审计照常按其余来源判）
            }
            return false;
        }

        /// <summary>非事件读端口的世界常驻源（FG19 规则三“固件、部件、敌人、世界常驻源”）：内核本来就有的量，不需要任何声明去写。</summary>
        internal static readonly Dictionary<string, string> WorldSources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["evt.hurt"] = "敌方对己方单位的伤害（DamageUnit 统一入口）；阶段 1 只有 func_spikes 读它，由爆炸反应装甲原生结算（EMG1-FW-01，FgEmgEraSelfCheck）",
            ["prop.heat"] = "武器积热 / 热信号（每台带武器的单位）",
            ["prop.mass"] = "实体档案质量（fg.TbEntityProfile）",
            ["prop.magnetic"] = "实体档案磁性（fg.TbEntityProfile；EMG2-FLD-01 磁场按它加权）",
            ["prop.conductive"] = "实体档案导电 + 浸湿 / 导电粉尘状态位",
            ["prop.integrity"] = "单位剩余耐久比例",
            ["fld.deflect"] = "敌方偏折单位（静默阵营偏折场）",
            ["chs.moving"] = "单位移动状态",
            ["sqd.relay"] = "信号中继结构模块（struct_relay 的 scope 声明）的中继范围：装中继的机器周围 emg.relay.range 米（EMG3-SQD-01，内核 CombatLogic.Squad；FgEmgSquadSelfCheck）",
            ["ter.obstacle"] = "地形障碍",
            ["ter.cover"] = "障碍 / 残骸",
            ["ter.ground"] = "地面（PCG 地形本身，任何落点都在地面上；EMG3-FW-01 阶段 2 出口核对登记）",
            ["ter.water"] = "水面（PCG 地形水域，chs.water 的来源；EMG3-FW-01 阶段 2 出口核对登记）",
            ["ent.projectile"] = "所有射弹武器的弹体",
            ["tmp.beat"] = "内核全局节拍（CombatLogic.Tempo，每 emg.tempo.beat 一拍；EMG3-TMP-01，FgEmgTempoSelfCheck B / C）",
            ["ent.unit"] = "场上所有单位（实体族“单位”端口；EMG3-FW-01 阶段 2 出口核对登记）",
            ["prop.flammable"] = "实体档案可燃性（fg.TbEntityProfile；EMG2-PERSIST-01 残骸 / 单位着火按它）",
            ["tmp.sync"] = "内核相位对齐（同机 ≥ 2 条节拍指令同拍执行；EMG3-TMP-01，FgEmgTempoSelfCheck D）",
            // EMG4-FW-01（N41 反夺控的读端）：夺控 / 槽位封锁由敌方经统一入口 SignalSeizeService 发起（内核 CombatLogic.EmgSeize 发 evt.seize；封锁注入给接入中的机器），
            // 正式调用方是信号幽灵（FG12-EVT-03）、余烬主机（FG11-BOSS-01）、根权限压制（FG13-END-05），见 DEBT-EMG4FW01-01；FgEmgStage4FirmwareSelfCheck F / FgEmgSeizeSelfCheck G 实测
            ["evt.seize"] = "敌方夺控 / 槽位封锁（SignalSeizeService → 内核 EmgSeize 与封锁注入；EMG4-HIJ-02；正式调用方未落地：DEBT-EMG4FW01-01）",
            ["hij.seized"] = "机器被夺控 / 接入中槽位被封锁的状态（同上，内核夺控记录 FlagSeize；正式调用方未落地：DEBT-EMG4FW01-01）",
        };

        /// <summary>派生源：读端口 ← 写这些端口的声明同样算写者（阶段 1 过渡动词）。</summary>
        internal static readonly Dictionary<string, string> DerivedSources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prop.charge"] = "en.elec",
            ["hij.hijacked"] = "hij.hack", // EMG4-FW-01：被劫持状态由入侵度满后阵亡拦截产生（重启注入写 hij.hack → 内核 EmgTryHijack）
        };

        internal sealed class SourceAudit
        {
            public readonly List<string> Failures = new List<string>();
            public readonly List<(string Port, string Card, string Why)> Gaps = new List<(string, string, string)>();
            public int Effective;
            public int Sourced;
        }

        /// <summary>
        /// 有源审计（FG19 规则三 / 四、FGR-EMG-221“任一读端口无源”）：只判 stage ≤ <paramref name="stage"/> 的端口。
        /// 本阶段生效端口 = 被此刻生效（stageGate ≤ 阶段、owner 正常游戏可获得）的声明读或写到的端口：读端必须有写者（生效声明 / 世界常驻源 / 派生源 / 固定集实测发出的事件），
        /// carrierImpl 写端必须有 fg.TbPortCarrier 行——否则失败。没被生效声明引用的已注册端口（含 stage &gt; 阶段的）逐条列“缺口（承接 &lt;卡&gt;）”，不算通过也不静默跳过。
        /// </summary>
        /// <param name="exitMode">阶段出口模式（FG19 规则三“端口 stage 不高于当前阶段、到该阶段出口仍无人引用即失败”）：stage ≤ 阶段、却没被生效声明引用的端口记失败而不是缺口。
        /// 平时（本卡）只报缺口；阶段出口旅程（EMG1-E2E-01 起）用出口模式调一次。</param>
        internal static SourceAudit AuditSources(EmgSource src, int stage, ICollection<string> observedEvents, bool exitMode = false)
        {
            var a = new SourceAudit();
            bool Obtainable(EmgSource.Decl d) => d.OwnerKind != "firmware" || FirmwareKinds.IsObtainable(d.Owner);
            // EMG2-MAT-01 未接通端口闸（EmgCatalog.PendingPorts，按端口判）：stageGate ≥ 2 的 owner 只要有一条声明用到还没有执行者的端口，整条仍走旧轨、不算生效
            var gatedBy = new Dictionary<string, EmgSource.Decl>(StringComparer.Ordinal);
            foreach (EmgSource.Decl d in src.Decls.Where(d => d != null && d.StageGate >= 2 && d.StageGate <= stage && PendingDecl(d)))
            {
                gatedBy.TryAdd(d.Owner, d);
            }
            var live = src.Decls.Where(d => d != null && d.StageGate <= stage && Obtainable(d) && !gatedBy.ContainsKey(d.Owner)).ToList();
            var portCarrier = new HashSet<string>(src.PortCarriers.Where(pc => pc != null).Select(pc => pc.Port), StringComparer.Ordinal);
            foreach (EmgSource.Port p in src.Ports.Where(p => p != null).OrderBy(p => p.Code))
            {
                var readers = live.Where(d => d.Dir == "read" && d.Port == p.Id).Select(d => d.Owner).Distinct().ToList();
                var writers = live.Where(d => d.Dir == "write" && d.Port == p.Id).Select(d => d.Owner).Distinct().ToList();
                if (p.Stage <= stage && (readers.Count > 0 || writers.Count > 0))
                {
                    a.Effective++;
                    bool sourced = writers.Count > 0 || WorldSources.ContainsKey(p.Id) || (observedEvents?.Contains(p.Id) ?? false) || EventProducers.ContainsKey(p.Id) || RuleProduct(p.Id, stage) || KernelPhase(p.Id, stage)
                                   || (DerivedSources.TryGetValue(p.Id, out string via) && live.Any(d => d.Dir == "write" && d.Port == via));
                    if (readers.Count > 0 && !sourced)
                    {
                        a.Failures.Add($"无源端口 {p.Id}：读者 {string.Join("/", readers.Take(3))}，没有生效写者、世界常驻源或固定集实测发出");
                    }
                    else
                    {
                        a.Sourced++;
                    }
                    if (writers.Count > 0 && p.CarrierImpl == 1 && !portCarrier.Contains(p.Id))
                    {
                        a.Failures.Add($"写端口 {p.Id}（carrierImpl）没有 fg.TbPortCarrier 行：写了也不执行");
                    }
                    continue;
                }
                int gate = src.Decls.Where(d => d != null && d.Port == p.Id).Select(d => d.StageGate).DefaultIfEmpty(0).Min();
                string card = gate > stage ? $"emg.stage = {gate}（阶段 {gate} 开启，出口核对 EMG{gate}-E2E-01）" : CardOf(p.Id, p.Family);
                string why = gate > stage ? $"只有 stageGate = {gate} 的声明引用" : (p.Stage > stage ? $"端口 stage = {p.Stage}" : "没有正常游戏可获得的写者或读者");
                EmgSource.Decl gatedRef = src.Decls.Where(d => d != null && d.Port == p.Id && gatedBy.ContainsKey(d.Owner)).Select(d => gatedBy[d.Owner]).FirstOrDefault();
                if (gate <= stage && gatedRef != null)
                {
                    card = PendingCard(gatedRef.Port, gatedRef.Role);
                    why = $"只有用到未接通端口（{gatedRef.Port}）的 stageGate {gatedRef.StageGate} 声明引用";
                }
                // FG19 第 12 节规则三出口口径（EMG1-E2E-01 修复轮，FG-GAP-183）：内核本阶段真实发出的事件端口（固定集实测）有写者，读者落表前记缺口；
                // 既没有声明引用、内核也不发出的端口到阶段出口即失败（端口 stage 应记到第一个读者 / 写者落表的阶段）。
                bool kernelWrites = (observedEvents?.Contains(p.Id) ?? false) || WorldSources.ContainsKey(p.Id) || EventProducers.ContainsKey(p.Id) || RuleProduct(p.Id, stage) || KernelPhase(p.Id, stage);
                if (exitMode && p.Stage <= stage && !kernelWrites)
                {
                    a.Failures.Add($"阶段出口仍无人引用的端口 {p.Id}（端口 stage = {p.Stage}，{why}，承接 {card}）");
                    continue;
                }
                if (kernelWrites && p.Stage <= stage)
                {
                    why = "本阶段已有生产者（内核 / 热更层注入，按需入队），读者未落表";
                }
                a.Gaps.Add((p.Id, card, why));
            }
            return a;
        }

        private static bool PendingDecl(EmgSource.Decl d) => Enum.TryParse(d.Role, true, out EmgRole role) && EmgCatalog.IsPendingPort(d.Port, role);

        /// <summary>最近一个已出口的 FG-EMG 阶段（阶段出口卡 E2E 完成时提一级：EMG3-E2E-01 → 3；EMG4-E2E-02 → 4）。</summary>
        internal const int ExitedStage = 4; // EMG4-E2E-02（DEBT-EMG4HIJ01-02）：阶段 4 出口

        /// <summary>未接通端口的承接卡（与 EmgCatalog.PendingPorts 注释同一张映射）。</summary>
        private static string PendingCard(string port, string role)
        {
            if (port.StartsWith("fld.", StringComparison.Ordinal))
            {
                return "EMG2-FLD-01";
            }
            if (port == "ent.wreck" || port.StartsWith("mat.", StringComparison.Ordinal))
            {
                return "EMG2-RECL-01";
            }
            // EMG3-CHS-01：阶段 3 开后仍未接通的端口
            if (port == "evt.beat" || port.StartsWith("tmp.", StringComparison.Ordinal))
            {
                return "EMG3-TMP-01";
            }
            if (port == "evt.ping" || port.StartsWith("sqd.", StringComparison.Ordinal))
            {
                return "EMG3-SQD-01";
            }
            if (port.StartsWith("meta.", StringComparison.Ordinal))
            {
                return "EMG3-META-01";
            }
            if (port == "grm.anchor")
            {
                return "EMG3-FUSE-01";
            }
            return "EMG2-RULE-01";
        }

        /// <summary>未被任何声明引用的端口的承接卡（FG19 第 12 节新固件落表；看板 EMG2～EMG4）。</summary>
        private static string CardOf(string port, string family)
        {
            if (port == "evt.ping" || port == "tmp.sync" || port.StartsWith("meta.", StringComparison.Ordinal))
            {
                return "EMG3-FW-02";
            }
            if (port.StartsWith("hij.", StringComparison.Ordinal))
            {
                return "EMG4-FW-01";
            }
            if (port == "grm.anchor")
            {
                return "EMG3-FUSE-01";
            }
            switch (port)
            {
                case "evt.hijack":
                case "evt.release":
                    // EMG4-FW-01：N39～N41 落表后 evt.seize 有了读者（N41）；evt.hijack / evt.release 的生产者已在内核（HIJ-01 / HIJ-02），FG19A 43 条里没有读它们的固件——
                    // 按“已有生产者、读者未落表”记缺口（与 evt.split 等同口径）；EMG4-E2E-02 阶段 4 出口裁决维持此口径（FG-GAP-210 Closed），读者随固件扩充按 FGR-EMG-014 准入另开卡
                    return "FG-GAP-210（维持缺口：读者随固件扩充另开卡）";
                case "evt.seize":
                    return "EMG4-FW-01";
                case "evt.beat":
                    return "EMG3-TMP-01";
                case "evt.phase":
                    return "EMG2-PHS-01";
                case "evt.absorb":
                    return "EMG2-FLD-01";
                case "evt.enter":
                case "evt.leave":
                    return "EMG2-MAT-01";
            }
            return family switch
            {
                "matter" => "EMG2-MAT-01",
                "energy" => "EMG2-RULE-01",
                "phase" => "EMG2-PHS-01",
                "field" => "EMG2-FLD-01",
                "squad" => "EMG3-SQD-01",
                "chassis" => "EMG3-CHS-01",
                "terrain" => "EMG4-TER-01",
                "tempo" => "EMG3-TMP-01",
                "meta" => "EMG3-FW-02",
                "hijack" => "EMG4-FW-01",
                _ => "EMG2-PHS-01（属性 / 累积量读端，DEBT-EMG2RULE01-11）",
            };
        }

        /// <summary>读 Editor/QA 下的自检源码（核对别段的断言原文仍在；影子工程没有根仓路径时用工程内同一文件）。</summary>
        private static string QaSource(string file)
        {
            string path = System.IO.Path.Combine(Application.dataPath, "Editor/QA", file);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
        }

        private static void CheckSources(List<string> observed)
        {
            Line("  · B. 有源（规则三）与已登记缺口：只判 stage ≤ emg.stage 的端口；事件端口的写者 = 固定集实测发出过");
            int stage = EmgCatalog.EffectiveStage;
            SourceAudit a = AuditSources(EmgCatalog.FromTables(), stage, observed);
            Expect(a.Failures.Count == 0 && a.Effective > 0,
                $"本阶段（emg.stage = {stage}）生效端口 {a.Effective} 个全部有源（生效声明写者 / 世界常驻源 {WorldSources.Count} 类 / 派生源 / 固定集实测发出的事件 {observed.Count} 种），本阶段生效端口的缺口清单为空{Detail(a.Failures)}");
            // EMG2-MAT-01：阶段 2 已开但端口按承接卡逐张接通（EmgCatalog.PendingPorts 非空）——出口模式核对上一个已出口的阶段；阶段 2 出口由 EMG2-E2E-01 在全部接通后核对。
            // EMG3-CHS-01：阶段 3 开后，出口阶段 = 还有未接通端口的最低阶段 − 1（与原“阶段 2 已开、有未接通端口 → 阶段 1”同一口径）。
            // EMG3-FW-01：evt.react / prop.heat 接通后只剩 grm.anchor（stage 3）→ 核对阶段 2 出口；暴露的 8 个端口都是内核产物（evt.phase 生产者、规则产物斑块），登记为生产者 / 规则产物源
            // EMG4-HIJ-01：阶段 4 已开（内核状态段与 hij.hack 执行者先行），但阶段 4 的内容（N39～N41、熔坑、夺控）按承接卡逐张落表——出口模式核对最近一个已出口的阶段；
            // 阶段 4 出口由 EMG4-E2E-02 把 ExitedStage 提到 4 后核对（那时 stage ≤ 4 的端口必须全部有人引用）。
            int exitStage = Math.Min(stage, ExitedStage);
            foreach (EmgSource.Port pp in EmgCatalog.FromTables().Ports)
            {
                if (pp != null && EmgCatalog.IsPendingPort(pp.Id, EmgRole.None) && pp.Stage <= stage)
                {
                    exitStage = Math.Min(exitStage, pp.Stage - 1);
                }
            }
            SourceAudit exit = AuditSources(EmgCatalog.FromTables(), exitStage, observed, exitMode: true);
            // EMG1-E2E-01（卡片：出口模式跑一次并清零；FG-GAP-183）：stage ≤ 当前阶段、既无声明引用内核也不发出的端口必须为 0。
            var exitOnly = exit.Failures.Except(a.Failures).ToList();
            var kernelOnly = exit.Gaps.Where(g => g.Why.StartsWith("本阶段已有生产者", StringComparison.Ordinal)).Select(g => g.Port).ToList();
            Expect(exitOnly.Count == 0 && exitStage == ExitedStage,
                $"出口模式（AuditSources exitMode，核对阶段 {exitStage} = 已出口阶段 {ExitedStage}（DEBT-EMG4HIJ01-02：有未接通端口时会降级，降级即失败），阶段 {exitStage} 出口，FG19 第 12 节规则三；生效阶段 {stage}、未接通端口 {EmgCatalog.PendingPorts.Length} 族）：stage ≤ {exitStage} 却无人引用的端口 {exitOnly.Count} 个{Detail(exitOnly)}；" +
                $"已有生产者、读者未落表 {kernelOnly.Count} 个（记缺口）：{string.Join("、", kernelOnly)}");
            // EMG4-E2E-02（FG-GAP-210）：两个劫持事件端口按“已有生产者、读者未落表”记缺口——生产者由 FgEmgSeizeSelfCheck 实测（下面核对断言原文仍在）
            string seizeSrc = QaSource("FgEmgSeizeSelfCheck.cs");
            Expect(kernelOnly.Contains("evt.hijack") && kernelOnly.Contains("evt.release") && seizeSrc.Contains("共用事件：劫持成立发 evt.hijack") && seizeSrc.Contains("劫持结束同样发 evt.release"),
                $"FG-GAP-210 裁决：evt.hijack / evt.release 在阶段 4 出口模式里记“已有生产者、读者未落表”缺口（不是失败），生产者断言在 FgEmgSeizeSelfCheck（{string.Join("、", kernelOnly.Where(p => p == "evt.hijack" || p == "evt.release"))}）");
            foreach (IGrouping<string, (string Port, string Card, string Why)> g in a.Gaps.GroupBy(x => x.Card).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Line($"    · 缺口（承接 {g.Key}）{g.Count()} 个：{string.Join("、", g.Select(x => x.Port))}");
            }
            // EMG2-MAT-01：N14 入场引信端口全部接通 → evt.enter 生效（不是缺口）；N15 接入冲击用到 fld.push（未接通）→ evt.uplink 记缺口、承接 EMG2-FLD-01
            var uplink = a.Gaps.Where(x => x.Port == "evt.uplink").ToList();
            // EMG3-FW-02（卡片“模糊测试器缺口清单里阶段 3 端口为空”）：N01～N08、N30～N32、N42 落表后 meta.*、evt.ping、tmp.sync 都有正常游戏可获得的写者 / 读者；
            // 阶段 3 端口只剩 grm.anchor（N43 接线随 EMG3-FUSE-01 落表，看板在本卡之后）；阶段 4 的 hij.* 照旧报缺口
            var stage3Gaps = a.Gaps.Where(x => EmgCatalog.FromTables().Ports.Any(p => p != null && p.Id == x.Port && p.Stage == 3)).ToList();
            // EMG4-FW-01（卡片“关闭 EMG4-HIJ-01 留下的 hij.* 缺口，模糊测试器缺口清单里阶段 4 端口为空”；DEBT-EMG4HIJ01-01）：N39～N41 落表后 hij.* 与 evt.seize 都有生效的读写者；
            // 阶段 4 端口里只剩 evt.hijack / evt.release（内核已有生产者、FG19A 没有读它们的固件，按“已有生产者、读者未落表”记缺口，FG-GAP-210），没有一个承接 EMG4-FW-01
            var stage4Gaps = a.Gaps.Where(x => EmgCatalog.FromTables().Ports.Any(p => p != null && p.Id == x.Port && p.Stage == 4)).ToList();
            var stage4Bare = stage4Gaps.Where(x => x.Port != "evt.hijack" && x.Port != "evt.release").ToList();
            Expect(a.Gaps.Count > 0 && a.Gaps.All(x => !string.IsNullOrEmpty(x.Card)) && !a.Gaps.Any(x => x.Port.StartsWith("hij.", StringComparison.Ordinal) || x.Port == "evt.seize" || x.Card == "EMG4-FW-01")
                   && stage4Bare.Count == 0
                   && !a.Gaps.Any(x => x.Port == "evt.ping" || x.Port == "tmp.sync" || x.Port.StartsWith("meta.", StringComparison.Ordinal))
                   && stage3Gaps.Count == 0 && !a.Gaps.Any(x => x.Port == "evt.enter") && uplink.Count == 0,
                $"未被生效声明引用的已注册端口 {a.Gaps.Count} 个逐条报“缺口（承接 <卡>）”（不算通过、不静默跳过）：hij.* / evt.seize 已由 EMG4-FW-01 落表清零（阶段 4 端口的缺口只剩已有生产者的 {string.Join("、", stage4Gaps.Select(x => x.Port + "→" + x.Card))}，" +
                $"其余 {stage4Bare.Count} 个）；meta.* / evt.ping / tmp.sync 已由 EMG3-FW-02 落表清零；" +
                $"阶段 3 端口的缺口 {stage3Gaps.Count} 个（{string.Join("、", stage3Gaps.Select(x => x.Port + "→" + x.Card))}；EMG3-FUSE-01 落表 N43 后应为 0）；N14 的读端 evt.enter 已生效，N15 的读端 evt.uplink 随 fld.push 接通（EMG2-FLD-01）也已生效，不再是缺口（{uplink.Count}）");
        }

        // ═════════════════════════════════ D 负向 ═════════════════════════════════

        private static void CheckNegatives(Pools pools)
        {
            Line("  · D. 负向：故意构造的死指令、无源端口、NaN、卡死、积压都必须被报出");
            // 1. 死指令：反弹引信放在没有任何反弹来源的场地（没有偏折单位、障碍、残骸，程序里也没有跳弹）
            var c = new FuzzCase
            {
                Index = 900, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                Faction = pools.Factions[0], Enemies = 3, EnemySeed = 7, Uplinked = true, Uplink = new[] { "fw_bouncefuse" }, NoWorldSources = true, Tag = "负向：无反弹来源",
            };
            CaseResult r = Evaluate(c, full: false, pools);
            Expect(r.Failed && r.Dead.Any(d => d.StartsWith("fw_bouncefuse", StringComparison.Ordinal)),
                $"构造的死指令被报出：{c.Text} → {r.Problems}");
            // 对照：同一程序在有偏折单位的场地不是死指令（判定不是一律报死）
            c.NoWorldSources = false;
            c.Index = 901;
            CaseResult ok = Evaluate(c, full: false, pools);
            Expect(ok.Dead.Count == 0 && ok.Events[(int)EmgEventKind.Bounce] > 0,
                $"对照：同一程序在有偏折单位的场地激活（evt.bounce {ok.Events[(int)EmgEventKind.Bounce]} 次，死指令 {ok.Dead.Count}）");
            // 1b. FG-GAP-179 口径（EMG1-E2E-01 定，待用户复核）：条件型零反应只在读法写明条件时才允许——不改数值、不改机制。
            //   ① [延时引信] 装在无人机舱：无人机贴近开火，子弹 0.6 秒内先命中就不空爆（与射弹载体同一条规则）；
            //   ② [冷却回流] 需要积热来源（固定集定向用例开局预热时照常激活，见 C 段；不积热、也没有积热指令时不触发）。
            var dz = new FuzzCase
            {
                Index = 902, Comp = ComponentCatalog.CompDroneBayId, Carrier = FirmwareCarrier.Summon, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                Faction = pools.Factions[0], Enemies = 3, EnemySeed = 7, Uplinked = true, Uplink = new[] { "fw_delayfuse", FgReadingMatrixSelfCheck.AddonFiller }, Tag = "FG-GAP-179 ①",
            };
            EnsureConditions(dz, pools);
            CaseResult dr = Evaluate(dz, full: false, pools);
            bool droneDead = dr.Dead.Concat(dr.CondZero).Any(d => d.StartsWith("fw_delayfuse", StringComparison.Ordinal)); // 固定集同一口径：读法写明条件的单列为条件型零反应
            string droneRead = GameText.Get("firmware.fw_delayfuse.read.summon");
            string coolDesc = GameText.Get("firmware.fw_coolback.desc");
            Expect(dr.Crash == null && dr.Stall == null && (!droneDead || droneRead.Contains("先命中则不空爆")) && coolDesc.Contains("需要积热来源"),
                $"FG-GAP-179：[延时引信]×无人机舱 {(droneDead ? "零反应（子弹先命中）" : "激活")}，读法写明条件“{droneRead}”；[冷却回流] 说明写明“需要积热来源”（{coolDesc}）");
            // 2. 无源端口：构造一个阶段 2 的实体族端口（不在世界源 / 生产者 / 规则产物里，EMG3-FW-01 起真实表里 stage ≤ 2 的端口都已有来源，改用构造端口，判定本身仍要能失败）
            const string NegPort = "prop.fuzzneg";
            EmgSource WithNegPort()
            {
                EmgSource s0 = EmgCatalog.FromTables();
                s0.Ports.Add(new EmgSource.Port { Id = NegPort, Family = "entity", Dirs = "both", Unit = "ratio", NameKey = "emg.port.prop.mass.name", DescKey = "emg.port.prop.mass.desc", Code = 699, Perceive = 1, Stage = 2 });
                return s0;
            }
            EmgSource src = WithNegPort();
            src.Decls.Add(new EmgSource.Decl
            {
                Id = "comp_fuzzneg.1", Owner = "comp_fuzzneg", OwnerKind = "component", Seq = 1, Dir = "read", Port = NegPort, Role = "sense", Carriers = "all",
                TextKey = "emg.decl.comp_fuzzneg.1", StageGate = 2, Amount = 1f,
            });
            SourceAudit au = AuditSources(src, 2, new List<string>());
            SourceAudit au0 = AuditSources(WithNegPort(), 2, new List<string>());
            SourceAudit auExit = AuditSources(WithNegPort(), 2, new List<string>(), exitMode: true);
            string exitPort = NegPort;
            SourceAudit au1 = AuditSources(EmgCatalog.FromTables(), 1, new List<string>());
            string producerPort = au1.Gaps.Select(g => g.Port).FirstOrDefault(EventProducers.ContainsKey);
            SourceAudit auExit1 = AuditSources(EmgCatalog.FromTables(), 1, new List<string>(), exitMode: true);
            Expect(auExit.Failures.Any(f => f.Contains("端口 " + exitPort + "（")) && !auExit.Gaps.Any(g => g.Port == exitPort) && au0.Gaps.Any(g => g.Port == exitPort)
                   && producerPort != null && !auExit1.Failures.Any(f => f.Contains("端口 " + producerPort + "（")) && auExit1.Gaps.Any(g => g.Port == producerPort),
                $"出口模式：stage ≤ 当前阶段、无人引用也没有生产者的端口（构造的阶段 2 端口 {exitPort}）平时只是缺口，出口模式下判失败（{auExit.Failures.FirstOrDefault(f => f.Contains(exitPort))}）；" +
                $"有阶段 1 生产者的端口（例 {producerPort ?? "无"}）出口模式下仍记缺口");
            Expect(au.Failures.Any(f => f.Contains(NegPort)) && !au0.Failures.Any(f => f.Contains(NegPort)) && au0.Gaps.Any(g => g.Port == NegPort),
                $"构造的无源端口被报出：{au.Failures.FirstOrDefault(f => f.Contains(NegPort))}；不加读它的声明时只是缺口（承接 {au0.Gaps.FirstOrDefault(g => g.Port == NegPort).Card}）");
            // 3. NaN 位置判崩溃：真实场地里把一个敌人的位置写成 NaN
            using (Arena a = Build(new FuzzCase { Index = 902, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home", Faction = pools.Factions[0], Enemies = 3, EnemySeed = 3 }, true, pools))
            {
                a.StepOnce(0);
                a.K.SetPosition(a.Hostiles[a.Hostiles.Count - 1], new double2(double.NaN, 0.0));
                var nr = new CaseResult { Case = new FuzzCase { Index = 902 } };
                Scan(a, nr, 1, 1e9f, 1e9f, 1 << 20, new Dictionary<int, int>());
                Expect(nr.Crash != null && nr.Crash.Contains("NaN"), $"NaN / 无穷位置判崩溃：{nr.Crash}");
            }
            // 4. 卡死：事件队列连续 2 秒只增不减；单步 > 100 ms（判定阈值与主跑同一常量）
            using (Arena a = Build(new FuzzCase { Index = 903, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home", Faction = pools.Factions[0], Enemies = 3, EnemySeed = 5, Uplinked = true, Uplink = new[] { "fw_split" } }, true, pools))
            {
                // 每步注入的事件比每步处理上限多（只增不减）——直接用内核注入口，不经积压合并
                a.StepOnce(0);
                int rise = 0, prev = a.K.EmgBusPending, injected = 0;
                int perStep = a.K.Config.EmgBusPerStep + 64;
                string stall = null;
                bool backlog = false;
                for (int i = 1; i < 400 && !(stall != null && backlog); i++)
                {
                    for (int j = 0; j < perStep && a.K.EmgBusPending < EmgConst.BusCapacity - 1; j++)
                    {
                        injected += a.K.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hurt, Ent = a.Target, EntKind = EmgEntKind.Unit, Prog = -1, Pos = new double2(3.0, 0.3) }) ? 1 : 0;
                    }
                    long before = a.K.Steps;
                    a.StepOnce(i);
                    int pending = a.K.EmgBusPending;
                    stall ??= StallOf(i, 0.0, before, a.K.Steps, pending, ref prev, ref rise); // 步耗记 0：只让“只增不减”这一条触发
                    backlog |= pending > EmgConst.BusCapacity / 2;
                }
                Expect(stall != null && stall.Contains("只增不减") && backlog && injected > 0,
                    $"卡死判定（主跑同一段 StallOf）：每步注入 {perStep} 条（> 每步处理 {a.K.Config.EmgBusPerStep}）后被判“{stall}”；队列 {prev} 条超过容量 50% 被判积压（共注入 {injected} 条）");
            }
            {
                int p0 = 0, r0 = 0, p1 = 0, r1 = 0, p2 = 0, r2 = 0;
                string slow = StallOf(7, 120.0, 10, 11, 0, ref p0, ref r0), frozen = StallOf(7, 1.0, 10, 10, 0, ref p1, ref r1), fine = StallOf(7, 99.0, 10, 11, 0, ref p2, ref r2);
                Expect(slow != null && slow.Contains("单步") && frozen != null && frozen.Contains("不前进") && fine == null
                       && StallStepMs == 100.0 && StallRiseSteps == Mathf.RoundToInt(2f / Dt),
                    $"卡死判定（主跑同一段 StallOf，注入步耗）：120 ms 一步被判“{slow}”；步数不前进被判“{frozen}”；99 ms 不判（阈值 FGR-EMG-221：单步 > {StallStepMs} ms、连续 {StallRiseSteps} 步只增不减）");
            }
            // 4b. 修复弹体只走穿透（FG-GAP-178 ④ 的边界）：维修束 + 绕行 + 穿透，修复弹体碰到友军后只能是穿透（带 Pierced），不能停下绕行或折返
            var hc = new FuzzCase { Index = 904, Comp = ComponentCatalog.CompRepairBeamId, Carrier = CarrierOf(ComponentCatalog.CompRepairBeamId), Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                Faction = pools.Factions[0], Enemies = 1, EnemySeed = 7, Uplinked = true, Uplink = new[] { "fw_orbit", "fw_pierce" }, NoWorldSources = true };
            using (Arena a = Build(hc, false, pools))
            {
                int healProj = 0, pierced = 0, bad = 0;
                for (int i = 0; i < 240; i++)
                {
                    a.StepOnce(i);
                    for (int s = 0; s < a.K.ProjectileCount; s++)
                    {
                        if (!a.K.TryGetProjectile(s, out CombatProjectile pr) || (pr.Flags & EmgProjFlags.Heal) == 0)
                        {
                            continue;
                        }
                        healProj++;
                        bool afterHit = pr.IgnoreUnit > 0 && pr.IgnoreUnit != a.Me; // 根修复弹体出膛时 IgnoreUnit = 本机
                        pierced += afterHit && (pr.Mods & EmgProjMods.Pierced) != 0 ? 1 : 0;
                        bad += (afterHit && (pr.Mods & EmgProjMods.Pierced) == 0) || (pr.Mods & EmgProjMods.ReturnHit) != 0 ? 1 : 0;
                    }
                }
                Expect(healProj > 0 && bad == 0,
                    $"维修束 + 绕行 + 穿透：修复弹体命中友军后只走穿透（在飞修复弹体 {healProj} 步次，穿过友军继续飞 {pierced} 步次，命中后绕行 / 折返 {bad} 步次 = 0）");
            }
            // 5. 计数开关：正式流程不开（新内核计数数组为空、InstrActivations 恒 0）
            using (var k = new CombatKernel(Config(), 8))
            {
                Expect(!k.InstrActivationCounting && k.InstrActivations(0) == 0, "激活计数缺省关闭（正式流程的内核计数数组长度 0，只有模糊测试器打开）");
            }
        }

        /// <summary>DEBT-EMG1ENT01-08（承接到本卡的部分）：子弹药质量 = 母载荷 ÷ 份数，逐代相除（真实编译 + 真实内核里在飞的子弹药）。</summary>
        private static void CheckSubMass(Pools pools)
        {
            Line("  · F. 子弹药质量 = 母载荷 ÷ 份数（DEBT-EMG1ENT01-08 承接到本卡的部分）");
            var c = new FuzzCase { Index = 950, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                Faction = pools.Factions[0], Enemies = 1, EnemySeed = 11, Uplinked = true, Uplink = new[] { "fw_split", "fw_delayfuse" }, NoWorldSources = true }; // 只留主目标：第 1 代子弹药从命中点向前飞，前方没有别的敌人挡着，0.6 秒定时到点放出第 2 代
            using Arena a = Build(c, false, pools);
            float root = a.K.EntityProfileAt(EmgConst.SlotProjectile).Mass;
            int n1 = a.Program.Nodes.Count > 1 ? a.Program.Nodes[1].TrigCount : 0;
            int n2 = a.Program.Nodes.Count > 2 ? a.Program.Nodes[2].TrigCount : 0;
            float g1 = -1f, g2 = -1f;
            for (int i = 0; i < 480 && (g1 < 0f || g2 < 0f); i++)
            {
                a.StepOnce(i);
                for (int j = 0; j < a.K.ProjectileCount; j++)
                {
                    if (a.K.TryGetProjectile(j, out CombatProjectile p) && (p.Flags & EmgProjFlags.Sub) != 0)
                    {
                        if (p.Gen == 1 && g1 < 0f) g1 = p.Mass;
                        if (p.Gen == 2 && g2 < 0f) g2 = p.Mass;
                    }
                }
            }
            Expect(n1 > 1 && n2 > 1 && Mathf.Approximately(g1, root / n1) && Mathf.Approximately(g2, root / n1 / n2),
                $"[分裂, 延时引信]：根弹体质量 {root}；第 1 代子弹药 {g1}（= {root} ÷ {n1}），第 2 代 {g2}（= {root} ÷ {n1} ÷ {n2}）——穿透按质量比较时子弹药更轻");
        }

        // ═════════════════════════════════ E 涌现满载 ═════════════════════════════════

        /// <summary>满载场景里机器的几套装配（真实编译；阶段 1：触发器 / 子载荷 / 定时 / 寻的 / 穿透 / 子母弹 / 无人机）。</summary>
        private static readonly (string Comp, string[] Uplink)[] LoadLoadouts =
        {
            (ComponentCatalog.CompGunId, new[] { "fw_split", "fw_delayfuse" }),
            (ComponentCatalog.CompGunId, new[] { "fw_cluster", "fw_homing" }),
            (ComponentCatalog.CompGunId, new[] { "fw_pierce", "fw_piercefuse" }),
            (ComponentCatalog.CompCannonId, new[] { "fw_fork", "fw_coolant" }),
            (ComponentCatalog.CompDroneBayId, new[] { "fw_split", "fw_ricochet" }),
            (ComponentCatalog.CompSprayerId, new[] { "fw_echo", "fw_water" }),
        };

        private sealed class LoadStats
        {
            public readonly List<double> SumMs = new List<double>();
            public readonly double[] SiteMs = new double[3];
            public int MaxProjectiles, MinProjectiles = int.MaxValue, MaxDrones, Wrecks, Machines, MinHostiles = int.MaxValue, MinTurrets = int.MaxValue;
            public int MinPatches = int.MaxValue, MinFields = int.MaxValue, MaxHomePatches, MinPatchesAll, MinDrones = int.MaxValue;
            public long Enqueued, Processed, SubSpawned, Injected, SpawnLost;
            public int MaxProcessedPerStep, MaxPending, Rise, MaxRise;
            public long Alloc;
        }

        private static void CheckFullLoad()
        {
            Line("  · E. 涌现满载性能场景（FGR-EMG-203 多地点：远征满载战斗 + 家园防线与长期残骸 + 前哨常态，同一世界步推进，计时取三地点内核之和）");
            const int warm = 600, measure = 600; // 预热 10 秒与 FgCombatKernelSelfCheck 性能段同一口径：突袭者从 45 米外压上来、双方弹幕铺满后才开始计
            LoadStats off = MeasureLoad(0, warm, measure);
            LoadStats on = MeasureLoad(EmgCatalog.ImplementedStage, warm, measure);
            UseStage(EmgCatalog.ImplementedStage);
            double P(List<double> l, double q)
            {
                var s = l.OrderBy(x => x).ToList();
                return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(s.Count * q))];
            }
            double p99On = P(on.SumMs, 0.99), p99Off = P(off.SumMs, 0.99), avgOn = on.SumMs.Average(), avgOff = off.SumMs.Average();
            const double rec = 0.6; // ADR-ARC-003 / ADR-EMG-001 ⑬：开发机数字 ÷ 0.6 = 推荐配置
            double addP99 = Math.Max(0, p99On - p99Off), addAvg = Math.Max(0, avgOn - avgOff);
            double enqPerStep = on.Enqueued / (double)measure, procPerStep = on.Processed / (double)measure, injPerStep = on.Injected / (double)measure, natPerStep = (on.Enqueued - on.Injected) / (double)measure;
            Line($"    · 环境：{SystemInfo.processorType}（{SystemInfo.processorCount} 线程），Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；" +
                 "内核是 Editor 下的 Burst 作业（真机 IL2CPP + Burst AOT 相同或更快），热更层另测（FG15-SYS-02）；推荐配置按开发机 ÷ 0.6 换算（ADR-ARC-003）");
            Line($"    · 规模（阶段 2 新增满载项，预期差异，LANDING 6.3）：远征斑块每秒补回后最少 {on.MinPatches}、活跃场最少 {on.MinFields}、家园斑块峰值 {on.MaxHomePatches}");
            Expect(on.MinPatches >= 488 && on.MinFields >= 64 && on.MaxHomePatches >= 100,
                $"阶段 2 满载项装上（FGR-EMG-203 活跃场 64、家园 128；斑块只放不挡弹不伤人的 8 种，软上限之和 488，到顶就是这个量，1024 块由 FgEmgMatterSelfCheck 斑块段单测）：斑块 {on.MinPatches}、场 {on.MinFields}、家园 {on.MaxHomePatches}");
            Line($"    · 规模（阶段 1 满载项）：远征 200 突袭者 + 80 炮塔 + {on.Machines} 台带程序机器（测量期敌人最少 {on.MinHostiles}、炮塔最少 {on.MinTurrets}），弹体 {on.MinProjectiles}～{on.MaxProjectiles}，无人机峰值 {on.MaxDrones}，残骸 {on.Wrecks}（远征 256 + 家园 256），" +
                 $"子载荷生成 {on.SubSpawned}；事件负载：远征每步平均入队 {enqPerStep:F1}（自然产生 {natPerStep:F1} + 宿主模板注入 {injPerStep:F1}，常态 {LoadEventsPerStep}、每秒一次峰值 {LoadEventsPeak}；家园每步注入 {HomeEventsPerStep}）/ 三地点处理 {procPerStep:F1}、" +
                 $"单步处理峰值 {on.MaxProcessedPerStep}、队列峰值 {on.MaxPending}、最长连续只增 {on.MaxRise} 步；无人机峰值未到规格 512（承接 FG15-SYS-02）");
            Line($"    · 三地点内核之和每世界步：阶段 1 平均 {avgOn:F3} / p99 {p99On:F3} ms（远征 {on.SiteMs[0] / measure:F3}、家园 {on.SiteMs[1] / measure:F3}、前哨 {on.SiteMs[2] / measure:F3}）；" +
                 $"同场景 emg.stage = 0（程序为空、没有事件负载）平均 {avgOff:F3} / p99 {p99Off:F3} ms；涌现新增 = 两次独立运行的平均之差 {addAvg:F3} ms（p99 之差 {addP99:F3} ms 只作参考：两次运行逐步不配对，差值没有分位意义）；" +
                 $"测量 {measure} 步托管分配 {on.Alloc / 1024.0:F1} KB");
            Line($"    · stat 4（节拍锚点 / 元程序运行态与待生成队列满拒绝，DEBT-EMGRTRIG01-01，只报告）：三地点合计 {on.SpawnLost}");
            bool scale = on.MinProjectiles >= 1500 && on.Wrecks >= 400 && on.Machines >= 120 && on.SubSpawned > 0 && on.MaxDrones > 0;
            Expect(scale, $"满载规模达标：测量窗口弹体始终 ≥ 1500（{on.MinProjectiles}）、带程序机器 {on.Machines} ≥ 120、残骸 {on.Wrecks} ≥ 400、有子载荷（{on.SubSpawned}）与无人机（{on.MaxDrones}）");
            PerfGate.Expect(true,
                $"FGR-EMG-200（绝对预算）：三地点战斗内核每世界步 p99 {p99On:F3} ms → 推荐配置 {p99On / rec:F3} ms ≤ 1.5 ms（3x 每世界步，按推荐默认执行，2026-10-28），即 1x ≤ 3.0 ms / 帧；" +
                $"涌现新增（平均之差）{addAvg:F3} ms → {addAvg / rec:F3} ms ≤ 1.0 ms",
                new[] { PerfGate.Le(p99On / rec, 1.5, "三地点内核每世界步 p99（推荐配置 ms）"), PerfGate.Le(addAvg / rec, 1.0, "涌现新增每世界步（平均之差，推荐配置 ms）") }, Expect, Line);
            Expect(procPerStep >= LoadEventsPerStep && on.MaxProcessedPerStep >= LoadEventsPeak && on.Injected > 0,
                $"满载事件负载装上（FGR-EMG-203“事件持续 512 / 步”、ADR ⑬ 常态约 150）：三地点每步平均处理 {procPerStep:F1} ≥ {LoadEventsPerStep}、单步处理峰值 {on.MaxProcessedPerStep} ≥ {LoadEventsPeak}，" +
                "走真实总线分派（宿主 = 机器程序里读该事件码的节点）");
            Expect(enqPerStep <= 300 && on.MaxProcessedPerStep <= 512 * 3 && on.MaxRise < StallRiseSteps,
                $"ADR ⑬ 事件入队量：远征满载每步平均入队 {enqPerStep:F1} ≤ 估算 150 × 2；每世界步处理峰值 {on.MaxProcessedPerStep} ≤ 三地点各 512；队列没有连续 2 秒只增不减（最长 {on.MaxRise} 步）");
            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\n  \"scene\": \"EMG-FULLLOAD\",\n  \"stage\": {0},\n  \"measuredAt\": \"{1}\",\n  \"cpu\": \"{2}\",\n  \"steps\": {3},\n  \"kernelSumAvgMs\": {4:F4},\n  \"kernelSumP99Ms\": {5:F4},\n" +
                "  \"stage0AvgMs\": {6:F4},\n  \"stage0P99Ms\": {7:F4},\n  \"emergentAddAvgMs\": {8:F4},\n  \"projectilesMin\": {9},\n  \"dronesMax\": {10},\n  \"wrecks\": {11},\n  \"machines\": {12},\n" +
                "  \"enqueuedPerStep\": {13:F2},\n  \"injectedPerStep\": {15:F2},\n  \"naturalEnqueuedPerStep\": {16:F2},\n  \"processedPerStep\": {14:F2},\n  \"maxProcessedPerStep\": {17},\n" +
                "  \"note\": \"FgEmergenceFuzzSelfCheck E 段写入；事件负载 = 自然产生 + 宿主模板注入（远征常态 150 / 步、每秒一次峰值 512）；无人机未到规格 512、斑块 / 场为阶段 2 项（DEBT-EMG1FUZZ01-01）\"\n}}\n",
                EmgCatalog.ImplementedStage, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), SystemInfo.processorType, measure, avgOn, p99On, avgOff, p99Off, addAvg,
                on.MinProjectiles, on.MaxDrones, on.Wrecks, on.Machines, enqPerStep, procPerStep, injPerStep, natPerStep, on.MaxProcessedPerStep);
            string dir = EvidenceDir();
            if (dir != null)
            {
                File.WriteAllText(Path.Combine(dir, "_emg-fullload.json"), json);
                string baseDir = Path.Combine(Directory.GetParent(dir).FullName, "perf-baseline");
                string basePath = Path.Combine(baseDir, "EMG-FULLLOAD.json");
                if (Directory.Exists(baseDir) && !File.Exists(basePath))
                {
                    File.WriteAllText(basePath, json); // 阶段 1 首次建立：存为后续阶段的“上一次”（FGR-EMG-203 判定 1 ②）
                    Line($"    · 首次建立满载基准：{basePath}");
                }
                else if (File.Exists(basePath))
                {
                    Line($"    · 上一次满载基准：{basePath}（阶段 2 起对它判退化 ≤ 10%；阶段 1 只报告）");
                }
            }
        }

        private static LoadStats MeasureLoad(int stage, int warm, int measure)
        {
            UseStage(stage);
            var st = new LoadStats();
            st.MinPatchesAll = int.MaxValue;
            _maxWorldDone = false;
            CombatBench.Spec spec = CombatBench.PerfSpec();
            CombatBench.Spec expedition = spec;
            expedition.RaiderHp *= _maxLoad != 0 ? 4.5f : 3f; // EMG4-FUZZ-01：加满（烟雾 / 蒸汽挪外环后不再遮蔽，火力更密，测量窗口里会打掉 1 个）再 ×1.5，只改耐久不改数量； 远征多了 120 台机器一起打：突袭者耐久 ×3，让“200 敌人 + ≥1,500 弹体”在测量窗口里持续同时存在（与 combat.perf.hp_scale 同一用意）
            var sites = new[] { new CombatSite("fuzz_load_expedition", Config()), new CombatSite("fuzz_load_home", Config()), new CombatSite("fuzz_load_outpost", Config()) };
            var machines = new List<int>[sites.Length];
            try
            {
                CombatBench.SpawnPerfScenario(sites[0], Vector2.zero, 200, 80, expedition);
                CombatBench.SpawnPerfScenario(sites[1], Vector2.zero, 60, 30, spec);
                CombatBench.SpawnPerfScenario(sites[2], Vector2.zero, 10, 12, spec);
                var hosts = new[] { new List<EmgEvent>(), new List<EmgEvent>(), new List<EmgEvent>() };
                machines[0] = SpawnLoadMachines(sites[0], 120, 18f, hosts[0]);
                machines[1] = SpawnLoadMachines(sites[1], 20, 16f, hosts[1]);
                machines[2] = new List<int>();
                int cursor = 0;
                st.Machines = machines[0].Count + machines[1].Count;
                st.Wrecks = AddWrecks(sites[0], 256, 26f) + AddWrecks(sites[1], 256, 20f);
                int patchSeq = 0;
                if (stage >= 2)
                {
                    TopUpStage2Load(sites, ref patchSeq); // DEBT-EMG1FUZZ01-01：阶段 2 满载项（远征斑块 + 活跃场 64、家园长期留存斑块 128）
                }
                double t = 0;
                for (int i = 0; i < warm; i++)
                {
                    if (i % 60 == 0)
                    {
                        for (int s = 0; s < sites.Length; s++)
                        {
                            OrderAttacks(sites[s], machines[s], i);
                        }
                    }
                    if (stage >= 2 && i % 60 == 0)
                    {
                        TopUpStage2Load(sites, ref patchSeq); // 预热期同样每秒补，测量第一秒就是满载
                    }
                    InjectLoad(sites, hosts, i, ref cursor);
                    foreach (CombatSite s in sites)
                    {
                        s.Step(Dt, t);
                    }
                    t += Dt;
                }
                long enq0 = sites[0].Kernel.EmgBusStats.Enqueued, proc0 = Processed(sites), sub0 = sites[0].Kernel.EmgBusStats.SubSpawned + sites[1].Kernel.EmgBusStats.SubSpawned;
                int prev = 0;
                long alloc0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < measure; i++)
                {
                    if (i % 60 == 0)
                    {
                        // 玩家的攻击命令（热更层下达，每秒一次，不在内核计时里）：目标死了换下一个活着的敌人
                        for (int s = 0; s < sites.Length; s++)
                        {
                            OrderAttacks(sites[s], machines[s], warm + i);
                        }
                        if (stage >= 2)
                        {
                            TopUpStage2Load(sites, ref patchSeq); // 补的是两步之间的注入，不在内核计时里
                        }
                    }
                    if (stage >= 2 && i % 60 == 30)
                    {
                        // 两次补之间读：请求按每步额度生成，3 秒的场在补的那一刻正好全部到期
                        st.MinPatches = Math.Min(st.MinPatches, sites[0].Kernel.PatchCount);
                        st.MinFields = Math.Min(st.MinFields, sites[0].Kernel.FieldCount);
                        st.MaxHomePatches = Math.Max(st.MaxHomePatches, sites[1].Kernel.PatchCount);
                    }
                    st.Injected += InjectLoad(sites, hosts, warm + i, ref cursor); // 两步之间（热更层注入的时机），分派在下一步的 5d 段、计入内核计时
                    long pb = Processed(sites);
                    double sum = 0;
                    for (int s = 0; s < sites.Length; s++)
                    {
                        sites[s].Step(Dt, t);
                        sum += sites[s].LastKernelMs;
                        st.SiteMs[s] += sites[s].LastKernelMs;
                    }
                    t += Dt;
                    st.SumMs.Add(sum);
                    int pc = sites[0].Kernel.ProjectileCount;
                    st.MinProjectiles = Math.Min(st.MinProjectiles, pc);
                    st.MaxProjectiles = Math.Max(st.MaxProjectiles, pc);
                    st.MaxDrones = Math.Max(st.MaxDrones, sites[0].Kernel.DroneCount + sites[1].Kernel.DroneCount);
                    st.MinDrones = Math.Min(st.MinDrones, sites[0].Kernel.DroneCount);
                    st.MinPatchesAll = Math.Min(st.MinPatchesAll, sites[0].Kernel.PatchCount);
                    if (i % 60 == 0)
                    {
                        st.MinHostiles = Math.Min(st.MinHostiles, sites[0].Kernel.CountAlive(CombatFaction.Hostile));
                        st.MinTurrets = Math.Min(st.MinTurrets, sites[0].Kernel.CountAlive(CombatFaction.Player, CombatUnitKind.Turret));
                    }
                    st.MaxProcessedPerStep = Math.Max(st.MaxProcessedPerStep, (int)(Processed(sites) - pb));
                    int pending = 0;
                    foreach (CombatSite s in sites)
                    {
                        pending += s.Kernel.EmgBusPending;
                    }
                    st.MaxPending = Math.Max(st.MaxPending, pending);
                    st.Rise = pending > prev ? st.Rise + 1 : 0;
                    st.MaxRise = Math.Max(st.MaxRise, st.Rise);
                    prev = pending;
                }
                st.Alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
                st.Enqueued = sites[0].Kernel.EmgBusStats.Enqueued - enq0;
                st.Processed = Processed(sites) - proc0;
                st.SubSpawned = sites[0].Kernel.EmgBusStats.SubSpawned + sites[1].Kernel.EmgBusStats.SubSpawned - sub0;
                st.SpawnLost = sites.Sum(s => s.Kernel.EmgBusStats.SpawnLost); // 整段（预热 + 测量）三地点合计
            }
            finally
            {
                foreach (CombatSite s in sites)
                {
                    s.Dispose();
                }
            }
            return st;
        }

        /// <summary>
        /// FGR-EMG-203 阶段 2 满载项：远征地点铺满斑块、活跃场 64，家园 128 块。按覆盖式格子轮着补（不重叠，软上限内并入 / 到期算真实降级路径），生成走内核同一条路径。
        /// 8 种常见物质混着放，规则节拍、接触、传导都真实发生。shortcut: 无人机没到规格 512（只有机器自然放出的几十架），要补得给满载场景配 512 架的来源，承接 FG15-SYS-02。
        /// </summary>
        private static void TopUpStage2Load(CombatSite[] sites, ref int seq)
        {
            // 不挡弹、不伤人的物质：要量的是斑块层本身的开销，不能让斑块把战斗打散（FGR-EMG-203 同屏弹体 1500 同时成立）
            byte[] mats = { EmgMatterConst.Oil, EmgMatterConst.Water, EmgMatterConst.Coolant, EmgMatterConst.Polymer, EmgMatterConst.Debris, EmgMatterConst.Dust, EmgMatterConst.Smoke, EmgMatterConst.Steam };
            const int side = 34;
            CombatKernel k = sites[0].Kernel;
            if ((_maxLoad & MaxPatches) == 0)
            {
                for (int n = 1024 - k.PatchCount; n > 0; n--) // 只补缺口：SpawnPatch 是排队的（每步额度），多要只会堆“软上限并入”的空转，那不是满载而是刷生成
                {
                    int cell = seq++ % (side * side);
                    var at = new double2((cell % side - side / 2) * 2.6, (cell / side - side / 2) * 2.6);
                    k.SpawnPatch(mats[seq % mats.Length], at, 1f, 1f, 8f);
                }
            }
            else
            {
                // 加满（FGR-EMG-203 斑块 1024 = 世界源保留 96 + 17 种物质软上限合计 928，FG19B 第 0 节第 6 条）：按种类只补到各自软上限（MatterRow.Quota），
                // 存续 30 秒——补的量 = 到期的量，不刷“软上限并入”的空转。上面 8 种不挡弹不伤人的照旧铺在战场格子上；其余 9 种（冰、玻璃、火、酸、熔融等挡弹 / 伤人）铺在 70～90 米外环，
                // 不打散 1500 弹体的战斗，斑块层的节拍、合并、传导开销照样全量发生
                var have = new int[EmgMatterConst.Count + 1];
                for (int i = 0; i < k.PatchSlots; i++)
                {
                    if (k.TryGetPatch(i, out CombatZone z) && z.Matter <= EmgMatterConst.Count)
                    {
                        have[z.Matter]++;
                    }
                }
                for (byte m = 1; m <= EmgMatterConst.Count; m++)
                {
                    // EMG4-FUZZ-01 续做（加满弹体最少 1357 < 1500 的根因）：烟雾 / 蒸汽按软上限铺满战场后遮蔽单位（CombatLogic.Field EmgObscured：烟里 3 米 / 蒸汽 6 米外选不中目标），
                    // 机器选不到目标就不开火，弹体掉到 1357；这两种也铺到外环（块数不变，斑块层开销照样全量），常规满载（上面那支，8 秒存续）不动
                    bool near = Array.IndexOf(mats, m) >= 0 && m != EmgMatterConst.Smoke && m != EmgMatterConst.Steam;
                    for (int n = k.MatterRow(m).Quota - have[m]; n > 0; n--)
                    {
                        int cell = seq++;
                        double2 at = near
                            ? new double2((cell % (side * side) % side - side / 2) * 2.6, (cell % (side * side) / side - side / 2) * 2.6)
                            : new double2(Math.Cos(2 * Math.PI * (cell % 240) / 240.0) * (70.0 + 10.0 * (cell / 240 % 3)), Math.Sin(2 * Math.PI * (cell % 240) / 240.0) * (70.0 + 10.0 * (cell / 240 % 3)));
                        k.SpawnPatch(m, at, 1f, 1f, 30f);
                    }
                }
                if (!_maxWorldDone)
                {
                    // 世界源（液洼）在 110 米外一圈常驻，只在开场放一次
                    _maxWorldDone = true;
                    for (int w = 0; w < 96; w++)
                    {
                        double ang = 2 * Math.PI * w / 96.0;
                        k.SpawnPatch(EmgMatterConst.Water, new double2(Math.Cos(ang) * 110.0, Math.Sin(ang) * 110.0), 1f, 1f, 1e5f, 0, true);
                    }
                }
            }
            for (int f = k.FieldCount; f < 64; f++)
            {
                k.SpawnField(EmgFieldConst.PortCorona, (f % 8 - 4) * 9.0, (f / 8 - 4) * 9.0, 3f, 3f, 0, CombatFaction.Player);
            }
            CombatKernel h = sites[1].Kernel;
            for (int n = 128 - h.PatchCount; n > 0; n--)
            {
                int cell = seq++ % 144;
                h.SpawnPatch(mats[seq % mats.Length], new double2((cell % 12 - 6) * 3.0, (cell / 12 - 6) * 3.0), 1f, 1f, 8f);
            }
        }

        /// <summary>满载场景机器的接战距离：守在炮塔圈内原地开火（接战距离 6 米时 120 台机器会冲出炮塔圈去贴突袭者，双方弹道都缩成几米，弹幕规模掉一半）。</summary>
        private const float LoadEngage = 30f;

        private static long Processed(CombatSite[] sites)
        {
            long n = 0;
            foreach (CombatSite s in sites)
            {
                n += s.Kernel.EmgBusStats.Processed;
            }
            return n;
        }

        /// <summary>满载事件负载（FGR-EMG-203“事件持续 512 / 步”、ADR ⑬ 入队量表“常态约 150、峰值 512 条 / 步”）：远征每步 150 条、每秒一次 512 条峰值，家园按机器数折算每步 25 条。</summary>
        private const int LoadEventsPerStep = 150, LoadEventsPeak = 512, HomeEventsPerStep = 25;

        /// <summary>
        /// 满载场景的事件负载：机器自然出手每步只入队约 2 条（固定装配里带事件读端的行少，满场弹体多数来自不带程序的炮塔 / 突袭者），远不到 ⑬ 的估算，
        /// 所以在两步之间经内核注入口按宿主模板补足——宿主是满载机器程序里真实读这个事件码的节点（<see cref="AddEventHosts"/>），入队、按 Seq 分派、唤起读端与子载荷配对都走真实总线。
        /// 返回远征地点本步注入的条数。程序为空（emg.stage = 0）时没有宿主、不注入，所以“涌现新增”包含总线开销。
        /// </summary>
        private static int InjectLoad(CombatSite[] sites, List<EmgEvent>[] hosts, int step, ref int cursor)
        {
            int injected = 0;
            for (int s = 0; s < sites.Length; s++)
            {
                List<EmgEvent> h = hosts[s];
                int count = s == 0 ? ((_maxLoad & MaxEvents) != 0 || step % 60 == 30 ? LoadEventsPeak : LoadEventsPerStep) : s == 1 ? HomeEventsPerStep : 0; // 加满场景：远征每步 512（FGR-EMG-203“事件持续 512 / 步”）
                for (int j = 0; j < count && h.Count > 0; j++)
                {
                    bool ok = sites[s].Kernel.InjectEmgEvent(h[cursor++ % h.Count]);
                    injected += s == 0 && ok ? 1 : 0;
                }
            }
            return injected;
        }

        /// <summary>程序里每个节点读的每个事件码各登记一条事件模板（宿主 = 该节点，实体 = 用这套装配的机器，Chain = 0 即每条开新链，与热更层注入同口径）。</summary>
        private static void AddEventHosts(CombatKernel k, int prog, int unit, List<EmgEvent> hosts)
        {
            if (!k.TryGetProgram(prog, out EmgProgram p) || !k.TryGetUnit(unit, out CombatUnitView v))
            {
                return;
            }
            for (int n = 0; n < p.NodeCount; n++)
            {
                if (!k.TryGetEmgNode(p.FirstNode + n, out EmgNode node))
                {
                    continue;
                }
                for (int i = node.FirstInstr; i < node.FirstInstr + node.InstrCount; i++)
                {
                    if (k.TryGetEmgInstr(i, out EmgInstr x) && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead
                        && x.Port > EmgConst.EventPortBase && x.Port <= EmgConst.EventPortBase + EmgConst.EventKinds)
                    {
                        hosts.Add(new EmgEvent { Kind = (EmgEventKind)(x.Port - EmgConst.EventPortBase), EntKind = EmgEntKind.Unit, Ent = unit, Prog = prog, Node = (byte)n, Pos = v.Position });
                    }
                }
            }
        }

        /// <summary>每台机器攻击一个活着的敌人（按机器序号错开分配，目标死了换下一个）。</summary>
        private static void OrderAttacks(CombatSite site, List<int> machines, int salt)
        {
            if (machines.Count == 0)
            {
                return;
            }
            CombatKernel k = site.Kernel;
            var alive = new List<(int Id, double2 Pos)>();
            for (int s = 0; s < k.SlotCount; s++)
            {
                CombatUnitView v = k.ViewAt(s);
                if (v.Faction == CombatFaction.Hostile && k.IsAlive(v.Id))
                {
                    alive.Add((v.Id, v.Position));
                }
            }
            if (alive.Count == 0)
            {
                return;
            }
            for (int i = 0; i < machines.Count; i++)
            {
                if (k.TryGetUnit(machines[i], out CombatUnitView mv) && mv.CommandTarget > 0 && k.IsAlive(mv.CommandTarget))
                {
                    continue;
                }
                (int id, double2 pos) = alive[(i * 7 + salt) % alive.Count];
                k.IssueCommand(machines[i], CombatCommandKind.Attack, pos, id, 0.5f, LoadEngage, 0.5f, false);
            }
        }

        /// <summary>按满载装配轮流编出机器（真实蓝图 → 接入编译 → CombatSite.WithProgram），放在炮塔圈内侧，接玩家的攻击命令（Commanded，FGR-BASE-020）。</summary>
        private static List<int> SpawnLoadMachines(CombatSite site, int count, float radius, List<EmgEvent> hosts)
        {
            var weapons = new int[LoadLoadouts.Length];
            var progs = new int[LoadLoadouts.Length];
            var loadW = new CombatWeapon[LoadLoadouts.Length];
            for (int i = 0; i < LoadLoadouts.Length; i++)
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, LoadLoadouts[i].Comp, null, null, Array.Empty<string>());
                board.TrySetUplink(UplinkSlot);
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, LoadLoadouts[i].Uplink);
                CombatWeapon w = site.WithProgram(p, CombatSite.MachineWeaponFrom(p));
                weapons[i] = site.WeaponIndex(w);
                loadW[i] = w;
                progs[i] = w.Program;
            }
            int bay8 = -1;
            if ((_maxLoad & MaxDrones) != 0 && count >= 100)
            {
                // 加满（FGR-EMG-203 无人机 512）：远征 120 台里 72 台换成无人机舱、每台挂满 8 架（CombatConst.MaxDronesPerOwner）。
                // shortcut: 架数直接写进满载装配的读法（正式内容里机舱 2 架 + 固件加架最多 5～6 架），只用于量容量满额时的开销；真实打法的无人机规模随 FG15-SYS-02 实机复测
                int bi = Array.FindIndex(LoadLoadouts, l => l.Comp == ComponentCatalog.CompDroneBayId);
                CombatWeapon bw = loadW[bi];
                bw.Reading.Drones = CombatConst.MaxDronesPerOwner;
                bay8 = site.WeaponIndex(bw);
            }
            var ids = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                double ang = 2 * Math.PI * i / count;
                var at = new double2(Math.Cos(ang) * radius, Math.Sin(ang) * radius);
                ids.Add(site.Kernel.Spawn(new CombatSpawn
                {
                    ExtKey = 0, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.ExternalHealth,
                    Position = at, Home = at, Radius = 0.6f, Speed = 4f, Health = 400f, MaxHealth = 400f, Weapon = bay8 >= 0 && i % 5 < 3 ? bay8 : weapons[i % weapons.Length], BehaviorProfile = -1, Priority = 1,
                }));
            }
            for (int i = 0; i < LoadLoadouts.Length && i < ids.Count; i++)
            {
                AddEventHosts(site.Kernel, progs[i], ids[i], hosts);
            }
            return ids;
        }

        private static int AddWrecks(CombatSite site, int count, float radius)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                double ang = 2 * Math.PI * i / count + 0.37;
                double r = radius * (0.35 + 0.65 * ((i * 7919) % 101) / 100.0);
                if (site.Kernel.TryAddWreck(new EmgWreck { Pos = new double2(Math.Cos(ang) * r, Math.Sin(ang) * r), Radius = 0.5f, Hp = 400f, MaxHp = 400f, Until = 1e9 }, out _))
                {
                    n++;
                }
            }
            return n;
        }

        // ═════════════════════════════════ 报告 ═════════════════════════════════

        private static string EvidenceDir()
        {
            var d = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "production", "qa", "evidence");
                if (Directory.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>FGR-EMG-222 报告：新颖度最高的 20 个组合、死指令清单、帧耗最高的 20 个用例、各事件 / 交互分布（只给策划参考，不自动改表）。</summary>
        private static string WriteReport(string file, string title, List<CaseResult> results)
        {
            var corpus = new HashSet<string>(StringComparer.Ordinal);
            foreach (CaseResult r in results)
            {
                r.Novelty = r.Keys.Count(k => !corpus.Contains(k)) + 2 * (r.DepthCut > 0 ? 1 : 0);
                corpus.UnionWith(r.Keys);
            }
            var sb = new StringBuilder();
            sb.AppendLine($"# 组合模糊测试器报告（{title}）");
            sb.AppendLine();
            sb.AppendLine($"- 生成：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；{results.Count} 例，失败 {results.Count(r => r.Failed)} 例；平庸 {results.Count(r => r.Plain)} 例；本机 {SystemInfo.processorType}");
            sb.AppendLine();
            sb.AppendLine("## 失败");
            foreach (CaseResult r in results.Where(r => r.Failed))
            {
                string evts = string.Join(" ", Enumerable.Range(1, EmgConst.EventKinds).Where(e => r.Events[e] > 0).Select(e => $"{(EmgEventKind)e}×{r.Events[e]}"));
                sb.AppendLine($"- {r.Case.Text}：{r.Problems}（本例事件：{(evts.Length == 0 ? "无" : evts)}；子载荷 {r.SubSpawned}；{r.Diag}）");
            }
            sb.AppendLine();
            sb.AppendLine("## 死指令清单（固件 × 载体：死 / 出现）");
            foreach (var g in results.SelectMany(r => r.Ordinals).GroupBy(o => (o.Fw, o.Carrier)).Where(g => g.Any(o => !o.Active)).OrderByDescending(g => g.Count(o => !o.Active)))
            {
                sb.AppendLine($"- {g.Key.Fw} × {g.Key.Carrier}：{g.Count(o => !o.Active)} / {g.Count()}");
            }
            sb.AppendLine();
            // EMG3-FW-02（卡片“新固件新颖度进 FGT-EMG-070 报告”）：阶段 3 落表的 12 条各出现在几例、所在例的最高新颖度
            sb.AppendLine("## 阶段 3 新固件（EMG3-FW-02，N01～N08、N30～N32、N42）");
            foreach (string fw in Stage3Firmware)
            {
                var hit = results.Where(r => r.Ordinals.Any(o => o.Fw == fw) || r.CondFw.Contains(fw)).ToList();
                sb.AppendLine($"- {fw}：出现 {hit.Count} 例（其中条件型零反应 {results.Count(r => r.CondFw.Contains(fw))} 例）、死指令 {results.SelectMany(r => r.Ordinals).Count(o => o.Fw == fw && !o.Active)} 次、所在例新颖度最高 {(hit.Count > 0 ? hit.Max(r => r.Novelty) : 0)}");
            }
            sb.AppendLine();
            sb.AppendLine("## 新颖度前 20");
            foreach (CaseResult r in results.OrderByDescending(r => r.Novelty).ThenBy(r => r.Case.Index).Take(20))
            {
                sb.AppendLine($"- {r.Novelty}：{r.Case.Text}（{string.Join(" ", r.Keys.OrderBy(k => k, StringComparer.Ordinal))}）");
            }
            sb.AppendLine();
            sb.AppendLine("## 帧耗前 20（内核步 p99 / 单步墙钟最大，ms）");
            foreach (CaseResult r in results.OrderByDescending(r => r.P99Ms).Take(20))
            {
                sb.AppendLine($"- {r.P99Ms:F3} / {r.MaxMs:F2}：{r.Case.Text}");
            }
            sb.AppendLine();
            sb.AppendLine("## 事件分布（累计入队条数）");
            for (int e = 1; e <= EmgConst.EventKinds; e++)
            {
                long sum = results.Sum(r => r.Events[e]);
                if (sum > 0)
                {
                    sb.AppendLine($"- {(EmgEventKind)e}：{sum}（{results.Count(r => r.Events[e] > 0)} 例）");
                }
            }
            sb.AppendLine("- 规则触发分布（稳定键:次数）：" + string.Join(" ", results.SelectMany(r => r.RuleHits).GroupBy(kv => kv.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Sum(kv => kv.Value)}")));
            AppendResidentReport(sb, results);
            string dir = EvidenceDir();
            if (dir == null)
            {
                return null;
            }
            string path = Path.Combine(dir, file);
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        // ═════════════════════════════════ 工具 ═════════════════════════════════

        private static string Detail(IEnumerable<string> bad)
        {
            var l = bad.ToList();
            return l.Count == 0 ? string.Empty : "：" + string.Join("；", l.Take(8)) + (l.Count > 8 ? $"……共 {l.Count} 条" : string.Empty);
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

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
