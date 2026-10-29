using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.SignalCore;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-UX-01 程序树预览与编译原因的自动验收（FG19 FGR-EMG-016、038；FG19A A3；FGU-48；FGT-EMG-018；FGT-UX-001 的 FGU-48 部分）。
    /// 起真实编译链（BlueprintCircuitCompiler / UplinkCompiler.CompileDual → EmgProgramCompiler）与真 UXML（CircuitBoardPanel.uxml + ProgramTreeView）断言：
    /// A 12 组以上固定程序（FgEmgProgramSelfCheck.FixedCases，FGT-EMG-010 同一组）的预览由显示行还原出的树、原因与编译器逐项一致，界面标签与行一致、形状符号区分自有 / 继承 / 溢出；
    /// B 八个原因键（FGR-EMG-038 六个 + 表满 + 旧内容版本）中英文与解决办法都在，每个都在预览里出现，按办法操作后消失（刷写两条用夹具注入，产出方 EMG1-FLASH-01）；
    /// C 单位级提升标记与提升溢出；D 空程序 / 旧读法 / 接入口不在路径上；E 调序与撤销后即时刷新；F 暂停与 0.5x～3x 不改预览、蓝图存读往返不变；
    /// G 固件悬停 / 详情的读写端口（只读声明表）；H 引导钩子；I 中英文；J 布局探针（中英 × UI 缩放 × 四种分辨率，长混合程序不出栏）；K 耗时。
    /// </summary>
    public static class FgEmgProgramTreeSelfCheck
    {
        private const string UxmlPath = "Assets/GameRes/Raw/UI/CircuitBoard/CircuitBoardPanel.uxml";
        private const string Homing = "fw_homing", Split = "fw_split", Pierce = "fw_pierce", Oil = "fw_oilleak", Etch = "fw_mix_etchshell", Vent = "fw_ventarc";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/涌现程序树预览")]
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
            Line("\n[程序树预览] EMG1-UX-01：程序树预览与编译原因（FGT-EMG-018；FGU-48）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；预览在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            GameLanguage originalLanguage = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                CarrierReadings.Reload();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                FgEmgProgramSelfCheck.UseStage1Catalog();
                Expect(EmgCatalog.EffectiveStage == 1, $"测试目录：真实表 + FG19A 测试声明，生效阶段 {EmgCatalog.EffectiveStage}");
                Step(CheckFixedPrograms);
                Step(CheckReasons);
                Step(CheckPromoted);
                Step(CheckEmptyLegacyOffPath);
                Step(CheckReorderUndo);
                Step(CheckPauseSpeedSave);
                Step(CheckPorts);
                Step(CheckHook);
                Step(CheckEnglish);
                Step(CheckLayout);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"程序树预览自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [程序树预览] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ──

        private static EmgProgramResult P(string[] uplink, string[] local, int cap = 0) =>
            EmgProgramCompiler.Compile(uplink ?? Array.Empty<string>(), local ?? Array.Empty<string>(), FirmwareCarrier.Projectile, cap);

        private static string[] A(params string[] ids) => ids;

        private static string Short(string id) => id != null && id.StartsWith("fw_", StringComparison.Ordinal) ? id.Substring(3) : id;

        private static List<EmgTreeRow> Rows(EmgProgramResult r, string offPath = null, bool tableFull = false)
        {
            var rows = new List<EmgTreeRow>();
            EmgProgramTreeText.BuildRows(r, rows, offPath, tableFull);
            return rows;
        }

        /// <summary>只从显示行还原树（父 = 前面最近一个更浅的节点行，代 = 缩进，占用 = 该节点下显示的指令行数），格式与 FgEmgProgramSelfCheck.Tree 相同。</summary>
        private static string ViewTree(EmgProgramResult r, List<EmgTreeRow> rows)
        {
            var parent = new Dictionary<int, int>();
            var gen = new Dictionary<int, int>();
            var entries = new Dictionary<int, List<string>>();
            var stack = new List<(int Node, int Depth)>();
            foreach (EmgTreeRow row in rows)
            {
                if (row.Kind == EmgTreeRowKind.Node)
                {
                    while (stack.Count > 0 && stack[stack.Count - 1].Depth >= row.Depth)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    parent[row.Node] = stack.Count > 0 ? stack[stack.Count - 1].Node : -1;
                    gen[row.Node] = row.Depth;
                    entries[row.Node] = new List<string>();
                    stack.Add((row.Node, row.Depth));
                }
                else if ((row.Kind == EmgTreeRowKind.Own || row.Kind == EmgTreeRowKind.Inherited) && entries.ContainsKey(row.Node))
                {
                    entries[row.Node].Add((row.Kind == EmgTreeRowKind.Inherited ? "^" : string.Empty) + Short(r.Steps[row.Ordinal].OwnerId));
                }
            }
            return string.Join(" | ", entries.Keys.OrderBy(k => k).Select(k =>
            {
                EmgTreeNode n = r.Nodes[k];
                string head = $"{k}{(parent[k] >= 0 ? "<" + parent[k] : string.Empty)} g{gen[k]} {entries[k].Count}/{n.Capacity}{(parent[k] >= 0 ? $" {n.TrigEvent.ToString().ToLowerInvariant()}×{n.TrigCount}" : string.Empty)}:";
                return head + string.Concat(entries[k].Select(e => " " + e));
            }));
        }

        /// <summary>显示的原因行（去重后）还原成 FgEmgProgramSelfCheck.Notes 的格式。</summary>
        private static string ViewNotes(EmgProgramResult r, List<EmgTreeRow> rows) => string.Join(", ", rows.Where(x => x.Kind == EmgTreeRowKind.Reason && x.Ordinal >= 0).Select(x =>
            $"{x.NoteKey.Substring("emg.compile.".Length)}:{Short(r.Steps[x.Ordinal].OwnerId)}{(x.Node >= 0 ? "@" + x.Node : string.Empty)}"));

        private static string DistinctNotes(EmgProgramResult r) => string.Join(", ", r.Notes.Select((n, i) => (n, i))
            .Where(t => !r.Notes.Take(t.i).Any(m => m.Key == t.n.Key && m.Ordinal == t.n.Ordinal && m.Node == t.n.Node))
            .Select(t => $"{t.n.Key.Substring("emg.compile.".Length)}:{Short(r.Steps[t.n.Ordinal].OwnerId)}{(t.n.Node >= 0 ? "@" + t.n.Node : string.Empty)}"));

        private static bool HasReason(List<EmgTreeRow> rows, string key) =>
            rows.Any(x => x.Kind == EmgTreeRowKind.Reason && x.NoteKey == key && x.Text.Contains(GameText.Get(EmgProgramTreeText.FixKey(key))));

        private static string Flat(List<EmgTreeRow> rows) => string.Join(" / ", rows.Select(x => x.Text));

        private static VisualElement Mount(out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgEmgProgramTreeSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        // ── A. FGT-EMG-018：预览与 FGT-EMG-010 的树逐项一致 ──

        private static void CheckFixedPrograms()
        {
            Line("  · A. FGT-EMG-018：12 组以上固定程序（与 FGT-EMG-010 同一组）——只从预览行还原的树、原因与编译器逐项一致；界面标签与行一致，形状区分自有 / 继承 / 溢出");
            (string Name, EmgProgramResult R, string Tree, string Notes)[] cases = FgEmgProgramSelfCheck.FixedCases();
            Expect(cases.Length >= 12, $"固定程序 {cases.Length} 组（≥ 12）");
            VisualElement root = Mount(out GameObject go);
            try
            {
                var view = new ProgramTreeView();
                List<string> missing = view.Bind(root);
                Expect(missing.Count == 0, $"程序树元素都在 CircuitBoardPanel.uxml 里{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");
                foreach ((string name, EmgProgramResult r, string tree, string notes) in cases)
                {
                    List<EmgTreeRow> rows = Rows(r);
                    string vt = ViewTree(r, rows), vn = ViewNotes(r, rows);
                    bool headers = r.Nodes.All(n => rows.Any(x => x.Kind == EmgTreeRowKind.Node && x.Node == n.Index && x.Text.StartsWith(EmgProgramTreeText.GlyphNode, StringComparison.Ordinal)
                                                                  && x.Text.Contains($"{n.Used}/{n.Capacity}") && (n.Parent < 0 || x.Text.Contains(EmgProgramTreeText.EventName(n.TrigEvent)))));
                    bool shapes = rows.Where(x => x.Kind == EmgTreeRowKind.Own).All(x => x.Text.StartsWith(EmgProgramTreeText.GlyphOwn, StringComparison.Ordinal))
                                  && rows.Where(x => x.Kind == EmgTreeRowKind.Inherited).All(x => x.Text.StartsWith(EmgProgramTreeText.GlyphInherited, StringComparison.Ordinal))
                                  && rows.Where(x => x.Kind == EmgTreeRowKind.Overflow).All(x => x.Text.StartsWith(EmgProgramTreeText.GlyphOverflow, StringComparison.Ordinal));
                    var overflowOrd = rows.Where(x => x.Kind == EmgTreeRowKind.Overflow).Select(x => x.Ordinal).ToList();
                    bool overflow = overflowOrd.SequenceEqual(r.Steps.Where(s => s.Overflow).Select(s => s.Ordinal));
                    string heat = r.HeatTotal.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    bool heatRow = rows.Count(x => x.Kind == EmgTreeRowKind.Heat) == 1 && rows.First(x => x.Kind == EmgTreeRowKind.Heat).Text.EndsWith(heat, StringComparison.Ordinal);
                    view.RenderColumn(ProgramTreeView.ColumnAi, r, null, false);
                    bool ui = Enumerable.Range(0, rows.Count).All(i => view.LabelText(ProgramTreeView.ColumnAi, i) == rows[i].Text
                                                                       && view.LabelHasClass(ProgramTreeView.ColumnAi, i, "cb-pt-inherited") == (rows[i].Kind == EmgTreeRowKind.Inherited))
                              && view.LabelText(ProgramTreeView.ColumnAi, rows.Count) == null;
                    bool ok = vt == r.Tree() && vt == tree && vn == DistinctNotes(r) && headers && shapes && overflow && heatRow && ui;
                    Expect(ok, $"{name}：预览还原 {vt}{(vn.Length > 0 ? "；原因 " + vn : string.Empty)}" +
                               (ok ? string.Empty : $"（编译器 {r.Tree()}；{DistinctNotes(r)}；节点行 {headers} 形状 {shapes} 溢出 {overflow} 热量 {heatRow} 界面 {ui}）"));
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── B. 八个原因键 ──

        private static void CheckReasons()
        {
            Line("  · B. 原因键：FGR-EMG-038 六个 + 表满 + 旧内容版本，中英文与解决办法都在；每个都出现在预览里，按办法操作后消失");
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string[] missing = EmgProgramTreeText.ReasonKeys.Where(k => GameText.Get(k) == k || GameText.Get(EmgProgramTreeText.FixKey(k)) == EmgProgramTreeText.FixKey(k)
                                                                           || GameText.ContainsMarker(GameText.Get(k))).ToArray();
                Expect(EmgProgramTreeText.ReasonKeys.Length == 8 && missing.Length == 0,
                    $"[{lang}] 8 个原因键都有文本与解决办法{(missing.Length > 0 ? "，缺 " + string.Join("、", missing) : string.Empty)}（例：{GameText.Get(EmgProgramCompiler.NoteOverflow)} {GameText.Get(EmgProgramTreeText.FixKey(EmgProgramCompiler.NoteOverflow))}）");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);

            void Pair(string key, string what, EmgProgramResult bad, EmgProgramResult fixedR, string how)
            {
                List<EmgTreeRow> b = Rows(bad), f = Rows(fixedR);
                Expect(HasReason(b, key) && !f.Any(x => x.NoteKey == key),
                    $"{what}：出现“{b.FirstOrDefault(x => x.NoteKey == key).Text}”；按提示{how}后消失（{Flat(f.Where(x => x.Kind == EmgTreeRowKind.Reason).ToList())}）");
            }
            Pair(EmgProgramCompiler.NoteOverflow, "溢出 [溶蚀弹头, 溶蚀弹头, 寻的, 贯穿]", P(A(Etch, Etch), A(Homing, Pierce)), P(A(Etch), A(Homing, Pierce)), "去掉一条混合固件");
            Pair(EmgProgramCompiler.NoteGenFull, "代际已满 [分裂, 分裂, 分裂]", P(A(Split), A(Split, Split)), P(null, A(Split, Split)), "去掉前面的一个触发器");
            Pair(EmgProgramCompiler.NoteNotFlashed, "未刷写 [寻的, 贯穿, 分裂, 漏油]", P(A(Homing, Pierce), A(Split, Oil)), P(A(Homing, Pierce), A(Split)), "减少触发器后面的指令（去掉漏油）");
            Pair(EmgProgramCompiler.NoteDuplicate, "重复无效 [寻的, 寻的]", P(null, A(Homing, Homing)), P(null, A(Homing)), "去掉重复的那条");

            // 刷写两条：编译器不产出（产出方 EMG1-FLASH-01：无人机 / 哨戒桩程序截断与距离衰减），预览按同一路径渲染注入的原因
            foreach (string key in new[] { EmgProgramTreeText.NoteFlashTruncated, EmgProgramTreeText.NoteFlashRange })
            {
                EmgProgramResult r = P(null, A(Split, Homing));
                r.Notes.Add(new EmgCompileNote { Key = key, Ordinal = 1, Node = -1 });
                List<EmgTreeRow> rows = Rows(r);
                Expect(HasReason(rows, key) && !HasReason(Rows(P(null, A(Split, Homing))), key),
                    $"{key}（夹具注入，真实产出随 EMG1-FLASH-01）：预览显示“{rows.First(x => x.NoteKey == key).Text}”；不注入时不出现");
            }

            // 表满：经 CombatSites 登记的真实地点程序表填满后，新装配登记被拒 → 电路编辑器的 ProgramTreeView（走 CombatSites.All 取数）显示；
            // 换回这处地点已用过的装配即恢复
            BlueprintCircuitBoard usedBoard = FgEmgProgramSelfCheck.GunBoard(true, Pierce, Oil), freshBoard = FgEmgProgramSelfCheck.GunBoard(false, Homing, Split);
            BlueprintCircuitPreview used = UplinkCompiler.CompileUplinked(usedBoard, new[] { Split });
            BlueprintCircuitPreview fresh = BlueprintCircuitCompiler.CompilePreview(freshBoard);
            const string fullSiteId = "emgtree-full";
            VisualElement fullRoot = Mount(out GameObject fullGo);
            try
            {
                CombatSite site = CombatSites.Open(fullSiteId, null, false, null, out _);
                var panelView = new ProgramTreeView();
                panelView.Bind(fullRoot);
                site.WithProgram(used, CombatSite.MachineWeaponFrom(used), out EmgProgramResult usedRes, out string usedNote);
                for (int i = site.Kernel.ProgramCount; i < EmgConst.MaxPrograms; i++)
                {
                    site.Kernel.AddProgram(FgEmergenceKernelSelfCheck.SampleProgram(i), FgEmergenceKernelSelfCheck.SampleNodes(), FgEmergenceKernelSelfCheck.SampleInstrs());
                }
                site.WithProgram(fresh, CombatSite.MachineWeaponFrom(fresh), out EmgProgramResult freshRes, out string freshNote);
                panelView.Render(UplinkCompiler.CompileDual(freshBoard, Array.Empty<string>()), null);
                List<EmgTreeRow> bad = panelView.Rows(ProgramTreeView.ColumnAi).ToList();
                bool badIsFresh = panelView.Result(ProgramTreeView.ColumnAi)?.Signature == freshRes.Signature;
                CombatWeapon back = site.WithProgram(used, CombatSite.MachineWeaponFrom(used), out EmgProgramResult backRes, out string backNote);
                panelView.Render(UplinkCompiler.CompileDual(usedBoard, new[] { Split }), null);
                List<EmgTreeRow> ok = panelView.Rows(ProgramTreeView.ColumnUplinked).ToList();
                bool okIsUsed = panelView.ColumnVisible(ProgramTreeView.ColumnUplinked) && panelView.Result(ProgramTreeView.ColumnUplinked)?.Signature == usedRes.Signature;
                Expect(usedNote == null && freshNote == EmgProgramCompiler.NoteTableFull && badIsFresh && HasReason(bad, EmgProgramCompiler.NoteTableFull)
                       && backNote == null && back.Program >= 0 && okIsUsed && !ok.Any(x => x.NoteKey == EmgProgramCompiler.NoteTableFull) && !site.IsProgramRejected(usedRes.Signature),
                    $"表满（{site.Kernel.ProgramCount} 行，经 CombatSites 登记）：新装配被拒，电路编辑器程序树显示“{bad.FirstOrDefault(x => x.NoteKey == EmgProgramCompiler.NoteTableFull).Text}”；按提示换回这处已用过的装配：登记成功（下标 {back.Program}），面板上原因消失（DEBT-EMG1PRG01-01）");
            }
            finally
            {
                CombatSites.Close(fullSiteId, null, dropRecord: false);
                Object.DestroyImmediate(fullGo);
            }

            // 旧内容版本：面板级——真 UXML 上的程序树摘要
            VisualElement root = Mount(out GameObject go);
            try
            {
                var view = new ProgramTreeView();
                view.Bind(root);
                BlueprintCircuitBoard board = FgEmgProgramSelfCheck.GunBoard(false, Pierce);
                BlueprintVersionRecord old = board.ToVersion(1, 0f);
                old.ContentVersionAtCompile = CampaignSaveService.CurrentContentVersion - 1;
                UplinkDualPreview dual = UplinkCompiler.CompileDual(board, Array.Empty<string>());
                view.Render(dual, old);
                string staleSummary = view.SummaryText;
                bool staleShown = !root.Q<Label>("ProgramTreeSummary").ClassListContains("cb-hidden");
                BlueprintVersionRecord resaved = BlueprintCircuitBoard.FromVersion(old).ToVersion(2, 0f);
                view.Render(dual, resaved);
                Expect(staleSummary.Contains(GameText.Get(EmgProgramCompiler.StaleContentKey)) && staleSummary.Contains(GameText.Get(EmgProgramTreeText.FixKey(EmgProgramCompiler.StaleContentKey)))
                       && staleShown && view.SummaryText.Length == 0 && root.Q<Label>("ProgramTreeSummary").ClassListContains("cb-hidden"),
                    $"旧内容版本的蓝图打开后程序树摘要：“{staleSummary}”；按提示重新保存后不再显示");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── C. 单位级提升（FG19A A3）──

        private static void CheckPromoted()
        {
            Line("  · C. 单位级提升：被提升的指令标“已提升：单位事件只在本机程序上生效”；根容量满时溢出行写“已提升但根容量已满”");
            List<EmgTreeRow> rows = Rows(P(A(Split, Homing), A(Pierce, Vent)));
            EmgTreeRow vent = rows.FirstOrDefault(x => x.Kind == EmgTreeRowKind.Own && x.Promoted);
            string tag = GameText.Get("emg.tree.tag.promoted");
            Expect(vent.Text != null && vent.Node == 0 && vent.Text.Contains(tag) && vent.Text.Contains(EmgProgramTreeText.GlyphPromoted) && rows.Count(x => x.Promoted) == 1
                   && !rows.Where(x => x.Kind == EmgTreeRowKind.Own && !x.Promoted).Any(x => x.Text.Contains(tag)),
                $"[分裂, 寻的, 贯穿, 过热泄放]：只有过热泄放标提升——“{vent.Text}”");
            EmgProgramResult full = P(A(Vent, Vent), A(Vent, Split), cap: 2);
            List<EmgTreeRow> fr = Rows(full);
            string promotedOverflow = GameText.Format("emg.tree.entry.overflow_promoted", EmgProgramTreeText.StepName(full.Steps[2]));
            Expect(fr.Any(x => x.Kind == EmgTreeRowKind.Overflow && x.Promoted && x.Text.Contains(promotedOverflow)) && fr.Any(x => x.Kind == EmgTreeRowKind.Overflow && !x.Promoted),
                $"根容量 2 放满后的过热泄放：{Flat(fr.Where(x => x.Kind == EmgTreeRowKind.Overflow).ToList())}");
            Expect(!LeaksId(Flat(rows)) && !LeaksId(Flat(fr)),
                $"没有显示名的指令不露内容 ID（写“{GameText.Get(EmgProgramTreeText.UnknownKey)}”，B16）：{Flat(fr)}");
            List<EmgTreeRow> mixed = Rows(P(A(Etch), null));
            Expect(mixed.Count(x => x.Kind == EmgTreeRowKind.Own) == 2 && mixed.Where(x => x.Kind == EmgTreeRowKind.Own).All(x => x.Text.Contains(FirmwareKinds.DisplayName(Etch)))
                   && mixed.First(x => x.Kind == EmgTreeRowKind.Node).Text.Contains("2/4"),
                $"混合固件一槽两指令、计 2 条容量：{Flat(mixed.Take(3).ToList())}");
        }

        // ── D. 空程序、旧读法、接入口不在路径上 ──

        private static void CheckEmptyLegacyOffPath()
        {
            Line("  · D. 程序为空显示“基础攻击”（B12）；装着但声明不生效的固件写“仍按原读法”；接入口不在路径上时说明接入固件为何不进程序");
            UplinkDualPreview none = UplinkCompiler.CompileDual(FgEmgProgramSelfCheck.GunBoard(false), Array.Empty<string>());
            var view = new ProgramTreeView();
            view.Render(none, null);
            IReadOnlyList<EmgTreeRow> empty = view.Rows(ProgramTreeView.ColumnAi);
            Expect(empty.Count == 1 && empty[0].Kind == EmgTreeRowKind.Empty && empty[0].Text == GameText.Get("emg.tree.empty") && empty[0].Text.Contains("基础攻击")
                   && view.Rows(ProgramTreeView.ColumnUplinked).Count == 0,
                $"没有固件的连射器：“{empty[0].Text}”；没有接入口时不显示接入栏");

            EmgCatalog.ResetForTests(); // 正式表 emg.stage = 0：全部固件走旧读法
            List<EmgTreeRow> legacy = Rows(EmgProgramCompiler.Compile(BlueprintCircuitCompiler.CompilePreview(FgEmgProgramSelfCheck.GunBoard(false, Split))));
            FgEmgProgramSelfCheck.UseStage1Catalog();
            Expect(legacy.Count == 2 && legacy[0].Kind == EmgTreeRowKind.Empty && legacy[1].Kind == EmgTreeRowKind.Legacy && legacy[1].Text.Contains(FirmwareKinds.DisplayName(Split)),
                $"emg.stage = 0 装着分裂：{Flat(legacy)}");

            BlueprintCircuitBoard off = FgEmgProgramSelfCheck.GunBoard(false, Pierce, Oil);
            int offSlot = Enumerable.Range(1, BlueprintCircuitLayout.SlotCount - 2).FirstOrDefault(i => BlueprintCircuitLayout.IsUplinkCandidate(i) && string.IsNullOrEmpty(off.SlotContentIds[i]) && !off.IsOnSourceSinkPath(i));
            bool placed = offSlot > 0 && off.TrySetUplink(offSlot).Success;
            UplinkDualPreview dual = UplinkCompiler.CompileDual(off, new[] { Split });
            view.Render(dual, null);
            IReadOnlyList<EmgTreeRow> up = view.Rows(ProgramTreeView.ColumnUplinked);
            EmgTreeRow offRow = up.FirstOrDefault(x => x.Kind == EmgTreeRowKind.OffPath);
            Expect(placed && offRow.Text != null && offRow.Text.Contains(FirmwareKinds.DisplayName(Split)) && view.Result(ProgramTreeView.ColumnUplinked).Tree() == "0 g0 2/4: pierce oilleak"
                   && !view.Rows(ProgramTreeView.ColumnAi).Any(x => x.Kind == EmgTreeRowKind.OffPath),
                $"接入口（{offSlot} 号格）不在路径上：接入栏写“{offRow.Text}”，程序只有机器槽");
            BlueprintCircuitBoard onPath = FgEmgProgramSelfCheck.GunBoard(true, Pierce, Oil);
            view.Render(UplinkCompiler.CompileDual(onPath, new[] { Split }), null);
            Expect(!view.Rows(ProgramTreeView.ColumnUplinked).Any(x => x.Kind == EmgTreeRowKind.OffPath) && view.Result(ProgramTreeView.ColumnUplinked).Tree() == "0 g0 1/4: split | 1<0 g1 2/2 hit×3: pierce oilleak"
                   && view.Result(ProgramTreeView.ColumnAi).Tree() == "0 g0 2/4: pierce oilleak",
                $"对照：接入口在路径上——AI 栏 {view.Result(ProgramTreeView.ColumnAi).Tree()}，接入栏 {view.Result(ProgramTreeView.ColumnUplinked).Tree()}（接入固件在前）");
        }

        // ── E. 调序、撤销后即时刷新 ──

        private static void CheckReorderUndo()
        {
            Line("  · E. 程序顺序 = 槽位顺序：调换固件槽、清空后撤销，下一次刷新的预览即是新编译结果（B02、B03）");
            var view = new ProgramTreeView();
            BlueprintCircuitBoard a = FgEmgProgramSelfCheck.GunBoard(false, Split, Oil);
            view.Render(UplinkCompiler.CompileDual(a, Array.Empty<string>()), null);
            string ta = view.Result(ProgramTreeView.ColumnAi).Tree();
            BlueprintCircuitBoard b = FgEmgProgramSelfCheck.GunBoard(false, Oil, Split);
            view.Render(UplinkCompiler.CompileDual(b, Array.Empty<string>()), null);
            string tb = view.Result(ProgramTreeView.ColumnAi).Tree();
            Expect(ta == "0 g0 1/4: split | 1<0 g1 1/2 hit×3: oilleak" && tb != ta && tb == ViewTree(view.Result(ProgramTreeView.ColumnAi), view.Rows(ProgramTreeView.ColumnAi).ToList()),
                $"[分裂, 漏油] → {ta}；调换为 [漏油, 分裂] → {tb}");
            Expect(a.TryClearFirmware(1).Success, "清空 1 号固件槽");
            view.Render(UplinkCompiler.CompileDual(a, Array.Empty<string>()), null);
            string cleared = view.Result(ProgramTreeView.ColumnAi).Tree();
            bool undone = a.Undo();
            view.Render(UplinkCompiler.CompileDual(a, Array.Empty<string>()), null);
            Expect(cleared == "0 g0 1/4: split | 1<0 g1 0/2 hit×3:" && undone && view.Result(ProgramTreeView.ColumnAi).Tree() == ta,
                $"清空漏油 → {cleared}；撤销后恢复 {view.Result(ProgramTreeView.ColumnAi).Tree()}");
        }

        // ── F. 暂停 / 倍速、存读 ──

        private static void CheckPauseSpeedSave()
        {
            Line("  · F. 战略暂停与 0.5x～3x 下预览照常且相同（只读编译结果，不随时钟）；蓝图存读往返后预览不变（预览不新增存档状态）");
            BlueprintCircuitBoard board = FgEmgProgramSelfCheck.GunBoard(true, Pierce, Oil);
            string Snap()
            {
                var v = new ProgramTreeView();
                v.Render(UplinkCompiler.CompileDual(board, new[] { Split }), null);
                return Flat(v.Rows(ProgramTreeView.ColumnAi).ToList()) + " || " + Flat(v.Rows(ProgramTreeView.ColumnUplinked).ToList());
            }
            string baseline = Snap();
            var diffs = new List<string>();
            try
            {
                GameClock.SetPaused(true);
                if (Snap() != baseline) diffs.Add("暂停");
                GameClock.SetPaused(false);
                foreach (float s in GameClock.Speeds)
                {
                    GameClock.SetSpeed(s);
                    if (Snap() != baseline) diffs.Add($"{s}x");
                }
            }
            finally
            {
                GameClock.ResetSession();
            }
            Expect(diffs.Count == 0 && baseline.Length > 0, $"暂停与 {string.Join("/", GameClock.Speeds)}x 下预览一致{(diffs.Count > 0 ? "，不一致：" + string.Join("、", diffs) : string.Empty)}");
            BlueprintCircuitBoard loaded = BlueprintCircuitBoard.FromVersion(board.ToVersion(1, 0f));
            var lv = new ProgramTreeView();
            lv.Render(UplinkCompiler.CompileDual(loaded, new[] { Split }), null);
            Expect(Flat(lv.Rows(ProgramTreeView.ColumnAi).ToList()) + " || " + Flat(lv.Rows(ProgramTreeView.ColumnUplinked).ToList()) == baseline,
                "蓝图版本存读往返（ToVersion → FromVersion）后两栏预览逐行相同");
        }

        // ── G. 读写端口 ──

        private static void CheckPorts()
        {
            Line("  · G. 固件悬停 / 详情显示读、写端口（只读声明表；混合固件取两父）");
            EmgCompiled c = EmgCatalog.Current;
            string Names(string owner, EmgDirs dir) => string.Join("|", c.Decls.Where(d => d.Owner == c.OwnerIndex[owner] && d.Dir == dir)
                .Select(d => GameText.Get("emg.port." + c.PortIdByCode[d.Port] + ".name")).Distinct());
            string homing = EmgProgramTreeText.PortsLine(Homing);
            string[] hr = Names(Homing, EmgDirs.Read).Split('|'), hw = Names(Homing, EmgDirs.Write).Split('|');
            Expect(homing != null && hr.All(n => n.Length > 0 && homing.Contains(n)) && hw.All(n => n.Length > 0 && homing.Contains(n)),
                $"寻的：“{homing}”（声明读 {string.Join("、", hr)}，写 {string.Join("、", hw)}）");
            string mix = EmgProgramTreeText.PortsLine(Etch);
            FirmwareKinds.TryGetParents(Etch, out string pa, out string pb);
            string[] all = Names(pa, EmgDirs.Write).Split('|').Concat(Names(pb, EmgDirs.Write).Split('|')).Where(n => n.Length > 0).ToArray();
            Expect(mix != null && all.Length > 0 && all.All(mix.Contains), $"混合固件溶蚀弹头取两父（{Short(pa)} + {Short(pb)}）：“{mix}”");
            string detail = CarrierReadings.DetailLines(Homing);
            string tip = SignalCoreHudUIToolkit.ReadingTip(Homing);
            Expect(detail.Contains(homing) && tip.Contains(homing) && EmgProgramTreeText.PortsLine("organ_focus") == null && CarrierReadings.DetailLines("organ_focus") == null,
                "固件详情（固件库 / 信号核悬停共用 CarrierReadings.DetailLines）含端口行；不是固件的不列");
        }

        // ── H. 引导钩子 ──

        private static void CheckHook()
        {
            Line("  · H. 引导钩子 emg_first_program_tree：第一次看到非空程序树时发（内容归 FG15-UX-04）");
            var touched = new List<string>();
            void OnTouch(string id) => touched.Add(id);
            GuidanceHooks.Touched += OnTouch;
            try
            {
                var v = new ProgramTreeView();
                v.Render(UplinkCompiler.CompileDual(FgEmgProgramSelfCheck.GunBoard(false), Array.Empty<string>()), null);
                bool quietOnEmpty = !touched.Contains(GuidanceHooks.EmgFirstProgramTree);
                v.Render(UplinkCompiler.CompileDual(FgEmgProgramSelfCheck.GunBoard(false, Split), Array.Empty<string>()), null);
                v.Render(UplinkCompiler.CompileDual(FgEmgProgramSelfCheck.GunBoard(false, Split), Array.Empty<string>()), null);
                Expect(GuidanceHooks.EmgFirstProgramTree == "emg_first_program_tree" && GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstProgramTree)
                       && quietOnEmpty && touched.Count(t => t == GuidanceHooks.EmgFirstProgramTree) == 1,
                    $"空程序不发；非空程序发 1 次（同一视图再刷新不重复）；已登记 GuidanceHooks.Known");
            }
            finally
            {
                GuidanceHooks.Touched -= OnTouch;
            }
        }

        // ── I. 中英文 ──

        private static void CheckEnglish()
        {
            Line("  · I. 英文：程序树全部文本走文本键，没有缺键标记");
            (string Name, EmgProgramResult R, string Tree, string Notes)[] cases = FgEmgProgramSelfCheck.FixedCases();
            string zh = Flat(Rows(cases[6].R));
            GameSettings.SetLanguage(GameLanguage.En);
            try
            {
                var bad = new List<string>();
                string sample = null;
                foreach ((string name, EmgProgramResult r, _, _) in cases)
                {
                    string t = Flat(Rows(r, "X", true));
                    sample ??= t;
                    if (GameText.ContainsMarker(t) || LeaksId(t))
                    {
                        bad.Add(name);
                    }
                }
                string en = Flat(Rows(cases[6].R));
                Expect(bad.Count == 0 && en != zh && en.Contains("Root program") && EmgProgramTreeText.PortsLine(Homing).StartsWith("Ports:", StringComparison.Ordinal),
                    $"英文无缺键（{(bad.Count > 0 ? string.Join("、", bad) : "全部")}）：{en.Substring(0, Math.Min(160, en.Length))}");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        /// <summary>显示文本里露出内容 ID（fw_ / comp_ 开头）= 漏了显示名兜底（B16）。</summary>
        private static bool LeaksId(string t) =>
            t.Contains("fw_") || t.Contains("comp_");

        // ── J. 布局探针 ──

        private static void CheckLayout()
        {
            Line("  · J. 布局探针（FGT-UX-001 的 FGU-48）：电路编辑器面板显示最长的混合程序（两栏、溢出、全部原因），中英 × UI 缩放 0.8 / 1 / 1.5 × 四种分辨率");
            EmgProgramResult longR = P(A(Etch, Etch), A(Etch, Etch));
            longR.Notes.Add(new EmgCompileNote { Key = EmgProgramTreeText.NoteFlashTruncated, Ordinal = 7, Node = -1 });
            longR.Notes.Add(new EmgCompileNote { Key = EmgProgramTreeText.NoteFlashRange, Ordinal = 6, Node = -1 });
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                {
                    string result = UiToolkitLayoutProbe.Probe(UxmlPath, "CircuitBoardPanelRoot", stressFill: true, prepare: r =>
                    {
                        var v = new ProgramTreeView();
                        v.Bind(r.panel.visualTree);
                        BlueprintVersionRecord old = FgEmgProgramSelfCheck.GunBoard(false, Pierce).ToVersion(1, 0f);
                        old.ContentVersionAtCompile = CampaignSaveService.CurrentContentVersion - 1;
                        v.Render(null, old);
                        v.RenderColumn(ProgramTreeView.ColumnAi, longR, null, true);
                        v.RenderColumn(ProgramTreeView.ColumnUplinked, P(A(Homing, Pierce), A(Split, Oil)), UplinkCompiler.FirmwareName(Split), false);
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 CircuitBoardPanel.uxml（含程序树）[{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
        }

        // ── K. 耗时 ──

        private static void CheckPerformance()
        {
            Line("  · K. 耗时：面板每 0.2 秒刷新一次程序树（两栏各编译 + 排行），只测一次");
            BlueprintCircuitBoard board = FgEmgProgramSelfCheck.GunBoard(true, Pierce, Oil);
            UplinkDualPreview dual = UplinkCompiler.CompileDual(board, new[] { Split, Homing });
            var view = new ProgramTreeView();
            view.Render(dual, null);
            const int n = 500;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                view.Render(dual, null);
            }
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / n;
            const double limit = 1.0;
            if (ms > limit && ms <= limit * 2)
            {
                Line($"    ⚠ 性能警告：每次刷新 {ms:0.000} ms，超过 {limit} ms 但不到 2 倍（整机负载未隔离）");
            }
            Expect(ms <= limit * 2, $"每次刷新（两栏编译 + 排行 + 写标签）{ms:0.000} ms（阈值 {limit} ms，超 2 倍才失败；0.2 秒一次，折合每帧 < {ms / 24:0.0000} ms@120 帧）");
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

    internal static class FgEmgProgramTreeExt
    {
        internal static string Tree(this EmgProgramResult r) => FgEmgProgramSelfCheck.Tree(r);
    }
}
