using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BinGames.Sim
{
    /// <summary>
    /// FG15-SYS-02（承接 DEBT-FG0ARCH01-02 / perf-cut-audit P14）：浮动画面原点。模拟坐标（double，格）永远是世界坐标；
    /// 画面（Transform、实例化绘制、着色器）一律放在“世界坐标 − 原点”，原点按区块对齐（<see cref="Block"/> 的整数倍，
    /// 单精度下精确可表示），镜头离原点超过 <c>camera.rebase_cells</c> 时重设。重设时把所有已载入场景（含 DontDestroyOnLoad）
    /// 的根物体整体平移 −Δ（屏幕空间画布除外），所以“只摆一次”的表现对象不用逐个改；每帧按世界坐标重写位置的
    /// 生产者（镜头、内核同步、实例化绘制、传送带 / 管线着色器）减去 <see cref="Cells"/>，射线取格时加回。
    /// 模拟从不读本类：开关原点不改变任何战斗 / 物流结果（FgViewOriginSelfCheck 断言哈希一致）。
    /// </summary>
    public static class ViewOrigin
    {
        /// <summary>原点对齐粒度（格）= 世界区块边长 grid.chunk_size = 32。</summary>
        public const double Block = 32.0;

        private static readonly int ShaderOriginId = Shader.PropertyToID("_BgViewOrigin");
        private static readonly int TerrainOriginId = Shader.PropertyToID("_TerrainWorldOrigin");

        /// <summary>当前原点（世界格坐标 x / y，即画面的 x / z）。</summary>
        public static double2 Cells { get; private set; }
        /// <summary>重设次数（自检 / 调试读点）。</summary>
        public static int RebaseCount { get; private set; }
        /// <summary>false = 原点固定在 0（调试 / 自检对照组）。</summary>
        public static bool Enabled { get; set; } = true;

        public static float X => (float)Cells.x;
        public static float Z => (float)Cells.y;

        /// <summary>世界坐标 → 画面坐标。</summary>
        public static Vector3 ToView(Vector3 world) => new Vector3(world.x - X, world.y, world.z - Z);
        /// <summary>世界格坐标（double）→ 画面坐标（先在 double 里减原点再转单精度，远处不丢精度）。</summary>
        public static Vector3 ToView(double x, float height, double y) => new Vector3((float)(x - Cells.x), height, (float)(y - Cells.y));
        /// <summary>画面坐标 → 世界坐标（射线取格、读 Transform 位置时加回原点）。</summary>
        public static Vector3 ToWorld(Vector3 view) => new Vector3(view.x + X, view.y, view.z + Z);

        /// <summary>屏幕点 → 世界坐标射线（只用于和水平面求交、与世界坐标比距离；物理射线用镜头自带的画面射线）。</summary>
        public static Ray WorldRay(Camera camera, Vector3 screen)
        {
            Ray r = camera.ScreenPointToRay(screen);
            r.origin = ToWorld(r.origin);
            return r;
        }

        /// <summary>画面射线 → 世界射线（物理射线没打中、改按水平面取点时用）。</summary>
        public static Ray ToWorld(Ray view) => new Ray(ToWorld(view.origin), view.direction);

        /// <summary>镜头注视点（世界格）离原点超过 <paramref name="threshold"/> 格就把原点重设到它所在的区块；返回 true = 本次重设了。
        /// 关闭时把原点收回 0。O(根物体数)，只在越过阈值的那一帧发生。</summary>
        public static bool Follow(double x, double y, double threshold)
        {
            double2 next;
            if (!Enabled)
            {
                next = double2.zero;
            }
            else
            {
                if (math.abs(x - Cells.x) <= threshold && math.abs(y - Cells.y) <= threshold)
                {
                    return false;
                }
                next = new double2(math.round(x / Block) * Block, math.round(y / Block) * Block);
            }
            return SetOrigin(next);
        }

        /// <summary>把原点设为 <paramref name="next"/>（自动对齐到区块）。已载入场景的根物体整体平移 −Δ。</summary>
        public static bool SetOrigin(double2 next)
        {
            next = new double2(math.round(next.x / Block) * Block, math.round(next.y / Block) * Block);
            double2 delta = next - Cells;
            if (delta.x == 0.0 && delta.y == 0.0)
            {
                PublishShaderGlobals();
                return false;
            }
            Cells = next;
            RebaseCount++;
            ShiftRoots(new Vector3(-(float)delta.x, 0f, -(float)delta.y));
            PublishShaderGlobals();
            Rebased?.Invoke(new Vector3(-(float)delta.x, 0f, -(float)delta.y));
            return true;
        }

        /// <summary>新会话 / 读档 / 回主菜单：原点回 0（仍留在场景里的根物体一并平移回去，画面不错位）。</summary>
        public static void Reset() => SetOrigin(double2.zero);

        /// <summary>重设后的回调（参数 = 画面平移量 −Δ）：给保存了画面坐标的少数状态（镜头过渡端点等）同步平移。</summary>
        public static event Action<Vector3> Rebased;

        private static void PublishShaderGlobals()
        {
            var v = new Vector4(X, 0f, Z, 0f);
            Shader.SetGlobalVector(ShaderOriginId, v);
            Shader.SetGlobalVector(TerrainOriginId, new Vector4(X, Z, 0f, 0f));
        }

        private static void ShiftRoots(Vector3 shift)
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                ShiftScene(SceneManager.GetSceneAt(s), shift);
            }
            if (Application.isPlaying)
            {
                var probe = new GameObject("[ViewOriginProbe]");
                UnityEngine.Object.DontDestroyOnLoad(probe);
                Scene ddol = probe.scene;
                UnityEngine.Object.DestroyImmediate(probe);
                ShiftScene(ddol, shift);
            }
            Physics.SyncTransforms();
        }

        private static void ShiftScene(Scene scene, Vector3 shift)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null || IsScreenSpaceUi(root))
                {
                    continue;
                }
                root.transform.position += shift;
            }
        }

        // 屏幕空间画布由 Canvas 驱动位置，不跟着世界平移（世界空间画布照常平移）。
        private static bool IsScreenSpaceUi(GameObject root)
        {
            var canvas = root.GetComponent<Canvas>();
            return canvas != null && canvas.renderMode != RenderMode.WorldSpace;
        }
    }
}
