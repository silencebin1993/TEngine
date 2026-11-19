// PL-QA-01 → production/design/presentation-track/handoff/PL-QA-01.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-QA-01 Play 路线冒烟：主菜单 → 新游戏 → 家园（复用 JourneyCommon 的正式入口步骤，用自己的小宿主跑，不进 FG 的旅程目录），
    /// 进家园后把各 UIDocument 的 PanelSettings.targetTexture 临时指到 RT（采完还原），游戏镜头另渲一张，合成出 1 张含 UI Toolkit HUD 的 1080p 图。
    /// 跑法：<c>cd /d/BinGames-PL &amp;&amp; PL_CAPTURE_OUT=… bash tools/pl/pl-unity.sh gpuplay GameLogic.EditorTools.PresentationPlayCapture.PlayHomeMenu play</c>
    /// （方法结束时自己 EditorApplication.Exit：0 = HUD 非空像素 ≥ 1%、1 = 失败、3 = 不是 RTX 4070）。
    /// </summary>
    [InitializeOnLoad]
    public static class PresentationPlayCapture
    {
        private const string Prefix = "BinGames.PLCapture.";
        public const string Id = "PL-CAPTURE-PLAY";
        public const float MinHudRatio = 0.01f;
        private static JourneyRunner _runner;

        static PresentationPlayCapture()
        {
            if (SessionState.GetString(Prefix + "active", "0") == "1")
            {
                _runner = new JourneyRunner(Build(), new SessionJourneyStore(Prefix), new EditorJourneyClock()); // 进 Play 重载域后续上
                Hook();
            }
        }

        public static void PlayHomeMenu()
        {
            string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(PresentationCapture.ReviewRoot(), "_capture", "play-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            Directory.CreateDirectory(outDir);
            SessionState.SetString(Prefix + "out", outDir);
            if (!PresentationCapture.DeviceOk(out string device))
            {
                PresentationCapture.Run run = PresentationCapture.NewRun();
                PresentationCapture.WriteRun(run, outDir);
                Debug.LogError($"[PL 采集] Play 路线：{run.invalidReason}；结论：采集无效（退出码 3）");
                Exit(3);
                return;
            }
            _runner = new JourneyRunner(Build(), new SessionJourneyStore(Prefix), new EditorJourneyClock())
            {
                ReportPath = Path.Combine(outDir, "play-journey.txt"),
            };
            _runner.Start();
            Hook();
        }

        private static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "PL 采集 Play 路线冒烟（主菜单 → 新游戏 → 家园 → 含 HUD 截图）",
            Seed = PresentationCapture.DefaultSeed,
            TotalTimeoutSeconds = 900,
            FailOnErrorLog = false, // 冒烟只看能否出图；主线的报错日志由 FG 旅程负责
            Steps =
            {
                JourneyCommon.S("enter", "开 main.unity 进 Play", 180, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, PresentationCapture.DefaultSeed)),
                JourneyCommon.S("menu", "主菜单点“新建”", 120, null, JourneyCommon.TickMenuNew, 2),
                JourneyCommon.S("newgame", "新游戏设置 → 进家园", 300, null, JourneyCommon.TickNewGame, 2),
                JourneyCommon.S("shot", "拍 1 张含 UI Toolkit HUD 的图", 120, null, TickShot),
            },
        };

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_runner == null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (EditorApplication.isPlaying)
            {
                JourneyInput.KeepScripted();
                JourneyInput.WatchClickFrames();
            }
            if (_runner.Tick())
            {
                return;
            }
            bool pass = _runner.Result == JourneyRunner.ResultPass;
            EditorApplication.update -= Tick;
            Debug.Log($"[PL 采集] Play 路线 {_runner.Result}：{(pass ? "完成" : _runner.FailStepId + " " + _runner.FailReason)}；报告 {_runner.ReportPath}；结论：{(pass ? "出图" : "失败")}");
            _runner = null;
            Exit(pass ? 0 : 1);
        }

        private static void Exit(int code)
        {
            RestorePanels(); // 超时 / 失败没走到第 3 帧时也还原
            SessionState.SetString(Prefix + "active", "0");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        /// <summary>第 1 帧把 PanelSettings 指到 RT，第 3 帧起读回（UI Toolkit 在播放循环里重绘进 RT），合成后还原。</summary>
        private static StepOutcome TickShot(JourneyContext c)
        {
            int phase = c.GetInt("phase");
            c.SetInt("phase", phase + 1);
            if (phase == 0)
            {
                foreach (PanelSettings ps in Panels())
                {
                    if (!Saved.ContainsKey(ps))
                    {
                        Saved[ps] = (ps.targetTexture, ps.clearColor, ps.colorClearValue);
                    }
                    ps.targetTexture = new RenderTexture(PresentationCapture.Width, PresentationCapture.Height, 24, RenderTextureFormat.ARGB32) { name = "PL capture UI " + ps.name };
                    ps.clearColor = true;
                    ps.colorClearValue = Color.clear;
                }
                return StepOutcome.Wait;
            }
            if (phase < 3)
            {
                return StepOutcome.Wait;
            }
            string outDir = SessionState.GetString(Prefix + "out", ".");
            PanelSettings[] panels = Panels().Where(p => p.targetTexture != null).OrderBy(p => p.sortingOrder).ToArray();
            Camera cam = Camera.main;
            var world = new RenderTexture(PresentationCapture.Width, PresentationCapture.Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var tex = new Texture2D(PresentationCapture.Width, PresentationCapture.Height, TextureFormat.RGBA32, false);
            try
            {
                if (cam == null || panels.Length == 0)
                {
                    return StepOutcome.Fail($"进家园后没有游戏镜头（{cam != null}）或没有 UI Toolkit 面板（{panels.Length}）");
                }
                cam.aspect = (float)PresentationCapture.Width / PresentationCapture.Height;
                cam.targetTexture = world;
                cam.Render();
                cam.targetTexture = null;
                cam.ResetAspect();
                Color32[] img = Read(world, tex);
                var ui = new Color32[img.Length];
                foreach (PanelSettings p in panels)
                {
                    Color32[] layer = Read(p.targetTexture, tex);
                    for (int i = 0; i < img.Length; i++)
                    {
                        img[i] = Over(layer[i], img[i]);
                        ui[i] = Over(layer[i], ui[i]);
                    }
                }
                int hud = ui.Count(px => px.a > 8);
                float ratio = hud / (float)ui.Length;
                tex.SetPixels32(img);
                File.WriteAllBytes(Path.Combine(outDir, "PlayHomeMenu_on.png"), tex.EncodeToPNG());
                tex.SetPixels32(ui);
                File.WriteAllBytes(Path.Combine(outDir, "PlayHomeMenu_on_ui.png"), tex.EncodeToPNG());
                PresentationCapture.Run run = PresentationCapture.NewRun();
                run.shots.Add(new PresentationCapture.Shot
                {
                    scene = "PlayHomeMenu", state = "on", feature = "-", inject = "None", file = "PlayHomeMenu_on.png", seed = PresentationCapture.DefaultSeed,
                    orthographic = cam.orthographic, orthographicSize = cam.orthographicSize, fieldOfView = cam.fieldOfView,
                    cameraPosition = cam.transform.position, cameraEuler = cam.transform.eulerAngles, maskPixels = hud,
                });
                PresentationCapture.WriteRun(run, outDir);
                string names = string.Join(", ", panels.Select(p => p.name));
                return ratio >= MinHudRatio
                    ? StepOutcome.Done($"含 HUD 的图 {outDir}/PlayHomeMenu_on.png：UI Toolkit 面板 {panels.Length} 个（{names}），HUD 非空像素 {ratio:P2} ≥ {MinHudRatio:P0}")
                    : StepOutcome.Fail($"HUD 非空像素 {ratio:P2} < {MinHudRatio:P0}（面板 {names}）");
            }
            finally
            {
                RestorePanels();
                world.Release();
                Object.Destroy(world);
                Object.Destroy(tex);
            }
        }

        /// <summary>第 0 帧前 PanelSettings 资源的原值（targetTexture / clearColor / colorClearValue）：采完、失败、超时都按它还原（共享资源，别留脏值）。</summary>
        private static readonly Dictionary<PanelSettings, (RenderTexture rt, bool clear, Color color)> Saved = new Dictionary<PanelSettings, (RenderTexture, bool, Color)>();

        private static void RestorePanels()
        {
            foreach (KeyValuePair<PanelSettings, (RenderTexture rt, bool clear, Color color)> kv in Saved)
            {
                PanelSettings p = kv.Key;
                if (p == null)
                {
                    continue;
                }
                RenderTexture ours = p.targetTexture;
                p.targetTexture = kv.Value.rt;
                p.clearColor = kv.Value.clear;
                p.colorClearValue = kv.Value.color;
                if (ours != null && ours != kv.Value.rt)
                {
                    ours.Release();
                    Object.Destroy(ours);
                }
            }
            Saved.Clear();
        }

        private static PanelSettings[] Panels() =>
            Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Select(d => d.panelSettings).Where(p => p != null).Distinct().ToArray();

        private static Color32[] Read(RenderTexture rt, Texture2D tex)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex.GetPixels32();
        }

        private static Color32 Over(Color32 top, Color32 under)
        {
            if (top.a == 0)
            {
                return under;
            }
            float a = top.a / 255f; // UI Toolkit 输出是预乘 alpha
            return new Color32((byte)Mathf.Min(255, top.r + under.r * (1 - a)), (byte)Mathf.Min(255, top.g + under.g * (1 - a)),
                (byte)Mathf.Min(255, top.b + under.b * (1 - a)), (byte)Mathf.Min(255, top.a + under.a * (1 - a)));
        }
    }
}
