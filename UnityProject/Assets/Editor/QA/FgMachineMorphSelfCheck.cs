using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-VFX-01 机身形变首批的自动验收（FG02 FGR-FW-020～022、FGT-FW-004；卡片负向：形变过程中阵亡、快速反复接入和离开）。
    /// 全部起真实系统：整个世界（家园 / 破碎都市控制器 + 战斗内核 + 统一时钟 + 全局镜头）、真输入路由（选中 + 接入键 / 机器列表）、
    /// 真实存档文件——形变表现只读战斗桥接层与武器参数同一次解析的编译结果，行为坏了会失败：
    /// A 数据（类别列、过渡调参、6 个作战组件都有三套状态）；B 部件库（三角面预算、单 MeshFilter、网格 / 材质共享、实例化开关）；
    /// C 默认战略缩放下可辨认（俯视栅格化：每个组件 × 每类状态新增的轮廓与自发光像素、三类互相区分）；
    /// D FGT-FW-004 正式链路（接入 → 状态出现 → 离开 → 复原；AI 自带固件的常驻状态；多类叠加；Tab 切机两台同时过渡；装配变更）；
    /// E 负向（形变过程中阵亡、快速反复接入离开就地折返、表现中途被卸载、没有作战组件、引信类不改机身）；
    /// F 暂停与 0.5x～3x；G 真实文件存读档（读档直接到位不重播）；H 观察无关（后台一致、重新观察直接到位）；I 性能与零分配。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgMachineMorphSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_vfx01_cannon_up";
        private const string BpGunTrail = "bp_selfcheck_vfx01_gun_trail";
        private const string BpGunMarkerUp = "bp_selfcheck_vfx01_gun_marker_up";
        private const string BpGunPlain = "bp_selfcheck_vfx01_gun_plain";
        private const float FrameDt = 0.05f;

        /// <summary>默认战略缩放：家园 / 破碎都市初始正交半高 30；铸造前哨初始 52 会被缩放上限 camera.zoom_max_ortho（46）夹住——取最远的 46 做下限验收。</summary>
        private const float OrthoHome = HomeValleyLayout.CameraBoundsHalfExtentZ;
        private const float OrthoFar = 46f;
        private const int ScreenHeight = 1080;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/FG1-VFX-01 机身形变自检")]
        public static void RunFromMenu()
        {
            var sb = new StringBuilder();
            int fails = Run(sb);
            Debug.Log(sb.ToString());
            Debug.Log(fails == 0 ? "[形变] 全部通过" : $"[形变] 失败 {fails} 项");
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[形变] 机身形变首批（FG1-VFX-01）");
            Snapshot snap = null;
            try
            {
                snap = SetUp();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，图形设备 {SystemInfo.graphicsDeviceType}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckLibrary);
                Step(CheckReadability);
                Step(CheckFormalJourney);
                Step(CheckOverlayAndSwitch);
                Step(CheckLoadoutChange);
                Step(CheckDeathMidMorph);
                Step(CheckRapidToggle);
                Step(CheckDetachAndNoComponent);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckObservation);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"形变自检抛异常：{e}");
            }
            finally
            {
                TearDown(snap);
            }
            Line($"  · [形变] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        /// <summary>自检前的全局状态（跑完原样恢复）。</summary>
        private sealed class Snapshot
        {
            public GameLanguage Language;
            public CampaignState Session;
            public int Slot;
            public string Prefs;
            public bool HadPrefs;
            public bool HadCamera;
            public Func<float> Delta;
            public Func<bool> AutoPause;
        }

        /// <summary>载入配置与文本、接管时钟 / 输入 / 存档目录（与正式游戏同一套世界提供者）。</summary>
        private static Snapshot SetUp()
        {
            var snap = new Snapshot
            {
                Language = GameSettings.Language,
                Session = CampaignSession.Current,
                Slot = CampaignSession.ActiveSlotIndex,
                Prefs = PlayerPrefs.GetString(SettingsPrefsKey, null),
                HadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey),
                HadCamera = Camera.main != null,
                Delta = CameraDirector.RealDeltaTime,
                AutoPause = NotificationCenter.AutoPauseHandler,
            };
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgvfx01-selfcheck-" + Guid.NewGuid().ToString("N"));
            ConfigSystem.Instance.Load();
            GameText.Reload();
            GridContent.Reload();
            WorldGenContent.Reload();
            FgContentTables.Reload();
            FirmwareKinds.Reload();
            GameClock.ReloadTuning();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            Directory.CreateDirectory(_dir);
            CameraDirector.RealDeltaTime = () => FrameDt;
            NotificationCenter.AutoPauseHandler = null;
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            GameRoot.BindWorldProviders();
            return snap;
        }

        private static void TearDown(Snapshot snap)
        {
            try
            {
                WorldSimulation.UnloadAll();
            }
            catch (Exception e)
            {
                Fail("收尾 UnloadAll 抛异常：" + e.Message);
            }
            MachineMorphView.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalCoreService.ResetForTests();
            SignalPresence.ResetForTests();
            FirmwareKinds.ResetForTests();
            GameClock.ResetSession();
            InputRouter.DebugSetReader(null);
            InputRouter.Reset();
            GridContent.ResetForTests();
            WorldGenContent.ResetForTests();
            HomeGridService.Invalidate();
            BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
            CampaignSaveService.SaveDirectoryOverrideForTests = null;
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            UiEscapeStack.Clear();
            if (snap != null)
            {
                CameraDirector.RealDeltaTime = snap.Delta;
                NotificationCenter.AutoPauseHandler = snap.AutoPause;
                GameSettings.SetLanguage(snap.Language);
                if (!snap.HadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (snap.HadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, snap.Prefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                if (snap.Session != null)
                {
                    CampaignSession.Set(snap.Slot, snap.Session);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            try
            {
                if (_dir != null)
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }

        // ── 证据截图（MorphEvidenceCapture）用的真实世界准备：与自检同一套入口，不计断言 ──

        private static Snapshot _evidenceSnap;

        /// <summary>证据截图开场：接管配置 / 时钟 / 输入（与自检相同）。准备失败记进 <paramref name="report"/>，由 <see cref="EvidencePrepFailures"/> 计数。</summary>
        internal static void EvidenceBegin(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            _evidenceSnap = SetUp();
        }

        internal static void EvidenceEnd()
        {
            TearDown(_evidenceSnap);
            _evidenceSnap = null;
        }

        internal static int EvidencePrepFailures => _fail;

        internal const string EvidenceBpCannonUp = BpCannonUp;
        internal const string EvidenceBpGunTrail = BpGunTrail;
        internal const string EvidenceBpGunPlain = BpGunPlain;

        /// <summary>新开局、载入家园并让全局镜头观察它；发电机 / 装配站投运，登记自检用的几张蓝图。</summary>
        internal static CampaignState EvidenceNewHome(int seed) => NewHome(seed);

        internal static int EvidenceSpawnHome(string bp, Vector2 offsetFromCore) => SpawnHome(bp, offsetFromCore);

        internal static void EvidenceEquipCore(CampaignState s, params string[] firmwareIds) => EquipCore(s, firmwareIds);

        internal static void EvidenceFrames(int n) => Frames(n);

        internal static void EvidencePressUplinkKey() => Press(Key(GameActionId.ToggleCameraView));

        // ── A. 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：固件类别入表（核心全是限制器）、过渡 0.3 秒入表、Demo 的 6 个作战组件都有三套状态");
            var expected = new Dictionary<string, FirmwareCategory>
            {
                { FirmwareCatalog.FwHomingId, FirmwareCategory.Fuse },
                { FirmwareCatalog.FwSplitId, FirmwareCategory.Fuse },
                { FirmwareCatalog.FwTrailId, FirmwareCategory.Fluid },
                { FirmwareCatalog.FwOverloadId, FirmwareCategory.Limiter },
                { FirmwareCatalog.FwMarkTagId, FirmwareCategory.Limiter },
                { FirmwareCatalog.FwArmorPierceId, FirmwareCategory.Fuse },
            };
            var wrong = expected.Where(kv => FirmwareKinds.CategoryOf(kv.Key) != kv.Value).Select(kv => $"{kv.Key}={FirmwareKinds.CategoryOf(kv.Key)}").ToList();
            Expect(wrong.Count == 0, $"固件类别按设计案 5.4 名表（寻的 / 分裂 / 装甲击穿 = 引信，拖尾 = 流体，过载 / 标记跳转 = 限制器）{(wrong.Count > 0 ? "；不符：" + string.Join("、", wrong) : "")}");
            var rows = FirmwareKinds.Rows;
            var bad = rows.Where(r => r.Kind == "core" && r.Category != "limiter").Select(r => r.Id).ToList();
            Expect(rows.Count >= 10 && bad.Count == 0 && rows.All(r => new[] { "fuse", "limiter", "fluid", "em" }.Contains(r.Category)),
                $"fg.TbFirmwareKind {rows.Count} 行都有合法类别，核心固件全部是限制器{(bad.Count > 0 ? "；违规：" + string.Join("、", bad) : "")}");
            Expect(FirmwareKinds.CategoryOf("organ_focus") == FirmwareCategory.Unknown && MachineMorph.BitOf(null) == MorphMask.None,
                "不是固件 / 空 ID：类别 Unknown，不产生形变");
            Expect(GridContent.TryGetTuning("morph.transition_seconds", out float t) && Mathf.Approximately(t, 0.3f) && Mathf.Approximately(MachineMorph.TransitionSeconds, 0.3f),
                $"过渡秒数读表 morph.transition_seconds = {t}（FGR-FW-021 初值 0.3）");
            // “Demo 已有的作战组件”= 组件目录里主组件与功能组件槽的全部条目：必须与有机身状态的组件完全一致（新增组件而忘了做形变会被这里拦下）。
            var catalogCombat = ComponentCatalog.All.Values.Where(c => c.Slot == "主组件" || c.Slot == "功能组件").Select(c => c.Id).OrderBy(x => x).ToList();
            var morphComps = MachineMorph.MorphComponents.OrderBy(x => x).ToList();
            Expect(catalogCombat.Count == 6 && catalogCombat.SequenceEqual(morphComps),
                $"作战组件 {catalogCombat.Count} 个（{string.Join("、", catalogCombat)}）与有机身状态的组件逐一对应");
            var fuseOnly = new BlueprintCircuitPreview { PrimaryId = ComponentCatalog.CompGunId, FirmwareIds = new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwSplitId } };
            var mixed = new BlueprintCircuitPreview { PrimaryId = ComponentCatalog.CompGunId, FirmwareIds = new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwTrailId, FirmwareCatalog.FwOverloadId } };
            var noComp = new BlueprintCircuitPreview { PrimaryId = null, UtilityId = null, FirmwareIds = new[] { FirmwareCatalog.FwTrailId } };
            var utilOnly = new BlueprintCircuitPreview { PrimaryId = null, UtilityId = ComponentCatalog.FuncMarkerId, FirmwareIds = new[] { FirmwareCatalog.FwTrailId } };
            Expect(MachineMorph.MaskOf(fuseOnly) == MorphMask.None && MachineMorph.MaskOf(mixed) == (MorphMask.Fluid | MorphMask.Limiter)
                   && MachineMorph.MaskOf(noComp) == MorphMask.None && MachineMorph.MaskOf(utilOnly) == MorphMask.Fluid,
                "编译结果 → 状态：只有引信类 = 不变形；引信 + 流体 + 限制器 = 喷口态 + 形变态；没有作战组件 = 不变形；只有功能组件也变形");
        }

        // ── B. 部件库 ───────────────────────────────────────────────────────────────

        private static void CheckLibrary()
        {
            Line("  · B. 部件库：6 组件 × 3 状态、三角面 ≤ 1,000、每件一个 MeshFilter、网格 / 材质全机共用、GPU Instancing");
            MachineMorphView.ResetForTests();
            var tris = new List<string>();
            bool budgetOk = true;
            var signatures = new Dictionary<MorphMask, HashSet<Mesh>>();
            foreach (MorphMask cat in MachineMorph.Categories)
            {
                signatures[cat] = new HashSet<Mesh>();
                foreach (string comp in MachineMorph.MorphComponents)
                {
                    int n = MachineMorphLibrary.StateTriangles(comp, cat);
                    tris.Add($"{comp}/{MachineMorph.Describe(cat)}={n}");
                    budgetOk &= n > 0 && n <= MachineMorphLibrary.TriangleBudgetPerState;
                    Mesh own = MachineMorphLibrary.MeshFor(comp, cat, MachineMorphLibrary.PartRole.Glow) ?? MachineMorphLibrary.MeshFor(comp, cat, MachineMorphLibrary.PartRole.Solid);
                    if (own != null)
                    {
                        signatures[cat].Add(own);
                    }
                }
            }
            Expect(budgetOk, $"每个组件每套状态都有几何、三角面 ≤ {MachineMorphLibrary.TriangleBudgetPerState}（美术规则 02 §2）：{string.Join("，", tris)}");
            Expect(signatures.Values.All(set => set.Count == MachineMorph.MorphComponents.Count),
                "每个组件在每类状态下都有自己的组件件（不是全组件共用一套）：6 × 3 = 18 套不同的组件网格");

            var hostA = new GameObject("MorphLibA").transform;
            var hostB = new GameObject("MorphLibB").transform;
            try
            {
                Transform rigA = MachineMorphView.BuildRig(hostA, ComponentCatalog.CompCannonId, ComponentCatalog.FuncMarkerId, out int partsA);
                int meshesAfterA = MachineMorphLibrary.MeshCount;
                Transform rigB = MachineMorphView.BuildRig(hostB, ComponentCatalog.CompCannonId, ComponentCatalog.FuncMarkerId, out int partsB);
                var filtersA = rigA.GetComponentsInChildren<MeshFilter>(true);
                var filtersB = rigB.GetComponentsInChildren<MeshFilter>(true);
                bool shared = filtersA.Length == filtersB.Length && filtersA.Length > 0
                              && filtersA.Zip(filtersB, (x, y) => x.sharedMesh == y.sharedMesh && x.GetComponent<MeshRenderer>().sharedMaterial == y.GetComponent<MeshRenderer>().sharedMaterial).All(ok => ok);
                Expect(partsA == partsB && partsA == filtersA.Length && shared && MachineMorphLibrary.MeshCount == meshesAfterA,
                    $"两台同配置机器：{partsA} 个部件逐一共用同一网格与同一材质，第二台不新建网格（网格缓存 {MachineMorphLibrary.MeshCount} 份）");
                bool oneFilterEach = filtersA.All(f => f.GetComponentsInChildren<MeshFilter>(true).Length == 1 && f.GetComponentInChildren<MeshFilter>(true) == f);
                Expect(oneFilterEach, "每个形变部件 = 一个 GameObject 一个 MeshFilter（“外形槽只取第一个 MeshFilter”的坑：第一个就是唯一一个，不会丢件）");
                var mats = filtersA.Select(f => f.GetComponent<MeshRenderer>().sharedMaterial).Distinct().ToList();
                bool instancing = mats.All(m => m != null && m.enableInstancing && ShaderSupportsInstancing(m.shader));
                bool noShadows = filtersA.All(f => f.GetComponent<MeshRenderer>().shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off);
                Expect(instancing && noShadows && MachineMorphLibrary.MaterialCount <= 4,
                    $"材质 {mats.Count} 种（{string.Join("、", mats.Select(m => m.shader.name).Distinct())}）全部开 GPU Instancing 且着色器带实例化变体；不投实时阴影；全局材质 ≤ 4 份");
                bool placeholder = filtersA.All(f => f.gameObject.name.Contains(MachineMorphView.PlaceholderTag));
                bool groups = MachineMorph.Categories.All(c => rigA.Find(c == MorphMask.Limiter ? "Limiter" : c == MorphMask.Fluid ? "Fluid" : "Electromagnetic") != null);
                Expect(placeholder && groups, "部件节点名带占位标记（B22），三类状态各有独立挂点组（叠加不互相覆盖）");
                Transform beamA = rigA.Find("Limiter/" + MachineMorphLibrary.SignalBeamName);
                Mesh beamMesh = MachineMorphLibrary.MeshFor(MachineMorphLibrary.ChassisKey, MorphMask.Limiter, MachineMorphLibrary.PartRole.Signal);
                bool beamOnlyLimiter = MachineMorphLibrary.MeshFor(MachineMorphLibrary.ChassisKey, MorphMask.Fluid, MachineMorphLibrary.PartRole.Signal) == null
                                       && MachineMorphLibrary.MeshFor(MachineMorphLibrary.ChassisKey, MorphMask.Electromagnetic, MachineMorphLibrary.PartRole.Signal) == null
                                       && MachineMorphLibrary.MeshFor(ComponentCatalog.CompCannonId, MorphMask.Limiter, MachineMorphLibrary.PartRole.Signal) == null;
                Expect(beamA != null && !beamA.gameObject.activeSelf && beamMesh != null && beamOnlyLimiter
                       && beamA.GetComponent<MeshRenderer>().sharedMaterial == MachineMorphLibrary.MaterialFor(MorphMask.Electromagnetic, MachineMorphLibrary.PartRole.Glow),
                    "美术规则 05 §4：形变态带接入信号光柱（只有底盘件的形变态有、默认关着、与线圈光环共用青蓝材质，不多出材质份数）");
                Mesh coilGlow = MachineMorphLibrary.MeshFor(MachineMorphLibrary.ChassisKey, MorphMask.Electromagnetic, MachineMorphLibrary.PartRole.Glow);
                int arcTris = coilGlow != null ? coilGlow.triangles.Length / 3 - 28 * 8 : 0; // 光环 28 段 × 8 个三角面，其余是电弧
                Expect(arcTris >= 6 * 3 * 12, $"美术规则 05 §4：线圈态表面有细小电弧（6 处 × 3 段折线，{arcTris} 个三角面）");
                for (int i = 0; i < 40; i++)
                {
                    var h = new GameObject("MorphLibExtra").transform;
                    MachineMorphView.BuildRig(h, ComponentCatalog.CompCannonId, ComponentCatalog.FuncMarkerId, out _);
                    Object.DestroyImmediate(h.gameObject);
                }
                Expect(MachineMorphLibrary.MeshCount == meshesAfterA, $"再建 40 台：网格份数不变（{MachineMorphLibrary.MeshCount}），不按机器生成独立网格（FG02 第 7 章）");
            }
            finally
            {
                Object.DestroyImmediate(hostA.gameObject);
                Object.DestroyImmediate(hostB.gameObject);
            }
            MachineMorphLibrary.ReleaseAll();
            Expect(MachineMorphLibrary.MeshCount == 0 && MachineMorphLibrary.MaterialCount == 0, "世界卸载时共享网格与材质全部释放");
        }

        // ── C. 默认战略缩放下可辨认（FGR-FW-022）────────────────────────────────────

        private static void CheckReadability()
        {
            Line($"  · C. FGR-FW-022：默认战略缩放（正交半高 {OrthoHome}，以及最远的 {OrthoFar}）、1920×1080、俯视：每个组件每类状态新增的轮廓与自发光像素");
            var host = new GameObject("MorphReadHost");
            try
            {
                Renderer hostRenderer = host.AddComponent<MeshRenderer>();
                host.AddComponent<MeshFilter>();
                hostRenderer.sharedMaterial = ViewMaterials.Standard(Color.gray);
                PlaceholderSilhouette.ApplyMachine(host, hostRenderer, HomeValleyLayout.Erc003ChassisId);
                var baseTris = CollectTriangles(host.transform.Find(PlaceholderSilhouette.PartsName));
                var minima = new List<string>();
                bool allOk = true;
                bool distinct = true;
                foreach (string comp in MachineMorph.MorphComponents)
                {
                    Transform rig = MachineMorphView.BuildRig(host.transform, comp, null, out _);
                    var added = new Dictionary<MorphMask, HashSet<int>>();
                    foreach (MorphMask cat in MachineMorph.Categories)
                    {
                        Transform group = rig.GetChild(MachineMorph.IndexOf(cat));
                        group.gameObject.SetActive(true);
                        group.localScale = Vector3.one;
                        var solid = CollectTriangles(group, MachineMorphLibrary.PartRole.Solid);
                        var glow = CollectTriangles(group, MachineMorphLibrary.PartRole.Glow);
                        foreach (float ortho in new[] { OrthoHome, OrthoFar })
                        {
                            HashSet<int> basePx = Raster(baseTris, ortho);
                            HashSet<int> morphPx = Raster(solid.Concat(glow).ToList(), ortho);
                            HashSet<int> glowPx = Raster(glow, ortho);
                            int outline = morphPx.Count(p => !basePx.Contains(p));
                            int minOutline = ortho >= OrthoFar ? 60 : 140;
                            int minGlow = ortho >= OrthoFar ? 12 : 28;
                            bool ok = outline >= minOutline && glowPx.Count >= minGlow;
                            allOk &= ok;
                            if (ortho >= OrthoFar)
                            {
                                minima.Add($"{comp}/{MachineMorph.Describe(cat)}：轮廓+{outline}px 发光{glowPx.Count}px");
                                added[cat] = new HashSet<int>(morphPx.Where(p => !basePx.Contains(p)));
                            }
                            if (!ok)
                            {
                                Line($"    ✗ {comp}/{MachineMorph.Describe(cat)} 在正交半高 {ortho}：轮廓新增 {outline}px（要 ≥ {minOutline}）、自发光 {glowPx.Count}px（要 ≥ {minGlow}）");
                            }
                        }
                    }
                    // 三类状态彼此能分清：任意两类新增像素的重合 < 较小者的一半（不是同一块地方换个颜色）。
                    foreach (MorphMask x in MachineMorph.Categories)
                    {
                        foreach (MorphMask y in MachineMorph.Categories)
                        {
                            if (x >= y)
                            {
                                continue;
                            }
                            int overlap = added[x].Count(p => added[y].Contains(p));
                            if (overlap * 2 >= Math.Min(added[x].Count, added[y].Count))
                            {
                                distinct = false;
                                Line($"    ✗ {comp}：{MachineMorph.Describe(x)} 与 {MachineMorph.Describe(y)} 的新增轮廓重合 {overlap}px，太像");
                            }
                        }
                    }
                    Object.DestroyImmediate(rig.gameObject);
                }
                Expect(allOk, $"每个组件每类状态在两档默认缩放下都有明显的轮廓变化（正交 {OrthoHome}：≥140px；{OrthoFar}：≥60px）和自发光（≥28 / ≥12px），不是只靠小部件");
                Expect(distinct, "三类状态占不同方位（形变态向两侧加宽、喷口态向后加长并喷热光、线圈态一圈环）：两两新增轮廓重合都不到一半");
                Line("    · 正交 46 时各套状态：" + string.Join("；", minima));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        // ── D. FGT-FW-004 正式链路 ───────────────────────────────────────────────────

        private static void CheckFormalJourney()
        {
            Line("  · D. FGT-FW-004：选中 → 接入键 → 过渡完成那一刻开始形变（0.3 秒展开）→ 再按一次离开 → 0.3 秒收起、完全复原；形变只读编译结果");
            PlayerPrefs.DeleteKey(SettingsPrefsKey); // 清掉“已看过的引导钩子”，才测得出钩子在什么时机第一次发（收尾恢复原设置）
            GameSettings.Load();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignState s = NewHome(9101);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int g = SpawnHome(BpGunTrail, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            HomeValleyController home = WorldSimulation.Home;
            CombatSite site = home.Combat;
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Expect(MachineMorphView.IsRegistered(a) && MachineMorphView.IsRegistered(g), "家园被观察：两台机器表现都挂上了形变登记");
            Expect(MachineMorphView.TargetOf(a) == MorphMask.None && MachineMorphView.VisibleOf(a) == MorphMask.None && !GroupActive(a, MorphMask.Limiter),
                "接入前：重炮 + 接入口的机器 AI 驾驶，接入口是空槽——不变形，挂点组都收着");
            site.TryGetMachineWeapon(g, out MachineWeaponInfo gInfo);
            MachineCombatResolution gRes = MachineLoadoutRegistry.ResolveForPilot(s, g, s.RandomSeed);
            Expect(gInfo.Morph == MorphMask.Fluid && gInfo.Morph == MachineMorph.MaskOf(gRes.Preview) && MachineMorphView.VisibleOf(g) == MorphMask.Fluid
                   && Mathf.Approximately(MachineMorphView.ProgressOf(g, MorphMask.Fluid), 1f) && GroupActive(g, MorphMask.Fluid),
                "AI 驾驶、机器电路自己装着拖尾（流体）的连射器：常驻喷口态（与武器参数同一次解析的编译结果，挂上表现时直接到位不播过渡）");
            Expect(!GameSettings.HasSeenGuidanceHook(GuidanceHooks.MorphFirstSeen) && !MachineMorphView.SignalBeamShownOf(a) && !MachineMorphView.SignalBeamShownOf(g),
                $"AI 常驻状态挂上表现时直接到位：不消耗引导钩子 {GuidanceHooks.MorphFirstSeen}（留给玩家第一次接入引起的形变）；没接入的机器都不亮信号光柱");

            int starts0 = MachineMorphView.TransitionStarts;
            Expect(home.TrySelectMachine(a), "左键选中的同一入口选中重炮机");
            Press(Key(GameActionId.ToggleCameraView));
            Frames(3);
            bool pendingNoMorph = SignalUplinkService.IsPending && MachineMorphView.TargetOf(a) == MorphMask.None && MachineMorphView.VisibleOf(a) == MorphMask.None;
            Expect(pendingNoMorph, "接入过渡中（信号还在归还核心）：机身不提前变形");
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 40)
            {
                Frames(1);
            }
            float p0 = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo upInfo);
            Expect(SignalPresence.CurrentMachineLogicId == a && upInfo.Uplinked && upInfo.Morph == MorphMask.Limiter && MachineMorphView.TargetOf(a) == MorphMask.Limiter
                   && p0 > 0f && p0 < 1f && MachineMorphView.TransitionStarts == starts0 + 1 && MachineMorphView.AnimatingCount >= 1,
                $"接入完成：接入口插入过载（限制器）→ 目标 = 形变态，过渡开始（当前进度 {p0:0.00}）");
            Frames(1);
            float p1 = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            Frames(10);
            float p2 = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            Transform grp = MachineMorphView.GroupOf(a, MorphMask.Limiter);
            Expect(p1 > p0 && Mathf.Approximately(p2, 1f) && grp != null && grp.gameObject.activeSelf && Mathf.Approximately(grp.localScale.x, 1f)
                   && MachineMorphView.VisibleOf(a) == MorphMask.Limiter && MachineMorphView.PartCountOf(a) > 0,
                $"0.3 秒内逐帧展开（{p0:0.00} → {p1:0.00} → {p2:0.00}），展开后挂点组全尺寸可见（{MachineMorphView.PartCountOf(a)} 个部件）");
            Expect(MachineMorphView.VisibleOf(g) == MorphMask.Fluid, "旁边 AI 机器的喷口态不受影响");
            Transform beam = grp != null ? grp.Find(MachineMorphLibrary.SignalBeamName) : null;
            Expect(MachineMorphView.SignalBeamShownOf(a) && beam != null && beam.gameObject.activeInHierarchy && !MachineMorphView.SignalBeamShownOf(g),
                "美术规则 05 §4：形变态的接入口射出青蓝信号光柱（随形变态挂点组展开）；旁边没接入的 AI 机器不亮");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.MorphFirstSeen) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.MorphFirstSeen),
                $"画面上第一次出现机身形变时发出引导钩子 {GuidanceHooks.MorphFirstSeen}（已登记，引导内容在 FG15-UX-04；之后不重复）");

            Press(Key(GameActionId.ToggleCameraView));
            bool stillUplinkedDuringCamera = SignalPresence.CurrentMachineLogicId == a;
            WaitAtCore();
            Frames(1);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo leftInfo);
            float q0 = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            Expect(stillUplinkedDuringCamera && SignalPresence.AtCore && !leftInfo.Uplinked && leftInfo.Morph == MorphMask.None && MachineMorphView.TargetOf(a) == MorphMask.None && q0 < 1f && q0 > 0f,
                $"再按接入 / 退出键：镜头拉回战略期间信号仍在机器里（形变保持），信号离开时重编译回本地配置 → 目标 = 不变形，开始收起（进度 {q0:0.00}）");
            Frames(8);
            Expect(MachineMorphView.VisibleOf(a) == MorphMask.None && !GroupActive(a, MorphMask.Limiter) && MachineMorphView.AnimatingCount == 0
                   && !MachineMorphView.SignalBeamShownOf(a),
                "0.3 秒后完全复原：挂点组收起、信号光柱熄灭、没有机器还在过渡");

            // 负向：AI 自己装着限制器类固件（把拖尾临时注入为限制器）常驻形变态，但信号不在它身上——不亮信号光柱（光柱只表示“接入”，FGR-BASE-020）。
            FirmwareKinds.OverrideCategoryForTests(new Dictionary<string, FirmwareCategory> { { FirmwareCatalog.FwTrailId, FirmwareCategory.Limiter } });
            try
            {
                MachineLoadoutRegistry.NotifyChanged(g);
                Frames(8);
                Expect(MachineMorphView.VisibleOf(g) == MorphMask.Limiter && GroupActive(g, MorphMask.Limiter) && !MachineMorphView.SignalBeamShownOf(g),
                    "AI 机器自带限制器类固件：常驻形变态，但信号不在它身上，不亮信号光柱");
            }
            finally
            {
                FirmwareKinds.OverrideCategoryForTests(null);
                MachineLoadoutRegistry.NotifyChanged(g);
                Frames(8);
            }
        }

        private static void CheckOverlayAndSwitch()
        {
            Line("  · D2. 多类叠加：信号核带过载（限制器）+ 正式电磁类固件“回旋”，接进自带拖尾（流体）的机器 → 三类同时显示、各在自己的挂点组；Tab 切机时两台同时过渡");
            CampaignState s = NewHome(9102);
            // FG2-FW-01 起正式表里有电磁场控类固件（DEBT-FG1VFX01-02 关闭）：用正式的“回旋”（中立协议、遗迹人类遗产），不再注入类别。
            // 它不是开局蓝图库内容：按正常游戏记一条解锁（带回遗迹终端的人类遗产）后刻印。
            const string EmFirmware = "fw_boomerang";
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(EmFirmware).ToArray();
            Expect(FirmwareKinds.CategoryOf(EmFirmware) == FirmwareCategory.Electromagnetic && FirmwareKinds.MorphOf(EmFirmware) == "em"
                   && !FirmwareKinds.IsCore(EmFirmware) && !FirmwareKinds.IsRaw(s, EmFirmware),
                "正式表：回旋 = 电磁场控类、形变状态 = 线圈态，常规、中立协议（不裸跑）");
            try
            {
                int g = SpawnHome(BpGunMarkerUp, new Vector2(4f, -4f));
                int a = SpawnHome(BpCannonUp, new Vector2(8f, -4f));
                WorldSimulation.StepMany(2);
                CombatSite site = WorldSimulation.Home.Combat;
                EquipCore(s, FirmwareCatalog.FwOverloadId, EmFirmware);
                Expect(MachineMorphView.VisibleOf(g) == MorphMask.Fluid, "接入前：连射器 + 标记器 + 接入口，自带拖尾 → 喷口态");
                CommitVia(g);
                Frames(8);
                site.TryGetMachineWeapon(g, out MachineWeaponInfo info);
                MorphMask all = MorphMask.Limiter | MorphMask.Fluid | MorphMask.Electromagnetic;
                bool groupsOk = MachineMorph.Categories.All(c => GroupActive(g, c) && Mathf.Approximately(MachineMorphView.ProgressOf(g, c), 1f));
                Transform lim = MachineMorphView.GroupOf(g, MorphMask.Limiter);
                Transform em = MachineMorphView.GroupOf(g, MorphMask.Electromagnetic);
                bool bothComponents = lim != null && lim.Cast<Transform>().Any(t => t.name.StartsWith(ComponentCatalog.CompGunId)) && lim.Cast<Transform>().Any(t => t.name.StartsWith(ComponentCatalog.FuncMarkerId))
                                      && lim.Cast<Transform>().Any(t => t.name.StartsWith(MachineMorphLibrary.ChassisKey));
                Expect(info.Morph == all && MachineMorphView.VisibleOf(g) == all && groupsOk && lim != em && bothComponents,
                    "接入后：限制器 + 流体 + 电磁三类同时显示，每类一个独立挂点组；主组件（连射器）与功能组件（标记器）各有自己的部件，外加整机轮廓件");

                int starts = MachineMorphView.TransitionStarts;
                UplinkRequestResult r = SignalUplinkService.Request(a, UplinkSource.MachineList);
                int n = 0;
                while (SignalUplinkService.IsPending && n++ < 40)
                {
                    Frames(1);
                }
                bool bothAnimating = MachineMorphView.AnimatingCount == 2;
                float gFluid = MachineMorphView.ProgressOf(g, MorphMask.Fluid);
                float gLim = MachineMorphView.ProgressOf(g, MorphMask.Limiter);
                Expect(r.Accepted && SignalPresence.CurrentMachineLogicId == a && bothAnimating && MachineMorphView.TargetOf(g) == MorphMask.Fluid
                       && Mathf.Approximately(gFluid, 1f) && gLim < 1f && MachineMorphView.TargetOf(a) == (MorphMask.Limiter | MorphMask.Electromagnetic),
                    "从 #g 切到重炮机：#g 收起形变态与线圈态、保留自带的喷口态，同时重炮机展开形变态 + 线圈态（两台同时过渡）");
                Frames(8);
                Expect(MachineMorphView.VisibleOf(g) == MorphMask.Fluid && MachineMorphView.VisibleOf(a) == (MorphMask.Limiter | MorphMask.Electromagnetic) && MachineMorphView.AnimatingCount == 0,
                    "0.3 秒后两台都到位");
            }
            finally
            {
                FirmwareKinds.OverrideCategoryForTests(null);
            }
        }

        private static void CheckLoadoutChange()
        {
            Line("  · D3. 装配变更（装配站回厂换版本 = 重新登记）：AI 机器换掉拖尾 → 喷口态按过渡收起；换回来再展开");
            CampaignState s = NewHome(9103);
            int g = SpawnHome(BpGunTrail, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            Expect(MachineMorphView.VisibleOf(g) == MorphMask.Fluid, "初始：喷口态");
            CircuitOpResult r = MachineLoadoutRegistry.Register(s, g, BpGunPlain, 1);
            Frames(1);
            float mid = MachineMorphView.ProgressOf(g, MorphMask.Fluid);
            Frames(8);
            Expect(r.Success && mid > 0f && mid < 1f && MachineMorphView.VisibleOf(g) == MorphMask.None,
                $"换成没有固件的连射器蓝图：喷口态按过渡收起（中途进度 {mid:0.00}），最后不变形");
            MachineLoadoutRegistry.Register(s, g, BpGunTrail, 1);
            Frames(8);
            Expect(MachineMorphView.VisibleOf(g) == MorphMask.Fluid, "换回带拖尾的蓝图：喷口态重新展开");
            // 组件换了（连射器 → 重炮）：部件换成重炮的，不叠旧部件。
            int partsGun = MachineMorphView.PartCountOf(g);
            Transform limGun = MachineMorphView.GroupOf(g, MorphMask.Fluid);
            MachineLoadoutRegistry.Register(s, g, BpCannonUp, 1);
            Frames(8);
            Transform fluidGroup = MachineMorphView.GroupOf(g, MorphMask.Fluid);
            bool cannonParts = fluidGroup != null && fluidGroup.Cast<Transform>().Any(t => t.name.StartsWith(ComponentCatalog.CompCannonId))
                               && !fluidGroup.Cast<Transform>().Any(t => t.name.StartsWith(ComponentCatalog.CompGunId));
            int rigs = WorldSimulation.Home.Combat.TryGetMachineMarker(g, out HomeValleyMachineMarker mk) && mk.View != null
                ? mk.View.transform.Cast<Transform>().Count(t => t.name == MachineMorphView.RigName) : -1;
            Expect(cannonParts && rigs == 1 && MachineMorphView.VisibleOf(g) == MorphMask.None && partsGun > 0,
                "主组件换成重炮：形变部件整套换成重炮的（不残留连射器部件、只有一个挂点根），重炮蓝图没有自带固件 → 不变形");
        }

        // ── E. 负向 ─────────────────────────────────────────────────────────────────

        private static void CheckDeathMidMorph()
        {
            Line("  · E1. 形变过程中阵亡：当场收起（残骸不带形变），之后的重算、帧推进都不再展开，也不报错");
            CampaignState s = NewHome(9104);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpCannonUp, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            CommitVia(a);
            Frames(1);
            float mid = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            MachineRegistry.ApplyDamage(a, 99999f);
            bool immediate = MachineMorphView.VisibleOf(a) == MorphMask.None && !GroupActive(a, MorphMask.Limiter);
            int errors = 0;
            Application.LogCallback onLog = (msg, st, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    errors++;
                }
            };
            Application.logMessageReceived += onLog;
            try
            {
                Frames(20);
                WorldSimulation.StepMany(10);
                Frames(10);
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }
            Expect(mid > 0f && mid < 1f && immediate && MachineMorphView.VisibleOf(a) == MorphMask.None && errors == 0,
                $"展开到一半（{mid:0.00}）时阵亡：当场收起；之后 30 帧 + 10 步没有再展开、0 条报错");
            Expect(SignalPresence.CurrentMachineLogicId != a && (SignalPresence.CurrentMachineLogicId == 0 || MachineMorphView.TargetOf(SignalPresence.CurrentMachineLogicId) == MorphMask.Limiter),
                $"信号离开阵亡的机器（在 #{SignalPresence.CurrentMachineLogicId}）；若回弹到另一台，形变跟着到那一台");
        }

        private static void CheckRapidToggle()
        {
            Line("  · E2. 快速反复接入和离开：进度就地折返（不跳变、不重播），部件不重建不叠加，最终状态只由最后一次编译结果决定");
            CampaignState s = NewHome(9105);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            HomeValleyMachineMarker marker = null;
            WorldSimulation.Home.Combat.TryGetMachineMarker(a, out marker);
            // 真实的“离开”要等镜头拉回（0.35 秒）、再接入也有 0.35 秒过渡——都比 0.3 秒形变长，默认时长下来不及折返。
            // 把形变时长临时调成 1 秒（调参项 morph.transition_seconds 的合法取值），让“还没展开完就离开 / 还没收完又接入”真的发生。
            MachineMorphView.TransitionSecondsOverrideForTests = 1f;
            bool reversalsOk = true;
            var trace = new List<string>();
            Transform rig0 = null;
            int parts0 = -1;
            int children0 = -1;
            try
            {
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    WorldSimulation.Home.TrySelectMachine(a);
                    CommitVia(a);
                    float atCommit = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
                    if (rig0 == null)
                    {
                        rig0 = marker?.View != null ? marker.View.transform.Find(MachineMorphView.RigName) : null;
                        parts0 = MachineMorphView.PartCountOf(a);
                        children0 = marker?.View != null ? marker.View.transform.childCount : -1;
                    }
                    Frames(1);
                    float rising = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
                    LeaveByKey(); // 还没展开完就离开
                    float atLeave = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
                    Frames(1);
                    float falling = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
                    trace.Add($"接入时 {atCommit:0.00}→{rising:0.00}，离开时 {atLeave:0.00}→{falling:0.00}");
                    reversalsOk &= rising > atCommit && atLeave < 1f && falling < atLeave && falling > 0f && MachineMorphView.TargetOf(a) == MorphMask.None
                                   && (cycle == 0 || atCommit > 0f); // 第 2 轮起：上一轮还没收完就再接入，从剩下的进度往上走，不从 0 重播
                }
                WorldSimulation.Home.TrySelectMachine(a);
                CommitVia(a);
                Frames(25);
            }
            finally
            {
                MachineMorphView.TransitionSecondsOverrideForTests = null;
            }
            Transform rig1 = marker?.View != null ? marker.View.transform.Find(MachineMorphView.RigName) : null;
            Expect(reversalsOk, $"4 轮“没展开完就离开、没收完又接入”：每次都从当前进度就地折返，不跳到全开或全收、不从 0 重播（{string.Join("；", trace)}）");
            Expect(rig0 != null && rig0 == rig1 && MachineMorphView.PartCountOf(a) == parts0 && marker.View.transform.childCount == children0,
                $"部件不重建、不叠加：挂点根还是同一个，部件数 {parts0}，机器表现子节点数 {children0} 不变");
            Expect(SignalPresence.CurrentMachineLogicId == a && MachineMorphView.VisibleOf(a) == MorphMask.Limiter && Mathf.Approximately(MachineMorphView.ProgressOf(a, MorphMask.Limiter), 1f)
                   && MachineMorphView.AnimatingCount == 0,
                "最后一次是接入：稳定在形变态全展开");

            // 同一帧里连续多次重算（冷却起止、改信号核）：目标没变不重启过渡。
            int starts = MachineMorphView.TransitionStarts;
            for (int i = 0; i < 10; i++)
            {
                MachineLoadoutRegistry.NotifyChanged(a);
            }
            Expect(MachineMorphView.TransitionStarts == starts && MachineMorphView.AnimatingCount == 0 && Mathf.Approximately(MachineMorphView.ProgressOf(a, MorphMask.Limiter), 1f),
                "同一台机器连续 10 次重算、状态没变：不闪、不重播过渡（核心固件冷却中固件照样插着，形变不闪）");
        }

        private static void CheckDetachAndNoComponent()
        {
            Line("  · E3. 表现中途被卸载（镜头飞去别的地点）、没有作战组件的机器、引信类固件");
            CampaignState s = NewHome(9106);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int cityMachine = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-2f, -24f));
            WorldSimulation.StepMany(2);
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            OpenCity(s, cityMachine);
            WorldView.Observe(WorldSimulation.Home.SiteId);
            WorldSimulation.StepMany(2);
            CommitVia(a);
            Frames(1);
            bool midway = MachineMorphView.AnimatingCount == 1;
            int errors = 0;
            Application.LogCallback onLog = (msg, st, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    errors++;
                }
            };
            Application.logMessageReceived += onLog;
            try
            {
                WorldView.Observe(FracturedCityLayout.RegionId); // 家园表现对象销毁
                Frames(10);
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }
            Expect(midway && !MachineMorphView.IsRegistered(a) && MachineMorphView.AnimatingCount == 0 && errors == 0,
                "过渡途中镜头飞去破碎都市（家园表现销毁）：登记随之删除、不再推进，0 条报错");
            WorldView.Observe(WorldSimulation.Home.SiteId);
            Frames(1);
            CombatSite site = WorldSimulation.Home.Combat;
            site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
            Expect(MachineMorphView.IsRegistered(a) && MachineMorphView.VisibleOf(a) == info.Morph && MachineMorphView.AnimatingCount == 0,
                $"飞回家园：按当前编译结果直接到位（{MachineMorph.Describe(info.Morph)}），不重播过渡");

            // 没有作战组件的机器：带着流体固件也不建部件、不变形。
            BlueprintCircuitBoard bare = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, null, null, null, new[] { FirmwareCatalog.FwTrailId });
            AddBlueprint(s, "bp_selfcheck_vfx01_bare", bare);
            int c = SpawnHome("bp_selfcheck_vfx01_bare", new Vector2(12f, -4f));
            WorldSimulation.StepMany(2);
            Frames(1);
            Expect(MachineMorphView.IsRegistered(c) && MachineMorphView.PartCountOf(c) == 0 && MachineMorphView.VisibleOf(c) == MorphMask.None,
                "没有作战组件的机器（搬运 / 维修类）：不建形变部件、不变形");

            // 引信类固件（寻的 / 分裂）：只改弹体，不改机身。
            BlueprintCircuitBoard fuse = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null,
                new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwSplitId });
            AddBlueprint(s, "bp_selfcheck_vfx01_fuse", fuse);
            int f = SpawnHome("bp_selfcheck_vfx01_fuse", new Vector2(16f, -4f));
            WorldSimulation.StepMany(2);
            Frames(1);
            site.TryGetMachineWeapon(f, out MachineWeaponInfo fInfo);
            MachineCombatResolution fRes = MachineLoadoutRegistry.ResolveForPilot(s, f, s.RandomSeed);
            Expect(fRes.Success && fRes.Preview.FirmwareIds.Contains(FirmwareCatalog.FwHomingId) && fInfo.Morph == MorphMask.None && MachineMorphView.VisibleOf(f) == MorphMask.None,
                "连射器装寻的 + 分裂（引信类）：编译结果里固件生效，但机身不变形");

            // 类别漏登记（表里没有 / 拼错）：不变形，不崩。
            FirmwareKinds.OverrideCategoryForTests(new Dictionary<string, FirmwareCategory> { { FirmwareCatalog.FwTrailId, FirmwareCategory.Unknown } });
            try
            {
                Expect(MachineMorph.BitOf(FirmwareCatalog.FwTrailId) == MorphMask.None, "固件类别未知：不产生形变");
            }
            finally
            {
                FirmwareKinds.OverrideCategoryForTests(null);
            }
        }

        // ── F. 暂停与倍速 ───────────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · F. 暂停与 0.5x～3x：过渡按真实时间 0.3 秒（与倍速无关）；战略暂停中不走，恢复后接着走完");
            var frames = new List<string>();
            bool same = true;
            foreach (float speed in GameClock.Speeds)
            {
                CampaignState s = NewHome(9107);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                WorldSimulation.StepMany(2);
                EquipCore(s, FirmwareCatalog.FwOverloadId);
                GameClock.SetSpeed(speed);
                CommitVia(a);
                float first = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
                int n = 0;
                var seq = new List<string>();
                while (MachineMorphView.ProgressOf(a, MorphMask.Limiter) < 1f && n < 60)
                {
                    Frames(1);
                    n++;
                    if (n <= 8)
                    {
                        seq.Add(MachineMorphView.ProgressOf(a, MorphMask.Limiter).ToString("0.00"));
                    }
                }
                frames.Add($"{speed}x：提交时 {first:0.00}，再 {n} 帧到位（{string.Join(" ", seq)}）");
                same &= first > 0f && (n == 5 || n == 6); // 0.3 / 0.05 = 6 帧，提交那一帧已推进一格
                GameClock.SetSpeed(1f);
            }
            Expect(same, $"各档倍速下展开所需真实帧数相同（每帧 {FrameDt} 秒）：{string.Join("，", frames)}");

            CampaignState st = NewHome(9108);
            int b = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipCore(st, FirmwareCatalog.FwOverloadId);
            CommitVia(b);
            float before = MachineMorphView.ProgressOf(b, MorphMask.Limiter);
            GameClock.SetPaused(true);
            Frames(20);
            float paused = MachineMorphView.ProgressOf(b, MorphMask.Limiter);
            GameClock.SetPaused(false);
            Frames(8);
            Expect(GameClock.Speeds.Length == 4 && before > 0f && before < 1f && Mathf.Approximately(paused, before) && Mathf.Approximately(MachineMorphView.ProgressOf(b, MorphMask.Limiter), 1f),
                $"战略暂停 1 秒：进度停在 {paused:0.00} 不动；恢复后走完");
        }

        // ── G. 存读档 ──────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · G. 真实文件存读档：形变不是新状态，是编译结果的表现——读档后按存档里的接入与装配直接到位，不重播过渡");
            CampaignState s = NewHome(9109);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int g = SpawnHome(BpGunTrail, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            CommitVia(a);
            Frames(1); // 存档时 a 正展开到一半
            float midSave = MachineMorphView.ProgressOf(a, MorphMask.Limiter);
            SaveNow();
            CampaignState loaded = LoadLikeMenu();
            Frames(1);
            Expect(loaded != null && loaded.SignalCore.UplinkMachineLogicId == a && MachineMorphView.VisibleOf(a) == MorphMask.Limiter
                   && Mathf.Approximately(MachineMorphView.ProgressOf(a, MorphMask.Limiter), 1f) && MachineMorphView.VisibleOf(g) == MorphMask.Fluid && MachineMorphView.AnimatingCount == 0,
                $"存档时形变展开到 {midSave:0.00}：读档后信号仍在 #a、形变态直接到位；AI 机器的喷口态直接到位；没有机器在播过渡");
            string file = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).FirstOrDefault(p => !p.EndsWith(".tmp"));
            string text = file != null ? File.ReadAllText(file) : string.Empty;
            Expect(file != null && !text.Contains("Morph") && !text.Contains("morph"), "存档文件里没有任何形变字段（不新增存档状态，读档由编译结果重建）");
            LeaveByKey();
            Frames(8);
            Expect(SignalPresence.AtCore && MachineMorphView.VisibleOf(a) == MorphMask.None, "读档后离开：照常收起复原");
        }

        // ── H. 观察无关 ─────────────────────────────────────────────────────────────

        private static void CheckObservation()
        {
            Line("  · H. 观察无关（B24）：家园不被观察时，机器的形变结论（桥接层 Morph）照样跟着装配变；再观察时按它直接到位；形变表现不改武器参数");
            CampaignState s = NewHome(9110);
            int g = SpawnHome(BpGunTrail, new Vector2(4f, -4f));
            int cityMachine = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-2f, -24f));
            WorldSimulation.StepMany(2);
            OpenCity(s, cityMachine);
            CombatSite site = WorldSimulation.Home.Combat;
            site.TryGetMachineWeapon(g, out MachineWeaponInfo observedInfo);
            WorldView.Observe(FracturedCityLayout.RegionId);
            Frames(1);
            site.TryGetMachineWeapon(g, out MachineWeaponInfo unobservedInfo);
            Expect(!MachineMorphView.IsRegistered(g) && unobservedInfo.Morph == observedInfo.Morph && unobservedInfo.WeaponIndex == observedInfo.WeaponIndex,
                "家园不被观察：没有表现对象（不建部件），桥接层的形变结论与武器行与观察时相同");
            MachineLoadoutRegistry.Register(s, g, BpGunPlain, 1);
            WorldSimulation.StepMany(5);
            site.TryGetMachineWeapon(g, out MachineWeaponInfo changedInfo);
            Expect(changedInfo.Morph == MorphMask.None, "不被观察时换了装配（去掉拖尾）：形变结论跟着变成不变形");
            WorldView.Observe(WorldSimulation.Home.SiteId);
            Frames(1);
            Expect(MachineMorphView.VisibleOf(g) == MorphMask.None && MachineMorphView.AnimatingCount == 0, "镜头飞回家园：直接显示当前结论（不变形），不补播过渡");
            MachineLoadoutRegistry.Register(s, g, BpGunTrail, 1);
            Frames(8);
            site.TryGetMachineWeapon(g, out MachineWeaponInfo withView);
            int w0 = withView.WeaponIndex;
            MachineMorphView.OnViewDetached(g);
            MachineLoadoutRegistry.NotifyChanged(g);
            site.TryGetMachineWeapon(g, out MachineWeaponInfo withoutView);
            Expect(withView.Morph == MorphMask.Fluid && withoutView.Morph == withView.Morph && withoutView.WeaponIndex == w0,
                "有没有形变表现，桥接层给这台机器的武器行与形变结论都一样（表现层只读，不参与结算）");
        }

        // ── I. 性能 ────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · I. 性能：平时每帧 O(1)（没有过渡时直接返回）；过渡中 O(正在过渡的机器数) 且零分配；切换一次 O(1)；共享网格 / 材质份数与机器数无关");
            CampaignState s = NewHome(9111);
            const int count = 80;
            var ids = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                ids.Add(SpawnHome(i % 2 == 0 ? BpGunTrail : BpCannonUp, new Vector2(4f + (i % 10) * 2.5f, -4f - (i / 10) * 2.5f)));
            }
            WorldSimulation.StepMany(2);
            Frames(1);
            int meshes = MachineMorphLibrary.MeshCount;
            int mats = MachineMorphLibrary.MaterialCount;
            int registered = ids.Count(MachineMorphView.IsRegistered);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10000; i++)
            {
                MachineMorphView.FrameTick(FrameDt);
            }
            sw.Stop();
            double idleUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 10000;

            // 全部机器同时切换（最坏情况：把拖尾临时注入为电磁类后逐台重算）。
            FirmwareKinds.OverrideCategoryForTests(new Dictionary<string, FirmwareCategory> { { FirmwareCatalog.FwTrailId, FirmwareCategory.Electromagnetic } });
            double refreshMs;
            double animMs;
            long alloc;
            int animating;
            try
            {
                var sw2 = Stopwatch.StartNew();
                foreach (int id in ids)
                {
                    MachineLoadoutRegistry.NotifyChanged(id);
                }
                sw2.Stop();
                refreshMs = sw2.Elapsed.TotalMilliseconds / ids.Count;
                animating = MachineMorphView.AnimatingCount;
                MachineMorphView.TransitionSecondsOverrideForTests = 1000f; // 让过渡持续，量稳态每帧开销
                MachineMorphView.FrameTick(0.001f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw3 = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    MachineMorphView.FrameTick(0.001f);
                }
                sw3.Stop();
                alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                animMs = sw3.Elapsed.TotalMilliseconds / 200;
            }
            finally
            {
                MachineMorphView.TransitionSecondsOverrideForTests = null;
                FirmwareKinds.OverrideCategoryForTests(null);
            }
            Frames(10);
            Expect(registered == count && MachineMorphLibrary.MeshCount == meshes && MachineMorphLibrary.MaterialCount == mats && meshes <= 43 && mats <= 4,
                $"{count} 台机器：共享网格 {meshes} 份、材质 {mats} 份（与机器数无关；上限 43 / 4）");
            Expect(idleUs < 5.0, $"没有过渡时每帧 {idleUs:0.000} 微秒（直接返回）");
            Expect(animating == count / 2 && alloc == 0 && animMs < 2.0,
                $"{animating} 台同时过渡（最坏情况）：每帧 {animMs:0.000} 毫秒、分配 {alloc} 字节");
            Expect(refreshMs < 1.0, $"每台机器重算 + 切换目标 {refreshMs:0.000} 毫秒（含桥接层重编译；只在接入 / 离开 / 装配变更时发生）");
            PerfLines.Add($"形变：{count} 台机器，空闲每帧 {idleUs:0.000} μs；{animating} 台同时过渡每帧 {animMs:0.000} ms、零分配；单台重算 {refreshMs:0.000} ms；共享网格 {meshes} / 材质 {mats}" +
                          "（Editor batchmode、Mono JIT；渲染侧同一部件共用网格 + 材质、开 GPU Instancing，由引擎合批；批次数需在带图形设备的 Play 下复核，见证据截图工具）");
        }

        // ── 栅格化（俯视覆盖像素）────────────────────────────────────────────────────

        private static List<Vector3[]> CollectTriangles(Transform root, MachineMorphLibrary.PartRole? role = null)
        {
            var list = new List<Vector3[]>();
            if (root == null)
            {
                return list;
            }
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null)
                {
                    continue;
                }
                if (role.HasValue && !f.gameObject.name.Contains("." + role.Value))
                {
                    continue;
                }
                Matrix4x4 m = f.transform.localToWorldMatrix;
                Vector3[] v = f.sharedMesh.vertices;
                int[] t = f.sharedMesh.triangles;
                for (int i = 0; i < t.Length; i += 3)
                {
                    list.Add(new[] { m.MultiplyPoint3x4(v[t[i]]), m.MultiplyPoint3x4(v[t[i + 1]]), m.MultiplyPoint3x4(v[t[i + 2]]) });
                }
            }
            return list;
        }

        /// <summary>俯视正交投影到 1920×1080 画面（像素边长 = 2 × 正交半高 / 1080 米），返回被覆盖的像素（像素中心落在三角形内）。</summary>
        private static HashSet<int> Raster(List<Vector3[]> tris, float ortho)
        {
            float px = 2f * ortho / ScreenHeight;
            var set = new HashSet<int>();
            const int half = 200;
            foreach (Vector3[] t in tris)
            {
                Vector2 a = new Vector2(t[0].x, t[0].z) / px, b = new Vector2(t[1].x, t[1].z) / px, c = new Vector2(t[2].x, t[2].z) / px;
                float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (Mathf.Abs(area) < 1e-6f)
                {
                    continue;
                }
                int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                int y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), y1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = ((b.x - p.x) * (c.y - p.y) - (c.x - p.x) * (b.y - p.y)) / area;
                        float w1 = ((c.x - p.x) * (a.y - p.y) - (a.x - p.x) * (c.y - p.y)) / area;
                        float w2 = 1f - w0 - w1;
                        if (w0 >= 0f && w1 >= 0f && w2 >= 0f && Math.Abs(x) < half && Math.Abs(y) < half)
                        {
                            set.Add((x + half) * 1000 + (y + half));
                        }
                    }
                }
            }
            return set;
        }

        // ── 准备 ───────────────────────────────────────────────────────────────────

        /// <summary>着色器有没有实例化变体：优先问编辑器内部的 ShaderUtil.HasInstancing（反射）；取不到时看着色器源文件里有没有 multi_compile_instancing。</summary>
        private static bool ShaderSupportsInstancing(Shader shader)
        {
            if (shader == null)
            {
                return false;
            }
            var mi = typeof(ShaderUtil).GetMethod("HasInstancing", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (mi != null)
            {
                return (bool)mi.Invoke(null, new object[] { shader });
            }
            string path = AssetDatabase.GetAssetPath(shader);
            return !string.IsNullOrEmpty(path) && File.Exists(path) && File.ReadAllText(path).Contains("multi_compile_instancing");
        }

        /// <summary>按接入 / 退出键离开（镜头正在过渡时先等它落到直控，与玩家看到的一样）。</summary>
        private static void LeaveByKey()
        {
            int guard = 0;
            while (WorldView.Director.Mode != ViewMode.Direct && guard++ < 20)
            {
                Frames(1);
            }
            Press(Key(GameActionId.ToggleCameraView));
            WaitAtCore();
        }

        /// <summary>按接入 / 退出键离开：镜头先拉回战略（0.35 秒过渡），信号在过渡结束时回到归还核心。</summary>
        private static void WaitAtCore()
        {
            int n = 0;
            while (!SignalPresence.AtCore && n++ < 40)
            {
                Frames(1);
            }
            if (!SignalPresence.AtCore)
            {
                Fail($"测试准备：按键离开没有完成（信号在 #{SignalPresence.CurrentMachineLogicId}）");
            }
        }

        private static bool GroupActive(int logicId, MorphMask cat)
        {
            Transform g = MachineMorphView.GroupOf(logicId, cat);
            return g != null && g.gameObject.activeSelf;
        }

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgvfx01-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            foreach (string type in new[] { HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeAssemblyStation })
            {
                BuildingRecord r = s.BuildingRecords.FirstOrDefault(x => x.BuildingTypeId == type);
                if (r != null)
                {
                    r.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            AddBlueprints(s);
            return s;
        }

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            InputRouter.DebugSetReader(Keys);
            Keys.Down = KeyCode.None;
            UiEscapeStack.Clear();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            MachineMorphView.ResetForTests();
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            ExpectPrep(cannon.TrySetUplink(2), "重炮蓝图 2 号格标接入口");
            AddBlueprint(s, BpCannonUp, cannon);
            BlueprintCircuitBoard gunTrail = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { FirmwareCatalog.FwTrailId });
            if (!gunTrail.FirmwareSlots.Contains(FirmwareCatalog.FwTrailId))
            {
                Fail("测试准备：连射器蓝图没装上拖尾");
            }
            AddBlueprint(s, BpGunTrail, gunTrail);
            BlueprintCircuitBoard gunMarker = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null, new[] { FirmwareCatalog.FwTrailId });
            ExpectPrep(gunMarker.TrySetUplink(2), "连射器 + 标记器蓝图 2 号格标接入口");
            AddBlueprint(s, BpGunMarkerUp, gunMarker);
            AddBlueprint(s, BpGunPlain, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
        }

        private static void ExpectPrep(CircuitOpResult r, string what)
        {
            if (!r.Success)
            {
                Fail($"测试准备：{what}失败：{r.Message}");
            }
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static int SpawnHome(string bp, Vector2 at)
        {
            int id = SpawnRegion(HomeValleyLayout.RegionId, bp, HomeSpot(at));
            CircuitOpResult r = MachineLoadoutRegistry.Register(CampaignSession.Current, id, bp, 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记装配失败：{r.Message}");
            }
            return id;
        }

        private static Vector2 HomeSpot(Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 away = (core - dummy).sqrMagnitude > 1e-4f ? (core - dummy).normalized : Vector2.right;
            Vector2 p = core + offset;
            int guard = 0;
            while (Vector2.Distance(p, dummy) < HomeValleyCombatTargets.EngageRange + 4f && guard++ < 20)
            {
                p += away * 3f;
            }
            return p;
        }

        private static int SpawnRegion(string region, string bp, Vector2 at, float hp = 400f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static FracturedCityController OpenCity(CampaignState s, params int[] ids)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(ids, resume: false);
        }

        /// <summary>信号核按顺序装入这些固件（刻印芯片后装槽；远征锁关掉）。</summary>
        private static void EquipCore(CampaignState s, params string[] firmwareIds)
        {
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            try
            {
                for (int i = 0; i < firmwareIds.Length; i++)
                {
                    if (SignalCoreService.SlotContentId(s, i) == firmwareIds[i])
                    {
                        continue;
                    }
                    string chip = s.PrimitiveChips?.FirstOrDefault(c => c != null && c.CardDefId == firmwareIds[i] && c.State == PrimitiveChipState.Bag)?.PartId;
                    if (chip == null)
                    {
                        SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareIds[i]);
                        if (!pr.Success)
                        {
                            Fail($"测试准备：刻印 {firmwareIds[i]} 失败（{pr.Message}）");
                            continue;
                        }
                        chip = pr.CreatedId;
                    }
                    SignalCoreResult r = SignalCoreService.TryEquip(s, chip, i);
                    if (!r.Success)
                    {
                        Fail($"测试准备：{firmwareIds[i]} 装入 {i + 1} 号槽失败：{r.Message}");
                    }
                }
            }
            finally
            {
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            }
        }

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 40)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        private static void SaveNow()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("存档写入失败：" + r.Message);
            }
        }

        private static CampaignState LoadLikeMenu()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(Slot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            SignalUplinkService.RestoreAfterLoad();
            InputRouter.DebugSetReader(Keys); // 卸载世界会复位输入路由：把测试键盘接回去
            return r.State;
        }

        private static KeyCode Key(GameActionId action) => GameSettings.KeyBindings.GetKey(action);

        private static void Press(KeyCode key)
        {
            Keys.Down = key;
            Frames(1);
            Keys.Down = KeyCode.None;
        }

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt);
            }
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        // ── 报告 ──────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
