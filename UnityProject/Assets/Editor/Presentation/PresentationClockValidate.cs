// PL-INF-07 → production/design/presentation-track/handoff/PL-INF-07.md
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Presentation;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View.Presentation;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static GameLogic.EditorTools.PresentationValidate;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-INF-07 表现时钟与重建协议的自检段（挂在 <see cref="PresentationValidate"/> 的段表里）：
    /// ClockPause / ClockSpeed / ClockManualDeterminism / RebuildLoad / RebuildSiteSwitch。
    /// </summary>
    internal static class PresentationClockValidate
    {
        private const float Dt = 1f / 60f;

        private static PresentationProbeModule RegisterProbe()
        {
            PresentationFeatureRegistry.ResetToDefaults();
            PresentationHost.SetEnabled(true);
            PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, true);
            var probe = new PresentationProbeModule();
            PresentationHost.Register(probe);
            return probe;
        }

        private static void Cleanup(IPresentationModule probe)
        {
            PresentationHost.Unregister(probe);
            PresentationFeatureRegistry.ResetToDefaults();
            PresentationHost.Reconcile();
            GameClock.ResetSession();
        }

        // ── ClockPause：暂停 120 帧 GameDt 恒 0、RealDt > 0，真实时间类的探针照常动 ──

        public static void ClockPause()
        {
            PresentationProbeModule probe = RegisterProbe();
            try
            {
                GameClock.ResetSession();
                GameClock.SetPaused(true);
                Transform cube = PresentationHost.Root.GetChild(5);
                Vector3 before = cube.localPosition;
                int nonZeroGame = 0, badReal = 0, notPaused = 0;
                for (int i = 0; i < 120; i++)
                {
                    GameClock.Advance(Dt);
                    PresentationFrame f = PresentationHostBridge.LiveFrame(Dt, false);
                    nonZeroGame += f.GameDt == 0f ? 0 : 1;
                    badReal += f.RealDt > 0f ? 0 : 1;
                    notPaused += f.Paused ? 0 : 1;
                    PresentationHost.Tick(f);
                }
                Expect(nonZeroGame == 0 && badReal == 0 && notPaused == 0,
                    $"GameClock.SetPaused(true) 后 120 帧：GameDt 恒 0（非 0 {nonZeroGame} 帧）、RealDt > 0（不满足 {badReal} 帧）、Paused 为真（不满足 {notPaused} 帧）");
                Expect(cube.localPosition != before, "暂停时真实时间类（[PresentationTime(Real)]）的探针方块照常动（震屏 / 镜头推动的约定）");
                GameClock.SetPaused(false);
                GameClock.Advance(Dt);
                PresentationFrame resumed = PresentationHostBridge.LiveFrame(Dt, false);
                Expect(Mathf.Abs(resumed.GameDt - Dt) <= 1e-6f && !resumed.Paused, $"取消暂停后下一帧 GameDt = {resumed.GameDt:R}（应 {Dt:R}）");
            }
            finally
            {
                Cleanup(probe);
            }
        }

        // ── ClockSpeed：4 档倍速与慢放下 GameDt 与 GameClock.FrameScaledDt / 理论值差 ≤ 1e-6 ──

        public static void ClockSpeed()
        {
            try
            {
                GameClock.ResetSession();
                var sb = new StringBuilder();
                double maxErr = 0;
                bool fieldsOk = true;
                void Check(float speed, float slow)
                {
                    GameClock.Advance(Dt);
                    PresentationFrame f = PresentationHostBridge.LiveFrame(Dt, false);
                    double expect = (double)Dt * speed * slow;
                    double err = Math.Max(Math.Abs(f.GameDt - GameClock.FrameScaledDt), Math.Abs(f.GameDt - expect));
                    maxErr = Math.Max(maxErr, err);
                    fieldsOk &= f.Speed == speed && f.SlowMotion == slow && Mathf.Approximately(f.RealDt, Dt);
                    sb.Append($"{speed}x×{slow}→{f.GameDt:R} ");
                }
                foreach (float s in GameClock.Speeds)
                {
                    GameClock.SetSpeed(s);
                    Check(s, 1f);
                }
                Expect(GameClock.Speeds.Length == 4, $"倍速档 {GameClock.Speeds.Length} 个（0.5 / 1 / 2 / 3）");
                GameClock.SetSpeed(2f);
                GameClock.BeginSlowMotion(1f, 0.25f);
                Check(2f, 0.25f);
                GameClock.CancelSlowMotion();
                Expect(maxErr <= 1e-6 && fieldsOk,
                    $"4 档倍速 + 2x 下 0.25 慢放：GameDt 与 FrameScaledDt、与 RealDt×Speed×SlowMotion 的最大差 {maxErr:E2}（≤ 1e-6），帧的 Speed / SlowMotion 字段与 GameClock 一致 {fieldsOk}：{sb}");

                // SmoothGameSeconds 连续：按正式循环 Advance → n 次 CommitStep → 拼帧，连续 4 帧严格递增、增量 = GameDt；GameSeconds 同期至少停 1 帧（证明它是阶梯）
                foreach ((float speed, float slow) in new[] { (0.5f, 1f), (1f, 0.25f), (0.5f, 0.25f) })
                {
                    GameClock.ResetSession();
                    GameClock.SetSpeed(speed);
                    if (slow < 1f)
                    {
                        GameClock.BeginSlowMotion(10f, slow);
                    }
                    bool rising = true;
                    int stalls = 0;
                    double maxDelta = 0, prev = 0, prevStep = 0;
                    var log = new StringBuilder();
                    for (int i = 0; i < 5; i++)
                    {
                        int n = GameClock.Advance(Dt);
                        for (int k = 0; k < n; k++)
                        {
                            GameClock.CommitStep(null);
                        }
                        PresentationFrame f = PresentationHostBridge.LiveFrame(Dt, false);
                        if (i > 0)
                        {
                            rising &= f.SmoothGameSeconds > prev;
                            maxDelta = Math.Max(maxDelta, Math.Abs(f.SmoothGameSeconds - prev - f.GameDt));
                            stalls += f.GameSeconds == prevStep ? 1 : 0;
                        }
                        prev = f.SmoothGameSeconds;
                        prevStep = f.GameSeconds;
                        log.Append(f.SmoothGameSeconds.ToString("F5", CultureInfo.InvariantCulture)).Append('/').Append(f.GameSeconds.ToString("F5", CultureInfo.InvariantCulture)).Append(' ');
                    }
                    GameClock.CancelSlowMotion();
                    Expect(rising && maxDelta <= 1e-6 && stalls > 0,
                        $"{speed}x × 慢放 {slow}：SmoothGameSeconds 连续 4 帧严格递增 {rising}、每帧增量与 GameDt 最大差 {maxDelta:E2}（≤ 1e-6）；同期 GameSeconds 停住 {stalls} 帧（阶梯）：Smooth/GameSeconds = {log}");
                }
            }
            finally
            {
                GameClock.ResetSession();
            }
        }

        // ── ClockManualDeterminism：合成帧 1/60 跑两遍 300 帧，变换日志逐字节相同；全部 PL 模块类声明了时间类别 ──

        public static void ClockManualDeterminism()
        {
            string RunOnce()
            {
                PresentationProbeModule probe = RegisterProbe();
                try
                {
                    var sb = new StringBuilder();
                    Transform root = PresentationHost.Root;
                    for (int f = 0; f < 300; f++)
                    {
                        PresentationHost.Tick(PresentationFrame.Synthetic(Dt, f * (double)Dt));
                        for (int c = 0; c < root.childCount; c++)
                        {
                            Vector3 p = root.GetChild(c).localPosition;
                            sb.Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                              .Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                              .Append(p.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                        }
                        sb.Append('\n');
                    }
                    return sb.ToString();
                }
                finally
                {
                    Cleanup(probe);
                }
            }

            string a = RunOnce();
            string b = RunOnce();
            string[] lines = a.Split('\n');
            Expect(a.Length > 0 && a == b && lines[0] != lines[298],
                $"Manual 模式（PresentationFrame.Synthetic，固定 dt 1/60）同一探针跑两遍 300 帧：变换日志逐字节相同（{a.Length} 字符，首尾帧不同 = 日志确实随帧变化）");

            Type[] undeclared = TypeCache.GetTypesDerivedFrom<IPresentationModule>()
                .Where(t => !t.IsAbstract && !t.IsInterface && t.Assembly != typeof(PresentationClockValidate).Assembly)
                .Where(t => t.GetCustomAttributes(typeof(PresentationTimeAttribute), false).Length == 0)
                .ToArray();
            Expect(undeclared.Length == 0, $"非编辑器程序集里的 PL 模块类都声明了 [PresentationTime(TimeDomain.…)]（缺：{string.Join(", ", undeclared.Select(t => t.FullName))}）");

            // 只认一个时钟：PL 模块源码（Lab 与桥除外）不得直接读 Unity Time / Random / GameClock（注释不算）
            var banned = new Regex(@"\bTime\.(deltaTime|time|timeAsDouble|unscaledDeltaTime|unscaledTime|unscaledTimeAsDouble|smoothDeltaTime|fixedDeltaTime|timeScale|frameCount|realtimeSinceStartup|realtimeSinceStartupAsDouble)\b|\bRandom\.|\bGameClock\b");
            string[] files = PlSourceDirs.Where(d => Directory.Exists(d) && !d.Contains("PresentationLab"))
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => Path.GetFileName(f) != "PresentationHostBridge.cs").ToArray();
            string[] offenders = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, code: l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0])))
                .Where(x => banned.IsMatch(x.code)).Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}").ToArray();
            Expect(files.Length > 0 && offenders.Length == 0,
                $"PL 模块源码 {files.Length} 个文件（Main/Presentation、View/Presentation，桥除外）不直接读 Time.* / Random / GameClock，时间只取帧（违规：{string.Join(", ", offenders)}）");
        }

        private static int Markers(string siteId)
        {
            if (!PresentationHost.RootExists)
            {
                return 0;
            }
            string name = PresentationProbeModule.SiteMarkerPrefix + siteId;
            return PresentationHost.Root.GetComponentsInChildren<Transform>(true).Count(t => t.name == name);
        }

        private static int AllMarkers() => !PresentationHost.RootExists
            ? 0
            : PresentationHost.Root.GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith(PresentationProbeModule.SiteMarkerPrefix, StringComparison.Ordinal));

        // ── RebuildLoad：换 CampaignSession → 1 帧内恰好 1 次 Rebuild(Reload)（同帧原点平移合并）；退出战役清场；开 / 关 sim 哈希 ──

        public static void RebuildLoad()
        {
            var probe = new PresentationProbeModule();
            bool ran = false;
            double3 savedOrigin = PresentationSpace.Origin;
            PresentationSimHash.Run(PresentationSimHash.DefaultSeed, 30, true, new IPresentationModule[] { probe }, () =>
            {
                ran = true;
                CampaignState cur = CampaignSession.Current;
                int slot = CampaignSession.ActiveSlotIndex;
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        PresentationHostBridge.TickManual(Dt, 0f);
                    }
                    string site = WorldView.ObservedSiteId;
                    int load0 = probe.RebuildCount(RebuildReason.Reload), total0 = probe.RebuildTotal, tr0 = probe.TransientCount;
                    Expect(tr0 >= 5 && site != null && probe.SiteMarkerId == site && Markers(site) == 1,
                        $"连续运行基线：Transient {tr0} 帧、Derived 地点标记 = {probe.SiteMarkerId}（观察地点 {site}）");

                    // 模拟读档：换一份内容相同的新 CampaignState 引用；同一帧再平移原点 → 两个原因合并成一次 Reload
                    CampaignSession.Set(slot, JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(cur)));
                    PresentationSpace.SetOrigin(savedOrigin + new double3(512, 0, 512));
                    PresentationHostBridge.TickManual(Dt, 0f);
                    Expect(probe.RebuildCount(RebuildReason.Reload) == load0 + 1 && probe.RebuildTotal == total0 + 1,
                        $"换 CampaignSession（+ 同帧原点平移）：1 帧内 Rebuild 恰好 1 次、原因 Reload（Reload {load0} → {probe.RebuildCount(RebuildReason.Reload)}，总数 {total0} → {probe.RebuildTotal}）");
                    Expect(probe.TransientCount == 1, $"Transient 在重建时归 0（重建后只剩本帧：{probe.TransientCount}）");
                    Expect(probe.SiteMarkerId == site && Markers(site) == 1 && AllMarkers() == 1,
                        $"Derived 地点标记与连续运行一致（{probe.SiteMarkerId}，标记 {AllMarkers()} 个）");

                    int os0 = probe.RebuildCount(RebuildReason.OriginShift);
                    PresentationSpace.SetOrigin(savedOrigin);
                    PresentationHostBridge.TickManual(Dt, 0f);
                    Expect(probe.RebuildCount(RebuildReason.OriginShift) == os0 + 1 && probe.RebuildTotal == total0 + 2,
                        $"单独平移原点：下一帧 Rebuild(OriginShift) 1 次（{os0} → {probe.RebuildCount(RebuildReason.OriginShift)}）");

                    // 设置变化与原点平移同帧：互不包含，各重建 1 次
                    int st0 = probe.RebuildCount(RebuildReason.Settings);
                    PresentationSpace.SetOrigin(savedOrigin + new double3(64, 0, 0));
                    PresentationHost.RequestRebuild(RebuildReason.Settings);
                    PresentationHostBridge.TickManual(Dt, 0f);
                    Expect(probe.RebuildCount(RebuildReason.Settings) == st0 + 1 && probe.RebuildCount(RebuildReason.OriginShift) == os0 + 2 && probe.RebuildTotal == total0 + 4,
                        $"Settings + OriginShift 同帧：各 1 次（Settings {st0} → {probe.RebuildCount(RebuildReason.Settings)}，OriginShift {os0 + 1} → {probe.RebuildCount(RebuildReason.OriginShift)}，总数 → {probe.RebuildTotal}）");
                    PresentationSpace.SetOrigin(savedOrigin);
                    PresentationHostBridge.TickManual(Dt, 0f);

                    // 战役引用不变、只有 InWorld 由 true 变 false（区域全部卸载）：单独走 InWorld 分支 → Reload 恰好 1 次、地点标记清掉；回到世界后不再重建、标记重建
                    int load1 = probe.RebuildCount(RebuildReason.Reload), totalW = probe.RebuildTotal;
                    PresentationHost.Tick(PresentationHostBridge.BuildFrame(Dt, 0f, false));
                    Expect(CampaignSession.Current != null && probe.RebuildCount(RebuildReason.Reload) == load1 + 1 && probe.RebuildTotal == totalW + 1 && AllMarkers() == 0,
                        $"Current 不变、InWorld true→false：Reload {load1} → {probe.RebuildCount(RebuildReason.Reload)}（应 +1）、总数 {totalW} → {probe.RebuildTotal}、地点标记 {AllMarkers()}");
                    PresentationHostBridge.TickManual(Dt, 0f);
                    Expect(probe.RebuildTotal == totalW + 1 && Markers(site) == 1, $"回到世界（InWorld false→true）不重建（总数 {probe.RebuildTotal}），地点标记按本帧重建 {Markers(site)} 个");

                    int total1 = probe.RebuildTotal, tr1 = probe.TransientCount;
                    for (int i = 0; i < 30; i++)
                    {
                        PresentationHostBridge.TickManual(Dt, 0f);
                    }
                    Expect(probe.RebuildTotal == total1 && probe.TransientCount == tr1 + 30, $"没有变化的 30 帧不重建（重建总数 {total1} → {probe.RebuildTotal}，Transient {tr1} → {probe.TransientCount}）");

                    // 退出战役（回主菜单）：InWorld 由 true 变 false + 战役引用置空 → Reload，世界上挂的 PL 对象清掉
                    CampaignSession.Clear();
                    PresentationHostBridge.TickManual(Dt, 0f);
                    Expect(probe.RebuildCount(RebuildReason.Reload) == load0 + 3 && probe.SiteMarkerId == null && AllMarkers() == 0,
                        $"退出战役：1 帧内 Rebuild(Reload)（{probe.RebuildCount(RebuildReason.Reload) - load0 - 2} 次）、地点标记清零（{AllMarkers()}）");
                }
                finally
                {
                    CampaignSession.Set(slot, cur);
                    PresentationSpace.SetOrigin(savedOrigin);
                }
            });
            Expect(ran, "夹具载入了世界、回调跑到");
            Expect(PresentationHost.RootChildCount == 0 && PresentationHost.ModuleCount == 0, "夹具收尾后宿主无模块、[PresentationRoot] 无残留");

            bool same = PresentationSimHash.CheckOnOff(out string d, PresentationSimHash.DefaultSeed, 120, new PresentationProbeModule());
            Expect(same, "本卡（重建检测 + 合并 + 探针重建）功能开 / 关 sim 哈希一致：" + d);
        }

        // ── RebuildSiteSwitch：WorldView.Observe 换地点 → 1 帧内 Rebuild(SiteSwitch)、旧地点的 PL 对象为 0 ──

        public static void RebuildSiteSwitch()
        {
            var probe = new PresentationProbeModule();
            bool ran = false;
            PresentationSimHash.Run(PresentationSimHash.DefaultSeed, 30, true, new IPresentationModule[] { probe }, () =>
            {
                ran = true;
                string home = WorldView.ObservedSiteId;
                CampaignState s = CampaignSession.Current;
                FracturedCityRegion.EnsureRegionRecordSeeded(s);
                FracturedCityRegion.Find(s).State = RegionState.Available;
                FracturedCityController city = WorldSimulation.LoadFracturedCity(Array.Empty<int>(), false);
                for (int i = 0; i < 3; i++)
                {
                    PresentationHostBridge.TickManual(Dt, 0f);
                }
                Expect(city != null && city.IsLoaded && Markers(home) == 1, $"破碎都市已载入（{city?.SiteId}），镜头仍在家园、家园标记 {Markers(home)} 个");
                if (city == null || !city.IsLoaded)
                {
                    return;
                }
                int ss0 = probe.RebuildCount(RebuildReason.SiteSwitch), total0 = probe.RebuildTotal, sw0 = WorldView.ObserveSwitchCount;
                bool switched = WorldView.Observe(city.SiteId);
                PresentationHostBridge.TickManual(Dt, 0f);
                Expect(switched && WorldView.ObservedSiteId == city.SiteId && WorldView.ObserveSwitchCount == sw0 + 1,
                    $"WorldView.Observe 换到 {city.SiteId}（切换计数 {sw0} → {WorldView.ObserveSwitchCount}）");
                Expect(probe.RebuildCount(RebuildReason.SiteSwitch) == ss0 + 1 && probe.RebuildTotal == total0 + 1,
                    $"1 帧内 Rebuild(SiteSwitch) 恰好 1 次（{ss0} → {probe.RebuildCount(RebuildReason.SiteSwitch)}，总数 {total0} → {probe.RebuildTotal}）");
                Expect(Markers(home) == 0 && Markers(city.SiteId) == 1, $"旧地点 {home} 的 PL 对象 {Markers(home)} 个（应 0），新地点 {Markers(city.SiteId)} 个（应 1）");

                WorldView.Observe(home);
                PresentationHostBridge.TickManual(Dt, 0f);
                Expect(probe.RebuildCount(RebuildReason.SiteSwitch) == ss0 + 2 && Markers(city.SiteId) == 0 && Markers(home) == 1,
                    $"切回家园：再 Rebuild(SiteSwitch) 1 次、破碎都市对象 {Markers(city.SiteId)} 个、家园 {Markers(home)} 个");
            });
            Expect(ran, "夹具载入了世界、回调跑到");
            Expect(PresentationHost.RootChildCount == 0 && PresentationHost.ModuleCount == 0, "夹具收尾后宿主无模块、[PresentationRoot] 无残留");
        }
    }
}
