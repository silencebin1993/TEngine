using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Battle.Feedback;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8-CONTENT-01：设置项“写得进、没人读”的回归闸门——UI 缩放（AC-UI-004）、镜头速度、边缘平移、
    /// 屏幕震动与降低闪光（AC-ACC-003）逐项断言真实消费方的行为，而不是只看字段存在。
    /// 并入 <c>CellFrameworkValidate.RunAll</c>。自检会临时改设置（写 PlayerPrefs），结束时逐项还原。
    /// </summary>
    public static class SettingsConsumersSelfCheck
    {
        private const string UiRoot = "Assets/GameRes/Raw/UI/";

        /// <summary>《地球归还》正式 UI Toolkit 面板（uxml, 面板根节点）。资源 HUD / 速度 HUD 是代码搭建的
        /// 视觉树，探针无从加载，不在此列。</summary>
        private static readonly (string Uxml, string Root)[] Panels =
        {
            ("Analysis/AnalysisPanel.uxml", "AnalysisPanelRoot"),
            ("Beacon/BeaconLaunchPanel.uxml", "BeaconLaunchPanelRoot"),
            ("CircuitBoard/CircuitBoardPanel.uxml", "CircuitBoardPanelRoot"),
            ("CoreBoss/CoreBossHud.uxml", "CoreBossHudRoot"),
            ("Expedition/ExpeditionPrepPanel.uxml", "ExpeditionPrepPanelRoot"),
            ("Expedition/ExpeditionReturnPanel.uxml", "ExpeditionReturnPanelRoot"),
            ("Factory/FactoryPanel.uxml", "FactoryPanelRoot"),
            ("Feedback/FeedbackCaptionHud.uxml", "FeedbackCaptionRoot"),
            ("HomeValleyFailure/HomeValleyFailure.uxml", "HomeValleyFailureRoot"),
            ("Objective/MissionLog.uxml", "MissionLogRoot"),
            ("Objective/ObjectiveHud.uxml", "ObjectiveHudRoot"),
            ("PrimitiveCraft/PrimitiveCraftPanel.uxml", "CraftPanelRoot"),
            ("RegionCommand/RegionCommandBar.uxml", "RegionCommandBarRoot"),
            ("Victory/VictoryPage.uxml", "VictoryPageRoot"),
            ("WorkOrder/WorkOrderPanel.uxml", "WorkOrderPanelRoot"),
        };

        // FG0-UX-01（FGR-UX-060）：UI 缩放上限从 140% 提到 150%，极值按新上限测。
        private static readonly float[] UiScales = { 0.8f, 1f, 1.5f };

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：设置项消费方")]
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
            Line("\n[设置] 设置项消费方（ER8-CONTENT-01 AC-UI-004 / AC-ACC-003）");

            float savedCamera = GameSettings.CameraSpeedMultiplier;
            bool savedEdge = GameSettings.EdgePanEnabled;
            float savedEdgeSpeed = GameSettings.EdgePanSpeedMultiplier;
            bool savedShake = GameSettings.ScreenShakeEnabled;
            bool savedFlash = GameSettings.FlashReductionEnabled;
            try
            {
                CheckCanvasReference();
                CheckPanelsAtUiScales();
                CheckCameraSpeedAndEdgePan();
                CheckScreenShake();
                CheckFlashReduction();
                CheckPanelTheme();
            }
            catch (Exception e)
            {
                Fail($"设置自检抛异常：{e}");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                ScreenShake.Reset();
                RestoreFloat(GameSettings.CameraSpeedMultiplier, savedCamera, GameSettings.SetCameraSpeedMultiplier);
                RestoreFloat(GameSettings.EdgePanSpeedMultiplier, savedEdgeSpeed, GameSettings.SetEdgePanSpeedMultiplier);
                if (GameSettings.EdgePanEnabled != savedEdge) GameSettings.SetEdgePanEnabled(savedEdge);
                if (GameSettings.ScreenShakeEnabled != savedShake) GameSettings.SetScreenShakeEnabled(savedShake);
                if (GameSettings.FlashReductionEnabled != savedFlash) GameSettings.SetFlashReductionEnabled(savedFlash);
            }
            return _fail;
        }

        private static void RestoreFloat(float current, float saved, Action<float> setter)
        {
            if (!Mathf.Approximately(current, saved))
            {
                setter(saved);
            }
        }

        // ── 题材用词（DEBT-ER2THEME01-01 回归闸门）────────────────────────

        /// <summary>《地球归还》正式面板 UXML 里写死的文字不得出现旧生物题材词或内部 ID。旧细胞阶段界面虽然随
        /// 战役挂载，但全程隐藏（只在细胞阶段运行时显示），不在此列。</summary>
        private static void CheckPanelTheme()
        {
            var hits = new List<string>();
            int scanned = 0;
            foreach ((string uxml, string _) in Panels)
            {
                string text = System.IO.File.ReadAllText(UiRoot + uxml);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\btext=""([^""]*)"""))
                {
                    string value = m.Groups[1].Value;
                    scanned++;
                    if (FeedbackCueSelfCheck.ForbiddenWords.IsMatch(value) || FeedbackCueSelfCheck.InternalIdPattern.IsMatch(value))
                    {
                        hits.Add($"{uxml}“{value}”");
                    }
                }
            }
            // 扫描本身失效（正则写坏、一条都没匹配到）必须报错，不能静默通过。
            Expect(hits.Count == 0 && scanned >= 50, $"{Panels.Length} 个正式面板的 {scanned} 处界面文字无旧题材词、无内部 ID" +
                                    (hits.Count == 0 ? string.Empty : "：" + string.Join("；", hits)));
        }

        // ── UI 缩放 ────────────────────────────────────────────────────

        private static void CheckCanvasReference()
        {
            Vector2 big = UiScaleApplier.CanvasReferenceFor(1.4f);
            Vector2 small = UiScaleApplier.CanvasReferenceFor(0.8f);
            Vector2 unit = UiScaleApplier.CanvasReferenceFor(1f);
            Expect(unit == new Vector2(1920f, 1080f) && Mathf.Abs(big.x - 1920f / 1.4f) < 0.01f && Mathf.Abs(small.y - 1350f) < 0.01f,
                $"uGUI 根 CanvasScaler 参考分辨率随缩放反比变化（1.0→{unit}，1.4→{big}，0.8→{small}）");
        }

        private static void CheckPanelsAtUiScales()
        {
            foreach ((string uxml, string root) in Panels)
            {
                var failedScales = new List<string>();
                string firstProblem = null;
                foreach (float scale in UiScales)
                {
                    string result = UiToolkitLayoutProbe.Probe(UiRoot + uxml, root, true,
                        UiToolkitLayoutProbe.DefaultPanelSettingsPath, null, scale);
                    if (!result.StartsWith("PASS", StringComparison.Ordinal))
                    {
                        failedScales.Add($"{scale * 100f:0}%");
                        firstProblem ??= FirstProblemLines(result, 6);
                    }
                }
                Expect(failedScales.Count == 0, failedScales.Count == 0
                    ? $"{uxml}：80%/100%/140% × 四种分辨率无越界、无文字塌缩（AC-UI-004）"
                    : $"{uxml}：{string.Join("/", failedScales)} 未通过布局探针\n{firstProblem}");
            }
        }

        private static string FirstProblemLines(string probeResult, int maxLines)
        {
            var lines = new List<string>();
            foreach (string line in probeResult.Split('\n'))
            {
                string t = line.TrimEnd();
                if (t.StartsWith("  ", StringComparison.Ordinal) || t.StartsWith("FAIL", StringComparison.Ordinal)
                    || t.Contains("找不到") || t.Contains("加载失败"))
                {
                    lines.Add("      " + t.Trim());
                    if (lines.Count >= maxLines)
                    {
                        break;
                    }
                }
            }
            return string.Join("\n", lines);
        }

        // ── 镜头速度 / 边缘平移 ─────────────────────────────────────────

        private sealed class FakeReader : IInputReader
        {
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public Vector3 Mouse;
            public bool GetKey(KeyCode key) => Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
        }

        private static Camera NewCamera()
        {
            var go = new GameObject("__SettingsSelfCheckCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 20f;
            go.transform.rotation = Quaternion.Euler(60f, 0f, 0f);
            return cam;
        }

        private static void CheckCameraSpeedAndEdgePan()
        {
            Camera cam = NewCamera();
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            GameSettings.SetScreenShakeEnabled(false);
            try
            {
                var director = new CameraDirector();
                director.Bind(cam, (out float2 anchor) => { anchor = default; return false; },
                    new Vector3(0f, 30f, -18f), 60f, startInStrategy: true);
                reader.Mouse = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);

                float Move()
                {
                    float before = director.StrategyFocus.x;
                    director.Tick(false);
                    return director.StrategyFocus.x - before;
                }

                reader.Held.Add(KeyCode.D);
                GameSettings.SetCameraSpeedMultiplier(1f);
                float normal = Move();
                GameSettings.SetCameraSpeedMultiplier(2f);
                float fast = Move();
                Expect(normal > 0f && Mathf.Abs(fast / normal - 2f) < 0.02f,
                    $"镜头速度 ×2 → 战略平移距离 ×2（实际 {normal:F3} → {fast:F3}）");
                GameSettings.SetCameraSpeedMultiplier(1f);
                reader.Held.Clear();

                if (Screen.width <= 32 || Screen.height <= 32)
                {
                    Line($"  · 边缘平移：当前环境屏幕尺寸 {Screen.width}x{Screen.height} 过小，跳过推屏距离断言");
                    return;
                }
                reader.Mouse = new Vector3(1f, Screen.height * 0.5f, 0f); // 贴左边缘
                GameSettings.SetEdgePanEnabled(false);
                float off = Move();
                GameSettings.SetEdgePanEnabled(true);
                GameSettings.SetEdgePanSpeedMultiplier(1f);
                float edge = Move();
                GameSettings.SetEdgePanSpeedMultiplier(2f);
                float edgeFast = Move();
                Expect(Mathf.Abs(off) < 1e-5f, $"边缘平移关闭 → 指针贴边镜头不动（实际位移 {off:F4}）");
                Expect(edge < 0f && Mathf.Abs(edgeFast / edge - 2f) < 0.02f,
                    $"边缘平移开启 → 向左推屏，边缘平移速度 ×2 → 距离 ×2（实际 {edge:F3} → {edgeFast:F3}）");
                director.Unbind();
            }
            finally
            {
                Object.DestroyImmediate(cam.gameObject);
            }
        }

        // ── 屏幕震动 ───────────────────────────────────────────────────

        private static void CheckScreenShake()
        {
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);

            GameSettings.SetScreenShakeEnabled(false);
            ScreenShake.Reset();
            ScreenShake.AddTrauma(1f);
            Expect(ScreenShake.Trauma == 0f, "屏幕震动关闭 → 大事件不注入震动量");

            GameSettings.SetScreenShakeEnabled(true);
            FeedbackCues.ResetForTests();
            FeedbackCues.Raise(FeedbackCueId.BossPhase, "核心暴露");
            Expect(ScreenShake.Trauma > 0f, $"屏幕震动开启 → Boss 阶段变化注入震动量（{ScreenShake.Trauma:F2}）");
            ScreenShake.Reset();

            // 直控跟随从“当前相机位置”做平滑：同一起点、同样帧数，带震动的一台在震动结束后必须与
            // 从未震动的一台落在同一位置——证明偏移每帧都被撤掉，没有被跟随算法吃进去。
            Vector3 start = new Vector3(0f, 30f, -18f);
            Vector3 clean = RunDirectFollow(start, 12, shakeFrames: 0);
            Vector3 shakenThenSettled = RunDirectFollow(start, 12, shakeFrames: 11);
            Expect((clean - shakenThenSettled).sqrMagnitude < 1e-8f,
                $"震动结束后镜头轨迹与未震动时完全一致，无漂移（差 {(clean - shakenThenSettled).magnitude:E1}）");

            GameSettings.SetScreenShakeEnabled(false);
            Vector3 disabled = RunDirectFollow(start, 12, shakeFrames: 11);
            Expect((clean - disabled).sqrMagnitude < 1e-8f, "屏幕震动关闭 → 注入的震动对镜头零影响");
            FeedbackCues.ResetForTests();
        }

        private static Vector3 RunDirectFollow(Vector3 start, int frames, int shakeFrames)
        {
            Camera cam = NewCamera();
            try
            {
                cam.transform.position = start;
                var director = new CameraDirector();
                director.Bind(cam, (out float2 anchor) => { anchor = new float2(6f, 4f); return true; },
                    new Vector3(0f, 30f, -18f), 60f, startInStrategy: false);
                ScreenShake.Reset();
                for (int i = 0; i < frames; i++)
                {
                    if (i < shakeFrames)
                    {
                        ScreenShake.AddTrauma(1f);
                    }
                    else
                    {
                        ScreenShake.Reset();
                    }
                    director.Tick(false);
                }
                Vector3 result = cam.transform.position;
                director.Unbind();
                return result;
            }
            finally
            {
                ScreenShake.Reset();
                Object.DestroyImmediate(cam.gameObject);
            }
        }

        // ── 降低闪光 ───────────────────────────────────────────────────

        private static void CheckFlashReduction()
        {
            PropertyInfo prop = typeof(WhiteboxCombatFeedback).GetProperty("FlashScale", BindingFlags.NonPublic | BindingFlags.Static);
            if (prop == null)
            {
                Fail("WhiteboxCombatFeedback.FlashScale 不存在（闪光强度不再受设置控制？）");
                return;
            }
            GameSettings.SetFlashReductionEnabled(false);
            float normal = (float)prop.GetValue(null);
            GameSettings.SetFlashReductionEnabled(true);
            float reduced = (float)prop.GetValue(null);
            Expect(Mathf.Approximately(normal, 1f) && reduced > 0f && reduced <= 0.35f,
                $"降低闪光 → 全屏受伤红闪与地面命中闪光强度 {normal:F2} → {reduced:F2}（仍保留可辨认的受击提示）");
        }

        // ── 报告 ────────────────────────────────────────────────────────

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
