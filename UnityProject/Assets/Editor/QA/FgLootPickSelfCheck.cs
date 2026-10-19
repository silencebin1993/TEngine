using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-LOOT-02 三选一与保底（FG08 FGR-LOOT-020 / 021 / 022；FG13 FGU-34；验收 FGT-EXP-006“三选一：生成、处理时机、没选的转为技术数据、保底触发、数据包不丢”）的自检——起真实系统断言行为：
    /// A 数据（源数据 = 运行时表、权重 / 保底初值、文本中英、图鉴、钩子、坏数据整行拒绝）；
    /// B 生成（野外精英被机器击毁 → 一个数据包、3 个不重复候选、不弹面板只有顶栏计数；杂兵不生成；候选按种子确定）；
    /// C 稀有度分布与同包不重复（大样本）、保底（连续 5 个没有稀有 → 下一个必有稀有位，真实生成序列里从不出现 6 连无稀有）；
    /// D 处理时机（野外运行中不能分拣、被拒不改任何东西；战略暂停中可以，装进货舱；在家园直接入库）；
    /// E 没选的转为技术数据（经济账本、统计面板来源）、选择记录、“新”标记、对比文字；各种候选的两种去向（加密固件 → 未破解芯片、原型刻录数据 → 已破解、模块 / 数据核心 → 解析身份、材料 → 仓库）；
    /// F 负向（货舱满：拒绝、不塞、不丢、同队有空的机器接手、丢弃后能选；仓库满；重复分拣 / 坏序号；杂兵与攻城精英不生成）；
    /// G 数据包不会丢（携带者阵亡 → 同队机器；全灭 → 留在原地、两个游戏日后还在、走近捡回；货舱物品掉在原地带身份）；
    /// H 远征卸货（规则开着入库、关着留在货舱）；I 真文件存读档；J 暂停与 0.5x～3x、观察与否一致；K 界面（面板 / 顶栏 / 探针 / 中英）；L 种子集；M 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgLootPickSelfCheck）；单段：bash tools/unity-validate.sh --segment FgLootPickSelfCheck。
    /// </summary>
    public static class FgLootPickSelfCheck
    {
        private const int Slot = 9;
        private const int RunSlot = 10;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "LootPickPanel.uxml";
        private const string RowUxml = UiKitFolder + "LootPickRow.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static int _eliteKey = 20_000;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 三选一与保底")]
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
            PerfLines.Clear();
            Line("\n[三选一与保底] 待分拣数据包的生成、处理时机、没选的转为技术数据、稀有度与保底、数据包不丢、货舱 / 仓库满、存读档、倍速（FG8-LOOT-02）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fglootpick-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                AnalysisCatalog.Reload();
                LootCatalog.Reload();
                LootPickCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                LootPickService.ResetSessionState();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckGeneration);
                Step(CheckDistributionAndPity);
                Step(CheckTimingAndCargo);
                Step(CheckDepotDelivery);
                Step(CheckNegative);
                Step(CheckNeverLost);
                Step(CheckUnload);
                Step(CheckSaveLoad);
                Step(CheckSpeedAndObservation);
                Step(CheckUi);
                Step(CheckSeedSet);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                LootPickCatalog.ResetForTests();
                LootCatalog.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                LootPickPanelUIToolkit.InWorldOverrideForTests = false;
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  三选一与保底：全部 {_pass} 项通过" : $"  三选一与保底：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata as d;import fgdata_lootpick as p;print('\\n'.join(p.dump_lines(d.LOOT_PICKS)))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("LP\t")).ToList();
            var rt = t.TbLootPick.DataList.Select(r => string.Join("\t", "LP", r.Id, r.Pool, r.Kind, r.Item, r.Target, r.Min, r.Max, r.Tier, r.Weight, r.SortOrder)).ToList();
            bool table = code == 0 && src.Count > 0 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            Expect(table && LootPickCatalog.Problems.Count == 0 && LootPickCatalog.Rows.Count == rt.Count,
                $"A1 源数据 fgdata_lootpick.py 与运行时 fg.TbLootPick 逐字段一致（{rt.Count} 行），运行时检查全部通过、没有被拒绝的行" +
                (table ? string.Empty : $"（源 {src.Count} / 运行时 {rt.Count}，python 退出码 {code}）"));

            float[] w = Enumerable.Range(0, 4).Select(LootPickCatalog.Weight).ToArray();
            var factions = new[] { "silent", "foundry", "clarity", "overclock" };
            var pool = new List<LootPickDef>();
            var lines = new List<string>();
            bool pools = true;
            foreach (string f in factions)
            {
                LootPickCatalog.PoolFor(f, pool);
                int fw = pool.Count(r => r.Kind == LootPickCatalog.KindFirmware && FirmwareKinds.FactionOf(r.Target) == f);
                int foreignFw = pool.Count(r => (r.Kind == LootPickCatalog.KindFirmware || r.Kind == LootPickCatalog.KindCracked) && FirmwareKinds.FactionOf(r.Target) != f);
                int rare = pool.Count(r => r.TierIndex >= LootPickCatalog.RareIndex);
                int any = pool.Count(r => r.Pool == LootPickCatalog.PoolAny);
                pools &= fw > 0 && foreignFw == 0 && rare > 0 && any > 0 && pool.Count(r => r.TierIndex == 0) >= 3;
                lines.Add($"{f}：{pool.Count} 行（本阵营固件 {fw}、稀有及以上 {rare}、通用 {any}）");
            }
            Expect(w.SequenceEqual(new[] { 60f, 30f, 9f, 1f }) && LootPickCatalog.Pity == 5 && LootPickCatalog.CandidateCount == 3 && pools,
                $"A2 FGR-LOOT-021：稀有度权重 普通 {w[0]} / 精良 {w[1]} / 稀有 {w[2]} / 原型 {w[3]}、保底 {LootPickCatalog.Pity} 包、每包 {LootPickCatalog.CandidateCount} 个候选；" +
                $"候选池 = 该阵营池 + 通用池（不混进别的阵营的固件），每个阵营都有稀有及以上：{string.Join("；", lines)}");

            var keys = new List<string>
            {
                "loot.pick.hud", "loot.pick.title", "loot.pick.reason.not_now", "loot.pick.reason.ground", "loot.pick.pause", "loot.pick.where.depot", "loot.pick.where.cargo", "loot.pick.reason.go_home", "loot.pick.store", "loot.pick.stored", "loot.pick.store.not_home",
                "loot.pick.hud.both", "loot.pick.hud.held", "loot.pick.hud.held_tip",
                "loot.pick.meta", "loot.pick.new", "loot.pick.cmp.fw", "loot.pick.cmp.module", "loot.pick.cmp.core", "loot.pick.cmp.item", "loot.pick.choose", "loot.pick.leftover",
                "loot.pick.full", "loot.pick.depot_full", "loot.pick.done.depot", "loot.pick.done.cargo", "loot.pick.discard", "loot.pick.empty", "loot.pick.footer",
                "loot.pick.got", "loot.pick.transfer", "loot.pick.dropped", "loot.pick.claimed", "loot.pick.home", "loot.rarity.common", "loot.rarity.fine",
                "loot.rarity.rare", "loot.rarity.prototype", "stats.research.src.loot", "codex.expedition.pick.title", "codex.expedition.pick.body", "codex.expedition.pick.hint",
            };
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrWhiteSpace(zh) || !GameText.TryGet(k, GameLanguage.En, out string en)
                                          || string.IsNullOrWhiteSpace(en)).ToList();
            bool codex = t.TbCodexEntry.GetOrDefault("codex.expedition.pick") != null;
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.LootFirstPacket) && GuidanceHooks.Known.Contains(GuidanceHooks.LootFirstPick)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.LootFirstPacketDropped);
            bool notify = t.TbNotifyType.GetOrDefault(LootPickService.NotifyType) != null;
            Expect(missing.Count == 0 && codex && hooks && notify && TechDataFlow.IncomeSources.Contains(TechDataFlow.Loot),
                $"A3 文本键中英齐全（{keys.Count} 个）、图鉴“三选一数据包”、三个引导钩子、通知类型 {LootPickService.NotifyType}、统计面板技术数据来源“三选一没选的候选”" +
                (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing)));

            var bad = new List<(string What, LootPickDef Def, string Why)>();
            void Bad(string what, string pool0, string kind, string item, string target, int min, int max, string tier, int weight)
            {
                LootPickDef d = LootPickCatalog.Validate("x", pool0, kind, item, target, min, max, tier, weight, 1, out string why);
                bad.Add((what, d, why));
            }
            Bad("候选池不认识", "pirates", "item", "metal_ore", "none", 1, 2, "common", 1);
            Bad("稀有度不认识", "*", "item", "metal_ore", "none", 1, 2, "legendary", 1);
            Bad("物品不存在", "*", "item", "zz_no_item", "none", 1, 2, "common", 1);
            Bad("流体不能当候选", "*", "item", "water", "none", 1, 2, "common", 1);
            Bad("普通物品是敌方加密", "*", "item", AnalysisCatalog.UnparsedModuleId, "none", 1, 1, "common", 1);
            Bad("固件身份不在固件表", "silent", "firmware", AnalysisCatalog.EncryptedFirmwareId, "zz_fw", 1, 1, "rare", 1);
            Bad("模块身份是固件", "silent", "module", AnalysisCatalog.UnparsedModuleId, "fw_vortex", 1, 1, "fine", 1);
            Bad("数据核心身份不是 lore", "*", "core", AnalysisCatalog.DataCoreId, "none", 1, 1, "fine", 1);
            Bad("带身份的一次多件", "*", "core", AnalysisCatalog.DataCoreId, "lore", 1, 2, "fine", 1);
            Bad("件数 / 权重不合法", "*", "item", "metal_ore", "none", 3, 2, "common", 0);
            Bad("种类不认识", "*", "blueprint", "metal_ore", "none", 1, 1, "common", 1);
            LootPickDef good = LootPickCatalog.Validate("x", "silent", "module", AnalysisCatalog.UnparsedModuleId, GameLogic.Campaign.Content.ComponentCatalog.FuncMarkerId, 1, 1, "fine", 1, 1, out _);
            Expect(bad.All(b => b.Def == null && !string.IsNullOrEmpty(b.Why)) && good != null,
                $"A4 坏数据整行拒绝并写明原因：{string.Join("、", bad.Select(b => b.What))}；合法行通过");
        }

        // ── B 生成 ──────────────────────────────────────────────────────────────

        private static void CheckGeneration()
        {
            CampaignState s = NewWorld(8201);
            Vector2 field = FieldPoint(s, 70f, 0);
            int m = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            CombatSite site = WorldSimulation.Home.Combat;
            RaidUnitDef silent = UnitOf("silent");
            int before = LootPickService.Count(s);
            KillWith(site, SpawnHostile(site, field + new Vector2(3f, 0f), silent, false), m);
            bool minionNone = LootPickService.Count(s) == before;
            int cue0 = FeedbackCues.CountOf(FeedbackCueId.Pickup);
            KillWith(site, SpawnHostile(site, field + new Vector2(3f, 1f), silent, true), m);
            LootPacketState st = LootPickService.StateOf(s);
            LootPackageRecord p = st.Packets.LastOrDefault();
            var keys = p?.Candidates.Select(c => c.Kind + "|" + c.ItemId + "|" + (c.Kind == LootPickCatalog.KindItem ? "" : c.TargetId)).ToList() ?? new List<string>();
            bool cands = p != null && p.Candidates.Length == 3 && keys.Distinct().Count() == 3
                         && p.Candidates.All(c => LootPickCatalog.Find(c.RowId) is LootPickDef d && (d.Pool == "*" || d.Pool == "silent") && c.Amount >= d.Min && c.Amount <= d.Max);
            bool holder = p != null && p.Holder == m && p.Source == LootPickService.SourceElite && p.Faction == "silent";
            Expect(minionNone && cands && holder && LootPickService.Count(s) == before + 1 && !LootPickPanelUIToolkit.IsOpen
                   && FeedbackCues.CountOf(FeedbackCueId.Pickup) > cue0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstPacket),
                $"B1 FGR-LOOT-010 / 020 生成：野外杂兵被击毁不生成（{minionNone}）；静默精英被机器击毁 → 一个待分拣数据包（{p?.PacketId}），3 个候选互不重复、都来自“静默池 + 通用池”" +
                $"（{string.Join(" / ", p?.Candidates.Select(c => c.RowId + "×" + c.Amount + "·" + c.Tier) ?? Array.Empty<string>())}），携带者 = 击毁它的机器（{holder}：携带 {p?.Holder} / 机器 {m}、内核单位 {Unit(site, m)}、来源 {p?.Source}、阵营 {p?.Faction}）；" +
                "不弹面板（只有一条字幕与顶栏计数）、第一次得到数据包的引导钩子");

            // 炮塔 / 不明来源击毁：交给附近的机器；附近没有机器：留在原地。
            int near = Spawn(s, field + new Vector2(10f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            KillWith(site, SpawnHostile(site, field + new Vector2(11f, 0f), silent, true), 0);
            LootPackageRecord q = st.Packets.Last();
            Vector2 far = FieldPoint(s, 140f, 3);
            KillWith(site, SpawnHostile(site, far, silent, true), 0);
            LootPackageRecord g = st.Packets.Last();
            Expect(q.Holder == near && g.Holder == 0 && Vector2.Distance(new Vector2(g.X, g.Y), far) < 3f,
                $"B2 不是机器打死的精英：数据包交给 {LootPickCatalog.ShareRadius} 格内最近的己方机器（{q.Holder} = {near}）；附近没有机器时留在原地（{g.Holder}，({g.X:0},{g.Y:0})）");

            // 确定性：同一世界种子、同一序号 → 同一组候选。
            List<LootCandidateRecord> r1 = LootPickService.RollCandidates(4242u, "foundry", 17, false);
            List<LootCandidateRecord> r2 = LootPickService.RollCandidates(4242u, "foundry", 17, false);
            List<LootCandidateRecord> r3 = LootPickService.RollCandidates(4242u, "foundry", 18, false);
            string S(List<LootCandidateRecord> l) => string.Join(",", l.Select(c => c.RowId + "x" + c.Amount + ":" + c.TargetId));
            Expect(S(r1) == S(r2) && S(r1) != S(r3) && r1.Count == 3,
                $"B3 候选按（世界种子, 第几个数据包, 候选位）确定：同一序号两次相同（{S(r1)}），下一个序号不同（{S(r3)}）");
        }

        // ── C 分布与保底 ─────────────────────────────────────────────────────────

        private static void CheckDistributionAndPity()
        {
            var counts = new int[4];
            int dupPackets = 0;
            int n = 4000;
            foreach (string f in new[] { "silent", "foundry" })
            {
                for (int i = 1; i <= n / 2; i++)
                {
                    List<LootCandidateRecord> c = LootPickService.RollCandidates(9001u, f, i, false);
                    foreach (LootCandidateRecord x in c)
                    {
                        counts[LootPickCatalog.TierIndexOf(x.Tier)]++;
                    }
                    if (c.Select(x => x.Kind + x.ItemId + (x.Kind == LootPickCatalog.KindItem ? "" : x.TargetId)).Distinct().Count() != c.Count || c.Count != 3)
                    {
                        dupPackets++;
                    }
                }
            }
            double total = counts.Sum();
            double[] pct = counts.Select(x => x * 100.0 / total).ToArray();
            bool dist = Math.Abs(pct[0] - 60) < 2.5 && Math.Abs(pct[1] - 30) < 2.5 && Math.Abs(pct[2] - 9) < 1.2 && Math.Abs(pct[3] - 1) < 0.5;
            Expect(dist && dupPackets == 0,
                $"C1 FGR-LOOT-021 稀有度按权重抽取：{n} 个数据包 {total} 个候选位——普通 {pct[0]:0.0}% / 精良 {pct[1]:0.0}% / 稀有 {pct[2]:0.0}% / 原型 {pct[3]:0.00}%（初值 60 / 30 / 9 / 1）；" +
                $"同一个数据包内不重复（违例 {dupPackets} 包）");

            List<LootCandidateRecord> forced = LootPickService.RollCandidates(9001u, "clarity", 77, true);
            bool forcedOk = Enumerable.Range(1, 300).All(i => LootPickService.RollCandidates(9001u, "overclock", i, true).Any(x => LootPickCatalog.IsRareOrBetter(x.Tier)));
            Expect(forced.Any(x => LootPickCatalog.IsRareOrBetter(x.Tier)) && forcedOk,
                "C2 保底包的候选：300 个保底包每一个都至少有一个稀有 / 原型候选（overclock 池最小也成立）");

            // 真实生成序列：连续 5 个没有稀有 → 第 6 个必有一个稀有位；从不出现 6 连无稀有。
            CampaignState s = NewWorld(8202);
            int m = Spawn(s, FieldPoint(s, 6f, 0), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            LootPacketState st = LootPickService.StateOf(s);
            int streak = 0, maxStreak = 0, pityPackets = 0, pityOk = 0, wrongFlag = 0;
            for (int i = 0; i < 400; i++)
            {
                int pre = st.NoRareStreak;
                LootPackageRecord p = LootPickService.Grant(s, LootPickService.SourceElite, i % 2 == 0 ? "silent" : "foundry", m, HomeValleyLayout.RegionId, Vector2.zero);
                bool rare = p.Candidates.Any(x => LootPickCatalog.IsRareOrBetter(x.Tier));
                if (p.Pity)
                {
                    pityPackets++;
                    pityOk += rare ? 1 : 0;
                }
                wrongFlag += p.Pity != (pre >= 5) ? 1 : 0;
                streak = rare ? 0 : streak + 1;
                maxStreak = Math.Max(maxStreak, streak);
            }
            Expect(maxStreak <= 5 && pityPackets > 0 && pityOk == pityPackets && wrongFlag == 0 && st.PityHits == pityPackets,
                $"C3 保底（FGR-LOOT-021）：真实生成 400 个数据包，最长连续无稀有 {maxStreak} 个（≤ 5）；保底触发 {pityPackets} 次、每次都有稀有位（{pityOk}）、只在连续 5 个之后触发（误触发 {wrongFlag}）");
        }

        // ── D 处理时机与货舱 ─────────────────────────────────────────────────────

        private static void CheckTimingAndCargo()
        {
            CampaignState s = NewWorld(8203);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 1);
            int hauler = Spawn(s, field + new Vector2(0f, 2f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int fighter = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { fighter, hauler }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            CombatSite site = WorldSimulation.Home.Combat;
            KillWith(site, SpawnHostile(site, Pos(fighter) + new Vector2(2f, 0f), UnitOf("foundry"), true), fighter);
            LootPacketState st = LootPickService.StateOf(s);
            LootPackageRecord p = st.Packets.Last();
            long tech0 = (long)s.TechData;
            int inv0 = InventoryTotal(s);
            bool can = LootPickService.CanProcess(s, p, out string why, out bool atDepot);
            LootPickService.PickResult r = LootPickService.TryPick(s, p.PacketId, 0);
            Expect(dep.Success && ExpeditionMarchService.IsTeamMember(s, fighter) && !can && why == "loot.pick.reason.not_now" && !atDepot && !r.Success
                   && LootPickService.Find(s, p.PacketId) != null && (long)s.TechData == tech0 && InventoryTotal(s) == inv0 && st.Held.Length == 0,
                $"D1 FGR-LOOT-020 处理时机：远征队在野外运行中（携带者 {Name(fighter)} 不在家园 / 带仓库的前哨站，也没暂停）不能分拣——“{GameText.Get(why ?? "")}”；" +
                "强行选被拒，数据包、技术数据、仓库、货舱都不变");

            GameClock.SetPaused(true);
            bool canPaused = LootPickService.CanProcess(s, p, out _, out bool depot2);
            int idx = SmallestIndex(p);
            LootCandidateRecord chosen = p.Candidates[idx];
            int expectTech = p.Candidates.Where((c, i) => i != idx).Sum(c => LootPickCatalog.TechFor(c.Tier));
            long flow0 = TechDataFlow.IncomeOf(s, TechDataFlow.Loot);
            LootPickService.PickResult ok = LootPickService.TryPick(s, p.PacketId, idx);
            LootHeldRecord h = st.Held.FirstOrDefault(x => x.HeldId == LootPickService.HeldPrefix + p.PacketId);
            bool techOk = (long)s.TechData == tech0 + expectTech && TechDataFlow.IncomeOf(s, TechDataFlow.Loot) == flow0 + expectTech
                          && TechDataFlow.ClassifyTransaction(LootPickService.TxPrefix + p.PacketId) == TechDataFlow.Loot;
            LootPickLogRecord log = st.Picks.LastOrDefault();
            bool seen = p.Candidates.All(c => !LootPickService.IsNew(s, c));
            Expect(canPaused && !depot2 && ok.Success && ok.Destination == LootPickService.DestCargo && h != null && (h.Machine == fighter || h.Machine == hauler)
                   && h.ItemId == chosen.ItemId && h.Space == LootPickService.SpaceOf(chosen) && LootPickService.Find(s, p.PacketId) == null && techOk
                   && log != null && log.PacketId == p.PacketId && log.ChosenRowId == chosen.RowId && log.Destination == LootPickService.DestCargo && log.TechData == expectTech
                   && seen && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstPick) && InventoryTotal(s) == inv0,
                $"D2 战略暂停中在野外分拣：选第 {idx + 1} 个（{LootPickService.CandidateName(chosen)}，占 {LootPickService.SpaceOf(chosen)}）→ 装进 {Name(h?.Machine ?? 0)} 的货舱（不入库）；" +
                $"没选的两个转为技术数据 +{expectTech}（经济账本、统计面板“三选一没选的候选” +{TechDataFlow.IncomeOf(s, TechDataFlow.Loot) - flow0}）；选择记录一条；三个候选都记为见过（不再标“新”）；第一次分拣钩子");
            GameClock.SetPaused(false);
        }

        // ── E 在家园直接入库：各种候选的两种去向 ─────────────────────────────────

        private static void CheckDepotDelivery()
        {
            List<LootPickDef> all = LootPickCatalog.Rows.ToList();
            string fwRaw = all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindFirmware).Target;
            string fwProto = all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindCracked && r.Target != fwRaw).Target;
            var rows = new List<LootPickDef>
            {
                all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindFirmware && r.Target == fwRaw),
                all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindCracked && r.Target == fwProto),
                all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindModule),
                all.First(r => r.Kind == LootPickCatalog.KindCore),
                all.First(r => r.Id == "pick.any.metal"),
            };
            foreach (LootPickDef r in rows)
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            CampaignState s = NewWorld(8204);
            EnsureWarehouse(s);
            Vector2 home = CoreNear(s);
            int m = Spawn(s, home, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            LootPickDef filler1 = rows[4];
            LootPickDef filler2 = all.First(r => r.Id == "pick.any.alloy");
            LootPickDef filler3 = all.First(r => r.Id == "pick.any.electronic");
            filler2.TierIndex = filler3.TierIndex = 0;
            filler2.Tier = filler3.Tier = "common";
            try
            {
                var results = new List<string>();
                bool allOk = true;
                bool atDepot = false;
                for (int k = 0; k < rows.Count; k++)
                {
                    // 测试捷径：候选池只留这一种 + 两种材料（3 个候选位、3 行 → 三行都会抽到），确定地验证每一种候选的入库去向。
                    LootPickCatalog.OverrideForTests(k < 4 ? new[] { rows[k], filler2, filler3 } : new[] { filler1, filler2, filler3 });
                    string kind = rows[k].Kind;
                    LootPackageRecord p = LootPickService.Grant(s, LootPickService.SourceElite, "silent", m, HomeValleyLayout.RegionId, home);
                    int i = Array.FindIndex(p.Candidates, c => c.RowId == rows[k].Id);
                    if (i < 0)
                    {
                        allOk = false;
                        results.Add(kind + ":没抽到");
                        continue;
                    }
                    LootCandidateRecord c = p.Candidates[i];
                    atDepot = LootPickService.CanProcess(s, p, out _, out bool d) && d;
                    int chips0 = Chips(s, c.TargetId);
                    int tags0 = AnalysisTags(s, c.TargetId);
                    int stock0 = HomeInventory.Stock(s, c.ItemId);
                    LootPickService.PickResult r = LootPickService.TryPick(s, p.PacketId, i);
                    bool ok;
                    switch (kind)
                    {
                        case LootPickCatalog.KindFirmware:
                            ok = Chips(s, c.TargetId) == chips0 + 1 && FirmwareKinds.IsRaw(s, c.TargetId) && HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId) == 0;
                            break;
                        case LootPickCatalog.KindCracked:
                            ok = Chips(s, c.TargetId) == chips0 + 1 && !FirmwareKinds.IsRaw(s, c.TargetId);
                            break;
                        case LootPickCatalog.KindModule:
                        case LootPickCatalog.KindCore:
                            ok = HomeInventory.Stock(s, c.ItemId) == stock0 + 1 && AnalysisTags(s, c.TargetId) == tags0 + 1;
                            break;
                        default:
                            ok = HomeInventory.Stock(s, c.ItemId) == stock0 + c.Amount;
                            break;
                    }
                    ok &= r.Success && r.Destination == LootPickService.DestDepot && atDepot;
                    allOk &= ok;
                    results.Add($"{kind}:{(ok ? "√" : "×")}");
                }
                Expect(allOk,
                    $"E1 在家园（离核心 / 仓库 {LootPickCatalog.DepotRadius} 格内）不暂停也能分拣，选中的直接入库并按协议分两种去向：加密固件 → 固件库未破解芯片（不进仓库）、原型刻录数据 → 芯片且已破解、" +
                    $"未解析模块 / 数据核心 → 仓库 + 解析身份、材料 → 仓库（{string.Join("、", results)}）");
            }
            finally
            {
                LootPickCatalog.ResetForTests();
            }

            // “新”与对比
            CampaignState s2 = NewWorld(8205);
            EnsureWarehouse(s2);
            int m2 = Spawn(s2, CoreNear(s2), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            LootPackageRecord p2 = LootPickService.Grant(s2, LootPickService.SourceElite, "foundry", m2, HomeValleyLayout.RegionId, Vector2.zero);
            var fresh = new LootCandidateRecord { Kind = LootPickCatalog.KindItem, ItemId = "rare_earth_ore", Amount = 10, Tier = "common" };
            bool newBefore = HomeInventory.Stock(s2, "rare_earth_ore") > 0 || LootPickService.IsNew(s2, fresh);
            HomeInventory.Add(s2, ItemCatalog.Find("rare_earth_ore"), 3);
            bool ownedNotNew = !LootPickService.IsNew(s2, fresh);
            string cmp = LootPickService.CompareText(s2, fresh);
            var fwc = new LootCandidateRecord { Kind = LootPickCatalog.KindFirmware, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = "fw_wide", Amount = 1, Tier = "fine" };
            string cmpFw0 = LootPickService.CompareText(s2, fwc);
            bool fwNew0 = LootPickService.IsNew(s2, fwc);
            FieldLootService.DeliverEnemyItem(s2, AnalysisCatalog.EncryptedFirmwareId, "fw_wide", FieldLootService.OriginField, Vector2.zero, "selfcheck:pick:fw_wide");
            string cmpFw1 = LootPickService.CompareText(s2, fwc);
            Expect(newBefore && ownedNotNew && cmp.Contains("3") && fwNew0 && !LootPickService.IsNew(s2, fwc) && cmpFw0.Contains(" 0 ") && cmpFw1.Contains(" 1 ") && cmpFw1.Contains("解析台")
                   && p2.Candidates.All(c => !string.IsNullOrEmpty(LootPickService.CompareText(s2, c))),
                $"E2 必须同时交付：从没见过的标“新”（仓库没有、没分拣过 → 新；有了就不是）；候选与已有同类物品比较——材料“{cmp}”；固件拿到前“{cmpFw0}” → 拿到一枚后“{cmpFw1}”");
        }

        // ── F 负向 ──────────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            // F1 货舱满：携带者（战斗履带，容量 4）满了、同队没有空位 → 拒绝；不塞、不丢；丢弃一件后能选。
            List<LootPickDef> all = LootPickCatalog.Rows.ToList();
            var big = new List<LootPickDef>
            {
                all.First(r => r.Id == "pick.any.component"),
                all.First(r => r.Id == "pick.any.structure"),
                all.First(r => r.Pool == "silent" && r.Kind == LootPickCatalog.KindModule),
            };
            foreach (LootPickDef r in big)
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            CampaignState s = NewWorld(8206);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 2);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            int b = Spawn(s, field + new Vector2(2f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            LootPickCatalog.OverrideForTests(big);
            try
            {
                LootPacketState st = LootPickService.StateOf(s);
                // 两台都先装满（施工材料之类的货：每 10 件材料占 1 格）
                Rec(a).Cargo = new[] { new CargoEntry { ResourceType = "metal_ore", Amount = 10 * LootPickCatalog.CapacityOf(s, Rec(a)) } };
                Rec(b).Cargo = new[] { new CargoEntry { ResourceType = "metal_ore", Amount = 10 * LootPickCatalog.CapacityOf(s, Rec(b)) } };
                LootPackageRecord p = LootPickService.Grant(s, LootPickService.SourceElite, "silent", a, HomeValleyLayout.RegionId, field);
                GameClock.SetPaused(true);
                long tech0 = (long)s.TechData;
                LootPickService.PickResult full = LootPickService.TryPick(s, p.PacketId, 0);
                bool kept = LootPickService.Find(s, p.PacketId) != null && st.Held.Length == 0 && (long)s.TechData == tech0;
                Expect(dep.Success && ExpeditionMarchService.IsTeamMember(s, b) && !full.Success && full.Message.Contains("货舱放不下") && full.Message.Contains("丢弃") && kept,
                    $"F1 负向“三选一时货舱已满”：携带者已用 {LootPickService.UsedSpace(s, Rec(a))} / {LootPickCatalog.CapacityOf(s, Rec(a))}、同队另一台已用 {LootPickService.UsedSpace(s, Rec(b))} / {LootPickCatalog.CapacityOf(s, Rec(b))}，" +
                    $"候选要 3 格 → 拒绝“{full.Message}”；不塞进去、数据包留着、没发技术数据（出发 {dep.Outcome}）");

                // 同队另一台腾出空位 → 由它接手。
                Rec(b).Cargo = Array.Empty<CargoEntry>();
                LootPickService.PickResult mate = LootPickService.TryPick(s, p.PacketId, 0);
                LootHeldRecord h = st.Held.FirstOrDefault();
                if (h == null)
                {
                    Fail($"F2 携带者满了、同队 {Name(b)} 有空位时应由它接手：{mate.Message}");
                    return;
                }
                Expect(mate.Success && h != null && h.Machine == b && mate.Destination == LootPickService.DestCargo,
                    $"F2 携带者满了、同队 {Name(b)} 有空位：装进它的货舱（{mate.Message}）");

                // 丢弃：落在机器旁边（地上带身份），货舱空出来。
                int ground0 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Length;
                bool discarded = LootPickService.TryDiscard(s, h.HeldId, out string dmsg);
                GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, h.HeldId);
                Expect(discarded && st.Held.Length == 0 && g != null && (s.GroundItems?.Length ?? 0) == ground0 + 1 && Vector2.Distance(g.Position, Pos(b)) < 3f,
                    $"F3 丢弃货舱里的分拣物品：落在 {Name(b)} 旁边（{dmsg}），货舱空出来、物品不消失");
            }
            finally
            {
                GameClock.SetPaused(false);
                LootPickCatalog.ResetForTests();
            }

            // F4 仓库满：在家园直接入库时仓库放不下 → 拒绝，数据包留着。
            CampaignState s2 = NewWorld(8207);
            EnsureWarehouse(s2);
            int m = Spawn(s2, CoreNear(s2), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            LootPickCatalog.OverrideForTests(new[] { all.First(r => r.Id == "pick.any.metal"), all.First(r => r.Id == "pick.any.alloy"), all.First(r => r.Id == "pick.any.electronic") });
            try
            {
                LootPackageRecord p = LootPickService.Grant(s2, LootPickService.SourceElite, "silent", m, HomeValleyLayout.RegionId, Vector2.zero);
                LootCandidateRecord c = p.Candidates[0];
                ItemDef item = ItemCatalog.Find(c.ItemId);
                HomeInventory.Add(s2, item, HomeInventory.Space(s2, item));
                int stock = HomeInventory.Stock(s2, c.ItemId);
                LootPickService.PickResult r = LootPickService.TryPick(s2, p.PacketId, 0);
                Expect(!r.Success && r.Message.Contains("仓库放不下") && LootPickService.Find(s2, p.PacketId) != null && HomeInventory.Stock(s2, c.ItemId) == stock,
                    $"F4 负向：在家园分拣但仓库放不下 {LootPickService.CandidateName(c)} → 拒绝“{r.Message}”，数据包留着、仓库不变");

                // F5 重复分拣 / 坏序号
                LootPickService.PickResult good = LootPickService.TryPick(s2, p.PacketId, 1);
                long tech = (long)s2.TechData;
                LootPickService.PickResult again = LootPickService.TryPick(s2, p.PacketId, 2);
                LootPackageRecord p2 = LootPickService.Grant(s2, LootPickService.SourceElite, "silent", m, HomeValleyLayout.RegionId, Vector2.zero);
                LootPickService.PickResult badIdx = LootPickService.TryPick(s2, p2.PacketId, 5);
                Expect(good.Success && !again.Success && (long)s2.TechData == tech && !badIdx.Success && LootPickService.Find(s2, p2.PacketId) != null,
                    $"F5 负向：同一个数据包分拣第二次被拒（{again.Message}），不重复发技术数据；不存在的候选序号被拒（{badIdx.Message}）");
            }
            finally
            {
                LootPickCatalog.ResetForTests();
            }

            // F6 攻城中的精英不生成数据包（突袭有自己的结算掉落，FGR-DEF-051；ADR-LOOT-002 第 3 节）。
            CampaignState s3 = NewWorld(8208);
            int before = LootPickService.Count(s3);
            List<int> raiders = new List<int>();
            TransitGroupRecord grp = RaidFixture(s3, out CombatSite site3);
            if (grp != null)
            {
                raiders = RaidersOf(site3, grp).ToList();
                foreach (int u in raiders)
                {
                    site3.Kernel.Kill(u, 0);
                }
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            Expect(grp != null && raiders.Count > 0 && LootPickService.Count(s3) == before,
                $"F6 攻城中被击毁的 {raiders.Count} 台突袭精英不生成数据包（突袭按 FGR-DEF-051 自己结算掉落；数据包只来自野外精英 / 事件 / 首领）：{LootPickService.Count(s3) - before} 个");

            // F7 负向（修复轮 P1）：不在远征队里的家园机器（巡逻 / 迎战）在离核心 60 格处拿到数据包 → 暂停也不能装货舱（这类机器没有“远征卸货”路径，装进去就卸不下），
            // 写明“开回家园再分拣”、不给“暂停并分拣”；开回核心旁后照常分拣、选中的直接入库，货舱里不留东西。
            CampaignState s4 = NewWorld(8216);
            EnsureWarehouse(s4);
            Vector2 far4 = FieldPoint(s4, 60f, 3);
            int home4 = Spawn(s4, far4, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            LootPacketState st4 = LootPickService.StateOf(s4);
            LootPackageRecord p4 = LootPickService.Grant(s4, LootPickService.SourceElite, "silent", home4, HomeValleyLayout.RegionId, far4);
            GameClock.SetPaused(true);
            bool can4 = LootPickService.CanProcess(s4, p4, out string why4, out bool dep4);
            int idx4 = SmallestIndex(p4);
            LootPickService.PickResult r4 = LootPickService.TryPick(s4, p4.PacketId, idx4);
            GameClock.SetPaused(false);
            bool refused4 = !ExpeditionMarchService.IsTeamMember(s4, home4) && !can4 && !dep4 && why4 == LootPickService.ReasonGoHome && !r4.Success
                            && LootPickService.Find(s4, p4.PacketId) != null && st4.Held.Length == 0;
            MoveTo(home4, CoreNear(s4)); // 测试捷径：开回核心旁（RTS 移动命令由 FG2 覆盖）
            WorldSimulation.StepMany(2);
            LootCandidateRecord c4 = p4.Candidates[idx4];
            bool home4Ok = LootPickService.CanProcess(s4, p4, out _, out bool atHome4) && atHome4;
            LootPickService.PickResult ok4 = LootPickService.TryPick(s4, p4.PacketId, idx4);
            Expect(refused4 && home4Ok && ok4.Success && ok4.Destination == LootPickService.DestDepot && st4.Held.Length == 0,
                $"F7 负向：不在远征队里的 {Name(home4)} 在野外拿到数据包，暂停后也不能装货舱（“{GameText.Get(why4 ?? "")}”，选被拒、数据包留着、货舱不变 {refused4}）；" +
                $"开回核心旁 → 照常分拣，{LootPickService.CandidateName(c4)} 直接入库（{ok4.Message}）");
        }

        /// <summary>测试捷径：把一台机器挪到 <paramref name="at"/>（战斗内核里的位置）。</summary>
        private static void MoveTo(int logicId, Vector2 at)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(at.x, at.y));
            }
        }

        // ── G 数据包不会丢 ───────────────────────────────────────────────────────

        private static void CheckNeverLost()
        {
            CampaignState s = NewWorld(8209);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 4);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            int b = Spawn(s, field + new Vector2(3f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            CombatSite site = WorldSimulation.Home.Combat;
            KillWith(site, SpawnHostile(site, Pos(a) + new Vector2(1f, 1f), UnitOf("silent"), true), Unit(site, a) > 0 ? a : 0);
            KillWith(site, SpawnHostile(site, Pos(a) + new Vector2(1f, -1f), UnitOf("silent"), true), a);
            LootPacketState st = LootPickService.StateOf(s);
            string[] ids = st.Packets.Where(p => p.Holder == a).Select(p => p.PacketId).ToArray();
            // 一份分拣物品装在 a 的货舱里（阵亡时掉在原地）。
            GameClock.SetPaused(true);
            LootPackageRecord first = LootPickService.Find(s, ids[0]);
            int small = SmallestIndex(first);
            LootPickService.TryPick(s, ids[0], small);
            GameClock.SetPaused(false);
            LootHeldRecord held = st.Held.FirstOrDefault(h => h.Machine == a || h.Machine == b);
            string[] rest = ids.Skip(1).ToArray();
            int notify0 = NotifyCount();
            Vector2 aDeath = Pos(a);
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(3);
            bool moved = rest.All(id => LootPickService.Find(s, id)?.Holder == b);
            bool heldDropped = held == null || held.Machine != a || HomeValleyCargo.FindGroundItemBySalvageId(s, held.HeldId) != null;
            Expect(dep.Success && rest.Length >= 1 && moved && heldDropped && NotifyCount() > notify0,
                $"G1 FGR-LOOT-022：携带的机器 {Name(a)} 阵亡 → {rest.Length} 个数据包转到同队的 {Name(b)}（{moved}，通知 +{NotifyCount() - notify0}）；它货舱里分拣得到的物品掉在原地（{heldDropped}；出发 {dep.Outcome}）");

            Vector2 bDeath = Pos(b);
            MachineRegistry.ApplyDamage(b, 1e6f);
            WorldSimulation.StepMany(3);
            LootPackageRecord g = LootPickService.Find(s, rest[0]);
            bool ground = g != null && g.Holder == 0 && Vector2.Distance(new Vector2(g.X, g.Y), bDeath) < 3f;
            bool reason = !LootPickService.CanProcess(s, g, out string why, out _) && why == "loot.pick.reason.ground";
            Expect(ground && reason && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstPacketDropped),
                $"G2 负向“全灭时数据包留在原地”：同队最后一台也阵亡 → 数据包留在阵亡处 ({g?.X:0},{g?.Y:0})（{ground}），分拣时写“{GameText.Get(why ?? "")}”；第一次留在原地的引导钩子");

            int c = Spawn(s, bDeath + new Vector2(1f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int by = LootPickService.Find(s, rest[0])?.Holder ?? -1;
            Expect(by > 0 && Rec(by) != null && Rec(by).IsAlive && Vector2.Distance(Pos(by), bDeath) <= LootPickCatalog.ClaimRadius + 1f,
                $"G3 再派机器取回：走到 {LootPickCatalog.ClaimRadius} 格内的己方机器捡起地上的数据包（{Name(by)}，新派的是 {Name(c)}）");

            // 永不消失（D-13）：一个没人去的地方的数据包，过了两个游戏日还在原地。
            Vector2 lonely = FieldPoint(s, 220f, 7);
            LootPackageRecord far = LootPickService.Grant(s, LootPickService.SourceElite, "silent", 0, HomeValleyLayout.RegionId, lonely);
            long days = GameClock.TicksFor(GameClock.DaySeconds * 2.0);
            for (long t = 0; t < days; t += GameClock.StepHz * 30)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 30);
            }
            LootPackageRecord still = LootPickService.Find(s, far.PacketId);
            Expect(still != null && still.Holder == 0 && Vector2.Distance(new Vector2(still.X, still.Y), lonely) < 1f
                   && KeyItemRetention.NeverExpires(KeyItemRetention.Kind.DataPacket) && KeyItemRetention.StaysOnWipe(KeyItemRetention.Kind.DataPacket)
                   && KeyItemRetention.NeverExpires(KeyItemRetention.OfItem("reactor_core")) && !KeyItemRetention.NeverExpires(KeyItemRetention.OfItem("metal_ore")),
                "G4 永不消失：留在地上的数据包过了两个游戏日仍在原地（与关键材料同一条 D-13 规则 KeyItemRetention：数据包 / 关键材料永不过期、全灭留在原地，普通材料不豁免——FG8-EXP-05 的消失计时用同一判定）");
        }

        // ── H 远征卸货 ──────────────────────────────────────────────────────────

        private static void CheckUnload()
        {
            List<LootPickDef> all = LootPickCatalog.Rows.ToList();
            var rows = new[] { all.First(r => r.Id == "pick.any.metal"), all.First(r => r.Id == "pick.any.alloy"), all.First(r => r.Pool == "foundry" && r.Kind == LootPickCatalog.KindFirmware) };
            foreach (LootPickDef r in rows)
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            CampaignState s = NewWorld(8210);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 5);
            int a = Spawn(s, field, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            // 野外装货舱只给在外的远征队成员（修复轮 P1）。
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            LootPickCatalog.OverrideForTests(rows);
            try
            {
                LootPacketState st = LootPickService.StateOf(s);
                GameClock.SetPaused(true);
                LootPackageRecord p1 = LootPickService.Grant(s, LootPickService.SourceElite, "foundry", a, HomeValleyLayout.RegionId, field);
                int fw = Array.FindIndex(p1.Candidates, c => c.Kind == LootPickCatalog.KindFirmware);
                string target = p1.Candidates[fw].TargetId;
                LootPickService.TryPick(s, p1.PacketId, fw);
                GameClock.SetPaused(false);
                int chips0 = Chips(s, target);
                StandingRuleRecord rule = StandingRuleService.Ordered(s).FirstOrDefault(x => x.Kind == StandingRuleService.KindUnload);
                bool wasOn = rule != null && rule.Enabled;
                StandingRuleService.TrySetEnabled(s, rule.Serial, false, out _);
                StandingRuleService.OnExpeditionReturned(s, new[] { a });
                WorldSimulation.StepMany(3);
                bool stays = st.Held.Count(h => h.Machine == a) == 1 && Chips(s, target) == chips0;
                // 规则关着、机器还不在仓库旁：手动“入库”被拒，写明原因，货留在货舱里。
                LootHeldRecord h1 = st.Held.FirstOrDefault(h => h.Machine == a);
                string farMsg = string.Empty;
                bool storeFar = h1 != null && !LootPickService.CanStoreHeld(s, h1) && !LootPickService.TryStoreHeld(s, h1.HeldId, out farMsg)
                                && farMsg.Contains("仓库旁") && st.Held.Count(h => h.Machine == a) == 1;
                StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
                StandingRuleService.OnExpeditionReturned(s, new[] { a });
                WorldSimulation.StepMany(3);
                bool unloaded = st.Held.Count(h => h.Machine == a) == 0 && Chips(s, target) == chips0 + 1 && FirmwareKinds.IsRaw(s, target);
                Expect(dep.Success && wasOn && stays && storeFar && unloaded,
                    $"H1 FGR-EXP-050 回到家园按“远征卸货”规则卸下（默认开启 {wasOn}）：规则关着 → 留在货舱（{stays}，FGR-BASE-020），不在仓库旁时手动入库被拒（“{farMsg}”）；开着 → 分拣得到的加密固件按协议入固件库成为未破解芯片（{unloaded}）");

                // H2 规则关着时回来的货：机器开到仓库旁后，玩家在面板“货舱里”一栏点“入库” → 按同一协议入库（不必再等下一次远征卸货）。
                GameClock.SetPaused(true);
                LootPackageRecord p2 = LootPickService.Grant(s, LootPickService.SourceElite, "foundry", a, HomeValleyLayout.RegionId, Pos(a));
                int fw2 = Array.FindIndex(p2.Candidates, c => c.Kind == LootPickCatalog.KindFirmware);
                string target2 = p2.Candidates[fw2].TargetId;
                LootPickService.PickResult cargo2 = LootPickService.TryPick(s, p2.PacketId, fw2);
                GameClock.SetPaused(false);
                StandingRuleService.TrySetEnabled(s, rule.Serial, false, out _);
                StandingRuleService.OnExpeditionReturned(s, new[] { a });
                int chips2 = Chips(s, target2);
                MoveTo(a, CoreNear(s)); // 测试捷径：开回仓库旁
                WorldSimulation.StepMany(2);
                LootHeldRecord h2 = st.Held.FirstOrDefault(h => h.Machine == a);
                bool canStore = h2 != null && LootPickService.CanStoreHeld(s, h2);
                string storeMsg = string.Empty;
                bool stored = h2 != null && LootPickService.TryStoreHeld(s, h2.HeldId, out storeMsg) && storeMsg.Contains("已入库");
                Expect(cargo2.Destination == LootPickService.DestCargo && canStore && stored && st.Held.Count(h => h.Machine == a) == 0 && Chips(s, target2) == chips2 + 1,
                    $"H2 “远征卸货”关着时回来的货不会卡在货舱里：机器开到仓库旁 → 面板“入库”可点（{canStore}）→ 加密固件按协议入固件库（{chips2} → {Chips(s, target2)}；“{storeMsg}”）");
                StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            }
            finally
            {
                GameClock.SetPaused(false);
                LootPickCatalog.ResetForTests();
            }
        }

        // ── I 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8211);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 6);
            int a = Spawn(s, field, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins); // 野外装货舱只给远征队成员
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            CombatSite site = WorldSimulation.Home.Combat;
            for (int i = 0; i < 3; i++)
            {
                KillWith(site, SpawnHostile(site, field + new Vector2(2f, i), UnitOf("clarity"), true), a);
            }
            KillWith(site, SpawnHostile(site, FieldPoint(s, 140f, 7), UnitOf("clarity"), true), 0); // 一个留在地上
            LootPacketState st = LootPickService.StateOf(s);
            GameClock.SetPaused(true);
            LootPackageRecord p0 = st.Packets.First(p => p.Holder == a);
            LootPickService.TryPick(s, p0.PacketId, SmallestIndex(p0));
            GameClock.SetPaused(false);
            st.NoRareStreak = Math.Min(4, st.NoRareStreak + 1);
            string snap = Snapshot(s);
            long serial = st.NextPacketSerial;
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            LootPacketState st2 = back != null ? LootPickService.StateOf(back) : null;
            Expect(sv.Success && back != null && Snapshot(back) == snap && st2.Packets.Length >= 3 && st2.Held.Length == 1 && st2.Packets.Any(p => p.Holder == 0),
                $"I1 存读档：待分拣数据包（{st2?.Packets.Length} 个，含地上的）、候选、携带者、货舱里的分拣物品、选择记录、见过的候选、保底计数（{st2?.NoRareStreak}）、序号（{st2?.NextPacketSerial}）逐项一致");
            LootPackageRecord q = st2.Packets.First(p => p.Holder > 0);
            GameClock.SetPaused(true);
            long tech0 = (long)back.TechData;
            LootPickService.PickResult r = LootPickService.TryPick(back, q.PacketId, SmallestIndex(q));
            LootPickService.PickResult dup = LootPickService.TryPick(back, q.PacketId, 0);
            GameClock.SetPaused(false);
            LootPackageRecord fresh = LootPickService.Grant(back, LootPickService.SourceElite, "clarity", 0, HomeValleyLayout.RegionId, field);
            Expect(r.Success && !dup.Success && (long)back.TechData == tech0 + r.TechData && fresh.Serial == serial,
                $"I2 读档后接着分拣（技术数据 +{r.TechData}、不重复）；新数据包序号接着编（{fresh.Serial} = 存档时的 {serial}）");
        }

        // ── J 暂停 / 倍速 / 观察 ─────────────────────────────────────────────────

        private static void CheckSpeedAndObservation()
        {
            string RunAt(float speed, bool pauseMid, bool observeHome)
            {
                CampaignState s = NewWorld(8212, observeHome);
                Vector2 field = FieldPoint(s, 70f, 0);
                int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
                WorldSimulation.StepMany(3);
                CombatSite site = WorldSimulation.Home.Combat;
                GameClock.SetSpeed(speed);
                int u = SpawnHostile(site, field + new Vector2(2f, 0f), UnitOf("foundry"), true);
                site.Kernel.Damage(u, 1e7f, Unit(site, a));
                bool pausedOk = true;
                if (pauseMid)
                {
                    GameClock.SetPaused(true);
                    for (int k = 0; k < 20; k++)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    pausedOk = LootPickService.Count(s) == 0;
                    GameClock.SetPaused(false);
                }
                long target = GameClock.Ticks + GameClock.StepHz * 2;
                for (int frames = 0; GameClock.Ticks < target && frames < 20000; frames++)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                return (pausedOk ? string.Empty : "PAUSE-MOVED ") + Snapshot(s);
            }
            string r05 = RunAt(0.5f, false, true);
            string r1 = RunAt(1f, true, true);
            string r3 = RunAt(3f, false, true);
            string hidden = RunAt(1f, false, false);
            Expect(r05 == r1 && r1 == r3 && r1 == hidden && !r1.Contains("PAUSE") && r1.Contains("lp-"),
                $"J1 B09 暂停与倍速 + FGR-BASE-021：同一个精英在 0.5x / 1x（暂停 20 帧期间不生成）/ 3x、以及镜头不在家园时被击毁，数据包与候选逐项相同（{r1.Length} 字节快照）");
        }

        // ── K 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(8213);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 1);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins); // 野外装货舱只给远征队成员
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            CombatSite site = WorldSimulation.Home.Combat;
            KillWith(site, SpawnHostile(site, field + new Vector2(2f, 0f), UnitOf("silent"), true), a);
            KillWith(site, SpawnHostile(site, field + new Vector2(2f, 2f), UnitOf("silent"), true), a);
            LootPickPanelUIToolkit.InWorldOverrideForTests = true;
            var row = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml);
            string zhReason = "", zhMeta = "", zhCounter = "", zhWhere = "", zhCmp = "", enAll = "", zhTech = "", afterMsg = "";
            bool uiDone = false;
            bool chooseDisabled = false, pauseShown = false, chooseEnabledAfter = false, cardsOk = false, picked = false, newShown = false, heldRow = false;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(PanelUxml, "LootPickWindow", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_loot_pick") { hideFlags = HideFlags.HideAndDontSave };
                    LootPickPanelUIToolkit p = go.AddComponent<LootPickPanelUIToolkit>();
                    p.SetRowTemplateForTests(row);
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Refresh(force: true);
                    if (lang == GameLanguage.ZhCn && !uiDone)
                    {
                        uiDone = true;
                        zhReason = p.ReasonText;
                        zhCounter = p.CounterText;
                        zhMeta = p.CardMeta(0);
                        zhCmp = p.CardCompare(0);
                        zhTech = p.CardTech(0);
                        chooseDisabled = !p.ChooseEnabled(0) && !p.ChooseEnabled(1) && !p.ChooseEnabled(2);
                        pauseShown = p.PauseVisible;
                        newShown = Enumerable.Range(0, 3).Any(p.CardIsNew);
                        cardsOk = Enumerable.Range(0, 3).All(i => p.CardName(i).Length > 0 && p.CardMeta(i).Contains("占货舱"));
                        Click(p.PauseButton);
                        chooseEnabledAfter = GameClock.Paused && p.ChooseEnabled(0) && !p.PauseVisible;
                        zhWhere = p.WhereText;
                        int before = LootPickService.Count(s);
                        int idx = SmallestIndex(LootPickService.Find(s, p.CurrentPacketId));
                        Click(p.ChooseButton(idx));
                        picked = LootPickService.Count(s) == before - 1;
                        afterMsg = p.MessageText;
                        heldRow = p.HeldRowCount == 1 && p.HeldRowText(0).Length > 0;
                        GameClock.SetPaused(false);
                        p.Refresh(force: true);
                    }
                    else if (lang == GameLanguage.En)
                    {
                        enAll = p.CounterText + p.StatusText + p.ReasonText + p.CardName(0) + p.CardMeta(0) + p.CardCompare(0) + p.CardTech(0);
                    }
                    p.SetOpen(false);
                });
                Expect(result.StartsWith("PASS"), $"K0 待分拣面板布局探针（{lang}，四种分辨率 + 超长文字）：{string.Join(" / ", result.Split('\n').Take(4))}");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(zhCounter.Contains("/") && zhReason.Contains("战略暂停") && chooseDisabled && pauseShown && cardsOk && zhMeta.Contains("·") && zhCmp.StartsWith("对比") && zhTech.Contains("技术数据"),
                $"K1 FGU-34 候选卡片：“{zhCounter}”，每张卡写明类型 · 稀有度 · 协议 · 占货舱（“{zhMeta}”）、对比（“{zhCmp}”）、选它时没选的转成多少技术数据（“{zhTech}”）；" +
                $"在野外运行中“选这个”禁用并写原因（“{zhReason}”）、给“暂停并分拣”；新标记（{newShown}）");
            Expect(chooseEnabledAfter && zhWhere.Contains("货舱") && picked && afterMsg.Contains("已选") && heldRow && !GameText.ContainsMarker(enAll) && !ContainsCjk(enAll) && enAll.Length > 0,
                $"K2 点“暂停并分拣”进入战略暂停 → 可以选（去向“{zhWhere}”）；点“选这个”分拣完成（“{afterMsg}”），货舱区列出这一件（可丢弃）；英文界面没有中文");

            // 顶栏计数（与远征行军条同一个 HUD）
            GameObject hgo = null;
            try
            {
                VisualElement hroot = F.MountUxml(UiKitFolder + "WorldBar.uxml", out hgo);
                WorldBarHudUIToolkit w = hgo.AddComponent<WorldBarHudUIToolkit>();
                w.BindView(hroot);
                w.Refresh();
                string hud = w.PacketCountText;
                Expect(hud == GameText.Format("loot.pick.hud.both", LootPickService.Count(s), LootPickService.HeldCount(s)) && LootPickService.Count(s) > 0 && LootPickService.HeldCount(s) == 1
                       && !LootPickPanelUIToolkit.IsOpen,
                    $"K3 FGR-LOOT-020 顶栏只显示计数“{hud}”（待分拣数 + 货舱里的分拣物品数；面板不会自己弹出，点它才打开）");

                // K4 修复轮 P1：最后一个数据包分拣完以后，货舱里的分拣物品仍然看得到、丢得掉——顶栏改写“货舱里 ×N”继续显示，点开面板列出这些物品；
                // 机器不在仓库旁时“入库”禁用，“丢弃”可用。
                GameClock.SetPaused(true);
                foreach (LootPackageRecord rest in LootPickService.Packets(s).ToArray())
                {
                    LootPickService.TryPick(s, rest.PacketId, SmallestIndex(rest));
                }
                GameClock.SetPaused(false);
                int heldN = LootPickService.HeldCount(s);
                w.Refresh();
                string hud2 = w.PacketCountText;
                bool hudHeld = LootPickService.Count(s) == 0 && heldN >= 1 && hud2 == GameText.Format("loot.pick.hud.held", heldN) && w.PacketButton != null;
                GameObject pgo = null;
                int rows = 0, rowsAfter = -1;
                bool storeOff = false, emptyShown = false, opened = false;
                try
                {
                    VisualElement proot = F.MountUxml(PanelUxml, out pgo);
                    LootPickPanelUIToolkit lp = pgo.AddComponent<LootPickPanelUIToolkit>();
                    lp.SetRowTemplateForTests(row);
                    lp.BindView(proot);
                    Click(w.PacketButton);
                    opened = LootPickPanelUIToolkit.IsOpen;
                    lp.Refresh(force: true);
                    rows = lp.HeldRowCount;
                    emptyShown = lp.EmptyText.Length > 0;
                    storeOff = rows > 0 && !lp.HeldStoreEnabled(0) && lp.HeldStoreButton(0)?.text == GameText.Get("loot.pick.store");
                    Click(lp.HeldDiscardButton(0));
                    lp.Refresh(force: true);
                    rowsAfter = lp.HeldRowCount;
                    lp.SetOpen(false);
                }
                finally
                {
                    if (pgo != null)
                    {
                        UnityEngine.Object.DestroyImmediate(pgo);
                    }
                }
                Expect(hudHeld && opened && rows == heldN && emptyShown && storeOff && rowsAfter == heldN - 1 && LootPickService.HeldCount(s) == heldN - 1,
                    $"K4 没有待分拣数据包时：顶栏仍显示“{hud2}”，点它打开面板（{opened}），“货舱里”列出 {rows} 件（期望 {heldN}），在野外“入库”禁用（{storeOff}），点“丢弃”后剩 {rowsAfter} 件");
            }
            finally
            {
                if (hgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(hgo);
                }
            }
        }

        // ── L 种子集 / M 性能 ────────────────────────────────────────────────────

        private static void CheckSeedSet()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach (int seed in new[] { 101, 202, 303, 404 })
            {
                CampaignState s = NewWorld(seed);
                Vector2 field = FieldPoint(s, 70f, seed % 8);
                int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
                WorldSimulation.StepMany(3);
                CombatSite site = WorldSimulation.Home.Combat;
                string faction = new[] { "silent", "foundry", "clarity", "overclock" }[seed / 101 - 1];
                LootPackageRecord p;
                if (RaidCatalog.Units.Any(u => u.Faction == faction))
                {
                    KillWith(site, SpawnHostile(site, field + new Vector2(2f, 0f), UnitOf(faction), true), a);
                    p = LootPickService.Packets(s).LastOrDefault();
                }
                else
                {
                    // 突袭编成表里还没有清晰 / 超频的野外单位（FG10 / FG11 阵营 Story 才有）：用同一个生成入口（事件 / 首领也走它）验证该阵营的候选池。
                    p = LootPickService.Grant(s, LootPickService.SourceEvent, faction, a, HomeValleyLayout.RegionId, field);
                }
                bool pool = p != null && p.Candidates.Length == 3 && p.Candidates.All(c => LootPickCatalog.Find(c.RowId) is LootPickDef d && (d.Pool == "*" || d.Pool == faction));
                ok &= pool && p.Holder == a;
                lines.Add($"{seed}:{faction} {string.Join("/", p?.Candidates.Select(c => c.Tier) ?? Array.Empty<string>())}");
            }
            Expect(ok, $"L1 B25 种子集：4 个种子、4 个阵营的野外精英都生成数据包、候选都来自“该阵营池 + 通用池”（{string.Join("；", lines)}）");

            // 性能：同一步 60 个精英被击毁；地上 60 个数据包 + 40 台机器时的对账。
            CampaignState w = NewWorld(8214);
            Vector2 at = FieldPoint(w, 70f, 2);
            int killer = Spawn(w, at, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            for (int i = 0; i < 40; i++)
            {
                Spawn(w, CoreNear(w) + new Vector2(i % 8, i / 8), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            }
            WorldSimulation.StepMany(3);
            long g0 = LootPickService.GenEvents;
            double ms0 = LootPickService.GenTotalMs;
            CombatSite site2 = WorldSimulation.Home.Combat;
            var units = Enumerable.Range(0, 60).Select(i => SpawnHostile(site2, at + new Vector2(2f + i % 6, i / 6), UnitOf("foundry"), true)).ToList();
            foreach (int u in units)
            {
                site2.Kernel.Damage(u, 1e7f, Unit(site2, killer));
            }
            WorldSimulation.StepMany(GameClock.StepHz);
            long gens = LootPickService.GenEvents - g0;
            double avg = gens > 0 ? (LootPickService.GenTotalMs - ms0) / gens : 0;
            for (int i = 0; i < 60; i++)
            {
                LootPickService.Grant(w, LootPickService.SourceElite, "silent", 0, HomeValleyLayout.RegionId, FieldPoint(w, 200f, i % 8) + new Vector2(i, 0));
            }
            double step0 = LootPickService.StepMaxMs;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            double stepMs = LootPickService.StepMaxMs;
            PerfGate.Expect(gens == 60, $"M1 性能：同一步 60 个野外精英被击毁 → 生成 {gens} 个数据包，平均 {avg:0.###} ms / 个（预算 {LootPickCatalog.PerfMs} ms；Editor batchmode，只在击毁事件里调用、不按帧）",
                new[] { PerfGate.Le(avg, LootPickCatalog.PerfMs, "lootpick.gen_avg_ms") }, (o, m) => Expect(o, m), Line);
            PerfGate.Expect(LootPickService.StepChecks > 0, $"M2 性能：{LootPickService.Count(w)} 个数据包（60 个在地上）+ {MachineRegistry.AllRecords.Count} 台机器时每 {LootPickCatalog.CheckSeconds} 游戏秒一次对账，最长 {stepMs:0.###} ms（预算 1 ms）",
                new[] { PerfGate.Le(stepMs, 1.0, "lootpick.step_max_ms") }, (o, m) => Expect(o, m), Line);
            PerfLines.Add($"三选一：生成平均 {avg:0.###} ms / 个（{gens} 个），对账最长 {stepMs:0.###} ms（Editor batchmode；真机 IL2CPP 归 FG15-SYS-02）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static bool ContainsCjk(string s) => s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static void Click(Button b)
        {
            if (b == null || !b.enabledInHierarchy)
            {
                return;
            }
            using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        private static int NotifyCount() => GameLogic.Notifications.NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == LootPickService.NotifyType).Sum(e => e.Count);

        private static int SmallestIndex(LootPackageRecord p)
        {
            int best = 0;
            for (int i = 1; i < p.Candidates.Length; i++)
            {
                if (LootPickService.SpaceOf(p.Candidates[i]) < LootPickService.SpaceOf(p.Candidates[best]))
                {
                    best = i;
                }
            }
            return best;
        }

        private static int InventoryTotal(CampaignState s) => ItemCatalog.Items.Where(HomeInventory.IsStorable).Sum(i => HomeInventory.Stock(s, i));

        private static int Chips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static int AnalysisTags(CampaignState s, string target)
        {
            var held = new List<HomeValleyAnalysis.HeldEntry>();
            HomeValleyAnalysis.CollectHeld(s, held);
            return held.Count(h => h.TargetId == target);
        }

        private static string Snapshot(CampaignState s)
        {
            LootPacketState st = LootPickService.StateOf(s);
            var sb = new StringBuilder();
            foreach (LootPackageRecord p in st.Packets)
            {
                sb.Append(p.PacketId).Append('@').Append(p.Holder).Append(':').Append(p.RegionId).Append(':').Append(Mathf.RoundToInt(p.X)).Append(',').Append(Mathf.RoundToInt(p.Y))
                  .Append(':').Append(p.Source).Append(':').Append(p.Faction).Append(':').Append(p.Pity ? 1 : 0).Append('[');
                foreach (LootCandidateRecord c in p.Candidates)
                {
                    sb.Append(c.RowId).Append('x').Append(c.Amount).Append('>').Append(c.TargetId).Append('/').Append(c.Tier).Append(';');
                }
                sb.Append("]|");
            }
            foreach (LootHeldRecord h in st.Held)
            {
                sb.Append("H:").Append(h.HeldId).Append('@').Append(h.Machine).Append(':').Append(h.ItemId).Append('>').Append(h.TargetId).Append('x').Append(h.Amount).Append('|');
            }
            foreach (LootPickLogRecord l in st.Picks)
            {
                sb.Append("P:").Append(l.PacketId).Append('>').Append(l.ChosenRowId).Append(':').Append(l.Destination).Append(':').Append(l.TechData).Append('|');
            }
            sb.Append("S:").Append(string.Join(",", st.SeenKeys)).Append("|N:").Append(st.NextPacketSerial).Append("|R:").Append(st.NoRareStreak).Append("|M:").Append(st.PacketsMade)
              .Append('/').Append(st.PacketsResolved).Append('/').Append(st.LeftoverTech);
            return sb.ToString();
        }

        private static RaidUnitDef UnitOf(string faction) =>
            RaidCatalog.Units.Where(u => u != null && u.Faction == faction).OrderBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault() ?? RaidCatalog.Units.First();

        private static int SpawnHostile(CombatSite site, Vector2 at, RaidUnitDef def, bool elite)
        {
            int key = ++_eliteKey;
            return SiegeService.SpawnOne(site, key, at, at, def, elite);
        }

        /// <summary>致命一击来自 <paramref name="killerLogic"/> 这台机器（0 = 不明来源 / 炮塔），走内核的真实击毁事件。</summary>
        private static void KillWith(CombatSite site, int unit, int killerLogic)
        {
            site.Kernel.Damage(unit, 1e7f, killerLogic > 0 ? Unit(site, killerLogic) : 0);
            WorldSimulation.StepMany(3);
        }

        private static int Unit(CombatSite site, int logicId) => site != null && site.TryGetMachineUnit(logicId, out int u) ? u : 0;

        private static Vector2 Pos(int logicId) =>
            MachineRegistry.TryGetLivePosition(logicId, out Vector2 p) ? p : MachineRegistry.TryGetRecord(logicId, out MachineRecord m) ? m.WorldPosition : Vector2.zero;

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static string Name(int id) => Rec(id) != null ? MachineNaming.Short(Rec(id)) : "#" + id;

        /// <summary>离核心 <paramref name="dist"/> 格、按 <paramref name="dir"/>（0～7，45° 一档）方向的可走格（世界按种子生成，不写死坐标）。</summary>
        private static Vector2 FieldPoint(CampaignState s, float dist, int dir)
        {
            GridCell c = HomeGridService.CorePivot(s);
            float ang = dir * Mathf.PI / 4f;
            var raw = new GridCell(c.X + Mathf.RoundToInt(Mathf.Cos(ang) * dist), c.Y + Mathf.RoundToInt(Mathf.Sin(ang) * dist));
            GridCell ok = SiegeService.NearestPassable(raw, 12);
            return new Vector2(ok.X, ok.Y);
        }

        private static Vector2 CoreNear(CampaignState s)
        {
            GridCell c = HomeGridService.CorePivot(s);
            GridCell ok = SiegeService.NearestPassable(new GridCell(c.X + 5, c.Y - 5), 8);
            return new Vector2(ok.X, ok.Y);
        }

        /// <summary>测试捷径：确保有一座运转中的仓库（入库容量；仓库施工与维修由 FG3-LOG-02 / FG4-ECO-05 覆盖）。</summary>
        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = F.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "pick_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        /// <summary>一支到达家园、展开攻城的突袭队（与 FgRaidResultSelfCheck 同一套夹具：正式的突袭计划 + 到达的队伍，FG6-DEF-05 展开成攻城单位），全部是精英。</summary>
        private static TransitGroupRecord RaidFixture(CampaignState s, out CombatSite site)
        {
            site = WorldSimulation.Home.Combat;
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 at = FieldPoint(s, 30f, 2);
            var c = new Vector2(core.X, core.Y);
            Vector2 o = at + (at - c).normalized * 60f;
            RaidUnitDef def = UnitOf("silent");
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, "silent", 3, o.x, o.y, core.X, core.Y);
            if (g == null)
            {
                return null;
            }
            g.Faction = "silent";
            g.UnitIds = new[] { def.Id };
            g.UnitCounts = new[] { 3 };
            g.EliteCounts = new[] { 3 };
            g.TargetKind = RaidDirectorService.TargetHome;
            g.PosX = at.x;
            g.PosY = at.y;
            g.RouteX = new[] { (int)Mathf.Round(at.x) };
            g.RouteY = new[] { (int)Mathf.Round(at.y) };
            g.RouteIndex = 1;
            g.RouteState = WorldTransitSystem.RouteFollowing;
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks;
            var plan = new RaidPlanRecord
            {
                PlanId = "plan-pick1", Serial = 990, Wave = 90, State = RaidDirectorService.StateDeparted,
                GroupId = g.GroupId, Faction = "silent", Level = 2, Trigger = RaidCatalog.TriggerStory, TargetKind = RaidDirectorService.TargetHome,
                WarnTick = Math.Max(0, GameClock.Ticks - GameClock.StepHz * 30L), ArrivedTick = g.ArrivedAtTick,
            };
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            d.Plans = d.Plans.Append(plan).ToArray();
            g.PlanId = plan.PlanId;
            WorldSimulation.StepMany(GameClock.StepHz);
            return SiegeService.IsSieging(g) ? g : null;
        }

        private static IEnumerable<int> RaidersOf(CombatSite site, TransitGroupRecord g)
        {
            var ids = new List<int>();
            site?.SiegeRaiderIds(SiegeService.KeyOf(g), ids);
            return ids;
        }

        private static CampaignState NewWorld(int seed, bool observe = true, int version = 3)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fglootpick-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return s;
        }

        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at, string chassis, string blueprint)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgLootPickSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                LootPickCatalog.ResetForTests();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
