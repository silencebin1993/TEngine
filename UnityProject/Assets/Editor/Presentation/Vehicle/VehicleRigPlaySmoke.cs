// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System;
using System.IO;
using System.Linq;
using BinGames.Presentation;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.View;
using GameLogic.View.Presentation;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-VEH-02 修复轮 1：正式路径 Play 冒烟（GameRoot.OnUpdate → PresentationHostBridge.Tick 第一次 Install / 登记模块 → 真实帧里挂上 / 摘下）。
    /// 主菜单 → 新游戏 → 家园；造一台装甲裙板履带机开 10 m（车身转向 +X）；换主组件（MorphRig 在 Play 下用 Destroy 延迟销毁 → Rig 经挂点组父节点取新节点）；
    /// 退出 Play（Application.quitting → PresentationHost.UnregisterAll → OnDisable 释放原生内存）、域重载后再退出 Unity。
    /// 跑法：<c>cd /d/BinGames-PL &amp;&amp; PL_CAPTURE_OUT=/d/BinGames-PL-review/PL-VEH-02/play bash tools/pl/pl-unity.sh gpuplay GameLogic.EditorTools.VehicleRigPlaySmoke.Run vehplay</c>，
    /// 之后 grep 日志里的 Leak / not been disposed / Exception（退出码 0 = 旅程断言全过）。
    /// </summary>
    [InitializeOnLoad]
    public static class VehicleRigPlaySmoke
    {
        private const string Prefix = "BinGames.PLVehSmoke.";
        private static JourneyRunner _runner;

        static VehicleRigPlaySmoke()
        {
            if (SessionState.GetString(Prefix + "active", "0") == "1")
            {
                _runner = new JourneyRunner(Build(), new SessionJourneyStore(Prefix), new EditorJourneyClock()); // 进 / 出 Play 重载域后续上
                Hook();
            }
        }

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(PresentationCapture.ReviewRoot(), "PL-VEH-02", "play");
            }
            Directory.CreateDirectory(outDir);
            _runner = new JourneyRunner(Build(), new SessionJourneyStore(Prefix), new EditorJourneyClock())
            {
                ReportPath = Path.Combine(outDir, "vehplay-journey.txt"),
            };
            _runner.Start();
            Hook();
        }

        private static JourneyDef Build() => new JourneyDef
        {
            Id = "PL-VEH-02-PLAY",
            Title = "PL-VEH-02 Rig 正式路径 Play 冒烟（主菜单 → 新游戏 → 家园 → 开车 → 换主组件 → 退出 Play）",
            Seed = PresentationCapture.DefaultSeed,
            TotalTimeoutSeconds = 900,
            FailOnErrorLog = false, // 报错由调用方 grep 日志统计（含退出 Play 后域重载时的原生内存泄漏报告）
            Steps =
            {
                JourneyCommon.S("enter", "开 main.unity 进 Play", 180, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, PresentationCapture.DefaultSeed)),
                JourneyCommon.S("menu", "主菜单点“新建”", 120, null, JourneyCommon.TickMenuNew, 2),
                JourneyCommon.S("newgame", "新游戏设置 → 进家园", 300, null, JourneyCommon.TickNewGame, 2),
                JourneyCommon.S("drive", "造装甲裙板履带机，命令向 +X 开 10 m", 60, null, TickDrive),
                JourneyCommon.S("refit", "换主组件（MorphRig 重建，Play 下延迟销毁）", 30, null, TickRefit),
                JourneyCommon.S("exit", "退出 Play，等域重载完成", 120, c => EditorApplication.ExitPlaymode(), TickExit),
            },
        };

        private static StepOutcome TickDrive(JourneyContext c)
        {
            if (!EditorApplication.isPlaying)
            {
                return StepOutcome.Fail("不在 Play 里");
            }
            IPresentationModule module = PresentationHostBridge.GameModules.FirstOrDefault(m => m is VehicleRigModule);
            int id = c.GetInt("id", 0);
            if (id == 0)
            {
                if (module == null || !PresentationHost.IsActive(module) || VehicleRigSystem.Count == 0)
                {
                    return c.StepElapsed > 20 ? StepOutcome.Fail($"进家园 20 s 后宿主桥的 VehicleRigModule 没启用（{(module != null && PresentationHost.IsActive(module))}）或没有机器登记（{VehicleRigSystem.Count}）") : StepOutcome.Wait;
                }
                c.Log($"宿主桥第一次 Tick 已装适配、登记模块：启用 {PresentationHost.IsActive(module)}、故障 {PresentationHost.IsFaulted(module)}、Rig 登记 {VehicleRigSystem.Count} 台（家园机器表现 {UnityEngine.Object.FindObjectsByType<MachineView>(FindObjectsSortMode.None).Length} 个）");
                id = VehicleRigWorld.SpawnArmored(new Vector2(6f, -6f));
                c.SetInt("id", id);
                return StepOutcome.Wait;
            }
            HomeValleyMachineMarker m = VehicleRigWorld.Marker(id);
            if (m?.View == null || !VehicleRigSystem.IsRegistered(id))
            {
                return c.StepElapsed > 10 ? StepOutcome.Fail($"新造的 #{id} 10 s 内没挂上表现 / 没登记进 Rig") : StepOutcome.Wait;
            }
            if (c.GetInt("moved") == 0)
            {
                c.SetInt("moved", 1);
                c.Set("x0", m.Position.x.ToString("R"));
                c.Set("z0", m.Position.y.ToString("R"));
                m.CommandMoveTo(new Vector3(m.Position.x + 10f, 1f, m.Position.y));
                c.Set("t0", c.StepElapsed.ToString("R"));
                return StepOutcome.Wait;
            }
            if (c.StepElapsed - double.Parse(c.Get("t0", "0")) < 3)
            {
                return StepOutcome.Wait;
            }
            Transform sil = m.View.transform.Find(PlaceholderSilhouette.PartsName);
            Transform rig = VehicleRigAdapter.MorphRigOf(id, m.View);
            float yaw = VehicleRigSystem.YawDegOf(id);
            float armor = VehicleRigWorld.Unit(id, out BinGames.Sim.Combat.CombatUnitView v) ? m.Site.Kernel.ArmorOf(m.UnitId) : 0f;
            float facing = Mathf.Atan2(v.Facing.x, v.Facing.y) * Mathf.Rad2Deg;
            Vector2 start = new Vector2(float.Parse(c.Get("x0", "0")), float.Parse(c.Get("z0", "0")));
            float moved = (m.Position - start).magnitude;
            // 家园有寻路，绕行时 Facing 不一定正对 +X：判“车身跟内核 Facing”（≤ 5°）+ 真的开出去了 + 车身不在初始的 0°
            bool ok = sil != null && rig != null && VehicleRigSystem.NodeOf(id, 0) == sil && VehicleRigSystem.NodeOf(id, 1) == rig && armor > 0f && moved > 1f
                      && Mathf.Abs(Mathf.DeltaAngle(yaw, facing)) <= 5f && Mathf.Abs(Mathf.DeltaAngle(yaw, 0f)) > 5f && sil.localRotation == rig.localRotation
                      && !PresentationHost.IsFaulted(module);
            string what = $"#{id}（正面装甲 {armor:0.##}）开 3 s 走了 {moved:0.0} m，车身偏航 {yaw:0.0}°、内核 Facing {facing:0.0}°（≤ 5°），两节点同一旋转 {sil?.localRotation == rig?.localRotation}，模块故障 {PresentationHost.IsFaulted(module)}";
            return ok ? StepOutcome.Done(what) : StepOutcome.Fail(what);
        }

        private static StepOutcome TickRefit(JourneyContext c)
        {
            int id = c.GetInt("id", 0);
            HomeValleyMachineMarker m = VehicleRigWorld.Marker(id);
            if (m?.View == null)
            {
                return StepOutcome.Fail($"#{id} 没有表现");
            }
            if (c.GetInt("refit") == 0)
            {
                c.SetInt("refit", 1);
                c.Set("oldRig", VehicleRigAdapter.MorphRigOf(id, m.View).GetInstanceID().ToString());
                const string bp = "plveh02-cannon-skirt";
                FgTurretSelfCheck.AddBlueprint(CampaignSession.Current, bp, BlueprintCircuitBoard.CreateDefault(m.ChassisId, "comp_cannon", null,
                    GameLogic.Campaign.Content.ComponentCatalog.StructArmorSkirtId, Array.Empty<string>()));
                var r = MachineLoadoutRegistry.Register(CampaignSession.Current, id, bp, 1);
                c.Log($"换主组件（连射器 → 重炮）：{r.Success} {r.Message}");
                c.Set("t0", c.StepElapsed.ToString("R"));
                return r.Success ? StepOutcome.Wait : StepOutcome.Fail("改装失败：" + r.Message);
            }
            if (c.StepElapsed - double.Parse(c.Get("t0", "0")) < 1.5)
            {
                return StepOutcome.Wait;
            }
            Transform sil = m.View.transform.Find(PlaceholderSilhouette.PartsName);
            Transform rig = VehicleRigAdapter.MorphRigOf(id, m.View);
            int rigs = m.View.transform.Cast<Transform>().Count(t => t.name == MachineMorphView.RigName);
            bool changed = rig != null && rig.GetInstanceID().ToString() != c.Get("oldRig");
            bool ok = changed && VehicleRigSystem.NodeOf(id, 1) == rig && rigs == 1 && rig.localRotation == sil.localRotation && rig.localRotation != Quaternion.identity;
            string what = $"MorphRig 重建：换成新节点 {changed}、Rig 登记的就是新节点 {VehicleRigSystem.NodeOf(id, 1) == rig}、挂点根 {rigs} 个（旧的已被 Destroy）、新节点与剪影同一非单位旋转 {rig != null && rig.localRotation == sil.localRotation}";
            return ok ? StepOutcome.Done(what) : StepOutcome.Fail(what);
        }

        private static StepOutcome TickExit(JourneyContext c)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("back") == 0)
            {
                c.SetInt("back", 1);
                c.Set("t0", c.StepElapsed.ToString("R"));
                return StepOutcome.Wait;
            }
            if (c.StepElapsed - double.Parse(c.Get("t0", "0")) < 3)
            {
                return StepOutcome.Wait; // 域重载后的泄漏报告在这几秒里进日志
            }
            return StepOutcome.Done($"已回到编辑模式（宿主模块数 {PresentationHost.ModuleCount}、Rig 登记 {VehicleRigSystem.Count}：域已重载，静态状态清空）");
        }

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
            Debug.Log($"[PL 采集] VehPlaySmoke {_runner.Result}：{(pass ? "完成" : _runner.FailStepId + " " + _runner.FailReason)}；报告 {_runner.ReportPath}；结论：{(pass ? "通过" : "失败")}");
            _runner = null;
            SessionState.SetString(Prefix + "active", "0");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(pass ? 0 : 1);
            }
        }
    }
}
