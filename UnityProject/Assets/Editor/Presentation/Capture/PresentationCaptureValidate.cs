// PL-QA-01 → production/design/presentation-track/handoff/PL-QA-01.md
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BinGames.Presentation;
using UnityEngine;
using UnityEngine.Rendering;
using static GameLogic.EditorTools.PresentationValidate;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-QA-01 自检段（登记在 PresentationValidate.Segments）。
    /// CaptureScenes 不要显卡（validate 里跑）；CaptureDevice / CaptureDeterminism / CaptureInjection / CaptureGpuPair 要显卡，
    /// -nographics 下只写一行“跳过”（不计通过也不计失败），用 gpu 模式点名跑：
    /// <c>cd /d/BinGames-PL &amp;&amp; PL_VALIDATE_SEGMENTS=CaptureDevice,CaptureDeterminism,CaptureInjection,CaptureGpuPair bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.PresentationValidate.RunAll capture-validate</c>。
    /// 设备不是 RTX 4070 时判失败、报告写“采集无效”、RunAll 退出码 3。图像指标与阈值只在 tools/pl/pl_metrics.py 里（本类调它）。
    /// </summary>
    internal static class PresentationCaptureValidate
    {
        private static readonly string[] FirstBatch = { "HomeDefault", "HomeWide", "HomeTarget", "LabSmoke" };

        /// <summary>登记表、标定靶模块（开关 sim 哈希、GameDt 冻结语义）、指标脚本自检。</summary>
        internal static void CaptureScenes()
        {
            var scenes = PresentationCapture.Scenes;
            string[] missing = FirstBatch.Where(n => !scenes.ContainsKey(n)).ToArray();
            Expect(missing.Length == 0, $"首批采集场景已登记（共 {scenes.Count} 个：{string.Join(", ", scenes.Keys)}；缺：{string.Join(", ", missing)}）");
            string[] bad = scenes.Where(kv =>
            {
                ParameterInfo[] ps = kv.Value.GetParameters();
                return !kv.Value.IsStatic || ps.Length != 1 || ps[0].ParameterType != typeof(PresentationCaptureContext);
            }).Select(kv => kv.Key).ToArray();
            Expect(bad.Length == 0, $"场景方法签名都是 static void 名字(PresentationCaptureContext)（不合格：{string.Join(", ", bad)}）");
            PresentationFeature f = PresentationFeatureRegistry.Features.FirstOrDefault(x => x.Id == PresentationFeatureRegistry.CaptureId);
            Expect(f != null && !f.DefaultPL && !f.DefaultMain, "pl.capture 已登记，PL 分支与合主线默认都关（正式游戏不登记标定靶）");

            // 标定靶按 GameDt 动：GameDt 恒 0 冻住（“动画冻结”注入的机制），1/60 时在动
            var t = new PresentationCaptureTarget(new Vector3(100f, 0f, 100f));
            PresentationFeatureRegistry.ResetToDefaults();
            PresentationHost.SetEnabled(true); // 显式开宿主：合主线后 HostId 默认关（同 PresentationClockValidate.RegisterProbe）
            PresentationFeatureRegistry.Set(PresentationFeatureRegistry.CaptureId, true);
            PresentationHost.Register(t);
            try
            {
                PresentationHost.Tick(PresentationFrame.Synthetic(1f / 60f, 0));
                Renderer r = t.Renderers.FirstOrDefault();
                Expect(r != null && t.Renderers.Length == PresentationCaptureTarget.UnitCount * 2, $"标定靶启用后有 {PresentationCaptureTarget.UnitCount} 台车 × 2 个渲染器（实际 {t.Renderers.Length}）");
                if (r != null)
                {
                    Vector3 p0 = r.transform.position;
                    for (int i = 0; i < 10; i++)
                    {
                        PresentationHost.Tick(new PresentationFrame(1f / 60f, 0f, 0, 0, 0f, false, false, null, null));
                    }
                    Vector3 p1 = r.transform.position;
                    PresentationHost.Tick(PresentationFrame.Synthetic(1f / 60f, 1f / 60f));
                    Vector3 p2 = r.transform.position;
                    Expect(p0 == p1 && (p2 - p1).magnitude > 1e-3f, $"GameDt 恒 0 的 10 帧位置不变（{(p1 - p0).magnitude:G3} 米），GameDt = 1/60 时移动 {(p2 - p1).magnitude:F4} 米");
                }
            }
            finally
            {
                PresentationHost.Unregister(t);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
            }

            Expect(PresentationSimHash.CheckOnOff(out string d, new PresentationCaptureTarget(new Vector3(8f, 0f, -4f))), "标定靶开 / 关 sim 哈希一致：" + d);

            if (InFgChain)
            {
                Line("  - 嵌在 FG 链里跳过 pl_metrics.py --selftest（外部 Python 依赖）；单独跑 RunAll 覆盖");
                return;
            }
            int code = PresentationCapture.RunPython("pl_metrics.py", "--selftest", out string o);
            Expect(code == 0, $"python tools/pl/pl_metrics.py --selftest 退出码 {code}：{Tail(o)}");
        }

        /// <summary>图形设备：不是 RTX 4070 → 判失败、“采集无效”、RunAll 退出码 3。</summary>
        internal static void CaptureDevice()
        {
            if (SkipWithoutGpu())
            {
                return;
            }
            bool ok = PresentationCapture.DeviceOk(out string device);
            if (!ok)
            {
                ExitCodeOverride = 3;
                Line("  采集无效：本次图形设备不是 " + PresentationCapture.RequiredGpuName);
            }
            Expect(ok, $"图形设备 {device} 含 “{PresentationCapture.RequiredGpuName}”（{QualitySettings.activeColorSpace}，{(GraphicsSettings.currentRenderPipeline == null ? "Built-in" : GraphicsSettings.currentRenderPipeline.name)}）");
        }

        /// <summary>HomeDefault 连拍两遍（各自从头新开局），SSIM ≥ 0.999；同一帧连渲两遍逐像素相同。</summary>
        internal static void CaptureDeterminism()
        {
            if (SkipWithoutGpu() || !GpuValid())
            {
                return;
            }
            string root = OutDir("determinism");
            var a = PresentationCapture.Capture("HomeDefault", Path.Combine(root, "a"), CaptureInject.None, true)[0];
            var b = PresentationCapture.Capture("HomeDefault", Path.Combine(root, "b"), CaptureInject.None, true)[0];
            Expect(a.error == null && b.error == null, $"两遍采集都成功（{a.error ?? "-"}；{b.error ?? "-"}）");
            if (a.error != null || b.error != null)
            {
                return;
            }
            Expect(a.repeatDiffPixels == 0 && b.repeatDiffPixels == 0, $"同一帧连渲两遍逐像素相同（不同像素 {a.repeatDiffPixels} / {b.repeatDiffPixels}）");
            // PL-INF-07 §0：每个采集帧 Ticks 增量 × StepSeconds = 传给宿主的 GameDt（= 0.05 游戏秒）
            Expect(a.clockMismatchFrames == 0 && b.clockMismatchFrames == 0 && Math.Abs(a.simSecondsPerFrame - PresentationCapture.Dt) < 1e-4,
                $"采集帧时钟：模拟每帧推进 {a.simSecondsPerFrame:F4} 游戏秒 = 宿主 GameDt {PresentationCapture.Dt}，不一致帧 {a.clockMismatchFrames} / {b.clockMismatchFrames}");
            int code = PresentationCapture.RunPython("pl_metrics.py", $"ssim \"{Path.Combine(root, "a", a.file)}\" \"{Path.Combine(root, "b", b.file)}\"", out string o);
            Match m = Regex.Match(o, @"SSIM=([0-9.]+)");
            double ssim = m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
            Expect(code == 0 && ssim >= 0.999, $"HomeDefault 两遍 SSIM {ssim:F5} ≥ 0.999（{Tail(o)}）；正交半高 {a.orthographicSize:F1}，输出 {root}");
        }

        /// <summary>三种注入缺陷分别被指标抓到（HomeTarget 的“开”，标定靶）：品红比 &gt; 0.01%、遮罩内 10 帧帧间差分 = 0、遮罩像素 &lt; 基线 60%；基线本身三项都不报。</summary>
        internal static void CaptureInjection()
        {
            if (SkipWithoutGpu() || !GpuValid())
            {
                return;
            }
            string root = OutDir("injection");
            string[] dirs = new string[4];
            CaptureInject[] kinds = { CaptureInject.None, CaptureInject.Magenta, CaptureInject.Freeze, CaptureInject.Sink };
            for (int i = 0; i < kinds.Length; i++)
            {
                dirs[i] = Path.Combine(root, kinds[i].ToString().ToLowerInvariant());
                var s = PresentationCapture.Capture("HomeTarget", dirs[i], kinds[i], true)[0];
                Expect(s.error == null && s.maskPixels > 0, $"HomeTarget 注入 {kinds[i]}：采集成功，剪影 {s.maskPixels} 像素（{s.error ?? "-"}）");
                WriteSingle(dirs[i], s);
            }
            int code = PresentationCapture.RunPython("pl_metrics.py", "inject-check " + string.Join(" ", dirs.Select(x => $"\"{x}\"")), out string o);
            foreach (string l in o.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0))
            {
                Line("    " + l.Replace("✓", "·").Replace("✗", "×")); // 逐条明细（不重复计数），结论用下面一条断言
            }
            Expect(code == 0, $"pl_metrics.py inject-check：三种注入全被抓到、基线不误报（退出码 {code}，输出 {root}）");
        }

        /// <summary>GPU 毫秒成对测量跑通（标定靶开 / 关，只测一次）：经 PerfGate 记账，超线不到 2 倍记警告。</summary>
        internal static void CaptureGpuPair()
        {
            if (SkipWithoutGpu() || !GpuValid())
            {
                return;
            }
            PresentationCapture.GpuPair p = PresentationCapture.MeasureGpuPair("HomeTarget", PresentationFeatureRegistry.CaptureId);
            bool finite = new[] { p.OffP50, p.OffP95, p.OnP50, p.OnP95 }.All(x => x > 0 && !double.IsNaN(x) && !double.IsInfinity(x));
            PerfGate.Expect(finite, $"HomeTarget 标定靶 GPU 毫秒（预热 30 帧、各测 120 帧，1920×1080 渲染 + 等 GPU）：{p}",
                new[] { PerfGate.Le(p.DeltaP95, 0.5, "pl.capture p95 增量 ms") }, Expect, Line);
        }

        // ── 小工具 ──

        private static void WriteSingle(string dir, PresentationCapture.Shot s)
        {
            PresentationCapture.Run run = PresentationCapture.NewRun();
            run.shots.Add(s);
            PresentationCapture.WriteRun(run, dir);
        }

        private static bool SkipWithoutGpu()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Application.isBatchMode)
            {
                return false;
            }
            Line("  - 跳过：没有图形设备（-nographics）或在交互编辑器里（会换场景）。要显卡：PL_VALIDATE_SEGMENTS=… bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.PresentationValidate.RunAll capture-validate");
            return true;
        }

        private static bool GpuValid()
        {
            if (PresentationCapture.DeviceOk(out string device))
            {
                return true;
            }
            ExitCodeOverride = 3;
            Expect(false, $"采集无效：图形设备 {device} 不是 {PresentationCapture.RequiredGpuName}");
            return false;
        }

        private static string OutDir(string name)
        {
            string d = Path.Combine(PresentationCapture.ReviewRoot(), "PL-QA-01", "validate", name);
            if (Directory.Exists(d))
            {
                Directory.Delete(d, true);
            }
            Directory.CreateDirectory(d);
            return d;
        }

        private static string Tail(string s)
        {
            // 外部输出里的 ✓ / ✗ 换掉、压成一行：unity-validate.sh 按行数 ✓ / ✗，嵌进来会虚增断言数
            s = (s ?? "").Trim().Replace("\r", "").Replace("\n", " | ").Replace("✓", "·").Replace("✗", "×");
            return s.Length > 400 ? "…" + s.Substring(s.Length - 400) : s;
        }
    }
}
