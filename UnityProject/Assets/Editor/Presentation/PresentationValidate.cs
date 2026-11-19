// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Presentation;
using BinGames.PresentationLab;
using GameLogic.Campaign.WorldSim;
using GameLogic.View.Presentation;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 表现泳道（PL）自检入口。段表 <see cref="Segments"/>：每张 PL 卡在末尾追加一行 ("段名", 方法)，方法里用 <see cref="Expect"/> 断言。
    /// 只跑点名段：环境变量 <c>PL_VALIDATE_SEGMENTS=A,B</c>（未知段名记失败）。
    /// 两个入口：<see cref="RunAll"/>（菜单 / pl-unity.sh；跑全部段含 Lab，batchmode 下按失败数 EditorApplication.Exit）；
    /// <see cref="Run"/>（给 FG 合流挂进 CellFrameworkValidate.RunAll：<c>Seg("PresentationValidate", PresentationValidate.Run);</c>——返回失败数、不退出进程、跳过会换场景的 Lab 段、不读 PL_VALIDATE_SEGMENTS）。
    /// 跑法：<c>cd /d/BinGames-PL &amp;&amp; bash tools/pl/pl-unity.sh validate GameLogic.EditorTools.PresentationValidate.RunAll</c>（结论看日志里的 ✓ / ✗）。
    /// </summary>
    public static class PresentationValidate
    {
        public const string LabScenePath = "Assets/PresentationLab/Scenes/PresentationLab.unity";

        private static readonly (string Name, Action Run)[] Segments =
        {
            ("Skeleton", Skeleton),
            ("HostLifecycle", HostLifecycle),
            ("Space", Space),
            ("Materials", Materials),
            ("Lab", Lab),
            ("SimHashOnOff", SimHashOnOff),
            ("FeaturesDoc", FeaturesDoc),
            // PL-INF-07 表现时钟与重建协议（PresentationClockValidate.cs）
            ("ClockPause", PresentationClockValidate.ClockPause),
            ("ClockSpeed", PresentationClockValidate.ClockSpeed),
            ("ClockManualDeterminism", PresentationClockValidate.ClockManualDeterminism),
            ("RebuildLoad", PresentationClockValidate.RebuildLoad),
            ("RebuildSiteSwitch", PresentationClockValidate.RebuildSiteSwitch),
            // PL-QA-01 采集管线（Capture/PresentationCaptureValidate.cs）；后四段要显卡，-nographics 下只写“跳过”
            ("CaptureScenes", PresentationCaptureValidate.CaptureScenes),
            ("CaptureDevice", PresentationCaptureValidate.CaptureDevice),
            ("CaptureDeterminism", PresentationCaptureValidate.CaptureDeterminism),
            ("CaptureInjection", PresentationCaptureValidate.CaptureInjection),
            ("CaptureGpuPair", PresentationCaptureValidate.CaptureGpuPair),
        };

        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;

        /// <summary>非 0 时 RunAll 在有失败时用它作退出码（PL-QA-01：图形设备不是 RTX 4070 = 3“采集无效”）。</summary>
        internal static int ExitCodeOverride;

        /// <summary>本次是经 <see cref="Run"/> 嵌在 FG 长链里跑（段内据此跳过外部依赖，如 Python）。</summary>
        internal static bool InFgChain { get; private set; }

        /// <summary>会换掉当前场景的段：嵌在 FG 长链里（<see cref="Run"/>）时跳过。</summary>
        private static readonly string[] SceneSwappingSegments = { "Lab", "CaptureDeterminism", "CaptureInjection", "CaptureGpuPair" };

        [MenuItem("QA/表现泳道/自检全部")]
        public static void RunAll()
        {
            string env = Environment.GetEnvironmentVariable("PL_VALIDATE_SEGMENTS");
            HashSet<string> only = string.IsNullOrWhiteSpace(env)
                ? null
                : new HashSet<string>(env.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.Ordinal);
            var report = new StringBuilder();
            int fail = RunSegments(report, only, true, only == null ? "全部段" : "只跑 " + env);
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : ExitCodeOverride != 0 ? ExitCodeOverride : 1);
            }
        }

        /// <summary>FG 合流入口（签名同 FG 的 <c>Seg(string, Func&lt;StringBuilder, int&gt;)</c>）：断言写进 <paramref name="report"/>，返回失败数；不退出、不换场景（跳过 Lab 段）。</summary>
        public static int Run(StringBuilder report) => RunSegments(report, null, false, "全部段（跳过会换场景的 Lab 段）");

        private static int RunSegments(StringBuilder report, HashSet<string> only, bool includeSceneSegments, string title)
        {
            _report = report;
            _pass = 0;
            _fail = 0;
            ExitCodeOverride = 0;
            InFgChain = !includeSceneSegments;
            Line($"[PL 自检] PresentationValidate {title}");
            if (only != null)
            {
                foreach (string n in only.Where(n => Segments.All(s => s.Name != n)))
                {
                    Expect(false, $"PL_VALIDATE_SEGMENTS 点了不存在的段 {n}");
                }
            }
            foreach ((string name, Action run) in Segments)
            {
                if (only != null && !only.Contains(name))
                {
                    continue;
                }
                if (!includeSceneSegments && SceneSwappingSegments.Contains(name))
                {
                    Line($"\n[PL:{name}]\n  - 嵌在 FG 链里跳过（会换掉当前场景）；单独跑 RunAll 覆盖");
                    continue;
                }
                Line($"\n[PL:{name}]");
                var sw = Stopwatch.StartNew();
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    Expect(false, $"{name} 段抛异常：{e}");
                }
                Line($"  · {name} 耗时 {sw.ElapsedMilliseconds} ms");
            }
            if (!Application.isPlaying && PresentationHost.ModuleCount == 0)
            {
                PresentationHost.DestroyRoot(); // 别在编辑器里留下空的 [PresentationRoot]
            }
            Line($"\n[PL 自检] 断言通过 {_pass}，失败 {_fail}");
            Line(_fail == 0 ? "[PL 自检] 结论：通过" : $"[PL 自检] 结论：失败 {_fail} 项");
            return _fail;
        }

        public static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                _fail++;
                Line("  ✗ " + what);
            }
        }

        public static void Line(string s) => _report.AppendLine(s);

        // ── Skeleton：构建场景、YooAsset 收集路径、asmdef 依赖 ──

        [Serializable]
        private sealed class AsmdefJson
        {
            public string name;
            public string[] references;
            public string[] defineConstraints;
            public bool autoReferenced = true;
        }

        private static void Skeleton()
        {
            string[] scenes = EditorBuildSettings.scenes.Select(s => s.path).ToArray();
            Expect(scenes.Length == 1 && scenes[0] == "Assets/Scenes/main.unity", $"构建场景仍只有 main.unity（实际 {string.Join(", ", scenes)}）");

            string collector = File.ReadAllText("Assets/Editor/AssetBundleCollector/AssetBundleCollectorSetting.asset");
            string[] paths = Regex.Matches(collector, @"CollectPath:\s*(\S+)").Cast<Match>().Select(m => m.Groups[1].Value.TrimEnd('/')).ToArray();
            string[] covering = paths.Where(p => "Assets/PresentationLab/".StartsWith(p + "/", StringComparison.Ordinal) ||
                                                 p.StartsWith("Assets/PresentationLab", StringComparison.Ordinal)).ToArray();
            Expect(paths.Length > 0 && covering.Length == 0, $"PresentationLab 不在任何 YooAsset CollectPath 下（共 {paths.Length} 条收集路径，覆盖 Lab 的：{string.Join(", ", covering)}）");

            var byName = new Dictionary<string, AsmdefJson>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AsmdefJson a = JsonUtility.FromJson<AsmdefJson>(File.ReadAllText(p));
                if (!string.IsNullOrEmpty(a.name))
                {
                    byName[a.name] = a;
                }
            }
            string Resolve(string r)
            {
                if (!r.StartsWith("GUID:", StringComparison.Ordinal))
                {
                    return r;
                }
                string p = AssetDatabase.GUIDToAssetPath(r.Substring(5));
                return string.IsNullOrEmpty(p) ? r : JsonUtility.FromJson<AsmdefJson>(File.ReadAllText(p)).name;
            }
            IEnumerable<string> Refs(string n) => byName.TryGetValue(n, out AsmdefJson a) && a.references != null ? a.references.Select(Resolve) : Enumerable.Empty<string>();

            const string pres = "BinGames.Presentation";
            Expect(byName.TryGetValue(pres, out AsmdefJson presA) && presA.autoReferenced, "BinGames.Presentation.asmdef 存在且 autoReferenced");
            string[] need = { "BinGames.Sim", "BinGames.TerrainVisual", "Unity.Burst", "Unity.Collections", "Unity.Mathematics", "Unity.Jobs" };
            string[] missing = need.Except(Refs(pres)).ToArray();
            Expect(missing.Length == 0, $"BinGames.Presentation 引用 Sim / TerrainVisual / Burst / Collections / Mathematics / Jobs（缺：{string.Join(", ", missing)}）");

            // 传递闭包：不得到达 GameLogic；DFS 判环
            var reach = new HashSet<string>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            string cycle = null;
            void Dfs(string n)
            {
                if (cycle != null)
                {
                    return;
                }
                onStack.Add(n);
                foreach (string r in Refs(n))
                {
                    if (onStack.Contains(r))
                    {
                        cycle = n + " → " + r;
                        return;
                    }
                    if (reach.Add(r))
                    {
                        Dfs(r);
                    }
                }
                onStack.Remove(n);
            }
            Dfs(pres);
            Expect(!reach.Contains("GameLogic") && !reach.Contains("GameProto"), $"BinGames.Presentation 不（直接或间接）引用 GameLogic / GameProto（可达 {reach.Count} 个程序集）");
            Expect(cycle == null, $"BinGames.Presentation 的引用图无环（{cycle ?? "无"}）");
            Expect(Refs("GameLogic").Contains(pres), "GameLogic.asmdef 引用 BinGames.Presentation（SEAMS 接缝）");
            string[] badGame = PresentationHostBridge.GameModules.Where(m => m == null || !PresentationFeatureRegistry.IsRegistered(m.Id)).Select(m => m?.GetType().Name ?? "null").ToArray();
            Expect(badGame.Length == 0, $"正式游戏登记表 PresentationHostBridge.GameModules 的 {PresentationHostBridge.GameModules.Count} 个模块 Id 都已在登记表里（不合格：{string.Join(", ", badGame)}）");
            Expect(byName.TryGetValue("BinGames.PresentationLab", out AsmdefJson lab) && lab.defineConstraints != null && lab.defineConstraints.Contains("UNITY_EDITOR"),
                "BinGames.PresentationLab.asmdef 带 defineConstraints UNITY_EDITOR（不进玩家构建）");
        }

        // ── HostLifecycle：探针模块开关 10 次无残留、零 GC ──

        private static PresentationFrame Frame(float dt) => new PresentationFrame(dt, dt, 0, 0, 1f, false, true, "selfcheck", null);

        private static void HostLifecycle()
        {
            var probe = new PresentationProbeModule();
            try
            {
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.SetEnabled(true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, true);
                PresentationHost.Register(probe);
                Expect(PresentationHost.RootChildCount == PresentationProbeModule.CubeCount, $"总开关与探针都开：[PresentationRoot] 下 {PresentationHost.RootChildCount} 个方块（应 {PresentationProbeModule.CubeCount}）");
                Transform root = PresentationHost.Root;
                Expect(root.name == PresentationHost.RootName && root.parent == null, "根节点名 [PresentationRoot]、在场景顶层");
                Vector3 before = root.GetChild(5).localPosition;
                PresentationHost.Tick(Frame(0.25f));
                Expect(root.GetChild(5).localPosition != before, "Tick 驱动模块（探针方块移动了）");

                int offLeft = 0, onWrong = 0;
                for (int i = 0; i < 10; i++)
                {
                    PresentationHost.SetEnabled(false);
                    PresentationHost.Tick(Frame(1f / 60f));
                    offLeft += PresentationHost.RootChildCount;
                    PresentationHost.SetEnabled(true);
                    for (int f = 0; f < 3; f++)
                    {
                        PresentationHost.Tick(Frame(1f / 60f));
                    }
                    onWrong += PresentationHost.RootChildCount == PresentationProbeModule.CubeCount ? 0 : 1;
                }
                Expect(offLeft == 0 && onWrong == 0, $"总开关开关 10 次：每次关后无残留（累计残留 {offLeft}）、每次开后 64 个（不对 {onWrong} 次）");

                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, false);
                PresentationHost.Tick(Frame(1f / 60f));
                Expect(PresentationHost.RootChildCount == 0, $"只关探针开关：下一次 Tick 对账后无残留（{PresentationHost.RootChildCount}）");
                PresentationHost.SetEnabled(false);
                Expect(PresentationHost.RootChildCount == 0, "最终关总开关后 [PresentationRoot] 子物体 = 0");

                // 零 GC：探针关、宿主开，经热更桥 TickManual（含拼帧）600 帧 × MeasureZeroAlloc 默认放大倍数
                PresentationHost.SetEnabled(true);
                for (int i = 0; i < 60; i++)
                {
                    PresentationHostBridge.TickManual(1f / 60f);
                }
                bool zero = MeasureZeroAlloc(() =>
                {
                    for (int i = 0; i < 600; i++)
                    {
                        PresentationHostBridge.TickManual(1f / 60f);
                    }
                }, out string allocDetail);
                Expect(zero, $"宿主 600 帧（探针关、经 TickManual）托管分配次数 = 0：" + allocDetail);
                MeasureZeroAlloc(() =>
                {
                    for (int i = 0; i < 600; i++)
                    {
                        PresentationHost.Tick(Frame(1f / 60f));
                    }
                }, out string hostOnly);
                Line("  · 对照（不计断言）：只调 PresentationHost.Tick 600 帧：" + hostOnly);
                // 反例（检出下限）：卡片关心的最稀疏每帧分配 = 每 4 帧 1 个 object，必须判失败
                var keep = new object[1];
                int frameNo = 0;
                bool falseZero = MeasureZeroAlloc(() =>
                {
                    for (int i = 0; i < 600; i++)
                    {
                        if ((++frameNo & 3) == 0)
                        {
                            keep[0] = new object();
                        }
                    }
                }, out string negDetail);
                Expect(!falseZero, "反例：600 帧里每 4 帧 new object() 一次会被零分配断言判失败（计量不空转）：" + negDetail);
                // 检出下限 = 1 次：600 帧里只 new 一次 object 也必须判失败
                int once = 0;
                bool missedOne = MeasureZeroAlloc(() =>
                {
                    for (int i = 0; i < 600; i++)
                    {
                        if (++once % 600 == 0)
                        {
                            keep[0] = new object();
                        }
                    }
                }, out string onceDetail);
                Expect(!missedOne, "反例：600 帧里只 new object() 一次也被判失败（检出下限 = 1 次分配）：" + onceDetail);

                // 异常隔离：抛异常的模块不让宿主往外抛、被停用，其余模块照常 Tick
                var thrower = new ThrowingModule(false);
                var enableThrower = new ThrowingModule(true);
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, true);
                PresentationHost.Register(thrower);
                bool registerThrew = false;
                try
                {
                    PresentationHost.Register(enableThrower);
                }
                catch (Exception)
                {
                    registerThrew = true;
                }
                Expect(!registerThrew && !PresentationHost.IsActive(enableThrower) && PresentationHost.IsFaulted(enableThrower) && enableThrower.Disables == 1,
                    $"OnEnable 抛异常：Register 不往外抛、模块没被标成启用、已调 OnDisable 收拾半截对象（OnDisable {enableThrower.Disables} 次）");
                Vector3 p0 = root.GetChild(5).localPosition;
                bool tickThrew = false;
                try
                {
                    for (int f = 0; f < 3; f++)
                    {
                        PresentationHost.Tick(Frame(0.25f));
                    }
                }
                catch (Exception)
                {
                    tickThrew = true;
                }
                Expect(!tickThrew && thrower.Ticks == 1 && !PresentationHost.IsActive(thrower) && PresentationHost.IsFaulted(thrower) && thrower.Disables == 1,
                    $"Tick 抛异常：宿主不往外抛、该模块只被 Tick 1 次后停用并 OnDisable（Tick {thrower.Ticks} 次、OnDisable {thrower.Disables} 次）");
                Expect(PresentationHost.IsActive(probe) && root.GetChild(5).localPosition != p0, "同一宿主里的探针照常启用、照常被 Tick（方块还在动）");
                PresentationHost.SetEnabled(false);
                PresentationHost.SetEnabled(true);
                Expect(!PresentationHost.IsActive(thrower) && thrower.Enables == 1 && enableThrower.Enables == 1, $"总开关开关一次后故障模块不再启用（OnEnable 各 {thrower.Enables} / {enableThrower.Enables} 次）");
                PresentationHost.Unregister(thrower);
                PresentationHost.Unregister(enableThrower);

                bool unknownThrew = false;
                try
                {
                    PresentationHost.Register(new UnregisteredModule());
                }
                catch (ArgumentException)
                {
                    unknownThrew = true;
                }
                Expect(unknownThrew && PresentationHost.ModuleCount == 1, $"Id 没在登记表里的模块 Register 抛 ArgumentException、没进宿主（模块数 {PresentationHost.ModuleCount}）");
            }
            finally
            {
                PresentationHost.Unregister(probe);
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
            }
        }

        /// <summary>自检用：Tick 必抛（或 OnEnable 就抛）的模块，Id 借用探针的登记。</summary>
        private sealed class ThrowingModule : IPresentationModule
        {
            private readonly bool _throwOnEnable;
            public int Enables, Ticks, Disables;

            public ThrowingModule(bool throwOnEnable) => _throwOnEnable = throwOnEnable;

            public string Id => PresentationFeatureRegistry.ProbeId;

            public void OnEnable()
            {
                Enables++;
                if (_throwOnEnable)
                {
                    throw new InvalidOperationException("PL 自检：故意在 OnEnable 抛异常（宿主异常隔离反例，日志里这条是预期的）");
                }
            }

            public void OnDisable() => Disables++;

            public void Tick(in PresentationFrame frame)
            {
                Ticks++;
                throw new InvalidOperationException("PL 自检：故意在 Tick 抛异常（宿主异常隔离反例，日志里这条是预期的）");
            }

            public void Rebuild(RebuildReason reason)
            {
            }
        }

        private sealed class UnregisteredModule : IPresentationModule
        {
            public string Id => "pl.selfcheck.unregistered";
            public void OnEnable() { }
            public void OnDisable() { }
            public void Tick(in PresentationFrame frame) { }
            public void Rebuild(RebuildReason reason) { }
        }

        private static object _allocSink;

        /// <summary>
        /// 零托管分配断言（后续卡复用）：用 Unity 的 GC.Alloc 采样标记计数（<c>UnityEngine.Profiling.Recorder.Get("GC.Alloc")</c>，
        /// 只看当前线程，与 Unity Test Framework 的 <c>Is.Not.AllocatingGCMemory()</c> 同一机制）数 <paramref name="body"/> 连跑
        /// <paramref name="repeat"/> 遍里的分配次数，必须为 0。按次数计、不受别的线程和 Boehm 空闲链表影响，检出下限 = 1 次分配
        /// （HostLifecycle 段断言：600 帧里只 new 一次 object 也判失败）。先跑一遍预热（JIT、静态构造、懒初始化不算）。
        /// 每次先用 new object() 标定计量器；标定计 0（计量器失效）时返回 false，不会空转通过。
        /// 不用 GC.GetAllocatedBytesForCurrentThread（Editor 的 Mono 下恒 0）和 Profiler.GetMonoUsedSizeLong（进程级、4 KB 页、会被空闲链表吸收，11-20 实测纯算术循环也会读到增量）。
        /// 只能抓 body 里出现的分配；只在少数帧才走到的分支要让 body 覆盖到。
        /// </summary>
        public static bool MeasureZeroAlloc(Action body, out string detail, int repeat = 1)
        {
            body();
            int calib = CountAllocs(() => _allocSink = new object());
            if (calib < 1)
            {
                detail = "计量器失效：Recorder(GC.Alloc) 对一次 new object() 计 0 次";
                return false;
            }
            int n = CountAllocs(() =>
            {
                for (int r = 0; r < repeat; r++)
                {
                    body();
                }
            });
            detail = $"Recorder(GC.Alloc，当前线程) 计到 {n} 次分配（body × {repeat}；标定 new object() 计 {calib} 次）";
            return n == 0;
        }

        private static int CountAllocs(Action act)
        {
            UnityEngine.Profiling.Recorder rec = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            rec.enabled = false; // 先关一次：冲掉创建 Recorder 本身等之前的计数
            rec.FilterToCurrentThread();
            rec.enabled = true;
            try
            {
                act();
            }
            finally
            {
                rec.enabled = false;
                rec.CollectFromAllThreads();
            }
            return rec.sampleBlockCount;
        }

        // ── Space：浮动原点往返精度与版本号 ──

        private static void Space()
        {
            double3 saved = PresentationSpace.Origin;
            try
            {
                int r0 = PresentationSpace.Revision;
                var origin = new double3(100000, 0, 100000);
                PresentationSpace.SetOrigin(origin);
                Expect(PresentationSpace.Revision == r0 + 1, $"设原点后 Revision +1（{r0} → {PresentationSpace.Revision}）");
                PresentationSpace.SetOrigin(origin);
                Expect(PresentationSpace.Revision == r0 + 2, "再设一次（同值）Revision 再 +1");
                double maxErr = 0;
                var rng = new System.Random(9201);
                for (int i = 0; i < 1000; i++)
                {
                    var w = new double3(origin.x + (rng.NextDouble() - 0.5) * 4000, rng.NextDouble() * 200 - 50, origin.z + (rng.NextDouble() - 0.5) * 4000);
                    double3 back = PresentationSpace.FromRender(PresentationSpace.ToRender(w));
                    maxErr = Math.Max(maxErr, math.cmax(math.abs(back - w)));
                }
                Expect(maxErr <= 1e-3, $"原点 (100000, 0, 100000)、原点 ±2000 米内 1000 点 ToRender → FromRender 最大误差 {maxErr:E2} 米（≤ 1e-3）");
                Expect(PresentationSpace.ToRender(origin) == Vector3.zero, "原点本身映射到渲染零点");
            }
            finally
            {
                PresentationSpace.SetOrigin(saved);
            }
        }

        // ── Materials：唯一 Shader.Find 入口、缓存、实例化 ──

        internal static readonly string[] PlSourceDirs =
        {
            "Assets/GameScripts/Main/Presentation",
            "Assets/GameScripts/HotFix/GameLogic/View/Presentation",
            "Assets/PresentationLab",
        };

        private static void Materials()
        {
            string[] files = PlSourceDirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)).ToArray();
            string[] offenders = files.Where(f => Path.GetFileName(f) != "PresentationMaterials.cs" && File.ReadAllText(f).Contains("Shader.Find(")).ToArray();
            Expect(files.Length > 0 && offenders.Length == 0, $"PL 源码 {files.Length} 个文件里 Shader.Find( 只出现在 PresentationMaterials.cs（违规：{string.Join(", ", offenders)}）");

            var color = new Color(0.31f, 0.42f, 0.53f, 1f);
            int count0 = PresentationMaterials.Count;
            bool same = true, inst = true, shaderOk = true;
            var shaders = new StringBuilder();
            foreach (PresentationMaterialUse use in Enum.GetValues(typeof(PresentationMaterialUse)))
            {
                Material a = PresentationMaterials.Get(use, color);
                Material b = PresentationMaterials.Get(use, color);
                same &= a != null && ReferenceEquals(a, b);
                inst &= a != null && a.enableInstancing;
                shaderOk &= a != null && a.shader != null && a.shader.name == PresentationMaterials.ShaderName(use);
                shaders.Append(use).Append('→').Append(a != null && a.shader != null ? a.shader.name : "null").Append("；");
            }
            int uses = Enum.GetValues(typeof(PresentationMaterialUse)).Length;
            Expect(same, "同 (用途, 颜色) 两次取回同一实例");
            Expect(PresentationMaterials.Count == count0 + uses || PresentationMaterials.Count == count0, $"缓存份数只按用途 × 颜色增长（{count0} → {PresentationMaterials.Count}）");
            Expect(!ReferenceEquals(PresentationMaterials.Get(PresentationMaterialUse.Lit, color), PresentationMaterials.Get(PresentationMaterialUse.Lit, Color.white)), "不同颜色是不同材质");
            Material hdrA = PresentationMaterials.Get(PresentationMaterialUse.Glow, new Color(4f, 2f, 1f));
            Material hdrB = PresentationMaterials.Get(PresentationMaterialUse.Glow, new Color(2f, 2f, 2f));
            Material hdrW = PresentationMaterials.Get(PresentationMaterialUse.Glow, Color.white);
            Material nearW = PresentationMaterials.Get(PresentationMaterialUse.Glow, new Color(1f, 1f, 1f - 1e-4f));
            Expect(!ReferenceEquals(hdrA, hdrB) && !ReferenceEquals(hdrA, hdrW) && !ReferenceEquals(hdrB, hdrW) && !ReferenceEquals(hdrW, nearW),
                "HDR 颜色 (4,2,1) / (2,2,2) / 白、以及与白只差 1e-4 的颜色各取回不同实例（缓存键不截断、不量化）");
            Expect(inst, "全部用途 enableInstancing = true");
            Expect(shaderOk, $"每个用途的 shader 都找到了、与映射一致：{shaders}");
            if (PresentationMaterials.IsBuiltIn)
            {
                Expect(PresentationMaterials.ShaderName(PresentationMaterialUse.Lit) == "Standard" &&
                       PresentationMaterials.ShaderName(PresentationMaterialUse.Unlit) == "BinGames/SimInstancedUnlit" &&
                       PresentationMaterials.ShaderName(PresentationMaterialUse.Glow) == "BinGames/SimInstancedUnlit",
                    "Built-in 下 Lit → Standard，Unlit / Glow → BinGames/SimInstancedUnlit");
            }
            else
            {
                Line($"  - 跳过：当前不是 Built-in 管线，URP 的 shader 映射未实测（{shaders}）");
            }
        }

        // ── Lab：场景存在、生成物齐全、生成物不存进场景、关掉无残留 ──

        [MenuItem("QA/表现泳道/打开 Lab 场景")]
        public static void OpenLab()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
            EnsureLabScene();
            EditorSceneManager.OpenScene(LabScenePath, OpenSceneMode.Single);
        }

        /// <summary>Lab 场景不存在时生成（场景里只有一个挂 PresentationLabRig 的物体，其余全由它程序生成）。</summary>
        public static void EnsureLabScene()
        {
            if (File.Exists(LabScenePath))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LabScenePath));
            // 未保存的无名场景下不能 Additive 新建：单场景新建（调用方已在交互模式下问过保存）
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("PresentationLab").AddComponent<PresentationLabRig>();
            EditorSceneManager.SaveScene(scene, LabScenePath);
            AssetDatabase.Refresh();
        }

        private static void Lab()
        {
            // 本段会换掉当前场景：交互模式下先问保存，取消就跳过
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Line("  - 取消保存当前场景，跳过 Lab 段");
                return;
            }
            EnsureLabScene();
            Expect(File.Exists(LabScenePath), $"Lab 场景存在：{LabScenePath}");
            string text = File.ReadAllText(LabScenePath);
            Expect(!text.Contains("Ground_Flat") && !text.Contains("LabCamera"), "场景文件里没有存生成物（只存台子组件）");

            Scene scene = EditorSceneManager.OpenScene(LabScenePath, OpenSceneMode.Single);
            try
            {
                PresentationLabRig rig = scene.GetRootGameObjects().Select(g => g.GetComponent<PresentationLabRig>()).FirstOrDefault(r => r != null);
                Expect(rig != null, "Lab 场景里有 PresentationLabRig");
                if (rig == null)
                {
                    return;
                }
                rig.Build();
                Camera cam = rig.LabCamera;
                Expect(cam != null && cam.orthographic && Mathf.Abs(Mathf.DeltaAngle(cam.transform.eulerAngles.x, PresentationLabRig.CameraPitch)) < 0.01f,
                    $"正交相机、俯视 {PresentationLabRig.CameraPitch}°（实际 {(cam != null ? cam.transform.eulerAngles.x : -1):F2}°）");
                string[] need = { "Ground_Flat", "Ground_Slope", "Ground_Cliff", "Ground_Water", "Synth_Vehicle", "Synth_Patch", "Synth_EventEmitter", "Grid_1m" };
                Transform[] all = rig.GetComponentsInChildren<Transform>(true);
                string[] missing = need.Where(n => all.All(t => t.name != n)).ToArray();
                Expect(missing.Length == 0, $"四块合成地面、三个占位、参照格齐全（缺：{string.Join(", ", missing)}）");
                Mesh grid = all.First(t => t.name == "Grid_1m").GetComponent<MeshFilter>().sharedMesh;
                Vector3[] v = grid.vertices;
                Expect(Mathf.Approximately(v[4].x - v[0].x, 1f) && v.Length == (PresentationLabRig.GridHalfCells * 2 + 1) * 4, $"参照格 1 格 = 1 米（相邻线距 {v[4].x - v[0].x}，{v.Length / 4} 组线）");
                Renderer[] rs = rig.GetComponentsInChildren<Renderer>(true);
                Expect(rs.Length >= need.Length && rs.All(r => r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("PL ", StringComparison.Ordinal)),
                    $"Lab 的 {rs.Length} 个渲染器材质全部来自 PresentationMaterials");
                Expect(all.Where(t => t != rig.transform).All(t => (t.gameObject.hideFlags & HideFlags.DontSave) == HideFlags.DontSave), "生成物都带 DontSave");
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            bool leftover = Resources.FindObjectsOfTypeAll<GameObject>().Any(g => g.name == "[LabContent]" && g.scene.IsValid());
            Expect(!leftover, "换掉 Lab 场景后生成物无残留");
        }

        // ── SimHashOnOff：全部 PL 功能关 / 开，四个 sim 哈希逐个相等 ──

        private static void SimHashOnOff()
        {
            bool ok = PresentationSimHash.CheckOnOff(out string detail, new PresentationProbeModule());
            Expect(ok, "全部 PL 功能关 / 开（含探针模块）sim 哈希一致：" + detail);
            Expect(PresentationHost.RootChildCount == 0, "夹具收尾后 [PresentationRoot] 无残留");

            // 反例：开的那次模块被停用（Tick 抛异常）时夹具必须判失败，不能因为哈希一致就空转通过
            bool idle = PresentationSimHash.CheckOnOff(out string idleDetail, PresentationSimHash.DefaultSeed, 30, new ThrowingModule(false));
            Expect(!idle && idleDetail.Contains("夹具判失败"), "反例：开的那次模块没真正运行（抛异常被停用）→ CheckOnOff 判失败：" + idleDetail);

            // 有战役、家园已载入的生产路径：TickManual（桥 + 宿主 + 启用中的探针）零分配
            bool inWorldZero = false, loaded = false;
            string inWorldDetail = "没跑到";
            PresentationSimHash.Run(PresentationSimHash.DefaultSeed, 30, true, new IPresentationModule[] { new PresentationProbeModule() }, () =>
            {
                loaded = Campaign.CampaignSession.Current != null && WorldSimulation.AnyLoaded && PresentationHost.ActiveCount == 1;
                inWorldZero = MeasureZeroAlloc(() =>
                {
                    for (int i = 0; i < 600; i++)
                    {
                        PresentationHostBridge.TickManual(1f / 60f, 0f);
                    }
                }, out inWorldDetail);
            });
            Expect(loaded && inWorldZero, $"有战役、家园已载入、探针启用：TickManual 600 帧托管分配次数 = 0（世界已载入 {loaded}）：" + inWorldDetail);
            Expect(PresentationHost.RootChildCount == 0 && PresentationHost.ModuleCount == 0, "两次夹具收尾后宿主无模块、[PresentationRoot] 无残留");
        }

        // ── FeaturesDoc：FEATURES.md 与登记表一致 ──

        public static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (; dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static void FeaturesDoc()
        {
            string path = RepoFile("production/design/presentation-track/FEATURES.md");
            Expect(path != null, "找到 FEATURES.md（从工程目录向上找）");
            if (path == null)
            {
                return;
            }
            var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string l = raw.Trim();
                if (!l.StartsWith("|", StringComparison.Ordinal) || l.Contains("---") || l.Contains("功能 ID"))
                {
                    continue;
                }
                string[] c = l.Trim('|').Split('|').Select(x => x.Trim().Trim('`')).ToArray();
                if (c.Length >= 4 && c[0].Length > 0)
                {
                    rows[c[0]] = c;
                }
            }
            bool? Parse(string s) => s == "开" ? true : s == "关" ? false : (bool?)null;
            var problems = new List<string>();
            foreach (PresentationFeature f in PresentationFeatureRegistry.Features)
            {
                if (!rows.TryGetValue(f.Id, out string[] c))
                {
                    problems.Add($"{f.Id} 没在表里");
                    continue;
                }
                if (c[1] != f.Switch)
                {
                    problems.Add($"{f.Id} 开关名 表={c[1]} 代码={f.Switch}");
                }
                if (Parse(c[2]) != f.DefaultPL)
                {
                    problems.Add($"{f.Id} PL 默认 表={c[2]} 代码={(f.DefaultPL ? "开" : "关")}");
                }
                if (Parse(c[3]) != f.DefaultMain)
                {
                    problems.Add($"{f.Id} 合主线默认 表={c[3]} 代码={(f.DefaultMain ? "开" : "关")}");
                }
            }
            problems.AddRange(rows.Keys.Where(id => !PresentationFeatureRegistry.IsRegistered(id)).Select(id => $"{id} 在表里但代码没登记"));
            Expect(problems.Count == 0, $"FEATURES.md 表 {rows.Count} 行与登记表 {PresentationFeatureRegistry.Features.Count} 项一致（ID、开关名、两套默认）{(problems.Count > 0 ? "：" + string.Join("；", problems) : "")}");
        }
    }
}
