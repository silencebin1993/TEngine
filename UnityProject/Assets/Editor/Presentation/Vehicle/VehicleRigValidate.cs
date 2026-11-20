// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Presentation;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View;
using GameLogic.View.Presentation;
using Unity.Mathematics;
using UnityEngine;
using static GameLogic.EditorTools.PresentationValidate;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-VEH-02 自检段 VehRig（PresentationValidate.Segments 末尾）：合成路径上的动作曲线（转速上限、装甲朝向收敛、起步抬头 / 急停前倾、停稳回零、
    /// 转弯侧倾、悬浮浮动、固定型不动、暂停冻结、2 倍速时长减半、冲量、信号、挂点回退）、开关复位、零 GC、120 台 CPU；
    /// 真战役（家园证据入口）里接缝登记 / 注销成对、节点就是 Silhouette / MorphRig、不改层级、MorphRig 重建只换那一台、观察切换 20 次不涨、
    /// 以及功能开 / 关 sim 哈希一致（PresentationSimHash.CheckOnOff 传本模块）。
    /// 跑法：<c>cd /d/BinGames-PL &amp;&amp; PL_VALIDATE_SEGMENTS=VehRig bash tools/pl/pl-unity.sh validate GameLogic.EditorTools.PresentationValidate.RunAll</c>
    /// </summary>
    public static class VehicleRigValidate
    {
        private const float Frame = 1f / 60f;

        public static void VehRig()
        {
            FeatureAndProfile();
            Synthetic();
            GroundClearance();
            InWorld();
            var module = new VehicleRigModule();
            VehicleRigAdapter.Install();
            Expect(PresentationSimHash.CheckOnOff(out string d, module), "开关 sim 哈希一致（pl.vehrig，适配已装、家园机器登记进 Rig）：" + d);
            VehicleRigSystem.Clear();
        }

        // ── 登记与档位 ──

        private static void FeatureAndProfile()
        {
            PresentationFeature f = PresentationFeatureRegistry.Features.FirstOrDefault(x => x.Id == PresentationFeatureRegistry.VehRigId);
            Expect(f != null && f.DefaultPL && !f.DefaultMain && f.Switch == nameof(VehicleRigModule) && f.TierKey == "VehicleRig",
                "pl.vehrig 已登记：开关 VehicleRigModule、PL 分支开、合主线关、档位 VehicleRig");
            Expect(PresentationHostBridge.GameModules.Any(m => m is VehicleRigModule), "正式游戏模块表 GameModulesTable 里有 VehicleRigModule");
            string path = RepoFile("TEngine/UnityProject/Assets/GameScripts/Main/Presentation/Profiles/VehicleRig.json");
            VehicleRigProfileFile file = path != null ? VehicleRigProfiles.Parse(File.ReadAllText(path)) : null;
            string a = file != null ? JsonUtility.ToJson(file) : "", b = JsonUtility.ToJson(VehicleRigProfiles.Defaults());
            Expect(file != null && a == b, $"档位文件 Profiles/VehicleRig.json 与代码默认值逐字段相同（{(a == b ? "相同" : "文件 " + a + " ↔ 代码 " + b)}）");
            VehicleRigProfileFile def = VehicleRigProfiles.Defaults();
            Expect(def.track.turnRateDeg == 180f && def.wheel.turnRateDeg == 240f && def.hover.turnRateDeg == 300f && def.@fixed.turnRateDeg == 0f
                   && def.hover.bobMeters == 0.03f && def.hover.bobHz == 0.6f && def.wheel.maxPitchDeg == 6f && def.wheel.brakePitchDeg == 4f
                   && def.wheel.maxRollDeg == 4f && def.wheel.settleSeconds == 0.35f,
                "档位值按卡片：转速 履带 180 / 轮式 240 / 悬浮 300 / 固定 0 °/s；点头限 6°、刹车前倾 4°、侧倾限 4°、回零 0.35 s；悬浮 0.6 Hz ±0.03 m");
        }

        // ── 合成路径（无世界） ──

        private sealed class Car
        {
            public int Key;
            public GameObject Root;
            public Transform A, B;
            public VehicleArchetype Arch;
            public bool Armored;
        }

        private static Car MakeCar(int key, VehicleArchetype arch, bool armored = false)
        {
            var root = new GameObject("pl_vehrig_test_" + key);
            var a = new GameObject(PlaceholderSilhouette.PartsName).transform;
            a.SetParent(root.transform, false);
            var b = new GameObject(MachineMorphView.RigName).transform;
            b.SetParent(root.transform, false);
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(a, false);
            body.transform.localPosition = new Vector3(0f, -0.62f, 0f);
            body.transform.localScale = new Vector3(1.5f, 0.5f, 1.8f);
            VehicleRigSystem.Register(key, root, null, 0, arch, a, b);
            return new Car { Key = key, Root = root, A = a, B = b, Arch = arch, Armored = armored };
        }

        /// <summary>直行 1 秒（+Z，6 米/秒）→ 原地掉头倒回 1.5 秒（−Z）→ 停住。</summary>
        private static double2 Patrol(double t) =>
            t < 1.0 ? new double2(0, 6 * t) : t < 2.5 ? new double2(0, 6 - 6 * (t - 1)) : new double2(0, -3);

        private static double2 Circle(double t) => new double2(4 * math.sin(1.5 * t), 4 * math.cos(1.5 * t)); // 6 米/秒、1.5 弧度/秒

        private struct Sample
        {
            public double T;
            public float Yaw, Pitch, Roll, Y;
        }

        private static void Feed(Car c, Func<double, double2> path, double t, double gdt)
        {
            double2 prev = path(t - gdt), pos = path(t);
            float2 mv = (float2)(pos - prev);
            float2 facing = math.lengthsq(mv) > 1e-10f ? math.normalize(mv) : float2.zero;
            VehicleRigSystem.SetSyntheticInput(c.Key, prev, pos, facing, c.Armored);
        }

        private static List<Sample>[] Drive(Car[] cars, Func<double, double2>[] paths, int frames, float gdtPerFrame, double t0 = 0)
        {
            var logs = cars.Select(_ => new List<Sample>(frames)).ToArray();
            for (int f = 0; f < frames; f++)
            {
                double t = t0 + f * (double)gdtPerFrame;
                for (int i = 0; i < cars.Length; i++)
                {
                    Feed(cars[i], paths[i], t, gdtPerFrame);
                }
                PresentationHost.Tick(new PresentationFrame(Frame, gdtPerFrame, t, 0, 0f, false, false, null, null, gdtPerFrame / Frame, 1f, t));
                for (int i = 0; i < cars.Length; i++)
                {
                    logs[i].Add(new Sample
                    {
                        T = t, Yaw = VehicleRigSystem.YawDegOf(cars[i].Key), Pitch = VehicleRigSystem.PitchDegOf(cars[i].Key),
                        Roll = VehicleRigSystem.RollDegOf(cars[i].Key), Y = cars[i].A.localPosition.y,
                    });
                }
            }
            return logs;
        }

        private static float AngleDiff(float a, float b) => Mathf.Abs(Mathf.DeltaAngle(a, b));

        private static void Synthetic()
        {
            var module = new VehicleRigModule();
            var cars = new List<Car>();
            try
            {
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, true);
                PresentationHost.Register(module);
                VehicleRigSystem.Clear();
                Expect(PresentationHost.IsActive(module), "VehicleRigModule 登记进宿主后启用");

                Car track = MakeCar(1, VehicleArchetype.Track), wheel = MakeCar(2, VehicleArchetype.Wheel), hover = MakeCar(3, VehicleArchetype.Hover);
                Car fixedCar = MakeCar(4, VehicleArchetype.Fixed), armored = MakeCar(5, VehicleArchetype.Track, true), circle = MakeCar(6, VehicleArchetype.Wheel);
                Car[] set = { track, wheel, hover, fixedCar, armored, circle };
                cars.AddRange(set);
                var paths = new Func<double, double2>[] { Patrol, Patrol, Patrol, Patrol, Patrol, Circle };
                List<Sample>[] log = Drive(set, paths, 240, Frame); // 4 游戏秒
                VehicleRigProfileFile prof = VehicleRigProfiles.Current;

                // 1. 每帧偏航变化 ≤ 档位转速 × dt + 0.5°
                var worst = new StringBuilder();
                bool rateOk = true;
                for (int i = 0; i < set.Length; i++)
                {
                    float rate = VehicleRigProfiles.Of(prof, set[i].Arch).turnRateDeg;
                    float maxStep = 0f;
                    for (int k = 1; k < log[i].Count; k++)
                    {
                        maxStep = Mathf.Max(maxStep, AngleDiff(log[i][k].Yaw, log[i][k - 1].Yaw));
                    }
                    rateOk &= maxStep <= rate * Frame + 0.5f;
                    worst.Append($"{set[i].Arch}#{set[i].Key} {maxStep:0.00}°/帧（限 {rate * Frame + 0.5f:0.00}）；");
                }
                Expect(rateOk, "每帧偏航变化 ≤ 档位转速 × dt + 0.5°：" + worst);

                // 2. 装甲机器：Facing 翻 180° 后，画面朝向 ≤ 5° 的时间 ≤ 180°/转速 + 0.25 s
                float tConv = float.NaN;
                for (int k = 0; k < log[4].Count; k++)
                {
                    if (log[4][k].T > 1.0 && AngleDiff(log[4][k].Yaw, 180f) <= 5f)
                    {
                        tConv = (float)(log[4][k].T - 1.0);
                        break;
                    }
                }
                float convLimit = 180f / prof.track.turnRateDeg + 0.25f;
                Expect(!float.IsNaN(tConv) && tConv <= convLimit,
                    $"带正面装甲的履带机：内核 Facing 在 t=1 s 翻 180° 后 {tConv:0.000} s 画面朝向收敛到 ≤ 5°（限 180/转速 + 0.25 = {convLimit:0.00} s）");
                bool trackLag = log[0].Any(s => s.T > 1.05 && s.T < 1.5 && AngleDiff(s.Yaw, 180f) > 20f);
                Expect(trackLag && AngleDiff(log[0].Last().Yaw, 180f) <= 1f, $"掉头时车身先转过去（t∈(1.05,1.5) 偏差 > 20° 的帧存在 = {trackLag}），最后朝向 {log[0].Last().Yaw:0.0}°（应 ±180）");

                // 3. 起步抬头、急停前倾
                float startMin = log[1].Where(s => s.T <= 0.5).Min(s => s.Pitch), brakeMax = log[1].Where(s => s.T >= 2.5 && s.T <= 3.0).Max(s => s.Pitch);
                float pitchAbsMax = log.Take(5).SelectMany(l => l).Max(s => Mathf.Abs(s.Pitch));
                Expect(startMin <= -1f && brakeMax >= 1f && pitchAbsMax <= 6f + 0.01f,
                    $"轮式起步车头上抬（最小俯仰 {startMin:0.00}° ≤ −1）、急停前倾（最大 {brakeMax:0.00}° ≥ 1），全程 |俯仰| ≤ 6°（{pitchAbsMax:0.00}）");

                // 4. 停稳 0.5 s 内俯仰 / 侧倾回到 ±0.2°（t ≥ 2.5 停住）
                var settle = new StringBuilder();
                bool settleOk = true;
                for (int i = 0; i < 5; i++)
                {
                    double last = log[i].Where(s => s.T >= 2.5 && (Mathf.Abs(s.Pitch) > 0.2f || Mathf.Abs(s.Roll) > 0.2f)).Select(s => s.T).DefaultIfEmpty(2.5).Max();
                    settleOk &= last - 2.5 <= 0.5;
                    settle.Append($"{set[i].Arch}#{set[i].Key} {last - 2.5:0.000}s；");
                }
                Expect(settleOk, "停稳后 0.5 s 内俯仰 / 侧倾回到 ±0.2°（最后一次超出距停住）：" + settle);

                // 5. 转弯侧倾（∝ 角速度 × 速度，限 ±4°）
                float rollMax = log[5].Where(s => s.T > 1.0).Max(s => Mathf.Abs(s.Roll));
                float rollAll = log.SelectMany(l => l).Max(s => Mathf.Abs(s.Roll));
                Expect(rollMax >= 1f && rollAll <= 4f + 0.01f, $"绕圈（6 米/秒、1.5 弧度/秒）侧倾 {rollMax:0.00}°（≥ 1；按档位 0.3×1.5×6 = 2.7），全程 |侧倾| ≤ 4°（{rollAll:0.00}）");

                // 6. 悬浮浮动、固定型不动
                // 停稳后（t > 3 s，俯仰 / 侧倾已回零）才只剩浮动；行驶中 y 还含俯仰转轴补偿与接地抬高
                float bobMax = log[2].Where(s => s.T > 3f).Max(s => Mathf.Abs(s.Y));
                Expect(bobMax >= 0.025f && bobMax <= 0.035f,
                    $"悬浮机停住后仍在浮动：t > 3 s 的 |y| 最大 {bobMax:0.0000} m（振幅 0.03）");
                Expect(log[3].All(s => s.Yaw == 0f && s.Pitch == 0f && s.Roll == 0f) && fixedCar.A.localRotation == Quaternion.identity && fixedCar.A.localPosition == Vector3.zero,
                    "固定型（转速 0）全程不转、不点头、不倾、不浮，节点保持单位姿态");

                // 7. 两节点写相同局部变换
                Expect(set.All(c => c.A.localRotation == c.B.localRotation && c.A.localPosition == c.B.localPosition), "Silhouette 与 MorphRig 两节点写相同的 localRotation / localPosition（原地转，不改层级）");

                // 8. 信号表
                VehicleRigSystem.TryGetSignals(circle.Key, out VehicleRigSignals sig);
                Expect(Mathf.Abs(sig.MoveSpeed - 6f) < 0.3f && Mathf.Abs(Mathf.Abs(sig.TurnRate) - 86f) < 8f && Mathf.Abs(sig.SteerAngle) > 1f && Mathf.Abs(sig.SteerAngle) <= 35f,
                    $"信号表（绕圈轮式）：move_speed {sig.MoveSpeed:0.00} m/s（≈6）、turn_rate {sig.TurnRate:0.0} °/s（≈±86）、steer_angle {sig.SteerAngle:0.0}°（≤ 35）");

                // 9. 暂停：游戏时间不走 30 帧，变换逐位不变
                Snapshot(set, out Quaternion[] r0, out Vector3[] p0);
                double tEnd = 239 * (double)Frame;
                for (int k = 0; k < 30; k++)
                {
                    PresentationHost.Tick(new PresentationFrame(Frame, 0f, tEnd, 0, 0f, true, false, null, null, 1f, 1f, tEnd));
                }
                Snapshot(set, out Quaternion[] r1, out Vector3[] p1);
                Expect(r0.SequenceEqual(r1) && p0.SequenceEqual(p1), "暂停 30 帧（GameDt = 0、连续游戏秒不变）：全部节点 localRotation / localPosition 逐位不变");

                // 10. 冲量（给 MORPH-01 / VEH-05）
                Drive(new[] { wheel }, new Func<double, double2>[] { _ => new double2(0, -3) }, 60, Frame, tEnd + Frame); // 先停稳
                float before = NosePitch(wheel.A);
                VehicleRigSystem.AddImpulse(wheel.Key, 3f, 0.05f, 0.3f);
                Drive(new[] { wheel }, new Func<double, double2>[] { _ => new double2(0, -3) }, 1, Frame, tEnd + 61 * Frame);
                float kick = NosePitch(wheel.A) - before;
                Drive(new[] { wheel }, new Func<double, double2>[] { _ => new double2(0, -3) }, 24, Frame, tEnd + 62 * Frame);
                float after = NosePitch(wheel.A) - before;
                Expect(kick >= 2f && kick <= 3.01f && Mathf.Abs(after) < 0.05f, $"冲量 AddImpulse(3°, 0.05 m, 0.3 s)：下一帧俯仰 +{kick:0.00}°（2～3），0.4 s 后 {after:0.000}°（回零）");

                // 11. 挂点：白模无节点按剪影包围盒回退（只算坐标、不建节点）；有节点 / 三段键时用节点
                int children = track.Root.transform.childCount, aChildren = track.A.childCount;
                bool ui = RigSockets.TryResolve(track.Root.transform, track.A, RigSockets.UiTop, out Vector3 top);
                bool hit = RigSockets.TryResolve(track.Root.transform, track.A, RigSockets.Hit, out Vector3 center);
                bool ex = RigSockets.TryResolve(track.Root.transform, track.A, RigSockets.ExhaustFirst, out Vector3 exhaust);
                Vector3 lt = track.A.InverseTransformPoint(top), lc = track.A.InverseTransformPoint(center), le = track.A.InverseTransformPoint(exhaust);
                Expect(ui && hit && ex && Near(lt, new Vector3(0f, -0.37f + 0.10f, 0f)) && Near(lc, new Vector3(0f, -0.62f, 0f)) && Near(le, new Vector3(0f, -0.37f, -0.9f))
                       && track.Root.transform.childCount == children && track.A.childCount == aChildren,
                    $"挂点回退（剪影局部）：skt_ui_top {lt}（顶面上 0.10）、skt_hit {lc}（中心）、skt_fx_exhaust_01 {le}（车尾上缘）；没新建节点");
                var weapon = new GameObject("skt_weapon").transform;
                weapon.SetParent(track.B, false);
                var asset = new GameObject("mch_player_repeater_gatling").transform;
                asset.SetParent(weapon, false);
                var muzzle = new GameObject("skt_muzzle").transform;
                muzzle.SetParent(asset, false);
                var hitNode = new GameObject(RigSockets.Hit).transform;
                hitNode.SetParent(track.B, false);
                hitNode.localPosition = new Vector3(0f, 1f, 0f);
                RigSockets.TryResolve(track.Root.transform, track.A, RigSockets.Hit, out Vector3 hitByNode);
                Expect(RigSockets.Find(track.Root.transform, "skt_weapon/mch_player_repeater_gatling/skt_muzzle") == muzzle
                       && RigSockets.Find(track.Root.transform, "/" + MachineMorphView.RigName + "/skt_hit") == hitNode && hitByNode == hitNode.position
                       && RigSockets.Find(track.Root.transform, "skt_nothing") == null,
                    "挂点查表：三段键 skt_weapon/mch_player_repeater_gatling/skt_muzzle、挂点段为空的 /MorphRig/skt_hit 都找到；有节点时 TryResolve 用节点；找不到返回 null");
                Object.DestroyImmediate(weapon.gameObject);
                Object.DestroyImmediate(hitNode.gameObject);

                // 12. 注销复位；功能关 → 两节点复位成单位姿态；再开不残留
                Car extra = MakeCar(7, VehicleArchetype.Track);
                cars.Add(extra);
                Drive(new[] { extra }, new Func<double, double2>[] { t => new double2(6 * t, 0) }, 30, Frame, 10.0);
                bool turned = extra.A.localRotation != Quaternion.identity;
                VehicleRigSystem.Unregister(extra.Key, extra.Root);
                Expect(turned && extra.A.localRotation == Quaternion.identity && extra.B.localRotation == Quaternion.identity && !VehicleRigSystem.IsRegistered(extra.Key),
                    $"注销（DetachView）时两节点复位（注销前已转向 = {turned}）");
                Drive(set, paths, 10, Frame, 5.0);
                bool anyTurned = set.Any(c => c.A.localRotation != Quaternion.identity);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, false);
                PresentationHost.Tick(PresentationFrame.Synthetic(Frame, 6.0));
                Expect(anyTurned && !PresentationHost.IsActive(module) && set.All(c => c.A.localRotation == Quaternion.identity && c.B.localRotation == Quaternion.identity
                                                                                 && c.A.localPosition == Vector3.zero && c.B.localPosition == Vector3.zero),
                    "关开关：模块停用，全部节点 localRotation 复位为 identity、局部位置复位（关前有转动的 = " + anyTurned + "）");
                Expect(VehicleRigSystem.Count == set.Length && set.All(c => c.Root.transform.childCount == 2), "关开关后登记保留（只存引用）、机器表现子节点数不变");
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, true);
                Drive(set, paths, 5, Frame, 6.0);
                Expect(PresentationHost.IsActive(module) && !PresentationHost.IsFaulted(module), "再开：模块重新启用、无异常");

                // 12b. 修复轮 1：读档（Reload）后不带上一局的朝向；观察切换（SiteSwitch）保留
                float seedReload = SeedAfterRebuild(RebuildReason.Reload, 31), seedSwitch = SeedAfterRebuild(RebuildReason.SiteSwitch, 32);
                Expect(seedReload < 1f && Mathf.Abs(seedSwitch - 90f) < 1f,
                    $"同一帧摘下旧局 / 挂上新局（同 LogicId）：Reload 后停着的机器朝 {seedReload:0.0}°（不带上一局的 90°），SiteSwitch 后仍朝 {seedSwitch:0.0}°");

                // 12c. 修复轮 1：同时登记第二个实例不重复积分（静态注册表只让第一个实例工作）
                var second = new VehicleRigModule();
                try
                {
                    PresentationHost.Register(second);
                    Drive(set, paths, 3, Frame, 7.0);
                    Expect(PresentationHost.IsActive(second) && !second.IsOwner && second.LastJobCount == 0 && module.IsOwner && module.LastJobCount == set.Length,
                        $"第二个 VehicleRigModule 同时启用：它不驱动（算了 {second.LastJobCount} 台），第一个照常（{module.LastJobCount} 台）");
                }
                finally
                {
                    PresentationHost.Unregister(second);
                }
                Drive(set, paths, 2, Frame, 7.1);
                Expect(module.IsOwner && module.LastJobCount == set.Length, "第二个实例注销后第一个仍在工作、没被它的 OnDisable 复位");

                // 13. 2 倍速：动作按游戏时间走，掉头所需真实帧数减半（±10%）
                VehicleRigSystem.Clear();
                Car u1 = MakeCar(11, VehicleArchetype.Track);
                cars.Add(u1);
                int n1 = UTurnFrames(u1, 1f);
                VehicleRigSystem.Clear();
                Car u2 = MakeCar(12, VehicleArchetype.Track);
                cars.Add(u2);
                int n2 = UTurnFrames(u2, 2f);
                float ratio = n1 > 0 ? (float)n2 / n1 : 0f;
                Expect(n1 > 0 && ratio >= 0.45f && ratio <= 0.55f, $"2 倍速：掉头 180° 用 {n2} 帧，1 倍速 {n1} 帧，比 {ratio:0.000}（0.5 ± 10%）");

                // 14. 零 GC + 120 台 CPU（只测一次，超线记警告）
                VehicleRigSystem.Clear();
                var many = new Car[120];
                for (int i = 0; i < many.Length; i++)
                {
                    many[i] = MakeCar(100 + i, (VehicleArchetype)(i % 3));
                    many[i].Root.transform.position = new Vector3(i % 12 * 4f, 0f, i / 12 * 4f);
                }
                cars.AddRange(many);
                double tt = 20.0;
                Func<double, double2> circlePath = Circle;
                void Body()
                {
                    for (int f = 0; f < 60; f++)
                    {
                        tt += Frame;
                        for (int i = 0; i < many.Length; i++)
                        {
                            VehicleRigSystem.SetSyntheticInput(many[i].Key, circlePath(tt - Frame), circlePath(tt), float2.zero, false);
                        }
                        PresentationHost.Tick(new PresentationFrame(Frame, Frame, tt, 0, 0f, false, false, null, null, 1f, 1f, tt));
                    }
                }
                Expect(MeasureZeroAlloc(Body, out string gc), "120 台合成机器 60 帧（喂输入 + 宿主 Tick + 两个 Burst 作业）零 GC 分配：" + gc);
                var sw = new Stopwatch();
                const int perfFrames = 600;
                var ms = new List<double>(perfFrames);
                for (int f = 0; f < perfFrames; f++)
                {
                    tt += Frame;
                    for (int i = 0; i < many.Length; i++)
                    {
                        VehicleRigSystem.SetSyntheticInput(many[i].Key, circlePath(tt - Frame), circlePath(tt), float2.zero, false);
                    }
                    sw.Restart();
                    module.Tick(new PresentationFrame(Frame, Frame, tt, 0, 0f, false, false, null, null, 1f, 1f, tt));
                    sw.Stop();
                    ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                ms.Sort();
                PerfGate.Expect(module.LastJobCount == 120, $"120 台 VehicleRigModule.Tick（Editor，含两次作业调度 + Complete）：p50 {ms[ms.Count / 2]:0.000} ms、p95 {ms[(int)(ms.Count * 0.95)]:0.000} ms",
                    new[] { PerfGate.Le(ms[ms.Count / 2], 0.15, "pl.vehrig 120 台 CPU p50 ms") }, Expect, Line);
            }
            finally
            {
                PresentationHost.Unregister(module);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                VehicleRigSystem.Clear();
                foreach (Car c in cars)
                {
                    if (c.Root != null)
                    {
                        Object.DestroyImmediate(c.Root);
                    }
                }
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
            }
        }

        // 节点的实际俯仰（度，正 = 车头下沉）：按车头方向的俯角算，不受偏航取值范围影响
        private static float NosePitch(Transform t) => -Mathf.Asin(Mathf.Clamp((t.localRotation * Vector3.forward).y, -1f, 1f)) * Mathf.Rad2Deg;

        // 一台履带开向 +X（偏航 90°）后摘下，再以同一 LogicId 挂上（同一帧换局），宿主处理 reason 后停着走几帧：返回 |偏航|（度）
        private static float SeedAfterRebuild(RebuildReason reason, int key)
        {
            Car c = MakeCar(key, VehicleArchetype.Track);
            try
            {
                Drive(new[] { c }, new Func<double, double2>[] { t => new double2(6 * t, 0) }, 40, Frame, 30.0);
                VehicleRigSystem.Unregister(key, c.Root);
                VehicleRigSystem.Register(key, c.Root, null, 0, VehicleArchetype.Track, c.A, c.B);
                PresentationHost.RequestRebuild(reason);
                Drive(new[] { c }, new Func<double, double2>[] { _ => new double2(5, 0) }, 5, Frame, 31.0);
                return Mathf.Abs(Mathf.DeltaAngle(0f, VehicleRigSystem.YawDegOf(key)));
            }
            finally
            {
                VehicleRigSystem.Unregister(key);
                VehicleRigSystem.ForgetYaw();
                Object.DestroyImmediate(c.Root);
            }
        }

        /// <summary>
        /// 修复轮 1（开状态下的剪影几何）：真实白模（PlaceholderSilhouette.ApplyMachine，同 SilhouetteSelfCheck 的三种玩家机器）登记进 Rig（只给 A、B 传 null），
        /// 幅度 1 与 1.6 档各跑起步 / 掉头 / 急停 / 绕圈，每帧按 SilhouetteSelfCheck 同一算法（部件网格包围盒 8 角换到胶囊局部）取包围盒：底 ≥ −1.05、顶 ≤ 1.35。
        /// </summary>
        private static void GroundClearance()
        {
            var module = new VehicleRigModule();
            var made = new List<GameObject>();
            var line = new StringBuilder();
            bool ok = true, bNullOk = true;
            float worstBottom = 0f, worstTop = -9f, peakPitch = 0f, peakRoll = 0f, maxH = 0f;
            try
            {
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, true);
                PresentationHost.Register(module);
                VehicleRigSystem.Clear();
                (string label, string chassis, VehicleArchetype arch)[] kinds =
                {
                    ("轮式机", HomeValleyLayout.Erc001ChassisId, VehicleArchetype.Wheel),
                    ("履带战斗机", HomeValleyLayout.Erc003ChassisId, VehicleArchetype.Track),
                    ("悬浮机", GameLogic.Campaign.Content.ChassisCatalog.ChassisHoverId, VehicleArchetype.Hover),
                };
                foreach (float amp in new[] { 1f, 1.6f })
                {
                    VehicleRigProfileFile prof = VehicleRigProfiles.Defaults();
                    prof.amplitude = amp;
                    VehicleRigProfiles.Apply(prof);
                    VehicleRigSystem.Clear();
                    var cars = new List<(string label, Transform hitbox, Transform parts, Car car)>();
                    for (int k = 0; k < kinds.Length; k++)
                    {
                        for (int p = 0; p < 2; p++)
                        {
                            GameObject hb = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                            made.Add(hb);
                            PlaceholderSilhouette.ApplyMachine(hb, hb.GetComponent<Renderer>(), kinds[k].chassis);
                            Transform parts = hb.transform.Find(PlaceholderSilhouette.PartsName);
                            int key = 200 + k * 2 + p;
                            VehicleRigSystem.Register(key, hb, null, 0, kinds[k].arch, parts, null);
                            cars.Add((kinds[k].label, hb.transform, parts, new Car { Key = key, Root = hb, A = parts, Arch = kinds[k].arch }));
                        }
                    }
                    Car[] set = cars.Select(c => c.car).ToArray();
                    var paths = new Func<double, double2>[set.Length];
                    for (int i = 0; i < set.Length; i++)
                    {
                        paths[i] = i % 2 == 0 ? (Func<double, double2>)Patrol : Circle;
                    }
                    for (int f = 0; f < 240; f++)
                    {
                        Drive(set, paths, 1, Frame, f * (double)Frame);
                        foreach ((string label, Transform hitbox, Transform parts, Car car) c in cars)
                        {
                            Bounds b = PartsBounds(c.parts, c.hitbox);
                            peakPitch = Mathf.Max(peakPitch, Mathf.Abs(VehicleRigSystem.PitchDegOf(c.car.Key)));
                            peakRoll = Mathf.Max(peakRoll, Mathf.Abs(VehicleRigSystem.RollDegOf(c.car.Key)));
                            maxH = Mathf.Max(maxH, b.size.y);
                            worstBottom = Mathf.Min(worstBottom, b.min.y);
                            worstTop = Mathf.Max(worstTop, b.max.y);
                            if (b.min.y < -1.05f || b.max.y > 1.35f)
                            {
                                if (ok)
                                {
                                    line.Append($"{c.label} 幅度 {amp} 第 {f} 帧 底 {b.min.y:0.000} 顶 {b.max.y:0.000}；");
                                }
                                ok = false;
                            }
                        }
                    }
                    bNullOk &= !PresentationHost.IsFaulted(module) && set.All(c => c.A.localRotation != Quaternion.identity);
                }
                Expect(ok && peakPitch >= 3f && peakRoll >= 2f,
                    $"开状态（幅度 1 / 1.6，峰值俯仰 {peakPitch:0.00}°、侧倾 {peakRoll:0.00}°）三种玩家白模每帧包围盒：最低 {worstBottom:0.000}（限 ≥ −1.05，SilhouetteSelfCheck 同判据）、最高 {worstTop:0.000}（限 ≤ 1.35）" +
                    $"；最大高度 {maxH:0.00}（SilhouetteSelfCheck 的“玩家矮宽 h ≤ 1.1”按静止白模判，转动中只记录）{(ok ? "" : "——超限：" + line)}");
                Expect(bNullOk, "只登记一个节点（B 传 null，正式资产只写 p_hull 的接法）：TransformAccessArray 含 null 不抛、模块没被停用、A 照常写");
            }
            finally
            {
                PresentationHost.Unregister(module);
                VehicleRigProfiles.Apply(VehicleRigProfiles.Defaults());
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                VehicleRigSystem.Clear();
                foreach (GameObject go in made)
                {
                    if (go != null)
                    {
                        Object.DestroyImmediate(go);
                    }
                }
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
            }
        }

        // 同 SilhouetteSelfCheck.LocalBounds：部件网格包围盒 8 个角换到胶囊局部坐标后取轴对齐包围盒
        private static Bounds PartsBounds(Transform parts, Transform hitbox)
        {
            var result = new Bounds();
            bool first = true;
            foreach (MeshFilter f in parts.GetComponentsInChildren<MeshFilter>(true))
            {
                Bounds mesh = f.sharedMesh.bounds;
                Matrix4x4 toLocal = hitbox.worldToLocalMatrix * f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = toLocal.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
                    if (first)
                    {
                        result = new Bounds(corner, Vector3.zero);
                        first = false;
                    }
                    else
                    {
                        result.Encapsulate(corner);
                    }
                }
            }
            return result;
        }

        private static void AimInCombatSite()
        {
            const float dt = 1f / 30f;
            const int key = 950;
            Car c = null;
            try
            {
                using (var site = new GameLogic.Campaign.Combat.CombatSite("plveh02-aim", GameLogic.Campaign.Combat.CombatSite.ConfigFromTuning()))
                {
                    CombatKernel k = site.Kernel;
                    string skirt = GameLogic.Campaign.Content.ComponentCatalog.StructArmorSkirtId;
                    BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, "comp_gun", null, skirt, Array.Empty<string>());
                    BlueprintCircuitPreview pv = BlueprintCircuitCompiler.CompilePreview(board);
                    CombatWeapon w = site.WithProgram(pv, GameLogic.Campaign.Combat.CombatSite.MachineWeaponFrom(pv));
                    GameLogic.Campaign.Combat.CombatSite.FlagFrontalArmor(skirt, out float frac, out float half);
                    const CombatUnitFlags live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
                    int me = k.Spawn(new CombatSpawn
                    {
                        ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = live,
                        Position = new double2(0, 0), Home = new double2(0, 0), Radius = 0.6f, Speed = 4f, Health = 5000f, MaxHealth = 5000f, Weapon = site.WeaponIndex(w),
                        BehaviorProfile = site.FlagBehaviorProfile("comp_gun", skirt), ArmorFraction = frac, ArmorHalfAngleDeg = half, ArmorFacing = new float2(0f, 1f), Priority = 1,
                    });
                    var foePos = new double2(6, 0); // 正右方：装甲 / 炮塔朝向应从 0° 转到 90°
                    int foe = k.Spawn(new CombatSpawn
                    {
                        ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                        Position = foePos, Home = foePos, Radius = 0.5f, Speed = 0f, Health = 1e7f, MaxHealth = 1e7f, BehaviorProfile = -1, Priority = 1,
                        Weapon = site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 1f, Damage = 0f, Cooldown = 1f }),
                    });
                    c = MakeCar(key, VehicleArchetype.Track, true);
                    VehicleRigSystem.Register(key, c.Root, k, me, VehicleArchetype.Track, c.A, c.B); // 换成真内核登记（同键覆盖合成登记）
                    k.TryGetUnit(me, out CombatUnitView v0);
                    bool cmd = k.IssueCommand(me, CombatCommandKind.Attack, foePos, foe, 0.3f, 20f, 1f, false);
                    double t = 50.0;
                    float worst = 0f, faceMax = 0f;
                    for (int f = 0; f < 90; f++) // 3 游戏秒
                    {
                        k.Step(dt, k.Time + dt);
                        t += dt;
                        PresentationHost.Tick(new PresentationFrame(Frame, dt, t, 0, 0f, false, false, null, null, 1f, 1f, t));
                        k.TryGetUnit(me, out CombatUnitView vf);
                        float fy = Mathf.Atan2(vf.Facing.x, vf.Facing.y) * Mathf.Rad2Deg;
                        faceMax = Mathf.Max(faceMax, AngleDiff(fy, 0f));
                        if (f >= 60)
                        {
                            worst = Mathf.Max(worst, AngleDiff(VehicleRigSystem.YawDegOf(key), fy));
                        }
                    }
                    k.TryGetUnit(me, out CombatUnitView v1);
                    float face1 = Mathf.Atan2(v1.Facing.x, v1.Facing.y) * Mathf.Rad2Deg;
                    double moved = math.distance(v0.Position, v1.Position);
                    k.TryGetUnit(foe, out CombatUnitView fv);
                    float dealt = 1e7f - fv.Health;
                    // 机器武器没有转速（只有炮塔武器设 TurnRate，TurretService.Sim.cs:391）：CombatLogic.cs:896 的 TurnToward 对机器不触发、:1673 只对 Turret，所以瞄准不改机器的装甲朝向
                    Expect(cmd && dealt > 0f && faceMax <= 1f && worst <= 5f,
                        $"装甲机站着瞄准（独立战斗地点真内核：履带 + 连射器 + 装甲裙板 {frac:0.##}，正右方 6 m 不还手靶子，攻击命令 {cmd}）：3 s 内打出伤害 {dealt:0.#}，内核装甲朝向 0° → {face1:0}°（最大转过 {faceMax:0}°，机器位移 {moved:0.00} m），" +
                        $"车身偏航与它最大偏差 {worst:0.0}°——机器开火不转装甲朝向，车身不随瞄准转（只有炮塔武器有转速）");
                    VehicleRigSystem.Unregister(key);
                }
            }
            finally
            {
                VehicleRigSystem.Unregister(key);
                if (c?.Root != null)
                {
                    Object.DestroyImmediate(c.Root);
                }
            }
        }

        private static int UTurnFrames(Car c, float speed)
        {
            float gdt = Frame * speed;
            Func<double, double2> path = t => t < 0.5 ? new double2(0, 6 * t) : new double2(0, 3 - 6 * (t - 0.5));
            List<Sample> log = Drive(new[] { c }, new[] { path }, (int)(3f / gdt), gdt)[0];
            int start = log.FindIndex(s => s.T > 0.5);
            int done = log.FindIndex(s => s.T > 0.5 && AngleDiff(s.Yaw, 180f) <= 1f);
            return start >= 0 && done >= 0 ? done - start : -1;
        }

        private static void Snapshot(Car[] set, out Quaternion[] r, out Vector3[] p)
        {
            r = set.SelectMany(c => new[] { c.A.localRotation, c.B.localRotation }).ToArray();
            p = set.SelectMany(c => new[] { c.A.localPosition, c.B.localPosition }).ToArray();
        }

        private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-4f;

        // ── 真战役（家园证据入口，不换场景） ──

        private static void InWorld()
        {
            var module = new VehicleRigModule();
            var prep = new StringBuilder();
            VehicleRigAdapter.Install();
            VehicleRigSystem.Clear();
            FgMachineMorphSelfCheck.EvidenceBegin(prep);
            try
            {
                CampaignState s = FgMachineMorphSelfCheck.EvidenceNewHome(PresentationSimHash.DefaultSeed);
                Expect(FgMachineMorphSelfCheck.EvidencePrepFailures == 0, "家园证据入口准备成功（新开局、家园被观察）" + (FgMachineMorphSelfCheck.EvidencePrepFailures > 0 ? "：" + prep : ""));
                int a = VehicleRigWorld.Spawn(HomeValleyLayout.Erc003ChassisId, new Vector2(4f, -4f));
                int w = VehicleRigWorld.Spawn(HomeValleyLayout.Erc001ChassisId, new Vector2(10f, -4f));
                int k = VehicleRigWorld.SpawnArmored(new Vector2(16f, -4f));
                VehicleRigWorld.Frames(2);

                // 修复轮 1：功能关（模块没登记 = 合主线 pl.host 关）时摘下 / 重挂不写 FG 节点——先假装 FG 给两节点写了局部变换，摘下再挂上后原样保留
                HomeValleyMachineMarker m0 = VehicleRigWorld.Marker(a);
                Transform s0 = m0?.View != null ? m0.View.transform.Find(PlaceholderSilhouette.PartsName) : null;
                Transform r0 = m0?.View != null ? m0.View.transform.Find(MachineMorphView.RigName) : null;
                bool offOk = false;
                if (s0 != null && r0 != null)
                {
                    Quaternion fgRot = Quaternion.Euler(0f, 0f, 7f);
                    Vector3 sp = s0.localPosition, rp = r0.localPosition, fgOff = new Vector3(0f, 0.1f, 0.05f);
                    s0.localRotation = fgRot;
                    r0.localRotation = fgRot;
                    s0.localPosition = sp + fgOff;
                    r0.localPosition = rp + fgOff;
                    MachineView v0 = m0.View;
                    PresentationHooks.OnMachineViewDetached(a, v0);
                    bool afterDetach = s0.localRotation == fgRot && r0.localRotation == fgRot && s0.localPosition == sp + fgOff && r0.localPosition == rp + fgOff;
                    PresentationHooks.OnMachineViewAttached(m0);
                    PresentationHooks.OnMachineViewAttached(m0); // 同键重登记也走 RemoveAt
                    offOk = afterDetach && s0.localRotation == fgRot && r0.localRotation == fgRot && s0.localPosition == sp + fgOff && VehicleRigSystem.IsRegistered(a);
                    s0.localRotation = Quaternion.identity;
                    r0.localRotation = Quaternion.identity;
                    s0.localPosition = sp;
                    r0.localPosition = rp;
                    PresentationHooks.OnMachineViewAttached(m0); // 按原位重登记（Base = 原局部位置）
                }
                Expect(offOk, "功能关（Rig 模块没启用）时：摘下 / 同键重挂都不改 FG 节点的局部变换（预置旋转 7°、位移 0.1 原样保留），只登记引用");

                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.VehRigId, true);
                PresentationHost.Register(module);
                VehicleRigWorld.Frames(20);
                int views = Object.FindObjectsByType<MachineView>(FindObjectsSortMode.None).Length;
                Expect(views > 0 && VehicleRigSystem.Count == views && VehicleRigSystem.IsRegistered(a) && VehicleRigSystem.IsRegistered(w),
                    $"接缝登记：家园机器表现 {views} 个、Rig 登记 {VehicleRigSystem.Count} 台（新造履带 #{a}、轮式 #{w} 都在）");
                HomeValleyMachineMarker ma = VehicleRigWorld.Marker(a);
                Transform sil = ma?.View != null ? ma.View.transform.Find(PlaceholderSilhouette.PartsName) : null;
                Transform rig = ma?.View != null ? ma.View.transform.Find(MachineMorphView.RigName) : null;
                Expect(sil != null && rig != null && VehicleRigSystem.NodeOf(a, 0) == sil && VehicleRigSystem.NodeOf(a, 1) == rig,
                    "登记的两个节点就是机器表现下的 Silhouette 与 MorphRig");
                int children0 = ma.View.transform.childCount, silChildren0 = sil.childCount;

                ma.CommandMoveTo(new Vector3(ma.Position.x + 10f, 1f, ma.Position.y + 10f));
                float yaw0 = VehicleRigSystem.YawDegOf(a);
                VehicleRigWorld.Frames(30);
                float yaw1 = VehicleRigSystem.YawDegOf(a);
                Expect(AngleDiff(yaw0, yaw1) > 10f && sil.localRotation == rig.localRotation && sil.localRotation != Quaternion.identity,
                    $"工作赶路：车身朝移动方向转（偏航 {yaw0:0.0}° → {yaw1:0.0}°），两节点同一旋转");
                Expect(ma.View.transform.childCount == children0 && sil.childCount == silChildren0 && sil.parent == ma.View.transform && rig.parent == ma.View.transform,
                    $"Rig 不改层级：机器表现子节点 {children0}、剪影部件 {silChildren0} 不变，两节点父节点仍是机器表现（SilhouetteSelfCheck / FgMachineMorphSelfCheck 按名取直接子节点的前提）");
                double tz = GameClock.GameSeconds + 100.0;
                Expect(MeasureZeroAlloc(() =>
                {
                    PresentationHostBridge.TickManual(0.05f, 0.05f);
                    for (int i = 0; i < 60; i++)
                    {
                        tz += 0.05;
                        module.Tick(new PresentationFrame(0.05f, 0.05f, tz, 0, 0f, false, true, null, null, 1f, 1f, tz));
                    }
                }, out string gc), $"有战役、家园 {VehicleRigSystem.Count} 台登记进 Rig：TickManual + 模块 60 帧（读内核 + 两个作业）零 GC 分配：" + gc);

                // MorphRig 随装配变更重建：只换那一台的节点
                MachineLoadoutRegistry.Register(s, a, FgMachineMorphSelfCheck.EvidenceBpCannonUp, 1);
                VehicleRigWorld.Frames(8);
                Transform rig2 = VehicleRigAdapter.MorphRigOf(a, ma.View);
                int rigsAfter = ma.View.transform.Cast<Transform>().Count(t => t.name == MachineMorphView.RigName);
                Expect(rig2 != null && rig2 != rig && VehicleRigSystem.NodeOf(a, 1) == rig2 && VehicleRigSystem.NodeOf(w, 1) != null && !PresentationHost.IsFaulted(module),
                    $"装配变更重建 MorphRig：Rig 换成新节点（旧 {(rig == null ? "已销毁" : "仍在")}、新 {rig2?.name}），另一台不受影响、模块没抛异常");
                string childNames = string.Join(", ", ma.View.transform.Cast<Transform>().Select(t => t.name));
                Expect(rigsAfter == 1 && MachineMorphView.PartCountOf(a) > 0 && sil.parent == ma.View.transform && rig2.parent == ma.View.transform,
                    $"开状态重放 FgMachineMorphSelfCheck:642 的层级断言：Rig 在转时换主组件（连射器 → 重炮），挂点根 {rigsAfter} 个（应 1）、形变部件 {MachineMorphView.PartCountOf(a)} 个，两节点父节点仍是机器表现（子节点 {childNames}；换主组件后多出的子节点由 FG 装配建，:642 本身不比子节点数）");
                VehicleRigWorld.Frames(4);
                Expect(rig2.localRotation == sil.localRotation, "新 MorphRig 下一帧起与 Silhouette 同一旋转");

                // 摘下 / 挂上成对：观察切换 20 次注册数不涨
                int count0 = VehicleRigSystem.Count;
                string home = WorldView.ObservedSiteId;
                FracturedCityRegion.EnsureRegionRecordSeeded(s);
                FracturedCityRegion.Find(s).State = RegionState.Available;
                FracturedCityController city = WorldSimulation.LoadFracturedCity(Array.Empty<int>(), false);
                VehicleRigWorld.Frames(2);
                int minCount = int.MaxValue, maxCount = 0;
                for (int i = 0; i < 20; i++)
                {
                    WorldView.Observe(i % 2 == 0 ? city.SiteId : home);
                    VehicleRigWorld.Frames(2);
                    minCount = Math.Min(minCount, VehicleRigSystem.Count);
                    maxCount = Math.Max(maxCount, VehicleRigSystem.Count);
                }
                VehicleRigWorld.Frames(2);
                int liveViews = Object.FindObjectsByType<MachineView>(FindObjectsSortMode.None).Length;
                Expect(WorldView.ObservedSiteId == home && VehicleRigSystem.Count == count0 && liveViews == count0 && minCount < count0 && maxCount <= count0,
                    $"观察切换 20 次（家园 ↔ 破碎都市）：Rig 登记 {count0} → {VehicleRigSystem.Count}（期间最少 {minCount}、最多 {maxCount}），机器表现 {liveViews} 个，不涨");

                // 修复轮 1：带正面装甲的机器走真内核（装甲裙板 → CombatSite.FlagFrontalArmor）：画面朝向跟内核 Facing 收敛，
                // 并用世界里的真实位移核对 Facing 的 (x, z) 轴序与符号（Facing → atan2(x, y) = 俯视顺时针、0 = +Z）
                HomeValleyMachineMarker mk = VehicleRigWorld.Marker(k);
                float armor = mk != null && VehicleRigWorld.Unit(k, out _) ? mk.Site.Kernel.ArmorOf(mk.UnitId) : 0f;
                mk?.CommandMoveTo(new Vector3(mk.Position.x + 12f, 1f, mk.Position.y));
                double ta = GameClock.GameSeconds;
                float conv = float.NaN, worstAfter = 0f, worstMotion = 0f, worstVisual = 0f;
                int movingFrames = 0;
                Vector2 lastPos = mk != null ? mk.Position : Vector2.zero;
                for (int f = 0; f < 50 && mk != null; f++)
                {
                    VehicleRigWorld.Frames(1);
                    if (!VehicleRigWorld.Unit(k, out CombatUnitView kv))
                    {
                        break;
                    }
                    float facingYaw = Mathf.Atan2(kv.Facing.x, kv.Facing.y) * Mathf.Rad2Deg;
                    float dev = AngleDiff(VehicleRigSystem.YawDegOf(k), facingYaw);
                    Vector2 d = mk.Position - lastPos;
                    lastPos = mk.Position;
                    if (float.IsNaN(conv))
                    {
                        if (AngleDiff(facingYaw, 90f) <= 10f && dev <= 5f)
                        {
                            conv = (float)(GameClock.GameSeconds - ta);
                        }
                        continue;
                    }
                    worstAfter = Mathf.Max(worstAfter, dev);
                    if (d.magnitude > 0.05f)
                    {
                        movingFrames++;
                        float motionYaw = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                        worstMotion = Mathf.Max(worstMotion, AngleDiff(facingYaw, motionYaw));
                        Transform ks = mk.View != null ? mk.View.transform.Find(PlaceholderSilhouette.PartsName) : null;
                        if (ks != null)
                        {
                            worstVisual = Mathf.Max(worstVisual, AngleDiff(Mathf.Atan2(ks.forward.x, ks.forward.z) * Mathf.Rad2Deg, motionYaw));
                        }
                    }
                }
                float convLimit = 180f / VehicleRigProfiles.Current.track.turnRateDeg + 0.25f;
                Expect(armor > 0f && !float.IsNaN(conv) && conv <= convLimit && worstAfter <= 5f && movingFrames >= 5 && worstMotion <= 10f && worstVisual <= 10f,
                    $"真内核装甲机（履带 + 装甲裙板，正面装甲 {armor:0.##}）：命令向 +X 开 12 m，内核 Facing 转到 90° 后画面朝向 {conv:0.000} s 收敛到 ≤ 5°（限 {convLimit:0.00}），之后最大偏差 {worstAfter:0.0}°；" +
                    $"行驶 {movingFrames} 帧里内核 Facing 与世界位移方向最大差 {worstMotion:0.0}°、剪影世界车头与位移方向最大差 {worstVisual:0.0}°（≤ 10°：Facing 轴序 / 符号与 Rig 一致）");

                // 站着瞄准：评审担心内核 TurnToward 改 Armor.zw 会让装甲机站着瞄准时车身跟着炮口转——在一个独立战斗地点（同 FgEmgLootHintSelfCheck F2 的造法）里用真内核实测，
                // Rig 直接登记那个内核的单位（家园里的机器下了攻击命令也不交战，测不到）。
                AimInCombatSite();

            }
            finally
            {
                PresentationHost.Unregister(module);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                FgMachineMorphSelfCheck.EvidenceEnd();
                VehicleRigSystem.Clear();
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
            }
        }
    }
}
