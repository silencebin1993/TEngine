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

        [MenuItem("BinGames/自检：主菜单初始化")]
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
                UnityEngine.Object.DestroyImmediate(instance);
            }
            return _fail;
        }

        /// <summary>UnityEvent 的运行时监听数（AddListener 加的，不含预制体里序列化的）。</summary>
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
