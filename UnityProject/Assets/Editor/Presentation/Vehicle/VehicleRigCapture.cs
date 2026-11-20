// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Presentation;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View.Presentation;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>PL-VEH-02 自检与采集共用的家园准备（证据入口同一套：FgMachineMorphSelfCheck.Evidence*）。</summary>
    internal static class VehicleRigWorld
    {
        public static string Blueprint => FgMachineMorphSelfCheck.EvidenceBpGunPlain;

        /// <summary>在家园核心 + <paramref name="offsetFromCore"/> 处造一台机器（指定底盘）并登记装配；返回 LogicId（失败抛异常）。</summary>
        public static int Spawn(string chassisId, Vector2 offsetFromCore, string blueprint = null)
        {
            blueprint = blueprint ?? Blueprint;
            Vector2 at = HomeValleyLayout.Core.Position + offsetFromCore;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassisId, blueprint, HomeValleyLayout.RegionId, at, 400f, 400f, "Player", 1);
            if (!r.Success)
            {
                throw new InvalidOperationException($"造机器失败（{chassisId}）：{r.Message}");
            }
            MachineLoadoutRegistry.Register(CampaignSession.Current, r.LogicId, blueprint, 1);
            return r.LogicId;
        }

        public const string SkirtBlueprint = "plveh02-armor-skirt";

        /// <summary>修复轮 1：带正面装甲的机器（履带 + 连射器 + 装甲裙板，走正式装配 → CombatSite.FlagFrontalArmor），返回 LogicId。</summary>
        public static int SpawnArmored(Vector2 offsetFromCore)
        {
            FgTurretSelfCheck.AddBlueprint(CampaignSession.Current, SkirtBlueprint, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null,
                GameLogic.Campaign.Content.ComponentCatalog.StructArmorSkirtId, Array.Empty<string>()));
            return Spawn(HomeValleyLayout.Erc003ChassisId, offsetFromCore, SkirtBlueprint);
        }

        /// <summary>内核里的单位视图（没有返回 false）。</summary>
        public static bool Unit(int logicId, out BinGames.Sim.Combat.CombatUnitView v)
        {
            HomeValleyMachineMarker m = Marker(logicId);
            v = default;
            return m != null && m.Site?.Kernel != null && m.Site.Kernel.TryGetUnit(m.UnitId, out v);
        }

        /// <summary>世界内推进 n 个采集帧（每帧 0.05 游戏秒 = EvidenceFrames(1)），每帧后 TickManual（PL-QA-01 口径）。</summary>
        public static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                FgMachineMorphSelfCheck.EvidenceFrames(1);
                PresentationHostBridge.TickManual(PresentationCapture.Dt, GameClock.FrameScaledDt);
            }
        }

        public static HomeValleyMachineMarker Marker(int logicId) =>
            WorldSimulation.Home != null && WorldSimulation.Home.Combat.TryGetMachineMarker(logicId, out HomeValleyMachineMarker m) ? m : null;

        public static Renderer[] Renderers(IEnumerable<int> ids) =>
            ids.Select(Marker).Where(m => m?.View != null).SelectMany(m => m.View.GetComponentsInChildren<Renderer>(false)).Where(r => r.enabled).ToArray();

        public static void Move(int id, Vector2 offset)
        {
            HomeValleyMachineMarker m = Marker(id);
            m?.CommandMoveTo(new Vector3(m.Position.x + offset.x, 1f, m.Position.y + offset.y));
        }
    }

    /// <summary>
    /// PL-VEH-02 采集：
    /// - 登记场景 <c>VehHomePatrol</c>（pl-capture.sh 出开 / 关成对图 + 10 帧短片）：家园 6 台（轮式 2、履带 2、悬浮 2），预跑后让一台履带掉头，主图那帧正在转；
    /// - 登记场景 <c>VehStress120</c>：家园 120 台绕圈（GPU 成对测量用）；
    /// - <see cref="PatrolFromCommandLine"/>（gpu）：VehHomePatrol 脚本化 200 帧 = 600 步（直线往返、原地掉头 180°、急停、绕圈、直控接入一台、末尾暂停 10 帧），
    ///   开 / 关逐帧比内核 StateHash，开的那次写运动日志并断言曲线；开（幅度 1）/ 关 / 幅度 0.5 / 1.6 各出 0.5 倍帧序列（评审 GIF 用）；
    /// - <see cref="StressFromCommandLine"/>（gpu）：120 台开 / 关 CPU 增量与 GPU 毫秒增量（只测一次，超线记警告）。
    /// </summary>
    public static class VehicleRigCapture
    {
        private static readonly (string Chassis, Vector2 At)[] PatrolCars =
        {
            (HomeValleyLayout.Erc001ChassisId, new Vector2(4f, -4f)),   // 0 轮式：直线往返
            (HomeValleyLayout.Erc003ChassisId, new Vector2(10f, -4f)),  // 1 履带：原地掉头 180°
            (ChassisCatalogHover, new Vector2(16f, -4f)),               // 2 悬浮：急停
            (HomeValleyLayout.Erc001ChassisId, new Vector2(4f, -12f)),  // 3 轮式：绕圈
            (HomeValleyLayout.Erc003ChassisId, new Vector2(10f, -12f)), // 4 履带：直行后停
            (ChassisCatalogHover, new Vector2(16f, -12f)),              // 5 悬浮：直控接入
        };

        private const string ChassisCatalogHover = GameLogic.Campaign.Content.ChassisCatalog.ChassisHoverId;
        private static readonly string[] Roles = { "轮式直线往返", "履带原地掉头", "悬浮急停", "轮式绕圈", "履带直行后停", "悬浮直控接入", "装甲裙板履带掉头" };
        public const int ArmoredIndex = 6; // 修复轮 1：带正面装甲的机器（真实装配），画面朝向跟内核 Facing
        private static List<int> _ids = new List<int>();

        private static List<int> SpawnPatrol()
        {
            var ids = new List<int>();
            foreach ((string chassis, Vector2 at) in PatrolCars)
            {
                int id;
                try
                {
                    id = VehicleRigWorld.Spawn(chassis, at);
                }
                catch (InvalidOperationException)
                {
                    id = VehicleRigWorld.Spawn(HomeValleyLayout.Erc003ChassisId, at); // 悬浮底盘造不出来时退回履带（日志里会写原型）
                }
                ids.Add(id);
            }
            ids.Add(VehicleRigWorld.SpawnArmored(new Vector2(22f, -8f)));
            VehicleRigWorld.Frames(20);
            return ids;
        }

        [PresentationCaptureScene("VehHomePatrol")]
        public static void VehHomePatrol(PresentationCaptureContext c)
        {
            VehicleRigAdapter.Install();
            VehicleRigSystem.Clear();
            c.OpenHome();
            _ids = SpawnPatrol();
            VehicleRigWorld.Move(_ids[1], new Vector2(0f, 14f)); // 履带朝 +Z（= Rig 起步朝向）开出去
            VehicleRigWorld.Move(_ids[0], new Vector2(-12f, 0f));
            VehicleRigWorld.Move(_ids[3], new Vector2(0f, -8f));
            VehicleRigWorld.Frames(30);
            VehicleRigWorld.Move(_ids[1], new Vector2(0f, -14f)); // 掉头：主图（预热 8 帧 = 0.4 s 后）正在转
            c.Modules.Add(new VehicleRigModule());
            c.Feature = PresentationFeatureRegistry.VehRigId;
            c.WarmupFrames = 8;
            List<int> ids = _ids;
            c.MaskTargets = () => VehicleRigWorld.Renderers(ids);
            c.TargetMoves = true;
        }

        [PresentationCaptureScene("VehStress120")]
        public static void VehStress120(PresentationCaptureContext c)
        {
            VehicleRigAdapter.Install();
            VehicleRigSystem.Clear();
            c.OpenHome();
            var ids = new List<int>();
            for (int i = 0; i < 120; i++)
            {
                try
                {
                    ids.Add(VehicleRigWorld.Spawn(i % 2 == 0 ? HomeValleyLayout.Erc001ChassisId : HomeValleyLayout.Erc003ChassisId,
                        new Vector2(-22f + i % 12 * 4f, -6f - i / 12 * 3.5f)));
                }
                catch (InvalidOperationException)
                {
                    break; // 家园容量到顶：按实际台数记
                }
            }
            VehicleRigWorld.Frames(20);
            for (int i = 0; i < ids.Count; i++)
            {
                VehicleRigWorld.Move(ids[i], i % 2 == 0 ? new Vector2(6f, 4f) : new Vector2(-6f, 4f));
            }
            _ids = ids;
            c.Modules.Add(new VehicleRigModule());
            c.Feature = PresentationFeatureRegistry.VehRigId;
            c.MaskTargets = () => VehicleRigWorld.Renderers(ids);
            c.TargetMoves = true;
        }

        // ── 脚本化巡逻：开 / 关 600 步哈希、运动日志、帧序列 ──

        [Serializable]
        public sealed class CarFrame
        {
            public int id;
            public string role;
            public string archetype;
            public float yaw, pitch, roll, y;
            public float facingYaw;
            public bool armored;
            public float moveSpeed, turnRate, steer;
            public Vector2 pos;
            public Vector2 viewport;
        }

        [Serializable]
        public sealed class PatrolFrame
        {
            public int frame;
            public double gameSeconds;
            public bool paused;
            public string hash;
            public List<CarFrame> cars = new List<CarFrame>();
        }

        [Serializable]
        public sealed class PatrolRun
        {
            public string label;
            public bool on;
            public float amplitude;
            public string device;
            public string error;
            public List<PatrolFrame> frames = new List<PatrolFrame>();
        }

        public const int PatrolFrames = 200;

        private static PatrolRun RunPatrol(bool on, float amplitude, string outDir, string label)
        {
            var run = new PatrolRun { label = label, on = on, amplitude = amplitude, device = SystemInfo.graphicsDeviceName };
            var c = new PresentationCaptureContext { FeatureOn = on };
            var module = new VehicleRigModule();
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                VehicleRigAdapter.Install();
                VehicleRigSystem.Clear();
                c.OpenHome();
                List<int> ids = SpawnPatrol();
                rt = new RenderTexture(PresentationCapture.Width / 2, PresentationCapture.Height / 2, 24, RenderTextureFormat.ARGB32);
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                Camera cam = c.Camera;
                cam.aspect = (float)rt.width / rt.height;
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, on);
                VehicleRigProfileFile prof = VehicleRigProfiles.Defaults();
                prof.amplitude = amplitude;
                VehicleRigProfiles.Apply(prof);
                PresentationHost.Register(module);
                string frameDir = Path.Combine(outDir, label);
                Directory.CreateDirectory(frameDir);
                bool uplinked = false;
                for (int f = 0; f < PatrolFrames; f++)
                {
                    Script(f, ids, ref uplinked);
                    VehicleRigWorld.Frames(1);
                    var pf = new PatrolFrame
                    {
                        frame = f, gameSeconds = GameClock.GameSeconds, paused = GameClock.Paused,
                        hash = WorldSimulation.Home.Combat.Kernel.StateHash().ToString("X16"),
                    };
                    for (int i = 0; i < ids.Count; i++)
                    {
                        HomeValleyMachineMarker m = VehicleRigWorld.Marker(ids[i]);
                        var cf = new CarFrame { id = ids[i], role = Roles[i] };
                        if (m != null)
                        {
                            cf.archetype = VehicleRigAdapter.ArchetypeOf(m.ChassisId).ToString();
                            cf.pos = m.Position;
                            if (m.Site.Kernel.TryGetUnit(m.UnitId, out BinGames.Sim.Combat.CombatUnitView v))
                            {
                                cf.facingYaw = Mathf.Atan2(v.Facing.x, v.Facing.y) * Mathf.Rad2Deg;
                                cf.armored = m.Site.Kernel.ArmorOf(m.UnitId) > 0f;
                            }
                            Transform sil = m.View != null ? m.View.transform.Find(GameLogic.View.PlaceholderSilhouette.PartsName) : null;
                            if (sil != null)
                            {
                                Vector3 fwd = sil.localRotation * Vector3.forward;
                                cf.yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                                cf.pitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                                Vector3 right = sil.localRotation * Vector3.right;
                                cf.roll = Mathf.Asin(Mathf.Clamp(right.y, -1f, 1f)) * Mathf.Rad2Deg;
                                cf.y = sil.localPosition.y;
                                Vector3 vp = cam.WorldToViewportPoint(m.View.transform.position);
                                cf.viewport = new Vector2(vp.x, vp.y);
                            }
                            if (VehicleRigSystem.TryGetSignals(ids[i], out VehicleRigSignals sg))
                            {
                                cf.moveSpeed = sg.MoveSpeed;
                                cf.turnRate = sg.TurnRate;
                                cf.steer = sg.SteerAngle;
                            }
                        }
                        pf.cars.Add(cf);
                    }
                    run.frames.Add(pf);
                    cam.targetTexture = rt;
                    PresentationCapture.BeforeRender?.Invoke(cam);
                    cam.Render();
                    cam.targetTexture = null;
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(frameDir, $"f{f:000}.png"), tex.EncodeToPNG());
                }
            }
            catch (Exception e)
            {
                run.error = e.ToString();
                Debug.LogError($"[PL 采集] VehPatrol {label} 出错：{e}");
            }
            finally
            {
                GameClock.SetPaused(false);
                PresentationHost.Unregister(module);
                VehicleRigProfiles.Apply(null);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                if (c.Camera != null)
                {
                    c.Camera.targetTexture = null;
                    c.Camera.ResetAspect();
                }
                if (c.Evidence)
                {
                    FgMachineMorphSelfCheck.EvidenceEnd();
                }
                VehicleRigSystem.Clear();
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
                if (tex != null)
                {
                    Object.DestroyImmediate(tex);
                }
            }
            return run;
        }

        // 脚本（只在固定帧号下命令，开 / 关两次完全相同）
        private static void Script(int f, List<int> ids, ref bool uplinked)
        {
            if (f == 0)
            {
                VehicleRigWorld.Move(ids[0], new Vector2(-12f, 0f));
                VehicleRigWorld.Move(ids[1], new Vector2(0f, 10f));
                VehicleRigWorld.Move(ids[2], new Vector2(-24f, 0f));
                VehicleRigWorld.Move(ids[4], new Vector2(0f, -14f));
                SignalUplinkService.Request(ids[5], UplinkSource.MachineList);
            }
            if (f == 40)
            {
                VehicleRigWorld.Move(ids[1], new Vector2(0f, -10f)); // 原地掉头 180°
            }
            if (ids.Count > ArmoredIndex)
            {
                if (f == 0)
                {
                    VehicleRigWorld.Move(ids[ArmoredIndex], new Vector2(0f, 10f));
                }
                if (f == 40)
                {
                    VehicleRigWorld.Move(ids[ArmoredIndex], new Vector2(0f, -10f)); // 装甲机掉头：内核 Facing 翻 180°
                }
                if (f == 110)
                {
                    VehicleRigWorld.Marker(ids[ArmoredIndex])?.CancelCommandMove();
                }
            }
            if (f == 50)
            {
                VehicleRigWorld.Marker(ids[2])?.CancelCommandMove(); // 急停
            }
            if (f == 70)
            {
                VehicleRigWorld.Move(ids[0], new Vector2(12f, 0f)); // 往回开
            }
            if (f == 110)
            {
                VehicleRigWorld.Marker(ids[4])?.CancelCommandMove();
            }
            if (f % 12 == 0 && f < 170)
            {
                float a = f / 12 * 60f * Mathf.Deg2Rad;
                VehicleRigWorld.Move(ids[3], new Vector2(Mathf.Sin(a) * 5f, Mathf.Cos(a) * 5f)); // 绕圈（每 0.6 s 换一个圆周点）
            }
            HomeValleyMachineMarker possessed = VehicleRigWorld.Marker(ids[5]);
            uplinked |= SignalPresence.CurrentMachineLogicId == ids[5];
            if (uplinked && possessed != null)
            {
                possessed.SetDirectInput(f < 100 ? new Vector2(0f, 1f) : f < 150 ? new Vector2(1f, 0f) : Vector2.zero);
            }
            if (f == 185)
            {
                GameClock.SetPaused(true);
            }
            if (f == 195)
            {
                GameClock.SetPaused(false);
            }
        }

        private static string OutDir(string sub)
        {
            string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(PresentationCapture.ReviewRoot(), "PL-VEH-02", sub);
            }
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        /// <summary>gpu 入口：脚本化巡逻开 / 关 + 幅度 3 档。结论行 “[PL 采集] VehPatrol 结论：…”，断言行 ✓ / ✗，patrol.json 在输出目录。</summary>
        public static void PatrolFromCommandLine()
        {
            int fails = 0;
            var log = new StringBuilder();
            void Check(bool ok, string what)
            {
                log.AppendLine((ok ? "  ✓ " : "  ✗ ") + what);
                if (!ok)
                {
                    fails++;
                }
            }
            try
            {
                string outDir = OutDir("patrol");
                bool device = PresentationCapture.DeviceOk(out string dev);
                Check(device, "图形设备 " + dev + " 含 " + PresentationCapture.RequiredGpuName);
                PatrolRun off = RunPatrol(false, 1f, outDir, "off");
                PatrolRun on = RunPatrol(true, 1f, outDir, "on");
                PatrolRun soft = RunPatrol(true, 0.5f, outDir, "on_amp050");
                PatrolRun strong = RunPatrol(true, 1.6f, outDir, "on_amp160");
                foreach (PatrolRun r in new[] { off, on, soft, strong })
                {
                    Check(r.error == null && r.frames.Count == PatrolFrames, $"{r.label}：{r.frames.Count} 帧{(r.error != null ? "，出错 " + r.error.Split('\n')[0] : "")}");
                    File.WriteAllText(Path.Combine(outDir, $"patrol_{r.label}.json"), JsonUtility.ToJson(r));
                }
                Assess(off, on, soft, strong, Check);
                log.Insert(0, $"[PL 采集] VehPatrol 输出 {outDir}\n");
            }
            catch (Exception e)
            {
                fails++;
                log.AppendLine("  ✗ 抛异常：" + e);
            }
            log.AppendLine($"[PL 采集] VehPatrol 结论：{(fails == 0 ? "通过" : $"失败 {fails} 项")}");
            Debug.Log(log.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fails == 0 ? 0 : 1);
            }
        }

        private static void Assess(PatrolRun off, PatrolRun on, PatrolRun soft, PatrolRun strong, Action<bool, string> check)
        {
            int n = Math.Min(off.frames.Count, on.frames.Count);
            int firstDiff = -1;
            for (int f = 0; f < n && firstDiff < 0; f++)
            {
                if (off.frames[f].hash != on.frames[f].hash)
                {
                    firstDiff = f;
                }
            }
            long steps = n > 0 ? (long)Math.Round(on.frames[n - 1].gameSeconds * 60) : 0;
            check(n == PatrolFrames && firstDiff < 0, $"开 / 关 {n} 帧（约 {steps} 步）内核 StateHash 逐帧相等{(firstDiff >= 0 ? $"（第 {firstDiff} 帧起不同）" : "")}");
            if (on.frames.Count < PatrolFrames)
            {
                return;
            }
            VehicleRigProfileFile prof = VehicleRigProfiles.Defaults();
            int cars = on.frames[0].cars.Count;
            for (int i = 0; i < cars; i++)
            {
                CarFrame first = on.frames[0].cars[i];
                VehicleArchetype arch = Enum.TryParse(first.archetype, out VehicleArchetype a) ? a : VehicleArchetype.Wheel;
                float rate = VehicleRigProfiles.Of(prof, arch).turnRateDeg * 0.05f + 0.5f;
                float maxStep = 0f, maxPitch = 0f, maxRoll = 0f, moved = 0f, turned = 0f;
                for (int f = 1; f < PatrolFrames; f++)
                {
                    CarFrame p = on.frames[f - 1].cars[i], q = on.frames[f].cars[i];
                    maxStep = Mathf.Max(maxStep, Mathf.Abs(Mathf.DeltaAngle(p.yaw, q.yaw)));
                    maxPitch = Mathf.Max(maxPitch, Mathf.Abs(q.pitch));
                    maxRoll = Mathf.Max(maxRoll, Mathf.Abs(q.roll));
                    moved += (q.pos - p.pos).magnitude;
                    turned += Mathf.Abs(Mathf.DeltaAngle(p.yaw, q.yaw));
                }
                check(maxStep <= rate && maxPitch <= 6.01f && maxRoll <= 4.01f,
                    $"#{first.id} {first.role}（{first.archetype}）：行驶 {moved:0.0} m、车身累计转 {turned:0}°；每帧偏航 ≤ {maxStep:0.00}°（限 {rate:0.00}）、|俯仰| ≤ {maxPitch:0.00}°、|侧倾| ≤ {maxRoll:0.00}°");
                bool offStill = off.frames.All(fr => fr.cars[i].yaw == 0f && fr.cars[i].pitch == 0f && fr.cars[i].roll == 0f);
                check(offStill, $"#{first.id} 关的那次车身全程不转（现状：平移的棋子）");
            }
            // 修复轮 1：带正面装甲的机器（真实装配）——内核 Facing 稳定 ≥ 180/转速 + 0.25 s 的帧里画面偏航与 Facing 偏差 ≤ 5°（站着瞄准在 VehRig 段的独立战斗地点里测：家园机器不主动交战）
            if (cars > ArmoredIndex)
            {
                CarFrame[] ar = on.frames.Select(fr => fr.cars[ArmoredIndex]).ToArray();
                float limit = 180f / prof.track.turnRateDeg + 0.25f;
                double lastChange = on.frames[0].gameSeconds;
                float worst = 0f, flip = 0f;
                int judged = 0;
                for (int f = 1; f < PatrolFrames; f++)
                {
                    float df = Mathf.Abs(Mathf.DeltaAngle(ar[f].facingYaw, ar[f - 1].facingYaw));
                    flip = Mathf.Max(flip, Mathf.Abs(Mathf.DeltaAngle(ar[f].facingYaw, ar[0].facingYaw)));
                    if (df > 1f)
                    {
                        lastChange = on.frames[f].gameSeconds;
                    }
                    else if (on.frames[f].gameSeconds - lastChange >= limit)
                    {
                        judged++;
                        worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(ar[f].yaw, ar[f].facingYaw)));
                    }
                }
                check(ar[0].armored && judged >= 20 && worst <= 5f,
                    $"#{ar[0].id} {ar[0].role}：正面装甲 {ar[0].armored}；内核 Facing 最大转过 {flip:0}°，Facing 稳定 ≥ {limit:0.00} s 的 {judged} 帧里画面偏航与 Facing 最大偏差 {worst:0.0}°（≤ 5°）");
            }
            // 急停（悬浮 #2 在第 50 帧取消赶路）：前倾 > 0.3°，停稳后 0.5 s（10 帧）内回到 ±0.2°
            CarFrame[] stopper = on.frames.Select(fr => fr.cars[2]).ToArray();
            int stopFrame = -1;
            for (int f = 51; f < 180; f++)
            {
                if ((stopper[f].pos - stopper[f - 1].pos).magnitude < 1e-4f)
                {
                    stopFrame = f;
                    break;
                }
            }
            float brake = stopFrame > 0 ? stopper.Skip(stopFrame - 2).Take(8).Max(c => c.pitch) : 0f;
            int settled = -1;
            if (stopFrame > 0)
            {
                for (int f = stopFrame; f < 180; f++)
                {
                    if (stopper.Skip(f).Take(180 - f).All(c => Mathf.Abs(c.pitch) <= 0.2f && Mathf.Abs(c.roll) <= 0.2f))
                    {
                        settled = f;
                        break;
                    }
                }
            }
            check(stopFrame > 0 && brake > 0.3f && settled >= 0 && (settled - stopFrame) * 0.05f <= 0.5f,
                $"急停（{stopper[0].role}）：第 {stopFrame} 帧停住，前倾峰值 {brake:0.00}°，{(settled - stopFrame) * 0.05f:0.00} s 后俯仰 / 侧倾回到 ±0.2°（限 0.5 s）");
            // 暂停（第 185～194 帧）：变换不变
            bool pausedStill = true;
            for (int f = 187; f < 195; f++)
            {
                for (int i = 0; i < cars; i++)
                {
                    CarFrame p = on.frames[f - 1].cars[i], q = on.frames[f].cars[i];
                    pausedStill &= p.yaw == q.yaw && p.pitch == q.pitch && p.roll == q.roll && p.y == q.y;
                }
            }
            check(on.frames[190].paused && pausedStill, "暂停期间（第 187～194 帧）全部机器车身姿态逐位不变");
            // 装甲机器（若有）：画面朝向与内核 Facing 偏差
            var armored = Enumerable.Range(0, cars).Where(i => on.frames.Any(fr => fr.cars[i].armored)).ToArray();
            Line(check, armored.Length == 0 ? "本场景机器都没有正面装甲（装甲朝向收敛由 VehRig 段合成路径断言）" : $"带正面装甲 {armored.Length} 台");
            // 幅度 3 档：峰值俯仰随档位单调
            float Peak(PatrolRun r) => r.frames.SelectMany(fr => fr.cars).Max(c => Mathf.Abs(c.pitch));
            check(soft.error == null && strong.error == null && Peak(soft) < Peak(on) && Peak(on) < Peak(strong),
                $"幅度 3 档对比片：峰值俯仰 0.5 档 {Peak(soft):0.00}° < 1 档 {Peak(on):0.00}° < 1.6 档 {Peak(strong):0.00}°");
        }

        private static void Line(Action<bool, string> check, string text) => check(true, "（信息）" + text);

        /// <summary>gpu 入口：VehStress120 开 / 关 CPU（宿主 TickManual）与 GPU 毫秒增量，只测一次，PerfGate 记账（超线不到 2 倍记警告）。</summary>
        public static void StressFromCommandLine()
        {
            var sb = new StringBuilder();
            int fails = 0;
            void Exp(bool ok, string what)
            {
                sb.AppendLine((ok ? "  ✓ " : "  ✗ ") + what);
                fails += ok ? 0 : 1;
            }
            try
            {
                (double off50, double off95, int nOff) = CpuPass(false);
                (double on50, double on95, int nOn) = CpuPass(true);
                PerfGate.Expect(nOn > 0 && nOn == nOff, $"VehStress120 宿主 TickManual CPU（{nOn} 台、预热 30 帧、测 120 帧）：关 p50 {off50:0.000} / p95 {off95:0.000} ms；开 p50 {on50:0.000} / p95 {on95:0.000} ms",
                    new[] { PerfGate.Le(on50 - off50, 0.15, "pl.vehrig CPU p50 增量 ms") }, Exp, s => sb.AppendLine(s));
                PresentationCapture.GpuPair g = PresentationCapture.MeasureGpuPair("VehStress120", PresentationFeatureRegistry.VehRigId);
                PerfGate.Expect(true, "VehStress120 GPU 毫秒（1920×1080 渲染 + 等 GPU）：" + g,
                    new[] { PerfGate.Le(g.DeltaP50, 0.1, "pl.vehrig GPU p50 增量 ms（≈0）") }, Exp, s => sb.AppendLine(s));
            }
            catch (Exception e)
            {
                Exp(false, "抛异常：" + e);
            }
            sb.AppendLine($"[PL 采集] VehStress 结论：{(fails == 0 ? "通过" : $"失败 {fails} 项")}");
            Debug.Log(sb.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fails == 0 ? 0 : 1);
            }
        }

        private static (double p50, double p95, int cars) CpuPass(bool on)
        {
            var c = new PresentationCaptureContext { FeatureOn = on };
            try
            {
                VehStress120(c);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, on);
                foreach (IPresentationModule m in c.Modules)
                {
                    PresentationHost.Register(m);
                }
                var ms = new List<double>();
                var sw = new Stopwatch();
                for (int f = 0; f < 150; f++)
                {
                    FgMachineMorphSelfCheck.EvidenceFrames(1);
                    sw.Restart();
                    PresentationHostBridge.TickManual(PresentationCapture.Dt, GameClock.FrameScaledDt);
                    sw.Stop();
                    if (f >= 30)
                    {
                        ms.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
                ms.Sort();
                return (ms[ms.Count / 2], ms[(int)(ms.Count * 0.95)], VehicleRigSystem.Count);
            }
            finally
            {
                foreach (IPresentationModule m in c.Modules)
                {
                    PresentationHost.Unregister(m);
                }
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                if (c.Evidence)
                {
                    FgMachineMorphSelfCheck.EvidenceEnd();
                }
                VehicleRigSystem.Clear();
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
            }
        }
    }
}
