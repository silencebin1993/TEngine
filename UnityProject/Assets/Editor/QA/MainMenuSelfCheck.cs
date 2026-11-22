using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GameLogic.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 主菜单走真实初始化路径的回归闸门：实例化正式预制体，按 UIWindow 生命周期真跑一遍
    /// <c>ScriptGenerator</c> + <c>OnCreate</c>（反射调用，和运行时同一份代码），断言不抛异常、每个节点都找得到。
    /// 2026-09-25 实锤：代码新增重绑行而预制体没加节点，主菜单一创建就空引用、整页初始化中断——
    /// 只实例化预制体、逐个点按钮的自检测不出来，必须走 ScriptGenerator。并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class MainMenuSelfCheck
    {
        private const string PrefabPath = "Assets/GameRes/Raw/UI/MainMenuUI.prefab";

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("QA/自检/主菜单初始化")]
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
            Line("\n[主菜单] 真实初始化路径（ScriptGenerator + OnCreate）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过");
                return 0;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Fail($"找不到主菜单预制体 {PrefabPath}");
                return _fail;
            }

            ConfigSystem.Instance.Load(); // FG15-UX-03：静态文字按文本表认键，先确保文本表已加载
            Localization.GameText.Reload();
            Localization.GameLanguage languageBefore = Settings.GameSettings.Language;
            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            var errors = new List<string>();
            void OnLog(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    errors.Add(condition);
                }
            }
            Application.logMessageReceived += OnLog;
            try
            {
                var window = (MainMenuUI)Activator.CreateInstance(typeof(MainMenuUI), true);
                FieldInfo panel = typeof(UIWindow).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic);
                if (panel == null)
                {
                    Fail("UIWindow._panel 字段找不到（框架改名了？）");
                    return _fail;
                }
                panel.SetValue(window, instance);

                Invoke(window, "ScriptGenerator", "ScriptGenerator（找节点、绑事件）");
                Invoke(window, "OnCreate", "OnCreate（视觉整理、点击音、首屏）");

                var labels = (Dictionary<GameActionId, Text>)typeof(MainMenuUI)
                    .GetField("_rebindLabels", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
                // FG0-UX-01：主菜单设置页保留 ER2 的常用动作行（预制体里的固定行），其余全部动作在“全部按键…”打开的
                // UI Toolkit 面板里重绑。这里逐个核对预制体里的常用行都绑上了，并且“全部按键…”按钮真实存在、有点击响应。
                var rows = (Array)typeof(MainMenuUI).GetField("RebindRows", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                var missing = new List<string>();
                int rowCount = 0;
                if (rows != null)
                {
                    foreach (object row in rows)
                    {
                        rowCount++;
                        var action = (GameActionId)row.GetType().GetField("Item1").GetValue(row);
                        if (labels == null || !labels.ContainsKey(action))
                        {
                            missing.Add(action.ToString());
                        }
                    }
                }
                Expect(rowCount >= 7 && missing.Count == 0, $"设置页 {rowCount} 个常用改键行都有对应的预制体节点" +
                                           (missing.Count == 0 ? string.Empty : "——缺：" + string.Join("、", missing)));
                var allKeys = (Button)typeof(MainMenuUI).GetField("_btnAllKeyBindings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
                Text allKeysLabel = allKeys != null ? allKeys.GetComponentInChildren<Text>(true) : null;
                Expect(allKeys != null && allKeys.onClick.GetPersistentEventCount() == 0 && CountRuntimeListeners(allKeys) > 0
                       && allKeysLabel != null && allKeysLabel.text == Localization.GameText.Get("ui.keybind.open_all")
                       && allKeys.transform.parent == ((Button)typeof(MainMenuUI).GetField("_btnResetAllDefaults", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window)).transform.parent,
                    $"设置页有“全部按键…”按钮（文本键、与“恢复默认”同一布局组、已接点击）：{(allKeysLabel != null ? allKeysLabel.text : "无")}");
                // FG14-SBX-02（FGR-SBX-010）：根菜单有“沙盒模式”（复制“新建”、排在它后面、文本键、已接点击）。
                var sandbox = (Button)typeof(MainMenuUI).GetField("_btnSandbox", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
                var btnNew = (Button)typeof(MainMenuUI).GetField("_btnNew", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
                Text sandboxLabel = sandbox != null ? sandbox.GetComponentInChildren<Text>(true) : null;
                Expect(sandbox != null && btnNew != null && sandbox.transform.parent == btnNew.transform.parent
                       && sandbox.transform.GetSiblingIndex() == btnNew.transform.GetSiblingIndex() + 1 && CountRuntimeListeners(sandbox) > 0
                       && sandboxLabel != null && sandboxLabel.text == Localization.GameText.Get("sandbox.menu.button"),
                    $"根菜单有“沙盒模式”按钮（在“新建”后面、文本键、已接点击）：{(sandboxLabel != null ? sandboxLabel.text : "无")}");
                CheckSandboxModeNotSticky(window);
                CheckLanguageSwitch(window, instance);
                Expect(errors.Count == 0, "初始化过程零报错" + (errors.Count == 0 ? string.Empty : "——" + string.Join("；", errors)));
                CheckSlotText();
            }
            catch (Exception e)
            {
                Fail($"主菜单自检抛异常：{e}");
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Settings.GameSettings.SetLanguage(languageBefore);
                UnityEngine.Object.DestroyImmediate(instance);
            }
            return _fail;
        }

        /// <summary>
        /// FG14-SBX-02 修复（审查 P1）：沙盒标志不得残留——点过“沙盒模式”再取消 / 返回后，从“读取”页空槽 / 坏档新建打开的必须是剧情档设置面板。
        /// 走真 UXML 绑定的新游戏面板，经主菜单私有入口（覆盖确认、读取页空槽）驱动，读面板 IsSandboxMode。
        /// </summary>
        private static void CheckSandboxModeNotSticky(MainMenuUI window)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type viewType = typeof(MainMenuUI).GetNestedType("MenuView", BindingFlags.NonPublic);
            MethodInfo confirm = typeof(MainMenuUI).GetMethod("ConfirmNewInSlot", Flags);
            MethodInfo yes = typeof(MainMenuUI).GetMethod("OnConfirmYesClicked", Flags);
            MethodInfo no = typeof(MainMenuUI).GetMethod("OnConfirmNoClicked", Flags);
            MethodInfo start = typeof(MainMenuUI).GetMethod("StartNewCampaign", Flags);
            MethodInfo slotAction = typeof(MainMenuUI).GetMethod("OnSlotActionClicked", Flags);
            var uxml = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/NewGamePanel.uxml");
            if (viewType == null || confirm == null || yes == null || no == null || start == null || slotAction == null || uxml == null)
            {
                Fail("沙盒标志负向断言：主菜单入口方法或新游戏面板 UXML 找不到");
                return;
            }
            object root = Enum.Parse(viewType, "Root");
            object slotList = Enum.Parse(viewType, "SlotList");
            var meta = new Campaign.CampaignSlotMetadata { SlotIndex = 0 };
            var go = new GameObject("__MainMenuSandboxFlag") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                UI.Kit.NewGamePanelUIToolkit panel = go.AddComponent<UI.Kit.NewGamePanelUIToolkit>();
                panel.BindView(uxml.CloneTree());
                // 沙盒 → 三槽全满的覆盖确认 → 取消 → 读取页坏档新建 → 确认：剧情档。
                confirm.Invoke(window, new object[] { meta, root, true });
                no.Invoke(window, null);
                confirm.Invoke(window, new object[] { meta, slotList, false });
                yes.Invoke(window, null);
                bool afterCancel = UI.Kit.NewGamePanelUIToolkit.IsOpen && !panel.IsSandboxMode;
                UI.Kit.NewGamePanelUIToolkit.Close();
                // 沙盒覆盖确认 → 确认：沙盒档（正向对照）。
                confirm.Invoke(window, new object[] { meta, root, true });
                yes.Invoke(window, null);
                bool sandboxConfirm = UI.Kit.NewGamePanelUIToolkit.IsOpen && panel.IsSandboxMode;
                UI.Kit.NewGamePanelUIToolkit.Close();
                // 沙盒面板打开过 → 返回 → 读取页空槽新建：剧情档（只读槽位元数据，空槽不写文件）。
                start.Invoke(window, new object[] { 0, true });
                UI.Kit.NewGamePanelUIToolkit.Close();
                int empty = -1;
                for (int i = 0; i < Campaign.CampaignSaveService.SlotCount && empty < 0; i++)
                {
                    if (Campaign.CampaignSaveService.GetSlotMetadata(i).State == Campaign.CampaignSlotState.Empty)
                    {
                        empty = i;
                    }
                }
                bool emptySlot = true;
                if (empty >= 0)
                {
                    slotAction.Invoke(window, new object[] { empty });
                    emptySlot = UI.Kit.NewGamePanelUIToolkit.IsOpen && !panel.IsSandboxMode;
                    UI.Kit.NewGamePanelUIToolkit.Close();
                }
                Expect(afterCancel && sandboxConfirm && emptySlot,
                    $"沙盒标志不残留：沙盒确认取消后坏档新建 = 剧情档 {afterCancel}，沙盒确认 = 沙盒档 {sandboxConfirm}，"
                    + (empty >= 0 ? $"沙盒返回后读取页空槽 {empty + 1} 新建 = 剧情档 {emptySlot}" : "（无空槽，空槽分支未走）"));
            }
            finally
            {
                UI.Kit.NewGamePanelUIToolkit.Close();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>UnityEvent 的运行时监听数（AddListener 加的，不含预制体里序列化的）。</summary>
        /// <summary>
        /// FG15-UX-03 修复轮（审查 P1：主菜单 uGUI 预制体文字写死中文）：设置里切英文后，主菜单下一帧（OnUpdate）全部静态文字变英文、无中文残留；
        /// 切回中文后恢复中文。代码按存档 / 键位现写的动态文字（存档卡片、继续原因、覆盖说明、重绑按钮上的键名）不在此列。
        /// </summary>
        private static void CheckLanguageSwitch(MainMenuUI window, GameObject instance)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var dynamic = new HashSet<Text>();
            foreach (string f in new[] { "_slotInfoTexts", "_slotActionLabels" })
            {
                foreach (Text t in (Text[])typeof(MainMenuUI).GetField(f, flags).GetValue(window))
                {
                    dynamic.Add(t);
                }
            }
            dynamic.UnionWith(((Dictionary<GameActionId, Text>)typeof(MainMenuUI).GetField("_rebindLabels", flags).GetValue(window)).Values);
            dynamic.Add((Text)typeof(MainMenuUI).GetField("_textContinueReason", flags).GetValue(window));
            dynamic.Add((Text)typeof(MainMenuUI).GetField("_textConfirmInfo", flags).GetValue(window));
            var cjk = new System.Text.RegularExpressions.Regex("[㐀-鿿！-｠　-〿]");
            var tracked = (System.Collections.ICollection)typeof(MainMenuUI).GetField("_staticTexts", flags).GetValue(window);

            Settings.GameSettings.SetLanguage(Localization.GameLanguage.En);
            Invoke(window, "OnUpdate", "OnUpdate（切英文后的下一帧）");
            var left = new List<string>();
            int checkedCount = 0;
            foreach (Text t in instance.GetComponentsInChildren<Text>(true))
            {
                if (dynamic.Contains(t) || string.IsNullOrEmpty(t.text))
                {
                    continue;
                }
                checkedCount++;
                if (cjk.IsMatch(t.text))
                {
                    left.Add(t.name + "“" + t.text + "”");
                }
            }
            Text newLabel = ((Button)typeof(MainMenuUI).GetField("_btnNew", flags).GetValue(window)).GetComponentInChildren<Text>(true);
            Expect(tracked.Count >= 25 && left.Count == 0 && newLabel.text == Localization.GameText.Get("mainmenu.prefab.new", Localization.GameLanguage.En),
                $"切英文后主菜单 {checkedCount} 个静态文字无中文（登记 {tracked.Count} 个；“新建”=“{newLabel.text}”）" + (left.Count == 0 ? string.Empty : "——残留：" + string.Join("、", left)));
            Settings.GameSettings.SetLanguage(Localization.GameLanguage.ZhCn);
            Invoke(window, "OnUpdate", "OnUpdate（切回中文）");
            Expect(newLabel.text == Localization.GameText.Get("mainmenu.prefab.new", Localization.GameLanguage.ZhCn) && cjk.IsMatch(newLabel.text),
                $"切回中文后主菜单恢复中文（“新建”=“{newLabel.text}”）");
        }

        private static int CountRuntimeListeners(Button button)
        {
            object calls = typeof(UnityEngine.Events.UnityEventBase).GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(button.onClick);
            object runtime = calls?.GetType().GetField("m_RuntimeCalls", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(calls);
            return runtime is System.Collections.ICollection c ? c.Count : 0;
        }

        /// <summary>存档卡/覆盖确认/失败页的玩家文字：不出现战役 ID、英文阶段枚举、UTC 时间串、区域内部 ID。</summary>
        private static void CheckSlotText()
        {
            var badPhases = new List<string>();
            foreach (Campaign.CampaignPhase phase in Enum.GetValues(typeof(Campaign.CampaignPhase)))
            {
                string name = Campaign.CampaignSlotText.PhaseName(phase);
                if (name == "未知阶段" || System.Text.RegularExpressions.Regex.IsMatch(name, "[A-Za-z]"))
                {
                    badPhases.Add(phase.ToString());
                }
            }
            Expect(badPhases.Count == 0, "每个战役阶段都有中文名" + (badPhases.Count == 0 ? string.Empty : "——缺：" + string.Join("、", badPhases)));
            Expect(Campaign.CampaignSlotText.PlayTime(3725f) == "1 小时 02 分" && Campaign.CampaignSlotText.PlayTime(125f) == "2 分 05 秒",
                $"游戏时长写成“1 小时 02 分 / 2 分 05 秒”（实际“{Campaign.CampaignSlotText.PlayTime(3725f)}”“{Campaign.CampaignSlotText.PlayTime(125f)}”）");

            var meta = new Campaign.CampaignSlotMetadata
            {
                SlotIndex = 0,
                State = Campaign.CampaignSlotState.Ready,
                CampaignId = "campaign-3f9a1c",
                CampaignPhase = Campaign.CampaignPhase.FirstExpedition,
                PlaySeconds = 1830f,
                WrittenAtUtc = "2026-09-25T05:20:00.0000000Z",
                ContentVersion = 1,
                LastRegionId = Campaign.Regions.FracturedCityLayout.RegionId,
            };
            string summary = Campaign.CampaignSlotText.Summary(meta);
            bool noInternal = !summary.Contains(meta.CampaignId) && !summary.Contains("FirstExpedition") && !summary.Contains("T05:20")
                              && !summary.Contains(meta.LastRegionId);
            Expect(noInternal && summary.Contains("首次远征") && summary.Contains("30 分 30 秒") && summary.Contains("破碎都市")
                   && System.Text.RegularExpressions.Regex.IsMatch(summary, @"保存于 \d{4}-\d{2}-\d{2} \d{2}:\d{2}"),
                $"存档卡摘要是玩家文字（实际“{summary.Replace("\n", " / ")}”）");
            Expect(Campaign.CampaignSlotText.SlotNumber(0) == 1, "槽位号从 1 开始（失败页此前写“第 0 槽”）");
        }

        private static void Invoke(MainMenuUI window, string method, string label)
        {
            MethodInfo mi = typeof(MainMenuUI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null)
            {
                Fail($"{label}：方法找不到");
                return;
            }
            try
            {
                mi.Invoke(window, null);
                Line($"  ✓ {label} 跑完不抛异常");
            }
            catch (TargetInvocationException e)
            {
                Fail($"{label} 抛异常：{e.InnerException?.GetType().Name}: {e.InnerException?.Message}\n{e.InnerException?.StackTrace}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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
            _report.AppendLine(text);
        }
    }
}
