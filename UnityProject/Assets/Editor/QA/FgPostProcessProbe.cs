using System.IO;
using System.Text;
using GameLogic.Settings;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-PL-01（承接 DEBT-FG15UX01-01）GPU 探针：设置“画面 → 后处理”开 / 关后，真实 URP 渲染出来的画面有别。
    /// 场景：中灰背景正中一块 HDR 自发光小方块；开 = 方块外一圈被 Bloom 照亮、四角被暗角压暗，关 = 两处都与背景相同。
    /// 反例（画面坏了必须失败）：关着时测到的“光晕增量 / 暗角压暗”都必须≈0——否则说明测量本身不分开 / 关，开着的断言就没有检测力。
    /// 第二段（修复轮 2）：世界相机 + UIRoot 的真实 UI 相机叠加（见 <see cref="UiStack"/>）。
    /// 跑法：PROBE_ONLY=postfx bash tools/unity-gpu-probes.sh（不带 -nographics；没有图形设备退出码 2）。
    /// </summary>
    public static class FgPostProcessProbe
    {
        private const int Size = 256;
        private static readonly Color Background = new Color(0.5f, 0.5f, 0.5f, 1f);

        [MenuItem("QA/自检/渲染/后处理开关 GPU 画面探针")]
        public static void Run()
        {
            var report = new StringBuilder("[后处理开关 GPU 画面] 设置 → 画面 → 后处理（FG15-PL-01，DEBT-FG15UX01-01）\n");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                report.AppendLine((ok ? "  ✓ " : "  ✗ ") + msg);
                fail += ok ? 0 : 1;
            }

            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），管线 {(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "Built-in")}");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                report.AppendLine("  · 没有图形设备（-nographics），跳过");
                Finish(report, 2);
                return;
            }
            Expect(PostProcessView.UrpActive, "当前管线是 URP（ProjectSettings 没被还原成 Built-in）");

            var camGo = new GameObject("FgPostProcessProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.hideFlags = HideFlags.HideAndDontSave;
            // HDR 目标：正式游戏渲染到屏幕时 URP 用 HDR 中间缓冲（Bloom 才吃得到 > 1 的亮度）；渲染到 ARGB32 的相机 URP 会按目标格式降成 LDR，Bloom 阈值 1 以上什么都没有
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.DefaultHDR);
            Material mat = null;
            System.Action<bool> savedSink = GameSettings.PostProcessSink;
            try
            {
                PostProcessView.ResetForTests();
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = 1f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Background;
                cam.allowHDR = true;
                cam.transform.position = new Vector3(0f, 0f, -5f);
                cam.targetTexture = rt;
                quadGo.transform.localScale = new Vector3(0.2f, 0.2f, 1f);
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.SetColor("_BaseColor", new Color(8f, 8f, 8f, 1f));
                quadGo.GetComponent<MeshRenderer>().sharedMaterial = mat;

                // 走设置的真实路径：开关 → GameSettings.ApplyQuality → PostProcessSink（默认 = PostProcessView.Apply）→ 渲染时 beginContextRendering 对齐相机（探针相机是停用的手动渲染相机，不在 Camera.allCameras 里）
                GameSettings.PostProcessSink = PostProcessView.Apply;
                GameSettings.SetPostProcessEnabled(true);
                Texture2D on = Shot(cam, rt);
                bool camOn = cam.GetUniversalAdditionalCameraData().renderPostProcessing;
                GameSettings.SetPostProcessEnabled(false);
                Texture2D off = Shot(cam, rt);
                bool camOff = cam.GetUniversalAdditionalCameraData().renderPostProcessing;
                // 暗角单独量：去掉自发光方块再拍一对（方块的 Bloom 在 256 像素小图上会散到四角，盖过暗角）
                quadGo.SetActive(false);
                GameSettings.SetPostProcessEnabled(true);
                Texture2D onBg = Shot(cam, rt);
                GameSettings.SetPostProcessEnabled(false);
                Texture2D offBg = Shot(cam, rt);
                quadGo.SetActive(true);

                float ringOn = Ring(on), ringOff = Ring(off), cornerOn = Corner(onBg), cornerOff = Corner(offBg), bg = Lum(QualitySettings.activeColorSpace == ColorSpace.Linear ? Background.linear : Background);
                float centerOff = Lum(off.GetPixel(Size / 2, Size / 2));
                Bloom stackBloom = VolumeManager.instance.stack.GetComponent<Bloom>();
                report.AppendLine($"  · 体积栈：Volume 启用 {PostProcessView.Volume != null && PostProcessView.Volume.enabled}、默认栈 Bloom 强度 {(stackBloom != null ? stackBloom.intensity.value : -1f):0.00}（最后一次渲染后，关）");
                report.AppendLine($"  · 读回（HDR 线性亮度）：光晕环 开 {ringOn:0.000} / 关 {ringOff:0.000}；四角（无方块）开 {cornerOn:0.000} / 关 {cornerOff:0.000}；背景 {bg:0.000}；关时方块中心 {centerOff:0.000}");
                Expect(camOn && !camOff, $"开 / 关 → 渲染时相机 renderPostProcessing 跟着设置（{camOn}/{camOff}）");
                Expect(centerOff > 0.9f, $"对照：方块本身画出来了（关时中心亮度 {centerOff:0.000} > 0.9）");
                Expect(ringOn > ringOff + 0.02f, $"开后处理：方块外一圈被 Bloom 照亮（+{ringOn - ringOff:0.000} > 0.02）");
                Expect(cornerOn < cornerOff - 0.02f, $"开后处理：四角被暗角压暗（-{cornerOff - cornerOn:0.000} > 0.02）");
                Expect(Mathf.Abs(ringOff - bg) < 0.01f && Mathf.Abs(cornerOff - bg) < 0.01f,
                    $"反例：关后处理时光晕环 / 四角都等于背景（差 {Mathf.Abs(ringOff - bg):0.000} / {Mathf.Abs(cornerOff - bg):0.000} < 0.01），测量能分辨开关");
                SavePng(on, "on");
                SavePng(off, "off");
                Object.DestroyImmediate(on);
                Object.DestroyImmediate(onBg);
                Object.DestroyImmediate(offBg);
                Object.DestroyImmediate(off);
            }
            catch (System.Exception e)
            {
                Expect(false, "探针异常：" + e);
            }
            finally
            {
                GameSettings.PostProcessSink = savedSink;
                PostProcessView.ResetForTests();
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(quadGo);
                if (mat != null)
                {
                    Object.DestroyImmediate(mat);
                }
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            UiStack(Expect, report);
            Finish(report, fail == 0 ? 0 : 1);
        }

        /// <summary>
        /// 第二段（FG15-PL-01 修复轮 2）：世界相机 + UIRoot 预制体里的真实 UI 相机（Depth only、depth 2、只画 UI 层）画到同一目标，UI 层白方块盖住左下角 1/4。
        /// 反例（画面坏了必须失败）：UI 相机留作第二台基础相机（URP 对它的默认处理）→ 世界画面被清掉，测量必须分得出来。
        /// 修好后（PostProcessView.SyncCameras 把它挂成世界相机的叠加相机）：世界画面保留、UI 画在上面；开后处理时世界四角被暗角压暗，UI 盖住的那一角不变（界面不吃后处理）。
        /// </summary>
        private static void UiStack(System.Action<bool, string> expect, StringBuilder report)
        {
            var hostGo = new GameObject("FgPostProcessProbeWorld") { hideFlags = HideFlags.HideAndDontSave };
            GameObject uiRootAsset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TEngine/Settings/Prefab/UIRoot.prefab");
            GameObject uiRoot = uiRootAsset != null ? (GameObject)PrefabUtility.InstantiatePrefab(uiRootAsset) : null;
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.hideFlags = HideFlags.HideAndDontSave;
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.DefaultHDR);
            Material mat = null;
            System.Action<bool> savedSink = GameSettings.PostProcessSink;
            try
            {
                PostProcessView.ResetForTests();
                GameSettings.PostProcessSink = PostProcessView.Apply;
                Camera ui = uiRoot != null ? uiRoot.GetComponentInChildren<Camera>(true) : null;
                expect(ui != null && PostProcessView.IsUiOnly(ui) && ui.clearFlags == CameraClearFlags.Depth,
                    "第二段：UIRoot 预制体里有只画 UI 层、Depth only 的 UI 相机（游戏里叠在世界上的那台）");
                if (ui == null)
                {
                    return;
                }
                uiRoot.transform.position = new Vector3(1000f, 0f, 0f);
                ui.enabled = true; // 叠加相机 isActiveAndEnabled 才会被基础相机栈画出（URP RenderCameraStack）
                ui.targetTexture = rt; // 与世界相机同一画面目标（游戏里两台都画到屏幕）
                Camera host = hostGo.AddComponent<Camera>();
                host.enabled = false;
                host.orthographic = true;
                host.orthographicSize = 1f;
                host.clearFlags = CameraClearFlags.SolidColor;
                host.backgroundColor = Background;
                host.allowHDR = true;
                host.cullingMask = ~ui.cullingMask;
                host.depth = -1f;
                host.transform.position = new Vector3(0f, 0f, -5f);
                host.targetTexture = rt;
                quad.layer = LayerMask.NameToLayer("UI");
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                mat.SetColor("_BaseColor", Color.white);
                quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
                float h = ui.orthographicSize; // 目标是正方形，半宽 = 半高
                Transform ut = ui.transform;
                quad.transform.SetPositionAndRotation(ut.position + ut.forward * 50f + ut.rotation * new Vector3(-0.75f * h, -0.75f * h, 0f), ut.rotation);
                quad.transform.localScale = new Vector3(0.5f * h, 0.5f * h, 1f);
                UniversalAdditionalCameraData ud = ui.GetUniversalAdditionalCameraData();

                GameSettings.SetPostProcessEnabled(false);
                ud.renderType = CameraRenderType.Base;
                host.Render();
                ui.Render();
                Texture2D naive = Read(rt);
                var both = new System.Collections.Generic.List<Camera> { host, ui };
                PostProcessView.SyncCameras(both);
                bool stacked = ud.renderType == CameraRenderType.Overlay && host.GetUniversalAdditionalCameraData().cameraStack.Contains(ui) && !ud.renderPostProcessing;
                Texture2D off = Shot(host, rt);
                GameSettings.SetPostProcessEnabled(true);
                PostProcessView.SyncCameras(both);
                bool uiPostOff = !ud.renderPostProcessing && host.GetUniversalAdditionalCameraData().renderPostProcessing;
                Texture2D on = Shot(host, rt);

                float bg = Lum(QualitySettings.activeColorSpace == ColorSpace.Linear ? Background.linear : Background);
                System.Func<float, float, bool> center = (dx, dy) => Mathf.Abs(dx) < 0.1f && Mathf.Abs(dy) < 0.1f;
                System.Func<float, float, bool> uiCorner = (dx, dy) => dx < -0.42f && dy < -0.42f;
                System.Func<float, float, bool> worldCorner = (dx, dy) => dx > 0.42f && dy > 0.42f;
                float naiveCenter = Average(naive, center), offCenter = Average(off, center);
                float uiOff = Average(off, uiCorner), uiOn = Average(on, uiCorner), worldOff = Average(off, worldCorner), worldOn = Average(on, worldCorner);
                report.AppendLine($"  · 第二段读回（HDR 线性亮度）：UI 相机留作基础相机时画面中心 {naiveCenter:0.000}；挂成叠加相机后中心 {offCenter:0.000}（背景 {bg:0.000}）；UI 角 关 {uiOff:0.000} / 开 {uiOn:0.000}；世界角 关 {worldOff:0.000} / 开 {worldOn:0.000}");
                expect(Mathf.Abs(naiveCenter - bg) > 0.1f, $"反例：UI 相机留作第二台基础相机 → 世界画面被清掉（中心 {naiveCenter:0.000} 与背景 {bg:0.000} 差 > 0.1），测量分得出“世界被盖掉”");
                expect(stacked && uiPostOff, $"SyncCameras：UI 相机转叠加相机挂进世界相机栈、自己不开后处理，世界相机照常开（{stacked}/{uiPostOff}）");
                expect(Mathf.Abs(offCenter - bg) < 0.01f && uiOff > 0.9f, $"挂成叠加相机后：世界画面保留（中心与背景差 {Mathf.Abs(offCenter - bg):0.000} < 0.01）、UI 画在上面（UI 角 {uiOff:0.000} > 0.9）");
                expect(worldOn < worldOff - 0.02f && Mathf.Abs(uiOn - uiOff) < 0.01f,
                    $"开后处理：世界四角被暗角压暗（-{worldOff - worldOn:0.000} > 0.02），UI 盖住的那一角不变（差 {Mathf.Abs(uiOn - uiOff):0.000} < 0.01，界面不吃后处理）");
                SavePng(naive, "ui-naive");
                SavePng(on, "ui-on");
                Object.DestroyImmediate(naive);
                Object.DestroyImmediate(off);
                Object.DestroyImmediate(on);
                ui.targetTexture = null;
            }
            catch (System.Exception e)
            {
                expect(false, "第二段探针异常：" + e);
            }
            finally
            {
                GameSettings.PostProcessSink = savedSink;
                PostProcessView.ResetForTests();
                Object.DestroyImmediate(hostGo);
                Object.DestroyImmediate(quad);
                if (uiRoot != null)
                {
                    Object.DestroyImmediate(uiRoot);
                }
                if (mat != null)
                {
                    Object.DestroyImmediate(mat);
                }
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static Texture2D Shot(Camera cam, RenderTexture rt)
        {
            cam.Render();
            return Read(rt);
        }

        private static Texture2D Read(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        /// <summary>方块（占画面中心 10%）外 0.08～0.2 画面宽的一圈平均亮度。</summary>
        private static float Ring(Texture2D t) => Average(t, (dx, dy) =>
        {
            float r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
            return r > 0.08f && r < 0.2f;
        });

        /// <summary>四角 8% 见方的平均亮度。</summary>
        private static float Corner(Texture2D t) => Average(t, (dx, dy) => Mathf.Abs(dx) > 0.42f && Mathf.Abs(dy) > 0.42f);

        private static float Average(Texture2D t, System.Func<float, float, bool> inside)
        {
            double sum = 0;
            int n = 0;
            for (int y = 0; y < Size; y += 2)
            {
                for (int x = 0; x < Size; x += 2)
                {
                    if (inside((x + 0.5f) / Size - 0.5f, (y + 0.5f) / Size - 0.5f))
                    {
                        sum += Lum(t.GetPixel(x, y));
                        n++;
                    }
                }
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        private static void SavePng(Texture2D tex, string tag)
        {
            // 从工程目录往上找仓库根的 production/qa/evidence（影子工程与真工程都能找到），找不到不存图
            for (var d = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, ".."))); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "production", "qa", "evidence");
                if (Directory.Exists(p))
                {
                    File.WriteAllBytes(Path.Combine(p, $"_gpu-probe-postfx-{tag}.exr"), tex.EncodeToEXR());
                    return;
                }
            }
        }

        private static void Finish(StringBuilder report, int code)
        {
            report.AppendLine(code == 0 ? "结论：PASS" : code == 2 ? "结论：跳过（无图形设备）" : "结论：FAIL");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
