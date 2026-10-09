using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GameLogic.Core;
using GameLogic.UI.Kit;
using GameLogic.UI.PrimitiveCraft;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace BinGames.EditorTools
{
    public static class UiDockLayoutSelfCheck
    {
        private const string SignalPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const string CraftPath = "Assets/GameRes/Raw/UI/PrimitiveCraft/PrimitiveCraftPanel.uxml";
        private static readonly Vector2Int[] Resolutions =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1280, 720),
            new Vector2Int(2560, 1080), new Vector2Int(1280, 1024),
            new Vector2Int(815, 650), new Vector2Int(858, 650), new Vector2Int(885, 793),
        };

        public static void RunPlaySmokeBatch()
        {
            SessionState.SetBool("BinGames.DockCapture.Active", true);
            ResumeCapture();
            GameLogic.EditorTools.PlaySmokeTest.Run();
        }

        [InitializeOnLoadMethod]
        private static void ResumeCapture()
        {
            if (SessionState.GetBool("BinGames.DockCapture.Active", false))
            {
                EditorApplication.update -= CaptureRuntimeHud;
                EditorApplication.update += CaptureRuntimeHud;
            }
            if (SessionState.GetBool("BinGames.CraftVisual.Active", false))
            {
                EditorApplication.update -= TickCraftVisual;
                EditorApplication.update += TickCraftVisual;
            }
        }

        public static void RunCraftVisualBatch()
        {
            if (RunLayoutChecks() != 0)
            {
                EditorApplication.Exit(1);
                return;
            }
            SessionState.SetBool("BinGames.CraftVisual.Active", true);
            SessionState.SetInt("BinGames.CraftVisual.Stage", 0);
            SessionState.SetFloat("BinGames.CraftVisual.Start", (float)EditorApplication.timeSinceStartup);
            ResumeCapture();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/main.unity");
            EditorApplication.EnterPlaymode();
        }

        private static void TickCraftVisual()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup - SessionState.GetFloat("BinGames.CraftVisual.Start", 0) < 90, "合成台运行时校验超时");
                if (!Application.isPlaying) return;
                int stage = SessionState.GetInt("BinGames.CraftVisual.Stage", 0);
                if (stage == 0)
                {
                    var menu = typeof(GameLogic.UIModule).GetMethod("GetWindow", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(global::GameModule.UI, new object[] { typeof(GameLogic.MainMenuUI).FullName }) as GameLogic.MainMenuUI;
                    if (menu == null) return;
                    GameLogic.Campaign.CampaignSaveService.SaveDirectoryOverrideForTests = Path.Combine(Application.dataPath, "../Temp/UiDockVisualSaves");
                    var settings = GameLogic.Campaign.WorldGen.WorldSettings.Resolve(GameLogic.Campaign.WorldGen.WorldGenVersions.Current, "R2O1P1D1S0");
                    typeof(GameLogic.MainMenuUI).GetMethod("CreateNewCampaign", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(menu, new object[] { 0, 424242, settings });
                    SessionState.SetInt("BinGames.CraftVisual.Stage", 1);
                    return;
                }
                var controller = Object.FindAnyObjectByType<PrimitiveCraftPanelUIToolkit>();
                VisualElement root = controller?.GetComponent<UIDocument>()?.rootVisualElement;
                if (root?.Q<Button>("EntryToggleButton") == null || GameLogic.Stage.GameRoot.HomeValley?.IsActive != true) return;
                if (stage == 1)
                {
                    CaptureGameView(Path.Combine(Environment.GetEnvironmentVariable("BINGAMES_DOCK_SHOTS"), "信号操作栏.png"));
                    Click(root.Q<Button>("EntryToggleButton"));
                    SessionState.SetInt("BinGames.CraftVisual.Stage", 2);
                    SessionState.SetFloat("BinGames.CraftVisual.CaptureAt", (float)EditorApplication.timeSinceStartup + 0.5f);
                    return;
                }
                if (stage == 2)
                {
                    if (EditorApplication.timeSinceStartup < SessionState.GetFloat("BinGames.CraftVisual.CaptureAt", 0)) return;
                    Require(GameLogic.Stage.GameRoot.HomeValley.IsCraftStationPanelOpen && InputRouter.IsModalOwner(controller)
                        && ReferenceEquals(UiEscapeStack.CurrentPage, controller), "真实入口未打开合成台或未取得页面所有权");
                    Require(GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.Instance.Root.Q("SignalHudStack").resolvedStyle.display == DisplayStyle.None,
                        "合成台打开后信号 HUD 仍显示");
                    Rect craft = root.Q("CraftPanelRoot").worldBound;
                    Rect clock = WorldBarHudUIToolkit.Instance.Root.Q("WorldBar").worldBound;
                    Rect notifications = NotificationHudUIToolkit.Instance.Root.Q("ToastColumn").worldBound;
                    Require(!craft.Overlaps(clock) && !craft.Overlaps(notifications), "运行时合成台遮挡资源或通知区");
                    CaptureGameView(Path.Combine(Environment.GetEnvironmentVariable("BINGAMES_DOCK_SHOTS"), "合成台.png"));
                    SessionState.SetInt("BinGames.CraftVisual.Stage", 3);
                    return;
                }
                if (stage == 3)
                {
                    Click(root.Q<Button>("CloseButton"));
                    Require(!GameLogic.Stage.GameRoot.HomeValley.IsCraftStationPanelOpen && !InputRouter.IsModalOwner(controller), "关闭按钮未释放输入");
                    Click(root.Q<Button>("EntryToggleButton"));
                    Object.DestroyImmediate(controller.gameObject);
                    Require(!InputRouter.IsModalOwner(controller) && !UiEscapeStack.Contains(controller)
                        && !GameLogic.Stage.GameRoot.HomeValley.IsCraftStationPanelOpen, "运行时销毁后仍占用输入或保留打开状态");
                    FinishCraftVisual("PASS：真实新建入口、合成台按钮打开、资源与通知区不重叠、HUD 收起、关闭与运行时销毁释放输入", 0);
                }
            }
            catch (Exception ex) { FinishCraftVisual("FAIL：" + ex, 1); }
        }

        private static void Click(Button button)
        {
            Require(button != null && button.enabledInHierarchy, "按钮不可操作");
            typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(button.clickable, new object[] { null });
        }

        private static void FinishCraftVisual(string result, int code)
        {
            SessionState.SetBool("BinGames.CraftVisual.Active", false);
            EditorApplication.update -= TickCraftVisual;
            File.WriteAllText(Environment.GetEnvironmentVariable("BINGAMES_CRAFT_OUT"), result);
            EditorApplication.Exit(code);
        }

        private static void CaptureRuntimeHud()
        {
            if (!Application.isPlaying || UiEscapeStack.CurrentPage != null || InputRouter.PanelModalOpen
                || GameLogic.Campaign.Regions.HomeValleyBuildMode.Current?.IsOpen == true) return;
            var hud = GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            if (hud == null || !hud.IsReady || !hud.HudVisible) return;
            string name = hud.UplinkHud.Visible ? "接入视角" : "信号操作栏";
            string key = "BinGames.DockCapture." + name;
            if (SessionState.GetBool(key, false)) return;
            string directory = Environment.GetEnvironmentVariable("BINGAMES_DOCK_SHOTS");
            if (string.IsNullOrEmpty(directory)) return;
            SessionState.SetBool(key, true);
            CaptureGameView(Path.Combine(directory, name + ".png"));
        }

        // 真实运行中的相机和 UI Toolkit 面板共同渲染到离屏纹理；不需要打开 Game 窗口。
        // 只临时改变本验证进程的渲染目标，完成后立即恢复。
        private static void CaptureGameView(string path)
        {
            const int width = 1280;
            const int height = 720;
            var target = new RenderTexture(width, height, 24);
            var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            var panels = new Dictionary<PanelSettings, RenderTexture>();
            Camera camera = Camera.main;
            Require(camera != null, "运行截图找不到主相机");
            RenderTexture previousCameraTarget = camera.targetTexture;
            try
            {
                target.Create();
                camera.targetTexture = target;
                camera.Render();
                foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    PanelSettings settings = doc.panelSettings;
                    if (settings == null || panels.ContainsKey(settings)) continue;
                    panels.Add(settings, settings.targetTexture);
                    settings.targetTexture = target;
                }
                MethodInfo applySettings = typeof(PanelSettings).GetMethod("ApplyPanelSettings", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (PanelSettings settings in panels.Keys) applySettings.Invoke(settings, null);
                foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                    UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
                Type runtime = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
                runtime.GetMethod("RepaintPanels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { true });
                var hud = GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.Instance;
                var ledger = GameLogic.UI.Common.EconomyHudToolkit.Instance;
                if (hud?.HudVisible == true && ledger != null)
                {
                    var ledgerPanel = typeof(GameLogic.UI.Common.EconomyHudToolkit)
                        .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ledger) as VisualElement;
                    if (ledgerPanel?.resolvedStyle.display == DisplayStyle.Flex)
                        Require(!hud.Root.Q("SignalHudBar").worldBound.Overlaps(ledgerPanel.worldBound), "信号操作栏遮挡资源账本入口");
                }
                runtime.GetMethod("RenderOffscreenPanels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Invoke(null, null);
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousCameraTarget;
                foreach (var pair in panels)
                {
                    pair.Key.targetTexture = pair.Value;
                    typeof(PanelSettings).GetMethod("ApplyPanelSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pair.Key, null);
                }
                foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                    UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
                RenderTexture.active = previous;
                Object.DestroyImmediate(pixels);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunLayoutChecks());
        }

        private static int RunLayoutChecks()
        {
            var report = new StringBuilder();
            int failures = 0;
            try
            {
                foreach (float scale in new[] { 0.8f, 1f, 1.4f, 1.5f })
                {
                    foreach (bool pilot in new[] { false, true })
                    {
                        string result = UiToolkitLayoutProbe.Probe(SignalPath, "SignalHudStack", false,
                            prepare: target => PrepareSignal(target, pilot), uiScale: scale, resolutions: Resolutions);
                        report.AppendLine($"信号 HUD / 接入={pilot} / 缩放={scale}: {result}");
                        if (!result.StartsWith("PASS", StringComparison.Ordinal)) failures++;
                    }
                    string craft = UiToolkitLayoutProbe.Probe(CraftPath, "CraftPanelRoot", true,
                        prepare: PrepareCraft, uiScale: scale, resolutions: Resolutions);
                    report.AppendLine($"合成台 / 缩放={scale}: {craft}");
                    if (!craft.StartsWith("PASS", StringComparison.Ordinal)) failures++;
                    foreach (Vector2Int resolution in Resolutions) CheckDocking(resolution, scale, report);
                }
                CheckPageLifecycle();
                report.AppendLine("PASS 合成台互斥、子页返回、Esc 关闭与销毁后的输入释放");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine(ex.ToString());
            }
            report.Insert(0, failures == 0 ? "PASS\n" : $"FAIL {failures}\n");
            string output = Environment.GetEnvironmentVariable("BINGAMES_DOCK_OUT");
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, report.ToString());
            Debug.Log(report.ToString());
            return failures == 0 ? 0 : 1;
        }

        private static void PrepareSignal(VisualElement root, bool pilot)
        {
            root.Query<VisualElement>().ForEach(e => e.RemoveFromClassList("uk-hidden"));
            root.Q<Label>("SignalLocation").text = "信号：ERC-001 #1";
            root.Q<Button>("SignalCoreEntry").text = "信号核 0/2";
            root.Q<Button>("SignalExposureEntry").text = "暴露 0";
            root.Q<Button>("SignalJumpHome").text = "跳回家园";
            root.Q<Button>("SignalJumpPrev").text = "上一台";
            root.Q<Button>("SignalCoverageToggle").text = "覆盖网络：开";
            root.Q<Label>("SignalUplinkStatus").text = "此机器没有接入口：可以驾驶、瞄准和攻击，但不插入固件";
            root.Q("UplinkHud").style.display = pilot ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Label>("UplinkHudTitle").text = "ERC-001 #1 · 搬运轮式";
            root.Q<Label>("UplinkHudMorph").text = "机身：常态";
            root.Q<Button>("UplinkHudHelp").text = "?";
            root.Q<Label>("UplinkHudNote").text = "没有接入口：可以驾驶、瞄准和攻击，不插入固件";
            root.Q<Label>("UplinkHudHeatText").text = "热量 0/100";
            root.Q<Label>("UplinkHudBatteryText").text = "电池 100%";
            root.Q<Label>("UplinkHudHealthText").text = "耐久 100/100";
            root.Q<Label>("UplinkHudLinkText").text = "链路 100% · 强";
            root.Q<Label>("UplinkHudInjury").text = "伤势：无";
            root.Q<Label>("UplinkHudExposure").text = "暴露 0";
            root.Q<Label>("UplinkHudExperience").text = "与信号同行 7 次 · 累计 0:45";
            root.Q<Label>("UplinkHudKeys").text = "V 离开 · Tab 切换机器 · H 跳回家园 · P 信号核";
        }

        private static void CheckDocking(Vector2Int resolution, float scale, StringBuilder report)
        {
            PanelSettings settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            var texture = new RenderTexture(resolution.x, resolution.y, 0);
            settings.targetTexture = texture;
            settings.scale = scale;
            var host = new GameObject("UiDockLayoutSelfCheck");
            try
            {
                VisualElement Add(string path)
                {
                    var child = new GameObject(path);
                    child.transform.SetParent(host.transform);
                    var doc = child.AddComponent<UIDocument>();
                    doc.panelSettings = settings;
                    doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                    return doc.rootVisualElement;
                }
                VisualElement signal = Add(SignalPath);
                PrepareSignal(signal.Q("SignalHudStack"), true);
                VisualElement craft = Add(CraftPath);
                craft.Q("CraftPanelRoot").RemoveFromClassList("craft-hidden");
                VisualElement world = Add("Assets/GameRes/Raw/UI/UiKit/WorldBar.uxml");
                world.Q("WorldBar").RemoveFromClassList("uk-hidden");
                world.Q<Label>("WorldDayTime").text = "第 1 日 14:30";
                world.Q<Label>("WorldStatus").text = "运行中";
                world.Q<Button>("WorldPause").text = "暂停";
                world.Q<Button>("WorldLabor").text = "劳动力 2 | 忙碌 0（0%）";
                world.Q<Label>("WorldFocusTitle").text = "关注点";
                foreach (string text in new[] { "废料 120（0/分）", "+ 固定物品", "电网 1：100/65", "技术数据 370", "研究点 0", "暴露 0/100" })
                {
                    var chip = new Button { text = text };
                    chip.AddToClassList("mw-btn");
                    chip.AddToClassList("wb-res-chip");
                    world.Q("WorldResourceRow").Add(chip);
                }
                VisualElement notify = Add("Assets/GameRes/Raw/UI/UiKit/NotificationHud.uxml");
                notify.Q("ToastColumn").RemoveFromClassList("uk-hidden");
                foreach (VisualElement tree in new[] { signal, craft, world, notify }) UiToolkitLayoutProbe.ForceLayout(tree);
                Rect bar = signal.Q("SignalHudBar").worldBound;
                Rect jump = signal.Q("SignalJumpBar").worldBound;
                Rect status = signal.Q("SignalUplinkStatus").worldBound;
                Rect pilot = signal.Q("UplinkHud").worldBound;
                Rect work = craft.Q("CraftPanelRoot").worldBound;
                Rect clock = world.Q("WorldBar").worldBound;
                Rect notifications = notify.Q("ToastColumn").worldBound;
                Require(Mathf.Abs(bar.xMin - jump.xMin) < 1 && Mathf.Abs(bar.xMin - status.xMin) < 1 && Mathf.Abs(bar.xMin - pilot.xMin) < 1,
                    $"信号 HUD 左边缘未对齐：操作栏 {bar.xMin} / 跳转 {jump.xMin} / 状态 {status.xMin} / 接入 {pilot.xMin}");
                Require(status.yMax < pilot.yMin, "接入 HUD 与顶部信号操作条重叠");
                Require(!bar.Overlaps(clock) && !jump.Overlaps(clock) && !pilot.Overlaps(notifications), "信号 HUD 遮挡右侧常驻 UI");
                Require(!work.Overlaps(clock) && !work.Overlaps(notifications), "合成台遮挡资源或通知区");
                Require(craft.Q("Body") is ScrollView && work.Contains(craft.Q("CloseButton").worldBound.center), "合成台滚动正文或关闭入口失效");
                report.AppendLine($"PASS {resolution.x}×{resolution.y} / {scale}：对齐、边缘停靠、区域互不遮挡");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
                texture.Release();
                Object.DestroyImmediate(texture);
            }
        }

        private static void PrepareCraft(VisualElement root)
        {
            var template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/PrimitiveCraft/templates/CraftQueueRow.uxml");
            for (int i = 0; i < 9; i++)
            {
                VisualElement row = template.CloneTree();
                row.Q<Label>("Kind").text = "升级";
                row.Q<Label>("State").text = i == 0 ? "进行中" : "排队中";
                row.Q<Label>("Progress").text = "8.0/8.0秒";
                row.Q<Label>("Reason").text = "等待前一项任务完成";
                root.Q<ScrollView>("QueueList").Add(row);
            }
        }

        private static void CheckPageLifecycle()
        {
            var host = new GameObject("CraftPageLifecycle");
            var panel = new VisualElement();
            var previous = new object();
            var child = new object();
            bool previousClosed = false;
            try
            {
                var controller = host.AddComponent<PrimitiveCraftPanelUIToolkit>();
                typeof(PrimitiveCraftPanelUIToolkit).GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, panel);
                UiEscapeStack.RegisterPage(controller, panel);
                UiEscapeStack.RegisterPage(previous, new VisualElement());
                UiEscapeStack.Push(previous, () => previousClosed = true);
                MethodInfo sync = typeof(PrimitiveCraftPanelUIToolkit).GetMethod("SyncPanelOpen", BindingFlags.Instance | BindingFlags.NonPublic);
                sync.Invoke(controller, new object[] { true });
                Require(previousClosed && ReferenceEquals(UiEscapeStack.CurrentPage, controller) && InputRouter.IsModalOwner(controller), "合成台未关闭同级页或未取得输入占用");
                UiEscapeStack.RegisterPage(child, new VisualElement());
                UiEscapeStack.OpenChild(controller, () => UiEscapeStack.Push(child, () => UiEscapeStack.Remove(child)));
                Require(!panel.visible, "打开子页后合成台仍显示");
                UiEscapeStack.CloseTop();
                Require(panel.visible && ReferenceEquals(UiEscapeStack.CurrentPage, controller), "关闭子页后合成台未恢复");
                UiEscapeStack.CloseTop();
                Require(!InputRouter.IsModalOwner(controller) && !UiEscapeStack.Contains(controller), "Esc 关闭后输入占用残留");
                sync.Invoke(controller, new object[] { true });
                // 编辑模式下未进入生命周期的 MonoBehaviour 不会收到 Unity 的 OnDestroy。
                typeof(PrimitiveCraftPanelUIToolkit).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                Object.DestroyImmediate(host);
                Require(!InputRouter.IsModalOwner(controller) && !UiEscapeStack.Contains(controller), "销毁合成台后输入占用残留");
            }
            finally
            {
                UiEscapeStack.UnregisterPage(previous);
                UiEscapeStack.UnregisterPage(child);
                if (host != null) Object.DestroyImmediate(host);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
