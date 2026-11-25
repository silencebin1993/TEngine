using System.Collections.Generic;
using GameLogic.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GameLogic.View
{
    /// <summary>
    /// 设置“画面 → 后处理”的渲染侧（FG15-PL-01 承接 DEBT-FG15UX01-01）：<see cref="Settings.GameSettings.PostProcessSink"/> 默认指向 <see cref="Apply"/>。
    /// URP 下维护一个全局 Volume（运行时建的档案：Bloom + 暗角，参数在 fg.TbUiTuning 的 settings.post_*），开 / 关 = Volume 启停 +
    /// 每台基础游戏相机的 renderPostProcessing（每帧在 beginContextRendering 里、URP 读相机数据之前对齐，之后才建的相机也跟着设置走）。Built-in 管线下没有后处理栈，什么都不做。
    /// 同一个回调里还把只画 UI 层的相机（UIRoot 的 UICamera：Depth only、depth 2）挂成世界相机的叠加相机（<see cref="StackUiCameras"/>）：
    /// URP 没有 Built-in 的“只清深度”基础相机，它当第二台基础相机会按 Solid Color 把下面的世界画面整屏清掉，后处理也会在 UI 上再叠一遍。
    /// 档案参数是占位观感，正式曝光 / 调色归表现泳道 PL-LOOK-01（DEBT-FG15PL01-03）。
    /// </summary>
    public static class PostProcessView
    {
        private static Volume _volume;
        private static VolumeProfile _profile;
        private static bool _enabled = true;
        private static bool _hooked;

        /// <summary>最近一次 Apply 的值（Built-in 下也记，切回 URP 时按它生效）。</summary>
        public static bool Enabled => _enabled;

        /// <summary>当前管线是 URP（有后处理栈）。</summary>
        public static bool UrpActive => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

        /// <summary>全局后处理 Volume（还没在 URP 下 Apply 过时为 null）。</summary>
        public static Volume Volume => _volume;

        /// <summary>只读档案（自检核对里面有哪些效果）。</summary>
        public static VolumeProfile Profile => _profile;

        public static void Apply(bool enabled)
        {
            _enabled = enabled;
            if (!UrpActive)
            {
                return;
            }
            EnsureVolume();
            _volume.enabled = enabled;
            if (!_hooked)
            {
                RenderPipelineManager.beginContextRendering += OnBeginContext;
                _hooked = true;
            }
            foreach (Camera cam in Camera.allCameras)
            {
                Sync(cam);
            }
        }

        /// <summary>把一台相机的后处理开关对齐到当前设置（只管基础游戏相机；叠加相机跟随基础相机，场景视图不动；只画 UI 层的相机永远不开，界面不吃泛光 / 暗角）。</summary>
        public static void Sync(Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Game || !UrpActive)
            {
                return;
            }
            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            bool want = _enabled && !IsUiOnly(cam);
            if (data != null && data.renderType == CameraRenderType.Base && data.renderPostProcessing != want)
            {
                data.renderPostProcessing = want;
            }
        }

        /// <summary>每帧渲染前对齐一批相机（beginContextRendering 的回调体；自检 / 探针拿手动渲染的相机列表直接调）：先挂 UI 叠加相机，再对齐后处理开关。</summary>
        public static void SyncCameras(List<Camera> cameras)
        {
            if (!UrpActive)
            {
                return;
            }
            StackUiCameras(cameras);
            for (int i = 0; i < cameras.Count; i++)
            {
                Sync(cameras[i]);
            }
        }

        /// <summary>只画 UI 层的游戏相机（UIRoot 的 UICamera，剔除层 = UI）。</summary>
        public static bool IsUiOnly(Camera cam)
        {
            int ui = LayerMask.NameToLayer("UI");
            return ui >= 0 && cam.cullingMask == 1 << ui;
        }

        /// <summary>
        /// 只画 UI 层的游戏相机 → 叠加相机，挂到同一画面目标上 depth 比它小的最上层基础游戏相机栈尾（= Built-in 下按 depth 叠在世界上面的效果），并从别的基础相机栈里摘掉；
        /// 这批里没有这样的基础相机（切场景空档、只有界面）就留作基础相机，界面照常画。O(相机数²)，相机只有个位数。
        /// </summary>
        public static void StackUiCameras(List<Camera> cameras)
        {
            for (int i = 0; i < cameras.Count; i++)
            {
                Camera ui = cameras[i];
                if (ui == null || ui.cameraType != CameraType.Game || !IsUiOnly(ui))
                {
                    continue;
                }
                Camera host = null;
                for (int j = 0; j < cameras.Count; j++)
                {
                    Camera c = cameras[j];
                    if (c != null && c != ui && c.cameraType == CameraType.Game && !IsUiOnly(c) && c.targetTexture == ui.targetTexture && c.depth < ui.depth
                        && c.GetUniversalAdditionalCameraData().renderType == CameraRenderType.Base && (host == null || c.depth > host.depth))
                    {
                        host = c;
                    }
                }
                UniversalAdditionalCameraData ud = ui.GetUniversalAdditionalCameraData();
                ud.renderType = host != null ? CameraRenderType.Overlay : CameraRenderType.Base;
                for (int j = 0; j < cameras.Count; j++)
                {
                    Camera c = cameras[j];
                    if (c == null || c == ui || c.cameraType != CameraType.Game)
                    {
                        continue;
                    }
                    UniversalAdditionalCameraData cd = c.GetUniversalAdditionalCameraData();
                    List<Camera> stack = cd.renderType == CameraRenderType.Base ? cd.cameraStack : null;
                    if (stack == null)
                    {
                        continue;
                    }
                    if (c == host)
                    {
                        if (!stack.Contains(ui))
                        {
                            stack.Add(ui);
                        }
                    }
                    else
                    {
                        stack.Remove(ui);
                    }
                }
            }
        }

        /// <summary>自检收尾：拆掉 Volume 与回调，回到未初始化状态。</summary>
        public static void ResetForTests()
        {
            if (_hooked)
            {
                RenderPipelineManager.beginContextRendering -= OnBeginContext;
                _hooked = false;
            }
            if (_volume != null)
            {
                Object.DestroyImmediate(_volume.gameObject);
            }
            if (_profile != null)
            {
                Object.DestroyImmediate(_profile);
            }
            _volume = null;
            _profile = null;
            _enabled = true;
        }

        // beginCameraRendering 在 URP 17 里晚于相机数据初始化（当帧改了不生效），所以挂在每帧最前的 beginContextRendering
        private static void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras) => SyncCameras(cameras);

        private static void EnsureVolume()
        {
            if (_volume != null)
            {
                return;
            }
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "FgPostProcessProfile";
            _profile.hideFlags = HideFlags.DontSave;
            // 非泛型 Add(Type)：热更层不实例化 AOT 泛型方法
            var bloom = (Bloom)_profile.Add(typeof(Bloom), true);
            bloom.threshold.Override(Tune("settings.post_bloom_threshold", 1f));
            bloom.intensity.Override(Tune("settings.post_bloom_intensity", 0.6f));
            var vignette = (Vignette)_profile.Add(typeof(Vignette), true);
            vignette.intensity.Override(Tune("settings.post_vignette_intensity", 0.2f));
            var go = new GameObject("FgPostProcessVolume") { hideFlags = HideFlags.DontSave };
            if (Application.isPlaying)
            {
                Object.DontDestroyOnLoad(go);
            }
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 0f;
            _volume.sharedProfile = _profile;
        }

        private static float Tune(string id, float fallback) => UiTuningValues.TryGet(id, out float v) && v >= 0f ? v : fallback; // 0 = 关掉这一项（只留另一种效果）
    }
}
