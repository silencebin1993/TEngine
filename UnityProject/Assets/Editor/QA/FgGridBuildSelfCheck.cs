using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using BinGames.EditorTools;
using Luban;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-04 格网建造原型与锚点迁移的自动验收（FGR-ARC-001；FGR-LOG-001、003、012、013；FGT-LOG-001 原型版本）。
    ///
    /// 全部断言走真实的生产入口（<see cref="HomeGridService"/>、<see cref="HomeValleyWorkOrders"/>、<see cref="HomeValleyBuildMode"/>、
    /// 家园控制器的开局播种、<see cref="CampaignSaveService"/> 真读写文件），行为坏了会失败：
    /// A 数据：五张表与源数据 fgdata_grid.py 逐行一致；check_luban 的 R12～R16 规则自测真跑。
    /// B 几何：旋转、占地、中心、端口朝向随旋转（负向“旋转后端口方向”）、负坐标区块寻址。
    /// C 锚点迁移：开局 7 座建筑来自开局布局表，ID / 位置与 Demo 锚点逐项一致；12 个种子下开局布局与建造位都合法（B25）；
    ///   地形按（种子, 格子）确定性生成，与访问顺序无关。
    /// D 放置与负向矩阵：合法放置 → 规划记录 + 占格 + 工单 + 预留废料；重叠 / 悬崖 / 迷雾 / 核心通道 / 污染 / 传送带 / 管线 /
    ///   未解锁 / 数量上限 / 不可放置 / 废料不足 / 需要特定地形（矿脉、水源），每一种都给出正确原因且不改动状态；水源可建（只有悬崖不可建）。
    /// E 旋转与拆除：旋转移动占格与端口、被挡时拒绝；拆除标记可撤回、规划取消全额退款、完工后拆除释放占格、返还地面物 ID 每单唯一；
    ///   无法重建的关键建筑不能拆（两条入口都拦），无法重建的普通建筑拆除需确认。
    /// F 正式输入：经 InputRouter 的动作（B / X / R / 左右键）走建造模式；Esc 栈；建造上下文。
    /// G 施工：暂停下可规划不推进；0.5x / 1x / 2x 下完工所需游戏时间相同。
    /// H 存读档：格网字段与格网域真文件往返、占用重建一致、实例序号续编；旧 v2 存档（无格网字段）迁移。
    /// I 后台一致性：同一组规划操作经建造模式（观察）与直接调服务（不观察）结果逐字段一致（FGR-BASE-021）。
    /// J 叠加层与界面：地形颜色 + 图案、迷雾变暗、核心通道；HUD 绑定真 UXML，中英文布局探针。
    /// K 性能：放置校验与建筑数无关；占用重建、区块生成耗时（Editor batchmode 数字）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgGridBuildSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;

        private static readonly int[] Seeds = { 1, 7, 42, 99, 123, 2024, 31337, 65535, 777777, 1000003, -5, -123456 };

        [MenuItem("BinGames/自检：FG 格网建造")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[格网建造] 格网建造原型与锚点迁移（FG0-ARCH-04）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fggrid-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;

                CheckData();
                CheckGeometry();
                CheckStartLayout();
                CheckPlacementMatrix();
                CheckRotateAndDemolish();
                CheckFormalInput();
                CheckConstructionTiming();
                CheckSaveLoad();
                CheckObservedEqualsUnobserved();
                CheckOverlayAndHud();
                CheckPerformance();
            }
            catch (Exception e)
            {
                Fail($"格网建造自检抛异常：{e}");
            }
            finally
            {
                GridContent.ResetForTests();
                HomeGridService.Invalidate();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                GameSettings.SetLanguage(originalLanguage);
                StrategyClock.Reset();
                InputRouter.DebugSetReader(null);
                InputRouter.SetBuildMode(false);
                InputRouter.SetGameplayPaused(false);
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            return _fail;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>经家园控制器的真实播种入口（私有静态 EnsureRegionSeeded）建一个新战役的家园，并登记一台空闲搬运机。</summary>
        private static CampaignState NewHome(int seed, bool withHauler = true)
        {
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fggrid-" + seed, "Standard", seed);
            MethodInfo seedMethod = typeof(HomeValleyController).GetMethod("EnsureRegionSeeded", BindingFlags.NonPublic | BindingFlags.Static);
            seedMethod.Invoke(null, new object[] { s });
            s.Scrap = 500;
            if (withHauler)
            {
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                    new Vector2(-10f, -6f), 100f, 100f);
            }
            CampaignSession.Set(0, s);
            return s;
        }

        /// <summary>在（起点, 半径）范围内找第一个能合法放下 <paramref name="typeId"/> 的枢轴格（按固定顺序扫描，确定性）。</summary>
        private static GridCell? FindValid(CampaignState s, string typeId, GridCell from, int radius, int rotation = 0)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, typeId, c, rotation).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>找一个探索区内、非起始区保护、地形为 <paramref name="terrainId"/> 的格子。</summary>
        private static GridCell? FindTerrain(CampaignState s, string terrainId, int radius)
        {
            byte code = GridContent.TerrainCode(terrainId);
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    var c = new GridCell(core.X + x, core.Y + y);
                    if (map.GetTerrain(c) == code && map.OccupantAt(c) == null && map.GetPollution(c) == 0 && AllExplored(map, c))
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static bool AllExplored(HomeGridMap map, GridCell c)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!map.IsExplored(new GridCell(c.X + dx, c.Y + dy)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static string BuildingsJson(CampaignState s) =>
            string.Join("\n", (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).OrderBy(b => b.BuildingId, StringComparer.Ordinal)
                .Select(b => $"{b.BuildingId}|{b.BuildingTypeId}|{b.GridX},{b.GridY}|{b.Rotation}|{b.Position.x:F2},{b.Position.y:F2}|{b.ConstructionState}"));

        private static bool SnapshotMatches(CampaignState s)
        {
            Dictionary<GridCell, string> cache = HomeGridService.MapFor(s).SnapshotOccupancy();
            Dictionary<GridCell, string> fresh = HomeGridService.RebuildSnapshotFromRecords(s);
            return cache.Count == fresh.Count && fresh.All(kv => cache.TryGetValue(kv.Key, out string v) && v == kv.Value);
        }

        private static void TickOrders(CampaignState s, float seconds, float dt = 0.1f)
        {
            int steps = Mathf.CeilToInt(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
                HomeValleyWorkOrders.Tick(s, dt, _ => null, _ => { }, _ => false,
                    order => HomeValleyWorkOrders.OnArrivedAtWork(s, order.WorkOrderId));
            }
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Expect(GridContent.LoadError == null && GridContent.Buildings.Count == 9 && GridContent.StartLayout.Count == 17
                   && GridContent.Terrains.Count == 7, $"五张格网表已加载：建筑格网属性 {GridContent.Buildings.Count}、开局布局 {GridContent.StartLayout.Count}、地形 {GridContent.Terrains.Count}");
            var missing = FgContentTables.Buildings.Where(b => !GridContent.TryGetBuilding(b.TypeId, out _)).Select(b => b.TypeId).ToList();
            Expect(missing.Count == 0, $"fg.TbBuilding 的每座建筑都有占地（缺：{string.Join(",", missing)}）");

            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            if (code != 0)
            {
                Fail($"fgdata.py --dump 失败：{Tail(output)}");
                return;
            }
            var diffs = new List<string>();
            int rows = 0;
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length < 2)
                {
                    continue;
                }
                switch (f[0])
                {
                    case "G":
                        rows++;
                        if (!GridContent.TryGetBuilding(f[1], out BuildingGrid g) || g.FootprintW != int.Parse(f[2]) || g.FootprintH != int.Parse(f[3])
                            || g.Placeable != int.Parse(f[4]) || g.MaxCount != int.Parse(f[5]) || g.UnlockRule != f[6] || g.Critical != int.Parse(f[7])
                            || g.RequiredTerrain != f[8] || g.DescKey != f[9] || g.UnlockHintKey != f[10])
                        {
                            diffs.Add("建筑格网 " + f[1]);
                        }
                        break;
                    case "P":
                        rows++;
                        BuildingPort p = GridContent.PortsOf(f[2]).FirstOrDefault(x => x.Id == f[1]);
                        if (p == null || p.LocalX != int.Parse(f[3]) || p.LocalY != int.Parse(f[4]) || p.Dir != f[5] || p.Kind != f[6])
                        {
                            diffs.Add("端口 " + f[1]);
                        }
                        break;
                    case "L":
                        rows++;
                        if (!GridContent.TryGetLayout(f[1], out StartLayout l) || l.Kind != f[2] || l.TypeId != f[3] || l.OffsetX != int.Parse(f[4])
                            || l.OffsetY != int.Parse(f[5]) || l.Rotation != int.Parse(f[6]) || l.State != f[7] || !Eq(l.Health, f[8]) || !Eq(l.Clearance, f[9]))
                        {
                            diffs.Add("开局布局 " + f[1]);
                        }
                        break;
                    case "X":
                        rows++;
                        GridTerrain t = GridContent.Terrains.FirstOrDefault(x => x.Id == f[1]);
                        if (t == null || t.Code != int.Parse(f[2]) || t.NameKey != f[3] || t.Buildable != int.Parse(f[4]) || t.Color != f[5] || t.Pattern != f[6])
                        {
                            diffs.Add("地形 " + f[1]);
                        }
                        break;
                    case "H":
                        rows++;
                        if (!GridContent.TryGetTuning(f[1], out float v) || !Eq(v, f[2]))
                        {
                            diffs.Add("调参 " + f[1]);
                        }
                        break;
                }
            }
            Expect(diffs.Count == 0 && rows > 60, $"源数据 fgdata_grid.py 与运行时五张表逐字段一致（{rows} 行）{(diffs.Count == 0 ? string.Empty : "——不一致：" + string.Join("，", diffs))}");

            (code, output) = RunPython(root, "tools/cell_tables/check_luban.py --selftest");
            Expect(code == 0 && output.Contains("开局布局占地重叠") && output.Contains("端口在占地外") && output.Contains("可放置却没有新建成本"),
                $"check_luban 格网规则（R12～R16）自测真跑：{Tail(output)}");

            // DEBT-FG0DATA01-03：家园常量已入表，数值与 Demo 一致，且改表 → 运行时跟着变。
            Expect(Mathf.Approximately(HomeValleyLayout.BaseCoreSupply, 20f) && Mathf.Approximately(HomeValleyLayout.BaseSignalBandwidth, 3f)
                   && Mathf.Approximately(HomeValleyLayout.SignalTowerBandwidthBonus, 5f) && HomeValleyLayout.CoreCacheCapacity == 180
                   && HomeValleyLayout.WarehouseCapacity == 300 && Mathf.Approximately(HomeValleyLayout.BatteryCapacity[HomeValleyLayout.Erc003ChassisId], 120f)
                   && Mathf.Approximately(HomeValleyLayout.DemolishSeconds, 15f),
                "家园常量读表（核心供电 20、基础带宽 3、信号塔 +5、核心缓存 180、仓库 300、履带电池 120、拆除 15 秒）与 Demo 一致");
            var tuning = new TbHomeTuning(TuningBuf(GridContent.Tuning, ("home.core_base_supply", 33f)));
            GridContent.OverrideForTests(tuning: tuning);
            float injected = HomeValleyLayout.BaseCoreSupply;
            GridContent.ResetForTests();
            Expect(Mathf.Approximately(injected, 33f) && Mathf.Approximately(HomeValleyLayout.BaseCoreSupply, 20f),
                $"改表 home.core_base_supply=33 → 核心基础供电 {injected}；还原后 {HomeValleyLayout.BaseCoreSupply}");

            var keys = new[] { "grid.reason.occupied", "grid.reason.terrain", "grid.reason.fog", "grid.reason.core_reserve", "grid.reason.pollution",
                "grid.reason.max_count", "grid.reason.locked", "grid.reason.insufficient_scrap", "ui.build.title", "ui.build.hint_place", "grid.terrain.cliff.name" };
            var badKeys = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                          || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            Expect(badKeys.Count == 0, $"格网建造的原因 / 界面文本中英两列齐全（抽查 {keys.Length} 条）{string.Join(",", badKeys)}");
        }

        // ── B. 几何 ──────────────────────────────────────────────────────────────

        private static void CheckGeometry()
        {
            Vector2Int r90 = GridMath.RotateOffset(2, 1, 90);
            Vector2Int r180 = GridMath.RotateOffset(2, 1, 180);
            Vector2Int r270 = GridMath.RotateOffset(2, 1, 270);
            Expect(r90 == new Vector2Int(1, -2) && r180 == new Vector2Int(-2, -1) && r270 == new Vector2Int(-1, 2) && GridMath.RotateOffset(2, 1, 360) == new Vector2Int(2, 1),
                $"局部偏移顺时针旋转：(2,1) → 90°{r90} / 180°{r180} / 270°{r270} / 360° 回原位");
            Expect(GridMath.NormalizeRotation(-90) == 270 && GridMath.NormalizeRotation(450) == 90 && GridMath.NormalizeRotation(44) == 0,
                "朝向规整：-90→270、450→90、44→0");

            var cells = new List<GridCell>();
            GridMath.FootprintCells(new GridCell(20, 0), 5, 3, 0, cells);
            GridMath.FootprintBounds(new GridCell(20, 0), 5, 3, 0, out GridCell a0, out GridCell b0);
            GridMath.FootprintBounds(new GridCell(20, 0), 5, 3, 90, out GridCell a1, out GridCell b1);
            Expect(cells.Count == 15 && cells.Distinct().Count() == 15 && a0 == new GridCell(18, -1) && b0 == new GridCell(22, 1)
                   && a1 == new GridCell(19, -2) && b1 == new GridCell(21, 2) && GridMath.FootprintCenter(new GridCell(20, 0), 5, 3, 90) == new Vector2(20f, 0f),
                $"5×3 占地：旋转 0 覆盖 {a0}～{b0}，旋转 90 覆盖 {a1}～{b1}，枢轴格与中心不动");
            GridMath.FootprintBounds(new GridCell(0, 0), 2, 2, 90, out GridCell e0, out GridCell e1);
            Expect(e0 == new GridCell(0, -1) && e1 == new GridCell(1, 0), $"偶数尺寸 2×2 旋转 90 后绕枢轴格：{e0}～{e1}");

            // 负向：旋转后端口方向。仓库输出口旋转 0 朝东在枢轴格东 2 格；每转 90° 朝向与位置都顺时针转。
            var ports = new List<PortPlacement>();
            string[] expect = { "E(22,0)", "S(20,-2)", "W(18,0)", "N(20,2)" };
            var got = new List<string>();
            for (int rot = 0; rot < 360; rot += 90)
            {
                HomeGridService.PortsFor(HomeValleyLayout.BuildingTypeWarehouse, new GridCell(20, 0), rot, ports);
                PortPlacement o = ports.First(p => p.IsOutput);
                got.Add($"{o.Dir}({o.Cell.X},{o.Cell.Y})");
            }
            Expect(got.SequenceEqual(expect), $"负向：旋转后端口方向——仓库输出口 0/90/180/270：{string.Join(" ", got)}（应为 {string.Join(" ", expect)}）");

            ChunkAddress neg = GridMath.Address(new GridCell(-1, -33), 32);
            ChunkAddress pos = GridMath.Address(new GridCell(32, 31), 32);
            Expect(neg.ChunkX == -1 && neg.LocalX == 31 && neg.ChunkY == -2 && neg.LocalY == 31 && pos.ChunkX == 1 && pos.LocalX == 0 && pos.ChunkY == 0 && pos.LocalY == 31,
                "区块寻址（区块索引 + 区块内偏移）：(-1,-33) → 区块(-1,-2)偏移(31,31)；(32,31) → 区块(1,0)偏移(0,31)");
        }

        // ── C. 开局布局迁移与种子无关性 ─────────────────────────────────────────────

        private static void CheckStartLayout()
        {
            CampaignState s = NewHome(4242);
            var demo = new Dictionary<string, Vector2>
            {
                ["core"] = new Vector2(0, 0), ["generator"] = new Vector2(14, 14), ["warehouse"] = new Vector2(20, 0), ["signal_tower"] = new Vector2(-14, 14),
                ["assembly_station"] = new Vector2(0, -20), ["analysis_bench"] = new Vector2(-10, -20), ["repair_bay"] = new Vector2(16, -14),
            };
            List<BuildingRecord> home = s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId).ToList();
            var wrong = demo.Where(kv =>
            {
                BuildingRecord b = home.FirstOrDefault(x => x.BuildingId == HomeValleyLayout.RegionId + ":" + kv.Key);
                return b == null || b.BuildingTypeId != kv.Key || b.Position != kv.Value || b.GridX != (int)kv.Value.x || b.GridY != (int)kv.Value.y
                       || !Mathf.Approximately(b.Rotation, 0f);
            }).Select(kv => kv.Key).ToList();
            Expect(home.Count == 7 && wrong.Count == 0,
                $"生产入口（家园首次播种）：开局 {home.Count} 座建筑来自开局布局表，ID / 位置 / 枢轴格 / 朝向与 Demo 锚点逐项一致{(wrong.Count == 0 ? string.Empty : "——不符：" + string.Join(",", wrong))}");
            BuildingRecord gen = home.First(b => b.BuildingTypeId == "generator");
            BuildingRecord core = home.First(b => b.BuildingTypeId == "core");
            Expect(gen.ConstructionState == BuildingConstructionState.Damaged && core.ConstructionState == BuildingConstructionState.Operational
                   && Mathf.Approximately(core.Health, 500f) && s.Grid.LayoutVersion == HomeGridService.LayoutVersion
                   && s.Grid.Explored.Length == 1 && s.Grid.Explored[0].Radius == 40 && s.Grid.TerrainSourceId == WorldTerrainSource.Id,
                "开局状态来自表（发电机受损、核心运转 500 生命）；格网域写入布局版本、开局探索半径 40、地形来源（FG0-ARCH-05 起新战役 = 世界生成器）");
            Expect(HomeValleyLayout.Validate().Count == 0 && HomeValleyLayout.Core.Position == Vector2.zero && HomeValleyLayout.Generator2Site.Position == new Vector2(24, -8)
                   && HomeValleyLayout.BeaconSlot.Position == new Vector2(12, 22) && HomeValleyLayout.CameraFocusStart == new Vector2(-4, 2),
                "非建筑锚点（建造位、出生点、残骸、镜头焦点）改由开局布局表提供，位置不变、净空不重叠");
            Expect(SnapshotMatches(s) && HomeGridService.MapFor(s).SnapshotOccupancy().Count == 5 * 5 + 3 * 3 * 4 + 5 * 3 + 5 * 5,
                "占用层 = 按建筑记录重建（逐格一致），开局 7 座共占 5×5+3×3×4+5×3+5×5 格");

            // B25：任意种子下开局布局与建造位都合法（起始区保证），且核心周围确实生成出了会阻挡放置的地形。
            var badSeeds = new List<string>();
            var cliffSeeds = 0;
            foreach (int seed in Seeds)
            {
                CampaignState t = NewHome(seed, withHauler: false);
                foreach (BuildingRecord b in t.BuildingRecords)
                {
                    GridPlacementResult r = HomeGridService.ValidatePlacement(t, b.BuildingTypeId, new GridCell(b.GridX, b.GridY), (int)b.Rotation,
                        asPlayerPlacement: false, ignoreBuildingId: b.BuildingId, checkCost: false);
                    if (!r.Ok)
                    {
                        badSeeds.Add($"{seed}:{b.BuildingTypeId}:{r.Describe()}");
                    }
                }
                foreach (string site in new[] { "generator_2", "beacon_slot" })
                {
                    StartLayout row = GridContent.Layout(site);
                    GridPlacementResult r = HomeGridService.ValidatePlacement(t, row.TypeId, HomeGridService.AnchorCell(t, site), row.Rotation,
                        asPlayerPlacement: false, checkCost: false);
                    if (!r.Ok)
                    {
                        badSeeds.Add($"{seed}:{site}:{r.Describe()}");
                    }
                }
                if (FindTerrain(t, "cliff", 38) != null)
                {
                    cliffSeeds++;
                }
            }
            Expect(badSeeds.Count == 0, $"B25：{Seeds.Length} 个种子（含负数）下开局 7 座建筑与 2 个建造位全部合法{(badSeeds.Count == 0 ? string.Empty : "——" + string.Join("；", badSeeds.Take(5)))}");
            Expect(cliffSeeds == Seeds.Length, $"世界生成器（FG0-ARCH-05 起新战役的地形来源）在每个种子的已探索区内都生成了悬崖（{cliffSeeds}/{Seeds.Length}），非法地形的负向用例在任意种子下可测");

            // 确定性：同一种子，两张新图按相反顺序访问同一片区块，逐格一致；不同种子不同。
            var proto = new GridTerrainPrototype(99, new GridCell(0, 0));
            var m1 = new HomeGridMap(32, proto);
            var m2 = new HomeGridMap(32, new GridTerrainPrototype(99, new GridCell(0, 0)));
            var m3 = new HomeGridMap(32, new GridTerrainPrototype(100, new GridCell(0, 0)));
            var order = new List<GridCell>();
            for (int y = -64; y < 64; y += 3)
            {
                for (int x = -64; x < 64; x += 5)
                {
                    order.Add(new GridCell(x, y));
                }
            }
            var first = order.Select(c => (m1.GetTerrain(c), m1.GetPollution(c))).ToList();
            var reversed = Enumerable.Reverse(order).Select(c => (m2.GetTerrain(c), m2.GetPollution(c))).Reverse().ToList();
            int diffSeed = order.Count(c => m3.GetTerrain(c) != m1.GetTerrain(c));
            Expect(first.SequenceEqual(reversed) && diffSeed > 0,
                $"地形确定性：同一种子正序 / 倒序访问 {order.Count} 格逐格一致；换种子后 {diffSeed} 格不同");
        }

        // ── D. 放置与负向矩阵（FGT-LOG-001 原型：非法放置给出正确原因）────────────────────────

        private static void CheckPlacementMatrix()
        {
            CampaignState s = NewHome(4243);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? spot = FindValid(s, "generator_2", new GridCell(core.X + 8, core.Y + 26), 10);
            if (spot == null)
            {
                Fail("找不到能合法放发电机的空地");
                return;
            }
            float scrapBefore = s.Scrap;
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", spot.Value, 90);
            BuildingRecord rec = HomeGridService.FindBuilding(s, placed.BuildingId);
            WorkOrderRecord order = s.WorkOrders.FirstOrDefault(o => o.Kind == WorkOrderKind.Build && o.TargetId == placed.BuildingId);
            Expect(placed.Outcome == GridOpResult.Kind.Placed && rec != null && rec.ConstructionState == BuildingConstructionState.Planned
                   && rec.GridX == spot.Value.X && rec.GridY == spot.Value.Y && Mathf.Approximately(rec.Rotation, 90f) && rec.Position == spot.Value.ToWorld()
                   && Mathf.Approximately(s.Scrap, scrapBefore - 60f) && order != null && order.State == WorkOrderState.Ready && order.AssignedMachineLogicId == 0,
                $"合法放置 {spot.Value}：规划中的格网建筑（朝向 90、中心 = 枢轴格），预留 60 废料，新建工单进入待分配池");
            Expect(HomeGridService.BuildingAt(s, spot.Value)?.BuildingId == placed.BuildingId && SnapshotMatches(s) && placed.BuildingId == "home_valley:generator_2",
                "放置后立即占格（查询即可见），占用 = 按记录重建；每类第一座沿用 Demo 的建筑 ID");

            // 同一格再放一次：重叠。
            AssertBlocked(s, "generator_2", spot.Value, 0, GridBlockReason.Occupied, "负向：在建筑上重叠放置", "发电机");
            // 在核心上：核心占地 + 通道。
            AssertBlocked(s, "generator_2", new GridCell(core.X + 4, core.Y), 0, GridBlockReason.CoreReserve, "核心周围 3 格通道", "3 格");
            // 悬崖 / 水源。
            GridCell? cliff = FindTerrain(s, "cliff", 38);
            GridCell? water = FindTerrain(s, "water", 38);
            if (cliff != null)
            {
                AssertBlocked(s, "generator_2", cliff.Value, 0, GridBlockReason.Terrain, $"负向：在悬崖上放置 {cliff.Value}", "悬崖");
            }
            else
            {
                Fail("已探索区里找不到悬崖格（原型地形参数失效）");
            }
            if (water != null)
            {
                // FGR-LOG-001：只有悬崖不可建；水源是资源点，普通建筑也能压（泵用 requiredTerrain 要求压在水源上）。
                GridPlacementResult onWaterPlain = HomeGridService.ValidatePlacement(s, "generator_2", water.Value, 0);
                Expect(!onWaterPlain.Has(GridBlockReason.Terrain), $"水源可以建造（FGR-LOG-001 只有悬崖不可建）：{water.Value} 上不报地形不符（“{onWaterPlain.Describe()}”）");
            }
            else
            {
                Fail("已探索区里找不到水源格（原型地形参数失效）");
            }
            // 迷雾。
            AssertBlocked(s, "generator_2", new GridCell(core.X + 60, core.Y + 60), 0, GridBlockReason.Fog, "未探索区域", "已探索");
            // 污染 / 传送带 / 管线：在一块合法空地上改一格。
            GridCell free = FindValid(s, "generator_2", new GridCell(core.X - 20, core.Y + 26), 10) ?? new GridCell(0, 0);
            HomeGridMap map = HomeGridService.MapFor(s);
            map.SetPollution(free, 2);
            AssertBlocked(s, "generator_2", free, 0, GridBlockReason.Pollution, "污染 2 级不可建（FGR-LOG-012）", "2 级");
            map.SetPollution(free, 1);
            Expect(HomeGridService.ValidatePlacement(s, "generator_2", free, 0).Ok, "污染 1 级可以建造（FGR-LOG-012）");
            map.SetPollution(free, 0);
            map.SetBelt(free, 7);
            AssertBlocked(s, "generator_2", free, 0, GridBlockReason.OccupiedBelt, "传送带层已占用", "传送带");
            map.SetBelt(free, 0);
            map.SetPipe(new GridCell(free.X + 1, free.Y), 3);
            AssertBlocked(s, "generator_2", free, 0, GridBlockReason.OccupiedPipe, "管线层已占用（占地任一格）", "管线");
            map.SetPipe(new GridCell(free.X + 1, free.Y), 0);
            // 非建筑锚点：未拆的残骸挡住放置；拆解后空出来。
            GridCell wreck = HomeGridService.AnchorCell(s, HomeValleyLayout.Wreckage2NodeId);
            AssertBlocked(s, "generator_2", wreck, 0, GridBlockReason.Obstacle, "压在未拆的残骸上", "残骸");
            RegionRecord home = s.RegionRecords.First(rr => rr.RegionId == HomeValleyLayout.RegionId);
            home.DestroyedNodeIds = (home.DestroyedNodeIds ?? Array.Empty<string>()).Append(HomeValleyLayout.Wreckage2NodeId).ToArray();
            Expect(!HomeGridService.ValidatePlacement(s, "generator_2", wreck, 0).Has(GridBlockReason.Obstacle), "残骸拆解后这块地可以建造");
            AssertBlocked(s, "generator_2", HomeGridService.AnchorCell(s, "assembly_exit"), 0, GridBlockReason.Obstacle, "堵住装配站出口", "出口");
            // 不可放置 / 未解锁 / 未知类型 / 废料不足。
            AssertBlocked(s, HomeValleyLayout.BuildingTypeWarehouse, free, 0, GridBlockReason.NotPlaceable, "开局预置建筑不能手动放置", "不能手动放置");
            AssertBlocked(s, HomeValleyLayout.BuildingTypeBeacon, free, 0, GridBlockReason.Locked, "信标未解锁", "主核心");
            AssertBlocked(s, "no_such_building", free, 0, GridBlockReason.UnknownType, "未知类型", "未知");
            s.Scrap = 10;
            AssertBlocked(s, "generator_2", free, 0, GridBlockReason.InsufficientScrap, "废料不足", "需要 60，现有 10");
            s.Scrap = 500;
            // 数量上限：解锁信标（完成 OBJ-09）后能放 1 座，第 2 座被拒。
            s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                .Append(new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj09, State = ObjectiveState.Completed }).ToArray();
            GridCell? beaconSpot = FindValid(s, HomeValleyLayout.BuildingTypeBeacon, new GridCell(core.X + 12, core.Y + 22), 12);
            GridOpResult beacon = beaconSpot != null ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeBeacon, beaconSpot.Value, 0) : default;
            Expect(beacon.Outcome == GridOpResult.Kind.Placed && beacon.BuildingId == "home_valley:beacon", $"解锁后信标放在 {beaconSpot}");
            AssertBlocked(s, HomeValleyLayout.BuildingTypeBeacon, free, 0, GridBlockReason.MaxCount, "信标数量上限 1", "1 座");
            // 同类第二座：新 ID 带实例序号。
            GridOpResult second = HomeGridService.TryPlace(s, "generator_2", free, 0);
            Expect(second.Outcome == GridOpResult.Kind.Placed && second.BuildingId == "home_valley:generator_2#2" && s.Grid.NextInstanceSerial == 3
                   && MechanicalContentFacade.ResolveWorkOrderTargetLabel(second.BuildingId) == "发电机",
                $"同类第二座：ID {second.BuildingId}（实例序号续编），名字按类型显示“{MechanicalContentFacade.ResolveWorkOrderTargetLabel(second.BuildingId)}”");

            // 需要特定地形：注入“发电机必须压在金属矿脉上”的测试表。
            GridCell? ore = FindTerrain(s, "ore_metal", 38);
            GridCell farFree = FindValid(s, "generator_2", new GridCell(core.X - 26, core.Y - 26), 8) ?? free;
            var grid = new TbBuildingGrid(GridBuf(GridContent.Buildings, g => g.TypeId == "generator_2" ? WithTerrain(g, "ore_metal") : g));
            GridContent.OverrideForTests(grid: grid);
            HomeGridService.Invalidate();
            GridPlacementResult needs = HomeGridService.ValidatePlacement(s, "generator_2", farFree, 0);
            GridPlacementResult onOre = ore != null ? HomeGridService.ValidatePlacement(s, "generator_2", ore.Value, 0) : null;
            GridContent.ResetForTests();
            HomeGridService.Invalidate();
            Expect(needs.Has(GridBlockReason.NeedsTerrain) && needs.Describe().Contains("金属矿脉") && onOre != null && !onOre.Has(GridBlockReason.NeedsTerrain),
                $"采集类建筑需要资源点（注入测试表）：空地上“{needs.Describe()}”；矿脉 {ore} 上不再报这一条");

            // 需要放在水源上（泵，FG03 第 143 行）：注入“发电机必须压在水源上”，水源上能合法放下，空地上报“需要放在水源上”。
            var waterGrid = new TbBuildingGrid(GridBuf(GridContent.Buildings, g => g.TypeId == "generator_2" ? WithTerrain(g, "water") : g));
            GridContent.OverrideForTests(grid: waterGrid);
            HomeGridService.Invalidate();
            GridPlacementResult needsWater = HomeGridService.ValidatePlacement(s, "generator_2", farFree, 0);
            byte waterCode = GridContent.TerrainCode("water");
            HomeGridMap wmap = HomeGridService.MapFor(s);
            GridCell? pumpSpot = null;
            for (int y = -38; y <= 38 && pumpSpot == null; y++)
            {
                for (int x = -38; x <= 38; x++)
                {
                    var c = new GridCell(core.X + x, core.Y + y);
                    if (wmap.GetTerrain(c) == waterCode && HomeGridService.ValidatePlacement(s, "generator_2", c, 0).Ok)
                    {
                        pumpSpot = c;
                        break;
                    }
                }
            }
            GridOpResult pumpPlaced = pumpSpot != null ? HomeGridService.TryPlace(s, "generator_2", pumpSpot.Value, 0) : default;
            if (pumpPlaced.Success)
            {
                HomeGridService.TryToggleDemolish(s, pumpPlaced.BuildingId); // 取消规划，不影响后面的计数
            }
            GridContent.ResetForTests();
            HomeGridService.Invalidate();
            Expect(needsWater.Has(GridBlockReason.NeedsTerrain) && needsWater.Describe().Contains("需要放在水源上") && pumpSpot != null && pumpPlaced.Success,
                $"需要放在水源上（注入测试表，FGR-LOG-003）：空地上“{needsWater.Describe()}”；水源 {pumpSpot} 上可以合法放下（{pumpPlaced.Outcome}）");

            // 英文界面下原因同样可读、无缺键标记。
            GameSettings.SetLanguage(GameLanguage.En);
            GridPlacementResult en = HomeGridService.ValidatePlacement(s, "generator_2", spot.Value, 0);
            string enText = en.Describe();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(enText.Contains("Occupied by") && !GameText.ContainsMarker(enText), $"英文原因：“{enText}”");
            Expect(s.WorkOrders.Count(o => o.Kind == WorkOrderKind.Build) == 4,
                "非法放置不产生工单、不改记录（全程只有 4 次合法放置产生了工单，其中水源上的一座已取消规划）");
        }

        private static void AssertBlocked(CampaignState s, string typeId, GridCell cell, int rotation, GridBlockReason code, string what, string textContains)
        {
            BuildingRecord[] recordsBefore = s.BuildingRecords;
            float scrapBefore = s.Scrap;
            int ordersBefore = s.WorkOrders?.Length ?? 0;
            GridOpResult r = HomeGridService.TryPlace(s, typeId, cell, rotation);
            string text = r.Describe();
            bool untouched = ReferenceEquals(recordsBefore, s.BuildingRecords) && Mathf.Approximately(scrapBefore, s.Scrap) && ordersBefore == (s.WorkOrders?.Length ?? 0);
            bool hasCode = r.Placement != null ? r.Placement.Has(code) : r.FirstReason == code;
            Expect(!r.Success && hasCode && text.Contains(textContains) && !GameText.ContainsMarker(text) && untouched,
                $"{what}：被拒，原因“{text}”，状态未改动");
        }

        // ── E. 旋转与拆除 ────────────────────────────────────────────────────────

        private static void CheckRotateAndDemolish()
        {
            CampaignState s = NewHome(4244);
            string warehouseId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeWarehouse;
            BuildingRecord wh = HomeGridService.FindBuilding(s, warehouseId);
            var ports = new List<PortPlacement>();
            HomeGridService.PortsOf(wh, ports);
            GridDir before = ports.First(p => p.IsOutput).Dir;
            GridOpResult rot = HomeGridService.TryRotate(s, warehouseId);
            HomeGridService.PortsOf(wh, ports);
            GridDir after = ports.First(p => p.IsOutput).Dir;
            Expect(rot.Outcome == GridOpResult.Kind.Rotated && Mathf.Approximately(wh.Rotation, 90f) && before == GridDir.E && after == GridDir.S
                   && HomeGridService.BuildingAt(s, new GridCell(20, 2))?.BuildingId == warehouseId && HomeGridService.BuildingAt(s, new GridCell(22, 0)) == null
                   && SnapshotMatches(s),
                "旋转已有建筑（仓库 5×3）：朝向 0→90，占格随之改变（(20,2) 被占、(22,0) 空出），输出口 东→南，占用 = 按记录重建");

            // 被挡时拒绝旋转：在仓库旋转后要占的格子旁放一座发电机，再转一次（90→180 恢复横向）会撞上。
            GridCell blocker = new GridCell(23, 0);
            GridOpResult g = HomeGridService.TryPlace(s, "generator_2", blocker, 0);
            GridOpResult blocked = HomeGridService.TryRotate(s, warehouseId);
            Expect(g.Success && !blocked.Success && blocked.FirstReason == GridBlockReason.Occupied && Mathf.Approximately(wh.Rotation, 90f) && SnapshotMatches(s),
                $"旋转后与别的建筑重叠：拒绝（“{blocked.Describe()}”），朝向保持 90，占格不变");

            GridOpResult coreRot = HomeGridService.TryRotate(s, HomeValleyLayout.RegionId + ":core");
            Expect(!coreRot.Success && coreRot.FirstReason == GridBlockReason.CannotRotateCore && coreRot.Describe() == "归还核心不能旋转", "核心不能旋转");

            // 取消规划：全额退款、记录与占格一起消失。
            float scrap0 = s.Scrap;
            GridOpResult cancel = HomeGridService.TryToggleDemolish(s, g.BuildingId);
            Expect(cancel.Outcome == GridOpResult.Kind.PlanCancelled && HomeGridService.FindBuilding(s, g.BuildingId) == null
                   && Mathf.Approximately(s.Scrap, scrap0 + 60f) && HomeGridService.BuildingAt(s, blocker) == null && SnapshotMatches(s),
                $"拆除模式点规划中的虚影 = 取消规划：废料全额退回（{scrap0}→{s.Scrap}），记录与占格一并移除");

            // 建成后拆除：标记 → 撤回 → 再标记 → 机器完成 → 记录消失、占格释放、按实际投入的一半返还。
            GridOpResult g2 = HomeGridService.TryPlace(s, "generator_2", blocker, 0);
            TickOrders(s, 45f);
            BuildingRecord built = HomeGridService.FindBuilding(s, g2.BuildingId);
            Expect(built != null && built.ConstructionState == BuildingConstructionState.Operational && built.InvestedScrap == 60,
                "机器领取待分配的新建工单，40 秒后建成（实际投入 60）");
            GridOpResult mark = HomeGridService.TryToggleDemolish(s, g2.BuildingId);
            WorkOrderRecord demolish = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Salvage && o.TargetId == g2.BuildingId);
            GridOpResult unmark = HomeGridService.TryToggleDemolish(s, g2.BuildingId);
            Expect(mark.Outcome == GridOpResult.Kind.DemolishMarked && demolish != null && unmark.Outcome == GridOpResult.Kind.DemolishUnmarked
                   && demolish.State == WorkOrderState.Cancelled && HomeGridService.FindBuilding(s, g2.BuildingId) != null,
                "拆除标记可撤回：再点一次取消标记，拆除工单作废，建筑保留（可逆操作不弹确认，FG00 B04）");
            float scrap1 = s.Scrap;
            HomeGridService.TryToggleDemolish(s, g2.BuildingId);
            WorkOrderRecord demolish2 = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Salvage && o.TargetId == g2.BuildingId);
            TickOrders(s, 20f);
            bool refunded = s.Scrap >= scrap1 + 30f - 0.01f
                            || (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(gi => demolish2 != null
                                && gi.SalvageInstanceId == HomeValleyWorkOrders.DemolishDropId(demolish2) && gi.Amount == 30);
            Expect(HomeGridService.FindBuilding(s, g2.BuildingId) == null && HomeGridService.BuildingAt(s, blocker) == null && SnapshotMatches(s) && refunded,
                $"拆除完成：记录消失、占格释放，返还实际投入的一半 30（进仓或仓满时落地待搬，{scrap1}→{s.Scrap}）");

            GridOpResult coreDemolish = HomeGridService.TryToggleDemolish(s, HomeValleyLayout.RegionId + ":core");
            Expect(coreDemolish.FirstReason == GridBlockReason.CannotDemolishCore, $"核心不能拆（“{coreDemolish.Describe()}”）");
            // 拆除返还地面物的 salvage ID 每张工单唯一：同一建筑 ID 重建后再拆，第二份返还不会被旧地面物按 ID 去重吞掉。
            GridOpResult g3 = HomeGridService.TryPlace(s, "generator_2", blocker, 0);
            TickOrders(s, 45f);
            HomeGridService.TryToggleDemolish(s, g3.BuildingId);
            WorkOrderRecord demolish3 = s.WorkOrders.LastOrDefault(o => o.Kind == WorkOrderKind.Salvage && o.TargetId == g3.BuildingId);
            Expect(g3.BuildingId == g2.BuildingId && demolish2 != null && demolish3 != null
                   && HomeValleyWorkOrders.DemolishDropId(demolish2) != HomeValleyWorkOrders.DemolishDropId(demolish3),
                $"同一建筑 ID（{g3.BuildingId}）重建后再拆：两次拆除的返还地面物 ID 不同（{(demolish3 != null ? HomeValleyWorkOrders.DemolishDropId(demolish3) : "无")}）");
            HomeGridService.TryToggleDemolish(s, g3.BuildingId); // 撤回，后面不受影响
            // 受损建筑先修复：把这座可重建的发电机临时标成受损再点拆除（开局发电机本身禁拆，测不到这条）。
            BuildingRecord g3Rec = HomeGridService.FindBuilding(s, g3.BuildingId);
            g3Rec.ConstructionState = BuildingConstructionState.Damaged;
            GridOpResult damaged = HomeGridService.TryToggleDemolish(s, g3.BuildingId);
            g3Rec.ConstructionState = BuildingConstructionState.Operational;
            Expect(damaged.FirstReason == GridBlockReason.DemolishDamaged && !HomeGridService.IsMarkedForDemolish(s, g3.BuildingId),
                $"受损建筑先修复（“{damaged.Describe()}”）");

            // 软锁保底（FG00 B11）：本局无法重建的开局建筑（placeable=0）全部不能拆——目标清单实时按类型找这些建筑，拆掉就永远完不成；
            // 拆除模式与 Demo 的“点选机器再点建筑”都拦。
            string[] forbidden = { "generator", "warehouse", "signal_tower", "assembly_station", "analysis_bench", "repair_bay" };
            var refusals = new List<string>();
            bool allRefused = true;
            foreach (string t in forbidden)
            {
                string id = HomeValleyLayout.RegionId + ":" + t;
                BuildingRecord[] recordsBefore = s.BuildingRecords;
                int ordersBefore = s.WorkOrders.Length;
                GridOpResult r = HomeGridService.TryToggleDemolish(s, id);
                HomeValleyWorkOrders.WorkOrderOpResult legacy = HomeValleyWorkOrders.TryCreateDemolishBuilding(s, id, 0);
                allRefused &= !r.Success && r.FirstReason == GridBlockReason.NotRebuildable && !legacy.Success && legacy.FailureReason == "not-rebuildable"
                              && ReferenceEquals(recordsBefore, s.BuildingRecords) && ordersBefore == s.WorkOrders.Length
                              && !HomeGridService.DemolishNeedsConfirm(s, id) && !HomeGridService.IsRebuildable(t);
                refusals.Add(t + "：" + r.Describe());
            }
            Expect(allRefused && refusals[0].Contains("无法重建"),
                $"本局无法重建的开局建筑（6 种）都不能拆除（拆除模式与工单入口都拒绝，不弹确认，状态未改动）：{string.Join("；", refusals)}");
            Expect(!HomeGridService.IsDemolishForbidden("generator_2") && HomeGridService.IsRebuildable("generator_2")
                   && !HomeGridService.DemolishNeedsConfirm(s, g3.BuildingId),
                "可放置的普通发电机能重建、可以拆，且不需确认（可逆，FG00 B04）");

            // 经建造模式：可拆的关键建筑（信标，critical=1、placeable=1）先弹确认框（写明返还与“关键建筑”），点“保留”什么都不发生，点“拆除”才标记。
            s.Scrap = 500;
            s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                .Append(new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj09, State = ObjectiveState.Completed }).ToArray();
            GridCell? beaconCell = FindValid(s, HomeValleyLayout.BuildingTypeBeacon, new GridCell(12, 22), 12);
            GridOpResult beaconPlaced = beaconCell != null ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeBeacon, beaconCell.Value, 0) : default;
            TickOrders(s, 60f);
            BuildingRecord beaconRec = beaconPlaced.Success ? HomeGridService.FindBuilding(s, beaconPlaced.BuildingId) : null;
            bool beaconBuilt = beaconRec != null && beaconRec.ConstructionState == BuildingConstructionState.Operational;
            var mode = new HomeValleyBuildMode();
            mode.SetDemolishMode(true);
            bool asked = false, keptAfterCancel = false, markedAfterConfirm = false;
            if (beaconBuilt)
            {
                mode.ClickCell(s, new GridCell(beaconRec.GridX, beaconRec.GridY));
                asked = UiConfirmDialog.IsOpen && mode.PendingConfirmBuildingId == beaconRec.BuildingId
                        && UiConfirmDialog.Current.Lines.Any(c => c.Contains("关键建筑"))
                        && UiConfirmDialog.Current.Consequences.Any(c => c.Contains("返还"));
                UiConfirmDialog.Cancel();
                keptAfterCancel = !HomeGridService.IsMarkedForDemolish(s, beaconRec.BuildingId);
                mode.ClickCell(s, new GridCell(beaconRec.GridX, beaconRec.GridY));
                UiConfirmDialog.Confirm();
                markedAfterConfirm = HomeGridService.IsMarkedForDemolish(s, beaconRec.BuildingId);
            }
            // 禁拆建筑：不弹框，直接拒绝并在状态行写明原因。
            var modeRefusals = new List<string>();
            bool modeRefused = true;
            foreach (string t in new[] { "repair_bay", "warehouse", "assembly_station" })
            {
                BuildingRecord rec = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + t);
                GridOpResult click = mode.ClickCell(s, new GridCell(rec.GridX, rec.GridY));
                modeRefused &= !UiConfirmDialog.IsOpen && !click.Success && mode.StatusIsError && mode.StatusText.Contains("无法重建")
                               && !mode.StatusText.StartsWith("不能放置") && !HomeGridService.IsMarkedForDemolish(s, rec.BuildingId);
                modeRefusals.Add(t + "：" + mode.StatusText);
            }
            // 状态行前缀按操作区分：旋转被拒写“不能旋转：”（不再是“不能放置：”）。
            mode.SetDemolishMode(false);
            mode.SetHover(s, HomeGridService.CorePivot(s));
            GridOpResult coreRotByMode = mode.RotateHovered(s);
            string rotateStatus = mode.StatusText;
            Expect(!coreRotByMode.Success && rotateStatus == "不能旋转：归还核心不能旋转",
                $"建造模式里旋转核心被拒：状态行“{rotateStatus}”（拆除被拒不带“不能放置”前缀，见下一条）");
            mode.Shutdown();
            Expect(beaconBuilt && asked && keptAfterCancel && markedAfterConfirm && modeRefused,
                $"拆除模式点已建成的信标（{beaconPlaced.BuildingId}）：弹确认框（写明返还与关键建筑）；点“保留”不标记，点“拆除”才标记。"
                + $"点维修台 / 仓库 / 装配站：不弹框，直接拒绝（{string.Join("；", modeRefusals)}）");

            CheckObjectiveBuildingNotDemolishable();
        }

        /// <summary>
        /// 复审第 2 轮复现：OBJ-02“恢复仓库与装配站”活跃、仓库已修好、装配站还没通电时，拆掉仓库会让 OBJ-02 永远完不成
        /// （清单每次实时判定，单项不锁存）。断言两条拆除入口都拒绝、状态不变，装配站通电后目标照常完成。
        /// </summary>
        private static void CheckObjectiveBuildingNotDemolishable()
        {
            CampaignState o = NewHome(4261);
            o.ObjectiveRecords = new[]
            {
                new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj01, State = ObjectiveState.Completed },
                new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj02, State = ObjectiveState.Active },
            };
            string whId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeWarehouse;
            string asId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation;
            BuildingRecord wh = HomeGridService.FindBuilding(o, whId);
            BuildingRecord station = HomeGridService.FindBuilding(o, asId);
            wh.ConstructionState = BuildingConstructionState.Operational;
            station.ConstructionState = BuildingConstructionState.Damaged;
            bool setupOk = CampaignObjectiveTracker.CurrentObjectiveId(o) == CampaignObjectiveTracker.Obj02
                           && !CampaignObjectiveCatalog.AllItemsDone(o, CampaignObjectiveTracker.Obj02);
            MachineOpResult worker = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId,
                HomeValleyLayout.RegionId, new Vector2(18f, -4f), 100f, 100f);
            BuildingRecord[] recordsBefore = o.BuildingRecords;
            int ordersBefore = o.WorkOrders.Length;

            var mode = new HomeValleyBuildMode();
            mode.SetDemolishMode(true);
            GridOpResult byMode = mode.ClickCell(o, new GridCell(wh.GridX, wh.GridY));
            bool noDialog = !UiConfirmDialog.IsOpen;
            mode.Shutdown();
            HomeValleyWorkOrders.WorkOrderOpResult byMachine = HomeValleyWorkOrders.TryCreateDemolishBuilding(o, whId, worker.LogicId);
            bool kept = ReferenceEquals(recordsBefore, o.BuildingRecords) && HomeGridService.FindBuilding(o, whId) == wh
                        && !HomeGridService.IsMarkedForDemolish(o, whId) && o.WorkOrders.Length == ordersBefore;

            // 装配站修好并通电 → 仓库那一项仍然成立，OBJ-02 照常完成。
            station.ConstructionState = BuildingConstructionState.Operational;
            station.PowerState = BuildingPowerState.Powered;
            bool completable = CampaignObjectiveCatalog.AllItemsDone(o, CampaignObjectiveTracker.Obj02);
            Expect(setupOk && worker.Success && !byMode.Success && byMode.FirstReason == GridBlockReason.NotRebuildable && noDialog
                   && !byMachine.Success && byMachine.FailureReason == "not-rebuildable" && kept && completable,
                $"OBJ-02 活跃、仓库已修好、装配站未通电：拆除模式（“{byMode.Describe()}”，不弹框）与“选中机器再点建筑”入口（{byMachine.FailureReason}）"
                + "都拒绝拆仓库、状态不变；装配站通电后 OBJ-02 仍能完成（不会软锁）");
        }

        // ── F. 正式输入（InputRouter 动作 + 上下文 + Esc 栈）───────────────────────────────

        private static void CheckFormalInput()
        {
            CampaignState s = NewHome(4245);
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(false);
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            try
            {
                reader.Press(GameSettings.KeyBindings.GetChord(GameActionId.OpenBuildMenu).Key);
                mode.Tick(null, s, true);
                bool opened = mode.IsOpen && InputRouter.BuildMode && InputRouter.ActiveContext == InputContext.Build;
                Frame(reader);
                mode.Tick(null, s, true);
                bool escLayer = UiEscapeStack.Contains(mode);
                Expect(opened && escLayer, "按建造菜单键（默认 B）：进入建造模式，输入上下文 = 建造，压一层 Esc");

                // 选中发电机 → 指向合法空地 → R 旋转虚影 → 左键放置。
                mode.Select("generator_2");
                GridCell core = HomeGridService.CorePivot(s);
                GridCell spot = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10) ?? new GridCell(0, 30);
                mode.SetHover(s, spot);
                mode.RefreshPreview(s);
                bool previewOk = mode.Preview != null && mode.Preview.Ok;
                reader.Press(GameSettings.KeyBindings.GetChord(GameActionId.Rotate).Key);
                mode.Tick(null, s, true);
                Frame(reader);
                int ghostRot = mode.GhostRotation;
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                BuildingRecord placed = HomeGridService.BuildingAt(s, spot);
                Expect(placed != null && mode.HoverBuildingId == placed.BuildingId,
                    $"放下后鼠标没换格：悬停建筑立即更新为新建筑（{mode.HoverBuildingId}），此时按旋转键转的是它");
                Expect(previewOk && ghostRot == 90 && placed != null && Mathf.Approximately(placed.Rotation, 90f)
                       && mode.LastResult.Outcome == GridOpResult.Kind.Placed && mode.StatusText.Contains("已规划"),
                    $"虚影预览合法 → 按旋转键（默认 R）虚影转到 {ghostRot}° → 左键放置：格网上出现朝向 90° 的规划建筑，状态行“{mode.StatusText}”");

                // 同一格再左键：非法，给原因，不改状态。
                BuildingRecord[] recs = s.BuildingRecords;
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(!mode.LastResult.Success && mode.StatusIsError && mode.StatusText.Contains("已被“发电机”占用") && ReferenceEquals(recs, s.BuildingRecords),
                    $"在同一格再点：拒绝并显示原因“{mode.StatusText}”");

                // 右键：取消选择；没有选择时指着建筑按 R 旋转那座建筑。
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                bool cleared = mode.IsOpen && mode.SelectedTypeId == null;
                BuildingRecord repair = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":repair_bay");
                mode.SetHover(s, new GridCell(repair.GridX, repair.GridY));
                reader.Press(GameSettings.KeyBindings.GetChord(GameActionId.Rotate).Key);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(cleared && Mathf.Approximately(repair.Rotation, 90f) && mode.LastResult.Outcome == GridOpResult.Kind.Rotated,
                    "右键取消当前选择（仍在建造模式）；指着维修台按旋转键：维修台转到 90°");

                // 拆除模式键（默认 X）→ 左键点维修台（本局无法重建：禁拆，直接拒绝不弹框）→ 左键点刚放的虚影（取消规划）
                // → 右键退出拆除模式 → 再右键退出建造模式。
                reader.Press(GameSettings.KeyBindings.GetChord(GameActionId.DemolishMode).Key);
                mode.Tick(null, s, true);
                Frame(reader);
                bool demolishOn = mode.DemolishMode;
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool bayRefused = !UiConfirmDialog.IsOpen && mode.StatusIsError && mode.StatusText.Contains("无法重建")
                                  && !HomeGridService.IsMarkedForDemolish(s, repair.BuildingId) && HomeGridService.FindBuilding(s, repair.BuildingId) != null;
                string bayStatus = mode.StatusText;
                mode.SetHover(s, spot);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool ghostCancelled = mode.LastResult.Outcome == GridOpResult.Kind.PlanCancelled && HomeGridService.BuildingAt(s, spot) == null;
                bool marked = bayRefused && ghostCancelled;
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                bool demolishOff = mode.IsOpen && !mode.DemolishMode;
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(demolishOn && marked && demolishOff && !mode.IsOpen && !InputRouter.BuildMode,
                    $"拆除模式键进入拆除模式 → 左键点维修台（禁拆，不弹框，直接拒绝：“{bayStatus}”）→ 左键点刚放的虚影（取消规划）"
                    + " → 右键退出拆除模式 → 再右键退出建造模式（上下文回到战略）");

                // Esc 栈：打开后按 Esc（界面层 CloseTop）退出。
                mode.Open();
                mode.Tick(null, s, true);
                bool closedByEsc = UiEscapeStack.CloseTop() && !mode.IsOpen && !UiEscapeStack.Contains(mode);
                Expect(closedByEsc, "Esc 退出建造模式（经 UiEscapeStack，FG03 第 4 节）");

                // 战略暂停下也能规划；接入视角下自动退出。
                InputRouter.SetGameplayPaused(true, strategic: true);
                mode.Open();
                mode.Select("generator_2");
                GridCell spot2 = FindValid(s, "generator_2", new GridCell(core.X + 20, core.Y + 26), 10) ?? new GridCell(10, 30);
                GridOpResult pausedPlace = mode.ClickCell(s, spot2);
                InputRouter.SetGameplayPaused(false);
                mode.Tick(null, s, false);
                Expect(pausedPlace.Outcome == GridOpResult.Kind.Placed && !mode.IsOpen && !InputRouter.BuildMode,
                    "战略暂停中可以规划（放下虚影）；切到接入视角时建造模式自动退出");

                // 关闭状态下按旋转键：战略上下文里 R 是“移动命令”，建造模式不响应。
                reader.Press(KeyCode.R);
                BuildingRecord bay = HomeGridService.FindBuilding(s, repair.BuildingId);
                float rotBefore = bay.Rotation;
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(!mode.IsOpen && Mathf.Approximately(bay.Rotation, rotBefore), "建造模式关着时按 R 不旋转任何建筑（战略上下文 R = 移动命令）");
            }
            finally
            {
                mode.Shutdown();
                HomeValleyBuildMode.Unbind(mode);
                InputRouter.DebugSetReader(null);
                UiEscapeStack.Clear();
            }
        }

        // ── G. 施工计时：暂停与倍速 ─────────────────────────────────────────────────

        private static void CheckConstructionTiming()
        {
            var results = new List<string>();
            bool allSame = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewHome(5000);
                GridCell spot = FindValid(s, "generator_2", new GridCell(-8, 26), 10) ?? new GridCell(0, 30);
                GridOpResult r = HomeGridService.TryPlace(s, "generator_2", spot, 0);
                StrategyClock.Reset();
                StrategyClock.SetSpeed(speed);
                const float frame = 1f / 60f;
                // 暂停：真实时间在走，缩放后 dt = 0，施工不推进。
                for (int i = 0; i < 120; i++)
                {
                    HomeValleyWorkOrders.MarkAssignmentDirty();
                    HomeValleyWorkOrders.Tick(s, 0f, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
                }
                WorkOrderRecord order = s.WorkOrders.First(o => o.TargetId == r.BuildingId);
                bool pausedNoProgress = order.Progress <= 0f && HomeGridService.FindBuilding(s, r.BuildingId).ConstructionState == BuildingConstructionState.Planned;
                float game = 0f;
                int frames = 0;
                while (HomeGridService.FindBuilding(s, r.BuildingId).ConstructionState != BuildingConstructionState.Operational && frames < 60 * 200)
                {
                    float dt = StrategyClock.GetScaledDt(frame, false);
                    HomeValleyWorkOrders.MarkAssignmentDirty();
                    HomeValleyWorkOrders.Tick(s, dt, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
                    game += dt;
                    frames++;
                }
                float real = frames * frame;
                results.Add($"{speed}x：游戏 {game:F1} 秒 / 真实 {real:F1} 秒");
                allSame &= pausedNoProgress && Mathf.Abs(game - 40f) < 0.6f && Mathf.Abs(real * speed - game) < 0.6f;
            }
            StrategyClock.Reset();
            Expect(allSame, $"倍速矩阵：暂停时施工不推进；0.5x / 1x / 2x / 3x 下完工都需要约 40 秒游戏时间，真实时间按倍速缩放（{string.Join("；", results)}）");
        }

        // ── H. 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewHome(6100);
            GridCell spot = FindValid(s, "generator_2", new GridCell(-8, 26), 10) ?? new GridCell(0, 30);
            HomeGridService.TryPlace(s, "generator_2", spot, 270);
            GridCell spot2 = FindValid(s, "generator_2", new GridCell(12, 26), 10) ?? new GridCell(10, 30);
            HomeGridService.TryPlace(s, "generator_2", spot2, 0);
            HomeGridService.TryRotate(s, HomeValleyLayout.RegionId + ":warehouse");
            s.Grid.Explored = s.Grid.Explored.Append(new ExploredAreaRecord { CenterX = 60, CenterY = 0, Radius = 5 }).ToArray();
            HomeGridService.Invalidate();
            string expectBuildings = BuildingsJson(s);
            Dictionary<GridCell, string> expectOcc = HomeGridService.MapFor(s).SnapshotOccupancy();
            int serial = s.Grid.NextInstanceSerial;

            SaveResult saved = CampaignSaveService.Save(1, s, SaveReason.Manual);
            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(1);
            CampaignState l = loaded.State;
            Dictionary<GridCell, string> gotOcc = loaded.Success ? HomeGridService.MapFor(l).SnapshotOccupancy() : new Dictionary<GridCell, string>();
            bool occSame = gotOcc.Count == expectOcc.Count && expectOcc.All(kv => gotOcc.TryGetValue(kv.Key, out string v) && v == kv.Value);
            Expect(saved.Success && loaded.Success && BuildingsJson(l) == expectBuildings && occSame && l.Grid.NextInstanceSerial == serial
                   && l.Grid.LayoutVersion == HomeGridService.LayoutVersion && l.Grid.Explored.Length == 2 && l.Grid.TerrainSourceId == WorldTerrainSource.Id,
                $"真文件存读档：建筑枢轴格 / 朝向 / 中心 / 状态逐字段一致，占用层重建后 {gotOcc.Count} 格逐格一致，实例序号 {serial}、布局版本、探索区、地形来源往返");
            if (loaded.Success)
            {
                GridCell spot3 = FindValid(l, "generator_2", new GridCell(-20, 26), 10) ?? new GridCell(-20, 30);
                GridOpResult after = HomeGridService.TryPlace(l, "generator_2", spot3, 0);
                Expect(after.BuildingId == "home_valley:generator_2#" + serial, $"读档后继续放置：实例序号续编（{after.BuildingId}），不与已有建筑撞号");
                var portsSaved = new List<PortPlacement>();
                HomeGridService.PortsOf(HomeGridService.FindBuilding(l, HomeValleyLayout.RegionId + ":warehouse"), portsSaved);
                Expect(portsSaved.First(p => p.IsOutput).Dir == GridDir.S, "读档后仓库仍朝 90°，输出口朝南");
            }

            // 旧 v2 存档（FG0-ARCH-04 之前）：没有格网字段 → 读档时按位置迁移。
            CampaignState old = NewHome(6200);
            string json = JsonUtility.ToJson(old);
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(json);
            legacy.Grid.LayoutVersion = 0;
            legacy.Grid.Explored = Array.Empty<ExploredAreaRecord>();
            legacy.Grid.TerrainSourceId = string.Empty;
            foreach (BuildingRecord b in legacy.BuildingRecords)
            {
                b.GridX = 0;
                b.GridY = 0;
            }
            HomeGridService.Invalidate();
            HomeGridService.MapFor(legacy);
            Expect(BuildingsJson(legacy) == BuildingsJson(old) && legacy.Grid.LayoutVersion == HomeGridService.LayoutVersion
                   && legacy.Grid.Explored.Length == 1 && SnapshotMatches(legacy),
                "旧 v2 存档（建筑没有格网字段）：读档时按位置迁移出枢轴格与朝向，补开局探索区，结果与新开局逐字段一致");
        }

        // ── I. 后台一致性（FGR-BASE-021）───────────────────────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string RunOps(bool observed)
            {
                CampaignState s = NewHome(7100);
                GridCell a = FindValid(s, "generator_2", new GridCell(-8, 26), 10) ?? new GridCell(0, 30);
                GridCell b = FindValid(s, "generator_2", new GridCell(12, 26), 10) ?? new GridCell(10, 30);
                string warehouse = HomeValleyLayout.RegionId + ":warehouse";
                if (observed)
                {
                    var mode = new HomeValleyBuildMode();
                    mode.Open(); // 创建虚影、叠加层等表现对象
                    mode.Select("generator_2");
                    mode.RotateGhost();
                    mode.ClickCell(s, a);
                    mode.ClearSelection();
                    mode.Select("generator_2");
                    mode.RotateGhost();
                    mode.RotateGhost();
                    mode.RotateGhost(); // 90 + 270 = 360 → 0
                    mode.ClickCell(s, b);
                    mode.ClearSelection();
                    mode.SetHover(s, new GridCell(20, 0));
                    mode.RotateHovered(s);
                    mode.SetDemolishMode(true);
                    mode.ClickCell(s, new GridCell(16, -14)); // 维修台本局无法重建：禁拆，被拒（两边一致）
                    mode.ClickCell(s, a); // 取消 a 处的规划
                    mode.Shutdown();
                }
                else
                {
                    HomeGridService.TryPlace(s, "generator_2", a, 90);
                    HomeGridService.TryPlace(s, "generator_2", b, 0);
                    HomeGridService.TryRotate(s, warehouse);
                    HomeGridService.TryToggleDemolish(s, HomeValleyLayout.RegionId + ":repair_bay");
                    HomeGridService.TryToggleDemolish(s, HomeGridService.BuildingAt(s, a)?.BuildingId);
                }
                TickOrders(s, 60f);
                // 第二阶段：拆掉已建成的 b（普通发电机，可拆、不需确认），机器上门拆除。
                if (observed)
                {
                    var mode2 = new HomeValleyBuildMode();
                    mode2.Open();
                    mode2.SetDemolishMode(true);
                    mode2.ClickCell(s, b);
                    mode2.Shutdown();
                }
                else
                {
                    HomeGridService.TryToggleDemolish(s, HomeGridService.BuildingAt(s, b)?.BuildingId);
                }
                TickOrders(s, 30f);
                return BuildingsJson(s) + "\nscrap=" + s.Scrap + "\nserial=" + s.Grid.NextInstanceSerial
                       + "\norders=" + string.Join(",", s.WorkOrders.Select(o => o.Kind + ":" + o.TargetId + ":" + o.State));
            }

            string seen = RunOps(true);
            string unseen = RunOps(false);
            Expect(seen == unseen, $"同一组规划操作：经建造模式（画面上有虚影 / 叠加层）与直接调服务（无任何表现对象）结果逐字段一致，并跑 60 秒施工{(seen == unseen ? string.Empty : "\n观察：" + seen + "\n不观察：" + unseen)}");
        }

        // ── J. 叠加层与界面 ──────────────────────────────────────────────────────

        private static void CheckOverlayAndHud()
        {
            CampaignState s = NewHome(8100);
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            try
            {
                mode.Open();
                // FG0-ARCH-05：叠加层按区块分块、跟随镜头，贴图在工作线程画；这里同步补齐窗口再读像素（断言与 FG0-ARCH-04 相同）。
                mode.CompleteOverlayNow(s);
                GridCell? cliff = FindTerrain(s, "cliff", 36);
                bool ok = mode.TerrainOverlay != null && mode.TerrainOverlay.TileCount > 0 && mode.TerrainOverlay.PlaceholderCount == 0;
                string detail = "无贴图";
                if (ok && cliff != null)
                {
                    Color32 Pixel(GridCell c, int px, int py)
                    {
                        if (!mode.TryGetOverlayPixel(c, px, py, out Color32 col))
                        {
                            ok = false;
                        }
                        return col;
                    }
                    ColorUtility.TryParseHtmlString(GridContent.Terrains.First(t => t.Id == "cliff").Color, out Color cliffColor);
                    Color32 cc = cliffColor;
                    Color32 plain = Pixel(cliff.Value, 2, 2);
                    Color32 hatch = Pixel(cliff.Value, 3, 3);
                    Color32 fog = Pixel(new GridCell(36, 36), 3, 2);
                    Color32 reserve = Pixel(new GridCell(0, 4), 0, 0);
                    ok = ok && Close(plain, cc) && hatch.r < plain.r && fog.r < 60 && fog.g < 60 && reserve.r > 200 && reserve.g > 180;
                    detail = $"悬崖底色 {plain} 斜线 {hatch}；迷雾 {fog}；核心通道 {reserve}；贴图 {mode.TerrainOverlay.TileCount} 块";
                }
                Expect(ok && cliff != null, $"地形叠加层由格网数据画出：悬崖 = 表颜色 + 斜线图案（色盲安全），迷雾变暗，核心通道黄框（{detail}）");

                mode.Select("generator_2");
                mode.SetHover(s, new GridCell(0, 3));
                mode.RefreshPreview(s);
                bool bad = mode.Preview != null && !mode.Preview.Ok;
                GridCell good = FindValid(s, "generator_2", new GridCell(-8, 26), 10) ?? new GridCell(0, 30);
                mode.SetHover(s, good);
                mode.RefreshPreview(s);
                Expect(bad && mode.Preview.Ok && mode.Preview.CellOk.All(x => x) && mode.Preview.Cells.Count == 9,
                    "虚影预览：核心旁逐格判红（不合法），空地 9 格全绿；预览只在换格 / 换朝向 / 状态变化时重算");

                // 画面：不合法时叉形可见（颜色之外的形状），合法时隐藏；端口箭头数量与方向与端口表一致。
                mode.RefreshVisuals(s);
                bool crossHiddenWhenOk = !mode.GhostCrossVisible;
                mode.SetHover(s, new GridCell(0, 3));
                mode.RefreshPreview(s);
                mode.RefreshVisuals(s);
                bool crossShownWhenBad = mode.GhostCrossVisible;
                mode.ClearSelection();
                BuildingRecord whRec = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":warehouse");
                mode.SetHover(s, new GridCell(whRec.GridX, whRec.GridY));
                mode.RefreshPreview(s);
                mode.RefreshVisuals(s);
                var whPorts = new List<PortPlacement>();
                HomeGridService.PortsOf(whRec, whPorts);
                bool arrowsMatch = whPorts.Count == 2 && mode.ActiveArrowCount == whPorts.Count;
                for (int i = 0; arrowsMatch && i < whPorts.Count; i++)
                {
                    Vector2Int d = GridMath.DirVector(whPorts[i].Dir);
                    Vector3 expect = new Vector3(d.x, 0f, d.y) * (whPorts[i].IsOutput ? 1f : -1f);
                    arrowsMatch &= Vector3.Dot(mode.ArrowForward(i), expect) > 0.99f;
                }
                Expect(crossShownWhenBad && crossHiddenWhenOk && arrowsMatch,
                    $"虚影画面：不合法时叉形可见、合法时隐藏；指着仓库时端口箭头 {mode.ActiveArrowCount} 个，方向与端口表一致（输出朝外、输入朝里）");
                mode.Select("generator_2");

                // HUD：绑定真 UXML，刷新后列出可放置建筑、状态行给原因。
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
                VisualElement root = vta.CloneTree();
                var go = new GameObject("__fggrid_hud") { hideFlags = HideFlags.HideAndDontSave };
                BuildModeHudUIToolkit hud = go.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                InputRouter.SetScope(InputScope.Strategy);
                mode.SetHover(s, new GridCell(0, 3));
                mode.RefreshPreview(s);
                hud.Refresh();
                string status = hud.StatusLabelText;
                bool itemsOk = hud.ItemCount == 2 && hud.PanelVisible && !hud.EntryVisible;
                string beaconItem = root.Q<Button>("BuildItem1")?.text ?? string.Empty;
                string generatorItem = root.Q<Button>("BuildItem0")?.text ?? string.Empty;
                bool inScroll = root.Q<ScrollView>("BuildList") != null && root.Q<ScrollView>("BuildList").Q<Button>("BuildItem0") != null;
                Object.DestroyImmediate(go);
                Expect(generatorItem.Contains("60 废料") && generatorItem.Contains("40 秒") && generatorItem.Contains("已有") && inScroll,
                    $"建造栏条目写明成本、工期与已有数量：“{generatorItem.Replace("\n", " ")}”；条目在可滚动列表里（数量可变，UI Toolkit 红线 5）");
                Expect(itemsOk && status.Contains("不能放置") && status.Contains("通道") && beaconItem.Contains("尚未解锁"),
                    $"建造栏：列出 {hud.ItemCount} 种可放置建筑（信标未解锁时写明条件：“{beaconItem.Replace("\n", " ")}”），状态行“{status.Replace("\n", " ")}”");

                // 布局探针：中英文、UI 缩放极值、四种分辨率（面板默认隐藏，探针会移除隐藏类）。
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                    {
                        string result = UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml", "BuildPanel", stressFill: true,
                            prepare: r =>
                            {
                                var probeGo = new GameObject("__probe_build") { hideFlags = HideFlags.HideAndDontSave };
                                BuildModeHudUIToolkit h = probeGo.AddComponent<BuildModeHudUIToolkit>();
                                h.BindView(r.panel.visualTree);
                                h.Refresh();
                                Object.DestroyImmediate(probeGo);
                            }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 BuildModeHud.uxml#BuildPanel [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                mode.Shutdown();
                HomeValleyBuildMode.Unbind(mode);
            }
        }

        private static bool Close(Color32 a, Color32 b) => Math.Abs(a.r - b.r) <= 3 && Math.Abs(a.g - b.g) <= 3 && Math.Abs(a.b - b.b) <= 3;

        // ── K. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState small = NewHome(9100, withHauler: false);
            CampaignState large = NewHome(9101, withHauler: false);
            // 后期规模：800 座建筑（FG03 第 7 节）——在探索区外沿铺满 3×3 的建筑（测试直接写记录，不经放置校验）。
            var extra = new List<BuildingRecord>(800);
            for (int i = 0; i < 800; i++)
            {
                int x = 60 + (i % 40) * 4;
                int y = -80 + (i / 40) * 4;
                extra.Add(new BuildingRecord
                {
                    BuildingId = "home_valley:generator_2#" + (100 + i), BuildingTypeId = "generator_2", RegionId = HomeValleyLayout.RegionId,
                    GridX = x, GridY = y, Position = new Vector2(x, y), Rotation = 0f, ConstructionState = BuildingConstructionState.Operational,
                    Inventory = Array.Empty<CargoEntry>(), QueueIds = Array.Empty<string>(),
                });
            }
            large.BuildingRecords = large.BuildingRecords.Concat(extra).ToArray();
            HomeGridService.Invalidate();
            var sw = Stopwatch.StartNew();
            HomeGridService.MapFor(large);
            double rebuildMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            HomeGridService.Invalidate();
            HomeGridService.MapFor(large);
            double rebuildMs2 = sw.Elapsed.TotalMilliseconds;

            double Measure(CampaignState s)
            {
                GridCell c = new GridCell(-8, 26);
                var buf = new GridPlacementResult();
                HomeGridService.ValidatePlacement(s, "generator_2", c, 0, into: buf);
                var w = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                {
                    HomeGridService.ValidatePlacement(s, "generator_2", new GridCell(c.X + (i % 7), c.Y + (i % 5)), (i % 4) * 90, into: buf);
                }
                return w.Elapsed.TotalMilliseconds * 1000.0 / 2000.0;
            }

            HomeGridService.Invalidate();
            double smallUs = Measure(small);
            double smallUs2 = Measure(small);
            HomeGridService.Invalidate();
            double largeUs = Measure(large);
            double largeUs2 = Measure(large);
            double s1 = Math.Min(smallUs, smallUs2);
            double l1 = Math.Min(largeUs, largeUs2);

            var proto = new GridTerrainPrototype(5, new GridCell(0, 0));
            var map = new HomeGridMap(32, proto);
            sw.Restart();
            for (int i = 0; i < 64; i++)
            {
                map.GetTerrain(new GridCell(1000 + i * 32, 2000));
            }
            double chunkMs = sw.Elapsed.TotalMilliseconds / 64.0;

            Line($"  · 性能（Editor batchmode，影子工程，本机 CPU；真机数字由 FG15-SYS-02 补）：7 座 vs 807 座时单次放置校验 {s1:F1} µs vs {l1:F1} µs；"
                 + $"807 座占用重建 {Math.Min(rebuildMs, rebuildMs2):F2} ms；原型地形单区块（32×32）生成 {chunkMs:F3} ms");
            Expect(l1 < Math.Max(s1 * 3.0, s1 + 20.0), $"放置校验与建筑数无关：807 座时 {l1:F1} µs，7 座时 {s1:F1} µs（O(占地)，B18）");
            Expect(Math.Min(rebuildMs, rebuildMs2) < 50.0, $"占用层整体重建 807 座 {Math.Min(rebuildMs, rebuildMs2):F2} ms（只在建筑记录变化时发生，不在每帧）");
            int rebuilds = HomeGridService.OccupancyRebuildCount;
            for (int i = 0; i < 100; i++)
            {
                HomeGridService.BuildingAt(large, new GridCell(i, i));
            }
            Expect(HomeGridService.OccupancyRebuildCount == rebuilds, "100 次查询不触发占用重建（记录没变 → 缓存命中，O(1)）");
            Expect(chunkMs < 5.0, $"原型地形单区块生成 {chunkMs:F3} ms（FG14 第 4 节区块生成 ≤ 5 ms 的参照；正式生成器在 FG0-ARCH-05 走工作线程）");
        }

        // ── 测试表构造 ────────────────────────────────────────────────────────────

        private sealed class GridRow
        {
            public string TypeId;
            public int W;
            public int H;
            public int Placeable;
            public int MaxCount;
            public string UnlockRule;
            public int Critical;
            public string RequiredTerrain;
            public string DescKey;
            public string UnlockHintKey;
        }

        private static GridRow WithTerrain(GridRow r, string terrain)
        {
            r.RequiredTerrain = terrain;
            return r;
        }

        private static ByteBuf GridBuf(IReadOnlyList<BuildingGrid> rows, Func<GridRow, GridRow> edit)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach (BuildingGrid g in rows)
            {
                GridRow r = edit(new GridRow
                {
                    TypeId = g.TypeId, W = g.FootprintW, H = g.FootprintH, Placeable = g.Placeable, MaxCount = g.MaxCount, UnlockRule = g.UnlockRule,
                    Critical = g.Critical, RequiredTerrain = g.RequiredTerrain, DescKey = g.DescKey, UnlockHintKey = g.UnlockHintKey,
                });
                buf.WriteString(r.TypeId);
                buf.WriteInt(r.W);
                buf.WriteInt(r.H);
                buf.WriteInt(r.Placeable);
                buf.WriteInt(r.MaxCount);
                buf.WriteString(r.UnlockRule);
                buf.WriteInt(r.Critical);
                buf.WriteString(r.RequiredTerrain);
                buf.WriteString(r.DescKey);
                buf.WriteString(r.UnlockHintKey);
            }
            return buf;
        }

        private static ByteBuf TuningBuf(Func<string, float> real, params (string id, float value)[] overrides)
        {
            var ids = new List<string>();
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length >= 3 && f[0] == "H")
                {
                    ids.Add(f[1]);
                }
            }
            var buf = new ByteBuf();
            buf.WriteSize(ids.Count);
            foreach (string id in ids)
            {
                float v = real(id);
                foreach ((string oid, float ov) in overrides)
                {
                    if (oid == id)
                    {
                        v = ov;
                    }
                }
                buf.WriteString(id);
                buf.WriteFloat(v);
                buf.WriteString("selfcheck");
            }
            return buf;
        }

        // ── 输入替身 ────────────────────────────────────────────────────────────

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public readonly HashSet<int> MouseDown = new HashSet<int>();

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                MouseDown.Clear();
            }

            public bool GetKey(KeyCode key) => _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => MouseDown.Contains(button);
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
        }

        // ── 小工具 ───────────────────────────────────────────────────────────────

        /// <summary>仓库根（影子工程里当前目录是 .unity-validate-clone，向上找 tools/cell_tables）。</summary>
        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static bool Eq(float value, string text) => Mathf.Approximately(value, float.Parse(text, CultureInfo.InvariantCulture));

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
            }
        }

        private static string Tail(string output) =>
            string.Join(" / ", output.Replace("\r", string.Empty).Split('\n').Where(l => l.Length > 0).Reverse().Take(2).Reverse());

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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
