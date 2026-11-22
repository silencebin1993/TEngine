using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using BinGames.EditorTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-UX-04 引导与教学全集（FG13 FGR-UX-040～042，FG00 B14，验收 FGT-UX-005）自检。起真实系统跑：真实 <see cref="GuidanceHooks.Raise"/>、
    /// 真实本机设置（已看记录 / 总开关，跑完逐条还原）、真实文本表、真实 UXML（引导卡片、引导与帮助、各面板标题栏）。
    /// A 钩子 ↔ 引导内容对照表：Known 里每个钩子都有中英卡片或 none 理由；源码里 Raise 的每个字面量 / Hook* 常量都已登记；FGR-UX-040 清单 21 条、FG19 B01 的 7 个、登记册承接本卡的钩子都有内容；
    /// B 正常路径：首次触发 → 卡片、多步演习、在图鉴中查看、只出现一次；C 负向：同一时刻多条排队不叠加、跳过、关闭引导后重新打开、重看不改已看记录、引导不阻塞（不压模态 / 不进 Esc 栈 / 不停时间）、none 钩子不出卡；
    /// D 暂停与 0.5x～3x（Time.timeScale 与 GameClock 两套）下队列与展示不变；E 存读档（本机设置落盘后重新读入）；F 引导与帮助面板（重看、开关、重新显示全部）；
    /// G 每个面板的“?”（UiKitPanelHost 全集：在映射表或豁免表二者居一，映射的图鉴条目存在，标题栏恰好一个“?”，点它打开对应条目、层级高于图鉴的面板让图鉴盖上来）。
    /// </summary>
    public static class FgGuidanceSelfCheck
    {
        private const string CardUxml = "Assets/GameRes/Raw/UI/UiKit/GuidanceCard.uxml";
        private const string HelpUxml = "Assets/GameRes/Raw/UI/UiKit/GuidanceHelpPanel.uxml";
        private const string UiRoot = "Assets/GameRes/Raw/UI";
        private const string LogicRoot = "Assets/GameScripts/HotFix/GameLogic";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        /// <summary>FG13 FGR-UX-040 引导清单（触发 → 钩子）。</summary>
        private static readonly (string Trigger, string Hook)[] SpecList =
        {
            ("第一次打开建造菜单", GuidanceHooks.BuildModeFirstOpen),
            ("第一次放传送带", GuidanceHooks.LogisticsFirstBelt),
            ("第一次出现堵塞", GuidanceHooks.LogisticsFirstBlocked),
            ("第一次出现副产品堵塞", GuidanceHooks.EconomyFirstByproductBlocked),
            ("第一次遇到缺电", GuidanceHooks.PowerFirstBrownout),
            ("第一次编辑电路遇到接入口", GuidanceHooks.CircuitUplinkFirstSeen),
            ("第一次接入带接入口的机器", GuidanceHooks.SignalFirstUplink),
            ("第一次拿到加密固件", GuidanceHooks.SignalFirstEncryptedFirmware),
            ("第一次拿到熔合线索", GuidanceHooks.FusionFirstClue),
            ("第一次建炮塔", GuidanceHooks.TurretFirstBuilt),
            ("第一次突袭预警（演习）", GuidanceHooks.RaidFirstWarning),
            ("静默夜前", GuidanceHooks.EnvFirstSilentWarning),
            ("第一次天气", GuidanceHooks.EnvFirstWeather),
            ("震动值第一次到 50", GuidanceHooks.VibrationFirstOmen),
            ("第一次数据包", GuidanceHooks.LootFirstPacket),
            ("第一次远征准备", GuidanceHooks.ExpeditionChecklistFirstOpen),
            ("第一次远距离跳转", GuidanceHooks.SignalFirstFarJump),
            ("第一次走出信号覆盖", GuidanceHooks.SignalCoverageFirstLeft),
            ("第一次解锁投送", GuidanceHooks.DropStationFirstBuilt),
            ("第一次建前哨站", GuidanceHooks.OutpostFirstFounded),
            ("第一次规则", GuidanceHooks.RulesPanelFirstOpen),
        };

        /// <summary>FG19 第 13 节 B01 的 7 个引导钩子。</summary>
        private static readonly string[] EmgB01 =
        {
            GuidanceHooks.EmgFirstProgramTree, GuidanceHooks.EmgFirstTrigger, GuidanceHooks.EmgFirstDiscovery, GuidanceHooks.EmgFirstMatter,
            GuidanceHooks.EmgFirstPhase, GuidanceHooks.EmgFirstRelay, GuidanceHooks.EmgFirstHijack,
        };

        [MenuItem("QA/自检/FG 引导与教学")]
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
            Line("\n[引导与教学] 钩子 ↔ 引导内容对照表、首次一次、排队不叠加、跳过 / 关闭 / 重新打开 / 重看、暂停与倍速、存读档、引导与帮助、每个面板的“?”（FG15-UX-04）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写本机设置里的已看记录）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            string[] originalSeen = GameSettings.SeenGuidanceHooks.ToArray();
            bool originalEnabled = GameSettings.GuidanceEnabled;
            float originalTimeScale = Time.timeScale;
            float originalSpeed = GameClock.Speed;
            bool originalPaused = GameClock.Paused;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            string codexPath = Path.Combine(Path.GetTempPath(), "fg_guidance_selfcheck_codex.json");
            try
            {
                // 图鉴解锁文件改到临时目录：触发钩子 / 点“?”会解锁图鉴条目，不能写进本机真实图鉴。
                if (File.Exists(codexPath))
                {
                    File.Delete(codexPath);
                }
                MechanicCodex.FilePathOverrideForTests = codexPath;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameText.Reload();
                MechanicCodex.Reload();
                CheckTable();
                CheckQueue();
                CheckNegative();
                CheckTiming();
                CheckSaveLoad();
                CheckHelpPanel();
                CheckPanelHelp();
                CheckLayout();
            }
            catch (Exception ex)
            {
                Fail("异常：" + ex);
            }
            finally
            {
                // 逐条还原本机设置（已看记录按原顺序、总开关），不留副作用。
                GuidanceQueue.ResetForTests();
                GameSettings.ResetSeenGuidanceHooks();
                foreach (string h in originalSeen)
                {
                    GameSettings.MarkGuidanceHookSeen(h);
                }
                GameSettings.SetGuidanceEnabled(originalEnabled);
                GameSettings.SetLanguage(originalLanguage);
                Time.timeScale = originalTimeScale;
                GameClock.SetSpeed(originalSpeed);
                GameClock.SetPaused(originalPaused);
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
            }
            Line($"  引导与教学：通过 {_pass} / 失败 {_fail}");
            return _fail;
        }

        // ── A 对照表 ───────────────────────────────────────────────────────

        private static void CheckTable()
        {
            var known = new HashSet<string>(GuidanceHooks.Known);
            Expect(known.Count == GuidanceHooks.Known.Count, $"A1 GuidanceHooks.Known 无重复（{GuidanceHooks.Known.Count} 个）");

            var missing = new List<string>();
            var badLang = new List<string>();
            int cards = 0, none = 0, steps = 0;
            foreach (string h in GuidanceHooks.Known)
            {
                if (GuidanceQueue.HasContent(h))
                {
                    cards++;
                    int n = GuidanceQueue.StepCount(h);
                    steps += n - 1;
                    var keys = new List<string> { GuidanceQueue.TitleKey(h) };
                    for (int i = 1; i <= n; i++)
                    {
                        keys.Add(GuidanceQueue.StepKey(h, i));
                    }
                    if (GameText.Has(GuidanceQueue.NoneKey(h)))
                    {
                        badLang.Add(h + "（既有卡片又有 none）");
                    }
                    foreach (string k in keys)
                    {
                        if (!BothLanguages(k, requireAsciiEn: true))
                        {
                            badLang.Add(k);
                        }
                    }
                }
                else if (GuidanceQueue.IsExempt(h))
                {
                    none++;
                    if (!BothLanguages(GuidanceQueue.NoneKey(h), requireAsciiEn: false))
                    {
                        badLang.Add(GuidanceQueue.NoneKey(h));
                    }
                }
                else
                {
                    missing.Add(h);
                }
            }
            Expect(missing.Count == 0, $"A2 对照表：Known 的 {known.Count} 个钩子 = 卡片 {cards} + 无需单独引导 {none}（理由写在 guide.<钩子>.none）；没有内容的：{string.Join(",", missing)}");
            Expect(badLang.Count == 0, $"A3 卡片标题 / 正文 / 演习步骤 / none 理由中英都不空、英文不含中文（FG15-UX-03 文本键）；有问题的：{string.Join(",", badLang.Take(12))}");
            Expect(steps >= 3, $"A3b 演习式多步引导：突袭预警 3 步、静默夜前 2 步（合计追加 {steps} 步）");

            // 文本表里每个 guide.<钩子>.* 都对应 Known 里的钩子（没有孤立的引导内容）。
            var orphan = new List<string>();
            foreach (string k in GameText.AllKeys())
            {
                if (!k.StartsWith("guide.", StringComparison.Ordinal) || k.StartsWith("guide.card.", StringComparison.Ordinal) || k.StartsWith("guide.help.", StringComparison.Ordinal))
                {
                    continue;
                }
                int dot = k.LastIndexOf('.');
                string hook = k.Substring(6, dot - 6);
                if (!known.Contains(hook))
                {
                    orphan.Add(k);
                }
            }
            Expect(orphan.Count == 0, $"A4 文本表里没有不在 Known 的引导内容；孤立的：{string.Join(",", orphan.Take(10))}");

            // 源码扫描：所有 Raise("字面量") 与 const string Hook* = "..." 都已登记（“确认没有遗漏的钩子”）。
            var raisedMissing = new SortedSet<string>();
            int scanned = 0;
            var rxRaise = new Regex("GuidanceHooks\\.Raise\\(\\s*\"([^\"]+)\"");
            var rxConst = new Regex("const string Hook\\w*\\s*=\\s*\"([^\"]+)\"");
            foreach (string file in Directory.GetFiles(LogicRoot, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(file);
                foreach (Match m in rxRaise.Matches(src).Cast<Match>().Concat(rxConst.Matches(src).Cast<Match>()))
                {
                    scanned++;
                    if (!known.Contains(m.Groups[1].Value))
                    {
                        raisedMissing.Add(m.Groups[1].Value + "@" + Path.GetFileName(file));
                    }
                }
            }
            Expect(scanned > 20 && raisedMissing.Count == 0,
                $"A5 源码里 Raise 的字面量 / Hook* 常量 {scanned} 处都在 Known（数据驱动的 fg.TbActPacing.hookId 由 FgAct1SelfCheck A1、事件钩子由 FgEventDirectorSelfCheck 核）；漏登记：{string.Join(",", raisedMissing)}");

            var specMissing = SpecList.Where(x => !GuidanceQueue.HasContent(x.Hook)).Select(x => x.Trigger + "→" + x.Hook).ToList();
            Expect(specMissing.Count == 0, $"A6 FG13 FGR-UX-040 引导清单 {SpecList.Length} 条都有卡片（含第一次突袭的演习、静默夜前的说明）；缺：{string.Join(",", specMissing)}");
            var emgMissing = EmgB01.Where(h => !GuidanceQueue.HasContent(h)).ToList();
            Expect(emgMissing.Count == 0, $"A7 FG19 第 13 节 B01 的 7 个引导钩子都有卡片；缺：{string.Join(",", emgMissing)}");
            string pg = GameText.Get(GuidanceQueue.BodyKey(GuidanceHooks.PostgameFirstStart));
            string intro = GameText.Get("postgame.intro.l2");
            Expect(GuidanceQueue.HasContent(GuidanceHooks.PostgameFirstStart) && pg != intro && !pg.Contains("1200"),
                $"A8 DEBT-FG14SBX01-04：postgame.first_start 有教学卡片，且不重复说明面板（不复述周期 / 预算）：“{pg}”");
            string silent2 = GameText.Get(GuidanceQueue.StepKey(GuidanceHooks.EnvFirstSilentWarning, 2));
            Expect(silent2.Contains("储能") && silent2.Contains("炮塔"), $"A9 静默夜前的说明写明怎么准备（储能 / 炮塔 / 规则）：“{silent2}”");
            int withCodex = GuidanceHooks.Known.Count(h => GuidanceQueue.HasContent(h) && GuidanceQueue.CodexIdFor(h) != null);
            Expect(withCodex >= 80, $"A10 有图鉴系统说明可跳的卡片 {withCodex} 张（以钩子解锁的系统条目，“在图鉴中查看”）");
            var matter = MechanicCodex.Entries.Where(e => e.Id.StartsWith("codex.matter.", StringComparison.Ordinal)).ToList();
            bool matterOk = matter.Count == 17 && matter.All(e => e.Hooks.Contains(GuidanceHooks.EmgFirstMatter) && BothLanguages(e.TitleKey, true) && BothLanguages(e.BodyKey, true));
            Expect(matterOk && matter.Any(e => e.Id == "codex.matter.oil" && GameText.Get(e.BodyKey).Contains("整片燃烧")),
                $"A11 DEBT-EMG2MAT01-07 剩余项：图鉴里 17 种物质各有一条系统说明（随 emg_first_matter 解锁，标题用物质名、正文写接下来会怎样与“不分敌我”），实有 {matter.Count} 条");
        }

        private static bool BothLanguages(string key, bool requireAsciiEn)
        {
            if (!GameText.TryGet(key, GameLanguage.ZhCn, out string zh) || !GameText.TryGet(key, GameLanguage.En, out string en))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(zh) || string.IsNullOrWhiteSpace(en))
            {
                return false;
            }
            return !requireAsciiEn || !en.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        }

        // ── B 正常路径 ─────────────────────────────────────────────────────

        private static GuidanceCardHudUIToolkit BindCard(out GameObject go)
        {
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(CardUxml).CloneTree();
            go = new GameObject("__FgGuidanceCard") { hideFlags = HideFlags.HideAndDontSave };
            GuidanceCardHudUIToolkit card = go.AddComponent<GuidanceCardHudUIToolkit>();
            card.BindView(tree);
            return card;
        }

        /// <summary>准备一组测试钩子：从已看记录里拿掉（跑完由 Run 的 finally 整体还原）。</summary>
        private static void Fresh(params string[] hooks)
        {
            foreach (string h in hooks)
            {
                GameSettings.ForgetGuidanceHookForTests(h);
            }
            GuidanceQueue.ResetForTests();
            GameSettings.SetGuidanceEnabled(true);
            GuidanceQueue.EnsureSubscribed();
        }

        private static void CheckQueue()
        {
            GuidanceCardHudUIToolkit card = BindCard(out GameObject go);
            try
            {
                string a = GuidanceHooks.BuildModeFirstOpen, b = GuidanceHooks.LogisticsFirstBelt, c = GuidanceHooks.PowerFirstBrownout;
                Fresh(a, b, c);
                card.Refresh();
                Expect(!card.CardVisible, "B0 没有引导时卡片不显示");
                bool r1 = GuidanceHooks.Raise(a);
                card.Refresh();
                Expect(r1 && GuidanceQueue.Current == a && card.CardVisible && card.TitleText == "建造菜单" && card.BodyText.Contains("快捷栏")
                       && card.MoreText.Length == 0 && card.OkButton.text == "知道了" && !GameText.ContainsMarker(card.TitleText + card.BodyText),
                    $"B1 第一次打开建造菜单 → 卡片“{card.TitleText}”：{card.BodyText}（FGR-UX-040：指向相关界面，一两句话）");
                bool r2 = GuidanceHooks.Raise(a);
                Expect(!r2 && GuidanceQueue.Pending.Count == 0 && GuidanceQueue.Current == a, "B2 同一钩子第二次触发不再出卡（只出现一次，FGT-UX-005）");
                string codexId = GuidanceQueue.CurrentCodexId;
                Click(card.CodexButton);
                int cardCodexOrder = MechanicCodexPanelUIToolkit.TargetOrder;
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                Expect(codexId == "codex.build.grid" && MechanicCodex.LastOpenedId == codexId && !card.CodexButton.ClassListContains("uk-hidden")
                       && cardCodexOrder == GuidanceCardHudUIToolkit.Order - 1 && cardCodexOrder > CargoPanelUIToolkit.Order && cardCodexOrder > DifficultyPanelUIToolkit.Order
                       && cardCodexOrder > EndingSettlementPanelUIToolkit.Order,
                    $"B3 “在图鉴中查看”打开以该钩子解锁的系统说明（{codexId}），图鉴层级 {cardCodexOrder} = 卡片正下方，高于货舱 {CargoPanelUIToolkit.Order} / 难度 {DifficultyPanelUIToolkit.Order} / 结局结算 {EndingSettlementPanelUIToolkit.Order}（首次打开这些面板时弹卡，点了看得见）");
                Expect(GuidanceCardHudUIToolkit.Order > EndingSettlementPanelUIToolkit.Order && GuidanceCardHudUIToolkit.Order > NarrativeCutscenePlayerUIToolkit.Order
                       && GuidanceCardHudUIToolkit.Order < UiKitOverlayUIToolkit.Order,
                    $"B3b 引导卡片层级 {GuidanceCardHudUIToolkit.Order} 高于结局结算 {EndingSettlementPanelUIToolkit.Order} 与演出播放器 {NarrativeCutscenePlayerUIToolkit.Order}（ending.first_settlement / narrative.first_cutscene 弹卡时不被盖住），低于确认框浮层 {UiKitOverlayUIToolkit.Order}");
                Click(card.OkButton);
                card.Refresh();
                Expect(GuidanceQueue.Current == null && !card.CardVisible && GameSettings.HasSeenGuidanceHook(a), "B4 “知道了”关闭卡片，已看记录保留");

                // 离开世界（回主菜单 / 读档都走 CloseWorldUi）：当前卡与排队一起清掉，不带到主菜单或下一局；已看记录保留。
                Fresh(a, b);
                GuidanceHooks.Raise(a);
                GuidanceHooks.Raise(b);
                bool queued = GuidanceQueue.Current == a && GuidanceQueue.Pending.Count == 1;
                UiKitRuntime.CloseWorldUi();
                card.Refresh();
                Expect(queued && GuidanceQueue.Current == null && GuidanceQueue.Pending.Count == 0 && !card.CardVisible
                       && GameSettings.HasSeenGuidanceHook(a) && GameSettings.HasSeenGuidanceHook(b),
                    $"B4b 离开世界（CloseWorldUi）清掉当前卡与排队（排队过 {queued}），两条都仍记为看过、可在“引导与帮助”重看");

                // 演习式多步：第一次突袭预警 3 步。
                string w = GuidanceHooks.RaidFirstWarning;
                Fresh(w);
                GuidanceHooks.Raise(w);
                card.Refresh();
                string s1 = card.BodyText, ok1 = card.OkButton.text;
                Click(card.OkButton);
                card.Refresh();
                string s2 = card.BodyText, ok2 = card.OkButton.text;
                Click(card.OkButton);
                card.Refresh();
                string s3 = card.BodyText, ok3 = card.OkButton.text;
                Click(card.OkButton);
                card.Refresh();
                Expect(s1.Contains("预警条") && s2.Contains("观战") && s3.Contains("维修") && ok1 == "下一步（2/3）" && ok2 == "下一步（3/3）" && ok3 == "知道了" && !card.CardVisible,
                    $"B5 第一次突袭的演习式提示：预警 → 观战 → 修理，三步依次（按钮 {ok1} / {ok2} / {ok3}）");

                // 英文：同一张卡片换语言即刷新。
                Fresh(c);
                GuidanceHooks.Raise(c);
                GameSettings.SetLanguage(GameLanguage.En);
                card.Refresh();
                string enTitle = card.TitleText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                card.Refresh();
                Expect(enTitle == "Brownout" && card.TitleText == "缺电了", $"B6 文本走中英文本键，切语言立即刷新（en “{enTitle}” / zh “{card.TitleText}”）");
                Click(card.OkButton);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── C 负向矩阵 ─────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            GuidanceCardHudUIToolkit card = BindCard(out GameObject go);
            try
            {
                string[] five =
                {
                    GuidanceHooks.TurretFirstBuilt, GuidanceHooks.DefenseBarrierFirstBuilt, GuidanceHooks.DefenseGateFirstBuilt,
                    GuidanceHooks.DefenseShieldFirstBuilt, GuidanceHooks.DefenseTrapFirstBuilt,
                };
                Fresh(five);
                foreach (string h in five)
                {
                    GuidanceHooks.Raise(h);
                }
                card.Refresh();
                int cardsInTree = card.Card.parent.Query<VisualElement>("GuideCard").ToList().Count;
                Expect(GuidanceQueue.Current == five[0] && GuidanceQueue.Pending.Count == 4 && cardsInTree == 1 && card.MoreText == "还有 4 条引导排队"
                       && card.SkipButton.text == "跳过其余 4 条",
                    $"C1 同一时刻触发 5 条：只显示 1 张卡片、其余 4 条按触发顺序排队（{card.MoreText}），不叠加");
                Click(card.OkButton);
                Expect(GuidanceQueue.Current == five[1] && GuidanceQueue.Pending.Count == 3, "C2 “知道了”后轮到排队的下一条（先进先出）");

                // 引导不阻塞：不压模态、不进 Esc 栈、根节点不拦截点击、不停时间。
                bool modal = InputRouter.ModalOwnerList.Contains(card);
                int esc = UiEscapeStack.Count;
                bool paused = GameClock.Paused;
                VisualElement root = card.Card.parent;
                string pass = GameLogic.UI.Common.UiWindowFocus.PointerPassthroughClass;
                bool textPass = card.Card.ClassListContains(pass) && card.Card.Query<Label>().ToList().All(l => l.ClassListContains(pass))
                                && !card.OkButton.ClassListContains(pass) && !card.SkipButton.ClassListContains(pass);
                Expect(!modal && esc == 0 && !paused && root.pickingMode == PickingMode.Ignore && card.Card.pickingMode == PickingMode.Position && textPass,
                    "C3 引导不阻塞流程：卡片不压模态、不进 Esc 栈、全屏根节点不拦截点击、卡片底板与文字对世界输入放行（只有按钮挡）、不暂停游戏——玩家可以无视它继续玩 / 跳过任何系统");

                Click(card.SkipButton);
                card.Refresh();
                Expect(GuidanceQueue.Current == null && GuidanceQueue.Pending.Count == 0 && !card.CardVisible && five.All(GameSettings.HasSeenGuidanceHook),
                    "C4 “跳过其余”：当前与排队的都关掉（已记为看过，可在“引导与帮助”里重看）");

                // 关闭引导 → 排队清空、新的不再弹也不记看过 → 重新打开后第一次接触照样提示。
                string x = GuidanceHooks.EnvFirstWeather, y = GuidanceHooks.EnvFirstRoof, z = GuidanceHooks.EnvFirstPipeFrozen;
                Fresh(x, y, z);
                GuidanceHooks.Raise(x);
                GuidanceHooks.Raise(y);
                card.Refresh();
                Click(card.DisableButton);
                card.Refresh();
                int suppressed = GuidanceHooks.SuppressedCount;
                bool rz = GuidanceHooks.Raise(z);
                Expect(!GameSettings.GuidanceEnabled && GuidanceQueue.Current == null && GuidanceQueue.Pending.Count == 0 && !card.CardVisible
                       && !rz && GuidanceHooks.SuppressedCount == suppressed + 1 && !GameSettings.HasSeenGuidanceHook(z),
                    "C5 卡片“关闭引导”= 关总开关：排队清空；关着时新系统不弹卡、也不记看过");
                GameSettings.SetGuidanceEnabled(true);
                bool rz2 = GuidanceHooks.Raise(z);
                card.Refresh();
                Expect(rz2 && GuidanceQueue.Current == z && card.CardVisible, "C6 重新打开引导后，关着时错过的系统第一次接触照样提示");
                Click(card.OkButton);

                // 从设置关（不点卡片）：Tick 清掉排队中的首次引导。
                Fresh(x, y);
                GuidanceHooks.Raise(x);
                GuidanceHooks.Raise(y);
                GameSettings.SetGuidanceEnabled(false);
                GuidanceQueue.Tick();
                card.Refresh();
                Expect(GuidanceQueue.Current == null && GuidanceQueue.Pending.Count == 0 && !card.CardVisible && GuidanceQueue.ClearedByDisableCount == 2,
                    "C7 从设置 › 游戏性关掉引导时，正在显示与排队中的首次引导一并收起");
                GameSettings.SetGuidanceEnabled(true);

                // 重看：不改已看记录；重看插队时原来那条退回队首、不丢；关着引导也能重看。
                Fresh(z);
                int seenBefore = GameSettings.SeenGuidanceHooks.Count;
                bool rep = GuidanceQueue.Replay(x);
                card.Refresh();
                Expect(rep && GuidanceQueue.Current == x && GuidanceQueue.CurrentIsReplay && card.TagText == "重看" && GameSettings.SeenGuidanceHooks.Count == seenBefore
                       && card.DisableButton.ClassListContains("uk-hidden"),
                    "C8 重看一条看过的引导：卡片标“重看”，已看记录条数不变");
                Click(card.OkButton);
                GuidanceHooks.Raise(z);
                GuidanceQueue.Replay(y);
                Expect(GuidanceQueue.Current == y && GuidanceQueue.Pending.Count == 1 && GuidanceQueue.Pending[0] == z, "C9 首次引导显示中去重看：首次那条退回队首，重看完接着显示，不丢");
                Click(card.OkButton);
                Expect(GuidanceQueue.Current == z && !GuidanceQueue.CurrentIsReplay, "C9b 重看关掉后回到被插队的首次引导");
                Click(card.OkButton);
                GameSettings.SetGuidanceEnabled(false);
                bool repOff = GuidanceQueue.Replay(x);
                GuidanceQueue.Tick();
                Expect(repOff && GuidanceQueue.Current == x, "C10 引导关着时玩家主动重看照常显示（关闭只拦首次弹出）");
                Click(card.OkButton);
                GameSettings.SetGuidanceEnabled(true);
                Expect(!GuidanceQueue.Replay("guide.not.a.hook") && GuidanceQueue.Current == null, "C11 重看不存在的钩子：什么都不做");

                // none 理由的钩子：触发照常记看过、解锁图鉴，但不出卡（承担引导的那个钩子出卡）。
                string act = GuidanceHooks.Act1PaceFirstTurret, turret = GuidanceHooks.TurretFirstBuilt;
                Fresh(act, turret);
                int noContent = GuidanceQueue.NoContentCount;
                bool ra = GuidanceHooks.Raise(act);
                bool rt = GuidanceHooks.Raise(turret);
                Expect(ra && rt && GuidanceQueue.NoContentCount == noContent + 1 && GuidanceQueue.Current == turret && GuidanceQueue.Pending.Count == 0,
                    "C12 第一幕节奏点“第一座炮塔”与 turret.first_built 同时触发：只出 1 张炮塔卡（none 钩子不出重复卡）");
                GuidanceQueue.SkipAll();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── D 暂停与倍速 ───────────────────────────────────────────────────

        private static void CheckTiming()
        {
            GuidanceCardHudUIToolkit card = BindCard(out GameObject go);
            try
            {
                var results = new List<string>();
                (bool paused, float speed)[] matrix = { (true, 1f), (false, 0.5f), (false, 1f), (false, 2f), (false, 3f) };
                foreach ((bool paused, float speed) in matrix)
                {
                    string p = GuidanceHooks.IntelFirstBuilt, q = GuidanceHooks.IntelFirstOpen;
                    Fresh(p, q);
                    GameClock.SetSpeed(speed);
                    GameClock.SetPaused(paused);
                    Time.timeScale = paused ? 0f : speed;
                    GuidanceHooks.Raise(p);
                    GuidanceHooks.Raise(q);
                    for (int i = 0; i < 30; i++)
                    {
                        GuidanceQueue.Tick(); // 每帧路径：与游戏时间无关，不会自己消失或丢条目
                    }
                    card.Refresh();
                    bool first = GuidanceQueue.Current == p && GuidanceQueue.Pending.Count == 1 && card.CardVisible;
                    Click(card.OkButton);
                    bool second = GuidanceQueue.Current == q && GuidanceQueue.Pending.Count == 0;
                    Click(card.OkButton);
                    results.Add($"{(paused ? "暂停" : speed + "x")}:{(first && second && GuidanceQueue.Current == null ? "ok" : "bad")}");
                }
                Expect(results.All(r => r.EndsWith(":ok")), $"D1 暂停与 0.5x / 1x / 2x / 3x 下（GameClock 与 Time.timeScale 同设）：排队、显示、前进一致，不丢条目、不自己消失（{string.Join(" ", results)}）");
            }
            finally
            {
                Object.DestroyImmediate(go);
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                Time.timeScale = 1f;
            }
        }

        // ── E 存读档 ──────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            string h = GuidanceHooks.QuestFirstNew;
            Fresh(h);
            GuidanceHooks.Raise(h);
            GuidanceQueue.SkipAll();
            GameSettings.SetGuidanceEnabled(false);
            GameSettings.Load(); // 从磁盘（PlayerPrefs）重新读入
            bool seen = GameSettings.HasSeenGuidanceHook(h);
            bool off = !GameSettings.GuidanceEnabled;
            bool again = GuidanceHooks.Raise(h);
            GameSettings.SetGuidanceEnabled(true);
            GameSettings.Load();
            Expect(seen && off && !again && GameSettings.GuidanceEnabled,
                "E1 已看记录与总开关随本机设置落盘、重新读入后保持（换存档 / 新游戏也不重复弹；要再看用“引导与帮助”重看或重新显示全部）");
        }

        // ── F 引导与帮助 ─────────────────────────────────────────────────

        private static void CheckHelpPanel()
        {
            VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(HelpUxml).CloneTree();
            var go = new GameObject("__FgGuidanceHelp") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                GuidanceHelpPanelUIToolkit panel = go.AddComponent<GuidanceHelpPanelUIToolkit>();
                panel.BindView(tree);
                GameSettings.ResetSeenGuidanceHooks();
                GuidanceQueue.ResetForTests();
                GameSettings.SetGuidanceEnabled(true);
                GuidanceHelpPanelUIToolkit.Open();
                panel.Refresh();
                Expect(GuidanceHelpPanelUIToolkit.IsOpen && panel.VisibleRowCount == 0 && panel.EmptyText.Length > 0 && InputRouter.ModalOwnerList.Contains(panel),
                    $"F1 暂停菜单“引导与帮助”：没看过时写明“{panel.EmptyText}”；模态（盖在暂停菜单上面）");
                string a = GuidanceHooks.RulesPanelFirstOpen, b = GuidanceHooks.RosterPanelFirstOpen, none = GuidanceHooks.Act1PaceOverrideT1;
                GuidanceHooks.Raise(a);
                GuidanceHooks.Raise(b);
                GuidanceHooks.Raise(none);
                GuidanceQueue.SkipAll();
                panel.Refresh();
                Expect(panel.VisibleRowCount == 2 && panel.RowHook(0) == b && panel.RowHook(1) == a && panel.Row(0).text == "机器名册" && panel.CountText.StartsWith("已看过 2 条"),
                    $"F2 列表 = 看过、且有卡片的引导，最近的在前（none 钩子不列）：{panel.CountText}");
                Click(panel.Row(1));
                Expect(GuidanceQueue.Current == a && GuidanceQueue.CurrentIsReplay && GameSettings.SeenGuidanceHooks.Count == 3, "F3 点一条重看：卡片显示该引导，已看记录不变");
                GuidanceQueue.Advance();
                Click(panel.ToggleButton);
                panel.Refresh();
                bool offText = panel.ToggleButton.text == "引导：关";
                Click(panel.ToggleButton);
                panel.Refresh();
                Expect(offText && GameSettings.GuidanceEnabled && panel.ToggleButton.text == "引导：开", "F4 总开关在暂停菜单里也能关 / 重新打开（FGR-UX-041），与设置 › 游戏性同一个开关");
                Click(panel.ResetButton);
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Lines.Any(l => l.Contains("3 条"));
                UiConfirmDialog.Confirm();
                panel.Refresh();
                bool fresh = GuidanceHooks.Raise(a);
                Expect(asked && fresh && panel.VisibleRowCount <= 1, "F5 “重新显示全部引导”先确认（写明条数），确认后已看记录清空，再遇到时重新提示");
                GuidanceQueue.SkipAll();
                Click(panel.CloseButton);
                Expect(!GuidanceHelpPanelUIToolkit.IsOpen && !InputRouter.ModalOwnerList.Contains(panel), "F6 关闭按钮关掉面板并让出模态");
            }
            finally
            {
                if (GuidanceHelpPanelUIToolkit.IsOpen)
                {
                    GuidanceHelpPanelUIToolkit.Instance?.SetOpen(false);
                }
                UiConfirmDialog.DiscardAll();
                Object.DestroyImmediate(go);
            }
        }

        // ── G 每个面板的“?” ──────────────────────────────────────────────

        private static void CheckPanelHelp()
        {
            var rxClass = new Regex(@"class (\w+) : UiKitPanelHost");
            var rxUxml = new Regex("UxmlLocation => \"(\\w+)\"");
            var panels = new SortedDictionary<string, string>(); // UXML → 类名
            foreach (string file in Directory.GetFiles(LogicRoot, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(file);
                Match c = rxClass.Match(src);
                Match u = rxUxml.Match(src);
                if (c.Success && u.Success)
                {
                    panels[u.Groups[1].Value] = c.Groups[1].Value;
                }
            }
            int hostCount = panels.Count;
            // 直接持有 UIDocument 的界面：UXML 里带标题栏 / 关闭按钮 / 名为 Title 的标签的，都算面板（与 UiPanelHelp.HeaderOf 的落点一致）。
            var rxWindow = new Regex("mw-header|<ui:Button[^>]*name=\"[A-Za-z]*Close[A-Za-z]*\"|<ui:Label[^>]*name=\"Title\"");
            foreach (string file in Directory.GetFiles(UiRoot, "*.uxml", SearchOption.AllDirectories))
            {
                string n = Path.GetFileNameWithoutExtension(file);
                if (!panels.ContainsKey(n) && rxWindow.IsMatch(File.ReadAllText(file)))
                {
                    panels[n] = "（直接持有 UIDocument）";
                }
            }
            var direct = new[] { "AnalysisPanel", "BeaconLaunchPanel", "CircuitBoardPanel", "ExpeditionPrepPanel", "ExpeditionReturnPanel", "FactoryPanel", "MissionLog", "PrimitiveCraftPanel" };
            var directMissing = direct.Where(d => !panels.ContainsKey(d) || !UiPanelHelp.CodexByPanel.ContainsKey(d)).ToList();
            var unmapped = panels.Keys.Where(p => !UiPanelHelp.CodexByPanel.ContainsKey(p) && !UiPanelHelp.Exempt.Contains(p)).ToList();
            var both = panels.Keys.Where(p => UiPanelHelp.CodexByPanel.ContainsKey(p) && UiPanelHelp.Exempt.Contains(p)).ToList();
            var stale = UiPanelHelp.CodexByPanel.Keys.Concat(UiPanelHelp.Exempt).Where(p => !panels.ContainsKey(p)).ToList();
            Expect(hostCount >= 55 && panels.Count >= hostCount + 15 && directMissing.Count == 0 && unmapped.Count == 0 && both.Count == 0 && stale.Count == 0,
                $"G1 面板清单：UiKitPanelHost {hostCount} 个 + 直接持有 UIDocument 的界面 {panels.Count - hostCount} 个 = 有“?” {UiPanelHelp.CodexByPanel.Count} + 豁免 {UiPanelHelp.Exempt.Count}；电路编辑 / 解析台 / 远征准备等 {direct.Length} 个正式版面板缺映射：{string.Join(",", directMissing)}；未归类 {string.Join(",", unmapped)}；两边都写 {string.Join(",", both)}；表里多余 {string.Join(",", stale)}");
            // 直接持有 UIDocument 的面板要在生产代码里自己调 Attach（UiKitPanelHost 子类由基类统一调）。
            string allSrc = string.Join("\n", Directory.GetFiles(LogicRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            var notCalled = direct.Where(d => !allSrc.Contains("UiPanelHelp.Attach(\"" + d + "\"")).ToList();
            Expect(notCalled.Count == 0, $"G1b 直接持有 UIDocument 的 {direct.Length} 个面板在绑定后都调 UiPanelHelp.Attach；没调的：{string.Join(",", notCalled)}");
            var badCodex = UiPanelHelp.CodexByPanel.Where(kv => MechanicCodex.Find(kv.Value) == null).Select(kv => kv.Key + "→" + kv.Value).ToList();
            Expect(badCodex.Count == 0, $"G2 “?”指向的图鉴系统说明都存在；缺：{string.Join(",", badCodex)}");

            var bad = new List<string>();
            int injected = 0, own = 0;
            foreach (KeyValuePair<string, string> kv in UiPanelHelp.CodexByPanel)
            {
                string path = Directory.GetFiles(UiRoot, kv.Key + ".uxml", SearchOption.AllDirectories).FirstOrDefault();
                VisualTreeAsset vta = path != null ? AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path.Replace('\\', '/')) : null;
                if (vta == null)
                {
                    bad.Add(kv.Key + "（找不到 UXML）");
                    continue;
                }
                VisualElement root = vta.CloneTree();
                VisualElement header = UiPanelHelp.HeaderOf(root, out bool corner);
                bool hadOwn = header != null && UiPanelHelp.CountHelp(header) > 0;
                Button b = UiPanelHelp.Attach(kv.Key, root, 30081);
                if (header == null || UiPanelHelp.CountHelp(header) != 1)
                {
                    bad.Add(kv.Key + $"（标题栏“?”个数 {(header == null ? -1 : UiPanelHelp.CountHelp(header))}）");
                    continue;
                }
                if (hadOwn)
                {
                    own++;
                    continue;
                }
                injected++;
                Button close = header.Q<Button>(className: "mw-close-btn");
                bool beforeClose = corner
                    ? b.parent == header && b.style.position.value == Position.Absolute && b.style.right.value.value == 4f && b.style.top.value.value == 4f
                    : close == null || (b.parent == close.parent && b.parent.IndexOf(b) == close.parent.IndexOf(close) - 1);
                Click(b);
                if (MechanicCodex.LastOpenedId != kv.Value || !beforeClose || b.text != "?")
                {
                    bad.Add(kv.Key + $"（点了打开 {MechanicCodex.LastOpenedId}，在关闭按钮前 {beforeClose}）");
                }
            }
            Expect(bad.Count == 0 && injected >= 20 && own >= 14,
                $"G3 每个映射面板的标题栏恰好 1 个“?”（自带 {own}、统一补上 {injected}），在关闭按钮前（没有标题栏的钉在右上角），点它打开对应图鉴系统说明；有问题：{string.Join(",", bad)}");
            CheckOwnHelpLayers();

            // 层级：从层级高于图鉴的面板点“?”，图鉴临时盖到它上面，关掉后回到原层级。
            VisualElement codexTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, "MechanicCodexPanel.uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree();
            var go = new GameObject("__FgGuidanceCodex") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                MechanicCodexPanelUIToolkit codex = go.AddComponent<MechanicCodexPanelUIToolkit>();
                codex.BindView(codexTree);
                VisualElement diff = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, "DifficultyPanel.uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree();
                Button help = UiPanelHelp.Attach("DifficultyPanel", diff, DifficultyPanelUIToolkit.Order);
                Click(help);
                int above = MechanicCodexPanelUIToolkit.TargetOrder;
                codex.OpenAt(MechanicCodex.LastOpenedId);
                bool open = MechanicCodexPanelUIToolkit.IsOpen;
                codex.SetOpen(false);
                int after = MechanicCodexPanelUIToolkit.TargetOrder;
                VisualElement low = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, "PowerPanel.uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree();
                Click(UiPanelHelp.Attach("PowerPanel", low, PowerPanelUIToolkit.Order));
                int lowOrder = MechanicCodexPanelUIToolkit.TargetOrder;
                // 图鉴已被抬到难度面板之上时，再从低层入口（暂停菜单“图鉴”、电网面板“?”）跳转不降层。
                Click(UiPanelHelp.Attach("DifficultyPanel", AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, "DifficultyPanel.uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree(), DifficultyPanelUIToolkit.Order));
                codex.OpenAt(MechanicCodex.LastOpenedId);
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                int keep = MechanicCodexPanelUIToolkit.TargetOrder;
                codex.SetOpen(false);
                Expect(keep == DifficultyPanelUIToolkit.Order + 1, $"G4b 图鉴开在难度面板之上（{DifficultyPanelUIToolkit.Order + 1}）时再从低层入口跳转，层级保持 {keep}，不沉到难度面板下面");
                Expect(open && above == DifficultyPanelUIToolkit.Order + 1 && after == MechanicCodexPanelUIToolkit.Order && lowOrder == MechanicCodexPanelUIToolkit.Order,
                    $"G4 从难度面板（{DifficultyPanelUIToolkit.Order}，高于图鉴 {MechanicCodexPanelUIToolkit.Order}）点“?”：图鉴打开在 {above} 层盖住它，关掉后回到 {after}；从电网面板（低于图鉴）点仍是 {lowOrder}");
            }
            finally
            {
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                if (MechanicCodexPanelUIToolkit.IsOpen)
                {
                    MechanicCodexPanelUIToolkit.Instance?.SetOpen(false);
                }
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>G5：面板自带的“?”。层级高于图鉴的必须让图鉴盖上来或先关自己——真点货舱的“?”；资料库按源码核对先关自己；其余高层面板的“?”都由 Attach 带层级统一插入。</summary>
        private static void CheckOwnHelpLayers()
        {
            var high = new List<string>();
            foreach (string panel in UiPanelHelp.CodexByPanel.Keys)
            {
                Type t = typeof(UiPanelHelp).Assembly.GetType("GameLogic.UI.Kit." + panel + "UIToolkit");
                System.Reflection.FieldInfo f = t?.GetField("Order", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f != null && (int)f.GetValue(null) > MechanicCodexPanelUIToolkit.Order)
                {
                    VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, panel + ".uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree();
                    VisualElement header = UiPanelHelp.HeaderOf(root, out _);
                    if (header != null && UiPanelHelp.CountHelp(header) > 0)
                    {
                        high.Add(panel); // 自带“?”且高于图鉴
                    }
                }
            }
            var go = new GameObject("__FgGuidanceCargoHelp") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                CargoPanelUIToolkit cargo = go.AddComponent<CargoPanelUIToolkit>();
                VisualElement tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Directory.GetFiles(UiRoot, "CargoPanel.uxml", SearchOption.AllDirectories).First().Replace('\\', '/')).CloneTree();
                cargo.BindView(tree);
                Click(tree.Q<Button>("CargoHelp"));
                int order = MechanicCodexPanelUIToolkit.TargetOrder;
                Expect(MechanicCodex.LastOpenedId == "codex.expedition.cargo" && order == CargoPanelUIToolkit.Order + 1,
                    $"G5 点货舱面板（{CargoPanelUIToolkit.Order}）自带的“?”：打开 {MechanicCodex.LastOpenedId}，图鉴层级 {order} 盖住货舱（图鉴默认 {MechanicCodexPanelUIToolkit.Order}）");
            }
            finally
            {
                MechanicCodexPanelUIToolkit.RequestAbove(0);
                Object.DestroyImmediate(go);
            }
            string libSrc = File.ReadAllText(Directory.GetFiles(LogicRoot, "NarrativeLibraryPanelUIToolkit.cs", SearchOption.AllDirectories).First());
            bool libClosesFirst = Regex.IsMatch(libSrc, @"_help\.clicked \+= \(\) =>\s*\{\s*SetOpen\(false\);\s*MechanicCodex\.Open\(""codex\.narrative""\)");
            var unhandled = high.Where(h => h != "CargoPanel" && h != "NarrativeLibraryPanel").ToList();
            Expect(unhandled.Count == 0 && high.Contains("CargoPanel") && libClosesFirst,
                $"G5b 自带“?”且层级高于图鉴的面板：{string.Join(",", high)}；货舱已实点，资料库点“?”先关自己再开图鉴（{libClosesFirst}）；未处理的：{string.Join(",", unhandled)}");
        }

        // ── H 布局探针（四种分辨率 + 超长文字） ─────────────────────────────

        private static void CheckLayout()
        {
            string card = UiToolkitLayoutProbe.Probe(CardUxml, "GuideRoot", true, UiToolkitLayoutProbe.DefaultPanelSettingsPath, root =>
            {
                root.Q<VisualElement>("GuideCard").RemoveFromClassList("uk-hidden");
                root.Q<Label>("GuideTitle").text = "第一次突袭预警（演习）";
                root.Q<Label>("GuideBody").text = GameText.Get(GuidanceQueue.BodyKey(GuidanceHooks.RaidFirstWarning)) + UiToolkitLayoutProbe.StressText;
                root.Q<Label>("GuideMore").text = "还有 12 条引导排队";
                root.Q<Button>("GuideOk").text = "下一步（2/3）";
                root.Q<Button>("GuideCodex").text = "在图鉴中查看";
                root.Q<Button>("GuideSkip").text = "跳过其余 12 条";
                root.Q<Button>("GuideDisable").text = "关闭引导";
            });
            Expect(card.StartsWith("PASS", StringComparison.Ordinal), card.StartsWith("PASS", StringComparison.Ordinal) ? "H1 引导卡片布局探针 PASS（四种分辨率 × 超长正文 / 四个按钮）" : "H1 引导卡片布局探针未通过：\n" + card);
            string help = UiToolkitLayoutProbe.Probe(HelpUxml, "GuideHelpRoot", true, UiToolkitLayoutProbe.DefaultPanelSettingsPath, root =>
            {
                ScrollView list = root.Q<ScrollView>("GuideHelpList");
                for (int i = 0; i < 40; i++)
                {
                    var b = new Button { text = "第一次突袭预警（演习）" + i };
                    b.AddToClassList("mw-btn");
                    b.AddToClassList("gd-help-row");
                    list.Add(b);
                }
            });
            Expect(help.StartsWith("PASS", StringComparison.Ordinal), help.StartsWith("PASS", StringComparison.Ordinal) ? "H2 引导与帮助布局探针 PASS（四种分辨率 × 40 行列表在栏内滚动）" : "H2 引导与帮助布局探针未通过：\n" + help);
        }

        // ── 工具 ─────────────────────────────────────────────────────────

        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
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

        private static void Line(string text) => _report.AppendLine(text);
    }
}
