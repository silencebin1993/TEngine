using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Grid;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG14-SBX-01（FGT-SBX-002“高等级清洗不超出性能预算”；开发标准最低 120 帧）：高等级后日谈清洗的真实绘制帧时间（GPU 探针，带图形设备，tools/unity-gpu-probes.sh）。
    /// 编成取正式的 <see cref="RaidDirectorService.Compose"/>（第 31 级清洗，预算 1200 × 1.15³⁰：同时存在 200 台、全部精英、单位强度 ×N），登陆舱按落地后的样子换成舱 + 投放的清洗无人机，
    /// 单位走正式的攻城生成（<see cref="SiegeService.SpawnOne"/>，耐久 / 伤害 × 强度）四路压向 80 座炮塔；按 3x 倍速、120 帧的节奏（每帧 2 个内核步，偏保守）真实绘制 600 帧，
    /// p95 ≤ 8.33 ms 判定（只测一次；超线不到 2 倍记性能警告）。
    /// 两个场景各量一次：A = 只有清洗（拆来源用）；B = 卡片负向原文“含涌现满载”——同一个地点上再装 FgEmergenceFuzzSelfCheck H 段的“加满”负载
    /// （120 台带程序机器、残骸 256、斑块 1024、活跃场 64、无人机满额、事件每步 512 条，<see cref="FgEmergenceFuzzSelfCheck.AttachFullLoad"/>），判定以 B 为准。
    /// </summary>
    public static class FgPostgamePerfProbe
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Frames = 600;
        private const int StepsPerFrame = 2;
        private const int Level = 31;

        public static void Run()
        {
            var report = new StringBuilder();
            PerfGate.ResetRun();
            int fail = 0;
            int pass = 0;
            void Expect(bool ok, string msg)
            {
                if (ok)
                {
                    pass++;
                    report.AppendLine("  ✓ " + msg);
                }
                else
                {
                    fail++;
                    report.AppendLine("  ✗ " + msg);
                }
            }

            report.AppendLine($"[后日谈高等级清洗 GPU 帧时间] 第 {Level} 级清洗真实绘制（FG14-SBX-01，FGT-SBX-002）");
            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），CPU {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                              $"Burst={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}，Unity {Application.unityVersion}");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                report.AppendLine("  · 没有图形设备（-nographics），跳过");
                Finish(report, 2);
                return;
            }

            ConfigSystem.Instance.Load();
            GridContent.Reload();
            RaidCatalog.Reload();
            SiegeCatalog.Reload();
            var camGo = new GameObject("PostgameProbeCamera");
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var sync = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            CombatSite site = null;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 46f;
                cam.transform.position = new Vector3(0f, 60f, 0f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.07f, 0.1f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.targetTexture = rt;
                cam.enabled = false;

                // 正式编成：第 Level 级清洗（序号 Level - 1）。
                CampaignState s = CampaignState.CreateNew("postgame-probe", "Standard", 14031);
                var plan = new RaidPlanRecord { Serial = 31, Faction = EnemyDefs.FactionFleet, Level = 4, PostgameIndex = Level - 1, Trigger = RaidCatalog.TriggerPurge };
                plan.Budget = RaidDirectorService.ComputeBudget(s, new RaidDirectorState(), plan);
                RaidDirectorService.Compose(s, plan);
                report.AppendLine($"  · 编成：预算 {plan.Budget}，{plan.UnitTotal} 台（同时存在 {plan.Occupancy}，精英 {plan.EliteCounts.Sum()}），单位强度 ×{plan.Strength:F2}：" +
                                  string.Join("、", plan.UnitIds.Select((id, i) => $"{id}×{plan.UnitCounts[i]}")));
                Expect(plan.Occupancy <= RaidCatalog.MaxUnits && plan.Strength > 1f, $"第 {Level} 级清洗同时存在 {plan.Occupancy} 台 ≤ {RaidCatalog.MaxUnits}，强度 ×{plan.Strength:F2} > 1（精英化 + 单位强度，不堆数量）");

                site = BuildScene("gpu_probe_postgame_a", plan, Expect);
                Measure(site, null, cam, rt, sync, report, "A 只有清洗", out _);
                site.Dispose();
                site = null;

                // B：同一个地点上叠涌现满载（卡片负向“含涌现满载：斑块 1024、残骸 256、事件 512 / 步”）
                site = BuildScene("gpu_probe_postgame_b", plan, Expect);
                FgEmergenceFuzzSelfCheck.AttachedLoad load = FgEmergenceFuzzSelfCheck.AttachFullLoad(site);
                try
                {
                    LoadStats ls = Measure(site, load, cam, rt, sync, report, "B 清洗 + 涌现满载", out double p95);
                    double injPerStep = ls.Steps > 0 ? load.Injected / (double)ls.Steps : 0;
                    double procPerStep = ls.Steps > 0 ? ls.Processed / (double)ls.Steps : 0;
                    report.AppendLine($"  · B 场景负载：斑块最少 {ls.MinPatches}、活跃场 {ls.MinFields}～{ls.MaxFields}（每秒补到 64、存续 3 秒，逐帧取最少会落在补场间隙里，不作判定）、残骸 {load.Wrecks}（测量期最少 {ls.MinWrecks}）、无人机 {ls.MinDrones}～{ls.MaxDrones}、带程序机器 {load.Machines.Count}；" +
                                      $"事件每步注入 {injPerStep:F1}、处理 {procPerStep:F1}、队列峰值 {ls.MaxPending}；清洗单位最少 {ls.MinHostiles} 台、炮塔最少 {ls.MinTurrets}");
                    // 判定只取卡片负向点名的三项（斑块 1024、残骸 256、事件 512 / 步）+ 机器；活跃场按 H 段同一套每秒补 64，只记峰值（修复第 1 轮首跑：逐帧最少 0）
                    Expect(ls.MinPatches >= 960 && load.Wrecks >= 256 && injPerStep >= 511 && load.Machines.Count >= 120 && ls.MaxFields >= 64,
                        $"B 场景装上涌现满载（卡片负向 / FGR-EMG-203；同 FgEmergenceFuzzSelfCheck H 段）：斑块始终 ≥ 960（{ls.MinPatches}，上限 1024）、残骸 256（{load.Wrecks}）、活跃场补到 64（峰值 {ls.MaxFields}）、" +
                        $"每步注入事件 {injPerStep:F1} ≥ 511、带程序机器 {load.Machines.Count}");
                    PerfGate.Expect(true, $"B 第 {Level} 级清洗含涌现满载：p95 帧时间 {p95:F2} ms ≤ 8.33 ms（≥ 120 帧/秒，3x 倍速）",
                        new[] { PerfGate.Le(p95, 1000.0 / 120.0, "清洗含涌现满载 p95 帧时间 ms") }, Expect, l => report.AppendLine(l));
                }
                finally
                {
                    FgEmergenceFuzzSelfCheck.DetachFullLoad();
                }
            }
            catch (Exception e)
            {
                fail++;
                report.AppendLine("  ✗ 探针抛异常：" + e);
            }
            finally
            {
                site?.Dispose();
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(sync);
                Object.DestroyImmediate(camGo);
            }
            report.AppendLine($"  · [后日谈高等级清洗 GPU 帧时间] 断言通过 {pass}，失败 {fail}");
            Finish(report, fail == 0 ? 0 : 1);
        }

        private sealed class LoadStats
        {
            public int Steps, MinPatches = int.MaxValue, MinFields = int.MaxValue, MaxFields, MinWrecks = int.MaxValue, MinDrones = int.MaxValue, MaxDrones, MaxPending, MinHostiles = int.MaxValue, MinTurrets = int.MaxValue;
            public long Processed;
        }

        /// <summary>80 座炮塔 + 按编成生成的清洗单位（登陆舱按落地后的样子：舱换一台精英无人机占位 + 投放的清洗无人机，同强度，投放的不是精英）。</summary>
        private static CombatSite BuildScene(string id, RaidPlanRecord plan, Action<bool, string> expect)
        {
            var site = new CombatSite(id, CombatSite.ConfigFromTuning());
            CombatBench.SpawnPerfScenario(site, Vector2.zero, 0, (int)CombatSite.Tuning("combat.perf.turrets", 80), CombatBench.PerfSpec());
            RaidUnitDef drone = RaidCatalog.Units.First(u => u.Id == "fleet.purge_drone");
            var list = new List<(RaidUnitDef Def, bool Elite)>(RaidCatalog.MaxUnits);
            for (int i = 0; i < plan.UnitIds.Length; i++)
            {
                RaidUnitDef def = RaidCatalog.Units.First(u => u.Id == plan.UnitIds[i]);
                int cargo = EnemyDefs.TryGet(def.EnemyTypeId, out GameConfig.fg.MechEnemy me) && EnemyFleetParams.Cargo(me, out _, out int c) ? c : 0;
                for (int k = 0; k < plan.UnitCounts[i]; k++)
                {
                    bool elite = k < plan.EliteCounts[i];
                    if (cargo > 0)
                    {
                        list.Add((drone, elite));
                        for (int j = 0; j < cargo; j++)
                        {
                            list.Add((drone, false));
                        }
                    }
                    else
                    {
                        list.Add((def, elite));
                    }
                }
            }
            int spawned = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int g = i % 4;
                float ang = g * Mathf.PI * 0.5f + 0.4f + (i / 4) * 0.004f;
                float r = 45f + (i / 4 % 10) * 1.2f;
                var at = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                if (SiegeService.SpawnOne(site, g + 1, at, Vector2.zero, list[i].Def, list[i].Elite, plan.Strength, plan.Strength) > 0)
                {
                    spawned++;
                }
            }
            expect(spawned == plan.Occupancy && spawned <= RaidCatalog.MaxUnits, $"{id}：按编成生成 {spawned} 台攻城单位（= 同时存在 {plan.Occupancy}）");
            return site;
        }

        /// <summary>预热 300 步 + 30 帧，再量 <see cref="Frames"/> 帧（只量一次）；负载读数在计时之外取。</summary>
        private static LoadStats Measure(CombatSite site, FgEmergenceFuzzSelfCheck.AttachedLoad load, Camera cam, RenderTexture rt, Texture2D sync, StringBuilder report, string label, out double p95)
        {
            float dt = 1f / 60f;
            double t = 0;
            for (int i = 0; i < 300; i++)
            {
                if (load != null)
                {
                    FgEmergenceFuzzSelfCheck.BetweenSteps(load);
                }
                site.Step(dt, t);
                t += dt;
            }
            for (int i = 0; i < 30; i++)
            {
                RenderFrame(site, load, cam, rt, sync, ref t, dt);
            }
            var ls = new LoadStats();
            long proc0 = site.Kernel.EmgBusStats.Processed;
            long inj0 = load?.Injected ?? 0;
            var frameMs = new List<double>(Frames);
            var simMs = new List<double>(Frames);
            int maxProj = 0;
            var sw = new Stopwatch();
            for (int i = 0; i < Frames; i++)
            {
                sw.Restart();
                double sim = RenderFrame(site, load, cam, rt, sync, ref t, dt);
                sw.Stop();
                frameMs.Add(sw.Elapsed.TotalMilliseconds);
                simMs.Add(sim);
                CombatKernel k = site.Kernel;
                maxProj = Math.Max(maxProj, k.ProjectileCount);
                ls.MinPatches = Math.Min(ls.MinPatches, k.PatchCount);
                ls.MinFields = Math.Min(ls.MinFields, k.FieldCount);
                ls.MaxFields = Math.Max(ls.MaxFields, k.FieldCount);
                ls.MinWrecks = Math.Min(ls.MinWrecks, k.WreckCount);
                ls.MinDrones = Math.Min(ls.MinDrones, k.DroneCount);
                ls.MaxDrones = Math.Max(ls.MaxDrones, k.DroneCount);
                ls.MaxPending = Math.Max(ls.MaxPending, k.EmgBusPending);
                if (i % 30 == 0)
                {
                    ls.MinHostiles = Math.Min(ls.MinHostiles, k.CountAlive(CombatFaction.Hostile));
                    ls.MinTurrets = Math.Min(ls.MinTurrets, k.CountAlive(CombatFaction.Player, CombatUnitKind.Turret));
                }
            }
            ls.Steps = Frames * StepsPerFrame;
            ls.Processed = site.Kernel.EmgBusStats.Processed - proc0;
            if (load != null)
            {
                load.Injected -= inj0; // 只算测量窗口
            }
            frameMs.Sort();
            double avg = frameMs.Average();
            p95 = frameMs[(int)(Frames * 0.95)];
            double max = frameMs[Frames - 1];
            int units = site.Renderer?.LastUnitInstances ?? 0;
            report.AppendLine($"  · {label}：{Frames} 帧（{Width}×{Height}，每帧 {StepsPerFrame} 个内核步 = 3x 倍速 120 帧节奏 + 事件 + 实例化提交 + 渲染 + 等 GPU）：帧时间 平均 {avg:F2} ms / p95 {p95:F2} ms / 最大 {max:F2} ms " +
                              $"≈ {1000.0 / avg:F0} 帧/秒；其中内核步 + 事件平均 {simMs.Average():F3} ms；画面上 {units} 个单位、弹体最多 {maxProj}");
            if (load == null)
            {
                report.AppendLine($"  · {label}：清洗单位最少 {ls.MinHostiles} 台、炮塔最少 {ls.MinTurrets}（只作来源拆分，判定看 B）");
            }
            return ls;
        }

        /// <summary>一帧：<see cref="StepsPerFrame"/> 个内核步（有满载时每步之前注入）+ 事件 + 绘制提交 + 渲染 + 读回 1 像素（强制等 GPU 做完这一帧）。返回内核 + 事件的耗时。</summary>
        private static double RenderFrame(CombatSite site, FgEmergenceFuzzSelfCheck.AttachedLoad load, Camera cam, RenderTexture rt, Texture2D sync, ref double t, float dt)
        {
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < StepsPerFrame; k++)
            {
                if (load != null)
                {
                    FgEmergenceFuzzSelfCheck.BetweenSteps(load); // 注入事件、每秒补斑块（热更层在两步之间做的事，算进帧时间）
                }
                site.Step(dt, t);
                t += dt;
            }
            double sim = sw.Elapsed.TotalMilliseconds;
            site.FrameRender(cam, 0.5f);
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            sync.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            sync.Apply();
            RenderTexture.active = prev;
            return sim;
        }

        private static string EvidenceDir()
        {
            var d = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "production", "qa", "evidence");
                if (Directory.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static void Finish(StringBuilder report, int code)
        {
            Debug.Log(report.ToString());
            string dir = EvidenceDir();
            if (dir != null)
            {
                File.WriteAllText(Path.Combine(dir, "_postgame-gpu-probe.txt"), report.ToString());
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
