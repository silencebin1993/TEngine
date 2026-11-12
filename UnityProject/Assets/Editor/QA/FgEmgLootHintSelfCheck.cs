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
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
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
using BinGames.Sim.Combat;
using Unity.Mathematics;
using L = GameLogic.EditorTools.FgLootPickSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-LOOT-01 三选一新交互提示、部件通道源与重复部件（FG19 FGR-EMG-191～193；FG08 FGR-LOOT-020；验收 FGT-EMG-062 的三选一 N 与重复部件部分）——起真实系统断言行为：
    /// A 新交互 N / M：与独立实现（直接读 fg.TbChannelDecl 原表）逐候选一致；拥有一件能接上的内容后 N 增加；发现一种交互后 N − 1、M + 1；N = 0 显示“暂无新交互”；
    ///   新语法标签（不计入 N）；部件（作战 / 功能 / 结构 / 底盘特性）都作为通道源；耗时 ≤ loot.pick.hint_perf_ms。
    /// B 面板：三个候选同样展示（顺序 = 数据包顺序、提示行样式相同、不按 N 加任何类）、悬停展开列表、打开时算一次（不按帧）、换数据包才重算、中英文。
    /// C 重复部件：已掌握的部件再获得 → 组件库存 +1（多一台车能装），不进解析台、不改数值；没掌握的照旧送解析；仓库满 → 拆成技术数据并有字幕；
    ///   数据包里同一部件不出现两次；固件芯片不受影响；读档后库存一致；FGR-EMG-193 第 2、3 条（同名限 1 件、prop.* 相加封顶）在重复入库后回归。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgLootHintSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgLootHintSelfCheck。
    /// </summary>
    public static class FgEmgLootHintSelfCheck
    {
        private const int Slot = 9;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "LootPickPanel.uxml";
        private const string RowUxml = UiKitFolder + "LootPickRow.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

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
            Line("\n[三选一新交互提示与重复部件] N / M 计算、只给信息、部件通道源、重复部件只加库存、仓满拆解、存读档（EMG4-LOOT-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgemgloothint-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                EmgCatalog.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                LootPickService.ResetSessionState();
                LootInteractionHint.ResetSessionState();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckHintCount);
                Step(CheckHintUi);
                Step(CheckDuplicateParts);
                Step(CheckFlagComponents);
                Step(CheckFlagBehaviors);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                LootPickCatalog.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
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
            Line(_fail == 0 ? $"  三选一新交互提示与重复部件：全部 {_pass} 项通过" : $"  三选一新交互提示与重复部件：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 新交互 N / M ──────────────────────────────────────────────────────

        /// <summary>独立实现（直接读原表 fg.TbChannelDecl，不经编译结果）：候选 owner 的 (N, M)。</summary>
        private static (int n, int m) Oracle(CampaignState s, string owner, bool hazards = true)
        {
            Tables t = ConfigSystem.Instance.Tables;
            int stage = EmgCatalog.EffectiveStage;
            var chips = new HashSet<string>((s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Where(c => c != null).Select(c => c.CardDefId));
            var provided = new HashSet<string>(t.TbFluidLeak.DataList.Select(l => l.Matter).Where(m => !string.IsNullOrEmpty(m) && m != "none"));
            // EMG4-CLEAN-01（FG-GAP-213）：危害带端口也是世界常驻源（独立实现直接读 fg.TbHazard.ports）
            if (hazards) provided.UnionWith(t.TbHazard.DataList.Where(h => !string.IsNullOrEmpty(h.Ports) && h.Ports != "none").SelectMany(h => h.Ports.Split(',')).Where(x => x.Length > 0));
            var cand = new HashSet<string>();
            foreach (GameConfig.fg.ChannelDecl d in t.TbChannelDecl.DataList)
            {
                if (d.StageGate > stage)
                {
                    continue;
                }
                if (d.Owner == owner)
                {
                    cand.Add(d.Port);
                }
                if (d.Dir == "write" && (chips.Contains(d.Owner) || MechanicalContentUnlock.IsUnlocked(s, d.Owner)))
                {
                    provided.Add(d.Port);
                }
            }
            var seen = new HashSet<string>();
            int n = 0, m = 0;
            foreach (GameConfig.fg.ChannelRule r in t.TbChannelRule.DataList)
            {
                if (r.Stage > stage || !((cand.Contains(r.A) && provided.Contains(r.B)) || (cand.Contains(r.B) && provided.Contains(r.A))))
                {
                    continue;
                }
                string e = r.Named != "none" ? r.Named : r.Id;
                if (!NamedReactionCatalog.TryGet(e, out _) || !seen.Add(e))
                {
                    continue;
                }
                if (ReactionFeedback.IsFirstTriggered(s, e))
                {
                    m++;
                }
                else
                {
                    n++;
                }
            }
            return (n, m);
        }

        private static LootPackageRecord Packet(CampaignState s, params LootCandidateRecord[] cands) =>
            new LootPackageRecord { PacketId = "lp-test-" + Guid.NewGuid().ToString("N").Substring(0, 6), Candidates = cands };

        private static LootCandidateRecord Fw(string id) => new LootCandidateRecord { Kind = LootPickCatalog.KindFirmware, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = id, Amount = 1, Tier = "common" };
        private static LootCandidateRecord Module(string id) => new LootCandidateRecord { Kind = LootPickCatalog.KindModule, ItemId = AnalysisCatalog.UnparsedModuleId, TargetId = id, Amount = 1, Tier = "fine" };

        private static void Unlock(CampaignState s, string id)
        {
            s.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(s.UnlockedContentIds, id) < 0)
            {
                s.UnlockedContentIds = s.UnlockedContentIds.Concat(new[] { id }).ToArray();
            }
        }

        private static void Relock(CampaignState s, string id) => s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Where(x => x != id).ToArray();

        private static void CheckHintCount()
        {
            CampaignState s = L.NewWorld(9401);
            Tables t = ConfigSystem.Instance.Tables;
            var owners = t.TbChannelDecl.DataList.Where(d => d.StageGate <= EmgCatalog.EffectiveStage).Select(d => d.Owner).Distinct().ToList();
            List<string> fws = owners.Where(o => FirmwareKinds.IsFirmware(o)).ToList();
            List<string> parts = owners.Where(o => !FirmwareKinds.IsFirmware(o) && MechanicalContentFacade.TryGet(o, out MechanicalContentDef pd) && pd.Category != MechanicalContentCategory.Enemy).ToList();

            // A1 与独立实现逐候选一致（全部固件 + 全部部件作候选）
            int bad = 0, total = 0, withN = 0;
            foreach (string o in fws.Concat(parts))
            {
                LootInteractionHint.Hint h = LootInteractionHint.For(s, Packet(s, MechanicalContentFacade.TryGet(o, out MechanicalContentDef d) && d.Category != MechanicalContentCategory.Firmware ? Module(o) : Fw(o)))[0];
                (int n, int m) = Oracle(s, o);
                total++;
                withN += h.New > 0 ? 1 : 0;
                if (h.Owner != o || h.New != n || h.Found != m)
                {
                    bad++;
                    if (bad <= 3)
                    {
                        Line($"    差异：{o} 提示 N={h.New} M={h.Found}，独立实现 N={n} M={m}");
                    }
                }
            }
            // EMG4-CLEAN-01（FG-GAP-213）：危害带端口计入世界常驻源——至少一个候选因危害带多出新交互，且提示与含危害带的独立实现一致（上面逐个比过）
            // 基础蓝图内容恒解锁、本身就写 en.heat / mat.acid，危害带对 N 的贡献在新战役里被盖住；这里直接验生产代码的世界常驻源集合含危害带端口，独立实现（A1 上面逐个比过）读同一列
            var world = new HashSet<string>();
            LootInteractionHint.CollectWorldSources(world);
            var hzPorts = t.TbHazard.DataList.Where(h => h.Ports != "none").SelectMany(h => h.Ports.Split(',')).Distinct().ToList();
            Expect(hzPorts.Contains("mat.acid") && hzPorts.Contains("en.heat") && hzPorts.All(world.Contains) && t.TbFluidLeak.DataList.Where(l => l.Matter != "none").All(l => world.Contains(l.Matter)),
                $"A1b FG-GAP-213：生产代码的世界常驻源集合含危害带端口 {string.Join("、", hzPorts)}（fg.TbHazard.ports）与泄漏流体，共 {world.Count} 个");
            Expect(bad == 0 && total > 40 && withN > 0, $"A1 {total} 个通道源作候选（{fws.Count} 条固件 + {parts.Count} 个部件 / 底盘特性），N / M 与直接读原表的独立实现逐个一致（{withN} 个 N > 0）");

            // A2 拥有一件能接上的内容后 N 增加；A3 发现后 N − 1、M + 1
            string candOwner = null, provider = null, entry = null;
            foreach (GameConfig.fg.ChannelRule r in t.TbChannelRule.DataList)
            {
                string e = r.Named != "none" ? r.Named : r.Id;
                if (!NamedReactionCatalog.TryGet(e, out _) || r.Stage > EmgCatalog.EffectiveStage)
                {
                    continue;
                }
                string c0 = fws.FirstOrDefault(f => !MechanicalContentUnlock.IsUnlocked(s, f) && t.TbChannelDecl.DataList.Any(d => d.Owner == f && d.Port == r.A));
                string p0 = fws.FirstOrDefault(f => f != c0 && !MechanicalContentUnlock.IsUnlocked(s, f) && t.TbChannelDecl.DataList.Any(d => d.Owner == f && d.Dir == "write" && d.Port == r.B));
                if (c0 != null && p0 != null)
                {
                    int before = LootInteractionHint.For(s, Packet(s, Fw(c0)))[0].New;
                    Unlock(s, p0);
                    int after = LootInteractionHint.For(s, Packet(s, Fw(c0)))[0].New;
                    Relock(s, p0);
                    if (after > before)
                    {
                        candOwner = c0;
                        provider = p0;
                        entry = e;
                        break;
                    }
                }
            }
            Expect(candOwner != null, $"A2 拥有一条能提供另一侧端口的固件后，候选的 N 增加（候选 {candOwner}，提供方 {provider}，交互 {entry}）");
            if (candOwner != null)
            {
                Unlock(s, provider);
                LootInteractionHint.Hint h0 = LootInteractionHint.For(s, Packet(s, Fw(candOwner)))[0];
                s.ReactionFirstTriggers = (s.ReactionFirstTriggers ?? Array.Empty<ReactionFirstTriggerRecord>()).Concat(new[] { new ReactionFirstTriggerRecord { ReactionId = entry, SiteId = "test" } }).ToArray();
                LootInteractionHint.Hint h1 = LootInteractionHint.For(s, Packet(s, Fw(candOwner)))[0];
                string name = GameText.Get(NamedReactionCatalog.TryGet(entry, out GameConfig.fg.Reaction row) ? row.NameKey : entry);
                Expect(h1.New == h0.New - 1 && h1.Found == h0.Found + 1 && h1.Names.Any(x => x.Contains(name)) && !h0.Names.Any(x => x.Contains(name)),
                    $"A3 发现“{name}”后：N {h0.New} → {h1.New}、M {h0.Found} → {h1.Found}；悬停列表未发现时只写两侧通道类别、发现后才写名字");
                s.ReactionFirstTriggers = Array.Empty<ReactionFirstTriggerRecord>();
                Relock(s, provider);
            }

            // A4 N = 0：暂无新交互；非通道源（材料）不显示
            string zero = fws.Concat(parts).FirstOrDefault(o => Oracle(s, o).n == 0);
            LootInteractionHint.Hint hz = zero != null ? LootInteractionHint.For(s, Packet(s, Fw(zero)))[0] : null;
            var mat = new LootCandidateRecord { Kind = LootPickCatalog.KindItem, ItemId = "metal_ore", TargetId = "none", Amount = 20, Tier = "common" };
            LootInteractionHint.Hint hm = LootInteractionHint.For(s, Packet(s, mat))[0];
            Expect(hz != null && LootInteractionHint.LineOf(hz).StartsWith("暂无新交互") && hm.Owner == null && LootInteractionHint.LineOf(hm) == string.Empty,
                $"A4 N = 0 的候选（{zero}）显示“{(hz != null ? LootInteractionHint.LineOf(hz).Split('\n')[0] : "")}”（B12）；材料候选不是通道源、不显示这一行");

            // A5 新语法标签：候选带来玩家没有的触发事件种类 / 元程序，不计入 N
            string gram = fws.FirstOrDefault(f => t.TbChannelDecl.DataList.Any(d => d.Owner == f && d.Port.StartsWith("meta.")) && !MechanicalContentUnlock.IsUnlocked(s, f));
            LootInteractionHint.Hint hg = gram != null ? LootInteractionHint.For(s, Packet(s, Fw(gram)))[0] : null;
            Unlock(s, gram);
            LootInteractionHint.Hint hg2 = gram != null ? LootInteractionHint.For(s, Packet(s, Fw(gram)))[0] : null;
            Relock(s, gram);
            Expect(hg != null && hg.Grammar.Any(x => x.StartsWith("新语法")) && hg2 != null && !hg2.Grammar.Any(x => x.Contains(GameText.Get("emg.port." + t.TbChannelDecl.DataList.First(d => d.Owner == gram && d.Port.StartsWith("meta.")).Port + ".name")))
                   && hg.New == Oracle(s, gram).n,
                $"A5 元程序候选（{gram}）带“{hg?.Grammar.FirstOrDefault()}”标签，已拥有同种元程序后不再标；N 不含语法（= {hg?.New}）");

            // A6 部件都是通道源：作战 / 功能组件、结构模块、底盘特性
            bool kinds = new[] { MechanicalContentCategory.MainComponent, MechanicalContentCategory.FunctionComponent, MechanicalContentCategory.Structure, MechanicalContentCategory.Chassis }
                .All(cat => owners.Any(o => MechanicalContentFacade.TryGet(o, out MechanicalContentDef d) && d.Category == cat));
            int ownedPart = parts.Count(o => MechanicalContentUnlock.IsUnlocked(s, o));
            Expect(kinds && ownedPart > 0, $"A6 作战组件、功能组件、结构模块、底盘特性都在声明表里作通道源（已拥有的部件 {ownedPart} 个参与“另一侧”提供端口）");

            // A7 性能：打开一个数据包算一次 ≤ loot.pick.hint_perf_ms
            float budget = GridContent.TryGetTuning("loot.pick.hint_perf_ms", out float b) ? b : 1f;
            LootPackageRecord big = Packet(s, Fw(fws[0]), Module(parts[0]), Fw(fws[fws.Count - 1]));
            double max = 0;
            for (int i = 0; i < 50; i++)
            {
                LootInteractionHint.For(s, big);
                max = Math.Max(max, LootInteractionHint.LastMs);
            }
            PerfLines.Add($"新交互提示：三个候选一次 最长 {max:0.000} ms / 50 次（预算 {budget} ms；Editor batchmode / Mono JIT，真机 IL2CPP 未测）");
            Expect(max <= budget * 2, $"A7 打开数据包算一次三个候选：最长 {max:0.000} ms（预算 {budget} ms，超线不到 2 倍记性能警告）");
            if (max > budget)
            {
                Line($"    性能警告：{max:0.000} ms 超过预算 {budget} ms（不到 2 倍）");
            }
        }

        // ── B 面板 ──────────────────────────────────────────────────────────────

        private static void CheckHintUi()
        {
            CampaignState s = L.NewWorld(9402);
            L.EnsureWarehouse(s);
            Vector2 home = L.CoreNear(s);
            int m = L.Spawn(s, home, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            List<LootPickDef> rows = LootPickCatalog.Rows.Where(r => r.Pool == "silent" && (r.Kind == LootPickCatalog.KindFirmware || r.Kind == LootPickCatalog.KindModule)).Take(3).ToList();
            foreach (LootPickDef r in rows)
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            LootPickCatalog.OverrideForTests(rows);
            LootPackageRecord p1 = LootPickService.Grant(s, LootPickService.SourceElite, "silent", m, HomeValleyLayout.RegionId, home);
            LootPackageRecord p2 = LootPickService.Grant(s, LootPickService.SourceElite, "silent", m, HomeValleyLayout.RegionId, home);
            LootPickPanelUIToolkit.InWorldOverrideForTests = true;
            var row = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml);
            bool sameOrder = false, sameStyle = false, noNClass = false, hover = false, once = false, step = false, en = false, lineOk = false;
            string zh0 = "", list0 = "";
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(PanelUxml, "LootPickWindow", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_loot_hint") { hideFlags = HideFlags.HideAndDontSave };
                    LootPickPanelUIToolkit p = go.AddComponent<LootPickPanelUIToolkit>();
                    p.SetRowTemplateForTests(row);
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Refresh(force: true);
                    LootPackageRecord cur = LootPickService.Find(s, p.CurrentPacketId);
                    LootInteractionHint.Hint[] hs = LootInteractionHint.For(s, cur);
                    if (lang == GameLanguage.ZhCn)
                    {
                        sameOrder = Enumerable.Range(0, 3).All(i => p.CardName(i) == LootPickService.CandidateName(cur.Candidates[i]));
                        var cls0 = new HashSet<string>(p.HintClasses(0));
                        sameStyle = Enumerable.Range(1, 2).All(i => cls0.SetEquals(p.HintClasses(i)));
                        noNClass = Enumerable.Range(0, 3).All(i => !p.CardClasses(i).Any(c => c.Contains("hint") || c.Contains("best") || c.Contains("recommend") || c.Contains("highlight")));
                        lineOk = Enumerable.Range(0, 3).All(i => p.CardHint(i) == LootInteractionHint.LineOf(hs[i]) && p.CardHint(i).Length > 0);
                        zh0 = p.CardHint(0);
                        p.SetHintExpanded(0, true);
                        hover = p.CardHintExpanded(0) && p.CardHintList(0).Length > 0 && !p.CardHintExpanded(1);
                        list0 = p.CardHintList(0);
                        p.SetHintExpanded(0, false);
                        hover &= !p.CardHintExpanded(0);
                        int c0 = p.HintComputes;
                        for (int k = 0; k < 30; k++)
                        {
                            WorldSimulation.StepMany(1);
                            p.Refresh(force: false);
                            p.Refresh(force: true);
                        }
                        once = p.HintComputes == c0;
                        p.Step(1);
                        step = p.HintComputes == c0 + 1 && p.CurrentPacketId != cur.PacketId;
                    }
                    else
                    {
                        en = Enumerable.Range(0, 3).All(i => p.CardHint(i).Length > 0 && !p.CardHint(i).Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
                    }
                });
                Expect(result.StartsWith("PASS"), $"B0 布局探针（{lang}，四种分辨率 + 超长文字）：{result.Split('\n')[0]}");
            }
            Expect(sameOrder && sameStyle && noNClass && lineOk,
                $"B1 三个候选同样展示：顺序 = 数据包候选顺序、提示行样式相同、卡片不按 N 加任何类（不排序、不高亮、不推荐）；例：“{zh0.Split('\n')[0]}”");
            Expect(hover, $"B2 悬停卡片展开交互列表、移开收起：{list0.Replace("\n", " / ")}");
            Expect(once && step, "B3 打开时算一次：面板逐帧刷新 30 次不重算；换到下一个数据包才重算一次");
            Expect(en, "B4 英文界面提示行没有中文");
        }

        // ── C 重复部件 ──────────────────────────────────────────────────────────

        private static void CheckDuplicateParts()
        {
            CampaignState s = L.NewWorld(9403);
            L.EnsureWarehouse(s);
            Vector2 home = L.CoreNear(s);
            int m = L.Spawn(s, home, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);
            LootPickDef known = LootPickCatalog.Rows.First(r => r.Kind == LootPickCatalog.KindModule && MechanicalContentFacade.TryGet(r.Target, out MechanicalContentDef d)
                                                              && d.Category == MechanicalContentCategory.Structure);
            Relock(s, known.Target);
            List<LootPickDef> fill = LootPickCatalog.Rows.Where(r => r.Id == "pick.any.alloy" || r.Id == "pick.any.electronic").ToList();
            foreach (LootPickDef r in fill.Concat(new[] { known }))
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            LootPickCatalog.OverrideForTests(new[] { known, fill[0], fill[1] });

            // C1 第一次获得（没掌握）：照旧送解析台（未解析模块 +1）
            int mod0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId), sm0 = HomeInventory.Stock(s, "structure_module");
            LootPackageRecord p = LootPickService.Grant(s, LootPickService.SourceElite, known.Pool, m, HomeValleyLayout.RegionId, home);
            int i = Array.FindIndex(p.Candidates, c => c.RowId == known.Id);
            LootPickService.PickResult r1 = LootPickService.TryPick(s, p.PacketId, i);
            Expect(r1.Success && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == mod0 + 1 && HomeInventory.Stock(s, "structure_module") == sm0,
                $"C1 第一次获得 {known.Target}（还没掌握）：未解析模块 {mod0} → {HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId)}，送解析得蓝图；组件库存不变");

            // C2 已掌握后再获得：结构模块库存 +1，不进解析、不改数值
            Unlock(s, known.Target);
            float massBefore = EmgCatalog.PartPropModify("prop.mass", new[] { null, known.Target }, out _);
            int stocked0 = LootPickService.DuplicatesStocked;
            p = LootPickService.Grant(s, LootPickService.SourceElite, known.Pool, m, HomeValleyLayout.RegionId, home);
            i = Array.FindIndex(p.Candidates, c => c.RowId == known.Id);
            string cmp = LootPickService.CompareText(s, p.Candidates[i]);
            bool notNew = !LootPickService.IsNew(s, p.Candidates[i]);
            mod0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
            LootPickService.PickResult r2 = LootPickService.TryPick(s, p.PacketId, i);
            float massAfter = EmgCatalog.PartPropModify("prop.mass", new[] { null, known.Target }, out _);
            Expect(r2.Success && HomeInventory.Stock(s, "structure_module") == sm0 + 1 && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == mod0
                   && LootPickService.DuplicatesStocked == stocked0 + 1 && Math.Abs(massAfter - massBefore) < 1e-6f && notNew && cmp.Contains("重复获得只加库存"),
                $"C2 已掌握后再获得：结构模块库存 {sm0} → {HomeInventory.Stock(s, "structure_module")}（多一台车能装），不进解析台、部件数值不变（质量改写 {massAfter:0.##}），卡面写“{cmp.Split('\n')[0]}”");

            // C3 FGR-EMG-193 第 2、3 条回归：同名部件每台限 1 件；prop.* 相加封顶
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, known.Target, Array.Empty<string>());
            b.UtilityId = known.Target;
            CircuitValidationResult v = b.Validate();
            float cap = GridContent.TryGetTuning(EmgCatalog.PartPropCapKey, out float cv) ? cv : 1f;
            float sum = EmgCatalog.PartPropModify("prop.mass", new[] { "comp_cannon", "struct_armor" }, out float raw);
            Expect(!v.IsValid && v.Issues.Any(x => x.Code == CircuitIssueCode.PartDuplicate) && sum <= cap + 1e-4f && raw > cap,
                $"C3 重复入库后：同一台机器装两件 {known.Target} 被蓝图校验拒绝（{v.Issues.FirstOrDefault(x => x.Code == CircuitIssueCode.PartDuplicate)?.Message}）；重炮 + 重甲的 prop.mass 相加 {raw:0.##} 封顶为 {sum:0.##}（上限 {cap}）");

            // C4 读档后库存一致
            int smSaved = HomeInventory.Stock(s, "structure_module");
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = L.Reload(Slot);
            Expect(sv.Success && back != null && HomeInventory.Stock(back, "structure_module") == smSaved && MechanicalContentUnlock.IsUnlocked(back, known.Target),
                $"C4 存读档：结构模块库存 {smSaved} → 读档后 {(back != null ? HomeInventory.Stock(back, "structure_module") : -1)}，蓝图掌握状态一致");
            if (back == null)
            {
                return;
            }
            s = back;

            // C5 仓库满：重复部件拆成技术数据，有字幕，不静默丢弃
            ItemDef sm = ItemCatalog.Find("structure_module");
            for (int k = 0; k < 64 && HomeInventory.Space(s, sm) > 0; k++)
            {
                HomeInventory.Add(s, sm, Math.Max(1, HomeInventory.Space(s, sm)));
            }
            int full = HomeInventory.Stock(s, "structure_module");
            LootPickCatalog.OverrideForTests(new[] { known, fill[0], fill[1] });
            p = LootPickService.Grant(s, LootPickService.SourceElite, known.Pool, m, HomeValleyLayout.RegionId, home);
            i = Array.FindIndex(p.Candidates, c => c.RowId == known.Id);
            string cmpFull = LootPickService.CompareText(s, p.Candidates[i]);
            bool canStore = LootPickService.DepotCanStore(s, p.Candidates[i]);
            long tech0 = (long)s.TechData;
            int dis0 = LootPickService.DuplicatesDismantled;
            LootPickService.PickResult r3 = LootPickService.TryPick(s, p.PacketId, i);
            long gained = (long)s.TechData - tech0 - r3.TechData;
            Expect(HomeInventory.Space(s, sm) <= 0 && canStore && r3.Success && HomeInventory.Stock(s, "structure_module") == full && LootPickService.DuplicatesDismantled == dis0 + 1
                   && gained == LootPickCatalog.DismantleTech && cmpFull.Contains("仓库已满"),
                $"C5 仓库满时拿到重复部件：能选（卡面预先写“{cmpFull.Split('\n').Last()}”），拆成技术数据 +{gained}（另有没选的两个 +{r3.TechData}），库存不超、不静默丢弃");

            // C6 同一部件在一个数据包里不出现两次；固件芯片不受重复部件规则影响
            LootPickCatalog.ResetForTests();
            string fw = LootPickCatalog.Rows.First(r => r.Kind == LootPickCatalog.KindFirmware).Target;
            var dupRows = new[] { known, known, fill[0], fill[1] };
            LootPickCatalog.OverrideForTests(dupRows);
            bool once = true;
            for (int k = 0; k < 40; k++)
            {
                List<LootCandidateRecord> c = LootPickService.RollCandidates(9403u + (uint)k, known.Pool, k + 1, false);
                once &= c.Count(x => x.Kind == LootPickCatalog.KindModule && x.TargetId == known.Target) <= 1;
            }
            Expect(once && LootPickService.DuplicateStockItem(s, Fw(fw)) == null,
                $"C6 候选池里同一部件出现两行：40 个数据包里同一部件最多出现一次；固件芯片（{fw}）不走重复部件规则（仍可多份）");
        }

        // ── E 阵营标志组件（FGR-FAC-009，FG-GAP-150 关闭断言）────────────────────

        private static readonly (string Id, string Faction, string Stock)[] Flags =
        {
            ("comp_jammer", "silent", "combat_component"), ("struct_stealth_cloak", "silent", "structure_module"),
            ("comp_brace_mortar", "foundry", "combat_component"), ("struct_armor_skirt", "foundry", "structure_module"),
            ("comp_spray_arm", "clarity", "combat_component"), ("comp_ring_sprinkler", "clarity", "combat_component"),
        };

        private static string _cloakBp;
        private static string _skirtBp;
        private static string _mortarBp;
        private static string _ringBp;

        private static bool Admitted(string owner)
        {
            EmgCompiled c = EmgCatalog.Current;
            if (!c.OwnerIndex.TryGetValue(owner, out int oi) || !c.OwnerActive(oi))
            {
                return false;
            }
            var reads = new List<EmgPortRow>();
            var writes = new List<EmgPortRow>();
            foreach (EmgDeclRow d in c.Decls.Where(x => x.Owner == oi))
            {
                EmgPortRow pr = c.Ports.First(x => x.Code == d.Port);
                string pid = c.PortIdByCode[d.Port];
                if (d.Dir == EmgDirs.Read && (pr.Flags & EmgPortRow.FlagPerceive) != 0 && !(d.Role == EmgRole.Trigger && EmgCatalog.AlwaysTrueTriggers.Contains(pid)))
                {
                    reads.Add(pr);
                }
                if (d.Dir == EmgDirs.Write && (pr.Flags & EmgPortRow.FlagNumericOnly) == 0)
                {
                    writes.Add(pr);
                }
            }
            return reads.Count > 0 && writes.Count > 0 && reads.Any(r => writes.Any(w => w.Family != r.Family));
        }

        private static bool Stealthed(CombatSite site, int logicId) =>
            site != null && logicId > 0 && site.TryGetMachineUnit(logicId, out int u) && site.Kernel.TryGetUnit(u, out BinGames.Sim.Combat.CombatUnitView v)
            && (v.Flags & BinGames.Sim.Combat.CombatUnitFlags.Stealthed) != 0;

        private static void CheckFlagComponents()
        {
            CampaignState s = FgAnalysisBenchSelfCheck.NewWorld(9405);
            L.EnsureWarehouse(s);
            Vector2 home = L.CoreNear(s);
            int m = L.Spawn(s, home, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(3);

            // E1 数据：现有载体 / 结构模块的阵营变体，带阵营键，新档未掌握；专属行为写成端口声明并过 FGR-EMG-014 准入
            var bad = new List<string>();
            foreach (var f in Flags)
            {
                bool def = MechanicalContentFacade.TryGet(f.Id, out MechanicalContentDef d) && d.Faction == f.Faction && d.Source == MechanicalContentSource.EliteDrop
                           && !MechanicalContentUnlock.IsUnlocked(s, f.Id);
                bool carrier = d != null && (d.Category == MechanicalContentCategory.Structure || CarrierReadings.TryGetComponent(f.Id, out _));
                if (!def || !carrier || !Admitted(f.Id))
                {
                    bad.Add(f.Id);
                }
            }
            Expect(bad.Count == 0, $"E1 6 个标志组件（静默 / 铸造 / 澄净各 2）都是现有载体或结构模块的阵营变体、带阵营键、新档未掌握，专属行为声明过准入；不合格：{string.Join("、", bad)}");

            // E2 每个：精英三选一掉落 → 解析台破解 → 破解前写不进蓝图、破解后蓝图量产并写阵营标签 → 重复获得只加库存
            List<LootPickDef> fill = LootPickCatalog.Rows.Where(r => r.Id == "pick.any.alloy" || r.Id == "pick.any.electronic").ToList();
            var steps = new List<string>();
            int okCount = 0;
            foreach (var f in Flags)
            {
                LootPickCatalog.ResetForTests();
                LootPickDef row = LootPickCatalog.Rows.FirstOrDefault(r => r.Kind == LootPickCatalog.KindModule && r.Target == f.Id && r.Pool == f.Faction);
                if (row == null)
                {
                    steps.Add(f.Id + ":池里没有");
                    continue;
                }
                LootPickCatalog.OverrideForTests(new[] { row, fill[0], fill[1] });
                LootPackageRecord p = LootPickService.Grant(s, LootPickService.SourceElite, f.Faction, m, HomeValleyLayout.RegionId, home);
                int i = Array.FindIndex(p.Candidates, c => c.RowId == row.Id);
                bool dropped = i >= 0 && LootPickService.TryPick(s, p.PacketId, i).Success && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) >= 1;
                string chassis = f.Id == "comp_ring_sprinkler" ? "chassis_fixed" : HomeValleyLayout.Erc003ChassisId;
                BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(chassis, "comp_gun", null, null, Array.Empty<string>());
                bool structure = f.Stock == "structure_module";
                CircuitOpResult before = structure ? b.TrySetStructure(s, f.Id) : b.TrySetPrimary(s, f.Id);
                HomeValleyAnalysis.AnalysisOpResult q = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
                bool cracked = q.Success && FgAnalysisBenchSelfCheck.RunToEnd(s, q.QueueItemId) && MechanicalContentUnlock.IsUnlocked(s, f.Id);
                CircuitOpResult after = structure ? b.TrySetStructure(s, f.Id) : b.TrySetPrimary(s, f.Id);
                BlueprintSaveResult sv = BlueprintEditorService.TrySave(s, b, null, "flag " + f.Id, true);
                BlueprintVersionRecord v = s.BlueprintRecords?.FirstOrDefault(r => r.BlueprintId == sv.BlueprintId)?.Versions?.LastOrDefault();
                bool bp = !before.Success && after.Success && sv.Success && v != null && (v.FactionTags ?? Array.Empty<string>()).Contains(f.Faction)
                          && (structure ? v.StructureId : v.PrimaryId) == f.Id;
                bool dup = LootPickService.DuplicateStockItem(s, f.Id) == f.Stock;
                okCount += dropped && cracked && bp && dup ? 1 : 0;
                steps.Add($"{f.Id}:掉落{(dropped ? "✓" : "✗")}破解{(cracked ? "✓" : "✗" + q.FailureReason)}蓝图{(bp ? "✓" : "✗" + before.Message + "/" + after.Message + "/" + sv.FailureReason)}重复{(dup ? "✓" : "✗")}");
                if (f.Id == "struct_stealth_cloak" && sv.Success)
                {
                    _cloakBp = sv.BlueprintId;
                }
                if (f.Id == "struct_armor_skirt" && sv.Success)
                {
                    _skirtBp = sv.BlueprintId;
                }
                if (f.Id == "comp_brace_mortar" && sv.Success)
                {
                    _mortarBp = sv.BlueprintId;
                }
                if (f.Id == "comp_ring_sprinkler" && sv.Success)
                {
                    _ringBp = sv.BlueprintId;
                }
            }
            LootPickCatalog.ResetForTests();
            Expect(okCount == Flags.Length, $"E2 6 个标志组件各走一次 掉落 → 破解 → 蓝图量产（写阵营标签）→ 重复只加库存：{string.Join("；", steps)}");

            // E3 / E4 专属行为生效（真实内核）：吸波伪装罩静止满幅度秒潜伏；装甲裙板正面减伤
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 at = home + new Vector2(3f, 3f);
            int cloak = _cloakBp != null ? L.Spawn(s, at, HomeValleyLayout.Erc003ChassisId, _cloakBp) : 0;
            int skirt = _skirtBp != null ? L.Spawn(s, at + new Vector2(2f, 0f), HomeValleyLayout.Erc003ChassisId, _skirtBp) : 0;
            int plain = L.Spawn(s, at + new Vector2(-2f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(2);
            EmgCatalog.PartDeclAmount("struct_stealth_cloak", "chs.moving", EmgDirs.Read, EmgRole.Condition, out float lurk, out _);
            bool hiddenEarly = Stealthed(site, cloak);
            WorldSimulation.StepMany((int)Math.Ceiling((lurk + 1f) * GameClock.StepHz));
            bool hidden = Stealthed(site, cloak);
            bool plainHidden = Stealthed(site, plain);
            Expect(cloak > 0 && !hiddenEarly && hidden && !plainHidden,
                $"E3 吸波伪装罩：机器静止满 {lurk} 秒进入潜伏（不能被远程锁定，与静默潜伏同一套内核结算；出生时 {hiddenEarly}、之后 {hidden}）；没装的机器不潜伏（{plainHidden}）");
            EmgCatalog.PartDeclAmount("struct_armor_skirt", "evt.hurt", EmgDirs.Read, EmgRole.Trigger, out float frac, out _);
            float armor = site != null && skirt > 0 && site.TryGetMachineUnit(skirt, out int su) ? site.Kernel.ArmorOf(su) : -1f;
            float plainArmor = site != null && site.TryGetMachineUnit(plain, out int pu) ? site.Kernel.ArmorOf(pu) : -1f;
            Expect(armor > 0f && Math.Abs(armor - frac) < 1e-4f && plainArmor == 0f,
                $"E4 装甲裙板：机器出生带正面装甲减伤 {armor:0.##}（= 声明幅度 {frac}，真实交战的正面 / 侧后对比见 F2）；没装的机器 {plainArmor:0.##}");

            // E7 改造刷新（修复轮 1，B10）：正式改造路径 MachineLoadoutRegistry.Register → Changed → RefreshMachineWeapon——没装的机器改装伪装罩后会潜伏、改装裙板后有正面装甲；
            // 再改回普通蓝图：潜伏位清掉、静止再久也不潜伏，正面装甲回到 0
            int Ver(string bp) => s.BlueprintRecords?.FirstOrDefault(r => r.BlueprintId == bp)?.Versions?.LastOrDefault()?.Version ?? 1;
            int Unit(int logic) => site != null && site.TryGetMachineUnit(logic, out int u) ? u : 0;
            int lurkSteps = (int)Math.Ceiling((lurk + 1f) * GameClock.StepHz);
            bool refit = _cloakBp != null && _skirtBp != null && MachineLoadoutRegistry.Register(s, plain, _cloakBp, Ver(_cloakBp)).Success;
            WorldSimulation.StepMany(lurkSteps);
            bool onCloak = Stealthed(site, plain);
            refit &= MachineLoadoutRegistry.Register(s, plain, _skirtBp, Ver(_skirtBp)).Success;
            bool offNow = !Stealthed(site, plain);
            float onSkirt = Unit(plain) > 0 ? site.Kernel.ArmorOf(Unit(plain)) : -1f;
            float2 skirtFacing = Unit(plain) > 0 ? View(site.Kernel, Unit(plain)).Facing : default; // 修复轮 2（P2）：改造新装裙板 = 出生同一初始朝向 (0,1)
            WorldSimulation.StepMany(lurkSteps);
            bool offLater = !Stealthed(site, plain);
            refit &= MachineLoadoutRegistry.Register(s, plain, HomeValleyLayout.BlueprintErc003Id, Ver(HomeValleyLayout.BlueprintErc003Id)).Success;
            float offSkirt = Unit(plain) > 0 ? site.Kernel.ArmorOf(Unit(plain)) : -1f;
            Expect(refit && onCloak && offNow && offLater && Math.Abs(onSkirt - frac) < 1e-4f && math.distance(skirtFacing, new float2(0f, 1f)) < 1e-4f && offSkirt == 0f,
                $"E7 改造：装上伪装罩静止后潜伏（{onCloak}），换成裙板立刻现形且静止 {lurk + 1f:0.#} 秒也不再潜伏（{offNow}/{offLater}）、正面装甲 {onSkirt:0.##}、朝向 {skirtFacing}（与出生同为 (0,1)）；改回普通蓝图正面装甲 {offSkirt:0.##}");

            // E8 兼容代价（修复轮 1，P2 口径）：归还底盘只装一件敌方阵营的标志组件，蓝图阵营标签就有两个 = 异派技术，带它出征真的记一次暴露；普通蓝图的机器不记
            bool alien = cloak > 0 && CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { cloak }, "emg4loot-test", 1);
            bool plainAlien = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { plain }, "emg4loot-test", 2);
            Expect(alien && !plainAlien, $"E8 只装一件吸波伪装罩的机器出征记“异派技术”暴露（{alien}）；普通蓝图的机器不记（{plainAlien}）");

            // E5 / E6 负向：环形喷淋架只能装固定底盘；标志组件上限；混装不同阵营 → 跨阵营标签（异派技术暴露代价）；重甲变体与其他部件 prop.mass 相加封顶
            bool mobile = !CarrierReadings.CanMount(HomeValleyLayout.Erc003ChassisId, "comp_ring_sprinkler", out string reason, out _) && reason == "component.mount.fixed_only"
                          && CarrierReadings.CanMount("chassis_fixed", "comp_ring_sprinkler", out _, out _);
            CircuitOpResult mob = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, null, Array.Empty<string>()).TrySetPrimary(s, "comp_ring_sprinkler");
            Expect(mobile && !mob.Success && !string.IsNullOrEmpty(mob.Message), $"E5 环形喷淋架装到移动底盘被拒并给原因（{mob.Message}）；固定底盘可装");
            BlueprintCircuitBoard mix = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_jammer", null, "struct_armor_skirt", Array.Empty<string>());
            string[] tags = mix.ComputeFactionTags();
            mix.UtilityId = "comp_spray_arm"; // 模拟旧档 / 外部改动：三件标志组件（正常装配接口只有主组件位与结构位能放）
            CircuitValidationResult lim = mix.Validate();
            float cap = GridContent.TryGetTuning(EmgCatalog.PartPropCapKey, out float cv) ? cv : 1f;
            float sum = EmgCatalog.PartPropModify("prop.mass", new[] { "comp_cannon", "struct_armor_skirt" }, out float raw);
            Expect(tags.Contains("silent") && tags.Contains("foundry") && lim.Issues.Any(x => x.Code == CircuitIssueCode.FlagLimit) && sum <= cap + 1e-4f && raw > cap,
                $"E6 混装静默干扰发射器 + 铸造装甲裙板：蓝图阵营标签 {string.Join("/", tags)}（≥ 2 = 异派技术暴露代价）；三件超过上限 {BlueprintCircuitBoard.MaxFlagParts} 被拒（{lim.Issues.FirstOrDefault(x => x.Code == CircuitIssueCode.FlagLimit)?.Message}）；重炮 + 装甲裙板 prop.mass {raw:0.##} 封顶 {sum:0.##}");

            CheckFlagReload(s, home, cloak);
        }

        private static int MachineUnit(CombatSite site, int logic) => site != null && logic > 0 && site.TryGetMachineUnit(logic, out int u) ? u : 0;

        private static int TurretUnit(CampaignState s, CombatSite site, BuildingRecord b)
        {
            TurretRecord r = b != null ? TurretService.Find(s, b.BuildingId) : null;
            return r != null && site != null && site.TryGetTurretUnit(r.Serial, out int u) ? u : 0;
        }

        /// <summary>不开火、不移动的敌方靶子（只给驻锄炮 / 喷淋架当目标，不打己方）。</summary>
        private static int Target(CombatSite site, Vector2 at) => site.Kernel.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(at.x, at.y), Home = new double2(at.x, at.y),
            Radius = 0.5f, Speed = 0f, Health = 50000f, MaxHealth = 50000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
        });

        /// <summary>
        /// E9 / E10 正式读档（修复轮 2，B10 / FGR-BASE-021）：存档 → CampaignRestoreOrchestrator 读档 → 家园控制器 TryRestore + RefreshAllMachineWeapons、炮塔对账（正式路径）。
        /// 潜伏中的伪装罩机器、已架设的驻锄曲射炮、预警 / 扫臂中的环形喷淋架读档后状态逐字段不变，行为参数表不增行；
        /// 另走炮塔服务真实接线：装环形喷淋架蓝图 → 内核单位带枢轴喷灌参数，换回普通主组件 → 参数清掉。
        /// </summary>
        private static void CheckFlagReload(CampaignState s, Vector2 home, int cloak)
        {
            // E9 只有潜伏中的伪装罩机器（场上没有敌人，不会因开火现形）
            CombatSite site = WorldSimulation.Home.Combat;
            int cu = MachineUnit(site, cloak);
            bool hid0 = cu > 0 && Hidden(site.Kernel, cu);
            float still0 = cu > 0 ? View(site.Kernel, cu).Silent.Still : -1f;
            int pc0 = site.Kernel.ProfileCount;
            bool ok = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual).Success;
            CampaignState back = ok ? L.Reload(Slot) : null;
            CombatSite site1 = back != null ? WorldSimulation.Home.Combat : null;
            int cu1 = MachineUnit(site1, cloak);
            bool hid1 = cu1 > 0 && Hidden(site1.Kernel, cu1);
            float still1 = cu1 > 0 ? View(site1.Kernel, cu1).Silent.Still : -2f;
            int pc1 = site1 != null ? site1.Kernel.ProfileCount : -1;
            Expect(hid0 && hid1 && still1 == still0 && pc1 == pc0,
                $"E9 正式读档：潜伏中的伪装罩机器读档后仍潜伏（{hid0} → {hid1}），静止计时 {still0:0.###} → {still1:0.###}；行为参数表 {pc0} → {pc1} 行（不增行）");
            if (back == null)
            {
                return;
            }
            s = back;
            site = site1;

            // E10 驻锄曲射炮架好、环形喷淋架（真实炮塔服务建成并装蓝图）进入预警 / 扫臂后存档读档
            int mortar = _mortarBp != null ? L.Spawn(s, home + new Vector2(-6f, 4f), HomeValleyLayout.Erc003ChassisId, _mortarBp) : 0;
            BuildingRecord ring = FgTurretSelfCheck.PlaceTurret(s, TurretCatalog.LightTypeId);
            TurretOpResult assign = ring != null && _ringBp != null ? TurretService.TryAssignBlueprint(s, ring.BuildingId, _ringBp) : default;
            TurretService.Sync(s);
            WorldSimulation.StepMany(2);
            int mu = MachineUnit(site, mortar);
            int tu = TurretUnit(s, site, ring);
            bool ringProfile = tu > 0 && site.Kernel.TryGetProfileOf(tu, out CombatBehaviorProfile rp) && rp.PivotRadius > 0f;
            if (mu > 0 && tu > 0)
            {
                CombatUnitView mv = View(site.Kernel, mu);
                int foeM = Target(site, new Vector2((float)mv.Position.x, (float)mv.Position.y + 9f));
                Target(site, ring.Position + new Vector2(3f, 0f));
                site.Kernel.FireAt(mu, foeM, site.Kernel.Time);
            }
            int maxSteps = (int)Math.Ceiling(12f * GameClock.StepHz);
            CombatFoundryUnit f0 = default;
            CombatClarityUnit c0 = default;
            for (int i = 0; i < maxSteps && mu > 0 && tu > 0; i++)
            {
                site.Kernel.TryGetFoundry(mu, out f0);
                site.Kernel.TryGetClarity(tu, out c0);
                if (f0.Deploy == 2 && c0.Pivot != 0)
                {
                    break;
                }
                WorldSimulation.StepMany(1);
            }
            int pc2 = site.Kernel.ProfileCount;
            ok = mu > 0 && tu > 0 && f0.Deploy == 2 && c0.Pivot != 0 && CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual).Success;
            CampaignState back2 = ok ? L.Reload(Slot) : null;
            CombatSite site2 = back2 != null ? WorldSimulation.Home.Combat : null;
            if (back2 != null)
            {
                TurretService.Sync(back2); // 读档后第一次炮塔对账（重推武器 + ApplyTurretFlagBehavior）
            }
            int mu2 = MachineUnit(site2, mortar);
            int tu2 = back2 != null ? TurretUnit(back2, site2, ring) : 0;
            CombatFoundryUnit f1 = default;
            CombatClarityUnit c1 = default;
            bool got = mu2 > 0 && tu2 > 0 && site2.Kernel.TryGetFoundry(mu2, out f1) & site2.Kernel.TryGetClarity(tu2, out c1);
            int pc3 = site2 != null ? site2.Kernel.ProfileCount : -1;
            Expect(assign.Ok && ringProfile && ok && got && f1.Deploy == f0.Deploy && f1.DeployT == f0.DeployT && c1.Pivot == c0.Pivot && c1.PivotT == c0.PivotT && pc3 == pc2,
                $"E10 正式读档：炮塔服务装环形喷淋架蓝图（{assign.Ok} {assign.Message}）后内核单位带枢轴喷灌参数（{ringProfile}）；驻锄曲射炮架设 {f0.Deploy}/{f0.DeployT:0.###} → 读档后 {f1.Deploy}/{f1.DeployT:0.###}，" +
                $"喷淋架 {c0.Pivot}/{c0.PivotT:0.###} → {c1.Pivot}/{c1.PivotT:0.###}；行为参数表 {pc2} → {pc3} 行");
            if (back2 == null)
            {
                return;
            }

            // E11 换回普通主组件（真实换蓝图 → 炮塔对账）：枢轴喷灌参数清掉
            FgTurretSelfCheck.AddBlueprint(back2, "emg4loot-plainfixed", FgTurretSelfCheck.Fixed("comp_gun"));
            TurretOpResult plainAssign = TurretService.TryAssignBlueprint(back2, ring.BuildingId, "emg4loot-plainfixed");
            TurretService.Sync(back2);
            int tu3 = TurretUnit(back2, site2, ring);
            bool cleared = tu3 > 0 && !site2.Kernel.TryGetProfileOf(tu3, out _);
            Expect(plainAssign.Ok && cleared, $"E11 炮塔换回普通主组件（{plainAssign.Ok} {plainAssign.Message}）：内核单位不再带枢轴喷灌参数（{cleared}）");
        }

        // ── F 阵营标志组件专属行为（修复轮 1：真实内核交战，规则与敌方版本一致）────────────────────

        private const float Dt = 1f / 30f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        /// <summary>按蓝图（底盘 + 主组件 + 结构模块）在孤立地点里放一台己方单位：武器走正式编译（CombatSite.WithProgram），行为参数 / 正面装甲走出生同一个函数。</summary>
        private static int FlagUnit(CombatSite site, string chassis, string primary, string structure, double2 at, CombatUnitKind kind, out EmgProgramResult program)
        {
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(chassis, primary, null, structure, Array.Empty<string>());
            BlueprintCircuitPreview pv = BlueprintCircuitCompiler.CompilePreview(b);
            CombatWeapon w = site.WithProgram(pv, CombatSite.MachineWeaponFrom(pv), out program, out _);
            CombatSite.FlagFrontalArmor(structure, out float frac, out float half);
            return site.Kernel.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kind, Faction = CombatFaction.Player, Behavior = kind == CombatUnitKind.Turret ? CombatBehavior.None : CombatBehavior.Commanded,
                Flags = Live, Position = at, Home = at, Radius = 0.6f, Speed = 4f, Health = 5000f, MaxHealth = 5000f, Weapon = site.WeaponIndex(w),
                BehaviorProfile = site.FlagBehaviorProfile(primary, structure), ArmorFraction = frac, ArmorHalfAngleDeg = half,
                ArmorFacing = frac > 0f ? new float2(0f, 1f) : default, Priority = 1,
            });
        }

        private static int Foe(CombatSite site, double2 at, float hp, CombatUnitFlags extra = 0) => site.Kernel.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | extra, Position = at, Home = at,
            Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp, BehaviorProfile = -1, Priority = 1,
            Weapon = site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 20f, Damage = 10f, Cooldown = 0.1f, TargetMode = CombatTargetMode.Nearest }),
        });

        private static void Run(CombatKernel k, float seconds)
        {
            int n = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static CombatUnitView View(CombatKernel k, int id)
        {
            k.TryGetUnit(id, out CombatUnitView v);
            return v;
        }

        private static bool Hidden(CombatKernel k, int id) => (View(k, id).Flags & CombatUnitFlags.Stealthed) != 0;

        /// <summary>离 <paramref name="at"/> 不超过 <paramref name="r"/> 米的活酸液斑块数（物质码 = mat.acid）。</summary>
        private static int AcidNear(CombatKernel k, double2 at, float r)
        {
            int n = 0;
            for (int i = 0; i < 4096 && k.TryGetPatch(i, out CombatZone z); i++)
            {
                if (z.Matter == EmgMatterConst.Acid && math.distance(z.Pos, at) <= r)
                {
                    n++;
                }
            }
            return n;
        }

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            var list = new List<CombatEvent>();
            for (int guard = 0; guard < 100; guard++)
            {
                CombatEvent[] ev = k.DrainGameplay(256, out int count);
                for (int i = 0; i < count; i++)
                {
                    list.Add(ev[i]);
                }
                if (count < 256)
                {
                    break;
                }
            }
            return list;
        }

        private static void CheckFlagBehaviors()
        {
            string mobile = HomeValleyLayout.Erc003ChassisId;

            // F1 吸波伪装罩（静默潜伏）：静止满幅度秒潜伏 → 敌方锁定不了；攻击命令先现形、预警满 RevealSeconds 才出手；出手后静止计时清零；移动即现形
            using (var site = new CombatSite("emg4loot-cloak", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                EmgCatalog.PartDeclAmount("struct_stealth_cloak", "chs.moving", EmgDirs.Read, EmgRole.Condition, out float lurk, out float reveal);
                float rv = Math.Max(0.8f, reveal);
                int me = FlagUnit(site, mobile, "comp_gun", "struct_stealth_cloak", new double2(0, 0), CombatUnitKind.Machine, out _);
                int foe = Foe(site, new double2(0, 6), 5000f);
                Run(k, lurk + 0.5f);
                bool hid = Hidden(k, me);
                CombatFireResult lockOn = k.FireAt(foe, me, k.Time);
                float hp0 = View(k, foe).Health;
                CombatFireResult first = k.FireAt(me, foe, k.Time);
                bool shown = !Hidden(k, me) && View(k, foe).Health == hp0;
                Run(k, rv * 0.5f);
                CombatFireResult mid = k.FireAt(me, foe, k.Time);
                Run(k, rv * 0.6f);
                CombatFireResult after = k.FireAt(me, foe, k.Time);
                bool hit = View(k, foe).Health < hp0 && View(k, me).Silent.Still == 0f;
                Run(k, lurk * 0.6f);
                bool notYet = !Hidden(k, me);
                Run(k, lurk * 0.6f);
                bool again = Hidden(k, me);

                // F1b 存读档（B10）：潜伏状态、行为参数行随内核快照来回一致，读回后按同一参数继续结算
                byte[] snap = k.Serialize();
                bool same;
                using (var site2 = new CombatSite("emg4loot-cloak-load", CombatSite.ConfigFromTuning()))
                {
                    CombatLoadResult lr = site2.Kernel.Load(snap);
                    same = lr == CombatLoadResult.Ok && Hidden(site2.Kernel, me) == Hidden(k, me) && View(site2.Kernel, me).Silent.Still == View(k, me).Silent.Still;
                    site2.Kernel.IssueCommand(me, CombatCommandKind.Move, new double2(8, 0), 0, 0.3f, 0f, 1f, false);
                    Run(site2.Kernel, 0.3f);
                    same &= !Hidden(site2.Kernel, me);
                }
                k.IssueCommand(me, CombatCommandKind.Move, new double2(8, 0), 0, 0.3f, 0f, 1f, false);
                Run(k, 0.3f);
                bool moved = !Hidden(k, me);
                Expect(hid && lockOn == CombatFireResult.Hidden && first == CombatFireResult.StillAiming && shown && mid == CombatFireResult.StillAiming
                       && after == CombatFireResult.Ok && hit && notYet && again && moved,
                    $"F1 吸波伪装罩：静止 {lurk} 秒潜伏（{hid}），敌方锁定 {lockOn}；潜伏中下攻击先现形不出手（{first}/{shown}），预警 {rv} 秒内仍 {mid}，之后 {after} 且命中、静止计时清零（{hit}）；" +
                    $"开火后静止不满 {lurk} 秒不潜伏（{notYet}）、满了再潜伏（{again}）；移动即现形（{moved}）");
                Expect(same, "F1b 存读档：潜伏状态与静止计时读回一致，读回后移动照样现形（同一行为参数行）");
            }

            // F2 装甲裙板（正面推进）：同一伤害源从正面打比从侧后打掉血少；移动后装甲朝向跟着移动方向；没装的机器朝向保持内核默认
            using (var site = new CombatSite("emg4loot-skirt", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                EmgCatalog.PartDeclAmount("struct_armor_skirt", "evt.hurt", EmgDirs.Read, EmgRole.Trigger, out float frac, out _);
                int me = FlagUnit(site, mobile, "comp_gun", "struct_armor_skirt", new double2(0, 0), CombatUnitKind.Machine, out _);
                int plain = FlagUnit(site, mobile, "comp_gun", null, new double2(20, 0), CombatUnitKind.Machine, out _);
                int front = Foe(site, new double2(0, 5), 500f);
                int rear = Foe(site, new double2(0, -5), 500f);
                float h0 = View(k, me).Health;
                k.FireAt(front, me, k.Time);
                float h1 = View(k, me).Health;
                k.FireAt(rear, me, k.Time);
                float h2 = View(k, me).Health;
                float lostFront = h0 - h1, lostRear = h1 - h2;
                k.IssueCommand(me, CombatCommandKind.Move, new double2(10, 0), 0, 0.3f, 0f, 1f, false);
                Run(k, 0.5f);
                float2 face = View(k, me).Facing;
                float2 plainFace = View(k, plain).Facing;
                Expect(lostFront > 0f && Math.Abs(lostFront - lostRear * (1f - frac)) < 0.05f && face.x > 0.95f && Math.Abs(plainFace.y + 1f) < 1e-4f,
                    $"F2 装甲裙板：正面挨一发掉 {lostFront:0.##}、侧后同一发掉 {lostRear:0.##}（正面减 {frac:P0}）；向 +x 移动后装甲朝向 ({face.x:0.##},{face.y:0.##})；" +
                    $"没装裙板的机器保持内核默认朝向 ({plainFace.x:0.##},{plainFace.y:0.##})");
            }

            // F3 喷淋臂（喷淋布区）：打中的地方留下酸液斑块（主组件声明“命中 → 排放酸液”编成内置指令）；同载体的普通喷洒器不留
            using (var site = new CombatSite("emg4loot-spray", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                int arm = FlagUnit(site, mobile, "comp_spray_arm", null, new double2(0, 0), CombatUnitKind.Machine, out EmgProgramResult prog);
                int plain = FlagUnit(site, mobile, "comp_sprayer", null, new double2(40, 0), CombatUnitKind.Machine, out EmgProgramResult plainProg);
                int foe = Foe(site, new double2(0, 4), 5000f);
                int foe2 = Foe(site, new double2(40, 4), 5000f);
                CombatFireResult r1 = k.FireAt(arm, foe, k.Time);
                CombatFireResult r2 = k.FireAt(plain, foe2, k.Time);
                Run(k, 1f);
                int acid = AcidNear(k, new double2(0, 4), 3f);
                int plainAcid = AcidNear(k, new double2(40, 4), 3f);
                bool innate = prog != null && prog.Steps.Any(x => x.Innate && x.OwnerId == "comp_spray_arm") && (plainProg == null || plainProg.IsEmpty);
                Expect(r1 == CombatFireResult.Ok && innate && acid > 0 && plainAcid == 0,
                    $"F3 喷淋臂：出手 {r1}，目标脚下酸液斑块 {acid} 块（内置指令 {innate}）；普通喷洒器出手 {r2}、酸液 {plainAcid} 块");
            }

            // F4 干扰发射器（信号干扰）：打中杂兵注入入侵度；精英按入侵抗性拒绝并发“拒绝劫持”原因事件（与重启注入同一条内核结算，不绕过抗性）
            using (var site = new CombatSite("emg4loot-jam", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                int jam = FlagUnit(site, mobile, "comp_jammer", null, new double2(0, 0), CombatUnitKind.Machine, out _);
                int minion = Foe(site, new double2(1.5, 0), 5000f);
                int elite = Foe(site, new double2(-1.5, 0), 5000f, CombatUnitFlags.Elite);
                Drain(k);
                CombatFireResult rm = k.FireAt(jam, minion, k.Time);
                CombatFireResult re = k.FireAt(jam, elite, k.Time);
                float am = k.AccumOf(minion, EmgPhaseConst.Hack), ae = k.AccumOf(elite, EmgPhaseConst.Hack);
                bool refused = Drain(k).Any(e => e.Kind == CombatEventKind.HijackRefused && e.Unit == elite);
                Expect(rm == CombatFireResult.Ok && am > 0f && ae == 0f && refused,
                    $"F4 干扰发射器：打杂兵 {rm} 入侵度 {am:0.###}；打精英 {re} 入侵度 {ae:0.###}、拒绝原因事件 {refused}");
            }

            // F5 驻锄曲射炮（架炮驻锄 + 高抛曲射，参数取自铸造步进炮行）：第一次出手先架设（瞄准中）、架好才开火、程序带抛射；贴进盲区打不到；
            // 架好后下移动命令：先收炮（收炮期间原地不动），收好才走
            using (var site = new CombatSite("emg4loot-mortar", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                GameConfig.fg.MechEnemy src = EnemyDefs.TryGet("enemy_strider", out GameConfig.fg.MechEnemy er) ? er : null;
                float dep = src?.DeploySeconds ?? 1f;
                int me = FlagUnit(site, mobile, "comp_brace_mortar", null, new double2(0, 0), CombatUnitKind.Machine, out EmgProgramResult prog);
                int far = Foe(site, new double2(0, 12), 5000f);
                int near = Foe(site, new double2(2.5, 0), 5000f);
                CombatFireResult r0 = k.FireAt(me, far, k.Time);
                k.TryGetFoundry(me, out CombatFoundryUnit f0);
                Run(k, Math.Max(0.8f, dep) + 0.1f);
                k.TryGetFoundry(me, out CombatFoundryUnit f1);
                CombatFireResult fired = CombatFireResult.StillAiming;
                for (int i = 0; i < 150 && (fired == CombatFireResult.StillAiming || fired == CombatFireResult.Cooldown); i++)
                {
                    fired = k.FireAt(me, far, k.Time);
                    Run(k, Dt);
                }
                CombatFireResult blind = k.FireAt(me, near, k.Time);
                ushort lob = EmgCatalog.Current.PortIndexById.TryGetValue("grm.mod.lob", out int li) ? EmgCatalog.Current.Ports[li].Code : (ushort)0;
                bool lobbed = prog != null && prog.KernelInstrs.Any(x => x.Port == lob) && prog.Steps.Any(x => x.Innate);
                double2 p0 = View(k, me).Position;
                k.IssueCommand(me, CombatCommandKind.Move, new double2(-10, 0), 0, 0.3f, 0f, 1f, false);
                Run(k, 0.2f);
                k.TryGetFoundry(me, out CombatFoundryUnit f2);
                bool stay = math.distance(View(k, me).Position, p0) < 1e-6 && f2.Deploy == 3;
                Run(k, Math.Max(0.8f, dep) + 0.5f);
                bool gone = View(k, me).Position.x < p0.x - 0.5;
                k.TryGetFoundry(me, out CombatFoundryUnit f3);
                Expect(src != null && r0 == CombatFireResult.StillAiming && f0.Deploy == 1 && f1.Deploy == 2 && (fired == CombatFireResult.Ok || fired == CombatFireResult.Launched)
                       && lobbed && blind == CombatFireResult.OutOfRange && stay && gone && f3.Deploy == 0,
                    $"F5 驻锄曲射炮：第一次出手 {r0}（架设中 {f0.Deploy}），{dep} 秒后已架设（{f1.Deploy}）再出手 {fired}；程序带抛射（{lobbed}）；" +
                    $"盲区内（< {src?.MinRange} 米）{blind}；架好后下移动：收炮中原地不动（{stay}，{f2.Deploy}），收好后走开（{gone}，{f3.Deploy}）");
            }

            // F6 环形喷淋架（枢轴喷灌，参数取自沉淀巨罐行，只能装固定底盘）：炮塔先亮预警，再转动喷淋臂扫过一圈；扫中的敌机掉血，脚下留下酸液斑块；圈外不受影响；本体不动
            using (var site = new CombatSite("emg4loot-ring", CombatSite.ConfigFromTuning()))
            {
                CombatKernel k = site.Kernel;
                GameConfig.fg.MechEnemy src = EnemyDefs.TryGet("enemy_settling_tank", out GameConfig.fg.MechEnemy er) ? er : null;
                int tower = FlagUnit(site, "chassis_fixed", "comp_ring_sprinkler", null, new double2(0, 0), CombatUnitKind.Turret, out _);
                int foe = Foe(site, new double2(3, 0), 5000f);
                int outside = Foe(site, new double2(30, 0), 5000f);
                Run(k, Dt * 2);
                k.TryGetClarity(tower, out CombatClarityUnit c0);
                float warn = Math.Max(0.8f, src?.WarnSeconds ?? 1f);
                Run(k, warn * 0.5f);
                bool untouched = View(k, foe).Health == 5000f;
                Run(k, warn + 360f / Math.Max(1f, src?.TurnRate ?? 70f) + 0.5f);
                k.TryGetClarity(tower, out CombatClarityUnit c1);
                bool hurt = View(k, foe).Health < 5000f && View(k, outside).Health == 5000f;
                int acid = AcidNear(k, new double2(3, 0), 3f);
                bool still = math.distance(View(k, tower).Position, double2.zero) < 1e-9;
                Expect(src != null && c0.Pivot == 1 && untouched && c1.Pivot == 2 && hurt && acid > 0 && still,
                    $"F6 环形喷淋架：发现敌机先预警（{c0.Pivot}，预警 {warn} 秒内不伤人 {untouched}），再转臂扫圈（{c1.Pivot}）；圈内敌机掉血、圈外不掉（{hurt}），脚下酸液 {acid} 块；炮塔原地（{still}）");
            }
        }


        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgEmgLootHintSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
