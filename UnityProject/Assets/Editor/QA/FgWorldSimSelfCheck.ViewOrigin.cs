using System;
using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-SYS-02（承接 DEBT-FG0ARCH01-02 / perf-cut-audit P14，功能验收项）：浮动画面原点。
    /// 真实世界（家园控制器 + 统一镜头）里把镜头飞到核心外 100,000 格再飞回来：可达、原点按区块对齐重设、根物体整体平移、
    /// 画面位置 = 世界 − 原点（远处不丢精度）、屏幕取点加回原点后落在世界坐标上、着色器全局同步；开 / 关原点同一段模拟哈希一致；
    /// 负向：未越过阈值不重设、关闭时收回 0、越过 world.coord_limit 被钳住、暂停中照常跟随、会话重置时原点回 0 且画面对齐。
    /// </summary>
    public static partial class FgWorldSimSelfCheck
    {
        private const float FarCells = 100000f;

        private static void CheckFloatingOrigin()
        {
            Line("  · 浮动画面原点（FG15-SYS-02，DEBT-FG0ARCH01-02）：");
            float rebase = GameClock.TuningOr("camera.rebase_cells", 0f);
            float limit = GridContent.TuningInt("world.coord_limit");
            Expect(Mathf.Approximately(rebase, 4096f) && limit >= 1_000_000f,
                $"调参：camera.rebase_cells = {rebase}（初值 4096）、镜头钳制改用 world.coord_limit = {limit}");

            bool enabledBefore = ViewOrigin.Enabled;
            var probe = new GameObject("[fo-probe]");
            try
            {
                ViewOrigin.Enabled = true;
                // ── 正向：飞到 100,000 格再回来 ──
                CampaignState s = NewWorld(515151, dispatch: false);
                Expect(ViewOrigin.Cells.Equals(double2.zero), $"新世界原点为 0（实际 {ViewOrigin.Cells}）");
                string home = WorldSimulation.Home.SiteId;
                GridCell core = HomeGridService.CorePivot(s);
                var far = new Vector2(core.X + FarCells + 3.25f, core.Y - 41.5f);
                var probeWorld = new Vector3(far.x + 2.25f, 0f, far.y - 1.75f);
                probe.transform.position = ViewOrigin.ToView(probeWorld);
                int rebasesBefore = ViewOrigin.RebaseCount;
                ulong hashOnBefore = SimHash();
                bool flew = WorldView.FlyTo(home, far);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                Camera cam = WorldView.Camera;
                float2 focus = WorldView.Director.StrategyFocus;
                double2 o = ViewOrigin.Cells;
                bool aligned = o.x % ViewOrigin.Block == 0 && o.y % ViewOrigin.Block == 0;
                Vector3 camView = cam != null ? cam.transform.position : Vector3.zero;
                Expect(flew && cam != null && math.distance(focus, new float2(far.x, far.y)) < 0.5f,
                    $"100,000 格处可达：镜头飞到 ({far.x:F2}, {far.y:F2})，注视点 ({focus.x:F2}, {focus.y:F2})");
                Expect(ViewOrigin.RebaseCount > rebasesBefore && aligned && math.abs(o.x - far.x) <= rebase && math.abs(o.y - far.y) <= rebase,
                    $"越过 {rebase} 格重设原点：重设 {ViewOrigin.RebaseCount - rebasesBefore} 次，原点 ({o.x}, {o.y}) 按 {ViewOrigin.Block} 格区块对齐、离注视点 ≤ {rebase} 格");
                Expect(math.abs(camView.x) < rebase + 200f && math.abs(camView.z) < rebase + 200f,
                    $"镜头画面坐标留在原点附近（{camView.x:F1}, {camView.z:F1}），单精度误差 < 1 毫米");
                // 根物体整体平移：重设前按旧原点摆好的探针，重设后画面位置仍 = 世界 − 新原点
                Vector3 probeNow = probe.transform.position;
                Vector3 probeExpect = ViewOrigin.ToView(probeWorld);
                Expect((probeNow - probeExpect).magnitude < 0.01f,
                    $"根物体随原点整体平移：探针画面 ({probeNow.x:F3}, {probeNow.z:F3}) = 世界 − 原点 ({probeExpect.x:F3}, {probeExpect.z:F3})");
                Transform homeRoot = FindRoot("[HomeValley]");
                Vector3 rootExpect = ViewOrigin.ToView(Vector3.zero);
                Expect(homeRoot != null && (homeRoot.position - rootExpect).magnitude < 0.01f,
                    $"家园根物体平移到世界原点的画面位置 {rootExpect}（实际 {(homeRoot != null ? homeRoot.position.ToString() : "无")}）");
                Vector4 g = Shader.GetGlobalVector("_BgViewOrigin");
                Vector4 t = Shader.GetGlobalVector("_TerrainWorldOrigin");
                Expect(Mathf.Approximately(g.x, (float)o.x) && Mathf.Approximately(g.z, (float)o.y) && Mathf.Approximately(t.x, (float)o.x) && Mathf.Approximately(t.y, (float)o.y),
                    $"着色器全局同步：_BgViewOrigin ({g.x}, {g.z})、_TerrainWorldOrigin ({t.x}, {t.y})（传送带 / 管线实例与地形纹理按世界坐标取样）");
                // 屏幕取点：探针投到屏幕，世界射线取地面点 = 画面射线取的点加回原点（远处仍精确到毫米以内）。
                // batchmode 没有图形设备时 WorldToScreenPoint ↔ ScreenPointToRay 往返本身有亚格误差（与原点无关，原点为 0 时同样存在），只作参考行。
                if (cam != null)
                {
                    Vector3 screen = cam.WorldToScreenPoint(probeNow);
                    Ray ray = ViewOrigin.WorldRay(cam, screen);
                    Ray raw = cam.ScreenPointToRay(screen);
                    var ground = new Plane(Vector3.up, Vector3.zero);
                    bool hit = ground.Raycast(ray, out float enter);
                    bool hitRaw = ground.Raycast(raw, out float enterRaw);
                    Vector3 picked = hit ? ray.GetPoint(enter) : Vector3.zero;
                    Vector3 rawWorld = hitRaw ? ViewOrigin.ToWorld(raw.GetPoint(enterRaw)) : Vector3.one * 1e9f;
                    float err = new Vector2(picked.x - rawWorld.x, picked.z - rawWorld.z).magnitude;
                    float roundTrip = hitRaw ? new Vector2(raw.GetPoint(enterRaw).x - probeNow.x, raw.GetPoint(enterRaw).z - probeNow.z).magnitude : -1f;
                    Expect(hit && hitRaw && err < 0.001f,
                        $"射线取格加回原点：世界射线取到 ({picked.x:F3}, {picked.z:F3}) = 画面射线命中点 + 原点 ({rawWorld.x:F3}, {rawWorld.z:F3})，相差 {err * 1000f:F3} 毫米（参考：本机镜头屏幕往返误差 {roundTrip:F3} 格）");
                    GridCell cell = GridCell.FromWorld(new Vector2(picked.x, picked.z));
                    GridCell want = GridCell.FromWorld(new Vector2(rawWorld.x, rawWorld.z));
                    Expect(cell.Equals(want) && math.abs(cell.X - (int)math.floor(far.x)) < 8, $"远处取到的格是世界格（{cell.X},{cell.Y}，在 100,000 格外）");
                }

                // 负向：未越过阈值不重设
                int rc = ViewOrigin.RebaseCount;
                bool moved = ViewOrigin.Follow(o.x + rebase - 64.0, o.y, rebase);
                Expect(!moved && ViewOrigin.RebaseCount == rc, $"注视点离原点 {rebase - 64} 格（未越过阈值）不重设");

                // 负向：暂停中镜头照常飞、原点照常跟随（镜头用真实时间，与倍速无关）
                GameClock.SetPaused(true);
                var far2 = new Vector2(far.x + 9000f, far.y);
                WorldView.FlyTo(home, far2);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                float2 focus2 = WorldView.Director.StrategyFocus;
                Expect(math.distance(focus2, new float2(far2.x, far2.y)) < 0.5f && math.abs(ViewOrigin.Cells.x - far2.x) <= rebase,
                    $"暂停中飞 9,000 格：注视点 ({focus2.x:F1}, {focus2.y:F1})，原点跟到 {ViewOrigin.Cells.x}");
                GameClock.SetPaused(false);

                // 负向：越过 world.coord_limit 被钳住
                var beyond = new Vector2(limit * 2f, core.Y);
                WorldView.FlyTo(home, beyond);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                float2 focus3 = WorldView.Director.StrategyFocus;
                Expect(focus3.x <= limit + 0.5f, $"越过 world.coord_limit（{limit}）的落点被钳住：注视点 x = {focus3.x:F0}");

                // 飞回核心：原点回到核心所在区块
                WorldView.FlyTo(home, new Vector2(core.X, core.Y));
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                float2 back = WorldView.Director.StrategyFocus;
                Vector3 probeBack = probe.transform.position;
                Expect(math.distance(back, new float2(core.X, core.Y)) < 0.5f && math.abs(ViewOrigin.Cells.x - core.X) <= rebase && (probeBack - ViewOrigin.ToView(probeWorld)).magnitude < 0.02f,
                    $"飞回核心：注视点 ({back.x:F1}, {back.y:F1})、原点 ({ViewOrigin.Cells.x}, {ViewOrigin.Cells.y})，探针仍对齐世界坐标");
                ulong hashOnAfter = SimHash();
                long ticksOn = GameClock.Ticks;

                // 开关原点前后哈希一致：同一种子、同样的帧序列，关闭浮动原点再跑一遍
                ViewOrigin.Enabled = false;
                CampaignState s2 = NewWorld(515151, dispatch: false);
                ulong hashOffBefore = SimHash();
                int rcOff = ViewOrigin.RebaseCount;
                WorldView.FlyTo(home, far);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                Expect(ViewOrigin.Cells.Equals(double2.zero) && ViewOrigin.RebaseCount == rcOff,
                    $"关闭浮动原点：飞到远处原点仍为 0（重设 {ViewOrigin.RebaseCount - rcOff} 次）");
                GameClock.SetPaused(true);
                WorldView.FlyTo(home, far2);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                GameClock.SetPaused(false);
                WorldView.FlyTo(home, beyond);
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                WorldView.FlyTo(home, new Vector2(core.X, core.Y));
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                ulong hashOffAfter = SimHash();
                Expect(hashOnBefore == hashOffBefore && hashOnAfter == hashOffAfter && ticksOn == GameClock.Ticks && hashOnAfter != hashOnBefore,
                    $"开 / 关浮动原点同一段模拟哈希一致：开始 {hashOnBefore:X16} / {hashOffBefore:X16}，结束 {hashOnAfter:X16} / {hashOffAfter:X16}（{ticksOn} 步；画面原点不进模拟）");

                // 关闭时收回 0；会话重置时原点回 0、根物体平移回去
                ViewOrigin.Enabled = true;
                ViewOrigin.SetOrigin(new double2(far.x, far.y));
                Vector3 beforeReset = probe.transform.position;
                ViewOrigin.Enabled = false;
                bool collapsed = ViewOrigin.Follow(far.x, far.y, rebase);
                Expect(collapsed && ViewOrigin.Cells.Equals(double2.zero) && (probe.transform.position - probeWorld).magnitude < 0.02f,
                    $"关闭浮动原点时收回 0：探针从画面 ({beforeReset.x:F1}, {beforeReset.z:F1}) 回到世界坐标 ({probe.transform.position.x:F1}, {probe.transform.position.z:F1})");
                ViewOrigin.Enabled = true;
                ViewOrigin.SetOrigin(new double2(far.x, far.y));
                WorldSimulation.ResetSessionState();
                Expect(ViewOrigin.Cells.Equals(double2.zero) && (probe.transform.position - probeWorld).magnitude < 0.02f,
                    "新战役 / 读档前（WorldSimulation.ResetSessionState）原点回 0，留在场景里的根物体一并平移回世界坐标（原点不进存档）");
            }
            finally
            {
                ViewOrigin.Enabled = enabledBefore;
                ViewOrigin.Reset();
                UnityEngine.Object.DestroyImmediate(probe);
                GameClock.SetPaused(false);
            }
        }

        // 全部已载入地点的战斗内核哈希 + 时钟步数（画面原点开关不应改变任何一项）。
        private static ulong SimHash()
        {
            ulong h = 1469598103934665603UL ^ (ulong)GameClock.Ticks;
            IReadOnlyList<IWorldSite> sites = WorldSimulation.LoadedSites;
            for (int i = 0; i < sites.Count; i++)
            {
                if (sites[i] is GameLogic.Campaign.Regions.HomeValleyController hv && hv.Combat != null && hv.Combat.Kernel != null)
                {
                    h = (h ^ hv.Combat.Kernel.StateHash()) * 1099511628211UL;
                }
            }
            return h;
        }

        private static Transform FindRoot(string name)
        {
            for (int si = 0; si < UnityEngine.SceneManagement.SceneManager.sceneCount; si++)
            {
                foreach (GameObject r in UnityEngine.SceneManagement.SceneManager.GetSceneAt(si).GetRootGameObjects())
                {
                    if (r != null && r.name == name)
                    {
                        return r.transform;
                    }
                }
            }
            return null;
        }
    }
}
