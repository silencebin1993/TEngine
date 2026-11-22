using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.UI.Expedition;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace BinGames.EditorTools
{
    /// <summary>
    /// FG15-UX-03 本地化与 Demo 文案迁移（FGR-SYS-090 / FGR-ARC-006；验收 FGT-UX-002、FGT-SYS-009；FGT-EMG-072 题材审计）。
    /// A 文本表：中英两列齐全、英文列无中文、占位符一致；长度预算（每键按规则算，表外特例登记在 <see cref="LengthBudgetOverrides"/>）；禁用生物词。
    /// B UiText：@键替换、切换语言后重写、代码改写过的动态文本不被覆盖、缺键显示 ⟦key⟧。
    /// C 13 个 0.2 可达 UXML：本地化后没有残留 @键 / 缺键标记；英文界面没有中文残留；英文布局探针（FGT-SYS-009 不溢出）。
    /// D 通知细节“键 + 参数”：中文产生、切英文显示英文、存档往返仍随语言（DEBT-FG0UX01-11）。
    /// D2 主线目标 OBJ-01～10 的标题与清单随语言现取（静态表不冻结语言）。
    /// E 启动期双语表 LaunchText；F 设置页语言选项 E2E 与落盘（DEBT-FG0DATA01-01）；G 稀有度术语（FG-GAP-134）；
    /// H 突袭预警三态（FG-GAP-240）；I 建造菜单双语检索（DEBT-FG3LOG01-06）；J 字体覆盖与后备字体。
    /// </summary>
    public static class FgLocalizationSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private static readonly Regex Cjk = new Regex("[㐀-鿿豈-﫿　-〿！-｠]");
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)(:[^}]*)?\}");

        /// <summary>长度预算特例（键 → 英文最大半角宽度）。规则见 <see cref="LengthBudget"/>；新增特例必须写理由。</summary>
        internal static readonly Dictionary<string, int> LengthBudgetOverrides = new Dictionary<string, int>();

        /// <summary>0.2 正式流程可达、含静态文本的 UXML（细胞原型期 UXML 见 tools/loc_scan.py EXEMPT_FILES）。</summary>
        internal static readonly string[] PlayerUxml =
        {
            "BattleHud", "BeaconLaunchPanel", "CircuitBoardPanel", "CoreBossHud", "ExpeditionReturnPanel", "FactoryPanel", "GameShellUI",
            "PrimitiveCraftPanel", "RegionCommandBar", "SkillSlot", "TagChip", "VictoryPage", "WorkOrderPanel",
        };

        [MenuItem("QA/自检/本地化/FG15-UX-03 本地化与文案迁移")]
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
            Line("\n[本地化] 本地化与 Demo 文案迁移（FG15-UX-03）");
            GameLanguage originalLanguage = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；文本表 {GameText.Count} 条");
                Step(CheckTable);
                Step(CheckUiText);
                Step(CheckUxmlPanels);
                Step(CheckNotificationDetail);
                Step(CheckObjectiveCatalogLanguage);
                Step(CheckLaunchText);
                Step(CheckSettingsLanguage);
                Step(CheckRarityTerms);
                Step(CheckRaidWarning);
                Step(CheckBuildSearch);
                Step(CheckFontCoverage);
                Step(CheckPanelTooltips);
                Step(CheckOverlayProbes);
                Step(CheckAbandonConfirm);
            }
            finally
            {
                GameSettings.SetLanguage(originalLanguage);
                NotificationCenter.ResetForTests();
                LocalizedDetail.ResetForTests();
            }
            Line($"  [本地化] 断言 {_pass} 过 / {_fail} 败");
            return _fail;
        }

        // ── A. 文本表 ──────────────────────────────────────────────────────────

        /// <summary>半角宽度：中日韩全角字符算 2。</summary>
        internal static int Width(string s)
        {
            int w = 0;
            foreach (char c in s ?? string.Empty)
            {
                w += c >= 0x1100 && (c <= 0x115f || c >= 0x2e80 && c <= 0xa4cf || c >= 0xac00 && c <= 0xd7a3 || c >= 0xf900 && c <= 0xfaff
                                     || c >= 0xfe30 && c <= 0xfe4f || c >= 0xff00 && c <= 0xff60 || c >= 0xffe0 && c <= 0xffe6) ? 2 : 1;
            }
            return w;
        }

        /// <summary>
        /// 每个文本键的英文长度预算（半角宽度）。短文本（中文 ≤ 8 个全角字：按钮、标题、标签）= 中文宽度 × 2 + 12；
        /// 长文本（说明、提示，界面里换行显示）= 中文宽度 × 2.6 + 16。表外特例登记在 LengthBudgetOverrides；本段 A 是门禁（全量自检每次都核全部键）。
        /// </summary>
        internal static int LengthBudget(string key, string zh)
        {
            if (key != null && LengthBudgetOverrides.TryGetValue(key, out int o))
            {
                return o;
            }
            int zw = Width(zh);
            return zw <= 16 ? zw * 2 + 12 : (int)Math.Ceiling(zw * 2.6) + 16;
        }

        private static void CheckTable()
        {
            int keys = 0, missing = 0, cjkInEn = 0, phMismatch = 0, over = 0, bio = 0;
            var samples = new List<string>();
            var phS = new List<string>();
            var overS = new List<string>();
            var bioS = new List<string>();
            foreach (string k in GameText.AllKeys())
            {
                keys++;
                bool hz = GameText.TryGet(k, GameLanguage.ZhCn, out string zh);
                bool he = GameText.TryGet(k, GameLanguage.En, out string en);
                if (!hz || !he)
                {
                    missing++;
                    Sample(samples, $"缺列 {k}");
                    continue;
                }
                if (Cjk.IsMatch(en))
                {
                    cjkInEn++;
                    Sample(samples, $"英文列含中文 {k}：{en}");
                }
                string pz = string.Join(",", Placeholder.Matches(zh).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
                string pe = string.Join(",", Placeholder.Matches(en).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
                if (pz != pe)
                {
                    phMismatch++;
                    Sample(phS, $"占位符不一致 {k}：[{pz}] vs [{pe}]");
                }
                if (Width(en) > LengthBudget(k, zh))
                {
                    over++;
                    Sample(overS, 40, $"超长度预算 {k}：{Width(en)} > {LengthBudget(k, zh)}");
                }
                string hit = GameLogic.EditorTools.EmgTextResidueSelfCheck.AuditHit(zh, en);
                if (hit != null)
                {
                    bio++;
                    Sample(bioS, 40, $"禁用词 {k}：{hit}");
                }
            }
            Expect(keys > 11000, $"文本表已加载（{keys} 条）");
            Expect(missing == 0 && cjkInEn == 0, $"中英两列齐全、英文列没有中文（缺列 {missing}、英文含中文 {cjkInEn}）" + Tail(samples));
            Expect(phMismatch == 0, $"中英占位符一致（不一致 {phMismatch}）" + Tail(phS));
            Expect(over == 0, $"每个键的英文都在长度预算内（超出 {over}；规则：短文本 中文宽×2+12、长文本 中文宽×2.6+16）" + Tail(overS));
            Expect(bio == 0, $"FGT-EMG-072 / FGR-EMG-232：全部文本键没有禁用生物词（命中 {bio}）" + Tail(bioS));
            // 负向：规则本身会拦超长英文与生物词（防止判定退化成永远通过）。
            // 修复轮（审查 P2）：两条都构造确实超预算的英文（短文本 16 宽中文 → 预算 44，给 45；长文本 42 宽 → 预算 126，给 127），并核刚好不超的不被误判。
            string longZh = "这是一段二十个字的说明文字用来测长文本预算";
            Expect(Width(new string('x', 45)) > LengthBudget("neg.short", "八个字的中文标题") && Width(new string('x', 44)) <= LengthBudget("neg.short", "八个字的中文标题")
                   && Width(new string('x', 127)) > LengthBudget("neg.long", longZh) && Width(new string('x', 126)) <= LengthBudget("neg.long", longZh) && GameLogic.EditorTools.EmgTextResidueSelfCheck.AuditHit("细胞工坊", "Workshop") != null,
                "负向：超出预算的英文、含禁用词的文本会被判出来");
        }

        // ── B. UiText ──────────────────────────────────────────────────────────

        private static void CheckUiText()
        {
            const string kMove = "uxml.region_command_bar.move_button";
            const string kChassis = "uxml.circuit_board_panel.chassis_dropdown.label";
            var root = new VisualElement();
            var btn = new Button { text = "@" + kMove };
            var dyn = new Label { text = "@" + kMove };
            var drop = new DropdownField { label = "@" + kChassis };
            var bad = new Label { text = "@uxml.no_such_key_for_test" };
            var plain = new Label { text = "12/34" };
            root.Add(btn);
            root.Add(dyn);
            root.Add(drop);
            root.Add(bad);
            root.Add(plain);
            UiText.Localize(root);
            Expect(btn.text == GameText.Get(kMove, GameLanguage.ZhCn) && drop.label == GameText.Get(kChassis, GameLanguage.ZhCn) && plain.text == "12/34",
                $"Localize：@键换成当前语言（按钮“{btn.text}”、下拉标签“{drop.label}”），普通文本不动");
            Expect(GameText.ContainsMarker(bad.text), $"负向：缺键显示 ⟦key⟧（{bad.text}），不静默留 @键或空串");
            dyn.text = "代码写的动态文本";
            SettingsCatalog.Find("gameplay.language").SetIndex(1);
            Expect(btn.text == GameText.Get(kMove, GameLanguage.En) && drop.label == GameText.Get(kChassis, GameLanguage.En) && dyn.text == "代码写的动态文本",
                $"切到英文：已打开界面的静态文本立即重写（“{btn.text}” / “{drop.label}”），代码改写过的动态文本不被覆盖");
            SettingsCatalog.Find("gameplay.language").SetIndex(0);
            Expect(btn.text == GameText.Get(kMove, GameLanguage.ZhCn), $"切回中文后还原（“{btn.text}”）");
        }

        // ── C. 0.2 可达 UXML ───────────────────────────────────────────────────

        internal static string UxmlPath(string name)
        {
            foreach (string guid in AssetDatabase.FindAssets(name + " t:VisualTreeAsset", new[] { "Assets/GameRes/Raw/UI" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p) == name)
                {
                    return p;
                }
            }
            return null;
        }

        private static IEnumerable<(string Text, VisualElement El)> Texts(VisualElement root)
        {
            var list = new List<(string, VisualElement)>();
            root.Query<VisualElement>().ForEach(e =>
            {
                if (e is UnityEngine.UIElements.TextElement te) list.Add((te.text, e));
                if (e is Toggle tg) list.Add((tg.text, e));
                if (e is Foldout fo) list.Add((fo.text, e));
                if (e is BaseField<string> sf) list.Add((sf.label, e));
                if (!string.IsNullOrEmpty(e.tooltip)) list.Add((e.tooltip, e));
            });
            return list;
        }

        private static void CheckUxmlPanels()
        {
            int panels = 0, leftKeys = 0, markers = 0, cjkInEn = 0;
            var samples = new List<string>();
            var probeFails = new List<string>();
            foreach (string name in PlayerUxml)
            {
                string path = UxmlPath(name);
                var vta = path != null ? AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path) : null;
                if (vta == null)
                {
                    Sample(samples, $"找不到 {name}.uxml");
                    leftKeys++;
                    continue;
                }
                panels++;
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    TemplateContainer tree = vta.CloneTree();
                    UiText.Localize(tree);
                    foreach ((string t, VisualElement el) in Texts(tree))
                    {
                        if (UiText.IsKey(t)) { leftKeys++; Sample(samples, $"{name} 残留 @键 {t}"); }
                        if (GameText.ContainsMarker(t)) { markers++; Sample(samples, $"{name} 缺键 {t}"); }
                        if (lang == GameLanguage.En && t != null && Cjk.IsMatch(t)) { cjkInEn++; Sample(samples, $"{name} 英文界面残留中文 {el.name}：{t}"); }
                    }
                }
                // FGT-SYS-009：英文下的布局探针（四种分辨率；与既有各面板探针同一工具）。
                GameSettings.SetLanguage(GameLanguage.En);
                string res = UiToolkitLayoutProbe.Probe(path, null, false, UiToolkitLayoutProbe.DefaultPanelSettingsPath, r => UiText.Localize(r));
                if (!res.StartsWith("PASS", StringComparison.Ordinal))
                {
                    probeFails.Add(name + "：" + string.Join(" / ", res.Split('\n').Take(4)));
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(panels == PlayerUxml.Length && leftKeys == 0 && markers == 0, $"FGT-UX-002：{panels} 个 UXML 本地化后没有残留 @键（{leftKeys}）与缺键标记（{markers}）" + Tail(samples));
            Expect(cjkInEn == 0, $"英文界面里这些 UXML 没有中文残留（{cjkInEn}）" + Tail(samples));
            Expect(probeFails.Count == 0, $"FGT-SYS-009：英文布局探针不溢出（{PlayerUxml.Length - probeFails.Count}/{PlayerUxml.Length} 通过）"
                                           + (probeFails.Count > 0 ? "；" + string.Join("；", probeFails.Take(3)) : string.Empty));
        }

        // ── D. 通知细节 ────────────────────────────────────────────────────────

        private static void CheckNotificationDetail()
        {
            NotificationCenter.ResetForTests();
            LocalizedDetail.ResetForTests();
            CampaignState state = CampaignState.CreateNew("fgux03-l10n", "Standard", 15003);
            CampaignSession.Set(0, state);
            string saveDirBefore = CampaignSaveService.SaveDirectoryOverrideForTests;
            string tempSaveDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bingames-fgux03-" + Guid.NewGuid().ToString("N"));
            try
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = tempSaveDir;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string building = GameText.Get("building.generator.name");
                string zhDetail = GameText.Format("cargo.picked", "#3", building);
                NotificationEntry e1 = NotificationCenter.Post("power_lost", zhDetail, new Vector3(1f, 0f, 2f));
                NotificationEntry e2 = NotificationCenter.Post("research_done", "玩家起的名字 #7"); // loc-ignore: 测试用的玩家输入文字
                Expect(e1 != null && e2 != null && e1.Latest.DetailText == zhDetail && e2.Latest.DetailText == "玩家起的名字 #7",
                    $"中文下产生：细节显示与当时一致（“{e1?.Latest?.DetailText}”）");
                string json = JsonUtility.ToJson(state.Notifications);
                Expect(!json.Contains(zhDetail) && json.Contains("cargo.picked") && json.Contains("building.generator.name"),
                    "落盘存的是“文本键 + 参数”（参数里的建筑名也存键），不是中文成句");
                GameSettings.SetLanguage(GameLanguage.En);
                string enExpect = GameText.Format("cargo.picked", "#3", GameText.Get("building.generator.name", GameLanguage.En));
                Expect(e1.Latest.DetailText == enExpect && !Cjk.IsMatch(e1.Latest.DetailText), $"切英文：历史里的细节按英文重组（“{e1.Latest.DetailText}”），参数里的建筑名也是英文");
                SaveResult saved = CampaignSaveService.Save(0, state, SaveReason.Manual);
                LoadResult loaded = CampaignSaveService.Load(0);
                bool ok = saved.Success && loaded.Success;
                NotificationEntry back = null, back2 = null;
                if (ok)
                {
                    CampaignSession.Set(0, loaded.State);
                    NotificationCenter.Bind(loaded.State);
                    back = NotificationCenter.History.FirstOrDefault(x => x.Type?.Id == "power_lost");
                    back2 = NotificationCenter.History.FirstOrDefault(x => x.Type?.Id == "research_done");
                }
                Expect(ok && back != null && back.Latest.DetailText == enExpect && back2 != null && back2.Latest.DetailText == "玩家起的名字 #7",
                    $"真实存档服务存读档往返：读回的历史在英文下仍显示英文（“{back?.Latest?.DetailText}”）；认不出来的玩家文字原样保留（负向）{saved.Message}{loaded.Message}");

                // 修复轮（审查 P1）：离家报告拷的是存档编码（AwayReportService 记 member.DetailCode），显示时按当前语言成句，不漏控制字符 / 键名。
                var away = new AwayEntryRecord { TypeId = "power_lost", Section = "power", Detail = e1.Latest.DetailCode };
                string awayEn = AwayReportView.EntryLine(away).Text;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string awayZh = AwayReportView.EntryLine(away).Text;
                Expect(LocalizedDetail.IsEncoded(away.Detail) && awayEn.Contains(enExpect) && awayZh.Contains(zhDetail)
                       && !awayEn.Contains("cargo.picked") && !awayZh.Contains("cargo.picked") && awayEn.IndexOf('\u0001') < 0 && awayZh.IndexOf('\u0001') < 0,
                    $"离家报告行按当前语言成句：英文“{awayEn}”，中文“{awayZh}”");
                // 修复轮（审查 P2）：数值参数保留原类型，模式里的 {1:0} 格式说明符照常生效。
                const string numKey = "economy.ledger.not_enough_0_need";
                string numCode = LocalizedDetail.Encode(numKey, building, 12.345678, 3.4f);
                string numExpect = GameText.Format(numKey, building, 12.345678, 3.4f);
                Expect(GameText.Has(numKey) && LocalizedDetail.Render(numCode) == numExpect && !numExpect.Contains("12.34"),
                    $"数值参数保留格式说明符：“{LocalizedDetail.Render(numCode)}”");
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = saveDirBefore;
                try
                {
                    if (System.IO.Directory.Exists(tempSaveDir)) System.IO.Directory.Delete(tempSaveDir, true);
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响断言
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                NotificationCenter.ResetForTests();
                CampaignSession.Clear();
            }
        }

        // ── D2. 主线目标（OBJ-01～10）标题 / 清单随语言现取 ─────────────────────

        /// <summary>修复轮 2（审查 P1）：CampaignObjectiveCatalog.All 是静态初始化的表，标题与清单文字必须在读取时按当前语言解析，
        /// 不能定格在首次访问时的语言（任务日志、追踪栏、“新目标”通知都读 TitleOf / Items[i].Label）。</summary>
        private static void CheckObjectiveCatalogLanguage()
        {
            try
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                ObjectiveDef[] all = CampaignObjectiveCatalog.All; // 中文下先访问一次，模拟“中文开局”
                string zhTitle = CampaignObjectiveCatalog.TitleOf(CampaignObjectiveTracker.Obj01);
                string zhItem = all[0].Items[0].Label;
                string zhParam = CampaignObjectiveCatalog.Get(CampaignObjectiveTracker.Obj05).Items[1].Label;
                Expect(zhTitle == GameText.Get("objective.restore_power_in_reclamation", GameLanguage.ZhCn) && Cjk.IsMatch(zhTitle)
                       && zhItem == GameText.Get("objective.repair_the_generator", GameLanguage.ZhCn) && Cjk.IsMatch(zhParam),
                    $"中文：OBJ-01 标题“{zhTitle}”、首条清单“{zhItem}”、带物品名的清单“{zhParam}”");

                GameSettings.SetLanguage(GameLanguage.En);
                string enTitle = CampaignObjectiveCatalog.TitleOf(CampaignObjectiveTracker.Obj01);
                string enItem = all[0].Items[0].Label;
                var leftovers = new List<string>();
                foreach (ObjectiveDef def in CampaignObjectiveCatalog.All)
                {
                    if (string.IsNullOrEmpty(def.Title) || Cjk.IsMatch(def.Title) || GameText.ContainsMarker(def.Title)) leftovers.Add($"{def.Id} 标题“{def.Title}”");
                    foreach (ObjectiveItemDef item in def.Items)
                    {
                        string l = item.Label;
                        if (string.IsNullOrEmpty(l) || Cjk.IsMatch(l) || GameText.ContainsMarker(l)) leftovers.Add($"{def.Id} 清单“{l}”");
                    }
                }
                Expect(enTitle == GameText.Get("objective.restore_power_in_reclamation", GameLanguage.En)
                       && enItem == GameText.Get("objective.repair_the_generator", GameLanguage.En) && leftovers.Count == 0,
                    $"切英文：OBJ-01 标题“{enTitle}”、首条清单“{enItem}”；10 个目标的标题与清单（含带物品名的）没有中文残留 / 缺键（{leftovers.Count}）"
                    + (leftovers.Count > 0 ? "：" + string.Join("；", leftovers.Take(4)) : string.Empty));

                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(CampaignObjectiveCatalog.TitleOf(CampaignObjectiveTracker.Obj01) == zhTitle && all[0].Items[0].Label == zhItem
                       && CampaignObjectiveCatalog.Get(CampaignObjectiveTracker.Obj05).Items[1].Label == zhParam,
                    "切回中文：标题与清单恢复中文");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        // ── E. 启动期双语表 ─────────────────────────────────────────────────────

        private static void CheckLaunchText()
        {
            Type t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Procedure.LaunchText")).FirstOrDefault(x => x != null);
            if (t == null)
            {
                Expect(false, "找到 Procedure.LaunchText");
                return;
            }
            var table = (System.Collections.IDictionary)t.GetField("Table").GetValue(null);
            MethodInfo get = t.GetMethod("Get", new[] { typeof(string), typeof(bool) });
            int bad = 0;
            foreach (object k in table.Keys)
            {
                string zh = (string)get.Invoke(null, new object[] { k, false });
                string en = (string)get.Invoke(null, new object[] { k, true });
                if (string.IsNullOrEmpty(zh) || string.IsNullOrEmpty(en) || !Cjk.IsMatch(zh) || Cjk.IsMatch(en)) bad++;
            }
            Expect(table.Count >= 18 && bad == 0, $"启动期双语表 {table.Count} 条中英齐全（问题 {bad}）");
            string prefsKey = (string)t.GetField("SettingsPrefsKey").GetValue(null);
            string saved = PlayerPrefs.GetString(prefsKey, string.Empty);
            MethodInfo reset = t.GetMethod("ResetCache");
            PropertyInfo isEn = t.GetProperty("IsEnglish");
            try
            {
                GameSettings.SetLanguage(GameLanguage.En);
                reset.Invoke(null, null);
                bool en = (bool)isEn.GetValue(null);
                PlayerPrefs.SetString(prefsKey, "{broken");
                reset.Invoke(null, null);
                bool brokenFallsBack = !(bool)isEn.GetValue(null);
                Expect(en && brokenFallsBack, "启动期按设置里的语言显示（英文设置 → 英文）；设置损坏时回落简体中文（负向）");
            }
            finally
            {
                PlayerPrefs.SetString(prefsKey, saved);
                reset.Invoke(null, null);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        // ── F. 设置页语言 ──────────────────────────────────────────────────────

        private static void CheckSettingsLanguage()
        {
            SettingItem item = SettingsCatalog.Find("gameplay.language");
            Expect(item != null && item.Choices().Count == 2 && item.Choices()[1] == "English", "设置页有语言选项（简体中文 / English）");
            if (item == null) return;
            item.SetIndex(1);
            bool en = GameSettings.Language == GameLanguage.En && GameText.Get("uxml.region_command_bar.move_button") == "Move" && item.GetIndex() == 1;
            GameSettings.Load(); // 重新读盘
            bool persisted = GameSettings.Language == GameLanguage.En;
            item.SetIndex(0);
            GameSettings.Load();
            Expect(en && persisted && GameSettings.Language == GameLanguage.ZhCn && GameText.Get("uxml.region_command_bar.move_button") == "移动",
                "DEBT-FG0DATA01-01：设置页选 English → 文本立即变英文、落盘后重新读盘仍是英文；选回简体中文同理");
        }

        // ── G. 稀有度术语 ──────────────────────────────────────────────────────

        private static void CheckRarityTerms()
        {
            bool same = true;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                same &= GameText.Get("firmware.rarity.common", lang) == GameText.Get("loot.rarity.common", lang)
                        && GameText.Get("firmware.rarity.rare", lang) == GameText.Get("loot.rarity.fine", lang)
                        && GameText.Get("firmware.rarity.epic", lang) == GameText.Get("loot.rarity.rare", lang);
            }
            MethodInfo rank = typeof(GameLogic.Campaign.Signal.FirmwareLibrary).GetMethod("RarityRank", BindingFlags.NonPublic | BindingFlags.Static);
            int R(string r) => (int)rank.Invoke(null, new object[] { r });
            // 库的稀有度筛选下拉按 RarityOptions 顺序列出，显示出来也必须是由低到高。
            var options = (string[])typeof(FirmwareLibraryPanelUIToolkit).GetField("RarityOptions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string shown = string.Join(" < ", options.Select(o => GameText.Get("firmware.rarity." + o, GameLanguage.ZhCn)));
            bool ascending = options.Zip(options.Skip(1), (a, b) => R(a) < R(b)).All(x => x);
            int lootFine = Array.IndexOf(LootPickCatalog.Tiers, "fine"), lootRare = Array.IndexOf(LootPickCatalog.Tiers, "rare");
            Expect(same && R("common") < R("rare") && R("rare") < R("epic") && ascending && lootFine >= 0 && lootFine < lootRare,
                $"FG-GAP-134：固件库与三选一同一套“普通 < 精良 < 稀有”（固件 rare = 精良、epic = 稀有，中英与三选一同名；库排序档位 common {R("common")} / rare {R("rare")} / epic {R("epic")}；筛选下拉“{shown}”）");
        }

        // ── H. 突袭预警三态 ─────────────────────────────────────────────────────

        private static void CheckRaidWarning()
        {
            var s = new CampaignState();
            string none = RaidDirectorService.CompUnknownText(s);
            var post = new BuildingRecord
            {
                BuildingId = "loc_selfcheck_post", BuildingTypeId = IntelService.TypeId, RegionId = HomeValleyLayout.RegionId,
                ConstructionState = BuildingConstructionState.Operational, PowerState = BuildingPowerState.Unpowered,
            };
            s.BuildingRecords = new[] { post };
            string idle = RaidDirectorService.CompUnknownText(s);
            post.PowerState = BuildingPowerState.Powered;
            s.BuildingRecords = new[] { post };
            string working = RaidDirectorService.CompUnknownText(s);
            Expect(none == GameText.Get("raid.warning.comp_unknown") && idle == GameText.Get("raid.warning.comp_post_idle")
                   && working == GameText.Get("raid.warning.comp_decoding") && none != idle && idle != working,
                $"FG-GAP-240：编成未知的预警行按监听站分三句——没有：“{none}”；有但没在工作：“{idle}”；在工作：“{working}”");
            // 修复轮（审查 P2）：经真实拼行入口——预警条一行（WaveRowText）与它的悬停说明（WaveTip）里确实带上这句。
            var wave = new RaidWaveView { Wave = 1, Forces = 1, Units = 3, Lead = new RaidPlanRecord(), IntelKnown = false, DepartTick = 300, ArrivalTick = 600 };
            var rows = new List<string>();
            bool rowOk = true;
            foreach ((BuildingPowerState? power, string expect) in new (BuildingPowerState?, string)[] { (null, none), (BuildingPowerState.Unpowered, idle), (BuildingPowerState.Powered, working) })
            {
                if (power.HasValue) post.PowerState = power.Value;
                s.BuildingRecords = power.HasValue ? new[] { post } : Array.Empty<BuildingRecord>();
                string row = RaidDirectorService.WaveRowText(s, wave, 0);
                string tip = RaidDirectorService.WaveTip(s, wave, 0);
                rowOk &= row.Contains(expect) && tip.Contains(expect);
                rows.Add(row.Replace('\n', ' '));
            }
            Expect(rowOk, $"FG-GAP-240：三种监听站状态下预警行与悬停说明都带对应的那句（{string.Join(" ｜ ", rows)}）");
        }

        // ── I. 建造菜单双语检索 ─────────────────────────────────────────────────

        private static void CheckBuildSearch()
        {
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            var list = new List<BuildEntry>();
            BuildCatalog.List(BuildCatalog.FirstNonEmptyCategory(), null, list);
            BuildEntry e = list.FirstOrDefault(x => !x.IsTool);
            if (e == null)
            {
                Expect(false, "建造菜单有建筑条目");
                return;
            }
            string zhName = e.Name;
            GameSettings.SetLanguage(GameLanguage.En);
            string enName = e.Name;
            bool enUiFindsZh = BuildCatalog.Matches(e, zhName);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool zhUiFindsEn = BuildCatalog.Matches(e, enName.ToUpperInvariant());
            bool junk = BuildCatalog.Matches(e, "zzqx-不存在的名字");
            Expect(zhName != enName && enUiFindsZh && zhUiFindsEn && !junk,
                $"DEBT-FG3LOG01-06：中文界面搜英文名（“{enName.ToUpperInvariant()}”，不分大小写）、英文界面搜中文名（“{zhName}”）都能找到；乱写的关键字找不到（负向）");
        }

        // ── J. 字体 ────────────────────────────────────────────────────────────

        private static void CheckFontCoverage()
        {
            const string path = "Assets/GameRes/Raw/UI/Fonts/BattleUITextSettings.asset";
            var settings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(path);
            if (settings == null || settings.defaultFontAsset == null)
            {
                Expect(false, $"界面文本设置 {path} 有默认字体");
                return;
            }
            var chain = new List<FontAsset> { settings.defaultFontAsset };
            chain.AddRange(settings.fallbackFontAssets ?? new List<FontAsset>());
            chain.AddRange(settings.defaultFontAsset.fallbackFontAssetTable ?? new List<FontAsset>());
            chain = chain.Where(f => f != null).Distinct().ToList();
            var chars = new HashSet<char>();
            foreach (string k in GameText.AllKeys())
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    if (GameText.TryGet(k, lang, out string t))
                    {
                        foreach (char c in t)
                        {
                            if (!char.IsControl(c) && !char.IsWhiteSpace(c) && !char.IsSurrogate(c)) chars.Add(c);
                        }
                    }
                }
            }
            var missing = new List<char>();
            foreach (char c in chars)
            {
                bool ok = chain.Any(f => f.HasCharacter(c, false, false) || (f.sourceFontFile != null && f.sourceFontFile.HasCharacter(c)));
                if (!ok) missing.Add(c);
            }
            Expect(settings.defaultFontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic && settings.defaultFontAsset.sourceFontFile != null,
                $"主字体 {settings.defaultFontAsset.name} 是动态字体（运行时按源字体补字形，不限于预烘焙图集）");
            // 后备字体：运行时与 UiScaleApplier 同一入口给共享文本设置挂系统字体；这里对资产的副本做，不改资产本体。
            var copy = UnityEngine.Object.Instantiate(settings);
            try
            {
                int before = copy.fallbackFontAssets?.Count ?? 0;
                int added = UiFontFallback.Ensure(copy);
                int again = UiFontFallback.Ensure(copy);
                var full = new List<FontAsset> { copy.defaultFontAsset };
                full.AddRange(copy.fallbackFontAssets ?? new List<FontAsset>());
                string probe = "한글あ"; // 玩家输入里可能出现、文本表里没有的韩文 / 假名
                string uncovered = new string(probe.Where(c => !full.Any(f => f != null && (f.HasCharacter(c, false, true) || (f.sourceFontFile != null && f.sourceFontFile.HasCharacter(c))))).ToArray());
                Expect(added >= 1 && again == 0 && full.Count >= 2 && (settings.fallbackFontAssets?.Count ?? 0) == before,
                    $"有后备字体：运行时挂上 {added} 个系统字体（{string.Join("、", copy.fallbackFontAssets.Where(f => f != null).Select(f => f.name))}），重复调用不重复挂，资产本体不改");
                Expect(uncovered.Length == 0, $"字体链覆盖文本表外的中日韩字符（韩文 / 假名探测“{probe}”，没覆盖：“{uncovered}”）");
            }
            finally
            {
                UiFontFallback.Restore();
                UnityEngine.Object.DestroyImmediate(copy);
            }
            Expect(missing.Count == 0, $"字体覆盖：文本表用到的 {chars.Count} 个字符全部有字形（缺 {missing.Count}：{new string(missing.Take(20).ToArray())}）");
        }

        // ── K. 面板悬停提示（DEBT-FG0UX01-05 / FG-GAP-003）────────────────────────

        /// <summary>代码里挂了更具体提示（带实时数值 / 固件说明）的元素：UXML 不写 tooltip，由面板代码 UiTooltip.Attach。</summary>
        internal static readonly HashSet<string> CodeTooltips = new HashSet<string>
        {
            "CircuitBoardPanel/PrimaryDropdown", "CircuitBoardPanel/Firmware0Dropdown", "CircuitBoardPanel/Firmware1Dropdown", "CircuitBoardPanel/RangeSendButton",
            "MissionLog/QuestBtnTrack", "ObjectiveHud/QuestTrackFly0", "ObjectiveHud/QuestTrackFly1", "ObjectiveHud/QuestTrackFly2",
        };

        /// <summary>
        /// 不扫的 UXML（相对 GameRes/Raw/UI，前缀匹配）与理由。其余 GameRes/Raw/UI 下的 UXML 全部枚举（不是手写清单）。
        /// 细胞原型期条目与 tools/loc_scan.py EXEMPT_FILES 同一批（可达性核对见 FG-GAP-268）。
        /// </summary>
        internal static readonly Dictionary<string, string> TooltipExempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "UiKit/", "UI 基础件面板（FG 新面板）：DEBT-FG0UX01-05 原文把它列在已有提示一侧（“新面板必须使用”），提示由面板代码按实时数值挂，覆盖由各卡自检（FgUiKitSelfCheck H / M 段等）核" },
            { "UiDemo/", "UI 基础件样例页（开发者检查用，玩家入口不可达）" },
            { "GameShell/GameShellUI.uxml", "细胞原型期运行中枢：首页永久隐藏，运行导航与设置浮层只在细胞原型阶段出现，0.2 不可达（FG-GAP-268）" },
            { "LineageWorkshop/", "细胞原型期遗留（模板版本与回巢改造，FG-GAP-268）" },
            { "BattleUI/BattleOverlays.uxml", "细胞原型期遗留（卡组 / 商店 / 图鉴，FG-GAP-268）" },
            { "BattleUI/BattleSandboxUI.uxml", "细胞原型期遗留（LookDev 沙盒，FG-GAP-268）" },
            { "BattleUI/BattleCarrierUI.uxml", "细胞原型期遗留（器官栏，FG-GAP-268）" },
            { "BattleUI/BattleGerminationUI.uxml", "细胞原型期遗留（萌生腔，FG-GAP-268）" },
            { "BattleUI/BattleResultUI.uxml", "细胞原型期遗留（局结算，FG-GAP-268）" },
            { "BattleUI/BattleDraftUI.uxml", "细胞原型期遗留（进化选卡，FG-GAP-268）" },
            { "BattleUI/templates/DraftCard.uxml", "细胞原型期遗留（进化选卡卡面，FG-GAP-268）" },
            { "BattleUI/templates/ShopSlot.uxml", "细胞原型期遗留（局内商店格，FG-GAP-268）" },
            { "BattleUI/templates/SlotCell.uxml", "细胞原型期遗留（旧装配格，运行时代码无引用）" },
            { "BattleUI/templates/BagItem.uxml", "细胞原型期遗留（旧背包格，运行时代码无引用）" },
        };

        /// <summary>GameRes/Raw/UI 下全部 UXML（相对路径），减去 <see cref="TooltipExempt"/>。</summary>
        internal static List<string> TooltipScanFiles(out int exempt)
        {
            const string root = "Assets/GameRes/Raw/UI/";
            var files = new List<string>();
            exempt = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { "Assets/GameRes/Raw/UI" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) || !p.StartsWith(root, StringComparison.Ordinal))
                {
                    continue;
                }
                string rel = p.Substring(root.Length);
                if (TooltipExempt.Keys.Any(k => rel.StartsWith(k, StringComparison.Ordinal)))
                {
                    exempt++;
                    continue;
                }
                files.Add(p);
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>玩家可操作的控件（ScrollView 自带滚动条里的内部滑块 / 按钮不算）。</summary>
        private static bool Interactive(VisualElement e) =>
            (e is Button || e is Toggle || e is DropdownField || e is TextField || e is Slider || e is SliderInt) && e.GetFirstAncestorOfType<Scroller>() == null;

        private static void CheckPanelTooltips()
        {
            int total = 0, covered = 0, panels = 0;
            var missing = new List<string>();
            UiTooltip.ResetForTests();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            var trees = new List<TemplateContainer>();
            List<string> scanFiles = TooltipScanFiles(out int exemptCount);
            foreach (string path in scanFiles)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                TemplateContainer tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path).CloneTree();
                trees.Add(tree);
                UiText.Localize(tree);
                int before = total;
                tree.Query<VisualElement>().Where(Interactive).ForEach(e =>
                {
                    total++;
                    if (UiTooltip.IsAttached(e) || CodeTooltips.Contains(name + "/" + e.name)) covered++;
                    else Sample(missing, 20, $"{name}/{e.name}");
                });
                if (total > before) panels++;
            }
            // 负向：没有提示的控件会被判出（判定没有退化成永远通过）。
            var bare = new Button { name = "__probe_no_tip" };
            Expect(Interactive(bare) && !UiTooltip.IsAttached(bare), "负向：没写提示的按钮会被扫描判为缺提示");
            Expect(total >= 120 && covered == total,
                $"GameRes/Raw/UI 下全部非豁免 UXML（{panels} 个含交互元素，豁免 {exemptCount} 个，豁免表写理由）的每个交互元素都有悬停提示（{covered}/{total}；其中 {CodeTooltips.Count} 个由面板代码挂带实时数值 / 原因的提示）" + Tail(missing));

            // 行为：命令栏放进真实面板（布局探针的面板），悬停“移动”0.4 秒后真实提示组件显示 UXML 写的提示；切英文后同一按钮显示英文。
            VisualElement overlay = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath("UiKitOverlay")).CloneTree();
            UiTooltip.BindView(overlay);
            float t = 10f;
            UiTooltip.Clock = () => t;
            UiTooltip.PinHeld = () => false;
            string zh = null, zhTitle = null, en = null;
            try
            {
                UiToolkitLayoutProbe.Probe(UxmlPath("RegionCommandBar"), null, false, UiToolkitLayoutProbe.DefaultPanelSettingsPath, r => UiText.Localize(r), 1f,
                    new[] { new Vector2Int(1920, 1080) }, (r, res) =>
                    {
                        Button move = r.Q<Button>("MoveButton");
                        UiTooltip.NotifyEnter(move);
                        t += 1f;
                        UiTooltip.Tick();
                        zh = UiTooltip.Content?.Body;
                        zhTitle = UiTooltip.Content?.Title;
                        UiTooltip.Hide();
                        SettingsCatalog.Find("gameplay.language").SetIndex(1);
                        UiTooltip.NotifyEnter(move);
                        t += 1f;
                        UiTooltip.Tick();
                        en = UiTooltip.Content?.Body;
                        UiTooltip.Hide();
                    });
            }
            finally
            {
                SettingsCatalog.Find("gameplay.language").SetIndex(0);
                UiTooltip.ResetForTests();
            }
            Expect(zh == GameText.Get("uxml.region_command_bar.move_button.tip", GameLanguage.ZhCn) && zhTitle == GameText.Get("uxml.region_command_bar.move_button", GameLanguage.ZhCn)
                   && en == GameText.Get("uxml.region_command_bar.move_button.tip", GameLanguage.En) && en != null && !Cjk.IsMatch(en),
                $"悬停“移动”按钮 0.4 秒后出现提示（“{zhTitle}：{zh}”）；切英文后同一按钮显示英文（“{en}”）");
        }

        // ── L. 浮层布局探针（DEBT-FG0UX01-13）─────────────────────────────────────

        private static void CheckOverlayProbes()
        {
            string path = UxmlPath("UiKitOverlay");
            VisualElement target = null;
            float t = 100f;
            void ShowTip()
            {
                UiTooltip.Hide();
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                UiTooltip.ToggleExpanded(); // 展开数值来源
            }
            Action<VisualElement> prepTip = root =>
            {
                VisualElement top = root.panel.visualTree;
                UiTooltip.BindView(top);
                UiTooltip.Clock = () => t;
                UiTooltip.PinHeld = () => false;
                target = new VisualElement { name = "__probe_tip_target" };
                top.Add(target);
                UiTooltip.Attach(target, () =>
                {
                    var c = new TooltipContent
                    {
                        Title = GameText.Get("uxml.circuit_board_panel.save_button.tip"),
                        Body = GameText.Get("uxml.circuit_board_panel.uplink_toggle_button.tip") + "\n" + GameText.Get("uxml.region_command_bar.group.tip"),
                        Total = GameText.Get("uxml.work_order_panel.priority_btn.tip"),
                    };
                    for (int i = 0; i < 6; i++)
                    {
                        c.Sources.Add(new TooltipSource(GameText.Get("uxml.expedition_return_panel.confirm_button.tip"), "+12.5/min"));
                    }
                    return c;
                });
                ShowTip();
            };
            Action<VisualElement, Vector2Int> placeTip = (root, res) =>
            {
                // 布局后目标才有真实位置：放到右下角附近再显示一次（提示按目标与屏幕边缘翻转）。目标位置是测试数据，直接写 style。
                target.style.position = Position.Absolute;
                target.style.left = root.panel.visualTree.worldBound.width * 0.8f;
                target.style.top = root.panel.visualTree.worldBound.height * 0.85f;
                target.style.width = 40;
                target.style.height = 20;
                UiToolkitLayoutProbe.ForceLayout(root.panel.visualTree);
                ShowTip();
                UiToolkitLayoutProbe.ForceLayout(root.panel.visualTree);
            };
            Action<VisualElement> prepMenu = root =>
            {
                UiContextMenu.BindView(root.panel.visualTree);
                UiContextMenu.Show(new Vector2(1800f, 1000f), MenuItems());
            };
            Action<VisualElement, Vector2Int> placeMenu = (root, res) =>
            {
                Rect panel = root.panel.visualTree.worldBound;
                UiContextMenu.Close();
                UiContextMenu.Show(new Vector2(panel.width - 10f, panel.height - 10f), MenuItems()); // 在右下角右键
                UiToolkitLayoutProbe.ForceLayout(root.panel.visualTree);
                UiContextMenu.Reclamp(); // 运行时由 GeometryChangedEvent 调；探针里布局是同步强制的，事件不派发，直接调同一入口
                UiToolkitLayoutProbe.ForceLayout(root.panel.visualTree);
            };
            var fails = new List<string>();
            int runs = 0;
            var cases = new (string Node, Action<VisualElement> Prep, Action<VisualElement, Vector2Int> After)[] { ("Tooltip", prepTip, placeTip), ("ContextMenu", prepMenu, placeMenu) };
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 1f, 1.5f })
                    {
                        foreach (var c in cases)
                        {
                            string r = UiToolkitLayoutProbe.Probe(path, c.Node, false, UiToolkitLayoutProbe.DefaultPanelSettingsPath, c.Prep, scale, null, c.After);
                            runs++;
                            if (!r.StartsWith("PASS", StringComparison.Ordinal)) fails.Add($"{c.Node} [{lang}] {scale:0.#}：" + string.Join(" / ", r.Split('\n').Take(4)));
                            UiTooltip.ResetForTests();
                            UiContextMenu.ResetForTests();
                        }
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                UiTooltip.ResetForTests();
                UiContextMenu.ResetForTests();
                UiEscapeStack.ResetForTests();
            }
            Expect(runs == 8 && fails.Count == 0, $"浮层探针：悬停提示框（多行英文来源、展开）与右键菜单在中 / 英 × 100% / 150% × 四种分辨率下不越界、不截断（{runs - fails.Count}/{runs}）"
                                                  + (fails.Count > 0 ? "；" + string.Join("；", fails.Take(3)) : string.Empty));
        }

        private static List<GameLogic.UI.Kit.ContextMenuItem> MenuItems() => new List<GameLogic.UI.Kit.ContextMenuItem>
        {
            new GameLogic.UI.Kit.ContextMenuItem(GameText.Get("uxml.circuit_board_panel.uplink_toggle_button.tip"), () => { }),
            new GameLogic.UI.Kit.ContextMenuItem(GameText.Get("uxml.circuit_board_panel.save_as_button.tip"), () => { }),
            GameLogic.UI.Kit.ContextMenuItem.Disabled(GameText.Get("uxml.factory_panel.retrofit_confirm_button.tip"), GameText.Get("uxml.factory_panel.retrofit_target_dropdown.tip")),
        };

        // ── M. 放弃远征走通用确认框（DEBT-FG0UX01-04）──────────────────────────────

        private static void CheckAbandonConfirm()
        {
            UiConfirmDialog.ResetForTests();
            var go = new GameObject("__fgux03_abandon") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var panel = go.AddComponent<ExpeditionReturnPanelUIToolkit>();
                MethodInfo click = typeof(ExpeditionReturnPanelUIToolkit).GetMethod("OnAbandonClicked", BindingFlags.NonPublic | BindingFlags.Instance);
                click.Invoke(panel, null);
                ConfirmRequest r = UiConfirmDialog.Current;
                bool asks = r != null && r.Irreversible && r.Title == GameText.Get("exp.return.abandon_confirm.title") && r.Consequences.Count == 1
                            && r.Consequences[0] == GameText.Get("exp.return.abandon_confirm.body") && r.OnConfirm?.Method.Name == "CommitAbandon";
                UiConfirmDialog.Cancel();
                Expect(asks && !UiConfirmDialog.IsOpen,
                    $"放弃远征：点按钮先弹通用确认框（不可撤销样式、写明后果“{r?.Consequences.FirstOrDefault()}”），确认才提交；取消后确认框关闭、不提交（负向）");
            }
            finally
            {
                UiConfirmDialog.ResetForTests();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── 工具 ───────────────────────────────────────────────────────────────

        private static void Sample(List<string> s, string x) => Sample(s, 6, x);

        private static void Sample(List<string> s, int cap, string x)
        {
            if (s.Count < cap) s.Add(x);
        }

        private static string Tail(List<string> s) => s.Count == 0 ? string.Empty : "；例：" + string.Join(" | ", s);

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Expect(false, $"{a.Method.Name} 抛异常：{ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void Expect(bool ok, string what)
        {
            if (ok) _pass++;
            else _fail++;
            Line($"  {(ok ? "✓" : "✗")} {what}");
        }

        private static void Line(string s) => _report.AppendLine(s);
    }
}
