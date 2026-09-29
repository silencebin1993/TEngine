using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Stage;
using GameLogic.View;
using BinGames.Sim.Combat;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-VFX-02 形变全量的自动验收（FG02 FGR-FW-020～022、FGT-FW-004 扩展；卡片“必须同时交付：美术预算检查”、负向“多类叠加时的遮挡”）。
    /// 与首批（FG1-VFX-01）同一套真实世界入口：家园控制器 + 战斗桥接层 + 统一时钟 + 真输入路由 + 真实存档文件。
    /// </summary>
    public static partial class FgMachineMorphSelfCheck
    {
        /// <summary>正式表里的电磁场控类常规固件（中立协议、遗迹人类遗产；FG2-FW-01 迁入）。</summary>
        private const string EmFirmwareId = "fw_boomerang";

        /// <summary>美术规则 02 §2：作战组件 ≤ 1,000；玩家机器（底盘 + 作战组件 + 结构的最终显示总和）与炮塔 L 档 ≤ 4,000。</summary>
        private const int ComponentTriangleCap = 1000;
        private const int MachineTriangleCap = 4000;

        private static bool IsUtility(string comp) =>
            ComponentCatalog.TryGet(comp, out MechanicalContentDef def) && def.Category == MechanicalContentCategory.FunctionComponent;

        private static IEnumerable<string> MainComponents => MachineMorph.MorphComponents.Where(c => !IsUtility(c));

        private static IEnumerable<string> UtilityComponents => MachineMorph.MorphComponents.Where(IsUtility);

        /// <summary>这个挂点组里有没有这个组件自己的部件（节点名 = 组件键.件[placeholder]）。</summary>
        private static bool GroupHas(int logicId, MorphMask cat, string comp)
        {
            Transform g = MachineMorphView.GroupOf(logicId, cat);
            return g != null && g.Cast<Transform>().Any(t => t.name.StartsWith(comp + ".", StringComparison.Ordinal));
        }

        private static BlueprintCircuitBoard RosterBoard(string comp, bool uplink, params string[] firmware)
        {
            bool util = IsUtility(comp);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, util ? ComponentCatalog.CompGunId : comp, util ? comp : null, null,
                firmware ?? Array.Empty<string>());
            if (uplink)
            {
                ExpectPrep(board.TrySetUplink(2), $"{comp} 蓝图 2 号格标接入口");
            }
            foreach (string fw in firmware ?? Array.Empty<string>())
            {
                if (!board.FirmwareSlots.Contains(fw))
                {
                    Fail($"测试准备：{comp} 蓝图没装上 {fw}");
                }
            }
            return board;
        }

        // ── J. FGT-FW-004 扩展：名表 16 个作战组件逐个真实接入 / 离开 ─────────────────────────

        private static void CheckFullRoster()
        {
            Line("  · J. FG2-VFX-02 形变全量（FGT-FW-004 扩展）：名表 16 个作战组件各装一台真实家园机器（功能组件配连射器）：AI 自带拖尾 → 喷口态常驻；" +
                 "机器列表接入（信号核：过载 + 回旋）→ 三类叠加、每类挂点组都有这个组件自己的部件；切到下一台 → 上一台回到 AI 常驻；最后按键离开 → 全部复原");
            CampaignState s = NewHome(9121);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(EmFirmwareId).Distinct().ToArray();
            EquipCore(s, FirmwareCatalog.FwOverloadId, EmFirmwareId);
            var machines = new List<(string Comp, int Id)>();
            int idx = 0;
            foreach (string comp in MachineMorph.MorphComponents)
            {
                string bp = "bp_selfcheck_vfx02_" + comp;
                AddBlueprint(s, bp, RosterBoard(comp, true, FirmwareCatalog.FwTrailId));
                machines.Add((comp, SpawnHome(bp, new Vector2(4f + 4f * (idx % 4), -4f - 4f * (idx / 4)))));
                idx++;
            }
            WorldSimulation.StepMany(2);
            Frames(2);
            CombatSite site = WorldSimulation.Home.Combat;

            var aiWrong = machines.Where(m => MachineMorphView.VisibleOf(m.Id) != MorphMask.Fluid || !GroupHas(m.Id, MorphMask.Fluid, m.Comp)).Select(m => m.Comp).ToList();
            Expect(aiWrong.Count == 0, $"接入前：16 台 AI 驾驶的机器都自带拖尾（流体）→ 喷口态常驻，挂点组里是各自组件的部件{(aiWrong.Count > 0 ? "；不符：" + string.Join("、", aiWrong) : "")}");

            var wrong = new List<string>();
            var noBeam = new List<string>();
            int prev = 0;
            var prevWrong = new List<string>();
            foreach ((string comp, int id) in machines)
            {
                CommitVia(id);
                Frames(8);
                site.TryGetMachineWeapon(id, out MachineWeaponInfo info);
                bool ok = info.Uplinked && info.Morph == MorphMask.All && MachineMorphView.VisibleOf(id) == MorphMask.All
                          && MachineMorph.Categories.All(c => GroupActive(id, c) && Mathf.Approximately(MachineMorphView.ProgressOf(id, c), 1f) && GroupHas(id, c, comp)
                                                              && GroupHas(id, c, MachineMorphLibrary.ChassisKey));
                if (!ok)
                {
                    wrong.Add($"{comp}（桥接 {MachineMorph.Describe(info.Morph)} / 画面 {MachineMorph.Describe(MachineMorphView.VisibleOf(id))}）");
                }
                if (!MachineMorphView.SignalBeamShownOf(id))
                {
                    noBeam.Add(comp);
                }
                if (prev != 0 && (MachineMorphView.VisibleOf(prev) != MorphMask.Fluid || MachineMorphView.SignalBeamShownOf(prev)))
                {
                    prevWrong.Add(machines.First(m => m.Id == prev).Comp);
                }
                prev = id;
            }
            Expect(wrong.Count == 0, $"16 个组件逐个接入：限制器 + 流体 + 电磁三类同时展开到位，每类挂点组里都有这个组件自己的部件与整机轮廓件{(wrong.Count > 0 ? "；不符：" + string.Join("、", wrong) : "")}");
            Expect(noBeam.Count == 0 && prevWrong.Count == 0,
                $"接入的那一台亮信号光柱；切到下一台后上一台收起信号带来的形变态 / 线圈态、熄灭光柱，只剩 AI 自带的喷口态{(noBeam.Count + prevWrong.Count > 0 ? $"；无光柱：{string.Join("、", noBeam)}；上一台没复原：{string.Join("、", prevWrong)}" : "")}");
            LeaveByKey();
            Frames(8);
            var notRestored = machines.Where(m => MachineMorphView.VisibleOf(m.Id) != MorphMask.Fluid || MachineMorphView.SignalBeamShownOf(m.Id)).Select(m => m.Comp).ToList();
            Expect(SignalPresence.AtCore && notRestored.Count == 0 && MachineMorphView.AnimatingCount == 0,
                $"按接入键离开：信号回核心，16 台全部回到接入前的状态（喷口态常驻、无光柱、没有机器还在过渡）{(notRestored.Count > 0 ? "；不符：" + string.Join("、", notRestored) : "")}");

            // 形变只读编译结果：换成不带固件的同组件蓝图 → 收起；阵亡中的新组件机器当场收起。
            (string c0, int m0) = machines.First(m => m.Comp == ComponentCatalog.CompOrbitId);
            string plain = "bp_selfcheck_vfx02_plain_" + c0;
            AddBlueprint(s, plain, RosterBoard(c0, false));
            MachineLoadoutRegistry.Register(s, m0, plain, 1);
            Frames(1);
            float mid = MachineMorphView.ProgressOf(m0, MorphMask.Fluid);
            Frames(8);
            Expect(mid > 0f && mid < 1f && MachineMorphView.VisibleOf(m0) == MorphMask.None && MachineMorphView.PartCountOf(m0) > 0,
                $"旋刃环机器换成不带固件的蓝图：喷口态按 0.3 秒过渡收起（中途 {mid:0.00}），部件保留、不变形");
            (string c1, int m1) = machines.First(m => m.Comp == ComponentCatalog.FuncSpikesId);
            CommitVia(m1);
            Frames(1);
            float mid1 = MachineMorphView.ProgressOf(m1, MorphMask.Limiter);
            MachineRegistry.ApplyDamage(m1, 99999f);
            Expect(mid1 > 0f && mid1 < 1f && MachineMorphView.VisibleOf(m1) == MorphMask.None && !GroupActive(m1, MorphMask.Fluid),
                $"尖刺外装机器形变展开到一半（{mid1:0.00}）时阵亡：三类当场收起，残骸不带形变");
            Frames(4);
        }

        // ── K. 负向：多类叠加时的遮挡 ────────────────────────────────────────────────────

        private static void CheckOcclusion()
        {
            Line("  · K. 负向“多类叠加时的遮挡”：13 个主组件 ×（无 / 3 个功能组件）= 52 种装配三类全开，按高度做俯视深度栅格化（默认正交 30 / 最远 46）：" +
                 "每类都保留看得见的独占像素；重叠时按优先级抬高挂点组（形变态 > 线圈态 > 喷口态）");
            Expect(MachineMorph.OverlayPriority(MorphMask.Limiter) > MachineMorph.OverlayPriority(MorphMask.Electromagnetic)
                   && MachineMorph.OverlayPriority(MorphMask.Electromagnetic) > MachineMorph.OverlayPriority(MorphMask.Fluid)
                   && MachineMorph.OverlayLiftPerLevel >= 0.02f,
                "优先级规则：形变态（带信号光柱）> 线圈态（细环）> 喷口态（向后拉长的一大块）；每级抬高 ≥ 2 厘米");
            var host = new GameObject("MorphOcclusionHost");
            try
            {
                Renderer hostRenderer = host.AddComponent<MeshRenderer>();
                host.AddComponent<MeshFilter>();
                hostRenderer.sharedMaterial = ViewMaterials.Standard(Color.gray);
                PlaceholderSilhouette.ApplyMachine(host, hostRenderer, HomeValleyLayout.Erc003ChassisId);
                List<Vector3[]> baseTris = CollectTriangles(host.transform.Find(PlaceholderSilhouette.PartsName));
                var utils = new List<string> { null };
                utils.AddRange(UtilityComponents);
                int combos = 0;
                var failures = new List<string>();
                var worst = new Dictionary<MorphMask, (int Px, string Where)>
                {
                    { MorphMask.Limiter, (int.MaxValue, "") }, { MorphMask.Fluid, (int.MaxValue, "") }, { MorphMask.Electromagnetic, (int.MaxValue, "") },
                };
                bool liftsOk = true;
                bool tieBreakOk = true;
                int totalTies = 0;
                foreach (string main in MainComponents)
                {
                    foreach (string util in utils)
                    {
                        combos++;
                        Transform rig = MachineMorphView.BuildRig(host.transform, main, util, out _);
                        var layers = new List<(List<Vector3[]> Tris, int Owner)> { (baseTris, -1) };
                        foreach (MorphMask cat in MachineMorph.Categories)
                        {
                            Transform group = rig.GetChild(MachineMorph.IndexOf(cat));
                            group.gameObject.SetActive(true);
                            group.localScale = Vector3.one;
                            liftsOk &= Mathf.Approximately(group.localPosition.y, MachineMorph.OverlayPriority(cat) * MachineMorph.OverlayLiftPerLevel);
                            layers.Add((CollectTriangles(group), MachineMorph.IndexOf(cat)));
                        }
                        foreach (float ortho in new[] { OrthoHome, OrthoFar })
                        {
                            int[] visible = DepthRasterOwners(layers, ortho, out int[] coverage, out int ties, out int tiesWonByHigher);
                            tieBreakOk &= ties == tiesWonByHigher;
                            totalTies += ties;
                            foreach (MorphMask cat in MachineMorph.Categories)
                            {
                                int i = MachineMorph.IndexOf(cat);
                                int need = Math.Max(ortho >= OrthoFar ? 12 : 30, (int)(coverage[i] * 0.3f));
                                if (visible[i] < need)
                                {
                                    failures.Add($"{main}+{util ?? "无"}/{MachineMorph.Describe(cat)} 正交 {ortho}：可见 {visible[i]}px（覆盖 {coverage[i]}px，要 ≥ {need}）");
                                }
                                if (ortho >= OrthoFar && visible[i] < worst[cat].Px)
                                {
                                    worst[cat] = (visible[i], $"{main}+{util ?? "无"}");
                                }
                            }
                        }
                        Object.DestroyImmediate(rig.gameObject);
                    }
                }
                Expect(combos == 52 && failures.Count == 0,
                    $"{combos} 种装配三类全开：每类在两档缩放下都保留 ≥ 30 / 12px 且 ≥ 自身覆盖 30% 的可见像素（没有哪一类被整个盖掉）{(failures.Count > 0 ? "；不符：" + string.Join("；", failures.Take(8)) : "")}");
                Line("    · 正交 46 时最少的可见像素：" + string.Join("；", worst.Select(kv => $"{MachineMorph.Describe(kv.Key)} {kv.Value.Px}px（{kv.Value.Where}）")));
                Expect(liftsOk && tieBreakOk, $"挂点组按优先级抬高；原始高度相同的重叠像素（52 种装配 × 两档缩放共 {totalTies} 个）一律由优先级高的一类显示");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// 俯视深度栅格化：每个像素取最高（y 最大）的三角形，记它属于哪一层（-1 = 机身剪影，0/1/2 = 三类状态）。
        /// 返回每类最终看得见的像素数；<paramref name="coverage"/> = 每类自己覆盖的像素数（不考虑遮挡）。
        /// <paramref name="ties"/> = 两类状态在抬高前高度相同（差 &lt; 1 毫米）的重叠像素数，<paramref name="tiesWonByHigher"/> = 其中由优先级高的一类显示的数。
        /// </summary>
        private static int[] DepthRasterOwners(List<(List<Vector3[]> Tris, int Owner)> layers, float ortho, out int[] coverage, out int ties, out int tiesWonByHigher)
        {
            float px = 2f * ortho / ScreenHeight;
            const int half = 200;
            var top = new Dictionary<int, (float Y, float RawY, int Owner)>();
            var covered = new HashSet<int>[3] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
            ties = 0;
            tiesWonByHigher = 0;
            foreach ((List<Vector3[]> tris, int owner) in layers)
            {
                float lift = owner >= 0 ? MachineMorph.OverlayPriority(MachineMorph.Categories[owner]) * MachineMorph.OverlayLiftPerLevel : 0f;
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
                            if (w0 < 0f || w1 < 0f || w2 < 0f || Math.Abs(x) >= half || Math.Abs(y) >= half)
                            {
                                continue;
                            }
                            int key = (x + half) * 1000 + (y + half);
                            float h = w0 * t[0].y + w1 * t[1].y + w2 * t[2].y; // 世界 y（已含挂点组抬高）
                            float raw = h - lift;
                            if (owner >= 0)
                            {
                                covered[owner].Add(key);
                            }
                            if (!top.TryGetValue(key, out var cur) || h > cur.Y)
                            {
                                if (cur.Owner >= 0 && owner >= 0 && cur.Owner != owner && Mathf.Abs(raw - cur.RawY) < 1e-3f && top.ContainsKey(key))
                                {
                                    ties++;
                                    tiesWonByHigher += MachineMorph.OverlayPriority(MachineMorph.Categories[owner]) > MachineMorph.OverlayPriority(MachineMorph.Categories[cur.Owner]) ? 1 : 0;
                                }
                                top[key] = (h, raw, owner);
                            }
                            else if (cur.Owner >= 0 && owner >= 0 && cur.Owner != owner && Mathf.Abs(raw - cur.RawY) < 1e-3f)
                            {
                                ties++;
                                tiesWonByHigher += MachineMorph.OverlayPriority(MachineMorph.Categories[cur.Owner]) > MachineMorph.OverlayPriority(MachineMorph.Categories[owner]) ? 1 : 0;
                            }
                        }
                    }
                }
            }
            var visible = new int[3];
            foreach (var v in top.Values)
            {
                if (v.Owner >= 0)
                {
                    visible[v.Owner]++;
                }
            }
            coverage = new[] { covered[0].Count, covered[1].Count, covered[2].Count };
            return visible;
        }

        // ── L. 美术预算（美术规则 02 §2）──────────────────────────────────────────────

        private static void CheckArtBudget()
        {
            Line("  · L. 美术预算（美术规则 02 §2 / §3）：每个作战组件每套状态 ≤ 1,000 三角；整机（剪影 + 三类状态的整机轮廓件 + 主组件 + 功能组件全开）≤ 4,000（L 档）；" +
                 "炮塔（固定底盘能装的组件）同 L 档；零骨骼；一件一网格");
            MachineMorphView.ResetForTests();
            var over = new List<string>();
            int maxComp = 0;
            string maxCompAt = "";
            foreach (string comp in MachineMorph.MorphComponents)
            {
                foreach (MorphMask cat in MachineMorph.Categories)
                {
                    int n = PartTriangles(comp, cat);
                    if (n > maxComp)
                    {
                        maxComp = n;
                        maxCompAt = $"{comp}/{MachineMorph.Describe(cat)}";
                    }
                    if (n <= 0 || n > ComponentTriangleCap)
                    {
                        over.Add($"{comp}/{MachineMorph.Describe(cat)}={n}");
                    }
                }
            }
            Expect(over.Count == 0, $"16 个作战组件 × 3 套状态的组件件都有几何且 ≤ {ComponentTriangleCap} 三角（最多 {maxComp}，{maxCompAt}）{(over.Count > 0 ? "；超标：" + string.Join("、", over) : "")}");

            var host = new GameObject("MorphBudgetHost");
            try
            {
                Renderer hostRenderer = host.AddComponent<MeshRenderer>();
                host.AddComponent<MeshFilter>();
                hostRenderer.sharedMaterial = ViewMaterials.Standard(Color.gray);
                PlaceholderSilhouette.ApplyMachine(host, hostRenderer, HomeValleyLayout.Erc003ChassisId);
                int silhouette = CollectTriangles(host.transform.Find(PlaceholderSilhouette.PartsName)).Count;
                var utils = new List<string> { null };
                utils.AddRange(UtilityComponents);
                int maxMachine = 0;
                string maxMachineAt = "";
                int maxTurret = 0;
                string maxTurretAt = "";
                var overMachine = new List<string>();
                bool bonesZero = true;
                bool oneFilter = true;
                foreach (string main in MainComponents)
                {
                    foreach (string util in utils)
                    {
                        Transform rig = MachineMorphView.BuildRig(host.transform, main, util, out _);
                        int morphTris = CollectTriangles(rig).Count;
                        bonesZero &= rig.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0 && rig.GetComponentsInChildren<Animator>(true).Length == 0;
                        oneFilter &= rig.GetComponentsInChildren<MeshFilter>(true).All(f => f.GetComponents<MeshFilter>().Length == 1);
                        int machine = silhouette + morphTris;
                        if (machine > maxMachine)
                        {
                            maxMachine = machine;
                            maxMachineAt = $"{main}+{util ?? "无"}";
                        }
                        if (machine > MachineTriangleCap)
                        {
                            overMachine.Add($"{main}+{util ?? "无"}={machine}");
                        }
                        // 炮塔：固定底盘能装的组件组合（炮塔本体模型随 FG6-DEF-01，按同一 L 档预留剪影同量）
                        bool turretOk = CarrierReadings.CanMount(CarrierReadings.FixedChassisId, main, out _, out _)
                                        && (util == null || CarrierReadings.CanMount(CarrierReadings.FixedChassisId, util, out _, out _));
                        if (turretOk && machine > maxTurret)
                        {
                            maxTurret = machine;
                            maxTurretAt = $"{main}+{util ?? "无"}";
                        }
                        Object.DestroyImmediate(rig.gameObject);
                    }
                }
                Expect(overMachine.Count == 0 && maxMachine <= MachineTriangleCap,
                    $"整机三类全开的最终显示总和 ≤ {MachineTriangleCap}：剪影 {silhouette} + 形变部件，最多 {maxMachine}（{maxMachineAt}）{(overMachine.Count > 0 ? "；超标：" + string.Join("、", overMachine) : "")}");
                Expect(maxTurret > 0 && maxTurret <= MachineTriangleCap,
                    $"炮塔能装的组件组合（冲刺器除外）三类全开 ≤ {MachineTriangleCap}：最多 {maxTurret}（{maxTurretAt}；炮塔本体按剪影同量预留）");
                Expect(bonesZero && oneFilter, "形变部件零骨骼（没有蒙皮网格 / 动画机）、每个部件一个 MeshFilter（整件换网格变体，美术规则 02 §3）");
                PerfLines.Add($"美术预算：组件件最多 {maxComp} 三角（{maxCompAt}）；整机三类全开最多 {maxMachine} 三角（{maxMachineAt}）；炮塔组合最多 {maxTurret}");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
            MachineMorphLibrary.ReleaseAll();
        }

        /// <summary>组件自己的部件（实体 + 发光，不含整机轮廓件）在一套状态下的三角数。</summary>
        private static int PartTriangles(string comp, MorphMask cat)
        {
            int n = 0;
            foreach (MachineMorphLibrary.PartRole role in new[] { MachineMorphLibrary.PartRole.Solid, MachineMorphLibrary.PartRole.Glow, MachineMorphLibrary.PartRole.Signal })
            {
                Mesh m = MachineMorphLibrary.MeshFor(comp, cat, role);
                n += m != null ? m.triangles.Length / 3 : 0;
            }
            return n;
        }

        // ── M. 新组件装配的存读档与观察无关 ─────────────────────────────────────────────

        private static void CheckRosterSaveAndObservation()
        {
            Line("  · M. 新组件装配（哨戒桩 + 尖刺外装、震荡脉冲器）的真实文件存读档：装配与接入按存档恢复、形变直接到位、内核武器读法（定点 / 反伤 / 落点）不丢；不被观察时换装配，形变结论照样跟着变");
            CampaignState s = NewHome(9122);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(EmFirmwareId).Distinct().ToArray();
            BlueprintCircuitBoard sentry = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompSentryId, ComponentCatalog.FuncSpikesId, null,
                new[] { FirmwareCatalog.FwTrailId });
            ExpectPrep(sentry.TrySetUplink(2), "哨戒桩 + 尖刺外装蓝图 2 号格标接入口");
            AddBlueprint(s, "bp_selfcheck_vfx02_sentry_spikes", sentry);
            AddBlueprint(s, "bp_selfcheck_vfx02_pulser", RosterBoard(ComponentCatalog.CompPulserId, false, EmFirmwareId));
            int a = SpawnHome("bp_selfcheck_vfx02_sentry_spikes", new Vector2(4f, -4f));
            int b = SpawnHome("bp_selfcheck_vfx02_pulser", new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            CommitVia(a);
            Frames(8);
            CombatSite site = WorldSimulation.Home.Combat;
            site.TryGetMachineWeapon(a, out MachineWeaponInfo before);
            site.Kernel.TryGetWeapon(before.WeaponIndex, out CombatWeapon wBefore);
            Expect(before.Morph == (MorphMask.Limiter | MorphMask.Fluid) && wBefore.Reading.DroneAnchored == 1 && wBefore.Reading.Thorns > 0f
                   && MachineMorphView.VisibleOf(b) == MorphMask.Electromagnetic,
                "存档前：哨戒桩 + 尖刺外装接入（形变态 + 喷口态），内核武器是定点无人机 + 反伤；震荡脉冲器 AI 自带回旋 → 线圈态常驻");
            SaveNow();
            CampaignState loaded = LoadLikeMenu();
            Frames(1);
            site = WorldSimulation.Home.Combat;
            site.TryGetMachineWeapon(a, out MachineWeaponInfo after);
            site.Kernel.TryGetWeapon(after.WeaponIndex, out CombatWeapon wAfter);
            site.TryGetMachineWeapon(b, out MachineWeaponInfo bAfter);
            site.Kernel.TryGetWeapon(bAfter.WeaponIndex, out CombatWeapon wb);
            MachineRecord recA = null;
            bool rec = MachineRegistry.TryGetRecord(a, out recA) && recA != null;
            Expect(loaded != null && rec && loaded.SignalCore.UplinkMachineLogicId == a && after.Morph == before.Morph
                   && MachineMorphView.VisibleOf(a) == before.Morph && Mathf.Approximately(MachineMorphView.ProgressOf(a, MorphMask.Limiter), 1f)
                   && GroupHas(a, MorphMask.Limiter, ComponentCatalog.CompSentryId) && GroupHas(a, MorphMask.Limiter, ComponentCatalog.FuncSpikesId)
                   && wAfter.Reading.DroneAnchored == 1 && Mathf.Approximately(wAfter.Reading.Thorns, wBefore.Reading.Thorns)
                   && wb.Reading.FieldPlacement == CombatZonePlacement.Attacker && MachineMorphView.VisibleOf(b) == MorphMask.Electromagnetic
                   && MachineMorphView.AnimatingCount == 0,
                "读档后：信号仍在哨戒桩机器、形变直接到位（两个组件的部件都在）、内核武器仍是定点 + 反伤；震荡脉冲器仍落在自己脚下、线圈态直接到位");
            // 暂停与 0.5x～3x（世界统一时钟）：读档后的哨戒桩在家园内核里——暂停帧不推进、桩不到期；3x / 0.5x 每真实秒推进 3 / 0.5 游戏秒。
            double2 pa = double2.zero;
            bool unit = site.TryGetMachineUnit(a, out int ua) && site.Kernel.TryGetPosition(ua, out pa);
            int foe = site.Kernel.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = pa + new double2(4, 0), Home = pa + new double2(4, 0),
                Radius = 0.5f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            CombatFireResult fr = site.Kernel.FireAt(ua, foe, site.Kernel.Time);
            int posts = site.Kernel.DronesOf(ua);
            // 接入（直控）时整个世界锁 1x（FG0 统一时钟的既有规则）：先离开再测倍速。
            LeaveByKey();
            Frames(8);
            GameClock.SetPaused(true);
            double t0 = site.Kernel.Time;
            Frames(20);
            bool pausedOk = site.Kernel.Time == t0 && site.Kernel.DronesOf(ua) == posts;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(3f);
            double t1 = site.Kernel.Time;
            Frames(20); // 1 真实秒
            double fast = site.Kernel.Time - t1;
            GameClock.SetSpeed(0.5f);
            double t2 = site.Kernel.Time;
            Frames(20);
            double slow = site.Kernel.Time - t2;
            GameClock.SetSpeed(1f);
            int postsAfter = site.Kernel.DronesOf(ua);
            site.Kernel.Despawn(foe);
            Expect(unit && fr == CombatFireResult.Ok && posts == 2 && pausedOk && Math.Abs(fast - 3.0) < 0.1 && Math.Abs(slow - 0.5) < 0.1
                   && postsAfter == 2,
                $"家园内核里的哨戒桩（{posts} 根）：离开后暂停 20 帧游戏时间不走、桩不到期；3x 一真实秒推进 {fast:0.00}、0.5x 推进 {slow:0.00}；桩仍按游戏时间存活");

            // 观察无关：镜头去破碎都市，家园没有表现对象；换装配（去掉回旋）→ 桥接层形变结论跟着变；飞回来直接到位。
            int cityMachine = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-2f, -24f));
            WorldSimulation.StepMany(2);
            OpenCity(loaded ?? s, cityMachine);
            WorldView.Observe(FracturedCityLayout.RegionId);
            Frames(1);
            AddBlueprint(CampaignSession.Current, "bp_selfcheck_vfx02_pulser_plain", RosterBoard(ComponentCatalog.CompPulserId, false));
            MachineLoadoutRegistry.Register(CampaignSession.Current, b, "bp_selfcheck_vfx02_pulser_plain", 1);
            WorldSimulation.StepMany(3);
            site.TryGetMachineWeapon(b, out MachineWeaponInfo unobserved);
            bool noView = !MachineMorphView.IsRegistered(b);
            WorldView.Observe(WorldSimulation.Home.SiteId);
            Frames(1);
            Expect(noView && unobserved.Morph == MorphMask.None && MachineMorphView.VisibleOf(b) == MorphMask.None && MachineMorphView.AnimatingCount == 0,
                "不被观察时（没有表现对象）换掉回旋：桥接层形变结论变成不变形；镜头飞回家园直接显示，不补播过渡（B24）");
        }
    }
}
